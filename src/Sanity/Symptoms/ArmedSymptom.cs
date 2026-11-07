using System.Collections.Generic;
using System.Text;
using GameNetcodeStuff;
using UnityEngine;
using static System.FormattableString;

namespace Sanity.Symptoms;

// A symptom that needs a particular moment (looking at a door, walking up to an item, leaving the facility), so it
// can't play the instant the director picks it. Picking it ARMS it instead ("the gun is loaded"): the director counts
// that as its symptom for the roll (dip, tension and recency restart, so the overall frequency stays the director's),
// and the symptom then waits for its moment and fires on its own. Rules for every armed symptom:
//   - One of each kind at a time: it isn't in the lottery while armed or while it's showing. At most MaxArmed armed
//     symptoms wait at once (2), so a third kind can't pile on. A symptom that waits all day for a rare moment (lost
//     time) has a slot of its own instead (CountsTowardCap), so it never blocks the others.
//   - It fires only while you're at or below its tier; above it, it keeps waiting.
//   - It never fires within MinGapSeconds of another symptom you could notice (two things in the same instant read as
//     a pattern). Its own arming doesn't count (it's invisible).
//   - It's dropped if you die, the day ends, or it waits longer than ExpireSeconds.
//   - Firing counts for pacing too: the director's dip restarts (a short breather after something visible); tension
//     and the roll itself were already spent when it was armed.
// Telemetry: SYMPTOM_ARMED when armed, SYMPTOM when it fires (with how long it waited), SYMPTOM_DROPPED when dropped.
// Both add how long it was held back (above its tier, too soon after another symptom) and the near misses the symptom
// counted (moments that almost fitted, e.g. a door hovered while a monster was near), to tune what's too strict.
internal abstract class ArmedSymptom : Symptom
{
    private const float MinGapSeconds = 5f;
    private const int MaxArmed = 2;

    private bool armed;
    private float armedFor;
    private bool armedByDebug;   // armed from the debug menu with checks ignored: fires at any tier, no gap
    private bool armedByMenu;    // armed from the debug menu or F8 (checks on): no gap or relax wait, tier still applies
    private float heldByTier;
    private float heldByGap;
    private readonly Dictionary<string, int> misses = new Dictionary<string, int>();
    private static readonly StringBuilder Text = new StringBuilder(160);

    internal bool IsArmed => armed;

    internal float ArmedFor => armedFor;

    internal bool IsShowing => Showing;

    internal float WaitLimit => ExpireSeconds;

    // For the debug menu: what holds it back right now ("" = nothing), and its own word on the moment.
    internal string HeldByNow => HeldBy;

    internal virtual string MomentStatus(PlayerControllerB player) => "";

    // Armed from the debug menu with "Ignore conditions": fires at any tier and without the gap, so it can be tested.
    protected bool ArmedByDebug => armedByDebug;

    // While armed: what holds it back this frame ("" = nothing, "tier" = you're above its tier, "gap" = too soon after
    // another symptom). Set before WatchAlways runs, so a symptom can count moments it had to let pass.
    protected string HeldBy { get; private set; } = "";

    // Count a near miss for telemetry (a moment that almost fitted). Kinds are short: "monster", "short visit".
    protected void Miss(string kind)
    {
        misses[kind] = misses.TryGetValue(kind, out int n) ? n + 1 : 1;
    }

    // Called when it's armed (e.g. to start watching from a clean slate).
    protected virtual void OnArmed()
    {
    }

    // How long it waits for its moment before it's dropped.
    protected virtual float ExpireSeconds => 240f;

    // What it waits for, in a few words, for the debug menu and telemetry ("a closed door you look at").
    internal abstract string Waits { get; }

    // Showing right now (between firing and ending)? Then it can't be armed again yet.
    protected abstract bool Showing { get; }

    // Why it can't be armed right now, beyond being switched off or the wrong tier ("" = it can).
    protected abstract string CantArmReason(PlayerControllerB player);

    // Called every frame while armed and allowed to fire: fire if the moment is here. True = it fired.
    protected abstract bool TryFire(PlayerControllerB player, SanityTier tier);

    // Called every frame on a moon, armed or not, before the armed check (e.g. to run or end what's showing).
    protected virtual void WatchAlways(PlayerControllerB player)
    {
    }

    // End whatever is showing at once and put everything back (off a moon, or dropped).
    protected virtual void StopShowing(string why)
    {
    }

    // Forget anything that lasts for a day (called off a moon, after StopShowing).
    protected virtual void ResetDayState()
    {
    }

    internal sealed override string CantPlayReason(PlayerControllerB player)
    {
        if (armed)
        {
            return Invariant($"already armed: waiting for {Waits} ({armedFor:F0} s)");
        }
        if (Showing)
        {
            return "already showing";
        }
        int waiting = ArmedCount();
        if (CountsTowardCap && waiting >= MaxArmed && !SanityDirector.ConditionsIgnored)
        {
            return Invariant($"armed cap: {waiting} already waiting");
        }
        return CantArmReason(player);
    }

    // Picked by the director (or the debug menu): arm it. Nothing happens yet.
    internal sealed override bool TryPlay(PlayerControllerB player, SanityTier tier)
    {
        if (armed || Showing)
        {
            LastFailure = "already armed or showing";
            return false;
        }

        armed = true;
        armedFor = 0f;
        armedByDebug = SanityDirector.ConditionsIgnored;
        armedByMenu = SanityDirector.ForcedContext;
        heldByTier = 0f;
        heldByGap = 0f;
        misses.Clear();
        HeldBy = "";
        OnArmed();
        return true;
    }

    internal sealed override void Watch(PlayerControllerB player)
    {
        HeldBy = !armed || armedByDebug ? ""
            : SanityMeter.Tier < MinTier ? "tier"
            : armedByMenu ? ""     // a test from the menu fires at its moment, without the gap or a relax window
            : SanityDirector.SecondsSinceVisible < MinGapSeconds ? "gap"
            : SanityDirector.Relaxing ? "relax"
            : "";
        WatchAlways(player);
        if (!armed)
        {
            return;
        }

        armedFor += Time.deltaTime;
        if (player.isPlayerDead)
        {
            Drop("died");
            return;
        }
        if (!Enabled && !armedByDebug)
        {
            Drop("switched off");
            return;
        }
        if (armedFor > ExpireSeconds)
        {
            Drop(Invariant($"nothing came up in {ExpireSeconds:F0} s"));
            return;
        }
        if (HeldBy == "tier")
        {
            heldByTier += Time.deltaTime;
            return;
        }
        if (HeldBy == "gap" || HeldBy == "relax")
        {
            heldByGap += Time.deltaTime;
            return;
        }

        // The strength is the tier at the moment it fires.
        SanityTier tier = SanityMeter.Tier < MinTier ? MinTier : SanityMeter.Tier;
        if (TryFire(player, tier))
        {
            armed = false;
            SanityDirector.NoteFired(this, tier, armedFor, WaitSummary());
        }
    }

    internal sealed override void ResetDay()
    {
        if (armed)
        {
            Drop("day ended");
        }
        StopShowing("day ended");
        ResetDayState();
    }

    // For a symptom that catches its moment itself (a hook on the game's own code), so it can fire right then rather
    // than on the next frame: the same rules as the per-frame watch (tier, gap, relax; held-back state from this
    // frame). True = it fired.
    protected bool FireIfFree(PlayerControllerB player)
    {
        if (!armed || HeldBy.Length > 0 || player.isPlayerDead || (!Enabled && !armedByDebug))
        {
            return false;
        }
        SanityTier tier = SanityMeter.Tier < MinTier ? MinTier : SanityMeter.Tier;
        if (!TryFire(player, tier))
        {
            return false;
        }
        armed = false;
        SanityDirector.NoteFired(this, tier, armedFor, WaitSummary());
        return true;
    }

    protected void Drop(string why)
    {
        armed = false;
        Telemetry.Event("SYMPTOM_DROPPED", armedFor, $"{Name} armed but never fired: {why}" + WaitSummary());
    }

    // Whether it takes one of the MaxArmed shared slots while it waits (false: a slot of its own).
    protected virtual bool CountsTowardCap => true;

    // Armed symptoms waiting right now that take a shared slot (for the cap).
    private static int ArmedCount()
    {
        int count = 0;
        foreach (Symptom symptom in SanityDirector.Symptoms)
        {
            if (symptom is ArmedSymptom other && other.armed && other.CountsTowardCap)
            {
                count++;
            }
        }
        return count;
    }

    // " held back: tier 12s gap 5s; near misses: monster=2 fear=1" (empty parts left out).
    private string WaitSummary()
    {
        Text.Clear();
        if (heldByTier >= 0.5f || heldByGap >= 0.5f)
        {
            Text.Append(Invariant($" held back: tier {heldByTier:F0}s gap {heldByGap:F0}s"));
        }
        if (misses.Count > 0)
        {
            Text.Append(Text.Length > 0 ? ";" : "").Append(" near misses:");
            foreach (KeyValuePair<string, int> miss in misses)
            {
                Text.Append(' ').Append(miss.Key.Replace(' ', '_')).Append('=').Append(miss.Value);
            }
        }
        return Text.ToString();
    }
}
