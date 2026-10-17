// The English texts across versions, and where a translation stands against them. No game needed; git and the
// repository's tags (git fetch --tags) for anything but the English as it is now.
//
//   dotnet run tools/loc.cs versions                       each release's English (the vX.Y.Z tags) and now: how many
//                                                          texts, and what changed since the release before
//   dotnet run tools/loc.cs changes <from> [<to>] [--md]   the English between two versions: a tag, a commit or a branch,
//                                                          `latest` for the newest release; no <to> = as it is now.
//                                                          --md: a Markdown table (CI's job summary)
//   dotnet run tools/loc.cs status <file> [--base <ref>] [--eng <ref or file>]
//                                                          a translation against the English (now, or --eng): texts new
//                                                          since it was made, texts whose English changed since, keys the
//                                                          English no longer has, and texts the mod skips (EMPTY, BROKEN;
//                                                          see read), each with its line in the file. Exit code 1 if any.
//   dotnet run tools/loc.cs compare <file> --base <ref> [--to <ref>] [--old <file>] [--html <page.html>]
//                                                          both sides of each English change since --base: the English
//                                                          then and now, and the translation then (--old: the copy made for
//                                                          that version) and now, with its line; then the texts it still
//                                                          lacks, the ones to fix (the mod skips them: see read) and the
//                                                          keys English doesn't have. --html: the same as a
//                                                          page to open in a browser, side by side. Exit code 1 if any to do.
//   dotnet run tools/loc.cs read <file> [--eng <ref or file>]
//                                                          how the mod reads a translation, key by key: the texts it uses,
//                                                          and the ones it skips and why (the English shows for those).
//                                                          Exit code 1 if it skips any. CI runs it on tools/fixtures/loc.
//
// A translation file holds only its own language, as the game reads it: the English it was made from is a version
// (--base), else the oldest release whose texts the file has (said so in the report; it can flag a text that was
// already updated, never miss one).
// The game's own text parser, the version it ships (data_sts2_windows_x86_64/SmartFormat.dll): a text it can't parse,
// the mod skips (Translations.Formats, through the game's LocValidator), and so does this tool. The self-test fails
// when the game's version changes (SelfTest.ToolParser; check.cs keeps the two equal): then update both.
#:package SmartFormat@3.3.0
using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

const string English = "localization/eng/settings_ui.json", Prefix = "SPIRE_MOD_MANAGER.";
var root = Path.GetDirectoryName(ScriptDir()) ?? ".";

if (args.Length == 0)
    return Usage();
try
{
    return args[0] switch
    {
        "versions" => Versions(),
        "changes" when Positional() is [var from, .. var rest] => Changes(from, rest.FirstOrDefault(), args.Contains("--md")),
        "status" when Positional() is [var file, ..] => Status(file, Option("--base"), Option("--eng")),
        "compare" when Positional() is [var file, ..] && Option("--base") is { } version => Compare(file, version, Option("--to"), Option("--old"), Option("--html")),
        "read" when Positional() is [var file, ..] => Read(file, Option("--eng")),
        _ => Usage(),
    };
}
catch (Problem e)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}

int Usage()
{
    Console.Error.WriteLine("usage: dotnet run tools/loc.cs versions | changes <from> [<to>] [--md] | status <file> [--base <ref>] [--eng <ref or file>]"
        + " | compare <file> --base <ref> [--to <ref>] [--old <file>] [--html <page.html>] | read <file> [--eng <ref or file>]");
    return 1;
}

// An option's value (null: not given). Given without one, it's refused rather than read as the next option.
string? Option(string name) => Array.IndexOf(args, name) is not (var at and >= 0) ? null
    : at + 1 < args.Length && !args[at + 1].StartsWith("--") ? args[at + 1]
    : throw new Problem($"{name} needs a value: {name} <{(name is "--old" or "--html" ? "file" : "version")}>");

// The arguments after the command that aren't options or their values, in order.
List<string> Positional()
{
    var (positional, valued) = (new List<string>(), new[] { "--base", "--eng", "--to", "--old", "--html" });
    for (var i = 1; i < args.Length; i++)
    {
        if (valued.Contains(args[i]))
            i++;
        else if (!args[i].StartsWith("--"))
            positional.Add(args[i]);
    }
    return positional;
}

// Each release's English, then now's: its count and what changed since the one before.
int Versions()
{
    var releases = Releases();
    Console.WriteLine($"{"version",-10}{"date",-12}{"texts",-7}since the one before");
    Dictionary<string, string>? before = null;
    foreach (var (name, date, texts) in releases.Select(r => (r, Git("log", "-1", "--format=%cs", r)?.Trim() ?? "", EnglishAt(r)))
        .Append(("now", "", EnglishAt(null))))
    {
        var since = before == null ? "" : Summary(Diff(before, texts));
        Console.WriteLine($"{name,-10}{date,-12}{texts.Count,-7}{since}");
        before = texts;
    }
    return 0;
}

// The English between two versions, text by text.
int Changes(string from, string? to, bool markdown)
{
    var (fromName, toName) = (Resolve(from), to == null ? null : Resolve(to));
    var (old, now) = (EnglishAt(fromName), EnglishAt(toName));
    var diff = Diff(old, now);
    var title = $"English {fromName} ({old.Count} texts) -> {toName ?? "now"} ({now.Count}): {Summary(diff)}";
    if (markdown)
    {
        Console.WriteLine($"### {title}\n");
        if (diff.Count == 0)
            return 0;
        Console.WriteLine("| | Text | English |\n| --- | --- | --- |");
        foreach (var (kind, key, was, text) in diff)
            Console.WriteLine($"| {kind} | `{Short(key)}` | {Cell(kind == "changed" ? WordDiff(was ?? "", text ?? "") : text ?? was ?? "")} |");
        return 0;
    }
    Console.WriteLine(title);
    foreach (var (kind, key, was, text) in diff)
        Console.WriteLine(kind == "changed" ? $"CHANGED {Short(key)}\n  {OneLine(WordDiff(was ?? "", text ?? ""))}" : $"{kind.ToUpperInvariant()} {Short(key)} {Quoted(text ?? was ?? "")}");
    return 0;
}

// A translation against the English: what's new since it was made, what changed, what went; each with its line.
int Status(string file, string? baseRef, string? eng)
{
    var now = eng != null && File.Exists(eng) ? ReadEnglishFile(eng) : EnglishAt(eng == null ? null : Resolve(eng));
    var translation = ReadTranslation(file, now);
    var (basis, made) = Basis(translation, baseRef);
    var fresh = now.Keys.Where(k => !translation.Lines.ContainsKey(k)).ToList();
    // Listed, but the mod can't use them and shows the English: blank ones are still to translate, the others to fix.
    var empty = translation.Skipped.Where(p => now.ContainsKey(p.Key) && p.Value.Why == "blank").Select(p => p.Key).ToList();
    var broken = translation.Skipped.Where(p => now.ContainsKey(p.Key) && p.Value.Why != "blank").ToList();
    var gone = translation.Lines.Keys.Where(k => !now.ContainsKey(k)).ToList();
    var changed = translation.Texts.Keys.Where(k => now.TryGetValue(k, out var text) && made?.GetValueOrDefault(k) is { } was && was != text).ToList();
    var name = Path.GetFileName(file);
    Console.WriteLine($"{name} ({translation.Texts.Count} texts) against the English {(eng == null ? "now" : eng)} ({now.Count} texts)");
    Console.WriteLine($"made from: {basis}");
    Console.WriteLine($"{fresh.Count} new, {changed.Count} changed, {gone.Count} no longer in English"
        + $"{(empty.Count > 0 ? $", {empty.Count} empty" : "")}{(broken.Count > 0 ? $", {broken.Count} broken" : "")}{(translation.Todos > 0 ? $", {translation.Todos} marked TODO" : "")}");
    if (translation.Twice.Count > 0)
        Console.WriteLine($"listed twice (the mod uses the last copy it can): {string.Join(", ", translation.Twice.Select(Short))}");
    foreach (var key in fresh)
        Console.WriteLine($"NEW      {Short(key)} {Quoted(now[key])}");
    foreach (var key in empty)
        Console.WriteLine($"EMPTY    {name}:{translation.Lines[key]} {Short(key)} {Quoted(now[key])}");
    foreach (var (key, skip) in broken)
        Console.WriteLine($"BROKEN   {name}:{translation.Lines[key]} {Short(key)}: {skip.Why}, so the English shows\n  yours:   {OneLine(skip.Text)}");
    foreach (var key in changed)
        Console.WriteLine($"CHANGED  {name}:{translation.Lines[key]} {Short(key)}\n  English: {OneLine(WordDiff(made?.GetValueOrDefault(key) ?? "", now[key]))}\n  yours:   {OneLine(translation.Texts[key])}");
    foreach (var key in gone)
        Console.WriteLine($"GONE     {name}:{translation.Lines[key]} {Short(key)}");
    return fresh.Count + empty.Count + broken.Count + changed.Count + gone.Count + translation.Todos > 0 ? 1 : 0;
}

// Both sides of each English change since a version: the English then and now, the translation then and now. Then
// what else the translation lacks or has extra, so the list is all there is to do.
int Compare(string file, string from, string? to, string? oldFile, string? html)
{
    var (fromName, toName) = (Resolve(from), to == null ? null : Resolve(to));
    var (englishThen, englishNow) = (EnglishAt(fromName), EnglishAt(toName));
    var now = ReadTranslation(file, englishNow);
    var then = oldFile == null ? null : ReadTranslation(oldFile, englishThen);
    var name = Path.GetFileName(file);
    // A listed text the mod can't use for a reason other than being blank: to fix, not to translate.
    Skip? Broken(string key) => now.Skipped.GetValueOrDefault(key) is { Why: not "blank" } skip ? skip : null;
    // Per English change: what the translation has to do, given what it did since (when its old copy says).
    var rows = Diff(englishThen, englishNow).Select(change =>
    {
        var (kind, key, was, text) = change;
        var (old, current, broken) = (then?.Texts.GetValueOrDefault(key), now.Texts.GetValueOrDefault(key), Broken(key));
        var (state, todo) = kind switch
        {
            "removed" => now.Lines.ContainsKey(key) ? ("to remove", true) : ("removed", false),
            _ when broken != null => ("to fix", true),
            "new" => current == null ? ("to translate", true) : ("translated", false),
            _ when current == null => ("to translate", true),
            _ when then == null => ("to check", true),
            _ => old == current ? ("to update", true) : ("updated", false),
        };
        var shown = current ?? broken?.Text ?? (kind == "removed" ? now.Skipped.GetValueOrDefault(key)?.Text : null);
        return new Row(key, kind, was, text, old, shown, now.Lines.GetValueOrDefault(key), state, todo, broken?.Why);
    }).ToList();
    var listed = rows.Select(r => r.Key).ToHashSet();
    rows.AddRange(englishNow.Where(p => !listed.Contains(p.Key) && !now.Texts.ContainsKey(p.Key)).Select(p => Broken(p.Key) is { } broken
        ? new Row(p.Key, "unchanged", p.Value, p.Value, then?.Texts.GetValueOrDefault(p.Key), broken.Text, now.Lines[p.Key], "to fix", true, broken.Why)
        : new Row(p.Key, "unchanged", p.Value, p.Value, then?.Texts.GetValueOrDefault(p.Key), null, now.Lines.GetValueOrDefault(p.Key), "to translate", true)));
    rows.AddRange(now.Lines.Keys.Where(k => !listed.Contains(k) && !englishNow.ContainsKey(k)).Select(k => new Row(k, "not in English", null, null,
        then?.Texts.GetValueOrDefault(k), now.Texts.GetValueOrDefault(k) ?? now.Skipped.GetValueOrDefault(k)?.Text, now.Lines[k], "to remove", true)));
    // The changed first: they need both sides.
    rows = [.. rows.OrderBy(r => r.Kind switch { "changed" => 0, "new" => 1, "unchanged" => 2, "removed" => 3, _ => 4 })];
    var title = $"{name} for English {fromName} -> {toName ?? "now"}" + (then != null ? $", its copy then: {Path.GetFileName(oldFile)}" : "");
    var counts = rows.Count == 0 ? "no English change, and no text missing" : string.Join(", ", rows.GroupBy(r => r.State).Select(g => $"{g.Count()} {g.Key}"));
    var result = rows.Any(r => r.Todo) ? 1 : 0;
    if (html != null)
    {
        try
        {
            File.WriteAllText(html, Page(title, counts, name, rows, then != null));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new Problem($"couldn't write {html}: {(e is DirectoryNotFoundException ? "its folder doesn't exist" : e.Message)}");
        }
        Console.WriteLine($"{title}: {counts}\nwritten to {Path.GetFullPath(html)}");
        return result;
    }
    Console.WriteLine($"{title}\n{counts}");
    foreach (var row in rows)
    {
        Console.WriteLine($"\n{row.State.ToUpperInvariant(),-13} {(row.Line > 0 ? $"{name}:{row.Line} " : "")}{Short(row.Key)} ({row.Kind})");
        Console.WriteLine($"  English:     {row.Kind switch
        {
            "changed" => OneLine(WordDiff(row.Was ?? "", row.Text ?? "")),
            "not in English" => "none",
            _ => Quoted(row.Text ?? row.Was ?? ""),
        }}");
        if (then != null && row.Old != row.Current)
            Console.WriteLine($"  translation: {OneLine(WordDiff(row.Old ?? "", row.Current ?? ""))}");
        else if (row.Current != null)
            Console.WriteLine($"  translation: {OneLine(row.Current)}{(then != null ? " (unchanged)" : "")}");
        if (row.Note != null)
            Console.WriteLine($"  problem:     {row.Note}, so the English shows");
    }
    return result;
}

// How the mod reads a translation (Translations.Read), key by key in the file's order: USED, EMPTY or BROKEN (skipped,
// so the English shows), or UNKNOWN (a key the English doesn't have, left out).
int Read(string file, string? eng)
{
    var english = eng != null && File.Exists(eng) ? ReadEnglishFile(eng) : EnglishAt(eng == null ? null : Resolve(eng));
    var translation = ReadTranslation(file, english);
    var name = Path.GetFileName(file);
    var known = translation.Lines.Keys.Where(english.ContainsKey).ToList();
    var skipped = known.Count(translation.Skipped.ContainsKey);
    Console.WriteLine($"{name}: used {known.Count - skipped}, skipped {skipped} (the English shows), left out {translation.Lines.Count - known.Count}"
        + $" (not in the English), missing {english.Keys.Count(k => !translation.Lines.ContainsKey(k))} (English texts the file doesn't list)");
    foreach (var (key, line) in translation.Lines)
    {
        var skip = translation.Skipped.GetValueOrDefault(key);
        var what = !english.ContainsKey(key) ? "UNKNOWN" : skip == null ? "USED" : skip.Why == "blank" ? "EMPTY" : "BROKEN";
        Console.WriteLine($"{what,-8} {name}:{line} {Short(key)}{(skip != null && english.ContainsKey(key) ? $": {skip.Why}" : "")}");
    }
    if (translation.Twice.Count > 0)
        Console.WriteLine($"listed twice (the mod uses the last copy it can): {string.Join(", ", translation.Twice.Select(Short))}");
    return skipped + translation.Lines.Count - known.Count > 0 ? 1 : 0;
}

// The English each text of a translation was made from: --base's, else the oldest release it fits (null: unknown).
(string Basis, Dictionary<string, string>? Made) Basis(Translation translation, string? baseRef)
{
    if (baseRef != null && Resolve(baseRef) is var version)
        return ($"{version} (--base)", EnglishAt(version));
    // The oldest release with every key the file has (fewest texts the file lacks first). None without git or tags.
    var releases = Releases(quiet: true);
    // Every key the file lists counts, blank or broken ones too: they were made for that English as well.
    var fits = releases.Select(r => (Release: r, Texts: EnglishAt(r))).Where(r => translation.Lines.Keys.All(r.Texts.ContainsKey))
        .OrderBy(r => r.Texts.Keys.Count(k => !translation.Lines.ContainsKey(k))).FirstOrDefault();
    if (fits.Release != null)
        return ($"{fits.Release}, inferred from its texts (--base <version> to set it)", fits.Texts);
    return (releases.Count == 0 ? "unknown: no release tags here (git fetch --tags, or --base <version>), so changed English can't be told"
        : "unknown: no release has all its keys (--base <version> to set it), so changed English can't be told", null);
}

// The release tags, oldest first: vX.Y.Z only (not v0.1.0-before-retag, not mvp).
List<string> Releases(bool quiet = false) => [.. (Quietly(quiet, () => Git("tag", "--list", "v*")) ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .Where(t => Regex.IsMatch(t, @"^v\d+\.\d+\.\d+$")).OrderBy(t => Version.Parse(t[1..]))];

// "latest": the newest release; anything else as git takes it.
string Resolve(string name) => name == "latest"
    ? Releases().LastOrDefault() ?? throw new Problem("no release tags here: git fetch --tags")
    : name;

// The English at a version (null: the file as it is now), in its own order.
Dictionary<string, string> EnglishAt(string? version)
{
    var json = version == null ? File.ReadAllText(Path.Combine(root, English))
        : Git("show", $"{version}:{English}") ?? throw new Problem($"no {English} at {version} (a tag, commit or branch; git fetch --tags for the releases)");
    return Flat(json);
}

Dictionary<string, string> ReadEnglishFile(string path) => Flat(File.ReadAllText(path));

static Dictionary<string, string> Flat(string json)
{
    using var document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
    var texts = new Dictionary<string, string>();
    foreach (var property in document.RootElement.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String))
        texts[property.Name] = property.Value.GetString() ?? ""; // a key listed twice: the last counts
    return texts;
}

// A translation file, read as the mod reads it (Translations.Read): per key, the last copy the mod can use. It skips a
// copy, and shows the English instead, when the text is blank or isn't text, and (given the English) when the game
// can't parse it or it has a {placeholder} the English doesn't (Unusable). Also each key's line (the copy that counts,
// else the last one skipped), the keys listed twice, and the TODO marks. A file the mod leaves out whole (over 1 MB,
// not JSON, not an object) is refused, saying so.
static Translation ReadTranslation(string path, Dictionary<string, string>? english = null)
{
    if (!File.Exists(path))
        throw new Problem($"{path} doesn't exist");
    if (new FileInfo(path).Length > 1024 * 1024) // Translations.MaxBytes
        throw new Problem($"{Path.GetFileName(path)} is over 1 MB, so the mod leaves it out whole");
    var source = File.ReadAllText(path); // UTF-8 with or without a BOM, or UTF-16, as the mod reads it
    var bytes = Encoding.UTF8.GetBytes(source);
    var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Allow, AllowTrailingCommas = true });
    var (texts, skipped, lines, seen, twice) = (new Dictionary<string, string>(), new Dictionary<string, Skip>(), new Dictionary<string, int>(), new HashSet<string>(), new List<string>());
    var (todos, depth) = (0, 0);
    // For "file:line" pointers: the line breaks before where the reader found the key.
    var (line, scanned, keyLine) = (1, 0L, 0);
    string? key = null;
    // One copy of a key: the text the mod uses, or why it skips it (an earlier copy it can use then stays).
    void Copy(string text, string? why)
    {
        if (key == null)
            return;
        if (!seen.Add(key))
            twice.Add(key);
        if (why == null)
        {
            (texts[key], lines[key]) = (text, keyLine);
            skipped.Remove(key);
        }
        else if (!texts.ContainsKey(key))
            (skipped[key], lines[key]) = (new Skip(text, why), keyLine);
        key = null;
    }
    try
    {
        while (reader.Read())
        {
            if (depth == 0 && reader.TokenType is not (JsonTokenType.StartObject or JsonTokenType.Comment))
                throw new Problem($"{Path.GetFileName(path)} isn't a list of \"key\": \"text\" pairs, so the mod leaves it out whole");
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject or JsonTokenType.StartArray:
                    if (depth == 1)
                        Copy(reader.TokenType == JsonTokenType.StartObject ? "{…}" : "[…]", "not text");
                    depth++;
                    break;
                case JsonTokenType.EndObject or JsonTokenType.EndArray:
                    depth--;
                    break;
                case JsonTokenType.Comment when reader.GetComment().Trim().StartsWith("TODO"):
                    todos++;
                    break;
                case JsonTokenType.PropertyName when depth == 1:
                    key = reader.GetString();
                    for (; scanned < reader.TokenStartIndex; scanned++)
                        line += bytes[scanned] == '\n' ? 1 : 0;
                    keyLine = line;
                    break;
                case JsonTokenType.String when depth == 1 && key != null:
                    var text = reader.GetString() ?? "";
                    Copy(text, Unusable(text, english?.GetValueOrDefault(key)));
                    break;
                case JsonTokenType.Number or JsonTokenType.True or JsonTokenType.False or JsonTokenType.Null when depth == 1:
                    Copy(Encoding.UTF8.GetString(reader.ValueSpan), "not text");
                    break;
            }
        }
    }
    catch (JsonException e)
    {
        throw new Problem($"{Path.GetFileName(path)} isn't valid JSON (line {e.LineNumber + 1}, at byte {e.BytePositionInLine + 1}): {Regex.Replace(e.Message, @"\s*LineNumber:.*$", "")}");
    }
    return new Translation(texts, skipped, lines, todos, [.. twice.Distinct()]);
}

// Why the mod can't use a text (null: it can), as Translations.Read decides: blank, the game can't parse it, or a
// {placeholder} its English doesn't have. The last two only for a key the English has, as in the mod.
static string? Unusable(string text, string? english)
{
    if (text.Trim().Length == 0)
        return "blank";
    if (english == null)
        return null;
    try
    {
        Game.Parser.ParseFormat(text); // LocValidator.ValidateFormatString, as the game runs it
    }
    catch (SmartFormat.Core.Parsing.ParsingErrors e)
    {
        // Its issue names a character by its code ('0x43F'): shown as the character.
        var issue = Regex.Replace(e.Issues.FirstOrDefault()?.Issue.TrimEnd('.') ?? "a format error", "'0x([0-9A-Fa-f]{1,4})'",
            m => $"'{(char)Convert.ToInt32(m.Groups[1].Value, 16)}'");
        return $"the game can't read it ({issue})";
    }
    catch (Exception)
    {
        // Any other failure of the check counts as a pass, as in Translations.Formats.
    }
    if (Placeholders(text).Except(Placeholders(english)).ToList() is not [_, ..] strange)
        return null;
    return $"{string.Join(", ", strange.Select(name => $"{{{name}}}"))} {(strange.Count == 1 ? "isn't" : "aren't")} in the English";
}

// The names a text's placeholders use: "count" in "{count}" and in "{count:plural:mod|mods}" (Translations.Placeholders).
static HashSet<string> Placeholders(string text) => [.. Regex.Matches(text, @"\{([A-Za-z_]\w*)").Select(m => m.Groups[1].Value)];

// Text by text from one English to another: new, changed (old and new), removed, in the newer one's order.
static List<(string Kind, string Key, string? Was, string? Text)> Diff(Dictionary<string, string> old, Dictionary<string, string> now) =>
[
    .. now.Where(p => !old.ContainsKey(p.Key)).Select(p => ("new", p.Key, (string?)null, (string?)p.Value)),
    .. now.Where(p => old.TryGetValue(p.Key, out var was) && was != p.Value).Select(p => ("changed", p.Key, (string?)old[p.Key], (string?)p.Value)),
    .. old.Where(p => !now.ContainsKey(p.Key)).Select(p => ("removed", p.Key, (string?)p.Value, (string?)null)),
];

static string Summary(List<(string Kind, string Key, string? Was, string? Text)> diff) => diff.Count == 0 ? "no change"
    : $"{diff.Count(d => d.Kind == "new")} new, {diff.Count(d => d.Kind == "removed")} removed, {diff.Count(d => d.Kind == "changed")} changed";

// What stayed (0), went (-1) and came (+1), word by word (letter by letter in Chinese, Japanese and Korean, which
// don't space their words). A kept run of at most one word between two changes joins them: "turned on or off" ->
// "enabled or disabled" reads as one change, not three.
static List<(int Side, string Text)> Pieces(string was, string now)
{
    // Letters and marks of scripts written without spaces; the rarer ideographs past U+FFFF come as surrogate pairs.
    const string Cjk = @"\p{IsCJKUnifiedIdeographs}\p{IsCJKUnifiedIdeographsExtensionA}\p{IsCJKCompatibilityIdeographs}\p{IsCJKSymbolsandPunctuation}"
        + @"\p{IsHiragana}\p{IsKatakana}\p{IsKatakanaPhoneticExtensions}\p{IsHangulSyllables}\p{IsHangulJamo}\p{IsHangulCompatibilityJamo}"
        + @"\p{IsHalfwidthandFullwidthForms}\uD840-\uD8BF";
    // A [tag] or {slot} on its own, a CJK letter, a space run, or a word up to the next of those.
    static string[] Tokens(string text) => [.. Regex.Matches(text, $@"\[[^\]\s]*\]|\{{[^}}\s]*\}}|[\uD840-\uD8BF][\uDC00-\uDFFF]|[{Cjk}]|\s+|[^\s\[{{{Cjk}]+|.")
        .Select(m => m.Value)];
    var (a, b) = (Tokens(was), Tokens(now));
    var common = new int[a.Length + 1, b.Length + 1];
    for (var i = a.Length - 1; i >= 0; i--)
        for (var j = b.Length - 1; j >= 0; j--)
            common[i, j] = a[i] == b[j] ? common[i + 1, j + 1] + 1 : Math.Max(common[i + 1, j], common[i, j + 1]);
    var pieces = new List<(int Side, string Text)>();
    var (p, q) = (0, 0);
    while (p < a.Length || q < b.Length)
    {
        if (p < a.Length && q < b.Length && a[p] == b[q])
        {
            pieces.Add((0, a[p++]));
            q++;
        }
        else if (q < b.Length && (p == a.Length || common[p, q + 1] >= common[p + 1, q]))
            pieces.Add((1, b[q++]));
        else
            pieces.Add((-1, a[p++]));
    }
    for (var i = 0; i < pieces.Count; i++)
    {
        if (pieces[i].Side != 0)
            continue;
        var end = i;
        while (end < pieces.Count && pieces[end].Side == 0)
            end++;
        var words = pieces.Skip(i).Take(end - i).Count(piece => piece.Text.Trim().Length > 0);
        if (i > 0 && end < pieces.Count && words <= 1)
        {
            var kept = pieces.Skip(i).Take(end - i).ToList();
            pieces.RemoveRange(i, end - i);
            pieces.InsertRange(i, [.. kept.Select(k => (-1, k.Text)), .. kept.Select(k => (1, k.Text))]);
            end = i + kept.Count * 2;
        }
        i = end - 1;
    }
    return pieces;
}

// The pieces as git's --word-diff writes them: [-went-]{+came+}.
static string WordDiff(string was, string now) => Render(Pieces(was, now), text => text, gone => $"[-{gone}-]", came => $"{{+{came}+}}");

// The pieces as HTML: went struck through in red, came in green.
static string HtmlDiff(string was, string now) => Render(Pieces(was, now), WebUtility.HtmlEncode,
    gone => $"<del>{WebUtility.HtmlEncode(gone)}</del>", came => $"<ins>{WebUtility.HtmlEncode(came)}</ins>");

static string Render(List<(int Side, string Text)> pieces, Func<string, string> kept, Func<string, string> went, Func<string, string> came)
{
    var (text, gone, come) = (new StringBuilder(), new StringBuilder(), new StringBuilder());
    void Flush()
    {
        if (gone.Length > 0)
            text.Append(went(gone.ToString()));
        if (come.Length > 0)
            text.Append(came(come.ToString()));
        (gone.Length, come.Length) = (0, 0);
    }
    foreach (var (side, piece) in pieces)
    {
        if (side == 0)
        {
            Flush();
            text.Append(kept(piece));
        }
        else
            (side < 0 ? gone : come).Append(piece);
    }
    Flush();
    return text.ToString();
}

// The comparison as one page: a row per English change, its state, the English then -> now and the translation then
// -> now side by side. Light or dark with the system.
static string Page(string title, string counts, string file, List<Row> rows, bool hasOld)
{
    static string E(string? text) => WebUtility.HtmlEncode(text ?? "");
    var body = new StringBuilder();
    foreach (var row in rows)
    {
        var english = row.Kind switch
        {
            "changed" => HtmlDiff(row.Was ?? "", row.Text ?? ""),
            "new" => $"<ins>{E(row.Text)}</ins>",
            "removed" => $"<del>{E(row.Was)}</del>",
            "not in English" => "<span class=none>not in English</span>",
            _ => E(row.Text),
        };
        var translation = row.Current == null && row.Old == null ? "<span class=none>none</span>"
            : hasOld && row.Old != row.Current ? HtmlDiff(row.Old ?? "", row.Current ?? "") : E(row.Current) + (hasOld ? " <span class=none>(unchanged)</span>" : "");
        body.Append($"<tr class=\"{(row.Todo ? "todo" : "done")}\"><td><code>{E(Short(row.Key))}</code>{(row.Line > 0 ? $"<br><span class=line>{E(file)}:{row.Line}</span>" : "")}</td>"
            + $"<td class=state>{E(row.State)}{(row.Note != null ? $"<br><span class=note>{E(row.Note)}, so the English shows</span>" : "")}</td>"
            + $"<td class=en>{english}</td><td class=tr>{translation}</td></tr>\n");
    }
    return $$"""
<!doctype html>
<html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>{{E(title)}}</title>
<style>
:root { --bg: #fdfcf8; --text: #222; --muted: #777; --line: #ddd; --del: #c62828; --del-bg: #fde7e7; --ins: #1b5e20; --ins-bg: #e3f4e4; --todo: #fff6dd; }
@media (prefers-color-scheme: dark) { :root { --bg: #16181d; --text: #e6e6e6; --muted: #9a9a9a; --line: #333; --del: #ff8a80; --del-bg: #3a1f1f; --ins: #a5d6a7; --ins-bg: #1e3321; --todo: #2c2716; } }
body { background: var(--bg); color: var(--text); font: 15px/1.5 system-ui, sans-serif; margin: 24px; }
h1 { font-size: 18px; margin: 0 0 4px; } p { color: var(--muted); margin: 0 0 16px; }
table { border-collapse: collapse; width: 100%; } th, td { border-bottom: 1px solid var(--line); padding: 8px; text-align: left; vertical-align: top; }
th { font-size: 13px; color: var(--muted); font-weight: 600; } .en, .tr { width: 40%; white-space: pre-wrap; }
tr.todo td { background: var(--todo); } .state { white-space: nowrap; font-weight: 600; }
.note { display: inline-block; max-width: 14em; white-space: normal; font-weight: normal; color: var(--del); font-size: 13px; }
del { color: var(--del); background: var(--del-bg); text-decoration: line-through; } ins { color: var(--ins); background: var(--ins-bg); text-decoration: none; }
.line, .none { color: var(--muted); font-size: 13px; } code { font-size: 13px; }
@media (max-width: 700px) {
  body { margin: 16px; } tr.head { display: none; }
  table, tbody, tr, td { display: block; } .en, .tr { width: auto; }
  tr { border-bottom: 1px solid var(--line); padding: 6px 0; } td { border: 0; padding: 2px 8px; }
  .en::before, .tr::before { display: block; color: var(--muted); font-size: 13px; }
  .en::before { content: "English, then \2192  now"; } .tr::before { content: "Translation"; }
}
</style></head><body>
<h1>{{E(title)}}</h1><p>{{E(counts)}}.{{(rows.Count > 0 ? " The rows still to do are highlighted; struck-through red went, green came." : "")}}</p>
<table><tr class=head><th>Text</th><th>State</th><th>English, then &rarr; now</th><th>Translation{{(hasOld ? ", then &rarr; now" : ", now")}}</th></tr>
{{body}}</table></body></html>
""";
}

static string Short(string key) => key.StartsWith(Prefix) ? key[Prefix.Length..] : key;

// The text as a JSON string literal, its letters as they are (not escaped as codes).
static string Quoted(string text)
{
    using var buffer = new MemoryStream();
    using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        writer.WriteStringValue(text);
    return Encoding.UTF8.GetString(buffer.ToArray());
}

// The text on one line, its line breaks and tabs as the file writes them (\n, \t).
static string OneLine(string text) => text.Replace("\r", @"\r").Replace("\n", @"\n").Replace("\t", @"\t");

static string Cell(string text) => text.Replace("|", "\\|").Replace("\n", "<br>");

// git in the repository, its output, or null if it failed.
string? Git(params string[] arguments)
{
    var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 };
    start.ArgumentList.Add("-C");
    start.ArgumentList.Add(root);
    foreach (var argument in arguments)
        start.ArgumentList.Add(argument);
    Process? started;
    try
    {
        started = Process.Start(start);
    }
    catch (System.ComponentModel.Win32Exception)
    {
        started = null;
    }
    using var git = started ?? throw new Problem("git isn't installed or isn't on the PATH: it's needed for the English of other versions (--eng <file> needs none)");
    var output = git.StandardOutput.ReadToEnd();
    git.WaitForExit();
    return git.ExitCode == 0 ? output : null;
}

// What the call gives, or null when it's refused and that's allowed (quiet: inference without git or tags).
static string? Quietly(bool quiet, Func<string?> call)
{
    try
    {
        return call();
    }
    catch (Problem) when (quiet)
    {
        return null;
    }
}

static string ScriptDir([CallerFilePath] string path = "") => Path.GetDirectoryName(path) ?? ".";

// Texts: what the mod uses, per key. Skipped: the keys it can't use any copy of, and why. Lines: every key the file
// lists, in order, at the copy that counts.
record Translation(Dictionary<string, string> Texts, Dictionary<string, Skip> Skipped, Dictionary<string, int> Lines, int Todos, List<string> Twice);

record Skip(string Text, string Why);

record Row(string Key, string Kind, string? Was, string? Text, string? Old, string? Current, int Line, string State, bool Todo, string? Note = null);

// The game's text parser with its default settings, as LocValidator makes it: one, reused for every text.
static class Game
{
    public static readonly SmartFormat.Core.Parsing.Parser Parser = new();
}

// A refusal: its message says what's wrong and what to do.
class Problem(string message) : Exception(message);
