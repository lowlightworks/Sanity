using System;
using System.Collections.Generic;
using GameNetcodeStuff;
using Sanity.Symptoms;
using UnityEngine;
using static System.FormattableString;
using Random = UnityEngine.Random;

namespace Sanity;

// Layers on top of the director's lottery. Their job is an organic, unpredictable spread at the same average rate,
// plus meaningful pile-ups instead of random ones.
//
// Why waves and not self-exciting clusters: in simulations of the director at a steady sanity, the gaps between
// symptoms had a CV (coefficient of variation: how uneven they are) of 0.73 for the plain director; self-excitation
// only reached ~0.8 and mostly added short gaps
// (piles), and a "relax after 3 in 60 s" rule turned it into regular bursts. Slow waves of activity (x1.6 for ~2 min,
// x0.4 for ~1.5 min, random lengths) reached CV ~0.9 at the same mean, mostly by adding lulls. So: waves for spread,
// chains for meaningful piles, relax windows only after peaks and bounces.

// The day's mood, rolled per player at the start of each day: tilts which kinds of symptom are likely, and a little
// the overall rate. Days differ from each other; within a day it's coherent.
internal static class DayMood
{
    private struct Mood
    {
        internal string Name;
        internal float Rate;
        internal float Sound;
        internal float Sight;
        internal float Body;
        internal float Odds;
    }

    private static readonly Mood[] Moods =
    {
        new Mood { Name = "steady", Rate = 1f, Sound = 1f, Sight = 1f, Body = 1f, Odds = 0.30f },
        new Mood { Name = "whispers", Rate = 1f, Sound = 1.5f, Sight = 0.8f, Body = 0.8f, Odds = 0.175f },
        new Mood { Name = "tricks", Rate = 1f, Sound = 0.8f, Sight = 1.5f, Body = 0.8f, Odds = 0.175f },
        new Mood { Name = "nerves", Rate = 1f, Sound = 0.8f, Sight = 0.8f, Body = 1.5f, Odds = 0.175f },
        new Mood { Name = "quiet", Rate = 0.85f, Sound = 1f, Sight = 1f, Body = 1f, Odds = 0.0875f },
        new Mood { Name = "restless", Rate = 1.15f, Sound = 1f, Sight = 1f, Body = 1f, Odds = 0.0875f },
    };

    private static int index = -1;

    internal static string Name => index >= 0 ? Moods[index].Name : "none";
    internal static float Rate => index >= 0 ? Moods[index].Rate : 1f;

    internal static string TuningSummary
    {
        get
        {
            List<string> parts = new List<string>();
            foreach (Mood mood in Moods)
            {
                parts.Add(Invariant($"{mood.Name} {mood.Odds:0.###} rate x{mood.Rate} sound/sight/body x{mood.Sound}/{mood.Sight}/{mood.Body}"));
            }
            return "moods " + string.Join(", ", parts);
        }
    }

    // A symptom's weight multiplier today, by its family ("sound", "sight", "body"; anything else x1).
    internal static float Tilt(string family)
    {
        if (index < 0)
        {
            return 1f;
        }
        Mood mood = Moods[index];
        return family == "sound" ? mood.Sound : family == "sight" ? mood.Sight : family == "body" ? mood.Body : 1f;
    }

    internal static void EnsureRolled()
    {
        if (index >= 0)
        {
            return;
        }
        float roll = Random.value;
        index = Moods.Length - 1;
        for (int i = 0; i < Moods.Length; i++)
        {
            roll -= Moods[i].Odds;
            if (roll <= 0f)
            {
                index = i;
                break;
            }
        }
        Mood chosen = Moods[index];
        Telemetry.Event("MOOD", chosen.Rate, Invariant($"{chosen.Name}: rate x{chosen.Rate} sound x{chosen.Sound} sight x{chosen.Sight} body x{chosen.Body}"));
    }

    internal static void Reset()
    {
        index = -1;
    }
}

// Slow waves of activity: the director's chance runs at x1.6 for a while (on average 2 min), then x0.4 (on average
// 1.5 min), switching at random. The average is about x1.09, which the director's base chance (0.044) allows for.
internal static class Waves
{
    private const float HighMultiplier = 1.6f;
    private const float LowMultiplier = 0.4f;
    private const float HighMeanSeconds = 120f;
    private const float LowMeanSeconds = 90f;

    internal static bool High { get; private set; } = true;
    internal static float Multiplier => High ? HighMultiplier : LowMultiplier;

    internal static string TuningSummary => Invariant(
        $"waves x{HighMultiplier} ~{HighMeanSeconds}s / x{LowMultiplier} ~{LowMeanSeconds}s");

    internal static void Tick(float dt)
    {
        if (Random.value < dt / (High ? HighMeanSeconds : LowMeanSeconds))
        {
            High = !High;
        }
    }

    // Each day starts in a random wave state (in proportion to how long each lasts).
    internal static void Reset()
    {
        High = Random.value < HighMeanSeconds / (HighMeanSeconds + LowMeanSeconds);
    }
}

// Chains: a symptom can bring a likely follow-up a moment later, so two things line up into "that was real". Each link
// has a chance, a lowest tier and a delay; the follow-up still has to make sense (its own conditions), but it skips the
// lottery and the recovery check, doesn't spend the director's roll, and ignores relax windows (it belongs to the same
// moment). Telemetry: CHAIN (scheduled, played or why not), SYMPTOM "(chain from X)".
internal static class Chains
{
    private sealed class Link
    {
        internal string From = "";
        internal string Trigger = "";
        internal string To = "";
        internal float Chance;
        internal SanityTier MinTier;
        internal float MinDelay;
        internal float MaxDelay;
        internal Func<PlayerControllerB, SanityTier, Vector3, Vector3, string> Play = (_, _, _, _) => "no action";
    }

    private struct Pending
    {
        internal Link Link;
        internal float At;
        internal SanityTier Tier;
        internal Vector3 Point;
        internal Vector3 Direction;
    }

    private static readonly List<Pending> Queue = new List<Pending>();

    private static readonly Link[] Links =
    {
        // No flicker -> fear link: a flicker on its own catches you off guard but isn't worth real fear. Lights stay
        // part of bigger chains (the approaching sound below, the Presence).
        // A rush that reaches you hits you.
        new Link { From = "PhantomSteps", Trigger = "reached you", To = "PhantomHit", Chance = 0.6f, MinTier = SanityTier.Breaking,
                   MinDelay = 0.1f, MaxDelay = 0.3f, Play = (p, t, _, _) => PlayHit(p, t, "the rush reached you") },
        // A teammate "died" on the radio: the dread follows.
        new Link { From = "PhantomWalkie", Trigger = "death static", To = "PhantomDread", Chance = 0.5f, MinTier = SanityTier.Breaking,
                   MinDelay = 3f, MaxDelay = 6f, Play = (p, t, _, _) => PlayCard("PhantomDread", p, t) },
        // Deliberately no follow-ups after the locked door or the vanishing item: they should leave you thinking "huh,
        // it was just locked" or "I misremembered", not that something is there. And nothing runs off after a phantom
        // hit: that never happens in the game, so it isn't believable.
        // Something comes closer... and the lights die.
        new Link { From = "PhantomSound", Trigger = "approaching", To = "Flicker", Chance = 0.3f, MinTier = SanityTier.Breaking,
                   MinDelay = 0.5f, MaxDelay = 1.5f, Play = (p, t, _, _) => FlickerSymptom.FlickNow(p, t) },
        // You come out to the wrong time... and the radio clicks.
        new Link { From = "LostTime", Trigger = "shown", To = "PhantomWalkie", Chance = 0.5f, MinTier = SanityTier.Uneasy,
                   MinDelay = 3f, MaxDelay = 6f, Play = (p, _, _, _) => PlayCard("PhantomWalkie", p, SanityTier.Uneasy) },
    };

    internal static string TuningSummary
    {
        get
        {
            List<string> parts = new List<string>();
            foreach (Link link in Links)
            {
                parts.Add(Invariant($"{link.From}:{link.Trigger}->{link.To} {link.Chance:0.##} {link.MinTier}+"));
            }
            return "chains " + string.Join(", ", parts);
        }
    }

    // A symptom reports a moment (played, shown, reached you, ...): schedule any follow-ups that roll in.
    internal static void Trigger(Symptom from, string trigger, Vector3 point = default, Vector3 direction = default)
    {
        SanityTier tier = SanityMeter.Tier;
        foreach (Link link in Links)
        {
            if (link.From != from.ConfigKey || link.Trigger != trigger || tier < link.MinTier
                || (!SanityDirector.ChainsAlways && Random.value >= link.Chance))
            {
                continue;
            }
            Symptom? target = SanityDirector.Find(link.To);
            if (target != null && !target.Enabled)
            {
                continue;
            }
            Queue.Add(new Pending
            {
                Link = link,
                At = Time.time + Random.Range(link.MinDelay, link.MaxDelay),
                Tier = tier,
                Point = point,
                Direction = direction,
            });
            Telemetry.Event("CHAIN", 0f, $"{link.From} {link.Trigger} -> {link.To} scheduled");
        }
    }

    // For the debug menu: follow-ups waiting to play.
    internal static string PendingSummary
    {
        get
        {
            if (Queue.Count == 0)
            {
                return "";
            }
            List<string> parts = new List<string>();
            foreach (Pending pending in Queue)
            {
                parts.Add(Invariant($"{pending.Link.From} -> {pending.Link.To} in {Mathf.Max(0f, pending.At - Time.time):F1}s"));
            }
            return string.Join(", ", parts);
        }
    }

    // Every frame on a moon: run the follow-ups that are due.
    internal static void Tick(PlayerControllerB player)
    {
        for (int i = Queue.Count - 1; i >= 0; i--)
        {
            Pending pending = Queue[i];
            if (Time.time < pending.At)
            {
                continue;
            }
            Queue.RemoveAt(i);
            if (player.isPlayerDead)
            {
                continue;
            }

            string why = pending.Link.Play(player, pending.Tier, pending.Point, pending.Direction);
            Link link = pending.Link;
            if (why.Length == 0)
            {
                Symptom? target = SanityDirector.Find(link.To);
                if (target != null)
                {
                    SanityDirector.NoteChained(target, pending.Tier, link.From);
                }
                Telemetry.Event("CHAIN", 1f, $"{link.From} {link.Trigger} -> {link.To} played");
            }
            else
            {
                Telemetry.Event("CHAIN", 0f, $"{link.From} {link.Trigger} -> {link.To} didn't play: {why}");
            }
        }
    }

    internal static void Reset()
    {
        Queue.Clear();
    }

    // A card's own conditions (not its recovery), then play it. "" = played, else why not.
    private static string PlayCard(string key, PlayerControllerB player, SanityTier tier)
    {
        Symptom? symptom = SanityDirector.Find(key);
        if (symptom == null)
        {
            return "no such symptom";
        }
        string reason = symptom.CantPlayReason(player);
        if (reason.Length > 0)
        {
            return reason;
        }
        symptom.LastFailure = "";
        return symptom.TryPlay(player, tier) ? "" : symptom.LastFailure;
    }

    private static string PlayHit(PlayerControllerB player, SanityTier tier, string why)
    {
        if (!(SanityDirector.Find("PhantomHit") is PhantomHitSymptom hit))
        {
            return "no such symptom";
        }
        string reason = hit.CantPlayReason(player);
        if (reason.Length > 0)
        {
            return reason;
        }
        return hit.Start(player, tier, withCause: false, why) ? "" : hit.LastFailure;
    }
}
