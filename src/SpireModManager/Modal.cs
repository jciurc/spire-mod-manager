using Godot;

namespace SpireModManager;

// The frame of our dialogs over the page (RelaunchCheck, ProfileShare): a layer over everything, dimming the page
// and taking its clicks, with a panel in the middle. One at a time: opening one closes any other. It takes keyboard
// focus from the page too, so Esc and typing reach the dialog, not a box behind it (the search box would take Esc to
// clear itself, and the dialog would stay open). It closes when the page that opened it goes.
static class Modal
{
    public static readonly Color Cream = new(1, 0.964706f, 0.886275f);
    public static readonly Color Gold = new(0.937f, 0.784f, 0.318f);

    // The open dialogs' owners, so closing one stops listening for its owner leaving.
    static readonly Dictionary<string, (Node Owner, Callable Gone)> _owners = [];

    // A dialog named name, width wide; returns the column to fill. Null if there's nowhere to show it.
    public static VBoxContainer? Open(Node owner, string name, float width, int separation)
    {
        foreach (var open in _owners.Keys.ToList())
            Close(open);
        if (Root() is not { } root)
            return null;
        var layer = new CanvasLayer { Name = name, Layer = 85 };
        root.AddChild(layer);
        var gone = Callable.From(() => Close(name));
        owner.Connect(Node.SignalName.TreeExiting, gone, (uint)GodotObject.ConnectFlags.OneShot);
        _owners[name] = (owner, gone);
        owner.GetViewport().GuiReleaseFocus();

        // Dim everything behind, and take its clicks.
        var size = owner.GetViewport().GetVisibleRect().Size; // anchors don't size controls right under a CanvasLayer here
        layer.AddChild(new ColorRect { Color = new Color(0, 0, 0, 0.6f), MouseFilter = Control.MouseFilterEnum.Stop, Size = size });
        var center = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore, Size = size };
        layer.AddChild(center);

        var panel = new PanelContainer { CustomMinimumSize = new Vector2(width, 0) };
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.05f, 0.08f, 0.12f, 0.98f), BorderColor = new Color(Gold, 0.5f),
            ContentMarginLeft = 26, ContentMarginRight = 26, ContentMarginTop = 20, ContentMarginBottom = 20,
            ShadowColor = new Color(0, 0, 0, 0.4f), ShadowSize = 10,
        };
        style.SetBorderWidthAll(1);
        style.SetCornerRadiusAll(8);
        panel.AddThemeStyleboxOverride("panel", style);
        center.AddChild(panel);
        var column = new VBoxContainer { Name = "Column" };
        column.AddThemeConstantOverride("separation", separation);
        panel.AddChild(column);
        return column;
    }

    // The dialog's title, for the top of its column.
    public static Label Title(string key)
    {
        var title = new Label { Name = "Title", Text = Menus.Text(key) };
        title.AddThemeFontOverride("font", ModEntry.Font("res://themes/kreon_bold_shared.tres"));
        title.AddThemeFontSizeOverride("font_size", 26);
        title.AddThemeColorOverride("font_color", Cream);
        return title;
    }

    public static bool IsOpen(string name) => Root()?.GetNodeOrNull(name) != null;

    // True when there was one to close.
    public static bool Close(string name)
    {
        if (_owners.Remove(name, out var owner) && GodotObject.IsInstanceValid(owner.Owner)
            && owner.Owner.IsConnected(Node.SignalName.TreeExiting, owner.Gone))
            owner.Owner.Disconnect(Node.SignalName.TreeExiting, owner.Gone);
        if (Root()?.GetNodeOrNull(name) is not { } layer)
            return false;
        layer.GetParent().RemoveChild(layer);
        layer.QueueFree();
        return true;
    }

    static Node? Root() => (Engine.GetMainLoop() as SceneTree)?.Root;
}
