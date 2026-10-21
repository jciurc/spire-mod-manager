// Build SpireModManager (pack + DLL + manifest) and install it into the game's mods folder.
// Usage: dotnet run build.cs [--no-install]     (needs the .NET 10 SDK; close the game first to install)
//        dotnet run build.cs --dist             the release: ReleaseVersion, only the files players get, in
//                                               dist/SpireModManager/ for the Workshop upload; never installs
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32;

const string ModId = "SpireModManager";
var Steam = SteamApps(); // the Steam library with the game
if (!Directory.Exists(Path.Combine(Steam, "common", "Slay the Spire 2")))
    return Fail($"Slay the Spire 2 isn't in {Steam}. Set the environment variable "
        + @"STS2_STEAMAPPS to the steamapps folder of the library that has it, for example D:\SteamLibrary\steamapps (CONTRIBUTING.md, ""What you need"").");
var root = ScriptDir();
var installDir = Path.Combine(Steam, "common", "Slay the Spire 2", "mods", ModId);
var project = Path.Combine(root, "src", ModId, ModId + ".csproj");
var outDir = Path.Combine(root, "out");
var dist = args.Contains("--dist");

// The version a --dist build carries, the one on the Workshop. Raised by hand for each upload; it never
// changes on its own, and dev builds keep their own number below.
const string ReleaseVersion = "0.1.4";

var manifest = new JsonObject
{
    ["id"] = ModId,
    ["pck_name"] = ModId,
    ["name"] = "Spire Mod Manager",
    ["author"] = "symphony",
    ["description"] = "Organize mods into categories, save profiles, set the load order and add notes and tags. " +
        "Relaunch the game in place to apply changes.",
    // Dev builds: above the Workshop's ReleaseVersion, or the game loads the Workshop copy instead (RELEASING.md, step 9).
    // The last number goes up by one with each install to the game; after the next release, the version after it.
    ["version"] = "0.1.5-dev.0",
    ["has_pck"] = true,
    ["has_dll"] = true,
    ["affects_gameplay"] = false,
    ["dependencies"] = new JsonArray(),
    ["min_game_version"] = "0.107.1",
};
if (dist)
    manifest["version"] = ReleaseVersion;

// What a player's mods folder gets: the pack, the manifest and the DLL.
string[] shipped = [".pck", ".json", ".dll"];

// A dev run from this checkout uses out/ (its build, its results, its screenshots): clearing it mid-run loses the run.
// The shared lock's holder file names the checkout a run is from (tools/dev.cs); that run's own build goes ahead.
if (RunUsingOut(root) is { } running)
    return Fail($"a dev run from this checkout is using out/ ({running}): wait for it to finish, or build in another worktree");
if (Directory.Exists(outDir))
    Directory.Delete(outDir, recursive: true);
Directory.CreateDirectory(outDir);

// Localization: localization/<lang>/<table>.json -> res://SpireModManager/localization/...
// Translations only need the keys they translate (missing keys fall back to eng), but every key
// must exist in eng, so a typo fails the build instead of showing nothing.
var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
var locRoot = Path.Combine(root, "localization");
var eng = Directory.GetFiles(Path.Combine(locRoot, "eng"), "*.json")
    .ToDictionary(f => Path.GetFileName(f), f => Keys(File.ReadAllBytes(f))); // a lambda: the method group's key would be string?
foreach (var langDir in Directory.GetDirectories(locRoot))
    foreach (var file in Directory.GetFiles(langDir, "*.json"))
    {
        var (lang, name) = (Path.GetFileName(langDir), Path.GetFileName(file));
        var blob = File.ReadAllBytes(file);
        var unknown = Keys(blob).Except(eng.GetValueOrDefault(name) ?? []).Order().ToList();
        if (unknown.Count > 0)
            return Fail($"localization/{lang}/{name}: keys not in eng: {string.Join(", ", unknown)}");
        files[$"{ModId}/localization/{lang}/{name}"] = blob;
    }
// Scenes: scenes/*.tscn -> res://SpireModManager/scenes/...
foreach (var file in Directory.GetFiles(Path.Combine(root, "scenes")))
    files[$"{ModId}/scenes/{Path.GetFileName(file)}"] = File.ReadAllBytes(file);
// Images: images/*.png -> res://SpireModManager/images/... as plain files, for Image.LoadPngFromBuffer. None today.
var images = Path.Combine(root, "images");
foreach (var file in Directory.Exists(images) ? Directory.GetFiles(images, "*.png") : [])
    files[$"{ModId}/images/{Path.GetFileName(file)}"] = File.ReadAllBytes(file);

WritePack(Path.Combine(outDir, ModId + ".pck"), files);
File.WriteAllText(Path.Combine(outDir, ModId + ".json"), manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

// SpireModManagerWorkshop leaves the dev-only code out of the Workshop build: the in-game self-test, the
// dev-run switches and the debug information (SpireModManager.csproj).
var build = Process.Start("dotnet", ["build", project, "-c", "Release", "-nologo", "-v", "q",
    $"-p:SteamLibrary={Steam}", $"-p:SpireModManagerWorkshop={(dist ? "true" : "false")}", $"-o:{Path.Combine(outDir, "bin")}"]);
build.WaitForExit();
if (build.ExitCode != 0)
    return Fail("dotnet build failed");
File.Copy(Path.Combine(outDir, "bin", ModId + ".dll"), Path.Combine(outDir, ModId + ".dll"));
Console.WriteLine($"built {files.Count} pack file(s)");

if (dist)
{
    // The upload comes from dist/SpireModManager alone (out/ also holds bin/ and dev output, some from your own mods).
    // Only this folder is emptied; anything else in dist/ (zips of earlier releases) stays.
    var distDir = Path.Combine(root, "dist", ModId);
    if (Directory.Exists(distDir))
        Directory.Delete(distDir, recursive: true);
    Directory.CreateDirectory(distDir);
    foreach (var ext in shipped)
        File.Copy(Path.Combine(outDir, ModId + ext), Path.Combine(distDir, ModId + ext));
    // A last look before anything is uploaded: exactly the shipped files, nothing missing, nothing extra.
    var expected = shipped.Select(ext => ModId + ext).Order().ToList();
    var found = Directory.GetFileSystemEntries(distDir).Select(Path.GetFileName).Order().ToList();
    if (!found.SequenceEqual(expected))
        return Fail($"dist/{ModId} must hold exactly {string.Join(", ", expected)}, but holds: {string.Join(", ", found)}");
    Console.WriteLine($"release {ReleaseVersion} in {distDir}: {string.Join(", ", found)}");
    return 0;
}

if (!args.Contains("--no-install"))
{
    Directory.CreateDirectory(installDir);
    try
    {
        foreach (var ext in shipped)
            File.Copy(Path.Combine(outDir, ModId + ext), Path.Combine(installDir, ModId + ext), overwrite: true);
    }
    catch (IOException e)
    {
        return Fail($"install failed (is the game running?): {e.Message}");
    }
    Console.WriteLine($"installed to {installDir}");
}
return 0;

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}

// The dev run holding the shared lock, if it's from this checkout and still going (and isn't the one building now).
static string? RunUsingOut(string root)
{
    try
    {
        var file = Path.Combine(Path.GetTempPath(), "sts2-dev-run.json");
        if (!File.Exists(file) || JsonNode.Parse(File.ReadAllText(file)) is not JsonObject held || held["dir"]?.GetValue<string>() is not { Length: > 0 } dir)
            return null;
        static string Full(string p) => Path.GetFullPath(p).TrimEnd('\\', '/');
        if (!string.Equals(Full(dir), Full(root), StringComparison.OrdinalIgnoreCase) || held["pid"]?.GetValue<int>() is not { } pid
            || Environment.GetEnvironmentVariable("SPIRE_MOD_MANAGER_DEV_RUN") == pid.ToString())
            return null;
        using var process = Process.GetProcessById(pid);
        return process.HasExited ? null : $"{held["mode"]}, process {pid}, since {held["started"]}";
    }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.Text.Json.JsonException)
    {
        return null; // no holder, a stale one, or its process gone: nothing to guard
    }
}

static string ScriptDir([CallerFilePath] string path = "") => Path.GetDirectoryName(path) ?? ".";

// The Steam library with the game: STS2_STEAMAPPS, else the library in Steam's libraryfolders.vdf that has it, else
// Steam's default folder (the same in tools/dev.cs).
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

// Top-level keys of a flat JSON object; throws on malformed JSON.
static HashSet<string> Keys(byte[] json) =>
    JsonNode.Parse(json)?.AsObject().Select(p => p.Key).ToHashSet() ?? [];

// Godot 4.5 resource pack, format v3 with a relative file base: 112-byte header (40 bytes + reserved),
// file data padded to 16, then the directory: count, and per file a 4-aligned path (no res://),
// offset, size, MD5 and flags.
static void WritePack(string path, SortedDictionary<string, byte[]> files)
{
    const int headerSize = 112;
    using var data = new MemoryStream();
    using var directory = new MemoryStream();
    var dir = new BinaryWriter(directory);
    dir.Write(files.Count);
    foreach (var (name, blob) in files)
    {
        var offset = data.Position;
        data.Write(blob);
        data.Write(new byte[(16 - data.Length % 16) % 16]);
        var raw = Encoding.UTF8.GetBytes(name);
        var padded = raw.Length + (4 - raw.Length % 4) % 4;
        dir.Write(padded);
        dir.Write(raw);
        dir.Write(new byte[padded - raw.Length]);
        dir.Write(offset);
        dir.Write((long)blob.Length);
        dir.Write(MD5.HashData(blob));
        dir.Write(0);
    }

    using var file = File.Create(path);
    var w = new BinaryWriter(file);
    w.Write("GDPC"u8);
    foreach (var v in new[] { 3, 4, 5, 1, 2 }) // format version, Godot 4.5.1, flags: relative file base
        w.Write(v);
    w.Write((long)headerSize);                 // file base
    w.Write((long)(headerSize + data.Length)); // directory offset
    w.Write(new byte[headerSize - file.Position]);
    data.WriteTo(file);
    directory.WriteTo(file);
}
