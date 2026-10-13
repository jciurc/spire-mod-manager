using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Saves;

namespace SpireModManager;

// Answers that every row of a redraw asks for, worked out once for the whole redraw: whether a mod is on (the
// game's IsModDisabled walks its whole saved list each time), which installed copy an id means, and which mods
// need a mod. Asked afresh, every row's "Required for" asked whether each other mod was on, so a redraw with
// about 250 mods walked the saved list some 60,000 times (around 70 ms).
// Nothing changes these answers while a redraw runs, so they are kept for just that long:
// `using var lookups = Lookups.Begin();` at the top of a redraw. Outside one, Dependencies works everything out
// fresh, so a click or a menu always sees the current state.
sealed class Lookups : IDisposable
{
    public static Lookups? Current { get; private set; }

    readonly HashSet<(string? Id, ModSource Source)> _off;
    readonly Dictionary<string, Mod> _byId = [];
    readonly Dictionary<string, List<Mod>> _neededBy = [];
    int _depth = 1; // redraws started inside this one (one redraw can start another): the outermost ends it

    // From the game's lists as they are now.
    public static Lookups Begin() =>
        Begin(ModManager.Mods, Installed.Mods, SaveManager.Instance.SettingsSave.ModSettings?.ModList ?? []);

    // From any lists (the self-test's made-up mods): mods stands for the game's list, installed for the mods added
    // since launch, saved for the saved on/off list. Inside another redraw, that one's answers serve.
    public static Lookups Begin(IReadOnlyList<Mod> mods, IReadOnlyList<Mod> installed, IEnumerable<SettingsSaveMod> saved)
    {
        if (Current is { } outer)
        {
            outer._depth++;
            return outer;
        }
        return Current = new Lookups(mods, installed, saved);
    }

    public void Dispose()
    {
        if (--_depth == 0 && Current == this)
            Current = null;
    }

    Lookups(IReadOnlyList<Mod> mods, IReadOnlyList<Mod> installed, IEnumerable<SettingsSaveMod> saved)
    {
        _off = saved.Where(e => !e.IsEnabled).Select(e => ((string?)e.Id, e.Source)).ToHashSet();
        // The copy Dependencies.Find picks for an id: the first one that is on, else the first.
        foreach (var mod in mods.Concat(installed))
            if (mod.manifest?.id is { } id && (!_byId.TryGetValue(id, out var kept) || !IsOn(kept) && IsOn(mod)))
                _byId[id] = mod;
        // Who needs whom, from the game's list only (as Dependencies.NeededBy); a mod naming one twice counts once.
        foreach (var mod in mods)
            foreach (var id in Dependencies.Of(mod).Distinct())
            {
                if (id == null)
                    continue; // a manifest entry without an id: Dependencies works those out the slow way
                if (!_neededBy.TryGetValue(id, out var needing))
                    _neededBy[id] = needing = [];
                needing.Add(mod);
            }
    }

    public bool IsOn(Mod mod) => !_off.Contains((mod.manifest?.id, mod.modSource));

    public Mod? Find(string id) => _byId.GetValueOrDefault(id);

    public IReadOnlyList<Mod> NeededBy(string id) => _neededBy.TryGetValue(id, out var needing) ? needing : [];
}
