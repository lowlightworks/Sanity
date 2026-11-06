namespace Sanity.Symptoms;

// Phantom fear's big sibling, Breaking only: a near-max spike to 0.9, the height the game uses for a
// Coilhead, a dead body or the Ghost Girl. Not 1.0, which is the game's "something has you" value and
// would send players looking for an attacker. Rarer than phantom fear, and the two share a gap.
internal sealed class PhantomDreadSymptom : PhantomFearSymptom
{
    internal override string Name => "Phantom dread";

    internal override string ConfigKey => "PhantomDread";

    internal override SanityTier MinTier => SanityTier.Breaking;

    internal override float BaseWeight => 0.5f;

    internal override float RecoverySeconds => 300f;

    protected override float Peak => 0.9f;

    protected override string EventName => "PHANTOM_DREAD";

    // Dread is a peak: the crisis passes soon after, and the director lets it breathe.
    internal override bool TryPlay(GameNetcodeStuff.PlayerControllerB player, SanityTier tier)
    {
        bool played = base.TryPlay(player, tier);
        if (played)
        {
            SanityDirector.Peak("dread");
        }
        return played;
    }
}
