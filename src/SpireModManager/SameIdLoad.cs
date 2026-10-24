using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Saves;

namespace SpireModManager;

// Which copy loads where a mod is installed more than once with one id and source (two Workshop items of it): the game's
// list has one switch for both, and the game would load the first and fail the rest with MOD_ERROR.DUPLICATE_ID. Our
// Init runs inside the game's load loop, and every mod after us is still to be tried (TryLoadMod skips a mod whose state
// isn't None, and sets Loaded only after the mod's initializers ran). So Apply chooses, of the copies the game left to
// load, the one our page says loads (Choose, Copies.Next's step 4: of the ticked copies of one source, the highest
// version), sets every other copy still to come to DisabledDuplicate, the game's own state for its duplicates, and swaps
// the chosen copy into the id's first slot, where mods that need the id look for it. Settle, at the first main menu,
// repairs what the game's rebuilt list coupled by id, keeps our entries first (so we load before every copy next
// launch) and checks the choice was taken. Anything unexpected: no flip, a warning, and the game's own behaviour.
static class SameIdLoad
{
    // Why the copy chosen doesn't load this launch: a copy of the id sorted before us (the game decided it already), or a
    // dependency of it loads after the id's first slot while a mod after us needs the id (the game's own choice loads).
    public enum Reason { None, DecidedBeforeUs, DependencyAfterSlot }

    // What Apply did for one id installed more than once. Loads: the copy that loads, or that the game tries; SetAside:
    // the copies Apply set aside; Swapped: the copy that loads moved from slot From to To (the id's first); Why: why the
    // copy chosen doesn't load this launch; Blocked and Late: for DependencyAfterSlot, the copy chosen (CopyOf) and the
    // dependency of it that loads after the slot, for the page to say the next launch does the same (Copies.Next).
    public record Entry(Mod? Loads, IReadOnlyList<Mod> SetAside, bool Swapped, int From, int To, Reason Why, double Ms, string? Blocked = null, string? Late = null);

    // The self-check's verdict on an id: Taken (loaded as planned), PickedFailed (the chosen copy failed for its own
    // reasons), NotApplied (Apply left it, Entry.Why), NotTaken (anything else: the game didn't do what Apply relies on).
    public enum Result { Taken, PickedFailed, NotApplied, NotTaken }

    static Dictionary<string, Entry> _plan = [];
    public static IReadOnlyDictionary<string, Entry> Plan => _plan;

    // Whether the game loads mods as Apply relies on (null: Apply didn't get to look).
    public static bool? Canary { get; private set; }

    // Our data's ticks as read at Init (copies_off and the record, from our file; null: none read): what this launch
    // chose by, and in a session that can't save, the record the next launch's ties go by (Ticks.Now).
    public static Ticks? InitTicks { get; private set; }

    // Where our Init found itself among the mods, and the first still to load there (the canary's look).
    public static (int At, string? Id) Self { get; private set; } = (-1, null);

    // First thing in ModEntry.Init, in a try there: a renamed game member throws at the call. Only memory changes, plus
    // one read of our file; any mismatch with how the game loads mods means no flip and a warning.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Apply()
    {
        try
        {
            var mods = ModManager.Mods;
            var (canary, why, plan, self) = Run(mods, ReadTicks, IsUs, live: IsLive(mods));
            (Canary, _plan, Self) = (canary, plan, self);
            if (!canary)
                Log.Warn($"[{ModEntry.ModId}] Same ID: the game now loads mods differently ({why}); which copy of a mod installed twice loads can't be chosen until Spire Mod Manager is updated");
        }
        catch (Exception e)
        {
            Canary = false;
            Log.Warn($"[{ModEntry.ModId}] Same ID: could not choose which copy of a mod loads, so the game's own choice applies: {e}");
        }
    }

    // The look and the flips, on the game's list or a self-test's made-up one: the canary, then each id installed more
    // than once, in the order of its first slot. readTicks runs only when some id has two copies; isUs tells our own
    // entry apart; live: the list is the one the game loads from (a copy would take the swap, not the game's loop). log:
    // false for the self-test's made-up lists.
    public static (bool Canary, string Why, Dictionary<string, Entry> Plan, (int At, string? Id) Self) Run(IReadOnlyList<Mod> mods,
        Func<Ticks> readTicks, Func<Mod, bool> isUs, bool log = true, bool live = true)
    {
        if (mods is not List<Mod> list)
            return (false, "its list of mods isn't a List<Mod>", [], (-1, null));
        if (!live)
            return (false, "its list of mods is a copy, not the one it loads from", [], (-1, null));
        if (!Enum.TryParse<ModLoadState>("DisabledDuplicate", out var aside))
            return (false, "it has no DisabledDuplicate state", [], (-1, null));
        // TryLoadMod decides each mod in turn, so everything before us is decided and we're the first still at None.
        var self = list.FindIndex(m => m.state == ModLoadState.None);
        var first = self < 0 ? null : list[self].manifest?.id;
        if (self < 0 || !isUs(list[self]))
            return (false, self < 0 ? "no mod is still to load while we load" : $"the first mod still to load is {first} at {self}, not us", [], (self, first));
        var groups = list.Select((m, i) => (Mod: m, At: i)).Where(p => p.Mod.manifest?.id is { } id && id != ModEntry.ModId)
            .GroupBy(p => p.Mod.manifest?.id ?? "").Where(g => g.Select(p => Copies.CopyOf(p.Mod)).Distinct().Count() > 1).ToList();
        var plan = new Dictionary<string, Entry>();
        if (groups.Count == 0)
            return (true, "", plan, (self, first));
        var ticks = readTicks();
        foreach (var group in groups)
        {
            var watch = Stopwatch.StartNew();
            var id = group.Key;
            var copies = group.ToList();
            var todo = copies.Where(p => p.At > self && p.Mod.state == ModLoadState.None).ToList();
            // The tick by Ticks' rule over the list's own copies of each source (a self-test's list is made up, so not
            // Copies.Twins), ties by the record read; a mod after us still to load that needs the id with a minimum
            // version: a copy the game can't read ranks below one it can (Copies.Best).
            bool Ticked(Mod m) => ticks.Ticked(id, Copies.CopyOf(m), copies.Where(p => p.Mod.modSource == m.modSource).Select(p => Copies.CopyOf(p.Mod)));
            IEnumerable<string> TieOrder(IEnumerable<string> tied) => Copies.TieOrder(tied, ticks.TieList(id), ticks.BeforeOf(id));
            var strict = list.Skip(self + 1).Any(m => m.state == ModLoadState.None && m.manifest?.id != id
                && m.manifest?.dependencies?.Any(d => d.id == id && d.minVersion != null) == true);
            (Mod Mod, Copies.Won How)? Chosen(IEnumerable<Mod> left) => Choose([.. left], Ticked, TieOrder, strict);
            void Say(string line)
            {
                if (log)
                    Log.Info($"[{ModEntry.ModId}] Same ID: {line}");
            }
            void Skipped(string text)
            {
                if (log)
                    Log.Warn($"[{ModEntry.ModId}] Same ID: {id}: the copy chosen doesn't load this launch: {text}");
            }

            // A copy of it sorted before us and loaded: each copy still to come would fail as a duplicate, so they're set
            // aside anyway. Nothing to set aside and the loaded copy the one chosen (or none ticked): nothing to say, nor to
            // check (the game failed the copies before us as duplicates: the update's first launch, before Settle put us
            // first).
            if (copies.FirstOrDefault(p => p.Mod.state == ModLoadState.Loaded) is { Mod: { } loaded } early)
            {
                foreach (var p in todo)
                    SetAside(p.Mod, aside, log);
                var decided = Chosen(todo.Select(p => p.Mod).Prepend(loaded)) is { } other && Copies.CopyOf(other.Mod) != Copies.CopyOf(loaded);
                if (todo.Count == 0 && !decided)
                    continue;
                if (decided)
                    Skipped($"already decided: it sorted before us, at {early.At}, and we load at {self}; applies next launch");
                plan[id] = new Entry(loaded, [.. todo.Select(p => p.Mod)], false, early.At, early.At, decided ? Reason.DecidedBeforeUs : Reason.None,
                    watch.Elapsed.TotalMilliseconds);
                continue;
            }
            // One copy (or none) still to come: nothing to choose. The game's local-vs-Workshop rule set the others aside,
            // which the page foresees (Copies.Next's step 2), so it's no surprise.
            if (todo.Count < 2)
                continue;

            // The copy chosen (Choose), else, none of them ticked, the game's own choice: the copy it would load before
            // failing the rest.
            var choice = Chosen(todo.Select(p => p.Mod));
            var picked = choice is { } chosen ? todo.First(p => p.Mod == chosen.Mod) : todo[0];
            var (s1, sp) = (todo[0].At, picked.At);
            var swapped = false;
            // The game finds a dependency at the id's first copy in the list, so the chosen copy goes there, if what it
            // needs loads before that slot.
            if (sp != s1)
            {
                var late = (picked.Mod.manifest?.dependencies ?? []).Select(d => d.id).FirstOrDefault(dep =>
                    list.FindIndex(m => m.manifest?.id == dep) is var at && at >= s1);
                if (late == null && s1 > self && sp > self)
                {
                    // Through the list's array: the indexer would bump its version, and the game's foreach would throw.
                    var span = CollectionsMarshal.AsSpan(list);
                    (span[s1], span[sp]) = (span[sp], span[s1]);
                    swapped = true;
                }
                else if (list.Skip(self + 1).Any(m => m.state == ModLoadState.None && m.manifest?.id != id
                    && m.manifest?.dependencies?.Any(d => d.id == id) == true))
                {
                    // The game's own choice instead (it needs no swap), the rest set aside as ever, so the game doesn't fail
                    // them as duplicates of it. The page reads Blocked and Late: the next launch does the same while
                    // nothing changes the choice.
                    var others = todo.Where(p => p.Mod != todo[0].Mod).Select(p => p.Mod).ToList();
                    foreach (var mod in others)
                        SetAside(mod, aside, log);
                    Skipped($"{Copies.CopyOf(picked.Mod)} needs {late}, which loads after slot {s1}; the game's choice, {Copies.CopyOf(todo[0].Mod)}, loads");
                    plan[id] = new Entry(todo[0].Mod, others, false, sp, s1, Reason.DependencyAfterSlot, watch.Elapsed.TotalMilliseconds,
                        Copies.CopyOf(picked.Mod), late);
                    continue;
                }
            }
            var setAside = todo.Where(p => p.Mod != picked.Mod).Select(p => p.Mod).ToList();
            foreach (var mod in setAside)
                SetAside(mod, aside, log);
            var ms = watch.Elapsed.TotalMilliseconds;
            plan[id] = new Entry(picked.Mod, setAside, swapped, sp, s1, Reason.None, ms);
            string Copy(Mod m) => $"{Copies.CopyOf(m)} ({m.manifest?.version})";
            var unread = setAside.Where(m => Copies.Version(m) == null || strict && Copies.GameVersion(m) == null).Select(Copies.CopyOf).Distinct();
            Say((choice?.How switch
                {
                    null => $"{id}: none of the copies left to load is enabled; the game's choice, {Copies.CopyOf(picked.Mod)}",
                    Copies.Won.First => $"{id} loads {Copies.CopyOf(picked.Mod)}, the copy installed first of the enabled copies with the same version",
                    Copies.Won.Readable => $"{id} loads {Copy(picked.Mod)}; {string.Join(", ", unread)} have a version that can't be read",
                    _ => $"{id} loads {Copy(picked.Mod)}, the highest version of the enabled copies",
                })
                + $" ({picked.Mod.path}); set aside {string.Join(", ", setAside.Select(Copy))} as a duplicate, so the game doesn't fail it"
                + (swapped ? $"; moved it from slot {sp} to {s1} so mods needing {id} find it" : "") + $" ({ms:0.0} ms)");
        }
        return (true, "", plan, (self, first));
    }

    // Spire Mod Manager's step at launch, as the page takes it (Copies.Next's step 4): of the copies left to load (one Mod
    // per copy, its newest manifest as the page reads it, Copies.NewestOf: two manifests of one Workshop item are one
    // copy, and the one whose version counts is the one that loads), the ticked ones of the first ticked copy's source
    // (nothing is chosen across sources), and of those Copies.Best. Null when none is ticked: the game's own choice then.
    // strict: a mod still to load needs the id with a minimum version.
    public static (Mod Mod, Copies.Won How)? Choose(IReadOnlyList<Mod> todo, Func<Mod, bool> ticked, Func<IEnumerable<string>, IEnumerable<string>> tieOrder,
        bool strict = false)
    {
        var copies = todo.GroupBy(Copies.CopyOf).Select(copy => Copies.NewestOf([.. copy])).ToList();
        if (copies.FirstOrDefault(ticked) is not { } first)
            return null;
        return Copies.Best([.. copies.Where(m => m.modSource == first.modSource && ticked(m))], Copies.Version, tieOrder,
            strict ? Copies.GameVersion : null);
    }

    // The game's own way with a duplicate it sets aside: DisabledDuplicate, its messages off the mod (the game clears
    // them, :368); ours keep them for its info panel (BranchErrors).
    static void SetAside(Mod mod, ModLoadState aside, bool log)
    {
        mod.state = aside;
        if (mod.errors is { Count: > 0 } errors)
        {
            BranchErrors.Keep(mod, errors);
            if (log)
                Log.Info($"[{ModEntry.ModId}] Same ID: took {errors.Count} game message{(errors.Count == 1 ? "" : "s")} off {Copies.CopyOf(mod)} (it doesn't load): "
                    + string.Join(", ", errors.Select(e => e.LocEntryKey)));
        }
        mod.errors = null;
    }

    // Our own entry: our id, from the folder our assembly loaded from (the id alone where the assembly has no file).
    static bool IsUs(Mod mod)
    {
        if (mod.manifest?.id != ModEntry.ModId)
            return false;
        var location = typeof(SameIdLoad).Assembly.Location;
        return string.IsNullOrEmpty(location) || string.Equals(Folder(mod.path), Folder(Path.GetDirectoryName(location)), StringComparison.OrdinalIgnoreCase);
    }

    static string Folder(string? path) => string.IsNullOrEmpty(path) ? "" : Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    // Whether the game's Mods is the list its load loop walks (its _mods field), not a copy of it; where the field is
    // renamed, at least the same list on every read.
    static bool IsLive(IReadOnlyList<Mod> mods) =>
        typeof(ModManager).GetField("_mods", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) is { } field
            ? ReferenceEquals(field, mods) : ReferenceEquals(mods, ModManager.Mods);

    // Our data's ticks as saved (manager.json's copies_off and copies), read from the file into InitTicks: loading our
    // data does work of its own that waits for the main menu (Store.Load). A file from before copies_off reads through
    // its copy_picks (Copies.LegacyOff), as the page reads it. With them, the copies each id had at the last launch
    // (LaunchGuard's records), for ties. No file: nothing off; an unreadable file or record counts as nothing off,
    // logged. The records are read inside the try too: outside it, a throw would end Apply and leave the game's own
    // choice, where nothing off still applies the rule.
    static Ticks ReadTicks()
    {
        Dictionary<string, HashSet<string>>? before = null;
        try
        {
            before = LaunchGuard.CopiesBefore(Store.Dir);
            var file = Store.FilePath;
            if (!File.Exists(file))
                return InitTicks = new Ticks(null, null, before);
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var record = Store.RecordIn(doc.RootElement);
            var off = Strings(doc.RootElement, "copies_off") ?? Copies.LegacyOff(Strings(doc.RootElement, "copy_picks"), record);
            return InitTicks = new Ticks(off, record, before);
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.ModId}] Same ID: our data or launch records unreadable: {e.Message}; every copy counts as enabled this launch");
            return InitTicks = new Ticks(null, null, before);
        }
    }

    // A list of strings in our file (null: none, or not a list).
    static List<string>? Strings(JsonElement root, string name) => root.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array
        ? [.. array.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.String).Select(p => p.GetString() ?? "")] : null;

    // For each id and source installed more than once (ours aside), the copy this launch loads, for the launch record
    // (LaunchGuard.Begin, which can't load our data yet): Apply's, else the first copy still to load or loaded (one the
    // game's rule left), else the game's own first.
    public static Dictionary<(string Id, ModSource Source), string> LoadsAtLaunch(IReadOnlyList<Mod> mods)
    {
        var loads = new Dictionary<(string, ModSource), string>();
        foreach (var group in mods.Where(m => m.manifest?.id is { } id && id != ModEntry.ModId).GroupBy(m => (Id: m.manifest?.id ?? "", m.modSource)))
        {
            var copies = group.ToList();
            if (copies.Select(Copies.CopyOf).Distinct().Count() < 2)
                continue;
            loads[group.Key] = _plan.GetValueOrDefault(group.Key.Id)?.Loads is { } planned && planned.modSource == group.Key.modSource ? Copies.CopyOf(planned)
                : copies.FirstOrDefault(m => m.state is ModLoadState.None or ModLoadState.Loaded) is { } going ? Copies.CopyOf(going)
                : Copies.CopyOf(copies[0]);
        }
        return loads;
    }

    static bool _settled;
    static readonly Dictionary<string, Result> Results = [];
    static HashSet<string> _twinIds = [];

    public static bool Settled => _settled;

    // The game loads mods in a way the choice doesn't fit (the canary failed, or Apply didn't run) while an id is installed
    // more than once: no copy is chosen by us this launch.
    public static bool CanaryFailed { get; private set; }

    // Once, at the first main menu (MenuReachedPatch, after LaunchGuard.MenuReached): before our data loads and the undo
    // history starts, so nothing here is a change since launch or an undo step. The mods we keep off after a failed start
    // get back the entries the game's list dropped (TurnedOff.KeepOff), so the save keeps them.
    public static void Settle()
    {
        if (_settled)
            return;
        _settled = true;
        if (SaveManager.Instance.SettingsSave.ModSettings is { } settings && Settle(settings.ModList, LaunchGuard.ReadList))
            SaveManager.Instance.SaveSettings(); // once, only when something changed
        Check(ModManager.Mods);
    }

    // Settle's changes to a saved list and the list as the game read it (the game's, or the self-test's made-up ones):
    // SettleList's, then the entries the game's list dropped of mods we keep off. True if anything changed.
    public static bool Settle(List<SettingsSaveMod> list, IReadOnlyList<(string Id, ModSource Source, bool On)>? read) =>
        SettleList(list, read) | TurnedOff.KeepOff(list);

    // Settle's changes to a saved list (the game's, or the self-test's made-up one): the rebuild repaired, each id's
    // entries that are on first, ours at the top. True if anything changed.
    public static bool SettleList(List<SettingsSaveMod> list, IReadOnlyList<(string Id, ModSource Source, bool On)>? read)
    {
        var changed = Repair(list, read);
        changed |= OnFirst(list);
        var at = list.FindIndex(e => e.Id == ModEntry.ModId);
        if (LoadOrder.OursFirst(list))
        {
            changed = true;
            Log.Info($"[{ModEntry.ModId}] Same ID: moved our entries to the top of the load order (we were {Ordinal(at + 1)}), so we load first next launch");
        }
        return changed;
    }

    static string Ordinal(int n) => n + (n % 100 is 11 or 12 or 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" });

    // The game's rebuilt list gives every copy of an id the first entry's on/off (it matches by id only): each id whose
    // entries at launch (read: the list as the game read it, LaunchGuard.ReadList) had two or more sources gets each
    // source's own back, off if any of its entries was. A source the launch's list didn't have (a new copy) keeps the
    // rebuild's. Ours too: a copy of ours the player turned off (on our page or the game's) would otherwise come back on
    // at every launch, from the copy of ours that's on. True if anything changed.
    public static bool Repair(List<SettingsSaveMod> list, IReadOnlyList<(string Id, ModSource Source, bool On)>? read)
    {
        if (read == null)
            return false;
        var changed = false;
        foreach (var id in read.GroupBy(e => e.Id).Where(g => g.Select(e => e.Source).Distinct().Count() > 1).Select(g => g.Key))
        {
            var put = new List<string>();
            foreach (var source in read.Where(e => e.Id == id).Select(e => e.Source).Distinct())
            {
                var on = read.Where(e => e.Id == id && e.Source == source).All(e => e.On);
                var wrong = list.Where(e => e.Id == id && e.Source == source && e.IsEnabled != on).ToList();
                foreach (var entry in wrong)
                    entry.IsEnabled = on;
                if (wrong.Count > 0)
                    put.Add($"({id}, {source}) {(on ? "on" : "off")}");
            }
            if (put.Count == 0)
                continue;
            changed = true;
            Log.Info($"[{ModEntry.ModId}] Same ID: the game's rebuilt list gave every copy of {id} the first entry's on/off; put {string.Join(", ", put)} again");
        }
        return changed;
    }

    // Within each id's own places in the list, its entries that are on first (ids with entries of two or more sources):
    // the rebuild copies the first entry's on/off to every copy, so a session that quits before Settle runs can only turn
    // copies on, never make the mod vanish. True if anything moved.
    public static bool OnFirst(List<SettingsSaveMod> list)
    {
        var changed = false;
        foreach (var id in list.GroupBy(e => e.Id).Where(g => g.Select(e => e.Source).Distinct().Count() > 1).Select(g => g.Key).ToList())
        {
            var slots = list.Select((e, i) => (Entry: e, At: i)).Where(p => p.Entry.Id == id).Select(p => p.At).ToList();
            var entries = slots.Select(i => list[i]).ToList();
            var sorted = entries.OrderBy(e => e.IsEnabled ? 0 : 1).ToList(); // stable: each kind keeps its order
            if (sorted.SequenceEqual(entries))
                continue;
            for (var k = 0; k < slots.Count; k++)
                list[slots[k]] = sorted[k];
            changed = true;
        }
        return changed;
    }

    // The self-check, kept for the info panel: each id Apply planned for, and whether the canary failed while ids are
    // installed more than once (a game update that reorders its startup trips it on its first day).
    static void Check(IReadOnlyList<Mod> mods)
    {
        // Ids installed more than once from one source: the ones whose copy we choose (a local and a Workshop copy are
        // the game's to choose between, by their switches and its own rule).
        _twinIds = mods.Where(m => m.manifest?.id is { } id && id != ModEntry.ModId).GroupBy(m => (Id: m.manifest?.id ?? "", m.modSource))
            .Where(g => g.Select(Copies.CopyOf).Distinct().Count() > 1).Select(g => g.Key.Id).ToHashSet();
        CanaryFailed = Canary != true && _twinIds.Count > 0;
        if (CanaryFailed)
            Log.Warn($"[{ModEntry.ModId}] Same ID: {string.Join(", ", _twinIds)} installed more than once, and which copy loads couldn't be chosen this launch (the game's own choice applied)");
        foreach (var (id, entry) in _plan)
            Results[id] = Judge(id, entry, mods, Self.At);
    }

    // self: where our Init found us; a copy before it the game decided on its own (a duplicate error there is the game's,
    // from before we could set it aside).
    public static Result Judge(string id, Entry entry, IReadOnlyList<Mod> mods, int self)
    {
        if (entry.Why != Reason.None)
            return Result.NotApplied;
        var copies = mods.Where(m => m.manifest?.id == id).ToList();
        var after = mods.Skip(self + 1).Where(m => m.manifest?.id == id);
        if (entry.Loads is { state: ModLoadState.Failed } failed && !HasError(failed, DuplicateKey))
            return Result.PickedFailed;
        if (entry.Loads is { state: ModLoadState.Loaded } picked && entry.SetAside.All(m => m.state == ModLoadState.DisabledDuplicate)
            && !after.Any(m => HasError(m, DuplicateKey)) && copies.FirstOrDefault() == picked && !mods.Any(m => Misses(m, id)))
            return Result.Taken;
        Log.Warn($"[{ModEntry.ModId}] Same ID: {id}: the copy chosen wasn't taken as planned: "
            + string.Join("; ", copies.Select(m => $"{Copies.CopyOf(m)} {m.state}" + (m.errors is { Count: > 0 } e ? $" ({string.Join(", ", e.Select(x => x.LocEntryKey))})" : ""))));
        return Result.NotTaken;
    }

    const string DuplicateKey = "MOD_ERROR.DUPLICATE_ID", MissingKey = "MOD_ERROR.MISSING_DEPENDENCY";

    static bool HasError(Mod mod, string key) => mod.errors?.Any(e => e.LocEntryKey == key) == true;

    // A mod that needs the id and failed for missing it.
    static bool Misses(Mod mod, string id) => mod.manifest?.dependencies?.Any(d => d.id == id) == true
        && mod.errors?.Any(e => e.LocEntryKey == MissingKey && Names(e, id)) == true;

    // Whether the game's missing-dependency message names the id (its missingDependencies, comma-separated); one that
    // can't be read counts as naming it.
    static bool Names(MegaCrit.Sts2.Core.Localization.LocString error, string id)
    {
        try
        {
            return !error.Variables.TryGetValue("missingDependencies", out var named) || named is not string list || list.Split(',').Contains(id);
        }
        catch (Exception)
        {
            return true;
        }
    }

    public static Result? ResultOf(string? id) => id != null && Results.TryGetValue(id, out var result) ? result : null;

    // A relaunch wouldn't change which copy of the id loads: the self-check found the choice not taken, or the canary
    // failed. The page then reads the game's own choice for it (Copies.Next's step 3), and the info panel says so
    // (COPY_NOT_APPLIED).
    public static bool Unchosen(string? id) => id != null && (ResultOf(id) == Result.NotTaken || CanaryFailed && _twinIds.Contains(id));

    // A copy Apply set aside this launch (its messages are ours to keep, BranchErrors).
    public static bool IsSetAside(Mod mod) => _plan.Values.Any(e => e.SetAside.Contains(mod));

#if DEVTOOLS
    // Self-test: an id's self-check as if it had found this (null: as it did).
    public static void TestResult(string id, Result? result)
    {
        if (result is { } found)
            Results[id] = found;
        else if (_plan.TryGetValue(id, out var entry))
            Results[id] = Judge(id, entry, ModManager.Mods, Self.At);
        else
            Results.Remove(id);
    }

    // Self-test: an id's plan as if Apply had made this one (null: none).
    public static void TestPlan(string id, Entry? entry)
    {
        if (entry is { } made)
            _plan[id] = made;
        else
            _plan.Remove(id);
    }
#endif
}
