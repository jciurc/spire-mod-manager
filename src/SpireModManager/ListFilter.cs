using Godot;
using MegaCrit.Sts2.Core.Modding;

namespace SpireModManager;

// The page bar's "Show:" choice on the main list: every mod, the enabled ones, the disabled ones (by their tickboxes
// now: what loads after a relaunch), or the ones changed since launch (the mods the Changes list names). Rows it
// leaves out hide like a search's misses, and a row shows when it matches both. Load Order and Vanilla List show
// every row.
// A mod ticked, unticked or edited by hand (its tickbox, its menu or the picked mods' menu, the info panel's fields)
// keeps its row (and its place in the counts) until a choice is pressed again or the page opens again, so a row never
// goes from under the mouse or an open menu; Enable all / Disable all, undo and a profile, changes to many mods at
// once, show the choice's rows at once. Going to a mod (a banner's Show, a name in the panel or in Changes) shows its
// row too. Kept for the session and never an edit (no undo step, not pending): each launch starts on All, as Changed
// counts from launch and a list left on Disabled would look like mods had gone.
static class ListFilter
{
    public enum Choice { All, Enabled, Disabled, Changed }

    // The choice pressed last, whichever view shows.
    public static Choice Chosen { get; private set; }

    // The choice in force: the main list's, All in Load Order and Vanilla List.
    public static Choice Current => Layout.Current == Layout.View.Categories ? Chosen : Choice.All;

    // Rows shown beside the choice's own (kept or gone to), until a choice is pressed again.
    static readonly HashSet<Mod> Kept = [];

    // The mods the last main-list redraw showed (choice and search both): the rows a change can keep.
    static HashSet<Mod> _shown = [];

    // Whether the choice lets a mod's row show (the search is CategoryList's).
    public static bool Shows(Mod? mod) => Current switch
    {
        Choice.All => true,
        _ when mod == null => false,
        _ when Kept.Contains(mod) => true,
        Choice.Enabled => Dependencies.IsOn(mod),
        Choice.Disabled => !Dependencies.IsOn(mod),
        _ => Changed().Contains(mod),
    };

    // Worked out once per redraw: it compares our data and the mod list with launch.
    static HashSet<Mod> Changed() => Lookups.Current?.Changed(HistoryBar.ChangedMods) ?? HistoryBar.ChangedMods();

    // A choice pressed (the chosen one again too: the rows kept meanwhile go), the list from its top as for a search.
    public static void Choose(Control content, Choice choice)
    {
        Chosen = choice;
        Kept.Clear();
        CategoryList.Apply(content);
        CategoryList.ScrollToTop(content);
    }

    // The main list's redraw, with what it showed.
    public static void Showed(IEnumerable<Mod?> mods) => _shown = mods.OfType<Mod>().ToHashSet();

    // A mod changed on its own (its tickbox, its menu, the info panel's fields): its row stays while the main list
    // shows a choice, if the last redraw showed it. Every copy of the id: edits belong to the id.
    public static void Keep(string? id)
    {
        if (Current != Choice.All && id != null)
            Kept.UnionWith(_shown.Where(m => m.manifest?.id == id));
    }

    // Picked mods edited together in their menu (the bulk menu's category and fields): each keeps its row, as one
    // mod's menu does.
    public static void Keep(IEnumerable<string> ids)
    {
        foreach (var id in ids)
            Keep(id);
    }

    // A mod gone to (CategoryList.Reveal): shown too if the choice leaves it out. True if it was.
    public static bool Bring(Mod? mod) => mod != null && !Shows(mod) && Kept.Add(mod);

    // The page opened again: kept rows go by the choice again. True if there were any (a redraw shows it).
    public static bool Release()
    {
        if (Kept.Count == 0)
            return false;
        Kept.Clear();
        return true;
    }
}
