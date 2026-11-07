using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System.Linq;

namespace Sanity;

// Where to start reading:
//   SanityMeter      the hidden meter: what drains it and what brings it back (with SanityReliefs, LootRecovery,
//                    Weather, DayLength).
//   SanityDirector   decides when a symptom happens and which one (with DirectorLayers: waves, moods, chains;
//                    SprintHold: sounds that wait while you sprint).
//   Symptoms/        one file per symptom; Symptom.cs is the shared template, ArmedSymptom.cs the "wait for the
//                    right moment" kind. PhantomAudio plays their sounds.
//   Patches/         hooks into the game's own code (HarmonyX); Compat/ is for optional mods. Safety keeps every
//                    hook from breaking the game.
//   Telemetry, Reactions, Insights, EligibilityLog, InputWatch, Diagnostics: the recording behind the development
//                    log (off by default) and the anonymous stats (DayStats, StatsUploader, StatsPanel, ContentInfo).
//                    DebugMenu, DebugLog, SanityReadout: testing tools (off by default).
// docs/design.md describes how everything behaves and why.

/// <summary>
/// The BepInEx plugin class of Sanity.
/// </summary>
[BepInAutoPlugin]
[BepInDependency(Compat.LethalHudCompat.Guid, BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency(Compat.GeneralImprovementsCompat.Guid, BepInDependency.DependencyFlags.SoftDependency)]
public partial class Plugin : BaseUnityPlugin
{
    internal static ManualLogSource Log { get; private set; } = null!;
    internal static ConfigEntry<bool> ModEnabled { get; private set; } = null!;
    internal static ConfigEntry<bool> ShowReadout { get; private set; } = null!;
    internal static ConfigEntry<bool> DebugKeys { get; private set; } = null!;
    internal static ConfigEntry<bool> TelemetryEnabled { get; private set; } = null!;
    internal static ConfigEntry<bool> ShareStats { get; private set; } = null!;
    internal static ConfigEntry<bool> ShowPresence { get; private set; } = null!;
    internal static ConfigEntry<float> DrainMultiplier { get; private set; } = null!;
    internal static ConfigEntry<float> RecoveryMultiplier { get; private set; } = null!;

    /// <summary>
    /// The version plus the commit it was built from, e.g. "0.0.1+f12bc59", so dev builds can be told apart.
    /// </summary>
    internal static string BuildVersion
    {
        get
        {
            string info = typeof(Plugin).Assembly
                .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
                .FirstOrDefault()?.InformationalVersion ?? "?";
            int plus = info.IndexOf('+');
            // Shorten the full commit hash to 7 characters.
            return plus >= 0 && info.Length > plus + 8 ? info.Substring(0, plus + 8) : info;
        }
    }

    private void Awake()
    {
        Log = Logger;

        ModEnabled = Config.Bind(
            "General",
            "EnableMod",
            true,
            "Master switch. Off: sanity stays at 100 and no symptoms happen. Takes effect immediately.");
        ModEnabled.SettingChanged += (_, _) =>
            Telemetry.Event("MOD_TOGGLED", ModEnabled.Value ? 1f : 0f, ModEnabled.Value ? "mod enabled" : "mod disabled");

        DrainMultiplier = Config.Bind(
            "Tuning",
            "DrainMultiplier",
            1f,
            new ConfigDescription(
                "Scales how fast sanity drops. 2 = twice as fast, 0.5 = half as fast.",
                new AcceptableValueRange<float>(0f, 5f)));

        RecoveryMultiplier = Config.Bind(
            "Tuning",
            "RecoveryMultiplier",
            1f,
            new ConfigDescription(
                "Scales how fast sanity recovers. 2 = twice as fast, 0.5 = half as fast.",
                new AcceptableValueRange<float>(0f, 5f)));

        ShowReadout = Config.Bind(
            "Debug",
            "ShowSanityReadout",
            false,
            "Show the current sanity value on screen. For testing and tuning: it shows the hidden meter, so it spoils the mystery.");

        DebugKeys = Config.Bind(
            "Debug",
            "EnableDebugKeys",
            false,
            "Testing keys: F7 opens the debug menu, F8 fires a random symptom that makes sense right now.");

        ShowPresence = Config.Bind(
            "Debug",
            "ShowPresence",
            false,
            "Testing: draw a red ball where the Presence is while it's around (only on your screen).");

        // Off by default. A development setting for playtests (also switchable in the F7 debug menu's Tools tab).
        TelemetryEnabled = Config.Bind(
            "Telemetry",
            "EnableTelemetry",
            false,
            "Development: keep a detailed log of each game (sanity every second, every event) as a CSV file in the mod's own folder, under telemetry/. Players appear as P1, P2... Local only: nothing in it is sent anywhere.");

        // The anonymous stats: on by default; nothing is sent until the player has seen the notice on the main menu.
        ShareStats = Config.Bind(
            "Stats",
            "ShareAnonymousStats",
            true,
            "Send one small anonymous summary per in-game day, to help tune the mod: numbers only (time in each sanity level, which symptoms happened and how people reacted, moon, group size). No names, no IDs. Nothing is sent until you've seen the notice on the main menu (whether you've seen it is kept in the game's own settings, not here, so a modpack's config can't skip it for you). The last 10 sent are kept in stats/sent/ in the mod's folder, so you can read them. Full list of fields in the README.");

        PhantomAudio.Loudness = Config.Bind(
            "Tuning",
            "PhantomLoudness",
            1f,
            new ConfigDescription(
                "How loud phantom steps and sounds are. 1 = default (already louder than the real thing), 2 = louder, 0.5 = quieter. Takes effect on the next sound.",
                new AcceptableValueRange<float>(0.25f, 4f)));
        PhantomAudio.Loudness.SettingChanged += (_, _) =>
            Telemetry.Event("PHANTOM_LOUDNESS", PhantomAudio.Loudness.Value, $"phantom loudness set to {PhantomAudio.Loudness.Value:0.##}");

        SanityDirector.BindConfig(Config);
        Weather.BindConfig(Config);

        // Each patch class on its own (what PatchAll does, one class at a time), so one that can't apply (say, a game
        // update renamed its method) is logged once and the rest still work.
        Harmony harmony = new Harmony(Id);
        foreach (System.Type type in AccessTools.GetTypesFromAssembly(typeof(Plugin).Assembly))
        {
            try
            {
                harmony.CreateClassProcessor(type).Patch();
            }
            catch (System.Exception e)
            {
                Log.LogWarning($"Couldn't apply {type.Name} (that part of Sanity is off): {e.Message}");
            }
        }
        Compat.LethalHudCompat.Apply(harmony);
        Compat.GeneralImprovementsCompat.Apply(harmony);
        SanityReadout.Create();
        DebugMenu.Create();
        PhantomAudio.Create();
        Telemetry.Create();   // ends a day cut short (game closed, disconnected) and closes the log file on quit
        StatsUploader.Create();   // sends any waiting stats in the background (only if sharing is on)

        Log.LogInfo($"Plugin {Name} {BuildVersion} is loaded!");
    }
}