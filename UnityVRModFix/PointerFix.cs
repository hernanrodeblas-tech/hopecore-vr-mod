using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace UnityVRModFix;

// FirstPersonController.CheckRay() raycasts from playerCamera's position/forward to find
// interactable NPCs/objects and toggle the crosshair. That camera's yaw follows the player's
// body (via CameraFollowFix) but its pitch is only driven by mouse/keyboard look, not the
// headset - so the interact ray doesn't point where the player is actually looking with their
// head. This replaces the raycast source with the VR headset's own gaze direction, so looking at
// something (with the existing on-screen crosshair as feedback) is what determines what you can
// interact with; movement, look, and the actual interact button all still come from normal
// keyboard/mouse.
internal static class PointerFix
{
    private const float InteractRayLength = 10f;

    private static FieldInfo _hitField;
    private static FieldInfo _canInteractField;
    private static float _nextLogTime;

    internal static void Apply(Harmony harmony)
    {
        var checkRay = typeof(FirstPersonController).GetMethod(
            "CheckRay", BindingFlags.NonPublic | BindingFlags.Instance);
        if (checkRay == null)
        {
            VRModFixLog.Info("[PointerFix] Could not find FirstPersonController.CheckRay; skipping patch.");
            return;
        }

        _hitField = typeof(FirstPersonController).GetField("m_hit", BindingFlags.NonPublic | BindingFlags.Instance);
        _canInteractField = typeof(FirstPersonController).GetField("m_canInteract", BindingFlags.NonPublic | BindingFlags.Instance);

        harmony.Patch(checkRay, prefix: new HarmonyMethod(
            typeof(PointerFix).GetMethod(nameof(Prefix), BindingFlags.NonPublic | BindingFlags.Static)));

        VRModFixLog.Info("[PointerFix] Patched CheckRay to raycast from the VR headset's gaze direction.");
    }

    private static bool Prefix(FirstPersonController __instance)
    {
        var vrRig = VRModInternals.GetVrRigTransform();
        var gazeCam = vrRig?.Find("OpenVR_VRCamera_Left")?.GetComponent<Camera>();
        var shouldLog = Time.time >= _nextLogTime;

        if (gazeCam == null)
        {
            if (shouldLog)
            {
                _nextLogTime = Time.time + 1f;
                VRModFixLog.Info($"[PointerFix] Not in VR yet; falling back to flat-camera raycast.");
            }
            return true; // Not in VR right now - let the original flat-camera logic run.
        }

        var ray = new Ray(gazeCam.transform.position, gazeCam.transform.forward);
        var didHit = Physics.Raycast(ray, out var hit, InteractRayLength);
        var isInteractable = didHit && hit.collider.gameObject.layer == LayerMask.NameToLayer("Interactable");

        if (shouldLog)
        {
            _nextLogTime = Time.time + 1f;
            VRModFixLog.Info($"[PointerFix] gaze ray from={ray.origin} dir={ray.direction} " +
                $"didHit={didHit} hitName={(didHit ? hit.collider.gameObject.name : "-")} " +
                $"hitLayer={(didHit ? LayerMask.LayerToName(hit.collider.gameObject.layer) : "-")} isInteractable={isInteractable}");
        }

        if (isInteractable)
        {
            __instance.EnableCrosshair();
            _hitField.SetValue(__instance, hit);
            _canInteractField.SetValue(__instance, true);
        }
        else
        {
            __instance.DisableCrosshair();
            _canInteractField.SetValue(__instance, false);
        }

        return false; // Skip the original flat-camera raycast.
    }
}
