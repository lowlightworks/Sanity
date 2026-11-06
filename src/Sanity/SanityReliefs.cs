using System.Collections.Generic;
using GameNetcodeStuff;
using UnityEngine;
using static System.FormattableString;

namespace Sanity;

// Ways back up outside the ship. Without them, playtests showed sanity as a one-way slide: about one upward tier
// change per player-day, Breaking spells of 145 s median (up to 440 s), and long days simply meant more time stuck at
// the bottom. These reliefs make the meter swing, so a longer day means more cycles, not more time at the bottom.
// Every number here was fitted with tools/replay.py against playtest data (target 35/28/22/15, Breaking spells
// 60-120 s); the same numbers live in replay.py's TUNING. Reliefs are instant gains (not limited by the ship's cap).
//   Regroup:  meeting a teammate after being alone (outside the ship) for long enough: +16, up to +32 for twice as
//             long. "Long enough" depends on the lobby, because regrouping is easier with more players: 60 s x 2/(n-1),
//             kept to 25..180 s (2 players 120 s, 3 players 60 s, 4 players 40 s). Playtest data: alone spells before
//             a regroup had a 90th percentile of 74 s with 3 players and 42 s with 4 (ratio 0.57, the rule says 0.67),
//             and the one 2-player regroup came after 140 s. Below Calm only.
//   Breather: stepping out of the facility in daylight (before Sundown) after at least 120 s inside: +10.
//   Find:     picking up new scrap (first time today, not yet collected): +2, at most once per 30 s (playtest days had
//             up to 42 such grabs).
//   Bounce:   75 s in Breaking (outside the ship) and the crisis passes: sanity jumps back to at least 58 (Uneasy), the
//             inside/outside/night drains run at half for 45 s ("you steady yourself"), and the director holds off
//             (a relax window). A peak symptom at Breaking (NotePeak) brings the bounce forward to 15 s after it.
internal static class SanityReliefs
{
    private const float RegroupRelief = 16f;
    private const float RegroupReliefMaxFactor = 2f;
    private const float RegroupAloneSeconds = 60f;      // for 3 players; x 2/(n-1) for other lobbies
    private const float RegroupAloneMin = 25f;
    private const float RegroupAloneMax = 180f;
    private const float BreatherRelief = 10f;
    private const float BreatherInsideSeconds = 120f;
    private const float SundownStart = 0.63f;
    private const float FindRelief = 2f;
    private const float FindGapSeconds = 30f;
    private const float BounceAfterSeconds = 75f;
    private const float BounceTo = 58f;
    private const float PeakBounceDelay = 15f;
    private const float ResilienceSeconds = 45f;
    private const float ResilienceDrain = 0.5f;
    private const float RelaxSeconds = 45f;

    internal static string TuningSummary => Invariant(
        $"reliefs: regroup +{RegroupRelief} (x{RegroupReliefMaxFactor} max) after {RegroupAloneSeconds}s x2/(n-1) alone ({RegroupAloneMin}-{RegroupAloneMax}s); ") + Invariant(
        $"breather +{BreatherRelief} after {BreatherInsideSeconds}s inside (daylight); find +{FindRelief} per new scrap, {FindGapSeconds}s apart; ") + Invariant(
        $"bounce to {BounceTo} after {BounceAfterSeconds}s in Breaking, drains x{ResilienceDrain} for {ResilienceSeconds}s, relax {RelaxSeconds}s");

    // The inside, outside and night drains are multiplied by this (0.5 while steadying after a bounce).
    internal static float DrainMultiplier => resilienceLeft > 0f ? ResilienceDrain : 1f;

    // For telemetry and the readout.
    internal static float AloneSeconds => aloneSeconds;

    // For the debug menu: where each relief stands.
    internal static string DebugSummary => Invariant(
        $"alone {aloneSeconds:F0}s (regroup after {Mathf.Min(RegroupNeed(SanityMeter.PlayersAtStart), 9999f):F0}) | inside visit {insideVisit:F0}s (breather after {BreatherInsideSeconds:F0}) | Breaking {breakingSeconds:F0}s (bounce at {BounceAfterSeconds:F0}){(resilienceLeft > 0f ? Invariant($" | drains halved {resilienceLeft:F0}s") : "")}");
    internal static int RegroupsToday;
    internal static int BreathersToday;
    internal static int FindsToday;
    internal static int BouncesToday;

    private static float aloneSeconds;
    private static bool wasCompany;
    private static float insideVisit;
    private static bool wasInside;
    private static float breakingSeconds;
    private static float resilienceLeft;
    private static float lastFind = -9999f;
    private static readonly HashSet<GrabbableObject> FoundToday = new HashSet<GrabbableObject>();
    private static readonly HashSet<GrabbableObject> HeldBefore = new HashSet<GrabbableObject>();
    private static readonly HashSet<GrabbableObject> HeldNow = new HashSet<GrabbableObject>();
    private static bool slotsPrimed;

    // Off a moon (or the mod switched off): a fresh start.
    internal static void Reset()
    {
        aloneSeconds = 0f;
        wasCompany = false;
        insideVisit = 0f;
        wasInside = false;
        breakingSeconds = 0f;
        resilienceLeft = 0f;
        lastFind = -9999f;
        FoundToday.Clear();
        HeldBefore.Clear();
        slotsPrimed = false;
        RegroupsToday = BreathersToday = FindsToday = BouncesToday = 0;
    }

    // Called by the meter every frame while alive on a moon, after the value moved. Returns how much was gained.
    internal static void Tick(PlayerControllerB player, float dt)
    {
        if (resilienceLeft > 0f)
        {
            resilienceLeft -= dt;
        }

        // Regroup: company starts after a long enough spell alone (the ship resets the spell: it's its own relief).
        bool company = SanityMeter.NearTeammate;
        bool ship = SanityMeter.InShip;
        if (company && !wasCompany && SanityMeter.Value < SanityMeter.CalmLine)
        {
            float need = RegroupNeed(SanityMeter.PlayersAtStart);
            if (aloneSeconds >= need)
            {
                float gain = RegroupRelief * Mathf.Min(RegroupReliefMaxFactor, aloneSeconds / need);
                float got = SanityMeter.Relief(gain);
                RegroupsToday++;
                Telemetry.Event("RELIEF", got, Invariant($"regroup after {aloneSeconds:F0}s alone (needs {need:F0}s) +{got:F1}"));
            }
        }
        aloneSeconds = company || ship ? 0f : aloneSeconds + dt;
        wasCompany = company;

        // Breather: out of the facility into daylight after a long trip inside.
        bool inside = SanityMeter.Inside;
        if (wasInside && !inside && !ship && insideVisit >= BreatherInsideSeconds && SanityMeter.DayFraction < SundownStart)
        {
            float got = SanityMeter.Relief(BreatherRelief);
            BreathersToday++;
            Telemetry.Event("RELIEF", got, Invariant($"breather after {insideVisit:F0}s inside +{got:F1}"));
        }
        insideVisit = inside ? insideVisit + dt : 0f;
        wasInside = inside;

        // Find: new scrap in your hands or slots.
        DetectFinds(player);
    }

    // Called after the tier is worked out: the bounce out of a long Breaking spell. True = it bounced.
    internal static bool AfterTier()
    {
        breakingSeconds = SanityMeter.Tier == SanityTier.Breaking && !SanityMeter.InShip ? breakingSeconds + Time.deltaTime : 0f;
        if (breakingSeconds < BounceAfterSeconds)
        {
            return false;
        }

        float lasted = breakingSeconds;
        breakingSeconds = 0f;
        float got = SanityMeter.Relief(Mathf.Max(0f, BounceTo - SanityMeter.Value));
        resilienceLeft = ResilienceSeconds;
        BouncesToday++;
        SanityDirector.Relax(RelaxSeconds, "bounce");
        Telemetry.Event("BOUNCE", got, Invariant($"after {lasted:F0}s in Breaking +{got:F1}, drains x{ResilienceDrain} for {ResilienceSeconds:F0}s"));
        return true;
    }

    // A peak symptom at Breaking (dread, a rush that reaches you, the death static, a presence set piece): the crisis
    // passes soon after it.
    internal static void NotePeak()
    {
        if (SanityMeter.Tier == SanityTier.Breaking)
        {
            breakingSeconds = Mathf.Max(breakingSeconds, BounceAfterSeconds - PeakBounceDelay);
        }
    }

    private static float RegroupNeed(int players)
    {
        if (players <= 1)
        {
            return float.MaxValue;   // solo: nobody to regroup with
        }
        return Mathf.Clamp(RegroupAloneSeconds * 2f / (players - 1), RegroupAloneMin, RegroupAloneMax);
    }

    private static void DetectFinds(PlayerControllerB player)
    {
        GrabbableObject[] slots = player.ItemSlots;
        if (slots == null)
        {
            return;
        }
        HeldNow.Clear();
        foreach (GrabbableObject item in slots)
        {
            if (item != null)
            {
                HeldNow.Add(item);
            }
        }
        if (player.ItemOnlySlot != null)
        {
            HeldNow.Add(player.ItemOnlySlot);
        }

        if (slotsPrimed)
        {
            foreach (GrabbableObject item in HeldNow)
            {
                if (HeldBefore.Contains(item) || item.itemProperties == null || !item.itemProperties.isScrap || item.scrapValue <= 0
                    || item.isInShipRoom || item.scrapPersistedThroughRounds || !FoundToday.Add(item))
                {
                    continue;
                }
                if (Time.time - lastFind >= FindGapSeconds)
                {
                    lastFind = Time.time;
                    float got = SanityMeter.Relief(FindRelief);
                    FindsToday++;
                    Telemetry.Event("RELIEF", got, Invariant($"found {item.itemProperties.itemName} +{got:F1}"));
                }
            }
        }
        slotsPrimed = true;
        HeldBefore.Clear();
        HeldBefore.UnionWith(HeldNow);
    }
}
