using System;
using System.Reflection;
using GameNetcodeStuff;
using HarmonyLib;

namespace Sanity.Compat;

// The trick that lets other mods' health displays show a phantom hit (LethalHUD, GeneralImprovements).
// Those mods draw your health straight from the player's real health value every frame. While a phantom hit is
// showing, each of their display methods gets the fake health instead: swapped in just before the method runs, and
// the real health put back right after it (even if the method throws). Nothing else in the game ever sees the fake.
// Only ever put this around pure display code: GeneralImprovements also sends your health to the other players,
// and a fake value must never reach that.
internal static class ShownHealthSwap
{
    // Wrap one display method in the swap.
    internal static void Patch(Harmony harmony, MethodBase target)
    {
        harmony.Patch(target,
            prefix: new HarmonyMethod(typeof(ShownHealthSwap), nameof(SwapIn)),
            finalizer: new HarmonyMethod(typeof(ShownHealthSwap), nameof(SwapOut)));
    }

    // Before the display method: if a phantom hit is showing, remember the real health and put in the fake one.
    // __state carries the real health to SwapOut (-1 = nothing was swapped).
    private static void SwapIn(out int __state)
    {
        __state = -1;
        try
        {
            PlayerControllerB? player = LocalPlayer();
            if (player != null && Symptoms.PhantomHitSymptom.TryGetShownHealth(player, out int shown))
            {
                __state = player.health;
                player.health = shown;
            }
        }
        catch (Exception e)
        {
            Safety.Report("the HUD health swap", e);
        }
    }

    // After the display method (a "finalizer" runs even if the method threw): put the real health back.
    // (Any error of the method itself is left alone: Harmony passes it on as if we weren't there.)
    private static void SwapOut(int __state)
    {
        if (__state < 0)
        {
            return;
        }

        try
        {
            PlayerControllerB? player = LocalPlayer();
            if (player != null)
            {
                player.health = __state;
            }
        }
        catch (Exception e)
        {
            Safety.Report("the HUD health swap", e);
        }
    }

    private static PlayerControllerB? LocalPlayer()
    {
        GameNetworkManager manager = GameNetworkManager.Instance;
        if (manager == null)
        {
            return null;
        }
        PlayerControllerB player = manager.localPlayerController;
        return player != null ? player : null;
    }
}
