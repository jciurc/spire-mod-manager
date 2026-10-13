using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Saves;

namespace SpireModManager;

// Everything the manager remembers, saved as one JSON file per Steam account:
// user://steam/<id>/mod_data/SpireModManager/manager.json
//
// Categories, aliases and notes belong to a mod id, so a Workshop copy and a local copy of the same
// mod share them. Profiles name a source too, like the game's own mod list (see Profiles).
public class StoreData
{
    public int Version { get; set; } = 1;
    public List<Category> Categories { get; set; } = [];
    public Dictionary<string, ModInfo> Mods { get; set; } = [];
    public List<Profile> Profiles { get; set; } = [];
    public string? ActiveProfile { get; set; }
    public bool VanillaView { get; set; }
    public bool LoadOrderView { get; set; }
    public bool UncategorizedCollapsed { get; set; }
    // Where the player moved the main menu button (top-left, in the game's 1920x1080 frame); null: the corner.
    public ScreenPoint? MenuButtonAt { get; set; }
    public string? ImportedFrom { get; set; }
}

public class Category
{
    public string Name { get; set; } = "";
    public bool Collapsed { get; set; }
}

public class ModInfo
{
    public string? Category { get; set; }
    public string? Alias { get; set; }
    public string? Notes { get; set; }
    public List<string>? Tags { get; set; }
}

public class Profile
{
    public string Name { get; set; } = "";
    public List<ProfileMod> Mods { get; set; } = [];
    // The active profile's unsaved changes (the mod list as it is now); null when there are none.
    public List<ProfileMod>? Edited { get; set; }
    public DateTime? EditedAt { get; set; }
    // When it was created or last saved (none for profiles from before this was kept).
    public DateTime? SavedAt { get; set; }
    // Id-only list of disabled mods as imported from Better Mod Menu; Profiles.Prepare converts it to Mods.
    public List<ModRef>? Disabled { get; set; }
}

public record ProfileMod(string Id, ModSource Source, bool Enabled);

public record ScreenPoint(float X, float Y);

public record ModRef(string Id, ModSource? Source);

public static class Store
{
    const string FileName = "manager.json";
    const int BackupsKept = 10;

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    static StoreData? _data;
    static bool _readOnly;
    static bool _backedUpThisSession;

    // Loaded on first use rather than in Init: the account folder needs Steam's user id.
    public static StoreData Data => _data ??= Load();

    static string Dir => ProjectSettings.GlobalizePath(UserDataPathProvider.GetAccountScopedBasePath($"mod_data/{ModEntry.ModId}"));
    static string FilePath => Path.Combine(Dir, FileName);

    static StoreData Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<StoreData>(File.ReadAllText(FilePath), Json) ?? new StoreData();

            var data = BetterModMenuImport.TryImport() ?? new StoreData();
            if (data.ImportedFrom != null)
                Write(data);
            return data;
        }
        catch (Exception e)
        {
            // Never overwrite a file we couldn't read: keep working in memory and leave it for the user.
            Log.Error($"[{ModEntry.ModId}] Could not load {FilePath}, changes this session won't be saved: {e}");
            _readOnly = true;
            return new StoreData();
        }
    }

    public static void Save()
    {
        if (_batching)
            return; // Batch saves once at the end
        if (_data != null && !_readOnly)
            Write(_data);
        History.Record();
    }

    static bool _batching;

    // Several edits as one: one save, one undo step (editing several mods at once).
    public static void Batch(Action edits)
    {
        if (_batching)
        {
            edits();
            return;
        }
        _batching = true;
        try
        {
            edits();
        }
        finally
        {
            _batching = false;
        }
        Save();
    }

    // Our data minus view state (views, collapsed categories): what undo compares and restores.
    public static string EditsJson()
    {
        var copy = JsonSerializer.Deserialize<StoreData>(JsonSerializer.Serialize(Data, Json), Json) ?? new StoreData();
        copy.VanillaView = copy.LoadOrderView = copy.UncategorizedCollapsed = false;
        foreach (var category in copy.Categories)
            category.Collapsed = false;
        return JsonSerializer.Serialize(copy, Json);
    }

    // Puts saved edits back, keeping the current view state.
    public static void RestoreEdits(string json)
    {
        var restored = JsonSerializer.Deserialize<StoreData>(json, Json) ?? new StoreData();
        var now = Data;
        (restored.VanillaView, restored.LoadOrderView, restored.UncategorizedCollapsed) = (now.VanillaView, now.LoadOrderView, now.UncategorizedCollapsed);
        foreach (var category in restored.Categories)
            category.Collapsed = now.Categories.FirstOrDefault(c => c.Name == category.Name)?.Collapsed ?? false;
        _data = restored;
        Save();
    }

    // Writes to a temp file and swaps it in, so a crash mid-write can't leave a half-written file.
    // The first save of each session first copies the previous file into backups/.
    static void Write(StoreData data)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            if (!_backedUpThisSession && File.Exists(FilePath))
            {
                var backups = Path.Combine(Dir, "backups");
                Directory.CreateDirectory(backups);
                File.Copy(FilePath, Path.Combine(backups, $"manager.{DateTime.Now:yyyyMMdd-HHmmss}.json"), overwrite: true);
                foreach (var old in Directory.GetFiles(backups, "manager.*.json").OrderDescending().Skip(BackupsKept))
                    File.Delete(old);
            }
            _backedUpThisSession = true;

            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(data, Json));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.ModId}] Could not save {FilePath}: {e}");
        }
    }

    public static ModInfo? InfoFor(string? modId) => modId != null && Data.Mods.TryGetValue(modId, out var info) ? info : null;

    // Changes one mod's details and saves. A mod left with nothing of ours (no category, alias, notes or tags)
    // loses its entry, so clearing everything reads as "unchanged", not as an edit.
    public static void EditMod(string modId, Action<ModInfo> change)
    {
        if (!Data.Mods.TryGetValue(modId, out var info))
            Data.Mods[modId] = info = new ModInfo();
        change(info);
        if (info is { Category: null, Alias: null, Notes: null, Tags: null })
            Data.Mods.Remove(modId);
        Save();
    }
}

// One-time import of Better Mod Menu's saved data (read-only; its file is never changed). It can be saved in
// several ways, so we take the file it would load itself and read it the way it does.
static class BetterModMenuImport
{
    const string BmmId = "BetterModMenu";

    // Its file names, in its order of preference within a folder.
    static readonly string[] FileNames = ["mod_profiles.json5", "mod_profiles.jsonc", "mod_profiles.json"];

    // It allows comments and trailing commas in its file, whatever the name. That is all ".json5" means to it:
    // other JSON5 syntax (unquoted names, single quotes) fails there as it does here.
    static readonly JsonDocumentOptions Lenient = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static StoreData? TryImport()
    {
        var path = FindFile(Folders());
        if (path == null)
            return null;
        try
        {
            var data = Parse(File.ReadAllText(path));
            data.ImportedFrom = path;
            Log.Info($"[{ModEntry.ModId}] Imported from {path}: {data.Categories.Count} categories, {data.Mods.Count} mods, {data.Profiles.Count} profiles");
            return data;
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.ModId}] Could not import {path}: {e}");
            return null;
        }
    }

    // Our data from the text of its file. Throws when the text is in neither of its formats.
    public static StoreData Parse(string text)
    {
        using var doc = JsonDocument.Parse(text, Lenient);
        var root = doc.RootElement;
        var data = new StoreData();

        // Its old format: the file is just the list of profiles, with no groups or notes.
        if (root.ValueKind == JsonValueKind.Array)
        {
            AddProfiles(data, root);
            return data;
        }

        var collapsed = Strings(root, "CollapsedGroups").ToHashSet();
        foreach (var name in Strings(root, "CustomGroups"))
            data.Categories.Add(new Category { Name = name, Collapsed = collapsed.Contains(name) });

        if (root.TryGetProperty("ModGroups", out var groups) && groups.ValueKind == JsonValueKind.Object)
            foreach (var entry in groups.EnumerateObject())
                if (entry.Value.ValueKind == JsonValueKind.String)
                    Info(data, entry.Name).Category = entry.Value.GetString();

        if (root.TryGetProperty("ModAnnotations", out var notes) && notes.ValueKind == JsonValueKind.Object)
            foreach (var entry in notes.EnumerateObject())
            {
                var info = Info(data, entry.Name);
                info.Alias = NonEmpty(entry.Value, "Alias");
                info.Notes = NonEmpty(entry.Value, "Notes");
            }

        if (root.TryGetProperty("Profiles", out var profiles) && profiles.ValueKind == JsonValueKind.Array)
            AddProfiles(data, profiles);

        if (root.TryGetProperty("CurrentProfileIndex", out var index) && index.TryGetInt32(out var i) && i >= 0 && i < data.Profiles.Count)
            data.ActiveProfile = data.Profiles[i].Name;

        return data;
    }

    static void AddProfiles(StoreData data, JsonElement profiles)
    {
        foreach (var p in profiles.EnumerateArray())
            data.Profiles.Add(new Profile
            {
                Name = NonEmpty(p, "Name") ?? $"Profile {data.Profiles.Count + 1}",
                Disabled = Strings(p, "DisabledMods").Select(id => new ModRef(id, null)).ToList(),
            });
    }

    // Where it looks, in its order: beside its own files first ("portable mode"), then its folder in the Steam
    // account's data. Like it, we only look beside its first copy in the mod list.
    static IEnumerable<string> Folders()
    {
        if (ModManager.Mods.FirstOrDefault(m => m.manifest?.id == BmmId)?.path is { Length: > 0 } own)
            yield return own;
        yield return ProjectSettings.GlobalizePath(UserDataPathProvider.GetAccountScopedBasePath($"mod_data/{BmmId}"));
    }

    // The file it would load: the first folder holding any of its files wins, and there the first name in its order.
    public static string? FindFile(IEnumerable<string> folders) =>
        folders.SelectMany(folder => FileNames.Select(name => Path.Combine(folder, name))).FirstOrDefault(File.Exists);

    static ModInfo Info(StoreData data, string id) =>
        data.Mods.TryGetValue(id, out var info) ? info : data.Mods[id] = new ModInfo();

    static IEnumerable<string> Strings(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array
            ? arr.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString() ?? "")
                .Where(s => s.Length > 0)
            : [];

    static string? NonEmpty(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;
}
