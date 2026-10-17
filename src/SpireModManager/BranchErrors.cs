using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace SpireModManager;

// The game checks every Workshop mod against the Steam branches its author marked before it knows which mods are
// switched off, so a mod the player turned off still carries "only supports Steam branch ...": the main menu's MODDED
// warning turns red over it, and both info panels show it. A mod that's off doesn't load, so that error goes, as the
// game clears the errors of a copy it turns off as a duplicate. Switched on, the mod gets it again at the next launch.
static class BranchErrors
{
    const string Table = "main_menu_ui", Key = "MOD_ERROR.STEAM_BRANCH_UNSUPPORTED";

    // From ModEntry.Init: the game's branch check and the mods' off states are in place by then, and the main menu
    // reads the errors later.
    public static int DropFromOffMods()
    {
        Ran = true;
        return DropFromOffMods(ModManager.Mods);
    }

    // For the self-test: Init got here (a dev run has no Steam, so no real branch error to see go).
    public static bool Ran { get; private set; }

    // Returns how many went.
    public static int DropFromOffMods(IEnumerable<Mod> mods)
    {
        var dropped = new List<string>();
        foreach (var mod in mods)
        {
            if (mod.state != ModLoadState.Disabled || mod.errors is not { } errors)
                continue;
            if (errors.RemoveAll(e => e.LocTable == Table && e.LocEntryKey == Key) > 0)
                dropped.Add(mod.manifest?.id ?? "?");
            if (errors.Count == 0)
                mod.errors = null;
        }
        if (dropped.Count > 0)
            Log.Info($"[{ModEntry.ModId}] Dropped the Steam branch error of mods that are off (they don't load): {string.Join(", ", dropped)}");
        return dropped.Count;
    }
}
