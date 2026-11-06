using System.Collections.Generic;
using GameNetcodeStuff;
using UnityEngine;
using static System.FormattableString;

namespace Sanity;

// Bringing fresh loot to the ship restores sanity, sized by how much of today's loot it is for one player.
//
//   full reward = Pool x item value / fair share      (fair share = today's loot / players at landing)
//                 clamped to MinPerItem..MaxPerItem
//   your share  = Finder 65% (the first player to ever pick it up) + haulers 35% (everyone else who carried it,
//                 split equally). No haulers: the finder gets it all. Dead or gone players' parts go to the
//                 living ones in proportion.
//   paid when   = the item is let go inside the ship (dropped, placed or thrown in; not while still carried).
//
// Every player's game sees every player's held items (the game syncs them), so each game records the same finder
// and haulers by itself and pays only its own player. No networking. After DailySoftLimit sanity from loot in a
// day, further loot pays half. Loot isn't stopped by the ship's cap: it's the way back to 100.
//
// Today's loot: a few seconds after landing, the host's game sends everyone the value of every scrap item it spawned
// (the game's own SyncScrapValuesClientRpc). Until that message arrives, scrap on everyone else's machine is worth 0,
// so counting the scene ourselves would give non-host players a tiny fair share. So the count comes from that
// message's own list of values: the same numbers on every machine. If it never arrives (it always should), we count
// the scene ourselves FallbackDelay seconds after landing. Loot let go in the ship before the count is paid once
// the count exists.
internal static class LootRecovery
{
    private const float Pool = 50f;               // sanity for delivering your whole fair share of the day's loot
    private const float MinPerItem = 1f;          // outer limits on one item's full reward
    private const float MaxPerItem = 15f;
    private const float FinderShare = 0.65f;      // the rest is split between the haulers
    private const float DailySoftLimit = 30f;     // after this much from loot in a day, loot pays half
    private const float FallbackDelay = 20f;      // no value message by this long after landing: count the scene
    private const float StaleSeconds = 300f;      // a value message older than this at landing is from another round
    private const float PollSeconds = 0.2f;

    // Today's loot. Zero = not counted yet (or nothing to count): deliveries wait for the count.
    internal static int LevelTotal { get; private set; }
    internal static int LevelItems { get; private set; }
    internal static int PlayersAtLanding { get; private set; } = 1;
    internal static float GainedToday { get; private set; }
    internal static float FairShare => LevelTotal > 0 ? (float)LevelTotal / PlayersAtLanding : 0f;

    // Where today's count came from: "sync" (the game's value message), "fallback" (our own scene count) or "" (none
    // yet).
    internal static string CountSource { get; private set; } = "";

    internal static string TuningSummary =>
        Invariant($"pool {Pool} per item {MinPerItem}-{MaxPerItem} finder {FinderShare} soft limit {DailySoftLimit} count from scrap sync (fallback {FallbackDelay}s)");

    private sealed class Haul
    {
        internal PlayerControllerB Finder = null!;
        internal readonly List<PlayerControllerB> Haulers = new List<PlayerControllerB>();
    }

    // Loot let go in the ship before today's loot was counted: what this player's share was at that moment.
    private sealed class EarlyDelivery
    {
        internal string Name = "";
        internal int Value;
        internal float Share;
        internal string Role = "";
    }

    private static readonly Dictionary<GrabbableObject, Haul> hauls = new Dictionary<GrabbableObject, Haul>();
    private static readonly List<GrabbableObject> arrived = new List<GrabbableObject>();
    private static readonly List<EarlyDelivery> early = new List<EarlyDelivery>();
    private static bool onMoon;
    private static float secondsOnMoon;
    private static float dayStartedAt;            // Time.time when this machine's day started
    private static float pollTimer;

    // The game's value message, kept here if it arrives before this machine's day has started.
    private static int[]? syncedValues;
    private static float syncedAt;                // Time.time when it arrived
    private static bool syncApplied;              // today's count already came from the message

    // Called once per frame for the local player, right after the sanity meter.
    internal static void Tick(PlayerControllerB me)
    {
        StartOfRound startOfRound = StartOfRound.Instance;
        if (!SanityMeter.OnMoon || startOfRound == null)
        {
            if (onMoon)
            {
                ResetDay();
            }
            return;
        }

        if (!onMoon)
        {
            // First frame of the day.
            onMoon = true;
            dayStartedAt = Time.time;
        }

        secondsOnMoon += Time.deltaTime;

        // The value message came before the day started on this machine (slow loading): use it now.
        if (!syncApplied && syncedValues != null)
        {
            if (Time.time - syncedAt <= StaleSeconds)
            {
                ApplySync(startOfRound);
            }
            else
            {
                syncedValues = null;
            }
        }

        // Safety net: no value message at all. Count the scene ourselves (by now values have normally arrived).
        if (CountSource.Length == 0 && secondsOnMoon >= FallbackDelay)
        {
            CountScene(startOfRound, out int items, out int total);
            SetCount(startOfRound, total, items, "fallback");
        }

        pollTimer -= Time.deltaTime;
        if (pollTimer > 0f)
        {
            return;
        }
        pollTimer = PollSeconds;

        TrackHolders(startOfRound);
        CheckArrivals(startOfRound, me);
    }

    // Called by the RoundManager patch each time the game's scrap value message runs on this machine.
    // The host runs it twice (once sending it, once receiving its own copy), so after the first copy has been used,
    // the rest of the day ignores it. The values are the message's own list, identical on every machine.
    internal static void OnScrapValuesSynced(int[]? values)
    {
        if (values == null || syncApplied)
        {
            return;
        }

        syncedValues = values;
        syncedAt = Time.time;

        StartOfRound startOfRound = StartOfRound.Instance;
        if (onMoon && startOfRound != null)
        {
            ApplySync(startOfRound);
        }
    }

    // Use the value message as today's loot. Also logs a scene count of the same moment, for comparison: scrap the
    // game didn't spawn through its normal scrap spawner (some moons' own scripts) is in the scene but not in the list.
    private static void ApplySync(StartOfRound startOfRound)
    {
        int[] values = syncedValues!;
        syncedValues = null;
        syncApplied = true;

        int total = 0;
        foreach (int value in values)
        {
            total += value;
        }

        CountScene(startOfRound, out int sceneItems, out int sceneTotal);
        float relative = syncedAt - dayStartedAt;   // negative: it arrived before this machine's day started
        Telemetry.Event("SCRAP_SYNC", total, Invariant(
            $"items {values.Length} total {total} at {relative:F1}s after landing (scene now {sceneItems} items worth {sceneTotal}) replaces {(CountSource.Length > 0 ? CountSource : "nothing")}"));

        SetCount(startOfRound, total, values.Length, "sync");
    }

    // Today's loot is known: record it, and pay anything that was let go in the ship before this moment.
    private static void SetCount(StartOfRound startOfRound, int total, int items, string source)
    {
        LevelTotal = total;
        LevelItems = items;
        CountSource = source;
        PlayersAtLanding = startOfRound.connectedPlayersAmount + 1;
        float average = items > 0 ? (float)total / items : 0f;
        Telemetry.Event("LEVEL_SCRAP", total, Invariant(
            $"total {total} items {items} average {average:F0} players {PlayersAtLanding} fair share {FairShare:F0} source {source}"));

        if (early.Count == 0 || FairShare <= 0f)
        {
            return;
        }

        GameNetworkManager networkManager = GameNetworkManager.Instance;
        PlayerControllerB? me = networkManager != null ? networkManager.localPlayerController : null;
        if (me != null && Alive(me))
        {
            foreach (EarlyDelivery delivery in early)
            {
                PayOut(delivery.Name, delivery.Value, delivery.Share, delivery.Role, " (let go before the count)");
            }
        }
        early.Clear();
    }

    // Who is holding what: the first holder of a fresh item is its finder, anyone after is a hauler.
    private static void TrackHolders(StartOfRound startOfRound)
    {
        PlayerControllerB[] players = startOfRound.allPlayerScripts;
        if (players == null)
        {
            return;
        }

        foreach (PlayerControllerB player in players)
        {
            if (player == null || !player.isPlayerControlled || player.isPlayerDead || player.ItemSlots == null)
            {
                continue;
            }

            foreach (GrabbableObject item in player.ItemSlots)
            {
                if (item == null)
                {
                    continue;
                }

                if (hauls.TryGetValue(item, out Haul? haul))
                {
                    if (player != haul.Finder && !haul.Haulers.Contains(player))
                    {
                        haul.Haulers.Add(player);
                    }
                }
                else if (IsFresh(item, startOfRound))
                {
                    hauls[item] = new Haul { Finder = player };
                }
            }
        }
    }

    // Tracked items that were let go inside the ship: pay out, then forget them. Carrying loot into the ship
    // doesn't count yet; dropping it there does.
    private static void CheckArrivals(StartOfRound startOfRound, PlayerControllerB me)
    {
        arrived.Clear();
        foreach (KeyValuePair<GrabbableObject, Haul> entry in hauls)
        {
            GrabbableObject item = entry.Key;
            if (item == null || (!item.isHeld && !item.isPocketed && (item.isInShipRoom || InShip(item, startOfRound))))
            {
                arrived.Add(item!);
            }
        }

        foreach (GrabbableObject item in arrived)
        {
            if (item != null && hauls.TryGetValue(item, out Haul? haul))
            {
                Pay(item, haul, me);
            }
            hauls.Remove(item!);
        }
    }

    private static void Pay(GrabbableObject item, Haul haul, PlayerControllerB me)
    {
        // This player's share among the living people involved.
        float finderWeight = haul.Haulers.Count > 0 ? FinderShare : 1f;
        float haulerWeight = haul.Haulers.Count > 0 ? (1f - FinderShare) / haul.Haulers.Count : 0f;
        float living = Alive(haul.Finder) ? finderWeight : 0f;
        foreach (PlayerControllerB hauler in haul.Haulers)
        {
            living += Alive(hauler) ? haulerWeight : 0f;
        }

        float mine = 0f;
        string role = "";
        if (me == haul.Finder && Alive(me))
        {
            mine += finderWeight;
            role = "finder";
        }
        if (haul.Haulers.Contains(me) && Alive(me))
        {
            mine += haulerWeight;
            role = role.Length > 0 ? "finder+hauler" : "hauler";
        }

        if (mine <= 0f || living <= 0f || !Plugin.ModEnabled.Value)
        {
            return;
        }

        float share = mine / living;
        string name = item.itemProperties != null ? item.itemProperties.itemName : "item";

        // Today's loot isn't counted yet: remember this player's share as it is now, and pay it once the count exists.
        if (FairShare <= 0f)
        {
            early.Add(new EarlyDelivery { Name = name, Value = item.scrapValue, Share = share, Role = role });
            Telemetry.Event("LOOT_QUEUED", item.scrapValue, Invariant(
                $"{name} value {item.scrapValue} share {share * 100f:F0}% ({role}): today's loot not counted yet, paid once it is"));
            return;
        }

        PayOut(name, item.scrapValue, share, role, "");
    }

    // The payout itself: full reward from the item's value, this player's share of it, then the daily soft limit and
    // the dial.
    private static void PayOut(string name, int value, float share, string role, string note)
    {
        float full = Mathf.Clamp(Pool * value / FairShare, MinPerItem, MaxPerItem);
        float amount = full * share;

        // Soft daily limit: past it, loot pays half.
        float beforeLimit = Mathf.Max(0f, DailySoftLimit - GainedToday);
        amount = amount <= beforeLimit ? amount : beforeLimit + (amount - beforeLimit) * 0.5f;
        amount *= Plugin.RecoveryMultiplier.Value;

        float gained = SanityMeter.LootGain(amount);
        GainedToday += gained;
        Telemetry.Event("LOOT_RECOVERY", gained, Invariant(
            $"{name} value {value} fair share {FairShare:F0} full {full:F1} share {share * 100f:F0}% ({role}) day total {GainedToday:F1}{note}"));
    }

    // Our own count of the fresh scrap in the scene right now (scrap that has a value on this machine).
    private static void CountScene(StartOfRound startOfRound, out int items, out int total)
    {
        items = 0;
        total = 0;
        foreach (GrabbableObject item in Object.FindObjectsOfType<GrabbableObject>())
        {
            if (IsFresh(item, startOfRound))
            {
                items++;
                total += item.scrapValue;
            }
        }
    }

    // Fresh loot: scrap worth something, not kept from earlier days, not already collected or in the ship.
    private static bool IsFresh(GrabbableObject item, StartOfRound startOfRound)
    {
        if (item == null || item.itemProperties == null || !item.itemProperties.isScrap || item.scrapValue <= 0
            || item.scrapPersistedThroughRounds || item.isInShipRoom || InShip(item, startOfRound))
        {
            return false;
        }

        RoundManager roundManager = RoundManager.Instance;
        return roundManager == null || !roundManager.scrapDroppedInShip.Contains(item);
    }

    private static bool InShip(GrabbableObject item, StartOfRound startOfRound)
    {
        Collider room = startOfRound.shipInnerRoomBounds;
        return room != null && room.bounds.Contains(item.transform.position);
    }

    private static bool Alive(PlayerControllerB player)
    {
        return player != null && player.isPlayerControlled && !player.isPlayerDead;
    }

    private static void ResetDay()
    {
        onMoon = false;
        secondsOnMoon = 0f;
        dayStartedAt = 0f;
        pollTimer = 0f;
        LevelTotal = 0;
        LevelItems = 0;
        PlayersAtLanding = 1;
        GainedToday = 0f;
        CountSource = "";
        syncedValues = null;
        syncApplied = false;
        hauls.Clear();
        early.Clear();
    }
}
