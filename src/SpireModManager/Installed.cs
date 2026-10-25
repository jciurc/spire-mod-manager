using Godot;
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

    // The frame the last one came in, and whether that's this one or the one before: the game announces a new mod as a
    // change of the mod list (OnModEnabledOrDisabled) as it comes, which isn't the player's step. Frames, not time: a
    // player's click soon after is a step.
    static ulong _arrivedIn;
    public static bool JustArrived => _arrivedIn != 0 && Engine.GetProcessFrames() - _arrivedIn <= 1;

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
        _arrivedIn = Engine.GetProcessFrames();
        Added?.Invoke();
    }

#if DEVTOOLS
    // Self-test: removes a made-up mod.
    public static void Forget(Mod mod) => _mods.Remove(mod);
#endif
}
