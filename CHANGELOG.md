# Changelog

## 0.0.3 (unreleased)

- The locked door and the vanishing item can now happen from Uneasy (a little less often there than lower down).
- Lost time no longer takes one of the two slots for symptoms waiting for their moment, so it doesn't keep the
  flicker, the locked door or the vanishing item from happening.
- A feedback form and a Discussions page for stories, ideas and questions (links in the README).
- Fixed the day-length measurement: a normal day now reads as 1x (it read 0.71x). Lost time's "6 minutes after
  landing" now really means 6 minutes on a normal day (it came after about 4).

## 0.0.2 (2026-11-06)

- Phantom sounds and phantom steps no longer get skipped while you sprint: they wait up to 10 s for you to slow down.
- Lost time reworked: it's now always night when it happens (8-10 PM on the clock), the game's own music cue for a new
  part of the day plays, and it lasts 12 s. It needs an unbroken 3-minute trip inside and at least 6 minutes since
  landing (longer on longer days), and it can happen on more days. It's applied as you go through the exit, so it
  never switches in front of you, and it stays out of the way once the ship is leaving.
- Lost time is followed by a walkie click more often.

## 0.0.1 (2026-11-06)

First public release. Very early and still rough: it's out so it's easy to install and share, not because it's
finished. Expect rough edges, and balance changes between versions.

- Hidden sanity meter: drains inside (faster the longer you stay), outside, at night, and with fear, scaled by weather and lobby size; recovers in the ship and with loot brought home. Damage, nearby deaths and fear spikes cause instant drops.
- Reliefs: regrouping after time alone, a breather outside after a long trip inside, finding scrap, and a bounce back out of Breaking.
- Tiers (Calm, Uneasy, Disturbed, Breaking) with a deadband.
- Symptom director: chance-based timing that rises with low sanity and quiet time, soft limits instead of hard cooldowns; armed symptoms that wait for the right moment; waves, day moods, chains, echoes, peaks and relax windows.
- Symptoms: Flicker, Phantom fear, Phantom dread, Phantom sound, Phantom steps, Phantom walkie, Phantom hit, Locked door, Vanishing item, Lost time, The Presence.
- Compatibility: GeneralImprovements and LethalHUD health/clock displays, Lategame Upgrades (Locksmith, speed, day length), Wesley's Weathers (through WeatherRegistry).
- Config: master switch, per-symptom switches, drain/recovery multipliers, phantom loudness.
- Testing tools (all off by default): on-screen readout, F7 debug menu, F8, local CSV telemetry (with reaction measurement and player profiles), analyze.py and replay.py.
