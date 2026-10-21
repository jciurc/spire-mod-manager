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
// - Descriptions: no code sets the engine's TooltipText; Hover.Tip shows our card.
// - Fixtures: the JSON under tools/fixtures parses, and each test mod's manifest has id, name and version.
// - Translation tooling: tools/loc.cs's SmartFormat package and the self-test's ToolParser are the same version.
// - Both game branches: the members only the public-beta has go through Compat.
// - Null-forgiving: no `!` that tells the compiler a value isn't null, in src, tools, workshop or build.cs.
// - The API (src/SpireModManager/Api, docs/dev/API.md): it's the only public code; its files keep the shapes that can
//   grow without breaking a mod built against them; the shipped surface only grows; ApiInfo.Version follows the
//   snapshot and VERSIONS.md; the assembly version stays 1.0.0.0; every member is named in the API self-test.
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
    // tags and notes (the bulk menu) also _BULK_ADD (its _BULK_HINTs are named whole, in ModField.BulkInfo).
    new("ModFields.cs", ["$\"SPIRE_MOD_MANAGER.{_kind.ToString().ToUpperInvariant()}_{part}\"", "enum Kind { Alias, Notes, Tags }",
            "LocKey(\"TERM\")", "LocKey(\"HINT\")", "LocKey((_bulk ? \"BULK_\" : \"\") + \"ADD\")",
            "foreach (var kind in new[] { Kind.Tags, Kind.Notes })"],
        _ => ["ALIAS_TERM", "ALIAS_HINT", "ALIAS_ADD", "NOTES_TERM", "NOTES_HINT", "NOTES_ADD", "TAGS_TERM", "TAGS_HINT", "TAGS_ADD",
            "NOTES_BULK_ADD", "TAGS_BULK_ADD"]),
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

// Descriptions: our hover card (Hover.Tip), never the engine's tooltip, which looks unlike ours. The self-test only
// reads it, so it doesn't count; nor do comment lines.
var native = new List<string>();
foreach (var (path, source) in sources.Where(s => Path.GetFileName(s.Key) != "SelfTest.cs"))
{
    var lines = source.Split('\n');
    for (var i = 0; i < lines.Length; i++)
        if (lines[i].Contains("TooltipText") && !lines[i].TrimStart().StartsWith("//"))
            native.Add($"{Name(path)}:{i + 1}");
}
Check("descriptions use Hover.Tip, not the engine's TooltipText", native.Count == 0, List(native));

// Both game branches: the members only the public-beta has (Mod.workshopId, NModInfoContainer.Clear) go through
// Compat, by name. Builds compile against the installed branch, so a direct use builds on the beta and then fails on
// stable, where the method using it throws as it's first called. Comment lines don't count. Also the beta's == operator
// on ModManifest: `manifest == null` compiles to it, and on stable the method using it can't run at all (a
// MissingMethodException as it's first called: the info panel lost our fields); `is null` and `?.` need no operator.
var betaOnly = new Regex(@"\.workshopId\b|\bworkshopId\s*=|\.assemblies\b|NModInfoContainer\.Clear\b|ModInfoContainer""\)\.Clear\(|\binfo\.Clear\(\)|\bmanifest\s*[!=]=");
var branchy = new List<string>();
foreach (var (path, source) in sources.Where(s => Path.GetFileName(s.Key) != "Compat.cs"))
{
    var lines = source.Split('\n');
    for (var i = 0; i < lines.Length; i++)
        if (betaOnly.IsMatch(lines[i]) && !lines[i].TrimStart().StartsWith("//"))
            branchy.Add($"{Name(path)}:{i + 1}");
}
Check("members only the beta has go through Compat (stable has no Mod.workshopId, Mod.assemblies, NModInfoContainer.Clear or ModManifest ==)", branchy.Count == 0, List(branchy));

// Null-forgiving: Jordan's code style has no `!` after a value to say it isn't null (null!, x!.y, F()!). A null there
// throws far from its cause; a check that narrows (a pattern, or a checked local) gives the code its own way out.
// Comments and the text of strings don't count; an interpolated string's holes do, as they're code. The scan runs on a
// sample first, so a scanner that blanks too much (and would pass everything) fails here.
var forgiving = new Regex(@"(?<=[\w)\]])(?<!\boperator)!(?!=)");
List<int> Forgiving(string source) =>
    CodeOnly(source).Split('\n').Select((line, i) => (line, i)).Where(l => forgiving.IsMatch(l.line)).Select(l => l.i + 1).ToList();
var sample = Forgiving(""""
    var a = $"{x!.Y}";
    var b = "Hi! there" + 'x' + '!' + '\'';
    var c = @"a ""b"" c! d
    still text!";
    var d = $$"""{ "a!": {{x!}} }""";
    /* x! */ var e = x != y && !z; // x!
    var f = null!;
    var g = F()! + a[0]!.Z;
    var h = $@"{p!}" + """raw! "quoted!" text""" + $"{{not a hole! }}";
    var i = $"{(q ? "a!" : r!)}" + "" + s!;
    public static bool operator!(T t) => false;
    """");
Check("the null-forgiving scan finds the sample's (lines 1 5 7 8 9 10)", sample.SequenceEqual([1, 5, 7, 8, 9, 10]), "lines " + string.Join(" ", sample));
var scripts = Files("tools", "*.cs").Concat(Files("workshop", "*.cs")).Append(Path.Combine(root, "build.cs")).Where(File.Exists);
var asserted = new List<string>();
foreach (var (path, source) in sources.Concat(scripts.ToDictionary(f => f, File.ReadAllText)))
    asserted.AddRange(Forgiving(source).Select(line => $"{Name(path)}:{line}"));
Check("no null-forgiving ! (narrow instead: `is { } x`, `is not { } x` then return or continue, or a checked local)", asserted.Count == 0, List(asserted));

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

// The API. Other mods compile against SpireModManager.dll's public surface and bind to it at run time, so it may only
// grow (docs/dev/API.md). Build time already holds the line exactly (PublicApiAnalyzers: every public signature recorded
// in src/SpireModManager/PublicAPI.*.txt); these are the rules it doesn't cover, and its safety net in CI.
var apiDir = Path.Combine(root, "src", "SpireModManager", "Api");
bool InApi(string path) => Path.GetFullPath(path).StartsWith(Path.GetFullPath(apiDir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
// 1. Only the API is public: no top-level public type anywhere else (files use file-scoped namespaces, so one starts at
//    column 0).
var outside = sources.Where(s => !InApi(s.Key)).SelectMany(s => s.Value.Split('\n').Select((line, i) => (s.Key, line, i)))
    .Where(l => l.line.StartsWith("public ")).Select(l => $"{Name(l.Key)}:{l.i + 1}").ToList();
Check("only the API is public: no top-level public type outside src/SpireModManager/Api", outside.Count == 0, List(outside));
// 2. Its files keep the shapes that can grow without breaking a caller: no record, struct, interface, event, const
//    (copied into the caller), required, protected, virtual or abstract; no #if; no game types (using MegaCrit); classes
//    sealed or static; no public instance field; no parameter defaults (copied into the caller too: overloads instead).
var shapes = new List<string>();
var banned = new Regex(@"\b(record|struct|interface|event|const|required|protected|virtual|abstract)\b");
var openClass = new Regex(@"\bpublic\s+(?!static\b|sealed\b)(partial\s+)?class\b");
var instanceField = new Regex(@"^\s*public\s+(?!static\b)(readonly\s+)?[\w.<>?,\[\]]+\s+\w+\s*(=(?!>)[^;]*)?;\s*$");
var defaulted = new Regex(@"\bpublic\b[^(]*\(([^)]*)\)");
foreach (var (path, source) in sources.Where(s => InApi(s.Key)))
{
    var code = CodeOnly(source).Split('\n');
    for (var i = 0; i < code.Length; i++)
    {
        var line = code[i];
        string? why = banned.Match(line) is { Success: true } word ? word.Value : line.TrimStart().StartsWith("#if") ? "#if"
            : line.Contains("using MegaCrit") ? "using MegaCrit" : openClass.IsMatch(line) ? "a class neither sealed nor static"
            : instanceField.IsMatch(line) ? "a public instance field"
            : defaulted.Match(line) is { Success: true } call && Regex.IsMatch(call.Groups[1].Value, "=(?!>)") ? "a parameter default" : null;
        if (why != null)
            shapes.Add($"{Name(path)}:{i + 1} ({why})");
    }
}
Check("the API's files keep its shapes (no const, record, struct, interface, event, #if, game types, open classes, public fields or parameter defaults)",
    shapes.Count == 0, List(shapes));
// 3. The shipped surface only grows: every line PublicAPI.Shipped.txt had at the latest release tag is still there.
var shippedPath = Path.Combine(root, "src", "SpireModManager", "PublicAPI.Shipped.txt");
var unshippedPath = Path.Combine(root, "src", "SpireModManager", "PublicAPI.Unshipped.txt");
static List<string> ApiLines(string text) => text.Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
string? Git(params string[] arguments)
{
    try
    {
        var info = new ProcessStartInfo("git", ["-C", root, .. arguments]) { RedirectStandardOutput = true, RedirectStandardError = true };
        using var git = Process.Start(info);
        var output = git?.StandardOutput.ReadToEnd();
        git?.WaitForExit();
        return git?.ExitCode == 0 ? output : null;
    }
    catch (System.ComponentModel.Win32Exception)
    {
        return null; // no git: nothing to compare with
    }
}
var shipped = File.Exists(shippedPath) ? ApiLines(File.ReadAllText(shippedPath)) : [];
var lastTag = Git("describe", "--tags", "--abbrev=0", "--match", "v*")?.Trim();
var atTag = lastTag is { Length: > 0 } ? Git("show", $"{lastTag}:src/SpireModManager/PublicAPI.Shipped.txt") : null; // none before the API shipped
var dropped = atTag == null ? [] : ApiLines(atTag).Except(shipped).ToList();
Check($"the shipped API only grows: every line of PublicAPI.Shipped.txt at {lastTag ?? "the last release"} is still there", dropped.Count == 0, List(dropped));
// 4. ApiInfo.Version follows the snapshot: it's VERSIONS.md's last row; while PublicAPI.Unshipped.txt has lines, that
//    row reads "unreleased" and is the one before it plus one; it's a public static readonly int.
var infoSource = sources.FirstOrDefault(s => InApi(s.Key) && Path.GetFileName(s.Key) == "ApiInfo.cs").Value ?? "";
var versionField = Regex.Match(infoSource, @"public static readonly int Version = (\d+);");
var versionsPath = Path.Combine(apiDir, "VERSIONS.md");
var rows = File.Exists(versionsPath)
    ? Regex.Matches(File.ReadAllText(versionsPath), @"^\|\s*(\d+)\s*\|\s*([^|]+?)\s*\|", RegexOptions.Multiline)
        .Select(m => (Api: int.Parse(m.Groups[1].Value), Release: m.Groups[2].Value)).ToList()
    : [];
var unshipped = File.Exists(unshippedPath) ? ApiLines(File.ReadAllText(unshippedPath)) : [];
var versionOk = versionField.Success && rows.Count > 0 && int.Parse(versionField.Groups[1].Value) == rows[^1].Api
    && (unshipped.Count == 0 || rows[^1].Release.Equals("unreleased", StringComparison.OrdinalIgnoreCase) && rows[^1].Api == (rows.Count > 1 ? rows[^2].Api + 1 : 1));
Check("ApiInfo.Version is VERSIONS.md's last row (\"unreleased\", one more than the row before, while PublicAPI.Unshipped.txt has lines)", versionOk,
    $"field {(versionField.Success ? versionField.Groups[1].Value : "missing")}, last row {(rows.Count > 0 ? $"{rows[^1].Api} {rows[^1].Release}" : "none")}, {unshipped.Count} unshipped");
// 5. The assembly version stays 1.0.0.0: dependents bind to it, and the game matches it strictly.
Check("the assembly version stays 1.0.0.0 (other mods bind to it)",
    File.ReadAllText(Path.Combine(root, "src", "SpireModManager", "SpireModManager.csproj")).Contains("<AssemblyVersion>1.0.0.0</AssemblyVersion>"));
// 6. Every API member has a test: each name in the PublicAPI files appears in SelfTest.Api.cs.
var apiTest = sources.FirstOrDefault(s => Path.GetFileName(s.Key) == "SelfTest.Api.cs").Value ?? "";
var memberName = new Regex(@"SpireModManager\.Api\.(?:\w+\.)*?(\w+)(?:\.(?:get|init|set))?(?:<\w+>)?(?:\(| ->| =|$)");
var untested = shipped.Concat(unshipped).Select(l => memberName.Match(l)).Where(m => m.Success).Select(m => m.Groups[1].Value).Distinct()
    .Where(n => !Regex.IsMatch(apiTest, $@"\b{Regex.Escape(n)}\b")).ToList();
Check("every API member is named in the API self-test (SelfTest.Api.cs)", unshipped.Count + shipped.Count > 0 && untested.Count == 0, List(untested));

// Translation tooling: tools/loc.cs judges texts with the game's parser, and the self-test checks the game still ships
// that version, so both name the same one.
var package = Regex.Match(File.ReadAllText(Path.Combine(root, "tools", "loc.cs")), @"^#:package SmartFormat@(\S+)", RegexOptions.Multiline)
    .Groups[1].Value;
var tested = Regex.Match(File.ReadAllText(Path.Combine(root, "src", "SpireModManager", "SelfTest.cs")), @"const string ToolParser = ""([^""]+)""")
    .Groups[1].Value;
Check($"tools/loc.cs and the self-test name the same SmartFormat ({package})", package.Length > 0 && package == tested,
    $"loc.cs has {(package.Length > 0 ? package : "none")}, SelfTest.ToolParser {(tested.Length > 0 ? tested : "none")}");

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

// A C# source with its comments and the text of its strings and chars blanked out, line breaks kept (so line numbers
// hold). Strings: regular, verbatim (@) and raw (three quotes or more), each maybe interpolated ($, or $$ and more for
// raw ones); their holes are code, so they stay.
static string CodeOnly(string source)
{
    var code = new StringBuilder(source.Length);
    var i = 0;
    Code(hole: false);
    return code.ToString();

    char At(int offset) => i + offset < source.Length ? source[i + offset] : '\0';
    int Run(char c) => source.AsSpan(i).IndexOfAnyExcept(c) is var n and >= 0 ? n : source.Length - i;
    int Upto(string end, int from) => source.IndexOf(end, from, StringComparison.Ordinal) is var at and >= 0 ? at + end.Length - i : source.Length - i;
    void Keep() => code.Append(source[i++]);
    void Blank(int count)
    {
        for (var end = Math.Min(i + count, source.Length); i < end; i++)
            code.Append(source[i] == '\n' ? '\n' : ' ');
    }

    // Code to the end, or in a hole to the brace that closes it (left for the string).
    void Code(bool hole)
    {
        var depth = 0;
        while (i < source.Length)
        {
            var c = source[i];
            if (c == '/' && At(1) == '/')
                Blank(Upto("\n", i));
            else if (c == '/' && At(1) == '*')
                Blank(Upto("*/", i + 2));
            else if (c == '\'')
                Char();
            else if (c == '"' || c is '$' or '@' && source.AsSpan(i).TrimStart("$@").StartsWith("\""))
                Text();
            else if (hole && c == '}' && depth == 0)
                return;
            else
            {
                depth += hole && c == '{' ? 1 : hole && c == '}' ? -1 : 0;
                Keep();
            }
        }
    }

    void Char()
    {
        var end = i + 1;
        while (end < source.Length && source[end] is not ('\'' or '\n'))
            end += source[end] == '\\' ? 2 : 1;
        Blank(end + 1 - i);
    }

    void Text()
    {
        var (dollars, verbatim) = (0, false);
        while (source[i] is '$' or '@')
        {
            dollars += source[i] == '$' ? 1 : 0;
            verbatim |= source[i] == '@';
            Blank(1);
        }
        var quotes = Run('"');
        if (!verbatim && quotes >= 3)
        {
            Blank(quotes);
            var close = new string('"', quotes);
            while (i < source.Length && !source.AsSpan(i).StartsWith(close))
                if (dollars > 0 && At(0) == '{' && Run('{') >= dollars)
                    Hole(Run('{'), dollars);
                else
                    Blank(1);
            Blank(quotes);
            return;
        }
        if (!verbatim && quotes == 2)
        {
            Blank(2); // ""
            return;
        }
        Blank(1);
        while (i < source.Length)
        {
            if (verbatim ? At(0) == '"' && At(1) == '"' : At(0) == '\\')
                Blank(2);
            else if (At(0) == '"' || !verbatim && At(0) == '\n') // a verbatim string may span lines
            {
                Blank(1);
                return;
            }
            else if (dollars > 0 && At(0) == '{')
            {
                if (At(1) == '{')
                    Blank(2);
                else
                    Hole(1, 1);
            }
            else
                Blank(1);
        }
    }

    // A hole: its opening braces, its code, then its closing ones.
    void Hole(int opening, int closing)
    {
        Blank(opening);
        Code(hole: true);
        Blank(Math.Min(closing, Run('}')));
    }
}

static string ScriptDir([CallerFilePath] string path = "") => Path.GetDirectoryName(path) ?? ".";

// Code: snippets of the source file that build the keys (all must still be there); Keys: the keys that
// code can make, without the prefix, worked out from the file's source.
record Rule(string File, string[] Code, Func<string, IEnumerable<string>> Keys);
