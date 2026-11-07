using System;
using HarmonyLib;

namespace Sanity.Patches;

// The main menu's hook: once the game's own menu has set itself up, the anonymous-stats notice may show (StatsPanel).
// Every patch catches its own errors (see Safety).
[HarmonyPatch(typeof(MenuManager), "Start")]
internal static class MenuPatch
{
    [HarmonyPostfix]
    private static void Postfix(MenuManager __instance)
    {
        try
        {
            StatsPanel.Show(__instance);
        }
        catch (Exception e)
        {
            Safety.Report("the main menu hook", e);
        }
    }
}
