using System.Collections.Generic;
using GameNetcodeStuff;
using UnityEngine;
using static System.FormattableString;

namespace Sanity.Symptoms;

// An item that isn't there when you reach it. Armed by the director; it fires the next time an item on the ground is
// both within 1 m of your reach (the game's grab distance, so a longer reach from upgrades counts; the extra metre so
// it's gone before you could grab it) and right where you're looking (a narrow cone around the centre of your view,
// not the screen edge), and wasn't a moment before: you walked up to it while looking at it, or you were already in
// reach and turned to look at it. Inside the facility or outside, never in the ship. At that moment it's gone. Gone
// means gone, on your screen: not drawn, its lights off, its sounds muted, nothing to grab (its colliders are off)
// and nothing to scan (its scan node is off). Scrap and tools alike. It comes back when:
//   - you step back out of reach (1 m past it, so the edge doesn't flicker), or
//   - you look away (it's fully off screen for 0.3 s): it's back when you look again, wherever you are, or
//   - anyone picks it up or it moves (a teammate, a monster): it shows wherever it really is, or
//   - you die, leave, or go into the ship, or after 3 minutes whatever happens.
// Nothing is sent: everyone else still sees it where it is.
// Never: the docked apparatus, a bee hive, bodies, anything held or still falling.
internal sealed class VanishingItemSymptom : ArmedSymptom
{
    private const float LookCone = 12f;            // degrees from the centre of your view
    private const float LookAhead = 6f;            // items this far beyond your reach are watched for the approach
    internal const float Lead = 1f;                 // it goes this far before your reach, so you can't grab it first
    private const float ComeBackSlack = 1f;        // back once you're this far past where it went
    private const float OffScreenSeconds = 0.3f;
    private const float ScreenMargin = 0.1f;       // "fully off screen" allows for the item's size
    private const float MovedDistance = 0.3f;
    private const float MaxSeconds = 180f;
    private const float ScanSeconds = 0.1f;
    private const float RecheckSeconds = 0.5f;     // re-hide parts the item's own code switched back on
    private const int ItemLayers = 64 | 512;       // the game's prop and interactable layers (items live there)

    private static readonly Collider[] Nearby = new Collider[48];

    // Watching for the moment: what you looked at on the last scan, and whether it was in reach then. The first scan
    // after a pause (just armed, or held back) only sets this, so an item you were already looking at doesn't count.
    private GrabbableObject? lastLooked;
    private bool lastInReach;
    private float lastScanAt = -9999f;
    private float nextScan;

    // Telemetry while it's gone: did you try to grab or scan where it was.
    private int grabTries;
    private int scans;
    private string how = "";

    // The hidden item, and everything switched off (to switch exactly those back on).
    private bool hiding;
    private GrabbableObject? hidden;
    private Vector3 hiddenAt;
    private Bounds hiddenBounds;
    private float hiddenSince;
    private float offScreenFor;
    private float nextRecheck;
    private readonly List<Renderer> renderers = new List<Renderer>();
    private readonly List<Light> lights = new List<Light>();
    private readonly List<Collider> colliders = new List<Collider>();
    private readonly List<AudioSource> muted = new List<AudioSource>();
    private GameObject? scanNode;
    private int reshown;           // times the item's own code (or a mod) switched a hidden part back on

    internal override string Name => "Vanishing item";

    internal override string ConfigKey => "VanishingItem";

    internal override string Family => "sight";

    internal override SanityTier MinTier => SanityTier.Uneasy;

    internal override float BaseWeight => 1.6f;

    // A bit less at Uneasy (the same as the flicker, the other armed sight symptom there), so it doesn't push out the
    // sounds that make up most of Uneasy.
    internal override float WeightAt(SanityTier tier) => tier == SanityTier.Uneasy ? 1f : BaseWeight;

    internal override float RecoverySeconds => 240f;

    // Waits up to 5 min.
    protected override float ExpireSeconds => 300f;

    internal override string Waits => "an item in reach you look at";

    protected override bool Showing => hiding;

    protected override string CantArmReason(PlayerControllerB player)
    {
        if (StartOfRound.Instance == null)
        {
            return "no round";
        }
        if (player.isInHangarShipRoom)
        {
            return "in ship";
        }
        return "";
    }

    protected override void OnArmed()
    {
        lastLooked = null;
        lastInReach = false;
        lastScanAt = -9999f;
    }

    internal override string MomentStatus(PlayerControllerB player)
    {
        if (hiding)
        {
            return "an item is gone right now";
        }
        GrabbableObject? looked = LookedAtItem(player, out float reach, out _);
        if (looked == null)
        {
            return "no item on the ground where you look";
        }
        string name = looked.itemProperties != null ? looked.itemProperties.itemName : looked.name;
        return Invariant($"looking at {name} {reach:F1} m (fires at {player.grabDistance + Lead:F1} m, walking up or turning to it)");
    }

    protected override bool TryFire(PlayerControllerB player, SanityTier tier)
    {
        if (player.isInHangarShipRoom || player.isPlayerDead || Time.time < nextScan)
        {
            return false;
        }
        bool baseline = Time.time - lastScanAt > 0.5f;
        lastScanAt = Time.time;
        nextScan = Time.time + ScanSeconds;

        float reach = player.grabDistance + Lead;
        GrabbableObject? looked = LookedAtItem(player, out float reachDistance, out Bounds bounds);
        bool inReach = looked != null && reachDistance <= reach;
        bool walkedUp = looked != null && looked == lastLooked && !lastInReach;
        bool already = looked != null && looked == lastLooked && lastInReach;
        lastLooked = looked;
        lastInReach = inReach;

        // Fires the moment an item is both in reach and looked at, and wasn't on the last scan.
        if (baseline || looked == null || !inReach || already)
        {
            return false;
        }

        how = walkedUp ? "walked up to it" : "looked at it in reach";
        Hide(looked, bounds);
        Vector3 away = PhantomPlacement.Flat(bounds.center - player.transform.position);
        Chains.Trigger(this, "vanished", bounds.center + away * 4f, away);
        lastLooked = null;
        string what = looked.itemProperties != null ? looked.itemProperties.itemName : looked.gameObject.name;
        Telemetry.Event("VANISH", looked.scrapValue, Invariant(
            $"{what} value {looked.scrapValue} at {reachDistance:F1}m (reach {player.grabDistance:F1}m + {Lead:F0}m) {how} {(player.isInsideFactory ? "inside" : "outside")}, {OthersNear(looked, bounds.center)} other items within 2.5 m"));
        return true;
    }

    protected override void WatchAlways(PlayerControllerB player)
    {
        if (!Showing)
        {
            return;
        }

        GrabbableObject? item = hidden;
        string ended = "";
        if (item == null)
        {
            ended = "item gone";
        }
        else if (player.isPlayerDead)
        {
            ended = "died";
        }
        else if (!SanityMeter.OnMoon)
        {
            ended = "left the moon";
        }
        else if (player.isInHangarShipRoom)
        {
            ended = "entered the ship";
        }
        else if (item.isHeld || item.isPocketed || item.playerHeldBy != null || item.isHeldByEnemy)
        {
            ended = "picked up by someone";
        }
        else if ((item.transform.position - hiddenAt).sqrMagnitude > MovedDistance * MovedDistance)
        {
            ended = "moved";
        }
        else if (Time.time - hiddenSince > MaxSeconds)
        {
            ended = "timed out";
        }
        else
        {
            Transform eye = player.gameplayCamera.transform;
            float distance = Vector3.Distance(eye.position, hiddenBounds.ClosestPoint(eye.position));
            if (distance > player.grabDistance + Lead + ComeBackSlack)
            {
                ended = Invariant($"walked away ({distance:F1}m)");
            }
            else
            {
                bool onScreen = OnScreen(player, hiddenBounds.center);
                offScreenFor = onScreen ? 0f : offScreenFor + Time.deltaTime;
                if (offScreenFor >= OffScreenSeconds)
                {
                    ended = "looked away";
                }
                else if (onScreen && distance <= player.grabDistance + Lead)
                {
                    // Telemetry: reaching for it, or scanning for it, while it's gone.
                    if (InputWatch.Pressed("Interact"))
                    {
                        grabTries++;
                    }
                    if (InputWatch.Pressed("PingScan"))
                    {
                        scans++;
                    }
                }
            }
        }

        if (ended.Length > 0)
        {
            Restore(ended);
            return;
        }

        // Some items switch their own parts back on (a light, a screen): keep them off, and catch new ones now and
        // then.
        foreach (Renderer renderer in renderers)
        {
            if (renderer != null && renderer.enabled)
            {
                renderer.enabled = false;
                reshown++;
            }
        }
        foreach (Light light in lights)
        {
            if (light != null && light.enabled)
            {
                light.enabled = false;
                reshown++;
            }
        }
        if (item != null && Time.time >= nextRecheck)
        {
            nextRecheck = Time.time + RecheckSeconds;
            HideParts(item);
        }
    }

    protected override void StopShowing(string why)
    {
        if (Showing)
        {
            Restore(why);
        }
    }

    protected override void ResetDayState()
    {
        lastLooked = null;
    }

    // ---- Which item ----

    // The item on the ground you're looking right at (closest to the centre of your view, in clear sight), with its
    // distance from your eyes to its nearest point (what your reach is measured against) and its size.
    internal static GrabbableObject? LookedAtItem(PlayerControllerB player, out float reachDistance, out Bounds bounds)
    {
        reachDistance = 0f;
        bounds = default;
        StartOfRound startOfRound = StartOfRound.Instance;
        Transform eye = player.gameplayCamera.transform;
        int count = Physics.OverlapSphereNonAlloc(eye.position, player.grabDistance + LookAhead, Nearby, ItemLayers,
                                                  QueryTriggerInteraction.Collide);
        GrabbableObject? best = null;
        float bestAngle = LookCone;
        for (int i = 0; i < count; i++)
        {
            Collider collider = Nearby[i];
            if (collider == null || !collider.CompareTag("PhysicsProp"))
            {
                continue;
            }
            GrabbableObject item = collider.GetComponent<GrabbableObject>();
            if (item == null)
            {
                item = collider.GetComponentInParent<GrabbableObject>();
            }
            if (item == null || item == best || !Eligible(item))
            {
                continue;
            }

            Bounds itemBounds = collider.bounds;
            float angle = Vector3.Angle(eye.forward, itemBounds.center - eye.position);
            if (angle > bestAngle)
            {
                continue;
            }
            if (startOfRound != null
                && Physics.Linecast(eye.position, itemBounds.center, startOfRound.collidersAndRoomMask, QueryTriggerInteraction.Ignore))
            {
                continue;   // behind a wall
            }

            best = item;
            bestAngle = angle;
            reachDistance = Vector3.Distance(eye.position, collider.ClosestPoint(eye.position));
            bounds = itemBounds;
        }
        return best;
    }

    // Telemetry: other items on the ground within 2.5 m of the one that went.
    private static readonly Collider[] Around = new Collider[48];

    private static int OthersNear(GrabbableObject item, Vector3 at)
    {
        int count = Physics.OverlapSphereNonAlloc(at, 2.5f, Around, ItemLayers, QueryTriggerInteraction.Collide);
        HashSet<GrabbableObject> found = new HashSet<GrabbableObject>();
        for (int i = 0; i < count; i++)
        {
            Collider collider = Around[i];
            GrabbableObject other = collider != null && collider.CompareTag("PhysicsProp") ? collider.GetComponentInParent<GrabbableObject>() : null!;
            if (other != null && other != item && Eligible(other))
            {
                found.Add(other);
            }
        }
        return found.Count;
    }

    // An item lying on the ground (or on a shelf) that can vanish believably.
    private static bool Eligible(GrabbableObject item)
    {
        if (!item.gameObject.activeInHierarchy || !item.grabbable || item.deactivated || item.isHeld || item.isPocketed
            || item.playerHeldBy != null || item.isHeldByEnemy || item.isInShipRoom || item.isInElevator
            || item.fallTime < 1f || item is RagdollGrabbableObject)
        {
            return false;
        }
        if (item is LungProp apparatus && apparatus.isLungDocked)
        {
            return false;
        }
        return !IsBeeHive(item);
    }

    private static bool IsBeeHive(GrabbableObject item)
    {
        RoundManager roundManager = RoundManager.Instance;
        if (roundManager == null || roundManager.SpawnedEnemies == null)
        {
            return false;
        }
        foreach (EnemyAI enemy in roundManager.SpawnedEnemies)
        {
            if (enemy is RedLocustBees bees && bees.hive == item)
            {
                return true;
            }
        }
        return false;
    }

    private static bool OnScreen(PlayerControllerB player, Vector3 point)
    {
        Vector3 view = player.gameplayCamera.WorldToViewportPoint(point);
        return view.z > 0f && view.x > -ScreenMargin && view.x < 1f + ScreenMargin
               && view.y > -ScreenMargin && view.y < 1f + ScreenMargin;
    }

    // ---- Hiding and restoring ----

    private void Hide(GrabbableObject item, Bounds bounds)
    {
        hiding = true;
        hidden = item;
        grabTries = 0;
        scans = 0;
        hiddenAt = item.transform.position;
        hiddenBounds = bounds;
        hiddenSince = Time.time;
        offScreenFor = 0f;
        nextRecheck = Time.time + RecheckSeconds;
        reshown = 0;
        HideParts(item);

        // The scan node: its object off (the game's scan and scan mods only see active nodes). If it sits on the item
        // itself, only its collider goes (switching off the item's own object would stop the item's code).
        ScanNodeProperties node = item.GetComponentInChildren<ScanNodeProperties>();
        if (node != null && node.gameObject != item.gameObject && node.gameObject.activeSelf)
        {
            scanNode = node.gameObject;
            scanNode.SetActive(false);
        }
    }

    // Switch off every part of the item that shows, sounds or can be touched (and remember which).
    private void HideParts(GrabbableObject item)
    {
        foreach (Renderer renderer in item.GetComponentsInChildren<Renderer>())
        {
            if (renderer.enabled && !renderers.Contains(renderer))
            {
                renderer.enabled = false;
                renderers.Add(renderer);
            }
        }
        foreach (Light light in item.GetComponentsInChildren<Light>())
        {
            if (light.enabled && !lights.Contains(light))
            {
                light.enabled = false;
                lights.Add(light);
            }
        }
        foreach (Collider collider in item.GetComponentsInChildren<Collider>())
        {
            if (collider.enabled && !colliders.Contains(collider))
            {
                collider.enabled = false;
                colliders.Add(collider);
            }
        }
        foreach (AudioSource source in item.GetComponentsInChildren<AudioSource>())
        {
            if (!source.mute && !muted.Contains(source))
            {
                source.mute = true;
                muted.Add(source);
            }
        }
    }

    private void Restore(string ended)
    {
        foreach (Renderer renderer in renderers)
        {
            if (renderer != null)
            {
                renderer.enabled = true;
            }
        }
        foreach (Light light in lights)
        {
            if (light != null)
            {
                light.enabled = true;
            }
        }
        foreach (Collider collider in colliders)
        {
            if (collider != null)
            {
                collider.enabled = true;
            }
        }
        foreach (AudioSource source in muted)
        {
            if (source != null)
            {
                source.mute = false;
            }
        }
        if (scanNode != null)
        {
            scanNode.SetActive(true);
        }

        string partsHidden = Invariant($"{renderers.Count} renderers {lights.Count} lights {colliders.Count} colliders {muted.Count} sounds{(scanNode != null ? " + scan node" : "")}");
        renderers.Clear();
        lights.Clear();
        colliders.Clear();
        muted.Clear();
        scanNode = null;
        hidden = null;
        hiding = false;
        float lasted = Time.time - hiddenSince;
        Telemetry.Event("VANISH_END", lasted, Invariant($"{ended} after {lasted:F1}s, grab pressed {grabTries}x, scanned {scans}x, hid {partsHidden}, parts switched back on by the item {reshown}x"));
    }
}
