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
//                                                          English no longer has. Exit code 1 if any.
//
// The English a translation was made from, per text: a `// en: "..."` line above its key if it has one, else the
// version --base names, else the oldest release whose texts the file has (said so in the report; it can flag a text
// that was already translated from newer English, never miss one).
using System.Diagnostics;
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
        "changes" when args.Length >= 2 => Changes(args[1], args.Skip(2).FirstOrDefault(a => !a.StartsWith("--")), args.Contains("--md")),
        "status" when args.Length >= 2 => Status(args[1], Option("--base"), Option("--eng")),
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
    Console.Error.WriteLine("usage: dotnet run tools/loc.cs versions | changes <from> [<to>] [--md] | status <file> [--base <ref>] [--eng <ref or file>]");
    return 1;
}

string? Option(string name) => Array.IndexOf(args, name) is var at and >= 0 && at + 1 < args.Length ? args[at + 1] : null;

// Each release's English, then now's: its count and what changed since the one before.
int Versions()
{
    var releases = Releases();
    Console.WriteLine($"{"version",-10}{"date",-12}{"texts",-7}since the one before");
    Dictionary<string, string>? before = null;
    foreach (var (name, date, texts) in releases.Select(r => (r, Git("log", "-1", "--format=%cs", r)?.Trim() ?? "", EnglishAt(r)))
        .Append(("now", "", EnglishAt(null))))
    {
        var since = before == null ? "" : Summary(Compare(before, texts));
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
    var diff = Compare(old, now);
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
        Console.WriteLine(kind == "changed" ? $"CHANGED {Short(key)}\n  {WordDiff(was ?? "", text ?? "")}" : $"{kind.ToUpperInvariant()} {Short(key)} {Quoted(text ?? was ?? "")}");
    return 0;
}

// A translation against the English: what's new since it was made, what changed, what went.
int Status(string file, string? baseRef, string? eng)
{
    if (!File.Exists(file))
        throw new Problem($"{file} doesn't exist");
    var (entries, todos) = ReadTranslation(file);
    var now = eng != null && File.Exists(eng) ? ReadEnglishFile(eng) : EnglishAt(eng == null ? null : Resolve(eng));
    // The English each text was made from: its own `// en:` line, else --base's, else the inferred release's.
    var keys = entries.Select(e => e.Key).ToHashSet();
    string? inferred = null;
    Dictionary<string, string>? baseTexts = null;
    if (entries.Any(e => e.En == null))
    {
        if (baseRef != null)
            baseTexts = EnglishAt(Resolve(baseRef));
        else
        {
            // The oldest release with every key the file has (fewest texts the file lacks first).
            var fits = Releases().Select(r => (Release: r, Texts: EnglishAt(r))).Where(r => keys.All(r.Texts.ContainsKey))
                .OrderBy(r => r.Texts.Keys.Count(k => !keys.Contains(k))).FirstOrDefault();
            (inferred, baseTexts) = (fits.Release, fits.Texts);
        }
    }
    var withLines = entries.Count(e => e.En != null);
    var rest = baseRef != null ? $"{Resolve(baseRef)} (--base)"
        : inferred != null ? $"{inferred}, inferred from its texts (--base <version> to set it)"
        : "unknown: no release has all its keys (--base <version> to set it), so their changed English can't be told";
    var basis = withLines == entries.Count ? "the `// en:` line of each text"
        : withLines > 0 ? $"the `// en:` lines of {withLines} texts; the other {entries.Count - withLines}: {rest}"
        : rest;
    var made = entries.ToDictionary(e => e.Key, e => e.En ?? baseTexts?.GetValueOrDefault(e.Key));

    var fresh = now.Keys.Where(k => !keys.Contains(k)).ToList();
    var gone = entries.Where(e => !now.ContainsKey(e.Key)).Select(e => e.Key).ToList();
    var changed = entries.Where(e => now.TryGetValue(e.Key, out var text) && made[e.Key] is { } was && was != text).Select(e => e.Key).ToList();
    Console.WriteLine($"{Path.GetFileName(file)} ({entries.Count} texts) against the English {(eng == null ? "now" : eng)} ({now.Count} texts)");
    Console.WriteLine($"made from: {basis}");
    Console.WriteLine($"{fresh.Count} new, {changed.Count} changed, {gone.Count} no longer in English{(todos > 0 ? $", {todos} marked TODO" : "")}");
    foreach (var key in fresh)
        Console.WriteLine($"NEW     {Short(key)} {Quoted(now[key])}");
    foreach (var key in changed)
        Console.WriteLine($"CHANGED {Short(key)}\n  {WordDiff(made[key] ?? "", now[key])}");
    foreach (var key in gone)
        Console.WriteLine($"GONE    {Short(key)}");
    return fresh.Count + changed.Count + gone.Count + todos > 0 ? 1 : 0;
}

// The release tags, oldest first: vX.Y.Z only (not v0.1.0-before-retag, not mvp).
List<string> Releases() => [.. (Git("tag", "--list", "v*") ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
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
    return document.RootElement.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String)
        .ToDictionary(p => p.Name, p => p.Value.GetString() ?? "");
}

// A translation file's texts in order, each with the English its `// en:` line names (if any), and its TODO marks.
static (List<Entry> Entries, int Todos) ReadTranslation(string path)
{
    var bytes = Encoding.UTF8.GetBytes(File.ReadAllText(path)); // UTF-8 with or without a BOM, or UTF-16, as the mod reads it
    var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Allow, AllowTrailingCommas = true });
    var (entries, todos, depth) = (new List<Entry>(), 0, 0);
    string? en = null, key = null;
    while (reader.Read())
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.StartObject or JsonTokenType.StartArray:
                depth++;
                break;
            case JsonTokenType.EndObject or JsonTokenType.EndArray:
                depth--;
                break;
            case JsonTokenType.Comment:
                var comment = reader.GetComment().Trim();
                if (comment.StartsWith("en:"))
                    en = Literal(comment[3..].Trim());
                else if (comment.StartsWith("TODO"))
                    todos++;
                break;
            case JsonTokenType.PropertyName when depth == 1:
                key = reader.GetString();
                break;
            case JsonTokenType.String when depth == 1 && key != null:
                entries.Add(new Entry(key, reader.GetString() ?? "", en));
                (key, en) = (null, null);
                break;
        }
    }
    return (entries, todos);
}

// A JSON string literal ("..." with its escapes), or null if it isn't one.
static string? Literal(string text)
{
    try
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.ValueKind == JsonValueKind.String ? document.RootElement.GetString() : null;
    }
    catch (JsonException)
    {
        return null;
    }
}

// Text by text from one English to another: new, removed, changed (old and new), in the newer one's order.
static List<(string Kind, string Key, string? Was, string? Text)> Compare(Dictionary<string, string> old, Dictionary<string, string> now) =>
[
    .. now.Where(p => !old.ContainsKey(p.Key)).Select(p => ("new", p.Key, (string?)null, (string?)p.Value)),
    .. now.Where(p => old.TryGetValue(p.Key, out var was) && was != p.Value).Select(p => ("changed", p.Key, (string?)old[p.Key], (string?)p.Value)),
    .. old.Where(p => !now.ContainsKey(p.Key)).Select(p => ("removed", p.Key, (string?)p.Value, (string?)null)),
];

static string Summary(List<(string Kind, string Key, string? Was, string? Text)> diff) => diff.Count == 0 ? "no change"
    : $"{diff.Count(d => d.Kind == "new")} new, {diff.Count(d => d.Kind == "removed")} removed, {diff.Count(d => d.Kind == "changed")} changed";

// The words that went in [-...-] and the words that came in {+...+}, as git's --word-diff writes them.
static string WordDiff(string was, string now)
{
    var (a, b) = (Regex.Split(was, @"(\s+)"), Regex.Split(now, @"(\s+)"));
    var common = new int[a.Length + 1, b.Length + 1];
    for (var i = a.Length - 1; i >= 0; i--)
        for (var j = b.Length - 1; j >= 0; j--)
            common[i, j] = a[i] == b[j] ? common[i + 1, j + 1] + 1 : Math.Max(common[i + 1, j], common[i, j + 1]);
    // Each piece: kept (0), gone (-1) or come (+1).
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
    // A kept run of at most one word between two changes joins them ("turned on or off" -> "enabled or disabled"
    // reads as one change, not three).
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
    var (text, gone, came) = (new StringBuilder(), new StringBuilder(), new StringBuilder());
    void Flush()
    {
        if (gone.Length > 0)
            text.Append("[-").Append(gone).Append("-]");
        if (came.Length > 0)
            text.Append("{+").Append(came).Append("+}");
        (gone.Length, came.Length) = (0, 0);
    }
    foreach (var (side, piece) in pieces)
    {
        if (side == 0)
        {
            Flush();
            text.Append(piece);
        }
        else
            (side < 0 ? gone : came).Append(piece);
    }
    Flush();
    return text.ToString();
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

static string Cell(string text) => text.Replace("|", "\\|").Replace("\n", "<br>");

// git in the repository, its output, or null if it failed.
string? Git(params string[] arguments)
{
    var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 };
    start.ArgumentList.Add("-C");
    start.ArgumentList.Add(root);
    foreach (var argument in arguments)
        start.ArgumentList.Add(argument);
    using var git = Process.Start(start) ?? throw new Problem("git didn't start (is it installed and on the PATH?)");
    var output = git.StandardOutput.ReadToEnd();
    git.WaitForExit();
    return git.ExitCode == 0 ? output : null;
}

static string ScriptDir([CallerFilePath] string path = "") => Path.GetDirectoryName(path) ?? ".";

record Entry(string Key, string Text, string? En);

// A refusal: its message says what's wrong and what to do.
class Problem(string message) : Exception(message);
