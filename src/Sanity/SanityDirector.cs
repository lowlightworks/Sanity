using System.Collections.Generic;
using System.Text;
using BepInEx.Configuration;
using GameNetcodeStuff;
using Sanity.Symptoms;
using UnityEngine;
using UnityEngine.InputSystem;
using static System.FormattableString;

namespace Sanity;

// Decides WHEN a symptom happens and WHICH one. The symptoms themselves live in Symptoms/.
//
// WHEN: every frame there is a small chance. The chance per second is
//     MaxChancePerSecond x depth x dip x tension
//   depth   - how far below the Calm line: DepthFloor just below it, 1 at 0 (smooth, no tier steps)
//   dip     - low right after a symptom, recovering over about half a minute (a soft cooldown: piles stay possible)
//   tension - grows the longer it's been quiet, so there are no long droughts
//
// WHICH: a lottery (by base weight) among the symptoms that make sense right now. The winner is then
// accepted with a chance equal to its recovery (low if it played recently). If it's rejected or fails to
// play (e.g. no valid spot), the next is drawn in the same roll. If none play, nothing happens and
// tension keeps building (the roll isn't wasted).
//
// ARMED symptoms (Symptoms/ArmedSymptom.cs) need a moment the director can't produce (a door you look at, an item
// you walk up to, leaving the facility). Winning the lottery arms them; that counts as the roll's symptom, and they
// fire on their own when the moment comes (NoteFired).
internal static class SanityDirector
{
    // ---- Tuning (checked against playtest telemetry) ----
    private const float MaxChancePerSecond = 0.044f;  // allows for the waves (DirectorLayers), which average x1.09
    private const float DepthFloor = 0.4f;           // depth the moment you leave Calm, so early Uneasy isn't dead
    private const float DipFloor = 0.1f;             // chance multiplier right after a symptom
    private const float DipRecoverySeconds = 8f;     // how fast the dip recovers (10% right after, ~95% by 24 s)
    private const float TensionDoublingSeconds = 120f; // quiet time that doubles the chance
    private const float PeakRelaxSeconds = 50f;        // after a peak, no natural rolls for this long

    // Every symptom the director knows about. New symptoms get added to this list.
    private static readonly List<Symptom> AllSymptoms = new List<Symptom>
    {
        new FlickerSymptom(),
        new PhantomFearSymptom(),
        new PhantomDreadSymptom(),
        new PhantomSoundSymptom(),
        new PhantomHitSymptom(),
        new PhantomStepsSymptom(),
        new PhantomWalkieSymptom(),
        new FakeLockedDoorSymptom(),
        new VanishingItemSymptom(),
        new LostTimeSymptom(),
        new PresenceSymptom(),
    };

    private static readonly List<Symptom> Eligible = new List<Symptom>();
    private static readonly HashSet<Symptom> HeldForSprint = new HashSet<Symptom>();   // in this roll only because they can wait

    // The ROLL telemetry row for the roll being decided (reused, so a roll makes no garbage beyond its text).
    private static readonly StringBuilder RollText = new StringBuilder(320);

    // For the debug menu.
    internal static IReadOnlyList<Symptom> Symptoms => AllSymptoms;
    internal static bool Paused;

    // Debug toggles (F7 menu). By default forced plays (F8, menu buttons) go through every natural check.
    internal static bool IgnoreConditions;     // forced plays skip every check (tier, conditions, config, gaps, time of day)
    internal static bool IgnoreGapsNatural;    // natural rolls skip the gaps (recovery check, shared fear gap)
    internal static bool IgnoreGapsForced;     // forced plays skip the gaps, but keep every other check

    // What applies to the play being decided right now. Symptoms read these (fear gap, time-of-day roll).
    internal static bool GapsIgnored { get; private set; }
    internal static bool ConditionsIgnored { get; private set; }

    // True while a forced play (F8, a debug menu button) is being handled.
    internal static bool ForcedContext { get; private set; }

    // The "can't play" reason of symptoms your own sprinting drowns out. While SprintCheckOff is set, they skip that
    // check, so the director can ask whether sprinting is the only thing in the way (then the symptom can be held).
    internal const string SprintingReason = "busy: sprinting";
    internal static bool SprintCheckOff;

    // Debug: every chain link rolls in (its chance treated as 1), so follow-ups can be tested.
    internal static bool ChainsAlways;

    internal static float RelaxLeft => Mathf.Max(0f, relaxUntil - Time.time);

    internal static float QuietSeconds => quietSeconds;

    // The director's numbers in one line, for the telemetry session row.
    internal static string TuningSummary => Invariant(
        $"chance {MaxChancePerSecond}/s floor {DepthFloor} dip {DipFloor}/{DipRecoverySeconds}s tension x2 per {TensionDoublingSeconds}s; ")
        + Waves.TuningSummary + "; " + DayMood.TuningSummary + "; " + Chains.TuningSummary;

    // ---- State ----
    internal static float ChancePerSecond;
    internal static float SecondsSinceLast = 9999f;

    // Seconds since something you could notice (a symptom that played, an armed one firing, a chain). Arming isn't
    // visible, so it doesn't reset this: armed symptoms keep their 5 s gap from this clock (SecondsSinceLast would make
    // every armed symptom also wait 5 s after its own arming).
    internal static float SecondsSinceVisible = 9999f;
    internal static string LastSymptom = "none";
    private static float quietSeconds;             // quiet time while symptoms are possible (drives tension)
    private static float relaxUntil;               // a relax window: no natural rolls until then (Time.time)
    private static float secondsOnMoon;            // today's time on the moon (the mood is rolled a moment in)

    // A relax window is running: no natural rolls, armed symptoms wait, tension doesn't build.
    internal static bool Relaxing => Time.time < relaxUntil;

    // Start (or extend) a relax window: after a peak, a bounce, or a burst of symptoms (like Left 4 Dead's AI
    // director: let it breathe).
    internal static void Relax(float seconds, string why)
    {
        float until = Time.time + seconds;
        if (until > relaxUntil)
        {
            relaxUntil = until;
            Telemetry.Event("RELAX", seconds, why);
        }
    }

    // One on/off setting per symptom, in the config's "Symptoms" section (live in LethalConfig).
    internal static void BindConfig(ConfigFile config)
    {
        foreach (Symptom symptom in AllSymptoms)
        {
            ConfigEntry<bool> entry = config.Bind(
                "Symptoms",
                symptom.ConfigKey,
                true,
                $"Allow the {symptom.Name} symptom. Off: it never happens on its own (the debug menu can still fire it).");
            Symptom captured = symptom;
            entry.SettingChanged += (_, _) =>
                Telemetry.Event("SYMPTOM_TOGGLED", entry.Value ? 1f : 0f, $"{captured.Name} {(entry.Value ? "on" : "off")}");
            symptom.EnabledEntry = entry;
        }
    }

    // Called once per frame for the local player, right after the sanity meter.
    internal static void Tick(PlayerControllerB player)
    {
        // Testing shortcut: F8 forces a symptom now. Only works if EnableDebugKeys is on in the config.
        if (Plugin.DebugKeys.Value && Keyboard.current != null && Keyboard.current.f8Key.wasPressedThisFrame)
        {
            TryPlay(player, forced: true);
        }

        // Off a moon: nothing counts, and every day starts fully recovered (no dip, full weights, no tension).
        if (!SanityMeter.OnMoon)
        {
            quietSeconds = 0f;
            ChancePerSecond = 0f;
            SecondsSinceLast = 9999f;
            SecondsSinceVisible = 9999f;
            LastSymptom = "none";
            relaxUntil = 0f;
            secondsOnMoon = 0f;
            DayMood.Reset();
            Waves.Reset();
            Chains.Reset();
            SprintHold.Reset();
            foreach (Symptom symptom in AllSymptoms)
            {
                symptom.SecondsSincePlayed = 9999f;
                symptom.ResetDay();
            }
            return;
        }

        float dt = Time.deltaTime;
        SecondsSinceLast += dt;
        SecondsSinceVisible += dt;
        secondsOnMoon += dt;
        if (secondsOnMoon > 2f)
        {
            DayMood.EnsureRolled();   // a moment in, so its telemetry row lands in the new day
        }
        Waves.Tick(dt);
        foreach (Symptom symptom in AllSymptoms)
        {
            symptom.SecondsSincePlayed += dt;
            symptom.Watch(player);
        }
        Chains.Tick(player);
        SprintHold.Tick(player);

        // No symptoms while dead or Calm.
        if (player.isPlayerDead || SanityMeter.Tier == SanityTier.Calm)
        {
            ChancePerSecond = 0f;
            return;
        }

        // A relax window: nothing natural plays and tension doesn't build.
        if (Relaxing)
        {
            ChancePerSecond = 0f;
            return;
        }

        quietSeconds += dt;

        // Telemetry: every few seconds, note which symptoms could have played right now (nothing plays).
        EligibilityLog.Tick(player, dt);

        // The Calm line comes from the tier settings, so moving a tier line moves this too.
        float calmLine = SanityMeter.CalmLine;
        float depth = DepthFloor + (1f - DepthFloor) * Mathf.Clamp01((calmLine - SanityMeter.Value) / calmLine);
        float dip = 1f - (1f - DipFloor) * Mathf.Exp(-SecondsSinceLast / DipRecoverySeconds);
        float tension = 1f + quietSeconds / TensionDoublingSeconds;
        ChancePerSecond = MaxChancePerSecond * depth * dip * tension * Waves.Multiplier * DayMood.Rate;

        // Chance this frame = chance per second x the frame's length in seconds.
        if (!Paused && Random.value < ChancePerSecond * dt)
        {
            TryPlay(player, forced: false);
        }
    }

    private static void TryPlay(PlayerControllerB player, bool forced)
    {
        SetContext(forced);
        try
        {
            // Plays at the current tier. Only when ignoring conditions can F8 fire at Calm (at Uneasy strength).
            SanityTier tier = SanityMeter.Tier;
            if (ConditionsIgnored && tier == SanityTier.Calm)
            {
                tier = SanityTier.Uneasy;
            }

            // The ROLL row, part 1: the moment (tier, where, company, chance).
            string where = SanityMeter.Inside ? "inside" : SanityMeter.InShip ? "ship" : "outside";
            RollText.Clear();
            RollText.Append(Invariant($"{tier} {where} {(SanityMeter.NearTeammate ? "company" : "alone")} chance {ChancePerSecond:F3} wave {(Waves.High ? "high" : "low")} mood {DayMood.Name}{(forced ? " F8" : "")} |"));

            // Collect the symptoms that make sense right now. Part 2 of the row: each one's weight, or why it's out.
            Eligible.Clear();
            HeldForSprint.Clear();
            float totalWeight = 0f;
            foreach (Symptom symptom in AllSymptoms)
            {
                string reason = WhyNot(symptom, player, tier);
                if (reason == SprintingReason && symptom.HoldsForSprint && !SprintHold.Waiting && WouldPlayWithoutSprint(symptom, player, tier))
                {
                    // Only your sprinting is in the way: it can wait for you to slow down (SprintHold).
                    HeldForSprint.Add(symptom);
                    reason = "";
                }
                if (reason.Length == 0)
                {
                    Eligible.Add(symptom);
                    float weight = Weight(symptom);
                    totalWeight += weight;
                    RollText.Append(Invariant($" {symptom.ConfigKey}=w{weight:0.##}")).Append(HeldForSprint.Contains(symptom) ? "(sprint)" : "");
                }
                else
                {
                    RollText.Append(' ').Append(symptom.ConfigKey).Append("=[").Append(reason).Append(']');
                }
            }
            RollText.Append(" |");

            if (Eligible.Count == 0)
            {
                Telemetry.Event("ROLL_NOTHING_ELIGIBLE", (int)tier, forced ? "F8" : "");
            }

            // Draw from the lottery. A recently played winner is usually turned down (unless gaps are ignored).
            // If it's turned down or fails to play, remove it and draw again. Part 3 of the row: each draw's outcome.
            string played = "none";
            while (Eligible.Count > 0)
            {
                Symptom chosen = Pick(totalWeight);
                float recovery = chosen.Recovery;
                float check = Random.value;
                if (!GapsIgnored && check >= recovery)
                {
                    Telemetry.Event("SYMPTOM_REJECTED", recovery,
                        Invariant($"{chosen.Name} turned down (recovery {recovery * 100f:F0}%)") + (forced ? " (F8)" : ""));
                    RollText.Append(Invariant($" {chosen.ConfigKey}:rejected[recovery {recovery:F2} roll {check:F2}]"));
                }
                else if (HeldForSprint.Contains(chosen))
                {
                    HoldForSprint(chosen, tier, forced ? "F8" : "");
                    RollText.Append(' ').Append(chosen.ConfigKey).Append(":held");
                    played = chosen.ConfigKey;
                    break;
                }
                else if (PlayChosen(player, chosen, tier, forced ? "F8" : ""))
                {
                    RollText.Append(' ').Append(chosen.ConfigKey).Append(chosen is ArmedSymptom ? ":armed" : ":played");
                    played = chosen.ConfigKey;
                    break;
                }
                else
                {
                    RollText.Append(' ').Append(chosen.ConfigKey).Append(":failed[").Append(chosen.LastFailure).Append(']');
                }

                totalWeight -= Weight(chosen);
                Eligible.Remove(chosen);
            }

            RollText.Append(" -> ").Append(played);
            Telemetry.Event("ROLL", ChancePerSecond, RollText.ToString());

            // Nothing could play: no bookkeeping, so tension keeps building.
            if (forced && played == "none")
            {
                Plugin.Log.LogInfo("Debug: no symptom could play here right now.");
            }
        }
        finally
        {
            SetContext(forced: false);
        }
    }

    // Debug menu: fire one specific symptom at a chosen strength, through the same checks as a natural roll
    // unless the menu says otherwise. Returns a message for the menu.
    internal static string DebugFire(PlayerControllerB player, Symptom symptom, SanityTier tier)
    {
        SetContext(forced: true);
        try
        {
            if (!ConditionsIgnored)
            {
                if (!symptom.Enabled)
                {
                    return $"{symptom.Name} is off (tick it, or turn on Ignore conditions).";
                }
                if (SanityMeter.Tier < symptom.MinTier)
                {
                    return $"{symptom.Name} needs {symptom.MinTier} or lower; you're {SanityMeter.Tier}.";
                }
                string reason = symptom.CantPlayReason(player);
                if (reason.Length > 0)
                {
                    return $"{symptom.Name} can't play here: {reason}.";
                }
            }

            if (!GapsIgnored && Random.value >= symptom.Recovery)
            {
                Telemetry.Event("SYMPTOM_REJECTED", symptom.Recovery,
                    Invariant($"{symptom.Name} turned down (recovery {symptom.Recovery * 100f:F0}%) (debug menu)"));
                return Invariant($"{symptom.Name} was turned down by the recovery check ({symptom.Recovery * 100f:F0}% recovered).");
            }

            if (!PlayChosen(player, symptom, tier, "debug menu"))
            {
                return $"{symptom.Name} couldn't play: {symptom.LastFailure}.";
            }
            return symptom is ArmedSymptom armed
                ? $"Armed {symptom.Name}: it fires on its own at {armed.Waits}."
                : $"Fired {symptom.Name} at {tier} strength.";
        }
        finally
        {
            SetContext(forced: false);
        }
    }

    // The debug menu's status column: why this symptom couldn't play on a natural roll right now ("" = it could).
    internal static string NaturalCantPlayReason(Symptom symptom, PlayerControllerB player)
    {
        SetContext(forced: false);
        return symptom.CantPlayReason(player);
    }

    // Eligibility telemetry: why this symptom wouldn't be in a natural roll right now ("" = it would).
    internal static string NaturalWhyNot(Symptom symptom, PlayerControllerB player)
    {
        SetContext(forced: false);
        return WhyNot(symptom, player, SanityMeter.Tier);
    }

    // Why a symptom isn't in a roll's lottery ("" = it is): switched off, too high a tier, or its own conditions.
    private static string WhyNot(Symptom symptom, PlayerControllerB player, SanityTier tier)
    {
        if (!symptom.Enabled)
        {
            return "off";
        }
        if (ConditionsIgnored)
        {
            return "";
        }
        if (tier < symptom.MinTier)
        {
            return "tier: needs " + symptom.MinTier;
        }
        return symptom.CantPlayReason(player);
    }

    // Which debug toggles apply: forced plays (F8, menu buttons) or natural rolls.
    private static void SetContext(bool forced)
    {
        ConditionsIgnored = forced && IgnoreConditions;
        ForcedContext = forced;
        GapsIgnored = forced ? IgnoreConditions || IgnoreGapsForced : IgnoreGapsNatural;
    }

    // Every symptom goes through here, so the bookkeeping is always the same.
    // Only a symptom that actually played resets the dip, tension and its own recency.
    private static bool PlayChosen(PlayerControllerB player, Symptom symptom, SanityTier tier, string source)
    {
        symptom.LastFailure = "";
        string via = source.Length == 0 ? "" : $" ({source})";
        if (!symptom.TryPlay(player, tier))
        {
            Telemetry.Event("SYMPTOM_FAILED", (int)tier, $"{symptom.Name}{via}: {symptom.LastFailure}");
            return false;
        }

        symptom.SecondsSincePlayed = 0f;
        SecondsSinceLast = 0f;
        quietSeconds = 0f;
        if (symptom is ArmedSymptom armed)
        {
            // Nothing visible yet: it waits for its moment. The roll is spent all the same.
            LastSymptom = symptom.Name + " (armed)";
            Telemetry.Event("SYMPTOM_ARMED", (int)tier, $"{symptom.Name}{via}: waits for {armed.Waits}");
            return true;
        }

        LastSymptom = symptom.Name;
        SecondsSinceVisible = 0f;
        Telemetry.Event("SYMPTOM", (int)tier, symptom.Name + via);
        Probe(symptom);
        Chains.Trigger(symptom, "played");
        return true;
    }

    // Would this symptom be in the roll if you weren't sprinting?
    private static bool WouldPlayWithoutSprint(Symptom symptom, PlayerControllerB player, SanityTier tier)
    {
        SprintCheckOff = true;
        try
        {
            return WhyNot(symptom, player, tier).Length == 0;
        }
        finally
        {
            SprintCheckOff = false;
        }
    }

    // Drawn while you sprint: hold it until you slow down. Like arming, the roll is spent now and the director carries
    // on; it plays (or is dropped) on its own.
    private static void HoldForSprint(Symptom symptom, SanityTier tier, string source)
    {
        symptom.SecondsSincePlayed = 0f;
        SecondsSinceLast = 0f;
        quietSeconds = 0f;
        LastSymptom = symptom.Name + " (held: sprinting)";
        string via = source.Length == 0 ? "" : $" ({source})";
        Telemetry.Event("SYMPTOM_ARMED", (int)tier, $"{symptom.Name}{via}: waits for you to stop sprinting");
        SprintHold.Start(symptom);
    }

    // Telemetry: open a reaction probe for a symptom that just played (with where it came from, if anywhere).
    private static void Probe(Symptom symptom)
    {
        Reactions.OnSymptom(symptom, symptom.LastSource);
        symptom.LastSource = null;
    }

    // A symptom's weight in today's lottery: its base weight, tilted by the day's mood for its family.
    internal static float Weight(Symptom symptom)
    {
        return symptom.BaseWeight * DayMood.Tilt(symptom.Family);
    }

    // The symptom with this config key (for chains), or null.
    internal static Symptom? Find(string key)
    {
        foreach (Symptom symptom in AllSymptoms)
        {
            if (symptom.ConfigKey == key)
            {
                return symptom;
            }
        }
        return null;
    }

    // A peak (a Breaking set piece: dread, a rush that reached you, the death static, a Breaking phantom hit): the
    // crisis passes soon after (the bounce comes forward) and the director lets it breathe.
    internal static void Peak(string why)
    {
        SanityReliefs.NotePeak();
        Relax(PeakRelaxSeconds, "peak: " + why);
    }

    // A chained follow-up played (Chains): its own recency restarts; the dip and tension stay (same moment).
    internal static void NoteChained(Symptom symptom, SanityTier tier, string from)
    {
        symptom.SecondsSincePlayed = 0f;
        SecondsSinceVisible = 0f;
        LastSymptom = symptom.Name;
        Telemetry.Event("SYMPTOM", (int)tier, $"{symptom.Name} (chain from {from})");
        Probe(symptom);
        Chains.Trigger(symptom, "played");
    }

    // An armed symptom just fired (its moment came). Its recency restarts, and so does the dip, so the next roll
    // gives a short breather after something visible. Tension isn't touched: the roll was spent when it was armed.
    internal static void NoteFired(Symptom symptom, SanityTier tier, float waited, string extra = "")
    {
        symptom.SecondsSincePlayed = 0f;
        SecondsSinceLast = 0f;
        SecondsSinceVisible = 0f;
        LastSymptom = symptom.Name;
        Telemetry.Event("SYMPTOM", (int)tier, Invariant($"{symptom.Name} (armed {waited:F0} s before)") + extra);
        Probe(symptom);
        Chains.Trigger(symptom, "played");
    }

    // Weighted lottery: each symptom gets a slice proportional to its base weight.
    private static Symptom Pick(float totalWeight)
    {
        // Rounding left nothing to share out: treat the rest as equal.
        if (totalWeight <= 0f)
        {
            return Eligible[Random.Range(0, Eligible.Count)];
        }

        float roll = Random.value * totalWeight;
        foreach (Symptom symptom in Eligible)
        {
            roll -= Weight(symptom);
            if (roll <= 0f)
            {
                return symptom;
            }
        }

        return Eligible[Eligible.Count - 1];
    }
}
