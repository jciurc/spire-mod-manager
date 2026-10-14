// Dev runs of the game, kept apart from real play, so Jordan can play through Steam at the same time.
//
//   dotnet run tools/dev.cs test      build, reset the sandbox, run the in-game self-test headless
//                                     (--window: with a window, for screenshots, kept behind others;
//                                      --bmm: with Better Mod Menu loaded too (listed first; --bmm-after:
//                                      after ours, as in Jordan's game; --bmm-local: its local-fix build
//                                      from the BmmRepo checkout below instead of the installed one);
//                                      --workshop <id>[,<id>...]: those Steam Workshop items too (subscribed
//                                      ones only: Steam downloads no others), copied into the dev copy's
//                                      mods for this run and switched on after ours, in the order given;
//                                      --real: Jordan's own mods, switched off, and a copy of his data;
//                                      --first-run: a short suite for a first run, with no data of ours and
//                                      none of Better Mod Menu's; --first-run-bmm: the same, but with Better
//                                      Mod Menu's data (tools/fixtures/bmm) there to import, and Better Mod
//                                      Menu installed but off, or on with --bmm)
//   dotnet run tools/dev.cs launch    build, open the game in the sandbox to poke at by hand
//                                     (--first-run, --first-run-bmm: starting from that state, not the last one;
//                                      --workshop: as for test)
//
// Isolation:
// - Dev copy: sts2-dev at the root of the game's drive (D:\sts2-dev here; STS2_DEV_DIR picks another
//   folder on that drive) holds hard links to the installed game's files (same drive, no extra space)
//   and its own mods\ folder: our fresh build plus links to the other local mods. Builds never go into
//   the real mods folder here; `dotnet run build.cs` does that, for play, when the game is closed.
//   The copy is re-linked whenever the installed game's release_info.json changes (a Steam update).
// - --force-steam off: no Steam presence, no cloud, and the game keeps its data in
//   %APPDATA%\SlayTheSpire2\default\1\ instead of steam\<id>\, so real saves and mod lists are never
//   touched. Only local mods load (Workshop mods come through Steam; --workshop copies some in as local ones).
// - --log-file: the dev log goes to out\dev.log, not the shared godot.log.
//
// test: resets the sandbox to tools/fixtures (mods accepted, Better Mod Menu off, our manager.json, no
// Better Mod Menu data; the first-run options change the last two), sets SPIRE_MOD_MANAGER_SELFTEST so
// SelfTest.cs runs and quits (SPIRE_MOD_MANAGER_SELFTEST_MODE picks the --real or first-run suite), then
// prints out/selftest/results.txt and our log lines. Screenshots (*.png) and UI dumps (*.txt) land in
// out/selftest. Exit code 1 on any FAIL.
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

// Full paths without a trailing separator: the running-game check below compares DevDir with the exe's folder.
var steamapps = FullDir(Env("STS2_STEAMAPPS", @"D:\SteamLibrary\steamapps")); // the Steam library with the game
var GameDir = Path.Combine(steamapps, "common", "Slay the Spire 2");
var DevDir = FullDir(Env("STS2_DEV_DIR", Path.Combine(Path.GetPathRoot(GameDir) ?? "", "sts2-dev"))); // same drive
var WorkshopDir = Path.Combine(steamapps, "workshop", "content", "2868840");
var BmmRepo = Env("STS2_BMM_REPO", @"C:\repos\gaming\slay the spire\bettermodmenu"); // Better Mod Menu's local-fix checkout (--bmm-local)
const string ModId = "SpireModManager";
const int TimeoutSeconds = 300; // the whole run, killed after this; with --bmm and the language reloads the suite passed 150 s on a busy machine
var root = Path.GetDirectoryName(ScriptDir()) ?? ".";
var userData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SlayTheSpire2");
var sandbox = Path.Combine(userData, "default", "1");
var outDir = Path.Combine(root, "out", "selftest");
var logPath = Path.Combine(root, "out", "dev.log");
var mode = args.FirstOrDefault() ?? "test";
var real = args.Contains("--real"); // Jordan's own mods (switched off) and a copy of his manager data
var bmm = args.Any(a => a.StartsWith("--bmm")); // Better Mod Menu on (any --bmm option)
var bmmData = args.Contains("--first-run-bmm"); // its fixture data in the sandbox, for our first-run import
var firstRun = bmmData || args.Contains("--first-run"); // no manager.json of ours
var workshopAt = Array.IndexOf(args, "--workshop"); // the Workshop item ids follow it, comma-separated
var workshopItems = workshopAt < 0 ? [] : (args.ElementAtOrDefault(workshopAt + 1) ?? "")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToArray();
var workshopMods = new List<string>(); // the mod ids in those items' manifests, in the order given

if (mode is not ("test" or "launch"))
    return Fail("usage: dotnet run tools/dev.cs [test [--window] [--bmm[-after]] [--bmm-local] [--real|--first-run|--first-run-bmm]|launch [--first-run|--first-run-bmm]] [--workshop <id>[,<id>...]]");
if (real && firstRun)
    return Fail("--real brings Jordan's own data, so it can't be a first run");
// --workshop: every item has to be there, or the run would test without it and look like a pass. So it takes
// one list: a second --workshop, --workshop=..., or ids after a space would be dropped without a word.
if (args.Count(a => a.StartsWith("--workshop")) != (workshopAt < 0 ? 0 : 1))
    return Fail("--workshop comes once, with its ids after a space, as in: --workshop 3747605109,3750485606");
if (args.Where((a, i) => i > 0 && i != workshopAt + 1 && !a.StartsWith("--")).FirstOrDefault() is { } stray)
    return Fail($"\"{stray}\" isn't an option. Workshop ids go after --workshop, comma-separated with no spaces, as in: --workshop 3747605109,3750485606");
if (workshopAt >= 0 && workshopItems.Length == 0)
    return Fail("--workshop needs Workshop item ids, comma-separated, as in: --workshop 3747605109,3750485606");
foreach (var item in workshopItems)
{
    if (!item.All(char.IsAsciiDigit))
        return Fail($"--workshop takes Workshop item ids (numbers), not \"{item}\"");
    var dir = Path.Combine(WorkshopDir, item);
    if (!Directory.Exists(dir))
        return Fail($"Workshop item {item} isn't in {WorkshopDir}. Subscribe to it in Steam first: Steam downloads only subscribed items.");
    var ids = ManifestIds(dir).ToList();
    if (ids.Count == 0)
        return Fail($"Workshop item {item} has no mod manifest in {dir}");
    workshopMods = workshopMods.Union(ids).ToList();
}
if (Process.GetProcessesByName("SlayTheSpire2").Any(p => string.Equals(Path.GetDirectoryName(ExePath(p)), DevDir, StringComparison.OrdinalIgnoreCase)))
    return Fail($"A dev copy of the game is already running from {DevDir}.");

var build = Process.Start("dotnet", ["run", Path.Combine(root, "build.cs"), "--no-install"]);
build.WaitForExit();
if (build.ExitCode != 0)
    return Fail("build failed");

LinkDevCopy();
SyncMods();
PrepareSandbox(mode == "test" || firstRun);

var start = new ProcessStartInfo(Path.Combine(DevDir, "SlayTheSpire2.exe"), ["--force-steam", "off", "--log-file", logPath])
    { WorkingDirectory = DevDir };
// Tests run headless (no window, UI dumps only) unless --window asks for screenshots; then the window
// is sent behind everything and focus handed back (see below), so Jordan's work isn't interrupted.
var windowed = mode == "launch" || args.Contains("--window");
if (!windowed)
    start.ArgumentList.Add("--headless");
start.Environment["SPIRE_MOD_MANAGER_DEV"] = "1"; // the mod marks the window "[DEV]"
if (File.Exists(Path.Combine(DevDir, "override.cfg")))
    File.Delete(Path.Combine(DevDir, "override.cfg")); // an earlier no-focus experiment: it kept the window on top
var previousForeground = GetForegroundWindow();

if (mode == "launch")
{
    Process.Start(start);
    Console.WriteLine($"launched the dev copy in the sandbox (log: {logPath})");
    return 0;
}

if (Directory.Exists(outDir))
    Directory.Delete(outDir, recursive: true);
Directory.CreateDirectory(outDir);
start.Environment["SPIRE_MOD_MANAGER_SELFTEST"] = outDir;
if (real)
    start.Environment["SPIRE_MOD_MANAGER_SELFTEST_MODE"] = "real";
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
    Console.WriteLine($"game still running after {TimeoutSeconds}s, killing it");
    game.Kill();
    game.WaitForExit();
}

var results = Path.Combine(outDir, "results.txt");
var text = File.Exists(results) ? File.ReadAllText(results) : "NO RESULTS (test did not finish)\n";
Console.Write(text);
Console.WriteLine("--- log ---");
if (File.Exists(logPath))
    using (var log = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
    using (var reader = new StreamReader(log))
        while (reader.ReadLine() is { } line)
            if (line.Contains($"[{ModId}]") || (line.Contains("[ERROR]") && line.Contains("Spire")))
                Console.WriteLine(line.Length > 400 ? line[..400] : line);
Console.WriteLine("screenshots: " + string.Join(", ", Directory.GetFiles(outDir, "*.png").Select(Path.GetFileName).Order()));
return !File.Exists(results) || text.Contains("FAIL") ? 1 : 0;

// Hard links to every installed file except mods\, rebuilt when the game's release info changes.
void LinkDevCopy()
{
    var info = Path.Combine(GameDir, "release_info.json");
    var devInfo = Path.Combine(DevDir, "release_info.json");
    if (File.Exists(devInfo) && File.ReadAllText(devInfo) == File.ReadAllText(info))
        return;
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
}

// The dev copy's mods: our fresh build, plus links to the other mods installed for play. Not Better Mod Menu's
// data if it keeps it beside its files ("portable mode", mod_profiles.*): our first-run import would find it
// there, and a run's Better Mod Menu data should be only what PrepareSandbox gives it. Nor an installed mod
// with the same id as a --workshop item's: the game would load only one of the two.
void SyncMods()
{
    var mods = Path.Combine(DevDir, "mods");
    if (Directory.Exists(mods))
        Directory.Delete(mods, recursive: true);
    foreach (var dir in Directory.GetDirectories(Path.Combine(GameDir, "mods")).Where(d => Path.GetFileName(d) != ModId && !ManifestIds(d).Any(workshopMods.Contains)))
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Where(f => !Path.GetFileName(f).StartsWith("mod_profiles.")))
            Link(file, Path.Combine(mods, Path.GetRelativePath(Path.Combine(GameDir, "mods"), file)));
    if (real)
    {
        // --real: Jordan's Workshop mods too, as local folders (switched off in PrepareSandbox), but not
        // the --workshop items: they're copied below.
        foreach (var dir in Directory.GetDirectories(WorkshopDir).Where(d => !workshopItems.Contains(Path.GetFileName(d))))
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                Link(file, Path.Combine(mods, "workshop-" + Path.GetFileName(dir), Path.GetRelativePath(dir, file)));
    }
    else
    {
        // Empty test mods with dependencies between them (tools/fixtures/mods), for the load order checks.
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "tools", "fixtures", "mods"), "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(mods, Path.GetRelativePath(Path.Combine(root, "tools", "fixtures", "mods"), file));
            Directory.CreateDirectory(Path.GetDirectoryName(target) ?? mods);
            File.Copy(file, target);
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
        File.Copy(Path.Combine(root, "out", ModId + ext), Path.Combine(ours, ModId + ext));
}

// Mods accepted with Better Mod Menu off (it patches the same screen), and for tests our data reset
// to the fixture (none for a first run) and Better Mod Menu's cleared (its fixture for --first-run-bmm).
// The game creates settings.save on first launch; a minimal one is enough before that.
void PrepareSandbox(bool resetData)
{
    Directory.CreateDirectory(sandbox);
    var settingsPath = Path.Combine(sandbox, "settings.save");
    var settings = File.Exists(settingsPath) ? JsonNode.Parse(File.ReadAllText(settingsPath))?.AsObject() ?? [] : [];
    var mods = settings["mod_settings"] as JsonObject ?? [];
    mods["mods_enabled"] = true;
    // A fresh mod list every run, so one run's toggles and order can't leak into the next: everything on
    // (no entry = on) except Better Mod Menu (--bmm: on, to test running alongside it) and its fork's folder
    // (never loads: its DLL name doesn't match its id). --bmm-after: listed after ours.
    var list = new JsonArray();
    if (args.Contains("--bmm-after"))
        list.Add((JsonNode)new JsonObject { ["id"] = ModId, ["is_enabled"] = true, ["source"] = "mods_directory" });
    list.Add((JsonNode)new JsonObject { ["id"] = "BetterModMenu", ["is_enabled"] = bmm, ["source"] = "mods_directory" });
    list.Add((JsonNode)new JsonObject { ["id"] = "BetterModMenuFork", ["is_enabled"] = false, ["source"] = "mods_directory" });
    // --workshop: the items' mods on, after ours, in the order given. An entry above for the same id (Better
    // Mod Menu's, when its Workshop item is given) gives way.
    if (workshopMods.Count > 0 && !args.Contains("--bmm-after"))
        list.Add((JsonNode)new JsonObject { ["id"] = ModId, ["is_enabled"] = true, ["source"] = "mods_directory" });
    foreach (var id in workshopMods)
    {
        if (list.FirstOrDefault(entry => (string?)entry?["id"] == id) is { } earlier)
            list.Remove(earlier);
        list.Add((JsonNode)new JsonObject { ["id"] = id, ["is_enabled"] = true, ["source"] = "mods_directory" });
    }
    // --real: every other mod is listed but switched off, so none of their code runs (some write config
    // files to the shared user folder); only the manager and any --workshop mods load.
    if (real)
        foreach (var id in ManifestIds(Path.Combine(DevDir, "mods")).Where(id => id is not (ModId or "BetterModMenu" or "BetterModMenuFork") && !workshopMods.Contains(id)))
            list.Add((JsonNode)new JsonObject { ["id"] = id, ["is_enabled"] = false, ["source"] = "mods_directory" });
    mods["mod_list"] = list;
    settings["mod_settings"] = mods;
    settings["seen_ea_disclaimer"] = true; // the one-time Early Access popup would cover every screenshot
    settings["volume_master"] = 0.0;      // silent, so dev runs don't play over Jordan's game
    settings["fullscreen"] = false;       // a 1920x1080 window, easy to tell apart and to move aside
    settings["window_size"] = new JsonObject { ["X"] = 1920, ["Y"] = 1080 };
    File.WriteAllText(settingsPath, settings.ToJsonString(new() { WriteIndented = true }));

    if (!resetData)
        return;
    var data = Path.Combine(sandbox, "mod_data", ModId);
    if (Directory.Exists(data))
        Directory.Delete(data, recursive: true);
    // Better Mod Menu's data sits beside ours (its account folder: where our first-run import looks, and where
    // it saves while loaded). Cleared every time, so neither its own saves nor a --first-run-bmm fixture carry
    // over into the next run.
    var bmmDir = Path.Combine(sandbox, "mod_data", "BetterModMenu");
    if (Directory.Exists(bmmDir))
        Directory.Delete(bmmDir, recursive: true);
    if (bmmData)
    {
        Directory.CreateDirectory(bmmDir);
        foreach (var file in Directory.GetFiles(Path.Combine(root, "tools", "fixtures", "bmm")))
            File.Copy(file, Path.Combine(bmmDir, Path.GetFileName(file)));
    }
    if (firstRun)
        return; // no manager.json (nor its folder), as before the mod's first launch
    Directory.CreateDirectory(data);
    // --real: a copy of Jordan's own manager data (read only; the original is never written).
    var source = real
        ? Directory.GetFiles(Path.Combine(userData, "steam"), "manager.json", SearchOption.AllDirectories)
            .FirstOrDefault(f => f.Contains(Path.Combine("mod_data", ModId)))
        : null;
    File.Copy(source ?? Path.Combine(root, "tools", "fixtures", "manager.json"), Path.Combine(data, "manager.json"));
}

// Mod ids from the manifests under a mods folder (a manifest is a JSON file with "id" and "has_dll").
static IEnumerable<string> ManifestIds(string mods)
{
    foreach (var file in Directory.EnumerateFiles(mods, "*.json", SearchOption.AllDirectories))
    {
        JsonObject? json;
        try { json = JsonNode.Parse(File.ReadAllText(file)) as JsonObject; }
        catch (Exception) { continue; } // not JSON (or not ours to judge)
        if (json?["id"] is JsonValue id && json.ContainsKey("has_dll"))
            yield return (string?)id ?? "";
    }
}

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

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}

static string ScriptDir([CallerFilePath] string path = "") => Path.GetDirectoryName(path) ?? ".";

// An environment variable's value (README.md, "Building"), or the author's own path when it isn't set.
static string Env(string name, string fallback) =>
    Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : fallback;

static string FullDir(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);

[DllImport("user32.dll")]
static extern IntPtr GetForegroundWindow();

[DllImport("user32.dll")]
static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

[DllImport("user32.dll")]
static extern bool SetForegroundWindow(IntPtr window);
