using Godot;
using MegaCrit.Sts2.Core.Modding;

namespace SpireModManager;

// The main list's "Show:" choice: All, Enabled, Disabled (by tickbox), or Changed since launch. Load Order and
// Vanilla List show every row.
// A mod changed by hand keeps its row until a choice is pressed again, so rows never vanish under the mouse. The
// choice lasts the session and starts on All.
static class ListFilter
{
    public enum Choice { All, Enabled, Disabled, Changed }

    // The choice pressed last, whichever view shows.
    public static Choice Chosen { get; private set; }

    // The choice in force: the main list's, All in Load Order and Vanilla List.
    public static Choice Current => Layout.Current == Layout.View.Categories ? Chosen : Choice.All;

    // Extra rows shown beside the choice's own, until a choice is pressed again.
    static readonly HashSet<Mod> Kept = [];

    // The mods the last main-list redraw showed.
    static HashSet<Mod> _shown = [];

    // Whether the choice lets a mod's row show (the search is CategoryList's).
    public static bool Shows(Mod? mod) => Current == Choice.All || (mod != null && Kept.Contains(mod)) || Matches(mod, Current);

    // Whether a mod is one a choice is for, kept rows aside: what its button counts.
    public static bool Matches(Mod? mod, Choice choice) => choice switch
    {
        Choice.All => true,
        _ when mod == null => false,
        Choice.Enabled => Dependencies.IsOn(mod),
        Choice.Disabled => !Dependencies.IsOn(mod),
        _ => Changed().Contains(mod),
    };

    // Worked out once per redraw.
    static HashSet<Mod> Changed() => Lookups.Current?.Changed(HistoryBar.ChangedMods) ?? HistoryBar.ChangedMods();

    // Applies a choice (pressing it again drops the kept rows) and shows the list from its top.
    public static void Choose(Control content, Choice choice)
    {
        Chosen = choice;
        Kept.Clear();
        CategoryList.Apply(content);
        CategoryList.ScrollToTop(content);
    }

    // The main list's redraw, with what it showed.
    public static void Showed(IEnumerable<Mod?> mods) => _shown = mods.OfType<Mod>().ToHashSet();

    // Keeps a mod's row after an edit by hand, if the last redraw showed it: the copy our data's key names (a mod
    // installed once, or the copy holding the plain id, by its id), not the other copies of its id.
    public static void Keep(string? key)
    {
        if (Current != Choice.All && key != null)
            Kept.UnionWith(_shown.Where(m => Copies.Key(m) == key));
    }

    // Keeps the rows of mods edited together in the bulk menu (their keys).
    public static void Keep(IEnumerable<string> keys)
    {
        foreach (var key in keys)
            Keep(key);
    }

    // Keeps the rows of every copy of these ids after a tick: it switches them together (the game's one switch for an
    // id and source; on our page, one copy on turns the others off).
    public static void KeepCopies(IEnumerable<string?> ids)
    {
        if (Current == Choice.All)
            return;
        var ticked = ids.OfType<string>().ToHashSet();
        Kept.UnionWith(_shown.Where(m => m.manifest?.id is { } id && ticked.Contains(id)));
    }

    // Shows a mod gone to even if the choice leaves it out; true if it was hidden.
    public static bool Bring(Mod? mod) => mod != null && !Shows(mod) && Kept.Add(mod);

    // Drops the kept rows (the page opened again, or many mods changed); true if there were any.
    public static bool Release()
    {
        if (Kept.Count == 0)
            return false;
        Kept.Clear();
        return true;
    }
}
