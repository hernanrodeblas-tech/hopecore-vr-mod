using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace UnityVRModFix;

// The player's own "FirstPersonController Variant" object carries a visible Capsule mesh with a
// placeholder "WIP" checkerboard material - almost certainly a leftover dev visualization of the
// player's collision capsule. In flat play the camera always sits dead-center inside it, so it's
// invisible; in VR, head movement lets the eye position drift off-center, revealing the inside
// of your own collision capsule as a checkered cylinder wrapped around your head. Hide it in VR.
internal static class PlayerCapsuleFix
{
    private static Renderer _capsuleRenderer;

    internal static void Apply(Harmony harmony)
    {
        var setupType = System.Type.GetType(
            "UnityVRMod.Features.VrVisualization.VrCameraSetup_CoreOpenVR, UnityVRMod");
        var setupRig = setupType?.GetMethod("SetupCameraRig", BindingFlags.Public | BindingFlags.Instance);
        var teardownRig = setupType?.GetMethod("TeardownCameraRig", BindingFlags.Public | BindingFlags.Instance);
        if (setupRig == null || teardownRig == null)
        {
            VRModFixLog.Info("[PlayerCapsuleFix] Could not find SetupCameraRig/TeardownCameraRig; skipping patch.");
            return;
        }

        harmony.Patch(setupRig, postfix: new HarmonyMethod(
            typeof(PlayerCapsuleFix).GetMethod(nameof(OnRigSetup), BindingFlags.NonPublic | BindingFlags.Static)));
        harmony.Patch(teardownRig, postfix: new HarmonyMethod(
            typeof(PlayerCapsuleFix).GetMethod(nameof(OnRigTeardown), BindingFlags.NonPublic | BindingFlags.Static)));

        VRModFixLog.Info("[PlayerCapsuleFix] Patched VR rig setup/teardown to hide the player's own collision capsule mesh.");
    }

    private static void OnRigSetup()
    {
        var fps = Object.FindAnyObjectByType<FirstPersonController>(FindObjectsInactive.Include);
        if (fps == null)
        {
            return;
        }
        _capsuleRenderer = fps.GetComponent<Renderer>();
        if (_capsuleRenderer != null)
        {
            _capsuleRenderer.enabled = false;
            VRModFixLog.Info("[PlayerCapsuleFix] Disabled the player's capsule renderer for VR.");
        }
    }

    private static void OnRigTeardown()
    {
        if (_capsuleRenderer != null)
        {
            _capsuleRenderer.enabled = true;
        }
    }
}
