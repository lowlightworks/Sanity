# Tools

Python scripts for reading the mod's data, and the stats endpoint. They're for tuning and testing the mod, not part
of it: they're not in the Thunderstore package and the game never runs them.

## The two kinds of data

| | Development log | Anonymous stats |
|---|---|---|
| Setting | `DetailedTestLog` (off) | `ShareAnonymousStats` (on; a one-time notice on the main menu offers to turn it off) |
| Where | a CSV per game launch in the mod's folder, `telemetry/` | one row per player-day in the stats spreadsheet |
| Detail | everything, every second | a daily summary (fields listed in the main README) |
| Used for | testing with your own group; exact replays | pooled numbers from everyone who shares; rough replays |

To read the stats, download the spreadsheet as CSV (File, Download, Comma-separated values) and pass that file to the
scripts. Duplicates are skipped; no day is thrown away. A day where the debug menu changed the meter or the director
(set or froze sanity, paused it, ignored its checks) is left out of only those numbers (time per tier, symptom rates,
the drain estimate) and used for everything else. Forced plays never reach the counts, and the readout, debug keys
and Presence marker change nothing. Days with a test tool installed (Imperium, UnityExplorer) are kept and counted:
they can explain odd values such as a day length of -1 or no monsters.

## Requirements

Python 3 (tested with 3.12). No extra packages are needed. Run the scripts from the repository folder; file
arguments accept wildcards (`*.csv`).

## analyze.py: what happened

```
python tools/analyze.py path/to/telemetry/*.csv
python tools/analyze.py path/to/file.csv --days
python tools/analyze.py stats.csv --per-moon
```

Development logs: per file, the time spent in each tier and where, which symptoms played and how each ended, how the
director's rolls went and what kept symptoms from playing, and any errors the mod logged (`--days` adds a table per
in-game day); then, across all files, how players reacted to each symptom (compared with random moments without
one), how they play, and how often each armed symptom's moment came up.

Stats export: the same questions over every shared day: tier split (overall and by lobby size; `--per-moon` adds one
per moon, since popular moons would otherwise dominate), symptoms per hour and by tier, reactions against the control
moments, phantom sounds by monster, ground and place, lost time, monsters on the moon, modded content, errors.

## replay.py: what if the numbers were different

```
python tools/replay.py path/to/telemetry/*.csv
python tools/replay.py path/to/telemetry/*.csv --set CompanyDrainMultiplier=0.7
python tools/replay.py stats.csv --set InsideDrain=0.4
```

Development logs: re-runs every recorded second through the sanity formula, assuming everyone played the same way
(fair enough, since the meter is hidden). Prints each file's tier split, the days that reached Breaking, symptom rolls
per day, and how often the tier changed.

Stats export: there's no per-second trace, so it estimates each day's total drain and gain from its time budget and
shows how a `--set` change would move them. It gives the direction and rough size of a change, not exact numbers.

| Option | What it does |
|---|---|
| `--set NAME=VALUE` | Change a tuning number (names: `TUNING` at the top of `replay.py`). Repeat for several. |
| `--players N` | Lobby size for old logs that didn't record it (default 4). |
| `--fair-share-from FILE` | Price everyone's loot with this log's count of the day's scrap (use the host's file). |
| `--ignore-session` | Ignore the multipliers and weather overrides each log recorded. |

`replay.py` has its own copy of the mod's numbers: `TUNING` mirrors `SanityMeter.cs`, `SanityReliefs.cs` and
`SanityDirector.cs`, and `WEATHER` mirrors `Weather.cs`. When a number or formula changes in the C# code, change it
there too, or replays stop matching the game.

## stats-endpoint/Code.gs: where the stats go

A Google Apps Script attached to a Google Sheet. The mod sends each packet to its web address; the script checks that
it looks like a Sanity packet and appends it as one row. It does nothing else. Setting it up (once):

1. Create a Google Sheet (any name).
2. In it: Extensions, Apps Script. Replace the code with `Code.gs`, and save.
3. Deploy, New deployment, type "Web app". Execute as: Me. Who has access: Anyone. Deploy, and allow the access it
   asks for (Google warns that the app isn't verified: Advanced, then go to the project).
4. Copy the web app address into `Endpoint` in `src/Sanity/StatsUploader.cs`.

Changing the script later: Deploy, Manage deployments, edit the existing deployment and pick "New version", so the
address stays the same. The address is public (it's in the mod), so the script only accepts packets of the right shape
and size; expect the odd bad row anyway.
