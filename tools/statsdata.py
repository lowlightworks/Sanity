"""Reading the anonymous stats: the spreadsheet the stats endpoint fills (downloaded as CSV).

Each row is one player's day: a few plain columns (received, schema, mod, session, day, moon, players) and the whole
packet as JSON in the "json" column. Used by analyze.py and replay.py; not run on its own.
"""
import csv
import json


def is_stats_file(path):
    """True for a stats export (a "json" column), False for a detailed local log."""
    with open(path, newline="", encoding="utf-8") as f:
        header = next(csv.reader(f), [])
    return "json" in header


def load_stats(paths):
    """Every packet from the given exports, each once (session + day). No day is thrown away: days where the debug
    menu changed the meter or the director are marked (meter_touched) and only left out of those numbers.
    Returns (packets, notes)."""
    seen, packets = set(), []
    duplicates = broken = 0
    for path in paths:
        with open(path, newline="", encoding="utf-8") as f:
            for row in csv.DictReader(f):
                try:
                    packet = json.loads(row.get("json", ""))
                except ValueError:
                    broken += 1
                    continue
                key = (packet.get("session"), packet.get("day"))
                if key in seen:
                    duplicates += 1
                    continue
                seen.add(key)
                packets.append(packet)
    touched = sum(1 for p in packets if meter_touched(p))
    tooled = sum(1 for p in packets if test_tools(p))
    notes = (f"{len(packets)} player-days ({duplicates} duplicates and {broken} unreadable skipped; {touched} with the "
             f"debug menu changing the meter or director, {tooled} with a test tool installed)")
    return packets, notes


TEST_TOOLS = ("Imperium", "UnityExplorer")


def meter_touched(packet):
    """A day where the debug menu changed the meter or the director (set or froze sanity, paused it, ignored its
    checks, forced chains). Its meter and director numbers (time per tier, symptom rates, drain) aren't normal, so
    they're left out of those; everything else from the day (reactions, sounds, hits, monsters, content, errors) is
    still used. Forced plays (F8, menu buttons) never reach the packet's counts, and the readout, debug keys and
    Presence marker only show things, so they don't count."""
    return get(packet, "debug", "debug_actions", default=0) > 0


def test_tools(packet):
    """Test tools installed that day (Imperium, UnityExplorer). The day is kept: they're often only used for small
    things, but they can stop the clock or the spawns, so they explain odd values (day_length -1, no monsters)."""
    mods = packet.get("mods", {})
    return [tool for tool in TEST_TOOLS if mods.get(tool)]


def get(packet, *path, default=0):
    """A nested value, or default when any part is missing (older or partial packets)."""
    value = packet
    for key in path:
        if not isinstance(value, dict) or key not in value:
            return default
        value = value[key]
    return value
