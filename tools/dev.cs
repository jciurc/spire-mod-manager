// Dev runs of the game, kept apart from real play, so Jordan can play through Steam at the same time.
//
//   dotnet run tools/dev.cs test      build, reset the sandbox, run the in-game self-test headless
//                                     (--window: with a window, for screenshots, kept behind others;
//                                      --bmm: with Better Mod Menu loaded too (listed first; --bmm-after:
//                                      after ours, as in Jordan's game; --bmm-local: its local-fix build
//                                      from ../bettermodmenu instead of the installed one);
//                                      --real: Jordan's own mods, switched off, and a copy of his data)
//   dotnet run tools/dev.cs launch    build, open the game in the sandbox to poke at by hand
//
// Isolation:
// - Dev copy: D:\sts2-dev holds hard links to the installed game's files (same drive, no extra space)
//   and its own mods\ folder: our fresh build plus links to the other local mods. Builds never go into
//   the real mods folder here; `dotnet run build.cs` does that, for play, when the game is closed.
//   The copy is re-linked whenever the installed game's release_info.json changes (a Steam update).
// - --force-steam off: no Steam presence, no cloud, and the game keeps its data in
//   %APPDATA%\SlayTheSpire2\default\1\ instead of steam\<id>\, so real saves and mod lists are never
//   touched. Only local mods load (Workshop mods come through Steam).
// - --log-file: the dev log goes to out\dev.log, not the shared godot.log.
//
// test: resets the sandbox to tools/fixtures (mods accepted, Better Mod Menu off, our manager.json),
// sets SPIRE_MOD_MANAGER_SELFTEST so SelfTest.cs runs and quits, then prints out/selftest/results.txt
// and our log lines. Screenshots (*.png) and UI dumps (*.txt) land in out/selftest. Exit code 1 on any FAIL.
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

const string GameDir = @"D:\SteamLibrary\steamapps\common\Slay the Spire 2";
const string DevDir = @"D:\sts2-dev";
const string WorkshopDir = @"D:\SteamLibrary\steamapps\workshop\content\2868840";
const string BmmRepo = @"C:eposgamingslay the spireettermodmenu"; // Better Mod Menu's local-fix checkout (--bmm-local)
const string ModId = "SpireModManager";
const int TimeoutSeconds = 150;
var root = Path.GetDirectoryName(ScriptDir()) ?? ".";
var userData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SlayTheSpire2");
var sandbox = Path.Combine(userData, "default", "1");
var outDir = Path.Combine(root, "out", "selftest");
var logPath = Path.Combine(root, "out", "dev.log");
var mode = args.FirstOrDefault() ?? "test";
var real = args.Contains("--real"); // Jordan's own mods (switched off) and a copy of his manager data
var bmm = args.Any(a => a.StartsWith("--bmm")); // Better Mod Menu on (any --bmm option)

if (mode is not ("test" or "launch"))
    return Fail("usage: dotnet run tools/dev.cs [test [--window] [--bmm[-after]] [--bmm-local] [--real]|launch]");
if (Process.GetProcessesByName("SlayTheSpire2").Any(p => string.Equals(Path.GetDirectoryName(ExePath(p)), DevDir, StringComparison.OrdinalIgnoreCase)))
    return Fail($"A dev copy of the game is already running from {DevDir}.");

var build = Process.Start("dotnet", ["run", Path.Combine(root, "build.cs"), "--no-install"]);
build.WaitForExit();
if (build.ExitCode != 0)
    return Fail("build failed");

LinkDevCopy();
SyncMods();
PrepareSandbox(mode == "test");

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

// The dev copy's mods: our fresh build, plus links to the other mods installed for play.
void SyncMods()
{
    var mods = Path.Combine(DevDir, "mods");
    if (Directory.Exists(mods))
        Directory.Delete(mods, recursive: true);
    foreach (var dir in Directory.GetDirectories(Path.Combine(GameDir, "mods")).Where(d => Path.GetFileName(d) != ModId))
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            Link(file, Path.Combine(mods, Path.GetRelativePath(Path.Combine(GameDir, "mods"), file)));
    if (real)
    {
        // --real: Jordan's Workshop mods too, as local folders (switched off in PrepareSandbox).
        foreach (var dir in Directory.GetDirectories(WorkshopDir))
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
// to the fixture. The game creates settings.save on first launch; a minimal one is enough before that.
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
    // --real: every other mod is listed but switched off, so none of their code runs (some write config
    // files to the shared user folder); only the manager loads.
    if (real)
        foreach (var id in ManifestIds(Path.Combine(DevDir, "mods")).Where(id => id is not (ModId or "BetterModMenu" or "BetterModMenuFork")))
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

[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);

[DllImport("user32.dll")]
static extern IntPtr GetForegroundWindow();

[DllImport("user32.dll")]
static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);

[DllImport("user32.dll")]
static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

[DllImport("user32.dll")]
static extern bool SetForegroundWindow(IntPtr window);
