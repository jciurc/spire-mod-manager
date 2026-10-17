using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace SpireModManager;

// The game checks every Workshop mod against the Steam branches its author marked before it knows which mods are
// switched off, so a mod the player turned off still carries "only supports Steam branch ...": the main menu's MODDED
// warning turns red over it. A mod that's off doesn't load, so that error comes off the mod's errors, as the game clears
// the errors of a copy it turns off as a duplicate. Nothing of the game's is lost: the message is kept and shows,
// marked as the game's, in the mod's info panel and card (ModDetails.GameNotes), and the game's log has its own line.
// Switched on, the mod gets the error again at the next launch.
static class BranchErrors
{
    const string Table = "main_menu_ui", Key = "MOD_ERROR.STEAM_BRANCH_UNSUPPORTED";

    // The game's messages taken off each mod, for its info panel.
    static readonly Dictionary<Mod, List<LocString>> _dropped = [];

    public static IReadOnlyList<LocString> Dropped(Mod mod) => _dropped.TryGetValue(mod, out var messages) ? messages : [];

    // From ModEntry.Init: the game's branch check and the mods' off states are in place by then, and the main menu
    // reads the errors later.
    public static int DropFromOffMods()
    {
        Ran = true;
        return DropFromOffMods(ModManager.Mods);
    }

    // For the self-test: Init got here (a dev run has no Steam, so no real branch error to see go).
    public static bool Ran { get; private set; }

    // Returns how many mods had one.
    public static int DropFromOffMods(IEnumerable<Mod> mods)
    {
        var dropped = new List<string>();
        foreach (var mod in mods)
        {
            if (mod.state != ModLoadState.Disabled || mod.errors is not { } errors)
                continue;
            var branch = errors.Where(e => e.LocTable == Table && e.LocEntryKey == Key).ToList();
            if (branch.Count > 0)
            {
                errors.RemoveAll(branch.Contains);
                (_dropped.TryGetValue(mod, out var kept) ? kept : _dropped[mod] = []).AddRange(branch);
                dropped.Add(mod.manifest?.id ?? "?");
            }
            if (errors.Count == 0)
                mod.errors = null;
        }
        if (dropped.Count > 0)
            Log.Info($"[{ModEntry.ModId}] Took the Steam branch error off mods that are off (they don't load; their info panels still show it): {string.Join(", ", dropped)}");
        return dropped.Count;
    }

#if DEVTOOLS
    // Self-test: forget what the made-up errors left.
    public static void TestForget(Mod mod) => _dropped.Remove(mod);
#endif
}
