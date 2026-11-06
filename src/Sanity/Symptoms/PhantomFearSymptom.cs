using GameNetcodeStuff;
using UnityEngine;
using static System.FormattableString;

namespace Sanity.Symptoms;

// A fear spike with nothing there: the game's own fear package (tension music, screen filter,
// heartbeat) triggered by the same call real monsters use (JumpToFearLevel), with the game's
// normal fade afterwards. The game's fear value only exists on this computer, so only this player
// gets it. Works anywhere, ship included.
// The spike is a fixed height, like the game's own events: a fixed value reads as a real event,
// a random one doesn't. 0.6 is what the game uses when you spot a Bracken.
// Phantom dread (PhantomDreadSymptom) is the same thing, bigger and rarer.
internal class PhantomFearSymptom : Symptom
{
    private const float QuietFearLimit = 0.1f;          // real fear must be below this (a quiet moment)
    private const float RealFearCooldownSeconds = 150f; // and no real fear rise for this long
    private const float SharedGapSeconds = 90f;         // and no fake spike (fear or dread) for this long

    // When the last fake spike of either kind happened. Shared, so fear and dread never land back to back.
    private static float lastSpikeTime = -9999f;

    internal override string Name => "Phantom fear";

    internal override string ConfigKey => "PhantomFear";

    internal override string Family => "body";

    internal override float BaseWeight => 0.8f;

    internal override SanityTier MinTier => SanityTier.Disturbed;

    // A big effect, so it should be rare.
    internal override float RecoverySeconds => 120f;

    // How high the spike goes.
    protected virtual float Peak => 0.6f;

    protected virtual string EventName => "PHANTOM_FEAR";

    // Needs a quiet moment (fear below 0.1, no real fear rise for 150 s) and no fake spike (fear or dread) for 90 s.
    internal override string CantPlayReason(PlayerControllerB player)
    {
        StartOfRound startOfRound = StartOfRound.Instance;
        if (startOfRound == null)
        {
            return "no round";
        }
        if (startOfRound.fearLevel >= QuietFearLimit)
        {
            return Invariant($"fear: {startOfRound.fearLevel:F2} (needs below {QuietFearLimit})");
        }
        if (SanityMeter.SecondsSinceRealFear < RealFearCooldownSeconds)
        {
            return Invariant($"recent real fear: {SanityMeter.SecondsSinceRealFear:F0} s ago (needs {RealFearCooldownSeconds:F0})");
        }

        float sinceSpike = Time.time - lastSpikeTime;
        if (!SanityDirector.GapsIgnored && sinceSpike < SharedGapSeconds)
        {
            return Invariant($"fake spike gap: {sinceSpike:F0} s ago (needs {SharedGapSeconds:F0})");
        }
        return "";
    }

    internal override bool TryPlay(PlayerControllerB player, SanityTier tier)
    {
        float peak = Peak;

        // Tag this one call as ours, so our fear hook doesn't treat it as a real scare.
        SanityMeter.PhantomJump = true;
        try
        {
            player.JumpToFearLevel(peak);
        }
        finally
        {
            SanityMeter.PhantomJump = false;
        }
        SanityMeter.NotePhantomJump();
        lastSpikeTime = Time.time;

        Telemetry.Event(EventName, peak, Invariant($"spike to {peak:F2} at {tier}"));
        return true;
    }
}
