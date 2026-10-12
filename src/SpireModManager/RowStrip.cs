using Godot;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;

namespace SpireModManager;

// A thin line along the bottom of each mod row (not in Vanilla View): its tags, then what the mod needs,
// red when a dependency is missing, off, or loads after it, then (dim, at the end) who made it. The row title
// moves up to make room. The row's actions are small buttons on the right, centred on the row like the
// tickbox; title and line stop short of them.
static class RowStrip
{
    const string StripName = "SpireModManagerStrip";
    public const string ActionsName = "SpireModManagerActions";
    const string StripMeta = "spire_mod_manager_strip";
    const string SaysMeta = "spire_mod_manager_says"; // on a strip: what it shows, to know when to remake it
    const float Height = 20;
    const float TitleGap = 4; // more room between the title and the line under it
    const float RightMargin = 76; // from the row's right edge: clear of the (smaller) tickbox
    const float LoadOrderMargin = 20; // Load Order has no tickbox
    const float ActionsGap = 12;
    static readonly Color Note = new(0.937f, 0.784f, 0.318f, 0.75f);

    public static void Apply(IList<NModMenuRow> rows, Func<NModMenuRow, IEnumerable<MenuItem>> actions)
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

            // The buttons are made again each time (what they do depends on the row's place now).
            Drop(row, ActionsName);
            var items = actions(row).ToList();
            var buttons = Menus.InlineButtons(items, ActionsName);
            row.AddChild(buttons);
            buttons.AnchorLeft = buttons.AnchorRight = 1;
            buttons.AnchorBottom = 1;
            buttons.GrowHorizontal = Control.GrowDirection.Begin;
            buttons.OffsetRight = buttons.OffsetLeft = Store.Data.LoadOrderView ? -LoadOrderMargin : -RightMargin;
            var clear = buttons.OffsetRight - Width(buttons, items) - ActionsGap;
            title.OffsetRight = clear;

            // The line itself only when what it says changed: making its nodes for every row on every redraw
            // (each keystroke in search) was most of a redraw's time.
            var chips = Tags.For(row.Mod);
            var text = string.Join("    ", new[] { RequiredFor(row.Mod), Needs(row.Mod, order, Store.Data.LoadOrderView) }.Where(s => s.Length > 0));
            var author = row.Mod?.manifest?.author is { Length: > 0 } who ? Menus.Format("SPIRE_MOD_MANAGER.BY_AUTHOR", ("author", who)) : "";
            var says = $"{title.OffsetLeft}|{clear}|{string.Join("|", chips.Select(c => $"{c.Text}/{c.Color}/{c.Custom}"))}|{text}|{author}";
            if (row.GetNodeOrNull<Control>(StripName) is { } kept && kept.HasMeta(SaysMeta) && (string)kept.GetMeta(SaysMeta) == says)
                continue;
            Drop(row, StripName);

            var strip = new HBoxContainer { Name = StripName, MouseFilter = Control.MouseFilterEnum.Ignore };
            strip.SetMeta(SaysMeta, says);
            strip.AddThemeConstantOverride("separation", 6);
            row.AddChild(strip);
            strip.AnchorTop = strip.AnchorBottom = 1;
            strip.OffsetLeft = title.OffsetLeft;
            strip.AnchorRight = 1;
            strip.OffsetRight = clear;
            strip.OffsetTop = -Height - 4;
            strip.OffsetBottom = -4;

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
            note.AddThemeFontSizeOverride("normal_font_size", 14);
            note.AddThemeColorOverride("default_color", Note);
            strip.AddChild(note);
            if (author.Length > 0)
            {
                // Who made it, quietly, at the end of the line.
                // Who made it, quietly; click to see their other mods (Shift: add to the search).
                var maker = row.Mod?.manifest?.author ?? "";
                var by = new Label
                {
                    Name = "Author", Text = author, VerticalAlignment = VerticalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Stop,
                    MouseDefaultCursorShape = Control.CursorShape.PointingHand,
                };
                by.AddThemeFontOverride("font", ResourceLoader.Load<Font>("res://themes/kreon_regular_shared.tres"));
                by.AddThemeFontSizeOverride("font_size", 14);
                by.AddThemeColorOverride("font_color", new Color(1, 0.964706f, 0.886275f, 0.45f));
                by.GuiInput += e =>
                {
                    if (e is not InputEventMouseButton { ButtonIndex: MouseButton.Left } click)
                        return;
                    by.AcceptEvent(); // not the row's click too
                    if (!click.Pressed)
                        Search.ByAuthor(row, maker, click.ShiftPressed);
                };
                Hover.Watch(by, "SpireModManagerTagCard", Tags.CardDelay, () => Tags.Hint(Menus.Text("SPIRE_MOD_MANAGER.AUTHOR_CLICK_TIP")), width: 320);
                strip.AddChild(by);
            }
        }
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
            return color == null ? name : $"[color={color}]{name}[/color]";
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
