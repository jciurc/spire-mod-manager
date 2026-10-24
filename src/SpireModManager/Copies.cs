using System.Globalization;
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
// off while the switch is on). Which copy loads is worked out from what's installed, the switches, the ticks, the
// versions and the saved order every time (Next), never stored, and only the ticked copies count (a copy that's off never
// stops a ticked one from loading, and loads only where no ticked copy can): the game's local-vs-Workshop rule over them,
// then of one source the highest ticked version; where the next launch can't put that copy in the id's first slot
// (something it needs that's on sorts after the slot), what the same rule loads without it (SameIdLoad.Fallback). The
// exception: a copy in a dependency cycle, or waiting on one, can make the game fail every copy of the id (its cycle
// check goes by id). Applied by SameIdLoad at launch. So a copy's box (Ticked) and whether it loads after a relaunch
// (Loads) are two things: two copies of an id ticked, only one loads.
static class Copies
{
    // Every mod the game found, and those installed this session.
    static IEnumerable<Mod> All => ModManager.Mods.Union(Installed.Mods);

    // Worked out once per copy (a Workshop id read by reflection on the beta, a folder name).
    static readonly ConditionalWeakTable<Mod, string> Names = new();

    public static string CopyOf(Mod mod) => Names.GetValue(mod, m => Compat.WorkshopId(m) is { } item ? $"w:{item}"
        : (m.modSource == ModSource.SteamWorkshop ? "ws:" : "l:")
            + (Path.GetFileName(m.path?.TrimEnd('/', '\\') ?? "") is { Length: > 0 } folder ? folder : m.modSource.ToString()));

    // The key a copy's tick is saved under (StoreData.CopiesOff): "<id>|<copy>", the same whichever copy holds the id's
    // entry (Key changes with the holder).
    public static string SwitchKey(Mod mod) => $"{mod.manifest?.id}|{CopyOf(mod)}";

    // The source a copy is from: "w:" and "ws:" copies are the Workshop's, "l:" ones local; null for a name that's neither.
    public static ModSource? SourceOf(string copy) => copy.StartsWith("w:") || copy.StartsWith("ws:") ? ModSource.SteamWorkshop
        : copy.StartsWith("l:") ? ModSource.ModsDirectory : null;

    // The id, copy and source a copies_off key (or a legacy pick) names; null for a key that names none.
    public static (string Id, string Copy, ModSource Source)? Parse(string? key)
    {
        var bar = key?.IndexOf('|') ?? -1;
        if (key == null || bar <= 0)
            return null;
        var copy = key[(bar + 1)..];
        return SourceOf(copy) is { } from && copy.Length > copy.IndexOf(':') + 1 ? (key[..bar], copy, from) : null;
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
    // which of the ticked ones loads is ours (Next).
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

    // The copies of an id from one source our record has (StoreData.Copies), installed or not, where it has two or more:
    // the group a profile's copies off and Restore's are written for, so a group with copies missing still counts. Never
    // ours: its copies of one source share one box. Null for none.
    public static List<string>? RecordGroup(string id, ModSource source, IReadOnlyDictionary<string, List<string>>? record) =>
        id != ModEntry.ModId && record?.GetValueOrDefault(id)?.Where(c => SourceOf(c) == source).ToList() is { Count: > 1 } copies ? copies : null;

    public static List<string>? RecordGroup(string id, ModSource source) => RecordGroup(id, source, Store.Data.Copies);

    // Copies the game found at launch, before those installed since: they keep the id they had.
    static HashSet<string> Launched() =>
        ModManager.Mods.Where(m => m.state != ModLoadState.AddedAtRuntime).Except(Installed.Mods).Select(CopyOf).ToHashSet();

    // How a first split orders a record, and copies new to it join (Join): a copy the last launch had first
    // (LaunchGuard.Before), then one found at this launch, then those installed since in the order they came, then by
    // age (ByAge).
    static IEnumerable<string> Ordered(string id, IEnumerable<string> copies, HashSet<string> launched)
    {
        var before = LaunchGuard.Before?.GetValueOrDefault(id);
        var came = Installed.Mods.Select(CopyOf).ToList();
        return ByAge(copies.OrderBy(c => before?.Contains(c) == true ? 0 : 1).ThenBy(c => launched.Contains(c) ? 0 : 1)
            .ThenBy(c => came.IndexOf(c) is var at && at >= 0 ? at : int.MaxValue));
    }

    // A Workshop copy before a local one, the oldest item first, then by name: fixed, so a version bump never moves it.
    static IOrderedEnumerable<string> ByAge(IOrderedEnumerable<string> copies) =>
        copies.ThenBy(c => c.StartsWith("w:") ? 0 : 1).ThenBy(c => c.StartsWith("w:") && ulong.TryParse(c[2..], out var item) ? item : ulong.MaxValue)
            .ThenBy(c => c, StringComparer.Ordinal);

    // The copies in the order a tie between them goes by (P4: "the copy installed first"): their place in our record's
    // list for the id; a copy the record doesn't have after those it has, the ones the last launch had first (before),
    // then by age, as a first split orders them. Pure: the page passes our record, the launch the one read at Init, so
    // the two agree.
    public static IEnumerable<string> TieOrder(IEnumerable<string> copies, IReadOnlyList<string>? recorded, IReadOnlySet<string>? before)
    {
        int At(string copy)
        {
            for (var i = 0; i < (recorded?.Count ?? 0); i++)
                if (recorded?[i] == copy)
                    return i;
            return int.MaxValue;
        }
        return ByAge(copies.OrderBy(At).ThenBy(c => before?.Contains(c) == true ? 0 : 1));
    }

    // These in their copies' tie order (tieOrder: TieOrder with a record), a copy's own Mods as given: where the game's
    // sort can put copies of one id either way round (they share the id's rank), the page and the launch both go by it.
    public static List<T> ByTie<T>(IEnumerable<T> items, Func<T, Mod> mod, Func<IEnumerable<string>, IEnumerable<string>> tieOrder)
    {
        var all = items.ToList();
        var order = tieOrder(all.Select(i => CopyOf(mod(i))).Distinct()).ToList();
        return [.. all.OrderBy(i => order.IndexOf(CopyOf(mod(i))))];
    }

    // A copy's version as the game reads it (SemanticVersion), for its own local-vs-Workshop rule (Next's step 2): null for
    // none, one it can't read, or one that throws (a number past int's range throws out of TryFromString).
    public static SemanticVersion? GameVersion(Mod mod) => VersionsOf(mod).Game;

    // A copy's version as our choice between copies of one source reads it (Next's step 4, P5): as the game reads it
    // where it can, so every such version is ordered as the game orders it, else leniently (Ver.Lenient). Null where no
    // number leads.
    public static Ver? Version(Mod mod) => VersionsOf(mod).Ours;

    // Both readings of a manifest's version, kept per Mod (an update mid-session adds a Mod with its new manifest), read
    // again where the version was changed in place (the self-test does).
    static readonly ConditionalWeakTable<Mod, Versions> Read = new();

    sealed record Versions(string? Text, SemanticVersion? Game, Ver? Ours);

    static Versions VersionsOf(Mod mod)
    {
        var text = mod.manifest?.version;
        if (Read.TryGetValue(mod, out var read) && read.Text == text)
            return read;
        var game = Ver.Game(text);
        var made = new Versions(text, game, game != null ? Ver.Of(game) : Ver.Lenient(text));
        Read.AddOrUpdate(mod, made);
#if DEVTOOLS
        TestReads++;
#endif
        return made;
    }

    // How Best's copy won: the highest version; the first by TieOrder among equal versions (or none readable); or the
    // only copy whose version reads.
    public enum Won { Higher, First, Readable }

    // Of the ticked copies of one source the game left to load (a Mod each), the one Spire Mod Manager loads (PENDING
    // JORDAN, P4 and P5): of those whose version reads, the highest, the first by tieOrder among equals; none reads: the
    // first by tieOrder. gameVersion, given where a mod that loads needs the id with a minimum version: a copy the game
    // can't read ranks below every one it can, or that mod would fail with the game's DEPENDENCY_VERSION_INVALID. Shared
    // by the page (Next) and the launch (SameIdLoad.Choose).
    public static (Mod Mod, Won How) Best(IReadOnlyList<Mod> candidates, Func<Mod, Ver?> version, Func<IEnumerable<string>, IEnumerable<string>> tieOrder,
        Func<Mod, SemanticVersion?>? gameVersion = null)
    {
        var pool = gameVersion != null && candidates.Any(c => gameVersion(c) != null) ? [.. candidates.Where(c => gameVersion(c) != null)] : candidates.ToList();
        var read = new List<(Mod Mod, Ver Ver)>();
        foreach (var candidate in pool)
            if (version(candidate) is { } ver)
                read.Add((candidate, ver));
        var top = pool;
        if (read.Count > 0)
        {
            var high = read.Aggregate((a, b) => b.Ver.CompareTo(a.Ver) > 0 ? b : a).Ver;
            top = [.. read.Where(p => p.Ver.CompareTo(high) == 0).Select(p => p.Mod)];
        }
        var first = tieOrder(top.Select(CopyOf)).First();
        var won = top.First(m => CopyOf(m) == first);
        var rest = candidates.Where(c => c != won).ToList();
        var how = rest.Count > 0 && rest.All(c => !pool.Contains(c) || version(c) == null) && read.Count > 0 ? Won.Readable
            : top.Count > 1 || read.Count == 0 ? Won.First : Won.Higher;
        return (won, how);
    }

    // This copy's own box: whether the player has it on, as our views show it (Vanilla View shows the game's switch): its
    // id and source's switch on, and its own tick (OwnTick). Inside a redraw, Lookups answers.
    public static bool Ticked(Mod mod) => Lookups.Current?.Ticked(mod) ?? SwitchOn(mod) && OwnTick(mod, Ticks.Now);

    // The same, read against given lists (launch's, a snapshot's, or now's).
    public static bool TickedIn(Mod mod, HashSet<(string?, ModSource)> gameOff, Ticks ticks) =>
        !gameOff.Contains((mod.manifest?.id, mod.modSource)) && OwnTick(mod, ticks);

    // Its id and source's switch as the game reads it (any entry off turns it off), without listing every entry.
    static bool SwitchOn(Mod mod) => !(SaveManager.Instance.SettingsSave.ModSettings?.IsModDisabled(mod) ?? false);

    // Its own tick, ignoring the switch (Ticks.Ticked over the other copies of its switch installed): what "Enable needed"
    // brings back where a needed id is all off (Dependencies.Preference).
    public static bool OwnTick(Mod mod, Ticks ticks) => mod.manifest?.id is not { } id || !ticks.Off.Contains(SwitchKey(mod))
        || ticks.Ticked(id, CopyOf(mod), Twins(mod).Select(CopyOf));

    // Whether this copy is the one of its id that loads after a relaunch (Loading); for an id installed once, its switch.
    // It implies the switch is on, and nearly always that it's ticked (an unticked copy loads only as the game's own
    // choice, in Next: for an id the launch couldn't choose for, or where no ticked copy can take a blocked one's place).
    // Inside a redraw, Lookups answers.
    public static bool Loads(Mod mod) => Lookups.Current?.Loads(mod)
        ?? (Others(mod).Count == 0 ? SwitchOn(mod) : LoadsIn(mod, Dependencies.SavedOff(), Ticks.Now));

    // The same, read against given lists.
    public static bool LoadsIn(Mod mod, HashSet<(string?, ModSource)> gameOff, Ticks ticks) =>
        LoadsWith(mod, gameOff, id => LoadingIn(id, gameOff, ticks));

    // The same, given how to find the copy of an id that loads (Lookups keeps each id's for the redraw).
    public static bool LoadsWith(Mod mod, HashSet<(string?, ModSource)> gameOff, Func<string, Mod?> loading) =>
        mod.manifest?.id is { } id && Others(mod).Count > 0 ? loading(id) is { } loads && CopyOf(loads) == CopyOf(mod)
            : !gameOff.Contains((mod.manifest?.id, mod.modSource));

    // The copy of an id that loads after a relaunch, or null when none does. Inside a redraw, Lookups answers.
    public static Mod? Loading(string id) => Next(id).Loads;

    // The same, read against given lists.
    public static Mod? LoadingIn(string id, HashSet<(string?, ModSource)> gameOff, Ticks ticks) => Next(id, gameOff, ticks).Loads;

    // Why a ticked copy doesn't load (Next's step 5): the game took it out for a local copy that loads (Local: stable, or
    // a tie or an unknown version on the beta); it lost to a higher version that loads (Higher: the game's rule between
    // sources, or ours within one); within one source, to the copy that loads on a tie, or with no version read (First);
    // its version can't be read and the loading copy's can (Unreadable); the next launch can't swap it in (Blocked:
    // SameIdLoad.Fallback's pick loads in its place, none: the first copy left that fits, else the first); or none of
    // these says it (Other: the copy it lost to isn't the one that loads, the copy that loads isn't ticked, the other
    // source of two local copies and a newer Workshop one, a copy of this mod from the same source, a ticked copy the
    // fallback passed over, or an id the launch couldn't choose for).
    public enum Why { Local, Higher, First, Unreadable, Blocked, Other }

    // What the next launch loads of an id (Next): Loads, or null for none; Losing: every ticked copy that won't load
    // (CopyOf), and why; Late: for the copy Blocked, what it needs that the next launch doesn't load before the slot.
    public sealed record Outcome(Mod? Loads, IReadOnlyDictionary<string, Why> Losing, string? Late = null)
    {
        public static readonly Outcome None = new(null, new Dictionary<string, Why>());
    }

    // The one rule every view reads, worked out each time from what's installed, the switches, the ticks and the
    // versions (nothing about which copy loads is stored). Inside a redraw, Lookups answers.
    public static Outcome Next(string id) => Lookups.Current?.Next(id) ?? Next(id, Dependencies.SavedOff(), Ticks.Now);

    // The same, read against given lists, as the next launch goes:
    // 1. the installed copies of the id, one Mod per copy (its first; versions from its newest manifest, NewestOf, as the
    //    launch reads them), in the game's list order; none whose switch is on: nothing loads;
    // 2. the game's rule, on switches, not ticks (it can't see copies_off), with its own reading of versions
    //    (ModManager.RemoveDisabledMods): stable takes out every Workshop copy beside a local one; the beta compares each
    //    Workshop copy with the first local copy it read (FirstRead), taking out the local one where the Workshop version
    //    is higher, else the Workshop one (a tie, or an unknown version);
    // 3. this mod: the game's own choice, the copy that loaded this session if the rule leaves it, else the first left; an
    //    id the launch couldn't choose for (SameIdLoad.Unchosen): the game's own choice, the first of its copies in the
    //    next launch's order (Dependencies.NextOrder);
    // 4. else of the ticked copies left, those of the first one's source (nothing is chosen across sources), and of
    //    those Best; with a copy on that isn't ticked (on through a ticked twin's switch), step 2 again over the ticked
    //    copies alone first (the launch brings back the copy it picks); none ticked at all: the game's own choice, the
    //    first left. Where the next launch can't put that copy in the id's first slot (Blocked: something it needs that's
    //    on sorts at or after the slot in that launch's order, Slot) while a mod that launch has still to load needs the
    //    id (Needers), SameIdLoad.Fallback's pick of the other ticked copies, none: the first of the id's other copies in
    //    that order, by the tie order, that fits, else the first; not where the launch doesn't look (one Mod of the id
    //    left by the game's rule, the copy chosen);
    // 5. every ticked copy that doesn't load, and why (Why).
    // saved: the list the next launch sorts by (Dependencies.NextOrder), else the game's in memory.
    public static Outcome Next(string id, HashSet<(string?, ModSource)> gameOff, Ticks ticks, IReadOnlyList<SettingsSaveMod>? saved = null)
    {
        var copies = (ById().TryGetValue(id, out var all) ? all : All.Where(m => m.manifest?.id == id)).DistinctBy(CopyOf).ToList();
        // In the order the next launch has them: this launch's before Apply swapped any (its sort orders by id, so a swap
        // doesn't carry over); a copy installed since after them.
        if (SameIdLoad.Plan.GetValueOrDefault(id)?.Order is { Count: > 0 } order)
            copies = [.. copies.OrderBy(m => order.TakeWhile(c => c != CopyOf(m)).Count())]; // one not in it: last
        var on = copies.Where(m => !gameOff.Contains((id, m.modSource))).ToList();
        if (on.Count == 0)
            return Outcome.None;
        if (copies.Count == 1)
            return new Outcome(on[0], Outcome.None.Losing);
        // 2. Each copy the game takes out, with the copy it lost to and why.
        var beaten = GameRule(on, m => GameVersion(Newest(m)));
        var gameLeft = on.Where(m => !beaten.ContainsKey(m)).ToList();
        var left = gameLeft;
        bool IsTicked(Mod m) => OwnTick(m, ticks);
        var list = saved ?? SaveManager.Instance.SettingsSave.ModSettings?.ModList ?? [];
        List<Mod>? next = null;
        List<Mod> Order() => next ??= NextOrderOf(gameOff, list);
        Mod loads;
        Mod? blocked = null;
        string? late = null;
        List<Mod> candidates = [];
        var strict = false;
        if (id == ModEntry.ModId)
            loads = on.FirstOrDefault(m => m.state == ModLoadState.Loaded && gameLeft.Contains(m)) ?? gameLeft[0];
        else if (SameIdLoad.Unchosen(id))
            loads = Order().FirstOrDefault(m => m.manifest?.id == id) is { } first && on.FirstOrDefault(m => CopyOf(m) == CopyOf(first)) is { } copy ? copy : gameLeft[0];
        else
        {
            // A copy that's off takes part in the rule while its switch is on through a ticked twin's: on the beta it can
            // beat a ticked copy (W2 off at 3.0 beats L at 2.0, which beats W1 at 1.0, so only W2 is left), or decide which
            // ticked copy is left. So with any such copy, the rule again over the ticked copies alone, as the launch has it
            // (SameIdLoad.Run brings back the copy it picks): a copy that's off never loads nor stops another from loading
            // (Jordan, 23 Oct). On stable it changes nothing (every Workshop copy beside a local one is out either way).
            if (on.Where(IsTicked).ToList() is { Count: > 0 } tickedOn && tickedOn.Count < on.Count)
            {
                beaten = GameRule(tickedOn, m => GameVersion(Newest(m)));
                // In the launch's order (SameIdLoad.Run): the copies the game's rule leaves first (still to load), then
                // those it beat (its sort put them after every mod still to load), as the first one's source decides.
                left = [.. tickedOn.Where(m => !beaten.ContainsKey(m)).OrderBy(m => gameLeft.Contains(m) ? 0 : 1)];
            }
            if (left.Where(IsTicked).ToList() is not [var firstTicked, ..] ticked)
                loads = left[0];
            else
            {
                candidates = [.. ticked.Where(m => m.modSource == firstTicked.modSource)];
                IEnumerable<string> TieOrdered(IEnumerable<string> tied) => TieOrder(tied, ticks.TieList(id), ticks.BeforeOf(id));
                // The mods the next launch has still to load that need the id, worked out only where asked: with two or
                // more candidates (one needing a minimum version: Best ranks a copy the game can't read below one it can),
                // or where the copy chosen needs something.
                List<Mod>? needers = null;
                List<Mod> Needing() => needers ??= Needers(id, gameOff, ticks, list, Order);
                strict = candidates.Count > 1 && Needing().Any(m => NeedsMin(m, id));
                var best = Best(candidates, m => Version(Newest(m)), TieOrdered, strict ? m => GameVersion(Newest(m)) : null).Mod;
                loads = best;
                // Blocked: what the next launch loads instead, worked out as it does (SameIdLoad.Fallback) over the ticked
                // copies in its order (those the game's rule leaves, then those it beats), by the ticks now; none fits: of
                // the id's other copies in that launch's order, by the tie order, the first that fits, else the first.
                // Only where the launch looks (Run's todo): two or more Mods of the id left by the game's rule (a copy with
                // manifests in two folders counts twice), or the copy chosen one it set aside.
                var looks = gameLeft.Count > 1 || !gameLeft.Contains(best);
                if (Dependencies.Read(Newest(best)).Count > 0 && (looks || (all ?? []).Where(m => CopyOf(m) == CopyOf(best)).Select(m => m.path).Distinct().Count() > 1)
                    && Needing().Count > 0 && SlotIn(id, Order(), Mins(id, Needing())) is var slot && (looks || slot.Order.Count > 1) && slot.Late(Newest(best)) is { } dep)
                {
                    var tickedNow = on.Where(IsTicked).Select(Newest).ToList();
                    var rest = ByTie(slot.Order.Where(m => CopyOf(m) != CopyOf(best)), m => m, TieOrdered);
                    if ((SameIdLoad.Fallback(tickedNow, Newest(best), m => gameLeft.Any(g => CopyOf(g) == CopyOf(m)), GameVersion, slot.Fits, TieOrdered,
                        Needing().Any(m => NeedsMin(m, id))) ?? rest.Where(slot.Fits).Concat(rest).FirstOrDefault())
                        is { } instead && on.FirstOrDefault(m => CopyOf(m) == CopyOf(instead)) is { } same)
                        (blocked, late, loads) = (best, dep, same);
                }
            }
        }
        // 5. Why each ticked copy that doesn't load doesn't.
        var losing = new Dictionary<string, Why>();
        foreach (var copy in on.Where(IsTicked).Where(m => CopyOf(m) != CopyOf(loads)))
            losing[CopyOf(copy)] = beaten.TryGetValue(copy, out var lost) ? GameReason(copy, lost.By, lost.Why, loads)
                : copy == blocked ? Why.Blocked
                : blocked == null && candidates.Contains(copy) && candidates.Contains(loads) ? SameSource(copy, loads, strict)
                : Why.Other;
        return new Outcome(loads, losing, late);
    }

    // The game's local-vs-Workshop rule (ModManager.RemoveDisabledMods) over copies of one id whose switches are on: each
    // copy it takes out, with the copy it lost to and why. Stable takes out every Workshop copy beside a local one; the
    // beta compares each Workshop copy with the first local copy it read (FirstRead), taking out the local one where the
    // Workshop version is higher, else the Workshop one (a tie, or an unknown version). version: as the game reads it.
    public static Dictionary<Mod, (Mod By, Why Why)> GameRule(IReadOnlyList<Mod> on, Func<Mod, SemanticVersion?> version)
    {
        var beaten = new Dictionary<Mod, (Mod By, Why Why)>();
        var locals = on.Where(m => m.modSource == ModSource.ModsDirectory).ToList();
        if (locals.Count == 0 || !on.Any(m => m.modSource == ModSource.SteamWorkshop))
            return beaten;
        var first = FirstRead(locals);
        var local = version(first);
        foreach (var workshop in on.Where(m => m.modSource == ModSource.SteamWorkshop))
        {
            var by = Compat.WorkshopBeatsOlderLocal && local != null && version(workshop) is { } higher ? higher.CompareTo(local) : 0;
            if (by > 0)
                beaten.TryAdd(first, (workshop, Why.Higher));
            else
                beaten[workshop] = (first, by < 0 ? Why.Higher : Why.Local);
        }
        return beaten;
    }

    // Why a copy the game's rule took out (step 2) doesn't load: a Workshop copy beside a local copy that loads (Local, or
    // Higher where that's the local copy it lost to as the higher version); a local copy beside a Workshop copy that
    // loads (every Workshop copy left beat it: Higher); else Other.
    static Why GameReason(Mod copy, Mod by, Why why, Mod loads) =>
        copy.modSource == ModSource.SteamWorkshop
            ? loads.modSource != ModSource.ModsDirectory ? Why.Other : why == Why.Higher ? (CopyOf(by) == CopyOf(loads) ? Why.Higher : Why.Other) : Why.Local
            : loads.modSource == ModSource.SteamWorkshop ? Why.Higher : Why.Other;

    // Why a ticked copy of one source lost to the one that loads there (Best): its version can't be read (or not by the
    // game, where a mod that loads needs the id with a minimum version) and the loading copy's can; a lower version; else
    // a tie (equal versions, or none read).
    static Why SameSource(Mod copy, Mod loads, bool strict)
    {
        if (strict && GameVersion(Newest(loads)) != null && GameVersion(Newest(copy)) == null)
            return Why.Unreadable;
        var (lost, won) = (Version(Newest(copy)), Version(Newest(loads)));
        return lost == null ? (won == null ? Why.First : Why.Unreadable) : won != null && lost.CompareTo(won) < 0 ? Why.Higher : Why.First;
    }

    // The mods that need the id that the next launch has still to load when it comes to the id, as SameIdLoad.Run asks
    // (one that needs it with a minimum version: Best ranks a copy the game can't read below one it can, and a copy below
    // it can't take a blocked one's place; any: a copy that can't take the id's first slot doesn't load): switch on, not
    // taken out by the game's rule, the newest manifest at each path (as Dependencies.NextOrder keeps them); of an id
    // installed more than once, its Mods in that launch's order where its first slot there comes after this one's, else
    // only the copy that loads (Run has set the others aside by then, and brought that one back where the game's rule set
    // it aside). The order is worked out only where such a mod is installed more than once.
    static List<Mod> Needers(string id, HashSet<(string?, ModSource)> gameOff, Ticks ticks, IReadOnlyList<SettingsSaveMod> saved, Func<List<Mod>> order)
    {
        bool Needs(Mod m) => m.manifest?.id != id && m.manifest?.dependencies?.Any(d => d.id == id) == true;
        var on = Dependencies.NeededBy(id).Where(m => Needs(m) && !gameOff.Contains((m.manifest?.id, m.modSource))).ToList();
        if (!on.Any(m => Others(m).Count > 0))
        {
            var paths = on.Select(m => (m.manifest?.id, m.path)).ToHashSet();
            var newest = All.Where(m => paths.Contains((m.manifest?.id, m.path))).GroupBy(m => (m.manifest?.id, m.path)).Select(g => g.Last()).ToHashSet();
            return [.. on.Where(newest.Contains)];
        }
        var next = order();
        int First(string? of) => next.FindIndex(m => m.manifest?.id == of);
        var slot = First(id);
        List<Mod> needers = [.. next.Where(m => Needs(m) && (Others(m).Count == 0 || First(m.manifest?.id) > slot))];
        foreach (var other in on.Where(m => Others(m).Count > 0).Select(m => m.manifest?.id).OfType<string>().Distinct())
            if (First(other) is var at && at >= 0 && at < slot && NextIn(other, gameOff, ticks, saved).Loads is { } loads && Newest(loads) is var copy && Needs(copy))
                needers.Add(copy);
        return needers;
    }

    // Whether a mod needs the id with a minimum version.
    static bool NeedsMin(Mod mod, string id) => mod.manifest?.dependencies?.Any(d => d.id == id && d.minVersion != null) == true;

    // The minimum versions of the id these mods need (of other ids), as the game reads them: one it can't read fails the
    // mod whatever copy loads, so it counts for none.
    public static List<SemanticVersion> Mins(string id, IEnumerable<Mod> mods) => [.. mods.Where(m => m.manifest?.id != id)
        .SelectMany(m => m.manifest?.dependencies ?? []).Where(d => d.id == id && d.minVersion != null).Select(d => Ver.Game(d.minVersion)).OfType<SemanticVersion>()];

    // Whether a copy meets them, by its version as the game reads it (one it can't read meets none: the mod would fail).
    public static bool Meets(Mod mod, IReadOnlyList<SemanticVersion> mins) => mins.Count == 0 || GameVersion(mod) is { } version && mins.All(min => version.CompareTo(min) >= 0);

    // Next with these lists, from the redraw's answers where they're its own.
    static Outcome NextIn(string id, HashSet<(string?, ModSource)> gameOff, Ticks ticks, IReadOnlyList<SettingsSaveMod> saved) =>
        Lookups.Current is { } now && now.Off == gameOff && now.Ticks == ticks && now.Saved == saved ? now.Next(id) : Next(id, gameOff, ticks, saved);

    // The next launch's order (Dependencies.NextOrder), once per redraw for each set of switches (Lookups).
    static List<Mod> NextOrderOf(HashSet<(string?, ModSource)> gameOff, IReadOnlyList<SettingsSaveMod> saved) =>
        Lookups.Current?.NextOrder(gameOff, saved) ?? Dependencies.NextOrder(gameOff, saved);

    // The id's first slot in the next launch's order (Dependencies.NextOrder: the game ranks an id by its last saved
    // entry, so a launch that blocked a copy ranks the id after what the copy needs, which then usually sorts first): the
    // ids sorted before it, every id the order has (switch on, not taken out by the game's rule), and the id's own Mods
    // there, in its order; and the minimum versions of the id the mods that launch has still to load need (Mins). The
    // limits are in DESIGN.md (a session that ends before the main menu, ties, cycles).
    sealed record Slot(HashSet<string?> Before, HashSet<string?> Sorted, List<Mod> Order, IReadOnlyList<SemanticVersion> Mins)
    {
        // What stops the copy chosen taking the slot, as at the launch (SameIdLoad.Run's Late): something it needs that's
        // on but sorts at or after it. One that's off or isn't installed doesn't: the copy loads and fails, as a mod
        // installed once does, and the dependency warnings say so. Null for none.
        public string? Late(Mod m) => Dependencies.Read(m).FirstOrDefault(dep => Sorted.Contains(dep) && !Before.Contains(dep));

        // Whether another copy can take it instead (Run's Fits): everything it needs installed, on and sorted before it,
        // and the minimum versions met.
        public bool Fits(Mod m) => Dependencies.Read(m).All(dep => Before.Contains(dep)) && Meets(m, Mins);
    }

    static Slot SlotIn(string id, List<Mod> order, IReadOnlyList<SemanticVersion> mins)
    {
        var at = order.FindIndex(m => m.manifest?.id == id);
        return new([.. order.Take(at < 0 ? order.Count : at).Select(m => m.manifest?.id)], [.. order.Select(m => m.manifest?.id)],
            [.. order.Where(m => m.manifest?.id == id)], mins);
    }

    // The local copy the game reads first, which its local-vs-Workshop rule compares Workshop copies with: it reads its
    // mods folder depth first, a folder's files before its subfolders, each list in Godot's DirAccess order (ordinal, so
    // capitals before small letters: the self-test pins it). The page can't see that order in the game's list (its sort
    // moved it), so it's rebuilt from each copy's folder, name by name, a folder before what's inside it.
    public static Mod FirstRead(IReadOnlyList<Mod> locals) => locals.OrderBy(m => m.path ?? "", ReadOrder).First();

    // Mods in the order the game reads them (ModManager.Init), by which a mod waits for the first copy of each id it needs
    // (SortModList): its mods folder, as FirstRead goes; then the Workshop's, a copy with no item (its mods_STEAMTEST
    // folder) first, then by item in the order Steam listed them at Init (ReadItems: the game asked Steam the same moments
    // before, and its own list is sorted by then), one Steam didn't list (subscribed since) after those, by its id; each
    // item's folders as FirstRead goes.
    public static IEnumerable<Mod> InReadOrder(IEnumerable<Mod> mods) => mods.OrderBy(m => m.modSource == ModSource.ModsDirectory ? 0 : 1)
        .ThenBy(m => m.modSource != ModSource.SteamWorkshop || Compat.WorkshopId(m) is not { } item ? (-1, 0ul)
            : (_items is { } items && items.IndexOf(item) is var at && at >= 0 ? at : int.MaxValue, item))
        .ThenBy(m => m.path ?? "", ReadOrder);

    static List<ulong>? _items;

    // At Init: the Workshop items as Steam lists them (null without Steam), for InReadOrder.
    public static void ReadItems() => _items = Subscriptions.Listed();

    static readonly Comparer<string> ReadOrder = Comparer<string>.Create((a, b) =>
    {
        var (x, y) = (Folders(a), Folders(b));
        for (var i = 0; i < Math.Min(x.Length, y.Length); i++)
            if (string.CompareOrdinal(x[i], y[i]) is var by && by != 0)
                return by;
        return x.Length.CompareTo(y.Length);
    });

    static string[] Folders(string path) => path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);

    // A copy's newest manifest (NewestOf) of its Mods in the game's list (the copy is still told by its first, CopyOf).
    static Mod Newest(Mod copy) => copy.manifest?.id is { } id && ById().TryGetValue(id, out var all)
        && all.Where(m => CopyOf(m) == CopyOf(copy)).ToList() is { Count: > 0 } mods ? NewestOf(mods) : copy;

    // Of one copy's Mods, the one its versions are read from, and so the one that loads of it: the last read while the
    // game runs (an update adds a Mod with the new manifest after the one found at launch, which keeps the old version,
    // and the next launch reads the new one); else, for a copy with two manifests of the id at launch (two in one folder,
    // or a Workshop item with one in two of its folders), the highest version, one the game can read first among equals.
    // Not by their order in the list: the game's sort can put them either way round, and the launch moves the one that
    // loads to the id's first slot. Shared by the page (Next) and the launch (SameIdLoad.Choose), so both read a copy the
    // same.
    public static Mod NewestOf(IReadOnlyList<Mod> mods)
    {
        if (mods.LastOrDefault(m => m.state == ModLoadState.AddedAtRuntime) is { } updated)
            return updated;
        var newest = mods[0];
        foreach (var mod in mods.Skip(1))
            if (Newer(mod, newest))
                newest = mod;
        return newest;
    }

    // Whether a manifest's version reads higher than another's, or the same and the game can read it where it can't read
    // the other's.
    static bool Newer(Mod mod, Mod than)
    {
        var (a, b) = (Version(mod), Version(than));
        if (a == null || b == null)
            return a != null;
        var by = a.CompareTo(b);
        return by > 0 || by == 0 && GameVersion(mod) != null && GameVersion(than) == null;
    }

    // For a ticked copy that won't load after a relaunch while another copy of its id will: that copy, and why (Next).
    // Null for any other.
    public static (Mod Loads, Why Why)? LosesTo(Mod mod) => mod.manifest?.id is { } id && Others(mod).Count > 0 && Next(id) is { Loads: { } loads } next
        && next.Losing.TryGetValue(CopyOf(mod), out var why) ? (loads, why) : null;

    // Whether this copy is ticked but won't load, another copy of its id loading instead: the one "won't load (same ID)".
    public static bool Loses(Mod mod) => LosesTo(mod) != null;

    // The copy of its id that would load if this copy were ticked too, as a click on its box does (Dependencies.Write: its
    // switch on, and where that was off, this copy on alone): for the tip on a copy that's off.
    public static Mod? LoadingIf(Mod mod)
    {
        if (mod.manifest?.id is not { } id)
            return null;
        var (off, ticks, saved) = Lookups.Current is { } now ? (now.Off, now.Ticks, now.Saved) : (Dependencies.SavedOff(), Ticks.Now, (IReadOnlyList<SettingsSaveMod>?)null);
        return Next(id, off.Where(o => o != (id, mod.modSource)).ToHashSet(), ticks.Ticking(mod, off.Contains((id, mod.modSource))), saved).Loads;
    }

    // Every installed copy of this mod unticked (one copy or more): the next launch runs without it, so the red banner
    // says so (LoadOrder) and Relaunch asks first (RelaunchCheck).
    public static bool OursOff() => Dependencies.EachCopy(All).Where(m => m.manifest?.id == ModEntry.ModId).ToList() is { Count: > 0 } ours && !ours.Any(Ticked);

    // The copy of this mod that runs (the one the game loaded), else its first installed: what "Enable now" turns on, so
    // the next launch runs the copy running now, whatever the game's rule would make of every source.
    public static Mod? Running() => All.FirstOrDefault(m => m.manifest?.id == ModEntry.ModId && m.state == ModLoadState.Loaded)
        ?? All.FirstOrDefault(m => m.manifest?.id == ModEntry.ModId);

    // Whether this copy is ticked while another copy of its id is too, so one of them won't load: the yellow chip. This
    // mod's copies too, like any mod's (Jordan, 23 Oct).
    public static bool Clashes(Mod mod) => Ticked(mod) && Others(mod).Any(Ticked);

    // The ticked copies where two or more copies of one id are ticked (only one of them loads), copies sharing a switch
    // too: the same-id banner's mods, in list order, this mod's among them.
    public static List<Mod> Clashing() => [.. ById().Select(p => p.Value.Where(Ticked).DistinctBy(CopyOf).ToList())
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

    // Brings the record in line with the copies installed and tidies copies_off. First a file from before copies_off (a
    // 0.1.4 one, or a first run) gets the reading Init made of it, from its picks and the record as read (LegacyOff), so
    // nothing looks different on the update; then copy_picks goes, never written again. Each copy new to the record joins
    // it (Join). The holder gone: its data moves to its own key, the next installed copy's to the plain id. At the first
    // main menu, before the undo baseline (so it's neither an undo step nor a change since launch). True if it changed.
    public static bool Reconcile(StoreData data)
    {
        var changed = false;
        if (data.CopiesOff == null || data.CopyPicks != null)
        {
            data.CopiesOff ??= LegacyOff(data.CopyPicks, data.Copies);
            data.CopyPicks = null;
            changed = true;
        }
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
                foreach (var copy in Ordered(id, installed, launched))
                    Join(data, id, copy, installed);
                foreach (var copy in data.Copies[id].Skip(1))
                    Seed(data, id, copy);
                changed = true;
                continue;
            }
            foreach (var copy in Ordered(id, installed.Where(c => !copies.Contains(c)), launched).ToList())
            {
                Join(data, id, copy, installed);
                Seed(data, id, copy);
                changed = true;
            }
            copies = data.Copies[id];
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
        var off = Store.TidyOff(data.CopiesOff) ?? [];
        if (data.CopiesOff is not { } was || !off.SequenceEqual(was))
        {
            data.CopiesOff = off;
            changed = true;
        }
        return changed;
    }

    // A copy new to our record joins it (Reconcile, at the first menu and as a copy arrives mid-session):
    // 1. its own key leaves copies_off (a stale one, from a hand edit: a copy new to the record reads ticked, whatever its
    //    key says);
    // 2. where the copies of its switch that the record knows and that are installed are all in copies_off (the read rule
    //    has them all ticked: Ticks.Ticked), they leave it, so the new copy can't end their implied ticks and a lower one
    //    never takes over. The same for each profile entry of its id and source that has them all off (Mods and Edited),
    //    so a profile switched to or discarded later doesn't bring the takeover back; a 0.1.4 entry, with none off yet,
    //    gets the same when it's filled in (Profiles.HoldCopies);
    // 3. it joins the record, last (TieOrder ranks it after the copies the record had).
    static void Join(StoreData data, string id, string copy, IReadOnlyCollection<string> installed)
    {
        var source = SourceOf(copy);
        var known = data.Copies?.GetValueOrDefault(id) ?? [];
        var lit = installed.Where(c => c != copy && SourceOf(c) == source && known.Contains(c)).Distinct().ToList();
        var off = (data.CopiesOff ?? []).ToHashSet();
        off.Remove($"{id}|{copy}");
        if (lit.Count > 0 && lit.All(c => off.Contains($"{id}|{c}")))
            off.ExceptWith(lit.Select(c => $"{id}|{c}"));
        data.CopiesOff = [.. off.Order(StringComparer.Ordinal)];
        if (lit.Count > 0 && source is { } from)
            foreach (var profile in data.Profiles)
                foreach (var list in new[] { profile.Mods, profile.Edited })
                    for (var i = 0; i < (list?.Count ?? 0); i++)
                        if (list?[i] is { CopiesOff: { } held } entry && entry.Id == id && entry.Source == from && lit.All(held.Contains))
                            list[i] = entry with { CopiesOff = [.. held.Except(lit)] };
        (data.Copies ??= [])[id] = [.. known, copy];
    }

    // copies_off as a file from before it reads (0.1.4, where only the pick counts as on), from its data alone: for each
    // pick (the first of each id and source) our record has, every other copy of that id and source in the record goes
    // off, installed or not. So with the pick missing, the read rule ticks the others until it's back, and it then loads
    // alone again. A group with no pick, or a pick the record doesn't have, puts nothing off. Sorted. Init, Reconcile and
    // Restore's PatchOff read a legacy file through this, as HoldCopies reads a 0.1.4 profile.
    public static List<string> LegacyOff(IEnumerable<string>? picks, IReadOnlyDictionary<string, List<string>>? record)
    {
        var off = new List<string>();
        var seen = new HashSet<(string, ModSource)>();
        foreach (var key in picks ?? [])
            if (Parse(key) is { } pick && seen.Add((pick.Id, pick.Source)) && pick.Id != ModEntry.ModId
                && record?.GetValueOrDefault(pick.Id) is { } copies && copies.Contains(pick.Copy))
                off.AddRange(copies.Where(c => c != pick.Copy && SourceOf(c) == pick.Source).Select(c => $"{pick.Id}|{c}"));
        return [.. off.Distinct().Order(StringComparer.Ordinal)];
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
            var had = Record(Store.Data);
            if (Reconcile(Store.Data))
                Store.Save();
            NoteJoined(had, Store.Data);
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

    // The copies (SwitchKey) that joined our record this session, at the first menu or since: a 0.1.4 profile's copies off
    // leave them out (Profiles.HoldCopies), as they read ticked in our data.
    static readonly HashSet<string> Joined = [];

    public static bool JoinedThisSession(string id, string copy) => Joined.Contains($"{id}|{copy}");

    static Dictionary<string, HashSet<string>> Record(StoreData data) => data.Copies?.ToDictionary(p => p.Key, p => p.Value.ToHashSet()) ?? [];

    static void NoteJoined(Dictionary<string, HashSet<string>> had, StoreData data)
    {
        foreach (var (id, copies) in data.Copies ?? [])
            foreach (var copy in copies.Where(c => had.GetValueOrDefault(id)?.Contains(c) != true))
                Joined.Add($"{id}|{copy}");
    }

    // A copy installed mid-session, worked out on a copy of our data (Reconcile, whose join is the one wanted), then
    // written into our data and every undo snapshot (History.Quietly): the record as Reconcile made it, the new copy's
    // seeded entry, and the join repeated on each by its own copies_off and profiles, so the new copy's tick is the same
    // in each: no change since launch, no undo step. It arrives as the game has it, ticked where its switch is on, and
    // the copies the record knows keep their ticks (Join); a copy the record knows (uninstalled and back, or missing at
    // launch) comes back with its stored tick, as it would with the game closed, so nothing changes for it. Which copy
    // loads is then the rule's, a new one by version too. Before Profiles' handler (Start runs before Profiles.Prepare),
    // which asks IsNewToRecord.
    static void Arrived()
    {
        try
        {
            // Installed adds the copy that came last.
            var come = Installed.Mods.LastOrDefault();
            var known = come?.manifest?.id is { } id && Store.Data.Copies?.GetValueOrDefault(id)?.Contains(CopyOf(come)) == true;
            if (come?.manifest?.id != null && !known)
                NewToRecord.Add(SwitchKey(come));
            var now = Store.Clone(Store.Data);
            if (!Reconcile(now) || now.Copies is not { } record)
                return;
            var had = Record(Store.Data);
            var added = now.Mods.Where(p => !Store.Data.Mods.ContainsKey(p.Key)).Select(p => (p.Key, p.Value)).ToList();
            var installed = All.Where(m => m.manifest?.id != null).GroupBy(m => m.manifest?.id ?? "").ToDictionary(g => g.Key, g => g.Select(CopyOf).Distinct().ToList());
            History.Quietly(data =>
            {
                data.CopiesOff ??= LegacyOff(data.CopyPicks, data.Copies);
                data.CopyPicks = null;
                foreach (var (id, copies) in record)
                {
                    var was = data.Copies?.GetValueOrDefault(id) ?? [];
                    foreach (var copy in copies.Where(c => !was.Contains(c)))
                        Join(data, id, copy, installed.GetValueOrDefault(id) ?? []);
                }
                data.Copies = record.ToDictionary(p => p.Key, p => p.Value.ToList()); // Reconcile's order, the holder moved too
                foreach (var (key, info) in added)
                    data.Mods[key] = Clone(info);
                data.CopiesOff = Store.TidyOff(data.CopiesOff);
            });
            NoteJoined(had, Store.Data);
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.ModId}] Could not sort out a newly installed copy of a mod: {e}");
        }
    }

#if DEVTOOLS
    // Self-test: the copies worked out again after made-up mods were put in the game's list or taken out (as many of
    // each leave the count the table is kept by as it was).
    public static void TestRecount() => _byId = null;

    // Self-test: how many times a manifest's version was read (each Mod's is read once, then kept).
    public static int TestReads { get; private set; }

    // Self-test: the Workshop items as if Steam had listed them so at Init (null: none, as without Steam); the ones before.
    public static List<ulong>? TestItems(List<ulong>? items)
    {
        var was = _items;
        _items = items;
        return was;
    }

    // Self-test: a copy as if it joined our record this session (Joined), or not.
    public static void TestJoined(string id, string copy, bool joined)
    {
        if (joined)
            Joined.Add($"{id}|{copy}");
        else
            Joined.Remove($"{id}|{copy}");
    }
#endif
}

// Which copies are ticked where copies share a switch: those off while their switch is on (StoreData.CopiesOff, keys
// "<id>|<copy>"), read with our record of each id's copies (StoreData.Copies) and the copies each id had at the last
// launch (LaunchGuard.Before), for ties. Our data as it is now, a snapshot of it (Store.TicksIn), a profile's (Of), or
// our file at Init (SameIdLoad).
internal sealed class Ticks
{
    public IReadOnlySet<string> Off { get; }

    public IReadOnlyDictionary<string, List<string>>? Record { get; }

    readonly IReadOnlyDictionary<string, HashSet<string>>? _before;
    readonly IReadOnlyDictionary<string, List<string>>? _tieRecord;

    // tieRecord: the record ties go by where it isn't this one (a session that can't save: the file's, which the next
    // launch reads).
    public Ticks(IEnumerable<string>? off, IReadOnlyDictionary<string, List<string>>? record, IReadOnlyDictionary<string, HashSet<string>>? before = null,
        IReadOnlyDictionary<string, List<string>>? tieRecord = null)
    {
        Off = off?.ToHashSet() ?? [];
        Record = record;
        _before = before;
        _tieRecord = tieRecord;
    }

    // Nothing off: every copy ticked by its switch.
    public static readonly Ticks All = new(null, null);

    // Ours now; none without loading our data when no mod is installed twice from one source (most players, and Init).
    public static Ticks Now => Copies.AnyTwins ? Of(Store.Data) : All;

    // Read again only when the data, its copies off or its record were replaced (every writer puts in a new list).
    static (StoreData Data, List<string>? Off, List<string>? Picks, Dictionary<string, List<string>>? Record, bool ReadOnly, Ticks Ticks)? _now;

    static Ticks Of(StoreData data)
    {
        if (_now is { } now && now.Data == data && now.Off == data.CopiesOff && now.Picks == data.CopyPicks && now.Record == data.Copies && now.ReadOnly == Store.ReadOnly)
            return now.Ticks;
        // A session that can't save our data: the next launch reads ties by the file's record (a holder moved here isn't
        // saved).
        var ticks = new Ticks(data.CopiesOff ?? Copies.LegacyOff(data.CopyPicks, data.Copies), data.Copies, LaunchGuard.Before,
            Store.ReadOnly ? SameIdLoad.InitTicks?.Record : null);
        _now = (data, data.CopiesOff, data.CopyPicks, data.Copies, Store.ReadOnly, ticks);
        return ticks;
    }

    // A profile's entries as ticks (their CopiesOff), with our record: what loads as the profile has it (Profiles.ShareRows).
    public static Ticks Of(IEnumerable<ProfileMod> entries) =>
        new(entries.SelectMany(e => (e.CopiesOff ?? []).Select(c => $"{e.Id}|{c}")), Store.Data.Copies, LaunchGuard.Before);

    // Whether a copy of an id is ticked with its switch on, given the other copies of its switch installed: unless its key
    // is off, our record knows it, and some other installed copy of its switch the record knows isn't off. So an off key
    // counts only for a known copy (one new to the record reads ticked, whatever a stale key says), and only while an
    // installed known copy of its switch is ticked by its own key: where every one of them is off (the ticked ones are
    // missing), they all read ticked, the read rule, so what's installed loads. A copy alone on its switch reads ticked.
    // This mod goes by its switch alone.
    public bool Ticked(string id, string copy, IEnumerable<string> installed)
    {
        if (id == ModEntry.ModId || !Off.Contains($"{id}|{copy}") || Record?.GetValueOrDefault(id) is not { } known || !known.Contains(copy))
            return true;
        return !installed.Any(c => c != copy && known.Contains(c) && !Off.Contains($"{id}|{c}"));
    }

    // The record's list ties for an id go by (Copies.TieOrder), and the copies of it the last launch had.
    public IReadOnlyList<string>? TieList(string id) => _tieRecord?.GetValueOrDefault(id) ?? Record?.GetValueOrDefault(id);

    public IReadOnlySet<string>? BeforeOf(string id) => _before?.GetValueOrDefault(id);

    // These as a tick on this copy would leave them (Copies.LoadingIf, as Dependencies.Write does): its key out of Off,
    // and where its switch is off, every other copy of that switch in (it comes on alone), our record's missing ones too.
    public Ticks Ticking(Mod mod, bool switchOff)
    {
        if (mod.manifest?.id is not { } id)
            return this;
        var (copy, key) = (Copies.CopyOf(mod), Copies.SwitchKey(mod));
        var off = Off.Where(k => k != key);
        if (switchOff && id != ModEntry.ModId)
            off = off.Concat(Copies.Twins(mod).Select(Copies.CopyOf).Concat(Copies.RecordGroup(id, mod.modSource, Record) ?? [])
                .Where(c => c != copy).Select(c => $"{id}|{c}"));
        return new Ticks(off, Record, _before, _tieRecord);
    }
}

// A version as Copies.Version reads it for the choice between copies of one source: its numbers (digits, leading zeros
// dropped; as many as it has, a missing one counting as 0) and its prerelease parts (none: a release). Ordered as the
// game's SemanticVersion orders versions, a number by value whatever its size.
internal sealed class Ver : IComparable<Ver>
{
    public IReadOnlyList<string> Numbers { get; }

    public IReadOnlyList<string> Pre { get; }

    Ver(IEnumerable<string> numbers, IEnumerable<string> pre)
    {
        Numbers = [.. numbers.Select(n => n.TrimStart('0') is { Length: > 0 } digits ? digits : "0")];
        Pre = [.. pre];
    }

    // The game's reading, inside our own try: a number past int's range throws out of TryFromString.
    public static SemanticVersion? Game(string? text)
    {
        if (text == null)
            return null;
        try
        {
            return SemanticVersion.TryFromString(text, out var version) ? version : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static Ver Of(SemanticVersion version) => new([.. new[] { version.Major, version.Minor, version.Patch }.Select(n => n.ToString(CultureInfo.InvariantCulture))],
        version.Prerelease ?? []);

    // A version the game can't read, read leniently (P5): trimmed, a leading "v" dropped, then the leading run of
    // numbers with dots between ("1.2" is 1.2.0, "1.1.0.1" above 1.1.0); what follows, after at most one separator, is a
    // prerelease, split at anything that isn't a letter or a digit ("0.4.1.beta" is 0.4.1-beta, "1.2.25(beta)"
    // 1.2.25-beta, "1.0b" 1.0.0-b, below 1.0); "+metadata" ignored. Null where no number leads ("abc").
    public static Ver? Lenient(string? text)
    {
        var s = text?.Trim() ?? "";
        if (s.StartsWith('v') || s.StartsWith('V'))
            s = s[1..];
        var (at, numbers) = (0, new List<string>());
        while (true)
        {
            var start = at;
            while (at < s.Length && char.IsAsciiDigit(s[at]))
                at++;
            if (at == start)
                break;
            numbers.Add(s[start..at]);
            if (at + 1 < s.Length && s[at] == '.' && char.IsAsciiDigit(s[at + 1]))
                at++;
            else
                break;
        }
        if (numbers.Count == 0)
            return null;
        var rest = s[at..];
        if (rest.IndexOf('+') is var plus and >= 0)
            rest = rest[..plus];
        if (rest.Length > 0 && !char.IsLetterOrDigit(rest[0]))
            rest = rest[1..];
        var (pre, part) = (new List<string>(), new System.Text.StringBuilder());
        foreach (var c in rest.Append(' '))
            if (char.IsLetterOrDigit(c))
                part.Append(c);
            else if (part.Length > 0)
            {
                pre.Add(part.ToString());
                part.Clear();
            }
        return new Ver(numbers, pre);
    }

    public int CompareTo(Ver? other)
    {
        if (other == null)
            return 1;
        for (var i = 0; i < Math.Max(Numbers.Count, other.Numbers.Count); i++)
            if (Number(i < Numbers.Count ? Numbers[i] : "0", i < other.Numbers.Count ? other.Numbers[i] : "0") is var by && by != 0)
                return by;
        // A release above its prereleases; then part by part, as SemanticVersion: two numbers by value, two words
        // ordinally, a number below a word; with every shared part equal, more parts above.
        if (Pre.Count == 0 || other.Pre.Count == 0)
            return (Pre.Count == 0 ? 1 : 0) - (other.Pre.Count == 0 ? 1 : 0);
        for (var i = 0; i < Math.Min(Pre.Count, other.Pre.Count); i++)
        {
            var (a, b) = (Pre[i], other.Pre[i]);
            if (a == b)
                continue;
            return (int.TryParse(a, out var x), int.TryParse(b, out var y)) switch
            {
                (false, false) => Math.Sign(string.CompareOrdinal(a, b)),
                (true, false) => -1,
                (false, true) => 1,
                _ => x.CompareTo(y),
            };
        }
        return Pre.Count.CompareTo(other.Pre.Count);
    }

    // Two numbers as digits, by value: the longer is larger, else digit by digit.
    static int Number(string a, string b) => a.Length != b.Length ? a.Length.CompareTo(b.Length) : Math.Sign(string.CompareOrdinal(a, b));

    public override string ToString() => string.Join(".", Numbers) + (Pre.Count > 0 ? "-" + string.Join(".", Pre) : "");
}
