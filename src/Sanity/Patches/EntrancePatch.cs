using System;
using GameNetcodeStuff;
using HarmonyLib;
using Sanity.Symptoms;

namespace Sanity.Patches;

// Lost time's exit hook: the game's own entrance teleport (main entrance and fire exits) moves the local player in or
// out of the facility. Right after it moves you out, lost time gets its chance in the same frame, so a fake time is
// already in place the moment you're outside. Every patch catches its own errors (see Safety).
[HarmonyPatch(typeof(EntranceTeleport), nameof(EntranceTeleport.TeleportPlayer))]
internal static class EntrancePatch
{
    // Before: were you inside? (__state carries it to the postfix.)
    [HarmonyPrefix]
    private static void Prefix(out bool __state)
    {
        __state = false;
        try
        {
            PlayerControllerB? player = GameNetworkManager.Instance != null ? GameNetworkManager.Instance.localPlayerController : null;
            __state = player != null && player.isInsideFactory;
        }
        catch (Exception e)
        {
            Safety.Report("the entrance hook", e);
        }
    }

    // After: inside before and outside now means you just left (a blocked entrance moves nobody).
    [HarmonyPostfix]
    private static void Postfix(bool __state)
    {
        try
        {
            PlayerControllerB? player = GameNetworkManager.Instance != null ? GameNetworkManager.Instance.localPlayerController : null;
            if (__state && player != null && !player.isInsideFactory && !player.isPlayerDead)
            {
                LostTimeSymptom.OnLeftFacility(player);
            }
        }
        catch (Exception e)
        {
            Safety.Report("the entrance hook", e);
        }
    }
}
