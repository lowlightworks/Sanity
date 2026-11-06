using System.Collections.Generic;
using GameNetcodeStuff;
using Sanity.Symptoms;
using UnityEngine;
using UnityEngine.InputSystem;
using static System.FormattableString;

namespace Sanity;

// A small testing window. F7 opens and closes it (only when EnableDebugKeys is on in the config).
// While it's open, the mouse is freed and the camera stops turning, like the game's own menus.
// A fixed-size window with four tabs.
//   Now:      what's going on (meter, director, armed symptoms and why they wait, what's showing, chains pending,
//             reliefs, the world, what's loaded).
//   Symptoms: one line per symptom (allow tick, fire button, short state; hover a line for the whole of it).
//   Log:      the last 80 events, newest first, filterable (symptoms, chains, the Presence, meter, problems).
//   Tools:    sanity presets and the test toggles.
internal sealed class DebugMenu : MonoBehaviour
{
    private const float Width = 460f;
    private const float Height = 540f;
    private static readonly float[] SanityPresets = { 100f, 70f, 60f, 40f, 15f, 0f };
    private static readonly string[] StrengthNames = { "Current", "Uneasy", "Disturbed", "Breaking" };
    private static readonly string[] Tabs = { "Now", "Symptoms", "Log", "Tools" };
    private static readonly string[] LogFilters = { "All", "Symptoms", "Chains", "Presence", "Meter", "Problems" };
    private static readonly string[] LogKinds = { "", "symptom", "chain", "presence", "meter", "problem" };

    internal static bool IsOpen { get; private set; }

    private bool open;
    private bool weDisabledLook;          // only undo what we changed ourselves
    private int strength;                 // 0 = current tier, 1-3 = a fixed tier
    private int tab;
    private int logFilter;
    private string message = "";
    private Rect window;
    private Vector2 scroll;
    private string[] statuses = System.Array.Empty<string>();
    private string[] statusesShort = System.Array.Empty<string>();
    private readonly List<string> nowLines = new List<string>();
    private float statusTimer;
    private GUIStyle? small;
    private GUIStyle? heading;

    internal static void Create()
    {
        GameObject host = new GameObject("Sanity.DebugMenu");
        host.hideFlags = HideFlags.HideAndDontSave;
        DontDestroyOnLoad(host);
        host.AddComponent<DebugMenu>();
    }

    private void Awake()
    {
        // Right side of the screen, away from the game's own pause menu on the left.
        window = new Rect(Screen.width - Width - 20f, 40f, Width, Height);
    }

    private void Update()
    {
        bool keysOn = Plugin.DebugKeys.Value;
        if (keysOn && Keyboard.current != null && Keyboard.current.f7Key.wasPressedThisFrame)
        {
            SetOpen(!open);
        }
        else if (open && !keysOn)
        {
            SetOpen(false);
        }

        // The game locks the cursor at various moments, so keep it free every frame while open.
        if (open)
        {
            Reactions.NoteConfound("debug menu");
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            // Some checks search the scene, so refresh the text twice a second, not every frame.
            statusTimer -= Time.deltaTime;
            if (statusTimer <= 0f)
            {
                statusTimer = 0.5f;
                Refresh();
            }
        }
    }

    // ---- What the tabs show (built twice a second) ----

    private void Refresh()
    {
        PlayerControllerB? player = LocalPlayer();
        int count = SanityDirector.Symptoms.Count;
        if (statuses.Length != count)
        {
            statuses = new string[count];
            statusesShort = new string[count];
        }
        for (int i = 0; i < count; i++)
        {
            Symptom symptom = SanityDirector.Symptoms[i];
            string state;
            if (!symptom.Enabled)
            {
                state = "off in config";
            }
            else if (player == null || !SanityMeter.OnMoon)
            {
                state = "not on a moon";
            }
            else if (symptom.RunningState(player).Length > 0)
            {
                state = "RUNNING: " + symptom.RunningState(player);
            }
            else if (symptom is ArmedSymptom armed && armed.IsArmed)
            {
                string held = armed.HeldByNow;
                state = Invariant($"ARMED {armed.ArmedFor:F0}/{armed.WaitLimit:F0}s{(held.Length > 0 ? ", held: " + held : "")}; {armed.MomentStatus(player)}");
            }
            else
            {
                string reason = SanityDirector.NaturalCantPlayReason(symptom, player);
                state = reason.Length > 0 ? "can't: " + reason
                    : SanityMeter.Tier < symptom.MinTier ? $"ready, needs {symptom.MinTier}"
                    : "ready";
            }
            statuses[i] = Invariant($"{state} (recovery {symptom.Recovery * 100f:F0}%)");
            statusesShort[i] = Short(state, 44);
        }

        nowLines.Clear();
        if (player == null)
        {
            nowLines.Add("Not in a game.");
            return;
        }
        BuildNow(player);
    }

    private void BuildNow(PlayerControllerB player)
    {
        // Director.
        string relax = SanityDirector.Relaxing ? Invariant($", RELAX {SanityDirector.RelaxLeft:F0}s") : "";
        nowLines.Add("#Director");
        nowLines.Add(Invariant($"chance {SanityDirector.ChancePerSecond:F3}/s, wave {(Waves.High ? "high x1.6" : "low x0.4")}, mood {DayMood.Name}{relax}"));
        nowLines.Add(Invariant($"last: {SanityDirector.LastSymptom}, {Ago(SanityDirector.SecondsSinceVisible)} since something showed, quiet {SanityDirector.QuietSeconds:F0}s"));
        string chains = Chains.PendingSummary;
        if (chains.Length > 0)
        {
            nowLines.Add("chain coming: " + chains);
        }

        // Armed and showing.
        nowLines.Add("#Armed and showing");
        int lines = 0;
        foreach (Symptom symptom in SanityDirector.Symptoms)
        {
            if (symptom is ArmedSymptom armed && (armed.IsArmed || armed.IsShowing))
            {
                string held = armed.HeldByNow;
                nowLines.Add(armed.IsArmed
                    ? Invariant($"{armed.Name}: armed {armed.ArmedFor:F0}/{armed.WaitLimit:F0}s{(held.Length > 0 ? ", HELD by " + held : "")}. {armed.MomentStatus(player)}")
                    : $"{armed.Name}: showing. {armed.MomentStatus(player)}");
                lines++;
            }
        }
        if (PresenceSymptom.Active)
        {
            nowLines.Add("Presence: " + PresenceSymptom.DebugState(player));
            nowLines.Add("  did: " + PresenceSymptom.LastNotes);
            lines++;
        }
        if (PhantomHitSymptom.DebugState.Length > 0)
        {
            nowLines.Add("Phantom hit: " + PhantomHitSymptom.DebugState);
            lines++;
        }
        if (PhantomAudio.IsPlaying && !PresenceSymptom.Active)
        {
            nowLines.Add("a phantom sound or steps is playing");
            lines++;
        }
        if (lines == 0)
        {
            nowLines.Add("nothing armed or showing. Presence: " + PresenceSymptom.DebugState(player));
        }

        // Meter.
        nowLines.Add("#Meter");
        nowLines.Add(Invariant($"inside {SanityMeter.PartInside:F2} outside {SanityMeter.PartOutside:F2} night {SanityMeter.PartNight:F2} fear {SanityMeter.PartFear:F2} ship +{SanityMeter.PartShip:F2} /s"));
        nowLines.Add(SanityReliefs.DebugSummary);
        string company = SanityMeter.NearestTeammateDistance < 0f ? "nobody" : Invariant($"{SanityMeter.NearestTeammateDistance:F0} m{(SanityMeter.NearTeammate ? " (company)" : "")}");
        nowLines.Add(Invariant($"nearest teammate {company}, lobby {SanityMeter.PlayersAtStart} (x{SanityMeter.LobbyMultiplier:0.##}), loot today +{LootRecovery.GainedToday:F1}"));

        // World and what's loaded.
        nowLines.Add("#World and loaded");
        nowLines.Add(Invariant($"{Weather.Name} (out x{Weather.Outside:0.##} in x{Weather.Inside:0.##}), day x{DayLength.Factor:0.##} long{(DayLength.Measured ? "" : " (measuring)")}, phantom loudness {PhantomAudio.Closer:0.##}x closer"));
        nowLines.Add("mods seen: " + Telemetry.ModsSummary());
        string telemetry = !Plugin.TelemetryEnabled.Value ? "off"
            : Telemetry.Failed ? "FAILED (see BepInEx log)"
            : Telemetry.FilePath.Length == 0 ? "on (file starts at first landing)"
            : System.IO.Path.GetFileName(Telemetry.FilePath);
        nowLines.Add("telemetry: " + telemetry);
        if (Diagnostics.LastProblem.Length > 0)
        {
            nowLines.Add("problem seen: " + Diagnostics.LastProblem);
        }
    }

    // ---- Opening and closing ----

    private void SetOpen(bool value)
    {
        open = value;
        IsOpen = value;
        PlayerControllerB? player = LocalPlayer();

        if (open)
        {
            message = "";
            statusTimer = 0f;
            if (player != null && !player.disableLookInput)
            {
                player.disableLookInput = true;
                weDisabledLook = true;
            }
            return;
        }

        if (player != null)
        {
            if (weDisabledLook)
            {
                player.disableLookInput = false;
            }

            // Lock the cursor again, unless one of the game's own menus still needs it.
            if (!player.quickMenuManager.isMenuOpen && !player.inTerminalMenu && !player.inSpecialMenu)
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
        }
        weDisabledLook = false;
    }

    // ---- Drawing ----

    private void OnGUI()
    {
        if (!open)
        {
            return;
        }
        if (small == null)
        {
            small = new GUIStyle(GUI.skin.label) { fontSize = 11, wordWrap = true, richText = false };
            small.margin = new RectOffset(2, 2, 0, 0);
            small.padding = new RectOffset(2, 2, 1, 1);
            heading = new GUIStyle(small) { fontStyle = FontStyle.Bold };
        }
        window.width = Width;
        window.height = Height;
        window = GUILayout.Window(GetInstanceID(), window, DrawWindow, "Sanity debug  (F7 closes)", GUILayout.Width(Width), GUILayout.Height(Height));
    }

    private void DrawWindow(int id)
    {
        PlayerControllerB? player = LocalPlayer();
        GUIStyle text = small!;

        string flags = (SanityMeter.Frozen ? " FROZEN" : "") + (SanityDirector.Paused ? " PAUSED" : "")
            + (SanityDirector.IgnoreConditions ? " NO-CHECKS" : "") + (SanityDirector.ChainsAlways ? " CHAINS-ALWAYS" : "");
        GUILayout.Label(Invariant($"Sanity {SanityMeter.Value:F1} {SanityMeter.Tier}   {SanityMeter.Rate:+0.00;-0.00}/s   fear {SanityMeter.Fear:F2}{flags}"), heading);
        tab = GUILayout.Toolbar(tab, Tabs);

        scroll = GUILayout.BeginScrollView(scroll, GUILayout.Height(Height - 120f));
        switch (tab)
        {
            case 0:
                DrawNow(text);
                break;
            case 1:
                DrawSymptoms(player, text);
                break;
            case 2:
                DrawLog(text);
                break;
            default:
                DrawTools(text);
                break;
        }
        GUILayout.EndScrollView();

        // The full state of a symptom line you hover, or the last button's result.
        string bottom = GUI.tooltip.Length > 0 ? GUI.tooltip : message;
        GUILayout.Label(bottom, text, GUILayout.Height(34f));
        GUI.DragWindow();
    }

    private void DrawNow(GUIStyle text)
    {
        foreach (string line in nowLines)
        {
            if (line.StartsWith("#"))
            {
                GUILayout.Label(line.Substring(1), heading);
            }
            else
            {
                GUILayout.Label(line, text);
            }
        }
    }

    private void DrawSymptoms(PlayerControllerB? player, GUIStyle text)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label("Fire at:", text, GUILayout.Width(48f));
        strength = GUILayout.Toolbar(strength, StrengthNames);
        GUILayout.EndHorizontal();
        GUILayout.Label("Tick = allowed naturally (saved). Button = fire / arm now. Hover a line for all of it.", text);

        for (int i = 0; i < SanityDirector.Symptoms.Count; i++)
        {
            Symptom symptom = SanityDirector.Symptoms[i];
            GUILayout.BeginHorizontal();
            if (symptom.EnabledEntry != null)
            {
                bool allowed = GUILayout.Toggle(symptom.EnabledEntry.Value, "", GUILayout.Width(16f));
                if (allowed != symptom.EnabledEntry.Value)
                {
                    symptom.EnabledEntry.Value = allowed;   // logs SYMPTOM_TOGGLED and saves the config
                    statusTimer = 0f;
                }
            }
            if (GUILayout.Button(symptom.Name, GUILayout.Width(112f)))
            {
                message = player == null ? "Not in a game." : SanityDirector.DebugFire(player, symptom, StrengthTier());
                statusTimer = 0f;
            }
            string full = i < statuses.Length ? statuses[i] : "";
            GUILayout.Label(new GUIContent(i < statusesShort.Length ? statusesShort[i] : "", symptom.Name + ": " + full), text);
            GUILayout.EndHorizontal();
        }
    }

    private void DrawLog(GUIStyle text)
    {
        GUILayout.BeginHorizontal();
        logFilter = GUILayout.Toolbar(logFilter, LogFilters);
        if (GUILayout.Button("Clear", GUILayout.Width(48f)))
        {
            DebugLog.Clear();
        }
        GUILayout.EndHorizontal();
        string kind = LogKinds[logFilter];
        IReadOnlyList<DebugLog.Entry> all = DebugLog.All;
        int shown = 0;
        for (int i = all.Count - 1; i >= 0; i--)
        {
            DebugLog.Entry entry = all[i];
            if (kind.Length > 0 && entry.Kind != kind && !(kind == "symptom" && entry.Kind == "chain"))
            {
                continue;
            }
            GUILayout.Label(Invariant($"{Ago(Time.time - entry.At),5} {entry.Mark,-2} {entry.Text}"), text);
            shown++;
        }
        if (shown == 0)
        {
            GUILayout.Label("Nothing yet.", text);
        }
        GUILayout.Label("> played   + armed   -> chained follow-up   x dropped/failed   . detail   ! problem", text);
    }

    private void DrawTools(GUIStyle text)
    {
        GUILayout.Label("Set sanity", heading);
        GUILayout.BeginHorizontal();
        foreach (float preset in SanityPresets)
        {
            if (GUILayout.Button(preset.ToString("0")))
            {
                message = SanityMeter.DebugSetSanity(preset);
            }
        }
        GUILayout.EndHorizontal();

        GUILayout.Label("Tests", heading);
        Toggle(ref SanityMeter.Frozen, "Freeze sanity", "sanity frozen", "sanity unfrozen");
        Toggle(ref SanityDirector.Paused, "Pause natural symptoms", "director paused", "director resumed");
        Toggle(ref SanityDirector.IgnoreGapsNatural, "Ignore gaps: natural rolls (recovery check, fear gap)", "gaps ignored (natural)", "gaps restored (natural)");
        Toggle(ref SanityDirector.IgnoreGapsForced, "Ignore gaps: F8 and buttons", "gaps ignored (forced)", "gaps restored (forced)");
        Toggle(ref SanityDirector.IgnoreConditions, "Ignore conditions: F8 and buttons skip every check (may look wrong)", "conditions ignored (forced)", "conditions restored (forced)");
        Toggle(ref SanityDirector.ChainsAlways, "Chains always follow (every link rolls in)", "chains always", "chains by chance");
        GUILayout.Label("Armed from a button: no 5 s gap or relax wait (tier still applies unless Ignore conditions).", text);

        GUILayout.Label("Display", heading);
        ConfigToggle(Plugin.ShowReadout, "On-screen readout");
        ConfigToggle(Plugin.ShowPresence, "Show the Presence (a red ball)");
        if (PhantomAudio.Loudness != null)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(Invariant($"Phantom loudness {PhantomAudio.Loudness.Value:0.00}"), text, GUILayout.Width(150f));
            float value = GUILayout.HorizontalSlider(PhantomAudio.Loudness.Value, 0.25f, 4f);
            if (Mathf.Abs(value - PhantomAudio.Loudness.Value) > 0.049f)
            {
                PhantomAudio.Loudness.Value = Mathf.Round(value * 10f) / 10f;
            }
            GUILayout.EndHorizontal();
        }
    }

    private static void Toggle(ref bool value, string label, string on, string off)
    {
        bool next = GUILayout.Toggle(value, " " + label);
        if (next != value)
        {
            value = next;
            Telemetry.Event("DEBUG", next ? 1f : 0f, next ? on : off);
        }
    }

    private static void ConfigToggle(BepInEx.Configuration.ConfigEntry<bool> entry, string label)
    {
        bool next = GUILayout.Toggle(entry.Value, " " + label);
        if (next != entry.Value)
        {
            entry.Value = next;
        }
    }

    // ---- Helpers ----

    private SanityTier StrengthTier()
    {
        if (strength > 0)
        {
            return (SanityTier)strength;
        }
        return SanityMeter.Tier == SanityTier.Calm ? SanityTier.Uneasy : SanityMeter.Tier;
    }

    private static string Short(string value, int length)
    {
        return value.Length <= length ? value : value.Substring(0, length - 1) + "~";
    }

    private static string Ago(float seconds)
    {
        return seconds > 9000f ? "-" : seconds >= 60f ? Invariant($"{seconds / 60f:F0}m") : Invariant($"{seconds:F0}s");
    }

    private static PlayerControllerB? LocalPlayer()
    {
        GameNetworkManager networkManager = GameNetworkManager.Instance;
        return networkManager != null ? networkManager.localPlayerController : null;
    }
}
