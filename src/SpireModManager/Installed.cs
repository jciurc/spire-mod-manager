using MegaCrit.Sts2.Core.Modding;

namespace SpireModManager;

// Mods installed while the game runs (Workshop subscriptions). The game lists them in ModManager.Mods, unloaded and
// with no saved settings entry, so they're tracked here for Changes and profiles (join with ModManager.Mods by Union).
static class Installed
{
    static readonly List<Mod> _mods = [];

    public static IReadOnlyList<Mod> Mods => _mods;

    // Raised when one is added.
    public static event Action? Added;

    public static void Watch() => ModManager.OnModDetected += Detected;

    public static void Detected(Mod mod)
    {
        if (mod.state != ModLoadState.AddedAtRuntime || mod.manifest?.id is not { } id)
            return;
        // The same copy (its Workshop item, or folder) again is an update; another one of the id is a new copy.
        bool Same(Mod m) => m.manifest?.id == id && m.modSource == mod.modSource && Copies.CopyOf(m) == Copies.CopyOf(mod);
        if (ModManager.Mods.Any(m => m != mod && m.state != ModLoadState.AddedAtRuntime && Same(m)))
            return; // an update of a mod loaded at launch, not a new mod
        _mods.RemoveAll(Same);
        _mods.Add(mod);
        Added?.Invoke();
    }

#if DEVTOOLS
    // Self-test: removes a made-up mod.
    public static void Forget(Mod mod) => _mods.Remove(mod);
#endif

    // Whether it loads after a relaunch: on unless the saved list says off, and the picked copy where it's another copy
    // of a mod from its source.
    public static bool On(Mod mod) => Dependencies.IsOn(mod);
}
