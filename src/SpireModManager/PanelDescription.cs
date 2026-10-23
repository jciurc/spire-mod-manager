using System.Runtime.CompilerServices;
using Godot;

namespace SpireModManager;

// A mod's own description in the info panel (our views): at most Lines lines, cut at a word with "…" where it runs
// longer, and the whole of it in a card over those lines (Jordan: "the info panel has precious real estate"). A
// description that fits shows as it is. Vanilla View is the game's panel, untouched.
static class PanelDescription
{
    public const int Lines = 3;
    public const string HoverName = "SpireModManagerDescriptionHover"; // over the cut lines, for the card
    public const string CardName = "SpireModManagerDescriptionCard";
    const string ProbeName = "SpireModManagerDescriptionProbe";
    const float CardWidth = 560;
    const int CardMost = 1500; // the card's text: about this many characters, the rest with Alt
    static readonly StringName FullMeta = "spire_mod_manager_full_description"; // on the label: the whole of a cut one, else ""

    // Before and after a cut description in the panel's text, so its lines can be found as the label lays them out:
    // the zero-width no-break space, which shows nothing, takes no room and lets no line break beside it (as GameText's
    // marks, though not one of them).
    public const char Mark = '﻿';

    static readonly string[] Fonts = ["normal_font", "bold_font", "italics_font", "bold_italics_font", "mono_font"];
    static readonly string[] Sizes = ["normal_font_size", "bold_font_size", "italics_font_size", "bold_italics_font_size", "mono_font_size"];

    // A description, its font and the room it had: what the panel showed for it, worked out once.
    static readonly Dictionary<(string Text, ulong Font, int Size, float Room), string> Cuts = [];

    // The description as the panel shows it in label: as it is when it fits in Lines lines at the label's width, else
    // the most of it that does, cut at a word with "…", between Marks; the whole of it noted for the card.
    public static string Shown(RichTextLabel label, string description)
    {
        var room = ModDetails.LineRoom(label);
        var (font, size) = (label.GetThemeFont("normal_font"), label.GetThemeFontSize("normal_font_size"));
        var key = (description, font.GetInstanceId(), size, room);
        if (!Cuts.TryGetValue(key, out var shown))
            Cuts[key] = shown = room <= 0 ? description : Cut(label, description, room); // no width yet: nothing to fit to
        label.SetMeta(FullMeta, shown == description ? "" : description);
        return shown;
    }

    // Measured as the label lays it out, in a label of its own (Probe) as wide as its lines: the longest cut that keeps
    // to Lines lines, found by halves (a longer cut never takes fewer lines).
    static string Cut(RichTextLabel label, string description, float room)
    {
        var probe = Probe(label, room);
        int LinesOf(string bbcode)
        {
            probe.Text = ModDetails.Described(bbcode, int.MaxValue); // the game's tags as our label reads them
            return probe.GetLineCount();
        }
        if (LinesOf(description.TrimEnd()) <= Lines)
            return description;
        var (fits, over) = (0, description.Length);
        while (over - fits > 1)
        {
            var mid = (fits + over) / 2;
            if (LinesOf(ModDetails.Shortened(description, mid)) <= Lines)
                fits = mid;
            else
                over = mid;
        }
        return $"{Mark}{ModDetails.Shortened(description, Math.Max(1, fits))}{Mark}";
    }

    // The label that measures (one per panel, never shown): the description label's fonts, sizes and line spacing, and
    // its wrapping, at the width of its lines.
    static RichTextLabel Probe(RichTextLabel label, float room)
    {
        var parent = label.GetParent();
        if (parent.GetNodeOrNull<RichTextLabel>(ProbeName) is not { } probe)
        {
            probe = new RichTextLabel
            {
                Name = ProbeName, BbcodeEnabled = true, ScrollActive = false, Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            probe.AddThemeStyleboxOverride("normal", new StyleBoxEmpty());
            parent.AddChild(probe);
        }
        foreach (var name in Fonts)
            probe.AddThemeFontOverride(name, label.GetThemeFont(name));
        foreach (var name in Sizes)
            probe.AddThemeFontSizeOverride(name, label.GetThemeFontSize(name));
        probe.AddThemeConstantOverride("line_separation", label.GetThemeConstant("line_separation"));
        probe.AutowrapMode = label.AutowrapMode;
        probe.Size = new Vector2(room, probe.Size.Y);
        return probe;
    }

    sealed class Area
    {
        public Control? Trigger;
        public Rect2? Placed;
    }

    static readonly ConditionalWeakTable<RichTextLabel, Area> Watched = [];

    // The card over a cut description's lines, kept on them as the label lays its text out and scrolls (worked out as it
    // draws). Once per label.
    public static void Watch(RichTextLabel label)
    {
        if (Watched.TryGetValue(label, out _))
            return;
        // Passes the mouse on: the label's links, the wheel and the pointer over a link stay the label's.
        var hover = new Control { Name = HoverName, MouseFilter = Control.MouseFilterEnum.Pass, Visible = false };
        label.AddChild(hover);
        var area = new Area { Trigger = hover };
        Watched.Add(label, area);
        string? Full() => Store.Data.VanillaView ? null : label.GetMeta(FullMeta, "").AsString() is { Length: > 0 } whole ? whole.Trim() : null;
        Hover.Watch(hover, CardName, () => Full() is { } whole ? ModDetails.Described(whole, CardMost) : null, width: CardWidth,
            full: () => Full() is { Length: > CardMost } whole ? ModDetails.Described(whole, int.MaxValue) : null);
        label.Draw += () => Place(label, area);
    }

    // From the line of the first Mark to the line of the second, as wide as the lines, where the label's scroll has them
    // now; none without them (a description that fits, the game's own text in Vanilla View).
    static void Place(RichTextLabel label, Area area)
    {
        Rect2? at = null;
        var text = label.GetParsedText();
        var start = Store.Data.VanillaView ? -1 : text.IndexOf(Mark);
        var end = start < 0 ? -1 : text.IndexOf(Mark, start + 1);
        if (end > start && label.GetCharacterLine(start) is >= 0 and var first && label.GetCharacterLine(end) is var last && last >= first)
        {
            var style = label.GetThemeStylebox("normal");
            var bar = label.GetVScrollBar();
            var top = style.GetOffset().Y - (float)bar.Value + label.GetLineOffset(first);
            var bottom = style.GetOffset().Y - (float)bar.Value + label.GetLineOffset(last) + label.GetLineHeight(last);
            var width = label.Size.X - style.GetMinimumSize().X - (bar.Visible ? bar.Size.X : 0);
            at = new Rect2(style.GetOffset().X, top, width, bottom - top);
        }
        if (at == area.Placed)
            return;
        area.Placed = at;
        // Moved after this draw: a control's box isn't changed while its parent draws.
        Callable.From(() =>
        {
            if (area.Trigger is not { } hover || !GodotObject.IsInstanceValid(hover))
                return;
            hover.Visible = at != null;
            if (at is { } box)
                (hover.Position, hover.Size) = (box.Position, box.Size);
        }).CallDeferred();
    }

#if DEVTOOLS
    // Self-test: where the card's area is in the label (null: none), as last placed.
    public static Rect2? TestPlaced(RichTextLabel label) => Watched.TryGetValue(label, out var area) ? area.Placed : null;
#endif
}
