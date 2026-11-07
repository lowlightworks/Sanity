using System.Collections.Generic;
using System.Text;
using GameNetcodeStuff;
using Sanity.Symptoms;
using static System.FormattableString;

namespace Sanity;

// For the development log and the stats: how much of the day each symptom COULD have played, and what stopped it the
// rest of the time.
// Every few seconds while symptoms are possible (on a moon, alive, below Calm) it asks every symptom the same
// question a natural roll asks (switched on? right tier? its own conditions?) and adds up the seconds, per tier
// and per place. It never plays anything and changes nothing (the symptoms' CantPlayReason only looks).
// The day's totals become the DAY_ELIGIBILITY row, written just before DAY_END.
internal static class EligibilityLog
{
    private const float SampleSeconds = 5f;
    private static readonly string[] TierLetters = { "C", "U", "D", "B" };
    private static readonly string[] Places = { "inside", "outside", "ship" };

    private static float timer;
    private static readonly float[] sampledByTier = new float[4];
    private static readonly float[] sampledByPlace = new float[3];
    private static float[,] eligibleByTier = new float[0, 4];      // [symptom, tier]: seconds it could have played
    private static float[,] eligibleByPlace = new float[0, 3];     // [symptom, place]
    private static Dictionary<string, float>[] blockedBy = System.Array.Empty<Dictionary<string, float>>(); // per symptom: kind of reason -> seconds
    private static readonly StringBuilder Text = new StringBuilder(640);

    // Called by the director every frame while symptoms are possible.
    internal static void Tick(PlayerControllerB player, float dt)
    {
        if (!Telemetry.Recording)
        {
            return;
        }

        timer += dt;
        if (timer < SampleSeconds)
        {
            return;
        }
        float seconds = timer;   // the stretch of time this one look stands for
        timer = 0f;

        IReadOnlyList<Symptom> symptoms = SanityDirector.Symptoms;
        EnsureSize(symptoms.Count);
        int tier = (int)SanityMeter.Tier;
        int place = SanityMeter.Inside ? 0 : SanityMeter.InShip ? 2 : 1;
        sampledByTier[tier] += seconds;
        sampledByPlace[place] += seconds;

        for (int i = 0; i < symptoms.Count; i++)
        {
            string reason = SanityDirector.NaturalWhyNot(symptoms[i], player);
            if (reason.Length == 0)
            {
                eligibleByTier[i, tier] += seconds;
                eligibleByPlace[i, place] += seconds;
                continue;
            }

            string kind = Kind(reason);
            blockedBy[i].TryGetValue(kind, out float total);
            blockedBy[i][kind] = total + seconds;
        }
    }

    // The DAY_ELIGIBILITY row. Whole seconds, so days can be added up later:
    //   sampled U=300 D=200 B=100 inside=350 outside=200 ship=50
    //   | Flicker U=120 D=20 B=0 inside=140 outside=0 ship=0 blocked=not inside:200/no light:150 | PhantomFear ...
    // "blocked" lists each kind of reason (the part of the reason before its colon) with the seconds it stopped it.
    internal static string Summary()
    {
        IReadOnlyList<Symptom> symptoms = SanityDirector.Symptoms;
        EnsureSize(symptoms.Count);

        Text.Clear();
        Text.Append("sampled");
        AppendTiersAndPlaces(sampledByTier, sampledByPlace);
        for (int i = 0; i < symptoms.Count; i++)
        {
            Text.Append(" | ").Append(symptoms[i].ConfigKey);
            for (int t = 1; t < TierLetters.Length; t++)
            {
                Text.Append(Invariant($" {TierLetters[t]}={eligibleByTier[i, t]:F0}"));
            }
            for (int p = 0; p < Places.Length; p++)
            {
                Text.Append(Invariant($" {Places[p]}={eligibleByPlace[i, p]:F0}"));
            }

            Text.Append(" blocked=");
            bool first = true;
            foreach (KeyValuePair<string, float> entry in blockedBy[i])
            {
                Text.Append(first ? "" : "/").Append(entry.Key).Append(Invariant($":{entry.Value:F0}"));
                first = false;
            }
        }
        return Text.ToString();
    }

    // A new day: forget yesterday.
    internal static void Reset()
    {
        timer = 0f;
        System.Array.Clear(sampledByTier, 0, sampledByTier.Length);
        System.Array.Clear(sampledByPlace, 0, sampledByPlace.Length);
        System.Array.Clear(eligibleByTier, 0, eligibleByTier.Length);
        System.Array.Clear(eligibleByPlace, 0, eligibleByPlace.Length);
        foreach (Dictionary<string, float> blocked in blockedBy)
        {
            blocked.Clear();
        }
    }

    private static void AppendTiersAndPlaces(float[] byTier, float[] byPlace)
    {
        for (int t = 1; t < TierLetters.Length; t++)
        {
            Text.Append(Invariant($" {TierLetters[t]}={byTier[t]:F0}"));
        }
        for (int p = 0; p < Places.Length; p++)
        {
            Text.Append(Invariant($" {Places[p]}={byPlace[p]:F0}"));
        }
    }

    // The kind of a reason: the part before its colon ("fear: 0.15 (needs below 0.1)" -> "fear"), or all of it.
    private static string Kind(string reason)
    {
        int colon = reason.IndexOf(':');
        return colon > 0 ? reason.Substring(0, colon) : reason;
    }

    // One row of totals per symptom (sized on first use, so new symptoms need no change here).
    private static void EnsureSize(int count)
    {
        if (blockedBy.Length == count)
        {
            return;
        }

        eligibleByTier = new float[count, TierLetters.Length];
        eligibleByPlace = new float[count, Places.Length];
        blockedBy = new Dictionary<string, float>[count];
        for (int i = 0; i < count; i++)
        {
            blockedBy[i] = new Dictionary<string, float>();
        }
    }
}
