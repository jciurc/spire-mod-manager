using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Saves;

namespace SpireModManager;

// Answers every row of a redraw asks for (on/off, which copy an id means, who needs whom, tags, search text,
// changes since launch), worked out once per redraw: asked row by row, a 250-mod redraw took about 70 ms.
// Use `using var lookups = Lookups.Begin();` at the top of a redraw; outside one, everything is worked out fresh.
sealed class Lookups : IDisposable
{
    public static Lookups? Current { get; private set; }

    readonly HashSet<(string? Id, ModSource Source)> _off;
    readonly Picks _picks;
    readonly Dictionary<string, Mod> _byId = [];
    readonly Dictionary<string, List<Mod>> _neededBy = [];
    readonly Dictionary<Mod, IReadOnlyList<Tags.Chip>> _chips = [];
    readonly Dictionary<Mod, IReadOnlyList<string>> _dependencies = [];
    readonly Dictionary<Mod, Search.Haystack> _haystacks = [];
    readonly Dictionary<string, string> _texts = [];
    HashSet<Mod>? _changed;
    int _depth = 1; // redraws started inside this one (one redraw can start another): the outermost ends it

    // From the game's lists as they are now.
    public static Lookups Begin() =>
        Begin(ModManager.Mods, Installed.Mods, SaveManager.Instance.SettingsSave.ModSettings?.ModList ?? [], Picks.Now);

    // From given lists (the self-test's made-up mods); inside another redraw, that one's answers serve.
    public static Lookups Begin(IReadOnlyList<Mod> mods, IReadOnlyList<Mod> installed, IEnumerable<SettingsSaveMod> saved, Picks picks)
    {
        if (Current is { } outer)
        {
            outer._depth++;
            return outer;
        }
        return Current = new Lookups(mods, installed, saved, picks);
    }

    public void Dispose()
    {
        if (--_depth == 0 && Current == this)
            Current = null;
    }

    Lookups(IReadOnlyList<Mod> mods, IReadOnlyList<Mod> installed, IEnumerable<SettingsSaveMod> saved, Picks picks)
    {
        _off = Dependencies.SavedOff(saved);
        _picks = picks;
        // The copy Dependencies.Find picks for an id: the first it prefers most (Dependencies.Preference).
        foreach (var mod in mods.Concat(installed))
            if (mod.manifest?.id is { } id && (!_byId.TryGetValue(id, out var kept)
                    || Dependencies.Preference(mod, IsOn, _picks) > Dependencies.Preference(kept, IsOn, _picks)))
                _byId[id] = mod;
        // Who needs whom, from the game's list only; a mod naming one twice counts once.
        foreach (var mod in mods)
            foreach (var id in DependenciesOf(mod, Dependencies.Read).Distinct()) // kept for the redraw's own asks
            {
                if (id == null)
                    continue; // a manifest entry without an id: Dependencies works those out the slow way
                if (!_neededBy.TryGetValue(id, out var needing))
                    _neededBy[id] = needing = [];
                needing.Add(mod);
            }
    }

    public bool IsOn(Mod mod) => Dependencies.OnIn(mod, _off, _picks);

    public Mod? Find(string id) => _byId.GetValueOrDefault(id);

    public IReadOnlyList<Mod> NeededBy(string id) => _neededBy.TryGetValue(id, out var needing) ? needing : [];

    // Per mod or per key: worked out on first use in this redraw, then kept.
    public IReadOnlyList<Tags.Chip> Chips(Mod mod, Func<Mod, IReadOnlyList<Tags.Chip>> make) => Kept(_chips, mod, make);

    public IReadOnlyList<string> DependenciesOf(Mod mod, Func<Mod, IReadOnlyList<string>> make) => Kept(_dependencies, mod, make);

    public Search.Haystack Haystack(Mod mod, Func<Mod, Search.Haystack> make) => Kept(_haystacks, mod, make);

    public string Text(string key, Func<string, string> make) => Kept(_texts, key, make);

    // The mods changed since launch, asked for every row.
    public HashSet<Mod> Changed(Func<HashSet<Mod>> make) => _changed ??= make();

    static TValue Kept<TKey, TValue>(Dictionary<TKey, TValue> kept, TKey key, Func<TKey, TValue> make) where TKey : notnull
    {
        if (!kept.TryGetValue(key, out var value))
            kept[key] = value = make(key);
        return value;
    }
}
