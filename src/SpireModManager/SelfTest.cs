using System.Text;
using System.Text.Json.Nodes;
using Godot;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Localization.Fonts;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;
using MegaCrit.Sts2.Core.Saves;

namespace SpireModManager;

// In-game smoke test, run by `dotnet run tools/dev.cs test`. Does nothing unless the environment variable
// SPIRE_MOD_MANAGER_SELFTEST names an output folder. Then, from the main menu: screenshots, opens the
// Modding screen, checks our nodes, switches profile and back (the mod list must round-trip), toggles
// the view, writes results.txt, and quits. The runner uses the no-Steam dev sandbox, never real saves.
// SPIRE_MOD_MANAGER_SELFTEST_MODE picks a shorter suite instead: "real" (RunReal), "first-run" or
// "first-run-bmm" (RunFirstRun).
static class SelfTest
{
    const string EnvVar = "SPIRE_MOD_MANAGER_SELFTEST";
    static bool _started;
    static readonly List<string> Results = [];

    public static void MaybeStart(NMainMenu menu)
    {
        if (_started || System.Environment.GetEnvironmentVariable(EnvVar) is not { Length: > 0 } outDir)
            return;
        _started = true;
        Directory.CreateDirectory(outDir);
        _ = Run(menu, outDir);
    }

    static async Task Run(NMainMenu menu, string outDir)
    {
        var mode = System.Environment.GetEnvironmentVariable("SPIRE_MOD_MANAGER_SELFTEST_MODE");
        if (mode == "real")
        {
            await RunReal(menu, outDir);
            return;
        }
        if (mode is "first-run" or "first-run-bmm")
        {
            await RunFirstRun(menu, outDir, imported: mode == "first-run-bmm");
            return;
        }
        var tree = menu.GetTree(); // to quit by: TestLanguageFonts rebuilds the main menu, this one with it
        try
        {
            await Wait(menu, 3);
            TestText(); // our pack's text, whichever page shows
            TestFailSoft();
            if (!ModEntry.OurPage)
            {
                await TestGamePage(menu, outDir); // nothing more of ours to test
                return;
            }
            Check("no popup open", NModalContainer.Instance?.OpenModal == null,
                NModalContainer.Instance?.OpenModal?.GetType().Name ?? "");
            Check("main menu button", menu.GetNodeOrNull("SpireModManagerMenuButton") != null);
            var relaunchArgs = ModEntry.RelaunchArgs();
            Check("relaunch passes the window mode once", relaunchArgs.Count(a => a is "--fullscreen" or "--maximized" or "--windowed") == 1
                && relaunchArgs.Contains("--force-steam"), string.Join(" ", relaunchArgs));
            Check("dev run marked", menu.GetTree().Root.Title.StartsWith("[DEV]") && menu.GetTree().Root.HasNode("SpireModManagerDevTag"),
                menu.GetTree().Root.Title);
            await Capture(menu, outDir, "1-main-menu");
            await TestMenuButtonMove(menu, outDir);
            TestBetterModMenuImport(outDir);
            await TestMenuButtonRelaunch(menu, outDir); // before the Modding page opens: the check's fixes without it

            var screen = menu.SubmenuStack.PushSubmenuType<NModdingScreen>();
            await Wait(menu, 2);
            var content = screen.GetNode<Control>("%ModsScrollContainer/Mask/Content");
            var rows = content.GetChildren().OfType<NModMenuRow>().ToList();
            Check("relaunch button", screen.GetNodeOrNull("SpireModManagerRelaunchButton") != null);
            TestNewModsAtLaunch(); // first: before anything switches profiles
            if (Bmm.Present) // dev.cs test --bmm
                await TestBetterModMenu(menu, screen, content, outDir);
            Check("view toggle", screen.GetNodeOrNull("SpireModManagerViewToggle") != null);
            Check("profile bar", screen.GetNodeOrNull("ModsBorder/SpireModManagerProfileBar") != null);
            Check("our views use the wide layout", screen.GetNode<Control>("ModsBorder").OffsetLeft < 255 && screen.GetNode<Control>("%ModInfoContainer").OffsetRight > 1727);
            Check($"rows ({rows.Count}) match mods ({ModManager.Mods.Count})", rows.Count == ModManager.Mods.Count);
            Results.Add($"INFO headers: {content.GetChildren().Count(c => c.IsInGroup("SpireModManagerCategoryHeader"))}, " +
                $"profiles: {string.Join(", ", Store.Data.Profiles.Select(p => $"{p.Name} ({p.Mods.Count(m => m.Enabled)} on)"))}, " +
                $"active: {Store.Data.ActiveProfile}");
            await Capture(screen, outDir, "2-modding-screen");

            // Profile round trip: switch away and back; the game's mod list must end up as it started.
            if (Profiles.Active is { } start && Store.Data.Profiles.FirstOrDefault(p => p != start) is { } other)
            {
                if (Profiles.Edited)
                    Profiles.Save(); // start clean
                var before = ModListState();
                Profiles.SwitchTo(other, screen);
                await Wait(menu, 1);
                Results.Add($"INFO switched to {other.Name}: {ModListState().Count(s => s.EndsWith("=on"))} on");
                Check("switching to the other profile changes the mod list", !ModListState().SequenceEqual(before)); // or the round trip proves nothing
                Check("our own mod stays on", !(SaveManager.Instance.SettingsSave.ModSettings?.ModList
                    .Any(m => m.Id == ModEntry.ModId && !m.IsEnabled) ?? false));
                await Capture(screen, outDir, "3-other-profile");
                Profiles.SwitchTo(start, screen);
                await Wait(menu, 1);
                var after = ModListState();
                Check("profile round trip restores the mod list", before.SequenceEqual(after),
                    string.Join(", ", before.Except(after)));
            }
            else
                Results.Add("SKIP profile round trip: only one profile");
            await TestNewModsOnSwitch(menu, screen, content);

            Click(screen.GetNode<NButton>("SpireModManagerViewToggle"));
            await Wait(menu, 1);
            Check("vanilla view hides headers", !content.GetChildren().Any(c => c.IsInGroup("SpireModManagerCategoryHeader")));
            var border = screen.GetNode<Control>("ModsBorder");
            Check("vanilla view: the game's row spacing and sides", content.GetThemeConstant("separation") == 16 && content.OffsetLeft == 0 && content.OffsetRight == 0,
                $"separation {content.GetThemeConstant("separation")}, sides {content.OffsetLeft}..{content.OffsetRight}");
            if (!Bmm.Present) // its page lays the list out its own way (TestBetterModMenu)
                Check("vanilla view restores the game's layout", border.OffsetLeft == 255 && border.OffsetRight == 994
                    && border.GetNode<Control>("ModsScrollContainer").OffsetTop == 8
                    && !border.GetNode<Control>("SpireModManagerProfileBar").Visible, $"{border.OffsetLeft}..{border.OffsetRight}");
            await Capture(screen, outDir, "4-vanilla-view");
            Click(screen.GetNode<NButton>("SpireModManagerViewToggle"));
            await Wait(menu, 1);

            await TestCategories(menu, screen, content, outDir);
            await TestListFixes(menu, screen, content);
            await TestProfileEditing(menu, screen, outDir);
            await TestUnsavedProfile(menu, screen, content, outDir);
            await TestProfileShare(menu, screen, outDir);
            await TestNotes(menu, screen, content, outDir);
            await TestPendingNotes(menu, screen, content);
            await TestPendingLine(menu, screen, content);
            await TestSearch(menu, screen, content, outDir);
            await TestRedrawCost(menu, screen, content);
            TestManyMods();
            await TestNewMod(menu, screen, content, outDir);
            await TestLoadOrder(menu, screen, content, outDir);
            await TestListBanner(menu, screen, content, outDir);
            await TestManagersBanner(menu, screen, content, outDir);
            await TestPageBar(menu, screen, content, outDir);
            await TestListFilters(menu, screen, content, outDir);
            await TestRelaunchCheck(menu, screen, content, outDir);
            await TestLongDetails(menu, screen, content, outDir);
            await TestWorkshopHead(menu, screen);
            await TestOtherModPanel(menu, screen, content, outDir);
            await TestBulkEdit(menu, screen, content, outDir);
            await TestTagSuggestions(menu, screen, content, outDir);
            await TestHistory(menu, screen, content, outDir);
            TestFallbacks(screen);

            // Leaving from Load Order: the page comes back on the main list.
            Click(screen.GetNode<NButton>("SpireModManagerLoadOrderToggle"));
            await Wait(menu, 0.3);
            screen.Visible = false;
            screen.Visible = true;
            await Wait(menu, 0.3);
            Check("reopening after Load Order shows the main list", Layout.Current == Layout.View.Categories
                && screen.GetNode<Control>("%ModInfoContainer").Visible);
            await TestListFilterReopen(menu, screen, content);

            await TestStoreSafety(menu, screen, outDir);
            await TestLanguageFonts(screen, outDir); // last: the main menu, the page with it, is made again
        }
        catch (Exception e)
        {
            Results.Add($"FAIL exception: {e}");
        }
        finally
        {
            File.WriteAllLines(Path.Combine(outDir, "results.txt"), Results);
            tree.Quit();
        }
    }

    // --real: Jordan's own mods (all off) and data. No edits; a tour of the views for screenshots and
    // timings, since this is the list size and naming the screen has to cope with.
    static async Task RunReal(NMainMenu menu, string outDir)
    {
        try
        {
            await Wait(menu, 3);
            CheckPatches();
            var screen = menu.SubmenuStack.PushSubmenuType<NModdingScreen>();
            await Wait(menu, 2);
            var content = screen.GetNode<Control>("%ModsScrollContainer/Mask/Content");
            var rows = content.GetChildren().OfType<NModMenuRow>().ToList();
            Check($"rows ({rows.Count}) match mods ({ModManager.Mods.Count})", rows.Count == ModManager.Mods.Count);
            Results.Add($"INFO categories: {Store.Data.Categories.Count}, profiles: {Store.Data.Profiles.Count}, " +
                $"mods with notes: {Store.Data.Mods.Values.Count(m => m.Notes != null)}");

            // A full redraw happens on every search keystroke and every edit: it has to stay quick.
            var timer = System.Diagnostics.Stopwatch.StartNew();
            for (var i = 0; i < 10; i++)
                CategoryList.Apply(content);
            var categoriesMs = timer.Elapsed.TotalMilliseconds / 10;
            Check($"category view redraw {categoriesMs:0.0} ms", categoriesMs < 50);
            double Ms(Action action)
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                for (var i = 0; i < 10; i++)
                    action();
                return watch.Elapsed.TotalMilliseconds / 10;
            }
            Results.Add($"INFO of which: titles {Ms(() => RowTitles.Apply(rows, false)):0.0} ms, toolbar {Ms(() => LoadOrder.UpdateToolbar(content, rows, () => { })):0.0} ms, "
                + $"strips and toolbar {Ms(() => CategoryList.RefreshStrips(content)):0.0} ms");
            // The page bar's Show: choices (Changed compares our data and the mod list with launch, once a redraw).
            var shows = new[] { ListFilter.Choice.Enabled, ListFilter.Choice.Disabled, ListFilter.Choice.Changed }.Select(choice =>
            {
                ListFilter.Choose(content, choice);
                return (choice, ms: Ms(() => CategoryList.Apply(content)));
            }).ToList();
            ListFilter.Choose(content, ListFilter.Choice.All);
            Check($"a redraw showing {string.Join(", ", shows.Select(s => $"{s.choice} {s.ms:0.0} ms"))}", shows.All(s => s.ms < 50));
            // A search keystroke: a redraw matching every row, then one with the whole list back.
            var typed = screen.GetNode<LineEdit>("ModsBorder/SpireModManagerSearch");
            void Type(string text)
            {
                typed.Text = text;
                typed.EmitSignal(LineEdit.SignalName.TextChanged, text);
            }
            Results.Add($"INFO a search keystroke {Ms(() => { Type("lib"); Type(""); }) / 2:0.0} ms");
            // The same to the end of the frame (FrameMs), with what they leave for it: titles fitted, layout.
            await Wait(menu, 0.3);
            var redrawFrame = await FrameMs(menu, () => CategoryList.Apply(content));
            var keyFrame = (await FrameMs(menu, () => Type("lib")) + await FrameMs(menu, () => Type(""))) / 2;
            Results.Add($"INFO to the end of the frame: a redraw {redrawFrame:0.0} ms, a search keystroke {keyFrame:0.0} ms");
            await Wait(menu, 1);
            await TimeSwitches(menu, screen, content);
            await Capture(screen, outDir, "real-1-categories");

            Click(screen.GetNode<NButton>("SpireModManagerLoadOrderToggle"));
            await Wait(menu, 1);
            timer.Restart();
            for (var i = 0; i < 10; i++)
                CategoryList.Apply(content);
            var orderMs = timer.Elapsed.TotalMilliseconds / 10;
            Check($"load order redraw {orderMs:0.0} ms", orderMs < 50);

            await Capture(screen, outDir, "real-2-load-order");

            var search = screen.GetNode<LineEdit>("ModsBorder/SpireModManagerSearch");
            search.Text = "lib";
            search.EmitSignal(LineEdit.SignalName.TextChanged, search.Text);
            await Wait(menu, 0.5);
            await Capture(screen, outDir, "real-3-search");
            search.Text = "";
            search.EmitSignal(LineEdit.SignalName.TextChanged, "");

            Click(screen.GetNode<NButton>("SpireModManagerLoadOrderToggle"));
            Click(screen.GetNode<NButton>("SpireModManagerViewToggle"));
            await Wait(menu, 1);
            await Capture(screen, outDir, "real-4-vanilla");
            Click(screen.GetNode<NButton>("SpireModManagerViewToggle"));
            await Wait(menu, 0.5);

            // Scrolled to the bottom, a search for one mod: it shows, in view. (The game's container stops scrolling
            // once the rows fit; the rows used to stay above the view, an empty list under "Search matches".)
            var scroller = content.GetParent().GetParent<Control>();
            scroller.Set("_targetDragPosY", -100000f);
            await Wait(menu, 1.5);
            var scrolledTo = content.Position.Y;
            var last = rows.Where(r => r.Mod?.manifest?.id != null).Last();
            search.Text = last.Mod?.manifest?.id ?? "";
            search.EmitSignal(LineEdit.SignalName.TextChanged, search.Text);
            await Wait(menu, 0.5);
            var view = scroller.GetNode<Control>("Mask").GetGlobalRect();
            var shown = rows.Where(r => r.Visible).ToList();
            Check("scrolled down, a narrow search shows its match in view", scrolledTo < -200 && shown.Count > 0 && shown.All(r => view.Encloses(r.GetGlobalRect())),
                $"scrolled to {scrolledTo}, {shown.Count} shown, first at {shown.FirstOrDefault()?.GetGlobalRect()}, view {view}");
            await Capture(screen, outDir, "real-5-search-scrolled");
            search.Text = "";
            search.EmitSignal(LineEdit.SignalName.TextChanged, "");
            await Wait(menu, 0.3);

            // Save as new with real clicks (this is a copy of the player's data): first an edit (a tickbox),
            // then the button.
            var tick = content.GetChildren().OfType<NModMenuRow>().First(r => r.Mod?.manifest?.id != ModEntry.ModId).GetNode<Control>("Tickbox");
            var tickUnder = await RealClick(menu, tick);
            Check("a real tickbox click makes an unsaved edit", Profiles.Edited, tickUnder);
            var profiles = Store.Data.Profiles.Count;
            var bar = screen.GetNode("ModsBorder/SpireModManagerProfileBar");
            var activeName = Profiles.Active?.Name ?? "";
            var under = await RealClick(menu, (Control)bar.FindChild("SaveAsNewButton", true, false));
            Check("Save as new (a real click) opens the name box with \"(1)\"", bar.FindChild("ProfileName", true, false) is LineEdit { Visible: true } box
                && box.HasFocus() && box.Text == Profiles.CopyName(activeName), under);
            await Capture(screen, outDir, "real-5-save-as-new");
            await RealClick(menu, (Control)bar.FindChild("SaveNameButton", true, false));
            Check("its Save (a real click) adds the profile", Store.Data.Profiles.Count == profiles + 1 && !Profiles.Edited);
        }
        catch (Exception e)
        {
            Results.Add($"FAIL exception: {e}");
        }
        finally
        {
            File.WriteAllLines(Path.Combine(outDir, "results.txt"), Results);
            menu.GetTree().Quit();
        }
    }

    // Switching views with a mod picked, as a player does: main list to Load Order and back, to Vanilla List and
    // back. Each switch is timed to the end of its frame (FrameMs), three times; the middle time is reported.
    static async Task TimeSwitches(Node menu, NModdingScreen screen, Control content)
    {
        if (content.GetChildren().OfType<NModMenuRow>().FirstOrDefault() is { } row)
            CategoryList.Select(screen, row);
        await Wait(menu, 0.5);
        var idle = new List<double>();
        var times = new Dictionary<string, List<double>>();
        async Task Switch(string name, string toggle)
        {
            if (!times.TryGetValue(name, out var list))
                times[name] = list = [];
            list.Add(await FrameMs(menu, () => Click(screen.GetNode<NButton>(toggle))));
            await Wait(menu, 0.3);
        }
        for (var round = 0; round < 3; round++)
        {
            idle.Add(await FrameMs(menu, () => { }));
            await Switch("to Load Order", "SpireModManagerLoadOrderToggle");
            await Switch("back from Load Order", "SpireModManagerLoadOrderToggle");
            await Switch("to Vanilla List", "SpireModManagerViewToggle");
            await Switch("back from Vanilla List", "SpireModManagerViewToggle");
        }
        static double Middle(List<double> ms) => ms.Order().ElementAt(ms.Count / 2);
        Results.Add($"INFO view switches (to the end of the frame; an idle frame {Middle(idle):0.0} ms): "
            + string.Join(", ", times.Select(t => $"{t.Key} {Middle(t.Value):0.0} ms")));
    }

    // How long something takes through to the end of the frame it happens in: its own work, then what it leaves
    // for the frame's end (layout, text, nodes freed). From the start of a frame, where a click lands, to a timer
    // that fires once that frame's deferred work is done; the time between frames isn't counted.
    static async Task<double> FrameMs(Node node, Action action)
    {
        var tree = node.GetTree();
        await node.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        action();
        await node.ToSignal(tree.CreateTimer(0), SceneTreeTimer.SignalName.Timeout);
        return watch.Elapsed.TotalMilliseconds;
    }

    // Our text reaches the game: each eng text without placeholders, as packed, reads back unchanged through
    // the game's own tables. tools/check.cs covers the other half without the game: every key the code uses
    // is in eng.
    static void TestText()
    {
        var json = Godot.FileAccess.GetFileAsString($"res://{ModEntry.ModId}/localization/eng/settings_ui.json");
        var texts = (json.Length > 0 ? JsonNode.Parse(json) as JsonObject : null)?
            .Select(p => (p.Key, Text: (string?)p.Value ?? ""))
            .Where(p => !p.Text.Contains('{')).ToList() ?? [];
        var wrong = texts.Where(p => Read(p.Key) != p.Text).Select(p => p.Key).ToList();
        Check($"our texts read back from the game's tables ({texts.Count})", texts.Count > 0 && wrong.Count == 0,
            $"{wrong.Count} differ: {string.Join(", ", wrong.Take(5))}");

        static string Read(string key)
        {
            try
            {
                return Menus.Text(key);
            }
            catch (Exception e)
            {
                return e.Message; // a key the game can't find may throw rather than show
            }
        }
    }

    // A game update that renames what a patch hooks. On this game version: every patch applied, and the row state
    // the game keeps to itself is found. Which failures keep the game's Modding page (with the needs-update line)
    // instead of ours: any one patch our page needs; none of the others.
    static void TestFailSoft()
    {
        CheckPatches();
        Check("row state: the game's selected and focused members found", RowState.Found);
        string[] spare = [nameof(RowLookPatch), nameof(ShortcutsPatch), nameof(NewModPatch), nameof(ModInfoClearPatch),
            nameof(NewModPopupPatch), nameof(NewModTogglePatch), nameof(TickKeepPatch)];
        Check("our page with no patch failed, or only ones it can do without", ModEntry.PageWorks([]) && ModEntry.PageWorks(spare));
        Check("the game's page if any one patch our page needs failed", ModEntry.PageNeeds.Contains(nameof(ModdingScreenPatch))
            && ModEntry.PageNeeds.All(needed => !ModEntry.PageWorks([needed]) && !ModEntry.PageWorks([.. spare, needed])),
            string.Join(", ", ModEntry.PageNeeds.Where(needed => ModEntry.PageWorks([needed]))));
        // The line itself, on a stand-in for the screen.
        var stand = new Control();
        var line = ModEntry.ShowNeedsUpdate(stand);
        Check("the needs-update line: its text, once, above the game's list and info panel",
            line.Text == "Spire Mod Manager needs an update for this game version." && ModEntry.ShowNeedsUpdate(stand) == line
            && stand.GetChildCount() == 1 && line.Position.Y + line.Size.Y <= 83, $"{line.Text} at {line.Position}, {line.Size}");
        stand.Free();
    }

    // dev.cs test with SPIRE_MOD_MANAGER_SKIP_PATCHES naming a patch our page needs (not MainMenuPatch: this test
    // starts from it): the Modding page is the game's, with the needs-update line and none of ours, the info panel
    // included. Without the screen's own patch, the line is the main menu button's note instead.
    static async Task TestGamePage(NMainMenu menu, string outDir)
    {
        Results.Add($"INFO left out: {string.Join(", ", ModEntry.FailedPatches)}");
        const string text = "Spire Mod Manager needs an update for this game version.";
        if (ModEntry.FailedPatches.Contains(nameof(ModdingScreenPatch)))
        {
            var note = menu.GetNodeOrNull<Label>("SpireModManagerMenuButton/PendingNote");
            Check("no Modding screen patch: the main menu button's note says the mod needs an update", note is { Visible: true, Text: text }, note?.Text ?? "no note");
            await Capture(menu, outDir, "1-main-menu-needs-update");
            return;
        }
        var screen = menu.SubmenuStack.PushSubmenuType<NModdingScreen>();
        await Wait(menu, 2);
        Check("the game's page, with the needs-update line", screen.GetNodeOrNull<Label>(ModEntry.NeedsUpdateName) is { Visible: true, Text: text },
            screen.GetNodeOrNull<Label>(ModEntry.NeedsUpdateName)?.Text ?? "no line");
        var border = screen.GetNode<Control>("ModsBorder");
        Check("none of ours on it: no buttons, bars or headers; the game's boxes", screen.GetNodeOrNull("SpireModManagerViewToggle") == null
            && screen.GetNodeOrNull("SpireModManagerRelaunchButton") == null && border.GetNodeOrNull("SpireModManagerProfileBar") == null
            && !screen.GetNode("%ModsScrollContainer/Mask/Content").GetChildren().Any(c => c.IsInGroup("SpireModManagerCategoryHeader"))
            && border.OffsetLeft == 255 && border.OffsetRight == 994, $"{border.OffsetLeft}..{border.OffsetRight}");
        if (screen.GetNode("%ModsScrollContainer/Mask/Content").GetChildren().OfType<NModMenuRow>().FirstOrDefault() is { } row)
        {
            CategoryList.Select(screen, row);
            await Wait(menu, 0.5);
            Check("a mod shown in the panel: the game's details only", screen.GetNode("%ModInfoContainer").GetNodeOrNull(ModDetails.HeadName) == null);
            // Our row look never ran (it saves the game's look the first time it does).
            Check("the selected row keeps the game's highlight", row.GetNodeOrNull<Control>("SelectionHighlight") is { } look
                && !look.HasMeta("spire_mod_manager_game_look"));
        }
        await Capture(screen, outDir, "2-game-page-needs-update");
    }

    // Every patch applied; in a dev run that leaves some out (SPIRE_MOD_MANAGER_SKIP_PATCHES), exactly those failed.
    static void CheckPatches()
    {
        var skipped = ModEntry.SkippedPatches;
        Check(skipped.Count == 0 ? "every patch applied" : $"every patch applied but the ones left out ({string.Join(", ", skipped)})",
            ModEntry.FailedPatches.Order().SequenceEqual(skipped.Order()), string.Join(", ", ModEntry.FailedPatches));
    }

    // --first-run / --first-run-bmm: the game started with no manager.json, and either nothing of Better Mod Menu's
    // or its data to import (tools/fixtures/bmm), with it installed but off, or on (--bmm). The page opens on what a
    // first run makes, and a second load reads our file back instead of starting over.
    static async Task RunFirstRun(NMainMenu menu, string outDir, bool imported)
    {
        try
        {
            await Wait(menu, 3);
            Results.Add($"INFO first run {(imported ? "with Better Mod Menu's data" : "with no data")}, Better Mod Menu {(Bmm.Present ? "on" : "off")}");
            Check("first run: no popup open", NModalContainer.Instance?.OpenModal == null, NModalContainer.Instance?.OpenModal?.GetType().Name ?? "");
            Check("first run: our file is written at once", File.Exists(Store.FilePath), Store.FilePath);

            var screen = menu.SubmenuStack.PushSubmenuType<NModdingScreen>();
            await Wait(menu, 2);
            var content = screen.GetNode<Control>("%ModsScrollContainer/Mask/Content");
            var rows = content.GetChildren().OfType<NModMenuRow>().ToList();
            // Each part comes from its own guarded step (Patches): one missing means that step failed (see the log).
            string[] parts = ["SpireModManagerRelaunchButton", "SpireModManagerViewToggle", "SpireModManagerLoadOrderToggle", HistoryBar.BarName,
                "ModsBorder/SpireModManagerProfileBar", "ModsBorder/SpireModManagerSearch", $"ModsBorder/{PageBar.Name}"];
            var missing = parts.Where(p => screen.GetNodeOrNull(p) == null).ToList();
            Check("first run: the page opens with all our parts", missing.Count == 0, "missing " + string.Join(", ", missing));
            Check($"first run: rows ({rows.Count}) match mods ({ModManager.Mods.Count})", rows.Count == ModManager.Mods.Count);

            if (imported)
                await TestImported(menu, screen, content, outDir);
            else
                await TestNoData(menu, screen, content, outDir);
            TestSecondLoad();
        }
        catch (Exception e)
        {
            Results.Add($"FAIL exception: {e}");
        }
        finally
        {
            File.WriteAllLines(Path.Combine(outDir, "results.txt"), Results);
            menu.GetTree().Quit();
        }
    }

    // No data at all: one profile, the mod list as it is; no categories, and the hint under the page bar saying how
    // to make one. Made that way (a mod's menu, New category), the hint goes; deleting the category brings it back.
    static async Task TestNoData(Node menu, NModdingScreen screen, Control content, string outDir)
    {
        var data = Store.Data;
        Check("no data: nothing imported, no categories, nothing kept per mod", data.ImportedFrom == null && data.Categories.Count == 0 && data.Mods.Count == 0,
            $"imported from {data.ImportedFrom}, {data.Categories.Count} categories, {data.Mods.Count} mods");
        Check("no data: one profile, Default, active and matching the mod list", data.Profiles.Count == 1
            && Profiles.Active?.Name == Menus.Text("SPIRE_MOD_MANAGER.DEFAULT_PROFILE") && !Profiles.Edited, string.Join(", ", data.Profiles.Select(p => p.Name)));
        var headers = content.GetChildren().Where(c => c.IsInGroup("SpireModManagerCategoryHeader")).Select(c => c.Name.ToString()).ToList();
        Check("no data: the list is Uncategorized alone", headers.SequenceEqual(["Uncategorized"]), string.Join(", ", headers));

        Label? Hint() => screen.FindChild(PageBar.HintName, true, false) as Label;
        var bar = screen.FindChild(PageBar.Name, true, false) as Control;
        var scroll = screen.GetNode<Control>("ModsBorder/ModsScrollContainer");
        var text = Menus.Format("SPIRE_MOD_MANAGER.NO_CATEGORIES_HINT", ("item", Menus.Text("SPIRE_MOD_MANAGER.NEW_CATEGORY")));
        Check("no data: the hint, under the page bar and above the list", Hint() is { Visible: true } hint && hint.Text == text
            && bar != null && hint.OffsetTop >= bar.OffsetBottom && scroll.OffsetTop >= hint.OffsetBottom,
            $"\"{Hint()?.Text}\" at {Hint()?.OffsetTop}..{Hint()?.OffsetBottom}, bar to {bar?.OffsetBottom}, list from {scroll.OffsetTop}");
        await Capture(screen, outDir, "first-run-1-no-data");

        // The way the hint says: a mod's right-click menu, New category (named in place; Enter keeps the name).
        var listTop = scroll.OffsetTop;
        var row = content.GetChildren().OfType<NModMenuRow>().FirstOrDefault(r => r.Visible && r.Mod?.manifest?.id != null);
        var modId = row?.Mod?.manifest?.id;
        if (row == null || modId == null || CategoryList.ShowRowMenu(content, row) is not { } rowMenu)
        {
            Results.Add("FAIL no data: no mod's menu to make a category from");
            return;
        }
        await Wait(menu, 0.3);
        MenuPanel.Press(rowMenu, Menus.Text("SPIRE_MOD_MANAGER.NEW_CATEGORY"));
        await Wait(menu, 0.5);
        var made = Store.Data.Categories.FirstOrDefault();
        Check("a category made from a mod's menu: the mod in it, the hint gone, the list moved up",
            made != null && Store.InfoFor(modId)?.Category == made.Name && Header(content, made.Name) != null && Hint() == null && scroll.OffsetTop < listTop,
            $"{Store.Data.Categories.Count} categories, hint {Hint() != null}, list from {listTop} to {scroll.OffsetTop}");
        await Capture(screen, outDir, "first-run-2-category-made");
        if (made == null)
            return;
        if (Header(content, made.Name)?.GetNodeOrNull<LineEdit>("RenameBox") is { } box)
            box.EmitSignal(LineEdit.SignalName.TextSubmitted, box.Text);
        await Wait(menu, 0.3);
        Categories.Delete(made);
        CategoryList.Apply(content);
        await Wait(menu, 0.3);
        Check("deleting the last category brings the hint back", Store.Data.Categories.Count == 0 && Hint() is { Visible: true } && scroll.OffsetTop == listTop,
            $"{Store.Data.Categories.Count} categories, list from {scroll.OffsetTop}");
    }

    // Better Mod Menu's data, as BetterModMenuImport maps it: its groups are our categories (in order, its collapsed
    // one collapsed), a mod's group its category, its aliases and notes ours (empty ones left out), its profiles ours
    // (every installed mod on but the ones it had off), its current one active.
    static async Task TestImported(Node menu, NModdingScreen screen, Control content, string outDir)
    {
        var data = Store.Data;
        Check("imported from Better Mod Menu's account folder", data.ImportedFrom?.Replace('\\', '/').EndsWith("mod_data/BetterModMenu/mod_profiles.json") == true,
            data.ImportedFrom ?? "nothing imported");
        if (data.ImportedFrom != null && !Bmm.Present) // its file is only read; with it on, it saves its own changes there
            Check("its file is left as it was", File.GetLastWriteTime(data.ImportedFrom) < System.Diagnostics.Process.GetCurrentProcess().StartTime);
        var categories = string.Join(", ", data.Categories.Select(c => c.Collapsed ? $"{c.Name} (collapsed)" : c.Name));
        Check("its groups are our categories, in order, the collapsed one collapsed", categories == "Frameworks, Content (collapsed), Menus", categories);
        string Fields(string id) => Store.InfoFor(id) is { } info ? $"{id}: {info.Category ?? "-"} / {info.Alias ?? "-"} / {info.Notes ?? "-"}" : $"{id}: none";
        Check("a mod's group is its category; aliases and notes come along, empty ones left out", data.Mods.Count == 4
            && Store.InfoFor("SmmTestLib") is { Category: "Frameworks", Alias: "Framework", Notes: "Keep on: Test Content and Test UI need it.", Tags: null }
            && Store.InfoFor("SmmTestContent") is { Category: "Content", Alias: "Content pack", Notes: null }
            && Store.InfoFor("SmmTestUi") is { Category: null, Alias: null, Notes: "Turn off for co-op." }
            && Store.InfoFor(Bmm.ModId) is { Category: "Menus", Alias: null, Notes: null },
            string.Join("; ", data.Mods.Keys.Order().Select(Fields)));

        var names = string.Join(", ", data.Profiles.Select(p => p.Name));
        Check("its profiles are ours, its current one active, matching the mod list", names == "Framework only, Everything" && data.ActiveProfile == "Everything"
            && !Profiles.Edited,
            $"{names}; active {data.ActiveProfile}");
        string[] testMods = ["SmmTestLib", "SmmTestContent", "SmmTestUi"];
        bool? On(string profile, string id) => data.Profiles.FirstOrDefault(p => p.Name == profile)?.Mods.FirstOrDefault(m => m.Id == id)?.Enabled;
        Check("in each, every installed mod on but the ones it had off", data.Profiles.All(p => p.Disabled == null)
            && On("Framework only", "SmmTestLib") == true && On("Framework only", "SmmTestContent") == false && On("Framework only", "SmmTestUi") == false
            && On("Framework only", ModEntry.ModId) == true && testMods.All(id => On("Everything", id) == true),
            string.Join("; ", data.Profiles.Select(p => $"{p.Name}: {string.Join(", ", testMods.Select(id => $"{id} {On(p.Name, id)}"))}")));

        // On the page: its groups as headers, its alias on the row, the notepad on a mod with notes; no hint.
        NModMenuRow? Row(string id) => content.GetChildren().OfType<NModMenuRow>().FirstOrDefault(r => r.Mod?.manifest?.id == id);
        var headers = content.GetChildren().Select(c => c.Name.ToString()).Where(n => n.StartsWith("Category ") || n == "Uncategorized").ToList();
        var libTitle = Row("SmmTestLib")?.GetNode<RichTextLabel>("Title").Text ?? "";
        Check("on the page: its groups as headers (the collapsed one closed), its alias on the row, the notepad for notes, no hint",
            headers.SequenceEqual(["Category Frameworks", "Category Content", "Category Menus", "Uncategorized"]) && Row("SmmTestContent") is { Visible: false }
            && libTitle.StartsWith("Framework") && Row("SmmTestUi")?.GetNodeOrNull<Control>("SpireModManagerEditedMark") is { Visible: true }
            && screen.FindChild(PageBar.HintName, true, false) == null,
            $"{string.Join(", ", headers)}; Test Content shown {Row("SmmTestContent")?.Visible}; title {libTitle}");
        var bar = screen.GetNode("ModsBorder/SpireModManagerProfileBar");
        var picker = (Button)bar.FindChild("ProfilePicker", true, false);
        Check("the profile bar shows its current profile", picker.Text == "Everything", picker.Text);
        await Capture(screen, outDir, "first-run-bmm-1-imported");
        await RealClick(menu, picker);
        var list = MenuPanel.Current;
        var labels = list == null ? [] : MenuPanel.Labels(list);
        Check("the profile list has both of its profiles", labels.Contains("Framework only") && labels.Contains("Everything"), string.Join(", ", labels));
        await Capture(screen, outDir, "first-run-bmm-2-profiles");
        await RealClick(menu, picker);
    }

    // A second launch, as far as our data goes: the file the first run wrote is read back. Nothing is imported or
    // made again: an edit since then is still there, and there's no second set of profiles.
    static void TestSecondLoad()
    {
        Store.EditMod("SmmTestUi", info => info.Tags = ["since first run"]);
        var before = Store.EditsJson();
        string Summary() => $"profiles {string.Join(", ", Store.Data.Profiles.Select(p => p.Name))}; {Store.Data.Categories.Count} categories; " +
            $"{Store.Data.Mods.Count} mods; imported from {Store.Data.ImportedFrom ?? "nothing"}";
        var was = Summary();
        Store.Reload();
        Profiles.Prepare(); // as at launch
        Check("a second load reads our file back: nothing imported or added again", Store.EditsJson() == before
            && Store.InfoFor("SmmTestUi")?.Tags?.SequenceEqual(["since first run"]) == true, $"{was} -> {Summary()}");
    }

    // Regressions from Jordan's 2026-10-07 notes: clicks outside the list, list height after collapsing,
    // focus on empty clicks, inline buttons centred in their strip.
    static async Task TestListFixes(Node menu, NModdingScreen screen, Control content)
    {
        Check("clicks above and below the list are blocked", screen.GetNode("ModsBorder/ModsScrollContainer/Mask").HasNode("SpireModManagerBlockTop"));

        var collapsed = Store.Data.Categories.Select(c => c.Collapsed).ToList();
        var uncategorized = Store.Data.UncategorizedCollapsed;
        var open = content.Size.Y;
        foreach (var c in Store.Data.Categories)
            c.Collapsed = true;
        Store.Data.UncategorizedCollapsed = true;
        CategoryList.Apply(content);
        await Wait(menu, 0.3);
        Check("the list keeps its width (a little in from the sides, for the rows' highlight)", Math.Abs(content.Size.X - (content.GetParent<Control>().Size.X + 2 * RowTitles.LookInset.X)) < 1,
            $"{content.Size.X} of {content.GetParent<Control>().Size.X}");
        Check("collapsing everything shrinks the list", content.Size.Y < open && Math.Abs(content.Size.Y - content.GetCombinedMinimumSize().Y) < 1,
            $"{open} -> {content.Size.Y} (min {content.GetCombinedMinimumSize().Y})");
        for (var i = 0; i < collapsed.Count; i++)
            Store.Data.Categories[i].Collapsed = collapsed[i];
        Store.Data.UncategorizedCollapsed = uncategorized;
        CategoryList.Apply(content);
        await Wait(menu, 0.3);

        var search = screen.GetNode<Control>("ModsBorder/SpireModManagerSearch");
        search.GrabFocus();
        screen.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true });
        await Wait(menu, 0.1);
        Check("a click on empty space releases focus", !search.HasFocus());

        if (content.GetChildren().OfType<NModMenuRow>().FirstOrDefault(r => r.Visible) is { } shown
            && shown.GetNodeOrNull<Control>(RowStrip.ActionsName)?.GetChildOrNull<Control>(0) is { } button)
        {
            var tick = shown.GetNode<Control>("Tickbox");
            var tickY = tick.GetGlobalTransform() * (tick.Size / 2);
            Check("row buttons line up with the tickbox", Math.Abs(button.GetGlobalRect().GetCenter().Y - tickY.Y) < 2,
                $"button {button.GetGlobalRect()}, tickbox centre {tickY}");
            var pencil = shown.GetNodeOrNull<Button>("SpireModManagerEdit");
            Check("the pencil: flat, past the tickbox, in line with it", pencil?.GetThemeStylebox("normal") is StyleBoxEmpty
                && pencil.GetGlobalRect().Position.X >= tick.GetGlobalRect().End.X - 2 && Math.Abs(pencil.GetGlobalRect().GetCenter().Y - tickY.Y) < 2,
                $"pencil {pencil?.GetGlobalRect()}, tickbox {tick.GetGlobalRect()}");
        }
    }

    // dev.cs test --bmm: Better Mod Menu loaded too. On our views its parts are hidden and our layout holds (its
    // re-layout paused); Vanilla View is its page; going back and forth doesn't drift; profiles leave it alone.
    static async Task TestBetterModMenu(NMainMenu menu, NModdingScreen screen, Control content, string outDir)
    {
        var ids = ModManager.Mods.Select(m => m.manifest?.id).ToList();
        var border = screen.GetNode<Control>("ModsBorder");
        var scroll = border.GetNode<Control>("ModsScrollContainer");
        var info = screen.GetNode<Control>("%ModInfoContainer");
        var mask = scroll.GetNode<Control>("Mask");
        var chrome = screen.GetNodeOrNull<Control>("BetterModMenuChrome");
        Results.Add($"INFO Better Mod Menu at {ids.IndexOf(Bmm.ModId)}, ours at {ids.IndexOf(ModEntry.ModId)} in the load order; " +
            $"its bars {(chrome == null ? "not built (it failed on this game version)" : "built")}");
        static bool Shown(Node? node) => node is CanvasItem { Visible: true };
        List<Node> RowControls() => content.GetChildren().OfType<NModMenuRow>()
            .Select(r => r.FindChild("RowCustomControls", true, false)).OfType<Node>().ToList();
        bool Groups() => content.GetChildren().Any(c => c.Name.ToString().StartsWith("ModGroup"));
        static string Box(Control c) => $"{c.OffsetLeft},{c.OffsetTop},{c.OffsetRight},{c.OffsetBottom}";

        void OursHold(string when, float left)
        {
            Check($"{when}: the managers banner shows (no step-aside notice, no warning in the bottom row), with our search and toggles",
                Shown(border.GetNodeOrNull(LoadOrder.ManagersName)) && screen.GetNodeOrNull("SpireModManagerBmmNotice") == null
                && screen.FindChild("SpireModManagerBmmWarning", true, false) == null
                && screen.GetNodeOrNull("SpireModManagerViewToggle") != null && screen.GetNodeOrNull("ModsBorder/SpireModManagerSearch") != null);
            Check($"{when}: Better Mod Menu's bars, row controls and panel buttons hidden",
                !Shown(chrome) && !RowControls().Any(Shown) && !Shown(info.GetNodeOrNull("BetterModMenuSelectedModActions")),
                $"bars {Shown(chrome)}, row controls {RowControls().Count(Shown)}");
            Check($"{when}: none of its groups in our list, every row there", !Groups()
                && content.GetChildren().OfType<NModMenuRow>().Count() == ModManager.Mods.Count);
            Check($"{when}: the list where ours goes, not pushed down or clipped", border.OffsetLeft == left && border.OffsetTop == 72
                && !border.HasMeta("better_mod_menu_top_bar_room") && !mask.ClipContents, $"list {Box(border)}, clip {mask.ClipContents}");
            Check($"{when}: the rows below our bars, inside the panel", border.GetGlobalRect().Grow(1).Encloses(scroll.GetGlobalRect())
                && scroll.Size.Y > 500, $"rows {scroll.GetGlobalRect()}, panel {border.GetGlobalRect()}");
            Check($"{when}: row titles ours (no colour of its own)", content.GetChildren().OfType<NModMenuRow>()
                .All(r => r.GetNode<RichTextLabel>("Title") is { BbcodeEnabled: true } t && !t.HasThemeColorOverride("default_color")));
        }

        // Opening on our main list, after its deferred work.
        OursHold("main list", 175);
        var toggles = new[] { "SpireModManagerViewToggle", "SpireModManagerLoadOrderToggle" }.Select(name => screen.GetNode<Control>(name)).ToList();
        Check("the button to its page still says Vanilla List", toggles[0].GetNode<Label>("Visuals/Label").Text == Menus.Text("SPIRE_MOD_MANAGER.VANILLA_VIEW"));
        var bannerBox = border.GetNode<Control>(LoadOrder.ManagersName).GetGlobalRect();
        var searchBox = border.GetNode<Control>("SpireModManagerSearch").GetGlobalRect();
        var pendingBox = screen.GetNode<Control>("%PendingChangesLabel").GetGlobalRect();
        var buttonsEnd = toggles[1].GetGlobalRect().End.X;
        Check("the managers banner under the search box, above the list; the restart line right after the view buttons (no warning between)",
            bannerBox.Position.Y >= searchBox.End.Y && bannerBox.End.Y <= scroll.GetGlobalRect().Position.Y
            && pendingBox.Position.X > buttonsEnd && pendingBox.Position.X <= buttonsEnd + 14,
            $"banner {bannerBox}, search {searchBox}, list from {scroll.GetGlobalRect().Position.Y}, buttons to {buttonsEnd}, restart line {pendingBox}");

        // Resizes don't set off its re-layout on our views.
        var before = $"{Box(border)} | {Box(scroll)}";
        foreach (var control in new[] { scroll, border.GetNode<Control>("%InstalledModsTitle"), info })
            control.EmitSignal(Control.SignalName.Resized);
        await Wait(menu, 0.3);
        Check("our views: a resize leaves the list where it is", $"{Box(border)} | {Box(scroll)}" == before, $"{before} -> {Box(border)} | {Box(scroll)}");

        // A mod shown in the panel: ours, with the description where ours goes (its Fill postfix runs first).
        var row = content.GetChildren().OfType<NModMenuRow>().First(r => r.Visible && r.Mod?.manifest?.version is { Length: > 0 });
        CategoryList.Select(screen, row);
        await Wait(menu, 0.5);
        var description = info.GetNode<Control>("ModDescription");
        Check("our views: the panel's title and description ours", info.GetNode<RichTextLabel>("ModTitle").Text.Contains("[font_size=")
            && description.AnchorBottom == 0 && description.GetGlobalRect().End.Y <= info.GetGlobalRect().End.Y + 1,
            $"anchor {description.AnchorBottom}, description {description.GetGlobalRect()}, panel {info.GetGlobalRect()}");
        await Capture(screen, outDir, "2-better-mod-menu-ours");

        Click(screen.GetNode<NButton>("SpireModManagerLoadOrderToggle"));
        await Wait(menu, 0.5);
        OursHold("Load Order", 400);
        Click(screen.GetNode<NButton>("SpireModManagerLoadOrderToggle"));
        await Wait(menu, 0.5);

        // Vanilla View: its page.
        var make = border.GetNode<Control>("%MakeModsButton");
        async Task<string> ToVanilla()
        {
            Click(screen.GetNode<NButton>("SpireModManagerViewToggle"));
            await Wait(menu, 1);
            Check("its page: none of ours (headers, strips, managers banner), the game's icons hidden, the list clipped",
                !content.GetChildren().Any(c => c.IsInGroup("SpireModManagerCategoryHeader"))
                && !content.GetChildren().OfType<NModMenuRow>().Any(r => Shown(r.GetNodeOrNull("SpireModManagerStrip")))
                && !Shown(border.GetNodeOrNull(LoadOrder.ManagersName)) && mask.ClipContents
                && content.GetChildren().OfType<NModMenuRow>().All(r => !Shown(r.GetNode("PlatformIcon"))));
            Check("its page: the same mod still selected", CategoryList.Selected == row.Mod && RowState.Selected(row));
            if (chrome != null)
            {
                Rect2 Rect(string name) => screen.FindChild(name, true, false) is Control c ? c.GetGlobalRect() : new Rect2();
                var (top, groups, search, list) = (Rect("BetterModMenuTopBar"), Rect("BetterModMenuGroupBar"), Rect("BetterModMenuSearchBar"), scroll.GetGlobalRect());
                var title = border.GetNode<Control>("%InstalledModsTitle").GetGlobalRect();
                Check("its page: its bars, row controls and panel buttons show", Shown(chrome) && RowControls().Any(Shown)
                    && Shown(info.GetNodeOrNull("BetterModMenuSelectedModActions")));
                // (Its own sizes on this game version: the group bar is taller than the room it keeps for it, and the
                // search bar can end past the panel. Reported, not checked: the same without us.)
                Check("its page: its top bar clear of the title, its group bar above the list, its search below, the list in its panel",
                    !top.Intersects(title) && groups.Position.Y < list.Position.Y && search.Position.Y >= list.End.Y - 1
                    && list.Position.Y >= border.GetGlobalRect().Position.Y,
                    $"title {title}, top bar {top}, groups {groups}, list {list}, search {search}, panel {border.GetGlobalRect()}");
                Results.Add($"INFO its groups: {content.GetChildren().Count(c => c.Name.ToString().StartsWith("ModGroupHeader_"))}; " +
                    $"group bar over the list by {Math.Max(0, groups.End.Y - list.Position.Y)}, search past the panel by {Math.Max(0, search.End.Y - border.GetGlobalRect().End.Y)}");
            }
            return $"list {border.GetGlobalRect()}, scroll {scroll.GetGlobalRect()}, Make Mods {make.GetGlobalRect()}";
        }
        async Task ToOurs(string when)
        {
            Click(screen.GetNode<NButton>("SpireModManagerViewToggle"));
            await Wait(menu, 1);
            OursHold(when, 175);
        }
        var first = await ToVanilla();
        await Capture(screen, outDir, "3-better-mod-menu-page");
        await ToOurs("back on the main list");
        var second = await ToVanilla();
        Check("its page the same the second time (nothing drifts)", second == first, $"{first} -> {second}");
        await ToOurs("back on the main list again");

        // Profiles leave it on or off as it is (both use the game's one mod list).
        bool? BmmOn() => SaveManager.Instance.SettingsSave.ModSettings?.ModList.FirstOrDefault(m => m.Id == Bmm.ModId)?.IsEnabled;
        if (Profiles.Active is { } start && Store.Data.Profiles.FirstOrDefault(p => p != start) is { } other)
        {
            var was = BmmOn();
            Profiles.SwitchTo(other, screen);
            await Wait(menu, 0.5);
            var then = BmmOn();
            Profiles.SwitchTo(start, screen);
            await Wait(menu, 0.5);
            Check("a profile switch leaves Better Mod Menu as it was", then == was && BmmOn() == was, $"{was} -> {then} -> {BmmOn()}");
        }
    }

    // Right-click the main menu button, Move button, click somewhere: it stays there; Reset puts it back.
    static async Task TestMenuButtonMove(NMainMenu menu, string outDir)
    {
        if (menu.GetNodeOrNull<Control>("SpireModManagerMenuButton") is not { } button)
            return;
        var corner = button.Position;
        var from = button.GetGlobalRect().GetCenter();
        var fake = from;
        Hover.TestMouse = () => fake;
        void Mouse(Vector2 at) => fake = at;
        async Task Click(MouseButton which, Vector2 at)
        {
            Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = which, Pressed = true, Position = at, GlobalPosition = at });
            await Wait(menu, 0.1);
            Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = which, Pressed = false, Position = at, GlobalPosition = at });
            await Wait(menu, 0.2);
        }
        async Task<Node?> OpenMenu()
        {
            Mouse(from);
            button.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton { ButtonIndex = MouseButton.Right, Pressed = false, Position = from, GlobalPosition = from });
            await Wait(menu, 0.3);
            return MenuPanel.Current;
        }

        var popup = await OpenMenu();
        Check("right-click offers Move button", popup != null && MenuPanel.Labels(popup).Contains("Move button"));
        if (popup == null)
            return;
        MenuPanel.Press(popup, "Move button");
        await Wait(menu, 0.2);
        // Relative: the real cursor may be anywhere while the test runs.
        var begin = button.Position;
        var at = fake;
        Check("Move button starts move mode", menu.HasNode(MenuButton.MoveLayerName));
        var to = at + new Vector2(500, -500);
        Mouse(to);
        await Wait(menu, 0.2);
        Check("the button follows the mouse, by as much as it moves", button.Position.DistanceTo(begin + new Vector2(500, -500)) < 2, $"{begin} -> {button.Position}");
        await Capture(menu, outDir, "1b-move-button");
        await Click(MouseButton.Left, to);
        Check("a click puts it down, remembered; move mode ends", Store.Data.MenuButtonAt != null && !menu.HasNode(MenuButton.MoveLayerName)
            && button.Position.DistanceTo(begin + new Vector2(500, -500)) < 2, $"{button.Position}, saved {Store.Data.MenuButtonAt}");
        var placed = button.Position;

        // Moving again and pressing Escape leaves it where it was put.
        from = button.GetGlobalRect().GetCenter();
        if (await OpenMenu() is { } second)
        {
            MenuPanel.Press(second, "Move button");
            await Wait(menu, 0.2);
            Mouse(fake + new Vector2(-200, 100));
            await Wait(menu, 0.2);
            Input.ParseInputEvent(new InputEventKey { Keycode = Key.Escape, Pressed = true });
            await Wait(menu, 0.2);
            Input.ParseInputEvent(new InputEventKey { Keycode = Key.Escape, Pressed = false });
            Check("Escape cancels: back where it was put, move mode ends", button.Position == placed
                && !menu.HasNode(MenuButton.MoveLayerName), $"{button.Position} vs {placed}");
            await Wait(menu, 0.3);
            Check("that Escape stays ours: the game doesn't act on it (no save profile screen)", !menu.SubmenuStack.SubmenusOpen);
        }

        // Escape in the same frame as Move button, before move mode starts following the mouse (a frame later): it ends
        // there, and no per-frame handler of move mode is left running.
        int Following() => menu.GetTree().GetSignalConnectionList(SceneTree.SignalName.ProcessFrame)
            .Count(c => c["callable"].AsCallable().Delegate?.Method.DeclaringType?.DeclaringType == typeof(MenuButton));
        if (await OpenMenu() is { } quick)
        {
            MenuPanel.Press(quick, "Move button");
            menu.GetViewport().PushInput(new InputEventKey { Keycode = Key.Escape, Pressed = true });
            var ended = !menu.HasNode(MenuButton.MoveLayerName);
            Mouse(fake + new Vector2(300, -200));
            await Wait(menu, 0.3);
            Check("Escape in the same frame as Move button: move mode ends, nothing left following the mouse", ended
                && !menu.HasNode(MenuButton.MoveLayerName) && Following() == 0 && button.Position == placed && !menu.SubmenuStack.SubmenusOpen,
                $"{Following()} frame handlers, {button.Position} vs {placed}");
        }

        if (await OpenMenu() is { } third)
            MenuPanel.Press(third, "Reset position");
        await Wait(menu, 0.2);
        Check("Reset position puts it back in the corner", Store.Data.MenuButtonAt == null && button.Position == corner, $"{button.Position} vs {corner}");
        Hover.TestMouse = null;
    }

    // The main menu button's right-click menu: Move button, Reset position, a divider, Relaunch game. Relaunch game
    // checks for mods that won't load first, as the Modding page's button does (fixture: Test Addon needs SmmTestMissing,
    // never installed): its dialog shows over the main menu, and its fixes work there before the page has been opened.
    // ModEntry.TestRelaunch counts each relaunch instead of quitting the test.
    static async Task TestMenuButtonRelaunch(NMainMenu menu, string outDir)
    {
        if (menu.GetNodeOrNull<Control>("SpireModManagerMenuButton") is not { } button)
            return;
        var at = button.GetGlobalRect().GetCenter();
        Hover.TestMouse = () => at;
        button.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton { ButtonIndex = MouseButton.Right, Pressed = false, Position = at, GlobalPosition = at });
        await Wait(menu, 0.3);
        Hover.TestMouse = null;
        var popup = MenuPanel.Current;
        var labels = popup == null ? [] : MenuPanel.Labels(popup);
        Check("main menu button's menu: Move button, Reset position, Relaunch game", labels.SequenceEqual(["Move button", "Reset position", "Relaunch game"]),
            string.Join(", ", labels));
        if (popup == null)
            return;
        // The divider: the one entry between Reset position and Relaunch game, a line under the first and above the second.
        string? Label(Node item) => item.IsInGroup(MenuPanel.ItemGroup) ? (string)item.GetMeta("label") : null;
        var items = popup.FindChild("Items", true, false)?.GetChildren().OfType<Control>().ToList() ?? [];
        var reset = items.FindIndex(i => Label(i) == "Reset position");
        var divider = reset >= 0 && reset + 2 < items.Count && Label(items[reset + 2]) == "Relaunch game" ? items[reset + 1] : null;
        Check("a divider right under Reset position, above Relaunch game", divider != null && Label(divider) == null
            && divider.FindChildren("*", "ColorRect", true, false).Count == 1
            && divider.GetGlobalRect().Position.Y >= items[reset].GetGlobalRect().End.Y
            && divider.GetGlobalRect().End.Y <= items[reset + 2].GetGlobalRect().Position.Y,
            string.Join(" | ", items.Select(i => $"{Label(i) ?? "(line)"} {i.GetGlobalRect()}")));
        await Capture(menu, outDir, "1c-menu-button-menu");

        var relaunches = 0;
        ModEntry.TestRelaunch = () => relaunches++;
        try
        {
            Node? Dialog() => menu.GetTree().Root.GetNodeOrNull(RelaunchCheck.Name);
            string Body() => (Dialog()?.FindChild("Body", true, false) as RichTextLabel)?.GetParsedText() ?? "";
            void Press(string name) => Dialog()?.FindChild(name, true, false)?.EmitSignal(BaseButton.SignalName.Pressed);
            MenuPanel.Press(popup, "Relaunch game");
            await Wait(menu, 0.3);
            if (Dialog() == null)
            {
                Check("Relaunch game, nothing wrong: relaunches at once", relaunches == 1 && !MenuPanel.IsOpen, $"{relaunches} relaunches");
                return;
            }
            Check("Relaunch game checks first: its dialog over the main menu, no relaunch yet", relaunches == 0 && !MenuPanel.IsOpen
                && menu.IsVisibleInTree() && !menu.SubmenuStack.SubmenusOpen && Body().Length > 0, $"{relaunches} relaunches: {Body()}");
            await Capture(menu, outDir, "1d-relaunch-check-main-menu");
            if (WontLoad() is not { } addon || Dialog()?.FindChild("DisableBrokenButton", true, false) == null)
            {
                Press("RelaunchNowButton");
                await Wait(menu, 0.2);
                Check("its Relaunch anyway reaches the relaunch", relaunches == 1, $"{relaunches} relaunches");
                return;
            }

            // A fix with no Modding page yet: the active profile and the pending dot follow, and it's one undo step of its
            // own (undoing it leaves the button's Reset position in place).
            var before = ModListState();
            var dot = button.GetNode<Control>("Dot");
            Press("DisableBrokenButton");
            await Wait(menu, 0.3);
            Check("from the main menu, Disable mods that can't load: off, the profile and the dot follow, then a plain Relaunch",
                !Dependencies.IsOn(addon) && Profiles.Edited && dot.Visible == ModEntry.HasPendingChanges()
                && Dialog()?.FindChild("RelaunchNowButton", true, false) is Button { Text: "Relaunch Game" }, Body());
            Press("RelaunchNowButton");
            await Wait(menu, 0.2);
            Check("its Relaunch reaches the relaunch", relaunches == 1, $"{relaunches} relaunches");
            RelaunchCheck.Close();
            History.Undo();
            await Wait(menu, 0.3);
            Check("one undo takes back that fix, and only it", ModListState().SequenceEqual(before) && !Profiles.Edited
                && Store.Data.MenuButtonAt == null && dot.Visible == ModEntry.HasPendingChanges(),
                $"{string.Join(", ", before.Except(ModListState()))}; edited {Profiles.Edited}, button at {Store.Data.MenuButtonAt}");
        }
        finally
        {
            ModEntry.TestRelaunch = null;
            RelaunchCheck.Close();
        }
    }

    // The first-run import reads Better Mod Menu's file as it does: comments and trailing commas, its old format (just
    // a list of profiles), nothing from a broken file, and of several files the one it would load. Sample texts, and
    // files in our output folder: the sandbox's data isn't touched.
    static void TestBetterModMenuImport(string outDir)
    {
        static StoreData? Import(string text)
        {
            try
            {
                return BetterModMenuImport.Parse(text);
            }
            catch (Exception)
            {
                return null;
            }
        }
        static string List(IEnumerable<string> items, string separator = ", ") => string.Join(separator, items) is { Length: > 0 } text ? text : "none";
        static string Fields(ModInfo info) => List(new[] { ("category", info.Category), ("alias", info.Alias), ("notes", info.Notes) }
            .Where(f => f.Item2 != null).Select(f => $"{f.Item1} {f.Item2}"));
        // What came out, in words: each profile with the mods it turns off, the active one, categories, each mod's fields.
        static string Summary(StoreData? data) => data == null ? "refused" : string.Join(" / ",
            "profiles " + List(data.Profiles.Select(p => $"{p.Name} (off: {List(p.Disabled?.Select(r => r.Id) ?? [])})"), "; "),
            "active " + (data.ActiveProfile ?? "none"),
            "categories " + List(data.Categories.Select(c => c.Collapsed ? $"{c.Name} (collapsed)" : c.Name), "; "),
            "mods " + List(data.Mods.OrderBy(m => m.Key, StringComparer.Ordinal).Select(m => $"{m.Key} ({Fields(m.Value)})"), "; "));

        var json5 = Summary(Import("""
            // Better Mod Menu's settings, edited by hand
            {
              "Profiles": [
                { "Name": "Co-op", "DisabledMods": ["ModA", "ModB",], },
                { "Name": "Solo", "DisabledMods": [], },
              ],
              "CurrentProfileIndex": 1,
              "CustomGroups": ["Content", "Visual",], // a trailing comma here too
              "ModGroups": { "ModA": "Content", },
              "CollapsedGroups": ["Visual"],
              "ModAnnotations": { "ModB": { "Alias": "Bee", "Notes": "", }, },
            }
            """));
        Check("Better Mod Menu import: line comments and trailing commas (.json5)", json5 == "profiles Co-op (off: ModA, ModB); Solo (off: none) / active Solo"
            + " / categories Content; Visual (collapsed) / mods ModA (category Content); ModB (alias Bee)", json5);
        var jsonc = Summary(Import("""
            /* Better Mod Menu's settings */
            {
              "Profiles": [ { "Name": "Main", "DisabledMods": [ "ModC" ] } ], /* one profile */
              "CurrentProfileIndex": 0,
              "CustomGroups": [ "Tools" ],
              "ModGroups": { "ModC": "Tools" },
              "ModAnnotations": { "ModC": { "Alias": "", "Notes": "Check after updates" } }
            }
            """));
        Check("Better Mod Menu import: block comments (.jsonc)", jsonc == "profiles Main (off: ModC) / active Main / categories Tools"
            + " / mods ModC (category Tools, notes Check after updates)", jsonc);
        var legacy = Summary(Import("""
            [
              { "Name": "Old favourites", "DisabledMods": ["ModD"] },
              { "DisabledMods": [] }
            ]
            """));
        Check("Better Mod Menu import: its old format (a bare list of profiles) gives those profiles, no categories", legacy
            == "profiles Old favourites (off: ModD); Profile 2 (off: none) / active none / categories none / mods none", legacy);
        var broken = new[] { """{ "Profiles": [ { "Name": "Cut off" """, "\"Profiles\"", "{ Profiles: [] }" }.Select(text => Summary(Import(text))).ToList();
        Check("Better Mod Menu import: a broken file is refused (cut off, not an object or list, unquoted names)", broken.All(s => s == "refused"),
            string.Join(" | ", broken));

        // Which file: beside its mod files before the account's folder, even for a less preferred name; then in a
        // folder .json5, .jsonc, .json.
        var root = Path.Combine(outDir, "bmm-import");
        var (portable, account) = (Path.Combine(root, "portable"), Path.Combine(root, "account"));
        static string In(string folder, string extension) => Path.Combine(folder, "mod_profiles" + extension);
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
        Directory.CreateDirectory(portable);
        Directory.CreateDirectory(account);
        foreach (var file in new[] { In(portable, ".jsonc"), In(portable, ".json"), In(account, ".json"), In(account, ".json5") })
            File.WriteAllText(file, "[]");
        var found = new List<string?> { BetterModMenuImport.FindFile([portable, account]) };
        File.Delete(In(portable, ".jsonc"));
        found.Add(BetterModMenuImport.FindFile([portable, account]));
        File.Delete(In(portable, ".json"));
        found.Add(BetterModMenuImport.FindFile([portable, account]));
        Directory.Delete(root, recursive: true);
        found.Add(BetterModMenuImport.FindFile([portable, account]));
        Check("Better Mod Menu import: the file it would load (its mod folder first, then .json5, .jsonc, .json)",
            found.SequenceEqual([In(portable, ".jsonc"), In(portable, ".json"), In(account, ".json5"), null]), string.Join(" | ", found));
    }

    // Row menu, new category renamed in place, assign, move, header menu, delete.
    static async Task TestCategories(Node menu, NModdingScreen screen, Control content, string outDir)
    {
        const string modId = "SmmTestFixes";
        var row = content.GetChildren().OfType<NModMenuRow>().FirstOrDefault(r => r.Mod?.manifest?.id == modId);
        if (row == null)
        {
            Results.Add($"SKIP categories: {modId} not installed");
            return;
        }

        var rowMenu = CategoryList.ShowRowMenu(content, row);
        await Wait(menu, 0.5);
        var rowItems = rowMenu == null ? [] : MenuPanel.Labels(rowMenu);
        var aliasBox = rowMenu?.FindChild("Fields", true, false)?.GetNodeOrNull<LineEdit>("Alias/Box");
        Check("row menu: categories on the left, the mod's fields on the right", rowItems.Count == Store.Data.Categories.Count + 2
            && aliasBox != null && rowMenu?.FindChild("Fields", true, false)?.GetNodeOrNull("Notes/Box") != null, string.Join(", ", rowItems));
        Check("an empty field invites: \"Add alias\"", aliasBox?.PlaceholderText == "Add alias", aliasBox?.PlaceholderText ?? "");
        await Capture(screen, outDir, "5-row-menu");
        if (rowMenu != null && aliasBox != null)
        {
            // Typing in the menu and clicking away (here: closing it) saves.
            aliasBox.GrabFocus();
            await Wait(menu, 0.1);
            Check("editing shows Save and Cancel, the hint, and the inline clear", aliasBox.ClearButtonEnabled
                && rowMenu.FindChild("Fields", true, false)?.GetNodeOrNull<Control>("Alias/Term/Buttons") is { Visible: true }
                && aliasBox.PlaceholderText != "Add alias");
            aliasBox.Text = "Menu alias";
            await Capture(screen, outDir, "5b-menu-editor");
            MenuPanel.Close();
            await Wait(menu, 0.3);
            Check("closing the menu saves what was typed", Store.InfoFor(modId)?.Alias == "Menu alias");
            Store.EditMod(modId, info => info.Alias = null);
            CategoryList.Apply(content);
        }
        // A right-click outside closes it and reaches what's under the mouse; showing the same menu again
        // (a second press on its button) closes it.
        Hover.TestMouse = () => new Vector2(300, 300); // the menu opens there, well away from the press below
        CategoryList.ShowRowMenu(content, row);
        Hover.TestMouse = null;
        await Wait(menu, 0.2);
        var outside = new Vector2(1500, 900);
        MenuPanel.Current?.GetNode<Control>("Catcher").EmitSignal(Control.SignalName.GuiInput,
            new InputEventMouseButton { ButtonIndex = MouseButton.Right, Pressed = true, Position = outside, GlobalPosition = outside });
        await Wait(menu, 0.2);
        // The forwarded press needs its release, as a real right-click has.
        Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Right, Pressed = false, Position = outside, GlobalPosition = outside });
        await Wait(menu, 0.1);
        Check("a press outside closes the menu", !MenuPanel.IsOpen);
        var picked = MenuPanel.Show(content, () => [new MenuItem("One", () => { })], toggleKey: "test");
        await Wait(menu, 0.1);
        var again = MenuPanel.Show(content, () => [new MenuItem("One", () => { })], toggleKey: "test");
        Check("showing the same menu again closes it", picked != null && again == null && !MenuPanel.IsOpen);


        var category = Categories.Add();
        Categories.Assign(modId, category.Name);
        CategoryList.Apply(content);
        var box = CategoryList.BeginRename(content, category);
        Check("rename box opens", box != null);
        if (box != null)
        {
            box.Text = "Renamed";
            box.EmitSignal(LineEdit.SignalName.TextSubmitted, box.Text);
        }
        await Wait(menu, 0.5);
        Check("rename keeps the mod in the category", Store.InfoFor(modId)?.Category == "Renamed");
        Check("renamed header shown", Header(content, "Renamed") != null);

        // A taken name warns, Enter doesn't take it, clicking away keeps the old name.
        if (Store.Data.Categories.FirstOrDefault(c => c != category) is { } otherCategory && CategoryList.BeginRename(content, category) is { } taken)
        {
            taken.Text = otherCategory.Name;
            taken.EmitSignal(LineEdit.SignalName.TextChanged, taken.Text);
            taken.EmitSignal(LineEdit.SignalName.TextSubmitted, taken.Text);
            await Wait(menu, 0.2);
            Check("renaming a category to a taken name warns and isn't accepted", GodotObject.IsInstanceValid(taken) && taken.IsInsideTree()
                && taken.GetNode<Label>("NameWarning").Text.Length > 0);
            taken.EmitSignal(Control.SignalName.FocusExited);
            await Wait(menu, 0.3);
            Check("clicking away keeps the old name", category.Name == "Renamed" && Header(content, "Renamed") != null);
        }

        Categories.Move(category, -1);
        CategoryList.Apply(content);
        await Wait(menu, 0.5);
        var order = content.GetChildren().Select(c => c.Name.ToString()).ToList();
        Check("moved up: header order follows", order.IndexOf("Category Renamed") < order.IndexOf($"Category {Store.Data.Categories.Last().Name}"));
        Check("mod row sits under its header", content.GetChildren().IndexOf(row) == order.IndexOf("Category Renamed") + 1);
        List<NModMenuRow> Members() => content.GetChildren().OfType<NModMenuRow>().Where(r => Store.InfoFor(r.Mod?.manifest?.id)?.Category == category.Name).ToList();
        var headerMenu = CategoryList.ShowHeaderMenu(content, category, Members());
        await Wait(menu, 0.5);
        await Capture(screen, outDir, "6-header-menu");
        Check("header menu: Enable all, Disable all, Select all, then the category's actions", headerMenu != null
            && string.Join(",", MenuPanel.Labels(headerMenu)).StartsWith("Enable all,Disable all,Select all,Rename"), headerMenu == null ? "no menu" : string.Join(",", MenuPanel.Labels(headerMenu)));
        MenuPanel.Close();
        // Disable all turns the category's mods off (this mod stays on), as one undo step.
        History.Record(); // the category edits above went straight to the store
        var inCategory = Members().Select(r => r.Mod).OfType<Mod>().ToList();
        var wereOn = inCategory.Where(Dependencies.IsOn).ToList();
        if (CategoryList.ShowHeaderMenu(content, category, Members()) is { } allMenu)
        {
            MenuPanel.Press(allMenu, "Disable all");
            await Wait(menu, 0.3);
            Check("Disable all turns the category's mods off", inCategory.All(m => !Dependencies.IsOn(m) || m.manifest?.id == ModEntry.ModId),
                string.Join(", ", inCategory.Select(m => $"{m.manifest?.id} {Dependencies.IsOn(m)}")));
            History.Undo();
            await Wait(menu, 0.3);
            Check("one undo turns them back on", wereOn.All(Dependencies.IsOn));
        }
        category = Store.Data.Categories.First(c => c.Name == "Renamed"); // undo restores the data as new objects

        // Delete from the header menu asks first; with one mod in the category, in the singular. (Cancelled by
        // closing the popup; the deletion itself is below.)
        if (CategoryList.ShowHeaderMenu(content, category, Members()) is { } deleteMenu)
        {
            MenuPanel.Press(deleteMenu, "Delete");
            await Wait(menu, 0.5);
            var asked = PopupTexts();
            Check("deleting a category with one mod asks, in the singular", asked.Any(t => t.Contains("holds 1 mod.")), string.Join(" | ", asked.Where(t => t.Length > 0)));
            NModalContainer.Instance?.Clear();
            await Wait(menu, 0.3);
        }

        Categories.Delete(category);
        CategoryList.Apply(content);
        await Wait(menu, 0.5);
        Check("delete returns the mod to Uncategorized", Store.InfoFor(modId)?.Category == null && Header(content, "Renamed") == null);
    }

    // New profile from the bar, renamed in its name box, then deleted while active.
    static async Task TestProfileEditing(Node menu, NModdingScreen screen, string outDir)
    {
        var bar = screen.GetNode("ModsBorder/SpireModManagerProfileBar");
        var name = (LineEdit)bar.FindChild("ProfileName", true, false);
        var picker = (Button)bar.FindChild("ProfilePicker", true, false);
        Control Button(string id) => (Control)bar.FindChild(id, true, false);
        var before = ModListState();
        var count = Store.Data.Profiles.Count;
        var start = Profiles.Active;

        // New: the name box opens first; Save creates a blank profile (only this mod on) and switches to it.
        var under = await RealClick(menu, Button("NewProfileButton"));
        Check("New opens the name box with a free name", name.Visible && name.HasFocus() && name.Text.StartsWith("Profile ")
            && !picker.Visible && !Button("Actions").Visible, $"{name.Text}; under the mouse: {under}");
        name.Text = "Brother co-op";
        await RealClick(menu, Button("SaveNameButton"));
        Check("Save creates the blank profile and switches to it", Store.Data.Profiles.Count == count + 1 && Profiles.Active?.Name == "Brother co-op"
            && !name.Visible && picker.Visible && ModListState().Count(s => s.EndsWith("=on") && !s.StartsWith(Bmm.ModId + "/")) == 1,
            string.Join(", ", ModListState())); // only ours on (Better Mod Menu, with --bmm, stays as it is)

        // A taken name can't be saved; Cancel leaves everything as it was.
        await RealClick(menu, Button("RenameProfileButton"));
        name.Text = start?.Name ?? "";
        name.EmitSignal(LineEdit.SignalName.TextChanged, name.Text);
        Check("renaming to a taken name warns and can't be saved", Button("NameWarning").Visible && Button("SaveNameButton") is BaseButton { Disabled: true },
            ((Label)Button("NameWarning")).Text);
        await Capture(screen, outDir, "7a-name-taken");
        await RealClick(menu, Button("CancelNameButton"));
        Check("Cancel keeps the old name", Profiles.Active?.Name == "Brother co-op" && !name.Visible);

        // Duplicate: the name box offers "Name (1)".
        await RealClick(menu, Button("DuplicateProfileButton"));
        Check("Duplicate offers \"(1)\" after the name", name.Text == "Brother co-op (1)", name.Text);
        name.EmitSignal(LineEdit.SignalName.TextSubmitted, name.Text);
        await Wait(menu, 0.3);
        Check("Enter saves the duplicate, which becomes active", Profiles.Active?.Name == "Brother co-op (1)" && Store.Data.Profiles.Count == count + 2);
        if (Profiles.Active is { } copy)
            Profiles.Delete(copy, screen);

        // The game's confirm popup, shown for the record (with our mark); deletion itself goes through Profiles.Delete.
        var body = new LocString("settings_ui", "SPIRE_MOD_MANAGER.DELETE_PROFILE_BODY");
        body.Add("name", "Brother co-op");
        _ = Menus.Confirm(new LocString("settings_ui", "SPIRE_MOD_MANAGER.DELETE_PROFILE_TITLE"), body);
        await Wait(menu, 1);
        CheckMark("delete profile popup", NModalContainer.Instance?.OpenModal as Control);
        await Capture(screen, outDir, "7-delete-profile-confirm");
        NModalContainer.Instance?.Clear();
        await Wait(menu, 0.5);

        if (Store.Data.Profiles.FirstOrDefault(p => p.Name == "Brother co-op") is { } created)
        {
            if (Profiles.Active != created)
                Profiles.SwitchTo(created, screen);
            Profiles.Delete(created, screen);
        }
        await Wait(menu, 0.5);
        Check("delete the active profile switches to a neighbour", Store.Data.Profiles.Count == count && Profiles.Active != null
            && Store.Data.Profiles.All(p => p.Name != "Brother co-op") && picker.Text == Profiles.Active.Name);
        if (start != null && Profiles.Active != start)
            Profiles.SwitchTo(start, screen);
        Check("mod list unchanged after new/duplicate/rename/delete", before.SequenceEqual(ModListState()));
        Check("last profile can't be deleted", Store.Data.Profiles.Count > 1 || !Profiles.Delete(Store.Data.Profiles[0], screen));

        // The profile list: opens below the picker; a second press on the picker closes it.
        await RealClick(menu, picker);
        var list = MenuPanel.Current;
        Check("the picker opens the profile list, with details, then Share profile and Import profile", list != null
            && MenuPanel.Labels(list).SequenceEqual(Store.Data.Profiles.Select(p => p.Name).Concat(["Share profile", "Import profile"])),
            list == null ? "no list" : string.Join(", ", MenuPanel.Labels(list)));
        await Capture(screen, outDir, "7b-profile-list");
        await RealClick(menu, picker);
        Check("a second press on the picker closes it", !MenuPanel.IsOpen);
    }

    // Turning a dependency off and on again: the rows' notes follow at once (no stale "(off)"), and the
    // library says how many mods that are on need it.
    static async Task TestPendingNotes(Node menu, NModdingScreen screen, Control content)
    {
        NModMenuRow? Row(string id) => content.GetChildren().OfType<NModMenuRow>().FirstOrDefault(r => r.Mod?.manifest?.id == id);
        string Note(string id) => Row(id)?.GetNodeOrNull<RichTextLabel>("SpireModManagerStrip/Needs")?.GetParsedText() ?? "";
        if (Row("SmmTestLib") is not { } lib)
        {
            Results.Add("SKIP pending notes: test mods not installed");
            return;
        }
        var tickbox = lib.GetNode<NTickbox>("Tickbox");
        Check("a library says how many mods need it", Note("SmmTestLib").Contains("Required for 2 enabled mods"), Note("SmmTestLib"));
        tickbox.IsTicked = false;
        tickbox.EmitSignal(NTickbox.SignalName.Toggled, tickbox);
        await Wait(menu, 0.3);
        Check("turning the library off marks it at once", Note("SmmTestUi").Contains("(off)"), Note("SmmTestUi"));
        tickbox.IsTicked = true;
        tickbox.EmitSignal(NTickbox.SignalName.Toggled, tickbox);
        await Wait(menu, 0.3);
        Check("turning it back on clears \"(off)\" at once", !Note("SmmTestUi").Contains("(off)"), Note("SmmTestUi"));
    }

    // The game's restart line: our wording on our views (the main list, Load Order), the game's in Vanilla View; still
    // ours once a tickbox change makes the game show it.
    static async Task TestPendingLine(Node menu, NModdingScreen screen, Control content)
    {
        var line = screen.GetNode<RichTextLabel>("%PendingChangesLabel");
        var ours = Menus.Text("SPIRE_MOD_MANAGER.PENDING_RESTART");
        var game = new LocString("settings_ui", "MODDING_SCREEN.PENDING_CHANGES_WARNING").GetFormattedText();
        var onList = line.Text;
        string? shown = null; // the line's text once the game shows it (a test mod turned off, then on again)
        if (content.GetChildren().OfType<NModMenuRow>().FirstOrDefault(r => r.Mod?.manifest?.id == "SmmTestUi")?.GetNode<NTickbox>("Tickbox") is { } tickbox)
        {
            tickbox.IsTicked = !tickbox.IsTicked;
            tickbox.EmitSignal(NTickbox.SignalName.Toggled, tickbox);
            await Wait(menu, 0.3);
            shown = line.Visible ? line.Text : "not shown";
            tickbox.IsTicked = !tickbox.IsTicked;
            tickbox.EmitSignal(NTickbox.SignalName.Toggled, tickbox);
            await Wait(menu, 0.3);
        }
        else
            Results.Add("SKIP the restart line once shown: test mod not installed");
        Click(screen.GetNode<NButton>("SpireModManagerLoadOrderToggle"));
        await Wait(menu, 0.3);
        var inLoadOrder = line.Text;
        Click(screen.GetNode<NButton>("SpireModManagerLoadOrderToggle"));
        await Wait(menu, 0.3);
        var inVanilla = "";
        await WithVanillaView(screen, menu, () =>
        {
            inVanilla = line.Text;
            return true;
        });
        Check("the restart line: ours on the main list (also once shown) and in Load Order, the game's in Vanilla View, ours again after",
            ours == "Mod changes will be applied after a restart." && game != ours && onList == ours && (shown == null || shown == ours)
            && inLoadOrder == ours && inVanilla == game && line.Text == ours,
            $"\"{onList}\" / shown \"{shown}\" / \"{inLoadOrder}\" / \"{inVanilla}\" / \"{line.Text}\"");
    }

    // Unsaved edits: a tickbox change leaves the saved profile alone and shows Unsaved; the edit survives
    // switching away, can be picked again, discarded or saved.
    static async Task TestUnsavedProfile(Node menu, NModdingScreen screen, Control content, string outDir)
    {
        var bar = screen.GetNode("ModsBorder/SpireModManagerProfileBar");
        if (Profiles.Active is not { } profile || content.GetChildren().OfType<NModMenuRow>().FirstOrDefault(r => r.Mod?.manifest?.id == "SmmTestUi") is not { Mod: { } mod } row
            || Store.Data.Profiles.FirstOrDefault(p => p != profile) is not { } other)
        {
            Results.Add("SKIP unsaved profile: needs two profiles and the test mods");
            return;
        }
        var tickbox = row.GetNode<NTickbox>("Tickbox");
        bool On() => Dependencies.IsOn(mod);
        bool SavedOn() => profile.Mods.FirstOrDefault(m => m.Id == "SmmTestUi")?.Enabled ?? false;
        async Task Toggle(bool on)
        {
            tickbox.IsTicked = on;
            tickbox.EmitSignal(NTickbox.SignalName.Toggled, tickbox);
            await Wait(menu, 0.3);
        }
        void Press(string name) => bar.FindChild(name, true, false)?.EmitSignal(BaseButton.SignalName.Pressed);
        if (Profiles.Edited)
            Profiles.Save(); // start clean
        var wasOn = On();
        string Shown(string box) => bar.FindChild(box, true, false) is Control { Visible: true } c
            ? string.Join(",", c.GetChildren().Select(b => b.Name.ToString())) : "";
        await Wait(menu, 0.2);
        Check("saved profile: New, Duplicate, Rename, Delete; no Save", Shown("Actions") == "NewProfileButton,DuplicateProfileButton,RenameProfileButton,DeleteProfileButton"
            && Shown("UnsavedButtons") == "", Shown("Actions"));

        await Toggle(!wasOn);
        Check("a tickbox change is an unsaved edit; the saved profile stays", Profiles.Edited && SavedOn() == wasOn
            && bar.FindChild("Unsaved", true, false) is Control { Visible: true } && bar.FindChild("SaveProfileButton", true, false) != null,
            $"edited {Profiles.Edited}, saved on {SavedOn()}, was on {wasOn}, now on {On()}, unsaved {(bar.FindChild("Unsaved", true, false) as Control)?.Visible}, save {bar.FindChild("SaveProfileButton", true, false) != null}");
        Check("edited: Save, Save as new, Discard beside Unsaved", Shown("UnsavedButtons") == "SaveProfileButton,SaveAsNewButton,DiscardButton", Shown("UnsavedButtons"));
        await Capture(screen, outDir, "3b-unsaved-profile");

        // "Unsaved •": its card opens at once, with no delay (checked outside any grace period, which would open a
        // card at once anyway). A click lists the changes in the info panel, names as links, and a second click
        // closes it. One list at a time: the Changes list takes the panel from it, and it takes the panel back.
        // While open it follows the tickboxes: back on the saved state, it says nothing is unsaved.
        var unsaved = (Control)bar.FindChild("Unsaved", true, false);
        var info = screen.GetNode<Control>("%ModInfoContainer");
        var line = $"{(wasOn ? "−" : "+")} {mod.manifest?.name}";
        Hover.Hide();
        await Wait(menu, Hover.Grace + 0.1);
        Hover.TestMouse = () => unsaved.GetGlobalRect().GetCenter();
        unsaved.EmitSignal(Control.SignalName.MouseEntered);
        var opened = screen.HasNode("SpireModManagerUnsavedCard");
        await Wait(menu, 0.1);
        var card = screen.GetNodeOrNull("SpireModManagerUnsavedCard")?.FindChildren("*", "RichTextLabel", true, false).OfType<RichTextLabel>().FirstOrDefault()?.GetParsedText() ?? "";
        Check("the Unsaved card opens at once, naming the change and saying a click lists it", opened && card.Contains(line)
            && card.Contains(Menus.Text("SPIRE_MOD_MANAGER.UNSAVED_CLICK")), $"opened at once {opened}: {card}");
        unsaved.EmitSignal(Control.SignalName.MouseExited);
        Hover.TestMouse = null;
        await Wait(menu, 0.1);

        void ClickOn(Control control) => control.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false });
        string Title() => info.GetNodeOrNull<Label>($"{HistoryBar.ViewName}/Head/Title")?.Text ?? "";
        string Listed() => info.GetNodeOrNull<RichTextLabel>($"{HistoryBar.ViewName}/List")?.GetParsedText() ?? "";
        var unsavedTitle = Menus.Format("SPIRE_MOD_MANAGER.UNSAVED_LIST_TITLE", ("name", profile.Name));
        ClickOn(unsaved);
        await Wait(menu, 0.2);
        Check("clicking Unsaved lists the profile's unsaved changes in the info panel, names as links", HistoryBar.ListOpen(info, HistoryBar.ListKind.Unsaved)
            && Title() == unsavedTitle && Listed().Contains(line)
            && info.GetNodeOrNull<RichTextLabel>($"{HistoryBar.ViewName}/List")?.Text.Contains("[url=mod:SmmTestUi]") == true, $"{Title()}: {Listed()}");
        await Capture(screen, outDir, "3c-unsaved-list");
        ClickOn(unsaved);
        await Wait(menu, 0.2);
        Check("clicking Unsaved again closes the list", !HistoryBar.ListOpen(info));
        ClickOn(unsaved);
        ClickOn(screen.GetNode<Control>(HistoryBar.ChangesName));
        await Wait(menu, 0.2);
        Check("opening the Changes list closes the unsaved list", HistoryBar.ListOpen(info, HistoryBar.ListKind.Changes)
            && Title() == Menus.Text("SPIRE_MOD_MANAGER.CHANGES_TITLE") && !Listed().Contains(line), $"{Title()}: {Listed()}");
        ClickOn(unsaved);
        await Wait(menu, 0.2);
        Check("and opening the unsaved list closes the Changes list", HistoryBar.ListOpen(info, HistoryBar.ListKind.Unsaved)
            && Title() == unsavedTitle && Listed().Contains(line), $"{Title()}: {Listed()}");
        await Toggle(wasOn);
        Check("the open unsaved list follows the tickboxes: back on the saved state, nothing unsaved", HistoryBar.ListOpen(info, HistoryBar.ListKind.Unsaved)
            && Listed().Contains(Menus.Text("SPIRE_MOD_MANAGER.NO_UNSAVED")), Listed());
        HistoryBar.CloseList(info);
        await Toggle(!wasOn); // the edit again, for the checks below

        Check("the picker keeps the plain name", bar.FindChild("ProfilePicker", true, false) is Button { Text: var shown } && shown == profile.Name);
        var profiles = Store.Data.Profiles.Count;
        Profiles.SwitchTo(other, screen);
        Check("leaving keeps the edit with its profile, marked *, no extra profile", profile.Edited != null && Store.Data.Profiles.Count == profiles
            && !Profiles.Edited);
        Profiles.SwitchTo(profile, screen);
        await Wait(menu, 0.3);
        Check("coming back restores the edit", Profiles.Edited && On() != wasOn && SavedOn() == wasOn);

        Press("DiscardButton");
        await Wait(menu, 0.3);
        Check("Discard goes back to the saved state and drops the edit", !Profiles.Edited && On() == wasOn && profile.Edited == null
            && bar.FindChild("Unsaved", true, false) is Control { Visible: false });

        await Toggle(!wasOn);
        Press("SaveProfileButton");
        await Wait(menu, 0.3);
        Check("Save makes the edit the saved state", !Profiles.Edited && SavedOn() != wasOn && profile.Edited == null);
        await Toggle(wasOn); // and back, saved (with Ctrl+S), for the tests after
        Input.ParseInputEvent(new InputEventKey { Keycode = Key.S, CtrlPressed = true, Pressed = true });
        await Wait(menu, 0.3);
        Check("Ctrl+S saves the profile", !Profiles.Edited && SavedOn() == wasOn && profile.Edited == null);
    }

    // Sharing a profile as CSV text and importing one (ProfileShare): the format on its own, the Share dialog (from the
    // profile list), Copy and saving, then the Import dialog with our own list and a Better Mod Menu one, its preview,
    // its errors, and an import as one undo step. Starts and ends on the active profile with nothing changed.
    static async Task TestProfileShare(Node menu, NModdingScreen screen, string outDir)
    {
        var bar = screen.GetNode("ModsBorder/SpireModManagerProfileBar");
        if (Profiles.Active is not { } start || Dependencies.Find("SmmTestUi") == null || Dependencies.Find("SmmTestLib") == null
            || Store.Data.Profiles.FirstOrDefault(p => p != start) is not { } other)
        {
            Results.Add("SKIP profile sharing: needs two profiles and the test mods");
            return;
        }
        if (Profiles.Edited)
            Profiles.Save(); // start clean
        // Undo puts our data back as new objects: profiles are found by name after one.
        var (startName, otherName, profiles) = (start.Name, other.Name, Store.Data.Profiles.Count);
        Profile? Named(string name) => Store.Data.Profiles.FirstOrDefault(p => p.Name == name);
        Node? Dialog() => menu.GetTree().Root.GetNodeOrNull(ProfileShare.Name);
        T? Part<T>(string name) where T : class => Dialog()?.FindChild(name, true, false) as T;
        void Press(string name) => Part<BaseButton>(name)?.EmitSignal(BaseButton.SignalName.Pressed);
        string Status() => Part<Label>("Status") is { Visible: true } status ? status.Text : "";
        bool StatusRed() => Part<Label>("Status")?.GetThemeColor("font_color") is { R: > 0.9f, G: < 0.6f };
        string Preview() => Part<RichTextLabel>("Preview")?.GetParsedText() ?? "";
        async Task Paste(string text)
        {
            if (Part<TextEdit>("CsvText") is { } box)
            {
                box.Text = text;
                box.EmitSignal(TextEdit.SignalName.TextChanged);
            }
            await Wait(menu, 0.2);
        }
        async Task Choose(string label)
        {
            await RealClick(menu, (Button)bar.FindChild("ProfilePicker", true, false));
            if (MenuPanel.Current is { } list)
                MenuPanel.Press(list, label);
            await Wait(menu, 0.3);
        }
        bool IsOn(string id) => Dependencies.Find(id) is { } mod && Dependencies.IsOn(mod);

        // The format on its own: quoting, the formula guard, and a forgiving read.
        var tricky = ProfileCsv.Write([
            new ProfileCsv.Row("SmmQuote", "Comma, \"quoted\"\nname", "1.0", true, "A, B", ""),
            new ProfileCsv.Row("SmmFormula", "=HYPERLINK(\"x\")", "-1", false, "@group", ""),
        ]);
        var back = ProfileCsv.Read(tricky).Mods;
        var trickyRecords = ProfileCsv.Records(tricky);
        Check("CSV: a name with a comma, quotes and a line break round-trips, quoted", tricky.Contains("\"Comma, \"\"quoted\"\"\nname\"")
            && back is [{ Id: "SmmQuote", Enabled: true, Name: "Comma, \"quoted\"\nname" }, ..] && trickyRecords.Count == 3 && trickyRecords[1][4] == "A, B",
            tricky);
        Check("CSV: a value starting with = + - @ gets a ' in front, taken off again when read", tricky.Contains("\"'=HYPERLINK(\"\"x\"\")\",'-1,FALSE,'@group,")
            && back is [_, { Id: "SmmFormula", Enabled: false, Name: "=HYPERLINK(\"x\")" }], tricky);
        var loose = ProfileCsv.Read("enabled;NAME;id\nyes;a;SmmA\r\nNo;b;SmmB\n\n1;c;SmmC\n0;\"d;e\";SmmD").Mods;
        Check("CSV: columns by name in any order and case, Id for Mod Id, a ; between values, yes/no and 1/0",
            string.Join(",", loose.Select(m => $"{m.Id}={m.Enabled}")) == "SmmA=True,SmmB=False,SmmC=True,SmmD=False" && loose[3].Name == "d;e",
            string.Join(",", loose.Select(m => $"{m.Id}={m.Enabled}/{m.Name}")));
        var (tabs, semicolons) = (ProfileCsv.Read("Mod Id\tEnabled\nSmmA\tTRUE\n"), ProfileCsv.Read("\r\n\r\nMod Id;Enabled\r\nSmmB;no\r\n"));
        Check("CSV: just Mod Id and Enabled, with a tab or a ; between (after blank lines too)",
            tabs.Mods is [{ Id: "SmmA", Enabled: true }] && semicolons.Mods is [{ Id: "SmmB", Enabled: false }], $"{tabs.Error} / {semicolons.Error}");
        Check("CSV: a Workshop link is rebuilt from its id; anything else is dropped",
            ProfileCsv.CleanLink("https://steamcommunity.com/sharedfiles/filedetails/?id=3123456789&searchtext=") == "https://steamcommunity.com/sharedfiles/filedetails/?id=3123456789"
            && ProfileCsv.CleanLink("https://example.com/?x=1") == "" && ProfileCsv.IsWorkshopLink(ProfileCsv.WorkshopLink(42)) && !ProfileCsv.IsWorkshopLink("https://example.com"));

        // Share profile, from the profile list: the active profile's list, a row per installed mod in load order, on and
        // off as it has them, the category as Group, no alias or notes (the fixture gives this mod both).
        await Choose("Share profile");
        var csv = Part<TextEdit>("CsvText")?.Text ?? "";
        var records = ProfileCsv.Records(csv);
        var installed = ModManager.Mods.Union(Installed.Mods).Where(m => m.manifest?.id != null).ToList();
        var order = (SaveManager.Instance.SettingsSave.ModSettings?.ModList ?? []).Select(e => e.Id)
            .Where(id => installed.Any(m => m.manifest?.id == id)).Distinct().ToList();
        var ids = records.Skip(1).Select(r => r[0]).ToList();
        Check("Share profile opens with the active profile's list: the header, a row per installed mod in load order",
            Dialog() != null && records.FirstOrDefault()?.SequenceEqual(ProfileCsv.Header) == true && ids.Count == installed.Count
            && ids.Take(order.Count).SequenceEqual(order) && Part<RichTextLabel>("Body")?.GetParsedText().Contains(startName) == true,
            $"{string.Join(" | ", ids)} vs order {string.Join(" | ", order)}");
        Check("each row's Enabled as the profile has it, TRUE and FALSE both there", records.Skip(1).All(r => r[3] == (IsOn(r[0]) ? "TRUE" : "FALSE"))
            && ids.Contains("SmmTestRetired") && records.Any(r => r[3] == "TRUE") && records.Any(r => r[3] == "FALSE"),
            string.Join(", ", records.Skip(1).Select(r => $"{r[0]}={r[3]}")));
        Check("Group is the category; no alias or notes", records.FirstOrDefault(r => r[0] == ModEntry.ModId)?[4] == "Tools"
            && !csv.Contains("The Manager") && !csv.Contains("Fixture note") && records.Skip(1).All(r => r.Count == 8 && r[6] == "" && r[7] == ""), csv);
        await Capture(screen, outDir, "7c-share-profile");
        var clipboard = DisplayServer.ClipboardGet(); // put back after: a windowed run is on Jordan's desktop
        Press("CopyButton");
        await Wait(menu, 0.1);
        Check("Copy says Copied", Part<Button>("CopyButton")?.Text == "Copied", Part<Button>("CopyButton")?.Text ?? "no button");
        if (DisplayServer.GetName() == "headless")
            Results.Add("SKIP Copy puts the text on the clipboard: there's no clipboard headless");
        else
            Check("Copy puts the text on the clipboard", DisplayServer.ClipboardGet() == csv);
        DisplayServer.ClipboardSet(clipboard);
        var file = Path.Combine(outDir, ProfileShare.FileName("Co-op: \"night\"?"));
        var saveError = ProfileShare.SaveTo(file, csv);
        var bytes = File.Exists(file) ? File.ReadAllBytes(file) : [];
        Check("Save CSV: a name made safe for a file, the text as UTF-8 with a BOM", Path.GetFileName(file) == "Co-op_ _night__.csv" && saveError == null
            && bytes is [0xEF, 0xBB, 0xBF, ..] && File.ReadAllText(file) == csv, $"{Path.GetFileName(file)}: {saveError}");
        Input.ParseInputEvent(new InputEventKey { Keycode = Key.Escape, Pressed = true });
        await Wait(menu, 0.2);
        Input.ParseInputEvent(new InputEventKey { Keycode = Key.Escape, Pressed = false });
        Check("Esc closes Share profile, and stays on the page", Dialog() == null && screen.IsVisibleInTree());

        // Import our own list while on the other profile: the preview, then Create profile brings the states back, in a
        // new profile named "Imported profile", made active. One undo step takes it away again.
        var startStates = ModListState();
        Profiles.SwitchTo(other, screen);
        await Wait(menu, 0.3);
        var otherStates = ModListState();
        await Choose("Import profile");
        await Paste(csv);
        Check("Import profile: our own list previews the new profile and what it enables", Dialog() != null && Status() == ""
            && Preview().Contains("Creates the profile Imported profile:") && Preview().Contains("Enables ") && !Preview().Contains("Not installed"), Preview());
        Press("CreateProfileButton");
        await Wait(menu, 0.3);
        Check("Create profile: a new profile with the list's states, active, and the dialog closed", Dialog() == null && Profiles.Active?.Name == "Imported profile"
            && Store.Data.Profiles.Count == profiles + 1 && !Profiles.Edited && ModListState().SequenceEqual(startStates),
            $"{Profiles.Active?.Name}: {string.Join(", ", startStates.Except(ModListState()))}");
        History.Undo();
        await Wait(menu, 0.3);
        Check("undo takes the imported profile away, back on the profile before", Named("Imported profile") == null && Store.Data.Profiles.Count == profiles
            && Profiles.Active?.Name == otherName && ModListState().SequenceEqual(otherStates), $"{Profiles.Active?.Name}: {string.Join(", ", Store.Data.Profiles.Select(p => p.Name))}");

        // Better Mod Menu's own export: its columns and order, a BOM, CRLF, TRUE/FALSE, a group with a comma, a guarded
        // alias, notes over two lines, and two mods that aren't installed (one with its Workshop link, one off).
        const string link = "https://steamcommunity.com/sharedfiles/filedetails/?id=3123456789";
        var bmm = "\uFEFFMod Id,Name,Version,Enabled,Group,Workshop Link,Alias,Notes\r\n"
            + "SmmTestGoneOff,Gone Off,1.0,FALSE,\"Content, mostly\",,,\r\n"
            + $"SmmTestNotHere,Not Here Mod,2.0,TRUE,\"Content, mostly\",{link},'=cool,\"Line one\r\nline two\"\r\n"
            + "SmmTestLib,Test Framework,1.0.0,TRUE,Frameworks,,,\r\n"
            + "SmmTestUi,Test UI,1.0.0,FALSE,Ungrouped,,,\r\n";
        var plan = Profiles.PlanImport(ProfileCsv.Read(bmm).Mods);
        var (wantOn, wantOff) = ((IsOn("SmmTestLib") ? 0 : 1), (IsOn("SmmTestUi") ? 1 : 0));
        var others = installed.Count(m => m.manifest?.id is { } id && id != ModEntry.ModId && id != Bmm.ModId && id != "SmmTestLib" && id != "SmmTestUi");
        await Choose("Import profile");
        await Paste(bmm);
        var bbcode = Part<RichTextLabel>("Preview")?.Text ?? "";
        Check("a Better Mod Menu list: what it changes compared with now, and the installed mods it doesn't name",
            Status() == "" && plan.On == wantOn && plan.Off == wantOff && plan.Same == 2 - wantOn - wantOff && plan.Unlisted == others
            && Preview().Contains(others == 1 ? "1 installed mod isn't in the list" : $"{others} installed mods aren't in the list"),
            $"on {plan.On}/{wantOn}, off {plan.Off}/{wantOff}, same {plan.Same}, unlisted {plan.Unlisted}/{others}: {Preview()}");
        Check("the mods it names that aren't installed are listed, by name, with the Workshop link", plan.Missing.Count == 2
            && Preview().Contains("Not installed (2)") && Preview().Contains("Not Here Mod  Workshop page") && Preview().Contains("Gone Off (disabled)")
            && bbcode.Contains($"[url={link}]"), Preview());
        await Capture(screen, outDir, "7d-import-preview");
        Press("CreateProfileButton");
        await Wait(menu, 0.3);
        var made = Profiles.Active;
        Check("its states applied; the new profile keeps the mods that aren't installed, for later", made?.Name == "Imported profile"
            && IsOn("SmmTestLib") && !IsOn("SmmTestUi")
            && made.Mods.Any(m => m is { Id: "SmmTestNotHere", Source: ModSource.SteamWorkshop, Enabled: true })
            && made.Mods.Any(m => m is { Id: "SmmTestGoneOff", Source: ModSource.ModsDirectory, Enabled: false }),
            string.Join(", ", made?.Mods.Select(m => $"{m.Id}/{m.Source}={m.Enabled}") ?? []));
        History.Undo();
        await Wait(menu, 0.3);
        Check("and undo takes that one away too", Named("Imported profile") == null && Profiles.Active?.Name == otherName && ModListState().SequenceEqual(otherStates));

        // Errors show in red in the dialog, and nothing is made: no Mod Id column, Enabled that isn't a yes or no, no text.
        await Choose("Import profile");
        await Paste("Name,Enabled\r\nTest UI,TRUE\r\n");
        var noId = (Status(), StatusRed(), Preview());
        await Capture(screen, outDir, "7e-import-error");
        await Paste("Mod Id,Enabled\nSmmTestUi,maybe\n");
        var badValue = Status();
        await Paste("");
        Press("CreateProfileButton");
        await Wait(menu, 0.2);
        Check("a list without Mod Id says so in red, with no preview", noId.Item1.StartsWith("This list has no Mod Id column") && noId.Item2 && noId.Item3 == "",
            $"\"{noId.Item1}\" red {noId.Item2}, preview \"{noId.Item3}\"");
        Check("a bad Enabled value names its mod", badValue == "Enabled for SmmTestUi should be TRUE or FALSE, not \"maybe\".", badValue);
        Check("Create profile with no text says what to do, in red, and makes nothing", Dialog() != null && Status() == "Paste a mod list or load a CSV file first."
            && StatusRed() && Store.Data.Profiles.Count == profiles, Status());
        Check("the imported name: the file's, numbered when taken; not Better Mod Menu's time-stamped one",
            Profiles.ImportName("C:/lists/Brother co-op.csv") == "Brother co-op" && Profiles.ImportName($"{startName}.csv") == Profiles.CopyName(startName)
            && Profiles.ImportName("C:/x/mod_list.20261014-120000.csv") == "Imported profile" && Profiles.ImportName("mod_list.20261014-120000-2.csv") == "Imported profile",
            Profiles.ImportName($"{startName}.csv"));
        Part<TextEdit>("CsvText")?.GrabFocus();
        Input.ParseInputEvent(new InputEventKey { Keycode = Key.Escape, Pressed = true });
        await Wait(menu, 0.2);
        Input.ParseInputEvent(new InputEventKey { Keycode = Key.Escape, Pressed = false });
        Check("Esc in the text box closes Import profile", Dialog() == null && screen.IsVisibleInTree());

        if (Named(startName) is { } back2)
            Profiles.SwitchTo(back2, screen);
        await Wait(menu, 0.3);

        // A dialog takes the keys from the page: with an unsaved edit and the search box focused, holding text, Ctrl+S
        // and Ctrl+Z do nothing behind Share, and Esc closes it and leaves the search alone. Then the edit is discarded.
        var content = screen.GetNode<Control>("%ModsScrollContainer/Mask/Content");
        Dependencies.Set([Dependencies.Find("SmmTestUi")!], !IsOn("SmmTestUi"), screen);
        CategoryList.Redraw(content);
        await Wait(menu, 0.3);
        var edited = (Profiles.Edited, string.Join(", ", ModListState()));
        var search = screen.GetNodeOrNull<LineEdit>("ModsBorder/SpireModManagerSearch");
        if (search != null)
        {
            search.Text = "zzz"; // no TextChanged: the list isn't filtered
            search.GrabFocus();
        }
        await Choose("Share profile");
        foreach (var key in new[] { Key.S, Key.Z })
        {
            Input.ParseInputEvent(new InputEventKey { Keycode = key, CtrlPressed = true, Pressed = true });
            await Wait(menu, 0.2);
            Input.ParseInputEvent(new InputEventKey { Keycode = key, CtrlPressed = true, Pressed = false });
        }
        Check("Ctrl+S and Ctrl+Z do nothing while Share profile is open", Dialog() != null && edited.Edited
            && (Profiles.Edited, string.Join(", ", ModListState())) == edited, $"edited {Profiles.Edited}, was {edited.Edited}");
        Input.ParseInputEvent(new InputEventKey { Keycode = Key.Escape, Pressed = true });
        await Wait(menu, 0.2);
        Input.ParseInputEvent(new InputEventKey { Keycode = Key.Escape, Pressed = false });
        Check("Esc closes Share profile even with the search box focused before, and the search keeps its text",
            Dialog() == null && search?.Text == "zzz" && search.GetViewport().GuiGetFocusOwner() != search, search?.Text ?? "no search box");
        Search.Clear(content);
        Profiles.Discard(screen);
        await Wait(menu, 0.3);

        Check("profile sharing leaves the profiles and the mod list as they were", Profiles.Active?.Name == startName && !Profiles.Edited
            && Store.Data.Profiles.Count == profiles && ModListState().SequenceEqual(startStates));
    }

    // New mods and profiles (Jordan, 2026-10-13). The fixture's Default profile doesn't list the test mods, which
    // are on (the game's default for a new mod): they joined it at launch, on, quietly (no unsaved edit, and so
    // already in the state the session started from, which Changes and Revert all compare with).
    static void TestNewModsAtLaunch()
    {
        string[] ids = ["SmmTestLib", "SmmTestUi", "SmmTestContent"];
        if (Profiles.Active is not { } profile || ids.Any(id => Dependencies.Find(id) == null))
        {
            Results.Add("SKIP new mods at launch: needs the test mods");
            return;
        }
        Check("mods the active profile had never seen joined it at launch, on", ids.All(id => profile.Mods.Any(m => m.Id == id && m.Enabled)),
            string.Join(", ", profile.Mods.Select(m => $"{m.Id}={(m.Enabled ? "on" : "off")}")));
        var (on, off) = Profiles.UnsavedChanges();
        Check("quietly: no unsaved edit, and already so in the launch state", !Profiles.Edited && ids.All(id => OnInSnapshot(History.Loaded, id)),
            $"edited {Profiles.Edited} (+{string.Join(", +", on.Select(m => m.Name))} -{string.Join(", -", off.Select(m => m.Name))}), in the launch state {ids.Count(id => OnInSnapshot(History.Loaded, id))} of {ids.Length}");
    }

    // Switching to a profile that has never seen a mod (here: two test mods taken out of the other profile, as if
    // installed since it was last used) leaves the mod as it is, on or off, and that profile keeps it that way from
    // then on. The profile left keeps its unsaved edit, which is then discarded.
    static async Task TestNewModsOnSwitch(Node menu, NModdingScreen screen, Control content)
    {
        if (Profiles.Active is not { } start || Store.Data.Profiles.FirstOrDefault(p => p != start) is not { } other
            || Dependencies.Find("SmmTestUi") == null
            || content.GetChildren().OfType<NModMenuRow>().FirstOrDefault(r => r.Mod?.manifest?.id == "SmmTestContent")?.GetNode<NTickbox>("Tickbox") is not { } tickbox)
        {
            Results.Add("SKIP new mods on switch: needs two profiles and the test mods");
            return;
        }
        bool On(string id) => !(SaveManager.Instance.SettingsSave.ModSettings?.IsModDisabled(id, ModSource.ModsDirectory) ?? false);
        bool Saved(Profile profile, string id, bool on) => profile.Mods.Any(m => m.Id == id && m.Enabled == on);
        string[] unseen = ["SmmTestUi", "SmmTestContent"];
        if (Profiles.Edited)
            Profiles.Save(); // start clean
        tickbox.IsTicked = false; // Test Content off (an unsaved edit), Test UI on
        tickbox.EmitSignal(NTickbox.SignalName.Toggled, tickbox);
        await Wait(menu, 0.3);
        other.Mods.RemoveAll(m => unseen.Contains(m.Id));
        other.Edited?.RemoveAll(m => unseen.Contains(m.Id));

        Profiles.SwitchTo(other, screen);
        await Wait(menu, 0.3);
        Check("switching to a profile that never saw a mod leaves it as it is, on or off", On("SmmTestUi") && !On("SmmTestContent"),
            $"Test UI on {On("SmmTestUi")}, Test Content on {On("SmmTestContent")}");
        Check("that profile keeps them that way from then on, with no unsaved edit",
            Saved(other, "SmmTestUi", true) && Saved(other, "SmmTestContent", false) && !Profiles.Edited,
            $"{string.Join(", ", other.Mods.Where(m => unseen.Contains(m.Id)).Select(m => $"{m.Id}={(m.Enabled ? "on" : "off")}"))}, edited {Profiles.Edited}");
        Profiles.SwitchTo(start, screen);
        await Wait(menu, 0.3);
        Check("back: the profile left still has its unsaved edit", Profiles.Edited && !On("SmmTestContent") && Saved(start, "SmmTestContent", true));
        Profiles.Discard(screen);
        await Wait(menu, 0.3);
    }

    // The fixture gives SpireModManager an alias and notes; SmmTestFixes gets them through the editor.
    static async Task TestNotes(Node menu, NModdingScreen screen, Control content, string outDir)
    {
        var panel = screen.GetNode<NModInfoContainer>("%ModInfoContainer");
        NModMenuRow? Row(string id) => content.GetChildren().OfType<NModMenuRow>().FirstOrDefault(r => r.Mod?.manifest?.id == id);
        string RowTitle(string id) => Row(id)?.GetNode<RichTextLabel>("Title").Text ?? "";

        if (Row(ModEntry.ModId) is not { } ours || Row("SmmTestFixes") is not { } other)
        {
            Results.Add("SKIP notes: fixture mods missing");
            return;
        }
        Check("row shows alias beside the name, and the notepad mark", RowTitle(ModEntry.ModId).StartsWith("The Manager")
            && RowTitle(ModEntry.ModId).Contains("Spire Mod Manager") && ours.GetNodeOrNull<Control>("SpireModManagerEditedMark") is { Visible: true },
            RowTitle(ModEntry.ModId));
        Check("no alias pencil on rows", !ours.HasNode("SpireModManagerPencil"));
        CategoryList.Select(screen, ours);
        await Wait(menu, 0.5);
        Check("row state reads the game's: the selected row is selected, another not", RowState.Selected(ours) && !RowState.Selected(other));
        var description = panel.GetNode<RichTextLabel>("ModDescription").GetParsedText();
        var yours = panel.GetNode<Control>("SpireModManagerYours");
        LineEdit Line(string field) => yours.GetNode<LineEdit>($"{field}/Box");
        var notes = yours.GetNode<TextEdit>("Notes/Box");
        var titleText = panel.GetNode<RichTextLabel>("ModTitle").GetParsedText();
        Check("panel titles with the mod's own name, the version beside it (no v); the alias is in its field",
            titleText.StartsWith("Spire Mod Manager") && Dependencies.Find(ModEntry.ModId)?.manifest?.version is { } version && titleText.TrimEnd().EndsWith(" " + version) && !titleText.Contains("v" + version)
            && Line("Alias").Text == "The Manager" && notes.Text == "Fixture note");
        var linkText = (panel.FindChild("SpireModManagerLink", true, false) as LinkButton)?.Text ?? "";
        Check("the source line: id (Local), then Enabled", panel.FindChild("SpireModManagerLink", true, false) is LinkButton { Disabled: true }
            && linkText == "SpireModManager (Local)" && (panel.FindChild("SpireModManagerSource", true, false)?.FindChild("Status", true, false) as Label)?.Text == "Enabled", linkText);
        var headText = panel.GetNodeOrNull<RichTextLabel>("SpireModManagerHead")?.GetParsedText() ?? "";
        Check("the head: author and compatibility; the details: description, then size", headText.Contains("Author: symphony") && headText.Contains("Compatibility:")
            && !description.StartsWith("Enabled") && description.Contains("Size:") && !description.Contains("Compatibility"), $"{headText} / {description[..Math.Min(60, description.Length)]}");
        Check("fields in order: Alias, Category, Tags, Notes", string.Join(",", yours.GetChildren().Select(c => c.Name.ToString())) == "Alias,Category,Tags,Notes",
            string.Join(",", yours.GetChildren().Select(c => c.Name.ToString())));
        Check("the thumbnail is 256 x 144 (the scene's minimum size lifted)", panel.GetNode<Control>("ModImage").Size == new Vector2(256, 144), $"{panel.GetNode<Control>("ModImage").Size}");
        Check("automatic tags beside the Tags term", yours.GetNodeOrNull<Control>("Tags/Term/SystemTags") is { Visible: true } system && system.GetChildCount() > 0);
        var image = panel.GetNode<TextureRect>("ModImage");
        var description0 = panel.GetNode<Control>("ModDescription").OffsetTop;
        image.Texture = ImageTexture.CreateFromImage(Image.CreateEmpty(16, 9, false, Image.Format.Rgba8));
        Layout.PlaceInfo(panel);
        var withPicture = panel.GetNode<Control>("ModDescription").OffsetTop;
        image.Texture = ImageTexture.CreateFromImage(Image.CreateEmpty(10, 10, false, Image.Format.Rgba8));
        Layout.PlaceInfo(panel);
        Check("a square picture: full height, against the right edge", image.Size == new Vector2(ModDetails.ThumbHeight, ModDetails.ThumbHeight)
            && Math.Abs(image.Position.X + image.Size.X - (panel.Size.X - ModDetails.ThumbRight)) < 1, $"{image.Position} {image.Size} in {panel.Size}");
        // Hovering it for a fifth of a second: the whole picture, at most 800 on its longer side.
        image.Texture = ImageTexture.CreateFromImage(Image.CreateEmpty(1600, 900, false, Image.Format.Rgba8));
        Hover.TestMouse = () => image.GetGlobalRect().GetCenter();
        image.EmitSignal(Control.SignalName.MouseEntered);
        await Wait(menu, Hover.Delay + 0.15);
        var big = screen.GetNodeOrNull("SpireModManagerPictureCard")?.FindChildren("*", "TextureRect", true, false).OfType<TextureRect>().FirstOrDefault();
        Check("hovering the thumbnail shows the whole picture, at most 800 wide", big != null && big.Size.X == 800 && big.Size.Y == 450, $"{big?.Size}");
        Hover.TestMouse = () => new Vector2(5, 5);
        await Wait(menu, 0.1);
        Hover.TestMouse = null;
        image.Texture = null;
        Layout.PlaceInfo(panel);
        Check("no picture: the description starts under the head; with one, under the picture", description0 < 160 && withPicture >= 16 + ModDetails.ThumbHeight,
            $"{description0} / {withPicture}");
        await Capture(screen, outDir, "8-details-with-notes");

        // Notes and tags typed in the panel save when focus leaves; notes are trimmed, tags tidied.
        CategoryList.Select(screen, other);
        await Wait(menu, 0.5);
        notes.Text = "  Typed in the panel \n";
        notes.EmitSignal(Control.SignalName.FocusExited);
        var tags = Line("Tags");
        tags.Text = "co-op, Favorites, co-op";
        tags.EmitSignal(Control.SignalName.FocusExited);
        await Wait(menu, 0.3);
        Check("panel saves notes, trimmed", Store.InfoFor("SmmTestFixes")?.Notes == "Typed in the panel" && notes.Text == "Typed in the panel", notes.Text);
        Check("panel saves tags, repeats dropped; at rest they're pills", Store.InfoFor("SmmTestFixes")?.Tags?.SequenceEqual(["co-op", "Favorites"]) == true
            && yours.GetNode<Control>("Tags/CustomTags") is { Visible: true } && yours.GetNode("Tags/CustomTags/Pills").GetChildCount() == 2,
            string.Join(", ", Store.InfoFor("SmmTestFixes")?.Tags ?? []));
        await Capture(screen, outDir, "9-notes-editing");

        // Fields sit right under the details; Cancel puts the text back; the alias field shows on the row.
        var details = panel.GetNode<Control>("ModDescription");
        Check("the fields sit right below the details", Math.Abs(yours.OffsetTop - details.OffsetBottom - ModDetails.FieldsGap) < 1,
            $"{details.OffsetBottom} -> {yours.OffsetTop}..{yours.OffsetBottom}");
        notes.GrabFocus();
        await Wait(menu, 0.1);
        Check("editing shows Save and Cancel, and the ✕", yours.GetNode<Control>("Notes/Term/Buttons").Visible && yours.GetNode<Control>("Notes/Box/Clear").Visible);
        notes.Text = "Not wanted";
        yours.GetNode("Notes/Term/Buttons/Cancel").EmitSignal(BaseButton.SignalName.Pressed);
        await Wait(menu, 0.2);
        Check("Cancel puts the notes back", notes.Text == "Typed in the panel" && Store.InfoFor("SmmTestFixes")?.Notes == "Typed in the panel", notes.Text);
        var alias = Line("Alias");
        alias.Text = "Fixes";
        alias.EmitSignal(Control.SignalName.FocusExited);
        await Wait(menu, 0.4);
        Check("the alias field saves, the row follows", Store.InfoFor("SmmTestFixes")?.Alias == "Fixes" && RowTitle("SmmTestFixes").StartsWith("Fixes"));

        // Hover cards (Hover's rules): the title's after the delay (id and state first); a quick wobble off it
        // keeps the card (wind-down); after a card goes, the next shows at once for the grace period; the
        // notepad's shows and goes with the mouse, with no grace after it; one card at a time.
        bool CardShown() => screen.HasNode("SpireModManagerNotesCard");
        int Cards() => screen.GetChildren().OfType<CanvasLayer>().Count(l => l.Name.ToString().StartsWith("SpireModManager") && l.Name.ToString().EndsWith("Card"));
        string CardText() => screen.GetNodeOrNull("SpireModManagerNotesCard")?.FindChildren("*", "RichTextLabel", true, false).OfType<RichTextLabel>().FirstOrDefault()?.GetParsedText() ?? "";
        async Task HoverOver(Control target, double seconds)
        {
            Hover.TestMouse = () => target.GetGlobalRect().GetCenter();
            target.EmitSignal(Control.SignalName.MouseEntered);
            await Wait(menu, seconds);
        }
        async Task Leave(Control target, double seconds)
        {
            Hover.TestMouse = () => new Vector2(5, 5);
            target.EmitSignal(Control.SignalName.MouseExited);
            await Wait(menu, seconds);
        }
        var hot = other.GetNode<Control>("SpireModManagerTitleHover");
        var mark = other.GetNode<Control>("SpireModManagerEditedMark");
        var oursHot = ours.GetNode<Control>("SpireModManagerTitleHover");
        Check("the notepad sits left of the name, in the room before the title", mark.Position.X + mark.Size.X <= other.GetNode<Control>("Title").Position.X && mark.Position.X >= 0,
            $"mark at {mark.Position.X}, title text {other.GetNode<Control>("Title").Position.X}..+{hot.Size.X}");
        await HoverOver(hot, Hover.Delay - 0.15);
        Check("the title's card waits for the delay", !CardShown());
        await Wait(menu, 0.3);
        Check("after the delay, the title's card: id and state first", CardShown() && CardText().StartsWith("SmmTestFixes") && CardText().Contains("Enabled"), CardText());
        await Capture(screen, outDir, "9b-notes-hover");
        Hover.TestMouse = () => new Vector2(5, 5);
        await menu.ToSignal(menu.GetTree(), SceneTree.SignalName.ProcessFrame);
        Hover.TestMouse = () => hot.GetGlobalRect().GetCenter();
        await Wait(menu, 0.1);
        Check("a one-frame wobble off the title keeps the card (wind-down)", CardShown());
        await Leave(hot, 0.1);
        Check("the card goes once the mouse is off the title", !CardShown());
        await HoverOver(oursHot, 0.05);
        Check("within the grace period, the next title's card shows at once", CardShown() && CardText().StartsWith(ModEntry.ModId), CardText());
        await HoverOver(mark, 0.05);
        Check("the notepad's card at once, and only one card at a time", CardShown() && CardText().StartsWith("SmmTestFixes") && Cards() == 1, $"{Cards()} cards");
        await Leave(mark, 0.05);
        Check("the notepad's card goes with the mouse", !CardShown());
        await HoverOver(hot, 0.2);
        Check("no grace period after the notepad: the title waits again", !CardShown());
        await Leave(hot, 0.3);
        Hover.TestMouse = null;
        Check("Vanilla View shows plain names", await WithVanillaView(screen, menu, () => RowTitle(ModEntry.ModId) == "Spire Mod Manager"));
    }

    // A mod installed while the page is open (here: the game's own handler called again for a fixture mod):
    // its row comes out in our style, in its place. The extra row goes again afterwards.
    static async Task TestNewMod(Node menu, NModdingScreen screen, Control content, string outDir)
    {
        var before = content.GetChildren().OfType<NModMenuRow>().ToList();
        if (Dependencies.Find("SmmTestUi") is not { } mod)
            return;
        typeof(NModdingScreen).GetMethod("OnNewModDetected", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.Invoke(screen, [mod]);
        await Wait(menu, 0.4);
        var added = content.GetChildren().OfType<NModMenuRow>().Except(before).FirstOrDefault();
        Check("a mod installed while the page is open gets our row style", added != null && added.HasNode("SpireModManagerStrip")
            && added.CustomMinimumSize.Y == RowTitles.RowHeight, added == null ? "no new row" : $"strip {added.HasNode("SpireModManagerStrip")}, height {added.CustomMinimumSize.Y}");
        // The game's popup for it: our wording, and only one for two mods in quick succession.
        var detected = typeof(NGame).GetMethod("OnNewModDetected", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        detected?.Invoke(NGame.Instance, [mod]);
        await Wait(menu, 0.3);
        var texts = PopupTexts();
        Check("new mod popup says \"New Mod Added\"", texts.Any(t => t.Contains("New Mod Added")), string.Join(" | ", texts.Where(t => t.Length > 0)));
        Check("new mod popup speaks of a relaunch, not a restart", texts.Any(t => t.Contains("relaunch")) && !texts.Any(t => t.Contains("restart")),
            string.Join(" | ", texts.Where(t => t.Length > 0)));
        CheckMark("new mod popup", NModalContainer.Instance?.OpenModal as Control);
        await Capture(screen, outDir, "10b-new-mod-popup");
        NModalContainer.Instance?.Clear();
        detected?.Invoke(NGame.Instance, [mod]);
        await Wait(menu, 0.3);
        Check("new mod popup: our text, at most one per 30 s", NModalContainer.Instance?.OpenModal == null);
        Check("the profile bar shows how many mods are on", screen.FindChild("ProfileCount", true, false) is Label { Text: var count } && count.EndsWith("mods"));

        // A mod installed while the game runs (made up here, the way the game announces one): Changes lists it
        // as installed, with whether it loads after a relaunch; unticking it sticks (the game's tickbox alone
        // changed nothing for a mod with no saved entry).
        var fresh = new Mod
        {
            path = "", modSource = ModSource.ModsDirectory, state = ModLoadState.AddedAtRuntime,
            manifest = new ModManifest { id = "SmmFreshMod", name = "Fresh Mod" },
        };
        var rowsBefore = content.GetChildren().OfType<NModMenuRow>().ToList();
        var wasEdited = Profiles.Edited;
        GameInstalls(fresh);
        await Wait(menu, 0.4);
        NModalContainer.Instance?.Clear();
        Check("a mod installed now is listed as installed, loading after relaunch",
            HistoryBar.Describe().Any(l => l.Contains("Installed: Fresh Mod") && l.Contains("loads after relaunch")), string.Join(" | ", HistoryBar.Describe()));
        // It joins the active profile at once, on, quietly: no unsaved edit, and Revert all's state has it too.
        Check("a mod installed now joins the active profile, on, quietly",
            Profiles.Active?.Mods.Any(m => m.Id == "SmmFreshMod" && m.Enabled) == true && Profiles.Edited == wasEdited && OnInSnapshot(History.Entered, "SmmFreshMod"),
            $"in the profile {Profiles.Active?.Mods.Any(m => m.Id == "SmmFreshMod")}, edited {wasEdited} -> {Profiles.Edited}, in Revert all's state {OnInSnapshot(History.Entered, "SmmFreshMod")}");
        Check("a mod installed now gets a New tag, first", Tags.For(fresh).FirstOrDefault()?.Text == "New");
        var freshRow = content.GetChildren().OfType<NModMenuRow>().Except(rowsBefore).FirstOrDefault(r => r.Mod == fresh);
        if (freshRow != null)
        {
            var box = freshRow.GetNode<NTickbox>("Tickbox");
            box.IsTicked = false;
            box.EmitSignal(NTickbox.SignalName.Toggled, box);
            await Wait(menu, 0.3);
            Check("unticking a new mod is saved, and Changes says it's disabled",
                SaveManager.Instance.SettingsSave.ModSettings?.IsModDisabled(fresh) == true
                && HistoryBar.Describe().Any(l => l.Contains("Fresh Mod") && l.Contains("Disabled")), string.Join(" | ", HistoryBar.Describe()));
            Check("unticking it is an unsaved edit of the profile", Profiles.Edited && Profiles.UnsavedChanges().Off.Any(m => m.Name == "Fresh Mod"),
                $"edited {Profiles.Edited}, off: {string.Join(", ", Profiles.UnsavedChanges().Off.Select(m => m.Name))}");
            HistoryBar.ShowList(screen);
            await Wait(menu, 0.2);
            await Capture(screen, outDir, "10c-installed-change");
            HistoryBar.CloseList(screen.GetNode<Control>("%ModInfoContainer"));
            // Undone: before the untick the game had no entry for it (on), and the profile had it on.
            History.Undo();
            await Wait(menu, 0.3);
            Check("undoing the untick: the new mod on again, the profile as it was", SaveManager.Instance.SettingsSave.ModSettings?.IsModDisabled(fresh) == false
                && Profiles.Edited == wasEdited, $"disabled {SaveManager.Instance.SettingsSave.ModSettings?.IsModDisabled(fresh)}, edited {wasEdited} -> {Profiles.Edited}");
            content.RemoveChild(freshRow);
            freshRow.QueueFree();
        }
        else
            Results.Add("FAIL a mod installed now gets a row");
        GameForgets(fresh);
        SaveManager.Instance.SettingsSave.ModSettings?.ModList.RemoveAll(e => e.Id == "SmmFreshMod");
        Profiles.Track(); // its unsaved edit goes with it
        History.Record();
        CategoryList.Redraw(content); // the pending note too
        if (added != null)
        {
            content.RemoveChild(added);
            added.QueueFree();
            CategoryList.Apply(content);
        }
    }

    // By name, by alias (fixture: "The Manager"), no match, then cleared.
    static async Task TestSearch(Node menu, NModdingScreen screen, Control content, string outDir)
    {
        var box = screen.GetNode<LineEdit>("ModsBorder/SpireModManagerSearch");
        async Task<List<string>> Shown(string text)
        {
            box.Text = text;
            box.EmitSignal(LineEdit.SignalName.TextChanged, text);
            await Wait(menu, 0.3);
            return content.GetChildren().OfType<NModMenuRow>().Where(r => r.Visible).Select(r => r.Mod?.manifest?.id ?? "").ToList();
        }

        var byName = await Shown("test fixes");
        Check("search by name", byName.SequenceEqual(["SmmTestFixes"]), string.Join(", ", byName));
        await Capture(screen, outDir, "10-search");
        var byAlias = await Shown("the manager");
        Check("search by alias", byAlias.SequenceEqual([ModEntry.ModId]), string.Join(", ", byAlias));
        var byTag = await Shown("favorites");
        Check("search by own tag", byTag.SequenceEqual(["SmmTestFixes"]), string.Join(", ", byTag));
        var libraries = await Shown("library");
        Check("search by automatic tag", libraries.SequenceEqual(["SmmTestLib"]), string.Join(", ", libraries));
        Check("automatic tags, in order", Tags.For(Dependencies.Find("SmmTestLib")).Select(c => c.Text).SequenceEqual(["Local", "Library", "Gameplay"])
            && Tags.For(Dependencies.Find("SmmTestUi")).Select(c => c.Text).SequenceEqual(["Local"]),
            string.Join(", ", Tags.For(Dependencies.Find("SmmTestLib")).Select(c => c.Text)));
        // Tags clicked on a row: "#Library" alone; Shift adds "#Gameplay" (both needed); "#Local" matches only the tag.
        List<string> Visible() => content.GetChildren().OfType<NModMenuRow>().Where(r => r.Visible).Select(r => r.Mod?.manifest?.id ?? "").ToList();
        void ClickChip(string modId, string tag, bool shift)
        {
            var chip = content.GetChildren().OfType<NModMenuRow>().FirstOrDefault(r => r.Mod?.manifest?.id == modId)?
                .GetNodeOrNull("SpireModManagerStrip")?.GetChildren().OfType<PanelContainer>().FirstOrDefault(c => c.GetChildOrNull<Label>(0)?.Text == tag);
            chip?.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, ShiftPressed = shift });
        }
        var all = await Shown("");
        ClickChip("SmmTestLib", "Library", false);
        await Wait(menu, 0.3);
        Check("clicking a row's tag searches for it", box.Text == "#Library" && Visible().SequenceEqual(["SmmTestLib"]), $"{box.Text}: {string.Join(", ", Visible())}");
        ClickChip("SmmTestLib", "Gameplay", true);
        await Wait(menu, 0.3);
        Check("Shift-clicking another adds it", box.Text == "#Library #Gameplay" && Visible().SequenceEqual(["SmmTestLib"]), $"{box.Text}: {string.Join(", ", Visible())}");
        // Clicking again undoes: Shift takes "#Gameplay" back out, then a plain click on the whole search clears it.
        ClickChip("SmmTestLib", "Gameplay", true);
        await Wait(menu, 0.3);
        Check("Shift-clicking a searched tag again removes it", box.Text == "#Library", box.Text);
        ClickChip("SmmTestLib", "Library", false);
        await Wait(menu, 0.3);
        Check("clicking the tag that is the whole search clears it", box.Text == "" && Visible().SequenceEqual(all), $"{box.Text}: {Visible().Count} shown");
        // Authors: from the info panel's author (the row's is plain text); Shift on a tag adds it after a space.
        await Shown("");
        var rowAuthor = content.GetChildren().OfType<NModMenuRow>().FirstOrDefault(r => r.Mod?.manifest?.id == "SmmTestFixes")?.GetNodeOrNull<Label>("SpireModManagerStrip/Author");
        Check("the row's author is plain: just the name, not clickable", rowAuthor is { Text: "symphony", MouseFilter: Control.MouseFilterEnum.Ignore });
        var panelHead = screen.GetNode<Control>("%ModInfoContainer").GetNodeOrNull<RichTextLabel>("SpireModManagerHead");
        panelHead?.EmitSignal(RichTextLabel.SignalName.MetaClicked, "author:symphony");
        await Wait(menu, 0.3);
        Check("the panel's author searches for their mods", box.Text == "@symphony"
            && Visible().Count > 0 && Visible().All(id => Dependencies.Find(id)?.manifest?.author == "symphony"), $"{box.Text}: {string.Join(", ", Visible())}");
        ClickChip("SmmTestFixes", "Local", true);
        await Wait(menu, 0.3);
        Check("Shift-click on a tag adds it after a space", box.Text == "@symphony #Local", box.Text);
        // The rules every click-to-search shares (row and panel tags, the author, Workshop tags), through the one
        // helper they all use: the search before, the click, the search after.
        string After(string before, Action click)
        {
            box.Text = before;
            click();
            return box.Text;
        }
        void Tag(string tag, bool shift) => SpireModManager.Search.ByTag(content, tag, shift);
        void Words(string text, bool shift) => SpireModManager.Search.ByText(content, text, shift);
        string[] clears = [After(" #library ", () => Tag("Library", false)), After("#\"Library\"", () => Tag("Library", false)),
            After("tools  &  apis", () => Words("Tools & APIs", false))];
        Check("click-to-search: a click on the whole search clears it (any case, quotes aside)", clears.All(s => s == ""), string.Join(" | ", clears));
        string[] replaces = [After("mod fixes", () => Tag("Library", false)), After("#Library #Gameplay", () => Tag("Library", false))];
        Check("click-to-search: a click replaces any other search", replaces.All(s => s == "#Library"), string.Join(" | ", replaces));
        string[] adds = [After("@symphony", () => Tag("Local", true)), After("mod", () => Words("Tools & APIs", true))];
        Check("click-to-search: Shift-click adds the term, keeping the rest", adds.SequenceEqual(["@symphony #Local", "mod Tools & APIs"]), string.Join(" | ", adds));
        string[] removes = [After("@symphony #local mod", () => Tag("Local", true)), After("#Library Tools & APIs", () => Words("tools & apis", true))];
        Check("click-to-search: Shift-click removes a term already searched, any case", removes.SequenceEqual(["@symphony mod", "#Library"]), string.Join(" | ", removes));
        string[] quoted = [After("#\"two words\" @symphony", () => Tag("Two Words", true)),
            After("mod @\"Some One\"", () => SpireModManager.Search.ByAuthor(content, "some one", true))];
        Check("click-to-search: Shift-click removes quoted terms too", quoted.SequenceEqual(["@symphony", "mod"]), string.Join(" | ", quoted));
        string[] tidy = [After("  #Library   mod  ", () => Tag("Library", true)), After("mod   ", () => Tag("Gameplay", true))];
        Check("click-to-search: spaces tidied, one between terms", tidy.SequenceEqual(["mod", "mod #Gameplay"]), string.Join(" | ", tidy));
        string[] glued = [After("foo#Library", () => Tag("Library", true)), After("C# #Library", () => Tag("Library", true))];
        Check("click-to-search: a #tag glued to a word is a tag, a lone # stays plain", glued.SequenceEqual(["foo", "C#"]), string.Join(" | ", glued));
        var local = await Shown("#local");
        Check("#tag matches the tag only, any case", local.Count > 1 && local.All(id => Tags.For(Dependencies.Find(id)).Any(c => c.Text == "Local")), string.Join(", ", local));
        var none = await Shown("zzz no such mod");
        Check("no match hides every row and header", none.Count == 0
            && !content.GetChildren().OfType<Control>().Any(c => c.IsInGroup("SpireModManagerCategoryHeader") && c.Visible));
        var cleared = await Shown("");
        Check("clearing restores the list", cleared.Count > 0);
    }

    // A redraw answers its questions once (Lookups) instead of once per row: the answers must be the same as asked
    // one at a time outside a redraw, for every mod shown. Then how long a redraw and a search keystroke take here
    // (about 50 mods): reported, with a generous bound. TestManyMods and the --real tour time longer lists.
    static async Task TestRedrawCost(Node menu, NModdingScreen screen, Control content)
    {
        var rows = content.GetChildren().OfType<NModMenuRow>().ToList();
        var mods = rows.Select(r => r.Mod).OfType<Mod>().ToList();
        var order = LoadOrder.Mods(rows).Select((m, i) => (m, i)).DistinctBy(p => p.m).ToDictionary(p => p.m, p => p.i);
        string[] queries = ["lib", "test fixes", "the manager", "co-op", "#Library", "#local", "@symphony", "#Gameplay lib", "test framework", "zzz"];
        static string Key(Mod? mod) => mod == null ? "none" : $"{mod.manifest?.id}/{mod.modSource}";
        List<string> Answers() =>
        [
            .. mods.Select(mod => $"{Key(mod)}: on {Dependencies.IsOn(mod)}, copy {Key(Dependencies.Find(mod.manifest?.id ?? ""))}, "
                + $"needed by {string.Join(",", Dependencies.NeededBy(mod.manifest?.id).Select(Key))}, "
                + $"needs {string.Join(",", Dependencies.Of(mod).Select(id => Key(Dependencies.Find(id))))}, "
                + $"tags {string.Join(",", Tags.For(mod).Select(c => $"{c.Text}/{c.Color}/{c.Custom}/{c.Tip}"))}, "
                + $"notes {RowStrip.RequiredFor(mod)} | {RowStrip.Needs(mod, order, false)} | {RowStrip.Needs(mod, order, true)}, "
                + $"matches {string.Concat(queries.Select(q => Search.Matches(mod, q) ? "y" : "n"))}"),
            $"order problems {string.Join(",", Dependencies.OrderViolations(LoadOrder.Mods(rows)).Select(v => $"{Key(v.Mod)}>{Key(v.Needs)}"))}",
            $"needed and off {string.Join(",", Dependencies.NeededOff(mods).Select(Key))}",
            $"words {Menus.Text("SPIRE_MOD_MANAGER.TAG_LIBRARY")} {Menus.Text("SPIRE_MOD_MANAGER.NEEDS_LABEL")}",
        ];
        var fresh = Answers();
        List<string> kept;
        using (Lookups.Begin())
            kept = Answers();
        var differ = fresh.Zip(kept).Where(p => p.First != p.Second).Select(p => $"{p.First} -> {p.Second}").ToList();
        Check("inside a redraw, the same answers as one at a time (on, copies, needs, tags, notes, search)",
            differ.Count == 0 && fresh.Count == kept.Count && Lookups.Current == null, string.Join(" | ", differ.Take(3)));

        var box = screen.GetNode<LineEdit>("ModsBorder/SpireModManagerSearch");
        void Type(string text)
        {
            box.Text = text;
            box.EmitSignal(LineEdit.SignalName.TextChanged, text);
        }
        static double Ms(Action action)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            for (var i = 0; i < 10; i++)
                action();
            return watch.Elapsed.TotalMilliseconds / 10;
        }
        var listMs = Ms(() => CategoryList.Apply(content));
        var keyMs = Ms(() =>
        {
            Type("lib"); // every row matched
            Type(""); // and the whole list back
        }) / 2;
        await Wait(menu, 0.3);
        Click(screen.GetNode<NButton>("SpireModManagerLoadOrderToggle"));
        await Wait(menu, 0.5);
        var orderMs = Ms(() => CategoryList.Apply(content));
        Click(screen.GetNode<NButton>("SpireModManagerLoadOrderToggle"));
        await Wait(menu, 0.5);
        Results.Add($"INFO redraw with {rows.Count} mods: main list {listMs:0.0} ms, a search keystroke {keyMs:0.0} ms, Load Order {orderMs:0.0} ms");
        Check("a redraw and a search keystroke stay quick (under 100 ms here)", Math.Max(listMs, Math.Max(keyMs, orderMs)) < 100,
            $"{listMs:0.0} / {keyMs:0.0} / {orderMs:0.0} ms");
    }

    // Three hundred made-up mods (Jordan has about 250), no screen: what a redraw asks about every row (tags, "Needs",
    // "Required for", searches) and the page's warnings, answered as a redraw does (Lookups) and timed, then each
    // answer checked against the slow way (the saved list searched every time). Every seventh mod is off, every
    // tenth is a library the next ones need, every 25th also needs one that isn't installed, and one library has
    // a second copy that is on while the first is off.
    static void TestManyMods()
    {
        var mods = new List<Mod>();
        var saved = new List<SettingsSaveMod>();
        for (var i = 0; i < 300; i++)
        {
            List<ModDependency> needs = [];
            if (i % 10 != 0)
                needs.Add(new ModDependency($"SmmMany{i / 10 * 10:000}"));
            if (i % 3 == 0 && i >= 20)
                needs.Add(new ModDependency($"SmmMany{i / 10 * 10 - 10:000}"));
            if (i % 25 == 0)
                needs.Add(new ModDependency("SmmManyMissing"));
            var mod = new Mod
            {
                path = "", modSource = ModSource.ModsDirectory,
                manifest = new ModManifest { id = $"SmmMany{i:000}", name = $"Many Mods {i}", author = $"Author {i % 12}", dependencies = needs, affectsGameplay = i % 2 == 0 },
            };
            mods.Add(mod);
            saved.Add(new SettingsSaveMod(mod) { IsEnabled = i % 7 != 0 });
        }
        var copy = new Mod { path = "", modSource = ModSource.SteamWorkshop, manifest = new ModManifest { id = "SmmMany050", name = "Many Mods 50 (Workshop)" } };
        mods.Add(copy);
        saved.Add(new SettingsSaveMod(copy));
        saved[50].IsEnabled = false;

        // The slow way.
        bool On(Mod m) => !saved.Any(e => e.Id == m.manifest?.id && e.Source == m.modSource && !e.IsEnabled);
        Mod? Copy(string id) => mods.Where(m => m.manifest?.id == id).OrderByDescending(On).FirstOrDefault();
        int Needing(Mod mod, bool onOnly) => mods.Count(m => m != mod && (!onOnly || On(m)) && Dependencies.Of(m).Contains(mod.manifest?.id));

        var order = mods.Select((m, i) => (m, i)).ToDictionary(p => p.m, p => p.i);
        string[] queries = ["many 12", "#Library", "@\"Author 3\"", "author 3 many", "zzz"];
        var watch = System.Diagnostics.Stopwatch.StartNew();
        double Lap()
        {
            var ms = watch.Elapsed.TotalMilliseconds;
            watch.Restart();
            return ms;
        }
        var wrong = new List<string>();
        using (Lookups.Begin(mods, [], saved))
        {
            var built = Lap();
            var notes = mods.ToDictionary(m => m, m => (Tags: Tags.For(m), Required: RowStrip.RequiredFor(m), Needs: RowStrip.Needs(m, order, loadOrder: false)));
            var strips = Lap();
            // A search redraw asks about each row about four times (its category's matches, count and rows, the page bar).
            foreach (var query in queries)
                for (var pass = 0; pass < 4; pass++)
                    foreach (var mod in mods)
                        Search.Matches(mod, query);
            var searches = Lap();
            var problems = Dependencies.OrderViolations(mods).Count + Dependencies.NeededOff(mods).Count;
            var warnings = Lap();
            Results.Add($"INFO 300 made-up mods (and a second copy of one): lookups {built:0.0} ms, tags and notes {strips:0.0} ms, "
                + $"{queries.Length} searches {searches:0.0} ms, warnings {warnings:0.0} ms ({problems} problems)");
            Check("300 made-up mods: every row's questions and five searches stay quick (under 100 ms)", built + strips + searches + warnings < 100,
                $"{built + strips + searches + warnings:0.0} ms");

            string Times(string text, string word) => (text.Split(word).Length - 1).ToString();
            foreach (var mod in mods)
            {
                var id = mod.manifest?.id ?? "";
                var (tags, required, needs) = notes[mod];
                var deps = Dependencies.Of(mod);
                var expected = $"on {On(mod)}, copy {Copy(id)?.modSource}, library {Needing(mod, false) > 0}, required for {Needing(mod, true)}, "
                    + $"missing {(On(mod) ? deps.Count(d => Copy(d) == null) : 0)}, off {(On(mod) ? deps.Count(d => Copy(d) is { } dep && !On(dep)) : 0)}";
                var answered = $"on {Dependencies.IsOn(mod)}, copy {Dependencies.Find(id)?.modSource}, library {tags.Any(c => c.Text == "Library")}, "
                    + $"required for {(System.Text.RegularExpressions.Regex.Match(required, @"Required for (\d+)") is { Success: true } match ? match.Groups[1].Value : "0")}, "
                    + $"missing {Times(needs, "(not installed)")}, off {Times(needs, "(off)")}";
                if (answered != expected)
                    wrong.Add($"{id}: {answered}; the slow way: {expected}");
            }
        }
        Check("300 made-up mods: the same answers as the slow way (on, copies, libraries, required for, missing, off)",
            wrong.Count == 0 && Lookups.Current == null, $"{wrong.Count} differ: {string.Join(" | ", wrong.Take(3))}");
    }

    // Load Order view: rows in the saved order with handles; move by menu action and by dragging a handle
    // (real mouse events into its input handler); restore; back to categories.
    static async Task TestLoadOrder(Node menu, NModdingScreen screen, Control content, string outDir)
    {
        List<NModMenuRow> Rows() => content.GetChildren().OfType<NModMenuRow>().ToList();
        List<string> Ids() => Rows().Select(r => r.Mod?.manifest?.id ?? "").ToList();
        List<string> Saved() => SaveManager.Instance.SettingsSave.ModSettings?.ModList
            .Select(m => m.Id).Where(id => Ids().Contains(id)).ToList() ?? [];
        var pending = screen.GetNode<Control>("%PendingChangesLabel");
        // Auto sort's hint: level with the search box, just left of Auto sort, above the list (so clear of every row).
        void HintPlaced(string when)
        {
            var border = screen.GetNode<Control>("ModsBorder");
            var hint = border.GetNode<Control>(LoadOrder.AutoSortHintName);
            var (at, sort, search, list) = (hint.GetGlobalRect(), border.GetNode<Control>(LoadOrder.AutoSortName).GetGlobalRect(),
                border.GetNode<Control>("SpireModManagerSearch").GetGlobalRect(), border.GetNode<Control>("ModsScrollContainer").GetGlobalRect());
            Check($"{when}: Auto sort's hint level with the search box, left of Auto sort, above the rows", hint.Visible
                && Math.Abs(at.GetCenter().Y - search.GetCenter().Y) < 1 && at.End.X <= sort.Position.X && at.End.Y <= list.Position.Y,
                $"hint {at}, search {search}, Auto sort {sort}, rows from {list.Position.Y}");
        }

        // Straight from the main list, nothing picked (letting go of picked rows redraws on the way, which hid the bug):
        // the hint is in place from the first frame, not where the search box sits on the main list.
        Selection.Clear(content);
        Click(screen.GetNode<NButton>("SpireModManagerLoadOrderToggle"));
        await Wait(menu, 0.5);
        HintPlaced("opening Load Order");
        CategoryList.Redraw(content);
        await Wait(menu, 0.3);
        HintPlaced("Load Order redrawn");
        var start = Ids();
        Check("load order view: saved order, handles, no headers", start.SequenceEqual(Saved())
            && Rows().All(r => r.GetNodeOrNull<Control>("SpireModManagerHandle") is { Visible: true })
            && !content.GetChildren().Any(c => c.IsInGroup("SpireModManagerCategoryHeader")), string.Join(", ", start));
        Check("nothing pending before moving", !pending.Visible && !ModEntry.HasPendingChanges());
        // Leaving and coming back: the rows' drag handlers aren't connected again (one press, one drag).
        int Handlers() => Rows()[0].GetSignalConnectionList(Control.SignalName.GuiInput).Count + Rows()[0].GetSignalConnectionList(Control.SignalName.MouseEntered).Count;
        var wired = Handlers();
        Click(screen.GetNode<NButton>("SpireModManagerLoadOrderToggle"));
        await Wait(menu, 0.3);
        Click(screen.GetNode<NButton>("SpireModManagerLoadOrderToggle"));
        await Wait(menu, 0.5);
        Check("a second visit to Load Order adds no handlers to the rows", Handlers() == wired && Store.Data.LoadOrderView, $"{wired} -> {Handlers()}");

        // Moves use a mod with no dependencies either way, so no order rule can block them.
        const string free = "SmmTestFixes";
        if (Rows().FirstOrDefault(r => r.Mod?.manifest?.id == free)?.Mod is { } freeMod)
            LoadOrder.Move(freeMod, 0, Rows());
        CategoryList.Redraw(content);
        await Wait(menu, 0.5);
        Check("move to top saves the order", Ids()[0] == free && Saved()[0] == free, string.Join(", ", Ids()));
        Check("an order change is pending", pending.Visible && ModEntry.HasPendingChanges());
        Check("load order: no tickboxes", Rows().All(r => !r.GetNode<Control>("Tickbox").Visible));
        // The first row's highlight (the same box under the mouse) reaches a little above the row; with the list at its
        // top it shows whole, under the list's top edge, its rounded top not cut off.
        CategoryList.ScrollToTop(content);
        CategoryList.Select(screen, Rows()[0]);
        await Wait(menu, 0.2);
        var topLook = Rows()[0].GetNode<Control>("SelectionHighlight").GetGlobalRect();
        var listTop = content.GetParent<Control>().GetGlobalRect().Position.Y;
        Check("load order: the first row's highlight shows whole, under the list's top edge", topLook.Position.Y >= listTop - 0.01f
            && topLook.Position.Y < Rows()[0].GetGlobalRect().Position.Y, $"highlight from {topLook.Position.Y}, row from {Rows()[0].GetGlobalRect().Position.Y}, list from {listTop}");
        var wasText = Rows()[0].GetNodeOrNull<Label>("SpireModManagerWas")?.Text;
        Check("a moved row shows where it was at launch", wasText == $"{start.IndexOf(free) + 1} →",
            wasText ?? "none");
        if (Rows().FirstOrDefault(r => r.Mod?.manifest?.id == "SmmTestUi") is { Mod: { } uiMod } uiRow)
        {
            var toggle = LoadOrder.Actions(content, uiRow, () => CategoryList.Redraw(content))[0];
            toggle.OnSelect?.Invoke();
            await Wait(menu, 0.3);
            var offNow = !Dependencies.IsOn(uiMod);
            var again = LoadOrder.Actions(content, uiRow, () => CategoryList.Redraw(content))[0];
            again.OnSelect?.Invoke();
            await Wait(menu, 0.3);
            Check("load order menu: Disable, then Enable", toggle.Label == "Disable" && offNow && again.Label == "Enable" && Dependencies.IsOn(uiMod),
                $"{toggle.Label}, off {offNow}, {again.Label}");
        }

        // Drag the first row's handle to just below the third row.
        var first = Rows()[0];
        var handle = first.GetNode<Control>("SpireModManagerHandle");
        var from = handle.GetGlobalRect().GetCenter();
        var to = new Vector2(from.X, Rows()[2].GetGlobalRect().End.Y - 4);
        void Send(InputEvent e) => handle.GetParent().EmitSignal(Control.SignalName.GuiInput, e);
        Send(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, GlobalPosition = from, Position = from });
        Send(new InputEventMouseMotion { GlobalPosition = to, Position = to });
        await Wait(menu, 0.3);
        await Capture(screen, outDir, "11-load-order-drag");
        Send(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, GlobalPosition = to, Position = to });
        await Wait(menu, 0.5);
        var dragged = Ids();
        Check("drag moves the row below the third", dragged.IndexOf(first.Mod?.manifest?.id ?? "") == 2 && dragged.SequenceEqual(Saved()),
            string.Join(", ", dragged));

        // A press alone already shows the drag (dimmed list, drop line); releasing without moving changes nothing.
        var pressed = Rows()[1];
        var at = pressed.GetGlobalRect().GetCenter();
        var orderBefore = Ids();
        pressed.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, GlobalPosition = at });
        await Wait(menu, 0.1);
        Check("pressing a row starts the drag look at once (the drop line's ends slightly rounded)", Rows().Any(r => r.Modulate.A < 0.5f && r != pressed)
            && content.GetParent().GetChildren().OfType<Panel>().Any(c => c.Size.Y == 5 && c.GetThemeStylebox("panel") is StyleBoxFlat { CornerRadiusTopLeft: > 0 }));
        pressed.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, GlobalPosition = at });
        await Wait(menu, 0.3);
        Check("a click without moving leaves the order alone", Ids().SequenceEqual(orderBefore));

        // Multi-select: pick the first and third rows, drag the first's grip below the last row; both move,
        // in their order.
        var all = Rows();
        Selection.Pick(content, all[0], toggle: false, range: false);
        Selection.Pick(content, all[2], toggle: true, range: false);
        var pickedIds = new[] { all[0], all[2] }.Select(r => r.Mod?.manifest?.id ?? "").ToList();
        Check("Ctrl-click picks a second row, both highlighted", Selection.Mods.Count == 2
            && Rows().Count(r => r.GetNodeOrNull<Control>("SpireModManagerPicked")?.Visible == true) == 2);
        var grip = all[0].GetNode<Control>("SpireModManagerHandle");
        var gripAt = grip.GetGlobalRect().GetCenter();
        var bottom = new Vector2(gripAt.X, all[^1].GetGlobalRect().End.Y - 2);
        grip.GetParent().EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, GlobalPosition = gripAt });
        grip.GetParent().EmitSignal(Control.SignalName.GuiInput, new InputEventMouseMotion { GlobalPosition = bottom });
        await Wait(menu, 0.2);
        await Capture(screen, outDir, "11b-multi-drag");
        grip.GetParent().EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, GlobalPosition = bottom });
        await Wait(menu, 0.5);
        Check("dragging a multi-selection moves the group, in order", Ids().TakeLast(2).SequenceEqual(pickedIds), string.Join(", ", Ids()));
        Selection.Clear(content);

        // Put the original order back: nothing pending again.
        for (var i = 0; i < start.Count; i++)
            if (Rows().FirstOrDefault(r => r.Mod?.manifest?.id == start[i])?.Mod is { } mod)
                LoadOrder.Move(mod, i, Rows());
        CategoryList.Redraw(content);
        await Wait(menu, 0.5);
        Check("restoring the order clears pending", Ids().SequenceEqual(start) && !ModEntry.HasPendingChanges());

        await TestDependencies(menu, screen, content, outDir);

        // Leaving Load Order: a plain category list again, the selected mod still selected, its collapsed
        // category opened, no lines or dimming left behind.
        if (Rows().FirstOrDefault(r => r.Mod?.manifest?.id == "SmmTestFixes") is { Mod: { } kept } keptRow)
        {
            CategoryList.Select(screen, keptRow);
            Store.Data.UncategorizedCollapsed = true;
        }
        LoadOrder.ShowLinks(content, Rows()[0], strong: false);
        Click(screen.GetNode<NButton>("SpireModManagerLoadOrderToggle"));
        await Wait(menu, 0.5);
        var selected = Rows().FirstOrDefault(r => r.Mod == CategoryList.Selected);
        Check("category view hides the handles", !Rows().Any(r => r.GetNodeOrNull<Control>("SpireModManagerHandle") is { Visible: true })
            && content.GetChildren().Any(c => c.IsInGroup("SpireModManagerCategoryHeader")));
        Check("back from Load Order: titles in place", Rows().All(r => r.GetNode<Control>("Title").OffsetLeft == 18),
            string.Join(", ", Rows().Select(r => r.GetNode<Control>("Title").OffsetLeft)));
        Check("back from Load Order: no lines or dimming", !content.GetParent().GetChildren().Any(c => c.IsInGroup("SpireModManagerLinks"))
            && Rows().All(r => r.Modulate == Colors.White));
        Check("the selection survives the switch, its category opened", selected?.Mod?.manifest?.id == "SmmTestFixes"
            && selected.Visible && !Store.Data.UncategorizedCollapsed);
    }

    // Fixture mods: SmmTestContent and SmmTestUi need SmmTestLib; SmmTestAddon needs SmmTestMissing, which never is there.
    static async Task TestDependencies(Node menu, NModdingScreen screen, Control content, string outDir)
    {
        List<NModMenuRow> Rows() => content.GetChildren().OfType<NModMenuRow>().ToList();
        List<string> Ids() => Rows().Select(r => r.Mod?.manifest?.id ?? "").ToList();
        NModMenuRow? Row(string id) => Rows().FirstOrDefault(r => r.Mod?.manifest?.id == id);
        string Note(string id) => Row(id)?.GetNodeOrNull<RichTextLabel>("SpireModManagerStrip/Needs")?.GetParsedText() ?? "";
        if (Row("SmmTestLib") is not { Mod: { } lib } || Row("SmmTestContent") is not { Mod: { } dependent })
        {
            Results.Add("SKIP dependencies: test mods not installed");
            return;
        }
        Check("dependency note names what it needs", Note("SmmTestContent").Contains("Test Framework"), Note("SmmTestContent"));
        if (WontLoad() != null)
            Check("missing dependency is called out", Note("SmmTestAddon").Contains(Missing + " (not installed)"), Note("SmmTestAddon"));
        else
            Results.Add("SKIP missing dependency is called out: Test Addon not installed, or off");

        // The info panel: where it's from, and dependencies both ways.
        var panel = screen.GetNode<NModInfoContainer>("%ModInfoContainer");
        if (Row("SmmTestLib") is { } libSelected)
            CategoryList.Select(screen, libSelected);
        await Wait(menu, 0.3);
        var facts = panel.GetNode<RichTextLabel>("ModDescription").Text;
        Check("panel: required for, Open Folder", facts.Contains("Required for")
            && facts.Contains("Test Content") && facts.Contains("Test UI") && panel.GetNode<Control>("OpenFolderButton").Visible, facts);

        // Force a bad order (as an old saved list might have): the needing mod above its framework.
        LoadOrder.Move(dependent, Ids().IndexOf("SmmTestLib"), Rows());
        CategoryList.Redraw(content);
        await Wait(menu, 0.5);
        Check("bad order: banner and warning", screen.FindChild("OrderProblems", true, false) != null && Note("SmmTestContent").Contains("Test Framework after"),
            Note("SmmTestContent"));
        Check("order warning wording", (screen.FindChild("OrderProblems", true, false) as Label)?.Text.StartsWith("For compatibility, 1 mod will be automatically reordered") == true,
            (screen.FindChild("OrderProblems", true, false) as Label)?.Text ?? "");
        Check("the order warning's corners are rounded like the rows", screen.FindChild(LoadOrder.WarningName, true, false) is PanelContainer warningBar
            && warningBar.GetThemeStylebox("panel") is StyleBoxFlat { CornerRadiusTopLeft: RowTitles.CornerRadius });
        Check("the order warning is red", Red(BannerFill(screen.FindChild(LoadOrder.WarningName, true, false))),
            $"fill {BannerFill(screen.FindChild(LoadOrder.WarningName, true, false))}");
        var fixAfter = ButtonsAfterText(screen.FindChild(LoadOrder.WarningName, true, false));
        Check("Load Order: the order warning's Fix now right after its text", fixAfter.Ok, fixAfter.Detail);
        Check("Auto sort has its hint", screen.FindChild("AutoSortHint", true, false) is Label { Visible: true } hintLabel && hintLabel.Text.Contains("compatibility"));
        Check("order warning and Auto sort stay above the list, outside its scrolling",
            screen.FindChild("OrderProblems", true, false) is Node warning && !content.IsAncestorOf(warning)
            && screen.FindChild("AutoSortButton", true, false) is Control { Visible: true } sort && !content.IsAncestorOf(sort));
        await Capture(screen, outDir, "12-order-problem");

        // Hovering the warning's text: a card naming each mod placed before a mod it needs, in load order, and nothing
        // else. Test UI moved there too joins it; ticked off (its menu's Disable) it leaves again. Enabled again, and the
        // move undone (this view's undo steps through the order only), it's back as it was.
        var orderCard = await BannerCard(menu, screen, screen.FindChild(LoadOrder.WarningName, true, false));
        Check("the order warning's card: \"Test Content loads before Test Framework\", nothing else",
            string.Join(" | ", orderCard) == "Affected mods | Test Content loads before Test Framework", string.Join(" | ", orderCard));
        // Both by the names the list shows: each with an alias, then as they were.
        var aliasesWere = (Content: Store.InfoFor("SmmTestContent")?.Alias, Lib: Store.InfoFor("SmmTestLib")?.Alias);
        Store.EditMod("SmmTestContent", info => info.Alias = "Content");
        Store.EditMod("SmmTestLib", info => info.Alias = "Framework");
        CategoryList.Redraw(content);
        await Wait(menu, 0.3);
        var aliased = string.Join(" | ", await BannerCard(menu, screen, screen.FindChild(LoadOrder.WarningName, true, false)));
        Store.EditMod("SmmTestContent", info => info.Alias = aliasesWere.Content);
        Store.EditMod("SmmTestLib", info => info.Alias = aliasesWere.Lib);
        CategoryList.Redraw(content);
        await Wait(menu, 0.3);
        Check("the order warning's card by the names the list shows: \"Content loads before Framework\" with both aliased",
            aliased == "Affected mods | Content loads before Framework", aliased);
        if (Row("SmmTestUi") is { Mod: { } ui } uiRow)
        {
            var idsWere = Ids();
            LoadOrder.Move(ui, Ids().IndexOf("SmmTestLib"), Rows());
            CategoryList.Redraw(content);
            await Wait(menu, 0.3);
            var both = string.Join(" | ", await BannerCard(menu, screen, screen.FindChild(LoadOrder.WarningName, true, false)));
            LoadOrder.Actions(content, uiRow, () => CategoryList.Redraw(content))[0].OnSelect?.Invoke(); // Disable
            await Wait(menu, 0.3);
            var off = string.Join(" | ", await BannerCard(menu, screen, screen.FindChild(LoadOrder.WarningName, true, false)));
            Check("the order warning's card follows: Test UI moved before the framework too, both in load order; ticked off, Test Content alone",
                both == "Affected mods | Test Content loads before Test Framework | Test UI loads before Test Framework"
                && off == "Affected mods | Test Content loads before Test Framework", $"{both}; then {off}");
            LoadOrder.Actions(content, uiRow, () => CategoryList.Redraw(content))[0].OnSelect?.Invoke(); // Enable
            await Wait(menu, 0.3);
            History.Undo();
            await Wait(menu, 0.3);
            Check("enabled again and the move undone: Test UI on, in its place", Dependencies.IsOn(ui) && Ids().SequenceEqual(idsWere), string.Join(", ", Ids()));
        }
        else
            Results.Add("FAIL the order warning's card: no row for Test UI");

        var before = Ids();
        screen.FindChild("FixOrderButton", true, false)?.EmitSignal(BaseButton.SignalName.Pressed);
        await Wait(menu, 0.5);
        var after = Ids();
        Check("Fix order puts the framework first and nothing else moves",
            after.IndexOf("SmmTestLib") < after.IndexOf("SmmTestContent") && after.IndexOf("SmmTestLib") < after.IndexOf("SmmTestUi")
            && screen.FindChild("OrderProblems", true, false) == null
            && after.Where(id => id != "SmmTestLib").SequenceEqual(before.Where(id => id != "SmmTestLib")),
            string.Join(", ", after));

        // A move above the framework is flagged but allowed: by the inline/menu actions and by dragging.
        var rowContent = Row("SmmTestContent");
        Check("moving above what it needs is flagged, not blocked", rowContent != null && LoadOrder.MoveBreaks(dependent, 0, Rows())
            && !LoadOrder.Actions(content, rowContent, () => { }, compact: true)[0].Disabled);
        var handle = rowContent?.GetNode<Control>("SpireModManagerHandle");
        if (handle != null)
        {
            var from = handle.GetGlobalRect().GetCenter();
            var to = new Vector2(from.X, Rows()[0].GetGlobalRect().Position.Y + 4);
            handle.GetParent().EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, GlobalPosition = from });
            handle.GetParent().EmitSignal(Control.SignalName.GuiInput, new InputEventMouseMotion { GlobalPosition = to });
            await Wait(menu, 0.3);
            await Capture(screen, outDir, "13-blocked-drag");
            var links = content.GetParent().GetChildren().Count(c => c.IsInGroup("SpireModManagerLinks"));
            Check("dragging dims unrelated rows, lights linked ones, draws links",
                Row("SmmTestFixes")?.Modulate.A < 1 && Row("SmmTestLib")?.Modulate.A == 1 && links == 1, $"{links} links");
            Check("grabbing a row selects it", CategoryList.Selected == dependent);
            handle.GetParent().EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, GlobalPosition = to });
            await Wait(menu, 0.5);
        }
        Check("a drag past a dependency goes through and is flagged", Ids()[0] == "SmmTestContent"
            && screen.FindChild("OrderProblems", true, false) != null, string.Join(", ", Ids()));
        screen.FindChild("FixOrderButton", true, false)?.EmitSignal(BaseButton.SignalName.Pressed);
        await Wait(menu, 0.5);

        // Auto sort: libraries, then gameplay, then visual-only; dependencies satisfied; one undo puts it back.
        var beforeSort = Ids();
        screen.FindChild("AutoSortButton", true, false)?.EmitSignal(BaseButton.SignalName.Pressed);
        await Wait(menu, 0.5);
        int Tier(NModMenuRow r) => Tags.For(r.Mod).Any(c => c.Text == "Library") ? 0
            : Tags.For(r.Mod).Any(c => c.Text == "Gameplay") ? 1 : 2;
        var tiers = Rows().Select(Tier).ToList();
        Check("auto sort: libraries, then gameplay, then visual-only, no dependency problems",
            tiers.Zip(tiers.Skip(1)).All(p => p.First <= p.Second) && Dependencies.OrderViolations(LoadOrder.Mods(Rows())).Count == 0,
            string.Join(", ", Ids()));
        await Capture(screen, outDir, "13c-auto-sort");
        History.Undo();
        await Wait(menu, 0.5);
        Check("one undo restores the order from before auto sort", Ids().SequenceEqual(beforeSort), string.Join(", ", Ids()));
        await Wait(menu, 0.2);
        Check("dropping restores the list look (only mods that are off stay dim)", Rows().All(r => r.Modulate == (r.Mod is { } m && !Dependencies.IsOn(m) ? new Color(1, 1, 1, 0.4f) : Colors.White))
            && !content.GetParent().GetChildren().Any(c => c.IsInGroup("SpireModManagerLinks")));

        // Hovering the framework shows a line to each mod that needs it.
        if (Row("SmmTestLib") is { } libRow)
        {
            libRow.EmitSignal(Control.SignalName.MouseEntered);
            await Wait(menu, 0.2);
            var shown = content.GetParent().GetChildren().Count(c => c.IsInGroup("SpireModManagerLinks"));
            // Rounded corners: more points than the square path (out, along the lane, in), none on its two corners,
            // the ends where they were.
            static bool Rounded(Vector2[] p)
            {
                if (p.Length <= 4)
                    return false;
                var lane = p.Min(q => q.X);
                Vector2[] corners = [new(lane, p[0].Y), new(lane, p[^1].Y)];
                return p[0].X == p[^1].X && p[0].X > lane && !p.Any(q => corners.Any(c => q.DistanceTo(c) < 0.5f));
            }
            var paths = content.GetParent().GetChildren().OfType<Line2D>().Where(l => l.IsInGroup("SpireModManagerLinks")).Select(l => l.Points).ToList();
            Check("the hover lines have slightly rounded corners", paths.Count > 0 && paths.All(Rounded),
                string.Join("; ", paths.Select(p => string.Join(" ", p.Select(q => $"{q.X:0.#},{q.Y:0.#}")))));
            // The rows here wear the main list's rounded looks: faint under the mouse, sunken when selected.
            var selectedRow = Rows().FirstOrDefault(r => r.Mod == CategoryList.Selected && r != libRow);
            StyleBox? LookOf(NModMenuRow? r) => r?.GetNodeOrNull<Panel>("SelectionHighlight")?.GetThemeStylebox("panel");
            Check("load order rows: rounded like the main list's, under the mouse and selected",
                LookOf(libRow) is StyleBoxFlat { CornerRadiusTopLeft: RowTitles.CornerRadius, BorderWidthTop: 0 }
                && LookOf(selectedRow) is StyleBoxFlat { CornerRadiusTopLeft: RowTitles.CornerRadius, BorderWidthTop: 2 }
                && selectedRow?.GetNode<Panel>("SelectionHighlight") is { } selectedLook && selectedLook.Modulate == Colors.White
                && selectedLook.OffsetLeft == RowTitles.LookInset.X, $"selected: {selectedRow?.Mod?.manifest?.id ?? "none"}");
            await Capture(screen, outDir, "13b-hover-links");
            libRow.EmitSignal(Control.SignalName.MouseExited);
            await Wait(menu, 0.2);
            Check("hover shows links to both dependents, and they go on leaving", shown == 2
                && !content.GetParent().GetChildren().Any(c => c.IsInGroup("SpireModManagerLinks")), $"{shown} shown");
        }
    }

    // Main list: a needed mod turned off gives a banner with "Enable needed mods" (one undoable step); a bad
    // order shows a "!" on the Load Order button.
    static async Task TestListBanner(Node menu, NModdingScreen screen, Control content, string outDir)
    {
        List<NModMenuRow> Rows() => content.GetChildren().OfType<NModMenuRow>().ToList();
        NModMenuRow? Row(string id) => Rows().FirstOrDefault(r => r.Mod?.manifest?.id == id);
        if (Layout.Current != Layout.View.Categories || Row("SmmTestLib") is not { Mod: { } lib } libRow || Row("SmmTestContent") is not { Mod: { } dependent })
        {
            Results.Add("SKIP list banner: not on the main list, or test mods not installed");
            return;
        }
        var tickbox = libRow.GetNode<NTickbox>("Tickbox");
        tickbox.IsTicked = false;
        tickbox.EmitSignal(NTickbox.SignalName.Toggled, tickbox);
        await Wait(menu, 0.3);
        var text = (screen.FindChild("OrderProblems", true, false) as Label)?.Text ?? "";
        Check("main list: a needed mod that is off gets a banner", text.Contains("need a mod that is disabled"), text);
        var warning = screen.FindChild(LoadOrder.WarningName, true, false);
        var showTip = (warning?.FindChild("ShowProblemButton", true, false) as Control)?.TooltipText;
        Check("main list banner: red, its Show tip \"Opens the next affected mod\", no \"Better Mod Menu\" in it",
            Red(BannerFill(warning)) && showTip == "Opens the next affected mod" && !BannerWords(warning).Any(w => w.Contains("Better Mod Menu")),
            $"fill {BannerFill(warning)}; {string.Join(" | ", BannerWords(warning))}");
        var after = ButtonsAfterText(warning);
        Check("main list banner: Show and Enable needed mods right after its text", after.Ok
            && warning?.FindChild("EnableNeededButton", true, false) is Control { Visible: true }, after.Detail);
        await Capture(screen, outDir, "2b-list-banner");

        // Hovering its text: a card with each mod that won't load and what it needs, by the names the list shows (Test
        // Content and the framework by an alias here): Test Content and Test UI, and every other enabled mod whose own row
        // says a mod it needs is off or not installed (the fixture's Test Addon: SmmTestMissing), nothing else.
        var aliasesWere = (Content: Store.InfoFor("SmmTestContent")?.Alias, Lib: Store.InfoFor("SmmTestLib")?.Alias);
        Store.EditMod("SmmTestContent", info => info.Alias = "Content");
        Store.EditMod("SmmTestLib", info => info.Alias = "Framework");
        CategoryList.Redraw(content);
        await Wait(menu, 0.3);
        string Shown(Mod mod) => Store.InfoFor(mod.manifest?.id)?.Alias ?? mod.manifest?.name ?? "";
        string NoteOf(Mod mod) => Rows().FirstOrDefault(r => r.Mod == mod)?.GetNodeOrNull<RichTextLabel>("SpireModManagerStrip/Needs")?.GetParsedText() ?? "";
        var flaggedMods = Rows().Select(r => r.Mod).OfType<Mod>()
            .Where(m => Dependencies.IsOn(m) && (NoteOf(m).Contains("(off)") || NoteOf(m).Contains("(not installed)"))).ToList();
        var flagged = flaggedMods.Select(Shown).Order().ToList();
        var card = await BannerCard(menu, screen, screen.FindChild(LoadOrder.WarningName, true, false), "2j-banner-card", outDir);
        var named = card.Skip(1).Select(l => l.Split(": needs ")[0]).Order().ToList();
        Check("main list banner: hovering its text, a card with each mod that won't load and what it needs, by the names the list shows, nothing else",
            card.FirstOrDefault() == "Affected mods" && card.Contains("Content: needs Framework (disabled)") && card.Contains("Test UI: needs Framework (disabled)")
            && named.SequenceEqual(flagged) && !card.Any(l => l.Contains("Test Framework") || l.StartsWith("Test Content")),
            $"{string.Join(" | ", card)}; rows saying so: {string.Join(", ", flagged)}");
        // Each line gives its row's reasons: "(off)" there is "(disabled)" here, "(not installed)" the same. The fixture's
        // Test Addon (on) needs SmmTestMissing, which isn't installed.
        string? LineOf(Mod mod) => card.Skip(1).FirstOrDefault(l => l.StartsWith(Shown(mod) + ": needs "));
        var wrong = flaggedMods.Where(m => LineOf(m) is not { } line || NoteOf(m).Contains("(off)") != line.Contains("(disabled)")
            || NoteOf(m).Contains("(not installed)") != line.Contains("(not installed)")).Select(Shown).ToList();
        Check("main list banner's card: each line's reasons are its row's (\"(off)\" as \"(disabled)\", \"(not installed)\")", wrong.Count == 0,
            $"wrong for {string.Join(", ", wrong)}: {string.Join(" | ", card)}");
        if (WontLoad() is { } addon)
            Check($"main list banner's card: Test Addon needs {Missing} (not installed)", LineOf(addon)?.Contains(Missing + " (not installed)") == true,
                LineOf(addon) ?? "no line");
        else
            Results.Add("SKIP main list banner's card, Test Addon's line: Test Addon not installed, or off");

        // A tick while the card shows: the banner is made again, and no card lists the mod ticked off (that one went with
        // its banner); the next, worked out when it opens, leaves it out.
        if (BannerText(screen.FindChild(LoadOrder.WarningName, true, false)) is { } bannerText && Row("SmmTestUi")?.GetNode<NTickbox>("Tickbox") is { } uiBox)
        {
            var at = bannerText.GetGlobalRect().GetCenter();
            Hover.TestMouse = () => at;
            bannerText.EmitSignal(Control.SignalName.MouseEntered);
            await Wait(menu, 0.1);
            var open = BannerCardLines(screen).Contains("Test UI: needs Framework (disabled)");
            uiBox.IsTicked = false;
            uiBox.EmitSignal(NTickbox.SignalName.Toggled, uiBox);
            await Wait(menu, 0.3);
            var stale = BannerCardLines(screen).Any(l => l.StartsWith("Test UI"));
            Hover.TestMouse = null;
            var next = await BannerCard(menu, screen, screen.FindChild(LoadOrder.WarningName, true, false));
            Check("a tick while the banner's card shows: no stale card, and the next leaves out the mod ticked off",
                open && !stale && next.Contains("Content: needs Framework (disabled)") && !next.Any(l => l.StartsWith("Test UI")),
                $"listed before {open}, stale after {stale}; then {string.Join(" | ", next)}");
            uiBox.IsTicked = true;
            uiBox.EmitSignal(NTickbox.SignalName.Toggled, uiBox);
            await Wait(menu, 0.3);
        }
        else
            Results.Add("FAIL main list banner's card after a tick: no banner text or no Test UI row");
        Store.EditMod("SmmTestContent", info => info.Alias = aliasesWere.Content);
        Store.EditMod("SmmTestLib", info => info.Alias = aliasesWere.Lib);
        CategoryList.Redraw(content);
        await Wait(menu, 0.3);
        screen.FindChild("EnableNeededButton", true, false)?.EmitSignal(BaseButton.SignalName.Pressed);
        await Wait(menu, 0.3);
        Check("Enable needed mods turns it on, banner says no more", Dependencies.IsOn(lib) && tickbox.IsTicked
            && screen.FindChild("EnableNeededButton", true, false) == null
            && (screen.FindChild("OrderProblems", true, false) as Label)?.Text.Contains("is disabled") != true);
        History.Undo();
        await Wait(menu, 0.3);
        Check("one undo turns it off again", !Dependencies.IsOn(lib));
        tickbox.IsTicked = true;
        tickbox.EmitSignal(NTickbox.SignalName.Toggled, tickbox);
        await Wait(menu, 0.3);

        var badge = screen.GetNodeOrNull<Control>("SpireModManagerLoadOrderToggle/WarningBadge");
        Check("no order problem, no '!' on Load Order", badge is not { Visible: true });
        LoadOrder.Move(dependent, LoadOrder.Mods(Rows()).IndexOf(lib), Rows());
        CategoryList.Redraw(content);
        await Wait(menu, 0.3);
        badge = screen.GetNodeOrNull<Control>("SpireModManagerLoadOrderToggle/WarningBadge");
        var badgeTip = screen.GetNode<Control>("SpireModManagerLoadOrderToggle").TooltipText;
        Check("a bad order puts a '!' with the warning on the Load Order button, then the mod it's about, as Load Order's card says it",
            badge is { Visible: true } && badgeTip.StartsWith("For compatibility, 1 mod ")
            && string.Join(" | ", badgeTip.Split('\n').Skip(1).Where(l => l.Length > 0)) == "Test Content loads before Test Framework", badgeTip);
        await Capture(screen, outDir, "2c-load-order-badge");
        LoadOrder.FixOrder(Rows());
        CategoryList.Redraw(content);
        await Wait(menu, 0.3);
        Check("fixed: the '!' goes", screen.GetNodeOrNull<Control>("SpireModManagerLoadOrderToggle/WarningBadge") is not { Visible: true });
    }

    // Better Mod Menu ticked as well as ours: a yellow banner on our views, above the problem banners, with a Show that
    // goes to its row and a Dismiss that hides it until the two aren't both ticked. It comes and goes with its tickbox at
    // once (no relaunch). The fixture has it installed but off; with --bmm it's on and loaded, so the banner is there from
    // the start. It ends as it started.
    static async Task TestManagersBanner(Node menu, NModdingScreen screen, Control content, string outDir)
    {
        List<NModMenuRow> Rows() => content.GetChildren().OfType<NModMenuRow>().ToList();
        NModMenuRow? Row(string id) => Rows().FirstOrDefault(r => r.Mod?.manifest?.id == id);
        if (Layout.Current != Layout.View.Categories || Row(Bmm.ModId) is not { Mod: { } bmm } row)
        {
            Results.Add("SKIP managers banner: not on the main list, or Better Mod Menu not installed");
            return;
        }
        var border = screen.GetNode<Control>("ModsBorder");
        var search = border.GetNode<Control>("SpireModManagerSearch");
        var list = border.GetNode<Control>("ModsScrollContainer");
        Control? Banner() => border.GetNodeOrNull<Control>(LoadOrder.ManagersName);
        var tickbox = row.GetNode<NTickbox>("Tickbox");
        async Task Tick(NTickbox box, bool on)
        {
            box.IsTicked = on;
            box.EmitSignal(NTickbox.SignalName.Toggled, box);
            await Wait(menu, 0.3);
        }
        // Under the search box and above the list; with the page's own warning (below), above that.
        bool Placed(Control? below = null) => Banner() is { Visible: true } banner && banner.GetGlobalRect() is var at
            && at.Position.Y >= search.GetGlobalRect().End.Y && at.End.Y <= (below ?? list).GetGlobalRect().Position.Y
            && (below == null || below.GetGlobalRect().End.Y <= list.GetGlobalRect().Position.Y);
        string Where() => $"banner {Banner()?.GetGlobalRect()}, warning {border.GetNodeOrNull<Control>(LoadOrder.WarningName)?.GetGlobalRect()}, "
            + $"search {search.GetGlobalRect()}, list from {list.GetGlobalRect().Position.Y}";

        var was = Bmm.Ticked;
        Check(was ? "Better Mod Menu ticked at the start (--bmm): the managers banner shows" : "Better Mod Menu off at the start: no managers banner",
            tickbox.IsTicked == was && (Banner() != null) == was, Where());
        if (!was)
            await Tick(tickbox, true);
        var text = (Banner()?.FindChild("ManagersWarning", true, false) as Label)?.Text ?? "";
        Check("Better Mod Menu ticked: the managers banner at once, under the search box, above the list", Bmm.Ticked && Placed()
            && text == "Multiple mod managers are currently enabled, which may cause unexpected behavior.", $"\"{text}\"; {Where()}");
        Check("the managers banner is yellow: a warning, not an error", Yellow(BannerFill(Banner())), $"fill {BannerFill(Banner())}");
        var showTip = (Banner()?.FindChild("ShowManagersButton", true, false) as Control)?.TooltipText;
        Check("the managers banner's Show tip is the other banner's: \"Opens the next affected mod\"", showTip == "Opens the next affected mod", showTip ?? "no Show");
        Check("the managers banner names no manager: no \"Better Mod Menu\" in its text or tips",
            BannerWords(Banner()) is { Count: > 0 } words && !words.Any(w => w.Contains("Better Mod Menu")), string.Join(" | ", BannerWords(Banner())));
        // Hovering its text: a card naming the mod managers that are on, by the names the list gives them (ours by the
        // fixture's alias), and nothing else.
        var managers = string.Join(" | ", await BannerCard(menu, screen, Banner()));
        var bmmShown = Store.InfoFor(Bmm.ModId)?.Alias ?? bmm.manifest?.name;
        Check("the managers banner's card: the managers that are on, ours by its alias (\"The Manager\"), then Better Mod Menu, nothing else",
            Store.InfoFor(ModEntry.ModId)?.Alias == "The Manager" && managers == $"Enabled mod managers | The Manager | {bmmShown}", managers);
        var after = ButtonsAfterText(Banner());
        Check("main list: the managers banner's Show and Dismiss right after its text", after.Ok, after.Detail);
        await Capture(screen, outDir, "2e-managers-banner");

        // A narrow list: the text wraps in what the buttons leave, they follow it, the banner no wider than the list (a
        // line too full widens it past the list's edge instead), and it grows to fit with the list still below it. Then
        // back to its width: one line, the usual height.
        const float narrower = 450;
        var wording = Banner()?.FindChild("ManagersWarning", true, false) as Label;
        border.OffsetRight -= narrower;
        await Wait(menu, 0.3);
        after = ButtonsAfterText(Banner());
        Check("a narrow list: the managers text wraps, Show and Dismiss right after it inside the list's width, the banner taller, the list below it",
            wording?.GetLineCount() > 1 && after.Ok && Banner()?.Size.Y > 40 && Placed(), $"{wording?.GetLineCount()} lines; {after.Detail}; {Where()}");
        await Capture(screen, outDir, "2e-managers-banner-narrow");
        border.OffsetRight += narrower;
        await Wait(menu, 0.3);
        Check("back to its width: the managers text on one line, the banner its usual height", wording?.GetLineCount() == 1 && Banner()?.Size.Y < 35 && Placed(),
            $"{wording?.GetLineCount()} lines; {Where()}");

        // With the page's own warning too (a needed mod turned off): this one first, that one under it.
        if (Row("SmmTestLib")?.GetNode<NTickbox>("Tickbox") is { } lib)
        {
            await Tick(lib, false);
            Check("with the page's own warning: the managers banner above it, both above the list",
                border.GetNodeOrNull<Control>(LoadOrder.WarningName) is { Visible: true } warning && Placed(warning), Where());
            Check("beside it, the page's own warning stays red, the managers banner yellow",
                Red(BannerFill(border.GetNodeOrNull(LoadOrder.WarningName))) && Yellow(BannerFill(Banner())),
                $"warning {BannerFill(border.GetNodeOrNull(LoadOrder.WarningName))}, managers {BannerFill(Banner())}");
            await Capture(screen, outDir, "2f-both-banners");
            await Tick(lib, true);
        }

        // Show: its row, even in a closed category: opened, scrolled to with a glow, and in the panel.
        var collapsed = Store.Data.Categories.Select(c => c.Collapsed).ToList();
        var uncategorized = Store.Data.UncategorizedCollapsed;
        foreach (var category in Store.Data.Categories)
            category.Collapsed = true;
        Store.Data.UncategorizedCollapsed = true;
        CategoryList.Apply(content);
        if (Rows().FirstOrDefault(r => r != row) is { } other)
            CategoryList.Select(screen, other);
        await Wait(menu, 0.2);
        int Glows() => row.GetChildren().OfType<ColorRect>().Count(c => c.Color.R > 0.9f && c.Color.B < 0.4f); // CategoryList's gold flash
        var (hidden, glows) = (!row.Visible, Glows());
        Banner()?.FindChild("ShowManagersButton", true, false)?.EmitSignal(BaseButton.SignalName.Pressed);
        await Wait(menu, 0.5);
        Check("its Show opens Better Mod Menu's category, scrolls to its row with a glow and shows it in the panel",
            hidden && row.Visible && list.GetGlobalRect().Intersects(row.GetGlobalRect()) && Glows() > glows && CategoryList.Selected == bmm,
            $"hidden before {hidden}, shown {row.Visible}, row {row.GetGlobalRect()}, list {list.GetGlobalRect()}, glows {glows} -> {Glows()}, panel {CategoryList.Selected?.manifest?.id}");

        // Show while a search leaves its row out (main list and Load Order): the search clears, box and list, and the
        // row shows as above. On the main list its category is closed again too.
        var searchBox = border.GetNode<LineEdit>("SpireModManagerSearch");
        async Task ShowThroughSearch(string view)
        {
            const string query = "SmmTest"; // the test mods, not Better Mod Menu
            searchBox.Text = query;
            searchBox.EmitSignal(LineEdit.SignalName.TextChanged, query);
            await Wait(menu, 0.3);
            if (Rows().FirstOrDefault(r => r != row && r.Visible) is { } match)
                CategoryList.Select(screen, match);
            await Wait(menu, 0.2);
            var left = !row.Visible && !SpireModManager.Search.Matches(bmm, query);
            var old = row.GetChildren().OfType<ColorRect>().ToList(); // an earlier glow may still be fading
            Banner()?.FindChild("ShowManagersButton", true, false)?.EmitSignal(BaseButton.SignalName.Pressed);
            await Wait(menu, 0.5);
            var filter = content.HasMeta(CategoryList.FilterMeta) ? (string)content.GetMeta(CategoryList.FilterMeta) : "";
            var glowed = row.GetChildren().OfType<ColorRect>().Except(old).Any(c => c.Color.R > 0.9f && c.Color.B < 0.4f);
            Check($"{view}: with a search that hides Better Mod Menu's row, its Show clears the search and shows the row, scrolled to with a glow and in the panel",
                left && searchBox.Text == "" && filter == "" && row.Visible && list.GetGlobalRect().Intersects(row.GetGlobalRect()) && glowed
                && CategoryList.Selected == bmm,
                $"hidden by the search {left}, box \"{searchBox.Text}\", filter \"{filter}\", shown {row.Visible}, row {row.GetGlobalRect()}, "
                + $"list {list.GetGlobalRect()}, glow {glowed}, panel {CategoryList.Selected?.manifest?.id}");
        }
        foreach (var category in Store.Data.Categories)
            category.Collapsed = true;
        Store.Data.UncategorizedCollapsed = true;
        await ShowThroughSearch("main list");

        for (var i = 0; i < collapsed.Count && i < Store.Data.Categories.Count; i++)
            Store.Data.Categories[i].Collapsed = collapsed[i];
        Store.Data.UncategorizedCollapsed = uncategorized;
        Store.Save(); // opening its category saved; so does this, as it was
        CategoryList.Apply(content);
        await Wait(menu, 0.2);

        // Load Order too; not in Vanilla View (with --bmm, Better Mod Menu's own page).
        Click(screen.GetNode<NButton>("SpireModManagerLoadOrderToggle"));
        await Wait(menu, 0.5);
        Check("Load Order: the managers banner too, under the search box", Placed(), Where());
        after = ButtonsAfterText(Banner());
        Check("Load Order: the managers banner's Show and Dismiss right after its text", after.Ok, after.Detail);
        await ShowThroughSearch("Load Order");
        Click(screen.GetNode<NButton>("SpireModManagerLoadOrderToggle"));
        await Wait(menu, 0.5);
        Check("Vanilla View: no managers banner", await WithVanillaView(screen, menu, () => Banner() is not { Visible: true }));

        // Dismiss, beside Show: the banner goes and leaves no gap (the next thing under the search box, the page's own
        // warning, the page bar or the list, right under it). View state: no undo step, not a change or pending, saved.
        bool Buttons() => Banner()?.FindChild("ShowManagersButton", true, false) is Button { Visible: true } show
            && Banner()?.FindChild("DismissManagersButton", true, false) is Button { Visible: true } dismiss
            && dismiss.GetGlobalRect().Position.X > show.GetGlobalRect().End.X && dismiss.TooltipText.StartsWith("Hides this warning");
        bool Closed() => Banner() == null && (new[] { LoadOrder.WarningName, PageBar.Name, PageBar.HintName }
            .Select(name => border.GetNodeOrNull<Control>(name)).FirstOrDefault(c => c is { Visible: true }) ?? list)
            .GetGlobalRect().Position.Y - search.GetGlobalRect().End.Y is >= 0 and < 12;
        bool? Saved() => JsonNode.Parse(File.ReadAllText(Store.FilePath))?["managers_warning_dismissed"]?.GetValue<bool>();
        (string, string, bool, bool, bool) State() =>
            (Store.EditsJson(), string.Join(" | ", HistoryBar.Describe()), ModEntry.HasPendingChanges(), Profiles.Edited, History.CanUndo);
        async Task Dismiss()
        {
            Banner()?.FindChild("DismissManagersButton", true, false)?.EmitSignal(BaseButton.SignalName.Pressed);
            await Wait(menu, 0.3);
        }
        Check("the managers banner has Show, then Dismiss with its tip", Buttons(), Where());
        var before = State();
        var moves = 0;
        void Moved() => moves++; // the history moves when a step is added
        History.Changed += Moved;
        await Dismiss();
        History.Changed -= Moved;
        Check("Dismiss: the banner goes, no gap above the list", Store.Data.ManagersWarningDismissed && Closed(), Where());
        Check("Dismiss is no undo step, not in Changes, not pending, no unsaved profile edit; saved", moves == 0 && State() == before && Saved() == true,
            $"history moved {moves}, same state {State() == before}, saved {Saved()}");
        await Capture(screen, outDir, "2g-managers-dismissed");
        CategoryList.Redraw(content);
        await Wait(menu, 0.3);
        Check("dismissed: still gone after a redraw", Closed(), Where());
        Click(screen.GetNode<NButton>("SpireModManagerLoadOrderToggle"));
        await Wait(menu, 0.5);
        Check("dismissed: not in Load Order either, no gap above the rows", Layout.Current == Layout.View.LoadOrder && Closed(), Where());
        Click(screen.GetNode<NButton>("SpireModManagerLoadOrderToggle"));
        await Wait(menu, 0.5);
        Check("dismissed: still gone back on the main list", Layout.Current == Layout.View.Categories && Closed() && Store.Data.ManagersWarningDismissed, Where());

        // Unticked, the dismiss is re-armed (cleared and saved; the untick is the only undo step); ticked again, the
        // banner is back.
        moves = 0;
        History.Changed += Moved;
        await Tick(tickbox, false);
        History.Changed -= Moved;
        Check("dismissed, then Better Mod Menu unticked: no banner, the dismiss cleared and saved, one undo step", !Bmm.Ticked && Banner() == null
            && !Store.Data.ManagersWarningDismissed && Saved() == false && moves == 1,
            $"flag {Store.Data.ManagersWarningDismissed}, saved {Saved()}, history moved {moves}; {Where()}");
        Check("no Better Mod Menu warning in the bottom row anywhere (the banner says it now)", screen.FindChild("SpireModManagerBmmWarning", true, false) == null);
        await Tick(tickbox, true);
        Check("ticked again: the banner is back, with its Show and Dismiss", Placed() && Buttons(), Where());

        // Turned off with another mod, the way Disable all and the relaunch check's fixes do it (their redraw re-arms it,
        // before the profile follows): still one undo step, which turns both back on, and the banner is back.
        if (Row("SmmTestLib") is { Mod: { } libMod })
        {
            await Dismiss();
            var dismissed = Store.Data.ManagersWarningDismissed && Banner() == null; // or the redraw has nothing to re-arm
            Dependencies.Set([bmm, libMod], false, screen);
            CategoryList.Redraw(content);
            await Wait(menu, 0.3);
            Check("dismissed, then Better Mod Menu turned off with another mod: no banner, the dismiss cleared",
                dismissed && !Bmm.Ticked && Banner() == null && !Store.Data.ManagersWarningDismissed, $"dismissed first {dismissed}; {Where()}");
            History.Undo();
            await Wait(menu, 0.3);
            Check("one undo turns both back on, and the banner is back", Bmm.Ticked && Dependencies.IsOn(libMod) && Placed() && Buttons(), Where());
        }
        else
            Results.Add("SKIP managers banner turned off with another mod: SmmTestLib not installed");

        // Our own row unticked re-arms it too (only both ticked is two managers on).
        if (Row(ModEntry.ModId)?.GetNode<NTickbox>("Tickbox") is { } ours)
        {
            await Dismiss();
            var dismissed = Store.Data.ManagersWarningDismissed && Banner() == null;
            await Tick(ours, false);
            Check("dismissed, then our own mod unticked: no banner, the dismiss cleared",
                dismissed && Banner() == null && !Store.Data.ManagersWarningDismissed, $"dismissed first {dismissed}; {Where()}");
            await Tick(ours, true);
            Check("ours ticked again: the banner is back", Placed() && Buttons(), Where());
        }
        else
            Results.Add("SKIP managers banner with our own mod unticked: no row for it");

        // A change our page never saw (Better Mod Menu uninstalled, or unticked elsewhere, between sessions): the launch
        // check forgets the dismiss as well. Switched off here without a redraw, then back on with one.
        {
            await Dismiss();
            var dismissed = Store.Data.ManagersWarningDismissed && Banner() == null;
            Dependencies.Set([bmm], false, screen);
            Bmm.ForgetDismissedWarning();
            Check("dismissed, then Better Mod Menu off where our page didn't see it: the launch check clears the dismiss",
                dismissed && !Store.Data.ManagersWarningDismissed && Saved() == false, $"dismissed first {dismissed}; flag {Store.Data.ManagersWarningDismissed}, saved {Saved()}");
            Dependencies.Set([bmm], true, screen);
            CategoryList.Redraw(content);
            await Wait(menu, 0.3);
            Check("on again: the banner is back", Placed() && Buttons(), Where());
        }

        await Tick(tickbox, false);
        Check("Better Mod Menu unticked: the banner goes at once", !Bmm.Ticked && Banner() == null, Where());
        if (!was)
            return;
        await Tick(tickbox, true);
        Check("ticked again, as it started: the banner is back", Banner() is { Visible: true }, Where());
    }

    // The bar under the search box: Enable all / Disable all for the page (or the search's matches),
    // Expand all / Collapse all. Category headers: the same two inline, last. Rows: the author at the end.
    static async Task TestPageBar(Node menu, NModdingScreen screen, Control content, string outDir)
    {
        List<NModMenuRow> Rows() => content.GetChildren().OfType<NModMenuRow>().ToList();
        Control? Bar() => screen.FindChild(PageBar.Name, true, false) as Control;
        void Press(string name) => Bar()?.FindChild(name, true, false)?.EmitSignal(BaseButton.SignalName.Pressed);
        Check("page bar on the main list", Bar() is { Visible: true } bar && bar.FindChild("Scope", true, false) is Label { Text: var all } && all.StartsWith("Installed mods"));
        await Capture(screen, outDir, "2d-page-bar");

        var box = screen.GetNode<LineEdit>("ModsBorder/SpireModManagerSearch");
        void Find(string text)
        {
            box.Text = text;
            box.EmitSignal(LineEdit.SignalName.TextChanged, text);
        }
        Find("#Gameplay");
        await Wait(menu, 0.3);
        var matched = Rows().Where(r => SpireModManager.Search.Matches(r.Mod, "#Gameplay")).Select(r => r.Mod).OfType<Mod>().ToList();
        var others = Rows().Select(r => r.Mod).OfType<Mod>().Except(matched).ToDictionary(m => m, Dependencies.IsOn);
        Check("searching: the bar says it acts on the matches", (Bar()?.FindChild("Scope", true, false) as Label)?.Text.StartsWith("Search matches") == true);
        Press("DisableAllButton");
        await Wait(menu, 0.3);
        Check("Disable all turns off just the matches", matched.All(m => !Dependencies.IsOn(m)) && others.All(p => Dependencies.IsOn(p.Key) == p.Value),
            string.Join(", ", matched.Select(m => m.manifest?.id)));
        History.Undo();
        await Wait(menu, 0.3);
        Find("");
        await Wait(menu, 0.3);

        Press("CollapseAllButton");
        await Wait(menu, 0.3);
        Check("Collapse all closes every category", Store.Data.Categories.All(c => c.Collapsed) && Store.Data.UncategorizedCollapsed && Rows().All(r => !r.Visible));
        Press("ExpandAllButton");
        await Wait(menu, 0.3);
        Check("Expand all opens them again", Store.Data.Categories.All(c => !c.Collapsed) && !Store.Data.UncategorizedCollapsed && Rows().Any(r => r.Visible));

        var headerButtons = (Header(content, Store.Data.Categories.First().Name) as Node)?.GetNodeOrNull("Actions")?.GetChildren().OfType<Button>().Select(b => b.Text).ToList() ?? [];
        Check("category headers: Enable all, Disable all inline, last", headerButtons.TakeLast(2).SequenceEqual(["Enable all", "Disable all"]), string.Join(",", headerButtons));
        var uncategorized = (content.GetNodeOrNull("Uncategorized")?.GetNodeOrNull("Actions")?.GetChildren().OfType<Button>().Select(b => b.Text).ToList()) ?? [];
        Check("Uncategorized: just Enable all, Disable all", uncategorized.SequenceEqual(["Enable all", "Disable all"]), string.Join(",", uncategorized));
        var author = Rows().FirstOrDefault(r => r.Mod?.manifest?.id == "SmmTestFixes")?.GetNodeOrNull<Label>("SpireModManagerStrip/Author")?.Text;
        Check("the author at the end of the row's line", author == "symphony", author ?? "none");

        // Mod names in the panel: hovering lights the row (it's in view); clicking scrolls to it, selected look.
        if (Rows().FirstOrDefault(r => r.Mod?.manifest?.id == "SmmTestLib") is { } libRow
            && Rows().FirstOrDefault(r => r.Mod?.manifest?.id == "SmmTestUi") is { } uiRow)
        {
            CategoryList.Select(screen, libRow);
            await Wait(menu, 0.3);
            var description = screen.GetNode<RichTextLabel>("%ModInfoContainer/ModDescription");
            var look = uiRow.GetNode<Panel>("SelectionHighlight");
            var before = look.Modulate;
            description.EmitSignal(RichTextLabel.SignalName.MetaHoverStarted, "mod:SmmTestUi");
            var lit = look.Modulate;
            var litFill = (look.GetThemeStylebox("panel") as StyleBoxFlat)?.BgColor.A ?? 0;
            description.EmitSignal(RichTextLabel.SignalName.MetaHoverEnded, "mod:SmmTestUi");
            Check("hovering a mod's name in the panel lights its row (visibly), and lets go", lit.A * litFill > 0.05f && look.Modulate == before, $"{before} -> {lit} x {litFill} -> {look.Modulate}");
            description.EmitSignal(RichTextLabel.SignalName.MetaClicked, "mod:SmmTestUi");
            await Wait(menu, 0.3);
            Check("clicking it gives the row the selected look; the panel stays", uiRow.HasFocus() && CategoryList.Selected?.manifest?.id == "SmmTestLib");
        }
    }

    // The page bar's Show: All, Enabled, Disabled, Changed (ListFilter). Each shows exactly its rows (by the tickboxes;
    // the mods Changes names), with a search too (both must match), and the bar counts them; each category counts its
    // rows shown and has no header when it shows none. Choosing is no undo step and not pending. A row ticked under a
    // choice stays, counted, until a choice is pressed again or many mods change at once (undo, a profile, Disable all);
    // Enable all acts on the rows shown, which then go, as do the mods a profile switched to turns off; picked mods
    // edited in their menu stay; going to a mod shows its row, clearing a search that leaves it out.
    // Load Order and Vanilla List show every row; All shows every row again. (The page opening again:
    // TestListFilterReopen, near the end, as it starts a new visit for undo.)
    static async Task TestListFilters(Node menu, NModdingScreen screen, Control content, string outDir)
    {
        if (Layout.Current != Layout.View.Categories)
        {
            Results.Add("SKIP list filters: not on the main list");
            return;
        }
        List<NModMenuRow> Rows() => content.GetChildren().OfType<NModMenuRow>().ToList();
        Control? Bar() => screen.FindChild(PageBar.Name, true, false) as Control;
        async Task Press(string name)
        {
            Bar()?.FindChild(name, true, false)?.EmitSignal(BaseButton.SignalName.Pressed);
            await Wait(menu, 0.3);
        }
        static List<string> Ids(IEnumerable<NModMenuRow> rows) => [.. rows.Select(r => r.Mod?.manifest?.id ?? "").Order()];
        List<string> Shown() => Ids(Rows().Where(r => r.Visible));
        List<string> Where(Func<Mod, bool> rule) => Ids(Rows().Where(r => r.Mod is { } mod && rule(mod)));
        string Scope() => (Bar()?.FindChild("Scope", true, false) as Label)?.Text ?? "";
        // The choices as the bar shows them, the lit one (white text) with a *.
        string Choices() => string.Join(" ", Bar()?.FindChild("Show", true, false)?.GetChildren().OfType<Button>()
            .Select(b => b.Text + (b.GetThemeColor("font_color") == Colors.White ? "*" : "")) ?? []);
        string Say() => $"shown {string.Join(", ", Shown())} | {Scope()} | {Choices()}";
        // Each category's header: the count of its rows the rule shows, or no header when that's none. "" when all agree.
        string Headers(Func<Mod, bool> rule)
        {
            var wrong = new List<string>();
            foreach (var name in Store.Data.Categories.Select(c => (string?)c.Name).Append(null))
            {
                var count = Rows().Count(r => r.Mod is { } mod && rule(mod)
                    && (Store.InfoFor(mod.manifest?.id)?.Category is { } c && Categories.Find(c) != null ? c : null) == name);
                var header = content.GetNodeOrNull<Control>(name == null ? "Uncategorized" : $"Category {name}");
                var said = header?.GetNodeOrNull<Label>("Count")?.Text;
                if (count == 0 ? header != null : said != Menus.Number(count))
                    wrong.Add($"{name ?? "Uncategorized"}: {count} shown, header {said ?? "none"}");
            }
            return string.Join("; ", wrong);
        }
        var box = screen.GetNode<LineEdit>("ModsBorder/SpireModManagerSearch");
        async Task Find(string text)
        {
            box.Text = text;
            box.EmitSignal(LineEdit.SignalName.TextChanged, text);
            await Wait(menu, 0.3);
        }
        static bool On(Mod mod) => Dependencies.IsOn(mod);
        static bool Off(Mod mod) => !Dependencies.IsOn(mod);

        await Find("");
        if (Bar()?.FindChild("ExpandAllButton", true, false) is Button { Disabled: false })
            await Press("ExpandAllButton"); // every category open: a row shown is a row on screen
        var all = Ids(Rows());
        Check("the page bar: \"Show:\" All, Enabled, Disabled, Changed, All lit", Choices() == "All* Enabled Disabled Changed"
            && (Bar()?.FindChild("ShowLabel", true, false) as Label)?.Text == "Show:" && Shown().SequenceEqual(all), Say());
        // Its buttons all within the list's width, also with a selection's "Clear selection" (the widest the bar gets).
        var list = screen.GetNode<Control>("%ModsScrollContainer").GetGlobalRect();
        string Overflow() => string.Join(", ", Bar()?.FindChildren("*", "Button", true, false).OfType<Button>()
            .Where(b => b.GetGlobalRect().End.X > list.End.X + 0.5f).Select(b => $"{b.Text} to {b.GetGlobalRect().End.X:0}") ?? []);
        var alone = Overflow();
        Selection.Add(content, Rows());
        await Wait(menu, 0.3);
        var (picked, withPicked) = (Scope(), Overflow());
        Selection.Clear(content);
        await Wait(menu, 0.3);
        Check("the page bar fits the list's width, with a selection too", alone == "" && withPicked == "" && picked.StartsWith("Selected"),
            $"alone: {alone}; {picked}: {withPicked}; the list ends at {list.End.X:0}");

        // Choosing: view state, never an edit.
        (string, string, bool, bool, bool) State() =>
            (Store.EditsJson(), string.Join(" | ", HistoryBar.Describe()), ModEntry.HasPendingChanges(), Profiles.Edited, History.CanUndo);
        var before = State();
        var moves = 0;
        void Moved() => moves++; // the history moves when a step is added
        History.Changed += Moved;
        try
        {
            await Press("ShowEnabledButton");
            var on = Where(On);
            Check("Show: Enabled: just the enabled mods, the bar counting them, Enabled lit", Shown().SequenceEqual(on)
                && Scope() == $"Enabled mods ({on.Count})" && Choices() == "All Enabled* Disabled Changed", Say());
            Check("Show: Enabled: each category counts its enabled mods; none: no header", Headers(On) == "", Headers(On));

            await Press("ShowDisabledButton");
            var off = Where(Off);
            Results.Add($"INFO {on.Count} enabled, {off.Count} disabled of {all.Count}");
            Check("Show: Disabled: just the disabled mods, counted, Disabled lit", Shown().SequenceEqual(off)
                && Scope() == $"Disabled mods ({off.Count})" && Choices() == "All Enabled Disabled* Changed", Say());
            Check("Show: Disabled: each category counts its disabled mods; none: no header", Headers(Off) == "", Headers(Off));
            Results.Add($"INFO headers under Disabled: {content.GetChildren().Count(c => c.IsInGroup("SpireModManagerCategoryHeader") && c.Name != "Uncategorized")} "
                + $"of {Store.Data.Categories.Count} categories");

            // With a search: rows matching both, counted as the search's matches. "symphony" finds mods on (this one, Test
            // Fixes) and off (Test Retired, off in every run), so some rows show and some of its matches don't.
            await Find("symphony");
            var found = Where(m => SpireModManager.Search.Matches(m, "symphony"));
            var both = Where(m => Off(m) && SpireModManager.Search.Matches(m, "symphony"));
            Check("Show: Disabled with a search: the rows matching both, counted as search matches", both.Count > 0 && both.Count < found.Count
                && Shown().SequenceEqual(both) && Scope() == $"Search matches ({both.Count})" && Choices() == "All Enabled Disabled* Changed",
                $"{Say()}; expected {string.Join(", ", both)} of the search's {string.Join(", ", found)}");
            await Find("");

            CategoryList.Redraw(content);
            await Wait(menu, 0.3);
            Check("Show: Disabled outlasts a redraw", Shown().SequenceEqual(off) && Choices() == "All Enabled Disabled* Changed", Say());

            await Press("ShowChangedButton");
            await Press("ShowAllButton");
        }
        finally
        {
            History.Changed -= Moved;
        }
        Check("choosing is no undo step, not in Changes, not pending, no unsaved profile edit", moves == 0 && State() == before,
            $"history moved {moves}, same state {State() == before}");

        // A mod unticked under Enabled: its row stays where it was, counted, through a redraw that changes no mod too (a
        // search, a fold), until Enabled is pressed again. (Undone after: the untick is a step.)
        await Press("ShowEnabledButton");
        if (Rows().FirstOrDefault(r => r.Visible && r.Mod is { } m && On(m) && m.manifest?.id is { } id && id != ModEntry.ModId && id != Bmm.ModId)
            is { Mod: { } ticked } row)
        {
            var count = Scope();
            var tickbox = row.GetNode<NTickbox>("Tickbox");
            tickbox.IsTicked = false;
            tickbox.EmitSignal(NTickbox.SignalName.Toggled, tickbox);
            await Wait(menu, 0.3);
            Check("Show: Enabled, a mod unticked: its row stays, still counted", Off(ticked) && row.Visible && Scope() == count,
                $"{ticked.manifest?.id} on {On(ticked)}, shown {row.Visible}; {count} -> {Scope()}");
            await Capture(screen, outDir, "2h-show-enabled-kept");
            CategoryList.Apply(content);
            await Wait(menu, 0.3);
            Check("... and after a redraw that changes no mod, its category counting it", row.Visible && Scope() == count && Headers(m => On(m) || m == ticked) == "",
                $"shown {row.Visible}, {count} -> {Scope()}; {Headers(m => On(m) || m == ticked)}");
            await Press("ShowEnabledButton");
            Check("Enabled pressed again: the unticked mod goes", !row.Visible && Shown().SequenceEqual(Where(On)), Say());
            History.Undo();
            await Wait(menu, 0.3);
            Check("undone: enabled and shown again", On(ticked) && row.Visible && Shown().SequenceEqual(Where(On)), Say());
        }
        else
            Results.Add("SKIP list filters: no enabled mod to untick");

        // A row kept for one mod's change goes at once with a change to many mods, which shows the choice's rows whole:
        // under Enabled, a mod unticked, then undo (of a second one unticked after it), a profile switched to and back (to
        // the untick, its unsaved edit), or Disable all. (Each undone, or ticked again, after.)
        await Press("ShowEnabledButton");
        var onRows = Rows().Where(r => r.Visible && r.Mod is { } m && On(m) && m.manifest?.id is { } id && id != ModEntry.ModId && id != Bmm.ModId).ToList();
        if (onRows is [{ Mod: { } kept } keptRow, { Mod: { } later } laterRow, ..])
        {
            async Task Tick(NModMenuRow which, bool on)
            {
                var tickbox = which.GetNode<NTickbox>("Tickbox");
                tickbox.IsTicked = on;
                tickbox.EmitSignal(NTickbox.SignalName.Toggled, tickbox);
                await Wait(menu, 0.3);
            }
            await Tick(keptRow, false);
            await Tick(laterRow, false);
            var stayed = keptRow.Visible && laterRow.Visible;
            History.Undo(); // the second
            await Wait(menu, 0.3);
            Check("Show: Enabled, two mods unticked (both kept), the second undone: the first goes at once", stayed && Off(kept) && On(later)
                && !keptRow.Visible && Shown().SequenceEqual(Where(On)), $"kept {stayed}; {Say()}");
            History.Undo(); // the first
            await Wait(menu, 0.3);

            if (Profiles.Active is { } home && Store.Data.Profiles.FirstOrDefault(p => p != home) is { } other)
            {
                await Tick(keptRow, false);
                stayed = keptRow.Visible;
                Profiles.SwitchTo(other, screen);
                await Wait(menu, 0.3);
                Profiles.SwitchTo(home, screen); // back to the untick, the profile's unsaved edit
                await Wait(menu, 0.3);
                Check($"Show: Enabled, a mod unticked (kept), {other.Name} switched to and back: it goes at once", stayed && Off(kept)
                    && !keptRow.Visible && Shown().SequenceEqual(Where(On)), $"kept {stayed}; {Say()}");
                await Tick(keptRow, true);
            }
            else
                Results.Add("SKIP list filters, a kept row and a profile: one profile only");

            await Press("ShowEnabledButton");
            await Tick(keptRow, false);
            stayed = keptRow.Visible;
            await Press("DisableAllButton");
            Check("Show: Enabled, a mod unticked (kept), Disable all: it goes at once with the rest", stayed && Off(kept)
                && !keptRow.Visible && Shown().SequenceEqual(Where(On)), $"kept {stayed}; {Say()}");
            History.Undo(); // Disable all
            History.Undo(); // the untick
            await Wait(menu, 0.3);
            Check("undone: the mods on again", On(kept) && Shown().SequenceEqual(Where(On)), Say());
        }
        else
            Results.Add("SKIP list filters, kept rows: fewer than two enabled mods to untick");

        // Enable all under Disabled: just the mods shown, as one step; they're enabled, so they go (a change to many at once).
        await Press("ShowDisabledButton");
        var wereOff = Rows().Where(r => r.Visible).Select(r => r.Mod).OfType<Mod>().ToList();
        if (wereOff.Count > 0)
        {
            var others = Rows().Select(r => r.Mod).OfType<Mod>().Except(wereOff).ToDictionary(m => m, On);
            await Press("EnableAllButton");
            Check("Show: Disabled, Enable all: just the mods shown, which then go", wereOff.All(On) && others.All(p => On(p.Key) == p.Value)
                && Shown().Count == 0 && Scope() == "Disabled mods (0)", Say());
            History.Undo();
            await Wait(menu, 0.3);
            Check("undone: disabled and shown again", wereOff.All(Off) && Shown().SequenceEqual(Ids(Rows().Where(r => r.Mod is { } m && wereOff.Contains(m)))), Say());
        }
        else
            Results.Add("SKIP list filters, Enable all: no disabled mod");

        // A profile switched to under Enabled: the mods it turns off go at once, as for Enable all (its tickboxes fire no
        // toggle, so the switch redraws the list itself); switched back, they show again.
        await Press("ShowEnabledButton");
        if (Profiles.Active is { } start && Store.Data.Profiles.FirstOrDefault(p => p != start) is { } away)
        {
            var wereOn = Where(On);
            Profiles.SwitchTo(away, screen);
            await Wait(menu, 0.3);
            var nowOn = Where(On);
            Check($"Show: Enabled, switched to {away.Name}: the mods it turns off go at once, the bar and each category counting the rest",
                nowOn.Count < wereOn.Count && Shown().SequenceEqual(nowOn) && Scope() == $"Enabled mods ({nowOn.Count})" && Headers(On) == "",
                $"{wereOn.Count} on, then {nowOn.Count}; {Say()}; {Headers(On)}");
            Profiles.SwitchTo(start, screen);
            await Wait(menu, 0.3);
            Check("... and switched back: they show again", Where(On).SequenceEqual(wereOn) && Shown().SequenceEqual(wereOn), Say());
        }
        else
            Results.Add("SKIP list filters, profile: one profile only");

        // Changed: the mods the Changes list names (an edit made here to be sure of one; undone after).
        await Press("ShowAllButton");
        Store.EditMod("SmmTestUi", info => info.Notes = "List filter test");
        CategoryList.Edited(content);
        await Press("ShowChangedButton");
        var lines = HistoryBar.Describe();
        // Each line's mod: the one whose name it holds, the longest if several ("Better Mod Menu (local fix)" holds
        // "Better Mod Menu" too). Lines naming no mod (a switched profile, profiles) name none.
        var mods = Rows().Select(r => r.Mod).OfType<Mod>().ToList();
        var named = lines.Select(l => mods.Where(m => m.manifest?.name is { Length: > 0 } name && l.Contains(name)).MaxBy(m => m.manifest?.name?.Length))
            .OfType<Mod>().ToHashSet();
        var changed = Where(named.Contains);
        Check("Show: Changed: the mods Changes names, counted, Changed lit", Shown().SequenceEqual(changed) && changed.Contains("SmmTestUi")
            && Scope() == $"Changed mods ({changed.Count})" && Choices() == "All Enabled Disabled Changed*", $"{Say()}; Changes: {string.Join(" | ", lines)}");
        Check("Show: Changed: each category counts its changed mods; none: no header", Headers(named.Contains) == "", Headers(named.Contains));
        Results.Add($"INFO Changed shows {changed.Count} of {all.Count} mods");
        await Capture(screen, outDir, "2i-show-changed");
        History.Undo(); // the note
        await Wait(menu, 0.3);

        // Picked mods edited together in their menu keep their rows, as one mod's menu does: under Changed, a tag given to
        // two and taken off again in their bulk menu (as at launch) leaves their rows while it's open; they go when
        // Changed is pressed again.
        string[] pair = ["SmmTestUi", "SmmTestContent"];
        bool InPair(Mod? mod) => pair.Contains(mod?.manifest?.id ?? "");
        if (Rows().Count(r => InPair(r.Mod)) != 2 || HistoryBar.ChangedMods().Any(InPair))
            Results.Add("SKIP list filters, bulk menu: the test mods missing, or changed already");
        else
        {
            Store.Batch(() =>
            {
                foreach (var id in pair)
                    Store.EditMod(id, info => info.Tags = ["filtered"]);
            });
            CategoryList.Edited(content);
            await Wait(menu, 0.3);
            var pairRows = Rows().Where(r => InPair(r.Mod)).ToList();
            Selection.Add(content, pairRows);
            if (pairRows.All(r => r.Visible) && CategoryList.ShowBulkMenu(content) is { } bulk
                && bulk.FindChild("Tags", true, false)?.GetNodeOrNull<LineEdit>("Box") is { } tags)
            {
                tags.Text = "";
                tags.EmitSignal(Control.SignalName.FocusExited);
                await Wait(menu, 0.3);
                Check("Show: Changed, a tag taken off two picked mods in their menu (as at launch): their rows stay while it's open",
                    pair.All(id => Store.InfoFor(id)?.Tags == null) && !HistoryBar.ChangedMods().Any(InPair) && pairRows.All(r => r.Visible)
                    && MenuPanel.IsOpen, Say());
            }
            else
                Check("Show: Changed, two mods given a tag: shown, and their bulk menu opens", false, Say());
            MenuPanel.Close();
            Selection.Clear(content);
            await Press("ShowChangedButton");
            Check("Changed pressed again: they go", pairRows.All(r => !r.Visible), Say());
        }

        // Going to a mod the choice leaves out (a name in the panel, a banner's Show) shows its row; the choice stays.
        await Press("ShowDisabledButton");
        if (Rows().FirstOrDefault(r => r.Mod?.manifest?.id == ModEntry.ModId) is { } ours)
        {
            var hidden = !ours.Visible;
            CategoryList.Reveal(content, ModEntry.ModId, flash: false);
            await Wait(menu, 0.5);
            Check("Show: Disabled, going to an enabled mod: its row shows too, Disabled stays", hidden && ours.Visible
                && Choices() == "All Enabled Disabled* Changed" && Shown().SequenceEqual(Where(m => Off(m) || m == ours.Mod)), Say());
            await Press("ShowDisabledButton");
            Check("Disabled pressed again: it goes", !ours.Visible && Shown().SequenceEqual(Where(Off)), Say());
        }

        // Load Order and Vanilla List: every row, no page bar; the main list's choice is still there after.
        Click(screen.GetNode<NButton>("SpireModManagerLoadOrderToggle"));
        await Wait(menu, 0.5);
        Check("Load Order: every row, no page bar, whatever the main list shows", Layout.Current == Layout.View.LoadOrder
            && Shown().SequenceEqual(all) && Bar() == null, $"{Layout.Current}, shown {Shown().Count} of {all.Count}, bar {Bar() != null}");
        Click(screen.GetNode<NButton>("SpireModManagerLoadOrderToggle"));
        await Wait(menu, 0.5);
        Check("back on the main list: still Disabled", Layout.Current == Layout.View.Categories && Choices() == "All Enabled Disabled* Changed"
            && Shown().SequenceEqual(Where(Off)), Say());
        if (!Bmm.Present) // its page shows its own groups
            Check("Vanilla List: every row, whatever the main list shows", await WithVanillaView(screen, menu, () => Shown().SequenceEqual(all)));

        await Press("ShowAllButton");
        Check("All: every row again, counted as installed mods", Shown().SequenceEqual(all) && Scope() == $"Installed mods ({all.Count})"
            && Choices() == "All* Enabled Disabled Changed", Say());

        // Going to a mod a search leaves out (a name in the panel, a line in Changes), as for a banner's Show: the search
        // clears, box and list, and its row shows; a search that finds it stays.
        if (Rows().FirstOrDefault(r => r.Mod?.manifest?.id == "SmmTestLib") is { } panelRow
            && Rows().FirstOrDefault(r => r.Mod?.manifest?.id == "SmmTestUi") is { Mod: { } linked } linkedRow)
        {
            CategoryList.Select(screen, panelRow);
            await Wait(menu, 0.3);
            var description = screen.GetNode<RichTextLabel>("%ModInfoContainer/ModDescription");
            string Filter() => content.HasMeta(CategoryList.FilterMeta) ? (string)content.GetMeta(CategoryList.FilterMeta) : "";
            string Said() => $"box \"{box.Text}\", filter \"{Filter()}\", shown {linkedRow.Visible}";
            const string misses = "SmmTestLib", finds = "SmmTest";
            await Find(misses);
            var hidden = !linkedRow.Visible && !SpireModManager.Search.Matches(linked, misses);
            description.EmitSignal(RichTextLabel.SignalName.MetaClicked, "mod:SmmTestUi");
            await Wait(menu, 0.3);
            Check("Show: All, a search that leaves a mod out: its name in the panel clears the search, box and list, and shows its row",
                hidden && box.Text == "" && Filter() == "" && linkedRow.Visible, $"hidden by the search {hidden}; {Said()}");
            await Find(finds);
            description.EmitSignal(RichTextLabel.SignalName.MetaClicked, "mod:SmmTestUi");
            await Wait(menu, 0.3);
            Check("... a search that finds it stays", box.Text == finds && Filter() == finds && linkedRow.Visible, Said());
            await Find(misses);
            hidden = !linkedRow.Visible;
            HistoryBar.OpenFromList(screen, "SmmTestUi");
            await Wait(menu, 0.3);
            Check("... and its line in Changes clears it too, the mod in the panel", hidden && box.Text == "" && Filter() == ""
                && linkedRow.Visible && CategoryList.Selected == linked, $"hidden by the search {hidden}; {Said()}; panel {CategoryList.Selected?.manifest?.id}");
            HistoryBar.HideBack(screen.GetNode<Control>("%ModInfoContainer"));
            await Find("");
        }
        else
            Results.Add("SKIP list filters, going to a mod through a search: the test mods missing");
    }

    // The page opening again: the choice stays, and the rows it kept for a change go by it again.
    static async Task TestListFilterReopen(Node menu, NModdingScreen screen, Control content)
    {
        if (Layout.Current != Layout.View.Categories
            || content.GetChildren().OfType<NModMenuRow>().FirstOrDefault(r => r.Mod?.manifest?.id == ModEntry.ModId) is not { } ours)
        {
            Results.Add("SKIP list filter reopen: not on the main list");
            return;
        }
        string Lit() => string.Join(" ", screen.FindChild(PageBar.Name, true, false)?.FindChild("Show", true, false)?.GetChildren().OfType<Button>()
            .Where(b => b.GetThemeColor("font_color") == Colors.White).Select(b => b.Text) ?? []);
        ListFilter.Choose(content, ListFilter.Choice.Disabled);
        CategoryList.Reveal(content, ModEntry.ModId, flash: false); // kept beside the disabled ones
        await Wait(menu, 0.5);
        var kept = ours.Visible;
        screen.Visible = false;
        screen.Visible = true;
        await Wait(menu, 0.5);
        Check("the page opening again: Disabled still chosen and lit, a row kept for a change gone", kept && Lit() == "Disabled"
            && ListFilter.Current == ListFilter.Choice.Disabled && !ours.Visible, $"kept {kept}, lit {Lit()}, shown {ours.Visible}");
        ListFilter.Choose(content, ListFilter.Choice.All);
    }

    // Relaunch with mods that won't load (fixture: Test Addon needs SmmTestMissing, never installed; here the framework
    // is turned off too): the dialog lists them; Enable needed mods, then Disable mods that can't load, fix
    // them in place; Cancel and Esc close it. Never presses Relaunch (that would quit the test).
    static async Task TestRelaunchCheck(Node menu, NModdingScreen screen, Control content, string outDir)
    {
        if (content.GetChildren().OfType<NModMenuRow>().FirstOrDefault(r => r.Mod?.manifest?.id == "SmmTestLib") is not { Mod: { } lib } libRow
            || WontLoad() is not { } addon)
        {
            Results.Add("SKIP relaunch check: fixture mods missing");
            return;
        }
        var tickbox = libRow.GetNode<NTickbox>("Tickbox");
        tickbox.IsTicked = false;
        tickbox.EmitSignal(NTickbox.SignalName.Toggled, tickbox);
        await Wait(menu, 0.3);
        Node? Dialog() => menu.GetTree().Root.GetNodeOrNull(RelaunchCheck.Name);
        string Body() => (Dialog()?.FindChild("Body", true, false) as RichTextLabel)?.GetParsedText() ?? "";
        void Press(string name) => Dialog()?.FindChild(name, true, false)?.EmitSignal(BaseButton.SignalName.Pressed);

        RelaunchCheck.Show(screen);
        await Wait(menu, 0.3);
        Check("relaunch check lists the mods that won't load", Body().Contains(Missing + ", which isn't installed") && Body().Contains("Test Framework, which is disabled"), Body());
        await Capture(screen, outDir, "15-relaunch-check");
        Press("EnableNeededButton");
        await Wait(menu, 0.3);
        Check("Enable needed mods fixes the off ones, the list updates", Dependencies.IsOn(lib) && !Body().Contains("Test Framework") && Body().Contains(Missing), Body());
        Press("DisableBrokenButton");
        await Wait(menu, 0.3);
        Check("Disable mods that can't load: nothing left, plain Relaunch", !Dependencies.IsOn(addon)
            && Dialog()?.FindChild("RelaunchNowButton", true, false) is Button { Text: "Relaunch Game" }, Body());
        Press("CancelRelaunchButton");
        await Wait(menu, 0.2);
        Check("Cancel closes it", Dialog() == null);
        History.Undo(); // Test Addon back on, as the fixture has it
        await Wait(menu, 0.3);
        RelaunchCheck.Show(screen);
        await Wait(menu, 0.2);
        Input.ParseInputEvent(new InputEventKey { Keycode = Key.Escape, Pressed = true });
        await Wait(menu, 0.2);
        Input.ParseInputEvent(new InputEventKey { Keycode = Key.Escape, Pressed = false });
        Check("Esc closes it, and stays on the page", Dialog() == null && screen.IsVisibleInTree() && Dependencies.IsOn(addon));
    }

    // A made-up mod with very long everything (name, id, author, version, description, alias, many tags, notes),
    // shown in the list and the panel (screenshot 16-long-details). The panel's title stays on one line with its
    // version and the source line keeps its status, both cut with "…" (the whole text a second over them); the
    // row keeps its author, the tags that don't fit folded into "+N". A redraw fits no row title again, its cut one
    // and an aliased one included, in Vanilla View and in our main list. It's removed again afterwards.
    static async Task TestLongDetails(Node menu, NModdingScreen screen, Control content, string outDir)
    {
        const string id = "SmmVeryLongModIdentifierThatGoesOnAndOnForTheSakeOfTesting";
        const string name = "The Extraordinarily Long-Named Compendium of Every Card, Relic, Potion and Event Ever Imagined (Deluxe Edition)";
        var words = "lorem ipsum dolor sit amet consectetur adipiscing elit sed do eiusmod tempor incididunt ut labore et dolore magna aliqua".Split(' ');
        string Text(int count) => string.Join(' ', Enumerable.Range(0, count).Select(i => words[i % words.Length]));
        var mod = new Mod
        {
            path = "", modSource = ModSource.ModsDirectory, state = ModLoadState.AddedAtRuntime,
            manifest = new ModManifest
            {
                id = id, name = name,
                author = "A Collective of Very Enthusiastic Modders With An Unusually Long Team Name", version = "12.345.6789-beta.12+build.98765",
                description = Text(220), minGameVersion = "0.111.0",
            },
        };
        GameInstalls(mod);
        await Wait(menu, 0.4);
        NModalContainer.Instance?.Clear();
        Store.EditMod(id, info =>
        {
            info.Alias = "My favourite enormous content pack that I keep meaning to rename to something shorter";
            info.Notes = Text(120);
            info.Tags = ["content", "big", "really-long-tag-name-for-testing", "favourites", "co-op", "needs-checking-after-every-update", "deluxe",
                "cards", "relics", "events", "potions", "late-game"];
        });
        CategoryList.Apply(content);
        await Wait(menu, 0.3);
        if (content.GetChildren().OfType<NModMenuRow>().FirstOrDefault(r => r.Mod == mod) is { } row)
        {
            CategoryList.Select(screen, row);
            CategoryList.Reveal(content, id, flash: false);
            await Wait(menu, 0.5);
            await Capture(screen, outDir, "16-long-details");
            var panel = screen.GetNode<Control>("%ModInfoContainer");
            var title = panel.GetNode<RichTextLabel>("ModTitle");
            var titleText = title.GetParsedText();
            var titleSize = title.GetThemeFontSize("normal_font_size");
            Check("long details: the panel shows the made-up mod (see 16-long-details)", titleText.StartsWith("The Extraordinarily"));
            Check("long details: the panel's title on one line at 24 or more, the name cut with \"…\", the version showing",
                title.GetLineCount() == 1 && titleSize >= 24 && titleText.Contains('…') && titleText.Contains("12.345")
                && title.GetContentWidth() <= title.Size.X + 1 && panel.GetGlobalRect().Encloses(title.GetGlobalRect()),
                $"{title.GetLineCount()} lines at {titleSize}, {title.GetContentWidth()} of {title.Size.X}: {titleText}");
            var source = panel.GetNode<Control>("SpireModManagerSource");
            var link = source.GetNode<LinkButton>("SpireModManagerLink");
            var status = (Label)source.FindChild("Status", true, false);
            Check("long details: the source line stops short of the thumbnail, the id cut with \"…\", the dot and status showing",
                link.Text.StartsWith("SmmVeryLong") && link.Text.Contains('…') && link.Text.EndsWith("(Local)") && status.Text.Length > 0
                && status.GetGlobalRect().End.X <= title.GetGlobalRect().End.X + 2 && panel.GetGlobalRect().Encloses(status.GetGlobalRect()),
                $"\"{link.Text}\" · {status.Text}: status {status.GetGlobalRect()}, title {title.GetGlobalRect()}");
            // A second over the cut title or id: the whole of it, in a plain tooltip.
            async Task<string> Tooltip(Control target, string card, double seconds)
            {
                Hover.TestMouse = () => target.GetGlobalRect().GetCenter();
                target.EmitSignal(Control.SignalName.MouseEntered);
                await Wait(menu, seconds);
                var text = screen.GetNodeOrNull(card)?.FindChildren("*", "RichTextLabel", true, false).OfType<RichTextLabel>().FirstOrDefault()?.GetParsedText() ?? "";
                Hover.TestMouse = () => new Vector2(5, 5);
                target.EmitSignal(Control.SignalName.MouseExited);
                await Wait(menu, 0.1);
                return text;
            }
            var wholeTitle = await Tooltip(title, "SpireModManagerFullText", Hover.SlowDelay + 0.2);
            var wholeId = await Tooltip(link, "SpireModManagerFullText", Hover.SlowDelay + 0.2);
            Check("long details: a second over the cut title or id shows the whole of it", wholeTitle.StartsWith(name) && wholeTitle.EndsWith("12.345.6789-beta.12+build.98765")
                && wholeId == id, $"{wholeTitle} / {wholeId}");

            var longAuthor = row.GetNodeOrNull<Label>("SpireModManagerStrip/Author");
            var longTag = row.GetNodeOrNull("SpireModManagerStrip")?.GetChildren().OfType<PanelContainer>().Select(c => c.GetChildOrNull<Label>(0)).FirstOrDefault(l => l?.Text.StartsWith("really-long") == true);
            Check("long details: the row's author and a long tag are cut with an ellipsis", longAuthor is { ClipText: true, TextOverrunBehavior: TextServer.OverrunBehavior.TrimEllipsis } && longTag is { ClipText: true });
            // Many tags: the line ends short of the row's buttons, with the author in it; the tags that don't fit
            // are folded into "+N", whose card lists them.
            var strip = row.GetNode<Control>("SpireModManagerStrip");
            var lineEnd = row.GetGlobalRect().End.X + strip.OffsetRight;
            Check("long details: many tags: the author still shows, inside the line", longAuthor is { Visible: true } && longAuthor.GetGlobalRect().End.X <= lineEnd + 1
                && strip.GetGlobalRect().End.X <= lineEnd + 1, $"author {longAuthor?.GetGlobalRect()}, line {strip.GetGlobalRect()}, ends at {lineEnd}");
            var folded = strip.GetChildren().OfType<PanelContainer>().Where(c => !c.Visible).Select(c => c.GetChildOrNull<Label>(0)?.Text ?? "").ToList();
            var more = strip.GetNodeOrNull<Control>(RowStrip.MoreName);
            var moreCard = more == null ? "" : await Tooltip(more, "SpireModManagerTagCard", Hover.Delay + 0.15);
            Check("long details: the tags that don't fit fold into \"+N\", its card listing them", more is { Visible: true } && folded.Count > 0
                && more.GetChildOrNull<Label>(0)?.Text == $"+{folded.Count}" && folded.Last() == "late-game"
                && moreCard.Split('\n').SequenceEqual(folded), $"{folded.Count} folded, card: {moreCard.ReplaceLineEndings(", ")}");
            Hover.TestMouse = null;
            Check("rows whose tags fit show them all, no \"+N\"", content.GetChildren().OfType<NModMenuRow>().Where(r => r != row && r.Visible)
                .All(r => r.GetNodeOrNull<Control>("SpireModManagerStrip") is not { } line
                    || (line.GetNodeOrNull(RowStrip.MoreName) == null && line.GetChildren().OfType<PanelContainer>().All(c => c.Visible))));
            // Load Order and back: the view switch sets the title's size to 32 again, then fills the panel again. The
            // title is drawn at the size it was fitted at before, not left at 32 (on two lines).
            Click(screen.GetNode<NButton>("SpireModManagerLoadOrderToggle"));
            await Wait(menu, 0.4);
            Click(screen.GetNode<NButton>("SpireModManagerLoadOrderToggle"));
            await Wait(menu, 0.5);
            var refitSize = title.GetThemeFontSize("normal_font_size");
            Check("long details: after Load Order and back, the panel's title is drawn at the size it was fitted at, on one line",
                title.GetParsedText() == titleText && refitSize == titleSize && title.GetLineCount() == 1 && title.GetContentWidth() <= title.Size.X + 1,
                $"{title.GetLineCount()} lines at {refitSize} (was {titleSize}), {title.GetContentWidth()} of {title.Size.X}: {title.GetParsedText()}");
            // Vanilla View: the game's auto-size shrinks the long name in the panel's title and in the row's (from 60 and
            // 32). A redraw and a re-layout there write those sizes straight in again, the text unchanged: the titles keep
            // their fitted sizes. The redraw leaves the row titles as they are, each at the size fitted for its text and
            // box (the long one too): none has a size written or is fitted again. (With Better Mod Menu, its page styles
            // them its own way.)
            if (!Bmm.Present)
            {
                var rowTitle = row.GetNode<RichTextLabel>("Title");
                (int Panel, int Row) Sizes() => (title.GetThemeFontSize("normal_font_size"), rowTitle.GetThemeFontSize("normal_font_size"));
                Click(screen.GetNode<NButton>("SpireModManagerViewToggle"));
                await Wait(menu, 0.5);
                var fitted = Sizes();
                var refitted = new List<string>();
                Layout.TestFitting = label =>
                {
                    if (label.GetParent() is NModMenuRow fittedRow)
                        refitted.Add(fittedRow.Mod?.manifest?.id ?? "?");
                };
                CategoryList.Apply(content);
                Layout.Apply(screen);
                await Wait(menu, 0.3);
                Layout.TestFitting = null;
                var again = Sizes();
                Click(screen.GetNode<NButton>("SpireModManagerViewToggle"));
                await Wait(menu, 0.5);
                Check("long details: in Vanilla View the long name is fitted in the panel's title and the row's, and stays so after a redraw and a re-layout",
                    fitted is (< 60, < 32) && again == fitted, $"panel {fitted.Panel}, row {fitted.Row}; after {again.Panel}, {again.Row}");
                Check("long details: a redraw in Vanilla View fits no row title again, the long one included (each stays at its own fitted size)",
                    refitted.Count == 0, $"{refitted.Count} fitted again: {string.Join(", ", refitted.Distinct().Take(8))}");
            }
            // Our main list: the same for a title with an alias (the alias, the name beside it) or cut with "…" (the long
            // one). Apply put the plain name back while the auto-size was off and Fit set the whole text again, so the
            // label fitted itself once more on every redraw: its own fitting, which TestFitting doesn't see, so a title
            // whose text or size changes during the redraw counts too (read before the titles are fitted, deferred, and
            // after).
            const string aliased = "SmmTestUi";
            var aliasWas = Store.InfoFor(aliased)?.Alias;
            Store.EditMod(aliased, info => info.Alias = "UI bits");
            CategoryList.Reveal(content, aliased, flash: false);
            CategoryList.Apply(content);
            await Wait(menu, 0.3);
            var titles = content.GetChildren().OfType<NModMenuRow>().Select(r => (Id: r.Mod?.manifest?.id ?? "?", Label: r.GetNode<RichTextLabel>("Title"))).ToList();
            List<string> Titles() => titles.Select(t => $"{t.Id}: {t.Label.Text} at {t.Label.GetThemeFontSize("normal_font_size")}").ToList();
            var settled = Titles();
            var fittedAgain = new List<string>();
            Layout.TestFitting = label =>
            {
                if (label.GetParent() is NModMenuRow fittedRow)
                    fittedAgain.Add(fittedRow.Mod?.manifest?.id ?? "?");
            };
            CategoryList.Apply(content);
            var during = Titles();
            await Wait(menu, 0.3);
            Layout.TestFitting = null;
            var changed = during.Concat(Titles()).Except(settled).ToList();
            Check("long details: a second redraw in our main list fits no row title again, aliased and cut ones included",
                fittedAgain.Count == 0 && changed.Count == 0,
                $"{fittedAgain.Count} fitted again: {string.Join(", ", fittedAgain.Distinct().Take(8))}; changed: {string.Join(" | ", changed.Take(4))}");
            var aliasedTitle = titles.Where(t => t.Id == aliased).Select(t => t.Label).FirstOrDefault();
            Check("long details: after it, the aliased title still shows the alias with the name beside it, at the rows' size",
                aliasedTitle != null && aliasedTitle.GetParsedText().StartsWith("UI bits") && aliasedTitle.GetParsedText().Contains("Test UI")
                && aliasedTitle.GetThemeFontSize("normal_font_size") == Layout.RowTitleSize,
                $"{aliasedTitle?.GetParsedText()} at {aliasedTitle?.GetThemeFontSize("normal_font_size")}");
            Store.EditMod(aliased, info => info.Alias = aliasWas);
            content.RemoveChild(row);
            row.QueueFree();
        }
        else
            Results.Add("FAIL long details: no row for the made-up mod");
        GameForgets(mod);
        Store.EditMod(id, info => (info.Alias, info.Notes, info.Tags, info.Category) = (null, null, null, null));
        SaveManager.Instance.SettingsSave.ModSettings?.ModList.RemoveAll(e => e.Id == id);
        History.Record();
        CategoryList.Redraw(content);
        screen.GetNode<NModInfoContainer>("%ModInfoContainer").Clear();
        await Wait(menu, 0.3);
    }

    // A Workshop mod's head (made-up details; the sandbox has no Steam): Author, Updated (how long ago, and a link
    // to its change notes on the Workshop), Compatibility. "Ago" in hours under a day, singular for one. Dates and
    // numbers in the game's language.
    static async Task TestWorkshopHead(Node menu, NModdingScreen screen)
    {
        var mod = new Mod
        {
            path = "", modSource = ModSource.SteamWorkshop, workshopId = 42, state = ModLoadState.AddedAtRuntime,
            manifest = new ModManifest { id = "SmmWorkshopTest", name = "Workshop Test", author = "Someone Careful" },
        };
        Installed.Detected(mod); // so the panel can find it by id
        WorkshopInfo.TestSet(42, new WorkshopInfo.Details(DateTime.Now.AddDays(-30), DateTime.Now.AddDays(-4), [], [], 1_572_864, 1234)); // 1.5 MB
        var info = screen.GetNode<NModInfoContainer>("%ModInfoContainer");
        info.Fill(mod);
        await Wait(menu, 0.3);
        var head = info.GetNodeOrNull<RichTextLabel>("SpireModManagerHead");
        var text = head?.GetParsedText() ?? "";
        Check("a Workshop mod's head: Author, Updated (4 days ago, Patch notes), Compatibility", text.IndexOf("Author") < text.IndexOf("Updated")
            && text.IndexOf("Updated") < text.IndexOf("Compatibility") && text.Contains("(4 days ago)  Patch notes") && head?.Text.Contains("[url=patchnotes]") == true, text);
        var link = info.FindChild("SpireModManagerLink", true, false) as LinkButton;
        Check("the source line: id (SteamID n)", link?.Text == "SmmWorkshopTest (SteamID 42)", link?.Text ?? "");
        Check("how long ago: hours under a day, one day singular, green under two days",
            ModDetails.Ago(DateTime.Now.AddMinutes(-20)).Contains("(less than an hour ago)") && ModDetails.Ago(DateTime.Now.AddHours(-3)).Contains("(3 hours ago)") && ModDetails.Ago(DateTime.Now.AddMinutes(-70)).Contains("(1 hour ago)")
            && ModDetails.Ago(DateTime.Now.AddHours(-30)).Contains("(1 day ago)") && ModDetails.Ago(DateTime.Now.AddHours(-30)).Contains("#a6e3a1")
            && !ModDetails.Ago(DateTime.Now.AddDays(-3)).Contains("#a6e3a1"));

        // Dates, sizes and counts follow the game's language: in English as they always read; in German, the game's
        // German date pattern and separators (our words stay English there, as we have no German text).
        var english = info.GetNode<RichTextLabel>("ModDescription").GetParsedText();
        Check("in English: October 11, 2026; Size: 1.5 MB; Subscribers: 1,234", ModDetails.Date(new DateTime(2026, 10, 11)) == "October 11, 2026"
            && english.Contains("Size: 1.5 MB") && english.Contains("Subscribers: 1,234"), english);
        var language = LocManager.Instance.Language;
        try
        {
            LocManager.Instance.SetLanguage("deu"); // the game's own switch, without the save or the menu rebuild
            info.Fill(mod);
            var german = info.GetNode<RichTextLabel>("ModDescription").GetParsedText();
            Check("with the game in German: 11. Oktober 2026; 1,5 MB; 1.234", ModDetails.Date(new DateTime(2026, 10, 11)) == "11. Oktober 2026"
                && german.Contains("1,5 MB") && german.Contains("1.234"), $"{ModDetails.Date(new DateTime(2026, 10, 11))} / {german}");
            var longAgo = ModDetails.Ago(DateTime.Now.AddDays(-1234));
            Check("with the game in German, other counts too: (1.234 days ago)", longAgo.Contains("(1.234 days ago)"), longAgo);
            // A date pattern that doesn't read (an unclosed quote; SetLanguage below reloads the tables without it):
            // all in English, not a German month in the English pattern.
            LocManager.Instance.GetTable("run_history").MergeWith(new() { ["INFO.DATE_FORMAT"] = "d 'MMMM" });
            Check("a broken date pattern in German: October 11, 2026", ModDetails.Date(new DateTime(2026, 10, 11)) == "October 11, 2026", ModDetails.Date(new DateTime(2026, 10, 11)));
        }
        finally
        {
            LocManager.Instance.SetLanguage(language);
        }
        WorkshopInfo.TestSet(42, null);
        Installed.Forget(mod);
        info.Clear();
    }

    // Another mod's part in the info panel (Undo And Restart adds a settings panel at a fixed (0,318), over our
    // fields; here a made-up one the same size): our views put it between the details and our fields, or out of sight
    // when there's no room (never hidden: its Visible is its mod's); Vanilla View leaves it where its mod put it. Its
    // mod's own hide holds, in a fill or outside one; its show too when there's room; and behind the changes list too.
    // Removed again afterwards.
    static async Task TestOtherModPanel(Node menu, NModdingScreen screen, Control content, string outDir)
    {
        if (content.GetChildren().OfType<NModMenuRow>().FirstOrDefault(r => r.Mod?.manifest?.id == "SmmTestUi") is not { Mod: { } mod } row)
        {
            Results.Add("SKIP another mod's panel: test mods not installed");
            return;
        }
        var info = screen.GetNode<NModInfoContainer>("%ModInfoContainer");
        CategoryList.Select(screen, row); // a short description and no picture, so it fits
        await Wait(menu, 0.3);
        var home = new Vector2(0, 318);
        var other = new PanelContainer { Name = "OtherModSettingsPanel", Position = home, CustomMinimumSize = new Vector2(560, 174) };
        info.AddChild(other);
        var (details, yours) = (info.GetNode<Control>("ModDescription"), info.GetNode<Control>("SpireModManagerYours"));
        // shownByItsMod: what its mod sets in its own Fill postfix.
        async Task<string> Refill(bool? shownByItsMod = null)
        {
            info.Fill(mod); // the panel filled again, as when a mod is picked
            if (shownByItsMod is { } shown)
                other.Visible = shown;
            await Wait(menu, 0.3);
            return $"panel {other.GetGlobalRect()} shown {other.Visible}, details {details.GetGlobalRect()}, fields {yours.GetGlobalRect()}";
        }
        // No room: above the screen and out of keyboard reach, still shown as far as its mod can tell.
        bool OutOfSight() => other.Visible && other.GetGlobalRect().End.Y < 0 && other.FocusBehaviorRecursive == Control.FocusBehaviorRecursiveEnum.Disabled;

        var where = await Refill();
        Check("another mod's panel: the only part counted as another mod's (not the game's, ours or Better Mod Menu's)",
            Layout.OtherParts(info).SequenceEqual([other]), string.Join(", ", Layout.OtherParts(info).Select(c => c.Name)));
        var (panel, text, fields) = (other.GetGlobalRect(), details.GetGlobalRect(), yours.GetGlobalRect());
        Check("another mod's panel goes between the details and our fields, overlapping neither", other.Visible
            && !panel.Intersects(text) && !panel.Intersects(fields) && panel.Position.Y >= text.End.Y && panel.End.Y <= fields.Position.Y, where);
        await Capture(screen, outDir, "17-other-mod-panel");

        other.CustomMinimumSize = new Vector2(560, 600);
        where = await Refill();
        Check("another mod's panel with no room for it: out of sight, our fields right under the details", OutOfSight()
            && Math.Abs(yours.OffsetTop - details.OffsetBottom - ModDetails.FieldsGap) < 1, where);
        var inVanilla = "";
        Check("another mod's panel in Vanilla View: back where its mod put it, shown, in keyboard reach", await WithVanillaView(screen, menu, () =>
        {
            inVanilla = $"{other.Position}, shown {other.Visible}, focus {other.FocusBehaviorRecursive}";
            return other.Position == home && other.Visible && other.FocusBehaviorRecursive == Control.FocusBehaviorRecursiveEnum.Inherited;
        }), inVanilla);
        Check("another mod's panel back on our views: out of sight again", OutOfSight(), $"{other.GetGlobalRect()}, shown {other.Visible}");

        // Out of sight for lack of room, then hidden by its mod: outside a fill (a setting of its own, say), then in one
        // (another mod picked). Each time room comes back without a fill (as when the changes list closes), its mod's
        // hide holds. Its mod showing it again, with room: shown, in its place.
        foreach (var inFill in new[] { false, true })
        {
            other.CustomMinimumSize = new Vector2(560, 600);
            await Refill(shownByItsMod: true);
            if (inFill)
                await Refill(shownByItsMod: false);
            else
                other.Visible = false;
            other.CustomMinimumSize = new Vector2(560, 174);
            other.ResetSize();
            ModDetails.PlaceFields(info);
            Check($"another mod's panel out of sight, then hidden by its mod {(inFill ? "in" : "outside")} a fill: still hidden with room again",
                !other.Visible && !Layout.OtherParts(info).Contains(other), $"shown {other.Visible}, size {other.Size}");
        }
        where = await Refill(shownByItsMod: true);
        Check("another mod's panel shown again by its mod, with room: shown between the details and our fields, in keyboard reach", other.Visible
            && other.GetGlobalRect().Position.Y >= details.GetGlobalRect().End.Y && other.GetGlobalRect().End.Y <= yours.GetGlobalRect().Position.Y
            && other.FocusBehaviorRecursive == Control.FocusBehaviorRecursiveEnum.Inherited, where);

        // Behind the changes list: out of sight too (never hidden), so what its mod sets while the list is open holds
        // when it closes. Its mod hides it: still hidden after. Its mod shows it again: shown in its place after. An
        // overlay of another mod's (stretching over the panel) the same way, back over the panel when the list closes.
        var place = other.GetGlobalRect();
        var overlay = new Control { Name = "OtherModOverlay", MouseFilter = Control.MouseFilterEnum.Ignore };
        info.AddChild(overlay);
        overlay.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        var covers = overlay.GetGlobalRect();
        bool Behind(Control part) => part.GetGlobalRect().End.Y < 0 && part.FocusBehaviorRecursive == Control.FocusBehaviorRecursiveEnum.Disabled;
        bool InPlace(Control part, Rect2 rect) => part.GetGlobalRect().Position.DistanceTo(rect.Position) < 1 && part.GetGlobalRect().Size.DistanceTo(rect.Size) < 1
            && part.FocusBehaviorRecursive == Control.FocusBehaviorRecursiveEnum.Inherited;
        string Seen() => $"panel {other.GetGlobalRect()} shown {other.Visible} focus {other.FocusBehaviorRecursive} (was {place}), overlay {overlay.GetGlobalRect()} (was {covers})";
        HistoryBar.ShowList(screen);
        await Wait(menu, 0.2);
        Check("another mod's panel behind the changes list: out of sight and keyboard reach, shown as far as its mod can tell; its overlay too",
            HistoryBar.ListOpen(info) && other.Visible && Behind(other) && overlay.Visible && Behind(overlay), Seen());
        other.Visible = false; // its mod hides it
        HistoryBar.CloseList(info);
        await Wait(menu, 0.2);
        Check("another mod's panel hidden by its mod while the changes list was open: still hidden when it closes; its overlay back over the panel",
            !HistoryBar.ListOpen(info) && !other.Visible && InPlace(other, place) && overlay.Visible && InPlace(overlay, covers), Seen());
        HistoryBar.ShowList(screen);
        await Wait(menu, 0.2);
        other.Visible = true; // its mod shows it again
        await Wait(menu, 0.2);
        Check("another mod's panel shown by its mod while the changes list is open: still out of sight", HistoryBar.ListOpen(info) && other.Visible && Behind(other), Seen());
        HistoryBar.CloseList(info);
        await Wait(menu, 0.2);
        Check("and when the list closes: shown in its place, in keyboard reach", !HistoryBar.ListOpen(info) && other.Visible && InPlace(other, place), Seen());
        info.RemoveChild(overlay);
        overlay.QueueFree();

        info.RemoveChild(other);
        other.QueueFree();
        info.Clear();
    }

    // Several mods at once on the main list: Select all in Uncategorized, Ctrl-click one away; the bar counts
    // them; right-click one: the bulk menu ("Editing N selected mods"); a category for all (one undo step);
    // tags added to all and taken off again, each mod's other tags kept; Shift-click a range; Esc lets go.
    static async Task TestBulkEdit(Node menu, NModdingScreen screen, Control content, string outDir)
    {
        List<NModMenuRow> Rows() => content.GetChildren().OfType<NModMenuRow>().ToList();
        NModMenuRow? Row(string id) => Rows().FirstOrDefault(r => r.Mod?.manifest?.id == id);
        List<NModMenuRow> Uncategorized() => Rows().Where(r => Store.InfoFor(r.Mod?.manifest?.id)?.Category == null && r.Visible).ToList();
        void Click(NModMenuRow row, bool ctrl = false, bool shift = false) =>
            row.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, CtrlPressed = ctrl, ShiftPressed = shift });
        if (Row("SmmTestFixes") is not { } fixes || Uncategorized().Count < 3)
        {
            Results.Add("SKIP bulk edit: fixture mods missing");
            return;
        }
        Selection.Clear(content);
        if (CategoryList.ShowHeaderMenu(content, null, Uncategorized()) is { } headerMenu)
            MenuPanel.Press(headerMenu, "Select all");
        await Wait(menu, 0.2);
        var all = Uncategorized().Count;
        Check("a category's Select all picks its mods, tinted", Selection.Mods.Count == all
            && Uncategorized().All(r => r.GetNodeOrNull<Control>("SpireModManagerPicked") is { Visible: true }), $"{Selection.Mods.Count} of {all}");
        Click(fixes, ctrl: true);
        await Wait(menu, 0.2);
        var picked = Selection.Mods.Select(m => m.manifest?.id ?? "").ToList();
        Check("Ctrl-click lets go of one; the bar counts the rest", picked.Count == all - 1 && !picked.Contains("SmmTestFixes")
            && (screen.FindChild(PageBar.Name, true, false)?.FindChild("Scope", true, false) as Label)?.Text == $"Selected ({all - 1})");

        var bulk = CategoryList.ShowBulkMenu(content);
        await Wait(menu, 0.3);
        Check("the bulk menu: Editing N selected mods, tags and notes, no alias", bulk != null
            && bulk.FindChild("Editing", true, false) is Label { Text: var editing } && editing == $"Editing {all - 1} selected mods"
            && bulk.FindChild("Tags", true, false) != null && bulk.FindChild("Notes", true, false) != null && bulk.FindChild("Alias", true, false) == null);
        await Capture(screen, outDir, "18-bulk-menu");
        var tool = Store.Data.Categories.First().Name;
        if (bulk != null)
            MenuPanel.Press(bulk, tool);
        await Wait(menu, 0.3);
        Check("a category for all of them at once", picked.All(id => Store.InfoFor(id)?.Category == tool) && Store.InfoFor("SmmTestFixes")?.Category == null,
            string.Join(", ", picked.Select(id => $"{id}: {Store.InfoFor(id)?.Category}")));
        History.Undo();
        await Wait(menu, 0.3);
        Check("one undo puts them all back", picked.All(id => Store.InfoFor(id)?.Category == null));
        MenuPanel.Close();

        // Tags: one mod has its own; a tag added to all keeps it; taking the shared one off leaves it.
        Store.EditMod(picked[0], info => info.Tags = ["mine"]);
        if (CategoryList.ShowBulkMenu(content) is { } tagsMenu && tagsMenu.FindChild("Tags", true, false)?.GetNodeOrNull<LineEdit>("Box") is { } tags)
        {
            tags.Text = "together";
            tags.EmitSignal(Control.SignalName.FocusExited);
            await Wait(menu, 0.3);
            Check("a tag typed for several goes on every one, their own tags kept", picked.All(id => Store.InfoFor(id)?.Tags?.Contains("together") == true)
                && Store.InfoFor(picked[0])?.Tags?.Contains("mine") == true, string.Join(" | ", picked.Select(id => string.Join(",", Store.InfoFor(id)?.Tags ?? []))));
            tags.Text = "";
            tags.EmitSignal(Control.SignalName.FocusExited);
            await Wait(menu, 0.3);
            Check("taking the shared tag off removes it from all, nothing else", picked.All(id => Store.InfoFor(id)?.Tags?.Contains("together") != true)
                && Store.InfoFor(picked[0])?.Tags?.SequenceEqual(["mine"]) == true);
        }
        MenuPanel.Close();
        Store.EditMod(picked[0], info => info.Tags = null);

        // Shift-click: the rows between the last one picked and this one. Esc lets go.
        var rows = Uncategorized();
        Selection.Clear(content);
        Click(rows[0], ctrl: true);
        Click(rows[2], shift: true);
        await Wait(menu, 0.2);
        Check("Shift-click picks the range", Selection.Mods.Count >= 3 && rows.Take(3).All(r => Selection.Contains(r.Mod)), $"{Selection.Mods.Count}");
        Input.ParseInputEvent(new InputEventKey { Keycode = Key.Escape, Pressed = true });
        await Wait(menu, 0.2);
        Input.ParseInputEvent(new InputEventKey { Keycode = Key.Escape, Pressed = false });
        Check("Esc lets go of the picked mods, staying on the page", Selection.Mods.Count == 0 && screen.IsVisibleInTree()
            && rows.All(r => r.GetNodeOrNull<Control>("SpireModManagerPicked") is not { Visible: true }));
    }

    // Tag suggestions, with made-up tags on made-up mods so the counts are known (the data is put back after): the
    // panel's tags box lists your tags, most used first, at most 6; typing narrows them (starting with the word,
    // then containing it), leaving out the ones in the box; Down lights one, Enter, a comma or a click takes it;
    // Escape closes the list but stays in the box; nothing matching or leaving the box closes it. The bulk menu's
    // box too, the list drawn over the menu; Tab takes one there. Keys go through Godot's input, as a player's do.
    static async Task TestTagSuggestions(Node menu, NModdingScreen screen, Control content, string outDir)
    {
        NModMenuRow? Row(string id) => content.GetChildren().OfType<NModMenuRow>().FirstOrDefault(r => r.Mod?.manifest?.id == id);
        if (Row("SmmTestUi") is not { } ui || Row("SmmTestLib") is not { } lib || Row("SmmTestContent") is not { } other)
        {
            Results.Add("SKIP tag suggestions: test mods not installed");
            return;
        }
        var before = Store.EditsJson();
        Store.Batch(() =>
        {
            foreach (var info in Store.Data.Mods.Values)
                info.Tags = null;
            // Must-have twice on one mod, in two cases (as a hand-edited file can have it): still one mod, behind the
            // tags on two.
            Store.EditMod("SmmSuggestA", info => info.Tags = ["Strategy", "Story-heavy", "Must-have", "must-have", "Co-op"]);
            Store.EditMod("SmmSuggestB", info => info.Tags = ["Strategy", "Story-heavy", "Co-op"]);
            Store.EditMod("SmmSuggestC", info => info.Tags = ["Strategy", "Stable", "Fast", "Visuals", "Tools"]);
        });
        async Task Press(Key key, char unicode = '\0')
        {
            Input.ParseInputEvent(new InputEventKey { Keycode = key, Unicode = unicode, Pressed = true });
            await Wait(menu, 0.1);
            Input.ParseInputEvent(new InputEventKey { Keycode = key, Unicode = unicode, Pressed = false });
            await Wait(menu, 0.1);
        }
        async Task TypeIn(LineEdit box, string text)
        {
            box.Text = text;
            box.CaretColumn = text.Length;
            box.EmitSignal(LineEdit.SignalName.TextChanged, text);
            await Wait(menu, 0.2);
        }
        string Listed() => string.Join(", ", TagSuggestions.Labels());
        // Under the box (or above it, short of room below), as wide, on screen, on a layer over `over`.
        string Placement(LineEdit box, int over)
        {
            if (TagSuggestions.Current is not { } layer || layer.GetNodeOrNull<Control>("Panel") is not { } list)
                return "no list";
            var (shown, at) = (list.GetGlobalRect(), box.GetGlobalRect());
            var ok = (shown.Position.Y >= at.End.Y || shown.End.Y <= at.Position.Y) && Math.Abs(shown.Position.X - at.Position.X) < 1
                && Math.Abs(shown.Size.X - at.Size.X) < 1 && list.GetViewportRect().Encloses(shown) && layer.Layer > over;
            return ok ? "ok" : $"list {shown} on layer {layer.Layer}, box {at}";
        }

        CategoryList.Select(screen, ui);
        await Wait(menu, 0.5);
        var box = screen.GetNode<LineEdit>("%ModInfoContainer/SpireModManagerYours/Tags/Box");
        box.GrabFocus();
        await Wait(menu, 0.3);
        Check("tag suggestions: focusing the tags box lists your tags, on the most mods first (a mod counts once), at most 6", Listed() == "Strategy, Co-op, Story-heavy, Fast, Must-have, Stable", Listed());
        var placed = Placement(box, 90); // MenuPanel's layer
        Check("tag suggestions: under the box, as wide, on screen, over our menus", placed == "ok", placed);
        await TypeIn(box, "st");
        Check("tag suggestions: typing narrows them, tags starting with the word first, then ones containing it", Listed() == "Strategy, Story-heavy, Stable, Fast, Must-have", Listed());
        await Capture(screen, outDir, "19-tag-suggestions");
        await Press(Key.Down);
        Check("tag suggestions: Down lights the first", TagSuggestions.Highlighted == "Strategy", TagSuggestions.Highlighted ?? "none lit");
        await Press(Key.Enter);
        Check("tag suggestions: Enter takes it in place of the word, then \", \"; the list stays, without it", box.Text == "Strategy, " && box.HasFocus()
            && TagSuggestions.Current != null && !TagSuggestions.Labels().Contains("Strategy"), $"\"{box.Text}\": {Listed()}");
        await TypeIn(box, "Strategy, sto");
        await Press(Key.Down);
        await Press(Key.Comma, ',');
        Check("tag suggestions: a comma takes the lit one", box.Text == "Strategy, Story-heavy, ", box.Text);
        await Press(Key.Escape);
        Check("tag suggestions: Escape closes the list, staying in the box", TagSuggestions.Current == null && box.HasFocus() && box.Text == "Strategy, Story-heavy, ", box.Text);
        await TypeIn(box, "Strategy, Story-heavy, zzz");
        Check("tag suggestions: none when nothing matches", TagSuggestions.Current == null, Listed());
        await TypeIn(box, "Strategy, Story-heavy, f");
        Check("tag suggestions: typing brings the list back", Listed() == "Fast", Listed());
        TagSuggestions.Press("Fast");
        await Wait(menu, 0.2);
        Check("tag suggestions: a click takes one, the box keeps focus", box.Text == "Strategy, Story-heavy, Fast, " && box.HasFocus(), box.Text);
        box.ReleaseFocus();
        await Wait(menu, 0.3);
        Check("tag suggestions: leaving the box closes the list; the tags are saved, tidied", TagSuggestions.Current == null
            && Store.InfoFor("SmmTestUi")?.Tags?.SequenceEqual(["Strategy", "Story-heavy", "Fast"]) == true, string.Join(", ", Store.InfoFor("SmmTestUi")?.Tags ?? []));

        Selection.Clear(content);
        Selection.Add(content, [lib, other]);
        if (Selection.Mods.Count == 2 && CategoryList.ShowBulkMenu(content) is CanvasLayer bulk
            && bulk.FindChild("Tags", true, false)?.GetNodeOrNull<LineEdit>("Box") is { } bulkBox)
        {
            await Wait(menu, 0.3);
            bulkBox.GrabFocus();
            await TypeIn(bulkBox, "to");
            var overMenu = Placement(bulkBox, bulk.Layer);
            Check("tag suggestions: the bulk menu's tags box too, the list over the menu", Listed() == "Tools, Story-heavy" && overMenu == "ok", $"{Listed()}; {overMenu}");
            await Capture(screen, outDir, "19b-tag-suggestions-bulk");
            await Press(Key.Down);
            await Press(Key.Tab);
            Check("tag suggestions: Tab takes the lit one", bulkBox.Text == "Tools, " && bulkBox.HasFocus(), bulkBox.Text);
            MenuPanel.Close();
            await Wait(menu, 0.3);
            Check("tag suggestions: closing the menu takes the list away; the tag goes on both", TagSuggestions.Current == null
                && new[] { "SmmTestLib", "SmmTestContent" }.All(id => Store.InfoFor(id)?.Tags?.SequenceEqual(["Tools"]) == true),
                string.Join(" | ", new[] { "SmmTestLib", "SmmTestContent" }.Select(id => string.Join(",", Store.InfoFor(id)?.Tags ?? []))));
        }
        else
            Results.Add("FAIL tag suggestions: no bulk menu with a tags box");
        Selection.Clear(content);
        Store.RestoreEdits(before);
        CategoryList.Apply(content);
        ModDetails.Refresh(screen);
        await Wait(menu, 0.3);
    }

    // Untick a mod through its real tickbox, review, undo/redo; undo a rename; revert all, and undo that.
    static async Task TestHistory(Node menu, NModdingScreen screen, Control content, string outDir)
    {
        const string modId = "SmmTestUi";
        if (content.GetChildren().OfType<NModMenuRow>().FirstOrDefault(r => r.Mod?.manifest?.id == modId) is not { Mod: { } mod } row)
        {
            Results.Add("SKIP history: test mod not installed");
            return;
        }
        var tickbox = row.GetNode<NTickbox>("Tickbox");
        bool On() => !(SaveManager.Instance.SettingsSave.ModSettings?.IsModDisabled(mod) ?? false);
        void Press(string name) => screen.FindChild(name, true, false)?.EmitSignal(BaseButton.SignalName.Pressed);

        tickbox.IsTicked = false;
        tickbox.EmitSignal(NTickbox.SignalName.Toggled, tickbox); // the game's own toggle handler writes the list
        await Wait(menu, 0.3);
        Check("tickbox change is listed under Changes", !On() && HistoryBar.Describe().Any(l => l.Contains("Test UI")),
            string.Join(" | ", HistoryBar.Describe()));
        Check("Changes counts it", HistoryBar.Counts().Off >= 1, $"{HistoryBar.Counts()}");
        Check("a mod turned off since launch has a changed-colour title", row.GetNode<Control>("Title").Modulate == RowTitles.ChangedOffTitle);
        var changes = screen.GetNode<Control>(HistoryBar.ChangesName);
        var twoLines = (changes as RichTextLabel)?.GetParsedText() ?? "";
        Check("the two lines read \"Mods enabled N (+a -b)\" and \"Changes n  ↕m\"",
            System.Text.RegularExpressions.Regex.IsMatch(twoLines, @"^Mods enabled \d+ \(\+\d+ -\d+\)\nChanges \d+  ↕\d+$"), twoLines);
        Hover.TestMouse = () => changes.GetGlobalRect().GetCenter();
        changes.EmitSignal(Control.SignalName.MouseEntered);
        await Wait(menu, Hover.Delay + 0.15);
        Check("hovering Changes shows the details", screen.HasNode("SpireModManagerChangesPanel"));
        var summary = screen.GetNodeOrNull("SpireModManagerChangesPanel")?.FindChildren("*", "RichTextLabel", true, false).OfType<RichTextLabel>().FirstOrDefault()?.GetParsedText() ?? "";
        Check("the summary is titled and names the mod turned off", summary.StartsWith("Changes since launch") && summary.Contains("Test UI"), summary);
        await Capture(screen, outDir, "14-changes");
        changes.EmitSignal(Control.SignalName.MouseExited);
        Hover.TestMouse = null;
        await Wait(menu, 0.2);

        Press("UndoButton");
        await Wait(menu, 0.3);
        Check("undo turns it back on, tickbox included", On() && tickbox.IsTicked && row.GetNode<Control>("Title").Modulate != RowTitles.ChangedOffTitle);
        Press("RedoButton");
        await Wait(menu, 0.3);
        Check("redo turns it off again", !On() && !tickbox.IsTicked);

        if (Store.Data.Categories.FirstOrDefault() is { } category)
        {
            var name = category.Name;
            Categories.Rename(category, "Undo me");
            CategoryList.Apply(content);
            Press("UndoButton");
            await Wait(menu, 0.3);
            Check("undo restores a category name", Store.Data.Categories.Any(c => c.Name == name) && Header(content, name) != null);
        }

        // Keyboard: real key events through Godot's input pipeline.
        if (Store.Data.Categories.FirstOrDefault() is { } keyed)
        {
            var original = keyed.Name;
            Categories.Rename(keyed, "Ctrl Z me");
            CategoryList.Apply(content);
            Input.ParseInputEvent(new InputEventKey { Keycode = Key.Z, CtrlPressed = true, Pressed = true });
            await Wait(menu, 0.3);
            Check("Ctrl+Z undoes", Store.Data.Categories.Any(c => c.Name == original));
            Input.ParseInputEvent(new InputEventKey { Keycode = Key.Y, CtrlPressed = true, Pressed = true });
            await Wait(menu, 0.3);
            Check("Ctrl+Y redoes", Store.Data.Categories.Any(c => c.Name == "Ctrl Z me"));
            Input.ParseInputEvent(new InputEventKey { Keycode = Key.Z, CtrlPressed = true, Pressed = true });
            await Wait(menu, 0.3);
        }
        Input.ParseInputEvent(new InputEventKey { Keycode = Key.F, CtrlPressed = true, Pressed = true });
        await Wait(menu, 0.3);
        Check("Ctrl+F focuses search", screen.GetNode<Control>("ModsBorder/SpireModManagerSearch").HasFocus());
        screen.GetNode<Control>("ModsBorder/SpireModManagerSearch").ReleaseFocus();

        var beforeRevert = History.CurrentMods();
        History.RevertMods();
        await Wait(menu, 0.3);
        Check("Revert all restores the state from when the page opened", History.Entered?.Mods.SequenceEqual(History.CurrentMods()) == true
            && History.AtEntered && HistoryBar.Counts() is (0, 0, _) && On(),
            $"mods {History.Entered?.Mods.SequenceEqual(History.CurrentMods())}, at {History.AtEntered}, counts {HistoryBar.Counts()}, on {On()}");
        Press("UndoButton"); // the list view's history: on/off comes back; the order has its own history
        await Wait(menu, 0.3);
        Check("Revert all can be undone", beforeRevert.Select(m => (m.Id, m.Source, m.Enabled)).ToHashSet()
            .SetEquals(History.CurrentMods().Select(m => (m.Id, m.Source, m.Enabled))), string.Join(", ", History.CurrentMods()));
        // Undo leaves the panel as it was; the Changes text opens the full list, and closes it again. While it's
        // open, a tickbox click doesn't take the panel away.
        var info = screen.GetNode("%ModInfoContainer");
        Check("Undo doesn't open the changes list", !HistoryBar.ListOpen(info));
        void ClickChanges() => screen.GetNode<Control>(HistoryBar.ChangesName).EmitSignal(Control.SignalName.GuiInput,
            new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false });
        ClickChanges();
        await Wait(menu, 0.2);
        Check("clicking Changes lists them in the right panel", HistoryBar.ListOpen(info) && !screen.GetNode<Control>("%ModInfoContainer/ModTitle").Visible);
        await Capture(screen, outDir, "14b-changes-list");
        tickbox.IsTicked = !tickbox.IsTicked;
        tickbox.EmitSignal(NTickbox.SignalName.Toggled, tickbox);
        await Wait(menu, 0.3);
        Check("a tickbox click keeps the list open", HistoryBar.ListOpen(info));
        tickbox.IsTicked = !tickbox.IsTicked;
        tickbox.EmitSignal(NTickbox.SignalName.Toggled, tickbox);
        await Wait(menu, 0.3);

        // A mod's name in the list opens it, with Back to the list; picking another mod takes Back away.
        var listText = (info.FindChild("List", true, false) as RichTextLabel)?.Text ?? "";
        Check("mod names in the changes list are links", listText.Contains("[url=mod:SmmTestUi]"), listText);
        HistoryBar.OpenFromList(screen, "SmmTestUi");
        await Wait(menu, 0.3);
        var back = info.GetNodeOrNull<Control>(HistoryBar.BackName);
        Check("a name opens that mod, with Back", !HistoryBar.ListOpen(info) && CategoryList.Selected?.manifest?.id == "SmmTestUi" && back is { Visible: true });
        if (content.GetChildren().OfType<NModMenuRow>().FirstOrDefault(r => r.Mod?.manifest?.id == "SmmTestUi")?.GetNodeOrNull<Panel>("SelectionHighlight") is { } shownLook)
            Check("the selected row: the sunken look, rounded, a little wider than the row", shownLook.GetThemeStylebox("panel") is StyleBoxFlat { CornerRadiusTopLeft: RowTitles.CornerRadius, BorderWidthTop: 2 }
                && shownLook.Modulate == Colors.White && shownLook.OffsetLeft == RowTitles.LookInset.X);
        await Capture(screen, outDir, "14c-opened-from-changes");
        back?.EmitSignal(BaseButton.SignalName.Pressed);
        await Wait(menu, 0.2);
        Check("Back returns to the list", HistoryBar.ListOpen(info) && back is { Visible: false });
        HistoryBar.OpenFromList(screen, "SmmTestUi");
        await Wait(menu, 0.2);
        if (content.GetChildren().OfType<NModMenuRow>().FirstOrDefault(r => r.Mod?.manifest?.id is { } id && id != "SmmTestUi") is { } another)
            CategoryList.Select(screen, another);
        await Wait(menu, 0.2);
        Check("picking another mod takes Back away", back is { Visible: false });
        ClickChanges(); // open again, for the close test below
        await Wait(menu, 0.2);
        ClickChanges();
        await Wait(menu, 0.2);
        Check("clicking Changes again closes it", !HistoryBar.ListOpen(info));
        History.Enter(); // as when the page opens again
        await Wait(menu, 0.2);
        Check("on (re)entering the page, Undo and Revert all agree: nothing to undo", !History.CanUndo && History.AtEntered
            && screen.FindChild("UndoButton", true, false) is BaseButton { Disabled: true } && screen.FindChild("RevertAllButton", true, false) is BaseButton { Disabled: true });

        // Separate histories: an order change made in Load Order isn't undone from the list view.
        Click(screen.GetNode<NButton>("SpireModManagerLoadOrderToggle"));
        await Wait(menu, 0.3);
        var rows = content.GetChildren().OfType<NModMenuRow>().ToList();
        if (rows.FirstOrDefault(r => r.Mod?.manifest?.id == "SmmTestFixes")?.Mod is { } fixes)
            LoadOrder.Move(fixes, 0, rows);
        CategoryList.Redraw(content);
        var orderAfterMove = History.CurrentMods().Select(m => m.Id).ToList();
        Click(screen.GetNode<NButton>("SpireModManagerLoadOrderToggle"));
        await Wait(menu, 0.3);
        Categories.Rename(Store.Data.Categories[0], "List edit");
        Press("UndoButton");
        await Wait(menu, 0.3);
        Check("list undo leaves the load order alone", History.CurrentMods().Select(m => m.Id).SequenceEqual(orderAfterMove)
            && Store.Data.Categories.All(c => c.Name != "List edit"));

        // A different profile than at launch reads as one "switched" line.
        if (Store.Data.Profiles.FirstOrDefault(p => p.Name != Store.Data.ActiveProfile) is { } other && Profiles.Active is { } start)
        {
            Profiles.SwitchTo(other, screen);
            Check("switching profile shows one switched line", HistoryBar.Describe().FirstOrDefault()?.StartsWith($"Switched from {History.LaunchProfile} to {other.Name}") == true,
                string.Join(" | ", HistoryBar.Describe()));
            Profiles.SwitchTo(start, screen);
            Check("back on the launch profile, no switched line", !HistoryBar.Describe().Any(l => l.StartsWith("Switched")));
        }

        // A profile renamed since launch reads as one renamed line, not as added and deleted; renamed back, it's gone.
        if (Store.Data.Profiles.FirstOrDefault(p => p.Name != Store.Data.ActiveProfile) is { } renamed)
        {
            var was = renamed.Name;
            Profiles.Rename(renamed, "Renamed in test");
            var lines = HistoryBar.Describe();
            Check("a renamed profile shows as one renamed line", lines.Contains($"Renamed profile {was} → Renamed in test")
                && !lines.Contains("Added profile Renamed in test") && !lines.Contains($"Deleted profile {was}"), string.Join(" | ", lines));
            Profiles.Rename(renamed, was);
            Check("renamed back, no renamed line", !HistoryBar.Describe().Any(l => l.Contains("Renamed in test")), string.Join(" | ", HistoryBar.Describe()));
        }
    }

    // Missing game resources: everything this run used loaded; a path that isn't there gives null without throwing
    // (noted, and logged, once); a missing font falls back to Godot's own; AddButton's plain stand-in is a working
    // button with the label, at the asked size, with the hidden "Dot" callers show; the category header made in
    // code has the parts a redraw reads.
    static void TestFallbacks(NModdingScreen screen)
    {
        Check("every game resource used so far loaded", ModEntry.MissingResources.Count == 0, string.Join(", ", ModEntry.MissingResources));
        const string madeUp = "res://SpireModManager/selftest/no_such_picture.png";
        Texture2D? first = null, second = null;
        var threw = "";
        try
        {
            first = ModEntry.Load<Texture2D>(madeUp);
            second = ModEntry.Load<Texture2D>(madeUp); // not tried, or logged, again
        }
        catch (Exception e)
        {
            threw = e.Message;
        }
        Check("a missing resource loads as null without throwing, noted once", threw.Length == 0 && first == null && second == null
            && ModEntry.MissingResources.Count(p => p == madeUp) == 1, threw);
        Check("a missing font falls back to Godot's own",
            ModEntry.Font("res://SpireModManager/selftest/no_such_font.tres").GetInstanceId() == ThemeDB.FallbackFont.GetInstanceId());

        var holder = new Control { Name = "SpireModManagerTestHolder" };
        screen.AddChild(holder);
        var presses = 0;
        var button = ModEntry.AddButton(holder, "SPIRE_MOD_MANAGER.RELAUNCH_BUTTON", new Vector2(220, 70), () => presses++, plain: true);
        button.EmitSignal(BaseButton.SignalName.Pressed);
        Check("AddButton's plain stand-in: a clickable button with the label, at its size, with a hidden Dot",
            button is Button { Text: var text, Disabled: false } && text == Menus.Text("SPIRE_MOD_MANAGER.RELAUNCH_BUTTON") && presses == 1
            && button.Size == new Vector2(220, 70) && button.GetNodeOrNull<Control>("Dot") is { Visible: false },
            $"{button.GetType().Name} \"{(button as Button)?.Text}\", {presses} press(es), size {button.Size}");
        ModEntry.Relabel(button, "SPIRE_MOD_MANAGER.LOAD_ORDER");
        Check("the stand-in takes a new label", button is Button { Text: var relabelled } && relabelled == Menus.Text("SPIRE_MOD_MANAGER.LOAD_ORDER"));
        screen.RemoveChild(holder);
        holder.Free();

        var header = CategoryList.PlainHeader();
        Check("the category header stand-in has the scene's parts", header.GetNodeOrNull<ColorRect>("Background") != null
            && header.GetNodeOrNull<Control>("Arrow") != null && header.GetNodeOrNull<Label>("Title") != null && header.GetNodeOrNull<Label>("Count") != null);
        header.Free();
    }

    // Store.cs on made-up folders under the output folder (the sandbox's own manager.json is never touched): an
    // unreadable file is set aside (never over another) and the newest backup that reads takes its place, written
    // back; with no backup, a fresh start; a locked file is left alone and nothing is saved; nulls in a
    // hand-edited file become empty lists; a backup copy that fails doesn't stop the save. Then the notice: the
    // game's popup as the page opens (here: shown again), once.
    static async Task TestStoreSafety(Node menu, NModdingScreen screen, string outDir)
    {
        var root = Path.Combine(outDir, "store-safety");
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
        string Folder(string name, string? manager, params (string Name, string Json)[] backups)
        {
            var dir = Path.Combine(root, name);
            Directory.CreateDirectory(Path.Combine(dir, "backups"));
            if (manager != null)
                File.WriteAllText(Path.Combine(dir, "manager.json"), manager);
            foreach (var (file, json) in backups)
                File.WriteAllText(Path.Combine(dir, "backups", file), json);
            return dir;
        }
        static string Manager(string dir) => Path.Combine(dir, "manager.json");
        static string Good(string category) => $"{{\"categories\":[{{\"name\":\"{category}\"}}]}}";
        static string? Category(Store.Loaded loaded) => loaded.Data?.Categories.FirstOrDefault()?.Name;
        const string cut = "{\"categories\":[{\"name\":\"Hal"; // a write cut off
        var now = new DateTime(2026, 10, 13, 9, 30, 0);

        // Unreadable, with backups: the newest that reads ("Newest"; the one after it is cut off too). A file set
        // aside earlier in the same second keeps its name.
        var dir = Folder("recovered", cut, ("manager.20261010-100000.json", Good("Older")), ("manager.20261011-100000.json", Good("Newest")),
            ("manager.20261012-100000.json", cut));
        var newest = Path.Combine(dir, "backups", "manager.20261011-100000.json");
        File.SetLastWriteTime(newest, new DateTime(2026, 10, 11, 22, 15, 0));
        var earlier = Path.Combine(dir, "manager.broken-20261013-093000.json");
        File.WriteAllText(earlier, "earlier");
        var recovered = Store.LoadFrom(dir, now);
        Check("an unreadable file: the newest backup that reads is loaded", Category(recovered) == "Newest" && recovered.Backup?.Path == newest
            && !recovered.ReadOnly, $"{Category(recovered)} from {recovered.Backup?.Path}");
        var setAside = Path.Combine(dir, "manager.broken-20261013-093000-2.json");
        Check("the unreadable file is kept as manager.broken-<time>.json, never over another file", recovered.Unreadable == setAside
            && File.Exists(setAside) && File.ReadAllText(setAside) == cut && File.ReadAllText(earlier) == "earlier", recovered.Unreadable ?? "not set aside");
        var next = Store.LoadFrom(dir, now);
        Check("the backup is written back: the next launch reads it as usual", Category(next) == "Newest" && next is { Unreadable: null, Backup: null },
            $"{Category(next)}, unreadable {next.Unreadable}");
        File.Delete(Manager(dir));
        var missing = Store.LoadFrom(dir, now);
        Check("no file, but one was set aside before: the backups again, not a first run (no import)", Category(missing) == "Newest"
            && missing.Unreadable == null && File.Exists(Manager(dir)));

        // Unreadable (empty: a write cut off by a power loss) and no backup: a fresh start, written.
        var fresh = Folder("fresh", "");
        var started = Store.LoadFrom(fresh, now);
        Check("no backup: a fresh start, the unreadable file kept, a new file written", started is { Data: { Categories.Count: 0, Profiles.Count: 0 }, Backup: null, Unreadable: { } keptAs }
            && File.Exists(keptAs) && Store.LoadFrom(fresh, now) is { Data: not null, Unreadable: null }, started.Unreadable ?? "not set aside");

        // A file another program holds open (a Windows lock): left where it is, the backup loaded, nothing saved.
        if (OperatingSystem.IsWindows())
        {
            var lockedDir = Folder("locked", Good("Mine"), ("manager.20261011-100000.json", Good("Backup")));
            Store.Loaded locked;
            using (File.Open(Manager(lockedDir), FileMode.Open, System.IO.FileAccess.Read, FileShare.None))
                locked = Store.LoadFrom(lockedDir, now);
            Check("a locked file: left in place, the backup loaded, read-only", locked.ReadOnly && locked.Unreadable == Manager(lockedDir)
                && Category(locked) == "Backup" && File.ReadAllText(Manager(lockedDir)) == Good("Mine")
                && Directory.GetFiles(lockedDir, "manager.broken-*").Length == 0, $"read-only {locked.ReadOnly}, {Category(locked)}");
        }

        // Nulls in a hand-edited (or old) file: empty lists, null entries dropped; the file itself untouched.
        const string nulls = "{\"categories\":[null,{\"name\":null}],\"mods\":{\"A\":null,\"B\":{\"tags\":[\"x\",null]},\"C\":{\"tags\":[]}},"
            + "\"profiles\":[null,{\"name\":\"P\",\"mods\":null,\"edited\":[null,{\"id\":\"X\",\"source\":\"ModsDirectory\",\"enabled\":true}],\"disabled\":[{\"id\":null}]}]}";
        var nullsDir = Folder("nulls", nulls);
        var tidied = Store.LoadFrom(nullsDir, now);
        Check("nulls in a file: empty lists, null entries dropped, the file untouched", tidied is { Unreadable: null, Data: { } data }
            && data is { Categories: [{ Name: "" }], Profiles: [{ Name: "P", Mods.Count: 0, Edited: [{ Id: "X" }], Disabled.Count: 0 }] }
            && data.Mods.Keys.Order().SequenceEqual(["B", "C"]) && data.Mods["B"].Tags is ["x"] && data.Mods["C"].Tags == null
            && File.ReadAllText(Manager(nullsDir)) == nulls, tidied.Unreadable ?? "");
        var allNull = Store.LoadFrom(Folder("all-null", "{\"categories\":null,\"mods\":null,\"profiles\":null}"), now);
        Check("a file whose lists are null: empty ones", allNull.Data is { Categories.Count: 0, Mods.Count: 0, Profiles.Count: 0 });
        Check("no file and nothing set aside: a first run", Store.LoadFrom(Folder("first", null), now).Data == null
            && Store.LoadFrom(Path.Combine(root, "no-folder"), now).Data == null);

        // Saving: the old file goes to backups/ first, the newest ten kept; a backup that fails (here a file sits
        // where the backups folder goes) doesn't stop the save.
        var saving = Folder("saving", Good("Old"), Enumerable.Range(1, 11).Select(i => ($"manager.20200101-0000{i:00}.json", Good($"Old {i}"))).ToArray());
        Store.WriteTo(saving, new StoreData { Categories = [new Category { Name = "New" }] }, backUp: true);
        var copies = Directory.GetFiles(Path.Combine(saving, "backups")).Order().ToList();
        Check("a save backs up the old file first, the newest ten kept", Category(Store.LoadFrom(saving, now)) == "New" && copies.Count == 10
            && File.ReadAllText(copies[^1]) == Good("Old") && !copies.Any(f => f.EndsWith("-000001.json") || f.EndsWith("-000002.json")),
            string.Join(", ", copies.Select(Path.GetFileName)));
        var blocked = Path.Combine(root, "blocked");
        Directory.CreateDirectory(blocked);
        File.WriteAllText(Manager(blocked), Good("Old"));
        File.WriteAllText(Path.Combine(blocked, "backups"), "a file where the backups folder goes");
        Store.WriteTo(blocked, new StoreData { Categories = [new Category { Name = "New" }] }, backUp: true);
        Check("a backup that fails doesn't stop the save", Category(Store.LoadFrom(blocked, now)) == "New");

        // The notice: none for a file that read fine; after a recovery, the game's popup as the page shows, with
        // the backup's date and where the unreadable file is; once.
        Check("no notice when the file read fine", !Store.ShowNotice(next) && NModalContainer.Instance?.OpenModal == null);
        Store.Notice = recovered;
        screen.Visible = false;
        screen.Visible = true;
        await Wait(menu, 0.3);
        var popup = NModalContainer.Instance?.OpenModal as Node;
        var text = string.Join(" | ", popup?.FindChildren("*", "", true, false)
            .Select(n => n is Label l ? l.Text : n is RichTextLabel r ? r.GetParsedText() : "").Where(t => t.Length > 0) ?? []);
        Check("after a recovery, the page shows the notice: the backup's date and the unreadable file's name", popup is NErrorPopup
            && text.Contains("couldn't read your data") && text.Contains("October 11, 2026, 22:15") && text.Contains(Path.GetFileName(setAside)) && Store.Notice == null, text);
        await Capture(popup ?? screen, outDir, "19-data-notice");
        NModalContainer.Instance?.Clear();
        screen.Visible = false;
        screen.Visible = true;
        await Wait(menu, 0.3);
        Check("the notice shows once", NModalContainer.Instance?.OpenModal == null);
        // Each of its three texts reads whole (no placeholder left, no stray escape), naming the file.
        var texts = new[] { recovered, recovered with { Backup = null }, recovered with { ReadOnly = true } }
            .Select(loaded => Store.NoticeText(loaded)?.GetFormattedText() ?? "").ToList();
        Check("the notice's texts (backup, fresh start, read-only) read whole and name the file", texts.All(t => t.Contains(Path.GetFileName(setAside))
            && !t.Contains('{') && !t.Contains('\\')), string.Join(" | ", texts));
    }

    // The game draws some languages in its own fonts, as Kreon has none of their letters (FontManager: Chinese,
    // Japanese, Korean, Thai, Russian, Polish). Switched to Chinese, then Russian, the way the game's language setting
    // does it (the main menu made again, and the Modding page with it), the page opened again has our text in that
    // language's font: a row title (the game's label, which ours mustn't cover), a category header, a tag chip, a row's
    // button, the profile bar, the history bar, the red banner if one shows, a menu item. The buttons' widths are cached
    // per font: they still start as far past the title as before. The shortcuts under Undo and Redo fit under their
    // buttons, however wide the font. Back in the starting language, its fonts again
    // (Kreon in English). Skipped for a language the game has no font of its own for. Screenshots 20-fonts-<language>.
    static async Task TestLanguageFonts(NModdingScreen screen, string outDir)
    {
        if (NGame.Instance is not { } game)
        {
            Results.Add("FAIL fonts by language: no game node");
            return;
        }
        var start = LocManager.Instance.Language;
        var startGaps = ButtonGaps(screen);
        // A scene-made button (menu_button.tscn): its label is the game's MegaLabel, which swaps the font itself; with
        // no font of the language's own, it keeps the scene's (a copy of Kreon bold).
        static string? ViewButtonFont(Control page) => page.GetNodeOrNull<Control>("SpireModManagerLoadOrderToggle/Visuals/Label")?.GetThemeFont("font")?.ResourcePath;
        var startViewFont = ViewButtonFont(screen);
        var switched = false;
        try
        {
            foreach (var language in new[] { "zhs", "rus" })
            {
                if (FontManager.GetSubstituteFont(language, FontType.Regular) == null || FontManager.GetSubstituteFont(language, FontType.Bold) == null)
                {
                    Results.Add($"SKIP fonts in {language}: the game has none of its own for it");
                    continue;
                }
                switched = true;
                await CheckLanguage(language);
            }
            if (switched)
                await CheckLanguage(start);
        }
        finally
        {
            LocManager.Instance.SetLanguage(start);
        }

        async Task CheckLanguage(string language)
        {
            if (await Reopen(game, language) is not { } page)
            {
                Results.Add($"FAIL fonts in {language}: the Modding page didn't open again");
                return;
            }
            var content = page.GetNode<Control>("%ModsScrollContainer/Mask/Content");
            var row = content.GetChildren().OfType<NModMenuRow>()
                .FirstOrDefault(r => r.GetNodeOrNull("SpireModManagerStrip")?.GetChildren().OfType<PanelContainer>().Any() == true);
            var header = content.GetChildren().OfType<Control>().FirstOrDefault(c => c.IsInGroup("SpireModManagerCategoryHeader") && c.HasNode("Title"));
            Menus.Show(page, [new MenuItem("Font test", () => { })]);
            var item = MenuPanel.Current?.FindChildren("*", "Label", true, false).OfType<Label>().FirstOrDefault(l => l.Text == "Font test");
            // Whole-label fonts take the language's regular font even where ours is Kreon bold, as the game's own labels
            // do; only rich text's bold_font takes its bold one.
            List<(string What, Control? Text, string Font, FontType Type, bool KreonBold)> samples =
            [
                ("row title", row?.GetNodeOrNull<Control>("Title"), "normal_font", FontType.Regular, false),
                ("category header", header?.GetNodeOrNull<Control>("Title"), "font", FontType.Regular, true),
                ("tag chip", row?.GetNodeOrNull("SpireModManagerStrip")?.GetChildren().OfType<PanelContainer>().FirstOrDefault()?.GetChildOrNull<Control>(0), "font", FontType.Regular, false),
                ("row button", row?.GetNodeOrNull(RowStrip.ActionsName)?.GetChildOrNull<Control>(0), "font", FontType.Regular, false),
                ("profile bar", page.FindChild("ProfilePicker", true, false) as Control, "font", FontType.Regular, true),
                ("history bar", page.FindChild(HistoryBar.BarName, true, false)?.FindChild("UndoButton", true, false) as Control, "font", FontType.Regular, false),
                ("changes line", page.FindChild(HistoryBar.ChangesName, true, false) as Control, "bold_font", FontType.Bold, true),
                ("menu item", item, "font", FontType.Regular, false),
            ];
            MenuPanel.Close();
            var border = page.GetNode<Control>("ModsBorder");
            if ((border.FindChild("OrderProblems", true, false) ?? border.FindChild("ManagersWarning", true, false)) is Control banner)
                samples.Add(("banner", banner, "font", FontType.Regular, false));
            static string FontOf(Control? text, string name) => text?.GetThemeFont(name)?.ResourcePath ?? "none";
            var wrong = samples.Where(s => FontOf(s.Text, s.Font) != GameFont(language, s.Type, s.KreonBold)).Select(s => $"{s.What}: {FontOf(s.Text, s.Font)}").ToList();
            Results.Add($"INFO fonts in {language}: {GameFont(language, FontType.Regular, false)}, {GameFont(language, FontType.Bold, true)}");
            Check($"in {language}: our text in its font ({string.Join(", ", samples.Select(s => s.What))})", wrong.Count == 0, string.Join("; ", wrong));
            var (viewFont, viewWanted) = (ViewButtonFont(page), FontManager.GetSubstituteFont(language, FontType.Regular)?.ResourcePath ?? startViewFont);
            Check($"in {language}: a scene-made button (Load Order) in the language's font too", viewFont != null && viewFont == viewWanted,
                $"{viewFont}, wanted {viewWanted}");
            var gaps = ButtonGaps(page);
            static bool Same(float? a, float? b) => a == null ? b == null : b != null && Math.Abs(a.Value - b.Value) < 1;
            Check($"in {language}: a row's and a header's buttons start as far past the title as in {start} (widths cached per font)",
                Same(gaps.Row, startGaps.Row) && Same(gaps.Header, startGaps.Header),
                $"row {gaps.Row} (was {startGaps.Row}), header {gaps.Header} (was {startGaps.Header})");
            // The shortcuts under Undo and Redo: each centred under its button, its text no wider than the button, so the
            // two never run into each other ("Ctrl + Z" and "Ctrl + Y" did in Chinese). A hint wider than its button used
            // to stretch it, text and button then the same width and still centred, so it's checked that no hint widens
            // its button, and that each is fitted to it less a little each side (FitHint) unless at the smallest size.
            List<(Rect2 Button, float Own, float Left, float Middle, float Right, bool Fitted, string Text, int Size)> keys = [];
            foreach (var column in page.GetNodeOrNull<Control>(HistoryBar.BarName)?.GetChildren().OfType<Control>() ?? [])
                if (column.GetChildOrNull<Control>(0) is { } button && column.GetNodeOrNull<Label>("Keys") is { } hint && hint.Text.Trim().Length > 0)
                {
                    var size = hint.GetThemeFontSize("font_size");
                    var whole = hint.GetThemeFont("font").GetStringSize(hint.Text, fontSize: size).X;
                    var width = Math.Min(whole, hint.Size.X); // the text as drawn, not its box: cut with "…" to the box if it must be
                    var middle = hint.GetGlobalRect().GetCenter().X;
                    var own = button.GetCombinedMinimumSize().X; // the button's width by its own text
                    var fitted = whole <= own - 2 * HistoryBar.HintInset + 0.5f || size <= HistoryBar.HintSmallest;
                    keys.Add((button.GetGlobalRect(), own, middle - width / 2, middle, middle + width / 2, fitted, hint.Text, size));
                }
            Results.Add($"INFO shortcuts in {language}: {string.Join(", ", keys.Select(k => $"{k.Text} at {k.Size}, {k.Right - k.Left:0} of {k.Button.Size.X:0} px"))}");
            Check($"in {language}: the shortcuts under Undo and Redo, each fitted under its button without widening it, centred, apart",
                keys.Count == 2 && keys.All(k => k.Button.Size.X <= k.Own + 0.5f && k.Fitted && Math.Abs(k.Middle - k.Button.GetCenter().X) < 1
                    && k.Left >= k.Button.Position.X - 0.5f && k.Right <= k.Button.End.X + 0.5f)
                && keys.Zip(keys.Skip(1)).All(p => p.First.Right < p.Second.Left),
                string.Join("; ", keys.Select(k => $"{k.Text} at {k.Size}: {k.Left:0.#}..{k.Right:0.#} under {k.Button.Position.X:0.#}..{k.Button.End.X:0.#}"
                    + $" (its own width {k.Own:0.#}{(k.Fitted ? "" : ", not fitted")})")));
            await Capture(page, outDir, $"20-fonts-{language}");
        }
    }

    // The font the game gives a language for that type, or our Kreon (bold or not) where it has none of its own.
    static string GameFont(string language, FontType type, bool kreonBold) => FontManager.GetSubstituteFont(language, type)?.ResourcePath
        ?? (kreonBold ? "res://themes/kreon_bold_shared.tres" : "res://themes/kreon_regular_shared.tres");

    // The game's language setting: the language set and the main menu made again (NGame.Relocalize: the settings
    // stay open over it in the game; here the Modding page), then the page opened on the new menu. Null if it didn't.
    static async Task<NModdingScreen?> Reopen(NGame game, string language)
    {
        var old = game.MainMenu;
        LocManager.Instance.SetLanguage(language);
        game.ReloadMainMenu();
        await Wait(game, 1);
        if (game.MainMenu is not { } menu || menu == old)
            return null;
        var page = menu.SubmenuStack.PushSubmenuType<NModdingScreen>();
        await Wait(game, 2);
        return page;
    }

    // How far a row's buttons and a category header's start past the end of its title: the room the title was left
    // (by the cached width) against the buttons' width now. Null where none shows.
    static (float? Row, float? Header) ButtonGaps(NModdingScreen screen)
    {
        var content = screen.GetNode<Control>("%ModsScrollContainer/Mask/Content");
        static float? Gap(Control? holder, string buttons) =>
            holder?.GetNodeOrNull<Control>(buttons) is { } box && holder.GetNodeOrNull<Control>("Title") is { } title
                ? box.GetGlobalRect().Position.X - title.GetGlobalRect().End.X : null;
        var row = content.GetChildren().OfType<NModMenuRow>().FirstOrDefault(r => r.Visible && r.HasNode(RowStrip.ActionsName));
        var header = content.GetChildren().OfType<Control>().FirstOrDefault(c => c.Visible && c.IsInGroup("SpireModManagerCategoryHeader") && c.HasNode("Actions"));
        return (Gap(row, RowStrip.ActionsName), Gap(header, "Actions"));
    }

    static async Task<bool> WithVanillaView(NModdingScreen screen, Node menu, Func<bool> check)
    {
        Click(screen.GetNode<NButton>("SpireModManagerViewToggle"));
        await Wait(menu, 0.5);
        var ok = check();
        Click(screen.GetNode<NButton>("SpireModManagerViewToggle"));
        await Wait(menu, 0.5);
        return ok;
    }

    static Node? Header(Control content, string category) => content.GetNodeOrNull($"Category {category}");

    // Every text in the popup open now (the game's modal), for checking its wording.
    static List<string> PopupTexts() => NModalContainer.Instance?.OpenModal is Node popup
        ? popup.FindChildren("*", "", true, false).Select(n => n is Label l ? l.Text : n is RichTextLabel r ? r.Text : "").ToList() : [];

    // A popup of ours in the game's style: our mark, once (marking it again adds none), shown, inside the popup's
    // art (clear of its painted edge), and clear of its title, text and buttons (their art included).
    static void CheckMark(string which, Control? popup)
    {
        if (popup == null)
        {
            Results.Add($"FAIL {which}: no popup open");
            return;
        }
        // Collected before marking it here, so a caller that forgot Menus.Mark fails
        var marks = popup.FindChildren(Menus.MarkName + "*", "", true, false).OfType<Label>().ToList();
        Menus.Mark(popup);
        var again = popup.FindChildren(Menus.MarkName + "*", "", true, false).Count;
        Check($"{which}: marking again adds none", again == marks.Count, $"{marks.Count} before, {again} after");
        Check($"{which}: marked \"Spire Mod Manager\", once, shown", marks is [{ Text: "Spire Mod Manager" } shown]
            && shown.IsVisibleInTree() && shown.Size.X > 0 && shown.Size.Y > 0 && shown.GetThemeColor("font_color").A >= 0.3f,
            string.Join(", ", marks.Select(m => $"{m.Name} \"{m.Text}\" shown {m.IsVisibleInTree()}, size {m.Size}")));
        if (marks.FirstOrDefault() is not { } mark)
            return;
        var art = popup.GetNodeOrNull<Control>("VerticalPopup") ?? popup;
        var rect = mark.GetGlobalRect();
        var parts = new[] { "Header", "Description", "YesButton", "YesButton/Visuals", "NoButton", "NoButton/Visuals" }
            .Select(path => art.GetNodeOrNull<Control>(path)).OfType<Control>().Where(c => c.IsVisibleInTree()).ToList();
        var under = parts.Where(c => c.GetGlobalRect().Intersects(rect)).Select(c => c.GetPath().ToString()).ToList();
        Check($"{which}: the mark inside the popup's art, clear of its title, text and buttons", parts.Count >= 4
            && art.GetGlobalRect().Grow(-24).Encloses(rect) && under.Count == 0,
            $"mark {rect}, art {art.GetGlobalRect()}, {parts.Count} parts found, overlapping: {string.Join(", ", under)}");
        Results.Add($"INFO {which}: mark {rect}, art {art.GetGlobalRect()}, buttons " +
            string.Join(", ", parts.Where(c => c.Name.ToString().EndsWith("Button")).Select(c => c.GetGlobalRect())));
    }

    static void Click(NButton button) => button.EmitSignal(NClickableControl.SignalName.Released, button);

    // A real mouse click at the control's centre, through the game's input like a player's: catches anything
    // drawn over it or eating the click. Returns the control under the mouse, for the report.
    static async Task<string> RealClick(Node menu, Control target)
    {
        if (DisplayServer.GetName() == "headless") // no mouse there: press it directly
        {
            target.EmitSignal(BaseButton.SignalName.Pressed);
            await Wait(menu, 0.3);
            return "headless: pressed directly";
        }
        var at = target.GetGlobalRect().GetCenter();
        Input.ParseInputEvent(new InputEventMouseMotion { Position = at, GlobalPosition = at });
        await Wait(menu, 0.1);
        var hovered = target.GetViewport().GuiGetHoveredControl();
        // Described before the click: it may free them.
        var where = $"{(hovered == null ? "nothing" : $"{hovered.Name} ({hovered.GetType().Name}) under {hovered.GetParent()?.Name}")} at {at}, rect {target.GetGlobalRect()}";
        Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = at, GlobalPosition = at });
        Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = at, GlobalPosition = at });
        await Wait(menu, 0.3);
        return where;
    }

    // A mod installed while the game runs, made up, the way the game does it: read into its mod list (not loaded),
    // then announced. Gone again: off both lists.
    static void GameInstalls(Mod mod)
    {
        GameMods().Add(mod);
        typeof(ModManager).GetMethod("InvokeOnModDetected", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?.Invoke(null, [mod]);
    }

    static void GameForgets(Mod mod)
    {
        GameMods().Remove(mod);
        Installed.Forget(mod);
    }

    static List<Mod> GameMods() =>
        typeof(ModManager).GetField("_mods", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)?.GetValue(null) as List<Mod> ?? [];

    static List<string> ModListState() =>
        SaveManager.Instance.SettingsSave.ModSettings?.ModList
            .Select(m => $"{m.Id}/{m.Source}={(m.IsEnabled ? "on" : "off")}").OrderBy(s => s).ToList() ?? [];

    // Whether a history snapshot's active profile has the mod, on.
    static bool OnInSnapshot(History.Snapshot? snapshot, string id)
    {
        if (snapshot == null || JsonNode.Parse(snapshot.Store) is not JsonObject store)
            return false;
        var active = (string?)store["active_profile"];
        return (store["profiles"] as JsonArray ?? []).Any(p => (string?)p?["name"] == active
            && (p?["mods"] as JsonArray ?? []).Any(m => (string?)m?["id"] == id && (bool?)m?["enabled"] == true));
    }

    // A banner's fill (LoadOrder.Banner's rounded box): yellow for a warning, red for a problem with the mods, by hue too.
    static Color? BannerFill(Node? banner) => (banner as Control)?.GetThemeStylebox("panel") is StyleBoxFlat look ? look.BgColor : null;
    static bool Yellow(Color? fill) => fill == LoadOrder.WarningColor && fill?.H is > 0.1f and < 0.18f;
    static bool Red(Color? fill) => fill == LoadOrder.ProblemColor && fill?.H is < 0.03f or > 0.97f;

    // A banner's buttons: the first right after the words of its text (a small gap), the rest each after the one before,
    // so none out at the line's right end. The words end one line's width in, or at the text's box if it wraps. And the
    // banner no wider than the list: a line too full for it doesn't squeeze its buttons (Godot never sizes a box below
    // what its insides ask for) but widens the banner past the list's right edge, buttons and all.
    static (bool Ok, string Detail) ButtonsAfterText(Node? banner)
    {
        if (banner is not Control bar || bar.FindChildren("*", "", true, false).OfType<Label>().FirstOrDefault() is not { } text)
            return (false, "no banner");
        if (bar.GetParent()?.GetNodeOrNull<Control>("ModsScrollContainer") is not { } list)
            return (false, "no list beside the banner");
        var buttons = bar.FindChildren("*", "", true, false).OfType<Button>().Where(b => b.Visible).Select(b => b.GetGlobalRect()).ToList();
        var (box, at, listEnd) = (text.GetGlobalRect(), bar.GetGlobalRect(), list.GetGlobalRect().End.X);
        var wordsEnd = Math.Min(box.End.X, box.Position.X + text.GetThemeStylebox("normal").GetMargin(Side.Left)
            + text.GetThemeFont("font").GetStringSize(text.Text, fontSize: text.GetThemeFontSize("font_size")).X);
        var ok = buttons.Count > 0 && buttons[0].Position.X >= box.End.X && buttons[0].Position.X - wordsEnd is >= 0 and <= 12
            && buttons.Zip(buttons.Skip(1)).All(p => p.Second.Position.X >= p.First.End.X)
            && at.End.X <= listEnd + 0.5f;
        return (ok, $"words end at {wordsEnd:0} (text box to {box.End.X:0}), buttons {string.Join(", ", buttons.Select(b => $"{b.Position.X:0}-{b.End.X:0}"))}, "
            + $"banner to {at.End.X:0}, list to {listEnd:0}");
    }

    // Everything a banner says: its text, its buttons' words and their tips.
    static List<string> BannerWords(Node? banner) => banner?.FindChildren("*", "", true, false)
        .SelectMany(n => n switch { Label label => [label.Text], Button button => new[] { button.Text, button.TooltipText }, _ => [] }).ToList() ?? [];

    // A banner's text: hovering it opens the banner's card.
    static Label? BannerText(Node? banner) => banner?.FindChildren("*", "", true, false).OfType<Label>().FirstOrDefault();

    // The banner card open on the page, a line each, its title first (none open: empty).
    static List<string> BannerCardLines(Node screen) => (screen.GetNodeOrNull(LoadOrder.BannerCardName)?.FindChildren("*", "", true, false)
        .OfType<RichTextLabel>().FirstOrDefault()?.GetParsedText() ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();

    // A banner's card as a player gets it: the mouse onto its text (where Hover sees it), the card's lines read, then the
    // mouse off again.
    static async Task<List<string>> BannerCard(Node menu, Node screen, Node? banner, string? shot = null, string outDir = "")
    {
        if (BannerText(banner) is not { } text)
            return [];
        var at = text.GetGlobalRect().GetCenter(); // not read from the label later: a redraw frees it
        Hover.TestMouse = () => at;
        text.EmitSignal(Control.SignalName.MouseEntered);
        await Wait(menu, 0.1);
        var lines = BannerCardLines(screen);
        if (shot != null)
            await Capture(screen, outDir, shot);
        Hover.TestMouse = () => new Vector2(5, 5);
        if (GodotObject.IsInstanceValid(text))
            text.EmitSignal(Control.SignalName.MouseExited);
        await Wait(menu, 0.1);
        Hover.TestMouse = null;
        return lines;
    }

    static void Check(string name, bool ok, string detail = "") =>
        Results.Add($"{(ok ? "PASS" : "FAIL")} {name}{(ok || detail.Length == 0 ? "" : $": {detail}")}");

    // The fixture's mod that won't load: Test Addon (on) needs SmmTestMissing, a mod id nothing has. Null when it isn't
    // installed (--real) or is off, so the checks that need a mod that won't load skip.
    static Mod? WontLoad() => Dependencies.Find("SmmTestAddon") is { } addon && Dependencies.IsOn(addon) ? addon : null;

    // Test Addon's dependency: never installed, so it's shown by its id.
    const string Missing = "SmmTestMissing";

    static async Task Wait(Node node, double seconds) =>
        await node.ToSignal(node.GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);

    // <name>.txt: the visible UI under root as an indented tree; <name>.png: a screenshot, unless the
    // game runs headless (nothing is drawn then).
    static async Task Capture(Node root, string outDir, string name)
    {
        var dump = new StringBuilder();
        DumpTree(root, 0, dump);
        File.WriteAllText(Path.Combine(outDir, name + ".txt"), dump.ToString());
        if (DisplayServer.GetName() == "headless")
            return;
        await root.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        root.GetViewport().GetTexture().GetImage().SavePng(Path.Combine(outDir, name + ".png"));
    }

    // One line per visible node: name, type, global rect for controls, and text or tick state.
    // Hidden branches are skipped, like the screen itself.
    static void DumpTree(Node node, int depth, StringBuilder dump)
    {
        if (node is CanvasItem { Visible: false })
            return;
        dump.Append(' ', depth * 2).Append(node.Name).Append(" [").Append(node.GetType().Name).Append(']');
        if (node is Control control)
        {
            var rect = control.GetGlobalRect();
            dump.Append($" ({rect.Position.X:0},{rect.Position.Y:0} {rect.Size.X:0}x{rect.Size.Y:0})");
        }
        var text = node switch
        {
            Label label => label.Text,
            RichTextLabel rich => rich.Text,
            LineEdit edit => edit.Text,
            Button button => button.Text,
            _ => "",
        };
        if (text.Length > 0)
            dump.Append(" \"").Append(text.ReplaceLineEndings(" ")).Append('"');
        if (node is NTickbox tickbox)
            dump.Append(tickbox.IsTicked ? " ticked" : " unticked");
        dump.AppendLine();
        foreach (var child in node.GetChildren())
            DumpTree(child, depth + 1, dump);
    }
}
