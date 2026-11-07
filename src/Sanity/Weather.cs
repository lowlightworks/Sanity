using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using BepInEx.Configuration;
using UnityEngine;

namespace Sanity;

// Weather changes how hard each place drains. Three numbers per weather:
//   Outside    - multiplies the outside and night drains
//   Inside     - multiplies the inside drain (and its cap)
//   NightHeadStart - how far into the night it already is from landing (0 = normal, 0.5 = halfway through
//                Sundown, 1 = Midnight). Everything that follows the time of day (night drain and ship gain)
//                treats it as at least this late; it still gets later with the real time.
// Built-in values are in Table. The config line Weather/Overrides can replace any of them without a rebuild.
// The name comes from WeatherRegistry when it's installed (modded weathers only have names there), otherwise
// from the game. A weather with no row has no effect.
internal static class Weather
{
    private const float RefreshSeconds = 5f;

    // Outside, inside, night head start. Keys ignore case and spaces ("Dust Clouds" = "DustClouds").
    private static readonly Dictionary<string, Vector3> Table = new Dictionary<string, Vector3>(StringComparer.OrdinalIgnoreCase)
    {
        ["None"] = new Vector3(1f, 1f, 0f),          // clear
        ["Rainy"] = new Vector3(1.15f, 1f, 0f),      // mud and quicksand
        ["DustClouds"] = new Vector3(1.3f, 1f, 0f),  // low visibility outside
        ["Stormy"] = new Vector3(1.3f, 1f, 0f),      // lightning
        ["Flooded"] = new Vector3(1.25f, 1f, 0f),    // rising water
        ["Foggy"] = new Vector3(1.3f, 1f, 0f),       // fog (kept mild: some visual mods remove most of it)
        ["Eclipsed"] = new Vector3(1.4f, 1.2f, 0.3f), // monsters out from landing, inside and out
        ["Minefield"] = new Vector3(1.2f, 1f, 0f),   // Wesley's Weathers: mines outside
        ["Hurricane"] = new Vector3(1.3f, 1f, 0f),   // Wesley's Weathers: wind, rain, falling trees outside
        ["Earthquakes"] = new Vector3(1.2f, 1.2f, 0f), // Wesley's Weathers: quakes; ceilings collapse inside
        ["Hallowed"] = new Vector3(1.25f, 1.1f, 0.2f), // Wesley's Weathers: scrap heads outside, horror enemies inside
        ["Forsaken"] = new Vector3(1.45f, 1.3f, 0.3f), // Wesley's Weathers: blood rain; the Wail and Emma inside
    };

    internal static string Name { get; private set; } = "-";

    // Whether a weather has built-in numbers here (vanilla, or a modded one this mod knows), for the stats.
    internal static bool IsKnown(string name) => Table.ContainsKey(name.Replace(" ", "")) || name.Equals("Clear", StringComparison.OrdinalIgnoreCase);
    internal static float Outside { get; private set; } = 1f;
    internal static float Inside { get; private set; } = 1f;
    internal static float NightHeadStart { get; private set; }

    private static ConfigEntry<string> overrides = null!;
    private static Dictionary<string, Vector3> overrideTable = new Dictionary<string, Vector3>(StringComparer.OrdinalIgnoreCase);
    private static float refreshTimer;
    private static SelectableLevel? lastLevel;

    // WeatherRegistry's "current weather name" function, found once by name (no hard dependency).
    // Versions differ in what it takes: (level, ignoreOverride) in 0.8.x, (level) or nothing in others.
    private static bool registrySearched;
    private static MethodInfo? registryName;
    private static int registryArguments;          // 2 = (level, false), 1 = (level), 0 = ()

    // A short summary for the telemetry session row and the debug menu.
    internal static string Summary => Invariant($"{Name} outside x{Outside} inside x{Inside} night head start {NightHeadStart}");

    internal static string TableSummary
    {
        get
        {
            List<string> parts = new List<string>();
            foreach (KeyValuePair<string, Vector3> row in Table)
            {
                parts.Add(Invariant($"{row.Key}={row.Value.x}/{row.Value.y}/{row.Value.z}"));
            }
            return string.Join(" ", parts) + (overrides.Value.Length > 0 ? " overrides " + overrides.Value : "");
        }
    }

    internal static void BindConfig(ConfigFile config)
    {
        overrides = config.Bind(
            "Weather",
            "Overrides",
            "",
            "Replace the built-in weather effects. Format: Name=outside/inside/nightHeadStart, separated by semicolons: " +
            "outside and inside multiply how fast sanity drops there (1 = normal), nightHeadStart is how far into the night " +
            "it already counts as from landing (0 = normal, 1 = Midnight). " +
            "Example: Foggy=1.5/1/0; Eclipsed=1.3/1.15/0.5. Empty = use the built-in values.");
        ParseOverrides();
        overrides.SettingChanged += (_, _) =>
        {
            ParseOverrides();
            refreshTimer = 0f;
        };
    }

    // Called every frame on a moon. Re-reads the weather on landing and every few seconds after.
    internal static void Tick(StartOfRound startOfRound)
    {
        SelectableLevel level = startOfRound.currentLevel;
        refreshTimer -= Time.deltaTime;
        if (level == lastLevel && refreshTimer > 0f)
        {
            return;
        }

        lastLevel = level;
        refreshTimer = RefreshSeconds;
        Name = level != null ? ResolveName(level) : "?";

        string key = Name.Replace(" ", "");
        if (key.Equals("Clear", StringComparison.OrdinalIgnoreCase))
        {
            key = "None";
        }

        Vector3 effect = overrideTable.TryGetValue(key, out Vector3 custom) ? custom
            : Table.TryGetValue(key, out Vector3 builtIn) ? builtIn
            : new Vector3(1f, 1f, 0f);
        Outside = effect.x;
        Inside = effect.y;
        NightHeadStart = Mathf.Clamp01(effect.z);
    }

    // Off a moon: no weather.
    internal static void Reset()
    {
        Name = "-";
        Outside = 1f;
        Inside = 1f;
        NightHeadStart = 0f;
        lastLevel = null;
        refreshTimer = 0f;
    }

    private static string ResolveName(SelectableLevel level)
    {
        string vanilla = level.currentWeather.ToString();
        try
        {
            FindRegistry();
            if (registryName != null)
            {
                object? result = registryArguments == 2 ? registryName.Invoke(null, new object[] { level, false })
                    : registryArguments == 1 ? registryName.Invoke(null, new object[] { level })
                    : registryName.Invoke(null, null);
                if (result is string name && name.Length > 0)
                {
                    return name;
                }
            }
        }
        catch (Exception e)
        {
            // Never try again this session; the game's own name still works for vanilla weathers.
            registryName = null;
            Plugin.Log.LogWarning($"Weather: couldn't read WeatherRegistry's weather name ({e.Message}); using the game's.");
        }
        return vanilla;
    }

    private static void FindRegistry()
    {
        if (registrySearched)
        {
            return;
        }

        registrySearched = true;
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? manager;
            try
            {
                manager = assembly.GetType("WeatherRegistry.WeatherManager", throwOnError: false);
            }
            catch (Exception)
            {
                continue;
            }

            if (manager == null)
            {
                continue;
            }

            foreach (MethodInfo method in manager.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (method.Name != "GetCurrentWeatherName" || method.ReturnType != typeof(string))
                {
                    continue;
                }

                ParameterInfo[] parameters = method.GetParameters();
                int arguments =
                    parameters.Length == 2 && parameters[0].ParameterType == typeof(SelectableLevel) && parameters[1].ParameterType == typeof(bool) ? 2
                    : parameters.Length == 1 && parameters[0].ParameterType == typeof(SelectableLevel) ? 1
                    : parameters.Length == 0 ? 0
                    : -1;

                // Prefer a version that takes the level (it names that level's weather, not just the current one).
                if (arguments >= 0 && (registryName == null || (registryArguments == 0 && arguments > 0)))
                {
                    registryName = method;
                    registryArguments = arguments;
                }
            }

            Plugin.Log.LogInfo(registryName != null
                ? "Weather: using WeatherRegistry for weather names."
                : "Weather: WeatherRegistry found, but not its weather-name function; using the game's names.");
            return;
        }
    }

    private static void ParseOverrides()
    {
        overrideTable = new Dictionary<string, Vector3>(StringComparer.OrdinalIgnoreCase);
        foreach (string entry in overrides.Value.Split(';'))
        {
            string[] nameAndValues = entry.Split('=');
            if (nameAndValues.Length != 2)
            {
                continue;
            }

            string[] values = nameAndValues[1].Split('/');
            if (values.Length == 3
                && float.TryParse(values[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float outside)
                && float.TryParse(values[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float inside)
                && float.TryParse(values[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float floor))
            {
                overrideTable[nameAndValues[0].Trim().Replace(" ", "")] = new Vector3(outside, inside, floor);
            }
            else
            {
                Plugin.Log.LogWarning($"Weather: ignoring override \"{entry.Trim()}\" (expected Name=outside/inside/nightHeadStart).");
            }
        }
    }

    private static string Invariant(FormattableString text)
    {
        return FormattableString.Invariant(text);
    }
}
