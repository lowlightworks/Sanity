using System;
using GameNetcodeStuff;
using HarmonyLib;
using Sanity.Symptoms;

namespace Sanity.Patches;

// The fake locked door's hooks. Every patch catches its own errors (see Safety).
// While a door shows as locked, it reads as locked (isLocked = true) only INSIDE the game's own door code below,
// put back the moment each call ends. So the game and every mod's patches on that code (GeneralImprovements' key
// text, Lategame Upgrades' Locksmith prompt and minigame) treat it exactly like a real locked door, and nothing else
// ever sees it locked.
[HarmonyPatch]
internal static class DoorPatch
{
    // Runs just BEFORE a key is used (held, or from the inventory with mods like GeneralImprovements: they call the
    // same method). On the door showing as locked, the key is taken instead and the game's own key code is skipped.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(KeyItem), nameof(KeyItem.ItemActivate))]
    private static bool KeyUsePrefix(KeyItem __instance)
    {
        try
        {
            return !FakeLockedDoorSymptom.TryUseKey(__instance);
        }
        catch (Exception e)
        {
            Safety.Report("the key hook", e);
            return true;
        }
    }

    // Runs right after a door is opened or closed by a player: a door you just used can't turn out locked.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(DoorLock), nameof(DoorLock.OpenOrCloseDoor))]
    private static void DoorUsedPostfix(DoorLock __instance, PlayerControllerB playerWhoTriggered)
    {
        try
        {
            GameNetworkManager networkManager = GameNetworkManager.Instance;
            if (networkManager != null && playerWhoTriggered != null && playerWhoTriggered == networkManager.localPlayerController)
            {
                FakeLockedDoorSymptom.OnDoorUsed(__instance);
            }
        }
        catch (Exception e)
        {
            Safety.Report("the door hook", e);
        }
    }

    // Runs right after a door is really unlocked (on every machine: the game sends it to everyone): never fake it
    // today.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(DoorLock), nameof(DoorLock.UnlockDoor))]
    private static void ReallyUnlockedPostfix(DoorLock __instance)
    {
        try
        {
            FakeLockedDoorSymptom.OnReallyUnlocked(__instance);
        }
        catch (Exception e)
        {
            Safety.Report("the door unlock record hook", e);
        }
    }

    // The door's own per-frame code computes the locked look (hover text; Locksmith's prompt).
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(DoorLock), "Update")]
    private static void DoorUpdatePrefix(DoorLock __instance, out bool __state)
    {
        __state = false;
        try
        {
            __state = FakeLockedDoorSymptom.BeginLockedLook(__instance);
        }
        catch (Exception e)
        {
            Safety.Report("the door look hook", e);
        }
    }

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(DoorLock), "Update")]
    private static void DoorUpdateFinalizer(DoorLock __instance, bool __state)
    {
        if (__state)
        {
            FakeLockedDoorSymptom.EndLockedLook(__instance);
        }
    }

    // Holding use on a locked door (Locksmith starts its minigame here).
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(DoorLock), nameof(DoorLock.OnHoldInteract))]
    private static void DoorHoldPrefix(DoorLock __instance, out bool __state)
    {
        __state = false;
        try
        {
            __state = FakeLockedDoorSymptom.BeginLockedLook(__instance);
        }
        catch (Exception e)
        {
            Safety.Report("the door hold hook", e);
        }
    }

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(DoorLock), nameof(DoorLock.OnHoldInteract))]
    private static void DoorHoldFinalizer(DoorLock __instance, bool __state)
    {
        if (__state)
        {
            FakeLockedDoorSymptom.EndLockedLook(__instance);
        }
    }

    // Bumping into a locked door (Locksmith can start its minigame here too).
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(InteractTrigger), "OnTriggerEnter")]
    private static void TriggerEnterPrefix(InteractTrigger __instance, out DoorLock? __state)
    {
        __state = null;
        try
        {
            __state = FakeLockedDoorSymptom.BeginLockedLook(__instance);
        }
        catch (Exception e)
        {
            Safety.Report("the door touch hook", e);
        }
    }

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(InteractTrigger), "OnTriggerEnter")]
    private static void TriggerEnterFinalizer(DoorLock? __state)
    {
        if (__state != null)
        {
            FakeLockedDoorSymptom.EndLockedLook(__state);
        }
    }

    // Something finished unlocking the shown door (Locksmith's minigame): it "unlocks" on this machine only.
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(DoorLock), nameof(DoorLock.UnlockDoorSyncWithServer))]
    private static bool UnlockPrefix(DoorLock __instance)
    {
        try
        {
            return !FakeLockedDoorSymptom.TryUnlockShown(__instance);
        }
        catch (Exception e)
        {
            Safety.Report("the door unlock hook", e);
            return true;
        }
    }
}
