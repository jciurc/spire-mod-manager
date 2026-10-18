using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Saves;

namespace SpireModManager;

// Undo and redo, kept separately for the mod list and our data (List) and for the load order (Order): Load Order
// steps through Order, the other views through List, so undoing an order change never flips a tickbox. The first
// snapshot is the session's start (Changes); Revert all goes back to when the Modding page opened. A change to the
// picked mods (Selection) is a step too, in time with the edits, as in an image editor: undoing it only picks them
// again, leaving the mod list and our data alone.
static class History
{
    public enum Track { List, Order }

    public abstract record Change;
    public record ModEntry(string Id, ModSource Source, bool Enabled);
    // Our data, the game's saved list, and which copy of a mod installed twice takes which of its key's entries there
    // (LoadOrder.CopyOrder: the game's entries can't tell them apart): the order is the list and the copy order together.
    public record Snapshot(string Store, IReadOnlyList<ModEntry> Mods, IReadOnlyDictionary<(string?, ModSource), IReadOnlyList<string>> CopyOrder) : Change;
    public record Picks(IReadOnlyList<Mod> Mods, Mod? Anchor) : Change;

    // Kept per history: this many snapshots of our data, and up to PicksLimit selection steps (small) among them.
    const int Limit = 200, PicksLimit = 1000;
    static readonly Dictionary<Track, List<Change>> Past = new() { [Track.List] = [], [Track.Order] = [] };
    static readonly Dictionary<Track, List<Change>> Future = new() { [Track.List] = [], [Track.Order] = [] };
    static Snapshot? _current;
    static bool _restoring;
    static readonly Dictionary<Track, long> Recorded = new() { [Track.List] = 0, [Track.Order] = 0 }; // steps so far, never down

    // Raised after an undo, redo or revert, so open screens can redraw from the restored state.
    public static event Action? Restored;

    // Raised whenever the history moves (a new edit, undo, redo), for the Undo/Redo buttons.
    public static event Action? Changed;

    // Raised after an undo or redo of a selection step (Selection holds the restored picks), so the page redraws them.
    public static event Action? Repicked;

    public static Snapshot? Loaded { get; private set; }
    public static Snapshot? Entered { get; private set; }
    public static string? LaunchProfile { get; private set; }

    public static Track CurrentTrack => Layout.Current == Layout.View.LoadOrder ? Track.Order : Track.List;
    public static bool CanUndo => Past[CurrentTrack].Count > 0;
    public static bool CanRedo => Future[CurrentTrack].Count > 0;

    // The baseline, once, when the game has finished loading (first main menu).
    public static void Start()
    {
        if (Loaded != null)
            return;
        Loaded = _current = Take();
        LaunchProfile = Store.Data.ActiveProfile;
    }

    // After any change; repeats of the current state are ignored, and a change to both parts goes into both.
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

    // A change to the picked mods: the picks before it, as a step of the given history.
    public static void RecordPicks(Track track, Picks before)
    {
        if (_restoring || _current == null)
            return;
        Push(track, before);
        Changed?.Invoke();
    }

    // The view switched: picks belong to the view they were made in (it lets go of them), so their steps go too.
    public static void ForgetPicks()
    {
        foreach (var steps in Past.Values.Concat(Future.Values))
            steps.RemoveAll(step => step is Picks);
        Changed?.Invoke();
    }

    // The Modding page opened: what Revert all goes back to and where undo starts. The active profile is first brought
    // in line with the tickboxes, as part of the current state.
    public static void Enter()
    {
        _restoring = true;
        try
        {
            Profiles.Track();
        }
        finally
        {
            _restoring = false;
        }
        Entered = Take();
        if (_current != null)
            _current = Entered;
        // Undo and redo cover this visit too, like Revert all (which leaves the picked mods alone, as they aren't edits).
        foreach (var track in new[] { Track.List, Track.Order })
        {
            Past[track].Clear();
            Future[track].Clear();
        }
        Changed?.Invoke();
    }

    // A change that isn't the player's edit (an installed mod joining the active profile): written into every
    // snapshot, so it's no undo step and no change since launch, and Revert all keeps it.
    public static void Quietly(Action<StoreData> change)
    {
        _restoring = true;
        try
        {
            change(Store.Data);
            Store.Save();
        }
        finally
        {
            _restoring = false;
        }
        Snapshot Patched(Snapshot snapshot) => snapshot with { Store = Store.Patched(snapshot.Store, change) };
        if (Loaded is { } loaded)
            Loaded = Patched(loaded);
        if (Entered is { } entered)
            Entered = Patched(entered);
        if (_current is { } current)
            _current = Patched(current);
        foreach (var steps in Past.Values.Concat(Future.Values))
            for (var i = 0; i < steps.Count; i++)
                if (steps[i] is Snapshot snapshot)
                    steps[i] = Patched(snapshot);
    }

    // Nothing to revert: the mod list and our data are as they were when the page opened.
    public static bool AtEntered => Entered is { } e && Take() is var now && now.Store == e.Store && SameStates(now, e) && SameOrder(now, e);

    // For a view switch, which changes the history the buttons act on.
    public static void NotifyChanged() => Changed?.Invoke();

    public static void Undo() => Step(CurrentTrack, Past, Future);
    public static void Redo() => Step(CurrentTrack, Future, Past);

    // A cancellable dialog (RelaunchCheck): Mark as it opens; UndoTo on Cancel takes back every step since, in both
    // histories, and forgets them (no Redo).
    public readonly record struct Point(long List, long Order);

    public static Point Mark() => new(Recorded[Track.List], Recorded[Track.Order]);

    public static void UndoTo(Point mark)
    {
        foreach (var (track, since) in new[] { (Track.List, mark.List), (Track.Order, mark.Order) })
        {
            var steps = (int)Math.Clamp(Recorded[track] - since, 0, Past[track].Count);
            for (var i = 0; i < steps; i++)
                Step(track, Past, Future);
            Future[track].RemoveRange(Future[track].Count - steps, steps);
        }
        Changed?.Invoke();
    }

#if DEVTOOLS
    // Self-test: how many undo steps a history has, and its newest.
    public static int Steps(Track track) => Past[track].Count;
    public static Change? Newest(Track track) => Past[track].LastOrDefault();
#endif

    // Takes the last step of the track from one side, puts now on the other, and restores it: the picked mods for a
    // selection step, else the track's part of the snapshot.
    static void Step(Track track, Dictionary<Track, List<Change>> fromSide, Dictionary<Track, List<Change>> toSide)
    {
        var (from, to) = (fromSide[track], toSide[track]);
        if (from.Count == 0 || _current == null)
            return;
        var step = from[^1];
        from.RemoveAt(from.Count - 1);
        if (step is Picks picks)
        {
            to.Add(Selection.Now());
            Selection.Set(picks);
            Repicked?.Invoke();
            Changed?.Invoke();
            return;
        }
        var target = (Snapshot)step;
        to.Add(_current);
        _restoring = true;
        try
        {
            if (track == Track.Order)
                ApplyOrder(target);
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

    // Everything back to when the Modding page opened: the mod list and our data, active profile included. Undoable.
    public static void RevertMods()
    {
        if (Entered == null)
            return;
        _restoring = true; // one undo step for the lot, recorded below
        try
        {
            Apply(Entered.Mods);
            LoadOrder.RestoreCopyOrder(Entered.CopyOrder);
            Store.RestoreEdits(Entered.Store);
            Profiles.Track(); // the active profile follows the restored tickboxes
        }
        finally
        {
            _restoring = false;
        }
        Record();
        Restored?.Invoke();
        Changed?.Invoke();
    }

    public static IReadOnlyList<ModEntry> CurrentMods() =>
        SaveManager.Instance.SettingsSave.ModSettings?.ModList.Select(m => new ModEntry(m.Id, m.Source, m.IsEnabled)).ToList() ?? [];

    static Snapshot Take() => new(Store.EditsJson(), CurrentMods(), LoadOrder.CopyOrderNow());

    static void Push(Track track, Change step)
    {
        Recorded[track]++;
        var past = Past[track];
        past.Add(step);
        // Over its limit, a kind loses its oldest step: many picks never push out an edit's snapshot.
        foreach (var (kind, limit) in new (Func<Change, bool>, int)[] { (s => s is Snapshot, Limit), (s => s is Picks, PicksLimit) })
            if (past.Count(kind) > limit)
                past.RemoveAt(past.FindIndex(s => kind(s)));
        Future[track].Clear();
    }

    static bool SameStates(Snapshot a, Snapshot b) =>
        a.Mods.Select(m => (m.Id, m.Source, m.Enabled)).ToHashSet().SetEquals(b.Mods.Select(m => (m.Id, m.Source, m.Enabled)));

    // The list's order and the copy order both: a drop that only swaps two copies of a mod saves the same list.
    static bool SameOrder(Snapshot a, Snapshot b) =>
        a.Mods.Select(m => (m.Id, m.Source)).SequenceEqual(b.Mods.Select(m => (m.Id, m.Source))) && LoadOrder.SameCopyOrder(a.CopyOrder, b.CopyOrder);

    // On/off from the snapshot, order as it is now (entries the snapshot has and now lacks go last).
    static void ApplyStates(IReadOnlyList<ModEntry> mods)
    {
        var states = mods.DistinctBy(m => (m.Id, m.Source)).ToDictionary(m => (m.Id, m.Source), m => m.Enabled);
        var now = CurrentMods();
        // An entry the snapshot didn't have was made since (a mod installed mid-session, ticked for the first time):
        // back then the game had no entry for it, which means on.
        Apply(now.Select(m => m with { Enabled = !states.TryGetValue((m.Id, m.Source), out var on) || on })
            .Concat(mods.Where(m => !now.Any(n => n.Id == m.Id && n.Source == m.Source))).ToList());
    }

    // Order from the snapshot, on/off as it is now (entries the snapshot doesn't have go last), ours first whatever the
    // snapshot had (one from before we kept it so: LoadOrder.OursFirst). Entries of one id and source (two copies of a
    // mod) take that key's places in the snapshot in turn, not all the first one, and the copies take them in the
    // snapshot's copy order.
    static void ApplyOrder(Snapshot target)
    {
        var mods = target.Mods;
        LoadOrder.RestoreCopyOrder(target.CopyOrder);
        var places = new Dictionary<(string?, ModSource), Queue<int>>();
        for (var i = 0; i < mods.Count; i++)
        {
            if (!places.TryGetValue((mods[i].Id, mods[i].Source), out var queue))
                places[(mods[i].Id, mods[i].Source)] = queue = new Queue<int>();
            queue.Enqueue(i);
        }
        Apply(CurrentMods().Select(m => (Mod: m, At: places.TryGetValue((m.Id, m.Source), out var queue) && queue.TryDequeue(out var i) ? i : int.MaxValue))
            .OrderBy(p => p.At).Select(p => p.Mod).OrderBy(m => m.Id == SpireModManager.ModEntry.ModId ? 0 : 1).ToList());
    }

    static void Apply(IReadOnlyList<ModEntry> mods)
    {
        var settings = SaveManager.Instance.SettingsSave.ModSettings ??= new ModSettings();
        settings.ModList.Clear();
        settings.ModList.AddRange(mods.Select(m => new SettingsSaveMod { Id = m.Id, Source = m.Source, IsEnabled = m.Enabled }));
        SaveManager.Instance.SaveSettings();
    }
}
