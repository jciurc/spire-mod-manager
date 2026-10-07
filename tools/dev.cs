// Dev runs of the game, kept apart from real play, so Jordan can play through Steam at the same time.
//
//   dotnet run tools/dev.cs test      build, reset the sandbox, run the in-game self-test
//                                     (--headless: no window and no screenshots, UI dumps only;
//                                      --bmm: with Better Mod Menu loaded, to test stepping aside)
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
const string ModId = "SpireModManager";
const int TimeoutSeconds = 150;
var root = Path.GetDirectoryName(ScriptDir()) ?? ".";
var userData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SlayTheSpire2");
var sandbox = Path.Combine(userData, "default", "1");
var outDir = Path.Combine(root, "out", "selftest");
var logPath = Path.Combine(root, "out", "dev.log");
var mode = args.FirstOrDefault() ?? "test";

if (mode is not ("test" or "launch"))
    return Fail("usage: dotnet run tools/dev.cs [test [--headless] [--bmm]|launch]");
if (Process.GetProcessesByName("SlayTheSpire2").Any(p => ExePath(p)?.StartsWith(DevDir, StringComparison.OrdinalIgnoreCase) == true))
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
if (args.Contains("--headless"))
    start.ArgumentList.Add("--headless");
start.Environment["SPIRE_MOD_MANAGER_DEV"] = "1"; // the mod marks the window "[DEV]"
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
// The game echoes its whole log to stdout; we read the log file instead.
start.RedirectStandardOutput = start.RedirectStandardError = true;
var game = Process.Start(start) ?? throw new InvalidOperationException("could not start the game");
game.OutputDataReceived += (_, _) => { };
game.ErrorDataReceived += (_, _) => { };
game.BeginOutputReadLine();
game.BeginErrorReadLine();
if (!game.WaitForExit(TimeoutSeconds * 1000))
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
    // Empty test mods with dependencies between them (tools/fixtures/mods), for the load order checks.
    foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "tools", "fixtures", "mods"), "*", SearchOption.AllDirectories))
    {
        var target = Path.Combine(mods, Path.GetRelativePath(Path.Combine(root, "tools", "fixtures", "mods"), file));
        Directory.CreateDirectory(Path.GetDirectoryName(target) ?? mods);
        File.Copy(file, target);
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
    var list = mods["mod_list"] as JsonArray ?? [];
    if (list.FirstOrDefault(m => (string?)m?["id"] == "BetterModMenu") is not JsonObject bmm)
        list.Add((JsonNode)(bmm = new JsonObject { ["id"] = "BetterModMenu", ["source"] = "mods_directory" }));
    bmm["is_enabled"] = args.Contains("--bmm"); // --bmm: test how we step aside when it's loaded
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
    File.Copy(Path.Combine(root, "tools", "fixtures", "manager.json"), Path.Combine(data, "manager.json"));
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
