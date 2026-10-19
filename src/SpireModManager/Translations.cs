using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Godot;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;
using SmartFormat;
using SmartFormat.Core.Formatting;
using SmartFormat.Core.Parsing;
using SmartFormat.Extensions;

namespace SpireModManager;

// Drop-in translations: <language>.json (the game's code, e.g. zhs.json) in our data folder's "translations" folder,
// with the same "key": "text" pairs as localization/eng/settings_ui.json. Merged into the game's settings_ui table
// whenever it loads a language, after our packed translation, so the file wins key by key; bad files and keys are
// logged and skipped. Not the game's localization_override folder: packed translations win over it, and a bad entry
// there brings up the game's error popup.
static class Translations
{
    const string Table = "settings_ui";
    const long MaxBytes = 1024 * 1024; // the whole English file is about 15 KB

    public static string Folder => Path.Combine(Store.Dir, "translations");

#if DEVTOOLS
    // Self-test: Open calls it with the folder instead of opening a window.
    public static Action<string>? TestOpen;
#endif

    // An example file in the folder: our English texts under a few lines on how to translate them. Its name is no
    // language's, so it's never read. Rewritten for each version unless the player has edited it (dated ExampleTime).
    public const string ExampleFile = "example-eng.json";
    public static readonly DateTime ExampleTime = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // Its lines end in CRLF, as Notepad writes them, the English table's included.
    public static string ExampleText() => string.Join("\r\n", [
        "// An example: every text of Spire Mod Manager, in English. This file is never loaded.",
        "// To translate: copy it, name the copy with your language's code from the list below (zhs.json for Simplified",
        "// Chinese), and translate the texts on the right. Keep the keys on the left, {placeholders} and [tags] as they are.",
        "// Plural forms ({count:plural:mod|mods}) don't work here; a text with one shows in English. Word it to fit any number.",
        "// A text left out shows in English. Then switch the game to that language (or relaunch it, if it's in it already).",
        "// This example is updated with the mod until you edit it.",
        .. CodeLines(),
        Godot.FileAccess.GetFileAsString($"res://{ModEntry.ModId}/localization/eng/{Table}.json").Trim().ReplaceLineEndings("\r\n")]) + "\r\n";

    // The game's languages by code (LocManager's own table), named for the example.
    static readonly Dictionary<string, string> LanguageNames = new()
    {
        ["zhs"] = "Chinese (Simplified)", ["zht"] = "Chinese (Traditional)", ["deu"] = "German", ["esp"] = "Spanish (Latin America)",
        ["fra"] = "French", ["ind"] = "Indonesian", ["ita"] = "Italian", ["jpn"] = "Japanese", ["kor"] = "Korean", ["pol"] = "Polish",
        ["ptb"] = "Portuguese (Brazil)", ["rus"] = "Russian", ["spa"] = "Spanish (Spain)", ["tha"] = "Thai", ["tur"] = "Turkish",
    };

    // "// Codes: deu German, ..." for the languages this game has (stable has fewer than beta), wrapped.
    public static IEnumerable<string> CodeLines()
    {
        var codes = LocManager.Languages.Where(code => code != "eng").Order()
            .Select(code => LanguageNames.TryGetValue(code, out var name) ? $"{code} {name}" : code).ToList();
        var line = "// Codes:";
        for (var i = 0; i < codes.Count; i++)
        {
            var part = $" {codes[i]}{(i == codes.Count - 1 ? "." : ",")}";
            if (line.Length + part.Length > 118)
            {
                yield return line;
                line = "//";
            }
            line += part;
        }
        yield return line;
    }

    // The example written, unless the player has edited it. Fails soft: a folder with no example is no worse than before.
    public static void WriteExample(string folder)
    {
        try
        {
            var file = Path.Combine(folder, ExampleFile);
            if (File.Exists(file) && File.GetLastWriteTimeUtc(file) != ExampleTime)
                return; // edited: theirs now
            Store.WriteAtomic(file, ExampleText(), ExampleTime); // whole and dated as ours, or not at all
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.ModId}] Could not write the example translation: {e.Message}");
        }
    }

    // The main menu button's "Open translations folder": creates it with the example if needed, then opens it.
    public static void Open()
    {
        try
        {
            var folder = Folder;
            Directory.CreateDirectory(folder);
            WriteExample(folder);
#if DEVTOOLS
            if (TestOpen is { } test)
            {
                test(folder);
                return;
            }
#endif
            OS.ShellOpen(folder);
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.ModId}] Could not open the translations folder: {e}");
        }
    }

    // Merges the drop-in file's texts for a language the game has just loaded, if there's a file.
    public static void Apply(string language, Dictionary<string, LocTable> tables)
    {
        if (language == "eng" || !tables.TryGetValue(Table, out var table) || !Directory.Exists(Folder))
            return;
        // <language>.json, or <language>.json.txt: what a browser's "Save as" can make of it, with Windows hiding the .txt.
        var file = new[] { ".json", ".json.txt" }.Select(end => Path.Combine(Folder, language + end)).FirstOrDefault(File.Exists);
        if (file == null)
        {
            // None for this language: what is there, so a misnamed file (deu.txt, a DEU folder, eng.json) shows in the log.
            List<string> there = [.. Directory.EnumerateFileSystemEntries(Folder).Select(Path.GetFileName).OfType<string>().Where(n => n != ExampleFile)];
            if (there.Count > 0)
                Log.Info($"[{ModEntry.ModId}] No translation {language}.json in {Folder}, which has: {Some(there)}");
            return;
        }
        if (Read(file) is not { } texts)
            return;
        table.MergeWith(texts);
        Log.Info($"[{ModEntry.ModId}] Translation {file}: {texts.Count} texts");
    }

    // A drop-in file's texts, only those for keys English has; null if the file can't be used at all.
    static Dictionary<string, string>? Read(string file)
    {
        try
        {
            if (new FileInfo(file).Length > MaxBytes)
            {
                Log.Warn($"[{ModEntry.ModId}] Translation {file} is over 1 MB, so it's left out");
                return null;
            }
            // ReadAllText: UTF-8 with or without a byte order mark, or UTF-16 with one. Comments and trailing commas are
            // forgiven, as a hand-edited file may have them.
            using var json = JsonDocument.Parse(File.ReadAllText(file),
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (json.RootElement.ValueKind != JsonValueKind.Object)
            {
                Log.Warn($"[{ModEntry.ModId}] Translation {file} isn't a list of \"key\": \"text\" pairs, so it's left out");
                return null;
            }
            var english = English();
            var texts = new Dictionary<string, string>();
            List<string> unknown = [], unusable = [];
            foreach (var entry in json.RootElement.EnumerateObject())
            {
                if (!english.TryGetValue(entry.Name, out var names))
                    unknown.Add(entry.Name);
                // Not text, empty, placeholders English doesn't have, or what the game can't format (a plural, a misspelled
                // formatter): English shows instead (the game would show it raw, and log and report an error on every draw).
                else if (entry.Value.ValueKind != JsonValueKind.String || entry.Value.GetString() is not { } text || text.Trim().Length == 0
                    || Placeholders(text) is not { } used || !used.IsSubsetOf(names) || !Formats(text, used))
                    unusable.Add(entry.Name);
                else
                    texts[entry.Name] = text;
            }
            if (unknown.Count > 0)
                Log.Warn($"[{ModEntry.ModId}] Translation {file}: {unknown.Count} keys English doesn't have, left out: {Some(unknown)}");
            if (unusable.Count > 0)
                Log.Warn($"[{ModEntry.ModId}] Translation {file}: {unusable.Count} texts empty or unreadable (a broken or renamed {{placeholder}}, or a plural form?), shown in English: {Some(unusable)}");
            return texts;
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.ModId}] Translation {file} couldn't be read, so it's left out: {e.Message}");
            return null;
        }
    }

    static string Some(List<string> names) => string.Join(", ", names.Take(10)) + (names.Count > 10 ? ", ..." : "");

    static Dictionary<string, HashSet<string>>? _english;

    // Our English keys with their placeholder names: what a drop-in file may translate and use.
    static Dictionary<string, HashSet<string>> English()
    {
        if (_english != null)
            return _english;
        var text = Godot.FileAccess.GetFileAsString($"res://{ModEntry.ModId}/localization/eng/{Table}.json");
        using var json = JsonDocument.Parse(text.Length > 0 ? text : "{}");
        return _english = json.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => Placeholders(p.Value.GetString() ?? ""));
    }

    // The names a text's placeholders use: "count" in "{count}" and in "{count:plural:mod|mods}".
    static HashSet<string> Placeholders(string text) =>
        Regex.Matches(text, @"\{([A-Za-z_]\w*)").Select(m => m.Groups[1].Value).ToHashSet();

    // Whether the game can format the text: its own check for override files, then the text formatted as the game does,
    // its placeholders filled with text as ours always are (Menus.Format), so a plural fails here as it would on screen.
    // All pass if either check is gone or changed in a game update.
    static bool Formats(string text, HashSet<string> names)
    {
        try
        {
            return GameFormats(text) && TrialFormats(text, names);
        }
        catch (Exception)
        {
            return true;
        }
    }

    // On its own, so a game update without LocValidator fails in Formats' try, not where that is compiled.
    [MethodImpl(MethodImplOptions.NoInlining)]
    static bool GameFormats(string text) => LocValidator.ValidateFormatString(text, out _);

    static SmartFormatter? _formatter;

    // The extensions of the game's formatter (LocManager.LoadLocFormatters) a text of ours can use; LocManager's own
    // isn't made yet when the first language loads.
    static SmartFormatter Formatter()
    {
        if (_formatter != null)
            return _formatter;
        var formatter = new SmartFormatter();
        formatter.AddExtensions(new DictionarySource(), new DefaultSource());
        formatter.AddExtensions(new PluralLocalizationFormatter(), new ConditionalFormatter(), new ChooseFormatter(), new DefaultFormatter());
        return _formatter = formatter;
    }

    // The game's formatter takes a plural only of a number: ours pass text, so "{count:plural:Mod|Mods}" fails, as does
    // a misspelled "{count:plurl:…}". Only those failures count; anything else (a SmartFormat change) passes.
    [MethodImpl(MethodImplOptions.NoInlining)]
    static bool TrialFormats(string text, HashSet<string> names)
    {
        try
        {
            Formatter().Format(CultureInfo.InvariantCulture, text, names.ToDictionary(name => name, name => (object)"x"));
            return true;
        }
        catch (FormattingException)
        {
            return false;
        }
        catch (ParsingErrors)
        {
            return false;
        }
    }
}
