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


def load_stats(paths, keep_debug=False):
    """All packets from the given exports, each once (session + day), test days left out unless keep_debug.
    Returns (packets, notes)."""
    seen, packets = set(), []
    duplicates = debug_days = broken = 0
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
                if not keep_debug and used_debug(packet):
                    debug_days += 1
                    continue
                packets.append(packet)
    notes = f"{len(packets)} player-days ({duplicates} duplicates, {debug_days} test days left out, {broken} unreadable)"
    return packets, notes


def used_debug(packet):
    """A day where the debug tools were on or symptoms were forced: not normal play."""
    debug = packet.get("debug", {})
    return bool(debug.get("keys") or debug.get("readout") or debug.get("presence_marker")
                or debug.get("forced_plays", 0) or debug.get("debug_actions", 0))


def get(packet, *path, default=0):
    """A nested value, or default when any part is missing (older or partial packets)."""
    value = packet
    for key in path:
        if not isinstance(value, dict) or key not in value:
            return default
        value = value[key]
    return value
