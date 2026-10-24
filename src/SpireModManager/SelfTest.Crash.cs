using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json.Nodes;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;

namespace SpireModManager;

// Who stopped the game from starting (Culprits), on real exceptions and real Harmony failures: stand-ins for the fixture
// mods' code (the nested classes below, each mapped to its fixture by Culprits.TestOwner) throw and patch as mods do, and
// ranking runs on made-up findings. Every patch is taken out again in finally. The stand-ins whose frames matter are kept
// out of their callers (NoInlining), so the JIT can't move a throw. Patch classes applied the way PatchAll applies one
// have no class-level [HarmonyPatch]: ModEntry.Init applies every type that has one.
static partial class SelfTest
{
    const string FixesId = "SmmTest.Fixes", UiId = "SmmTest.Ui", ContentId = "SmmTest.Content", LibId = "SmmTest.Lib", OurId = "SmmTest.Ours";
    const string CulpritGame = "0.111.0 (test)", OtherGame = "0.112.0 (test)";
    const int RunReturns = 7341; // what GameLike.Run returns unpatched; ContentPatch.Marked makes it one more
    static readonly string[] CulpritIds = ["SmmTestFixes", "SmmTestLib", "SmmTestUi", "SmmTestContent", "SmmTestMdUser"];

    // Which fixture each stand-in is. The test's own code, and the game-like code it patches, are the game's.
    static readonly Dictionary<Type, string> CulpritOwners = new()
    {
        [typeof(SelfTest)] = "", [typeof(GameLike)] = "",
        [typeof(Fixes)] = "SmmTestFixes", [typeof(FixesPatch)] = "SmmTestFixes", [typeof(FixesBadRun)] = "SmmTestFixes", [typeof(FixesGoodRun)] = "SmmTestFixes",
        [typeof(FixesNoTarget)] = "SmmTestFixes", [typeof(FixesBadAsync)] = "SmmTestFixes", [typeof(FixesBrokenTranspiler)] = "SmmTestFixes",
        [typeof(Lib)] = "SmmTestLib", [typeof(LibPatch)] = "SmmTestLib", [typeof(LibBadPrepare)] = "SmmTestLib",
        [typeof(Ui)] = "SmmTestUi", [typeof(UiPatch)] = "SmmTestUi", [typeof(UiBadBoom)] = "SmmTestUi", [typeof(UiBadFinish)] = "SmmTestUi",
        [typeof(Content)] = "SmmTestContent", [typeof(ContentPatch)] = "SmmTestContent",
        [typeof(Ours)] = ModEntry.ModId, [typeof(OurPatch)] = ModEntry.ModId, [typeof(Doctor)] = ModDoctor.ModId,
        // Hornet's shape names another mod's type in Harmony's message (Skadi's card base, Downfall's): never blamed
        [typeof(BaseCard)] = "SmmTestContent", [typeof(InheritingCard)] = "SmmTestContent",
        [typeof(OtherBaseCard)] = "SmmTestUi", [typeof(OtherInheritingCard)] = "SmmTestUi",
    };

    // Test Fixes' code
    static class Fixes
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Boom() => throw new InvalidOperationException("made up: Test Fixes' own throw");

        // A load error from its own body: where the JIT throws for a member gone that a method's body calls
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Gone() => throw new MissingMethodException("made up: a member its build had");

        // It calls the game, which throws further down
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int Deep() => GameLike.Inner() + 1;

        // Hornet's shape: a patch Harmony refuses before patching anything, the method being inherited by the card given
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Install(Type card) => new Harmony(FixesId).Patch(card.GetMethod(nameof(BaseCard.Act)) ?? throw new MissingMethodException(card.Name, "Act"),
            prefix: new HarmonyMethod(typeof(FixesPatch), nameof(FixesPatch.Prefix)));

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void CallOurs() => Ours.Boom();

        // One of its patch classes applied, as PatchAll applies each
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Apply(Type patches) => new Harmony(FixesId).CreateClassProcessor(patches).Patch();

        // A direct Harmony.Patch
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Patch(MethodBase original, HarmonyMethod? prefix = null, HarmonyMethod? transpiler = null) =>
            new Harmony(FixesId).Patch(original, prefix: prefix, transpiler: transpiler);
    }

    // Test Framework's code: a library Test Content and Test UI need
    static class Lib
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Boom() => throw new InvalidOperationException("made up: Test Framework's own throw");

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void CallUi() => Ui.Boom();

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Install(Type card) => new Harmony(LibId).Patch(card.GetMethod(nameof(BaseCard.Act)) ?? throw new MissingMethodException(card.Name, "Act"),
            prefix: new HarmonyMethod(typeof(LibPatch), nameof(LibPatch.Prefix)));

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Apply(Type patches) => new Harmony(LibId).CreateClassProcessor(patches).Patch();

        // A direct Harmony.Patch, as a framework applies a patch method it's handed (RitsuLib's ModPatcher)
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Patch(MethodBase original, HarmonyMethod transpiler) => new Harmony(LibId).Patch(original, transpiler: transpiler);

        // A framework reading every mod's types: what its read throws (a type load), thrown from its code
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Throw(Exception error) => throw error;
    }

    // Test UI's code
    static class Ui
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Boom() => throw new InvalidOperationException("made up: Test UI's own throw");

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void CallLib() => Lib.Boom();

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int CallFixes() => Fixes.Deep() + 1;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Apply(Type patches) => new Harmony(UiId).CreateClassProcessor(patches).Patch();

        // Its patch handed to Test Framework to apply, from its own code
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void PatchThroughLib(MethodBase original, HarmonyMethod transpiler) => Lib.Patch(original, transpiler);
    }

    // Test Content's code: its model throws what it's given (the game's clash, from its constructor)
    static class Content
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Throw(Exception error) => throw error;
    }

    static class Ours
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Boom() => throw new InvalidOperationException("made up: our own throw");
    }

    static class Doctor
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Boom() => throw new InvalidOperationException("made up: Mod Doctor's own throw");
    }

    // The game's code as far as the rules can tell (mapped ""): the methods mods patch
    static class GameLike
    {
        public static Exception? Throws; // what Run throws from its own body, if anything

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int Run()
        {
            if (Throws is { } error)
                throw error;
            return RunReturns;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int Outer() => Inner() + 1;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int Inner() => throw new InvalidOperationException("made up: the game's own throw");

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static object? Invoker() => typeof(GameLike).GetMethod(nameof(Inner))?.Invoke(null, null);

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int Lookup() => new Dictionary<string, int>()["made up"];

        public static async Task<int> RunAsync()
        {
            await Task.Yield();
            return RunReturns;
        }

        // Each the target of one mod's patch class alone, so whose class a library applied shows
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int Prepare() => RunReturns;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int Finish() => RunReturns;
    }

    class BaseCard
    {
        public virtual void Act() { }
    }

    class InheritingCard : BaseCard;

    class OtherBaseCard
    {
        public virtual void Act() { }
    }

    class OtherInheritingCard : OtherBaseCard;

    // What the transpilers below do, and the switch that makes the throwing ones throw on a build after their first
    static bool _throwAgain;
    static int _counted, _eager, _lazy;

    // Bad code: a pop on an empty stack first, so the build can't compile
    static List<CodeInstruction> Popped(IEnumerable<CodeInstruction> code) => [new CodeInstruction(OpCodes.Pop), .. code];

    static IEnumerable<CodeInstruction> ThrowsAgain(ref int runs, IEnumerable<CodeInstruction> code) =>
        ++runs > 1 && _throwAgain ? throw new InvalidOperationException("made up: the transpiler's pattern is gone") : code;

    static class FixesPatch
    {
        public static void Prefix() { }

        public static IEnumerable<CodeInstruction> Bad(IEnumerable<CodeInstruction> code) => Popped(code);
    }

    static class UiPatch
    {
        public static void Prefix() { }

        public static bool Through() => true; // a prefix that lets the original run

        public static void Postfix() { }

        public static IEnumerable<CodeInstruction> Bad(IEnumerable<CodeInstruction> code) => Popped(code);
    }

    static class ContentPatch
    {
        public static void Prefix() { }

        public static void Postfix() { }

        // A visible effect: Run returns one more, so a build without it shows
        public static IEnumerable<CodeInstruction> Marked(IEnumerable<CodeInstruction> code)
        {
            foreach (var instruction in code)
            {
                if (instruction.opcode == OpCodes.Ldc_I4 && instruction.operand is RunReturns)
                    instruction.operand = RunReturns + 1;
                yield return instruction;
            }
        }

        public static IEnumerable<CodeInstruction> Same(IEnumerable<CodeInstruction> code) => code;

        public static IEnumerable<CodeInstruction> Counted(IEnumerable<CodeInstruction> code)
        {
            _counted++;
            return code;
        }

        // Throws when called (Harmony calls it through reflection)
        public static IEnumerable<CodeInstruction> Eager(IEnumerable<CodeInstruction> code) => ThrowsAgain(ref _eager, code);

        // Throws where its output is read (an iterator)
        public static IEnumerable<CodeInstruction> Lazy(IEnumerable<CodeInstruction> code)
        {
            if (++_lazy > 1 && _throwAgain)
                throw new InvalidOperationException("made up: the transpiler's pattern is gone");
            foreach (var instruction in code)
                yield return instruction;
        }
    }

    static class LibPatch
    {
        public static void Prefix() { }

        public static IEnumerable<CodeInstruction> Eager(IEnumerable<CodeInstruction> code) => ThrowsAgain(ref _eager, code);

        public static IEnumerable<CodeInstruction> Lazy(IEnumerable<CodeInstruction> code)
        {
            if (++_lazy > 1 && _throwAgain)
                throw new InvalidOperationException("made up: the library transpiler's pattern is gone");
            foreach (var instruction in code)
                yield return instruction;
        }
    }

    static class OurPatch
    {
        public static void Prefix() { }
    }

    // Test Fixes' patch classes, applied the way PatchAll applies one
    static class FixesBadRun
    {
        [HarmonyPatch(typeof(GameLike), nameof(GameLike.Run)), HarmonyTranspiler]
        static IEnumerable<CodeInstruction> Bad(IEnumerable<CodeInstruction> code) => Popped(code);
    }

    static class FixesGoodRun
    {
        [HarmonyPatch(typeof(GameLike), nameof(GameLike.Run)), HarmonyPrefix]
        static void Before() { }
    }

    static class FixesNoTarget
    {
        [HarmonyPatch(typeof(GameLike), "NotThere"), HarmonyPrefix]
        static void Before() { }
    }

    static class FixesBadAsync
    {
        [HarmonyPatch(typeof(GameLike), nameof(GameLike.RunAsync), MethodType.Async), HarmonyTranspiler]
        static IEnumerable<CodeInstruction> Bad(IEnumerable<CodeInstruction> code) => Popped(code);
    }

    static class FixesBrokenTranspiler
    {
        [HarmonyPatch(typeof(GameLike), nameof(GameLike.Run)), HarmonyTranspiler]
        static IEnumerable<CodeInstruction> Broken(IEnumerable<CodeInstruction> code) =>
            throw new InvalidOperationException("made up: Test Fixes' new transpiler's pattern is gone");
    }

    static class UiBadBoom
    {
        [HarmonyPatch(typeof(Fixes), nameof(Fixes.Boom)), HarmonyTranspiler]
        static IEnumerable<CodeInstruction> Bad(IEnumerable<CodeInstruction> code) => Popped(code);
    }

    // Test UI's and Test Framework's patch classes, which Test Framework applies
    static class UiBadFinish
    {
        [HarmonyPatch(typeof(GameLike), nameof(GameLike.Finish)), HarmonyTranspiler]
        static IEnumerable<CodeInstruction> Bad(IEnumerable<CodeInstruction> code) => Popped(code);
    }

    static class LibBadPrepare
    {
        [HarmonyPatch(typeof(GameLike), nameof(GameLike.Prepare)), HarmonyTranspiler]
        static IEnumerable<CodeInstruction> Bad(IEnumerable<CodeInstruction> code) => Popped(code);
    }

    static MethodInfo RunMethod => AccessTools.Method(typeof(GameLike), nameof(GameLike.Run));

    static HarmonyMethod By(Type type, string name) => new(type, name);

    // A stand-in's throw, caught; one that throws nothing gives an error saying so, which no rule picks
    static Exception Thrown(Action act)
    {
        try
        {
            act();
        }
        catch (Exception e)
        {
            return e;
        }
        return new InvalidOperationException("the stand-in threw nothing");
    }

    // A launch of the fixtures, each at 1.0 but the versions given, on a game ("": one that doesn't say)
    static LaunchGuard.Setup Launched(string game, params (string Id, string Version)[] versions) => new(DateTime.Now,
        [.. CulpritIds.Select(id => new LaunchGuard.Entry(id, ModSource.ModsDirectory, true,
            versions.Where(v => v.Id == id).Select(v => v.Version).FirstOrDefault() ?? "1.0"))],
        game.Length > 0 ? game : null);

    static Culprits.Context? _culpritMods;

    // The fixtures as the rules read them, made up so the checks don't hang on the sandbox's state: each ran this launch,
    // Test Content and Test UI need Test Framework; and asked for, Test MD User needing Mod Doctor (Local Mod Fixes' shape)
    static Culprits.Context CrashMods(bool mdUser = false, params Culprits.Copy[] more)
    {
        static Culprits.Copy Ran(string id) => new(id, ModSource.ModsDirectory, "l:" + id, true);
        string[] ran = ["SmmTestFixes", "SmmTestLib", "SmmTestUi", "SmmTestContent", ModEntry.ModId, ModDoctor.ModId];
        var needs = new Dictionary<string, IReadOnlySet<string>>
        {
            ["SmmTestContent"] = new HashSet<string> { "SmmTestLib" }, ["SmmTestUi"] = new HashSet<string> { "SmmTestLib" },
        };
        if (mdUser)
            needs["SmmTestMdUser"] = new HashSet<string> { ModDoctor.ModId };
        return (_culpritMods ??= Culprits.Context.Live()) with
        {
            Installs = [.. ran.Concat(mdUser ? ["SmmTestMdUser"] : []).Select(Ran), .. more], Needs = needs,
        };
    }

    static Culprits.Reading ReadOf(Exception error, LaunchGuard.Setup? planned, LaunchGuard.Setup? working, Culprits.Context? mods = null) =>
        Culprits.ByFrame(Culprits.Chain(error), planned, working, mods ?? CrashMods());

    // A reading in a line: its pick and why, or none and its shortlist
    static string PickIn(Culprits.Reading reading) => reading.Pick is { } pick
        ? $"{string.Join("+", pick.Ids)} {pick.Why}" : $"none ({string.Join("+", reading.Shortlist)})";

    // Every patch the checks made, taken out; first a transpiler that throws again told not to, or a rebuild would
    static void UnpatchCulpritChecks()
    {
        _throwAgain = false;
        GameLike.Throws = null;
        foreach (var id in new[] { FixesId, UiId, ContentId, LibId, OurId })
            new Harmony(id).UnpatchAll(id);
    }

    // Each part on its own (one that throws is a FAIL naming it, and the next runs), in this order: the first-patch check
    // needs Test Fixes' Boom patched and taken out by an earlier one
    static async Task TestCrashCulprit(NMainMenu menu, string outDir)
    {
        await CulpritPart(TestCulpritOwners);
        await CulpritPart(TestCulpritFrames);
        await CulpritPart(TestCulpritPatchedFrames);
        await CulpritPart(TestCulpritProbe);
        await CulpritPart(TestCulpritShapes);
        await CulpritPart(TestCulpritHarmony);
        await CulpritPart(TestCulpritRebuild);
        await CulpritPart(TestCulpritRank);
        await CulpritPart(() => TestCulpritDoctorFile(menu, outDir));
        await CulpritPart(TestCulpritHook);
    }

    // One part, the stand-ins mapped, and every hook and patch put back after it
    static async Task CulpritPart(Func<Task> test, [CallerArgumentExpression(nameof(test))] string name = "")
    {
        Culprits.TestOwner = type => CulpritOwners.GetValueOrDefault(type);
        try
        {
            await test();
        }
        catch (Exception e)
        {
            Results.Add($"FAIL {name} threw: {e}");
            PutBack();
        }
        finally
        {
            Culprits.TestOwner = null;
            Culprits.TestPrepare = null;
            Culprits.TestTypesBroken = null;
            UnpatchCulpritChecks();
        }
    }

    static Task TestCulpritOwners()
    {
        // The name rule, for an assembly loaded from bytes or from outside its mod's folder: one id's folders have a DLL of
        // its name, that mod; two ids', none; one loaded from the game's folder, none (a mod bundling a DLL the game has)
        var dlls = new Dictionary<string, HashSet<string>>
        {
            ["one.dll"] = ["SmmTestFixes"], ["two.dll"] = ["SmmTestFixes", "SmmTestUi"], ["game.dll"] = ["SmmTestUi"],
        };
        var gameDir = Path.Combine(Path.GetTempPath(), "SmmTestGame") + Path.DirectorySeparatorChar;
        string?[] byName =
        [
            Culprits.OwnerMap.ByName("One.dll", "", dlls, [gameDir]), Culprits.OwnerMap.ByName("two.dll", "", dlls, [gameDir]),
            Culprits.OwnerMap.ByName("game.dll", Path.Combine(gameDir, "game.dll"), dlls, [gameDir]),
        ];
        Check("the crash culprit's owner map by name: one mod's folders have the DLL, that mod; two mods', none; loaded from the game's folder, none",
            byName.SequenceEqual(["SmmTestFixes", null, null]), string.Join(", ", byName.Select(id => id ?? "none")));
        // Needs: by manifest, and by an assembly's references (a mod can build on a library without declaring it)
        var byReference = Culprits.NeedsOf([("SmmTestUi", [])], [(typeof(ModEntry).Assembly, "SmmTestUi"), (typeof(ModManager).Assembly, "SmmTestLib")]);
        Check("the crash culprit's needs: a mod whose assembly references a library's needs it, though its manifest doesn't say",
            byReference.GetValueOrDefault("SmmTestUi")?.Contains("SmmTestLib") == true, string.Join(", ", byReference.GetValueOrDefault("SmmTestUi") ?? new HashSet<string>()));

        // A copy of our own DLL loaded into a load context of its own from under Test Fixes' folder, as Hornet's loader loads
        // its runtime: Test Fixes', by its folder, while our own assembly, with the same name, stays ours (the map is by
        // assembly, never by name). The same file under Test UI's folder too: no mod's (a DLL several mods bundle). One under
        // both copies' folders of Test Twin (one id): Test Twin's. An earlier run's copies, if its unload hadn't freed them,
        // go first.
        var twins = ModManager.Mods.Where(m => m.manifest?.id == "SmmTestTwin").Select(m => m.path).Where(p => p is { Length: > 0 }).Distinct().ToList();
        if (Dependencies.Find("SmmTestFixes") is not { path: { Length: > 0 } fixesDir } || Dependencies.Find("SmmTestUi") is not { path: { Length: > 0 } uiDir }
            || twins.Count < 2 || typeof(ModEntry).Assembly.Location is not { Length: > 0 } ours)
        {
            Results.Add("SKIP the crash culprit's owner map by folder: no fixture folders, or no file of our own");
            return Task.CompletedTask;
        }
        // Test Retired is off in dev runs: a mod that didn't run
        var retiredDir = Dependencies.Find("SmmTestRetired") is { path: { Length: > 0 } retired, state: not ModLoadState.Loaded } ? retired : null;
        string[] files = [Path.Combine(fixesDir, "compat", "SmmTestCompat.dll"), Path.Combine(uiDir, "compat", "SmmTestCompat.dll"),
            Path.Combine(twins[0], "compat", "SmmTestTwinCompat.dll"), Path.Combine(twins[1], "compat", "SmmTestTwinCompat.dll"),
            .. retiredDir == null ? [] : new[] { Path.Combine(retiredDir, "compat", "SmmTestCompat.dll") }];
        static void Put(string from, string file)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file) ?? ".");
            File.Copy(from, file, overwrite: true);
        }
        static void Remove(string file)
        {
            try
            {
                File.Delete(file);
                if (Path.GetDirectoryName(file) is { } dir && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
            }
            catch (Exception)
            {
                // still mapped: the next run deletes it
            }
        }
        foreach (var file in files)
            Remove(file);
        AssemblyLoadContext? compatContext = null, twinContext = null;
        try
        {
            Put(ours, files[0]);
            compatContext = new AssemblyLoadContext("SmmTestCompat", isCollectible: true);
            var compat = compatContext.LoadFromAssemblyPath(files[0]);
            var map = new Culprits.OwnerMap(ModManager.Mods);
            var (alone, self, harmony) = (map.Of(compat), map.Of(typeof(ModEntry).Assembly), map.Of(typeof(Harmony).Assembly));
            // Its needs come from every assembly the map gives it, not only the ones it registered (here: none): it references sts2,
            // made out to be Test Framework's
            var needs = Culprits.NeedsOf([("SmmTestFixes", [])], [(compat, alone ?? ""), (typeof(ModManager).Assembly, "SmmTestLib")]);
            // The same file under the folder of a mod that's off takes nothing from it: that mod loads nothing
            string? offToo = null;
            if (retiredDir != null)
            {
                Put(ours, files[4]);
                offToo = new Culprits.OwnerMap(ModManager.Mods).Of(compat);
            }
            else
                Results.Add("SKIP the crash culprit's owner map beside a mod that's off: Test Retired ran this launch");
            Put(ours, files[1]);
            var shared = new Culprits.OwnerMap(ModManager.Mods).Of(compat);
            Put(ours, files[2]);
            Put(ours, files[3]);
            twinContext = new AssemblyLoadContext("SmmTestTwinCompat", isCollectible: true);
            var twin = new Culprits.OwnerMap(ModManager.Mods).Of(twinContext.LoadFromAssemblyPath(files[2]));
            Check("the crash culprit's owner map by folder: a DLL a mod loads itself from its own folder is that mod's, also beside the same file in a mod's "
                + "folder that's off; ours stays ours by assembly though the names match, Harmony's is no mod's; the same file under two running mods' "
                + "folders, none; under one mod's two copies' folders, that mod",
                alone == "SmmTestFixes" && (retiredDir == null || offToo == "SmmTestFixes") && self == ModEntry.ModId && harmony == null && shared == null && twin == "SmmTestTwin",
                $"alone {alone}, beside an off mod's {offToo}, ours {self}, Harmony {harmony}, shared {shared}, twin {twin}");
            Check("the crash culprit's needs: read from an assembly the map gives a mod by its folder alone (a loader mod registers only its loader)",
                needs.GetValueOrDefault("SmmTestFixes")?.Contains("SmmTestLib") == true, string.Join(", ", needs.GetValueOrDefault("SmmTestFixes") ?? new HashSet<string>()));
        }
        finally
        {
            compatContext?.Unload();
            twinContext?.Unload();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            foreach (var file in files)
                Remove(file);
        }
        return Task.CompletedTask;
    }

    static Task TestCulpritFrames()
    {
        var (game, same, otherGame) = (Launched(CulpritGame), Launched(CulpritGame), Launched(OtherGame));
        var contentUp = Launched(CulpritGame, ("SmmTestContent", "1.1"));
        var contentUpNewGame = Launched(OtherGame, ("SmmTestContent", "1.1"));
        // A mod's own throw, also through reflection (the TargetInvocationException's own frames are the caller's)
        var own = PickIn(ReadOf(Thrown(Fixes.Boom), game, same));
        var invoked = PickIn(ReadOf(Thrown(() => typeof(Fixes).GetMethod(nameof(Fixes.Boom))?.Invoke(null, null)), game, same));
        Check("the crash culprit by frame: a mod's own throw names it, through reflection too", own == "SmmTestFixes Code" && invoked == "SmmTestFixes Code",
            $"own {own}, invoked {invoked}");

        // Hornet's shape: Harmony refuses the patch before patching anything, its own frames first. The mod that asked is
        // the culprit, by its frame, never the mod whose type the message names (Skadi's card base, Downfall's): another
        // mod's type there can change only the signature's message.
        var hornet = Thrown(() => Fixes.Install(typeof(InheritingCard)));
        var otherCard = Thrown(() => Fixes.Install(typeof(OtherInheritingCard)));
        var (read, readOther) = (ReadOf(hornet, game, same), ReadOf(otherCard, game, same));
        var first = new System.Diagnostics.StackTrace(hornet, false).GetFrame(0)?.GetMethod()?.DeclaringType?.FullName;
        var (signature, otherSignature) = (read.Signature.Split('|'), readOther.Signature.Split('|'));
        Check("the crash culprit, Hornet's shape: Harmony's refusal (its frame first) names the mod whose call it was, checked; not the mod whose type its "
            + "message names, nor on any list; another mod's type there changes nothing in the signature but its message",
            first == "HarmonyLib.PatchProcessor" && hornet is ArgumentException && PickIn(read) == "SmmTestFixes Code" && read.Pick?.Checked == true
            && !read.Named.Contains("SmmTestContent") && PickIn(readOther) == "SmmTestFixes Code" && !readOther.Named.Contains("SmmTestUi")
            && signature.Length == 4 && otherSignature.Length == 4 && signature[0] == otherSignature[0]
            && signature[2] == otherSignature[2] && signature[3] == otherSignature[3],
            $"first frame {first}; {hornet.GetType().Name}; {PickIn(read)} (checked {read.Pick?.Checked}), named {string.Join(", ", read.Named)}; "
            + $"other {PickIn(readOther)}; signatures {read.Signature} | {readOther.Signature}");
        var library = PickIn(ReadOf(Thrown(() => Lib.Install(typeof(InheritingCard))), contentUp, same));
        Check("the crash culprit, Hornet's shape in a library: the library's own patch call is its own code, though a mod that needs it changed",
            library == "SmmTestLib Code", library);

        // The library rule (Test Framework, needed by Test Content and Test UI)
        string[] libraryRule =
        [
            PickIn(ReadOf(Thrown(Ui.CallLib), game, same)), PickIn(ReadOf(Thrown(Lib.CallUi), game, same)),
            PickIn(ReadOf(Thrown(Lib.Boom), contentUp, same)), PickIn(ReadOf(Thrown(Lib.Boom), game, same)),
            PickIn(ReadOf(Thrown(Lib.Boom), otherGame, same)), PickIn(ReadOf(Thrown(Lib.Boom), contentUpNewGame, same)),
        ];
        Check("the crash culprit's library rule: the mod that called into it; a mod it called into; one that needs it changed on the same game, that one; "
            + "nothing changed, no pick; the game changed, the library; one that needs it changed but the game too, the library",
            libraryRule.SequenceEqual(["SmmTestUi Code", "SmmTestUi Code", "SmmTestContent LibraryUser", "none (SmmTestLib)", "SmmTestLib Code", "SmmTestLib Code"]),
            string.Join(" | ", libraryRule));
        // Mod Doctor's own code: a library only where a mod really needs it (the walk's "needed by every mod" is the walk's
        // alone), so offered as itself, or the one dependent changed
        var mdUserUp = Launched(CulpritGame, ("SmmTestMdUser", "1.1"));
        string[] doctor =
        [
            PickIn(ReadOf(Thrown(Doctor.Boom), game, same)), PickIn(ReadOf(Thrown(Doctor.Boom), game, same, CrashMods(mdUser: true))),
            PickIn(ReadOf(Thrown(Doctor.Boom), mdUserUp, same, CrashMods(mdUser: true))), PickIn(ReadOf(Thrown(Doctor.Boom), contentUp, same)),
        ];
        Check("the crash culprit, Mod Doctor's own code: itself when nothing needs it; with a dependent, no pick; that dependent changed, it; "
            + "never a mod that doesn't need it", doctor.SequenceEqual(["ModDoctor Code", "none (ModDoctor)", "SmmTestMdUser LibraryUser", "ModDoctor Code"]),
            string.Join(" | ", doctor));
        // Ours: our own throw; a mod that called into our code takes it
        string[] ourCode = [PickIn(ReadOf(Thrown(Ours.Boom), game, same)), PickIn(ReadOf(Thrown(Fixes.CallOurs), game, same))];
        Check("the crash culprit: our own throw is ours; a mod calling our code that threw, that mod", ourCode.SequenceEqual([$"{ModEntry.ModId} Self", "SmmTestFixes Code"]),
            string.Join(" | ", ourCode));
        // No mod's frame; and the signature's throw site, the first frame not the runtime's (not ThrowHelper, a Dictionary
        // internal or Harmony)
        var lookup = ReadOf(Thrown(() => GameLike.Lookup()), game, same);
        Check("the crash culprit: the game's own throw names no mod; the signature's throw site is the first frame that isn't the runtime's (Hornet's, "
            + "its patch call; a missing key, the method that looked it up)",
            PickIn(ReadOf(Thrown(() => GameLike.Inner()), game, same)) == "none ()"
            && signature.ElementAtOrDefault(2) == $"{typeof(Fixes).FullName}.{nameof(Fixes.Install)}"
            && lookup.Signature.Split('|').ElementAtOrDefault(2) == $"{typeof(GameLike).FullName}.{nameof(GameLike.Lookup)}",
            $"{read.Signature} | {lookup.Signature}");
        return Task.CompletedTask;
    }

    static Task TestCulpritPatchedFrames()
    {
        var (game, same, otherGame) = (Launched(CulpritGame), Launched(CulpritGame), Launched(OtherGame));
        var (contentUp, contentUpNewGame) = (Launched(CulpritGame, ("SmmTestContent", "1.1")), Launched(OtherGame, ("SmmTestContent", "1.1")));
        var (libUp, libUpNewGame) = (Launched(CulpritGame, ("SmmTestLib", "1.1")), Launched(OtherGame, ("SmmTestLib", "1.1")));
        var (uiUp, uiUpNewGame) = (Launched(CulpritGame, ("SmmTestUi", "1.1")), Launched(OtherGame, ("SmmTestUi", "1.1")));
        var unsaid = Launched("");

        // A game method patched by a harmless prefix, its own body throwing: the throw is in its replacement, so its one
        // patcher. A game method that calls an unpatched one that throws, or calls it through reflection: no patch pick.
        new Harmony(UiId).Patch(RunMethod, prefix: By(typeof(UiPatch), nameof(UiPatch.Prefix)));
        new Harmony(UiId).Patch(AccessTools.Method(typeof(GameLike), nameof(GameLike.Outer)), prefix: By(typeof(UiPatch), nameof(UiPatch.Prefix)));
        new Harmony(UiId).Patch(AccessTools.Method(typeof(GameLike), nameof(GameLike.Invoker)), prefix: By(typeof(UiPatch), nameof(UiPatch.Prefix)));
        GameLike.Throws = new InvalidOperationException("made up: the game method's own throw");
        var patched = ReadOf(Thrown(() => GameLike.Run()), game, same);
        string[] around = [PickIn(ReadOf(Thrown(() => GameLike.Outer()), game, same)), PickIn(ReadOf(Thrown(() => GameLike.Invoker()), game, same))];
        Check("the crash culprit at a patched game method: its own throw names its one patcher (the step its name); one it calls, directly or "
            + "through reflection, names none",
            PickIn(patched) == "SmmTestUi Patch" && patched.Pick?.Step == "GameLike.Run" && around.SequenceEqual(["none ()", "none ()"]),
            $"{PickIn(patched)} at {patched.Pick?.Step}; {string.Join(" | ", around)}");
        // A second patcher: the one changed, on the same game only. Ours never counts.
        new Harmony(ContentId).Patch(RunMethod, prefix: By(typeof(ContentPatch), nameof(ContentPatch.Prefix)));
        new Harmony(OurId).Patch(RunMethod, prefix: By(typeof(OurPatch), nameof(OurPatch.Prefix)));
        string[] two =
        [
            PickIn(ReadOf(Thrown(() => GameLike.Run()), game, same)), PickIn(ReadOf(Thrown(() => GameLike.Run()), contentUp, same)),
            PickIn(ReadOf(Thrown(() => GameLike.Run()), contentUpNewGame, same)), PickIn(ReadOf(Thrown(() => GameLike.Run()), contentUp, unsaid)),
        ];
        Check("the crash culprit at a game method two mods patch (and ours): no pick, both on the shortlist; one updated, it; updated but the game too, "
            + "or a last working launch that doesn't say its game, no pick",
            two.SequenceEqual(["none (SmmTestUi+SmmTestContent)", "SmmTestContent Patch", "none (SmmTestUi+SmmTestContent)", "none (SmmTestUi+SmmTestContent)"]),
            string.Join(" | ", two));
        UnpatchCulpritChecks();
        // A library its one patcher: only when it changed itself, on the same game
        new Harmony(LibId).Patch(RunMethod, prefix: By(typeof(LibPatch), nameof(LibPatch.Prefix)));
        GameLike.Throws = new InvalidOperationException("made up: the game method's own throw");
        string[] library =
        [
            PickIn(ReadOf(Thrown(() => GameLike.Run()), game, same)), PickIn(ReadOf(Thrown(() => GameLike.Run()), otherGame, same)),
            PickIn(ReadOf(Thrown(() => GameLike.Run()), libUp, same)), PickIn(ReadOf(Thrown(() => GameLike.Run()), libUpNewGame, same)),
        ];
        Check("the crash culprit at a game method a library patches: no pick with nothing changed or only the game; updated, it; updated but the game too, no pick",
            library.SequenceEqual(["none (SmmTestLib)", "none (SmmTestLib)", "SmmTestLib Patch", "none (SmmTestLib)"]), string.Join(" | ", library));
        UnpatchCulpritChecks();

        // A mod's own method, patched (an add-on's postfix): its replacement is its mod's frame, at the throw site (unless the
        // patcher alone changed, on the same game) and off it (the walk sees it). Hornet's shape patched by a prefix that
        // lets it run: the mod whose call it was.
        new Harmony(UiId).Patch(AccessTools.Method(typeof(Fixes), nameof(Fixes.Boom)), postfix: By(typeof(UiPatch), nameof(UiPatch.Postfix)));
        new Harmony(UiId).Patch(AccessTools.Method(typeof(Fixes), nameof(Fixes.Deep)), postfix: By(typeof(UiPatch), nameof(UiPatch.Postfix)));
        new Harmony(UiId).Patch(AccessTools.Method(typeof(Fixes), nameof(Fixes.Install)), prefix: By(typeof(UiPatch), nameof(UiPatch.Through)));
        string[] modMethod =
        [
            PickIn(ReadOf(Thrown(Fixes.Boom), game, same)), PickIn(ReadOf(Thrown(Fixes.Boom), uiUp, same)), PickIn(ReadOf(Thrown(Fixes.Boom), uiUpNewGame, same)),
            PickIn(ReadOf(Thrown(() => Fixes.Install(typeof(InheritingCard))), game, same)), PickIn(ReadOf(Thrown(() => Ui.CallFixes()), game, same)),
        ];
        Check("the crash culprit at a mod's patched method: its mod; its patcher updated on the same game, the patcher; with the game changed too, its mod; "
            + "Hornet's shape patched by a prefix that lets it run, its mod; called by another mod and throwing further down, its mod",
            modMethod.SequenceEqual(["SmmTestFixes Code", "SmmTestUi Patch", "SmmTestFixes Code", "SmmTestFixes Code", "SmmTestFixes Code"]),
            string.Join(" | ", modMethod));
        return Task.CompletedTask;
    }

    static Task TestCulpritProbe()
    {
        var (game, same, otherGame) = (Launched(CulpritGame), Launched(CulpritGame), Launched(OtherGame));
        var uiUp = Launched(CulpritGame, ("SmmTestUi", "1.1"));
        // A load error at a patched game method's replacement: every patch method of it compiled in a try, ours too (one that
        // can't compile throws before its try is entered). TestPrepare stands in for the ones that can't.
        static bool Is(MethodBase method, Type type, string name) => method.DeclaringType == type && method.Name == name;
        void CantCompile(params (Type Type, string Name, Exception Error)[] broken) => Culprits.TestPrepare = method =>
        {
            foreach (var (type, name, error) in broken)
                if (Is(method, type, name))
                    throw error;
        };
        static MissingMethodException Missing() => new("made up: a member its build had");
        new Harmony(UiId).Patch(RunMethod, prefix: By(typeof(UiPatch), nameof(UiPatch.Prefix)));
        new Harmony(OurId).Patch(RunMethod, prefix: By(typeof(OurPatch), nameof(OurPatch.Prefix)));
        GameLike.Throws = Missing();
        CantCompile((typeof(OurPatch), nameof(OurPatch.Prefix), Missing()));
        var ours = PickIn(ReadOf(Thrown(() => GameLike.Run()), game, same));
        CantCompile((typeof(UiPatch), nameof(UiPatch.Prefix), Missing()));
        var theirs = ReadOf(Thrown(() => GameLike.Run()), game, same);
        CantCompile();
        var neither = PickIn(ReadOf(Thrown(() => GameLike.Run()), game, same));
        CantCompile((typeof(OurPatch), nameof(OurPatch.Prefix), Missing()), (typeof(UiPatch), nameof(UiPatch.Prefix), Missing()));
        var both = ReadOf(Thrown(() => GameLike.Run()), game, same);
        GameLike.Throws = new InvalidOperationException("made up: not a load error");
        var notLoad = PickIn(ReadOf(Thrown(() => GameLike.Run()), game, same));
        Check("the crash culprit's probe: a patch of ours that can't compile, ours; another mod's, it (its code that can't run on this game); none, no pick; "
            + "ours and another's, the other (ours named in the log, for the next start); not a load error, the probe doesn't run and ours isn't counted",
            ours == $"{ModEntry.ModId} Self" && PickIn(theirs) == "SmmTestUi Stale" && theirs.Pick?.Checked == true && neither == "none ()"
            && PickIn(both) == "SmmTestUi Stale" && both.Notes.Any(n => n.Contains(ModEntry.ModId) && n.Contains("next start")) && notLoad == "SmmTestUi Patch",
            $"ours {ours}, theirs {PickIn(theirs)}, neither {neither}, both {PickIn(both)} ({string.Join("; ", both.Notes)}), not a load error {notLoad}");
        UnpatchCulpritChecks();
        // A library's patch that can't compile is its own code, whatever changed, with its dependents (Rank's Needing). Several
        // mods' that can't, one group, whatever the error of each.
        new Harmony(LibId).Patch(RunMethod, prefix: By(typeof(LibPatch), nameof(LibPatch.Prefix)));
        GameLike.Throws = Missing();
        CantCompile((typeof(LibPatch), nameof(LibPatch.Prefix), Missing()));
        var library = new[] { PickIn(ReadOf(Thrown(() => GameLike.Run()), game, same)), PickIn(ReadOf(Thrown(() => GameLike.Run()), otherGame, same)) };
        var needing = Culprits.Rank(ReadOf(Thrown(() => GameLike.Run()), game, same), null, game, same, null, [], CrashMods())?.Needing ?? [];
        UnpatchCulpritChecks();
        new Harmony(UiId).Patch(RunMethod, prefix: By(typeof(UiPatch), nameof(UiPatch.Prefix)));
        new Harmony(ContentId).Patch(RunMethod, prefix: By(typeof(ContentPatch), nameof(ContentPatch.Prefix)));
        GameLike.Throws = Missing();
        CantCompile((typeof(UiPatch), nameof(UiPatch.Prefix), new TypeLoadException("made up: a type its build had")),
            (typeof(ContentPatch), nameof(ContentPatch.Prefix), Missing()));
        var group = PickIn(ReadOf(Thrown(() => GameLike.Run()), uiUp, same));
        Check("the crash culprit's probe: a library's patch that can't compile is its own, with nothing changed or only the game, its dependents with it; "
            + "two mods' patches that can't (a missing type, a missing member), both as one group, whatever changed",
            library.SequenceEqual(["SmmTestLib Stale", "SmmTestLib Stale"]) && needing.Order().SequenceEqual(["SmmTestContent", "SmmTestUi"])
            && group == "SmmTestUi+SmmTestContent Stale",
            $"{string.Join(" | ", library)}; needing {string.Join(", ", needing)}; group {group}");
        UnpatchCulpritChecks();
        // A load error at a mod's patched method (its replacement the throw site): no pin, its mod (most likely a call its
        // body makes); its patcher alone updated on the same game, the patcher; the patcher's patch can't compile, the
        // patcher. The probe never prepares the original itself (MonoMod compiled it when it was patched).
        new Harmony(UiId).Patch(AccessTools.Method(typeof(Fixes), nameof(Fixes.Gone)), postfix: By(typeof(UiPatch), nameof(UiPatch.Postfix)));
        var asked = new List<MethodBase>();
        Culprits.TestPrepare = asked.Add;
        string[] modMethod = [PickIn(ReadOf(Thrown(Fixes.Gone), game, same)), PickIn(ReadOf(Thrown(Fixes.Gone), uiUp, same))];
        var preparedGone = asked.Any(m => Is(m, typeof(Fixes), nameof(Fixes.Gone)));
        CantCompile((typeof(UiPatch), nameof(UiPatch.Postfix), Missing()));
        var pinned = PickIn(ReadOf(Thrown(Fixes.Gone), game, same));
        Check("the crash culprit's probe at a mod's patched method (the load error at its replacement): no pin, its mod; its patcher alone updated, the "
            + "patcher; the patcher's patch can't compile, the patcher; the original itself never prepared",
            modMethod.SequenceEqual(["SmmTestFixes Code", "SmmTestUi Patch"]) && pinned == "SmmTestUi Stale" && !preparedGone,
            $"{string.Join(" | ", modMethod)} | {pinned}; prepared the original {preparedGone}");
        return Task.CompletedTask;
    }

    static Task TestCulpritShapes()
    {
        var (game, same, otherGame) = (Launched(CulpritGame), Launched(CulpritGame), Launched(OtherGame));
        // Shape A: a framework reading every mod's types met one built for another game version. The mod read is the
        // culprit, never the reader: by a type of it that loaded, or with none, the mod whose own types can't all load.
        static ReflectionTypeLoadException Unloaded(Type?[] loaded) => new(loaded, [new TypeLoadException("made up: a type built for another game")]);
        var types = PickIn(ReadOf(Thrown(() => Lib.Throw(Unloaded([typeof(BaseCard), null]))), game, same));
        Culprits.TestTypesBroken = () => ["SmmTestUi"];
        var none = ReadOf(Thrown(() => Lib.Throw(Unloaded([null]))), game, same);
        Culprits.TestTypesBroken = () => ["SmmTestUi", "SmmTestContent"];
        var several = PickIn(ReadOf(Thrown(() => Lib.Throw(Unloaded([null]))), otherGame, Launched(OtherGame, ("SmmTestUi", "0.9"))));
        Culprits.TestTypesBroken = null;
        Check("the crash culprit, a type load: the mod whose type is named, not the reader; none named, the one mod whose types can't all load; "
            + "several, the one changed (on any game: each is broken)",
            types == "SmmTestContent Types" && PickIn(none) == "SmmTestUi Types" && none.Check == Culprits.Check.Types && several == "SmmTestUi Types",
            $"{types} | {PickIn(none)} | {several}");
        // Shape B: two mods' content under one id. The one new since the last working launch; neither, no pick and both on
        // the shortlist; one mod's type twice, the frames. The stable game's exception names one type made twice, never a
        // clash of two: its frames.
        if (typeof(ModManager).Assembly.GetType("MegaCrit.Sts2.Core.Models.Exceptions.DuplicateModelException") is not { } duplicate)
        {
            Results.Add("SKIP the crash culprit, a content clash: this game has no DuplicateModelException");
            return Task.CompletedTask;
        }
        if (duplicate.GetConstructor([typeof(string)]) == null)
        {
            var once = duplicate.GetConstructor([typeof(Type)]) is { } byType
                ? PickIn(ReadOf(Thrown(() => Content.Throw((Exception)byType.Invoke([typeof(BaseCard)]))), game, same)) : "no constructor of a type";
            Check("the crash culprit, the stable game's duplicate model (one type made twice): no clash, the frames (the mod that threw)", once == "SmmTestContent Code", once);
            return Task.CompletedTask;
        }
        Exception Clash(Type first, Type second) => (Exception?)Activator.CreateInstance(duplicate,
            $"ModelDb already contains ID CARD.SMM_TEST mapped to type {first}, but you are trying to map it to type {second}. Possible causes:\n - made up")
            ?? new InvalidOperationException("no clash");
        var uiNew = new LaunchGuard.Setup(DateTime.Now, [.. Launched(CulpritGame).Mods.Where(m => m.Id != "SmmTestUi")], CulpritGame);
        var clash = ReadOf(Thrown(() => Content.Throw(Clash(typeof(BaseCard), typeof(OtherBaseCard)))), Launched(CulpritGame), uiNew);
        var neither = ReadOf(Thrown(() => Content.Throw(Clash(typeof(BaseCard), typeof(OtherBaseCard)))), game, same);
        var twice = PickIn(ReadOf(Thrown(() => Content.Throw(Clash(typeof(BaseCard), typeof(InheritingCard)))), game, same));
        Check("the crash culprit, a content clash: the mod new since the last working launch (the other and the id named); neither changed, no pick, "
            + "both on the shortlist; one mod's own type twice, the frames (the mod that threw)",
            PickIn(clash) == "SmmTestUi Clash" && clash.Pick?.Other == "SmmTestContent" && clash.Pick?.ContentId == "CARD.SMM_TEST"
            && PickIn(neither) == "none (SmmTestContent+SmmTestUi)" && twice == "SmmTestContent Code",
            $"{PickIn(clash)} (other {clash.Pick?.Other}, id {clash.Pick?.ContentId}) | {PickIn(neither)} | {twice}");
        return Task.CompletedTask;
    }

    static Task TestCulpritHarmony()
    {
        // Harmony's behaviour the rebuild rule rests on, with real failing patches. These fail if a Harmony update changes it.
        // H1, saved only after the build: Test Content's transpiler (Run returns one more), then a bad one under another
        // id. That throws, Harmony keeps only the first, and Run runs bare (the failed build's detour went first).
        new Harmony(ContentId).Patch(RunMethod, transpiler: By(typeof(ContentPatch), nameof(ContentPatch.Marked)));
        var marked = GameLike.Run();
        var bad = Thrown(() => Fixes.Patch(RunMethod, transpiler: By(typeof(FixesPatch), nameof(FixesPatch.Bad))));
        var info = Harmony.GetPatchInfo(RunMethod);
        var bare = GameLike.Run();
        Check("Harmony saves a method's patches only after their build worked: a bad transpiler throws, the saved set keeps only the first, its id not "
            + "among the owners, and the method runs as if unpatched",
            marked == RunReturns + 1 && bad is InvalidProgramException && info?.Transpilers.Count == 1 && info.Owners.Contains(ContentId) && !info.Owners.Contains(FixesId)
            && bare == RunReturns,
            $"marked {marked}, {bad.GetType().Name}, transpilers {info?.Transpilers.Count}, owners {string.Join(", ", info?.Owners.AsEnumerable() ?? [])}, bare {bare}");
        // H3, the two shapes: PatchAll's HarmonyException naming the original, with the build's error inside whose first frame
        // is UpdateWrapper; a direct Harmony.Patch, that error bare
        var byClass = Thrown(() => Fixes.Apply(typeof(FixesBadRun)));
        string? FirstFrame(Exception? e) => e == null ? null : new System.Diagnostics.StackTrace(e, false).GetFrame(0)?.GetMethod() is { } m ? $"{m.DeclaringType?.Name}.{m.Name}" : null;
        Check("Harmony's two shapes of a failed build: PatchAll's names the original (\"Patching exception in method \" and its FullDescription), the "
            + "build's error inside starting at UpdateWrapper; a direct Harmony.Patch, the error bare from UpdateWrapper",
            byClass is HarmonyException && byClass.Message == "Patching exception in method " + RunMethod.FullDescription() && byClass.InnerException is InvalidProgramException
            && FirstFrame(byClass.InnerException) == "PatchFunctions.UpdateWrapper" && FirstFrame(bad) == "PatchFunctions.UpdateWrapper",
            $"{byClass.GetType().Name}: {byClass.Message}; inside {byClass.InnerException?.GetType().Name} from {FirstFrame(byClass.InnerException)}; direct from {FirstFrame(bad)}");
        UnpatchCulpritChecks();
        // H2, every build runs the saved transpilers again: one counting its runs, then another mod's prefix
        _counted = 0;
        new Harmony(ContentId).Patch(RunMethod, transpiler: By(typeof(ContentPatch), nameof(ContentPatch.Counted)));
        var once = _counted;
        new Harmony(UiId).Patch(RunMethod, prefix: By(typeof(UiPatch), nameof(UiPatch.Prefix)));
        Check("Harmony runs a method's saved transpilers again at every build: another mod's prefix runs it once more", once == 1 && _counted == 2,
            $"after its own patch {once}, after the prefix {_counted}");
        return Task.CompletedTask;
    }

    static Task TestCulpritRebuild()
    {
        var (game, same, otherGame) = (Launched(CulpritGame), Launched(CulpritGame), Launched(OtherGame));
        var contentUp = Launched(CulpritGame, ("SmmTestContent", "1.1"));
        var bothUp = Launched(CulpritGame, ("SmmTestContent", "1.1"), ("SmmTestFixes", "0.2"));
        var contentUpNewGame = Launched(OtherGame, ("SmmTestContent", "1.1"));
        // Test Content's transpiler saved on Run, then Test Fixes' bad one: the failed build is one that worked plus Test
        // Fixes' new patch. Without it the method built this launch, so it's most likely Test Fixes, unless the one of the
        // two changed on the same game.
        new Harmony(ContentId).Patch(RunMethod, transpiler: By(typeof(ContentPatch), nameof(ContentPatch.Marked)));
        var byClass = Thrown(() => Fixes.Apply(typeof(FixesBadRun)));
        string[] rebuild =
        [
            PickIn(ReadOf(byClass, contentUpNewGame, same)), PickIn(ReadOf(byClass, game, same)), PickIn(ReadOf(byClass, contentUp, same)),
            PickIn(ReadOf(byClass, bothUp, same)),
        ];
        var direct = Thrown(() => Fixes.Patch(RunMethod, transpiler: By(typeof(FixesPatch), nameof(FixesPatch.Bad))));
        var byCall = PickIn(ReadOf(direct, contentUpNewGame, same));
        var signature = ReadOf(byClass, game, same).Signature.Split('|').ElementAtOrDefault(2);
        Check("the crash culprit, a failed rebuild: the patching mod, most likely, with the game changed and the saved transpiler's mod updated, or with "
            + "nothing changed; the saved transpiler's mod alone updated on the same game, it; both updated, the patching mod; the same failure by a direct "
            + "Harmony.Patch, the patching mod the same way; the signature's throw site the rebuilt method",
            rebuild.SequenceEqual(["SmmTestFixes RebuildLast", "SmmTestFixes RebuildLast", "SmmTestContent Rebuild", "SmmTestFixes RebuildLast"])
            && byCall == "SmmTestFixes RebuildLast" && signature == $"{typeof(GameLike).FullName}.{nameof(GameLike.Run)}",
            $"{string.Join(" | ", rebuild)}; direct {byCall}; signature at {signature}");
        // A library's failed rebuild. A framework applies its dependents' patches too and wrote none of them, so it's the pick
        // only for a patch class of its own on the method (one Harmony didn't save), and a dependent for one of the
        // dependent's. A class of neither (Test Fixes', which doesn't need it): no pick on any game, the library and the saved
        // transpiler's mod on the shortlist. Its direct Harmony.Patch names no method: no pick, unless the dependent's own
        // call is in the frames.
        string ByLibrary(Action patch, LaunchGuard.Setup planned) => PickIn(ReadOf(Thrown(patch), planned, same));
        string[] library =
        [
            ByLibrary(() => Lib.Apply(typeof(FixesBadRun)), game), ByLibrary(() => Lib.Apply(typeof(FixesBadRun)), otherGame),
            ByLibrary(() => Lib.Apply(typeof(LibBadPrepare)), game), ByLibrary(() => Lib.Apply(typeof(LibBadPrepare)), otherGame),
            ByLibrary(() => Lib.Apply(typeof(UiBadFinish)), otherGame), ByLibrary(() => Lib.Patch(RunMethod, By(typeof(UiPatch), nameof(UiPatch.Bad))), otherGame),
            ByLibrary(() => Ui.PatchThroughLib(RunMethod, By(typeof(UiPatch), nameof(UiPatch.Bad))), otherGame),
        ];
        Check("the crash culprit, a library's failed rebuild: another mod's patch class, no pick on any game, the library and the saved transpiler's mod on "
            + "the shortlist; its own class, the library; a dependent's class, the dependent; a dependent's patch by its direct Harmony.Patch, no pick, or the "
            + "dependent where its own call is in the frames",
            library.SequenceEqual(["none (SmmTestLib+SmmTestContent)", "none (SmmTestLib+SmmTestContent)", "SmmTestLib RebuildLast", "SmmTestLib RebuildLast",
                "SmmTestUi RebuildLast", "none (SmmTestLib)", "SmmTestUi RebuildLast"]), string.Join(" | ", library));
        UnpatchCulpritChecks();

        // A transpiler's own throw names its mod, by its frame, eager (inside reflection's TargetInvocationException) or an
        // iterator (where its output is read), whatever changed: the saved one thrown at the next build, or the patching
        // mod's new one
        string Saved(string method, Action patch)
        {
            (_eager, _lazy) = (0, 0);
            _throwAgain = true;
            new Harmony(ContentId).Patch(RunMethod, transpiler: By(typeof(ContentPatch), method));
            var read = PickIn(ReadOf(Thrown(patch), contentUpNewGame, same));
            UnpatchCulpritChecks();
            return read;
        }
        string[] threw =
        [
            Saved(nameof(ContentPatch.Eager), () => Fixes.Apply(typeof(FixesGoodRun))), Saved(nameof(ContentPatch.Lazy), () => Fixes.Apply(typeof(FixesGoodRun))),
            Saved(nameof(ContentPatch.Eager), () => Fixes.Patch(RunMethod, prefix: By(typeof(FixesPatch), nameof(FixesPatch.Prefix)))),
            PickIn(ReadOf(Thrown(() => Fixes.Apply(typeof(FixesBrokenTranspiler))), contentUpNewGame, same)),
        ];
        Check("the crash culprit, a transpiler's own throw at a rebuild: its mod, eager or an iterator, by PatchAll or a direct Harmony.Patch; the patching "
            + "mod's new transpiler throwing, the patching mod",
            threw.SequenceEqual(["SmmTestContent Code", "SmmTestContent Code", "SmmTestContent Code", "SmmTestFixes Code"]), string.Join(" | ", threw));
        // A library's saved transpiler throwing at a rebuild by a direct Harmony.Patch, a mod that needs it updated on the same
        // game: the library's code, not the mod that needs it. Its throw tops the frames (eager: reflection's; an iterator: its
        // own), so without Harmony's patching found among every exception's frames, not only at their top, the frames would
        // read the library and its rule would pick the changed dependent (LibraryUser).
        string Library(string method)
        {
            (_eager, _lazy) = (0, 0);
            _throwAgain = true;
            new Harmony(LibId).Patch(RunMethod, transpiler: By(typeof(LibPatch), method));
            var read = PickIn(ReadOf(Thrown(() => Fixes.Patch(RunMethod, prefix: By(typeof(FixesPatch), nameof(FixesPatch.Prefix)))), contentUp, same));
            UnpatchCulpritChecks();
            return read;
        }
        string[] libraryThrew = [Library(nameof(LibPatch.Eager)), Library(nameof(LibPatch.Lazy))];
        Check("the crash culprit, a library's saved transpiler throwing at a direct Harmony.Patch's rebuild, a mod that needs it updated: the library, eager "
            + "or an iterator", libraryThrew.SequenceEqual(["SmmTestLib Code", "SmmTestLib Code"]), string.Join(" | ", libraryThrew));

        // The original by its FullDescription: an async method's state machine (a nested type, which that text names without
        // its declaring type). Test Content's transpiler on it alone updated: found, so it. A patch class whose target isn't
        // there ("Patching exception in method null"): Harmony's own check, the patching mod, no original read.
        var moveNext = AccessTools.AsyncMoveNext(AccessTools.Method(typeof(GameLike), nameof(GameLike.RunAsync)));
        new Harmony(ContentId).Patch(moveNext, transpiler: By(typeof(ContentPatch), nameof(ContentPatch.Same)));
        var async = PickIn(ReadOf(Thrown(() => Fixes.Apply(typeof(FixesBadAsync))), contentUp, same));
        var nowhere = ReadOf(Thrown(() => Fixes.Apply(typeof(FixesNoTarget))), game, same);
        Check("the crash culprit, a failed rebuild's original by Harmony's description: an async method's state machine found (the saved transpiler's mod "
            + "alone updated, it); a target that isn't there, the patching mod by Harmony's own check, no original read",
            async == "SmmTestContent Rebuild" && PickIn(nowhere) == "SmmTestFixes Code" && !nowhere.Notes.Any(n => n.Contains("rebuild")),
            $"{async} | {PickIn(nowhere)} ({string.Join("; ", nowhere.Notes)})");
        UnpatchCulpritChecks();

        // A mod's method on its first patch (no saved patches: an earlier check patched it and took it out, so Harmony still
        // lists it), Test UI's bad transpiler on Test Fixes' Boom: when Boom itself can't compile, Test Fixes; else Test UI,
        // most likely, and Test Fixes on no list. Patched before (Test Content's postfix): Boom isn't prepared (it compiled
        // when patched), Test UI.
        var boom = AccessTools.Method(typeof(Fixes), nameof(Fixes.Boom));
        static bool IsBoom(MethodBase method) => method.DeclaringType == typeof(Fixes) && method.Name == nameof(Fixes.Boom);
        var listed = Harmony.GetAllPatchedMethods().Any(IsBoom) && Harmony.GetPatchInfo(boom) is { Prefixes.Count: 0, Postfixes.Count: 0, Transpilers.Count: 0, Finalizers.Count: 0 };
        var asked = new List<MethodBase>();
        Culprits.TestPrepare = method =>
        {
            asked.Add(method);
            if (IsBoom(method))
                throw new MissingMethodException("made up: a member Test Fixes' build had");
        };
        var pinned = PickIn(ReadOf(Thrown(() => Ui.Apply(typeof(UiBadBoom))), game, same));
        Culprits.TestPrepare = asked.Add;
        var first = ReadOf(Thrown(() => Ui.Apply(typeof(UiBadBoom))), game, same);
        new Harmony(ContentId).Patch(boom, postfix: By(typeof(ContentPatch), nameof(ContentPatch.Postfix)));
        asked.Clear();
        var before = ReadOf(Thrown(() => Ui.Apply(typeof(UiBadBoom))), game, same);
        var preparedBefore = asked.Any(IsBoom);
        Check("the crash culprit, a mod's method on its first patch: it can't compile, its mod; it can, the patching mod (it not on the list); patched "
            + "before, not prepared, the patching mod, the earlier patch's mod on the list",
            listed && pinned == "SmmTestFixes Stale" && PickIn(first) == "SmmTestUi RebuildLast" && !first.Shortlist.Contains("SmmTestFixes")
            && PickIn(before) == "SmmTestUi RebuildLast" && !before.Shortlist.Contains("SmmTestFixes") && before.Shortlist.Contains("SmmTestContent") && !preparedBefore,
            $"listed unpatched {listed}; {pinned} | {PickIn(first)} | {PickIn(before)}, prepared {preparedBefore}");
        return Task.CompletedTask;
    }

    static Task TestCulpritRank()
    {
        var mods = CrashMods();
        var (game, same, otherGame) = (Launched(CulpritGame), Launched(CulpritGame), Launched(OtherGame));
        static Culprits.Reading Found(Culprits.Pick? pick, Culprits.Check check = Culprits.Check.None, string[]? shortlist = null, bool noPatcher = false,
            string[]? named = null) => new(pick, check, shortlist ?? [], noPatcher, new HashSet<string>(named ?? []), "signature", "error", []);
        static Culprits.Pick OurPick(Culprits.Why why, params string[] ids) => new(why, why is not (Culprits.Why.Code or Culprits.Why.Patch or Culprits.Why.LibraryUser), ids);
        static ModDoctor.Pick Md(string id, ModDoctor.Reason reason = ModDoctor.Reason.Threw) => new(id, null, reason);
        Culprits.Culprit? Ranked(Culprits.Reading? reading, ModDoctor.Pick? md, LaunchGuard.Setup? planned = null, LaunchGuard.Setup? working = null,
            List<string>? log = null, Culprits.Context? with = null) =>
            Culprits.Rank(reading, md == null ? null : new ModDoctor.Findings([], md, "Mod Doctor's words"), planned ?? game, working ?? same, null, [], with ?? mods, log);
        static string Of(Culprits.Culprit? culprit) => culprit == null ? "none" : $"{string.Join("+", culprit.Ids)} {culprit.Why}";

        // Our checks first: Mod Doctor's thrower can't see a type load, a rebuild or a patch that can't compile
        var log = new List<string>();
        string[] checksFirst =
        [
            Of(Ranked(Found(OurPick(Culprits.Why.Types, "SmmTestContent")), Md("SmmTestUi"), log: log)),
            Of(Ranked(Found(OurPick(Culprits.Why.Rebuild, "SmmTestContent")), Md("SmmTestUi"))), Of(Ranked(Found(OurPick(Culprits.Why.Self, ModEntry.ModId)), Md("SmmTestUi"))),
        ];
        Check("the crash culprit's ranking: our checks above Mod Doctor's pick of a mod whose code threw, both in the log",
            checksFirst.SequenceEqual(["SmmTestContent Types", "SmmTestContent Rebuild", $"{ModEntry.ModId} Self"])
            && log.Any(l => l.Contains("SmmTestContent") && l.Contains("SmmTestUi")), $"{string.Join(" | ", checksFirst)}; {string.Join("; ", log)}");
        // A check of ours with no pick: Mod Doctor's pick only for a mod on its shortlist, whatever its reason; anywhere when
        // Harmony's failure had no mod frame under its own
        var offList = new List<string>();
        string[] shortlists =
        [
            Of(Ranked(Found(null, Culprits.Check.Types, ["SmmTestContent", "SmmTestUi"]), Md("SmmTestFixes"))),
            Of(Ranked(Found(null, Culprits.Check.Types, ["SmmTestContent", "SmmTestUi"]), Md("SmmTestUi"))),
            Of(Ranked(Found(null, Culprits.Check.Types, ["SmmTestContent", "SmmTestUi"]), Md("SmmTestFixes", ModDoctor.Reason.Updated))),
            Of(Ranked(Found(null, Culprits.Check.Harmony, ["SmmTestFixes", "SmmTestContent"]), Md("SmmTestFixes"), otherGame)),
            Of(Ranked(Found(null, Culprits.Check.Harmony, ["SmmTestFixes", "SmmTestContent"]), Md("SmmTestFixes", ModDoctor.Reason.Updated), otherGame)),
            Of(Ranked(Found(null, Culprits.Check.Harmony, ["SmmTestFixes", "SmmTestContent"]), Md("SmmTestContent"), otherGame)),
            Of(Ranked(Found(null, Culprits.Check.Harmony, ["SmmTestFixes", "SmmTestContent"]), Md("SmmTestUi"), otherGame, log: offList)),
            Of(Ranked(Found(null, Culprits.Check.Harmony, ["SmmTestLib", "SmmTestContent"]), Md("SmmTestContent", ModDoctor.Reason.Updated))),
            Of(Ranked(Found(null, Culprits.Check.Harmony, [], noPatcher: true), Md("SmmTestFixes"))),
            Of(Ranked(Found(null, Culprits.Check.Harmony, ["SmmTestContent"], noPatcher: true), Md("SmmTestFixes"))),
        ];
        Check("the crash culprit's ranking where a check of ours has no pick: a type load's reader dropped, a mod on its shortlist taken, whatever the "
            + "reason; a failed rebuild's patching mod or saved transpiler's mod taken, one off its shortlist dropped (the log says why); a library's "
            + "rebuild, the shortlist member Mod Doctor names; no mod frame under Harmony's, Mod Doctor's pick, a rebuild's too",
            shortlists.SequenceEqual(["none", "SmmTestUi DoctorThrew", "none", "SmmTestFixes DoctorThrew", "SmmTestFixes DoctorUpdated", "SmmTestContent DoctorThrew",
                "none", "SmmTestContent DoctorUpdated", "SmmTestFixes DoctorThrew", "SmmTestFixes DoctorThrew"])
            && offList.Any(l => l.Contains("SmmTestUi") && l.Contains("shortlist")), $"{string.Join(" | ", shortlists)}; {string.Join("; ", offList)}");
        // Agreeing: ours offered, Mod Doctor's box with it. Mod Doctor's thrower above our frame picks, its other reasons below
        // them and above the updated mods.
        var agreed = Ranked(Found(OurPick(Culprits.Why.RebuildLast, "SmmTestFixes")), Md("SmmTestFixes"));
        var agreedUnsaid = Ranked(Found(OurPick(Culprits.Why.Code, "SmmTestFixes")), Md("SmmTestFixes", ModDoctor.Reason.Updated));
        var frameLog = new List<string>();
        string[] order =
        [
            Of(Ranked(Found(OurPick(Culprits.Why.Patch, "SmmTestUi")), Md("SmmTestFixes"), log: frameLog)), Of(Ranked(Found(OurPick(Culprits.Why.Code, "SmmTestUi")), Md("SmmTestFixes"))),
            Of(Ranked(Found(OurPick(Culprits.Why.Code, "SmmTestUi")), Md("SmmTestFixes", ModDoctor.Reason.Updated))),
            Of(Ranked(Found(OurPick(Culprits.Why.Code, "SmmTestUi")), Md("SmmTestFixes", ModDoctor.Reason.Added))),
            Of(Ranked(Found(null), Md("SmmTestFixes", ModDoctor.Reason.Added), Launched(CulpritGame, ("SmmTestContent", "1.1")))),
        ];
        Check("the crash culprit's ranking: agreeing with Mod Doctor, ours offered with its words; its thrower above our frame pick (a patched mod method's "
            + "patcher, a mod's code), both in the log; its other reasons below our frame pick, above the updated mods",
            agreed is { Why: Culprits.Why.RebuildLast, DoctorAgrees: true, DoctorText: "Mod Doctor's words" } && agreedUnsaid is { Why: Culprits.Why.Code, DoctorAgrees: true }
            && order.SequenceEqual(["SmmTestFixes DoctorThrew", "SmmTestFixes DoctorThrew", "SmmTestUi Code", "SmmTestUi Code", "SmmTestFixes DoctorAdded"])
            && frameLog.Any(l => l.Contains("SmmTestUi") && l.Contains("SmmTestFixes")), $"{Of(agreed)} {agreed?.DoctorAgrees}; {Of(agreedUnsaid)}; {string.Join(" | ", order)}");
        // Mod Doctor's pick of ours, of a mod not installed, or of one that didn't run: left out, its words too. A mod whose
        // other copy ran: counted.
        var twinMods = CrashMods(false, new Culprits.Copy("SmmTestTwin", ModSource.ModsDirectory, "l:SmmTestTwinA", false),
            new Culprits.Copy("SmmTestTwin", ModSource.SteamWorkshop, "w:1", true), new Culprits.Copy("SmmTestRetired", ModSource.ModsDirectory, "l:SmmTestRetired", false));
        var ignored = new[] { Md(ModEntry.ModId), Md("SmmTestNotThere"), Md("SmmTestRetired") }.Select(md => Ranked(Found(OurPick(Culprits.Why.Code, "SmmTestUi")), md, with: twinMods)).ToList();
        var twin = Of(Ranked(Found(null), Md("SmmTestTwin"), with: twinMods));
        Check("the crash culprit's ranking: Mod Doctor's pick of ours, of a mod not installed or of one that didn't run left out, its words too; of a mod "
            + "whose other copy ran, counted", ignored.All(c => Of(c) == "SmmTestUi Code" && c?.DoctorText == null) && twin == "SmmTestTwin DoctorThrew",
            $"{string.Join(" | ", ignored.Select(Of))}; twin {twin}");

        // The updated mods: only when they're all the new code, on the same game
        var contentUp = Launched(CulpritGame, ("SmmTestContent", "1.1"));
        var contentUi = Launched(CulpritGame, ("SmmTestContent", "1.1"), ("SmmTestUi", "1.1"));
        var withNew = contentUp with { Mods = [.. contentUp.Mods, new LaunchGuard.Entry("SmmTestNew", ModSource.ModsDirectory, true, "1.0")] };
        var reordered = contentUp with { Mods = [.. contentUp.Mods.AsEnumerable().Reverse()] };
        string[] updated =
        [
            Of(Ranked(Found(null), null, contentUp)), Of(Ranked(Found(null, named: ["SmmTestUi"]), null, contentUi)),
            Of(Ranked(Found(null), null, contentUi)), Of(Ranked(Found(null), null, withNew)), Of(Ranked(Found(null), null, Launched(OtherGame, ("SmmTestContent", "1.1")))),
            Of(Ranked(Found(null), null, reordered)), Of(Ranked(Found(null), null, contentUp, Launched(""))),
        ];
        Check("the crash culprit's ranking, the updated mods: one, it; two, the one the failure names; two unnamed, both; one updated and one new, none; "
            + "across a game change, none; with an order change, it; a last working launch that doesn't say its game, none",
            updated.SequenceEqual(["SmmTestContent Updated", "SmmTestUi Updated", "SmmTestUi+SmmTestContent UpdatedMany", "none", "none", "SmmTestContent Updated", "none"]),
            string.Join(" | ", updated));

        // Didn't stick, the streak, Needing, nothing
        var at = DateTimeOffset.Now;
        Culprits.Recorded Entry(bool menu, string copy = "l:SmmTestFixes", DateTimeOffset? when = null) =>
            new(when ?? at, "SmmTestFixes", [new Culprits.Switch(ModSource.ModsDirectory, true, [copy])], menu);
        List<(string, ModSource, bool)> read = [("SmmTestFixes", ModSource.ModsDirectory, true)];
        bool[] stuck =
        [
            Culprits.DidntStick(["SmmTestFixes"], read, [Entry(false)], mods), Culprits.DidntStick(["SmmTestFixes"], read, [Entry(false, "l:SmmTestFixesOther")], mods),
            Culprits.DidntStick(["SmmTestFixes"], read, [Entry(true)], mods),
        ];
        var streak = Culprits.Streak([Entry(false), Entry(false), Entry(false, when: at.AddMinutes(-3)), Entry(true, when: at.AddMinutes(-9))]);
        var dependents = mods with
        {
            Installs = [.. mods.Installs, new Culprits.Copy("SmmTestNeedsFixes", ModSource.ModsDirectory, "l:a", false), new Culprits.Copy("SmmTestNewNeedsFixes", ModSource.ModsDirectory, "l:b", false)],
            Needs = new Dictionary<string, IReadOnlySet<string>>(mods.Needs) { ["SmmTestNeedsFixes"] = new HashSet<string> { "SmmTestFixes" }, ["SmmTestNewNeedsFixes"] = new HashSet<string> { "SmmTestFixes" } },
        };
        var needing = Culprits.Needing(["SmmTestFixes"], [("SmmTestFixes", ModSource.ModsDirectory, true), ("SmmTestNeedsFixes", ModSource.ModsDirectory, true)], dependents);
        Check("the crash culprit's ranking: a turn-off with no main menu since whose copy ran again didn't stick, not when another copy ran or a menu came; "
            + "the streak counts the turn-offs since a menu, and this one; the mods needing a culprit, one that failed this launch and one with no entry in "
            + "the list read; nothing, no culprit",
            stuck.SequenceEqual([true, false, false]) && streak == 3 && needing.Order().SequenceEqual(["SmmTestNeedsFixes", "SmmTestNewNeedsFixes"])
            && Ranked(Found(null), null) == null,
            $"stuck {string.Join(", ", stuck)}; streak {streak}; needing {string.Join(", ", needing)}");
        // Names: with the id where another mod's name holds the name ("大黄蜂" beside "Hornet / 大黄蜂")
        string[] named =
        [
            Culprits.Named("Hornet", "大黄蜂", ["Hornet / 大黄蜂"], plain: true), Culprits.Named("HornetMod", "Hornet / 大黄蜂", ["大黄蜂"], plain: true),
            Culprits.Named("Hornet", "Hornet", ["Hornet / 大黄蜂"], plain: true), Culprits.Named("Hornet", "大黄蜂", ["Hornet / 大黄蜂"]),
        ];
        Check("the crash culprit's names: one another mod's name holds gets its id; the other stays; a name that's its id stays",
            named.SequenceEqual(["大黄蜂 (Hornet)", "Hornet / 大黄蜂", "Hornet", "大黄蜂 (Hornet)"]), string.Join(" | ", named));
        return Task.CompletedTask;
    }

    static async Task TestCulpritDoctorFile(Node menu, string outDir)
    {
        var top = Path.Combine(outDir, "mod-doctor-findings");
        var file = Path.Combine(top, "mod_data", ModDoctor.ModId, "startup.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file) ?? top);
        ModDoctor.TestFolder = top;
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        string Json(JsonNode? culprit, string? explanation = null, int? process = null) => new JsonObject
        {
            ["version"] = 1, ["processId"] = process ?? System.Environment.ProcessId, ["processStartUtc"] = self.StartTime.ToUniversalTime().ToString("o"),
            ["fatal"] = new JsonObject
            {
                ["kind"] = "StartupFailed", ["message"] = "made up", ["step"] = "ModelDb.Init", ["suspects"] = new JsonArray("SmmTestFixes"),
                ["culprit"] = culprit, ["explanation"] = explanation,
            },
            ["failedMods"] = new JsonArray(),
        }.ToJsonString();
        async Task<ModDoctor.Findings> Found(string text)
        {
            File.WriteAllText(file, text);
            return await ModDoctor.WaitForFindings(menu);
        }
        static JsonObject Culprit(JsonNode? id, string? reason) => new() { ["id"] = id, ["name"] = "Test Fixes", ["reason"] = reason };
        var long600 = string.Join(" ", Enumerable.Repeat("word", 200));
        try
        {
            var threw = await Found(Json(Culprit("SmmTestFixes", "Threw"), "Test Fixes [b]crashed[/b]\nwhile loading"));
            var unsaid = await Found(Json(Culprit("SmmTestFixes", null)));
            var unknown = await Found(Json(Culprit("SmmTestFixes", "x")));
            var none = await Found(Json(null));
            var text = await Found(Json(JsonValue.Create("x")));
            var number = await Found(Json(Culprit(5, "Threw")));
            var other = await Found(Json(Culprit("SmmTestFixes", "Threw"), process: System.Environment.ProcessId + 1));
            var cut = await Found(Json(Culprit("SmmTestFixes", "Added"), long600));
            Check("Mod Doctor's findings: its pick with its reason and its words (as written, line breaks kept); no reason or one unknown, Updated; no pick, "
                + "a pick that's a string or an id that's a number, none; another process's file, nothing; its words past 600 characters cut at a word with \"..\"",
                threw is { Culprit: { Id: "SmmTestFixes", Reason: ModDoctor.Reason.Threw }, Explanation: "Test Fixes [b]crashed[/b]\nwhile loading" } && threw.Suspects.Contains("SmmTestFixes")
                && unsaid.Culprit?.Reason == ModDoctor.Reason.Updated && unknown.Culprit?.Reason == ModDoctor.Reason.Updated && none.Culprit == null
                && text.Culprit == null && number.Culprit == null && other.Suspects.Count == 0 && other.Culprit == null
                && cut is { Culprit.Reason: ModDoctor.Reason.Added, Explanation: { Length: <= 602 } words } && words.EndsWith("word..", StringComparison.Ordinal),
                $"{threw.Culprit} \"{threw.Explanation}\"; {unsaid.Culprit?.Reason}, {unknown.Culprit?.Reason}; none {none.Culprit}, string {text.Culprit}, "
                + $"number {number.Culprit}; other {other.Culprit}; cut {cut.Explanation?.Length} \"..{cut.Explanation?[^10..]}\"");
            // Half a file (it writes in place), then the whole one two frames later: read again, the whole one. No file: the
            // wait goes on its 30 frames while a Mod Doctor copy is loaded, and ends at once without one.
            var whole = Json(Culprit("SmmTestFixes", "Threw"));
            File.WriteAllText(file, whole[..(whole.Length / 2)]);
            var waiting = ModDoctor.WaitForFindings(menu);
            for (var i = 0; i < 2; i++)
                await menu.ToSignal(menu.GetTree(), SceneTree.SignalName.ProcessFrame);
            File.WriteAllText(file, whole);
            var later = await waiting;
            File.Delete(file);
            var frames = 0;
            void Count() => frames++;
            menu.GetTree().ProcessFrame += Count;
            ModDoctor.TestLoaded = true;
            await ModDoctor.WaitForFindings(menu);
            var loadedFrames = frames;
            ModDoctor.TestLoaded = false;
            frames = 0;
            await ModDoctor.WaitForFindings(menu);
            var absentFrames = frames;
            menu.GetTree().ProcessFrame -= Count;
            // A run with a real Mod Doctor loaded can't see the wait end at once
            var realDoctor = ModManager.Mods.Any(m => m.manifest?.id == ModDoctor.ModId && m.state == ModLoadState.Loaded);
            Check("Mod Doctor's findings: half a file is read again until the whole one is there; with no file the wait lasts its frames while Mod Doctor is "
                + "loaded, and ends at once without it",
                later.Culprit?.Id == "SmmTestFixes" && loadedFrames >= 29 && (realDoctor || absentFrames <= 1),
                $"later {later.Culprit}; frames loaded {loadedFrames}, absent {absentFrames}{(realDoctor ? " (a real Mod Doctor is loaded)" : "")}");
        }
        finally
        {
            ModDoctor.TestFolder = null;
            ModDoctor.TestLoaded = false;
            File.Delete(file);
        }
    }

    static Task TestCulpritHook()
    {
        // The finalizer that keeps the exception: first among the start step's finalizers, void (Harmony rethrows the
        // exception as it was)
        var finalizer = Harmony.GetPatchInfo(AccessTools.Method(typeof(OneTimeInitialization), nameof(OneTimeInitialization.ExecuteEssential)))?.Finalizers
            .FirstOrDefault(p => p.PatchMethod.DeclaringType == typeof(StartupFailurePatch));
        Check("the failed start's exception is kept by a finalizer on the game's start step, first in line and void",
            finalizer is { priority: Priority.First } && finalizer.PatchMethod.ReturnType == typeof(void), $"{finalizer?.priority}, returns {finalizer?.PatchMethod.ReturnType.Name}");
        // The fallback, from worker threads: two faults at once, exactly one kept, and the handler off the game's event
        if (Compat.UnobservedFault == null)
        {
            Results.Add("SKIP the failed start's fallback: this game has no TaskHelper.UnobservedFault");
            return Task.CompletedTask;
        }
        var (kept, watching) = (Culprits.TestFault, Culprits.TestWatching != null);
        try
        {
            Culprits.TestFault = null;
            Culprits.TestAnyFault = true;
            Culprits.Watch();
            var (one, two) = (new InvalidOperationException("made up: one"), new InvalidOperationException("made up: two"));
            Task.WaitAll(Task.Run(() => Culprits.TestFaulted(one)), Task.Run(() => Culprits.TestFaulted(two)));
            var error = Culprits.TestFault?.Chain.LastOrDefault()?.Error;
            var handlers = (typeof(TaskHelper).GetField("UnobservedFault", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as Delegate)?.GetInvocationList() ?? [];
            Check("the failed start's fallback, raised from two worker threads at once: exactly one kept, and its handler off the game's event",
                (error == one || error == two) && Culprits.TestWatching == null && !handlers.Any(h => h.Method.DeclaringType == typeof(Culprits)),
                $"kept {error?.Message}, watching {Culprits.TestWatching != null}, handlers {handlers.Length}");
        }
        finally
        {
            Culprits.TestAnyFault = false;
            Culprits.TestFault = kept;
            if (watching)
                Culprits.Watch();
        }
        return Task.CompletedTask;
    }
}
