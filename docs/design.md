# Sanity: how it works

> **Note:** like the code, this document was written with AI help and hasn't been reviewed by an experienced
> programmer. It's checked against the code, but small errors are possible; the code is the final word.

This describes what the mod does and why, as it is now. The code has the same descriptions at the top of each file;
the comment at the top of `src/Sanity/Plugin.cs` is a map of where everything lives.

## What it is

Each player has a hidden sanity value. It drops when you're alone, deep in the facility, out at night or scared, and
comes back in the ship, with teammates and with loot. Low sanity brings symptoms: small effects, built from the game's
own sounds and visuals, that make you doubt what you saw or heard. Only you experience them, so your crewmates can't
confirm or deny anything.

## Goals

- Make people distrust what they see and hear, and stay unsure what was real.
- Never go stale: rare, irregular, believable. Not frequent jumpscares.
- Work for any group size, and for solo play.

## Rules the mod follows

- **Only on your screen.** The mod sends no messages of its own. Two things reach other players, both through the
  game's own code: a key used on the fake locked door is really used up, and the phantom hit's flinch holds back the
  game's own animation updates to others for 1.2 s (so they don't see it). Everything else is local: the flicker (the
  game's `RoundManager.FlickerLights`), fear (the game's local fear value), every sound, the walkie, lost time's clock
  and sky, the vanished item (hidden only for you; teammates can still see and take it) and the HUD. Loot recovery
  only reads the host's scrap-value message. So only the player who wants the mod needs it; the host and the others
  don't, and mismatched versions are fine.
- **No custom assets.** Every sound and effect is the game's own.
- **Never break the game.** If something expected is missing, that effect is skipped quietly. Every hook into the
  game's code catches its own errors and reports the first one once (`Safety.cs`).
- **Vanilla behaviour is left alone.** The mod reads the game's values; the one it writes is your local fear value,
  through the game's own `JumpToFearLevel`.
- **Nothing that hurts or kills.** Real health is never touched.
- **Config with sensible defaults.** Groups tune it with multipliers, not modes. A master switch (`EnableMod`): off
  means sanity is held at 100 and nothing happens. One on/off switch per symptom: off means it's never picked
  naturally (the debug menu can still fire it).

## How symptoms should feel

- Plausible: only fake what could really be there, and where it could be.
- Half-truths beat inventions.
- Lower sanity means more convincing effects, not just more of them. Each symptom states its strength per tier
  (Uneasy = missable, Disturbed = noticeable, Breaking = unmistakable), or is constant on purpose.
- Every symptom has a vanilla twin: indistinguishable from something the game really does, ideally the same code.
- Fakes cluster where real danger is (alone, deep inside), so "it's probably fake" is never safe.
- Random timing with soft limits: lower odds right after a symptom, never hard bans, so piles stay possible.
- Related symptoms together read as intentional; a lone oddity reads as a bug.
- No tells: the meter stays hidden.

## The sanity meter (`SanityMeter.cs`)

Sanity runs from 0 to 100. It sits at 100 whenever you're not on a moon. Each frame, rate = gains - drains.

**Tiers:** Calm (75+), Uneasy (52-75), Disturbed (25-52), Breaking (below 25). You drop a tier the moment you cross a
line, but only climb back once 3 above it, so the tier doesn't flicker at the edge. The lines are set in one place
(`SanityMeter.TierBoundaries`).

**Drains**
- Inside the facility: 0.36/s, growing to x1.5 over 4 minutes inside ("the spiral"), capped at 0.495/s. After you
  leave, the build-up fades over 15 s, so a quick step outside doesn't reset it.
- Outside (not in the facility or the ship): 0.1/s at any time of day.
- Night (outside, or in the ship with the doors open): from 0 at the start of Sundown to 0.4/s at Midnight. Adds to
  the outside drain.
- Company (a living teammate within 10 m, kept until 14 m, not more than 4 m above or below you) halves the inside,
  outside and night drains.
- Fear (the game's own fear level, 0 to 1): extra drain from 0 at fear 0.1 to 0.5/s at fear 1.0. Company doesn't
  soften it.
- Lobby size: with fewer players you're alone more often, so the inside, outside and night drains are scaled by the
  number of players at the start of the day: 1 player x0.90, 2 x1.00, 3 x1.10, 4 x1.19, 5 or more x1.20. The rule
  behind these numbers is in `SanityMeter.cs` (built so every lobby size drains about the same on average).
- Weather (`Weather.cs`): each weather has an outside multiplier, an inside multiplier and a "night head start" (how
  far into the night it already counts as from landing). For example Eclipsed: outside x1.4, inside x1.2, head start
  0.3. Wesley's Weathers (Minefield, Hurricane, Earthquakes, Hallowed, Forsaken) have their own numbers; other unknown
  weathers count as clear. Names come from WeatherRegistry when it's installed. The config line `Weather/Overrides`
  can change any of them.

**Gains**
- The ship: 0.35/s with the doors open, 0.5/s closed in the daytime, sliding to 0.1 / 0.3 by Midnight; x1.1 with
  company. It builds up over the first 30 s aboard (a quick drop-off barely counts), is up to twice as strong near 0,
  and never lifts you above 85.
- Reliefs (`SanityReliefs.cs`), ways back up outside the ship, so the meter swings instead of only sliding down:
  - Regroup: meeting a teammate after being alone long enough (60 s with 3 players; longer with fewer, shorter with
    more): +16, up to +32. Below Calm only.
  - Breather: stepping out of the facility in daylight after at least 2 minutes inside: +10.
  - Find: picking up new scrap: +2, at most once every 30 s.
  - Bounce: 75 s in Breaking (outside the ship) and the crisis passes: back to at least 58, the drains run at half for
    45 s, and the director holds off for 45 s.
- Loot (`LootRecovery.cs`): bringing fresh loot to the ship restores sanity, sized by how much of the day's loot it
  is for one player (up to 15 per item). The finder gets 65%, everyone else who carried it shares 35%. Paid when the
  item is let go inside the ship. After 30 sanity from loot in a day, loot pays half. Not limited by the ship's 85 cap.
  The day's loot comes from the game's own scrap-value message, so every player gets the same numbers.

**Instant drops**
- Damage: 0.2 per health point lost, at most 10 per hit. Falls count half.
- A teammate dies: -10 within 5 m of you, -5 if you're in the ship, -2 anywhere else.
- Fear jumps to 0.4 or higher: -5, at most once every 8 s.

**Other**
- Dead: sanity freezes. Brought back mid-round (for example by Lategame Upgrades' interns): a fresh start at 85.
- Config dials: `DrainMultiplier` (also scales instant drops) and `RecoveryMultiplier`.
- Day length (`DayLength.cs`): how long today really lasts compared with a normal day, measured from how fast the
  clock moves. Used for telemetry and lost time's timing; drains don't depend on it.
- The tuning aims at about 35% of the time Calm, 28% Uneasy, 22% Disturbed and 15% Breaking, and Breaking spells of
  one to two minutes. The numbers were fitted to playtest data with `tools/replay.py`.

## The director (`SanityDirector.cs`, `DirectorLayers.cs`)

The director decides **when** a symptom happens and **which** one.

**When:** every frame there's a small chance. Per second it's 0.044 x depth x dip x tension x wave x mood:
- depth: how far below Calm you are (0.4 just below the line, 1 at 0).
- dip: low right after a symptom, back to about 95% after 24 s (a soft cooldown).
- tension: grows the longer it's been quiet (doubles after 2 quiet minutes), so there are no long droughts.
- waves: the chance runs at x1.6 for about 2 minutes, then x0.4 for about 1.5 minutes, switching at random, which
  makes the spacing less regular.
- mood: each player's day gets a mood: steady, whispers (sounds more likely), tricks (sights), nerves (body), quiet
  (fewer) or restless (more).

No symptoms at Calm, while dead, or off a moon. Each day starts fresh.

**Which:** a lottery by weight among the symptoms that make sense right now (their tier allows it and their own
conditions pass). The winner is accepted with a chance that's low if it played recently and recovers over its
"recovery time", so recency is a soft limit. If it's turned down or can't play after all, the next is drawn in the
same roll. If nothing plays, tension keeps building.

**Armed symptoms** need a moment the director can't create (a door you look at, an item you walk up to, leaving the
facility). Winning the lottery arms them, and they fire on their own when the moment comes. At most 2 wait at once.
They fire only at or below their tier, never within 5 s of another symptom you could notice, and are dropped if you
die, the day ends, or nothing fitting comes up in time.

**Chains:** some symptoms can bring a likely follow-up a moment later, so two things line up into "that was real":
a rush of footsteps that reaches you can hit you, a teammate's "death" on the radio can bring dread, a sound coming
closer can kill the lights, and lost time can be followed by a walkie click. The locked door and the vanishing item
deliberately have no follow-ups: they should leave you thinking "it was just locked" or "I misremembered".

**Peaks and relaxing:** after a big moment (dread, a rush reaching you, the death static, Breaking hits, the
Presence's finale) the crisis passes sooner and the director rests for 50 s.

**Echoes:** a real scare near a monster can come back 1-5 minutes later as that monster's phantom sound.

## Symptoms (`Symptoms/`)

| Symptom | From tier | Weight | Recovery |
|---|---|---|---|
| Flicker (armed) | Uneasy | 1 | 60 s |
| Phantom sound | Uneasy | 0.6 | 90 s |
| Phantom steps | Uneasy | 0.9 | 120 s |
| Phantom walkie | Uneasy | 0.8 | 150 s |
| The Presence | Uneasy | 0.9 | 240 s |
| Lost time (armed) | Uneasy | 3 | 600 s |
| Phantom fear | Disturbed | 0.8 | 120 s |
| Phantom hit | Disturbed | 0.8 | 180 s |
| Locked door (armed) | Disturbed | 1.6 | 300 s |
| Vanishing item (armed) | Disturbed | 1.6 | 240 s |
| Phantom dread | Breaking | 0.5 | 300 s |

Phantoms placed in the world (sounds, steps) come from behind or beside you, on walkable floor at about your height.
Phantom sounds and steps share one slot: only one plays at a time. Every phantom falls off as if it were 1.8x closer
than it is (times the `PhantomLoudness` config), because at real loudness they were too easy to miss; direction and
wall muffling stay true.

- **Flicker:** the game's own light flicker (the Ghost Girl's function), on your screen only. It waits for a moment
  you'd notice: you look at a powered light within 12 m, or you're standing in one's light. Disturbed adds flashlights
  stuttering; Breaking makes them cut out briefly.
- **Phantom fear:** the game's own fear effect (music, screen filter, heartbeat) with nothing there: a fixed spike to
  0.6, the value the game uses for spotting a Bracken. Only in a quiet moment (no real fear for 150 s).
- **Phantom dread:** the bigger, rarer version at Breaking: a spike to 0.9, the game's value for a Coilhead or a dead
  body.
- **Phantom sound:** a real monster's sound with nothing there, only from monsters that fit this moon and where you
  are, more likely ones that are really alive, and from a different direction than a real one nearby. Distance by
  tier: Uneasy 10-16 m, Disturbed 6-11 m, Breaking 3.5-7 m (rhythmic sounds approach you at Breaking). A night monster
  that isn't out yet is heard only about a third of the time before late afternoon. Not while you're sprinting.
- **Phantom steps:** the game's own footsteps with nobody there. Uneasy: someone walking past 8-13 m away. Disturbed:
  steps following you 6-9 m behind, a beat after each of yours. Breaking: a sprint from 10-12 m that closes in to
  1.5-2.5 m behind you. Looking toward them ends it. Never with a teammate within 15 m or while you sprint.
- **Phantom walkie:** your walkie clicks on as if a teammate pressed theirs. Uneasy: silence. Disturbed: something
  faint (their footsteps, or a soft monster sound near them). Breaking: a monster sound up close, cut off, or now and
  then the static of a teammate dying. Needs another player with a walkie who isn't in plain view.
- **Phantom hit:** an attack sound behind you, then 1-3 hits (2-4 at Breaking) with the game's own hit effects: HUD,
  damage sound, visor cracks, camera shake, flinch. The lost health shows on your HUD (and HUD mods), holds for 10 s,
  then creeps back. Real health is never touched, and it never shows below 26.
- **Locked door:** the door you reach is locked, though it isn't really. The game and mods treat it exactly like a
  locked door (text, icon, Locksmith's lockpicking). Use a key on it and the key is gone. Look away and it's an
  ordinary door again. Never when a monster is near or fear is high.
- **Vanishing item:** the item you're walking up to isn't there when you reach it: not drawn, no sound, nothing to grab
  or scan. Step back or look away and it's back.
- **Lost time:** you step out of the facility after a long trip inside and the clock says it's 3-6 hours later
  (evening, or nearly midnight), with the sky to match. A few seconds later both slide back to the real time. At most
  once a day, on about half the days.
- **The Presence:** something unseen that stays around for 30-70 s and moves like a creature, along real paths at your
  pace: its footsteps, its voice (a monster that fits the moon, or "a person"), the lights flickering as it passes.
  Uneasy: it circles 8-12 m away. Disturbed: it follows 5-8 m behind. Breaking: it closes in and ends with a few things
  in a row. Turn toward it and it goes quiet, then speaks. It's never seen, and a teammate coming close scares it off.

## Testing tools (all off by default)

- `ShowSanityReadout`: sanity, tier, rate, fear and the director's chance on screen.
- `EnableDebugKeys`: F8 fires a random symptom that makes sense right now; F7 opens a debug menu with four tabs: Now
  (what the meter and director are doing, armed symptoms and what holds them), Symptoms (fire or arm any symptom),
  Log (the last 80 events) and Tools (set sanity, freeze, ignore conditions, always chain, loudness).
- `ShowPresence`: a red ball where the Presence is.
- `Diagnostics.cs` records when the game's interact prompt goes missing, with what the mod was doing at the time.

## Telemetry (off by default)

With `EnableTelemetry` on, one CSV per game session goes to `BepInEx/Sanity_Telemetry/`. Nothing is sent anywhere.
- The first row (SESSION) records the mod version, the config and every tuning number.
- S rows, once a second on a moon: sanity, rate and its parts, where you are, company, fear, health, position and
  view, and more.
- E rows, the moment something happens: entering and leaving places, damage, deaths, tier changes, every symptom and
  how it ended, every roll of the director, loot, reliefs, and a summary at the end of each day.
- REACTION rows measure whether a symptom changed what you did (turning, stopping, sprinting, scanning, answering the
  walkie), compared with random moments without a symptom.

`tools/analyze.py` summarises the files; `tools/replay.py` re-runs them through the sanity formula to test tuning
changes before playing (its numbers must match the C# code).

## Known issues and limitations

| Issue | Status |
|---|---|
| Phantom sounds and steps don't play while you sprint (your own steps would drown them out), and players sprint a lot, so many chances are lost. | Confirmed. Planned fix: wait up to a few seconds for you to slow down instead of skipping. |
| Lost time is rare and subtle: the clock and the sky change, but it's easy to miss if you don't watch either. | Confirmed. It should come a bit more often; how to make it more noticeable (with the game's own effects only) is open. |
| Phantom sounds, the phantom hit's attack sound and the Presence's masks only imitate vanilla monsters. On moons with mostly modded monsters there are fewer sounds, silent hits and more "a person" Presences. | Planned: modded monster support (reading any monster's own sounds). |
| Vanishing item with several items in view: only one goes, which can read as a glitch, and in a cluster it can be the item next to the one you meant. | Open. Options: only lone items; the whole pile goes at Breaking; always the item your grab would pick. |
| Some items (seen with flasks) act oddly when they vanish. | Being investigated (telemetry records which parts were hidden). |
| The Presence may come too often for how little it does, and "a person" says nothing when you turn to it. | Open. Ideas: lower its weight; a few quick steps moving off when looked at. |
| A lockpicker item does nothing on the fake locked door. | Known gap. |
| Interiors that don't tag their lights the way the game expects can't flicker. | Limitation (the flicker is the game's own). |
| The game's interact prompt was seen disappearing once during testing. | Not caused by Sanity as far as known; the mod records it when it happens. |
| The locked door can be opened right after it showed "Locked". | By design: like the vanishing item, it should leave you thinking "it was just locked". |
| Phantom hits can happen in the ship. | By design: rare enough. |
| The meter and its tuning come from a handful of sessions with one group and one modpack. | Expect balance changes. |

## Ideas and plans

Spitballed during development. Not built and not promised; some may never happen.

| Idea | Area | Notes |
|---|---|---|
| Hold sounds and steps while you sprint | Symptoms | See known issues. |
| Modded monster support | Symptoms | Read any monster's sounds by name (footsteps, idle, attack); a config list to exclude some. |
| Lost time more often, and more noticeable | Symptoms | |
| Lying clock variants | Symptoms | Uneasy: the clock skips ahead for a moment. Disturbed: it slips or freezes for a while. Breaking: it says it's far too late, or runs backwards. |
| Vanishing item options for piles | Symptoms | See known issues. |
| Rare loud scare | Symptoms | A big, rare one, for example the Jester (kept out of phantom sounds for now). |
| Aggressive monster sounds at low sanity | Symptoms | For example an angry Thumper in the distance, maybe coming closer. |
| A single near-lethal hit with dread | Symptoms | |
| A real jumpscare when the fake door takes your key | Symptoms | |
| Fake ship warning text | Symptoms | Breaking. |
| Ghost blips on the ship's radar | Symptoms | |
| Held items acting up | Symptoms | |
| A lying scanner or a haunted terminal | Symptoms | Scanner mods replace scanning, so careful. |
| Breaking distortion | Symptoms | A screen effect at the lowest tier. |
| A glimpse of a teammate who isn't there, a fake body | Symptoms | Needs player models. |
| Apparatus-pulled sequence | Symptoms | Something special when the apparatus is taken. |
| Flicker improvements | Symptoms | One light or one direction instead of the whole facility; ship and exterior lights; pocketed flashlights. |
| Faster fade after phantom fear | Symptoms | If episodes feel too long. |
| Limits on repeats | Symptoms | Long sounds and the same monster several times in a row. |
| The Presence doing more | Symptoms | It could set up the locked door or vanishing item in a way that reads as one coherent thing. |
| Micro-texture | Director | Very rare, tiny effects at Uneasy (one far step, one light blinking once) outside the normal count. |
| Smarter picks | Director | Choose the symptom that makes the most sense, not only by chance. |
| Frequency dial and daily budget | Director | One overall dial that keeps the curve's shape; a soft budget per day. |
| Symptoms that cost sanity | Meter | |
| More inputs to the meter | Meter | Darkness inside, line of sight for company, more teammates together, moon difficulty. |
| A hard mode | Meter | Slower fall but harder recovery, to compare with the current one. |
| Fairer loot sharing | Meter | Split by how much of the trip each hauler carried it. |
| Effects that are actually dangerous | Gameplay | A later phase; everything is harmless for now. |
| A recap after each round | Gameplay | What you experienced, revealed afterwards. |
| Sync settings with the host | Multiplayer | So a group plays with the same tuning. |
| Optional telemetry sharing | Tooling | Opt-in only, clearly explained. |
| Automatic builds and releases | Tooling | A GitHub Action that builds and uploads a release. |
