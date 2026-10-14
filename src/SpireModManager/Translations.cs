using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Godot;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;

namespace SpireModManager;

// Drop-in translations: a player puts <language>.json (the game's three-letter code, e.g. zhs.json) in the
// "translations" folder of our data folder, and our text shows from it, with no new build of the mod. The file holds
// the same flat "key": "text" pairs as localization/eng/settings_ui.json. Its texts go into the game's settings_ui
// table each time the game loads its tables for a language (TranslationsPatch: at startup and on every language
// change), after the game has merged in our packed translation for it. So the file wins over a packed translation,
// which wins over English, key by key: a key the file leaves out falls back as usual.
// Fail soft: a file that's too big, can't be read or isn't JSON is logged and left out, and so are the keys English
// doesn't have and the texts the game couldn't format. English is the source, so there's no eng.json.
static class Translations
{
    const string Table = "settings_ui";
    const long MaxBytes = 1024 * 1024; // the whole English file is about 15 KB

    public static string Folder => Path.Combine(Store.Dir, "translations");

    // Set by the self-test: Open calls it with the folder instead of opening a window.
    public static Action<string>? TestOpen;

    // The main menu button's "Open translations folder": made if it isn't there yet, then shown in the system's file
    // browser.
    public static void Open()
    {
        try
        {
            var folder = Folder;
            Directory.CreateDirectory(folder);
            if (TestOpen is { } test)
                test(folder);
            else
                OS.ShellOpen(folder);
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.ModId}] Could not open the translations folder: {e}");
        }
    }

    // The game has just loaded its tables for a language: the drop-in file's texts for that language go into its
    // settings_ui table, if there is a file.
    public static void Apply(string language, Dictionary<string, LocTable> tables)
    {
        if (language == "eng" || !tables.TryGetValue(Table, out var table) || !Directory.Exists(Folder))
            return;
        // <language>.json, or <language>.json.txt: what a browser's "Save as" can make of it, with Windows hiding the .txt.
        var file = new[] { ".json", ".json.txt" }.Select(end => Path.Combine(Folder, language + end)).FirstOrDefault(File.Exists);
        if (file == null)
        {
            // None for this language: what is there, so a misnamed file (deu.txt, a DEU folder, eng.json) shows in the log.
            List<string> there = [.. Directory.EnumerateFileSystemEntries(Folder).Select(Path.GetFileName).OfType<string>()];
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
                // Not text, empty, a placeholder the game can't read, or one English doesn't fill in for that key (a
                // translated {count}, or one added): English shows. The game would show the text raw and log an error
                // on every draw.
                else if (entry.Value.ValueKind != JsonValueKind.String || entry.Value.GetString() is not { } text || text.Trim().Length == 0
                    || !Formats(text) || !Placeholders(text).IsSubsetOf(names))
                    unusable.Add(entry.Name);
                else
                    texts[entry.Name] = text;
            }
            if (unknown.Count > 0)
                Log.Warn($"[{ModEntry.ModId}] Translation {file}: {unknown.Count} keys English doesn't have, left out: {Some(unknown)}");
            if (unusable.Count > 0)
                Log.Warn($"[{ModEntry.ModId}] Translation {file}: {unusable.Count} texts empty or unreadable (a broken or renamed {{placeholder}}?), shown in English: {Some(unusable)}");
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

    // Our English keys, as packed, each with its placeholders' names: a drop-in file may only translate those keys, and
    // use only those placeholders in each.
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

    // Whether the game can format the text (its placeholders read), checked the way it checks its own override files.
    // If a game update took that check away, every text passes: the game still shows an unreadable one as it is.
    static bool Formats(string text)
    {
        try
        {
            return GameFormats(text);
        }
        catch (Exception)
        {
            return true;
        }
    }

    // On its own, so a game update without LocValidator fails in Formats' try, not where that is compiled.
    [MethodImpl(MethodImplOptions.NoInlining)]
    static bool GameFormats(string text) => LocValidator.ValidateFormatString(text, out _);
}
