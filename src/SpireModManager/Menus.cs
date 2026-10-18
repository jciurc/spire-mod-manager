using System.Runtime.CompilerServices;
using Godot;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;

namespace SpireModManager;

// A menu entry, or a heading when it has no action. Detail: dim text on the right; Radio: one of several choices;
// Badge: a mark after the label; Tip: a hover card (inline buttons); Flat: drawn as plain text; External: it opens
// something outside the game, with the external-link mark after the label (menus).
record MenuItem(string Label, Action? OnSelect, bool Checked = false, bool Disabled = false, string? Detail = null,
    bool Radio = false, (string Text, Color Color)? Badge = null, string? Tip = null, bool Flat = false, bool External = false)
{
    public static MenuItem Heading(string label) => new(label, null);
}

static class Menus
{
    static readonly Color Cream = new(1, 0.964706f, 0.886275f);
    static readonly Color Gold = new(0.937f, 0.784f, 0.318f);

    // A right-click menu at the mouse, or from a picker (see MenuPanel).
    public static Node? Show(Node owner, IEnumerable<MenuItem> items, Rect2? from = null, string? toggleKey = null) =>
        MenuPanel.Show(owner, () => items.ToList(), from, toggleKey);

    // The game's yes/no popup with our mark; true if confirmed. no and yes relabel its buttons.
    public static async Task<bool> Confirm(LocString title, LocString body, LocString? no = null, LocString? yes = null)
    {
        if (NGenericPopup.Create() is not { } popup || NModalContainer.Instance is not { } modals)
            return false;
        Mark(popup);
        modals.Add(popup);
        return await popup.WaitForConfirmation(body, title,
            no ?? new LocString("main_menu_ui", "GENERIC_POPUP.cancel"), yes ?? new LocString("main_menu_ui", "GENERIC_POPUP.confirm"));
    }

    public const string MarkName = "SpireModManagerMark";
    const float MarkFromBottom = 48;

    // A small, faint "Spire Mod Manager" under the buttons of our game-style popups, so players can tell where they
    // come from. Anchored inside the popup's art, once per popup.
    public static void Mark(Control popup)
    {
        var art = popup.GetNodeOrNull<Control>("VerticalPopup") ?? popup;
        if (art.HasNode(MarkName))
            return;
        var mark = new Label
        {
            Name = MarkName, Text = Text("SPIRE_MOD_MANAGER.POPUP_MARK"), HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        mark.AddThemeFontOverride("font", ModEntry.Font("res://themes/kreon_regular_shared.tres"));
        mark.AddThemeFontSizeOverride("font_size", 15);
        mark.AddThemeColorOverride("font_color", new Color(Cream, 0.5f));
        art.AddChild(mark);
        // A point halfway across, MarkFromBottom up; growing both ways from it keeps the text centred on it.
        (mark.AnchorLeft, mark.AnchorRight, mark.AnchorTop, mark.AnchorBottom) = (0.5f, 0.5f, 1, 1);
        (mark.OffsetLeft, mark.OffsetRight, mark.OffsetTop, mark.OffsetBottom) = (0, 0, -MarkFromBottom, -MarkFromBottom);
        mark.GrowHorizontal = mark.GrowVertical = Control.GrowDirection.Both;
    }

    // A loc text, looked up once per redraw.
    public static string Text(string key) => Lookups.Current?.Text(key, Lookup) ?? Lookup(key);

    static string Lookup(string key) => new LocString("settings_ui", key).GetFormattedText();

    public static string Format(string key, params (string Name, string Value)[] values)
    {
        var text = new LocString("settings_ui", key);
        foreach (var (name, value) in values)
            text.Add(name, value);
        return text.GetFormattedText();
    }

    // The game's language as a culture, for the dates and numbers we format ourselves.
    public static System.Globalization.CultureInfo Culture => LocManager.Instance.CultureInfo;

    // A count in the game's language: "1,234" in English, "1.234" in German (no separator under 1,000).
    public static string Number<T>(T count) where T : IFormattable => count.ToString("N0", Culture);

    // Shared looks, made once (every row has a few of these buttons).
    static readonly (string State, StyleBox Box)[] SmallBoxes =
    [
        ("normal", Box(new Color(0.0756f, 0.12084f, 0.18f, 0.7f), new Color(Gold, 0.35f))),
        ("hover", Box(new Color(0.2f, 0.32f, 0.42f), Gold)),
        ("pressed", Box(new Color(0.12f, 0.2f, 0.28f), Gold)),
        ("disabled", Box(new Color(0.0756f, 0.12084f, 0.18f, 0.35f), new Color(Gold, 0.1f))),
    ];
    static readonly StyleBoxEmpty FlatBox = new() { ContentMarginLeft = 4, ContentMarginRight = 4 };
    // ChoiceButton's chosen one: gold, a shade brighter under the mouse (the same size as the others).
    static readonly (string State, StyleBox Box)[] ChosenBoxes =
    [
        ("normal", Box(new Color(Gold, 0.3f), Gold)),
        ("hover", Box(new Color(Gold, 0.42f), Gold)),
        ("pressed", Box(new Color(Gold, 0.3f), Gold)),
    ];

    static StyleBoxFlat Box(Color background, Color border)
    {
        var box = new StyleBoxFlat { BgColor = background, BorderColor = border, ContentMarginLeft = 8, ContentMarginRight = 8, ContentMarginTop = 0, ContentMarginBottom = 0 };
        box.SetBorderWidthAll(1);
        box.SetCornerRadiusAll(3);
        return box;
    }

    // A small button's look and a flat one's (Flatten), each a Theme set on the button: one property instead of a
    // dozen overrides, on each of the ~500 buttons a 248-mod list makes (#46). What a look leaves out comes from the
    // page, as it did. Made again when the font changes (with the game's language); buttons made before keep theirs.
    static (Font Font, Theme Small, Theme Flat)? _looks;

    static (Theme Small, Theme Flat) Looks()
    {
        var font = ModEntry.Font("res://themes/kreon_regular_shared.tres");
        if (_looks is { } made && made.Font == font)
            return (made.Small, made.Flat);
        Theme Make(IEnumerable<(string State, StyleBox Box)> boxes, int size, params (string Name, Color Color)[] colors)
        {
            var theme = new Theme();
            foreach (var (state, box) in boxes)
                theme.SetStylebox(state, "Button", box);
            theme.SetFont("font", "Button", font);
            theme.SetFontSize("font_size", "Button", size);
            foreach (var (name, color) in colors)
                theme.SetColor(name, "Button", color);
            return theme;
        }
        var small = Make(SmallBoxes, 16, ("font_color", Cream), ("font_hover_color", Colors.White), ("font_disabled_color", new Color(Cream, 0.3f)));
        // Flat: no box, the text bigger and dimmer, gold under the mouse; disabled as a small one.
        var flat = Make(new[] { "normal", "hover", "pressed", "disabled" }.Select(state => (state, (StyleBox)FlatBox)), 18,
            ("font_color", new Color(Cream, 0.6f)), ("font_hover_color", Gold), ("font_pressed_color", Gold), ("font_disabled_color", new Color(Cream, 0.3f)));
        _looks = (font, small, flat);
        return (small, flat);
    }

    // A plain, compact text button for inline actions: no art, a thin border, lit on hover.
    public static Button SmallButton(string text, Action onPressed, bool disabled = false, string? name = null)
    {
        var button = new Button
        {
            Text = text, Disabled = disabled, FocusMode = Control.FocusModeEnum.None,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand, Theme = Looks().Small,
        };
        if (name != null)
            button.Name = name;
        button.Pressed += onPressed;
        return button;
    }

    // One of a few side-by-side choices (the page bar's Show:): the chosen one lit gold, and it still presses.
    public static Button ChoiceButton(string text, bool chosen, Action onPressed, string? name = null)
    {
        var button = SmallButton(text, onPressed, name: name);
        if (!chosen)
            return button;
        foreach (var (state, box) in ChosenBoxes)
            button.AddThemeStyleboxOverride(state, box);
        button.AddThemeColorOverride("font_color", Colors.White);
        return button;
    }

    // Just the text: no box at rest, gold on hover (a quiet button among others, like the row's pencil). For a
    // SmallButton: its look swapped for the flat one.
    public static void Flatten(Button button) => button.Theme = Looks().Flat;

    static readonly StringName Separation = "separation"; // made once: every row's buttons set it (a text is converted anew each time)

    // The same actions a right-click menu offers, as a row of small buttons (headings are skipped).
    public static HBoxContainer InlineButtons(IEnumerable<MenuItem> items, string name)
    {
        var box = new HBoxContainer { Name = name, MouseFilter = Control.MouseFilterEnum.Ignore, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        box.AddThemeConstantOverride(Separation, 4);
        foreach (var item in items)
            if (item.OnSelect is { } action)
            {
                // A press runs the button's action as it is then: Refill can give it another.
                var does = new StrongBox<Action?>(action);
                var button = SmallButton(item.Label, () => does.Value?.Invoke(), item.Disabled);
                Pressing.Add(button, does);
                Hover.Tip(button, item.Tip);
                if (item.Flat)
                    Flatten(button);
                button.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
                box.AddChild(button);
            }
        return box;
    }

    static readonly ConditionalWeakTable<Button, StrongBox<Action?>> Pressing = new();

    // InlineButtons' buttons given other items in place (their words, states, tips and actions) rather than made
    // again: in Load Order every move changes what Up and Down do on each row it passes. False, nothing changed,
    // unless there are as many buttons as items, each in the look its item wants.
    public static bool Refill(HBoxContainer box, IEnumerable<MenuItem> items)
    {
        var wanted = items.Where(i => i.OnSelect != null).ToList();
        var buttons = box.GetChildren().OfType<Button>().ToList();
        var looks = Looks();
        if (buttons.Count != box.GetChildCount() || buttons.Count != wanted.Count
            || buttons.Zip(wanted).Any(p => !Pressing.TryGetValue(p.First, out _) || p.First.Theme != (p.Second.Flat ? looks.Flat : looks.Small)))
            return false;
        Rebind(box, wanted);
        foreach (var (button, item) in buttons.Zip(wanted))
        {
            (button.Text, button.Disabled) = (item.Label, item.Disabled);
            Hover.Tip(button, item.Tip);
        }
        return true;
    }

    // InlineButtons' buttons given these items' actions only, for buttons that already show the items' words, states
    // and tips: an action holds what its redraw knew (Load Order's rows), so one kept from an earlier redraw goes stale.
    public static void Rebind(HBoxContainer box, IEnumerable<MenuItem> items)
    {
        foreach (var (button, item) in box.GetChildren().OfType<Button>().Zip(items.Where(i => i.OnSelect != null)))
            if (Pressing.TryGetValue(button, out var does))
                does.Value = item.OnSelect;
    }
}
