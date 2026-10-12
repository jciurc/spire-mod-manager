using Godot;

namespace SpireModManager;

// Small hover cards: a dark panel with a few lines of text, above everything else on the page. Used for the
// Changes summary, a profile's unsaved changes, and a mod's notes. One card per name at a time.
static class Hover
{
    static readonly Color Cream = new(1, 0.964706f, 0.886275f);
    static readonly Color Gold = new(0.937f, 0.784f, 0.318f);

    // Where the mouse is. The self-test sets it: during a test run the real cursor can be anywhere.
    public static Func<Vector2>? TestMouse;
    public static Vector2 Mouse(CanvasItem item) => TestMouse?.Invoke() ?? item.GetGlobalMousePosition();

    // at: the card's corner: its top-left, or with right/above, the matching corner. Kept on screen. keep: checked
    // every frame; the card goes as soon as it's false (so it can't be left behind when the mouse moves on
    // without an exit event, a click opens something else, or the page changes).
    public static void Show(Node owner, string name, string bbcode, Vector2 at, bool right = false, bool above = false, float width = 380,
        Func<bool>? keep = null)
    {
        Hide(owner, name);
        var layer = new CanvasLayer { Name = name, Layer = 80 };
        owner.AddChild(layer);
        if (keep != null)
        {
            var tree = owner.GetTree();
            void Check()
            {
                if (!GodotObject.IsInstanceValid(owner) || !owner.IsInsideTree() || !keep())
                    Hide(owner, name);
            }
            tree.ProcessFrame += Check;
            layer.TreeExiting += () => tree.ProcessFrame -= Check;
        }
        var panel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.05f, 0.08f, 0.12f, 0.97f), BorderColor = new Color(Gold, 0.5f),
            ContentMarginLeft = 16, ContentMarginRight = 16, ContentMarginTop = 10, ContentMarginBottom = 10,
            ShadowColor = new Color(0, 0, 0, 0.35f), ShadowSize = 6,
        };
        style.SetBorderWidthAll(1);
        style.SetCornerRadiusAll(6);
        panel.AddThemeStyleboxOverride("panel", style);
        var text = new RichTextLabel
        {
            BbcodeEnabled = true, FitContent = true, ScrollActive = false, CustomMinimumSize = new Vector2(width, 0),
            MouseFilter = Control.MouseFilterEnum.Ignore, Text = bbcode,
        };
        Fonts(text, 17);
        panel.AddChild(text);
        layer.AddChild(panel);
        panel.Position = at;
        Callable.From(() =>
        {
            if (!GodotObject.IsInstanceValid(panel))
                return;
            var size = panel.GetCombinedMinimumSize();
            panel.Size = size;
            var screen = panel.GetViewportRect().Size;
            var position = new Vector2(right ? at.X - size.X : at.X, above ? at.Y - size.Y : at.Y);
            panel.Position = new Vector2(Math.Clamp(position.X, 0, Math.Max(0, screen.X - size.X)), Math.Clamp(position.Y, 0, Math.Max(0, screen.Y - size.Y)));
        }).CallDeferred();
    }

    public static void Hide(Node owner, string name)
    {
        if (owner.GetNodeOrNull(name) is not { } old)
            return;
        owner.RemoveChild(old);
        old.QueueFree();
    }

    public static bool Showing(Node owner, string name) => owner.HasNode(name);

    public static void Fonts(RichTextLabel text, int size)
    {
        text.AddThemeFontOverride("normal_font", ResourceLoader.Load<Font>("res://themes/kreon_regular_shared.tres"));
        text.AddThemeFontOverride("bold_font", ResourceLoader.Load<Font>("res://themes/kreon_bold_shared.tres"));
        text.AddThemeFontSizeOverride("normal_font_size", size);
        text.AddThemeFontSizeOverride("bold_font_size", size);
        text.AddThemeColorOverride("default_color", Cream);
    }
}
