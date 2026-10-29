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

    // One part: a fresh record file of the test's, and after it, though it threw midway, the state put back first (every
    // hook, the game's mod list, the active profile's unsaved edit, both undo histories, this launch's record), then the
    // popups and the test's folder, each on its own: one that can't be put back is a FAIL naming it, and the rest still
    // are (Turn back on and a tickbox record the lists a part makes up, and its pressed answers delete the record)
    static async Task TurnOffPart(Func<Task> test, string outDir, [CallerArgumentExpression(nameof(test))] string name = "")
    {
        var settings = SaveManager.Instance.SettingsSave.ModSettings;
        var listWas = settings?.ModList.Select(e => new SettingsSaveMod { Id = e.Id, Source = e.Source, IsEnabled = e.IsEnabled }).ToList();
        var history = History.TestKeep();
        var profile = Profiles.Active;
        var (edit, editedAt) = (profile?.Edited, profile?.EditedAt);
        var pending = Path.Combine(LaunchGuard.Dir ?? OS.GetUserDataDir(), LaunchGuard.PendingFile);
        var pendingWas = File.Exists(pending) ? File.ReadAllBytes(pending) : null;
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
            void Back(string what, Action put)
            {
                try
                {
                    put();
                }
                catch (Exception e)
                {
                    Results.Add($"FAIL {name}: couldn't put back {what}: {e}");
                }
            }
            (ModEntry.TestQuit, ModEntry.TestRelaunch) = (null, null);
            (LaunchGuard.TestNoLanguage, LaunchGuard.TestSaveFails, LaunchGuard.TestTakeoverThrows) = (false, null, false);
            (ModDoctor.TestSuspects, ModDoctor.TestFolder, ModDoctor.TestLoaded, TurnedOff.TestSteam, TurnedOff.TestNoPopup) = (null, null, null, null, false);
            TurnedOff.TestFile = null;
            Back("the game's mod list", () =>
            {
                if (settings == null || listWas == null)
                    return;
                settings.ModList.Clear();
                settings.ModList.AddRange(listWas);
                SaveManager.Instance.SaveSettings();
            });
            Back("the active profile's edit", () =>
            {
                if (profile == null)
                    return;
                (profile.Edited, profile.EditedAt) = (edit, editedAt);
                Store.SaveViewState();
            });
            Back("the profile bar", Profiles.NotifySwitched); // its "Unsaved" as it was, though the save above threw
            Back("the undo histories", () => History.TestPutBack(history));
            Back("this launch's record", () =>
            {
                if (pendingWas != null)
                    File.WriteAllBytes(pending, pendingWas);
                else
                    File.Delete(pending);
            });
            Back("the popups", () =>
            {
                NModalContainer.Instance?.Clear();
                ((SceneTree)Engine.GetMainLoop()).Root.GetNodeOrNull(LaunchGuard.PlainDialogName)?.QueueFree();
                if (NGame.Instance is { } game && transitionWas is { } visible)
                    game.Transition.Visible = visible; // the dialog's setup hides the startup transition
            });
            Back("the test's folder", () => Emptied(dir));
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

        // A source of the culprit with no copy installed, on as the game read it: a Workshop item unsubscribed since (its entry
        // still in the list), or one the game didn't list, launched without Steam (the list as rebuilt without its entry, so
        // a save would lose it). It loads once it's back, so it's turned off too, saved and recorded; Turn back on puts back
        // exactly the switches recorded, installed or not.
        if (ModManager.Mods.Union(Installed.Mods).Any(m => m.manifest?.id == "SmmTestFixes" && m.modSource == W))
            Results.Add("SKIP a turn-off of a mod with a source not installed: Test Fixes is installed from the Workshop too");
        else
        {
            List<(string, ModSource)> local = [("SmmTestFixes", L)];
            static string Order(IEnumerable<SettingsSaveMod> list) => string.Join(",", list.Select(e => e.Source == W ? $"{e.Id}/w" : e.Id));
            (string Error, string List, string Disk, TurnedOff.Entry? Entry, string Back, string Order) Uninstalled(params SettingsSaveMod[] list)
            {
                FreshRecord();
                Swap(list);
                var failed = LaunchGuard.TurnOff(settings, Read(ours, ("SmmTestFixes", L, true), ("SmmTestFixes", W, true)), false, fixes);
                var (entry, after) = (Recorded("SmmTestFixes"), settings.ModList.Select(e => S(e.Id, e.Source, e.IsEnabled)).ToList());
                TurnedOff.SwitchOn(after, entry == null ? [] : [entry], local);
                return (failed?.Key ?? "", Of("SmmTestFixes"), SwitchedOnDisk("SmmTestFixes"), entry, Switched(after, "SmmTestFixes"), Order(settings.ModList));
            }
            var unsubscribed = Uninstalled(S(ModEntry.ModId, L, true), S("SmmTestFixes", L, true), S("SmmTestFixes", W, true));
            var unlisted = Uninstalled(S(ModEntry.ModId, L, true), S("SmmTestFixes", L, true), S("SmmTestUi", L, true));
            // The installed local copy with no entry of its own (only the Workshop one listed): added right after the mod's
            // entry, not at the end (round 5's review: reverting that insert to an append survived every row)
            var localUnlisted = Uninstalled(S(ModEntry.ModId, L, true), S("SmmTestFixes", W, true), S("SmmTestUi", L, true));
            static bool Both(TurnedOff.Entry? entry) => entry is { Switches: [{ Source: L, On: true }, { Source: W, On: true, Copies: [] }] };
            Check("a turn-off also turns off a source of the mod with no copy installed that was on (a Workshop item unsubscribed, one not listed without "
                + "Steam, its entry added right after the mod's own), in memory and in settings.save, and records its switch; Turn back on puts both back",
                new[] { unsubscribed, unlisted }.All(t => t is { Error: "", List: "l-,w-", Disk: "l-,w-", Back: "l+,w+" } && Both(t.Entry))
                && unlisted.Order == $"{ModEntry.ModId},SmmTestFixes,SmmTestFixes/w,SmmTestUi" && localUnlisted.Order == $"{ModEntry.ModId},SmmTestFixes/w,SmmTestFixes,SmmTestUi",
                $"unsubscribed {unsubscribed}; not listed {unlisted}; local copy unlisted {localUnlisted.Order}");

            // The next launch: the game's list rebuilt without the Workshop entry (it lists only the copies it found). Its
            // repair puts the entry back, off, right after the mod's own (its place in the load order kept), so a save keeps
            // it and the copy stays off once it's back: before the first main menu (RepairBeforeMenu), with the list as the
            // game read it or without; at the first main menu (Settle), without it, and with ours moved first in the same
            // call. Not for an entry whose mod is on again, nor one whose mod is gone (Test Gone, its local copy scanned).
            TurnedOff.Add([OffEntry("SmmTestGone")]);
            SettingsSaveMod[] Rebuilt() => [S(ModEntry.ModId, L, true), S("SmmTestFixes", L, false), S("SmmTestUi", L, true)];
            var rebuiltRead = Read(ours, ("SmmTestFixes", L, false), ("SmmTestFixes", W, false), ("SmmTestUi", L, true));
            Swap(Rebuilt());
            var keptOff = (LaunchGuard.RepairBeforeMenu(settings, rebuiltRead, false), Of("SmmTestFixes"), Order(settings.ModList));
            Swap(Rebuilt());
            var unread = (LaunchGuard.RepairBeforeMenu(settings, null, false), Order(settings.ModList));
            List<SettingsSaveMod> menuList = [.. Rebuilt()], moved = [S("SmmTestFixes", L, false), S(ModEntry.ModId, L, true), S("SmmTestUi", L, true)];
            var (atMenu, movedToo) = ((SameIdLoad.Settle(menuList, null), Order(menuList)), (SameIdLoad.Settle(moved, rebuiltRead), Order(moved)));
            Swap(S(ModEntry.ModId, L, true), S("SmmTestFixes", L, true));
            var onAgain = (TurnedOff.KeepOff(settings.ModList), Of("SmmTestFixes"));
            var after = $"{ModEntry.ModId},SmmTestFixes,SmmTestFixes/w,SmmTestUi";
            Check("a turned-off mod's Workshop entry the game's rebuilt list dropped is put back, off, right after its own, at the next launch's repair (before "
                + "the first main menu with the list as read and without it, at the first main menu without it and with ours moved first), so it stays off "
                + "once the copy is back; not once the mod is on again, nor for a mod gone",
                keptOff == (true, "l-,w-", after) && unread == (true, after) && atMenu == (true, after) && movedToo == (true, after) && onAgain == (false, "l+"),
                $"repaired {keptOff}; without the list as read {unread}; at the menu {atMenu}; ours moved too {movedToo}; on again {onAgain}");
        }

        // Disable suspects writes entries too (why Suspect). Turn off all mods: every mod on, one with no entry as read too
        // (Test Content), never ours; one already off (Test UI, as read before the menu and as the list is after it) is
        // neither turned off again nor recorded.
        FreshRecord();
        Swap(S(ModEntry.ModId, L, true), S("SmmTestFixes", L, true), S("SmmTestUi", L, true));
        var suspected = LaunchGuard.TurnOff(settings, Read(ours, ("SmmTestFixes", L, true), ("SmmTestUi", L, true)), false,
            new LaunchGuard.Off([("SmmTestUi", Culprits.Why.Suspect, null), ("SmmTestFixes", Culprits.Why.Suspect, null)], "", ""));
        var suspects = (Recorded("SmmTestUi")?.Why, Recorded("SmmTestFixes")?.Why, Of("SmmTestUi"));
        FreshRecord();
        var all = LaunchGuard.AllOff("InvalidOperationException: made up", "made up|signature", rebuild: false);
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
        var again = (Recorded("SmmTestUi")?.SameErrorAfter, Recorded("SmmTestFixes")?.SameErrorAfter);
        // Not between two failed rebuilds, by the failures' own flag whatever rule picked each mod (Mod Doctor's pick, then
        // ours): any bad build of a method reads the same
        FreshRecord();
        TurnedOff.Add([OffEntry("SmmTestUi", Culprits.Why.DoctorThrew, DateTimeOffset.Now.AddMinutes(-2)) with { Rebuild = true }]);
        TurnedOff.Add([OffEntry("SmmTestFixes", Culprits.Why.RebuildLast) with { Rebuild = true }]);
        var rebuilds = Recorded("SmmTestUi")?.SameErrorAfter;
        // A failed rebuild, then the same signature from a failure that isn't one, and the other way round: marked (only
        // both rebuilds are let off)
        FreshRecord();
        TurnedOff.Add([OffEntry("SmmTestUi", Culprits.Why.RebuildLast, DateTimeOffset.Now.AddMinutes(-2)) with { Rebuild = true }]);
        TurnedOff.Add([OffEntry("SmmTestFixes")]);
        var oneRebuild = Recorded("SmmTestUi")?.SameErrorAfter;
        FreshRecord();
        TurnedOff.Add([OffEntry("SmmTestUi", Culprits.Why.Code, DateTimeOffset.Now.AddMinutes(-2))]);
        TurnedOff.Add([OffEntry("SmmTestFixes", Culprits.Why.RebuildLast) with { Rebuild = true }]);
        var rebuildAfter = Recorded("SmmTestUi")?.SameErrorAfter;
        Check("a mod turned off again: one entry, the first switch kept for a source it had, the new one and its copies joined; the start failing the same "
            + "way after a turn-off marks that entry, but not one failed rebuild after another (by the failures' flag, Mod Doctor's pick then ours); a "
            + "rebuild then a failure that isn't one, or the other way round, marked",
            merged is [{ Source: L, On: true, Copies: ["l:SmmTestFixes", "l:SmmTestFixesFixed"] }, { Source: W, On: true }] && again == (true, false) && rebuilds == false
            && oneRebuild == true && rebuildAfter == true,
            $"{string.Join("; ", merged.Select(s => $"{s.Source} {s.On} {string.Join("+", s.Copies)}"))}; Test UI again {again}; after a rebuild {rebuilds}; "
            + $"after a rebuild, not one {oneRebuild}; a rebuild after one that isn't {rebuildAfter}");

        // Turn back on: exactly the switches recorded on, installed now or not. Of a mod recorded with its local copy off and
        // its Workshop one on, both installed, the Workshop switch alone; with no Workshop entry in the list, no entry
        // added. With the Workshop one not installed now (no source recorded on has a copy): unsubscribed (Steam up, its
        // source scanned), every installed copy too; not listed without Steam, the Workshop switch alone (the local copy
        // kept off would load in its place).
        static TurnedOff.Entry Was() => OffEntry("SmmTestFixes") with { Switches = [new(L, false, ["l:SmmTestFixes"]), new(W, true, ["w:1"])] };
        List<(string, ModSource)> installedBoth = [("SmmTestFixes", L), ("SmmTestFixes", W)], installedLocal = [("SmmTestFixes", L)];
        var both = new List<SettingsSaveMod> { S("SmmTestFixes", L, false), S("SmmTestFixes", W, false) };
        TurnedOff.SwitchOn(both, [Was()], installedBoth);
        var gone = new List<SettingsSaveMod> { S("SmmTestFixes", L, false) };
        TurnedOff.SwitchOn(gone, [Was()], installedBoth);
        string NotInstalled(bool steam)
        {
            TurnedOff.TestSteam = steam;
            var list = new List<SettingsSaveMod> { S("SmmTestFixes", L, false), S("SmmTestFixes", W, false) };
            TurnedOff.SwitchOn(list, [Was()], installedLocal);
            TurnedOff.TestSteam = null;
            return Switched(list, "SmmTestFixes");
        }
        var (unsubscribedOn, hiddenOn) = (NotInstalled(steam: true), NotInstalled(steam: false));
        Check("Turn back on turns on exactly the switches recorded on (the Workshop copy alone, installed now or not), adding no entry where the list has "
            + "none; with no copy installed now of a source recorded on, every installed copy too where that source was scanned (unsubscribed), the "
            + "recorded switch alone where it wasn't (not listed without Steam)",
            Switched(both, "SmmTestFixes") == "l-,w+" && Switched(gone, "SmmTestFixes") == "l-" && gone.Count == 1 && unsubscribedOn == "l+,w+" && hiddenOn == "l-,w+",
            $"{Switched(both, "SmmTestFixes")}; {Switched(gone, "SmmTestFixes")} ({gone.Count}); unsubscribed {unsubscribedOn}; not listed without Steam {hiddenOn}");

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
            code.Body.StartsWith(LaunchGuard.GamesMessage(plain: true)) && code.Body.Contains("An error while loading Test Fixes has prevented the game from starting.")
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
            At(agreed, "has prevented the game from starting") is >= 0 and var line && At(agreed, "From Mod Doctor:\n" + doctorWords) > line
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
            && self.Body.Contains("The error came from Spire Mod Manager itself.\n\nIt can turn itself off and restart the game. To use it again, enable it on the vanilla modding page.")
            && self.Body.Contains("These mods need it and won't load until it's on again: Test Fixes.")
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

        // A failed write: what went wrong first (the mods named), then the culprit's line, with Quit alone; ours, its line
        // without the turn-off it couldn't make; a Restore that couldn't save, then the offer's text
        var failed = Plain(Crash(Blamed(Culprits.Why.Code, "SmmTestFixes")), failed: new("SPIRE_MOD_MANAGER.CRASH_SAVE_FAILED", ["SmmTestFixes"]));
        var selfFailed = Plain(Crash(Blamed(Culprits.Why.Self, ModEntry.ModId)), failed: new("SPIRE_MOD_MANAGER.CRASH_SAVE_FAILED", [ModEntry.ModId]));
        var unrestored = Plain(Crash(null, otherNew), failed: new("SPIRE_MOD_MANAGER.CRASH_RESTORE_FAILED", []));
        Check("after a failed write the dialog says what went wrong first, then the culprit's line (or the offer's text), with Quit alone and no link; ours, "
            + "only that the error was its own",
            At(failed, "couldn't turn off Test Fixes: the game couldn't save its settings") is >= 0 and var said && At(failed, "An error while loading Test Fixes") > said
            && !failed.Body.Contains("Spire Mod Manager can turn off") && Said(failed) == "Quit alone | no link"
            && selfFailed.Body.EndsWith("The error came from Spire Mod Manager itself.") && !selfFailed.Body.Contains("turn itself off") && Said(selfFailed) == "Quit alone | no link"
            && unrestored.Body.Contains("couldn't restore the last working setup") && Said(unrestored) == "Quit alone | no link",
            $"{failed.Body} {Said(failed)} || {selfFailed.Body} {Said(selfFailed)} || {unrestored.Body} {Said(unrestored)}");

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
        var fit = PopupFit();
        PressOurs("YesButton");
        await Wait(menu, 0.3);
        var entry = TurnedOff.Entries.FirstOrDefault(e => e.Id == "SmmTestFixes");
        Check("the crash culprit's popup: the game's message, Test Fixes named, Quit and \"Turn off Test Fixes and restart\", no link, its text and the button's "
            + "fitting at a readable size; pressed, every switch of Test Fixes off, its record (Code, its switch with its copy), one plain relaunch, this "
            + "launch's record gone",
            pressed.IsCompleted && words.Contains("Notice from the game") && words.Contains("An error while loading Test Fixes has prevented the game from starting") && words.Contains("Quit")
            && words.Contains("Turn off Test Fixes and restart") && noLink && fit is { Fits: true, Size: >= ReadableText, Button: >= LaunchGuard.SmallestButtonText }
            && AllOff("SmmTestFixes") && relaunches == 1 && quits == 0
            && entry is { Why: Culprits.Why.Code, Switches: [{ On: true, Copies: ["l:SmmTestFixes"] }] } && (DateTimeOffset.Now - entry.At).TotalMinutes < 1
            && !File.Exists(pendingPath), $"{words}; {fit}; relaunches {relaunches}, quits {quits}; {entry}; record left {File.Exists(pendingPath)}");
        ListBack();

        // Beside the restore: the link, which restores the working list with a mod turned off after a failed start kept
        // off (Test UI here, on in that list)
        TurnedOff.Add([OffEntry("SmmTestUi", at: DateTimeOffset.Now.AddMinutes(-3))]);
        (pressed, words) = await Show(Crash(Blamed(Culprits.Why.Code, "SmmTestFixes"), otherNew));
        var link = ((NModalContainer.Instance?.OpenModal as Node)?.FindChild(LaunchGuard.LinkName, true, false) as LinkButton)?.Text; // the press frees it
        PressLink();
        await Wait(menu, 0.3);
        Check("beside the restore, the turn-off is the button and \"Restore and restart\" the link; the link writes the working list (Test Fixes off there), "
            + "a mod turned off after a failed start kept off (Test UI, on there), then relaunches",
            pressed.IsCompleted && words.Contains("Or restore the setup of the last working launch") && link == "Restore and restart" && relaunches == 2
            && AllOff("SmmTestFixes") && AllOff("SmmTestUi"), $"{words}; link {link}; relaunches {relaunches}");
        ListBack();

        // The rules' own whys in the record: a failed rebuild's patching mod (RebuildLast, the failure's rebuild flag), a
        // probe group (both, one time), a library with the mods that need it (Needs, naming it), a twin (every copy's switch
        // off, copies_off untouched)
        async Task<List<TurnedOff.Entry>> Pressed(Culprits.Culprit culprit)
        {
            await Show(Crash(culprit));
            PressOurs("YesButton");
            await Wait(menu, 0.3);
            var entries = TurnedOff.Entries.ToList();
            ListBack();
            return entries;
        }
        var rebuilt = await Pressed(Blamed(Culprits.Why.RebuildLast, "SmmTestFixes") with { Rebuild = true });
        var probed = await Pressed(Blamed(Culprits.Why.Stale, "SmmTestUi", "SmmTestContent"));
        var dependents = await Pressed(Blamed(Culprits.Why.Code, "SmmTestLib") with { Needing = ["SmmTestContent", "SmmTestUi"] });
        var copiesOff = string.Join(",", Store.Data.CopiesOff ?? []);
        await Show(Crash(Blamed(Culprits.Why.Code, "SmmTestTwin")));
        PressOurs("YesButton");
        await Wait(menu, 0.3);
        var twinOff = (AllOff("SmmTestTwin"), string.Join(",", Store.Data.CopiesOff ?? []) == copiesOff);
        ListBack();
        Check("pressed, each culprit's record: a failed rebuild's patching mod (RebuildLast, a rebuild); a probe group, both with one time (Stale, not a "
            + "rebuild); a library and the two mods that need it (Needs, naming it); a twin, every copy's switch off and copies_off untouched",
            rebuilt is [{ Id: "SmmTestFixes", Why: Culprits.Why.RebuildLast, Rebuild: true }]
            && probed.Count == 2 && probed.All(e => e is { Why: Culprits.Why.Stale, Rebuild: false }) && probed.Select(e => e.At).Distinct().Count() == 1
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
        var plainButtons = (Ok: plainDialog?.OkButtonText, Cancel: plainDialog?.CancelButtonText); // read before the answer frees it
        var plainSays = plainDialog == null ? "no dialog" : $"{plainDialog.DialogText} | {plainButtons.Ok} | {plainButtons.Cancel} | "
            + string.Join(", ", plainDialog.FindChildren("*", "Button", true, false).OfType<Button>().Select(b => b.Text));
        plainDialog?.EmitSignal(AcceptDialog.SignalName.Canceled);
        await Wait(menu, 0.3);
        LaunchGuard.TestNoLanguage = false;
        Check("the plain dialog: the turn-off as OK, Quit as Cancel, Restore as a third button, Mod Doctor's words under \"From Mod Doctor:\"; Quit quits",
            asked.IsCompleted && quits == 1 && plainButtons == ("Turn off Test Fixes and restart", "Quit")
            && plainSays.Contains("Restore and restart") && plainSays.Contains("From Mod Doctor:\n" + doctorWords), plainSays);

        // At its longest: a library and the mods that need it, three already off, a restore with five changes (one listed
        // beside the culprit, the rest counted), and Mod Doctor's words, the streak's words giving way to the restore: the
        // popup's text still fits, at a size that can be read, both boxes drawn. Then the densest with no restore: the same,
        // the streak's words with their link, Turn off all mods.
        void AlreadyOff() => TurnedOff.Add([OffEntry("SmmTestMadeUpA", at: DateTimeOffset.Now.AddMinutes(-20)),
            OffEntry("SmmTestMadeUpB", at: DateTimeOffset.Now.AddMinutes(-15)), OffEntry("SmmTestMadeUpC", at: DateTimeOffset.Now.AddMinutes(-10))]);
        var densest = Blamed(Culprits.Why.Code, "SmmTestLib") with { Needing = ["SmmTestContent", "SmmTestUi"], Streak = 3, DoctorAgrees = true, DoctorText = doctorWords };
        async Task<(string Words, string? Boxes, (bool Fits, int Size, int Button) Fit, string Tall, string? Link, bool Answered)> Densest(LaunchGuard.Offer? offer, string shot)
        {
            AlreadyOff();
            var (shown, said) = await Show(Crash(densest, offer));
            await Capture(menu, outDir, shot);
            var popup = NModalContainer.Instance?.OpenModal as Node;
            var text = popup?.GetNodeOrNull<RichTextLabel>("VerticalPopup/Description");
            // Read before the press frees them
            var (boxes, fit, tall, link) = (text == null ? "no text" : BoxesWrong(text, 2), PopupFit(), text == null ? "" : $"{text.GetContentHeight()} tall in {text.Size}",
                (popup?.FindChild(LaunchGuard.LinkName, true, false) as LinkButton)?.Text);
            PressOurs("NoButton");
            await Wait(menu, 0.3);
            FreshRecord();
            return (said, boxes, fit, tall, link, shown.IsCompleted);
        }
        var five = Offer([.. Enumerable.Range(1, 5).Select(i => (LaunchGuard.Kind.New, $"SmmTestMadeUp{i}"))]);
        var longest = await Densest(five, "1j-crash-culprit-longest");
        Check($"the crash culprit's dialog at its longest (a library and its dependents, three already off, a restore with five changes, Mod Doctor's "
            + $"words; the streak's words give way to the restore): the text fits the popup at a size that can be read ({ReadableText} or more), both boxes drawn",
            longest is { Boxes: null, Fit: { Fits: true, Size: >= ReadableText }, Link: "Restore and restart", Answered: true }
            && longest.Words.Contains("Turned off after the last failed start") && longest.Words.Contains("From Mod Doctor") && longest.Words.Contains("SmmTestMadeUp1 (new)")
            && !longest.Words.Contains("SmmTestMadeUp2 (new)") && longest.Words.Contains("4 more") && !longest.Words.Contains("failed to start 3 times"),
            $"{longest.Boxes}; {longest.Fit}, {longest.Tall}; {longest.Words}");
        var streaked = await Densest(null, "1l-crash-culprit-streak");
        Check($"the crash culprit's dialog at its densest with no restore (a library and its dependents, three already off, Mod Doctor's words, the streak "
            + $"with its link, Turn off all mods): the text fits the popup at a size that can be read ({ReadableText} or more), both boxes drawn",
            streaked is { Boxes: null, Fit: { Fits: true, Size: >= ReadableText }, Link: "Turn off all mods and restart", Answered: true }
            && streaked.Words.Contains("failed to start 3 times") && streaked.Words.Contains("Turned off after the last failed start") && streaked.Words.Contains("From Mod Doctor"),
            $"{streaked.Boxes}; {streaked.Fit}, {streaked.Tall}; {streaked.Words}");
        Results.Add($"INFO the crash culprit's dialog at its longest: its text at {longest.Fit.Size}; at its densest with no restore, at {streaked.Fit.Size}");

        // A name too long for the button: cut there, so its words wrap and stay readable; the whole name in the text
        const string LongId = "SmmTestAModWhoseNameRunsOnAndOnPastTheButton";
        (asked, words) = await Show(Crash(Blamed(Culprits.Why.Code, LongId)));
        var cut = ((NModalContainer.Instance?.OpenModal as Node)?.GetNodeOrNull("VerticalPopup/YesButton")?.FindChild("Label", true, false) as Label)?.Text ?? "";
        var cutFit = PopupFit();
        PressOurs("NoButton");
        await Wait(menu, 0.3);
        Check($"a mod's name too long for the button is cut there, the words wrapped at {LaunchGuard.SmallestButtonText} or more; the whole name in the text",
            asked.IsCompleted && cut.StartsWith("Turn off SmmTestAMod") && cut.EndsWith(Menus.CutMark + " and restart") && cutFit.Button >= LaunchGuard.SmallestButtonText
            && words.Contains("An error while loading " + LongId), $"\"{cut}\" {cutFit}; {words}");
        var quitsBefore = quits;

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
            && unrestoredSaid.Contains("couldn't restore the last working setup") && unrestoredSaid.Contains("answered True") && relaunches == 8 && quits - quitsBefore == 2
            && !AllOff("SmmTestFixes"), $"{unsaved} || {unrestoredSaid}; relaunches {relaunches}, quits {quits - quitsBefore}");
    }

    const int ReadableText = 12; // the size the popup's text keeps to at its longest: smaller can't be read at 1080p

    // How the popup open now holds its words: whether its text fits its box, the size the text settled at, and the right
    // button's
    static (bool Fits, int Size, int Button) PopupFit()
    {
        var popup = NModalContainer.Instance?.OpenModal as Node;
        var text = popup?.GetNodeOrNull<RichTextLabel>("VerticalPopup/Description");
        var yes = popup?.GetNodeOrNull("VerticalPopup/YesButton")?.FindChild("Label", true, false) as Label;
        return (text != null && text.GetContentHeight() <= text.Size.Y + 1, text?.GetThemeFontSize("normal_font_size") ?? 0, yes?.GetThemeFontSize("font_size") ?? 0);
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

        // A mod with no copy installed now (a Workshop one, unsubscribed with Steam up): nothing to turn on, so said, and the
        // entry stays; the other mod in the same notice turned on, its entry gone. With Steam down, said why (its Workshop
        // copies aren't listed then).
        async Task<string> TurnBackOnWords(bool steam, params TurnedOff.Entry[] entries)
        {
            Recorded(entries);
            TurnedOff.TestSteam = steam;
            TurnedOff.ShowNotice(menu);
            await Wait(menu, 0.5);
            PressOurs("YesButton");
            await Wait(menu, 0.5);
            var shown = ShownWords();
            NModalContainer.Instance?.Clear();
            TurnedOff.TestSteam = null;
            return shown;
        }
        var goneWorkshop = OffEntry("SmmTestGoneWorkshop", at: at) with { Switches = [new(ModSource.SteamWorkshop, true, ["w:3700000009"])] };
        Off("SmmTestFixes");
        var noneWords = await TurnBackOnWords(true, OffEntry("SmmTestFixes", at: at), goneWorkshop);
        var noneKept = (TurnedOff.Has("SmmTestGoneWorkshop"), TurnedOff.Has("SmmTestFixes"), On("SmmTestFixes"));
        var offlineWords = await TurnBackOnWords(false, goneWorkshop);
        Check("Turn back on of a mod with no copy installed says there's none to turn on, and its entry stays; the other one is turned on, its entry gone; "
            + "with Steam down it says Workshop mods are listed only while Steam runs, the entry kept",
            noneWords.Contains("Couldn't turn SmmTestGoneWorkshop back on: there's no installed copy to turn on") && noneKept == (true, false, true)
            && offlineWords.Contains("Couldn't turn SmmTestGoneWorkshop back on: Workshop mods are only listed while Steam is running") && TurnedOff.Has("SmmTestGoneWorkshop"),
            $"{noneWords}; kept, Test Fixes kept, on {noneKept}; offline: {offlineWords}");

        // The same mod with its entry in the list, off, as the repair keeps it without Steam: that entry on, nothing said, and
        // its record kept until the copy is listed and on (it may have been unsubscribed meanwhile), as the switch is only of
        // a source this launch didn't scan
        settings.ModList.Add(new SettingsSaveMod { Id = "SmmTestGoneWorkshop", Source = ModSource.SteamWorkshop, IsEnabled = false });
        var listedWords = await TurnBackOnWords(false, goneWorkshop);
        var listedOn = settings.ModList.Any(e => e.Id == "SmmTestGoneWorkshop" && e.IsEnabled);
        settings.ModList.RemoveAll(e => e.Id == "SmmTestGoneWorkshop");
        Check("Turn back on of a mod with no copy installed but its entry in the list (kept off without Steam): that entry on, nothing said, its record kept "
            + "(the switch only of a source this launch didn't scan)",
            listedOn && TurnedOff.Has("SmmTestGoneWorkshop") && !listedWords.Contains("Couldn't turn"), $"{listedWords}; on {listedOn}; entry {TurnedOff.Has("SmmTestGoneWorkshop")}");
        // The next launch, still without Steam: the game's rebuilt list has no entry for that copy (it isn't listed), and
        // KeepOff puts it back on, not off, so it loads once Steam lists it; Restore, profiles and the banner don't keep it
        // off meanwhile (review of round 5: it came back off and the mod stayed off).
        TurnedOff.TestSteam = false;
        List<SettingsSaveMod> nextLaunch = [.. settings.ModList];
        var keptOn = TurnedOff.KeepOff(nextLaunch) && nextLaunch.Any(e => e.Id == "SmmTestGoneWorkshop" && e.Source == ModSource.SteamWorkshop && e.IsEnabled)
            && !nextLaunch.Any(e => e.Id == "SmmTestGoneWorkshop" && !e.IsEnabled);
        var (offForRestore, offOnBanner) = (TurnedOff.OffIds().Contains("SmmTestGoneWorkshop"), TurnedOff.StillOff().Any(e => e.Id == "SmmTestGoneWorkshop"));
        TurnedOff.TestSteam = null;
        Check("... the next launch, still without Steam: its entry, which the game's list dropped, put back on (not off); Restore, profiles and the banner "
            + "leave it out", keptOn && !offForRestore && !offOnBanner && TurnedOff.Has("SmmTestGoneWorkshop"),
            $"put back on {keptOn}; kept off by Restore {offForRestore}; on the banner {offOnBanner}; record {TurnedOff.Has("SmmTestGoneWorkshop")}");

        // Its local copy (on when it ran) deleted and its Workshop one (kept off) not listed without Steam: the list's entries
        // for them (KeepOff's) count for nothing, as the local source was scanned and the Workshop one was off. Said why, the
        // entry kept and nothing turned on; with Steam up, said there's none to turn on.
        var goneLocal = OffEntry("SmmTestGoneWorkshop", at: at) with
        {
            Switches = [new(ModSource.ModsDirectory, true, ["l:SmmTestGoneWorkshop"]), new(ModSource.SteamWorkshop, false, ["w:3700000009"])],
        };
        settings.ModList.AddRange([new SettingsSaveMod { Id = "SmmTestGoneWorkshop", Source = ModSource.ModsDirectory, IsEnabled = false },
            new SettingsSaveMod { Id = "SmmTestGoneWorkshop", Source = ModSource.SteamWorkshop, IsEnabled = false }]);
        var (localOffline, localOnline) = (await TurnBackOnWords(false, goneLocal), await TurnBackOnWords(true, goneLocal));
        var localOn = settings.ModList.Any(e => e.Id == "SmmTestGoneWorkshop" && e.IsEnabled);
        settings.ModList.RemoveAll(e => e.Id == "SmmTestGoneWorkshop");
        Check("Turn back on of a mod whose local copy (on) is gone and whose Workshop one (off) isn't listed without Steam: said why, its entry kept, nothing "
            + "turned on; with Steam up, said there's none to turn on",
            localOffline.Contains("Workshop mods are only listed while Steam is running") && localOnline.Contains("there's no installed copy to turn on")
            && TurnedOff.Has("SmmTestGoneWorkshop") && !localOn, $"offline: {localOffline}; online: {localOnline}; on {localOn}");

        // A popup that can't be made marks nothing shown, so the next main menu tries again
        Recorded(OffEntry("SmmTestFixes", at: at));
        TurnedOff.TestNoPopup = true;
        TurnedOff.ShowNotice(menu);
        await Wait(menu, 0.2);
        TurnedOff.TestNoPopup = false;
        var unshown = (NModalContainer.Instance?.OpenModal == null, TurnedOff.Entries is [{ Shown: false }]);
        Check("a notice whose popup can't be made marks nothing shown, so the next main menu tries again", unshown == (true, true), $"{unshown}");

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

    // Restore and profiles keep a mod turned off after a failed start off: ours, Mod Doctor's (by its rule: while it's
    // loaded and the mod is still off), and Mod Doctor itself when an entry of ours names it; a restore offer whose only
    // change is such a mod off is none; the needs lines say so
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
        // Mod Doctor's record of Test UI: not while Mod Doctor isn't running (its rule and its note don't run then, whatever
        // the sandbox has), nor once Test UI is on again; with it running and Test UI off, kept off
        var top = Path.Combine(TurnOffDir(outDir), "mod-doctor");
        var account = UserDataPathProvider.GetAccountScopedBasePath(null).Replace("user://", "");
        var record = Path.Combine(top, "mod_data", ModDoctor.ModId, account, "turned-off.json");
        Directory.CreateDirectory(Path.GetDirectoryName(record) ?? top);
        File.WriteAllText(record, new JsonObject { ["version"] = 1, ["mods"] = new JsonArray(new JsonObject { ["id"] = "SmmTestUi", ["at"] = DateTimeOffset.Now.ToString("o") }) }.ToJsonString());
        ModDoctor.TestFolder = top;
        void UiOn(bool on)
        {
            foreach (var e in settings.ModList.Where(e => e.Id == "SmmTestUi"))
                e.IsEnabled = on;
        }
        ModDoctor.TestLoaded = false;
        UiOn(false);
        var notRunning = LaunchGuard.Written(working);
        UiOn(true);
        ModDoctor.TestLoaded = true;
        var uiOn = LaunchGuard.Written(working);
        UiOn(false);
        var doctors = LaunchGuard.Written(working);
        // The restore offer with turnedOff as the dialog passes it (none: ours and Mod Doctor's now), Test UI off its only
        // change: none while Mod Doctor runs, offered while it doesn't
        var now = DateTime.Now;
        var uiOff = new LaunchGuard.Setup(now, [new("SmmTestFixes", ModSource.ModsDirectory, true, "0.1.0"), new("SmmTestUi", ModSource.ModsDirectory, false, "1.0.0")], CulpritGame);
        var uiWas = new LaunchGuard.Setup(now.AddHours(-1), [new("SmmTestFixes", ModSource.ModsDirectory, true, "0.1.0"), new("SmmTestUi", ModSource.ModsDirectory, true, "1.0.0")], CulpritGame);
        var offerRunning = LaunchGuard.CrashOffer(uiOff, uiWas);
        // A profile with Test UI on: kept off while Mod Doctor runs, on while it doesn't, and on where the profile turns Mod
        // Doctor off (its note won't run to say why after the relaunch)
        var uiProfile = Profiles.TestCurrent().Select(m => m.Id == "SmmTestUi" ? m with { Enabled = true } : m).ToList();
        Profiles.TestApply(uiProfile);
        var profileRunning = Copies.Ticked(ui);
        UiOn(false);
        Profiles.TestApply([.. uiProfile, new ProfileMod(ModDoctor.ModId, ModSource.ModsDirectory, false)]);
        var profileDoctorOff = Copies.Ticked(ui);
        UiOn(false);
        // Mod Doctor unticked this session, off in the list now: Restore brings it back off, and a profile that doesn't
        // name it leaves it off, so neither keeps its record's mod off (its note won't run to say why). The same offer
        // with it on: none.
        var doctorIn = new LaunchGuard.Entry(ModDoctor.ModId, ModSource.ModsDirectory, true, "1.0.0");
        LaunchGuard.Offer? OfferWithDoctor() => LaunchGuard.CrashOffer(uiOff with { Mods = [.. uiOff.Mods, doctorIn] }, uiWas with { Mods = [.. uiWas.Mods, doctorIn] });
        var offerDoctorOn = OfferWithDoctor();
        settings.ModList.Add(new SettingsSaveMod { Id = ModDoctor.ModId, Source = ModSource.ModsDirectory, IsEnabled = false });
        var (restoredUnticked, offerUnticked) = (OnIn(LaunchGuard.Written(working), "SmmTestUi"), OfferWithDoctor());
        Profiles.TestApply(uiProfile);
        var profileUnticked = Copies.Ticked(ui);
        settings.ModList.RemoveAll(e => e.Id == ModDoctor.ModId);
        UiOn(false);
        ModDoctor.TestLoaded = false;
        var offerNotRunning = LaunchGuard.CrashOffer(uiOff, uiWas);
        Profiles.TestApply(uiProfile);
        var profileNotRunning = Copies.Ticked(ui);
        UiOn(false);
        ModDoctor.TestLoaded = true;
        TurnedOff.Add([OffEntry(ModDoctor.ModId)]);
        var naming = LaunchGuard.Written(working);
        // An entry of ours naming Mod Doctor holds it off, so a profile that doesn't name it doesn't count its record either
        Profiles.TestApply(uiProfile);
        var profileNaming = Copies.Ticked(ui);
        UiOn(false);
        (ModDoctor.TestFolder, ModDoctor.TestLoaded) = (null, null);
        UiOn(true);
        Check("Restore writes a mod turned off after a failed start off, ours (Test Fixes) and Mod Doctor's (Test UI) while Mod Doctor runs and the mod is "
            + "still off, not while it doesn't nor once on again; and Mod Doctor itself off when an entry of ours names it, though Restore keeps it as it is otherwise",
            !OnIn(ours, "SmmTestFixes") && OnIn(ours, "SmmTestUi") && OnIn(ours, ModDoctor.ModId) && OnIn(notRunning, "SmmTestUi")
            && OnIn(uiOn, "SmmTestUi") && !OnIn(doctors, "SmmTestUi") && !OnIn(naming, ModDoctor.ModId),
            $"ours: Fixes {OnIn(ours, "SmmTestFixes")}, UI {OnIn(ours, "SmmTestUi")}; Mod Doctor's: not running {OnIn(notRunning, "SmmTestUi")}, on again "
            + $"{OnIn(uiOn, "SmmTestUi")}, off {OnIn(doctors, "SmmTestUi")}; naming it: {OnIn(naming, ModDoctor.ModId)}");
        Check("Mod Doctor's record keeps Test UI off for the restore offer (its only change: none) and a profile with it on while Mod Doctor runs; while it "
            + "doesn't, offered and turned on; a profile that turns Mod Doctor off turns Test UI on",
            offerRunning == null && !profileRunning && offerNotRunning != null && profileNotRunning && profileDoctorOff,
            $"offer: running {offerRunning != null}, not {offerNotRunning != null}; the profile's Test UI on: running {profileRunning}, not {profileNotRunning}, "
            + $"with Mod Doctor off {profileDoctorOff}");
        Check("Mod Doctor's record doesn't keep Test UI off where Mod Doctor won't run: off in the list now, Restore writes Test UI on, the restore offer is "
            + "made (none with Mod Doctor on) and a profile that doesn't name Mod Doctor turns Test UI on; so does such a profile while an entry of ours names Mod Doctor",
            restoredUnticked && offerUnticked != null && offerDoctorOn == null && profileUnticked && profileNaming,
            $"Restore's Test UI on {restoredUnticked}; offer {offerUnticked != null} (Mod Doctor on: {offerDoctorOn != null}); the profile's Test UI on {profileUnticked}, "
            + $"with our entry for Mod Doctor {profileNaming}");

        // An offer whose only change is a turned-off mod off: none (Restore keeps it off, so it would loop)
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
        // The takeover as the game's startup error calls it, after a first main menu, in a folder of the test's as that
        // launch's own (made to throw once past its undo, so no dialog comes): the last working setup as it was before the
        // launch (none if none), and the menu's marks on the turned-off mods (not the ones from before)
        if (NGame.Instance is not { } game)
        {
            Results.Add("SKIP the takeover's steps: no game");
            return;
        }
        var dir = Path.Combine(TurnOffDir(outDir), "undo");
        Directory.CreateDirectory(dir);
        var was = Launched(CulpritGame) with { At = DateTime.Now.AddHours(-2) };
        void Takeover(LaunchGuard.Setup? before)
        {
            LaunchGuard.PutWorking(dir, before);
            LaunchGuard.Reached(dir, Launched(CulpritGame), null, null); // this launch's menu saves it as the last working one
            LaunchGuard.TestTakeoverThrows = true;
            try
            {
                LaunchGuard.TestTakeover(dir, before, () => LaunchGuard.AskAfterCrash(game));
            }
            catch (InvalidOperationException)
            {
                // the self-test's throw, past the undo
            }
            finally
            {
                LaunchGuard.TestTakeoverThrows = false;
            }
        }
        if (SaveManager.Instance.SettingsSave.ModSettings is { } settings)
            foreach (var e in settings.ModList.Where(e => e.Id is "SmmTestFixes" or "SmmTestUi"))
                e.IsEnabled = false; // still off at the menu, so it keeps their entries
        TurnedOff.Add([OffEntry("SmmTestFixes"), OffEntry("SmmTestUi", at: DateTimeOffset.Now.AddMinutes(-3), menu: true)]);
        TurnedOff.MenuReached();
        var marked = TurnedOff.Entries.All(e => e.MenuSince);
        Takeover(was);
        var back = LaunchGuard.Read(Path.Combine(dir, LaunchGuard.WorkingFile))?.At == was.At;
        var undone = string.Join(", ", TurnedOff.Entries.Select(e => $"{e.Id} {e.MenuSince}"));
        Takeover(null);
        var none = !File.Exists(Path.Combine(dir, LaunchGuard.WorkingFile));
        Check("a start that fails after its first main menu: the takeover takes back what that menu recorded, the last working setup as it was (none if there "
            + "was none), and the menu's marks on the turned-off mods, not the ones from before", back && none && marked && undone == "SmmTestFixes False, SmmTestUi True",
            $"back {back}, none {none}, marked {marked}, undone {undone}");

        // The game's own line as ours takes over, once (the game's method, which writes it, is skipped)
        if (Compat.LogCallback != null)
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
            Results.Add("SKIP the game's own line as ours takes over: no log callback");

        // The takeover in its try: ours throwing before it returns its task lets the game's own dialog through, logged.
        // Called as Harmony calls it (its undo in the test's folder); with the throw taken out it would start our dialog
        // for real.
        if (StartupErrorPatch.GaveWay)
            Results.Add("SKIP the takeover's try: the game's own dialog was given way to already this session");
        else if (AccessTools.Method(typeof(StartupErrorPatch), "Prefix") is { } prefix && Compat.LogCallback != null)
        {
            var errors = 0;
            Action<LogLevel, string, int> count = (level, text, _) => errors += level == LogLevel.Error && text.Contains("Could not offer anything after the failed start") ? 1 : 0;
            Compat.LogCallback.AddEventHandler(null, count);
            LaunchGuard.TestTakeoverThrows = true;
            object?[] args = [game, null];
            var through = false;
            LaunchGuard.TestTakeover(dir, was, () => through = prefix.Invoke(null, args) is true);
            LaunchGuard.TestTakeoverThrows = false;
            Compat.LogCallback.RemoveEventHandler(null, count);
            Check("the takeover in its try: ours throwing before it returns its task lets the game's own dialog through, and says so in the log",
                through && errors == 1 && NModalContainer.Instance?.OpenModal == null, $"through {through}, logged {errors}");
        }
        else
            Results.Add("SKIP the takeover's try: no prefix, or no log callback");

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
            // Its Show: "#failed #disabled" in the box, which finds the mod (#failed has the enabled mods that failed to
            // load too; #warning finds it, the banner being yellow); pressed again, the box empty
            var searchBox = border.GetNode<LineEdit>("SpireModManagerSearch");
            BannerPress("FilterTurnedOffButton");
            await Wait(menu, 0.3);
            var (showBox, tagged, warned) = (searchBox.Text, HiddenTags.Find("failed")?.Invoke(fixes), HiddenTags.Find("warning")?.Invoke(fixes));
            BannerPress("FilterTurnedOffButton");
            await Wait(menu, 0.3);
            Check("the turned-off banner's Show: \"#failed #disabled\" in the box, #failed finding the mod, as #warning does; pressed again, the box empty",
                showBox == "#failed #disabled" && tagged == true && warned == true && searchBox.Text == "", $"\"{showBox}\" {tagged} {warned}; then \"{searchBox.Text}\"");
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
            // Its Turn back on of a mod with no copy installed and no entry in the list (Steam up): said, the entry and the
            // banner kept
            TurnedOff.Add([OffEntry("SmmTestGoneWorkshop", at: DateTimeOffset.Now.AddMinutes(-30)) with { Switches = [new(ModSource.SteamWorkshop, true, ["w:3700000009"])] }]);
            CategoryList.Redraw(content);
            await Wait(menu, 0.3);
            TurnedOff.TestSteam = true;
            BannerPress("TurnBackOnButton");
            await Wait(menu, 0.5);
            TurnedOff.TestSteam = null;
            var noneWords = ShownWords();
            NModalContainer.Instance?.Clear();
            Check("the turned-off banner's Turn back on of a mod with no copy installed says there's none to turn on, and its entry and the banner stay",
                noneWords.Contains("Couldn't turn SmmTestGoneWorkshop back on: there's no installed copy to turn on") && TurnedOff.Has("SmmTestGoneWorkshop") && Banner() != null,
                $"{noneWords}; entry {TurnedOff.Has("SmmTestGoneWorkshop")}, banner {Banner() != null}");
        }
        finally
        {
            (TurnedOff.TestFile, TurnedOff.TestSteam) = (null, null);
            if (!Copies.Ticked(fixes))
                Dependencies.Set([fixes], true, screen);
            CategoryList.Redraw(content);
        }
    }
}
