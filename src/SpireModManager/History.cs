using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Saves;

namespace SpireModManager;

// Undo and redo, kept separately for the two kinds of edit:
// - List: which mods are on, and our data (categories, aliases, notes, tags, profiles);
// - Order: the load order.
// Each view steps through its own (Load Order view: Order; the others: List). A snapshot of everything
// is taken after each change; undoing restores only that history's part and leaves the other as it is
// now, so undoing an order change never flips a tickbox back. View choices and collapsed categories
// aren't edits. The first snapshot is the state this session loaded with ("Revert all", "Changes").
static class History
{
    public enum Track { List, Order }

    public record ModEntry(string Id, ModSource Source, bool Enabled);
    public record Snapshot(string Store, IReadOnlyList<ModEntry> Mods);

    // The last profile switch this session: who it was from, and the mod list right after it.
    public record Switch(string From, string To, IReadOnlyList<ModEntry> After);

    const int Limit = 200;
    static readonly Dictionary<Track, List<Snapshot>> Past = new() { [Track.List] = [], [Track.Order] = [] };
    static readonly Dictionary<Track, List<Snapshot>> Future = new() { [Track.List] = [], [Track.Order] = [] };
    static Snapshot? _current;
    static bool _restoring;

    // Raised after an undo, redo or revert, so open screens can redraw from the restored state.
    public static event Action? Restored;

    // Raised whenever the history moves (a new edit, undo, redo), for the Undo/Redo buttons.
    public static event Action? Changed;

    public static Snapshot? Loaded { get; private set; }
    public static Switch? LastSwitch { get; private set; }
    public static string? LaunchProfile { get; private set; }

    public static Track CurrentTrack => Layout.Current == Layout.View.LoadOrder ? Track.Order : Track.List;
    public static bool CanUndo => Past[CurrentTrack].Count > 0;
    public static bool CanRedo => Future[CurrentTrack].Count > 0;

    // Where the current view's history is: steps taken so far, and steps in all (including redoable ones).
    public static (int Position, int Total) Position =>
        (Past[CurrentTrack].Count, Past[CurrentTrack].Count + Future[CurrentTrack].Count);

    // The baseline, once, when the game has finished loading (first main menu).
    public static void Start()
    {
        if (Loaded != null)
            return;
        Loaded = _current = Take();
        LaunchProfile = Store.Data.ActiveProfile;
    }

    // After any change. Repeats of the current state (several saves for one action) are ignored; a change
    // to both parts (e.g. Revert all) goes into both histories.
    public static void Record()
    {
        if (_restoring || _current == null)
            return;
        var now = Take();
        var list = now.Store != _current.Store || !SameStates(now, _current);
        var order = !SameOrder(now, _current);
        if (!list && !order)
            return;
        if (list)
            Push(Track.List, _current);
        if (order)
            Push(Track.Order, _current);
        _current = now;
        Changed?.Invoke();
    }

    public static void NoteSwitch(string from, string to) => LastSwitch = new Switch(from, to, CurrentMods());

    // For a view switch, which changes the history the buttons act on.
    public static void NotifyChanged() => Changed?.Invoke();

    public static void Undo() => Step(Past[CurrentTrack], Future[CurrentTrack]);
    public static void Redo() => Step(Future[CurrentTrack], Past[CurrentTrack]);

    // Takes the last snapshot from one side, puts now on the other, and restores this track's part of it.
    static void Step(List<Snapshot> from, List<Snapshot> to)
    {
        if (from.Count == 0 || _current == null)
            return;
        var target = from[^1];
        from.RemoveAt(from.Count - 1);
        to.Add(_current);
        _restoring = true;
        try
        {
            if (CurrentTrack == Track.Order)
                ApplyOrder(target.Mods);
            else
            {
                ApplyStates(target.Mods);
                Store.RestoreEdits(target.Store);
            }
        }
        finally
        {
            _restoring = false;
        }
        _current = Take();
        Restored?.Invoke();
        Changed?.Invoke();
    }

    // The game's mod list as this session loaded it (on/off and order); our data stays as it is. Undoable,
    // in both histories.
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

    static void Push(Track track, Snapshot snapshot)
    {
        Past[track].Add(snapshot);
        if (Past[track].Count > Limit)
            Past[track].RemoveAt(0);
        Future[track].Clear();
    }

    static bool SameStates(Snapshot a, Snapshot b) =>
        a.Mods.Select(m => (m.Id, m.Source, m.Enabled)).ToHashSet().SetEquals(b.Mods.Select(m => (m.Id, m.Source, m.Enabled)));

    static bool SameOrder(Snapshot a, Snapshot b) =>
        a.Mods.Select(m => (m.Id, m.Source)).SequenceEqual(b.Mods.Select(m => (m.Id, m.Source)));

    // On/off from the snapshot, order as it is now (entries the snapshot has and now lacks go last).
    static void ApplyStates(IReadOnlyList<ModEntry> mods)
    {
        var states = mods.ToDictionary(m => (m.Id, m.Source), m => m.Enabled);
        var now = CurrentMods();
        Apply(now.Select(m => m with { Enabled = states.TryGetValue((m.Id, m.Source), out var on) ? on : m.Enabled })
            .Concat(mods.Where(m => !now.Any(n => n.Id == m.Id && n.Source == m.Source))).ToList());
    }

    // Order from the snapshot, on/off as it is now (entries the snapshot doesn't have go last).
    static void ApplyOrder(IReadOnlyList<ModEntry> mods)
    {
        var position = mods.Select((m, i) => (m, i)).ToDictionary(p => (p.m.Id, p.m.Source), p => p.i);
        Apply(CurrentMods().OrderBy(m => position.TryGetValue((m.Id, m.Source), out var i) ? i : int.MaxValue).ToList());
    }

    static void Apply(IReadOnlyList<ModEntry> mods)
    {
        var settings = SaveManager.Instance.SettingsSave.ModSettings ??= new ModSettings();
        settings.ModList.Clear();
        settings.ModList.AddRange(mods.Select(m => new SettingsSaveMod { Id = m.Id, Source = m.Source, IsEnabled = m.Enabled }));
        SaveManager.Instance.SaveSettings();
    }
}
