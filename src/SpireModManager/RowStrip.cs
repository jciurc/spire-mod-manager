using Godot;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;

namespace SpireModManager;

// A thin line along the bottom of each mod row (not in Vanilla View): its tags, then what the mod needs,
// red when a dependency is missing, off, or loads after it, then (dim, at the end) who made it. Tags that don't
// fit fold into a "+N" chip. The row title moves up to make room. The row's actions are small buttons on the
// right, centred on the row like the tickbox; title and line stop short of them.
static class RowStrip
{
    const string StripName = "SpireModManagerStrip";
    public const string ActionsName = "SpireModManagerActions";
    public const string MoreName = "SpireModManagerMoreTags"; // the "+N" chip
    const string StripMeta = "spire_mod_manager_strip";
    const string SaysMeta = "spire_mod_manager_says"; // on a strip: what it shows, to know when to remake it
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
            if (row.GetNodeOrNull<Control>(ActionsName) is not { } buttons || !buttons.HasMeta(SaysMeta) || (string)buttons.GetMeta(SaysMeta) != does)
            {
                Drop(row, ActionsName);
                buttons = Menus.InlineButtons(items, ActionsName);
                buttons.SetMeta(SaysMeta, does);
                row.AddChild(buttons);
                buttons.AnchorLeft = buttons.AnchorRight = 1;
                buttons.AnchorBottom = 1;
                buttons.GrowHorizontal = Control.GrowDirection.Begin;
            }
            buttons.OffsetRight = buttons.OffsetLeft = Store.Data.LoadOrderView ? -LoadOrderMargin : -RightMargin;
            var clear = buttons.OffsetRight - Width(buttons, items) - ActionsGap;
            title.OffsetRight = clear;

            // The line itself only when what it says changed: making its nodes for every row on every redraw
            // (each keystroke in search) was most of a redraw's time.
            var chips = Tags.For(row.Mod);
            var text = string.Join("    ", new[] { RequiredFor(row.Mod), Needs(row.Mod, order, Store.Data.LoadOrderView) }.Where(s => s.Length > 0));
            var author = ModDetails.Short(row.Mod?.manifest?.author ?? "");
            var says = $"{title.OffsetLeft}|{clear}|{string.Join("|", chips.Select(c => $"{c.Text}/{c.Color}/{c.Custom}"))}|{text}|{author}";
            if (row.GetNodeOrNull<Control>(StripName) is { } kept && kept.HasMeta(SaysMeta) && (string)kept.GetMeta(SaysMeta) == says)
                continue;
            Drop(row, StripName);

            var strip = new HBoxContainer { Name = StripName, MouseFilter = Control.MouseFilterEnum.Ignore, ClipContents = true }; // too much for the line: cut at its edge, not under the buttons
            strip.SetMeta(SaysMeta, says);
            strip.AddThemeConstantOverride("separation", 6);
            row.AddChild(strip);
            strip.AnchorTop = strip.AnchorBottom = 1;
            strip.OffsetLeft = title.OffsetLeft;
            strip.AnchorRight = 1;
            strip.OffsetRight = clear;
            strip.OffsetTop = -Height - 2;
            strip.OffsetBottom = -2;

            foreach (var chip in chips)
            {
                var tag = chip.Text;
                strip.AddChild(Tags.Make(chip, add => Search.ByTag(row, tag, add)));
            }

            var note = new RichTextLabel
            {
                Name = "Needs", Text = text, BbcodeEnabled = true, ScrollActive = false,
                AutowrapMode = TextServer.AutowrapMode.Off, VerticalAlignment = VerticalAlignment.Center,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.Fill,
                ClipContents = true, MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            note.AddThemeFontOverride("normal_font", ResourceLoader.Load<Font>("res://themes/kreon_regular_shared.tres"));
            note.AddThemeFontSizeOverride("normal_font_size", 13);
            note.AddThemeColorOverride("default_color", Note);
            strip.AddChild(note);
            if (author.Length > 0)
            {
                // Who made it, quietly. (Searching by author is in the info panel.)
                var by = new Label { Name = "Author", Text = author, VerticalAlignment = VerticalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore };
                by.AddThemeFontOverride("font", ResourceLoader.Load<Font>("res://themes/kreon_regular_shared.tres"));
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

    // Shows as many tags as fit beside what must show: the author first, then what the mod needs (up to half the
    // line). The rest fold into a "+N" chip, whose card lists them. A line that fits shows every tag, as before.
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
            Drop(row, EdgeName);
            return;
        }
        if (old != null)
            return; // (what it does doesn't change: it opens this row's menu)
        var button = Menus.SmallButton(item.Label, action, name: EdgeName);
        Menus.Flatten(button);
        button.TooltipText = item.Tip ?? "";
        row.AddChild(button);
        (button.AnchorLeft, button.AnchorRight, button.AnchorTop, button.AnchorBottom) = (1, 1, 0.5f, 0.5f);
        button.GrowHorizontal = Control.GrowDirection.Begin;
        (button.OffsetLeft, button.OffsetRight, button.OffsetTop, button.OffsetBottom) = (-2, -2, -13, 13);
    }

    // The buttons' width, measured once per set of labels (measuring shapes their text: slow for every row of
    // every redraw, and most rows share their labels).
    static readonly Dictionary<string, float> Widths = [];

    static float Width(Control buttons, List<MenuItem> items)
    {
        var key = string.Join("\n", items.Select(i => $"{i.Label}{(i.Flat ? "~" : "")}"));
        if (!Widths.TryGetValue(key, out var width))
            Widths[key] = width = buttons.GetCombinedMinimumSize().X;
        return width;
    }

    public static void Remove(IEnumerable<NModMenuRow> rows)
    {
        foreach (var row in rows)
        {
            RemoveStrip(row);
            if (!row.HasMeta(StripMeta))
                continue;
            row.GetNode<Control>("Title").OffsetBottom += Height + TitleGap;
            row.RemoveMeta(StripMeta);
        }
    }

    static void RemoveStrip(NModMenuRow row)
    {
        Drop(row, StripName);
        Drop(row, ActionsName);
        Drop(row, EdgeName);
    }

    static void Drop(Node row, string name)
    {
        if (row.GetNodeOrNull(name) is not { } old)
            return;
        row.RemoveChild(old);
        old.QueueFree();
    }

    // "Needs: BaseLib, RitsuLib" with each dependency coloured: green when fine, red (and why) when it is
    // missing, off, or loads after this mod. In Load Order every one says "before" or "after".
    // Disabled mods aren't checked for problems (their dependencies don't matter until they're on), but each
    // dependency is still green when it's on and red when it isn't.
    static string Needs(Mod? mod, IReadOnlyDictionary<Mod, int> order, bool loadOrder)
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
    static string RequiredFor(Mod? mod)
    {
        if (mod?.manifest?.id is not { } id)
            return "";
        var count = ModManager.Mods.Count(m => m != mod && Dependencies.IsOn(m) && Dependencies.Of(m).Contains(id));
        if (count == 0)
            return "";
        var text = Menus.Format(count == 1 ? "SPIRE_MOD_MANAGER.REQUIRED_FOR_ONE" : "SPIRE_MOD_MANAGER.REQUIRED_FOR", ("count", count.ToString()));
        return $"[color={(Dependencies.IsOn(mod) ? RequiredHex : ProblemHex)}]{text}[/color]";
    }

    const string FineHex = "#9fdc9f", ProblemHex = "#ff8070", NameHex = "#fff6e2", RequiredHex = "#e8cf7a";

    static string Escape(string text) => text.Replace("[", "[lb]");
}
