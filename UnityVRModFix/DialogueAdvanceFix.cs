namespace UnityVRModFix;

// YarnSpinner's LineAdvancer waits for a keyboard key (Space/Escape by default) or a specific
// Input Action reference to advance/skip a dialogue line - none of which our VR controllers
// drive. Without that, a line just sits there forever waiting for "next", and
// GameManager.GetIsInDialogue() never clears, which blocks the crosshair/interact ray from ever
// running again. Bypass all of that by calling GameManager's own DialogueRunner directly -
// the same instance DialogueTrigger.StartDialogue() uses to start it - whenever the player
// presses the right controller's A button while a dialogue is showing.
internal static class DialogueAdvanceFix
{
    internal static void OnInteractButtonPressed()
    {
        var gm = GameManager.instance;
        if (gm == null || !gm.GetIsInDialogue() || gm.dialogueRunner == null)
        {
            return;
        }

        gm.dialogueRunner.RequestNextLine();
        VRModFixLog.Info(
            $"[DialogueAdvanceFix] Requested next dialogue line via right controller A button. " +
            $"IsDialogueRunning={gm.dialogueRunner.IsDialogueRunning}");
    }
}
