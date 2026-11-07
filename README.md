# Sanity

*Did you hear that?*

A Lethal Company mod that gives every player a hidden sanity meter.

Wander off alone, stay deep in the facility too long, get caught out after dark or get a real scare, and it starts
slipping. The ship, your crew, a breath of fresh air and a good haul bring you back.

Let it slip far enough and things start happening that only you notice. They're made from the game's own sounds and
effects, so you can't tell what's real, and your crew can't back you up. No new monsters, and nothing that can really
hurt you.

**Tell us how it played:** the [feedback form](https://forms.gle/zraR8cwDnDv6Yz5a7) takes 1–2 minutes and needs no
sign-in. Stories, ideas and questions go in [Discussions](https://github.com/lowlightworks/Sanity/discussions).

## ⚠️ Disclaimer

- **Made with AI.** The code was written by an AI coding assistant (Claude Opus 5.5, by Anthropic), and the design
  was worked out with its help. The direction and decisions come from a human side with limited programming
  knowledge.
- **No code review yet**, and **limited playtesting** (a handful of sessions, one small group, one modpack).
- **It's early.** Balance will change between versions.

**It's still safe to play.** It adds no items or monsters, never touches your real health or your save file, and
fails quietly: if something it needs is missing, that effect is skipped. It has been played through full sessions
without breaking anything. (The license says it's provided "as is", without warranty.)

## What you might run into (spoilers)

- The lights flickering, right when you look at them.
- A monster behind you. You turn around. Nothing.
- Footsteps keeping pace with yours.
- Your walkie clicking on, with nobody on the other end.
- Getting hit by something that isn't there.
- ...and a few things it's better to find out for yourself.

## Multiplayer

**Only you need it.** Everyone who has the mod gets their own meter and their own scares; players without it (and
the host) are fine, and versions don't need to match. It all happens on your screen, so your crew can't confirm a
thing. Two tiny exceptions: a key you use on a door that "won't open" really is used up, and your crew won't see you
flinch from a hit that never happened (your character may look stuck for a moment).

## Install

With a mod manager (Thunderstore Mod Manager, r2modman, Gale). Manual: put `Lowlight.Sanity.dll` in
`BepInEx/plugins` (BepInEx 5).

## Config

In-game with LethalConfig (optional), or in `BepInEx/config/Lowlight.Sanity.cfg`.

| Setting | Default | What it does |
|---|---|---|
| EnableMod | on | Master switch. |
| Symptoms / each symptom | on | Turn individual symptoms off. |
| DrainMultiplier / RecoveryMultiplier | 1 | How fast sanity drops / comes back. |
| PhantomLoudness | 1 | How loud phantom steps and sounds are. |
| ShareAnonymousStats | on | Send anonymous stats (see [below](#anonymous-stats)). |
| Testing tools | off | For testers and modders: readout, debug keys, a detailed local log. |

## Compatibility

Works with LethalHUD and GeneralImprovements (health displays), Lategame Upgrades (Locksmith, speed, longer days),
Wesley's Weathers and other weather mods through WeatherRegistry, and modded moons and interiors. Phantom sounds only
imitate vanilla monsters for now.

## Anonymous stats

**On by default, and you're told first.** The first time you reach the main menu, a small notice in the corner says
that Sanity shares anonymous stats, with **Turn off** right there (it closes when you choose, use the menu, or after
30 seconds). Nothing is sent before it has been on screen. You can change it any time with `ShareAnonymousStats` in
the config (modpack makers can set it off for their pack). The notice comes back only if what's collected changes.

While it's on, the mod sends **one small summary for each in-game day**, when the ship leaves, to a spreadsheet only
the mod's author can see. It contains numbers and game content names only:

| What | Details |
|---|---|
| The build | mod and game version; a random code that's new every time you launch the game (it can't link your games together) |
| The day | moon, interior, weather (with the mod they come from, if modded), day length, players in the lobby, whether you were the host, how the day ended, how many teammates were still alive |
| Sanity | time spent in each sanity level, how often it changed, the lowest point, the ways it came back up |
| Where you spent the day | seconds inside / outside / in the ship, alone or with someone; in fear; in the evening; sprinting; dead; your trips inside |
| Symptoms | for each one: how often it was picked, played, waited, or didn't happen, and why it couldn't play; the day's mood; how often one symptom led to another |
| Reactions | how often players turned, stopped, sprinted or scanned after a symptom, compared with random quiet moments |
| Phantom sounds and hits | which monster, inside or outside, the type of ground, and whether you turned toward it or stopped; for hits, what it imitated and the health it showed as lost |
| The real game | which monsters were on the moon (with their mod, if modded), real scares, damage taken, cause of death, revives, loot brought back, when the apparatus was pulled |
| Settings | the mod's multipliers, any symptoms you switched off, whether its debug tools were used, whether a few compatibility-relevant mods and test tools (like Imperium) are installed |
| Performance | average and lowest frame rate |

**Never sent:** names (yours or anyone's), Steam IDs, your position, the time of day you played, your full mod list.
The last 10 summaries sent are kept in the mod's own folder, in `stats/sent/`, so you can read exactly what left your
computer. If it can't send (offline), the summary waits and goes later; turning sharing off deletes anything still
waiting. Nothing slows the game.

## Feedback

| What | Where |
|---|---|
| How it played, quick ratings, or a quick bug report | [Feedback form](https://forms.gle/zraR8cwDnDv6Yz5a7) (1–2 minutes, no sign-in) |
| A detailed bug report | [GitHub issues](https://github.com/lowlightworks/Sanity/issues) |
| Moments that got you, ideas, questions | [Discussions](https://github.com/lowlightworks/Sanity/discussions) |

For a bug, the BepInEx log (`BepInEx/LogOutput.log`) and what you were doing help a lot (check it for names before
sharing). Experienced modders: a look through the code, or any advice, is very welcome.

## More

- **How it all works** (the numbers behind sanity, each symptom, compatibility, the stats, known issues, plans, the
  testing tools, building from source): [the design document](https://github.com/lowlightworks/Sanity/blob/main/docs/design.md).
- **What changed in each version**: [the changelog](https://github.com/lowlightworks/Sanity/blob/main/CHANGELOG.md), and the technical
  [detailed changelog](https://github.com/lowlightworks/Sanity/blob/main/docs/CHANGELOG-detailed.md).

## License

MIT (see [LICENSE](https://github.com/lowlightworks/Sanity/blob/main/LICENSE)).
