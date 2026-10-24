using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Godot;
using MegaCrit.Sts2.Core.Audio;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.addons.mega_text;

namespace SpireModManager;

// Crash-safe launch. Our initializer records the mods this launch loads; reaching the main menu deletes the record and
// saves them as the last working setup. A record still there at the next launch means that launch never reached the
// menu, and a notice offers Restore (that setup, then a relaunch) or Keep. A launch the game reports it couldn't start
// (its startup error, StartupErrorPatch) gets our dialog at once, in place of the game's with only Quit: a mod that stops
// every launch never lets the menu come. It names the mod that stopped the start (Culprits) with Turn off and restart
// (TurnedOff keeps the record), offers the last working setup, or both. Nothing happens on its own, and every step fails
// open.
static class LaunchGuard
{
    // One copy. Copy: which copy it is (Copies.CopyOf), for every entry, so a later launch knows which copy was there
    // first (Before, for ties); none in a record from before copies were told apart, and only for a mod installed twice
    // from one source in one from before every entry had it. For such a mod Enabled says whether it's the copy that
    // loaded, not whether its box was ticked (our copies_off says that: two can be ticked, and only one loads). What loaded
    // is what a crash is about.
    public record Entry(string Id, ModSource Source, bool Enabled, string? Version,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Copy = null);

    // Game: the game's version and build (none in a record from before it was kept), so a crash after a game update can
    // say a setup from before it may not help.
    public record Setup(DateTime At, List<Entry> Mods, string? Game = null);

    // One difference, for the notice: Kind is New, Enabled, Disabled, Updated (From and To are the versions), Order, or
    // Switched (another copy of a mod installed twice loads; From and To are the copies).
    public enum Kind { New, Enabled, Disabled, Updated, Order, Switched }
    public record Change(Kind Kind, string Id, string? From = null, string? To = null);

    public const string PendingFile = "launch-pending.json", WorkingFile = "last-working.json";

    // Our records' JSON (TurnedOff's too): snake_case, enums by name
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter() },
    };

    // Taken in Init, and kept for the menu: the account folder is known by then (the game has read settings.save from
    // it), and the same folder clears the record whatever Store resolves later. Set before the launch record is written,
    // so a turn-off's record has its folder even where that write fails.
    public static string? Dir { get; private set; }
    static Setup? _failed, _working, _planned;
    static List<(string Id, ModSource Source, bool On)>? _read;

    // The mod list as this launch read it (Begin), for the self-test.
    public static IReadOnlyList<(string Id, ModSource Source, bool On)>? ReadList => _read;
    static bool _reached;

    // The notice after a launch that never reached the menu: shown once at the main menu, after any other popup.
    public static (Setup Working, List<Change> Changes)? Notice { get; set; }

    // The copies of each id the last launch's records name (the last working setup's, and a launch's that never reached
    // the menu), read in Begin: which copies were there before a launch, for ties between copies new to our record
    // (Copies.TieOrder, and a first split's order). Null before Begin.
    public static IReadOnlyDictionary<string, HashSet<string>>? Before { get; private set; }

#if DEVTOOLS
    // Self-test: as if the last launch's records named these copies.
    public static void TestBefore(IReadOnlyDictionary<string, HashSet<string>>? before) => Before = before;
#endif

    // The same read from a folder's records (SameIdLoad reads them at Init, before Begin): entries from before every
    // entry named its copy say nothing.
    public static Dictionary<string, HashSet<string>> CopiesBefore(string dir)
    {
        var before = new Dictionary<string, HashSet<string>>();
        foreach (var setup in new[] { PendingFile, WorkingFile }.Select(name => Read(Path.Combine(dir, name))))
            foreach (var entry in setup?.Mods ?? [])
                if (entry.Copy is { } copy)
                    (before.TryGetValue(entry.Id, out var copies) ? copies : before[entry.Id] = []).Add(copy);
        return before;
    }

    // In ModEntry.Init right after SameIdLoad.Apply, inside a try there too (a renamed game member throws at the call,
    // before this try). The mods in load order, each on or off by its own entry (the game's rebuilt list copies the first
    // copy's state), each naming its copy; a mod installed twice from one source an entry per copy, on for the copy this
    // launch loads (after Apply's swap, the first of them) and off for the rest, ticked or not.
    public static void Begin()
    {
        try
        {
            var off = Dependencies.SavedOff();
            var mods = ModManager.Mods;
            Dictionary<(string Id, ModSource Source), string> loads;
            try
            {
                loads = SameIdLoad.LoadsAtLaunch(mods);
            }
            catch (Exception e)
            {
                loads = []; // the record as before copies were told apart: each copy by its id and source's switch
                Log.Warn($"[{ModEntry.ModId}] Could not tell which copy of a mod installed twice this launch loads, so the launch record doesn't say: {e.Message}");
            }
            var planned = new Setup(DateTime.Now, mods.Select(m => m.manifest is { id: { } id } manifest
                ? new Entry(id, m.modSource, !off.Contains((id, m.modSource)) && (!loads.TryGetValue((id, m.modSource), out var copy) || copy == Copies.CopyOf(m)),
                    manifest.version, Copies.CopyOf(m))
                : null).OfType<Entry>().ToList(), GameNow());
            // The mod list as the game read it: after the mods load, it puts its own in place (sorted by dependencies).
            Start(Store.Dir, planned, SaveManager.Instance.SettingsSave.ModSettings?.ModList.Select(Key).ToList());
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.ModId}] Could not record this launch, so a launch that stops before the main menu won't be noticed: {e}");
        }
    }

    // This launch from a folder: Dir first, the list as read, the records (Begin), and the planned setup only once they're
    // in (the main menu clears a record only with it)
    static void Start(string dir, Setup planned, List<(string Id, ModSource Source, bool On)>? read)
    {
        Dir = dir;
        Before = CopiesBefore(dir);
        _read = read;
        (_failed, _working) = Begin(dir, planned);
        _planned = planned;
    }

#if DEVTOOLS
    // Self-test: this launch's start from another folder (Begin's catch around it), then this launch put back: whether Dir
    // was set, and whether the planned setup was
    public static (string? Dir, bool Planned) TestStart(string dir, Setup planned)
    {
        var kept = (Dir, Before, _read, _failed, _working, _planned);
        try
        {
            try
            {
                Start(dir, planned, _read);
            }
            catch (Exception)
            {
                // as Begin logs it
            }
            return (Dir, _planned == planned);
        }
        finally
        {
            (Dir, Before, _read, _failed, _working, _planned) = kept;
        }
    }
#endif

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
            // Mod Doctor turned off the mod that broke the last launch and restarted (from the game's dialog, its start fix):
            // its note at this menu says so, with a way to turn the mod back on, so ours would only say it again beside it.
            if (changes.Count > 0 && _failed is { } failed && ModDoctor.TurnedOffSince(failed.At))
                Log.Info($"[{ModEntry.ModId}] Mod Doctor turned a mod off after the last launch failed, and its note says so: no notice of ours about that launch");
            else if (changes.Count > 0 && _working is { } working) // Reached has changes only with a working setup
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

    // What a launch's mods changed since a working setup: mods new, enabled, disabled, switched to another copy or
    // updated, and whether the ones enabled in both load in another order. Each id and source by its copy that loaded
    // (the first, for a record from before copies were told apart, where every copy has its switch). A switch only where
    // the id and source was installed twice in either (one copy swapped for another is nothing Restore can undo). Never
    // ours (we were running, and an update of ours can't be undone) nor Mod Doctor's (it's there to look at failures;
    // Restore keeps it as it is).
    public static List<Change> Changes(IReadOnlyList<Entry> tried, IReadOnlyList<Entry> working)
    {
        static Dictionary<(string, ModSource), Entry> ByKey(IEnumerable<Entry> mods) =>
            mods.GroupBy(m => (m.Id, m.Source)).ToDictionary(g => g.Key, g => g.FirstOrDefault(m => m.Enabled) ?? g.First());
        var then = ByKey(working);
        static bool Twice(IReadOnlyList<Entry> mods, Entry mod) => mods.Count(m => m.Id == mod.Id && m.Source == mod.Source) > 1;
        var changes = new List<Change>();
        foreach (var mod in ByKey(tried.Where(m => !Ours(m.Id))).Values)
            if (!then.TryGetValue((mod.Id, mod.Source), out var was))
            {
                if (mod.Enabled)
                    changes.Add(new Change(Kind.New, mod.Id));
            }
            else if (mod.Enabled != was.Enabled)
                changes.Add(new Change(mod.Enabled ? Kind.Enabled : Kind.Disabled, mod.Id));
            else if (mod.Enabled && mod.Copy != null && was.Copy != null && mod.Copy != was.Copy && (Twice(tried, mod) || Twice(working, mod)))
                changes.Add(new Change(Kind.Switched, mod.Id, was.Copy, mod.Copy)); // Restore can undo it
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
                    new LocString("settings_ui", "SPIRE_MOD_MANAGER.LAUNCH_KEEP"), new LocString("settings_ui", "SPIRE_MOD_MANAGER.LAUNCH_RESTORE"),
                    popup => Links(popup, new Dictionary<string, string> { [MoreChanges] = Rest(changes) })))
                Restore(working.Mods, menu);
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.ModId}] Could not show the notice about the last launch: {e}");
        }
    }

    static string When(Setup setup) => When(setup.At);

    // A time as our notices say it: the date in the game's language, then the time
    public static string When(DateTime at) => $"{ModDetails.Date(at)}, {at.ToString("HH:mm", CultureInfo.InvariantCulture)}";

    public static string Escaped(string text) => text.Replace("[", "[lb]");

    static string Name(string id) => Escaped(PlainName(id));

    // The changes as the popups list them, a line each, the last ones counted in one line if there are more than most.
    // Plain: for where the game's text couldn't load, in English from our own file and without markup (Our).
    static List<string> Lines(IReadOnlyList<Change> changes, bool plain = false, int most = LinesShown)
    {
        var shown = changes.Count > most ? most - 1 : most;
        var lines = changes.Take(shown).Select(c => Line(c, plain)).ToList();
        if (changes.Count > most)
            lines.Add("  •  " + More(plain, MoreChanges, changes.Count - shown));
        return lines;
    }

    // The changes the list counts in its last line rather than lists: the card over that line.
    static string Rest(IReadOnlyList<Change> changes, int most = LinesShown) =>
        changes.Count > most ? string.Join("\n", changes.Skip(most - 1).Select(c => Line(c, plain: false))) : "";

    // "{count} more", for the changes, Mod Doctor's suspects or the mods a dialog doesn't list: a link whose card names
    // them, in the game's popup (Links); plain text in the plain dialog, which can't show a card
    public const string MoreChanges = "spire-mod-manager-more-changes", MoreSuspects = "spire-mod-manager-more-suspects",
        MoreMods = "spire-mod-manager-more-mods", MoreNeeding = "spire-mod-manager-more-needing", MoreOff = "spire-mod-manager-more-off";
    public const string MoreCardName = "SpireModManagerMoreCard";

    public static string More(bool plain, string link, int count)
    {
        var text = Our(plain, "SPIRE_MOD_MANAGER.LAUNCH_MORE", ("count", Count(plain, count)));
        return plain ? text : $"[url={link}]{text}[/url]";
    }

    // The game's popup's text: its "more" links show what they stand for, a card by link. The text box takes the mouse
    // for that (the game's leaves it to the popup).
    public static void Links(NGenericPopup popup, IReadOnlyDictionary<string, string> cards)
    {
        if (popup.GetNodeOrNull<RichTextLabel>("VerticalPopup/Description") is not { } text)
            return;
        text.MouseFilter = Control.MouseFilterEnum.Pass;
        Hover.WatchLinks(text, MoreCardName, link => cards.TryGetValue(link, out var card) && card.Length > 0 ? card : null, width: 420);
    }

    // The game's popup's text, as given after its own setup: it takes one loc entry, and ours is put together from several
    // whole ones. Its auto-size measures the text as one paragraph, without the room our boxes take (GameText), so the
    // size it picks can run the text past the label: it's made smaller until it fits as laid out (FitBody).
    public static void SetBody(NGenericPopup popup, string body)
    {
        if (popup.GetNodeOrNull<MegaRichTextLabel>("VerticalPopup/Description") is not { } words)
            return;
        words.SetTextAutoSize(body);
        try
        {
            FitBody(words);
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.ModId}] Could not fit a popup's text to it, so it may run past its edge: {e.Message}");
        }
    }

    // The smallest size the right button's words are meant for: a mod's name in them is cut to fit at it (FittedYes)
    public const int SmallestButtonText = 14;

    static readonly StringName[] TextSizes = ["normal_font_size", "bold_font_size", "bold_italics_font_size", "italics_font_size", "mono_font_size"];

    // The text at the largest size, from the auto-size's own down, at which it fits the label as laid out now (its boxes'
    // gaps as they'll be drawn), made the auto-size's largest too: its own pass comes later (deferred) and keeps to it
    static void FitBody(MegaRichTextLabel words)
    {
        if (words.Size.Y <= 0)
            return; // not laid out: nothing to measure against
        GameText.Fit(words);
        void Sized(int size)
        {
            foreach (var name in TextSizes)
                words.AddThemeFontSizeOverride(name, size);
        }
        var fits = words.MaxFontSize;
        Sized(fits);
        while (words.GetContentHeight() > words.Size.Y && fits > words.MinFontSize)
            Sized(--fits);
        words.MaxFontSize = fits;
    }

    // One change as the popups list it.
    static string Line(Change c, bool plain)
    {
        string Mod(string id) => plain ? PlainName(id) : Name(id);
        string Version(string? version) => plain ? version ?? "" : Escaped(version ?? "");
        return "  •  " + c.Kind switch
        {
            Kind.New => Our(plain, "SPIRE_MOD_MANAGER.LAUNCH_NEW", ("mod", Mod(c.Id))),
            Kind.Enabled => Our(plain, "SPIRE_MOD_MANAGER.LAUNCH_ENABLED", ("mod", Mod(c.Id))),
            Kind.Disabled => Our(plain, "SPIRE_MOD_MANAGER.LAUNCH_DISABLED", ("mod", Mod(c.Id))),
            Kind.Updated => Our(plain, "SPIRE_MOD_MANAGER.LAUNCH_UPDATED", ("mod", Mod(c.Id)), ("from", Version(c.From)), ("to", Version(c.To))),
            // The copies by Workshop item id, or folder for a local one.
            Kind.Switched => Our(plain, "SPIRE_MOD_MANAGER.LAUNCH_SWITCHED", ("mod", Mod(c.Id)), ("from", Version(Copies.Shown(c.From ?? ""))),
                ("to", Version(Copies.Shown(c.To ?? "")))),
            _ => Our(plain, "SPIRE_MOD_MANAGER.LAUNCH_ORDER"),
        };
    }

    static string PlainName(string id) => ModManager.Mods.FirstOrDefault(m => m.manifest?.id == id)?.manifest?.name ?? id;

    static string Count(bool plain, int count) => plain ? count.ToString(CultureInfo.InvariantCulture) : Menus.Number(count);

    // One of our texts, filled in: through the game's text, or (plain) in English from our own file without markup, for
    // where the game's text couldn't load. Names and versions go in as given (Lines escapes them for the game's markup).
    public static string Our(bool plain, string key, params (string Name, string Value)[] values)
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
    // loads), ours always on (Written says why); then a relaunch through the relaunch check (owner: the main menu),
    // which catches a mod left on that now needs one installed since (its dialog offers the fix). No relaunch when the
    // game couldn't save it.
    public static void Restore(IReadOnlyList<Entry> working, Node owner)
    {
        try
        {
            WriteList(working);
        }
        catch (SaveFailed e)
        {
            Log.Error($"[{ModEntry.ModId}] Could not restore the last working mods: the game couldn't save its settings ({e.Message}), so nothing was changed");
            return;
        }
        RelaunchCheck.Relaunch(owner);
    }

    // Our data first (every other copy of the groups that loaded a copy off, so the copy that worked loads: OffOf), then
    // the game's list, as Dependencies.Write saves, the save checked. A save that fails puts the list back and throws
    // (SaveFailed); our copies_off stays, which is logged: it only picks which ticked copy loads, the one that worked.
    static void WriteList(IReadOnlyList<Entry> working)
    {
        WriteOff(working);
        var settings = SaveManager.Instance.SettingsSave.ModSettings ??= new ModSettings();
        var (asWas, entries) = (State(settings.ModList), Written(working));
        settings.ModList.Clear();
        settings.ModList.AddRange(entries);
        try
        {
            SaveChecked(settings.ModList, asWas);
        }
        catch (SaveFailed)
        {
            Log.Warn($"[{ModEntry.ModId}] The restored mod list wasn't saved, but which copy of a mod installed twice loads is (copies_off): the one that worked");
            throw;
        }
        Log.Info($"[{ModEntry.ModId}] Restored the mod list of the last launch that worked");
    }

    // The mod list a Restore writes: the working setup's, in its order and on or off (copies of one id and source share
    // a switch: on if one of them was), ours first (LoadOrder.OursFirst); mods installed since then off (a mod the list
    // doesn't name loads); Mod Doctor as it is now (it's there to look at the failure; as the game read it, before the
    // first main menu). Every mod turned off after a failed start off, ours and Mod Doctor's (TurnedOff.OffIds): Restore
    // must not turn a culprit back on, or the start fails again and offers Restore once more; Mod Doctor too, when an
    // entry of ours names it. Every copy of ours on, whatever the player had: the restored launch needs us running, as
    // our Init is what loads the copies that worked (the ticks WriteOff puts back) and what notices if it fails again. All
    // of them, so one still loads if the copy that ran was removed since, though on the beta that can make a higher
    // Workshop copy of ours run in place of a local one.
    public static List<SettingsSaveMod> Written(IReadOnlyList<Entry> working)
    {
        var (off, read, kept) = (Dependencies.SavedOff(), SameIdLoad.Settled ? null : _read, TurnedOff.OffIds());
        var switches = working.GroupBy(m => (m.Id, m.Source)).ToDictionary(g => g.Key, g => g.Any(m => m.Enabled));
        bool On(string id, ModSource source, bool then) => id == ModEntry.ModId || !kept.Contains(id)
            && (id == ModDoctor.ModId ? read != null ? Culprits.OnAsRead(id, source, read) : !off.Contains((id, source)) : then);
        var entries = working.Select(m => new SettingsSaveMod { Id = m.Id, Source = m.Source, IsEnabled = On(m.Id, m.Source, switches[(m.Id, m.Source)]) }).ToList();
        foreach (var m in ModManager.Mods.Union(Installed.Mods))
            if (m.manifest?.id is { } id && !working.Any(w => w.Id == id && w.Source == m.modSource))
                entries.Add(new SettingsSaveMod(m) { IsEnabled = On(id, m.modSource, false) });
        LoadOrder.OursFirst(entries);
        return entries;
    }

    // The mods Mod Doctor suspects that the third answer turns off: not ours, nor Mod Doctor (it's there to look at the
    // failure).
    public static List<string> Disableable(IReadOnlyList<string> suspects) => [.. suspects.Where(id => !Ours(id)).Distinct()];

    static bool _repairTried;

    // Whether RepairBeforeMenu got to the list in memory this session (before the first main menu, with the list as the
    // game read it): the self-test's failed start reads it.
    public static bool RepairedBeforeMenu { get; private set; }

    // Before the first main menu the game's mod list in memory is the one it rebuilt: every copy of an id has the id's
    // first entry's switch (it matches by id), and only the first main menu puts each source's own back and saves it
    // (SameIdLoad.Settle). Saved before that as it is (at a failed start, Disable suspects or another mod's answer; at
    // any time, the window's close, through the game's NGame.Quit), it would turn another mod off, or load another copy
    // of it. So once, as a startup error shows or the game quits before the menu, the same repair in memory, nothing
    // saved: every save from then on writes the switches the game read. Not earlier: the menu's Settle saves only a list
    // that still needs it.
    public static void RepairBeforeMenu()
    {
        if (_repairTried)
            return;
        _repairTried = true;
        var settings = SaveManager.Instance.SettingsSave.ModSettings;
        RepairedBeforeMenu = !SameIdLoad.Settled && settings != null && ReadList != null;
        if (RepairBeforeMenu(settings, ReadList, SameIdLoad.Settled))
            Log.Info($"[{ModEntry.ModId}] Before the first main menu: put each source's own switch back in the game's mod list in memory, as the menu "
                + "would, so a save from here keeps every other mod as it was");
    }

    // The repair on given lists (the self-test's made-up ones): none after the first main menu (Settle did it), nor
    // without the list as the game read it. True if anything changed.
    public static bool RepairBeforeMenu(ModSettings? settings, IReadOnlyList<(string Id, ModSource Source, bool On)>? read, bool settled)
    {
        if (settled || settings == null)
            return false;
        if (read == null)
        {
            Log.Warn($"[{ModEntry.ModId}] Before the first main menu: no list as the game read it, so the mod list in memory stays as the game rebuilt it");
            return false;
        }
        return SameIdLoad.SettleList(settings.ModList, read);
    }

    // What a turn-off turns off: each mod with why (a dependent: the culprit it needs), and the failure's error and
    // signature for their record
    public record Off(IReadOnlyList<(string Id, Culprits.Why Why, string? Needs)> Mods, string Error, string Signature);

    // A write that failed: the dialog's line for it, and the mods it names
    public record Failed(string Key, IReadOnlyList<string> Ids);

    // A settings save the game couldn't make
    public sealed class SaveFailed(string message) : IOException(message);

    // The culprit's turn-off: its mods with its why, and the mods that need one of them (Needs, naming the one). Ours
    // alone for ours: the mods that need us stay as they are, so turning us back on brings them all back.
    public static Off OffOf(Culprits.Culprit culprit, Culprits.Context mods) => culprit.Why == Culprits.Why.Self
        ? new([(ModEntry.ModId, culprit.Why, null)], culprit.Error, culprit.Signature)
        : new([.. culprit.Ids.Select(id => (id, culprit.Why, (string?)null)),
            .. culprit.Needing.Select(id => (id, Culprits.Why.Needs, (string?)(culprit.Ids.FirstOrDefault(c => mods.Requires(id, c)) ?? culprit.Ids[0])))],
            culprit.Error, culprit.Signature);

    // Turn off all mods, from the third failed start in a row: every installed mod but ours and Mod Doctor (it's there to
    // look at the failure), as one group
    public static Off AllOff(string error, string signature) => new([.. ModManager.Mods.Union(Installed.Mods).Select(m => m.manifest?.id)
        .OfType<string>().Where(id => id.Length > 0 && !Ours(id)).Distinct().Select(id => (id, Culprits.Why.All, (string?)null))], error, signature);

    // The turn-off of the dialog's answer: the repair as the error showed (here too, should that prefix not have run),
    // then on the game's list in memory, the list as it read it, and whether the first main menu settled it
    static Failed? TurnOff(Off off)
    {
        RepairBeforeMenu();
        return TurnOff(SaveManager.Instance.SettingsSave.ModSettings ??= new ModSettings(), _read, SameIdLoad.Settled, off);
    }

    // A turn-off (Mod Doctor's f0ec0e1 is the same design). Only mods with a copy on: as the game read them before the
    // first main menu, as the list is after it, a copy with no entry on as the game loads it; the rest are logged (they're
    // off, and an entry would have the notice name them as turned off). Then the repair on the list given
    // (RepairBeforeMenu: it changes nothing on a list already repaired), each mod's switches recorded, every switch of each
    // off (SwitchOff), the save checked (SaveChecked), and only after a good save their entries (TurnedOff.Add; none for
    // ours, which won't be running to show it). A record that can't be written undoes the turn-off: a mod off with no entry
    // has no notice and no Turn back on, and Restore would turn it back on. Then the restart; one that fails keeps the
    // turn-off, saved and recorded. Null when done, else what failed. Every step reads the lists given (the self-test's
    // made-up ones) but the save, which writes the game's own settings: settings is its ModSettings.
    public static Failed? TurnOff(ModSettings settings, IReadOnlyList<(string Id, ModSource Source, bool On)>? read, bool settled, Off off)
    {
        if (!settled && read == null)
            throw new InvalidOperationException("the mod list as the game read it isn't known, so a save would change other mods");
        IReadOnlyList<(string Id, ModSource Source, bool On)> asRead = settled ? [.. settings.ModList.Select(Key)] : read ?? [];
        var installed = ModManager.Mods.Union(Installed.Mods).Where(m => m.manifest?.id is { Length: > 0 }).ToList();
        bool On(string id) => installed.Any(m => m.manifest?.id == id && Culprits.OnAsRead(id, m.modSource, asRead));
        var mods = off.Mods.Where(m => On(m.Id)).DistinctBy(m => m.Id).ToList();
        if (off.Mods.Select(m => m.Id).Where(id => !On(id)).Distinct().ToList() is { Count: > 0 } already)
            Log.Info($"[{ModEntry.ModId}] Already off, so not turned off again or recorded: {string.Join(", ", already)}");
        var named = off.Mods.Where(m => m.Why != Culprits.Why.Needs).Select(m => m.Id).Distinct().ToList();
        var what = string.Join(", ", named);
        RepairBeforeMenu(settings, read, settled);
        var asWas = State(settings.ModList);
        var at = DateTimeOffset.Now;
        var entries = mods.Where(m => m.Id != ModEntry.ModId).Select(m => Recorded(m, at, off, installed, asRead)).ToList();
        SwitchOff(settings.ModList, mods.Select(m => m.Id).ToHashSet(), installed);
        try
        {
            SaveChecked(settings.ModList, asWas);
        }
        catch (SaveFailed e)
        {
            Log.Error($"[{ModEntry.ModId}] Could not turn off {what}: the game couldn't save its settings ({e.Message}), so nothing was changed");
            return new Failed("SPIRE_MOD_MANAGER.CRASH_SAVE_FAILED", named);
        }
        if (entries.Count > 0)
        {
            try
            {
                TurnedOff.Add(entries);
            }
            catch (Exception e)
            {
                PutBack(settings.ModList, asWas);
                try
                {
                    SaveChecked(settings.ModList, asWas);
                    Log.Error($"[{ModEntry.ModId}] Could not record turning off {what}, so it's undone: {e.Message}");
                    return new Failed("SPIRE_MOD_MANAGER.CRASH_RECORD_FAILED", named);
                }
                catch (SaveFailed again)
                {
                    Log.Error($"[{ModEntry.ModId}] Could not record turning off {what} ({e.Message}), nor turn it back on ({again.Message}): it may stay off");
                    return new Failed("SPIRE_MOD_MANAGER.CRASH_RECORD_FAILED_OFF", named);
                }
            }
        }
        Log.Info($"[{ModEntry.ModId}] Turned off after the failed start: {string.Join(", ", mods.Select(m => $"{m.Id} ({m.Why})"))}");
        ClearPending();
        try
        {
            ModEntry.Relaunch(plain: true);
            return null;
        }
        catch (Exception e)
        {
            OS.SetRestartOnExit(false);
            Log.Error($"[{ModEntry.ModId}] Could not restart after turning off {what}, which stays off: {e}");
            return new Failed("SPIRE_MOD_MANAGER.CRASH_RESTART_FAILED", named);
        }
    }

    // A mod's record: the copy that ran (else one on as read), its name and version, why, and each source of its
    // installed copies, on as read, with its copies (Turn back on reads the switch, didn't stick the copies). Only a
    // culprit's has the failure's error and signature: a dependent's start didn't fail that way.
    static TurnedOff.Entry Recorded((string Id, Culprits.Why Why, string? Needs) mod, DateTimeOffset at, Off off, List<Mod> installed,
        IReadOnlyList<(string Id, ModSource Source, bool On)> asRead)
    {
        var copies = installed.Where(m => m.manifest?.id == mod.Id).ToList();
        var ran = copies.FirstOrDefault(m => m.state == ModLoadState.Loaded) ?? copies.FirstOrDefault(m => Culprits.OnAsRead(mod.Id, m.modSource, asRead));
        var culprit = mod.Why != Culprits.Why.Needs;
        return new TurnedOff.Entry(at, mod.Id, ran?.manifest?.name, ran?.manifest?.version, ran == null ? null : Copies.CopyOf(ran), mod.Why, mod.Needs,
            culprit ? off.Error : null, culprit ? off.Signature : null,
            [.. copies.GroupBy(m => m.modSource).Select(g => new Culprits.Switch(g.Key, Culprits.OnAsRead(mod.Id, g.Key, asRead), [.. g.Select(Copies.CopyOf).Distinct()]))]);
    }

    // Every saved entry of each id off, of each source with an installed copy (one of a source with none can't load, and
    // Turn back on, which goes by the installed sources, couldn't put it back); an installed copy with no entry gets one,
    // off, or it would load
    static void SwitchOff(List<SettingsSaveMod> list, IReadOnlySet<string> ids, IReadOnlyList<Mod> installed)
    {
        foreach (var entry in list.Where(e => ids.Contains(e.Id) && installed.Any(m => m.manifest?.id == e.Id && m.modSource == e.Source)))
            entry.IsEnabled = false;
        foreach (var m in installed)
            if (m.manifest?.id is { } id && ids.Contains(id) && !list.Any(e => e.Id == id && e.Source == m.modSource))
                list.Add(new SettingsSaveMod(m) { IsEnabled = false });
    }

    // A mod list's entries with their switches, to put back as they were
    public static List<(SettingsSaveMod Entry, bool On)> State(List<SettingsSaveMod> list) => [.. list.Select(e => (e, e.IsEnabled))];

    static void PutBack(List<SettingsSaveMod> list, List<(SettingsSaveMod Entry, bool On)> state)
    {
        list.Clear();
        foreach (var (entry, on) in state)
        {
            entry.IsEnabled = on;
            list.Add(entry);
        }
    }

#if DEVTOOLS
    // Self-test: asked before each checked save, true to have it fail as a read-only settings.save does
    public static Func<bool>? TestSaveFails;
#endif

    // The game's settings saved, and the save checked: the game catches its own failure and only logs it ("Failed to save
    // settings"), so that line is listened for around the call (Compat.LogCallback; without it the save goes unchecked,
    // and the next launch's didn't-stick check is left). A failure, or a throw, puts the list back as it was (asWas) and
    // throws SaveFailed.
    public static void SaveChecked(List<SettingsSaveMod> list, List<(SettingsSaveMod Entry, bool On)> asWas)
    {
        string? failure = null;
        Action<LogLevel, string, int> watch = (level, text, _) =>
        {
            if (level >= LogLevel.Error && text.Contains("Failed to save settings"))
                failure ??= text.Split('\n')[0];
        };
        Compat.LogCallback?.AddEventHandler(null, watch);
        try
        {
#if DEVTOOLS
            if (TestSaveFails?.Invoke() == true)
                throw new IOException("Failed to save settings (the self-test's)");
#endif
            SaveManager.Instance.SaveSettings();
        }
        catch (Exception e)
        {
            failure ??= e.Message;
        }
        finally
        {
            Compat.LogCallback?.RemoveEventHandler(null, watch);
        }
        if (failure == null)
            return;
        PutBack(list, asWas);
        throw new SaveFailed(failure);
    }

    // For each id and source a working setup loaded a copy of (not ours), every other copy of it off, so the copy that
    // worked is the only one ticked and loads again (the highest ticked version would load otherwise): the setup's other
    // copies, our record's of that id and source (record: StoreData.Copies, or the file's before our data loads), and the
    // ones installed now, so neither a copy installed since nor one missing then and now can win when it's back. The
    // groups it's for, and their copies_off keys, sorted.
    public static (HashSet<(string Id, ModSource Source)> Groups, List<string> Off) OffOf(IReadOnlyList<Entry> working,
        IReadOnlyDictionary<string, List<string>>? record)
    {
        var groups = new HashSet<(string, ModSource)>();
        var off = new List<string>();
        var installed = ModManager.Mods.Union(Installed.Mods).ToList();
        foreach (var loaded in working.Where(m => m.Enabled && m.Copy != null && !Ours(m.Id)).DistinctBy(m => (m.Id, m.Source)))
        {
            var others = working.Where(m => m.Id == loaded.Id && m.Source == loaded.Source).Select(m => m.Copy).OfType<string>()
                .Concat(record?.GetValueOrDefault(loaded.Id)?.Where(c => Copies.SourceOf(c) == loaded.Source) ?? [])
                .Concat(installed.Where(m => m.manifest?.id == loaded.Id && m.modSource == loaded.Source).Select(Copies.CopyOf))
                .Where(c => c != loaded.Copy).Distinct().ToList();
            if (others.Count == 0)
                continue;
            groups.Add((loaded.Id, loaded.Source));
            off.AddRange(others.Select(c => $"{loaded.Id}|{c}"));
        }
        return (groups, [.. off.Order(StringComparer.Ordinal)]);
    }

    // copies_off with these groups' keys replaced.
    static IEnumerable<string> Replaced(IEnumerable<string> off, HashSet<(string Id, ModSource Source)> groups, IEnumerable<string> with) =>
        off.Where(k => Copies.Parse(k) is not { } key || !groups.Contains((key.Id, key.Source))).Concat(with);

    // The working setup's copies ticked again, alone in their groups (OffOf): through our data where it's loaded, else in
    // its file (a failed start, before the main menu loads it). A read-only session keeps the ticks it has.
    static void WriteOff(IReadOnlyList<Entry> working)
    {
        if (!Store.IsLoaded)
        {
            PatchOff(Store.Dir, working);
            return;
        }
        if (OffOf(working, Store.Data.Copies) is not { Groups.Count: > 0 } restored)
            return;
        if (Store.ReadOnly)
        {
            Log.Warn($"[{ModEntry.ModId}] Our data can't be saved this session, so the restored setup loads the copies ticked now: "
                + string.Join(", ", restored.Groups.Select(g => g.Id)));
            return;
        }
        // A new list: Ticks reads it again only then.
        Store.Data.CopiesOff = Store.TidyOff(Replaced(Store.Data.CopiesOff ?? Copies.LegacyOff(Store.Data.CopyPicks, Store.Data.Copies), restored.Groups, restored.Off));
        Store.SaveViewState();
    }

    // The same in manager.json in a folder, the rest of it as it is (Store.PatchFile): a file from before copies_off first
    // gets the reading the page would make of it (Copies.LegacyOff, from its copy_picks and copies), so its other groups
    // keep theirs; then the restored groups' keys go in, and copy_picks goes. Nothing to restore: the file as it is. True
    // if saved, or nothing to.
    public static bool PatchOff(string dir, IReadOnlyList<Entry> working) => Store.PatchFile(dir, json =>
    {
        static List<string>? Strings(JsonNode? node) => node is JsonArray array
            ? [.. array.Select(p => p?.GetValueKind() == JsonValueKind.String ? (string?)p : null).OfType<string>()] : null;
        using var doc = JsonDocument.Parse(json.ToJsonString());
        var record = Store.RecordIn(doc.RootElement);
        if (OffOf(working, record) is not { Groups.Count: > 0 } restored)
            return false;
        var off = Strings(json["copies_off"]) ?? Copies.LegacyOff(Strings(json["copy_picks"]), record);
        json["copies_off"] = new JsonArray([.. (Store.TidyOff(Replaced(off, restored.Groups, restored.Off)) ?? []).Select(k => (JsonNode?)JsonValue.Create(k))]);
        json.Remove("copy_picks");
        return true;
    });

    // What the game couldn't start with, offered back: the last working setup, what this launch changed since, and
    // whether the game itself changed since (then the old setup may not help).
    public record Offer(Setup Working, List<Change> Changes, bool GameChanged);

    // An offer when Restore would change what the game loads: this launch changed mods since the last working setup
    // beyond updates (Restore can't undo one), and the list it would write loads differently from the list this launch
    // read. Not after a Restore whose launch failed too, nor for an order the game's own sorting sets (a dependency an
    // update added): offering it would loop, Restore and the same failure again.
    public static Offer? CrashOffer() => CrashOffer(_planned, _working, _read);

    // A switch to another copy of a mod changes what loads with the game's list the same (which of them loads is ours).
    public static Offer? CrashOffer(Setup? planned, Setup? working, IReadOnlyList<(string Id, ModSource Source, bool On)>? read) =>
        CrashOffer(planned, working) is { } offer && read != null
            && (offer.Changes.Any(c => c.Kind == Kind.Switched) || !SameLoad([.. Written(offer.Working.Mods).Select(Key)], read)) ? offer : null;

    static (string, ModSource, bool) Key(SettingsSaveMod entry) => (entry.Id, entry.Source, entry.IsEnabled);

    // Whether two mod lists have a launch load the same: the same mods on, in the same order (the game's tie-break
    // after dependencies), and every mod on or off alike (a mod a list doesn't name loads). Off mods' order doesn't count.
    public static bool SameLoad(IReadOnlyList<(string Id, ModSource Source, bool On)> a, IReadOnlyList<(string Id, ModSource Source, bool On)> b) =>
        a.Where(e => e.On).Select(e => (e.Id, e.Source)).SequenceEqual(b.Where(e => e.On).Select(e => (e.Id, e.Source)))
        && a.ToHashSet().SetEquals(b);

    // The offer from a launch's record and the working setup: none without either, or with only updates since. A mod
    // turned off after a failed start (turnedOff; null: ours and Mod Doctor's now) being off isn't a change: Restore keeps
    // it off, and an offer of nothing else would loop.
    public static Offer? CrashOffer(Setup? planned, Setup? working, IReadOnlySet<string>? turnedOff = null)
    {
        if (planned == null || working == null)
            return null;
        var kept = turnedOff ?? TurnedOff.OffIds();
        var changes = Changes(planned.Mods, working.Mods).Where(c => !(c.Kind == Kind.Disabled && kept.Contains(c.Id))).ToList();
        if (!changes.Any(c => c.Kind != Kind.Updated))
            return null;
        return new Offer(working, changes, working.Game != null && planned.Game != null && working.Game != planned.Game);
    }

    // Mod Doctor's suspects named, a culprit's dependents, the mods already off and a failure's: the first few, then how many
    // more (Mod Doctor can name every mod patching the failing step)
    const int SuspectsShown = 3;

    const int StreakForAllOff = 3; // a game update that breaks ten mods shouldn't cost ten failed starts

#if DEVTOOLS
    // Self-test: as if the game's text couldn't load, for the plain dialog.
    public static bool TestNoLanguage;

    // Self-test: the takeover throwing before it hands the game its task (StartupErrorPatch's try), once past its undo
    public static bool TestTakeoverThrows;
#endif

    // The dialog's answers. TurnOff: the culprit, and the mods that need it; AllOff: every mod but ours and Mod Doctor.
    public enum Answer { Quit, TurnOff, Restore, DisableSuspects, AllOff }

    // A failed start's offer: the last working setup back (Offer), and the mod that stopped it (Culprit), with this
    // launch's setups (the Restore link's rule, the updated line's date) and the failure's error and signature (for the
    // record of Disable suspects and Turn off all mods)
    public record Crash(Offer? Offer, Culprits.Culprit? Culprit, Setup? Planned, Setup? Working, string Error = "", string Signature = "");

    // The game's own line as its startup error starts: ours skips the method that writes it, and bug reports search for it
    const string GamesLine = "Encountered error on game startup! Attempting to show error dialog";

    // In place of the game's startup error dialog (StartupErrorPatch). A start that failed after its first main menu (in
    // the checks after it) first takes back what that menu recorded.
    public static Task AskAfterCrash(NGame game)
    {
        if (_reached)
            UndoMenuReached();
#if DEVTOOLS
        if (TestTakeoverThrows)
            throw new InvalidOperationException("the self-test's takeover that throws");
#endif
        return AskAfterCrash(game, null);
    }

#if DEVTOOLS
    // Self-test: the takeover (call) as a start that failed after its first main menu in another folder, whose last
    // working setup from before this launch is working; this launch's put back after
    public static void TestTakeover(string dir, Setup? working, Action call)
    {
        var kept = (Dir, _working, _reached);
        (Dir, _working, _reached) = (dir, working, true);
        try
        {
            call();
        }
        finally
        {
            (Dir, _working, _reached) = kept;
        }
    }
#endif

    // After a start that failed past its first main menu: the last working setup as it was before this launch (none if
    // there was none), so the next launch doesn't compare against a launch that failed, and the turned-off entries' marks
    // that menu set taken back. The list in memory is Settle's, saved: nothing to repair.
    static void UndoMenuReached()
    {
        try
        {
            if (Dir != null)
                PutWorking(Dir, _working);
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.ModId}] Could not put the last working setup back after a start that failed past the main menu: {e.Message}");
        }
        try
        {
            TurnedOff.UndoMenuMarks();
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.ModId}] Could not take back the main menu's marks on the mods turned off after a failed start: {e.Message}");
        }
    }

    // A folder's last working setup written back as it was, or deleted where there was none
    public static void PutWorking(string dir, Setup? working)
    {
        var file = Path.Combine(dir, WorkingFile);
        if (working == null)
            File.Delete(file);
        else
            Write(file, working);
    }

    // The dialog, after the game's own line. given: the self-test's made-up crash; else this launch's, weighed once Mod
    // Doctor's findings and the failure are in (Weigh). With nothing to offer, the game's own dialog (Mod Doctor's box
    // shows there). The answer is then written: a write that fails shows the dialog once more, saying what went wrong,
    // with Quit alone. Where the game's text can't load (a mod broke it), a plain dialog in English. Anything else that
    // fails, before the choice or in writing it, gives the game's own dialog.
    public static async Task AskAfterCrash(NGame game, Crash? given)
    {
        Log.Error(GamesLine);
        var styled = false;
        Crash? crash = null;
        IReadOnlyList<string> suspects = [];
        var answer = Answer.Quit;
        try
        {
            styled = await ErrorInit(game);
            var findings = await ModDoctor.WaitForFindings(game);
            suspects = findings.Suspects;
            crash = given ?? await Weigh(game, findings);
            if (crash.Offer != null || crash.Culprit != null)
                answer = await Ask(game, styled, crash, suspects, null);
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.ModId}] Could not offer anything after the failed start, so the game's own dialog shows: {e}");
            NModalContainer.Instance?.Clear();
            await StartupErrorPatch.GamesDialog(game);
            return;
        }
        if (crash is not { } asked || asked.Offer == null && asked.Culprit == null)
        {
            Log.Info($"[{ModEntry.ModId}] Nothing to offer after the failed start, so the game's own dialog shows");
            await StartupErrorPatch.GamesDialog(game);
            return;
        }
        // Answered, so this launch's record goes either way: after a Restore the restored launch's notice would offer the
        // same setup again, and after a Quit a fix made by hand would get that offer (the same mods failing again bring
        // this dialog back without the record).
        ClearPending();
        if (answer == Answer.Quit)
        {
            ModEntry.Quit();
            return;
        }
        Failed? failed;
        try
        {
            failed = Write(answer, asked, suspects);
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.ModId}] Could not write the answer ({answer}) and restart, so the game's own dialog shows: {e}");
            await AfterOurPopup(game);
            await StartupErrorPatch.GamesDialog(game);
            return;
        }
        if (failed == null)
            return;
        // Once more with Quit alone: the other answers would fail the same way (Restore writes the same settings.save, and
        // our folder holds Restore's copies_off too)
        try
        {
            await AfterOurPopup(game);
            await Ask(game, styled, asked, suspects, failed);
            ModEntry.Quit();
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.ModId}] Could not say what went wrong after the failed start, so the game's own dialog shows: {e}");
            NModalContainer.Instance?.Clear();
            await StartupErrorPatch.GamesDialog(game);
        }
    }

    // The answer runs inside our popup's button handler, before the popup's own close clears the container, which would
    // keep the next popup out (it takes one at a time) and then clear it: a frame first.
    static async Task AfterOurPopup(NGame game)
    {
        await game.ToSignal(game.GetTree(), SceneTree.SignalName.ProcessFrame);
        NModalContainer.Instance?.Clear();
    }

    // The answer written. The game didn't start: only its settings (the mod list) are written, then a plain quit and a
    // relaunch, as its own dialog quits (NGame.Quit would save the profile and progress of a half-started game). Null
    // when done, else what failed.
    static Failed? Write(Answer answer, Crash crash, IReadOnlyList<string> suspects)
    {
        switch (answer)
        {
            case Answer.Restore when crash.Offer is { } offer:
                try
                {
                    WriteList(offer.Working.Mods);
                }
                catch (SaveFailed e)
                {
                    Log.Error($"[{ModEntry.ModId}] Could not restore the last working mods: the game couldn't save its settings ({e.Message}), so nothing was changed");
                    return new Failed("SPIRE_MOD_MANAGER.CRASH_RESTORE_FAILED", []);
                }
                ModEntry.Relaunch(plain: true);
                return null;
            case Answer.TurnOff when crash.Culprit is { } culprit:
                return TurnOff(OffOf(culprit, Culprits.Context.Live()));
            case Answer.DisableSuspects:
                return TurnOff(new Off([.. Disableable(suspects).Select(id => (id, Culprits.Why.Suspect, (string?)null))], crash.Error, crash.Signature));
            case Answer.AllOff:
                return TurnOff(AllOff(crash.Error, crash.Signature));
            default:
                throw new InvalidOperationException($"nothing to write for {answer}");
        }
    }

    // This launch's crash, each part in its own try: the restore offer (CrashOffer), and the culprit (Culprits: the
    // failure kept as it was thrown, read by its frames, weighed against Mod Doctor's pick and the updated mods), left out
    // where its turn-off couldn't keep the other mods as they were (no list as the game read it). One line for support
    // with what was weighed.
    static async Task<Crash> Weigh(NGame game, ModDoctor.Findings findings)
    {
        Offer? offer = null;
        try
        {
            offer = CrashOffer();
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.ModId}] Could not look at the last working launch after the failed start: {e}");
        }
        Culprits.Fault? fault = null;
        Culprits.Reading? reading = null;
        Culprits.Culprit? culprit = null;
        var weighed = new List<string>();
        try
        {
            fault = await Culprits.Failure(game);
            var mods = Culprits.Context.Live();
            reading = fault == null ? null : Culprits.ByFrame(fault.Chain, _planned, _working, mods);
            culprit = Culprits.Rank(reading, findings, _planned, _working, AsRead(), TurnedOff.Recorded(), mods, weighed);
            if (culprit != null && !CanTurnOff)
            {
                weighed.Add("no list as the game read it, so no turn-off is offered");
                culprit = null;
            }
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.ModId}] Could not work out which mod stopped the start, so none is named: {e}");
        }
        var crash = new Crash(offer, culprit, _planned, _working, reading?.Error ?? "", reading?.Signature ?? "");
        Log.Info($"[{ModEntry.ModId}] The failed start: {(fault == null ? "its error wasn't kept" : $"{reading?.Error} (kept at {fault.Where})")}; "
            + string.Join("; ", (reading?.Notes ?? []).Concat(weighed)) + $"; {Offered(crash)}");
        return crash;
    }

    // The culprit and the restore, for the support line
    static string Offered(Crash crash) => (crash.Culprit is { } c
            ? $"culprit {string.Join(", ", c.Ids)} ({c.Why}{(c.Checked ? ", our check" : "")}{(c.DidntStick ? ", didn't stick" : "")}), "
                + $"needing {(c.Needing.Count == 0 ? "none" : string.Join(", ", c.Needing))}, streak {c.Streak}"
            : "no culprit")
        + $"; restore {(crash.Offer == null ? "none" : crash.Culprit == null ? "offered" : RestoreHelps(crash) ? "beside it" : "not beside it")}";

    // The switches as the game read them before the first main menu (the list in memory is its rebuilt one then), the
    // list as it is after it
    public static IReadOnlyList<(string Id, ModSource Source, bool On)>? AsRead() =>
        SameIdLoad.Settled ? SaveManager.Instance.SettingsSave.ModSettings?.ModList.Select(Key).ToList() : _read;

    // A turn-off can keep every other mod as the game read it: after the first main menu, or with that list
    static bool CanTurnOff => SameIdLoad.Settled || _read != null;

    // Whether the Restore link shows beside a culprit: a restore applies; the culprit isn't ours (Restore keeps us on)
    // nor updated since the working setup (Restore writes the list where it's on at its new version); and across a game
    // change, or with a setup that doesn't say which, every one of its mods is new, on again or switched since (one
    // unchanged ran on the old game, so Restore would turn it back on into the same failure). Beside an unchanged
    // culprit on the same game it shows: the cause is then more likely among the other changes, which Restore puts back.
    public static bool RestoreHelps(Crash crash) => crash is { Offer: { } offer, Culprit: { } culprit }
        && culprit.Why != Culprits.Why.Self && culprit.Updates.Count == 0
        && (Culprits.SameGame(crash.Planned, offer.Working)
            || culprit.Ids.All(id => offer.Changes.Any(c => c.Id == id && c.Kind is Kind.New or Kind.Enabled or Kind.Switched)));

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
    static List<string> CrashLines(Offer offer, IReadOnlyList<string> suspects, bool plain, int most = LinesShown)
    {
        var lines = Lines(offer.Changes, plain, most);
        if (offer.GameChanged)
            lines.Add("\n" + Our(plain, "SPIRE_MOD_MANAGER.CRASH_GAME_CHANGED"));
        if (suspects.Count > 0)
        {
            var named = suspects.Take(SuspectsShown).Select(id => plain ? PlainName(id) : Name(id)).ToList();
            if (suspects.Count > SuspectsShown)
                named.Add(More(plain, MoreSuspects, suspects.Count - SuspectsShown));
            lines.Add("\n" + Our(plain, "SPIRE_MOD_MANAGER.CRASH_SUSPECTS", ("mods", string.Join(", ", named))));
        }
        return lines;
    }

    // The game's own message for a failed start, the one its dialog (which ours replaces) shows, first and set apart as
    // the game's, so nothing of the game's is hidden: "Notice from the game" centred over it, and the message in an
    // outlined box of its own (GameText), our text below. Through the game's text, or (plain: Godot's dialog has no
    // markup) in English from the game's own file, under the same words. Its paragraphs on one line, its markup as the
    // game shows it. None if it can't be read, or the game no longer has it.
    public static string GamesMessage(bool plain)
    {
        const string Key = "STARTUP_ERROR.description";
        try
        {
            var message = plain
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(Godot.FileAccess.GetFileAsString("res://localization/eng/main_menu_ui.json"))?.GetValueOrDefault(Key)
                : LocString.GetIfExists("main_menu_ui", Key)?.GetFormattedText();
            if (message is not { Length: > 0 })
            {
                Log.Warn($"[{ModEntry.ModId}] The game has no {Key} text{(plain ? " in English" : "")}, so the dialog leaves the game's own message out");
                return "";
            }
            message = Whitespace.Replace(plain ? Markup.Replace(message, "") : message, " ").Trim();
            var notice = Our(plain, "SPIRE_MOD_MANAGER.CRASH_GAME_NOTICE");
            return plain ? $"{notice}:\n{message}\n\n" : $"{GameText.Caption(notice, centred: true)}\n{GameText.Box(message, centred: true)}\n\n";
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.ModId}] Could not read the game's own startup error message, so the dialog leaves it out: {e.Message}");
            return "";
        }
    }

    static readonly Regex Whitespace = new(@"\s+");

    // A popup's visible words, on one line and cut at 300 characters, for the log.
    static string Words(Node popup)
    {
        var words = Whitespace.Replace(string.Join(" | ", popup.FindChildren("*", "", true, false)
            .Select(n => n switch { RichTextLabel rich => rich.GetParsedText(), Label label => label.Text, _ => "" })
            .Where(t => t.Length > 0)), " ");
        return words.Length <= 300 ? words : Menus.Prefix(words, 300) + "…";
    }

    // A button of the dialog: its words' key, the answer, and the mod (cut to fit in the game's popup) or count it names
    public record Button(string Key, Answer Answer, string? Mod = null, int Count = 0);

    // The dialog for a crash: its text (the game's message first), the right button (none: Quit alone, as the game's own
    // dialog), the quiet link under the buttons, and the cards over the text's "N more" links
    public record Shown(string Body, Button? Yes, Button? Link, Dictionary<string, string> Cards);

    static Task<Answer> Ask(NGame game, bool styled, Crash crash, IReadOnlyList<string> suspects, Failed? failed) => styled
        ? AskStyled(Plan(crash, suspects, failed, plain: false)) : AskPlain(game, Plan(crash, suspects, failed, plain: true));

    // What the dialog says, after the game's message. With no culprit, the restore offer: what changed, Restore, and Mod
    // Doctor's suspects with Disable suspects as the link. With one: our line for it by why (none for Mod Doctor's pick
    // with its own words), Mod Doctor's box under it when it names the same mod, the turn-off (the mods that need it go
    // too; ours names them and leaves them), the mods this streak turned off already, and the restore beside it when
    // Restore can help (RestoreHelps, the first few changes listed), its link; else from the third failed start in a row
    // the streak, with Turn off all mods as the link. A turn-off that didn't stick says so, and offers Restore if it
    // applies, else Quit alone. After a failed write, what went wrong first, then the culprit's line (or the offer's
    // text), with Quit alone.
    public static Shown Plan(Crash crash, IReadOnlyList<string> suspects, Failed? failed, bool plain)
    {
        var cards = new Dictionary<string, string>();
        var game = GamesMessage(plain);
        var parts = new List<string>();
        if (failed != null)
            parts.Add(Our(plain, failed.Key, ("mod", Names(failed.Ids, plain, MoreMods, cards))));
        if (crash.Culprit is not { } culprit)
        {
            var offer = crash.Offer ?? throw new InvalidOperationException("nothing to offer");
            cards[MoreChanges] = Rest(offer.Changes);
            cards[MoreSuspects] = MoreSuspectsCard(suspects);
            var body = Our(plain, "SPIRE_MOD_MANAGER.CRASH_BODY", ("game", game + string.Concat(parts.Select(p => p + "\n\n"))),
                ("date", Date(offer.Working, plain)), ("changes", string.Join("\n", CrashLines(offer, suspects, plain))));
            return failed != null ? new Shown(body, null, null, cards)
                : new Shown(body, new Button("SPIRE_MOD_MANAGER.CRASH_RESTORE", Answer.Restore),
                    Disableable(suspects).Count > 0 && CanTurnOff ? new Button("SPIRE_MOD_MANAGER.CRASH_DISABLE_SUSPECTS", Answer.DisableSuspects) : null, cards);
        }
        Shown Done(Button? yes = null, Button? link = null) => new(game + string.Join("\n\n", parts), yes, link, cards);
        var ids = culprit.Ids;
        var mod = Called(ids[0], plain);
        if (culprit.DidntStick)
        {
            parts.Add(Our(plain, "SPIRE_MOD_MANAGER.CRASH_DIDNT_STICK", ("mod", mod)));
            if (failed != null || crash.Offer is not { } offer)
                return Done();
            parts.Add(RestoreOr(offer, plain, cards));
            return Done(new Button("SPIRE_MOD_MANAGER.CRASH_RESTORE", Answer.Restore));
        }
        var doctors = culprit.Why is Culprits.Why.DoctorThrew or Culprits.Why.DoctorUpdated or Culprits.Why.DoctorAdded;
        var box = culprit.DoctorText is { } words ? DoctorBox(words, plain) : null;
        if (Line(crash, culprit, plain, cards) is { } line)
            parts.Add(line);
        if (box != null && !doctors)
            parts.Add(box); // under our line: it names the same mod
        if (failed != null)
        {
            if (box != null && doctors)
                parts.Add(box);
            return Done();
        }
        if (culprit.Why == Culprits.Why.Self)
        {
            parts.Add(Our(plain, "SPIRE_MOD_MANAGER.CRASH_TURN_OFF_SELF_BODY"));
            if (culprit.Needing.Count > 0)
                parts.Add(Our(plain, "SPIRE_MOD_MANAGER.CRASH_SELF_NEEDED", ("mods", Names(culprit.Needing, plain, MoreNeeding, cards))));
            return Done(new Button("SPIRE_MOD_MANAGER.CRASH_TURN_OFF_SELF", Answer.TurnOff));
        }
        var turnOff = ids.Count == 1 ? Our(plain, "SPIRE_MOD_MANAGER.CRASH_TURN_OFF_BODY", ("mod", mod)) : Our(plain, "SPIRE_MOD_MANAGER.CRASH_TURN_OFF_BODY_MANY");
        // The turn-offs in one paragraph: this one, the mods that go with it, the ones this streak made already
        if (culprit.Needing.Count > 0)
            turnOff += " " + Our(plain, "SPIRE_MOD_MANAGER.CRASH_TURN_OFF_NEEDS", ("mods", Names(culprit.Needing, plain, MoreNeeding, cards)));
        if (TurnedOff.Entries.Where(e => !e.MenuSince && !ids.Contains(e.Id)).Select(e => TurnedOff.Name(e, plain)).Distinct().ToList() is { Count: > 0 } already)
            turnOff += " " + Our(plain, "SPIRE_MOD_MANAGER.CRASH_ALREADY_OFF", ("mods", Listed(already, plain, MoreOff, cards)));
        parts.Add(turnOff);
        if (box != null && doctors)
            parts.Add(box);
        // The streak's words go with its link, Turn off all mods, which Restore takes the place of
        var restore = RestoreHelps(crash);
        var streak = culprit.Streak >= StreakForAllOff && !restore;
        if (streak)
            parts.Add(Our(plain, "SPIRE_MOD_MANAGER.CRASH_STREAK", ("count", Count(plain, culprit.Streak))));
        if (restore && crash.Offer is { } beside)
            parts.Add(RestoreOr(beside, plain, cards));
        var yes = ids.Count == 1 ? new Button("SPIRE_MOD_MANAGER.CRASH_TURN_OFF", Answer.TurnOff, Mod: Culprits.Named(ids[0], plain))
            : new Button("SPIRE_MOD_MANAGER.CRASH_TURN_OFF_MANY", Answer.TurnOff, Count: ids.Count);
        return Done(yes, restore ? new Button("SPIRE_MOD_MANAGER.CRASH_RESTORE", Answer.Restore)
            : streak ? new Button("SPIRE_MOD_MANAGER.CRASH_ALL_OFF", Answer.AllOff) : null);
    }

    // Our line for the culprit, by the rule that picked it; none for Mod Doctor's pick with its own words (its box says it)
    static string? Line(Crash crash, Culprits.Culprit culprit, bool plain, Dictionary<string, string> cards)
    {
        var mod = Called(culprit.Ids[0], plain);
        var date = crash.Working is { } working ? Date(working, plain) : "";
        string Of(string key) => Our(plain, key, ("mod", mod));
        switch (culprit.Why)
        {
            case Culprits.Why.Patch:
                return Of("SPIRE_MOD_MANAGER.CRASH_CULPRIT_PATCH");
            case Culprits.Why.LibraryUser:
                return Our(plain, "SPIRE_MOD_MANAGER.CRASH_CULPRIT_LIBRARY_USER", ("mod", mod), ("library", Called(culprit.Library ?? "", plain)));
            case Culprits.Why.Types:
                return Of("SPIRE_MOD_MANAGER.CRASH_CULPRIT_TYPES");
            case Culprits.Why.Clash:
                return Our(plain, "SPIRE_MOD_MANAGER.CRASH_CULPRIT_CLASH", ("mod", mod), ("other", Called(culprit.Other ?? "", plain)),
                    ("id", plain ? culprit.ContentId ?? "" : Escaped(culprit.ContentId ?? "")));
            case Culprits.Why.Rebuild:
                return Of("SPIRE_MOD_MANAGER.CRASH_CULPRIT_REBUILD");
            case Culprits.Why.RebuildLast:
                return Of("SPIRE_MOD_MANAGER.CRASH_CULPRIT_REBUILD_LAST");
            case Culprits.Why.Stale when culprit.Ids.Count > 1:
                return Our(plain, "SPIRE_MOD_MANAGER.CRASH_CULPRIT_STALE_MANY", ("mods", Bullets(culprit.Ids, plain, cards)));
            case Culprits.Why.Stale:
                return Of("SPIRE_MOD_MANAGER.CRASH_CULPRIT_STALE");
            case Culprits.Why.Self:
                return Our(plain, "SPIRE_MOD_MANAGER.CRASH_CULPRIT_SELF");
            case Culprits.Why.DoctorThrew or Culprits.Why.DoctorUpdated or Culprits.Why.DoctorAdded:
                return culprit.DoctorText == null ? Of("SPIRE_MOD_MANAGER.CRASH_CULPRIT_DOCTOR") : null;
            case Culprits.Why.Updated when culprit.Updates is [var update, ..]:
                return Our(plain, "SPIRE_MOD_MANAGER.CRASH_CULPRIT_UPDATED", ("mod", mod), ("date", date),
                    ("from", plain ? update.From ?? "" : Escaped(update.From ?? "")), ("to", plain ? update.To ?? "" : Escaped(update.To ?? "")));
            case Culprits.Why.UpdatedMany:
                cards[MoreChanges] = Rest(culprit.Updates);
                return Our(plain, "SPIRE_MOD_MANAGER.CRASH_CULPRIT_UPDATED_MANY", ("date", date), ("mods", string.Join("\n", Lines(culprit.Updates, plain))));
            default:
                return Of("SPIRE_MOD_MANAGER.CRASH_CULPRIT_CODE");
        }
    }

    // A mod's name in the crash texts (with its id where another mod's name holds it: Culprits.Named), escaped for the
    // game's markup
    static string Called(string id, bool plain) => plain ? Culprits.Named(id, plain: true) : Escaped(Culprits.Named(id));

    // Mods named in a line: the first few, the rest counted as a link whose card names them (plain: as text)
    static string Names(IReadOnlyList<string> ids, bool plain, string link, Dictionary<string, string> cards) =>
        Listed([.. ids.Select(id => Called(id, plain))], plain, link, cards);

    static string Listed(IReadOnlyList<string> names, bool plain, string link, Dictionary<string, string> cards)
    {
        var shown = names.Take(SuspectsShown).ToList();
        if (names.Count > SuspectsShown)
        {
            shown.Add(More(plain, link, names.Count - SuspectsShown));
            cards[link] = string.Join("\n", names.Skip(SuspectsShown).Select(name => "  •  " + name));
        }
        return string.Join(", ", shown);
    }

    // Mods a line each, as the changes are listed, the last ones counted in one line if there are many
    static string Bullets(IReadOnlyList<string> ids, bool plain, Dictionary<string, string> cards)
    {
        var shown = ids.Count > LinesShown ? LinesShown - 1 : LinesShown;
        var lines = ids.Take(shown).Select(id => "  •  " + Called(id, plain)).ToList();
        if (ids.Count > LinesShown)
        {
            lines.Add("  •  " + More(plain, MoreMods, ids.Count - shown));
            cards[MoreMods] = string.Join("\n", ids.Skip(shown).Select(id => "  •  " + Called(id, plain: false)));
        }
        return string.Join("\n", lines);
    }

    const int LinesBeside = 2; // beside a culprit, the second offer: its first change, the rest counted

    // Restore beside a culprit: the date, what changed since (and whether the game did)
    static string RestoreOr(Offer offer, bool plain, Dictionary<string, string> cards)
    {
        cards[MoreChanges] = Rest(offer.Changes, LinesBeside);
        return Our(plain, "SPIRE_MOD_MANAGER.CRASH_RESTORE_OR", ("date", Date(offer.Working, plain)),
            ("changes", string.Join("\n", CrashLines(offer, [], plain, LinesBeside))));
    }

    // Mod Doctor's own words, set apart as its: its name over them, in a box like the game's message (plain: under its
    // name, as the game's)
    static string DoctorBox(string words, bool plain)
    {
        var caption = Our(plain, "SPIRE_MOD_MANAGER.CRASH_DOCTOR_CAPTION");
        return plain ? $"{caption}:\n{words}" : $"{GameText.Caption(caption, centred: true)}\n{GameText.Box(Escaped(words), centred: true)}";
    }

    static string Date(Setup setup, bool plain) => plain ? setup.At.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : When(setup);

    // A button's words
    public static string Said(Button button, bool plain) => Our(plain, button.Key, ("mod", button.Mod ?? ""), ("count", Count(plain, button.Count)));

    static LocString Loc(Button button)
    {
        var text = new LocString("settings_ui", button.Key);
        text.Add("mod", button.Mod ?? "");
        text.Add("count", Menus.Number(button.Count));
        return text;
    }

    // The offer in the game's popup, our mark on it. Another mod's popup would keep ours out (the container takes one at
    // a time), so it goes; if ours still isn't the one shown, that's a failure (the game's dialog then). The link, when
    // there is one, under the game's two buttons (LinkButton).
    static async Task<Answer> AskStyled(Shown shown)
    {
        if (NGenericPopup.Create() is not { } popup || NModalContainer.Instance is not { } modals)
            throw new InvalidOperationException("the game's popup can't be made");
        // A popup already open (the game's or another mod's) goes, so what it said goes into the log first.
        if (modals.OpenModal is Node open)
            Log.Warn($"[{ModEntry.ModId}] A popup was open at the failed start and closes for the offer: {open.GetType().Name} \"{Words(open)}\"");
        modals.Clear();
        Menus.Mark(popup);
        Links(popup, shown.Cards);
        if (popup.GetNodeOrNull<RichTextLabel>("VerticalPopup/Description") is { } words)
            GameText.Watch(words); // the game's message in its box
        modals.Add(popup);
        if (modals.OpenModal != popup)
            throw new InvalidOperationException("the game's popup container didn't take ours");
        var linked = new TaskCompletionSource<Answer>();
        if (shown.Link is { } link)
            LinkButton(popup, Said(link, plain: false), () => linked.TrySetResult(link.Answer));
        var title = new LocString("settings_ui", "SPIRE_MOD_MANAGER.CRASH_TITLE");
        var quit = new LocString("main_menu_ui", "QUIT");
        var yes = shown.Yes is { Mod: { } mod } named ? FittedYes(popup, named, mod) : shown.Yes;
        // The title stands in for the text, which SetBody puts in after
        var confirmed = yes != null ? popup.WaitForConfirmation(title, title, quit, Loc(yes)) : popup.WaitForConfirmation(title, title, null, quit);
        SetBody(popup, shown.Body);
        if (await Task.WhenAny(confirmed, linked.Task) == linked.Task)
            return linked.Task.Result;
        return await confirmed && shown.Yes is { } chosen ? chosen.Answer : Answer.Quit;
    }

    // The right button naming a mod: its words on as many lines as its label holds (on the game's one line even a short
    // name shrinks them past reading), the name cut short where they still don't fit at SmallestButtonText; the whole
    // name is in the text
    static Button FittedYes(NGenericPopup popup, Button button, string mod)
    {
        if (popup.GetNodeOrNull<NPopupYesNoButton>("VerticalPopup/YesButton")?.GetNodeOrNull<MegaLabel>("%Label") is not { } label)
            return button;
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        // As tall as its least, centred where it is: the label reaches past the button's art above and below, which a line
        // alone keeps inside
        if (label.Size.Y > label.CustomMinimumSize.Y && label.CustomMinimumSize.Y > 0)
        {
            var spare = (label.Size.Y - label.CustomMinimumSize.Y) / 2;
            (label.OffsetTop, label.OffsetBottom) = (label.OffsetTop + spare, label.OffsetBottom - spare);
        }
        var (font, room) = (label.GetThemeFont("font"), label.Size is { X: > 0, Y: > 0 } size ? size : label.CustomMinimumSize);
        bool Fits(string name) => font.GetMultilineStringSize(Said(button with { Mod = name }, plain: false), HorizontalAlignment.Center, room.X, SmallestButtonText)
            is var words && words.X <= room.X && words.Y <= room.Y;
        if (Fits(mod))
            return button;
        // The longest start of the name that fits, by halves (a longer start is never smaller)
        var starts = StringInfo.ParseCombiningCharacters(mod);
        var (fits, over) = (0, starts.Length);
        while (over - fits > 1)
        {
            var mid = (fits + over) / 2;
            if (Fits(Menus.CutShort(mod[..starts[mid]])))
                fits = mid;
            else
                over = mid;
        }
        return button with { Mod = Menus.CutShort(mod[..starts[fits]]) };
    }

    // The suspects the line counts rather than names: the card over its "more".
    static string MoreSuspectsCard(IReadOnlyList<string> suspects) =>
        suspects.Count > SuspectsShown ? string.Join("\n", suspects.Skip(SuspectsShown).Select(id => "  •  " + Name(id))) : "";

    public const string LinkName = "SpireModManagerCrashLink";
    static readonly Color LinkColor = new(0.961f, 0.71f, 0.42f), LinkHoverColor = new(1, 0.82f, 0.58f); // the panel's pending orange

    // A third answer: a quiet link under the game's two buttons, centred, in orange, lit and underlined on hover, a
    // pointer over it. No keys (Enter and Escape stay the game's two). It closes the popup as the game's buttons do, with
    // the game's click. One at most: they'd share its place.
    public static void LinkButton(NGenericPopup popup, string text, Action pressed)
    {
        if (popup.GetNodeOrNull<Control>("VerticalPopup/YesButton") is not { } yes || yes.GetParent() is not Control art)
            return;
        var link = new Godot.LinkButton
        {
            Name = LinkName, Text = text, Underline = Godot.LinkButton.UnderlineMode.Never,
            FocusMode = Control.FocusModeEnum.None, MouseDefaultCursorShape = Control.CursorShape.PointingHand,
        };
        link.AddThemeFontOverride("font", ModEntry.Font("res://themes/kreon_bold_shared.tres"));
        link.AddThemeFontSizeOverride("font_size", 20);
        link.AddThemeColorOverride("font_color", LinkColor);
        link.AddThemeColorOverride("font_hover_color", LinkHoverColor);
        link.AddThemeColorOverride("font_pressed_color", LinkHoverColor);
        link.AddThemeColorOverride("font_hover_pressed_color", LinkHoverColor);
        link.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.6f)); // readable on the popup's stone
        link.AddThemeConstantOverride("outline_size", 4);
        link.Pressed += () =>
        {
            try
            {
                SfxCmd.Play(FmodSfx.uiClick);
            }
            catch (Exception)
            {
                // no sound this early in a failed start
            }
            pressed();
            NModalContainer.Instance?.Clear();
        };
        art.AddChild(link);
        // Centred on the art just under the buttons, as wide as its words; our mark moves under it.
        (link.AnchorLeft, link.AnchorRight, link.AnchorTop, link.AnchorBottom) = (0.5f, 0.5f, 1, 1);
        link.GrowHorizontal = Control.GrowDirection.Both;
        (link.OffsetLeft, link.OffsetRight, link.OffsetTop, link.OffsetBottom) = (0, 0, yes.OffsetBottom + 4, yes.OffsetBottom + 34);
        // The mark is a point it grows from both ways (Menus.Mark): its middle half its height under the link's bottom.
        if (art.GetNodeOrNull<Control>(Menus.MarkName) is { } mark)
        {
            var middle = link.OffsetBottom + 4 + mark.GetCombinedMinimumSize().Y / 2;
            (mark.OffsetTop, mark.OffsetBottom) = (middle, middle);
        }
    }

    public const string PlainDialogName = "SpireModManagerCrashDialog", LinkAction = "spire_mod_manager_link";

    // The offer where the game's text and popups can't be used: Godot's own dialog, with our English texts (they come
    // through the game's text too) and the game's word for Quit in English; the link as a third button. Quit alone: a
    // dialog with one button.
    static async Task<Answer> AskPlain(NGame game, Shown shown)
    {
        AcceptDialog dialog = shown.Yes is { } yes
            ? new ConfirmationDialog { OkButtonText = Said(yes, plain: true), CancelButtonText = "Quit" }
            : new AcceptDialog { OkButtonText = "Quit" };
        dialog.Name = PlainDialogName;
        dialog.Title = Our(true, "SPIRE_MOD_MANAGER.CRASH_TITLE");
        (dialog.DialogText, dialog.DialogAutowrap, dialog.Exclusive) = (shown.Body, true, true);
        var answer = new TaskCompletionSource<Answer>();
        dialog.Confirmed += () => answer.TrySetResult(shown.Yes?.Answer ?? Answer.Quit);
        dialog.Canceled += () => answer.TrySetResult(Answer.Quit);
        if (shown.Link is { } link)
        {
            dialog.AddButton(Said(link, plain: true), false, LinkAction);
            dialog.CustomAction += action =>
            {
                if (action == LinkAction)
                {
                    answer.TrySetResult(link.Answer);
                    dialog.Hide();
                }
            };
        }
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
