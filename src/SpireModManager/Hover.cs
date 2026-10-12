using Godot;

namespace SpireModManager;

// Hover cards (our tooltips): a dark panel with a few lines of text, or a picture, above everything on the
// page. The rules live here, for every card:
// - a card shows after Delay of hovering what it's about; Instant ones (the row's notepad) at once;
// - once a card has gone, for Grace any other card shows at once (moving along names or tags doesn't wait
//   each time); Instant cards don't start one;
// - leaving: the card goes after WindDown (Instant ones at once), so a quick wobble off the edge keeps it;
// - one card at a time: a newer one closes the one before.
static class Hover
{
    public const double Delay = 0.4, Grace = 0.25, WindDown = 0.02;

    public enum Timing { Normal, Instant }

    static readonly Color Cream = new(1, 0.964706f, 0.886275f);
    static readonly Color Gold = new(0.937f, 0.784f, 0.318f);
    const float PictureMax = 800;

    // Where the mouse is. The self-test sets it: during a test run the real cursor can be anywhere.
    public static Func<Vector2>? TestMouse;
    public static Vector2 Mouse(CanvasItem item) => TestMouse?.Invoke() ?? item.GetGlobalMousePosition();

    static ulong _graceUntil;
    static Node? _shown; // the card showing (its layer), if any

    static bool InGrace => Time.GetTicksMsec() < _graceUntil;

    // A card for `trigger` while the mouse is over it. card: its text, or null for none (asked when it opens).
    // picture: a picture card instead (to the left of the mouse), or null for none. place: where it goes
    // (default: below right of the mouse).
    public static void Watch(Control trigger, string name, Func<string?> card, Timing timing = Timing.Normal, float width = 380,
        Func<Texture2D?>? picture = null, Func<(Vector2 At, bool Right, bool Above)>? place = null)
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
                    Show(owner, name, "", Mouse(trigger) + new Vector2(-16, 20), right: true, keep: Over, timing: timing, picture: image);
                else if (picture == null && card() is { } text)
                {
                    var (at, right, above) = place?.Invoke() ?? (Mouse(trigger) + new Vector2(16, 20), false, false);
                    Show(owner, name, text, at, right, above, width, Over, timing);
                }
            }
            Later(trigger, timing, Open);
        };
        trigger.MouseExited += () =>
        {
            if (!Over())
                token++;
        };
    }

    // The same for [url] links in a text: a card for the link under the mouse. card: its text for the link's
    // meta, or null for none.
    public static void WatchLinks(RichTextLabel label, string name, Func<string, string?> card, float width = 380)
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
            Later(label, Timing.Normal, () =>
            {
                if (mine == token && Over(link) && card(link) is { } text && Owner(label) is { } owner)
                    Show(owner, name, text, Mouse(label) + new Vector2(16, 20), width: width, keep: () => Over(link));
            });
        };
        label.MetaHoverEnded += _ =>
        {
            current = null;
            token++;
        };
    }

    // Now (an Instant card, or in a grace period) or after the delay.
    static void Later(Node node, Timing timing, Action open)
    {
        if (timing == Timing.Instant || InGrace)
            open();
        else
            node.GetTree().CreateTimer(Delay).Timeout += open;
    }

    // Cards belong to the page they're on (they go with it), else to the window.
    static Node? Owner(Node node) => CategoryList.ScreenOf(node) ?? (Node?)node.GetTree()?.Root;

    // at: the card's corner: its top-left, or with right/above, the matching corner. Kept on screen. keep: checked
    // every frame; once it's false (for WindDown, unless Instant) the card goes, so it can't be left behind when
    // the mouse moves on without an exit event, a click opens something else, or the page changes.
    public static void Show(Node owner, string name, string bbcode, Vector2 at, bool right = false, bool above = false, float width = 380,
        Func<bool>? keep = null, Timing timing = Timing.Normal, Texture2D? picture = null)
    {
        Hide(); // one at a time
        var layer = new CanvasLayer { Name = name, Layer = 80 };
        owner.AddChild(layer);
        _shown = layer;
        if (keep != null)
        {
            var tree = owner.GetTree();
            ulong? lostAt = null;
            void Check()
            {
                if (GodotObject.IsInstanceValid(owner) && owner.IsInsideTree() && keep())
                {
                    lostAt = null;
                    return;
                }
                var now = Time.GetTicksMsec();
                lostAt ??= now;
                if (timing == Timing.Normal && now - lostAt < WindDown * 1000)
                    return; // winding down: the mouse may come back
                Close(layer);
                if (timing == Timing.Normal)
                    _graceUntil = now + (ulong)(Grace * 1000);
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

    // Closes the card showing, if any.
    public static void Hide() => Close(_shown);

    static void Close(Node? layer)
    {
        if (layer == null || !GodotObject.IsInstanceValid(layer))
            return;
        if (_shown == layer)
            _shown = null;
        layer.GetParent()?.RemoveChild(layer);
        layer.QueueFree();
    }

    public static void Fonts(RichTextLabel text, int size)
    {
        text.AddThemeFontOverride("normal_font", ResourceLoader.Load<Font>("res://themes/kreon_regular_shared.tres"));
        text.AddThemeFontOverride("bold_font", ResourceLoader.Load<Font>("res://themes/kreon_bold_shared.tres"));
        text.AddThemeFontSizeOverride("normal_font_size", size);
        text.AddThemeFontSizeOverride("bold_font_size", size);
        text.AddThemeColorOverride("default_color", Cream);
    }
}
