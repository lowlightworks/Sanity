"""Summarise telemetry: what each player went through, what the director did, what looks off.

Reads either kind of file:
  - detailed local logs (the development setting): one CSV per game launch, from the mod's telemetry/ folder;
  - the anonymous stats spreadsheet, downloaded as CSV (one row per player-day): a pooled report instead.

Usage (from the repo folder):
    python tools/analyze.py path/to/telemetry/*.csv
    python tools/analyze.py FILE --days        (local logs: adds a per-day table)
    python tools/analyze.py stats.csv          (the stats export; --per-moon adds a table per moon)

Reads old and new CSV formats. Natural symptoms are counted separately from debug/F8 ones.
"""
import argparse
import csv
import glob
import math
import re
import statistics
from collections import Counter, defaultdict

from statsdata import get, is_stats_file, load_stats

TIERS = ["Calm", "Uneasy", "Disturbed", "Breaking"]

# ROLL row: "<tier> <where> <company|alone> chance X[ F8] | Key=wN Key=[reason] ... | Key:rejected[...] Key:played -> Key"
ROLL_STATUS = re.compile(r"(\w+)=(?:w([\d.]+)|\[([^\]]*)\])")
ROLL_PICK = re.compile(r"(\w+):(rejected|failed|played|armed|held)(?:\[([^\]]*)\])?")


def kind(reason):
    """The kind of a 'can't play' reason: the part before its colon ('fear: 0.15 (...)' -> 'fear')."""
    return reason.split(":", 1)[0] if ":" in reason else reason


def summarise_rolls(events):
    """Natural rolls (ROLL rows without F8): how often each symptom was in the lottery, what kept it out, what the draws did."""
    rolls = []
    for r in events:
        if r["event"] != "ROLL":
            continue
        parts = r["detail"].split(" | ")
        if len(parts) >= 3 and " F8" not in parts[0]:
            rolls.append(parts)
    if not rolls:
        return
    per_tier = defaultdict(Counter)
    in_lottery = defaultdict(Counter)   # symptom -> tier -> rolls it was in
    kept_out = defaultdict(Counter)     # symptom -> kind of reason -> rolls
    drawn = defaultdict(Counter)        # symptom -> rejected / failed / played
    fails = Counter()
    for context, statuses, picks in (p[:3] for p in rolls):
        tier = context.split()[0]
        per_tier[tier]["rolls"] += 1
        for key, weight, reason in ROLL_STATUS.findall(statuses):
            if weight:
                in_lottery[key][tier] += 1
            else:
                kept_out[key][kind(reason)] += 1
        draws, _, played = picks.rpartition(" -> ")
        for key, what, why in ROLL_PICK.findall(draws):
            drawn[key][what] += 1
            if what == "failed":
                fails[f"{key}: {why}"] += 1
        per_tier[tier]["played" if played.strip() != "none" else "nothing"] += 1
    print("natural rolls: " + ", ".join(
        f"{t} {per_tier[t]['rolls']} (played {pct(per_tier[t]['played'], per_tier[t]['rolls'])})"
        for t in TIERS if per_tier[t]["rolls"]))
    for key in sorted(set(in_lottery) | set(kept_out) | set(drawn)):
        lottery = " ".join(f"{t[0]} {in_lottery[key][t]}/{per_tier[t]['rolls']}" for t in TIERS[1:] if per_tier[t]["rolls"])
        out = ", ".join(f"{k} {n}" for k, n in kept_out[key].most_common(3))
        d = drawn[key]
        print(f"  {key:13} in lottery {lottery:28} drawn: played {d['played']} armed {d['armed']} held {d['held']} rejected {d['rejected']} failed {d['failed']}"
              + (f" | kept out by: {out}" if out else ""))
    for reason, n in fails.most_common(5):
        print(f"  failed after being drawn x{n}: {reason}")


def summarise_eligibility(events):
    """DAY_ELIGIBILITY rows: share of the possible time each symptom could have played, and the main blockers."""
    rows = [r for r in events if r["event"] == "DAY_ELIGIBILITY"]
    if not rows:
        return
    sampled = Counter()
    could = defaultdict(Counter)
    blocked = defaultdict(Counter)
    for r in rows:
        parts = r["detail"].split(" | ")
        for token in parts[0].split()[1:]:
            name, _, seconds = token.partition("=")
            sampled[name] += float(seconds)
        for part in parts[1:]:
            key, _, rest = part.partition(" ")
            main, _, blockers = rest.partition(" blocked=")
            for token in main.split():
                name, _, seconds = token.partition("=")
                could[key][name] += float(seconds)
            for item in filter(None, blockers.split("/")):
                name, _, seconds = item.rpartition(":")
                blocked[key][name] += float(seconds)
    columns = ["U", "D", "B", "inside", "outside", "ship"]
    print("could play (% of time symptoms were possible): sampled "
          + " ".join(f"{c} {sampled[c]:.0f}s" for c in columns))
    for key in could:
        total_blocked = sum(blocked[key].values())
        top = ", ".join(f"{name} {pct(seconds, total_blocked)}" for name, seconds in blocked[key].most_common(3))
        print(f"  {key:13} " + " ".join(f"{c} {pct(could[key][c], sampled[c]):>4}" for c in columns)
              + (f" | blocked by: {top}" if top else ""))


def load(path):
    with open(path, newline="", encoding="utf-8") as f:
        return list(csv.DictReader(f))


def pct(part, whole):
    return f"{100 * part / whole:.0f}%" if whole else "-"


def summarise(path, show_days):
    rows = load(path)
    samples = [r for r in rows if r["kind"] == "S" and r["dead"] == "0"]
    events = [r for r in rows if r["kind"] == "E"]
    name = path.replace("\\", "/").split("/")[-1]
    print(f"\n=== {name}")

    session = next((r["detail"] for r in events if r["event"] == "SESSION"), None)
    if session:
        print("build:", session[:160] + ("..." if len(session) > 160 else ""))
    else:
        print("build: (old file, no SESSION row)")

    alive = len(samples)
    tiers = Counter(r["tier"] for r in samples)
    print(f"alive on moons: {alive} s over {len({r['day'] for r in samples})} days | "
          + " ".join(f"{t} {pct(tiers[t], alive)}" for t in TIERS))

    where = Counter()
    for r in samples:
        place = "ship" if r["in_ship"] == "1" else "inside" if r["inside"] == "1" else "outside"
        where[place + (" +team" if r["near_teammate"] == "1" else " alone")] += 1
    print("where:", ", ".join(f"{k} {pct(v, alive)}" for k, v in sorted(where.items())))

    ev = Counter(r["event"] for r in events)
    # Armed symptoms fire later with "(armed N s before)"; they count as natural (how they were armed is in SYMPTOM_ARMED).
    def is_forced(detail):
        return "(" in detail and "(armed " not in detail and "(chain from " not in detail
    natural = Counter(r["detail"].split(" (")[0] for r in events if r["event"] == "SYMPTOM" and not is_forced(r["detail"]))
    chained = Counter(r["detail"].split(" (")[0] for r in events if r["event"] == "SYMPTOM" and "(chain from " in r["detail"])
    if chained:
        print("  of which chained follow-ups:", dict(chained))
    for name in ("MOOD", "RELIEF", "BOUNCE", "RELAX", "ECHO"):
        rows_ = [r["detail"] for r in events if r["event"] == name]
        if rows_:
            kinds = Counter(re.split(r"[ :]", d)[0] for d in rows_)
            print(f"  {name.lower()}: {len(rows_)} {dict(kinds)}")
    chain_rows = [r["detail"] for r in events if r["event"] == "CHAIN"]
    if chain_rows:
        print(f"  chains: scheduled {sum(1 for c in chain_rows if c.endswith('scheduled'))}, played {sum(1 for c in chain_rows if c.endswith('played'))}, "
              f"didn't {sum(1 for c in chain_rows if 'didn' in c)}")
    forced = Counter(r["detail"].split(" (")[0] for r in events if r["event"] == "SYMPTOM" and is_forced(r["detail"]))
    print("symptoms natural:", dict(natural) or "none", "| forced (debug/F8):", dict(forced) or "none")
    rejected = Counter(r["detail"].split(" turned")[0] for r in events if r["event"] == "SYMPTOM_REJECTED")
    failed = Counter(r["detail"] for r in events if r["event"] == "SYMPTOM_FAILED")
    print(f"rolls: rejected {sum(rejected.values())} {dict(rejected)}, nothing eligible {ev['ROLL_NOTHING_ELIGIBLE']}")
    for reason, n in failed.most_common(6):
        print(f"  failed x{n}: {reason}")
    summarise_rolls(events)
    summarise_eligibility(events)

    sounds = [r["detail"] for r in events if r["event"] == "PHANTOM_SOUND"]
    monsters = Counter(re.split(r" x\d", s)[0] for s in sounds)
    if sounds:
        print("phantom sounds:", dict(monsters))
    lasted = [float(r["value"]) for r in events if r["event"] == "PHANTOM_SOUND_END"]
    if lasted:
        print(f"  durations: min {min(lasted):.1f}s, median {sorted(lasted)[len(lasted) // 2]:.1f}s, max {max(lasted):.1f}s")
    flickers = [float(r["value"]) for r in events if r["event"] == "FLICKER"]
    if flickers:
        print(f"flicker light distances: {', '.join(f'{d:.1f}' for d in flickers)}")
        how = Counter("standing in" if "standing in" in r["detail"] else "looking at"
                      for r in events if r["event"] == "FLICKER")
        print(f"  fired by: {dict(how)}")

    drops = defaultdict(float)
    for r in events:
        m = re.search(r"sanity -([\d.]+)", r["detail"])
        if m:
            drops[r["event"]] += float(m.group(1))
    if drops:
        print("instant drops:", ", ".join(f"{k} -{v:.0f}" for k, v in sorted(drops.items())))

    deaths = [r["detail"] for r in events if r["event"] == "DIED"]
    print(f"deaths: {len(deaths)} {dict(Counter(deaths))}" if deaths else "deaths: 0")

    steps = [r["detail"] for r in events if r["event"] == "PHANTOM_STEPS"]
    if steps:
        modes = Counter(s.split(" ")[0] for s in steps)
        ends = [r["detail"] for r in events if r["event"] == "PHANTOM_STEPS_END"]
        played = [int(float(r["value"])) for r in events if r["event"] == "PHANTOM_STEPS_END"]
        endings = Counter(re.sub(r" after [\d.]+s$", "", e.split(" ended ", 1)[1]) if " ended " in e else e for e in ends)
        print(f"phantom steps: {len(steps)} {dict(modes)}, steps heard per episode {dict(sorted(Counter(played).items()))}, "
              f"ended: {dict(endings)}")
    walkie = [r["detail"] for r in events if r["event"] == "PHANTOM_WALKIE"]
    if walkie:
        kinds = Counter(w.split(" implied sender ")[0].split(" ")[0] for w in walkie)
        ends = Counter(re.sub(r" after [\d.]+s$", "", r["detail"]) for r in events if r["event"] == "PHANTOM_WALKIE_END")
        print(f"phantom walkie: {len(walkie)} {dict(kinds)}, ended: {dict(ends)}")

    armed = [r["detail"] for r in events if r["event"] == "SYMPTOM_ARMED"]
    if armed:
        fired = [r["detail"] for r in events if r["event"] == "SYMPTOM" and "(armed " in r["detail"]]
        dropped = [r["detail"] for r in events if r["event"] == "SYMPTOM_DROPPED"]
        waits = [float(re.search(r"\(armed (\d+) s before\)", f).group(1)) for f in fired if re.search(r"\(armed (\d+) s before\)", f)]
        print(f"armed symptoms: armed {dict(Counter(a.split(':')[0] for a in armed))}, "
              f"fired {dict(Counter(f.split(' (armed')[0] for f in fired))}, "
              f"dropped {dict(Counter(d.split(': ', 1)[1] if ': ' in d else d for d in dropped))}"
              + (f", wait median {sorted(waits)[len(waits) // 2]:.0f}s max {max(waits):.0f}s" if waits else ""))
    for name, label in (("FAKE_DOOR", "locked door"), ("VANISH", "vanishing item"), ("LOST_TIME", "lost time")):
        shown = [r["detail"] for r in events if r["event"] == name]
        if shown:
            ends = Counter(re.sub(r" after [\d.]+s$", "", r["detail"]) for r in events if r["event"] == name + "_END")
            print(f"{label}: {len(shown)}, ended: {dict(ends)}")
            for d in shown[:6]:
                print(f"  {d}")

    # Reactions: what the player did right after a symptom (needs the speed/sprint columns; older files lack them).
    replies = [r["detail"] for r in events if r["event"] == "PHANTOM_WALKIE_REPLY"]
    if walkie or replies:
        print(f"  walkie answered after a phantom transmission: {len(replies)} of {len(walkie)}"
              + (f" ({'; '.join(replies[:4])})" if replies else ""))
    after = [r["detail"] for r in events if r["event"] == "FAKE_DOOR_AFTER"]
    if after:
        print(f"  opened the 'locked' door again within a minute: {len(after)} ({'; '.join(after[:4])})")
    errors = [r["detail"] for r in events if r["event"] == "SANITY_ERROR"]
    if errors:
        print(f"ERRORS inside Sanity ({len(errors)}): " + " | ".join(errors[:5]))
    if samples and "speed" in samples[0]:
        timed = sorted(((float(r["t"]), r) for r in samples if r.get("speed") not in (None, "")), key=lambda x: x[0])
        times = [t for t, _ in timed]
        import bisect
        reactions = defaultdict(list)
        for r in events:
            if r["event"] != "SYMPTOM":
                continue
            t = float(r["t"])
            i = bisect.bisect_left(times, t)
            before = [float(x["speed"]) for _, x in timed[max(0, i - 5):i]]
            later = [x for _, x in timed[i:i + 5]]
            if before and later:
                reactions[r["detail"].split(" (")[0]].append((
                    sum(before) / len(before), sum(float(x["speed"]) for x in later) / len(later),
                    any(x.get("sprint") == "1" for x in later)))
        if reactions:
            print("reactions (speed 5 s before -> 5 s after, share sprinting after):")
            for key, items in sorted(reactions.items()):
                b = sum(i[0] for i in items) / len(items)
                a = sum(i[1] for i in items) / len(items)
                sp = sum(1 for i in items if i[2]) / len(items)
                print(f"  {key}: {len(items)}x  {b:.1f} -> {a:.1f} m/s, sprinting after {sp * 100:.0f}%")
    for r in events:
        if r["event"] == "DAY_LENGTH":
            print(f"  day {r['day']} {r['moon']}: {r['detail']}")
    for r in events:
        if r["event"] == "DAY_END":
            m = re.search(r"fps=(\d+) fps_min=(\d+)", r["detail"])
            if m:
                print(f"  day {r['day']} {r['moon']}: fps avg {m.group(1)}, slowest second {m.group(2)}")

    hits = [r for r in events if r["event"] == "PHANTOM_HIT"]
    if hits:
        counts = [int(float(r["value"])) for r in hits]
        print(f"phantom hits: {len(hits)} episodes, hits per episode {dict(sorted(Counter(counts).items()))}, "
              f"ended: {dict(Counter(r['detail'] for r in events if r['event'] == 'PHANTOM_HIT_END'))}")
        healed = [r for r in events if r["event"] == "PHANTOM_HIT_HEALED"]
        print(f"  crept back fully: {len(healed)} (the rest cleared by death, leaving or the day ending)")

    loot = [r for r in events if r["event"] == "LOOT_RECOVERY"]
    if loot:
        roles = Counter(re.search(r"\((finder\+hauler|finder|hauler)\)", r["detail"]).group(1) if "(" in r["detail"] else "none" for r in loot)
        gained = sum(float(r["value"]) for r in loot)
        print(f"loot recovery: {len(loot)} payouts, +{gained:.1f} sanity, roles {dict(roles)}")

    deposits = [int(float(r["value"])) for r in events if r["event"] == "DEPOSIT"]
    team = [int(float(r["value"])) for r in events if r["event"] == "DEPOSIT_TEAMMATE"]
    if deposits or team:
        print(f"loot brought to ship: you {len(deposits)} items worth {sum(deposits)}, teammates seen {len(team)} worth {sum(team)}")
    for r in events:
        if r["event"] == "SCRAP_SYNC":
            print(f"  day {r['day']} {r['moon']}: scrap values arrived: {r['detail']}")
        elif r["event"] == "LEVEL_SCRAP":
            print(f"  day {r['day']} {r['moon']}: {r['detail']}")
    queued = [r for r in events if r["event"] == "LOOT_QUEUED"]
    if queued:
        print(f"  loot let go before the day's loot was counted (paid once counted): {len(queued)}")

    for r in events:
        if r["event"] == "DAY_START" and " lights " in r["detail"]:
            m = re.search(r"interior (\S+) players (\d+) lobby x([\d.]+) lights (\S+)", r["detail"])
            if m:
                print(f"  day {r['day']} {r['moon']}: interior {m.group(1)}, players {m.group(2)} (drain x{m.group(3)}), "
                      f"flickerable lights/animators {m.group(4)}")

    if show_days:
        print(f"  {'day':>3} {'moon':18} {'weather':12} {'alive':>5} {'min':>5}  calm/uneasy/dist/break   symptoms deaths  loot")
        by_day = defaultdict(list)
        for r in samples:
            by_day[(int(r["day"]), r["moon"])].append(r)
        for (d, moon), rs in sorted(by_day.items()):
            c = Counter(r["tier"] for r in rs)
            low = min(float(r["sanity"]) for r in rs)
            sym = sum(1 for r in events if r["event"] == "SYMPTOM" and r["day"] == str(d))
            died = sum(1 for r in events if r["event"] == "DIED" and r["day"] == str(d))
            weather = rs[0].get("weather", "") or ""
            loot_gain = sum(float(r["value"]) for r in events if r["event"] == "LOOT_RECOVERY" and r["day"] == str(d))
            print(f"  {d:>3} {moon[:18]:18} {weather[:12]:12} {len(rs):>5} {low:5.1f}  "
                  + "/".join(pct(c[t], len(rs)) for t in TIERS) + f"   {sym:>8} {died:>6}  +{loot_gain:.1f}")


# ---- Reactions (real vs control), profiles, opportunities, projections ----

FEATURES = [  # (key, label, only among probes where ...)
    ("turned", "turned toward the source", "dir"),
    ("away", "moved away from it", "dir"),
    ("toward", "moved toward it", "dir"),
    ("stopped", "stopped (was moving 2+ m/s)", "moving"),
    ("sprinted", "started sprinting", None),
    ("scan", "scanned", None),
    ("use", "pressed use", None),
    ("walkie", "pressed their walkie (carrying one)", "walkie"),
]


def kv(detail):
    return dict(part.split("=", 1) for part in detail.split() if "=" in part)


def wilson(k, n, z=1.96):
    if n == 0:
        return 0.0, 0.0, 0.0
    p = k / n
    d = 1 + z * z / n
    c = (p + z * z / (2 * n)) / d
    h = z * math.sqrt(p * (1 - p) / n + z * z / (4 * n * n)) / d
    return p, max(0.0, c - h), min(1.0, c + h)


def newcombe(k1, n1, k2, n2):
    """Difference of two proportions with Newcombe's hybrid score interval (good at small n)."""
    p1, l1, u1 = wilson(k1, n1)
    p2, l2, u2 = wilson(k2, n2)
    d = p1 - p2
    return d, d - math.sqrt((p1 - l1) ** 2 + (u2 - p2) ** 2), d + math.sqrt((u1 - p1) ** 2 + (p2 - l2) ** 2)


def applies(probe, only):
    if only == "dir":
        return probe.get("dir") == "1"
    if only == "moving":
        return float(probe.get("v_b", 0)) >= 2.0
    if only == "walkie":
        return probe.get("has_walkie") == "1"
    return True


def summarise_reactions(all_events):
    probes = [kv(r["detail"]) for r in all_events if r["event"] == "REACTION"]
    if not probes:
        return
    clean = [p for p in probes if p.get("confound") == "none"]
    controls = [p for p in clean if p.get("kind") == "control"]
    print(f"\n=== reactions: {len(probes)} probes, {len(clean)} unconfounded, {len(controls)} controls")
    print("    lift = rate after the symptom - rate at random control moments (same detector), 95% interval;")
    print("    controls are re-weighted to each symptom's mix of place and moving (standardised). * = interval excludes 0.")
    conf = Counter(p.get("confound") for p in probes if p.get("confound") != "none")
    if conf:
        print("    left out as confounded:", dict(conf))
    by_name = defaultdict(list)
    for p in clean:
        if p.get("kind") == "real":
            by_name[p.get("name")].append(p)
    for name, reals in sorted(by_name.items(), key=lambda x: -len(x[1])):
        lines = []
        for key, label, only in FEATURES:
            r = [p for p in reals if applies(p, only)]
            c = [p for p in controls if applies(p, only)]
            if len(r) < 3 or len(c) < 5:
                continue
            k1 = sum(1 for p in r if p.get(key) == "1")
            # standardise the control rate to this symptom's strata (place x moving)
            strata = Counter((p.get("place"), p.get("moving")) for p in r)
            rate_c, weight = 0.0, 0
            for stratum, count in strata.items():
                cs = [p for p in c if (p.get("place"), p.get("moving")) == stratum]
                if cs:
                    rate_c += count * sum(1 for p in cs if p.get(key) == "1") / len(cs)
                    weight += count
            rate_c = rate_c / weight if weight else sum(1 for p in c if p.get(key) == "1") / len(c)
            k2 = round(rate_c * len(c))
            d, lo, hi = newcombe(k1, len(r), k2, len(c))
            star = "*" if lo > 0 or hi < 0 else " "
            lines.append(f"      {label:38} {100 * k1 / len(r):4.0f}% vs {100 * rate_c:4.0f}%  lift {100 * d:+4.0f} [{100 * lo:+.0f}, {100 * hi:+.0f}]{star} (n {len(r)}/{len(c)})")
        yaw = [float(p["yaw_a"]) - float(p["yaw_b"]) for p in reals if "yaw_a" in p]
        yaw_c = [float(p["yaw_a"]) - float(p["yaw_b"]) for p in controls if "yaw_a" in p]
        if len(yaw) >= 3 and len(yaw_c) >= 5:
            m1, m2 = statistics.mean(yaw), statistics.mean(yaw_c)
            se = math.sqrt(statistics.pvariance(yaw) / len(yaw) + statistics.pvariance(yaw_c) / len(yaw_c))
            lines.append(f"      {'head turning (deg/s, after - before)':38} {m1:+5.0f} vs {m2:+5.0f}  diff {m1 - m2:+.0f} [{m1 - m2 - 1.96 * se:+.0f}, {m1 - m2 + 1.96 * se:+.0f}]")
        print(f"  {name}: {len(reals)} probes")
        for line in lines:
            print(line)


def summarise_profiles(all_events):
    rows = [(r["moon"], kv(r["detail"])) for r in all_events if r["event"] == "DAY_PROFILE"]
    if not rows:
        return
    print(f"\n=== player profiles ({len(rows)} player-days; medians)")
    keys = ["alive", "inside", "outside", "ship", "alone_inside", "alone_outside", "speed", "still", "sprint", "turn_inside",
            "turn_outside", "scans_min", "use_min", "walkie_min", "walkie_carried", "light_inside", "trips", "trip_median", "left_at"]
    out = []
    for key in keys:
        vals = [float(d[key].rstrip("s")) for _, d in rows if key in d]
        if vals:
            out.append(f"{key} {statistics.median(vals):g}")
    print("  " + ", ".join(out))


ARMED = {  # opportunity kind -> (symptom, lowest tier index, wait in minutes)
    "door": ("Locked door", 2, 6.0),
    "item": ("Vanishing item", 2, 5.0),
    "light": ("Flicker (armed)", 1, 2.0),
    "exit": ("Lost time", 1, 999.0),
}


def summarise_opportunities(all_events, all_samples):
    rows = [(r["_file"], r["day"], r["detail"]) for r in all_events if r["event"] == "DAY_OPPORTUNITIES"]
    if not rows:
        return
    minutes_by_tier = defaultdict(lambda: [0.0] * 4)
    for r in all_samples:
        minutes_by_tier[(r["_file"], r["day"])][TIERS.index(r["tier"]) if r["tier"] in TIERS else 0] += 1 / 60
    totals = {k: [0] * 4 for k in ARMED}
    minutes = [0.0] * 4
    doors = Counter()
    for f, d, detail in rows:
        m = re.search(r"by tier C/U/D/B: door (\S+) item (\S+) light (\S+) exit (\S+)", detail)
        if not m:
            continue
        for kind, field in zip(("door", "item", "light", "exit"), m.groups()):
            for i, n in enumerate(field.split("/")):
                totals[kind][i] += int(n)
        for i in range(4):
            minutes[i] += minutes_by_tier[(f, d)][i]
        dm = re.search(r"doors(.*?)\|", detail)
        if dm:
            for part in dm.group(1).split():
                k, _, v = part.partition("=")
                doors[k] += int(v)
    print(f"\n=== opportunities: how often each armed symptom's moment came up (armed or not), {len(rows)} player-days")
    if doors:
        print("  door hovers:", dict(doors))
    for kind, (name, low, wait) in ARMED.items():
        n = sum(totals[kind][low:])
        mins = sum(minutes[low:])
        if mins <= 0:
            continue
        rate = n / mins
        p = 1 - math.exp(-rate * wait) if wait < 900 else 1 - math.exp(-rate * 30)
        print(f"  {name:18} {n:4} moments in {mins:5.1f} min at its tiers = {rate:.2f}/min"
              f" -> projected chance it fires before its {'wait' if wait < 900 else 'day'} ends once armed: {100 * p:.0f}%")


def summarise_stats(paths, per_moon, keep_debug):
    """The anonymous stats export: pooled numbers over every player-day."""
    packets, notes = load_stats(paths, keep_debug)
    print(f"=== anonymous stats: {notes}")
    if not packets:
        return

    def count(key):
        return Counter(str(get(p, *key.split("."), default="unknown")) for p in packets)

    alive = sum(get(p, "alive_s") for p in packets)
    print(f"alive on moons: {alive / 3600:.1f} h | versions {dict(count('mod').most_common(5))}")
    print(f"players at start {dict(sorted(count('players').items()))} | role {dict(count('role'))} | ended {dict(count('ended'))}")
    print(f"day length {dict(Counter(round(get(p, 'day_length', default=-1), 1) for p in packets).most_common(6))}")

    def tier_split(group):
        total = Counter()
        for p in group:
            for tier in ("calm", "uneasy", "disturbed", "breaking"):
                total[tier] += get(p, "tiers_s", tier)
        whole = sum(total.values()) or 1
        return "/".join(f"{100 * total[t] / whole:.0f}" for t in ("calm", "uneasy", "disturbed", "breaking"))

    print(f"tier split (calm/uneasy/disturbed/breaking %): all {tier_split(packets)}")
    by_players = defaultdict(list)
    for p in packets:
        by_players[get(p, "players")].append(p)
    for n in sorted(by_players):
        print(f"  {n} players ({len(by_players[n])} days): {tier_split(by_players[n])}")

    moons = Counter(get(p, "moon", "name", default="unknown") for p in packets)
    print("moons (days):", dict(moons.most_common(12)))
    if per_moon:
        for moon, _ in moons.most_common(12):
            print(f"  {moon}: {tier_split([p for p in packets if get(p, 'moon', 'name', default='unknown') == moon])}")
    modded = Counter(f"{get(p, kind, 'name', default='?')} [{get(p, kind, 'source', default='?')}]"
                     for p in packets for kind in ("moon", "interior") if not get(p, kind, "vanilla", default=True))
    if modded:
        print("modded moons/interiors (days):", dict(modded.most_common(10)))

    hours = alive / 3600 or 1
    print("symptoms (played per alive hour | totals: played armed held dropped failed | played by tier):")
    totals = defaultdict(Counter)
    for p in packets:
        for key, n in get(p, "symptoms", default={}).items():
            totals[key].update(n)
    for key, n in sorted(totals.items(), key=lambda kv: -kv[1]["played"]):
        print(f"  {key:15} {n['played'] / hours:5.1f}/h | {n['played']} {n['armed']} {n['held']} {n['dropped']} {n['failed']}"
              f" | U {n['played_uneasy']} D {n['played_disturbed']} B {n['played_breaking']}")

    print("reactions (share of unconfounded probes; control = random quiet moments):")
    react = defaultdict(Counter)
    for p in packets:
        for key, n in get(p, "reactions", default={}).items():
            react[key].update(n)

    def share(n, field, base="probes"):
        return f"{100 * n[field] / n[base]:.0f}%" if n[base] else "-"
    for key, n in sorted(react.items(), key=lambda kv: -kv[1]["probes"]):
        print(f"  {key:15} n={n['probes']:4} turned {share(n, 'turned', 'directional'):>4} stopped {share(n, 'stopped'):>4}"
              f" sprinted {share(n, 'sprinted'):>4} scanned {share(n, 'scanned'):>4}")
    if react.get("control"):
        print("  (compare each line with the control line: the difference is the symptom's effect)")

    sounds = [s for p in packets for s in get(p, "sounds", default=[])]
    if sounds:
        def turned_rate(group):
            judged = [s for s in group if s.get("turned", -1) >= 0]
            return f"{100 * sum(s['turned'] for s in judged) / len(judged):.0f}% of {len(judged)}" if judged else "-"
        for field in ("monster", "ground", "place"):
            groups = defaultdict(list)
            for s in sounds:
                groups[s.get(field, "unknown")].append(s)
            print(f"phantom sounds by {field} (count, turned toward):",
                  ", ".join(f"{k} {len(v)} ({turned_rate(v)})" for k, v in sorted(groups.items(), key=lambda kv: -len(kv[1]))[:12]))

    fired = sum(1 for p in packets if get(p, "lost_time", "fired", default=False))
    trips = sorted(get(p, "trips", "median_s") for p in packets if get(p, "trips", "count"))
    print(f"lost time fired on {fired} of {len(packets)} days | median trip inside {trips[len(trips) // 2] if trips else 0} s")
    seen = Counter()
    for p in packets:
        for m in get(p, "monsters", default=[]):
            seen[f"{m.get('name')} [{m.get('source')}]"] += m.get("count", 0)
    print("monsters on the moon (total spawned):", dict(seen.most_common(15)))
    errors = Counter()
    for p in packets:
        errors.update(get(p, "errors", default={}))
    fps = sorted(get(p, "fps", "avg") for p in packets)
    print("errors:", dict(errors) or "none", "| median fps", fps[len(fps) // 2])


def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument("files", nargs="+")
    parser.add_argument("--days", action="store_true", help="local logs: per-day table for each file")
    parser.add_argument("--per-moon", action="store_true", help="stats export: tier split per moon")
    parser.add_argument("--keep-debug", action="store_true", help="stats export: keep days played with debug tools")
    args = parser.parse_args()
    paths = sorted({p for pattern in args.files for p in glob.glob(pattern)})
    if not paths:
        raise SystemExit("No files matched.")
    stats_paths = [p for p in paths if is_stats_file(p)]
    if stats_paths:
        summarise_stats(stats_paths, args.per_moon, args.keep_debug)
        paths = [p for p in paths if p not in stats_paths]
        if not paths:
            return
    all_events, all_samples = [], []
    for path in paths:
        summarise(path, args.days)
        for r in load(path):
            r["_file"] = path
            if r["kind"] == "E":
                all_events.append(r)
            elif r["kind"] == "S" and r.get("dead") == "0":
                all_samples.append(r)
    # Across all files (reactions need pooled numbers to say anything).
    summarise_reactions(all_events)
    summarise_profiles(all_events)
    summarise_opportunities(all_events, all_samples)


if __name__ == "__main__":
    main()
