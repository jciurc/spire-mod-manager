using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Godot;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Saves;

namespace SpireModManager;

// Crash-safe launch. Our initializer records the mods this launch loads; reaching the main menu deletes the record and
// saves them as the last working setup. A record still there at the next launch means that launch never reached the
// menu, and a notice offers Restore (that setup, then a relaunch) or Keep. A launch the game reports it couldn't start
// (its startup error, StartupErrorPatch) gets the offer at once, in place of the game's dialog with only Quit: a mod that
// stops every launch never lets the menu come. Nothing happens on its own, and every step fails open.
static class LaunchGuard
{
    public record Entry(string Id, ModSource Source, bool Enabled, string? Version);

    // Game: the game's version and build (none in a record from before it was kept), so a crash after a game update can
    // say a setup from before it may not help.
    public record Setup(DateTime At, List<Entry> Mods, string? Game = null);

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
    static List<(string Id, ModSource Source, bool On)>? _read;

    // The mod list as this launch read it (Begin), for the self-test.
    public static IReadOnlyList<(string Id, ModSource Source, bool On)>? ReadList => _read;
    static bool _reached;

    // The notice after a launch that never reached the menu: shown once at the main menu, after any other popup.
    public static (Setup Working, List<Change> Changes)? Notice { get; set; }

    // First thing in ModEntry.Init, inside a try there too (a renamed game member throws at the call, before this try).
    // The mods in load order, each on or off by its own entry (the game's rebuilt list copies the first copy's state).
    public static void Begin()
    {
        try
        {
            var off = Dependencies.SavedOff();
            var planned = new Setup(DateTime.Now, ModManager.Mods.Select(m => m.manifest is { id: { } id } manifest
                ? new Entry(id, m.modSource, !off.Contains((id, m.modSource)), manifest.version) : null).OfType<Entry>().ToList(), GameNow());
            var dir = Store.Dir;
            // The mod list as the game read it: after the mods load, it puts its own in place (sorted by dependencies).
            _read = SaveManager.Instance.SettingsSave.ModSettings?.ModList.Select(Key).ToList();
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
            if (changes.Count > 0 && _working is { } working) // Reached has changes only with a working setup
                Notice = (working, changes);
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

    static bool Ours(string id) => id is ModEntry.ModId or ModDoctor.ModId;

    // What a launch's mods changed since a working setup: mods new, enabled, disabled or updated, and whether the
    // ones enabled in both load in another order. Never ours (we were running, and an update of ours can't be undone) nor
    // Mod Doctor's (it's there to look at failures; Restore keeps it as it is).
    public static List<Change> Changes(IReadOnlyList<Entry> tried, IReadOnlyList<Entry> working)
    {
        var then = working.DistinctBy(m => (m.Id, m.Source)).ToDictionary(m => (m.Id, m.Source));
        var changes = new List<Change>();
        foreach (var mod in tried.DistinctBy(m => (m.Id, m.Source)).Where(m => !Ours(m.Id)))
            if (!then.TryGetValue((mod.Id, mod.Source), out var was))
            {
                if (mod.Enabled)
                    changes.Add(new Change(Kind.New, mod.Id));
            }
            else if (mod.Enabled != was.Enabled)
                changes.Add(new Change(mod.Enabled ? Kind.Enabled : Kind.Disabled, mod.Id));
            else if (mod.Enabled && was.Version != null && mod.Version != null && mod.Version != was.Version)
                changes.Add(new Change(Kind.Updated, mod.Id, was.Version, mod.Version));
        var both = tried.Where(m => m.Enabled && !Ours(m.Id) && then.TryGetValue((m.Id, m.Source), out var w) && w.Enabled)
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
            var body = new LocString("settings_ui", "SPIRE_MOD_MANAGER.LAUNCH_FAILED_BODY");
            body.Add("date", When(working));
            body.Add("changes", string.Join("\n", Lines(changes)));
            if (await Menus.Confirm(new LocString("settings_ui", "SPIRE_MOD_MANAGER.LAUNCH_FAILED_TITLE"), body,
                    new LocString("settings_ui", "SPIRE_MOD_MANAGER.LAUNCH_KEEP"), new LocString("settings_ui", "SPIRE_MOD_MANAGER.LAUNCH_RESTORE")))
                Restore(working.Mods, menu);
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.ModId}] Could not show the notice about the last launch: {e}");
        }
    }

    static string When(Setup setup) => $"{ModDetails.Date(setup.At)}, {setup.At.ToString("HH:mm", CultureInfo.InvariantCulture)}";

    static string Escaped(string text) => text.Replace("[", "[lb]");

    static string Name(string id) => Escaped(PlainName(id));

    // The changes as the popups list them, a line each, the last ones counted in one line if there are many. Plain: for
    // where the game's text couldn't load, in English from our own file and without markup (Our).
    static List<string> Lines(List<Change> changes, bool plain = false)
    {
        string Mod(string id) => plain ? PlainName(id) : Name(id);
        string Version(string? version) => plain ? version ?? "" : Escaped(version ?? "");
        var lines = changes.Take(changes.Count > LinesShown ? LinesShown - 1 : LinesShown).Select(c => "  •  " + c.Kind switch
        {
            Kind.New => Our(plain, "SPIRE_MOD_MANAGER.LAUNCH_NEW", ("mod", Mod(c.Id))),
            Kind.Enabled => Our(plain, "SPIRE_MOD_MANAGER.LAUNCH_ENABLED", ("mod", Mod(c.Id))),
            Kind.Disabled => Our(plain, "SPIRE_MOD_MANAGER.LAUNCH_DISABLED", ("mod", Mod(c.Id))),
            Kind.Updated => Our(plain, "SPIRE_MOD_MANAGER.LAUNCH_UPDATED", ("mod", Mod(c.Id)), ("from", Version(c.From)), ("to", Version(c.To))),
            _ => Our(plain, "SPIRE_MOD_MANAGER.LAUNCH_ORDER"),
        }).ToList();
        if (changes.Count > LinesShown)
            lines.Add("  •  " + Our(plain, "SPIRE_MOD_MANAGER.LAUNCH_MORE", ("count", Count(plain, changes.Count - LinesShown + 1))));
        return lines;
    }

    static string PlainName(string id) => ModManager.Mods.FirstOrDefault(m => m.manifest?.id == id)?.manifest?.name ?? id;

    static string Count(bool plain, int count) => plain ? count.ToString(CultureInfo.InvariantCulture) : Menus.Number(count);

    // One of our texts, filled in: through the game's text, or (plain) in English from our own file without markup, for
    // where the game's text couldn't load. Names and versions go in as given (Lines escapes them for the game's markup).
    static string Our(bool plain, string key, params (string Name, string Value)[] values)
    {
        if (!plain)
            return values.Length == 0 ? Menus.Text(key) : Menus.Format(key, values);
        _english ??= JsonSerializer.Deserialize<Dictionary<string, string>>(
            Godot.FileAccess.GetFileAsString($"res://{ModEntry.ModId}/localization/eng/settings_ui.json")) ?? [];
        var text = Markup.Replace(_english[key], "");
        foreach (var (name, value) in values)
            text = text.Replace("{" + name + "}", value);
        return text;
    }

    static Dictionary<string, string>? _english;
    static readonly Regex Markup = new(@"\[/?[a-z_]+(?:[ =][^\]]*)?\]");

    // The working setup's mod list back (order and on/off), mods installed since then off (a mod the list doesn't name
    // loads), ours always on; then a relaunch through the relaunch check (owner: the main menu), which catches a mod
    // left on that now needs one installed since (its dialog offers the fix).
    public static void Restore(IReadOnlyList<Entry> working, Node owner)
    {
        WriteList(working);
        RelaunchCheck.Relaunch(owner);
    }

    static void WriteList(IReadOnlyList<Entry> working)
    {
        var settings = SaveManager.Instance.SettingsSave.ModSettings ??= new ModSettings();
        var entries = Written(working);
        settings.ModList.Clear();
        settings.ModList.AddRange(entries);
        SaveManager.Instance.SaveSettings();
        Log.Info($"[{ModEntry.ModId}] Restored the mod list of the last launch that worked");
    }

    // The mod list a Restore writes: the working setup's, in its order and on or off; mods installed since then off (a
    // mod the list doesn't name loads); ours always on, and Mod Doctor as it is now (it's there to look at the failure).
    public static List<SettingsSaveMod> Written(IReadOnlyList<Entry> working)
    {
        var off = Dependencies.SavedOff();
        bool On(string id, ModSource source, bool then) =>
            id == ModEntry.ModId || (id == ModDoctor.ModId ? !off.Contains((id, source)) : then);
        var entries = working.Select(m => new SettingsSaveMod { Id = m.Id, Source = m.Source, IsEnabled = On(m.Id, m.Source, m.Enabled) }).ToList();
        foreach (var m in ModManager.Mods.Union(Installed.Mods))
            if (m.manifest?.id is { } id && !working.Any(w => w.Id == id && w.Source == m.modSource))
                entries.Add(new SettingsSaveMod(m) { IsEnabled = On(id, m.modSource, false) });
        return entries;
    }

    // What the game couldn't start with, offered back: the last working setup, what this launch changed since, and
    // whether the game itself changed since (then the old setup may not help).
    public record Offer(Setup Working, List<Change> Changes, bool GameChanged);

    // An offer when Restore would change what the game loads: this launch changed mods since the last working setup
    // beyond updates (Restore can't undo one), and the list it would write loads differently from the list this launch
    // read. Not after a Restore whose launch failed too, nor for an order the game's own sorting sets (a dependency an
    // update added): offering it would loop, Restore and the same failure again.
    public static Offer? CrashOffer() => CrashOffer(_planned, _working, _read);

    public static Offer? CrashOffer(Setup? planned, Setup? working, IReadOnlyList<(string Id, ModSource Source, bool On)>? read) =>
        CrashOffer(planned, working) is { } offer && read != null && !SameLoad([.. Written(offer.Working.Mods).Select(Key)], read) ? offer : null;

    static (string, ModSource, bool) Key(SettingsSaveMod entry) => (entry.Id, entry.Source, entry.IsEnabled);

    // Whether two mod lists have a launch load the same: the same mods on, in the same order (the game's tie-break
    // after dependencies), and every mod on or off alike (a mod a list doesn't name loads). Off mods' order doesn't count.
    public static bool SameLoad(IReadOnlyList<(string Id, ModSource Source, bool On)> a, IReadOnlyList<(string Id, ModSource Source, bool On)> b) =>
        a.Where(e => e.On).Select(e => (e.Id, e.Source)).SequenceEqual(b.Where(e => e.On).Select(e => (e.Id, e.Source)))
        && a.ToHashSet().SetEquals(b);

    // The offer from a launch's record and the working setup: none without either, or with only updates since.
    public static Offer? CrashOffer(Setup? planned, Setup? working)
    {
        if (planned == null || working == null)
            return null;
        var changes = Changes(planned.Mods, working.Mods);
        if (!changes.Any(c => c.Kind != Kind.Updated))
            return null;
        return new Offer(working, changes, working.Game != null && planned.Game != null && working.Game != planned.Game);
    }

    const int SuspectsShown = 3; // Mod Doctor can name many (every mod patching the failing step)

#if DEVTOOLS
    // Self-test: as if the game's text couldn't load, for the plain dialog.
    public static bool TestNoLanguage;
#endif

    // In place of the game's startup error dialog (StartupErrorPatch): the changes since the last setup that worked, and
    // Restore and restart or Quit, with Mod Doctor's suspects if it found any. The game's own setup for its dialog first
    // (ErrorInit). Where the game's text can't load (a mod broke it), a plain dialog in English. If anything fails
    // before the choice, or the Restore fails, the game's own dialog instead.
    public static async Task AskAfterCrash(NGame game, Offer offer)
    {
        Log.Error($"[{ModEntry.ModId}] The game couldn't start; offering the mods of the last launch that worked ({offer.Working.At.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)})");
        bool restore;
        try
        {
            var styled = await ErrorInit(game);
            var suspects = await ModDoctor.Suspects(game);
            restore = styled ? await AskStyled(offer, suspects) : await AskPlain(game, offer, suspects);
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.ModId}] Could not offer the last working mods, so the game's own dialog shows: {e}");
            NModalContainer.Instance?.Clear();
            await StartupErrorPatch.GamesDialog(game);
            return;
        }
        // Answered, so this launch's record goes either way: after a Restore the restored launch's notice would offer the
        // same setup again, and after a Quit a fix made by hand would get that offer (the same mods failing again bring
        // this dialog back without the record).
        ClearPending();
        if (!restore)
        {
            ModEntry.Quit();
            return;
        }
        // The game didn't start: only its settings (the mod list) are written, then a plain quit and a relaunch, as its
        // own dialog quits (NGame.Quit would save the profile and progress of a half-started game).
        try
        {
            WriteList(offer.Working.Mods);
            ModEntry.Relaunch(plain: true);
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.ModId}] Could not restore the last working mods and restart, so the game's own dialog shows: {e}");
            // The answer runs inside our popup's button handler, before the popup's own close clears the container,
            // which would keep the game's popup out (it takes one at a time) and then clear it: a frame first.
            await game.ToSignal(game.GetTree(), SceneTree.SignalName.ProcessFrame);
            NModalContainer.Instance?.Clear();
            await StartupErrorPatch.GamesDialog(game);
        }
    }

    // The game's setup for its startup error dialog (its TryErrorInit) without its quit when that fails: the language
    // (false if a mod's patch on it throws: no game text, no game popup), the node ready, and the startup transition out
    // of the way. Its settings are read already: Begin took the mod list from them, and Restore writes it back.
    static async Task<bool> ErrorInit(NGame game)
    {
        var language = true;
        if (LocManager.Instance == null)
        {
            try
            {
                LocManager.Initialize();
            }
            catch (Exception e)
            {
                language = false;
                Log.Error($"[{ModEntry.ModId}] The game's text couldn't load after the failed start, so the offer is in plain English: {e.Message}");
            }
        }
        if (!game.IsNodeReady())
            await game.ToSignal(game, Node.SignalName.Ready);
        game.Transition.Visible = false;
#if DEVTOOLS
        if (TestNoLanguage)
            return false;
#endif
        return language && LocManager.Instance != null;
    }

    // The dialog's list: the changes, whether the game changed since, and the first few of Mod Doctor's suspects.
    static List<string> CrashLines(Offer offer, List<string> suspects, bool plain)
    {
        var lines = Lines(offer.Changes, plain);
        if (offer.GameChanged)
            lines.Add("\n" + Our(plain, "SPIRE_MOD_MANAGER.CRASH_GAME_CHANGED"));
        if (suspects.Count > 0)
        {
            var named = suspects.Take(SuspectsShown).Select(id => plain ? PlainName(id) : Name(id)).ToList();
            if (suspects.Count > SuspectsShown)
                named.Add(Our(plain, "SPIRE_MOD_MANAGER.LAUNCH_MORE", ("count", Count(plain, suspects.Count - SuspectsShown))));
            lines.Add("\n" + Our(plain, "SPIRE_MOD_MANAGER.CRASH_SUSPECTS", ("mods", string.Join(", ", named))));
        }
        return lines;
    }

    // The offer in the game's popup, our mark on it. Another mod's popup would keep ours out (the container takes one at
    // a time), so it goes; if ours still isn't the one shown, that's a failure (the game's dialog then).
    static async Task<bool> AskStyled(Offer offer, List<string> suspects)
    {
        var body = new LocString("settings_ui", "SPIRE_MOD_MANAGER.CRASH_BODY");
        body.Add("date", When(offer.Working));
        body.Add("changes", string.Join("\n", CrashLines(offer, suspects, plain: false)));
        if (NGenericPopup.Create() is not { } popup || NModalContainer.Instance is not { } modals)
            throw new InvalidOperationException("the game's popup can't be made");
        modals.Clear();
        Menus.Mark(popup);
        modals.Add(popup);
        if (modals.OpenModal != popup)
            throw new InvalidOperationException("the game's popup container didn't take ours");
        return await popup.WaitForConfirmation(body, new LocString("settings_ui", "SPIRE_MOD_MANAGER.CRASH_TITLE"),
            new LocString("main_menu_ui", "QUIT"), new LocString("settings_ui", "SPIRE_MOD_MANAGER.CRASH_RESTORE"));
    }

    public const string PlainDialogName = "SpireModManagerCrashDialog";

    // The offer where the game's text and popups can't be used: Godot's own dialog, with our English texts (they come
    // through the game's text too) and the game's word for Quit in English.
    static async Task<bool> AskPlain(NGame game, Offer offer, List<string> suspects)
    {
        var text = Our(true, "SPIRE_MOD_MANAGER.CRASH_BODY", ("date", offer.Working.At.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)),
            ("changes", string.Join("\n", CrashLines(offer, suspects, plain: true))));
        var dialog = new ConfirmationDialog
        {
            Name = PlainDialogName, Title = Our(true, "SPIRE_MOD_MANAGER.CRASH_TITLE"), DialogText = text, DialogAutowrap = true,
            OkButtonText = Our(true, "SPIRE_MOD_MANAGER.CRASH_RESTORE"), CancelButtonText = "Quit", Exclusive = true,
        };
        var answer = new TaskCompletionSource<bool>();
        dialog.Confirmed += () => answer.TrySetResult(true);
        dialog.Canceled += () => answer.TrySetResult(false);
        game.GetTree().Root.AddChild(dialog);
        dialog.PopupCentered(new Vector2I(760, 420));
        try
        {
            return await answer.Task;
        }
        finally
        {
            dialog.QueueFree();
        }
    }

    // This launch's record, gone (the dialog after its failed start was answered).
    static void ClearPending()
    {
        try
        {
            if (Dir != null)
                File.Delete(Path.Combine(Dir, PendingFile));
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.ModId}] Could not delete this launch's record after the failed start: {e.Message}");
        }
    }

    // The game's version and build, or null if it doesn't say.
    static string? GameNow()
    {
        try
        {
            return MegaCrit.Sts2.Core.Debug.ReleaseInfoManager.Instance.ReleaseInfo is { } info ? $"{info.Version} ({info.Commit})" : null;
        }
        catch (Exception)
        {
            return null;
        }
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
