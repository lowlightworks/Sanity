using System.Collections.Generic;

namespace Sanity;

// The debug menu's "what just happened" list: the last events worth reading (symptoms, arming, chains, the Presence,
// reliefs, problems), kept in memory whether telemetry is on or not. Fed by Telemetry.Event.
internal static class DebugLog
{
    internal struct Entry
    {
        internal float At;
        internal string Kind;    // symptom, chain, presence, meter, problem
        internal string Mark;    // ">" played, "+" armed, "x" dropped/failed, "->" chain, "." detail
        internal string Text;
    }

    private const int Max = 80;
    private static readonly List<Entry> Entries = new List<Entry>(Max);

    internal static IReadOnlyList<Entry> All => Entries;

    internal static void Clear()
    {
        Entries.Clear();
    }

    internal static void Note(string name, string detail)
    {
        string kind;
        string mark = ".";
        switch (name)
        {
            case "SYMPTOM":
                kind = detail.Contains("(chain from") ? "chain" : "symptom";
                mark = kind == "chain" ? "->" : ">";
                break;
            case "SYMPTOM_ARMED":
                kind = "symptom";
                mark = "+";
                break;
            case "SYMPTOM_DROPPED":
            case "SYMPTOM_FAILED":
            case "SYMPTOM_REJECTED":
                kind = "symptom";
                mark = "x";
                break;
            case "CHAIN":
                kind = "chain";
                mark = detail.EndsWith("played") ? "->" : detail.Contains("didn't play") ? "x" : ".";
                break;
            case "PRESENCE":
            case "PRESENCE_EVENT":
            case "PRESENCE_END":
                kind = "presence";
                mark = name == "PRESENCE" ? ">" : ".";
                break;
            case "RELIEF":
            case "BOUNCE":
            case "RELAX":
            case "MOOD":
            case "FEAR_JUMP":
            case "DAMAGE":
            case "TEAMMATE_DIED":
            case "TEAMMATE_DIED_NEARBY":
            case "TEAMMATE_DIED_SHIP":
            case "ECHO":
            case "REVIVED":
                kind = "meter";
                break;
            case "SANITY_ERROR":
            case "PROMPT_MISSING":
                kind = "problem";
                mark = "!";
                break;
            case "FLICKER":
            case "FAKE_DOOR":
            case "FAKE_DOOR_END":
            case "FAKE_DOOR_AFTER":
            case "VANISH":
            case "VANISH_END":
            case "PHANTOM_HIT":
            case "PHANTOM_HIT_END":
            case "PHANTOM_HIT_HEALED":
            case "PHANTOM_SOUND":
            case "PHANTOM_STEPS":
            case "PHANTOM_STEPS_END":
            case "PHANTOM_WALKIE":
            case "PHANTOM_WALKIE_END":
            case "PHANTOM_WALKIE_REPLY":
            case "LOST_TIME":
            case "LOST_TIME_END":
                kind = "symptom";
                break;
            default:
                return;
        }
        if (Entries.Count >= Max)
        {
            Entries.RemoveAt(0);
        }
        Entries.Add(new Entry { At = UnityEngine.Time.time, Kind = kind, Mark = mark, Text = Label(name) + detail });
    }

    private static string Label(string name)
    {
        return name switch
        {
            "SYMPTOM" or "SYMPTOM_ARMED" or "CHAIN" or "PRESENCE_EVENT" => name == "SYMPTOM_ARMED" ? "armed " : "",
            "SYMPTOM_DROPPED" => "dropped ",
            "SYMPTOM_FAILED" => "failed ",
            "SYMPTOM_REJECTED" => "",
            _ => name.ToLowerInvariant().Replace('_', ' ') + ": ",
        };
    }
}
