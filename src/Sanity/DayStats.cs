using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using GameNetcodeStuff;
using Sanity.Symptoms;
using UnityEngine;

namespace Sanity;

// The anonymous stats: one small packet per player per in-game day, built from the same events the local log records
// (fed by Telemetry.Event) plus a few things kept here: the time budget, trips inside, the monsters on the moon, each
// phantom sound, and the day's context (moon, interior, weather, mood...), re-read once a second while on the moon.
// The day ends when the ship is back in orbit, the game is closed ("quit"), or the round is gone ("disconnected").
// Only numbers and content names: no player names, no IDs that last beyond one game launch, no positions or times of
// day. The packet is sent (StatsUploader) only while sharing is on; every field is listed in the README.
// Format: JSON, "schema" = SchemaVersion (raise it whenever a field changes meaning). Unknown values are "unknown" or
// -1, never an error: anything that fails is skipped and the rest of the packet still goes.
internal static class DayStats
{
    internal const int SchemaVersion = 1;
    private const int MaxSounds = 30;
    private const int MaxMonsters = 30;
    private const float FearLine = 0.1f;          // the game's own "tension" line
    private const float EveningFraction = 0.63f;  // the game's Sundown

    // A new random number every game launch: tells one launch's days apart (and retries apart), nothing more.
    internal static readonly string SessionId = Guid.NewGuid().ToString("N").Substring(0, 12);

    private static readonly Regex Number = new Regex(@"-?\d+(\.\d+)?", RegexOptions.Compiled);

    private static int day;
    private static bool active;
    private static bool warned;

    // Time budget and meter (alive on the moon).
    private static float alive, insideAlone, insideCompany, outsideAlone, outsideCompany, ship, fear, evening, sprint, dead;
    private static readonly float[] TierSeconds = new float[4];
    private static float minSanity;
    private static int tierChanges, bounces, echoes, chainsPlayed, realFearJumps, hits, debugEvents, forcedPlays;
    private static float damageTaken, lootSanity;
    private static int delivered;
    private static string causeOfDeath = "";
    private static readonly Dictionary<string, int> Reliefs = new Dictionary<string, int>();
    private static readonly Dictionary<string, int> Errors = new Dictionary<string, int>();

    // Per symptom (by config key): what happened to it today.
    private static readonly Dictionary<string, int[]> Counts = new Dictionary<string, int[]>();
    private const int Drawn = 0, Played = 1, Armed = 2, Held = 3, Dropped = 4, Failed = 5, Rejected = 6, PlayedU = 7, PlayedD = 8, PlayedB = 9, Size = 10;

    // Reactions (unconfounded probes only): per symptom key and "control".
    private static readonly Dictionary<string, int[]> ReactionCounts = new Dictionary<string, int[]>();
    private const int Probes = 0, Directional = 1, Turned = 2, Stopped = 3, Sprinted = 4, Scanned = 5, RSize = 6;

    // Each phantom sound: which monster, where, on what ground, and (once its own reaction probe closes) the reaction.
    private sealed class Sound
    {
        internal string Monster = ContentInfo.Unknown;
        internal string Source = ContentInfo.Unknown;
        internal string Place = ContentInfo.Unknown;
        internal string Ground = ContentInfo.Unknown;
        internal int Turned = -1;
        internal int Stopped = -1;
    }
    private static readonly List<Sound> Sounds = new List<Sound>();

    // Each phantom hit: what it imitated, and the health it showed as lost.
    private static readonly List<(string As, int Damage)> Hits = new List<(string, int)>();
    private static float powerOffAt = -1f;   // seconds alive on the moon when the apparatus was pulled (-1: it wasn't)
    private static Sound? justPlayed;   // the newest sound, until Reactions opens its probe and takes it

    // Monsters seen on the moon today: name -> (count, source).
    private static readonly Dictionary<string, (int Count, string Source)> Monsters = new Dictionary<string, (int, string)>();
    private static readonly HashSet<int> MonstersCounted = new HashSet<int>();
    private static float monsterTimer;

    // Trips inside.
    private static readonly List<float> Trips = new List<float>();
    private static bool wasInside;
    private static float tripStart;

    // Lost time.
    private static bool lostTimeFired;
    private static float lostTimeMinutes = -1f, lostTimeTrip = -1f;

    // From the day's eligibility summary: per symptom key, seconds it could have played, and its main blocker.
    private static readonly Dictionary<string, (int Seconds, string Blocker)> Eligibility = new Dictionary<string, (int, string)>();

    // The day's context, re-read once a second while on the moon. The packet is built from this copy, not read at the
    // end: by then the weather, day length and mood are already reset (back in orbit), the dead are revived, and after
    // a disconnect the round is gone altogether.
    private static string moonName = ContentInfo.Unknown, moonSource = ContentInfo.Unknown;
    private static string interiorName = ContentInfo.Unknown, interiorSource = ContentInfo.Unknown;
    private static string weatherName = ContentInfo.Unknown, moodName = ContentInfo.Unknown;
    private static string role = ContentInfo.Unknown, gameVersion = ContentInfo.Unknown;
    private static float dayLength = -1f;
    private static bool earlyVote;
    private static int teammatesAlive = -1, revives;

    // ---- Fed by Telemetry ----

    internal static void StartDay()
    {
        Guard(() =>
        {
            active = true;
            day++;
            alive = insideAlone = insideCompany = outsideAlone = outsideCompany = ship = fear = evening = sprint = dead = 0f;
            Array.Clear(TierSeconds, 0, TierSeconds.Length);
            minSanity = 100f;
            tierChanges = bounces = echoes = chainsPlayed = realFearJumps = hits = debugEvents = forcedPlays = 0;
            damageTaken = lootSanity = 0f;
            delivered = 0;
            causeOfDeath = "";
            Reliefs.Clear();
            Errors.Clear();
            Counts.Clear();
            ReactionCounts.Clear();
            Sounds.Clear();
            justPlayed = null;
            Hits.Clear();
            powerOffAt = -1f;
            Monsters.Clear();
            MonstersCounted.Clear();
            monsterTimer = 0f;
            Trips.Clear();
            wasInside = false;
            lostTimeFired = false;
            lostTimeMinutes = lostTimeTrip = -1f;
            Eligibility.Clear();
            moonName = moonSource = interiorName = interiorSource = weatherName = moodName = role = gameVersion = ContentInfo.Unknown;
            dayLength = -1f;
            earlyVote = false;
            teammatesAlive = -1;
            revives = 0;
            ReadContext();
        });
    }

    // Recording was switched off mid-day: forget the day, so a part of it is never sent as if it were a whole day.
    internal static void Discard()
    {
        active = false;
    }

    // Every frame on the moon, alive or dead.
    internal static void Tick(PlayerControllerB player, float dt)
    {
        if (!active)
        {
            return;
        }
        Guard(() =>
        {
            // Once a second, alive or dead: the monsters on the moon and the day's context.
            monsterTimer -= dt;
            if (monsterTimer <= 0f)
            {
                monsterTimer = 1f;
                CountMonsters();
                ReadContext();
            }

            if (player.isPlayerDead)
            {
                dead += dt;
                return;
            }

            alive += dt;
            TierSeconds[(int)SanityMeter.Tier] += dt;
            minSanity = Mathf.Min(minSanity, SanityMeter.Value);
            bool company = SanityMeter.NearTeammate;
            if (SanityMeter.InShip)
            {
                ship += dt;
            }
            else if (SanityMeter.Inside)
            {
                if (company) insideCompany += dt; else insideAlone += dt;
            }
            else
            {
                if (company) outsideCompany += dt; else outsideAlone += dt;
            }
            if (SanityMeter.Fear >= FearLine)
            {
                fear += dt;
            }
            if (SanityMeter.DayFraction >= EveningFraction)
            {
                evening += dt;
            }
            if (player.isSprinting)
            {
                sprint += dt;
            }

            bool inside = player.isInsideFactory;
            if (inside && !wasInside)
            {
                tripStart = alive;
            }
            else if (!inside && wasInside)
            {
                Trips.Add(alive - tripStart);
            }
            wasInside = inside;
        });
    }

    internal static void OnEvent(string name, float value, string detail)
    {
        if (!active)
        {
            return;
        }
        Guard(() =>
        {
            bool forced = detail.Contains("(F8)") || detail.Contains("(debug menu)");
            switch (name)
            {
                case "TIER": tierChanges++; break;
                case "BOUNCE": bounces++; break;
                case "ECHO": echoes++; break;
                case "FEAR_JUMP": realFearJumps++; break;
                case "DAMAGE": hits++; damageTaken += Mathf.Abs(value); break;
                case "DIED": causeOfDeath = ContentInfo.Clean(detail); break;
                case "REVIVED": revives++; break;
                case "APPARATUS_PULLED": if (powerOffAt < 0f) powerOffAt = alive; break;
                case "DEPOSIT": delivered++; break;
                case "LOOT_RECOVERY": lootSanity += Mathf.Max(0f, value); break;
                case "DEBUG": debugEvents++; break;
                case "SANITY_ERROR": Add(Errors, ContentInfo.Clean(detail.Split(':')[0])); break;
                case "RELIEF": Add(Reliefs, ContentInfo.Clean(detail.Split(' ')[0])); break;
                case "SYMPTOM":
                    if (forced) { forcedPlays++; break; }
                    if (detail.Contains("(chain from ")) chainsPlayed++;
                    CountSymptom(detail, Played);
                    CountSymptom(detail, value >= 3f ? PlayedB : value >= 2f ? PlayedD : PlayedU);
                    break;
                case "SYMPTOM_ARMED":
                    if (forced) { forcedPlays++; break; }
                    CountSymptom(detail, detail.Contains("stop sprinting") ? Held : Armed);
                    break;
                case "SYMPTOM_DROPPED": CountSymptom(detail, Dropped); break;
                case "SYMPTOM_FAILED": if (!forced) CountSymptom(detail, Failed); break;
                case "SYMPTOM_REJECTED": if (!forced) CountSymptom(detail, Rejected); break;
                case "ROLL": CountDraws(detail); break;
                case "REACTION": CountReaction(detail); break;
                case "LOST_TIME": NoteLostTime(detail); break;
                case "DAY_ELIGIBILITY": ReadEligibility(detail); break;
            }
        });
    }

    // Called by phantom sound when one plays: the monster, inside or outside, and the ground you're on. Forced plays
    // (F8, the debug menu) aren't normal play and are left out.
    internal static void NoteSound(EnemyAI? example, string monster, PlayerControllerB player)
    {
        justPlayed = null;
        if (!active || Sounds.Count >= MaxSounds || SanityDirector.ForcedContext)
        {
            return;
        }
        Guard(() =>
        {
            Sound sound = new Sound
            {
                Monster = ContentInfo.Clean(monster),
                Source = ContentInfo.MonsterSource(example),
                Place = player.isInsideFactory ? "inside" : player.isInHangarShipRoom ? "ship" : "outside",
                Ground = GroundName(player),
            };
            Sounds.Add(sound);
            justPlayed = sound;
        });
    }

    // Called by phantom hit when one starts. Forced plays are left out, like sounds.
    internal static void NoteHit(string imitating, int damage)
    {
        if (active && Hits.Count < MaxSounds && !SanityDirector.ForcedContext)
        {
            Guard(() => Hits.Add((ContentInfo.Clean(imitating), damage)));
        }
    }

    // Reactions, opening the probe for a symptom that just played: the phantom sound's record to hand back when the
    // probe closes (null for anything else). This pairs each sound with its own probe, whatever order probes close in.
    internal static object? TakeSound(Symptom symptom)
    {
        if (symptom is not PhantomSoundSymptom)
        {
            return null;
        }
        Sound? sound = justPlayed;
        justPlayed = null;
        return sound;
    }

    // Reactions, when that probe closes. A probe spoiled by something else (confounded) leaves the reaction unknown.
    internal static void SoundReaction(object record, bool directional, bool turned, bool stopped, bool clean)
    {
        if (record is Sound sound && clean)
        {
            sound.Turned = directional ? (turned ? 1 : 0) : -1;
            sound.Stopped = stopped ? 1 : 0;
        }
    }

    // The day ended: build the packet and hand it to the uploader (which only sends while sharing is on).
    internal static void EndDay(string ended, float fps, float fpsMin)
    {
        if (!active)
        {
            return;
        }
        active = false;
        if (wasInside)
        {
            Trips.Add(alive - tripStart);   // still inside at the end (or died inside): the trip lasted until then
            wasInside = false;
        }
        string? packet = null;
        Guard(() => packet = Build(ended, fps, fpsMin));
        if (packet != null)
        {
            StatsUploader.Submit(packet);
        }
    }

    // ---- Counting ----

    private static void CountSymptom(string detail, int slot)
    {
        string key = KeyFor(detail);
        if (key.Length == 0)
        {
            return;
        }
        if (!Counts.TryGetValue(key, out int[] counts))
        {
            counts = new int[Size];
            Counts[key] = counts;
        }
        counts[slot]++;
    }

    // ROLL rows (natural only): every symptom that was drawn this roll, whatever happened to it.
    private static void CountDraws(string detail)
    {
        string[] parts = detail.Split('|');
        if (parts.Length < 3 || parts[0].Contains(" F8"))
        {
            return;
        }
        foreach (Match match in Regex.Matches(parts[2], @"(\w+):(rejected|failed|played|armed|held)"))
        {
            string key = match.Groups[1].Value;
            if (!Counts.TryGetValue(key, out int[] counts))
            {
                counts = new int[Size];
                Counts[key] = counts;
            }
            counts[Drawn]++;
        }
    }

    private static void CountReaction(string detail)
    {
        Dictionary<string, string> kv = Pairs(detail);
        if (!kv.TryGetValue("confound", out string confound) || confound != "none")
        {
            return;
        }
        string key = kv.TryGetValue("kind", out string kind) && kind == "control" ? "control"
            : kv.TryGetValue("name", out string probeName) ? probeName : "";
        if (key.Length == 0)
        {
            return;
        }
        if (!ReactionCounts.TryGetValue(key, out int[] r))
        {
            r = new int[RSize];
            ReactionCounts[key] = r;
        }
        r[Probes]++;
        if (Flag(kv, "dir"))
        {
            r[Directional]++;
        }
        if (Flag(kv, "turned")) r[Turned]++;
        if (Flag(kv, "stopped")) r[Stopped]++;
        if (Flag(kv, "sprinted")) r[Sprinted]++;
        if (Flag(kv, "scan")) r[Scanned]++;
    }

    // "real 17:37 shown 21:39 (+4.0 h) after 258 s inside, 7.1 min after landing"
    private static void NoteLostTime(string detail)
    {
        lostTimeFired = true;
        Match inside = Regex.Match(detail, @"after (\d+) s inside");
        Match landing = Regex.Match(detail, @"([\d.]+) min after landing");
        lostTimeTrip = inside.Success ? Parse(inside.Groups[1].Value) : -1f;
        lostTimeMinutes = landing.Success ? Parse(landing.Groups[1].Value) : -1f;
    }

    // "sampled ... | Flicker U=120 D=20 B=0 inside=... blocked=not inside:200/no light:150 | PhantomFear ..."
    // (EligibilityLog.Summary)
    private static void ReadEligibility(string detail)
    {
        foreach (string part in detail.Split('|').Skip(1))
        {
            string[] words = part.Trim().Split(' ');
            if (words.Length == 0 || words[0].Length == 0)
            {
                continue;
            }
            Dictionary<string, string> kv = Pairs(part);
            int seconds = 0;
            foreach (string tier in new[] { "U", "D", "B" })
            {
                if (kv.TryGetValue(tier, out string s))
                {
                    seconds += (int)Parse(s);
                }
            }
            // "blocked=kind:seconds/kind:seconds..." (always last): the main blocker is the one that held it back
            // longest.
            string blocker = "none";
            float longest = 0f;
            int at = part.IndexOf("blocked=", StringComparison.Ordinal);
            if (at >= 0)
            {
                foreach (string entry in part.Substring(at + "blocked=".Length).Trim().Split('/'))
                {
                    int colon = entry.LastIndexOf(':');
                    float blockedFor = colon > 0 ? Parse(entry.Substring(colon + 1)) : -1f;
                    if (blockedFor > longest)
                    {
                        longest = blockedFor;
                        blocker = ContentInfo.Clean(entry.Substring(0, colon).Replace('_', ' '));
                    }
                }
            }
            Eligibility[words[0]] = (seconds, blocker);
        }
    }

    private static void CountMonsters()
    {
        RoundManager roundManager = RoundManager.Instance;
        if (roundManager == null || roundManager.SpawnedEnemies == null)
        {
            return;
        }
        foreach (EnemyAI enemy in roundManager.SpawnedEnemies)
        {
            if (enemy == null || !MonstersCounted.Add(enemy.GetInstanceID()) || Monsters.Count >= MaxMonsters)
            {
                continue;
            }
            string name = ContentInfo.Clean(enemy.enemyType != null ? enemy.enemyType.enemyName : null);
            Monsters[name] = Monsters.TryGetValue(name, out var seen)
                ? (seen.Count + 1, seen.Source)
                : (1, ContentInfo.MonsterSource(enemy));
        }
    }

    // ---- The packet ----

    private static string Build(string ended, float fps, float fpsMin)
    {
        Json json = new Json();
        json.Num("schema", SchemaVersion)
            .Str("mod", Plugin.BuildVersion)
            .Str("game", gameVersion)
            .Str("session", SessionId)
            .Num("day", day)
            .Str("role", role)
            .Num("players", SanityMeter.PlayersAtStart)
            .Obj("moon", o => o.Str("name", moonName).Bool("vanilla", ContentInfo.IsVanillaMoon(moonName)).Str("source", moonSource))
            .Obj("interior", o => o.Str("name", interiorName).Bool("vanilla", ContentInfo.IsVanillaInterior(interiorName)).Str("source", interiorSource))
            .Obj("weather", o => o.Str("name", weatherName).Bool("vanilla", ContentInfo.IsVanillaWeather(weatherName)).Bool("known", Weather.IsKnown(weatherName)))
            .Num("day_length", Round(dayLength, 2))
            .Str("ended", earlyVote && ended == "left" ? "early_vote" : ended)
            .Num("alive_s", (int)alive)
            .Num("dead_s", (int)dead)
            .Str("death", causeOfDeath.Length > 0 ? causeOfDeath : "none")
            .Num("revives", revives)
            .Num("teammates_alive_at_end", teammatesAlive)
            .Obj("tiers_s", o => o.Num("calm", (int)TierSeconds[0]).Num("uneasy", (int)TierSeconds[1]).Num("disturbed", (int)TierSeconds[2]).Num("breaking", (int)TierSeconds[3]))
            .Num("tier_changes", tierChanges)
            .Num("min_sanity", (int)minSanity)
            .Num("bounces", bounces)
            .Obj("reliefs", o => { foreach (var r in Reliefs) o.Num(r.Key, r.Value); })
            .Obj("time_s", o => o.Num("inside_alone", (int)insideAlone).Num("inside_company", (int)insideCompany)
                .Num("outside_alone", (int)outsideAlone).Num("outside_company", (int)outsideCompany).Num("ship", (int)ship)
                .Num("fear", (int)fear).Num("evening", (int)evening).Num("sprint", (int)sprint))
            .Obj("trips", o => o.Num("count", Trips.Count).Num("longest_s", Trips.Count > 0 ? (int)Trips.Max() : 0)
                .Num("median_s", Trips.Count > 0 ? (int)Trips.OrderBy(t => t).ElementAt(Trips.Count / 2) : 0))
            .Obj("real", o => o.Num("fear_jumps", realFearJumps).Num("hits", hits).Num("damage", (int)damageTaken))
            .Obj("loot", o => o.Num("delivered", delivered).Num("sanity", Round(lootSanity, 1)))
            .Obj("symptoms", o =>
            {
                foreach (var c in Counts)
                {
                    int[] n = c.Value;
                    o.Obj(c.Key, s => s.Num("drawn", n[Drawn]).Num("played", n[Played]).Num("armed", n[Armed]).Num("held", n[Held])
                        .Num("dropped", n[Dropped]).Num("failed", n[Failed]).Num("rejected", n[Rejected])
                        .Num("played_uneasy", n[PlayedU]).Num("played_disturbed", n[PlayedD]).Num("played_breaking", n[PlayedB]));
                }
            })
            .Obj("reactions", o =>
            {
                foreach (var r in ReactionCounts)
                {
                    int[] n = r.Value;
                    o.Obj(r.Key, s => s.Num("probes", n[Probes]).Num("directional", n[Directional]).Num("turned", n[Turned])
                        .Num("stopped", n[Stopped]).Num("sprinted", n[Sprinted]).Num("scanned", n[Scanned]));
                }
            })
            .Arr("sounds", Sounds, (s, a) => a.Str("monster", s.Monster).Str("source", s.Source).Str("place", s.Place)
                .Str("ground", s.Ground).Num("turned", s.Turned).Num("stopped", s.Stopped))
            .Arr("phantom_hits", Hits, (h, a) => a.Str("as", h.As).Num("damage", h.Damage))
            .Num("apparatus_pulled_s", (int)powerOffAt)
            .Arr("monsters", Monsters.ToList(), (m, a) => a.Str("name", m.Key).Num("count", m.Value.Count).Str("source", m.Value.Source))
            .Obj("lost_time", o => o.Bool("fired", lostTimeFired).Num("min_after_landing", Round(lostTimeMinutes, 1)).Num("trip_s", (int)lostTimeTrip))
            .Obj("could_play_s", o => { foreach (var e in Eligibility) o.Obj(e.Key, s => s.Num("seconds", e.Value.Seconds).Str("main_blocker", e.Value.Blocker)); })
            .Str("mood", moodName)
            .Num("echoes", echoes)
            .Num("chains", chainsPlayed)
            .Obj("debug", o => o.Bool("keys", Plugin.DebugKeys.Value).Bool("readout", Plugin.ShowReadout.Value)
                .Bool("presence_marker", Plugin.ShowPresence.Value).Num("forced_plays", forcedPlays).Num("debug_actions", debugEvents))
            .Obj("settings", o => o.Num("drain", Round(Plugin.DrainMultiplier.Value, 2)).Num("recovery", Round(Plugin.RecoveryMultiplier.Value, 2))
                .Num("loudness", Round(PhantomAudio.Loudness != null ? PhantomAudio.Loudness.Value : 1f, 2))
                .Str("symptoms_off", string.Join(" ", SanityDirector.Symptoms.Where(s => !s.Enabled).Select(s => s.ConfigKey))))
            .Obj("mods", o => { foreach (string mod in Telemetry.WatchedModsFound()) o.Bool(mod, true); })
            .Obj("errors", o => { foreach (var e in Errors) o.Num(e.Key, e.Value); })
            .Obj("fps", o => o.Num("avg", (int)fps).Num("min", (int)fpsMin));
        return json.ToString();
    }

    // ---- Helpers ----

    // A symptom's config key from the start of an event's text (its display name), or "".
    private static string KeyFor(string detail)
    {
        Symptom? best = null;
        foreach (Symptom symptom in SanityDirector.Symptoms)
        {
            if (detail.StartsWith(symptom.Name, StringComparison.Ordinal) && (best == null || symptom.Name.Length > best.Name.Length))
            {
                best = symptom;
            }
        }
        return best != null ? best.ConfigKey : "";
    }

    // The floor right under you, read now: the game's own surface value is only updated when you take a step (and
    // keeps the last one on a floor with no surface tag), so it can still be the outside ground just after you walk in.
    // Outside ground is "terrain"; a floor with no surface tag is "untagged".
    private static string GroundName(PlayerControllerB player)
    {
        StartOfRound startOfRound = StartOfRound.Instance;
        if (startOfRound == null || startOfRound.footstepSurfaces == null || player.thisPlayerBody == null)
        {
            return ContentInfo.Unknown;
        }
        Ray down = new Ray(player.thisPlayerBody.position + Vector3.up, Vector3.down);
        if (!Physics.Raycast(down, out RaycastHit hit, 6f, startOfRound.walkableSurfacesMask, QueryTriggerInteraction.Ignore))
        {
            return ContentInfo.Unknown;
        }
        if (hit.collider.GetComponent<Terrain>() != null)
        {
            return "terrain";
        }
        foreach (FootstepSurface surface in startOfRound.footstepSurfaces)
        {
            if (surface != null && hit.collider.CompareTag(surface.surfaceTag))
            {
                return ContentInfo.Clean(surface.surfaceTag);
            }
        }
        return "untagged";
    }

    private static string Role()
    {
        StartOfRound startOfRound = StartOfRound.Instance;
        if (startOfRound != null && startOfRound.connectedPlayersAmount == 0)
        {
            return "solo";
        }
        return startOfRound != null && startOfRound.IsServer ? "host" : "client";
    }

    private static string GameVersion()
    {
        GameNetworkManager networkManager = GameNetworkManager.Instance;
        return networkManager != null ? networkManager.gameVersionNum.ToString(CultureInfo.InvariantCulture) : ContentInfo.Unknown;
    }

    private static int TeammatesAlive()
    {
        StartOfRound startOfRound = StartOfRound.Instance;
        GameNetworkManager networkManager = GameNetworkManager.Instance;
        if (startOfRound == null || startOfRound.allPlayerScripts == null || networkManager == null)
        {
            return -1;
        }
        return startOfRound.allPlayerScripts.Count(p => p != null && p != networkManager.localPlayerController
            && p.isPlayerControlled && !p.isPlayerDead);
    }

    private static string Source(string found, bool vanilla) => found.Length > 0 ? found : vanilla ? "vanilla" : ContentInfo.Unknown;

    // Re-reads the day's context (see the fields). A value that can't be read right now keeps the last one read.
    private static void ReadContext()
    {
        StartOfRound startOfRound = StartOfRound.Instance;
        if (startOfRound == null)
        {
            return;
        }
        moonName = Known(ContentInfo.Clean(startOfRound.currentLevel != null ? startOfRound.currentLevel.PlanetName : null), moonName);
        moonSource = Source(ContentInfo.MoonSource(), ContentInfo.IsVanillaMoon(moonName));
        interiorName = Known(ContentInfo.Clean(Telemetry.CurrentInterior), interiorName);
        interiorSource = Source(ContentInfo.InteriorSource(), ContentInfo.IsVanillaInterior(interiorName));
        weatherName = Known(ContentInfo.Clean(Weather.Name), weatherName);
        moodName = Known(ContentInfo.Clean(DayMood.Name), moodName);
        role = Role();
        gameVersion = Known(GameVersion(), gameVersion);
        if (DayLength.Measured)
        {
            dayLength = DayLength.Factor;
        }
        TimeOfDay timeOfDay = TimeOfDay.Instance;
        earlyVote = timeOfDay != null && timeOfDay.shipLeaveAutomaticallyTime < 0.99f;
        teammatesAlive = TeammatesAlive();
    }

    private static string Known(string value, string previous) => value != ContentInfo.Unknown ? value : previous;

    private static Dictionary<string, string> Pairs(string text)
    {
        Dictionary<string, string> kv = new Dictionary<string, string>();
        foreach (string word in text.Split(' '))
        {
            int eq = word.IndexOf('=');
            if (eq > 0)
            {
                kv[word.Substring(0, eq)] = word.Substring(eq + 1);
            }
        }
        return kv;
    }

    private static bool Flag(Dictionary<string, string> kv, string key) => kv.TryGetValue(key, out string value) && value == "1";

    private static float Parse(string text)
    {
        Match match = Number.Match(text);
        return match.Success && float.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float n) ? n : -1f;
    }

    private static float Round(float value, int digits) => (float)Math.Round(value, digits);

    private static void Add(Dictionary<string, int> counts, string key)
    {
        counts[key] = counts.TryGetValue(key, out int n) ? n + 1 : 1;
    }

    // Stats must never get in the way: the first error is logged once, then they quietly carry on.
    private static void Guard(Action action)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            if (!warned)
            {
                warned = true;
                Plugin.Log.LogWarning($"Stats: skipped a part ({e.GetType().Name}: {e.Message}).");
            }
        }
    }

    // A tiny JSON writer: numbers, strings, booleans, nested objects and arrays. Enough for the packet.
    private sealed class Json
    {
        private readonly StringBuilder text = new StringBuilder("{");
        private bool first = true;

        internal Json Num(string key, float value) => Raw(key, float.IsNaN(value) || float.IsInfinity(value) ? "-1" : value.ToString("0.##", CultureInfo.InvariantCulture));

        internal Json Str(string key, string value) => Raw(key, Quote(value));

        internal Json Bool(string key, bool value) => Raw(key, value ? "true" : "false");

        // A part that fails keeps what it had written so far (Raw only ever writes whole pairs), and the rest goes on.
        internal Json Obj(string key, Action<Json> fill)
        {
            Json inner = new Json();
            Safely(() => fill(inner));
            return Raw(key, inner.ToString());
        }

        internal Json Arr<T>(string key, IEnumerable<T> items, Action<T, Json> fill)
        {
            StringBuilder list = new StringBuilder("[");
            bool firstItem = true;
            Safely(() =>
            {
                foreach (T item in items)
                {
                    Json inner = new Json();
                    fill(item, inner);
                    list.Append(firstItem ? "" : ",").Append(inner);
                    firstItem = false;
                }
            });
            return Raw(key, list.Append(']').ToString());
        }

        private static void Safely(Action action) => Guard(action);

        public override string ToString() => text.ToString() + "}";

        private Json Raw(string key, string value)
        {
            text.Append(first ? "" : ",").Append(Quote(key)).Append(':').Append(value);
            first = false;
            return this;
        }

        private static string Quote(string value)
        {
            StringBuilder quoted = new StringBuilder("\"");
            foreach (char c in value)
            {
                if (c == '"' || c == '\\')
                {
                    quoted.Append('\\').Append(c);
                }
                else if (c < ' ')
                {
                    quoted.Append(' ');
                }
                else
                {
                    quoted.Append(c);
                }
            }
            return quoted.Append('"').ToString();
        }
    }
}
