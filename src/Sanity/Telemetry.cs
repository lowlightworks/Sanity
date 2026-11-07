using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using GameNetcodeStuff;
using UnityEngine;
using static System.FormattableString;

namespace Sanity;

// Records this player's sanity, and everything that changes it. Runs when either of these is on:
//   - the local log (config Development/DetailedTestLog, for playtesting): one CSV file per game launch in the
//     mod's own folder, "telemetry/". Players are written as their lobby slot (P1, P2, ...), never by name. Nothing in
//     it is sent anywhere.
//   - the anonymous stats (StatsUploader): every event below is also handed to DayStats, which builds the day's small
//     packet. Without the local log, no file is written.
//
// Two kinds of rows, same columns:
//   S (sample) - once per second on a moon: the "heart rate monitor" trace.
//   E (event)  - the moment something happens: a cause (entered the facility, door closed, damage,
//                fear jump, symptom, death...), with the sanity values at that instant.
// After the player dies, nothing more is recorded for that day except the day summary, unless they're revived (the
// REVIVED row; recording carries on from there). The stats still count the time spent dead.
// The first row after the header (SESSION) records the mod version, config and every tuning number,
// so any CSV can be matched to the build that made it.
internal static class Telemetry
{
    private const float SampleInterval = 1f;

    private const string Header =
        "t,day,moon,kind,event,value,detail,sanity,rate,tier," +
        "part_inside,part_night,part_fear,part_ship,part_outside," +
        "inside,in_ship,near_teammate,doors_closed,day_fraction,day_mode,fear,time_inside,dead,symptom_chance,mod_enabled," +
        "health,nearest_teammate_m,teammate_dy,weather,interior,players,game_dt,speed,sprint,armed," +
        "pos_x,pos_y,pos_z,yaw,turn,scans,uses,walkie_tx,light,alone_s,presence_m,lost_hp,wave,mood";

    private static readonly CultureInfo Num = CultureInfo.InvariantCulture;

    // ---- File ----
    private static StreamWriter? writer;
    private static bool failed;         // the file failed: no more file this launch (the stats carry on)
    private static bool playerSeen;     // no file before the first player tick (so a menu toggle can't start one)
    private static float sessionStart;

    // For the debug menu.
    internal static string FilePath = "";
    internal static bool Failed => failed;

    // Recording runs when the local log is on or the player shares the anonymous stats; the file is only for the log.
    internal static bool Recording => Plugin.TelemetryEnabled.Value || StatsUploader.Sharing;
    private static bool FileWanted => Plugin.TelemetryEnabled.Value && !failed;

    // Today's interior name, for the stats.
    internal static string CurrentInterior => interior;

    // A player as written in telemetry: their slot in the lobby ("P1", "P2", ...), never their name.
    internal static string PlayerLabel(PlayerControllerB? player) => player != null ? "P" + (player.playerClientId + 1) : "P?";

    // ---- Day tracking ----
    private static bool wasOnMoon;
    private static int day;
    private static string moon = "-";
    private static float sampleTimer;
    private static bool dead;
    private static int health = 100;
    private static string interior = "?";
    private static int players = 1;
    private static float secondsOnMoon;
    private static readonly HashSet<GrabbableObject> heldBefore = new HashSet<GrabbableObject>();   // any number of slots (modded hotbars)
    private static readonly HashSet<GrabbableObject> heldNow = new HashSet<GrabbableObject>();
    private static readonly HashSet<GrabbableObject> grabbedToday = new HashSet<GrabbableObject>(); // so re-pickups don't count twice
    private static bool slotsPrimed;
    // Loot numbers copied while on a moon: loot recovery resets itself the frame the day ends, before DAY_END is
    // written.
    private static int dayLevelTotal;
    private static int dayLevelItems;
    private static string dayScrapSource = "";
    private static float dayLootSanity;
    private static float dayLengthFactor = -1f;   // -1 until the clock has moved enough to measure
    private static Vector3 lastPosition;
    private static bool hasLastPosition;

    // ---- Previous values, to spot changes ----
    private static bool prevInside;
    private static bool prevInShip;
    private static bool prevNearTeammate;
    private static bool prevDoorsClosed;
    private static int prevFearZone;
    private static SanityTier prevTier;
    private static DayMode prevDayMode = DayMode.None;

    // ---- Day summary ----
    private static readonly float[] tierSeconds = new float[4];
    private static float minSanity;
    private static float secondsInside;
    private static float secondsAlone;
    private static int damageHits;
    private static int fearJumps;
    private static int symptoms;
    private static int nearbyDeaths;
    private static bool diedToday;
    private static int revives;
    private static int grabs;
    private static int grabValue;
    private static int delivered;
    private static int deliveredValue;
    private static float walked;
    private static float sprintSeconds;
    private static int frames;                  // frames on the moon today, for the average frame rate
    private static float minFps = 9999f;        // the slowest one-second sample today
    private static int sampleFrames;
    private static float sampleWalked;          // metres walked since the last S row (for its speed column)
    private static bool sprintingNow;
    // Per-second extras: where you are and look, how much you turned, what you pressed.
    private static float sampleTurn;
    private static int sampleScans;
    private static int sampleUses;
    private static float lastYaw;
    private static bool hasYaw;
    private static Vector3 position;
    private static float yawNow;
    private static bool talkingNow;
    private static bool lightNow;

    // Called once per frame for the local player, after the meter and the director.
    internal static void Tick(PlayerControllerB player)
    {
        if (!Recording)
        {
            // Both switched off mid-day: that day isn't recorded any further (and not sent as if it were whole).
            if (wasOnMoon)
            {
                wasOnMoon = false;
                DayStats.Discard();
            }
            return;
        }

        playerSeen = true;
        dead = player.isPlayerDead;
        health = player.health;

        bool onMoon = SanityMeter.OnMoon;
        if (onMoon && !wasOnMoon)
        {
            StartDay();
        }
        else if (!onMoon && wasOnMoon)
        {
            EndDay();
        }
        wasOnMoon = onMoon;
        if (onMoon)
        {
            DayStats.Tick(player, Time.deltaTime);
        }

        // Off a moon, or dead: nothing to record until the next day.
        if (!onMoon || dead)
        {
            return;
        }

        // Day summary totals.
        float dt = Time.deltaTime;
        tierSeconds[(int)SanityMeter.Tier] += dt;
        minSanity = Mathf.Min(minSanity, SanityMeter.Value);
        if (SanityMeter.Inside)
        {
            secondsInside += dt;
        }
        if (!SanityMeter.NearTeammate)
        {
            secondsAlone += dt;
        }

        secondsOnMoon += dt;
        frames++;
        sampleFrames++;
        players = StartOfRound.Instance != null ? StartOfRound.Instance.connectedPlayersAmount + 1 : 1;

        dayLevelTotal = LootRecovery.LevelTotal;
        dayLevelItems = LootRecovery.LevelItems;
        dayScrapSource = LootRecovery.CountSource;
        dayLootSanity = LootRecovery.GainedToday;
        dayLengthFactor = DayLength.Measured ? DayLength.Factor : -1f;   // -1: the clock hasn't moved enough to tell

        TrackMovement(player, dt);
        DetectGrabs(player);
        DetectChanges();

        // Reaction probes, the day's profile and opportunities, and the per-second extras.
        Reactions.Tick(player);
        Insights.Tick(player, secondsOnMoon);
        float yaw = player.gameplayCamera.transform.eulerAngles.y;
        sampleTurn += hasYaw ? Mathf.Abs(Mathf.DeltaAngle(lastYaw, yaw)) : 0f;
        lastYaw = yaw;
        hasYaw = true;
        sampleScans += InputWatch.Pressed("PingScan") ? 1 : 0;
        sampleUses += InputWatch.Pressed("Interact") ? 1 : 0;
        position = player.transform.position;
        yawNow = yaw;
        talkingNow = player.speakingToWalkieTalkie;
        lightNow = Reactions.LightOn(player);

        // The once-per-second trace. Saving to disk here means a crash loses at most ~1 second.
        // game_dt = the game time this row stands for (Unity's frame time, which can differ from the clock when the
        // game lags); the replay tool steps by it.
        sampleTimer += Time.deltaTime;
        if (sampleTimer >= SampleInterval)
        {
            float step = sampleTimer;
            sampleTimer = 0f;

            // The interior's name may not be readable on the first frames; try again once a second, not every frame.
            if (interior == "?")
            {
                interior = InteriorName();
            }

            if (step > 0f)
            {
                minFps = Mathf.Min(minFps, sampleFrames / step);
            }
            sprintingNow = player.isSprinting;
            WriteRow("S", "", "", "", step);
            sampleFrames = 0;
            sampleWalked = 0f;
            sampleTurn = 0f;
            sampleScans = 0;
            sampleUses = 0;
            Flush();
        }
    }

    // Record a moment: something happened. Value and detail depend on the event.
    internal static void Event(string name, float value, string detail)
    {
        DebugLog.Note(name, detail);

        // Before the first player tick (main menu) there's nothing to record, and no file yet.
        if (!Recording || !playerSeen)
        {
            return;
        }

        // Dead for the rest of the day: only the day markers still get written (and a revive, which ends it).
        if (dead && name != "DAY_START" && name != "DAY_END" && name != "DAY_ELIGIBILITY" && name != "REVIVED")
        {
            return;
        }

        switch (name)
        {
            case "REVIVED": revives++; break;
            case "DAMAGE": damageHits++; break;
            case "FEAR_JUMP": fearJumps++; break;
            case "SYMPTOM": symptoms++; break;
            case "TEAMMATE_DIED_NEARBY": nearbyDeaths++; break;
            case "DEPOSIT": delivered++; deliveredValue += (int)value; break;
        }

        DayStats.OnEvent(name, value, detail);
        WriteRow("E", name, value.ToString("0.###", Num), detail);
    }

    // Called by the KillPlayer patch, with the real cause of death.
    internal static void LocalDied(CauseOfDeath cause)
    {
        if (!Recording || !SanityMeter.OnMoon || dead)
        {
            return;
        }

        diedToday = true;
        Reactions.Flush("died");
        Event("DIED", 0f, cause.ToString());
        Flush();
    }

    // Creates the hidden object that closes the file when the game quits and notices a round vanishing mid-day.
    internal static void Create()
    {
        GameObject host = new GameObject("Sanity.Telemetry");
        host.hideFlags = HideFlags.HideAndDontSave;
        UnityEngine.Object.DontDestroyOnLoad(host);
        host.AddComponent<Lifecycle>();
    }

    internal static void Close()
    {
        // Quitting mid-day: still write that day's summary.
        if (wasOnMoon && Recording)
        {
            wasOnMoon = false;
            EndDay("quit");
        }

        try
        {
            writer?.Flush();
            writer?.Dispose();
        }
        catch (Exception)
        {
            // Closing anyway; nothing useful to do.
        }
        writer = null;
    }

    private static void StartDay()
    {
        day++;
        DayStats.StartDay();
        StartOfRound startOfRound = StartOfRound.Instance;
        moon = startOfRound != null && startOfRound.currentLevel != null ? startOfRound.currentLevel.PlanetName : "?";
        interior = InteriorName();

        Array.Clear(tierSeconds, 0, tierSeconds.Length);
        minSanity = 100f;
        secondsInside = 0f;
        secondsAlone = 0f;
        damageHits = 0;
        fearJumps = 0;
        symptoms = 0;
        nearbyDeaths = 0;
        diedToday = false;
        revives = 0;
        sampleTimer = 0f;
        grabs = 0;
        grabValue = 0;
        delivered = 0;
        deliveredValue = 0;
        walked = 0f;
        sprintSeconds = 0f;
        frames = 0;
        minFps = 9999f;
        sampleFrames = 0;
        sampleWalked = 0f;
        secondsOnMoon = 0f;
        hasLastPosition = false;
        heldBefore.Clear();
        grabbedToday.Clear();
        slotsPrimed = false;
        dayLevelTotal = 0;
        dayLevelItems = 0;
        dayScrapSource = "";
        dayLootSanity = 0f;
        EligibilityLog.Reset();
        Reactions.Reset();
        Insights.Reset();
        hasYaw = false;

        // Start "previous" values from now, so the first frame doesn't report fake changes.
        prevInside = SanityMeter.Inside;
        prevInShip = SanityMeter.InShip;
        prevNearTeammate = SanityMeter.NearTeammate;
        prevDoorsClosed = SanityMeter.DoorsClosed;
        prevTier = SanityMeter.Tier;
        prevFearZone = FearZone(SanityMeter.Fear);
        // "None" so the first frame always writes the day's real phase.
        prevDayMode = DayMode.None;

        // The interior's lights the game can flicker: lights / light animators (a modded interior that doesn't tag its
        // lights as "PoweredLight" has none, so neither the Ghost Girl nor our flicker can reach them).
        RoundManager roundManager = RoundManager.Instance;
        string lights = roundManager != null && roundManager.allPoweredLights != null && roundManager.allPoweredLightsAnimators != null
            ? Invariant($"{roundManager.allPoweredLights.Count}/{roundManager.allPoweredLightsAnimators.Count}")
            : "?";
        Event("DAY_START", day, Invariant(
            $"{moon} weather {Weather.Summary} interior {interior} players {SanityMeter.PlayersAtStart} lobby x{SanityMeter.LobbyMultiplier} lights {lights}"));
    }

    // ended: how the day ended: "left" (back to orbit), "quit" (game closed) or "disconnected" (back to the menu).
    private static void EndDay(string ended = "left")
    {
        string summary =
            Invariant($"calm={tierSeconds[0]:F0}s uneasy={tierSeconds[1]:F0}s disturbed={tierSeconds[2]:F0}s breaking={tierSeconds[3]:F0}s ") +
            Invariant($"min_sanity={minSanity:F1} inside={secondsInside:F0}s alone={secondsAlone:F0}s ") +
            Invariant($"damage_hits={damageHits} fear_jumps={fearJumps} symptoms={symptoms} nearby_deaths={nearbyDeaths} died={diedToday} revived={revives} ") +
            Invariant($"grabs={grabs} grab_value={grabValue} delivered={delivered} delivered_value={deliveredValue} ") +
            Invariant($"walked={walked:F0}m sprint={sprintSeconds:F0}s level_scrap={dayLevelTotal} level_items={dayLevelItems} scrap_source={(dayScrapSource.Length > 0 ? dayScrapSource : "none")} loot_sanity={dayLootSanity:F1} ") +
            Invariant($"fps={(secondsOnMoon > 0f ? frames / secondsOnMoon : 0f):F0} fps_min={(minFps < 9999f ? minFps : 0f):F0} day_length={dayLengthFactor:F2} ended={ended}");

        // Probes still open, the day's profile and opportunities, which symptoms could have played (EligibilityLog),
        // then the day summary.
        Reactions.Flush("day ended");
        Insights.WriteDay(day);
        Event("DAY_ELIGIBILITY", day, EligibilityLog.Summary());
        Event("DAY_END", day, summary);
        Flush();
        DayStats.EndDay(ended, secondsOnMoon > 0f ? frames / secondsOnMoon : 0f, minFps < 9999f ? minFps : 0f);

        // One line per day in the normal BepInEx log too, for a quick look.
        Plugin.Log.LogInfo($"Day {day} ({moon}) summary: {summary}");
    }

    // Called by the SetItemInElevator patch: an item is about to count as brought to the ship.
    // Only fresh loot (scrap worth something, found today, not already collected) is logged: that's what loot
    // recovery would reward. Keys, tools and bodies aren't loot.
    internal static void ScrapIntoShip(PlayerControllerB carrier, GrabbableObject item)
    {
        RoundManager roundManager = RoundManager.Instance;
        if (item.itemProperties == null || !item.itemProperties.isScrap || item.scrapValue <= 0 || item.scrapPersistedThroughRounds
            || roundManager == null || roundManager.scrapDroppedInShip.Contains(item) || !SanityMeter.OnMoon)
        {
            return;
        }

        GameNetworkManager networkManager = GameNetworkManager.Instance;
        bool mine = networkManager != null && carrier == networkManager.localPlayerController;
        string name = item.itemProperties.itemName;
        if (mine)
        {
            Event("DEPOSIT", item.scrapValue, Invariant($"{name} value {item.scrapValue}"));
        }
        else
        {
            Event("DEPOSIT_TEAMMATE", item.scrapValue, Invariant($"{name} value {item.scrapValue} by {PlayerLabel(carrier)}"));
        }
    }

    // Metres walked (flat) and seconds sprinting. Big jumps between frames (teleports) don't count.
    private static void TrackMovement(PlayerControllerB player, float dt)
    {
        Vector3 position = player.transform.position;
        if (hasLastPosition)
        {
            Vector3 step = position - lastPosition;
            step.y = 0f;
            float distance = step.magnitude;
            if (distance < 3f)
            {
                walked += distance;
                sampleWalked += distance;
            }
        }
        lastPosition = position;
        hasLastPosition = true;

        if (player.isSprinting)
        {
            sprintSeconds += dt;
        }
    }

    // A new item in your hands or slots since last frame = a grab.
    private static void DetectGrabs(PlayerControllerB player)
    {
        GrabbableObject[] slots = player.ItemSlots;
        if (slots == null)
        {
            return;
        }

        heldNow.Clear();
        foreach (GrabbableObject item in slots)
        {
            if (item != null)
            {
                heldNow.Add(item);
            }
        }

        // First frame of the day: whatever you're already carrying isn't a grab.
        if (slotsPrimed)
        {
            foreach (GrabbableObject item in heldNow)
            {
                if (heldBefore.Contains(item) || item.itemProperties == null)
                {
                    continue;
                }

                bool scrap = item.itemProperties.isScrap && item.scrapValue > 0;
                bool collected = item.isInShipRoom || item.scrapPersistedThroughRounds;
                bool first = grabbedToday.Add(item);
                if (scrap && !collected && first)
                {
                    grabs++;
                    grabValue += item.scrapValue;
                }

                string where = SanityMeter.Inside ? "inside" : SanityMeter.InShip ? "ship" : "outside";
                string kind = !scrap ? "tool" : collected ? "already collected" : "new scrap";
                Event("GRAB", item.scrapValue, Invariant($"{item.itemProperties.itemName} value {item.scrapValue} {kind} {where}{(first ? "" : " (again)")}"));
            }
        }
        slotsPrimed = true;

        heldBefore.Clear();
        heldBefore.UnionWith(heldNow);
    }

    // The interior's name (vanilla or modded), read without depending on the dungeon generator's own types.
    private static string InteriorName()
    {
        try
        {
            RoundManager roundManager = RoundManager.Instance;
            if (roundManager == null || roundManager.dungeonFlowTypes == null
                || roundManager.currentDungeonType < 0 || roundManager.currentDungeonType >= roundManager.dungeonFlowTypes.Length)
            {
                return "?";
            }

            object map = roundManager.dungeonFlowTypes[roundManager.currentDungeonType];
            FieldInfo? field = map.GetType().GetField("dungeonFlow");
            return field?.GetValue(map) is UnityEngine.Object flow && flow != null ? flow.name : "?";
        }
        catch (Exception)
        {
            return "?";
        }
    }

    // First row of every file: which build, which config, which numbers.
    private static void WriteSession()
    {
        string version = Plugin.BuildVersion;
        string symptomsOn = string.Join(" ", SanityDirector.Symptoms.Select(s => $"{s.ConfigKey}={(s.Enabled ? 1 : 0)}"));
        // Each symptom's card: lowest tier / base weight / recovery seconds.
        string cards = string.Join(" ", SanityDirector.Symptoms.Select(s => Invariant($"{s.ConfigKey}={s.MinTier}/w{s.BaseWeight}/r{s.RecoverySeconds}")));
        string detail = Invariant($"mod {version}; dials drain {Plugin.DrainMultiplier.Value} recovery {Plugin.RecoveryMultiplier.Value}; ")
            + $"debug keys {(Plugin.DebugKeys.Value ? 1 : 0)} readout {(Plugin.ShowReadout.Value ? 1 : 0)}; "
            + Invariant($"phantom loudness {PhantomAudio.Closer:0.##}x closer; ")
            + $"symptoms {symptomsOn}; cards {cards}; tuning {SanityMeter.TuningSummary}; director {SanityDirector.TuningSummary}; weather {Weather.TableSummary}; loot {LootRecovery.TuningSummary}; "
            + $"mods {ModsSummary()}";
        WriteRow("E", "SESSION", "0", detail);
    }

    // Mods that matter for reading the data (found by part of their plugin id), and how many plugins are loaded.
    private static readonly string[] WatchedMods =
    {
        "GeneralImprovements", "LethalHUD", "MoreShipUpgrades", "WeatherRegistry", "GoodItemScan", "CullFactory",
        "ReservedItemSlot", "LethalLevelLoader", "MoreCompany", "LateCompany",
        "Imperium", "UnityExplorer",   // test tools: they can stop the clock or the spawns, so odd days can be explained
    };

    // The watched mods that are installed, for the stats (plugin ids only matched, nothing else read).
    internal static List<string> WatchedModsFound()
    {
        List<string> found = new List<string>();
        try
        {
            ICollection<string> ids = BepInEx.Bootstrap.Chainloader.PluginInfos.Keys;
            foreach (string watched in WatchedMods)
            {
                if (ids.Any(id => id.IndexOf(watched, StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    found.Add(watched);
                }
            }
        }
        catch (Exception)
        {
            // Nothing found then.
        }
        return found;
    }

    internal static string ModsSummary()
    {
        try
        {
            ICollection<string> ids = BepInEx.Bootstrap.Chainloader.PluginInfos.Keys;
            List<string> found = new List<string>();
            foreach (string watched in WatchedMods)
            {
                if (ids.Any(id => id.IndexOf(watched, StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    found.Add(watched);
                }
            }
            return $"{ids.Count} plugins: {(found.Count > 0 ? string.Join(" ", found) : "none watched")}";
        }
        catch (Exception)
        {
            return "?";
        }
    }

    // The Presence's distance from you while it's here, for the S rows ("" when it isn't).
    private static string PresenceDistance()
    {
        return Symptoms.PresenceSymptom.Active ? Symptoms.PresenceSymptom.DistanceTo(position).ToString("F1", Num) : "";
    }

    // Armed symptoms waiting right now, for the S rows ("FakeLockedDoor+LostTime", empty if none).
    private static string ArmedNow()
    {
        string armed = "";
        foreach (Symptoms.Symptom symptom in SanityDirector.Symptoms)
        {
            if (symptom is Symptoms.ArmedSymptom waiting && waiting.IsArmed)
            {
                armed = armed.Length == 0 ? symptom.ConfigKey : armed + "+" + symptom.ConfigKey;
            }
        }
        return armed;
    }

    // Compare with last frame: anything that changes the rate gets its own event row.
    private static void DetectChanges()
    {
        Changed(ref prevInside, SanityMeter.Inside, "ENTER_FACILITY", "LEAVE_FACILITY");
        Changed(ref prevInShip, SanityMeter.InShip, "ENTER_SHIP", "LEAVE_SHIP");
        Changed(ref prevNearTeammate, SanityMeter.NearTeammate, "COMPANY_ON", "COMPANY_OFF");
        Changed(ref prevDoorsClosed, SanityMeter.DoorsClosed, "DOORS_CLOSED", "DOORS_OPENED");

        // Fear crossing the game's own thresholds (this also catches gradual fear, which has no jump).
        int fearZone = FearZone(SanityMeter.Fear);
        if (fearZone != prevFearZone)
        {
            Event("FEAR_ZONE", fearZone, Invariant($"zone {prevFearZone}->{fearZone} (fear {SanityMeter.Fear:F2})"));
            prevFearZone = fearZone;
        }

        if (SanityMeter.Tier != prevTier)
        {
            Event("TIER", (int)SanityMeter.Tier, $"{prevTier}->{SanityMeter.Tier}");
            prevTier = SanityMeter.Tier;
        }

        DayMode mode = CurrentDayMode();
        if (mode != prevDayMode)
        {
            Event("DAY_PHASE", (int)mode, mode.ToString());
            prevDayMode = mode;
        }
    }

    private static void Changed(ref bool previous, bool now, string becameTrue, string becameFalse)
    {
        if (now != previous)
        {
            Event(now ? becameTrue : becameFalse, 0f, "");
            previous = now;
        }
    }

    // The game's own fear thresholds: 0 = none, 1 = tension (0.1+), 2 = high (0.4+), 3 = peak (0.7+).
    private static int FearZone(float fear)
    {
        if (fear >= 0.7f)
        {
            return 3;
        }
        if (fear >= 0.4f)
        {
            return 2;
        }
        return fear >= 0.1f ? 1 : 0;
    }

    private static DayMode CurrentDayMode()
    {
        TimeOfDay timeOfDay = TimeOfDay.Instance;
        return timeOfDay != null ? timeOfDay.dayMode : DayMode.None;
    }

    // gameStep: S rows only, the game time since the previous S row (-1 = leave the column empty).
    private static void WriteRow(string kind, string eventName, string value, string detail, float gameStep = -1f)
    {
        if (!FileWanted || (writer == null && !Open()))
        {
            return;
        }

        try
        {
            string row = string.Join(",",
                (Time.realtimeSinceStartup - sessionStart).ToString("F1", Num),
                day.ToString(Num),
                Clean(moon),
                kind,
                eventName,
                value,
                Clean(detail),
                SanityMeter.Value.ToString("F2", Num),
                SanityMeter.Rate.ToString("F3", Num),
                SanityMeter.Tier.ToString(),
                SanityMeter.PartInside.ToString("F3", Num),
                SanityMeter.PartNight.ToString("F3", Num),
                SanityMeter.PartFear.ToString("F3", Num),
                SanityMeter.PartShip.ToString("F3", Num),
                SanityMeter.PartOutside.ToString("F3", Num),
                Bit(SanityMeter.Inside),
                Bit(SanityMeter.InShip),
                Bit(SanityMeter.NearTeammate),
                Bit(SanityMeter.DoorsClosed),
                SanityMeter.DayFraction.ToString("F3", Num),
                CurrentDayMode().ToString(),
                SanityMeter.Fear.ToString("F2", Num),
                SanityMeter.TimeInside.ToString("F0", Num),
                Bit(dead),
                SanityDirector.ChancePerSecond.ToString("F4", Num),
                Bit(Plugin.ModEnabled.Value),
                health.ToString(Num),
                SanityMeter.NearestTeammateDistance.ToString("F1", Num),
                SanityMeter.NearestTeammateHeight.ToString("F1", Num),
                Clean(Weather.Name),
                Clean(interior),
                players.ToString(Num),
                gameStep >= 0f ? gameStep.ToString("F3", Num) : "",
                gameStep > 0f ? (sampleWalked / gameStep).ToString("F2", Num) : "",
                gameStep >= 0f ? Bit(sprintingNow) : "",
                gameStep >= 0f ? ArmedNow() : "",
                gameStep >= 0f ? position.x.ToString("F1", Num) : "",
                gameStep >= 0f ? position.y.ToString("F1", Num) : "",
                gameStep >= 0f ? position.z.ToString("F1", Num) : "",
                gameStep >= 0f ? yawNow.ToString("F0", Num) : "",
                gameStep >= 0f ? sampleTurn.ToString("F0", Num) : "",
                gameStep >= 0f ? sampleScans.ToString(Num) : "",
                gameStep >= 0f ? sampleUses.ToString(Num) : "",
                gameStep >= 0f ? Bit(talkingNow) : "",
                gameStep >= 0f ? Bit(lightNow) : "",
                gameStep >= 0f ? SanityReliefs.AloneSeconds.ToString("F0", Num) : "",
                gameStep >= 0f ? PresenceDistance() : "",
                gameStep >= 0f ? Symptoms.PhantomHitSymptom.Lost.ToString(Num) : "",
                gameStep >= 0f ? (Waves.High ? "h" : "l") : "",
                gameStep >= 0f ? Clean(DayMood.Name) : "");

            writer!.WriteLine(row);
        }
        catch (Exception e)
        {
            Fail(e);
        }
    }

    private static bool Open()
    {
        try
        {
            string folder = Path.Combine(ModFolder, "telemetry");
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.csv");

            writer = new StreamWriter(path, append: false);
            FilePath = path;
            writer.WriteLine(Header);
            sessionStart = Time.realtimeSinceStartup;
            WriteSession();

            Plugin.Log.LogInfo($"Telemetry: writing to {path}");
            return true;
        }
        catch (Exception e)
        {
            Fail(e);
            return false;
        }
    }

    private static void Flush()
    {
        try
        {
            writer?.Flush();
        }
        catch (Exception e)
        {
            Fail(e);
        }
    }

    // The mod's own folder (where its DLL is): all of its files live in here.
    internal static string ModFolder => Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? Paths.PluginPath;

    // Fail silently: one warning, then the local file stops for this launch (the stats don't depend on it).
    private static void Fail(Exception e)
    {
        failed = true;
        Plugin.Log.LogWarning($"Telemetry: the local log stopped for this session: {e.Message}");
        try
        {
            writer?.Dispose();
        }
        catch (Exception)
        {
            // Closing anyway.
        }
        writer = null;
    }

    // Commas would break the CSV columns, so swap them out of free text.
    private static string Clean(string text)
    {
        return text.Replace(",", ";").Replace("\n", " ").Replace("\r", " ");
    }

    private static string Bit(bool value)
    {
        return value ? "1" : "0";
    }

    // Ends a day whose round vanished without going back to orbit (left to the main menu, kicked, host quit):
    // there's no local player any more, so the normal per-frame Tick can't notice.
    private static void RoundLost()
    {
        if (!wasOnMoon)
        {
            return;
        }

        wasOnMoon = false;
        if (Recording)
        {
            EndDay("disconnected");
        }
    }

    // A hidden object that lives for the whole game session. It does the two jobs the per-frame Tick can't:
    // closing the file when the game quits (the plugin's own object isn't relied on: in many packs BepInEx's
    // object doesn't live that long), and ending the day if the round disappears mid-day.
    private sealed class Lifecycle : MonoBehaviour
    {
        private void Update()
        {
            if (wasOnMoon && StartOfRound.Instance == null)
            {
                RoundLost();
            }
        }

        private void OnApplicationQuit()
        {
            Close();
        }
    }
}
