using System.Runtime.CompilerServices;
using Godot;
using MegaCrit.Sts2.Core.Debug;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;

namespace SpireModManager;

// A mod installed more than once with one id (a Workshop item and a local fix, two Workshop items of one mod): each
// copy is its own mod in our data, with its own category, alias, notes and tags. A copy is "w:<Workshop item id>", or
// its folder: "l:<folder>" for a local copy, "ws:<folder>" for a Workshop one whose item id can't be read. One copy of
// an id, the holder, keeps the plain id's entry; every other copy has "<id>|<copy>", seeded from the plain entry the
// first time it's seen (StoreData.Copies lists them, the holder first). A mod installed once has no record and its data
// stays under its id, so files without copies never change. The game's own list can't tell same-source copies apart
// (it keeps id, source and on/off): which of them loads is our pick (StoreData.CopyPicks, applied by SameIdLoad).
static class Copies
{
    // Every mod the game found, and those installed this session.
    static IEnumerable<Mod> All => ModManager.Mods.Union(Installed.Mods);

    // Worked out once per copy (a Workshop id read by reflection on the beta, a folder name).
    static readonly ConditionalWeakTable<Mod, string> Names = new();

    public static string CopyOf(Mod mod) => Names.GetValue(mod, m => Compat.WorkshopId(m) is { } item ? $"w:{item}"
        : (m.modSource == ModSource.SteamWorkshop ? "ws:" : "l:")
            + (Path.GetFileName(m.path?.TrimEnd('/', '\\') ?? "") is { Length: > 0 } folder ? folder : m.modSource.ToString()));

    // The key a copy's pick is saved under (StoreData.CopyPicks): "<id>|<copy>", the same whichever copy holds the id's
    // entry (Key changes with the holder).
    public static string SwitchKey(Mod mod) => $"{mod.manifest?.id}|{CopyOf(mod)}";

    // The id, copy and source a saved pick names ("w:" and "ws:" copies are the Workshop's, "l:" ones local); null for
    // a key that names none.
    public static (string Id, string Copy, ModSource Source)? Parse(string? key)
    {
        var bar = key?.IndexOf('|') ?? -1;
        if (key == null || bar <= 0)
            return null;
        var copy = key[(bar + 1)..];
        ModSource? source = copy.StartsWith("w:") || copy.StartsWith("ws:") ? ModSource.SteamWorkshop
            : copy.StartsWith("l:") ? ModSource.ModsDirectory : null;
        return source is { } from && copy.Length > copy.IndexOf(':') + 1 ? (key[..bar], copy, from) : null;
    }

    // The saved picks with these made the copies that load, each in place of its id and source's pick (tidied).
    public static List<string>? WithPicks(IEnumerable<string>? stored, IEnumerable<string> picked)
    {
        var made = new List<(string Id, string Copy, ModSource Source)>();
        foreach (var key in picked)
            if (Parse(key) is { } pick)
                made.Add(pick);
        var replaced = made.Select(p => (p.Id, p.Source)).ToHashSet();
        return Store.TidyPicks((stored ?? []).Where(key => Parse(key) is not { } kept || !replaced.Contains((kept.Id, kept.Source)))
            .Concat(made.Select(p => $"{p.Id}|{p.Copy}")));
    }

    // A copy as the player knows it: its Workshop item id, or its folder.
    public static string Shown(string copy) => copy[(copy.IndexOf(':') + 1)..];

    // The key our data has for this copy: its id, or "<id>|<copy>" for a copy that isn't its id's holder.
    public static string Key(Mod mod) => mod.manifest?.id is not { } id ? ""
        : Store.Data.Copies?.TryGetValue(id, out var copies) == true && copies.Count > 0 && CopyOf(mod) is var copy && copies[0] != copy
            ? $"{id}|{copy}" : id;

    public static string? KeyOf(Mod? mod) => mod == null ? null : Key(mod);

    // The mod id in a key.
    public static string IdOf(string key) => key.Split('|')[0];

    // What a link to this copy names ("mod:<key>"): where its id is installed more than once, its own copy ("<id>|<copy>",
    // SwitchKey), so a link to the copy that holds the plain id never finds the copy that's on; else its id.
    public static string LinkKey(Mod mod) => Others(mod).Count > 0 ? SwitchKey(mod) : mod.manifest?.id ?? "";

    // The installed copy a key names (our data's key, or a link's SwitchKey); a plain id with no holder installed: its
    // first copy.
    public static Mod? ModOf(string? key) => key == null ? null
        : All.FirstOrDefault(m => Key(m) == key) ?? All.FirstOrDefault(m => key.Contains('|') && SwitchKey(m) == key)
            ?? All.FirstOrDefault(m => m.manifest?.id == key);

    // The row a key names (a copy's, by our data's key or a link's, or for a plain id, its holder's or else its first
    // copy's: a dependency link names only the id).
    public static NModMenuRow? RowOf(Control content, string? key)
    {
        if (key == null)
            return null;
        var rows = content.GetChildren().OfType<NModMenuRow>().ToList();
        return rows.FirstOrDefault(r => r.Mod is { } m && Key(m) == key) ?? rows.FirstOrDefault(r => r.Mod is { } m && key.Contains('|') && SwitchKey(m) == key)
            ?? rows.FirstOrDefault(r => r.Mod?.manifest?.id == key);
    }

    // The other installed copies of a mod (none for a mod installed once), from a table made again when mods come.
    public static IReadOnlyList<Mod> Others(Mod mod) => mod.manifest?.id is { } id && ById().TryGetValue(id, out var copies)
        ? [.. copies.Where(m => m != mod && CopyOf(m) != CopyOf(mod))] : [];

    // The other copies from the same source (two Workshop items of one mod): the game's list has one switch for them, and
    // only the picked one loads.
    public static IReadOnlyList<Mod> Twins(Mod mod) => Others(mod) is { Count: > 0 } others ? [.. others.Where(m => m.modSource == mod.modSource)] : [];

    // Whether any id is installed more than once from one source (most players have none).
    public static bool AnyTwins
    {
        get
        {
            ById();
            return _twinGroups.Count > 0;
        }
    }

    // Each id and source installed more than once, with its copies (CopyOf), in list order.
    public static IReadOnlyList<(string Id, ModSource Source, List<string> Copies)> TwinGroups()
    {
        ById();
        return _twinGroups;
    }

    static Dictionary<string, List<Mod>>? _byId;
    static List<(string Id, ModSource Source, List<string> Copies)> _twinGroups = [];
    static int _byIdCount = -1;

    static Dictionary<string, List<Mod>> ById()
    {
        var count = ModManager.Mods.Count + Installed.Mods.Count;
        if (_byId == null || count != _byIdCount)
        {
            _byId = All.Where(m => m.manifest?.id != null).GroupBy(m => m.manifest?.id ?? "")
                .Where(g => g.Select(CopyOf).Distinct().Count() > 1).ToDictionary(g => g.Key, g => g.ToList());
            _twinGroups = [.. _byId.SelectMany(p => p.Value.GroupBy(m => m.modSource).Select(g => (Id: p.Key, Source: g.Key, Copies: g.Select(CopyOf).Distinct().ToList())))
                .Where(g => g.Copies.Count > 1 && g.Id != ModEntry.ModId)]; // ours load as the game decides
            _byIdCount = count;
        }
        return _byId;
    }

    // The copy of an id and source that loads (its CopyOf), where it's installed more than once from that source: the
    // pick when it names one of them; else the one the game loaded; else the first in our record (StoreData.Copies);
    // else the first as Reconcile orders them. Null for an id and source installed once, or not at all.
    public static string? Picked(string id, ModSource source) => Picked(id, source, Picks.Now);

    public static string? Picked(string id, ModSource source, Picks picks)
    {
        if (!ById().TryGetValue(id, out var all))
            return null;
        var group = all.Where(m => m.modSource == source).ToList();
        var copies = group.Select(CopyOf).Distinct().ToList();
        if (copies.Count < 2)
            return null;
        if (picks.Stored(id, source) is { } stored && copies.Contains(stored))
            return stored;
        if (group.FirstOrDefault(m => m.state == ModLoadState.Loaded) is { } loaded)
            return CopyOf(loaded);
        if (picks.Record?.GetValueOrDefault(id)?.FirstOrDefault(copies.Contains) is { } recorded)
            return recorded;
        return Ordered(copies, Launched()).First();
    }

    // Copies the game found at launch, before those installed since: they keep the id they had.
    static HashSet<string> Launched() =>
        ModManager.Mods.Where(m => m.state != ModLoadState.AddedAtRuntime).Except(Installed.Mods).Select(CopyOf).ToHashSet();

    // A copy found at launch first, then a Workshop copy before a local one, the oldest item first (fixed, so a version
    // bump never moves it).
    static IEnumerable<string> Ordered(IEnumerable<string> copies, HashSet<string> launched) =>
        copies.OrderBy(c => launched.Contains(c) ? 0 : 1).ThenBy(c => c.StartsWith("w:") ? 0 : 1)
            .ThenBy(c => c.StartsWith("w:") && ulong.TryParse(c[2..], out var item) ? item : ulong.MaxValue).ThenBy(c => c, StringComparer.Ordinal);

    // The copies that are on where two or more copies of one id are on (the game loads only one of them): the same-id
    // banner's mods, in list order. Same-source copies take turns (one is picked), so this is a local copy and a
    // Workshop one, both on.
    public static List<Mod> Clashing() => [.. ById().Values.Select(copies => copies.Where(Dependencies.IsOn).DistinctBy(CopyOf).ToList())
        .Where(on => on.Count > 1).SelectMany(on => on)];

    // Whether the game sets this copy aside for the other copy of its id from the other source, both on (its local-vs-
    // Workshop rule, ModManager.RemoveDisabledMods): the beta keeps the higher version, a tie or an unreadable one going
    // to the local copy; stable always keeps the local copy.
    public static bool Loses(Mod mod)
    {
        if (Others(mod).Where(Dependencies.IsOn).FirstOrDefault(m => m.modSource != mod.modSource) is not { } rival)
            return false;
        var (workshop, local) = mod.modSource == ModSource.SteamWorkshop ? (mod, rival) : (rival, mod);
        var workshopNewer = Compat.WorkshopBeatsOlderLocal && SemanticVersion.TryFromString(workshop.manifest?.version ?? "", out var w)
            && SemanticVersion.TryFromString(local.manifest?.version ?? "", out var l) && w != null && l != null && w.CompareTo(l) > 0;
        return mod == (workshopNewer ? local : workshop);
    }

    // Which copies clash, as one string: a dismissed banner stays away for this set, and shows again for another.
    public static string Signature(IEnumerable<Mod> clashing) => string.Join(",", clashing.Select(SwitchKey).Order(StringComparer.Ordinal));

    // " (Local)" or " (<Workshop item id>)" after a mod's name, where another copy of it is installed (a local copy
    // beside another local one: its folder); else nothing.
    public static string Which(Mod mod) => Others(mod).Count == 0 ? ""
        : $" ({(Compat.WorkshopId(mod) is { } item ? item.ToString()
            : mod.modSource == ModSource.ModsDirectory && Twins(mod).Count > 0 ? Shown(CopyOf(mod)) : Menus.Text("SPIRE_MOD_MANAGER.SOURCE_LOCAL"))})";

    // Brings the record in line with the copies installed, and gives each id and source installed more than once a pick
    // when it has none that names one of its copies (Picked: the copy the game loaded, so nothing changes until the
    // player picks another): at the first main menu, before the undo baseline (so it's neither an undo step nor a change
    // since launch). True if it changed.
    public static bool Reconcile(StoreData data)
    {
        var changed = false;
        var launched = Launched();
        foreach (var group in All.Where(m => m.manifest?.id != null).GroupBy(m => m.manifest?.id ?? ""))
        {
            var id = group.Key;
            var installed = group.Select(CopyOf).Distinct().ToList();
            data.Copies ??= [];
            if (!data.Copies.TryGetValue(id, out var copies))
            {
                if (installed.Count < 2)
                    continue;
                // First split: the plain entry stays with the first copy as Ordered puts them.
                data.Copies[id] = copies = [.. Ordered(installed, launched)];
                foreach (var copy in copies.Skip(1))
                    Seed(data, id, copy);
                changed = true;
                continue;
            }
            foreach (var copy in installed.Where(c => !copies.Contains(c)))
            {
                copies.Add(copy);
                Seed(data, id, copy);
                changed = true;
            }
            // The holder gone: its data moves to its own key, the next installed copy's to the plain id. Nothing is lost.
            if (copies[0] is var holder && !installed.Contains(holder) && copies.FirstOrDefault(installed.Contains) is { } next)
            {
                var (plain, own) = (data.Mods.GetValueOrDefault(id), data.Mods.GetValueOrDefault($"{id}|{next}"));
                Put(data, $"{id}|{holder}", plain);
                Put(data, id, own);
                data.Mods.Remove($"{id}|{next}");
                copies.Remove(next);
                copies.Insert(0, next);
                changed = true;
            }
        }
        var picks = new Picks(data.CopyPicks, data.Copies);
        var seeded = TwinGroups().Where(g => picks.Stored(g.Id, g.Source) is not { } stored || !g.Copies.Contains(stored))
            .Select(g => Picked(g.Id, g.Source, picks) is { } copy ? $"{g.Id}|{copy}" : null).OfType<string>().ToList();
        if (seeded.Count > 0)
        {
            data.CopyPicks = WithPicks(data.CopyPicks, seeded);
            changed = true;
        }
        return changed;
    }

    // A copy's own entry, from the plain one, unless it has one already (or the plain id has none).
    static void Seed(StoreData data, string id, string copy)
    {
        if (data.Mods.TryGetValue(id, out var plain) && !data.Mods.ContainsKey($"{id}|{copy}"))
            data.Mods[$"{id}|{copy}"] = Clone(plain);
    }

    static ModInfo Clone(ModInfo info) => new() { Category = info.Category, Alias = info.Alias, Notes = info.Notes, Tags = info.Tags?.ToList() };

    static void Put(StoreData data, string key, ModInfo? info)
    {
        if (info == null)
            data.Mods.Remove(key);
        else
            data.Mods[key] = info;
    }

    static bool _started;

    // At the first main menu, with the store loaded: before Profiles.Prepare and History.Start. Once a session.
    public static void Start()
    {
        if (_started)
            return;
        _started = true;
        try
        {
            if (Reconcile(Store.Data))
                Store.Save();
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.ModId}] Could not sort out mods installed more than once: {e}");
        }
        Installed.Added += () => _byId = null; // an update can replace a copy's Mod with no change in the count
        Installed.Added += Arrived;
    }

    // A copy installed mid-session: worked out on our data as it is now, then written as it is into every undo
    // snapshot (History.Quietly), so the new copy's entry is the same in each: no change since launch, no undo step.
    // Its id and source's pick stays the copy that was there (it never takes over; enabling it does).
    static void Arrived()
    {
        try
        {
            var now = Store.Clone(Store.Data);
            var changed = Reconcile(now);
            // Installed adds the copy that came last.
            if (Installed.Mods.LastOrDefault() is { } come && KeepThere(now, come))
                changed = true;
            if (!changed || now.Copies is not { } record)
                return;
            var added = now.Mods.Where(p => !Store.Data.Mods.ContainsKey(p.Key)).Select(p => (p.Key, p.Value)).ToList();
            var seeded = (now.CopyPicks ?? []).Except(Store.Data.CopyPicks ?? []).ToList();
            History.Quietly(data =>
            {
                data.Copies = record.ToDictionary(p => p.Key, p => p.Value.ToList());
                foreach (var (key, info) in added)
                    data.Mods[key] = Clone(info);
                if (seeded.Count > 0)
                    data.CopyPicks = WithPicks(data.CopyPicks, seeded);
            });
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.ModId}] Could not sort out a newly installed copy of a mod: {e}");
        }
    }

    // Where the pick of its id and source names the copy just installed (a pick saved before it was uninstalled: picks
    // are never pruned; or the record's order, where both came this session), the pick goes to the copy that loads
    // without it, in Picked's order (the one loaded, else the record's first). Not for an update of a copy installed
    // this session (the game keeps its earlier Mod): a pick of that one is the player's. True if it changed.
    static bool KeepThere(StoreData data, Mod come)
    {
        if (come.manifest?.id is not { } id || id == ModEntry.ModId || All.Any(m => m != come && SwitchKey(m) == SwitchKey(come)))
            return false;
        var copy = CopyOf(come);
        if (new Picks(data.CopyPicks, data.Copies).Picked(id, come.modSource) != copy)
            return false;
        var without = new Picks(data.CopyPicks?.Where(k => k != SwitchKey(come)),
            data.Copies?.ToDictionary(p => p.Key, p => p.Value.Where(c => c != copy).ToList()));
        if (without.Picked(id, come.modSource) is not { } there || there == copy)
            return false;
        data.CopyPicks = WithPicks(data.CopyPicks, [$"{id}|{there}"]);
        return true;
    }
}

// Which copy loads for each id and source installed more than once (StoreData.CopyPicks), read once for many asks: our
// data as it is now, or a snapshot of it (Store.PicksIn). Record: the copies record it came with (StoreData.Copies),
// for Copies.Picked's fallback.
public sealed class Picks
{
    readonly Dictionary<(string Id, ModSource Source), string> _stored = [];

    public IReadOnlyDictionary<string, List<string>>? Record { get; }

    public Picks(IEnumerable<string>? stored, IReadOnlyDictionary<string, List<string>>? record)
    {
        foreach (var key in stored ?? [])
            if (Copies.Parse(key) is { } pick)
                _stored.TryAdd((pick.Id, pick.Source), pick.Copy);
        Record = record;
    }

    public static readonly Picks None = new(null, null);

    // Ours now; none without loading our data when no mod is installed twice from one source (most players, and Init).
    public static Picks Now => Copies.AnyTwins ? Of(Store.Data) : None;

    // Read again only when the data or its picks were replaced (every writer puts in a new list).
    static (StoreData Data, List<string>? Stored, Dictionary<string, List<string>>? Record, Picks Picks)? _now;

    static Picks Of(StoreData data)
    {
        if (_now is { } now && now.Data == data && now.Stored == data.CopyPicks && now.Record == data.Copies)
            return now.Picks;
        var picks = new Picks(data.CopyPicks, data.Copies);
        _now = (data, data.CopyPicks, data.Copies, picks);
        return picks;
    }

    // The copy saved for an id and source, whether it's installed or not.
    public string? Stored(string id, ModSource source) => _stored.GetValueOrDefault((id, source));

    public string? Picked(string id, ModSource source) => Copies.Picked(id, source, this);
}
