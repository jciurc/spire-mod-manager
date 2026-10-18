using System.Text.RegularExpressions;
using Godot;

namespace SpireModManager;

// Hover cards (our tooltips): a dark panel with a few lines of text, or a picture, above the page. For every card:
// - it shows after Delay; Instant ones at once (a banner's mod list, the unsaved mark, the change tracker); Slow ones
//   (the whole of a text cut short, as a plain tooltip) after SlowDelay; Plain ones look like Slow ones but come after
//   Delay; Late ones (the main menu button's) are Normal ones after SlowDelay. Slow, Plain and Late skip the grace;
// - once a card has gone, any other shows at once for Grace (Instant cards don't start one);
// - leaving, it goes after WindDown (Instant ones at once), so a quick wobble off the edge keeps it;
// - one card at a time: a newer one closes the one before;
// - unless its caller places it, it opens below right of the mouse, off its trigger, or above it (left of the mouse)
//   where there's no room below (right): never over the trigger and the mouse while there's room on the other side. A
//   picture card mirrors it across: below left of the mouse, turning right of it (or above) where there's no room.
static class Hover
{
    public const double Delay = 0.4, Grace = 0.25, WindDown = 0.02, SlowDelay = 1.0;
    public const int Depth = 80; // the cards' layer: over the page, under our menus (MenuPanel.Depth)
    // A card beside its trigger: this far off it.
    public const float Gap = 6;
    // How far above and below the mouse a card keeps off its trigger: all of a button or a row (and a note beside it),
    // only the part near the mouse of anything taller.
    const float Reach = 100;

    public enum Timing { Normal, Instant, Slow, Plain, Late }

    static readonly Color Cream = new(1, 0.964706f, 0.886275f);
    static readonly Color Gold = new(0.937f, 0.784f, 0.318f);
    const float PictureMax = 800, PlainMax = 520, FullWidth = 640;

#if DEVTOOLS
    // Self-test: where the mouse is during a test run (the real cursor can be anywhere).
    public static Func<Vector2>? TestMouse { get; set; }
    public static Vector2 Mouse(CanvasItem item) => TestMouse?.Invoke() ?? item.GetGlobalMousePosition();
#else
    public static Vector2 Mouse(CanvasItem item) => item.GetGlobalMousePosition();
#endif

    static ulong _graceUntil;
    static Node? _shown; // the card showing (its layer), if any

    // In a grace period, or a card still showing (winding down, or about to be replaced): the next shows at once.
    static bool InGrace => Time.GetTicksMsec() < _graceUntil || (_shown != null && GodotObject.IsInstanceValid(_shown));

    // A card for `trigger` while the mouse is over it. card: its text, or null for none (asked when it opens).
    // picture: a picture card instead (left of the mouse, or right of it or above it where there's no room), or null for none. place: where it goes, kept as it is
    // (default: below right of the mouse, off the trigger, or the other way where there's no room: Show's around). full:
    // the whole text, when the card's is cut short (Alt shows it). fit: the card only as wide as its text (up to width),
    // for a few words. area: what the default place keeps off, when more than the trigger (a note of its own beside it).
    public static void Watch(Control trigger, string name, Func<string?> card, Timing timing = Timing.Normal, float width = 380,
        Func<Texture2D?>? picture = null, Func<(Vector2 At, bool Right, bool Above)>? place = null, Func<string?>? full = null, bool fit = false,
        Func<Rect2>? area = null)
    {
        bool Over() => GodotObject.IsInstanceValid(trigger) && trigger.IsVisibleInTree() && Allowed(trigger)
            && trigger.GetGlobalRect().HasPoint(Mouse(trigger));
        var token = 0;
        trigger.MouseEntered += () =>
        {
            var mine = ++token;
            void Open()
            {
                if (mine != token || !Over() || Owner(trigger) is not { } owner)
                    return;
                var mouse = Mouse(trigger);
                // A picture (the thumbnail under the mouse, bigger) keeps off the mouse only: beside it, over the thumbnail.
                if (picture?.Invoke() is { } image)
                    Show(owner, name, "", mouse + new Vector2(-16, 20), right: true, keep: Over, timing: timing, picture: image, depth: LayerFor(trigger),
                        around: new Rect2(mouse, Vector2.Zero));
                else if (picture == null && card() is { } text)
                {
                    var (at, right, above, around) = place?.Invoke() is { } own ? (own.At, own.Right, own.Above, (Rect2?)null)
                        : (mouse + new Vector2(16, 20), false, false, Near(area?.Invoke() ?? trigger.GetGlobalRect(), mouse));
                    Show(owner, name, text, at, right, above, width, Over, timing, full: full?.Invoke(), depth: LayerFor(trigger), fit: fit, around: around);
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

    // What a card opens beside (Show's around): the trigger, as far as Reach above and below the mouse, at the mouse's x.
    static Rect2 Near(Rect2 trigger, Vector2 mouse)
    {
        var (top, bottom) = (Math.Max(trigger.Position.Y, mouse.Y - Reach), Math.Min(trigger.End.Y, mouse.Y + Reach));
        return new Rect2(mouse.X, top, 0, Math.Max(0, bottom - top));
    }

    // What a control does (a button, an icon), in our card instead of the engine's TooltipText, which looks like none
    // of ours. Set it again to change the words; null or "" takes the card off. timing: when the card shows (the first
    // call's stays). markup: the text is BBCode, anything of the player's in it escaped by the caller. area: as Watch's
    // (the first call's stays).
    public const string TipName = "SpireModManagerTip";
    const string TipMeta = "spire_mod_manager_tip", MarkupMeta = "spire_mod_manager_tip_markup";

    public static void Tip(Control control, string? text, Timing timing = Timing.Normal, bool markup = false, Func<Rect2>? area = null)
    {
        var watched = control.HasMeta(TipMeta);
        if (!watched && string.IsNullOrEmpty(text))
            return; // nothing to say, and never had: no card to watch for
        control.SetMeta(TipMeta, text ?? "");
        control.SetMeta(MarkupMeta, markup);
        if (!watched)
            Watch(control, TipName, () => TipOf(control) is { Length: > 0 } tip
                ? control.GetMeta(MarkupMeta, false).AsBool() ? tip : Escape(tip) : null, timing, fit: true, area: area);
    }

    public static string TipOf(Control control) => control.GetMeta(TipMeta, "").AsString();

    // The same for [url] links in a text: a card for the link under the mouse. card: its text for the link's
    // meta, or null for none.
    public static void WatchLinks(RichTextLabel label, string name, Func<string, string?> card, float width = 380, Func<string, string?>? full = null)
    {
        string? current = null;
        var token = 0;
        bool Over(string meta) => current == meta && GodotObject.IsInstanceValid(label) && label.IsVisibleInTree() && Allowed(label)
            && label.GetGlobalRect().HasPoint(Mouse(label));
        label.MetaHoverStarted += meta =>
        {
            var link = meta.AsString();
            current = link;
            var mine = ++token;
            Later(label, Timing.Normal, () =>
            {
                if (mine != token || !Over(link) || card(link) is not { } text || Owner(label) is not { } owner)
                    return;
                // Turned above the link, it keeps off the link's line, which starts at most a line above the mouse.
                var mouse = Mouse(label);
                var line = label.GetThemeFont("normal_font").GetHeight(label.GetThemeFontSize("normal_font_size"));
                Show(owner, name, text, mouse + new Vector2(16, 20), width: width, keep: () => Over(link), full: full?.Invoke(link),
                    depth: LayerFor(label), // in the open menu: above it, like Watch's cards
                    around: new Rect2(mouse.X, mouse.Y - line, 0, line));
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
            node.GetTree().CreateTimer(timing is Timing.Slow or Timing.Late ? SlowDelay : Delay).Timeout += open;
    }

    // Cards belong to the page they're on (they go with it), else to the window.
    static Node? Owner(Node node) => CategoryList.ScreenOf(node) ?? (Node?)node.GetTree()?.Root;

    // Cards are off while a menu is open (it covers what they'd be about), except for what's in the menu itself (a
    // field's term, a tag chip, the bulk menu's i).
    static bool Allowed(Node trigger) => !MenuPanel.IsOpen || InOpenMenu(trigger);
    static bool InOpenMenu(Node node) => MenuPanel.Current is { } open && open.IsAncestorOf(node);

    // A card goes just above the layer its trigger is on, and never under the page's cards: over the open menu (under
    // its tag suggestions, MenuPanel.Depth + 5) or a dialog (Modal), as a tooltip would be.
    static int LayerFor(Node trigger)
    {
        for (var at = trigger.GetParent(); at != null; at = at.GetParent())
            if (at is CanvasLayer layer)
                return Math.Max(Depth, layer.Layer + 2);
        return Depth;
    }

    // Text shown as it is in a card (its "[" not read as BBCode).
    public static string Escape(string text) => text.Replace("[", "[lb]");

    // The game's own [gold] tag (its text labels know it; a plain RichTextLabel, as in our cards, doesn't) as our gold,
    // so our texts can use it in a card too.
    static string Gamelike(string bbcode) => bbcode.Replace("[gold]", $"[color=#{Gold.ToHtml(false)}]").Replace("[/gold]", "[/color]");

    // The text a card shows, without its tags: what its width is measured on.
    static string Unmarked(string bbcode) =>
        Regex.Replace(bbcode, @"\[(/?[a-z_]+(=[^\]]*)?)\]", m => m.Value == "[lb]" ? "[" : m.Value == "[rb]" ? "]" : "");

    // at: the card's top-left corner (or, with right/above, that corner), kept on screen. around: what it opens beside
    // (the trigger near the mouse, at the mouse's x; null: nothing, the card stays at `at`): at least Gap off it, and where
    // it runs off the screen, on the other side of it (above, or left of the mouse) if less runs off there, chosen each
    // time it's placed. keep: checked every frame; once false (for WindDown, unless Instant) the card goes. full: the
    // whole text when it's cut short (hold Alt).
    public static void Show(Node owner, string name, string bbcode, Vector2 at, bool right = false, bool above = false, float width = 380,
        Func<bool>? keep = null, Timing timing = Timing.Normal, Texture2D? picture = null, string? full = null, int depth = Depth, bool fit = false,
        Rect2? around = null)
    {
        Hide(); // one at a time
        (bbcode, full) = (Gamelike(bbcode), full == null ? null : Gamelike(full));
        var layer = new CanvasLayer { Name = name, Layer = depth };
        owner.AddChild(layer);
        _shown = layer;
        var plain = timing is Timing.Slow or Timing.Plain;
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
            var size = plain ? 15 : 17;
            Fonts(text, size);
            if (plain || fit)
            {
                // As wide as the text (its longest line), up to PlainMax or the card's width; longer text wraps.
                var most = plain ? PlainMax : width;
                var wide = text.GetThemeFont("normal_font").GetMultilineStringSize(Unmarked(bbcode), fontSize: size).X + 4;
                (text.AutowrapMode, text.CustomMinimumSize) = wide <= most ? (TextServer.AutowrapMode.Off, new Vector2(wide, 0)) : (TextServer.AutowrapMode.WordSmart, new Vector2(most, 0));
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
            if (around is { } clear)
            {
                // Off the trigger on its own side; then, short of room, above it rather than kept on screen over it and the
                // mouse (or below, for one above it), and left of the mouse by as much as it was right (or the reverse).
                var (over, under) = (clear.Position.Y - Gap - size.Y, clear.End.Y + Gap);
                position.Y = above ? Math.Min(position.Y, over) : Math.Max(position.Y, under);
                position.Y = Turned(position.Y, size.Y, screen.Y, above ? under : over);
                position.X = Turned(position.X, size.X, screen.X, 2 * clear.GetCenter().X - position.X - size.X);
            }
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
                if (cut && full is { } whole && text != null && Input.IsKeyPressed(Key.Alt) != expanded)
                {
                    expanded = !expanded;
                    text.Text = expanded ? whole : shortText;
                    text.CustomMinimumSize = new Vector2(expanded ? FullWidth : width, 0);
                    // Laid out at the new width first, then measured (after the panel has sorted its child).
                    text.Size = new Vector2(expanded ? FullWidth : width, 0);
                    Callable.From(() => Callable.From(Place).CallDeferred()).CallDeferred();
                }
                if (GodotObject.IsInstanceValid(owner) && owner.IsInsideTree() && keep())
                {
                    lostAt = null;
                    return;
                }
                var now = Time.GetTicksMsec();
                lostAt ??= now;
                if ((timing is Timing.Normal or Timing.Late) && now - lostAt < WindDown * 1000)
                    return; // winding down: the mouse may come back
                Close(layer);
                if (timing is Timing.Normal or Timing.Late)
                    _graceUntil = now + (ulong)(Grace * 1000);
            }
            tree.ProcessFrame += Check;
            layer.TreeExiting += () => tree.ProcessFrame -= Check;
        }
    }

    // Along one axis: a card from `start` (size long), or from `other` instead when it runs off the screen (0..screen)
    // and less of it would from there.
    static float Turned(float start, float size, float screen, float other) => Turns(start, size, screen, other) ? other : start;

    // Whether that turns it: it runs off the screen from `start`, and less of it would from `other`. Our menus turn the
    // same way (MenuPanel).
    public static bool Turns(float start, float size, float screen, float other)
    {
        float Off(float from) => Math.Max(0, -from) + Math.Max(0, from + size - screen);
        return Off(start) > 0 && Off(other) < Off(start);
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
        text.AddThemeFontOverride("normal_font", ModEntry.Font("res://themes/kreon_regular_shared.tres"));
        text.AddThemeFontOverride("bold_font", ModEntry.Font("res://themes/kreon_bold_shared.tres", richBold: true));
        text.AddThemeFontSizeOverride("normal_font_size", size);
        text.AddThemeFontSizeOverride("bold_font_size", size);
        text.AddThemeColorOverride("default_color", Cream);
    }
}
