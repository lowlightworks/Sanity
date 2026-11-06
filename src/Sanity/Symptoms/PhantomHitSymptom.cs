using System.Collections;
using GameNetcodeStuff;
using UnityEngine;
using static System.FormattableString;

namespace Sanity.Symptoms;

// Phantom hits. Something hits you: an attack sound behind you (a monster that attacks without killing outright,
// MonsterSoundCatalog.Attackers), then 1-3 hits (2-4 at Breaking), each the game's own small-hit package: the HUD hit
// animation and red tint, the damage sound, visor cracks, a camera shake (a big one on the last hit at Breaking), and
// a fear spike on the first hit. The health it takes is shown as gone (the HUD, and HUD mods that draw health:
// LethalHUD, GeneralImprovements, show your real health minus this lost amount), held for 10 s after the last hit,
// then it creeps back 5 HP a second, five times the game's own regeneration (it must come back, but not so fast it
// looks fake). Real damage stacks on top (real 100, 25 lost: shows 75; a real 20 hit: real 80, shows 55) and restarts
// the 10 s hold; real healing raises the shown value by the same amount. It never shows below 26 (the game's
// critical-injury effects start under 20, and we don't fake those): if real damage would take it there, the lie
// shrinks instead, and at real health under 26 the truth shows. Cleared when you die, leave the moon or are revived.
// Real health is never touched: nothing other players or mods can see changes.
internal sealed class PhantomHitSymptom : Symptom
{
    private const int MinDamage = 8;
    private const int MaxDamage = 15;
    private const int LowestShownHealth = 26;
    private const float MinSpacing = 0.7f;
    private const float MaxSpacing = 1.3f;
    private const float RealDamageQuietSeconds = 30f;   // no real damage for this long
    private const float HighFear = 0.4f;                // the game's own "high fear" line
    private const float HitFear = 0.45f;                // the spike on the first hit
    private const float CauseLead = 0.5f;               // the cause sound comes this long before the first hit
    private const float CauseMinDistance = 2f;
    private const float CauseMaxDistance = 3.5f;
    private const float FlinchSyncHold = 1.2f;          // your animation updates to others wait this long after a flinch
    private const float HoldSeconds = 10f;              // shown as lost this long after the last hit or real damage
    private const float CreepSeconds = 0.2f;            // then 1 HP back this often: 5x the game's own regen (1 HP/s)

    // Chance of 1, 2, 3 hits (Uneasy/Disturbed), and of 2, 3, 4 at Breaking.
    private static readonly float[] HitCountOdds = { 0.3f, 0.45f, 0.25f };

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
    internal static string DebugState => lost <= 0 ? "" : playing ? Invariant($"{lost} HP shown as lost (hits landing)")
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
        if (shown - LowestShownHealth < MinDamage)
        {
            return Invariant($"health: {shown} shown (needs {LowestShownHealth + MinDamage} or more)");
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

    // Also used by chains (a rush that reaches you: the steps were the cause, so no cause sound).
    internal bool Start(PlayerControllerB player, SanityTier tier, bool withCause, string why)
    {
        HUDManager hud = HUDManager.Instance;
        if (hud == null || playing || player.isPlayerDead)
        {
            LastFailure = "HUD not ready or a phantom hit is already running";
            return false;
        }

        int[]? hits = PlanHits(Shown(player.health), tier == SanityTier.Breaking);
        if (hits == null)
        {
            LastFailure = "health too low for a phantom hit";
            return false;
        }

        int total = 0;
        foreach (int hit in hits)
        {
            total += hit;
        }
        int before = Shown(player.health);
        playing = true;
        string cause = withCause ? PlayCause(player) : why;
        Telemetry.Event("PHANTOM_HIT", hits.Length, Invariant(
            $"x{hits.Length} hits {string.Join("/", hits)} = {total} (shown {before} -> {before - total}, real {player.health}) cause {cause}"));
        if (!PhantomAudio.Run(Run(player, hud, hits, tier == SanityTier.Breaking, withCause ? CauseLead : 0.1f)))
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
    // animation). Real damage restarts the hold. Not while a chain of hits is still landing.
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

    // An attack sound behind you just before the first hit, from a monster that fits here (the Attackers catalog).
    private static string PlayCause(PlayerControllerB player)
    {
        if (PhantomAudio.IsPlaying)
        {
            return "none (another phantom sound playing)";
        }
        bool played = PhantomSoundSymptom.PlayCloseOneShot(player, CauseMinDistance, CauseMaxDistance, out string what, out _);
        return played ? what : "none";
    }

    // How many hits and how much each takes, fitted to the room above LowestShownHealth.
    private static int[]? PlanHits(int shown, bool breaking)
    {
        int budget = shown - LowestShownHealth;
        int count = PickCount() + (breaking ? 1 : 0);
        while (count > 0 && count * MinDamage > budget)
        {
            count--;
        }
        if (count == 0)
        {
            return null;
        }

        int[] hits = new int[count];
        int sum = 0;
        for (int i = 0; i < count; i++)
        {
            hits[i] = Random.Range(MinDamage, MaxDamage + 1);
            sum += hits[i];
        }
        for (int i = count - 1; i >= 0 && sum > budget; i--)
        {
            int cut = Mathf.Min(hits[i] - MinDamage, sum - budget);
            hits[i] -= cut;
            sum -= cut;
        }
        return hits;
    }

    private static int PickCount()
    {
        float roll = Random.value;
        for (int i = 0; i < HitCountOdds.Length; i++)
        {
            roll -= HitCountOdds[i];
            if (roll <= 0f)
            {
                return i + 1;
            }
        }
        return 2;
    }

    private static IEnumerator Run(PlayerControllerB player, HUDManager hud, int[] hits, bool breaking, float lead)
    {
        int real = player.health;
        string ended = "landed";
        try
        {
            for (float t = 0f; t < lead; t += Time.deltaTime)
            {
                yield return null;
            }

            for (int i = 0; i < hits.Length; i++)
            {
                if (Interrupted(player, real))
                {
                    ended = "stopped by real damage, death or leaving";
                    yield break;
                }

                lost += hits[i];
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
                hud.ShakeCamera(breaking && i == hits.Length - 1 ? ScreenShakeType.Big : ScreenShakeType.Small);
                Flinch(player, hits[i]);
                if (i == 0)
                {
                    FearSpike(player);
                }

                float gap = i < hits.Length - 1 ? Random.Range(MinSpacing, MaxSpacing) : 0f;
                for (float t = 0f; t < gap; t += Time.deltaTime)
                {
                    if (Interrupted(player, real))
                    {
                        ended = "stopped by real damage, death or leaving";
                        yield break;
                    }
                    yield return null;
                }
            }
            if (breaking)
            {
                SanityDirector.Peak("phantom hits");
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

    // The first hit comes with a spike like a real attack's (tagged as ours: no sanity drop for it).
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
