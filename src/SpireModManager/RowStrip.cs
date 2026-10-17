using Godot;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;

namespace SpireModManager;

// The line along the bottom of each mod row in our views: its tags (extra ones fold into "+N"), what it needs (red
// when a dependency is missing, off or loads after it) and its author; the row's action buttons sit on the right.
static class RowStrip
{
    const string StripName = "SpireModManagerStrip";
    public const string ActionsName = "SpireModManagerActions", OrderActionsName = "SpireModManagerOrderActions"; // main list, Load Order
    public const string MoreName = "SpireModManagerMoreTags"; // the "+N" chip
    const string StripMeta = "spire_mod_manager_strip";
    const string SaysMeta = "spire_mod_manager_says"; // on a strip or buttons: what they show and where, to know when to change them
    const string ShowsMeta = "spire_mod_manager_shows"; // on a strip: its tags and author, to know when to remake it
    const string ShownMeta = "spire_mod_manager_tags_shown"; // on a strip: how many tags show (the rest fold into "+N")
    const float Height = 20;
    const float TitleGap = 4; // more room between the title and the line under it
    const float RightMargin = 84; // from the row's right edge: clear of the (smaller, moved) tickbox and the pencil
    const float LoadOrderMargin = 20; // Load Order has no tickbox
    const float ActionsGap = 12;
    const float AuthorMax = 200; // px
    static readonly Color Note = new(0.937f, 0.784f, 0.318f, 0.75f);

    // edge: a quiet button at the row's far right, past the tickbox (the main list's pencil), or none.
    public static void Apply(IList<NModMenuRow> rows, Func<NModMenuRow, IEnumerable<MenuItem>> actions, Func<NModMenuRow, MenuItem>? edge = null)
    {
        var order = LoadOrder.Mods(rows).Select((m, i) => (m, i)).DistinctBy(p => p.m).ToDictionary(p => p.m, p => p.i);
        // Each view keeps its own buttons and hides the other's, so a switch between the main list and Load Order
        // doesn't make every row's again.
        var (name, other) = Store.Data.LoadOrderView ? (OrderActionsName, ActionsName) : (ActionsName, OrderActionsName);
        foreach (var row in rows)
        {
            var title = row.GetNode<Control>("Title");
            if (!row.HasMeta(StripMeta))
            {
                title.OffsetBottom -= Height + TitleGap;
                row.SetMeta(StripMeta, true);
            }

            Edge(row, edge?.Invoke(row));

            // The buttons, made again only when they'd differ: their labels and states, and in Load Order the
            // row's place (what Up and Down do depends on it).
            var items = actions(row).ToList();
            var place = Store.Data.LoadOrderView ? $"{row.GetIndex()}/{row.GetParent()?.GetChildCount()}" : "";
            var does = $"{place}|{string.Join("|", items.Select(i => $"{i.Label}/{i.Disabled}/{i.Flat}/{i.Tip}"))}";
            if (row.GetNodeOrNull<Control>(name) is not { } buttons || !buttons.HasMeta(SaysMeta) || (string)buttons.GetMeta(SaysMeta) != does)
            {
                Drop(row, name);
                buttons = Menus.InlineButtons(items, name);
                buttons.SetMeta(SaysMeta, does);
                row.AddChild(buttons);
                buttons.AnchorLeft = buttons.AnchorRight = 1;
                buttons.AnchorBottom = 1;
                buttons.GrowHorizontal = Control.GrowDirection.Begin;
            }
            buttons.OffsetRight = buttons.OffsetLeft = Store.Data.LoadOrderView ? -LoadOrderMargin : -RightMargin;
            var clear = buttons.OffsetRight - Width(buttons, items) - ActionsGap;
            title.OffsetRight = clear;

            // The line is remade only when its tags or author change (remaking every row's on each keystroke was most of
            // a redraw's time); otherwise it's updated in place.
            var chips = Tags.For(row.Mod);
            var text = string.Join("    ", new[] { RequiredFor(row.Mod), Needs(row.Mod, order, Store.Data.LoadOrderView) }.Where(s => s.Length > 0));
            var author = ModDetails.Short(row.Mod?.manifest?.author ?? "");
            var shows = $"{string.Join("|", chips.Select(c => $"{c.Text}/{c.Color}/{c.Custom}"))}|{author}";
            var says = $"{shows}|{Store.Data.LoadOrderView}|{title.OffsetLeft}|{clear}|{text}";
            var kept = row.GetNodeOrNull<HBoxContainer>(StripName);
            if (kept?.GetMeta(SaysMeta, "").AsString() == says)
                continue;
            if (row.GetNodeOrNull<Control>(other) is { } hidden)
                hidden.Visible = false;
            buttons.Visible = true;
            if (kept?.GetMeta(ShowsMeta, "").AsString() == shows)
            {
                kept.SetMeta(SaysMeta, says);
                (kept.OffsetLeft, kept.OffsetRight, kept.Visible) = (title.OffsetLeft, clear, true);
                var needs = kept.GetNode<RichTextLabel>("Needs");
                if (needs.Text != text)
                {
                    needs.Text = text;
                    if (chips.Count > 0) // the tags fold again for the note's new width (a new width of the line refolds by itself)
                        Callable.From(() => Fold(row, kept, chips)).CallDeferred();
                }
                continue;
            }
            Drop(row, StripName);

            var strip = new HBoxContainer { Name = StripName, MouseFilter = Control.MouseFilterEnum.Ignore, ClipContents = true }; // too much for the line: cut at its edge, not under the buttons
            strip.SetMeta(SaysMeta, says);
            strip.SetMeta(ShowsMeta, shows);
            strip.AddThemeConstantOverride("separation", 6);
            row.AddChild(strip);
            strip.AnchorTop = strip.AnchorBottom = 1;
            strip.OffsetLeft = title.OffsetLeft;
            strip.AnchorRight = 1;
            strip.OffsetRight = clear;
            strip.OffsetTop = -Height - 2;
            strip.OffsetBottom = -2;

            foreach (var chip in chips)
                strip.AddChild(Tags.Make(chip, add => Search.ByChip(row, chip, add)));

            var note = new RichTextLabel
            {
                Name = "Needs", Text = text, BbcodeEnabled = true, ScrollActive = false,
                AutowrapMode = TextServer.AutowrapMode.Off, VerticalAlignment = VerticalAlignment.Center,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.Fill,
                ClipContents = true, MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            note.AddThemeFontOverride("normal_font", ModEntry.Font("res://themes/kreon_regular_shared.tres"));
            note.AddThemeFontSizeOverride("normal_font_size", 13);
            note.AddThemeColorOverride("default_color", Note);
            strip.AddChild(note);
            if (author.Length > 0)
            {
                // Who made it, quietly. (Searching by author is in the info panel.)
                var by = new Label { Name = "Author", Text = author, VerticalAlignment = VerticalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore };
                by.AddThemeFontOverride("font", ModEntry.Font("res://themes/kreon_regular_shared.tres"));
                by.AddThemeFontSizeOverride("font_size", 13);
                by.AddThemeColorOverride("font_color", new Color(1, 0.964706f, 0.886275f, 0.45f));
                // A long one is cut with "…"; the whole name after a second over it.
                var wide = by.GetThemeFont("font").GetStringSize(author, fontSize: 13).X;
                if (wide > AuthorMax)
                {
                    (by.ClipText, by.TextOverrunBehavior, by.CustomMinimumSize, by.MouseFilter) =
                        (true, TextServer.OverrunBehavior.TrimEllipsis, new Vector2(AuthorMax, 0), Control.MouseFilterEnum.Pass);
                    Hover.Watch(by, "SpireModManagerFullText", () => author.Replace("[", "[lb]"), Hover.Timing.Slow);
                }
                strip.AddChild(by);
            }

            // Too many tags for the line: the last ones fold into "+N" (Fold). Worked out once the chips are laid
            // out, and again whenever the line changes width.
            if (chips.Count > 0)
            {
                void Refold() => Fold(row, strip, chips);
                strip.Resized += Refold;
                Callable.From(Refold).CallDeferred();
            }
        }
    }

    // Shows as many tags as fit beside the author and the needs (up to half the line); the rest fold into "+N".
    static void Fold(NModMenuRow row, HBoxContainer strip, IReadOnlyList<Tags.Chip> chips)
    {
        if (!GodotObject.IsInstanceValid(row) || !GodotObject.IsInstanceValid(strip) || !strip.IsInsideTree())
            return;
        var gap = strip.GetThemeConstant("separation");
        var room = row.Size.X - strip.OffsetLeft + strip.OffsetRight; // the line's own width (a line too full grows past it)
        var note = strip.GetNode<RichTextLabel>("Needs");
        var needs = note.GetParsedText() is { Length: > 0 } said
            ? note.GetThemeFont("normal_font").GetStringSize(said, fontSize: note.GetThemeFontSize("normal_font_size")).X : 0;
        var kept = Math.Min(needs, room / 2) + (strip.GetNodeOrNull<Control>("Author") is { } author ? gap + author.GetCombinedMinimumSize().X : 0);
        // Each tag with the gap after it (the chips come first on the line, in order).
        var tags = strip.GetChildren().OfType<Control>().Take(chips.Count).ToList();
        var widths = tags.Select(t => t.GetCombinedMinimumSize().X + gap).ToList();
        var count = widths.Count;
        if (kept + widths.Sum() > room)
        {
            count--;
            while (count > 0 && kept + widths.Take(count).Sum() + Tags.MoreWidth(widths.Count - count) + gap > room)
                count--;
        }
        if (strip.HasMeta(ShownMeta) && (int)strip.GetMeta(ShownMeta) == count)
            return;
        strip.SetMeta(ShownMeta, count);
        for (var i = 0; i < tags.Count; i++)
            tags[i].Visible = i < count;
        Drop(strip, MoreName);
        if (count == tags.Count)
            return;
        var more = Tags.More(chips.Skip(count).ToList());
        more.Name = MoreName;
        strip.AddChild(more);
        strip.MoveChild(more, tags.Count); // after the tags, before the note
    }

    const string EdgeName = "SpireModManagerEdit";

    static void Edge(NModMenuRow row, MenuItem? item)
    {
        var old = row.GetNodeOrNull<Button>(EdgeName);
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
        row.AddChild(button);
        (button.AnchorLeft, button.AnchorRight, button.AnchorTop, button.AnchorBottom) = (1, 1, 0.5f, 0.5f);
        button.GrowHorizontal = Control.GrowDirection.Begin;
        (button.OffsetLeft, button.OffsetRight, button.OffsetTop, button.OffsetBottom) = (-2, -2, -13, 13);
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
            foreach (var name in new[] { StripName, ActionsName, OrderActionsName, EdgeName })
                if (row.GetNodeOrNull<Control>(name) is { } part)
                    part.Visible = false;
            row.GetNodeOrNull(StripName)?.SetMeta(SaysMeta, ""); // so the next Apply shows it again
            if (!row.HasMeta(StripMeta))
                continue;
            row.GetNode<Control>("Title").OffsetBottom += Height + TitleGap;
            row.RemoveMeta(StripMeta);
        }
    }

    static void Drop(Node row, string name)
    {
        if (row.GetNodeOrNull(name) is not { } old)
            return;
        row.RemoveChild(old);
        old.QueueFree();
    }

    // "Needs: BaseLib, RitsuLib", each dependency green when fine, red with why when missing, off or loading after this
    // mod ("before"/"after" in Load Order). A disabled mod's line only shows whether each dependency is on.
    public static string Needs(Mod? mod, IReadOnlyDictionary<Mod, int> order, bool loadOrder)
    {
        var deps = Dependencies.Of(mod);
        if (mod == null || deps.Count == 0)
            return "";
        var on = Dependencies.IsOn(mod);
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
                // A mod that's off: no problems to report yet, but each dependency still shows whether it's on.
                _ => (null, Dependencies.Find(id) is { } dep && Dependencies.IsOn(dep) ? FineHex : ProblemHex),
            };
            if (word != null)
                return $"[color={NameHex}]{name}[/color] [color={color}]{word}[/color]";
            return $"[color={color}]{name}[/color]";
        });
        return $"{Menus.Text("SPIRE_MOD_MANAGER.NEEDS_LABEL")}: {string.Join(", ", parts)}";
    }

    // "Required for 3 mods": how many mods that are on need this one; red when this one is off.
    public static string RequiredFor(Mod? mod)
    {
        if (mod?.manifest?.id is not { } id)
            return "";
        var count = Dependencies.NeededBy(id).Count(m => m != mod && Dependencies.IsOn(m));
        if (count == 0)
            return "";
        var text = Menus.Format(count == 1 ? "SPIRE_MOD_MANAGER.REQUIRED_FOR_ONE" : "SPIRE_MOD_MANAGER.REQUIRED_FOR", ("count", Menus.Number(count)));
        return $"[color={(Dependencies.IsOn(mod) ? RequiredHex : ProblemHex)}]{text}[/color]";
    }

    const string FineHex = "#9fdc9f", ProblemHex = "#ff8070", NameHex = "#fff6e2", RequiredHex = "#e8cf7a";

    static string Escape(string text) => text.Replace("[", "[lb]");
}
