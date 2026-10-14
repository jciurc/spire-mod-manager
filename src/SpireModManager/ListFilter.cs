using Godot;
using MegaCrit.Sts2.Core.Modding;

namespace SpireModManager;

// The page bar's "Show:" choice on the main list: every mod, the enabled ones, the disabled ones (by their tickboxes
// now: what loads after a relaunch), or the ones changed since launch (the mods the Changes list names). Rows it
// leaves out hide like a search's misses, and a row shows when it matches both. Load Order and Vanilla List show
// every row.
// Kept for the session and never an edit (no undo step, not pending): each launch starts on All, as Changed counts
// from launch and a list left on Disabled would look like mods had gone.
static class ListFilter
{
    public enum Choice { All, Enabled, Disabled, Changed }

    // The choice pressed last, whichever view shows.
    public static Choice Chosen { get; private set; }

    // The choice in force: the main list's, All in Load Order and Vanilla List.
    public static Choice Current => Layout.Current == Layout.View.Categories ? Chosen : Choice.All;

    // Whether the choice lets a mod's row show (the search is CategoryList's).
    public static bool Shows(Mod? mod) => Current switch
    {
        Choice.All => true,
        _ when mod == null => false,
        Choice.Enabled => Dependencies.IsOn(mod),
        Choice.Disabled => !Dependencies.IsOn(mod),
        _ => Changed().Contains(mod),
    };

    // Worked out once per redraw: it compares our data and the mod list with launch.
    static HashSet<Mod> Changed() => Lookups.Current?.Changed(HistoryBar.ChangedMods) ?? HistoryBar.ChangedMods();

    // A choice pressed, the list from its top as for a search.
    public static void Choose(Control content, Choice choice)
    {
        Chosen = choice;
        CategoryList.Apply(content);
        CategoryList.ScrollToTop(content);
    }
}
