using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Saves;

namespace SpireModManager;

// Answers every row of a redraw asks for (ticked, loads, which copy an id means, who needs whom, tags, search text,
// changes since launch, the sets over the whole list a search's hidden tags ask for), worked out once per redraw: asked
// row by row, a 250-mod redraw took about 70 ms.
// Use `using var lookups = Lookups.Begin();` at the top of a redraw; outside one, everything is worked out fresh.
sealed class Lookups : IDisposable
{
    public static Lookups? Current { get; private set; }

    readonly HashSet<(string? Id, ModSource Source)> _off;
    readonly IReadOnlyList<SettingsSaveMod> _saved;
    readonly Ticks _ticks;
    Dictionary<string, Mod>? _byId;
    readonly Dictionary<string, Copies.Outcome> _next = [];
    readonly Dictionary<string, List<Mod>> _orders = [];
    readonly Dictionary<string, List<Mod>> _neededBy = [];
    readonly Dictionary<Mod, IReadOnlyList<Tags.Chip>> _chips = [];
    readonly Dictionary<Mod, IReadOnlyList<string>> _dependencies = [];
    readonly Dictionary<Mod, Search.Haystack> _haystacks = [];
    readonly Dictionary<string, string> _texts = [];
    readonly IReadOnlyList<Mod> _mods, _installed;
    readonly Dictionary<string, HashSet<Mod>> _wholes = [];
    HashSet<Mod>? _changed;
    int _depth = 1; // redraws started inside this one (one redraw can start another): the outermost ends it

    // From the game's lists as they are now.
    public static Lookups Begin() =>
        Begin(ModManager.Mods, Installed.Mods, SaveManager.Instance.SettingsSave.ModSettings?.ModList ?? [], Ticks.Now);

    // From given lists (the self-test's made-up mods); inside another redraw, that one's answers serve.
    public static Lookups Begin(IReadOnlyList<Mod> mods, IReadOnlyList<Mod> installed, IEnumerable<SettingsSaveMod> saved, Ticks ticks)
    {
        if (Current is { } outer)
        {
            outer._depth++;
            return outer;
        }
        return Current = new Lookups(mods, installed, saved, ticks);
    }

    public void Dispose()
    {
        if (--_depth == 0 && Current == this)
            Current = null;
    }

    Lookups(IReadOnlyList<Mod> mods, IReadOnlyList<Mod> installed, IEnumerable<SettingsSaveMod> saved, Ticks ticks)
    {
        (_mods, _installed, _saved) = (mods, installed, saved as IReadOnlyList<SettingsSaveMod> ?? [.. saved]);
        _off = Dependencies.SavedOff(_saved);
        _ticks = ticks;
        // Who needs whom, from the game's list (a mod installed while the game runs is in it too: the game reads it in); a
        // mod naming one twice counts once.
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

    // A copy's box and whether it loads after a relaunch (Copies.Ticked, Copies.Loads), and what loads of an id
    // (Copies.Next, worked out once per id: every view reads it, so none can disagree).
    public bool Ticked(Mod mod) => Copies.TickedIn(mod, _off, _ticks);

    public bool Loads(Mod mod) => Copies.LoadsWith(mod, _off, Loading);

    public Mod? Loading(string id) => Next(id).Loads;

    public Copies.Outcome Next(string id) => Kept(_next, id, i => Copies.Next(i, _off, _ticks, _saved));

    // The next launch's order (Dependencies.NextOrder) for the redraw's saved list and a set of switches off (its own, or
    // with a tick tried on it: Copies.LoadingIf), worked out once each; null for another saved list.
    public List<Mod>? NextOrder(HashSet<(string? Id, ModSource Source)> off, IReadOnlyList<SettingsSaveMod> saved) => saved != _saved ? null
        : Kept(_orders, string.Join("\n", off.Select(o => $"{o.Id}|{o.Source}").Order(StringComparer.Ordinal)), _ => Dependencies.NextOrder(off, saved));

    // The lists it answers from (Copies.LoadingIf tries a tick on them).
    public HashSet<(string? Id, ModSource Source)> Off => _off;

    public Ticks Ticks => _ticks;

    public IReadOnlyList<SettingsSaveMod> Saved => _saved;

    // The copy Dependencies.Find picks for an id: the first it prefers most (Dependencies.Preference), read against these
    // lists on the first ask rather than at Begin, so what it asks of Copies.Next finds this redraw's answers (the next
    // launch's order, worked out once).
    public Mod? Find(string id)
    {
        if (_byId == null)
        {
            _byId = [];
            foreach (var mod in _mods.Concat(_installed))
                if (mod.manifest?.id is { } each && (!_byId.TryGetValue(each, out var kept)
                        || Dependencies.Preference(mod, Loads, Ticked, _ticks) > Dependencies.Preference(kept, Loads, Ticked, _ticks)))
                    _byId[each] = mod;
        }
        return _byId.GetValueOrDefault(id);
    }

    public IReadOnlyList<Mod> NeededBy(string id) => _neededBy.TryGetValue(id, out var needing) ? needing : [];

    // Per mod or per key: worked out on first use in this redraw, then kept.
    public IReadOnlyList<Tags.Chip> Chips(Mod mod, Func<Mod, IReadOnlyList<Tags.Chip>> make) => Kept(_chips, mod, make);

    public IReadOnlyList<string> DependenciesOf(Mod mod, Func<Mod, IReadOnlyList<string>> make) => Kept(_dependencies, mod, make);

    public Search.Haystack Haystack(Mod mod, Func<Mod, Search.Haystack> make) => Kept(_haystacks, mod, make);

    public string Text(string key, Func<string, string> make) => Kept(_texts, key, make);

    // The mods changed since launch, asked for every row.
    public HashSet<Mod> Changed(Func<HashSet<Mod>> make) => _changed ??= make();

    // A set over the redraw's whole list, by name (HiddenTags: #needed, #order...).
    public HashSet<Mod> Whole(string name, Func<IReadOnlyList<Mod>, HashSet<Mod>> make) => Kept(_wholes, name, _ => make(_mods));

    static TValue Kept<TKey, TValue>(Dictionary<TKey, TValue> kept, TKey key, Func<TKey, TValue> make) where TKey : notnull
    {
        if (!kept.TryGetValue(key, out var value))
            kept[key] = value = make(key);
        return value;
    }
}
