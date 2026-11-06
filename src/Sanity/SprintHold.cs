using GameNetcodeStuff;
using Sanity.Symptoms;
using UnityEngine;
using static System.FormattableString;

namespace Sanity;

// A phantom sound or phantom steps drawn while you sprint would be drowned out by your own footsteps. Instead of being
// skipped, it waits for you to slow down, like an armed symptom: the director's roll is spent when it's held, and the
// director carries on as normal. It plays the moment you're no longer sprinting and it still makes sense, or it's
// dropped after HoldSeconds. Only one waits at a time, and it doesn't count toward the armed symptoms' cap.
// Telemetry: SYMPTOM_ARMED when held, SYMPTOM "(held while you sprinted)" when it plays, SYMPTOM_DROPPED if it doesn't.
internal static class SprintHold
{
    private const float HoldSeconds = 10f;
    private const float MinGapSeconds = 5f;    // as armed symptoms: never right on top of another symptom you noticed
    private const float RetrySeconds = 0.5f;   // a failed attempt (no spot, not walking yet) is tried again this often

    private static Symptom? waiting;
    private static float waited;
    private static float nextTry;
    private static string holding = "";

    internal static bool Waiting => waiting != null;

    // For the debug menu ("" when nothing waits).
    internal static string Status => waiting == null ? ""
        : Invariant($"{waiting.Name}: held while you sprint {waited:F0}/{HoldSeconds:F0}s (now: {holding})");

    // The director drew this symptom while you sprint: hold it.
    internal static void Start(Symptom symptom)
    {
        waiting = symptom;
        waited = 0f;
        nextTry = 0f;
        holding = "sprinting";
    }

    // Every frame on a moon.
    internal static void Tick(PlayerControllerB player)
    {
        if (waiting == null)
        {
            return;
        }

        waited += Time.deltaTime;
        if (player.isPlayerDead)
        {
            Drop("died");
            return;
        }
        if (!waiting.Enabled)
        {
            Drop("switched off");
            return;
        }
        if (waited > HoldSeconds)
        {
            Drop(Invariant($"nothing came up in {HoldSeconds:F0} s (last: {holding})"));
            return;
        }

        holding = player.isSprinting ? "sprinting"
            : SanityMeter.Tier < waiting.MinTier ? "tier"
            : SanityDirector.SecondsSinceVisible < MinGapSeconds ? "gap"
            : SanityDirector.Relaxing ? "relax"
            : "";
        if (holding.Length > 0 || Time.time < nextTry)
        {
            return;
        }

        nextTry = Time.time + RetrySeconds;
        string reason = waiting.CantPlayReason(player);
        if (reason.Length > 0)
        {
            holding = reason;
            return;
        }
        SanityTier tier = SanityMeter.Tier;
        waiting.LastFailure = "";
        if (!waiting.TryPlay(player, tier))
        {
            holding = waiting.LastFailure;
            return;
        }

        Symptom played = waiting;
        float heldFor = waited;
        waiting = null;
        SanityDirector.NoteFired(played, tier, heldFor, " (held while you sprinted)");
    }

    // Off a moon: nothing waits.
    internal static void Reset()
    {
        waiting = null;
    }

    private static void Drop(string why)
    {
        if (waiting != null)
        {
            Telemetry.Event("SYMPTOM_DROPPED", waited, $"{waiting.Name} held while you sprinted, never played: {why}");
        }
        waiting = null;
    }
}
