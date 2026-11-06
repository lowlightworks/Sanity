# Changelog

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
