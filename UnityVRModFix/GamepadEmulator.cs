using System.Collections.Generic;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace UnityVRModFix;

// "Move" starts out disabled for reasons unrelated to any input device (nothing in the game's
// own code ever calls Disable() on it), so WASD/stick never drives it without this. Force it
// back on every frame, but only while playerCanMove says movement should actually be allowed,
// so dialogue/cutscene/scene-specific movement locks still work.
internal static class ActionEnableFix
{
    // playerCanMove isn't reliable for this (seen as True even in scenes/moments that
    // shouldn't allow walking), so movement lock for specific scenes is handled with an
    // explicit per-scene rule instead of trusting that flag.
    private static readonly HashSet<string> ScenesWithMovementDisabled = new()
    {
        "1_ModuloMandosCohete", // Sitting at the rocket control console - no walking around.
    };

    private static FirstPersonController _fpsController;
    private static bool _didStartupDialogueReset;

    internal static void Tick()
    {
        if (!_didStartupDialogueReset && GameManager.instance != null)
        {
            _didStartupDialogueReset = true;
            // Force-enabling "Interact" from the very first frame can catch a stray
            // already-"performed" input state and fire an interaction before the player ever
            // presses anything, leaving GameManager stuck thinking a dialogue is running (which
            // then blocks the real first interaction later, since Interact is dialogue-gated).
            GameManager.instance.IsInDialogue(false);
        }

        if (_fpsController == null)
        {
            _fpsController = Object.FindAnyObjectByType<FirstPersonController>();
        }
        // NOT gating this on GameManager.GetIsInDialogue(): that flag gets stuck "true" even
        // after a dialogue genuinely ends (a pre-existing bug in the game, not us), and gating
        // movement on it risks permanently freezing the player rather than just letting them
        // walk during the occasional dialogue.
        var sceneBlocksMovement = ScenesWithMovementDisabled.Contains(SceneManager.GetActiveScene().name);
        if (sceneBlocksMovement)
        {
            // Move might already be enabled from a previous scene (it's a single global
            // action, not per-scene) - actively turn it off rather than just not re-enabling.
            ForceDisable("Move");
        }
        else if (_fpsController == null || _fpsController.playerCanMove)
        {
            ForceEnable("Move");
        }
        // "Look" (camera rotation) is gated by cameraCanMove, not playerCanMove: scenes that
        // deliberately immobilize the player (floating in zero-g, sitting at a console) still
        // want the player to be able to look around.
        if (_fpsController == null || _fpsController.cameraCanMove)
        {
            ForceEnable("Look");
        }

        // "Interact" DOES need the dialogue gate, unlike Move: re-enabling it while the
        // triggering button press is still held (right as the game disables it to start a
        // dialogue) makes the Input System fire an immediate second "performed" event, which
        // re-triggers the NPC and cuts the just-opened dialogue off instantly.
        var gm = GameManager.instance;
        if (gm == null || !gm.GetIsInDialogue())
        {
            ForceEnable("Interact");
        }
    }

    private static void ForceEnable(string actionName)
    {
        var action = InputSystem.actions?.FindAction(actionName, false);
        if (action != null && !action.enabled)
        {
            action.Enable();
        }
    }

    private static void ForceDisable(string actionName)
    {
        var action = InputSystem.actions?.FindAction(actionName, false);
        if (action != null && action.enabled)
        {
            action.Disable();
        }
    }
}
