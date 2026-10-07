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
    const float Height = 22;
    const float RightMargin = 144; // from the row's right edge: clear of the game's platform icon and tickbox
    static readonly Color Note = new(0.937f, 0.784f, 0.318f, 0.75f);
    static readonly Color Problem = new(1f, 0.45f, 0.4f);

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

            var (text, problem) = Needs(row.Mod, order);
            var note = new Label
            {
                Name = "Needs", Text = text, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                VerticalAlignment = VerticalAlignment.Center, ClipText = true, MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            note.AddThemeFontOverride("font", ResourceLoader.Load<Font>("res://themes/kreon_regular_shared.tres"));
            note.AddThemeFontSizeOverride("font_size", 15);
            note.AddThemeColorOverride("font_color", problem ? Problem : Note);
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

    // "Needs BaseLib, RitsuLib", marking each dependency that is missing, off, or later in the order.
    static (string Text, bool Problem) Needs(Mod? mod, IReadOnlyDictionary<Mod, int> order)
    {
        var deps = Dependencies.Of(mod);
        if (mod == null || deps.Count == 0)
            return ("", false);
        var on = Dependencies.IsOn(mod);
        var problem = false;
        var parts = deps.Select(id =>
        {
            var name = Dependencies.Find(id)?.manifest?.name ?? id;
            var issue = on ? Dependencies.Check(mod, id, order) : Dependencies.Problem.None;
            problem |= issue != Dependencies.Problem.None;
            return issue switch
            {
                Dependencies.Problem.Missing => $"{name} ({Menus.Text("SPIRE_MOD_MANAGER.DEP_MISSING")})",
                Dependencies.Problem.Off => $"{name} ({Menus.Text("SPIRE_MOD_MANAGER.DEP_OFF")})",
                Dependencies.Problem.LoadsBefore => $"{name} ({Menus.Text("SPIRE_MOD_MANAGER.DEP_LOADS_AFTER")})",
                _ => name,
            };
        }).ToList();
        return (Menus.Format("SPIRE_MOD_MANAGER.NEEDS", ("mods", string.Join(", ", parts))), problem);
    }
}
