using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Unity.Mono;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UniverseLib.Input;
using TMPro;

namespace UnityVRModFix;

// UnityVRMod v0.1.0-beta ships a leftover debug feature (TemporaryLiveReloadTester) that
// permanently hijacks PageUp/PageDown/+/-/Enter to cycle and toggle the mod's own config values.
// If the game being modded uses any of those same keys for gameplay (e.g. a numeric keypad
// control panel), playing normally randomly mutates the VR mod's config at runtime, which can
// silently disable VR injection or break the camera rig. This patch disables that feature.
[BepInPlugin("com.local.unityvrmodfix", "UnityVRMod Debug-Hotkey Fix", "1.0.0")]
[BepInDependency("com.newunitymodder.unityvrmod")]
public class Plugin : BaseUnityPlugin
{
    private void Awake()
    {
        var harmony = new Harmony("com.local.unityvrmodfix");

        var testerType = System.Type.GetType(
            "UnityVRMod.Features.Debug.TemporaryLiveReloadTester, UnityVRMod");

        var initMethod = testerType.GetMethod(
            "Initialize", BindingFlags.Public | BindingFlags.Static);
        var updateMethod = testerType.GetMethod(
            "Update", BindingFlags.Public | BindingFlags.Static);

        var skipPrefix = new HarmonyMethod(typeof(Plugin).GetMethod(
            nameof(SkipOriginal), BindingFlags.NonPublic | BindingFlags.Static));

        harmony.Patch(initMethod, prefix: skipPrefix);
        harmony.Patch(updateMethod, prefix: skipPrefix);

        Logger.LogInfo("Disabled UnityVRMod's TemporaryLiveReloadTester debug hotkeys.");

        VRModFixLog.LogInfo = Logger.LogInfo;

        CameraFollowFix.Apply(harmony);
        HeightFix.Apply(harmony);
        PointerFix.Apply(harmony);
        CameraCleanerFix.Apply(harmony);
        PlayerCapsuleFix.Apply(harmony);
        BackwardMovementFix.Apply(harmony);
        RigidbodyInterpolationFix.Apply(harmony);
        CinemachineUpdateModeFix.Apply(harmony);
        // Tried 3 variants (double-submit to compositor, Depth clear flags, manual GL.Clear +
        // Nothing clear flags) of "re-render the eye camera a second time for just the UI layer"
        // - all three either froze the VR view on scene change or turned the whole view solid
        // yellow. The common factor across all of them is the second manual Camera.Render() call
        // itself on this specific (disabled, asymmetric-projection, about-to-be-Submitted) eye
        // camera within the same frame - seems fundamentally incompatible with this pipeline
        // rather than a clear-flags/culling detail. Left disabled; text-behind-geometry remains
        // an open, unresolved limitation.
        // RenderEyeOverlayFix.Apply(harmony);
        // CommandBuffers attached via AddCommandBuffer never actually execute on these eye
        // cameras either - they're disabled and rendered manually via Camera.Render(), outside
        // Unity's normal per-frame camera pipeline that CommandBuffer/CameraEvent hooks rely on.
        // Confirmed with a debug magenta clear that never showed up. Fourth dead end - leaving
        // text-behind-geometry unresolved.
        // TransparentDepthClearFix.Apply(harmony);

        // GameManager's "in dialogue" flag can get stuck true (a pre-existing game bug, not
        // us) and never clears on its own. You can't still be "in a dialogue" that belongs to a
        // scene you just left, so a scene change is a safe, automatic point to reset it -
        // instead of requiring a manual F5 every time.
        SceneManager.activeSceneChanged += (_, _) =>
        {
            var gm = GameManager.instance;
            if (gm != null && gm.GetIsInDialogue())
            {
                gm.IsInDialogue(false);
                Logger.LogInfo("[Plugin] Scene changed while GetIsInDialogue()==true; auto-reset to false.");
            }
        };
    }

    private void Update()
    {
        if (InputManager.GetKeyDown(KeyCode.F9))
        {
            DumpCameras();
        }

        if (InputManager.GetKeyDown(KeyCode.F8))
        {
            DumpCanvases();
        }

        if (InputManager.GetKeyDown(KeyCode.F6))
        {
            DumpPlayerState();
        }

        if (InputManager.GetKeyDown(KeyCode.F5))
        {
            var gm = GameManager.instance;
            if (gm != null)
            {
                gm.IsInDialogue(false);
                Logger.LogInfo("[Plugin] Forced GameManager.IsInDialogue(false) to unstick the dialogue flag.");
            }
        }

        if (InputManager.GetKeyDown(KeyCode.F4))
        {
            DumpNearbyRenderers();
        }

        if (InputManager.GetKeyDown(KeyCode.F3))
        {
            DumpVideoPlayers();
        }

        CanvasFix.Tick();
        ActionEnableFix.Tick();
        VideoFix.Tick();
    }

    private void LateUpdate()
    {
        CanvasFix.LateTick();
    }

    private void DumpVideoPlayers()
    {
        var sb = new StringBuilder();
        var players = Object.FindObjectsByType<UnityEngine.Video.VideoPlayer>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        sb.AppendLine($"[VideoDump] Found {players.Length} VideoPlayer(s):");
        foreach (var vp in players)
        {
            sb.AppendLine(
                $"  - '{GetPath(vp.gameObject)}' isPlaying={vp.isPlaying} enabled={vp.enabled} " +
                $"renderMode={vp.renderMode} targetTexture={(vp.targetTexture != null ? vp.targetTexture.name : "null")} " +
                $"targetCamera={(vp.targetCamera != null ? vp.targetCamera.name : "null")}");
        }

        foreach (var renderer in Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            var mat = renderer.sharedMaterial;
            if (mat == null || mat.mainTexture == null)
            {
                continue;
            }
            foreach (var vp in players)
            {
                if (vp.targetTexture != null && mat.mainTexture == vp.targetTexture)
                {
                    sb.AppendLine($"  -> Renderer '{GetPath(renderer.gameObject)}' displays VideoPlayer '{GetPath(vp.gameObject)}' via material '{mat.name}'");
                }
            }
        }

        Logger.LogInfo(sb.ToString());
    }

    private void DumpNearbyRenderers()
    {
        var vrRig = VRModInternals.GetVrRigTransform();
        var origin = vrRig != null ? vrRig.position : (Camera.main != null ? Camera.main.transform.position : Vector3.zero);

        var sb = new StringBuilder();
        sb.AppendLine($"[RendererDump] Renderers within 5m of {origin}:");
        foreach (var renderer in Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            var dist = Vector3.Distance(renderer.transform.position, origin);
            if (dist > 5f)
            {
                continue;
            }
            var matName = renderer.sharedMaterial != null ? renderer.sharedMaterial.name : "null";
            var shaderName = renderer.sharedMaterial?.shader != null ? renderer.sharedMaterial.shader.name : "null";
            var meshFilter = renderer.GetComponent<MeshFilter>();
            var meshName = meshFilter?.sharedMesh != null ? meshFilter.sharedMesh.name : "-";
            sb.AppendLine(
                $"  - '{GetPath(renderer.gameObject)}' dist={dist:F2} enabled={renderer.enabled} " +
                $"activeInHierarchy={renderer.gameObject.activeInHierarchy} mesh={meshName} material={matName} shader={shaderName}");
        }
        Logger.LogInfo(sb.ToString());
    }

    private void DumpPlayerState()
    {
        var sb = new StringBuilder();
        var fps = Object.FindAnyObjectByType<FirstPersonController>(FindObjectsInactive.Include);
        if (fps == null)
        {
            sb.AppendLine("[PlayerDump] No FirstPersonController found anywhere in the loaded scenes (even inactive).");
        }
        else
        {
            sb.AppendLine($"[PlayerDump] FirstPersonController on '{GetPath(fps.gameObject)}': " +
                $"componentEnabled={fps.enabled} activeInHierarchy={fps.gameObject.activeInHierarchy} " +
                $"playerCanMove={fps.playerCanMove} cameraCanMove={fps.cameraCanMove} " +
                $"playerCamera={(fps.playerCamera != null ? fps.playerCamera.name : "null")}");
        }

        var gm = GameManager.instance;
        if (gm == null)
        {
            sb.AppendLine("[PlayerDump] GameManager.instance is null.");
        }
        else
        {
            sb.AppendLine($"[PlayerDump] GameManager.instance found. GetIsInDialogue()={gm.GetIsInDialogue()}");
        }

        Logger.LogInfo(sb.ToString());
    }

    private void DumpCanvases()
    {
        var sb = new StringBuilder();
        var canvases = Object.FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        sb.AppendLine($"[CanvasDump] Found {canvases.Length} Canvas(es):");
        foreach (var canvas in canvases)
        {
            var rect = canvas.GetComponent<RectTransform>();
            var rawImages = canvas.GetComponentsInChildren<RawImage>(true);
            var texts = canvas.GetComponentsInChildren<Text>(true).Length
                + canvas.GetComponentsInChildren<TMP_Text>(true).Length;
            var activeChildren = 0;
            foreach (Transform child in canvas.transform)
            {
                if (child.gameObject.activeSelf) activeChildren++;
            }
            sb.AppendLine(
                $"  - '{GetPath(canvas.gameObject)}' renderMode={canvas.renderMode} active={canvas.gameObject.activeInHierarchy} " +
                $"sortOrder={canvas.sortingOrder} size={rect.sizeDelta} directChildren={canvas.transform.childCount} " +
                $"activeDirectChildren={activeChildren} textElements={texts} rawImages={rawImages.Length}");
            foreach (var raw in rawImages)
            {
                sb.AppendLine(
                    $"      RawImage '{GetPath(raw.gameObject)}' active={raw.gameObject.activeInHierarchy} " +
                    $"texture={(raw.texture != null ? raw.texture.name + " (" + raw.texture.GetType().Name + ")" : "null")}");
            }
        }
        Logger.LogInfo(sb.ToString());
    }

    private void DumpCameras()
    {
        var scene = SceneManager.GetActiveScene();
        var sb = new StringBuilder();
        sb.AppendLine($"[CameraDump] Active scene: '{scene.name}'. Camera.main = " +
            (Camera.main != null ? Camera.main.name : "NULL"));

        var cameras = Object.FindObjectsByType<Camera>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        sb.AppendLine($"[CameraDump] Found {cameras.Length} Camera component(s) in the whole hierarchy:");
        foreach (var cam in cameras)
        {
            var go = cam.gameObject;
            var brain = go.GetComponent("CinemachineBrain");
            sb.AppendLine(
                $"  - Path='{GetPath(go)}' enabled={cam.enabled} activeInHierarchy={go.activeInHierarchy} " +
                $"tag={go.tag} depth={cam.depth} hasCinemachineBrain={brain != null} scene='{go.scene.name}'");
        }

        Logger.LogInfo(sb.ToString());
    }

    private static string GetPath(GameObject go)
    {
        var path = go.name;
        var t = go.transform.parent;
        while (t != null)
        {
            path = t.name + "/" + path;
            t = t.parent;
        }
        return path;
    }

    private static bool SkipOriginal() => false;
}
