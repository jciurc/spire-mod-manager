using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;
using MegaCrit.Sts2.Core.Saves;

namespace SpireModManager;

// Hard dependencies from mod manifests. The game loads a mod after everything it needs, whatever the saved order,
// and fails it if one isn't loaded; order rules only bind mods that are both on. Inside a redraw, Lookups answers.
static class Dependencies
{
    public static IReadOnlyList<string> Of(Mod? mod) =>
        mod?.manifest?.dependencies?.Select(d => d.id).ToList() ?? [];

    public static bool IsOn(Mod mod) => Lookups.Current?.IsOn(mod) ?? !(SaveManager.Instance.SettingsSave.ModSettings?.IsModDisabled(mod) ?? false);

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
        var waiting = new Queue<Mod>(mods.Where(IsOn));
        while (waiting.TryDequeue(out var mod))
            foreach (var dependency in Of(mod).Select(Find).OfType<Mod>())
                if (!IsOn(dependency) && !needed.Contains(dependency))
                {
                    needed.Add(dependency);
                    waiting.Enqueue(dependency);
                }
        return needed;
    }

    // Turns mods on or off in the saved list and ticks the page's boxes; the caller's redraw records one change. This
    // mod stays on.
    public static void Set(IEnumerable<Mod> mods, bool on, NModdingScreen? screen)
    {
        var settings = SaveManager.Instance.SettingsSave.ModSettings ??= new ModSettings();
        foreach (var mod in mods)
        {
            var entry = settings.ModList.FirstOrDefault(e => e.Id == mod.manifest?.id && e.Source == mod.modSource);
            if (entry == null)
                settings.ModList.Add(entry = new SettingsSaveMod(mod));
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
        var waitingOn = order.ToDictionary(m => m, m => IsOn(m)
            ? Of(m).Select(Find).OfType<Mod>().Where(d => IsOn(d) && position.ContainsKey(d) && d != m).Distinct().Count()
            : 0);
        var dependents = order.ToDictionary(m => m, _ => new List<Mod>());
        foreach (var m in order.Where(IsOn))
            foreach (var d in Of(m).Select(Find).OfType<Mod>().Where(d => IsOn(d) && position.ContainsKey(d) && d != m).Distinct())
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
        result.AddRange(order.Where(m => !result.Contains(m)));
        return result;
    }
}
