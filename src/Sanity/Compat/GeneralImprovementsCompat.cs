using System;
using System.Reflection;
using BepInEx.Bootstrap;
using HarmonyLib;

namespace Sanity.Compat;

// GeneralImprovements (optional) can show your health as a number on the HUD ("ShowHitPoints", "100 HP").
// It writes that number from your real health every frame, in its own HUD update
// (GeneralImprovements.Patches.HUDManagerPatch.Update), so on its own a phantom hit wouldn't change it.
// While a phantom hit is showing, that one update sees the fake health instead (ShownHealthSwap), so the number
// drops, holds and fades back together with the rest of the fake hit, and real damage switches it back at once.
// Nothing else of GeneralImprovements is touched: its ship monitors, med station and the health it sends to the
// other players keep using real health. If it isn't installed, or a newer version renamed things, this does nothing.
internal static class GeneralImprovementsCompat
{
    internal const string Guid = "ShaosilGaming.GeneralImprovements";

    internal static void Apply(Harmony harmony)
    {
        try
        {
            if (!Chainloader.PluginInfos.TryGetValue(Guid, out BepInEx.PluginInfo info) || info.Instance == null)
            {
                return;
            }

            Type? hudPatch = info.Instance.GetType().Assembly.GetType("GeneralImprovements.Patches.HUDManagerPatch");
            MethodInfo? update = hudPatch != null ? AccessTools.DeclaredMethod(hudPatch, "Update", Type.EmptyTypes) : null;
            if (update == null)
            {
                Plugin.Log.LogInfo("GeneralImprovements found, but not its HUD update: its HP number won't show phantom hits.");
                return;
            }

            ShownHealthSwap.Patch(harmony, update);
            Plugin.Log.LogInfo("GeneralImprovements found: phantom hits show on its HP number.");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"GeneralImprovements support skipped: {e.Message}");
        }
    }
}
