using UnityEngine;

namespace Sanity;

// A tiny on-screen label for testing. Hidden unless enabled in the config.
internal sealed class SanityReadout : MonoBehaviour
{
    internal static void Create()
    {
        GameObject host = new GameObject("Sanity.Readout");
        host.hideFlags = HideFlags.HideAndDontSave;
        DontDestroyOnLoad(host);
        host.AddComponent<SanityReadout>();
    }

    private void OnGUI()
    {
        if (!Plugin.ShowReadout.Value)
        {
            return;
        }

        // Also show the game's own fear level, raw (vanilla lets it drift below 0).
        float fear = StartOfRound.Instance != null ? StartOfRound.Instance.fearLevel : 0f;
        string off = Plugin.ModEnabled.Value ? "" : "   [MOD OFF]";
        GUI.Label(new Rect(10, 10, 600, 25), $"Sanity: {SanityMeter.Value:F1} ({SanityMeter.Tier})   rate: {SanityMeter.Rate:F2}   fear: {fear:F2}{off}");

        // Second line: the director. Chance per second, and roughly how many per minute at that rate.
        float chance = SanityDirector.ChancePerSecond;
        string ago = SanityDirector.SecondsSinceLast > 9000f ? "-" : $"{SanityDirector.SecondsSinceLast:F0}s ago";
        GUI.Label(new Rect(10, 30, 600, 25), $"Symptom chance: {chance:F3}/s (~{chance * 60f:F1}/min)   last: {SanityDirector.LastSymptom} ({ago})");

        // The Presence while it's around: what it is, where, and what it just did (newest first).
        GameNetcodeStuff.PlayerControllerB? player = GameNetworkManager.Instance != null ? GameNetworkManager.Instance.localPlayerController : null;
        if (player != null && Symptoms.PresenceSymptom.Active)
        {
            GUI.Label(new Rect(10, 50, 900, 25), "Presence: " + Symptoms.PresenceSymptom.DebugState(player));
            GUI.Label(new Rect(10, 70, 900, 25), "  did: " + Symptoms.PresenceSymptom.LastNotes);
        }
    }
}