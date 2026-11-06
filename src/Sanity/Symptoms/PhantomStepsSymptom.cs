using System.Collections;
using GameNetcodeStuff;
using UnityEngine;
using UnityEngine.AI;
using static System.FormattableString;

namespace Sanity.Symptoms;

// Footsteps with nobody there. The game's own footstep sounds, played the way it plays a player's (or a Masked's)
// steps: same clips, pitch spread and walking/sprinting volume, never the same clip twice in a row
// (PlayerControllerB.PlayFootstepSound). In a group the first thought is a teammate; then nobody is there.
//   Uneasy    pass:   3-6 walking steps 8-13 m behind or beside you: someone walking past.
//   Disturbed follow: walking steps 6-9 m behind you, on the path you just walked. Each one comes a beat after one of
//                     your own steps (sprint and they sprint), they stop when you stop, and they stop for good once
//                     you look toward them. 6-14 steps. Only starts while you're walking.
//   Breaking  rush:   sprinting steps from 10-12 m closing to 1.5-2.5 m behind you in 2-3 s, then silence.
//                     Looking toward them stops them at once.
// Any mode: looking toward where the steps are ends it (they never keep stepping while you watch the spot).
// Sound: the clips of the floor you're on (follow: the floor you were on at that point of your path). That is the
// game's own choice of surface, so every moon and modded terrain is handled. The audio settings (falloff, mixer,
// muffling) are copied from a real player's footstep source.
// Never in the ship, never with a living teammate within 15 m (their real steps would mix in), never on a ladder or
// in a special animation. Shares phantom sound's one-at-a-time slot. Death, leaving, or going in or out of the
// facility or into the ship ends it at once.
internal sealed class PhantomStepsSymptom : Symptom
{
    private const float TeammateRadius = 15f;

    // The game's own footstep sound.
    private const float WalkVolume = 0.85f;          // louder than the game's 0.6 / 0.9, which is easy to miss
    private const float SprintVolume = 1f;
    private const float MinPitch = 0.93f;
    private const float MaxPitch = 1.07f;
    private const float StepHeight = 0.3f;           // where a footstep sound comes from, above the floor

    // Cadence: a player's own walking and sprinting rhythm.
    private const float WalkInterval = 0.5f;
    private const float WalkJitter = 0.04f;
    private const float SprintInterval = 0.32f;
    private const float SprintJitter = 0.03f;

    // Pass (Uneasy).
    private const int PassMinSteps = 3;
    private const int PassMaxSteps = 6;
    private const float PassMinDistance = 8f;       // further away is too easy to miss
    private const float PassMaxDistance = 13f;
    private const float Stride = 0.75f;              // metres per walking step

    // Follow (Disturbed).
    private const int FollowMinSteps = 6;
    private const int FollowMaxSteps = 14;
    private const float FollowMinDistance = 6f;
    private const float FollowMaxDistance = 9f;
    private const float FollowDrift = 0.3f;          // the distance wanders by up to this much per step
    private const float FollowMinStraight = 4f;      // a path point this close in a straight line (you doubled back) is skipped
    private const float FollowMinLag = 0.24f;        // seconds after your step that theirs lands (drifts per episode;
    private const float FollowMaxLag = 0.42f;        // much less sounds like an echo of your own steps)
    private const float FollowLagJitter = 0.08f;
    private const float FollowLagDrift = 0.03f;      // the lag wanders by up to this much per step
    private const float WalkingRecently = 0.8f;      // you took a step this recently: you're walking
    private const float FollowGiveUpSeconds = 8f;    // you stand still this long: it's over

    // Rush (Breaking).
    private const float RushMinStart = 10f;
    private const float RushMaxStart = 12f;
    private const float RushMinClear = 8f;           // needs at least this much open floor in a straight line
    private const float RushMinEnd = 1.5f;
    private const float RushMaxEnd = 2.5f;
    private const float RushMinSeconds = 2f;
    private const float RushMaxSeconds = 3f;


    // Looking toward them.
    private const float LookAngle = 60f;
    private const float LookSeconds = 0.3f;          // follow and pass; a rush stops the first moment you look

    private const int PlacementAttempts = 12;

    // The path you walked (follow mode): a point every TrailSpacing metres, newest last, with the floor type there.
    private const float TrailSpacing = 0.5f;
    private const int TrailLength = 160;             // 80 m of path
    private const float TrailJump = 4f;              // moved further than this in one frame: a teleport, start over
    private static readonly Vector3[] trailPoints = new Vector3[TrailLength];
    private static readonly int[] trailSurfaces = new int[TrailLength];
    private static int trailCount;
    private static int trailNewest;
    private static bool trailInside;
    private static Vector3 lastPosition;

    // Your own steps (the game's footstep event), for follow mode's timing.
    private static float lastLocalStep = -9999f;
    private static bool following;
    private static float followLag;
    private const int PendingMax = 4;
    private static readonly float[] pendingAt = new float[PendingMax];
    private static readonly bool[] pendingSprint = new bool[PendingMax];
    private static int pendingCount;

    private static int previousClip = -1;

    // The one instance the director holds (chains and the static coroutines report through it).
    private static PhantomStepsSymptom? instance;

    internal PhantomStepsSymptom()
    {
        instance = this;
    }

    private enum Mode
    {
        Pass,
        Follow,
        Rush,
    }

    internal override string Name => "Phantom steps";

    internal override string ConfigKey => "PhantomSteps";

    internal override bool HoldsForSprint => true;

    internal override string Family => "sound";

    internal override SanityTier MinTier => SanityTier.Uneasy;

    internal override float BaseWeight => 0.9f;

    internal override float RecoverySeconds => 120f;

    // Needs: not in the ship, no phantom sound or steps already playing, no living teammate within 15 m, not on a
    // ladder or in a special animation. Finding floor behind you (and, for follow, that you're walking) is TryPlay's.
    internal override string CantPlayReason(PlayerControllerB player)
    {
        StartOfRound startOfRound = StartOfRound.Instance;
        if (startOfRound == null || startOfRound.footstepSurfaces == null || startOfRound.footstepSurfaces.Length == 0)
        {
            return "no round";
        }
        if (player.isInHangarShipRoom)
        {
            return "in ship";
        }
        if (PhantomAudio.IsPlaying)
        {
            return "already playing";
        }
        if (player.isClimbingLadder)
        {
            return "busy: on a ladder";
        }
        if (player.inSpecialInteractAnimation)
        {
            return "busy: in an animation";
        }
        if (player.isSprinting && !SanityDirector.SprintCheckOff)
        {
            return SanityDirector.SprintingReason;   // your own footsteps drown them out (the director may hold them)
        }

        float teammate = NearestTeammate(player, startOfRound);
        if (teammate >= 0f && teammate <= TeammateRadius)
        {
            return Invariant($"teammate close: {teammate:F0} m (needs over {TeammateRadius:F0})");
        }
        return "";
    }

    internal override bool TryPlay(PlayerControllerB player, SanityTier tier)
    {
        StartOfRound startOfRound = StartOfRound.Instance;
        if (startOfRound == null || startOfRound.footstepSurfaces == null || startOfRound.footstepSurfaces.Length == 0)
        {
            LastFailure = "no footstep sounds";
            return false;
        }
        if (PhantomAudio.IsPlaying)
        {
            LastFailure = "a phantom sound is already playing";
            return false;
        }

        Mode mode = tier == SanityTier.Breaking ? Mode.Rush : tier == SanityTier.Disturbed ? Mode.Follow : Mode.Pass;
        int surface = player.currentFootstepSurfaceIndex;
        Vector3[]? path = null;
        Vector3 rushDirection = Vector3.zero;
        float distance = 0f;
        float rushEnd = 0f;
        float rushSeconds = 0f;
        int steps;
        string detail;

        switch (mode)
        {
            case Mode.Pass:
            {
                steps = Random.Range(PassMinSteps, PassMaxSteps + 1);
                path = PlanPass(player, steps);
                if (path == null)
                {
                    LastFailure = "no floor behind or beside you";
                    return false;
                }
                distance = Vector3.Distance(player.transform.position, path[0]);
                detail = Invariant($"pass x{steps} {SurfaceName(surface)} {distance:F0}m {PhantomPlacement.Side(player, path[0])}");
                break;
            }
            case Mode.Follow:
            {
                if (Time.time - lastLocalStep > WalkingRecently)
                {
                    LastFailure = "you're standing still (they only follow while you walk)";
                    return false;
                }
                distance = Random.Range(FollowMinDistance, FollowMaxDistance);
                if (!TryTrailPoint(player.transform.position, distance, out Vector3 start, out int startSurface)
                    || PhantomPlacement.FlatDistance(player.transform.position, start) < FollowMinStraight)
                {
                    LastFailure = "no path behind you yet (you haven't walked far enough)";
                    return false;
                }
                steps = Random.Range(FollowMinSteps, FollowMaxSteps + 1);
                path = new[] { start + Vector3.up * StepHeight };
                surface = startSurface;
                detail = Invariant($"follow x{steps} {SurfaceName(surface)} {distance:F0}m behind on your path");
                break;
            }
            default:
            {
                if (!PlanRush(player, out rushDirection, out distance))
                {
                    LastFailure = "no open floor behind you";
                    return false;
                }
                rushEnd = Random.Range(RushMinEnd, RushMaxEnd);
                rushSeconds = Random.Range(RushMinSeconds, RushMaxSeconds);
                steps = Mathf.Max(2, Mathf.RoundToInt(rushSeconds / SprintInterval) + 1);
                path = new[] { RushPoint(player, rushDirection, distance) };
                detail = Invariant($"rush x{steps} {SurfaceName(surface)} {distance:F0}->{rushEnd:F0}m over {rushSeconds:F1}s {PhantomPlacement.Side(player, path[0])}");
                break;
            }
        }

        if (!PhantomAudio.TryClaim())
        {
            LastFailure = "a phantom sound is already playing";
            return false;
        }

        AudioSource source = PhantomAudio.CreateSource("Sanity.PhantomSteps", FootstepTemplate(player, startOfRound), path[0]);
        IEnumerator routine = mode switch
        {
            Mode.Pass => Pass(player, source, path, surface),
            Mode.Follow => Follow(player, source, steps, distance),
            _ => Rush(player, source, rushDirection, distance, rushEnd, rushSeconds, steps, surface),
        };
        if (!PhantomAudio.Run(routine))
        {
            Object.Destroy(source.gameObject);
            PhantomAudio.Release();
            LastFailure = "nothing to run it on";
            return false;
        }

        LastSource = path[0];
        string gait = player.isSprinting ? "sprinting" : Time.time - lastLocalStep <= WalkingRecently ? "walking" : "still";
        Telemetry.Event("PHANTOM_STEPS", distance, detail + " (you " + gait + ")");
        return true;
    }

    // ---- Watching (every frame on a moon) ----

    // Keep the path you walked, for follow mode. Starts over on death, teleports and going in or out of the facility.
    internal override void Watch(PlayerControllerB player)
    {
        Vector3 here = player.transform.position;
        bool jumped = (here - lastPosition).sqrMagnitude > TrailJump * TrailJump;
        lastPosition = here;
        if (player.isPlayerDead || jumped || player.isInsideFactory != trailInside)
        {
            trailCount = 0;
            trailInside = player.isInsideFactory;
        }
        if (player.isPlayerDead || player.isClimbingLadder)
        {
            return;
        }

        if (trailCount == 0 || (here - trailPoints[trailNewest]).sqrMagnitude >= TrailSpacing * TrailSpacing)
        {
            trailNewest = trailCount == 0 ? 0 : (trailNewest + 1) % TrailLength;
            trailPoints[trailNewest] = here;
            trailSurfaces[trailNewest] = player.currentFootstepSurfaceIndex;
            trailCount = Mathf.Min(trailCount + 1, TrailLength);
        }
    }

    internal override void ResetDay()
    {
        trailCount = 0;
        lastLocalStep = -9999f;
    }

    // The game's footstep event for our own player (patched in PlayerControllerBPatch): follow mode steps after it.
    internal static void OnLocalStep(PlayerControllerB player)
    {
        lastLocalStep = Time.time;
        if (!following || pendingCount >= PendingMax)
        {
            return;
        }

        followLag = Mathf.Clamp(followLag + Random.Range(-FollowLagDrift, FollowLagDrift), FollowMinLag, FollowMaxLag);
        pendingAt[pendingCount] = Time.time + followLag + Random.Range(-FollowLagJitter, FollowLagJitter);
        pendingSprint[pendingCount] = player.isSprinting;
        pendingCount++;
    }

    // ---- Playing ----

    private static IEnumerator Pass(PlayerControllerB player, AudioSource source, Vector3[] path, int surface, string mode = "pass")
    {
        bool inside = player.isInsideFactory;
        int played = 0;
        string ended = "";
        float started = Time.time;
        try
        {
            float looked = 0f;
            for (int i = 0; i < path.Length && ended.Length == 0; i++)
            {
                PlayStep(source, path[i], surface, sprinting: false);
                played++;

                float wait = i < path.Length - 1 ? WalkInterval + Random.Range(-WalkJitter, WalkJitter) : 0f;
                for (float t = 0f; t < wait && ended.Length == 0; t += Time.deltaTime)
                {
                    yield return null;
                    ended = Interrupted(player, inside);
                    if (ended.Length == 0 && Watched(player, path[i], ref looked, LookSeconds))
                    {
                        ended = "looked toward them";
                    }
                }
            }
            if (ended.Length == 0)
            {
                ended = "walked past";
            }
            yield return new WaitForSeconds(0.6f);   // let the last step ring out
        }
        finally
        {
            Finish(source, mode, played, path.Length, ended, started);
        }
    }

    private static IEnumerator Follow(PlayerControllerB player, AudioSource source, int planned, float distance)
    {
        bool inside = player.isInsideFactory;
        int played = 0;
        string ended = "";
        float started = Time.time;
        followLag = Random.Range(FollowMinLag, FollowMaxLag);
        pendingCount = 0;
        following = true;
        try
        {
            Vector3 heard = source.transform.position;
            float looked = 0f;
            while (played < planned)
            {
                yield return null;
                ended = Interrupted(player, inside);
                if (ended.Length > 0)
                {
                    break;
                }
                if (played > 0 && Watched(player, heard, ref looked, LookSeconds))
                {
                    ended = "looked back";
                    break;
                }
                if (Time.time - lastLocalStep > FollowGiveUpSeconds)
                {
                    ended = "you stopped";
                    break;
                }
                if (pendingCount == 0 || Time.time < pendingAt[0])
                {
                    continue;
                }

                bool sprinting = pendingSprint[0];
                for (int i = 1; i < pendingCount; i++)
                {
                    pendingAt[i - 1] = pendingAt[i];
                    pendingSprint[i - 1] = pendingSprint[i];
                }
                pendingCount--;

                // Where they are now: that far back along your path. Doubled back on yourself: this step is skipped.
                distance = Mathf.Clamp(distance + Random.Range(-FollowDrift, FollowDrift), FollowMinDistance, FollowMaxDistance);
                if (TryTrailPoint(player.transform.position, distance, out Vector3 point, out int surface)
                    && PhantomPlacement.FlatDistance(player.transform.position, point) >= FollowMinStraight)
                {
                    heard = point + Vector3.up * StepHeight;
                    PlayStep(source, heard, surface, sprinting);
                    played++;
                }
            }
            if (ended.Length == 0)
            {
                ended = "finished";
            }
            following = false;
            yield return new WaitForSeconds(0.6f);
        }
        finally
        {
            following = false;
            pendingCount = 0;
            Finish(source, "follow", played, planned, ended, started);
        }
    }

    private static IEnumerator Rush(PlayerControllerB player, AudioSource source, Vector3 direction, float from,
                                    float to, float seconds, int steps, int surface, string mode = "rush")
    {
        bool inside = player.isInsideFactory;
        int played = 0;
        string ended = "";
        float started = Time.time;
        try
        {
            float gap = seconds / (steps - 1);
            float looked = 0f;
            for (int i = 0; i < steps && ended.Length == 0; i++)
            {
                Vector3 point = RushPoint(player, direction, Mathf.Lerp(from, to, (float)i / (steps - 1)));
                PlayStep(source, point, surface, sprinting: true);
                played++;

                float wait = i < steps - 1 ? gap + Random.Range(-SprintJitter, SprintJitter) : 0f;
                for (float t = 0f; t < wait && ended.Length == 0; t += Time.deltaTime)
                {
                    yield return null;
                    ended = Interrupted(player, inside);
                    if (ended.Length == 0 && Watched(player, point, ref looked, 0f))
                    {
                        ended = "looked back";
                    }
                }
            }
            if (ended.Length == 0)
            {
                ended = "reached you";
                if (mode == "rush" && instance != null)
                {
                    SanityDirector.Peak("a rush reached you");
                    Chains.Trigger(instance, "reached you");
                }
            }
            yield return new WaitForSeconds(0.6f);
        }
        finally
        {
            Finish(source, mode, played, steps, ended, started);
        }
    }

    internal static void PlayStep(AudioSource source, Vector3 position, int surface, bool sprinting)
    {
        AudioClip[]? clips = SurfaceClips(surface);
        if (clips == null)
        {
            return;
        }

        int index = Random.Range(0, clips.Length);
        if (index == previousClip)
        {
            index = (index + 1) % clips.Length;
        }
        previousClip = index;

        source.transform.position = position;
        source.pitch = Random.Range(MinPitch, MaxPitch);
        if (clips[index] != null)
        {
            source.PlayOneShot(clips[index], sprinting ? SprintVolume : WalkVolume);
        }
    }

    private static void Finish(AudioSource source, string mode, int played, int planned, string ended, float started)
    {
        if (source != null)
        {
            Object.Destroy(source.gameObject);
        }
        PhantomAudio.Release();
        float lasted = Time.time - started;
        Telemetry.Event("PHANTOM_STEPS_END", played, Invariant($"{mode} {played}/{planned} steps ended {ended} after {lasted:F1}s"));
    }

    // Why the episode must stop now ("" = it can go on).
    private static string Interrupted(PlayerControllerB player, bool inside)
    {
        if (player == null || player.isPlayerDead)
        {
            return "died";
        }
        if (!SanityMeter.OnMoon)
        {
            return "left the moon";
        }
        if (player.isInsideFactory != inside)
        {
            return inside ? "left the facility" : "went inside";
        }
        if (player.isInHangarShipRoom)
        {
            return "entered the ship";
        }
        return "";
    }

    // Has the player been looking toward this point for long enough (within LookAngle, for holdSeconds)?
    private static bool Watched(PlayerControllerB player, Vector3 point, ref float looked, float holdSeconds)
    {
        if (PhantomPlacement.AngleFromLook(player, point) < LookAngle)
        {
            looked += Time.deltaTime;
            return looked >= holdSeconds;
        }
        looked = 0f;
        return false;
    }

    // ---- Where ----

    // Someone walking past: a point behind or beside you, then a few strides sideways along open floor.
    private static Vector3[]? PlanPass(PlayerControllerB player, int steps)
    {
        Vector3 origin = player.transform.position;
        Vector3 look = PhantomPlacement.LookDirection(player);
        float heightTolerance = player.isInsideFactory ? 3.5f : 8f;
        for (int attempt = 0; attempt < PlacementAttempts; attempt++)
        {
            Vector3 direction = PhantomPlacement.RandomBehindDirection(look);
            float distance = Random.Range(PassMinDistance, PassMaxDistance);
            if (!PhantomPlacement.TrySample(origin, origin + direction * distance, heightTolerance, PassMinDistance * 0.7f,
                                            out Vector3 start))
            {
                continue;
            }

            // Walk across: sideways to the line from you, whichever way has more open floor.
            Vector3 across = Vector3.Cross(Vector3.up, PhantomPlacement.Flat(start - origin));
            if (Random.value < 0.5f)
            {
                across = -across;
            }
            float wanted = Stride * (steps - 1);
            float clear = PhantomPlacement.ClearWalk(start, across, wanted);
            if (clear < wanted)
            {
                float other = PhantomPlacement.ClearWalk(start, -across, wanted);
                if (other > clear)
                {
                    across = -across;
                    clear = other;
                }
            }

            // Not much room: shorter strides (pacing about) rather than walking through a wall.
            float stride = steps > 1 ? Mathf.Min(Stride, clear / (steps - 1)) : 0f;
            Vector3[] path = new Vector3[steps];
            for (int i = 0; i < steps; i++)
            {
                Vector3 point = start + across * (stride * i);
                if (NavMesh.SamplePosition(point, out NavMeshHit hit, 1.5f, NavMesh.AllAreas))
                {
                    point = hit.position;
                }
                path[i] = point + Vector3.up * StepHeight;
            }
            return path;
        }
        return null;
    }

    // A rush needs a straight stretch of open floor behind or beside you.
    private static bool PlanRush(PlayerControllerB player, out Vector3 direction, out float start)
    {
        direction = Vector3.zero;
        start = 0f;
        if (!PhantomPlacement.TryFloorUnder(player, out Vector3 floor))
        {
            return false;
        }

        Vector3 look = PhantomPlacement.LookDirection(player);
        for (int attempt = 0; attempt < PlacementAttempts; attempt++)
        {
            Vector3 candidate = PhantomPlacement.RandomBehindDirection(look);
            float clear = PhantomPlacement.ClearWalk(floor, candidate, RushMaxStart);
            if (clear >= RushMinClear)
            {
                direction = candidate;
                start = Mathf.Min(Random.Range(RushMinStart, RushMaxStart), clear - 0.5f);
                return true;
            }
        }
        return false;
    }

    // That far from you, along the rush's line (it closes in on you even if you move), on the floor if possible.
    private static Vector3 RushPoint(PlayerControllerB player, Vector3 direction, float distance)
    {
        Vector3 point = player.transform.position + direction * distance;
        if (NavMesh.SamplePosition(point, out NavMeshHit hit, 1.5f, NavMesh.AllAreas))
        {
            point = hit.position;
        }
        return point + Vector3.up * StepHeight;
    }

    // That far back along the path you walked, and the floor type there. False if the path isn't that long yet.
    internal static bool TryTrailPoint(Vector3 here, float distance, out Vector3 point, out int surface)
    {
        point = Vector3.zero;
        surface = 0;
        float left = distance;
        Vector3 from = here;
        for (int k = 0; k < trailCount; k++)
        {
            int index = (trailNewest - k + TrailLength) % TrailLength;
            Vector3 to = trailPoints[index];
            float segment = Vector3.Distance(from, to);
            if (segment >= left)
            {
                point = Vector3.Lerp(from, to, segment > 0f ? left / segment : 0f);
                surface = trailSurfaces[index];
                return true;
            }
            left -= segment;
            from = to;
        }
        return false;
    }

    internal static MonsterSound? FootstepsOf(MonsterEntry entry)
    {
        foreach (MonsterSound sound in entry.Sounds)
        {
            if (sound.Name == "footsteps")
            {
                return sound;
            }
        }
        return null;
    }

    // One monster footstep, played the way the catalog copies it from the monster.
    internal static void PlayMonsterStep(AudioSource source, Vector3 position, MonsterSound feet, AudioClip[] clips)
    {
        AudioClip clip = clips[Random.Range(0, clips.Length)];
        if (clip == null)
        {
            return;
        }
        source.transform.position = position;
        source.pitch = Random.Range(feet.MinPitch, feet.MaxPitch);
        source.PlayOneShot(clip, Random.Range(feet.MinVolume, feet.MaxVolume));
    }

    // ---- Lookups ----

    private static AudioClip[]? SurfaceClips(int surface)
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
        return clips != null && clips.Length > 0 ? clips : null;
    }

    private static string SurfaceName(int surface)
    {
        StartOfRound startOfRound = StartOfRound.Instance;
        if (startOfRound == null || startOfRound.footstepSurfaces == null || surface < 0
            || surface >= startOfRound.footstepSurfaces.Length || startOfRound.footstepSurfaces[surface] == null)
        {
            return "surface " + surface;
        }
        return startOfRound.footstepSurfaces[surface].surfaceTag;
    }

    // A real player's footstep source, to copy its settings (a teammate's object, else your own).
    internal static AudioSource? FootstepTemplate(PlayerControllerB player, StartOfRound startOfRound)
    {
        if (startOfRound.allPlayerScripts != null)
        {
            foreach (PlayerControllerB other in startOfRound.allPlayerScripts)
            {
                if (other != null && other != player && other.movementAudio != null)
                {
                    return other.movementAudio;
                }
            }
        }
        return player.movementAudio;
    }

    // Straight-line distance to the nearest living teammate, or -1 if none.
    private static float NearestTeammate(PlayerControllerB player, StartOfRound startOfRound)
    {
        float best = -1f;
        if (startOfRound.allPlayerScripts == null)
        {
            return best;
        }
        foreach (PlayerControllerB other in startOfRound.allPlayerScripts)
        {
            if (other == null || other == player || !other.isPlayerControlled || other.isPlayerDead)
            {
                continue;
            }
            float distance = Vector3.Distance(player.transform.position, other.transform.position);
            if (best < 0f || distance < best)
            {
                best = distance;
            }
        }
        return best;
    }
}
