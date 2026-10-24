using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;

namespace SpireModManager;

// Who stopped the game from starting. The exception that ended the start is kept as it's thrown (StartupFailurePatch,
// or the beta's UnobservedFault for a failure elsewhere in the startup), with each exception's frames as they were then.
// The frames map to the mods whose code they are (OwnerMap); three shapes are read first (a mod's types that can't load,
// two mods' content under one id, a Harmony patch that failed: whose patches it was applying, as PatchingFailurePatch
// records them on its exception), then the frames (ByFrame), and Rank weighs that against Mod Doctor's pick and what
// changed since the last working launch. Every step fails open: no culprit, the game's dialog.
static class Culprits
{
    // The rule behind a culprit, for the dialog's line, the turn-off's record and the log. Needs, Suspect and All are the
    // turn-off's own (a culprit's dependents, Mod Doctor's suspects, every mod).
    public enum Why
    {
        Code, Patch, LibraryUser, Types, Clash, Rebuild, RebuildLast, Stale, Self, DoctorThrew, DoctorUpdated, DoctorAdded,
        Updated, UpdatedMany, Needs, Suspect, All,
    }

    // Which of our checks applied: a mod's types that can't load, a content clash, a Harmony patch that failed, the probe
    // of a patched method's patches
    public enum Check { None, Types, Clash, Harmony, Probe }

    // Whose code a frame is: a mod's (a patched mod method's replacement too: its original's mod), a patched game method's
    // replacement, the runtime's (System, Microsoft, Harmony, MonoMod: code that only carries the call on), reflection's
    // (a call through it is a call into someone else's code), or the game's (anything else)
    public enum Whose { Mod, Replacement, Runtime, Reflection, Game }

    // One exception of a failure, with its frames as they were when it was kept: another mod's finalizer can reset them
    public record Link(Exception Error, IReadOnlyList<StackFrame> Frames);

    // The failure that ended the start, its exceptions innermost first (Chain), and where it was kept
    public record Fault(IReadOnlyList<Link> Chain, string Where);

    // One frame as the rules read it: whose, the mod's id for a mod's, and the original of a replacement
    public record Frame(Whose Whose, string? Mod, MethodBase? Method, MethodBase? Original);

    // One rule's pick: the mods (several for a probe group), why, whether one of our checks made it (the shapes, the
    // probe), and what its line names: the library that threw, a clash's other mod and their content's id, the step
    public record Pick(Why Why, bool Checked, IReadOnlyList<string> Ids, string? Library = null, string? Other = null,
        string? ContentId = null, string? Step = null);

    // What ByFrame read in a failure: its pick, or none and the shortlist the check that applied left (or the frames':
    // several patchers, a library); NoPatcher, a Harmony failure with no record of whose patches and no mod frame under
    // Harmony's (Mod Doctor's map by namespace may know the mod). Named: every mod the failure names, for ranking's
    // updated mods. Signature, Error and Rebuild (Harmony's build of a patched method failed), for the turn-off's record;
    // Notes, for the log.
    public record Reading(Pick? Pick, Check Check, IReadOnlyList<string> Shortlist, bool NoPatcher, IReadOnlySet<string> Named,
        string Signature, string Error, bool Rebuild, IReadOnlyList<string> Notes)
    {
        public static readonly Reading None = new(null, Check.None, [], false, new HashSet<string>(), "", "", false, []);
    }

    // A turn-off on record (TurnedOff's entries, as ranking reads them): when (one answer's entries share it), the mod,
    // each source's switch then with its copies, and whether a main menu came since
    public record Recorded(DateTimeOffset At, string Id, IReadOnlyList<Switch> Switches, bool MenuSince);

    public record Switch(ModSource Source, bool On, IReadOnlyList<string> Copies);

    // The culprit offered, and what the dialog says with it. Checked: one of our checks picked it. DoctorAgrees: Mod
    // Doctor's pick is its one mod, so Mod Doctor's box (DoctorText, its explanation) shows. Updates: its mods' updates
    // since the last working launch. Needing: the mods that need it, on as read (turned off with it; for ours, named and
    // left alone). DidntStick: a turn-off of it came undone. Streak: this failed start's place in a row of them.
    // Signature, Error and Rebuild: the failure's, for the record.
    public record Culprit(Why Why, bool Checked, IReadOnlyList<string> Ids, bool DoctorAgrees, string? DoctorText,
        string? Library, string? Other, string? ContentId, string? Step, IReadOnlyList<LaunchGuard.Change> Updates,
        IReadOnlyList<string> Needing, bool DidntStick, int Streak, string Signature, string Error, bool Rebuild = false);

    // What a failed patch call was applying, as PatchingFailurePatch writes it on the exception (Exception.Data, under
    // AttemptKey): PatchAll's patch class (its patch methods are the class's own), or Harmony.Patch's patch methods
    public record PatchAttempt(Type? Class, IReadOnlyList<MethodInfo> Methods);

    public const string AttemptKey = ModEntry.ModId + ".PatchAttempt";

#if DEVTOOLS
    // Self-test: a type's mod, asked first for each frame's type (and the types it's nested in) and each patch method's:
    // a mod id, that mod; "", the game's; null, the usual mapping
    public static Func<Type, string?>? TestOwner;

    // Self-test: asked before each PrepareMethod (the probe's, a rebuild's first patch), made to throw
    public static Action<MethodBase>? TestPrepare;

    // Self-test: the mods whose types can't all load, in place of each mod assembly's GetTypes
    public static Func<IEnumerable<string>>? TestTypesBroken;

    // Self-test: a type's content id and the type ModelDb holds under it, in place of ModelDb's (the stand-ins aren't models);
    // and the lookup itself, ModelDb's when TestHeld is null
    public static Func<Type, (string Id, Type? Type)?>? TestHeld;
    public static (string Id, Type? Type)? TestHeldOf(Type type) => Held(type);

    // Self-test: the failure kept, set or cleared; and the fallback keeping any exception, not only one from the startup
    public static Fault? TestFault { get => _fault; set => _fault = value; }
    public static bool TestAnyFault;

    // Self-test: the fallback's handler as the game calls it, and whether it's still on the game's event
    public static void TestFaulted(Exception error) => Faulted(error);
    public static Action<Exception>? TestWatching => _watching;
#endif

    static Fault? _fault;

    // From StartupFailurePatch's finalizer: the first exception only, its frames taken at once. A throw here only loses the
    // capture (Harmony drops what a finalizer throws), so it's caught.
    public static void Caught(Exception error, string where)
    {
        try
        {
            if (Volatile.Read(ref _fault) == null && Interlocked.CompareExchange(ref _fault, new Fault(Chain(error), where), null) == null)
                Log.Info($"[{ModEntry.ModId}] Kept the error that ended the game's start ({where}), to name the mod behind it");
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.ModId}] Could not keep the error that ended the game's start, so it can't name a mod: {e.Message}");
        }
    }

    // From PatchingFailurePatch's finalizers: what a failed patch call was applying, on its exception, the first record
    // only (one Harmony throws again as it is keeps the inner call's, the nearer one). Never throws, and the exception
    // stays as it is: ByFrame reads its frames without the record.
    public static void Attempted(Exception error, Type? patchClass, IEnumerable<MethodInfo?> methods)
    {
        try
        {
            if (!error.Data.Contains(AttemptKey))
                error.Data[AttemptKey] = new PatchAttempt(patchClass, [.. methods.OfType<MethodInfo>().Distinct()]);
        }
        catch (Exception)
        {
            // no record: the frames decide
        }
    }

    static Action<Exception>? _watching;

    // The fallback, from ModEntry.Init: a failure elsewhere in the game's startup (the main menu, the checks after it)
    // through the beta's TaskHelper.UnobservedFault (stable has none). No cutoff at the main menu: only the startup's
    // frames count (FromStartup), and it runs once.
    public static void Watch()
    {
        if (Compat.UnobservedFault is not { } fault || _watching != null)
            return;
        _watching = Faulted;
        fault.AddEventHandler(null, _watching);
    }

    // Raised from any thread (an async continuation): the first exception from the startup is kept, and the handler goes
    // once one is. Nothing of Godot here.
    static void Faulted(Exception error)
    {
        try
        {
#if DEVTOOLS
            var startup = TestAnyFault || FromStartup(error);
#else
            var startup = FromStartup(error);
#endif
            if (Volatile.Read(ref _fault) == null && startup)
                Interlocked.CompareExchange(ref _fault, new Fault(Chain(error), "UnobservedFault"), null);
            if (Volatile.Read(ref _fault) != null && Interlocked.Exchange(ref _watching, null) is { } handler)
                Compat.UnobservedFault?.RemoveEventHandler(null, handler);
        }
        catch (Exception)
        {
            // the capture is lost; the game's own handling of the fault goes on
        }
    }

    // Whether a failure came through NGame.GameStartup: its frame, or its async state machine's
    static bool FromStartup(Exception error) => error is not OperationCanceledException && Chain(error).Any(link => link.Frames.Any(frame =>
        frame.GetMethod() is { DeclaringType: { } type } method
        && (type == typeof(NGame) && method.Name == "GameStartup" || type.DeclaringType == typeof(NGame) && type.Name.StartsWith("<GameStartup>", StringComparison.Ordinal))));

    const int FramesForFallback = 10; // the game raises UnobservedFault a frame or so after its startup error

    // The failure, once it's in: at once when the finalizer kept it, else after up to 10 frames for the fallback. Null if
    // neither came.
    public static async Task<Fault?> Failure(Node node)
    {
        for (var i = 0; Volatile.Read(ref _fault) == null && i < FramesForFallback; i++)
            await node.ToSignal(node.GetTree(), SceneTree.SignalName.ProcessFrame);
        return Volatile.Read(ref _fault);
    }

    const int ChainMost = 16;

    // An exception and every one inside it (InnerException, an AggregateException's each), at most 16, cycles guarded, each
    // after the ones inside it: the innermost, nearest the cause, first. Each with its frames as they are now.
    public static List<Link> Chain(Exception error)
    {
        var links = new List<Link>();
        var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        void Add(Exception e)
        {
            if (seen.Count >= ChainMost || !seen.Add(e))
                return;
            IEnumerable<Exception> inside = e is AggregateException all ? all.InnerExceptions : e.InnerException is { } one ? [one] : [];
            foreach (var inner in inside)
                Add(inner);
            links.Add(new Link(e, new StackTrace(e, false).GetFrames()));
        }
        Add(error);
        return links;
    }

    // Which mod each loaded assembly is, by the assembly itself, never by its name alone (a loader and its runtime can
    // share a name's start, and a mod can load a second copy of another's): the mods' own lists (Mod.assemblies, whatever
    // the mod's state: one that failed after patching still has code running), else the folder its file is in, else its
    // name among the DLL files of the mods that ran. One that two mods' ids claim is no mod's, and so are Harmony's and
    // MonoMod's.
    public sealed class OwnerMap
    {
        readonly Dictionary<Assembly, string?> _known = [];
        readonly Dictionary<Assembly, HashSet<string>> _listed = [];
        readonly List<(string Dir, string Id)> _folders;
        readonly HashSet<string> _ran;
        readonly string[] _outside;
        Dictionary<string, HashSet<string>>? _dlls;

        public OwnerMap(IEnumerable<Mod> mods)
        {
            var withIds = mods.Where(m => m.manifest?.id is { Length: > 0 }).ToList();
            foreach (var mod in withIds)
                foreach (var assembly in Compat.Assemblies(mod))
                    (_listed.TryGetValue(assembly, out var ids) ? ids : _listed[assembly] = []).Add(mod.manifest?.id ?? "");
            // Longest first, for mods in folders nested in another's
            _folders = [.. withIds.Where(m => m.path is { Length: > 0 }).Select(m => (Dir(m.path), m.manifest?.id ?? "")).Distinct()
                .OrderByDescending(f => f.Item1.Length)];
            _ran = [.. withIds.Where(RanThisLaunch).Select(m => m.manifest?.id ?? "")];
            // The game's own folder (sts2.dll's) and the runtime's: a mod that bundles a DLL the game has isn't handed the game's
            _outside = [.. new[] { typeof(ModManager).Assembly, typeof(object).Assembly }.Select(LocationOf).Where(l => l.Length > 0)
                .Select(l => Dir(Path.GetDirectoryName(l) ?? l))];
        }

        public string? Of(Assembly assembly) => _known.TryGetValue(assembly, out var known) ? known : _known[assembly] = Find(assembly);

        // The loaded assemblies that are a mod's, with its id
        public List<(Assembly Assembly, string Id)> Owned()
        {
            var owned = new List<(Assembly, string)>();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                if (Of(assembly) is { Length: > 0 } id)
                    owned.Add((assembly, id));
            return owned;
        }

        string? Find(Assembly assembly)
        {
            if (IsHarmony(assembly))
                return null;
            if (_listed.TryGetValue(assembly, out var ids))
                return ids.Count == 1 ? ids.First() : null; // two mods' ids claim it
            var location = LocationOf(assembly);
            if (location.Length > 0 && _folders.Where(f => Under(location, f.Dir)).Select(f => f.Id).FirstOrDefault() is { } id)
                // A DLL several mods bundle loads once, from whichever folder comes first, so its folder says nothing
                return Dlls().TryGetValue(Path.GetFileName(location).ToLowerInvariant(), out var named) && named.Any(other => other != id) ? null : id;
            return ByName(assembly.GetName().Name + ".dll", location, Dlls(), _outside);
        }

        // Each DLL file name under the folders of the mods that ran this launch, with the ids whose folders have one (a file
        // in a nested mod's folder is that mod's alone), listed once, when the first assembly needs it. A mod that's off
        // loads nothing from its folder, so its files name no one.
        Dictionary<string, HashSet<string>> Dlls()
        {
            if (_dlls != null)
                return _dlls;
            _dlls = [];
            foreach (var (dir, id) in _folders.Where(f => _ran.Contains(f.Id)))
            {
                try
                {
                    foreach (var file in Directory.EnumerateFiles(dir, "*.dll", SearchOption.AllDirectories))
                        if (_folders.First(f => Under(file, f.Dir)).Id == id)
                            (_dlls.TryGetValue(Path.GetFileName(file).ToLowerInvariant(), out var ids) ? ids : _dlls[Path.GetFileName(file).ToLowerInvariant()] = []).Add(id);
                }
                catch (Exception e)
                {
                    Log.Warn($"[{ModEntry.ModId}] Could not list {id}'s DLL files, so an assembly of it loaded from elsewhere may not be found as its: {e.Message}");
                }
            }
            return _dlls;
        }

        // The rule for an assembly loaded from bytes (no file) or from outside its mod's folder: the one id whose folders have
        // a DLL of its name (Dlls: of the mods that ran). None under the game's folder or the runtime's.
        public static string? ByName(string name, string? location, IReadOnlyDictionary<string, HashSet<string>> dlls, IReadOnlyList<string> outside) =>
            location is { Length: > 0 } file && outside.Any(dir => Under(file, dir)) ? null
            : dlls.TryGetValue(name.ToLowerInvariant(), out var ids) && ids.Count == 1 ? ids.First() : null;

        static string Dir(string path) => Full(path).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;

        static bool Under(string file, string dir) => Full(file).StartsWith(dir, StringComparison.OrdinalIgnoreCase);

        static string Full(string path)
        {
            try
            {
                return Path.GetFullPath(path);
            }
            catch (Exception)
            {
                return path; // a path the system can't read, as it is
            }
        }
    }

    // Where an assembly was loaded from, or "" (a dynamic one, or one from bytes)
    static string LocationOf(Assembly assembly)
    {
        try
        {
            return assembly.IsDynamic ? "" : assembly.Location;
        }
        catch (Exception)
        {
            return "";
        }
    }

    // Harmony's and MonoMod's own code (the game's 0Harmony holds both, and a mod may load its own copy) and Harmony's
    // shared state: never a mod's, wherever it loaded from
    static bool IsHarmony(Assembly assembly) => assembly.GetName().Name is { } name
        && (name is "0Harmony" or "HarmonySharedState" || name.StartsWith("MonoMod", StringComparison.Ordinal));

    // One installed copy as the rules read it: its id, source and which copy (Copies.CopyOf), and whether its code ran this
    // launch (RanThisLaunch)
    public record Copy(string Id, ModSource Source, string Key, bool Ran);

    // Whether a mod's code ran this launch: loaded, or failed after its assemblies loaded
    static bool RanThisLaunch(Mod mod) => mod.state == ModLoadState.Loaded || mod.state == ModLoadState.Failed && Compat.Assemblies(mod).Any();

    // What the rules read about the mods: whose each loaded assembly is, the installed copies, and what each id needs (its
    // manifests, and the mods its assemblies reference: a mod can build on a library without declaring it)
    public record Context(OwnerMap Owners, IReadOnlyList<Copy> Installs, IReadOnlyDictionary<string, IReadOnlySet<string>> Needs)
    {
        public bool Ran(string id) => Installs.Any(c => c.Id == id && c.Ran);

        // Whether one id needs another, directly or through others
        public bool Requires(string id, string other)
        {
            var (seen, todo) = (new HashSet<string> { id }, new Stack<string>());
            todo.Push(id);
            while (todo.TryPop(out var at))
            {
                IEnumerable<string> next = Needs.TryGetValue(at, out var needs) ? needs : [];
                foreach (var need in next)
                {
                    if (need == other)
                        return true;
                    if (seen.Add(need))
                        todo.Push(need);
                }
            }
            return false;
        }

        // The mods that ran this launch and need one
        public List<string> Users(string id) => [.. Installs.Where(c => c.Ran && c.Id != id).Select(c => c.Id).Distinct().Where(user => Requires(user, id))];

        // A library: a mod that ran this launch needs it
        public bool IsLibrary(string id) => Users(id).Count > 0;

        // The game's mods now: every copy listed at launch and installed since, the owner map over the loaded assemblies
        public static Context Live()
        {
            var owners = new OwnerMap(ModManager.Mods);
            var installed = ModManager.Mods.Union(Installed.Mods).Where(m => m.manifest?.id is { Length: > 0 }).ToList();
            var copies = installed.Select(m => new Copy(m.manifest?.id ?? "", m.modSource, SpireModManager.Copies.CopyOf(m), RanThisLaunch(m))).ToList();
            var manifests = installed.Select(m => (m.manifest?.id ?? "", (IEnumerable<string>)Dependencies.Read(m)));
            return new Context(owners, copies, NeedsOf(manifests, owners.Owned()));
        }
    }

    // Each id's needs: the ids its manifests list, and the mods whose assemblies its own assemblies (every one the owner
    // map gives it, not only the ones it registered: a loader mod registers only its loader) reference, by simple name
    public static Dictionary<string, IReadOnlySet<string>> NeedsOf(IEnumerable<(string Id, IEnumerable<string> Listed)> manifests,
        IReadOnlyList<(Assembly Assembly, string Id)> owned)
    {
        var needs = new Dictionary<string, HashSet<string>>();
        HashSet<string> Of(string id) => needs.TryGetValue(id, out var set) ? set : needs[id] = [];
        foreach (var (id, listed) in manifests)
            Of(id).UnionWith(listed.Where(need => need != id));
        var byName = owned.GroupBy(o => o.Assembly.GetName().Name ?? "").Where(g => g.Select(o => o.Id).Distinct().Count() == 1)
            .ToDictionary(g => g.Key, g => g.First().Id);
        foreach (var (assembly, id) in owned)
            foreach (var reference in References(assembly))
                if (byName.TryGetValue(reference, out var other) && other != id)
                    Of(id).Add(other);
        return needs.ToDictionary(p => p.Key, p => (IReadOnlySet<string>)p.Value);
    }

    static IEnumerable<string> References(Assembly assembly)
    {
        try
        {
            return [.. assembly.GetReferencedAssemblies().Select(n => n.Name).OfType<string>()];
        }
        catch (Exception)
        {
            return [];
        }
    }

    // A type's mod: the self-test's mapping first (DEVTOOLS; "" is the game's), else its assembly's
    static string? ModOfType(Type? type, OwnerMap owners)
    {
        if (type == null)
            return null;
#if DEVTOOLS
        if (TestOwner is { } test)
            for (var t = type; t != null; t = t.DeclaringType)
                if (test(t) is { } id)
                    return id;
#endif
        return owners.Of(type.Assembly);
    }

    // One frame read: a patched method's replacement by Harmony's map of its originals (on .NET the method itself when it
    // isn't one; one of Harmony's own, which PatchingFailurePatch patches, as that method), else by its type's mod, else by
    // its namespace. Null: the runtime gave no method (a dynamic one).
    public static Frame? Resolve(StackFrame frame, OwnerMap owners)
    {
        if (frame.GetMethod() is not { } method)
            return null;
        if (method is MethodInfo info && OriginalOf(info) is { } original)
            return original.DeclaringType is { } harmony && IsHarmony(harmony.Assembly) ? new Frame(Whose.Runtime, null, original, null)
                : ModOfType(original.DeclaringType, owners) is { Length: > 0 } of ? new Frame(Whose.Mod, of, method, original)
                : new Frame(Whose.Replacement, null, method, original);
        if (ModOfType(method.DeclaringType, owners) is { } mod)
            return new Frame(mod.Length > 0 ? Whose.Mod : Whose.Game, mod.Length > 0 ? mod : null, method, null);
        return new Frame(WhoseIs(method), null, method, null);
    }

    static MethodBase? OriginalOf(MethodInfo replacement)
    {
        try
        {
            return Harmony.GetOriginalMethod(replacement) is { } original && original != replacement ? original : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    static Whose WhoseIs(MethodBase method)
    {
        if (method.DeclaringType is not { } type || IsHarmony(type.Assembly))
            return Whose.Runtime;
        var space = type.Namespace ?? "";
        if (space.StartsWith("System.Reflection", StringComparison.Ordinal) || type == typeof(RuntimeMethodHandle) || type == typeof(Activator)
            || type.FullName == "System.RuntimeType" && method.Name.Contains("CreateInstance"))
            return Whose.Reflection;
        return space is "System" or "Microsoft" || space.StartsWith("System.", StringComparison.Ordinal) || space.StartsWith("Microsoft.", StringComparison.Ordinal)
            ? Whose.Runtime : Whose.Game;
    }

    // The ids changed since the last working launch: new, on again, updated, another copy (none without either launch)
    public static HashSet<string> Changed(LaunchGuard.Setup? planned, LaunchGuard.Setup? working) => planned == null || working == null ? []
        : [.. LaunchGuard.Changes(planned.Mods, working.Mods)
            .Where(c => c.Kind is LaunchGuard.Kind.New or LaunchGuard.Kind.Enabled or LaunchGuard.Kind.Updated or LaunchGuard.Kind.Switched).Select(c => c.Id)];

    // Both launches say which game they ran on, and it's the same one. A change is a sign of the cause only then: after a
    // game update, the mod updated that day is most likely the one made for it.
    public static bool SameGame(LaunchGuard.Setup? planned, LaunchGuard.Setup? working) =>
        planned?.Game is { } now && working?.Game is { } then && now == then;

    // A copy's switch as the game read it: on unless an entry of its id and source is off (a copy with no entry loads, as
    // the game loads it). After the first main menu the caller passes the list as it is.
    public static bool OnAsRead(string id, ModSource source, IReadOnlyList<(string Id, ModSource Source, bool On)>? read) =>
        read?.Any(e => e.Id == id && e.Source == source && !e.On) != true;

    static bool Ours(string id) => id is ModEntry.ModId or ModDoctor.ModId;

    // One ByFrame's state: the chain with its frames read, what changed, and what it found along the way
    sealed class Scan(IReadOnlyList<Link> chain, LaunchGuard.Setup? planned, LaunchGuard.Setup? working, Context mods)
    {
        public readonly Context Mods = mods;
        public readonly List<(Link Link, List<Frame> Frames)> Links = [.. chain.Select(l => (l, l.Frames.Select(f => Resolve(f, mods.Owners)).OfType<Frame>().ToList()))];
        public readonly HashSet<string> ChangedIds = Changed(planned, working);
        public readonly bool Same = SameGame(planned, working);
        public readonly HashSet<string> Named = [];
        public readonly List<string> Shortlist = [];
        public readonly List<string> Notes = [];
        public Check Check;
        public bool NoPatcher;
        public bool Rebuild; // Harmony's build of a patched method failed
        public MethodBase? Rebuilt; // a failed rebuild's original, as Harmony's message names it

        // Changed, on the same game
        public bool Moved(string id) => Same && ChangedIds.Contains(id);

        public void List(IEnumerable<string> ids)
        {
            foreach (var id in ids)
                if (!Shortlist.Contains(id))
                    Shortlist.Add(id);
        }
    }

    // What the failure's frames say (section 2 of the design): the three shapes, each from the whole chain, then each
    // exception's frames, innermost first. Checked picks come from the shapes and the probe; frame picks from the rest.
    public static Reading ByFrame(IReadOnlyList<Link> chain, LaunchGuard.Setup? planned, LaunchGuard.Setup? working, Context mods)
    {
        var scan = new Scan(chain, planned, working, mods);
        foreach (var (_, frames) in scan.Links)
            foreach (var frame in frames)
            {
                if (frame is { Whose: Whose.Mod, Mod: { } id })
                    scan.Named.Add(id);
                if (frame.Original is { } original)
                    scan.Named.UnionWith(Patchers(original, scan, ours: true));
            }
        var pick = TypeLoad(scan);
        if (pick == null && scan.Check == Check.None)
            pick = Clash(scan);
        if (pick == null && scan.Check == Check.None)
            pick = HarmonyFailure(scan);
        if (pick == null && scan.Check == Check.None)
            pick = ByFrames(scan);
        if (pick != null)
            scan.Notes.Add($"picked {string.Join(", ", pick.Ids)} ({pick.Why}{(pick.Checked ? ", our check" : "")})");
        scan.Named.UnionWith(scan.Shortlist);
        scan.Named.UnionWith(pick?.Ids ?? []);
        return new Reading(pick, scan.Check, [.. scan.Shortlist], scan.NoPatcher, scan.Named, Signature(scan), Error(scan), scan.Rebuild, scan.Notes);
    }

    // A pick, or ours when it names us
    static Pick PickOf(Why why, string id, bool isChecked = true, string? step = null) =>
        id == ModEntry.ModId ? new Pick(Why.Self, isChecked, [id]) : new Pick(why, isChecked, [id], Step: step);

    // Shape A: a framework (or the game) reading every mod's types met a mod built for another game version
    // (ReflectionTypeLoadException). The mod read is the culprit: the reader's frames are the only ones, and the library
    // rule would turn the reader off with every dependent, the broken mod among them.
    static Pick? TypeLoad(Scan scan)
    {
        if (scan.Links.Select(l => l.Link.Error).OfType<ReflectionTypeLoadException>().FirstOrDefault() is not { } failed)
            return null;
        var loaded = failed.Types.OfType<Type>().ToList();
        if (loaded.Count > 0)
        {
            if (loaded.Select(t => ModOfType(t, scan.Mods.Owners)).FirstOrDefault(id => id is { Length: > 0 }) is not { } read)
                return null;
            scan.Check = Check.Types;
            scan.List([read]);
            return PickOf(Why.Types, read);
        }
        // Every entry null: the mods whose own types can't all load, the one changed when several can't (any game: each is
        // known to be broken)
        var broken = BrokenTypes(scan).Distinct().ToList();
        scan.Notes.Add($"a type load with no type named; mods whose types can't all load: {(broken.Count == 0 ? "none" : string.Join(", ", broken))}");
        if (broken.Count == 0)
            return null;
        scan.Check = Check.Types;
        scan.List(broken);
        return broken.Count == 1 ? PickOf(Why.Types, broken[0]) : broken.Where(scan.ChangedIds.Contains).ToList() is [var one] ? PickOf(Why.Types, one) : null;
    }

    static IEnumerable<string> BrokenTypes(Scan scan)
    {
#if DEVTOOLS
        if (TestTypesBroken is { } test)
            return test();
#endif
        return scan.Mods.Owners.Owned().Where(o => !o.Assembly.IsDynamic && TypesFail(o.Assembly)).Select(o => o.Id);
    }

    static bool TypesFail(Assembly assembly)
    {
        try
        {
            assembly.GetTypes();
            return false;
        }
        catch (ReflectionTypeLoadException)
        {
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    const string DuplicateModel = "MegaCrit.Sts2.Core.Models.Exceptions.DuplicateModelException";
    static readonly Regex Duplicate = new(@"ModelDb already contains ID (?<id>.+?) mapped to type (?<a>.+?), but you are trying to map it to type (?<b>.+?)\. ");
    static readonly Regex DuplicateOne = new(@"^Trying to create a duplicate canonical model of type (?<b>.+?)\. Don't call constructors");

    // Shape B: two mods' content under one id (the game's DuplicateModelException, from the second mod's model: which is
    // second is load order, not cause). The one of the two new or changed since the last working launch, on any game (the
    // sign is their own content); else both are the shortlist. One mod's own model twice, or a type no mod has: the frames.
    static Pick? Clash(Scan scan)
    {
        if (scan.Links.Select(l => l.Link.Error).FirstOrDefault(e => e.GetType().FullName == DuplicateModel) is not { } clash
            || Clashing(clash.Message, scan) is not { } pair)
            return null;
        var (first, second) = (ModOfType(pair.First, scan.Mods.Owners), ModOfType(pair.Second, scan.Mods.Owners));
        if (first is not { Length: > 0 } a || second is not { Length: > 0 } b || a == b)
            return null;
        scan.Check = Check.Clash;
        scan.List([a, b]);
        return new[] { a, b }.Where(scan.ChangedIds.Contains).ToList() is [var changed]
            ? PickOf(Why.Clash, changed) with { Other = changed == a ? b : a, ContentId = pair.Id } : null;
    }

    // The clash's two types and their id. The beta's message names all three. Stable's names the second type alone: its
    // model checks the id (the category and the type's name), so the first is the one ModelDb holds under that id, and
    // that being the same type is one type made twice. Null when they aren't found.
    static (Type First, Type Second, string Id)? Clashing(string message, Scan scan)
    {
        if (Duplicate.Match(message) is { Success: true } both)
            return TypeNamed(both.Groups["a"].Value, scan) is { } a && TypeNamed(both.Groups["b"].Value, scan) is { } b ? (a, b, both.Groups["id"].Value) : null;
        if (DuplicateOne.Match(message) is not { Success: true } one || TypeNamed(one.Groups["b"].Value, scan) is not { } second
            || Held(second) is not { Type: { } first } held || first == second)
            return null;
        return (first, second, held.Id);
    }

    // A model type's content id, and the type of the model ModelDb holds under it; null if ModelDb can't say (not a model)
    static (string Id, Type? Type)? Held(Type type)
    {
#if DEVTOOLS
        if (TestHeld is { } test)
            return test(type);
#endif
        try
        {
            var id = ModelDb.GetId(type);
            return (id.ToString(), ModelDb.GetByIdOrNull<AbstractModel>(id)?.GetType());
        }
        catch (Exception)
        {
            return null;
        }
    }

    // A type the game names by its full name, looked for in each mod's assemblies (ours too)
    static Type? TypeNamed(string name, Scan scan)
    {
        foreach (var (assembly, _) in scan.Mods.Owners.Owned())
        {
            try
            {
                if (assembly.GetType(name, false) is { } type)
                    return type;
            }
            catch (Exception)
            {
                // a name this assembly can't read: the next one
            }
        }
        return null;
    }

    // Whether a frame is a method of a Harmony type (or of a type nested in it); any of its methods with none named
    static bool IsHarmonyFrame(Frame frame, string type, string? method = null) =>
        frame.Method is { DeclaringType: { } declared } found && Outermost(declared).FullName == "HarmonyLib." + type
        && (method == null || found.Name == method);

    static Type Outermost(Type type)
    {
        while (type.DeclaringType is { } outer)
            type = outer;
        return type;
    }

    // Harmony patching: a Patch call, a patch class's processing, or a replacement's build
    static bool Patching(Frame frame) => IsHarmonyFrame(frame, "PatchProcessor", "Patch") || IsHarmonyFrame(frame, "PatchClassProcessor")
        || IsHarmonyFrame(frame, "PatchFunctions", "UpdateWrapper");

    // Harmony building a replacement: where a transpiler's own throw ends its frames
    static bool Building(Frame frame) => new[] { "CodeTranspiler", "MethodCopier", "MethodBodyReader", "MethodCreator", "MethodCreatorTools", "PatchFunctions" }
        .Any(type => IsHarmonyFrame(frame, type));

    // Shape C: a Harmony patch that failed, Harmony's patching on any exception's frames (a wrapper's too: a direct
    // Harmony.Patch has no wrapper of Harmony's, and a transpiler's own throw tops its frames). Whose patches the call
    // was applying is the record on its exception (Attempted: a dependent's where a framework applies them for it);
    // without one, the frames' patching mod (Patcher). Harmony's own checks of a patch call (an inherited method, a
    // target it can't find, a Prepare or TargetMethod that threw) involve no other patch: that mod. A failed rebuild of
    // the replacement can involve the method's earlier patches (Rebuild).
    static Pick? HarmonyFailure(Scan scan)
    {
        if (!scan.Links.Any(l => l.Frames.Any(Patching)))
            return null;
        scan.Check = Check.Harmony;
        var (owners, patcher) = (Attempted(scan), Patcher(scan));
        scan.NoPatcher = owners == null && patcher == null;
        scan.Notes.Add(owners != null ? $"a Harmony patch failed applying {string.Join(" and ", owners)}'s patches, as recorded"
            : $"a Harmony patch failed with no record of whose patches, so by its frames: the patching mod {patcher ?? "not found under Harmony's frames"}");
        scan.Rebuild = scan.Links.Any(l => l.Link.Error is InvalidProgramException || l.Frames.Any(f => IsHarmonyFrame(f, "PatchFunctions", "UpdateWrapper")));
        if (scan.Rebuild)
            return Rebuild(scan, owners, patcher);
        var refused = owners == null ? patcher : owners is [var one] ? one : null;
        scan.List(owners ?? new[] { patcher }.OfType<string>());
        return refused == null ? null : PickOf(Why.Code, refused);
    }

    // The mods whose patches a failed patch call was applying, by the record on its exception (the innermost that has
    // one): its patch class's and its patch methods' (the owner map's). Null without a record, or with one no mod's.
    static List<string>? Attempted(Scan scan)
    {
        if (scan.Links.Select(l => AttemptOn(l.Link.Error)).OfType<PatchAttempt>().FirstOrDefault() is not { } attempt)
            return null;
        var ids = attempt.Methods.Select(m => m.DeclaringType).Prepend(attempt.Class).Select(t => ModOfType(t, scan.Mods.Owners))
            .OfType<string>().Where(id => id.Length > 0).Distinct().ToList();
        return ids.Count > 0 ? ids : null;
    }

    static PatchAttempt? AttemptOn(Exception error)
    {
        try
        {
            return error.Data[AttemptKey] as PatchAttempt;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // The mod whose patch call failed: the mod frames under Harmony's (after its last), walked, in the innermost exception
    // that has some, else the one wrapping it (PatchAll's HarmonyException around Harmony's own)
    static string? Patcher(Scan scan)
    {
        foreach (var (_, frames) in scan.Links)
        {
            var last = frames.FindLastIndex(f => f.Whose == Whose.Runtime && f.Method?.DeclaringType is { } type && IsHarmony(type.Assembly));
            if (last >= 0 && ModsIn(frames.Skip(last + 1)) is { Count: > 0 } mods)
                return Walk(mods, scan);
        }
        return null;
    }

    // A failed rebuild. Harmony saves a method's patches only after their build worked, and its state starts empty each
    // launch, so every saved patch built this launch on this game: the failed build is one that worked plus the new
    // patches being applied, the recorded owners' (else Applying's guess). In order: a transpiler's own throw names its
    // mod; a mod's method that can't compile on its first patch names its mod; the one of the new patches' mod and the
    // method's saved patches' owners changed on the same game; else the new patches' mod, most likely (without them the
    // method built). None when whose patches they were doesn't show.
    static Pick? Rebuild(Scan scan, IReadOnlyList<string>? owners, string? patcher)
    {
        if (TranspilerThrew(scan) is { } threw)
            return PickOf(Why.Code, threw);
        var originals = Originals(scan);
        scan.Rebuilt = originals?.FirstOrDefault();
        var step = scan.Rebuilt is { } named ? StepOf(named) : null;
        scan.Notes.Add(originals == null ? "a failed rebuild by a direct Harmony.Patch (no original named)"
            : $"a failed rebuild of {(originals.Count == 0 ? "a method not found" : string.Join(", ", originals.Select(StepOf).Distinct()))}");
        foreach (var original in originals ?? [])
            if (ModOfType(original.DeclaringType, scan.Mods.Owners) is { Length: > 0 } of && PatchesOn(original).Count == 0 && CantCompile(original))
            {
                scan.Notes.Add($"{StepOf(original)} can't compile on its first patch");
                scan.List([of]);
                return PickOf(Why.Stale, of, step: step);
            }
        var applying = owners == null ? Applying(scan, patcher, originals) : owners is [var one] ? one : null;
        var shortlist = (owners ?? new[] { applying ?? patcher }.OfType<string>()).Concat((originals ?? []).SelectMany(o => Patchers(o, scan, ours: false))).Distinct().ToList();
        scan.List(shortlist);
        if (applying == null)
            return null; // the one change is the new patches' mod's, and it's unknown
        if (shortlist.Where(scan.Moved).ToList() is [var changed])
            return PickOf(Why.Rebuild, changed, step: step);
        return PickOf(Why.RebuildLast, applying, step: step); // turning it off (with its dependents) gives the method the build that worked
    }

    // Without a record, whose new patches the failed build had, by the frames and the patch classes: the patching mod's. A
    // library (a running mod needs it) also applies its dependents' patch classes, and wrote none of them, so for one it's
    // whichever of it and its dependents has a patch class on the rebuilt method that Harmony didn't save (Applied); none
    // when that doesn't show, as for its direct Harmony.Patch, which names no method.
    static string? Applying(Scan scan, string? patcher, IReadOnlyList<MethodBase>? originals)
    {
        if (patcher == null || patcher == ModEntry.ModId || !scan.Mods.IsLibrary(patcher))
            return patcher;
        var applied = originals is { Count: > 0 } ? Applied(patcher, originals, scan) : null;
        scan.Notes.Add($"the patching mod {patcher} is a library: " + (applied == null ? "whose patch it applied doesn't show" : $"the patch it applied is {applied}'s"));
        return applied;
    }

    // The one of a library and the running mods that need it with a patch class on these methods that Harmony didn't save
    // (it saves a class's patches only after their build worked), by the classes' attributes. None when none or several
    // have one, and when the one's classes there may never have been tried (a Prepare can say no, and PatchAll leaves out
    // a [HarmonyPatchCategory] class), as such a class never stands alone.
    static string? Applied(string library, IReadOnlyList<MethodBase> originals, Scan scan)
    {
        var mods = scan.Mods.Users(library).Append(library).ToHashSet();
        var saved = originals.SelectMany(PatchesOn).Select(p => (p.Module, p.MetadataToken)).ToHashSet();
        var (owners, tried) = (new HashSet<string>(), new HashSet<string>());
        foreach (var (assembly, id) in scan.Mods.Owners.Owned())
            if (!assembly.IsDynamic && Holds(id, mods))
                foreach (var type in AccessTools.GetTypesFromAssembly(assembly))
                {
                    if (ModOfType(type, scan.Mods.Owners) is not { } owner || !mods.Contains(owner) || PatchClassOf(type, originals) is not { } patches
                        || patches.On.Count == 0 || patches.On.Any(p => saved.Contains((p.Module, p.MetadataToken))))
                        continue;
                    owners.Add(owner);
                    if (!patches.MaySkip)
                        tried.Add(owner);
                }
        return owners.Count == 1 && tried.Count == 1 ? owners.First() : null;
    }

    // Whether an assembly of this mod can hold a type of these mods: its own (the self-test maps its stand-ins by type)
    static bool Holds(string owner, IReadOnlySet<string> mods)
    {
#if DEVTOOLS
        if (TestOwner != null)
            return true;
#endif
        return mods.Contains(owner);
    }

    // A patch class as Applied reads it: its patch methods on the methods asked about; MaySkip, Harmony may never have
    // tried it (a Prepare, a category)
    record PatchClass(List<MethodInfo> On, bool MaySkip);

    // The kinds of patch method Harmony applies to the original (a reverse patch patches a copy), by name or attribute
    static readonly string[] PatchKinds = ["Prefix", "Postfix", "Transpiler", "Finalizer", "InnerPrefix", "InnerPostfix"];

    // A type as a patch class: one with Harmony's attributes on it or on a method (else null), its patch methods told as
    // Harmony tells them. Harmony's attributes are looked for first without making them: every type of the mods is asked.
    static PatchClass? PatchClassOf(Type type, IReadOnlyList<MethodBase> originals)
    {
        try
        {
            var methods = AccessTools.GetDeclaredMethods(type);
            if (!HasHarmony(type) && !methods.Any(HasHarmony))
                return null;
            var outer = HarmonyMethodExtensions.GetMergedFromType(type);
            var on = methods.Where(m => PatchKinds.Any(kind => Kind(m, kind)) && Target(outer, m) is { } target && originals.Contains(target)).ToList();
            return new(on, methods.Any(m => Kind(m, "Prepare")) || outer.category is { Length: > 0 });
        }
        catch (Exception)
        {
            return null;
        }
    }

    static bool HasHarmony(MemberInfo member) => member.GetCustomAttributesData().Any(a => a.AttributeType.Namespace == "HarmonyLib");

    static bool Marked(MemberInfo member, string kind) => member.GetCustomAttributesData().Any(a => a.AttributeType.FullName == "HarmonyLib.Harmony" + kind);

    // A method of a kind Harmony looks for in a patch class: by its name or its attribute ([HarmonyPrefix], [HarmonyPrepare])
    static bool Kind(MethodInfo method, string kind) => method.Name == kind || Marked(method, kind);

    // The method a patch method's attributes name, found as Harmony finds it (a method, a property's getter or setter, an
    // indexer's, a constructor, an async method's or an iterator's state machine); null where the one named isn't there,
    // where they name none (TargetMethod's class), or name a kind this doesn't find
    static MethodBase? Target(HarmonyMethod outer, MethodInfo method)
    {
        try
        {
            var patch = outer.Merge(HarmonyMethodExtensions.GetMergedFromMethod(method));
            if (patch.declaringType is not { } type)
                return null;
            var normal = patch.methodType is null or MethodType.Normal or MethodType.Async or MethodType.Enumerator;
            var named = normal && patch.methodName is { Length: > 0 } name ? AccessTools.DeclaredMethod(type, name, patch.argumentTypes) : null;
            return patch.methodType switch
            {
                MethodType.Getter => patch.methodName is { Length: > 0 } getter ? AccessTools.DeclaredPropertyGetter(type, getter)
                    : AccessTools.DeclaredIndexerGetter(type, patch.argumentTypes),
                MethodType.Setter => patch.methodName is { Length: > 0 } setter ? AccessTools.DeclaredPropertySetter(type, setter)
                    : AccessTools.DeclaredIndexerSetter(type, patch.argumentTypes),
                MethodType.Constructor => AccessTools.DeclaredConstructor(type, patch.argumentTypes),
                MethodType.StaticConstructor => AccessTools.GetDeclaredConstructors(type).FirstOrDefault(c => c.IsStatic),
                MethodType.Async => named == null ? null : AccessTools.AsyncMoveNext(named),
                MethodType.Enumerator => named == null ? null : AccessTools.EnumeratorMoveNext(named),
                null or MethodType.Normal => named,
                _ => null,
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    // A transpiler's own throw (eager, inside reflection's TargetInvocationException; an iterator's, where its output is
    // read): the innermost exception's mod frames before Harmony's build frames, walked (a library helper it called gives
    // its mod). Harmony's helpers above it (CodeMatcher) are the runtime's.
    static string? TranspilerThrew(Scan scan)
    {
        if (!scan.Links.Any(l => l.Frames.Any(f => IsHarmonyFrame(f, "CodeTranspiler", "GetResult"))))
            return null;
        var mods = ModsIn(scan.Links[0].Frames.TakeWhile(f => !Building(f)));
        return mods.Count > 0 ? Walk(mods, scan) : null;
    }

    const string PatchingIn = "Patching exception in method ";

    // The original a patch class's failure names (Harmony's "Patching exception in method " and its FullDescription),
    // found by MethodsNamed. Null: no message names one (a direct Harmony.Patch call). None: "null" (a target Harmony
    // didn't find), or one not found.
    static List<MethodBase>? Originals(Scan scan)
    {
        if (scan.Links.Select(l => l.Link.Error).OfType<HarmonyException>().FirstOrDefault(e => e.Message.StartsWith(PatchingIn, StringComparison.Ordinal)) is not { } failed)
            return null;
        var described = failed.Message[PatchingIn.Length..];
        return described == "null" ? [] : MethodsNamed(described, scan);
    }

    // The methods Harmony's FullDescription of one names (namespace and name, no declaring type, so a state machine's
    // MoveNext reads "Ns.<OnPlay>d__5"), found by the candidates' own: the methods Harmony patched, else every method and
    // constructor of the mods' assemblies (a first patch), several that match together
    static List<MethodBase> MethodsNamed(string described, Scan scan) =>
        Patched().Where(m => Matches(m, described)).ToList() is { Count: > 0 } patched ? patched : ModMethods(described, scan);

    // The same among the mods' assemblies alone
    static List<MethodBase> ModMethods(string described, Scan scan)
    {
        const BindingFlags Every = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var name = NameIn(described);
        return [.. scan.Mods.Owners.Owned().Where(o => !o.Assembly.IsDynamic).SelectMany(o => AccessTools.GetTypesFromAssembly(o.Assembly))
            .SelectMany(t => t.GetMember(name, MemberTypes.Method | MemberTypes.Constructor, Every).OfType<MethodBase>()).Where(m => Matches(m, described))];
    }

    static bool Matches(MethodBase method, string described) => method.Name == NameIn(described) && Described(method) == described;

    // The method's name in its FullDescription: after the declaring type's "::", before its parameters
    static string NameIn(string described)
    {
        var paren = described.IndexOf('(');
        var start = paren < 0 ? -1 : described.LastIndexOf("::", paren, StringComparison.Ordinal);
        return paren < 0 ? described : start >= 0 ? described[(start + 2)..paren] : described[(described.LastIndexOf(' ', paren) + 1)..paren];
    }

    static string Described(MethodBase method)
    {
        try
        {
            return method.FullDescription();
        }
        catch (Exception)
        {
            return "";
        }
    }

    static List<MethodBase> Patched()
    {
        try
        {
            return [.. Harmony.GetAllPatchedMethods()];
        }
        catch (Exception)
        {
            return [];
        }
    }

    // A method's patch methods, every list (Harmony's inner ones too); none if it has none, or Harmony can't say
    static List<MethodInfo> PatchesOn(MethodBase original)
    {
        try
        {
            return Harmony.GetPatchInfo(original) is { } info
                ? [.. new[] { info.Prefixes, info.Postfixes, info.Transpilers, info.Finalizers, info.InnerPrefixes, info.InnerPostfixes }
                    .SelectMany(list => list).Select(p => p.PatchMethod).OfType<MethodInfo>()]
                : [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    // The mods that patch a method. Ours and Mod Doctor's are left out (ours: false) for most errors: both run every patch
    // body in try/catch, so nothing escapes one once it's entered (the probe is for one that can't be entered).
    static List<string> Patchers(MethodBase original, Scan scan, bool ours) =>
        [.. PatchesOn(original).Select(p => ModOfType(p.DeclaringType, scan.Mods.Owners)).OfType<string>().Where(id => id.Length > 0 && (ours || !Ours(id))).Distinct()];

    // The kinds of error a method that can't compile on this game gives: a member, type or file it uses gone
    static bool LoadError(Exception error) =>
        error is MissingMemberException or TypeLoadException or FileNotFoundException or FileLoadException or BadImageFormatException;

    // Whether a method can't compile here: PrepareMethod in a try. A generic one can't be prepared, and doesn't count.
    static bool CantCompile(MethodBase method)
    {
        try
        {
#if DEVTOOLS
            TestPrepare?.Invoke(method);
#endif
            RuntimeHelpers.PrepareMethod(method.MethodHandle);
            return false;
        }
        catch (Exception e)
        {
            return LoadError(e);
        }
    }

    // A step as the texts and the log name it: "ModelDb.Init"
    static string StepOf(MethodBase method) => $"{method.DeclaringType?.Name}.{method.Name}";

    // The mods of the mod frames, in order, each once
    static List<string> ModsIn(IEnumerable<Frame> frames) =>
        [.. frames.Where(f => f.Whose == Whose.Mod).Select(f => f.Mod).OfType<string>().Distinct()];

    // The walk: the first mod, then each later one that needs it (the mod that called into a library used it). Spire Mod
    // Manager and Mod Doctor count as needed by every mod here alone: others call their APIs.
    static string Walk(IReadOnlyList<string> mods, Scan scan)
    {
        var pick = mods[0];
        foreach (var later in mods.Skip(1))
            if (scan.Mods.Requires(later, pick) || Ours(pick) && !Ours(later))
                pick = later;
        return pick;
    }

    // Each exception's frames, innermost first: its throw site (its first frame that isn't the runtime's); a patched
    // method's replacement there (not in a wrapper, whose own frames are the caller's) by its patches; else its mod frames,
    // walked, with the library rule. No frame resolved at all: the exceptions' text.
    static Pick? ByFrames(Scan scan)
    {
        if (scan.Links.All(l => l.Frames.Count == 0))
            return ByText(scan);
        foreach (var (link, frames) in scan.Links)
        {
            var at = frames.FindIndex(f => f.Whose != Whose.Runtime);
            if (at < 0)
                continue;
            var wrapper = link.Error is TargetInvocationException or TypeInitializationException or AggregateException;
            if (!wrapper && frames[at] is { Original: { } original } site)
            {
                var (pick, done) = AtReplacement(link, site, original, scan);
                if (pick != null)
                    return pick;
                if (done)
                    continue;
            }
            if (ByMods(ModsIn(frames.Skip(at)), scan) is { } found)
                return found;
        }
        return null;
    }

    // A patched method's replacement at the throw site. A load error first goes to the probe: every patch method of it
    // compiled in a try (ours and Mod Doctor's too: a patch that can't compile throws before its try is entered). Then a
    // mod's method is its mod's frame, unless exactly one of its patchers alone changed on the same game; a game method's
    // patchers decide. Done: no pick from this exception, go on to the next one.
    static (Pick? Pick, bool Done) AtReplacement(Link link, Frame site, MethodBase original, Scan scan)
    {
        var step = StepOf(original);
        if (LoadError(link.Error))
        {
            var pins = Probe(original, scan);
            if (pins.Where(id => id != ModEntry.ModId).ToList() is { Count: > 0 } theirs)
            {
                // Several go as one group: the method can't run while any of them is on. Ours waits for the next start,
                // where our dialog still shows.
                scan.Check = Check.Probe;
                scan.List(theirs);
                return (new Pick(Why.Stale, true, theirs, Step: step), true);
            }
            if (pins.Count > 0)
                return (new Pick(Why.Self, true, [ModEntry.ModId], Step: step), true);
            if (site.Whose == Whose.Replacement)
            {
                // A helper a transpiler calls fails the same way, so its owners are the shortlist
                scan.Check = Check.Probe;
                scan.List(TranspilersOf(original).Select(p => ModOfType(p.DeclaringType, scan.Mods.Owners)).OfType<string>()
                    .Where(id => id.Length > 0 && !Ours(id)));
                return (null, true);
            }
        }
        var patchers = Patchers(original, scan, ours: false);
        if (site is { Whose: Whose.Mod, Mod: { } mod })
            return patchers.Prepend(mod).Distinct().Where(scan.Moved).ToList() is [var changed] && changed != mod
                ? (new Pick(Why.Patch, false, [changed], Step: step), true) : (null, false);
        if (patchers.Count == 0)
            return (null, false); // the game's frame
        var picked = patchers.Count == 1 ? patchers[0] : patchers.Where(scan.Moved).ToList() is [var one] ? one : null;
        // A library a patch frame alone names stands only when it changed itself: it patches a startup step for every mod
        if (picked == null || scan.Mods.IsLibrary(picked) && !scan.Moved(picked))
        {
            scan.List(picked == null ? patchers : [picked]);
            return (null, true);
        }
        return (new Pick(Why.Patch, false, [picked], Step: step), true);
    }

    static List<MethodInfo> TranspilersOf(MethodBase original)
    {
        try
        {
            return [.. Harmony.GetPatchInfo(original)?.Transpilers.Select(p => p.PatchMethod).OfType<MethodInfo>() ?? []];
        }
        catch (Exception)
        {
            return [];
        }
    }

    // The probe: the mods whose patch methods on the original can't compile, ours among them, logged
    static List<string> Probe(MethodBase original, Scan scan)
    {
        var pins = PatchesOn(original).Where(CantCompile).Select(p => ModOfType(p.DeclaringType, scan.Mods.Owners)).OfType<string>()
            .Where(id => id.Length > 0).Distinct().ToList();
        scan.Notes.Add($"the probe at {StepOf(original)}: " + (pins.Count == 0 ? "every patch method compiles"
            : $"can't compile: {string.Join(", ", pins)}{(pins.Contains(ModEntry.ModId) && pins.Count > 1 ? " (ours left for the next start)" : "")}"));
        return pins;
    }

    // The mod frames walked; ours, Self. A library (a running mod needs it): the one of its users changed on the same game,
    // else the library when it changed itself, the game changed or there's no working launch, else no pick.
    static Pick? ByMods(IReadOnlyList<string> mods, Scan scan)
    {
        if (mods.Count == 0)
            return null;
        var pick = Walk(mods, scan);
        if (pick == ModEntry.ModId)
            return new Pick(Why.Self, false, [pick]);
        if (!scan.Mods.IsLibrary(pick))
            return new Pick(Why.Code, false, [pick]);
        if (scan.Mods.Users(pick).Where(scan.Moved).ToList() is [var user])
            return new Pick(Why.LibraryUser, false, [user], Library: pick);
        if (scan.ChangedIds.Contains(pick) || !scan.Same)
            return new Pick(Why.Code, false, [pick]);
        scan.List([pick]);
        return null;
    }

    static readonly Regex PatchedLine = new(@"at (?<type>[\w.`+]+)\.(?<method>[\w`<>]+)_Patch\d+\(|DMD<(?<type>[^:>]+)::(?<method>[^>]+)>");

    // No frame resolved (the runtime gave no method for any): a replacement's line in the exceptions' text, its original
    // found among Harmony's patched methods by type and name (every overload's patchers together; not Harmony's own)
    static Pick? ByText(Scan scan)
    {
        foreach (var (link, _) in scan.Links)
            foreach (Match line in PatchedLine.Matches(link.Error.StackTrace ?? ""))
            {
                var (type, name) = (line.Groups["type"].Value, line.Groups["method"].Value);
                var originals = Patched().Where(m => m.DeclaringType is { } declared && declared.FullName == type && m.Name == name && !IsHarmony(declared.Assembly)).ToList();
                if (originals.Count == 0)
                    continue;
                var patchers = originals.SelectMany(o => Patchers(o, scan, ours: false)).Distinct().ToList();
                scan.Notes.Add($"read from the error's text: {type}.{name}, patched by {string.Join(", ", patchers)}");
                var picked = patchers.Count == 1 ? patchers[0] : patchers.Where(scan.Moved).ToList() is [var one] ? one : null;
                if (picked != null && (!scan.Mods.IsLibrary(picked) || scan.Moved(picked)))
                    return new Pick(Why.Patch, false, [picked], Step: StepOf(originals[0]));
                scan.List(picked == null ? patchers : [picked]);
                return null;
            }
        return null;
    }

    // The failure's signature, to tell the same error coming back: the innermost exception's type, its message with digits
    // made alike, its throw site (a replacement read as its original; a failed rebuild's, the original Harmony's message
    // names), and the step (the first replacement's original in the chain)
    static string Signature(Scan scan)
    {
        if (scan.Links.Count == 0)
            return "";
        var (inner, frames) = scan.Links[0];
        var first = frames.FirstOrDefault(f => f.Whose != Whose.Runtime);
        var site = scan.Rebuilt ?? first?.Original ?? first?.Method;
        var step = scan.Links.SelectMany(l => l.Frames).FirstOrDefault(f => f.Original != null)?.Original;
        return string.Join("|", inner.Error.GetType().FullName, Digits.Replace(Cut(FirstLine(inner.Error.Message), 80), "#"),
            site == null ? "" : $"{site.DeclaringType?.FullName}.{site.Name}", step == null ? "" : StepOf(step));
    }

    static readonly Regex Digits = new(@"\d+");

    // The innermost exception's type and first line, for the record ("ArgumentException: You can only patch..")
    static string Error(Scan scan) => scan.Links.Count == 0 ? ""
        : Cut($"{scan.Links[0].Link.Error.GetType().Name}: {FirstLine(scan.Links[0].Link.Error.Message)}", 120);

    static string FirstLine(string text) => text.Split('\n')[0].Trim();

    static string Cut(string text, int most) => text.Length <= most ? text : Menus.CutShort(Menus.Prefix(text, most));

    // Who to offer (section 3 of the design), in order: our checked pick (the shapes, the probe); Mod Doctor's pick whose
    // mod's code threw; our frame pick; Mod Doctor's pick of another reason (or none); the mods updated since the last
    // working launch, when the updates are all the new code on the same game. Mod Doctor's pick counts only for a mod that
    // ran this launch, never ours, and where a check of ours applied with no pick, only for a mod on that check's
    // shortlist, or when Harmony's failure had no mod frame under its own. Read: the switches as the game read them before
    // the first main menu, the list as it is after it. Log: what was weighed, for the support line.
    public static Culprit? Rank(Reading? reading, ModDoctor.Findings? doctor, LaunchGuard.Setup? planned, LaunchGuard.Setup? working,
        IReadOnlyList<(string Id, ModSource Source, bool On)>? read, IReadOnlyList<Recorded> turnedOff, Context mods, List<string>? log = null)
    {
        var ours = reading ?? Reading.None;
        var md = DoctorPick(doctor, mods, log);
        var stands = md != null && (ours.Pick != null || ours.Check == Check.None || ours.NoPatcher || ours.Shortlist.Contains(md.Id));
        if (md != null && !stands)
            log?.Add($"Mod Doctor named {md.Id}, which isn't on the shortlist our {ours.Check} check left ({string.Join(", ", ours.Shortlist)}), so it isn't offered");
        var doctors = stands && md != null ? DoctorsPick(md) : null;
        var pick = ours.Pick is { Checked: true } ? ours.Pick
            : doctors is { Why: Why.DoctorThrew } ? doctors
            : ours.Pick ?? doctors ?? UpdatedPick(ours, planned, working, turnedOff);
        log?.Add($"ours: {Described(ours.Pick)}; Mod Doctor's: {(md == null ? "none" : $"{md.Id} ({md.Reason})")}; offered: {Described(pick)}");
        if (pick == null)
            return null;
        var ids = pick.Ids;
        var agrees = md != null && ids is [var one] && one == md.Id;
        var updates = planned == null || working == null ? []
            : LaunchGuard.Changes(planned.Mods, working.Mods).Where(c => c.Kind == LaunchGuard.Kind.Updated && ids.Contains(c.Id)).ToList();
        return new Culprit(pick.Why, pick.Checked, ids, agrees, agrees ? doctor?.Explanation : null, pick.Library, pick.Other, pick.ContentId,
            pick.Step, updates, Needing(ids, read, mods), DidntStick(ids, read, turnedOff, mods), Streak(turnedOff), ours.Signature, ours.Error, ours.Rebuild);
    }

    static string Described(Pick? pick) => pick == null ? "none" : $"{string.Join(", ", pick.Ids)} ({pick.Why})";

    // Mod Doctor's pick when it counts: a copy of that id ran this launch (the rebuilt list's switch says nothing: a mod's
    // other copy can be the one that ran), and it isn't ours
    static ModDoctor.Pick? DoctorPick(ModDoctor.Findings? doctor, Context mods, List<string>? log)
    {
        if (doctor?.Culprit is not { } pick)
            return null;
        if (pick.Id.Length > 0 && pick.Id != ModEntry.ModId && mods.Ran(pick.Id))
            return pick;
        log?.Add($"Mod Doctor named {pick.Id}, which is ours or didn't run this launch, so its pick and its explanation are left out");
        return null;
    }

    static Pick DoctorsPick(ModDoctor.Pick md) => new(md.Reason switch
    {
        ModDoctor.Reason.Threw => Why.DoctorThrew,
        ModDoctor.Reason.Added => Why.DoctorAdded,
        _ => Why.DoctorUpdated,
    }, false, [md.Id]);

    // (b): the mods updated since the last working launch, when they're all the new code (nothing new, on again or
    // switched: Restore undoes those) on the same game (an unchanged mod can break on a new game as well). The one the
    // failure names, else the one updated, else all of them (the ones it names, if two or more).
    static Pick? UpdatedPick(Reading reading, LaunchGuard.Setup? planned, LaunchGuard.Setup? working, IReadOnlyList<Recorded> turnedOff)
    {
        if (planned == null || working == null || !SameGame(planned, working))
            return null;
        var off = turnedOff.Select(e => e.Id).ToHashSet();
        var changes = LaunchGuard.Changes(planned.Mods, working.Mods).Where(c => !(c.Kind == LaunchGuard.Kind.Disabled && off.Contains(c.Id))).ToList();
        if (changes.Any(c => c.Kind is LaunchGuard.Kind.New or LaunchGuard.Kind.Enabled or LaunchGuard.Kind.Switched))
            return null;
        var updated = changes.Where(c => c.Kind == LaunchGuard.Kind.Updated).Select(c => c.Id).Distinct().ToList();
        if (updated.Count == 0)
            return null;
        var narrowed = updated.Where(id => reading.Named.Contains(id) || reading.Shortlist.Contains(id)).ToList();
        if (narrowed.Count == 1)
            return new Pick(Why.Updated, false, narrowed);
        if (updated.Count == 1)
            return new Pick(Why.Updated, false, updated);
        return new Pick(Why.UpdatedMany, false, narrowed.Count >= 2 ? narrowed : updated);
    }

    // Every mod with a copy on as read, ran or not, that needs a culprit (directly or through others): left on, it fails
    // to load next time. Not the culprits, nor ours.
    public static List<string> Needing(IReadOnlyList<string> ids, IReadOnlyList<(string Id, ModSource Source, bool On)>? read, Context mods) =>
        [.. mods.Installs.Where(c => !ids.Contains(c.Id) && c.Id != ModEntry.ModId && OnAsRead(c.Id, c.Source, read) && ids.Any(id => mods.Requires(c.Id, id)))
            .Select(c => c.Id).Distinct()];

    // A turn-off of a culprit came undone: an entry with no main menu since, and a copy it wrote off on as read and run this
    // launch (for a switch it wrote off with no copy installed then, any copy of that source). The save was checked, so
    // something put the switch back. A copy it never wrote off is a new culprit.
    public static bool DidntStick(IReadOnlyList<string> ids, IReadOnlyList<(string Id, ModSource Source, bool On)>? read,
        IReadOnlyList<Recorded> turnedOff, Context mods) =>
        turnedOff.Any(e => !e.MenuSince && ids.Contains(e.Id) && e.Switches.Any(s => OnAsRead(e.Id, s.Source, read)
            && mods.Installs.Any(c => c.Id == e.Id && c.Source == s.Source && c.Ran && (s.Copies.Count == 0 || s.Copies.Contains(c.Key)))));

    // This failed start's place in a row of them: the turn-offs with no main menu since (one answer's entries share their
    // time), and this one
    public static int Streak(IReadOnlyList<Recorded> turnedOff) => turnedOff.Where(e => !e.MenuSince).Select(e => e.At).Distinct().Count() + 1;

    // A mod's name as the crash texts show it: with its id (NAME_AND_ID, "大黄蜂 (Hornet)") where another installed mod's
    // name holds it, ignoring case, unless the name is the id itself. Plain: English from our own file, for where the
    // game's text couldn't load.
    public static string Named(string id, string name, IEnumerable<string> others, bool plain = false) =>
        name != id && others.Any(other => other.Contains(name, StringComparison.OrdinalIgnoreCase))
            ? LaunchGuard.Our(plain, "SPIRE_MOD_MANAGER.NAME_AND_ID", ("name", name), ("id", id)) : name;

    // The same for an installed mod, by its manifest's name (its id without one), beside every other id's
    public static string Named(string id, bool plain = false)
    {
        var mods = ModManager.Mods.Union(Installed.Mods).Where(m => m.manifest?.id is { Length: > 0 }).ToList();
        var name = mods.FirstOrDefault(m => m.manifest?.id == id)?.manifest?.name ?? id;
        return Named(id, name, mods.Where(m => m.manifest?.id != id).Select(m => m.manifest?.name).OfType<string>(), plain);
    }
}
