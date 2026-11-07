using GameNetcodeStuff;
using UnityEngine;
using static System.FormattableString;

namespace Sanity.Symptoms;

// Lost time: you step out of the facility and it's night.
// When: at most once a day, on days a daily roll allows (DailyChance, rolled by each player's own game). It fires on
// the way out of the facility, once both of these are true:
//   - enough real time has passed since landing: MinMinutesAfterLanding on a normal-length day, longer in proportion
//     on longer days (DayLength), so it's never early in the day on the clock either;
//   - this visit inside lasted at least MinInsideSeconds without a break. The visit can start before the first rule is
//     met (in at 3 minutes, out after 6: it fires).
// Armed by the director (Uneasy or lower) no earlier than MinInsideSeconds before it could fire, then it waits for the
// day. It fires as you go through the exit (a hook on the game's entrance teleport), so the fake time is already there
// the moment you're outside; it never switches to night in front of you. If anything holds it back at that exit (tier,
// another symptom under 5 s ago, a short visit), that exit is let go and it waits for the next one.
// What you see: the clock shows a time between EarliestShownHour and LatestShownHour (always before the game's own
// "ship leaving soon" warning), with the matching day icon; the sky and light (the game's sun animation) match it; and
// the game's own time-of-day music cue plays, as it does when you step out into a new part of the day. After
// FakeSeconds, the clock and sky slide back to the real time over SlideSeconds. The shown time runs on at the game's
// own pace, so mods that change the day's speed still work.
// The ship leaving early (a vote) or the midnight warning: it isn't armed, an armed one is dropped, and one that's
// showing ends at once, so the clock never contradicts the game's "return by" message.
// Going back inside, dying or leaving also end it at once. Nothing is sent: only your clock, sky and music lie.
internal sealed class LostTimeSymptom : ArmedSymptom
{
    private const float DailyChance = 0.8f;              // share of days it's allowed at all, per player
    private const float MinMinutesAfterLanding = 6f;     // real minutes on a normal-length day; scaled by day length
    internal const float MinInsideSeconds = 180f;        // one continuous visit inside
    private const float EarliestShownHour = 20f;         // 8 PM: clearly evening
    private const float LatestShownHour = 22f;           // 10 PM: before the game's own warning at about 10:12 PM
    private const float LeastLieHours = 1f;
    private const float FakeSeconds = 12f;
    private const float SlideSeconds = 1.5f;
    private const float ClockStartHour = 6f;             // the game's clock: 6 AM + day fraction x hours in a day
    private const float CueDelay = 0.5f;                 // the game's own delay for its time-of-day cue

    private static LostTimeSymptom? instance;

    private static bool rolledToday;
    private static float landedAt;
    private static bool allowedToday;
    private static bool usedToday;
    private static bool wasInside;
    private static float insideSince;
    private static int exitHandledFrame = -10;
    private static bool exiting;
    private static float exitVisitSeconds;
    private static bool lying;
    private static float lieStarted;
    private static float lieOffset;

    internal LostTimeSymptom()
    {
        instance = this;
    }

    // For the clock and sky patches: how far ahead of the real day time to show right now (0 = the truth).
    internal static float ShownOffset { get; private set; }

    internal override string Name => "Lost time";

    internal override string ConfigKey => "LostTime";

    internal override string Family => "sight";

    internal override SanityTier MinTier => SanityTier.Uneasy;

    // High, so it's armed soon once it's possible on an allowed day (it plays at most once a day anyway).
    internal override float BaseWeight => 3f;

    internal override float RecoverySeconds => 600f;

    // Waits all day for its exit (the day ending drops it).
    protected override float ExpireSeconds => 1e9f;

    // It can wait all day, so it has a slot of its own and never keeps the flicker, the door or the item from arming.
    protected override bool CountsTowardCap => false;

    internal override string Waits => Invariant($"you leaving the facility after {MinInsideSeconds / 60f:0.#} min inside");

    protected override bool Showing => lying;

    // Real seconds after landing before it can fire today.
    private static float EarliestSeconds => MinMinutesAfterLanding * 60f * (DayLength.Measured ? DayLength.Factor : 1f);

    private static float SinceLanding => Time.time - landedAt;

    // Enough real time since landing for it to fire (also used by telemetry's opportunity count).
    internal static bool LateEnough => SinceLanding >= EarliestSeconds;

    internal override string MomentStatus(PlayerControllerB player)
    {
        string landing = SinceLanding < EarliestSeconds
            ? Invariant($"fires from {EarliestSeconds / 60f:F1} min after landing (now {SinceLanding / 60f:F1}); ")
            : "";
        if (!player.isInsideFactory)
        {
            return landing + Invariant($"waits for you to go in, stay {MinInsideSeconds:F0} s, and come out");
        }
        float visit = Time.time - insideSince;
        return landing + (visit >= MinInsideSeconds ? Invariant($"inside {visit:F0} s: fires when you step out")
            : Invariant($"inside {visit:F0} s (needs {MinInsideSeconds:F0} before an exit counts)"));
    }

    protected override string CantArmReason(PlayerControllerB player)
    {
        TimeOfDay timeOfDay = TimeOfDay.Instance;
        if (timeOfDay == null || timeOfDay.currentLevel == null || !timeOfDay.currentLevel.planetHasTime)
        {
            return "no clock";
        }
        if (usedToday)
        {
            return "used today";
        }
        if (!allowedToday)
        {
            return "not today (daily roll)";
        }
        if (ShipLeaving(timeOfDay))
        {
            return "the ship is leaving (early vote or the midnight warning)";
        }
        // Armed no earlier than one full visit before it could fire, so it doesn't hold an armed slot all morning.
        if (SinceLanding < EarliestSeconds - MinInsideSeconds)
        {
            return Invariant($"too early: {SinceLanding / 60f:F1} min since landing (armed from {(EarliestSeconds - MinInsideSeconds) / 60f:F1})");
        }
        float hour = HourOf(timeOfDay.normalizedTimeOfDay, timeOfDay.numberOfHours);
        if (hour > LatestShownHour - LeastLieHours)
        {
            return $"too late: {Clock(hour)} (nothing believable left to lose)";
        }
        return "";
    }

    // Only on the way out (HandleExit). Armed from the debug menu with checks ignored, any exit will do.
    protected override bool TryFire(PlayerControllerB player, SanityTier tier)
    {
        TimeOfDay timeOfDay = TimeOfDay.Instance;
        if (!exiting || timeOfDay == null)
        {
            return false;
        }
        if (!ArmedByDebug)
        {
            if (exitVisitSeconds < MinInsideSeconds)
            {
                Miss("short visit");
                return false;
            }
            if (SinceLanding < EarliestSeconds)
            {
                Miss("too soon after landing");
                return false;
            }
            if (ShipLeaving(timeOfDay))
            {
                return false;
            }
        }

        float hourNow = HourOf(timeOfDay.normalizedTimeOfDay, timeOfDay.numberOfHours);
        float shownHour = Random.Range(EarliestShownHour, LatestShownHour);
        if (shownHour - hourNow < LeastLieHours)
        {
            Miss("too late in the day");
            return false;
        }

        float hours = Mathf.Max(1f, timeOfDay.numberOfHours);
        lieOffset = (shownHour - hourNow) / hours;
        ShownOffset = lieOffset;
        lying = true;
        usedToday = true;
        lieStarted = Time.time;
        timeOfDay.RefreshClockUI();   // the clock and its icon show the lie at once
        float shownFraction = Mathf.Clamp(timeOfDay.normalizedTimeOfDay + lieOffset, 0f, 0.99f);
        if (timeOfDay.sunAnimator != null)
        {
            timeOfDay.sunAnimator.SetFloat("timeOfDay", shownFraction);   // the sky, before this frame is drawn
        }
        PlayCue(timeOfDay, shownFraction);

        Telemetry.Event("LOST_TIME", shownHour - hourNow, Invariant(
            $"real {Clock(hourNow)} shown {Clock(shownHour)} (+{shownHour - hourNow:F1} h) after {exitVisitSeconds:F0} s inside, {SinceLanding / 60f:F1} min after landing"));
        Chains.Trigger(this, "shown");
        return true;
    }

    // Called by the entrance hook (EntrancePatch) right after the game moves you out of the facility, in the same
    // frame, before anything outside is drawn.
    internal static void OnLeftFacility(PlayerControllerB player)
    {
        instance?.HandleExit(player);
    }

    protected override void WatchAlways(PlayerControllerB player)
    {
        if (!rolledToday)
        {
            rolledToday = true;
            allowedToday = Random.value < DailyChance;
            landedAt = Time.time;
        }

        // Time inside on this visit, and the moment you step out. An exit the entrance hook didn't see (another mod's
        // teleport, say) is caught here instead, a frame later.
        bool inside = player.isInsideFactory && !player.isPlayerDead;
        if (inside && !wasInside)
        {
            insideSince = Time.time;
        }
        if (wasInside && !inside && !player.isPlayerDead)
        {
            HandleExit(player);
        }
        wasInside = inside;

        TimeOfDay timeOfDay = TimeOfDay.Instance;
        if (IsArmed && timeOfDay != null && ShipLeaving(timeOfDay))
        {
            Drop("the ship is leaving");
        }

        if (!lying)
        {
            return;
        }

        float t = Time.time - lieStarted;
        string ended = timeOfDay == null ? "no clock"
            : player.isPlayerDead ? "died"
            : !SanityMeter.OnMoon ? "left the moon"
            : player.isInsideFactory ? "went back inside"
            : ShipLeaving(timeOfDay) ? "the ship is leaving"
            : t >= FakeSeconds + SlideSeconds ? "finished"
            : "";
        if (ended.Length > 0)
        {
            EndLie(ended);
            return;
        }

        // The lie holds, then slides back to the truth; during the slide the clock moves every frame.
        ShownOffset = t <= FakeSeconds ? lieOffset : lieOffset * (1f - (t - FakeSeconds) / SlideSeconds);
        if (t > FakeSeconds && timeOfDay != null)
        {
            timeOfDay.RefreshClockUI();
        }
    }

    protected override void StopShowing(string why)
    {
        if (lying)
        {
            EndLie(why);
        }
    }

    protected override void ResetDayState()
    {
        rolledToday = false;
        allowedToday = false;
        usedToday = false;
        wasInside = false;
        exitHandledFrame = -10;
    }

    // One exit, seen by the entrance hook or (a frame later) by the per-frame watch: handled once.
    private void HandleExit(PlayerControllerB player)
    {
        if (Time.frameCount - exitHandledFrame <= 2)
        {
            return;
        }
        exitHandledFrame = Time.frameCount;
        exitVisitSeconds = Time.time - insideSince;
        if (!IsArmed)
        {
            return;
        }

        exiting = true;
        try
        {
            if (!FireIfFree(player) && HeldBy.Length > 0)
            {
                Miss("exit held by " + HeldBy);
            }
        }
        finally
        {
            exiting = false;
        }
    }

    private static void EndLie(string ended)
    {
        lying = false;
        ShownOffset = 0f;
        TimeOfDay timeOfDay = TimeOfDay.Instance;
        if (timeOfDay != null && HUDManager.Instance != null)
        {
            timeOfDay.RefreshClockUI();   // the real time and icon (the sky follows on the game's next frame)
        }
        float lasted = Time.time - lieStarted;
        Telemetry.Event("LOST_TIME_END", lasted, Invariant($"{ended} after {lasted:F1}s"));
    }

    // The game's own music cue for that part of the day, as it plays when you step out into a new one.
    private static void PlayCue(TimeOfDay timeOfDay, float shownFraction)
    {
        int phase = (int)timeOfDay.GetDayPhase(shownFraction);
        AudioClip[]? cues = timeOfDay.timeOfDayCues;
        if (cues == null || phase < 0 || phase >= cues.Length || cues[phase] == null || timeOfDay.TimeOfDayMusic == null)
        {
            return;
        }
        timeOfDay.PlayTimeMusicDelayed(cues[phase], CueDelay);
    }

    // The ship is about to leave: dead players voted to leave early (the game's "return by" message), or the game's
    // own midnight warning came.
    private static bool ShipLeaving(TimeOfDay timeOfDay)
    {
        StartOfRound startOfRound = StartOfRound.Instance;
        return timeOfDay.shipLeavingAlertCalled || (startOfRound != null && startOfRound.shipIsLeaving);
    }

    // The game's clock: 6 AM plus the day fraction times the hours in a day (HUDManager.SetClock).
    private static float HourOf(float dayFraction, float hoursInDay)
    {
        return ClockStartHour + dayFraction * hoursInDay;
    }

    // For telemetry and the debug menu: 24-hour "15:05".
    private static string Clock(float hour)
    {
        int minutes = Mathf.FloorToInt(hour * 60f);
        return Invariant($"{minutes / 60 % 24:00}:{minutes % 60:00}");
    }
}
