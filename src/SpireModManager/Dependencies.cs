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

    public static bool IsOn(Mod mod) => Lookups.Current?.IsOn(mod) ?? !(SaveManager.Instance.SettingsSave.ModSettings?.IsModDisabled(mod) ?? false);

    // The (id, source) pairs the saved list has off, for checking many mods at once (IsModDisabled walks the list per
    // mod): any entry for one off, as the game reads it.
    public static HashSet<(string?, ModSource)> SavedOff() => SavedOff(SaveManager.Instance.SettingsSave.ModSettings?.ModList ?? []);

    public static HashSet<(string?, ModSource)> SavedOff(IEnumerable<SettingsSaveMod> saved) =>
        saved.Where(e => !e.IsEnabled).Select(e => ((string?)e.Id, e.Source)).ToHashSet();

    // Each mod's saved entry by (id, source), the first if listed twice (as the game's own lookups find it), for looking
    // up many mods at once.
    public static Dictionary<(string?, ModSource), SettingsSaveMod> SavedEntries(IEnumerable<SettingsSaveMod> saved)
    {
        var entries = new Dictionary<(string?, ModSource), SettingsSaveMod>();
        foreach (var entry in saved)
            entries.TryAdd((entry.Id, entry.Source), entry);
        return entries;
    }

    // The installed copy of an id, preferring one that's on; mods installed this session count.
    public static Mod? Find(string id) => id != null && Lookups.Current is { } now ? now.Find(id)
        : ModManager.Mods.Union(Installed.Mods).Where(m => m.manifest?.id == id).OrderByDescending(IsOn).FirstOrDefault();

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

    // Turns mods on or off in the saved list and ticks the page's boxes; the caller's redraw records one change. This
    // mod stays on.
    public static void Set(IEnumerable<Mod> mods, bool on, NModdingScreen? screen)
    {
        var settings = SaveManager.Instance.SettingsSave.ModSettings ??= new ModSettings();
        var entries = AllSavedEntries(settings.ModList);
        foreach (var mod in mods)
        {
            if (!entries.TryGetValue((mod.manifest?.id, mod.modSource), out var list))
            {
                var entry = new SettingsSaveMod(mod);
                settings.ModList.Add(entry);
                entries[(mod.manifest?.id, mod.modSource)] = list = [entry];
            }
            foreach (var entry in list)
                entry.IsEnabled = on || mod.manifest?.id == ModEntry.ModId;
        }
        SaveManager.Instance.SaveSettings();
        if (screen != null)
            Profiles.RefreshTickboxes(screen);
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
