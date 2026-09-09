using HarmonyLib;
using UnityEngine;

namespace UnityVRModFix;

// Physics-driven objects (the canoe, the sliding stones, etc.) move in FixedUpdate at the
// physics tick rate (~50Hz by default), not the render rate. Without Rigidbody interpolation,
// their visual position only updates once per physics step and holds still in between - on a
// normal 60Hz monitor that's mildly noticeable; at a VR headset's 90Hz+ refresh it reads as
// outright juddering/choppy motion, especially for anything the camera is attached to or
// following closely. Turn on interpolation for every non-kinematic Rigidbody so its rendered
// position gets smoothed between physics steps instead of jumping.
//
// First attempt at this did it for every Rigidbody in the scene, including the player's own
// (FirstPersonController also moves itself via rb.AddForce in FixedUpdate, same pattern as the
// canoe/stones) - that made movement feel choppy everywhere, not just the boat. Our own VR head
// tracking re-syncs the rig to the tracked camera every frame (CameraFollowFix), and interpolating
// the player's Rigidbody changes exactly when/how its rendered position updates each frame -
// almost certainly what clashed. Excluding the player's own Rigidbody here targets just the
// vehicles/platforms the fix was meant for.
internal static class RigidbodyInterpolationFix
{
    internal static void Apply(Harmony harmony)
    {
        var setupType = System.Type.GetType(
            "UnityVRMod.Features.VrVisualization.VrCameraSetup_CoreOpenVR, UnityVRMod");
        var setupRig = setupType?.GetMethod("SetupCameraRig", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        if (setupRig == null)
        {
            VRModFixLog.Info("[RigidbodyInterpolationFix] Could not find SetupCameraRig; skipping patch.");
            return;
        }

        harmony.Patch(setupRig, postfix: new HarmonyMethod(
            typeof(RigidbodyInterpolationFix).GetMethod(nameof(OnRigSetup), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)));

        VRModFixLog.Info("[RigidbodyInterpolationFix] Patched VR rig setup to force Rigidbody interpolation on scene load.");
    }

    private static void OnRigSetup()
    {
        var count = 0;
        foreach (var rb in Object.FindObjectsByType<Rigidbody>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (rb.isKinematic || rb.interpolation != RigidbodyInterpolation.None)
            {
                continue;
            }
            if (rb.GetComponent<FirstPersonController>() != null)
            {
                continue;
            }
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            count++;
        }
        if (count > 0)
        {
            VRModFixLog.Info($"[RigidbodyInterpolationFix] Enabled interpolation on {count} Rigidbody(s).");
        }
    }
}
