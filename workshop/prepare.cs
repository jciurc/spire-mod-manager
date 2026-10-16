// Get the Workshop folder ready for an upload with Mega Crit's ModUploader, and check it. Nothing is uploaded.
// Usage: dotnet run workshop/prepare.cs     (after dotnet run build.cs --dist; see workshop/README.md)
// Fills content/ from dist/SpireModManager/, copies description.bbcode into workshop.json (the uploader reads the
// description only from there), then checks and lists everything the upload will set.
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

const string ModId = "SpireModManager";
const int MaxDescription = 8000; // Steam's limit for a Workshop description
const long MaxPicture = 1_000_000; // Steam's 1 MB limit for image.png and each preview, taken low to be safe

var workshop = ScriptDir();
var root = Path.GetDirectoryName(workshop) ?? ".";
var problems = new List<string>();

// The description: from its own file into workshop.json, line endings as Steam expects them.
var configPath = Path.Combine(workshop, "workshop.json");
if (JsonNode.Parse(File.ReadAllText(configPath)) is not JsonObject config)
    return Fail("workshop.json: not a JSON object");
var description = File.ReadAllText(Path.Combine(workshop, "description.bbcode")).Replace("\r\n", "\n").Trim();
var descriptionBytes = Encoding.UTF8.GetByteCount(description);
if (descriptionBytes >= MaxDescription)
    problems.Add($"description.bbcode: {descriptionBytes:N0} bytes; Steam takes under {MaxDescription:N0}");
if (Text(config, "description") != description)
{
    config["description"] = description;
    var json = new JsonSerializerOptions { WriteIndented = true, NewLine = "\n", Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    File.WriteAllText(configPath, config.ToJsonString(json) + "\n");
    Console.WriteLine("workshop.json: description updated from description.bbcode");
}

// content/: exactly what dist/ holds, or nothing (the uploader then refuses), never a stale copy.
var dist = Path.Combine(root, "dist", ModId);
var content = Path.Combine(workshop, "content");
string[] expected = [$"{ModId}.dll", $"{ModId}.json", $"{ModId}.pck"]; // in ordinal order
if (Directory.Exists(content))
    Directory.Delete(content, recursive: true);
var found = Directory.Exists(dist)
    ? Directory.GetFileSystemEntries(dist).Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal).ToList()
    : null;
if (found == null)
    problems.Add($"dist/{ModId}/ is missing: run dotnet run build.cs --dist first");
else if (!found.SequenceEqual(expected))
    problems.Add($"dist/{ModId}/ should hold only {string.Join(", ", expected)}, not {string.Join(", ", found)}");
else
{
    Directory.CreateDirectory(content);
    foreach (var name in expected)
        File.Copy(Path.Combine(dist, name), Path.Combine(content, name));
}
var manifestPath = Path.Combine(content, $"{ModId}.json");
var version = File.Exists(manifestPath) && JsonNode.Parse(File.ReadAllText(manifestPath)) is JsonObject manifest ? Text(manifest, "version") : null;

// The fields the uploader sets on every upload (a missing one is left as it is, except the branch range and
// dependencies, which are always set: missing means every branch and no dependencies).
var title = Text(config, "title");
var visibility = Text(config, "visibility");
var changeNote = Text(config, "changeNote");
var minBranch = Text(config, "minBranch");
var maxBranch = Text(config, "maxBranch");
if (string.IsNullOrWhiteSpace(title))
    problems.Add("workshop.json: title is empty");
if (visibility is not ("private" or "public" or "unlisted" or "friends_only"))
    problems.Add("workshop.json: visibility should be private, public, unlisted or friends_only");
// minBranch: empty is every branch, right while the build runs on stable and the beta; one that needs the beta
// sets "public-beta" (workshop/README.md). Either way the range is listed below, to read before the upload.
if (config["dependencies"] is not JsonArray)
    problems.Add("workshop.json: dependencies should be written out, as [] when there are none");
if (config["tags"] is not JsonArray)
    problems.Add("workshop.json: tags should be a list");
if (string.IsNullOrWhiteSpace(changeNote))
    problems.Add("workshop.json: changeNote is empty");
else if (version != null && !changeNote.StartsWith($"{version}:"))
    problems.Add($"workshop.json: changeNote should start with \"{version}:\", the version in content/");

// mod_id.txt: written by the uploader after the first upload; without it a new item is made, and that one
// goes up private (docs/dev/DESIGN.md, "Releases": test it privately first, then make it public).
var modIdPath = Path.Combine(workshop, "mod_id.txt");
ulong? itemId = File.Exists(modIdPath) && ulong.TryParse(File.ReadAllText(modIdPath).Trim(), out var id) ? id : null;
if (File.Exists(modIdPath) && itemId == null)
    problems.Add("mod_id.txt: not an item id");
if (!File.Exists(modIdPath) && visibility != "private")
    problems.Add("workshop.json: the first upload should be private");

// Pictures. image.png: square, 512 to 1024 px. previews/: if the folder is there, the item ends up with exactly
// its files (by name); if it isn't, the item's previews stay as they are.
var image = Path.Combine(workshop, "image.png");
if (!File.Exists(image))
    problems.Add("image.png is missing (the uploader needs it)");
else if (PngSize(image) is not { } size)
    problems.Add("image.png: not a PNG file");
else
{
    if (size.Width != size.Height || size.Width is < 512 or > 1024)
        problems.Add($"image.png: {size.Width}x{size.Height}; it should be square, 512 to 1024 px");
    if (new FileInfo(image).Length >= MaxPicture)
        problems.Add($"image.png: {new FileInfo(image).Length:N0} bytes; Steam takes under 1 MB");
}
var previewsDir = Path.Combine(workshop, "previews");
var previews = Directory.Exists(previewsDir) ? Directory.GetFiles(previewsDir).Order().ToList() : null;
if (previews is [])
    problems.Add("previews/ is empty: the upload would remove every preview; delete the folder to keep them as they are");
foreach (var preview in previews ?? [])
{
    var name = Path.GetFileName(preview);
    if (Path.GetExtension(preview).ToLowerInvariant() is not (".jpg" or ".jpeg" or ".png" or ".gif"))
        problems.Add($"previews/{name}: not a JPG, PNG or GIF");
    if (new FileInfo(preview).Length >= MaxPicture)
        problems.Add($"previews/{name}: {new FileInfo(preview).Length:N0} bytes; Steam takes under 1 MB (dotnet run workshop/shrink.cs)");
}

Console.WriteLine();
Console.WriteLine("This upload sets:");
Console.WriteLine($"  item:          {(itemId is { } known ? $"{known} (from mod_id.txt)" : "a new one")}");
Console.WriteLine($"  title:         {title}");
Console.WriteLine($"  visibility:    {visibility}");
Console.WriteLine($"  branches:      {Branches(minBranch, maxBranch)}");
Console.WriteLine($"  tags:          {(config["tags"] is JsonArray list ? string.Join(", ", list) : "")}");
Console.WriteLine($"  dependencies:  {(config["dependencies"] is JsonArray { Count: > 0 } deps ? string.Join(", ", deps) : "none")}");
Console.WriteLine($"  change note:   {changeNote}");
Console.WriteLine($"  description:   {descriptionBytes:N0} of {MaxDescription:N0} bytes");
Console.WriteLine($"  content:       {(version != null ? $"{string.Join(", ", expected)} (version {version})" : "none")}");
Console.WriteLine($"  previews:      {previews switch
{
    null => "unchanged (no previews folder)",
    [] => "none (every preview removed)",
    _ => string.Join(", ", previews.Select(Path.GetFileName)),
}}");
Console.WriteLine();

if (problems.Count > 0)
{
    foreach (var problem in problems)
        Console.Error.WriteLine($"problem: {problem}");
    return 1;
}
Console.WriteLine("Ready. In the uploader's folder (Steam running):");
Console.WriteLine($"  .\\ModUploader.exe upload -w \"{workshop}\"");
return 0;

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}

static string ScriptDir([CallerFilePath] string path = "") => Path.GetDirectoryName(path) ?? ".";

static string? Text(JsonObject json, string key) => json[key] is JsonValue value && value.TryGetValue(out string? text) ? text : null;

// The branch range as the uploader will set it (an empty end is open).
static string Branches(string? min, string? max) => (string.IsNullOrWhiteSpace(min), string.IsNullOrWhiteSpace(max)) switch
{
    (true, true) => "every branch",
    (false, true) => $"{min} and later",
    (true, false) => $"up to {max}",
    _ => $"{min} to {max}",
};

// Width and height from a PNG's header (the IHDR chunk comes first, big-endian), or null if it isn't one.
static (int Width, int Height)? PngSize(string path)
{
    var head = new byte[24];
    using (var file = File.OpenRead(path))
        if (file.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) < head.Length)
            return null;
    if (!head.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A }))
        return null;
    return (BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(16)), BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(20)));
}
