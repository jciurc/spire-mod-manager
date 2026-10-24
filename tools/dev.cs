// Dev runs of the game, apart from real play (CONTRIBUTING.md, "Trying it and testing", has the details).
//
//   dotnet run tools/dev.cs test      build, reset the sandbox, run the in-game self-test headless
//                                     --window: with a window, for screenshots, kept behind others
//                                     --size WxH, with --window: that window size instead of 1920x1080, for
//                                      screenshots (the mouse checks place the pointer in 1920x1080 units, so
//                                      they fail at other sizes)
//                                     --bmm / --bmm-after: with Better Mod Menu loaded before / after ours
//                                     --bmm-local: its local-fix build (BmmRepo) instead of the installed one
//                                     --workshop <id>[,<id>...]: those subscribed Workshop items too, after ours
//                                     --twins, with --workshop: allow two items of one mod id; the last given is
//                                      picked to load, and the log is checked for it
//                                     --real: your own mods (switched off) and a copy of your data
//                                     --first-run, --first-run-bmm: a first launch, without or with Better Mod
//                                      Menu's data to import
//                                     --crash-prepare, then --crash: a launch that saves the last working setup,
//                                      then one that fails to start (made up) with Test Retired on, our dialog answered
//                                     --stable, with any of them: the stable branch's game, in its own dev copy
//   dotnet run tools/dev.cs launch    build, open the sandboxed game to try by hand (--first-run,
//                                     --first-run-bmm and --workshop as for test); returns when you close it;
//                                     --keep-saves keeps the sandbox's saves, which every run empties
//   dotnet run tools/dev.cs smoke     the release build (dist/SpireModManager) headless until the main menu
//                                     loads; fails if our mod didn't start or logged errors
//
// The dev copy (sts2-dev on the game's drive, STS2_DEV_DIR; sts2-dev-stable for --stable) is hard links to the
// installed game plus its own mods\ folder (and, during a run, the fixtures' Workshop copies in its mods_STEAMTEST,
// which the game reads as Workshop items), re-linked after a Steam update of its branch. --force-steam off keeps the
// game's data in %APPDATA%\SlayTheSpire2\default\1\, apart from real saves, and --log-file writes out\dev.log. test
// resets the sandbox to tools/fixtures and prints out/selftest/results.txt; exit code 1 on any FAIL.
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32;

// Full paths without a trailing separator: the running-game check below compares DevDir with the exe's folder.
var steamapps = FullDir(SteamApps()); // the Steam library with the game
var GameDir = Path.Combine(steamapps, "common", "Slay the Spire 2");
if (!Directory.Exists(GameDir))
    return Fail($"Slay the Spire 2 isn't in {steamapps}. Set the environment variable "
        + @"STS2_STEAMAPPS to the steamapps folder of the library that has it, for example D:\SteamLibrary\steamapps (CONTRIBUTING.md, ""What you need"").");
var stable = args.Contains("--stable"); // the stable branch's game, in its own dev copy
var BetaDir = FullDir(Env("STS2_DEV_DIR", Path.Combine(Path.GetPathRoot(GameDir) ?? "", "sts2-dev"))); // same drive
var StableDir = FullDir(Env("STS2_STABLE_DIR", Path.Combine(Path.GetPathRoot(GameDir) ?? "", "sts2-dev-stable")));
var DevDir = stable ? StableDir : BetaDir;
var WorkshopDir = Path.Combine(steamapps, "workshop", "content", "2868840");
var BmmRepo = Env("STS2_BMM_REPO", @"C:\repos\gaming\slay the spire\bettermodmenu"); // Better Mod Menu's local-fix checkout (--bmm-local)
const string ModId = "SpireModManager";
// The whole run, killed after this. The full suite takes 110-125 s from the game's start, windowed too (October 2026),
// and grows with every check: this leaves it room on a slower PC, and a hung game is killed within 5 minutes, so the
// next run (any repo's) gets the game.
const int TimeoutSeconds = 300;
var root = Path.GetDirectoryName(ScriptDir()) ?? ".";
var userData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SlayTheSpire2");
var sandbox = Path.Combine(userData, "default", "1");
var outDir = Path.Combine(root, "out", "selftest");
var logPath = Path.Combine(root, "out", "dev.log");
var mode = args.FirstOrDefault() ?? "test";
var smoke = mode == "smoke"; // the release build from dist/, not a fresh one
var distDir = Path.Combine(root, "dist", ModId);
var real = args.Contains("--real"); // your own mods (switched off) and a copy of your manager data
var bmm = new[] { "--bmm", "--bmm-after", "--bmm-local" }.Any(args.Contains); // Better Mod Menu on
var bmmData = args.Contains("--first-run-bmm"); // its fixture data in the sandbox, for our first-run import
var firstRun = bmmData || args.Contains("--first-run"); // no manager.json of ours
var crashPrepare = args.Contains("--crash-prepare"); // reach the main menu only, so the last working setup is saved
var crash = args.Contains("--crash"); // then: our data kept, Test Retired on, a made-up failed start and our dialog answered
var workshopAt = Array.IndexOf(args, "--workshop"); // the Workshop item ids follow it, comma-separated
var workshopItems = workshopAt < 0 ? [] : (args.ElementAtOrDefault(workshopAt + 1) ?? "")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToArray();
var workshopMods = new List<string>(); // the mod ids in those items' manifests, in the order given
var twins = args.Contains("--twins"); // two --workshop items of one mod id on purpose (the self-test's two-copies check)
// --size WxH: test --window at another window size than 1920x1080 (a 2560x1440 screen draws layout and text at another
// scale; screenshots then show it).
var sizeAt = Array.IndexOf(args, "--size");
var windowSize = (X: 1920, Y: 1080);

if (mode is not ("test" or "launch" or "smoke"))
    return Fail("usage: dotnet run tools/dev.cs [test [--window [--size WxH]] [--bmm[-after]] [--bmm-local] [--real|--first-run|--first-run-bmm|--crash-prepare|--crash]|launch [--first-run|--first-run-bmm] [--keep-saves]|smoke [--bmm]] [--workshop <id>[,<id>...] [--twins]] [--stable] [--no-wait]");
if (real && firstRun)
    return Fail("--real brings your own data, so it can't be a first run");
if (crashPrepare && crash)
    return Fail("--crash-prepare and --crash are two runs, in that order: dotnet run tools/dev.cs test --crash-prepare, then test --crash");
if ((crashPrepare || crash) && (mode != "test" || real || firstRun))
    return Fail("--crash-prepare and --crash are test runs with the fixture mods and the data the first one leaves: not launch, smoke, --real or a first run");
if (crashPrepare && workshopAt >= 0)
    return Fail("--crash-prepare saves the setup that works, the fixture mods alone: --workshop goes with --crash, for mods that fail the start");
// --workshop: every item has to be there, or the run would test without it and look like a pass. So it takes
// one list: a second --workshop, --workshop=..., or ids after a space would be dropped without a word.
if (args.Count(a => a.StartsWith("--workshop")) != (workshopAt < 0 ? 0 : 1))
    return Fail("--workshop comes once, with its ids after a space, as in: --workshop 3747605109,3750485606");
// The options each mode takes, spelled exactly: any other is refused, or a typo (--stabel, --frist-run) would run the
// plain suite, and its pass would read as that option's.
string[] known = ["--bmm", "--bmm-after", "--bmm-local", "--workshop", "--twins", "--stable", "--no-wait", .. mode switch
{
    "test" => new[] { "--window", "--size", "--real", "--first-run", "--first-run-bmm", "--crash-prepare", "--crash" },
    "launch" => ["--first-run", "--first-run-bmm", "--keep-saves"],
    _ => [],
}];
if (args.Skip(1).FirstOrDefault(a => a.StartsWith("--") && !known.Contains(a)) is { } unknown)
    return Fail($"\"{unknown}\" isn't an option of {mode}. It takes {string.Join(", ", known)}.");
if (sizeAt >= 0)
{
    if (!args.Contains("--window"))
        return Fail("--size goes with --window: a run with no window draws nothing to measure");
    if (args.ElementAtOrDefault(sizeAt + 1)?.Split('x') is not [var w, var h] || !int.TryParse(w, out var width) || !int.TryParse(h, out var height)
        || width < 800 || height < 600)
        return Fail("--size needs a window size of at least 800x600, as in: --size 2560x1440");
    windowSize = (width, height);
}
if (args.Where((a, i) => i > 0 && i != workshopAt + 1 && i != sizeAt + 1 && !a.StartsWith("--")).FirstOrDefault() is { } stray)
    return Fail($"\"{stray}\" isn't an option. Workshop ids go after --workshop, comma-separated with no spaces, as in: --workshop 3747605109,3750485606");
if (workshopAt >= 0 && workshopItems.Length == 0)
    return Fail("--workshop needs Workshop item ids, comma-separated, as in: --workshop 3747605109,3750485606");
var workshopManifests = new List<(string Item, JsonObject Manifest)>(); // the mods in those items, in the order given
foreach (var item in workshopItems)
{
    if (!item.All(char.IsAsciiDigit))
        return Fail($"--workshop takes Workshop item ids (numbers), not \"{item}\"");
    var dir = Path.Combine(WorkshopDir, item);
    if (!Directory.Exists(dir))
        return Fail($"Workshop item {item} isn't in {WorkshopDir}. Subscribe to it in Steam first: Steam downloads only subscribed items.");
    var manifests = Manifests(dir).ToList();
    if (manifests.Count == 0)
        return Fail($"Workshop item {item} has no mod manifest in {dir}");
    // Two mods with one id (both ModTheSpire2s have ModTheSpire2): the game would load only one of them.
    foreach (var manifest in manifests)
    {
        if (!twins && workshopManifests.FirstOrDefault(m => Id(m.Manifest) == Id(manifest)) is { Item: { } other })
            return Fail($"Workshop items {other} and {item} both have the mod id {Id(manifest)}, and the game loads only one of them. Test them one at a time, or add --twins to test both on purpose.");
        workshopManifests.Add((item, manifest));
    }
}
workshopMods = workshopManifests.Select(m => Id(m.Manifest)).Distinct().ToList(); // twins: one id, one of them loads
// What each --workshop mod needs has to be on too, all the way down: the game won't load a mod whose dependency
// didn't load. On in this run: ours, the --workshop mods, Better Mod Menu with --bmm, and the fixture mods (not --real).
var installedManifests = Manifests(Path.Combine(GameDir, "mods")).ToList();
var on = workshopManifests.GroupBy(m => Id(m.Manifest)).ToDictionary(g => g.Key, g => g.First().Manifest); // twins: each copy is walked with its own, below
on.TryAdd(ModId, []);
var others = real ? [] : Manifests(Path.Combine(root, "tools", "fixtures", "mods"));
foreach (var manifest in others.Concat(installedManifests.Where(m => bmm && Id(m) == "BetterModMenu")))
    on.TryAdd(Id(manifest), manifest);
// Walked from the --workshop mods only: some self-test checks need a mod that won't load (Test Addon, as above).
foreach (var (item, manifest) in workshopManifests)
{
    on[Id(manifest)] = manifest; // twins: each copy is walked with its own needs
    if (MissingDependency([Id(manifest)], []) is { } chain)
    {
        var needed = chain[^1];
        if (chain.IndexOf(needed) < chain.Count - 1) // back to a mod already in the chain: the game refuses every mod in a loop, and every mod needing one
            return Fail($"Workshop item {item} won't load: {string.Join(", ", chain.Zip(chain.Skip(1), (mod, dependency) => $"{mod} needs {dependency}"))}: "
                + "a dependency loop, which the game refuses.");
        var installed = installedManifests.Any(m => Id(m) == needed);
        var subscribed = Directory.GetDirectories(WorkshopDir).FirstOrDefault(d => ManifestIds(d).Contains(needed));
        var needs = string.Join(", ", chain.Zip(chain.Skip(1), (mod, dependency) => $"{mod} needs {dependency}"));
        var add = subscribed != null ? $"add its Workshop item, as in --workshop {string.Join(',', workshopItems.Append(Path.GetFileName(subscribed)))}"
            : "subscribe to it in Steam, then add its Workshop item to --workshop.";
        return Fail($"Workshop item {item} won't load: {needs}, and {needed} won't be on in this run: " + (installed && needed == "BetterModMenu" ? "add --bmm."
            : installed ? "dev runs don't switch on the mods installed for play (Better Mod Menu aside), so " + add
            : add));
    }
}
// One dev game at a time across every repo and session (the guidelines' sts2-mods add-on, "One dev game at a time"):
// the shared lock, taken before anything touches the sandbox and held to the end of this script, after the game has
// exited and its cleanup is done. --no-wait refuses at once instead of waiting for it.
using var devRun = TakeDevRunLock(string.Join(' ', args.DefaultIfEmpty("test")), args.Contains("--no-wait"), out var held);
if (held != null)
    return Fail(held);
// Every dev copy of the game plays in the one no-Steam sandbox (default\1): ours on either branch, and other mods' dev
// runners (sts-content-options, Mod Doctor). A run that writes it while another runs breaks that one, so wait until no
// copy but the real install runs, here and again right before the sandbox is written (another can start while we
// build and link). A second guard after the lock, for a dev copy started by hand.
if (WaitForOtherDevCopies() is { } busy)
    return Fail(busy);
// Mod Doctor's runs put a fixture mod (MdTestCopy) in a dev copy's mods_STEAMTEST, which the game reads as Workshop items,
// and take it out again after every run, as ours do with theirs (SmmTestDual's Workshop copy, tools/fixtures/mods_STEAMTEST);
// one killed outright can leave it behind, a copy the next run doesn't expect. Only those fixtures go, and only while we
// hold the lock (no other run is using the dev copy).
var steamTest = Path.Combine(DevDir, "mods_STEAMTEST");
var steamTestFixtures = Path.Combine(root, "tools", "fixtures", "mods_STEAMTEST");
RemoveSteamTest(["MdTestCopy*", .. Directory.Exists(steamTestFixtures) ? Directory.GetDirectories(steamTestFixtures).Select(Path.GetFileName).OfType<string>() : []], "leftover ");

if (smoke)
{
    if (new[] { ".dll", ".json", ".pck" }.FirstOrDefault(ext => !File.Exists(Path.Combine(distDir, ModId + ext))) is { } missing)
        return Fail($"no {ModId}{missing} in {distDir}: run `dotnet run build.cs --dist` first");
}
else
{
    // Its own build may clear out/ while it holds the lock (build.cs refuses anyone else's while this run is going).
    var buildStart = new ProcessStartInfo("dotnet", ["run", Path.Combine(root, "build.cs"), "--no-install"]);
    buildStart.Environment["SPIRE_MOD_MANAGER_DEV_RUN"] = Environment.ProcessId.ToString();
    if (Process.Start(buildStart) is not { } build)
        return Fail("could not start the build");
    build.WaitForExit();
    if (build.ExitCode != 0)
        return Fail("build failed");
}

if (LinkDevCopy() is { } noCopy)
    return Fail(noCopy);
if (WaitForOtherDevCopies() is { } stillBusy)
    return Fail(stillBusy);
SyncMods();
PrepareSandbox((mode is "test" or "smoke" || firstRun) && !crash); // --crash keeps --crash-prepare's last working setup
// Saves start empty every run (the guidelines' sts2-mods add-on): progress, runs and history in the sandbox, so no other
// mod's ids and no half-played run are left from an earlier run, in any repo. launch --keep-saves keeps them, to try
// something across launches by hand. Nothing is backed up: only dev runs write there.
if (!(mode == "launch" && args.Contains("--keep-saves")))
    EmptySaves();

var start = new ProcessStartInfo(Path.Combine(DevDir, "SlayTheSpire2.exe"), ["--force-steam", "off", "--log-file", logPath])
    { WorkingDirectory = DevDir };
// Tests run headless (no window, UI dumps only) unless --window asks for screenshots; then the window
// is sent behind everything and focus handed back (see below), so your work isn't interrupted.
var windowed = mode == "launch" || (!smoke && args.Contains("--window"));
if (!windowed)
    start.ArgumentList.Add("--headless");
start.Environment["SPIRE_MOD_MANAGER_DEV"] = "1"; // the mod marks the window "[DEV]"
if (File.Exists(Path.Combine(DevDir, "override.cfg")))
    File.Delete(Path.Combine(DevDir, "override.cfg")); // an earlier no-focus experiment: it kept the window on top
var previousForeground = GetForegroundWindow();

// Launch waits for you to close the game, so the shared dev-run lock is held while it's open, as for every mode. A
// relaunch (our Relaunch game, the crash dialog's restart) starts a new game from the dev copy as the first exits: so
// it waits until none has run from there for a few seconds.
if (mode == "launch")
{
    using var launched = Process.Start(start) ?? throw new InvalidOperationException("could not start the game");
    Console.WriteLine($"launched the dev copy in the sandbox (log: {logPath}); this returns when you close the game");
    launched.WaitForExit();
    var devCopy = Path.GetFullPath(DevDir).TrimEnd('\\') + "\\";
    bool DevGameRuns() => Process.GetProcessesByName("SlayTheSpire2")
        .Any(p => ExePath(p)?.StartsWith(devCopy, StringComparison.OrdinalIgnoreCase) == true);
    var said = false;
    for (var quiet = 0; quiet < 3;)
    {
        Thread.Sleep(2000);
        if (!DevGameRuns())
            quiet++;
        else
        {
            quiet = 0;
            if (!said)
                Console.WriteLine("the game relaunched: waiting for that one too");
            said = true;
        }
    }
    RemoveOurSteamTest();
    return 0;
}

if (smoke)
{
    // Until the main menu has finished loading (our button goes on it then), plus a few seconds, then the game is
    // closed. Results go where test's do, so session scripts count them the same way.
    if (Directory.Exists(outDir))
        Directory.Delete(outDir, recursive: true);
    Directory.CreateDirectory(outDir);
    if (File.Exists(logPath))
        File.Delete(logPath);
    start.RedirectStandardOutput = start.RedirectStandardError = true;
    var released = Process.Start(start) ?? throw new InvalidOperationException("could not start the game");
    released.OutputDataReceived += (_, _) => { };
    released.ErrorDataReceived += (_, _) => { };
    released.BeginOutputReadLine();
    released.BeginErrorReadLine();
    const string menuLoaded = "main menu loaded (complete)";
    DateTime? menuAt = null;
    var giveUp = DateTime.Now.AddSeconds(120);
    while (!released.WaitForExit(500) && DateTime.Now < giveUp && (menuAt == null || DateTime.Now < menuAt.Value.AddSeconds(5)))
        if (menuAt == null && LogLines().Any(line => line.Contains(menuLoaded)))
            menuAt = DateTime.Now;
    var exitedEarly = released.HasExited ? $"the game exited on its own, code {released.ExitCode}" : ""; // a crash, if before the menu
    if (!released.HasExited)
    {
        released.Kill();
        released.WaitForExit();
    }
    RemoveOurSteamTest();
    var lines = LogLines();
    // Ours: a warning or error tagged [SpireModManager], an [ERROR] naming our id or name (not the fixture mods',
    // whose author is "Spire Mod Manager tests"), and a Godot "ERROR:" naming our code, on its own line or in the
    // stack lines below it (a release build has no debug information, so the stack may be missing).
    var errors = new List<string>();
    for (var i = 0; i < lines.Count; i++)
    {
        var line = lines[i];
        if (line.StartsWith("ERROR:") && lines.Skip(i).TakeWhile((l, n) => n == 0 || l.StartsWith(' ')).Any(l => l.Contains(ModId)))
            errors.Add(line);
        else if ((line.Contains("[ERROR]") && (line.Contains(ModId) || (line.Contains("Spire Mod Manager") && !line.Contains("Spire Mod Manager tests"))))
            || (line.Contains($"[{ModId}]") && line.Contains("[WARN]")))
            errors.Add(line);
    }
    var dll = Path.Combine(distDir, ModId + ".dll");
    var version = (string?)JsonNode.Parse(File.ReadAllText(Path.Combine(distDir, ModId + ".json")))?["version"] ?? "?";
    var committed = CommitTime();
    string[] checks =
    [
        Result($"dist/ was built after the checked-out commit (built {File.GetLastWriteTime(dll):yyyy-MM-dd HH:mm})",
            committed == null || File.GetLastWriteTimeUtc(dll) >= committed, "rebuild it with `dotnet run build.cs --dist`"),
        Result($"the release build ({version}) loaded and finished initializing", lines.Any(l => l.Contains("Finished mod initialization for") && l.EndsWith($"({ModId})."))),
        Result("its initializer logged that it started", lines.Any(l => l.Contains($"[{ModId}] Initialized"))),
        // The run sets SPIRE_MOD_MANAGER_DEV=1 as every dev run does; a build without DEVTOOLS ignores it.
        Result("the dev switches are compiled out (it ignored SPIRE_MOD_MANAGER_DEV)", !lines.Any(l => l.Contains($"[{ModId}] Initialized (dev run)"))),
        Result("the game reached its main menu", menuAt != null, exitedEarly),
        // The run closes it itself, so a game that exited on its own crashed, after the menu too.
        Result("the game kept running until the run closed it", exitedEarly.Length == 0, exitedEarly),
        Result("nothing in the log from our mod is an error or a warning", errors.Count == 0, string.Join(" | ", errors.Take(3))),
    ];
    File.WriteAllLines(Path.Combine(outDir, "results.txt"), checks);
    foreach (var check in checks)
        Console.WriteLine(check);
    Console.WriteLine("--- log ---");
    // Before the menu, the last lines say where it stopped.
    foreach (var line in lines.Where(l => l.Contains($"[{ModId}]") || errors.Contains(l)).Concat(menuAt == null ? lines.TakeLast(15) : []))
        Console.WriteLine(line.Length > 400 ? line[..400] : line);
    Console.WriteLine("Not covered here: opening the Modding page. Do that once by hand in the real game (docs/dev/RELEASING.md).");
    return checks.Any(c => c.StartsWith("FAIL")) ? 1 : 0;

    static string Result(string name, bool ok, string detail = "") => $"{(ok ? "PASS" : "FAIL")} {name}{(ok || detail.Length == 0 ? "" : $": {detail}")}";
}

// The fixtures in the dev copy's mods_STEAMTEST whose names match these, gone (from the run before, or ours after the
// game exits: the copy is shared with other mods' dev runs, which don't expect them).
void RemoveSteamTest(IEnumerable<string> patterns, string what = "")
{
    if (!Directory.Exists(steamTest))
        return;
    foreach (var leftover in patterns.SelectMany(pattern => Directory.EnumerateFileSystemEntries(steamTest, pattern)).Distinct().ToList())
    {
        try
        {
            if (Directory.Exists(leftover))
                Directory.Delete(leftover, recursive: true);
            else
                File.Delete(leftover);
            Console.WriteLine($"removed the {what}fixture {leftover}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"(couldn't remove the {what}fixture {leftover}: {e.Message})");
        }
    }
}

// Ours, after the game has exited.
void RemoveOurSteamTest()
{
    if (!real && Directory.Exists(steamTestFixtures))
        RemoveSteamTest(Directory.GetDirectories(steamTestFixtures).Select(Path.GetFileName).OfType<string>());
}

// When the checked-out commit was made (UTC), or null without git.
DateTime? CommitTime()
{
    try
    {
        var git = Process.Start(new ProcessStartInfo("git", ["-C", root, "log", "-1", "--format=%ct"]) { RedirectStandardOutput = true });
        var seconds = git?.StandardOutput.ReadToEnd().Trim();
        git?.WaitForExit();
        return long.TryParse(seconds, out var s) ? DateTimeOffset.FromUnixTimeSeconds(s).UtcDateTime : null;
    }
    catch (System.ComponentModel.Win32Exception)
    {
        return null;
    }
}

// The dev log so far, read while the game may still be writing it.
List<string> LogLines()
{
    if (!File.Exists(logPath))
        return [];
    using var log = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    using var reader = new StreamReader(log);
    var lines = new List<string>();
    while (reader.ReadLine() is { } line)
        lines.Add(line);
    return lines;
}

if (Directory.Exists(outDir))
    Directory.Delete(outDir, recursive: true);
Directory.CreateDirectory(outDir);
start.Environment["SPIRE_MOD_MANAGER_SELFTEST"] = outDir;
if (real)
    start.Environment["SPIRE_MOD_MANAGER_SELFTEST_MODE"] = "real";
else if (crashPrepare || crash)
    // --crash with --workshop: the start fails by those mods (crash-real), not by our made-up throw.
    start.Environment["SPIRE_MOD_MANAGER_SELFTEST_MODE"] = !crash ? "crash-prepare" : workshopMods.Count > 0 ? "crash-real" : "crash";
else if (firstRun)
    start.Environment["SPIRE_MOD_MANAGER_SELFTEST_MODE"] = bmmData ? "first-run-bmm" : "first-run";
// The game echoes its whole log to stdout; we read the log file instead.
start.RedirectStandardOutput = start.RedirectStandardError = true;
var game = Process.Start(start) ?? throw new InvalidOperationException("could not start the game");
game.OutputDataReceived += (_, _) => { };
game.ErrorDataReceived += (_, _) => { };
game.BeginOutputReadLine();
game.BeginErrorReadLine();
// With a window: as soon as it appears (and whenever it comes forward), push it to the bottom of the
// window stack without activating it and give focus back to what was in front before.
var deadline = DateTime.Now.AddSeconds(TimeoutSeconds);
while (!game.WaitForExit(100) && DateTime.Now < deadline)
{
    if (!windowed)
        continue;
    game.Refresh();
    var window = game.MainWindowHandle;
    if (window == IntPtr.Zero || GetForegroundWindow() != window)
        continue;
    SetWindowPos(window, new IntPtr(1) /* HWND_BOTTOM */, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 /* NOSIZE|NOMOVE|NOACTIVATE */);
    if (previousForeground != IntPtr.Zero)
        SetForegroundWindow(previousForeground);
}
if (!game.HasExited)
{
    // Where it got to: the newest of the self-test's files (a suite that's only slow ends late, one that's stuck early).
    var last = Directory.Exists(outDir) ? new DirectoryInfo(outDir).GetFiles().OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault() : null;
    Console.WriteLine($"game still running after {TimeoutSeconds}s, killing it" + (last == null ? "" : $" (last written: {last.Name}, {(DateTime.UtcNow - last.LastWriteTimeUtc).TotalSeconds:0} s before)"));
    game.Kill();
    game.WaitForExit();
}
RemoveOurSteamTest();

var results = Path.Combine(outDir, "results.txt");
// --crash: the relaunch after Restore reads the mod list from settings.save, and the self-test saw it only in memory.
// So here, with the game gone: Test Retired (on for this launch) off again on disk.
if (crash && File.Exists(results))
{
    string line;
    try
    {
        var retired = JsonNode.Parse(File.ReadAllText(Path.Combine(sandbox, "settings.save")))?["mod_settings"]?["mod_list"]?.AsArray()
            .Where(entry => (string?)entry?["id"] == "SmmTestRetired").Select(entry => (bool?)entry?["is_enabled"]).ToList() ?? [];
        line = $"{(retired.Count > 0 && retired.All(on => on == false) ? "PASS" : "FAIL")} a failed start: settings.save on disk, "
            + $"which the relaunch reads, has Test Retired off again (is_enabled {string.Join(",", retired)})";
    }
    catch (Exception e)
    {
        line = $"FAIL a failed start: settings.save couldn't be read after the run: {e.Message}";
    }
    File.AppendAllLines(results, [line]);
}
var text = File.Exists(results) ? File.ReadAllText(results) : "NO RESULTS (test did not finish)\n";
Console.Write(text);
Console.WriteLine("--- log ---");
// Our lines and the --workshop mods'. A mod loaded if it logged "Finished mod initialization", and failed to start if a
// loadFailure error came while the loader was on its files (errors don't always name the mod; it loads one at a time).
var named = workshopMods.Select(id => new Regex($@"(?<![\w-]){Regex.Escape(id)}(?![\w-])")).ToList();
var loadFailure = new Regex(@"^\[ERROR\] .*(mod initializer|PatchAll on assembly|but no (assembly|PCK) at path)");
var modFile = new Regex(@"^\[\w+\] (Loading assembly DLL|Loading Godot PCK|.* at path) .*?(?<mod>[^\\/]+)\.(dll|pck)\b");
var loaded = new Dictionary<string, string?>(); // a --workshop mod that loaded: the error it failed to start with, if any
string? loading = null; // the mod the loader named a file of last, until its finish line
string? failure = null; // the first loadFailure error while loading it
var shown = (string line) => line.Contains($"[{ModId}]") || (line.Contains("[ERROR]") && line.Contains("Spire"))
    || (line.StartsWith('[') && named.Any(name => name.IsMatch(line)));
var print = (string line) => Console.WriteLine(line.Length > 400 ? line[..400] : line);
if (File.Exists(logPath))
    using (var log = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
    using (var reader = new StreamReader(log))
        while (reader.ReadLine() is { } line)
        {
            if (modFile.Match(line) is { Success: true } file && file.Groups["mod"].Value != loading)
                (loading, failure) = (file.Groups["mod"].Value, null);
            if (loadFailure.IsMatch(line))
                failure ??= line;
            if (line.Contains("Finished mod initialization for"))
            {
                if (workshopMods.FirstOrDefault(mod => line.EndsWith($"({mod}).")) is { } id)
                {
                    loaded[id] = id == loading ? failure : null; // with no DLL or PCK it named no file, and has no code to fail
                    if (loaded[id] is { } error && !shown(error))
                        print(error);
                }
                (loading, failure) = (null, null);
            }
            if (shown(line))
                print(line);
        }
Console.WriteLine("screenshots: " + string.Join(", ", Directory.GetFiles(outDir, "*.png").Select(Path.GetFileName).Order()));
// A --workshop mod that didn't load, or failed to start, fails the run: the self-test passing says nothing about
// running beside it then (and the cause is that mod, not ours).
var notReady = workshopMods.Where(id => !loaded.TryGetValue(id, out var error) || error != null).ToList();
// --twins: each id of two or more given items loaded once, from the picked copy (the last given, PrepareSandbox), with
// no duplicate error. A picked copy the game refuses for its own reasons (a newer game version) isn't the pick failing.
var twinFails = 0;
var logged = LogLines();
foreach (var (id, item) in TwinPicks())
{
    var folder = $"workshop-{item}";
    var finished = logged.Select((line, at) => (line, at)).Where(p => p.line.Contains("Finished mod initialization for") && p.line.EndsWith($"({id}).")).ToList();
    var duplicate = logged.Any(l => l.Contains($"Tried to load mod with id {id}, but a mod is already loaded"));
    var from = finished.Count == 1 ? logged.Take(finished[0].at).LastOrDefault(l => l.Contains("Loading assembly DLL") || l.Contains("Loading Godot PCK")) : null;
    if (finished.Count == 1 && !duplicate && from != null && (from.Contains(folder + "\\") || from.Contains(folder + "/")))
        Console.WriteLine($"PASS twins: {id} loaded from {folder} as picked");
    else if (finished.Count == 0 && !duplicate && logged.Any(l => l.Contains($"Tried to load mod with id {id}, but its declared min game version")))
    {
        Console.WriteLine($"INFO twins: {id}: the picked copy ({folder}) needs a newer game, so it failed for its own reasons (not the pick)");
        notReady.Remove(id);
    }
    else
    {
        twinFails++;
        Console.WriteLine($"FAIL twins: {id} finished loading {finished.Count} time(s){(duplicate ? ", with a duplicate-id error" : "")}, "
            + $"from {from ?? "no file named"}; picked {folder}");
    }
}
foreach (var id in notReady)
    Console.WriteLine(loaded.ContainsKey(id)
        ? $"FAIL --workshop: {id} loaded but failed to start (its error is in the log above), so this run didn't test beside all of it"
        : $"FAIL --workshop: {id} didn't load (the log above says why), so this run didn't test beside it");
return !File.Exists(results) || text.Contains("FAIL") || notReady.Count > 0 || twinFails > 0 ? 1 : 0;

// --twins: for each mod id two or more --workshop items have, the item picked to load: the last one given.
List<(string Id, string Item)> TwinPicks() => !twins ? [] : [.. workshopManifests.GroupBy(m => Id(m.Manifest))
    .Where(g => g.Select(m => m.Item).Distinct().Count() > 1).Select(g => (g.Key, g.Last().Item))];

// The dev copy (hard links to the installed game, except mods\), linked again when the install changed and is on the
// copy's branch. Returns why there's no copy to run (none yet, and the install is on the other branch), or null.
string? LinkDevCopy()
{
    var info = Path.Combine(GameDir, "release_info.json");
    var devInfo = Path.Combine(DevDir, "release_info.json");
    if (File.Exists(devInfo) && File.ReadAllText(devInfo) == File.ReadAllText(info))
        return null;
    var wanted = stable ? "public" : "public-beta";
    if (InstalledBranch() is { } installed && installed != wanted)
    {
        if (!File.Exists(devInfo))
            return $"{DevDir} doesn't exist yet, and Steam has the {installed} branch installed: switch it to {wanted} and run again.";
        Console.WriteLine($"Steam has the {installed} branch installed: the {wanted} dev copy in {DevDir} is kept as it is");
        return null;
    }
    Console.WriteLine($"linking the dev copy in {DevDir}");
    if (Directory.Exists(DevDir))
        Directory.Delete(DevDir, recursive: true); // deleting links leaves the installed files alone
    foreach (var file in Directory.EnumerateFiles(GameDir, "*", SearchOption.AllDirectories))
    {
        var relative = Path.GetRelativePath(GameDir, file);
        if (relative.StartsWith("mods" + Path.DirectorySeparatorChar))
            continue;
        Link(file, Path.Combine(DevDir, relative));
    }
    return null;
}

// The branch Steam has installed ("public" is stable, "public-beta" the beta), from the game's app manifest; null
// when it can't be read (the copy is then linked whenever the install changes, as before).
string? InstalledBranch()
{
    var manifest = Path.Combine(steamapps, "appmanifest_2868840.acf");
    if (!File.Exists(manifest))
        return null;
    var keys = Regex.Matches(File.ReadAllText(manifest), @"""BetaKey""\s+""([^""]*)""");
    return keys.Count == 0 ? "public" : keys[^1].Groups[1].Value;
}

// The dev copy's mods: our fresh build plus links to mods installed for play: all with --real, else Better Mod Menu
// only, so runs match on every machine. Not Better Mod Menu's portable data, nor an installed mod sharing an id with a
// --workshop item.
void SyncMods()
{
    var mods = Path.Combine(DevDir, "mods");
    if (Directory.Exists(mods))
        Directory.Delete(mods, recursive: true);
    foreach (var (dir, _) in Directory.GetDirectories(Path.Combine(GameDir, "mods")).Select(d => (Dir: d, Ids: ManifestIds(d).ToList()))
        .Where(m => Path.GetFileName(m.Dir) != ModId && !m.Ids.Any(workshopMods.Contains) && (real || m.Ids.Contains("BetterModMenu"))))
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Where(f => !Path.GetFileName(f).StartsWith("mod_profiles.")))
            Link(file, Path.Combine(mods, Path.GetRelativePath(Path.Combine(GameDir, "mods"), file)));
    if (real)
    {
        // --real: your Workshop mods too, as local folders (switched off later), except the --workshop items and other
        // items with their mod ids.
        foreach (var dir in Directory.GetDirectories(WorkshopDir).Where(d => !workshopItems.Contains(Path.GetFileName(d)) && !ManifestIds(d).Any(workshopMods.Contains)))
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                Link(file, Path.Combine(mods, "workshop-" + Path.GetFileName(dir), Path.GetRelativePath(dir, file)));
    }
    else
    {
        // Empty test mods with dependencies between them (tools/fixtures/mods): for the load order checks, a mod that
        // won't load, and one by our author to edit, tag and search for.
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "tools", "fixtures", "mods"), "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(mods, Path.GetRelativePath(Path.Combine(root, "tools", "fixtures", "mods"), file));
            Directory.CreateDirectory(Path.GetDirectoryName(target) ?? mods);
            File.Copy(file, target);
        }
        // And the Workshop copies of fixture mods (SmmTestDual's), in the dev copy's mods_STEAMTEST, which the game reads
        // as Workshop items, so every run has a mod installed from both sources. Taken out after the game exits.
        foreach (var file in Directory.Exists(steamTestFixtures) ? Directory.EnumerateFiles(steamTestFixtures, "*", SearchOption.AllDirectories) : [])
        {
            var target = Path.Combine(steamTest, Path.GetRelativePath(steamTestFixtures, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target) ?? steamTest);
            File.Copy(file, target, overwrite: true);
        }
    }
    // --workshop: the given items, copied rather than linked, so nothing a mod writes over its own files
    // reaches Steam's copy. The folder above is emptied every run, so they're gone from a run without them.
    foreach (var item in workshopItems)
        foreach (var file in Directory.EnumerateFiles(Path.Combine(WorkshopDir, item), "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(mods, "workshop-" + item, Path.GetRelativePath(Path.Combine(WorkshopDir, item), file));
            Directory.CreateDirectory(Path.GetDirectoryName(target) ?? mods);
            File.Copy(file, target);
        }
    // --bmm-local: Better Mod Menu's local-fix build (built by its install-local.sh) in place of the installed one.
    if (args.Contains("--bmm-local"))
    {
        var local = Path.Combine(BmmRepo, ".godot", "mono", "temp", "bin", "Release", "BetterModMenu.dll");
        var dll = Path.Combine(mods, "BetterModMenu", "BetterModMenu.dll");
        File.Delete(dll); // the link only
        File.Copy(local, dll);
    }
    var ours = Path.Combine(mods, ModId);
    Directory.CreateDirectory(ours);
    foreach (var ext in new[] { ".pck", ".json", ".dll" })
        File.Copy(Path.Combine(smoke ? distDir : Path.Combine(root, "out"), ModId + ext), Path.Combine(ours, ModId + ext));
}

// Mods accepted with Better Mod Menu off (it patches the same screen), and for tests our data reset
// to the fixture (none for a first run) and Better Mod Menu's cleared (its fixture for --first-run-bmm).
// The game creates settings.save on first launch; a minimal one is enough before that.
// The sandbox's save folders emptied: profileN\saves and modded\profileN\saves (each profile's progress, runs, history).
void EmptySaves()
{
    var folders = new[] { sandbox, Path.Combine(sandbox, "modded") }.Where(Directory.Exists)
        .SelectMany(dir => Directory.GetDirectories(dir, "profile*")).Select(profile => Path.Combine(profile, "saves")).Where(Directory.Exists);
    foreach (var saves in folders)
    {
        foreach (var file in Directory.GetFiles(saves))
            File.Delete(file);
        foreach (var dir in Directory.GetDirectories(saves))
            Directory.Delete(dir, recursive: true);
    }
}

void PrepareSandbox(bool resetData)
{
    Directory.CreateDirectory(sandbox);
    var settingsPath = Path.Combine(sandbox, "settings.save");
    var settings = File.Exists(settingsPath) ? JsonNode.Parse(File.ReadAllText(settingsPath))?.AsObject() ?? [] : [];
    var mods = settings["mod_settings"] as JsonObject ?? [];
    mods["mods_enabled"] = true;
    // A fresh mod list every run: everything on except Better Mod Menu (on with --bmm, after ours with --bmm-after), its
    // fork's folder (--real) and the fixture's Test Retired (off for the self-test's searches; on with --crash, so that
    // launch differs from the last working one). Ours first (after Better Mod Menu with plain --bmm), as the mod keeps
    // itself: its Init picks which copy of a mod installed twice loads, before the game tries one.
    var list = new JsonArray();
    var bmmFirst = bmm && !args.Contains("--bmm-after");
    if (!bmmFirst)
        list.Add((JsonNode)new JsonObject { ["id"] = ModId, ["is_enabled"] = true, ["source"] = "mods_directory" });
    list.Add((JsonNode)new JsonObject { ["id"] = "BetterModMenu", ["is_enabled"] = bmm, ["source"] = "mods_directory" });
    if (bmmFirst)
        list.Add((JsonNode)new JsonObject { ["id"] = ModId, ["is_enabled"] = true, ["source"] = "mods_directory" });
    list.Add((JsonNode)new JsonObject { ["id"] = real ? "BetterModMenuFork" : "SmmTestRetired", ["is_enabled"] = crash && !real, ["source"] = "mods_directory" });
    // The fixture's two copies of SmmTestTwin in a known order: the newer one needs Test Framework, listed before them,
    // so the older one (SmmTestTwinA) always sorts first and the picked newer one (SmmTestTwinB) second. Both share the
    // id's place, so that rests on .NET's PriorityQueue keeping its root on a tie (A is queued first and is the root when
    // B joins): if that changes, the self-test's "swapped" check fails, not the pick. Any pair that tests a swap has
    // this tie.
    if (!real)
        foreach (var id in new[] { "SmmTestLib", "SmmTestTwin" })
            list.Add((JsonNode)new JsonObject { ["id"] = id, ["is_enabled"] = true, ["source"] = "mods_directory" });
    // The fixture's mod from both sources (SmmTestDual): its local copy on, its Workshop copy (mods_STEAMTEST) off, listed
    // side by side, so the game takes the Workshop copy out and its rebuild lists it after all the rest; the main menu
    // keeps them together (the order rule), as the self-test checks.
    if (!real)
        foreach (var (from, on) in new[] { ("mods_directory", true), ("steam_workshop", false) })
            list.Add((JsonNode)new JsonObject { ["id"] = "SmmTestDual", ["is_enabled"] = on, ["source"] = from });
    // --workshop: the items' mods on, after ours, in the order given (--twins: an entry per copy). An entry above for the
    // same id (Better Mod Menu's, when its Workshop item is given) gives way.
    foreach (var id in workshopMods)
        if (list.FirstOrDefault(entry => (string?)entry?["id"] == id) is { } earlier)
            list.Remove(earlier);
    foreach (var (_, manifest) in workshopManifests)
        list.Add((JsonNode)new JsonObject { ["id"] = Id(manifest), ["is_enabled"] = true, ["source"] = "mods_directory" });
    // --real: every other mod is listed but switched off, so none of their code runs (some write config
    // files to the shared user folder); only the manager and any --workshop mods load.
    if (real)
        foreach (var id in ManifestIds(Path.Combine(DevDir, "mods")).Where(id => id is not (ModId or "BetterModMenu" or "BetterModMenuFork") && !workshopMods.Contains(id)))
            list.Add((JsonNode)new JsonObject { ["id"] = id, ["is_enabled"] = false, ["source"] = "mods_directory" });
    mods["mod_list"] = list;
    settings["mod_settings"] = mods;
    settings["seen_ea_disclaimer"] = true; // the one-time Early Access popup would cover every screenshot
    settings["volume_master"] = 0.0;      // silent, so dev runs don't play over your own game
    settings["fullscreen"] = false;       // a 1920x1080 window (or --size's), easy to tell apart and to move aside
    settings["window_size"] = new JsonObject { ["X"] = windowSize.X, ["Y"] = windowSize.Y };
    File.WriteAllText(settingsPath, settings.ToJsonString(new() { WriteIndented = true }));

    var data = Path.Combine(sandbox, "mod_data", ModId);
    if (!resetData)
    {
        PickTwins(data); // --crash keeps --crash-prepare's data, which has no pick for the --workshop twins
        return;
    }
    if (Directory.Exists(data))
        Directory.Delete(data, recursive: true);
    // Better Mod Menu's data beside ours, cleared every run so no saves or fixture carry over.
    var bmmDir = Path.Combine(sandbox, "mod_data", "BetterModMenu");
    if (Directory.Exists(bmmDir))
        Directory.Delete(bmmDir, recursive: true);
    // The --workshop mods' data there too (mods keep it in a folder named for their id), so one run's saves and
    // presets can't change the next.
    foreach (var id in workshopMods)
    {
        var dir = Path.GetFullPath(Path.Combine(sandbox, "mod_data", id));
        if (Path.GetFileName(dir) == id && Directory.Exists(dir)) // mod_data\<id> only: the id is the mod author's, not ours
            Directory.Delete(dir, recursive: true);
    }
    if (bmmData)
    {
        Directory.CreateDirectory(bmmDir);
        foreach (var file in Directory.GetFiles(Path.Combine(root, "tools", "fixtures", "bmm")))
            File.Copy(file, Path.Combine(bmmDir, Path.GetFileName(file)));
    }
    if (firstRun)
        return; // no manager.json (nor its folder), as before the mod's first launch
    Directory.CreateDirectory(data);
    // --real: a copy of your own manager data (read only; the original is never written).
    var source = real
        ? Directory.GetFiles(Path.Combine(userData, "steam"), "manager.json", SearchOption.AllDirectories)
            .FirstOrDefault(f => f.Contains(Path.Combine("mod_data", ModId)))
        : null;
    File.Copy(source ?? Path.Combine(root, "tools", "fixtures", "manager.json"), Path.Combine(data, "manager.json"));
    PickTwins(data);
}

// --twins: the last given copy of each id picked to load (copied in as a local folder: l:workshop-<item>), in the
// sandbox's manager.json (none: nothing to pick in), as a file from before copies_off has it: the pick (copy_picks),
// and our record of the id's copies in the order given ("copies"), which the pick is read with, every other copy off
// (Copies.LegacyOff). Without the record no copy would be off, and the higher version would load, picked or not. A file
// with copies_off already (--crash keeps --crash-prepare's data) has the other copies put in it instead.
void PickTwins(string data)
{
    var file = Path.Combine(data, "manager.json");
    if (TwinPicks() is not { Count: > 0 } picked || !File.Exists(file) || JsonNode.Parse(File.ReadAllText(file)) is not JsonObject manager)
        return;
    var keys = (manager["copy_picks"] as JsonArray ?? []).Select(p => (string?)p).OfType<string>()
        .Where(key => !picked.Any(p => key.StartsWith(p.Id + "|"))).Concat(picked.Select(p => $"{p.Id}|l:workshop-{p.Item}")).Order(StringComparer.Ordinal);
    manager["copy_picks"] = new JsonArray([.. keys.Select(k => (JsonNode?)JsonValue.Create(k))]);
    if (manager["copies"] is not JsonObject record)
        manager["copies"] = record = new JsonObject();
    List<string> CopiesOf(string id) => [.. workshopManifests.Where(m => Id(m.Manifest) == id).Select(m => $"l:workshop-{m.Item}").Distinct()];
    foreach (var (id, _) in picked)
        record[id] = new JsonArray([.. CopiesOf(id).Select(c => (JsonNode?)JsonValue.Create(c))]);
    if (manager["copies_off"] is JsonArray off)
    {
        var kept = off.Select(k => (string?)k).OfType<string>().Where(k => !picked.Any(p => k.StartsWith(p.Id + "|")))
            .Concat(picked.SelectMany(p => CopiesOf(p.Id).Where(c => c != $"l:workshop-{p.Item}").Select(c => $"{p.Id}|{c}"))).Order(StringComparer.Ordinal);
        manager["copies_off"] = new JsonArray([.. kept.Select(k => (JsonNode?)JsonValue.Create(k))]);
    }
    File.WriteAllText(file, manager.ToJsonString(new() { WriteIndented = true }));
}

// The chain from the last mod in chain to the first mod it needs (directly or not) that won't be on in this run, or
// null; a chain that loops back is returned too (the game refuses every mod in a loop).
List<string>? MissingDependency(List<string> chain, HashSet<string> seen)
{
    seen.Add(chain[^1]);
    foreach (var id in DependencyIds(on[chain[^1]]))
        if (!on.ContainsKey(id) || chain.Contains(id))
            return [.. chain, id];
        else if (!seen.Contains(id) && MissingDependency([.. chain, id], seen) is { } missing)
            return missing;
    return null;
}

// The manifests under a mods folder (a manifest is a JSON file with "id" and "has_dll"), and their mod ids.
static IEnumerable<JsonObject> Manifests(string mods)
{
    foreach (var file in Directory.EnumerateFiles(mods, "*.json", SearchOption.AllDirectories))
    {
        JsonObject? json;
        try { json = JsonNode.Parse(File.ReadAllText(file)) as JsonObject; }
        catch (Exception) { continue; } // not JSON (or not ours to judge)
        if (json?["id"] is JsonValue && json.ContainsKey("has_dll"))
            yield return json;
    }
}

static IEnumerable<string> ManifestIds(string mods) => Manifests(mods).Select(Id);

static string Id(JsonObject manifest) => (string?)manifest["id"] ?? "";

// The mod ids a manifest depends on: {"id": ...} entries, or plain strings as a few manifests have.
static IEnumerable<string> DependencyIds(JsonObject manifest) =>
    (manifest["dependencies"] as JsonArray ?? []).Select(d => (d as JsonObject)?["id"] ?? d).OfType<JsonValue>()
        .Select(id => id.TryGetValue<string>(out var text) ? text : null).OfType<string>();

static void Link(string target, string link)
{
    Directory.CreateDirectory(Path.GetDirectoryName(link) ?? ".");
    if (!CreateHardLink(link, target, IntPtr.Zero))
        throw new IOException($"could not link {link} -> {target} (error {Marshal.GetLastWin32Error()})");
}

static string? ExePath(Process p)
{
    try { return p.MainModule?.FileName; }
    catch { return null; } // another user's or an elevated process
}

// Waits while a copy of the game other than the real install runs (a dev copy: they share the sandbox), saying so once;
// null when none is left, else why we stopped waiting (half an hour). One whose path can't be read counts as a dev copy.
string? WaitForOtherDevCopies()
{
    var real = Path.GetFullPath(GameDir).TrimEnd('\\') + "\\";
    List<string> Others() => [.. Process.GetProcessesByName("SlayTheSpire2").Select(p => ExePath(p) ?? $"process {p.Id}")
        .Where(path => !path.StartsWith(real, StringComparison.OrdinalIgnoreCase))];
    var giveUp = DateTime.Now.AddMinutes(30);
    var said = false;
    while (Others() is { Count: > 0 } others)
    {
        if (DateTime.Now > giveUp)
            return $"Another dev copy of the game is still running after half an hour: {string.Join(", ", others)}.";
        if (!said)
            Console.WriteLine($"waiting for another dev copy of the game to close (they share the sandbox): {string.Join(", ", others)}");
        said = true;
        Thread.Sleep(5000);
    }
    return null;
}

// The shared dev-run lock: the named mutex Local\sts2-dev-run, with %TEMP%\sts2-dev-run.json naming who holds it. Waits up
// to 15 minutes, naming the holder once (none with noWait); a holder that died left the mutex abandoned, which counts as
// free. The lock to dispose (on this thread: a mutex is released by the thread that took it), or null and why not.
static IDisposable? TakeDevRunLock(string what, bool noWait, out string? refused)
{
    refused = null;
    var holderFile = Path.Combine(Path.GetTempPath(), "sts2-dev-run.json");
    string Holder()
    {
        try
        {
            // Shared, so reading it never stops its holder deleting it.
            using var stream = new FileStream(holderFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonNode.Parse(new StreamReader(stream).ReadToEnd()) is JsonObject held
                ? $"{held["repo"]} ({held["mode"]}, process {held["pid"]}, since {held["started"]})" : "another run";
        }
        catch
        {
            return "another run (no holder file)";
        }
    }
    var mutex = new Mutex(false, @"Local\sts2-dev-run");
    try
    {
        if (!mutex.WaitOne(0))
        {
            if (noWait)
            {
                mutex.Dispose();
                refused = $"another dev run holds the game: {Holder()} (--no-wait)";
                return null;
            }
            Console.WriteLine($"waiting for another dev run to finish (one game at a time across every repo): {Holder()}");
            if (!mutex.WaitOne(TimeSpan.FromMinutes(15)))
            {
                refused = $"another dev run still holds the game after 15 minutes: {Holder()}";
                mutex.Dispose();
                return null;
            }
        }
    }
    catch (AbandonedMutexException)
    {
        // the last holder died without letting go: ours now, and its file is stale
    }
    // The holder file only names us for those waiting: if it can't be written, the lock still holds.
    try
    {
        File.WriteAllText(holderFile, new JsonObject
        {
            ["repo"] = "spire-mod-manager", ["mode"] = what, ["pid"] = Environment.ProcessId, ["started"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            // The checkout the run is from: build.cs won't clear its out/ (the run's build and results) meanwhile.
            ["dir"] = Path.GetDirectoryName(ScriptDir()) ?? "",
        }.ToJsonString());
    }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
    {
        Console.WriteLine($"(couldn't write {holderFile}: {e.Message}; the lock holds all the same)");
    }
    return new DevRunLock(mutex, holderFile);
}

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}


static string ScriptDir([CallerFilePath] string path = "") => Path.GetDirectoryName(path) ?? ".";

// An environment variable's value (CONTRIBUTING.md, "What you need"), or the author's own path when it isn't set.
static string Env(string name, string fallback) =>
    Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : fallback;

// The Steam library with the game: STS2_STEAMAPPS, else the library in Steam's libraryfolders.vdf that has it, else
// Steam's default folder (the same in build.cs).
static string SteamApps()
{
    if (Environment.GetEnvironmentVariable("STS2_STEAMAPPS") is { Length: > 0 } set)
        return set;
    var steam = (OperatingSystem.IsWindows() ? Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) : null) is string { Length: > 0 } path
        ? path : @"C:\Program Files (x86)\Steam";
    var list = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
    var libraries = File.Exists(list)
        ? Regex.Matches(File.ReadAllText(list), "\"path\"\\s+\"([^\"]+)\"").Select(m => Path.Combine(m.Groups[1].Value.Replace(@"\\", @"\"), "steamapps"))
        : [];
    var found = libraries.Prepend(Path.Combine(steam, "steamapps"))
        .FirstOrDefault(library => Directory.Exists(Path.Combine(library, "common", "Slay the Spire 2")));
    return Path.GetFullPath(found ?? Path.Combine(steam, "steamapps"));
}

static string FullDir(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);

[DllImport("user32.dll")]
static extern IntPtr GetForegroundWindow();

[DllImport("user32.dll")]
static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

[DllImport("user32.dll")]
static extern bool SetForegroundWindow(IntPtr window);

// Lets the shared dev-run lock go: its holder file, then the mutex, which is released whatever the file does. There's
// no Ctrl+C handler: Ctrl+C ends the process, which leaves the mutex abandoned, and that counts as free.
sealed class DevRunLock(Mutex mutex, string holderFile) : IDisposable
{
    public void Dispose()
    {
        try
        {
            File.Delete(holderFile);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // a stale file is overwritten by the next holder
        }
        finally
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
        }
    }
}
