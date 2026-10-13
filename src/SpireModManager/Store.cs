using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
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
    const string BackupsFolder = "backups", BackupPattern = "manager.*.json", BrokenPattern = "manager.broken-*.json";
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

    // A launch that found the file unreadable, for the notice on the Modding screen (shown once, then null). The
    // self-test sets it too.
    public static Loaded? Notice { get; set; }

    static StoreData Load()
    {
        try
        {
            var loaded = LoadFrom(Dir, DateTime.Now);
            _readOnly = loaded.ReadOnly;
            if (loaded.Unreadable != null)
                Notice = loaded;
            if (loaded.Data is { } data)
                return data;

            // First run: Better Mod Menu's data, if it has some.
            var imported = BetterModMenuImport.TryImport() ?? new StoreData();
            if (imported.ImportedFrom != null)
                Write(imported);
            return imported;
        }
        catch (Exception e)
        {
            // Never overwrite a file we couldn't read: keep working in memory and leave it for the user.
            Log.Error($"[{ModEntry.ModId}] Could not load {FilePath}, changes this session won't be saved: {e}");
            _readOnly = true;
            return new StoreData();
        }
    }

    // What LoadFrom found. Data is null only on a first run. Unreadable: where the file that couldn't be read is
    // now (set aside; or still in place when even that failed, and then ReadOnly: nothing is saved this
    // session). Backup: the one loaded in its place and when it was saved; none with Unreadable is a fresh start.
    public record Loaded(StoreData? Data, string? Unreadable = null, (string Path, DateTime Saved)? Backup = null, bool ReadOnly = false);

    // Reads manager.json in a folder (ours, or a self-test's). One that can't be read or parsed is moved aside as
    // manager.broken-<time>.json, never over another file, and the newest backup that reads takes its place (with
    // none, a fresh start). That is written back at once, so the next launch reads it as usual and never takes
    // it for a first run. A first run is no file and none set aside before: if an earlier write-back failed, the
    // backups load again rather than Better Mod Menu's data being imported over them.
    public static Loaded LoadFrom(string dir, DateTime now)
    {
        var file = Path.Combine(dir, FileName);
        if (!File.Exists(file))
        {
            if (!Directory.Exists(dir) || Directory.GetFiles(dir, BrokenPattern).Length == 0)
                return new Loaded(null);
            var (again, from) = NewestBackup(dir);
            Log.Warn($"[{ModEntry.ModId}] {file} is missing after an earlier recovery; loaded {from?.Path ?? "nothing (a fresh start)"}");
            WriteBack(file, again);
            return new Loaded(again, Backup: from);
        }
        try
        {
            return new Loaded(Read(file));
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.ModId}] Could not read {file}: {e.Message}");
        }

        var (data, backup) = NewestBackup(dir);
        string setAside;
        try
        {
            setAside = SetAside(dir, file, now);
        }
        catch (Exception e)
        {
            // Never overwrite a file we couldn't read: the backup in memory only, nothing saved this session.
            Log.Error($"[{ModEntry.ModId}] Could not move {file} aside, changes this session won't be saved: {e}");
            return new Loaded(data, file, backup, ReadOnly: true);
        }
        Log.Warn($"[{ModEntry.ModId}] Moved the unreadable file to {setAside}; loaded {backup?.Path ?? "nothing (a fresh start)"}");
        WriteBack(file, data);
        return new Loaded(data, setAside, backup);
    }

    // One file's data; throws when it can't be read or holds none (an empty file, "null").
    static StoreData Read(string file) =>
        JsonSerializer.Deserialize<StoreData>(File.ReadAllText(file), Json) ?? throw new JsonException("no data in the file");

    // The newest backup that reads (by name: manager.<time>.json), or a fresh start when none does.
    static (StoreData Data, (string Path, DateTime Saved)? From) NewestBackup(string dir)
    {
        var backups = Path.Combine(dir, BackupsFolder);
        if (Directory.Exists(backups))
            foreach (var path in Directory.GetFiles(backups, BackupPattern).OrderDescending())
                try
                {
                    return (Read(path), (path, File.GetLastWriteTime(path)));
                }
                catch (Exception e)
                {
                    Log.Warn($"[{ModEntry.ModId}] Could not read the backup {path} either: {e.Message}");
                }
        return (new StoreData(), null);
    }

    // Beside it as manager.broken-<time>.json (-2, -3 and on when that name is taken): never over another file.
    static string SetAside(string dir, string file, DateTime now)
    {
        var name = $"manager.broken-{now:yyyyMMdd-HHmmss}";
        var target = Path.Combine(dir, name + ".json");
        for (var n = 2; File.Exists(target); n++)
            target = Path.Combine(dir, $"{name}-{n}.json");
        File.Move(file, target, overwrite: false);
        return target;
    }

    // The recovered data as the file again. If this fails, the file set aside still says it's no first run.
    static void WriteBack(string file, StoreData data)
    {
        try
        {
            WriteAtomic(file, data);
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.ModId}] Could not write the recovered data to {file}: {e}");
        }
    }

    // Once, as the Modding screen opens after a launch that couldn't read the file: the game's popup saying what
    // happened and where the unreadable file is. Kept for the next opening while another popup is up.
    public static void ShowNotice()
    {
        try
        {
            if (Notice is { } notice && ShowNotice(notice))
                Notice = null;
        }
        catch (Exception e)
        {
            Notice = null; // it can't be shown: the log has it
            Log.Error($"[{ModEntry.ModId}] Could not show the notice about the unreadable file: {e}");
        }
    }

    // The popup for one load (nothing when it had no problem). True if shown.
    public static bool ShowNotice(Loaded loaded)
    {
        if (loaded.Unreadable is not { } file || NModalContainer.Instance is not { OpenModal: null } modals)
            return false;
        var body = new LocString("settings_ui", loaded.ReadOnly ? "SPIRE_MOD_MANAGER.DATA_READ_ONLY"
            : loaded.Backup != null ? "SPIRE_MOD_MANAGER.DATA_RECOVERED" : "SPIRE_MOD_MANAGER.DATA_FRESH");
        body.Add("path", Escape(file));
        if (loaded.Backup is { Saved: var saved })
            body.Add("date", $"{ModDetails.Date(saved)}, {saved.ToString("HH:mm", CultureInfo.InvariantCulture)}");
        if (NErrorPopup.Create(new LocString("settings_ui", "SPIRE_MOD_MANAGER.DATA_TITLE"), body, null, showReportBugButton: false) is not { } popup)
            return false;
        modals.Add(popup);
        return true;
    }

    static string Escape(string text) => text.Replace("[", "[lb]");

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

    // The first save of each session first copies the previous file into backups/ (one try a session).
    static void Write(StoreData data)
    {
        WriteTo(Dir, data, backUp: !_backedUpThisSession);
        _backedUpThisSession = true;
    }

    // Saves manager.json into a folder (ours, or a self-test's), after copying the old one into backups/ when
    // asked (the newest ten kept). The copy has its own try: when it fails (a file lock, antivirus), that's
    // logged and the save itself still happens.
    public static void WriteTo(string dir, StoreData data, bool backUp)
    {
        var file = Path.Combine(dir, FileName);
        if (backUp && File.Exists(file))
            try
            {
                var backups = Path.Combine(dir, BackupsFolder);
                Directory.CreateDirectory(backups);
                File.Copy(file, Path.Combine(backups, $"manager.{DateTime.Now:yyyyMMdd-HHmmss}.json"), overwrite: true);
                foreach (var old in Directory.GetFiles(backups, BackupPattern).OrderDescending().Skip(BackupsKept))
                    File.Delete(old);
            }
            catch (Exception e)
            {
                Log.Error($"[{ModEntry.ModId}] Could not back up {file}, saving without a backup: {e}");
            }

        try
        {
            Directory.CreateDirectory(dir);
            WriteAtomic(file, data);
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.ModId}] Could not save {file}: {e}");
        }
    }

    // Writes to a temp file and swaps it in, so a crash mid-write can't leave a half-written file.
    static void WriteAtomic(string file, StoreData data)
    {
        var temp = file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(data, Json));
        File.Move(temp, file, overwrite: true);
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

// One-time import of Better Mod Menu's mod_profiles.json (read-only; its file is never changed).
static class BetterModMenuImport
{
    const string BmmId = "BetterModMenu";

    public static StoreData? TryImport()
    {
        var path = FindFile();
        if (path == null)
            return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var data = new StoreData();

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
                foreach (var p in profiles.EnumerateArray())
                    data.Profiles.Add(new Profile
                    {
                        Name = NonEmpty(p, "Name") ?? $"Profile {data.Profiles.Count + 1}",
                        Disabled = Strings(p, "DisabledMods").Select(id => new ModRef(id, null)).ToList(),
                    });

            if (root.TryGetProperty("CurrentProfileIndex", out var index) && index.TryGetInt32(out var i) && i >= 0 && i < data.Profiles.Count)
                data.ActiveProfile = data.Profiles[i].Name;

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

    // Its account folder, or "portable mode" beside its own files.
    static string? FindFile()
    {
        var account = ProjectSettings.GlobalizePath(UserDataPathProvider.GetAccountScopedBasePath($"mod_data/{BmmId}"));
        var candidates = new List<string> { Path.Combine(account, "mod_profiles.json") };
        foreach (var mod in ModManager.Mods.Where(m => m.manifest?.id == BmmId))
            candidates.Add(Path.Combine(mod.path, "mod_profiles.json"));
        return candidates.FirstOrDefault(File.Exists);
    }

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
