# Detailed changelog

The technical version of [CHANGELOG.md](../CHANGELOG.md): what changed, with the numbers and where it lives in the
code. For how everything works now, see [design.md](design.md).

## 0.0.3 (2026-11-07)

### New
- **Phantom hits imitate the monster** (`PhantomHitSymptom.cs`). The attack sound picks the monster, only among those
  whose hit fits the room left on the shown health (`PhantomSoundSymptom.PlayCloseOneShot` takes a filter and returns
  the monster's name), then one hit for that monster's real damage in the game: hoarding bug 30, thumper 40,
  hygrodere 35, baboon hawk 20, snare flea 10, butler 10, giant sapsucker 10, puma 7. "A person" hits for 20 (a
  shovel): the hit after a rush of phantom steps (chain), and the Presence's finale when its mask is a person. The
  Presence wearing a monster hits as that monster; bunker spider and coilhead (90, more than can be shown above 26)
  and monsters that only kill don't fake hits, so the Presence picks another finale. A hit never comes out of
  silence: on its own it needs its attack sound (it used to fall back to a silent "person" hit), and the Presence's
  comes right after its step behind you. Camera shake is big from 20 up;
  the fear spike is sized to the hit (0.6 from 30 damage, 0.45 around 20, 0.3 for 10 or less; it was 0.45 always).
  Replaces 1-3 hits (2-4 at Breaking) of 8-15 each. A Breaking hit still counts as a peak for the director.
- **Apparatus pulled** (`SanityMeter.OnPowerOff`, `Patches/ApparatusPatch.cs`). On the game's radiation warning (it
  plays a moment after the power is cut for good, on every player's HUD, and only for the apparatus): an instant drop
  of 10 if you're inside, 4 elsewhere (scaled by `DrainMultiplier`), then the inside drain and its cap are x1.75 for
  the rest of the day (the same way weather scales them). If the warning never comes (another mod), the power being
  off for good (`RoundManager.powerOffPermanently`) starts it 3 s later. Event APPARATUS_PULLED; `replay.py` mirrors
  the drop and the multiplier.
- **Anonymous stats** (`DayStats.cs`, `StatsUploader.cs`, `StatsPanel.cs`, `ContentInfo.cs`, `Patches/MenuPatch.cs`).
  One JSON packet per player per in-game day, sent in the background with UnityWebRequest to a Google Apps Script
  (`tools/stats-endpoint/Code.gs`) that appends it as a row of a spreadsheet.
  - On by default (`Stats/ShareAnonymousStats`). A one-time notice on the main menu: a bordered box in the game's
    menu font with Keep sharing / Turn off / What's sent?. It waits until the menu is free, closes on a choice, on any
    menu button, when the menu moves on (another screen, the loading screen, a popup) or after 30 s. Whether it was
    seen is kept in the game's PlayerPrefs (not the config, so a modpack's shared config can't skip it); it shows
    again when `StatsUploader.NoticeVersion` goes up. Nothing is sent until it has been on screen.
  - Contents: build, game version, a random code per game launch (no lasting ID); the day's context (moon, interior,
    weather with vanilla flags and source mod, through LethalLevelLoader or the monster's assembly), players, role,
    day length, how the day ended (left, early vote, quit, disconnected); time per tier and per place, trips inside,
    dead time, revives; per-symptom counts (drawn, played, armed, held, dropped, failed, rejected, played per tier);
    reactions against control moments; each phantom sound (monster, place, the floor under you read by a raycast,
    reaction) and phantom hit (imitated monster, damage); monsters on the moon; real scares, damage, death; loot;
    when the apparatus was pulled; what kept each symptom from playing; mood, echoes, chains; settings; debug use;
    watched mods (including the test tools Imperium and UnityExplorer, so odd days can be explained); errors; frame
    rate. The day's context is a once-a-second snapshot taken on the moon (orbit resets the weather, day length and
    mood first; a disconnect removes the round).
  - Queue in `stats/queue/` inside the mod's folder (sent on the next launch or day if offline; deleted if sharing is
    turned off); the last 10 sent are kept in `stats/sent/`. A redirect from the script counts as delivered (it isn't
    followed, so a row is never sent twice). Files are written without a byte-order mark.
  - Every part fails on its own; the first error is logged once.
- **Feedback channels**: a form and GitHub Discussions, linked in the README.
- **Tools**: `analyze.py` and `replay.py` read the stats export (CSV with a `json` column) through `statsdata.py`:
  duplicates (session + day) skipped; no day thrown away: a day where the debug menu changed the meter or director is
  left out of only those numbers, and days with test tools are kept and counted. `analyze.py` reports the pooled
  numbers (tier split overall, by lobby size and with `--per-moon` by moon; symptoms per hour; reactions against
  control; phantom sounds and hits; monsters; the apparatus); `replay.py` gives a rough drain and gain estimate per
  day from the time budget.

### Changed
- **Locked door and vanishing item** can be armed from Uneasy, at weight 1.0 there (1.6 below). The director supports
  per-tier weights (`Symptom.WeightAt`).
- **Lost time** no longer takes one of the two armed slots (`ArmedSymptom.CountsTowardCap`), so it can't keep the
  flicker, the locked door or the vanishing item from arming.
- **The development log**: setting renamed `Development/DetailedTestLog` (was `Telemetry/EnableTelemetry`; the old
  entry does nothing), CSVs moved from `BepInEx/Sanity_Telemetry/` to the mod's folder under `telemetry/`, file names
  without the player's name, players written as their lobby slot (P1, P2...). Also switchable in the F7 menu's Tools
  tab. The recording behind it runs when either the log or the stats is on; only the log writes a file. Its DAY_END
  `day_length` is -1 when the clock didn't move enough to measure.

- **Docs**: the README is cut down to what a player needs (the anonymous stats field list stays in full);
  `docs/design.md` is the full reference, with a contents list and new Settings, Compatibility and Building from
  source sections. The public changelog is sorted into New / Changed / Fixed; this file holds the technical details.

### Fixed
- **Day length** (`DayLength.cs`): measured against the vanilla pace (a clock day of 1080 game units at the game's
  time speed of 1.4, about 771 real s; 700 s from landing at 7:40 AM to midnight). A normal day now reads x1.0; it
  read x0.71, so lost time's "6 minutes after landing" came after about 4.

## 0.0.2 (2026-11-06)

### New
- **Held while you sprint** (`SprintHold.cs`): a phantom sound or phantom steps drawn while you sprint waits up to
  10 s for you to slow down instead of being skipped (the roll is spent, like arming). One at a time, outside the
  armed cap. Events: SYMPTOM_ARMED "waits for you to stop sprinting", SYMPTOM "(held while you sprinted)",
  SYMPTOM_DROPPED.

### Changed
- **Lost time reworked** (`LostTimeSymptom.cs`, `Patches/EntrancePatch.cs`):
  - When: at least 6 real minutes after landing on a normal-length day (scaled by day length) and one unbroken visit
    inside of at least 3 minutes (was: from 1 PM on the clock, 2 minutes inside). Daily chance 80% (was 55%).
  - What: the clock shows 8 to 10 PM (was the real time plus 3-6 hours, up to 11:50 PM), the sky and light match, and
    the game's own time-of-day music cue plays; it lasts 12 s (was 7) before sliding back.
  - How: applied at the exit, in the same frame as the game's entrance teleport, so it's already night when you're
    outside. Not armed once the ship is leaving (early vote, midnight warning); an armed one is dropped and a showing
    one ends.
- **Lost time's walkie click** chain: 50% (was 25%).

## 0.0.1 (2026-11-06)

First public release.

### New
- **The sanity meter** (`SanityMeter.cs`), 0 to 100, at 100 off a moon. Tiers Calm (75+), Uneasy (52-75), Disturbed
  (25-52), Breaking (below 25), with a 3-point deadband on the way up.
  - Drains: inside 0.36/s growing to x1.5 over 4 minutes inside, capped at 0.495/s, fading over 15 s after you leave;
    outside 0.1/s; night (outside, or the ship with doors open) up to 0.4/s by Midnight; fear from 0 at 0.1 to 0.5/s
    at 1.0. Company (a living teammate within 10 m, kept to 14 m, within 4 m of height) halves inside, outside and
    night drain. Lobby size scales them (1 player x0.90 to 5+ x1.20). Weather multipliers per weather (`Weather.cs`,
    names through WeatherRegistry; `Weather/Overrides` in the config), with numbers for Wesley's Weathers.
  - Gains: the ship (0.35/s doors open, 0.5/s closed by day, sliding to 0.1 / 0.3 by Midnight; x1.1 with company;
    ramps up over 30 s; up to twice as strong near 0; never above 85). Reliefs (`SanityReliefs.cs`): regroup +16 (up
    to +32), breather +10, finding scrap +2, and the bounce out of Breaking after 75 s (back to 58, drains at half for
    45 s). Loot brought to the ship (`LootRecovery.cs`), sized by its share of the day's loot (up to 15 per item, 65%
    to the finder, half after 30 a day).
  - Instant drops: damage 0.2 per HP (at most 10, falls half), a teammate dying (10 nearby, 5 from the ship, 2
    elsewhere), fear jumps to 0.4+ (5, at most every 8 s). Revived: 85.
- **The director** (`SanityDirector.cs`, `DirectorLayers.cs`): per second 0.044 x depth x dip x tension x wave x mood;
  a weighted lottery with soft recency (recovery); armed symptoms (at most 2 waiting, 5 s from any other symptom);
  waves, day moods, chains, peaks and relax windows, echoes of real scares.
- **Symptoms** (`Symptoms/`): Flicker, Phantom fear, Phantom dread, Phantom sound, Phantom steps, Phantom walkie,
  Phantom hit, Locked door, Vanishing item, Lost time, the Presence. All from the game's own sounds and effects,
  local to your screen (except a key used on the fake locked door, which is really used up, and the hit's flinch
  holding back your animation sync for 1.2 s).
- **Compatibility**: GeneralImprovements and LethalHUD health and clock displays (`Compat/`), Lategame Upgrades
  (Locksmith, speed upgrades, longer days), Wesley's Weathers through WeatherRegistry, modded moons and interiors.
- **Config**: master switch, a switch per symptom, drain and recovery multipliers, phantom loudness.
- **Testing tools** (off by default): on-screen readout, the F7 debug menu, F8 (a random symptom that fits), the
  Presence marker, a local CSV log with reaction probes and player profiles, `analyze.py` and `replay.py`.
- **Release button**: a GitHub Actions workflow that builds and publishes to Thunderstore.
