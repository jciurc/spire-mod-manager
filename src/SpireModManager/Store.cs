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

// Everything the manager remembers, as one JSON file per Steam account:
// user://steam/<id>/mod_data/SpireModManager/manager.json
// Categories, aliases and notes belong to a mod id, so a Workshop and a local copy share them. Profiles name each
// mod's source too, like the game's own list.
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
    // The two-mod-managers banner dismissed (LoadOrder): it stays away until the two aren't both ticked.
    public bool ManagersWarningDismissed { get; set; }
    // Where the player moved the main menu button (top-left, in the game's 1920x1080 frame); null: the corner.
    public ScreenPoint? MenuButtonAt { get; set; }
    public string? ImportedFrom { get; set; }
    // The share dialog's tick boxes, kept across launches while its Remember selection is ticked; null: the defaults.
    public ShareChoice? ShareChoices { get; set; }
}

public record ShareChoice(bool Category, bool Disabled, bool Alias, bool Notes);

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
    // Kept through renames and saves, new for a duplicate or import, so a profile renamed since launch isn't read as
    // one deleted and another added. Files from before it get one on load.
    public string? Id { get; set; } = NewId();
    public static string NewId() => Guid.NewGuid().ToString("N");
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

    // Loaded on first use (or while the main menu is idle), not in Init, so it doesn't slow the launch.
    public static StoreData Data => _data ??= Load();

#if DEVTOOLS
    // Self-test: the next use loads again, from the file the first load wrote (a second launch).
    public static void Reload() => _data = null;
#endif

    // The mod's data folder (known from Init on): manager.json, backups, drop-in translations and the launch record.
    public static string Dir => ProjectSettings.GlobalizePath(UserDataPathProvider.GetAccountScopedBasePath($"mod_data/{ModEntry.ModId}"));
    public static string FilePath => Path.Combine(Dir, FileName);

    // A launch that found the file unreadable, for the Modding screen's notice (shown once, then null).
    public static Loaded? Notice { get; set; }

    static StoreData Load()
    {
        try
        {
            var loaded = WithImport(LoadFrom(Dir, DateTime.Now), BetterModMenuImport.TryImport);
            _readOnly = loaded.ReadOnly;
            if (loaded.Unreadable != null)
                Notice = loaded;
            // Saves the import over the fresh start LoadFrom wrote back, without backing up that empty file.
            if (loaded.Imported && loaded.Data != null)
                WriteTo(Dir, loaded.Data, backUp: false);
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

    // What LoadFrom found. Data: null only on a first run. Unreadable: where the bad file is now (set aside, or in
    // place if that failed too, then ReadOnly). Backup: the one loaded instead. Imported: Better Mod Menu's data.
    public record Loaded(StoreData? Data, string? Unreadable = null, (string Path, DateTime Saved)? Backup = null, bool ReadOnly = false,
        bool Imported = false);

    // A recovery with no readable backup starts from Better Mod Menu's data, if it has some, as on a first run.
    public static Loaded WithImport(Loaded loaded, Func<StoreData?> import) =>
        loaded is { Unreadable: not null, Backup: null, ReadOnly: false } && import() is { } imported
            ? loaded with { Data = imported, Imported = true } : loaded;

    // Reads manager.json in a folder. One that can't be read is set aside (manager.broken-<time>.json) and the newest
    // backup that reads, or a fresh start, is written in its place, so the next launch never takes it for a first run.
    // A first run is no file and none set aside before, so the backups load again if that write-back failed.
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

    // One file's data, tidied; throws when it can't be read or holds none (an empty file, "null").
    static StoreData Read(string file) =>
        Tidy(JsonSerializer.Deserialize<StoreData>(File.ReadAllText(file), Json) ?? throw new JsonException("no data in the file"));

    // Nulls from a hand-edited or old file: lists become empty, names "", null entries dropped. Nulls that mean
    // something stay (a profile's unsaved edit, a mod's tags when it has none).
    static StoreData Tidy(StoreData data)
    {
        data.Categories = data.Categories?.OfType<Category>().ToList() ?? [];
        foreach (var category in data.Categories)
            category.Name ??= "";
        data.Mods ??= [];
        foreach (var id in data.Mods.Where(p => p.Value == null).Select(p => p.Key).ToList())
            data.Mods.Remove(id);
        foreach (var info in data.Mods.Values)
            info.Tags = info.Tags?.OfType<string>().ToList() is { Count: > 0 } tags ? tags : null;
        data.Profiles = data.Profiles?.OfType<Profile>().ToList() ?? [];
        var ids = new HashSet<string>();
        foreach (var profile in data.Profiles)
        {
            if (string.IsNullOrEmpty(profile.Id) || !ids.Add(profile.Id)) // none (a hand-edited file), or another's
                ids.Add(profile.Id = Profile.NewId());
            profile.Name ??= "";
            profile.Mods = profile.Mods?.Where(m => m?.Id != null).ToList() ?? [];
            profile.Edited = profile.Edited?.Where(m => m?.Id != null).ToList();
            profile.Disabled = profile.Disabled?.Where(m => m?.Id != null).ToList();
        }
        return data;
    }

    // The newest backup that reads, or a fresh start when none does.
    static (StoreData Data, (string Path, DateTime Saved)? From) NewestBackup(string dir)
    {
        var backups = Path.Combine(dir, BackupsFolder);
        if (Directory.Exists(backups))
            foreach (var path in NewestFirst(Directory.GetFiles(backups, BackupPattern)))
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

    // Backups newest first, by the time in their names: manager.<time>Z.json (UTC) or, older, manager.<time>.json
    // (local time); a name without a time last.
    public static IEnumerable<string> NewestFirst(IEnumerable<string> paths) =>
        paths.OrderByDescending(BackupTime).ThenByDescending(path => path, StringComparer.Ordinal);

    static DateTime BackupTime(string path)
    {
        var stamp = Path.GetFileNameWithoutExtension(path)["manager.".Length..];
        var utc = stamp.EndsWith('Z');
        return DateTime.TryParseExact(utc ? stamp[..^1] : stamp, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture,
            utc ? DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal : DateTimeStyles.AssumeLocal | DateTimeStyles.AdjustToUniversal,
            out var time) ? time : DateTime.MinValue;
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
            WriteAtomic(file, JsonSerializer.Serialize(data, Json));
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.ModId}] Could not write the recovered data to {file}: {e}");
        }
    }

    // The popup about an unreadable file, once; while another popup is up, it waits for the next call.
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
        if (NoticeText(loaded) is not { } body || NModalContainer.Instance is not { OpenModal: null } modals)
            return false;
        if (NErrorPopup.Create(new LocString("settings_ui", "SPIRE_MOD_MANAGER.DATA_TITLE"), body, null, showReportBugButton: false) is not { } popup)
            return false;
        modals.Add(popup);
        LinkFolder(popup);
        return true;
    }

#if DEVTOOLS
    // Self-test: the notice's folder link calls it with the folder instead of opening a window.
    public static Action<string>? TestOpenFolder;
#endif

    // The notice's "mod's data folder" ([url=folder]) opens the folder in the system's file browser.
    static void LinkFolder(Node popup)
    {
        if (popup.FindChild("Description", true, false) is not RichTextLabel text)
            return;
        text.MouseFilter = Control.MouseFilterEnum.Stop; // the game's label lets clicks through
        text.MetaClicked += meta =>
        {
            if (meta.AsString() != "folder")
                return;
#if DEVTOOLS
            if (TestOpenFolder is { } test)
            {
                test(Dir);
                return;
            }
#endif
            OS.ShellOpen(Dir);
        };
    }

    // The notice's text: what happened, the set-aside file's name, the backup's date; null when the load was fine.
    public static LocString? NoticeText(Loaded loaded)
    {
        if (loaded.Unreadable is not { } file)
            return null;
        var body = new LocString("settings_ui", loaded.ReadOnly ? "SPIRE_MOD_MANAGER.DATA_READ_ONLY"
            : loaded.Backup != null ? "SPIRE_MOD_MANAGER.DATA_RECOVERED"
            : loaded.Imported ? "SPIRE_MOD_MANAGER.DATA_FROM_BMM" : "SPIRE_MOD_MANAGER.DATA_FRESH");
        body.Add("file", Escape(Path.GetFileName(file))); // the name only: a full path doesn't fit the popup
        if (loaded.Backup is { Saved: var saved })
            body.Add("date", $"{ModDetails.Date(saved)}, {saved.ToString("HH:mm", CultureInfo.InvariantCulture)}");
        return body;
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

    // Saves view state changed mid-redraw (a re-armed banner) without History.Record: a record there could split one
    // undo step in two.
    public static void SaveViewState()
    {
        if (!_batching && _data != null && !_readOnly)
            Write(_data);
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

    // Our data minus view state (views, collapsed categories, a dismissed banner, the share dialog's kept tick boxes):
    // what undo compares and restores.
    public static string EditsJson()
    {
        var json = JsonSerializer.Serialize(Data, Json);
        if (_edits is { } last && last.Data == json)
            return last.Edits;
        var copy = JsonSerializer.Deserialize<StoreData>(json, Json) ?? new StoreData();
        copy.VanillaView = copy.LoadOrderView = copy.UncategorizedCollapsed = copy.ManagersWarningDismissed = false;
        copy.ShareChoices = null;
        foreach (var category in copy.Categories)
            category.Collapsed = false;
        var edits = JsonSerializer.Serialize(copy, Json);
        _edits = (json, edits);
        return edits;
    }

    // The last EditsJson and the data it was made from: asked several times per change (undo history, the changes bar,
    // its list), it's only made again when the data has changed.
    static (string Data, string Edits)? _edits;

    // A snapshot of our data (EditsJson) with a change made to it as well.
    public static string Patched(string json, Action<StoreData> change)
    {
        var data = JsonSerializer.Deserialize<StoreData>(json, Json) ?? new StoreData();
        change(data);
        return JsonSerializer.Serialize(data, Json);
    }

    // Puts saved edits back, keeping the current view state.
    public static void RestoreEdits(string json)
    {
        var restored = JsonSerializer.Deserialize<StoreData>(json, Json) ?? new StoreData();
        var now = Data;
        (restored.VanillaView, restored.LoadOrderView, restored.UncategorizedCollapsed, restored.ManagersWarningDismissed, restored.ShareChoices) =
            (now.VanillaView, now.LoadOrderView, now.UncategorizedCollapsed, now.ManagersWarningDismissed, now.ShareChoices);
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

    // Saves manager.json into a folder, first copying the old one into backups/ if asked (the newest ten kept). A
    // failed backup (a file lock, antivirus) is logged and the save still happens.
    public static void WriteTo(string dir, StoreData data, bool backUp)
    {
        var file = Path.Combine(dir, FileName);
        if (backUp && File.Exists(file))
            try
            {
                var backups = Path.Combine(dir, BackupsFolder);
                Directory.CreateDirectory(backups);
                // Named in UTC ("Z"), so daylight saving can't put two out of order or one over another. Older ones
                // are in local time; NewestFirst reads both.
                File.Copy(file, Path.Combine(backups, $"manager.{DateTime.UtcNow:yyyyMMdd-HHmmss}Z.json"), overwrite: true);
                foreach (var old in NewestFirst(Directory.GetFiles(backups, BackupPattern)).Skip(BackupsKept))
                    File.Delete(old);
            }
            catch (Exception e)
            {
                Log.Error($"[{ModEntry.ModId}] Could not back up {file}, saving without a backup: {e}");
            }

        try
        {
            Directory.CreateDirectory(dir);
            WriteAtomic(file, JsonSerializer.Serialize(data, Json));
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.ModId}] Could not save {file}: {e}");
        }
    }

    // Writes to a temp file and swaps it in, so a crash can't leave half a file. at: the last-write time to set.
    public static void WriteAtomic(string file, string json, DateTime? at = null)
    {
        var temp = file + ".tmp";
        File.WriteAllText(temp, json);
        if (at is { } time)
            File.SetLastWriteTimeUtc(temp, time);
        File.Move(temp, file, overwrite: true);
    }

    public static ModInfo? InfoFor(string? modId) => modId != null && Data.Mods.TryGetValue(modId, out var info) ? info : null;

    // Changes one mod's details and saves; a mod left with none of ours loses its entry, so it reads as unchanged.
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

// One-time import of Better Mod Menu's saved data, read-only: the file it would load itself, read the way it does.
static class BetterModMenuImport
{
    const string BmmId = "BetterModMenu";

    // Its file names, in its order of preference within a folder.
    static readonly string[] FileNames = ["mod_profiles.json5", "mod_profiles.jsonc", "mod_profiles.json"];

    // It allows comments and trailing commas whatever the file's name; that's all ".json5" means to it.
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
        foreach (var name in Strings(root, "CustomGroups").Distinct()) // a group named twice: one category
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
        {
            // Better Mod Menu lets two profiles share a name (a new one is named by the count, a rename isn't checked).
            // Ours are told apart by name, so a repeated one gets the first free " (n)", as a copy does; it stays at its
            // place, so CurrentProfileIndex still finds it.
            var wanted = NonEmpty(p, "Name") ?? $"Profile {data.Profiles.Count + 1}";
            var name = wanted;
            for (var n = 1; data.Profiles.Any(o => o.Name == name); n++)
                name = $"{wanted} ({n})";
            data.Profiles.Add(new Profile
            {
                Name = name,
                Disabled = Strings(p, "DisabledMods").Select(id => new ModRef(id, null)).ToList(),
            });
        }
    }

    // Where it looks, in its order: beside its first copy in the mod list ("portable mode"), then its folder in the
    // Steam account's data.
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
