using GameNetcodeStuff;
using UnityEngine;
using static System.FormattableString;

namespace Sanity;

// How far gone the player is. Symptoms use this to decide what may happen.
internal enum SanityTier
{
    Calm,       // the lines between tiers are set in SanityMeter.TierBoundaries
    Uneasy,
    Disturbed,
    Breaking,
}

internal static class SanityMeter
{
    // ---- Tuning: steady rates (per second) ----
    private const float InsideDrain = 0.36f;
    private const float SpiralMaxMultiplier = 1.5f;   // drain multiplier after a long time inside
    private const float InsideDrainCap = 0.495f;      // the inside drain never goes above this (alone it hits it at ~3 min)
    private const float SpiralSeconds = 240f;          // time inside to reach that maximum
    private const float SpiralDecaySeconds = 15f;      // after leaving, the build-up fades to nothing over this long
    private const float SpiralDecayCurve = 3f;         // shape of that fade: holds at first, drops fastest at the end
    private const float PowerOffInsideMultiplier = 1.75f; // the apparatus is out: the facility is dark for good
    private const float PowerOffDropInside = 10f;      // the moment it happens: the lights die around you, the alarm
    private const float PowerOffDropElsewhere = 4f;    // outside or in the ship: only the alarm
    private const float PowerOffFallbackSeconds = 3f;  // no warning seen this long after the power cut: start anyway
    private const float CompanyDrainMultiplier = 0.5f;
    private const float OutsideDrain = 0.1f;           // outside the facility and the ship, any time of day

    // Lobby size: with fewer players you're alone more of the time, so the drains that company softens (inside,
    // outside, night) are scaled by how many players were in the game when the day started, fixed for that day.
    // One rule for any lobby size, built so every size drains about the same ON AVERAGE as the anchor lobby:
    //   chance that a given teammate is near you      p = TeammateNearChance (0.2: fits the playtest data)
    //   share of time with company, n players         f(n) = 1 - (1 - p)^(n - 1)        (solo 0, 3 players 0.36)
    //   drain left after company softens it           D(n) = 1 - (1 - CompanyDrainMultiplier) x f(n)
    //   lobby multiplier                               LobbyAnchorMultiplier x D(anchor) / D(n), kept to 0.6..1.2
    // Gives 1: 0.90, 2: 1.00, 3: 1.10, 4: 1.19, 5: 1.20 (cap) ... Only the anchor is tuned to data: with the reliefs
    // (SanityReliefs) and the lines at 75/52/25, two playtest sessions replay at 35/31/22/11 and 34/29/25/12 with 1.1,
    // against the target 35/28/22/15 (the reliefs lift you back up, so the drains can be stronger). Fear is not scaled.
    private const float TeammateNearChance = 0.2f;
    private const int LobbyAnchorPlayers = 3;
    private const float LobbyAnchorMultiplier = 1.1f;
    private const float LobbyMultiplierMin = 0.6f;
    private const float LobbyMultiplierMax = 1.2f;

    // Ship gain: daytime values slide to night values between Sundown and Midnight.
    private const float ShipGainDayOpen = 0.35f;
    private const float ShipGainDayClosed = 0.5f;
    private const float ShipGainNightOpen = 0.1f;
    private const float ShipGainNightClosed = 0.3f;
    private const float CompanyGainMultiplier = 1.1f;
    private const float ShipRampSeconds = 30f;         // gain builds from 0 to full over this long after boarding
    private const float ShipRecoveryCap = 85f;         // the ship never lifts you above this
    private const float ShipBreakingBoost = 2.0f;      // gain multiplier at 0 sanity, easing to 1 at the Breaking line

    private const float NightDrainMax = 0.4f;
    private const float SundownStart = 0.63f;          // the game's own day stages
    private const float MidnightStart = 0.9f;          // (TimeOfDay.GetDayPhase)

    private const float FearDrainMax = 0.5f;           // extra drain at full fear
    private const float FearDrainStart = 0.1f;         // the game's own "tension" threshold

    private const float NearbyEnterRadius = 10f;       // a living teammate this close counts as company
    private const float NearbyLeaveRadius = 14f;       // and keeps counting until they're further than this
    private const float NearbyMaxHeight = 4f;          // more than this above or below you (another floor) never counts

    // ---- Tuning: instant drops ----
    private const float DamageToSanity = 0.2f;         // sanity lost per point of health lost
    private const float DamageMaxDrop = 10f;           // most sanity a single hit can cost
    private const float FallDamageMultiplier = 0.5f;   // a fall you survive is a shock, not a horror
    private const float TeammateDeathDrop = 10f;       // died within TeammateDeathRadius of you
    private const float TeammateDeathRadius = 5f;
    private const float TeammateDeathShipDrop = 5f;    // died further away while you were in the ship
    private const float TeammateDeathFarDrop = 2f;     // died anywhere else
    private const float FearJumpDrop = 5f;
    private const float FearJumpThreshold = 0.4f;      // the game's own "high fear" threshold
    private const float FearJumpCooldown = 8f;
    private const float RevivedSanity = 85f;           // brought back mid-round (Lategame Upgrades' interns): a fresh start at the ship's level

    // ---- Tuning: tiers ----
    // Boundaries below Calm, Uneasy and Disturbed. Dropping below one moves you down a tier.
    // The ONLY place the tier lines are set: the director and debug tools read them from here.
    // The Uneasy/Disturbed line is 52 (replay-fitted): bounces and regroups land in the upper tiers, which would
    // otherwise grow Disturbed at Uneasy's expense.
    private static readonly float[] TierBoundaries = { 75f, 52f, 25f };
    // To move back up, you must climb this far above the boundary (stops flickering at the edge).
    private const float TierDeadband = 3f;   // small, so a relief's climb of 10-16 counts
    internal static float CalmLine => TierBoundaries[0];

    // Every tuning number in one line, for the telemetry session row (so each CSV says which build made it).
    internal static string TuningSummary => Invariant(
        $"tiers {TierBoundaries[0]}/{TierBoundaries[1]}/{TierBoundaries[2]} deadband {TierDeadband}; ") + Invariant(
        $"inside {InsideDrain} x{SpiralMaxMultiplier} over {SpiralSeconds}s cap {InsideDrainCap} fade {SpiralDecaySeconds}s power off x{PowerOffInsideMultiplier} drop {PowerOffDropInside}/{PowerOffDropElsewhere}; outside {OutsideDrain}; night {NightDrainMax}; fear {FearDrainMax}; ") + Invariant(
        $"company x{CompanyDrainMultiplier} near {NearbyEnterRadius}/{NearbyLeaveRadius}m height {NearbyMaxHeight}m; ") + Invariant(
        $"lobby near chance {TeammateNearChance} anchor {LobbyAnchorPlayers}p x{LobbyAnchorMultiplier} range {LobbyMultiplierMin}-{LobbyMultiplierMax} (1-8 players {LobbyTable}); ") + Invariant(
        $"ship day {ShipGainDayOpen}/{ShipGainDayClosed} night {ShipGainNightOpen}/{ShipGainNightClosed} company x{CompanyGainMultiplier} ramp {ShipRampSeconds}s cap {ShipRecoveryCap} breaking x{ShipBreakingBoost}; ") + Invariant(
        $"damage {DamageToSanity}/hp cap {DamageMaxDrop} falls x{FallDamageMultiplier}; deaths {TeammateDeathDrop}/{TeammateDeathShipDrop}/{TeammateDeathFarDrop}; fear jump {FearJumpDrop}; revived {RevivedSanity}; ") + SanityReliefs.TuningSummary;
    internal static float BreakingLine => TierBoundaries[TierBoundaries.Length - 1];

    // ---- State ----
    internal static float Value = 100f;
    internal static float Rate;
    internal static SanityTier Tier = SanityTier.Calm;
    internal static bool OnMoon;
    internal static bool PowerOff;  // the apparatus was pulled: the facility's power is off for the rest of the day
    private static float powerCutSeenAt = -1f;
    internal static bool Frozen;    // debug: when true, nothing changes the value

    // Players in the game when today started, and the drain multiplier that gives (both fixed for the day).
    internal static int PlayersAtStart = 1;
    internal static float LobbyMultiplier = 1f;

    // Real fear tracking, for phantom fear's "no real fear recently" rule.
    internal static float SecondsSinceRealFear = 9999f;
    internal static float SecondsSinceDamage = 9999f;   // real damage, for phantom hit's quiet rule
    internal static bool PhantomJump;   // true only during our own phantom fear call
    private static float lastFear;

    // This frame's inputs, kept for telemetry and the readout.
    internal static bool Inside;
    internal static bool InShip;
    internal static bool NearTeammate;
    internal static bool DoorsClosed;
    internal static float DayFraction;
    internal static float Fear;
    internal static float NearestTeammateDistance = -1f;   // flat distance to the nearest living teammate, -1 = none
    internal static float NearestTeammateHeight;           // how far above (+) or below (-) you they are
    internal static float TimeInside => spiralSeconds;   // the spiral's clock: time inside, fading after you leave

    // Each factor's share of the final rate (per second, after company and dials).
    // Gains minus drains = Rate, so telemetry can show exactly what caused a change.
    internal static float PartInside;
    internal static float PartNight;
    internal static float PartFear;
    internal static float PartShip;
    internal static float PartOutside;
    private static float spiralSeconds;          // grows inside; after leaving, fades back to 0
    private static float spiralAtExit;           // the spiral when you last stepped out
    private static float secondsOutsideFacility;
    private static float secondsInShip;
    private static CauseOfDeath pendingDamageCause = CauseOfDeath.Unknown;
    private static int lastHealth = -1;
    private static bool[] wasDead = System.Array.Empty<bool>();
    private static float fearCooldown;
    private static bool dayStarted;              // the first frame of each day sets the lobby multiplier
    private static bool wasDeadMyself;           // last frame: was the local player dead (to spot a revive)

    // Called once per frame for the local player.
    internal static void Tick(PlayerControllerB player)
    {
        StartOfRound startOfRound = StartOfRound.Instance;
        TimeOfDay timeOfDay = TimeOfDay.Instance;
        if (startOfRound == null || timeOfDay == null)
        {
            OnMoon = false;
            return;
        }

        // Not on a moon (in orbit, or the day hasn't started): sit at full, calculate nothing.
        if (startOfRound.inShipPhase || !timeOfDay.currentDayTimeStarted)
        {
            OnMoon = false;
            PowerOff = false;
            powerCutSeenAt = -1f;
            Weather.Reset();
            DayLength.Reset();
            SanityReliefs.Reset();
            Value = 100f;
            Rate = 0f;
            Tier = SanityTier.Calm;
            ResetLocationTrackers();
            fearCooldown = 0f;
            lastHealth = player.health;
            SecondsSinceRealFear = 9999f;   // each day starts with no recent real fear
            SecondsSinceDamage = 9999f;
            lastFear = startOfRound.fearLevel;
            RecordDeadStates(startOfRound.allPlayerScripts);
            dayStarted = false;
            wasDeadMyself = player.isPlayerDead;
            return;
        }

        OnMoon = true;
        Weather.Tick(startOfRound);
        DayLength.Tick(timeOfDay);

        // The apparatus: normally the radiation warning starts it (ApparatusPatch, OnPowerOff). If something stops
        // that warning, the power being off for good starts it a few seconds later instead.
        RoundManager roundManager = RoundManager.Instance;
        if (!PowerOff && roundManager != null && roundManager.powerOffPermanently)
        {
            if (powerCutSeenAt < 0f)
            {
                powerCutSeenAt = Time.time;
            }
            else if (Time.time - powerCutSeenAt >= PowerOffFallbackSeconds)
            {
                OnPowerOff("power off for good, no warning seen");
            }
        }

        // First frame of the day: how many players are here sets today's lobby multiplier (it stays for the day).
        if (!dayStarted)
        {
            dayStarted = true;
            PlayersAtStart = startOfRound.connectedPlayersAmount + 1;
            LobbyMultiplier = LobbyMultiplierFor(PlayersAtStart);
        }

        if (fearCooldown > 0f)
        {
            fearCooldown -= Time.deltaTime;
        }

        // Fear only ever rises from real monsters/sightings (jumps or gradual rises) or from our phantom
        // spike, and ours updates lastFear itself. So any rise seen here is real.
        SecondsSinceRealFear += Time.deltaTime;
        SecondsSinceDamage += Time.deltaTime;
        float fearNow = startOfRound.fearLevel;
        if (fearNow > lastFear + 0.001f)
        {
            SecondsSinceRealFear = 0f;
        }
        lastFear = fearNow;

        // Master switch off: hold sanity at 100 and do nothing else, but keep the trackers current
        // so switching back on doesn't fire stale damage or death drops.
        if (!Plugin.ModEnabled.Value)
        {
            Value = 100f;
            Rate = 0f;
            Tier = SanityTier.Calm;
            PartInside = 0f;
            PartNight = 0f;
            PartFear = 0f;
            PartShip = 0f;
            PartOutside = 0f;
            ResetLocationTrackers();
            SanityReliefs.Reset();
            lastHealth = player.health;
            RecordDeadStates(startOfRound.allPlayerScripts);
            wasDeadMyself = player.isPlayerDead;
            return;
        }

        // Brought back to life mid-round (Lategame Upgrades' interns): a fresh start at RevivedSanity.
        // The game's own revive after a round happens in orbit, so it never gets here.
        if (wasDeadMyself && !player.isPlayerDead)
        {
            Revive();
        }
        wasDeadMyself = player.isPlayerDead;

        // Instant drops (these also keep their trackers up to date while you're dead).
        CheckDamage(player);
        CheckTeammateDeaths(player, startOfRound.allPlayerScripts);

        Tier = UpdateTier(Tier, Value);

        // Dead: freeze where it was.
        if (player.isPlayerDead)
        {
            Rate = 0f;
            return;
        }

        Inside = player.isInsideFactory;
        InShip = player.isInHangarShipRoom;
        NearTeammate = CheckNearTeammate(player, startOfRound.allPlayerScripts, NearTeammate);
        DoorsClosed = startOfRound.hangarDoorsClosed;
        Fear = startOfRound.fearLevel;

        // The spiral's clock counts up inside and fades after you leave; the ship's ramp counts time aboard.
        UpdateSpiral(Time.deltaTime);
        secondsInShip = InShip ? secondsInShip + Time.deltaTime : 0f;

        // How far through the day we are: 0 = start, 1 = midnight.
        DayFraction = timeOfDay.totalTime > 0f ? timeOfDay.currentDayTime / timeOfDay.totalTime : 0f;

        Rate = GetRate(Inside, InShip, NearTeammate, DoorsClosed, spiralSeconds, DayFraction, Fear, secondsInShip, Value);
        if (!Frozen)
        {
            float next = Value + Rate * Time.deltaTime;
            // The ship is the only gain, and it never lifts you past its cap.
            if (Rate > 0f && Value <= ShipRecoveryCap)
            {
                next = Mathf.Min(next, ShipRecoveryCap);
            }

            Value = Mathf.Clamp(next, 0f, 100f);
        }

        // Ways back up outside the ship (regroup, breather, find), then the bounce out of a long Breaking spell.
        SanityReliefs.Tick(player, Time.deltaTime);
        Tier = UpdateTier(Tier, Value);
        if (SanityReliefs.AfterTier())
        {
            Tier = UpdateTier(Tier, Value);
        }
    }

    // An instant gain from a relief (regroup, breather, find, bounce). Returns how much was actually gained.
    internal static float Relief(float amount)
    {
        if (Frozen || !OnMoon || amount <= 0f)
        {
            return 0f;
        }
        float before = Value;
        Value = Mathf.Min(100f, Value + amount);
        return Value - before;
    }

    // The lobby rule (see LobbyAnchorMultiplier): the drain multiplier for a game with this many players.
    internal static float LobbyMultiplierFor(int players)
    {
        float multiplier = LobbyAnchorMultiplier * ExpectedDrainShare(LobbyAnchorPlayers) / ExpectedDrainShare(players);
        return Mathf.Clamp(multiplier, LobbyMultiplierMin, LobbyMultiplierMax);
    }

    // On average, how much of the full drain is left once company has softened it, in a game with this many players.
    private static float ExpectedDrainShare(int players)
    {
        float timeWithCompany = 1f - Mathf.Pow(1f - TeammateNearChance, Mathf.Max(1, players) - 1);
        return 1f - (1f - CompanyDrainMultiplier) * timeWithCompany;
    }

    // The rule's results for 1 to 8 players, for the telemetry session row ("0.6/0.66/...").
    private static string LobbyTable
    {
        get
        {
            string[] values = new string[8];
            for (int players = 1; players <= values.Length; players++)
            {
                values[players - 1] = LobbyMultiplierFor(players).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            }
            return string.Join("/", values);
        }
    }

    // A revived player starts again at RevivedSanity, with fresh location trackers (no spiral, no ship ramp).
    private static void Revive()
    {
        Symptoms.PhantomHitSymptom.ClearLost();
        if (!Frozen)
        {
            Value = RevivedSanity;
        }
        Tier = UpdateTier(SanityTier.Calm, Value);
        ResetLocationTrackers();
        Telemetry.Event("REVIVED", Value, Invariant($"brought back mid-round; sanity {Value:F0}"));
    }

    // Called right after our phantom spike, so the rise isn't mistaken for real fear next frame.
    internal static void NotePhantomJump()
    {
        StartOfRound startOfRound = StartOfRound.Instance;
        if (startOfRound != null)
        {
            lastFear = startOfRound.fearLevel;
        }
    }

    // Debug menu: jump straight to a value. The tier is worked out fresh (no deadband).
    internal static string DebugSetSanity(float value)
    {
        if (!OnMoon)
        {
            return "Only works on a moon (sanity is fixed at 100 in orbit).";
        }

        Value = Mathf.Clamp(value, 0f, 100f);
        Tier = UpdateTier(SanityTier.Calm, Value);
        Telemetry.Event("DEBUG", Value, Invariant($"set sanity {Value:F0}"));
        return Invariant($"Sanity set to {Value:F0} ({Tier}).");
    }

    // Move down a tier as soon as a boundary is crossed; move up only once clear of it by the deadband.
    private static SanityTier UpdateTier(SanityTier current, float value)
    {
        int tier = (int)current;

        // Going down (can skip tiers after a big instant drop).
        while (tier < TierBoundaries.Length && value < TierBoundaries[tier])
        {
            tier++;
        }

        // Going up.
        while (tier > 0 && value > TierBoundaries[tier - 1] + TierDeadband)
        {
            tier--;
        }

        return (SanityTier)tier;
    }

    // Called by the patch when the game makes fear jump up.
    internal static void OnFearJump(PlayerControllerB player, float oldFearLevel, float newFearLevel)
    {
        // Sanity is frozen while dead, not tracked off a moon, and held at 100 with the mod off.
        if (!OnMoon || player.isPlayerDead || !Plugin.ModEnabled.Value)
        {
            return;
        }

        string jump = Invariant($"fear {oldFearLevel:F2}->{newFearLevel:F2}");

        // Our own phantom spike: logged, but no instant drop (the normal fear drain still applies).
        if (PhantomJump)
        {
            Telemetry.Event("FEAR_JUMP_PHANTOM", newFearLevel, jump + " (phantom, no drop)");
            return;
        }

        if (newFearLevel < FearJumpThreshold)
        {
            Telemetry.Event("FEAR_JUMP_SMALL", newFearLevel, jump + " (below threshold, no drop)");
            return;
        }

        if (fearCooldown > 0f)
        {
            Telemetry.Event("FEAR_JUMP_COOLDOWN", newFearLevel, jump + " (cooldown, no drop)");
            return;
        }

        fearCooldown = FearJumpCooldown;
        Symptoms.PhantomSoundSymptom.NoteEcho(player);
        float lost = Drop(FearJumpDrop);
        Telemetry.Event("FEAR_JUMP", newFearLevel, jump + Invariant($" sanity -{lost:F1}"));
    }

    private static float GetRate(bool inside, bool inShip, bool nearTeammate, bool doorsClosed,
                                 float spiral, float dayFraction, float fearLevel, float shipSeconds, float sanity)
    {
        // 0 before Sundown, rising to 1 at Midnight.
        float night = Mathf.InverseLerp(SundownStart, MidnightStart, dayFraction);
        // Some weathers start partway into the night: both night drain and ship gain treat it as at least this late.
        night = Mathf.Max(night, Weather.NightHeadStart);

        // Inside: base drain, growing with the spiral's clock, scaled by weather and by the power being off for good
        // (the apparatus pulled). Capped below, after company; the cap scales the same way.
        float insideScale = Weather.Inside * (PowerOff ? PowerOffInsideMultiplier : 1f);
        PartInside = inside ? InsideDrain * Mathf.Lerp(1f, SpiralMaxMultiplier, spiral / SpiralSeconds) * insideScale : 0f;

        // Outside (not in the facility, not in the ship): a small drain at any time of day.
        bool outside = !inside && !inShip;
        PartOutside = outside ? OutsideDrain * Weather.Outside : 0f;

        // Night: applies outside, and in the ship while the doors are open. Stacks on the outside drain.
        bool exposedToNight = !inside && (!inShip || !doorsClosed);
        PartNight = exposedToNight ? NightDrainMax * night * Weather.Outside : 0f;

        // Steadying after a bounce: these three drains run at half for a while (SanityReliefs).
        float steady = SanityReliefs.DrainMultiplier;
        PartInside *= steady;
        PartOutside *= steady;
        PartNight *= steady;

        // Fear: the game's own fear level adds drain.
        PartFear = FearDrain(fearLevel);

        // Ship: the only source of gain. Doors matter more as night comes. It builds up over the first
        // seconds aboard (drop-offs barely count), is a little stronger when Breaking, and stops at the cap.
        PartShip = 0f;
        if (inShip && sanity < ShipRecoveryCap)
        {
            float dayGain = doorsClosed ? ShipGainDayClosed : ShipGainDayOpen;
            float nightGain = doorsClosed ? ShipGainNightClosed : ShipGainNightOpen;
            float ramp = Mathf.Clamp01(shipSeconds / ShipRampSeconds);
            float boost = Mathf.Lerp(1f, ShipBreakingBoost, Mathf.InverseLerp(BreakingLine, 0f, sanity));
            PartShip = Mathf.Lerp(dayGain, nightGain, night) * ramp * boost;
        }

        // Company softens inside, outside and night drain, and slightly boosts ship gain. It does NOT soften fear.
        if (nearTeammate)
        {
            PartInside *= CompanyDrainMultiplier;
            PartOutside *= CompanyDrainMultiplier;
            PartNight *= CompanyDrainMultiplier;
            PartShip *= CompanyGainMultiplier;
        }

        // The inside drain never exceeds its cap (scaled like the drain: weather, the power being off). Alone it
        // reaches it before the spiral ends; with company the cap doesn't bite, so grouped drain tops out at half the
        // uncapped value.
        PartInside = Mathf.Min(PartInside, InsideDrainCap * insideScale);

        // Lobby size: fewer players, more time alone, so these three drains are scaled by today's lobby multiplier.
        // (After the inside cap, like the dials, so the capped drain shrinks too.) Fear is left alone.
        PartInside *= LobbyMultiplier;
        PartOutside *= LobbyMultiplier;
        PartNight *= LobbyMultiplier;

        // Player-facing dials from the config.
        float drainDial = Plugin.DrainMultiplier.Value;
        float gainDial = Plugin.RecoveryMultiplier.Value;
        PartInside *= drainDial;
        PartOutside *= drainDial;
        PartNight *= drainDial;
        PartFear *= drainDial;
        PartShip *= gainDial;

        float drain = PartInside + PartOutside + PartNight + PartFear;
        return PartShip - drain;
    }

    // The spiral's clock. Inside it counts up. After you leave it fades from where it was to 0 over
    // SpiralDecaySeconds: slow at first, fastest at the end. A quick step outside keeps most of it.
    private static void UpdateSpiral(float dt)
    {
        if (Inside)
        {
            spiralSeconds += dt;
            spiralAtExit = Mathf.Min(spiralSeconds, SpiralSeconds);
            secondsOutsideFacility = 0f;
            return;
        }

        secondsOutsideFacility += dt;
        spiralSeconds = spiralAtExit * SpiralFade(secondsOutsideFacility / SpiralDecaySeconds);
    }

    // 1 at x = 0, 0 at x = 1. Holds near 1 early and drops fastest near the end (a mirrored exponential).
    private static float SpiralFade(float x)
    {
        if (x >= 1f)
        {
            return 0f;
        }

        float k = SpiralDecayCurve;
        return 1f - (Mathf.Exp(k * x) - 1f) / (Mathf.Exp(k) - 1f);
    }

    // Company: a living teammate close by on roughly the same floor. Once you have company it lasts a bit
    // further out (leave radius > enter radius), so walking at the edge doesn't flip it back and forth.
    // Also records the nearest teammate's distance and height for telemetry, whether or not they count.
    private static bool CheckNearTeammate(PlayerControllerB me, PlayerControllerB[] players, bool hadCompany)
    {
        NearestTeammateDistance = -1f;
        NearestTeammateHeight = 0f;
        if (players == null)
        {
            return false;
        }

        float radius = hadCompany ? NearbyLeaveRadius : NearbyEnterRadius;
        Vector3 here = me.transform.position;
        bool company = false;
        for (int i = 0; i < players.Length; i++)
        {
            PlayerControllerB other = players[i];
            if (other == null || other == me || !other.isPlayerControlled || other.isPlayerDead)
            {
                continue;
            }

            Vector3 offset = other.transform.position - here;
            float height = offset.y;
            offset.y = 0f;
            float flat = offset.magnitude;
            if (NearestTeammateDistance < 0f || flat < NearestTeammateDistance)
            {
                NearestTeammateDistance = flat;
                NearestTeammateHeight = height;
            }

            if (Mathf.Abs(height) <= NearbyMaxHeight && flat <= radius)
            {
                company = true;
            }
        }

        return company;
    }

    // Off a moon or with the mod off: forget everything about where you've been.
    private static void ResetLocationTrackers()
    {
        spiralSeconds = 0f;
        spiralAtExit = 0f;
        secondsOutsideFacility = 0f;
        secondsInShip = 0f;
        NearTeammate = false;
        NearestTeammateDistance = -1f;
        NearestTeammateHeight = 0f;
        pendingDamageCause = CauseOfDeath.Unknown;
    }

    // Loot recovery: an instant gain, not limited by the ship's cap. Returns how much was actually gained.
    internal static float LootGain(float amount)
    {
        if (Frozen || !OnMoon || amount <= 0f)
        {
            return 0f;
        }

        float before = Value;
        Value = Mathf.Min(100f, Value + amount);
        return Value - before;
    }

    // Called by the DamagePlayer patch just before the game damages us, so CheckDamage knows why.
    internal static void NoteDamageCause(CauseOfDeath cause)
    {
        pendingDamageCause = cause;
    }

    // 0 below the game's tension threshold, rising to FearDrainMax at full fear.
    private static float FearDrain(float fearLevel)
    {
        return FearDrainMax * Mathf.InverseLerp(FearDrainStart, 1f, fearLevel);
    }

    // Health went down since last frame? That's damage.
    private static void CheckDamage(PlayerControllerB player)
    {
        int health = player.health;
        CauseOfDeath cause = pendingDamageCause;
        pendingDamageCause = CauseOfDeath.Unknown;

        if (lastHealth >= 0 && health < lastHealth && !player.isPlayerDead)
        {
            int damage = lastHealth - health;
            SecondsSinceDamage = 0f;
            float amount = Mathf.Min(damage * DamageToSanity, DamageMaxDrop);
            if (cause == CauseOfDeath.Gravity)
            {
                amount *= FallDamageMultiplier;   // the game reports falls as Gravity
            }

            float lost = Drop(amount);
            Telemetry.Event("DAMAGE", damage, Invariant($"health {lastHealth}->{health} cause {cause} sanity -{lost:F1}"));
        }
        lastHealth = health;
    }

    // A teammate who was alive last frame is dead now? Costs most up close, some if you were in the ship
    // (watching it happen on the monitor or radio), a little anywhere else.
    private static void CheckTeammateDeaths(PlayerControllerB me, PlayerControllerB[] players)
    {
        if (players == null)
        {
            return;
        }

        // Player list changed size (someone joined, or a mod resized it): just re-record, no drops this frame.
        if (wasDead.Length != players.Length)
        {
            RecordDeadStates(players);
            return;
        }

        for (int i = 0; i < players.Length; i++)
        {
            PlayerControllerB other = players[i];
            if (other == null)
            {
                continue;
            }

            bool justDied = other.isPlayerDead && !wasDead[i];
            if (justDied && other != me)
            {
                float distance = Vector3.Distance(me.transform.position, other.placeOfDeath);
                Reactions.NoteConfound("teammate died");
                string who = Invariant($"{Telemetry.PlayerLabel(other)} ({other.causeOfDeath}) {distance:F0}m away");
                if (me.isPlayerDead)
                {
                    Telemetry.Event("TEAMMATE_DIED", distance, who);
                }
                else if (distance <= TeammateDeathRadius)
                {
                    float lost = Drop(TeammateDeathDrop);
                    Telemetry.Event("TEAMMATE_DIED_NEARBY", distance, who + Invariant($" sanity -{lost:F1}"));
                }
                else if (me.isInHangarShipRoom)
                {
                    float lost = Drop(TeammateDeathShipDrop);
                    Telemetry.Event("TEAMMATE_DIED_SHIP", distance, who + Invariant($" sanity -{lost:F1}"));
                }
                else
                {
                    float lost = Drop(TeammateDeathFarDrop);
                    Telemetry.Event("TEAMMATE_DIED", distance, who + Invariant($" sanity -{lost:F1}"));
                }
            }

            wasDead[i] = other.isPlayerDead;
        }
    }

    // Remember who is dead right now, so the next frame can spot new deaths.
    private static void RecordDeadStates(PlayerControllerB[] players)
    {
        if (players == null)
        {
            return;
        }

        if (wasDead.Length != players.Length)
        {
            wasDead = new bool[players.Length];
        }

        for (int i = 0; i < players.Length; i++)
        {
            wasDead[i] = players[i] != null && players[i].isPlayerDead;
        }
    }

    // The apparatus was pulled (anyone's), at the radiation warning: an instant drop (bigger inside, where the lights
    // just died), then the inside drain and its cap are PowerOffInsideMultiplier for the rest of the day.
    internal static void OnPowerOff(string why)
    {
        if (PowerOff || !OnMoon)
        {
            return;
        }
        PowerOff = true;
        GameNetworkManager networkManager = GameNetworkManager.Instance;
        PlayerControllerB? player = networkManager != null ? networkManager.localPlayerController : null;
        bool alive = player != null && !player.isPlayerDead;
        float lost = alive ? Drop(Inside ? PowerOffDropInside : PowerOffDropElsewhere) : 0f;
        Telemetry.Event("APPARATUS_PULLED", lost, Invariant(
            $"{why}: the facility's power is off for good ({(Inside ? "inside" : "outside")}) sanity -{lost:F1}; inside drain x{PowerOffInsideMultiplier} from now"));
    }

    // An instant drop, scaled by the drain dial. Returns how much sanity was actually lost.
    private static float Drop(float amount)
    {
        if (Frozen)
        {
            return 0f;
        }

        float before = Value;
        Value = Mathf.Clamp(Value - amount * Plugin.DrainMultiplier.Value, 0f, 100f);
        return before - Value;
    }
}