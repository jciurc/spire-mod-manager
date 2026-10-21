using System.Runtime.CompilerServices;
using Godot;
using MegaCrit.Sts2.Core.Debug;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;
using MegaCrit.Sts2.Core.Saves;

namespace SpireModManager;

// A mod installed more than once with one id (a Workshop item and a local fix, two Workshop items of one mod): each
// copy is its own mod in our data, with its own category, alias, notes and tags. A copy is "w:<Workshop item id>", or
// its folder: "l:<folder>" for a local copy, "ws:<folder>" for a Workshop one whose item id can't be read. One copy of
// an id, the holder, keeps the plain id's entry; every other copy has "<id>|<copy>", seeded from the plain entry the
// first time it's seen (StoreData.Copies lists them, the holder first). A mod installed once has no record and its data
// stays under its id, so files without copies never change. The game's own list can't tell same-source copies apart
// (it keeps id, source and on/off, one switch for them): which of them are ticked is ours (StoreData.CopiesOff: those
// off while the switch is on), and which of the ticked ones loads is our pick (StoreData.CopyPicks, applied by
// SameIdLoad), the copy enabled last. So a copy's box (Ticked) and whether it loads after a relaunch (Loads) are two
// things: two copies of an id ticked, only one loads.
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
    // pick when it names one of them; else the one the game loaded; else the first in our record (StoreData.Copies)
    // that copies_off doesn't have, else the record's first; else the first as Reconcile orders them. Null for an id
    // and source installed once, or not at all.
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
        if (picks.Record?.GetValueOrDefault(id)?.Where(copies.Contains).ToList() is { Count: > 0 } recorded)
            return recorded.FirstOrDefault(c => picks.Off?.Contains($"{id}|{c}") != true) ?? recorded[0];
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

    // Of these copies, the first as Ordered puts them; null for none.
    public static Mod? FirstInOrder(IEnumerable<Mod> copies)
    {
        var list = copies.ToList();
        return Ordered(list.Select(CopyOf), Launched()).FirstOrDefault() is { } first ? list.FirstOrDefault(m => CopyOf(m) == first) : null;
    }

    // This copy's own box: whether the player has it on, as our views show it (Vanilla View shows the game's switch).
    // Its id and source's switch is on in the game's list and, where copies share that switch, copies_off doesn't have
    // it or it's the pick (the pick always counts as on; a snapshot from before copies_off: only the pick). This mod
    // goes by its switch alone. Inside a redraw, Lookups answers.
    public static bool Ticked(Mod mod) => Lookups.Current?.Ticked(mod) ?? SwitchOn(mod) && OwnTick(mod, Picks.Now);

    // The same, read against given lists (launch's, a snapshot's, or now's).
    public static bool TickedIn(Mod mod, HashSet<(string?, ModSource)> gameOff, Picks picks) =>
        !gameOff.Contains((mod.manifest?.id, mod.modSource)) && OwnTick(mod, picks);

    // Its id and source's switch as the game reads it (any entry off turns it off), without listing every entry.
    static bool SwitchOn(Mod mod) => !(SaveManager.Instance.SettingsSave.ModSettings?.IsModDisabled(mod) ?? false);

    // With its switch on: copies_off doesn't have it, or it's picked (copies_off first: the pick takes longer to work out).
    static bool OwnTick(Mod mod, Picks picks) => picks.Off is { } off && !off.Contains(SwitchKey(mod)) || IsPicked(mod, picks);

    // Whether this copy is its id and source's pick where copies share that switch; any other mod counts as picked
    // (this mod's copies too: the game sorts them out).
    public static bool IsPicked(Mod mod, Picks picks) => mod.manifest?.id is not { } id || id == ModEntry.ModId
        || Twins(mod).Count == 0 || picks.Picked(id, mod.modSource) == CopyOf(mod);

    // Whether this copy is the one of its id that loads after a relaunch (Loading); for an id installed once, its switch.
    // It implies the switch is on, and nearly always that it's ticked (LoseReason.Other is the exception). Inside a
    // redraw, Lookups answers.
    public static bool Loads(Mod mod) => Lookups.Current?.Loads(mod)
        ?? (Others(mod).Count == 0 ? SwitchOn(mod) : LoadsIn(mod, Dependencies.SavedOff(), Picks.Now));

    // The same, read against given lists.
    public static bool LoadsIn(Mod mod, HashSet<(string?, ModSource)> gameOff, Picks picks) =>
        LoadsWith(mod, gameOff, id => LoadingIn(id, gameOff, picks));

    // The same, given how to find the copy of an id that loads (Lookups keeps each id's for the redraw).
    public static bool LoadsWith(Mod mod, HashSet<(string?, ModSource)> gameOff, Func<string, Mod?> loading) =>
        mod.manifest?.id is { } id && Others(mod).Count > 0 ? loading(id) is { } loads && CopyOf(loads) == CopyOf(mod)
            : !gameOff.Contains((mod.manifest?.id, mod.modSource));

    // The copy of an id that loads after a relaunch, or null when none does. Inside a redraw, Lookups answers.
    public static Mod? Loading(string id) => Lookups.Current is { } now ? now.Loading(id) : LoadingIn(id, Dependencies.SavedOff(), Picks.Now);

    // The same, read against given lists, as the next launch goes. The game's rule first, on switches, not ticks (it can't
    // see copies_off): ModManager.RemoveDisabledMods compares each Workshop copy that's on with the first local copy on
    // it read, keeping the higher version on the beta (the local copy on a tie or an unreadable version) and the local
    // copy on stable. Then SameIdLoad.Apply's choice among what's left: the pick, else the first copy not in copies_off,
    // else the first. Two local copies and a Workshop one are approximated: which local copy the game read first can't
    // be known after its sort, so the local pick stands for it. This mod: the game's own choice (Apply leaves it out).
    public static Mod? LoadingIn(string id, HashSet<(string?, ModSource)> gameOff, Picks picks)
    {
        var copies = (ById().TryGetValue(id, out var all) ? all : All.Where(m => m.manifest?.id == id)).DistinctBy(CopyOf).ToList();
        var on = copies.Where(m => !gameOff.Contains((id, m.modSource))).ToList();
        if (on.Count == 0)
            return null;
        var locals = on.Where(m => m.modSource == ModSource.ModsDirectory).ToList();
        var workshop = on.Where(m => m.modSource == ModSource.SteamWorkshop).ToList();
        var left = on;
        if (locals.Count > 0 && workshop.Count > 0)
            left = locals.Count > 1 ? locals : workshop.Where(w => Beats(w, locals[0])).ToList() is { Count: > 0 } won ? won : locals;
        if (left.Count == 1 || id == ModEntry.ModId)
            return left[0];
        var pick = picks.Picked(id, left[0].modSource);
        return left.FirstOrDefault(m => CopyOf(m) == pick) ?? left.FirstOrDefault(m => picks.Off?.Contains(SwitchKey(m)) != true) ?? left[0];
    }

    // Whether the game keeps this Workshop copy over this local one, both on: the beta keeps the higher version (a
    // pre-release sorts lower), a tie or an unreadable one going to the local copy; stable always keeps the local copy.
    // Each copy's newest manifest, as the next launch reads it (Newest).
    static bool Beats(Mod workshop, Mod local) => Compat.WorkshopBeatsOlderLocal
        && SemanticVersion.TryFromString(Newest(workshop).manifest?.version ?? "", out var w)
        && SemanticVersion.TryFromString(Newest(local).manifest?.version ?? "", out var l) && w != null && l != null && w.CompareTo(l) > 0;

    // A copy's last Mod in the game's list: an update while the game runs adds one with the new manifest after the one
    // found at launch, which keeps the old version (the copy is still told by its first, CopyOf).
    static Mod Newest(Mod copy) => copy.manifest?.id is { } id && ById().TryGetValue(id, out var all)
        ? all.LastOrDefault(m => CopyOf(m) == CopyOf(copy)) ?? copy : copy;

    // Why a ticked copy doesn't load (LosesTo): the game keeps the local copy (stable, or a tie or an unreadable version
    // on the beta), or the higher version (the beta); of copies sharing a switch that the game's rule left, our pick loads
    // (EnabledLast: a copy ticked on becomes the pick); Other: none of these says it, e.g. a Workshop copy that isn't ticked
    // loads, as its switch is shared with one that is and it beats the local copy (the game sees switches only).
    public enum LoseReason { LocalCopy, HigherVersion, EnabledLast, Other }

    // For a ticked copy that won't load after a relaunch while another copy of its id will: that copy, and why. Null for
    // any other.
    public static (Mod Loads, LoseReason Why)? LosesTo(Mod mod)
    {
        if (mod.manifest?.id is not { } id || Others(mod).Count == 0 || !Ticked(mod) || Loads(mod) || Loading(id) is not { } loads)
            return null;
        var why = !Ticked(loads) ? LoseReason.Other
            : loads.modSource == mod.modSource && id == ModEntry.ModId ? LoseReason.Other
            : loads.modSource == mod.modSource && CopyOf(loads) == Picked(id, mod.modSource) ? LoseReason.EnabledLast
            // Not the pick of its source: the game's rule between sources set the pick aside (a newer Workshop copy loads).
            : loads.modSource == ModSource.SteamWorkshop ? LoseReason.HigherVersion
            // A newer Workshop copy losing to a local one: two local copies, so which one the game compares isn't known.
            : Beats(mod, loads) ? LoseReason.Other : LoseReason.LocalCopy;
        return (loads, why);
    }

    // Whether this copy is ticked but won't load, another copy of its id loading instead: the one "won't load (same ID)".
    public static bool Loses(Mod mod) => LosesTo(mod) != null;

    // The copy of its id that would load if this copy were ticked too, as a click on its box does (it's then its id and
    // source's pick, and its switch on): for the tip on a copy that's off.
    public static Mod? LoadingIf(Mod mod)
    {
        if (mod.manifest?.id is not { } id)
            return null;
        var (off, picks) = Lookups.Current is { } now ? (now.Off, now.Picks) : (Dependencies.SavedOff(), Picks.Now);
        return LoadingIn(id, off.Where(o => o != (id, mod.modSource)).ToHashSet(), picks.Ticking(mod));
    }

    // A copy of this mod where it's installed more than once: it stays ticked on our views (Write keeps it on), so
    // removing the copy that runs still leaves one that loads. Installed once, its box works as any other.
    public static bool Locked(Mod mod) => mod.manifest?.id == ModEntry.ModId && Others(mod).Count > 0;

    // Whether this copy is ticked while another copy of its id is too, so one of them won't load: the yellow chip. Never
    // this mod's copies, which stay ticked together (Locked).
    public static bool Clashes(Mod mod) => mod.manifest?.id is { } id && id != ModEntry.ModId && Ticked(mod) && Others(mod).Any(Ticked);

    // The ticked copies where two or more copies of one id are ticked (only one of them loads), copies sharing a switch
    // too: the same-id banner's mods, in list order. This mod's aside.
    public static List<Mod> Clashing() => [.. ById().Where(p => p.Key != ModEntry.ModId).Select(p => p.Value.Where(Ticked).DistinctBy(CopyOf).ToList())
        .Where(on => on.Count > 1).SelectMany(on => on)];

    // A copy as rows name it: its alias, else its name, else its id, saying which copy where another is installed
    // ("Test Twin (SmmTestTwinA)"). For every message about copies.
    public static string Named(Mod mod) => (Store.InfoFor(mod)?.Alias ?? mod.manifest?.name ?? mod.manifest?.id ?? "") + Which(mod);

    // Which copies clash, as one string: a dismissed banner stays away for this set, and shows again for another.
    public static string Signature(IEnumerable<Mod> clashing) => string.Join(",", clashing.Select(SwitchKey).Order(StringComparer.Ordinal));

    // " (Local)" or " (<Workshop item id>)" after a mod's name, where another copy of it is installed (a local copy
    // beside another local one: its folder); else nothing.
    public static string Which(Mod mod) => Others(mod).Count == 0 ? ""
        : $" ({(Compat.WorkshopId(mod) is { } item ? item.ToString()
            : mod.modSource == ModSource.ModsDirectory && Twins(mod).Count > 0 ? Shown(CopyOf(mod)) : Menus.Text("SPIRE_MOD_MANAGER.SOURCE_LOCAL"))})";

    // Brings the record in line with the copies installed, gives each id and source installed more than once a pick
    // when it has none that names one of its copies (Picked: the copy the game loaded, so nothing changes until the
    // player picks another), and tidies copies_off: a file from before it (or a first run) gets the reading it had, only
    // the pick on (LegacyOff), so nothing looks different on the update; a pick in it comes out, so a switch that's on
    // always has a copy ticked; so does a copy now alone on its switch (its twins uninstalled since), as Write drops one
    // clicked, so another copy arriving later doesn't find it off and take over. At the first main menu, before the undo
    // baseline (so it's neither an undo step nor a change since launch). True if it changed.
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
        var picks = new Picks(data.CopyPicks, data.Copies, data.CopiesOff);
        var seeded = TwinGroups().Where(g => picks.Stored(g.Id, g.Source) is not { } stored || !g.Copies.Contains(stored))
            .Select(g => Picked(g.Id, g.Source, picks) is { } copy ? $"{g.Id}|{copy}" : null).OfType<string>().ToList();
        if (seeded.Count > 0)
        {
            data.CopyPicks = WithPicks(data.CopyPicks, seeded);
            changed = true;
        }
        var alone = All.Where(m => m.manifest?.id is { } each && each != ModEntry.ModId && Twins(m).Count == 0).Select(SwitchKey).ToHashSet();
        var off = Store.TidyOff((data.CopiesOff ?? LegacyOff(data)).Where(k => !alone.Contains(k)), data.CopyPicks) ?? [];
        if (data.CopiesOff is not { } was || !off.SequenceEqual(was))
        {
            data.CopiesOff = off;
            changed = true;
        }
        return changed;
    }

    // copies_off as data from before it reads (only the pick on): every copy of each id and source installed more than
    // once but its pick, sorted.
    public static List<string> LegacyOff(StoreData data)
    {
        var picks = new Picks(data.CopyPicks, data.Copies);
        var off = new List<string>();
        foreach (var (id, source, copies) in TwinGroups())
        {
            var pick = picks.Picked(id, source);
            off.AddRange(copies.Where(c => c != pick).Select(c => $"{id}|{c}"));
        }
        return [.. off.Order(StringComparer.Ordinal)];
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

    // The copies installed this session (SwitchKey) that our record (StoreData.Copies) didn't have yet: new to us, not
    // ones uninstalled and back, which a profile has its own tick for (Profiles.TakeInInstalled).
    static readonly HashSet<string> NewToRecord = [];

    public static bool IsNewToRecord(Mod mod) => NewToRecord.Contains(SwitchKey(mod));

    // A copy installed mid-session: worked out on our data as it is now, then written as it is into every undo
    // snapshot (History.Quietly), so the new copy's entry is the same in each: no change since launch, no undo step.
    // It arrives as the game has it, ticked where its switch is on (an entry copies_off kept for it from before it was
    // uninstalled goes), and its id and source's pick stays the copy that was there (it never takes over; enabling it
    // does). Before Profiles' handler (Start runs before Profiles.Prepare), which asks IsNewToRecord.
    static void Arrived()
    {
        try
        {
            // Installed adds the copy that came last.
            var come = Installed.Mods.LastOrDefault();
            if (come?.manifest?.id is { } id && Store.Data.Copies?.GetValueOrDefault(id)?.Contains(CopyOf(come)) != true)
                NewToRecord.Add(SwitchKey(come));
            var now = Store.Clone(Store.Data);
            var changed = Reconcile(now);
            if (come != null && KeepThere(now, come))
                changed = true;
            // Not for an update of a copy installed this session (the game keeps its earlier Mod): it's ticked or not
            // as the player left it.
            var cleared = come != null && !All.Any(m => m != come && SwitchKey(m) == SwitchKey(come)) ? SwitchKey(come) : null;
            now.CopiesOff = Store.TidyOff(now.CopiesOff?.Where(k => k != cleared), now.CopyPicks);
            if (now.CopiesOff is { } off && (Store.Data.CopiesOff is not { } was || !off.SequenceEqual(was)))
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
                // The new copy ticked and no pick off, as in now (data from before copies_off as Reconcile reads it).
                data.CopiesOff = Store.TidyOff((data.CopiesOff ?? LegacyOff(data)).Where(k => k != cleared), data.CopyPicks);
            });
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.ModId}] Could not sort out a newly installed copy of a mod: {e}");
        }
    }

    // Where the pick of its id and source names the copy just installed (a pick saved before it was uninstalled: picks
    // are never pruned; or the record's order, where both came this session), the pick goes to the copy that loads
    // without it, in Picked's order (the one loaded, else the record's first): the new copy arrives ticked but not
    // picked, so the copy that was loading still does. Not for an update of a copy installed this session (the game
    // keeps its earlier Mod): a pick of that one is the player's. True if it changed.
    static bool KeepThere(StoreData data, Mod come)
    {
        if (come.manifest?.id is not { } id || id == ModEntry.ModId || All.Any(m => m != come && SwitchKey(m) == SwitchKey(come)))
            return false;
        var copy = CopyOf(come);
        if (new Picks(data.CopyPicks, data.Copies, data.CopiesOff).Picked(id, come.modSource) != copy)
            return false;
        var without = new Picks(data.CopyPicks?.Where(k => k != SwitchKey(come)),
            data.Copies?.ToDictionary(p => p.Key, p => p.Value.Where(c => c != copy).ToList()), data.CopiesOff);
        if (without.Picked(id, come.modSource) is not { } there || there == copy)
            return false;
        data.CopyPicks = WithPicks(data.CopyPicks, [$"{id}|{there}"]);
        return true;
    }
}

// Which copy loads for each id and source installed more than once (StoreData.CopyPicks), and which copies are off while
// their switch is on (StoreData.CopiesOff), read once for many asks: our data as it is now, or a snapshot of it
// (Store.PicksIn). Record: the copies record it came with (StoreData.Copies), for Copies.Picked's fallback.
internal sealed class Picks
{
    readonly Dictionary<(string Id, ModSource Source), string> _stored = [];

    public IReadOnlyDictionary<string, List<string>>? Record { get; }

    // The copies off while their switch is on (Copies.SwitchKey); null: data from before copies_off, where only the pick
    // counts as on.
    public IReadOnlySet<string>? Off { get; }

    public Picks(IEnumerable<string>? stored, IReadOnlyDictionary<string, List<string>>? record, IEnumerable<string>? off = null)
    {
        foreach (var key in stored ?? [])
            if (Copies.Parse(key) is { } pick)
                _stored.TryAdd((pick.Id, pick.Source), pick.Copy);
        Record = record;
        Off = off?.ToHashSet();
    }

    public static readonly Picks None = new(null, null);

    // Ours now; none without loading our data when no mod is installed twice from one source (most players, and Init).
    public static Picks Now => Copies.AnyTwins ? Of(Store.Data) : None;

    // Read again only when the data, its picks or its copies off were replaced (every writer puts in a new list).
    static (StoreData Data, List<string>? Stored, Dictionary<string, List<string>>? Record, List<string>? Off, Picks Picks)? _now;

    static Picks Of(StoreData data)
    {
        if (_now is { } now && now.Data == data && now.Stored == data.CopyPicks && now.Record == data.Copies && now.Off == data.CopiesOff)
            return now.Picks;
        var picks = new Picks(data.CopyPicks, data.Copies, data.CopiesOff);
        _now = (data, data.CopyPicks, data.Copies, data.CopiesOff, picks);
        return picks;
    }

    // These as a tick on this copy would leave them (Copies.LoadingIf): it's its id and source's pick, and not off.
    public Picks Ticking(Mod mod)
    {
        if (mod.manifest?.id is not { } id)
            return this;
        var key = Copies.SwitchKey(mod);
        return new Picks(_stored.Where(p => p.Key != (id, mod.modSource)).Select(p => $"{p.Key.Id}|{p.Value}").Append(key), Record,
            Off?.Where(k => k != key));
    }

    // The copy saved for an id and source, whether it's installed or not.
    public string? Stored(string id, ModSource source) => _stored.GetValueOrDefault((id, source));

    public string? Picked(string id, ModSource source) => Copies.Picked(id, source, this);
}
