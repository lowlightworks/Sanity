using GameNetcodeStuff;
using UnityEngine;
using UnityEngine.AI;
using static System.FormattableString;

namespace Sanity.Symptoms;

// The Presence. One roll in the lottery; once rolled, a whole episode runs on its own, alongside the normal rolls:
// something is there for a while. A point on walkable floor that moves like a creature would (along real paths, at your
// pace), is never seen, and is the source of what you hear.
//   Uneasy:    it passes or circles 8-12 m away (repositioning now and then, unseen): footsteps.
//   Disturbed: it follows 5-8 m behind on the path you walked, keeping your pace (it walks when you walk, runs when
//              you run, stops when you stop: your measured speed, so speed upgrades are matched).
//   Breaking:  it closes in to 2.5-4 m and ends in a set piece of 2-3 things in a row (a last rush, its voice right
//              behind you, the lights, dread, hits), then it's gone (a peak: the crisis passes, the director rests).
// Any tier: passing a powered light you can see makes the lights flicker (at most twice an episode).
// Turn toward its steps and they stop; a moment later its mask speaks from there (a monster that fits this moon and
// area, imitated all episode), or, if it's "a person", your walkie may click. Then it relocates out of sight. If the
// spot is in clear view, the voice comes from just out of sight near it. A mask also speaks once on its own, 10-25 s
// in.
// Its steps are its mask's: a person's footsteps; a monster's own footsteps if it has them (bunker spider, Butler,
// giant sapsucker, puma), else its own sound every few seconds as it moves (no human steps for a monster). It moves in
// bursts like a creature: 7-14 steps then a pause; at least 5 once it starts (no 2-3 step fragments); never sprinting
// more than 5-8 steps in a row (a long sprint unseen in any direction doesn't ring true).
// At Disturbed or lower it may also arm the locked door or the vanishing item (if they aren't already), so they get
// their chance while it's around. Never seen: in clear view within 30 m it freezes and goes silent; seen for over a
// second within 15 m, it relocates the next moment you're not looking. It holds the phantom-sound slot for the
// episode, so there are never two sources of phantom footsteps at once. Ends if you die, leave, go into the ship or
// change area, or a teammate comes within 8 m (company scares it off). Cooldown 2 minutes after an episode.
// Debug: ShowPresence (config) draws a red ball where it is.
internal sealed class PresenceSymptom : Symptom
{
    private const float CooldownSeconds = 120f;
    private const float TeammateRadius = 8f;
    private const float SeenRange = 30f;
    private const float RelocateIfSeenWithin = 15f;
    private const float RelocateAfterSeen = 1f;
    private const float RepathSeconds = 0.4f;
    // Its steps keep a person's rhythm (the game's footstep cadence, as phantom steps use), whatever its speed. One
    // step per distance travelled would make ~6 steps a second at a running player's pace.
    private const float WalkInterval = 0.5f;
    private const float RunInterval = 0.32f;
    private const float StepJitter = 0.04f;
    private const float RunSpeed = 5.5f;              // above this (m/s) its steps are sprint steps
    private const float FlickerRange = 6f;
    private const float FlickerGap = 25f;
    private const int MaxFlickers = 2;
    private const float TurnAngle = 50f;
    private const float ArmChance = 0.5f;
    private const float PersonChance = 0.4f;
    private const int MinBurstSteps = 5;
    private const float VoiceLoudness = 1.4f;        // its voice is louder still than other phantoms (it must be heard)
    private const float StartBurstDistance = 3.5f;   // it only sets off if it has at least this far to go

    private enum Phase
    {
        Stalk,
        Follow,
        CloseIn,
        Withdraw,
    }

    private static PresenceSymptom? instance;

    // Your pace (flat m/s, smoothed), measured every frame.
    private static float playerSpeed;
    private static Vector3 lastPlayerPosition;
    private static bool hasLastPosition;

    // The episode.
    private static bool active;
    private static float endedAt = -9999f;
    private static Phase phase;
    private static Vector3 position;
    private static bool inside;
    private static float startedAt;
    private static float endsAt;
    private static float nextRepath;
    private static float nextReposition;
    private static readonly NavMeshPath Path = new NavMeshPath();
    private static Vector3[] corners = System.Array.Empty<Vector3>();
    private static int corner;
    private static float stepBank;
    private static int steps;
    private static float seenFor;
    private static bool silencedByTurn;
    private static float voiceAt = -1f;
    private static float resumeAt = -1f;
    private static int flickers;
    private static float lastFlicker = -9999f;
    private static AudioSource? feet;
    private static MonsterEntry? mask;
    private static EnemyAI? maskExample;
    private static MonsterSound? maskVoice;
    private static AudioClip[]? maskClips;
    private static MonsterSound? idleVoice;           // its idle sound (the unprompted voice)
    private static AudioClip[]? idleClips;
    private static MonsterSound? maskSteps;           // the mask's own footsteps, if it has them
    private static AudioClip[]? maskStepClips;
    private static MonsterSound? maskCue;             // else the sound it makes as it moves
    private static AudioClip[]? maskCueClips;
    private static float nextMonsterStep;
    private static float nextCue;
    private static float idleVoiceAt;
    private static string log = "";

    // Moving in bursts.
    private static bool inBurst;
    private static int burstSteps;
    private static int burstTarget;
    private static int burstRunSteps;
    private static int burstRunLimit;
    private static float pauseUntil;

    // Debug: a red ball where it is (config ShowPresence).
    private static GameObject? marker;

    internal PresenceSymptom()
    {
        instance = this;
    }

    internal override string Name => "Presence";

    internal override string ConfigKey => "Presence";

    internal override string Family => "sound";

    internal override SanityTier MinTier => SanityTier.Uneasy;

    internal override float BaseWeight => 0.9f;

    internal override float RecoverySeconds => 240f;

    internal static bool Active => active;

    internal static float DistanceTo(Vector3 point) => Vector3.Distance(point, position);

    internal override string RunningState(PlayerControllerB player) => active ? DebugState(player) : "";

    // Debug observation: with the marker on (ShowPresence) it isn't shy (no freezing or relocating when seen), so you
    // can watch what it does.
    private static bool Observed => Plugin.ShowPresence.Value;

    // The last few things it did, for the on-screen readout.
    internal static string LastNotes { get; private set; } = "";

    internal override string CantPlayReason(PlayerControllerB player)
    {
        StartOfRound startOfRound = StartOfRound.Instance;
        if (startOfRound == null || startOfRound.footstepSurfaces == null || RoundManager.Instance == null)
        {
            return "no round";
        }
        if (active)
        {
            return "already here";
        }
        if (Time.time - endedAt < CooldownSeconds)
        {
            return Invariant($"cooldown: {CooldownSeconds - (Time.time - endedAt):F0} s left");
        }
        if (player.isInHangarShipRoom)
        {
            return "in ship";
        }
        if (PhantomAudio.IsPlaying)
        {
            return "already playing";
        }
        float teammate = NearestTeammate(player, startOfRound);
        if (teammate >= 0f && teammate <= TeammateRadius * 1.5f)
        {
            return Invariant($"teammate close: {teammate:F0} m (needs over {TeammateRadius * 1.5f:F0})");
        }
        return "";
    }

    internal override bool TryPlay(PlayerControllerB player, SanityTier tier)
    {
        StartOfRound startOfRound = StartOfRound.Instance;
        if (startOfRound == null || active)
        {
            LastFailure = "not now";
            return false;
        }
        float distance = BandFor(tier, out float max);
        if (!TryHiddenPoint(player, distance, max, out Vector3 start))
        {
            LastFailure = "no hidden floor near you";
            return false;
        }
        if (!PhantomAudio.TryClaim())
        {
            LastFailure = "a phantom sound is playing";
            return false;
        }

        // The mask: a monster that fits here, or "a person" (footsteps only).
        mask = null;
        maskExample = null;
        maskVoice = null;
        maskClips = null;
        idleVoice = null;
        idleClips = null;
        if (Random.value >= PersonChance && PhantomSoundSymptom.PickMask(player, out MonsterEntry entry, out EnemyAI example,
                                                                         out MonsterSound voice, out AudioClip[] clips))
        {
            mask = entry;
            maskExample = example;
            maskVoice = voice;
            maskClips = clips;
        }
        // Its voice when you look its way: the monster's cry from the attack list if it has one (hoarding bug screech,
        // baboon hawk / sapsucker / puma scream: distinctive, the idle ones are easy to miss), else its idle sound.
        idleVoice = maskVoice;
        idleClips = maskClips;
        if (maskExample != null)
        {
            MonsterEntry? cry = MonsterSoundCatalog.Find(MonsterSoundCatalog.Attackers, maskExample);
            AudioClip[]? cryClips = cry != null && cry.Sounds.Length > 0 ? cry.Sounds[0].Clips(maskExample) : null;
            if (cry != null && cryClips != null && cryClips.Length > 0)
            {
                maskVoice = cry.Sounds[0];
                maskClips = cryClips;
            }
        }
        maskSteps = maskCue = null;
        maskStepClips = maskCueClips = null;
        AudioSource? feetTemplate = PhantomStepsSymptom.FootstepTemplate(player, startOfRound);
        if (mask != null && maskExample != null)
        {
            MonsterSound? own = PhantomStepsSymptom.FootstepsOf(mask);
            AudioClip[]? ownClips = own != null ? own.Clips(maskExample) : null;
            if (own != null && ownClips != null && ownClips.Length > 0)
            {
                maskSteps = own;
                maskStepClips = ownClips;
                feetTemplate = own.Source(maskExample) ?? maskExample.creatureSFX;
            }
            else
            {
                // No footsteps: its rhythmic sound if it has one (chitter, breathing), else its voice.
                foreach (MonsterSound option in mask.Sounds)
                {
                    AudioClip[]? optionClips = option.Clips(maskExample);
                    if (optionClips != null && optionClips.Length > 0 && (maskCue == null || (option.Rhythmic && !maskCue.Rhythmic)))
                    {
                        maskCue = option;
                        maskCueClips = optionClips;
                    }
                }
                feetTemplate = maskCue != null ? maskCue.Source(maskExample) ?? maskExample.creatureVoice : maskExample.creatureVoice;
            }
        }

        active = true;
        position = start;
        inside = player.isInsideFactory;
        startedAt = Time.time;
        endsAt = Time.time + (tier == SanityTier.Breaking ? Random.Range(40f, 60f) : tier == SanityTier.Disturbed ? Random.Range(45f, 70f) : Random.Range(30f, 45f));
        phase = tier == SanityTier.Uneasy ? Phase.Stalk : Phase.Follow;
        corners = System.Array.Empty<Vector3>();
        nextRepath = 0f;
        nextReposition = Time.time + Random.Range(8f, 14f);
        stepBank = 0f;
        steps = 0;
        seenFor = 0f;
        silencedByTurn = false;
        voiceAt = resumeAt = -1f;
        flickers = 0;
        log = "";
        LastNotes = "";
        inBurst = false;
        pauseUntil = 0f;
        nextMonsterStep = nextCue = 0f;
        idleVoiceAt = mask != null ? Time.time + Random.Range(10f, 25f) : float.MaxValue;
        feet = PhantomAudio.CreateSource("Sanity.Presence", feetTemplate, position);
        LastSource = position;

        // It brings the situational ones with it: arm the door (inside) or the item, if they're free.
        string armed = TryArmCompanion(player, tier);
        Telemetry.Event("PRESENCE", Vector3.Distance(player.transform.position, position), Invariant(
            $"{MaskName} ({(maskSteps != null ? "its footsteps" : maskCue != null ? "its " + maskCue.Name + " as it moves" : "footsteps")}) {Vector3.Distance(player.transform.position, position):F0}m {PhantomPlacement.Side(player, position)} {phase} for {endsAt - startedAt:F0}s{armed}"));
        return true;
    }

    // ---- Every frame on a moon ----

    internal override void Watch(PlayerControllerB player)
    {
        // Your pace (for matching it), ignoring teleports.
        Vector3 here = player.transform.position;
        if (hasLastPosition && Time.deltaTime > 0f)
        {
            Vector3 step = here - lastPlayerPosition;
            step.y = 0f;
            float speed = step.magnitude < 4f ? step.magnitude / Time.deltaTime : playerSpeed;
            playerSpeed = Mathf.Lerp(playerSpeed, speed, 1f - Mathf.Exp(-Time.deltaTime / 0.5f));
        }
        lastPlayerPosition = here;
        hasLastPosition = true;

        if (active)
        {
            Run(player);
        }
        UpdateMarker();
    }

    private static string MaskName => mask != null ? mask.Name : "a person";

    // For the debug menu.
    internal static string DebugState(PlayerControllerB player)
    {
        if (!active)
        {
            float cool = CooldownSeconds - (Time.time - endedAt);
            return cool > 0f ? Invariant($"gone (cooldown {cool:F0} s)") : "gone";
        }
        return Invariant($"{phase}, {MaskName}, {Vector3.Distance(player.transform.position, position):F1} m {PhantomPlacement.Side(player, position)}, {Mathf.Max(0f, endsAt - Time.time):F0} s left{(silencedByTurn ? ", silent (you turned)" : inBurst ? ", moving" : ", still")}");
    }

    private static void UpdateMarker()
    {
        bool want = active && Plugin.ShowPresence.Value;
        if (want && marker == null)
        {
            marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            marker.name = "Sanity.PresenceMarker";
            Object.Destroy(marker.GetComponent<Collider>());
            marker.transform.localScale = Vector3.one * 0.4f;
            Renderer renderer = marker.GetComponent<Renderer>();
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            Shader shader = Shader.Find("HDRP/Unlit");
            if (shader != null)
            {
                Material material = new Material(shader);
                material.SetColor("_UnlitColor", new Color(1f, 0.1f, 0.1f, 1f));
                renderer.material = material;
            }
        }
        else if (!want && marker != null)
        {
            Object.Destroy(marker);
            marker = null;
        }
        if (marker != null)
        {
            marker.transform.position = position + Vector3.up * 1.1f;
        }
    }

    internal override void ResetDay()
    {
        if (active)
        {
            End("day ended");
        }
        hasLastPosition = false;
        endedAt = -9999f;
    }

    private static void Run(PlayerControllerB player)
    {
        StartOfRound startOfRound = StartOfRound.Instance;
        string why = player.isPlayerDead ? "died"
            : !SanityMeter.OnMoon ? "left the moon"
            : player.isInHangarShipRoom ? "you entered the ship"
            : player.isInsideFactory != inside ? "you changed area"
            : startOfRound == null ? "no round"
            : "";
        if (why.Length > 0)
        {
            End(why);
            return;
        }
        float teammate = NearestTeammate(player, startOfRound!);
        if (phase != Phase.Withdraw && teammate >= 0f && teammate <= TeammateRadius)
        {
            Begin(Phase.Withdraw, "company came");
        }
        if (phase != Phase.Withdraw && (Time.time >= endsAt || SanityMeter.Tier == SanityTier.Calm))
        {
            Begin(Phase.Withdraw, "time's up");
        }
        if (phase == Phase.Follow && SanityMeter.Tier == SanityTier.Breaking && Time.time - startedAt > 8f)
        {
            Begin(Phase.CloseIn, "closing in");
        }

        float distance = Vector3.Distance(player.transform.position, position);
        if (phase == Phase.Withdraw && distance > 22f)
        {
            End("withdrew");
            return;
        }
        if (phase == Phase.Withdraw && Time.time >= endsAt + 20f)
        {
            End("withdrew (time)");
            return;
        }

        // Turned toward its steps: it falls silent, then its mask speaks (or your walkie clicks), then it moves off.
        // Checked before "seen": otherwise turning to face it in the open would only freeze it and the voice would
        // never come.
        if (!silencedByTurn && steps >= (maskCue != null ? 1 : 2) && PhantomPlacement.AngleFromLook(player, position) < TurnAngle && distance < 20f)
        {
            silencedByTurn = true;
            inBurst = false;
            voiceAt = Time.time + Random.Range(0.8f, 1.5f);
            resumeAt = voiceAt + Random.Range(3f, 6f);
            Note("you turned toward it");
        }
        if (silencedByTurn)
        {
            if (voiceAt > 0f && Time.time >= voiceAt)
            {
                voiceAt = -1f;
                if (!Observed && Seen(player, distance))
                {
                    MoveOutOfSightNear(player);   // the voice comes from just out of sight, never from an empty spot in view
                }
                Speak(player);
            }
            if (Time.time >= resumeAt)
            {
                silencedByTurn = false;
                steps = 0;
                Relocate(player, "after the voice");
                if (phase != Phase.Withdraw && Random.value < 0.4f)
                {
                    Begin(Phase.Withdraw, "after you turned");
                }
            }
            return;
        }

        // Never seen: freeze in clear view; relocate if watched up close (not while observed with the marker).
        bool seen = !Observed && Seen(player, distance);
        seenFor = seen ? seenFor + Time.deltaTime : 0f;
        if (seen)
        {
            inBurst = false;
            if (seenFor >= RelocateAfterSeen && distance <= RelocateIfSeenWithin)
            {
                Relocate(player, "seen");
            }
            return;
        }

        // A mask speaks once on its own, while you're not looking its way.
        if (Time.time >= idleVoiceAt && distance < 18f && phase != Phase.Withdraw)
        {
            idleVoiceAt = float.MaxValue;
            Speak(player, idle: true);
        }

        // Erratic: now and then it repositions out of sight (more chances for its moments, harder to predict).
        if (phase != Phase.CloseIn && Time.time >= nextReposition)
        {
            nextReposition = Time.time + Random.Range(10f, 18f);
            if (Random.value < 0.5f)
            {
                Relocate(player, "repositioned");
            }
        }

        Move(player, distance);
        TryFlicker(player);

        if (phase == Phase.CloseIn && distance <= 4f)
        {
            SetPiece(player);
        }
    }

    // ---- Moving and stepping ----

    private static void Move(PlayerControllerB player, float distance)
    {
        Vector3 goal = Goal(player, distance, out float speed);
        if (Time.time >= nextRepath)
        {
            nextRepath = Time.time + RepathSeconds;
            if (NavMesh.CalculatePath(position, goal, NavMesh.AllAreas, Path) && Path.corners.Length > 1)
            {
                corners = Path.corners;
                corner = 1;
            }
            else
            {
                corners = System.Array.Empty<Vector3>();
            }
        }
        bool pathLeft = corners.Length > 0 && corner < corners.Length;
        if (phase == Phase.CloseIn)
        {
            pauseUntil = 0f;
        }
        if (!inBurst)
        {
            // Sets off only with somewhere to go, so a burst is never a 2-3 step fragment.
            if (Time.time < pauseUntil || speed <= 0.2f || !pathLeft || Vector3.Distance(position, goal) < StartBurstDistance)
            {
                return;
            }
            inBurst = true;
            burstSteps = 0;
            burstRunSteps = 0;
            burstTarget = Random.Range(7, 15);
            burstRunLimit = Random.Range(5, 9);
        }
        else if (speed <= 0.2f)
        {
            if (burstSteps >= MinBurstSteps)
            {
                EndBurst(Random.Range(0.6f, 1.5f));
                return;
            }
            speed = 2.6f * PaceScale(player);   // finish the burst at a walk
        }
        if (speed >= RunSpeed && burstRunSteps >= burstRunLimit && phase != Phase.CloseIn)
        {
            EndBurst(Random.Range(1.2f, 2.5f));  // it can't sprint on and on unseen
            return;
        }
        if (!pathLeft)
        {
            EndBurst(0.5f);
            return;
        }

        float left = speed * Time.deltaTime;
        float moved = 0f;
        while (left > 0f && corner < corners.Length)
        {
            Vector3 to = corners[corner];
            float span = Vector3.Distance(position, to);
            if (span <= left)
            {
                position = to;
                left -= span;
                moved += span;
                corner++;
            }
            else
            {
                position = Vector3.MoveTowards(position, to, left);
                moved += left;
                left = 0f;
            }
        }

        // Its steps: a person's by distance (one per stride); a monster's own footsteps at its own rhythm; a monster
        // without footsteps, its sound every few seconds.
        bool running = speed >= RunSpeed;
        if (moved <= 0f || feet == null)
        {
            return;
        }
        bool stepped = false;
        if (maskSteps != null && maskStepClips != null)
        {
            if (Time.time >= nextMonsterStep)
            {
                nextMonsterStep = Time.time + Random.Range(maskSteps.MinGap, Mathf.Max(maskSteps.MinGap, maskSteps.MaxGap)) * (running ? 0.8f : 1f);
                PhantomStepsSymptom.PlayMonsterStep(feet, position + Vector3.up * 0.3f, maskSteps, maskStepClips);
                stepped = true;
            }
        }
        else if (maskCue != null && maskCueClips != null)
        {
            if (Time.time >= nextCue)
            {
                nextCue = Time.time + Random.Range(2.5f, 5f);   // sparser is too hard to follow
                PhantomStepsSymptom.PlayMonsterStep(feet, position + Vector3.up * 0.8f, maskCue, maskCueClips);
                stepped = true;
            }
            stepBank += Time.deltaTime;
            if (stepBank >= (running ? RunInterval : WalkInterval))
            {
                stepBank = Random.Range(-StepJitter, StepJitter);
                CountStep(running);   // the burst still counts its (silent) strides
            }
        }
        else
        {
            stepBank += Time.deltaTime;
            if (stepBank >= (running ? RunInterval : WalkInterval))
            {
                stepBank = Random.Range(-StepJitter, StepJitter);
                PhantomStepsSymptom.PlayStep(feet, position + Vector3.up * 0.3f, player.currentFootstepSurfaceIndex, running);
                stepped = true;
            }
        }
        if (stepped)
        {
            steps++;
            if (maskCue == null)
            {
                CountStep(running);
            }
        }
    }

    private static void CountStep(bool running)
    {
        burstSteps++;
        if (running)
        {
            burstRunSteps++;
        }
        if (burstSteps >= burstTarget && phase != Phase.CloseIn)
        {
            EndBurst(phase == Phase.Stalk ? Random.Range(1.5f, 4f) : Random.Range(0.8f, 2f));
        }
    }

    private static void EndBurst(float pause)
    {
        inBurst = false;
        pauseUntil = Time.time + pause;
    }

    // The nearest spot out of your sight around where it is (for its voice when you're looking right at it).
    private static void MoveOutOfSightNear(PlayerControllerB player)
    {
        float height = player.isInsideFactory ? 3f : 6f;
        for (int attempt = 0; attempt < 12; attempt++)
        {
            Vector3 direction = Quaternion.AngleAxis(Random.Range(0f, 360f), Vector3.up) * Vector3.forward;
            if (PhantomPlacement.TrySample(position, position + direction * Random.Range(2f, 6f), height, 1.5f, out Vector3 point)
                && !Seen(player, Vector3.Distance(player.transform.position, point), point))
            {
                position = point;
                corners = System.Array.Empty<Vector3>();
                Note("slipped out of sight before its voice");
                return;
            }
        }
        Relocate(player, "out of sight before its voice");
    }

    // Where it heads, and how fast, for its phase.
    private static Vector3 Goal(PlayerControllerB player, float distance, out float speed)
    {
        Vector3 here = player.transform.position;
        float walk = 2.6f * PaceScale(player);
        switch (phase)
        {
            case Phase.Follow:
            {
                // On your path, 5-8 m back, at your pace (stopped if you stop; catching up if it fell behind).
                float want = Mathf.Lerp(5f, 8f, 0.5f);
                speed = playerSpeed < 0.5f && distance <= 8.5f ? 0f : Mathf.Max(playerSpeed, distance > 9f ? walk * 1.3f : 0f);
                // No path behind you yet: it holds where it is (never walks straight at you while following).
                return PhantomStepsSymptom.TryTrailPoint(here, want, out Vector3 onPath, out _) ? onPath : position;
            }
            case Phase.CloseIn:
                speed = Mathf.Max(playerSpeed * 1.2f, walk * 1.4f);
                return here;
            case Phase.Withdraw:
                speed = walk;
                return position + PhantomPlacement.Flat(position - here) * 15f;
            default:
            {
                // Stalk: circles at 8-12 m, beside or behind you, walking.
                speed = walk;
                Vector3 around = Quaternion.AngleAxis(Time.time * 6f % 360f, Vector3.up) * PhantomPlacement.Flat(position - here);
                if (around == Vector3.zero)
                {
                    around = -PhantomPlacement.LookDirection(player);
                }
                return here + around * 10f;
            }
        }
    }

    // Speed upgrades (e.g. Lategame Upgrades' running shoes, any tier) raise the player's base movement speed.
    private static float PaceScale(PlayerControllerB player)
    {
        return Mathf.Clamp(player.movementSpeed / 4.6f, 0.6f, 2.5f);
    }

    // ---- Moments ----

    private static void Speak(PlayerControllerB player, bool idle = false)
    {
        MonsterSound? maskVoice = idle && idleVoice != null ? idleVoice : PresenceSymptom.maskVoice;
        AudioClip[]? maskClips = idle && idleClips != null ? idleClips : PresenceSymptom.maskClips;
        if (mask != null && maskVoice != null && maskClips != null && maskClips.Length > 0 && maskExample != null)
        {
            AudioClip clip = maskClips[Random.Range(0, maskClips.Length)];
            if (clip != null)
            {
                AudioSource? template = maskVoice.Source(maskExample) ?? maskExample.creatureVoice;
                AudioSource voice = PhantomAudio.CreateSource("Sanity.PresenceVoice", template, position + Vector3.up * 0.8f, extra: VoiceLoudness);
                int count = maskVoice.Rhythmic ? Random.Range(maskVoice.MinCount, maskVoice.MaxCount + 1) : 1;
                if (count <= 1 || !PhantomAudio.Run(Burst(voice, maskVoice, maskClips, count)))
                {
                    voice.pitch = Random.Range(maskVoice.MinPitch, maskVoice.MaxPitch);
                    voice.PlayOneShot(clip, Random.Range(maskVoice.MinVolume, maskVoice.MaxVolume));
                    Object.Destroy(voice.gameObject, clip.length + 0.5f);
                }
                Note(Invariant($"its voice: {mask.Name} {maskVoice.Name}{(count > 1 ? Invariant($" x{count}") : "")}"));
                return;
            }
        }
        // "A person": no voice. Maybe your walkie clicks.
        if (SanityDirector.Find("PhantomWalkie") is PhantomWalkieSymptom walkie && walkie.Enabled && walkie.CantPlayReason(player).Length == 0
            && walkie.TryPlay(player, SanityTier.Uneasy))
        {
            Note("your walkie clicked");
        }
        else
        {
            Note("silence");
        }
    }

    // A rhythmic voice (chitter, breathing, a spider's scuttle) as the monster plays it.
    private static System.Collections.IEnumerator Burst(AudioSource voice, MonsterSound sound, AudioClip[] clips, int count)
    {
        try
        {
            float tail = 0f;
            for (int i = 0; i < count && voice != null; i++)
            {
                AudioClip clip = clips[Random.Range(0, clips.Length)];
                if (clip != null)
                {
                    voice.pitch = Random.Range(sound.MinPitch, sound.MaxPitch);
                    voice.PlayOneShot(clip, Random.Range(sound.MinVolume, sound.MaxVolume));
                    tail = clip.length / Mathf.Max(0.1f, voice.pitch);
                }
                float gap = Random.Range(sound.MinGap, sound.MaxGap) + (sound.GapAfterClip ? tail : 0f);
                yield return new WaitForSeconds(gap);
            }
            yield return new WaitForSeconds(tail + 0.2f);
        }
        finally
        {
            if (voice != null)
            {
                Object.Destroy(voice.gameObject);
            }
        }
    }

    private static void TryFlicker(PlayerControllerB player)
    {
        if (!inside || flickers >= MaxFlickers || Time.time - lastFlicker < FlickerGap)
        {
            return;
        }
        RoundManager roundManager = RoundManager.Instance;
        if (roundManager == null || roundManager.allPoweredLights == null)
        {
            return;
        }
        foreach (Light light in roundManager.allPoweredLights)
        {
            if (light == null || !light.gameObject.activeInHierarchy
                || Vector3.Distance(light.transform.position, position) > FlickerRange)
            {
                continue;
            }
            if (FlickerSymptom.FlickNow(player, SanityMeter.Tier).Length == 0)
            {
                flickers++;
                lastFlicker = Time.time;
                Note("lights flickered as it passed");
            }
            return;
        }
    }

    // Breaking: 2-3 things in a row, right behind you, then it's gone.
    private static void SetPiece(PlayerControllerB player)
    {
        int count = Random.Range(2, 4);
        string done = "";
        for (int i = 0; i < 6 && count > 0; i++)
        {
            int pick = Random.Range(0, 5);
            string result = pick switch
            {
                0 => PlayIf(() => FlickerSymptom.FlickNow(player, SanityTier.Breaking), "lights"),
                1 => PlayCard("PhantomDread", player) ? "dread" : "",
                2 => HitFromBehind(player),
                3 => SpeakNow(player),
                _ => LastSteps(player),
            };
            if (result.Length > 0 && !done.Contains(result))
            {
                done += (done.Length > 0 ? "+" : "") + result;
                count--;
            }
        }
        Note("set piece: " + (done.Length > 0 ? done : "nothing could play"));
        SanityDirector.Peak("presence");
        End("set piece");
    }

    // A step right behind you, then its hit (as its mask: a monster's own hit, or a person's). Only if that hit can
    // land.
    private static string HitFromBehind(PlayerControllerB player)
    {
        string imitating = mask != null ? mask.Name : PhantomHitSymptom.Person;
        if (!(SanityDirector.Find("PhantomHit") is PhantomHitSymptom hit) || !hit.CanHitAs(player, imitating)
            || LastSteps(player).Length == 0)
        {
            return "";
        }
        return hit.Start(player, SanityTier.Breaking, withCause: false, why: "the presence, right behind you", imitating)
            ? "hits" : "";
    }

    private static string SpeakNow(PlayerControllerB player)
    {
        Speak(player);
        return "voice";
    }

    private static string LastSteps(PlayerControllerB player)
    {
        if (feet == null)
        {
            return "";
        }
        Vector3 behind = player.transform.position - PhantomPlacement.LookDirection(player) * 1.5f;
        if (maskSteps != null && maskStepClips != null)
        {
            PhantomStepsSymptom.PlayMonsterStep(feet, behind + Vector3.up * 0.3f, maskSteps, maskStepClips);
        }
        else if (maskCue != null && maskCueClips != null)
        {
            PhantomStepsSymptom.PlayMonsterStep(feet, behind + Vector3.up * 0.8f, maskCue, maskCueClips);
        }
        else
        {
            PhantomStepsSymptom.PlayStep(feet, behind + Vector3.up * 0.3f, player.currentFootstepSurfaceIndex, sprinting: true);
        }
        return "last step";
    }

    private static string PlayIf(System.Func<string> play, string label)
    {
        return play().Length == 0 ? label : "";
    }

    private static bool PlayCard(string key, PlayerControllerB player)
    {
        Symptom? card = SanityDirector.Find(key);
        return card != null && card.Enabled && card.CantPlayReason(player).Length == 0 && card.TryPlay(player, SanityTier.Breaking);
    }

    private static string TryArmCompanion(PlayerControllerB player, SanityTier tier)
    {
        if (tier < SanityTier.Disturbed || Random.value >= ArmChance)
        {
            return "";
        }
        string key = player.isInsideFactory && Random.value < 0.5f ? "FakeLockedDoor" : "VanishingItem";
        Symptom? card = SanityDirector.Find(key);
        if (card is ArmedSymptom armed && armed.Enabled && armed.CantPlayReason(player).Length == 0 && armed.TryPlay(player, tier))
        {
            Telemetry.Event("SYMPTOM_ARMED", (int)tier, $"{card.Name} (by the presence): waits for {armed.Waits}");
            return ", armed " + key;
        }
        return "";
    }

    // ---- Where ----

    private static float BandFor(SanityTier tier, out float max)
    {
        if (tier == SanityTier.Uneasy)
        {
            max = 12f;
            return 8f;
        }
        max = 8f;
        return 5f;
    }

    // A point at that distance behind or beside you, on floor, out of your sight.
    private static bool TryHiddenPoint(PlayerControllerB player, float min, float max, out Vector3 point)
    {
        Vector3 origin = player.transform.position;
        Vector3 look = PhantomPlacement.LookDirection(player);
        float height = player.isInsideFactory ? 3f : 6f;
        for (int attempt = 0; attempt < 14; attempt++)
        {
            Vector3 direction = PhantomPlacement.RandomBehindDirection(look);
            if (PhantomPlacement.TrySample(origin, origin + direction * Random.Range(min, max), height, min * 0.6f, out point)
                && !Seen(player, Vector3.Distance(origin, point), point))
            {
                return true;
            }
        }
        point = Vector3.zero;
        return false;
    }

    private static void Relocate(PlayerControllerB player, string why)
    {
        float min = phase == Phase.Stalk ? 8f : 5f;
        float max = phase == Phase.Stalk ? 12f : 8f;
        if (TryHiddenPoint(player, min, max, out Vector3 point))
        {
            position = point;
            corners = System.Array.Empty<Vector3>();
            nextRepath = 0f;
            seenFor = 0f;
            Note("relocated (" + why + ")");
        }
    }

    // In your view (with a margin) and in clear sight within 30 m.
    private static bool Seen(PlayerControllerB player, float distance, Vector3? at = null)
    {
        Vector3 spot = (at ?? position) + Vector3.up * 1.2f;
        if (distance > SeenRange)
        {
            return false;
        }
        Vector3 view = player.gameplayCamera.WorldToViewportPoint(spot);
        if (view.z <= 0f || view.x < -0.05f || view.x > 1.05f || view.y < -0.05f || view.y > 1.05f)
        {
            return false;
        }
        StartOfRound startOfRound = StartOfRound.Instance;
        return startOfRound == null
               || !Physics.Linecast(player.gameplayCamera.transform.position, spot, startOfRound.collidersAndRoomMask, QueryTriggerInteraction.Ignore);
    }

    // ---- Bookkeeping ----

    private static void Begin(Phase next, string why)
    {
        if (phase == next)
        {
            return;
        }
        phase = next;
        if (next == Phase.Withdraw)
        {
            endsAt = Time.time;   // it has 20 s to get out of earshot
        }
        Note(next + " (" + why + ")");
    }

    private static void Note(string what)
    {
        if (log.Length < 400)
        {
            log += (log.Length > 0 ? "; " : "") + Invariant($"{Time.time - startedAt:F0}s {what}");
        }
        Telemetry.Event("PRESENCE_EVENT", Time.time - startedAt, what);
        LastNotes = Invariant($"{Time.time - startedAt:F0}s {what}") + (LastNotes.Length > 0 ? " | " + LastNotes : "");
        if (LastNotes.Length > 160)
        {
            LastNotes = LastNotes.Substring(0, 160);
        }
    }

    private static void End(string why)
    {
        if (!active)
        {
            return;
        }
        active = false;
        endedAt = Time.time;
        inBurst = false;
        UpdateMarker();
        if (feet != null)
        {
            Object.Destroy(feet.gameObject, 0.8f);
            feet = null;
        }
        PhantomAudio.Release();
        if (instance != null)
        {
            instance.SecondsSincePlayed = 0f;
        }
        Telemetry.Event("PRESENCE_END", Time.time - startedAt, Invariant($"{why} after {Time.time - startedAt:F0}s, {steps} steps since last turn"));
    }

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
