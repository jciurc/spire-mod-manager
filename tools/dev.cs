// Dev runs of the game, kept apart from real play.
//
//   dotnet run tools/dev.cs test      build, install, reset the sandbox, run the in-game self-test
//   dotnet run tools/dev.cs launch    build, install, open the game in the sandbox to poke at by hand
//
// Dev runs start the exe with --force-steam off: no Steam presence (friends see nothing), no cloud,
// and the game keeps its data in %APPDATA%\SlayTheSpire2\default\1\ instead of steam\<id>\, so real
// saves, profiles and mod lists are never touched. Only mods in the game's mods\ folder load
// (Workshop mods come through Steam). Play normally by launching from Steam.
//
// test: resets the sandbox to tools/fixtures (mods accepted, Better Mod Menu off, our manager.json),
// sets SPIRE_MOD_MANAGER_SELFTEST so SelfTest.cs runs and quits, then prints out/selftest/results.txt
// and our log lines. Screenshots land in out/selftest/*.png. Exit code 1 on any FAIL.
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

const string GameDir = @"D:\SteamLibrary\steamapps\common\Slay the Spire 2";
const int TimeoutSeconds = 150;
var root = Path.GetDirectoryName(ScriptDir()) ?? ".";
var userData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SlayTheSpire2");
var sandbox = Path.Combine(userData, "default", "1");
var outDir = Path.Combine(root, "out", "selftest");
var mode = args.FirstOrDefault() ?? "test";

if (mode is not ("test" or "launch"))
    return Fail("usage: dotnet run tools/dev.cs [test|launch]");
if (Process.GetProcessesByName("SlayTheSpire2").Length > 0)
    return Fail("Close the game first.");

var build = Process.Start("dotnet", ["run", Path.Combine(root, "build.cs")]);
build.WaitForExit();
if (build.ExitCode != 0)
    return Fail("build failed");

PrepareSandbox(mode == "test");

var start = new ProcessStartInfo(Path.Combine(GameDir, "SlayTheSpire2.exe"), ["--force-steam", "off"]) { WorkingDirectory = GameDir };
if (mode == "launch")
{
    Process.Start(start);
    Console.WriteLine("launched in the dev sandbox");
    return 0;
}

if (Directory.Exists(outDir))
    Directory.Delete(outDir, recursive: true);
Directory.CreateDirectory(outDir);
start.Environment["SPIRE_MOD_MANAGER_SELFTEST"] = outDir;
// The game echoes its whole log to stdout; we read godot.log instead.
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
using (var log = new FileStream(Path.Combine(userData, "logs", "godot.log"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
using (var reader = new StreamReader(log))
    while (reader.ReadLine() is { } line)
        if (line.Contains("[SpireModManager]") || (line.Contains("[ERROR]") && line.Contains("Spire")))
            Console.WriteLine(line.Length > 400 ? line[..400] : line);
Console.WriteLine("screenshots: " + string.Join(", ", Directory.GetFiles(outDir, "*.png").Select(Path.GetFileName).Order()));
return !File.Exists(results) || text.Contains("FAIL") ? 1 : 0;

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
    if (!list.Any(m => (string?)m?["id"] == "BetterModMenu"))
        list.Add((JsonNode)new JsonObject { ["id"] = "BetterModMenu", ["is_enabled"] = false, ["source"] = "mods_directory" });
    mods["mod_list"] = list;
    settings["mod_settings"] = mods;
    settings["seen_ea_disclaimer"] = true; // the one-time Early Access popup would cover every screenshot
    File.WriteAllText(settingsPath, settings.ToJsonString(new() { WriteIndented = true }));

    if (!resetData)
        return;
    var data = Path.Combine(sandbox, "mod_data", "SpireModManager");
    if (Directory.Exists(data))
        Directory.Delete(data, recursive: true);
    Directory.CreateDirectory(data);
    File.Copy(Path.Combine(root, "tools", "fixtures", "manager.json"), Path.Combine(data, "manager.json"));
}

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}

static string ScriptDir([CallerFilePath] string path = "") => Path.GetDirectoryName(path) ?? ".";
