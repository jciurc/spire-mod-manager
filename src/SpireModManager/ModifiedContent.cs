using Godot;
using MegaCrit.Sts2.Core.Modding;

namespace SpireModManager;

// What a mod changes in the game itself (GameChanges) in the info panel, our views only. One row, Modified content, on
// one line and always there (Jordan: "the info panel has precious real estate"): the areas that fit, then how many more.
// Its card counts the places and files and lists every area; a click opens the details, a dialog with the areas, the
// game's files replaced and the game's code changed.
static class ModifiedContent
{
    public const string Meta = "changes"; // the row's link in the panel's text
    public const string CardName = "SpireModManagerChangesCard";
    public const float CardWidth = 440;
    public const string Name = "SpireModManagerModifiedContent"; // the dialog
    const string GoldHex = "#efc851", CardGoldHex = "#e8cf7a", Quiet = "#fff6e299";
    const string Bullet = "[color=#fff6e280]•[/color]";
    const int TipAreas = 320; // the card's list of areas: about this many characters at most, then "and N more"
    const float DialogWidth = 820;
    const float ListMost = 560; // the dialog's list: this tall at most, then it scrolls
    const int BodySize = 17;
    // The dialog's areas: this many columns side by side across it, each at most AreaRows tall; more columns follow to
    // the right, scrolled sideways.
    const int AreaColumns = 3, AreaRows = 5;
    const int ColumnGap = 24;

    public static bool IsOpen => Modal.IsOpen(Name);

    // True when there was one to close.
    public static bool Close() => Modal.Close(Name);

    // The row for the panel's text (its label: the room it has, its font): "Modified content: The Regent (energy orb),
    // merchant room and 4 more", the areas that fit whole, most first. The counts are in its card (Jordan: "We don't need
    // brackets count anymore"). With none found, "No information found", subtle. All of it the link for the card and the
    // click.
    public static string Row(GameChanges.Changes changes, RichTextLabel text)
    {
        var term = Menus.Text("SPIRE_MOD_MANAGER.MODIFIED_CONTENT");
        if (!changes.Any)
            return $"[url={Meta}][gold]{Escape(term)}[/gold]: [color={Quiet}]{Escape(Menus.Text("SPIRE_MOD_MANAGER.MODIFIED_NONE"))}[/color][/url]";
        var (font, size, room) = (text.GetThemeFont("normal_font"), text.GetThemeFontSize("normal_font_size"), ModDetails.LineRoom(text));
        // Not laid out yet (no width): all of them, as there's no line to fit yet.
        var listed = AreasCut(changes.Areas, areas => room <= 0 || Width($"{term}: {areas}", font, size) <= room);
        return $"[url={Meta}][gold]{Escape(term)}[/gold]: {Escape(listed)}[/url]";
    }

    public static string FilesCount(int count) => count == 1 ? Menus.Text("SPIRE_MOD_MANAGER.GAME_FILES_COUNT_ONE")
        : Menus.Format("SPIRE_MOD_MANAGER.GAME_FILES_COUNT", ("count", Menus.Number(count)));

    public static string PlacesCount(int count) => count == 1 ? Menus.Text("SPIRE_MOD_MANAGER.GAME_CODE_COUNT_ONE")
        : Menus.Format("SPIRE_MOD_MANAGER.GAME_CODE_COUNT", ("count", Menus.Number(count)));

    // A line's width as it's shaped (as Layout measures its page link), from the fonts its letters use.
    static float Width(string text, Font font, int size)
    {
        using var line = new TextLine();
        line.AddString(text, font, size);
        return line.GetLineWidth();
    }

    // The areas that fit (fits: with what goes around them), most first and whole, then "and N more". Adding an area
    // only makes the line longer, until the last takes away the "and 1 more": all of them are tried first. When not even
    // the first fits, a character's (with its list of what) shows as many of what as fit, whole ("The Regent (energy
    // orb, hands, ..)"); any other is cut short with "..".
    public static string AreasCut(IReadOnlyList<string> areas, Func<string, bool> fits)
    {
        if (areas.Count == 0)
            return "";
        var all = Listed(areas, areas.Count, areas[0]);
        if (fits(all))
            return all;
        string? best = null;
        for (var shown = 1; shown < areas.Count; shown++)
        {
            var listed = Listed(areas, shown, areas[0]);
            if (!fits(listed))
                break;
            best = listed;
        }
        if (best != null)
            return best;
        if (Items(areas[0]) is (var subject, var whats))
            for (var kept = whats.Length - 1; kept > 0; kept--)
                if (Listed(areas, 1, ItemsCut(subject, whats, kept)) is var cut && fits(cut))
                    return cut;
        var first = areas[0];
        // A text element at a time (Menus.Prefix), down to one.
        while (System.Globalization.StringInfo.GetNextTextElementLength(first) < first.Length && !fits(Listed(areas, 1, Menus.CutShort(first))))
            first = Menus.Prefix(first, first.Length - 1).TrimEnd();
        return Listed(areas, 1, Menus.CutShort(first));
    }

    // A character's area as its name and what of theirs ("The Regent", ["energy orb", "hands"]); null for another.
    public static (string Subject, string[] Whats)? Items(string area) =>
        area.EndsWith(')') && area.IndexOf(" (", StringComparison.Ordinal) is var open and > 0
            ? (area[..open], area[(open + 2)..^1].Split(", ")) : null;

    // The first `kept` of them, and ".." for the rest: "The Regent (energy orb, hands, ..)".
    public static string ItemsCut(string subject, string[] whats, int kept) => $"{subject} ({string.Join(", ", whats.Take(kept))}, {Menus.CutMark})";

    // The first `shown` areas (the first as given), then how many more.
    static string Listed(IReadOnlyList<string> areas, int shown, string first)
    {
        var listed = string.Join(", ", areas.Skip(1).Take(shown - 1).Prepend(first));
        return (areas.Count - shown) switch
        {
            0 => listed,
            1 => Menus.Format("SPIRE_MOD_MANAGER.MODIFIED_MORE_ONE", ("areas", listed)),
            var more => Menus.Format("SPIRE_MOD_MANAGER.MODIFIED_MORE", ("areas", listed), ("count", Menus.Number(more))),
        };
    }

    // The row's card: what the mod changes, in Jordan's words, with the places and files counted (code and files: one
    // sentence with both), a line saying a mod's contents show only while it's loaded, every area (cut when there are
    // very many), then the click hint. With none found, what that means.
    public static string Tip(Mod mod, GameChanges.Changes changes)
    {
        var said = (changes.Code, changes.Files) switch
        {
            ( > 0, > 0) => Menus.Format("SPIRE_MOD_MANAGER.MODIFIED_BOTH_TIP", ("places", PlacesCount(changes.Code)), ("files", FilesCount(changes.Files))),
            ( > 0, _) => Menus.Format("SPIRE_MOD_MANAGER.MODIFIED_CODE_TIP", ("places", PlacesCount(changes.Code))),
            (_, 1) => Menus.Text("SPIRE_MOD_MANAGER.MODIFIED_FILES_TIP_ONE"),
            (_, > 1) => Menus.Format("SPIRE_MOD_MANAGER.MODIFIED_FILES_TIP", ("count", Menus.Number(changes.Files))),
            _ => Menus.Text("SPIRE_MOD_MANAGER.MODIFIED_NONE_TIP"),
        };
        var lines = new List<string> { Escape(said) };
        if (mod.state != ModLoadState.Loaded)
            lines.Add($"[color={Quiet}]{Escape(Menus.Text("SPIRE_MOD_MANAGER.MODIFIED_NOT_LOADED"))}[/color]");
        if (changes.Any)
            lines.AddRange([ModDetails.CardGap, $"[color={CardGoldHex}]{Escape(AreasCut(changes.Areas, areas => areas.Length <= TipAreas))}[/color]"]);
        return $"{string.Join("\n", lines)}\n\n{Tags.Hint(Menus.Text("SPIRE_MOD_MANAGER.MODIFIED_CLICK_TIP"))}";
    }

    // The details, over the page (Modal, as the import preview is): the mod's name, where the list comes from, the
    // areas (Areas), then the list (Details), scrolled when it's long, and Close. owner: the panel; the dialog goes with it.
    public static void Show(Node owner, Mod mod, GameChanges.Changes changes)
    {
        Hover.Hide(); // the row's card: the dialog covers what it's about
        var name = mod.manifest?.name is { Length: > 0 } named ? named : mod.manifest?.id ?? "";
        if (Modal.Open(owner, Name, DialogWidth, 12) is not { } column)
            return;
        column.AddChild(Modal.Title("SPIRE_MOD_MANAGER.MODIFIED_TITLE", ("name", name)));
        column.AddChild(Text("Note", $"[color={Quiet}]{Escape(Menus.Text("SPIRE_MOD_MANAGER.MODIFIED_NOTE"))}[/color]", 16));
        column.AddChild(Areas(changes));
        var details = Details(mod, changes);
        var list = Text("List", details, BodySize);
        list.MouseFilter = Control.MouseFilterEnum.Pass; // the wheel goes on to the scroll
        var inside = new MarginContainer { Name = "Inside", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Pass };
        inside.AddThemeConstantOverride("margin_right", 16); // the words clear of the scrollbar
        inside.AddChild(list);
        // Nothing found in a loaded mod: the areas say so, and there's no list under them.
        var scroll = new ScrollContainer { Name = "Scroll", HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, Visible = details.Length > 0 };
        scroll.AddChild(inside);
        // As tall as the list once it's laid out, up to ListMost; then it scrolls.
        list.MinimumSizeChanged += () =>
        {
            if (GodotObject.IsInstanceValid(scroll))
                scroll.CustomMinimumSize = new Vector2(0, Math.Min(ListMost, list.GetCombinedMinimumSize().Y));
        };
        column.AddChild(scroll);
        var buttons = new HBoxContainer { Name = "Buttons", Alignment = BoxContainer.AlignmentMode.End };
        var close = Menus.SmallButton(Menus.Text("SPIRE_MOD_MANAGER.CLOSE"), () => Close(), name: "CloseModifiedButton");
        close.AddThemeFontSizeOverride("font_size", 18);
        close.CustomMinimumSize = new Vector2(0, 34);
        buttons.AddChild(close);
        column.AddChild(buttons);
    }

    public const string AreasName = "Areas", AreasScrollName = "AreasScroll"; // the dialog's areas, and their sideways scroll

    // The dialog's areas under their heading (Jordan, 23 Oct: "list the areas vertically (maybe 3 columns in a
    // container that scrolls horizontally?) to save space"): one to a line, in columns filled top to bottom (Columns),
    // three side by side across the dialog; with more than three columns hold, the rest follow to the right, scrolled
    // sideways. A long one (a character's, with what of theirs) wraps in its column: this is where an area shows whole.
    // With none found, that.
    static Control Areas(GameChanges.Changes changes)
    {
        var box = new VBoxContainer { Name = AreasName };
        box.AddThemeConstantOverride("separation", 2);
        box.AddChild(Text("AreasHeading", Heading("SPIRE_MOD_MANAGER.MODIFIED_AREAS", null), BodySize));
        if (!changes.Any)
        {
            box.AddChild(Text("NoAreas", $"[color={Quiet}]{Escape(Menus.Text("SPIRE_MOD_MANAGER.MODIFIED_NONE"))}[/color]", BodySize));
            return box;
        }
        // Three columns and their gaps as wide as the dialog's column, so a fourth starts just out of view.
        var width = MathF.Floor((DialogWidth - 2 * Modal.Side - (AreaColumns - 1) * ColumnGap) / AreaColumns);
        var columns = new HBoxContainer { Name = "Columns" };
        columns.AddThemeConstantOverride("separation", ColumnGap);
        var at = 0;
        foreach (var (areas, i) in Columns(changes.Areas).Select((areas, i) => (areas, i)))
        {
            var one = new VBoxContainer { Name = $"AreaColumn{i}", CustomMinimumSize = new Vector2(width, 0) };
            one.AddThemeConstantOverride("separation", 4); // a little more between areas than between a long one's lines
            foreach (var area in areas)
                one.AddChild(Text($"Area{at++}", Escape(area), BodySize));
            columns.AddChild(one);
        }
        // The wheel over the columns goes on to their scroll, which moves them sideways when there are more than three.
        foreach (var part in columns.GetChildren().OfType<Control>().Prepend(columns))
            part.MouseFilter = Control.MouseFilterEnum.Pass;
        var sideways = columns.GetChildCount() > AreaColumns;
        var scroll = new ScrollContainer
        {
            Name = AreasScrollName, VerticalScrollMode = ScrollContainer.ScrollMode.Disabled,
            HorizontalScrollMode = sideways ? ScrollContainer.ScrollMode.Auto : ScrollContainer.ScrollMode.Disabled,
        };
        // The game's scrollbar is drawn over what it scrolls, so the last line keeps clear of it, as the list's words do.
        var inside = new MarginContainer { Name = "Inside", MouseFilter = Control.MouseFilterEnum.Pass };
        inside.AddThemeConstantOverride("margin_bottom", sideways ? 16 : 0);
        inside.AddChild(columns);
        scroll.AddChild(inside);
        box.AddChild(scroll);
        return box;
    }

    // The areas in columns, filled top to bottom: as few to a column as three columns need (two areas side by side on
    // one line, seven in three columns of 3, 3 and 1), at most AreaRows; past three columns of those, more columns.
    public static List<List<string>> Columns(IReadOnlyList<string> areas)
    {
        var rows = Math.Clamp((areas.Count + AreaColumns - 1) / AreaColumns, 1, AreaRows);
        return [.. areas.Chunk(rows).Select(column => column.ToList())];
    }

    // The dialog's list, under its areas: the game's files replaced, by area, each path; the game's code changed, by
    // area, each method with how it's patched, and what those words mean. Each heading with its count. A mod not
    // loaded: that its contents show only while it is. Empty with none of those.
    public static string Details(Mod mod, GameChanges.Changes changes)
    {
        var lines = new List<string>();
        // A blank line between parts.
        string[] Gap() => lines.Count > 0 ? [""] : [];
        if (changes.Files > 0)
        {
            lines.Add(Heading("SPIRE_MOD_MANAGER.GAME_FILES", FilesCount(changes.Files)));
            foreach (var area in changes.ReplacedFiles.GroupBy(f => f.Area))
            {
                lines.Add(Group(area.Key, FilesCount(area.Count())));
                lines.AddRange(area.Select(f => Item + Escape(f.Path)));
            }
        }
        if (changes.Code > 0)
        {
            lines.AddRange([.. Gap(), Heading("SPIRE_MOD_MANAGER.GAME_CODE", PlacesCount(changes.Code))]);
            foreach (var area in changes.Places.GroupBy(p => p.Area))
            {
                lines.Add(Group(area.Key, PlacesCount(area.Count())));
                lines.AddRange(area.Select(p => $"{Item}{Escape($"{p.Class}.{p.Method}")}  [color={Quiet}]({Escape(KindWords(p.Kinds))})[/color]"));
            }
            lines.AddRange(["", $"[color={Quiet}]{Escape(Menus.Text("SPIRE_MOD_MANAGER.PATCH_KINDS"))}[/color]"]);
        }
        if (mod.state != ModLoadState.Loaded)
            lines.AddRange([.. Gap(), $"[color={Quiet}]{Escape(Menus.Text("SPIRE_MOD_MANAGER.MODIFIED_NOT_LOADED"))}[/color]"]);
        return string.Join("\n", lines);
    }

    const string Item = "        "; // a path or a method, under its area
    const string GroupIndent = "    ";

    static string Heading(string key, string? count) =>
        $"[color={GoldHex}]{Escape(Menus.Text(key))}[/color]" + (count is { } shown ? $"  {Bullet}  [color={Quiet}]{Escape(shown)}[/color]" : "");

    static string Group(string area, string count) => $"{GroupIndent}[b]{Escape(area)}[/b]  {Bullet}  [color={Quiet}]{Escape(count)}[/color]";

    // How a place is patched, in plain words: "before, after".
    public static string KindWords(GameChanges.Kinds kinds) => string.Join(", ", new[]
    {
        (GameChanges.Kinds.Before, "SPIRE_MOD_MANAGER.PATCH_BEFORE"), (GameChanges.Kinds.After, "SPIRE_MOD_MANAGER.PATCH_AFTER"),
        (GameChanges.Kinds.Rewritten, "SPIRE_MOD_MANAGER.PATCH_REWRITTEN"), (GameChanges.Kinds.Wrapped, "SPIRE_MOD_MANAGER.PATCH_WRAPPED"),
    }.Where(k => kinds.HasFlag(k.Item1)).Select(k => Menus.Text(k.Item2)));

    static RichTextLabel Text(string name, string bbcode, int size)
    {
        var text = new RichTextLabel
        {
            Name = name, BbcodeEnabled = true, FitContent = true, ScrollActive = false, AutowrapMode = TextServer.AutowrapMode.WordSmart,
            MouseFilter = Control.MouseFilterEnum.Ignore, Text = bbcode,
        };
        Hover.Fonts(text, size);
        return text;
    }

    static string Escape(string text) => text.Replace("[", "[lb]");
}
