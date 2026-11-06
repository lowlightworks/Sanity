using System.Collections.Generic;
using GameNetcodeStuff;
using UnityEngine;
using UnityEngine.AI;
using static System.FormattableString;

namespace Sanity.Symptoms;

// A monster sound with nothing there.
// WHICH monster: only ones that fit this moon and where you are (inside or outside), weighted toward ones
//   actually alive right now, and most toward ones near you (misdirection: the sound comes from a clearly
//   different direction than the real one).
// WHERE: behind or beside you (never where you're looking), on walkable floor at about your height.
//   Stricter up close: nearby sounds must be on your floor; distant ones get more leeway.
// HOW FAR: by tier. At Breaking, rhythmic sounds (footsteps, chitter, breathing) approach you.
// WHEN: an outdoor night monster that isn't actually out yet is only believable late in the day, so before
//   Sundown its sound usually doesn't pass (skipped only when the debug menu ignores conditions).
internal sealed class PhantomSoundSymptom : Symptom
{
    private const float NearbyRadius = 25f;             // a real monster this close counts as "nearby"
    private const float MisdirectionMinAngle = 90f;     // keep at least this far from the real one's direction
    private const int PlacementAttempts = 16;
    private const float SoundHeight = 0.8f;             // roughly where a monster's sound comes from, above the floor

    // Outdoor night monsters (the moon's outside list) that aren't alive: chance their sound is believable.
    private const float DaytimePlausibility = 0.3f;     // until mid-afternoon (about 1 round in 5 per player)
    private const float PlausibilityRampStart = 0.5f;   // day fraction where it starts rising
    private const float PlausibilityFullAt = 0.63f;     // the game's Sundown: from here, fully believable

    private readonly List<Candidate> candidates = new List<Candidate>();

    // The one instance the director holds, for other symptoms that borrow its catalog (phantom hit's cause sound).
    private static PhantomSoundSymptom? instance;

    internal PhantomSoundSymptom()
    {
        instance = this;
    }

    // The Presence's mask: a monster that fits here (same rules and weights as a phantom sound), and its sound to use
    // as a voice (a one-shot if it has one). False if nothing fits.
    internal static bool PickMask(PlayerControllerB player, out MonsterEntry entry, out EnemyAI example, out MonsterSound voice,
                                  out AudioClip[] clips)
    {
        entry = null!;
        example = null!;
        voice = null!;
        clips = null!;
        PhantomSoundSymptom? self = instance;
        StartOfRound startOfRound = StartOfRound.Instance;
        if (self == null || startOfRound == null || startOfRound.currentLevel == null || RoundManager.Instance == null)
        {
            return false;
        }
        self.BuildCandidates(player, player.isInsideFactory);
        self.candidates.RemoveAll(c => NotAMask(c.Entry.Name));
        if (self.candidates.Count == 0)
        {
            return false;
        }
        foreach (Candidate candidate in self.candidates)
        {
            if (PhantomStepsSymptom.FootstepsOf(candidate.Entry) != null)
            {
                candidate.Weight *= 3f;   // a mask you can hear walking is a better presence than one that only calls out
            }
        }
        Candidate chosen = self.PickCandidate();
        foreach (MonsterSound option in chosen.Entry.Sounds)
        {
            AudioClip[]? optionClips = option.Clips(chosen.Example);
            if (optionClips != null && optionClips.Length > 0 && (voice == null || (voice.Rhythmic && !option.Rhythmic)))
            {
                voice = option;
                clips = optionClips;
            }
        }
        if (voice == null)
        {
            return false;
        }
        entry = chosen.Entry;
        example = chosen.Example;
        return true;
    }

    // Monsters that make no sense as something following you around: the Hygrodere (not threatening), the snare flea
    // (waits on the ceiling) and the Nutcracker (its only sound here is a torso turn).
    private static bool NotAMask(string name)
    {
        return name == "Hygrodere" || name == "Snare flea" || name == "Nutcracker";
    }

    // A single close sound behind you, phantom hit's "something got you": an attack sound from a monster that attacks
    // without killing outright (MonsterSoundCatalog.Attackers) and fits here (this moon's spawn lists, inside/outside,
    // weighted like a phantom sound; a monster only on the night list, not alive, passes the same time-of-day odds).
    internal static bool PlayCloseOneShot(PlayerControllerB player, float minDistance, float maxDistance, out string what,
                                          out EnemyAI? attacker)
    {
        what = "none";
        attacker = null;
        PhantomSoundSymptom? self = instance;
        StartOfRound startOfRound = StartOfRound.Instance;
        if (self == null || startOfRound == null || startOfRound.currentLevel == null || RoundManager.Instance == null
            || PhantomAudio.IsPlaying)
        {
            return false;
        }

        bool inside = player.isInsideFactory;
        self.BuildCandidates(player, inside, MonsterSoundCatalog.Attackers);
        self.candidates.RemoveAll(c => Random.value > Plausibility(c));
        if (self.candidates.Count == 0)
        {
            return false;
        }
        Candidate chosen = self.PickCandidate();
        MonsterSound? sound = null;
        AudioClip[]? clips = null;
        foreach (MonsterSound option in chosen.Entry.Sounds)
        {
            AudioClip[]? optionClips = option.Clips(chosen.Example);
            if (optionClips != null && optionClips.Length > 0 && (sound == null || (sound.Rhythmic && !option.Rhythmic)))
            {
                sound = option;
                clips = optionClips;
            }
        }
        if (sound == null || clips == null)
        {
            return false;
        }

        Vector3 origin = player.transform.position;
        Vector3 forward = PhantomPlacement.LookDirection(player);
        for (int attempt = 0; attempt < PlacementAttempts; attempt++)
        {
            Vector3 direction = PhantomPlacement.RandomBehindDirection(forward);
            float distance = Random.Range(minDistance, maxDistance);
            if (!TrySample(origin, origin + direction * distance, 2f, minDistance * 0.6f, out Vector3 point))
            {
                continue;
            }
            AudioSource? template = sound.Source(chosen.Example) ?? chosen.Example.creatureVoice;
            if (!PhantomAudio.Play(player, sound, clips, template, new[] { point }))
            {
                return false;
            }
            what = Invariant($"{chosen.Entry.Name} {sound.Name} {distance:F1}m {PhantomPlacement.Side(player, point)}");
            attacker = chosen.Example;
            return true;
        }
        return false;
    }

    internal override string Name => "Phantom sound";

    internal override string ConfigKey => "PhantomSound";

    internal override bool HoldsForSprint => true;

    internal override string Family => "sound";

    internal override SanityTier MinTier => SanityTier.Uneasy;

    internal override float BaseWeight => 0.6f;

    internal override float RecoverySeconds => 90f;

    // Needs: not in the ship with the doors closed, no phantom sound already playing, and a monster that fits this
    // moon and where you are (inside or outside). The time-of-day check and finding a free spot happen in TryPlay.
    internal override string CantPlayReason(PlayerControllerB player)
    {
        StartOfRound startOfRound = StartOfRound.Instance;
        if (startOfRound == null || startOfRound.currentLevel == null || RoundManager.Instance == null)
        {
            return "no round";
        }
        if (PhantomAudio.IsPlaying)
        {
            return "already playing";
        }
        if (player.isSprinting && !SanityDirector.SprintCheckOff)
        {
            return SanityDirector.SprintingReason;   // your own footsteps drown it out (the director may hold it)
        }
        if (player.isInHangarShipRoom && startOfRound.hangarDoorsClosed)
        {
            return "ship doors closed";
        }

        // Looking only: TryPlay builds this list again before it picks.
        bool inside = player.isInsideFactory;
        BuildCandidates(player, inside);
        if (candidates.Count == 0)
        {
            return inside ? "no monster: none fits inside on this moon" : "no monster: none fits outside on this moon";
        }
        return "";
    }

    internal override bool TryPlay(PlayerControllerB player, SanityTier tier)
    {
        // Normally guaranteed by CanPlay, but the debug menu can skip CanPlay.
        StartOfRound startOfRound = StartOfRound.Instance;
        if (startOfRound == null || startOfRound.currentLevel == null || RoundManager.Instance == null)
        {
            LastFailure = "not in a round";
            return false;
        }

        bool inside = player.isInsideFactory;
        BuildCandidates(player, inside);
        if (candidates.Count == 0)
        {
            LastFailure = inside ? "no fitting inside monster on this moon" : "no fitting outside monster on this moon";
            return false;
        }

        Candidate chosen = PickCandidate();

        // A night monster that isn't out yet: usually not believable this early in the day.
        float plausibility = Plausibility(chosen);
        if (!SanityDirector.ConditionsIgnored && Random.value >= plausibility)
        {
            LastFailure = Invariant($"{chosen.Entry.Name} isn't believable at this time of day (passes {plausibility * 100f:F0}% of the time)");
            return false;
        }

        // Pick one of its sounds that actually has clips.
        MonsterSound? sound = null;
        AudioClip[]? clips = null;
        int start = Random.Range(0, chosen.Entry.Sounds.Length);
        for (int i = 0; i < chosen.Entry.Sounds.Length && sound == null; i++)
        {
            MonsterSound option = chosen.Entry.Sounds[(start + i) % chosen.Entry.Sounds.Length];
            AudioClip[]? optionClips = option.Clips(chosen.Example);
            if (optionClips != null && optionClips.Length > 0)
            {
                sound = option;
                clips = optionClips;
            }
        }
        if (sound == null || clips == null)
        {
            LastFailure = $"{chosen.Entry.Name} has no usable sounds";
            return false;
        }

        AudioSource? template = sound.Source(chosen.Example);
        if (template == null)
        {
            template = chosen.Example.creatureVoice;
        }

        bool approaching = tier == SanityTier.Breaking && sound.Rhythmic;
        int count = sound.Rhythmic ? Random.Range(sound.MinCount, sound.MaxCount + 1) : 1;
        Vector3[]? positions = Place(player, inside, tier, approaching, count, chosen.Nearby);
        if (positions == null)
        {
            LastFailure = "no free spot behind or beside you";
            return false;
        }

        if (!PhantomAudio.Play(player, sound, clips, template, positions))
        {
            LastFailure = "a phantom sound is already playing";
            return false;
        }

        float distance = Vector3.Distance(player.transform.position, positions[0]);
        string detail = Invariant($"{chosen.Entry.Name} {sound.Name} x{count}{(approaching ? " approaching" : "")} ")
            + Invariant($"{distance:F0}m {PhantomPlacement.Side(player, positions[0])} ({chosen.Origin})");
        Telemetry.Event("PHANTOM_SOUND", distance, detail);
        LastSource = positions[0];
        if (approaching)
        {
            Chains.Trigger(this, "approaching");
        }
        return true;
    }

    // ---- Which monster ----

    private sealed class Candidate
    {
        internal MonsterEntry Entry = null!;
        internal EnemyAI Example = null!;    // a live one if there is one, otherwise the moon's prefab
        internal EnemyAI? Nearby;            // the closest real one within NearbyRadius, if any
        internal float Weight;
        internal string Origin = "";
        internal bool NightOnly;             // only from the moon's outside (night) list, and not alive
    }

    private void BuildCandidates(PlayerControllerB player, bool inside, MonsterEntry[]? catalog = null)
    {
        candidates.Clear();
        MonsterEntry[] list = catalog ?? MonsterSoundCatalog.All;

        // Could spawn on this moon, in this area (weight 1).
        SelectableLevel level = StartOfRound.Instance.currentLevel;
        if (inside)
        {
            AddSpawnable(level.Enemies, nightList: false, list);
        }
        else
        {
            AddSpawnable(level.OutsideEnemies, nightList: true, list);
            AddSpawnable(level.DaytimeEnemies, nightList: false, list);
        }

        // Actually alive in this area (weight 2), or alive and near you (weight 4).
        foreach (EnemyAI enemy in RoundManager.Instance.SpawnedEnemies)
        {
            if (enemy == null || enemy.isEnemyDead || enemy.isOutside == inside)
            {
                continue;
            }

            MonsterEntry? entry = MonsterSoundCatalog.Find(list, enemy);
            if (entry == null)
            {
                continue;
            }

            Candidate candidate = GetOrAdd(entry, enemy);
            candidate.Example = enemy;
            candidate.NightOnly = false;   // it's really out there, so it's believable any time
            float distance = Vector3.Distance(enemy.transform.position, player.transform.position);
            if (distance <= NearbyRadius)
            {
                if (candidate.Nearby == null
                    || distance < Vector3.Distance(candidate.Nearby.transform.position, player.transform.position))
                {
                    candidate.Nearby = enemy;
                }
                candidate.Weight = 4f;
                candidate.Origin = "copying a real one nearby";
            }
            else if (candidate.Weight < 2f)
            {
                candidate.Weight = 2f;
                candidate.Origin = "one is alive elsewhere";
            }
        }

        // An echo: a real one scared you a little while ago, here (see NoteEcho).
        if (echoEntry != null && echoInside == inside && Time.time - echoTime >= EchoMinSeconds && Time.time - echoTime <= EchoMaxSeconds)
        {
            foreach (Candidate candidate in candidates)
            {
                if (candidate.Entry == echoEntry)
                {
                    candidate.Weight *= EchoWeight;
                    candidate.Origin = "echo of a real scare";
                }
            }
        }
    }

    // Echoes (a firing layer): a real scare (fear 0.4+) from a monster near you can come back 1-5 minutes later as a
    // phantom sound of the same monster (weight x5 while it lasts). Uses your own history, so it can't be predicted.
    private const float EchoMinSeconds = 60f;
    private const float EchoMaxSeconds = 300f;
    private const float EchoWeight = 5f;
    private const float EchoRadius = 30f;
    private static MonsterEntry? echoEntry;
    private static float echoTime = -9999f;
    private static bool echoInside;

    internal static void NoteEcho(PlayerControllerB player)
    {
        RoundManager roundManager = RoundManager.Instance;
        if (roundManager == null || roundManager.SpawnedEnemies == null)
        {
            return;
        }
        EnemyAI? nearest = null;
        float best = EchoRadius;
        foreach (EnemyAI enemy in roundManager.SpawnedEnemies)
        {
            if (enemy == null || enemy.isEnemyDead || MonsterSoundCatalog.Find(enemy) == null)
            {
                continue;
            }
            float distance = Vector3.Distance(enemy.transform.position, player.transform.position);
            if (distance < best)
            {
                best = distance;
                nearest = enemy;
            }
        }
        if (nearest == null)
        {
            return;
        }
        echoEntry = MonsterSoundCatalog.Find(nearest);
        echoTime = Time.time;
        echoInside = player.isInsideFactory;
        Telemetry.Event("ECHO", best, $"real scare near a {echoEntry?.Name}: its sound may come back in 1-5 min");
    }

    private void AddSpawnable(List<SpawnableEnemyWithRarity> list, bool nightList, MonsterEntry[] catalog)
    {
        if (list == null)
        {
            return;
        }

        foreach (SpawnableEnemyWithRarity spawnable in list)
        {
            if (spawnable == null || spawnable.rarity <= 0 || spawnable.enemyType == null
                || spawnable.enemyType.enemyPrefab == null)
            {
                continue;
            }

            EnemyAI prefab = spawnable.enemyType.enemyPrefab.GetComponent<EnemyAI>();
            if (prefab == null)
            {
                continue;
            }

            MonsterEntry? entry = MonsterSoundCatalog.Find(catalog, prefab);
            if (entry == null)
            {
                continue;
            }

            Candidate candidate = GetOrAdd(entry, prefab);
            if (candidate.Weight < 1f)
            {
                candidate.Weight = 1f;
                candidate.Origin = "could spawn here";
                candidate.NightOnly = nightList;
            }
            else if (!nightList)
            {
                candidate.NightOnly = false;   // also on a daytime or inside list
            }
        }
    }

    private Candidate GetOrAdd(MonsterEntry entry, EnemyAI example)
    {
        foreach (Candidate existing in candidates)
        {
            if (existing.Entry == entry)
            {
                return existing;
            }
        }

        Candidate candidate = new Candidate { Entry = entry, Example = example };
        candidates.Add(candidate);
        return candidate;
    }

    // 1 = always believable. Night-only monsters: DaytimePlausibility until mid-afternoon, rising to 1 at Sundown.
    private static float Plausibility(Candidate candidate)
    {
        if (!candidate.NightOnly)
        {
            return 1f;
        }

        float rise = Mathf.InverseLerp(PlausibilityRampStart, PlausibilityFullAt, SanityMeter.DayFraction);
        return Mathf.Lerp(DaytimePlausibility, 1f, rise);
    }

    private Candidate PickCandidate()
    {
        float total = 0f;
        foreach (Candidate candidate in candidates)
        {
            total += candidate.Weight;
        }

        float roll = Random.value * total;
        foreach (Candidate candidate in candidates)
        {
            roll -= candidate.Weight;
            if (roll <= 0f)
            {
                return candidate;
            }
        }
        return candidates[candidates.Count - 1];
    }

    // ---- Where ----

    private static Vector3[]? Place(PlayerControllerB player, bool inside, SanityTier tier, bool approaching,
                                    int count, EnemyAI? nearby)
    {
        Vector3 origin = player.transform.position;
        Vector3 forward = PhantomPlacement.LookDirection(player);

        // Misdirection: if a real one is near, keep clear of its direction.
        Vector3 avoid = nearby != null ? PhantomPlacement.Flat(nearby.transform.position - origin) : Vector3.zero;

        // Stricter up close: nearby sounds must be on your floor.
        float heightTolerance = tier == SanityTier.Breaking ? (inside ? 2f : 4f) : (inside ? 3.5f : 8f);

        GetDistances(tier, out float minDistance, out float maxDistance);

        for (int attempt = 0; attempt < PlacementAttempts; attempt++)
        {
            Vector3 direction = PhantomPlacement.RandomBehindDirection(forward);
            if (avoid != Vector3.zero && Vector3.Angle(direction, avoid) < MisdirectionMinAngle)
            {
                continue;
            }

            if (!approaching)
            {
                float distance = Random.Range(minDistance, maxDistance);
                if (TrySample(origin, origin + direction * distance, heightTolerance, minDistance * 0.7f, out Vector3 point))
                {
                    Vector3[] same = new Vector3[count];
                    for (int i = 0; i < count; i++)
                    {
                        same[i] = point;
                    }
                    return same;
                }
                continue;
            }

            // Approaching: from about 9 m in to about 4 m, each repeat a bit closer.
            if (!TrySample(origin, origin + direction * Random.Range(8f, 10f), heightTolerance, 6f, out Vector3 from)
                || !TrySample(origin, origin + direction * Random.Range(3f, 4.5f), heightTolerance, 2.5f, out Vector3 to))
            {
                continue;
            }

            Vector3[] path = new Vector3[count];
            Vector3 previous = from;
            for (int i = 0; i < count; i++)
            {
                float t = count > 1 ? (float)i / (count - 1) : 1f;
                Vector3 step = Vector3.Lerp(from, to, t);
                previous = NavMesh.SamplePosition(step, out NavMeshHit hit, 1.5f, NavMesh.AllAreas)
                    ? hit.position + Vector3.up * SoundHeight
                    : previous;
                path[i] = previous;
            }
            return path;
        }

        return null;
    }

    private static void GetDistances(SanityTier tier, out float min, out float max)
    {
        if (tier == SanityTier.Breaking)
        {
            min = 3.5f;   // further away is too easy to miss
            max = 7f;
        }
        else if (tier == SanityTier.Disturbed)
        {
            min = 6f;
            max = 11f;
        }
        else
        {
            min = 10f;
            max = 16f;
        }
    }

    // A floor point from the shared placement, lifted to where a monster's sound comes from.
    private static bool TrySample(Vector3 origin, Vector3 point, float heightTolerance, float minFlatDistance,
                                  out Vector3 result)
    {
        bool found = PhantomPlacement.TrySample(origin, point, heightTolerance, minFlatDistance, out Vector3 floor);
        result = found ? floor + Vector3.up * SoundHeight : Vector3.zero;
        return found;
    }
}
