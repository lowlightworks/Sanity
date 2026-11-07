# Sanity

A Lethal Company mod that gives each player a hidden sanity meter.

Being alone, deep inside the facility, out at night, or scared drains it. The ship, your teammates (meeting up after
time alone), fresh air after a long trip inside and bringing loot home bring it back.
When it gets low, you start experiencing symptoms that only you can see or hear. They're built from the game's own
sounds and effects, so you can't easily tell them apart from the real thing, and your crewmates can't confirm them
either. No new monsters, no custom assets, and nothing that can really hurt you.

## ⚠️ Disclaimer: use at your own risk

- **Made with AI.** The code was written by an AI coding assistant (Claude Opus 5.5, by Anthropic), and the design
  was worked out with its help. The direction and decisions come from a human side with limited programming
  knowledge.
- **No code review yet.** The code hasn't been read through by an experienced programmer, so there may be bugs,
  rough edges, or things done in an odd way.
- **Limited playtesting.** It has been played over a handful of sessions, by one small group, with one modpack. Other
  setups may turn up problems.
- **It's early.** Balance will change between versions.
- It's built to fail quietly (if something it needs is missing, that effect is skipped instead of breaking the game),
  but no guarantees: the license says it's provided "as is", without warranty of any kind.

**That said, it's safe to play.** It adds no items or monsters, never touches your real health or your save file, and
everything it does stays on your own screen, apart from two small effects (see [Multiplayer](#multiplayer-who-needs-it)).
It has been played through full sessions without breaking anything. The points above are about being honest, not a
warning that something is wrong.

**Experienced modders:** if you feel like looking through the code, any help or advice is very welcome (see
[Feedback](#feedback-and-contributing)). Otherwise, have fun, and please report what you find.

**Tell us how it played:** the [feedback form](https://forms.gle/zraR8cwDnDv6Yz5a7) takes 1–2 minutes and needs no
sign-in. Stories, ideas and questions go in [Discussions](https://github.com/lowlightworks/Sanity/discussions).

## Symptoms (spoilers)

- **Flicker**: the lights flicker when one is right there (in view, or you're standing in it); at lower sanity your
  flashlight stutters or cuts out.
- **Phantom fear / dread**: the game's fear music, screen filter and heartbeat, with nothing there.
- **Phantom sound**: a monster that could be on this moon makes a sound behind you. Nothing's there.
- **Phantom steps**: footsteps passing, following you at your pace, or rushing up behind you.
- **Phantom walkie**: your walkie clicks on as if a teammate pressed theirs.
- **Phantom hit**: you get hit by nothing; the health it took comes back slowly, on your screen only.
- **Locked door**: the door you reach is locked. Use a key on it and the key is gone.
- **Vanishing item**: the loot you're walking up to isn't there when you reach it.
- **Lost time**: you step out of the facility after a long trip inside and it's night.
- **The Presence**: something unseen moves around you for a while: its steps, its voice, the lights.

How sanity is calculated, and the design behind each symptom, is in [docs/design.md](docs/design.md).

## Multiplayer: who needs it?

**Only you.** Each player who has the mod gets their own meter and their own symptoms. Players without it, and the
host, don't need it, and mismatched versions are fine: the mod sends no messages of its own.

Almost everything happens only on your screen. Two small things can reach other players, both through the game's own
code:

- **The locked door's key.** If you use a key on the fake locked door, the key really is used up, through the game's
  normal key use. To everyone else it looks like you used a key.
- **The phantom hit's flinch.** While your character flinches from a phantom hit, the game's updates of your body's
  animation to other players pause for about a second, so they don't see a flinch. At most, your character looks
  briefly stuck in the animation it was in.

Also worth knowing: a vanished item is only hidden for you. Your teammates can still see it and pick it up.

## Install

Install it with a mod manager (Thunderstore Mod Manager, r2modman, Gale): it brings BepInEx with it.

Manual install: put `Lowlight.Sanity.dll` in `BepInEx/plugins` (BepInEx 5 required).

## Config

Editable in-game with LethalConfig (optional), or in `BepInEx/config/Lowlight.Sanity.cfg`.

| Setting | Default | What it does |
|---|---|---|
| EnableMod | on | Master switch. Off: sanity stays at 100, no symptoms. |
| Symptoms / each symptom | on | Turn individual symptoms off. |
| DrainMultiplier / RecoveryMultiplier | 1 | How fast sanity drops / comes back. |
| PhantomLoudness | 1 | How loud phantom steps and sounds are (2 = louder, 0.5 = quieter). |
| EnableTelemetry | off | Records a CSV for tuning (see below). |
| ShowSanityReadout / EnableDebugKeys | off | Testing tools: on-screen numbers, F7 debug menu, F8 random symptom. |
| ShowPresence | off | Testing: a red ball where the Presence is. |

## Compatibility

- Health displays: works with LethalHUD and GeneralImprovements (they show the phantom hit's lost health too).
- Lategame Upgrades: Locksmith (the fake locked door can be picked), speed upgrades, and longer days are handled.
- Modded weathers: Wesley's Weathers (by Magic_Wesley) has its own drain numbers for each of its weathers. Weather
  names are read through WeatherRegistry, which Wesley's Weathers and most other weather mods use. Other modded
  weathers count as clear weather; every weather's numbers can be changed in the config.
- Modded moons and interiors work. Phantom sounds only use vanilla monsters (modded monsters are skipped for now).

## Telemetry (off by default)

If you turn on `EnableTelemetry`, the mod writes one CSV per game session to `BepInEx/Sanity_Telemetry/`: your sanity
once a second, what changed it, and which symptoms happened. It stays on your computer: nothing is sent anywhere.
The file includes in-game player names (yours, and teammates' where they matter, such as who carried loot in), so
check it before sharing it with anyone. `tools/analyze.py` and `tools/replay.py` read these files (see
[tools/README.md](tools/README.md)).

## Feedback and contributing

All of it is welcome: bug reports, suggestions, ideas, constructive criticism, advice, and questions about why the code
does something. If you know C# or Unity modding and would like to review some of the code, that would be especially
appreciated.

| What | Where |
|---|---|
| How it played, quick ratings, or a quick bug report | [Feedback form](https://forms.gle/zraR8cwDnDv6Yz5a7) (1–2 minutes, no sign-in) |
| A detailed bug report | [GitHub issues](https://github.com/lowlightworks/Sanity/issues) |
| Moments that got you, ideas, questions | [Discussions](https://github.com/lowlightworks/Sanity/discussions) |

When reporting a bug, the BepInEx log (`BepInEx/LogOutput.log`) and what you were doing help a lot. Please check it
for personal names before sharing.

## Building from source

Needs the .NET 10 SDK.

1. Copy `Config.Build.user.props.template` to `Config.Build.user.props` and set your BepInEx folder (for a mod
   manager profile, the profile's `BepInEx` folder).
2. `dotnet build -c Release`. The DLL is copied into that BepInEx `plugins` folder automatically, and a Thunderstore
   zip is made in `artifacts/thunderstore/`.

Where to start reading the code: the comment at the top of `src/Sanity/Plugin.cs`.

## Known limitations

- Phantom sounds and the Presence only imitate vanilla monsters.
- Phantom sounds and steps wait while you sprint (your own steps would drown them out); a long sprint can lose one.
- Lost time changes the clock, the sky and the music, but can still be missed.
- Balance is early and based on limited data.

The full list, with what's planned for each, is in [docs/design.md](docs/design.md#known-issues-and-limitations).

## License

MIT (see `LICENSE`).
