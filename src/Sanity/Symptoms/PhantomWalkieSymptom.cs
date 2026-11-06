using System.Collections;
using System.Collections.Generic;
using GameNetcodeStuff;
using UnityEngine;
using static System.FormattableString;

namespace Sanity.Symptoms;

// Dead air: your walkie clicks on as if a teammate pressed theirs, then nothing (or something), then clicks off.
// The game's own transmission clicks on your walkie's own speaker, and relayed sounds on its relay speaker at the
// game's relay volume, exactly as when a teammate presses their button (WalkieTalkie.cs: the start/stop clicks and
// TransmitOneShotAudio). Only on this computer: nothing is sent, nothing is relayed on, no noise monsters can hear.
//   Uneasy:    click on, 0.6-1.8 s of nothing, click off (an accidental press).
//   Disturbed: click on, 2-4 s of something faint, click off: the implied sender's own footsteps (on the floor they're
//              really standing on), or a soft monster sound (footsteps, chitter, breathing) that fits where they are.
//   Breaking:  click on, a monster sound up close, cut off mid-sound by the click off. Or, 1 time in 4 and at most
//              once a day, the static the game plays when a teammate dies while transmitting.
// Needs a powered walkie on you (any slot, held or pocketed), another living player carrying a powered walkie who
// isn't in plain view (within 15 m with nothing in between: you'd see them not pressing), and nobody transmitting
// (you included). Independent of the phantom sound slot. Stops at once if your walkie goes off or leaves your
// inventory, a real transmission starts, you die or leave.
internal sealed class PhantomWalkieSymptom : Symptom
{
    private const float SenderViewRadius = 15f;

    // The game's click: WalkieTalkie plays it through RoundManager.PlayRandomClip (pitch and volume spread).
    private const float ClickMinPitch = 0.94f;
    private const float ClickMaxPitch = 1.06f;
    private const float ClickMinVolume = 0.82f;
    private const float ClickMaxVolume = 1f;

    // The game's relay: volume = maxVolume faded by distance over (recordingRange + 3).
    private const float RelayMaxVolume = 0.6f;
    private const float RelayRange = 6f;

    private const float UneasyMinSilence = 0.6f;
    private const float UneasyMaxSilence = 1.8f;
    private const float LeadMin = 0.3f;              // after the click, before anything comes through
    private const float LeadMax = 0.7f;
    private const float DisturbedMinContent = 2f;
    private const float DisturbedMaxContent = 4f;
    private const float WalkInterval = 0.5f;
    private const float WalkJitter = 0.04f;
    private const float StepVolume = 0.6f;           // the game's walking footstep volume
    private const float OwnStepsMinDistance = 0.5f;  // the sender's own feet, from their walkie
    private const float OwnStepsMaxDistance = 1f;
    private const float FaintMinDistance = 4f;       // a monster a few metres from them
    private const float FaintMaxDistance = 6f;
    private const float CloseMinDistance = 1f;       // a monster right next to them
    private const float CloseMaxDistance = 2f;
    private const float CutMinSeconds = 0.6f;        // the click off cuts the monster sound this far in
    private const float CutMaxSeconds = 1.5f;
    private const float DeathStaticChance = 0.25f;
    private const float DeathStaticMinWait = 0.8f;
    private const float DeathStaticMaxWait = 2.5f;
    private const float AfterStaticMin = 0.15f;      // the game's release click comes a moment after the static
    private const float AfterStaticMax = 0.35f;
    private const float SundownStart = 0.63f;        // the game's Sundown: night monsters are out from here
    private const float ReplyWatchSeconds = 20f;     // telemetry: did you answer on your walkie within this long

    private static bool playing;

    // The one instance the director holds (the static coroutine reports chains through it).
    private static PhantomWalkieSymptom? instance;

    internal PhantomWalkieSymptom()
    {
        instance = this;
    }
    private static bool deathStaticUsedToday;

    private readonly List<PlayerControllerB> senders = new List<PlayerControllerB>();
    private readonly List<KeyValuePair<MonsterEntry, EnemyAI>> monsters = new List<KeyValuePair<MonsterEntry, EnemyAI>>();

    private enum Content
    {
        Nothing,
        Steps,
        Faint,
        Close,
        DeathStatic,
    }

    internal override string Name => "Phantom walkie";

    internal override string ConfigKey => "PhantomWalkie";

    internal override string Family => "sound";

    internal override SanityTier MinTier => SanityTier.Uneasy;

    internal override float BaseWeight => 0.8f;

    internal override float RecoverySeconds => 150f;

    internal override string CantPlayReason(PlayerControllerB player)
    {
        StartOfRound startOfRound = StartOfRound.Instance;
        if (startOfRound == null || startOfRound.allPlayerScripts == null)
        {
            return "no round";
        }
        if (playing)
        {
            return "already playing";
        }
        if (OwnWalkie(player, poweredOnly: false) == null)
        {
            return "no walkie";
        }
        if (OwnWalkie(player, poweredOnly: true) == null)
        {
            return "walkie off";
        }
        if (player.speakingToWalkieTalkie)
        {
            return "you're transmitting";
        }
        if (SomeoneTransmitting(startOfRound))
        {
            return "someone's talking";
        }

        int carrying = FindSenders(player, startOfRound);
        if (carrying == 0)
        {
            return "nobody else has a walkie on";
        }
        if (senders.Count == 0)
        {
            return Invariant($"sender in view: all {carrying} in plain view");
        }
        return "";
    }

    internal override bool TryPlay(PlayerControllerB player, SanityTier tier)
    {
        StartOfRound startOfRound = StartOfRound.Instance;
        WalkieTalkie? walkie = OwnWalkie(player, poweredOnly: true);
        if (startOfRound == null || walkie == null || playing)
        {
            LastFailure = "no powered walkie on you, or one is already playing";
            return false;
        }
        if (FindSenders(player, startOfRound) == 0 || senders.Count == 0)
        {
            LastFailure = "nobody out of sight has a walkie on";
            return false;
        }
        if (walkie.startTransmissionSFX == null || walkie.startTransmissionSFX.Length == 0
            || walkie.stopTransmissionSFX == null || walkie.stopTransmissionSFX.Length == 0
            || walkie.thisAudio == null || walkie.target == null)
        {
            LastFailure = "this walkie has no transmission sounds";
            return false;
        }

        PlayerControllerB sender = senders[Random.Range(0, senders.Count)];
        bool senderInside = sender.isInsideFactory;

        // What comes through, by tier.
        Content content = Content.Nothing;
        MonsterSound? sound = null;
        AudioClip[]? clips = null;
        string monsterName = "";
        if (tier == SanityTier.Disturbed)
        {
            content = Random.value < 0.5f ? Content.Steps : Content.Faint;
            if (content == Content.Faint && !TryPickMonster(senderInside, softOnly: true, out monsterName, out sound, out clips))
            {
                content = Content.Steps;   // nothing soft fits where they are: their own footsteps instead
            }
        }
        else if (tier == SanityTier.Breaking)
        {
            bool deathStatic = !deathStaticUsedToday && walkie.playerDieOnWalkieTalkieSFX != null
                               && Random.value < DeathStaticChance;
            if (deathStatic)
            {
                content = Content.DeathStatic;
            }
            else if (TryPickMonster(senderInside, softOnly: false, out monsterName, out sound, out clips))
            {
                content = Content.Close;
            }
            else
            {
                content = Content.Steps;
            }
        }

        if (content == Content.DeathStatic)
        {
            deathStaticUsedToday = true;
        }

        playing = true;
        if (!PhantomAudio.Run(Transmit(player, walkie, sender, content, sound, clips)))
        {
            playing = false;
            LastFailure = "nothing to run it on";
            return false;
        }

        float distance = Vector3.Distance(player.transform.position, sender.transform.position);
        string what = content switch
        {
            Content.Nothing => "click",
            Content.Steps => "footsteps " + SurfaceName(startOfRound, sender.currentFootstepSurfaceIndex),
            Content.Faint => "faint " + monsterName + " " + sound?.Name,
            Content.Close => "close " + monsterName + " " + sound?.Name,
            _ => "death static",
        };
        bool held = player.currentlyHeldObjectServer == walkie;
        Telemetry.Event("PHANTOM_WALKIE", distance, Invariant(
            $"{what} implied sender {sender.playerUsername} {distance:F0}m ({(senderInside ? "inside" : "outside")}) your walkie {(held ? "held" : "pocketed")}"));
        return true;
    }

    internal override void ResetDay()
    {
        deathStaticUsedToday = false;
    }

    // ---- Playing ----

    private static IEnumerator Transmit(PlayerControllerB player, WalkieTalkie walkie, PlayerControllerB sender,
                                        Content content, MonsterSound? sound, AudioClip[]? clips)
    {
        string ended = "";
        bool clickedOff = false;
        float started = Time.time;
        try
        {
            Click(walkie, walkie.startTransmissionSFX);

            // Each "for (t...)" below is a wait that stops early (with the reason in "ended") if the moment is gone.
            float wait = content switch
            {
                Content.Nothing => Random.Range(UneasyMinSilence, UneasyMaxSilence),
                Content.Close => Random.Range(0.2f, 0.5f),
                Content.DeathStatic => Random.Range(DeathStaticMinWait, DeathStaticMaxWait),
                _ => Random.Range(LeadMin, LeadMax),
            };
            for (float t = 0f; t < wait && ended.Length == 0; t += Time.deltaTime)
            {
                yield return null;
                ended = Interrupted(player, walkie);
            }

            if (ended.Length == 0 && (content == Content.Steps || content == Content.Faint))
            {
                float end = Time.time + Random.Range(DisturbedMinContent, DisturbedMaxContent);
                while (ended.Length == 0 && Time.time < end)
                {
                    float next;
                    if (content == Content.Steps)
                    {
                        Relay(walkie, FootstepClip(sender != null ? sender.currentFootstepSurfaceIndex : 0),
                              Random.Range(OwnStepsMinDistance, OwnStepsMaxDistance), StepVolume);
                        next = WalkInterval + Random.Range(-WalkJitter, WalkJitter);
                    }
                    else
                    {
                        AudioClip? clip = clips != null && clips.Length > 0 ? clips[Random.Range(0, clips.Length)] : null;
                        Relay(walkie, clip, Random.Range(FaintMinDistance, FaintMaxDistance),
                              sound != null ? Random.Range(sound.MinVolume, sound.MaxVolume) : 1f);
                        next = sound != null ? Random.Range(sound.MinGap, sound.MaxGap) : 1f;
                        if (sound != null && sound.GapAfterClip && clip != null)
                        {
                            next += clip.length;
                        }
                        next = Mathf.Max(0.12f, next);
                    }

                    wait = Mathf.Min(next, Mathf.Max(0f, end - Time.time));
                    for (float t = 0f; t < wait && ended.Length == 0; t += Time.deltaTime)
                    {
                        yield return null;
                        ended = Interrupted(player, walkie);
                    }
                }
            }
            else if (ended.Length == 0 && content == Content.Close)
            {
                AudioClip? clip = clips != null && clips.Length > 0 ? clips[Random.Range(0, clips.Length)] : null;
                if (clip != null)
                {
                    Relay(walkie, clip, Random.Range(CloseMinDistance, CloseMaxDistance),
                          sound != null ? Random.Range(sound.MinVolume, sound.MaxVolume) : 1f);
                    wait = Mathf.Min(Random.Range(CutMinSeconds, CutMaxSeconds), clip.length);
                    for (float t = 0f; t < wait && ended.Length == 0; t += Time.deltaTime)
                    {
                        yield return null;
                        ended = Interrupted(player, walkie);
                    }
                    if (ended.Length == 0 && walkie.target != null)
                    {
                        walkie.target.Stop();   // cut off mid-sound, as when the sender lets go
                    }
                }
            }
            else if (ended.Length == 0 && content == Content.DeathStatic && walkie.playerDieOnWalkieTalkieSFX != null)
            {
                walkie.thisAudio.PlayOneShot(walkie.playerDieOnWalkieTalkieSFX);
                SanityDirector.Peak("death static");
                if (instance != null)
                {
                    Chains.Trigger(instance, "death static");
                }
                wait = Random.Range(AfterStaticMin, AfterStaticMax);
                for (float t = 0f; t < wait && ended.Length == 0; t += Time.deltaTime)
                {
                    yield return null;
                    ended = Interrupted(player, walkie);
                }
            }

            // The release click, only if the walkie is still on you and on, and nobody really started talking.
            if (ended.Length == 0 && walkie != null)
            {
                Click(walkie, walkie.stopTransmissionSFX);
                clickedOff = true;
                ended = "finished";
            }
        }
        finally
        {
            playing = false;
            float lasted = Time.time - started;
            Telemetry.Event("PHANTOM_WALKIE_END", lasted, Invariant($"{ended}{(clickedOff ? "" : " (no release click)")} after {lasted:F1}s"));
        }

        // Telemetry: did you answer on your own walkie soon after? The clearest sign it was taken for a real press.
        if (ended == "you pressed your walkie")
        {
            Telemetry.Event("PHANTOM_WALKIE_REPLY", 0f, "you pressed your walkie during it");
            yield break;
        }
        float done = Time.time;
        while (Time.time - done < ReplyWatchSeconds)
        {
            if (player == null || player.isPlayerDead || !SanityMeter.OnMoon)
            {
                yield break;
            }
            if (player.speakingToWalkieTalkie)
            {
                float later = Time.time - done;
                Telemetry.Event("PHANTOM_WALKIE_REPLY", later, Invariant($"you pressed your walkie {later:F1}s after"));
                yield break;
            }
            yield return null;
        }
    }

    private static string Interrupted(PlayerControllerB player, WalkieTalkie walkie)
    {
        if (player == null || player.isPlayerDead)
        {
            return "died";
        }
        if (!SanityMeter.OnMoon)
        {
            return "left the moon";
        }
        if (walkie == null || walkie.playerHeldBy != player)
        {
            return "walkie left your inventory";
        }
        if (!walkie.isBeingUsed)
        {
            return "walkie switched off";
        }
        if (player.speakingToWalkieTalkie)
        {
            return "you pressed your walkie";
        }
        StartOfRound startOfRound = StartOfRound.Instance;
        if (startOfRound != null && SomeoneTransmitting(startOfRound))
        {
            return "someone really transmitted";
        }
        return "";
    }

    private static void Click(WalkieTalkie walkie, AudioClip[] clips)
    {
        AudioClip clip = clips[Random.Range(0, clips.Length)];
        if (clip == null || walkie.thisAudio == null)
        {
            return;
        }
        walkie.thisAudio.pitch = Random.Range(ClickMinPitch, ClickMaxPitch);
        walkie.thisAudio.PlayOneShot(clip, Random.Range(ClickMinVolume, ClickMaxVolume));
    }

    // A sound heard through the walkie, at the game's relay volume for a source this far from the sender's walkie.
    private static void Relay(WalkieTalkie walkie, AudioClip? clip, float distance, float volume)
    {
        if (clip == null || walkie.target == null)
        {
            return;
        }
        float relay = Mathf.Lerp(RelayMaxVolume, 0f, distance / (RelayRange + 3f));
        walkie.target.PlayOneShot(clip, relay * volume);
    }

    // ---- Lookups ----

    // A walkie in any of your slots (held or pocketed, modded slots included). Prefers the one in your hand.
    private static WalkieTalkie? OwnWalkie(PlayerControllerB player, bool poweredOnly)
    {
        if (player.currentlyHeldObjectServer is WalkieTalkie held && (!poweredOnly || held.isBeingUsed))
        {
            return held;
        }
        if (player.ItemSlots == null)
        {
            return null;
        }
        foreach (GrabbableObject item in player.ItemSlots)
        {
            if (item is WalkieTalkie walkie && (!poweredOnly || walkie.isBeingUsed))
            {
                return walkie;
            }
        }
        return null;
    }

    private static bool SomeoneTransmitting(StartOfRound startOfRound)
    {
        foreach (PlayerControllerB other in startOfRound.allPlayerScripts)
        {
            if (other != null && other.isPlayerControlled && other.speakingToWalkieTalkie)
            {
                return true;
            }
        }
        return false;
    }

    // Other living players carrying a powered walkie. Returns how many; senders gets the ones not in plain view.
    private int FindSenders(PlayerControllerB player, StartOfRound startOfRound)
    {
        senders.Clear();
        int carrying = 0;
        int walls = startOfRound.collidersAndRoomMask;
        Vector3 eye = player.gameplayCamera.transform.position;
        foreach (WalkieTalkie walkie in WalkieTalkie.allWalkieTalkies)
        {
            PlayerControllerB? other = walkie != null && walkie.isBeingUsed ? walkie.playerHeldBy : null;
            if (other == null || other == player || !other.isPlayerControlled || other.isPlayerDead)
            {
                continue;
            }

            bool counted = false;
            foreach (PlayerControllerB seen in senders)
            {
                counted |= seen == other;
            }
            if (counted)
            {
                continue;
            }

            carrying++;
            Vector3 them = other.transform.position + Vector3.up * 1.4f;
            bool inView = Vector3.Distance(eye, them) <= SenderViewRadius
                          && !Physics.Linecast(eye, them, walls, QueryTriggerInteraction.Ignore);
            if (!inView)
            {
                senders.Add(other);
            }
        }
        return carrying;
    }

    // A monster that fits where the sender is (alive there, or could spawn there; the outside night list only once
    // it's believable), and one of its sounds with clips. softOnly: rhythmic ones only (footsteps, chitter, breathing).
    private bool TryPickMonster(bool inside, bool softOnly, out string name, out MonsterSound? sound, out AudioClip[]? clips)
    {
        name = "";
        sound = null;
        clips = null;
        monsters.Clear();
        StartOfRound startOfRound = StartOfRound.Instance;
        RoundManager roundManager = RoundManager.Instance;
        if (startOfRound == null || startOfRound.currentLevel == null || roundManager == null)
        {
            return false;
        }

        foreach (EnemyAI enemy in roundManager.SpawnedEnemies)
        {
            if (enemy != null && !enemy.isEnemyDead && enemy.isOutside != inside)
            {
                AddMonster(enemy);
            }
        }

        SelectableLevel level = startOfRound.currentLevel;
        if (inside)
        {
            AddSpawnable(level.Enemies);
        }
        else
        {
            AddSpawnable(level.DaytimeEnemies);
            if (SanityMeter.DayFraction >= SundownStart || Weather.NightHeadStart > 0f)
            {
                AddSpawnable(level.OutsideEnemies);
            }
        }

        // Try them in a random order until one has a fitting sound.
        for (int tries = monsters.Count; tries > 0; tries--)
        {
            int pick = Random.Range(0, monsters.Count);
            KeyValuePair<MonsterEntry, EnemyAI> candidate = monsters[pick];
            monsters.RemoveAt(pick);
            foreach (MonsterSound option in candidate.Key.Sounds)
            {
                if (softOnly && !option.Rhythmic)
                {
                    continue;
                }
                AudioClip[]? optionClips = option.Clips(candidate.Value);
                if (optionClips != null && optionClips.Length > 0)
                {
                    name = candidate.Key.Name;
                    sound = option;
                    clips = optionClips;
                    return true;
                }
            }
        }
        return false;
    }

    private void AddSpawnable(List<SpawnableEnemyWithRarity> list)
    {
        if (list == null)
        {
            return;
        }
        foreach (SpawnableEnemyWithRarity spawnable in list)
        {
            if (spawnable == null || spawnable.rarity <= 0 || spawnable.enemyType == null || spawnable.enemyType.enemyPrefab == null)
            {
                continue;
            }
            EnemyAI prefab = spawnable.enemyType.enemyPrefab.GetComponent<EnemyAI>();
            if (prefab != null)
            {
                AddMonster(prefab);
            }
        }
    }

    private void AddMonster(EnemyAI example)
    {
        MonsterEntry? entry = MonsterSoundCatalog.Find(example);
        if (entry == null)
        {
            return;
        }
        foreach (KeyValuePair<MonsterEntry, EnemyAI> existing in monsters)
        {
            if (existing.Key == entry)
            {
                return;   // one entry per monster kind (a live one is added first, so its clips are used)
            }
        }
        monsters.Add(new KeyValuePair<MonsterEntry, EnemyAI>(entry, example));
    }

    private static AudioClip? FootstepClip(int surface)
    {
        StartOfRound startOfRound = StartOfRound.Instance;
        if (startOfRound == null || startOfRound.footstepSurfaces == null || startOfRound.footstepSurfaces.Length == 0)
        {
            return null;
        }
        if (surface < 0 || surface >= startOfRound.footstepSurfaces.Length)
        {
            surface = 0;
        }
        AudioClip[]? clips = startOfRound.footstepSurfaces[surface]?.clips;
        return clips != null && clips.Length > 0 ? clips[Random.Range(0, clips.Length)] : null;
    }

    private static string SurfaceName(StartOfRound startOfRound, int surface)
    {
        return startOfRound.footstepSurfaces != null && surface >= 0 && surface < startOfRound.footstepSurfaces.Length
               && startOfRound.footstepSurfaces[surface] != null
            ? startOfRound.footstepSurfaces[surface].surfaceTag
            : "surface " + surface;
    }
}
