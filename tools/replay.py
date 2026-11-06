"""Re-run recorded telemetry through the sanity formula, to test tuning changes before playing.

Usage (from the repo folder):
    python tools/replay.py path/to/Sanity_Telemetry/*.csv
    python tools/replay.py FILES... --set CompanyDrainMultiplier=0.7 --set ShipGainDayOpen=0.4

What it does: takes every logged second (inside, ship, company, time of day, fear) and every logged
event (damage, fear jumps, teammate deaths, loot payouts), and recomputes sanity with the numbers in TUNING below.
It assumes people would have played the same way, which is roughly fair because the meter is hidden.

KEEP IN SYNC: TUNING mirrors src/Sanity/SanityMeter.cs and SanityDirector.cs, WEATHER mirrors Weather.cs. Update it whenever the
C# formula or numbers change, or the replay stops matching the game.

Works with old CSVs too: company comes from the logged yes/no flag unless the file has the
nearest_teammate_m column (then the 10/14 m rule and the height limit are recomputed), and damage is
read from "health A->B" (falls are only halved when the file logs the cause).

Lobby multiplier: players at the start of each day come from the file's `players` column. Old files without it
use --players N, or 4 if not given (a note is printed).

Also follows what each file recorded: the dials and weather overrides in its SESSION row (--ignore-session to
skip), and each S row's game_dt (game time per row) when the file has it.
    python tools/replay.py FILES... --fair-share-from HOST.csv     (re-price everyone's loot with the host's count,
                                                                   e.g. for files made before the loot count fix)
"""
import argparse
import csv
import glob
import math
import random
import re
from collections import Counter, defaultdict

TUNING = {
    # tiers (lines below Calm, Uneasy, Disturbed) and deadband
    "Tier1": 75.0, "Tier2": 52.0, "Tier3": 25.0, "TierDeadband": 3.0,
    # drains per second
    "InsideDrain": 0.36, "SpiralMaxMultiplier": 1.5, "SpiralSeconds": 240.0, "InsideDrainCap": 0.495,
    "SpiralDecaySeconds": 15.0, "SpiralDecayCurve": 3.0,
    "OutsideDrain": 0.1, "NightDrainMax": 0.4, "SundownStart": 0.63, "MidnightStart": 0.9,
    "FearDrainMax": 0.5, "FearDrainStart": 0.1,
    "CompanyDrainMultiplier": 0.5, "NearbyEnterRadius": 10.0, "NearbyLeaveRadius": 14.0, "NearbyMaxHeight": 4.0,
    # lobby size rule (inside/outside/night drain multiplier by players at the start of the day), see lobby_multiplier
    "TeammateNearChance": 0.2, "LobbyAnchorPlayers": 3.0, "LobbyAnchorMultiplier": 1.1,
    "LobbyMultiplierMin": 0.6, "LobbyMultiplierMax": 1.2,
    # ship gain
    "ShipGainDayOpen": 0.35, "ShipGainDayClosed": 0.5, "ShipGainNightOpen": 0.1, "ShipGainNightClosed": 0.3,
    "CompanyGainMultiplier": 1.1, "ShipRampSeconds": 30.0, "ShipRecoveryCap": 85.0, "ShipBreakingBoost": 2.0,
    # instant drops
    "DamageToSanity": 0.2, "DamageMaxDrop": 10.0, "FallDamageMultiplier": 0.5,
    "TeammateDeathDrop": 10.0, "TeammateDeathShipDrop": 5.0, "TeammateDeathFarDrop": 2.0, "FearJumpDrop": 5.0,
    "RevivedSanity": 85.0,   # brought back mid-round (Lategame Upgrades' interns)
    # director (for the symptom estimate)
    # day length: drains and ship gain x (vanilla day length / this day's length) ^ exponent (0 = off)
    "DayReferenceMinutes": 12.85, "DayLengthDrainExponent": 0.0, "DayLengthGainExponent": 0.0, "DayLengthNightExponent": 0.0,
    # experiment: steady gain while a teammate is near you outside the ship, at Disturbed and below (0 = off)
    "CompanyRecovery": 0.0,
    # reliefs (SanityReliefs.cs): ways back up outside the ship (set the sizes to 0 to replay without them)
    # regroup relief: meeting a teammate after being alone (outside the ship) for at least RegroupAloneSeconds x 2/(n-1)
    # (60 s for 3 players, 40 s for 4, 120 s for 2; kept to 25..180 s) gives RegroupRelief, up to x2 for twice as long
    "RegroupRelief": 16.0, "RegroupReliefMaxFactor": 2.0, "RegroupAloneSeconds": 60.0, "RegroupAloneMin": 25.0, "RegroupAloneMax": 180.0,
    # breather: stepping out of the facility in daylight after at least BreatherInsideSeconds inside
    "BreatherRelief": 10.0, "BreatherInsideSeconds": 120.0,
    # bounce: after BounceAfterSeconds in Breaking (outside the ship), sanity jumps back to at least BounceTo; then for
    # ResilienceSeconds the inside, outside and night drains run at ResilienceDrainMultiplier
    "BounceAfterSeconds": 75.0, "BounceTo": 58.0, "ResilienceSeconds": 45.0, "ResilienceDrainMultiplier": 0.5,
    # find relief: picking up new scrap (a GRAB row "new scrap"), at most once per GrabReliefGapSeconds
    "GrabRelief": 2.0, "GrabReliefGapSeconds": 30.0,
    "MaxChancePerSecond": 0.044, "DepthFloor": 0.4, "DipFloor": 0.1, "DipRecoverySeconds": 8.0,
    # activity waves (DirectorLayers.cs): x1.6 for ~120 s, x0.4 for ~90 s, switching at random
    "WaveHigh": 1.6, "WaveLow": 0.4, "WaveHighSeconds": 120.0, "WaveLowSeconds": 90.0,
    "TensionDoublingSeconds": 120.0,
    # loot recovery (replayed from LOOT_RECOVERY rows in newer CSVs)
    "LootPool": 50.0, "LootMinPerItem": 1.0, "LootMaxPerItem": 15.0, "LootSoftLimit": 30.0,
}

# Weather: outside multiplier, inside multiplier, night head start. Mirrors Weather.cs. Unlisted = no effect.
WEATHER = {
    "none": (1.0, 1.0, 0.0), "clear": (1.0, 1.0, 0.0), "rainy": (1.15, 1.0, 0.0), "dustclouds": (1.3, 1.0, 0.0),
    "stormy": (1.3, 1.0, 0.0), "flooded": (1.25, 1.0, 0.0), "foggy": (1.3, 1.0, 0.0),
    "eclipsed": (1.4, 1.2, 0.3), "minefield": (1.2, 1.0, 0.0), "hurricane": (1.3, 1.0, 0.0), "earthquakes": (1.2, 1.2, 0.0),
    "hallowed": (1.25, 1.1, 0.2), "forsaken": (1.45, 1.3, 0.3),
}


def inv_lerp(a, b, x):
    if a == b:
        return 0.0
    return min(1.0, max(0.0, (x - a) / (b - a)))


def lerp(a, b, t):
    t = min(1.0, max(0.0, t))
    return a + (b - a) * t


def fade(x, k):
    return 0.0 if x >= 1 else 1 - (math.exp(k * x) - 1) / (math.exp(k) - 1)


def update_tier(value, tier, T):
    lines = [T["Tier1"], T["Tier2"], T["Tier3"]]
    while tier < 3 and value < lines[tier]:
        tier += 1
    while tier > 0 and value > lines[tier - 1] + T["TierDeadband"]:
        tier -= 1
    return tier


def session_settings(rows):
    """Dials and weather overrides recorded in the file's SESSION row (old files: dials 1/1, built-in weather)."""
    detail = next((r["detail"] for r in rows if r["event"] == "SESSION"), "")
    drain = recovery = 1.0
    m = re.search(r"dials drain ([\d.]+) recovery ([\d.]+)", detail)
    if m:
        drain, recovery = float(m.group(1)), float(m.group(2))
    weather = dict(WEATHER)
    m = re.search(r" overrides (.*?)(?:; loot |$)", detail)
    if m:
        for name, out, inside, start in re.findall(r"([A-Za-z][\w ]*)=([\d.]+)/([\d.]+)/([\d.]+)", m.group(1)):
            weather[name.replace(" ", "").lower()] = (float(out), float(inside), min(1.0, max(0.0, float(start))))
    return drain, recovery, weather


def fair_shares_from(path):
    """Each day's fair share as another file (the host's) logged it: {day: fair share}. The last count of a day wins."""
    shares = {}
    with open(path, newline="", encoding="utf-8") as f:
        for r in csv.DictReader(f):
            if r["event"] == "LEVEL_SCRAP":
                m = re.search(r"fair share ([\d.]+)", r["detail"])
                if m:
                    shares[r["day"]] = float(m.group(1))
    return shares


def instant_drops(rows, T, fair_override=None):
    """Events that cost sanity instantly, recomputed with the current numbers: {(day, rounded t): [kind, amount]}.
    fair_override: {day: fair share} to re-price loot with (e.g. the host's), instead of the file's own."""
    drops = []
    for r in rows:
        if r["kind"] != "E":
            continue
        ev, detail, t = r["event"], r["detail"], float(r["t"])
        if ev == "DAMAGE":
            m = re.search(r"health (\d+)->(\d+)", detail)
            if not m:
                continue
            hp = int(m.group(1)) - int(m.group(2))
            amount = min(hp * T["DamageToSanity"], T["DamageMaxDrop"])
            if "cause Gravity" in detail:
                amount *= T["FallDamageMultiplier"]
            drops.append([r["day"], t, "fixed", amount])
        elif ev == "FEAR_JUMP":
            drops.append([r["day"], t, "fixed", T["FearJumpDrop"]])
        elif ev == "TEAMMATE_DIED_NEARBY":
            drops.append([r["day"], t, "fixed", T["TeammateDeathDrop"]])
        elif ev in ("TEAMMATE_DIED", "TEAMMATE_DIED_SHIP"):
            drops.append([r["day"], t, "far", 0.0])
        elif ev == "REVIVED":
            drops.append([r["day"], t, "revive", T["RevivedSanity"]])
        elif ev == "LOOT_RECOVERY":
            m = re.search(r"value (\d+) fair share ([\d.]+) full [\d.]+ share (\d+)%", detail)
            if not m:
                continue
            fair = fair_override[r["day"]] if fair_override and r["day"] in fair_override else float(m.group(2))
            if fair > 0:
                full = min(max(T["LootPool"] * int(m.group(1)) / fair, T["LootMinPerItem"]), T["LootMaxPerItem"])
                drops.append([r["day"], t, "loot", full * int(m.group(3)) / 100.0])
    return drops


NOTES = set()       # things the summary should mention (e.g. a player count that had to be assumed)


def lobby_multiplier(players, T):
    """Mirrors SanityMeter.LobbyMultiplierFor: equal average drain to the anchor lobby, given expected company time."""
    def drain_share(n):
        with_company = 1 - (1 - T["TeammateNearChance"]) ** (max(1, n) - 1)
        return 1 - (1 - T["CompanyDrainMultiplier"]) * with_company
    value = T["LobbyAnchorMultiplier"] * drain_share(int(T["LobbyAnchorPlayers"])) / drain_share(players)
    return min(T["LobbyMultiplierMax"], max(T["LobbyMultiplierMin"], value))


def day_lengths(samples, T):
    """Each day's length factor: real minutes per full day / the vanilla reference (1 = vanilla, 2 = twice as long).
    Measured from how fast day_fraction moves, like the C# does live. Files can also carry it (DAY_LENGTH rows)."""
    by_day = defaultdict(list)
    for r in samples:
        f = float(r["day_fraction"])
        if f > 0:
            by_day[r["day"]].append((float(r["t"]), f))
    out = {}
    for d, pts in by_day.items():
        (t0, f0), (t1, f1) = pts[0], pts[-1]
        out[d] = ((t1 - t0) / (f1 - f0) / 60 / T["DayReferenceMinutes"]) if f1 - f0 > 0.03 else 1.0
    return out


def replay_file(path, T, players_default=None, fair_override=None, use_session=True):
    with open(path, newline="", encoding="utf-8") as f:
        rows = list(csv.DictReader(f))
    header = rows[0].keys() if rows else []
    has_distance = "nearest_teammate_m" in header
    has_players = "players" in header
    has_game_dt = "game_dt" in header
    samples = [r for r in rows if r["kind"] == "S" and r["dead"] == "0"]
    drops = instant_drops(rows, T, fair_override)

    # The dials and weather overrides the game ran with (from the SESSION row), so the replay matches it.
    drain_dial, recovery_dial, weather_table = session_settings(rows) if use_session else (1.0, 1.0, WEATHER)
    name = path.replace("\\", "/").split("/")[-1]
    if (drain_dial, recovery_dial) != (1.0, 1.0):
        NOTES.add(f"{name}: dials drain {drain_dial} recovery {recovery_dial} (from its SESSION row)")
    if weather_table != WEATHER:
        NOTES.add(f"{name}: weather overrides from its SESSION row")

    trace = []          # (day, sanity, tier, dt)
    grabs = defaultdict(list)      # day -> times of new-scrap grabs (per day, so other days are never consumed)
    for r in rows:
        if r["kind"] == "E" and r["event"] == "GRAB" and "new scrap" in r.get("detail", ""):
            grabs[r["day"]].append(float(r["t"]))
    for times in grabs.values():
        times.sort()
    grab_i = 0
    last_grab_relief = -1e9
    lengths = day_lengths(samples, T)
    DAY_FACTORS.update({(path, d): f for d, f in lengths.items()})
    day = None
    for r in samples:
        t = float(r["t"])
        if r["day"] != day:
            day = r["day"]
            value, tier, last_t = 100.0, 0, t
            loot_today = 0.0
            ship_s = spiral = at_exit = out_s = 0.0
            company = False
            # Lobby multiplier: players at the start of the day, fixed for the day (like the C#).
            if has_players and r["players"]:
                players = int(float(r["players"]))
            else:
                players = players_default or 4
                NOTES.add(f"no players column in some files: assumed {players} players (use --players N)")
            lobby = lobby_multiplier(players, T)
            factor = max(0.25, lengths.get(day, 1.0))
            alone_s = inside_visit = breaking_s = resilience = 0.0
            grab_i = 0
            was_company = was_inside = False
            regroup_need = min(T["RegroupAloneMax"], max(T["RegroupAloneMin"],
                               T["RegroupAloneSeconds"] * 2.0 / max(1, players - 1))) if players > 1 else 1e9
            drain_day = factor ** -T["DayLengthDrainExponent"]
            gain_day = factor ** -T["DayLengthGainExponent"]
            night_day = factor ** -T["DayLengthNightExponent"]
        # Game time this row stands for: logged by newer files (game_dt), else the clock gap (capped).
        if has_game_dt and r["game_dt"]:
            dt = float(r["game_dt"])
        else:
            dt = min(2.0, t - last_t)
        last_t = t

        inside = r["inside"] == "1"
        ship = r["in_ship"] == "1"
        doors = r["doors_closed"] == "1"
        night = inv_lerp(T["SundownStart"], T["MidnightStart"], float(r["day_fraction"]))
        weather_name = (r.get("weather") or "none").replace(" ", "").lower()
        w_out, w_in, w_floor = weather_table.get("none" if weather_name == "clear" else weather_name, (1.0, 1.0, 0.0))
        night = max(night, w_floor)   # head start: drain and gain both treat it as at least this late
        exposure = night
        fear = float(r["fear"])

        if has_distance:
            d, dy = float(r["nearest_teammate_m"]), float(r["teammate_dy"])
            radius = T["NearbyLeaveRadius"] if company else T["NearbyEnterRadius"]
            company = d >= 0 and abs(dy) <= T["NearbyMaxHeight"] and d <= radius
        else:
            company = r["near_teammate"] == "1"

        if inside:
            spiral += dt
            at_exit = min(spiral, T["SpiralSeconds"])
            out_s = 0.0
        else:
            out_s += dt
            spiral = at_exit * fade(out_s / T["SpiralDecaySeconds"], T["SpiralDecayCurve"])
        ship_s = ship_s + dt if ship else 0.0

        p_inside = T["InsideDrain"] * lerp(1, T["SpiralMaxMultiplier"], spiral / T["SpiralSeconds"]) * w_in if inside else 0.0
        p_outside = T["OutsideDrain"] * w_out if (not inside and not ship) else 0.0
        p_night = T["NightDrainMax"] * exposure * w_out if (not inside and (not ship or not doors)) else 0.0
        p_fear = T["FearDrainMax"] * inv_lerp(T["FearDrainStart"], 1.0, fear)
        p_ship = 0.0
        if ship and value < T["ShipRecoveryCap"]:
            day_gain = T["ShipGainDayClosed"] if doors else T["ShipGainDayOpen"]
            night_gain = T["ShipGainNightClosed"] if doors else T["ShipGainNightOpen"]
            ramp = min(1.0, ship_s / T["ShipRampSeconds"])
            boost = lerp(1.0, T["ShipBreakingBoost"], inv_lerp(T["Tier3"], 0.0, value))
            p_ship = lerp(day_gain, night_gain, night) * ramp * boost
        if company and not ship and value < T["Tier2"]:
            p_ship += T["CompanyRecovery"]
        if resilience > 0:
            p_inside *= T["ResilienceDrainMultiplier"]
            p_outside *= T["ResilienceDrainMultiplier"]
            p_night *= T["ResilienceDrainMultiplier"]
            resilience -= dt
        if company:
            p_inside *= T["CompanyDrainMultiplier"]
            p_outside *= T["CompanyDrainMultiplier"]
            p_night *= T["CompanyDrainMultiplier"]
            p_ship *= T["CompanyGainMultiplier"]
        p_inside = min(p_inside, T["InsideDrainCap"] * w_in)
        # lobby size scales the three company-softened drains, after the cap (fear untouched)
        p_inside *= lobby
        p_outside *= lobby
        p_night *= lobby
        # the player's dials last, like the C# (DrainMultiplier on all drains, RecoveryMultiplier on ship gain)
        p_inside *= drain_dial * drain_day
        p_outside *= drain_dial * drain_day
        p_night *= drain_dial * drain_day * night_day
        p_fear *= drain_dial
        p_ship *= recovery_dial * gain_day

        rate = p_ship - (p_inside + p_outside + p_night + p_fear)
        nxt = value + rate * dt
        if rate > 0 and value <= T["ShipRecoveryCap"]:
            nxt = min(nxt, T["ShipRecoveryCap"])
        value = min(100.0, max(0.0, nxt))

        for drop in drops:
            # A revive happens while no samples are written (dead), so it applies at the first sample after it.
            if drop[2] == "revive" and drop[0] == day and drop[1] <= t + 0.5:
                value = drop[3]
                ship_s = spiral = at_exit = out_s = 0.0
                company = False
                drop[2] = None
                continue
            if drop[0] == day and abs(drop[1] - t) <= 0.5 and drop[2]:
                amount = drop[3]
                if drop[2] == "loot":
                    room = max(0.0, T["LootSoftLimit"] - loot_today)
                    gain = amount if amount <= room else room + (amount - room) * 0.5
                    gain *= recovery_dial
                    before = value
                    value = min(100.0, value + gain)
                    loot_today += value - before
                    drop[2] = None
                    continue
                if drop[2] == "far":
                    amount = T["TeammateDeathShipDrop"] if ship else T["TeammateDeathFarDrop"]
                value = max(0.0, value - amount * drain_dial)   # instant drops follow the drain dial too
                drop[2] = None

        # reliefs (each off when its size is 0)
        today = grabs.get(day, [])
        while grab_i < len(today) and today[grab_i] <= t + 0.5:
            if T["GrabRelief"] > 0 and t - last_grab_relief >= T["GrabReliefGapSeconds"]:
                value = min(100.0, value + T["GrabRelief"])
                last_grab_relief = t
                EVENTS["grab"] += 1
            grab_i += 1
        if company and not was_company and alone_s >= regroup_need and T["RegroupRelief"] > 0 and value < T["Tier1"]:
            value = min(100.0, value + T["RegroupRelief"] * min(T["RegroupReliefMaxFactor"], alone_s / regroup_need))
            EVENTS["regroup"] += 1
        alone_s = 0.0 if (company or ship) else alone_s + dt
        was_company = company
        if was_inside and not inside and not ship and inside_visit >= T["BreatherInsideSeconds"] \
                and T["BreatherRelief"] > 0 and float(r["day_fraction"]) < T["SundownStart"]:
            value = min(100.0, value + T["BreatherRelief"])
            EVENTS["breather"] += 1
        inside_visit = inside_visit + dt if inside else 0.0
        was_inside = inside

        tier = update_tier(value, tier, T)
        breaking_s = breaking_s + dt if (tier == 3 and not ship) else 0.0
        if T["BounceAfterSeconds"] > 0 and breaking_s >= T["BounceAfterSeconds"]:
            value = max(value, T["BounceTo"])
            tier = update_tier(value, tier, T)
            resilience = T["ResilienceSeconds"]
            breaking_s = 0.0
            EVENTS["bounce"] += 1
        trace.append((day, value, tier, dt))
    return trace


EVENTS = Counter()


DAY_FACTORS = {}


def estimate_rolls(trace, T, runs=40):
    """Monte Carlo of the director's chance formula: symptom rolls per player-day (before rejections)."""
    days = len({d for d, *_ in trace}) or 1
    hits = 0
    calm_line = T["Tier1"]
    for _ in range(runs):
        day = None
        for d, value, tier, dt in trace:
            if d != day:
                day, quiet, since = d, 0.0, 999.0
                high = random.random() < T["WaveHighSeconds"] / (T["WaveHighSeconds"] + T["WaveLowSeconds"])
            since += dt
            if tier == 0:
                continue
            quiet += dt
            depth = T["DepthFloor"] + (1 - T["DepthFloor"]) * min(1.0, max(0.0, (calm_line - value) / calm_line))
            dip = 1 - (1 - T["DipFloor"]) * math.exp(-since / T["DipRecoverySeconds"])
            if random.random() < dt / (T["WaveHighSeconds"] if high else T["WaveLowSeconds"]):
                high = not high
            wave = T["WaveHigh"] if high else T["WaveLow"]
            chance = T["MaxChancePerSecond"] * depth * dip * (1 + quiet / T["TensionDoublingSeconds"]) * wave
            if random.random() < chance * dt:
                hits += 1
                quiet = since = 0.0
    return hits / runs / days


def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument("files", nargs="+", help="telemetry CSVs (globs allowed)")
    parser.add_argument("--set", action="append", default=[], metavar="NAME=VALUE",
                        help="override a tuning number, e.g. --set CompanyDrainMultiplier=0.7")
    parser.add_argument("--players", type=int, default=None,
                        help="players per day for old files without a players column (default 4)")
    parser.add_argument("--fair-share-from", metavar="FILE", default=None,
                        help="re-price every file's loot with this file's daily fair share (e.g. the host's CSV)")
    parser.add_argument("--ignore-session", action="store_true",
                        help="ignore the dials and weather overrides recorded in each file's SESSION row")
    args = parser.parse_args()
    fair_override = fair_shares_from(args.fair_share_from) if args.fair_share_from else None

    T = dict(TUNING)
    for item in args.set:
        name, _, raw = item.partition("=")
        if name not in T:
            raise SystemExit(f"Unknown tuning name: {name}. Known: {', '.join(sorted(T))}")
        T[name] = float(raw)

    paths = sorted({p for pattern in args.files for p in glob.glob(pattern)})
    if not paths:
        raise SystemExit("No files matched.")

    random.seed(1)
    names = ["Calm", "Uneasy", "Disturbed", "Breaking"]
    total = Counter()
    all_days = breaking_days = 0
    weighted_rolls = 0.0
    by_length = {"normal days": Counter(), "long days (x1.5+)": Counter()}
    changes = climbs = 0
    alive_seconds = 0.0
    spells = []
    print(f"{'file':40} {'Calm':>5} {'Uneasy':>7} {'Dist':>5} {'Break':>6}  days reaching Breaking  rolls/day")
    for path in paths:
        trace = replay_file(path, T, args.players, fair_override, not args.ignore_session)
        if not trace:
            continue
        tiers = Counter()
        lows = defaultdict(lambda: 100.0)
        for d, value, tier, dt in trace:
            tiers[tier] += dt
            lows[d] = min(lows[d], value)
            long_day = DAY_FACTORS.get((path, d), 1.0) >= 1.5
            by_length["long days (x1.5+)" if long_day else "normal days"][tier] += dt
        prev = None
        spell = 0.0
        for d, value, tier, dt in trace:
            alive_seconds += dt
            if prev is not None and prev[0] == d and tier != prev[1]:
                changes += 1
                climbs += tier < prev[1]
            if tier == 3 and prev is not None and prev[0] == d and prev[1] == 3:
                spell += dt
            elif tier == 3:
                spell = dt
            if prev is not None and prev[1] == 3 and (tier != 3 or prev[0] != d) and spell > 0:
                spells.append(spell)
                spell = 0.0
            prev = (d, tier)
        if prev is not None and prev[1] == 3 and spell > 0:
            spells.append(spell)
        seconds = sum(tiers.values())
        bdays = sum(1 for v in lows.values() if v < T["Tier3"])
        rolls = estimate_rolls(trace, T)
        shares = [100 * tiers[i] / seconds for i in range(4)]
        label = path.replace("\\", "/").split("/")[-1][:40]
        print(f"{label:40} {shares[0]:5.0f} {shares[1]:7.0f} {shares[2]:5.0f} {shares[3]:6.0f}  {bdays:>3}/{len(lows):<3}"
              f"                 {rolls:4.1f}")
        total += tiers
        all_days += len(lows)
        breaking_days += bdays
        weighted_rolls += rolls * len(lows)
    seconds = sum(total.values()) or 1
    shares = [100 * total[i] / seconds for i in range(4)]
    print(f"{'ALL':40} {shares[0]:5.0f} {shares[1]:7.0f} {shares[2]:5.0f} {shares[3]:6.0f}  {breaking_days:>3}/{all_days:<3}"
          f"                 {weighted_rolls / max(1, all_days):4.1f}")
    spells.sort()
    print(f"  dynamics: tier changes {changes / max(1.0, alive_seconds / 60):.2f}/min, climbs {climbs / max(1, all_days):.1f}/player-day, "
          f"Breaking spells {len(spells)} median {spells[len(spells) // 2] if spells else 0:.0f}s"
          + (f" p90 {spells[int(0.9 * (len(spells) - 1))]:.0f}s" if spells else "")
          + (f" | reliefs: {dict(EVENTS)}" if EVENTS else ""))
    for label, tiers in by_length.items():
        secs = sum(tiers.values())
        if secs:
            print(f"  {label:38} " + " ".join(f"{100 * tiers[i] / secs:5.0f}" for i in range(4)) + f"   ({secs / 60:.0f} min)")
    if args.set:
        print("overrides:", ", ".join(args.set))
    if fair_override:
        print(f"loot re-priced with the fair shares of {args.fair_share_from.replace(chr(92), '/').split('/')[-1]}: "
              + ", ".join(f"day {d} {s:.0f}" for d, s in sorted(fair_override.items(), key=lambda x: int(x[0]))))
    for note in sorted(NOTES):
        print("note:", note)


if __name__ == "__main__":
    main()
