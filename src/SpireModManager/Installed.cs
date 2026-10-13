using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Saves;

namespace SpireModManager;

// Mods installed while the game runs (a Workshop subscription). The game reads the new mod into its mod list
// (ModManager.Mods, state AddedAtRuntime: not loaded), then announces it; it shows on the Modding page, greyed,
// and loads after a relaunch, but gets no saved settings entry. So they're remembered here, for Changes and for
// profiles (one joins the active profile at once; lists that join ModManager.Mods with these use Union: the same
// mod is in both); and a tickbox change gets the settings entry it needs to stick (see NewModTogglePatch).
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
        if (ModManager.Mods.Any(m => m != mod && m.state != ModLoadState.AddedAtRuntime && m.manifest?.id == id && m.modSource == mod.modSource))
            return; // an update of a mod that was here at launch, not a new one (the new mod itself is in the list too)
        _mods.RemoveAll(m => m.manifest?.id == id && m.modSource == mod.modSource);
        _mods.Add(mod);
        Added?.Invoke();
    }

    // Self-test only: takes a made-up mod off the list again.
    public static void Forget(Mod mod) => _mods.Remove(mod);

    // Whether it will load after a relaunch: on unless the saved list says off (the game's own rule).
    public static bool On(Mod mod) => !(SaveManager.Instance.SettingsSave.ModSettings?.IsModDisabled(mod) ?? false);
}
