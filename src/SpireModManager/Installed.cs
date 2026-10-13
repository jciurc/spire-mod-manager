using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Saves;

namespace SpireModManager;

// Mods installed while the game runs (a Workshop subscription, a folder dropped in). The game shows them on
// the Modding page, greyed, and loads them after a relaunch, but leaves them out of its mod list
// (ModManager.Mods) and its saved settings. So they're remembered here, for Changes and for profiles (one joins
// the active profile at once); and a tickbox change gets the settings entry it needs to stick (see NewModTogglePatch).
static class Installed
{
    static readonly List<Mod> _mods = [];

    public static IReadOnlyList<Mod> Mods => _mods;

    // Raised when one is added (Changes counts it, the active profile takes it in).
    public static event Action? Added;

    public static void Watch() => ModManager.OnModDetected += Detected;

    public static void Detected(Mod mod)
    {
        if (mod.state != ModLoadState.AddedAtRuntime || mod.manifest?.id is not { } id)
            return;
        if (ModManager.Mods.Any(m => m.manifest?.id == id && m.modSource == mod.modSource))
            return; // an update of a mod that was here at launch, not a new one
        _mods.RemoveAll(m => m.manifest?.id == id && m.modSource == mod.modSource);
        _mods.Add(mod);
        Added?.Invoke();
    }

    // Self-test only: takes a made-up mod off the list again.
    public static void Forget(Mod mod) => _mods.Remove(mod);

    // Whether it will load after a relaunch: on unless the saved list says off (the game's own rule).
    public static bool On(Mod mod) => !(SaveManager.Instance.SettingsSave.ModSettings?.IsModDisabled(mod) ?? false);
}
