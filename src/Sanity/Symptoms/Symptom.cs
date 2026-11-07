using System.Collections.Generic;
using BepInEx.Configuration;
using GameNetcodeStuff;
using UnityEngine;

namespace Sanity.Symptoms;

// The shared template ("card") every symptom follows.
// The director only ever talks to this template, so adding a symptom never means editing the director.
internal abstract class Symptom
{
    // Shown in the readout and telemetry.
    internal abstract string Name { get; }

    // Its on/off setting's name in the config file (no spaces).
    internal abstract string ConfigKey { get; }

    // The on/off setting itself, created by the director at startup. Off = never picked naturally.
    internal ConfigEntry<bool>? EnabledEntry;
    internal bool Enabled => EnabledEntry == null || EnabledEntry.Value;

    // The symptom can appear from this tier downward (e.g. Uneasy = Uneasy, Disturbed and Breaking).
    internal abstract SanityTier MinTier { get; }

    // How likely it is to be picked, compared with the other symptoms that make sense right now.
    internal virtual float BaseWeight => 1f;

    // Its weight at a given tier: the base weight unless a symptom weighs differently at some tiers.
    internal virtual float WeightAt(SanityTier tier) => BaseWeight;

    // Its kind, for the day's mood ("sound", "sight", "body"): a mood tilts whole families up or down.
    internal virtual string Family => "sound";

    // Right after it plays, its weight drops to almost zero, then recovers over roughly this many seconds.
    internal virtual float RecoverySeconds => 60f;

    // True for symptoms your own sprinting drowns out (phantom sound, phantom steps): drawn while you sprint, they wait
    // for you to slow down instead of being skipped (SprintHold).
    internal virtual bool HoldsForSprint => false;

    // Not used (always empty): follow-ups are defined as chains in DirectorLayers (Chains).
    internal virtual IReadOnlyList<Symptom> FollowUps => System.Array.Empty<Symptom>();

    // Why this symptom can't play right now, for this player. "" means it can.
    // Otherwise a short reason that starts with its kind, e.g. "fear: 0.15 (needs below 0.1)" or "not inside".
    // The part before the colon is the kind: telemetry adds up time per kind, the debug menu shows the whole text.
    // It must only look, never change anything: telemetry asks every few seconds just to record what was possible.
    internal abstract string CantPlayReason(PlayerControllerB player);

    // Does this symptom make sense right now, for this player?
    internal bool CanPlay(PlayerControllerB player) => CantPlayReason(player).Length == 0;

    // Make it happen. The tier lets a symptom get stronger as sanity drops.
    // Returns false if it couldn't happen after all (e.g. no valid spot); then nothing counts as played.
    internal abstract bool TryPlay(PlayerControllerB player, SanityTier tier);

    // Why the last TryPlay failed, for the debug menu.
    internal string LastFailure = "";

    // Where the last play came from, if it had a place (a phantom sound, steps, the presence): telemetry's reaction
    // probes check whether you turned toward it. Set by the symptom, read and cleared by the director.
    internal Vector3? LastSource;

    // Called every frame on a moon (alive or dead, any tier), before any roll, for symptoms that need to watch
    // something over time (e.g. the path you walked). Must be cheap and must never play anything by itself.
    // For the debug menu: what it's doing right now if it's running ("" = not running).
    internal virtual string RunningState(PlayerControllerB player) => "";

    internal virtual void Watch(PlayerControllerB player)
    {
    }

    // Called every frame off a moon: forget anything that lasts for a day (e.g. a once-a-day effect).
    internal virtual void ResetDay()
    {
    }

    // ---- Bookkeeping, handled by the director ----
    internal float SecondsSincePlayed = 9999f;

    // How recovered it is since it last played: near 0 right after, rising toward 1 (fast at first, then slower).
    // The director accepts a drawn symptom only with this chance, so recency is a real soft limit.
    internal float Recovery => 1f - Mathf.Exp(-SecondsSincePlayed / RecoverySeconds);
}
