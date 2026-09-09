using HarmonyLib;
using Unity.Cinemachine;
using UnityEngine;

namespace UnityVRModFix;

// The canoe/sliding-stones judder isn't just about the Rigidbody's own render position (fixed by
// RigidbodyInterpolationFix) - it's about the CAMERA that follows it. CinemachineBrain defaults
// to "SmartUpdate", which (per its own tooltip) picks FixedUpdate for a Rigidbody-driven target -
// meaning the camera's own transform only gets a new position 50 times/sec, same as physics,
// regardless of how smoothly the target itself renders. On a 60Hz monitor that's a small mismatch;
// at a VR headset's 90Hz+ it's the choppy camera motion the player is actually looking through
// (they're sitting IN the boat - it's the camera's judder they feel, not the boat mesh's).
//
// Forcing LateUpdate makes the Brain re-evaluate every render frame instead. Cinemachine's own
// docs call this less ideal for physics targets *without* Rigidbody interpolation (you'd read a
// stale, un-interpolated position) - but we already enabled interpolation on those Rigidbodies,
// so by the time LateUpdate runs each frame, Unity has already computed that frame's smoothed
// position for the target. LateUpdate + Rigidbody interpolation is the documented-correct pairing;
// SmartUpdate's automatic FixedUpdate choice is what was fighting it.
internal static class CinemachineUpdateModeFix
{
    internal static void Apply(Harmony harmony)
    {
        var setupType = System.Type.GetType(
            "UnityVRMod.Features.VrVisualization.VrCameraSetup_CoreOpenVR, UnityVRMod");
        var setupRig = setupType?.GetMethod("SetupCameraRig", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        if (setupRig == null)
        {
            VRModFixLog.Info("[CinemachineUpdateModeFix] Could not find SetupCameraRig; skipping patch.");
            return;
        }

        harmony.Patch(setupRig, postfix: new HarmonyMethod(
            typeof(CinemachineUpdateModeFix).GetMethod(nameof(OnRigSetup), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)));

        VRModFixLog.Info("[CinemachineUpdateModeFix] Patched VR rig setup to force CinemachineBrain.UpdateMethod to LateUpdate.");
    }

    private static void OnRigSetup()
    {
        var count = 0;
        foreach (var brain in Object.FindObjectsByType<CinemachineBrain>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (brain.UpdateMethod == CinemachineBrain.UpdateMethods.LateUpdate)
            {
                continue;
            }
            brain.UpdateMethod = CinemachineBrain.UpdateMethods.LateUpdate;
            count++;
        }
        if (count > 0)
        {
            VRModFixLog.Info($"[CinemachineUpdateModeFix] Forced {count} CinemachineBrain(s) to LateUpdate.");
        }
    }
}
