using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;

namespace SpireModManager;

// What a mod changes in the game itself, for the info panel's Modified content, its details dialog (ModifiedContent.Show) and
// search (Jordan: a mod that turned the Regent into another character was hard to track down). Two kinds:
// - the game's files its pack replaces: a file at the same path as one in the game's own pack (a skin, an energy orb, a
//   card frame), told by the character it's for and what it is (ReplacedFiles);
// - the game's code its Harmony patches change: the game's methods with a patch from one of its assemblies, told by
//   their class, with how each is patched (Places).
// Packs are read only as far as their directory, once each (by path, size and time); the patches again when they change.
static class GameChanges
{
    // How a mod's patches change a game method (Harmony's prefix, postfix, transpiler and finalizer).
    [Flags]
    public enum Kinds { Before = 1, After = 2, Rewritten = 4, Wrapped = 8 }

    // A game method a mod patches: its class and name as the code has them ("NMerchantRoom", "_Ready"), its area in
    // words ("merchant room") and how.
    public record Place(string Class, string Method, string Area, Kinds Kinds);

    // Everything found, kept so the dialog shows it without working it out again: each file with its area, and each
    // place, both most first by area.
    public record Changes(IReadOnlyList<(string Path, string Area)> ReplacedFiles, IReadOnlyList<Place> Places)
    {
        public int Files => ReplacedFiles.Count;
        public int Code => Places.Count;
        public IReadOnlyList<string> FileSubjects { get; } = [.. ReplacedFiles.Select(f => f.Area).Distinct()];
        public IReadOnlyList<string> CodeSubjects { get; } = [.. Places.Select(p => p.Area).Distinct()];
        // Every area, the files' first (each once): what Modified content lists.
        public IReadOnlyList<string> Areas { get; } = [.. ReplacedFiles.Select(f => f.Area).Concat(Places.Select(p => p.Area)).Distinct()];
        // Their words, for search (Search: matched at the start of a word): "regent" finds the mods that change the Regent.
        public string Words => string.Join(' ', FileSubjects.Concat(CodeSubjects));
        public bool Any => Files > 0 || Code > 0;
    }

    static readonly Changes None = new([], []);
    static readonly HashSet<string> Warned = []; // once a mod each

    // Search's words for a mod's changes, kept for the session and made again when the language changes: a keystroke
    // searches every mod, and working them out each time (its pack against the game's, its patches) made one take
    // 172 ms with 249 mods (29 ms before). The patches are the launch's; an updated mod arrives as a new Mod.
    static readonly ConditionalWeakTable<Mod, StrongBox<(string Language, string Words)>> SearchWords = new();

    public static string Words(Mod mod)
    {
        var language = LocManager.Instance?.Language ?? "";
        var kept = SearchWords.GetValue(mod, m => new StrongBox<(string, string)>((language, For(m).Words)));
        if (kept.Value.Language != language)
            kept.Value = (language, For(mod).Words);
        return kept.Value.Words;
    }

    // A mod's changes, or None (nothing found, or what it ships can't be read).
    public static Changes For(Mod? mod)
    {
        if (mod?.manifest is null) // not ==: the beta's ModManifest has an == operator, stable's doesn't
            return None;
        try
        {
            var files = ReplacedFiles(mod);
            var places = PatchedCode(mod);
            return files.Count == 0 && places.Count == 0 ? None : new Changes(FileAreas(files, Characters()), places);
        }
        catch (Exception e)
        {
            if (Warned.Add(mod.manifest.id ?? ""))
                Log.Warn($"[{ModEntry.ModId}] Could not tell what {mod.manifest.id} changes in the game: {e.Message}");
            return None;
        }
    }

    // Whether its pack replaces any of the game's art or sound (an image, a sound, a video, a font: Media), the Cosmetic
    // tag's evidence beside its Workshop tags (Jordan, 25 Oct: a character reskin whose tags didn't say so). Definitive,
    // where code changes to visuals are only inferred, so those don't count. Kept for the session per mod: every row asks
    // at each redraw.
    public static bool ReplacesArt(Mod mod) => Art.GetValue(mod, m => new StrongBox<bool>(ReadsAsArt(m))).Value;

    static readonly ConditionalWeakTable<Mod, StrongBox<bool>> Art = new();
    static readonly string[] Media = [".png", ".jpg", ".jpeg", ".webp", ".svg", ".ogg", ".wav", ".mp3", ".ogv", ".ttf", ".otf"];

    // A game file that is art or sound, by its extension ("images/ui/top_panel/x.png"; not a scene or a script).
    public static bool IsArt(string path) => Media.Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

    static bool ReadsAsArt(Mod mod)
    {
        try
        {
            return ReplacedFiles(mod).Any(IsArt);
        }
        catch (Exception)
        {
            return false; // what it ships can't be read: For warns once, when its panel or search asks
        }
    }

    // ---- The game's files

    // The game's own pack beside its executable, read once; empty if it's not there (a pack inside the executable).
    static HashSet<string>? _gamePaths;
    static HashSet<string> GamePaths() => _gamePaths ??= Path.Combine(Path.GetDirectoryName(OS.GetExecutablePath()) ?? "", "SlayTheSpire2.pck") is var pack
        && File.Exists(pack) ? [.. PackPaths(pack)] : [];

    // The assets a mod's packs have at the game's own paths ("images/ui/hands/hand_regent.png", from its .import).
    static List<string> ReplacedFiles(Mod mod)
    {
        if (string.IsNullOrEmpty(mod.path) || !Directory.Exists(mod.path) || GamePaths() is not { Count: > 0 } game)
            return [];
        return [.. Directory.EnumerateFiles(mod.path, "*.pck").SelectMany(PackPaths).Where(game.Contains).Select(Asset).OfType<string>().Distinct()];
    }

    // The asset a pack entry stands for: "x.png" for "x.png.import" (an exported game ships the import, not the file);
    // none for the engine's own files (.godot/, the uid cache).
    public static string? Asset(string entry) =>
        entry.StartsWith(".godot/") || entry.EndsWith(".uid") || entry is "project.binary" ? null
        : entry.EndsWith(".import") ? entry[..^".import".Length] : entry.EndsWith(".remap") ? entry[..^".remap".Length] : entry;

    // A pack's paths (format v3, as tools/pck.cs reads it), kept by path, size and time.
    static readonly ConcurrentDictionary<(string, long, DateTime), string[]> Packs = new();

    public static string[] PackPaths(string pack)
    {
        var info = new FileInfo(pack);
        return Packs.GetOrAdd((info.FullName, info.Length, info.LastWriteTimeUtc), _ => ReadPaths(pack));
    }

    static string[] ReadPaths(string pack)
    {
        using var r = new BinaryReader(File.OpenRead(pack));
        if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "GDPC" || r.ReadInt32() != 3)
            return [];
        r.ReadBytes(12); // engine version
        if ((r.ReadInt32() & 1) != 0)
            return []; // an encrypted directory
        r.ReadInt64(); // where the files start
        r.BaseStream.Position = r.ReadInt64();
        var count = r.ReadInt32();
        var paths = new string[count];
        for (var i = 0; i < count; i++)
        {
            paths[i] = Encoding.UTF8.GetString(r.ReadBytes(r.ReadInt32())).TrimEnd('\0');
            r.ReadBytes(8 + 8 + 16 + 4); // offset, size, MD5, flags
        }
        return paths;
    }

    // The game's characters: the id their files use ("regent") and their name in the game's language ("The Regent"),
    // made again when the language changes.
    static (string Language, (string Id, string Name)[] List)? _characters;

    static (string Id, string Name)[] Characters()
    {
        var language = LocManager.Instance?.Language ?? "";
        if (_characters is not { } known || known.Language != language)
            _characters = known = (language, [.. ModelDb.AllCharacters.Select(c => (c.Id.Entry.ToLowerInvariant(), c.Title.GetFormattedText()))]);
        return known.List;
    }

    // What the files are, most first: each character's ("The Regent (character select, energy orb)"), then the rest by
    // what they are ("hands", "top panel", "game text").
    public static List<string> FileSubjects(IEnumerable<string> assets, IReadOnlyList<(string Id, string Name)> characters) =>
        [.. FileAreas(assets, characters).Select(f => f.Area).Distinct()];

    // Each file with the area it's in (FileSubjects' words), in their order, each area's files by path.
    static List<(string Path, string Area)> FileAreas(IEnumerable<string> assets, IReadOnlyList<(string Id, string Name)> characters)
    {
        var told = new List<(string Path, string? Character, string What)>();
        foreach (var asset in assets)
        {
            var folders = asset.Split('/')[..^1];
            var words = asset.ToLowerInvariant().Split('/', '_', '.', '-');
            if (asset.StartsWith("localization/"))
            {
                told.Add((asset, null, "game text"));
                continue;
            }
            var character = characters.FirstOrDefault(c => words.Contains(c.Id));
            if (character.Id == null)
            {
                told.Add((asset, null, What(folders.LastOrDefault() ?? asset)));
                continue;
            }
            // Its folder (".../energy_counters/regent/orb.png") or the file named for it (".../card_frames/regent_icon.png").
            var at = Array.FindIndex(folders, f => f.Equals(character.Id, StringComparison.OrdinalIgnoreCase));
            told.Add((asset, character.Name, What(at > 0 ? folders[at - 1] : folders.LastOrDefault() ?? asset)));
        }
        static IEnumerable<string> Most(IEnumerable<string> things) => things.GroupBy(t => t).OrderByDescending(g => g.Count()).Select(g => g.Key);
        static IEnumerable<(string, string)> ByPath(IEnumerable<string> paths, string area) => paths.Order(StringComparer.Ordinal).Select(p => (p, area));
        var characterFiles = told.Where(f => f.Character != null).GroupBy(f => f.Character).OrderByDescending(g => g.Count())
            .SelectMany(g => ByPath(g.Select(f => f.Path), $"{g.Key} ({string.Join(", ", Most(g.Select(f => f.What)))})"));
        var rest = told.Where(f => f.Character == null).GroupBy(f => f.What).OrderByDescending(g => g.Count())
            .SelectMany(g => ByPath(g.Select(f => f.Path), g.Key));
        return [.. characterFiles, .. rest];
    }

    // A folder as words: the game's few with a plainer name, else its name spaced ("top_panel": "top panel").
    static string What(string folder)
    {
        var name = folder.Split('.')[0].ToLowerInvariant();
        return name switch
        {
            "characters" => "character model",
            "energy_counters" => "energy orb",
            "card_portraits" or "card_atlas" => "card art",
            _ => name.Replace('_', ' '),
        };
    }

    // ---- The game's code

    // Each loaded mod's assemblies' patches on the game's methods, and how, worked out at most once a frame: a page's
    // mods share one look, and a patch added or taken off since (on a method already patched too) is in the next.
    static (ulong Frame, Dictionary<Assembly, Dictionary<MethodBase, Kinds>> ByAssembly)? _patches;

#if DEVTOOLS
    // Self-test: a made-up mod's assemblies, for its code (null: the game's own, as for every other mod).
    public static Func<Mod, IEnumerable<Assembly>?>? TestAssemblies { get; set; }
#endif

    // The game's methods the mod's patches change, most first by area, each area's by class and name.
    static List<Place> PatchedCode(Mod mod)
    {
        var frame = Engine.GetProcessFrames();
        if (_patches is not { } known || known.Frame != frame)
        {
            var patched = Harmony.GetAllPatchedMethods().ToList();
            var game = typeof(ModelDb).Assembly;
            var byAssembly = new Dictionary<Assembly, Dictionary<MethodBase, Kinds>>();
            foreach (var method in patched.Where(m => m.DeclaringType?.Assembly == game))
                if (Harmony.GetPatchInfo(method) is { } info)
                    foreach (var (patches, kind) in new[] { (info.Prefixes, Kinds.Before), (info.Postfixes, Kinds.After), (info.Transpilers, Kinds.Rewritten), (info.Finalizers, Kinds.Wrapped) })
                        foreach (var owner in patches.Select(p => p.PatchMethod?.DeclaringType?.Assembly).OfType<Assembly>().Distinct())
                        {
                            if (!byAssembly.TryGetValue(owner, out var methods))
                                byAssembly[owner] = methods = [];
                            methods[method] = methods.GetValueOrDefault(method) | kind;
                        }
            _patches = known = (frame, byAssembly);
        }
        var assemblies = Compat.Assemblies(mod);
#if DEVTOOLS
        assemblies = TestAssemblies?.Invoke(mod) ?? assemblies;
#endif
        // A method two of its assemblies patch is one place, patched both ways.
        var found = new Dictionary<MethodBase, Kinds>();
        foreach (var (method, how) in assemblies.SelectMany(a => known.ByAssembly.GetValueOrDefault(a) ?? []))
            found[method] = found.GetValueOrDefault(method) | how;
        return [.. found.Select(p => PlaceOf(p.Key, p.Value)).GroupBy(p => p.Area).OrderByDescending(g => g.Count())
            .SelectMany(g => g.OrderBy(p => p.Class, StringComparer.Ordinal).ThenBy(p => p.Method, StringComparer.Ordinal))];
    }

    // A game method as its code names it: its class ("NMerchantRoom") and name ("_Ready"). One the compiler made (an
    // async method's "<OnPlay>d__42.MoveNext", a lambda's "<>c.<Setup>b__3_0") is told by the class it's in and the
    // method it's from ("OnPlay", "Setup"); a constructor is "ctor".
    static Place PlaceOf(MethodBase method, Kinds kinds)
    {
        var type = method.DeclaringType;
        var name = method.Name;
        while (type is { DeclaringType: { } outer } && (type.Name.StartsWith('<') || type.IsDefined(typeof(CompilerGeneratedAttribute), false)))
        {
            name = From(type.Name) ?? name;
            type = outer;
        }
        var typeName = type?.Name ?? "";
        if (typeName.IndexOf('`') is var tick and >= 0)
            typeName = typeName[..tick];
        return new Place(typeName, (From(name) ?? name).TrimStart('.'), Class(method.DeclaringType), kinds);
    }

    // The method a compiler-made name is from: "OnPlay" for "<OnPlay>d__42"; null for one that names none ("<>c").
    static string? From(string compiled) => compiled.StartsWith('<') && compiled.IndexOf('>') is var end and > 1 ? compiled[1..end] : null;

    // A game class as words: "NMerchantRoom" is "merchant room", "RelicModel" is "relic", "NCreature" is "creature". One
    // the compiler made (an async method's "<OnPlay>d__42", a lambda's "<>c": a patch on their body lands there) is told
    // by the class it's in.
    public static string Class(Type? type)
    {
        while (type is { DeclaringType: { } outer } && (type.Name.StartsWith('<') || type.IsDefined(typeof(CompilerGeneratedAttribute), false)))
            type = outer;
        var name = type?.Name ?? "";
        if (name.IndexOf('`') is var tick and >= 0)
            name = name[..tick];
        if (name.Length > 1 && name[0] == 'N' && char.IsUpper(name[1]))
            name = name[1..];
        if (name.EndsWith("Model") && name.Length > "Model".Length)
            name = name[..^"Model".Length];
        var words = new StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && (char.IsLower(name[i - 1]) || i + 1 < name.Length && char.IsLower(name[i + 1])))
                words.Append(' ');
            words.Append(char.ToLowerInvariant(name[i]));
        }
        return words.ToString();
    }
}
