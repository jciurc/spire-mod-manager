using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;
using MegaCrit.Sts2.Core.Saves;

namespace SpireModManager;

// Hard dependencies from mod manifests. The game loads a mod after everything it needs, whatever the saved order,
// and fails it if one isn't loaded; order rules only bind mods that both load (Copies.Loads: of copies of one id ticked,
// only one loads). Inside a redraw, Lookups answers.
static class Dependencies
{
    // A mod's dependency ids, read once per redraw (Lookups).
    public static IReadOnlyList<string> Of(Mod? mod) => mod == null ? [] : Lookups.Current?.DependenciesOf(mod, Read) ?? Read(mod);

    // Read from the manifest, every time.
    public static IReadOnlyList<string> Read(Mod mod) => mod.manifest?.dependencies?.Select(d => d.id).ToList() ?? [];

    // The (id, source) pairs the saved list has off, for checking many mods at once (IsModDisabled walks the list per
    // mod): any entry for one off, as the game reads it.
    public static HashSet<(string?, ModSource)> SavedOff() => SavedOff(SaveManager.Instance.SettingsSave.ModSettings?.ModList ?? []);

    public static HashSet<(string?, ModSource)> SavedOff(IEnumerable<SettingsSaveMod> saved) =>
        saved.Where(e => !e.IsEnabled).Select(e => ((string?)e.Id, e.Source)).ToHashSet();

    // The same from an undo snapshot's list (History.Loaded: launch).
    public static HashSet<(string?, ModSource)> SavedOff(IEnumerable<History.ModEntry> saved) =>
        saved.Where(e => !e.Enabled).Select(e => ((string?)e.Id, e.Source)).ToHashSet();

    // Each mod's saved entry by (id, source), the first if listed twice (as the game's own lookups find it), for looking
    // up many mods at once.
    public static Dictionary<(string?, ModSource), SettingsSaveMod> SavedEntries(IEnumerable<SettingsSaveMod> saved)
    {
        var entries = new Dictionary<(string?, ModSource), SettingsSaveMod>();
        foreach (var entry in saved)
            entries.TryAdd((entry.Id, entry.Source), entry);
        return entries;
    }

    // The installed copy of an id: the one that loads after a relaunch, else one that's ticked, else of copies from one
    // source one ticked by its own tick (its switch off), else the one the game loaded, else the first (Preference); mods
    // installed this session count.
    public static Mod? Find(string id)
    {
        if (id != null && Lookups.Current is { } now)
            return now.Find(id);
        var (off, ticks) = (SavedOff(), Ticks.Now);
        return ModManager.Mods.Union(Installed.Mods).Where(m => m.manifest?.id == id)
            .OrderByDescending(m => Preference(m, c => Copies.LoadsIn(c, off, ticks), c => Copies.TickedIn(c, off, ticks), ticks)).FirstOrDefault();
    }

    // How strongly Find prefers a copy (higher first; the first in list order among equals): what the dependency checks
    // read (a needed id loads when any copy of it does) and what "enable the mod this one needs" turns on, so a needed id
    // installed twice that's all off brings back the copy the player had ticked (Write keeps copies_off when the last
    // copy goes off).
    public static int Preference(Mod mod, Func<Mod, bool> loads, Func<Mod, bool> ticked, Ticks ticks) =>
        loads(mod) ? 4 : ticked(mod) ? 3 : Copies.Twins(mod).Count > 0 && Copies.OwnTick(mod, ticks) ? 2 : mod.state == ModLoadState.Loaded ? 1 : 0;

    // The mods in the game's list that name this id as a dependency (whether they're on or not), those installed while the
    // game runs too: the game reads them into its list.
    public static IEnumerable<Mod> NeededBy(string? id) => id != null && Lookups.Current is { } now ? now.NeededBy(id)
        : ModManager.Mods.Where(m => Of(m).Contains(id));

    public enum Problem { None, Missing, Off, LoadsBefore }

    // What's wrong with one dependency of an enabled mod, given the order (index per mod) it would load in. Off: no copy
    // of it loads (Find prefers the one that does).
    public static Problem Check(Mod mod, string dependency, IReadOnlyDictionary<Mod, int> order)
    {
        if (Find(dependency) is not { } dep)
            return Problem.Missing;
        if (!Copies.Loads(dep))
            return Problem.Off;
        return order.TryGetValue(dep, out var d) && order.TryGetValue(mod, out var m) && d > m ? Problem.LoadsBefore : Problem.None;
    }

    // Whether a mod that loads after a relaunch needs one that's missing or won't load, so it fails to: the main list's red
    // banner's mods (LoadOrder.Unloadable).
    public static bool Unmet(Mod mod) => Copies.Loads(mod) && Of(mod).Any(id => Find(id) is not { } dep || !Copies.Loads(dep));

    // Installed mods that won't load but are needed by one that will, directly or through another one of these.
    public static List<Mod> NeededOff(IEnumerable<Mod> mods)
    {
        var needed = new List<Mod>();
        var seen = new HashSet<Mod>();
        var waiting = new Queue<Mod>(mods.Where(Copies.Loads));
        while (waiting.TryDequeue(out var mod))
            foreach (var dependency in Of(mod).Select(Find).OfType<Mod>())
                if (!Copies.Loads(dependency) && seen.Add(dependency))
                {
                    needed.Add(dependency);
                    waiting.Enqueue(dependency);
                }
        return needed;
    }

    // Every installed copy once (a copy the game lists twice, as found at launch and installed since, is one), in order.
    public static IEnumerable<Mod> EachCopy(IEnumerable<Mod> mods) => mods.Where(m => m.manifest?.id != null)
        .DistinctBy(m => (m.manifest?.id, m.modSource, Copies.CopyOf(m)));

    // Every saved entry of each (id, source), in the list's order. The game reads a mod as off if any of its entries is
    // off, so turning a mod listed twice (two copies with one id and source) on or off sets them all.
    public static Dictionary<(string?, ModSource), List<SettingsSaveMod>> AllSavedEntries(IEnumerable<SettingsSaveMod> saved)
    {
        var entries = new Dictionary<(string?, ModSource), List<SettingsSaveMod>>();
        foreach (var entry in saved)
        {
            if (!entries.TryGetValue((entry.Id, entry.Source), out var list))
                entries[(entry.Id, entry.Source)] = list = [];
            list.Add(entry);
        }
        return entries;
    }

    // Turns mods on or off (Write) and ticks the page's boxes; the caller's redraw records one change.
    public static void Set(IEnumerable<Mod> mods, bool on, NModdingScreen? screen)
    {
        Write([.. mods.Select(m => (m, on))]);
        if (screen != null)
            Profiles.RefreshTickboxes(screen);
    }

    // Turns copies on or off, as one change the caller records: the game's list (one switch per id and source), and where
    // copies share a switch, our copies_off (those off while it's on), our data saved first. Each copy given is set as
    // asked and every other keeps its tick: nothing turns another copy off. A copy on comes out of copies_off; with two
    // or more copies on a switch, one off goes in. A copy turned on while its switch is off comes on alone: the others
    // read off then, so they go into copies_off, our record's missing ones too (one back later isn't on beside it). A
    // switch whose copies all end up off keeps its copies_off, so turned on elsewhere (the game's page) it brings the
    // same copies back. Copies of this mod from one source share one box: one given sets them all, and none of them is
    // ever in copies_off. offs: a profile's copies off for some of our record's groups, written as they are, copies not
    // installed included (Profiles.Apply); for those, only the switch comes from wanted. A read-only session can't save
    // copies_off: the game's list changes, it doesn't.
    public static void Write(IReadOnlyList<(Mod Mod, bool On)> wanted, IReadOnlyDictionary<(string Id, ModSource Source), List<string>>? offs = null)
    {
        var all = EachCopy(ModManager.Mods.Union(Installed.Mods).Concat(wanted.Select(w => w.Mod))).ToList();
        // Read fresh, not through Lookups.Current: a redraw's answers can be from before an earlier change.
        var (gameOff, now) = (SavedOff(), Ticks.Now);
        bool Was(Mod m) => Copies.TickedIn(m, gameOff, now);
        // By copy (SwitchKey), not by Mod: the game can list one copy twice (found at launch, and again on an update).
        var final = new Dictionary<string, bool>();
        var ours = new Dictionary<ModSource, bool>();
        foreach (var (mod, on) in wanted.Where(w => w.Mod.manifest?.id != null))
            if (mod.manifest?.id == ModEntry.ModId)
                ours[mod.modSource] = on;
            else
                final[Copies.SwitchKey(mod)] = on;
        bool Final(Mod m) => m.manifest?.id == ModEntry.ModId ? ours.TryGetValue(m.modSource, out var set) ? set : Was(m)
            : final.TryGetValue(Copies.SwitchKey(m), out var on) ? on : Was(m);
        bool Given(Mod m) => m.manifest?.id == ModEntry.ModId ? ours.ContainsKey(m.modSource) : final.ContainsKey(Copies.SwitchKey(m));

        var settings = SaveManager.Instance.SettingsSave.ModSettings ??= new ModSettings();
        var entries = AllSavedEntries(settings.ModList);
        var off = (Store.Data.CopiesOff ?? Copies.LegacyOff(Store.Data.CopyPicks, Store.Data.Copies)).ToHashSet();
        foreach (var (id, source) in all.Where(Given).Select(m => (Id: m.manifest?.id, m.modSource)).Distinct().ToList())
        {
            if (id == null)
                continue;
            var copies = all.Where(m => m.manifest?.id == id && m.modSource == source).ToList();
            var on = copies.Where(Final).ToList();
            var wasOff = gameOff.Contains((id, source));
            // No entry is on already (a mod installed while the game runs has none yet): one is made to turn it off. One of
            // ours goes with ours at the top: the game sorts a mod by its id's last entry, so at the end it would load this
            // mod last next launch, after copies it has to choose between (LoadOrder.OursFirst).
            if (!entries.TryGetValue((id, source), out var list) && on.Count == 0)
            {
                var entry = new SettingsSaveMod { Id = id, Source = source };
                settings.ModList.Add(entry);
                entries[(id, source)] = list = [entry];
                if (id == ModEntry.ModId)
                    LoadOrder.OursFirst(settings.ModList);
            }
            foreach (var entry in list ?? [])
                entry.IsEnabled = on.Count > 0;
            if (id == ModEntry.ModId)
                continue;
            var keys = copies.Select(Copies.CopyOf).Concat(Copies.RecordGroup(id, source) ?? []).Distinct().Select(c => $"{id}|{c}").ToList();
            if (offs?.GetValueOrDefault((id, source)) is { } given)
            {
                off.ExceptWith(keys);
                off.UnionWith(given.Select(c => $"{id}|{c}"));
                continue;
            }
            if (on.Count == 0)
                continue;
            foreach (var copy in copies)
                if (on.Contains(copy))
                    off.Remove(Copies.SwitchKey(copy));
                else if (copies.Count > 1)
                    off.Add(Copies.SwitchKey(copy));
            // The switch was off: the copies turned on come on alone, our record's missing ones off too.
            if (wasOff)
                off.UnionWith(keys.Except(on.Select(Copies.SwitchKey)));
        }
        // Groups given whole that no copy installed names (every copy of it missing): written all the same.
        foreach (var ((id, source), given) in offs ?? new Dictionary<(string, ModSource), List<string>>())
            if (!all.Any(m => m.manifest?.id == id && m.modSource == source))
            {
                off.RemoveWhere(k => Copies.Parse(k) is { } key && key.Id == id && key.Source == source);
                off.UnionWith(given.Select(c => $"{id}|{c}"));
            }
        var copiesOff = Store.TidyOff(off) ?? [];
        if (Store.Data.CopiesOff is not { } was || !copiesOff.SequenceEqual(was))
        {
            if (Store.ReadOnly)
                Log.Warn($"[{ModEntry.ModId}] Our data can't be saved this session, so which copies of a mod are on stay as they were: "
                    + string.Join(", ", all.Where(m => Given(m) && Copies.Twins(m).Count > 0).Select(Copies.SwitchKey)));
            else
            {
                // A new list: Ticks reads it again only then.
                Store.Data.CopiesOff = copiesOff;
                Store.SaveViewState(); // the caller records the change, both halves in one undo step
            }
        }
        SaveManager.Instance.SaveSettings();
    }

    // Mods that load placed before a mod they need that loads.
    public static List<(Mod Mod, Mod Needs)> OrderViolations(IReadOnlyList<Mod> order)
    {
        var index = order.Select((m, i) => (m, i)).DistinctBy(p => p.m).ToDictionary(p => p.m, p => p.i);
        return order.Where(Copies.Loads)
            .SelectMany(m => Of(m).Select(Find).OfType<Mod>().Where(d => Copies.Loads(d) && index.TryGetValue(d, out var i) && i > index[m])
                .Select(d => (m, d)))
            .ToList();
    }

    // The order with each dependency that loads moved just ahead of what needs it, otherwise unchanged (the game's own
    // stable topological sort).
    public static List<Mod> Fixed(IReadOnlyList<Mod> given)
    {
        var order = given.Distinct().ToList(); // a mod listed twice counts once
        var position = order.Select((m, i) => (m, i)).ToDictionary(p => p.m, p => p.i);
        // Each loading mod's loading dependencies in the list, worked out once.
        var needs = order.ToDictionary(m => m, m => Copies.Loads(m)
            ? Of(m).Select(Find).OfType<Mod>().Where(d => Copies.Loads(d) && position.ContainsKey(d) && d != m).Distinct().ToList()
            : []);
        var waitingOn = order.ToDictionary(m => m, m => needs[m].Count);
        var dependents = order.ToDictionary(m => m, _ => new List<Mod>());
        foreach (var m in order)
            foreach (var d in needs[m])
                dependents[d].Add(m);

        var ready = new PriorityQueue<Mod, int>();
        foreach (var m in order.Where(m => waitingOn[m] == 0))
            ready.Enqueue(m, position[m]);
        var result = new List<Mod>();
        while (ready.TryDequeue(out var m, out _))
        {
            result.Add(m);
            foreach (var next in dependents[m])
                if (--waitingOn[next] == 0)
                    ready.Enqueue(next, position[next]);
        }
        // A dependency cycle leaves mods behind: keep them in their old order at the end.
        var placed = result.ToHashSet();
        result.AddRange(order.Where(m => !placed.Contains(m)));
        return result;
    }
}
