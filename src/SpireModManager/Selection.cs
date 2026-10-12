using Godot;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;

namespace SpireModManager;

// Several mods picked at once, on the main list and in Load Order: Ctrl-click toggles a row, Shift-click picks
// the rows between the last one picked and this one (as shown), Ctrl+A every row shown, a category's
// "Select all" its rows; a plain click or Esc lets go. Picked rows get a blue tint (two or more: one is just
// the selection the game already shows). The main list edits them together from a right-click (bulk menu);
// Load Order drags them together. Switching views lets go.
static class Selection
{
    const string TintName = "SpireModManagerPicked";
    static readonly HashSet<Mod> Picked = [];
    static Mod? _anchor;

    public static IReadOnlyCollection<Mod> Mods => Picked;
    public static bool Several => Picked.Count > 1;

    public static bool Contains(Mod? mod) => mod != null && Picked.Contains(mod);

    public static void Pick(Control content, NModMenuRow row, bool toggle, bool range)
    {
        if (row.Mod is not { } mod)
            return;
        // Starting a selection from the row the panel shows (as file lists do).
        if (Picked.Count == 0 && CategoryList.Selected is { } current && (toggle || range))
        {
            Picked.Add(current);
            _anchor = current;
        }
        var rows = content.GetChildren().OfType<NModMenuRow>().ToList();
        if (range && _anchor != null && rows.FindIndex(r => r.Mod == _anchor) is >= 0 and var from)
        {
            var to = rows.IndexOf(row);
            Picked.Clear();
            for (var i = Math.Min(from, to); i <= Math.Max(from, to); i++)
                if (rows[i].Visible && rows[i].Mod is { } m)
                    Picked.Add(m);
        }
        else if (toggle)
        {
            if (!Picked.Remove(mod))
                Picked.Add(mod);
            _anchor = mod;
        }
        else
        {
            Picked.Clear();
            Picked.Add(mod);
            _anchor = mod;
        }
        Show(content);
    }

    // Adds rows (a category's "Select all", Ctrl+A).
    public static void Add(Control content, IEnumerable<NModMenuRow> rows)
    {
        foreach (var row in rows)
            if (row.Visible && row.Mod is { } mod)
                Picked.Add(mod);
        _anchor ??= Picked.FirstOrDefault();
        Show(content);
    }

    // True when there was something to let go of.
    public static bool Clear(Control content)
    {
        if (Picked.Count == 0)
            return false;
        Picked.Clear();
        _anchor = null;
        Show(content);
        return true;
    }

    // The tints, then the bar above the list (it counts and acts on the selection).
    public static void Show(Control content)
    {
        foreach (var row in content.GetChildren().OfType<NModMenuRow>())
        {
            var on = Several && row.Mod is { } m && Picked.Contains(m);
            var tint = row.GetNodeOrNull<ColorRect>(TintName);
            if (tint == null && !on)
                continue;
            if (tint == null)
            {
                tint = new ColorRect
                {
                    Name = TintName, Color = new Color(0.35f, 0.6f, 0.95f, 0.18f), MouseFilter = Control.MouseFilterEnum.Ignore,
                    AnchorRight = 1, AnchorBottom = 1,
                };
                row.AddChild(tint);
                row.MoveChild(tint, 0); // behind everything else in the row
            }
            tint.Visible = on;
        }
        LoadOrder.UpdateToolbar(content, content.GetChildren().OfType<NModMenuRow>().ToList(), () => CategoryList.Redraw(content));
    }
}
