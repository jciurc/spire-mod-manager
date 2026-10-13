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

const string ModId = "SpireModManager";
var Steam = Env("STS2_STEAMAPPS", @"D:\SteamLibrary\steamapps"); // the Steam library with the game
var root = ScriptDir();
var installDir = Path.Combine(Steam, "common", "Slay the Spire 2", "mods", ModId);
var project = Path.Combine(root, "src", ModId, ModId + ".csproj");
var outDir = Path.Combine(root, "out");
var dist = args.Contains("--dist");

// The version a --dist build carries, the one on the Workshop. Raised by hand for each upload; it never
// changes on its own, and dev builds keep their own number below.
const string ReleaseVersion = "0.1.0";

var manifest = new JsonObject
{
    ["id"] = ModId,
    ["pck_name"] = ModId,
    ["name"] = "Spire Mod Manager",
    ["author"] = "symphony",
    ["description"] = "Organize mods into categories, save profiles, set the load order and add notes and tags. " +
        "Relaunch the game in place to apply changes.",
    ["version"] = "0.0.45", // pre-release: the last number goes up by one with each install to Jordan's game
    ["has_pck"] = true,
    ["has_dll"] = true,
    ["affects_gameplay"] = false,
    ["dependencies"] = new JsonArray(),
    ["min_game_version"] = "0.111.0",
};
if (dist)
    manifest["version"] = ReleaseVersion;

// What a player's mods folder gets: the pack, the manifest and the DLL.
string[] shipped = [".pck", ".json", ".dll"];

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
// Images: images/*.png -> res://SpireModManager/images/... as plain files (not imported); the mod loads
// them with Image.LoadPngFromBuffer.
foreach (var file in Directory.GetFiles(Path.Combine(root, "images"), "*.png"))
    files[$"{ModId}/images/{Path.GetFileName(file)}"] = File.ReadAllBytes(file);

WritePack(Path.Combine(outDir, ModId + ".pck"), files);
File.WriteAllText(Path.Combine(outDir, ModId + ".json"), manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

var build = Process.Start("dotnet", ["build", project, "-c", "Release", "-nologo", "-v", "q",
    $"-p:SteamLibrary={Steam}", $"-o:{Path.Combine(outDir, "bin")}"]);
build.WaitForExit();
if (build.ExitCode != 0)
    return Fail("dotnet build failed");
File.Copy(Path.Combine(outDir, "bin", ModId + ".dll"), Path.Combine(outDir, ModId + ".dll"));
Console.WriteLine($"built {files.Count} pack file(s)");

if (dist)
{
    // The upload is filled from dist/SpireModManager alone: out/ also holds bin/ (pdb, deps.json) and, between
    // builds, the dev log and self-test output, some of it from Jordan's own mods and profiles. Only this
    // folder is emptied, so anything else kept in dist/ (zips of earlier releases) stays.
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

static string ScriptDir([CallerFilePath] string path = "") => Path.GetDirectoryName(path) ?? ".";

// An environment variable's value (README.md, "Building"), or the author's own path when it isn't set.
static string Env(string name, string fallback) =>
    Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : fallback;

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
