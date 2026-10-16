using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Saves;

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
        if (ModManager.Mods.Any(m => m != mod && m.state != ModLoadState.AddedAtRuntime && m.manifest?.id == id && m.modSource == mod.modSource))
            return; // an update of a mod loaded at launch, not a new mod
        _mods.RemoveAll(m => m.manifest?.id == id && m.modSource == mod.modSource);
        _mods.Add(mod);
        Added?.Invoke();
    }

    // Self-test only: removes a made-up mod.
    public static void Forget(Mod mod) => _mods.Remove(mod);

    // Whether it loads after a relaunch: on unless the saved list says off.
    public static bool On(Mod mod) => !(SaveManager.Instance.SettingsSave.ModSettings?.IsModDisabled(mod) ?? false);
}
