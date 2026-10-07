using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Saves;

namespace SpireModManager;

// Undo and redo for everything the player edits: the game's mod list (on/off and order) and our saved
// data (categories, aliases, notes, profiles). A snapshot is recorded after each change; undo restores
// the one before. View choices and collapsed categories aren't edits and are left as they are.
// The first snapshot is the state this session loaded with, which "Revert all" goes back to.
static class History
{
    public record ModEntry(string Id, ModSource Source, bool Enabled);
    public record Snapshot(string Store, IReadOnlyList<ModEntry> Mods);

    const int Limit = 200;
    static readonly List<Snapshot> Past = []; // last = now
    static readonly List<Snapshot> Future = [];
    static bool _restoring;

    // Raised after an undo, redo or revert, so open screens can redraw from the restored state.
    public static event Action? Restored;

    // Raised whenever the history moves (a new edit, undo, redo), for the Undo/Redo buttons.
    public static event Action? Changed;

    public static Snapshot? Loaded { get; private set; }
    public static bool CanUndo => Past.Count > 1;
    public static bool CanRedo => Future.Count > 0;

    // The baseline, once, when the game has finished loading (first main menu).
    public static void Start()
    {
        if (Loaded != null)
            return;
        Loaded = Take();
        Past.Add(Loaded);
    }

    // After any change. Repeats of the current state (several saves for one action) are ignored.
    public static void Record()
    {
        if (_restoring || Loaded == null)
            return;
        var now = Take();
        if (Same(now, Past[^1]))
            return;
        Past.Add(now);
        if (Past.Count > Limit)
            Past.RemoveAt(1); // keep the loaded baseline
        Future.Clear();
        Changed?.Invoke();
    }

    public static void Undo()
    {
        if (!CanUndo)
            return;
        Future.Add(Past[^1]);
        Past.RemoveAt(Past.Count - 1);
        Restore(Past[^1]);
    }

    public static void Redo()
    {
        if (!CanRedo)
            return;
        var next = Future[^1];
        Future.RemoveAt(Future.Count - 1);
        Past.Add(next);
        Restore(next);
    }

    // The game's mod list as this session loaded it; our data stays as it is. Undoable.
    public static void RevertMods()
    {
        if (Loaded == null)
            return;
        Apply(Loaded.Mods);
        Profiles.Track(); // the active profile follows now, so it's part of this one undo step
        Record();
        Restored?.Invoke();
        Changed?.Invoke();
    }

    public static IReadOnlyList<ModEntry> CurrentMods() =>
        SaveManager.Instance.SettingsSave.ModSettings?.ModList.Select(m => new ModEntry(m.Id, m.Source, m.IsEnabled)).ToList() ?? [];

    static Snapshot Take() => new(Store.EditsJson(), CurrentMods());

    static bool Same(Snapshot a, Snapshot b) => a.Store == b.Store && a.Mods.SequenceEqual(b.Mods);

    static void Restore(Snapshot snapshot)
    {
        _restoring = true;
        try
        {
            Apply(snapshot.Mods);
            Store.RestoreEdits(snapshot.Store);
        }
        finally
        {
            _restoring = false;
        }
        Restored?.Invoke();
        Changed?.Invoke();
    }

    static void Apply(IReadOnlyList<ModEntry> mods)
    {
        var settings = SaveManager.Instance.SettingsSave.ModSettings ??= new ModSettings();
        settings.ModList.Clear();
        settings.ModList.AddRange(mods.Select(m => new SettingsSaveMod { Id = m.Id, Source = m.Source, IsEnabled = m.Enabled }));
        SaveManager.Instance.SaveSettings();
    }
}
