using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace UnityVRModFix;

// There's a "CameraCleaner" camera (depth -100, no Cinemachine) in the Persistent scene that
// renders first, most likely drawing a sphere mesh that's meant to always exactly enclose the
// flat game camera (a common trick to mask/clean the background). In flat play the camera is
// always dead-center in it so it's invisible; in VR, head movement lets the player's actual eye
// position drift off that exact center, so the inside of that sphere becomes visible as a wall
// you can walk into or a shell around your head. Simplest fix: don't render it in VR at all.
internal static class CameraCleanerFix
{
    private static Camera _cleanerCam;

    internal static void Apply(Harmony harmony)
    {
        var setupType = System.Type.GetType(
            "UnityVRMod.Features.VrVisualization.VrCameraSetup_CoreOpenVR, UnityVRMod");
        var setupRig = setupType?.GetMethod("SetupCameraRig", BindingFlags.Public | BindingFlags.Instance);
        var teardownRig = setupType?.GetMethod("TeardownCameraRig", BindingFlags.Public | BindingFlags.Instance);
        if (setupRig == null || teardownRig == null)
        {
            VRModFixLog.Info("[CameraCleanerFix] Could not find SetupCameraRig/TeardownCameraRig; skipping patch.");
            return;
        }

        harmony.Patch(setupRig, postfix: new HarmonyMethod(
            typeof(CameraCleanerFix).GetMethod(nameof(OnRigSetup), BindingFlags.NonPublic | BindingFlags.Static)));
        harmony.Patch(teardownRig, postfix: new HarmonyMethod(
            typeof(CameraCleanerFix).GetMethod(nameof(OnRigTeardown), BindingFlags.NonPublic | BindingFlags.Static)));

        VRModFixLog.Info("[CameraCleanerFix] Patched VR rig setup/teardown to hide 'CameraCleaner' while in VR.");
    }

    private static void OnRigSetup()
    {
        FindCleaner();
        if (_cleanerCam != null)
        {
            _cleanerCam.enabled = false;
            VRModFixLog.Info("[CameraCleanerFix] Disabled 'CameraCleaner' camera for VR.");
        }
    }

    private static void OnRigTeardown()
    {
        if (_cleanerCam != null)
        {
            _cleanerCam.enabled = true;
        }
    }

    private static void FindCleaner()
    {
        if (_cleanerCam != null)
        {
            return;
        }
        foreach (var cam in Object.FindObjectsByType<Camera>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (cam.name == "CameraCleaner")
            {
                _cleanerCam = cam;
                return;
            }
        }
    }
}
