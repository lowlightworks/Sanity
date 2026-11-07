# Changelog

The technical details of every version are in the
[detailed changelog](https://github.com/lowlightworks/Sanity/blob/main/docs/CHANGELOG-detailed.md).

## 0.0.3 (unreleased)

### New
- Phantom hits now hit like the monster you heard: one hit, as hard as that monster really hits, with a big flinch.
- Pulling the apparatus shakes your sanity, and the dark facility wears it down faster for the rest of the day.
- Anonymous stats to help tune the mod. On by default, with a one-time notice on the main menu where you can turn
  it off. Numbers only: no names, no IDs. Everything that's sent is listed in the README.
- A feedback form and a Discussions page for stories, ideas and questions (links in the README).

### Changed
- The locked door and the vanishing item can now happen at Uneasy too.
- Lost time no longer keeps other symptoms from happening while it waits for its moment.
- For testers: the detailed log is now the `DetailedTestLog` setting, saved in the mod's folder, without player names.

### Fixed
- The length of the day was misread (a normal day counted as shorter), so lost time came too early.

## 0.0.2 (2026-11-06)

### New
- Phantom sounds and footsteps no longer get skipped while you sprint: they wait until you slow down.

### Changed
- Lost time reworked: when it happens it's always night, with the game's own music for a new part of the day, and it
  happens on more days. It switches as you step out, never in front of you, and stays away once the ship is leaving.
- Lost time is followed by a walkie click more often.

## 0.0.1 (2026-11-06)

First public release. Very early and still rough: it's out so it's easy to install and share, not because it's
finished. Expect rough edges, and balance changes between versions.

### New
- A hidden sanity meter for each player: it drops when you're alone, deep in the facility, out at night or scared,
  and comes back in the ship, with teammates and with loot.
- Eleven symptoms that get stronger as sanity drops: flickering lights, fear with nothing there, monster sounds,
  footsteps, walkie clicks, fake hits, a locked door, a vanishing item, lost time, and something that follows you.
- Works with GeneralImprovements, LethalHUD, Lategame Upgrades, Wesley's Weathers and modded moons.
- Settings: switch the mod or any symptom off, and make sanity drop or recover faster or slower.
- Testing tools for modders and testers (all off by default).
