using System.Collections;
using System.Collections.Generic;
using GameNetcodeStuff;
using UnityEngine;
using static System.FormattableString;

namespace Sanity.Symptoms;

// Phantom hits. Something hits you: an attack sound behind you (a monster that attacks without killing outright,
// MonsterSoundCatalog.Attackers), then that monster's own hit, once: the damage it really does in the game (Hits).
// With the Presence it's the Presence's mask; after a rush of footsteps it's a person (a shovel's 20). A monster that
// only kills, or hits for more than can be shown, doesn't fake a hit. The hit is the game's own hit package: the HUD
// hit animation and red tint, the damage sound, visor cracks, a camera shake (big for hits of 20 or more), the body
// flinch, and a fear spike. The health it takes is shown as gone (the HUD, and HUD mods that draw health: LethalHUD,
// GeneralImprovements, show your real health minus this lost amount), held for 10 s after the hit, then it creeps
// back 5 HP a second, five times the game's own regeneration (it must come back, but not so fast it looks fake). Real
// damage stacks on top (real 100, 25 lost: shows 75; a real 20 hit: real 80, shows 55) and restarts the 10 s hold;
// real healing raises the shown value by the same amount. It never shows below 26 (the game's critical-injury effects
// start under 20, and we don't fake those): if real damage would take it there, the lie shrinks instead, and at real
// health under 26 the truth shows. Cleared when you die, leave the moon or are revived.
// Real health is never touched: nothing other players or mods can see changes.
internal sealed class PhantomHitSymptom : Symptom
{
    private const int LowestShownHealth = 26;
    private const int SmallestHit = 7;                  // the smallest hit in Hits (a puma's scratch)
    private const int BigShakeFrom = 20;                // hits this big shake the camera hard
    private const float RealDamageQuietSeconds = 30f;   // no real damage for this long
    private const float HighFear = 0.4f;                // the game's own "high fear" line
    private const float HitFear = 0.45f;                // the spike when the hit lands
    private const float CauseLead = 0.5f;               // the cause sound comes this long before the hit
    private const float CauseMinDistance = 2f;
    private const float CauseMaxDistance = 3.5f;
    private const float FlinchSyncHold = 1.2f;          // your animation updates to others wait this long after a flinch
    private const float HoldSeconds = 10f;              // shown as lost this long after the last phantom hit or real damage
    private const float CreepSeconds = 0.2f;            // then 1 HP back this often: 5x the game's own regen (1 HP/s)

    // What a phantom hit can imitate when there's no attack sound to name it: a person (the shovel's hit).
    internal const string Person = "a person";

    // Each one's real hit in the game (its damage). Monsters not listed only kill, or hit for more than can be shown
    // (bunker spider and coilhead, 90): they don't fake hits.
    private static readonly Dictionary<string, int> Hits = new Dictionary<string, int>
    {
        ["Hoarding bug"] = 30,
        ["Thumper"] = 40,
        ["Hygrodere"] = 35,
        ["Baboon hawk"] = 20,
        ["Snare flea"] = 10,
        ["Butler"] = 10,
        ["Giant sapsucker"] = 10,
        ["Puma"] = 7,
        [Person] = 20,
    };

    private static bool playing;

    // The one instance the director holds (the static coroutine reports chains through it).
    private static PhantomHitSymptom? instance;

    internal PhantomHitSymptom()
    {
        instance = this;
    }

    // The health taken on screen right now (shown = real - lost), when the last fake hit landed, and the creep's clock.
    private static int lost;
    private static float lastHitAt = -9999f;
    private static float nextCreep;
    private static int lostAtPeak;

    // How much is shown for the local player right now: real health minus the lost amount (never below 26 unless the
    // real health is lower, never above the real health). Used by the HUD hook and the HUD mods' swap.
    internal static int Shown(int real)
    {
        if (lost <= 0)
        {
            return real;
        }
        if (real - lost < LowestShownHealth)
        {
            lost = Mathf.Max(0, real - LowestShownHealth);   // the lie shrinks rather than going below 26
        }
        return real - lost;
    }

    // HUD mods that read health straight from the player are shown the lie through this (Compat/ShownHealthSwap).
    internal static bool TryGetShownHealth(PlayerControllerB player, out int shown)
    {
        shown = player.health;
        GameNetworkManager networkManager = GameNetworkManager.Instance;
        if (lost <= 0 || player.isPlayerDead || networkManager == null || player != networkManager.localPlayerController)
        {
            return false;
        }
        shown = Shown(player.health);
        return shown != player.health;
    }

    internal static int Lost => lost;

    // While true, the game's per-frame sync of your body animation to other players waits (PlayerControllerBPatch),
    // so the fake flinch stays on your screen. Ordinary updates resume right after (a delay of about a second).
    internal static bool HoldAnimationSync => Time.time < flinchUntil;
    private static float flinchUntil = -9999f;

    // For the debug menu: the lie right now.
    internal static string DebugState => lost <= 0 ? "" : playing ? Invariant($"{lost} HP shown as lost (hit landing)")
        : Invariant($"{lost} HP shown as lost, {(Mathf.Min(Time.time - lastHitAt, SanityMeter.SecondsSinceDamage) < HoldSeconds ? "holding" : "creeping back")}");

    internal override string Name => "Phantom hit";

    internal override string ConfigKey => "PhantomHit";

    internal override string Family => "body";

    internal override SanityTier MinTier => SanityTier.Disturbed;

    internal override float BaseWeight => 0.8f;

    internal override float RecoverySeconds => 180f;

    // Needs: room above 26 on the shown health, no real damage for 30 s, fear below 0.4, not already running.
    internal override string CantPlayReason(PlayerControllerB player)
    {
        if (StartOfRound.Instance == null || HUDManager.Instance == null)
        {
            return "no round";
        }
        if (playing)
        {
            return "already running";
        }
        if (player.isPlayerDead)
        {
            return "dead";
        }
        int shown = Shown(player.health);
        if (shown - LowestShownHealth < SmallestHit)
        {
            return Invariant($"health: {shown} shown (needs {LowestShownHealth + SmallestHit} or more)");
        }
        if (SanityMeter.SecondsSinceDamage < RealDamageQuietSeconds)
        {
            return Invariant($"recent damage: {SanityMeter.SecondsSinceDamage:F0} s ago (needs {RealDamageQuietSeconds:F0})");
        }
        if (StartOfRound.Instance.fearLevel >= HighFear)
        {
            return Invariant($"fear: {StartOfRound.Instance.fearLevel:F2} (needs below {HighFear})");
        }
        return "";
    }

    internal override bool TryPlay(PlayerControllerB player, SanityTier tier)
    {
        return Start(player, tier, withCause: true, why: "");
    }

    // Also used by chains and the Presence, without a cause sound: "imitating" names what hits you (the Presence's
    // mask, or a person after a rush of footsteps).
    internal bool Start(PlayerControllerB player, SanityTier tier, bool withCause, string why, string imitating = Person)
    {
        HUDManager hud = HUDManager.Instance;
        if (hud == null || playing || player.isPlayerDead)
        {
            LastFailure = "HUD not ready or a phantom hit is already running";
            return false;
        }

        int before = Shown(player.health);
        int room = before - LowestShownHealth;
        string who = imitating;
        if (!withCause && (!Hits.TryGetValue(who, out int known) || known > room))
        {
            LastFailure = Hits.ContainsKey(who) ? $"health too low for {who}'s hit" : $"{who} doesn't hit without killing";
            return false;
        }

        // The attack sound picks the monster (only ones whose hit fits); with no sound, it's a person's hit.
        string cause = why;
        if (withCause)
        {
            cause = PlayCause(player, room, out string monster);
            who = monster.Length > 0 ? monster : Person;
        }
        if (!Hits.TryGetValue(who, out int damage) || damage > room)
        {
            LastFailure = $"health too low for {who}'s hit";
            return false;
        }
        playing = true;
        Telemetry.Event("PHANTOM_HIT", damage, Invariant(
            $"as {who} hit {damage} (shown {before} -> {before - damage}, real {player.health}) cause {cause}"));
        DayStats.NoteHit(who, damage);
        if (!PhantomAudio.Run(Run(player, hud, damage, tier == SanityTier.Breaking, withCause ? CauseLead : 0.1f)))
        {
            playing = false;
            LastFailure = "nothing to run it on";
            return false;
        }
        return true;
    }

    internal override void ResetDay()
    {
        lost = 0;
        lostAtPeak = 0;
    }

    // After the hold, the shown health creeps back 5 HP a second (5x the game's regen, its own UI update, no hit
    // animation). Real damage restarts the hold. Not while a hit is still landing.
    internal override void Watch(PlayerControllerB player)
    {
        if (lost <= 0 || playing)
        {
            return;
        }
        HUDManager hud = HUDManager.Instance;
        if (hud == null || player.isPlayerDead)
        {
            return;
        }
        float quiet = Mathf.Min(Time.time - lastHitAt, SanityMeter.SecondsSinceDamage);
        if (quiet < HoldSeconds)
        {
            nextCreep = Time.time + CreepSeconds;
            return;
        }
        if (Time.time < nextCreep)
        {
            return;
        }
        nextCreep = Time.time + CreepSeconds;
        lost--;
        hud.UpdateHealthUI(player.health, hurtPlayer: false);   // the HUD hook shows real - lost (the truth at 0)
        if (lost <= 0)
        {
            Telemetry.Event("PHANTOM_HIT_HEALED", lostAtPeak, Invariant($"{lostAtPeak} crept back; health shown {player.health} (real)"));
            lostAtPeak = 0;
        }
    }

    // Death or a revive: the lie is over (the game shows the truth from then on).
    internal static void ClearLost()
    {
        lost = 0;
        lostAtPeak = 0;
    }

    // An attack sound behind you just before the hit, from a monster that fits here (the Attackers catalog) and whose
    // hit fits the room left on the shown health. monster = its name ("" if no sound played).
    private static string PlayCause(PlayerControllerB player, int room, out string monster)
    {
        monster = "";
        if (PhantomAudio.IsPlaying)
        {
            return "none (another phantom sound playing)";
        }
        bool played = PhantomSoundSymptom.PlayCloseOneShot(player, CauseMinDistance, CauseMaxDistance, out string what, out _,
            out string name, candidate => Hits.TryGetValue(candidate, out int h) && h <= room);
        if (played)
        {
            monster = name;
        }
        return played ? what : "none";
    }

    private static IEnumerator Run(PlayerControllerB player, HUDManager hud, int damage, bool breaking, float lead)
    {
        int real = player.health;
        string ended = "landed";
        try
        {
            for (float t = 0f; t < lead; t += Time.deltaTime)
            {
                yield return null;
            }
            if (Interrupted(player, real))
            {
                ended = "stopped by real damage, death or leaving";
                yield break;
            }

            lost += damage;
            lostAtPeak = Mathf.Max(lostAtPeak, lost);
            lastHitAt = Time.time;
            hud.SetCracksOnVisor(player.health);           // the HUD hook shows real - lost
            hud.UpdateHealthUI(player.health, hurtPlayer: true);
            Compat.LethalHudCompat.OnFakeHit(player);
            StartOfRound startOfRound = StartOfRound.Instance;
            if (startOfRound != null && startOfRound.damageSFX != null)
            {
                hud.UIAudio.PlayOneShot(startOfRound.damageSFX, 1f);
            }
            hud.ShakeCamera(damage >= BigShakeFrom ? ScreenShakeType.Big : ScreenShakeType.Small);
            Flinch(player, damage);
            FearSpike(player);

            if (breaking)
            {
                SanityDirector.Peak("phantom hit");
            }
            if (instance != null)
            {
                Chains.Trigger(instance, "landed");
            }
        }
        finally
        {
            playing = false;
            Telemetry.Event("PHANTOM_HIT_END", lost, Invariant($"{ended}; health shown {Shown(player != null ? player.health : 0)} real {(player != null ? player.health : 0)}"));
        }
    }

    // What the game's own DamagePlayer does to you locally after a hit: the body flinch (the "Damage" animation and the
    // quick special-animation blend your arms show) and the adrenaline kick (a hit of 10 or more gives back stamina:
    // damage / 125 of the sprint meter). Teammates would see the flinch through the animation sync, so that waits
    // (HoldAnimationSync); nothing else is sent.
    private static void Flinch(PlayerControllerB player, int damage)
    {
        if (damage >= 10)
        {
            player.sprintMeter = Mathf.Clamp(player.sprintMeter + damage / 125f, 0f, 1f);
        }
        if (player.playerBodyAnimator == null)
        {
            return;
        }
        flinchUntil = Time.time + FlinchSyncHold;
        if (!player.inSpecialInteractAnimation && !player.twoHandedAnimation)
        {
            player.playerBodyAnimator.SetTrigger("Damage");
        }
        player.specialAnimationWeight = 1f;
        player.PlayQuickSpecialAnimation(0.7f);
    }

    // The hit comes with a spike like a real attack's (tagged as ours: no sanity drop for it).
    private static void FearSpike(PlayerControllerB player)
    {
        StartOfRound startOfRound = StartOfRound.Instance;
        if (startOfRound == null || startOfRound.fearLevel >= HitFear)
        {
            return;
        }
        SanityMeter.PhantomJump = true;
        try
        {
            player.JumpToFearLevel(HitFear);
        }
        finally
        {
            SanityMeter.PhantomJump = false;
        }
        SanityMeter.NotePhantomJump();
    }

    private static bool Interrupted(PlayerControllerB player, int real)
    {
        return player == null || player.isPlayerDead || player.health < real || !SanityMeter.OnMoon;
    }
}
