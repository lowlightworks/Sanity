using System.Collections.Generic;
using System.Text;
using GameNetcodeStuff;
using Sanity.Symptoms;
using UnityEngine;
using static System.FormattableString;

namespace Sanity;

// How this player plays (the baseline every reaction is judged against, and what a replay
// needs to project the next session), and how often each armed symptom's moment comes up whether or not it's armed (so
// different weights and waits can be projected without playing). Both are per day, written just before DAY_END.
internal static class Insights
{
    // ---- Profile (per frame counters) ----
    private static float alive, inside, outside, ship, aloneInside, aloneOutside, sprint, distance;
    private static float turnInside, turnOutside, turnShip, lightInside, walkieCarried, still;
    private static int scans, uses, walkiePresses, trips;
    private static readonly List<float> TripLengths = new List<float>();
    private static float tripStart = -1f;
    private static float firstEntry = -1f;
    private static float lastFraction;
    private static bool wasInside, wasTalking;
    private static float lastYaw;
    private static bool hasYaw;
    private static Vector3 lastPos;
    private static bool hasPos;

    // ---- Opportunities ----
    private static readonly Dictionary<string, int> DoorHovers = new Dictionary<string, int>();
    private static InteractTrigger? lastTrigger;
    private static int itemMoments, lightLooks, longExits, longExitsBelievable;
    private static readonly int[] ItemByTier = new int[4];
    private static readonly int[] LightByTier = new int[4];
    private static readonly int[] DoorByTier = new int[4];
    private static readonly int[] ExitByTier = new int[4];
    private static float nextItemScan, nextLightScan;
    private static GrabbableObject? lastLooked;
    private static bool lastInReach;
    private static bool lookingAtLight;

    private static readonly StringBuilder Text = new StringBuilder(600);

    internal static void Reset()
    {
        alive = inside = outside = ship = aloneInside = aloneOutside = sprint = distance = 0f;
        turnInside = turnOutside = turnShip = lightInside = walkieCarried = still = 0f;
        scans = uses = walkiePresses = trips = 0;
        TripLengths.Clear();
        tripStart = firstEntry = -1f;
        lastFraction = 0f;
        wasInside = wasTalking = hasYaw = hasPos = false;
        DoorHovers.Clear();
        lastTrigger = null;
        itemMoments = lightLooks = longExits = longExitsBelievable = 0;
        System.Array.Clear(ItemByTier, 0, 4);
        System.Array.Clear(LightByTier, 0, 4);
        System.Array.Clear(DoorByTier, 0, 4);
        System.Array.Clear(ExitByTier, 0, 4);
        lastLooked = null;
        lookingAtLight = false;
    }

    // Every frame while alive on a moon (telemetry on).
    internal static void Tick(PlayerControllerB player, float dayTime)
    {
        float dt = Time.deltaTime;
        int tier = (int)SanityMeter.Tier;
        bool isInside = player.isInsideFactory;
        bool isShip = player.isInHangarShipRoom;
        alive += dt;
        lastFraction = SanityMeter.DayFraction;

        float yaw = player.gameplayCamera.transform.eulerAngles.y;
        float turn = hasYaw ? Mathf.Abs(Mathf.DeltaAngle(lastYaw, yaw)) : 0f;
        lastYaw = yaw;
        hasYaw = true;
        Vector3 here = player.transform.position;
        Vector3 step = hasPos ? here - lastPos : Vector3.zero;
        step.y = 0f;
        float moved = step.magnitude < 4f ? step.magnitude : 0f;
        lastPos = here;
        hasPos = true;
        distance += moved;
        if (dt > 0f && moved / dt < 0.3f)
        {
            still += dt;
        }

        if (isInside)
        {
            inside += dt;
            turnInside += turn;
            if (!SanityMeter.NearTeammate)
            {
                aloneInside += dt;
            }
            if (Reactions.LightOn(player))
            {
                lightInside += dt;
            }
        }
        else if (isShip)
        {
            ship += dt;
            turnShip += turn;
        }
        else
        {
            outside += dt;
            turnOutside += turn;
            if (!SanityMeter.NearTeammate)
            {
                aloneOutside += dt;
            }
        }
        if (player.isSprinting)
        {
            sprint += dt;
        }
        if (InputWatch.Pressed("PingScan"))
        {
            scans++;
        }
        if (InputWatch.Pressed("Interact"))
        {
            uses++;
        }
        bool talking = player.speakingToWalkieTalkie;
        if (talking && !wasTalking)
        {
            walkiePresses++;
        }
        wasTalking = talking;
        if (CarriesWalkie(player))
        {
            walkieCarried += dt;
        }

        // Trips inside, and long-trip exits (lost time's moment).
        if (isInside && !wasInside)
        {
            trips++;
            tripStart = alive;
            if (firstEntry < 0f)
            {
                firstEntry = dayTime;
            }
        }
        if (!isInside && wasInside && tripStart >= 0f)
        {
            float length = alive - tripStart;
            TripLengths.Add(length);
            if (length >= Symptoms.LostTimeSymptom.MinInsideSeconds && !isShip)
            {
                longExits++;
                ExitByTier[tier]++;
                if (Symptoms.LostTimeSymptom.LateEnough)
                {
                    longExitsBelievable++;
                }
            }
        }
        wasInside = isInside;

        // Doors: each new hover of a door counts once, with what would stop a fake lock.
        InteractTrigger? trigger = player.hoveringOverTrigger;
        if (trigger != lastTrigger)
        {
            lastTrigger = trigger;
            if (trigger != null && isInside)
            {
                string verdict = FakeLockedDoorSymptom.Assess(player, trigger);
                if (verdict != "not a door")
                {
                    string key = verdict.Length == 0 ? "would fire" : verdict;
                    DoorHovers[key] = DoorHovers.TryGetValue(key, out int n) ? n + 1 : 1;
                    if (verdict.Length == 0)
                    {
                        DoorByTier[tier]++;
                    }
                }
            }
        }

        // Items: an item comes in reach (+1 m) where you look, and wasn't a moment ago (4 times a second).
        if (Time.time >= nextItemScan && !isShip)
        {
            nextItemScan = Time.time + 0.25f;
            GrabbableObject? looked = VanishingItemSymptom.LookedAtItem(player, out float reach, out _);
            bool inReach = looked != null && reach <= player.grabDistance + VanishingItemSymptom.Lead;
            if (inReach && !(looked == lastLooked && lastInReach))
            {
                itemMoments++;
                ItemByTier[tier]++;
            }
            lastLooked = looked;
            lastInReach = inReach;
        }

        // Lights: you start looking at a powered light (twice a second, inside with power).
        if (Time.time >= nextLightScan && isInside)
        {
            nextLightScan = Time.time + 0.5f;
            RoundManager roundManager = RoundManager.Instance;
            bool looking = roundManager != null && FlickerSymptom.FacilityHasPower(roundManager)
                           && FlickerSymptom.NearestVisibleLight(player, roundManager, out _) >= 0f;
            if (looking && !lookingAtLight)
            {
                lightLooks++;
                LightByTier[tier]++;
            }
            lookingAtLight = looking;
        }
    }

    // DAY_PROFILE and DAY_OPPORTUNITIES (called by Telemetry just before DAY_END).
    internal static void WriteDay(int day)
    {
        if (alive <= 1f)
        {
            return;
        }
        float minutes = alive / 60f;
        TripLengths.Sort();
        float medianTrip = TripLengths.Count > 0 ? TripLengths[TripLengths.Count / 2] : 0f;
        Text.Clear();
        Text.Append(Invariant($"alive={alive:F0}s inside={Share(inside)} outside={Share(outside)} ship={Share(ship)} "))
            .Append(Invariant($"alone_inside={Share(aloneInside, inside)} alone_outside={Share(aloneOutside, outside)} "))
            .Append(Invariant($"speed={distance / Mathf.Max(1f, alive):F2} still={Share(still)} sprint={Share(sprint)} "))
            .Append(Invariant($"turn_inside={turnInside / Mathf.Max(1f, inside):F0} turn_outside={turnOutside / Mathf.Max(1f, outside):F0} turn_ship={turnShip / Mathf.Max(1f, ship):F0} "))
            .Append(Invariant($"scans_min={scans / minutes:F2} use_min={uses / minutes:F2} walkie_min={walkiePresses / minutes:F2} walkie_carried={Share(walkieCarried)} "))
            .Append(Invariant($"light_inside={Share(lightInside, inside)} trips={trips} trip_median={medianTrip:F0}s first_entry={firstEntry:F0}s left_at={lastFraction:F2}"));
        Telemetry.Event("DAY_PROFILE", day, Text.ToString());

        Text.Clear();
        Text.Append(Invariant($"minutes={minutes:F1} doors"));
        foreach (KeyValuePair<string, int> pair in DoorHovers)
        {
            Text.Append(' ').Append(pair.Key.Replace(' ', '_')).Append('=').Append(pair.Value);
        }
        Text.Append(Invariant($" | items={itemMoments} lights={lightLooks} long_exits={longExits} long_exits_after_noon={longExitsBelievable}"))
            .Append(" | by tier C/U/D/B: door ").Append(Join(DoorByTier)).Append(" item ").Append(Join(ItemByTier))
            .Append(" light ").Append(Join(LightByTier)).Append(" exit ").Append(Join(ExitByTier));
        Telemetry.Event("DAY_OPPORTUNITIES", day, Text.ToString());
    }

    private static string Share(float part, float whole = -1f)
    {
        float of = whole < 0f ? alive : whole;
        return of > 0f ? Invariant($"{part / of:F2}") : "0";
    }

    private static string Join(int[] values)
    {
        return string.Join("/", values);
    }

    private static bool CarriesWalkie(PlayerControllerB player)
    {
        if (player.ItemSlots == null)
        {
            return false;
        }
        foreach (GrabbableObject item in player.ItemSlots)
        {
            if (item is WalkieTalkie)
            {
                return true;
            }
        }
        return false;
    }
}
