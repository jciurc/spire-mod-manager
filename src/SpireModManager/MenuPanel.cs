using Godot;

namespace SpireModManager;

// Our pop-up menus (right-click menus, the profile list), in place of the engine's PopupMenu, which can't
// show these: each row has an optional mark (a dot for the one current choice, a tick otherwise), a
// label, and a dim detail on the right; headings separate groups; and an item can turn the menu into a
// small editor in place, whose Save or Cancel brings the list back. Freed when it closes.
static class MenuPanel
{
    static readonly Color Cream = new(1, 0.964706f, 0.886275f);
    static readonly Color Gold = new(0.937f, 0.784f, 0.318f);
    public const string ItemGroup = "SpireModManagerMenuItem";
    const ulong ReopenGuardMs = 350;

    // When each toggle key's menu last closed: a press on the control that opened it, arriving just after
    // the click that closed it, closes it instead of opening it again.
    static readonly Dictionary<string, ulong> ClosedAt = [];

    public static PopupPanel? Show(Node owner, IReadOnlyList<MenuItem> items, Vector2? at = null, string? toggleKey = null)
    {
        if (toggleKey != null && ClosedAt.TryGetValue(toggleKey, out var closed) && Time.GetTicksMsec() - closed < ReopenGuardMs)
            return null;
        var panel = new PopupPanel { Name = "SpireModManagerMenu", WrapControls = true };
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.0756f, 0.12084f, 0.18f, 0.97f), BorderColor = new Color(Gold, 0.6f),
            ContentMarginLeft = 8, ContentMarginRight = 8, ContentMarginTop = 8, ContentMarginBottom = 8,
        };
        style.SetBorderWidthAll(2);
        style.SetCornerRadiusAll(6);
        panel.AddThemeStyleboxOverride("panel", style);

        var list = new VBoxContainer { Name = "Items" };
        list.AddThemeConstantOverride("separation", 2);
        panel.AddChild(list);
        Fill(panel, list, items);

        panel.PopupHide += () =>
        {
            if (toggleKey != null)
                ClosedAt[toggleKey] = Time.GetTicksMsec();
            panel.QueueFree();
        };
        owner.AddChild(panel);
        var viewport = owner.GetViewport();
        var position = at ?? viewport.GetMousePosition();
        panel.Popup(new Rect2I((Vector2I)position, Vector2I.Zero));
        // Kept on screen.
        var bounds = viewport.GetVisibleRect().Size;
        panel.Position = new Vector2I(Math.Clamp(panel.Position.X, 0, Math.Max(0, (int)bounds.X - panel.Size.X)),
            Math.Clamp(panel.Position.Y, 0, Math.Max(0, (int)bounds.Y - panel.Size.Y)));
        return panel;
    }

    static void Fill(PopupPanel panel, VBoxContainer list, IReadOnlyList<MenuItem> items)
    {
        foreach (var child in list.GetChildren())
        {
            list.RemoveChild(child);
            child.QueueFree();
        }
        var marks = items.Any(i => i.Checked || i.Radio);
        foreach (var item in items)
            list.AddChild(item.OnSelect == null && item.Editor == null ? Heading(item.Label) : Row(panel, list, items, item, marks));
        panel.ResetSize();
    }

    static Control Heading(string text)
    {
        var box = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        box.AddThemeConstantOverride("separation", 2);
        if (text.Length > 0)
        {
            var label = new Label { Text = text, MouseFilter = Control.MouseFilterEnum.Ignore };
            Style(label, "res://themes/kreon_bold_shared.tres", 16, Gold);
            var margin = new MarginContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
            margin.AddThemeConstantOverride("margin_left", 12);
            margin.AddThemeConstantOverride("margin_top", 4);
            margin.AddChild(label);
            box.AddChild(margin);
        }
        box.AddChild(new ColorRect { Color = new Color(Gold, 0.3f), CustomMinimumSize = new Vector2(0, 1), MouseFilter = Control.MouseFilterEnum.Ignore });
        return box;
    }

    // One item: hover lights it, a click runs it and closes the menu (or opens its editor in place).
    static Control Row(PopupPanel panel, VBoxContainer list, IReadOnlyList<MenuItem> items, MenuItem item, bool marks)
    {
        var row = new PanelContainer
        {
            Name = item.Label, MouseFilter = Control.MouseFilterEnum.Stop,
            MouseDefaultCursorShape = item.Disabled ? Control.CursorShape.Arrow : Control.CursorShape.PointingHand,
        };
        row.AddToGroup(ItemGroup);
        row.SetMeta("label", item.Label);
        StyleBoxFlat Box(Color color)
        {
            var box = new StyleBoxFlat { BgColor = color, ContentMarginLeft = 12, ContentMarginRight = 14, ContentMarginTop = 6, ContentMarginBottom = 6 };
            box.SetCornerRadiusAll(4);
            return box;
        }
        var normal = Box(Colors.Transparent);
        var hover = Box(new Color(0.2f, 0.32f, 0.42f));
        row.AddThemeStyleboxOverride("panel", normal);

        var line = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        line.AddThemeConstantOverride("separation", 8);
        row.AddChild(line);
        var color = item.Disabled ? new Color(Cream, 0.35f) : Cream;
        if (marks)
        {
            // A dot where only one can be the choice (profiles, a mod's category), a tick otherwise.
            var mark = new Label
            {
                Text = item.Checked ? item.Radio ? "●" : "✓" : item.Radio ? "○" : "",
                CustomMinimumSize = new Vector2(18, 0), HorizontalAlignment = HorizontalAlignment.Center,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            Style(mark, "res://themes/kreon_regular_shared.tres", 15, item.Checked ? Gold : new Color(Cream, 0.35f));
            line.AddChild(mark);
        }
        var label = new Label { Text = item.Label, MouseFilter = Control.MouseFilterEnum.Ignore, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        Style(label, "res://themes/kreon_regular_shared.tres", 20, item.Checked ? Gold : color);
        line.AddChild(label);
        if (item.Detail is { Length: > 0 } detail)
        {
            var dim = new Label { Text = detail, MouseFilter = Control.MouseFilterEnum.Ignore, VerticalAlignment = VerticalAlignment.Center };
            Style(dim, "res://themes/kreon_regular_shared.tres", 15, new Color(Cream, 0.5f));
            line.AddChild(new Control { CustomMinimumSize = new Vector2(16, 0), MouseFilter = Control.MouseFilterEnum.Ignore });
            line.AddChild(dim);
        }
        if (item.Disabled)
            return row;

        row.MouseEntered += () => row.AddThemeStyleboxOverride("panel", hover);
        row.MouseExited += () => row.AddThemeStyleboxOverride("panel", normal);
        row.GuiInput += e =>
        {
            if (e is not InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false })
                return;
            row.AcceptEvent();
            Choose(panel, list, items, item);
        };
        return row;
    }

    // What a click on an item does: runs it and closes, or swaps the list for its editor.
    static void Choose(PopupPanel panel, VBoxContainer list, IReadOnlyList<MenuItem> items, MenuItem item)
    {
        if (item.Editor is { } editor)
        {
            foreach (var child in list.GetChildren())
            {
                list.RemoveChild(child);
                child.QueueFree();
            }
            void Back()
            {
                if (GodotObject.IsInstanceValid(panel))
                    Fill(panel, list, items);
            }
            list.AddChild(editor(Back, panel.Hide));
            panel.ResetSize();
            return;
        }
        panel.Hide();
        item.OnSelect?.Invoke();
    }

    // For the self-test: the labels of the menu's items, and choosing one as a click would.
    public static List<string> Labels(PopupPanel panel) =>
        panel.FindChildren("*", "", true, false).Where(n => n.IsInGroup(ItemGroup)).Select(n => (string)n.GetMeta("label")).ToList();

    public static void Press(PopupPanel panel, string label)
    {
        if (panel.FindChildren("*", "", true, false).FirstOrDefault(n => n.IsInGroup(ItemGroup) && (string)n.GetMeta("label") == label) is Control row)
            row.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false });
    }

    static void Style(Control control, string font, int size, Color color)
    {
        control.AddThemeFontOverride("font", ResourceLoader.Load<Font>(font));
        control.AddThemeFontSizeOverride("font_size", size);
        control.AddThemeColorOverride("font_color", color);
    }
}
