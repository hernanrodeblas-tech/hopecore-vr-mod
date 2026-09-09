using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace UnityVRModFix;

// Two changes to FirstPersonController.FixedUpdate():
//
// 1. It clamps the move action's Y axis to a minimum of 0 (`Mathf.Max(0f, val.y)`), so the
//    player can never walk backward - looks intentional in the original flat game (a design
//    choice, maybe tension-related), but feels wrong/limiting in VR. Removed.
//
// 2. It moves relative to the character body's transform (driven by mouse/gamepad "Look", i.e.
//    body yaw), not the headset. In VR, pushing the stick "forward" should walk toward wherever
//    the player is actually looking with their head, like most VR games do - not toward
//    whatever direction the flat camera happens to be facing. Movement is now computed from
//    the VR headset's yaw (flattened to the horizontal plane, ignoring head pitch/roll) when
//    VR is active, falling back to the original body-relative direction otherwise.
//
// REVERSIBLE: set Enabled = false below (or delete this file) to restore the original
// movement exactly as the base game has it.
internal static class BackwardMovementFix
{
    internal static bool Enabled = true;

    private static FieldInfo _rbField;
    private static FieldInfo _isGroundedField;
    private static FieldInfo _moveActionField;
    private static FieldInfo _sprintActionField;

    internal static void Apply(Harmony harmony)
    {
        if (!Enabled)
        {
            return;
        }

        var fixedUpdate = typeof(FirstPersonController).GetMethod(
            "FixedUpdate", BindingFlags.NonPublic | BindingFlags.Instance);
        if (fixedUpdate == null)
        {
            VRModFixLog.Info("[BackwardMovementFix] Could not find FirstPersonController.FixedUpdate; skipping patch.");
            return;
        }

        var t = typeof(FirstPersonController);
        _rbField = t.GetField("rb", BindingFlags.NonPublic | BindingFlags.Instance);
        _isGroundedField = t.GetField("isGrounded", BindingFlags.NonPublic | BindingFlags.Instance);
        _moveActionField = t.GetField("m_moveAction", BindingFlags.NonPublic | BindingFlags.Instance);
        _sprintActionField = t.GetField("m_sprintAction", BindingFlags.NonPublic | BindingFlags.Instance);

        harmony.Patch(fixedUpdate, prefix: new HarmonyMethod(
            typeof(BackwardMovementFix).GetMethod(nameof(Prefix), BindingFlags.NonPublic | BindingFlags.Static)));

        VRModFixLog.Info("[BackwardMovementFix] Patched FixedUpdate: backward movement allowed, and movement now follows the headset's gaze in VR.");
    }

    // Same as the original FixedUpdate, minus the Mathf.Max(0f, val.y) clamp, and using the
    // headset's flattened forward/right instead of the character transform's when in VR.
    private static bool Prefix(FirstPersonController __instance)
    {
        if (!__instance.playerCanMove)
        {
            return false;
        }

        var moveAction = (UnityEngine.InputSystem.InputAction)_moveActionField.GetValue(__instance);
        var sprintAction = (UnityEngine.InputSystem.InputAction)_sprintActionField.GetValue(__instance);
        var rb = (Rigidbody)_rbField.GetValue(__instance);
        var isGrounded = (bool)_isGroundedField.GetValue(__instance);

        var val = moveAction.ReadValue<Vector2>();
        var moveInput = new Vector2(val.x, val.y); // no Mathf.Max(0f, ...) - backward allowed.

        __instance.isWalking = (moveInput.x != 0f || moveInput.y != 0f) && isGrounded;

        var speed = (__instance.enableSprint && sprintAction != null && sprintAction.IsPressed())
            ? __instance.sprintSpeed
            : __instance.walkSpeed;

        GetMoveAxes(__instance, out var forward, out var right);
        var move = (right * moveInput.x + forward * moveInput.y) * speed;

        var velocity = rb.linearVelocity;
        var delta = move - velocity;
        delta.x = Mathf.Clamp(delta.x, -__instance.maxVelocityChange, __instance.maxVelocityChange);
        delta.z = Mathf.Clamp(delta.z, -__instance.maxVelocityChange, __instance.maxVelocityChange);
        delta.y = 0f;
        rb.AddForce(delta, ForceMode.Impulse); // matches original FixedUpdate's (ForceMode)2

        return false;
    }

    private static void GetMoveAxes(FirstPersonController instance, out Vector3 forward, out Vector3 right)
    {
        var vrRig = VRModInternals.GetVrRigTransform();
        var gazeCam = vrRig?.Find("OpenVR_VRCamera_Left")?.GetComponent<Camera>();
        if (gazeCam == null)
        {
            forward = instance.transform.forward;
            right = instance.transform.right;
            return;
        }

        forward = gazeCam.transform.forward;
        forward.y = 0f;
        forward = forward.sqrMagnitude > 0.0001f ? forward.normalized : instance.transform.forward;

        right = gazeCam.transform.right;
        right.y = 0f;
        right = right.sqrMagnitude > 0.0001f ? right.normalized : instance.transform.right;
    }
}
