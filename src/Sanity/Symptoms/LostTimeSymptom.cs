using GameNetcodeStuff;
using UnityEngine;
using static System.FormattableString;

namespace Sanity.Symptoms;

// Lost time: you step out of the facility and it's evening, or nearly midnight.
// Armed by the director (Uneasy or lower) once the day is far enough along to be believable (1 PM or later). At most
// once a day, and only on days it's allowed at all (a daily roll, so it happens on about one day in 2-3). It then
// waits, for as long as the day lasts, for you to leave the facility after at least 120 s inside on that visit (time
// inside before it was armed counts, so it can be armed outside or in the ship).
// It fires ONLY on the frame you step out. If anything holds it back at that moment (above its tier, another
// symptom under 5 s ago, a visit under 120 s), that exit is let go and it waits for the next one: it never turns
// night later while you're already outside.
// When you step out, for 7 s the clock shows a later time and the sky matches it; then over 1.5 s both slide back to
// the real time, which then stays. The shown time runs on at the game's own pace (always the real time plus a fixed
// lie), so mods that change the day's speed still work.
//   The lie: the real time plus 3-6 hours, but at least 6 PM and at most 11:50 PM; it needs at least an hour of lie to
//   play (from about 10:50 PM there's nothing believable left to lose). It doesn't depend on sanity.
//   Clock: every clock that formats from the game's own clock update shows it (vanilla, GeneralImprovements' 24-hour
//   clock, LethalHUD's), with the matching day icon. Sky: the game's own sun animation, set to the shown time.
// Going back inside, dying or leaving ends it at once. Nothing is sent: only your clock and sky lie.
internal sealed class LostTimeSymptom : ArmedSymptom
{
    private const float DailyChance = 0.55f;        // share of days it's allowed at all
    private const float EarliestHour = 13f;         // armed from 1 PM on a normal-length day (earlier on longer days)
    private const float MinInsideSeconds = 120f;    // 2 minutes inside on this visit
    private const float MinLieHours = 3f;
    private const float MaxLieHours = 6f;
    private const float EveningHour = 18f;
    private const float LatestHour = 23f + 50f / 60f;
    private const float LeastLieHours = 1f;
    private const float FakeSeconds = 7f;
    private const float SlideSeconds = 1.5f;
    private const float ClockStartHour = 6f;        // the game's clock: 6 AM + day fraction x hours in a day

    private static bool rolledToday;
    private static float landedAt;
    private static float landedFraction;
    private static bool allowedToday;
    private static bool usedToday;
    private static bool wasInside;
    private static float insideSince;
    private static bool leftThisFrame;
    private static int exitFrame = -1;
    private static float visitSeconds;
    private static bool lying;
    private static float lieStarted;
    private static float lieOffset;

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

    internal override string Waits => "you leaving the facility after 120 s inside";

    protected override bool Showing => lying;

    internal override string MomentStatus(PlayerControllerB player)
    {
        if (!player.isInsideFactory)
        {
            return "waits for you to go in, stay 120 s, and come out";
        }
        float visit = Time.time - insideSince;
        return visit >= MinInsideSeconds ? Invariant($"inside {visit:F0} s: fires when you step out")
            : Invariant($"inside {visit:F0} s (needs {MinInsideSeconds:F0} before an exit counts)");
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

        float hour = HourOf(timeOfDay.normalizedTimeOfDay, timeOfDay.numberOfHours);
        float waitLeft = RealSecondsUntilBelievable(timeOfDay);
        if (hour < EarliestHour && waitLeft > 0f)
        {
            return Invariant($"too early: {Clock(hour)} (needs {Clock(EarliestHour)} or {waitLeft / 60f:F1} more real min)");
        }
        if (hour > LatestHour - LeastLieHours)
        {
            return $"too late: {Clock(hour)} (nothing believable left to lose)";
        }
        return "";
    }

    protected override bool TryFire(PlayerControllerB player, SanityTier tier)
    {
        TimeOfDay timeOfDay = TimeOfDay.Instance;
        // Only on the very frame you step out. Armed from the debug menu with checks ignored, any exit will do.
        if (!leftThisFrame || exitFrame != Time.frameCount || (visitSeconds < MinInsideSeconds && !ArmedByDebug)
            || timeOfDay == null)
        {
            return false;
        }

        float hourNow = HourOf(timeOfDay.normalizedTimeOfDay, timeOfDay.numberOfHours);
        float shownHour = Mathf.Clamp(hourNow + Random.Range(MinLieHours, MaxLieHours), EveningHour, LatestHour);
        if (shownHour - hourNow < LeastLieHours)
        {
            return false;
        }

        lieOffset = (shownHour - hourNow) / Mathf.Max(1f, timeOfDay.numberOfHours);
        ShownOffset = lieOffset;
        lying = true;
        usedToday = true;
        lieStarted = Time.time;
        timeOfDay.RefreshClockUI();   // the clock shows the lie at once, as it fades in outside

        Telemetry.Event("LOST_TIME", shownHour - hourNow, Invariant(
            $"real {Clock(hourNow)} shown {Clock(shownHour)} (+{shownHour - hourNow:F1} h) after {visitSeconds:F0} s inside"));
        Chains.Trigger(this, "shown");
        return true;
    }

    protected override void WatchAlways(PlayerControllerB player)
    {
        if (!rolledToday)
        {
            rolledToday = true;
            allowedToday = Random.value < DailyChance;
            landedAt = Time.time;
            TimeOfDay clock = TimeOfDay.Instance;
            landedFraction = clock != null ? clock.normalizedTimeOfDay : 0f;
        }
        else if (Time.time - landedAt < 30f)
        {
            // On the first day after a launch the clock jumps to the morning just after landing, so the first reading
            // is too early (lost time would wait ~1.5 min too long). Move the landing reading up with the clock for the
            // first 30 s (on a clock running normally this changes nothing).
            TimeOfDay clock = TimeOfDay.Instance;
            if (clock != null && clock.normalizedTimeOfDay > landedFraction + 0.01f)
            {
                landedFraction = clock.normalizedTimeOfDay;
                landedAt = Time.time;
            }
        }

        // Time inside on this visit, and the moment you step out.
        bool inside = player.isInsideFactory && !player.isPlayerDead;
        if (inside && !wasInside)
        {
            insideSince = Time.time;
        }
        leftThisFrame = wasInside && !inside && !player.isPlayerDead;
        visitSeconds = Time.time - insideSince;
        wasInside = inside;
        if (leftThisFrame)
        {
            exitFrame = Time.frameCount;
            if (IsArmed && visitSeconds < MinInsideSeconds && !ArmedByDebug)
            {
                Miss("short visit");
            }
            else if (IsArmed && HeldBy.Length > 0)
            {
                Miss("exit held by " + HeldBy);
            }
        }

        if (!lying)
        {
            return;
        }

        TimeOfDay timeOfDay = TimeOfDay.Instance;
        float t = Time.time - lieStarted;
        string ended = timeOfDay == null ? "no clock"
            : player.isPlayerDead ? "died"
            : !SanityMeter.OnMoon ? "left the moon"
            : player.isInsideFactory ? "went back inside"
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
        leftThisFrame = false;
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

    // "Enough of the day has passed" in REAL time: as long as a normal-length day takes to get from your landing time
    // to 1 PM (about 3.8 min). On a normal day that's the same as the clock rule; on a longer day (a mod or upgrade
    // slowing the clock) it comes earlier on the clock, because people don't stay for the whole longer day (in
    // playtests, doubled days ended around 1:20 PM). Returns the real seconds still to wait (0 or less = believable
    // now).
    private static float RealSecondsUntilBelievable(TimeOfDay timeOfDay)
    {
        // The game's base pace (as DayLength: the moon's own speed settings don't match the real clock on modded
        // moons).
        float normalPace = timeOfDay.totalTime > 0f ? 1f / timeOfDay.totalTime : 0f;
        if (normalPace <= 0f)
        {
            return 0f;
        }
        float earliestFraction = (EarliestHour - ClockStartHour) / Mathf.Max(1f, timeOfDay.numberOfHours);
        float needed = Mathf.Max(0f, earliestFraction - landedFraction) / normalPace;
        return needed - (Time.time - landedAt);
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
