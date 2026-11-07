using System.Collections.Generic;
using GameNetcodeStuff;
using UnityEngine;
using static System.FormattableString;

namespace Sanity.Symptoms;

// A door that's locked, once. Armed by the director; it fires the first time you look at a closed, unlocked door
// (inside the facility, Uneasy or lower), and that door was ALREADY locked when your cursor reached it.
//
// Pre-locking: while it's armed and free to fire, every eligible closed door around you (within 8 m, or your reach
// + 4 m) reads as locked ahead of time, so the first thing you see is "Locked", never an "Open" that turns "Locked"
// while you look. The first one you look at (or bump into) is the one; every other door goes back to normal at once.
// A door you saw unlocked in the last 10 s is never pre-locked (no flip in front of you), and nothing is pre-locked
// while it's held back (tier, the 5 s gap, a relax window), fear is high or a monster is near.
//
// Showing: its triggers get what the game's own LockDoor gives a locked door, and the door reads as locked inside the
// game's own door code (its Update, its lock-interaction and touch handlers; patched in DoorPatch). So the game and
// every mod draw and handle it exactly like a real locked door: the disabled icon and "Locked" / "Use key" text
// (GeneralImprovements' inventory keys included), and with Lategame Upgrades' Locksmith its "Lockpick" prompt and
// minigame. Outside those calls the door is not locked, so nothing else (enemies, the host's own logic, other players)
// is affected. Look away (cursor off the door for 0.2 s) and it's an ordinary door again.
//
// A key used on it is gone from your inventory (whatever slot it was in, also with mods that let keys be reused), with
// the key's own drop sound played loud, as if you dropped it; the door stays unlocked. The key is removed through the
// game's own item removal, so it's really gone for everyone: the one real effect of this symptom. Pick it with
// Locksmith and it "unlocks" (the unlock sound, on your machine only).
//
// Never while fear is high or a monster is within 10 m (a door that won't open mid-chase could get you killed), never
// on a door you opened or closed in the last 30 s, never on a haunted door (the game's own door haunting runs in the
// code we'd make read as locked), never on a door that was really locked earlier today (unlocked by anyone: a key, a
// lockpicker, Locksmith).
//
// Double doors are one group, found in both directions (a half that names the other as its twin, or is named by it):
// every half locks, the cursor can move between them, and a key, a pick or looking away from all of them ends it for
// all of them.
internal sealed class FakeLockedDoorSymptom : ArmedSymptom
{
    private const float HighFear = 0.4f;               // the game's own "high fear" line
    private const float MonsterClearRadius = 10f;
    private const float RecentUseSeconds = 30f;        // closed and untouched for 30 s
    private const float PreLockRadius = 8f;            // doors this close (or your reach + 4 m) read as locked ahead
    private const float PreLockRefreshSeconds = 0.1f;
    private const float SeenNormalSeconds = 10f;       // a door you saw unlocked this recently is never pre-locked
    private const float LookAwaySeconds = 0.2f;        // cursor off the door this long: back to normal
    private const float KeyReach = 3f;                 // the game's key reach and layers (KeyItem.ItemActivate)
    private const int KeyLayers = 2816;
    private const int KeyItemId = 14;                  // the game's own check for "holding a key"
    private const float LockedHoldSeconds = 30f;       // what the game's LockDoor gives a locked door's trigger
    private const float DropLoudness = 1.5f;           // the 2D copy of the drop sound (a 3D copy plays at your feet)
    private const float DoorListSeconds = 20f;         // how often the level's door list is refreshed
    private const float AfterSeconds = 60f;            // telemetry: did you open that door within a minute after?

    // Doors this player opened or closed recently (DoorLock.OpenOrCloseDoor, patched): a door you just used can't be
    // locked now.
    private static readonly Dictionary<DoorLock, float> LastUsed = new Dictionary<DoorLock, float>();

    // Doors that were really locked earlier today (DoorLock.UnlockDoor, patched: it runs on every machine).
    private static readonly HashSet<DoorLock> WasLocked = new HashSet<DoorLock>();

    // The doors showing as locked (one, or both halves of a double door), and the last such group for telemetry.
    private static readonly List<DoorLock> ShownGroup = new List<DoorLock>();
    private static readonly List<DoorLock> LastGroup = new List<DoorLock>();
    private static readonly List<DoorLock> Group = new List<DoorLock>();

    // A trigger we changed (its door, and everything it was before).
    private struct SavedTrigger
    {
        internal DoorLock Door;
        internal InteractTrigger Trigger;
        internal bool Interactable;
        internal string DisabledTip;
        internal string HoverTip;
        internal string HoldTip;
        internal float TimeToHold;
    }

    // Doors reading as locked ahead of your look (not fired yet), when you last saw each door unlocked, and a
    // pre-locked door you bumped into (it fires on the next frame).
    private static readonly HashSet<DoorLock> PreLocked = new HashSet<DoorLock>();
    private static readonly List<DoorLock> PreLockedScratch = new List<DoorLock>();
    private static readonly Dictionary<DoorLock, float> SeenNormal = new Dictionary<DoorLock, float>();
    private static float nextPreLockRefresh;
    private static DoorLock? touchedPreLocked;

    // The door showing as locked, and every trigger we changed (pre-locked and shown).
    private static DoorLock? shownDoor;
    private static readonly List<SavedTrigger> Changed = new List<SavedTrigger>();
    private static float shownSince;
    private static float cursorOffSince = -1f;
    private static int tries;
    private static bool pickable;     // telemetry: the door's own code made it usable (a lock-picking mod like Locksmith)

    // For telemetry: the last fake door, to see whether you opened it right after.
    private static DoorLock? lastDoor;
    private static float lastEnded = -9999f;

    // Near misses are counted once per hovered trigger, not every frame.
    private static InteractTrigger? lastMissTrigger;

    // The level's doors, found once in a while.
    private static DoorLock[] doors = System.Array.Empty<DoorLock>();
    private static float doorsFoundAt = -9999f;

    internal override string Name => "Locked door";

    internal override string ConfigKey => "FakeLockedDoor";

    internal override string Family => "sight";

    internal override SanityTier MinTier => SanityTier.Uneasy;

    internal override float BaseWeight => 1.6f;

    // A bit less at Uneasy (the same as the flicker, the other armed sight symptom there), so it doesn't push out the
    // sounds that make up most of Uneasy.
    internal override float WeightAt(SanityTier tier) => tier == SanityTier.Uneasy ? 1f : BaseWeight;

    internal override float RecoverySeconds => 300f;

    // Waits up to 6 min (shorter waits mostly ran out on open doors).
    protected override float ExpireSeconds => 360f;

    internal override string Waits => "a closed door you look at";

    protected override bool Showing => ShownGroup.Count > 0;

    protected override string CantArmReason(PlayerControllerB player)
    {
        if (StartOfRound.Instance == null || RoundManager.Instance == null)
        {
            return "no round";
        }
        if (!player.isInsideFactory)
        {
            return "not inside";
        }
        return "";
    }

    internal override string MomentStatus(PlayerControllerB player)
    {
        return ShownGroup.Count > 0 ? "showing a locked door"
            : PreLocked.Count > 0 ? Invariant($"{PreLocked.Count} doors near you read as locked: look at one")
            : !player.isInsideFactory ? "not inside"
            : "no door pre-locked (none eligible within 8 m, or held back)";
    }

    // Fires when the door you look at (or bumped into) is one of the pre-locked ones: it read as locked from the first
    // frame. Any other door you look at is a near miss, with why it wasn't pre-locked.
    protected override bool TryFire(PlayerControllerB player, SanityTier tier)
    {
        StartOfRound startOfRound = StartOfRound.Instance;
        InteractTrigger? trigger = player.hoveringOverTrigger;
        DoorLock? touched = touchedPreLocked;
        touchedPreLocked = null;
        if (startOfRound == null || !player.isInsideFactory)
        {
            lastMissTrigger = null;
            return false;
        }

        DoorLock? door = touched != null && PreLocked.Contains(touched) ? touched : trigger != null ? DoorOf(trigger) : null;
        if (door == null)
        {
            lastMissTrigger = null;
            return false;
        }

        if (!PreLocked.Contains(door))
        {
            if (trigger != null && trigger != lastMissTrigger)
            {
                string miss = Assess(player, trigger);
                Miss(miss.Length > 0 ? miss : "seen unlocked");
                lastMissTrigger = trigger;
            }
            return false;
        }

        int around = PreLocked.Count;
        GroupOf(door, Group);
        Show(door, Group, player);
        float distance = Vector3.Distance(player.transform.position, door.transform.position);
        Vector3 through = PhantomPlacement.Flat(door.transform.position - player.transform.position);
        Chains.Trigger(this, "shown", door.transform.position + through * 1.5f, through);
        Telemetry.Event("FAKE_DOOR", distance, Invariant(
            $"{door.gameObject.name} {distance:F1}m shows \"{LockedText(player)}\"{(ShownGroup.Count > 1 ? " (double door)" : "")} holding {HeldName(player)}, {(touched != null ? "bumped into" : "looked at")}, {around} doors were pre-locked"));
        return true;
    }

    protected override void WatchAlways(PlayerControllerB player)
    {
        NoteSeenNormal(player);
        WatchShown(player);
        WatchPreLocked(player);
    }

    // A door you look at that reads unlocked: remember when (it won't be pre-locked for a while).
    private static void NoteSeenNormal(PlayerControllerB player)
    {
        InteractTrigger? trigger = player.hoveringOverTrigger;
        if (trigger == null)
        {
            return;
        }
        DoorLock? door = DoorOf(trigger);
        if (door == null || PreLocked.Contains(door) || ShownGroup.Contains(door))
        {
            return;
        }
        GroupOf(door, Group);
        foreach (DoorLock half in Group)
        {
            SeenNormal[half] = Time.time;
        }
    }

    // Pre-locking: while armed and free to fire, the eligible doors around you read as locked ahead of your look.
    private void WatchPreLocked(PlayerControllerB player)
    {
        StartOfRound startOfRound = StartOfRound.Instance;
        bool allowed = IsArmed && HeldBy.Length == 0 && ShownGroup.Count == 0 && startOfRound != null
            && SanityMeter.OnMoon && !player.isPlayerDead && player.isInsideFactory
            && startOfRound.fearLevel < HighFear && !MonsterNear(player);
        if (!allowed)
        {
            ReleasePreLocked();
            return;
        }
        if (Time.time < nextPreLockRefresh)
        {
            return;
        }
        nextPreLockRefresh = Time.time + PreLockRefreshSeconds;

        float radius = Mathf.Max(PreLockRadius, player.grabDistance + 4f);
        Vector3 here = player.transform.position;
        InteractTrigger? hovered = player.hoveringOverTrigger;
        string text = LockedText(player);
        foreach (DoorLock door in Doors())
        {
            if (door == null)
            {
                continue;
            }
            GroupOf(door, Group);
            bool near = false, eligible = true, hoveredNow = false;
            foreach (DoorLock half in Group)
            {
                near |= half != null && Vector3.Distance(here, half.transform.position) <= radius;
                eligible &= half != null && Lockable(half) && !WasLocked.Contains(half) && !UsedRecently(half)
                            && !half.hauntedDoor && !(SeenNormal.TryGetValue(half, out float seen) && Time.time - seen < SeenNormalSeconds);
                hoveredNow |= half != null && hovered != null && (half.doorTrigger == hovered || half.doorTriggerB == hovered);
            }
            if (PreLocked.Contains(door))
            {
                if (!near || !eligible)
                {
                    foreach (DoorLock half in Group)
                    {
                        Unchange(half);
                    }
                }
            }
            else if (near && eligible && !hoveredNow)
            {
                foreach (DoorLock half in Group)
                {
                    Change(half, half.doorTrigger, text);
                    Change(half, half.doorTriggerB, text);
                    PreLocked.Add(half);
                }
            }
        }
    }

    // Every pre-locked door back to normal (none of them was looked at, so nothing visible changes).
    private static void ReleasePreLocked()
    {
        if (PreLocked.Count == 0)
        {
            return;
        }
        PreLockedScratch.Clear();
        PreLockedScratch.AddRange(PreLocked);
        foreach (DoorLock door in PreLockedScratch)
        {
            Unchange(door);
        }
        PreLocked.Clear();
    }

    private static void WatchShown(PlayerControllerB player)
    {
        if (ShownGroup.Count == 0)
        {
            return;
        }

        bool hovering = IsShownTrigger(player.hoveringOverTrigger);
        string ended = "";
        if (player.isPlayerDead)
        {
            ended = "died";
        }
        else if (!SanityMeter.OnMoon)
        {
            ended = "left the moon";
        }
        else if (!player.isInsideFactory)
        {
            ended = "left the facility";
        }
        else if (shownDoor == null)
        {
            ended = "door gone";
        }
        else if (!GroupLockable())
        {
            ended = "door opened or really locked";
        }
        else if (hovering)
        {
            cursorOffSince = -1f;
            pickable |= Changed.Count > 0 && Changed[0].Trigger != null && Changed[0].Trigger.interactable;
            if (InputWatch.Pressed("Interact"))
            {
                tries++;   // pressed use on a door that won't open
            }
        }
        else if (cursorOffSince < 0f)
        {
            cursorOffSince = Time.time;
        }
        else if (Time.time - cursorOffSince >= LookAwaySeconds)
        {
            ended = "looked away";
        }

        if (ended.Length > 0)
        {
            Restore(ended);
        }
    }

    protected override void StopShowing(string why)
    {
        if (ShownGroup.Count > 0)
        {
            Restore(why);
        }
        ReleasePreLocked();
    }

    protected override void ResetDayState()
    {
        ReleasePreLocked();
        SeenNormal.Clear();
        touchedPreLocked = null;
        LastUsed.Clear();
        WasLocked.Clear();
        LastGroup.Clear();
        doors = System.Array.Empty<DoorLock>();
        doorsFoundAt = -9999f;
        lastDoor = null;
        lastMissTrigger = null;
    }

    // Telemetry (opportunities): would a fake lock fire on this hovered trigger right now, armed or not?
    // "" = yes; "not a door" when it isn't one; otherwise what would stop it (same rules as TryFire).
    private static readonly List<DoorLock> AssessGroup = new List<DoorLock>();

    internal static string Assess(PlayerControllerB player, InteractTrigger trigger)
    {
        StartOfRound startOfRound = StartOfRound.Instance;
        DoorLock? door = trigger != null ? DoorOf(trigger) : null;
        if (door == null || startOfRound == null)
        {
            return "not a door";
        }
        GroupOf(door, AssessGroup);
        bool lockable = true, used = false, wasLocked = false, haunted = false;
        foreach (DoorLock half in AssessGroup)
        {
            lockable &= Lockable(half);
            used |= UsedRecently(half);
            wasLocked |= WasLocked.Contains(half);
            haunted |= half.hauntedDoor;
        }
        return !lockable ? "open or unlockable"
            : wasLocked ? "really locked earlier"
            : used ? "used recently"
            : haunted ? "haunted door"
            : startOfRound.fearLevel >= HighFear ? "fear"
            : MonsterNear(player) ? "monster"
            : "";
    }

    // ---- The door reads as locked inside the game's own door code (DoorPatch) ----

    // DoorLock.Update / OnHoldInteract: true = this door now reads as locked until EndLockedLook.
    internal static bool BeginLockedLook(DoorLock door)
    {
        if (Changed.Count == 0 || door == null || door.isLocked || !(IsShown(door) || PreLocked.Contains(door)))
        {
            return false;
        }
        door.isLocked = true;
        return true;
    }

    // InteractTrigger.OnTriggerEnter (bumping into the door): the door of one of our triggers, made to read as locked.
    // A pre-locked door you bump into is the one (it fires on the next frame).
    internal static DoorLock? BeginLockedLook(InteractTrigger trigger)
    {
        if (Changed.Count == 0 || trigger == null)
        {
            return null;
        }
        foreach (SavedTrigger saved in Changed)
        {
            if (saved.Trigger == trigger && saved.Door != null)
            {
                if (PreLocked.Contains(saved.Door))
                {
                    touchedPreLocked = saved.Door;
                }
                return BeginLockedLook(saved.Door) ? saved.Door : null;
            }
        }
        return null;
    }

    internal static void EndLockedLook(DoorLock door)
    {
        if (door != null)
        {
            door.isLocked = false;
        }
    }

    // DoorLock.UnlockDoorSyncWithServer on the shown door (a lock-picking minigame finished): it "unlocks" on your
    // machine only (the game's unlock sound), and the door is normal again. True = handled (nothing is sent).
    internal static bool TryUnlockShown(DoorLock door)
    {
        bool preLocked = door != null && PreLocked.Contains(door);
        if (door == null || (!IsShown(door) && !preLocked))
        {
            return false;
        }
        if (door.doorLockSFX != null && door.unlockSFX != null)
        {
            door.doorLockSFX.Stop();
            door.doorLockSFX.PlayOneShot(door.unlockSFX);
        }
        if (preLocked)
        {
            GroupOf(door, Group);
            foreach (DoorLock half in Group)
            {
                Unchange(half);
            }
        }
        else
        {
            Restore("picked the lock");
        }
        return true;
    }

    // ---- The key (KeyItem.ItemActivate, patched) ----

    // A key used on the door showing as locked: the key is taken and its drop sound plays. True = handled (the game's
    // own key code is skipped: the door was never locked, so it would do nothing anyway).
    internal static bool TryUseKey(KeyItem key)
    {
        DoorLock? door = shownDoor;
        PlayerControllerB? player = key != null ? key.playerHeldBy : null;
        GameNetworkManager networkManager = GameNetworkManager.Instance;
        if (Changed.Count == 0 || door == null || key == null || player == null || !key.IsOwner || networkManager == null
            || player != networkManager.localPlayerController)
        {
            return false;
        }

        // The same ray the game's key uses.
        Transform eye = player.gameplayCamera.transform;
        if (!Physics.Raycast(new Ray(eye.position, eye.forward), out RaycastHit hit, KeyReach, KeyLayers))
        {
            return false;
        }
        DoorLock? target = hit.transform.GetComponent<DoorLock>();
        if (target == null)
        {
            TriggerPointToDoor pointer = hit.transform.GetComponent<TriggerPointToDoor>();
            target = pointer != null ? pointer.pointToDoor : null;
        }
        if (target == null || !ShownGroup.Contains(target))
        {
            return false;
        }

        AudioClip? drop = key.itemProperties != null ? key.itemProperties.dropSFX : null;
        AudioSource? keyAudio = key.GetComponent<AudioSource>();
        Vector3 feet = player.transform.position;
        string slot = TakeKey(player, key);
        PlayDrop(drop, keyAudio, feet);
        Restore("used a key (" + slot + ")");
        return true;
    }

    // Take the key out of whichever slot it's in, through the game's own removal (so it's gone for everyone).
    private static string TakeKey(PlayerControllerB player, KeyItem key)
    {
        HUDManager hud = HUDManager.Instance;
        if (player.currentlyHeldObjectServer == key)
        {
            player.DespawnHeldObject();   // what the game does when a key is used
            return "held";
        }
        if (player.ItemOnlySlot == key)
        {
            if (hud != null && hud.itemOnlySlotIcon != null)
            {
                hud.itemOnlySlotIcon.enabled = false;
            }
            player.DestroyItemInSlotAndSync(50);
            return "utility slot";
        }
        for (int i = 0; i < player.ItemSlots.Length; i++)
        {
            if (player.ItemSlots[i] != key)
            {
                continue;
            }
            // The game's removal only clears a slot's icon when you're holding something, so clear it here.
            if (hud != null && hud.itemSlotIcons != null && i < hud.itemSlotIcons.Length && hud.itemSlotIcons[i] != null)
            {
                hud.itemSlotIcons[i].enabled = false;
            }
            player.DestroyItemInSlotAndSync(i);
            return "slot " + i;
        }
        return "not in a slot";
    }

    // The key's own drop sound, loud: once in your head (the HUD's 2D source) and once at your feet.
    private static void PlayDrop(AudioClip? drop, AudioSource? keyAudio, Vector3 feet)
    {
        if (drop == null)
        {
            return;
        }
        HUDManager hud = HUDManager.Instance;
        if (hud != null && hud.UIAudio != null)
        {
            hud.UIAudio.PlayOneShot(drop, DropLoudness);
        }
        AudioSource source = PhantomAudio.CreateSource("Sanity.KeyDrop", keyAudio, feet, louder: false);
        source.PlayOneShot(drop, 1f);
        Object.Destroy(source.gameObject, drop.length + 0.5f);
    }

    // ---- Doors this player used (DoorLock.OpenOrCloseDoor, patched) ----

    internal static void OnDoorUsed(DoorLock door)
    {
        if (door == null)
        {
            return;
        }
        LastUsed[door] = Time.time;

        // Telemetry: you opened the door that had looked locked, shortly after.
        if (lastDoor != null && LastGroup.Contains(door) && Time.time - lastEnded <= AfterSeconds)
        {
            float later = Time.time - lastEnded;
            Telemetry.Event("FAKE_DOOR_AFTER", later, Invariant($"opened the door that looked locked {later:F0}s later"));
            lastDoor = null;
            LastGroup.Clear();
        }
    }

    // A door was really unlocked (by anyone): it can't be "locked" again today.
    internal static void OnReallyUnlocked(DoorLock door)
    {
        if (door != null)
        {
            WasLocked.Add(door);
        }
    }

    // ---- Showing and restoring ----

    private static void Show(DoorLock door, List<DoorLock> group, PlayerControllerB player)
    {
        shownDoor = door;
        ShownGroup.Clear();
        ShownGroup.AddRange(group);
        shownSince = Time.time;
        cursorOffSince = -1f;
        tries = 0;
        pickable = false;
        string text = LockedText(player);
        foreach (DoorLock half in ShownGroup)
        {
            PreLocked.Remove(half);
            Change(half, half.doorTrigger, text);    // already changed when pre-locked: kept as is
            Change(half, half.doorTriggerB, text);
        }
        ReleasePreLocked();                          // every other door is normal again
    }

    // What the game's LockDoor does to a locked door's trigger. The door's own Update then fills in the text each
    // frame.
    private static void Change(DoorLock door, InteractTrigger? trigger, string text)
    {
        if (trigger == null || IsChanged(trigger) || !trigger.interactable)
        {
            return;
        }
        Changed.Add(new SavedTrigger
        {
            Door = door,
            Trigger = trigger,
            Interactable = trigger.interactable,
            DisabledTip = trigger.disabledHoverTip,
            HoverTip = trigger.hoverTip,
            HoldTip = trigger.holdTip,
            TimeToHold = trigger.timeToHold,
        });
        trigger.interactable = false;
        trigger.timeToHold = LockedHoldSeconds;
        trigger.hoverTip = "Locked (pickable)";
        trigger.holdTip = "Picking lock";
        trigger.disabledHoverTip = text;
    }

    private static void Restore(string ended)
    {
        DoorLock? door = shownDoor;
        bool reallyLocked = false;
        foreach (DoorLock half in ShownGroup)
        {
            reallyLocked |= half != null && half.isLocked;
        }
        for (int i = Changed.Count - 1; i >= 0; i--)
        {
            SavedTrigger saved = Changed[i];
            if (saved.Door != null && !ShownGroup.Contains(saved.Door))
            {
                continue;   // a pre-locked door: not part of what's shown
            }
            if (saved.Trigger != null && !reallyLocked)
            {
                Put(saved);   // (gone, or something really locked it meanwhile: leave it to the game)
            }
            Changed.RemoveAt(i);
        }
        lastDoor = door;
        LastGroup.Clear();
        LastGroup.AddRange(ShownGroup);
        ShownGroup.Clear();
        lastEnded = Time.time;
        shownDoor = null;
        float lasted = Time.time - shownSince;
        Telemetry.Event("FAKE_DOOR_END", lasted, Invariant($"{ended} after {lasted:F1}s, use pressed {tries}x{(pickable ? ", pickable (lock-picking mod)" : "")}"));
    }

    // One door's changed triggers back to what they were (unless something really locked it meanwhile).
    private static void Unchange(DoorLock door)
    {
        PreLocked.Remove(door);
        for (int i = Changed.Count - 1; i >= 0; i--)
        {
            SavedTrigger saved = Changed[i];
            if (saved.Door != door)
            {
                continue;
            }
            if (saved.Trigger != null && door != null && !door.isLocked)
            {
                Put(saved);
            }
            Changed.RemoveAt(i);
        }
    }

    private static void Put(SavedTrigger saved)
    {
        saved.Trigger.interactable = saved.Interactable;
        saved.Trigger.disabledHoverTip = saved.DisabledTip;
        saved.Trigger.hoverTip = saved.HoverTip;
        saved.Trigger.holdTip = saved.HoldTip;
        saved.Trigger.timeToHold = saved.TimeToHold;
    }

    // ---- Lookups ----

    private static bool IsChanged(InteractTrigger? trigger)
    {
        if (trigger == null)
        {
            return false;
        }
        foreach (SavedTrigger saved in Changed)
        {
            if (saved.Trigger == trigger)
            {
                return true;
            }
        }
        return false;
    }

    // A trigger of a door showing as locked (fired, not just pre-locked).
    private static bool IsShownTrigger(InteractTrigger? trigger)
    {
        if (trigger == null)
        {
            return false;
        }
        foreach (DoorLock half in ShownGroup)
        {
            if (half != null && (half.doorTrigger == trigger || half.doorTriggerB == trigger))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsShown(DoorLock door)
    {
        return ShownGroup.Count > 0 && door != null && ShownGroup.Contains(door);
    }

    // Every half still closed and unlocked.
    private static bool GroupLockable()
    {
        foreach (DoorLock half in ShownGroup)
        {
            if (half == null || !Lockable(half))
            {
                return false;
            }
        }
        return ShownGroup.Count > 0;
    }

    // A door and its other half (either names the other as its twin), into a reused list.
    private static void GroupOf(DoorLock door, List<DoorLock> group)
    {
        group.Clear();
        group.Add(door);
        if (door.twinDoor != null && door.twinDoor != door)
        {
            group.Add(door.twinDoor);
        }
        foreach (DoorLock other in Doors())
        {
            if (other != null && other != door && other.twinDoor == door && !group.Contains(other))
            {
                group.Add(other);
            }
        }
    }

    // The first frame's text, by the game's own rule (the door's Update takes over from the next frame).
    private static string LockedText(PlayerControllerB player)
    {
        GrabbableObject held = player.currentlyHeldObjectServer;
        if (held != null && held.itemProperties != null && held.itemProperties.itemId == KeyItemId)
        {
            StartOfRound startOfRound = StartOfRound.Instance;
            return startOfRound != null && startOfRound.localPlayerUsingController ? "Use key: [R-trigger]" : "Use key: [ LMB ]";
        }
        return "Locked";
    }

    private static string HeldName(PlayerControllerB player)
    {
        GrabbableObject held = player.currentlyHeldObjectServer;
        return held != null && held.itemProperties != null ? held.itemProperties.itemName : "nothing";
    }

    private static DoorLock[] Doors()
    {
        bool stale = Time.time - doorsFoundAt > DoorListSeconds;
        if (!stale)
        {
            foreach (DoorLock door in doors)
            {
                stale |= door == null;
            }
        }
        if (stale)
        {
            doors = Object.FindObjectsOfType<DoorLock>();
            doorsFoundAt = Time.time;
        }
        return doors;
    }

    // The door a trigger belongs to (its own object, or a door that names it as one of its triggers).
    private static DoorLock? DoorOf(InteractTrigger trigger)
    {
        DoorLock door = trigger.GetComponent<DoorLock>();
        if (door != null && (door.doorTrigger == trigger || door.doorTriggerB == trigger))
        {
            return door;
        }
        foreach (DoorLock candidate in Doors())
        {
            if (candidate != null && (candidate.doorTrigger == trigger || candidate.doorTriggerB == trigger))
            {
                return candidate;
            }
        }
        return null;
    }

    // Closed, unlocked, a door that can be locked at all, and nobody picking it.
    private static bool Lockable(DoorLock door)
    {
        if (door == null || !door.canBeLocked || door.isLocked || door.isPickingLock || door.isDoorOpened)
        {
            return false;
        }
        AnimatedObjectTrigger animation = door.GetComponent<AnimatedObjectTrigger>();
        return animation == null || !animation.boolValue;
    }

    private static bool UsedRecently(DoorLock door)
    {
        return LastUsed.TryGetValue(door, out float at) && Time.time - at < RecentUseSeconds;
    }

    private static bool MonsterNear(PlayerControllerB player)
    {
        RoundManager roundManager = RoundManager.Instance;
        if (roundManager == null || roundManager.SpawnedEnemies == null)
        {
            return false;
        }
        foreach (EnemyAI enemy in roundManager.SpawnedEnemies)
        {
            if (enemy != null && !enemy.isEnemyDead
                && Vector3.Distance(enemy.transform.position, player.transform.position) <= MonsterClearRadius)
            {
                return true;
            }
        }
        return false;
    }
}
