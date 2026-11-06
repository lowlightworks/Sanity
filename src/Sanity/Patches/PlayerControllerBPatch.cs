using System;
using GameNetcodeStuff;
using HarmonyLib;

namespace Sanity.Patches;

// Every patch here runs inside the game's own methods, so each one catches its own errors (see Safety).
[HarmonyPatch(typeof(PlayerControllerB))]
internal static class PlayerControllerBPatch
{
    // Phantom hit's flinch stays local: while it plays, the game's animation sync to other players waits a moment.
    [HarmonyPrefix]
    [HarmonyPatch("UpdatePlayerAnimationsToOtherClients")]
    private static bool AnimationSyncPrefix(PlayerControllerB __instance)
    {
        try
        {
            GameNetworkManager networkManager = GameNetworkManager.Instance;
            return !(Symptoms.PhantomHitSymptom.HoldAnimationSync && networkManager != null
                     && __instance == networkManager.localPlayerController);
        }
        catch (Exception e)
        {
            Safety.Report("the animation sync hook", e);
            return true;
        }
    }

    // Runs right after the game's own PlayerControllerB.Update(), every frame.
    [HarmonyPostfix]
    [HarmonyPatch("Update")]
    private static void UpdatePostfix(PlayerControllerB __instance)
    {
        // Every player in the lobby has an Update. Only act for our own.
        GameNetworkManager networkManager = GameNetworkManager.Instance;
        if (networkManager == null || __instance != networkManager.localPlayerController)
        {
            return;
        }

        // Each part on its own, so one part failing doesn't stop the others.
        try
        {
            SanityMeter.Tick(__instance);
        }
        catch (Exception e)
        {
            Safety.Report("the sanity meter", e);
        }

        try
        {
            LootRecovery.Tick(__instance);
        }
        catch (Exception e)
        {
            Safety.Report("loot recovery", e);
        }

        try
        {
            SanityDirector.Tick(__instance);
        }
        catch (Exception e)
        {
            Safety.Report("the symptom director", e);
        }

        try
        {
            Diagnostics.Tick(__instance);
        }
        catch (Exception e)
        {
            Safety.Report("diagnostics", e);
        }

        try
        {
            Telemetry.Tick(__instance);
        }
        catch (Exception e)
        {
            Safety.Report("telemetry", e);
        }
    }

    // Runs just BEFORE the game's JumpToFearLevel: note the fear level beforehand.
    [HarmonyPrefix]
    [HarmonyPatch("JumpToFearLevel")]
    private static void JumpToFearLevelPrefix(out float __state)
    {
        __state = 0f;
        try
        {
            StartOfRound startOfRound = StartOfRound.Instance;
            __state = startOfRound != null ? startOfRound.fearLevel : 0f;
        }
        catch (Exception e)
        {
            Safety.Report("the fear hook", e);
        }
    }

    // Runs just AFTER it: if fear actually went up, tell the sanity meter.
    [HarmonyPostfix]
    [HarmonyPatch("JumpToFearLevel")]
    private static void JumpToFearLevelPostfix(PlayerControllerB __instance, float __state)
    {
        try
        {
            StartOfRound startOfRound = StartOfRound.Instance;
            GameNetworkManager networkManager = GameNetworkManager.Instance;
            if (startOfRound == null || networkManager == null || __instance != networkManager.localPlayerController)
            {
                return;
            }

            if (startOfRound.fearLevel > __state)
            {
                SanityMeter.OnFearJump(__instance, __state, startOfRound.fearLevel);
            }
        }
        catch (Exception e)
        {
            Safety.Report("the fear hook", e);
        }
    }

    // Runs right after the game's PlayFootstepLocal (an animation event: one of our own footsteps). Phantom steps in
    // follow mode step a beat after each of ours. Same checks as the game's own method (it only plays then).
    [HarmonyPostfix]
    [HarmonyPatch("PlayFootstepLocal")]
    private static void PlayFootstepLocalPostfix(PlayerControllerB __instance)
    {
        try
        {
            GameNetworkManager networkManager = GameNetworkManager.Instance;
            if (networkManager == null || __instance != networkManager.localPlayerController
                || __instance.isClimbingLadder || __instance.inSpecialInteractAnimation || !__instance.isPlayerControlled)
            {
                return;
            }

            Symptoms.PhantomStepsSymptom.OnLocalStep(__instance);
        }
        catch (Exception e)
        {
            Safety.Report("the footstep hook", e);
        }
    }

    // Runs just BEFORE the game's DamagePlayer. The sanity meter picks up the health drop itself;
    // this only tells it WHY (falls count for less).
    [HarmonyPrefix]
    [HarmonyPatch("DamagePlayer")]
    private static void DamagePlayerPrefix(PlayerControllerB __instance, CauseOfDeath causeOfDeath)
    {
        try
        {
            GameNetworkManager networkManager = GameNetworkManager.Instance;
            if (networkManager == null || __instance != networkManager.localPlayerController)
            {
                return;
            }

            SanityMeter.NoteDamageCause(causeOfDeath);
        }
        catch (Exception e)
        {
            Safety.Report("the damage hook", e);
        }
    }

    // Runs just BEFORE the game's SetItemInElevator: the moment an item counts as brought to the ship
    // (dropped in it, or carried in). Telemetry logs fresh scrap; the game's own popup fires on the same moment.
    [HarmonyPrefix]
    [HarmonyPatch("SetItemInElevator")]
    private static void SetItemInElevatorPrefix(PlayerControllerB __instance, bool droppedInShipRoom, GrabbableObject gObject)
    {
        try
        {
            if (!droppedInShipRoom || gObject == null || gObject.isInShipRoom)
            {
                return;
            }

            Telemetry.ScrapIntoShip(__instance, gObject);
        }
        catch (Exception e)
        {
            Safety.Report("the ship deposit hook", e);
        }
    }

    // Runs right after the game's KillPlayer. For our own player, the game only fills in
    // causeOfDeath later (when the server replies), so we take the cause straight from here.
    [HarmonyPostfix]
    [HarmonyPatch("KillPlayer")]
    private static void KillPlayerPostfix(PlayerControllerB __instance, CauseOfDeath causeOfDeath)
    {
        try
        {
            GameNetworkManager networkManager = GameNetworkManager.Instance;
            if (networkManager == null || __instance != networkManager.localPlayerController || !__instance.isPlayerDead)
            {
                return;
            }

            Symptoms.PhantomHitSymptom.ClearLost();
            Telemetry.LocalDied(causeOfDeath);
        }
        catch (Exception e)
        {
            Safety.Report("the death hook", e);
        }
    }
}
