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

    // Grace period: once a card opened by Watch has gone, for twice its delay any card shows at once (moving
    // along names or tags doesn't wait each time).
    static ulong _graceUntil;
    const float PictureMax = 800;

    // A card over `trigger` after `delay` seconds of hovering it (at once in a grace period); it goes when the
    // mouse leaves. card: its text, or null for none (asked when it opens). picture: a picture card instead (to
    // the left of the mouse), or null for none.
    public static void Watch(Control trigger, string name, double delay, Func<string?> card, float width = 380, Func<Texture2D?>? picture = null)
    {
        bool Over() => GodotObject.IsInstanceValid(trigger) && trigger.IsVisibleInTree() && !MenuPanel.IsOpen
            && trigger.GetGlobalRect().HasPoint(Mouse(trigger));
        var token = 0;
        trigger.MouseEntered += () =>
        {
            var mine = ++token;
            void Open()
            {
                if (mine != token || !Over() || Owner(trigger) is not { } owner)
                    return;
                if (picture?.Invoke() is { } image)
                    Show(owner, name, "", Mouse(trigger) + new Vector2(-16, 20), right: true, keep: Over, grace: delay, picture: image);
                else if (picture == null && card() is { } text)
                    Show(owner, name, text, Mouse(trigger) + new Vector2(16, 20), width: width, keep: Over, grace: delay);
            }
            if (Time.GetTicksMsec() < _graceUntil)
                Open();
            else
                trigger.GetTree().CreateTimer(delay).Timeout += Open;
        };
        trigger.MouseExited += () =>
        {
            if (!Over())
                token++;
        };
    }

    // The same for [url] links in a text: a card for the link under the mouse. card: its text for the link's
    // meta, or null for none.
    public static void WatchLinks(RichTextLabel label, string name, double delay, Func<string, string?> card, float width = 380)
    {
        string? current = null;
        var token = 0;
        bool Over(string meta) => current == meta && GodotObject.IsInstanceValid(label) && label.IsVisibleInTree() && !MenuPanel.IsOpen
            && label.GetGlobalRect().HasPoint(Mouse(label));
        label.MetaHoverStarted += meta =>
        {
            var link = meta.AsString();
            current = link;
            var mine = ++token;
            void Open()
            {
                if (mine == token && Over(link) && card(link) is { } text && Owner(label) is { } owner)
                    Show(owner, name, text, Mouse(label) + new Vector2(16, 20), width: width, keep: () => Over(link), grace: delay);
            }
            if (Time.GetTicksMsec() < _graceUntil)
                Open();
            else
                label.GetTree().CreateTimer(delay).Timeout += Open;
        };
        label.MetaHoverEnded += _ =>
        {
            current = null;
            token++;
        };
    }

    // Cards belong to the page they're on (they go with it), else to the window.
    static Node? Owner(Node node) => CategoryList.ScreenOf(node) ?? (Node?)node.GetTree()?.Root;

    // at: the card's corner: its top-left, or with right/above, the matching corner. Kept on screen. keep: checked
    // every frame; the card goes as soon as it's false (so it can't be left behind when the mouse moves on
    // without an exit event, a click opens something else, or the page changes). grace: the delay it waited
    // (Watch), which starts a grace period when it goes.
    public static void Show(Node owner, string name, string bbcode, Vector2 at, bool right = false, bool above = false, float width = 380,
        Func<bool>? keep = null, double grace = 0, Texture2D? picture = null)
    {
        Hide(owner, name);
        var layer = new CanvasLayer { Name = name, Layer = 80 };
        owner.AddChild(layer);
        if (keep != null)
        {
            var tree = owner.GetTree();
            void Check()
            {
                if (GodotObject.IsInstanceValid(owner) && owner.IsInsideTree() && keep())
                    return;
                Hide(owner, name);
                if (grace > 0)
                    _graceUntil = Time.GetTicksMsec() + (ulong)(grace * 2000);
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
        if (picture != null)
        {
            // The whole picture, at most PictureMax on its longer side.
            var scale = Math.Min(1f, PictureMax / Math.Max(picture.GetWidth(), picture.GetHeight()));
            panel.AddChild(new TextureRect
            {
                Texture = picture, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                CustomMinimumSize = picture.GetSize() * scale, MouseFilter = Control.MouseFilterEnum.Ignore,
            });
        }
        else
        {
            var text = new RichTextLabel
            {
                BbcodeEnabled = true, FitContent = true, ScrollActive = false, CustomMinimumSize = new Vector2(width, 0),
                MouseFilter = Control.MouseFilterEnum.Ignore, Text = bbcode,
            };
            Fonts(text, 17);
            panel.AddChild(text);
        }
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
