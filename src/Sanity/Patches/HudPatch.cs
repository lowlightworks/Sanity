using System;
using HarmonyLib;
using Sanity.Symptoms;

namespace Sanity.Patches;

// Phantom hit: the health the phantom hits took stays taken on screen. The game draws the local player's health
// through these two methods (on damage, on healing, at round start); while hits have taken some, they're handed the
// shown health (real minus lost) instead. Real health is never touched. Every patch catches its own errors (Safety).
[HarmonyPatch(typeof(HUDManager))]
internal static class HudPatch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(HUDManager.UpdateHealthUI))]
    private static void HealthBarPrefix(ref int health)
    {
        try
        {
            if (PhantomHitSymptom.Lost > 0 && health > 0)
            {
                health = PhantomHitSymptom.Shown(health);
            }
        }
        catch (Exception e)
        {
            Safety.Report("the health bar hook", e);
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(HUDManager.SetCracksOnVisor))]
    private static void VisorPrefix(ref float health)
    {
        try
        {
            if (PhantomHitSymptom.Lost > 0 && health > 0f)
            {
                health = PhantomHitSymptom.Shown((int)health);
            }
        }
        catch (Exception e)
        {
            Safety.Report("the visor hook", e);
        }
    }
}
