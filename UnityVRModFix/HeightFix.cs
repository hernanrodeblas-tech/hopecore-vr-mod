using System.Reflection;
using HarmonyLib;
using UnityVRMod.Features.VRVisualization.OpenVR;

namespace UnityVRModFix;

// UnityVRMod sets the OpenVR tracking space to "Standing", which reports the HMD's pose as an
// ABSOLUTE height above the real-world floor. That absolute height then gets applied as a local
// offset on top of the VR rig, which is already sitting at the game camera's own (correct) eye
// height - so the player's real standing height effectively gets added a second time, making
// everything look twice as tall as it should. Switching to "Seated" tracking space makes OpenVR
// report head pose RELATIVE to wherever the headset was when VR was last activated/reset, which
// is what a rig that's already positioned by the game's own camera needs.
internal static class HeightFix
{
    internal static void Apply(Harmony harmony)
    {
        var setupType = System.Type.GetType(
            "UnityVRMod.Features.VrVisualization.VrCameraSetup_CoreOpenVR, UnityVRMod");
        var initializeVr = setupType?.GetMethod("InitializeVr", BindingFlags.Public | BindingFlags.Instance);
        if (initializeVr == null)
        {
            VRModFixLog.Info("[HeightFix] Could not find InitializeVr method; skipping patch.");
            return;
        }

        harmony.Patch(initializeVr, postfix: new HarmonyMethod(
            typeof(HeightFix).GetMethod(nameof(Postfix), BindingFlags.NonPublic | BindingFlags.Static)));

        VRModFixLog.Info("[HeightFix] Patched InitializeVr to force Seated tracking space (fixes doubled height).");
    }

    private static void Postfix(bool __result)
    {
        if (__result && OpenVR.Compositor != null)
        {
            OpenVR.Compositor.SetTrackingSpace(ETrackingUniverseOrigin.TrackingUniverseSeated);
            VRModFixLog.Info("[HeightFix] Tracking space set to Seated.");
        }
    }
}
