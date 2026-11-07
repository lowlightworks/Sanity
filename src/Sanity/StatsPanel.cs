using System;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace Sanity;

// The anonymous-stats notice: a small panel in the bottom-right corner of the main menu, shown once (and again only
// when what's collected changes: StatsUploader.NoticeVersion), never in a game. Sharing is on by default, but nothing
// is sent until this notice has been shown. It's built from the game's own menu parts (the menu font and colour, and
// copies of the game's own menu button), so it looks like the rest of the menu. Keep sharing / Turn off set config
// Stats/ShareAnonymousStats; "What's sent?" opens the README's list of fields. It stays until a choice is made or the
// menu goes away (hosting or joining a game). If anything it needs is missing, there's simply no panel, the notice
// counts as not shown, and nothing is sent.
internal static class StatsPanel
{
    private const string FieldsUrl = "https://github.com/lowlightworks/Sanity#anonymous-stats";
    private static readonly Vector2 PanelSize = new Vector2(440f, 168f);
    private const float Margin = 24f;
    private const float Padding = 14f;
    private const float ButtonHeight = 26f;

    private static GameObject? panel;
    private static TextMeshProUGUI? status;

    // Called when the main menu opens: shows the notice if it hasn't been shown yet (and sharing is on).
    internal static void Show(MenuManager menu)
    {
        if (menu.isInitScene || !StatsUploader.Available || StatsUploader.NoticeSeen || !Plugin.ShareStats.Value)
        {
            return;
        }

        try
        {
            if (Build(menu))
            {
                StatsUploader.MarkNoticeSeen();
            }
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"Stats panel: couldn't build it ({e.GetType().Name}: {e.Message}). The setting still works from the config.");
            if (panel != null)
            {
                UnityEngine.Object.Destroy(panel);
            }
            panel = null;
        }
    }

    // True once the panel is on screen.
    private static bool Build(MenuManager menu)
    {
        TextMeshProUGUI gameText = menu.menuNotificationText;
        Button gameButton = menu.menuNotification.GetComponentInChildren<Button>(true);
        Transform parent = menu.menuNotification.transform.parent;
        if (gameText == null || gameButton == null || parent == null)
        {
            return false;
        }
        Color accent = gameText.color;

        panel = new GameObject("Sanity.StatsPanel", typeof(RectTransform), typeof(Image), typeof(Outline));
        RectTransform rect = (RectTransform)panel.transform;
        rect.SetParent(parent, worldPositionStays: false);
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(1f, 0f);
        rect.anchoredPosition = new Vector2(-Margin, Margin);
        rect.sizeDelta = PanelSize;
        rect.SetAsLastSibling();
        panel.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.92f);
        Outline outline = panel.GetComponent<Outline>();
        outline.effectColor = accent;
        outline.effectDistance = new Vector2(2f, -2f);

        Label(gameText, "Sanity shares anonymous stats", 17f, FontStyles.Bold, new Vector2(Padding, -Padding), 24f);
        Label(gameText,
            "To help tune the mod, it sends one small summary per in-game day: numbers only, no names, no IDs. "
            + "You can turn it off now, or any time in the config.",
            13f, FontStyles.Normal, new Vector2(Padding, -Padding - 26f), 64f);
        status = Label(gameText, "", 13f, FontStyles.Italic, new Vector2(Padding, -Padding - 92f), 20f);

        float width = (PanelSize.x - Padding * 2f - 16f) / 3f;
        Button(gameButton, "[ Keep sharing ]", new Vector2(Padding, Padding), width, () => Choose(true));
        Button(gameButton, "[ Turn off ]", new Vector2(Padding + width + 8f, Padding), width, () => Choose(false));
        Button(gameButton, "[ What's sent? ]", new Vector2(Padding + (width + 8f) * 2f, Padding), width, () => Application.OpenURL(FieldsUrl));
        return true;
    }

    // A button was clicked: save the choice and say what it is now.
    private static void Choose(bool share)
    {
        Plugin.ShareStats.Value = share;
        if (status != null)
        {
            status.text = share ? "Sharing. Thank you!" : "Off. Nothing will be sent.";
        }
    }

    // A text line in the game's own menu font and colour.
    private static TextMeshProUGUI Label(TextMeshProUGUI style, string text, float size, FontStyles fontStyle, Vector2 topLeft, float height)
    {
        GameObject go = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
        RectTransform rect = (RectTransform)go.transform;
        rect.SetParent(panel!.transform, worldPositionStays: false);
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(1f, 1f);
        rect.pivot = new Vector2(0f, 1f);
        rect.anchoredPosition = topLeft;
        rect.sizeDelta = new Vector2(-Padding * 2f, height);

        TextMeshProUGUI label = go.GetComponent<TextMeshProUGUI>();
        label.font = style.font;
        label.color = style.color;
        label.fontSize = size;
        label.fontStyle = fontStyle;
        label.enableWordWrapping = true;
        label.alignment = TextAlignmentOptions.TopLeft;
        label.raycastTarget = false;
        label.text = text;
        return label;
    }

    // A copy of the game's own menu button, with our own text and action. Returns its label.
    private static TextMeshProUGUI Button(Button template, string text, Vector2 bottomLeft, float width, UnityAction onClick)
    {
        GameObject go = UnityEngine.Object.Instantiate(template.gameObject, panel!.transform, worldPositionStays: false);
        go.name = "Button";
        go.SetActive(true);
        RectTransform rect = (RectTransform)go.transform;
        rect.anchorMin = rect.anchorMax = rect.pivot = Vector2.zero;
        rect.anchoredPosition = bottomLeft;
        rect.sizeDelta = new Vector2(width, ButtonHeight);
        rect.localScale = Vector3.one;

        Button button = go.GetComponent<Button>();
        button.onClick = new Button.ButtonClickedEvent();
        button.onClick.AddListener(onClick);

        TextMeshProUGUI label = go.GetComponentInChildren<TextMeshProUGUI>(true);
        label.text = text;
        label.fontSize = 13f;
        label.enableAutoSizing = false;
        label.enableWordWrapping = false;
        return label;
    }
}
