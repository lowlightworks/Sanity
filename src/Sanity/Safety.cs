using System;
using System.Collections.Generic;

namespace Sanity;

// Sanity must never break the game or other mods. Sanity's code that runs inside the game's own methods (the
// Harmony patches) catches its own errors: an error in a prefix would stop the game's method, and an error in a
// postfix would skip every other mod's postfix after ours. The first error of each kind is logged once with its
// details (so it can be fixed); after that it stays silent, and the next frame simply tries again.
internal static class Safety
{
    private static readonly HashSet<string> Reported = new HashSet<string>();

    internal static void Report(string where, Exception error)
    {
        if (Reported.Add(where))
        {
            Plugin.Log.LogWarning($"Error in {where} (logged once, Sanity keeps going): {error}");

            // Also in the telemetry file, so a session's CSV shows it (not for telemetry's own errors).
            if (where != "telemetry")
            {
                try
                {
                    Telemetry.Event("SANITY_ERROR", 0f, $"{where}: {error.GetType().Name}: {error.Message}");
                }
                catch (Exception)
                {
                    // Nothing more to do: it's in the BepInEx log.
                }
            }
        }
    }
}
