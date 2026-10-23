using Godot;

namespace SpireModManager;

// Our dialogs' frame (RelaunchCheck, ProfileShare, MissingMods, ModifiedContent): a dimming layer that takes the page's
// clicks, with a centred panel. One open at a time; Esc and F3 close it (ShortcutsPatch.CloseOurs), and so does its owner
// leaving.
static class Modal
{
    public static readonly Color Cream = new(1, 0.964706f, 0.886275f);
    public static readonly Color Gold = new(0.937f, 0.784f, 0.318f);

    // Each open dialog's owner and its leave handler.
    static readonly Dictionary<string, (Node Owner, Callable Gone)> _owners = [];

    // Opens a dialog and returns the column to fill, or null if there's nowhere to show it.
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

    // The dialog's title, for the top of its column; with values, its text filled in with them (a name in it wraps).
    public static Label Title(string key, params (string Name, string Value)[] values)
    {
        var title = new Label
        {
            Name = "Title", Text = values.Length > 0 ? Menus.Format(key, values) : Menus.Text(key),
            AutowrapMode = values.Length > 0 ? TextServer.AutowrapMode.WordSmart : TextServer.AutowrapMode.Off,
        };
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
