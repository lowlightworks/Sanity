using GameNetcodeStuff;
using UnityEngine;
using static System.FormattableString;

namespace Sanity.Symptoms;

// Facility lights flicker, and at lower tiers flashlights too.
// This calls the same game function the Ghost Girl uses (RoundManager.FlickerLights).
// The function only affects the computer that runs it, and we don't tell anyone else's game,
// so only this player sees it. It covers every facility light and every flashlight on this screen.
// Armed (a flicker nobody happens to be facing goes unnoticed): picked by the director inside the facility
// with the power on, it fires the moment a powered light is unmissable: you look at one (within 12 m, within 30
// degrees of the centre of your view), or you're standing in one's light (within 6 m and well inside its reach, any
// direction: a lit room counts even with the bulb off screen); nothing solid in between either way.
// Waits up to 2 minutes.
internal sealed class FlickerSymptom : ArmedSymptom
{
    // A powered light this close to your eyes, this near where you look, with nothing solid in between.
    private const float LightRange = 12f;
    private const float LookCone = 30f;
    // Or this close, in any direction, and within this share of the light's own range (well inside its lit area).
    private const float StandInRange = 6f;
    private const float StandInShare = 0.7f;
    // A bulb often sits inside its fixture or just above the ceiling surface, so a line straight to it can be
    // blocked by the ceiling a little short of it. So a light counts as in view if you can see the bulb
    // (ignoring anything within FixtureTolerance of it) OR the lit space LitSpaceDrop below it.
    private const float FixtureTolerance = 1f;
    private const float LitSpaceDrop = 1f;

    // The level's breaker box, remembered (finding it searches the whole scene), and when to look again if none.
    private const float BreakerSearchSeconds = 5f;
    private static BreakerBox? breakerBox;
    private static float nextBreakerSearch;

    internal override string Name => "Flicker";

    internal override string ConfigKey => "Flicker";

    internal override string Family => "sight";

    internal override SanityTier MinTier => SanityTier.Uneasy;

    internal override float RecoverySeconds => 60f;

    protected override float ExpireSeconds => 120f;

    internal override string Waits => "a light you look at or stand in";

    // Nothing lingers: the game's flicker runs on its own.
    protected override bool Showing => false;

    // Needs: inside the facility, the power on. The light you look at is the moment it waits for.
    protected override string CantArmReason(PlayerControllerB player)
    {
        // Facility lights are only around you inside, and only flicker if the facility has power.
        if (!player.isInsideFactory)
        {
            return "not inside";
        }

        RoundManager roundManager = RoundManager.Instance;
        if (roundManager == null)
        {
            return "no round";
        }
        if (!FacilityHasPower(roundManager))
        {
            return "no power";
        }
        if (roundManager.allPoweredLights == null || roundManager.allPoweredLights.Count == 0)
        {
            return "no light: none in this interior";
        }
        return "";
    }

    internal override string MomentStatus(PlayerControllerB player)
    {
        RoundManager roundManager = RoundManager.Instance;
        if (!player.isInsideFactory || roundManager == null || !FacilityHasPower(roundManager))
        {
            return "not inside a powered facility";
        }
        float distance = NearestVisibleLight(player, roundManager, out string reason, out bool standingIn);
        return distance >= 0f ? Invariant($"moment here: {(standingIn ? "standing in" : "looking at")} a light {distance:F1} m") : reason;
    }

    protected override bool TryFire(PlayerControllerB player, SanityTier tier)
    {
        RoundManager roundManager = RoundManager.Instance;
        if (!player.isInsideFactory || roundManager == null || !FacilityHasPower(roundManager))
        {
            return false;
        }
        float distance = NearestVisibleLight(player, roundManager, out _, out bool standingIn);
        if (distance < 0f)
        {
            return false;
        }
        Flick(player, tier, roundManager, distance, standingIn);
        return true;
    }

    // A chain (something approaching at Breaking): the lights go now, if there's power and a light in view.
    // "" = played, else why not.
    internal static string FlickNow(PlayerControllerB player, SanityTier tier)
    {
        RoundManager roundManager = RoundManager.Instance;
        if (!player.isInsideFactory || roundManager == null || !FacilityHasPower(roundManager))
        {
            return "no lit facility here";
        }
        float distance = NearestVisibleLight(player, roundManager, out string reason, out bool standingIn);
        if (distance < 0f)
        {
            return reason;
        }
        roundManager.FlickerLights(tier >= SanityTier.Disturbed, tier == SanityTier.Breaking);
        Telemetry.Event("FLICKER", distance, Invariant($"chained, {(standingIn ? "standing in" : "looking at")} a light {distance:F1}m away"));
        return "";
    }

    private void Flick(PlayerControllerB player, SanityTier tier, RoundManager roundManager, float light, bool standingIn)
    {
        // Uneasy: room lights only. Disturbed: flashlights stutter too. Breaking: flashlights cut out briefly.
        bool flickerFlashlights = tier >= SanityTier.Disturbed;
        bool disableFlashlights = tier == SanityTier.Breaking;
        roundManager.FlickerLights(flickerFlashlights, disableFlashlights);
        Telemetry.Event("FLICKER", light, Invariant($"{(standingIn ? "standing in" : "looking at")} a light {light:F1}m away"));
    }

    internal static float NearestVisibleLight(PlayerControllerB player, RoundManager roundManager, out string reason)
    {
        return NearestVisibleLight(player, roundManager, out reason, out _);
    }

    // Distance from your eyes to the nearest powered light that's unmissable right now, or -1: one you're looking at
    // (within LightRange and LookCone), or one you're standing in (within StandInRange and StandInShare of its range,
    // any direction; standingIn = true when that's the only kind found). Walls use the same layers the game itself
    // checks for "is there a wall in the way". reason: "" if one is found, otherwise why not (for CantPlayReason).
    internal static float NearestVisibleLight(PlayerControllerB player, RoundManager roundManager, out string reason, out bool standingIn)
    {
        standingIn = false;
        StartOfRound startOfRound = StartOfRound.Instance;
        if (startOfRound == null || player.gameplayCamera == null || roundManager.allPoweredLights == null)
        {
            reason = "no light: no light list";
            return -1f;
        }

        Vector3 eye = player.gameplayCamera.transform.position;
        Vector3 forward = player.gameplayCamera.transform.forward;
        int walls = startOfRound.collidersAndRoomMask;
        float best = -1f;
        float bestStand = -1f;
        int inRange = 0;
        foreach (Light light in roundManager.allPoweredLights)
        {
            // activeInHierarchy, not enabled: a flicker already running switches lights off for a moment.
            if (light == null || !light.gameObject.activeInHierarchy)
            {
                continue;
            }

            Vector3 bulb = light.transform.position;
            float distance = Vector3.Distance(eye, bulb);
            bool looking = distance <= LightRange && Vector3.Angle(forward, bulb - eye) <= LookCone;
            bool standing = distance <= StandInRange && distance <= light.range * StandInShare;
            if (!looking && !standing)
            {
                continue;
            }

            inRange++;
            bool better = looking ? best < 0f || distance < best : bestStand < 0f || distance < bestStand;
            if (better && (Clear(eye, bulb, walls, FixtureTolerance) || Clear(eye, bulb + Vector3.down * LitSpaceDrop, walls, 0f)))
            {
                if (looking)
                {
                    best = distance;
                }
                else
                {
                    bestStand = distance;
                }
            }
        }

        if (best < 0f && bestStand >= 0f)
        {
            best = bestStand;
            standingIn = true;
        }
        reason = best >= 0f ? ""
            : inRange == 0 ? "no light: none in view within 12 m or around you within 6 m"
            : Invariant($"no light: {inRange} near or in view, all behind walls");
        return best;
    }

    // Nothing solid between a and b, except possibly within "tolerance" of b.
    private static bool Clear(Vector3 a, Vector3 b, int mask, float tolerance)
    {
        if (!Physics.Linecast(a, b, out RaycastHit hit, mask, QueryTriggerInteraction.Ignore))
        {
            return true;
        }

        return hit.distance >= Vector3.Distance(a, b) - tolerance;
    }

    // No power if the apparatus was pulled, or the breaker box is off.
    // Some interiors have no breaker box; those always have power.
    internal static bool FacilityHasPower(RoundManager roundManager)
    {
        if (roundManager.powerOffPermanently)
        {
            return false;
        }

        // Finding the breaker box searches the whole scene, so remember it. A new day's level has a new one (the old
        // one is destroyed, so it reads as null), and while there is none we look again at most every few seconds.
        if (breakerBox == null && Time.time >= nextBreakerSearch)
        {
            breakerBox = Object.FindObjectOfType<BreakerBox>();
            nextBreakerSearch = Time.time + BreakerSearchSeconds;
        }
        return breakerBox == null || breakerBox.isPowerOn;
    }
}
