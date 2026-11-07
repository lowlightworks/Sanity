using System;
using System.Collections.Generic;
using System.Reflection;

namespace Sanity;

// Names of the day's content (moon, interior, weather, monsters) for the anonymous stats, with whether it's vanilla
// and, for modded content, which mod it came from when that can be found. Anything unreadable becomes "unknown"; a
// failure here never stops anything else (every lookup catches its own errors).
//   Monsters: vanilla monsters' code lives in the game's own assembly; a modded monster's code lives in its mod's
//   assembly, so that assembly's name is the source.
//   Moons and interiors: from LethalLevelLoader (most modded content loads through it), found by name with reflection
//   (no hard dependency); without it, only vanilla names are recognised.
internal static class ContentInfo
{
    internal const string Unknown = "unknown";
    private const int MaxNameLength = 48;

    private static readonly HashSet<string> VanillaMoons = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "41 Experimentation", "220 Assurance", "56 Vow", "21 Offense", "61 March", "20 Adamance", "85 Rend", "7 Dine",
        "8 Titan", "68 Artifice", "5 Embrion", "44 Liquidation", "71 Gordion",
    };

    private static readonly HashSet<string> VanillaWeathers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "None", "Clear", "Rainy", "Stormy", "Foggy", "Flooded", "Eclipsed", "DustClouds", "Dust Clouds",
    };

    // A name made safe for the stats: no separators, no line breaks, a sensible length. The mod's own "not known yet"
    // placeholders ("?", "-") count as unknown.
    internal static string Clean(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name == "?" || name == "-")
        {
            return Unknown;
        }
        string clean = name!.Replace(",", " ").Replace(";", " ").Replace("\"", "'").Replace("\n", " ").Replace("\r", " ").Trim();
        return clean.Length > MaxNameLength ? clean.Substring(0, MaxNameLength) : clean;
    }

    internal static bool IsVanillaMoon(string name) => VanillaMoons.Contains(name);

    internal static bool IsVanillaInterior(string name) =>
        name.StartsWith("Level1Flow", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Level2Flow", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Level3Flow", StringComparison.OrdinalIgnoreCase);

    internal static bool IsVanillaWeather(string name) => VanillaWeathers.Contains(name);

    // The mod a monster comes from: "vanilla", or its code's assembly name.
    internal static string MonsterSource(EnemyAI? enemy)
    {
        try
        {
            if (enemy == null)
            {
                return Unknown;
            }
            string assembly = enemy.GetType().Assembly.GetName().Name ?? Unknown;
            return assembly == "Assembly-CSharp" ? "vanilla" : Clean(assembly);
        }
        catch (Exception)
        {
            return Unknown;
        }
    }

    // Today's moon and interior source, from LethalLevelLoader if it's installed ("" when it isn't, or can't be read).
    internal static string MoonSource() => LevelLoaderSource("LethalLevelLoader.LevelManager", "CurrentExtendedLevel");

    internal static string InteriorSource() => LevelLoaderSource("LethalLevelLoader.DungeonManager", "CurrentExtendedDungeonFlow");

    private static string LevelLoaderSource(string typeName, string member)
    {
        try
        {
            Type? manager = FindType(typeName);
            object? content = manager != null ? ReadStatic(manager, member) : null;
            if (content == null)
            {
                return "";
            }
            object? source = ReadInstance(content, "ContentSourceName");
            return source is string text && text.Length > 0 ? Clean(text) : "";
        }
        catch (Exception)
        {
            return "";
        }
    }

    // Looked up once per name (searching every loaded assembly isn't free, and the stats ask once a second).
    private static readonly Dictionary<string, Type?> FoundTypes = new Dictionary<string, Type?>();

    private static Type? FindType(string fullName)
    {
        if (FoundTypes.TryGetValue(fullName, out Type? cached))
        {
            return cached;
        }
        Type? found = null;
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            found = assembly.GetType(fullName, throwOnError: false);
            if (found != null)
            {
                break;
            }
        }
        FoundTypes[fullName] = found;
        return found;
    }

    private static object? ReadStatic(Type type, string member)
    {
        const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        PropertyInfo? property = type.GetProperty(member, Flags);
        if (property != null)
        {
            return property.GetValue(null);
        }
        return type.GetField(member, Flags)?.GetValue(null);
    }

    private static object? ReadInstance(object target, string member)
    {
        const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        Type type = target.GetType();
        PropertyInfo? property = type.GetProperty(member, Flags);
        if (property != null)
        {
            return property.GetValue(target);
        }
        return type.GetField(member, Flags)?.GetValue(target);
    }
}
