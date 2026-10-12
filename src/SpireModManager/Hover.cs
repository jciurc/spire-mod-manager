using Godot;

namespace SpireModManager;

// Hover cards (our tooltips): a dark panel with a few lines of text, or a picture, above everything on the
// page. The rules live here, for every card:
// - a card shows after Delay of hovering what it's about; Instant ones (the row's notepad) at once; Slow ones
//   (the whole of a text cut short, in a plain tooltip) after SlowDelay, with no grace either way;
// - once a card has gone, for Grace any other card shows at once (moving along names or tags doesn't wait
//   each time); Instant cards don't start one;
// - leaving: the card goes after WindDown (Instant ones at once), so a quick wobble off the edge keeps it;
// - one card at a time: a newer one closes the one before.
static class Hover
{
    public const double Delay = 0.4, Grace = 0.25, WindDown = 0.02, SlowDelay = 1.0;

    public enum Timing { Normal, Instant, Slow }

    static readonly Color Cream = new(1, 0.964706f, 0.886275f);
    static readonly Color Gold = new(0.937f, 0.784f, 0.318f);
    const float PictureMax = 800, PlainMax = 520, FullWidth = 640;

    // Where the mouse is. The self-test sets it: during a test run the real cursor can be anywhere.
    public static Func<Vector2>? TestMouse;
    public static Vector2 Mouse(CanvasItem item) => TestMouse?.Invoke() ?? item.GetGlobalMousePosition();

    static ulong _graceUntil;
    static Node? _shown; // the card showing (its layer), if any

    // In a grace period, or a card still showing (winding down, or about to be replaced): the next shows at once.
    static bool InGrace => Time.GetTicksMsec() < _graceUntil || (_shown != null && GodotObject.IsInstanceValid(_shown));

    // A card for `trigger` while the mouse is over it. card: its text, or null for none (asked when it opens).
    // picture: a picture card instead (to the left of the mouse), or null for none. place: where it goes
    // (default: below right of the mouse). full: the whole text, when the card's is cut short (Alt shows it).
    public static void Watch(Control trigger, string name, Func<string?> card, Timing timing = Timing.Normal, float width = 380,
        Func<Texture2D?>? picture = null, Func<(Vector2 At, bool Right, bool Above)>? place = null, Func<string?>? full = null)
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
                    Show(owner, name, text, at, right, above, width, Over, timing, full: full?.Invoke());
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
    public static void WatchLinks(RichTextLabel label, string name, Func<string, string?> card, float width = 380, Func<string, string?>? full = null)
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
                    Show(owner, name, text, Mouse(label) + new Vector2(16, 20), width: width, keep: () => Over(link), full: full?.Invoke(link));
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
        if (timing == Timing.Instant || (timing == Timing.Normal && InGrace))
            open();
        else
            node.GetTree().CreateTimer(timing == Timing.Slow ? SlowDelay : Delay).Timeout += open;
    }

    // Cards belong to the page they're on (they go with it), else to the window.
    static Node? Owner(Node node) => CategoryList.ScreenOf(node) ?? (Node?)node.GetTree()?.Root;

    // at: the card's corner: its top-left, or with right/above, the matching corner. Kept on screen. keep: checked
    // every frame; once it's false (for WindDown, unless Instant) the card goes, so it can't be left behind when
    // the mouse moves on without an exit event, a click opens something else, or the page changes. full: the
    // whole text when the card's is cut short: a hint says to hold Alt, and Alt shows it in a wider card.
    public static void Show(Node owner, string name, string bbcode, Vector2 at, bool right = false, bool above = false, float width = 380,
        Func<bool>? keep = null, Timing timing = Timing.Normal, Texture2D? picture = null, string? full = null)
    {
        Hide(); // one at a time
        var layer = new CanvasLayer { Name = name, Layer = 80 };
        owner.AddChild(layer);
        _shown = layer;
        var plain = timing == Timing.Slow;
        var panel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        var style = plain
            ? new StyleBoxFlat
            {
                // The full text of something cut short: a plain, quiet tooltip.
                BgColor = new Color(0.1f, 0.11f, 0.13f, 0.97f), BorderColor = new Color(0.4f, 0.42f, 0.46f),
                ContentMarginLeft = 8, ContentMarginRight = 8, ContentMarginTop = 4, ContentMarginBottom = 4,
            }
            : new StyleBoxFlat
            {
                BgColor = new Color(0.05f, 0.08f, 0.12f, 0.97f), BorderColor = new Color(Gold, 0.5f),
                ContentMarginLeft = 16, ContentMarginRight = 16, ContentMarginTop = 10, ContentMarginBottom = 10,
                ShadowColor = new Color(0, 0, 0, 0.35f), ShadowSize = 6,
            };
        style.SetBorderWidthAll(1);
        style.SetCornerRadiusAll(plain ? 3 : 6);
        panel.AddThemeStyleboxOverride("panel", style);
        RichTextLabel? text = null;
        var cut = full != null && full != bbcode;
        var shortText = cut ? $"{bbcode}\n\n[b][font_size=14][color=#fff6e280]{Menus.Text("SPIRE_MOD_MANAGER.HOLD_ALT")}[/color][/font_size][/b]" : bbcode;
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
            text = new RichTextLabel
            {
                BbcodeEnabled = true, FitContent = true, ScrollActive = false, MouseFilter = Control.MouseFilterEnum.Ignore, Text = shortText,
            };
            Fonts(text, plain ? 15 : 17);
            if (plain)
            {
                // As wide as the text, up to PlainMax; longer text wraps.
                var wide = text.GetThemeFont("normal_font").GetStringSize(bbcode, fontSize: 15).X + 4;
                (text.AutowrapMode, text.CustomMinimumSize) = wide <= PlainMax ? (TextServer.AutowrapMode.Off, new Vector2(wide, 0)) : (TextServer.AutowrapMode.WordSmart, new Vector2(PlainMax, 0));
            }
            else
                text.CustomMinimumSize = new Vector2(width, 0);
            panel.AddChild(text);
        }
        layer.AddChild(panel);
        panel.Position = at;
        void Place()
        {
            if (!GodotObject.IsInstanceValid(panel))
                return;
            panel.Size = Vector2.Zero;
            var size = panel.GetCombinedMinimumSize();
            panel.Size = size;
            var screen = panel.GetViewportRect().Size;
            var position = new Vector2(right ? at.X - size.X : at.X, above ? at.Y - size.Y : at.Y);
            panel.Position = new Vector2(Math.Clamp(position.X, 0, Math.Max(0, screen.X - size.X)), Math.Clamp(position.Y, 0, Math.Max(0, screen.Y - size.Y)));
        }
        Callable.From(Place).CallDeferred();
        if (keep != null)
        {
            var tree = owner.GetTree();
            ulong? lostAt = null;
            var expanded = false;
            void Check()
            {
                // Holding Alt: the whole text, in a wider card.
                if (cut && text != null && Input.IsKeyPressed(Key.Alt) != expanded)
                {
                    expanded = !expanded;
                    text.Text = expanded ? full! : shortText;
                    text.CustomMinimumSize = new Vector2(expanded ? FullWidth : width, 0);
                    Callable.From(Place).CallDeferred();
                }
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
