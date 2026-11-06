using System;
using HarmonyLib;
using Sanity.Symptoms;
using UnityEngine;

namespace Sanity.Patches;

// Lost time's hooks: while it lies, the clock is fed the shown time and the sun is set to it.
// Every patch catches its own errors (see Safety).
[HarmonyPatch]
internal static class ClockPatch
{
    // Runs first, before the game (and any clock mod) formats the clock: the time it's given becomes the shown time.
    // Mods that format the clock themselves from this call (GeneralImprovements' 24-hour clock, LethalHUD) show it too.
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(HUDManager), nameof(HUDManager.SetClock))]
    private static void SetClockPrefix(ref float timeNormalized)
    {
        try
        {
            float offset = LostTimeSymptom.ShownOffset;
            if (offset != 0f)
            {
                timeNormalized += offset;
            }
        }
        catch (Exception e)
        {
            Safety.Report("the clock hook", e);
        }
    }

    // The day icon next to the clock follows the shown time.
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(HUDManager), nameof(HUDManager.SetClockIcon))]
    private static void SetClockIconPrefix(ref DayMode dayMode)
    {
        try
        {
            float offset = LostTimeSymptom.ShownOffset;
            TimeOfDay timeOfDay = TimeOfDay.Instance;
            if (offset != 0f && timeOfDay != null)
            {
                dayMode = timeOfDay.GetDayPhase(Mathf.Clamp01(timeOfDay.normalizedTimeOfDay + offset));
            }
        }
        catch (Exception e)
        {
            Safety.Report("the clock icon hook", e);
        }
    }

    // Runs right after the game moves the sun for the real time this frame: set it to the shown time instead.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(TimeOfDay), "Update")]
    private static void SunPostfix(TimeOfDay __instance)
    {
        try
        {
            float offset = LostTimeSymptom.ShownOffset;
            if (offset != 0f && __instance.sunAnimator != null)
            {
                __instance.sunAnimator.SetFloat("timeOfDay", Mathf.Clamp(__instance.normalizedTimeOfDay + offset, 0f, 0.99f));
            }
        }
        catch (Exception e)
        {
            Safety.Report("the sun hook", e);
        }
    }
}
