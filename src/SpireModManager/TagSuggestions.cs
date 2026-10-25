using Godot;

namespace SpireModManager;

// Suggestions under a tags box while it has focus: the player's own tags, most used first, matching the word being
// typed (starts-with, then contains), under a heading that says so. Up/Down or the mouse light one; Enter, Tab or a
// comma (a full-width one too) take the lit one and keep editing, as one undo step; a click takes one and finishes (the
// box left, so it saves), a Shift-click takes one and keeps the list for more. Nothing is lit until then, so Enter still
// saves. Escape closes the list. Styled like MenuPanel, on its own layer above our menus (the box can be in one).
sealed class TagSuggestions
{
    const int Max = 6;
    const string LayerName = "SpireModManagerTagSuggestions";
    const string TagMeta = "tag";
    const float Gap = 2; // px between the box and the list
    static readonly Color Cream = new(1, 0.964706f, 0.886275f);
    static readonly Color Gold = new(0.937f, 0.784f, 0.318f);

    // MenuPanel's look: its panel, and its rows (lit like a hovered item), a little tighter.
    static readonly StyleBoxFlat PanelBox = MakePanelBox(), Unlit = RowBox(Colors.Transparent), Lit = RowBox(new Color(0.2f, 0.32f, 0.42f));

    static TagSuggestions? _open; // the one showing its list (one box has focus at a time)

    readonly LineEdit _box;
    CanvasLayer? _layer;
    VBoxContainer? _list;
    List<string> _tags = [];
    int _highlight = -1; // in _tags; -1: none lit
    Rect2 _placedBy; // the box's rect when the list was last placed

    public TagSuggestions(LineEdit box)
    {
        _box = box;
        box.FocusEntered += Update;
        box.TextChanged += _ => Update();
        box.FocusExited += Close;
        box.TreeExiting += Close;
    }

#if DEVTOOLS
    // Self-test: the list showing, its tags top to bottom, the lit one, and a click on one.
    public static CanvasLayer? Current => _open?._layer;

    public static List<string> Labels() => _open?._list?.GetChildren().Select(row => (string)row.GetMeta(TagMeta)).ToList() ?? [];

    public static string? Highlighted => _open is { } open && open._highlight >= 0 && open._highlight < open._tags.Count ? open._tags[open._highlight] : null;

    public static void Press(string tag, bool shift = false)
    {
        if (_open?._list?.GetChildren().FirstOrDefault(row => (string)row.GetMeta(TagMeta) == tag) is Control row)
            row.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, ShiftPressed = shift });
    }

    public static string Heading() => _open?._layer?.FindChild("Heading", true, false) is Label heading ? heading.Text : "";
#endif

    // A key pressed in the box: true when the list took it (the box then leaves it alone).
    public bool Use(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true } key || key.CtrlPressed || key.AltPressed || key.MetaPressed)
            return false;
        if (key.Keycode is Key.Down or Key.Up)
        {
            if (_layer == null)
                Update(); // an arrow brings back a list closed with Escape
            if (_tags.Count == 0)
                return false;
            var down = key.Keycode == Key.Down;
            Light(_highlight < 0 ? (down ? 0 : _tags.Count - 1) : (_highlight + (down ? 1 : _tags.Count - 1)) % _tags.Count);
            return true;
        }
        if (key.Keycode == Key.Escape && _layer != null)
        {
            Close();
            return true;
        }
        // Shift+Tab still goes back a field.
        var take = key.Keycode is Key.Enter or Key.KpEnter || (key.Keycode == Key.Tab && !key.ShiftPressed) || TagPills.Comma(key.Unicode);
        if (!take || _highlight < 0 || _highlight >= _tags.Count)
            return false;
        Pick(_tags[_highlight]);
        return true;
    }

    // The tags to offer for the box's text, in order; a tag counts once per mod, whatever its case.
    static List<string> For(string text)
    {
        var typed = text[(text.LastIndexOf(',') + 1)..].Trim();
        var have = Tags.Parse(text) ?? [];
        return Store.Data.Mods.Values.SelectMany(info => (info.Tags ?? []).Distinct(StringComparer.OrdinalIgnoreCase))
            .GroupBy(tag => tag, StringComparer.OrdinalIgnoreCase)
            .Where(used => used.Key.Contains(typed, StringComparison.OrdinalIgnoreCase) && !have.Contains(used.Key, StringComparer.OrdinalIgnoreCase))
            .OrderBy(used => used.Key.StartsWith(typed, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenByDescending(used => used.Count())
            .ThenBy(used => used.Key, StringComparer.OrdinalIgnoreCase)
            .Select(used => used.Key).Take(Max).ToList();
    }

    // The list for the box's text now, nothing lit; closed when there's nothing to offer.
    void Update()
    {
        _tags = _box.HasFocus() ? For(_box.Text) : [];
        _highlight = -1;
        if (_tags.Count == 0)
            Close();
        else
            Build();
    }

    // The tag in place of the word being typed, then ", " for the next one; Ctrl+Z takes it back.
    void Pick(string tag)
    {
        var text = _box.Text;
        var cut = text.LastIndexOf(',') + 1; // in chars, where Replace takes the box's columns
        TagPills.Replace(_box, TagPills.Column(text, cut), TagPills.Columns(text), (cut == 0 ? "" : " ") + tag + ", ");
        Update(); // now: the box says its text changed only at the frame's end
    }

    // The rows for _tags; the list itself first if it isn't showing.
    void Build()
    {
        if (_layer == null || _list == null || !GodotObject.IsInstanceValid(_layer))
        {
            if (_open != this)
                _open?.Close();
            // On the window, not inside the page or a menu: over our menus and every hover card, a menu's own too.
            var layer = _layer = new CanvasLayer { Name = LayerName, Layer = MenuPanel.Depth + 5 };
            var panel = new PanelContainer { Name = "Panel" };
            panel.AddThemeStyleboxOverride("panel", PanelBox);
            var column = new VBoxContainer { Name = "Column" };
            column.AddThemeConstantOverride("separation", 4);
            // What these are and how to take several (Jordan's words), over a faint line.
            // One line, so the list's height is known when it's placed (a wrapping label's isn't until it's laid out);
            // the list widens for it under a narrow box.
            var heading = new Label { Name = "Heading", Text = Menus.Text("SPIRE_MOD_MANAGER.TAG_SUGGESTIONS_HEADING"), MouseFilter = Control.MouseFilterEnum.Ignore };
            heading.AddThemeFontOverride("font", ModEntry.Font("res://themes/kreon_regular_shared.tres"));
            heading.AddThemeFontSizeOverride("font_size", 14);
            heading.AddThemeColorOverride("font_color", new Color(Cream, 0.6f));
            var headingBox = new MarginContainer { Name = "HeadingBox", MouseFilter = Control.MouseFilterEnum.Ignore };
            headingBox.AddThemeConstantOverride("margin_left", 10);
            headingBox.AddThemeConstantOverride("margin_right", 10);
            headingBox.AddChild(heading);
            column.AddChild(headingBox);
            var line = new ColorRect { Name = "Divider", Color = new Color(Gold, 0.25f), CustomMinimumSize = new Vector2(0, 1), MouseFilter = Control.MouseFilterEnum.Ignore };
            column.AddChild(line);
            _list = new VBoxContainer { Name = "Tags" };
            _list.AddThemeConstantOverride("separation", 2);
            column.AddChild(_list);
            panel.AddChild(column);
            layer.AddChild(panel);
            panel.MinimumSizeChanged += Place;
            var tree = _box.GetTree();
            tree.Root.AddChild(layer);
            // Every frame: it follows the box when that moves (the menu placed, the panel laid out again).
            void Follow()
            {
                if (layer == _layer && GodotObject.IsInstanceValid(_box) && _box.GetGlobalRect() != _placedBy)
                    Place();
            }
            tree.ProcessFrame += Follow;
            layer.TreeExiting += () => tree.ProcessFrame -= Follow;
            _open = this;
        }
        foreach (var old in _list.GetChildren())
        {
            _list.RemoveChild(old);
            old.QueueFree();
        }
        for (var i = 0; i < _tags.Count; i++)
            _list.AddChild(Row(i));
        Paint();
        Place();
    }

    Control Row(int index)
    {
        var tag = _tags[index];
        var row = new PanelContainer
        {
            Name = $"Tag{index}", MouseFilter = Control.MouseFilterEnum.Stop, MouseDefaultCursorShape = Control.CursorShape.PointingHand,
        };
        row.SetMeta(TagMeta, tag);
        var label = new Label { MouseFilter = Control.MouseFilterEnum.Ignore };
        Menus.CutToFit(label, tag);
        label.AddThemeFontOverride("font", ModEntry.Font("res://themes/kreon_regular_shared.tres"));
        label.AddThemeFontSizeOverride("font_size", 18);
        label.AddThemeColorOverride("font_color", Cream);
        row.AddChild(label);
        // Pointing at a row lights it, only when the mouse moves: a row appearing under a still mouse mustn't take Enter.
        row.MouseExited += () =>
        {
            if (_highlight == index)
                Light(-1);
        };
        row.GuiInput += e =>
        {
            if (e is InputEventMouseMotion && _highlight != index)
                Light(index);
            if (e is not InputEventMouseButton { ButtonIndex: MouseButton.Left } button)
                return;
            row.AcceptEvent(); // nothing underneath gets it; the box keeps focus (rows don't take it)
            if (button.Pressed)
                return;
            Pick(tag);
            // A click finishes: the box left, which saves and closes the list. Shift: more to take, the list stays.
            if (!button.ShiftPressed)
                _box.ReleaseFocus();
        };
        return row;
    }

    void Light(int index)
    {
        _highlight = index;
        Paint();
    }

    void Paint()
    {
        if (_list == null)
            return;
        var rows = _list.GetChildren();
        for (var i = 0; i < rows.Count; i++)
            ((Control)rows[i]).AddThemeStyleboxOverride("panel", i == _highlight ? Lit : Unlit);
    }

    // Under the box and as wide, or above it when there's no room below; kept on screen.
    void Place()
    {
        if (_layer?.GetNodeOrNull<Control>("Panel") is not { } panel || !GodotObject.IsInstanceValid(_box))
            return;
        var box = _placedBy = _box.GetGlobalRect();
        panel.CustomMinimumSize = new Vector2(box.Size.X, 0);
        var size = panel.GetCombinedMinimumSize();
        panel.Size = size;
        var screen = panel.GetViewportRect().Size;
        var y = box.End.Y + Gap + size.Y <= screen.Y ? box.End.Y + Gap : box.Position.Y - Gap - size.Y;
        panel.Position = new Vector2(Math.Clamp(box.Position.X, 0, Math.Max(0, screen.X - size.X)), Math.Clamp(y, 0, Math.Max(0, screen.Y - size.Y)));
    }

    // Takes the list away: the box lost focus or left, Escape, or nothing to offer.
    void Close()
    {
        _tags = [];
        _highlight = -1;
        if (_open == this)
            _open = null;
        if (_layer != null && GodotObject.IsInstanceValid(_layer))
        {
            // Hidden now and freed at the end of the frame rather than removed: the box may be leaving the tree with
            // the menu it's in, and the window is busy removing that.
            _layer.Visible = false;
            _layer.QueueFree();
        }
        _layer = null;
        _list = null;
    }

    static StyleBoxFlat MakePanelBox()
    {
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.0756f, 0.12084f, 0.18f, 0.98f), BorderColor = new Color(Gold, 0.6f),
            ContentMarginLeft = 6, ContentMarginRight = 6, ContentMarginTop = 6, ContentMarginBottom = 6,
            ShadowColor = new Color(0, 0, 0, 0.4f), ShadowSize = 8,
        };
        style.SetBorderWidthAll(2);
        style.SetCornerRadiusAll(6);
        return style;
    }

    static StyleBoxFlat RowBox(Color color)
    {
        var box = new StyleBoxFlat { BgColor = color, ContentMarginLeft = 10, ContentMarginRight = 10, ContentMarginTop = 3, ContentMarginBottom = 3 };
        box.SetCornerRadiusAll(4);
        return box;
    }
}
