using Godot;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;

namespace SpireModManager;

// Several mods picked at once (Ctrl-click, Shift-click, Ctrl+A, a category's "Select all"); a plain click, Esc or
// Ctrl+D lets go. Two or more picked rows get a blue tint; the main list edits them together, Load Order drags them.
// Each change to the tinted set is an undo step (History.Picks); picking a single mod with a plain click isn't.
static class Selection
{
    const string TintName = "SpireModManagerPicked";
    public static readonly Color Tint = new(0.35f, 0.6f, 0.95f, 0.18f); // the picked rows' blue (the page bar's chip too)
    static readonly HashSet<Mod> Picked = [];
    static Mod? _anchor;

    public static IReadOnlyCollection<Mod> Mods => Picked;
    public static bool Several => Picked.Count > 1;

    public static bool Contains(Mod? mod) => mod != null && Picked.Contains(mod);

    // The picks as an undo step, and an undo or redo's picks put back (History, which then has the page redraw them).
    public static History.Picks Now() => new([.. Picked], _anchor);

    public static void Set(History.Picks picks)
    {
        Picked.Clear();
        Picked.UnionWith(picks.Mods);
        _anchor = picks.Anchor;
    }

    public static void Pick(Control content, NModMenuRow row, bool toggle, bool range)
    {
        if (row.Mod is not { } mod)
            return;
        Changing(() =>
        {
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
        });
        Show(content);
    }

    // Adds rows (a category's "Select all", Ctrl+A).
    public static void Add(Control content, IEnumerable<NModMenuRow> rows)
    {
        Changing(() =>
        {
            foreach (var row in rows)
                if (row.Visible && row.Mod is { } mod)
                    Picked.Add(mod);
            _anchor ??= Picked.FirstOrDefault();
        });
        Show(content);
    }

    // True when there was something to let go of. Not an undo step when the view switches (record false): the picks
    // and their steps belong to the view they were made in (History.ForgetPicks).
    public static bool Clear(Control content, bool record = true)
    {
        if (Picked.Count == 0)
            return false;
        if (record)
            Changing(Forget);
        else
            Forget();
        Show(content);
        return true;

        static void Forget()
        {
            Picked.Clear();
            _anchor = null;
        }
    }

    // A change to the picks, recorded as an undo step when the tinted set (two or more) changed: the picks before it.
    static void Changing(Action change)
    {
        var before = Now();
        HashSet<Mod> shown = Several ? [.. Picked] : [];
        change();
        if (!shown.SetEquals(Several ? Picked : []))
            History.RecordPicks(History.CurrentTrack, before);
    }

    // Draws the tints and the bar above the list.
    public static void Show(Control content)
    {
        foreach (var row in content.GetChildren().OfType<NModMenuRow>())
        {
            var on = Several && row.Mod is { } m && Picked.Contains(m);
            var tint = row.GetNodeOrNull<Panel>(TintName);
            if (tint == null && !on)
                continue;
            if (tint == null)
            {
                // The same shape as the row's own highlight.
                tint = new Panel { Name = TintName, MouseFilter = Control.MouseFilterEnum.Ignore, AnchorRight = 1, AnchorBottom = 1 };
                (tint.OffsetLeft, tint.OffsetTop, tint.OffsetRight, tint.OffsetBottom) = (RowTitles.LookInset.X, RowTitles.LookInset.Y, RowTitles.LookInset.Z, RowTitles.LookInset.W);
                var look = new StyleBoxFlat { BgColor = Tint };
                look.SetCornerRadiusAll(RowTitles.CornerRadius);
                tint.AddThemeStyleboxOverride("panel", look);
                row.AddChild(tint);
                row.MoveChild(tint, 0); // behind everything else in the row
            }
            tint.Visible = on;
        }
        LoadOrder.UpdateToolbar(content, content.GetChildren().OfType<NModMenuRow>().ToList(), () => CategoryList.Redraw(content));
    }
}
