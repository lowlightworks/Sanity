using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Bootstrap;
using GameNetcodeStuff;
using HarmonyLib;

namespace Sanity.Compat;

// LethalHUD (optional) draws its health number and health bar from the player's real health every frame,
// so on its own a phantom hit would only reach the red body image. While a phantom hit is showing, each of its health
// display methods sees the fake health instead (ShownHealthSwap: swapped in just before the call, real health
// put back right after, even if the method throws). Nothing else in the game ever sees the fake value.
// If LethalHUD isn't installed, or a newer version renamed things, this does nothing.
internal static class LethalHudCompat
{
    internal const string Guid = "LethalHUD";

    private static MethodInfo? shakeOnHit;

    internal static void Apply(Harmony harmony)
    {
        try
        {
            if (!Chainloader.PluginInfos.TryGetValue(Guid, out BepInEx.PluginInfo info) || info.Instance == null)
            {
                return;
            }
            Assembly assembly = info.Instance.GetType().Assembly;

            Type? display = assembly.GetType("LethalHUD.HUD.PlayerHPDisplay");
            Type? customBar = assembly.GetType("LethalHUD.CustomHUD.CustomHealthBar");
            Type? hudPatch = assembly.GetType("LethalHUD.Patches.HUDManagerPatch");

            List<MethodBase> targets = new List<MethodBase>();
            Add(targets, display, "UpdateNumber");       // the health number
            Add(targets, display, "ShakeOnHit");         // its hit shake (reads health for the size)
            Add(targets, customBar, "UpdateFromPlayer"); // the custom health bar
            if (hudPatch != null)
            {
                // The coroutine that re-applies the red body a tick after each health update.
                foreach (Type nested in hudPatch.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public))
                {
                    if (nested.Name.Contains("ApplySelfRedAfterTick"))
                    {
                        Add(targets, nested, "MoveNext");
                    }
                }
            }

            foreach (MethodBase target in targets)
            {
                ShownHealthSwap.Patch(harmony, target);
            }

            if (display != null)
            {
                shakeOnHit = AccessTools.Method(display, "ShakeOnHit", new[] { typeof(PlayerControllerB) });
            }
            Plugin.Log.LogInfo($"LethalHUD found: phantom hits show on its health display ({targets.Count} methods).");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"LethalHUD support skipped: {e.Message}");
        }
    }

    // Each fake hit: LethalHUD's own number shake, the same as a real hit gets.
    internal static void OnFakeHit(PlayerControllerB player)
    {
        if (shakeOnHit == null)
        {
            return;
        }
        try
        {
            shakeOnHit.Invoke(null, new object[] { player });
        }
        catch (Exception)
        {
            shakeOnHit = null; // this LethalHUD version doesn't match: stop trying
        }
    }

    private static void Add(List<MethodBase> targets, Type? type, string name)
    {
        if (type == null)
        {
            return;
        }
        MethodInfo? method = AccessTools.DeclaredMethod(type, name);
        if (method != null)
        {
            targets.Add(method);
        }
    }
}
