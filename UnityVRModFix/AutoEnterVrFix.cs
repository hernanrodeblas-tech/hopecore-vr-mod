using System.Reflection;
using UnityEngine;

namespace UnityVRModFix;

// UnityVRMod always starts in "Safe Mode" (flat screen) and only initializes OpenVR the first time the
// player turns Safe Mode off with F11. Its own "Safe Mode Starts Active = false" setting does NOT
// fix that: it only sets the flag, and nothing ever triggers the first VR initialization without a
// toggle. So do exactly what F11 does, once, a few seconds after UnityVRMod has finished starting.
//
// If SteamVR isn't running or the headset isn't connected, UnityVRMod itself logs the failure and goes
// back to Safe Mode, so the game just stays on the flat screen and F11 still works later.
internal static class AutoEnterVrFix
{
    private const float DelaySeconds = 3f;

    internal static bool Enabled = true;

    private static bool _done;
    private static float _readySince = -1f;

    internal static void Tick()
    {
        if (_done || !Enabled)
        {
            return;
        }

        var feature = VRModInternals.GetVisualizationFeature();
        if (feature == null)
        {
            return;
        }

        if (_readySince < 0f)
        {
            _readySince = Time.unscaledTime;
            return;
        }
        if (Time.unscaledTime - _readySince < DelaySeconds)
        {
            return;
        }

        _done = true;
        var type = feature.GetType();
        var safeModeField = type.GetField("_isUserSafeModeActive", BindingFlags.NonPublic | BindingFlags.Instance);
        var toggle = type.GetMethod("ToggleUserSafeMode", BindingFlags.Public | BindingFlags.Instance);
        if (safeModeField == null || toggle == null)
        {
            VRModFixLog.Info("[AutoEnterVrFix] Could not find UnityVRMod's Safe Mode toggle; press F11 manually.");
            return;
        }
        if (!(bool)safeModeField.GetValue(feature))
        {
            return;
        }

        VRModFixLog.Info("[AutoEnterVrFix] Turning Safe Mode off automatically (same as pressing F11).");
        toggle.Invoke(feature, null);
    }
}
