using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Saves;

namespace SpireModManager;

// Crash-safe launch (docs/spikes/launch-first.md), its smallest form. Our initializer writes launch-pending.json in
// our data folder: the mods this launch is about to load. Reaching the main menu deletes it and saves the mod list as
// last-working.json. So a launch that finds launch-pending.json still there follows one that never reached the menu:
// a crash or hang in a mod loading after ours, a content or text error once every mod has loaded, or a close while
// it loaded. At the menu, a notice lists what changed since the last launch that worked, with "Restore last working
// setup" (that mod list back, then a relaunch) and "Keep". Nothing happens on its own: no rollback, no relaunch.
// We don't keep ourselves first in the load order, so a mod loading before ours that kills the launch leaves no
// record (and no notice either). Every step fails open: an error is logged and the launch goes on as usual.
static class LaunchGuard
{
    public record Entry(string Id, ModSource Source, bool Enabled, string? Version);
    public record Setup(DateTime At, List<Entry> Mods);

    // One difference, for the notice: Kind is New, Enabled, Disabled, Updated (From and To are the versions) or Order.
    public enum Kind { New, Enabled, Disabled, Updated, Order }
    public record Change(Kind Kind, string Id, string? From = null, string? To = null);

    public const string PendingFile = "launch-pending.json", WorkingFile = "last-working.json";

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter() },
    };

    // Taken in Init, and kept for the menu: the account folder is known by then (the game has read settings.save from
    // it), and the same folder clears the record whatever Store resolves later.
    public static string? Dir { get; private set; }
    static Setup? _failed, _working;
    static bool _reached;

    // After a launch that never reached the menu, with changes since the last one that worked: shown once at the main
    // menu (kept while another popup is up). The self-test sets it too.
    public static (Setup Working, List<Change> Changes)? Notice { get; set; }

    // First thing in ModEntry.Init.
    public static void Begin()
    {
        try
        {
            var settings = SaveManager.Instance.SettingsSave.ModSettings;
            var planned = ModManager.Mods.Where(m => m.manifest?.id != null)
                .Select(m => new Entry(m.manifest!.id!, m.modSource, !(settings?.IsModDisabled(m) ?? false), m.manifest.version)).ToList();
            var dir = Store.Dir;
            (_failed, _working) = Begin(dir, new Setup(DateTime.Now, planned));
            Dir = dir;
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.ModId}] Could not record this launch, so a launch that stops before the main menu won't be noticed: {e}");
        }
    }

    // Reads the last launch's record and the last working setup from a folder (ours, or a self-test's), then writes
    // this launch's record. A file that can't be read counts as none.
    public static (Setup? Failed, Setup? Working) Begin(string dir, Setup planned)
    {
        var (failed, working) = (Read(Path.Combine(dir, PendingFile)), Read(Path.Combine(dir, WorkingFile)));
        if (failed != null)
            Log.Warn($"[{ModEntry.ModId}] The last launch (started {failed.At:yyyy-MM-dd HH:mm:ss}) didn't reach the main menu");
        Directory.CreateDirectory(dir);
        Write(Path.Combine(dir, PendingFile), planned);
        return (failed, working);
    }

    // First main menu of the session (MenuReachedPatch, a prefix: it runs even when the menu's own _Ready throws).
    public static void MenuReached()
    {
        if (_reached || Dir == null)
            return;
        _reached = true;
        try
        {
            var versions = ModManager.Mods.Where(m => m.manifest?.id != null)
                .ToLookup(m => (m.manifest!.id!, m.modSource), m => m.manifest!.version);
            var saved = History.CurrentMods().Select(m => new Entry(m.Id, m.Source, m.Enabled, versions[(m.Id, m.Source)].FirstOrDefault())).ToList();
            var changes = Reached(Dir, new Setup(DateTime.Now, saved), _failed, _working);
            if (changes.Count > 0 && _working != null)
                Notice = (_working, changes);
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.ModId}] Could not clear this launch's record: {e}");
        }
    }

    // This launch worked: its mod list (the game's own, rewritten in load order) is the last working setup, and its
    // record goes. Returns what the failed launch before it, if any, had changed since the last working setup.
    public static List<Change> Reached(string dir, Setup saved, Setup? failed, Setup? working)
    {
        Write(Path.Combine(dir, WorkingFile), saved);
        File.Delete(Path.Combine(dir, PendingFile));
        if (failed == null || working == null)
            return [];
        var changes = Changes(failed.Mods, working.Mods);
        Log.Warn($"[{ModEntry.ModId}] Changed since the last launch that worked ({working.At:yyyy-MM-dd HH:mm}): "
            + (changes.Count == 0 ? "nothing" : string.Join(", ", changes.Select(c => $"{c.Id} {c.Kind} {c.From} {c.To}".TrimEnd()))));
        return changes;
    }

    // What a launch's mods changed since a working setup: mods new, enabled, disabled or updated, and whether the
    // ones enabled in both load in another order. Never ours: we were running, and an update of ours can't be undone.
    public static List<Change> Changes(IReadOnlyList<Entry> tried, IReadOnlyList<Entry> working)
    {
        var then = working.DistinctBy(m => (m.Id, m.Source)).ToDictionary(m => (m.Id, m.Source));
        var changes = new List<Change>();
        foreach (var mod in tried.DistinctBy(m => (m.Id, m.Source)).Where(m => m.Id != ModEntry.ModId))
            if (!then.TryGetValue((mod.Id, mod.Source), out var was))
            {
                if (mod.Enabled)
                    changes.Add(new Change(Kind.New, mod.Id));
            }
            else if (mod.Enabled != was.Enabled)
                changes.Add(new Change(mod.Enabled ? Kind.Enabled : Kind.Disabled, mod.Id));
            else if (mod.Enabled && was.Version != null && mod.Version != null && mod.Version != was.Version)
                changes.Add(new Change(Kind.Updated, mod.Id, was.Version, mod.Version));
        var both = tried.Where(m => m.Enabled && m.Id != ModEntry.ModId && then.TryGetValue((m.Id, m.Source), out var w) && w.Enabled)
            .Select(m => (m.Id, m.Source)).Distinct().ToList();
        if (!both.SequenceEqual(working.Where(m => both.Contains((m.Id, m.Source))).Select(m => (m.Id, m.Source)).Distinct()))
            changes.Add(new Change(Kind.Order, ""));
        return changes;
    }

    // From MainMenuPatch's timer, after the game's popups and our data notice; kept for the next main menu while
    // another popup is up.
    public static void ShowNotice()
    {
        if (Notice is not { } notice || NModalContainer.Instance is not { OpenModal: null })
            return;
        Notice = null;
        _ = Ask(notice.Working, notice.Changes);
    }

    const int LinesShown = 8; // the popup's text box holds about ten lines

    static async Task Ask(Setup working, List<Change> changes)
    {
        try
        {
            string Name(string id) => (ModManager.Mods.FirstOrDefault(m => m.manifest?.id == id)?.manifest?.name ?? id).Replace("[", "[lb]");
            var lines = changes.Take(changes.Count > LinesShown ? LinesShown - 1 : LinesShown).Select(c => "  •  " + c.Kind switch
            {
                Kind.New => Menus.Format("SPIRE_MOD_MANAGER.LAUNCH_NEW", ("mod", Name(c.Id))),
                Kind.Enabled => Menus.Format("SPIRE_MOD_MANAGER.LAUNCH_ENABLED", ("mod", Name(c.Id))),
                Kind.Disabled => Menus.Format("SPIRE_MOD_MANAGER.LAUNCH_DISABLED", ("mod", Name(c.Id))),
                Kind.Updated => Menus.Format("SPIRE_MOD_MANAGER.LAUNCH_UPDATED", ("mod", Name(c.Id)), ("from", c.From ?? ""), ("to", c.To ?? "")),
                _ => Menus.Text("SPIRE_MOD_MANAGER.LAUNCH_ORDER"),
            }).ToList();
            if (changes.Count > LinesShown)
                lines.Add("  •  " + Menus.Format("SPIRE_MOD_MANAGER.LAUNCH_MORE", ("count", Menus.Number(changes.Count - LinesShown + 1))));
            var body = new LocString("settings_ui", "SPIRE_MOD_MANAGER.LAUNCH_FAILED_BODY");
            body.Add("date", $"{ModDetails.Date(working.At)}, {working.At.ToString("HH:mm", CultureInfo.InvariantCulture)}");
            body.Add("changes", string.Join("\n", lines));
            if (await Menus.Confirm(new LocString("settings_ui", "SPIRE_MOD_MANAGER.LAUNCH_FAILED_TITLE"), body,
                    new LocString("settings_ui", "SPIRE_MOD_MANAGER.LAUNCH_KEEP"), new LocString("settings_ui", "SPIRE_MOD_MANAGER.LAUNCH_RESTORE")))
                Restore(working.Mods);
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.ModId}] Could not show the notice about the last launch: {e}");
        }
    }

    // The working setup's mod list back (order and on/off), mods installed since then off (a mod the list doesn't name
    // loads), ours always on; then a relaunch.
    public static void Restore(IReadOnlyList<Entry> working)
    {
        var settings = SaveManager.Instance.SettingsSave.ModSettings ??= new ModSettings();
        var entries = working.Select(m => new SettingsSaveMod { Id = m.Id, Source = m.Source, IsEnabled = m.Enabled || m.Id == ModEntry.ModId }).ToList();
        entries.AddRange(ModManager.Mods.Union(Installed.Mods).Where(m => m.manifest?.id is { } id && !working.Any(w => w.Id == id && w.Source == m.modSource))
            .Select(m => new SettingsSaveMod(m) { IsEnabled = m.manifest?.id == ModEntry.ModId }));
        settings.ModList.Clear();
        settings.ModList.AddRange(entries);
        SaveManager.Instance.SaveSettings();
        Log.Info($"[{ModEntry.ModId}] Restored the mod list of the last launch that worked");
        ModEntry.Relaunch();
    }

    // A record or a working setup from its file; null when there is none or it can't be read.
    public static Setup? Read(string file)
    {
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize<Setup>(File.ReadAllText(file), Json) is { Mods: not null } setup ? setup : null : null;
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.ModId}] Could not read {file}, so it counts as none: {e.Message}");
            return null;
        }
    }

    // To a temp file, then swapped in: a crash mid-write leaves the old file or the new one, never half of one.
    static void Write(string file, Setup setup)
    {
        var temp = file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(setup, Json));
        File.Move(temp, file, overwrite: true);
    }
}
