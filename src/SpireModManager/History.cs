using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Saves;

namespace SpireModManager;

// Undo and redo, kept separately for the mod list and our data (List) and for the load order (Order): Load Order
// steps through Order, the other views through List, so undoing an order change never flips a tickbox. The first
// snapshot is the session's start (Loaded): what Changes compares with, and what Revert all puts back of what it counts,
// the order too (Jordan, 23 Oct). A change to the picked mods (Selection) is a step too, in time with the edits, as in an
// image editor: undoing it only picks them again, leaving the mod list and our data alone.
static class History
{
    public enum Track { List, Order }

    public abstract record Change;
    public record ModEntry(string Id, ModSource Source, bool Enabled);
    // Our data, the game's saved list, and which copy of a mod installed twice takes which of its key's entries there
    // (LoadOrder.CopyOrder: the game's entries can't tell them apart): the order is the list and the copy order together.
    public record Snapshot(string Store, IReadOnlyList<ModEntry> Mods, IReadOnlyDictionary<(string?, ModSource), IReadOnlyList<string>> CopyOrder) : Change;
    public record Picks(IReadOnlyList<Mod> Mods, Mod? Anchor) : Change;
    // Revert all's step: the state before it, in both histories, the two told by Id. Undone or redone while its twin is
    // next in the other history too, it brings everything back at once (one Ctrl+Z, from either view); with something
    // done in the other history since, only this history's part, as a Snapshot.
    public record Whole(Snapshot State, long Id) : Change;

    // Kept per history: this many snapshots of our data, and up to PicksLimit selection steps (small) among them.
    const int Limit = 200, PicksLimit = 1000;
    static readonly Dictionary<Track, List<Change>> Past = new() { [Track.List] = [], [Track.Order] = [] };
    static readonly Dictionary<Track, List<Change>> Future = new() { [Track.List] = [], [Track.Order] = [] };
    static Snapshot? _current;
    static bool _restoring;
    static readonly Dictionary<Track, long> Recorded = new() { [Track.List] = 0, [Track.Order] = 0 }; // steps so far, never down
    static long _wholes; // Whole steps made, for their ids

    // The state Revert all leaves, for its button: launch's, until a mod is installed (Revert all puts it off, which only
    // running it works out), then none until Revert all runs again and leaves its own.
    static Snapshot? _reverted;

    // Raised after an undo, redo or revert, so open screens can redraw from the restored state.
    public static event Action? Restored;

    // Raised whenever the history moves (a new edit, undo, redo), for the Undo/Redo buttons.
    public static event Action? Changed;

    // Raised after an undo or redo of a selection step (Selection holds the restored picks), so the page redraws them.
    public static event Action? Repicked;

    public static Snapshot? Loaded { get; private set; }
    public static string? LaunchProfile { get; private set; }

    public static Track CurrentTrack => Layout.Current == Layout.View.LoadOrder ? Track.Order : Track.List;
    public static bool CanUndo => Past[CurrentTrack].Count > 0;
    public static bool CanRedo => Future[CurrentTrack].Count > 0;

    // The baseline, once, when the game has finished loading (first main menu): after our own startup changes (Settle's
    // repair of the game's list, Copies.Reconcile, Profiles.Prepare), which are no change since launch, so the state the
    // game loaded with as we keep it. A mod's arrival is written into it as well (Quietly), its being on isn't.
    public static void Start()
    {
        if (Loaded != null)
            return;
        Loaded = _current = _reverted = Take();
        LaunchProfile = Store.Data.ActiveProfile;
        Installed.Added += () => _reverted = null;
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

    // The Modding page opened: where undo starts. The active profile is first brought in line with the tickboxes, as part
    // of the current state.
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
        if (_current != null)
            _current = Take();
        // Undo and redo cover this visit (Revert all, the whole session).
        foreach (var track in new[] { Track.List, Track.Order })
        {
            Past[track].Clear();
            Future[track].Clear();
        }
        Changed?.Invoke();
    }

    // A change taken as the current state, with no step (its saves record nothing): for state that follows something
    // else (a profile's unsaved edit following the mod list), which a restored state works out again for itself.
    public static void AsCurrent(Action change)
    {
        _restoring = true;
        try
        {
            change();
        }
        finally
        {
            _restoring = false;
        }
        if (_current is { } current)
            _current = current with { Store = Take().Store };
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
        if (_reverted is { } reverted)
            _reverted = Patched(reverted);
        if (_current is { } current)
            _current = Patched(current);
        foreach (var steps in Past.Values.Concat(Future.Values))
            for (var i = 0; i < steps.Count; i++)
                if (steps[i] is Snapshot snapshot)
                    steps[i] = Patched(snapshot);
                else if (steps[i] is Whole whole)
                    steps[i] = whole with { State = Patched(whole.State) };
    }

    // Nothing for Revert all to do: the mod list, its order and what Changes counts in our data (Store.Counted) as it
    // leaves them. A Save, a category added or moved, the main menu button moved: still nothing.
    public static bool AtLaunch => _reverted is { } target && Take() is var now && Store.Counted(now.Store) == Store.Counted(target.Store)
        && SameStates(now, target) && SameOrder(Launched(now), Launched(target));

    // The entries launch had, and their copy order, as Changes counts moves (an entry made since, for a mod installed
    // since, moves nobody: LoadOrder.MovedSince; nor does a turn of its copies), so a drag of such a mod after Revert all
    // leaves the button greyed, as Changes reads the same.
    static Snapshot Launched(Snapshot snapshot) => Loaded is { } launch && launch.Mods.Select(m => ((string?)m.Id, m.Source)).ToHashSet() is var had
        ? snapshot with { Mods = [.. snapshot.Mods.Where(m => had.Contains((m.Id, m.Source)))], CopyOrder = snapshot.CopyOrder.Where(p => had.Contains(p.Key)).ToDictionary() }
        : snapshot;

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
            // Each history's own steps, a Revert all's part by part: the count is per history.
            for (var i = 0; i < steps; i++)
                Step(track, Past, Future, whole: false);
            Future[track].RemoveRange(Future[track].Count - steps, steps);
        }
        Changed?.Invoke();
    }

#if DEVTOOLS
    // Self-test: how many undo steps a history has, and its newest.
    public static int Steps(Track track) => Past[track].Count;
    public static Change? Newest(Track track) => Past[track].LastOrDefault();

    // Self-test: both histories, and the state Revert all left, for a test that shows the page again (which starts the
    // histories anew) or runs Revert all to put back after, so the tests after it undo and grey the button as they would
    // have.
    public sealed record Kept(IReadOnlyDictionary<Track, List<Change>> Past, IReadOnlyDictionary<Track, List<Change>> Future, Snapshot? Reverted);

    public static Kept TestKeep() => new(Past.ToDictionary(p => p.Key, p => p.Value.ToList()), Future.ToDictionary(f => f.Key, f => f.Value.ToList()), _reverted);

    public static void TestPutBack(Kept kept)
    {
        foreach (var track in new[] { Track.List, Track.Order })
        {
            Past[track].Clear();
            Past[track].AddRange(kept.Past[track]);
            Future[track].Clear();
            Future[track].AddRange(kept.Future[track]);
        }
        _reverted = kept.Reverted;
        if (_current != null)
            _current = Take();
        Changed?.Invoke();
    }

    // Self-test: the state now, and a state put back whole with no step, for a test to leave things as it found them.
    public static Snapshot TestNow() => Take();

    public static void TestRestore(Snapshot state)
    {
        _restoring = true;
        try
        {
            Restore(state);
        }
        finally
        {
            _restoring = false;
        }
        _current = Take();
        Restored?.Invoke();
        Changed?.Invoke();
    }
#endif

    // Takes the last step of the track from one side, puts now on the other, and restores it: the picked mods for a
    // selection step; Revert all's whole state where its twin is next on that side of the other history too (both move
    // across, unless whole is false); else the track's part of the snapshot.
    static void Step(Track track, Dictionary<Track, List<Change>> fromSide, Dictionary<Track, List<Change>> toSide, bool whole = true)
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
        var other = fromSide[track == Track.List ? Track.Order : Track.List];
        whole = whole && step is Whole { Id: var id } && other is [.., Whole { Id: var twin }] && twin == id;
        var target = step is Whole { State: var state } ? state : (Snapshot)step;
        if (whole)
        {
            other.RemoveAt(other.Count - 1);
            var now = new Whole(_current, ++_wholes);
            to.Add(now);
            toSide[track == Track.List ? Track.Order : Track.List].Add(now);
        }
        else
            to.Add(_current);
        _restoring = true;
        try
        {
            if (whole)
                Restore(target);
            else if (track == Track.Order)
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

    // Everything Changes counts back to its launch value (Loaded), so Changes reads 0 (Jordan, 23 Oct), and nothing else:
    // the mod list as it was, every switch and its place in the order; which copy took which of an id's entries (the copy
    // order); and what Changes counts in our data (Store.RestoreCounted): each copy's tick, each mod's details, the
    // profiles by name and the active one. A profile's saved list, the categories and their order, the main menu
    // button's place stay as they are. The copies then load as at launch, their rule reading the same switches, ticks and
    // record (Copies.Next). A mod installed since stays installed: put off as it was then (it didn't load, PutOff), so no
    // other mod moves; Changes still lists it. The active profile then follows the tickboxes. One step, in both histories
    // (Whole): one Ctrl+Z takes it back.
    public static void RevertMods()
    {
        if (Loaded is not { } launch || _current == null)
            return;
        var before = Take();
        _restoring = true; // one undo step for the lot, recorded below
        try
        {
            Apply(launch.Mods);
            LoadOrder.RestoreCopyOrder(launch.CopyOrder);
            Store.RestoreCounted(launch.Store);
            PutOff(ArrivedTicked());
            Profiles.Track();
        }
        finally
        {
            _restoring = false;
        }
        var now = Take();
        // In both histories whatever it changed (an entry made or dropped too), so either view's Undo takes it back.
        if (now.Store != before.Store || !now.Mods.SequenceEqual(before.Mods) || !LoadOrder.SameCopyOrder(now.CopyOrder, before.CopyOrder))
        {
            var step = new Whole(before, ++_wholes);
            Push(Track.List, step);
            Push(Track.Order, step);
        }
        _current = _reverted = now;
        Restored?.Invoke();
        Changed?.Invoke();
    }

    // The copies installed since launch that are ticked, for Revert all to put off. Not a copy of this mod on the switch
    // of one found at launch: its copies of one source share one box, so the running copy would go off with it.
    static List<Mod> ArrivedTicked() => [.. Dependencies.EachCopy(Installed.Mods).Where(m => Copies.Ticked(m)
        && (m.manifest?.id != SpireModManager.ModEntry.ModId || Copies.Twins(m).All(Installed.Mods.Contains)))];

    // Those copies off, as at launch. One on the switch of copies found at launch: into copies_off, the switch left on,
    // and each launch copy there ticked as it was then (read among the copies installed then), so the copy that loaded
    // loads again. Dependencies.Write would read the launch copies' ticks with it among them: a copy back with its stored
    // tick (one our record knows) reads them off, so the switch would go off and nothing load. The rest through Write: a
    // copy on a switch of its own gets its entry off (one made for it if none). So does every copy in a session that
    // can't save our data (copies_off stays as the file has it): the next launch reads the file, and for a known copy
    // back the switch off is the closest it can come (one line, the launch copy off, against two with it ticked).
    static void PutOff(List<Mod> arrived)
    {
        var since = Copies.SinceLaunch();
        var (gameOff, ticks) = (Dependencies.SavedOff(), Ticks.Now); // launch's, just put back
        var off = (Store.Data.CopiesOff ?? Copies.LegacyOff(Store.Data.CopyPicks, Store.Data.Copies)).ToHashSet();
        var rest = new List<Mod>();
        foreach (var mod in arrived)
        {
            var launched = Copies.Twins(mod).Where(t => !since.Contains(Copies.SwitchKey(t))).ToList();
            if (launched.Count == 0 || Store.ReadOnly)
            {
                rest.Add(mod);
                continue;
            }
            off.Add(Copies.SwitchKey(mod));
            foreach (var copy in launched.Where(c => Copies.TickedIn(c, gameOff, ticks, since)))
                off.Remove(Copies.SwitchKey(copy));
        }
        if (rest.Count < arrived.Count && Store.TidyOff(off) is { } copiesOff && !copiesOff.SequenceEqual(Store.Data.CopiesOff ?? []))
        {
            Store.Data.CopiesOff = copiesOff; // a new list: Ticks reads it again only then
            Store.SaveViewState();
        }
        if (rest.Count > 0)
            Dependencies.Write([.. rest.Select(m => (m, false))]);
    }

    // A snapshot's whole state: the game's list exactly as it was, entries and order, its copy order, and our data.
    static void Restore(Snapshot target)
    {
        Apply(target.Mods);
        LoadOrder.RestoreCopyOrder(target.CopyOrder);
        Store.RestoreEdits(target.Store);
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
        foreach (var (kind, limit) in new (Func<Change, bool>, int)[] { (s => s is Snapshot or Whole, Limit), (s => s is Picks, PicksLimit) })
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
