# Tools

Two Python scripts that read the mod's telemetry files, for tuning and testing. They aren't part of the mod: they're
not in the Thunderstore package and the game never runs them.

## Setup

1. Install Python 3 (tested with 3.12). No extra packages are needed.
2. Turn on `EnableTelemetry` in `BepInEx/config/Lowlight.Sanity.cfg` (or in LethalConfig).
3. Play. Each game session writes one CSV to `BepInEx/Sanity_Telemetry/`. In a group, every player with telemetry on
   gets their own file.

The files contain in-game player names, so check them before sharing.

Run both scripts from the repository folder. File arguments accept wildcards (`*.csv`).

## analyze.py: what happened

```
python tools/analyze.py path/to/Sanity_Telemetry/*.csv
python tools/analyze.py path/to/file.csv --days
```

Per file: the time spent in each tier and where, which symptoms played and how each ended, how the director's rolls
went and what kept symptoms from playing, and any errors the mod logged. `--days` adds a table per in-game day.

Across all files: how players reacted to each symptom (compared with random moments without one), how they play, and
how often each armed symptom's moment came up.

## replay.py: what if the numbers were different

```
python tools/replay.py path/to/Sanity_Telemetry/*.csv
python tools/replay.py path/to/Sanity_Telemetry/*.csv --set CompanyDrainMultiplier=0.7
```

Re-runs the recorded sessions through the sanity formula, assuming everyone played the same way (fair enough, since
the meter is hidden). Prints each file's tier split, the days that reached Breaking, symptom rolls per day, and how
often the tier changed.

| Option | What it does |
|---|---|
| `--set NAME=VALUE` | Change a tuning number (names: `TUNING` at the top of `replay.py`). Repeat for several. |
| `--players N` | Lobby size for old files that didn't record it (default 4). |
| `--fair-share-from FILE` | Price everyone's loot with this file's count of the day's scrap (use the host's file). |
| `--ignore-session` | Ignore the multipliers and weather overrides each file recorded. |

## Keeping replay.py in sync

`replay.py` has its own copy of the mod's numbers: `TUNING` mirrors `SanityMeter.cs`, `SanityReliefs.cs` and
`SanityDirector.cs`, and `WEATHER` mirrors `Weather.cs`. When a number or formula changes in the C# code, change it
there too, or replays stop matching the game.
