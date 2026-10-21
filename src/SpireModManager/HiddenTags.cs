using MegaCrit.Sts2.Core.Modding;

namespace SpireModManager;

// Tags a search finds without a chip: a mod's state (#enabled, #loaded, #pending...), what changed since launch (#changed,
// #moved, #edited), our details (#renamed, #notes, #uncategorized) and what's wrong with it (#unmet, #needed, #order,
// #clash, #managers, #failed, #gameerror; #error, #warning and #problem gather them). English words in any language, as
// #stable is, and only after a "#": "enabled" alone is a word. A tag of the player's with the same name finds its mods
// too (Search.Matches). What needs the whole list is worked out once per redraw (Lookups).
static class HiddenTags
{
    static readonly Dictionary<string, Func<Mod, bool>> Finds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["enabled"] = Copies.Ticked,
        ["disabled"] = m => !Copies.Ticked(m),
        ["workshop"] = m => m.modSource == ModSource.SteamWorkshop,
        ["local"] = Tags.IsLocal, // the Local chip's, in any language
        ["installed"] = _ => true, // every row is an installed copy
        // A profile's mods that aren't installed have no rows (the missing-mods dialog lists them).
        ["uninstalled"] = _ => false,
        ["missing"] = _ => false,
        ["loaded"] = m => m.state == ModLoadState.Loaded,
        ["notloaded"] = m => m.state != ModLoadState.Loaded,
        ["pending"] = ModDetails.PendingRelaunch, // the panel's Pending relaunch (not a copy set aside, nor one that failed)
        ["new"] = Tags.IsNew, // the New chip's, in any language
        ["changed"] = m => ListFilter.Matches(m, ListFilter.Choice.Changed),
        ["moved"] = m => Whole("moved", Moved).Contains(m),
        ["edited"] = m => Whole("edited", Edited).Contains(m),
        ["renamed"] = m => Store.InfoFor(m)?.Alias is { Length: > 0 },
        ["notes"] = m => !string.IsNullOrWhiteSpace(Store.InfoFor(m)?.Notes),
        ["uncategorized"] = m => Store.InfoFor(m)?.Category is not { } name || Categories.Find(name) == null, // as the list files it
        ["unmet"] = Dependencies.Unmet,
        ["needed"] = Needed,
        ["order"] = Misordered,
        ["sameid"] = m => Copies.Others(m).Count > 0, // the Same ID chip's
        ["clash"] = Clash,
        ["aside"] = Copies.Loses, // ticked, but another copy of its id loads
        ["managers"] = Managers,
        ["failed"] = Failed,
        ["gameerror"] = GameError,
        ["error"] = Error,
        ["warning"] = Warning,
        ["problem"] = m => Error(m) || Warning(m),
    };

    // What the hidden tag of this name finds (any case); null for a name that isn't one.
    public static Func<Mod, bool>? Find(string name) => Finds.GetValueOrDefault(name);

    // What the red banners point to (a mod that won't load for what it needs, a disabled mod that one needs, one that
    // loads before what it needs), and mods the game failed, and this mod with no copy of it ticked.
    static bool Error(Mod mod) => Dependencies.Unmet(mod) || Needed(mod) || Misordered(mod) || Failed(mod) || SelfOff(mod);

    // What the yellow ones point to (copies of one mod ticked together, both mod managers ticked), and the game's errors.
    static bool Warning(Mod mod) => Clash(mod) || Managers(mod) || GameError(mod);

    // Not loading after a relaunch, though a mod that loads needs it (Dependencies.NeededOff).
    static bool Needed(Mod mod) => !Copies.Loads(mod) && Whole("needed", mods => [.. Dependencies.NeededOff(mods)]).Contains(mod);

    // Loading before a mod it needs, in the saved order: the mods Load Order's banner counts.
    static bool Misordered(Mod mod) => Copies.Loads(mod)
        && Whole("order", mods => [.. Dependencies.OrderViolations(LoadOrder.InOrder(mods)).Select(v => v.Mod)]).Contains(mod);

    // Ticked with another copy of its id ticked too: the same-ID banner's (Copies.Clashing).
    static bool Clash(Mod mod) => Copies.Others(mod).Count > 0 && Whole("clash", _ => [.. Copies.Clashing()]).Contains(mod);

    // One of the two mod managers' ticked copies, while both are ticked: the banner's.
    static bool Managers(Mod mod) => mod.manifest?.id is ModEntry.ModId or Bmm.ModId && Bmm.BothTicked && Bmm.TickedManagers().Contains(mod);

    static bool Failed(Mod mod) => Copies.Ticked(mod) && mod.state == ModLoadState.Failed;

    // On a disabled mod the game's errors are notes instead (BranchErrors), so it's ticked ones.
    static bool GameError(Mod mod) => Copies.Ticked(mod) && mod.errors is { Count: > 0 };

    // This mod with no copy of it ticked: the page goes at the relaunch.
    static bool SelfOff(Mod mod) => mod.manifest?.id == ModEntry.ModId && !Copies.Ticked(mod) && !Copies.Others(mod).Any(Copies.Ticked);

    // Each copy that moved since launch (HistoryBar.Moves, which lists a copy once however many times the game does).
    static HashSet<Mod> Moved(IReadOnlyList<Mod> mods)
    {
        var moved = HistoryBar.Moves().Select(m => Copies.SwitchKey(m.Mod)).ToHashSet();
        return [.. mods.Where(m => moved.Contains(Copies.SwitchKey(m)))];
    }

    // Each copy whose own details (category, alias, notes, tags) were edited since launch.
    static HashSet<Mod> Edited(IReadOnlyList<Mod> mods)
    {
        var edited = HistoryBar.EditedKeys();
        return [.. mods.Where(m => edited.Contains(Copies.Key(m)))];
    }

    // A set over the whole list, worked out once per redraw from its mods; outside one, from the game's, every time.
    static HashSet<Mod> Whole(string name, Func<IReadOnlyList<Mod>, HashSet<Mod>> make) =>
        Lookups.Current?.Whole(name, make) ?? make(ModManager.Mods);
}
