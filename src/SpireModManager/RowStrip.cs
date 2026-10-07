using Godot;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;

namespace SpireModManager;

// A thin line along the bottom of each mod row (not in Vanilla View): what the mod needs on the left,
// red when a dependency is missing, off, or loads after it; the row's actions as small buttons on the right.
// The row title moves up to make room.
static class RowStrip
{
    const string StripName = "SpireModManagerStrip";
    const string StripMeta = "spire_mod_manager_strip";
    const float Height = 20;
    const float RightMargin = 76; // from the row's right edge: clear of the (smaller) tickbox
    static readonly Color Note = new(0.937f, 0.784f, 0.318f, 0.75f);

    public static void Apply(IList<NModMenuRow> rows, Func<NModMenuRow, IEnumerable<MenuItem>> actions)
    {
        var order = LoadOrder.Mods(rows).Select((m, i) => (m, i)).ToDictionary(p => p.m, p => p.i);
        foreach (var row in rows)
        {
            RemoveStrip(row);
            var title = row.GetNode<Control>("Title");
            if (!row.HasMeta(StripMeta))
            {
                title.OffsetBottom -= Height;
                row.SetMeta(StripMeta, true);
            }

            var strip = new HBoxContainer { Name = StripName, MouseFilter = Control.MouseFilterEnum.Ignore };
            strip.AddThemeConstantOverride("separation", 6);
            row.AddChild(strip);
            strip.AnchorTop = strip.AnchorBottom = 1;
            strip.OffsetLeft = title.OffsetLeft;
            strip.AnchorRight = 1;
            strip.OffsetRight = -RightMargin;
            strip.OffsetTop = -Height - 4;
            strip.OffsetBottom = -4;

            foreach (var chip in Tags.For(row.Mod))
                strip.AddChild(Tags.Make(chip));

            var note = new RichTextLabel
            {
                Name = "Needs", Text = Needs(row.Mod, order, Store.Data.LoadOrderView), BbcodeEnabled = true, ScrollActive = false,
                AutowrapMode = TextServer.AutowrapMode.Off, VerticalAlignment = VerticalAlignment.Center,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.Fill,
                ClipContents = true, MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            note.AddThemeFontOverride("normal_font", ResourceLoader.Load<Font>("res://themes/kreon_regular_shared.tres"));
            note.AddThemeFontSizeOverride("normal_font_size", 14);
            note.AddThemeColorOverride("default_color", Note);
            strip.AddChild(note);
            strip.AddChild(Menus.InlineButtons(actions(row), "Actions"));
        }
    }

    public static void Remove(IEnumerable<NModMenuRow> rows)
    {
        foreach (var row in rows)
        {
            RemoveStrip(row);
            if (!row.HasMeta(StripMeta))
                continue;
            row.GetNode<Control>("Title").OffsetBottom += Height;
            row.RemoveMeta(StripMeta);
        }
    }

    static void RemoveStrip(NModMenuRow row)
    {
        if (row.GetNodeOrNull(StripName) is not { } old)
            return;
        row.RemoveChild(old);
        old.QueueFree();
    }

    // "Needs: BaseLib, RitsuLib" with each dependency coloured: green when fine, red (and why) when it is
    // missing, off, or loads after this mod. In Load Order every one says "before" or "after".
    // Disabled mods aren't checked: their dependencies don't matter until they're on.
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
                _ => (null, on ? FineHex : null),
            };
            if (word != null)
                return $"[color={NameHex}]{name}[/color] [color={color}]{word}[/color]";
            return color == null ? name : $"[color={color}]{name}[/color]";
        });
        return $"{Menus.Text("SPIRE_MOD_MANAGER.NEEDS_LABEL")}: {string.Join(", ", parts)}";
    }

    const string FineHex = "#9fdc9f", ProblemHex = "#ff8070", NameHex = "#fff6e2";

    static string Escape(string text) => text.Replace("[", "[lb]");
}
