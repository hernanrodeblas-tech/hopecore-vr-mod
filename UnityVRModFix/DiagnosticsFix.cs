using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityVRMod.Features.VRVisualization.OpenVR;

namespace UnityVRModFix;

// Crash forensics, not a gameplay fix. A native crash (graphics driver, OpenVR compositor...) leaves
// no managed exception, and BepInEx's own log file is only flushed every ~2s, so the last lines before
// a crash are usually lost and the log seems to stop earlier than it really did. This writes a
// separate file (BepInEx\UnityVRModFix_trace.log) that is flushed on every line, containing:
//   - system/GPU info and whether Direct3D 11 is really in use,
//   - a copy of every BepInEx log line (including UnityVRMod's own),
//   - Unity errors/exceptions (deduplicated) and a bounded number of normal Unity log lines,
//   - scene load/unload events,
//   - a heartbeat every 5s plus a watchdog thread that reports if the main thread stops running
//     (tells a hang apart from an instant crash),
//   - fine-grained begin/end markers around the VR rig setup/teardown and, for the first few frames
//     after every rig setup, around WaitGetPoses / Camera.Render / Submit, so the LAST line in the
//     file says which step the process died in.
// The previous run's file is kept as UnityVRModFix_trace.prev.log.
internal static class DiagnosticsFix
{
    private const int TracedFramesAfterSetup = 6;
    private const int MaxNormalUnityLogLines = 400;
    private const int MaxWarningLines = 100;

    private static readonly object _lock = new();
    private static StreamWriter _writer;
    private static readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private static long _lastTickMs;
    private static int _frame;
    private static volatile string _phase = "startup";
    private static volatile string _scene = "";
    private static volatile bool _inTrace;
    private static int _traceFramesLeft;
    private static int _normalUnityLogLines;
    private static int _warningLines;
    private static readonly Dictionary<string, int> _unityLogCounts = new();
    private static bool _started;
    private static volatile bool _vrInitialized;
    private static float _nextVrPoll;
    private static string _lastHmdState = "";
    private static int _openVrEventsLogged;

    private const uint ThreadAccess = 0x4A; // SUSPEND_RESUME | GET_CONTEXT | QUERY_INFORMATION
    private const int ContextSize = 1232;   // sizeof(CONTEXT) on x64
    private static uint _mainThreadId;
    private static IntPtr _contextRaw;
    private static IntPtr _context;
    private static byte[] _stackBuffer;

    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern IntPtr OpenThread(uint access, bool inherit, uint threadId);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern uint SuspendThread(IntPtr thread);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern uint ResumeThread(IntPtr thread);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern bool GetThreadContext(IntPtr thread, IntPtr context);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern bool ReadProcessMemory(IntPtr process, IntPtr baseAddress, byte[] buffer, UIntPtr size, out UIntPtr bytesRead);

    internal static void Init()
    {
        if (_started)
        {
            return;
        }
        _started = true;

        try
        {
            var root = BepInEx.Paths.BepInExRootPath;
            var path = Path.Combine(root, "UnityVRModFix_trace.log");
            var prev = Path.Combine(root, "UnityVRModFix_trace.prev.log");
            if (File.Exists(path))
            {
                try { File.Copy(path, prev, true); } catch { }
            }
            var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        }
        catch
        {
            return;
        }

        _scene = SceneManager.GetActiveScene().name;

        Trace($"=== UnityVRModFix trace started {DateTime.Now:O} ===");
        Trace($"Unity {Application.unityVersion} | {SystemInfo.operatingSystem}");
        Trace($"GPU: {SystemInfo.graphicsDeviceName} | vendor {SystemInfo.graphicsDeviceVendor} | {SystemInfo.graphicsMemorySize} MB VRAM");
        Trace($"Graphics API: {SystemInfo.graphicsDeviceType} | {SystemInfo.graphicsDeviceVersion}");
        Trace($"CPU: {SystemInfo.processorType} x{SystemInfo.processorCount} | RAM {SystemInfo.systemMemorySize} MB");
        Trace($"Command line: {Environment.CommandLine}");
        Trace($"Rendering threading mode: {SystemInfo.renderingThreadingMode}");
        if (SystemInfo.renderingThreadingMode != UnityEngine.Rendering.RenderingThreadingMode.Direct)
        {
            Trace("NOTE: Unity renders on a separate thread. If the game freezes inside WaitGetPoses, add -force-gfx-direct to the Steam launch options.");
        }
        if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Direct3D11)
        {
            Trace("WARNING: not running on Direct3D 11. UnityVRMod only supports D3D11; add -force-d3d11 to the Steam launch options.");
        }

        BepInEx.Logging.Logger.Listeners.Add(new TraceListener());
        Application.logMessageReceived += OnUnityLog;
        Application.quitting += () => Trace("Application.quitting (graceful exit)");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Trace($"UNHANDLED EXCEPTION (terminating={e.IsTerminating}): {e.ExceptionObject}");
        SceneManager.sceneLoaded += (s, mode) => { _scene = SceneManager.GetActiveScene().name; Trace($"sceneLoaded '{s.name}' mode={mode}"); };
        SceneManager.sceneUnloaded += s => Trace($"sceneUnloaded '{s.name}'");
        SceneManager.activeSceneChanged += (a, b) => { _scene = b.name; Trace($"activeSceneChanged '{a.name}' -> '{b.name}'"); };

        // Awake runs on Unity's main thread; remember its native id so the watchdog can inspect it later.
        _mainThreadId = GetCurrentThreadId();
        PrepareStackSnapshot();

        var watchdog = new Thread(WatchdogLoop) { IsBackground = true, Name = "UnityVRModFix-Watchdog" };
        watchdog.Start();
    }

    // Allocates the buffers used by SnapshotMainThreadStack and runs the exact same Win32 calls once
    // against a harmless helper thread. While the real main thread is suspended we must not trigger
    // anything that could block on a lock it holds (first-call P/Invoke stub generation, allocations),
    // so everything is warmed up here, at startup, when nothing else is running yet.
    private static void PrepareStackSnapshot()
    {
        try
        {
            _stackBuffer = new byte[65536];
            _contextRaw = System.Runtime.InteropServices.Marshal.AllocHGlobal(ContextSize + 16);
            _context = (IntPtr)(((long)_contextRaw + 15) & ~15L);

            uint helperId = 0;
            var ready = new ManualResetEventSlim(false);
            var release = new ManualResetEventSlim(false);
            var helper = new Thread(() => { helperId = GetCurrentThreadId(); ready.Set(); release.Wait(); }) { IsBackground = true };
            helper.Start();
            ready.Wait(2000);

            var h = OpenThread(ThreadAccess, false, helperId);
            if (h != IntPtr.Zero)
            {
                SuspendThread(h);
                GetThreadContext(h, _context);
                ResumeThread(h);
                CloseHandle(h);
            }
            ReadProcessMemory(GetCurrentProcess(), _context, _stackBuffer, (UIntPtr)64, out _);
            release.Set();
        }
        catch (Exception ex)
        {
            Trace($"Stack snapshot setup failed (feature disabled): {ex.Message}");
            _mainThreadId = 0;
        }
    }

    // Called by the watchdog when the main thread has stopped running Update. Briefly suspends it, reads
    // its instruction pointer and a slice of its native stack, resumes it, and lists which modules those
    // addresses fall into (nearest to the top of the stack first). It is not a real unwinder, but the
    // module names (vrclient_x64.dll, fmod*.dll, nvwgf2umx.dll...) show who the main thread is stuck in.
    private static void SnapshotMainThreadStack(string reason)
    {
        if (_mainThreadId == 0 || _stackBuffer == null)
        {
            return;
        }
        try
        {
            // Everything that allocates or takes locks happens BEFORE the suspend.
            var modules = new List<(long Start, long End, string Name)>();
            foreach (System.Diagnostics.ProcessModule m in System.Diagnostics.Process.GetCurrentProcess().Modules)
            {
                var start = (long)m.BaseAddress;
                modules.Add((start, start + m.ModuleMemorySize, m.ModuleName));
            }

            for (var i = 0; i < ContextSize; i += 8)
            {
                System.Runtime.InteropServices.Marshal.WriteInt64(_context, i, 0);
            }
            System.Runtime.InteropServices.Marshal.WriteInt32(_context, 0x30, 0x00100001); // CONTEXT_CONTROL

            var thread = OpenThread(ThreadAccess, false, _mainThreadId);
            if (thread == IntPtr.Zero)
            {
                Trace($"STALL SNAPSHOT ({reason}): could not open the main thread");
                return;
            }

            long rip = 0, rsp = 0;
            var bytesRead = 0;
            var haveContext = false;
            if (SuspendThread(thread) != uint.MaxValue)
            {
                try
                {
                    if (GetThreadContext(thread, _context))
                    {
                        rsp = System.Runtime.InteropServices.Marshal.ReadInt64(_context, 0x98);
                        rip = System.Runtime.InteropServices.Marshal.ReadInt64(_context, 0xF8);
                        haveContext = true;
                        foreach (var size in new[] { 65536, 16384, 4096 })
                        {
                            if (ReadProcessMemory(GetCurrentProcess(), (IntPtr)rsp, _stackBuffer, (UIntPtr)size, out var read))
                            {
                                bytesRead = (int)read;
                                break;
                            }
                        }
                    }
                }
                finally
                {
                    ResumeThread(thread);
                }
            }
            CloseHandle(thread);

            if (!haveContext)
            {
                Trace($"STALL SNAPSHOT ({reason}): could not read the main thread context");
                return;
            }

            string Describe(long address)
            {
                foreach (var m in modules)
                {
                    if (address >= m.Start && address < m.End)
                    {
                        return $"{m.Name}+0x{address - m.Start:X}";
                    }
                }
                return null;
            }

            var sb = new StringBuilder();
            var seen = new HashSet<string>();
            for (var i = 0; i + 8 <= bytesRead && seen.Count < 80; i += 8)
            {
                var d = Describe(BitConverter.ToInt64(_stackBuffer, i));
                if (d != null && seen.Add(d))
                {
                    sb.Append(d).Append("  ");
                }
            }
            Trace($"STALL SNAPSHOT ({reason}): main thread RIP={Describe(rip) ?? $"0x{rip:X}"} | module addresses on its stack, top first: {sb}");
        }
        catch (Exception ex)
        {
            Trace($"STALL SNAPSHOT ({reason}) failed: {ex.Message}");
        }
    }

    // Shift+F12: blocks the main thread for 8 seconds, to check that the watchdog and the stack snapshot
    // work (it is the only way to test them without waiting for a real hang).
    internal static void SimulateFreeze()
    {
        Trace("SimulateFreeze: blocking the main thread for 8s on purpose");
        Thread.Sleep(8000);
        Trace("SimulateFreeze: main thread running again");
    }

    internal static void Tick()
    {
        _frame = Time.frameCount;
        Interlocked.Exchange(ref _lastTickMs, _clock.ElapsedMilliseconds);
        PollOpenVr();
    }

    // Twice a second while VR is up: logs every change of the headset's state (pose validity, tracking
    // result, connection, activity level - e.g. UserInteraction vs Standby/Timeout when the headset
    // is taken off or goes idle) plus the runtime's events (quit, dashboard, device lost...). When the
    // headset view freezes while the game keeps running, UnityVRMod only says "HMD pose NOT valid";
    // this records WHY, with a timestamp, in the same file as everything else.
    private static void PollOpenVr()
    {
        if (!_vrInitialized || Time.unscaledTime < _nextVrPoll)
        {
            return;
        }
        _nextVrPoll = Time.unscaledTime + 0.5f;
        try
        {
            var system = OpenVR.System;
            if (system == null)
            {
                return;
            }

            var poses = new TrackedDevicePose_t[1];
            system.GetDeviceToAbsoluteTrackingPose(ETrackingUniverseOrigin.TrackingUniverseSeated, 0f, poses);
            var activity = system.GetTrackedDeviceActivityLevel(0);
            var state = $"poseValid={poses[0].bPoseIsValid} tracking={poses[0].eTrackingResult} deviceConnected={poses[0].bDeviceIsConnected} activity={activity}";
            if (state != _lastHmdState)
            {
                _lastHmdState = state;
                Trace($"OpenVR HMD state: {state}");
            }

            var vrEvent = new VREvent_t();
            var size = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(VREvent_t));
            for (var i = 0; i < 32 && system.PollNextEvent(ref vrEvent, size); i++)
            {
                if (_openVrEventsLogged++ < 300)
                {
                    Trace($"OpenVR event: {(EVREventType)vrEvent.eventType} device={vrEvent.trackedDeviceIndex}");
                }
            }
        }
        catch (Exception ex)
        {
            _vrInitialized = false;
            Trace($"OpenVR state polling disabled: {ex.Message}");
        }
    }

    internal static void Trace(string message)
    {
        var w = _writer;
        if (w == null)
        {
            return;
        }
        var line = $"[{_clock.ElapsedMilliseconds / 1000.0:F3}s f={_frame} scene={_scene} phase={_phase}] {message}";
        lock (_lock)
        {
            try { w.WriteLine(line); } catch { }
        }
    }

    private sealed class TraceListener : ILogListener
    {
        public LogLevel LogLevelFilter => LogLevel.All;

        public void LogEvent(object sender, LogEventArgs eventArgs)
        {
            Trace($"[{eventArgs.Level}:{eventArgs.Source.SourceName}] {eventArgs.Data}");
        }

        public void Dispose()
        {
            Trace("BepInEx logger disposed");
        }
    }

    private static void OnUnityLog(string condition, string stackTrace, LogType type)
    {
        if (type == LogType.Log)
        {
            if (_normalUnityLogLines++ < MaxNormalUnityLogLines)
            {
                Trace($"UNITY Log: {condition}");
            }
            return;
        }
        if (type == LogType.Warning)
        {
            if (_warningLines++ < MaxWarningLines)
            {
                Trace($"UNITY Warning: {condition}");
            }
            return;
        }

        var key = type + condition;
        _unityLogCounts.TryGetValue(key, out var n);
        n++;
        if (_unityLogCounts.Count < 500)
        {
            _unityLogCounts[key] = n;
        }
        if (n == 1)
        {
            Trace($"UNITY {type}: {condition}\n{FirstLines(stackTrace, 6)}");
        }
        else if (n == 10 || n == 100 || n == 1000 || n == 10000)
        {
            Trace($"UNITY {type} (repeated {n}x): {condition}");
        }
    }

    private static string FirstLines(string text, int count)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }
        var lines = text.Split('\n');
        return string.Join("\n", lines, 0, Math.Min(count, lines.Length));
    }

    private static void WatchdogLoop()
    {
        long lastStallReport = -100000;
        long lastHeartbeat = 0;
        var snapshotEarly = false;
        var snapshotLate = false;
        while (true)
        {
            Thread.Sleep(500);
            var now = _clock.ElapsedMilliseconds;
            var last = Interlocked.Read(ref _lastTickMs);
            if (last == 0)
            {
                continue;
            }
            var stalled = now - last;
            if (stalled > 2000)
            {
                if (now - lastStallReport > 5000)
                {
                    lastStallReport = now;
                    Trace($"WATCHDOG: main thread has not run Update for {stalled / 1000.0:F1}s (process still alive)");
                }
                if (!snapshotEarly && stalled > 4000)
                {
                    snapshotEarly = true;
                    SnapshotMainThreadStack("stalled 4s");
                }
                if (!snapshotLate && stalled > 20000)
                {
                    snapshotLate = true;
                    SnapshotMainThreadStack("stalled 20s");
                }
            }
            else
            {
                snapshotEarly = false;
                snapshotLate = false;
                if (now - lastHeartbeat > 5000)
                {
                    lastHeartbeat = now;
                    Trace("heartbeat: alive");
                }
            }
        }
    }

    internal static void Apply(Harmony harmony)
    {
        try
        {
            var setupType = Type.GetType("UnityVRMod.Features.VrVisualization.VrCameraSetup_CoreOpenVR, UnityVRMod");
            const BindingFlags pub = BindingFlags.Public | BindingFlags.Instance;
            const BindingFlags priv = BindingFlags.NonPublic | BindingFlags.Instance;

            PatchAll(harmony, setupType, "InitializeVr", pub, nameof(InitVrPre), nameof(InitVrPost));
            PatchAll(harmony, setupType, "SetupCameraRig", pub, nameof(SetupPre), nameof(SetupPost));
            PatchAll(harmony, setupType, "TeardownCameraRig", pub, nameof(AlwaysPre), nameof(AlwaysPost));
            PatchAll(harmony, setupType, "TeardownVr", pub, nameof(AlwaysPre), nameof(AlwaysPost));
            PatchAll(harmony, setupType, "UpdatePoses", pub, nameof(UpdatePosesPre), nameof(UpdatePosesPost));
            PatchAll(harmony, setupType, "RenderEye", priv, nameof(RenderEyePre), nameof(RenderEyePost));

            var compositorType = Type.GetType("UnityVRMod.Features.VRVisualization.OpenVR.CVRCompositor, UnityVRMod");
            PatchAll(harmony, compositorType, "WaitGetPoses", pub, nameof(WaitGetPosesPre), nameof(WaitGetPosesPost));
            PatchAll(harmony, compositorType, "Submit", pub, nameof(SubmitPre), nameof(SubmitPost));

            PatchAll(harmony, typeof(Camera), "Render", pub, nameof(CameraRenderPre), nameof(CameraRenderPost), Type.EmptyTypes);

            VRModFixLog.Info("[DiagnosticsFix] Crash trace active: BepInEx\\UnityVRModFix_trace.log");
        }
        catch (Exception ex)
        {
            VRModFixLog.Info($"[DiagnosticsFix] Could not install VR step tracing: {ex}");
        }
    }

    private static void PatchAll(Harmony harmony, Type type, string method, BindingFlags flags, string pre, string post, Type[] parameterTypes = null)
    {
        if (type == null)
        {
            VRModFixLog.Info($"[DiagnosticsFix] Type for '{method}' not found; step tracing for it skipped.");
            return;
        }
        var prefix = new HarmonyMethod(typeof(DiagnosticsFix).GetMethod(pre, BindingFlags.NonPublic | BindingFlags.Static));
        var postfix = new HarmonyMethod(typeof(DiagnosticsFix).GetMethod(post, BindingFlags.NonPublic | BindingFlags.Static));
        var patched = 0;
        foreach (var m in type.GetMethods(flags))
        {
            if (m.Name != method)
            {
                continue;
            }
            if (parameterTypes != null && m.GetParameters().Length != parameterTypes.Length)
            {
                continue;
            }
            harmony.Patch(m, prefix: prefix, postfix: postfix);
            patched++;
        }
        if (patched == 0)
        {
            VRModFixLog.Info($"[DiagnosticsFix] {type.Name}.{method} not found; step tracing for it skipped.");
        }
    }

    private static void AlwaysPre(MethodBase __originalMethod)
    {
        if (__originalMethod.Name == "TeardownVr")
        {
            _vrInitialized = false;
        }
        _phase = __originalMethod.Name;
        Trace($"{__originalMethod.Name} BEGIN");
    }

    private static void AlwaysPost(MethodBase __originalMethod)
    {
        Trace($"{__originalMethod.Name} END");
        _phase = "frame";
    }

    private static void InitVrPre()
    {
        _phase = "InitializeVr";
        Trace("InitializeVr BEGIN");
    }

    private static void InitVrPost(bool __result)
    {
        Trace($"InitializeVr END result={__result}");
        _vrInitialized = __result;
        _phase = "frame";
    }

    private static void SetupPre(Camera mainCamera)
    {
        _phase = "SetupCameraRig";
        Trace($"SetupCameraRig BEGIN camera='{(mainCamera != null ? mainCamera.name : "null")}' cameraScene='{(mainCamera != null ? mainCamera.gameObject.scene.name : "")}'");
    }

    private static void SetupPost()
    {
        Trace($"SetupCameraRig END (tracing the next {TracedFramesAfterSetup} frames in detail)");
        _traceFramesLeft = TracedFramesAfterSetup;
        _phase = "frame";
    }

    private static void UpdatePosesPre()
    {
        _phase = "UpdatePoses";
        if (_traceFramesLeft > 0)
        {
            _inTrace = true;
            Trace($"UpdatePoses BEGIN (traced frames left: {_traceFramesLeft})");
        }
    }

    private static void UpdatePosesPost()
    {
        if (_inTrace)
        {
            Trace("UpdatePoses END");
            _inTrace = false;
            _traceFramesLeft--;
        }
        _phase = "frame";
    }

    private static void WaitGetPosesPre()
    {
        _phase = "WaitGetPoses";
        if (_inTrace)
        {
            Trace("WaitGetPoses BEGIN");
        }
    }

    private static void WaitGetPosesPost()
    {
        if (_inTrace)
        {
            Trace("WaitGetPoses END");
        }
        _phase = "UpdatePoses";
    }

    private static void RenderEyePre(object eye)
    {
        _phase = "RenderEye";
        if (_inTrace)
        {
            Trace($"RenderEye BEGIN eye={eye}");
        }
    }

    private static void RenderEyePost(object eye)
    {
        if (_inTrace)
        {
            Trace($"RenderEye END eye={eye}");
        }
        _phase = "UpdatePoses";
    }

    private static void SubmitPre()
    {
        _phase = "Submit";
        if (_inTrace)
        {
            Trace("Submit BEGIN");
        }
    }

    private static void SubmitPost()
    {
        if (_inTrace)
        {
            Trace("Submit END");
        }
        _phase = "RenderEye";
    }

    private static void CameraRenderPre(Camera __instance)
    {
        if (!_inTrace || __instance == null)
        {
            return;
        }
        var n = __instance.name;
        if (n != "OpenVR_VRCamera_Left" && n != "OpenVR_VRCamera_Right")
        {
            return;
        }
        _phase = "Camera.Render";
        Trace($"Camera.Render BEGIN {n}");
    }

    private static void CameraRenderPost(Camera __instance)
    {
        if (!_inTrace || __instance == null)
        {
            return;
        }
        var n = __instance.name;
        if (n != "OpenVR_VRCamera_Left" && n != "OpenVR_VRCamera_Right")
        {
            return;
        }
        Trace($"Camera.Render END {n}");
        _phase = "RenderEye";
    }
}
