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

// Crash-safe launch, its smallest form (next: #142). Our initializer records the mods this launch loads; reaching the
// main menu deletes the record and saves them as the last working setup. A record still there at the next launch
// means that launch never reached the menu, and a notice offers Restore (that setup, then a relaunch) or Keep.
// Nothing happens on its own, and every step fails open.
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
    static Setup? _failed, _working, _planned;
    static bool _reached;

    // The notice after a launch that never reached the menu: shown once at the main menu, after any other popup.
    public static (Setup Working, List<Change> Changes)? Notice { get; set; }

    // First thing in ModEntry.Init, inside a try there too (a renamed game member throws at the call, before this try).
    // The mods in load order, each on or off by its own entry (the game's rebuilt list copies the first copy's state).
    public static void Begin()
    {
        try
        {
            var settings = SaveManager.Instance.SettingsSave.ModSettings;
            var planned = new Setup(DateTime.Now, ModManager.Mods.Where(m => m.manifest?.id != null)
                .Select(m => new Entry(m.manifest!.id!, m.modSource, !(settings?.IsModDisabled(m) ?? false), m.manifest.version)).ToList());
            var dir = Store.Dir;
            (_failed, _working) = Begin(dir, planned);
            (Dir, _planned) = (dir, planned);
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.ModId}] Could not record this launch, so a launch that stops before the main menu won't be noticed: {e}");
        }
    }

    // Reads the last launch's record and working setup from a folder (ours, or a self-test's), then writes this
    // launch's record. An unreadable file counts as none, and so does a record no newer than the working setup.
    public static (Setup? Failed, Setup? Working) Begin(string dir, Setup planned)
    {
        var (failed, working) = (Read(Path.Combine(dir, PendingFile)), Read(Path.Combine(dir, WorkingFile)));
        if (failed != null && working != null && failed.At <= working.At)
        {
            Log.Info($"[{ModEntry.ModId}] The last launch's record is older than the last working setup (it reached the main menu), so it counts as none");
            failed = null;
        }
        if (failed != null)
            Log.Warn($"[{ModEntry.ModId}] The last launch (started {failed.At:yyyy-MM-dd HH:mm:ss}) didn't reach the main menu");
        Directory.CreateDirectory(dir);
        Write(Path.Combine(dir, PendingFile), planned);
        return (failed, working);
    }

    // First main menu of the session (MenuReachedPatch, a prefix: it runs even when the menu's own _Ready throws).
    public static void MenuReached()
    {
        if (_reached || Dir == null || _planned == null)
            return;
        _reached = true;
        try
        {
            var changes = Reached(Dir, _planned with { At = DateTime.Now }, _failed, _working);
            if (changes.Count > 0)
                Notice = (_working!, changes);
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.ModId}] Could not clear this launch's record: {e}");
        }
    }

    // This launch worked: deletes its record and saves its mods as the last working setup. Returns what the failed
    // launch before it changed, for the notice: none when it loaded the same mods or only updated some (Restore can't
    // undo an update).
    public static List<Change> Reached(string dir, Setup saved, Setup? failed, Setup? working)
    {
        try
        {
            File.Delete(Path.Combine(dir, PendingFile));
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.ModId}] Could not delete this launch's record (the next launch ignores it once the working setup is saved): {e.Message}");
        }
        try
        {
            Write(Path.Combine(dir, WorkingFile), saved);
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.ModId}] Could not save this launch's mods as the last working setup: {e}");
        }
        if (failed == null || working == null)
            return [];
        if (failed.Mods.SequenceEqual(saved.Mods))
        {
            Log.Info($"[{ModEntry.ModId}] This launch loaded the same mods as the one that stopped, and reached the main menu: no notice");
            return [];
        }
        var changes = Changes(failed.Mods, working.Mods);
        Log.Warn($"[{ModEntry.ModId}] Changed since the last launch that worked ({working.At:yyyy-MM-dd HH:mm}): "
            + (changes.Count == 0 ? "nothing" : string.Join(", ", changes.Select(c => $"{c.Id} {c.Kind} {c.From} {c.To}".TrimEnd()))));
        return changes.Any(c => c.Kind != Kind.Updated) ? changes : [];
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

    // Shows the notice from MainMenuPatch's timer, trying again each second while another popup is up.
    public static void ShowNotice(Node menu)
    {
        if (Notice is not { } notice)
            return;
        if (!GodotObject.IsInstanceValid(menu) || !menu.IsInsideTree())
        {
            Notice = null;
            Log.Info($"[{ModEntry.ModId}] The main menu went before the notice about the last launch could show, so it's dropped");
            return;
        }
        if (NModalContainer.Instance is not { OpenModal: null })
        {
            menu.GetTree().CreateTimer(1).Timeout += () => ShowNotice(menu);
            return;
        }
        Notice = null;
        _ = Ask(menu, notice.Working, notice.Changes);
    }

    const int LinesShown = 5; // with the title line and the Restore sentence, about all the popup's text box holds

    static async Task Ask(Node menu, Setup working, List<Change> changes)
    {
        try
        {
            static string Escaped(string text) => text.Replace("[", "[lb]");
            string Name(string id) => Escaped(ModManager.Mods.FirstOrDefault(m => m.manifest?.id == id)?.manifest?.name ?? id);
            var lines = changes.Take(changes.Count > LinesShown ? LinesShown - 1 : LinesShown).Select(c => "  •  " + c.Kind switch
            {
                Kind.New => Menus.Format("SPIRE_MOD_MANAGER.LAUNCH_NEW", ("mod", Name(c.Id))),
                Kind.Enabled => Menus.Format("SPIRE_MOD_MANAGER.LAUNCH_ENABLED", ("mod", Name(c.Id))),
                Kind.Disabled => Menus.Format("SPIRE_MOD_MANAGER.LAUNCH_DISABLED", ("mod", Name(c.Id))),
                Kind.Updated => Menus.Format("SPIRE_MOD_MANAGER.LAUNCH_UPDATED", ("mod", Name(c.Id)),
                    ("from", Escaped(c.From ?? "")), ("to", Escaped(c.To ?? ""))),
                _ => Menus.Text("SPIRE_MOD_MANAGER.LAUNCH_ORDER"),
            }).ToList();
            if (changes.Count > LinesShown)
                lines.Add("  •  " + Menus.Format("SPIRE_MOD_MANAGER.LAUNCH_MORE", ("count", Menus.Number(changes.Count - LinesShown + 1))));
            var body = new LocString("settings_ui", "SPIRE_MOD_MANAGER.LAUNCH_FAILED_BODY");
            body.Add("date", $"{ModDetails.Date(working.At)}, {working.At.ToString("HH:mm", CultureInfo.InvariantCulture)}");
            body.Add("changes", string.Join("\n", lines));
            if (await Menus.Confirm(new LocString("settings_ui", "SPIRE_MOD_MANAGER.LAUNCH_FAILED_TITLE"), body,
                    new LocString("settings_ui", "SPIRE_MOD_MANAGER.LAUNCH_KEEP"), new LocString("settings_ui", "SPIRE_MOD_MANAGER.LAUNCH_RESTORE")))
                Restore(working.Mods, menu);
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.ModId}] Could not show the notice about the last launch: {e}");
        }
    }

    // The working setup's mod list back (order and on/off), mods installed since then off (a mod the list doesn't name
    // loads), ours always on; then a relaunch through the relaunch check (owner: the main menu), which catches a mod
    // left on that now needs one installed since (its dialog offers the fix).
    public static void Restore(IReadOnlyList<Entry> working, Node owner)
    {
        var settings = SaveManager.Instance.SettingsSave.ModSettings ??= new ModSettings();
        var entries = working.Select(m => new SettingsSaveMod { Id = m.Id, Source = m.Source, IsEnabled = m.Enabled || m.Id == ModEntry.ModId }).ToList();
        entries.AddRange(ModManager.Mods.Union(Installed.Mods).Where(m => m.manifest?.id is { } id && !working.Any(w => w.Id == id && w.Source == m.modSource))
            .Select(m => new SettingsSaveMod(m) { IsEnabled = m.manifest?.id == ModEntry.ModId }));
        settings.ModList.Clear();
        settings.ModList.AddRange(entries);
        SaveManager.Instance.SaveSettings();
        Log.Info($"[{ModEntry.ModId}] Restored the mod list of the last launch that worked");
        RelaunchCheck.Relaunch(owner);
    }

    // A record or working setup from its file, or null. A mod without an id or a known source, or a missing time, makes
    // the whole file unreadable: Restore would save a mod list the game can't load, and Begin compares the times.
    public static Setup? Read(string file)
    {
        try
        {
            if (!File.Exists(file))
                return null;
            if (JsonSerializer.Deserialize<Setup>(File.ReadAllText(file), Json) is not { Mods: not null } setup)
                Log.Warn($"[{ModEntry.ModId}] {file} has no list of mods, so it counts as none");
            else if (setup.At == default)
                Log.Warn($"[{ModEntry.ModId}] {file} has no time, so it counts as none");
            else if (!setup.Mods.All(m => m != null && !string.IsNullOrEmpty(m.Id) && Enum.IsDefined(m.Source)))
                Log.Warn($"[{ModEntry.ModId}] {file} lists a mod with no id or an unknown source, so it counts as none");
            else
                return setup;
            return null;
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.ModId}] Could not read {file}, so it counts as none: {e.Message}");
            return null;
        }
    }

    static void Write(string file, Setup setup) => Store.WriteAtomic(file, JsonSerializer.Serialize(setup, Json));
}
