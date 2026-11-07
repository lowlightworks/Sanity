using UnityEngine;
using static System.FormattableString;

namespace Sanity;

// How long today really lasts compared with a normal day: 1 = normal, 2 = twice as long in real time. Measured live
// from how fast the clock actually moves, against the vanilla pace: a full clock day (6 AM to midnight) takes
// BaselineSecondsPerDay real seconds, which is 700 s from landing (7:40 AM) to midnight, or 1.4 game minutes per real
// second. So any mod or upgrade level that changes the pace (Lategame Upgrades' Quantum Disruptor at any level, or
// anything else) is caught without knowing about it. The moon's own speed settings are not used as the reference: on
// modded moons and on clients they don't match how the clock really runs. The raw values are logged in DAY_LENGTH.
// A clock jump of more than RestartJump in one frame, either way, restarts the measurement: Quantum Disruptor's rewind,
// and the jump to the morning just after landing on the first day after a launch. Smaller steps back (a client's
// clock-sync corrections) are ignored.
internal static class DayLength
{
    private const float MeasureAfterSeconds = 20f;   // a stable reading needs a little time
    private const float LogAfterSeconds = 60f;
    private const float RestartJump = 0.01f;         // of a day (~11 game minutes) in one frame: not the clock running
    private const float BaselineSecondsPerDay = 1080f / 1.4f;   // vanilla: 1080 game units at the game's time speed of 1.4

    internal static float Factor { get; private set; } = 1f;
    internal static bool Measured { get; private set; }

    private static bool started;
    private static bool logged;
    private static float startTime;
    private static float startFraction;
    private static float lastFraction;

    internal static void Tick(TimeOfDay timeOfDay)
    {
        if (timeOfDay == null || timeOfDay.currentLevel == null || !timeOfDay.currentDayTimeStarted || timeOfDay.totalTime <= 0f)
        {
            return;
        }

        float fraction = timeOfDay.normalizedTimeOfDay;
        if (!started || Mathf.Abs(fraction - lastFraction) > RestartJump)
        {
            started = true;
            startTime = Time.time;
            startFraction = fraction;
        }
        lastFraction = fraction;

        float elapsed = Time.time - startTime;
        float progress = fraction - startFraction;
        if (elapsed < MeasureAfterSeconds || progress <= 0.0005f || fraction >= 0.995f)
        {
            return;
        }

        float secondsPerDay = elapsed / progress;   // real seconds for a full clock day at today's pace
        Factor = Mathf.Clamp(secondsPerDay / BaselineSecondsPerDay, 0.25f, 8f);
        Measured = true;
        if (!logged && elapsed >= LogAfterSeconds)
        {
            logged = true;
            Telemetry.Event("DAY_LENGTH", Factor, Invariant(
                $"day lasts x{Factor:F2} a normal day ({secondsPerDay:F0} real s per day; total time {timeOfDay.totalTime:F0}, moon day speed {timeOfDay.currentLevel.DaySpeedMultiplier:0.###}, global time speed {timeOfDay.globalTimeSpeedMultiplier:0.###})"));
        }
    }

    internal static void Reset()
    {
        Factor = 1f;
        Measured = false;
        started = false;
        logged = false;
    }
}
