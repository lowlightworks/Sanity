using GameNetcodeStuff;
using Sanity.Symptoms;
using UnityEngine;
using static System.FormattableString;

namespace Sanity;

// Watches for things that look broken on screen, to find out whether Sanity is involved (seen in testing: the game's
// grab / interact prompt stopped appearing while the actions still worked). Twice a second: if you're aiming at
// something the game would label (an interactable trigger with a tip, or an item in grab reach) but the cursor tip is
// empty or hidden, it records PROMPT_MISSING (at most once every 30 s) with what Sanity had going at the time.
internal static class Diagnostics
{
    private const float CheckSeconds = 0.5f;
    private const float ReportGapSeconds = 30f;
    private static float nextCheck;
    private static float lastReport = -9999f;
    private static int missingChecks;

    internal static string LastProblem { get; private set; } = "";

    internal static void Tick(PlayerControllerB player)
    {
        if (Time.time < nextCheck || player.isPlayerDead || player.cursorTip == null)
        {
            return;
        }
        nextCheck = Time.time + CheckSeconds;
        if (player.isGrabbingObjectAnimation || player.inSpecialMenu || player.inTerminalMenu || player.isTypingChat
            || (player.quickMenuManager != null && player.quickMenuManager.isMenuOpen) || player.inSpecialInteractAnimation || player.twoHanded)
        {
            missingChecks = 0;
            return;
        }

        string expected = Expected(player);
        bool shown = player.cursorTip.enabled && player.cursorTip.gameObject.activeInHierarchy
                     && !string.IsNullOrEmpty(player.cursorTip.text);
        if (expected.Length == 0 || shown)
        {
            missingChecks = 0;
            return;
        }
        missingChecks++;
        if (missingChecks < 3 || Time.time - lastReport < ReportGapSeconds)
        {
            return;   // a frame or two of nothing is normal; three checks in a row (1.5 s) isn't
        }
        lastReport = Time.time;
        LastProblem = Invariant($"{Time.time:F0}s: no prompt over {expected}");
        Telemetry.Event("PROMPT_MISSING", 0f, Invariant(
            $"over {expected}; tip enabled {player.cursorTip.enabled} active {player.cursorTip.gameObject.activeInHierarchy} text \"{player.cursorTip.text}\"; ")
            + Invariant($"sanity: debug menu {(DebugMenu.IsOpen ? 1 : 0)}, presence {(PresenceSymptom.Active ? 1 : 0)}, last symptom {SanityDirector.LastSymptom}, fake hp {PhantomHitSymptom.Lost}"));
    }

    // What the game would show a tip for right now ("" = nothing).
    private static string Expected(PlayerControllerB player)
    {
        InteractTrigger trigger = player.hoveringOverTrigger;
        if (trigger != null && (player.isHoldingInteract || trigger.isPlayingSpecialAnimation))
        {
            return "";   // the game leaves the tip as it is while you hold use, or blanks it during a trigger's animation
        }
        if (trigger != null)
        {
            string tip = trigger.interactable ? trigger.hoverTip : trigger.disabledHoverTip;
            return string.IsNullOrEmpty(tip) ? "" : "a trigger (" + trigger.gameObject.name + ")";
        }
        Transform eye = player.gameplayCamera.transform;
        if (Physics.Raycast(new Ray(eye.position, eye.forward), out RaycastHit hit, player.grabDistance, player.interactableObjectsMask)
            && hit.collider.gameObject.layer != 8 && hit.collider.gameObject.layer != 30 && hit.collider.CompareTag("PhysicsProp"))
        {
            GrabbableObject item = hit.collider.GetComponent<GrabbableObject>();
            return item != null && item.grabbable ? "an item (" + item.name + ")" : "";
        }
        return "";
    }
}
