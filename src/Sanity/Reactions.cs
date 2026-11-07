using System.Collections.Generic;
using System.Text;
using GameNetcodeStuff;
using Sanity.Symptoms;
using UnityEngine;
using static System.FormattableString;

namespace Sanity;

// Reaction probes: did a symptom change what the player did, beyond chance?
//
// Method. Every symptom that plays opens a probe: an observation window (6 s; 20 s for walkie replies) with the 3 s
// before it as the baseline. The SAME detector also runs at random CONTROL moments (on average every 40 s, only after
// 10 s without any symptom, discarded if a symptom lands inside the window), each with a made-up source direction
// behind or beside you, like a phantom's. Whatever the detector catches at control moments is the base rate of random
// matches (people turn, stop and scan on their own all the time); a symptom's effect is its rate minus the control
// rate (analyze.py: the lift, with a 95% interval, by place and movement). Windows with real fear, real damage, a
// teammate's death, another symptom, the debug menu or the end of the day are flagged as confounded and left out.
//
// What each probe records (key=value, see REACTION rows):
//   turned / turn_s   directional probes: your view came within 30 degrees of the source, which started 90+ away
//   yaw_b / yaw_a     head-turn rate (deg/s) in the 3 s before / the 3 s after
//   v_b / v_a         speed (m/s) in the 3 s before / the 3 s after
//   stopped           moving at 2+ m/s before, below 0.6 m/s within 3 s
//   sprinted          not sprinting at the start, sprinting within 3 s
//   scan / use        pressed scan / use within the window
//   walkie            pressed your walkie within 20 s (only counted when you carry one: has_walkie)
//   away / toward     directional: moved 2+ m away from / toward the source within 4 s
// Context: place, company, moving, tier, fear, light on, walkie carried.
internal static class Reactions
{
    private const float Window = 6f;
    private const float WalkieWindow = 20f;
    private const float AfterSeconds = 3f;
    private const float BinSeconds = 0.1f;
    private const int Bins = 30;                     // 3 s of history
    private const float ControlMeanGap = 40f;
    private const float ControlQuietBefore = 10f;
    private const float TurnCone = 30f;
    private const float TurnStartMin = 90f;
    private const float StopFrom = 2f;
    private const float StopTo = 0.6f;
    private const float MoveMetres = 2f;

    private sealed class Probe
    {
        internal object? StatsSound;   // the anonymous stats' record of this phantom sound, if it is one
        internal bool Real;
        internal string Name = "";
        internal float T0;
        internal bool Directional;
        internal Vector3 Source;
        internal float StartAngle;
        internal bool Turned;
        internal float TurnTime = -1f;
        internal float YawBefore;
        internal float SpeedBefore;
        internal float YawAfter;
        internal float DistAfter;
        internal bool Stopped;
        internal bool Sprinted;
        internal bool SprintingAtStart;
        internal bool Scan;
        internal bool Use;
        internal bool Walkie;
        internal bool HasWalkie;
        internal bool Away;
        internal bool Toward;
        internal bool Moved;
        internal Vector3 StartPos;
        internal string Confound = "";
        internal string Context = "";
    }

    private static readonly List<Probe> Open = new List<Probe>();
    private static readonly float[] YawBins = new float[Bins];
    private static readonly float[] DistBins = new float[Bins];
    private static int bin;
    private static float binTime;
    private static float lastYaw;
    private static bool hasYaw;
    private static Vector3 lastPos;
    private static bool hasPos;
    private static float smoothSpeed;
    private static float nextControl = -1f;
    private static readonly StringBuilder Text = new StringBuilder(400);

    // A symptom played (natural, armed, chained): open a real probe. source = where it came from, if anywhere.
    internal static void OnSymptom(Symptom symptom, Vector3? source)
    {
        // Every open probe now overlaps another symptom.
        foreach (Probe open in Open)
        {
            if (open.Confound.Length == 0)
            {
                open.Confound = "another symptom";
            }
        }
        // A control that overlaps a symptom is no control at all.
        Open.RemoveAll(p => !p.Real);

        PlayerControllerB? player = Local();
        if (player == null || !Telemetry.Recording)
        {
            return;
        }
        Probe probe = NewProbe(player, true, symptom.ConfigKey, source);
        probe.StatsSound = DayStats.TakeSound(symptom);
        Open.Add(probe);
    }

    // Something that would muddy every open probe (real fear, damage, a teammate's death, the debug menu).
    internal static void NoteConfound(string why)
    {
        foreach (Probe open in Open)
        {
            if (open.Confound.Length == 0)
            {
                open.Confound = why;
            }
        }
    }

    internal static void Reset()
    {
        Open.Clear();
        hasYaw = false;
        hasPos = false;
        nextControl = -1f;
        System.Array.Clear(YawBins, 0, Bins);
        System.Array.Clear(DistBins, 0, Bins);
    }

    // Every frame while alive on a moon (telemetry on).
    internal static void Tick(PlayerControllerB player)
    {
        float dt = Time.deltaTime;
        float yaw = player.gameplayCamera.transform.eulerAngles.y;
        float yawDelta = hasYaw ? Mathf.Abs(Mathf.DeltaAngle(lastYaw, yaw)) : 0f;
        lastYaw = yaw;
        hasYaw = true;
        Vector3 here = player.transform.position;
        Vector3 step = hasPos ? here - lastPos : Vector3.zero;
        step.y = 0f;
        float dist = step.magnitude < 4f ? step.magnitude : 0f;
        lastPos = here;
        hasPos = true;
        if (dt > 0f)
        {
            smoothSpeed = Mathf.Lerp(smoothSpeed, dist / dt, 1f - Mathf.Exp(-dt / 0.3f));
        }

        // The 3 s history, in 0.1 s bins.
        binTime += dt;
        YawBins[bin] += yawDelta;
        DistBins[bin] += dist;
        if (binTime >= BinSeconds)
        {
            binTime = 0f;
            bin = (bin + 1) % Bins;
            YawBins[bin] = 0f;
            DistBins[bin] = 0f;
        }

        // Controls at random quiet moments.
        if (nextControl < 0f)
        {
            nextControl = Time.time + NextGap();
        }
        if (Time.time >= nextControl)
        {
            nextControl = Time.time + NextGap();
            if (SanityDirector.SecondsSinceLast >= ControlQuietBefore && !player.isPlayerDead && !player.quickMenuManager.isMenuOpen)
            {
                Vector3 bearing = PhantomPlacement.RandomBehindDirection(PhantomPlacement.LookDirection(player));
                Open.Add(NewProbe(player, false, "control", here + bearing * 8f));
            }
        }

        for (int i = Open.Count - 1; i >= 0; i--)
        {
            Probe probe = Open[i];
            float elapsed = Time.time - probe.T0;
            Update(player, probe, elapsed, yawDelta, dist);
            float window = probe.HasWalkie ? WalkieWindow : Window;
            if (elapsed >= window)
            {
                Emit(probe);
                Open.RemoveAt(i);
            }
        }
    }

    // The day ended or you died: open probes are cut short (logged as confounded, so nothing is silently lost).
    internal static void Flush(string why)
    {
        foreach (Probe probe in Open)
        {
            if (probe.Confound.Length == 0)
            {
                probe.Confound = why;
            }
            Emit(probe);
        }
        Open.Clear();
    }

    private static float NextGap()
    {
        return -Mathf.Log(Mathf.Max(0.0001f, Random.value)) * ControlMeanGap;
    }

    private static Probe NewProbe(PlayerControllerB player, bool real, string name, Vector3? source)
    {
        float yawSum = 0f, distSum = 0f;
        for (int i = 0; i < Bins; i++)
        {
            yawSum += YawBins[i];
            distSum += DistBins[i];
        }
        Probe probe = new Probe
        {
            Real = real,
            Name = name,
            T0 = Time.time,
            Directional = source.HasValue,
            Source = source ?? Vector3.zero,
            YawBefore = yawSum / (Bins * BinSeconds),
            SpeedBefore = distSum / (Bins * BinSeconds),
            SprintingAtStart = player.isSprinting,
            HasWalkie = CarriesPoweredWalkie(player),
            StartPos = player.transform.position,
        };
        if (probe.Directional)
        {
            probe.StartAngle = PhantomPlacement.AngleFromLook(player, probe.Source);
        }
        string place = player.isInsideFactory ? "inside" : player.isInHangarShipRoom ? "ship" : "outside";
        probe.Context = Invariant($"place={place} company={(SanityMeter.NearTeammate ? 1 : 0)} moving={(probe.SpeedBefore >= 1f ? 1 : 0)} tier={SanityMeter.Tier} fear={SanityMeter.Fear:F2} light={(LightOn(player) ? 1 : 0)} has_walkie={(probe.HasWalkie ? 1 : 0)}");
        return probe;
    }

    private static void Update(PlayerControllerB player, Probe probe, float elapsed, float yawDelta, float dist)
    {
        if (player.isPlayerDead)
        {
            probe.Confound = probe.Confound.Length == 0 ? "died" : probe.Confound;
            return;
        }
        if (probe.Confound.Length == 0)
        {
            if (SanityMeter.SecondsSinceRealFear < elapsed)
            {
                probe.Confound = "real fear";
            }
            else if (SanityMeter.SecondsSinceDamage < elapsed)
            {
                probe.Confound = "damage";
            }
        }

        if (elapsed <= AfterSeconds)
        {
            probe.YawAfter += yawDelta;
            probe.DistAfter += dist;
            if (probe.SpeedBefore >= StopFrom && smoothSpeed <= StopTo)
            {
                probe.Stopped = true;
            }
            if (!probe.SprintingAtStart && player.isSprinting)
            {
                probe.Sprinted = true;
            }
        }
        if (elapsed <= Window)
        {
            probe.Scan |= InputWatch.Pressed("PingScan");
            probe.Use |= InputWatch.Pressed("Interact");
            if (probe.Directional && !probe.Turned && probe.StartAngle >= TurnStartMin
                && PhantomPlacement.AngleFromLook(player, probe.Source) <= TurnCone)
            {
                probe.Turned = true;
                probe.TurnTime = elapsed;
            }
            if (probe.Directional && !probe.Moved && elapsed >= 4f)
            {
                probe.Moved = true;
                Vector3 awayDir = PhantomPlacement.Flat(probe.StartPos - probe.Source);
                Vector3 moved = player.transform.position - probe.StartPos;
                moved.y = 0f;
                float along = Vector3.Dot(moved, awayDir);
                probe.Away = along >= MoveMetres;
                probe.Toward = along <= -MoveMetres;
            }
        }
        if (probe.HasWalkie && player.speakingToWalkieTalkie)
        {
            probe.Walkie = true;
        }
    }

    private static void Emit(Probe probe)
    {
        Text.Clear();
        Text.Append("kind=").Append(probe.Real ? "real" : "control").Append(" name=").Append(probe.Name)
            .Append(" dir=").Append(probe.Directional ? 1 : 0);
        if (probe.Directional)
        {
            Text.Append(Invariant($" start_angle={probe.StartAngle:F0} turned={(probe.Turned ? 1 : 0)} turn_s={probe.TurnTime:F1} away={(probe.Away ? 1 : 0)} toward={(probe.Toward ? 1 : 0)}"));
        }
        Text.Append(Invariant($" yaw_b={probe.YawBefore:F0} yaw_a={probe.YawAfter / AfterSeconds:F0} v_b={probe.SpeedBefore:F1} v_a={probe.DistAfter / AfterSeconds:F1}"))
            .Append(Invariant($" stopped={(probe.Stopped ? 1 : 0)} sprinted={(probe.Sprinted ? 1 : 0)} scan={(probe.Scan ? 1 : 0)} use={(probe.Use ? 1 : 0)} walkie={(probe.Walkie ? 1 : 0)}"))
            .Append(' ').Append(probe.Context)
            .Append(" confound=").Append(probe.Confound.Length == 0 ? "none" : probe.Confound.Replace(' ', '_'));
        Telemetry.Event("REACTION", probe.Real ? 1f : 0f, Text.ToString());
        if (probe.StatsSound != null)
        {
            DayStats.SoundReaction(probe.StatsSound, probe.Directional, probe.Turned, probe.Stopped, probe.Confound.Length == 0);
        }
    }

    private static bool CarriesPoweredWalkie(PlayerControllerB player)
    {
        if (player.ItemSlots == null)
        {
            return false;
        }
        foreach (GrabbableObject item in player.ItemSlots)
        {
            if (item is WalkieTalkie walkie && walkie.isBeingUsed)
            {
                return true;
            }
        }
        return false;
    }

    internal static bool LightOn(PlayerControllerB player)
    {
        if (player.helmetLight != null && player.helmetLight.enabled)
        {
            return true;
        }
        return player.currentlyHeldObjectServer is FlashlightItem flashlight && flashlight.isBeingUsed;
    }

    private static PlayerControllerB? Local()
    {
        GameNetworkManager networkManager = GameNetworkManager.Instance;
        return networkManager != null ? networkManager.localPlayerController : null;
    }
}
