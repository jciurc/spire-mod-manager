using Godot;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;

namespace SpireModManager;

// A menu entry: a label and action, or a heading (no action) that separates groups.
// Detail: dim text on the right. Radio: one of several choices (a dot marks the checked one). Badge: a
// short mark after the label in its own colour (an unsaved profile's "*"). Tip: a tooltip (inline buttons).
// Flat: an inline button drawn as just its text, no box (the row's pencil).
record MenuItem(string Label, Action? OnSelect, bool Checked = false, bool Disabled = false, string? Detail = null,
    bool Radio = false, (string Text, Color Color)? Badge = null, string? Tip = null, bool Flat = false)
{
    public static MenuItem Heading(string label) => new(label, null);
}

static class Menus
{
    static readonly Color Cream = new(1, 0.964706f, 0.886275f);
    static readonly Color Gold = new(0.937f, 0.784f, 0.318f);

    // A right-click menu at the mouse (see MenuPanel).
    public static Node? Show(Node owner, IEnumerable<MenuItem> items, Vector2? at = null, string? toggleKey = null) =>
        MenuPanel.Show(owner, () => items.ToList(), at, toggleKey);

    // The game's own yes/no popup. True if confirmed.
    public static async Task<bool> Confirm(LocString title, LocString body)
    {
        if (NGenericPopup.Create() is not { } popup || NModalContainer.Instance is not { } modals)
            return false;
        modals.Add(popup);
        return await popup.WaitForConfirmation(body, title,
            new LocString("main_menu_ui", "GENERIC_POPUP.cancel"), new LocString("main_menu_ui", "GENERIC_POPUP.confirm"));
    }

    // A redraw asks for the same few words for every row (tag names, tooltips): inside one, each is looked up once.
    public static string Text(string key) => Lookups.Current?.Text(key, Lookup) ?? Lookup(key);

    static string Lookup(string key) => new LocString("settings_ui", key).GetFormattedText();

    public static string Format(string key, params (string Name, string Value)[] values)
    {
        var text = new LocString("settings_ui", key);
        foreach (var (name, value) in values)
            text.Add(name, value);
        return text.GetFormattedText();
    }

    // Its looks, made once and shared (every row has a few of these, remade on each redraw).
    static readonly (string State, StyleBox Box)[] SmallBoxes =
    [
        ("normal", Box(new Color(0.0756f, 0.12084f, 0.18f, 0.7f), new Color(Gold, 0.35f))),
        ("hover", Box(new Color(0.2f, 0.32f, 0.42f), Gold)),
        ("pressed", Box(new Color(0.12f, 0.2f, 0.28f), Gold)),
        ("disabled", Box(new Color(0.0756f, 0.12084f, 0.18f, 0.35f), new Color(Gold, 0.1f))),
    ];
    static readonly StyleBoxEmpty FlatBox = new() { ContentMarginLeft = 4, ContentMarginRight = 4 };

    static StyleBoxFlat Box(Color background, Color border)
    {
        var box = new StyleBoxFlat { BgColor = background, BorderColor = border, ContentMarginLeft = 8, ContentMarginRight = 8, ContentMarginTop = 0, ContentMarginBottom = 0 };
        box.SetBorderWidthAll(1);
        box.SetCornerRadiusAll(3);
        return box;
    }

    // A plain, compact text button for inline actions: no art, a thin border, lit on hover.
    public static Button SmallButton(string text, Action onPressed, bool disabled = false, string? name = null)
    {
        var button = new Button
        {
            Text = text, Disabled = disabled, FocusMode = Control.FocusModeEnum.None,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
        };
        if (name != null)
            button.Name = name;
        foreach (var (state, box) in SmallBoxes)
            button.AddThemeStyleboxOverride(state, box);
        button.AddThemeFontOverride("font", ResourceLoader.Load<Font>("res://themes/kreon_regular_shared.tres"));
        button.AddThemeFontSizeOverride("font_size", 16);
        button.AddThemeColorOverride("font_color", Cream);
        button.AddThemeColorOverride("font_hover_color", Colors.White);
        button.AddThemeColorOverride("font_disabled_color", new Color(Cream, 0.3f));
        button.Pressed += onPressed;
        return button;
    }

    // Just the text: no box at rest, gold on hover (a quiet button among others, like the row's pencil).
    public static void Flatten(Button button)
    {
        foreach (var state in new[] { "normal", "hover", "pressed", "disabled" })
            button.AddThemeStyleboxOverride(state, FlatBox);
        button.AddThemeColorOverride("font_color", new Color(Cream, 0.6f));
        button.AddThemeColorOverride("font_hover_color", Gold);
        button.AddThemeColorOverride("font_pressed_color", Gold);
        button.AddThemeFontSizeOverride("font_size", 18);
    }

    // The same actions a right-click menu offers, as a row of small buttons (headings are skipped).
    public static HBoxContainer InlineButtons(IEnumerable<MenuItem> items, string name)
    {
        var box = new HBoxContainer { Name = name, MouseFilter = Control.MouseFilterEnum.Ignore, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        box.AddThemeConstantOverride("separation", 4);
        foreach (var item in items)
            if (item.OnSelect is { } action)
            {
                var button = SmallButton(item.Label, action, item.Disabled);
                button.TooltipText = item.Tip ?? "";
                if (item.Flat)
                    Flatten(button);
                button.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
                box.AddChild(button);
            }
        return box;
    }

}
