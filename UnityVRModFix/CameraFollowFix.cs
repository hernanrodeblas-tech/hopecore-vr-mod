using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace UnityVRModFix;

// UnityVRMod snapshots the tracked camera's position/yaw ONCE, when the VR rig is first built,
// and never again. This game drives its camera with Cinemachine (reframing shots, following the
// player, etc.), so a couple of seconds after the rig is set up the real camera has moved on and
// the VR rig is left behind, floating wherever the camera happened to be at that first instant.
// This re-syncs the rig's position/yaw to the tracked camera every frame, right before the mod
// applies the HMD's head-tracking offset on top of it, so head tracking still works but the rig
// as a whole keeps following the game's camera like it's supposed to.
internal static class CameraFollowFix
{
    internal static void Apply(Harmony harmony)
    {
        var setupType = System.Type.GetType(
            "UnityVRMod.Features.VrVisualization.VrCameraSetup_CoreOpenVR, UnityVRMod");
        if (setupType == null)
        {
            VRModFixLog.Info("[CameraFollowFix] Could not find VrCameraSetup_CoreOpenVR type; skipping patch.");
            return;
        }

        var updatePosesMethod = setupType.GetMethod("UpdatePoses", BindingFlags.Public | BindingFlags.Instance);
        if (updatePosesMethod == null)
        {
            VRModFixLog.Info("[CameraFollowFix] Could not find UpdatePoses method; skipping patch.");
            return;
        }

        harmony.Patch(updatePosesMethod, prefix: new HarmonyMethod(
            typeof(CameraFollowFix).GetMethod(nameof(Prefix), BindingFlags.NonPublic | BindingFlags.Static)));

        VRModFixLog.Info("[CameraFollowFix] Patched UpdatePoses to keep the VR rig synced with the tracked camera every frame.");
    }

    private static void Prefix(object __instance)
    {
        var vrRig = VRModInternals.GetVrRigTransform(__instance);
        var trackedCam = VRModInternals.GetTrackedOriginalCameraTransform(__instance);
        if (vrRig == null || trackedCam == null)
        {
            return;
        }

        var yaw = Quaternion.Euler(0f, trackedCam.eulerAngles.y, 0f);
        vrRig.SetPositionAndRotation(trackedCam.position, yaw);
    }
}
