using Godot;

namespace SpireModManager;

// A short list under a tags box while it has focus: the player's own tags already on mods, most used first, for
// the word being typed (the text after the last comma, any case): tags that start with it, then tags that only
// contain it. Tags already in the box aren't offered (a tag typed out in full counts), and the automatic tags
// never are (they aren't stored). At most Max; hidden when nothing matches, gone when the box loses focus.
// - Up/Down move the highlight; pointing at a row lights it too. Enter, Tab, a comma or a click take the lit tag:
//   it replaces the word being typed, followed by ", " for the next one. Nothing is lit until then, so Enter
//   still saves, Tab still moves on and a comma is just a comma.
// - Escape closes the list but stays in the box (another Escape cancels the edit, as before). Typing, or an
//   arrow, brings it back.
// It looks like our menus (MenuPanel): under the box and as wide (above it when there's no room below), kept on
// screen, on its own layer over our menus, since the box can be in one (a mod's right-click menu, the bulk menu).
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

    // For the self-test: the list showing (its layer), its tags top to bottom, the lit one, and a click on one.
    public static CanvasLayer? Current => _open?._layer;

    public static List<string> Labels() => _open?._list?.GetChildren().Select(row => (string)row.GetMeta(TagMeta)).ToList() ?? [];

    public static string? Highlighted => _open is { } open && open._highlight >= 0 && open._highlight < open._tags.Count ? open._tags[open._highlight] : null;

    public static void Press(string tag)
    {
        if (_open?._list?.GetChildren().FirstOrDefault(row => (string)row.GetMeta(TagMeta) == tag) is Control row)
            row.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false });
    }

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
        var take = key.Keycode is Key.Enter or Key.KpEnter || (key.Keycode == Key.Tab && !key.ShiftPressed) || key.Unicode == ',';
        if (!take || _highlight < 0 || _highlight >= _tags.Count)
            return false;
        Pick(_tags[_highlight]);
        return true;
    }

    // The tags to offer for the box's text, in order (see the top). Most used means on the most mods: a mod counts
    // once for a tag, even when its saved list has it twice in different cases (a hand-edited file can).
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

    // The tag in place of the word being typed, then ", " for the next one.
    void Pick(string tag)
    {
        var text = _box.Text;
        var cut = text.LastIndexOf(',');
        _box.Text = (cut < 0 ? "" : text[..(cut + 1)] + " ") + tag + ", ";
        _box.CaretColumn = _box.Text.Length;
        Update(); // (setting the text doesn't count as typing)
    }

    // The rows for _tags; the list itself first if it isn't showing.
    void Build()
    {
        if (_layer == null || _list == null || !GodotObject.IsInstanceValid(_layer))
        {
            if (_open != this)
                _open?.Close();
            // On the window, not inside the page or a menu: over our menus (layer 90) and hover cards (80).
            var layer = _layer = new CanvasLayer { Name = LayerName, Layer = 95 };
            var panel = new PanelContainer { Name = "Panel" };
            panel.AddThemeStyleboxOverride("panel", PanelBox);
            _list = new VBoxContainer { Name = "Tags" };
            _list.AddThemeConstantOverride("separation", 2);
            panel.AddChild(_list);
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
        var label = new Label { Text = tag, ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis, MouseFilter = Control.MouseFilterEnum.Ignore };
        label.AddThemeFontOverride("font", ModEntry.Font("res://themes/kreon_regular_shared.tres"));
        label.AddThemeFontSizeOverride("font_size", 18);
        label.AddThemeColorOverride("font_color", Cream);
        row.AddChild(label);
        // Pointing at a row lights it (what Enter would take); off it, none is. Only when the mouse moves: a row that
        // turns up under a still mouse (the list redrawn after a click) mustn't get Enter's tag.
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
            if (!button.Pressed)
                Pick(tag);
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
