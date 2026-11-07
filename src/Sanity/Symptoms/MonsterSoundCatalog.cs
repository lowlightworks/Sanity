using System;
using UnityEngine;

namespace Sanity.Symptoms;

// One sound a monster can make, copied from how the monster itself plays it.
internal sealed class MonsterSound
{
    internal string Name = "";

    // Reads the clips from the monster (a live one or the moon's prefab).
    internal Func<EnemyAI, AudioClip[]?> Clips = _ => null;

    // The monster's own sound source for this sound; we copy its settings (falloff, distance, mixer).
    internal Func<EnemyAI, AudioSource?> Source = ai => ai.creatureVoice;

    // Repeats: 1 = a one-shot. More = a rhythmic burst (footsteps, chittering, breathing).
    internal int MinCount = 1;
    internal int MaxCount = 1;
    internal float MinGap;               // seconds from one repeat to the next
    internal float MaxGap;
    internal bool GapAfterClip;          // the gap starts when the clip ends (for long clips like breathing)

    // Per-play variation, as the game does for its own monster sounds.
    internal float MinPitch = 0.94f;
    internal float MaxPitch = 1.06f;
    internal float MinVolume = 0.82f;
    internal float MaxVolume = 1f;

    internal bool Rhythmic => MaxCount > 1;
}

// A monster and the (non-aggressive) sounds we can fake for it.
internal sealed class MonsterEntry
{
    internal string Name = "";
    internal Func<EnemyAI, bool> Is = _ => false;
    internal MonsterSound[] Sounds = Array.Empty<MonsterSound>();
}

// The curated list. Only sounds a monster makes while moving around or idle: never attack or kill sounds. Excluded on
// purpose: Jester (too loud and distinctive to fake), Bracken (silent; its one sound is a kill), Masked (uses player
// voices), Thumper (its only non-attack sound is unused, and its wall hits happen mid-charge, which is an attack).
// Modded monsters are skipped. Inside or outside is decided by the moon's spawn lists, not here.
internal static class MonsterSoundCatalog
{
    internal static readonly MonsterEntry[] All =
    {
        // ---- Usually inside ----
        new MonsterEntry
        {
            Name = "Hoarding bug",
            Is = ai => ai is HoarderBugAI,
            Sounds = new[]
            {
                new MonsterSound { Name = "chitter", Clips = ai => ai is HoarderBugAI m ? m.chitterSFX : null,
                    MinCount = 1, MaxCount = 3, MinGap = 1.5f, MaxGap = 3f },
            },
        },
        new MonsterEntry
        {
            Name = "Bunker spider",
            Is = ai => ai is SandSpiderAI,
            Sounds = new[]
            {
                new MonsterSound { Name = "footsteps", Clips = ai => ai is SandSpiderAI m ? m.footstepSFX : null,
                    Source = ai => ai is SandSpiderAI m ? m.footstepAudio : null,
                    MinCount = 6, MaxCount = 12, MinGap = 0.12f, MaxGap = 0.3f,
                    MinPitch = 0.6f, MaxPitch = 1.2f, MinVolume = 0.1f, MaxVolume = 1f },
            },
        },
        new MonsterEntry
        {
            Name = "Coilhead",
            Is = ai => ai is SpringManAI,
            Sounds = new[]
            {
                new MonsterSound { Name = "spring", Clips = ai => ai is SpringManAI m ? m.springNoises : null,
                    MinPitch = 1f, MaxPitch = 1f },
            },
        },
        new MonsterEntry
        {
            Name = "Nutcracker",
            Is = ai => ai is NutcrackerEnemyAI,
            Sounds = new[]
            {
                new MonsterSound { Name = "torso turn", Clips = ai => ai is NutcrackerEnemyAI m ? m.torsoFinishTurningClips : null,
                    Source = ai => ai is NutcrackerEnemyAI m ? m.torsoTurnAudio : null },
            },
        },
        new MonsterEntry
        {
            Name = "Butler",
            Is = ai => ai is ButlerEnemyAI,
            Sounds = new[]
            {
                new MonsterSound { Name = "footsteps", Clips = ai => ai is ButlerEnemyAI m ? m.footsteps : null,
                    Source = ai => ai.creatureSFX, MinCount = 5, MaxCount = 10, MinGap = 0.4f, MaxGap = 0.55f,
                    MinVolume = 1f, MaxVolume = 1f, MinPitch = 1f, MaxPitch = 1f },
                new MonsterSound { Name = "broom sweep", Clips = ai => ai is ButlerEnemyAI m ? m.broomSweepSFX : null,
                    Source = ai => ai.creatureSFX, MinVolume = 1f, MaxVolume = 1f, MinPitch = 1f, MaxPitch = 1f },
            },
        },
        new MonsterEntry
        {
            Name = "Snare flea",
            Is = ai => ai is CentipedeAI,
            Sounds = new[]
            {
                new MonsterSound { Name = "shriek", Clips = ai => ai is CentipedeAI m ? m.shriekClips : null,
                    MinPitch = 1f, MaxPitch = 1f },
            },
        },
        new MonsterEntry
        {
            Name = "Hygrodere",
            Is = ai => ai is BlobAI,
            Sounds = new[]
            {
                new MonsterSound { Name = "jiggle", Clips = ai => ai is BlobAI m ? One(m.jiggleSFX) : null,
                    Source = ai => ai.creatureSFX },
            },
        },
        new MonsterEntry
        {
            Name = "Barber",
            Is = ai => ai is ClaySurgeonAI,
            Sounds = new[]
            {
                new MonsterSound { Name = "snare drum", Clips = ai => ai is ClaySurgeonAI m ? One(m.snareDrum) : null,
                    Source = ai => ai is ClaySurgeonAI m ? m.musicAudio : null,
                    MinCount = 3, MaxCount = 5, MinGap = 0.9f, MaxGap = 1.3f,
                    MinVolume = 1f, MaxVolume = 1f, MinPitch = 1f, MaxPitch = 1f },
                new MonsterSound { Name = "scissors", Clips = ai => ai is ClaySurgeonAI m ? One(m.snipScissors) : null,
                    Source = ai => ai.creatureSFX, MinVolume = 1f, MaxVolume = 1f, MinPitch = 1f, MaxPitch = 1f },
            },
        },
        new MonsterEntry
        {
            Name = "Maneater",
            Is = ai => ai is CaveDwellerAI,
            Sounds = new[]
            {
                new MonsterSound { Name = "cry", Clips = ai => ai is CaveDwellerAI m ? m.fakeCrySFX : null,
                    MinVolume = 1f, MaxVolume = 1f, MinPitch = 1f, MaxPitch = 1f },
                new MonsterSound { Name = "growl", Clips = ai => ai is CaveDwellerAI m ? One(m.growlSFX) : null,
                    MinVolume = 1f, MaxVolume = 1f, MinPitch = 1f, MaxPitch = 1f },
            },
        },

        // ---- Usually outside ----
        new MonsterEntry
        {
            Name = "Eyeless dog",
            Is = ai => ai is MouthDogAI,
            Sounds = new[]
            {
                new MonsterSound { Name = "breathing", Clips = ai => ai is MouthDogAI m ? One(m.breathingSFX) : null,
                    MinCount = 2, MaxCount = 3, MinGap = 0.1f, MaxGap = 0.4f, GapAfterClip = true,
                    MinVolume = 1f, MaxVolume = 1f, MinPitch = 1f, MaxPitch = 1f },
            },
        },
        new MonsterEntry
        {
            Name = "Baboon hawk",
            Is = ai => ai is BaboonBirdAI,
            Sounds = new[]
            {
                new MonsterSound { Name = "caw", Clips = ai => ai is BaboonBirdAI m ? m.cawLaughSFX : null },
            },
        },
        new MonsterEntry
        {
            Name = "Kidnapper fox",
            Is = ai => ai is BushWolfEnemy,
            Sounds = new[]
            {
                new MonsterSound { Name = "call", Clips = ai => ai is BushWolfEnemy m ? m.callsFar : null,
                    Source = ai => ai is BushWolfEnemy m ? m.callFar : null,
                    MinVolume = 1f, MaxVolume = 1f, MinPitch = 1f, MaxPitch = 1f },
                new MonsterSound { Name = "growl", Clips = ai => ai is BushWolfEnemy m ? m.growlSFX : null,
                    Source = ai => ai is BushWolfEnemy m ? m.growlAudio : null,
                    MinVolume = 1f, MaxVolume = 1f, MinPitch = 1f, MaxPitch = 1f },
            },
        },
        new MonsterEntry
        {
            Name = "Giant sapsucker",
            Is = ai => ai is GiantKiwiAI,
            Sounds = new[]
            {
                new MonsterSound { Name = "squawk", Clips = ai => ai is GiantKiwiAI m ? One(m.squawkSFX) : null,
                    Source = ai => ai is GiantKiwiAI m ? m.peckAudio : null,
                    MinVolume = 1f, MaxVolume = 1f, MinPitch = 1f, MaxPitch = 1f },
                new MonsterSound { Name = "footsteps", Clips = ai => ai is GiantKiwiAI m ? m.footstepSFX : null,
                    Source = ai => ai.creatureSFX, MinCount = 4, MaxCount = 8, MinGap = 0.45f, MaxGap = 0.6f,
                    MinVolume = 1f, MaxVolume = 1f, MinPitch = 1f, MaxPitch = 1f },
            },
        },
        new MonsterEntry
        {
            Name = "Puma",
            Is = ai => ai is PumaAI,
            Sounds = new[]
            {
                new MonsterSound { Name = "growl", Clips = ai => ai is PumaAI m ? m.pumaGrowlSFX : null },
                new MonsterSound { Name = "footsteps", Clips = ai => ai is PumaAI m ? m.pumaFootstepSFX : null,
                    Source = ai => ai is PumaAI m ? m.footstepSource : null,
                    MinCount = 4, MaxCount = 8, MinGap = 0.3f, MaxGap = 0.4f, MinVolume = 0.2f, MaxVolume = 1f },
            },
        },
    };

    // Phantom hit's cause: an AGGRESSIVE sound, only from monsters that really attack you and whose attack doesn't kill
    // outright (damage per hit in game version 81). Each sound is the one the monster makes as it attacks or charges,
    // played the way it plays it. Left out: one-hit kills (eyeless dog, forest giant, earth leviathan, Jester, Bracken,
    // Nutcracker's shotgun, Barber, Maneater, kidnapper fox, circuit bees, Masked, Old Bird), hits too big to show
    // above 26 health (bunker spider 90, Coilhead 90), the Hygrodere (no attack sound: it can only hit as the
    // Presence's mask), monsters that don't attack you (manticoil, locusts, tulip snake; spore lizard only bites when
    // cornered), and the Butler (its 10-damage stabs make no sound of their own). Modded monsters are skipped.
    internal static readonly MonsterEntry[] Attackers =
    {
        // ---- Usually inside ----
        new MonsterEntry
        {
            Name = "Hoarding bug",                  // bites for 30
            Is = ai => ai is HoarderBugAI,
            Sounds = new[]
            {
                new MonsterSound { Name = "angry screech", Clips = ai => ai is HoarderBugAI m ? m.angryScreechSFX : null,
                    MinVolume = 1f, MaxVolume = 1f },
            },
        },
        new MonsterEntry
        {
            Name = "Thumper",                       // bites for 40
            Is = ai => ai is CrawlerAI,
            Sounds = new[]
            {
                new MonsterSound { Name = "roar", Clips = ai => ai is CrawlerAI m ? m.longRoarSFX : null,
                    MinVolume = 1f, MaxVolume = 1f, MinPitch = 1f, MaxPitch = 1f },
            },
        },
        new MonsterEntry
        {
            Name = "Snare flea",                    // 10 a tick while clinging
            Is = ai => ai is CentipedeAI,
            Sounds = new[]
            {
                new MonsterSound { Name = "attack shriek", Clips = ai => ai is CentipedeAI m ? m.shriekClips : null,
                    MinVolume = 1f, MaxVolume = 1f, MinPitch = 1f, MaxPitch = 1f },
            },
        },

        // ---- Usually outside ----
        new MonsterEntry
        {
            Name = "Baboon hawk",                   // hits for 20
            Is = ai => ai is BaboonBirdAI,
            Sounds = new[]
            {
                new MonsterSound { Name = "scream", Clips = ai => ai is BaboonBirdAI m ? m.cawScreamSFX : null,
                    MinVolume = 1f, MaxVolume = 1f },
            },
        },
        new MonsterEntry
        {
            Name = "Giant sapsucker",               // pecks for 10
            Is = ai => ai is GiantKiwiAI,
            Sounds = new[]
            {
                new MonsterSound { Name = "scream", Clips = ai => ai is GiantKiwiAI m ? m.screamSFX : null,
                    MinVolume = 1f, MaxVolume = 1f },
            },
        },
        new MonsterEntry
        {
            Name = "Puma",                          // scratches for 7
            Is = ai => ai is PumaAI,
            Sounds = new[]
            {
                new MonsterSound { Name = "attack scream", Clips = ai => ai is PumaAI m ? One(m.attackScream) : null,
                    Source = ai => ai is PumaAI m ? m.attackSFX : null,
                    MinVolume = 1f, MaxVolume = 1f, MinPitch = 0.94f, MaxPitch = 1.1f },
            },
        },
    };

    // Which catalog entry (if any) this monster belongs to.
    internal static MonsterEntry? Find(EnemyAI ai)
    {
        return Find(All, ai);
    }

    internal static MonsterEntry? Find(MonsterEntry[] catalog, EnemyAI ai)
    {
        foreach (MonsterEntry entry in catalog)
        {
            if (entry.Is(ai))
            {
                return entry;
            }
        }
        return null;
    }

    // Some monsters store a single clip instead of a list.
    private static AudioClip[]? One(AudioClip? clip)
    {
        return clip != null ? new[] { clip } : null;
    }
}
