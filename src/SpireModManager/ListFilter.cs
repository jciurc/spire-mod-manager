using Godot;
using MegaCrit.Sts2.Core.Modding;

namespace SpireModManager;

// The main list's "Show:" choice: All, Enabled, Disabled (by tickbox), or Changed since launch. Load Order and
// Vanilla List show every row.
// A mod changed by hand keeps its row until a choice is pressed again, so rows never vanish under the mouse; under a
// search, it counts as one of its matches until the search changes. The choice lasts the session and starts on All.
static class ListFilter
{
    public enum Choice { All, Enabled, Disabled, Changed }

    // The choice pressed last, whichever view shows.
    public static Choice Chosen { get; private set; }

    // The choice in force: the main list's, All in Load Order and Vanilla List.
    public static Choice Current => Layout.Current == Layout.View.Categories ? Chosen : Choice.All;

    // Extra rows shown beside the choice's own, until a choice is pressed again.
    static readonly HashSet<Mod> Kept = [];

    // Extra rows counted among the search's matches, and the search they were kept under: until it changes.
    static readonly HashSet<Mod> Found = [];
    static string _search = "";

    // The mods the last main-list redraw showed (under a choice or a search).
    static HashSet<Mod> _shown = [];

    // Whether the choice lets a mod's row show (the search: Finds).
    public static bool Shows(Mod? mod) => Current == Choice.All || (mod != null && Kept.Contains(mod)) || Matches(mod, Current);

    // Whether the search lets a mod's row show: it matches, or it was kept under this search. Every row without one.
    public static bool Finds(Mod? mod, string filter) =>
        filter.Length == 0 || Search.Matches(mod, filter) || (mod != null && filter == _search && Found.Contains(mod));

    // The search a redraw goes by (CategoryList.Apply, every view): rows kept under another go by this one.
    public static void Searching(string filter)
    {
        if (filter == _search)
            return;
        _search = filter;
        Found.Clear();
    }

    // Whether a mod is one a choice is for, kept rows aside: what its button counts.
    public static bool Matches(Mod? mod, Choice choice) => choice switch
    {
        Choice.All => true,
        _ when mod == null => false,
        Choice.Enabled => Copies.Ticked(mod),
        Choice.Disabled => !Copies.Ticked(mod),
        _ => Changed().Contains(mod),
    };

    // Worked out once per redraw.
    static HashSet<Mod> Changed() => Lookups.Current?.Changed(HistoryBar.ChangedMods) ?? HistoryBar.ChangedMods();

    // Applies a choice (pressing it again drops the kept rows, the search's too) and shows the list from its top.
    public static void Choose(Control content, Choice choice)
    {
        Chosen = choice;
        Kept.Clear();
        Found.Clear();
        CategoryList.Apply(content);
        CategoryList.ScrollToTop(content);
    }

    // The main list's redraw, with what it showed.
    public static void Showed(IEnumerable<Mod?> mods) => _shown = mods.OfType<Mod>().ToHashSet();

    // Keeps a mod's row after an edit by hand, if the last redraw showed it: the copy our data's key names (a mod
    // installed once, or the copy holding the plain id, by its id), not the other copies of its id.
    public static void Keep(string? key)
    {
        if (key != null)
            Hold(_shown.Where(m => Copies.Key(m) == key));
    }

    // Keeps the rows of mods edited together in the bulk menu (their keys).
    public static void Keep(IEnumerable<string> keys)
    {
        foreach (var key in keys)
            Keep(key);
    }

    // Keeps the rows of every copy of these ids after a tick: it can change which of them loads, and the game's one switch
    // for an id and source sets its copies together (Vanilla View, a session that can't save our data).
    public static void KeepCopies(IEnumerable<string?> ids)
    {
        var ticked = ids.OfType<string>().ToHashSet();
        Hold(_shown.Where(m => m.manifest?.id is { } id && ticked.Contains(id)));
    }

    // Keeps these rows on the main list: under its choice (not All), and under its search (if there's one).
    static void Hold(IEnumerable<Mod> mods)
    {
        if (Layout.Current != Layout.View.Categories || (Chosen == Choice.All && _search.Length == 0))
            return;
        var held = mods.ToList();
        if (Chosen != Choice.All)
            Kept.UnionWith(held);
        if (_search.Length > 0)
            Found.UnionWith(held);
    }

    // Shows a mod gone to even if the choice leaves it out; true if it was hidden.
    public static bool Bring(Mod? mod) => mod != null && !Shows(mod) && Kept.Add(mod);

    // Drops the kept rows, the choice's and the search's (the page opened again, or many mods changed); true if there
    // were any.
    public static bool Release()
    {
        if (Kept.Count == 0 && Found.Count == 0)
            return false;
        Kept.Clear();
        Found.Clear();
        return true;
    }
}
