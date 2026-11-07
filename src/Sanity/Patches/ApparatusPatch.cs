using System;
using HarmonyLib;

namespace Sanity.Patches;

// The apparatus: a moment after it's pulled, the game cuts the facility's power for good and plays its radiation
// warning on everyone's HUD (it's only ever played for that). Right after the warning, the sanity meter reacts
// (SanityMeter.OnPowerOff). Every patch catches its own errors (see Safety).
[HarmonyPatch(typeof(HUDManager), nameof(HUDManager.RadiationWarningHUD))]
internal static class ApparatusPatch
{
    [HarmonyPostfix]
    private static void Postfix()
    {
        try
        {
            SanityMeter.OnPowerOff("radiation warning");
        }
        catch (Exception e)
        {
            Safety.Report("the apparatus hook", e);
        }
    }
}
