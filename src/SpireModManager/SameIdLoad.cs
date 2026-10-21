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
// isn't None, and sets Loaded only after the mod's initializers ran). So Apply sets each copy still to come but the
// picked one (StoreData.CopyPicks) to DisabledDuplicate, the game's own state for its duplicates, and swaps the picked
// copy into the id's first slot, where mods that need the id look for it. Settle, at the first main menu, repairs what
// the game's rebuilt list coupled by id, turns every entry of ours on and keeps them first (so we load before every
// copy next launch) and checks the pick was taken. Anything unexpected: no flip, a warning, and the game's own behaviour.
static class SameIdLoad
{
    // Why the pick doesn't load this launch: a copy of the id sorted before us (the game decided it already), the game's
    // local-vs-Workshop rule set it aside, or a dependency of it loads after the id's first slot while a mod after us
    // needs the id (no flip, the game's own choice). A pick whose source is off, or that names a copy no longer
    // installed, isn't one: the player chose the other source, or it has nothing to apply to.
    public enum Reason { None, DecidedBeforeUs, SetAsideByGame, DependencyAfterSlot }

    // What Apply did for one id installed more than once. Loads: the copy that loads, or that the game tries; SetAside:
    // the copies Apply set aside; Swapped: the copy that loads moved from slot From to To (the id's first); HadPick: a
    // saved pick named it (else it's the game's own choice); Why: why the pick doesn't load this launch.
    public record Entry(Mod? Loads, IReadOnlyList<Mod> SetAside, bool Swapped, int From, int To, bool HadPick, Reason Why, double Ms);

    // The self-check's verdict on an id: Taken (loaded as planned), PickedFailed (the picked copy failed for its own
    // reasons), NotApplied (Apply left it, Entry.Why), NotTaken (anything else: the game didn't do what Apply relies on).
    public enum Result { Taken, PickedFailed, NotApplied, NotTaken }

    static Dictionary<string, Entry> _plan = [];
    public static IReadOnlyDictionary<string, Entry> Plan => _plan;

    // Whether the game loads mods as Apply relies on (null: Apply didn't get to look).
    public static bool? Canary { get; private set; }

    // The picks read at Init (null: none read).
    public static IReadOnlySet<string>? InitPicks { get; private set; }

    // The copies off read at Init (copies_off; null: none read, or a file from before it).
    public static IReadOnlySet<string>? InitOff { get; private set; }

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
            var (canary, why, plan, self) = Run(mods, ReadPicks, IsUs, live: IsLive(mods), readOff: () => InitOff);
            (Canary, _plan, Self) = (canary, plan, self);
            if (!canary)
                Log.Warn($"[{ModEntry.ModId}] Same ID: the game now loads mods differently ({why}); copy picks can't apply until Spire Mod Manager is updated");
        }
        catch (Exception e)
        {
            Canary = false;
            Log.Warn($"[{ModEntry.ModId}] Same ID: could not choose which copy of a mod loads, so the game's own choice applies: {e}");
        }
    }

    // The look and the flips, on the game's list or a self-test's made-up one: the canary, then each id installed more
    // than once, in the order of its first slot. readPicks runs only when some id has two copies, readOff (the copies off,
    // for an id with no pick) after it; isUs tells our own entry apart; live: the list is the one the game loads from (a
    // copy would take the swap, not the game's loop). log: false for the self-test's made-up lists.
    public static (bool Canary, string Why, Dictionary<string, Entry> Plan, (int At, string? Id) Self) Run(IReadOnlyList<Mod> mods,
        Func<IReadOnlySet<string>?> readPicks, Func<Mod, bool> isUs, bool log = true, bool live = true, Func<IReadOnlySet<string>?>? readOff = null)
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
        var picks = readPicks() ?? new HashSet<string>();
        var off = readOff?.Invoke();
        foreach (var group in groups)
        {
            var watch = Stopwatch.StartNew();
            var id = group.Key;
            var copies = group.ToList();
            var todo = copies.Where(p => p.At > self && p.Mod.state == ModLoadState.None).ToList();
            var pickKeys = picks.Where(k => Copies.Parse(k) is { } p && p.Id == id).ToHashSet();
            bool IsPick(Mod m) => pickKeys.Contains(Copies.SwitchKey(m));
            // A source's own pick: which of its copies loads (between sources, their switches in the game's list decide). One
            // naming a copy no longer installed is none: there's nothing for it to wait for.
            bool PickedIn(ModSource source) => copies.Any(p => p.Mod.modSource == source && IsPick(p.Mod));
            void Say(string line)
            {
                if (log)
                    Log.Info($"[{ModEntry.ModId}] Same ID: {line}");
            }
            void Skipped(string text)
            {
                if (log)
                    Log.Warn($"[{ModEntry.ModId}] Same ID: {id}: the picked copy doesn't load this launch: {text}");
            }

            // A copy of it sorted before us and loaded: each copy still to come would fail as a duplicate, so they're set
            // aside anyway. Nothing to set aside and the loaded copy picked (or no pick yet): nothing to say, nor to check
            // (the game failed the copies before us as duplicates: the update's first launch, before Settle put us first).
            if (copies.FirstOrDefault(p => p.Mod.state == ModLoadState.Loaded) is { Mod: { } loaded } early)
            {
                foreach (var p in todo)
                    SetAside(p.Mod, aside, log);
                var decided = PickedIn(loaded.modSource) && !IsPick(loaded);
                if (todo.Count == 0 && !decided)
                    continue;
                if (decided)
                    Skipped($"already decided: it sorted before us, at {early.At}, and we load at {self}; applies next launch");
                plan[id] = new Entry(loaded, [.. todo.Select(p => p.Mod)], false, early.At, early.At, IsPick(loaded),
                    decided ? Reason.DecidedBeforeUs : Reason.None, watch.Elapsed.TotalMilliseconds);
                continue;
            }
            // One copy (or none) still to come: nothing to choose. Said only where the pick of its own source is another
            // copy installed, which the game's local-vs-Workshop rule set aside (two copies of one source share a switch,
            // so the pick can't be off while this one is on). The other source's pick goes by that source's switch: off,
            // the player chose this copy. A pick naming a copy no longer installed has nothing to apply to.
            if (todo.Count < 2)
            {
                if (todo.FirstOrDefault() is not { Mod: { } only } || IsPick(only)
                    || copies.Select(p => p.Mod).FirstOrDefault(m => m.modSource == only.modSource && IsPick(m)) is not { } pick || pick.state != aside)
                    continue;
                Skipped("the game's local-vs-Workshop rule set it aside");
                plan[id] = new Entry(only, [], false, todo[0].At, todo[0].At, false, Reason.SetAsideByGame, watch.Elapsed.TotalMilliseconds);
                continue;
            }

            // The pick, or with none (one naming a copy no longer installed, or two: a local and a Workshop copy each
            // picked) the first copy that isn't off on our page (copies_off), else the game's own choice: the copy it would
            // load before failing the rest (also where that first copy not off can't take the id's first slot). A pick in
            // copies_off (a hand-edited file) loads all the same. One copy can be two manifests (of one Workshop item): its
            // first.
            var matching = todo.Where(p => IsPick(p.Mod)).ToList();
            var hadPick = matching.Select(p => Copies.CopyOf(p.Mod)).Distinct().Count() == 1;
            var picked = hadPick ? matching[0]
                : todo.FirstOrDefault(p => off?.Contains(Copies.SwitchKey(p.Mod)) != true) is { Mod: not null } ticked ? ticked : todo[0];
            var (s1, sp) = (todo[0].At, picked.At);
            var swapped = false;
            string? blocked = null;
            // The game finds a dependency at the id's first copy in the list, so the picked copy goes there, if what it
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
                    if (hadPick)
                    {
                        Skipped($"its dependency {late} loads after slot {s1}");
                        plan[id] = new Entry(todo[0].Mod, [], false, sp, s1, hadPick, Reason.DependencyAfterSlot, watch.Elapsed.TotalMilliseconds);
                        continue;
                    }
                    // No pick: the game's own choice instead (it needs no swap), the rest set aside as ever, so the game
                    // doesn't fail them as duplicates of it.
                    (picked, sp, blocked) = (todo[0], s1, late);
                }
            }
            var setAside = todo.Where(p => p.Mod != picked.Mod).Select(p => p.Mod).ToList();
            foreach (var mod in setAside)
                SetAside(mod, aside, log);
            var ms = watch.Elapsed.TotalMilliseconds;
            plan[id] = new Entry(picked.Mod, setAside, swapped, sp, s1, hadPick, Reason.None, ms);
            var others = string.Join(", ", setAside.Select(m => $"{Copies.CopyOf(m)} ({m.manifest?.version})"));
            Say(hadPick
                ? $"{id} loads from {Copies.CopyOf(picked.Mod)} ({picked.Mod.manifest?.version}, {picked.Mod.path}) as picked; set aside {others} as a duplicate, so the game doesn't fail it"
                    + (swapped ? $"; moved the picked copy from slot {sp} to {s1} so mods needing {id} find it" : "") + $" ({ms:0.0} ms)"
                : $"{id} has no picked copy yet: "
                    + (picked.Mod == todo[0].Mod ? $"kept the game's choice, {Copies.CopyOf(picked.Mod)}" : $"loads {Copies.CopyOf(picked.Mod)}, the first copy not disabled on our page")
                    + (blocked != null ? $" (that one needs {blocked}, which loads after slot {s1})" : "")
                    + $"; set aside {string.Join(", ", setAside.Select(Copies.CopyOf))} as a duplicate"
                    + (swapped ? $"; moved it from slot {sp} to {s1} so mods needing {id} find it" : ""));
        }
        return (true, "", plan, (self, first));
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

    // The picks as saved (manager.json's copy_picks), read from the file: loading our data does work of its own that waits
    // for the main menu (Store.Load). None for no file; an unreadable file counts as none, logged. The copies off
    // (copies_off) are read with them, into InitOff.
    static IReadOnlySet<string>? ReadPicks()
    {
        try
        {
            var file = Store.FilePath;
            if (!File.Exists(file))
                return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            InitPicks = doc.RootElement.TryGetProperty("copy_picks", out var picks) && picks.ValueKind == JsonValueKind.Array
                ? picks.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.String).Select(p => p.GetString() ?? "").ToHashSet()
                : new HashSet<string>();
            InitOff = doc.RootElement.TryGetProperty("copies_off", out var off) && off.ValueKind == JsonValueKind.Array
                ? off.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.String).Select(p => p.GetString() ?? "").ToHashSet()
                : null;
            return InitPicks;
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.ModId}] Same ID: picks unreadable: {e.Message}; the game's own choice applies this launch");
            return null;
        }
    }

    // For each id and source installed more than once (ours aside), the copy this launch loads, for the launch record
    // (LaunchGuard.Begin, which can't load our data yet): Apply's, else the pick read at Init where Apply could look (an
    // id it had nothing to do for: its copies off), else the game's own choice, its first copy.
    public static Dictionary<(string Id, ModSource Source), string> LoadsAtLaunch(IReadOnlyList<Mod> mods)
    {
        var loads = new Dictionary<(string, ModSource), string>();
        foreach (var group in mods.Where(m => m.manifest?.id is { } id && id != ModEntry.ModId).GroupBy(m => (Id: m.manifest?.id ?? "", m.modSource)))
        {
            var copies = group.ToList();
            if (copies.Select(Copies.CopyOf).Distinct().Count() < 2)
                continue;
            loads[group.Key] = _plan.GetValueOrDefault(group.Key.Id)?.Loads is { } planned && planned.modSource == group.Key.modSource ? Copies.CopyOf(planned)
                : Canary == true && copies.FirstOrDefault(m => InitPicks?.Contains(Copies.SwitchKey(m)) == true) is { } picked ? Copies.CopyOf(picked)
                : Copies.CopyOf(copies[0]);
        }
        return loads;
    }

    static bool _settled;
    static readonly Dictionary<string, Result> Results = [];
    static HashSet<string> _twinIds = [];

    public static bool Settled => _settled;

    // The game loads mods in a way the picks don't fit (the canary failed, or Apply didn't run) while an id is installed
    // more than once: no copy is chosen by us this launch.
    public static bool CanaryFailed { get; private set; }

    // Once, at the first main menu (MenuReachedPatch, after LaunchGuard.MenuReached): before our data loads and the undo
    // history starts, so nothing here is a change since launch or an undo step. Uses the picks read at Init only.
    public static void Settle()
    {
        if (_settled)
            return;
        _settled = true;
        if (SaveManager.Instance.SettingsSave.ModSettings is { } settings && SettleList(settings.ModList, LaunchGuard.ReadList))
            SaveManager.Instance.SaveSettings(); // once, only when something changed
        Check(ModManager.Mods);
    }

    // Settle's changes to a saved list (the game's, or the self-test's made-up one): the rebuild repaired, every entry of
    // ours on, each id's entries that are on first, ours at the top. True if anything changed.
    public static bool SettleList(List<SettingsSaveMod> list, IReadOnlyList<(string Id, ModSource Source, bool On)>? read)
    {
        var changed = Repair(list, read);
        changed |= OursOn(list);
        changed |= OnFirst(list);
        var at = list.FindIndex(e => e.Id == ModEntry.ModId);
        if (LoadOrder.OursFirst(list))
        {
            changed = true;
            Log.Info($"[{ModEntry.ModId}] Same ID: moved our entries to the top of the load order (we were {Ordinal(at + 1)}), so we load first next launch");
        }
        return changed;
    }

    // Every entry of ours on (Settle runs, so this launch loaded us): a copy of ours left off (unticked on the game's page)
    // would leave the mod off once the copy that runs is removed. Our views keep every copy of ours ticked
    // (Copies.Locked). True if anything changed.
    public static bool OursOn(List<SettingsSaveMod> list)
    {
        var off = list.Where(e => e.Id == ModEntry.ModId && !e.IsEnabled).ToList();
        foreach (var entry in off)
            entry.IsEnabled = true;
        if (off.Count > 0)
            Log.Info($"[{ModEntry.ModId}] Same ID: turned our entries back on ({string.Join(", ", off.Select(e => e.Source).Distinct())}), "
                + "so a copy of us still loads if the one running is removed");
        return off.Count > 0;
    }

    static string Ordinal(int n) => n + (n % 100 is 11 or 12 or 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" });

    // The game's rebuilt list gives every copy of an id the first entry's on/off (it matches by id only): each id whose
    // entries at launch (read: the list as the game read it, LaunchGuard.ReadList) had two or more sources gets each
    // source's own back, off if any of its entries was. A source the launch's list didn't have (a new copy) keeps the
    // rebuild's. Not ours: every entry of ours goes on (OursOn), so putting one off back would only be undone again. True
    // if anything changed.
    public static bool Repair(List<SettingsSaveMod> list, IReadOnlyList<(string Id, ModSource Source, bool On)>? read)
    {
        if (read == null)
            return false;
        var changed = false;
        foreach (var id in read.GroupBy(e => e.Id).Where(g => g.Key != ModEntry.ModId && g.Select(e => e.Source).Distinct().Count() > 1).Select(g => g.Key))
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
        Log.Warn($"[{ModEntry.ModId}] Same ID: {id}: the picked copy wasn't taken as planned: "
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

    // A relaunch wouldn't change which copy of the id loads: the self-check found the pick not taken, or the canary
    // failed. Left out of the pending test, and the info panel says so (COPY_NOT_APPLIED).
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
#endif
}
