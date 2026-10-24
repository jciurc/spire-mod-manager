using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;
using MegaCrit.Sts2.Core.Platform.Steam;
using MegaCrit.Sts2.Core.Saves;

namespace SpireModManager;

// The mods we turned off after a failed start (the crash dialog's Turn off, Turn off all mods and Disable suspects:
// LaunchGuard.TurnOff), in turned-off.json beside the launch records, in the account's folder: what each was, why, the
// switches it had (Turn back on brings back those) and whether a main menu came since (the streak, and didn't stick).
// The next main menu says so, with Keep off and Turn back on, and our page's banner keeps saying so until the mod is on
// again by any means, gone, or the banner dismissed. Restore and profiles keep these mods off. Every step fails open.
static class TurnedOff
{
    public const string FileName = "turned-off.json";
    const int FileVersion = 1; // a reader must not need fields added later

    // One mod turned off. At: when, with its offset (one answer's entries share it, which makes them a group). Copy: the
    // copy that ran. Needs: for a dependent turned off with a culprit, that culprit. Switches: each source of its
    // installed copies then, on as the game read it, with its copies, and each other source whose entry the turn-off
    // switched off (none installed: unsubscribed, or not listed without Steam). MenuSince: a main menu came since (it's
    // no longer part of a streak). Shown: its notice showed. SameErrorAfter: the start failed the same way after it.
    // Rebuild: its failure was a failed rebuild of a patched method.
    public record Entry(DateTimeOffset At, string Id, string? Name, string? Version, string? Copy, Culprits.Why Why, string? Needs,
        string? Error, string? Signature, IReadOnlyList<Culprits.Switch> Switches, bool MenuSince = false, bool Shown = false,
        bool SameErrorAfter = false, bool Rebuild = false);

    record Saved(int Version, List<Entry>? Mods);

#if DEVTOOLS
    // Self-test: another file in place of ours (null: ours), read afresh; and Steam up or not, for which sources a launch
    // scanned
    public static string? TestFile
    {
        get => _testFile;
        set
        {
            _testFile = value;
            _entries = null;
            Marked.Clear();
        }
    }

    static string? _testFile;
    public static bool? TestSteam;
#endif

    static string? FilePath
    {
        get
        {
#if DEVTOOLS
            if (_testFile != null)
                return _testFile;
#endif
            return LaunchGuard.Dir is { } dir ? Path.Combine(dir, FileName) : null;
        }
    }

    static List<Entry>? _entries;

    // The entries, read once a session (a file that can't be read counts as none). What changes is kept here first, so a
    // file that can't be written still shows the notice once.
    public static IReadOnlyList<Entry> Entries => _entries ??= Read();

    static List<Entry> Read()
    {
        if (FilePath is not { } file || !File.Exists(file))
            return [];
        try
        {
            if (JsonSerializer.Deserialize<Saved>(File.ReadAllText(file), LaunchGuard.Json) is not { Version: FileVersion, Mods: { } mods })
            {
                Log.Warn($"[{ModEntry.ModId}] {file} isn't a version {FileVersion} list of mods, so it counts as none");
                return [];
            }
            return [.. mods.Where(e => e is { Id.Length: > 0 }).Select(e => e with { Switches = e.Switches ?? [] })];
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.ModId}] Could not read {file}, so it counts as none: {e.Message}");
            return [];
        }
    }

    // The entries written (the file gone once there are none), kept as they are now; throws if the file can't be written
    static void Write(List<Entry> entries)
    {
        var file = FilePath ?? throw new InvalidOperationException("no folder for our records");
        if (entries.Count == 0)
            File.Delete(file);
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file) ?? ".");
            Store.WriteAtomic(file, JsonSerializer.Serialize(new Saved(FileVersion, entries), LaunchGuard.Json));
        }
        _entries = entries;
    }

    // The same, where a file that can't be written only costs the next launch the change: kept in memory, logged
    static void Keep(List<Entry> entries, string what)
    {
        try
        {
            Write(entries);
        }
        catch (Exception e)
        {
            _entries = entries;
            Log.Warn($"[{ModEntry.ModId}] Could not save {what} in {FileName}, so it holds for this session only: {e.Message}");
        }
    }

    // A turn-off's entries, written only after its save worked. An entry there for the same mod (another copy of it
    // turned off since) takes the new one's details, and keeps each source's switch it recorded first (the one Turn back
    // on needs), gaining the new sources and copies. An entry of this streak whose failure had the same signature gets
    // SameErrorAfter (most likely not the cause), but not between two failed rebuilds, whatever rule picked each mod:
    // their signature is the rebuilt method's and Harmony's error is the same for any bad build, so two patching mods'
    // failures read alike. Throws if the file can't be written, and nothing changes then.
    public static void Add(IReadOnlyList<Entry> added)
    {
        var entries = Entries.ToList();
        if (added.FirstOrDefault(e => e.Signature is { Length: > 0 }) is { Signature: { } signature } culprit)
            for (var i = 0; i < entries.Count; i++)
                if (!entries[i].MenuSince && entries[i].Signature == signature && !added.Any(a => a.Id == entries[i].Id)
                    && !(entries[i].Rebuild && culprit.Rebuild))
                    entries[i] = entries[i] with { SameErrorAfter = true };
        foreach (var entry in added)
        {
            var at = entries.FindIndex(e => e.Id == entry.Id);
            if (at < 0)
                entries.Add(entry);
            else
                entries[at] = entry with { Switches = Merged(entries[at].Switches, entry.Switches) };
        }
        Write(entries);
    }

    static List<Culprits.Switch> Merged(IReadOnlyList<Culprits.Switch> was, IReadOnlyList<Culprits.Switch> now) =>
        [.. was.Select(w => now.FirstOrDefault(n => n.Source == w.Source) is { } same ? w with { Copies = [.. w.Copies.Union(same.Copies)] } : w),
            .. now.Where(n => !was.Any(w => w.Source == n.Source))];

    // These mods' entries gone (Turn back on, the banner's Dismiss); a file that can't be written is logged, and the next
    // main menu drops an entry whose mod is on
    public static void Remove(IReadOnlyCollection<string> ids) => Keep([.. Entries.Where(e => !ids.Contains(e.Id))], "the mods turned back on");

    // An entry of ours for this mod: its needs lines say so
    public static bool Has(string? id) => id != null && Entries.Any(e => e.Id == id);

    // Every mod turned off after a failed start, that Restore and profiles keep off: ours, and (doctor) Mod Doctor's that
    // are still off in the game's list as read (LaunchGuard.AsRead)
    public static HashSet<string> OffIds(bool doctor = true) => [.. Entries.Select(e => e.Id), .. doctor ? ModDoctor.TurnedOffIds(LaunchGuard.AsRead()) : []];

    // The entries as ranking reads them (Culprits.Rank: didn't stick, the streak)
    public static List<Culprits.Recorded> Recorded() => [.. Entries.Select(e => new Culprits.Recorded(e.At, e.Id, e.Switches, e.MenuSince))];

    static readonly HashSet<(string Id, DateTimeOffset At)> Marked = []; // the entries this launch's first main menu marked

    // At each launch's first main menu, after SameIdLoad.Settle (the list repaired, not the rebuilt one): every entry
    // marked as seen at a menu, so it's no longer part of a streak; one whose mod is on again by any means (our page, a
    // mod's tickbox, a fix, the game's page) goes; and one whose mod is no longer installed, but only where its sources
    // were scanned this launch: without Steam the game doesn't list Workshop mods, which stay off and installed
    public static void MenuReached()
    {
        if (Entries.Count == 0 || SaveManager.Instance.SettingsSave.ModSettings?.ModList.Select(e => (e.Id, e.Source, e.IsEnabled)).ToList() is not { } list)
            return;
        var installed = Installs();
        var kept = new List<Entry>();
        foreach (var entry in Entries)
        {
            if (On(entry.Id, installed, list))
                Log.Info($"[{ModEntry.ModId}] {entry.Id} is on again, so it's no longer listed as turned off after a failed start");
            else if (Gone(entry, installed))
                Log.Info($"[{ModEntry.ModId}] {entry.Id} is no longer installed, so it's no longer listed as turned off after a failed start");
            else
            {
                if (!entry.MenuSince)
                    Marked.Add((entry.Id, entry.At));
                kept.Add(entry with { MenuSince = true });
            }
        }
        Keep(kept, "which mods a main menu came after");
    }

    // No copy of the mod installed, where each of its sources was scanned this launch
    static bool Gone(Entry entry, List<(string Id, ModSource Source)> installed) =>
        !installed.Any(c => c.Id == entry.Id) && entry.Switches.All(s => Scanned(s.Source));

    // At each launch's repair of the game's list in memory (LaunchGuard.RepairBeforeMenu, and SameIdLoad.Settle before
    // it saves): an entry, off, for each switch an entry of ours recorded whose source has none in the list. The game's
    // rebuilt list holds only the copies it found, so a Workshop item unsubscribed, or not listed without Steam, loses
    // its entry at the next save and loads once it's back. Not for an entry the first main menu drops (its mod on again
    // or gone). Each goes right after the mod's own entry, where it has one: the game orders a mod by its last entry, so
    // one at the end would move it. True if any was added.
    public static bool KeepOff(List<SettingsSaveMod> list)
    {
        if (Entries.Count == 0)
            return false;
        var (installed, switches) = (Installs(), list.Select(e => (e.Id, e.Source, e.IsEnabled)).ToList());
        var added = new List<string>();
        foreach (var entry in Entries.Where(e => !On(e.Id, installed, switches) && !Gone(e, installed)))
            foreach (var source in entry.Switches.Select(s => s.Source).Distinct().Where(source => !list.Any(e => e.Id == entry.Id && e.Source == source)).ToList())
            {
                var off = new SettingsSaveMod { Id = entry.Id, Source = source, IsEnabled = false };
                if (list.FindLastIndex(e => e.Id == entry.Id) is var at and >= 0)
                    list.Insert(at + 1, off);
                else
                    list.Add(off);
                added.Add($"({entry.Id}, {source})");
            }
        if (added.Count > 0)
            Log.Info($"[{ModEntry.ModId}] Kept off after a failed start, though the game's list had no entry for them: {string.Join(", ", added)}");
        return added.Count > 0;
    }

    // A start that failed after its first main menu (the checks after it): the marks that menu set taken back, so they
    // count in the streak again
    public static void UndoMenuMarks()
    {
        if (Marked.Count == 0)
            return;
        var entries = Entries.Select(e => Marked.Contains((e.Id, e.At)) ? e with { MenuSince = false } : e).ToList();
        Marked.Clear();
        Keep(entries, "the main menu's marks taken back");
    }

    // The entries whose mod is still off, the rest dropped (on again by any means): for the banner, at each redraw
    public static IReadOnlyList<Entry> StillOff()
    {
        if (Entries.Count == 0 || SaveManager.Instance.SettingsSave.ModSettings?.ModList.Select(e => (e.Id, e.Source, e.IsEnabled)).ToList() is not { } list)
            return Entries;
        var installed = Installs();
        var off = Entries.Where(e => !On(e.Id, installed, list)).ToList();
        if (off.Count != Entries.Count)
            Keep(off, "the mods on again");
        return off;
    }

    static List<(string Id, ModSource Source)> Installs() => [.. ModManager.Mods.Union(Installed.Mods).Select(m => (Id: m.manifest?.id ?? "", m.modSource))
        .Where(c => c.Id.Length > 0).Distinct()];

    // Whether a mod is on in the game's list: a copy of it whose switch is on (one with no entry loads)
    static bool On(string id, List<(string Id, ModSource Source)> installed, IReadOnlyList<(string Id, ModSource Source, bool On)>? list) =>
        installed.Any(c => c.Id == id && Culprits.OnAsRead(id, c.Source, list));

    static bool Scanned(ModSource source) => source != ModSource.SteamWorkshop || Steam();

    static bool Steam()
    {
#if DEVTOOLS
        if (TestSteam is { } test)
            return test;
#endif
        return SteamInitializer.Initialized;
    }

    // From MainMenuPatch's timer: the entries not shown yet, once, after any other popup (trying again each second while
    // one is up)
    public static void ShowNotice(Node menu)
    {
        try
        {
            if (Entries.Where(e => !e.Shown).ToList() is not { Count: > 0 } fresh || !GodotObject.IsInstanceValid(menu) || !menu.IsInsideTree())
                return;
            if (NModalContainer.Instance is not { OpenModal: null })
            {
                menu.GetTree().CreateTimer(1).Timeout += () => ShowNotice(menu);
                return;
            }
            _ = Ask(fresh);
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.ModId}] Could not show the notice about the mods turned off after a failed start: {e}");
        }
    }

#if DEVTOOLS
    // Self-test: as if the game's popup couldn't be made for the notice
    public static bool TestNoPopup;
#endif

    // The notice in the game's popup, our mark on it: Keep off on the left, Turn back on on the right. Enter presses Turn
    // back on: the game binds its confirm to the right button and its cancel (Escape) to the left, and swapping them
    // would put Escape on Turn back on. An entry whose error came back after it first, with a link to turn it alone back on.
    // The entries are marked shown once the popup is up, in memory first: the main menu comes back after every run.
    static async Task Ask(List<Entry> entries)
    {
        try
        {
#if DEVTOOLS
            if (TestNoPopup)
                throw new InvalidOperationException("the self-test's popup that can't be made");
#endif
            if (NGenericPopup.Create() is not { } popup || NModalContainer.Instance is not { } modals)
                throw new InvalidOperationException("the game's popup can't be made");
            var (title, body, cards) = Notice(entries, plain: false);
            Menus.Mark(popup);
            LaunchGuard.Links(popup, cards);
            modals.Add(popup);
            Keep([.. Entries.Select(e => entries.Contains(e) ? e with { Shown = true } : e)], "that its notice showed");
            var again = entries.FirstOrDefault(e => e.SameErrorAfter);
            var alone = new TaskCompletionSource();
            if (again != null)
                LaunchGuard.LinkButton(popup, Menus.Format("SPIRE_MOD_MANAGER.TURNED_OFF_ON_ONE", ("mod", Name(again, plain: true))), () => alone.TrySetResult());
            var titled = new LocString("settings_ui", title);
            // The title stands in for the text, which SetBody puts in after
            var confirmed = popup.WaitForConfirmation(titled, titled, new LocString("settings_ui", "SPIRE_MOD_MANAGER.TURNED_OFF_KEEP"),
                new LocString("settings_ui", "SPIRE_MOD_MANAGER.TURNED_OFF_ON"));
            LaunchGuard.SetBody(popup, body);
            if (await Task.WhenAny(confirmed, alone.Task) == alone.Task && again != null)
                await TurnBackOn([again], null);
            else if (await confirmed)
                await TurnBackOn(entries, null);
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.ModId}] Could not show the notice about the mods turned off after a failed start: {e}");
        }
    }

    // What the notice says (plain: no markup, for the self-test's reading). One mod: "stopped" where its own code threw
    // or can't run on this game (Code, Stale, DoctorThrew), "most likely" for every other rule, Mod Doctor's suspicion for
    // Disable suspects, then the mods turned off with it and an update since. Several (a streak, a group): a line each,
    // claiming nothing. An entry whose error came back after it goes first.
    public static (string Title, string Body, Dictionary<string, string> Cards) Notice(IReadOnlyList<Entry> entries, bool plain)
    {
        var (parts, cards) = (new List<string>(), new Dictionary<string, string>());
        foreach (var again in entries.Where(e => e.SameErrorAfter))
            parts.Add(LaunchGuard.Our(plain, "SPIRE_MOD_MANAGER.TURNED_OFF_SAME_ERROR", ("mod", Name(again, plain))));
        string title;
        if (entries.Where(e => e.Needs == null).ToList() is [var one])
        {
            title = "SPIRE_MOD_MANAGER.TURNED_OFF_TITLE";
            var key = one.Why switch
            {
                Culprits.Why.Code or Culprits.Why.Stale or Culprits.Why.DoctorThrew => "SPIRE_MOD_MANAGER.TURNED_OFF_BODY",
                Culprits.Why.Suspect => "SPIRE_MOD_MANAGER.TURNED_OFF_BODY_SUSPECT",
                _ => "SPIRE_MOD_MANAGER.TURNED_OFF_BODY_LIKELY",
            };
            parts.Add(LaunchGuard.Our(plain, key, ("mod", Name(one, plain)), ("date", When(one))));
            if (entries.Where(e => e.Needs != null).Select(e => Name(e, plain)).ToList() is { Count: > 0 } with)
                parts.Add(LaunchGuard.Our(plain, "SPIRE_MOD_MANAGER.TURNED_OFF_WITH", ("mods", string.Join(", ", with))));
            if (CopyNow(one) is { } now && one.Version != null && now.manifest?.version is { } version && version != one.Version)
                parts.Add(LaunchGuard.Our(plain, "SPIRE_MOD_MANAGER.TURNED_OFF_UPDATED", ("from", Plain(one.Version, plain)), ("to", Plain(version, plain))));
        }
        else
        {
            title = "SPIRE_MOD_MANAGER.TURNED_OFF_TITLE_MANY";
            string Line(Entry e) => "  •  " + (e.Needs is { } needs
                ? LaunchGuard.Our(plain, "SPIRE_MOD_MANAGER.TURNED_OFF_LINE_NEEDS", ("mod", Name(e, plain)), ("other", Name(needs, plain)))
                : LaunchGuard.Our(plain, "SPIRE_MOD_MANAGER.TURNED_OFF_LINE", ("mod", Name(e, plain)), ("date", When(e))));
            var shown = entries.Count > LinesShown ? LinesShown - 1 : LinesShown;
            var lines = entries.Take(shown).Select(Line).ToList();
            if (entries.Count > LinesShown)
            {
                lines.Add("  •  " + LaunchGuard.More(plain, LaunchGuard.MoreMods, entries.Count - shown));
                cards[LaunchGuard.MoreMods] = string.Join("\n", entries.Skip(shown).Select(Line));
            }
            parts.Add(LaunchGuard.Our(plain, "SPIRE_MOD_MANAGER.TURNED_OFF_BODY_MANY", ("mods", string.Join("\n", lines))));
        }
        return (title, string.Join("\n\n", parts), cards);
    }

    const int LinesShown = 5; // the launch notice's, with its title and its last sentence

    static string When(Entry entry) => LaunchGuard.When(entry.At.LocalDateTime);

    static string Plain(string text, bool plain) => plain ? text : LaunchGuard.Escaped(text);

    // A mod as the notice, the banner and the crash dialog name it: its name (with its id where another mod's name holds
    // it: Culprits.Named; the one recorded once it's gone), and which copy broke where another copy is installed
    public static string Name(Entry entry, bool plain)
    {
        var installed = ModManager.Mods.Union(Installed.Mods).Any(m => m.manifest?.id == entry.Id);
        var name = (installed ? Culprits.Named(entry.Id, plain) : entry.Name ?? entry.Id)
            + (CopyNow(entry) is { } copy && copy.manifest?.id == entry.Id && Copies.CopyOf(copy) == entry.Copy ? Copies.Which(copy) : "");
        return Plain(name, plain);
    }

    static string Name(string id, bool plain) => Plain(Culprits.Named(id, plain), plain);

    // The installed copy an entry names (the copy that broke), else any of its id
    static Mod? CopyNow(Entry entry)
    {
        var copies = ModManager.Mods.Union(Installed.Mods).Where(m => m.manifest?.id == entry.Id).ToList();
        return copies.FirstOrDefault(m => Copies.CopyOf(m) == entry.Copy) ?? copies.FirstOrDefault();
    }

    // Turn back on (the popup's, the banner's): each entry's sources that were on, on again (SwitchOn), saved and checked;
    // then the change recorded (the main menu button's dot and its note follow, and on the page it's an undo step), and
    // the entries gone. No restart: it applies at the next relaunch, through the relaunch check, as any change. A save
    // that fails leaves the entries, and says so; so does a mod with nothing to turn on: no copy installed now, and no
    // entry in the list for a source it had on (KeepOff keeps one for a Workshop copy not listed without Steam). True if
    // every one was turned on.
    public static async Task<bool> TurnBackOn(IReadOnlyList<Entry> entries, NModdingScreen? screen)
    {
        var (installed, list) = (Installs(), SaveManager.Instance.SettingsSave.ModSettings?.ModList ?? []);
        bool Nothing(Entry entry) => !installed.Any(c => c.Id == entry.Id)
            && !list.Any(e => e.Id == entry.Id && entry.Switches.Any(s => s.On && s.Source == e.Source));
        var (some, none) = (entries.Where(e => !Nothing(e)).ToList(), entries.Where(Nothing).ToList());
        if (none.Count > 0)
            Log.Info($"[{ModEntry.ModId}] Not turned back on, with no copy installed now nor an entry in the list: {string.Join(", ", none.Select(e => e.Id))}");
        if (some.Count > 0)
        {
            try
            {
                var settings = SaveManager.Instance.SettingsSave.ModSettings ?? throw new InvalidOperationException("the game has no mod settings");
                var asWas = LaunchGuard.State(settings.ModList);
                SwitchOn(settings.ModList, some, installed);
                LaunchGuard.SaveChecked(settings.ModList, asWas);
                Log.Info($"[{ModEntry.ModId}] Turned back on after a failed start: {string.Join(", ", some.Select(e => e.Id))}");
            }
            catch (Exception e)
            {
                Log.Error($"[{ModEntry.ModId}] Could not turn {string.Join(", ", some.Select(e => e.Id))} back on: {e.Message}");
                await Told(some, "SPIRE_MOD_MANAGER.TURNED_OFF_ON_FAILED");
                return false;
            }
            if (screen != null && GodotObject.IsInstanceValid(screen))
            {
                Profiles.RefreshTickboxes(screen);
                CategoryList.Redraw(screen.GetNode<Control>("%ModsScrollContainer/Mask/Content")); // one undoable change
            }
            else
            {
                Profiles.Track();
                History.Record(); // Track only records when the active profile changed
            }
            Remove([.. some.Select(e => e.Id)]);
        }
        if (none.Count > 0)
            await Told(none, "SPIRE_MOD_MANAGER.TURNED_OFF_ON_NONE");
        return none.Count == 0;
    }

    // The switches Turn back on sets, on a given list and installed copies (the self-test's made-up ones): the ones each
    // entry recorded on, installed now or not, every saved entry of that id and source; and when none of those sources
    // has a copy installed now (a Workshop item unsubscribed since), every installed copy's, but only where each of them
    // was scanned this launch: without Steam the game doesn't list a Workshop copy, and a local one kept off would load
    // in its place. No entry is added: a copy with no entry loads anyway. The game's switches only: Dependencies.Write
    // would tick every copy of a source, and an unticked twin would lose its tick (copies_off was kept while the switch
    // was off, so the same copies come back).
    public static void SwitchOn(List<SettingsSaveMod> list, IEnumerable<Entry> entries, IReadOnlyList<(string Id, ModSource Source)> installed)
    {
        foreach (var entry in entries)
        {
            var sources = entry.Switches.Where(s => s.On).Select(s => s.Source).ToHashSet();
            if (!installed.Any(c => c.Id == entry.Id && sources.Contains(c.Source)) && sources.All(Scanned))
                sources.UnionWith(installed.Where(c => c.Id == entry.Id).Select(c => c.Source));
            foreach (var saved in list.Where(e => e.Id == entry.Id && sources.Contains(e.Source)))
                saved.IsEnabled = true;
        }
    }

    // A Turn back on that failed (its save, or a mod with no copy installed: key), said in the game's popup with its Ok.
    // From the notice's button, a frame first: the notice's own close clears the popups after its handler.
    static async Task Told(IReadOnlyList<Entry> entries, string key)
    {
        try
        {
            var tree = (SceneTree)Engine.GetMainLoop();
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            if (NGenericPopup.Create() is not { } popup || NModalContainer.Instance is not { } modals)
                return;
            modals.Clear();
            Menus.Mark(popup);
            modals.Add(popup);
            var titled = new LocString("settings_ui", entries.Count == 1 ? "SPIRE_MOD_MANAGER.TURNED_OFF_TITLE" : "SPIRE_MOD_MANAGER.TURNED_OFF_TITLE_MANY");
            var told = popup.WaitForConfirmation(titled, titled, null, new LocString("main_menu_ui", "GENERIC_POPUP.ok"));
            LaunchGuard.SetBody(popup, Menus.Format(key, ("mod", string.Join(", ", entries.Select(e => Name(e, plain: false))))));
            await told;
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.ModId}] Could not say that turning the mods back on failed: {e}");
        }
    }
}
