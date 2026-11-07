using System;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace Sanity;

// The anonymous-stats notice: a small box in the bottom-right corner of the main menu, shown once (and again only
// when what's collected changes: StatsUploader.NoticeVersion), never in a game. Sharing is on by default, but nothing
// is sent until this notice has been on screen. It uses the game's own menu font and colour: an orange border and
// orange text, no fill (so it looks right however the menu draws its colours). Keep sharing / Turn off set config
// Stats/ShareAnonymousStats and close it; "What's sent?" opens the README's list of fields.
// It waits until the main menu is free (its buttons showing, no loading screen, none of the game's own popups), then
// counts as seen. It closes when you click Keep sharing or Turn off, click any of the menu's own buttons, the menu
// moves on (another screen, the loading screen, a popup), or after ShowSeconds. If anything it needs is missing,
// there's simply no notice, it doesn't count as seen, and nothing is sent.
internal static class StatsPanel
{
    private const string FieldsUrl = "https://github.com/lowlightworks/Sanity#anonymous-stats";
    private const float ShowSeconds = 30f;
    private const float Width = 380f;
    private const float Height = 124f;
    private const float Margin = 24f;
    private const float Padding = 12f;
    private const float BorderWidth = 2f;
    private const float ButtonHeight = 20f;
    private const float ButtonGap = 22f;

    private static GameObject? panel;

    // Called when the main menu opens: builds the notice if it hasn't been seen yet (and sharing is on).
    internal static void Show(MenuManager menu)
    {
        if (panel != null || menu.isInitScene || !StatsUploader.Available || StatsUploader.NoticeSeen || !Plugin.ShareStats.Value)
        {
            return;
        }

        try
        {
            Build(menu);
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"Stats notice: couldn't build it ({e.GetType().Name}: {e.Message}). The setting still works from the config.");
            Close();
        }
    }

    // Closes the notice, if it's open (safe to call any time).
    internal static void Close()
    {
        if (panel != null)
        {
            UnityEngine.Object.Destroy(panel);
        }
        panel = null;
    }

    private static void Build(MenuManager menu)
    {
        TextMeshProUGUI gameText = menu.menuNotificationText;
        Transform? parent = menu.menuNotification != null ? menu.menuNotification.transform.parent : null;
        if (gameText == null || parent == null)
        {
            return;
        }
        Color accent = gameText.color;

        panel = new GameObject("Sanity.StatsNotice", typeof(RectTransform), typeof(CanvasGroup));
        RectTransform rect = (RectTransform)panel.transform;
        rect.SetParent(parent, worldPositionStays: false);
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(1f, 0f);
        rect.anchoredPosition = new Vector2(-Margin, Margin);
        rect.sizeDelta = new Vector2(Width, Height);
        rect.localScale = Vector3.one;
        rect.SetAsLastSibling();

        // Hidden until the menu is free (NoticeLife shows it).
        CanvasGroup group = panel.GetComponent<CanvasGroup>();
        group.alpha = 0f;
        group.interactable = false;
        group.blocksRaycasts = false;

        BorderLine(accent, new Vector2(0f, 1f), new Vector2(1f, 1f));   // top
        BorderLine(accent, new Vector2(0f, 0f), new Vector2(1f, 0f));   // bottom
        BorderLine(accent, new Vector2(0f, 0f), new Vector2(0f, 1f));   // left
        BorderLine(accent, new Vector2(1f, 0f), new Vector2(1f, 1f));   // right

        Label(gameText, accent, "Sanity shares anonymous stats", 15f, FontStyles.Bold, -Padding, 20f);
        Label(gameText, accent,
            "To help tune the mod, it sends one small summary per in-game day: numbers only, no names, no IDs. "
            + "You can turn it off here, or any time in the config.",
            12f, FontStyles.Normal, -Padding - 24f, 48f);

        float x = Padding;
        x += Button(gameText, accent, "[ Keep sharing ]", x, () => Choose(true)) + ButtonGap;
        x += Button(gameText, accent, "[ Turn off ]", x, () => Choose(false)) + ButtonGap;
        Button(gameText, accent, "[ What's sent? ]", x, () => Application.OpenURL(FieldsUrl));

        // Any of the menu's own buttons closes it (host, join, settings, quit...).
        foreach (Button button in parent.root.GetComponentsInChildren<Button>(true))
        {
            if (!button.transform.IsChildOf(panel.transform))
            {
                button.onClick.AddListener(Close);
            }
        }

        NoticeLife life = panel.AddComponent<NoticeLife>();
        life.Menu = menu;
        life.Group = group;
    }

    private static void Choose(bool share)
    {
        Plugin.ShareStats.Value = share;
        Close();
    }

    // A thin line along one edge of the box.
    private static void BorderLine(Color color, Vector2 anchorMin, Vector2 anchorMax)
    {
        GameObject go = new GameObject("Border", typeof(RectTransform), typeof(Image));
        RectTransform rect = (RectTransform)go.transform;
        rect.SetParent(panel!.transform, worldPositionStays: false);
        rect.anchorMin = anchorMin;
        rect.anchorMax = anchorMax;
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = Vector2.zero;
        bool horizontal = anchorMin.y == anchorMax.y;
        rect.sizeDelta = horizontal ? new Vector2(0f, BorderWidth) : new Vector2(BorderWidth, 0f);
        Image image = go.GetComponent<Image>();
        image.color = color;
        image.raycastTarget = false;
    }

    // A text line in the game's own menu font, its top at the given distance from the top of the box.
    private static void Label(TextMeshProUGUI style, Color color, string text, float size, FontStyles fontStyle, float top, float height)
    {
        GameObject go = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
        RectTransform rect = (RectTransform)go.transform;
        rect.SetParent(panel!.transform, worldPositionStays: false);
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(1f, 1f);
        rect.pivot = new Vector2(0f, 1f);
        rect.anchoredPosition = new Vector2(Padding, top);
        rect.sizeDelta = new Vector2(-Padding * 2f, height);

        TextMeshProUGUI label = go.GetComponent<TextMeshProUGUI>();
        label.font = style.font;
        label.color = color;
        label.fontSize = size;
        label.fontStyle = fontStyle;
        label.enableWordWrapping = true;
        label.alignment = TextAlignmentOptions.TopLeft;
        label.raycastTarget = false;
        label.text = text;
    }

    // A text button along the bottom of the box, as wide as its text; brighter when the mouse is over it. Returns its
    // width.
    private static float Button(TextMeshProUGUI style, Color color, string text, float left, UnityAction onClick)
    {
        GameObject go = new GameObject("Button", typeof(RectTransform), typeof(TextMeshProUGUI), typeof(Button));
        RectTransform rect = (RectTransform)go.transform;
        rect.SetParent(panel!.transform, worldPositionStays: false);

        TextMeshProUGUI label = go.GetComponent<TextMeshProUGUI>();
        label.font = style.font;
        label.fontSize = 12f;
        label.enableWordWrapping = false;
        label.alignment = TextAlignmentOptions.MidlineLeft;
        label.color = Color.white;   // the button's colours below tint it
        label.raycastTarget = true;
        label.text = text;
        float width = Mathf.Ceil(label.GetPreferredValues(text).x) + 2f;

        rect.anchorMin = rect.anchorMax = rect.pivot = Vector2.zero;
        rect.anchoredPosition = new Vector2(left, Padding);
        rect.sizeDelta = new Vector2(width, ButtonHeight);

        Button button = go.GetComponent<Button>();
        button.targetGraphic = label;
        button.navigation = new Navigation { mode = Navigation.Mode.None };
        ColorBlock colors = button.colors;
        colors.normalColor = color;
        colors.highlightedColor = Color.Lerp(color, Color.white, 0.6f);
        colors.pressedColor = Color.white;
        colors.selectedColor = color;
        colors.colorMultiplier = 1f;
        colors.fadeDuration = 0.05f;
        button.colors = colors;
        button.onClick.AddListener(onClick);
        return width;
    }

    // Shows the notice once the menu is free, counts it as seen, and closes it when the menu moves on or time is up.
    private sealed class NoticeLife : MonoBehaviour
    {
        internal MenuManager? Menu;
        internal CanvasGroup? Group;
        private bool shown;
        private float shownFor;

        private void Update()
        {
            if (Menu == null || Group == null)
            {
                Close();
                return;
            }

            bool menuFree = Menu.menuButtons != null && Menu.menuButtons.activeInHierarchy
                && (Menu.loadingScreen == null || !Menu.loadingScreen.activeInHierarchy)
                && (Menu.menuNotification == null || !Menu.menuNotification.activeInHierarchy);
            if (!shown)
            {
                if (menuFree)
                {
                    shown = true;
                    Group.alpha = 1f;
                    Group.interactable = true;
                    Group.blocksRaycasts = true;
                    StatsUploader.MarkNoticeSeen();
                }
                return;
            }

            shownFor += Time.unscaledDeltaTime;
            if (!menuFree || shownFor >= ShowSeconds)
            {
                Close();
            }
        }
    }
}
