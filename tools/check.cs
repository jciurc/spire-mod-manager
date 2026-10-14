// Offline checks that need no game: CI runs them on every pull request, and they take seconds by hand.
//
//   dotnet run tools/check.cs
//
// - Localization: every JSON parses as flat "key": "text" pairs with no key twice, every key carries our
//   prefix (the game merges our keys into its own tables), and translations only use keys eng has
//   (build.cs refuses the others, but CI can't run build.cs).
// - Text keys: every SPIRE_MOD_MANAGER.* key the code uses is in localization/eng/settings_ui.json, the ones
//   it puts together at runtime too (see the rules below). A key in eng that no code uses is only a warning:
//   dead text, or a use this script can't see.
// - Game resources: no code loads one with ResourceLoader.Load or GD.Load; ModEntry.Load and Font do.
// - Fixtures: the JSON under tools/fixtures parses, and each test mod's manifest has id, name and version.
// - Tracked files: none over 1 MB, none holding what looks like a GitHub token.
// Prints PASS, FAIL and WARN lines (as annotations on GitHub); exit code 1 on any FAIL.
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

const string Prefix = "SPIRE_MOD_MANAGER.";
var root = Path.GetDirectoryName(ScriptDir()) ?? ".";
var onGitHub = Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true";
var (passed, failed, warned) = (0, 0, 0);

// Localization: localization/<lang>/<table>.json.
var tables = new Dictionary<string, HashSet<string>>(); // "eng/settings_ui.json" -> its keys
foreach (var file in Files("localization", "*.json"))
{
    var (json, error) = ReadJson(file);
    var flat = json is { ValueKind: JsonValueKind.Object } o && o.EnumerateObject().All(p => p.Value.ValueKind == JsonValueKind.String);
    Check($"{Name(file)} parses as \"key\": \"text\" pairs", flat, json is null ? error : "not a flat object of texts");
    if (!flat || json is not { } texts)
        continue;
    var keys = texts.EnumerateObject().Select(p => p.Name).ToHashSet();
    tables[Path.GetRelativePath(Path.Combine(root, "localization"), file).Replace('\\', '/')] = keys;
    Check($"{Name(file)}: every key starts with {Prefix}", keys.All(k => k.StartsWith(Prefix)), List(keys.Where(k => !k.StartsWith(Prefix))));
}
var eng = tables.GetValueOrDefault("eng/settings_ui.json");
foreach (var (name, keys) in tables.Where(t => !t.Key.StartsWith("eng/")))
{
    var engName = "eng/" + Path.GetFileName(name);
    var known = tables.GetValueOrDefault(engName);
    var unknown = keys.Except(known ?? []).Order().ToList();
    Check($"localization/{name}: only keys eng has", known is not null && unknown.Count == 0,
        known is null ? $"localization/{engName} didn't load" : List(unknown));
}

// Text keys in the code: whole literals, plus the keys the rules work out.
var sources = Files("src", "*.cs").ToDictionary(f => f, File.ReadAllText);
var literal = new Regex(@"""SPIRE_MOD_MANAGER\.([\w.]+)""");
var used = sources.Values.SelectMany(s => literal.Matches(s)).Select(m => Prefix + m.Groups[1].Value).ToHashSet();
var literals = used.Count;

// Keys the code puts together at runtime, so no literal shows them whole. Each rule names the code that
// builds them, which must still read that way (otherwise the rule is stale and fails), and works out the
// keys that code can make. A line that seems to build a key with no rule naming it fails too.
Rule[] rules =
[
    // Counts: Part(key, n) uses key + "_ONE" for one (the problems banner on the main list).
    new("LoadOrder.cs", ["string Part(string key, int count) => Menus.Format(count == 1 ? key + \"_ONE\" : key"],
        source => Calls(source, "Part", "_ONE")),
    // Load Order moves: Item(key, to) uses key + "_SHORT" on the compact inline buttons.
    new("LoadOrder.cs", ["MenuItem Item(string key, int to) => new(Menus.Text(key + (compact ? \"_SHORT\" : \"\"))"],
        source => Calls(source, "Item", "_SHORT")),
    // Category header moves: "SPIRE_MOD_MANAGER.X" + suffix, "_SHORT" on the compact inline buttons.
    new("CategoryList.cs", ["var suffix = compact ? \"_SHORT\" : \"\";"],
        source => Joined(source, "suffix", "_SHORT")),
    // Changes panel: which details of a mod were edited ("alias, notes").
    new("HistoryBar.cs", ["$\"SPIRE_MOD_MANAGER.FIELD_{f.ToUpperInvariant()}\"", "new[] { \"category\", \"alias\", \"notes\", \"tags\" }"],
        _ => ["FIELD_CATEGORY", "FIELD_ALIAS", "FIELD_NOTES", "FIELD_TAGS"]),
    // Alias, notes and tags fields: <KIND>_TERM, _HINT (the term's tooltip) and _ADD (the box's placeholder), and for
    // tags and notes (the bulk menu) also _BULK_HINT and _BULK_ADD.
    new("ModFields.cs", ["$\"SPIRE_MOD_MANAGER.{_kind.ToString().ToUpperInvariant()}_{part}\"", "enum Kind { Alias, Notes, Tags }",
            "LocKey(\"TERM\")", "LocKey((bulk ? \"BULK_\" : \"\") + \"HINT\")", "LocKey((_bulk ? \"BULK_\" : \"\") + \"ADD\")",
            "foreach (var kind in new[] { Kind.Tags, Kind.Notes })"],
        _ => ["ALIAS_TERM", "ALIAS_HINT", "ALIAS_ADD", "NOTES_TERM", "NOTES_HINT", "NOTES_ADD", "TAGS_TERM", "TAGS_HINT", "TAGS_ADD",
            "NOTES_BULK_HINT", "NOTES_BULK_ADD", "TAGS_BULK_HINT", "TAGS_BULK_ADD"]),
];
var stale = new List<string>();
foreach (var rule in rules)
{
    var source = sources.Where(s => Path.GetFileName(s.Key) == rule.File).Select(s => s.Value).FirstOrDefault() ?? "";
    stale.AddRange(rule.Code.Where(code => !source.Contains(code)).Select(code => $"{rule.File} no longer has {code}"));
    used.UnionWith(rule.Keys(source).Select(k => Prefix + k));
}
Check("every rule for keys built at runtime still matches the code", stale.Count == 0, List(stale));

// A line seems to build a key when, without its whole literal keys, it still has our prefix or a key-like
// piece such as "_ONE" or "BULK_". Comment lines don't count.
var piece = new Regex(@"""(_[A-Z][A-Z0-9_]*|[A-Z][A-Z0-9_]*_)""");
var unruled = new List<string>();
foreach (var (path, source) in sources)
{
    var named = rules.Where(r => r.File == Path.GetFileName(path)).SelectMany(r => r.Code).ToList();
    var lines = source.Split('\n');
    for (var i = 0; i < lines.Length; i++)
    {
        var rest = literal.Replace(lines[i], "");
        if (rest.TrimStart().StartsWith("//") || !(rest.Contains(Prefix) || piece.IsMatch(rest)) || named.Any(lines[i].Contains))
            continue;
        unruled.Add($"{Name(path)}:{i + 1}");
    }
}
Check("every key built at runtime has a rule in tools/check.cs", unruled.Count == 0, List(unruled));

var missing = used.Where(k => eng?.Contains(k) != true).Order().ToList();
Check($"every key the code uses is in eng ({literals} literal, {used.Count - literals} more built at runtime)", eng is not null && missing.Count == 0,
    eng is null ? "localization/eng/settings_ui.json didn't load" : List(missing));
foreach (var key in (eng ?? []).Where(k => !used.Contains(k)).Order())
    Warn($"{key} is in eng, but no code uses it");

// Game resources: only ModEntry.Load (and Font, through it) loads them, so one a game update moves gives a
// stand-in and the self-test's list of missing ones, not a crash. A direct load works until then, so no in-game
// check can see it. Comment lines don't count.
var directLoad = new Regex(@"\b(ResourceLoader\.Load\w*|GD\.Load)\b");
var direct = new List<string>();
foreach (var (path, source) in sources)
{
    var lines = source.Split('\n');
    for (var i = 0; i < lines.Length; i++)
        if (directLoad.IsMatch(lines[i]) && !lines[i].TrimStart().StartsWith("//")
            && !(Path.GetFileName(path) == "ModEntry.cs" && lines[i].Contains("ResourceLoader.Load(path) is T resource"))) // the helper itself
            direct.Add($"{Name(path)}:{i + 1}");
}
Check("game resources load through ModEntry.Load or ModEntry.Font", direct.Count == 0, List(direct));

// Fixtures: the test mods and data tools/dev.cs puts in the sandbox.
var fixtures = Path.Combine(root, "tools", "fixtures");
foreach (var file in Files(Path.Combine("tools", "fixtures"), "*.json"))
{
    var (json, error) = ReadJson(file);
    Check($"{Name(file)} parses", json is not null, error);
    if (json is not { } manifest || !Path.GetRelativePath(fixtures, file).StartsWith("mods"))
        continue;
    var absent = new[] { "id", "name", "version" }.Where(field => !HasText(manifest, field)).ToList();
    Check($"{Name(file)}: manifest has id, name and version", absent.Count == 0, "missing " + List(absent));
}

// Tracked files: nothing big (game files and builds never go in) and no tokens. Only the file and line of
// a token are printed, never the token.
using var git = Process.Start(new ProcessStartInfo("git", ["-C", root, "ls-files", "-z"])
    { RedirectStandardOutput = true, StandardOutputEncoding = Encoding.UTF8 });
var tracked = git?.StandardOutput.ReadToEnd().Split('\0', StringSplitOptions.RemoveEmptyEntries) ?? [];
git?.WaitForExit();
Check($"tracked files listed ({tracked.Length})", git is { ExitCode: 0 } && tracked.Length > 0, "git ls-files failed");
var token = new Regex(@"\b(gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,})");
var (big, tokens) = (new List<string>(), new List<string>());
foreach (var file in tracked)
{
    var path = Path.Combine(root, file);
    if (!File.Exists(path))
        continue; // deleted here but not yet committed, or a submodule
    var bytes = File.ReadAllBytes(path);
    if (bytes.Length > 1024 * 1024)
        big.Add($"{file} ({bytes.Length / (1024.0 * 1024):0.00} MB)");
    var text = Encoding.Latin1.GetString(bytes); // one char per byte, so binary files scan too
    tokens.AddRange(token.Matches(text).Select(m => $"{file}:{text.AsSpan(0, m.Index).Count('\n') + 1}"));
}
Check("no tracked file over 1 MB", big.Count == 0, List(big));
Check("no tracked file holds a GitHub token", tokens.Count == 0, List(tokens));

Console.WriteLine($"{passed} passed, {failed} failed, {warned} warning(s)");
return failed > 0 ? 1 : 0;

void Check(string name, bool ok, string detail = "")
{
    if (ok)
        passed++;
    else
        failed++;
    var line = $"{(ok ? "PASS" : "FAIL")} {name}{(ok || detail.Length == 0 ? "" : $": {detail}")}";
    Console.WriteLine(!ok && onGitHub ? "::error::" + Escape(line) : line);
}

void Warn(string text)
{
    warned++;
    Console.WriteLine(onGitHub ? "::warning::" + Escape(text) : "WARN " + text);
}

// Files under a folder of the repo, in a fixed order; build output (bin, obj) left out.
IEnumerable<string> Files(string dir, string pattern)
{
    var full = Path.Combine(root, dir);
    return Directory.Exists(full)
        ? Directory.EnumerateFiles(full, pattern, SearchOption.AllDirectories)
            .Where(f => !Path.GetRelativePath(full, f).Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"))
            .Order(StringComparer.Ordinal)
        : [];
}

string Name(string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

static string List(IEnumerable<string> items) => string.Join(", ", items);

// GitHub's workflow commands end at a line break and read % as an escape.
static string Escape(string text) => text.Replace("%", "%25").Replace("\r", "%0D").Replace("\n", "%0A");

// The file's JSON, or why it doesn't parse. A key given twice counts as an error: only one of the two
// values could be used.
static (JsonElement? Json, string Error) ReadJson(string path)
{
    try
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { AllowDuplicateProperties = false });
        return (doc.RootElement.Clone(), "");
    }
    catch (JsonException e)
    {
        return (null, e.Message);
    }
}

// A field the game reads from a manifest: there, and a non-empty string.
static bool HasText(JsonElement json, string field) =>
    json.ValueKind == JsonValueKind.Object && json.TryGetProperty(field, out var value)
    && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 };

// Literal keys passed to helper("SPIRE_MOD_MANAGER.X", ...), each with the suffix the helper adds.
static IEnumerable<string> Calls(string source, string helper, string suffix) =>
    Regex.Matches(source, $@"\b{helper}\(""SPIRE_MOD_MANAGER\.([\w.]+)""").Select(m => m.Groups[1].Value + suffix);

// Literal keys joined to a suffix variable ("SPIRE_MOD_MANAGER.X" + variable), each with that suffix.
static IEnumerable<string> Joined(string source, string variable, string suffix) =>
    Regex.Matches(source, $@"""SPIRE_MOD_MANAGER\.([\w.]+)"" \+ {variable}\b").Select(m => m.Groups[1].Value + suffix);

static string ScriptDir([CallerFilePath] string path = "") => Path.GetDirectoryName(path) ?? ".";

// Code: snippets of the source file that build the keys (all must still be there); Keys: the keys that
// code can make, without the prefix, worked out from the file's source.
record Rule(string File, string[] Code, Func<string, IEnumerable<string>> Keys);
