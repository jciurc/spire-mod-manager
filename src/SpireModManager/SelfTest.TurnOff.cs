using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;
using MegaCrit.Sts2.Core.Saves;

namespace SpireModManager;

// What a failed start's answers do, and what follows them: the turn-off (LaunchGuard.TurnOff), the dialog's words and
// presses with made-up culprits, the next main menu's notice and our page's banner (TurnedOff), Restore and profiles
// keeping those mods off, and the takeover's own steps. A turn-off's lists go into the game's own settings object (its
// save writes that one, never another), and each part puts the game's list back and saves it after. Our records go to
// a file of the test's (TurnedOff.TestFile).
static partial class SelfTest
{
    static async Task TestCrashTurnOff(NMainMenu menu, string outDir)
    {
        await TurnOffPart(() => TestTurnOffWrite(outDir), outDir);
        await TurnOffPart(() => TestCrashDialog(menu, outDir), outDir);
        await TurnOffPart(() => TestTurnedOffNotice(menu), outDir);
        await TurnOffPart(() => TestTurnedOffKept(outDir), outDir);
        await TurnOffPart(() => TestCrashTakeover(menu, outDir), outDir);
    }

    static string TurnOffDir(string outDir) => Path.Combine(outDir, "turn-off");

    // One part: a fresh record file of the test's, and every hook, popup and the game's mod list put back after it
    static async Task TurnOffPart(Func<Task> test, string outDir, [CallerArgumentExpression(nameof(test))] string name = "")
    {
        var settings = SaveManager.Instance.SettingsSave.ModSettings;
        var listWas = settings?.ModList.Select(e => new SettingsSaveMod { Id = e.Id, Source = e.Source, IsEnabled = e.IsEnabled }).ToList();
        var dir = TurnOffDir(outDir);
        Emptied(dir);
        TurnedOff.TestFile = Path.Combine(dir, TurnedOff.FileName);
        var transitionWas = NGame.Instance?.Transition.Visible;
        try
        {
            await test();
        }
        catch (Exception e)
        {
            Results.Add($"FAIL {name} threw: {e}");
            PutBack();
        }
        finally
        {
            (ModEntry.TestQuit, ModEntry.TestRelaunch) = (null, null);
            (LaunchGuard.TestNoLanguage, LaunchGuard.TestSaveFails, LaunchGuard.TestTakeoverThrows) = (false, null, false);
            (ModDoctor.TestSuspects, ModDoctor.TestFolder, TurnedOff.TestSteam) = (null, null, null);
            NModalContainer.Instance?.Clear();
            ((SceneTree)Engine.GetMainLoop()).Root.GetNodeOrNull(LaunchGuard.PlainDialogName)?.QueueFree();
            if (NGame.Instance is { } game && transitionWas is { } visible)
                game.Transition.Visible = visible; // the dialog's setup hides the startup transition
            TurnedOff.TestFile = null;
            Emptied(dir);
            if (settings != null && listWas != null)
            {
                settings.ModList.Clear();
                settings.ModList.AddRange(listWas);
                SaveManager.Instance.SaveSettings();
            }
        }
    }

    // A folder of the test's, empty (a file left read-only made writable first)
    static void Emptied(string dir)
    {
        if (Directory.Exists(dir))
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(dir, recursive: true);
        }
        Directory.CreateDirectory(dir);
    }

    // The record file of the test's empty again: gone (a folder put in its place too), read afresh
    static void FreshRecord()
    {
        if (TurnedOff.TestFile is not { } file)
            return;
        if (Directory.Exists(file))
            Directory.Delete(file, recursive: true);
        else if (File.Exists(file))
        {
            File.SetAttributes(file, FileAttributes.Normal);
            File.Delete(file);
        }
        TurnedOff.TestFile = file;
    }

    // A made-up culprit: why and its mods, nothing more to say
    static Culprits.Culprit Blamed(Culprits.Why why, params string[] ids) => new(why,
        why is not (Culprits.Why.Code or Culprits.Why.Patch or Culprits.Why.LibraryUser), ids, false, null, null, null, null, null, [], [], false, 1,
        "made up|signature", "InvalidOperationException: made up");

    // A made-up record of a turn-off: the mod, why, when, its local copy recorded on
    static TurnedOff.Entry OffEntry(string id, Culprits.Why why = Culprits.Why.Code, DateTimeOffset? at = null, bool menu = false, string? needs = null) =>
        new(at ?? DateTimeOffset.Now, id, null, "1.0.0", $"l:{id}", why, needs, null, why == Culprits.Why.Needs ? null : "made up|signature",
            [new Culprits.Switch(ModSource.ModsDirectory, true, [$"l:{id}"])], MenuSince: menu);

    // A mod's switches in a list, sorted ("l-,w+": its local copy off, its Workshop one on)
    static string Switched(IEnumerable<SettingsSaveMod> list, string id) => string.Join(",", list.Where(e => e.Id == id)
        .Select(e => $"{(e.Source == ModSource.SteamWorkshop ? "w" : "l")}{(e.IsEnabled ? "+" : "-")}").Order(StringComparer.Ordinal));

    // The same in settings.save as saved, what the next launch reads
    static string SwitchedOnDisk(string id)
    {
        try
        {
            var file = ProjectSettings.GlobalizePath(UserDataPathProvider.GetAccountScopedBasePath("settings.save"));
            var list = JsonNode.Parse(File.ReadAllText(file))?["mod_settings"]?["mod_list"]?.AsArray() ?? new JsonArray();
            return string.Join(",", list.OfType<JsonNode>().Where(e => (string?)e["id"] == id).Select(e =>
                $"{(e["source"]?.ToJsonString() is { } source && (source.Contains("workshop", StringComparison.OrdinalIgnoreCase) || source == "2") ? "w" : "l")}{((bool?)e["is_enabled"] == true ? "+" : "-")}")
                .Order(StringComparer.Ordinal));
        }
        catch (Exception e)
        {
            return $"unread ({e.Message})";
        }
    }

    // The words in the popup open now as a player reads them (the game's markup out)
    static string ShownWords() => NModalContainer.Instance?.OpenModal is Node popup ? string.Join(" | ", popup.FindChildren("*", "", true, false)
        .Where(n => n is not CanvasItem item || item.IsVisibleInTree()).Select(n => n switch { RichTextLabel r => r.GetParsedText(), Label l => l.Text, _ => "" })
        .Where(t => t.Length > 0)) : "";

    // A button of our popup open now (marked: no other popup's), pressed as the game's are
    static void PressOurs(string button)
    {
        if (NModalContainer.Instance?.OpenModal is Node popup && popup.FindChild(Menus.MarkName, true, false) != null
            && popup.GetNodeOrNull($"VerticalPopup/{button}") is { } found)
            found.EmitSignal("Released", found);
    }

    static void PressLink() => ((NModalContainer.Instance?.OpenModal as Node)?.FindChild(LaunchGuard.LinkName, true, false) as BaseButton)
        ?.EmitSignal(BaseButton.SignalName.Pressed);

    // The turn-off's write: the core (LaunchGuard.TurnOff with its lists), as a failed start's answer writes it, on made-up
    // lists in the game's own settings object. The live values can't reach these paths: at the main menu the list is
    // settled and the parameterless repair has run, so each check hands the core its lists and settled: false.
    static Task TestTurnOffWrite(string outDir)
    {
        if (SaveManager.Instance.SettingsSave.ModSettings is not { } settings || Dependencies.Find("SmmTestFixes") == null)
        {
            Results.Add("SKIP the turn-off's write: no mod list, or no Test Fixes");
            return Task.CompletedTask;
        }
        const ModSource L = ModSource.ModsDirectory, W = ModSource.SteamWorkshop;
        var file = TurnedOff.TestFile ?? "";
        static SettingsSaveMod S(string id, ModSource source, bool on) => new() { Id = id, Source = source, IsEnabled = on };
        void Swap(params SettingsSaveMod[] list)
        {
            settings.ModList.Clear();
            settings.ModList.AddRange(list);
        }
        static List<(string, ModSource, bool)> Read(params (string, ModSource, bool)[] read) => [.. read];
        string Of(string id) => Switched(settings.ModList, id);
        TurnedOff.Entry? Recorded(string id) => TurnedOff.Entries.FirstOrDefault(e => e.Id == id);
        var relaunches = 0;
        ModEntry.TestRelaunch = _ => relaunches++;
        var fixes = new LaunchGuard.Off([("SmmTestFixes", Culprits.Why.Code, null)], "InvalidOperationException: made up", "made up|signature");
        var copiesOff = string.Join(",", Store.Data.CopiesOff ?? []);
        var ours = (ModEntry.ModId, L, true);
        // Test Twin as two sources, the list as RepairBeforeMenu leaves it at a failed start: its local copy kept off, its
        // Workshop one on. Turning off Test Fixes saves Test Twin as it is, in memory and in settings.save; again the other
        // way round. Our copies_off isn't touched.
        Swap(S(ModEntry.ModId, L, true), S("SmmTestFixes", L, true), S("SmmTestTwin", L, false), S("SmmTestTwin", W, true));
        var first = LaunchGuard.TurnOff(settings, Read(ours, ("SmmTestFixes", L, true), ("SmmTestTwin", L, false), ("SmmTestTwin", W, true)), false, fixes);
        var kept = (Of("SmmTestFixes"), Of("SmmTestTwin"), SwitchedOnDisk("SmmTestFixes"), SwitchedOnDisk("SmmTestTwin"));
        var entry = Recorded("SmmTestFixes");
        var json = File.Exists(file) ? File.ReadAllText(file) : "";
        FreshRecord();
        Swap(S(ModEntry.ModId, L, true), S("SmmTestFixes", L, true), S("SmmTestTwin", L, true), S("SmmTestTwin", W, false));
        var second = LaunchGuard.TurnOff(settings, Read(ours, ("SmmTestFixes", L, true), ("SmmTestTwin", L, true), ("SmmTestTwin", W, false)), false, fixes);
        var otherWay = (Of("SmmTestTwin"), SwitchedOnDisk("SmmTestTwin"));
        Check("a turn-off saves every other mod's copies as the game read them (Test Twin local off and Workshop on, then the other way round), in memory "
            + "and in settings.save; Test Fixes off; our copies_off untouched; then a restart each time",
            first == null && second == null && kept == ("l-", "l-,w+", "l-", "l-,w+") && otherWay == ("l+,w-", "l+,w-") && relaunches == 2
            && string.Join(",", Store.Data.CopiesOff ?? []) == copiesOff, $"{first?.Key} {second?.Key}; {kept}; {otherWay}; relaunches {relaunches}");
        Check("the turn-off's record: Test Fixes, why Code, the switch its source had as the game read it with its copy, the time with its offset, the "
            + "failure's error and signature; in the file by name (snake_case), no main menu since",
            entry is { Why: Culprits.Why.Code, Switches: [{ Source: ModSource.ModsDirectory, On: true, Copies: ["l:SmmTestFixes"] }], Error: "InvalidOperationException: made up",
                Signature: "made up|signature", MenuSince: false } && (DateTimeOffset.Now - entry.At).TotalMinutes < 1 && entry.At.Offset == DateTimeOffset.Now.Offset
            && json.Contains("\"why\": \"Code\"") && json.Contains("\"menu_since\": false") && json.Contains("\"source\": \"ModsDirectory\""),
            $"{entry}; {json.Length} characters written");

        // The list as the game rebuilt it, before the first main menu (Test Twin both off: its first entry's switch): the
        // core's own repair puts its Workshop copy back on before the save. After the menu (settled), nothing repaired.
        FreshRecord();
        Swap(S(ModEntry.ModId, L, true), S("SmmTestFixes", L, true), S("SmmTestTwin", L, false), S("SmmTestTwin", W, false));
        var unrepaired = LaunchGuard.TurnOff(settings, Read(ours, ("SmmTestFixes", L, true), ("SmmTestTwin", L, false), ("SmmTestTwin", W, true)), false, fixes);
        var repaired = (Of("SmmTestTwin"), SwitchedOnDisk("SmmTestTwin"));
        FreshRecord();
        Swap(S(ModEntry.ModId, L, true), S("SmmTestFixes", L, true), S("SmmTestTwin", L, false), S("SmmTestTwin", W, false));
        var settled = LaunchGuard.TurnOff(settings, Read(ours, ("SmmTestFixes", L, true), ("SmmTestTwin", L, false), ("SmmTestTwin", W, true)), true, fixes);
        Check("a turn-off on the list as the game rebuilt it repairs it first (Test Twin's Workshop copy on again, as read); after the first main menu it "
            + "leaves the list as it is", unrepaired == null && repaired == ("l-,w+", "l-,w+") && settled == null && Of("SmmTestTwin") == "l-,w-" && Of("SmmTestFixes") == "l-",
            $"{unrepaired?.Key} {repaired}; settled {settled?.Key} {Of("SmmTestTwin")} {Of("SmmTestFixes")}");

        // A culprit new since the list was saved: no entry as read, so the game loads it. It gets an entry, off, and its
        // record says it was on. Without the rule (on only with an entry that's on) nothing would go off.
        FreshRecord();
        Swap(S(ModEntry.ModId, L, true), S("SmmTestUi", L, true));
        var fresh = LaunchGuard.TurnOff(settings, Read(ours, ("SmmTestUi", L, true)), false, fixes);
        Check("a culprit with no entry as the game read it (new since): turned off with an entry of its own, off; its record says it was on",
            fresh == null && Of("SmmTestFixes") == "l-" && Recorded("SmmTestFixes") is { Switches: [{ On: true }] }, $"{fresh?.Key} {Of("SmmTestFixes")}; {Recorded("SmmTestFixes")}");

        // Disable suspects writes entries too (why Suspect). Turn off all mods: every mod on, one with no entry as read too
        // (Test Content), never ours; one already off (Test UI, as read before the menu and as the list is after it) is
        // neither turned off again nor recorded.
        FreshRecord();
        Swap(S(ModEntry.ModId, L, true), S("SmmTestFixes", L, true), S("SmmTestUi", L, true));
        var suspected = LaunchGuard.TurnOff(settings, Read(ours, ("SmmTestFixes", L, true), ("SmmTestUi", L, true)), false,
            new LaunchGuard.Off([("SmmTestUi", Culprits.Why.Suspect, null), ("SmmTestFixes", Culprits.Why.Suspect, null)], "", ""));
        var suspects = (Recorded("SmmTestUi")?.Why, Recorded("SmmTestFixes")?.Why, Of("SmmTestUi"));
        FreshRecord();
        var all = LaunchGuard.AllOff("InvalidOperationException: made up", "made up|signature");
        Swap(S(ModEntry.ModId, L, true), S("SmmTestFixes", L, true), S("SmmTestUi", L, false));
        var allOff = LaunchGuard.TurnOff(settings, Read(ours, ("SmmTestFixes", L, true), ("SmmTestUi", L, false)), false, all);
        var everything = (Recorded("SmmTestFixes")?.Why, Recorded("SmmTestUi"), Recorded("SmmTestContent")?.Why, Of("SmmTestContent"), Of(ModEntry.ModId), Recorded(ModEntry.ModId));
        FreshRecord();
        Swap(S(ModEntry.ModId, L, true), S("SmmTestFixes", L, true), S("SmmTestUi", L, false));
        var afterMenu = LaunchGuard.TurnOff(settings, null, true, all);
        Check("Disable suspects records its mods (why Suspect); Turn off all mods turns off and records every mod on (why All), one with no entry as read "
            + "too, never ours, and not one already off (as read before the first main menu, as the list is after it)",
            suspected == null && suspects == (Culprits.Why.Suspect, Culprits.Why.Suspect, "l-") && allOff == null
            && everything == (Culprits.Why.All, null, Culprits.Why.All, "l-", "l+", null) && afterMenu == null && Recorded("SmmTestUi") == null
            && Recorded("SmmTestFixes")?.Why == Culprits.Why.All, $"{suspects}; {everything}; after the menu {afterMenu?.Key}, Test UI {Recorded("SmmTestUi")}");

        // A save that fails: nothing changed, no record, no restart. A record that can't be written (its file a folder):
        // the switches saved back as they were. And when that save fails too: the list in memory as it was, the mod left
        // off in settings.save, each with its own line.
        void Usual() => Swap(S(ModEntry.ModId, L, true), S("SmmTestFixes", L, true), S("SmmTestTwin", L, false), S("SmmTestTwin", W, true));
        var usual = Read(ours, ("SmmTestFixes", L, true), ("SmmTestTwin", L, false), ("SmmTestTwin", W, true));
        var relaunched = relaunches;
        FreshRecord();
        Usual();
        LaunchGuard.TestSaveFails = () => true;
        var unsaved = LaunchGuard.TurnOff(settings, usual, false, fixes);
        var unsavedList = (Of("SmmTestFixes"), Of("SmmTestTwin"), TurnedOff.Entries.Count);
        FreshRecord();
        Directory.CreateDirectory(file);
        TurnedOff.TestFile = file;
        Usual();
        LaunchGuard.TestSaveFails = null;
        var unrecorded = LaunchGuard.TurnOff(settings, usual, false, fixes);
        var undone = (Of("SmmTestFixes"), SwitchedOnDisk("SmmTestFixes"));
        Usual();
        var saves = 0;
        LaunchGuard.TestSaveFails = () => ++saves > 1;
        var stuck = LaunchGuard.TurnOff(settings, usual, false, fixes);
        LaunchGuard.TestSaveFails = null;
        var stuckList = (Of("SmmTestFixes"), SwitchedOnDisk("SmmTestFixes"));
        FreshRecord();
        Check("a turn-off whose save fails changes nothing, records nothing and doesn't restart; one whose record can't be written is undone and saved back; "
            + "when that save fails too, the list in memory as it was and the mod off in settings.save; each with its own line",
            unsaved is { Key: "SPIRE_MOD_MANAGER.CRASH_SAVE_FAILED", Ids: ["SmmTestFixes"] } && unsavedList == ("l+", "l-,w+", 0)
            && unrecorded?.Key == "SPIRE_MOD_MANAGER.CRASH_RECORD_FAILED" && undone == ("l+", "l+")
            && stuck?.Key == "SPIRE_MOD_MANAGER.CRASH_RECORD_FAILED_OFF" && stuckList == ("l+", "l-") && relaunches == relaunched,
            $"{unsaved?.Key} {unsavedList}; {unrecorded?.Key} {undone}; {stuck?.Key} {stuckList}; relaunches {relaunches - relaunched}");

        // A restart that fails keeps the turn-off: it's saved and recorded, and the dialog says to start the game again
        Usual();
        ModEntry.TestRelaunch = _ => throw new InvalidOperationException("made up: the restart fails");
        var unrestarted = LaunchGuard.TurnOff(settings, usual, false, fixes);
        ModEntry.TestRelaunch = _ => relaunches++;
        Check("a turn-off whose restart fails stays done: Test Fixes off and recorded, with its line", unrestarted?.Key == "SPIRE_MOD_MANAGER.CRASH_RESTART_FAILED"
            && Of("SmmTestFixes") == "l-" && Recorded("SmmTestFixes") != null, $"{unrestarted?.Key} {Of("SmmTestFixes")} {Recorded("SmmTestFixes")}");

        // A mod turned off again (another copy of it, since): one entry, the switch it recorded first kept for each source,
        // the new source and copies joined. The start failing the same way after a turn-off: that entry marked.
        FreshRecord();
        TurnedOff.Add([OffEntry("SmmTestFixes")]);
        TurnedOff.Add([OffEntry("SmmTestFixes") with { Switches = [new(L, false, ["l:SmmTestFixesFixed"]), new(W, true, ["w:1"])] }]);
        var merged = Recorded("SmmTestFixes")?.Switches ?? [];
        FreshRecord();
        TurnedOff.Add([OffEntry("SmmTestUi", at: DateTimeOffset.Now.AddMinutes(-2))]);
        TurnedOff.Add([OffEntry("SmmTestFixes")]);
        Check("a mod turned off again: one entry, the first switch kept for a source it had, the new one and its copies joined; the start failing the same "
            + "way after a turn-off marks that entry", merged is [{ Source: L, On: true, Copies: ["l:SmmTestFixes", "l:SmmTestFixesFixed"] }, { Source: W, On: true }]
            && Recorded("SmmTestUi")?.SameErrorAfter == true && Recorded("SmmTestFixes")?.SameErrorAfter == false,
            $"{string.Join("; ", merged.Select(s => $"{s.Source} {s.On} {string.Join("+", s.Copies)}"))}; Test UI again {Recorded("SmmTestUi")?.SameErrorAfter}");

        // Turn back on: of a mod recorded with its local copy off and its Workshop one on, the Workshop switch alone; with
        // the Workshop copy gone since, the local one, and no entry added for the Workshop
        static TurnedOff.Entry Was() => OffEntry("SmmTestFixes") with { Switches = [new(L, false, ["l:SmmTestFixes"]), new(W, true, ["w:1"])] };
        var both = new List<SettingsSaveMod> { S("SmmTestFixes", L, false), S("SmmTestFixes", W, false) };
        TurnedOff.SwitchOn(both, [Was()], [("SmmTestFixes", L), ("SmmTestFixes", W)]);
        var gone = new List<SettingsSaveMod> { S("SmmTestFixes", L, false) };
        TurnedOff.SwitchOn(gone, [Was()], [("SmmTestFixes", L)]);
        Check("Turn back on turns on the sources that were on (the Workshop copy alone); with none of those installed now, every installed copy's, adding no entry",
            Switched(both, "SmmTestFixes") == "l-,w+" && Switched(gone, "SmmTestFixes") == "l+" && gone.Count == 1,
            $"{Switched(both, "SmmTestFixes")}; {Switched(gone, "SmmTestFixes")} ({gone.Count})");

        // A launch whose record can't be written (launch-pending.json a folder) still has its folder for a turn-off's
        // record (Dir is set first), and no planned setup, so its main menu clears nothing
        var noRecord = Path.Combine(TurnOffDir(outDir), "no-record");
        Directory.CreateDirectory(Path.Combine(noRecord, LaunchGuard.PendingFile));
        var (dir, planned) = LaunchGuard.TestStart(noRecord, Launched(CulpritGame));
        Check("a launch whose record can't be written still has the folder for a turn-off's record, and its main menu clears nothing",
            dir == noRecord && !planned, $"Dir {dir}, planned {planned}");
        return Task.CompletedTask;
    }

    // The dialog with made-up culprits: what it says and offers as Plan has it (in plain English, as the plain dialog shows
    // it), then in the game's popup where a press writes (the turn-off and its record, Restore keeping turned-off mods off,
    // a failed write shown again with Quit alone). Quit and the relaunch are caught.
    static async Task TestCrashDialog(NMainMenu menu, string outDir)
    {
        if (SaveManager.Instance.SettingsSave.ModSettings is not { } settings || NGame.Instance is not { } game
            || Dependencies.Find("SmmTestFixes") is not { } fixes || !Copies.Ticked(fixes))
        {
            Results.Add("SKIP the crash culprit's dialog: no mod list, game, or Test Fixes on");
            return;
        }
        var listWas = settings.ModList.Select(e => new SettingsSaveMod { Id = e.Id, Source = e.Source, IsEnabled = e.IsEnabled }).ToList();
        void ListBack()
        {
            settings.ModList.Clear();
            settings.ModList.AddRange(listWas.Select(e => new SettingsSaveMod { Id = e.Id, Source = e.Source, IsEnabled = e.IsEnabled }));
            FreshRecord();
        }
        bool AllOff(string id) => settings.ModList.Any(e => e.Id == id) && settings.ModList.Where(e => e.Id == id).All(e => !e.IsEnabled);
        var (here, there) = (Launched(CulpritGame), Launched(OtherGame));
        var working = new LaunchGuard.Setup(DateTime.Now.AddHours(-1), [.. here.Mods.Select(m => m.Id == "SmmTestFixes" ? m with { Enabled = false } : m)], CulpritGame);
        LaunchGuard.Offer Offer(params (LaunchGuard.Kind Kind, string Id)[] changes) => new(working, [.. changes.Select(c => new LaunchGuard.Change(c.Kind, c.Id))], false);
        LaunchGuard.Crash Crash(Culprits.Culprit? culprit, LaunchGuard.Offer? offer = null, LaunchGuard.Setup? planned = null, LaunchGuard.Setup? since = null) =>
            new(offer, culprit, planned ?? here, since ?? offer?.Working ?? working, "InvalidOperationException: made up", "made up|signature");
        static LaunchGuard.Shown Plain(LaunchGuard.Crash crash, IReadOnlyList<string>? suspects = null, LaunchGuard.Failed? failed = null) =>
            LaunchGuard.Plan(crash, suspects ?? [], failed, plain: true);
        static string Said(LaunchGuard.Shown shown) => $"{(shown.Yes is { } yes ? LaunchGuard.Said(yes, plain: true) : "Quit alone")} | "
            + (shown.Link is { } link ? LaunchGuard.Said(link, plain: true) : "no link");
        var code = Plain(Crash(Blamed(Culprits.Why.Code, "SmmTestFixes")));
        Check("the crash culprit's dialog: the game's message first, then that Test Fixes stopped the start (its code), that it can be turned off; "
            + "\"Turn off Test Fixes and restart\" and Quit, no link",
            code.Body.StartsWith(LaunchGuard.GamesMessage(plain: true)) && code.Body.Contains("Test Fixes stopped the game from starting. The error came from its code.")
            && code.Body.Contains("Spire Mod Manager can turn off Test Fixes and restart the game.") && Said(code) == "Turn off Test Fixes and restart | no link",
            $"{code.Body} || {Said(code)}");
        // Each rule's own line
        (Culprits.Culprit Culprit, string Says)[] lines =
        [
            (Blamed(Culprits.Why.Patch, "SmmTestFixes"), "Test Fixes most likely stopped the game from starting. The error came from a part of the game it changes."),
            (Blamed(Culprits.Why.LibraryUser, "SmmTestUi") with { Library = "SmmTestLib" }, "The error came from Test Framework, which it needs"),
            (Blamed(Culprits.Why.Types, "SmmTestFixes"), "Part of its code couldn't be loaded"),
            (Blamed(Culprits.Why.Clash, "SmmTestUi") with { Other = "SmmTestContent", ContentId = "CARD.SMM_TEST" }, "the same ID as Test Content does (CARD.SMM_TEST)"),
            (Blamed(Culprits.Why.Rebuild, "SmmTestFixes"), "it's the one of them that's new or updated"),
            (Blamed(Culprits.Why.RebuildLast, "SmmTestFixes"), "The error came while its changes to a part of the game were being put in place, and that part was fine without them."),
            (Blamed(Culprits.Why.Stale, "SmmTestFixes"), "Test Fixes stopped the game from starting. Part of its code can't run on this version of the game"),
            (Blamed(Culprits.Why.Updated, "SmmTestFixes") with { Updates = [new(LaunchGuard.Kind.Updated, "SmmTestFixes", "0.1.0", "0.2.0")] }, "from 0.1.0 to 0.2.0. It most likely"),
            (Blamed(Culprits.Why.DoctorThrew, "SmmTestFixes"), "Mod Doctor found that Test Fixes stopped the game from starting."),
        ];
        var wrongLines = lines.Where(l => !Plain(Crash(l.Culprit)).Body.Contains(l.Says)).Select(l => $"{l.Culprit.Why}: {Plain(Crash(l.Culprit)).Body}").ToList();
        var group = Plain(Crash(Blamed(Culprits.Why.Stale, "SmmTestUi", "SmmTestContent")));
        var updated = Plain(Crash(Blamed(Culprits.Why.UpdatedMany, "SmmTestUi", "SmmTestContent") with
            { Updates = [new(LaunchGuard.Kind.Updated, "SmmTestUi", "1.0", "1.1"), new(LaunchGuard.Kind.Updated, "SmmTestContent", "1.0", "1.2")] }, Offer((LaunchGuard.Kind.New, "SmmTestRetired"))));
        Check("the crash culprit's dialog: a line for each rule; a probe group and several updated mods named a line each, with \"Turn off 2 mods and restart\" "
            + "(no Restore beside updated mods)",
            wrongLines.Count == 0 && group.Body.Contains("These mods stopped the game from starting.") && group.Body.Contains("  •  Test UI\n  •  Test Content")
            && group.Body.Contains("Spire Mod Manager can turn them off") && Said(group) == "Turn off 2 mods and restart | no link"
            && updated.Body.Contains("Test UI (updated: 1.0 → 1.1)") && Said(updated) == "Turn off 2 mods and restart | no link",
            $"{string.Join(" || ", wrongLines)} || {group.Body} || {updated.Body} || {Said(updated)}");

        // Mod Doctor's box: under our line when it names the same mod; after the turn-off line for its own pick, which then has
        // no line of ours; never where it names another (the culprit has no words of it then). The suspects line goes.
        var doctorWords = "Test Fixes crashed while loading [b]really[/b]";
        var agreed = Plain(Crash(Blamed(Culprits.Why.Code, "SmmTestFixes") with { DoctorAgrees = true, DoctorText = doctorWords }), ["SmmTestUi"]);
        var doctors = Plain(Crash(Blamed(Culprits.Why.DoctorThrew, "SmmTestFixes") with { DoctorAgrees = true, DoctorText = doctorWords }), ["SmmTestUi"]);
        int At(LaunchGuard.Shown shown, string text) => shown.Body.IndexOf(text, StringComparison.Ordinal);
        Check("Mod Doctor's box (\"From Mod Doctor:\" over its words as written): under our line when it names the same mod, after the turn-off line for its "
            + "own pick (no line of ours then); no suspects line beside a culprit",
            At(agreed, "stopped the game") is >= 0 and var line && At(agreed, "From Mod Doctor:\n" + doctorWords) > line
            && At(agreed, "Spire Mod Manager can turn off") > At(agreed, "From Mod Doctor:") && !agreed.Body.Contains("Mod Doctor suspects")
            && At(doctors, "Spire Mod Manager can turn off") is >= 0 and var off && At(doctors, "From Mod Doctor:") > off && !doctors.Body.Contains("Mod Doctor found that"),
            $"{agreed.Body} || {doctors.Body}");

        // A library with the mods that need it; ours, with the mods that need us (left as they are), no link; a turn-off that
        // didn't stick, Quit alone or Restore; the streak, with the mods turned off already and Turn off all mods
        var library = Plain(Crash(Blamed(Culprits.Why.Code, "SmmTestLib") with { Needing = ["SmmTestContent", "SmmTestUi"] }));
        var self = Plain(Crash(Blamed(Culprits.Why.Self, ModEntry.ModId) with { Needing = ["SmmTestFixes"] }, Offer((LaunchGuard.Kind.New, "SmmTestRetired"))));
        var stuck = Plain(Crash(Blamed(Culprits.Why.Code, "SmmTestFixes") with { DidntStick = true }));
        var stuckRestore = Plain(Crash(Blamed(Culprits.Why.Code, "SmmTestFixes") with { DidntStick = true }, Offer((LaunchGuard.Kind.New, "SmmTestRetired"))));
        TurnedOff.Add([OffEntry("SmmTestUi", at: DateTimeOffset.Now.AddMinutes(-9)), OffEntry("SmmTestContent", at: DateTimeOffset.Now.AddMinutes(-4))]);
        var streak = Plain(Crash(Blamed(Culprits.Why.Code, "SmmTestFixes") with { Streak = 3 }));
        FreshRecord();
        Check("the dialog for a library (the mods that need it named, turned off too), ours (how to get it back, the mods that need us, no link), a turn-off "
            + "that didn't stick (said; Quit alone, or Restore where it applies) and the streak (the mods turned off already, Turn off all mods)",
            library.Body.Contains("The mods that need it are turned off too: Test Content, Test UI.")
            && self.Body.Contains("The error came from Spire Mod Manager itself") && self.Body.Contains("These mods need it and won't load until it's on again: Test Fixes.")
            && Said(self) == "Turn off Spire Mod Manager and restart | no link"
            && stuck.Body.Contains("turned off Test Fixes after the last failed start, but the same copy loaded again") && Said(stuck) == "Quit alone | no link"
            && Said(stuckRestore) == "Restore and restart | no link" && streak.Body.Contains("Turned off after the last failed start: Test UI, Test Content.")
            && streak.Body.Contains("The game failed to start 3 times in a row.") && Said(streak) == "Turn off Test Fixes and restart | Turn off all mods and restart",
            $"{library.Body} || {self.Body} {Said(self)} || {stuck.Body} {Said(stuck)} | {Said(stuckRestore)} || {streak.Body} {Said(streak)}");

        // The Restore link's rule. The culprit updated, another mod new: none. Unchanged, another new: the link. Ours: none.
        // Across a game change only beside a culprit new since, every one of a group; a working setup that doesn't say its
        // game counts as one.
        var otherNew = Offer((LaunchGuard.Kind.New, "SmmTestRetired"));
        var fixesNew = Offer((LaunchGuard.Kind.New, "SmmTestFixes"));
        var unsaid = new LaunchGuard.Offer(working with { Game = null }, otherNew.Changes, false);
        bool[] helps =
        [
            LaunchGuard.RestoreHelps(Crash(Blamed(Culprits.Why.Code, "SmmTestFixes") with { Updates = [new(LaunchGuard.Kind.Updated, "SmmTestFixes", "0.1", "0.2")] }, otherNew)),
            LaunchGuard.RestoreHelps(Crash(Blamed(Culprits.Why.Code, "SmmTestFixes"), otherNew)),
            LaunchGuard.RestoreHelps(Crash(Blamed(Culprits.Why.Self, ModEntry.ModId), otherNew)),
            LaunchGuard.RestoreHelps(Crash(Blamed(Culprits.Why.Code, "SmmTestFixes"), otherNew, there)),
            LaunchGuard.RestoreHelps(Crash(Blamed(Culprits.Why.Code, "SmmTestFixes"), fixesNew, there)),
            LaunchGuard.RestoreHelps(Crash(Blamed(Culprits.Why.Stale, "SmmTestFixes", "SmmTestUi"), fixesNew, there)),
            LaunchGuard.RestoreHelps(Crash(Blamed(Culprits.Why.Stale, "SmmTestFixes", "SmmTestUi"), Offer((LaunchGuard.Kind.New, "SmmTestFixes"), (LaunchGuard.Kind.New, "SmmTestUi")), there)),
            LaunchGuard.RestoreHelps(Crash(Blamed(Culprits.Why.Code, "SmmTestFixes"), unsaid)),
        ];
        Check("the Restore link beside a culprit: not beside one updated, beside one unchanged on the same game, not beside ours; across a game change only "
            + "beside one new since, every one of a group, and a last working launch that doesn't say its game counts as a change",
            helps.SequenceEqual([false, true, false, false, true, false, true, false]), string.Join(", ", helps));

        // A failed write: what went wrong first (the mods named), then the culprit's line, with Quit alone; a Restore that
        // couldn't save, then the offer's text
        var failed = Plain(Crash(Blamed(Culprits.Why.Code, "SmmTestFixes")), failed: new("SPIRE_MOD_MANAGER.CRASH_SAVE_FAILED", ["SmmTestFixes"]));
        var unrestored = Plain(Crash(null, otherNew), failed: new("SPIRE_MOD_MANAGER.CRASH_RESTORE_FAILED", []));
        Check("after a failed write the dialog says what went wrong first, then the culprit's line (or the offer's text), with Quit alone and no link",
            At(failed, "couldn't turn off Test Fixes: the game couldn't save its settings") is >= 0 and var said && At(failed, "Test Fixes stopped the game") > said
            && !failed.Body.Contains("Spire Mod Manager can turn off") && Said(failed) == "Quit alone | no link"
            && unrestored.Body.Contains("couldn't restore the last working setup") && Said(unrestored) == "Quit alone | no link",
            $"{failed.Body} {Said(failed)} || {unrestored.Body} {Said(unrestored)}");

        // In the game's popup, pressed
        var (quits, relaunches) = (0, 0);
        ModEntry.TestQuit = () => quits++;
        ModEntry.TestRelaunch = plain => relaunches += plain ? 1 : 0; // a plain quit (the game's would save a half-started game)
        var pendingPath = Path.Combine(LaunchGuard.Dir ?? OS.GetUserDataDir(), LaunchGuard.PendingFile);
        async Task<(Task Asked, string Words)> Show(LaunchGuard.Crash crash)
        {
            var asked = LaunchGuard.AskAfterCrash(game, crash);
            await Wait(menu, 0.8);
            return (asked, ShownWords());
        }
        File.WriteAllText(pendingPath, "{}"); // as a failed start leaves it: the answer deletes it
        var (pressed, words) = await Show(Crash(Blamed(Culprits.Why.Code, "SmmTestFixes")));
        var popup = NModalContainer.Instance?.OpenModal as Control;
        CheckMark("the crash culprit's dialog", popup);
        await Capture(menu, outDir, "1i-crash-culprit");
        var noLink = popup?.FindChild(LaunchGuard.LinkName, true, false) == null;
        PressOurs("YesButton");
        await Wait(menu, 0.3);
        var entry = TurnedOff.Entries.FirstOrDefault(e => e.Id == "SmmTestFixes");
        Check("the crash culprit's popup: the game's message, Test Fixes named, Quit and \"Turn off Test Fixes and restart\", no link; pressed, every switch "
            + "of Test Fixes off, its record (Code, its switch with its copy), one plain relaunch, this launch's record gone",
            pressed.IsCompleted && words.Contains("Notice from the game") && words.Contains("Test Fixes stopped the game from starting") && words.Contains("Quit")
            && words.Contains("Turn off Test Fixes and restart") && noLink && AllOff("SmmTestFixes") && relaunches == 1 && quits == 0
            && entry is { Why: Culprits.Why.Code, Switches: [{ On: true, Copies: ["l:SmmTestFixes"] }] } && (DateTimeOffset.Now - entry.At).TotalMinutes < 1
            && !File.Exists(pendingPath), $"{words}; relaunches {relaunches}, quits {quits}; {entry}; record left {File.Exists(pendingPath)}");
        ListBack();

        // Beside the restore: the link, which restores the working list with a mod turned off after a failed start kept
        // off (Test UI here, on in that list)
        TurnedOff.Add([OffEntry("SmmTestUi", at: DateTimeOffset.Now.AddMinutes(-3))]);
        (pressed, words) = await Show(Crash(Blamed(Culprits.Why.Code, "SmmTestFixes"), otherNew));
        var link = (NModalContainer.Instance?.OpenModal as Node)?.FindChild(LaunchGuard.LinkName, true, false) as LinkButton;
        PressLink();
        await Wait(menu, 0.3);
        Check("beside the restore, the turn-off is the button and \"Restore and restart\" the link; the link writes the working list (Test Fixes off there), "
            + "a mod turned off after a failed start kept off (Test UI, on there), then relaunches",
            pressed.IsCompleted && words.Contains("Or restore the setup of the last working launch") && link?.Text == "Restore and restart" && relaunches == 2
            && AllOff("SmmTestFixes") && AllOff("SmmTestUi"), $"{words}; link {link?.Text}; relaunches {relaunches}");
        ListBack();

        // The rules' own whys in the record: a failed rebuild's patching mod (RebuildLast), a probe group (both, one time),
        // a library with the mods that need it (Needs, naming it), a twin (every copy's switch off, copies_off untouched)
        async Task<List<TurnedOff.Entry>> Pressed(Culprits.Culprit culprit)
        {
            await Show(Crash(culprit));
            PressOurs("YesButton");
            await Wait(menu, 0.3);
            var entries = TurnedOff.Entries.ToList();
            ListBack();
            return entries;
        }
        var rebuilt = await Pressed(Blamed(Culprits.Why.RebuildLast, "SmmTestFixes"));
        var probed = await Pressed(Blamed(Culprits.Why.Stale, "SmmTestUi", "SmmTestContent"));
        var dependents = await Pressed(Blamed(Culprits.Why.Code, "SmmTestLib") with { Needing = ["SmmTestContent", "SmmTestUi"] });
        var copiesOff = string.Join(",", Store.Data.CopiesOff ?? []);
        await Show(Crash(Blamed(Culprits.Why.Code, "SmmTestTwin")));
        PressOurs("YesButton");
        await Wait(menu, 0.3);
        var twinOff = (AllOff("SmmTestTwin"), string.Join(",", Store.Data.CopiesOff ?? []) == copiesOff);
        ListBack();
        Check("pressed, each culprit's record: a failed rebuild's patching mod (RebuildLast); a probe group, both with one time (Stale); a library and the two "
            + "mods that need it (Needs, naming it); a twin, every copy's switch off and copies_off untouched",
            rebuilt is [{ Id: "SmmTestFixes", Why: Culprits.Why.RebuildLast }]
            && probed.Count == 2 && probed.All(e => e.Why == Culprits.Why.Stale) && probed.Select(e => e.At).Distinct().Count() == 1
            && dependents.Count == 3 && dependents.Count(e => e is { Why: Culprits.Why.Needs, Needs: "SmmTestLib" }) == 2 && twinOff == (true, true) && relaunches == 6,
            $"{string.Join(", ", rebuilt.Concat(probed).Concat(dependents).Select(e => $"{e.Id} {e.Why} {e.Needs}"))}; twin {twinOff}; relaunches {relaunches}");

        // Ours: every switch of ours off, the rest as they were, no record (we won't be running to show it)
        var others = settings.ModList.Where(e => e.Id != ModEntry.ModId).Select(e => $"{e.Id}/{e.Source}={e.IsEnabled}").ToList();
        await Show(Crash(Blamed(Culprits.Why.Self, ModEntry.ModId) with { Needing = ["SmmTestFixes"] }));
        PressOurs("YesButton");
        await Wait(menu, 0.3);
        var oursOff = (AllOff(ModEntry.ModId), settings.ModList.Where(e => e.Id != ModEntry.ModId).Select(e => $"{e.Id}/{e.Source}={e.IsEnabled}").SequenceEqual(others),
            TurnedOff.Entries.Count);
        ListBack();
        SaveManager.Instance.SaveSettings(); // ours on again in settings.save at once
        Check("pressed for ours: every switch of ours off, every other mod's as it was, no record", oursOff == (true, true, 0) && relaunches == 7, $"{oursOff}");

        // The streak's link, Turn off all mods: every mod that was on off but ours and Mod Doctor, one group (why All), none
        // recorded for a mod that was off (Test Retired, off in dev runs)
        TurnedOff.Add([OffEntry("SmmTestMadeUpA", at: DateTimeOffset.Now.AddMinutes(-9)), OffEntry("SmmTestMadeUpB", at: DateTimeOffset.Now.AddMinutes(-4))]);
        var wasOn = settings.ModList.Where(e => e.IsEnabled && e.Id is not (ModEntry.ModId or ModDoctor.ModId)).Select(e => e.Id).Distinct().ToList();
        var retiredOff = settings.ModList.Any(e => e.Id == "SmmTestRetired" && !e.IsEnabled);
        await Show(Crash(Blamed(Culprits.Why.Code, "SmmTestFixes") with { Streak = 3 }));
        PressLink();
        await Wait(menu, 0.3);
        var allEntries = TurnedOff.Entries.Where(e => e.Why == Culprits.Why.All).ToList();
        var allOff = (wasOn.All(AllOff), !AllOff(ModEntry.ModId), allEntries.Select(e => e.Id).Order().SequenceEqual(wasOn.Order()),
            allEntries.Select(e => e.At).Distinct().Count() == 1, !retiredOff || !TurnedOff.Has("SmmTestRetired"));
        ListBack();
        Check("the streak's link, Turn off all mods: every mod that was on off but ours, as one group (why All), and none recorded for a mod that was off",
            allOff == (true, true, true, true, true) && wasOn.Count > 2 && relaunches == 8, $"{allOff}; on before: {string.Join(", ", wasOn)}");

        // The plain dialog (the game's text didn't load): the turn-off as OK, Quit as Cancel, the link a third button, Mod
        // Doctor's words under "From Mod Doctor:"
        LaunchGuard.TestNoLanguage = true;
        var asked = LaunchGuard.AskAfterCrash(game, Crash(Blamed(Culprits.Why.Code, "SmmTestFixes") with { DoctorAgrees = true, DoctorText = doctorWords }, otherNew));
        await Wait(menu, 0.5);
        var plainDialog = menu.GetTree().Root.GetNodeOrNull<ConfirmationDialog>(LaunchGuard.PlainDialogName);
        var plainSays = plainDialog == null ? "no dialog" : $"{plainDialog.DialogText} | {plainDialog.OkButtonText} | {plainDialog.CancelButtonText} | "
            + string.Join(", ", plainDialog.FindChildren("*", "Button", true, false).OfType<Button>().Select(b => b.Text));
        plainDialog?.EmitSignal(AcceptDialog.SignalName.Canceled);
        await Wait(menu, 0.3);
        LaunchGuard.TestNoLanguage = false;
        Check("the plain dialog: the turn-off as OK, Quit as Cancel, Restore as a third button, Mod Doctor's words under \"From Mod Doctor:\"; Quit quits",
            asked.IsCompleted && quits == 1 && plainDialog is { OkButtonText: "Turn off Test Fixes and restart", CancelButtonText: "Quit" }
            && plainSays.Contains("Restore and restart") && plainSays.Contains("From Mod Doctor:\n" + doctorWords), plainSays);

        // At its longest: a library and the mods that need it, three already off, the streak, a restore with five changes, and
        // Mod Doctor's words: the popup's text still fits, both boxes drawn
        TurnedOff.Add([OffEntry("SmmTestMadeUpA", at: DateTimeOffset.Now.AddMinutes(-20)), OffEntry("SmmTestMadeUpB", at: DateTimeOffset.Now.AddMinutes(-15)),
            OffEntry("SmmTestMadeUpC", at: DateTimeOffset.Now.AddMinutes(-10))]);
        var five = Offer([.. Enumerable.Range(1, 5).Select(i => (LaunchGuard.Kind.New, $"SmmTestMadeUp{i}"))]);
        (asked, words) = await Show(Crash(Blamed(Culprits.Why.Code, "SmmTestLib") with { Needing = ["SmmTestContent", "SmmTestUi"], Streak = 3, DoctorAgrees = true, DoctorText = doctorWords }, five));
        await Capture(menu, outDir, "1j-crash-culprit-longest");
        var text = (NModalContainer.Instance?.OpenModal as Node)?.GetNodeOrNull<RichTextLabel>("VerticalPopup/Description");
        var boxes = text == null ? "no text" : BoxesWrong(text, 2);
        PressOurs("NoButton");
        await Wait(menu, 0.3);
        FreshRecord();
        Check("the crash culprit's dialog at its longest (a library and its dependents, three already off, the streak, a restore with five changes, Mod "
            + "Doctor's words): the text fits the popup, both boxes drawn", asked.IsCompleted && boxes == null && text != null && text.GetContentHeight() <= text.Size.Y + 1
            && words.Contains("Turned off after the last failed start") && words.Contains("From Mod Doctor"), $"{boxes}; {text?.GetContentHeight()} tall in {text?.Size}; {words}");

        // A write that fails shows the dialog once more, saying so, with Quit alone: a turn-off whose save fails, a Restore
        // whose save fails; nothing relaunched
        async Task<string> FailedAgain(LaunchGuard.Crash crash)
        {
            LaunchGuard.TestSaveFails = () => true;
            var (again, _) = await Show(crash);
            PressOurs("YesButton");
            await Wait(menu, 0.5);
            LaunchGuard.TestSaveFails = null;
            var said = $"{ShownWords()} | no button visible: {(NModalContainer.Instance?.OpenModal as Node)?.GetNodeOrNull<Control>("VerticalPopup/NoButton")?.Visible != true}";
            PressOurs("YesButton");
            await Wait(menu, 0.3);
            return $"{said} | answered {again.IsCompleted}";
        }
        var unsaved = await FailedAgain(Crash(Blamed(Culprits.Why.Code, "SmmTestFixes")));
        var unrestoredSaid = await FailedAgain(Crash(null, otherNew));
        Check("a write that fails shows the dialog again, saying so, with Quit alone: a turn-off whose save fails, a Restore whose save fails; nothing "
            + "relaunched, and Quit quits", unsaved.Contains("couldn't turn off Test Fixes") && unsaved.Contains("no button visible: True") && unsaved.Contains("answered True")
            && unrestoredSaid.Contains("couldn't restore the last working setup") && unrestoredSaid.Contains("answered True") && relaunches == 8 && quits == 4 && !AllOff("SmmTestFixes"),
            $"{unsaved} || {unrestoredSaid}; relaunches {relaunches}, quits {quits}");
    }

    // The notice at the next main menu: its words by why (as Notice has them in plain English), then the popup: Turn back
    // on (the switch it had, the entry gone), one whose save fails (said, the entry kept), Keep off, shown once a session
    // with its file read-only, an entry whose error came back first with its own link; and the first main menu's marks
    static async Task TestTurnedOffNotice(NMainMenu menu)
    {
        if (SaveManager.Instance.SettingsSave.ModSettings is not { } settings || Dependencies.Find("SmmTestFixes") is not { } fixes)
        {
            Results.Add("SKIP the notice of mods turned off: no mod list, or no Test Fixes");
            return;
        }
        var file = TurnedOff.TestFile ?? "";
        void Recorded(params TurnedOff.Entry[] entries)
        {
            FreshRecord();
            TurnedOff.Add(entries);
        }
        void Off(string id, bool off = true)
        {
            foreach (var e in settings.ModList.Where(e => e.Id == id))
                e.IsEnabled = !off;
        }
        bool On(string id) => settings.ModList.Any(e => e.Id == id) && settings.ModList.Where(e => e.Id == id).All(e => e.IsEnabled);
        string Body(params TurnedOff.Entry[] entries) => TurnedOff.Notice(entries, plain: true).Body;
        var at = DateTimeOffset.Now.AddMinutes(-30);
        string[] bodies =
        [
            Body(OffEntry("SmmTestFixes", Culprits.Why.Code, at)), Body(OffEntry("SmmTestFixes", Culprits.Why.Stale, at)),
            Body(OffEntry("SmmTestFixes", Culprits.Why.Patch, at)), Body(OffEntry("SmmTestFixes", Culprits.Why.Updated, at)),
            Body(OffEntry("SmmTestFixes", Culprits.Why.RebuildLast, at)), Body(OffEntry("SmmTestFixes", Culprits.Why.Suspect, at)),
            Body(OffEntry("SmmTestFixes", at: at), OffEntry("SmmTestUi", at: at)),
        ];
        var date = LaunchGuard.When(at.LocalDateTime);
        var withDependent = Body(OffEntry("SmmTestLib", at: at), OffEntry("SmmTestUi", Culprits.Why.Needs, at, needs: "SmmTestLib"));
        var updatedSince = Body(OffEntry("SmmTestFixes", at: at) with { Version = "0.0.1" });
        Check("the notice's words by why: \"stopped\" for its own code and code that can't run on this game; \"most likely\" for a patch, an update, a failed "
            + "rebuild; Mod Doctor's suspicion for Disable suspects; a group, a line each, claiming nothing; the date; the mods turned off with it; an update since",
            bodies[0].StartsWith($"Test Fixes stopped the game from starting on {date}") && bodies[1].StartsWith("Test Fixes stopped")
            && bodies[2..5].All(b => b.StartsWith("Test Fixes most likely stopped")) && bodies[5].Contains("Mod Doctor suspected it")
            && bodies[6].StartsWith("Spire Mod Manager turned these mods off") && bodies[6].Contains($"  •  Test Fixes ({date})") && !bodies[6].Contains("stopped")
            && withDependent.Contains("The mods that need it were turned off with it: Test UI.") && updatedSince.Contains("It has been updated since (0.0.1 → 0.1.0)"),
            string.Join(" || ", bodies.Append(withDependent).Append(updatedSince)));

        // The popup: its words and our mark; Turn back on turns Test Fixes on again, the entry gone
        Off("SmmTestFixes");
        Recorded(OffEntry("SmmTestFixes", at: at));
        TurnedOff.ShowNotice(menu);
        await Wait(menu, 0.5);
        var words = ShownWords();
        CheckMark("the notice of mods turned off", NModalContainer.Instance?.OpenModal as Control);
        var shownFirst = TurnedOff.Entries.All(e => e.Shown);
        PressOurs("YesButton");
        await Wait(menu, 0.3);
        Check("the notice: \"A mod was turned off\", Test Fixes and when, Keep off and Turn back on, marked shown as it opened; Turn back on turns Test Fixes "
            + "on again and its entry goes", words.Contains("A mod was turned off") && words.Contains("Test Fixes stopped the game from starting on")
            && words.Contains(date) && words.Contains("Keep off") && words.Contains("Turn back on") && shownFirst && On("SmmTestFixes") && !TurnedOff.Has("SmmTestFixes"),
            $"{words}; shown {shownFirst}, on {On("SmmTestFixes")}, entry {TurnedOff.Has("SmmTestFixes")}");

        // Its save failing: said in a popup of its own, the entry kept. Keep off: the entry kept, shown.
        Off("SmmTestFixes");
        Recorded(OffEntry("SmmTestFixes", at: at));
        LaunchGuard.TestSaveFails = () => true;
        TurnedOff.ShowNotice(menu);
        await Wait(menu, 0.5);
        PressOurs("YesButton");
        await Wait(menu, 0.5);
        LaunchGuard.TestSaveFails = null;
        var failedWords = ShownWords();
        var failedKept = (TurnedOff.Has("SmmTestFixes"), On("SmmTestFixes"));
        NModalContainer.Instance?.Clear();
        Recorded(OffEntry("SmmTestFixes", at: at));
        TurnedOff.ShowNotice(menu);
        await Wait(menu, 0.5);
        PressOurs("NoButton");
        await Wait(menu, 0.3);
        Check("Turn back on whose save fails says so, and the entry stays; Keep off keeps the entry, shown, the mod off",
            failedWords.Contains("Couldn't turn Test Fixes back on") && failedKept == (true, false)
            && TurnedOff.Entries is [{ Id: "SmmTestFixes", Shown: true }] && !On("SmmTestFixes"), $"{failedWords}; {failedKept}; {string.Join(", ", TurnedOff.Entries)}");

        // With its file read-only, the mark goes into memory, so the notice shows once a session (the main menu comes back after
        // every run). Without it in memory, every menu would show it again.
        Recorded(OffEntry("SmmTestFixes", at: at));
        File.SetAttributes(file, FileAttributes.ReadOnly);
        TurnedOff.ShowNotice(menu);
        await Wait(menu, 0.5);
        var once = NModalContainer.Instance?.OpenModal != null;
        NModalContainer.Instance?.Clear();
        await Wait(menu, 0.2);
        TurnedOff.ShowNotice(menu);
        await Wait(menu, 0.5);
        var twice = NModalContainer.Instance?.OpenModal != null;
        NModalContainer.Instance?.Clear();
        File.SetAttributes(file, FileAttributes.Normal);
        Check("with its file read-only, the notice shows once a session", once && !twice && File.ReadAllText(file).Contains("\"shown\": false"), $"first {once}, again {twice}");

        // The start failing the same way after an entry: that entry first, its line, and a link that turns it alone back on
        Off("SmmTestFixes");
        Off("SmmTestUi");
        Recorded(OffEntry("SmmTestFixes", at: at.AddMinutes(-5)) with { SameErrorAfter = true }, OffEntry("SmmTestUi", at: at));
        TurnedOff.ShowNotice(menu);
        await Wait(menu, 0.5);
        var againWords = ShownWords();
        var againText = (NModalContainer.Instance?.OpenModal as Node)?.GetNodeOrNull<RichTextLabel>("VerticalPopup/Description")?.GetParsedText() ?? "";
        var againLink = ((NModalContainer.Instance?.OpenModal as Node)?.FindChild(LaunchGuard.LinkName, true, false) as LinkButton)?.Text;
        PressLink();
        await Wait(menu, 0.3);
        Check("an entry whose error came back after it goes first, with its line and \"Turn Test Fixes back on\", which turns it alone on",
            againWords.Contains("Mods were turned off") && againText.StartsWith("The same error came back after Test Fixes was turned off, so it most likely wasn't the cause.")
            && againLink == "Turn Test Fixes back on" && On("SmmTestFixes") && !On("SmmTestUi") && !TurnedOff.Has("SmmTestFixes") && TurnedOff.Has("SmmTestUi"),
            $"{againText}; link {againLink}; {string.Join(", ", TurnedOff.Entries.Select(e => e.Id))}");

        // The first main menu, after the list is settled: every entry marked; one whose mod is on again goes; one whose mod
        // is gone goes where its source was scanned (local: always; the Workshop: with Steam only)
        Off("SmmTestFixes");
        Off("SmmTestUi", off: false);
        Recorded(OffEntry("SmmTestFixes", at: at), OffEntry("SmmTestUi", at: at), OffEntry("SmmTestGone", at: at),
            OffEntry("SmmTestGoneWorkshop", at: at) with { Switches = [new(ModSource.SteamWorkshop, true, ["w:3700000009"])] });
        TurnedOff.TestSteam = false;
        TurnedOff.MenuReached();
        var offline = string.Join(", ", TurnedOff.Entries.Select(e => $"{e.Id} {(e.MenuSince ? "seen" : "not seen")}"));
        TurnedOff.TestSteam = true;
        TurnedOff.MenuReached();
        var online = string.Join(", ", TurnedOff.Entries.Select(e => e.Id));
        Check("at the first main menu every entry is marked, one on again goes, one gone goes where its source was scanned (a Workshop one only with Steam)",
            offline == "SmmTestFixes seen, SmmTestGoneWorkshop seen" && online == "SmmTestFixes", $"without Steam: {offline}; with it: {online}");
    }

    // Restore and profiles keep a mod turned off after a failed start off: ours, Mod Doctor's, and Mod Doctor itself when an
    // entry of ours names it; a restore offer whose only change is such a mod off is none; the needs lines say so
    static Task TestTurnedOffKept(string outDir)
    {
        if (SaveManager.Instance.SettingsSave.ModSettings is not { } settings || Dependencies.Find("SmmTestFixes") is not { } fixes
            || Dependencies.Find("SmmTestUi") is not { } ui)
        {
            Results.Add("SKIP Restore and profiles keeping turned-off mods off: no mod list, or no Test Fixes or Test UI");
            return Task.CompletedTask;
        }
        var working = History.CurrentMods().Where(m => m.Id != null).Select(m => new LaunchGuard.Entry(m.Id, m.Source, true, null))
            .Append(new LaunchGuard.Entry(ModDoctor.ModId, ModSource.ModsDirectory, true, null)).ToList();
        bool OnIn(List<SettingsSaveMod> list, string id) => list.Where(e => e.Id == id).Any(e => e.IsEnabled);
        TurnedOff.Add([OffEntry("SmmTestFixes")]);
        var ours = LaunchGuard.Written(working);
        var top = Path.Combine(TurnOffDir(outDir), "mod-doctor");
        var account = UserDataPathProvider.GetAccountScopedBasePath(null).Replace("user://", "");
        var record = Path.Combine(top, "mod_data", ModDoctor.ModId, account, "turned-off.json");
        Directory.CreateDirectory(Path.GetDirectoryName(record) ?? top);
        File.WriteAllText(record, new JsonObject { ["version"] = 1, ["mods"] = new JsonArray(new JsonObject { ["id"] = "SmmTestUi", ["at"] = DateTimeOffset.Now.ToString("o") }) }.ToJsonString());
        ModDoctor.TestFolder = top;
        var doctors = LaunchGuard.Written(working);
        TurnedOff.Add([OffEntry(ModDoctor.ModId)]);
        var naming = LaunchGuard.Written(working);
        ModDoctor.TestFolder = null;
        Check("Restore writes a mod turned off after a failed start off, ours (Test Fixes) and Mod Doctor's (Test UI); and Mod Doctor itself off when an entry "
            + "of ours names it, though Restore keeps it as it is otherwise",
            !OnIn(ours, "SmmTestFixes") && OnIn(ours, "SmmTestUi") && OnIn(ours, ModDoctor.ModId) && !OnIn(doctors, "SmmTestUi") && !OnIn(naming, ModDoctor.ModId),
            $"ours: Fixes {OnIn(ours, "SmmTestFixes")}, UI {OnIn(ours, "SmmTestUi")}; Mod Doctor's: UI {OnIn(doctors, "SmmTestUi")}; naming it: {OnIn(naming, ModDoctor.ModId)}");

        // An offer whose only change is a turned-off mod off: none (Restore keeps it off, so it would loop)
        var now = DateTime.Now;
        var planned = new LaunchGuard.Setup(now, [new("SmmTestFixes", ModSource.ModsDirectory, false, "0.1.0"), new("SmmTestUi", ModSource.ModsDirectory, true, "1.0.0")], CulpritGame);
        var before = new LaunchGuard.Setup(now.AddHours(-1), [new("SmmTestFixes", ModSource.ModsDirectory, true, "0.1.0"), new("SmmTestUi", ModSource.ModsDirectory, true, "1.0.0")], CulpritGame);
        Check("a restore offer whose only change is a mod turned off after a failed start is none; for any other mod off, offered",
            LaunchGuard.CrashOffer(planned, before, new HashSet<string> { "SmmTestFixes" }) == null && LaunchGuard.CrashOffer(planned, before, new HashSet<string>()) != null);

        // A profile with Test Fixes on keeps it off while its entry stands (switching, Discard and an import never name it);
        // its own tickbox turns it on, and the entry drops out
        foreach (var e in settings.ModList.Where(e => e.Id == "SmmTestFixes"))
            e.IsEnabled = false;
        var profile = Profiles.TestCurrent().Select(m => m.Id == "SmmTestFixes" ? m with { Enabled = true } : m).ToList();
        FreshRecord();
        TurnedOff.Add([OffEntry("SmmTestFixes")]);
        Profiles.TestApply(profile);
        var heldOff = !Copies.Ticked(fixes);
        Dependencies.Write([(fixes, true)]);
        var tickedOn = Copies.Ticked(fixes);
        var dropped = TurnedOff.StillOff().All(e => e.Id != "SmmTestFixes");
        Check("a profile with Test Fixes on keeps it off while its entry stands; its own tickbox turns it on, and the entry drops out",
            heldOff && tickedOn && dropped && !TurnedOff.Has("SmmTestFixes"), $"held {heldOff}, ticked {tickedOn}, dropped {dropped}");

        // A mod that needs one turned off says so: the row's needs line, the info panel's and the relaunch check's
        FreshRecord();
        if (Dependencies.Find("SmmTestLib") is { } library)
        {
            TurnedOff.Add([OffEntry("SmmTestLib")]);
            Dependencies.Write([(library, false)]);
            var needs = RowStrip.Needs(ui, new Dictionary<Mod, int>(), loadOrder: false);
            var problems = string.Join(" | ", ModDetails.DependencyProblems(ui));
            Dependencies.Write([(library, true)]);
            Check("a mod that needs one turned off after a failed start says so in its needs line and its problems (the relaunch check's lines)",
                needs.Contains("(turned off after a failed start)") && problems.Contains("Test Framework (turned off after a failed start)"), $"{needs} | {problems}");
        }
        else
            Results.Add("SKIP the needs lines of a mod turned off: no Test Framework");
        return Task.CompletedTask;
    }

    // The takeover's own steps: the start failing after its first main menu takes back what that menu recorded; the
    // game's own line logged once; the takeover in its try; and the repair prefixes first in line, void
    static async Task TestCrashTakeover(NMainMenu menu, string outDir)
    {
        // The last working setup as it was before the launch (none if none), and the menu's marks on the turned-off mods
        // (not the ones from before)
        var dir = Path.Combine(TurnOffDir(outDir), "undo");
        Directory.CreateDirectory(dir);
        var was = Launched(CulpritGame) with { At = DateTime.Now.AddHours(-2) };
        LaunchGuard.PutWorking(dir, was);
        LaunchGuard.Reached(dir, Launched(CulpritGame), null, null); // this launch's menu saves it as the last working one
        LaunchGuard.PutWorking(dir, was);
        var back = LaunchGuard.Read(Path.Combine(dir, LaunchGuard.WorkingFile))?.At == was.At;
        LaunchGuard.PutWorking(dir, null);
        var none = !File.Exists(Path.Combine(dir, LaunchGuard.WorkingFile));
        if (SaveManager.Instance.SettingsSave.ModSettings is { } settings)
            foreach (var e in settings.ModList.Where(e => e.Id is "SmmTestFixes" or "SmmTestUi"))
                e.IsEnabled = false; // still off at the menu, so it keeps their entries
        TurnedOff.Add([OffEntry("SmmTestFixes"), OffEntry("SmmTestUi", at: DateTimeOffset.Now.AddMinutes(-3), menu: true)]);
        TurnedOff.MenuReached();
        var marked = TurnedOff.Entries.All(e => e.MenuSince);
        TurnedOff.UndoMenuMarks();
        var undone = string.Join(", ", TurnedOff.Entries.Select(e => $"{e.Id} {e.MenuSince}"));
        Check("a start that fails after its first main menu takes back what that menu recorded: the last working setup as it was (none if there was none), "
            + "and the menu's marks on the turned-off mods, not the ones from before", back && none && marked && undone == "SmmTestFixes False, SmmTestUi True",
            $"back {back}, none {none}, marked {marked}, undone {undone}");

        // The game's own line as ours takes over, once (the game's method, which writes it, is skipped)
        if (NGame.Instance is { } game && Compat.LogCallback != null)
        {
            var lines = 0;
            Action<LogLevel, string, int> count = (_, text, _) => lines += text.Contains("Encountered error on game startup! Attempting to show error dialog") ? 1 : 0;
            Compat.LogCallback.AddEventHandler(null, count);
            ModEntry.TestQuit = () => { };
            var asked = LaunchGuard.AskAfterCrash(game, new(new(Launched(CulpritGame), [new(LaunchGuard.Kind.New, "SmmTestRetired")], false), null, null, Launched(CulpritGame)));
            await Wait(menu, 0.8);
            PressOurs("NoButton");
            await Wait(menu, 0.3);
            Compat.LogCallback.RemoveEventHandler(null, count);
            Check("our dialog logs the game's own line as it takes over, once", asked.IsCompleted && lines == 1, $"{lines} times, answered {asked.IsCompleted}");
        }
        else
            Results.Add("SKIP the game's own line as ours takes over: no game, or no log callback");

        // The takeover in its try: ours throwing before it returns its task lets the game's own dialog through, logged.
        // Called as Harmony calls it; with the throw taken out it would start our dialog for real.
        if (StartupErrorPatch.GaveWay)
            Results.Add("SKIP the takeover's try: the game's own dialog was given way to already this session");
        else if (NGame.Instance is { } game2 && AccessTools.Method(typeof(StartupErrorPatch), "Prefix") is { } prefix && Compat.LogCallback != null)
        {
            var errors = 0;
            Action<LogLevel, string, int> count = (level, text, _) => errors += level == LogLevel.Error && text.Contains("Could not offer anything after the failed start") ? 1 : 0;
            Compat.LogCallback.AddEventHandler(null, count);
            LaunchGuard.TestTakeoverThrows = true;
            object?[] args = [game2, null];
            var through = prefix.Invoke(null, args) is true;
            LaunchGuard.TestTakeoverThrows = false;
            Compat.LogCallback.RemoveEventHandler(null, count);
            Check("the takeover in its try: ours throwing before it returns its task lets the game's own dialog through, and says so in the log",
                through && errors == 1 && NModalContainer.Instance?.OpenModal == null, $"through {through}, logged {errors}");
        }
        else
            Results.Add("SKIP the takeover's try: no game, no prefix, or no log callback");

        // The repair before any save: void prefixes first in line on the game's startup error and on its quit (one that
        // returned bool would be skipped where another mod's prefix takes over the dialog)
        static PatchInfoView? Prefix(MethodBase? method, Type patch) => method == null ? null
            : Harmony.GetPatchInfo(method)?.Prefixes.Where(p => p.PatchMethod.DeclaringType == patch).Select(p => new PatchInfoView(p.priority, p.PatchMethod.ReturnType)).FirstOrDefault();
        var onError = Prefix(AccessTools.Method(typeof(NGame), "GameStartupError"), typeof(StartupErrorRepairPatch));
        var onQuit = Prefix(AccessTools.Method(typeof(NGame), nameof(NGame.Quit)), typeof(QuitRepairPatch));
        Check("the mod list's repair before a failed start's saves: void prefixes, first in line, on the game's startup error and on its quit",
            onError is { Priority: Priority.First } && onError.Returns == typeof(void) && onQuit is { Priority: Priority.First } && onQuit.Returns == typeof(void),
            $"{onError}; {onQuit}");
    }

    record PatchInfoView(int Priority, Type Returns);

    // Our page's banner for mods turned off after a failed start: yellow, naming the mod, its card each with its date; Turn
    // back on turns it on (one more undo step) and the banner goes; Dismiss drops the entry, the mod staying off; one on
    // again by its own tickbox drops out at the next redraw
    static async Task TestTurnedOffBanner(NMainMenu menu, NModdingScreen screen, Control content, string outDir)
    {
        if (Dependencies.Find("SmmTestFixes") is not { } fixes || !Copies.Ticked(fixes))
        {
            Results.Add("SKIP the turned-off banner: no Test Fixes on");
            return;
        }
        var dir = Path.Combine(TurnOffDir(outDir), "banner");
        Emptied(dir);
        TurnedOff.TestFile = Path.Combine(dir, TurnedOff.FileName);
        var border = screen.GetNode<Control>("ModsBorder");
        Control? Banner() => border.GetNodeOrNull<Control>(LoadOrder.TurnedOffName);
        string Said() => Banner()?.FindChild("TurnedOffWarning", true, false) is Label { } label ? label.Text : "";
        void BannerPress(string name) => (Banner()?.FindChild(name, true, false) as BaseButton)?.EmitSignal(BaseButton.SignalName.Pressed);
        async Task TurnedOffNow()
        {
            Dependencies.Set([fixes], false, screen);
            TurnedOff.Add([OffEntry("SmmTestFixes", at: DateTimeOffset.Now.AddMinutes(-30))]);
            CategoryList.Redraw(content);
            await Wait(menu, 0.3);
        }
        try
        {
            await TurnedOffNow();
            var (shown, said, fill) = (Banner() is { Visible: true }, Said(), BannerFill(Banner()));
            var card = string.Join(" | ", await BannerCard(menu, screen, Banner(), "2x-turned-off-banner", outDir));
            var steps = History.Steps(History.Track.List);
            BannerPress("TurnBackOnButton");
            await Wait(menu, 0.3);
            Check("the turned-off banner: yellow, \"Test Fixes was turned off after a failed start\", its card naming it with when; Turn back on turns it on (one "
                + "more undo step) and the banner goes", shown && said == "Test Fixes was turned off after a failed start" && Yellow(fill)
                && card.Contains("Turned off after a failed start") && card.Contains("Test Fixes (") && Copies.Ticked(fixes) && !TurnedOff.Has("SmmTestFixes")
                && Banner() == null && History.Steps(History.Track.List) == steps + 1, $"{shown} \"{said}\" {fill}; card {card}; on {Copies.Ticked(fixes)}, banner {Banner() != null}");
            await TurnedOffNow();
            BannerPress("DismissTurnedOffButton");
            await Wait(menu, 0.3);
            var dismissed = (!TurnedOff.Has("SmmTestFixes"), !Copies.Ticked(fixes), Banner() == null);
            await TurnedOffNow();
            Dependencies.Set([fixes], true, screen);
            CategoryList.Redraw(content);
            await Wait(menu, 0.3);
            Check("the turned-off banner's Dismiss drops the entry, the mod staying off; a mod turned on by its own tickbox drops out at the next redraw",
                dismissed == (true, true, true) && !TurnedOff.Has("SmmTestFixes") && Banner() == null, $"dismissed {dismissed}; after its tickbox {TurnedOff.Has("SmmTestFixes")}");
        }
        finally
        {
            TurnedOff.TestFile = null;
            if (!Copies.Ticked(fixes))
                Dependencies.Set([fixes], true, screen);
            CategoryList.Redraw(content);
        }
    }
}
