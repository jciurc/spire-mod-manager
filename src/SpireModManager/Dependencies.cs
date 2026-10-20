using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;
using MegaCrit.Sts2.Core.Saves;

namespace SpireModManager;

// Hard dependencies from mod manifests. The game loads a mod after everything it needs, whatever the saved order,
// and fails it if one isn't loaded; order rules only bind mods that are both on. Inside a redraw, Lookups answers.
static class Dependencies
{
    // A mod's dependency ids, read once per redraw (Lookups).
    public static IReadOnlyList<string> Of(Mod? mod) => mod == null ? [] : Lookups.Current?.DependenciesOf(mod, Read) ?? Read(mod);

    // Read from the manifest, every time.
    public static IReadOnlyList<string> Read(Mod mod) => mod.manifest?.dependencies?.Select(d => d.id).ToList() ?? [];

    // Whether this copy loads after a relaunch (OnIn, with the saved list and our picks as they are now).
    public static bool IsOn(Mod mod) => Lookups.Current?.IsOn(mod)
        ?? !(SaveManager.Instance.SettingsSave.ModSettings?.IsModDisabled(mod) ?? false) && IsPicked(mod, Picks.Now);

    // The one rule for whether a copy is on, read against given lists (launch's, or now's): its id and source not off in
    // the game's list, and where the id is installed more than once from that source, it's the picked copy. This mod
    // goes by its switch alone (a second copy of it is the game's to sort out).
    public static bool OnIn(Mod mod, HashSet<(string?, ModSource)> gameOff, Picks picks) =>
        !gameOff.Contains((mod.manifest?.id, mod.modSource)) && IsPicked(mod, picks);

    public static bool IsPicked(Mod mod, Picks picks) => mod.manifest?.id is not { } id || id == ModEntry.ModId
        || Copies.Twins(mod).Count == 0 || picks.Picked(id, mod.modSource) == Copies.CopyOf(mod);

    // A copy installed more than once from its source that isn't the one picked now: enabling it changes the pick.
    public static bool ChangesPick(Mod mod) => mod.manifest?.id is { } id && id != ModEntry.ModId && Copies.Twins(mod).Count > 0
        && Copies.Picked(id, mod.modSource) != Copies.CopyOf(mod);

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

    // The installed copy of an id: one that's on, else the picked one of copies from one source, else the one the game
    // loaded, else the first (Preference); mods installed this session count.
    public static Mod? Find(string id) => id != null && Lookups.Current is { } now ? now.Find(id)
        : ModManager.Mods.Union(Installed.Mods).Where(m => m.manifest?.id == id).OrderByDescending(m => Preference(m, IsOn, Picks.Now)).FirstOrDefault();

    // How strongly Find prefers a copy (higher first; the first in list order among equals): what "enable the mod this
    // one needs" turns on, so a needed id installed twice keeps its pick.
    public static int Preference(Mod mod, Func<Mod, bool> isOn, Picks picks) =>
        isOn(mod) ? 3 : Copies.Twins(mod).Count > 0 && IsPicked(mod, picks) ? 2 : mod.state == ModLoadState.Loaded ? 1 : 0;

    // The mods in the game's list that name this id as a dependency (whether they're on or not).
    public static IEnumerable<Mod> NeededBy(string? id) => id != null && Lookups.Current is { } now ? now.NeededBy(id)
        : ModManager.Mods.Where(m => Of(m).Contains(id));

    public enum Problem { None, Missing, Off, LoadsBefore }

    // What's wrong with one dependency of an enabled mod, given the order (index per mod) it would load in.
    public static Problem Check(Mod mod, string dependency, IReadOnlyDictionary<Mod, int> order)
    {
        if (Find(dependency) is not { } dep)
            return Problem.Missing;
        if (!IsOn(dep))
            return Problem.Off;
        return order.TryGetValue(dep, out var d) && order.TryGetValue(mod, out var m) && d > m ? Problem.LoadsBefore : Problem.None;
    }

    // Installed mods that are off but needed by an enabled mod, directly or through another one of these.
    public static List<Mod> NeededOff(IEnumerable<Mod> mods)
    {
        var needed = new List<Mod>();
        var seen = new HashSet<Mod>();
        var waiting = new Queue<Mod>(mods.Where(IsOn));
        while (waiting.TryDequeue(out var mod))
            foreach (var dependency in Of(mod).Select(Find).OfType<Mod>())
                if (!IsOn(dependency) && seen.Add(dependency))
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

    // Turns mods on or off (Write: one copy of an id on) and ticks the page's boxes; the caller's redraw records one
    // change. winner: the copy that stays on where several of an id are turned on (the row clicked).
    public static void Set(IEnumerable<Mod> mods, bool on, NModdingScreen? screen, Mod? winner = null)
    {
        Write([.. mods.Select(m => (m, on))], winner);
        if (screen != null)
            Profiles.RefreshTickboxes(screen);
    }

    // Turns copies on or off, as one change the caller records: the game's list (one switch per id and source) and our
    // picks (which copy of an id and source loads), our data saved first. exclusive: one copy of an id on — for each id
    // with a copy turned on that was off, winner if it's one of them, else one on already, else the picked one, else the
    // first; the id's other copies, of any source, go off (an id whose copies asked for are on already is left as it is).
    // Otherwise (a profile, which holds exactly what loads) each as asked. An id and source with no copy left on keeps its
    // pick. This mod stays on. A read-only session can't save a pick: the game's list changes, the picks don't.
    public static void Write(IReadOnlyList<(Mod Mod, bool On)> wanted, Mod? winner = null, bool exclusive = true)
    {
        var all = EachCopy(ModManager.Mods.Union(Installed.Mods).Concat(wanted.Select(w => w.Mod))).ToList();
        // By copy (SwitchKey), not by Mod: the game can list one copy twice (found at launch, and again on an update).
        var final = new Dictionary<string, bool>();
        foreach (var (mod, on) in wanted.Where(w => w.Mod.manifest?.id != null))
            final[Copies.SwitchKey(mod)] = on || mod.manifest?.id == ModEntry.ModId;
        if (exclusive)
            foreach (var id in wanted.Where(w => w.On && !IsOn(w.Mod)).Select(w => w.Mod.manifest?.id).OfType<string>().Distinct().Where(id => id != ModEntry.ModId).ToList())
            {
                var win = Winner([.. wanted.Where(w => w.On && w.Mod.manifest?.id == id).Select(w => w.Mod)], winner);
                foreach (var other in all.Where(m => m.manifest?.id == id))
                    final[Copies.SwitchKey(other)] = Copies.CopyOf(other) == Copies.CopyOf(win);
            }
        bool Final(Mod m) => final.TryGetValue(Copies.SwitchKey(m), out var on) ? on : IsOn(m);

        var settings = SaveManager.Instance.SettingsSave.ModSettings ??= new ModSettings();
        var entries = AllSavedEntries(settings.ModList);
        var picked = new List<string>();
        foreach (var (id, source) in all.Where(m => final.ContainsKey(Copies.SwitchKey(m))).Select(m => (Id: m.manifest?.id, m.modSource)).Distinct().ToList())
        {
            if (id == null)
                continue;
            var copies = all.Where(m => m.manifest?.id == id && m.modSource == source).ToList();
            var on = copies.Any(Final);
            // No entry is on already (a mod installed while the game runs has none yet): one is made to turn it off.
            if (!entries.TryGetValue((id, source), out var list) && !on)
            {
                var entry = new SettingsSaveMod { Id = id, Source = source };
                settings.ModList.Add(entry);
                entries[(id, source)] = list = [entry];
            }
            foreach (var entry in list ?? [])
                entry.IsEnabled = on;
            if (id != ModEntry.ModId && copies.Select(Copies.CopyOf).Distinct().Count() > 1 && copies.FirstOrDefault(Final) is { } loads)
                picked.Add(Copies.SwitchKey(loads));
        }
        if (picked.Count > 0 && Copies.WithPicks(Store.Data.CopyPicks, picked) is var picks
            && !(picks ?? []).SequenceEqual(Store.Data.CopyPicks ?? []))
        {
            if (Store.ReadOnly)
                Log.Warn($"[{ModEntry.ModId}] Our data can't be saved this session, so which copy of a mod loads stays as it was: {string.Join(", ", picked)}");
            else
            {
                Store.Data.CopyPicks = picks;
                Store.SaveViewState(); // the caller records the change, both halves in one undo step
            }
        }
        SaveManager.Instance.SaveSettings();
    }

    // Of copies of one id turned on together, the one that stays on (Write): winner if it's one of them, else one on
    // already, else the picked one, else the first.
    static Mod Winner(List<Mod> on, Mod? winner) => winner != null && on.Contains(winner) ? winner
        : on.FirstOrDefault(IsOn) ?? on.FirstOrDefault(m => Copies.Twins(m).Count > 0 && !ChangesPick(m)) ?? on[0];

    // The copies on now, besides the ones given, that turning these on turns off (Write: one copy of an id on), for a
    // tip before the click.
    public static List<Mod> TurnedOffBy(IReadOnlyCollection<Mod> turnedOn, Mod? winner = null)
    {
        var off = new List<Mod>();
        foreach (var id in turnedOn.Where(m => !IsOn(m)).Select(m => m.manifest?.id).OfType<string>().Distinct().Where(id => id != ModEntry.ModId))
        {
            var on = turnedOn.Where(m => m.manifest?.id == id).ToList();
            var win = Winner(on, winner);
            off.AddRange(EachCopy(ModManager.Mods.Union(Installed.Mods))
                .Where(m => m.manifest?.id == id && Copies.CopyOf(m) != Copies.CopyOf(win) && !on.Contains(m) && IsOn(m)));
        }
        return off;
    }

    // Enabled mods placed before an enabled mod they need.
    public static List<(Mod Mod, Mod Needs)> OrderViolations(IReadOnlyList<Mod> order)
    {
        var index = order.Select((m, i) => (m, i)).DistinctBy(p => p.m).ToDictionary(p => p.m, p => p.i);
        return order.Where(IsOn)
            .SelectMany(m => Of(m).Select(Find).OfType<Mod>().Where(d => IsOn(d) && index.TryGetValue(d, out var i) && i > index[m])
                .Select(d => (m, d)))
            .ToList();
    }

    // The order with each enabled dependency moved just ahead of what needs it, otherwise unchanged (the game's own
    // stable topological sort).
    public static List<Mod> Fixed(IReadOnlyList<Mod> given)
    {
        var order = given.Distinct().ToList(); // a mod listed twice counts once
        var position = order.Select((m, i) => (m, i)).ToDictionary(p => p.m, p => p.i);
        // Each enabled mod's enabled dependencies in the list, worked out once.
        var needs = order.ToDictionary(m => m, m => IsOn(m)
            ? Of(m).Select(Find).OfType<Mod>().Where(d => IsOn(d) && position.ContainsKey(d) && d != m).Distinct().ToList()
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
