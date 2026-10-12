// Build SpireModManager (pack + DLL + manifest) and install it into the game's mods folder.
// Usage: dotnet run build.cs [--no-install]     (needs the .NET 10 SDK; close the game first to install)
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

const string ModId = "SpireModManager";
const string Steam = @"D:\SteamLibrary\steamapps";
var root = ScriptDir();
var installDir = Path.Combine(Steam, "common", "Slay the Spire 2", "mods", ModId);
var project = Path.Combine(root, "src", ModId, ModId + ".csproj");
var outDir = Path.Combine(root, "out");

var manifest = new JsonObject
{
    ["id"] = ModId,
    ["pck_name"] = ModId,
    ["name"] = "Spire Mod Manager",
    ["author"] = "symphony",
    ["description"] = "Mod profiles, sorting and an in-place Restart for the Modding screen.",
    ["version"] = "0.0.45", // pre-release: the last number goes up by one with each install to Jordan's game
    ["has_pck"] = true,
    ["has_dll"] = true,
    ["affects_gameplay"] = false,
    ["dependencies"] = new JsonArray(),
    ["min_game_version"] = "0.111.0",
};

if (Directory.Exists(outDir))
    Directory.Delete(outDir, recursive: true);
Directory.CreateDirectory(outDir);

// Localization: localization/<lang>/<table>.json -> res://SpireModManager/localization/...
// Translations only need the keys they translate (missing keys fall back to eng), but every key
// must exist in eng, so a typo fails the build instead of showing nothing.
var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
var locRoot = Path.Combine(root, "localization");
var eng = Directory.GetFiles(Path.Combine(locRoot, "eng"), "*.json")
    .ToDictionary(Path.GetFileName, f => Keys(File.ReadAllBytes(f)));
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

if (!args.Contains("--no-install"))
{
    Directory.CreateDirectory(installDir);
    try
    {
        foreach (var ext in new[] { ".pck", ".json", ".dll" })
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
