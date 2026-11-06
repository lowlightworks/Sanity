using System;
using HarmonyLib;

namespace Sanity.Patches;

// Reads the day's scrap values when the host sends them, for loot recovery (LootRecovery).
[HarmonyPatch(typeof(RoundManager))]
internal static class RoundManagerPatch
{
    private static bool failed;

    // Runs right after the game's SyncScrapValuesClientRpc: the host's message that gives every scrap item its value,
    // sent about 11 s after the scrap spawned. Until it arrives, scrap on everyone else's machine is worth 0.
    // Loot recovery counts the day's loot from the message's own list, so every player gets the same total.
    // This runs inside the game's network message handling, so it must never throw: on any error it warns once
    // and stops (loot recovery then falls back to counting the scene itself).
    [HarmonyPostfix]
    [HarmonyPatch("SyncScrapValuesClientRpc")]
    private static void SyncScrapValuesPostfix(int[] allScrapValue)
    {
        if (failed)
        {
            return;
        }

        try
        {
            LootRecovery.OnScrapValuesSynced(allScrapValue);
        }
        catch (Exception e)
        {
            failed = true;
            Plugin.Log.LogWarning($"Loot recovery: couldn't read the game's scrap values ({e.Message}); counting the scene instead.");
        }
    }
}
