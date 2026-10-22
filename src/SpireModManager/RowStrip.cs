using Godot;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;

namespace SpireModManager;

// The line along the bottom of each mod row in our views: its tags, its other keywords (the hidden tags that find it),
// "and 3 more..." for what doesn't fit, what it needs (red when a dependency is missing, off or loads after it) and its
// author; the row's action buttons sit on the right. The row keeps its height: nothing opens out.
static class RowStrip
{
    const string StripName = "SpireModManagerStrip";
    public const string ActionsName = "SpireModManagerActions", OrderActionsName = "SpireModManagerOrderActions"; // main list, Load Order
    public const string MoreName = "SpireModManagerMoreTags"; // "and 3 more..."
    public const string KeywordsName = "SpireModManagerKeywords"; // "Additional keywords: enabled, installed"
    static readonly StringName StripMeta = "spire_mod_manager_strip";
    static readonly StringName SaysMeta = "spire_mod_manager_says"; // on a strip or buttons: what they show and where, to know when to change them
    static readonly StringName ShowsMeta = "spire_mod_manager_shows"; // on a strip: its tags, keywords and author, to know when to remake it
    static readonly StringName ShownMeta = "spire_mod_manager_tags_shown"; // on a strip: how many tags and keywords show (the rest are "and N more...")

    // What Apply and Fold ask each row for, in the types Godot takes, made once: a text passed as one is converted anew
    // each time (a native call), on every row of every redraw.
    static readonly NodePath TitlePath = "Title", StripPath = StripName, ActionsPath = ActionsName, OrderActionsPath = OrderActionsName,
        EdgePath = EdgeName, NeedsPath = NeedsName, AuthorPath = AuthorName, SpacerPath = SpacerName, MorePath = MoreName,
        KeywordsPath = KeywordsName;
    static readonly StringName Separation = "separation", NormalFont = "normal_font", NormalFontSize = "normal_font_size";
    static readonly StringName Faint = "SpireModManagerFaint"; // the keywords' and "and 3 more..."'s look (Look)
    const float Height = 20;
    const float TitleGap = 4; // more room between the title and the line under it
    const float RightMargin = 84; // from the row's right edge: clear of the (smaller, moved) tickbox and the pencil
    const float LoadOrderMargin = 20; // Load Order has no tickbox
    const float ActionsGap = 12;
    const float AuthorMax = 200; // px
    const int LabelSize = 13; // the author's, the keywords' and "and 3 more..."'s text
    static readonly Color NoteColor = new(0.937f, 0.784f, 0.318f, 0.75f), AuthorColor = new(1, 0.964706f, 0.886275f, 0.45f);
    public const string FaintHex = "#fff6e280"; // the keywords: as the info panel's Other keywords (ModDetails.Fainter)

    // edge: a quiet button at the row's far right, past the tickbox (the main list's pencil), or none.
    public static void Apply(IList<NModMenuRow> rows, Func<NModMenuRow, IEnumerable<MenuItem>> actions, Func<NModMenuRow, MenuItem>? edge = null)
    {
        using var lookups = Lookups.Begin(); // the redraw's (each row's keywords ask about the whole list), or one for these rows
        var order = LoadOrder.Mods(rows).Select((m, i) => (m, i)).DistinctBy(p => p.m).ToDictionary(p => p.m, p => p.i);
        // Each view keeps its own buttons and hides the other's, so a switch between the main list and Load Order
        // doesn't make every row's again.
        var (name, path, other) = Store.Data.LoadOrderView ? (OrderActionsName, OrderActionsPath, ActionsPath) : (ActionsName, ActionsPath, OrderActionsPath);
        foreach (var row in rows)
        {
            Lap(null);
            var title = row.GetNode<Control>(TitlePath);
            if (!row.HasMeta(StripMeta))
            {
                title.OffsetBottom -= Height + TitleGap;
                row.SetMeta(StripMeta, true);
            }
            Lap("title");

            Edge(row, edge?.Invoke(row));
            Lap("pencil");

            // The buttons, changed only when they'd differ: their labels and states, and in Load Order the row's place
            // (what Up and Down do depends on it).
            var items = actions(row).ToList();
            var place = Store.Data.LoadOrderView ? $"{row.GetIndex()}/{row.GetParent()?.GetChildCount()}" : "";
            var does = $"{place}|{string.Join("|", items.Select(i => $"{i.Label}/{i.Disabled}/{i.Flat}/{i.Tip}"))}";
            var margin = Store.Data.LoadOrderView ? -LoadOrderMargin : -RightMargin;
            if (row.GetNodeOrNull<HBoxContainer>(path) is not { } buttons || buttons.GetMeta(SaysMeta, "").AsString() != does)
                buttons = Buttons(row, name, path, items, does, margin);
            else
                Menus.Rebind(buttons, items); // showing the same, but this redraw's actions
            buttons.OffsetRight = buttons.OffsetLeft = margin;
            var clear = buttons.OffsetRight - Width(buttons, items) - ActionsGap;
            title.OffsetRight = clear;
            Lap("buttons");

            // The line is remade only when its tags, keywords or author change (remaking every row's on each keystroke was
            // most of a redraw's time); otherwise it's updated in place.
            var chips = Tags.For(row.Mod);
            var keywords = Keywords(row.Mod);
            Lap("keywords");
            var text = string.Join("    ", new[] { RequiredFor(row.Mod), Needs(row.Mod, order, Store.Data.LoadOrderView) }.Where(s => s.Length > 0));
            var author = ModDetails.Short(row.Mod?.manifest?.author ?? "");
            var shows = Shows(chips, author, keywords);
            var says = $"{shows}|{Store.Data.LoadOrderView}|{title.OffsetLeft}|{clear}|{text}";
            var kept = row.GetNodeOrNull<HBoxContainer>(StripPath);
            if (kept?.GetMeta(SaysMeta, "").AsString() == says)
            {
                Lap("rest");
                continue;
            }
            if (row.GetNodeOrNull<Control>(other) is { } hidden)
                hidden.Visible = false;
            buttons.Visible = true;
            if (kept?.GetMeta(ShowsMeta, "").AsString() == shows)
            {
                kept.SetMeta(SaysMeta, says);
                (kept.OffsetLeft, kept.OffsetRight, kept.Visible) = (title.OffsetLeft, clear, true);
                if (SetNote(kept, text) && chips.Count + keywords.Count > 0) // folded again for the note's new width (a new width of the line refolds by itself)
                    Callable.From(() => Fold(row, kept, chips, keywords)).CallDeferred();
                Lap("rest");
                continue;
            }
            Drop(row, StripPath);

            // Made whole before it joins the row, so its parts enter the page together.
            var look = Look();
            var strip = new HBoxContainer
            {
                Name = StripName, MouseFilter = Control.MouseFilterEnum.Ignore, Theme = look.Theme,
                ClipContents = true, // too much for the line: cut at its edge, not under the buttons
            };
            strip.SetMeta(SaysMeta, says);
            strip.SetMeta(ShowsMeta, shows);
            (strip.AnchorTop, strip.AnchorBottom, strip.AnchorRight) = (1, 1, 1);
            (strip.OffsetLeft, strip.OffsetRight, strip.OffsetTop, strip.OffsetBottom) = (title.OffsetLeft, clear, -Height - 2, -2);
            Lap("rest");

            foreach (var chip in chips)
                strip.AddChild(Tags.Make(chip, add => Search.ByChip(row, chip, add)));
            if (keywords.Count > 0)
                strip.AddChild(Links(row, keywords));
            Lap("tags");

            if (text.Length > 0)
                strip.AddChild(Note(text));
            else if (author.Length > 0)
                strip.AddChild(Spacer());
            Lap("needs");

            if (author.Length > 0)
            {
                // Who made it, quietly. (Searching by author is in the info panel.)
                var by = new Label { Name = AuthorName, Text = author, VerticalAlignment = VerticalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore };
                // A long one is cut with "…"; the whole name after a second over it.
                if (RowTitles.Measure(look.Font, author, LabelSize) > AuthorMax)
                {
                    (by.ClipText, by.TextOverrunBehavior, by.CustomMinimumSize, by.MouseFilter) =
                        (true, TextServer.OverrunBehavior.TrimEllipsis, new Vector2(AuthorMax, 0), Control.MouseFilterEnum.Pass);
                    Hover.Watch(by, "SpireModManagerFullText", () => author.Replace("[", "[lb]"), Hover.Timing.Slow);
                }
                strip.AddChild(by);
            }
            Lap("author");

            row.AddChild(strip);
            Lap("added");

            // Too many tags and keywords for the line: the last ones are "and N more..." (Fold). Worked out once the
            // chips are laid out, and again whenever the line changes width.
            if (chips.Count + keywords.Count > 0)
            {
                void Refold() => Fold(row, strip, chips, keywords);
                strip.Resized += Refold;
                Callable.From(Refold).CallDeferred();
            }
            Lap("rest");
        }
    }

    // What a line's tags, author and keywords show, to know when the line must be made again: each tag's words, colour,
    // kind, card and search (a tag keeps its card and its click's search: the Duplicate tag's card names the other
    // copies), the author, and the keywords.
    public static string Shows(IEnumerable<Tags.Chip> chips, string author, IEnumerable<string>? keywords = null) =>
        $"{string.Join("|", chips.Select(c => $"{c.Text}/{c.Color}/{c.Custom}/{c.Tip}/{c.Search}"))}|{author}|{string.Join(",", keywords ?? [])}";

    // A row's keywords: the hidden tags that find its mod (HiddenTags.Of, in their order), but those a tag on the line
    // already says (Local, New, Duplicate): "Additional keywords" are the ones the tags don't show. Asked inside the
    // redraw's Lookups, so what they ask about the whole list is worked out once for every row.
    public static List<string> Keywords(Mod? mod) => mod == null ? [] : [.. HiddenTags.Of(mod).Where(k => !TagShown.Contains(k))];

    static readonly HashSet<string> TagShown = new(StringComparer.OrdinalIgnoreCase) { "local", "new", "duplicate" };

    // "Additional keywords:" and the keywords after it, plain and faint (no pill, no "#"), comma-separated: a click on
    // one searches #name, Shift-click adds it, as a tag's click does, and its card says so, as the info panel's
    // keywords' do; a click on the words before them is the row's. One label for it all (a node each, on every row, cost
    // too much): a click, and the pointing hand, find the keyword by where the mouse is (KeywordAt).
    static Label Links(NModMenuRow row, IReadOnlyList<string> keywords)
    {
        var links = new Label
        {
            Name = KeywordsName, Text = KeywordsLine(keywords), ThemeTypeVariation = Faint, VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Pass,
        };
        Hover.Watch(links, "SpireModManagerTagCard", () => KeywordAt(links, (links.GetGlobalTransform().AffineInverse() * Hover.Mouse(links)).X) != null
            ? Tags.Hint(Menus.Text("SPIRE_MOD_MANAGER.SEARCH_CLICK_TIP")) : null, width: 320);
        // The card is decided once, where the mouse came in: one coming from the tags comes in over the words before the
        // keywords, and has none. So moving onto a keyword from there starts its card, as if the mouse came in on it.
        string? under = null;
        links.MouseExited += () => under = null;
        links.GuiInput += e =>
        {
            if (e is InputEventMouseMotion moved) // the row's own (Load Order's drag) over the words before them
            {
                var at = KeywordAt(links, moved.Position.X);
                links.MouseDefaultCursorShape = at == null ? row.MouseDefaultCursorShape : Control.CursorShape.PointingHand;
                if (under == null && at != null)
                    links.EmitSignal(Control.SignalName.MouseEntered);
                under = at;
            }
            if (e is not InputEventMouseButton { ButtonIndex: MouseButton.Left } button || KeywordAt(links, button.Position.X) is not { } keyword)
                return;
            links.AcceptEvent(); // not the row's click (or drag) too
            if (!button.Pressed)
                Search.ByTag(row, keyword, button.ShiftPressed);
        };
        return links;
    }

    // "Additional keywords: enabled, installed": the line's, and the "and 3 more..." card's.
    public static string KeywordsLine(IEnumerable<string> keywords) => $"{Menus.Text("SPIRE_MOD_MANAGER.ADDITIONAL_KEYWORDS")}: {string.Join(", ", keywords)}";

    // The keyword at x on the label, null before the first: each from its start to the next one's (a comma goes with the
    // word before it).
    static string? KeywordAt(Label links, float x)
    {
        var (font, size, at) = (links.GetThemeFont(FontName), links.GetThemeFontSize(FontSize), KeywordsLine([]));
        if (!links.Text.StartsWith(at) || x < RowTitles.Measure(font, at, size))
            return null;
        var shown = links.Text[at.Length..].Split(", ", StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < shown.Length - 1; i++)
            if (x < RowTitles.Measure(font, at += shown[i] + ", ", size))
                return shown[i];
        return shown.LastOrDefault();
    }

    static readonly StringName FontName = "font", FontSize = "font_size";

    // "and 3 more...": what didn't fit on the line. Hovering it, a card with those tags (pills in their colours) and
    // keywords, and the click hint; only those, so the card is what the count counts. A click goes on to the row,
    // which it selects, so the info panel shows them all.
    static Label More(IReadOnlyList<Tags.Chip> tags, IReadOnlyList<string> keywords)
    {
        var more = new Label
        {
            Name = MoreName, Text = MoreText(tags.Count + keywords.Count), ThemeTypeVariation = Faint, VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Pass, MouseDefaultCursorShape = Control.CursorShape.PointingHand,
        };
        Hover.Watch(more, "SpireModManagerTagCard", () => MoreCard(tags, keywords), width: 320);
        return more;
    }

    static string MoreCard(IReadOnlyList<Tags.Chip> tags, IReadOnlyList<string> keywords)
    {
        var lines = new List<string>();
        if (tags.Count > 0)
            lines.Add(string.Join("  ", tags.Select(Tags.Pill)));
        if (tags.Count > 0 && keywords.Count > 0)
            lines.Add(ModDetails.CardGap); // the pills' tint clear of the words under them
        if (keywords.Count > 0)
            lines.Add($"[color={FaintHex}]{Hover.Escape(KeywordsLine(keywords))}[/color]");
        return $"{string.Join("\n", lines)}\n\n{Tags.Hint(Menus.Text("SPIRE_MOD_MANAGER.MORE_TAGS_CLICK"))}";
    }

    static string MoreText(int count) =>
        Menus.Format(count == 1 ? "SPIRE_MOD_MANAGER.MORE_TAGS_ONE" : "SPIRE_MOD_MANAGER.MORE_TAGS", ("count", Menus.Number(count)));

    // A row's buttons for these items: the ones it has, given them (Menus.Refill), or new ones, placed (margin: from
    // the row's right edge) before they join the row. path: their name's, to find them by.
    static HBoxContainer Buttons(NModMenuRow row, string name, NodePath path, List<MenuItem> items, string does, float margin)
    {
        if (row.GetNodeOrNull<HBoxContainer>(path) is not { } buttons || !Menus.Refill(buttons, items))
        {
            Drop(row, path);
            buttons = Menus.InlineButtons(items, name);
            buttons.AnchorLeft = buttons.AnchorRight = 1;
            buttons.AnchorBottom = 1;
            buttons.GrowHorizontal = Control.GrowDirection.Begin;
            buttons.OffsetRight = buttons.OffsetLeft = margin;
            row.AddChild(buttons);
        }
        buttons.SetMeta(SaysMeta, does);
        buttons.Visible = true; // as new ones are
        return buttons;
    }

    // The line's look, one Theme on every line (made again when the font changes with the game's language): its gap,
    // the note's text, the author's, and the keywords' and "and 3 more..."'s (fainter, the Faint variation of a label).
    // The tags have their own (Tags.Look), which their text takes first.
    static (Font Font, Theme Theme)? _look;

    static (Font Font, Theme Theme) Look()
    {
        var font = ModEntry.Font("res://themes/kreon_regular_shared.tres");
        if (_look is { } made && made.Font == font)
            return made;
        var theme = new Theme();
        theme.SetConstant("separation", "HBoxContainer", 6);
        theme.SetFont("normal_font", "RichTextLabel", font);
        theme.SetFontSize("normal_font_size", "RichTextLabel", 13);
        theme.SetColor("default_color", "RichTextLabel", NoteColor);
        theme.SetFont("font", "Label", font);
        theme.SetFontSize("font_size", "Label", LabelSize);
        theme.SetColor("font_color", "Label", AuthorColor);
        theme.SetTypeVariation(Faint, "Label");
        theme.SetColor("font_color", Faint, Color.FromHtml(FaintHex));
        _look = (font, theme);
        return (font, theme);
    }

    // The note ("Needs: …", "Required for 3 mods") is made only for a line that has one: rows with none each had an
    // empty rich text label, one of the costlier parts of a long list's lines (#46). Without one, a spacer keeps the
    // author at the line's end, where the note's room put it. SetNote changes a kept line's note in place; true if it
    // changed.
    const string NeedsName = "Needs", AuthorName = "Author", SpacerName = "Spacer";

    static RichTextLabel Note(string text) => new()
    {
        Name = NeedsName, BbcodeEnabled = true, Text = text, ScrollActive = false,
        AutowrapMode = TextServer.AutowrapMode.Off, VerticalAlignment = VerticalAlignment.Center,
        SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.Fill,
        ClipContents = true, MouseFilter = Control.MouseFilterEnum.Ignore,
    };

    static Control Spacer() => new() { Name = SpacerName, MouseFilter = Control.MouseFilterEnum.Ignore, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };

    static bool SetNote(HBoxContainer strip, string text)
    {
        var note = strip.GetNodeOrNull<RichTextLabel>(NeedsPath);
        if ((note?.Text ?? "") == text)
            return false;
        if (note != null && text.Length > 0)
        {
            note.Text = text;
            return true;
        }
        Drop(strip, NeedsPath);
        Drop(strip, SpacerPath);
        var author = strip.GetNodeOrNull<Control>(AuthorPath);
        if (text.Length == 0 && author == null)
            return true;
        var part = text.Length > 0 ? Note(text) : Spacer();
        strip.AddChild(part);
        if (author != null)
            strip.MoveChild(part, author.GetIndex()); // before the author, after the tags, keywords and "and N more..."
        return true;
    }

#if DEVTOOLS
    // Self-test: what each part of Apply took, added up while Timing is on (the many-mods test, the --real tour).
    public static bool Timing { get; set; }
    public static readonly Dictionary<string, double> Costs = [];
    static readonly System.Diagnostics.Stopwatch Watch = new();
#endif

    // Ends the part being timed (null: a row's start) and starts the next. The calls vanish from the Workshop build.
    [System.Diagnostics.Conditional("DEVTOOLS")]
    static void Lap(string? part)
    {
#if DEVTOOLS
        if (!Timing)
            return;
        if (part != null)
            Costs[part] = Costs.GetValueOrDefault(part) + Watch.Elapsed.TotalMilliseconds;
        Watch.Restart();
#endif
    }

    // Shows as many tags and keywords as fit beside the author and the needs (up to half the line), tags first, whole
    // ones only; the rest are "and N more..." at their end.
    static void Fold(NModMenuRow row, HBoxContainer strip, IReadOnlyList<Tags.Chip> chips, IReadOnlyList<string> keywords)
    {
        if (!GodotObject.IsInstanceValid(row) || !GodotObject.IsInstanceValid(strip) || !strip.IsInsideTree())
            return;
        var gap = strip.GetThemeConstant(Separation);
        var room = row.Size.X - strip.OffsetLeft + strip.OffsetRight; // the line's own width (a line too full grows past it)
        var needs = strip.GetNodeOrNull<RichTextLabel>(NeedsPath) is { } note && note.GetParsedText() is { Length: > 0 } said
            ? RowTitles.Measure(note.GetThemeFont(NormalFont), said, note.GetThemeFontSize(NormalFontSize)) : 0;
        var kept = Math.Min(needs, room / 2) + (strip.GetNodeOrNull<Control>(AuthorPath) is { } author ? gap + author.GetCombinedMinimumSize().X : 0);
        // Each tag with the gap after it (the chips come first on the line, in order), then the keywords: the first
        // with "Additional keywords:" before it, each after a comma, and the gap after the last.
        var tags = strip.GetChildren().OfType<Control>().Take(chips.Count).ToList();
        var links = strip.GetNodeOrNull<Label>(KeywordsPath);
        var (font, size, title) = (Look().Font, LabelSize, KeywordsLine([]));
        var tagWidths = tags.Select(t => t.GetCombinedMinimumSize().X + gap).ToList();
        float Width(int count) => tagWidths.Take(count).Sum() + (count <= tags.Count || links == null ? 0
            : RowTitles.Measure(font, title + string.Join(", ", keywords.Take(count - tags.Count)), size) + gap);
        var all = tags.Count + (links != null ? keywords.Count : 0);
        var count = all;
        if (kept + Width(all) > room)
        {
            count--;
            while (count > 0 && kept + Width(count) + RowTitles.Measure(font, MoreText(all - count), size) + gap > room)
                count--;
        }
        if (strip.HasMeta(ShownMeta) && (int)strip.GetMeta(ShownMeta) == count)
            return;
        strip.SetMeta(ShownMeta, count);
        for (var i = 0; i < tags.Count; i++)
            tags[i].Visible = i < count;
        var words = Math.Max(0, count - tags.Count);
        if (links != null)
            (links.Visible, links.Text) = (words > 0, title + string.Join(", ", keywords.Take(words)));
        Drop(strip, MorePath);
        if (count == all)
            return;
        var more = More([.. chips.Skip(count)], [.. keywords.Skip(words)]);
        strip.AddChild(more);
        strip.MoveChild(more, (links ?? tags.LastOrDefault())?.GetIndex() + 1 ?? 0); // after the tags and keywords, before the note
    }

    const string EdgeName = "SpireModManagerEdit";

    static void Edge(NModMenuRow row, MenuItem? item)
    {
        var old = row.GetNodeOrNull<Button>(EdgePath);
        if (item?.OnSelect is not { } action)
        {
            if (old != null)
                old.Visible = false; // kept for the way back to the main list
            return;
        }
        if (old != null)
        {
            old.Visible = true;
            return; // (what it does doesn't change: it opens this row's menu)
        }
        var button = Menus.SmallButton(item.Label, action, name: EdgeName);
        Menus.Flatten(button);
        Hover.Tip(button, item.Tip);
        // Placed before it joins the row: in the page, each of these would lay it out again.
        (button.AnchorLeft, button.AnchorRight, button.AnchorTop, button.AnchorBottom) = (1, 1, 0.5f, 0.5f);
        button.GrowHorizontal = Control.GrowDirection.Begin;
        (button.OffsetLeft, button.OffsetRight, button.OffsetTop, button.OffsetBottom) = (-2, -2, -13, 13);
        row.AddChild(button);
    }

    // Button widths per set of labels, measured once (shaping text is slow, and most rows share their labels).
    static readonly Dictionary<string, float> Widths = [];

    static float Width(Control buttons, List<MenuItem> items)
    {
        var key = $"{ModEntry.Font("res://themes/kreon_regular_shared.tres").GetInstanceId()}\n"
            + string.Join("\n", items.Select(i => $"{i.Label}{(i.Flat ? "~" : "")}"));
        if (!Widths.TryGetValue(key, out var width))
            Widths[key] = width = buttons.GetCombinedMinimumSize().X;
        return width;
    }

    // Vanilla View: the line and buttons hidden, not removed (remaking them was slow), and the title full height again.
    public static void Hide(IEnumerable<NModMenuRow> rows)
    {
        foreach (var row in rows)
        {
            foreach (var path in HiddenParts)
                if (row.GetNodeOrNull<Control>(path) is { } part)
                    part.Visible = false;
            row.GetNodeOrNull(StripPath)?.SetMeta(SaysMeta, ""); // so the next Apply shows it again
            if (!row.HasMeta(StripMeta))
                continue;
            row.GetNode<Control>(TitlePath).OffsetBottom += Height + TitleGap;
            row.RemoveMeta(StripMeta);
        }
    }

    static readonly NodePath[] HiddenParts = [StripPath, ActionsPath, OrderActionsPath, EdgePath];

    static void Drop(Node row, NodePath path)
    {
        if (row.GetNodeOrNull(path) is not { } old)
            return;
        row.RemoveChild(old);
        old.QueueFree();
    }

    // "Needs: BaseLib, RitsuLib", each dependency green when fine, red with why when missing, off or loading after this
    // mod ("before"/"after" in Load Order). The line of a mod that won't load (off, or another copy of it loads) only
    // shows whether each dependency loads (any copy of it).
    public static string Needs(Mod? mod, IReadOnlyDictionary<Mod, int> order, bool loadOrder)
    {
        var deps = Dependencies.Of(mod);
        if (mod == null || deps.Count == 0)
            return "";
        var on = Copies.Loads(mod);
        var parts = deps.Select(id =>
        {
            var name = Escape(Dependencies.Find(id)?.manifest?.name ?? id);
            var issue = on ? Dependencies.Check(mod, id, order) : Dependencies.Problem.None;
            // With a status word ("before", "after", "(off)"...), the name is plain and the word carries the colour;
            // without one, the name does.
            var (word, color) = issue switch
            {
                Dependencies.Problem.Missing => (Menus.Text("SPIRE_MOD_MANAGER.DEP_MISSING"), ProblemHex),
                Dependencies.Problem.Off => (Menus.Text("SPIRE_MOD_MANAGER.DEP_OFF"), ProblemHex),
                Dependencies.Problem.LoadsBefore => (Menus.Text("SPIRE_MOD_MANAGER.DEP_AFTER"), ProblemHex),
                _ when loadOrder && on => (Menus.Text("SPIRE_MOD_MANAGER.DEP_BEFORE"), FineHex),
                // A mod that won't load: no problems to report yet, but each dependency still shows whether it loads.
                _ => (null, Dependencies.Find(id) is { } dep && Copies.Loads(dep) ? FineHex : ProblemHex),
            };
            if (word != null)
                return $"[color={NameHex}]{name}[/color] [color={color}]{word}[/color]";
            return $"[color={color}]{name}[/color]";
        });
        return $"{Menus.Text("SPIRE_MOD_MANAGER.NEEDS_LABEL")}: {string.Join(", ", parts)}";
    }

    // "Required for 3 mods": how many mods that load need this one; red when no copy of it loads.
    public static string RequiredFor(Mod? mod)
    {
        if (mod?.manifest?.id is not { } id)
            return "";
        var count = Dependencies.NeededBy(id).Count(m => m != mod && Copies.Loads(m));
        if (count == 0)
            return "";
        var text = Menus.Format(count == 1 ? "SPIRE_MOD_MANAGER.REQUIRED_FOR_ONE" : "SPIRE_MOD_MANAGER.REQUIRED_FOR", ("count", Menus.Number(count)));
        return $"[color={(Copies.Loading(id) != null ? RequiredHex : ProblemHex)}]{text}[/color]";
    }

    const string FineHex = "#9fdc9f", ProblemHex = "#ff8070", NameHex = "#fff6e2", RequiredHex = "#e8cf7a";

    static string Escape(string text) => text.Replace("[", "[lb]");
}
