using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Godot;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Localization.Fonts;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Debug;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;
using MegaCrit.Sts2.Core.Nodes.Screens.Settings;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.addons.mega_text;

namespace SpireModManager;

// In-game smoke test, run by `dotnet run tools/dev.cs test`. Does nothing unless the environment variable
// SPIRE_MOD_MANAGER_SELFTEST names an output folder. Then, from the main menu: screenshots, opens the
// Modding screen, checks our nodes, switches profile and back (the mod list must round-trip), toggles
// the view, writes results.txt, and quits. The runner uses the no-Steam dev sandbox, never real saves.
// SPIRE_MOD_MANAGER_SELFTEST_MODE picks a shorter suite instead: "real" (RunReal), "first-run" or
// "first-run-bmm" (RunFirstRun).
static partial class SelfTest
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
        // A dev run killed while loading leaves its launch record, and its notice would pop up over the checks.
        if (LaunchGuard.Notice != null)
        {
            LaunchGuard.Notice = null;
            Results.Add("INFO dropped a real notice about the last launch (a dev run stopped while loading)");
        }
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
        if (mode is "crash" or "crash-real") // EarlyStart answers the dialog of a failed start: reaching the menu is the failure
        {
            Results.Add("FAIL a failed start: the game reached the main menu (nothing stopped its start)");
            File.WriteAllLines(Path.Combine(outDir, "results.txt"), [.. Results, .. Timing()]);
            menu.GetTree().Quit();
            return;
        }
        if (mode == "crash-prepare")
        {
            await RunCrashPrepare(menu, outDir);
            return;
        }
        if (mode is "first-run" or "first-run-bmm")
        {
            await RunFirstRun(menu, outDir, imported: mode == "first-run-bmm");
            return;
        }
        var tree = menu.GetTree(); // to quit by: TestLanguageFonts rebuilds the main menu, this one with it
        // Each test through Step: one that throws is a FAIL and the next runs. This try is for the checks in between.
        try
        {
            await Wait(menu, 3);
            await Step(() => TestText()); // our pack's text, whichever page shows
            await Step(() => TestFailSoft());
            if (!ModEntry.OurPage)
            {
                await Step(() => TestGamePage(menu, outDir)); // nothing more of ours to test
                return;
            }
            Check("no popup open", NModalContainer.Instance?.OpenModal == null,
                NModalContainer.Instance?.OpenModal?.GetType().Name ?? "");
            Check("main menu button", menu.GetNodeOrNull("SpireModManagerMenuButton") != null);
            await TestStep();
            await Step(() => TestMenuButtonLook(menu));
            await Step(() => TestMenuButtonCard(menu));
            var relaunchArgs = ModEntry.RelaunchArgs();
            // Once, or not at all for a minimized window or one with no size (a headless run's reads Minimized, 0x0).
            var sized = DisplayServer.WindowGetMode() switch
            {
                DisplayServer.WindowMode.Fullscreen or DisplayServer.WindowMode.ExclusiveFullscreen or DisplayServer.WindowMode.Maximized => true,
                DisplayServer.WindowMode.Windowed => DisplayServer.WindowGetSize() is { X: > 0, Y: > 0 },
                _ => false,
            };
            Check("relaunch passes the window mode once (none for a window with no size, as headless)",
                relaunchArgs.Count(a => a is "--fullscreen" or "--maximized" or "--windowed") == (sized ? 1 : 0)
                && relaunchArgs.Contains("--force-steam"), $"{string.Join(" ", relaunchArgs)} (window {DisplayServer.WindowGetMode()} {DisplayServer.WindowGetSize()})");
            // Started from a Steam invite: Steam adds +connect_lobby and the lobby's id, and the game joins it at startup.
            // The forms the game reads: the id after it or after "=", dashes before it; one with no id keeps the flag after it.
            // Fixed cases (the game's arguments: Godot keeps its own options, like --log-file, out of them).
            string Args(string[] given, string[] process, DisplayServer.WindowMode mode, int width = 1600, int height = 900) =>
                string.Join(" ", ModEntry.RelaunchArgs(given, process, mode, new Vector2I(width, height), new Vector2I(40, 30)));
            var invited = ModEntry.RelaunchArgs(["--force-steam", "+connect_lobby", "109775241000000001", "--seed", "ABC",
                "+connect_lobby=109775241000000002", "--+connect_lobby", "109775241000000003", "+connect_lobby", "--nomods", "+connect_lobby"],
                [], DisplayServer.WindowMode.Windowed, new Vector2I(1600, 900), new Vector2I(40, 30));
            Check("relaunch leaves out the Steam invite the game was started from (+connect_lobby, its lobby), keeps the rest",
                !invited.Any(a => a.Contains("connect_lobby") || a.StartsWith("10977524")) && invited.Contains("--force-steam")
                && invited.Contains("--seed") && invited.Contains("ABC") && invited.Contains("--nomods"), string.Join(" ", invited));
            string[] windows =
            [
                Args(["--nomods"], [], DisplayServer.WindowMode.Windowed), Args(["--nomods"], [], DisplayServer.WindowMode.Maximized),
                Args(["--nomods"], [], DisplayServer.WindowMode.ExclusiveFullscreen), Args(["--nomods"], [], DisplayServer.WindowMode.Minimized),
                Args(["--nomods"], [], DisplayServer.WindowMode.Windowed, 0, 0),
            ];
            Check("relaunch opens as this window is: windowed at its size and place, maximized, fullscreen; minimized or 0x0 (headless): no window options",
                windows.SequenceEqual(["--nomods --windowed --resolution 1600x900 --position 40,30", "--nomods --maximized", "--nomods --fullscreen", "--nomods", "--nomods"]),
                string.Join(" | ", windows));
            // launch_opengl.bat: the renderer is an engine option, in the process's arguments only.
            string[] renderers =
            [
                Args([], ["SlayTheSpire2.exe", "--rendering-driver", "opengl3", "--rendering-method", "gl_compatibility"], DisplayServer.WindowMode.Maximized),
                Args([], ["SlayTheSpire2.exe", "--rendering-driver", "vulkan"], DisplayServer.WindowMode.Maximized),
                Args([], ["SlayTheSpire2.exe", "--rendering-driver"], DisplayServer.WindowMode.Maximized),
                Args(["--rendering-driver", "d3d12"], ["SlayTheSpire2.exe", "--rendering-driver", "opengl3"], DisplayServer.WindowMode.Maximized),
            ];
            Check("relaunch keeps the renderer the game was started with (launch_opengl.bat, launch_vulkan.bat), once",
                renderers.SequenceEqual(["--rendering-driver opengl3 --rendering-method gl_compatibility --maximized", "--rendering-driver vulkan --maximized",
                    "--maximized", "--rendering-driver d3d12 --maximized"]), string.Join(" | ", renderers));
            Check("dev run marked", menu.GetTree().Root.Title.StartsWith("[DEV]") && menu.GetTree().Root.HasNode("SpireModManagerDevTag"),
                menu.GetTree().Root.Title);
            await Capture(menu, outDir, "1-main-menu");
            await Step(() => TestMenuButtonCarets(menu, outDir));
            await Step(() => TestApi(menu, outDir)); // the API, as another mod uses it (SelfTest.Api.cs)
            // The relaunch note as it shows while one is pending: centred above the button (shown here for the shot).
            if (menu.GetNodeOrNull<Control>("SpireModManagerMenuButton/PendingNote") is { Visible: false } pendingNote)
            {
                pendingNote.Visible = true;
                await Capture(menu, outDir, "1e-menu-button-note");
                pendingNote.Visible = false;
            }
            await Step(() => TestMenuButtonMove(menu, outDir));
            await Step(() => TestBetterModMenuImport(outDir));
            await Step(() => TestMenuButtonRelaunch(menu, outDir)); // before the Modding page opens: the check's fixes without it
            await Step(() => TestLaunchGuard(menu, outDir));
            await Step(() => TestCrashOffer(menu, outDir));
            await Step(() => TestSameIdCanary()); // which copy of a mod installed twice loads (SameIdLoad)
            await Step(() => TestSameIdPlan());
            await Step(() => TestSameIdPick());
            await Step(() => TestSameIdSettle());
            await Step(() => TestCopiesOff());
            await Step(() => TestSameIdErrors());
            await Step(() => TestSameIdLaunchGuard(menu, outDir));
            await Step(() => TestTranslationsFolder(menu));
            await Step(() => TestTranslations(menu));
            await Step(() => TestModDoctorFile(menu, outDir));
            await Step(() => TestPrewarm(menu));

            var screen = menu.SubmenuStack.PushSubmenuType<NModdingScreen>();
            _page = screen; // what Step puts back after a test that throws
            // The page as first drawn, in the frame it opened. The stable game's scene holds a placeholder row (no mod),
            // freed only at the frame's end; counting it made the list one mod longer until the next redraw.
            var firstCount = screen.FindChild(PageBar.Name, true, false)?.GetNodeOrNull<Label>("Scope")?.Text;
            var firstUncategorized = UncategorizedCount(screen.GetNode<Control>("%ModsScrollContainer/Mask/Content"));
            await Wait(menu, 2);
            var content = screen.GetNode<Control>("%ModsScrollContainer/Mask/Content");
            var rows = content.GetChildren().OfType<NModMenuRow>().ToList();
            Check("the page's first draw counts only the installed mods (no placeholder row)",
                firstCount == Menus.Format("SPIRE_MOD_MANAGER.PAGE_ALL", ("count", Menus.Number(ModManager.Mods.Count)))
                && firstUncategorized == UncategorizedCount(content), $"{firstCount}; uncategorized {firstUncategorized} then {UncategorizedCount(content)}");
            Check("relaunch button", screen.GetNodeOrNull("SpireModManagerRelaunchButton") != null);
            await Step(() => TestNewModsAtLaunch()); // first: before anything switches profiles
            if (Bmm.Present) // dev.cs test --bmm
                await Step(() => TestBetterModMenu(menu, screen, content, outDir));
            Check("view toggle", screen.GetNodeOrNull("SpireModManagerViewToggle") != null);
            await Step(() => TestPageLinkBaseline(screen));
            Check("profile bar", screen.GetNodeOrNull("ModsBorder/SpireModManagerProfileBar") != null);
            Check("our views use the wide layout", screen.GetNode<Control>("ModsBorder").OffsetLeft < 255 && screen.GetNode<Control>("%ModInfoContainer").OffsetRight > 1727);
            Check($"rows ({rows.Count}) match mods ({ModManager.Mods.Count})", rows.Count == ModManager.Mods.Count);
            Check("the page's 'after a restart' line follows the main menu button's rule from the page's build",
                screen.GetNode<Control>("%PendingChangesLabel").Visible == ModEntry.HasPendingChanges(), $"line {screen.GetNode<Control>("%PendingChangesLabel").Visible}, menu {ModEntry.HasPendingChanges()}");
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
                // The main menu button, under the page, has followed already: it's what shows once the page closes.
                Check("the main menu button follows a profile switch: the other profile and its count, as wide as its words",
                    MenuButtonText(menu) == MenuButtonExpected() && MenuButtonText(menu)?.Contains(other.Name) == true && MenuButtonMisfit(menu) == null,
                    $"{MenuButtonText(menu)} vs {MenuButtonExpected()}; {MenuButtonMisfit(menu)}");
                Check("our own mod stays on", !(SaveManager.Instance.SettingsSave.ModSettings?.ModList
                    .Any(m => m.Id == ModEntry.ModId && !m.IsEnabled) ?? false));
                await Capture(screen, outDir, "3-other-profile");
                Profiles.SwitchTo(start, screen);
                await Wait(menu, 1);
                var after = ModListState();
                Check("profile round trip restores the mod list", before.SequenceEqual(after),
                    string.Join(", ", before.Except(after)));
                Check("the main menu button follows the switch back, as wide as its words", MenuButtonText(menu) == MenuButtonExpected()
                    && MenuButtonText(menu)?.Contains(start.Name) == true && MenuButtonMisfit(menu) == null,
                    $"{MenuButtonText(menu)} vs {MenuButtonExpected()}; {MenuButtonMisfit(menu)}");
            }
            else
                Results.Add("SKIP profile round trip: only one profile");
            await Step(() => TestNewModsOnSwitch(menu, screen, content));

            Click(screen.GetNode<BaseButton>("SpireModManagerViewToggle"));
            await Wait(menu, 1);
            Check("vanilla view hides headers", !content.GetChildren().Any(c => c.IsInGroup("SpireModManagerCategoryHeader")));
            var border = screen.GetNode<Control>("ModsBorder");
            Check("vanilla view: the game's row spacing and sides", content.GetThemeConstant("separation") == 16 && content.OffsetLeft == 0 && content.OffsetRight == 0,
                $"separation {content.GetThemeConstant("separation")}, sides {content.OffsetLeft}..{content.OffsetRight}");
            if (!Bmm.Present) // its page lays the list out its own way (TestBetterModMenu)
                Check("vanilla view restores the game's layout", border.OffsetLeft == 255 && border.OffsetRight == 994
                    && border.GetNode<Control>("ModsScrollContainer").OffsetTop == 8
                    && !border.GetNode<Control>("SpireModManagerProfileBar").Visible, $"{border.OffsetLeft}..{border.OffsetRight}");
            var (makeMods, getMods) = (border.GetNode<Control>("%MakeModsButton"), border.GetNode<Control>("%GetModsButton"));
            Check("vanilla view: Make Mods and Get Mods as the game's own page has them (shown on the beta, hidden on stable)",
                makeMods.Visible == (Compat.InfoClear != null) && getMods.Visible == makeMods.Visible, $"Make Mods {makeMods.Visible}, Get Mods {getMods.Visible}");
            await Capture(screen, outDir, "4-vanilla-view");
            Click(screen.GetNode<BaseButton>("SpireModManagerViewToggle"));
            await Wait(menu, 1);

            await Step(() => TestCategories(menu, screen, content, outDir));
            await Step(() => TestListFixes(menu, screen, content));
            await Step(() => TestProfileEditing(menu, screen, outDir));
            await Step(() => TestUnsavedProfile(menu, screen, content, outDir));
            await Step(() => TestProfileShare(menu, screen, outDir));
            await Step(() => TestSubscribeMissing(menu, screen, content, outDir));
            await Step(() => TestNotes(menu, screen, content, outDir));
            await Step(() => TestPendingNotes(menu, screen, content));
            await Step(() => TestPendingLine(menu, screen, content));
            await Step(() => TestSearch(menu, screen, content, outDir));
            await Step(() => TestBranchTags());
            await Step(() => TestDescriptionSearch());
            await Step(() => TestSearchGrammar());
            await Step(() => TestHiddenTags(content));
            await Step(() => TestRedrawCost(menu, screen, content));
            await Step(() => TestManyMods(menu, screen, content));
            await Step(() => TestNewMod(menu, screen, content, outDir));
            await Step(() => TestLoadOrder(menu, screen, content, outDir));
            await Step(() => TestLoadOrderFind(menu, screen, content, outDir));
            await Step(() => TestListBanner(menu, screen, content, outDir));
            await Step(() => TestManagersBanner(menu, screen, content, outDir));
            await Step(() => TestSelfIncluded(menu, screen, content, outDir));
            await Step(() => TestPageBar(menu, screen, content, outDir));
            await Step(() => TestListFilters(menu, screen, content, outDir));
            await Step(() => TestRelaunchCheck(menu, screen, content, outDir));
            await Step(() => TestLongDetails(menu, screen, content, outDir));
            await Step(() => TestWorkshopHead(menu, screen, outDir));
            await Step(() => TestOtherModPanel(menu, screen, content, outDir));
            await Step(() => TestGameChanges(menu, screen, content, outDir));
            await Step(() => TestBulkEdit(menu, screen, content, outDir));
            await Step(() => TestPicks(menu, screen, content, outDir));
            await Step(() => TestCopies(menu, screen, content, outDir));
            await Step(() => TestSameIdCopies(menu, screen, content, outDir));
            await Step(() => TestSameIdBothOn(menu, screen, content, outDir)); // both copies ticked: which loads, and the words for it
            await Step(() => TestTagSuggestions(menu, screen, content, outDir));
            await Step(() => TestTagPills(menu, screen, content, outDir));
            await Step(() => TestHistory(menu, screen, content, outDir));
            await Step(() => TestFallbacks(screen));

            // Leaving from Load Order: the page comes back on the main list.
            Click(screen.GetNode<NButton>("SpireModManagerLoadOrderToggle"));
            await Wait(menu, 0.3);
            screen.Visible = false;
            screen.Visible = true;
            await Wait(menu, 0.3);
            Check("reopening after Load Order shows the main list", Layout.Current == Layout.View.Categories
                && screen.GetNode<Control>("%ModInfoContainer").Visible);
            await Step(() => TestListFilterReopen(menu, screen, content));
            await Step(() => TestMenuButtonOpensOurs(menu, screen));

            await Step(() => TestStoreSafety(menu, screen, outDir));
            await Step(() => TestLanguageFonts(screen, outDir)); // last: the main menu, the page with it, is made again
        }
        catch (Exception e)
        {
            Results.Add($"FAIL exception: {e}");
        }
        finally
        {
            File.WriteAllLines(Path.Combine(outDir, "results.txt"), [.. Results, .. Timing()]);
            tree.Quit();
        }
    }

    // The page the run opened, for Step to put back (none before it opens).
    static NModdingScreen? _page;

    // One test, guarded: an exception in it is a FAIL naming it (from the call: Step(() => TestX(...))), and the run goes
    // on to the next test instead of losing every check after it. First what it may have left open is put back (PutBack);
    // data it left changed isn't, so a later failure may come from this one.
    static async Task Step(Func<Task> test, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(test))] string call = "")
    {
        try
        {
            await test();
        }
        catch (Exception e)
        {
            Results.Add($"FAIL {call.Split("=>")[^1].Trim().Split('(')[0]} threw: {e}");
            PutBack();
        }
    }

    static Task Step(Action test, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(test))] string call = "") => Step(() =>
    {
        test();
        return Task.CompletedTask;
    }, call);

    // After a test that threw: our menus and dialogs closed, the game's popup too, the real mouse and title fitting back,
    // and on the page the main list with nothing picked and no search.
    static void PutBack()
    {
        try
        {
            Hover.TestMouse = null;
            Layout.TestFitting = null;
            Hover.Hide();
            MenuPanel.Close();
            RelaunchCheck.Close();
            ProfileShare.Close();
            MissingMods.Close();
            MenuButton.CancelMove();
            NModalContainer.Instance?.Clear();
            if (_page is not { } page || !GodotObject.IsInstanceValid(page) || !page.IsInsideTree())
                return;
            if (Store.Data.VanillaView)
                Click(page.GetNode<BaseButton>("SpireModManagerViewToggle"));
            if (Store.Data.LoadOrderView)
                Click(page.GetNode<NButton>("SpireModManagerLoadOrderToggle"));
            var content = page.GetNode<Control>("%ModsScrollContainer/Mask/Content");
            Selection.Clear(content, record: false);
            Search.Clear(content);
        }
        catch (Exception e)
        {
            Results.Add($"INFO couldn't put everything back after it: {e.Message}");
        }
    }

    // The guard itself: a test that throws is one FAIL naming it and its exception, and the run goes on (the probe's line
    // is taken back: it's this check's).
    static async Task TestStep()
    {
        static Task Probe() => throw new InvalidOperationException("the probe's own");
        var at = Results.Count;
        await Step(() => Probe());
        var said = Results.Skip(at).ToList();
        Results.RemoveRange(at, said.Count);
        Check("a test that throws: one FAIL naming it and its exception, then the run goes on",
            said is [var line] && line.StartsWith("FAIL Probe threw: System.InvalidOperationException: the probe's own"), string.Join(" | ", said));
    }

    // --real: your own mods (all off) and data. No edits; a tour of the views for screenshots and
    // timings, since this is the list size and naming the screen has to cope with.
    static async Task RunReal(NMainMenu menu, string outDir)
    {
        try
        {
            await WaitReal(menu, 3); // the main menu settled: nothing timed below shares its frames with start-up work
            CheckPatches();
            // The first open, timed to the end of its first frame, once the page's text has been shaped ahead (Prewarm).
            await WaitUntil(menu, () => Prewarm.Done, timeout: 10);
            RowStrip.Costs.Clear();
            RowStrip.Timing = true; // the rows' lines, buttons and pencils, part by part (#46)
            var opening = System.Diagnostics.Stopwatch.StartNew();
            var screen = menu.SubmenuStack.PushSubmenuType<NModdingScreen>();
            var built = opening.Elapsed.TotalMilliseconds;
            RowStrip.Timing = false;
            await menu.ToSignal(menu.GetTree().CreateTimer(0), SceneTreeTimer.SignalName.Timeout);
            Results.Add($"INFO first open: built in {built:0} ms, drawn by {opening.Elapsed.TotalMilliseconds:0} ms; text shaped ahead: "
                + $"{Prewarm.Shaped.Count}{(Prewarm.Done ? "" : " (not finished)")}, its slowest frame {Prewarm.SlowestMs:0} ms");
            Results.Add($"INFO of the build, the rows' lines, buttons and pencils {RowStrip.Costs.Values.Sum():0} ms: {Parts(RowStrip.Costs)}");
            await WaitReal(menu, 2); // the first open settled (its pictures, deferred layout) before the redraws are timed
            var content = screen.GetNode<Control>("%ModsScrollContainer/Mask/Content");
            var rows = content.GetChildren().OfType<NModMenuRow>().ToList();
            Check($"rows ({rows.Count}) match mods ({ModManager.Mods.Count})", rows.Count == ModManager.Mods.Count);
            Results.Add($"INFO categories: {Store.Data.Categories.Count}, profiles: {Store.Data.Profiles.Count}, " +
                $"mods with notes: {Store.Data.Mods.Values.Count(m => m.Notes != null)}");

            // A full redraw happens on every search keystroke and every edit: it has to stay quick.
            CleanHeap();
            var timer = System.Diagnostics.Stopwatch.StartNew();
            for (var i = 0; i < 10; i++)
                CategoryList.Apply(content);
            var categoriesMs = timer.Elapsed.TotalMilliseconds / 10;
            Check($"category view redraw {categoriesMs:0.0} ms", categoriesMs < 50);
            double Ms(Action action)
            {
                CleanHeap();
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
            Results.Add($"INFO a search keystroke {Ms(() => { Type("lib"); Type(""); }) / 2:0.0} ms, "
                + $"by hidden tags over the whole list {Ms(() => { Type("#problem OR #pending"); Type(""); }) / 2:0.0} ms");
            // The same to the end of the frame (FrameMs), with what they leave for it: titles fitted, layout.
            await Wait(menu, 0.3);
            var redrawFrame = await FrameMs(menu, () => CategoryList.Apply(content));
            var keyFrame = (await FrameMs(menu, () => Type("lib")) + await FrameMs(menu, () => Type(""))) / 2;
            Results.Add($"INFO to the end of the frame: a redraw {redrawFrame:0.0} ms, a search keystroke {keyFrame:0.0} ms");
            await Wait(menu, 1);
            _page = screen;
            await Step(() => TimeSwitches(menu, screen, content));
            await Capture(screen, outDir, "real-1-categories");

            Click(screen.GetNode<NButton>("SpireModManagerLoadOrderToggle"));
            await Wait(menu, 1);
            CleanHeap();
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
            Click(screen.GetNode<BaseButton>("SpireModManagerViewToggle"));
            await Wait(menu, 1);
            await Capture(screen, outDir, "real-4-vanilla");
            Click(screen.GetNode<BaseButton>("SpireModManagerViewToggle"));
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
            File.WriteAllLines(Path.Combine(outDir, "results.txt"), [.. Results, .. Timing()]);
            menu.GetTree().Quit();
        }
    }

    // Switching views with a mod picked, as a player does: main list to Load Order and back, to Vanilla List and
    // back. Each switch is timed to the end of its frame (FrameMs), three times; the middle time is reported.
    static async Task TimeSwitches(Node menu, NModdingScreen screen, Control content)
    {
        if (content.GetChildren().OfType<NModMenuRow>().FirstOrDefault() is { } row)
        {
            CategoryList.Select(screen, row);
            await PanelFor(menu, screen, row);
        }
        await Wait(menu, 0.5);
        var idle = new List<double>();
        var times = new Dictionary<string, List<double>>();
        async Task Switch(string name, string toggle)
        {
            if (!times.TryGetValue(name, out var list))
                times[name] = list = [];
            list.Add(await FrameMs(menu, () =>
            {
                if (screen.GetNode(toggle) is BaseButton link)
                    Click(link);
                else
                    Click(screen.GetNode<NButton>(toggle));
            }));
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
    // Before each timing: a full GC that comes due otherwise lands inside some runs' timing and not others' (one
    // added 50-90 ms to a loop of ten redraws), so numbers from two builds wouldn't compare.
    static void CleanHeap()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    // The Uncategorized header's count as drawn (the newest header: an older one may wait to be freed this frame).
    static string? UncategorizedCount(Control content) => content.GetChildren().OfType<Control>()
        .LastOrDefault(c => c.Name.ToString().Contains("Uncategorized") && !c.IsQueuedForDeletion())?.GetNodeOrNull<Label>("Count")?.Text;

    static async Task<double> FrameMs(Node node, Action action)
    {
        CleanHeap();
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
            nameof(NewModPopupPatch), nameof(NewModTogglePatch), nameof(TickKeepPatch), nameof(TranslationsPatch)];
        Check("our page with no patch failed, or only ones it can do without", ModEntry.PageWorks([]) && ModEntry.PageWorks(spare));
        Check("the game's page if any one patch our page needs failed", ModEntry.PageNeeds.Contains(nameof(ModdingScreenPatch))
            && ModEntry.PageNeeds.All(needed => !ModEntry.PageWorks([needed]) && !ModEntry.PageWorks([.. spare, needed])),
            string.Join(", ", ModEntry.PageNeeds.Where(needed => ModEntry.PageWorks([needed]))));
        // The line itself, on a stand-in for the screen.
        var stand = new Control();
        var line = ModEntry.ShowNeedsUpdate(stand);
        Check("the needs-update line: its text, once, above the game's list and info panel",
            line.Text == "Spire Mod Manager is incompatible with this game version, or may require an update." && ModEntry.ShowNeedsUpdate(stand) == line
            && stand.GetChildCount() == 1 && line.Position.Y + line.Size.Y <= 83, $"{line.Text} at {line.Position}, {line.Size}");
        stand.Free();
    }

    // dev.cs test with SPIRE_MOD_MANAGER_SKIP_PATCHES naming a patch our page needs (not MainMenuPatch: this test
    // starts from it): the Modding page is the game's, with the needs-update line and none of ours, the info panel
    // included. Without the screen's own patch, the line is the main menu button's note instead.
    static async Task TestGamePage(NMainMenu menu, string outDir)
    {
        Results.Add($"INFO left out: {string.Join(", ", ModEntry.FailedPatches)}");
        const string text = "Spire Mod Manager is incompatible with this game version, or may require an update.";
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
            await PanelFor(menu, screen, row);
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
            // The launch that brings picks (no data yet, so no pick): the game's own choice loads, the copy sorted first,
            // the other set aside (no duplicate error); that one becomes the pick, so nothing is pending for them.
            var twins = ModManager.Mods.Where(m => m.manifest?.id == TwinId).ToList();
            if (twins.FirstOrDefault(m => Copies.CopyOf(m) == "l:SmmTestTwinA") is { } a && twins.FirstOrDefault(m => Copies.CopyOf(m) == "l:SmmTestTwinB") is { } b)
                Check("first run: a mod installed twice, no pick saved yet: the game's own choice (the copy sorted first) loads, the other set aside with no "
                    + "duplicate error; that one picked, nothing pending for them",
                    SameIdLoad.Plan.GetValueOrDefault(TwinId) is { HadPick: false, Swapped: false } plan && plan.Loads == a && a.state == ModLoadState.Loaded
                    && b.state == ModLoadState.DisabledDuplicate && !ModManager.Mods.Any(m => m.errors?.Any(e => e.LocEntryKey == "MOD_ERROR.DUPLICATE_ID") == true)
                    && Store.Data.CopyPicks?.Contains($"{TwinId}|l:SmmTestTwinA") == true && Copies.Ticked(a) && !Copies.Ticked(b)
                    && Store.Data.CopiesOff?.Contains($"{TwinId}|l:SmmTestTwinB") == true, // a first run reads as data from before copies_off: the pick alone on
                    $"A {a.state}, B {b.state}; picks {string.Join(", ", Store.Data.CopyPicks ?? [])}; plan {SameIdLoad.Plan.GetValueOrDefault(TwinId)}");
            else
                TwinsMissing("first run: a mod installed twice");

            _page = screen;
            if (imported)
                await Step(() => TestImported(menu, screen, content, outDir));
            else
                await Step(() => TestNoData(menu, screen, content, outDir));
            await Step(() => TestSecondLoad());
        }
        catch (Exception e)
        {
            Results.Add($"FAIL exception: {e}");
        }
        finally
        {
            File.WriteAllLines(Path.Combine(outDir, "results.txt"), [.. Results, .. Timing()]);
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
        var text = Menus.Text("SPIRE_MOD_MANAGER.NO_CATEGORIES_HINT");
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
        // One undo step, as from the bulk menu: an undo takes the category and the mod's move back together; a redo both
        // again.
        History.Undo();
        await Wait(menu, 0.3);
        var undone = Store.Data.Categories.Count == 0 && Store.InfoFor(modId)?.Category == null;
        History.Redo();
        await Wait(menu, 0.3);
        Check("a category made from a mod's menu is one undo step: an undo takes the category and the move back, a redo both again",
            undone && Store.Data.Categories.FirstOrDefault()?.Name is { } redone && Store.InfoFor(modId)?.Category == redone,
            $"undone {undone}; {Store.Data.Categories.Count} categories, the mod's {Store.InfoFor(modId)?.Category}");
        made = Store.Data.Categories.FirstOrDefault() ?? made; // undo restores the data as new objects
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

    // Regressions: clicks outside the list, list height after collapsing,
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
        // Rows folded at launch were never laid out: no width yet. A redraw then, and a category opened by its header,
        // must still show its mods' names (#151: they were cut to fit no width, and the header only shows the rows).
        foreach (var row in content.GetChildren().OfType<NModMenuRow>())
            row.Size = new Vector2(0, row.Size.Y);
        CategoryList.Apply(content);
        await Wait(menu, 0.3);
        var folded = content.GetChildren().OfType<Control>().First(c => c.IsInGroup("SpireModManagerCategoryHeader") && c.Name.ToString().StartsWith("Category "));
        var foldedName = folded.Name.ToString(); // read now: the click redraws the list, and the header is made again
        folded.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true });
        folded.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false });
        await Wait(menu, 0.3);
        var opened = content.GetChildren().OfType<NModMenuRow>().Where(r => r.Visible).ToList();
        string Shown(NModMenuRow row) => row.GetNode<RichTextLabel>("Title").GetParsedText().Trim();
        Check("a category folded since launch shows its mods' names when opened", opened.Count > 0 && opened.All(r => Shown(r).Length > 0),
            $"{foldedName}: {string.Join(" | ", opened.Select(r => $"{r.Mod?.manifest?.id}=\"{Shown(r)}\""))}");
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
        OursHold("main list", Layout.CornerX);
        var toggles = new[] { "SpireModManagerViewToggle", "SpireModManagerLoadOrderToggle" }.Select(name => screen.GetNode<Control>(name)).ToList();
        Check("the link to its page says Go to vanilla Modding page", toggles[0] is LinkButton { Text: var linkText } && linkText == Menus.Text("SPIRE_MOD_MANAGER.GO_TO_VANILLA"));
        Check("the page link is a little smaller than the row text, never underlined (our links: colour and the pointer), like our other links",
            toggles[0] is LinkButton { Underline: LinkButton.UnderlineMode.Never, MouseDefaultCursorShape: Control.CursorShape.PointingHand } pageLink
            && pageLink.GetThemeFontSize("font_size") == CategoryList.PageLinkSize
            && screen.FindChildren("SpireModManager*", "LinkButton", true, false).OfType<LinkButton>().All(l => l.Underline == LinkButton.UnderlineMode.Never)
            && CategoryList.PageLinkSize < 20, $"{(toggles[0] as LinkButton)?.Underline}");

        // Where it leads, in its tip: the game's own page (as Better Mod Menu shows it, with that loaded), and back.
        var vanillaTip = Hover.TipOf(toggles[0]);
        var backTip = "";
        var linkAt = toggles[0].GetGlobalRect();
        (Rect2 At, string Overlaps) back = default;
        await WithVanillaView(screen, menu, () =>
        {
            var link = screen.GetNode<Control>("SpireModManagerViewToggle");
            backTip = Hover.TipOf(link);
            // Clear of the game's page: its list, its title and its buttons beside it (the beta's page shows them).
            var parts = new[] { "%ModsScrollContainer", "%InstalledModsTitle", "%MakeModsButton", "%GetModsButton", "%ModInfoContainer" }
                .Select(name => screen.GetNodeOrNull<Control>(name)).OfType<Control>().Where(c => c.IsVisibleInTree());
            back = (link.GetGlobalRect(), string.Join(", ", parts.Where(c => c.GetGlobalRect().Intersects(link.GetGlobalRect())).Select(c => c.Name.ToString())));
            return true;
        });
        Check("the link back from the game's page is where ours was (under the mouse that clicked it), clear of the game's page",
            back.At.Size.X > 0 && back.At.Position.DistanceTo(linkAt.Position) < 1 && back.Overlaps == "", $"{linkAt} / back {back.At}, over: {back.Overlaps}");
        // While a mod is installed twice from one source (the fixture twins), a second line says how that page's tickbox
        // treats its copies (its words are checked in TestSameIdCopies).
        var tipLines = vanillaTip.Split('\n');
        Check("the vanilla page link's card says it's the game's own page; the link back from it has none",
            tipLines[0] == Menus.Text("SPIRE_MOD_MANAGER.VANILLA_VIEW_TIP")
            && (Copies.TwinGroups().Count > 0 ? tipLines is [_, var copies] && copies.StartsWith("In vanilla, mods ") : tipLines.Length == 1)
            && string.IsNullOrEmpty(backTip), $"{vanillaTip} | {backTip}");
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
        await PanelFor(menu, screen, row);
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
            Click(screen.GetNode<BaseButton>("SpireModManagerViewToggle"));
            await Wait(menu, 1);
            Check("its page: none of ours (headers, strips, managers banner), the game's icons hidden, the list clipped",
                !content.GetChildren().Any(c => c.IsInGroup("SpireModManagerCategoryHeader"))
                && !content.GetChildren().OfType<NModMenuRow>().Any(r => Shown(r.GetNodeOrNull("SpireModManagerStrip")))
                && !Shown(border.GetNodeOrNull(LoadOrder.ManagersName)) && mask.ClipContents
                && content.GetChildren().OfType<NModMenuRow>().All(r => !Shown(r.GetNode("PlatformIcon"))));
            Check("its page: the same mod still selected", CategoryList.Selected == row.Mod && RowState.Selected(row));
            var get = border.GetNode<Control>("%GetModsButton");
            Check("its page: Make Mods and Get Mods as the game's own page has them (shown on the beta, hidden on stable)",
                make.Visible == (Compat.InfoClear != null) && get.Visible == make.Visible, $"Make Mods {make.Visible}, Get Mods {get.Visible}");
            if (chrome != null)
            {
                Rect2 Rect(string name) => screen.FindChild(name, true, false) is Control c ? c.GetGlobalRect() : new Rect2();
                var (top, groups, search, list) = (Rect("BetterModMenuTopBar"), Rect("BetterModMenuGroupBar"), Rect("BetterModMenuSearchBar"), scroll.GetGlobalRect());
                var title = border.GetNode<Control>("%InstalledModsTitle").GetGlobalRect();
                // On the stable branch Better Mod Menu 1.1.1 makes no row controls at all, with or without us: its row
                // postfix was built against the beta, where ModManifest is a record with ==, and stable's has none
                // (MissingMethodException as it compiles). So none there is its own, not a row of ours hiding them.
                var stableWithoutRowControls = Compat.InfoClear == null && RowControls().Count == 0;
                Check("its page: its bars, row controls and panel buttons show", Shown(chrome) && (RowControls().Any(Shown) || stableWithoutRowControls)
                    && Shown(info.GetNodeOrNull("BetterModMenuSelectedModActions")),
                    $"bars {Shown(chrome)}, row controls {RowControls().Count(Shown)} of {RowControls().Count}, panel buttons "
                    + (info.GetNodeOrNull("BetterModMenuSelectedModActions") is { } actions ? $"shown {Shown(actions)}" : "none"));
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
            Click(screen.GetNode<BaseButton>("SpireModManagerViewToggle"));
            await Wait(menu, 1);
            OursHold(when, Layout.CornerX);
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

    // The main menu button's text, and what it should read: "Mod Profile: <active profile> (<the profile bar's count>)".
    static string? MenuButtonText(NMainMenu menu) => menu.GetNodeOrNull<Label>("SpireModManagerMenuButton/Label")?.Text;

    static string MenuButtonExpected() => $"{Profiles.Active?.Name} ({Profiles.CountText()})";

    // Null if the main menu button fits its words as they are now (measured afresh): its label's box that wide and
    // centred in it, grown on hover from its middle, the button that and its margins, whole on screen. Shown or hidden
    // (under a page): it follows at once either way. Otherwise what's wrong.
    static string? MenuButtonMisfit(NMainMenu menu)
    {
        if (menu.GetNodeOrNull<Control>("SpireModManagerMenuButton") is not { } button || button.GetNodeOrNull<Label>("Label") is not { } label)
            return "no button";
        var words = label.GetCombinedMinimumSize().X;
        var (box, inner) = (button.GetGlobalRect(), label.GetGlobalRect());
        return Math.Abs(label.Size.X - words) < 1 && Math.Abs(label.PivotOffset.X - words / 2) < 1
            && Math.Abs(box.Size.X - (words + 2 * MenuButton.Margin)) < 1 && Math.Abs(inner.GetCenter().X - box.GetCenter().X) < 1
            && box.Encloses(inner) && button.GetViewportRect().Encloses(box)
            ? null : $"words {words}, label {inner} (pivot {label.PivotOffset}), button {box}";
    }

    // The main menu button is the game's own text button, a copy of its Settings button: the same class, font, size and
    // outline (no picture), with the game's notification dot on its label. It reads the active profile and the profile
    // bar's count, as wide as its words, whole on screen. A long name is cut with "…", one mod reads "1 mod", and a
    // saved position off the screen still puts it all on screen.
    static void TestMenuButtonLook(NMainMenu menu)
    {
        if (menu.GetNodeOrNull<Control>("SpireModManagerMenuButton") is not { } button)
            return;
        var label = button.GetNodeOrNull<Label>("Label");
        var settings = menu.GetNodeOrNull<Label>("MainMenuTextButtons/SettingsButton/Label");
        Check("main menu button: the game's text button in its Settings button's font and outline, at a smaller size, no picture (but its hover carets)",
            button is NMainMenuTextButton && label != null && settings != null && label.GetThemeFont("font") == settings.GetThemeFont("font")
            && label.GetThemeFontSize("font_size") == MenuButton.TextSize && MenuButton.TextSize < settings.GetThemeFontSize("font_size")
            && label.GetThemeConstant("outline_size") == settings.GetThemeConstant("outline_size")
            && label.GetThemeColor("font_outline_color") == settings.GetThemeColor("font_outline_color")
            && !button.FindChildren("*", "TextureRect", true, false).Any(t => t.GetParent()?.Name != "Dot" && t.Name.ToString() is not (MenuButton.CaretLeftName or MenuButton.CaretRightName)),
            $"{button.GetType().Name}, size {label?.GetThemeFontSize("font_size")} vs {settings?.GetThemeFontSize("font_size")}");
        Check("main menu button reads the active profile and the profile bar's count", MenuButtonText(menu) == MenuButtonExpected(),
            $"{MenuButtonText(menu)} vs {MenuButtonExpected()}");
        Check("main menu button: hovering it says what it opens, names the profile in gold, and gives the key that opens it too",
            Hover.TipOf(button) == MenuCard($"[gold]{Profiles.Active?.Name}[/gold]") && MenuButton.Tip("[b]Odd") == MenuCard("[gold][lb]b]Odd[/gold]"), Hover.TipOf(button));
        var screen = button.GetViewportRect();
        Check("main menu button: as wide as its words, whole on screen", MenuButtonMisfit(menu) == null, MenuButtonMisfit(menu) ?? "");
        // The game's hover carets reach 54 from the text either side (NMainMenu.MainMenuButtonFocused): inside the button.
        Check("main menu button: room for the hover carets inside it", label != null
            && button.GetGlobalRect().Encloses(label.GetGlobalRect().GrowIndividual(54, 0, 54, 0)), $"{label?.GetGlobalRect()} in {button.GetGlobalRect()}");
        Check("main menu button: the game's notification dot on its label, shown only with a relaunch pending",
            button.FindChild("Dot", true, false) is Control dot && dot.GetParent() == label && dot.Visible == ModEntry.HasPendingChanges()
            && dot.FindChildren("*", "TextureRect", true, false).Count == 1);

        if (label == null)
            return;
        var (font, size) = (label.GetThemeFont("font"), label.GetThemeFontSize("font_size"));
        var cut = MenuButton.Text("A profile with a really quite long name", 3, font, size);
        Check("main menu button: a long profile name cut with \"…\", one mod, thousands", cut.StartsWith("A profile") && cut.EndsWith("… (3 mods)")
            && MenuButton.Text("Default", 1, font, size) == "Default (1 mod)"
            && MenuButton.Text("Default", 1234, font, size) == "Default (1,234 mods)",
            $"{cut} | {MenuButton.Text("Default", 1, font, size)}");
        var saved = Store.Data.MenuButtonAt; // in memory only: nothing is saved
        Store.Data.MenuButtonAt = new ScreenPoint(5000, 5000);
        MenuButton.Place(button);
        var corner = button.GetGlobalRect();
        var note = button.GetNodeOrNull<Control>("PendingNote");
        var noteThere = note?.GetGlobalRect() ?? default;
        Store.Data.MenuButtonAt = saved;
        MenuButton.Place(button);
        var noteHere = note?.GetGlobalRect() ?? default;
        // The "Relaunch to apply" note (shown or not): centred above the button, on screen.
        bool Above(Rect2 noteAt, Rect2 buttonAt) => noteAt.End.Y <= buttonAt.Position.Y + 1 && Math.Abs(noteAt.GetCenter().X - buttonAt.GetCenter().X) < 1;
        Check("main menu button: a saved place past the screen's corner keeps it whole on screen, its note centred above it",
            screen.Encloses(corner) && corner.End.DistanceTo(screen.End) < 1 && note != null && screen.Encloses(noteThere) && Above(noteThere, corner),
            $"{corner} in {screen}, note {noteThere}");
        Check("main menu button: back in its place, its note centred above it, on screen", note != null && screen.Encloses(noteHere)
            && Above(noteHere, button.GetGlobalRect()), $"note {noteHere}, button {button.GetGlobalRect()}");
    }

    // A real hover on the main menu button (a mouse motion through the viewport): its own carets come out either side of
    // its text, in their colour, and fade once the mouse leaves. (RitsuLib's main menu scroller hid the menu's shared
    // pair over it.)
    static async Task TestMenuButtonCarets(NMainMenu menu, string outDir)
    {
        if (menu.GetNodeOrNull<Control>("SpireModManagerMenuButton") is not { } button || button.GetChild(0) is not Control label
            || button.GetNodeOrNull<Control>(MenuButton.CaretLeftName) is not { } left || button.GetNodeOrNull<Control>(MenuButton.CaretRightName) is not { } right)
        {
            Check("main menu button: its own hover carets", false, "missing");
            return;
        }
        // A mouse motion onto it, or away, through the viewport. With no window (headless) there is no mouse: its own
        // mouse_entered and mouse_exited instead, which the game's hover handler takes the same way.
        var headless = DisplayServer.GetName() == "headless";
        var away = new Vector2(1800, 100);
        void MoveTo(Vector2 at) => menu.GetViewport().PushInput(new InputEventMouseMotion { Position = at, GlobalPosition = at });
        void Enter()
        {
            if (headless)
                button.EmitSignal(Control.SignalName.MouseEntered);
            else
                MoveTo(button.GetGlobalRect().GetCenter());
        }
        void Leave()
        {
            if (headless)
                button.EmitSignal(Control.SignalName.MouseExited);
            else
                MoveTo(away);
        }
        Leave();
        await Wait(menu, 0.4);
        Enter();
        await WaitReal(menu, 0.5);
        var text = label.GetGlobalRect();
        Check("main menu button: a hover brings its own carets out either side of its text, in their own colour",
            left.IsVisibleInTree() && right.IsVisibleInTree() && left.Modulate.IsEqualApprox(MenuButton.CaretColor) && right.Modulate.IsEqualApprox(MenuButton.CaretColor)
            && left.GetGlobalRect().End.X <= text.Position.X && right.GlobalPosition.X >= text.End.X
            && Math.Abs(left.GlobalPosition.Y - (button.GlobalPosition.Y + 5)) < 1,
            $"left {left.GetGlobalRect()} a {left.Modulate.A:0.##}, right {right.GetGlobalRect()} a {right.Modulate.A:0.##}, text {text}");
        await Capture(menu, outDir, "1f-menu-button-carets");
        Leave();
        await WaitReal(menu, 0.5);
        Check("main menu button: its carets fade once the mouse leaves", left.Modulate.A < 0.05f && right.Modulate.A < 0.05f,
            $"{left.Modulate.A:0.##}, {right.Modulate.A:0.##}");
        // A pass across it in one frame (on the way to the game's buttons): the carets, put up a frame late, stay down.
        Enter();
        Leave();
        await Wait(menu, 0.4);
        Check("main menu button: a pass across it in one frame leaves its carets down", left.Modulate.A < 0.05f && right.Modulate.A < 0.05f,
            $"{left.Modulate.A:0.##}, {right.Modulate.A:0.##}");
    }

    // The main menu button card's last line: the key that opens the page, unless the patch that reads it didn't apply.
    static string ManagerKeyLine => ModEntry.FailedPatches.Contains(nameof(ShortcutsPatch)) ? "" : "\n" + OS.GetKeycodeString(ShortcutsPatch.ManagerKey);

    // The main menu button's card for a profile (as written), with the key that opens the page, or without one.
    static string MenuCard(string profile) => ModEntry.FailedPatches.Contains(nameof(ShortcutsPatch))
        ? $"Spire Mod Manager - View and manage your mods\nRight-click for more options\n\nCurrently using profile {profile}"
        : $"Spire Mod Manager - View and manage your mods\n{OS.GetKeycodeString(ShortcutsPatch.ManagerKey)} to open, or right-click for more options\n\nCurrently using profile {profile}";

    // The main menu button's card: nothing at the usual delay (the mouse crosses the button on its way to the game's), then
    // after a second, the normal card, its profile name in gold.
    static async Task TestMenuButtonCard(NMainMenu menu)
    {
        if (menu.GetNodeOrNull<Control>("SpireModManagerMenuButton") is not { } button)
            return;
        var early = await HoverCard(menu, button, Hover.TipName);
        var card = ""; // its BBCode, read while it shows (it's freed as it closes)
        var late = await SizedHoverCard(menu, button, Hover.TipName, () =>
        {
            card = menu.GetTree().Root.FindChild(Hover.TipName, true, false)?.FindChildren("*", "RichTextLabel", true, false).OfType<RichTextLabel>().FirstOrDefault()?.Text ?? "";
            return Task.CompletedTask;
        }, seconds: Hover.SlowDelay + 0.2);
        Check("main menu button: its card waits a second, then names the profile in gold, the key on its own line", early == "" && Hover.SlowDelay == 1.0
            && late.Text == MenuCard($"{Profiles.Active?.Name}")
            && !card.Contains("[gold]") && card.Contains("[color=#") && card.Contains($"]{Profiles.Active?.Name}[/color]"), $"early \"{early}\", then \"{late.Text}\"");

        // Where the card opens, chosen each time from where the button is then (CardMisplaced: exactly where Hover puts
        // it, over neither the button nor the mouse, on screen): above it in its lower-left corner, with no room below;
        // moved to the top-right corner, below it and left of the mouse; to the bottom-right corner, above it and left of
        // the mouse. With the relaunch note showing (above the button in its corner, below it at the top), off the note
        // too. Moved in memory only: nothing is saved.
        var saved = Store.Data.MenuButtonAt;
        var note = button.GetNodeOrNull<Control>("PendingNote");
        var noteShown = note?.Visible ?? false;
        async Task<string?> CardAt(ScreenPoint? at, bool up, bool left, bool withNote = false)
        {
            Store.Data.MenuButtonAt = at;
            MenuButton.Place(button);
            if (note == null)
                return "no note";
            note.Visible = withNote;
            var area = withNote ? button.GetGlobalRect().Merge(note.GetGlobalRect()) : (Rect2?)null;
            return CardMisplaced(await HoverCardBox(menu, button, Hover.TipName, Hover.SlowDelay + 0.2), button, up, left, area);
        }
        try
        {
            var corner = await CardAt(null, up: true, left: false);
            Check("main menu button: in its corner, its card opens above it, not pushed up over it and the mouse", corner == null, corner ?? "");
            var topRight = await CardAt(new ScreenPoint(5000, -5000), up: false, left: true);
            var bottomRight = await CardAt(new ScreenPoint(5000, 5000), up: true, left: true);
            Check("main menu button: moved, its card opens where there's room (top right: below, left of the mouse; bottom right: above, left of it)",
                topRight == null && bottomRight == null, $"top right: {topRight ?? "fine"}; bottom right: {bottomRight ?? "fine"}");
            var noteAbove = await CardAt(null, up: true, left: false, withNote: true);
            var noteBelow = await CardAt(new ScreenPoint(5000, -5000), up: false, left: true, withNote: true);
            Check("main menu button: its card keeps off the relaunch note too (above it in the corner, below it at the top)",
                noteAbove == null && noteBelow == null, $"corner: {noteAbove ?? "fine"}; top: {noteBelow ?? "fine"}");
        }
        finally
        {
            if (note != null)
                note.Visible = noteShown;
            Store.Data.MenuButtonAt = saved;
            MenuButton.Place(button);
        }
    }

    // Our main menu button opens the page on our view even when Vanilla View was the last one shown; the game's way in
    // (from its Settings) keeps the last view. The page closed for real, then opened each way: the
    // game reuses the page, so this is the page shown again (on its first opening, ours was read as it was made). Then
    // F3, which opens and closes it.
    static async Task TestMenuButtonOpensOurs(NMainMenu menu, NModdingScreen screen)
    {
        if (menu.GetNodeOrNull<Control>("SpireModManagerMenuButton") is not { } button)
            return;
        async Task<string?> Reopened(Action open, bool vanilla)
        {
            menu.SubmenuStack.Pop();
            await Wait(menu, 0.5);
            var closed = !screen.IsVisibleInTree() && button.IsVisibleInTree();
            open();
            await Wait(menu, 1);
            var headers = screen.GetNode<Control>("%ModsScrollContainer/Mask/Content").GetChildren().Any(c => c.IsInGroup("SpireModManagerCategoryHeader"));
            return closed && screen.IsVisibleInTree() && Store.Data.VanillaView == vanilla && headers != vanilla
                ? null : $"closed {closed}, shown {screen.IsVisibleInTree()}, vanilla {Store.Data.VanillaView}, headers {headers}";
        }
        var toggle = screen.GetNode<BaseButton>("SpireModManagerViewToggle");
        Click(toggle);
        await Wait(menu, 1);
        var game = await Reopened(() => menu.SubmenuStack.PushSubmenuType<NModdingScreen>(), vanilla: true);
        Check("the game's way in keeps Vanilla View, the last one shown", game == null, game ?? "");
        var ours = await Reopened(() => button.EmitSignal(NClickableControl.SignalName.Released, button), vanilla: false);
        Check("our main menu button opens our view, after Vanilla View", ours == null, ours ?? "");
        if (Store.Data.VanillaView) // never leave the tests after this on Vanilla View
        {
            Click(toggle);
            await Wait(menu, 1);
        }

        // F3 (ShortcutsPatch.ManagerKey): on the page, in either view, back to the main menu as its Back button goes; on
        // the main menu, our view, as the button opens it. A menu of ours open closes first, on the page and on the main
        // menu. On another of the game's pages, or while the search box has the keyboard, it does nothing.
        async Task Press()
        {
            foreach (var pressed in new[] { true, false })
            {
                Input.ParseInputEvent(new InputEventKey { Keycode = ShortcutsPatch.ManagerKey, Pressed = pressed });
                await Wait(menu, 0.2);
            }
            await Wait(menu, 0.3);
        }
        bool OnMainMenu() => !menu.SubmenuStack.SubmenusOpen && !screen.IsVisibleInTree() && button.IsVisibleInTree() && !MenuPanel.IsOpen;
        bool OnOurView() => menu.SubmenuStack.Peek() == screen && screen.IsVisibleInTree() && !Store.Data.VanillaView && !MenuPanel.IsOpen
            && screen.GetNode<Control>("%ModsScrollContainer/Mask/Content").GetChildren().Any(c => c.IsInGroup("SpireModManagerCategoryHeader"));
        string Where() => $"screen {ActiveScreenContext.Instance.GetCurrentScreen()?.GetType().Name ?? "none"}, page shown {screen.IsVisibleInTree()}, "
            + $"vanilla {Store.Data.VanillaView}, menu open {MenuPanel.IsOpen}";
        screen.GetViewport().GuiReleaseFocus(); // the key reaches the page, not a text box
        Click(toggle);
        await Wait(menu, 1);
        await Press();
        Check("F3 on the page goes back to the main menu, as its Back button does (from Vanilla View too)", OnMainMenu(), Where());
        var settings = menu.SubmenuStack.PushSubmenuType<NSettingsScreen>();
        await Wait(menu, 0.5);
        await Press();
        Check("F3 on another of the game's pages (Settings) does nothing", menu.SubmenuStack.Peek() == settings && settings.IsVisibleInTree()
            && !screen.IsVisibleInTree(), Where());
        menu.SubmenuStack.Pop();
        await Wait(menu, 0.5);
        await Press();
        await Wait(menu, 0.7); // our view's redraw
        Check("F3 on the main menu opens our view of the page, as the button does, after Vanilla View", OnOurView(), Where());

        MenuPanel.Show(screen.GetNode<Control>("%ModsScrollContainer/Mask/Content"), () => [new MenuItem("One", () => { })]);
        await Wait(menu, 0.1);
        var pageMenu = MenuPanel.IsOpen;
        await Press();
        var pageMenuClosed = OnOurView();
        await Press();
        Check("F3 with a menu open on the page closes the menu first, staying; the next goes back to the main menu",
            pageMenu && pageMenuClosed && OnMainMenu(), $"opened {pageMenu}, closed and stayed {pageMenuClosed}; {Where()}");
        var buttonMenu = await ButtonMenu(menu, button) != null;
        await Press();
        var buttonMenuClosed = OnMainMenu();
        await Press();
        await Wait(menu, 0.7);
        Check("F3 with the main menu button's menu open closes the menu first, staying; the next opens the page",
            buttonMenu && buttonMenuClosed && OnOurView(), $"opened {buttonMenu}, closed and stayed {buttonMenuClosed}; {Where()}");

        if (screen.GetNodeOrNull<LineEdit>("ModsBorder/SpireModManagerSearch") is { } search)
        {
            var text = search.Text;
            search.GrabFocus();
            await Press();
            Check("F3 while typing in the search box does nothing to the page: it stays, the box keeps the keyboard and its text",
                OnOurView() && search.HasFocus() && search.Text == text, $"focused {search.HasFocus()}, \"{search.Text}\"; {Where()}");
            screen.GetViewport().GuiReleaseFocus();
        }
        else
            Results.Add("FAIL F3 while typing in the search box: no search box");
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

    // The main menu button's right-click menu, open (null if it didn't open).
    static async Task<Node?> ButtonMenu(NMainMenu menu, Control button)
    {
        var at = button.GetGlobalRect().GetCenter();
        Hover.TestMouse = () => at;
        button.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton { ButtonIndex = MouseButton.Right, Pressed = false, Position = at, GlobalPosition = at });
        await Wait(menu, 0.3);
        Hover.TestMouse = null;
        return MenuPanel.Current;
    }

    // The main menu button's right-click menu: under a heading with our name and version, Move button, Reset position, Open
    // translations folder, a divider, Relaunch game (last and on its own). Relaunch game checks for mods that won't load first, as the Modding page's button does (fixture: Test Addon
    // needs SmmTestMissing, never installed): its dialog shows over the main menu, and its fixes work there before the
    // page has been opened. ModEntry.TestRelaunch counts each relaunch instead of quitting the test.
    static async Task TestMenuButtonRelaunch(NMainMenu menu, string outDir)
    {
        if (menu.GetNodeOrNull<Control>("SpireModManagerMenuButton") is not { } button)
            return;

        // Where its menu opens, from the mouse on the button (MenuMisplaced), chosen from where the button is: in its
        // lower-left corner, above the mouse (right of it), not pushed up over it; moved to the top-right corner, below it
        // and left of it; to the bottom-right corner, above it and left of it. Moved in memory only: nothing is saved.
        var saved = Store.Data.MenuButtonAt;
        async Task<string?> MenuAt(ScreenPoint? at, bool up, bool left)
        {
            Store.Data.MenuButtonAt = at;
            MenuButton.Place(button);
            var mouse = new Rect2(button.GetGlobalRect().GetCenter(), Vector2.Zero);
            var misplaced = MenuMisplaced(await ButtonMenu(menu, button), mouse, up, left);
            MenuPanel.Close();
            return misplaced;
        }
        try
        {
            var corner = await MenuAt(null, up: true, left: false);
            Check("main menu button's menu: in its corner, it opens above the mouse, not pushed up over it", corner == null, corner ?? "");
            var topRight = await MenuAt(new ScreenPoint(5000, -5000), up: false, left: true);
            var bottomRight = await MenuAt(new ScreenPoint(5000, 5000), up: true, left: true);
            Check("main menu button's menu: moved, it opens where there's room (top right: below the mouse, left of it; bottom right: above it, left of it)",
                topRight == null && bottomRight == null, $"top right: {topRight ?? "fine"}; bottom right: {bottomRight ?? "fine"}");
        }
        finally
        {
            Store.Data.MenuButtonAt = saved;
            MenuButton.Place(button);
        }

        var popup = await ButtonMenu(menu, button);
        var labels = popup == null ? [] : MenuPanel.Labels(popup);
        var manage = $"Manage mods{(ModEntry.FailedPatches.Contains(nameof(ShortcutsPatch)) ? "" : $" ({OS.GetKeycodeString(ShortcutsPatch.ManagerKey)})")}";
        Check("main menu button's menu: Manage mods (with its key), Move button, Reset position, Open translations folder, Relaunch game",
            labels.SequenceEqual([manage, "Move button", "Reset position", "Open translations folder", "Relaunch game"]), string.Join(", ", labels));
        if (popup == null)
            return;
        string? Label(Node item) => item.IsInGroup(MenuPanel.ItemGroup) ? (string)item.GetMeta("label") : null;
        var items = popup.FindChild("Items", true, false)?.GetChildren().OfType<Control>().ToList() ?? [];

        // At the top, our name and version (from the manifest the game loaded), a heading with a line under it: not an
        // item, so nothing in it takes the mouse or the focus, and a click on it leaves the menu open.
        var version = ModEntry.Version;
        var header = $"Spire Mod Manager {version}";
        var top = items.FirstOrDefault();
        var parts = top?.FindChildren("*", "Control", true, false).OfType<Control>().Prepend(top).ToList() ?? [];
        Check("main menu button's menu: \"Spire Mod Manager <version>\" first (no \"v\"), a heading, not an item", version != null && top != null
            && top.IsInGroup(MenuPanel.HeadingGroup) && (string)top.GetMeta("label") == header && MenuPanel.Headings(popup).FirstOrDefault() == header
            && !labels.Contains(header) && top.FindChildren("*", "Label", true, false).OfType<Label>().Any(l => l.Text == header)
            && top.FindChildren("*", "ColorRect", true, false).Count == 1 && items.Count > 1 && Label(items[1]) == manage
            && parts.All(c => c.MouseFilter == Control.MouseFilterEnum.Ignore && c.FocusMode == Control.FocusModeEnum.None),
            $"{string.Join(" | ", MenuPanel.Headings(popup))}; version {version}");
        // A real click on it, through the viewport (and the menu's Catcher): the menu stays open, nothing runs.
        var placed = Store.Data.MenuButtonAt;
        if (top != null)
        {
            var at = top.GetGlobalRect().GetCenter();
            Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = at, GlobalPosition = at });
            await Wait(menu, 0.1);
            Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = at, GlobalPosition = at });
            await Wait(menu, 0.2);
        }
        Check("a click on the header does nothing: the menu stays open", top != null && MenuPanel.Current == popup
            && !menu.SubmenuStack.SubmenusOpen && !menu.HasNode(MenuButton.MoveLayerName) && Store.Data.MenuButtonAt == placed
            && menu.GetTree().Root.GetNodeOrNull(RelaunchCheck.Name) == null);

        // The divider: the one entry between Open translations folder and Relaunch game, a line under the first and above
        // the second.
        var above = items.FindIndex(i => Label(i) == "Open translations folder");
        var divider = above >= 0 && above + 2 < items.Count && Label(items[above + 2]) == "Relaunch game" ? items[above + 1] : null;
        Check("a divider right under Open translations folder, above Relaunch game", divider != null && Label(divider) == null
            && divider.FindChildren("*", "ColorRect", true, false).Count == 1
            && divider.GetGlobalRect().Position.Y >= items[above].GetGlobalRect().End.Y
            && divider.GetGlobalRect().End.Y <= items[above + 2].GetGlobalRect().Position.Y,
            string.Join(" | ", items.Select(i => $"{Label(i) ?? "(line)"} {i.GetGlobalRect()}")));
        await Capture(menu, outDir, "1c-menu-button-menu");

        var relaunches = 0;
        ModEntry.TestRelaunch = plain => relaunches += plain ? 0 : 1; // through the game's quit (a plain one doesn't count)
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
            var steps = (History.Steps(History.Track.List), History.Steps(History.Track.Order));
            var dot = (Control)button.FindChild("Dot", true, false);
            var text = MenuButtonText(menu);
            Press("DisableBrokenButton");
            await Wait(menu, 0.3);
            Check("from the main menu, Disable mods that can't load: off, the profile and the dot follow, then a plain Relaunch",
                !Copies.Ticked(addon) && Profiles.Edited && dot.Visible == ModEntry.HasPendingChanges()
                && Dialog()?.FindChild("RelaunchNowButton", true, false) is Button { Text: "Relaunch Game" }, Body());
            Check("the main menu button's count follows a change made on the main menu, as wide as its words", MenuButtonText(menu) == MenuButtonExpected()
                && MenuButtonText(menu) != text && MenuButtonMisfit(menu) == null,
                $"{text} -> {MenuButtonText(menu)} vs {MenuButtonExpected()}; {MenuButtonMisfit(menu)}");
            Press("RelaunchNowButton");
            await Wait(menu, 0.2);
            Check("its Relaunch reaches the relaunch", relaunches == 1, $"{relaunches} relaunches");
            // Cancel (the relaunch here only counted, so the dialog is still up) takes the fix back with no Modding page
            // to redraw, and forgets it: both histories as they were when the dialog opened, nothing to redo.
            bool AsBefore() => ModListState().SequenceEqual(before) && !Profiles.Edited && Store.Data.MenuButtonAt == null
                && dot.Visible == ModEntry.HasPendingChanges();
            string NotAsBefore() => $"{string.Join(", ", before.Except(ModListState()))}; edited {Profiles.Edited}, button at {Store.Data.MenuButtonAt}";
            Press("CancelRelaunchButton");
            await Wait(menu, 0.3);
            Check("from the main menu, Cancel takes back that fix, and only it", Dialog() == null && AsBefore(), NotAsBefore());
            Check("and forgets it: the undo histories as the dialog found them, nothing to redo",
                (History.Steps(History.Track.List), History.Steps(History.Track.Order)) == steps && !History.CanRedo,
                $"{steps} -> ({History.Steps(History.Track.List)}, {History.Steps(History.Track.Order)}), redo {History.CanRedo}");
        }
        finally
        {
            ModEntry.TestRelaunch = null;
            RelaunchCheck.Close();
        }
    }

    // Crash-safe launch (LaunchGuard). This launch: the record our initializer wrote is in Store's folder and gone at the
    // main menu, which saved this launch's mods as the last working setup; the menu hook is a prefix, first. A real crash
    // can't happen here, so the rest runs on a made-up folder with made-up mod lists: a launch that never reached the
    // menu leaves its record, the next one reads it and, at its menu, what changed since the last working setup. No
    // notice when the next launch loaded the same mods, nor for updates alone; a failed save of the working setup still
    // clears the record; an unreadable record, or one with a mod that has no id or an unknown source, counts as none.
    // Then the notice (the game's popup, our mark) with those changes, waiting while another popup is up, and shown
    // once. Restore: the working list back, a mod added since off, ours on, and a relaunch (counted, not done); with a
    // mod left on that needs one added since, the relaunch check's dialog instead. The list is put back.
    static async Task TestLaunchGuard(NMainMenu menu, string outDir)
    {
        var ownDir = LaunchGuard.Dir ?? "(none)";
        Check("launch record: in the folder Store uses", ownDir == Store.Dir, $"{ownDir} vs {Store.Dir}");
        static string Listed(IEnumerable<LaunchGuard.Entry>? mods) => mods == null ? "(none)" : string.Join(", ", mods.Select(m => $"{m.Id}/{m.Source}={m.Enabled}"));
        var savedNow = Listed(History.CurrentMods().Where(m => m.Id != null).Select(m => new LaunchGuard.Entry(m.Id, m.Source, m.Enabled, null)));
        // The record has each copy of a mod installed twice from one source (the fixture twins), on for the one that
        // loaded; the saved list one switch for them: on if one copy was.
        var workingThen = LaunchGuard.Read(Path.Combine(ownDir, LaunchGuard.WorkingFile))?.Mods;
        // The record keeps the order the mods loaded in; the saved list has ours first since (Settle), where Better Mod
        // Menu loaded before us (--bmm).
        var savedThen = Listed(workingThen?.OrderBy(m => m.Id == ModEntry.ModId ? 0 : 1)
            .Select(m => m with { Enabled = workingThen.Any(o => o.Id == m.Id && o.Source == m.Source && o.Enabled) }));
        Check("launch record: gone at the main menu, and this launch's mods saved as the last working setup",
            !File.Exists(Path.Combine(ownDir, LaunchGuard.PendingFile)) && savedThen == savedNow, $"saved {savedThen}; now {savedNow}");
        var hook = HarmonyLib.Harmony.GetPatchInfo(HarmonyLib.AccessTools.Method(typeof(NMainMenu), nameof(NMainMenu._Ready)))?.Prefixes
            .FirstOrDefault(p => p.owner == ModEntry.ModId);
        Check("launch record: cleared by a prefix on the main menu, first in line", hook?.priority == HarmonyLib.Priority.First, $"{hook?.priority}");

        static LaunchGuard.Entry Mod(string id, bool on = true, string? version = "1.0") => new(id, ModSource.ModsDirectory, on, version);
        List<LaunchGuard.Entry> working = [Mod(ModEntry.ModId), Mod("SmmTestLib"), Mod("SmmTestContent"), Mod("SmmTestUi"), Mod("SmmTestFixes", on: false)];
        // Ours updated and moved (never a change); the framework updated (a version with a bracket, which the notice
        // shows as text), and after Test Content now; Test UI off, Test Fixes on; a new mod on, and a new one off (no change).
        List<LaunchGuard.Entry> tried = [Mod("SmmTestContent"), Mod("SmmTestLib", version: "1.1 [b]"), Mod(ModEntry.ModId, version: "2.0"),
            Mod("SmmTestUi", on: false), Mod("SmmTestFixes"), Mod("SmmTestNew"), Mod("SmmTestNewOff", on: false)];
        static string Kinds(List<LaunchGuard.Change> changes) => string.Join(", ", changes.Select(c => $"{c.Kind} {c.Id} {c.From} {c.To}".Trim()));
        Check("launch changes: updated, disabled, enabled, new, and the load order; never ours, nor a new mod that's off",
            Kinds(LaunchGuard.Changes(tried, working)) == "Updated SmmTestLib 1.0 1.1 [b], Disabled SmmTestUi, Enabled SmmTestFixes, New SmmTestNew, Order",
            Kinds(LaunchGuard.Changes(tried, working)));
        List<LaunchGuard.Entry> offMoved = [working[0], working[4], working[1], working[2], working[3]];
        Check("launch changes: none for the same setup, nor for a disabled mod that moved",
            LaunchGuard.Changes(working, working).Count == 0 && LaunchGuard.Changes(offMoved, working).Count == 0,
            Kinds(LaunchGuard.Changes(offMoved, working)));

        var dir = Path.Combine(outDir, "launch-guard");
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
        var (pending, workingFile) = (Path.Combine(dir, LaunchGuard.PendingFile), Path.Combine(dir, LaunchGuard.WorkingFile));
        var now = new DateTime(2026, 10, 14, 9, 30, 0);
        var first = LaunchGuard.Begin(dir, new(now, working));
        Check("a first launch finds no record and writes its own", first.Failed == null && first.Working == null && File.Exists(pending));
        var none = LaunchGuard.Reached(dir, new(now.AddMinutes(1), working), first.Failed, first.Working);
        Check("its menu deletes the record and saves the last working setup", none.Count == 0 && !File.Exists(pending)
            && LaunchGuard.Read(workingFile)?.At == now.AddMinutes(1));
        LaunchGuard.Begin(dir, new(now.AddMinutes(5), tried)); // never reaches the menu
        List<LaunchGuard.Entry> unsubscribed = [.. tried.Where(m => m.Id != "SmmTestNew")]; // gone before the next launch
        var next = LaunchGuard.Begin(dir, new(now.AddMinutes(10), unsubscribed));
        Check("the next launch reads the record of the one that stopped, and the last working setup",
            next.Failed?.At == now.AddMinutes(5) && Listed(next.Failed?.Mods) == Listed(tried) && next.Working?.At == now.AddMinutes(1),
            $"failed {next.Failed?.At}, working {next.Working?.At}");
        var found = LaunchGuard.Reached(dir, new(now.AddMinutes(11), unsubscribed), next.Failed, next.Working);
        Check("at its menu, with other mods than the one that stopped: what that one changed, and this setup is the last working one now",
            Kinds(found) == Kinds(LaunchGuard.Changes(tried, working)) && LaunchGuard.Read(workingFile)?.At == now.AddMinutes(11), Kinds(found));
        LaunchGuard.Begin(dir, new(now.AddMinutes(12), tried)); // never reaches the menu (closed while loading)
        var again = LaunchGuard.Begin(dir, new(now.AddMinutes(13), tried));
        var same = LaunchGuard.Reached(dir, new(now.AddMinutes(14), tried), again.Failed, again.Working);
        Check("the same mods as the launch that stopped reach the menu: no notice", again.Failed != null && same.Count == 0 && !File.Exists(pending),
            Kinds(same));
        List<LaunchGuard.Entry> updated = [.. working.Select(m => m.Id == "SmmTestLib" ? m with { Version = "1.1" } : m)];
        var updates = LaunchGuard.Reached(dir, new(now.AddMinutes(15), [.. updated.Select(m => m.Id == "SmmTestLib" ? m with { Version = "1.2" } : m)]),
            new(now.AddMinutes(14), updated), new(now, working));
        Check("updates alone (Restore can't undo one): no notice", updates.Count == 0, Kinds(updates));
        LaunchGuard.Begin(dir, new(now.AddMinutes(16), tried));
        Directory.CreateDirectory(workingFile + ".tmp"); // the working setup can't be written
        try
        {
            var unsaved = LaunchGuard.Reached(dir, new(now.AddMinutes(17), working), null, null);
            Check("a working setup that can't be saved: the record still goes, the old setup stays", unsaved.Count == 0 && !File.Exists(pending)
                && LaunchGuard.Read(workingFile)?.At == now.AddMinutes(15));
        }
        catch (Exception e)
        {
            Check("a working setup that can't be saved: the record still goes, the old setup stays", false, e.Message);
        }
        Directory.Delete(workingFile + ".tmp");
        // A record that can't be deleted at the menu (#65): the working setup is still saved, and the next launch
        // takes the record, now older than that setup, as none.
        LaunchGuard.Begin(dir, new(now.AddMinutes(18), tried));
        List<LaunchGuard.Change>? locked = null;
        string? lockedError = null;
        using (new FileStream(pending, FileMode.Open, System.IO.FileAccess.Read, FileShare.None))
            try
            {
                locked = LaunchGuard.Reached(dir, new(now.AddMinutes(19), tried), null, null);
            }
            catch (Exception e)
            {
                lockedError = e.Message;
            }
        var afterLock = LaunchGuard.Begin(dir, new(now.AddMinutes(19.5), unsubscribed));
        Check("a record that can't be deleted at the menu: the working setup still saved, the next launch takes the record as none",
            lockedError == null && locked?.Count == 0 && LaunchGuard.Read(workingFile)?.At == now.AddMinutes(19) && afterLock.Failed == null
            && afterLock.Working?.At == now.AddMinutes(19), lockedError ?? $"failed {afterLock.Failed?.At}, working {afterLock.Working?.At}");
        File.WriteAllText(pending, "{\"at\":\"2026-10-14T09:"); // a write cut off
        var torn = LaunchGuard.Begin(dir, new(now.AddMinutes(20), tried));
        Check("an unreadable record counts as none", torn.Failed == null && torn.Working != null);
        var odd = Path.Combine(dir, "odd.json");
        File.WriteAllText(odd, "{\"at\":\"2026-10-14T09:30:00\",\"mods\":[{\"id\":null,\"source\":\"ModsDirectory\",\"enabled\":true}]}");
        var noId = LaunchGuard.Read(odd);
        File.WriteAllText(odd, "{\"at\":\"2026-10-14T09:30:00\",\"mods\":[{\"id\":\"SmmTestLib\",\"source\":99,\"enabled\":true}]}");
        Check("a record with a mod that has no id, or an unknown source, counts as none", noId == null && LaunchGuard.Read(odd) == null);
        File.WriteAllText(odd, "{\"mods\":[{\"id\":\"SmmTestLib\",\"source\":\"ModsDirectory\",\"enabled\":true}]}");
        var noTime = LaunchGuard.Read(odd);
        File.WriteAllText(odd, "{\"at\":\"2026-10-14T09:30:00\",\"mods\":[{\"id\":\"SmmTestLib\",\"source\":\"ModsDirectory\",\"enabled\":true}]}");
        Check("a record with no time counts as none (Copilot, #65); the same with its time reads", noTime == null && LaunchGuard.Read(odd) is { Mods.Count: 1 });

        // The notice; while another popup is up, it waits (the first notice stands in for one), then shows; shown once.
        LaunchGuard.Notice = (new LaunchGuard.Setup(now, working), found);
        LaunchGuard.ShowNotice(menu);
        await Wait(menu, 1);
        var texts = string.Join(" | ", PopupTexts());
        var parsed = string.Join(" | ", NModalContainer.Instance?.OpenModal is Node shown
            ? shown.FindChildren("*", "RichTextLabel", true, false).OfType<RichTextLabel>().Select(r => r.GetParsedText()) : []);
        Check("the notice: its title, the changes by name, a version's bracket as text, Restore and Keep", LaunchGuard.Notice == null
            && texts.Contains("The last launch stopped before the main menu") && texts.Contains("Test Framework") && parsed.Contains("(updated: 1.0 → 1.1 [b])")
            && texts.Contains("SmmTestNew") && texts.Split(" | ").Contains("Restore") && texts.Contains("Keep"), $"{texts} || {parsed}");
        CheckMark("launch notice", NModalContainer.Instance?.OpenModal as Control);
        await Capture(menu, outDir, "1e-launch-notice");
        var firstPopup = NModalContainer.Instance?.OpenModal;
        LaunchGuard.Notice = (new LaunchGuard.Setup(now, working), found);
        LaunchGuard.ShowNotice(menu);
        await Wait(menu, 0.3);
        var waited = LaunchGuard.Notice != null && NModalContainer.Instance?.OpenModal == firstPopup;
        NModalContainer.Instance?.Clear();
        await WaitReal(menu, 1.5); // ShowNotice tries again a second after it found a popup up (LaunchGuard.ShowNotice's CreateTimer(1))
        Check("the notice waits while another popup is up, then shows", waited && LaunchGuard.Notice == null
            && NModalContainer.Instance?.OpenModal is { } later && later != firstPopup, $"waited {waited}");
        NModalContainer.Instance?.Clear();
        await Wait(menu, 0.5);
        LaunchGuard.ShowNotice(menu);
        await Wait(menu, 0.3);
        Check("the notice is shown once", NModalContainer.Instance?.OpenModal == null, NModalContainer.Instance?.OpenModal?.GetType().Name ?? "");

        // Restore, to the list as it is but reversed, Test Fixes, Test Addon (it can't load) and ours off, and Test UI
        // missing (installed since). Then again with Test Framework missing too, which Test Content (on) needs.
        var settings = SaveManager.Instance.SettingsSave.ModSettings;
        var listWas = settings?.ModList.Select(e => new SettingsSaveMod { Id = e.Id, Source = e.Source, IsEnabled = e.IsEnabled }).ToList() ?? [];
        var relaunches = 0;
        ModEntry.TestRelaunch = plain => relaunches += plain ? 0 : 1; // through the game's quit (a plain one doesn't count)
        try
        {
            var target = History.CurrentMods().Where(m => m.Id != "SmmTestUi").Reverse()
                .Select(m => new LaunchGuard.Entry(m.Id, m.Source, m.Enabled && m.Id is not ("SmmTestFixes" or "SmmTestAddon" or ModEntry.ModId), null)).ToList();
            LaunchGuard.Restore(target, menu);
            await Wait(menu, 0.3);
            var after = string.Join(", ", History.CurrentMods().Select(m => $"{m.Id}={m.Enabled}"));
            // Ours first whatever the order (LoadOrder.OursFirst: it has to load before any copy it picks for).
            var expected = string.Join(", ", target.OrderBy(m => m.Id == ModEntry.ModId ? 0 : 1).Select(m => $"{m.Id}={m.Enabled || m.Id == ModEntry.ModId}")
                .Concat(Enumerable.Repeat("SmmTestUi=False", Dependencies.Find("SmmTestUi") != null ? 1 : 0)));
            Check("Restore: the working list back in its order (ours first), a mod added since off, ours on, then a relaunch",
                relaunches == 1 && !RelaunchCheck.IsOpen && after == expected, $"{relaunches} relaunches; {after}; expected {expected}");
            RelaunchCheck.Close();

            if (Dependencies.Find("SmmTestLib") == null || !target.Any(m => m.Id == "SmmTestContent" && m.Enabled))
                Results.Add("SKIP Restore with a mod that needs one added since: no Test Framework, or Test Content off");
            else
            {
                relaunches = 0;
                LaunchGuard.Restore([.. target.Where(m => m.Id != "SmmTestLib")], menu);
                await Wait(menu, 0.3);
                var body = (menu.GetTree().Root.GetNodeOrNull(RelaunchCheck.Name)?.FindChild("Body", true, false) as RichTextLabel)?.GetParsedText() ?? "";
                Check("Restore with a mod left on that needs one added since: the relaunch check's dialog, no relaunch yet",
                    relaunches == 0 && RelaunchCheck.IsOpen && body.Contains("Test Framework"), $"{relaunches} relaunches: {body}");
            }
        }
        finally
        {
            ModEntry.TestRelaunch = null;
            RelaunchCheck.Close();
            if (settings != null)
            {
                settings.ModList.Clear();
                settings.ModList.AddRange(listWas);
                SaveManager.Instance.SaveSettings();
            }
        }
    }

    // The game's startup error (StartupErrorPatch): LaunchGuard.CrashOffer offers the last working setup only when this
    // launch changed mods since it, beyond updates, and says when the game changed too. The dialog in place of the
    // game's: its title, the changes, Quit (nothing written) and Restore and restart (the working list, then a plain
    // relaunch). Quit and the relaunch are caught here, and the saved list is put back.
    static async Task TestCrashOffer(NMainMenu menu, string outDir)
    {
        var now = DateTime.Now;
        static LaunchGuard.Entry Mod(string id, bool on, string? version = "1.0") => new(id, ModSource.ModsDirectory, on, version);
        LaunchGuard.Setup Setup(List<LaunchGuard.Entry> mods, string game = "0.111.0 (abc)") => new(now, mods, game);
        List<LaunchGuard.Entry> working = [Mod(ModEntry.ModId, true), Mod("SmmTestFixes", true), Mod("SmmTestLib", true)];
        var none = LaunchGuard.CrashOffer(Setup(working), null);
        var same = LaunchGuard.CrashOffer(Setup(working), Setup(working));
        var updated = LaunchGuard.CrashOffer(Setup([Mod(ModEntry.ModId, true), Mod("SmmTestFixes", true, "1.1"), Mod("SmmTestLib", true)]), Setup(working));
        var added = LaunchGuard.CrashOffer(Setup([.. working, Mod("SmmTestUi", true)]), Setup(working));
        var newGame = LaunchGuard.CrashOffer(Setup([.. working, Mod("SmmTestUi", true)], "0.112.0 (def)"), Setup(working));
        Check("after a failed start: nothing to offer without a working setup, with the same mods or with only an update; "
            + "a mod new since: offered; a game update since: said",
            none == null && same == null && updated == null && added is { GameChanged: false } offered
            && offered.Changes.Any(c => c.Id == "SmmTestUi" && c.Kind == LaunchGuard.Kind.New) && newGame is { GameChanged: true },
            $"none {none != null}, same {same != null}, updated {updated != null}, added {added?.Changes.Count}, new game {newGame?.GameChanged}");
        // The list Restore would write against the one the launch read: no offer when they load the same (after a
        // Restore, or an order the game's sorting sets), whatever the order of the mods off.
        static (string, ModSource, bool) E(string id, bool on) => (id, ModSource.ModsDirectory, on);
        List<(string, ModSource, bool)> read = [E(ModEntry.ModId, true), E("A", true), E("B", true), E("C", false), E("D", false)];
        Check("after a failed start: the same load with the mods off in another order; not with mods on in another order, one switched, or one missing (it would load)",
            LaunchGuard.SameLoad(read, read) && LaunchGuard.SameLoad(read, [read[0], read[1], read[2], read[4], read[3]])
            && !LaunchGuard.SameLoad(read, [read[0], read[2], read[1], read[3], read[4]]) && !LaunchGuard.SameLoad(read, [.. read[..3], E("C", true), read[4]])
            && !LaunchGuard.SameLoad(read, read[..4]));

        var settings = SaveManager.Instance.SettingsSave.ModSettings;
        if (settings == null || NGame.Instance is not { } game || Dependencies.Find("SmmTestFixes") is not { } fixes || !Copies.Ticked(fixes))
        {
            Results.Add("SKIP the dialog after a failed start: no mod list, game, or Test Fixes on");
            return;
        }
        var listWas = settings.ModList.Select(e => new SettingsSaveMod { Id = e.Id, Source = e.Source, IsEnabled = e.IsEnabled }).ToList();
        // This launch as it is; the working setup the same but with Test Fixes off (so it was enabled since).
        var current = History.CurrentMods().Select(m => new LaunchGuard.Entry(m.Id, m.Source, m.Enabled, null)).ToList();
        var before = current.Select(m => m.Id == "SmmTestFixes" ? m with { Enabled = false } : m).ToList();
        // The guard below compares with the list as the game read it (Begin's copy), not the one it put in place once the
        // mods loaded, which names every installed mod: dev.cs's list names only a few.
        var listNow = SaveManager.Instance.SettingsSave.ModSettings?.ModList.Count ?? 0;
        Check("after a failed start: the list compared is the one the game read, from before it put its own in place",
            LaunchGuard.ReadList is { } readList && readList.Count < listNow, $"read {LaunchGuard.ReadList?.Count}, now {listNow}");
        // An order the game's own sorting sets (a dependency an update added) against the list a Restore wrote: no
        // offer, or each Restore would fail the same and offer it again. The same order as the player's own: offered.
        // Mods listed once: the game sorts an id by its last entry, so swapping one of two copies' entries loads nothing differently.
        var on = current.Select((m, i) => (m, i)).Where(p => p.m.Enabled && p.m.Id is not (ModEntry.ModId or ModDoctor.ModId) && current.Count(c => c.Id == p.m.Id) == 1)
            .Select(p => p.i).Take(2).ToList();
        if (on.Count == 2)
        {
            var sorted = current.ToList();
            (sorted[on[0]], sorted[on[1]]) = (sorted[on[1]], sorted[on[0]]);
            var restored = LaunchGuard.Written(current).Select(e => (e.Id, e.Source, e.IsEnabled)).ToList();
            var reordered = restored.ToList();
            var (first, second) = (reordered.FindIndex(e => e.Id == current[on[0]].Id), reordered.FindIndex(e => e.Id == current[on[1]].Id));
            (reordered[first], reordered[second]) = (reordered[second], reordered[first]);
            var forced = LaunchGuard.CrashOffer(new(now, sorted), new(now.AddHours(-1), current), restored);
            var chosen = LaunchGuard.CrashOffer(new(now, sorted), new(now.AddHours(-1), current), reordered);
            Check("after a failed start: no offer for a load order the game's sorting sets (the list read is the one Restore writes); "
                + "offered for one the player's list sets", forced == null && chosen is { } order && order.Changes.Any(c => c.Kind == LaunchGuard.Kind.Order),
                $"forced {forced != null}, chosen {string.Join(", ", chosen?.Changes.Select(c => c.Kind) ?? [])}");
        }
        else
            Results.Add("SKIP the offer for a sorted load order: fewer than two fixture mods on");
        if (LaunchGuard.CrashOffer(new(now, current), new(now.AddHours(-1), before)) is not { } offer)
        {
            Check("the dialog after a failed start: an offer to show", false);
            return;
        }
        var (quits, relaunches) = (0, 0);
        ModEntry.TestQuit = () => quits++;
        ModEntry.TestRelaunch = plain => relaunches += plain ? 1 : 0; // a plain quit (the game's would save a half-started game)
        var transitionWas = game.Transition.Visible; // the dialog's setup hides the startup transition
        var pendingPath = Path.Combine(LaunchGuard.Dir ?? OS.GetUserDataDir(), LaunchGuard.PendingFile);
        var root = menu.GetTree().Root;
        // The game's own message for a failed start (its dialog, which ours replaces), as each dialog has it: from the
        // game's text, and in English from its file. Not empty: the game has one.
        var gamesMessage = LaunchGuard.GamesMessage(plain: false).Trim();
        var gamesMessagePlain = LaunchGuard.GamesMessage(plain: true).Trim();
        // Styled: "Notice from the game" centred, then the message alone in an outlined, faintly filled box (a one-cell
        // table), our text after. Plain (no markup): the same words over the message.
        var boxed = System.Text.RegularExpressions.Regex.Match(gamesMessage, @"^\[center\]\[color=#[0-9a-f]{6}\]Notice from the game\[/color\]\[/center\]\n"
            + @"\[table=1\]\[cell expand=1 border=#[0-9a-f]{8} bg=#[0-9a-f]{8} padding=[0-9,]+\]\[center\](?<message>.+)\[/center\]\[/cell\]\[/table\]$");
        Check("the game's own message for a failed start, as the dialogs show it: under \"Notice from the game\" (centred), alone in its outlined box; "
            + "plain, under the same words",
            boxed.Success && boxed.Groups["message"].Value.Length > 30 && !boxed.Groups["message"].Value.Contains("Notice from the game")
            && gamesMessagePlain.StartsWith("Notice from the game:\n") && gamesMessagePlain.Length > 50,
            $"{gamesMessage} | {gamesMessagePlain}");
        // Our popup's button (marked: no other popup's), by its node and signal names (the same in every game version so far).
        static void Press(string button)
        {
            if (NModalContainer.Instance?.OpenModal is Node popup && popup.FindChild(Menus.MarkName, true, false) != null
                && popup.GetNodeOrNull($"VerticalPopup/{button}") is { } found)
                found.EmitSignal("Released", found);
        }
        string PlainTexts() => root.GetNodeOrNull<ConfirmationDialog>(LaunchGuard.PlainDialogName) is { Visible: true } plain
            ? $"{plain.Title} | {plain.DialogText} | {plain.OkButtonText} | {plain.CancelButtonText}" : "";
        void Answer(StringName signal) => root.GetNodeOrNull<ConfirmationDialog>(LaunchGuard.PlainDialogName)?.EmitSignal(signal);
        try
        {
            // A launch record as a failed start leaves one (none is left at the menu): an answer, Quit too, deletes it.
            File.WriteAllText(pendingPath, "{}");
            var asked = LaunchGuard.AskAfterCrash(game, offer);
            await Wait(menu, 0.8);
            var texts = string.Join(" | ", PopupTexts());
            CheckMark("dialog after a failed start", NModalContainer.Instance?.OpenModal as Control);
            await Capture(menu, outDir, "1f-crash-dialog");
            Press("NoButton");
            await Wait(menu, 0.3);
            Check("the dialog after a failed start: its title, the game's own message (marked as the game's), what changed, Quit and Restore and restart; "
                + "Quit writes no list, deletes the launch's record and quits",
                asked.IsCompleted && texts.Contains("The game couldn't start") && texts.Contains(gamesMessage) && texts.Contains("Test Fixes") && texts.Contains("Restore and restart")
                && quits == 1 && relaunches == 0 && Copies.Ticked(fixes) && !File.Exists(pendingPath),
                $"{texts}; quits {quits}, relaunches {relaunches}, record left {File.Exists(pendingPath)}");
            asked = LaunchGuard.AskAfterCrash(game, offer);
            await Wait(menu, 0.8);
            Press("YesButton");
            await Wait(menu, 0.3);
            Check("the dialog after a failed start: Restore and restart writes the working list (Test Fixes off again), then relaunches",
                asked.IsCompleted && relaunches == 1 && quits == 1 && !Copies.Ticked(fixes), $"quits {quits}, relaunches {relaunches}, Test Fixes on {Copies.Ticked(fixes)}");

            // Where the game's text can't load (a mod broke it): Godot's plain dialog, in English.
            settings.ModList.Clear();
            settings.ModList.AddRange(listWas.Select(e => new SettingsSaveMod { Id = e.Id, Source = e.Source, IsEnabled = e.IsEnabled }));
            LaunchGuard.TestNoLanguage = true;
            asked = LaunchGuard.AskAfterCrash(game, offer);
            await Wait(menu, 0.5);
            var plain = PlainTexts();
            Answer(AcceptDialog.SignalName.Canceled);
            await Wait(menu, 0.3);
            Check("the plain dialog after a failed start (no game text): its title, the game's own message in English, what changed, Quit and Restore and restart; "
                + "Quit writes nothing and quits",
                asked.IsCompleted && plain.Contains("The game couldn't start") && plain.Contains(gamesMessagePlain) && plain.Contains("Test Fixes (enabled)") && plain.Contains("Restore and restart")
                && plain.Contains("Quit") && quits == 2 && relaunches == 1 && Copies.Ticked(fixes), $"{plain}; quits {quits}, relaunches {relaunches}");
            asked = LaunchGuard.AskAfterCrash(game, offer);
            await Wait(menu, 0.5);
            Answer(AcceptDialog.SignalName.Confirmed);
            await Wait(menu, 0.3);
            Check("the plain dialog after a failed start: Restore and restart writes the working list, then relaunches; the dialog goes",
                asked.IsCompleted && relaunches == 2 && quits == 2 && !Copies.Ticked(fixes) && root.GetNodeOrNull(LaunchGuard.PlainDialogName) == null,
                $"quits {quits}, relaunches {relaunches}, Test Fixes on {Copies.Ticked(fixes)}, dialog left {root.GetNodeOrNull(LaunchGuard.PlainDialogName) != null}");
            // Mod Doctor's suspects (made up: Test UI, which this launch has on, among them, ours and Mod Doctor never
            // turned off, and one not installed): three named, the rest under "more", whose card names them; a third button
            // as a quiet link under the game's two, which turns them off and restarts, the rest of this launch's list as it was. Escape
            // stays the game's Quit.
            LaunchGuard.TestNoLanguage = false;
            ModDoctor.TestSuspects = [ModEntry.ModId, ModDoctor.ModId, "SmmTestUi", "SmmTestFixes", "SmmTestLib", "SmmTestNotThere"];
            void ListAsItWas()
            {
                settings.ModList.Clear();
                settings.ModList.AddRange(listWas.Select(e => new SettingsSaveMod { Id = e.Id, Source = e.Source, IsEnabled = e.IsEnabled }));
            }
            bool Listed(string id, bool on) => settings.ModList.Any(e => e.Id == id && e.IsEnabled == on);
            // Every entry of an id off, whatever its source (a mod listed from two sources has two).
            bool AllOff(string id) => settings.ModList.Any(e => e.Id == id) && settings.ModList.Where(e => e.Id == id).All(e => !e.IsEnabled);
            ListAsItWas();
            var libWasOn = Listed("SmmTestLib", true);
            asked = LaunchGuard.AskAfterCrash(game, offer);
            await Wait(menu, 0.8);
            var popup = NModalContainer.Instance?.OpenModal as Control;
            var third = popup?.FindChild(LaunchGuard.SuspectsButtonName, true, false) as Control;
            var (quitButton, restoreButton) = (popup?.GetNodeOrNull<Control>("VerticalPopup/NoButton"), popup?.GetNodeOrNull<Control>("VerticalPopup/YesButton"));
            // A quiet link under the two, centred on the popup's art, overlapping neither: orange, a pointer over it, lit and
            // underlined on hover.
            var art = popup?.GetNodeOrNull<Control>("VerticalPopup");
            var between = third is LinkButton { Underline: LinkButton.UnderlineMode.Never, MouseDefaultCursorShape: Control.CursorShape.PointingHand } link
                && art != null && quitButton != null && restoreButton != null && link.GetGlobalRect().Position.Y >= restoreButton.GetGlobalRect().End.Y - 4
                && Math.Abs(link.GetGlobalRect().GetCenter().X - art.GetGlobalRect().GetCenter().X) < 2 && link.Size.X > 100
                && !link.GetGlobalRect().Intersects(quitButton.GetGlobalRect()) && !link.GetGlobalRect().Intersects(restoreButton.GetGlobalRect())
                && link.GetThemeColor("font_color") is { R: > 0.9f, G: > 0.6f and < 0.8f, B: < 0.5f } && link.GetThemeColor("font_hover_color") != link.GetThemeColor("font_color")
                && art.GetNodeOrNull<Control>(Menus.MarkName) is { } mark && mark.GetGlobalRect().Position.Y >= link.GetGlobalRect().End.Y; // our mark under it, clear of it
            var suspectTexts = string.Join(" | ", PopupTexts());
            await Capture(menu, outDir, "1h-crash-dialog-suspects");
            var suspectsCard = popup?.GetNodeOrNull<RichTextLabel>("VerticalPopup/Description") is { } description
                ? await LinkCard(description, "spire-mod-manager-more-suspects") : "";
            Check("the dialog with Mod Doctor's suspects: three named, the rest counted as a link whose card names them; a third button, "
                + "\"Disable suspect mods and restart\", a quiet orange link under Quit and Restore and restart, centred",
                third is LinkButton { Visible: true, Text: "Disable suspect mods and restart" } && between && suspectTexts.Contains("3 more")
                && suspectsCard.Contains("Test Fixes") && suspectsCard.Contains("SmmTestNotThere") && !suspectsCard.Contains("Test UI"),
                $"third {third != null}, between {between}; {suspectTexts}; card: {suspectsCard}");
            Input.ParseInputEvent(new InputEventKey { Keycode = Key.Escape, Pressed = true });
            await Wait(menu, 0.1);
            Input.ParseInputEvent(new InputEventKey { Keycode = Key.Escape, Pressed = false });
            await Wait(menu, 0.3);
            Check("Escape on that dialog is still the game's Quit, not the third button", asked.IsCompleted && quits == 3 && relaunches == 2
                && Listed("SmmTestUi", true) == listWas.Any(e => e.Id == "SmmTestUi" && e.IsEnabled), $"quits {quits}, relaunches {relaunches}");
            if (!asked.IsCompleted)
                Press("NoButton");
            // On stable the same Escape then reaches the main menu, where it's the profile button's hotkey (pauseAndBack):
            // the profile screen opens. A player's game would have quit; here, back to the main menu for what follows.
            for (var i = 0; i < 3 && menu.SubmenuStack.SubmenusOpen; i++)
            {
                menu.SubmenuStack.Pop();
                await Wait(menu, 0.3);
            }
            ListAsItWas();
            asked = LaunchGuard.AskAfterCrash(game, offer);
            await Wait(menu, 0.8);
            if ((NModalContainer.Instance?.OpenModal as Node)?.FindChild(LaunchGuard.SuspectsButtonName, true, false) is BaseButton suspectsButton)
                suspectsButton.EmitSignal(BaseButton.SignalName.Pressed);
            await Wait(menu, 0.3);
            Check("Disable suspect mods and restart: this launch's list with Test UI, Test Fixes and Test Framework off (not ours, not Mod Doctor, "
                + "nothing added for a mod not installed), then a restart; the launch's record goes",
                asked.IsCompleted && relaunches == 3 && quits == 3 && AllOff("SmmTestUi") && AllOff("SmmTestFixes")
                && (!libWasOn || AllOff("SmmTestLib")) && Listed(ModEntry.ModId, true) && !settings.ModList.Any(e => e.Id == "SmmTestNotThere")
                && !File.Exists(pendingPath),
                $"quits {quits}, relaunches {relaunches}; {string.Join(", ", settings.ModList.Select(e => $"{e.Id}={e.IsEnabled}"))}");
            // The plain dialog: the third answer as one more button.
            ListAsItWas();
            LaunchGuard.TestNoLanguage = true;
            asked = LaunchGuard.AskAfterCrash(game, offer);
            await Wait(menu, 0.5);
            var plainDialog = root.GetNodeOrNull<ConfirmationDialog>(LaunchGuard.PlainDialogName);
            var plainThird = plainDialog?.FindChildren("*", "Button", true, false).OfType<Button>().Any(b => b.Text == "Disable suspect mods and restart") == true;
            plainDialog?.EmitSignal(AcceptDialog.SignalName.CustomAction, "disable_suspects");
            await Wait(menu, 0.3);
            Check("the plain dialog with suspects: the third button, which turns them off and restarts",
                plainThird && asked.IsCompleted && relaunches == 4 && quits == 3 && AllOff("SmmTestUi") && Listed(ModEntry.ModId, true),
                $"button {plainThird}, quits {quits}, relaunches {relaunches}");
            ModDoctor.TestSuspects = null;
            ListAsItWas();

            // At its longest: more changes than it lists and a game update since, captured to see that it all fits.
            LaunchGuard.TestNoLanguage = false;
            List<LaunchGuard.Entry> many = [.. Enumerable.Range(1, 7).Select(i => Mod($"SmmTestMadeUp{i}", true))];
            if (LaunchGuard.CrashOffer(Setup([.. working, .. many], "0.112.0 (def)"), Setup(working)) is { } longest)
            {
                asked = LaunchGuard.AskAfterCrash(game, longest);
                await Wait(menu, 0.8);
                await Capture(menu, outDir, "1g-crash-dialog-longest");
                var longTexts = string.Join(" | ", PopupTexts());
                var moreCard = (NModalContainer.Instance?.OpenModal as Node)?.GetNodeOrNull<RichTextLabel>("VerticalPopup/Description") is { } longDescription
                    ? await LinkCard(longDescription, "spire-mod-manager-more-changes") : "";
                var noThird = (NModalContainer.Instance?.OpenModal as Node)?.FindChild(LaunchGuard.SuspectsButtonName, true, false) == null;
                Press("NoButton");
                await Wait(menu, 0.3);
                Check("the dialog at its longest (7 changes, a game update since): 4 listed, 3 counted, the update said, the game's message first; "
                    + "no suspects, so no third button",
                    asked.IsCompleted && longTexts.Contains("3 more") && longTexts.Contains("The game itself was updated") && longTexts.Contains(gamesMessage) && noThird, longTexts);
                Check("the dialog at its longest: \"3 more\" is a link whose card names the three changes it counts",
                    moreCard.Contains("SmmTestMadeUp5") && moreCard.Contains("SmmTestMadeUp7") && !moreCard.Contains("SmmTestMadeUp4"), moreCard);
            }
            else
                Check("the dialog at its longest: an offer for 7 new mods and a game update since", false, "no offer");
        }
        finally
        {
            ModEntry.TestQuit = null;
            ModEntry.TestRelaunch = null;
            LaunchGuard.TestNoLanguage = false;
            ModDoctor.TestSuspects = null;
            game.Transition.Visible = transitionWas;
            NModalContainer.Instance?.Clear();
            root.GetNodeOrNull(LaunchGuard.PlainDialogName)?.QueueFree();
            File.Delete(pendingPath);
            settings.ModList.Clear();
            settings.ModList.AddRange(listWas);
            SaveManager.Instance.SaveSettings();
        }
    }

    // dev.cs --crash-prepare, then --crash: the second launch fails to start as a mod that stops every launch makes it
    // (a made-up throw where the game's startup goes on after the mods load), and the dialog that replaces the game's is
    // answered as a player would: Restore and restart. The first launch only reaches the main menu, so the last working
    // setup is saved; the second has Test Retired on besides (dev.cs), so it differs. The relaunch is caught.
    public static void EarlyStart()
    {
        var crashMode = System.Environment.GetEnvironmentVariable("SPIRE_MOD_MANAGER_SELFTEST_MODE");
        if (crashMode is not ("crash" or "crash-real")
            || System.Environment.GetEnvironmentVariable("SPIRE_MOD_MANAGER_SELFTEST") is not { Length: > 0 } outDir)
            return;
        // Until answered, the result is that it wasn't: the game may quit before the watcher below sees anything.
        var results = Path.Combine(outDir, "results.txt");
        Directory.CreateDirectory(outDir);
        File.WriteAllLines(results, ["FAIL a failed start: the game ended before our dialog was answered"]);
        if (crashMode == "crash") // crash-real: the --workshop mods fail the start themselves
        {
            try
            {
                new HarmonyLib.Harmony(ModEntry.ModId + ".SelfTestCrash").Patch(
                    HarmonyLib.AccessTools.Method(typeof(MegaCrit.Sts2.Core.Helpers.OneTimeInitialization), "ExecuteEssential"),
                    prefix: new HarmonyLib.HarmonyMethod(typeof(SelfTest), nameof(MadeUpFailure)));
            }
            catch (Exception e)
            {
                Results.Add($"FAIL a failed start: the made-up failure couldn't be patched in: {e.Message}"); // Run writes these at the menu
                File.WriteAllLines(results, Results);
                return; // the game starts; Run, at the main menu, says so too
            }
        }
        var (quits, relaunches) = (0, 0);
        ModEntry.TestQuit = () => quits++;
        ModEntry.TestRelaunch = plain => relaunches += plain ? 1 : 0; // a plain quit (the game's would save a half-started game)
        var tree = (SceneTree)Engine.GetMainLoop();
        var started = DateTime.Now;
        string? texts = null;
        void Finish()
        {
            tree.ProcessFrame -= Watch;
            try
            {
                File.WriteAllLines(results, Results);
            }
            finally
            {
                tree.Quit();
            }
        }
        void Watch()
        {
            try
            {
                if (texts == null && NModalContainer.Instance?.OpenModal is Node popup)
                {
                    texts = string.Join(" | ", PopupTexts());
                    if (!texts.Contains("The game couldn't start"))
                    {
                        Check("a failed start: our dialog in place of the game's", false, texts);
                        Finish();
                        return;
                    }
                    popup.GetNodeOrNull("VerticalPopup/YesButton")?.EmitSignal("Released", popup.GetNode("VerticalPopup/YesButton"));
                    return;
                }
                // Or the plain one, where a mod broke the game's text.
                if (texts == null && tree.Root.GetNodeOrNull<ConfirmationDialog>(LaunchGuard.PlainDialogName) is { Visible: true } plain)
                {
                    texts = $"{plain.Title} | {plain.DialogText} | {plain.OkButtonText} | {plain.CancelButtonText}";
                    Results.Add("INFO a failed start: the plain dialog (the game's text didn't load)");
                    plain.EmitSignal(AcceptDialog.SignalName.Confirmed);
                    return;
                }
                if (relaunches + quits > 0)
                {
                    var retired = SaveManager.Instance.SettingsSave.ModSettings?.ModList.Where(e => e.Id == "SmmTestRetired").Select(e => e.IsEnabled).ToList() ?? [];
                    var pending = LaunchGuard.Dir is { } dir && File.Exists(Path.Combine(dir, LaunchGuard.PendingFile));
                    Check("a failed start: our dialog in place of the game's, naming what changed since the last working launch",
                        texts != null && texts.Contains("The game couldn't start") && texts.Contains("Test Retired") && texts.Contains("Restore and restart"), texts ?? "no dialog");
                    Check("a failed start: Restore and restart writes the last working list (Test Retired off again), clears this launch's record, then relaunches",
                        relaunches == 1 && quits == 0 && retired.Count > 0 && retired.All(on => !on) && !pending,
                        $"relaunches {relaunches}, quits {quits}, Test Retired {string.Join(",", retired)}, record left {pending}");
                    Finish();
                }
                else if (DateTime.Now - started > TimeSpan.FromSeconds(90))
                {
                    Check("a failed start: our dialog, answered, within 90 s", false, $"dialog {texts ?? "none"}, relaunches {relaunches}, quits {quits}");
                    Finish();
                }
            }
            catch (Exception e)
            {
                Results.Add($"FAIL exception: {e}");
                Finish();
            }
        }
        tree.ProcessFrame += Watch;
    }

    static void MadeUpFailure() => throw new InvalidOperationException("Spire Mod Manager dev run (--crash): a made-up failed start");

    // --crash-prepare: the main menu reached, so the last working setup is saved (and this launch's record gone).
    static async Task RunCrashPrepare(NMainMenu menu, string outDir)
    {
        try
        {
            await Wait(menu, 3);
            var dir = LaunchGuard.Dir ?? "";
            Check("--crash-prepare: the main menu reached, the last working setup saved",
                File.Exists(Path.Combine(dir, LaunchGuard.WorkingFile)) && !File.Exists(Path.Combine(dir, LaunchGuard.PendingFile)), dir);
        }
        catch (Exception e)
        {
            Results.Add($"FAIL exception: {e}");
        }
        finally
        {
            File.WriteAllLines(Path.Combine(outDir, "results.txt"), [.. Results, .. Timing()]);
            menu.GetTree().Quit();
        }
    }

    // The main menu button's Open translations folder: the folder, in our data folder beside manager.json, is made if it
    // isn't there (a fresh sandbox has none; an empty one is removed first) and handed to the system's file browser (to Translations.TestOpen here, so
    // no window opens).
    static async Task TestTranslationsFolder(NMainMenu menu)
    {
        if (menu.GetNodeOrNull<Control>("SpireModManagerMenuButton") is not { } button)
            return;
        var folder = Translations.Folder;
        if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
            Directory.Delete(folder); // an empty one left over: gone, so the press has to make it
        var before = Directory.Exists(folder);
        string? opened = null;
        Translations.TestOpen = path => opened = path;
        try
        {
            if (await ButtonMenu(menu, button) is { } popup)
            {
                // It opens the system's file browser (#126): the external-link mark after its label, in the label's
                // colour, and only there in the menu.
                var labels = popup.FindChildren("*", "Label", true, false).OfType<Label>().ToList();
                var item = labels.FirstOrDefault(l => l.Text == "Open translations folder");
                var mark = item == null ? null : LinkMark.Rect(item);
                var itemSize = item?.GetThemeFontSize("font_size") ?? 0;
                var itemText = item == null ? 0 : item.GetThemeFont("font").GetStringSize(item.Text, fontSize: itemSize).X;
                Check("Open translations folder: the external-link mark a small gap after its label, 14 px for its 20 px text; no other item has one",
                    item != null && itemSize == 20 && mark is { } at && at.Position.X >= itemText + 2 && at.Position.X <= itemText + 7 && at.Size == new Vector2(14, 14)
                    && at.End.X <= item.Size.X + 0.5f && labels.Where(l => l.IsInGroup(LinkMark.Group)).SequenceEqual([item]),
                    $"mark {mark} after text {itemText:0.#}, label {item?.Size}; marked: {string.Join(", ", labels.Where(l => l.IsInGroup(LinkMark.Group)).Select(l => l.Text))}");
                var drawn = item == null ? null : await MarkDrawn(menu, item);
                Check("Open translations folder's mark: drawn in the label's colour", item != null && drawn == item.GetThemeColor("font_color"),
                    $"drawn {drawn?.ToString() ?? "nothing"}, label {item?.GetThemeColor("font_color")}");
                MenuPanel.Press(popup, "Open translations folder");
            }
            await Wait(menu, 0.2);
            Check("Open translations folder: made in the data folder and opened, the menu closed", !before && opened == folder && Directory.Exists(folder)
                && folder.Replace('\\', '/').EndsWith($"/mod_data/{ModEntry.ModId}/translations") && !MenuPanel.IsOpen,
                $"opened {opened ?? "nothing"}; {folder} there before: {before}");
            // The example: how-to lines, then every English text of ours, under a name no language has.
            var example = Path.Combine(folder, Translations.ExampleFile);
            var text = File.Exists(example) ? File.ReadAllText(example) : "";
            Dictionary<string, string>? pairs = null;
            try
            {
                pairs = JsonSerializer.Deserialize<Dictionary<string, string>>(text, new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip });
            }
            catch (JsonException)
            {
            }
            var english = JsonSerializer.Deserialize<Dictionary<string, string>>(Godot.FileAccess.GetFileAsString($"res://{ModEntry.ModId}/localization/eng/settings_ui.json"));
            Check("the translations folder has the example: how-to lines, then every English text, never read (no language's name)",
                text.StartsWith("// An example") && pairs != null && english != null && pairs.Count == english.Count && english.All(e => pairs.GetValueOrDefault(e.Key) == e.Value)
                && !Path.GetFileNameWithoutExtension(Translations.ExampleFile).All(char.IsLetter), $"{text.Length} chars, {pairs?.Count} texts of {english?.Count}");
            // Its codes: the languages this game has, every one but English (stable has fewer than beta), within 118 columns.
            var codeLines = Translations.CodeLines().ToList();
            var listed = codeLines.SelectMany(l => System.Text.RegularExpressions.Regex.Matches(l, @"(?<![\w(])[a-z]{3}(?=[ ,.])").Select(m => m.Value)).ToList();
            Check("the example's codes: the game's languages but English, each once, wrapped within 118 columns, in the example",
                listed.Order().SequenceEqual(LocManager.Languages.Where(c => c != "eng").Order()) && codeLines.All(l => l.Length <= 118 && text.Contains(l)),
                $"{string.Join(" ", listed)} vs {string.Join(" ", LocManager.Languages)}");
            // Written again while it's as we left it (an older version's, say); once edited, it's the player's.
            File.WriteAllText(example, "// an older example\n{}");
            File.SetLastWriteTimeUtc(example, Translations.ExampleTime);
            Translations.Open();
            var refreshed = File.ReadAllText(example) == Translations.ExampleText();
            File.WriteAllText(example, "// my translation, started here\n{}");
            Translations.Open();
            Check("the example: written again while it's as we left it, kept once it's edited", refreshed
                && File.ReadAllText(example) == "// my translation, started here\n{}", $"refreshed {refreshed}");
            File.Delete(example);
            Translations.Open();
        }
        finally
        {
            Translations.TestOpen = null;
            MenuPanel.Close();
        }
    }

    // The page's text shaped ahead on the main menu (Prewarm): only texts with a letter Kreon lacks (a Chinese alias
    // here; the fixtures' own text is Latin), each once, and the pass gets to the end.
    static async Task TestPrewarm(NMainMenu menu)
    {
        const string Alias = "测试别名";
        var before = Store.InfoFor("SmmTestContent")?.Alias;
        Store.EditMod("SmmTestContent", info => info.Alias = Alias);
        try
        {
            Prewarm.Start(menu);
            for (var i = 0; i < 50 && !Prewarm.Done; i++)
                await WaitReal(menu, 0.1); // Prewarm starts a second after Start (StartDelay), then works a frame at a time
            // Once if Kreon lacks its letters; not at all where a mod gave the font Chinese ones (RitsuLib-based mods can).
            var font = ModEntry.Font("res://themes/kreon_regular_shared.tres");
            var lacks = Alias.EnumerateRunes().Any(r => !font.HasChar(r.Value));
            Check("the page's text is shaped ahead on the main menu: a Chinese alias once (none if the font has its letters), nothing all-Latin, to the end",
                Prewarm.Done && Prewarm.Shaped.Count(t => t == Alias) == (lacks ? 1 : 0) && !Prewarm.Shaped.Any(t => t.All(c => c < 0x80)),
                $"done {Prewarm.Done}, the font lacks its letters {lacks}: {string.Join(" | ", Prewarm.Shaped)}");
        }
        finally
        {
            Store.EditMod("SmmTestContent", info => info.Alias = before);
        }
    }

    // Drop-in translations (Translations) in German, which has no packed translation, so our text is all English there
    // until now: a file in the translations folder, read as the game switches language (its own switch, as in its
    // settings, without the menu rebuild). Its text shows, in the game's table and on the main menu button's menu, and its
    // {count} takes German numbers; a key it leaves out, an empty text, a number, a broken placeholder and one English
    // doesn't fill in (renamed or added) show in English, and a key English doesn't have is left out.
    // Back in English, English. A file that isn't JSON, isn't an object or is over 1 MB changes nothing and throws
    // nothing. One saved as deu.json.txt (a browser's "Save as", the .txt hidden) is read. The file is removed and the
    // language set back afterwards.
    // The SmartFormat version tools/loc.cs judges translations with (its #:package; check.cs keeps the two equal).
    const string ToolParser = "3.3.0";

    // A key's text in our packed English, as written (placeholders and all), whatever the game's language is now.
    static string EnglishText(string key) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(Godot.FileAccess.GetFileAsString($"res://{ModEntry.ModId}/localization/eng/settings_ui.json"))
            ?.GetValueOrDefault(key) ?? "";

    static async Task TestTranslations(NMainMenu menu)
    {
        // The tool reads texts as the game does only with the game's parser: when the game ships another version, update
        // the package in tools/loc.cs and ToolParser here.
        var parser = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "SmartFormat")
            ?.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+')[0];
        Check($"translation tools: the game's text parser is SmartFormat {ToolParser}, the one tools/loc.cs uses", parser == ToolParser,
            $"the game has {parser ?? "no SmartFormat loaded"}");
        var start = LocManager.Instance.Language;
        if (start != "eng" || Godot.FileAccess.FileExists($"res://{ModEntry.ModId}/localization/deu/settings_ui.json"))
        {
            Results.Add($"SKIP drop-in translations: the game is in {start}, or German is packed");
            return;
        }
        const string Translated = "SPIRE_MOD_MANAGER.RELAUNCH_GAME", LeftOut = "SPIRE_MOD_MANAGER.RESET_POSITION",
            Broken = "SPIRE_MOD_MANAGER.MOVE_BUTTON", Empty = "SPIRE_MOD_MANAGER.MOVE_HINT";
        const string NotOurs = Translated + "X"; // a key mistyped
        const string Renamed = "SPIRE_MOD_MANAGER.PAGE_DISABLED", Added = "SPIRE_MOD_MANAGER.OPEN_TRANSLATIONS";
        const string Counted = "SPIRE_MOD_MANAGER.PAGE_ALL", NotText = "SPIRE_MOD_MANAGER.NEEDS_UPDATE";
        // A plural (the game's formatter takes one only of a number; ours pass text) and a misspelled formatter: both parse,
        // and both fail on screen (shown raw, an error logged and reported on every draw).
        const string Plural = "SPIRE_MOD_MANAGER.MISSING_WARNING", Misspelled = "SPIRE_MOD_MANAGER.SUBSCRIBE_ALL";
        const string OnButton = "SPIRE_MOD_MANAGER.MAIN_MENU_PROFILE", OnButtonOne = "SPIRE_MOD_MANAGER.MAIN_MENU_PROFILE_ONE";
        // Listed twice: a good copy, then a blank one; and a blank one, then a good one (tools/loc.cs reads them alike).
        const string TwiceKept = "SPIRE_MOD_MANAGER.NOTES_TERM", TwiceLater = "SPIRE_MOD_MANAGER.NOTES_ADD";
        const string German = "Spiel neu starten (Änderungen, 模组)"; // not ASCII: read as UTF-8, with a byte order mark
        string[] keys = [Translated, LeftOut, Broken, Empty];
        static string Read(string key)
        {
            try
            {
                return Menus.Text(key);
            }
            catch (Exception e)
            {
                return e.Message;
            }
        }
        var english = keys.ToDictionary(k => k, Read);
        string Shown() => string.Join(" | ", keys.Select(Read));
        bool AllEnglish() => keys.All(k => Read(k) == english[k]);
        var file = Path.Combine(Translations.Folder, "deu.json");
        // The file written, then the game switched to German. The exception's message if the switch threw.
        string? Switch(string text)
        {
            File.WriteAllText(file, text, new UTF8Encoding(true));
            try
            {
                LocManager.Instance.SetLanguage("deu");
                return null;
            }
            catch (Exception e)
            {
                return e.Message;
            }
        }
        try
        {
            Directory.CreateDirectory(Translations.Folder);
            var threw = Switch($$"""
                {
                  // a comment and a trailing comma, as a hand-edited file may have
                  "{{Translated}}": "{{German}}",
                  "{{Counted}}": "Installierte Mods ({count})",
                  "{{OnButton}}": "Mod-Profil: {name} ({count} Mods)",
                  "{{OnButtonOne}}": "Mod-Profil: {name} (1 Mod)",
                  "{{Broken}}": "Knopf {verschieben",
                  "{{Empty}}": " ",
                  "{{Renamed}}": "Deaktivierte Mods ({anzahl})",
                  "{{Added}}": "Ordner {ordner}",
                  "{{Plural}}": "Dieses Profil hat {count} aktivierte {count:plural:Mod|Mods}, die nicht installiert sind.",
                  "{{Misspelled}}": "Alle abonnieren ({count:plurl:eins|mehr})",
                  "{{NotText}}": 5,
                  "{{NotOurs}}": "Nicht unser Text",
                  "{{TwiceKept}}": "Notizen",
                  "{{TwiceKept}}": " ",
                  "{{TwiceLater}}": "",
                  "{{TwiceLater}}": "Notizen hinzufügen",
                }
                """);
            Check("a drop-in translation: its text shows in its language", threw == null && Read(Translated) == German, threw ?? Shown());
            var counted = Menus.Format(Counted, ("count", Menus.Number(1234)));
            Check("a drop-in translation: its {count} filled in, in German numbers", counted == "Installierte Mods (1.234)", counted);
            Check("a drop-in translation: a key it leaves out, an empty text and a broken placeholder show in English",
                Read(LeftOut) == english[LeftOut] && Read(Empty) == english[Empty] && Read(Broken) == english[Broken], Shown());
            var table = LocManager.Instance.GetTable("settings_ui");
            Check("a drop-in translation: a renamed placeholder, an added one and a number show in English",
                !table.IsLocalKey(Renamed) && !table.IsLocalKey(Added) && !table.IsLocalKey(NotText),
                $"{table.GetRawText(Renamed)} | {table.GetRawText(Added)} | {table.GetRawText(NotText)}");
            var plural = Menus.Format(Plural, ("count", Menus.Number(5)));
            Check("a drop-in translation: a plural form and a misspelled formatter show in English (the game can't format them)",
                !table.IsLocalKey(Plural) && !table.IsLocalKey(Misspelled) && plural == EnglishText(Plural).Replace("{count}", "5"),
                $"{table.GetRawText(Plural)} | {table.GetRawText(Misspelled)} | {plural}");
            Check("a drop-in translation: a key English doesn't have is left out", !table.HasEntry(NotOurs));
            Check("a drop-in translation: a key listed twice shows its last copy that isn't blank",
                Read(TwiceKept) == "Notizen" && Read(TwiceLater) == "Notizen hinzufügen", $"{Read(TwiceKept)} | {Read(TwiceLater)}");
            var popup = menu.GetNodeOrNull<Control>("SpireModManagerMenuButton") is { } button ? await ButtonMenu(menu, button) : null;
            var labels = popup == null ? [] : MenuPanel.Labels(popup);
            MenuPanel.Close();
            Check("a drop-in translation on screen: the main menu button's menu has it", labels.Contains(German),
                string.Join(", ", labels));
            // The button itself follows the language as it changes, its count in German numbers.
            var on = Profiles.OnCount();
            var german = on == 1 ? $"Mod-Profil: {Profiles.Active?.Name} (1 Mod)" : $"Mod-Profil: {Profiles.Active?.Name} ({Menus.Number(on)} Mods)";
            var germanSize = menu.GetNodeOrNull<Label>("SpireModManagerMenuButton/Label")?.GetThemeFontSize("font_size");
            Check("a drop-in translation on screen: the main menu button reads it, as wide as its words, still at its smaller size",
                MenuButtonText(menu) == german && MenuButtonMisfit(menu) == null && germanSize == MenuButton.TextSize,
                $"{MenuButtonText(menu)} vs {german}; {MenuButtonMisfit(menu)}; size {germanSize}");
            LocManager.Instance.SetLanguage(start);
            await Wait(menu, 0.1);
            Check("a drop-in translation: back in English, English", AllEnglish() && MenuButtonText(menu) == MenuButtonExpected()
                && MenuButtonMisfit(menu) == null, $"{Shown()} | {MenuButtonText(menu)}; {MenuButtonMisfit(menu)}");

            foreach (var (what, text) in new[]
            {
                ("not JSON", $"{{ \"{Translated}\": \"Spiel neu starten\""),
                ("not an object", "[\"Spiel neu starten\"]"),
                ("over 1 MB", $"{{ \"{Translated}\": \"Spiel neu starten\", \"{NotOurs}\": \"{new string('x', 1_100_000)}\" }}"),
            })
            {
                threw = Switch(text);
                Check($"a drop-in file {what}: changes nothing, throws nothing", threw == null && LocManager.Instance.Language == "deu" && AllEnglish(),
                    threw ?? Shown());
            }

            File.Delete(file);
            File.WriteAllText(file + ".txt", $"{{ \"{Translated}\": \"Spiel neu starten\" }}");
            LocManager.Instance.SetLanguage("deu");
            Check("a drop-in file saved as deu.json.txt: read all the same", Read(Translated) == "Spiel neu starten", Shown());
        }
        finally
        {
            File.Delete(file);
            File.Delete(file + ".txt");
            LocManager.Instance.SetLanguage(start);
        }
    }

    // Mod Doctor's file (no Mod Doctor here: written by hand, as it would for this process), read where it writes now,
    // mod_data/ModDoctor/startup.json, and at the user folder's top where it wrote before its move, the new place first.
    // In a folder of the test's own, as the game's user folder (the real game's too).
    static async Task TestModDoctorFile(Node menu, string outDir)
    {
        var top = Path.Combine(outDir, "mod-doctor-file");
        string[] files = [Path.Combine(top, "mod_data", ModDoctor.ModId, "startup.json"), Path.Combine(top, "mod_doctor_startup.json")];
        ModDoctor.TestFolder = top;
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        void Write(string file, string suspect)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file) ?? top);
            File.WriteAllText(file, new System.Text.Json.Nodes.JsonObject
            {
                ["version"] = 1, ["processId"] = System.Environment.ProcessId, ["processStartUtc"] = self.StartTime.ToUniversalTime().ToString("o"),
                ["fatal"] = new System.Text.Json.Nodes.JsonObject { ["kind"] = "test", ["message"] = "made up", ["step"] = "test", ["suspects"] = new System.Text.Json.Nodes.JsonArray(suspect) },
                ["failedMods"] = new System.Text.Json.Nodes.JsonArray(),
            }.ToJsonString());
        }
        var seen = new List<string>();
        try
        {
            Write(files[0], "SmmNewPlace");
            seen.Add($"new {string.Join(",", await ModDoctor.Suspects(menu))}");
            File.Delete(files[0]);
            Write(files[1], "SmmOldPlace");
            seen.Add($"old {string.Join(",", await ModDoctor.Suspects(menu))}");
            Write(files[0], "SmmNewPlace");
            seen.Add($"both {string.Join(",", await ModDoctor.Suspects(menu))}");
        }
        finally
        {
            foreach (var file in files)
                File.Delete(file);
            ModDoctor.TestFolder = null;
        }
        Check("Mod Doctor's file: read where it writes now, else where it wrote before its move; the new place first",
            seen.SequenceEqual(["new SmmNewPlace", "old SmmOldPlace", "both SmmNewPlace"]), string.Join(" | ", seen));
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
        // Better Mod Menu lets two profiles share a name; ours go by name, so the later one is numbered as a copy is, and
        // the current index still finds it.
        var twins = Summary(Import("""
            {
              "Profiles": [
                { "Name": "Profile 3", "DisabledMods": ["ModA"] },
                { "Name": "Profile 3", "DisabledMods": ["ModB"] },
                { "Name": "Modded (1)", "DisabledMods": [] },
                { "Name": "Modded (1)", "DisabledMods": [] }
              ],
              "CurrentProfileIndex": 1,
              "CustomGroups": ["Tools", "Tools"]
            }
            """));
        Check("Better Mod Menu import: two profiles of one name stay two (the later numbered as a copy is), the current one still current; a group named twice once",
            twins == "profiles Profile 3 (off: ModA); Profile 3 (1) (off: ModB); Modded (1) (off: none); Modded (2) (off: none) / active Profile 3 (1)"
            + " / categories Tools / mods none", twins);
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
        // What each field is for: our hover card on its term (shown inside the open menu, above it), the term only as
        // wide as its word, and no native tooltip.
        var terms = new[] { "Alias", "Tags", "Notes" }.Select(f => rowMenu?.FindChild("Fields", true, false)?.GetNodeOrNull<Label>($"{f}/Term/Label")).ToList();
        string[] termTips = [Menus.Text("SPIRE_MOD_MANAGER.ALIAS_HINT"), Menus.Text("SPIRE_MOD_MANAGER.TAGS_HINT"), Menus.Text("SPIRE_MOD_MANAGER.NOTES_HINT")];
        var termCards = new List<string>();
        foreach (var term in terms.OfType<Label>())
            termCards.Add(await HoverCard(menu, term, ModField.TipName));
        var termLayer = menu.GetTree().Root.FindChild(ModField.TipName, true, false); // gone again after each hover
        Check("each field's term explains it in our hover card, over the menu, the term no wider than its word",
            termCards.SequenceEqual(termTips) && termLayer == null
            && terms.All(t => t is { TooltipText: "" } && t.Size.X <= t.GetMinimumSize().X + 1),
            string.Join(" | ", termCards) + string.Join(" ", terms.Select(t => t == null ? " none" : $" ({t.Size.X:0}/{t.GetMinimumSize().X:0})")));
        await Capture(screen, outDir, "5-row-menu");
        if (rowMenu != null && aliasBox != null)
        {
            // Typing in the menu and clicking away (here: closing it) saves.
            aliasBox.GrabFocus();
            await Wait(menu, 0.1);
            Check("editing shows Save and Cancel and the inline clear, with the same \"Add alias\" while it's empty", aliasBox.ClearButtonEnabled
                && rowMenu.FindChild("Fields", true, false)?.GetNodeOrNull<Control>("Alias/Term/Buttons") is { Visible: true }
                && aliasBox.PlaceholderText == "Add alias", aliasBox.PlaceholderText);
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

        // Where a menu opens from the mouse (MenuMisplaced: a corner exactly on it, whole on screen, so not over it):
        // mid-screen, below right of it; near the bottom, above it instead of pushed up over it; near the right edge, left
        // of it; in the bottom-right corner, both. Placed again as it grows: turned above, it grows upward, its bottom
        // still at the mouse; grown past the bottom as it opens, it turns above; grown past it once open (slides), it only
        // slides up by what's past, its top-left corner no longer at the mouse but not turned (its bottom at the screen's).
        var view = content.GetViewportRect().Size;
        async Task<string?> MenuAt(Vector2 mouse, bool up, bool left, float grow = 0, bool? upFirst = null, bool slides = false)
        {
            var point = new Rect2(mouse, Vector2.Zero);
            Hover.TestMouse = () => mouse;
            var probe = MenuPanel.Show(content, () => [new MenuItem("One", () => { }), new MenuItem("Two", () => { }), new MenuItem("Three", () => { })]);
            Hover.TestMouse = null;
            void Grow()
            {
                if (probe?.FindChild("Items", true, false) is Control list)
                    list.AddChild(new Control { CustomMinimumSize = new Vector2(0, grow), MouseFilter = Control.MouseFilterEnum.Ignore });
            }
            if (grow > 0 && upFirst == null && !slides)
                Grow(); // as it opens
            await WaitReal(menu, 0.1); // past MenuPanel's Settle (its second frame): it turns only while it opens
            var first = MenuMisplaced(probe, point, upFirst ?? up, left);
            if (grow > 0 && (upFirst != null || slides))
            {
                Grow(); // once open
                await Wait(menu, 0.1);
            }
            string? Slid() => probe?.GetNodeOrNull<Control>("Catcher/Panel")?.GetGlobalRect() is { } box && box.End.Y - view.Y is > -0.5f and < 0.5f
                && box.Position.X - mouse.X is > -0.5f and < 0.5f && box.Position.Y >= 0 ? null : $"menu {probe?.GetNodeOrNull<Control>("Catcher/Panel")?.GetGlobalRect()} from {mouse}, on {view}";
            var misplaced = first != null ? $"at first: {first}" : slides ? Slid() : MenuMisplaced(probe, point, up, left);
            MenuPanel.Close();
            return misplaced;
        }
        var middle = await MenuAt(view / 2, up: false, left: false);
        Check("menus: mid-screen, a menu opens below right of the mouse", middle == null, middle ?? "");
        var bottom = await MenuAt(new Vector2(300, view.Y - 60), up: true, left: false);
        Check("menus: near the bottom, a menu opens above the mouse, not pushed up over it", bottom == null, bottom ?? "");
        var right = await MenuAt(new Vector2(view.X - 40, 300), up: false, left: true);
        Check("menus: near the right edge, a menu opens left of the mouse", right == null, right ?? "");
        var corner = await MenuAt(view - new Vector2(40, 60), up: true, left: true);
        Check("menus: in the bottom-right corner, a menu opens above the mouse and left of it", corner == null, corner ?? "");
        var grownUp = await MenuAt(new Vector2(300, view.Y - 60), up: true, left: false, grow: 200, upFirst: true);
        var grownPast = await MenuAt(new Vector2(300, view.Y - 300), up: true, left: false, grow: 300);
        var grownOpen = await MenuAt(new Vector2(300, view.Y - 300), up: false, left: false, grow: 300, slides: true);
        Check("menus: placed again as they grow (turned above, upward from the mouse; grown past the bottom as it opens, turned "
            + "above; grown past it once open, only slid up, not turned)", grownUp == null && grownPast == null && grownOpen == null,
            $"turned: {grownUp ?? "fine"}; grown past: {grownPast ?? "fine"}; once open: {grownOpen ?? "fine"}");


        var category = Categories.Add();
        Categories.Assign(modId, category.Name);
        CategoryList.Apply(content);
        var box = CategoryList.BeginRename(content, category);
        Check("rename box opens", box != null);
        // The box covers the name, and the mod count after it would show through it (#186).
        var countWhileRenaming = box?.GetParent()?.GetNodeOrNull<Control>("Count")?.Visible;
        Check("while its rename box is open, the header's mod count is hidden", countWhileRenaming == false, $"{countWhileRenaming}");
        if (box != null)
        {
            box.Text = "Renamed";
            box.EmitSignal(LineEdit.SignalName.TextSubmitted, box.Text);
        }
        await Wait(menu, 0.5);
        Check("rename keeps the mod in the category", Store.InfoFor(modId)?.Category == "Renamed");
        Check("renamed header shown, its count with it", Header(content, "Renamed") is Node renamed && renamed.GetNodeOrNull<Control>("Count") is { Visible: true });

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
            Check("clicking away keeps the old name, the count shown again", category.Name == "Renamed"
                && Header(content, "Renamed") is Node kept && kept.GetNodeOrNull<Control>("Count") is { Visible: true });
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
        var wereOn = inCategory.Where(Copies.Ticked).ToList();
        if (CategoryList.ShowHeaderMenu(content, category, Members()) is { } allMenu)
        {
            MenuPanel.Press(allMenu, "Disable all");
            await Wait(menu, 0.3);
            Check("Disable all turns the category's mods off", inCategory.All(m => !Copies.Ticked(m) || m.manifest?.id == ModEntry.ModId),
                string.Join(", ", inCategory.Select(m => $"{m.manifest?.id} {Copies.Ticked(m)}")));
            History.Undo();
            await Wait(menu, 0.3);
            Check("one undo turns them back on", wereOn.All(Copies.Ticked));
        }
        category = Store.Data.Categories.First(c => c.Name == "Renamed"); // undo restores the data as new objects

        // Delete from the header menu asks first; with one mod in the category, in the singular. (Cancelled by
        // closing the popup; the deletion itself is below.)
        if (CategoryList.ShowHeaderMenu(content, category, Members()) is { } deleteMenu)
        {
            MenuPanel.Press(deleteMenu, "Delete");
            await Wait(menu, 0.5);
            var asked = PopupTexts();
            Check("deleting a category with one mod asks, in the singular", asked.Any(t => t.Contains("contains 1 mod.")), string.Join(" | ", asked.Where(t => t.Length > 0)));
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
        var toDelete = Store.Data.Profiles.FirstOrDefault(p => p.Name == "Brother co-op") ?? Store.Data.Profiles[0];
        var body = ProfileBar.DeleteBody(toDelete);
        var asked = body.GetFormattedText();
        Check("Delete's question names the profile and its mods, whole", asked.StartsWith("Delete profile") && asked.Contains($"{toDelete.Name} ({Profiles.ModCount(toDelete)})")
            && asked.Contains("All mods remain installed") && !asked.Contains('{'), asked);
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
        // One undo step: an undo brings it back, active (a step for the switch alone would leave the neighbour active); a
        // redo deletes it again.
        History.Undo();
        await Wait(menu, 0.3);
        var undone = Profiles.Active?.Name == "Brother co-op" && Store.Data.Profiles.Count == count + 1;
        History.Redo();
        await Wait(menu, 0.3);
        Check("deleting the active profile is one undo step: an undo brings it back, active; a redo deletes it again", undone
            && Store.Data.Profiles.Count == count && Store.Data.Profiles.All(p => p.Name != "Brother co-op"), $"undone {undone}; active {Profiles.Active?.Name}");
        if (start != null && Profiles.Active != start)
            Profiles.SwitchTo(start, screen);
        Check("mod list unchanged after new/duplicate/rename/delete", before.SequenceEqual(ModListState()));
        Check("last profile can't be deleted", Store.Data.Profiles.Count > 1 || !Profiles.Delete(Store.Data.Profiles[0], screen));

        // The profile list: opens below the picker; a second press on the picker closes it.
        await RealClick(menu, picker);
        var list = MenuPanel.Current;
        Check("the picker opens the profile list, with details, then Share profile and Import profile", list != null
            && MenuPanel.Labels(list).SequenceEqual(Store.Data.Profiles.Select(p => p.Name).Concat(["Import profile", "Export profile"])),
            list == null ? "no list" : string.Join(", ", MenuPanel.Labels(list)));
        var dropped = MenuMisplaced(list, picker.GetGlobalRect().GrowIndividual(0, 4, 0, 4), up: false, left: false);
        Check("the profile list drops right under the picker (4 below it), left edges together", dropped == null, dropped ?? "");
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
        // Test Content, Test UI and the fixture's picked twin, Test Twin Beta (the other copy, not picked, is off).
        Check("a library says how many mods need it", Note("SmmTestLib").Contains("Required for 3 enabled mods"), Note("SmmTestLib"));
        tickbox.IsTicked = false;
        tickbox.EmitSignal(NTickbox.SignalName.Toggled, tickbox);
        await Wait(menu, 0.3);
        Check("turning the library off marks it at once", Note("SmmTestUi").Contains("(disabled)"), Note("SmmTestUi"));
        tickbox.IsTicked = true;
        tickbox.EmitSignal(NTickbox.SignalName.Toggled, tickbox);
        await Wait(menu, 0.3);
        Check("turning it back on clears \"(disabled)\" at once", !Note("SmmTestUi").Contains("(disabled)"), Note("SmmTestUi"));
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
            if (menu.GetNodeOrNull<Control>("SpireModManagerMenuButton") is { } menuButton)
            {
                string Note() => menuButton.GetNodeOrNull<Label>("PendingNote")?.Text ?? "";
                int Count()
                {
                    var (on, off, moved) = HistoryBar.Counts();
                    return on + off + moved + HistoryBar.CopySwitchCount();
                }
                static string Said(int count) => count == 1 ? "1 mod change will be applied after a restart" : $"{Menus.Number(count)} mod changes will be applied after a restart";
                var (count, noteText, card) = (Count(), Note(), Hover.TipOf(menuButton));
                Check("main menu button, a relaunch pending: its note counts the mods turned on, off and moved (\"1 mod change\", \"2 mod changes\"); its card starts with the changes, a faint line, then its own words",
                    count >= 1 && noteText == Said(count)
                    && card.StartsWith($"[b]{Menus.Text("SPIRE_MOD_MANAGER.CHANGES_TITLE")}[/b]") && card.Contains($"\n{MenuButton.Divider}\n")
                    && card.EndsWith(MenuButton.Tip(Profiles.Active?.Name ?? "")) && !card.Contains(Menus.Text("SPIRE_MOD_MANAGER.CHANGES_CLICK")),
                    $"\"{noteText}\" ({count}); card \"{card}\"");
                // One change more or less (Test Fixes, which nothing needs, flipped and back): the note follows, in its own form.
                if (content.GetChildren().OfType<NModMenuRow>().FirstOrDefault(r => r.Mod?.manifest?.id == "SmmTestFixes")?.GetNode<NTickbox>("Tickbox") is { } fixes)
                {
                    fixes.IsTicked = !fixes.IsTicked;
                    fixes.EmitSignal(NTickbox.SignalName.Toggled, fixes);
                    await Wait(menu, 0.3);
                    var (more, moreText) = (Count(), Note());
                    fixes.IsTicked = !fixes.IsTicked;
                    fixes.EmitSignal(NTickbox.SignalName.Toggled, fixes);
                    await Wait(menu, 0.3);
                    Check("main menu button: one change more or less, its note says the new count", Math.Abs(more - count) == 1 && moreText == Said(more),
                        $"\"{moreText}\" ({more}, was {count})");
                }
            }
            else
                Results.Add("FAIL the main menu button's pending note: no button");
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
            ours == "Mod changes will be applied after a restart" && game != ours && onList == ours && (shown == null || shown == ours)
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
        bool On() => Copies.Ticked(mod);
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
        var (unsavedOn, unsavedOff, _) = Profiles.UnsavedChanges();
        Check("Unsaved says how many changes: Unsaved (1) • for one tickbox", unsavedOn.Count + unsavedOff.Count == 1
            && bar.FindChild("Unsaved", true, false) is Label { Text: "Unsaved (1) •" }, (bar.FindChild("Unsaved", true, false) as Label)?.Text ?? "none");
        await Capture(screen, outDir, "3b-unsaved-profile");

        // "Unsaved •": its card opens at once, with no delay (checked outside any grace period, which would open a
        // card at once anyway). A click lists the changes in the info panel, names as links, and a second click
        // closes it. One list at a time: the Changes list takes the panel from it, and it takes the panel back.
        // While open it follows the tickboxes: back on the saved state, it says nothing is unsaved.
        var unsaved = (Control)bar.FindChild("Unsaved", true, false);
        var info = screen.GetNode<Control>("%ModInfoContainer");
        var line = $"{(wasOn ? "-" : "+")} {mod.manifest?.name}";
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
    // A made-up Steam for the subscription checks (the sandbox runs without Steam): each item's answer, set before, and
    // its download's progress, set as the test goes.
    sealed class FakeSteam : Subscriptions.ITestSteam
    {
        public readonly Dictionary<ulong, Steamworks.EResult> Answers = [];
        public readonly Dictionary<ulong, float> Downloaded = [];
        public readonly List<ulong> Asked = [];
        public Task<Steamworks.EResult> Subscribe(ulong item)
        {
            Asked.Add(item);
            return Task.FromResult(Answers.GetValueOrDefault(item, Steamworks.EResult.k_EResultOK));
        }
        public float? Progress(ulong item) => Downloaded.TryGetValue(item, out var done) ? done : null;
    }

    // Subscribing to a profile's missing mods, with Steam made up: an imported list naming Workshop mods that aren't
    // installed offers Subscribe in the preview, which then follows each download or says why it failed; the new profile
    // keeps each item, and its banner (Jordan's design) counts the missing mods, lists them with Show (progress, a failure
    // and Subscribe again, a mod not on the Workshop), turns them off with Disable missing mods (one undo step), and
    // Dismiss hides it until the profile is switched to again or its edit discarded. The mod arriving (the game finding
    // it) takes it off the banner. Save as new keeps the missing mods. Everything made up goes after.
    static async Task TestSubscribeMissing(Node menu, NModdingScreen screen, Control content, string outDir)
    {
        if (Profiles.Active is not { } start || Store.Data.Profiles.FirstOrDefault(p => p != start) == null)
        {
            Results.Add("SKIP subscribing to missing mods: needs two profiles");
            return;
        }
        if (Profiles.Edited)
            Profiles.Save();
        const ulong Here = 3700000001, Gone = 3700000002;
        const string HereId = "SmmSubHere", GoneId = "SmmSubGone", LocalId = "SmmSubLocal";
        var (startName, profiles) = (start.Name, Store.Data.Profiles.Count);
        var steam = new FakeSteam();
        steam.Answers[Gone] = Steamworks.EResult.k_EResultFileNotFound;
        Subscriptions.TestSteam = steam;
        Node? Dialog(string name) => menu.GetTree().Root.GetNodeOrNull(name);
        T? Part<T>(string dialog, string name) where T : class => Dialog(dialog)?.FindChild(name, true, false) as T;
        void Press(string dialog, string name) => Part<BaseButton>(dialog, name)?.EmitSignal(BaseButton.SignalName.Pressed);
        var border = screen.GetNode<Control>("ModsBorder");
        Control? Banner() => border.GetNodeOrNull<Control>(LoadOrder.MissingName);
        string BannerText() => Banner()?.FindChild("MissingWarning", true, false) is Label { } label ? label.Text : "";
        void BannerPress(string name) => (Banner()?.FindChild(name, true, false) as BaseButton)?.EmitSignal(BaseButton.SignalName.Pressed);
        string RowText(string id) => Dialog(MissingMods.Name)?.FindChild("Rows", true, false)?.GetNodeOrNull(id) is Node row
            ? string.Join(" ", row.FindChildren("*", "", true, false).Select(n => n switch
            {
                RichTextLabel r => r.GetParsedText(),
                Label l => l.Text,
                Button b => $"[{b.Text}]",
                ProgressBar p => $"bar {p.Value:0.00}",
                _ => "",
            }).Where(s => s.Length > 0))
            : "none";
        var arrived = new Mod
        {
            path = "", modSource = ModSource.SteamWorkshop, state = ModLoadState.AddedAtRuntime,
            manifest = new ModManifest { id = HereId, name = "Sub Here", version = "1.0.0" },
        };
        Compat.SetWorkshopId(arrived, Here);
        try
        {
            // The import: three mods not installed, two on the Workshop (one Steam will refuse), one local.
            var csv = ProfileCsv.Write([
                new ProfileCsv.Row(HereId, "Sub Here", "1.0", true, "", Here.ToString()),
                new ProfileCsv.Row(GoneId, "Sub Gone", "1.0", true, "", Gone.ToString()),
                new ProfileCsv.Row(LocalId, "Sub Local", "1.0", true, "", ""),
            ], new ProfileCsv.Options(false, false, false, false));
            ProfileShare.ShowImport(screen);
            await Wait(menu, 0.2);
            if (Part<TextEdit>(ProfileShare.Name, "CsvText") is { } box)
            {
                box.Text = csv;
                box.EmitSignal(TextEdit.SignalName.TextChanged);
            }
            await Wait(menu, 0.2);
            var offer = Part<Button>(ProfileShare.Name, "SubscribeMissingButton");
            Check("import preview: Subscribe offers the missing mods on the Workshop (not the local one), with Steam",
                offer is { Visible: true, Text: "Subscribe to 2 missing mods" }, offer == null ? "no button" : $"{offer.Visible} \"{offer.Text}\"");
            await Capture(screen, outDir, "7f-import-subscribe");
            Press(ProfileShare.Name, "SubscribeMissingButton");
            steam.Downloaded[Here] = 0.45f;
            await WaitReal(menu, 0.6); // Subscriptions.Watch reads each download's progress a quarter second apart
            var preview = Part<RichTextLabel>(ProfileShare.Name, "Preview")?.GetParsedText() ?? "";
            Check("import preview: Subscribe asks Steam for both; one downloads (its progress), the other fails and says why; Subscribe offers that one again",
                steam.Asked.Order().SequenceEqual([Here, Gone]) && preview.Contains("Downloading 45%")
                && preview.Contains("Couldn't find mod. The listing could be removed, or set to private.") && !preview.Contains("Couldn't subscribe") && offer is { Visible: true, Text: "Subscribe to the missing mod" },
                $"{string.Join(",", steam.Asked)}; button {offer?.Visible} \"{offer?.Text}\"; {preview.Replace("\n", " | ")}");
            await Capture(screen, outDir, "7g-import-downloading");
            Press(ProfileShare.Name, "CreateProfileButton");
            await Wait(menu, 0.4);
            var importedName = Profiles.Active?.Name ?? "";
            var imported = Profiles.Active;
            Check("the imported profile keeps each missing Workshop mod's item and the list's name for it, and the banner counts the three",
                importedName != startName && imported?.Mods.Any(m => m.Id == HereId && m.Copy == $"w:{Here}" && m.Name == "Sub Here" && m.Enabled) == true
                && imported.Mods.Any(m => m.Id == LocalId && m.Copy == null && m.Enabled) && Banner() is { Visible: true }
                && BannerText() == "This profile contains 3 mods that aren't installed.", $"{importedName}: {BannerText()}");
            await Capture(screen, outDir, "2l-missing-banner");

            // Show: the list, as it was when it opened.
            BannerPress("ShowMissingButton");
            await Wait(menu, 0.4);
            Check("Show lists the missing mods by the list's names: one downloading with its bar, one that failed with Subscribe again, the local one not on the Workshop",
                RowText(HereId).StartsWith("Sub Here") && RowText(LocalId).StartsWith("Sub Local") && RowText(HereId).Contains("bar 0.45") && RowText(HereId).Contains("Downloading 45%")
                && RowText(GoneId).Contains("Couldn't find mod") && RowText(GoneId).Contains("[Subscribe]") && RowText(LocalId).Contains("Not on Workshop")
                && Part<Button>(MissingMods.Name, "SubscribeAllButton") is { Text: "Subscribe to all (1)" },
                $"{RowText(HereId)} | {RowText(GoneId)} | {RowText(LocalId)} | {Part<Button>(MissingMods.Name, "SubscribeAllButton")?.Text}");
            await Capture(screen, outDir, "2m-missing-list");

            // The download lands: the game finds the mod, the list says so and the banner counts two.
            GameInstalls(arrived);
            await Wait(menu, 0.6);
            NModalContainer.Instance?.Clear();
            CategoryList.Redraw(content);
            await Wait(menu, 0.3);
            Check("the mod arriving: the list says it's installed, and the banner counts the two left",
                RowText(HereId).Contains("Installed: loads after a relaunch") && BannerText() == "This profile contains 2 mods that aren't installed.",
                $"{RowText(HereId)} | {BannerText()}");
            Modal.Close(MissingMods.Name);
            await Wait(menu, 0.2);

            // A search for #missing (or #uninstalled): no rows, those mods have none; a line at the list's top says how many
            // the profile has, its link opening their list. Not for -#missing, nor without a search.
            var searchBox = border.GetNode<LineEdit>("SpireModManagerSearch");
            async Task Type(string text)
            {
                searchBox.Text = text;
                searchBox.EmitSignal(LineEdit.SignalName.TextChanged, text);
                await Wait(menu, 0.3);
            }
            RichTextLabel? Hint() => content.GetNodeOrNull<RichTextLabel>($"{CategoryList.MissingHintName}/Line");
            await Type("#missing");
            var (hint, rowsShown) = (Hint()?.GetParsedText(), content.GetChildren().OfType<NModMenuRow>().Count(r => r.Visible));
            await Capture(screen, outDir, "2n-missing-search");
            Hint()?.EmitSignal(RichTextLabel.SignalName.MetaClicked, "missing");
            await Wait(menu, 0.4);
            var opened = Modal.IsOpen(MissingMods.Name);
            Input.ParseInputEvent(new InputEventKey { Keycode = Key.Escape, Pressed = true });
            await Wait(menu, 0.2);
            Input.ParseInputEvent(new InputEventKey { Keycode = Key.Escape, Pressed = false });
            Check("Esc closes the missing mods list (no dimmer left over the main menu); the page stays open",
                opened && !Modal.IsOpen(MissingMods.Name) && screen.IsVisibleInTree(), $"opened {opened}, still open {Modal.IsOpen(MissingMods.Name)}, page {screen.IsVisibleInTree()}");
            Modal.Close(MissingMods.Name);
            await Wait(menu, 0.2);
            await Type("#Uninstalled OR #enabled");
            var withOr = Hint() != null;
            await Type("#enabled -#missing");
            var leftOut = Hint() == null;
            await Type("");
            Check("a search for #missing: no rows, a line saying the profile's 2 mods aren't installed, its link opening their list; "
                + "#Uninstalled in an OR too; not -#missing, nor no search",
                hint == ModDetails.WithoutTags(Draft(MissingSearchHint, ("count", "2"))) && rowsShown == 0 && opened && withOr && leftOut && Hint() == null,
                $"\"{hint}\", {rowsShown} rows, list opened {opened}, in an OR {withOr}, -#missing none {leftOut}, no search none {Hint() == null}");

            // Dismiss: gone; back after switching away and back; gone again, then back after Discard.
            BannerPress("DismissMissingButton");
            await Wait(menu, 0.3);
            var dismissed = Banner() == null;
            Profiles.SwitchTo(Store.Data.Profiles.First(p => p.Name == startName), screen);
            await Wait(menu, 0.3);
            var awayClear = Banner() == null;
            Profiles.SwitchTo(Store.Data.Profiles.First(p => p.Name == importedName), screen);
            await Wait(menu, 0.3);
            var backShown = Banner() != null;
            Profiles.DismissMissing();
            Profiles.Discard(screen);
            await Wait(menu, 0.3);
            Check("Dismiss hides the banner; switching to the profile again shows it, and so does Discard",
                dismissed && awayClear && backShown && Banner() != null, $"dismissed {dismissed}, away {awayClear}, back {backShown}, after Discard {Banner() != null}");

            // Save as new keeps the missing mods.
            var copy = Profiles.Create(Profiles.NewKind.SaveAsNew, "SmmSubCopy", screen);
            await Wait(menu, 0.3);
            Check("Save as new keeps the missing mods, as Save does",
                copy.Mods.Any(m => m.Id == GoneId && m.Enabled && m.Copy == $"w:{Gone}") && copy.Mods.Any(m => m.Id == LocalId && m.Enabled),
                string.Join(", ", copy.Mods.Where(m => m.Id.StartsWith("SmmSub")).Select(m => $"{m.Id}={m.Enabled}/{m.Copy}")));

            // Disable missing mods: out of the profile, the banner gone, one undo step back.
            var steps = History.Steps(History.Track.List);
            BannerPress("DisableMissingButton");
            await Wait(menu, 0.3);
            var off = Profiles.Active?.Mods.Any(m => m.Id is GoneId or LocalId) == false && Profiles.Active?.Edited?.Any(m => m.Id is GoneId or LocalId) != true && Banner() == null
                && History.Steps(History.Track.List) == steps + 1;
            History.Undo();
            await Wait(menu, 0.3);
            Check("Disable missing mods takes them out of the profile (the banner goes), one undo step that brings them back",
                off && Profiles.Missing().Count == 2 && Banner() != null, $"off {off}, missing after undo {Profiles.Missing().Count}");

            // The #missing line for one mod not installed (Sub Gone taken out of the active profile, the copy saved as new,
            // here with no undo step), then for none (Sub Local too): no line, and still no rows.
            var activeName = Profiles.Active?.Name;
            void TakeOut(string id) => History.Quietly(data =>
            {
                foreach (var p in data.Profiles.Where(p => p.Name == activeName))
                {
                    p.Mods.RemoveAll(m => m.Id == id);
                    p.Edited?.RemoveAll(m => m.Id == id);
                }
            });
            TakeOut(GoneId);
            await Type("#missing");
            var (oneHint, oneMissing) = (Hint()?.GetParsedText(), Profiles.Missing().Count);
            TakeOut(LocalId);
            await Type("#uninstalled");
            var (noneHint, noneMissing, noneRows) = (Hint(), Profiles.Missing().Count, content.GetChildren().OfType<NModMenuRow>().Count(r => r.Visible));
            await Type("");
            Check("the #missing line for one mod not installed says so; with none, a search for them shows no line (and no rows)",
                oneMissing == 1 && oneHint == ModDetails.WithoutTags(MissingSearchHintOne) && noneMissing == 0 && noneHint == null && noneRows == 0,
                $"{oneMissing} missing: \"{oneHint}\"; {noneMissing} missing: line {noneHint != null}, {noneRows} rows");
        }
        finally
        {
            Modal.Close(MissingMods.Name);
            ProfileShare.Close();
            GameForgets(arrived);
            // The row the game made for the arrival goes with it, as after every made-up arrival.
            foreach (var row in content.GetChildren().OfType<NModMenuRow>().Where(r => r.Mod == arrived).ToList())
            {
                content.RemoveChild(row);
                row.QueueFree();
            }
            Subscriptions.TestSteam = null;
            Subscriptions.TestReset();
            if (Store.Data.Profiles.FirstOrDefault(p => p.Name == startName) is { } back)
                Profiles.SwitchTo(back, screen);
            // The made-up profiles, and the arrival the start profile took in (off) while it wasn't active.
            History.Quietly(data =>
            {
                data.Profiles.RemoveAll(p => p.Name != startName && p.Mods.Any(m => m.Id.StartsWith("SmmSub")));
                foreach (var p in data.Profiles)
                {
                    p.Mods.RemoveAll(m => m.Id.StartsWith("SmmSub"));
                    p.Edited?.RemoveAll(m => m.Id.StartsWith("SmmSub"));
                }
            });
            // The game's list keeps a mod it found (the arrival), and switching back wrote the start profile's entry for
            // it there too: gone with it, as after every made-up arrival.
            SaveManager.Instance.SettingsSave.ModSettings?.ModList.RemoveAll(e => e.Id.StartsWith("SmmSub"));
            Profiles.Track();
            CategoryList.Redraw(content);
            await Wait(menu, 0.3);
        }
        Check("as it started: the start profile active, the made-up profiles gone, no missing-mods banner",
            Profiles.Active?.Name == startName && Store.Data.Profiles.Count == profiles && Banner() == null
            && !Store.Data.Profiles.Any(p => p.Mods.Any(m => m.Id.StartsWith("SmmSub"))),
            $"{Profiles.Active?.Name}, {Store.Data.Profiles.Count} profiles (were {profiles})");
    }

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
        string Status() => Part<Label>("Status") is { Visible: true } status && status.Modulate.A > 0 ? status.Text : "";
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
        bool IsOn(string id) => Dependencies.Find(id) is { } mod && Copies.Ticked(mod);

        // The format on its own: quoting, the formula guard, and a forgiving read.
        var tricky = ProfileCsv.Write([
            new ProfileCsv.Row("SmmQuote", "Comma, \"quoted\"\nname", "1.0", true, "A, B", ""),
            new ProfileCsv.Row("SmmFormula", "=HYPERLINK(\"x\")", "-1", false, "@group", ""),
        ], new(Disabled: true));
        var back = ProfileCsv.Read(tricky).Mods;
        var trickyRecords = ProfileCsv.Records(tricky);
        Check("CSV: a name with a comma, quotes and a line break round-trips, quoted; no line break after the last row",
            !tricky.EndsWith('\n') && !tricky.EndsWith('\r') && tricky.Contains("\"Comma, \"\"quoted\"\"\nname\"")
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
        // The Workshop column is just the id, read back as the item's link; the optional columns.
        var withId = ProfileCsv.Write([new ProfileCsv.Row("SmmWorkshop", "From the Workshop", "1.0", true, "", "3123456789")], new());
        var odd = ProfileCsv.Read("Mod Id,Enabled,Workshop Id\nSmmA,TRUE,not a link\nSmmB,TRUE,https://steamcommunity.com/sharedfiles/filedetails/?id=42&x=1\n").Mods;
        Check("CSV: a Workshop Id that's neither an id nor a link is dropped; a link there is rebuilt from its id",
            odd is [{ WorkshopLink: "" }, { WorkshopLink: var fromLink }] && fromLink == ProfileCsv.WorkshopLink(42), string.Join(" | ", odd.Select(m => m.WorkshopLink)));
        Check("CSV: the Workshop Id column holds just the id, read back as the item's page",
            ProfileCsv.Records(withId) is [_, [.., "3123456789"]] && ProfileCsv.Read(withId).Mods is [{ WorkshopLink: var idLink }]
            && idLink == ProfileCsv.WorkshopLink(3123456789), withId);
        Check("CSV: the columns follow the tick boxes", ProfileCsv.Header(new()).SequenceEqual(["Mod Id", "Name", "Version", "Enabled", "Category", "Workshop Id"])
            && ProfileCsv.Header(new(Category: false, Alias: true, Notes: true)).SequenceEqual(["Mod Id", "Name", "Version", "Enabled", "Workshop Id", "Alias", "Notes"]),
            string.Join(",", ProfileCsv.Header(new(Category: false, Alias: true, Notes: true))));

        // Share profile, from the profile list: at first the active profile's enabled mods in load
        // order with their category, no alias or notes; the tick boxes add the mods that are off, aliases and notes, or
        // take the category out.
        await Choose("Export profile");
        string Csv() => Part<TextEdit>("CsvText")?.Text ?? "";
        async Task Tick(string name, bool on)
        {
            if (Part<Button>(name) is { } box)
                box.ButtonPressed = on;
            await Wait(menu, 0.1);
        }
        bool Ticked(string name) => Part<Button>(name) is { ButtonPressed: true } box && box.GetNodeOrNull<Control>("Box/Tick")?.Visible == true;
        var csv = Csv();
        var records = ProfileCsv.Records(csv);
        var installed = ModManager.Mods.Union(Installed.Mods).Where(m => m.manifest?.id != null).ToList();
        var order = (SaveManager.Instance.SettingsSave.ModSettings?.ModList ?? []).Select(e => e.Id)
            .Where(id => installed.Any(m => m.manifest?.id == id)).Distinct().ToList();
        var ids = records.Skip(1).Select(r => r[0]).ToList();
        var enabledOrder = order.Where(IsOn).ToList();
        // A row per copy that's on (of the fixture twins, the picked one).
        Check("Share profile opens with the active profile's enabled mods in load order, under the default columns",
            Dialog() != null && records.FirstOrDefault()?.SequenceEqual(ProfileCsv.Header(new())) == true && ids.Count == installed.Count(Copies.Ticked)
            && ids.Take(enabledOrder.Count).SequenceEqual(enabledOrder) && records.Skip(1).All(r => r[3] == "TRUE") && !ids.Contains("SmmTestRetired")
            && Part<RichTextLabel>("Body")?.GetParsedText().Contains(startName) == true, $"{string.Join(" | ", ids)} vs order {string.Join(" | ", enabledOrder)}");
        var tops = new[] { "IncludeCategory", "IncludeDisabled", "IncludeAlias", "IncludeNotes", "RememberChoices" }
            .Select(n => Part<Button>(n)?.GetGlobalRect().Position.Y ?? -1).ToList();
        Check("the five tick boxes on one line, Remember selection last", tops.All(y => y >= 0 && Math.Abs(y - tops[0]) < 1)
            && Part<Button>("RememberChoices")?.GetGlobalRect().Position.X > Part<Button>("IncludeNotes")?.GetGlobalRect().End.X, string.Join(", ", tops));
        Check("its tick boxes: Include category on; disabled, alias and notes off",
            Ticked("IncludeCategory") && !Ticked("IncludeDisabled") && !Ticked("IncludeAlias") && !Ticked("IncludeNotes"));
        Check("its tick boxes as heavy as their words: a thicker rim, the ✓ outlined in its own gold and raised off the bottom rim",
            Part<Button>("IncludeCategory")?.GetNodeOrNull<Panel>("Box") is { } tickBox
            && tickBox.GetThemeStylebox("panel") is StyleBoxFlat rim
            && new[] { rim.BorderWidthTop, rim.BorderWidthBottom, rim.BorderWidthLeft, rim.BorderWidthRight }.All(w => w == ProfileShare.BoxRim)
            && tickBox.GetNodeOrNull<Label>("Tick") is { } tickMark && tickMark.GetThemeConstant("outline_size") == ProfileShare.TickOutline
            && tickMark.GetThemeColor("font_outline_color") == tickMark.GetThemeColor("font_color")
            && tickMark.OffsetTop == -ProfileShare.TickRaise && tickMark.OffsetBottom == -ProfileShare.TickRaise);
        var (alias, notes) = (Store.InfoFor(ModEntry.ModId)?.Alias ?? "", Store.InfoFor(ModEntry.ModId)?.Notes ?? "");
        Check("Category is the category; no alias or notes", records.FirstOrDefault(r => r[0] == ModEntry.ModId)?[4] == "Tools" && alias.Length > 0 && notes.Length > 0
            && !csv.Contains(alias) && !csv.Contains(notes) && records.All(r => r.Count == 6), csv);
        await Capture(screen, outDir, "7c-share-profile");
        await Tick("IncludeAlias", true);
        await Tick("IncludeNotes", true);
        await Tick("IncludeCategory", false);
        var more = ProfileCsv.Records(Csv());
        Check("Include alias and notes add their columns with the player's words; without the category, no Category column",
            more.FirstOrDefault()?.SequenceEqual(ProfileCsv.Header(new(Category: false, Alias: true, Notes: true))) == true
            && more.FirstOrDefault(r => r[0] == ModEntry.ModId) is [.., var hasAlias, var hasNotes] && hasAlias == alias && hasNotes == notes, Csv());
        await Tick("IncludeAlias", false);
        await Tick("IncludeNotes", false);
        await Tick("IncludeCategory", true);
        await Tick("IncludeDisabled", true);
        csv = Csv();
        records = ProfileCsv.Records(csv);
        ids = records.Skip(1).Select(r => r[0]).ToList();
        // A row per copy (the fixture twins: two rows of one id, side by side, each on or off as that copy is, told apart
        // by their versions).
        bool RowOn(List<string> r) => installed.FirstOrDefault(m => m.manifest?.id == r[0] && m.manifest?.version == r[2]) is { } copy
            ? Copies.Ticked(copy) : IsOn(r[0]);
        Check("Include disabled: a row per installed mod in load order, TRUE and FALSE as the profile has them", ids.Count == installed.Count
            && ids.Distinct().Take(order.Count).SequenceEqual(order) && records.Skip(1).All(r => r[3] == (RowOn(r) ? "TRUE" : "FALSE")) && ids.Contains("SmmTestRetired")
            && records.Any(r => r[3] == "FALSE") && records.FirstOrDefault()?.SequenceEqual(ProfileCsv.Header(new())) == true,
            string.Join(", ", records.Skip(1).Select(r => $"{r[0]}={r[3]}")));
        var clipboard = DisplayServer.ClipboardGet(); // put back after: a windowed run is on your desktop
        Press("CopyButton");
        await Wait(menu, 0.1);
        Check("Copy says Copied!", Part<Button>("CopyButton")?.Text == "Copied!", Part<Button>("CopyButton")?.Text ?? "no button");
        if (DisplayServer.GetName() == "headless")
            Results.Add("SKIP Copy puts the text on the clipboard: there's no clipboard headless");
        else
        {
            // Line ends aside: Godot's Windows clipboard hands CRLF text back with LF (a paste still gets CRLF).
            var copied = DisplayServer.ClipboardGet();
            Check("Copy puts the text on the clipboard", copied.Replace("\r\n", "\n") == csv.Replace("\r\n", "\n"),
                $"{copied.Length} characters copied, {csv.Length} in the box: {copied[..Math.Min(80, copied.Length)].Replace("\r", "\\r").Replace("\n", "\\n")}");
        }
        DisplayServer.ClipboardSet(clipboard);
        // Save, then Copy, no "…" on Save.
        var buttonOrder = Part<Control>("Buttons")?.GetChildren().OfType<Button>().Select(b => b.Name.ToString()).ToList() ?? [];
        Check("Save comes before Copy, and reads \"Save file\"", buttonOrder.IndexOf("SaveCsvButton") is >= 0 and var save && save < buttonOrder.IndexOf("CopyButton")
            && Part<Button>("SaveCsvButton")?.Text == "Save file", string.Join(", ", buttonOrder));
        var at = new DateTime(2026, 10, 15, 9, 5, 7);
        var file = Path.Combine(outDir, ProfileShare.FileName("Co-op: \"night\"?", at));
        var saveError = ProfileShare.SaveTo(file, csv);
        var bytes = File.Exists(file) ? File.ReadAllBytes(file) : [];
        Check("Save: spire-mods-<profile>-<time>.txt, the name safe for a file, the text as UTF-8 with a BOM",
            Path.GetFileName(file) == "spire-mods-Co-op_ _night__-20261015090507.txt" && saveError == null
            && bytes is [0xEF, 0xBB, 0xBF, ..] && File.ReadAllText(file) == csv, $"{Path.GetFileName(file)}: {saveError}");
        Check("Save: a device name (CON) is safe behind the prefix; importing the file takes the profile's name back",
            ProfileShare.FileName("CON", at) == "spire-mods-CON-20261015090507.txt"
            && Profiles.ImportName($"C:/lists/{ProfileShare.FileName("Brother co-op", at)}") == "Brother co-op",
            $"{ProfileShare.FileName("CON", at)} | {Profiles.ImportName($"C:/lists/{ProfileShare.FileName("Brother co-op", at)}")}");
        await Tick("IncludeDisabled", false); // the tick boxes stay for the session: back as they started
        // Remember selection: off at first, nothing kept; on, the boxes are kept in our data and a new
        // launch starts on them; a tick while on is kept too; off, they're forgotten and a launch starts on the defaults.
        var keptBefore = Store.Data.ShareChoices;
        var remember = Part<Button>("RememberChoices");
        var rightmost = remember?.GetGlobalRect().End.X >= Part<Button>("IncludeNotes")?.GetGlobalRect().End.X;
        await Tick("RememberChoices", true);
        await Tick("IncludeNotes", true);
        var kept = Store.Data.ShareChoices;
        Store.RestoreEdits(Store.EditsJson()); // what an undo does to our data: the kept boxes are view state, left as they are
        var undoKeeps = Store.Data.ShareChoices == kept;
        ProfileShare.ForgetSessionChoices(); // as at the next launch
        ProfileShare.Close();
        await Wait(menu, 0.2);
        await Choose("Export profile");
        var restored = Ticked("IncludeNotes") && Ticked("IncludeCategory") && Ticked("RememberChoices") && ProfileCsv.Records(Csv()).FirstOrDefault()?.Contains("Notes") == true;
        await Tick("RememberChoices", false);
        var forgotten = Store.Data.ShareChoices == null;
        ProfileShare.ForgetSessionChoices();
        ProfileShare.Close();
        await Wait(menu, 0.2);
        await Choose("Export profile");
        Check("Remember selection: off at first and at the row's right; on, the boxes are kept (undo leaves them) and a new launch starts on them; off, forgotten",
            keptBefore == null && remember != null && rightmost && kept == new ShareChoice(true, false, false, true) && undoKeeps && restored && forgotten
            && !Ticked("IncludeNotes") && !Ticked("RememberChoices"), $"before {keptBefore}, kept {kept}, restored {restored}, forgotten {forgotten}");
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
            && Preview().Contains("Creates the profile Imported profile:") && Preview().Contains("Mods enabled ") && !Preview().Contains("Not installed"), Preview());
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
            + "SmmTestUi,Test UI,1.0.0,FALSE,Unassigned,,,\r\n";
        var read = ProfileCsv.Read(bmm).Mods;
        var plan = Profiles.PlanImport(read);
        Check("a Better Mod Menu list: its groups read as categories (its Unassigned as none), its alias unguarded, its notes on two lines",
            read.Select(e => e.Category).SequenceEqual(["Content, mostly", "Content, mostly", "Frameworks", ""])
            && read[1] is { Alias: "=cool", Notes: "Line one\nline two" } && read[0] is { Alias: "", Notes: "" },
            string.Join(" | ", read.Select(e => $"{e.Id}: {e.Category}/{e.Alias}/{e.Notes}")));
        var ours = ProfileCsv.Read(ProfileCsv.Write([new("SmmA", "A", "1", true, "Tools", "", "Aye", "Some notes")], new ProfileCsv.Options(Alias: true, Notes: true))).Mods;
        Check("our own list: its category, alias and notes read back", ours is [{ Category: "Tools", Alias: "Aye", Notes: "Some notes" }],
            string.Join(" | ", ours.Select(e => $"{e.Category}/{e.Alias}/{e.Notes}")));
        var messy = ProfileCsv.Read("Mod Id,Enabled,Category,Alias\nSmmA,TRUE,\"Two\nlines\",\"" + new string('x', 79) + "\U0001F600\U0001F600\"\n").Mods;
        Check("a category or alias from a list: on one line, cut to its box's length by characters, never half of one",
            messy is [{ Category: "Two lines" } one] && one.Alias == new string('x', 79) + "\U0001F600", messy.FirstOrDefault()?.Category ?? "none");
        // What the list gives that the mods don't have: Test Framework keeps its own category while it has one (the
        // fixture's Frameworks, unless an earlier test took that category away), and the mods not installed get theirs,
        // an alias and notes, so they arrive sorted. The categories made: those needed that don't exist, in list order.
        var libCategory = Store.InfoFor("SmmTestLib")?.Category;
        var libSorted = libCategory == null || Categories.Find(libCategory) == null;
        var uiCategory = Store.InfoFor("SmmTestUi")?.Category;
        var (hadContent, hadFrameworks) = (Categories.Find("Content, mostly") != null, Categories.Find("Frameworks") != null);
        var toMake = new[] { "Content, mostly" }.Concat(libSorted ? ["Frameworks"] : Array.Empty<string>()).Where(c => Categories.Find(c) == null).ToList();
        Check("an import sorts only mods without a category of their own, makes the categories it needs, fills in empty aliases and notes",
            plan.Extras.Sorted.OrderBy(s => s.Key).SequenceEqual(new[] { ("SmmTestGoneOff", "Content, mostly"), ("SmmTestNotHere", "Content, mostly") }
                .Concat(libSorted ? [("SmmTestLib", "Frameworks")] : []).OrderBy(s => s.Item1))
            && plan.Extras.NewCategories.SequenceEqual(toMake) && plan.Extras.Details == 2
            && plan.Extras.Named is [("SmmTestNotHere", "=cool", "Line one\nline two")],
            $"{string.Join(", ", plan.Extras.Sorted)}; new {string.Join(", ", plan.Extras.NewCategories)}; named {string.Join(", ", plan.Extras.Named)}");
        // A category the player has, named in another case, is that one; an alias or notes a mod already has stay.
        var existing = Store.Data.Categories.FirstOrDefault()?.Name;
        var libInfo = Store.InfoFor("SmmTestLib");
        var byCase = Profiles.PlanImport([new("SmmTestNotHere", true, "", "", existing?.ToUpperInvariant() ?? "", "", ""),
            new("SmmTestLib", true, "", "", "", "Other alias", "Other notes")]).Extras;
        Check("an import puts a mod into the player's category named in another case, and leaves aliases and notes a mod has",
            existing == null || (byCase.Sorted is [("SmmTestNotHere", var into)] && into == existing && byCase.NewCategories.Count == 0)
            && byCase.Named.Where(n => n.Key == "SmmTestLib").All(n => (n.Alias != null) == string.IsNullOrEmpty(libInfo?.Alias) && (n.Notes != null) == string.IsNullOrEmpty(libInfo?.Notes)),
            $"{existing}: {string.Join(", ", byCase.Sorted)}; new {string.Join(", ", byCase.NewCategories)}; named {string.Join(", ", byCase.Named)}");
        // A mod installed once (the local test mod) listed twice, a Workshop copy first: its one copy takes every row of its
        // id, on as one of them is; nothing is missing, though the list links a Workshop item it doesn't have (an import
        // never offers a second copy of a mod the player has).
        var libOn = IsOn("SmmTestLib");
        string LibPlanned(Profiles.ImportPlan plan) => string.Join(", ", plan.Mods.Where(m => m.Id == "SmmTestLib").Select(m => $"{m.Source}={m.Enabled}"))
            + $" (on {plan.On}, off {plan.Off}, same {plan.Same}); missing {string.Join(", ", plan.Missing)}; both {plan.BothCopies.Count}";
        var twice = Profiles.PlanImport([new("SmmTestLib", !libOn, "Test Framework", link), new("SmmTestLib", libOn, "Test Framework", "")]);
        Check("a mod installed once, listed twice: its copy takes every row of its id (on, as one is); nothing missing", Dependencies.Find("SmmTestLib")?.modSource == ModSource.ModsDirectory
            && twice.Mods.SingleOrDefault(m => m.Id == "SmmTestLib") is { Source: ModSource.ModsDirectory, Enabled: true } && twice.On == (libOn ? 0 : 1)
            && twice.Same == (libOn ? 1 : 0) && twice is { Off: 0, Missing.Count: 0 }, LibPlanned(twice));
        // A list naming the mod by its Workshop item alone, while it's installed here by hand (a friend's list: ours and
        // Better Mod Menu's write a Workshop mod's link): the row sets the copy installed, and nothing is missing (nothing to
        // subscribe to). Listed on or off, no word about both copies: there's one, and the import adds none (that line is
        // for a Workshop copy installed beside it: TestCopies).
        var bothOn = Profiles.PlanImport([new("SmmTestLib", true, "Test Framework", link), new("SmmTestLib", true, "Test Framework", "")]);
        var linkOn = Profiles.PlanImport([new("SmmTestLib", true, "Test Framework", link)]);
        var linkOff = Profiles.PlanImport([new("SmmTestLib", false, "Test Framework", link)]);
        var (onPreview, offPreview) = (ProfileShare.PreviewFor([new("SmmTestLib", true, "Test Framework", link)], "On"),
            ProfileShare.PreviewFor([new("SmmTestLib", false, "Test Framework", link)], "Off"));
        var bothCopies = Menus.Text("SPIRE_MOD_MANAGER.IMPORT_BOTH_COPIES");
        Check("a list's Workshop row for a mod installed by hand sets that copy: on turns it on, off turns it off, nothing missing either way (no subscribe), "
            + "and no word about both copies (there's one)",
            bothOn.BothCopies.Count == 0 && linkOn.BothCopies.Count == 0 && linkOff.BothCopies.Count == 0
            && linkOn.Mods.SingleOrDefault(m => m.Id == "SmmTestLib") is { Source: ModSource.ModsDirectory, Enabled: true } && linkOn.On == (libOn ? 0 : 1)
            && linkOff.Mods.SingleOrDefault(m => m.Id == "SmmTestLib") is { Source: ModSource.ModsDirectory, Enabled: false } && linkOff.Off == (libOn ? 1 : 0)
            && linkOn.Missing.Count == 0 && linkOff.Missing.Count == 0 && bothOn.Missing.Count == 0
            && !onPreview.Contains(bothCopies) && !onPreview.Contains("Not installed")
            && !offPreview.Contains(bothCopies) && !offPreview.Contains("Not installed"),
            $"framework on {libOn}; on: {LibPlanned(linkOn)}; off: {LibPlanned(linkOff)}; both: {LibPlanned(bothOn)}; {onPreview} || {offPreview}");
        var (wantOn, wantOff) = ((IsOn("SmmTestLib") ? 0 : 1), (IsOn("SmmTestUi") ? 1 : 0));
        // "1 mod sorted into categories", "3 mods sorted into categories": a count of mods, said as one.
        static string SortedLine(int count) => count == 1 ? "1 mod sorted into categories" : $"{count} mods sorted into categories";
        var others = installed.Count(m => m.manifest?.id is { } id && id != ModEntry.ModId && id != Bmm.ModId && id != "SmmTestLib" && id != "SmmTestUi");
        await Choose("Import profile");
        await Paste(bmm);
        var buttonsWithPreview = Part<Control>("Buttons")?.GetGlobalRect().Position.Y ?? -1;
        var bbcode = Part<RichTextLabel>("Preview")?.Text ?? "";
        // The summary: what the list holds, then only what changes compared with now (no line for the mods
        // already as listed, nor for the ones it doesn't name).
        string Changes(int on, int off) => on == 0 && off == 0 ? "None"
            : string.Join("\n", new[] { on > 0 ? $"Mods enabled {on}" : null, off > 0 ? $"Mods disabled {off}" : null }.OfType<string>().Select(l => "  •  " + l));
        Check("a Better Mod Menu list: what it holds (2 enabled, 2 disabled), then what it changes compared with now, and nothing about the rest",
            Status() == "" && plan.On == wantOn && plan.Off == wantOff && plan.Same == 2 - wantOn - wantOff && plan.Unlisted == others
            && Preview().Contains("Contains 2 enabled mods and 2 disabled mods") && Preview().Contains("Changes compared to the current profile:\n"
                + (wantOn == 0 && wantOff == 0 ? "" : Changes(wantOn, wantOff) + "\n") + "  •  " + SortedLine(plan.Extras.Sorted.Count))
            && !Preview().Contains("already set") && !Preview().Contains("aren't in the list") && !Preview().Contains("isn't in the list"),
            $"on {plan.On}/{wantOn}, off {plan.Off}/{wantOff}, same {plan.Same}, unlisted {plan.Unlisted}/{others}: {Preview()}");
        Check("the preview counts what else the list does: mods sorted into categories, new categories, aliases and notes added; no None then",
            Preview().Contains(SortedLine(plan.Extras.Sorted.Count) + "\n")
            && (plan.Extras.NewCategories.Count == 0 ? !Preview().Contains("New categories") : Preview().Contains($"New categories {plan.Extras.NewCategories.Count}\n"))
            && Preview().Contains("Aliases and notes added 2\n") && !Preview().Contains("  •  None"), Preview());
        var onlyOn = ProfileShare.PreviewFor(ProfileCsv.Read("Mod Id,Enabled\nSmmTestLib,\nSmmTestUi,TRUE\n").Mods, "Only on");
        Check("a list with nothing disabled says just its enabled mods; a blank Enabled reads as enabled", onlyOn.Contains("Contains 2 enabled mods")
            && !onlyOn.Contains("disabled mods"), onlyOn);
        // A row that ends before its Enabled cell reads as a blank one: enabled (#95: it was refused, with an error
        // that said blank was fine).
        var cutShort = ProfileCsv.Read("Mod Id,Name,Enabled\nSmmTestUi,Test UI\nSmmTestLib,Test Framework,FALSE\n");
        Check("a row cut short before its Enabled cell reads as enabled, like a blank one",
            cutShort.Error == null && cutShort.Mods.SequenceEqual([new ProfileCsv.Entry("SmmTestUi", true, "Test UI", ""), new ProfileCsv.Entry("SmmTestLib", false, "Test Framework", "")]),
            cutShort.Error ?? string.Join(", ", cutShort.Mods));
        // The name itself is the link to its Workshop page; one with no page is plain.
        var linkAt = bbcode.IndexOf($"[url={link}]", StringComparison.Ordinal);
        var nameAt = linkAt < 0 ? -1 : bbcode.IndexOf("Not Here Mod", linkAt, StringComparison.Ordinal);
        Check("the mods it names that aren't installed are listed by name, a name with a Workshop page its link to it", plan.Missing.Count == 2
            && Preview().Contains("Not installed (2)") && Preview().Contains("Not Here Mod") && Preview().Contains("Gone Off (disabled)")
            && nameAt > linkAt && nameAt < bbcode.IndexOf("[/url]", linkAt, StringComparison.Ordinal)
            && bbcode.Split("[url=").Length == 2, Preview());
        // That link leaves the game (#126): the external-link mark after the name, in the link; still the page's link.
        var markTag = LinkMark.Tag(18, "#99ccff");
        var markAt = linkAt < 0 ? -1 : bbcode.IndexOf(markTag, linkAt, StringComparison.Ordinal);
        Check("an import's Workshop link: the external-link mark right after the name, inside its link and colour, 12 px for the preview's 18 px text; none after a name with no page",
            markAt > nameAt && bbcode[..markAt].EndsWith("[color=#99ccff][b]Not Here Mod[/b]") && bbcode[(markAt + markTag.Length)..].StartsWith("[/color][/url]")
            && bbcode.Split(markTag).Length == 2 && LinkMark.Size(18) == 12 && Part<RichTextLabel>("Preview")?.GetThemeFontSize("normal_font_size") == 18, bbcode);
        Check("an import's Workshop link still opens its page: the link is the page's address, the preview still handles its click",
            ProfileCsv.IsWorkshopLink(link) && Part<RichTextLabel>("Preview") is { MouseFilter: Control.MouseFilterEnum.Stop } shown
            && shown.GetSignalConnectionList(RichTextLabel.SignalName.MetaClicked).Count > 0);
        await Capture(screen, outDir, "7d-import-preview");
        Press("CreateProfileButton");
        await Wait(menu, 0.3);
        var made = Profiles.Active;
        Check("its categories, alias and notes applied: the mods not installed sorted for when they are; Test Framework's own category kept",
            Categories.Find("Content, mostly") != null && Store.InfoFor("SmmTestNotHere") is { Category: "Content, mostly", Alias: "=cool", Notes: "Line one\nline two" }
            && Store.InfoFor("SmmTestGoneOff")?.Category == "Content, mostly" && Store.InfoFor("SmmTestLib")?.Category == (libSorted ? "Frameworks" : libCategory)
            && Store.InfoFor("SmmTestUi")?.Category == uiCategory,
            $"{Store.InfoFor("SmmTestNotHere")?.Category}/{Store.InfoFor("SmmTestNotHere")?.Alias}; lib {Store.InfoFor("SmmTestLib")?.Category}");
        Check("its states applied; the new profile keeps the mods that aren't installed, for later", made?.Name == "Imported profile"
            && IsOn("SmmTestLib") && !IsOn("SmmTestUi")
            && made.Mods.Any(m => m is { Id: "SmmTestNotHere", Source: ModSource.SteamWorkshop, Enabled: true })
            && made.Mods.Any(m => m is { Id: "SmmTestGoneOff", Source: ModSource.ModsDirectory, Enabled: false }),
            string.Join(", ", made?.Mods.Select(m => $"{m.Id}/{m.Source}={m.Enabled}") ?? []));
        History.Undo();
        await Wait(menu, 0.3);
        Check("and undo takes that one away too, its categories, alias and notes with it (one step)", Named("Imported profile") == null && Profiles.Active?.Name == otherName
            && ModListState().SequenceEqual(otherStates) && (Categories.Find("Content, mostly") != null) == hadContent
            && (Categories.Find("Frameworks") != null) == hadFrameworks
            && Store.InfoFor("SmmTestNotHere") == null && Store.InfoFor("SmmTestGoneOff") == null && Store.InfoFor("SmmTestLib")?.Category == libCategory,
            $"{Store.InfoFor("SmmTestNotHere")?.Category}; content {Categories.Find("Content, mostly") != null}");

        // Errors show in red in the dialog, and nothing is made: no Mod Id column, Enabled that isn't a yes or no, no text.
        await Choose("Import profile");
        await Paste("Name,Enabled\r\nTest UI,TRUE\r\n");
        var noId = (Status(), StatusRed(), Preview());
        var buttonsWithError = Part<Control>("Buttons")?.GetGlobalRect().Position.Y ?? -2;
        Check("Import profile keeps its size and place as an error replaces the preview: the buttons stay put",
            Math.Abs(buttonsWithPreview - buttonsWithError) < 1, $"buttons at {buttonsWithPreview:0} with a preview, {buttonsWithError:0} with an error");
        await Capture(screen, outDir, "7e-import-error");
        await Paste("Mod Id,Enabled\nSmmTestUi,maybe\n");
        var badValue = Status();
        await Paste("");
        Press("CreateProfileButton");
        await Wait(menu, 0.2);
        Check("a list without Mod Id says so in red, with no preview", noId.Item1.StartsWith("This list is missing the Mod Id column") && noId.Item2 && noId.Item3 == "",
            $"\"{noId.Item1}\" red {noId.Item2}, preview \"{noId.Item3}\"");
        Check("a bad Enabled value names its mod", badValue == "Enabled value for SmmTestUi must be \"TRUE\", \"FALSE\" or blank. Current value \"maybe\" can not be parsed.", badValue);
        Check("Create profile with no text says what to do, in red, and makes nothing", Dialog() != null && Status() == "Load or paste a mod list first"
            && StatusRed() && Store.Data.Profiles.Count == profiles, Status());
        Check("the imported name: the file's, numbered when taken; not Better Mod Menu's time-stamped one",
            Profiles.ImportName("C:/lists/Brother co-op.csv") == "Brother co-op" && Profiles.ImportName("C:/lists/Brother co-op.txt") == "Brother co-op" && Profiles.ImportName($"{startName}.csv") == Profiles.CopyName(startName)
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
        if (Dependencies.Find("SmmTestUi") is { } testUi)
            Dependencies.Set([testUi], !IsOn("SmmTestUi"), screen);
        else
            Results.Add("FAIL Share profile's keys: no Test UI mod to edit");
        CategoryList.Redraw(content);
        await Wait(menu, 0.3);
        var edited = (Profiles.Edited, string.Join(", ", ModListState()));
        var search = screen.GetNodeOrNull<LineEdit>("ModsBorder/SpireModManagerSearch");
        if (search != null)
        {
            search.Text = "zzz"; // no TextChanged: the list isn't filtered
            search.GrabFocus();
        }
        await Choose("Export profile");
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

    // New mods and profiles. The fixture's Default profile doesn't list the test mods, which
    // are on (the game's default for a new mod): they joined it at launch, on, quietly (no unsaved edit, and so
    // already in the state the session started from, which Changes and Revert all compare with).
    // The page link on our title's baseline, with or without a taller fallback a mod adds to the game's shared fonts (for
    // its own script): the letters don't use it, though each font's own ascent and height grow (Jordan's game, 22 Oct: the
    // link 7 px low at 1440p).
    static void TestPageLinkBaseline(NModdingScreen screen)
    {
        if (screen.GetNodeOrNull<LinkButton>("SpireModManagerViewToggle") is not { } link)
        {
            Check("the page link stays on the title's baseline when a mod adds a taller fallback to the game's fonts", false, "no page link");
            return;
        }
        var fonts = new[] { screen.FindChild("InstalledModsTitle", true, false) is RichTextLabel title ? title.GetThemeFont("normal_font") : null,
            link.GetThemeFont("font") }.OfType<Font>().Distinct().ToList();
        var (linkY, before) = (link.Position.Y, fonts.Select(f => new Godot.Collections.Array<Font>(f.Fallbacks)).ToList());
        var ascent = fonts.Select(f => f.GetAscent(CategoryList.PageLinkSize)).ToList();
        var (tallY, tallAscent) = (0f, new List<float>());
        try
        {
            foreach (var font in fonts)
                font.Fallbacks = new Godot.Collections.Array<Font>(font.Fallbacks) { new FontVariation { BaseFont = ThemeDB.FallbackFont, SpacingTop = 24, SpacingBottom = 12 } };
            Layout.Apply(screen);
            (tallY, tallAscent) = (link.Position.Y, fonts.Select(f => f.GetAscent(CategoryList.PageLinkSize)).ToList());
        }
        finally
        {
            for (var i = 0; i < fonts.Count; i++)
                fonts[i].Fallbacks = before[i];
            Layout.Apply(screen);
        }
        Check("the page link stays on the title's baseline when a mod adds a taller fallback to the game's fonts (the letters don't use it)",
            fonts.Count > 0 && tallAscent.Zip(ascent).All(p => p.First > p.Second + 1) && Math.Abs(tallY - linkY) < 0.5 && Math.Abs(link.Position.Y - linkY) < 0.5,
            $"{fonts.Count} font(s), ascent {string.Join("/", ascent)} -> {string.Join("/", tallAscent)}, link y {linkY} -> {tallY} -> {link.Position.Y}");
    }

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
        // The fixture twins too, copy by copy, as the launch reads our file from before copies had a box each: the picked
        // one (B) on, the other off beside it.
        if (Copies.TwinGroups().Any(g => g.Id == TwinId))
            Check("... the twins copy by copy: the picked one on, the other off beside it, as the launch has them",
                profile.Mods.FirstOrDefault(m => m.Id == TwinId && m.Source == ModSource.ModsDirectory) is { Enabled: true, Copy: "l:SmmTestTwinB", CopiesOff: ["l:SmmTestTwinA"] },
                string.Join(", ", profile.Mods.Where(m => m.Id == TwinId).Select(m => $"{m.Source} {m.Enabled} {m.Copy} off [{string.Join(",", m.CopiesOff ?? [])}]")));
        var (on, off, _) = Profiles.UnsavedChanges();
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
        // The notepad marks notes: an alias alone (it shows beside the original name already) puts none.
        if (Row("SmmTestContent") is { } aliasOnly && Store.InfoFor("SmmTestContent")?.Notes == null)
        {
            var aliasBefore = Store.InfoFor("SmmTestContent")?.Alias;
            Store.EditMod("SmmTestContent", info => info.Alias = "Alias only");
            CategoryList.Redraw(content);
            await Wait(menu, 0.3);
            Check("an alias alone puts no notepad on the row", RowTitle("SmmTestContent").StartsWith("Alias only")
                && aliasOnly.GetNodeOrNull<Control>("SpireModManagerEditedMark") is not { Visible: true }, RowTitle("SmmTestContent"));
            Store.EditMod("SmmTestContent", info => info.Alias = aliasBefore);
            CategoryList.Redraw(content);
            await Wait(menu, 0.2);
        }
        // The game's own load errors mark parts with its colour tags ("Mod [red]{id}[/red] only supports..."): the panel
        // shows their text, not the tags.
        if (other.Mod is { } erring)
        {
            var errorsBefore = erring.errors;
            var branchError = new LocString("main_menu_ui", "MOD_ERROR.STEAM_BRANCH_UNSUPPORTED");
            branchError.Add("id", erring.manifest?.id ?? "");
            branchError.Add("supportedBranch", "public");
            branchError.Add("currentBranch", "public-beta");
            erring.errors = [branchError];
            try
            {
                CategoryList.Select(screen, other);
                await PanelFor(menu, screen, other);
                await Wait(menu, 0.5);
                var descriptionLabel = panel.GetNode<RichTextLabel>("ModDescription");
                var (shown, raw) = (descriptionLabel.GetParsedText(), descriptionLabel.Text);
                await Capture(screen, outDir, "17b-game-error-panel");
                var card = ModDetails.Card(erring); // the row's hover card shows the same (as BBCode: "[lb]" is a "[")
                Check("a game load error shows apart from ours, in the panel and the row's card: under \"Error while loading\", alone in its box, "
                    + "in the game's red (its own [red] in the panel, its colour in the card, no tags as text), not among our problems",
                    shown.Contains("Error while loading") && shown.Contains("only supports Steam branch public") && !shown.Contains("[red]")
                    && raw.Contains(GameText.Caption("Error while loading")) && raw.Contains("[table=1]") && raw.Contains("[red]Mod ")
                    && card.Contains(GameText.Caption("Error while loading")) && card.Contains("[table=1]") && card.Contains("[color=#ff5555]Mod ")
                    && !card.Contains("[lb]red]") && !ModDetails.Problems(erring).Any(p => p.Contains("Steam branch")),
                    $"{shown.Split('\n')[0]} | card: {card.Split('\n').FirstOrDefault(l => l.Contains("branch"))}");
            }
            finally
            {
                erring.errors = errorsBefore;
            }
        }
        else
            Results.Add("SKIP a game load error in the panel: the fixture row has no mod");
        // The game marks every Workshop mod outside its author's branches before it knows which are off (a dev run has
        // no Steam, so these are made up): a mod that's off loses that error and keeps its others, one on keeps it, and
        // the game's message still shows, marked as the game's, in the mod's card and panel.
        Check("our Init drops the Steam branch error of mods that are off", BranchErrors.Ran);
        if (ModManager.Mods.FirstOrDefault(m => m.state == ModLoadState.Disabled) is { } offMod
            && ModManager.Mods.FirstOrDefault(m => m.state == ModLoadState.Loaded && m.manifest?.id != ModEntry.ModId) is { } onMod)
        {
            var (offBefore, onBefore) = (offMod.errors, onMod.errors);
            static LocString Error(string key) => new("main_menu_ui", key);
            LocString Branch(Mod mod)
            {
                var error = Error("MOD_ERROR.STEAM_BRANCH_UNSUPPORTED");
                error.Add("id", mod.manifest?.id ?? "");
                error.Add("supportedBranch", "public");
                error.Add("currentBranch", "public-beta");
                return error;
            }
            offMod.errors = [Branch(offMod), Error("MOD_ERROR.DUPLICATE_ID")];
            onMod.errors = [Branch(onMod)];
            try
            {
                var dropped = BranchErrors.DropFromOffMods([offMod, onMod]);
                var offKeys = offMod.errors?.Select(e => e.LocEntryKey).ToList() ?? [];
                var onKeys = onMod.errors?.Select(e => e.LocEntryKey).ToList() ?? [];
                var card = ModDetails.Card(offMod);
                var gameMessage = $"Mod {offMod.manifest?.id} only supports Steam branch public";
                Check("the game's message taken off a mod that's off still shows in its card, apart from ours under its own caption, in a fainter box, "
                    + "not as a problem",
                    card.Contains(GameText.Caption("Error while loading - Can be ignored if mod is disabled")) && card.Contains(gameMessage)
                    && card.Contains(GameText.Box("", dim: true)[..40]) && !ModDetails.Problems(offMod).Any(p => p.Contains("only supports Steam branch")),
                    card.Split('\n').FirstOrDefault(l => l.Contains("branch")) ?? card.Split('\n')[0]);
                // The second pass, just before the main menu's warning reads the errors (BranchErrorsLatePatch): one
                // marked after our Init goes there too.
                if (menu.GetTree().Root.FindChildren("*", "", true, false).OfType<NDebugInfoLabelManager>().FirstOrDefault() is { } warning)
                {
                    offMod.errors = [Branch(offMod)];
                    HarmonyLib.AccessTools.Method(typeof(NDebugInfoLabelManager), "UpdateText")?.Invoke(warning, [null]);
                    Check("the main menu's warning: a Steam branch error put on a mod that's off after our Init goes before the warning reads it",
                        offMod.errors == null, $"{offMod.errors?.Count} left");
                }
                else
                    Results.Add("SKIP the late pass of the Steam branch error: no main menu warning in the tree");
                offMod.errors = [Error("MOD_ERROR.STEAM_BRANCH_UNSUPPORTED")];
                BranchErrors.DropFromOffMods([offMod]);
                Check("a mod that's off loses the game's Steam branch error (it doesn't load) and keeps its others; one on keeps it; "
                    + "none left is none, as the game keeps it", dropped == 1 && offKeys.SequenceEqual(["MOD_ERROR.DUPLICATE_ID"])
                    && onKeys.SequenceEqual(["MOD_ERROR.STEAM_BRANCH_UNSUPPORTED"]) && offMod.errors == null,
                    $"dropped {dropped}; off: {string.Join(",", offKeys)}; on: {string.Join(",", onKeys)}; then {offMod.errors?.Count}");
            }
            finally
            {
                (offMod.errors, onMod.errors) = (offBefore, onBefore);
                BranchErrors.TestForget(offMod);
            }
        }
        else
            Results.Add("SKIP the Steam branch error of a mod that's off: no fixture mod off, or none on");
        CategoryList.Select(screen, ours);
        await PanelFor(menu, screen, ours);
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
        Check("a local mod's source line links nowhere: no external-link mark, and no room kept for one",
            panel.FindChild("SpireModManagerLink", true, false) is LinkButton localLink && LinkMark.Rect(localLink) == null && localLink.CustomMinimumSize.X == 0);
        var headText = panel.GetNodeOrNull<RichTextLabel>("SpireModManagerHead")?.GetParsedText() ?? "";
        Check("the head: author and compatibility; the details: description, then size", headText.Contains("Author: symphony") && headText.Contains("Compatibility:")
            && !description.StartsWith("Enabled") && description.Contains("Size:") && !description.Contains("Compatibility"), $"{headText} / {description[..Math.Min(60, description.Length)]}");
        // Other keywords: where Workshop tags would be (a local mod has none: right after Size), the hidden tags that find the
        // mod as #name, in their order; the whole row at about half the panel text's alpha, links too; a click on one
        // searches it as a Workshop tag's does, a second clears it; none in the game's panel (Vanilla View).
        var keywordsLabel = Menus.Text("SPIRE_MOD_MANAGER.OTHER_KEYWORDS");
        var facts = description.Split('\n');
        var keywordsAt = Array.FindIndex(facts, l => l.StartsWith(keywordsLabel + ": "));
        var keywords = $"{keywordsLabel}: {string.Join(", ", HiddenTagNames.Where(t => Search.Matches(ours.Mod, t)))}";
        Check("Other keywords right after Size: the hidden tags that find the mod as #name, in their order (this mod: #enabled, #local, #installed, #loaded... "
            + "#renamed, #notes)", keywordsAt > 0 && facts[keywordsAt - 1].StartsWith(Menus.Text("SPIRE_MOD_MANAGER.SIZE") + ": ") && facts[keywordsAt] == keywords
            && keywords.StartsWith($"{keywordsLabel}: #enabled, #local, #installed, #loaded, ") && keywords.Contains("#renamed, #notes"),
            $"{(keywordsAt < 0 ? "no row" : facts[keywordsAt])}; wanted {keywords}");
        var panelText = panel.GetNode<RichTextLabel>("ModDescription");
        var keywordsRow = panelText.Text.Split('\n').FirstOrDefault(l => l.Contains(keywordsLabel)) ?? "";
        var faint = keywordsRow.StartsWith("[color=#") ? keywordsRow[7..keywordsRow.IndexOf(']')] : "";
        var (alpha, textAlpha) = (Color.HtmlIsValid(faint) ? Color.FromHtml(faint).A : 0, panelText.GetThemeColor("default_color").A);
        Check("Other keywords fainter than the facts: the whole row, label and links, at about half the panel text's alpha (no gold, no colour of their own)",
            keywordsRow.EndsWith("[/color]") && keywordsRow.Split("[color=").Length == 2 && !keywordsRow.Contains("[gold]") && Math.Abs(alpha - textAlpha / 2) < 0.08f
            && keywordsRow.Contains("[url=htag:enabled]#enabled[/url], "), $"{keywordsRow} (alpha {alpha:0.00}, the panel text's {textAlpha:0.00})");
        var searchBox = screen.GetNode<LineEdit>("ModsBorder/SpireModManagerSearch");
        panelText.EmitSignal(RichTextLabel.SignalName.MetaClicked, "htag:enabled");
        var searched = searchBox.Text;
        panelText.EmitSignal(RichTextLabel.SignalName.MetaClicked, "htag:enabled");
        await Wait(menu, 0.3);
        Check("a click on an Other keyword searches it, as a Workshop tag's does; a second clears it", searched == "#enabled" && searchBox.Text == "",
            $"\"{searched}\", then \"{searchBox.Text}\"");
        var inVanilla = "";
        await WithVanillaView(screen, menu, () =>
        {
            inVanilla = panelText.Text;
            return true;
        });
        await PanelFor(menu, screen, ours);
        Check("Vanilla View: the game's panel has no Other keywords; ours has them again after", inVanilla.Length > 0 && !inVanilla.Contains(keywordsLabel)
            && !inVanilla.Contains("htag:") && panelText.Text.Contains(keywordsLabel), inVanilla[..Math.Min(80, inVanilla.Length)]);
        Check("fields in order: Alias, Category, Tags, Notes", string.Join(",", yours.GetChildren().Select(c => c.Name.ToString())) == "Alias,Category,Tags,Notes",
            string.Join(",", yours.GetChildren().Select(c => c.Name.ToString())));
        // The category picker drops its list right under it (2 below it, from its left edge) where it fits, otherwise
        // just above it; moved near the bottom of the screen, just above it, not pushed up over it. Moved for the press
        // only, and put back in the same frame.
        var categoryPicker = yours.GetNode<Button>("Category/Picker");
        var screenSize = categoryPicker.GetViewportRect().Size;
        async Task<(string? Misplaced, bool Up)> PickerMenu(float? top = null)
        {
            var home = categoryPicker.Position;
            if (top is { } y)
                categoryPicker.GlobalPosition = new Vector2(categoryPicker.GlobalPosition.X, y);
            var from = categoryPicker.GetGlobalRect().GrowIndividual(0, 2, 0, 2);
            categoryPicker.EmitSignal(BaseButton.SignalName.Pressed);
            categoryPicker.Position = home;
            await Wait(menu, 0.2);
            var open = MenuPanel.Current;
            var size = open?.GetNodeOrNull<Control>("Catcher/Panel")?.Size ?? Vector2.Zero;
            var (up, left) = (from.End.Y + size.Y > screenSize.Y, from.Position.X + size.X > screenSize.X);
            var misplaced = MenuMisplaced(open, from, up, left);
            MenuPanel.Close();
            return (misplaced, up);
        }
        var (ownPlace, ownUp) = await PickerMenu();
        Results.Add($"INFO the category picker's list opened {(ownUp ? "above" : "below")} it");
        Check("the category picker drops its list right under it where it fits, otherwise just above it", ownPlace == null, ownPlace ?? "");
        var (lowPlace, lowUp) = await PickerMenu(screenSize.Y - categoryPicker.Size.Y - 10);
        Check("the category picker near the bottom of the screen: its list opens just above it, not pushed up over it", lowUp && lowPlace == null,
            lowPlace ?? "below it");
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
        await WaitReal(menu, Hover.Delay + 0.15);
        var big = screen.GetNodeOrNull("SpireModManagerPictureCard")?.FindChildren("*", "TextureRect", true, false).OfType<TextureRect>().FirstOrDefault();
        Check("hovering the thumbnail shows the whole picture, at most 800 wide", big != null && big.Size.X == 800 && big.Size.Y == 450, $"{big?.Size}");
        Hover.TestMouse = () => new Vector2(5, 5);
        await WaitReal(menu, Hover.WindDown + Hover.Grace + 0.1); // the picture card gone and its grace over: the title's card below waits the delay
        Hover.TestMouse = null;
        image.Texture = null;
        Layout.PlaceInfo(panel);
        Check("no picture: the description starts under the head; with one, under the picture", description0 < 160 && withPicture >= 16 + ModDetails.ThumbHeight,
            $"{description0} / {withPicture}");
        await Capture(screen, outDir, "8-details-with-notes");

        // Notes and tags typed in the panel save when focus leaves; notes are trimmed, tags tidied.
        CategoryList.Select(screen, other);
        await PanelFor(menu, screen, other);
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
        // A click on the pills opens the box ready for another tag: ", " after them and the caret after that (it was at
        // the start), so the suggestions don't take the last tag for the word being typed.
        // Clicking away leaves the tags as they were.
        yours.GetNode<Control>("Tags/CustomTags").EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false });
        await Wait(menu, 0.1);
        Check("a click on the tag pills opens the box ready for another tag", tags.Visible && tags.HasFocus() && tags.Text == "co-op, Favorites, " && tags.CaretColumn == tags.Text.Length
            && !TagSuggestions.Labels().Any(t => t.Contains("Favorites", StringComparison.OrdinalIgnoreCase)),
            $"\"{tags.Text}\" caret {tags.CaretColumn}, focus {tags.HasFocus()}, offered {string.Join(", ", TagSuggestions.Labels())}");
        // The look of the tags while they're typed (TagPills): three pills and a word being typed, then as they were.
        void Typed(string text)
        {
            tags.Text = text;
            tags.CaretColumn = text.Length;
            tags.EmitSignal(LineEdit.SignalName.TextChanged, text);
        }
        Typed("co-op, Favorites, Story-heavy, wor");
        await Wait(menu, 0.2);
        Results.Add($"INFO tag pills drawn: {string.Join(", ", TagPills.TestPills(tags).Select(p => $"{p.Tag} {p.Rect}"))}; text: "
            + string.Join(" | ", TagPills.TestInk(tags).Select(r => $"\"{r.Text}\" {r.Color} {r.Size}px")));
        await Capture(screen, outDir, "9a-tags-editing");
        tags.CaretColumn = 10; // in "Favorites": that pill at the box's size, the others smaller
        await Wait(menu, 0.1);
        await Capture(screen, outDir, "9a-tags-caret-in-pill");
        Typed("co-op, Favorites, ");
        tags.ReleaseFocus();
        await Wait(menu, 0.3);
        Check("clicking away after that keeps the tags as pills", Store.InfoFor("SmmTestFixes")?.Tags?.SequenceEqual(["co-op", "Favorites"]) == true
            && tags.Text == "co-op, Favorites" && yours.GetNode<Control>("Tags/CustomTags").Visible && !tags.Visible,
            $"\"{tags.Text}\" / {string.Join(", ", Store.InfoFor("SmmTestFixes")?.Tags ?? [])}");
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
            await WaitReal(menu, seconds);
        }
        // The mouse at a given point over a hover area (not its centre), as the area sees it coming in.
        async Task HoverAt(Control target, Vector2 at, double seconds)
        {
            Hover.TestMouse = () => at;
            target.EmitSignal(Control.SignalName.MouseEntered);
            await WaitReal(menu, seconds);
        }
        async Task Leave(Control target, double seconds)
        {
            Hover.TestMouse = () => new Vector2(5, 5);
            target.EmitSignal(Control.SignalName.MouseExited);
            await WaitReal(menu, seconds);
        }
        var hot = other.GetNode<Control>("SpireModManagerTitleHover");
        var mark = other.GetNode<Control>("SpireModManagerEditedMark");
        var markHot = other.GetNode<Control>(RowTitles.MarkHotName); // the notepad's hover area, over it (the notepad takes no mouse)
        var oursHot = ours.GetNode<Control>("SpireModManagerTitleHover");
        Check("the notepad sits left of the name, in the room before the title", mark.Position.X + mark.Size.X <= other.GetNode<Control>("Title").Position.X && mark.Position.X >= 0,
            $"mark at {mark.Position.X}, title text {other.GetNode<Control>("Title").Position.X}..+{hot.Size.X}");
        await HoverOver(hot, Hover.Delay - 0.15);
        Check("the title's card waits for the delay", !CardShown());
        await WaitReal(menu, 0.3);
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
        await Leave(oursHot, 0.3);
        // The notepad: a plain "Contains note" after the usual delay, not the mod's card (that's the title's), and no
        // grace either way.
        bool TipShown() => screen.HasNode(RowTitles.NoteTipName);
        string TipText() => screen.GetNodeOrNull(RowTitles.NoteTipName)?.FindChildren("*", "RichTextLabel", true, false).OfType<RichTextLabel>()
            .FirstOrDefault()?.GetParsedText() ?? "";
        await HoverAt(markHot, mark.GetGlobalRect().GetCenter(), 0.1); // on the notepad itself
        var early = TipShown() || CardShown();
        await WaitReal(menu, Hover.Delay);
        Check("the notepad: a plain \"Contains note\" after the usual delay, not the mod's card", !early && TipShown() && TipText() == "Contains note"
            && Cards() == 0, $"early {early}, tip \"{TipText()}\", cards {Cards()}");
        await Leave(markHot, 0.1);
        Check("the notepad's tip goes with the mouse", !TipShown());
        await HoverOver(hot, 0.2);
        Check("no grace period after the notepad: the title waits again", !CardShown());
        await Leave(hot, 0.3);

        // The notepad's hover area (issue 124): the notepad is small, so the area reaches a little left of it (no further
        // than the row's edge), above and below (the title's line, as the title's area beside it), and not right, where the
        // title's area starts. The notepad is drawn as before, its own size, and takes no mouse; the area passes clicks on.
        var (markAt, areaAt, titleAt, rowAt) = (mark.GetGlobalRect(), markHot.GetGlobalRect(), hot.GetGlobalRect(), other.GetGlobalRect());
        Check("the notepad's hover area reaches a little left of it, above and below, not right into the title; clicks pass through it",
            markHot.Visible && areaAt.Encloses(markAt) && areaAt.Position.X < markAt.Position.X && areaAt.Position.X >= rowAt.Position.X
            && areaAt.Position.Y < markAt.Position.Y && areaAt.End.Y > markAt.End.Y && Math.Abs(areaAt.End.X - markAt.End.X) < 0.5f
            && areaAt.End.X <= titleAt.Position.X && Math.Abs(areaAt.Position.Y - titleAt.Position.Y) < 0.5f && Math.Abs(areaAt.End.Y - titleAt.End.Y) < 0.5f
            && mark.Size == Icons.Notepad.GetSize() && mark.MouseFilter == Control.MouseFilterEnum.Ignore && markHot.MouseFilter == Control.MouseFilterEnum.Pass,
            $"area {areaAt}, notepad {markAt} ({mark.MouseFilter}), title's area {titleAt}, row {rowAt}");
        // Just left of the notepad, off it but in the area: its "Contains note", as on the notepad.
        Vector2 LeftOfMark() => new((markHot.GetGlobalRect().Position.X + mark.GetGlobalRect().Position.X) / 2, mark.GetGlobalRect().GetCenter().Y);
        var leftOf = LeftOfMark();
        await HoverAt(markHot, leftOf, Hover.Delay + 0.15);
        Check("hovering just left of the notepad shows its \"Contains note\"", areaAt.HasPoint(leftOf) && !markAt.HasPoint(leftOf)
            && TipShown() && TipText() == "Contains note", $"at {leftOf}, area {areaAt}, notepad {markAt}, tip \"{TipText()}\"");
        await Leave(markHot, 0.1);
        // A click there, through the viewport as the game gets a player's (works headless too; only hover needs a window):
        // it lands on the area, which passes it on to the row, selected as by a click anywhere else on it. The game's row
        // takes a click only while the mouse is on it, so first its mouse_entered (a mouse motion doesn't hover headless).
        // The row in view first, as a player's click needs it.
        var listView = content.GetParent<Control>();
        if (!listView.GetGlobalRect().Encloses(markHot.GetGlobalRect()))
        {
            CategoryList.ScrollTo(content, other);
            await Wait(menu, 1.5);
        }
        CategoryList.Select(screen, ours);
        await PanelFor(menu, screen, ours);
        await Wait(menu, 0.3);
        var selectedBefore = RowState.Selected(other);
        var landed = false;
        void Landed(InputEvent e) => landed |= e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true };
        markHot.GuiInput += Landed;
        other.EmitSignal(Control.SignalName.MouseEntered);
        var clickAt = LeftOfMark();
        var inView = listView.GetGlobalRect().HasPoint(clickAt);
        var viewportAt = ViewportPoint(markHot, clickAt);
        foreach (var pressed in new[] { true, false })
            other.GetViewport().PushInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = pressed, Position = viewportAt, GlobalPosition = viewportAt }, true);
        await Wait(menu, 0.3);
        markHot.GuiInput -= Landed;
        other.EmitSignal(Control.SignalName.MouseExited);
        Check("a click just left of the notepad lands on its hover area and still selects the row", !selectedBefore && landed
            && RowState.Selected(other) && !RowState.Selected(ours) && CategoryList.Selected == other.Mod,
            $"at {clickAt} (in the list's view {inView}), selected before {selectedBefore}, landed on the area {landed}, "
            + $"selected after {RowState.Selected(other)} (ours {RowState.Selected(ours)})");
        Hover.TestMouse = null;

        // Where Hover puts a card it places itself (CardMisplaced: exactly there, over neither the trigger nor the mouse,
        // on screen), chosen each time a card opens from where its trigger is then: a probe with a tip, moved between
        // hovers. Mid-screen, below the trigger and right of the mouse; near the bottom, above the trigger instead of
        // pushed back up over it and the mouse; near the right edge, left of the mouse; in the bottom-right corner, both.
        var probe = new Control { Name = "SpireModManagerHoverProbe", Size = new Vector2(200, 40) };
        screen.AddChild(probe);
        Hover.Tip(probe, "Where a card opens");
        var view = probe.GetViewportRect().Size;
        async Task<string?> ProbeAt(Vector2 at, bool up, bool left)
        {
            probe.GlobalPosition = at;
            return CardMisplaced(await HoverCardBox(menu, probe, Hover.TipName), probe, up, left);
        }
        try
        {
            var middle = await ProbeAt(view / 2 - probe.Size / 2, up: false, left: false);
            Check("hover cards: mid-screen, a card opens below its trigger, right of the mouse", middle == null, middle ?? "");
            var bottom = await ProbeAt(new Vector2(100, view.Y - 50), up: true, left: false);
            Check("hover cards: near the bottom, a card opens above its trigger, not pushed up over it and the mouse", bottom == null, bottom ?? "");
            var right = await ProbeAt(new Vector2(view.X - 210, view.Y / 2 - 20), up: false, left: true);
            Check("hover cards: near the right edge, a card opens left of the mouse", right == null, right ?? "");
            var corner = await ProbeAt(view - new Vector2(210, 50), up: true, left: true);
            Check("hover cards: in the bottom-right corner, a card opens above its trigger and left of the mouse", corner == null, corner ?? "");
        }
        finally
        {
            probe.QueueFree();
        }
        // A row's card, on the lowest row in full view: below its title where it fits, otherwise above it.
        var list = content.GetParent<Control>().GetGlobalRect();
        if (content.GetChildren().OfType<NModMenuRow>().Where(r => r.IsVisibleInTree() && r.Mod != null && list.Encloses(r.GetGlobalRect()))
            .MaxBy(r => r.GetGlobalRect().Position.Y)?.GetNodeOrNull<Control>("SpireModManagerTitleHover") is { Visible: true } lowest)
        {
            var box = await HoverCardBox(menu, lowest, "SpireModManagerNotesCard");
            var (title, mouse) = (lowest.GetGlobalRect(), lowest.GetGlobalRect().GetCenter());
            var up = box is { } card && Math.Max(mouse.Y + 20, title.End.Y + Hover.Gap) + card.Size.Y > view.Y;
            var misplaced = CardMisplaced(box, lowest, up, left: false);
            Results.Add($"INFO the lowest row's card opened {(up ? "above" : "below")} its title");
            Check("a row's card near the bottom of the list: below its title where it fits, otherwise above it", misplaced == null, misplaced ?? "");
        }
        else
            Results.Add("SKIP a row's card near the bottom of the list: no row in full view");
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
        NewModPopupPatch.TestReset(); // TestSubscribeMissing's arrival had its popup, maybe within 30 s
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
        Check("the profile bar leaves the mod count to the profile list (none beside the name)", screen.FindChild("SpireModManagerProfileBar", true, false) is { } profileBar
            && profileBar.FindChild("ProfileCount", true, false) == null && Profiles.Active is { } listed && Profiles.Details(listed).Contains(Profiles.CountText()),
            Profiles.Active is { } a ? Profiles.Details(a) : "no profile");

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
        var countsBefore = HistoryBar.Counts();
        GameInstalls(fresh);
        await Wait(menu, 0.4);
        NModalContainer.Instance?.Clear();
        Check("a mod installed now is listed as installed, loading after relaunch",
            HistoryBar.Describe().Any(l => l.Contains("Installed: Fresh Mod") && l.Contains("pending relaunch")), string.Join(" | ", HistoryBar.Describe()));
        // It joins the active profile at once as off (the profile was saved before it existed), so its being on is the
        // profile's unsaved edit; Revert all's state has it, off, too. Turned on since launch: +1 on the first line.
        var countsNew = HistoryBar.Counts();
        Check("a mod installed now joins the active profile as off, so its being on is an unsaved edit; it counts as turned on (+1)",
            Profiles.Active?.Mods.Any(m => m.Id == "SmmFreshMod" && !m.Enabled) == true && Profiles.Edited
            && Profiles.UnsavedChanges().On.Any(m => m.Name == "Fresh Mod") && OnInSnapshot(History.Entered, "SmmFreshMod", enabled: false)
            && countsNew.On == countsBefore.On + 1 && countsNew.Off == countsBefore.Off,
            $"saved off {Profiles.Active?.Mods.Any(m => m.Id == "SmmFreshMod" && !m.Enabled)}, edited {wasEdited} -> {Profiles.Edited}, "
            + $"off in Revert all's state {OnInSnapshot(History.Entered, "SmmFreshMod", enabled: false)}, counts {countsBefore} -> {countsNew}");
        Check("a mod installed now gets a New tag, first", Tags.For(fresh).FirstOrDefault()?.Text == "New");
        var asMeant = HiddenTagsAsMeant(content);
        Check("a mod installed now: hidden tags on the list as it is (it #new, #changed and #pending), each finding the rows it means",
            asMeant.Wrong.Count == 0 && new[] { "#new", "#changed", "#pending" }.All(tag => Search.Matches(fresh, tag)),
            $"{string.Join(" | ", asMeant.Wrong)}; found {asMeant.Found}");
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
            var countsOff = HistoryBar.Counts();
            Check("unticking it matches the profile again (no edit), and the counts go back to before it came (no -1)",
                Profiles.Edited == wasEdited && !Profiles.UnsavedChanges().Off.Any(m => m.Name == "Fresh Mod") && countsOff == countsBefore,
                $"edited {wasEdited} -> {Profiles.Edited}, off: {string.Join(", ", Profiles.UnsavedChanges().Off.Select(m => m.Name))}, counts {countsBefore} -> {countsOff}");
            HistoryBar.ShowList(screen);
            await Wait(menu, 0.2);
            await Capture(screen, outDir, "10c-installed-change");
            HistoryBar.CloseList(screen.GetNode<Control>("%ModInfoContainer"));
            // Undone: before the untick the game had no entry for it (on), the profile's edit, +1 again.
            History.Undo();
            await Wait(menu, 0.3);
            Check("undoing the untick: the new mod on again, the profile's unsaved edit again, +1 again", SaveManager.Instance.SettingsSave.ModSettings?.IsModDisabled(fresh) == false
                && Profiles.Edited && HistoryBar.Counts() == countsNew,
                $"disabled {SaveManager.Instance.SettingsSave.ModSettings?.IsModDisabled(fresh)}, edited {wasEdited} -> {Profiles.Edited}, counts {HistoryBar.Counts()}");
            content.RemoveChild(freshRow);
            freshRow.QueueFree();
        }
        else
            Results.Add("FAIL a mod installed now gets a row");
        GameForgets(fresh);
        SaveManager.Instance.SettingsSave.ModSettings?.ModList.RemoveAll(e => e.Id == "SmmFreshMod");
        History.Quietly(data =>
        {
            foreach (var profile in data.Profiles)
            {
                profile.Mods.RemoveAll(m => m.Id == "SmmFreshMod");
                profile.Edited?.RemoveAll(m => m.Id == "SmmFreshMod");
            }
        });
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

    // A mod's description is searched too (Jordan: "contrast" didn't find Art Roller, whose description says it): its
    // words, the whole query without spaces, and quoted text; not as a tag. A mod with no description still searches.
    static void TestDescriptionSearch()
    {
        static Mod Made(string id, string? description, ModSource source = ModSource.ModsDirectory) => new()
        {
            path = "", modSource = source, state = ModLoadState.Loaded,
            manifest = new ModManifest { id = id, name = "Description Test", author = "Someone Careful", description = description },
        };
        // Words at a word's start (any start of one: "recolor"), with punctuation ("per-card"), quoted text as shown (its
        // markup out, [lb] and [rb] as brackets); not inside a word ("ui" in "quick", "olor"), not the markup ("gold") or a
        // picture's path ("banner"), not as a tag.
        string[] queries = ["contrast", "card art", "recolor", "\"art recoloring\"", "per-card", "ui", "gold", "olor", "#contrast", "-contrast", "zzz", "banner", "\"[v2]\""];
        var seen = new List<string>();
        foreach (var mod in new[] { Made("SmmDescTest", "Shared card art [gold]recoloring[/gold]: per-card hue, color, contrast; quick to build. [img]res://pics/banner.png[/img] [lb]v2[rb]"), Made("SmmDescNone", null) })
            seen.Add($"{mod.manifest?.id}: {string.Concat(queries.Select(q => Search.Matches(mod, q) ? "y" : "n"))}");
        // A script written without spaces: anywhere, words can't be told apart.
        var cjk = Made("SmmDescCjk", "卡牌对比度调整");
        seen.Add($"SmmDescCjk: {string.Concat(new[] { "对比", "调整", "卡牌对比度调整", "色" }.Select(q => Search.Matches(cjk, q) ? "y" : "n"))}");
        Check("search finds a mod's description by the starts of its words (not inside one, not its markup, not as a tag); "
            + "anywhere in a script without spaces; one with none still searches",
            seen.SequenceEqual(["SmmDescTest: yyyyynnnnnnny", "SmmDescNone: nnnnnnnnnynnn", "SmmDescCjk: yyyn"]), string.Join(" | ", seen));

        // A Workshop tag is a #tag (the panel's click on one searches that): the mods with it, not ones whose description
        // only says the word, which plain words still find.
        const ulong item = 4400;
        var tagged = Made("SmmDescTagged", null, ModSource.SteamWorkshop);
        Compat.SetWorkshopId(tagged, item);
        WorkshopInfo.TestSet(item, new WorkshopInfo.Details(DateTime.Now, DateTime.Now, ["Cards", "Tools & APIs", "Library"], [], 0, null));
        try
        {
            var says = Made("SmmDescSays", "Adds cards to every act.");
            var found = $"tagged {Search.Matches(tagged, "#cards")}/{Search.Matches(tagged, "#\"tools & apis\"")}/{Search.Matches(tagged, "cards")}/{Search.Matches(tagged, "#library")}, "
                + $"description only {Search.Matches(says, "#cards")}/{Search.Matches(says, "cards")}";
            Check("a Workshop tag is a #tag (its own mods, quoted for spaces), but not one named like an automatic chip (#library: "
                + "mods others need); a word finds it in tags and descriptions alike",
                found == "tagged True/True/True/False, description only False/True", found);
        }
        finally
        {
            WorkshopInfo.TestSet(item, null);
        }
    }

    // AND, OR, NOT and brackets on made-up mods (Workshop tags made up too): OR binds before the space ("#a OR #b #c" is
    // "(#a OR #b) #c"), "|" is OR, and NOT closer still; or/and/not in small letters are words, but operators between #tags,
    // @authors, quotes and groups; quotes are literal; a missing ")" closes at the end, an extra one is nothing, and so is an
    // operator with nothing on one side; in a bare #tag a ")" ends it only while a group is open.
    static void TestSearchGrammar()
    {
        static Mod Made(int i, string name, string author, string? description, List<string> tags)
        {
            var mod = new Mod
            {
                path = "", modSource = ModSource.SteamWorkshop, state = ModLoadState.Loaded,
                manifest = new ModManifest { id = $"SmmGrammarTest{i}", name = name, author = author, description = description },
            };
            Compat.SetWorkshopId(mod, 4500ul + (ulong)i);
            WorkshopInfo.TestSet(4500ul + (ulong)i, new WorkshopInfo.Details(DateTime.Now, DateTime.Now, tags, [], 0, null));
            return mod;
        }
        Mod[] mods = [Made(0, "Rock Stone", "Alice", "Heavy things.", ["Cards", "Two Kinds"]), Made(1, "Mod Fixes", "Bob", "Fight OR Flight, every act.", ["Relics"]),
            Made(2, "Stone Age", "Carl", null, ["Cards", "Relics"])];
        try
        {
            // Each query, and which of the three it finds.
            (string Query, string Finds)[] cases =
            [
                ("#cards OR #relics", "yyy"), ("#cards | #relics", "yyy"), ("#cards #relics", "nny"), ("#cards AND #relics", "nny"),
                ("#cards OR #relics @bob", "nyn"), ("(#cards OR #relics) @bob", "nyn"), ("#cards OR (#relics @bob)", "yyy"), ("@bob OR @alice #cards", "ynn"),
                ("#cards or #relics", "yyy"), ("#cards and #relics", "nny"), ("#cards not #relics", "ynn"), ("rock OR stone", "yny"), ("rock or stone", "nnn"),
                ("\"OR\"", "nyn"), ("\"or\"", "nnn"), ("#cards -(@alice OR @bob)", "nny"), ("-(#relics OR @carl)", "ynn"), ("NOT #relics", "ynn"),
                ("(@alice OR @bob", "yyn"), ("(#cards)) @carl", "nny"), ("#cards OR", "yny"), ("OR #relics", "nyy"), ("#cards AND", "yny"),
                ("#\"two kinds\" OR @carl", "yny"), ("(#cards @alice) OR (#relics @carl)", "yny"), ("stone -(rock)", "nny"), ("#relics)", "nnn"), ("(#relics)", "nyy"),
                ("#\"two kinds", "ynn"), ("@\"alice", "ynn"), ("#\"\" @bob", "nyn"), // a quote still to type runs to the end; an empty one is nothing
            ];
            var wrong = cases.Select(c => (c.Query, c.Finds, Found: string.Concat(mods.Select(m => Search.Matches(m, c.Query) ? "y" : "n"))))
                .Where(c => c.Found != c.Finds).Select(c => $"{c.Query}: {c.Found}, not {c.Finds}").ToList();
            Check($"search grammar: AND, OR, NOT and brackets, keywords in capitals or between terms that aren't words, forgiving ({cases.Length} queries)",
                wrong.Count == 0, string.Join(" | ", wrong));
            string[] empty = ["()", "OR", "NOT", "( OR )", "AND |", "-()"];
            Check("a search of brackets or operators alone is no search", !empty.Any(Search.HasTerms) && Search.HasTerms("#cards OR"),
                string.Join(", ", empty.Where(Search.HasTerms)));
        }
        finally
        {
            for (var i = 0; i < mods.Length; i++)
                WorkshopInfo.TestSet(4500ul + (ulong)i, null);
        }

        // The design's examples, row by row, then the rest of the grammar, on six more made-up mods in a made-up saved list:
        // A "RitsuLib" (Alice; Workshop, marked for stable only, its tag "Cards (Big)"; gameplay), B "Ritsu Cards" (Bob;
        // Workshop, beta only; the game failed it), C "Fight OR Flight" (Carl; a game error; D needs it), D "Stone Tools"
        // (Alice; off), E "Rock Kit" (Bob; needs F) and F "Spare Parts" (Carl; off). On: A, B, C and E. So B (failed), E
        // (needs a mod that's off) and F (that mod) are errors, C a warning; C and F are libraries.
        static Mod Designed(string letter, string name, string author, string? description, ModLoadState state, bool gameplay, params string[] needs) => new()
        {
            path = "", modSource = ModSource.ModsDirectory, state = state,
            manifest = new ModManifest
            {
                id = $"SmmDesign{letter}", name = name, author = author, description = description, affectsGameplay = gameplay,
                dependencies = [.. needs.Select(n => new ModDependency(n))],
            },
        };
        Mod[] designed = [Designed("A", "RitsuLib", "Alice", "Rock and stone.", ModLoadState.Loaded, true),
            Designed("B", "Ritsu Cards", "Bob", "Adds a lib of cards.", ModLoadState.Failed, false), Designed("C", "Fight OR Flight", "Carl", null, ModLoadState.Loaded, false),
            Designed("D", "Stone Tools", "Alice", null, ModLoadState.Disabled, false, "SmmDesignC"),
            Designed("E", "Rock Kit", "Bob", "Rock or stone, your pick.", ModLoadState.Loaded, false, "SmmDesignF"),
            Designed("F", "Spare Parts", "Carl", null, ModLoadState.Disabled, false)];
        (Mod Mod, ulong Item, List<(string, string)> Ranges, List<string> Tags)[] workshop =
            [(designed[0], 4700, [("public", "public")], ["Cards (Big)"]), (designed[1], 4701, [("public-beta", "")], [])];
        foreach (var (mod, item, ranges, tags) in workshop)
        {
            mod.modSource = ModSource.SteamWorkshop;
            Compat.SetWorkshopId(mod, item);
            WorkshopInfo.TestSet(item, new WorkshopInfo.Details(DateTime.Now, DateTime.Now, tags, ranges, 0, null));
        }
        designed[2].errors = [new LocString("main_menu_ui", "MOD_ERROR.DUPLICATE_ID")];
        List<SettingsSaveMod> saved = [.. designed.Select(m => new SettingsSaveMod(m) { IsEnabled = m.manifest?.id is not ("SmmDesignD" or "SmmDesignF") })];
        try
        {
            // Each query, and which of the six it finds.
            (string Query, string Finds)[] table =
            [
                ("ritsu lib", "yynnnn"), ("#beta OR #stable", "yyyyyy"), ("#beta | #stable", "yyyyyy"), ("(#warning OR #error) #enabled", "nyynyn"),
                ("#warning OR #error #enabled", "nyynyn"), ("#warning or #error", "nyynyy"), ("rock or stone", "nnnnyn"), ("\"OR\"", "nnynnn"),
                ("#enabled -(#gameplay OR #library)", "nynnyn"), ("(@Alice OR @Bob", "yynyyn"),
            ];
            // What the examples stand beside: each tag alone and both at once, the two without brackets, an AND of words.
            (string Query, string Finds)[] besides =
            [
                ("#beta", "nyyyyy"), ("#stable", "ynyyyy"), ("#beta #stable", "nnyyyy"), ("#warning", "nnynnn"), ("#error", "nynnyy"),
                ("#warning OR #error", "nyynyy"), ("#enabled", "yyynyn"), ("rock OR stone", "ynnyyn"), ("ritsu", "yynnnn"),
            ];
            // Precedence: NOT before OR before AND; brackets first.
            (string Query, string Finds)[] precedence =
            [
                ("-@alice OR @bob", "nyynyy"), ("NOT @alice OR @bob", "nyynyy"), ("-(@alice OR @bob)", "nnynny"), ("@alice OR @bob #enabled", "yynnyn"),
                ("@alice OR (@bob #enabled)", "yynyyn"), ("@carl @alice OR @bob", "nnnnnn"), ("@carl (@alice OR @bob)", "nnnnnn"), ("(@carl @alice) OR @bob", "nynnyn"),
            ];
            // Keywords and words: capitals anywhere; small letters between two terms that aren't words (#tags, @authors, quotes,
            // groups), and words beside a word ("rock and stone" is three); quotes literal, operators and all.
            (string Query, string Finds)[] keywords =
            [
                ("rock and stone", "ynnnnn"), ("rock AND stone", "ynnnyn"), ("@alice or @bob", "yynyyn"), ("\"Kit\" or \"Fight\"", "nnynyn"),
                ("(@carl) or (@bob)", "nyynyy"), ("#enabled not #library", "yynnyn"), ("#enabled and #gameplay", "ynnnnn"), ("rock or @carl", "nnnnnn"),
                ("rock OR @carl", "ynynyy"), ("\"or\"", "nnnnyn"), ("\"Fight OR Flight\"", "nnynnn"), ("\"#enabled\"", "nnnnnn"), ("\"(rock)\"", "nnnnnn"),
            ];
            // Forgiving: a missing ")" closes at the end, an extra one is nothing, "()" is nothing, an operator with nothing on a
            // side is dropped, a "-" or NOT before nothing is nothing; a bare #tag ends at a ")" only while a group is open, and
            // a quoted one keeps its brackets.
            (string Query, string Finds)[] forgiving =
            [
                ("((@alice OR @bob) -#library", "yynyyn"), ("(@alice) ) OR @bob", "yynyyn"), ("(@alice)) OR @bob", "yynyyn"), ("() @alice", "ynnynn"),
                ("@alice OR ()", "ynnynn"), ("@alice OR", "ynnynn"), ("OR @alice", "ynnynn"), ("@alice AND", "ynnynn"), ("AND @alice", "ynnynn"),
                ("@alice |", "ynnynn"), ("| @alice", "ynnynn"), ("@alice OR OR @bob", "yynyyn"), ("@alice -", "ynnynn"), ("- @alice", "ynnynn"),
                ("@alice NOT", "ynnynn"), ("NOT NOT @alice", "ynnynn"), ("(#library)", "nnynny"), ("(#library", "nnynny"), ("#library)", "nnnnnn"),
                ("(#library) @carl", "nnynny"), ("#\"cards (big)\"", "ynnnnn"), ("(#\"cards (big)\" OR @carl)", "ynynny"),
            ];
            List<string> Wrong((string Query, string Finds)[] cases) => [.. cases
                .Select(c => (c.Query, c.Finds, Found: string.Concat(designed.Select(m => Search.Matches(m, c.Query) ? "y" : "n"))))
                .Where(c => c.Found != c.Finds).Select(c => $"{c.Query}: {c.Found}, not {c.Finds}")];
            List<string> wrongTable, wrongBesides, wrongPrecedence, wrongKeywords, wrongForgiving;
            using (Lookups.Begin(designed, [], saved, Picks.None))
                (wrongTable, wrongBesides, wrongPrecedence, wrongKeywords, wrongForgiving) = (Wrong(table), Wrong(besides), Wrong(precedence), Wrong(keywords), Wrong(forgiving));
            Check($"search grammar: the design's examples, row by row ({table.Length}), and the tags they're made of alone", wrongTable.Count + wrongBesides.Count == 0,
                string.Join(" | ", wrongTable.Concat(wrongBesides)));
            Check($"search grammar: brackets first, then NOT, then OR, then AND ({precedence.Length} queries)", wrongPrecedence.Count == 0, string.Join(" | ", wrongPrecedence));
            Check($"search grammar: OR, AND and NOT in capitals anywhere, in small letters only between terms that aren't words; quotes literal ({keywords.Length} queries)",
                wrongKeywords.Count == 0, string.Join(" | ", wrongKeywords));
            Check($"search grammar: never an error: a missing or extra \")\", \"()\", a dangling operator, a lone - or NOT, a bracket in a #tag ({forgiving.Length} queries)",
                wrongForgiving.Count == 0, string.Join(" | ", wrongForgiving));
            string[] nothing = ["()", "( )", "OR", "AND", "NOT", "-", "|", "( OR )", "AND |", "-()", "NOT ()", "(((", ")))", "\"", "\"\"", "#\"", "#\"\"", "@\""];
            Check("brackets, operators, a lone - or an empty quote alone: no search, every mod shown",
                !nothing.Any(Search.HasTerms) && nothing.All(q => designed.All(m => Search.Matches(m, q))), string.Join(", ", nothing.Where(Search.HasTerms)));
        }
        finally
        {
            foreach (var (_, item, _, _) in workshop)
                WorkshopInfo.TestSet(item, null);
        }
    }

    // Hidden tags on the fixture's list: each finds inside a redraw (Lookups) what it finds worked out on its own; #enabled
    // the ticked rows and #disabled the others, any case; #installed every row, #missing and #uninstalled none; each the
    // rows it means (HiddenTagsAsMeant); a tag of the player's named like each finds its mod too. Then what each means, on
    // made-up mods in a made-up saved list: A needs C (off) and D (after A), B needs a mod that isn't installed and the
    // game failed it, D has a game error, E is off but loaded, with a tag of its own named "Enabled".
    // Every hidden tag, in HiddenTags' order (the info panel's Other keywords lists them in it).
    static readonly string[] HiddenTagNames = ["#enabled", "#disabled", "#workshop", "#local", "#installed", "#uninstalled", "#missing", "#loaded", "#notloaded",
        "#pending", "#new", "#changed", "#moved", "#edited", "#renamed", "#notes", "#uncategorized", "#unmet", "#needed", "#order", "#sameid", "#clash", "#aside",
        "#managers", "#failed", "#gameerror", "#error", "#warning", "#problem"];

    static void TestHiddenTags(Control content)
    {
        var rows = content.GetChildren().OfType<NModMenuRow>().Select(r => r.Mod).OfType<Mod>().ToList();
        var tags = HiddenTagNames;
        List<string> Answers() => [.. tags.Select(t => $"{t} {string.Concat(rows.Select(m => Search.Matches(m, t) ? "y" : "n"))}")];
        var fresh = Answers();
        List<string> kept;
        using (Lookups.Begin())
            kept = Answers();
        var differ = fresh.Zip(kept).Where(p => p.First != p.Second).Select(p => $"{p.First} -> {p.Second}").ToList();
        Check("hidden tags: inside a redraw, the same mods as worked out one at a time", differ.Count == 0 && fresh.Count == kept.Count, string.Join(" | ", differ));
        bool Finds(string query, Func<Mod, bool> expected) => rows.All(m => Search.Matches(m, query) == expected(m));
        Check("#enabled finds the ticked rows and #Disabled the others; #installed every row, #missing and #UNINSTALLED none",
            rows.Any(Copies.Ticked) && rows.Any(m => !Copies.Ticked(m)) && Finds("#enabled", Copies.Ticked) && Finds("#Disabled", m => !Copies.Ticked(m))
            && Finds("#installed", _ => true) && Finds("#missing", _ => false) && Finds("#UNINSTALLED", _ => false));
        // Each of them on the fixture's rows, by what it means (HiddenTagsAsMeant), and the ones the fixture holds by name:
        // Test Addon needs a mod that isn't installed, the twins share an id, this mod has its alias and note, Test Retired
        // is off, every row is local.
        var asMeant = HiddenTagsAsMeant(content);
        bool Has(string tag, params string[] ids) => ids.All(id => rows.Any(m => m.manifest?.id == id && Search.Matches(m, tag)));
        var twins = rows.Where(m => m.manifest?.id == TwinId).ToList();
        Check("hidden tags on the fixture: each finds the rows it means (the box, chips, status, the banners' rules, changes since launch); Test Addon #unmet, "
            + "the twins #sameid, this mod #renamed and #notes, Test Retired #disabled and #notloaded, every row #local",
            asMeant.Wrong.Count == 0 && (WontLoad() == null || Has("#unmet", "SmmTestAddon")) && twins.Count == 2 && twins.All(m => Search.Matches(m, "#sameid"))
            && Has("#renamed", ModEntry.ModId) && Has("#notes", ModEntry.ModId) && Has("#disabled", "SmmTestRetired") && Has("#notloaded", "SmmTestRetired")
            && Finds("#local", _ => true), $"{string.Join(" | ", asMeant.Wrong)}; found {asMeant.Found}");
        Results.Add($"INFO hidden tags on the fixture, rows found: {asMeant.Found}");

        // A tag of the player's named like each of them (Test Retired given them all): each finds it too, those it isn't
        // (#enabled, #unmet, #missing...) as well, and so does the bare word, as for any tag. Its own tags back after.
        if (rows.FirstOrDefault(m => m.manifest?.id == "SmmTestRetired") is { } retired)
        {
            var tagsWere = Store.InfoFor(retired)?.Tags;
            var names = tags.Select(t => t[1..]).ToList();
            var hiddenOnly = names.Count(n => !Search.Matches(retired, "#" + n));
            Store.EditMod("SmmTestRetired", info => info.Tags = [.. names.Select(n => char.ToUpperInvariant(n[0]) + n[1..])]);
            try
            {
                var missed = names.Where(n => !Search.Matches(retired, "#" + n) || !Search.Matches(retired, n)).ToList();
                Check($"a tag of the player's named like a hidden one finds its mod too, for each of them ({hiddenOnly} of {names.Count} it isn't otherwise), "
                    + "#tag and bare word alike", hiddenOnly > names.Count / 2 && missed.Count == 0, string.Join(", ", missed));
            }
            finally
            {
                Store.EditMod("SmmTestRetired", info => info.Tags = tagsWere);
            }
        }
        else
            Results.Add("SKIP hidden tags beside a player's tags: Test Retired not installed");

        static Mod Made(string letter, ModLoadState state, ModSource source, params string[] needs) => new()
        {
            path = "", modSource = source, state = state,
            manifest = new ModManifest { id = $"SmmHidden{letter}", name = $"Hidden Test {letter}", dependencies = [.. needs.Select(n => new ModDependency(n))] },
        };
        Mod[] made = [Made("A", ModLoadState.Loaded, ModSource.ModsDirectory, "SmmHiddenC", "SmmHiddenD"),
            Made("B", ModLoadState.Failed, ModSource.SteamWorkshop, "SmmHiddenMissing"), Made("C", ModLoadState.Disabled, ModSource.ModsDirectory),
            Made("D", ModLoadState.Loaded, ModSource.ModsDirectory), Made("E", ModLoadState.Loaded, ModSource.ModsDirectory)];
        Compat.SetWorkshopId(made[1], 4600);
        made[3].errors = [new LocString("main_menu_ui", "MOD_ERROR.DUPLICATE_ID")];
        List<SettingsSaveMod> saved = [.. made.Select(m => new SettingsSaveMod(m) { IsEnabled = m.manifest?.id is not ("SmmHiddenC" or "SmmHiddenE") })];
        // A renamed, B with notes, C in a category the list has, D in one it doesn't: our data, until the end.
        var category = Store.Data.Categories.FirstOrDefault()?.Name ?? "";
        Store.Batch(() =>
        {
            Store.EditMod("SmmHiddenA", info => info.Alias = "Hidden Rock");
            Store.EditMod("SmmHiddenB", info => info.Notes = "A note");
            Store.EditMod("SmmHiddenC", info => info.Category = category);
            Store.EditMod("SmmHiddenD", info => info.Category = "SmmNoSuchCategory");
            Store.EditMod("SmmHiddenE", info => info.Tags = ["Enabled"]);
        });
        try
        {
            (string Query, string Finds)[] cases =
            [
                ("#enabled", "yynyy"), ("#disabled", "nnyny"), ("enabled", "nnnny"), ("#workshop", "nynnn"), ("#local", "ynyyy"), ("#installed", "yyyyy"),
                ("#missing", "nnnnn"), ("#loaded", "ynnyy"), ("#notloaded", "nyynn"), ("#pending", "nnnny"), ("#unmet", "yynnn"), ("#needed", "nnynn"),
                ("#order", "ynnnn"), ("#failed", "nynnn"), ("#gameerror", "nnnyn"), ("#error", "yyynn"), ("#warning", "nnnyn"), ("#problem", "yyyyn"),
                ("#renamed", "ynnnn"), ("#notes", "nynnn"), ("#uncategorized", "yynyy"), ("#edited", "yyyyy"), ("#sameid OR #clash OR #aside OR #managers", "nnnnn"),
                ("#enabled -#local", "nynnn"), ("#problem #local", "ynyyn"), ("(#unmet OR #needed) -#failed", "ynynn"),
            ];
            List<string> wrong;
            using (Lookups.Begin(made, [], saved, Picks.None))
                wrong = [.. cases.Select(c => (c.Query, c.Finds, Found: string.Concat(made.Select(m => Search.Matches(m, c.Query) ? "y" : "n"))))
                    .Where(c => c.Found != c.Finds).Select(c => $"{c.Query}: {c.Found}, not {c.Finds}")];
            Check("hidden tags mean what they say: state, problems (#error, #warning, #problem gather them), our details; only after #; "
                + "a tag of the player's named like one finds its mods too", wrong.Count == 0, string.Join(" | ", wrong));
        }
        finally
        {
            Store.Batch(() =>
            {
                foreach (var m in made)
                    Store.EditMod(m.manifest?.id ?? "", info => (info.Alias, info.Notes, info.Category, info.Tags) = (null, null, null, null));
            });
        }
    }

    // Each hidden tag against the list's rows, by what it means told from what the page shows or from where the search's
    // decisions say it comes from, not by HiddenTags' own rules: the row's box (#enabled), its chips (#local, #new,
    // #sameid), its category as the list files it; a dependency missing or off by Dependencies.Check (#unmet), NeededOff,
    // OrderViolations, Copies.Clashing and Loses, the managers both ticked, what changed since launch (ChangedMods, Moves,
    // the edited keys). Wrong: each tag that finds other rows than it means, with them; Found: how many each finds.
    static (List<string> Wrong, string Found) HiddenTagsAsMeant(Control content)
    {
        var rows = content.GetChildren().OfType<NModMenuRow>().Where(r => r.Mod != null).ToList();
        var mods = rows.Select(r => r.Mod).OfType<Mod>().ToList();
        var ticked = rows.Where(r => r.GetNodeOrNull<NTickbox>("Tickbox")?.IsTicked == true).Select(r => r.Mod).OfType<Mod>().ToHashSet();
        bool Chip(Mod mod, string key) => Tags.For(mod).Any(c => !c.Custom && c.Text == Menus.Text(key));
        var order = new Dictionary<Mod, int>(); // missing or off: the order doesn't matter
        bool Unmet(Mod mod) => Copies.Loads(mod)
            && Dependencies.Of(mod).Any(id => Dependencies.Check(mod, id, order) is Dependencies.Problem.Missing or Dependencies.Problem.Off);
        var neededOff = Dependencies.NeededOff(mods).ToHashSet();
        bool Needed(Mod mod) => !Copies.Loads(mod) && neededOff.Contains(mod);
        var misordered = Dependencies.OrderViolations(LoadOrder.Mods(rows)).Select(v => v.Mod).ToHashSet();
        bool Misordered(Mod mod) => Copies.Loads(mod) && misordered.Contains(mod);
        var clashing = Copies.Clashing().ToHashSet();
        var managers = Bmm.BothTicked ? Bmm.TickedManagers().ToHashSet() : [];
        var (changed, moved, edited) = (HistoryBar.ChangedMods(), HistoryBar.Moves().Select(m => m.Mod).ToHashSet(), HistoryBar.EditedKeys());
        bool Failed(Mod mod) => ticked.Contains(mod) && mod.state == ModLoadState.Failed;
        bool GameError(Mod mod) => ticked.Contains(mod) && mod.errors is { Count: > 0 };
        bool SelfOff(Mod mod) => mod.manifest?.id == ModEntry.ModId && !mods.Any(m => m.manifest?.id == ModEntry.ModId && ticked.Contains(m));
        bool Error(Mod mod) => Unmet(mod) || Needed(mod) || Misordered(mod) || Failed(mod) || SelfOff(mod);
        bool Warning(Mod mod) => clashing.Contains(mod) || managers.Contains(mod) || GameError(mod);
        (string Tag, Func<Mod, bool> Means)[] meant =
        [
            ("enabled", ticked.Contains), ("disabled", m => !ticked.Contains(m)), ("workshop", m => m.modSource == ModSource.SteamWorkshop),
            ("local", m => Chip(m, "SPIRE_MOD_MANAGER.TAG_LOCAL")), ("installed", _ => true), ("uninstalled", _ => false), ("missing", _ => false),
            ("loaded", m => m.state == ModLoadState.Loaded), ("notloaded", m => m.state != ModLoadState.Loaded),
            ("pending", m => ModDetails.Status(m).Contains(Menus.Text("SPIRE_MOD_MANAGER.STATUS_PENDING"))), ("new", m => Chip(m, "SPIRE_MOD_MANAGER.TAG_NEW")),
            ("changed", changed.Contains), ("moved", moved.Contains), ("edited", m => edited.Contains(Copies.Key(m))),
            ("renamed", m => Store.InfoFor(m)?.Alias is { Length: > 0 }), ("notes", m => !string.IsNullOrWhiteSpace(Store.InfoFor(m)?.Notes)),
            ("uncategorized", m => CategoryList.CategoryName(Copies.KeyOf(m)) == Menus.Text("SPIRE_MOD_MANAGER.UNCATEGORIZED")),
            ("unmet", Unmet), ("needed", Needed), ("order", Misordered), ("sameid", m => Chip(m, "SPIRE_MOD_MANAGER.TAG_SAME_ID")),
            ("clash", clashing.Contains), ("aside", Copies.Loses), ("managers", managers.Contains), ("failed", Failed), ("gameerror", GameError),
            ("error", Error), ("warning", Warning), ("problem", m => Error(m) || Warning(m)),
        ];
        static string Key(Mod mod) => $"{mod.manifest?.id}/{mod.modSource}";
        var wrong = meant.Select(t => (t.Tag, Found: mods.Where(m => Search.Matches(m, "#" + t.Tag)).ToList(), Meant: mods.Where(t.Means).ToList()))
            .Where(t => !t.Found.SequenceEqual(t.Meant))
            .Select(t => $"#{t.Tag} finds {string.Join(",", t.Found.Select(Key))}, means {string.Join(",", t.Meant.Select(Key))}").ToList();
        return (wrong, string.Join(" ", meant.Select(t => $"#{t.Tag} {mods.Count(m => Search.Matches(m, "#" + t.Tag))}")));
    }

    // Stable and Beta, by the game's own branch rule over a Workshop mod's supported range (made-up mods and details):
    // a chip only on a mod marked for one branch, whose click searches for that ("#Stable -#Beta"); #stable and #beta
    // (and the plain words) find every mod marked for a branch, "#stable -#beta" the ones for stable only. No range, or a
    // local mod: both, no chip.
    static void TestBranchTags()
    {
        string[] queries = ["#stable", "#beta", "#stable -#beta", "stable", "stable -beta", "table"];
        (string Name, List<(string, string)> Ranges)[] cases =
            [("stable only", [("public", "public")]), ("beta and up", [("public-beta", "")]), ("both", [("public", "public-beta")]), ("no range", [])];
        var seen = new List<string>();
        for (var i = 0; i < cases.Length; i++)
        {
            var mod = new Mod
            {
                path = "", modSource = ModSource.SteamWorkshop, state = ModLoadState.Loaded,
                manifest = new ModManifest { id = $"SmmBranchTest{i}", name = $"Branch Test {i}", author = "Someone Careful" },
            };
            var id = 4300ul + (ulong)i;
            Compat.SetWorkshopId(mod, id);
            WorkshopInfo.TestSet(id, new WorkshopInfo.Details(DateTime.Now, DateTime.Now, [], cases[i].Ranges, 0, null));
            try
            {
                var chips = string.Join("/", Tags.For(mod).Where(c => c.Text is "Stable" or "Beta").Select(c => $"{c.Text} ({c.Search})"));
                var found = string.Concat(queries.Select(q => Search.Matches(mod, q) ? "y" : "n"));
                seen.Add($"{cases[i].Name}: [{chips}] {found}");
            }
            finally
            {
                WorkshopInfo.TestSet(id, null);
            }
        }
        var local = Dependencies.Find("SmmTestFixes");
        seen.Add($"local: [{string.Join("/", Tags.For(local).Where(c => c.Text is "Stable" or "Beta").Select(c => c.Text))}] "
            + string.Concat(queries.Select(q => Search.Matches(local, q) ? "y" : "n")));
        Check("Stable and Beta by the game's branch rule: a chip on one-branch mods that searches for it; #stable, #beta, #stable -#beta "
            + "and the plain words find them; \"table\" only through a chip's text (as any chip)",
            seen.SequenceEqual(["stable only: [Stable (#Stable -#Beta)] ynyyyy", "beta and up: [Beta (#Beta -#Stable)] nynnnn",
                "both: [] yynynn", "no range: [] yynynn", "local: [] yynynn"]),
            string.Join(" | ", seen));
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
        // The alias's exact text: its words alone are in other mods' descriptions too ("the … mod manager").
        var byAlias = await Shown("\"The Manager\"");
        Check("search by alias", byAlias.SequenceEqual([ModEntry.ModId]), string.Join(", ", byAlias));
        var byTag = await Shown("favorites");
        Check("search by own tag", byTag.SequenceEqual(["SmmTestFixes"]), string.Join(", ", byTag));
        var libraries = await Shown("library");
        // The libraries: Test Framework, and both copies of the fixture's twin (Test Twin User needs it).
        List<string> Libraries() => ["SmmTestLib", .. ModManager.Mods.Where(m => m.manifest?.id == TwinId).Select(_ => TwinId)];
        Check("search by automatic tag", libraries.Order().SequenceEqual(Libraries().Order()), string.Join(", ", libraries));
        Check("automatic tags, in order", Tags.For(Dependencies.Find("SmmTestLib")).Select(c => c.Text).SequenceEqual(["Local", "Library", "Gameplay"])
            && Tags.For(Dependencies.Find("SmmTestUi")).Select(c => c.Text).SequenceEqual(["Local"]),
            string.Join(", ", Tags.For(Dependencies.Find("SmmTestLib")).Select(c => c.Text)));
        Check("Cosmetic: the Workshop's Cosmetics and Audio categories and art or sound words; not QoL, tools or UI",
            Tags.IsCosmetic(["Cosmetics"]) && Tags.IsCosmetic(["Characters", "Audio"]) && Tags.IsCosmetic(["Sound Effects"]) && Tags.IsCosmetic(["Card Art"])
            && !Tags.IsCosmetic(["QoL", "Tools & APIs", "Utility"]) && !Tags.IsCosmetic(["UI"]) && !Tags.IsCosmetic([]) && !Tags.IsCosmetic(null));
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
        Check("clicking a row's tag searches for it", box.Text == "#Library" && Visible().Order().SequenceEqual(Libraries().Order()), $"{box.Text}: {string.Join(", ", Visible())}");
        ClickChip("SmmTestLib", "Gameplay", true);
        await Wait(menu, 0.3);
        Check("Shift-clicking another adds it", box.Text == "#Library #Gameplay" && Visible().Order().SequenceEqual(Libraries().Order()), $"{box.Text}: {string.Join(", ", Visible())}");
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
        Check("the panel's author is a link in the mod names' light blue", panelHead?.Text
            .Contains($"[url=author:symphony][color={ModDetails.ModLinkHex}]symphony[/color][/url]") == true, panelHead?.Text ?? "no head");
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
        string[] clears = [After(" #library ", () => Tag("Library", false)), After("#\"Library\"", () => Tag("Library", false)),
            After("#\"tools & apis\"", () => Tag("Tools & APIs", false))];
        Check("click-to-search: a click on the whole search clears it (any case, quotes aside)", clears.All(s => s == ""), string.Join(" | ", clears));
        string[] replaces = [After("mod fixes", () => Tag("Library", false)), After("#Library #Gameplay", () => Tag("Library", false))];
        Check("click-to-search: a click replaces any other search", replaces.All(s => s == "#Library"), string.Join(" | ", replaces));
        string[] adds = [After("@symphony", () => Tag("Local", true)), After("mod", () => Tag("Tools & APIs", true))];
        Check("click-to-search: Shift-click adds the term, keeping the rest", adds.SequenceEqual(["@symphony #Local", "mod #\"Tools & APIs\""]), string.Join(" | ", adds));
        string[] removes = [After("@symphony #local mod", () => Tag("Local", true)), After("#Library #\"Tools & APIs\"", () => Tag("tools & apis", true))];
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
        // A "-" before a word, #tag, @author or "exact text" leaves out the mods it matches; alone, every other mod shows.
        var notWord = await Shown("test -fixes");
        Check("a -word leaves out the mods with it", notWord.Contains("SmmTestContent") && !notWord.Contains("SmmTestFixes"), string.Join(", ", notWord));
        var notAuthor = await Shown("#local -@symphony");
        Check("-@author leaves out that author's mods", notAuthor.Count > 0 && !notAuthor.Contains("SmmTestFixes")
            && notAuthor.All(id => Dependencies.Find(id)?.manifest?.author != "symphony"), string.Join(", ", notAuthor));
        var notTag = await Shown("-#library");
        Check("-#tag alone: every mod but the tagged ones", !notTag.Contains("SmmTestLib") && notTag.Contains("SmmTestFixes"), string.Join(", ", notTag));
        // Punctuation and symbols count as spaces, in the search and in a mod's text; "exact text" keeps them, and case.
        var spaced = await Shown("test-fixes!");
        Check("punctuation in a search counts as spaces: test-fixes! finds Test Fixes", spaced.SequenceEqual(["SmmTestFixes"]), string.Join(", ", spaced));
        var aliasBefore = Store.InfoFor("SmmTestContent")?.Alias;
        Store.EditMod("SmmTestContent", info => info.Alias = "Don't Panic");
        try
        {
            var apostrophe = await Shown("dont panic");
            Check("punctuation in a mod's text is skipped: dont panic finds Don't Panic", apostrophe.SequenceEqual(["SmmTestContent"]), string.Join(", ", apostrophe));
            var exact = await Shown("\"Don't\"");
            var otherCase = await Shown("\"don't\"");
            var noApostrophe = await Shown("\"Dont\"");
            Check("\"exact text\" matches as typed: its symbols and case", exact.SequenceEqual(["SmmTestContent"]) && otherCase.Count == 0 && noApostrophe.Count == 0,
                $"{string.Join(", ", exact)} | {string.Join(", ", otherCase)} | {string.Join(", ", noApostrophe)}");
            var notExact = await Shown("test -\"Don't\"");
            Check("-\"exact text\" leaves out the mods with it", !notExact.Contains("SmmTestContent") && notExact.Contains("SmmTestFixes"), string.Join(", ", notExact));
            // A word's pieces sit together: "don't-panic" finds Don't Panic; "test-panic" isn't one, though "test panic"
            // (two words, each anywhere) finds Test Content; "-don't" leaves out only that one.
            var together = await Shown("don't-panic");
            var apart = await Shown("test-panic");
            var twoWords = await Shown("test panic");
            var notDont = await Shown("-don't");
            Check("a punctuated word's pieces sit together, and the same for a -word",
                together.SequenceEqual(["SmmTestContent"]) && apart.Count == 0 && twoWords.SequenceEqual(["SmmTestContent"])
                && !notDont.Contains("SmmTestContent") && notDont.Contains("SmmTestFixes"),
                $"{string.Join(", ", together)} | {string.Join(", ", apart)} | {string.Join(", ", twoWords)} | {string.Join(", ", notDont)}");
        }
        finally
        {
            Store.EditMod("SmmTestContent", info => info.Alias = aliasBefore);
        }
        // Only the punctuation of sentences is read past (Jordan); other symbols are part of a word: "C#" isn't "c".
        Store.EditMod("SmmTestContent", info => info.Alias = "C# & C++ Kit!");
        try
        {
            var symbols = string.Join(" | ", new[] { "c#", "C++", "&", "kit", "c# kit" }.Select(q => string.Join(",", content.GetChildren().OfType<NModMenuRow>().Where(r => r.Mod != null
                && SpireModManager.Search.Matches(r.Mod, q)).Select(r => r.Mod?.manifest?.id).Distinct().Order())));
            var plainC = content.GetChildren().OfType<NModMenuRow>().Count(r => r.Mod != null && SpireModManager.Search.Matches(r.Mod, "c"));
            Check("symbols are part of a word (C#, C++, &), sentence punctuation isn't (Kit!); a lone letter still finds more",
                symbols == "SmmTestContent | SmmTestContent | SmmTestContent | SmmTestContent | SmmTestContent" && plainC > 1, $"{symbols}; \"c\" finds {plainC}");
        }
        finally
        {
            Store.EditMod("SmmTestContent", info => info.Alias = aliasBefore);
        }
        var nothing = await Shown("!!!");
        Check("a search with no terms (\"!!!\", a \"-\" alone) is no search: every row", nothing.Count == (await Shown("")).Count
            && (await Shown("-")).Count == nothing.Count, $"{nothing.Count}");
        string[] whole = [After("-#Library mod", () => Tag("Gameplay", true)), After("\"two words\" #mod", () => Tag("mod", true))];
        Check("click-to-search: a -#tag and \"exact text\" stay whole terms", whole.SequenceEqual(["-#Library mod #Gameplay", "\"two words\""]),
            string.Join(" | ", whole));
        string[] flipped = [After("-#Gameplay mod", () => Tag("Gameplay", true)), After("\"Don't", () => Tag("Local", true)),
            After("", () => SpireModManager.Search.ByChip(content, new Tags.Chip("Stable", Colors.White, Search: "#Stable -#Beta"), false))];
        Check("click-to-search: Shift on a left-out tag puts it in, an open quote is closed first, a branch chip searches for what it means",
            flipped.SequenceEqual(["mod #Gameplay", "\"Don't\" #Local", "#Stable -#Beta"]), string.Join(" | ", flipped));
        // Brackets, OR, "|" and AND are pieces of their own: a term taken out takes the OR (else AND) that joined it, so the
        // rest means what it did, and brackets left empty go; one added after an OR still being typed completes it.
        string[] operators = [After("#a OR #b #c", () => Tag("b", true)), After("(#a OR #b) #c", () => Tag("a", true)),
            After("#a AND #b OR #c", () => Tag("b", true)), After("(#a) #c", () => Tag("a", true)), After("#a | #b", () => Tag("a", true)),
            After("#a or #b", () => Tag("b", true)), After("#a OR", () => Tag("b", true)), After("-(#a OR #b) #c", () => Tag("c", true)),
            After("NOT #a", () => Tag("a", true)), After("#x", () => Tag("x (y)", true))];
        Check("click-to-search: brackets, OR, \"|\" and AND stay; a term taken out takes its OR (else AND) and empty brackets with it",
            operators.SequenceEqual(["#a #c", "(#b) #c", "#a AND #c", "#c", "#b", "#a", "#a OR #b", "-(#a OR #b)", "#a", "#x #\"x (y)\""]),
            string.Join(" | ", operators));
        // Operators written back in capitals (lowercase ones, operators only between two tags, could be left beside a word
        // and read as one); an emptied group taking the OR or AND that joined it, as a term does.
        string[] written = [After("#Gameplay not #Library", () => Tag("Gameplay", true)), After("#a or not #b", () => Tag("a", true)),
            After("foo (#a) or #b", () => Tag("a", true)), After("#c OR (#b) #a", () => Tag("b", true)), After("#c (#b) OR #a", () => Tag("b", true)),
            After("#a AND (#b) OR #c", () => Tag("b", true)), After("#a or #b", () => Tag("c", true))];
        Check("click-to-search: operators come back in capitals (a lowercase one could turn into a word); an emptied group goes with what joined it",
            written.SequenceEqual(["NOT #Library", "NOT #b", "foo #b", "#c #a", "#c #a", "#a AND #c", "#a OR #b #c"]), string.Join(" | ", written));
        // More of it: a term in brackets beside an OR, in an OR of three, an author; and through a row's chips, Shift taking
        // one out of an OR (in brackets too), a plain click searching for it alone.
        string[] grouped = [After("(#a) OR #b", () => Tag("a", true)), After("#a OR (#b)", () => Tag("b", true)), After("#a OR (#b OR #c)", () => Tag("b", true)),
            After("#a (#b | #c)", () => Tag("c", true)), After("#a OR #b OR #c", () => Tag("b", true)), After("#a AND (#b)", () => Tag("b", true)),
            After("@a OR @b", () => SpireModManager.Search.ByAuthor(content, "a", true)), After("#Library OR #Local", () => ClickChip("SmmTestLib", "Library", true)),
            After("(#Library OR #Gameplay) @symphony", () => ClickChip("SmmTestLib", "Gameplay", true)), After("#Library | #Local", () => ClickChip("SmmTestLib", "Local", false))];
        Check("click-to-search: a term taken out of brackets or an OR leaves the rest as it was, through a row's chips too; a plain click searches for it alone",
            grouped.SequenceEqual(["#b", "#a", "#a OR (#c)", "#a (#b)", "#a OR #c", "#a", "@b", "#Local", "(#Library) @symphony", "#Local"]), string.Join(" | ", grouped));
        // A bare #tag ends at a quote or a "(", which start the next term (a group, "exact text"), as a word does.
        var framework = ModEntry.CopyOf("SmmTestLib");
        Check("search: a bare #tag ends at a quote or a \"(\" (#Library\"Test Framework\", #Library(Framework) find Test Framework)",
            framework != null && SpireModManager.Search.Matches(framework, "#Library\"Test Framework\"") && SpireModManager.Search.Matches(framework, "#Library(Framework)")
            && !SpireModManager.Search.Matches(framework, "#Library\"no such words\""));
        // The box takes 200 characters (60 before the grammar): a search longer than 60 stays whole and finds its mods; more
        // than 200 is cut there.
        const string longSearch = "(#Library OR #Gameplay) @\"Spire Mod Manager tests\" -@nobody -@\"no one at all\" -\"no such words\"";
        var longFound = await Shown(longSearch);
        var longMeant = content.GetChildren().OfType<NModMenuRow>().Where(r => SpireModManager.Search.Matches(r.Mod, longSearch)).Select(r => r.Mod?.manifest?.id ?? "").ToList();
        box.Clear();
        box.InsertTextAtCaret(new string('x', 250));
        var (cut, max) = (box.Text.Length, box.MaxLength);
        Check($"the search box takes 200 characters: a search of {longSearch.Length} stays whole and finds its mods; 250 typed, 200 kept",
            max == 200 && cut == 200 && longFound.Count > 0 && longFound.SequenceEqual(longMeant),
            $"max {max}, kept {cut}; found {string.Join(", ", longFound)}, matching {string.Join(", ", longMeant)}");
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
            .. mods.Select(mod => $"{Key(mod)}: on {Copies.Ticked(mod)}, copy {Key(Dependencies.Find(mod.manifest?.id ?? ""))}, "
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
            CleanHeap();
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

    // Three hundred made-up mods (a big list is about 250), no screen: what a redraw asks about every row (tags, "Needs",
    // "Required for", searches) and the page's warnings, answered as a redraw does (Lookups) and timed, then each
    // answer checked against the slow way (the saved list searched every time). Every seventh mod is off, every
    // tenth is a library the next ones need, every 25th also needs one that isn't installed, and one library has
    // a second copy that is on while the first is off. Then their rows (TestManyRows).
    static async Task TestManyMods(Node menu, NModdingScreen screen, Control content)
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
                manifest = new ModManifest
                {
                    id = $"SmmMany{i:000}", name = $"Many Mods {i}", author = $"Author {i % 12}", dependencies = needs, affectsGameplay = i % 2 == 0,
                    // About as long as a long real one (searched too).
                    description = string.Concat(Enumerable.Repeat($"Many Mods {i} adds [gold]cards[/gold], relics and events to every act. ", 8)),
                },
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
        using (Lookups.Begin(mods, [], saved, Picks.None))
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
            // Hidden tags that need the whole list (#problem asks for all of them) work it out once for the redraw.
            string[] hidden = ["#problem", "#needed OR #order", "#enabled -#local", "(#pending OR #unmet) many"];
            Lap();
            foreach (var query in hidden)
                for (var pass = 0; pass < 4; pass++)
                    foreach (var mod in mods)
                        Search.Matches(mod, query);
            var hiddenMs = Lap();
            Results.Add($"INFO 300 made-up mods: {hidden.Length} searches by hidden tags {hiddenMs:0.0} ms");
            Check("300 made-up mods: searches by hidden tags stay quick too (under 50 ms)", hiddenMs < 50, $"{hiddenMs:0.0} ms");

            string Times(string text, string word) => (text.Split(word).Length - 1).ToString();
            foreach (var mod in mods)
            {
                var id = mod.manifest?.id ?? "";
                var (tags, required, needs) = notes[mod];
                var deps = Dependencies.Of(mod);
                var expected = $"on {On(mod)}, copy {Copy(id)?.modSource}, library {Needing(mod, false) > 0}, required for {Needing(mod, true)}, "
                    + $"missing {(On(mod) ? deps.Count(d => Copy(d) == null) : 0)}, off {(On(mod) ? deps.Count(d => Copy(d) is { } dep && !On(dep)) : 0)}";
                var answered = $"on {Copies.Ticked(mod)}, copy {Dependencies.Find(id)?.modSource}, library {tags.Any(c => c.Text == "Library")}, "
                    + $"required for {(System.Text.RegularExpressions.Regex.Match(required, @"Required for (\d+)") is { Success: true } match ? match.Groups[1].Value : "0")}, "
                    + $"missing {Times(needs, "(not installed)")}, off {Times(needs, "(disabled)")}";
                if (answered != expected)
                    wrong.Add($"{id}: {answered}; the slow way: {expected}");
            }
        }
        Check("300 made-up mods: the same answers as the slow way (on, copies, libraries, required for, missing, off)",
            wrong.Count == 0 && Lookups.Current == null, $"{wrong.Count} differ: {string.Join(" | ", wrong.Take(3))}");
        await TestManyRows(menu, screen, content, mods, saved);
    }

    // The same mods as rows of a page (out of sight to the left of this one), in our row style: each row's line under
    // its title (tags, note, author), its buttons and its pencil made from nothing, as a new page makes them (after a
    // run the main menu is made again, and the page with it: #46), timed part by part (with the titles' styling before
    // them, and how often fonts were asked for and looked up), and redrawn with nothing to change. Then what the lines
    // show, against what the mods say: their tags, notes and authors, the author at the
    // line's end, the pencil and the category button with their tips. Each part of a line styled exactly as its old
    // per-node overrides styled it (UnlikeOverrides), at the old sizes. Everything turned off (some notes go: no mod on
    // needs those libraries) and on again: the lines changed in place show what lines made afresh do, and then what
    // they first did; no line holds an empty note.
    static async Task TestManyRows(Node menu, NModdingScreen screen, Control content, List<Mod> mods, List<SettingsSaveMod> saved)
    {
        if (Bmm.Present)
        {
            Results.Add("SKIP 300 made-up rows: Better Mod Menu dresses every row the game makes, and leaves handlers on the page for each");
            return;
        }
        var box = new VBoxContainer { Name = "SpireModManagerManyRows", MouseFilter = Control.MouseFilterEnum.Ignore };
        screen.AddChild(box);
        // Wide enough that no line folds its tags (what folds is the long-details test's).
        (box.Position, box.Size) = (new Vector2(-3000, 0), new Vector2(1600, 0));
        try
        {
            // The game's own rows, from its factory (found by name: a game update may change it).
            var create = typeof(NModMenuRow).GetMethod("Create", [typeof(NModdingScreen), typeof(Mod)]);
            var rows = new List<NModMenuRow>();
            foreach (var mod in mods)
                if (create?.Invoke(null, [screen, mod]) is NModMenuRow made)
                {
                    box.AddChild(made);
                    rows.Add(made);
                }
            if (rows.Count != mods.Count)
            {
                Results.Add($"SKIP 300 made-up rows: the game made {rows.Count} of {mods.Count}");
                return;
            }
            void Build(List<SettingsSaveMod> list)
            {
                using (Lookups.Begin(mods, [], list, Picks.None))
                    RowStrip.Apply(rows, CategoryList.StripActions(content), CategoryList.EdgeAction(content));
            }
            List<string> Expected(List<SettingsSaveMod> list)
            {
                using (Lookups.Begin(mods, [], list, Picks.None))
                {
                    var order = LoadOrder.Mods(rows).Select((m, i) => (m, i)).DistinctBy(p => p.m).ToDictionary(p => p.m, p => p.i);
                    return [.. rows.Select(row =>
                    {
                        var says = string.Join("    ", new[] { RowStrip.RequiredFor(row.Mod), RowStrip.Needs(row.Mod, order, false) }.Where(s => s.Length > 0));
                        var by = ModDetails.Short(row.Mod?.manifest?.author ?? "");
                        return string.Join(" | ", new[]
                        {
                            $"pencil ✎ ({Menus.Text("SPIRE_MOD_MANAGER.EDIT_TIP")})",
                            $"button {CategoryList.CategoryName(Copies.KeyOf(row.Mod))} ({Menus.Text("SPIRE_MOD_MANAGER.CATEGORY_TIP")})",
                        }.Concat(Tags.For(row.Mod).Select(c => $"tag {c.Text}"))
                            .Append(says.Length > 0 ? $"note {System.Text.RegularExpressions.Regex.Replace(says, @"\[[^\]]+\]", "")}" : "")
                            .Append(by.Length > 0 ? $"author {by} at the end" : "").Where(p => p.Length > 0));
                    })];
                }
            }
            static string Differ(List<string> shown, List<string> wanted) => shown.Zip(wanted).Where(p => p.First != p.Second)
                .Select(p => $"{p.First} -- wanted: {p.Second}").FirstOrDefault() ?? (shown.Count == wanted.Count ? "" : $"{shown.Count} rows, wanted {wanted.Count}");

            var (titlesMs, madeMs, fontAsks, fontLookups) = (0.0, 0.0, 0, 0);
            RowStrip.Costs.Clear();
            var frameMs = await FrameMs(menu, () =>
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                RowTitles.Apply(rows, vanilla: false); // our row style first, as a redraw does (titles fitted at the frame's end)
                titlesMs = watch.Elapsed.TotalMilliseconds;
                RowStrip.Timing = true;
                (fontAsks, fontLookups) = (ModEntry.FontAsks, ModEntry.FontLookups);
                watch.Restart();
                Build(saved);
                madeMs = watch.Elapsed.TotalMilliseconds;
                (fontAsks, fontLookups) = (ModEntry.FontAsks - fontAsks, ModEntry.FontLookups - fontLookups);
                RowStrip.Timing = false;
            });
            CleanHeap();
            var again = System.Diagnostics.Stopwatch.StartNew();
            Build(saved);
            var keptMs = again.Elapsed.TotalMilliseconds;
            Results.Add($"INFO 300 made-up rows: lines, buttons and pencils made from nothing {madeMs:0} ms ({Parts(RowStrip.Costs)}), "
                + $"to the end of that frame {frameMs:0} ms; redrawn with nothing to change {keptMs:0.0} ms; their titles styled first {titlesMs:0} ms; "
                + $"fonts asked for {fontAsks} times while the lines were made, looked up {fontLookups}");
            await Wait(menu, 0.3);

            var first = rows.Select(LineOf).ToList();
            Check("300 made-up rows: each line shows its tags, note and author (at the line's end), with the pencil and the category button and their tips",
                first.SequenceEqual(Expected(saved)), Differ(first, Expected(saved)));
            var sample = rows[10]; // a library: three tags, "Required for", an author
            var line = sample.GetNodeOrNull<Control>("SpireModManagerStrip");
            var tag = line?.GetChildren().OfType<PanelContainer>().FirstOrDefault();
            var (pencil, button, tagText) = (sample.GetNodeOrNull<Control>("SpireModManagerEdit"),
                sample.GetNodeOrNull<Control>(RowStrip.ActionsName)?.GetChildOrNull<Control>(0), tag?.GetChildOrNull<Control>(0));
            var (note, author) = (line?.GetNodeOrNull<Control>("Needs"), line?.GetNodeOrNull<Control>("Author"));
            var unlike = new[] { pencil, button, line, tag, tagText, note, author }
                .SelectMany((part, i) => part == null ? [$"part {i} missing"] : UnlikeOverrides(sample, part)).ToList();
            Check("300 made-up rows: the pencil, a button, the line, a tag and its text, the note and the author styled as their overrides did "
                + "(every theme item of their kind), at the old sizes (18, 16, 12, 13, 13; gap 6)", unlike.Count == 0
                && pencil?.GetThemeFontSize("font_size") == 18 && pencil.GetThemeStylebox("normal") is StyleBoxEmpty
                && button?.GetThemeFontSize("font_size") == 16 && button.GetThemeStylebox("normal") is StyleBoxFlat
                && tagText?.GetThemeFontSize("font_size") == 12 && note?.GetThemeFontSize("normal_font_size") == 13
                && author?.GetThemeFontSize("font_size") == 13 && line?.GetThemeConstant("separation") == 6, string.Join("; ", unlike.Take(6)));
            // The old overrides' own values, written out here, so a look changed by mistake (a colour, a font, a box)
            // shows: UnlikeOverrides compares each part with its new look only.
            var (kreon, cream, gold) = (ModEntry.Font("res://themes/kreon_regular_shared.tres"), new Color(1, 0.964706f, 0.886275f), new Color(0.937f, 0.784f, 0.318f));
            var chip = Tags.For(sample.Mod).FirstOrDefault();
            var olds = new (string Part, bool Same)[]
            {
                ("pencil", pencil?.GetThemeFont("font") == kreon && pencil.GetThemeColor("font_color") == new Color(cream, 0.6f)
                    && pencil.GetThemeColor("font_hover_color") == gold && pencil.GetThemeColor("font_pressed_color") == gold
                    && pencil.GetThemeColor("font_disabled_color") == new Color(cream, 0.3f)
                    && pencil.GetThemeStylebox("hover") is StyleBoxEmpty { ContentMarginLeft: 4, ContentMarginRight: 4 }),
                ("button", button?.GetThemeFont("font") == kreon && button.GetThemeColor("font_color") == cream
                    && button.GetThemeColor("font_hover_color") == Colors.White && button.GetThemeColor("font_disabled_color") == new Color(cream, 0.3f)
                    && button.GetThemeStylebox("normal") is StyleBoxFlat { } normal && normal.BgColor == new Color(0.0756f, 0.12084f, 0.18f, 0.7f)
                    && normal.BorderColor == new Color(gold, 0.35f) && button.GetThemeStylebox("hover") is StyleBoxFlat { } hover
                    && hover.BgColor == new Color(0.2f, 0.32f, 0.42f) && hover.BorderColor == gold),
                ("tag", chip != null && tag?.GetThemeStylebox("panel") is StyleBoxFlat { CornerRadiusTopLeft: 2, ContentMarginTop: 0 } panel
                    && panel.BgColor == new Color(chip.Color, chip.Custom ? 0.1f : 0.2f) && tagText?.GetThemeFont("font") == kreon
                    && tagText.GetThemeColor("font_color") == chip.Color),
                ("note", note?.GetThemeFont("normal_font") == kreon && note.GetThemeColor("default_color") == new Color(0.937f, 0.784f, 0.318f, 0.75f)),
                ("author", author?.GetThemeFont("font") == kreon && author.GetThemeColor("font_color") == new Color(1, 0.964706f, 0.886275f, 0.45f)),
            };
            Check("300 made-up rows: the pencil, a button, a tag, the note and the author in the old overrides' own fonts, colours and boxes",
                olds.All(o => o.Same), "unlike: " + string.Join(", ", olds.Where(o => !o.Same).Select(o => o.Part)));

            var off = mods.Select(m => new SettingsSaveMod(m) { IsEnabled = false }).ToList();
            Build(off);
            await Wait(menu, 0.3);
            var changed = rows.Select(LineOf).ToList();
            var noted = rows.Count(r => r.HasNode("SpireModManagerStrip/Needs"));
            foreach (var row in rows)
                Unmake(row);
            Build(off);
            await Wait(menu, 0.3);
            var fresh = rows.Select(LineOf).ToList();
            Check("300 made-up rows, all turned off: the lines changed in place show what lines made afresh do (and what the mods say)",
                noted < rows.Count && changed.SequenceEqual(fresh) && fresh.SequenceEqual(Expected(off)),
                $"{rows.Count - noted} notes gone; {Differ(changed, fresh)} {Differ(fresh, Expected(off))}");
            Build(saved);
            await Wait(menu, 0.3);
            var back = rows.Select(LineOf).ToList();
            Check("300 made-up rows, on again: each line shows what it first did (the notes made again in place)", back.SequenceEqual(first), Differ(back, first));
            Check("300 made-up rows: no line holds an empty note", rows.All(r => r.GetNodeOrNull<RichTextLabel>("SpireModManagerStrip/Needs") is not { Text.Length: 0 }));

            // A row's buttons kept through a redraw (the same words and states) run that redraw's actions: an action holds
            // what its redraw knew (Load Order's rows). And a line is made again when a tag's card or search changes, its
            // words, colour and kind the same (the Same ID tag's card names the other copies).
            var ran = "";
            using (Lookups.Begin(mods, [], saved, Picks.None))
            {
                RowStrip.Apply([rows[0]], _ => [new MenuItem("Probe", () => ran = "first")]);
                RowStrip.Apply([rows[0]], _ => [new MenuItem("Probe", () => ran = "second")]);
            }
            var probeButtons = rows[0].GetNodeOrNull<HBoxContainer>(RowStrip.ActionsName)?.GetChildren().OfType<Button>().ToList() ?? [];
            if (probeButtons is [var probe])
                probe.EmitSignal(BaseButton.SignalName.Pressed);
            Check("300 made-up rows: buttons kept through a redraw run that redraw's actions, not the ones they were made with",
                probeButtons is [{ Text: "Probe" }] && ran == "second", $"{probeButtons.Count} buttons, ran \"{ran}\"");
            var sameId = new Tags.Chip("Same ID", Colors.Yellow, Tip: "Another copy: A");
            var shows = RowStrip.Shows([sameId], "Someone");
            Check("300 made-up rows: a line is made again when a tag's card or search changes, its words, colour and kind the same",
                RowStrip.Shows([sameId with { Tip = "Another copy: B" }], "Someone") != shows && RowStrip.Shows([sameId with { Search = "#same" }], "Someone") != shows
                && RowStrip.Shows([sameId], "Someone") == shows, shows);
        }
        finally
        {
            screen.RemoveChild(box);
            box.QueueFree();
        }
    }

    // Load Order's search finds rather than filters: every row stays; beside the box, "N matches · ‹ ›"
    // steps through the matching rows (each selected and scrolled to), wrapping at the ends; typing goes to the first;
    // Enter and Shift+Enter step too; no search, no bar.
    static async Task TestLoadOrderFind(Node menu, NModdingScreen screen, Control content, string outDir)
    {
        var border = screen.GetNode<Control>("ModsBorder");
        var box = border.GetNode<LineEdit>("SpireModManagerSearch");
        var wasLoadOrder = Store.Data.LoadOrderView;
        if (!wasLoadOrder)
        {
            Click(screen.GetNode<NButton>("SpireModManagerLoadOrderToggle"));
            await Wait(menu, 0.5);
        }
        List<NModMenuRow> Rows() => content.GetChildren().OfType<NModMenuRow>().ToList();
        Control? Bar() => border.GetNodeOrNull<Control>(FindBar.Name);
        string Count() => Bar()?.GetNodeOrNull<Label>("Count")?.Text ?? "";
        void Press(string name) => Bar()?.GetNodeOrNull<Button>(name)?.EmitSignal(BaseButton.SignalName.Pressed);
        async Task Type(string text)
        {
            box.Text = text;
            box.EmitSignal(LineEdit.SignalName.TextChanged, text);
            await Wait(menu, 0.3);
        }
        async Task Key(bool shift)
        {
            box.EmitSignal(Control.SignalName.GuiInput, new InputEventKey { Keycode = Godot.Key.Enter, Pressed = true, ShiftPressed = shift });
            await Wait(menu, 0.2);
        }
        int At(List<NModMenuRow> found) => found.FindIndex(r => r.Mod == CategoryList.Selected);

        Check("Load Order, no search: no find bar", Bar() is not { Visible: true });
        var boxEnd = box.GetGlobalRect().End.X;
        if (Rows().FirstOrDefault(r => !SpireModManager.Search.Matches(r.Mod, "SmmTest")) is { } other)
            CategoryList.Select(screen, other); // a row the search won't match: typing goes to the first match
        await Type("SmmTest");
        var found = FindBar.Found(content);
        var view = border.GetNode<Control>("ModsScrollContainer").GetGlobalRect();
        Check("Load Order, a search: every row stays, \"N matches\" and ‹ › beside the box, the first match selected and in view",
            found.Count >= 3 && Rows().All(r => r.Visible) && Bar() is { Visible: true } bar && Count() == $"{found.Count} matches" && At(found) == 0
            && view.Intersects(found[0].GetGlobalRect()) && bar.GetGlobalRect().Position.X >= box.GetGlobalRect().End.X,
            $"{found.Count} found, count \"{Count()}\", at {At(found)}, rows shown {Rows().Count(r => r.Visible)}/{Rows().Count}");
        Check("Load Order: the search box keeps its width as a search starts (the find bar's room kept beside it)",
            Math.Abs(box.GetGlobalRect().End.X - boxEnd) < 0.5f, $"box ends at {boxEnd}, then {box.GetGlobalRect().End.X}");
        Check("the arrows say where they go", Bar() is { } arrows && Hover.TipOf(arrows.GetNode<Button>("PreviousMatch")) == "Go to previous found match"
            && Hover.TipOf(arrows.GetNode<Button>("NextMatch")) == "Go to next found match");
        // The rows it doesn't match fade below even a match that's off; a match keeps its usual look.
        float Expected(NModMenuRow r, bool finding) => (!finding || found.Contains(r), r.Mod is { } mod && Copies.Ticked(mod)) switch
        {
            (true, true) => 1, (true, false) => LoadOrder.OffAlpha, (false, true) => LoadOrder.UnmatchedAlpha, _ => LoadOrder.UnmatchedOffAlpha,
        };
        string Looks() => string.Join(", ", Rows().Select(r => $"{r.Mod?.manifest?.id} {r.Modulate.A:0.##}"));
        Check("finding: the rest fade (on 0.2, off 0.1) below a match that's off (0.4); matches look as usual",
            Rows().All(r => Math.Abs(r.Modulate.A - Expected(r, finding: true)) < 0.01f) && Rows().Any(r => !found.Contains(r))
            && Rows().Any(r => found.Contains(r) && r.Mod is { } mod && !Copies.Ticked(mod)), Looks());
        LoadOrder.Reset(content); // as a drag ends
        Check("finding: the same looks after a drag ends", Rows().All(r => Math.Abs(r.Modulate.A - Expected(r, finding: true)) < 0.01f), Looks());
        await Capture(screen, outDir, "11c-load-order-find");
        Press("NextMatch");
        await Wait(menu, 0.2);
        var second = At(found);
        Press("PreviousMatch");
        await Wait(menu, 0.2);
        var backToFirst = At(found);
        Press("PreviousMatch");
        await Wait(menu, 0.2);
        var wrappedToLast = At(found);
        Press("NextMatch");
        await Wait(menu, 0.2);
        Check("› and ‹ step through the matches, wrapping at the ends, each scrolled to", second == 1 && backToFirst == 0
            && wrappedToLast == found.Count - 1 && At(found) == 0 && view.Intersects(found[0].GetGlobalRect()),
            $"next {second}, back {backToFirst}, before the first {wrappedToLast}, after the last {At(found)}");
        await Key(shift: false);
        var byEnter = At(found);
        await Key(shift: true);
        Check("Enter goes to the next match, Shift+Enter the one before", byEnter == 1 && At(found) == 0, $"Enter {byEnter}, Shift+Enter {At(found)}");
        await Type("zzzz no such mod");
        Check("no match: \"No matches\", the arrows off, every row still shown", Count() == "No matches" && Rows().All(r => r.Visible)
            && Bar() is { } emptyBar && emptyBar.GetChildren().OfType<Button>().All(b => b.Disabled), Count());
        Check("no match: every row faded", Rows().All(r => r.Modulate.A <= LoadOrder.UnmatchedAlpha + 0.01f), Looks());
        await Type("");
        Check("search cleared: no find bar, the box the same width, its room kept", Bar() is not { Visible: true }
            && Math.Abs(box.GetGlobalRect().End.X - boxEnd) < 0.5f, $"box ends at {boxEnd}, then {box.GetGlobalRect().End.X}");
        Check("search cleared: every row its usual look again", Rows().All(r => Math.Abs(r.Modulate.A - Expected(r, finding: false)) < 0.01f), Looks());
        if (!wasLoadOrder)
        {
            Click(screen.GetNode<NButton>("SpireModManagerLoadOrderToggle"));
            await Wait(menu, 0.5);
        }
    }

    // Two copies of one mod with one id and source (with dev.cs test --workshop and two items of one mod, those; else the
    // fixture twins, SmmTestTwinA and B). The saved list keeps one entry per copy through a move and back (it grew by one
    // with every save), Changes and the main menu agree nothing moved, and the page's line follows the menu's rule. On our
    // main list (where a tick is its own undo step: Load Order's undo steps through the order) each copy has its own box,
    // the picked one's ticked, the other off (copies_off): a click on the other's makes that copy the one that loads, the
    // first staying ticked, as one undo step, Changes naming only it, the saved list's entries all on (one switch for
    // both); undo puts the pick back. Turning the mod off and on sets every one of its entries (the game reads any entry
    // off as off), the pick and copies_off kept.
    static async Task TestTwinCopies(Node menu, NModdingScreen screen, Control content)
    {
        List<NModMenuRow> Rows() => content.GetChildren().OfType<NModMenuRow>().ToList();
        if (ModManager.Mods.Where(m => m.manifest?.id != null).GroupBy(m => (Id: m.manifest?.id ?? "", m.modSource))
            .Where(g => g.Select(Copies.CopyOf).Distinct().Count() > 1).OrderBy(g => g.Key.Id == TwinId ? 1 : 0).FirstOrDefault() is not { } twins)
        {
            TwinsMissing("two copies of one mod");
            return;
        }
        if (SaveManager.Instance.SettingsSave.ModSettings is not { } settings)
        {
            Results.Add("FAIL two copies of one mod: the game has no saved mod list");
            return;
        }
        Results.Add($"INFO two copies of one mod: {twins.Key.Id} ({string.Join(", ", twins.Select(Copies.CopyOf))})");
        var list = settings.ModList;
        var (id, source) = twins.Key;
        int Entries() => list.Count(e => e.Id == id && e.Source == source);
        bool LineFollows() => screen.GetNode<Control>("%PendingChangesLabel").Visible == ModEntry.HasPendingChanges();
        var (count, entries) = (list.Count, Entries());
        var other = Rows().Select(r => r.Mod).FirstOrDefault(m => m != null && m.manifest?.id != id && m.manifest?.id != ModEntry.ModId);
        if (other == null)
        {
            Check("two copies of one mod: another mod to move", false);
            return;
        }
        // Past its neighbour (not to the top: ours stay first).
        var at = Rows().FindIndex(r => r.Mod == other);
        LoadOrder.Move(other, at + 1 < Rows().Count ? at + 1 : at - 1, Rows());
        CategoryList.Redraw(content);
        await Wait(menu, 0.3);
        Check("two copies of one mod: a move keeps one entry each (the list doesn't grow), and Changes, the menu and the page's line agree",
            list.Count == count && Entries() == entries && HistoryBar.Moves().Count > 0 && LoadOrder.OrderChanged && ModEntry.HasPendingChanges() && LineFollows(),
            $"{list.Count} entries (was {count}), {id}: {Entries()} (was {entries}), moved {HistoryBar.Moves().Count}");
        LoadOrder.Move(other, at, Rows());
        CategoryList.Redraw(content);
        await Wait(menu, 0.3);
        Check("two copies of one mod: moved back, nothing moved for Changes or the menu, and the list is as long as before",
            list.Count == count && Entries() == entries && HistoryBar.Moves().Count == 0 && !LoadOrder.OrderChanged && LineFollows(),
            $"{list.Count} entries (was {count}), moved {HistoryBar.Moves().Count}, order changed {LoadOrder.OrderChanged}");
        var on = list.Where(e => e.Id == id && e.Source == source).Select(e => e.IsEnabled).ToList(); // to put back at the end

        // A click as the game's tickbox makes one (its ForceToggleTick is beta-only): the box flips, then Toggled fires.
        static void Toggle(NModMenuRow row)
        {
            var box = row.GetNode<NTickbox>("Tickbox");
            box.IsTicked = !box.IsTicked;
            box.EmitSignal("Toggled", box);
        }
        static bool Ticked(NModMenuRow row) => row.GetNode<NTickbox>("Tickbox").IsTicked;
        var toggle = screen.GetNode<NButton>("SpireModManagerLoadOrderToggle");
        Click(toggle); // to the main list
        await Wait(menu, 0.5);
        var twinRows = Rows().Where(r => r.Mod != null && twins.Contains(r.Mod)).ToList();
        if (twinRows.Count == 2 && Copies.Picked(id, source) is { } pick && twinRows.FirstOrDefault(r => r.Mod is { } m && Copies.CopyOf(m) == pick) is { Mod: { } pickedMod } picked
            && twinRows.FirstOrDefault(r => r != picked) is { Mod: { } otherMod } unpicked)
        {
            string Named(Mod mod) => (mod.manifest?.name ?? id) + Copies.Which(mod);
            var wasOn = Copies.Ticked(pickedMod);
            var offWere = CopiesOff();
            Check("two copies of one mod: a box each on our page, the picked copy's ticked as it's on, the other's not (copies_off)",
                Ticked(picked) == wasOn && !Ticked(unpicked) && !Copies.Ticked(otherMod) && offWere.Contains(Copies.SwitchKey(otherMod)) && CopiesBroken() == null,
                $"boxes {Ticked(picked)}/{Ticked(unpicked)}, on {wasOn}, off {string.Join(", ", offWere)}; {CopiesBroken()}");
            var steps = History.Steps(History.Track.List);
            var (onBefore, offBefore, _) = HistoryBar.Counts();
            Toggle(unpicked);
            await Wait(menu, 0.3);
            var lines = HistoryBar.Describe();
            var (onNow, offNow, _) = HistoryBar.Counts();
            var changed = HistoryBar.ChangedMods();
            Check("two copies of one mod: ticking the other copy makes it the one that loads, the first staying ticked, one undo step; Changes names only it, "
                + "+1; the saved list's entries all on; Show: Changed lists only that row; pending",
                Ticked(unpicked) && Ticked(picked) == wasOn && Copies.Ticked(otherMod) && Copies.Ticked(pickedMod) == wasOn
                && Copies.Picked(id, source) == Copies.CopyOf(otherMod) && Store.Data.CopyPicks?.Contains(Copies.SwitchKey(otherMod)) == true
                && !CopiesOff().Contains(Copies.SwitchKey(otherMod)) && list.Where(e => e.Id == id && e.Source == source).All(e => e.IsEnabled)
                && History.Steps(History.Track.List) == steps + 1 && lines.Contains($"Enabled: {Named(otherMod)}") && !lines.Any(l => l.Contains(Named(pickedMod)))
                && onNow - onBefore == 1 && offNow == offBefore && changed.Contains(otherMod) && !changed.Contains(pickedMod) && ModEntry.HasPendingChanges()
                && CopiesBroken() == null,
                $"boxes {Ticked(picked)}/{Ticked(unpicked)}, pick {Copies.Picked(id, source)}, steps {steps} -> {History.Steps(History.Track.List)}, "
                + $"counts +{onNow - onBefore} -{offNow - offBefore}; {string.Join(" | ", lines)}; {CopiesBroken()}");
            History.Undo();
            await Wait(menu, 0.3);
            Check("two copies of one mod: undo puts the pick back, boxes, copies_off and Changes too", Ticked(picked) == wasOn && !Ticked(unpicked)
                && Copies.Picked(id, source) == pick && CopiesOff().SequenceEqual(offWere) && !HistoryBar.Describe().Any(l => l.Contains(Named(otherMod))),
                $"boxes {Ticked(picked)}/{Ticked(unpicked)}, pick {Copies.Picked(id, source)}; {string.Join(" | ", HistoryBar.Describe())}");
            // The picked copy the only one ticked: off, the switch goes off (the pick and copies_off kept); on, it comes on
            // alone, the pick still.
            Dependencies.Set([pickedMod], false, screen);
            var offAll = list.Where(e => e.Id == id && e.Source == source).All(e => !e.IsEnabled) && CopiesOff().SequenceEqual(offWere);
            Dependencies.Set([pickedMod], true, screen);
            var onAll = list.Where(e => e.Id == id && e.Source == source).All(e => e.IsEnabled) && Copies.Picked(id, source) == pick && CopiesOff().SequenceEqual(offWere);
            Check("two copies of one mod: turning the picked copy off and on sets every one of the mod's entries, the pick and copies_off kept",
                offAll && onAll && CopiesBroken() == null, $"off {offAll}, on {onAll}; {string.Join(", ", CopiesOff())}; {CopiesBroken()}");
        }
        else
            Check($"two copies of one mod: a box per copy (rows {twinRows.Count}), one of them picked ({Copies.Picked(id, source)})", false);
        var restore = list.Where(e => e.Id == id && e.Source == source).ToList();
        for (var i = 0; i < restore.Count && i < on.Count; i++)
            restore[i].IsEnabled = on[i];
        SaveManager.Instance.SaveSettings();
        Profiles.RefreshTickboxes(screen);
        Click(toggle); // back to Load Order
        await Wait(menu, 0.5);
    }

    // The fixture twins missing: a FAIL where the fixture's other mods are there (staging lost them, or their copies are
    // named another way), else a SKIP (a run without the fixtures).
    static void TwinsMissing(string what)
    {
        if (Dependencies.Find("SmmTestTwinUser") != null)
            Check($"{what}: the fixture twins installed, as l:SmmTestTwinA and l:SmmTestTwinB", false,
                string.Join(", ", ModManager.Mods.Where(m => m.manifest?.id == TwinId).Select(Copies.CopyOf)));
        else
            Results.Add($"SKIP {what}: the fixture twins aren't installed");
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
        var startMods = Rows().Select(r => r.Mod).OfType<Mod>().ToList(); // to put back by copy: two copies of a mod share an id
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

        // Ours lead the list (one row per copy), pinned: the moves below are of the rows after them.
        var pinned = Ids().TakeWhile(id => id == ModEntry.ModId).Count();
        Check("load order: ours first, pinned", pinned >= 1, string.Join(", ", start));
        // Moves use a mod with no dependencies either way, so no order rule can block them.
        const string free = "SmmTestFixes";
        // The first row after ours, which the move pushes down: its buttons (Top, Up, Down, Bottom) are given their new
        // states and actions in place (Menus.Refill), not made again.
        var passed = Rows()[pinned].Mod?.manifest?.id == free ? null : Rows()[pinned];
        List<ulong> ButtonIds() => passed?.GetNodeOrNull(RowStrip.OrderActionsName)?.GetChildren().Select(c => c.GetInstanceId()).ToList() ?? [];
        List<bool> ButtonsOff() => passed?.GetNodeOrNull(RowStrip.OrderActionsName)?.GetChildren().OfType<Button>().Select(b => b.Disabled).ToList() ?? [];
        var (passedIds, passedOff) = (ButtonIds(), ButtonsOff());
        if (Rows().FirstOrDefault(r => r.Mod?.manifest?.id == free)?.Mod is { } freeMod)
            LoadOrder.Move(freeMod, 0, Rows());
        CategoryList.Redraw(content);
        await Wait(menu, 0.5);
        Check("move to top saves the order, under ours", Ids()[pinned] == free && Saved()[pinned] == free && Ids()[0] == ModEntry.ModId,
            string.Join(", ", Ids()));
        if (passed != null)
            Check("the row the move pushed down keeps its buttons, given their new states (Top and Up free now)", passedIds.Count == 4
                && ButtonIds().SequenceEqual(passedIds) && passedOff.SequenceEqual([true, true, false, false]) && ButtonsOff().SequenceEqual([false, false, false, false]),
                $"{string.Join(",", passedIds)} -> {string.Join(",", ButtonIds())}; disabled {string.Join(",", passedOff)} -> {string.Join(",", ButtonsOff())}");
        else
            Results.Add($"SKIP a move's passed row keeps its buttons: {free} was first after ours already");
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
        var wasText = Rows()[pinned].GetNodeOrNull<Label>("SpireModManagerWas")?.Text;
        Check("a moved row shows where it was at launch", wasText == $"{start.IndexOf(free) + 1} →",
            wasText ?? "none");
        if (Rows().FirstOrDefault(r => r.Mod?.manifest?.id == "SmmTestUi") is { Mod: { } uiMod } uiRow)
        {
            var toggle = LoadOrder.Actions(content, uiRow, () => CategoryList.Redraw(content))[0];
            toggle.OnSelect?.Invoke();
            await Wait(menu, 0.3);
            var offNow = !Copies.Ticked(uiMod);
            var again = LoadOrder.Actions(content, uiRow, () => CategoryList.Redraw(content))[0];
            again.OnSelect?.Invoke();
            await Wait(menu, 0.3);
            Check("load order menu: Disable, then Enable", toggle.Label == "Disable" && offNow && again.Label == "Enable" && Copies.Ticked(uiMod),
                $"{toggle.Label}, off {offNow}, {again.Label}");
        }
        // Its Up, a button kept through the move, moves it from where it is now: to the top (under ours) again. Then the moved mod goes
        // back to the top for the drags below.
        if (passed?.GetNodeOrNull(RowStrip.OrderActionsName)?.GetChildOrNull<Button>(1) is { } up)
        {
            up.EmitSignal(BaseButton.SignalName.Pressed);
            await Wait(menu, 0.5);
            var upped = Ids();
            Check("a kept Up button moves its row from where it is now (to the top, under ours)", upped[pinned] == passed.Mod?.manifest?.id && upped[pinned + 1] == free
                && ButtonIds().SequenceEqual(passedIds) && ButtonsOff().SequenceEqual([true, true, false, false]), string.Join(", ", upped.Take(pinned + 2)));
            if (Rows().FirstOrDefault(r => r.Mod?.manifest?.id == free)?.Mod is { } freeAgain)
                LoadOrder.Move(freeAgain, 0, Rows());
            CategoryList.Redraw(content);
            await Wait(menu, 0.5);
        }

        // Drag the first row after ours by its handle to just below the third.
        var first = Rows()[pinned];
        var handle = first.GetNode<Control>("SpireModManagerHandle");
        var from = handle.GetGlobalRect().GetCenter();
        var to = new Vector2(from.X, Rows()[pinned + 2].GetGlobalRect().End.Y - 4);
        void Send(InputEvent e) => handle.GetParent().EmitSignal(Control.SignalName.GuiInput, e);
        Send(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, GlobalPosition = from, Position = from });
        Send(new InputEventMouseMotion { GlobalPosition = to, Position = to });
        await Wait(menu, 0.3);
        await Capture(screen, outDir, "11-load-order-drag");
        Send(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, GlobalPosition = to, Position = to });
        await Wait(menu, 0.5);
        var dragged = Ids();
        Check("drag moves the row below the third", dragged.IndexOf(first.Mod?.manifest?.id ?? "") == pinned + 2 && dragged.SequenceEqual(Saved()),
            string.Join(", ", dragged));

        // A press alone already shows the drag (dimmed list, drop line); releasing without moving changes nothing.
        var pressed = Rows()[pinned];
        var at = pressed.GetGlobalRect().GetCenter();
        var orderBefore = Ids();
        pressed.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, GlobalPosition = at });
        await Wait(menu, 0.1);
        Check("pressing a row starts the drag look at once (the drop line's ends slightly rounded)", Rows().Any(r => r.Modulate.A < 0.5f && r != pressed)
            && content.GetParent().GetChildren().OfType<Panel>().Any(c => c.Size.Y == 5 && c.GetThemeStylebox("panel") is StyleBoxFlat { CornerRadiusTopLeft: > 0 }));
        pressed.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, GlobalPosition = at });
        await Wait(menu, 0.3);
        Check("a click without moving leaves the order alone", Ids().SequenceEqual(orderBefore));
        // The list hidden mid-drag (the page left with the button held): the drag stops, its drop line gone, and a release
        // from the hidden list's top (Godot's, as the row hides) or after it shows again drops nothing.
        var scroller = content.GetParent().GetParent<Control>();
        var held = Rows()[pinned + 2]; // a drop at the top would move it
        var heldAt = held.GetGlobalRect().GetCenter();
        void Mouse(InputEvent e) => held.EmitSignal(Control.SignalName.GuiInput, e);
        var hiddenDrags = new List<string>();
        foreach (var atOnce in new[] { true, false })
        {
            var top = new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, GlobalPosition = new Vector2(heldAt.X, 10) };
            Mouse(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, GlobalPosition = heldAt });
            Mouse(new InputEventMouseMotion { GlobalPosition = heldAt + new Vector2(0, 40) });
            await Wait(menu, 0.1);
            scroller.Visible = false;
            int lines;
            try
            {
                if (atOnce)
                    Mouse(top);
                await Wait(menu, 0.1);
                lines = content.GetParent().GetChildren().OfType<Panel>().Count(c => c.Size.Y == 5 && !c.IsQueuedForDeletion());
            }
            finally
            {
                scroller.Visible = true; // a throw while it's hidden would hide the list for every later test
            }
            if (!atOnce)
                Mouse(top);
            await Wait(menu, 0.3);
            hiddenDrags.Add($"{(atOnce ? "released" : "held")}: {lines} lines, {(Ids().SequenceEqual(orderBefore) ? "kept" : "moved")}");
        }
        Check("the list hidden mid-drag: the drag stops (no drop line left) and no release drops the row",
            hiddenDrags.All(d => d.EndsWith(": 0 lines, kept")), string.Join("; ", hiddenDrags));

        // Multi-select: pick the first and third rows after ours, drag the first's grip below the last row; both move,
        // in their order.
        var all = Rows();
        Selection.Pick(content, all[pinned], toggle: false, range: false);
        Selection.Pick(content, all[pinned + 2], toggle: true, range: false);
        var pickedIds = new[] { all[pinned], all[pinned + 2] }.Select(r => r.Mod?.manifest?.id ?? "").ToList();
        Check("Ctrl-click picks a second row, both highlighted", Selection.Mods.Count == 2
            && Rows().Count(r => r.GetNodeOrNull<Control>("SpireModManagerPicked")?.Visible == true) == 2);
        var grip = all[pinned].GetNode<Control>("SpireModManagerHandle");
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
        for (var i = 0; i < startMods.Count; i++)
            LoadOrder.Move(startMods[i], i, Rows());
        CategoryList.Redraw(content);
        await Wait(menu, 0.5);
        Check("restoring the order clears pending", Ids().SequenceEqual(start) && !ModEntry.HasPendingChanges());
        await TestTwinCopies(menu, screen, content);
        await TestSameIdLoadOrder(menu, screen, content);

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
        {
            CategoryList.Select(screen, libSelected);
            await PanelFor(menu, screen, libSelected);
        }
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
        Check("Load Order: the order warning's Go to, Show and Fix now right after its text", fixAfter.Ok, fixAfter.Detail);
        // Its Go to goes to the mod loading too early; its Show finds it ("#order": every row in place, "1 match").
        void OrderPress(string name) => (screen.FindChild(LoadOrder.WarningName, true, false)?.FindChild(name, true, false) as BaseButton)
            ?.EmitSignal(BaseButton.SignalName.Pressed);
        if (Row(ModEntry.ModId) is { } elsewhere)
            CategoryList.Select(screen, elsewhere);
        OrderPress("GoToOrderButton");
        await Wait(menu, 0.3);
        var goneTo = CategoryList.Selected;
        OrderPress("FilterOrderButton");
        await Wait(menu, 0.3);
        var searchBox = screen.GetNode<LineEdit>("ModsBorder/SpireModManagerSearch");
        bool OrderLit() => screen.FindChild(LoadOrder.WarningName, true, false)?.FindChild("FilterOrderButton", true, false) is Button { } show
            && show.HasThemeStyleboxOverride("normal"); // the chosen look
        var (orderBox, orderFound, everyRow, orderLit) = (searchBox.Text, (screen.FindChild(FindBar.Name, true, false)?.GetNodeOrNull<Label>("Count"))?.Text,
            Rows().All(r => r.Visible), OrderLit());
        OrderPress("FilterOrderButton");
        await Wait(menu, 0.3);
        Check("Load Order: the order warning's Go to goes to Test Content; its Show puts \"#order\" in the box and finds it alone, every row in place; again, the box empty",
            goneTo == dependent && orderBox == "#order" && orderFound == "1 match" && everyRow && searchBox.Text == "",
            $"went to {goneTo?.manifest?.id}; box \"{orderBox}\", find \"{orderFound}\", every row {everyRow}; then \"{searchBox.Text}\"");
        var orderWords = (Go: (screen.FindChild(LoadOrder.WarningName, true, false)?.FindChild("GoToOrderButton", true, false) as Button)?.Text,
            Show: (screen.FindChild(LoadOrder.WarningName, true, false)?.FindChild("FilterOrderButton", true, false) as Button)?.Text);
        var orderShowTip = TipOf(screen.FindChild(LoadOrder.WarningName, true, false)?.FindChild("FilterOrderButton", true, false));
        Check("Load Order: the order warning's Show is lit while the box holds \"#order\", not after; its buttons read \"Go to\" and \"Show\"; its tip says it "
            + "finds them (Load Order hides no row)",
            orderLit && !OrderLit() && orderWords == (GoToProblem, ShowFilter) && orderShowTip == ShowFilterFindTip, $"lit {orderLit}, after {OrderLit()}; {orderWords}; {orderShowTip}");
        // The hidden tags on the list as it is: Test Content #order (and #moved: it was moved here), each finding the rows it means.
        var asMeant = HiddenTagsAsMeant(content);
        Check("bad order: hidden tags on the list as it is (Test Content #order and #moved), each finding the rows it means",
            asMeant.Wrong.Count == 0 && SpireModManager.Search.Matches(dependent, "#order") && SpireModManager.Search.Matches(dependent, "#moved")
            && !SpireModManager.Search.Matches(lib, "#order"), $"{string.Join(" | ", asMeant.Wrong)}; found {asMeant.Found}");
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
            Check("enabled again and the move undone: Test UI on, in its place", Copies.Ticked(ui) && Ids().SequenceEqual(idsWere), string.Join(", ", Ids()));
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

        // A move above the framework is flagged but allowed: by the inline/menu actions and by dragging. Ours, pinned
        // first, counted before (counted after, a drop above ours would count none).
        var pinned = Ids().TakeWhile(id => id == ModEntry.ModId).Count();
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
        // Dropped at the top, it lands under ours (pinned first).
        Check("a drag past a dependency goes through and is flagged, landing under ours", pinned > 0 && Ids()[0] == ModEntry.ModId
            && Ids()[pinned] == "SmmTestContent" && screen.FindChild("OrderProblems", true, false) != null, string.Join(", ", Ids()));
        screen.FindChild("FixOrderButton", true, false)?.EmitSignal(BaseButton.SignalName.Pressed);
        await Wait(menu, 0.5);
        string? SavedFirst() => SaveManager.Instance.SettingsSave.ModSettings?.ModList.FirstOrDefault()?.Id;
        Check("Fix order leaves ours first", Ids()[0] == ModEntry.ModId && SavedFirst() == ModEntry.ModId, string.Join(", ", Ids()));

        // Auto sort: libraries, then gameplay, then visual-only; dependencies satisfied; one undo puts it back.
        var beforeSort = Ids();
        screen.FindChild("AutoSortButton", true, false)?.EmitSignal(BaseButton.SignalName.Pressed);
        await Wait(menu, 0.5);
        int Tier(NModMenuRow r) => Tags.For(r.Mod).Any(c => c.Text == "Library") ? 0
            : Tags.For(r.Mod).Any(c => c.Text == "Gameplay") ? 1 : 2;
        var tiers = Rows().Skip(pinned).Select(Tier).ToList(); // ours stay first, whatever their tier
        Check("auto sort: ours first, then libraries, then gameplay, then visual-only, no dependency problems", Ids()[0] == ModEntry.ModId
            && SavedFirst() == ModEntry.ModId && Ids().Take(pinned).All(id => id == ModEntry.ModId) &&
            tiers.Zip(tiers.Skip(1)).All(p => p.First <= p.Second) && Dependencies.OrderViolations(LoadOrder.Mods(Rows())).Count == 0,
            string.Join(", ", Ids()));
        await Capture(screen, outDir, "13c-auto-sort");
        History.Undo();
        await Wait(menu, 0.5);
        Check("one undo restores the order from before auto sort", Ids().SequenceEqual(beforeSort), string.Join(", ", Ids()));
        await Wait(menu, 0.2);
        Check("dropping restores the list look (only mods that are off stay dim)", Rows().All(r => r.Modulate == (r.Mod is { } m && !Copies.Ticked(m) ? new Color(1, 1, 1, 0.4f) : Colors.White))
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
            // The rows here wear the main list's rounded looks: faint under the mouse, brighter when selected, with an even
            // gold rim (the same width on every side, so its edges line up with the other rows' boxes).
            var selectedRow = Rows().FirstOrDefault(r => r.Mod == CategoryList.Selected && r != libRow);
            StyleBox? LookOf(NModMenuRow? r) => r?.GetNodeOrNull<Panel>("SelectionHighlight")?.GetThemeStylebox("panel");
            Check("load order rows: rounded like the main list's, under the mouse and selected; the selected one brighter, with an even gold rim",
                LookOf(libRow) is StyleBoxFlat { CornerRadiusTopLeft: RowTitles.CornerRadius, BorderWidthTop: 0, BorderWidthLeft: 0 } hoverLook
                && LookOf(selectedRow) is StyleBoxFlat { CornerRadiusTopLeft: RowTitles.CornerRadius } selectedBox && EvenGoldRim(selectedBox)
                && selectedBox.BgColor.A > hoverLook.BgColor.A
                && selectedRow?.GetNode<Panel>("SelectionHighlight") is { } selectedLook && selectedLook.Modulate == Colors.White
                && selectedLook.OffsetLeft == RowTitles.LookInset.X, $"selected: {selectedRow?.Mod?.manifest?.id ?? "none"}");
            await Capture(screen, outDir, "13b-hover-links");
            libRow.EmitSignal(Control.SignalName.MouseExited);
            await Wait(menu, 0.2);
            // Test Content, Test UI and the fixture's newer twin, Test Twin Beta.
            Check("hover shows links to each mod that needs it, and they go on leaving", shown == Dependencies.NeededBy("SmmTestLib").Count() && shown == 3
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
        Check("main list: a needed mod that is off gets a banner", text.Contains("require a mod that is disabled"), text);
        var warning = screen.FindChild(LoadOrder.WarningName, true, false);
        var goToTip = TipOf(warning?.FindChild("GoToProblemButton", true, false));
        var showTip = TipOf(warning?.FindChild("FilterProblemButton", true, false));
        Check("main list banner: red, its Go to tip \"Open the next affected mod\", its Show's saying it filters, no \"Better Mod Menu\" in it",
            Red(BannerFill(warning)) && goToTip == GoToProblemTip && showTip == ShowFilterTip
            && !BannerWords(warning).Any(w => w.Contains("Better Mod Menu")), $"fill {BannerFill(warning)}; {string.Join(" | ", BannerWords(warning))}");
        var (goToWords, showWords) = ((warning?.FindChild("GoToProblemButton", true, false) as Button)?.Text, (warning?.FindChild("FilterProblemButton", true, false) as Button)?.Text);
        Check("main list banner: its buttons read \"Go to\" and \"Show\"", goToWords == GoToProblem && showWords == ShowFilter, $"\"{goToWords}\", \"{showWords}\"");
        // The mods it's about, each tag by what it means: Test Framework #needed (off, the others need it), Test Content and
        // Test UI #unmet.
        var asMeant = HiddenTagsAsMeant(content);
        Check("main list banner: hidden tags on the list as it is (Test Framework #needed, Test Content and Test UI #unmet), each finding the rows it means",
            asMeant.Wrong.Count == 0 && SpireModManager.Search.Matches(lib, "#needed") && SpireModManager.Search.Matches(dependent, "#unmet")
            && Row("SmmTestUi")?.Mod is { } ui && SpireModManager.Search.Matches(ui, "#unmet") && !SpireModManager.Search.Matches(lib, "#unmet"),
            $"{string.Join(" | ", asMeant.Wrong)}; found {asMeant.Found}");
        var after = ButtonsAfterText(warning);
        Check("main list banner: Go to, Show and Enable required mods right after its text", after.Ok
            && warning?.FindChild("EnableNeededButton", true, false) is Control { Visible: true }, after.Detail);
        await Capture(screen, outDir, "2b-list-banner");
        // Its Go to steps through both sides: the mods that won't load and the disabled one they need.
        if (Row(ModEntry.ModId) is { } notAbout)
            CategoryList.Select(screen, notAbout);
        var stepped = new List<string>();
        for (var i = 0; i < 4; i++)
        {
            warning?.FindChild("GoToProblemButton", true, false)?.EmitSignal(BaseButton.SignalName.Pressed);
            await Wait(menu, 0.3);
            stepped.Add(CategoryList.Selected?.manifest?.id ?? "");
        }
        Check("main list banner: Go to steps through the mods that won't load and the disabled mod they need, each once",
            stepped.Distinct().Count() == 4 && stepped.Contains("SmmTestLib") && stepped.Contains("SmmTestContent") && stepped.Contains("SmmTestUi")
            && stepped[1] == "SmmTestLib", string.Join(", ", stepped)); // a mod that needs it, then it

        // Its Show: "#unmet OR #needed" in the search box, just those rows (Go to's stops among them), the button lit;
        // pressed again, the box empty, every row back and the button not lit.
        var searchBox = screen.GetNode<LineEdit>("ModsBorder/SpireModManagerSearch");
        const string problems = "#unmet OR #needed";
        Button? Filter() => screen.FindChild(LoadOrder.WarningName, true, false)?.FindChild("FilterProblemButton", true, false) as Button;
        static bool Lit(Button? button) => button?.HasThemeStyleboxOverride("normal") == true; // Menus.ChoiceButton's chosen look
        List<string> Visible() => [.. Rows().Where(r => r.Visible).Select(r => r.Mod?.manifest?.id ?? "")];
        var unfiltered = Visible();
        Filter()?.EmitSignal(BaseButton.SignalName.Pressed);
        await Wait(menu, 0.3);
        var (filtered, matching, lit) = (Visible(), Rows().Where(r => SpireModManager.Search.Matches(r.Mod, problems)).Select(r => r.Mod?.manifest?.id ?? "").ToList(), Lit(Filter()));
        await Capture(screen, outDir, "2b-list-banner-show");
        Filter()?.EmitSignal(BaseButton.SignalName.Pressed);
        await Wait(menu, 0.3);
        Check("main list banner: its Show puts \"#unmet OR #needed\" in the box and shows just those mods (Go to's stops), lit; again, every row and not lit",
            filtered.SequenceEqual(matching) && stepped.All(filtered.Contains) && filtered.Count < Rows().Count && lit
            && searchBox.Text == "" && Visible().SequenceEqual(unfiltered) && !Lit(Filter()),
            $"shown {string.Join(", ", filtered)}; matching {string.Join(", ", matching)}; lit {lit}, then box \"{searchBox.Text}\", {Visible().Count} rows (before {unfiltered.Count}), lit {Lit(Filter())}");
        // The box holding its search typed by hand (any case, "|" for OR): lit, and pressing it empties the box. Holding
        // more than that: not lit, and pressing it puts its own search there.
        async Task Type(string text)
        {
            searchBox.Text = text;
            searchBox.EmitSignal(LineEdit.SignalName.TextChanged, text);
            await Wait(menu, 0.3);
        }
        await Type("#UNMET | #Needed");
        var (typedLit, typedRows) = (Lit(Filter()), Visible());
        Filter()?.EmitSignal(BaseButton.SignalName.Pressed);
        await Wait(menu, 0.3);
        var typedThen = searchBox.Text;
        await Type("#unmet OR #needed #enabled");
        var moreLit = Lit(Filter());
        Filter()?.EmitSignal(BaseButton.SignalName.Pressed);
        await Wait(menu, 0.3);
        var (moreThen, moreThenLit) = (searchBox.Text, Lit(Filter()));
        await Type("");
        Check("main list banner: its search typed by hand (\"#UNMET | #Needed\") lights its Show, which then empties the box; with more in the box, not lit, "
            + "and pressed it puts \"#unmet OR #needed\" there, lit",
            typedLit && typedRows.SequenceEqual(filtered) && typedThen == "" && !moreLit && moreThen == problems && moreThenLit,
            $"lit {typedLit}, rows {string.Join(", ", typedRows)}, then \"{typedThen}\"; with more lit {moreLit}, then \"{moreThen}\" lit {moreThenLit}");

        // Under the page bar's Show: Enabled, its Show still shows all its mods, the disabled one they need too (the choice
        // goes back to All). Then its Enable required mods: the mods it was about stay on screen under the search, though
        // fixed they no longer match it and the banner goes, until the search changes. Test Framework off again after.
        // Test Framework needs Test Fixes (off) meanwhile: a need's own need, which #needed lists and Enable required mods
        // turns on, though not among the banner's own mods.
        var (fixes, libNeeds) = (Row("SmmTestFixes")?.Mod, lib.manifest?.dependencies);
        var fixesOn = fixes != null && Copies.Ticked(fixes);
        if (fixes != null && lib.manifest is { } libManifest)
        {
            libManifest.dependencies = [new ModDependency("SmmTestFixes")];
            Dependencies.Set([fixes], false, screen);
        }
        ListFilter.Choose(content, ListFilter.Choice.Enabled);
        await Wait(menu, 0.3);
        Filter()?.EmitSignal(BaseButton.SignalName.Pressed);
        await Wait(menu, 0.3);
        var (choiceAfter, libShown) = (ListFilter.Current, libRow.Visible);
        // By copy (Copies.Key): a mod installed twice has a row per copy, and only one of the twins is among them.
        List<string> VisibleCopies() => [.. Rows().Where(r => r.Visible).Select(r => r.Mod is { } m ? Copies.Key(m) : "")];
        var shownThen = VisibleCopies();
        (screen.FindChild(LoadOrder.WarningName, true, false)?.FindChild("EnableNeededButton", true, false) as BaseButton)?.EmitSignal(BaseButton.SignalName.Pressed);
        await Wait(menu, 0.3);
        var (stayed, boxKept) = (shownThen.All(VisibleCopies().Contains), searchBox.Text);
        var gone = string.Join(", ", shownThen.Except(VisibleCopies()));
        var libOn = Copies.Ticked(lib);
        await Type("");
        tickbox.IsTicked = false;
        tickbox.EmitSignal(NTickbox.SignalName.Toggled, tickbox);
        await Wait(menu, 0.3);
        var fixesListed = fixes == null || shownThen.Contains(Copies.Key(fixes));
        if (lib.manifest is { } libBack)
            libBack.dependencies = libNeeds;
        if (fixes != null)
            Dependencies.Set([fixes], fixesOn, screen);
        await Wait(menu, 0.3);
        Check("main list banner: its Show under Show: Enabled shows all its mods (the choice back to All); its Enable required mods then keeps them on "
            + "screen under the search, a need's own need too", choiceAfter == ListFilter.Choice.All && libShown && libOn && stayed && fixesListed && boxKept == problems,
            $"choice {choiceAfter}, Test Framework shown {libShown}, Test Fixes {fixesListed}; after Enable: Test Framework on {libOn}, stayed {stayed} (gone {gone}), box \"{boxKept}\"");

        // A mod fixed by hand under it keeps its row through a redraw (Test Framework ticked: no longer needed), counted,
        // while the mods it fixed go; until the search changes. Unticked after, as it was.
        Filter()?.EmitSignal(BaseButton.SignalName.Pressed);
        await Wait(menu, 0.3);
        tickbox.IsTicked = true;
        tickbox.EmitSignal(NTickbox.SignalName.Toggled, tickbox);
        await Wait(menu, 0.3);
        CategoryList.Apply(content);
        await Wait(menu, 0.3);
        string Scope() => (screen.FindChild(PageBar.Name, true, false)?.FindChild("Scope", true, false) as Label)?.Text ?? "";
        var (kept, fixedGone, keptScope, keptCount) = (libRow.Visible, Row("SmmTestContent") is { Visible: false }, Scope(), Visible().Count);
        searchBox.Text = problems + " ";
        searchBox.EmitSignal(LineEdit.SignalName.TextChanged, searchBox.Text);
        await Wait(menu, 0.3);
        var spaced = libRow.Visible; // the same search: a space more doesn't change it
        searchBox.Text = "#needed OR #unmet";
        searchBox.EmitSignal(LineEdit.SignalName.TextChanged, searchBox.Text);
        await Wait(menu, 0.3);
        Check("a mod fixed by hand under Show keeps its row, counted, through a redraw, as the mods it fixed go; it goes once the search changes",
            Copies.Ticked(lib) && kept && fixedGone && keptScope == $"Search matches ({keptCount})" && spaced && !libRow.Visible,
            $"kept {kept}, Test Content gone {fixedGone}, \"{keptScope}\", with a space {spaced}, changed {libRow.Visible}");
        tickbox.IsTicked = false;
        tickbox.EmitSignal(NTickbox.SignalName.Toggled, tickbox);
        await Wait(menu, 0.3);
        SpireModManager.Search.Clear(content);
        await Wait(menu, 0.3);

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
            .Where(m => Copies.Ticked(m) && (NoteOf(m).Contains("(disabled)") || NoteOf(m).Contains("(not installed)"))).ToList();
        var flagged = flaggedMods.Select(Shown).Order().ToList();
        var card = await BannerCard(menu, screen, screen.FindChild(LoadOrder.WarningName, true, false), "2j-banner-card", outDir);
        var named = card.Skip(1).Select(l => l.Split(": requires ")[0]).Order().ToList();
        Check("main list banner: hovering its text, a card with each mod that won't load and what it needs, by the names the list shows, nothing else",
            card.FirstOrDefault() == "Affected mods" && card.Contains("Content: requires Framework (disabled)") && card.Contains("Test UI: requires Framework (disabled)")
            && named.SequenceEqual(flagged) && !card.Any(l => l.Contains("Test Framework") || l.StartsWith("Test Content")),
            $"{string.Join(" | ", card)}; rows saying so: {string.Join(", ", flagged)}");
        // Each line gives its row's reasons: "(off)" there is "(disabled)" here, "(not installed)" the same. The fixture's
        // Test Addon (on) needs SmmTestMissing, which isn't installed.
        string? LineOf(Mod mod) => card.Skip(1).FirstOrDefault(l => l.StartsWith(Shown(mod) + ": requires "));
        var wrong = flaggedMods.Where(m => LineOf(m) is not { } line || NoteOf(m).Contains("(disabled)") != line.Contains("(disabled)")
            || NoteOf(m).Contains("(not installed)") != line.Contains("(not installed)")).Select(Shown).ToList();
        Check("main list banner's card: each line's reasons are its row's (\"(disabled)\", \"(not installed)\")", wrong.Count == 0,
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
            var open = BannerCardLines(screen).Contains("Test UI: requires Framework (disabled)");
            uiBox.IsTicked = false;
            uiBox.EmitSignal(NTickbox.SignalName.Toggled, uiBox);
            await Wait(menu, 0.3);
            var stale = BannerCardLines(screen).Any(l => l.StartsWith("Test UI"));
            Hover.TestMouse = null;
            var next = await BannerCard(menu, screen, screen.FindChild(LoadOrder.WarningName, true, false));
            Check("a tick while the banner's card shows: no stale card, and the next leaves out the mod ticked off",
                open && !stale && next.Contains("Content: requires Framework (disabled)") && !next.Any(l => l.StartsWith("Test UI")),
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
        Check("Enable required mods turns it on, banner says no more", Copies.Ticked(lib) && tickbox.IsTicked
            && screen.FindChild("EnableNeededButton", true, false) == null
            && (screen.FindChild("OrderProblems", true, false) as Label)?.Text.Contains("is disabled") != true);
        History.Undo();
        await Wait(menu, 0.3);
        Check("one undo turns it off again", !Copies.Ticked(lib));
        tickbox.IsTicked = true;
        tickbox.EmitSignal(NTickbox.SignalName.Toggled, tickbox);
        await Wait(menu, 0.3);

        var badge = screen.GetNodeOrNull<Control>("SpireModManagerLoadOrderToggle/WarningBadge");
        Check("no order problem, no '!' on Load Order", badge is not { Visible: true });
        LoadOrder.Move(dependent, LoadOrder.Mods(Rows()).IndexOf(lib), Rows());
        CategoryList.Redraw(content);
        await Wait(menu, 0.3);
        badge = screen.GetNodeOrNull<Control>("SpireModManagerLoadOrderToggle/WarningBadge");
        var badgeTip = Hover.TipOf(screen.GetNode<Control>("SpireModManagerLoadOrderToggle"));
        Check("a bad order puts a '!' with the warning on the Load Order button, the warning alone (Load Order's card names the mods)",
            badge is { Visible: true } && badgeTip.StartsWith("For compatibility, 1 mod ") && !badgeTip.Contains('\n'), badgeTip);
        await Capture(screen, outDir, "2c-load-order-badge");
        LoadOrder.FixOrder(Rows());
        CategoryList.Redraw(content);
        await Wait(menu, 0.3);
        Check("fixed: the '!' goes", screen.GetNodeOrNull<Control>("SpireModManagerLoadOrderToggle/WarningBadge") is not { Visible: true });

        // The banner every run can raise: Test Addon on needs a mod that isn't installed, so with it off the banner goes,
        // and its tick brings the banner back (TestBannersKeepRows, the managers banner's checks without Better Mod Menu).
        // On again after, as it started.
        if (WontLoad() is { } wontLoad && Rows().FirstOrDefault(r => r.Mod == wontLoad) is { } addonRow)
        {
            var addonBox = addonRow.GetNode<NTickbox>("Tickbox");
            addonBox.IsTicked = false;
            addonBox.EmitSignal(NTickbox.SignalName.Toggled, addonBox);
            await Wait(menu, 0.3);
            await TestBannersKeepRows(menu, screen, content, addonRow, LoadOrder.WarningName, "main list banner");
            if (!Copies.Ticked(wontLoad))
            {
                addonBox.IsTicked = true;
                addonBox.EmitSignal(NTickbox.SignalName.Toggled, addonBox);
                await Wait(menu, 0.3);
            }
        }
        else
            Results.Add("SKIP main list banner keeping the rows in place: Test Addon not installed, or off");
    }

    // Better Mod Menu ticked as well as ours: a yellow banner on our views, above the problem banners, with a Go to that
    // goes to its row, a Show that searches for the managers, and a Dismiss that hides it until the two aren't both
    // ticked. It comes and goes with its tickbox at
    // once (no relaunch). The fixture has it installed but off; with --bmm it's on and loaded, so the banner is there from
    // the start. It ends as it started.
    // This mod among the others: the counts count it; installed twice, its boxes are locked on (a click puts one back),
    // the panel says which copy runs, and there's no Same ID warning for it; a Disable all that covers it says it stays
    // on; and installed once and unticked on its own row, a red banner above everything says it (and this page) goes at
    // the relaunch, with Go to (its row) and Enable now. Enable now, and undo, take the banner away.
    static async Task TestSelfIncluded(Node menu, NModdingScreen screen, Control content, string outDir)
    {
        if (Layout.Current != Layout.View.Categories || content.GetChildren().OfType<NModMenuRow>().FirstOrDefault(r => r.Mod?.manifest?.id == ModEntry.ModId)
            is not { Mod: { } self } row)
        {
            Results.Add("SKIP this mod among the others: not on the main list, or its row not found");
            return;
        }
        var border = screen.GetNode<Control>("ModsBorder");
        var search = border.GetNode<Control>("SpireModManagerSearch");
        Control? Banner() => border.GetNodeOrNull<Control>(LoadOrder.SelfName);
        // Mods on now, by id: two copies of one mod counted once (as the counts count them).
        int OnNow() => ModManager.Mods.Union(Installed.Mods).Where(m => m.manifest?.id != null && Copies.Ticked(m))
            .Select(m => m.manifest?.id).Distinct().Count();
        string Summary() => (screen.GetNodeOrNull<Control>(HistoryBar.ChangesName) as RichTextLabel)?.GetParsedText() ?? "";
        bool ProfileCountShown() => Profiles.Active is { } active && Profiles.ModCount(active) == Profiles.CountText();
        var on = OnNow();
        Check("the counts count this mod: the profile bar's, the main menu button's and Mods enabled's", Copies.Ticked(self)
            && Profiles.OnCount() == on && ProfileCountShown() && Summary().StartsWith($"Mods enabled {Menus.Number(on)} "),
            $"{on} on: profile {Profiles.OnCount()}, summary \"{Summary().Split('\n')[0]}\"");
        // Installed twice (a Workshop copy beside this local one): both on, as the game loads one, the newer, and the
        // counts name it once (#96: they counted each copy).
        var copy = new Mod
        {
            path = "", modSource = ModSource.SteamWorkshop, state = ModLoadState.AddedAtRuntime,
            manifest = new ModManifest { id = ModEntry.ModId, name = "Spire Mod Manager", version = "0.1.0" },
        };
        Compat.SetWorkshopId(copy, 3600000001);
        string Named(Mod mod) => (Store.InfoFor(mod)?.Alias ?? mod.manifest?.name ?? "") + Copies.Which(mod);
        NewModPopupPatch.TestReset();
        NModalContainer.Instance?.Clear();
        GameInstalls(copy);
        await Wait(menu, 0.4);
        var popup = string.Join(" | ", PopupTexts());
        NModalContainer.Instance?.Clear();
        CategoryList.Redraw(content);
        await Wait(menu, 0.2);
        Check("installed twice: both copies on, every count names it once; the new copy's popup says both stay enabled, and which loads", Copies.Ticked(copy)
            && Profiles.OnCount() == on && ProfileCountShown() && Summary().StartsWith($"Mods enabled {Menus.Number(on)} ")
            && popup.Contains(Draft(NewSelfCopy, ("loads", Named(self)))),
            $"copy on {Copies.Ticked(copy)}; {on} on before: profile {Profiles.OnCount()}, summary \"{Summary().Split('\n')[0]}\"; {popup}");
        // Its boxes locked on our page while it's installed twice (removing the copy that runs still leaves one that loads):
        // each disabled, white again, our padlock on it, its tip saying why and how to turn it off; unticking either (a click,
        // or the controller's hotkey, which ignores a disabled box) puts it back: every entry of ours on, no undo step, no
        // red banner. The panel says which copy runs and that the other stays on; no Same ID banner or yellow chip (both on
        // is as it should be). Disable all keeps both on.
        var ourRows = content.GetChildren().OfType<NModMenuRow>().Where(r => r.Mod?.manifest?.id == ModEntry.ModId).ToList();
        string Box(NModMenuRow r) => r.GetNode<NTickbox>("Tickbox") is var box
            ? $"{Copies.KeyOf(r.Mod)} ticked {box.IsTicked}, enabled {box.IsEnabled}, {box.Modulate}, padlock {r.GetNodeOrNull<Control>("SpireModManagerSelfLock")?.Visible}, "
                + $"tip {Hover.TipOf(box)}" : "";
        bool Locked(NModMenuRow r) => r.GetNode<NTickbox>("Tickbox") is { IsTicked: true, IsEnabled: false } box && box.Modulate == Colors.White
            && Hover.TipOf(box) == SelfLockedTip && r.GetNodeOrNull<Control>("SpireModManagerSelfLock") is { Visible: true };
        // The padlock on each box's corner, the same on both rows (it follows the box: a new row's is scaled after it's locked).
        var lockPlaces = ourRows.Select(r => r.GetNodeOrNull<Control>("SpireModManagerSelfLock") is { } padlock
            ? (padlock.Position - r.GetNode<Control>("Tickbox").Position).Round() : new Vector2(-1, -1)).Distinct().ToList();
        var locked = ourRows.Count == 2 && ourRows.All(Locked) && lockPlaces.Count == 1 && lockPlaces[0] != new Vector2(-1, -1);
        var lockedSay = string.Join(" | ", ourRows.Select(Box)) + $"; padlocks at {string.Join(", ", lockPlaces)} from their boxes";
        if (ourRows.FirstOrDefault() is { } shownRow)
            CategoryList.ScrollTo(content, shownRow); // our rows in view for the screenshot
        await Wait(menu, 0.2);
        await Capture(screen, outDir, "2n-self-twice-locked");
        var stepsTwice = History.Steps(History.Track.List);
        var keptOn = new List<bool>();
        foreach (var ourRow in ourRows)
        {
            var box = ourRow.GetNode<NTickbox>("Tickbox");
            box.IsTicked = false;
            box.EmitSignal(NTickbox.SignalName.Toggled, box);
            await Wait(menu, 0.3);
            keptOn.Add(box.IsTicked && (SaveManager.Instance.SettingsSave.ModSettings?.ModList.Where(e => e.Id == ModEntry.ModId).All(e => e.IsEnabled) ?? false)
                && History.Steps(History.Track.List) == stepsTwice && Banner() == null && ourRows.All(Locked));
        }
        Check("installed twice: both boxes locked on our page (disabled, white, our padlock, the tip saying why); unticking either puts it back: every entry "
            + "of ours on, no undo step, no red banner", locked && keptOn.Count == 2 && keptOn.All(k => k), $"{lockedSay}; kept on {string.Join(", ", keptOn)}");
        var lines = (Running: ModDetails.CopyLine(self, false)?.Text, Other: ModDetails.CopyLine(copy, false)?.Text);
        Check("... the copy running says the other stays enabled, in case it's removed; the other says which copy runs",
            lines == (Draft(SelfRuns, ("other", Named(copy))), Draft(SelfBackup, ("other", Named(self)))), $"{lines.Running} / {lines.Other}");
        // The backup copy failed by the game as a duplicate (made up: two copies of ours from one source, the second fails):
        // grey "Won't load (same ID)" all the same, not a red "Failed to load" (it's kept on by design, and won't load).
        var (stateWas, errorsWas, backupStatus) = (copy.state, copy.errors, ModDetails.Status(copy));
        copy.state = ModLoadState.Failed;
        copy.errors = [new LocString("main_menu_ui", "MOD_ERROR.DUPLICATE_ID")];
        var failedStatus = ModDetails.Status(copy);
        (copy.state, copy.errors) = (stateWas, errorsWas);
        Check("... the backup copy failed by the game as a duplicate (made up): grey Won't load (same ID) as before, not a red Failed to load",
            backupStatus.Contains(StatusWontLoad) && failedStatus == backupStatus, $"{backupStatus} / failed {failedStatus}");
        // The other copy unticked (as the vanilla page does: the game's switch for it): this one's locked box says Spire Mod
        // Manager won't load if this copy is removed, and to enable the other too, not that the other still loads. Put back.
        var settings = SaveManager.Instance.SettingsSave.ModSettings;
        var copyEntries = settings?.ModList.Where(e => e.Id == ModEntry.ModId && e.Source == ModSource.SteamWorkshop).ToList() ?? [];
        if (copyEntries.Count == 0 && settings != null)
        {
            copyEntries.Add(new SettingsSaveMod { Id = ModEntry.ModId, Source = ModSource.SteamWorkshop });
            settings.ModList.Add(copyEntries[0]);
        }
        foreach (var entry in copyEntries)
            entry.IsEnabled = false;
        CategoryList.Redraw(content);
        await Wait(menu, 0.2);
        var otherOffTip = Draft(SelfLockedOtherOffTip, ("other", Named(copy)));
        var (offTip, offBox, copyTicked) = (CategoryList.CopyTip(self), Hover.TipOf(row.GetNode<NTickbox>("Tickbox")), Copies.Ticked(copy));
        foreach (var entry in copyEntries)
            entry.IsEnabled = true;
        CategoryList.Redraw(content);
        await Wait(menu, 0.2);
        Check("... the other copy unticked (as the vanilla page does): this one's locked box says Spire Mod Manager won't load if this copy is removed, to "
            + "enable the other too; ticked again, the lock's own tip", !copyTicked && offTip == otherOffTip && offBox == otherOffTip && ourRows.All(Locked),
            $"other ticked {copyTicked}; tip {offTip}; box {offBox}; then {string.Join(" | ", ourRows.Select(Box))}");
        var ourChips = new[] { self, copy }.Select(m => Tags.For(m).FirstOrDefault(c => c.Text == "Same ID")).ToList();
        Check("... no Same ID banner or yellow chip for this mod: both on is as it should be",
            !Copies.Clashing().Any(m => m.manifest?.id == ModEntry.ModId) && border.GetNodeOrNull<Control>(LoadOrder.SameIdName) == null
            && ourChips.All(c => c != null && c.Color != Tags.SameIdClash), string.Join(" / ", ourChips.Select(c => $"{c?.Color} {c?.Tip}")));
        if (border.FindChild(PageBar.Name, true, false)?.FindChildren("*", "Button", true, false).OfType<Button>().FirstOrDefault(b => b.Text == "Disable all") is { } disableAll)
        {
            disableAll.EmitSignal(BaseButton.SignalName.Pressed);
            await Wait(menu, 0.3);
            var bothStay = Copies.Ticked(self) && Copies.Ticked(copy) && ourRows.All(Locked);
            History.Undo();
            await Wait(menu, 0.3);
            Check("... Disable all keeps both on (undone)", bothStay && Copies.Ticked(self) && Copies.Ticked(copy), string.Join(" | ", ourRows.Select(Box)));
        }
        else
            Check("... Disable all keeps both on: the page bar's Disable all", false);
        // Gone again, its row too (the game made one as it announced it; a later search would find it).
        if (content.GetChildren().OfType<NModMenuRow>().FirstOrDefault(r => r.Mod == copy) is { } copyRow)
        {
            content.RemoveChild(copyRow);
            copyRow.QueueFree();
        }
        GameForgets(copy);
        SaveManager.Instance.SettingsSave.ModSettings?.ModList.RemoveAll(e => e.Id == ModEntry.ModId && e.Source == ModSource.SteamWorkshop);
        Profiles.Track();
        CategoryList.Redraw(content);
        await Wait(menu, 0.2);

        // Disable all, where this mod is among the rows it covers: the page bar's and its category's say it stays on.
        var pageDisable = border.FindChild(PageBar.Name, true, false)?.FindChildren("*", "Button", true, false).OfType<Button>()
            .FirstOrDefault(b => b.Text == "Disable all");
        var header = content.GetChildren().OfType<Control>().FirstOrDefault(c => c.IsInGroup("SpireModManagerCategoryHeader")
            && c.GetIndex() < row.GetIndex() && content.GetChildren().OfType<Control>().Skip(c.GetIndex() + 1).TakeWhile(n => !n.IsInGroup("SpireModManagerCategoryHeader")).Contains(row));
        var headerDisable = header?.FindChildren("*", "Button", true, false).OfType<Button>().FirstOrDefault(b => b.Text == "Disable all");
        var otherHeader = content.GetChildren().OfType<Control>().Where(c => c.IsInGroup("SpireModManagerCategoryHeader") && c != header)
            .Select(c => c.FindChildren("*", "Button", true, false).OfType<Button>().FirstOrDefault(b => b.Text == "Disable all")).OfType<Button>().FirstOrDefault();
        const string keeps = "Spire Mod Manager (this mod) will remain enabled";
        Check("Disable all, where this mod is among them, says it stays on (the page bar's, its category's); another category's says nothing",
            pageDisable != null && Hover.TipOf(pageDisable) == keeps && headerDisable != null && Hover.TipOf(headerDisable) == keeps
            && (otherHeader == null || Hover.TipOf(otherHeader) == ""),
            $"page \"{(pageDisable == null ? "none" : Hover.TipOf(pageDisable))}\", its header \"{(headerDisable == null ? "none" : Hover.TipOf(headerDisable))}\", "
            + $"another \"{(otherHeader == null ? "none" : Hover.TipOf(otherHeader))}\"");

        // Unticked on its own row (the game's tickbox, as a player would): the red banner, first under the search box.
        var tickbox = row.GetNode<NTickbox>("Tickbox");
        tickbox.IsTicked = false;
        tickbox.EmitSignal(NTickbox.SignalName.Toggled, tickbox);
        await Wait(menu, 0.3);
        var text = Banner()?.FindChild("SelfOff", true, false) is Label label ? label.Text : "";
        var first = Banner() is { Visible: true } banner && banner.GetGlobalRect().Position.Y >= search.GetGlobalRect().End.Y
            && new[] { LoadOrder.ManagersName, LoadOrder.WarningName }.Select(n => border.GetNodeOrNull<Control>(n)).OfType<Control>()
                .All(other => !other.Visible || other.GetGlobalRect().Position.Y >= banner.GetGlobalRect().End.Y);
        Check("this mod unticked: a red banner, first under the search box, says it and this page go at the relaunch, with Go to (no Show: one mod) and Enable now",
            !Copies.Ticked(self) && first && text == "Spire Mod Manager (this mod) will be disabled on relaunch, and this menu will no longer be accessible until enabled again."
            && Banner()?.FindChild("GoToSelfButton", true, false) is Button && Banner()?.FindChild("EnableSelfButton", true, false) is Button enableButton
            && Hover.TipOf(enableButton) == "Keep Spire Mod Manager (this mod) enabled" && Profiles.OnCount() == on - 1
            && Banner()?.FindChild("GoToSelfButton", true, false) is Button goToButton && goToButton.Text == GoToProblem
            && Hover.TipOf(goToButton) == "Open Spire Mod Manager (this mod)" && Banner()?.FindChildren("*", "Button", true, false).Count == 2,
            $"on {Copies.Ticked(self)}, first {first}, text \"{text}\", count {Profiles.OnCount()}");
        await Capture(screen, outDir, "2k-self-banner");
        if (content.GetChildren().OfType<NModMenuRow>().FirstOrDefault(r => r != row) is { } elsewhere)
            CategoryList.Select(screen, elsewhere);
        Banner()?.FindChild("GoToSelfButton", true, false)?.EmitSignal(BaseButton.SignalName.Pressed);
        await Wait(menu, 0.5);
        Check("its Go to goes to this mod's row", CategoryList.Selected == self, CategoryList.Selected?.manifest?.id ?? "none");
        Banner()?.FindChild("EnableSelfButton", true, false)?.EmitSignal(BaseButton.SignalName.Pressed);
        await Wait(menu, 0.3);
        Check("Enable now turns it on and takes the banner away", Copies.Ticked(self) && tickbox.IsTicked && Banner() == null && Profiles.OnCount() == on,
            $"on {Copies.Ticked(self)}, ticked {tickbox.IsTicked}, banner {Banner() != null}");
        tickbox.IsTicked = false;
        tickbox.EmitSignal(NTickbox.SignalName.Toggled, tickbox);
        await Wait(menu, 0.3);
        var shown = Banner() != null;
        History.Undo();
        await Wait(menu, 0.3);
        Check("unticked again, then undone: on, and no banner", shown && Copies.Ticked(self) && Banner() == null, $"shown {shown}, on {Copies.Ticked(self)}");
    }

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
            && text == "Multiple mod managers are currently enabled, and may cause unexpected behavior.", $"\"{text}\"; {Where()}");
        Check("the managers banner is yellow: a warning, not an error", Yellow(BannerFill(Banner())), $"fill {BannerFill(Banner())}");
        var goToTip = TipOf(Banner()?.FindChild("GoToManagersButton", true, false));
        Check("the managers banner's Go to tip is the other banner's: \"Open the next affected mod\"", goToTip == GoToProblemTip, goToTip ?? "no Go to");
        Check("the managers banner names no manager: no \"Better Mod Menu\" in its text or tips",
            BannerWords(Banner()) is { Count: > 0 } words && !words.Any(w => w.Contains("Better Mod Menu")), string.Join(" | ", BannerWords(Banner())));
        // Hovering its text: a card naming the mod managers that are on, by the names the list gives them (ours by the
        // fixture's alias), and nothing else.
        var managers = string.Join(" | ", await BannerCard(menu, screen, Banner()));
        var bmmShown = Store.InfoFor(Bmm.ModId)?.Alias ?? bmm.manifest?.name;
        Check("the managers banner's card: the managers that are on, ours by its alias (\"The Manager\"), then Better Mod Menu, nothing else",
            Store.InfoFor(ModEntry.ModId)?.Alias == "The Manager" && managers == $"Enabled mod managers | The Manager | {bmmShown}", managers);
        var after = ButtonsAfterText(Banner());
        Check("main list: the managers banner's Go to, Show and Dismiss right after its text", after.Ok, after.Detail);
        await Capture(screen, outDir, "2e-managers-banner");

        // A narrow list: the text wraps in what the buttons leave, they follow it, the banner no wider than the list (a
        // line too full widens it past the list's edge instead), and it grows to fit with the list still below it. Then
        // back to its width: one line, the usual height.
        const float narrower = 450;
        var wording = Banner()?.FindChild("ManagersWarning", true, false) as Label;
        border.OffsetRight -= narrower;
        await Wait(menu, 0.3);
        after = ButtonsAfterText(Banner());
        Check("a narrow list: the managers text wraps, Go to, Show and Dismiss right after it inside the list's width, the banner taller, the list below it",
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
        {
            CategoryList.Select(screen, other);
            await PanelFor(menu, screen, other);
        }
        await Wait(menu, 0.2);
        int Glows() => row.GetChildren().OfType<ColorRect>().Count(c => c.Color.R > 0.9f && c.Color.B < 0.4f); // CategoryList's gold flash
        var (hidden, glows) = (!row.Visible, Glows());
        Banner()?.FindChild("GoToManagersButton", true, false)?.EmitSignal(BaseButton.SignalName.Pressed);
        await Wait(menu, 0.5);
        Check("its Go to opens Better Mod Menu's category, scrolls to its row with a glow and shows it in the panel",
            hidden && row.Visible && list.GetGlobalRect().Intersects(row.GetGlobalRect()) && Glows() > glows && CategoryList.Selected == bmm,
            $"hidden before {hidden}, shown {row.Visible}, row {row.GetGlobalRect()}, list {list.GetGlobalRect()}, glows {glows} -> {Glows()}, panel {CategoryList.Selected?.manifest?.id}");
        // Pressed again: this mod's row, the other manager, then back to Better Mod Menu.
        Banner()?.FindChild("GoToManagersButton", true, false)?.EmitSignal(BaseButton.SignalName.Pressed);
        await Wait(menu, 0.5);
        var secondStop = CategoryList.Selected?.manifest?.id;
        Banner()?.FindChild("GoToManagersButton", true, false)?.EmitSignal(BaseButton.SignalName.Pressed);
        await Wait(menu, 0.5);
        Check("its Go to, pressed again, goes to this mod's row, then back to Better Mod Menu's", secondStop == ModEntry.ModId && CategoryList.Selected == bmm,
            $"{secondStop}, then {CategoryList.Selected?.manifest?.id}");

        // Its Show: "#managers" in the box, just the two managers' rows, lit; pressed again, every row.
        var searchBox = border.GetNode<LineEdit>("SpireModManagerSearch");
        Button? Filter() => Banner()?.FindChild("FilterManagersButton", true, false) as Button;
        Filter()?.EmitSignal(BaseButton.SignalName.Pressed);
        await Wait(menu, 0.3);
        var (managersBox, managerRows, managersLit) = (searchBox.Text, Rows().Where(r => r.Visible).Select(r => r.Mod).ToList(),
            Filter()?.HasThemeStyleboxOverride("normal") == true);
        Filter()?.EmitSignal(BaseButton.SignalName.Pressed);
        await Wait(menu, 0.3);
        Check("its Show: \"#managers\" in the box, just the two managers' rows, lit; pressed again, the box empty and not lit",
            managersBox == "#managers" && managerRows.Count == 2 && managerRows.Contains(bmm) && managerRows.Any(m => m?.manifest?.id == ModEntry.ModId) && managersLit
            && searchBox.Text == "" && Filter()?.HasThemeStyleboxOverride("normal") == false,
            $"box \"{managersBox}\", rows {string.Join(", ", managerRows.Select(m => m?.manifest?.id))}, lit {managersLit}; then \"{searchBox.Text}\"");
        var asMeant = HiddenTagsAsMeant(content);
        Check("both managers ticked: hidden tags on the list as it is (both #managers and #warning), each finding the rows it means",
            asMeant.Wrong.Count == 0 && SpireModManager.Search.Matches(bmm, "#managers") && SpireModManager.Search.Matches(bmm, "#warning")
            && Rows().Count(r => SpireModManager.Search.Matches(r.Mod, "#managers")) == 2, $"{string.Join(" | ", asMeant.Wrong)}; found {asMeant.Found}");

        // Go to while a search leaves its row out (main list and Load Order): the search clears, box and list, and the
        // row shows as above. On the main list its category is closed again too.
        async Task ShowThroughSearch(string view)
        {
            const string query = "SmmTest"; // the test mods, not Better Mod Menu
            searchBox.Text = query;
            searchBox.EmitSignal(LineEdit.SignalName.TextChanged, query);
            await Wait(menu, 0.3);
            if (Rows().FirstOrDefault(r => r != row && r.Visible) is { } match)
            {
                CategoryList.Select(screen, match);
                await PanelFor(menu, screen, match);
            }
            await Wait(menu, 0.2);
            var left = !row.Visible && !SpireModManager.Search.Matches(bmm, query);
            var old = row.GetChildren().OfType<ColorRect>().ToList(); // an earlier glow may still be fading
            Banner()?.FindChild("GoToManagersButton", true, false)?.EmitSignal(BaseButton.SignalName.Pressed);
            await Wait(menu, 0.5);
            var filter = content.HasMeta(CategoryList.FilterMeta) ? (string)content.GetMeta(CategoryList.FilterMeta) : "";
            var glowed = row.GetChildren().OfType<ColorRect>().Except(old).Any(c => c.Color.R > 0.9f && c.Color.B < 0.4f);
            Check($"{view}: with a search that hides Better Mod Menu's row, its Go to clears the search and shows the row, scrolled to with a glow and in the panel",
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
        Check("Load Order: the managers banner's Go to, Show and Dismiss right after its text", after.Ok, after.Detail);
        // Load Order finds rather than filters (FindBar): its Show leaves every row in place, the find bar counting the two
        // managers, the first in the panel.
        Filter()?.EmitSignal(BaseButton.SignalName.Pressed);
        await Wait(menu, 0.5);
        var found = (border.GetNodeOrNull(FindBar.Name)?.GetNodeOrNull<Label>("Count"))?.Text;
        var (allShown, firstFound) = (Rows().All(r => r.Visible), CategoryList.Selected);
        SpireModManager.Search.Clear(content);
        await Wait(menu, 0.2);
        Check("Load Order: the managers banner's Show finds the two managers (\"2 matches\"), every row in place, the first in the panel",
            found == "2 matches" && allShown && (firstFound == bmm || firstFound?.manifest?.id == ModEntry.ModId), $"\"{found}\", all shown {allShown}, panel {firstFound?.manifest?.id}");
        // The same search leaves Better Mod Menu's row in place, and Go to goes to it with the search kept.
        const string notBmm = "SmmTest";
        searchBox.Text = notBmm;
        searchBox.EmitSignal(LineEdit.SignalName.TextChanged, notBmm);
        await Wait(menu, 0.3);
        var stays = row.Visible && !SpireModManager.Search.Matches(bmm, notBmm);
        Banner()?.FindChild("GoToManagersButton", true, false)?.EmitSignal(BaseButton.SignalName.Pressed);
        await Wait(menu, 0.5);
        Check("Load Order: a search leaves Better Mod Menu's row in place; the banner's Go to goes to it, the search kept",
            stays && row.Visible && searchBox.Text == notBmm && list.GetGlobalRect().Intersects(row.GetGlobalRect()) && CategoryList.Selected == bmm,
            $"in place {stays}, box \"{searchBox.Text}\", row {row.GetGlobalRect()}, list {list.GetGlobalRect()}, panel {CategoryList.Selected?.manifest?.id}");
        SpireModManager.Search.Clear(content);
        await Wait(menu, 0.2);
        Click(screen.GetNode<NButton>("SpireModManagerLoadOrderToggle"));
        await Wait(menu, 0.5);
        Check("Vanilla View: no managers banner", await WithVanillaView(screen, menu, () => Banner() is not { Visible: true }));

        // Dismiss, after Go to and Show: the banner goes and leaves no gap (the next thing under the search box, the page's
        // own warning, the page bar or the list, right under it). View state: no undo step, not a change or pending, saved.
        bool Buttons() => Banner()?.FindChild("GoToManagersButton", true, false) is Button { Visible: true } goTo
            && Filter() is { Visible: true } show && show.GetGlobalRect().Position.X > goTo.GetGlobalRect().End.X
            && Banner()?.FindChild("DismissManagersButton", true, false) is Button { Visible: true } dismiss
            && dismiss.GetGlobalRect().Position.X > show.GetGlobalRect().End.X && Hover.TipOf(dismiss).StartsWith("Dismiss the warning");
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
        Check("the managers banner has Go to, Show, then Dismiss with its tip", Buttons(), Where());
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
        Check("ticked again: the banner is back, with its Go to, Show and Dismiss", Placed() && Buttons(), Where());

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
            Check("one undo turns both back on, and the banner is back", Bmm.Ticked && Copies.Ticked(libMod) && Placed() && Buttons(), Where());
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
        await TestBannersKeepRows(menu, screen, content, row, LoadOrder.ManagersName, "managers banner");
        if (!was)
            return;
        await Tick(tickbox, true);
        Check("ticked again, as it started: the banner is back", Banner() is { Visible: true }, Where());
    }

    // Banners coming and going with the list scrolled part way: the rows on screen stay where they were in every frame
    // (Layout.KeepRows), through a tick that raises one, undo and redo, and (the managers banner's) Dismiss; a redraw
    // that makes a wrapped banner again moves nothing, at the list's top too. The page hidden, a banner coming or going
    // keeps the rows in place too, but a list at its top stays there. Reveal still lands where it aimed with a banner
    // coming as it eases, and a Show: choice with one coming in its frame still starts the list at its top. A spacer
    // under the rows gives the short fixture list room to scroll; it goes after. bannerRow: the row whose tick raises the
    // banner called name: Test Addon's (the main list banner, in every run) or Better Mod Menu's (the managers banner,
    // where it's installed); what: that banner in the checks' names. Starts and ends with that row's mod off. Showing the
    // page again enters it anew (History.Enter: Revert all's state then, no undo steps), so the page's opening state and
    // both undo histories are kept first and put back after: the tests after it undo and Revert all as they would have.
    static async Task TestBannersKeepRows(Node menu, NModdingScreen screen, Control content, NModMenuRow bannerRow, string name, string what)
    {
        var border = screen.GetNode<Control>("ModsBorder");
        var list = border.GetNode<Control>("ModsScrollContainer");
        var tickbox = bannerRow.GetNode<NTickbox>("Tickbox");
        Control? Banner() => border.GetNodeOrNull<Control>(name);
        if (bannerRow.Mod is not { } bannerMod || Layout.Current != Layout.View.Categories || Copies.Ticked(bannerMod) || Banner() != null
            || ListFilter.Chosen != ListFilter.Choice.All || CategoryList.Filter(content).Length > 0)
        {
            Results.Add($"SKIP {what} keeping the rows in place: not on the main list showing every mod, its mod on, or the banner up without it");
            return;
        }
        bool On() => Copies.Ticked(bannerMod);
        float Target() => (float)list.Get("_targetDragPosY");
        float Padding() => (float)list.Get("_paddingTop");
        async Task ScrollTo(float target)
        {
            list.Set("_targetDragPosY", target);
            await WaitReal(menu, 0.6);
        }
        void Tick(bool on)
        {
            tickbox.IsTicked = on;
            tickbox.EmitSignal(NTickbox.SignalName.Toggled, tickbox);
        }
        // After act, over the next frames (each as drawn): how far the row strayed from where it was, at most, and how
        // far the list's top moved.
        async Task<(float Strayed, float Moved)> Watch(Control row, Action act)
        {
            var (y, top) = (row.GetGlobalRect().Position.Y, list.GetGlobalRect().Position.Y);
            act();
            var strayed = 0f;
            for (var i = 0; i < 20; i++)
            {
                await menu.ToSignal(menu.GetTree(), SceneTree.SignalName.ProcessFrame);
                strayed = Math.Max(strayed, Math.Abs(row.GetGlobalRect().Position.Y - y));
            }
            return (strayed, list.GetGlobalRect().Position.Y - top);
        }

        var (history, listWas, changesWas) = (History.TestKeep(), History.CurrentMods(), HistoryBar.Describe());
        var spacer = new Control { Name = "SpireModManagerTestSpacer", CustomMinimumSize = new Vector2(0, 1500), MouseFilter = Control.MouseFilterEnum.Ignore };
        content.AddChild(spacer);
        const float narrower = 450;
        var narrowed = 0f;
        try
        {
            await Wait(menu, 0.1); // the rows' container grown first: scrolled past its old end, the list would spring back
            await ScrollTo(-160);
            var view = list.GetGlobalRect();
            if (content.GetChildren().OfType<NModMenuRow>().Where(r => r.IsVisibleInTree() && view.Encloses(r.GetGlobalRect()))
                .MinBy(r => Math.Abs(r.GetGlobalRect().GetCenter().Y - view.GetCenter().Y)) is not { } row)
            {
                Results.Add($"FAIL {what} keeping the rows in place: no row in view with the list scrolled part way");
                return;
            }

            var ticked = await Watch(row, () => Tick(true));
            Check($"{what}, scrolled part way: a tick that raises it moves the list's top down, the rows on screen stay where they were in every frame",
                On() && Banner() is { Visible: true } && ticked.Moved > 30 && ticked.Strayed <= 1,
                $"banner {Banner() != null}, the list's top moved {ticked.Moved}, the row strayed {ticked.Strayed}, scrolled to {Target()}");
            var undone = await Watch(row, History.Undo);
            var gone = !On() && Banner() == null;
            var redone = await Watch(row, History.Redo);
            Check($"{what}, scrolled part way: undo takes it away and redo brings it back, the rows staying where they were in every frame",
                gone && On() && Banner() is { Visible: true } && undone.Moved < -30 && redone.Moved > 30 && undone.Strayed <= 1 && redone.Strayed <= 1,
                $"gone on undo {gone}, back on redo {Banner() != null}; the list's top moved {undone.Moved}, then {redone.Moved}; the row strayed {undone.Strayed}, then {redone.Strayed}");

            // The page hidden (the main menu over it), its banner switched on or off with a redraw, as the relaunch check's
            // fixes do there: what the page shows once it opens again. Scrolled part way, the rows where they were; at the
            // list's top, the list still at its top, its first row as far under the list's top as before (not scrolled
            // under it by the banner's height).
            async Task<(bool Hidden, float Moved)> SwitchHidden(bool on)
            {
                var top = list.GetGlobalRect().Position.Y;
                screen.Visible = false;
                Dependencies.Set([bannerMod], on, screen);
                CategoryList.Redraw(content);
                await Wait(menu, 0.3);
                var hidden = !list.IsVisibleInTree() && (Banner() != null) == on;
                screen.Visible = true;
                await Wait(menu, 0.3);
                return (hidden, list.GetGlobalRect().Position.Y - top);
            }
            await ScrollTo(-160);
            var rowAt = row.GetGlobalRect().Position.Y;
            var hiddenGone = await SwitchHidden(false);
            var rowMoved = row.GetGlobalRect().Position.Y - rowAt;
            Check($"{what}, the page hidden and scrolled part way: the banner going there leaves the rows where they were once the page shows again",
                hiddenGone.Hidden && Banner() == null && hiddenGone.Moved < -30 && Math.Abs(rowMoved) <= 1,
                $"gone while hidden {hiddenGone.Hidden}, the list's top moved {hiddenGone.Moved}, the row {rowMoved}, scrolled to {Target()}");
            CategoryList.ScrollToTop(content);
            await Wait(menu, 0.2);
            var first = content.GetChildren().OfType<NModMenuRow>().Where(r => r.Visible).MinBy(r => r.Position.Y);
            float Under(Control r) => r.GetGlobalRect().Position.Y - list.GetGlobalRect().Position.Y;
            var under = first is { } firstRow ? Under(firstRow) : float.NaN;
            var hiddenRaised = await SwitchHidden(true);
            Check($"{what}, the page hidden at the list's top: the banner raised there leaves the list at its top once the page shows again, its first row where it was",
                hiddenRaised.Hidden && Banner() is { Visible: true } && hiddenRaised.Moved > 30 && Target() == 0
                && Math.Abs(content.Position.Y - Padding()) <= 0.5f && first != null && Math.Abs(Under(first) - under) <= 1,
                $"raised while hidden {hiddenRaised.Hidden}, the list's top moved {hiddenRaised.Moved}, scrolled to {Target()} (rows at {content.Position.Y - Padding()}), "
                + $"first row {first?.Mod?.manifest?.id} {under} under the list's top, then {(first != null ? Under(first) : float.NaN)}");

            // The managers banner's text is long enough to wrap in a narrow list, and it has a Dismiss.
            if (name == LoadOrder.ManagersName)
            {
                // Wrapped (a narrow list) and at the list's top, a redraw makes every banner again, as tall as before:
                // nothing moves. (A banner that shrank and grew back there would be kept from scrolling one way but not the
                // other.)
                border.OffsetRight -= narrower;
                narrowed = narrower;
                await Wait(menu, 0.3);
                CategoryList.ScrollToTop(content);
                await Wait(menu, 0.2);
                var wrapped = Banner()?.Size.Y > 40;
                var redrawn = await Watch(row, () => CategoryList.Redraw(content));
                Check("a redraw that makes a wrapped banner again, at the list's top: the list stays at its top, nothing moves",
                    wrapped && Target() == 0 && redrawn.Moved == 0 && redrawn.Strayed <= 1,
                    $"wrapped {wrapped} ({Banner()?.Size.Y}), scrolled to {Target()}, the list's top moved {redrawn.Moved}, the row strayed {redrawn.Strayed}");
                border.OffsetRight += narrower;
                narrowed = 0;
                await Wait(menu, 0.3);

                await ScrollTo(-160);
                var dismissed = await Watch(row, () => Banner()?.FindChild("DismissManagersButton", true, false)?.EmitSignal(BaseButton.SignalName.Pressed));
                Check("Dismiss, scrolled part way: the banner goes, the rows stay where they were in every frame",
                    Store.Data.ManagersWarningDismissed && Banner() == null && dismissed.Moved < -30 && dismissed.Strayed <= 1,
                    $"dismissed {Store.Data.ManagersWarningDismissed}, banner {Banner() != null}, the list's top moved {dismissed.Moved}, the row strayed {dismissed.Strayed}");
            }

            // Reveal from the list's top (a third of the way down the list, the spacer leaving room below), and a banner
            // coming while its scroll still eases there, its target set: the rows keep to their course, so the row still
            // ends where Reveal aimed it, a third of the way down the list as it was then (the target scrolled on by the
            // banner's height with them), in view. Unticked first (which re-arms a dismissed managers banner).
            Tick(false);
            await Wait(menu, 0.3);
            CategoryList.ScrollToTop(content);
            await Wait(menu, 0.2);
            var last = content.GetChildren().OfType<NModMenuRow>().Where(r => r.Visible).MaxBy(r => r.Position.Y);
            if (last?.Mod is { } lastMod)
            {
                var aim = Math.Min(0, list.Size.Y / 3 - last.Position.Y);
                CategoryList.Reveal(content, Copies.Key(lastMod), flash: false);
                await WaitReal(menu, 0.1); // past ScrollSoon's 0.05 s: the target set, the rows on their way
                var (aimed, easing) = (Target(), Math.Abs(content.Position.Y - Padding() - Target()) > 1);
                var (origin, top) = (content.GlobalPosition.Y - content.Position.Y, list.GetGlobalRect().Position.Y); // the rows' container at 0
                Tick(true);
                await WaitReal(menu, 0.8);
                var moved = list.GetGlobalRect().Position.Y - top;
                var off = last.GetGlobalRect().Position.Y - (origin + Padding() + aim + last.Position.Y); // from where Reveal aimed it
                Check($"{what}: Reveal, the banner coming while its scroll eases: the row still ends where Reveal aimed it, a third of the way down the list as it was, in view",
                    Banner() is { Visible: true } && aim < 0 && Math.Abs(aimed - aim) <= 1 && easing && moved > 30 && Math.Abs(Target() - (aim - moved)) <= 1
                    && Math.Abs(content.Position.Y - Padding() - Target()) <= 1 && Math.Abs(off) <= 1 && list.GetGlobalRect().Encloses(last.GetGlobalRect()),
                    $"banner {Banner() != null}, aimed at {aim} (target {aimed}, still easing {easing}), the list's top moved {moved}, scrolled to {Target()} "
                    + $"(rows at {content.Position.Y - Padding()}), the row {off} from where it was aimed, row {last.GetGlobalRect()}, list {list.GetGlobalRect()}");
            }
            else
                Results.Add($"FAIL {what}: Reveal, the banner coming while its scroll eases: no row shown");

            // A Show: choice scrolled part way, and a banner coming in that frame just after it: the list starts at its top,
            // the rows where a fresh choice puts them (a choice starts the list where it always did, for the rest of its
            // frame too). Unticked first, so the banner comes with the choice.
            Tick(false);
            await Wait(menu, 0.3);
            await ScrollTo(-160);
            var scrolled = Banner() == null && Target() < -100;
            ListFilter.Choose(content, ListFilter.Choice.Enabled);
            Tick(true);
            await Wait(menu, 0.3);
            Check($"{what}: a Show: choice scrolled part way, then the banner in that frame: the list starts at its top, the rows where a fresh choice puts them",
                scrolled && Banner() is { Visible: true } && Target() == 0 && Math.Abs(content.Position.Y - Padding()) <= 0.5f,
                $"scrolled part way, no banner first {scrolled}; banner {Banner() != null}, scrolled to {Target()}, rows at {content.Position.Y - Padding()}");
        }
        finally
        {
            border.OffsetRight += narrowed;
            content.RemoveChild(spacer);
            spacer.QueueFree();
            if (On())
                Tick(false);
            ListFilter.Choose(content, ListFilter.Choice.All); // a redraw, which shrinks the rows' container again, then the top
            await Wait(menu, 0.3);
            var enteredAnew = History.Entered != history.Entered;
            History.TestPutBack(history);
            static string Said(IEnumerable<History.ModEntry> mods) => string.Join(", ", mods.Select(m => m.Id + (m.Enabled ? "" : " (off)")));
            Check($"{what} keeping the rows in place: the saved list and Changes left as found, the page's opening state and both undo histories put back",
                History.CurrentMods().SequenceEqual(listWas) && HistoryBar.Describe().SequenceEqual(changesWas) && History.Entered == history.Entered
                && History.Steps(History.Track.List) == history.Past[History.Track.List].Count && History.Steps(History.Track.Order) == history.Past[History.Track.Order].Count,
                $"entered anew by showing the page again {enteredAnew}; list {Said(History.CurrentMods())}, was {Said(listWas)}; "
                + $"changes {string.Join(" | ", HistoryBar.Describe())}, were {string.Join(" | ", changesWas)}");
        }
    }

    // The bar under the search box: Enable all / Disable all for the page (or the search's matches),
    // Expand all / Collapse all. Category headers: the same two inline, last. Rows: the author at the end.
    static async Task TestPageBar(Node menu, NModdingScreen screen, Control content, string outDir)
    {
        List<NModMenuRow> Rows() => content.GetChildren().OfType<NModMenuRow>().ToList();
        Control? Bar() => screen.FindChild(PageBar.Name, true, false) as Control;
        void Press(string name) => Bar()?.FindChild(name, true, false)?.EmitSignal(BaseButton.SignalName.Pressed);
        Check("page bar on the main list", Bar() is { Visible: true } bar && bar.FindChild("Scope", true, false) is Label { Text: var all } && all.StartsWith("Mods installed"));
        // Each choice with how many mods it's for ("All (20)"), the same counts as the scope's for the chosen one, and
        // room for a 3-digit count (PageBar.RoomFor), so a count changing never moves the bar; the bar still fits its row.
        var filterNames = new[] { ("ShowAllButton", "All", ListFilter.Choice.All), ("ShowEnabledButton", "Enabled", ListFilter.Choice.Enabled),
            ("ShowDisabledButton", "Disabled", ListFilter.Choice.Disabled), ("ShowChangedButton", "Changed", ListFilter.Choice.Changed) };
        var filterTexts = filterNames.Select(f => (Bar()?.FindChild(f.Item1, true, false) as Button, f.Item2, Rows().Count(r => ListFilter.Matches(r.Mod, f.Item3)))).ToList();
        Check("the filters say how many mods each is for: All (n), Enabled (n), Disabled (n), Changed (n)",
            filterTexts.All(f => f.Item1?.Text == $"{f.Item2} ({Menus.Number(f.Item3)})") && filterTexts[0].Item3 == Rows().Count
            && filterTexts[1].Item3 + filterTexts[2].Item3 == Rows().Count(r => r.Mod != null),
            string.Join(" | ", filterTexts.Select(f => $"{f.Item1?.Text} / {f.Item3}")));
        Check("each filter keeps room for a 3-digit count: its width doesn't follow its count, and the bar fits its row",
            filterTexts.All(f => f.Item1 is { } b && b.CustomMinimumSize.X == PageBar.Roomy(b, f.Item2) && Math.Abs(b.Size.X - b.CustomMinimumSize.X) < 0.5f
                && b.CustomMinimumSize.X >= b.GetThemeFont("font").GetStringSize($"{f.Item2} ({new string('8', PageBar.RoomFor)})", fontSize: b.GetThemeFontSize("font_size")).X)
            && Bar() is { } fits && fits.GetCombinedMinimumSize().X <= fits.GetParent<Control>().Size.X - fits.OffsetLeft + fits.OffsetRight + 0.5f,
            $"{string.Join(", ", filterTexts.Select(f => $"{f.Item1?.Size.X:0}/{f.Item1?.CustomMinimumSize.X:0}"))}; bar {Bar()?.GetCombinedMinimumSize().X:0} in {(Bar() is { } room ? room.GetParent<Control>().Size.X - room.OffsetLeft + room.OffsetRight : 0):0}");
        await Capture(screen, outDir, "2d-page-bar");
        // What each filter shows: our hover card (not the engine's tooltip), only as wide as its words. And no engine
        // tooltip on anything of ours: every description we write is the same card. Other mods'
        // controls on the page (Better Mod Menu's row buttons) keep theirs. tools/check.cs guards the source too.
        if (Bar()?.FindChild("ShowAllButton", true, false) is Button allButton)
        {
            var (tip, width) = await SizedHoverCard(menu, allButton, Hover.TipName, () => Capture(screen, outDir, "2d2-filter-tip"));
            Check("a filter's description is our hover card, as wide as its words", tip == Menus.Text("SPIRE_MOD_MANAGER.SHOW_ALL_TIP")
                && width is > 0 and < 300 && allButton.TooltipText == "", $"\"{tip}\", {width:0} px");
        }
        bool Ours(Node node)
        {
            for (var at = node; at != null && at != screen; at = at.GetParent())
                if (at.Name.ToString().StartsWith("SpireModManager") || at.HasMeta("spire_mod_manager_tip"))
                    return true;
            return false;
        }
        var natives = screen.FindChildren("*", "Control", true, false).OfType<Control>().Where(c => c.TooltipText.Length > 0 && Ours(c))
            .Select(c => $"{c.Name}: {c.TooltipText}").ToList();
        Check("no engine tooltips on our controls: every description is our card", natives.Count == 0, string.Join(" | ", natives));

        var box = screen.GetNode<LineEdit>("ModsBorder/SpireModManagerSearch");
        void Find(string text)
        {
            box.Text = text;
            box.EmitSignal(LineEdit.SignalName.TextChanged, text);
        }
        Find("#Gameplay");
        await Wait(menu, 0.3);
        var matched = Rows().Where(r => SpireModManager.Search.Matches(r.Mod, "#Gameplay")).Select(r => r.Mod).OfType<Mod>().ToList();
        var others = Rows().Select(r => r.Mod).OfType<Mod>().Except(matched).ToDictionary(m => m, Copies.Ticked);
        Check("searching: the bar says it acts on the matches", (Bar()?.FindChild("Scope", true, false) as Label)?.Text.StartsWith("Search matches") == true);
        Press("DisableAllButton");
        await Wait(menu, 0.3);
        Check("Disable all turns off just the matches", matched.All(m => !Copies.Ticked(m)) && others.All(p => Copies.Ticked(p.Key) == p.Value),
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
        // A header's click folds its category and the bar's Expand all follows at once; while searching every category
        // shows open, so a click there folds nothing out of sight.
        if (Store.Data.Categories.FirstOrDefault(c => Rows().Any(r => Store.InfoFor(r.Mod)?.Category == c.Name)) is { } folding)
        {
            void ClickHeader()
            {
                foreach (var pressed in new[] { true, false })
                    Header(content, folding.Name)?.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = pressed });
            }
            Button? ExpandAll() => Bar()?.FindChild("ExpandAllButton", true, false) as Button;
            Find(Rows().First(r => Store.InfoFor(r.Mod)?.Category == folding.Name).Mod?.manifest?.id ?? "");
            await Wait(menu, 0.3);
            ClickHeader();
            var foldedSearching = folding.Collapsed;
            Find("");
            await Wait(menu, 0.3);
            ClickHeader();
            await Wait(menu, 0.3);
            Check("a header's click folds its category and the bar's Expand all comes on; while searching it folds nothing",
                !foldedSearching && folding.Collapsed && ExpandAll() is { Disabled: false }, $"searching {foldedSearching}, then {folding.Collapsed}, Expand all off {ExpandAll()?.Disabled}");
            ClickHeader();
            await Wait(menu, 0.3);
        }
        else
            Results.Add("SKIP a header's click: no category holds a mod");

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
            await PanelFor(menu, screen, libRow);
            await Wait(menu, 0.3);
            // Its row in view, as hovering lights only one on screen (the names sort apart).
            if (!content.GetParent<Control>().GetGlobalRect().Intersects(uiRow.GetGlobalRect()))
            {
                CategoryList.ScrollTo(content, uiRow);
                await Wait(menu, 1.5);
            }
            var description = screen.GetNode<RichTextLabel>("%ModInfoContainer/ModDescription");
            var look = uiRow.GetNode<Panel>("SelectionHighlight");
            var before = look.Modulate;
            description.EmitSignal(RichTextLabel.SignalName.MetaHoverStarted, "mod:SmmTestUi");
            var lit = look.Modulate;
            var litFill = (look.GetThemeStylebox("panel") as StyleBoxFlat)?.BgColor.A ?? 0;
            description.EmitSignal(RichTextLabel.SignalName.MetaHoverEnded, "mod:SmmTestUi");
            Check("hovering a mod's name in the panel lights its row (visibly), and lets go", lit.A * litFill > 0.05f && look.Modulate == before, $"{before} -> {lit} x {litFill} -> {look.Modulate}");
            description.EmitSignal(RichTextLabel.SignalName.MetaClicked, "mod:SmmTestUi");
            await WaitReal(menu, 0.1); // past ScrollSoon's 0.05 s: the row focused
            Check("clicking it gives the row the selected look; the panel stays", uiRow.HasFocus() && CategoryList.Selected?.manifest?.id == "SmmTestLib",
                $"focus {uiRow.HasFocus()} (focused: {screen.GetViewport().GuiGetFocusOwner()?.Name}), selected {CategoryList.Selected?.manifest?.id}");
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
        // The choices by name ("All (20)" reads All), the lit one starred; and the counts they show, in order.
        IEnumerable<Button> ChoiceButtons() => Bar()?.FindChild("Show", true, false)?.GetChildren().OfType<Button>() ?? [];
        string Choices() => string.Join(" ", ChoiceButtons()
            .Select(b => System.Text.RegularExpressions.Regex.Replace(b.Text, @" \([\d,]+\)$", "") + (b.GetThemeColor("font_color") == Colors.White ? "*" : "")));
        List<int> ChoiceCounts() => ChoiceButtons().Select(b => System.Text.RegularExpressions.Regex.Match(b.Text, @"\(([\d,]+)\)$") is { Success: true } m
            ? int.Parse(m.Groups[1].Value.Replace(",", "")) : -1).ToList();
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
        static bool On(Mod mod) => Copies.Ticked(mod);
        static bool Off(Mod mod) => !Copies.Ticked(mod);

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
                && Scope() == $"All enabled ({on.Count})" && Choices() == "All Enabled* Disabled Changed", Say());
            Check("Show: Enabled: each category counts its enabled mods; none: no header", Headers(On) == "", Headers(On));

            await Press("ShowDisabledButton");
            var off = Where(Off);
            Results.Add($"INFO {on.Count} enabled, {off.Count} disabled of {all.Count}");
            Check("Show: Disabled: just the disabled mods, counted, Disabled lit", Shown().SequenceEqual(off)
                && Scope() == $"All disabled ({off.Count})" && Choices() == "All Enabled Disabled* Changed", Say());
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
            // The choices count within the search too: All its matches, Enabled and Disabled splitting them, the lit one
            // as many as it shows (the count before Enable all says the same).
            Check("with a search, each choice counts its matches: All the search's, Disabled what it shows",
                ChoiceCounts() is [var allCount, var onCount, var offCount, _] && allCount == found.Count && offCount == both.Count && onCount + offCount == found.Count,
                $"{string.Join(", ", ChoiceCounts())}; search {found.Count}, both {both.Count}");
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

        // The same under a search (Show: All): with "#enabled" typed, a mod unticked keeps its row, counted among the
        // matches, through a redraw that changes no mod and a space more in the box; the search changed, it goes, and
        // coming back to "#enabled" doesn't bring it. Again with a Show: choice pressed: it goes. (Undone after.)
        await Press("ShowAllButton");
        await Find("#enabled");
        if (Rows().FirstOrDefault(r => r.Visible && r.Mod is { } m && On(m) && m.manifest?.id is { } id && id != ModEntry.ModId && id != Bmm.ModId)
            is { Mod: { } byHand } byHandRow)
        {
            var count = Scope();
            async Task Untick()
            {
                var tickbox = byHandRow.GetNode<NTickbox>("Tickbox");
                tickbox.IsTicked = false;
                tickbox.EmitSignal(NTickbox.SignalName.Toggled, tickbox);
                await Wait(menu, 0.3);
            }
            await Untick();
            var keptAtOnce = Off(byHand) && byHandRow.Visible && Scope() == count;
            CategoryList.Apply(content);
            await Wait(menu, 0.3);
            var keptRedrawn = byHandRow.Visible && Scope() == count;
            await Find("#enabled ");
            var keptSpaced = byHandRow.Visible;
            await Find("#enabled -zzz");
            var goneChanged = !byHandRow.Visible;
            await Find("#enabled");
            Check("a search (\"#enabled\"): a mod unticked keeps its row, counted, through a redraw and a space more; the search changed, it goes, and stays gone",
                keptAtOnce && keptRedrawn && keptSpaced && goneChanged && !byHandRow.Visible && Shown().SequenceEqual(Where(On)),
                $"at once {keptAtOnce}, redrawn {keptRedrawn}, a space more {keptSpaced}, changed {goneChanged}; {count} -> {Scope()}; {Say()}");
            History.Undo();
            await Wait(menu, 0.3);
            await Untick();
            var keptAgain = byHandRow.Visible;
            await Press("ShowAllButton");
            Check("a search (\"#enabled\"), a mod unticked (kept), a Show: choice pressed: it goes", keptAgain && !byHandRow.Visible && Shown().SequenceEqual(Where(On)),
                $"kept {keptAgain}; {Say()}");
            History.Undo();
            await Wait(menu, 0.3);
            Check("undone: enabled, and found again", On(byHand) && byHandRow.Visible, Say());
        }
        else
            Results.Add("SKIP list filters, a kept row under a search: no enabled mod to untick");
        await Find("");

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
            // A copy of a mod installed twice among them (the fixture twins' copy not picked) comes on beside the other,
            // which stays on: nothing turns another copy off. Turned on, it's the copy that loads (the copy enabled last).
            var others = Rows().Select(r => r.Mod).OfType<Mod>().Except(wereOff).ToDictionary(m => m, On);
            var twin = wereOff.FirstOrDefault(m => Copies.Twins(m).Count > 0);
            var (pickWas, offWas) = (twin?.manifest?.id is { } twinId ? Copies.Picked(twinId, twin.modSource) : null, CopiesOff());
            await Press("EnableAllButton");
            Check("Show: Disabled, Enable all: just the mods shown, which then go; no other copy turns off (a copy turned on is the one that loads)",
                wereOff.All(On) && others.All(p => On(p.Key) == p.Value) && Shown().Count == 0 && Scope() == "All disabled (0)"
                && (twin?.manifest?.id is not { } turnedId || Copies.Picked(turnedId, twin.modSource) == Copies.CopyOf(twin) && ModEntry.HasPendingChanges()),
                $"{Say()}; copy {Copies.KeyOf(twin)}, picked {(twin?.manifest?.id is { } shownId ? Copies.Picked(shownId, twin.modSource) : null)}");
            History.Undo();
            await Wait(menu, 0.3);
            Check("undone: disabled and shown again (a copy's pick and copies_off back)", wereOff.All(Off)
                && Shown().SequenceEqual(Ids(Rows().Where(r => r.Mod is { } m && wereOff.Contains(m)))) && CopiesOff().SequenceEqual(offWas)
                && (twin?.manifest?.id is not { } backId || Copies.Picked(backId, twin.modSource) == pickWas), Say());
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
                nowOn.Count < wereOn.Count && Shown().SequenceEqual(nowOn) && Scope() == $"All enabled ({nowOn.Count})" && Headers(On) == "",
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
            && Scope() == $"Changed since launch ({changed.Count})" && Choices() == "All Enabled Disabled Changed*", $"{Say()}; Changes: {string.Join(" | ", lines)}");
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

        // Going to a mod the choice leaves out (a name in the panel, a banner's Go to) shows its row; the choice stays.
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
        {
            Check("Vanilla List: every row, whatever the main list shows", await WithVanillaView(screen, menu, () => Shown().SequenceEqual(all)));
            // A search typed on the main list doesn't follow to Vanilla List, which has no box for it: every row there,
            // and the search still on the main list after. The game's panel title there: the game's pointer, no link.
            var searchBox = screen.GetNode<LineEdit>("ModsBorder/SpireModManagerSearch");
            void Search(string text)
            {
                searchBox.Text = text;
                searchBox.EmitSignal(LineEdit.SignalName.TextChanged, text);
            }
            Search("SmmTestLib");
            await Wait(menu, 0.5);
            var searched = Shown().Count;
            var title = screen.GetNode<Control>("%ModInfoContainer/ModTitle");
            var vanillaAll = false;
            var vanillaCursor = Control.CursorShape.PointingHand;
            await WithVanillaView(screen, menu, () =>
            {
                vanillaAll = Shown().SequenceEqual(all);
                vanillaCursor = title.MouseDefaultCursorShape;
                return true;
            });
            var searchKept = Shown().Count == searched && searchBox.Text == "SmmTestLib";
            var ourCursor = title.MouseDefaultCursorShape;
            Search("");
            await Wait(menu, 0.5);
            Check("Vanilla List with a search typed on the main list: every row, and the search still there after; its panel title has the "
                + "game's pointer (ours: the hand)", vanillaAll && searched < all.Count && searchKept && vanillaCursor != Control.CursorShape.PointingHand
                && ourCursor == Control.CursorShape.PointingHand, $"searched {searched} of {all.Count}, kept {searchKept}, cursor {vanillaCursor} then {ourCursor}");
        }

        await Press("ShowAllButton");
        Check("All: every row again, counted as installed mods", Shown().SequenceEqual(all) && Scope() == $"Mods installed ({all.Count})"
            && Choices() == "All* Enabled Disabled Changed", Say());

        // Going to a mod a search leaves out (a name in the panel, a line in Changes), as for a banner's Show: the search
        // clears, box and list, and its row shows; a search that finds it stays.
        if (Rows().FirstOrDefault(r => r.Mod?.manifest?.id == "SmmTestLib") is { } panelRow
            && Rows().FirstOrDefault(r => r.Mod?.manifest?.id == "SmmTestUi") is { Mod: { } linked } linkedRow)
        {
            CategoryList.Select(screen, panelRow);
            await PanelFor(menu, screen, panelRow);
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
            .Where(b => b.GetThemeColor("font_color") == Colors.White).Select(b => System.Text.RegularExpressions.Regex.Replace(b.Text, @" \([\d,]+\)$", "")) ?? []);
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
        Check("relaunch check lists the mods that won't load", Body().Contains("Requires mod to be installed: " + Missing)
            && Body().Contains("Requires mod to be enabled: Test Framework"), Body());
        // Several of a kind: one line naming them all, in the plural (a made-up mod, never installed).
        var several = ModDetails.DependencyProblems(new Mod
        {
            path = "", modSource = ModSource.ModsDirectory,
            manifest = new ModManifest { id = "SmmPluralTest", name = "Plural Test", dependencies = [new("SmmGoneA"), new("SmmGoneB"), new("SmmTestLib")] },
        });
        Check("a mod needing several of a kind: one line naming them all, in the plural",
            several.SequenceEqual(["Requires mods to be installed: SmmGoneA, SmmGoneB", "Requires mod to be enabled: Test Framework"]), string.Join(" | ", several));
        // Cancel first, at the row's left; the fixes and Relaunch anyway at its right.
        var row = Dialog()?.FindChild("Buttons", true, false) as Control;
        var rowButtons = row?.GetChildren().OfType<Button>().ToList() ?? [];
        Check("its Cancel comes first, at the left, the fixes and Relaunch anyway at the right",
            rowButtons.Select(b => b.Name.ToString()).SequenceEqual(["CancelRelaunchButton", "EnableNeededButton", "DisableBrokenButton", "RelaunchNowButton"])
            && row != null && rowButtons[0].GetGlobalRect().Position.X - row.GetGlobalRect().Position.X < 1
            && row.GetGlobalRect().End.X - rowButtons[^1].GetGlobalRect().End.X < 1,
            string.Join(", ", rowButtons.Select(b => $"{b.Name} {b.GetGlobalRect().Position.X:0}-{b.GetGlobalRect().End.X:0}")) + $" in {row?.GetGlobalRect()}");
        await Capture(screen, outDir, "15-relaunch-check");
        Press("EnableNeededButton");
        await Wait(menu, 0.3);
        Check("Enable required mods fixes the off ones, the list updates", Copies.Ticked(lib) && !Body().Contains("Test Framework") && Body().Contains(Missing), Body());
        Press("DisableBrokenButton");
        await Wait(menu, 0.3);
        Check("Disable mods that can't load: nothing left, plain Relaunch", !Copies.Ticked(addon)
            && Dialog()?.FindChild("RelaunchNowButton", true, false) is Button { Text: "Relaunch Game" }, Body());
        // Cancel takes both fixes back: Test Framework off again (as this test set it), Test Addon on (the fixture's).
        // It forgets them too: nothing to redo.
        Press("CancelRelaunchButton");
        await Wait(menu, 0.3);
        Check("Cancel closes it and takes back the fixes made in it", Dialog() == null && !Copies.Ticked(lib) && Copies.Ticked(addon)
            && !tickbox.IsTicked && !History.CanRedo, $"framework on {Copies.Ticked(lib)}, addon on {Copies.Ticked(addon)}");
        // Esc the same, after one fix; the page stays.
        RelaunchCheck.Show(screen);
        await Wait(menu, 0.2);
        Press("EnableNeededButton");
        await Wait(menu, 0.3);
        var fixedIt = Copies.Ticked(lib);
        Input.ParseInputEvent(new InputEventKey { Keycode = Key.Escape, Pressed = true });
        await Wait(menu, 0.2);
        Input.ParseInputEvent(new InputEventKey { Keycode = Key.Escape, Pressed = false });
        await Wait(menu, 0.2);
        Check("Esc closes it the same way, the fix taken back, and stays on the page", fixedIt && Dialog() == null && screen.IsVisibleInTree()
            && !Copies.Ticked(lib) && Copies.Ticked(addon), $"fixed {fixedIt}, framework on {Copies.Ticked(lib)}");
        // Cancel with nothing done takes nothing back: the undo step before the dialog (this test's untick) stays.
        RelaunchCheck.Show(screen);
        await Wait(menu, 0.2);
        Press("CancelRelaunchButton");
        await Wait(menu, 0.3);
        Check("Cancel with no fix made changes nothing", Dialog() == null && !Copies.Ticked(lib) && Copies.Ticked(addon));
        History.Undo(); // this test's untick: Test Framework on again, as the fixture has it
        await Wait(menu, 0.3);
        Check("then one undo puts the framework back on, as the fixture has it", Copies.Ticked(lib) && tickbox.IsTicked);
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
                await WaitReal(menu, seconds);
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
                Click(screen.GetNode<BaseButton>("SpireModManagerViewToggle"));
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
                var description = panel.GetNode<RichTextLabel>("ModDescription");
                var underlined = description.MetaUnderlined;
                Click(screen.GetNode<BaseButton>("SpireModManagerViewToggle"));
                await Wait(menu, 0.5);
                // The game's scene leaves Godot's default: underlined. Our page filled the panel first (WatchLinks takes it off).
                Check("Vanilla View: the game's panel underlines its links as the game has it, after our page filled it; ours don't",
                    underlined && !description.MetaUnderlined, $"vanilla {underlined}, ours {description.MetaUnderlined}");
                Check("long details: in Vanilla View the long name is fitted in the panel's title and the row's, and stays so after a redraw and a re-layout",
                    fitted is (< 60, < 32) && again == fitted, $"panel {fitted.Panel}, row {fitted.Row}; after {again.Panel}, {again.Row}");
                Check("long details: a redraw in Vanilla View fits no row title again, the long one included (each stays at its own fitted size)",
                    refitted.Count == 0, $"{refitted.Count} fitted again: {string.Join(", ", refitted.Distinct().Take(8))}");
                // The same for a panel our page fills before anything recorded the game's looks (one fresh from the game's
                // scene, hidden on the menu, filled in our view): the page's own panel has them recorded at its _Ready.
                var scene = screen.SceneFilePath is { Length: > 0 } scenePath ? ModEntry.Load<PackedScene>(scenePath)?.Instantiate() : null;
                var freshSay = "no panel in the game's scene";
                if (scene?.GetNodeOrNull<NModInfoContainer>("%ModInfoContainer") is { } fresh)
                {
                    var freshText = fresh.GetNode<RichTextLabel>("ModDescription");
                    var sceneUnderline = freshText.MetaUnderlined;
                    fresh.GetParent()?.RemoveChild(fresh);
                    fresh.Visible = false;
                    menu.AddChild(fresh);
                    ModDetails.Fill(fresh, mod);
                    var oursUnderline = freshText.MetaUnderlined;
                    ModDetails.Looks(fresh, game: true);
                    freshSay = $"scene {sceneUnderline}, ours {oursUnderline}, vanilla {freshText.MetaUnderlined}";
                    await Wait(menu, 0.1); // its fields' deferred placing first
                    fresh.QueueFree();
                }
                scene?.Free();
                Check("Vanilla View: a panel our page filled before the game's looks were recorded gets the game's underline back",
                    freshSay == "scene True, ours False, vanilla True", freshSay);
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
        Compat.Clear(screen.GetNode<NModInfoContainer>("%ModInfoContainer"));
        await Wait(menu, 0.3);
    }

    // A Workshop mod's head (made-up details; the sandbox has no Steam): Author, Updated (how long ago, and a link
    // to its change notes on the Workshop), Compatibility. "Ago" in hours under a day, singular for one. Dates and
    // numbers in the game's language. Its Other keywords after its Workshop tags (screenshot 8b).
    static async Task TestWorkshopHead(Node menu, NModdingScreen screen, string outDir)
    {
        var mod = new Mod
        {
            path = "", modSource = ModSource.SteamWorkshop, state = ModLoadState.AddedAtRuntime,
            manifest = new ModManifest { id = "SmmWorkshopTest", name = "Workshop Test", author = "Someone Careful" },
        };
        Compat.SetWorkshopId(mod, 42);
        Installed.Detected(mod); // so the panel can find it by id
        WorkshopInfo.TestSet(42, new WorkshopInfo.Details(DateTime.Now.AddDays(-30), DateTime.Now.AddDays(-4), ["QoL", "Utility"], [], 1_572_864, 1234)); // 1.5 MB
        var info = screen.GetNode<NModInfoContainer>("%ModInfoContainer");
        info.Fill(mod);
        await Wait(menu, 0.3);
        var head = info.GetNodeOrNull<RichTextLabel>("SpireModManagerHead");
        var text = head?.GetParsedText() ?? "";
        Check("a Workshop mod's head: Author, Updated (4 days ago • Changelog), Compatibility", text.IndexOf("Author") < text.IndexOf("Updated")
            && text.IndexOf("Updated") < text.IndexOf("Compatibility") && text.Contains("(4 days ago)  •\u00a0\u00a0Changelog") && head?.Text.Contains("[url=patchnotes]") == true, text);
        var link = info.FindChild("SpireModManagerLink", true, false) as LinkButton;
        Check("the source line: id (SteamID n)", link?.Text == "SmmWorkshopTest (SteamID 42)", link?.Text ?? "");

        // Both leave the game (#126): the external-link mark after each, part of the link and in its colour, and each
        // still goes where it went.
        if (link != null && head != null)
        {
            var linkSize = link.GetThemeFontSize("font_size");
            var linkText = link.GetThemeFont("font").GetStringSize(link.Text, fontSize: linkSize).X;
            var mark = LinkMark.Rect(link);
            Check("the Workshop link: the external-link mark a small gap after its text, 11 px for its 16 px text, inside the link's own (clickable) box",
                linkSize == 16 && mark is { } at && at.Position.X >= linkText + 2 && at.Position.X <= linkText + 6 && at.Size == new Vector2(11, 11)
                && at.End.X <= link.Size.X + 0.5f && at.Position.Y >= 0 && at.End.Y <= link.Size.Y + 0.5f
                && link.IsInGroup(LinkMark.Group) && link.GetSignalConnectionList(CanvasItem.SignalName.Draw).Count > 0,
                $"mark {mark} after text {linkText:0.#}, link {link.Size}");
            // The mark as drawn: the link's colour with the mouse off it, then (a mouse motion through the viewport; none
            // headless) its hover colour while the mouse is on it, and its own again once it leaves.
            var (normal, lit) = (link.GetThemeColor("font_color"), link.GetThemeColor("font_hover_color"));
            var headless = DisplayServer.GetName() == "headless";
            static void MoveTo(Vector2 at) => Input.ParseInputEvent(new InputEventMouseMotion { Position = at, GlobalPosition = at });
            var linkRect = link.GetGlobalRect();
            var away = link.GetViewport().GetMousePosition(); // where the mouse was, unless that's on the link: above it then
            if (linkRect.Grow(2).HasPoint(away))
                away = linkRect.Position - new Vector2(0, 30);
            if (!headless)
            {
                MoveTo(away);
                await Wait(menu, 0.1);
            }
            var unlit = await MarkDrawn(menu, link);
            Check("the Workshop link's mark: drawn in the link's own colour while it isn't lit",
                link.GetDrawMode() == BaseButton.DrawMode.Normal && unlit == normal,
                $"{link.GetDrawMode()}, drawn {unlit?.ToString() ?? "nothing"}; the link's {normal}");
            if (headless)
                Results.Add("SKIP the Workshop link's mark lit with the link: there's no mouse headless");
            else
            {
                // Redrawn by the link itself as it lights and goes out (no QueueRedraw here).
                LinkMark.TestForget(link);
                MoveTo(linkRect.GetCenter());
                await Wait(menu, 0.1);
                var (onMode, onDrawn, under) = (link.GetDrawMode(), LinkMark.TestDrawn(link), link.GetViewport().GuiGetHoveredControl());
                LinkMark.TestForget(link);
                MoveTo(away);
                await Wait(menu, 0.1);
                var (offMode, offDrawn) = (link.GetDrawMode(), LinkMark.TestDrawn(link));
                Check("the Workshop link's mark: redrawn in the link's hover colour while the mouse is on the link, and in its own once it leaves",
                    lit != normal && onMode == BaseButton.DrawMode.Hover && onDrawn == lit && offMode == BaseButton.DrawMode.Normal && offDrawn == normal,
                    $"on it ({under?.Name.ToString() ?? "nothing"} under the mouse at {linkRect.GetCenter()}): {onMode}, drawn {onDrawn?.ToString() ?? "nothing"}; "
                    + $"off it: {offMode}, drawn {offDrawn?.ToString() ?? "nothing"}; the link's {normal}, lit {lit}");
            }
            // The status after the link: the link as wide as its mark's end, then the line's separation, the dot and its
            // separation.
            var source = info.GetNode<Control>("SpireModManagerSource");
            var status = (Label)source.FindChild("Status", true, false);
            var dot = (Control)source.FindChild("Dot", true, false);
            var statusAt = link.GetGlobalRect().End.X + source.GetThemeConstant("separation") + dot.Size.X + dot.GetParent<Control>().GetThemeConstant("separation");
            var title = info.GetNode<Control>("ModTitle");
            Check("the Workshop link still opens the mod's Workshop page, and the status after it moves over only for the mark, within the title's width",
                link is { Disabled: false, Uri: "https://steamcommunity.com/sharedfiles/filedetails/?id=42" } && mark is { } shown
                && Math.Abs(link.Size.X - shown.End.X) <= 0.5f && Math.Abs(status.GlobalPosition.X - statusAt) <= 1
                && status.GetGlobalRect().End.X <= title.GetGlobalRect().End.X + 2,
                $"{link.Uri}; mark {mark}, link {link.GetGlobalRect()}, status {status.GetGlobalRect()} (expected at {statusAt:0.#}), title {title.GetGlobalRect()}");

            var bbcode = head.Text;
            var tag = LinkMark.Tag(16, "#99ccff");
            var changelog = bbcode.IndexOf("[url=patchnotes]", StringComparison.Ordinal);
            var tagAt = changelog < 0 ? -1 : bbcode.IndexOf(tag, changelog, StringComparison.Ordinal);
            Check("Changelog: the external-link mark right after its text, inside its link and its colour, 11 px for the head's 16 px text",
                tagAt >= 0 && bbcode[..tagAt].EndsWith("[url=patchnotes][color=#99ccff]Changelog") && bbcode[(tagAt + tag.Length)..].StartsWith("[/color][/url]")
                && head.GetThemeFontSize("normal_font_size") == 16 && LinkMark.Size(16) == 11, bbcode);
            // The [img] finds the mark by its path, and shows it: a line of just the mark is as wide as the gap and 11 px.
            var probe = new RichTextLabel { BbcodeEnabled = true, FitContent = true, AutowrapMode = TextServer.AutowrapMode.Off, Text = tag };
            Hover.Fonts(probe, 16);
            info.AddChild(probe);
            var gapOnly = probe.GetThemeFont("normal_font").GetStringSize("\u00a0", fontSize: 16).X;
            var probeWidth = probe.GetContentWidth();
            info.RemoveChild(probe);
            probe.QueueFree();
            Check("Changelog's mark: its [img] path loads the mark itself, which shows 11 px wide after the gap",
                ModEntry.Load<Texture2D>(Icons.ExternalLink(11).ResourcePath)?.GetInstanceId() == Icons.ExternalLink(11).GetInstanceId()
                && Icons.ExternalLink(11).GetSize() == new Vector2(11, 11) && Math.Abs(probeWidth - (gapOnly + 11)) <= 1,
                $"path {Icons.ExternalLink(11).ResourcePath}, a line of it {probeWidth} wide (gap {gapOnly:0.#})");
            Check("Changelog still opens the mod's change notes: the head's link is still patchnotes, its click still handled",
                bbcode.Split("[url=patchnotes]").Length == 2 && head.GetSignalConnectionList(RichTextLabel.SignalName.MetaClicked).Count > 0
                && head.MouseFilter != Control.MouseFilterEnum.Ignore);
        }
        Check("how long ago: hours under a day, one day singular, green under two days",
            ModDetails.Ago(DateTime.Now.AddMinutes(-20)).Contains("(less than an hour ago)") && ModDetails.Ago(DateTime.Now.AddHours(-3)).Contains("(3 hours ago)") && ModDetails.Ago(DateTime.Now.AddMinutes(-70)).Contains("(1 hour ago)")
            && ModDetails.Ago(DateTime.Now.AddHours(-30)).Contains("(1 day ago)") && ModDetails.Ago(DateTime.Now.AddHours(-30)).Contains("#a6e3a1")
            && !ModDetails.Ago(DateTime.Now.AddDays(-3)).Contains("#a6e3a1"));

        // Dates, sizes and counts follow the game's language: in English as they always read; in German, the game's
        // German date pattern and separators (our words stay English there, as we have no German text).
        var english = info.GetNode<RichTextLabel>("ModDescription").GetParsedText();
        Check("in English: October 11, 2026; Size: 1.5 MB; Subscribers: 1,234; the profile list's Oct 11", ModDetails.Date(new DateTime(2026, 10, 11)) == "October 11, 2026"
            && english.Contains("Size: 1.5 MB") && english.Contains("Subscribers: 1,234") && Profiles.ShortDate(new DateTime(2026, 10, 11)) == "Oct 11", english);
        var facts = english.Split('\n');
        var tagsAt = Array.IndexOf(facts, $"{Menus.Text("SPIRE_MOD_MANAGER.WORKSHOP_TAGS")}: QoL, Utility");
        Check("a Workshop mod: Other keywords right after its Workshop tags, #workshop among them (see 8b-other-keywords)", tagsAt >= 0 && tagsAt + 1 < facts.Length
            && facts[tagsAt + 1].StartsWith($"{Menus.Text("SPIRE_MOD_MANAGER.OTHER_KEYWORDS")}: #") && facts[tagsAt + 1].Contains("#workshop"), english);
        await Capture(screen, outDir, "8b-other-keywords");
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
            // The profile list's date too, in the game's order and month (#98: it followed Windows' language).
            Check("with the game in German, the profile list's date: 11. Okt.", Profiles.ShortDate(new DateTime(2026, 10, 11)) == "11. Okt.",
                Profiles.ShortDate(new DateTime(2026, 10, 11)));
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
        Compat.Clear(info);
    }

    // What a mod changes in the game (GameChanges; Jordan: a mod that turned the Regent into another character was hard
    // to track down). The files: a real mod's (EnderLilis's 16 at the game's paths, as its pack has them) told by
    // character and what they are; a pack written here, read as the game's packs are, with one of the game's files in
    // it, in a made-up mod's folder: that file counted, the mod's own and the engine's not, and found by searching
    // "regent". The code: our own patches, by the game's classes, in our info panel. Screenshot 17c.
    static async Task TestGameChanges(Node menu, NModdingScreen screen, Control content, string outDir)
    {
        (string, string)[] characters = [("ironclad", "The Ironclad"), ("regent", "The Regent")];
        string[] lilis =
        [
            "animations/character_select/regent/characterselect_regent.png.import", "images/packed/character_select/char_select_regent.png.import",
            "images/packed/sprite_fonts/regent_energy_icon.png.import", "images/packed/sprite_fonts/star_icon.png.import",
            "images/ui/combat/energy_counters/regent/regent_orb_layer_1.png.import", "images/ui/combat/energy_counters/regent/regent_orb_layer_2.png.import",
            "images/ui/combat/energy_counters/regent/regent_orb_layer_3.png.import", "images/ui/combat/energy_counters/regent/regent_orb_layer_5.png.import",
            "images/ui/combat/energy_star.png.import", "images/ui/combat/energy_star_layer_2.png.import", "images/ui/combat/energy_star_layer_3.png.import",
            "images/ui/hands/multiplayer_hand_regent_paper.png.import", "images/ui/hands/multiplayer_hand_regent_point.png.import",
            "images/ui/hands/multiplayer_hand_regent_rock.png.import", "images/ui/hands/multiplayer_hand_regent_scissors.png.import",
            "images/ui/top_panel/character_icon_regent.png.import", ".godot/imported/char_select_regent.png-0123.ctex",
        ];
        var assets = lilis.Select(GameChanges.Asset).OfType<string>().Distinct().ToList();
        var subjects = GameChanges.FileSubjects(assets, characters);
        Check("a mod's files at the game's paths: counted once each (not the engine's), told by character and what they are, most first",
            assets.Count == 16 && subjects.SequenceEqual(["The Regent (energy orb, hands, character select, sprite fonts, top panel)", "combat", "sprite fonts"]),
            $"{assets.Count}: {string.Join(" | ", subjects)}");
        // And the compiler's classes inside one (an async method's state machine, the lambdas' "<>c"), here this test's.
        var nested = typeof(SelfTest).GetNestedTypes(System.Reflection.BindingFlags.NonPublic);
        string[] classes = [GameChanges.Class(typeof(NModdingScreen)), GameChanges.Class(typeof(MegaCrit.Sts2.Core.Models.CharacterModel)),
            GameChanges.Class(typeof(NModMenuRow)), GameChanges.Class(typeof(MegaCrit.Sts2.Core.Models.ModelDb)),
            GameChanges.Class(nested.FirstOrDefault(t => t.Name.StartsWith("<TestGameChanges>d__"))), GameChanges.Class(nested.FirstOrDefault(t => t.Name == "<>c"))];
        Check("the game's classes as words: modding screen, character (no \"Model\"), mod menu row, model db; a compiler-made one by the class it's in",
            classes.SequenceEqual(["modding screen", "character", "mod menu row", "model db", "self test", "self test"]), string.Join(" | ", classes));

        // A pack of a made-up mod: one of the game's files (the Regent's rock hand), one of its own, one of the engine's.
        var folder = Path.Combine(outDir, "game-changes-mod");
        Directory.CreateDirectory(folder);
        var gameFile = "images/ui/hands/multiplayer_hand_regent_rock.png.import";
        WritePack(Path.Combine(folder, "SmmChangesTest.pck"), [gameFile, "SmmChangesTest/thing.png.import", ".godot/imported/thing.png-0123.ctex"]);
        var made = new Mod { path = folder, modSource = ModSource.ModsDirectory, state = ModLoadState.Loaded,
            manifest = new ModManifest { id = "SmmChangesTest", name = "Changes Test", author = "Someone Careful" } };
        var changes = GameChanges.For(made);
        var regent = MegaCrit.Sts2.Core.Models.ModelDb.AllCharacters.First(c => c.Id.Entry == "REGENT").Title.GetFormattedText();
        Check("a pack read as the game's are: the game's file it has counted, its own and the engine's not; the Regent's hands; found by \"regent\" and \"hands\"",
            changes.Files == 1 && changes.FileSubjects.SequenceEqual([$"{regent} (hands)"]) && changes.Code == 0
            && SpireModManager.Search.Matches(made, "regent") && SpireModManager.Search.Matches(made, "hands") && !SpireModManager.Search.Matches(made, "ironclad"),
            $"{changes.Files} files: {string.Join(" | ", changes.FileSubjects)}; code {changes.Code}");
        Check("search's words for what a mod changes are kept for the session, the same as worked out afresh",
            GameChanges.Words(made) == changes.Words && GameChanges.Words(made) == GameChanges.Words(made), GameChanges.Words(made));

        // Our own patches, in our info panel.
        if (content.GetChildren().OfType<NModMenuRow>().FirstOrDefault(r => r.Mod is { manifest.id: ModEntry.ModId, state: ModLoadState.Loaded }) is not { Mod: { } ours } row)
        {
            Results.Add("SKIP what a mod changes in the panel: our row isn't there");
            return;
        }
        var own = GameChanges.For(ours);
        CategoryList.Select(screen, row);
        await PanelFor(menu, screen, row);
        await Wait(menu, 0.4);
        var shown = screen.GetNode<NModInfoContainer>("%ModInfoContainer").GetNode<RichTextLabel>("ModDescription").GetParsedText();
        Check("our own patches: the game's code we change, by class (modding screen among them), in our info panel with how many places",
            own.Code > 0 && own.Files == 0 && own.CodeSubjects.Contains("modding screen") && shown.Contains("Changes game code: ")
            && shown.Contains($"({own.Code} places)") && !shown.Contains("Replaces game files"),
            $"{own.Code}: {string.Join(", ", own.CodeSubjects)} | {shown.Replace("\n", " / ")}");
        await Capture(screen, outDir, "17c-game-changes");
    }

    // A Godot pack (format v3, as GameChanges reads one) listing these paths, each with no content.
    static void WritePack(string file, string[] paths)
    {
        using var w = new BinaryWriter(File.Create(file));
        w.Write("GDPC"u8.ToArray());
        w.Write(3); // format
        w.Write(4); w.Write(5); w.Write(1); // engine version
        w.Write(0); // flags
        w.Write(0L); // where the files start
        var directoryAt = w.BaseStream.Position + 8 + 16 * 4;
        w.Write(directoryAt);
        for (var i = 0; i < 16; i++)
            w.Write(0); // reserved
        w.Write(paths.Length);
        foreach (var path in paths)
        {
            var name = System.Text.Encoding.UTF8.GetBytes(path);
            var padded = (name.Length + 3) / 4 * 4;
            w.Write(padded);
            w.Write(name);
            w.Write(new byte[padded - name.Length]);
            w.Write(0L); // offset
            w.Write(0L); // size
            w.Write(new byte[16]); // MD5
            w.Write(0); // flags
        }
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
        await PanelFor(menu, screen, row);
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
        var parkedLine = info.GetNodeOrNull<Label>(ModDetails.OthersName);
        Check("another mod's panel with no room for it: out of sight, a line under the details says so, our fields right under it", OutOfSight()
            && parkedLine is { Visible: true } && parkedLine.Text == "1 more from other mods: see the vanilla Modding page"
            && Math.Abs(parkedLine.OffsetTop - details.OffsetBottom - ModDetails.FieldsGap) < 1
            && Math.Abs(yours.OffsetTop - parkedLine.OffsetBottom - ModDetails.FieldsGap) < 1, $"{where}; line \"{parkedLine?.Text}\" shown {parkedLine?.Visible}");
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
        ModDetails.PlaceFields(info); // as a resize does while the list is open
        Check("behind the changes list: no \"more from other mods\" line over it", info.GetNodeOrNull<Control>(ModDetails.OthersName) is not { Visible: true });
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
        Compat.Clear(info);
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
        var info = bulk?.FindChild("Info", true, false) as TextureRect;
        // The heading and its i are one hover target, only as wide as the two; it and both terms open one card, at once
        // (read 0.05 s after the mouse arrives): how tags and notes work for all of them.
        const double atOnce = 0.05;
        var group = bulk?.FindChild("Heading", true, false) as Control;
        var editingLabel = group?.GetNodeOrNull<Label>("Editing");
        Check("the bulk menu's heading and its i are one hover target, as wide as the two", group is { MouseFilter: Control.MouseFilterEnum.Pass }
            && editingLabel != null && info != null && group.GetGlobalRect().Encloses(editingLabel.GetGlobalRect()) && group.GetGlobalRect().Encloses(info.GetGlobalRect())
            && group.Size.X <= editingLabel.Size.X + info.Size.X + 9 && info.MouseFilter == Control.MouseFilterEnum.Ignore,
            $"group {group?.GetGlobalRect()}, heading {editingLabel?.GetGlobalRect()}, i {info?.GetGlobalRect()}");
        var infoCard = group == null ? "" : (await SizedHoverCard(menu, group, ModField.TipName, () => Capture(screen, outDir, "18c-bulk-info"), atOnce)).Text;
        var bulkTerms = new List<string>();
        foreach (var f in new[] { "Tags", "Notes" })
            if ((bulk?.FindChild("Fields", true, false) ?? bulk)?.GetNodeOrNull<Label>($"{f}/Term/Label") is { } term)
                bulkTerms.Add(await HoverCard(menu, term, ModField.TipName, atOnce));
        var howItWorks = $"{Menus.Text("SPIRE_MOD_MANAGER.TAGS_TERM")}\n{Menus.Text("SPIRE_MOD_MANAGER.TAGS_BULK_HINT")}\n\n"
            + $"{Menus.Text("SPIRE_MOD_MANAGER.NOTES_TERM")}\n{Menus.Text("SPIRE_MOD_MANAGER.NOTES_BULK_HINT")}";
        Check("the bulk menu: its heading and \"i\" and both terms open the same card at once, how tags and notes work for all of them",
            info is { Visible: true, Texture: not null } && infoCard == howItWorks && bulkTerms.Count == 2 && bulkTerms.All(t => t == howItWorks),
            $"info: {infoCard}; terms: {string.Join(" | ", bulkTerms)}");
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

        // Notes that differ: a warning beside Notes counts the mods with notes, the ones typing would overwrite; the
        // same notes all round, no warning.
        var notesBefore = Store.InfoFor(picked[0])?.Notes;
        Label? Mixed(Node? bulkMenu) => bulkMenu?.FindChild("Mixed", true, false) as Label;
        var sameNotes = CategoryList.ShowBulkMenu(content);
        await Wait(menu, 0.2);
        var quiet = Mixed(sameNotes) is { Visible: false };
        MenuPanel.Close();
        Store.EditMod(picked[0], info => info.Notes = "Only mine");
        var differing = CategoryList.ShowBulkMenu(content);
        await Wait(menu, 0.2);
        Check("bulk notes: no warning while they're the same; when they differ, one beside Notes counting the mods with notes",
            quiet && Mixed(differing) is { Visible: true, Text: "1 mod has differing notes and will be overwritten!" }
            && (differing?.FindChild("Notes", true, false)?.GetNodeOrNull<TextEdit>("Box")?.PlaceholderText == Menus.Text("SPIRE_MOD_MANAGER.NOTES_BULK_ADD")),
            $"same: {(quiet ? "none" : "shown")}; differ: {Mixed(differing)?.Text ?? "none"}");
        await Capture(screen, outDir, "18b-bulk-notes-differ");
        MenuPanel.Close();
        Store.EditMod(picked[0], info => info.Notes = notesBefore);

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

    // Picked mods on the bar, together on their tickboxes, and in undo (Selection, History.Picks). With two or more
    // picked, the page bar counts them on a chip in the rows' blue, with a ×, and says Enable selected / Disable selected
    // (headers keep Enable all / Disable all), as the bulk menu does; a click on the chip lets go. A picked row's tickbox
    // sets every picked mod, one undo step; another row's only its own. Ctrl+D lets go. Each change to the tinted set is
    // an undo step, in time with the edits, that leaves our data, the mod list and Changes alone; picking one mod with a
    // plain click, none tinted, is none. A view switch lets go of the picks and their steps.
    static async Task TestPicks(Node menu, NModdingScreen screen, Control content, string outDir)
    {
        List<NModMenuRow> Rows() => content.GetChildren().OfType<NModMenuRow>().ToList();
        // Mods installed once: the copies of one installed twice share a switch (TestTwinCopies has theirs).
        var rows = Rows().Where(r => r.Visible && r.Mod is { manifest.id: { } id } mod && id != ModEntry.ModId && id != Bmm.ModId && Copies.Others(mod).Count == 0)
            .Take(4).ToList();
        if (rows.Count < 4)
        {
            Results.Add("SKIP picked mods: fewer than 4 rows");
            return;
        }
        void CtrlClick(NModMenuRow row) =>
            row.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, CtrlPressed = true });
        async Task Keys(Key key)
        {
            foreach (var pressed in new[] { true, false })
            {
                Input.ParseInputEvent(new InputEventKey { Keycode = key, CtrlPressed = true, Pressed = pressed });
                await Wait(menu, 0.15);
            }
        }
        bool On(NModMenuRow row) => row.Mod is { } mod && !(SaveManager.Instance.SettingsSave.ModSettings?.IsModDisabled(mod) ?? false);
        int Tinted() => Rows().Count(r => r.GetNodeOrNull<Control>("SpireModManagerPicked") is { Visible: true });
        Control? Bar() => screen.FindChild(PageBar.Name, true, false) as Control;
        int Steps() => History.Steps(History.Track.List);
        string Say() => $"{Selection.Mods.Count} picked, {Tinted()} tinted, {Steps()} steps";
        // What a selection step leaves alone: our data, the mod list, Changes.
        string Data() => $"{Store.EditsJson()}\n{string.Join(",", History.CurrentMods())}\n{string.Join(" | ", HistoryBar.Describe())}";
        bool Picked(params NModMenuRow[] some) => Selection.Mods.Count == some.Length && some.All(r => Selection.Contains(r.Mod));
        var (first, three) = (rows.Take(2).ToArray(), rows.Take(3).ToArray());

        screen.GetViewport().GuiReleaseFocus(); // Ctrl+Z and Ctrl+D reach the page, not a text box
        Selection.Clear(content);
        await Wait(menu, 0.2);
        var steps = Steps();
        // A plain click on the main list (its press is the game's, its release lets go of picks), and Load Order's,
        // which picks the one mod: neither is a step.
        foreach (var pressed in new[] { true, false })
            rows[0].EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = pressed });
        await Wait(menu, 0.2);
        var mainList = Steps() == steps;
        Selection.Pick(content, rows[0], toggle: false, range: false);
        await Wait(menu, 0.2);
        Check("a plain click on one mod, none picked, is no undo step (main list and Load Order's way)", mainList && Picked(rows[0]) && Steps() == steps, Say());
        CtrlClick(rows[1]);
        CtrlClick(rows[2]);
        await Wait(menu, 0.2);
        Check("each Ctrl-click that changes the tinted set is an undo step", Picked(three) && Tinted() == 3 && Steps() == steps + 2
            && History.Newest(History.Track.List) is History.Picks, Say());

        var chip = Bar()?.FindChild("SelectionChip", true, false) as Control;
        var onOff = Bar()?.GetNodeOrNull("OnOff")?.GetChildren().OfType<Button>().Select(b => b.Text).ToList() ?? [];
        var header = content.GetChildren().OfType<Node>().FirstOrDefault(n => n.IsInGroup("SpireModManagerCategoryHeader"))?.GetNodeOrNull("Actions")
            ?.GetChildren().OfType<Button>().Select(b => b.Text).ToList() ?? [];
        var scope = (chip?.FindChild("Scope", true, false) as Label)?.Text;
        Check("picked: the bar counts them on a chip with a ×, and says Enable selected, Disable selected; headers keep Enable all, Disable all",
            scope == "Selected (3)" && chip?.FindChild("Clear", true, false) is Label { Text: "×" } && onOff.SequenceEqual(["Enable selected", "Disable selected"])
            && Bar()?.FindChild("ClearSelectionButton", true, false) == null && header.TakeLast(2).SequenceEqual(["Enable all", "Disable all"]),
            $"{scope ?? "no chip"}; {string.Join(",", onOff)}; header {string.Join(",", header)}");
        var listEnd = screen.GetNode<Control>("%ModsScrollContainer").GetGlobalRect().End.X;
        var past = Bar()?.FindChildren("*", "Control", true, false).OfType<Control>().Where(c => c.IsVisibleInTree() && c.GetGlobalRect().End.X > listEnd + 0.5f)
            .Select(c => $"{c.Name} to {c.GetGlobalRect().End.X:0}").ToList() ?? ["no bar"];
        Check("picked: the page bar fits the list's width", past.Count == 0, $"{string.Join(", ", past)}; the list ends at {listEnd:0}");
        await Capture(screen, outDir, "18d-picked-bar");
        var bulk = CategoryList.ShowBulkMenu(content);
        await Wait(menu, 0.3);
        var bulkLabels = bulk == null ? [] : MenuPanel.Labels(bulk);
        MenuPanel.Close();
        Check("the bulk menu says Enable selected and Disable selected", bulkLabels.Contains("Enable selected") && bulkLabels.Contains("Disable selected")
            && !bulkLabels.Contains("Enable all"), string.Join(", ", bulkLabels));

        var data = Data();
        await Keys(Key.Z);
        Check("Ctrl+Z takes back the last pick, and only that: our data, the mod list and Changes as they were", Picked(first) && Tinted() == 2 && Data() == data, Say());
        await Keys(Key.Y);
        Check("Ctrl+Y picks it again", Picked(three) && Tinted() == 3 && Data() == data, Say());

        // A picked row's tickbox: all three take the state it took; one undo puts them back. Another row's: just its own.
        var states = rows.Select(On).ToList();
        var target = !On(rows[1]);
        var box = rows[1].GetNode<NTickbox>("Tickbox");
        steps = Steps();
        box.IsTicked = target;
        box.EmitSignal(NTickbox.SignalName.Toggled, box);
        await Wait(menu, 0.3);
        Check("a picked row's tickbox sets every picked mod the same way, one undo step, the picks kept",
            three.All(r => On(r) == target && r.GetNode<NTickbox>("Tickbox").IsTicked == target) && On(rows[3]) == states[3] && Steps() == steps + 1 && Picked(three),
            $"{Say()}; {string.Join(",", rows.Select(On))}");
        History.Undo();
        await Wait(menu, 0.3);
        Check("one undo puts them all back", rows.Select(On).SequenceEqual(states), string.Join(",", rows.Select(On)));
        var other = rows[3].GetNode<NTickbox>("Tickbox");
        other.IsTicked = !states[3];
        other.EmitSignal(NTickbox.SignalName.Toggled, other);
        await Wait(menu, 0.3);
        Check("another row's tickbox switches only its own mod, the picks kept", On(rows[3]) != states[3] && three.Select(On).SequenceEqual(states.Take(3)) && Picked(three),
            string.Join(",", rows.Select(On)));
        History.Undo();
        await Wait(menu, 0.3);

        // In time with the edits: a category set for the picks is undone first, then the last pick.
        var ids = three.Select(r => r.Mod?.manifest?.id ?? "").ToList();
        var categoriesBefore = ids.Select(id => Store.InfoFor(id)?.Category).ToList();
        if (Store.Data.Categories.FirstOrDefault(c => !ids.All(id => Store.InfoFor(id)?.Category == c.Name)) is { } category
            && CategoryList.ShowBulkMenu(content) is { } move)
        {
            MenuPanel.Press(move, category.Name);
            await Wait(menu, 0.3);
            await Keys(Key.Z);
            var categoryBack = ids.Select(id => Store.InfoFor(id)?.Category).SequenceEqual(categoriesBefore) && Picked(three);
            await Keys(Key.Z);
            Check("undo walks edits and picks in the order they happened: the category, then the last pick", categoryBack && Picked(first), Say());
            await Keys(Key.Y);
        }
        else
            Results.Add("SKIP undo in time with the edits: no category to move the picks to");

        Selection.Add(content, three);
        await Wait(menu, 0.2);
        steps = Steps();
        chip = Bar()?.FindChild("SelectionChip", true, false) as Control;
        // A real click where there's a mouse (--window): the chip goes with the bar it rebuilds.
        var clicked = chip == null ? "no chip" : DisplayServer.GetName() == "headless"
            ? (chip.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false }) == Error.Ok ? "emitted" : "failed")
            : await RealClick(menu, chip);
        await Wait(menu, 0.2);
        Check("a click on the chip lets go of them, an undo step", Selection.Mods.Count == 0 && Tinted() == 0 && Steps() == steps + 1
            && Bar()?.FindChild("SelectionChip", true, false) == null, $"{Say()}; {clicked}");
        await Keys(Key.Z);
        Check("Ctrl+Z after letting go picks them again", Picked(three) && Tinted() == 3, Say());
        await Keys(Key.D);
        Check("Ctrl+D lets go of them, staying on the page", Selection.Mods.Count == 0 && Tinted() == 0 && screen.IsVisibleInTree(), Say());
        await Keys(Key.Z);
        Check("Ctrl+Z after Ctrl+D picks them again", Picked(three) && Tinted() == 3, Say());

        // A view switch lets go, and the picks' steps go with it. In Load Order, picks are steps of its own history, and
        // the Undo button takes them back as Ctrl+Z does.
        var toggle = screen.GetNode<NButton>("SpireModManagerLoadOrderToggle");
        Click(toggle);
        await Wait(menu, 0.5);
        var letGo = Selection.Mods.Count == 0;
        var orderSteps = History.Steps(History.Track.Order);
        var orderRows = Rows().Where(r => r.Visible && r.Mod?.manifest?.id is { } id && id != ModEntry.ModId).Take(2).ToList();
        if (orderRows.Count == 2)
        {
            Selection.Pick(content, orderRows[0], toggle: false, range: false); // Load Order's plain click: one mod, no step
            CtrlClick(orderRows[1]);
        }
        await Wait(menu, 0.2);
        var orderPicked = orderRows.Count == 2 && Selection.Mods.Count == 2 && History.Steps(History.Track.Order) == orderSteps + 1
            && History.Newest(History.Track.Order) is History.Picks;
        screen.FindChild("UndoButton", true, false)?.EmitSignal(BaseButton.SignalName.Pressed);
        await Wait(menu, 0.2);
        Check("in Load Order, picks are steps of its own history, and the Undo button takes them back", orderPicked
            && Selection.Mods.Count == 1 && History.Steps(History.Track.Order) == orderSteps, $"{Selection.Mods.Count} picked, {History.Steps(History.Track.Order)} order steps");
        Click(toggle);
        await Wait(menu, 0.5);
        Check("a view switch lets go of the picks, and their undo steps go too", letGo && Selection.Mods.Count == 0
            && History.Newest(History.Track.List) is not History.Picks && History.Newest(History.Track.Order) is not History.Picks, Say());
    }

    // A mod installed twice (a made-up Workshop copy of a local test mod, announced as the game announces one mid-session)
    // is two mods in our data (Copies): the local copy, found at launch, keeps the id's entry; the new copy gets its own,
    // seeded from it, the same in every undo snapshot (no edit since launch, no undo step); its popup says it's enabled
    // and which copy loads. An alias set on one copy is that copy's only: search, Changes (naming which copy), the info
    // panel, one undo. With the holder gone, the other copy takes the plain id with its own data, the gone one's kept under
    // its key. Both ticked, only one loads (the game's local-vs-Workshop rule), and the banner, chips, statuses, panel
    // lines and relaunch check say which, for a tie and for a newer Workshop copy on this branch; a tick on either copy
    // switches it alone. Everything is put back after.
    static async Task TestCopies(Node menu, NModdingScreen screen, Control content, string outDir)
    {
        const string id = "SmmTestFixes";
        if (content.GetChildren().OfType<NModMenuRow>().FirstOrDefault(r => r.Mod?.manifest?.id == id) is not { Mod: { } local })
        {
            Results.Add("SKIP copies: test mod missing");
            return;
        }
        // The Edited lines, read without the "(Local)" a line gains once a second copy is installed (Copies.Which).
        string Edits() => string.Join(" | ", HistoryBar.Describe().Where(l => l.StartsWith("Edited:"))).Replace(" (Local)", "");
        var (edits, steps) = (Edits(), History.Steps(History.Track.List));
        var (aliasBefore, notesBefore) = (Store.InfoFor(id)?.Alias, Store.InfoFor(id)?.Notes);
        var copy = new Mod
        {
            path = "", modSource = ModSource.SteamWorkshop, state = ModLoadState.AddedAtRuntime,
            manifest = new ModManifest { id = id, name = local.manifest?.name, version = local.manifest?.version },
        };
        Compat.SetWorkshopId(copy, 123);
        // Copies as rows name them (Copies.Named): the alias, else the name, then which copy.
        string Named(Mod mod) => (Store.InfoFor(mod)?.Alias ?? mod.manifest?.name ?? "") + Copies.Which(mod);
        NewModPopupPatch.TestReset();
        NModalContainer.Instance?.Clear();
        GameInstalls(copy);
        await Wait(menu, 0.4);
        var popup = string.Join(" | ", PopupTexts());
        NModalContainer.Instance?.Clear();
        CategoryList.Redraw(content);
        await Wait(menu, 0.2);
        Check("a Workshop copy arriving beside the local one: the new copy's words, enabled, naming the copy that loads (the local one: the same version)",
            popup.Contains(Draft(NewCopyOn, ("mods", Named(copy)), ("loads", Named(local)))), popup);
        // A list's row on for this Workshop copy, with the local copy on beside it: the preview's word about both copies
        // installed, one of which should be on (Jordan's words), naming the mod.
        var bothPlan = Profiles.PlanImport([new(id, true, local.manifest?.name ?? "", "https://steamcommunity.com/sharedfiles/filedetails/?id=123")]);
        Check("an import's Workshop row on beside a local copy that's on: the preview says both copies stay installed, one should be on",
            !Copies.Ticked(local) || bothPlan.BothCopies is [{ Id: var bothId }] && bothId == id,
            $"local ticked {Copies.Ticked(local)}; both {string.Join(", ", bothPlan.BothCopies.Select(e => e.Id))}");
        var (localKey, copyKey) = (Copies.Key(local), Copies.Key(copy));
        Check("a copy installed mid-session: its own entry, seeded from the id's, which the copy found at launch keeps; no edit, no undo step",
            localKey == id && copyKey == $"{id}|w:123" && Store.Data.Copies?.GetValueOrDefault(id) is [var first, "w:123"] && first == $"l:{id}"
            && Store.InfoFor(local) is { } localInfo && Store.InfoFor(copy) is { } copyInfo && localInfo != copyInfo
            && copyInfo.Alias == aliasBefore && copyInfo.Notes == notesBefore && Edits() == edits && History.Steps(History.Track.List) == steps,
            $"keys {localKey}, {copyKey}; record {string.Join(",", Store.Data.Copies?.GetValueOrDefault(id) ?? [])}; edits {Edits()} (were {edits}); "
            + $"steps {History.Steps(History.Track.List) - steps}; aliases {Store.InfoFor(local)?.Alias}/{Store.InfoFor(copy)?.Alias} (was {aliasBefore}); profile edited {Profiles.Edited}");

        // An alias on the new copy: that copy's only.
        Store.EditMod(copyKey, info => info.Alias = "Fixes from the Workshop");
        CategoryList.Apply(content);
        await Wait(menu, 0.2);
        Check("an alias on one copy: search and Changes find that copy, naming which; the other copy keeps its own",
            // (Both copies are under Changed anyway: the new one installed, the local one edited by tests before.)
            Store.InfoFor(local)?.Alias == aliasBefore && Search.Matches(copy, "\"from the Workshop\"") && !Search.Matches(local, "\"from the Workshop\"")
            && Edits() is var now && now.Contains("Edited: Test Fixes (123) (alias \"Fixes from the Workshop\")")
            && now.Replace("Edited: Test Fixes (123) (alias \"Fixes from the Workshop\")", "").Trim(' ', '|') == edits,
            $"edits {Edits()}");
        var info = screen.GetNode<NModInfoContainer>("%ModInfoContainer");
        info.Fill(copy);
        await Wait(menu, 0.2);
        var copyAlias = (info.FindChild("Alias", true, false)?.GetNodeOrNull<LineEdit>("Box"))?.Text;
        info.Fill(local);
        await Wait(menu, 0.2);
        var localAlias = (info.FindChild("Alias", true, false)?.GetNodeOrNull<LineEdit>("Box"))?.Text;
        Check("the info panel shows each copy's own alias", copyAlias == "Fixes from the Workshop" && localAlias == (aliasBefore ?? ""), $"{copyAlias} / {localAlias}");
        History.Undo();
        await Wait(menu, 0.3);
        Check("one undo takes the copy's alias back", Store.InfoFor(copy)?.Alias == aliasBefore && Store.InfoFor(local)?.Alias == aliasBefore && Edits() == edits,
            $"{Store.InfoFor(copy)?.Alias} / {Store.InfoFor(local)?.Alias}; edits {Edits()}");

        // The holder gone (the local copy off the game's list for a moment, our data tried on a copy): the Workshop copy
        // takes the plain id with its own data, and the local copy's is kept under its key.
        Store.EditMod(copyKey, i => i.Notes = "the Workshop copy's notes");
        var mods = GameMods();
        var at = mods.IndexOf(local);
        var tried = Store.Clone(Store.Data);
        mods.RemoveAt(at);
        try
        {
            Copies.Reconcile(tried);
        }
        finally
        {
            mods.Insert(at, local);
        }
        Check("the holder gone: the other copy takes the plain id with its own data; the gone copy's stays under its key",
            tried.Copies?.GetValueOrDefault(id) is ["w:123", var second] && second == $"l:{id}" && tried.Mods.GetValueOrDefault(id)?.Notes == "the Workshop copy's notes"
            && tried.Mods.GetValueOrDefault($"{id}|l:{id}")?.Notes == notesBefore && !tried.Mods.ContainsKey($"{id}|w:123"),
            $"record {string.Join(",", tried.Copies?.GetValueOrDefault(id) ?? [])}; plain {tried.Mods.GetValueOrDefault(id)?.Notes}");

        // Both copies on: the same-id banner, yellow, saying only one loads, its card which copy; a Same ID chip on each
        // whose tip names the other copy and the one that loads; Show steps between the two; Dismiss hides it for this pair
        // through a redraw (view state: no change, no undo step). The copy that won't load reads "Won't load (same ID)",
        // not "Pending relaunch", before a launch too.
        var border = screen.GetNode<Control>("ModsBorder");
        Control? SameId() => border.GetNodeOrNull<Control>(LoadOrder.SameIdName);
        CategoryList.Redraw(content);
        await Wait(menu, 0.3);
        var bothOn = Copies.Ticked(local) && Copies.Ticked(copy);
        var bannerText = (SameId()?.FindChild("SameIdWarning", true, false) as Label)?.Text;
        var chips = Tags.For(local).Concat(Tags.For(copy)).Where(c => c.Text == "Same ID").ToList();
        var card = await BannerCard(menu, screen, SameId());
        Check("both copies on: a yellow banner (\"2 mods with the same ID are enabled. Only one copy of each loads.\"), its card saying which loads (the local "
            + "copy: the same version); a Same ID chip on each naming the other and the copy that loads",
            bothOn && bannerText == Draft(SameIdText, ("count", "2")) && Yellow(BannerFill(SameId()))
            && card.Contains(Draft(BannerLoads, ("mod", Named(local)))) && card.Contains(Draft(BannerWontLoad, ("mod", Named(copy))))
            && chips.Count == 2 && chips[0].Tip == Draft(ClashTip, ("others", Named(copy)), ("loads", Named(local)))
            && chips[1].Tip == Draft(ClashTip, ("others", Named(local)), ("loads", Named(local))),
            $"on {bothOn}; banner {bannerText ?? "none"} ({string.Join(" | ", card)}); chips {string.Join(" / ", chips.Select(c => c.Tip))}");
        void Press(string name) => (SameId()?.FindChild(name, true, false) as BaseButton)?.EmitSignal(BaseButton.SignalName.Pressed);
        Press("GoToSameIdButton");
        await Wait(menu, 0.3);
        var firstStop = CategoryList.Selected;
        Press("GoToSameIdButton");
        await Wait(menu, 0.3);
        Check("the banner's Go to steps between the two copies", firstStop != null && CategoryList.Selected is { } secondStop && firstStop != secondStop
            && new[] { firstStop, secondStop }.All(m => m == local || m == copy), $"{Copies.KeyOf(firstStop)} then {Copies.KeyOf(CategoryList.Selected)}");
        // Its Show: "#clash" in the box, just the two copies' rows; pressed again, every row.
        var searchBox = border.GetNode<LineEdit>("SpireModManagerSearch");
        bool Lit() => SameId()?.FindChild("FilterSameIdButton", true, false) is Button { } show && show.HasThemeStyleboxOverride("normal"); // the chosen look
        var litBefore = Lit();
        Press("FilterSameIdButton");
        await Wait(menu, 0.3);
        var (clashBox, clashRows, clashLit) = (searchBox.Text, content.GetChildren().OfType<NModMenuRow>().Where(r => r.Visible).Select(r => r.Mod).ToList(), Lit());
        Press("FilterSameIdButton");
        await Wait(menu, 0.3);
        Check("the banner's Show: \"#clash\" in the box, just the two copies; pressed again, the box empty",
            clashBox == "#clash" && clashRows.Count == 2 && clashRows.Contains(local) && clashRows.Contains(copy) && searchBox.Text == "",
            $"box \"{clashBox}\", rows {string.Join(", ", clashRows.Select(Copies.KeyOf))}; then \"{searchBox.Text}\"");
        Check("the banner's Show is lit while the box holds \"#clash\", not before or after", !litBefore && clashLit && !Lit(),
            $"before {litBefore}, with it {clashLit}, after {Lit()}");
        // The hidden tags on the list as it is: both copies #clash, #sameid and #warning; the Workshop copy (the local one
        // loads) #aside; each finding the rows it means.
        var asMeant = HiddenTagsAsMeant(content);
        Check("both copies on: hidden tags on the list as it is (both #clash, #sameid, #warning; the Workshop copy #aside), each finding the rows it means",
            asMeant.Wrong.Count == 0 && new[] { local, copy }.All(m => Search.Matches(m, "#clash") && Search.Matches(m, "#sameid") && Search.Matches(m, "#warning"))
            && Search.Matches(copy, "#aside") && !Search.Matches(local, "#aside"), $"{string.Join(" | ", asMeant.Wrong)}; found {asMeant.Found}");
        var (editsShown, stepsShown) = (Edits(), History.Steps(History.Track.List));
        Press("DismissSameIdButton");
        await Wait(menu, 0.3);
        CategoryList.Redraw(content);
        await Wait(menu, 0.3);
        Check("Dismiss hides it for this pair, through a redraw: no change, no undo step", SameId() == null
            && Store.Data.SameIdDismissed == Copies.Signature([local, copy]) && Edits() == editsShown && History.Steps(History.Track.List) == stepsShown);
        // The clash over (the new copy off), the dismissal is forgotten. Enabled again on our page, the new copy is on beside
        // the local one (nothing turns another copy off; a local and a Workshop copy have a switch each, so nothing is
        // stored per copy): the banner is back; undone, it's off again. On again for the rest. In Load Order too. A click on
        // the chip searches #"Same ID": both copies, no other mod.
        Dependencies.Set([copy], false, screen);
        CategoryList.Redraw(content);
        await Wait(menu, 0.3);
        var forgotten = Store.Data.SameIdDismissed == null && SameId() == null;
        Dependencies.Set([copy], true, screen);
        CategoryList.Redraw(content);
        await Wait(menu, 0.3);
        var beside = Copies.Ticked(copy) && Copies.Ticked(local) && SameId() != null;
        bool NothingStored() => !(Store.Data.CopyPicks ?? []).Any(k => Copies.IdOf(k) == id) && !CopiesOff().Any(k => Copies.IdOf(k) == id);
        var stored = !NothingStored();
        History.Undo();
        await Wait(menu, 0.3);
        var undone = !Copies.Ticked(copy) && Copies.Ticked(local);
        Dependencies.Set([copy], true, screen);
        CategoryList.Redraw(content);
        await Wait(menu, 0.3);
        Check("the clash over, Dismiss is forgotten; enabled on our page, the new copy is on beside the local one (nothing turned off, nothing stored per "
            + "copy) and the banner is back; undone, it's off again", forgotten && beside && !stored && undone && Copies.Ticked(copy) && Copies.Ticked(local)
            && SameId() != null, $"forgotten {forgotten}; beside {beside}, stored {stored}, undone {undone}; on {Copies.Ticked(copy)}/{Copies.Ticked(local)}; "
            + $"banner {SameId() != null}");
        var toggle = screen.GetNode<NButton>("SpireModManagerLoadOrderToggle");
        Click(toggle);
        await Wait(menu, 0.5);
        var inLoadOrder = SameId() != null;
        // Its Show there: the find bar counts the two copies, every row in place, lit; pressed again, the box empty.
        Press("FilterSameIdButton");
        await Wait(menu, 0.3);
        var (orderFound, orderEvery, orderLit) = ((border.GetNodeOrNull(FindBar.Name)?.GetNodeOrNull<Label>("Count"))?.Text,
            content.GetChildren().OfType<NModMenuRow>().All(r => r.Visible), Lit());
        Press("FilterSameIdButton");
        await Wait(menu, 0.3);
        Check("Load Order: the same-ID banner's Show finds the two copies (\"2 matches\"), every row in place, lit; again, the box empty",
            orderFound == "2 matches" && orderEvery && orderLit && searchBox.Text == "" && !Lit(),
            $"\"{orderFound}\", every row {orderEvery}, lit {orderLit}; then \"{searchBox.Text}\"");
        Click(toggle);
        await Wait(menu, 0.5);
        var sameIdSearch = "#\"Same ID\"";
        // The other mods installed once (the fixture's own twins have the chip too).
        var others = content.GetChildren().OfType<NModMenuRow>().Select(r => r.Mod).OfType<Mod>().Where(m => m.manifest?.id is { } other && other != id && other != TwinId).ToList();
        Check("the banner shows in Load Order too; the chip's search finds both copies and no other mod", inLoadOrder
            && Search.Matches(local, sameIdSearch) && Search.Matches(copy, sameIdSearch) && !others.Any(m => Search.Matches(m, sameIdSearch)),
            $"Load Order {inLoadOrder}; others found {string.Join(", ", others.Where(m => Search.Matches(m, sameIdSearch)).Select(m => m.manifest?.id))}");
        // The Workshop copy loses to the local one (the same version): it says so, and why, before a launch (installed since)
        // and once the game set it aside; the local copy says it's the one that loads.
        string Line(Mod mod) => ModDetails.CopyLine(mod, false)?.Text ?? "";
        var (beforeStatus, beforeLine) = (ModDetails.Status(copy), Line(copy));
        copy.state = ModLoadState.DisabledDuplicate;
        var (setAside, setAsideLine) = (ModDetails.Status(copy), Line(copy));
        copy.state = ModLoadState.AddedAtRuntime;
        var losesLine = Draft(CopyLoses, ("other", Named(local)), ("why", WhyLocal));
        Check("a Workshop copy enabled beside the local one that wins reads \"Won't load (same ID)\", before a launch too and once the game set it aside; "
            + "its panel says the local copy loads instead, and why; the local copy's that it's the one that loads",
            beforeStatus.Contains(StatusWontLoad) && setAside.Contains(StatusWontLoad) && beforeLine == losesLine && setAsideLine == losesLine
            && Line(local) == Draft(LoadsAlso, ("other", Named(copy))), $"{beforeStatus} / {setAside}; lines {beforeLine} / {setAsideLine}; local {Line(local)}");

        // The Workshop copy updated while the game runs, to a newer version: the game adds a Mod for it after the one found
        // first, which keeps its manifest, and the next launch reads the new one. Which loads goes by that (the beta: the
        // update; stable: the local copy), and so does pending.
        var w = Compat.WorkshopBeatsOlderLocal;
        var update = new Mod
        {
            path = copy.path, modSource = ModSource.SteamWorkshop, state = ModLoadState.AddedAtRuntime,
            manifest = new ModManifest { id = id, name = copy.manifest?.name, version = "1.1.0" },
        };
        Compat.SetWorkshopId(update, 123);
        GameMods().Add(update);
        bool updated;
        string updatedSay;
        try
        {
            var lost = w ? Copies.LosesTo(local) : Copies.LosesTo(copy);
            updated = Copies.Loads(copy) == w && Copies.Loads(local) == !w && lost?.Why == (w ? Copies.LoseReason.HigherVersion : Copies.LoseReason.LocalCopy)
                && ModEntry.HasPendingChanges() == (w || LoadOrder.OrderChanged);
            updatedSay = $"beta rule {w}; loads copy {Copies.Loads(copy)}, local {Copies.Loads(local)}; loser's reason {lost?.Why}; pending {ModEntry.HasPendingChanges()}, "
                + $"moved since launch {LoadOrder.OrderChanged}";
        }
        finally
        {
            GameMods().Remove(update);
            Copies.TestRecount(); // the count as it was with the update in
        }
        Check("local and Workshop both ticked, the Workshop copy updated to a newer version while the game runs: which loads goes by its newest manifest "
            + "(the beta: it; stable: the local copy), and so does pending (a move since launch aside)", updated, updatedSay);

        // A newer Workshop copy beside the local one, both ticked: which loads is the game's rule for this branch (the beta:
        // the higher version; stable: the local copy), and the status, panel lines, chips, banner and relaunch check all say
        // the same; pending only where the new copy loads. Then the copy's box off: its tip says which would load with both
        // on (Load Order's Enable too). Its box and the local one's switch only themselves, and nothing is stored per copy;
        // in a session that can't save our data too.
        var versionWas = copy.manifest?.version;
        if (copy.manifest is { } newer)
            newer.version = "1.1.0";
        CategoryList.Redraw(content);
        await Wait(menu, 0.3);
        var (winner, loser) = w ? (copy, local) : (local, copy);
        var winnerLine = w ? $"Loads on relaunch, in place of {Named(local)}" : Draft(LoadsAlso, ("other", Named(copy)));
        var loserLine = Draft(CopyLoses, ("other", Named(winner)), ("why", w ? WhyHigher : WhyLocal));
        // (An earlier test can leave the order moved since launch, as on stable: a relaunch for that, whichever copy loads.)
        Check("local and Workshop both ticked, the Workshop copy newer: which loads follows the game's rule for this branch (the beta: the higher version; "
            + "stable: the local copy); the other reads Won't load, saying why; pending where the new copy loads (a move since launch aside)",
            Copies.Loads(copy) == w && Copies.Loads(local) == !w && ModDetails.Status(winner).Contains(w ? "Pending relaunch" : "Enabled")
            && ModDetails.Status(loser).Contains(StatusWontLoad) && Line(winner) == winnerLine && Line(loser) == loserLine
            && ModEntry.HasPendingChanges() == (w || LoadOrder.OrderChanged),
            $"beta rule {w}; loads copy {Copies.Loads(copy)}, local {Copies.Loads(local)}; status {ModDetails.Status(local)} / {ModDetails.Status(copy)}; "
            + $"lines {Line(local)} / {Line(copy)}; pending {ModEntry.HasPendingChanges()}, moved since launch {LoadOrder.OrderChanged}");
        var newerChips = new[] { local, copy }.Select(m => Tags.For(m).FirstOrDefault(c => c.Text == "Same ID")?.Tip).ToList();
        RelaunchCheck.Show(screen);
        await Wait(menu, 0.3);
        var relaunchBody = (menu.GetTree().Root.GetNodeOrNull(RelaunchCheck.Name)?.FindChild("Body", true, false) as RichTextLabel)?.GetParsedText() ?? "";
        RelaunchCheck.Cancel();
        await Wait(menu, 0.3);
        Check("... the banner, the chips and the relaunch check name the copy that loads",
            (SameId()?.FindChild("SameIdWarning", true, false) as Label)?.Text == Draft(SameIdText, ("count", "2"))
            && newerChips[0] == Draft(ClashTip, ("others", Named(copy)), ("loads", Named(winner))) && newerChips[1] == Draft(ClashTip, ("others", Named(local)), ("loads", Named(winner)))
            && relaunchBody.Contains($"{Named(loser)}  {Draft(RelaunchLoses, ("other", Named(winner)))}"),
            $"chips {string.Join(" / ", newerChips)}; {relaunchBody}");
        Dependencies.Set([copy], false, screen);
        CategoryList.Redraw(content);
        await Wait(menu, 0.3);
        var tipOff = Draft(CopyOnTip, ("other", Named(local)), ("loads", Named(w ? copy : local)));
        var workshopRow = content.GetChildren().OfType<NModMenuRow>().FirstOrDefault(r => r.Mod == copy);
        var localRow = content.GetChildren().OfType<NModMenuRow>().FirstOrDefault(r => r.Mod == local);
        var boxTip = workshopRow == null ? null : Hover.TipOf(workshopRow.GetNode<Control>("Tickbox"));
        var orderTip = workshopRow == null ? null : LoadOrder.Actions(content, workshopRow, () => { })[0].Tip;
        Check("... the box of the copy that's off says the other is also enabled, and which loads; Load Order's Enable too", boxTip == tipOff && orderTip == tipOff,
            $"box {boxTip}; Load Order {orderTip}");
        if (workshopRow != null && localRow != null)
        {
            // As a click does: the box flips, then Toggled fires.
            async Task Flip(NModMenuRow row)
            {
                var box = row.GetNode<NTickbox>("Tickbox");
                box.IsTicked = !box.IsTicked;
                box.EmitSignal(NTickbox.SignalName.Toggled, box);
                await Wait(menu, 0.3);
            }
            List<SettingsSaveMod> Switches(ModSource source) => SaveManager.Instance.SettingsSave.ModSettings?.ModList.Where(e => e.Id == id && e.Source == source).ToList() ?? [];
            var flips = new List<bool>();
            await Flip(workshopRow); // on
            flips.Add(Copies.Ticked(copy) && Copies.Ticked(local) && NothingStored());
            await Flip(localRow); // off
            flips.Add(Copies.Ticked(copy) && !Copies.Ticked(local) && NothingStored());
            await Flip(localRow); // on
            flips.Add(Copies.Ticked(copy) && Copies.Ticked(local) && NothingStored());
            for (var i = 0; i < 3; i++)
                History.Undo();
            await Wait(menu, 0.3);
            Store.TestReadOnly(true);
            try
            {
                await Flip(workshopRow);
                flips.Add(Copies.Ticked(copy) && Copies.Ticked(local) && Switches(ModSource.SteamWorkshop).All(e => e.IsEnabled)
                    && Switches(ModSource.ModsDirectory).All(e => e.IsEnabled) && NothingStored());
            }
            finally
            {
                Store.TestReadOnly(false);
            }
            History.Undo();
            await Wait(menu, 0.3);
            Check("... a tick on either copy switches it alone (the other stays as it is) and stores nothing per copy; in a session that can't save our "
                + "data too", flips.All(f => f) && flips.Count == 4 && !Copies.Ticked(copy) && Copies.Ticked(local), string.Join(", ", flips));
        }
        else
            Check("... a box each for the local and the Workshop copy", false, $"rows {workshopRow != null}/{localRow != null}");
        if (copy.manifest is { } older)
            older.version = versionWas;
        copy.state = ModLoadState.AddedAtRuntime;

        // Put back: the copy, its row and saved entry, and its data (quietly, in every undo snapshot).
        if (content.GetChildren().OfType<NModMenuRow>().FirstOrDefault(r => r.Mod == copy) is { } copyRow)
        {
            content.RemoveChild(copyRow);
            copyRow.QueueFree();
        }
        GameForgets(copy);
        SaveManager.Instance.SettingsSave.ModSettings?.ModList.RemoveAll(e => e.Id == id && e.Source == ModSource.SteamWorkshop);
        History.Quietly(data =>
        {
            data.Mods.Remove($"{id}|w:123");
            data.Copies?.Remove(id);
        });
        Profiles.Track();
        CategoryList.Redraw(content);
        await Wait(menu, 0.2);
        Check("put back: one copy, its data under its id as before", Copies.Key(local) == id && Store.InfoFor(id)?.Notes == notesBefore
            && Store.InfoFor(id)?.Alias == aliasBefore && Store.Data.Copies?.ContainsKey(id) != true && Edits() == edits, $"edits {Edits()}");
    }

    // The fixture's two copies of one mod id (SmmTestTwinA 1.0.0, SmmTestTwinB 1.1.0 needing Test Framework, the picked
    // one) and Test Twin User, which needs 1.1.0 or newer. dev.cs lists the id after the framework, so A sorts first.
    const string TwinId = "SmmTestTwin";

    // A made-up mod for SameIdLoad's plan, in a folder of its own: its copy "l:<folder>", or "w:<item>" with an item.
    static Mod Made(string id, string folder, ModLoadState state = ModLoadState.None, ulong? item = null, string version = "1.0.0", params string[] needs)
    {
        var mod = new Mod
        {
            path = $"C:/SmmSameIdTest/{folder}", modSource = item == null ? ModSource.ModsDirectory : ModSource.SteamWorkshop, state = state,
            manifest = new ModManifest { id = id, name = id, version = version, dependencies = [.. needs.Select(n => new ModDependency(n))] },
        };
        if (item is { } workshop)
            Compat.SetWorkshopId(mod, workshop);
        return mod;
    }

    // copies_off (the copies off while their switch is on) as our data has it now, a copy (none read: empty); and as our
    // file has it, which our Init reads at the next launch.
    static List<string> CopiesOff() => [.. Store.Data.CopiesOff ?? []];

    static List<string> FileCopiesOff() =>
        [.. (JsonNode.Parse(File.ReadAllText(Store.FilePath))?["copies_off"] as JsonArray ?? []).Select(k => (string?)k).OfType<string>()];

    // What every id and source installed more than once keeps, whatever was done to its copies: its pick installed and
    // never off, its switch on exactly while a copy of it is ticked, and then exactly one copy of the id loading. Null
    // when every one keeps it, else what broke first.
    static string? CopiesBroken()
    {
        var (off, gameOff) = (CopiesOff(), Dependencies.SavedOff());
        var installed = Dependencies.EachCopy(ModManager.Mods.Union(Installed.Mods)).ToList();
        foreach (var (id, source, copies) in Copies.TwinGroups())
        {
            var pick = Store.Data.CopyPicks?.Select(Copies.Parse).FirstOrDefault(p => p is { } k && k.Id == id && k.Source == source) is { } stored ? stored.Copy : null;
            if (pick == null || !copies.Contains(pick))
                return $"{id}/{source}: the pick {pick ?? "none"} isn't one of its copies ({string.Join(", ", copies)})";
            if (off.Contains($"{id}|{pick}"))
                return $"{id}/{source}: the pick {pick} is in copies_off";
            var on = !gameOff.Contains((id, source));
            var ticked = installed.Where(m => m.manifest?.id == id && m.modSource == source && Copies.Ticked(m)).Select(Copies.CopyOf).ToList();
            if (on != ticked.Count > 0)
                return $"{id}/{source}: the switch {(on ? "on" : "off")}, ticked {string.Join(", ", ticked)}";
            if (on && installed.Where(m => m.manifest?.id == id && Copies.Loads(m)).Select(Copies.CopyOf).ToList() is { Count: not 1 } loading)
                return $"{id}/{source}: {loading.Count} copies load ({string.Join(", ", loading)})";
        }
        return null;
    }

    // The fixture twins both ticked, or the pick alone again, as if the launch had them so: copies_off without A, or with
    // it, in every undo snapshot, launch's too (History.Quietly), and the active profile's entry for them likewise, so it's
    // no change, no undo step and no unsaved edit. What loaded is as it was: A set aside (DisabledDuplicate), B.
    static void BothTickedQuietly(bool both)
    {
        const string a = "l:SmmTestTwinA";
        History.Quietly(data =>
        {
            var off = (data.CopiesOff ?? []).Where(k => k != $"{TwinId}|{a}");
            data.CopiesOff = Store.TidyOff(both ? off : off.Append($"{TwinId}|{a}"), data.CopyPicks);
            if (data.Profiles.FirstOrDefault(p => p.Name == data.ActiveProfile) is { } active)
                foreach (var list in new[] { active.Mods, active.Edited })
                    for (var i = 0; i < (list?.Count ?? 0); i++)
                        if (list?[i] is { Id: TwinId, Source: ModSource.ModsDirectory } entry)
                            list[i] = entry with { CopiesOff = both ? [] : [a] };
        });
    }

    // SameIdLoad's look at how the game loads (this launch): our Init found us the first mod still to load, in a
    // List<Mod>, with the DisabledDuplicate state there. It ran before the launch record: the record names each copy of
    // the fixture twins, on for the one that loads.
    static void TestSameIdCanary()
    {
        var (at, first) = SameIdLoad.Self;
        Check("same ID, the canary: our Init the first mod still to load, the game's list a List<Mod>, DisabledDuplicate there; the picks apply",
            SameIdLoad.Canary == true && first == ModEntry.ModId && at >= 0 && at < ModManager.Mods.Count && ModManager.Mods[at].manifest?.id == ModEntry.ModId
            && ModManager.Mods is List<Mod> && Enum.TryParse<ModLoadState>("DisabledDuplicate", out _) && !SameIdLoad.CanaryFailed,
            $"canary {SameIdLoad.Canary}, first still to load {first} at {at}");
        if (ModManager.Mods.Count(m => m.manifest?.id == TwinId) != 2)
        {
            TwinsMissing("same ID, the launch record's copies");
            return;
        }
        var twins = (LaunchGuard.Read(Path.Combine(LaunchGuard.Dir ?? "", LaunchGuard.WorkingFile))?.Mods ?? []).Where(m => m.Id == TwinId).ToList();
        Check("... it ran before the launch record: the record names each copy of the fixture twins, on for the picked one (SmmTestTwinB)",
            twins.Count == 2 && twins.All(m => m.Copy != null) && twins.Where(m => m.Enabled).Select(m => m.Copy).SequenceEqual(["l:SmmTestTwinB"]),
            string.Join(", ", twins.Select(m => $"{m.Copy}={m.Enabled}")));
    }

    // SameIdLoad's plan on made-up lists, as the game's loop has them while our Init runs (us at the first slot still to
    // load): the pick first (no swap), later (swapped through the list's array: the game's loop carries on), a copy decided
    // before us (picked, or not, or the pick gone: nothing waits), the pick's source off or the pick gone (nothing said),
    // the pick set aside by the game's local-vs-Workshop rule, a dependency of the pick after the id's slot (no swap; with
    // a mod after us needing the id, no flip either; one not installed: swapped), the canary (not a List<Mod>, not us
    // first, not the game's own list), three copies, two Workshop copies and a local one, a local and a Workshop copy only,
    // our own second copy, no pick yet and a pick naming no copy installed (with copies_off: the first copy not off, or the
    // first copy where that one can't take the id's slot), a pick that's off too, one Workshop item with two manifests, a
    // set-aside copy's messages kept for its panel; then the self-check's verdicts (Judge) on what the game made of a plan.
    static void TestSameIdPlan()
    {
        const string X = "SmmSameIdX";
        var us = Made(ModEntry.ModId, "SpireModManager");
        var reads = 0;
        // off: copies_off as our file has it (none: a file from before it).
        (bool Canary, Dictionary<string, SameIdLoad.Entry> Plan) PlanOf(IReadOnlyList<Mod> mods, bool live, string[] picks, string[]? off = null)
        {
            var run = SameIdLoad.Run(mods, () =>
            {
                reads++;
                return picks.ToHashSet();
            }, m => m == us, log: false, live: live, readOff: off == null ? null : () => off.ToHashSet());
            return (run.Canary, run.Plan);
        }
        // The game's list is a List<Mod> (a collection expression for a read-only list would make a list of its own kind).
        (bool Canary, Dictionary<string, SameIdLoad.Entry> Plan) Plan(List<Mod> mods, params string[] picks) => PlanOf(mods, live: true, picks);
        static string Copy(Mod? m) => m == null ? "none" : Copies.CopyOf(m);
        static string Say(Dictionary<string, SameIdLoad.Entry> plan, params Mod[] mods) =>
            string.Join("; ", plan.Select(p => $"{p.Key}: loads {Copy(p.Value.Loads)}, aside {string.Join(",", p.Value.SetAside.Select(Copy))}, "
                + $"swapped {p.Value.Swapped} ({p.Value.From}->{p.Value.To}), had pick {p.Value.HadPick}, why {p.Value.Why}"))
            + " | " + string.Join(", ", mods.Select(m => $"{Copy(m)} {m.state}"));
        const ModLoadState None = ModLoadState.None, Aside = ModLoadState.DisabledDuplicate;
        static LocString Error(string key, string? missing = null)
        {
            var error = new LocString("main_menu_ui", key);
            if (missing != null)
                error.Add("missingDependencies", missing);
            return error;
        }

        {
            var (a, b) = (Made(X, "A"), Made(X, "B"));
            List<Mod> list = [us, a, b];
            var (ok, plan) = Plan(list, $"{X}|l:A");
            Check("same ID, the plan: the pick in the id's first slot loads there, the other copy set aside as the game's own duplicate, no swap",
                ok && plan.GetValueOrDefault(X) is { Swapped: false, HadPick: true, Why: SameIdLoad.Reason.None } entry && entry.Loads == a
                && a.state == None && b.state == Aside && list[1] == a, Say(plan, a, b));
        }
        {
            // As in the game's loop: our Init runs while it's on us; the swap must leave its enumerator going.
            var (a, b, c) = (Made(X, "A"), Made(X, "B"), Made("SmmSameIdOther", "C"));
            List<Mod> list = [us, a, c, b];
            var version = typeof(List<Mod>).GetField("_version", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var before = version?.GetValue(list);
            var (ok, seen, thrown) = (false, new List<Mod>(), (string?)null);
            Dictionary<string, SameIdLoad.Entry> plan = [];
            try
            {
                foreach (var mod in list)
                    if (mod == us)
                        (ok, plan) = Plan(list, $"{X}|l:B");
                    else
                        seen.Add(mod);
            }
            catch (InvalidOperationException e)
            {
                thrown = e.Message;
            }
            Check("... the pick later: swapped into the id's first slot through the list's array (its version unchanged: the game's loop carries on), "
                + "the other set aside",
                ok && thrown == null && before != null && before.Equals(version?.GetValue(list)) && plan.GetValueOrDefault(X) is { Swapped: true, From: 3, To: 1 } entry
                && entry.Loads == b && list[1] == b && list[3] == a && seen.SequenceEqual([b, c, a]) && a.state == Aside && b.state == None,
                $"{thrown ?? "no throw"}; {Say(plan, a, b)}");
        }
        {
            var (a, b) = (Made(X, "A", ModLoadState.Loaded), Made(X, "B"));
            var (ok, plan) = Plan([a, us, b], $"{X}|l:B");
            // The copy that loaded the pick, or no pick yet (the update's first launch, ours sorted last): the copies after
            // us set aside; with none after us, nothing to say, nor any entry for the self-check to find "not taken" (the
            // game failed the copy before us as a duplicate already).
            var (c, d, e) = (Made(X, "C", ModLoadState.Loaded), Made(X, "D", ModLoadState.Failed), Made(X, "E"));
            d.errors = [Error("MOD_ERROR.DUPLICATE_ID")];
            var (okPicked, planPicked) = Plan([c, d, us], $"{X}|l:C");
            var (okNone, planNone) = Plan([c, d, us]);
            List<Mod> later = [c, d, us, e];
            var (okAfter, planAfter) = Plan(later);
            // The self-check on that one, as the game leaves it: the duplicate error before us is the game's own.
            var judged = planAfter.GetValueOrDefault(X) is { } set ? SameIdLoad.Judge(X, set, later, 2) : (SameIdLoad.Result?)null;
            Check("... a copy that sorted before us and loaded: the copies after us set aside (no duplicate error), another pick waits for the next "
                + "launch; the loaded copy picked, or no pick, and nothing after us: nothing to say; something after us: taken, the game's duplicate "
                + "error before us notwithstanding",
                ok && plan.GetValueOrDefault(X) is { Why: SameIdLoad.Reason.DecidedBeforeUs } entry && entry.Loads == a && b.state == Aside
                && okPicked && planPicked.Count == 0 && okNone && planNone.Count == 0
                && okAfter && planAfter.GetValueOrDefault(X) is { Why: SameIdLoad.Reason.None, HadPick: false } after && after.Loads == c && e.state == Aside
                && judged == SameIdLoad.Result.Taken,
                $"{Say(plan, a, b)} || {Say(planPicked, c, d)} || {Say(planNone, c, d)} || {Say(planAfter, c, d, e)}, self-check {judged}");
        }
        {
            // The same with a pick naming a copy no longer installed: no pick waits for the next launch.
            var (a, b) = (Made(X, "A", ModLoadState.Loaded), Made(X, "B"));
            var (ok, plan) = Plan([a, us, b], $"{X}|l:Gone");
            var (c, d) = (Made(X, "C", ModLoadState.Loaded), Made(X, "D", ModLoadState.Failed));
            var (okNone, planNone) = Plan([c, d, us], $"{X}|l:Gone");
            Check("... a copy that sorted before us and loaded, the pick naming a copy no longer installed: not one that waits for the next launch (the copy "
                + "after us set aside all the same); nothing after us: nothing to say",
                ok && plan.GetValueOrDefault(X) is { Why: SameIdLoad.Reason.None, HadPick: false } entry && entry.Loads == a && b.state == Aside
                && okNone && planNone.Count == 0, $"{Say(plan, a, b)} || {Say(planNone, c, d)}");
        }
        {
            // The local copy chosen: the Workshop copies off in the game's list, their own pick kept. And a pick naming a
            // Workshop copy since uninstalled, the one left set aside by the game's rule for the local copy.
            var (local, w21, w22) = (Made(X, "L"), Made(X, "W21", ModLoadState.Disabled, 21), Made(X, "W22", ModLoadState.Disabled, 22));
            var (okOff, planOff) = Plan([us, local, w21, w22], $"{X}|w:21");
            var (local2, w32) = (Made(X, "L2"), Made(X, "W32", Aside, 32));
            var (okGone, planGone) = Plan([us, local2, w32], $"{X}|w:31");
            // Two Workshop copies and a local one, all on: the game's rule set the picked older one aside for the local
            // copy, and the local one aside for the newer: the newer loads, the pick set aside by the game, said.
            var (w41, w42, local3) = (Made(X, "W41", Aside, 41, "1.0.0"), Made(X, "W42", None, 42, "3.0.0"), Made(X, "L3", Aside, version: "2.0.0"));
            var (okAside, planAside) = Plan([us, w41, w42, local3], $"{X}|w:41");
            Check("... the pick's source off (the local copy chosen) or the pick uninstalled: nothing said, nothing flipped; the pick set aside by "
                + "the game's local-vs-Workshop rule: said",
                okOff && planOff.Count == 0 && local.state == None && okGone && planGone.Count == 0 && local2.state == None
                && okAside && planAside.GetValueOrDefault(X) is { Why: SameIdLoad.Reason.SetAsideByGame } aside && aside.Loads == w42 && w42.state == None,
                $"{Say(planOff, local, w21, w22)} || {Say(planGone, local2, w32)} || {Say(planAside, w41, w42, local3)}");
        }
        {
            var (a, dep, b) = (Made(X, "A"), Made("SmmSameIdDep", "Dep"), Made(X, "B", needs: ["SmmSameIdDep"]));
            var (ok, plan) = Plan([us, a, dep, b], $"{X}|l:B");
            var (a2, dep2, b2, user) = (Made(X, "A"), Made("SmmSameIdDep", "Dep"), Made(X, "B", needs: ["SmmSameIdDep"]), Made("SmmSameIdUser", "U", needs: [X]));
            var (okUser, planUser) = Plan([us, a2, dep2, b2, user], $"{X}|l:B");
            var (a3, b3, user3) = (Made(X, "A"), Made(X, "B", needs: ["SmmSameIdNotInstalled"]), Made("SmmSameIdUser", "U", needs: [X]));
            List<Mod> gone = [us, a3, b3, user3];
            var (okGone, planGone) = Plan(gone, $"{X}|l:B");
            Check("... the pick needing a mod after the id's first slot: no swap, the flip alone; with a mod after us needing the id, no flip (the game's "
                + "own way); needing a mod not installed: swapped all the same",
                ok && plan.GetValueOrDefault(X) is { Swapped: false, Why: SameIdLoad.Reason.None } entry && entry.Loads == b && a.state == Aside && b.state == None
                && okUser && planUser.GetValueOrDefault(X) is { Why: SameIdLoad.Reason.DependencyAfterSlot } && a2.state == None && b2.state == None
                && okGone && planGone.GetValueOrDefault(X) is { Swapped: true } && gone[1] == b3 && a3.state == Aside,
                $"{Say(plan, a, b)} || {Say(planUser, a2, b2)} || {Say(planGone, a3, b3)}");
        }
        {
            var (a, b) = (Made(X, "A"), Made(X, "B"));
            Mod[] array = [us, a, b];
            var (ok, plan) = PlanOf(array, live: true, [$"{X}|l:B"]);
            var (c, d) = (Made(X, "C"), Made(X, "D"));
            var (notFirst, planNotFirst) = Plan([c, us, d], $"{X}|l:D");
            var (e, f) = (Made(X, "E"), Made(X, "F"));
            var (copied, planCopied) = PlanOf(new List<Mod> { us, e, f }, live: false, [$"{X}|l:F"]);
            Check("... the canary: no flip when the game's list isn't a List<Mod>, when the first mod still to load isn't us, nor when the list is a "
                + "copy of the one the game loads from",
                !ok && plan.Count == 0 && a.state == None && b.state == None && !notFirst && planNotFirst.Count == 0 && d.state == None
                && !copied && planCopied.Count == 0 && e.state == None && f.state == None,
                $"{Say(plan, a, b)} || {Say(planNotFirst, c, d)} || {Say(planCopied, e, f)}");
        }
        {
            var (a, b, c) = (Made(X, "A"), Made(X, "B"), Made(X, "C"));
            List<Mod> list = [us, a, b, c];
            var (ok, plan) = Plan(list, $"{X}|l:C");
            Check("... three copies from one source: the picked one loads, swapped into the first slot, the two others set aside",
                ok && plan.GetValueOrDefault(X) is { Swapped: true } entry && entry.Loads == c && entry.SetAside.Count == 2 && list[1] == c
                && a.state == Aside && b.state == Aside && c.state == None, Say(plan, a, b, c));
        }
        {
            var (w1, w2, local) = (Made(X, "W1", item: 21), Made(X, "W2", item: 22), Made(X, "L"));
            List<Mod> list = [us, w1, w2, local];
            var (ok, plan) = Plan(list, $"{X}|w:22");
            Check("... two Workshop copies and a local one, all still to load: exactly one loads, the picked one",
                ok && plan.GetValueOrDefault(X)?.Loads == w2 && new[] { w1, w2, local }.Count(m => m.state == None) == 1 && list[1] == w2, Say(plan, w1, w2, local));
        }
        {
            var (local, workshop) = (Made(X, "L"), Made(X, "W", Aside, 31));
            var (ok, plan) = Plan([us, local, workshop]);
            var (us2, solo) = (Made(ModEntry.ModId, "SpireModManagerOther"), Made("SmmSameIdSolo", "S"));
            reads = 0;
            var (okOurs, planOurs) = Plan([us, us2, solo]);
            Check("... a local and a Workshop copy only (the game's rule chose): nothing to do; our own second copy is left alone, and no id twice "
                + "means the picks aren't even read", ok && plan.Count == 0 && local.state == None && okOurs && planOurs.Count == 0 && us2.state == None && reads == 0,
                $"{Say(plan, local, workshop)} || {Say(planOurs, us2)}; picks read {reads} times");
        }
        {
            var (a, b) = (Made(X, "A"), Made(X, "B"));
            var (ok, plan) = Plan([us, a, b]);
            var (c, d) = (Made(X, "C"), Made(X, "D"));
            var (okStale, planStale) = Plan([us, c, d], $"{X}|l:Gone");
            Check("... no pick yet, or one naming no copy installed: the game's own choice (the first) loads, the others set aside, no duplicate error",
                ok && plan.GetValueOrDefault(X) is { HadPick: false, Swapped: false } entry && entry.Loads == a && b.state == Aside
                && okStale && planStale.GetValueOrDefault(X) is { HadPick: false } stale && stale.Loads == c && d.state == Aside,
                $"{Say(plan, a, b)} || {Say(planStale, c, d)}");
        }
        {
            // copies_off read with the picks: with no pick naming a copy installed, the first copy not off on our page loads
            // (swapped into the id's first slot), the rest set aside; a pick that's off too (a hand-edited file) loads all
            // the same.
            var (a, b, c) = (Made(X, "A"), Made(X, "B"), Made(X, "C"));
            List<Mod> list = [us, a, b, c];
            var (ok, plan) = PlanOf(list, live: true, [$"{X}|l:Gone"], off: [$"{X}|l:A"]);
            var (d, e) = (Made(X, "D"), Made(X, "E"));
            var (okOff, planOff) = PlanOf(new List<Mod> { us, d, e }, live: true, [$"{X}|l:E"], off: [$"{X}|l:E"]);
            Check("... no pick naming a copy installed, the first copy off on our page: the first one that isn't loads, swapped into the id's first slot, the "
                + "others set aside; a pick that's off (a hand-edited file) loads all the same",
                ok && plan.GetValueOrDefault(X) is { HadPick: false, Swapped: true } entry && entry.Loads == b && list[1] == b && a.state == Aside && c.state == Aside
                && b.state == None && okOff && planOff.GetValueOrDefault(X) is { HadPick: true } picked && picked.Loads == e && d.state == Aside && e.state == None,
                $"{Say(plan, a, b, c)} || {Say(planOff, d, e)}");
        }
        {
            // That first copy not off needing a mod after the id's first slot, with a mod after us needing the id: it can't
            // take the slot, and no pick waits for it, so the game's own choice (the first copy) loads and the rest are set
            // aside (left to the game, it would fail them as duplicates of it).
            var (a, dep, b, user) = (Made(X, "A"), Made("SmmSameIdDep", "Dep"), Made(X, "B", needs: ["SmmSameIdDep"]), Made("SmmSameIdUser", "U", needs: [X]));
            List<Mod> list = [us, a, dep, b, user];
            var (ok, plan) = PlanOf(list, live: true, [$"{X}|l:Gone"], off: [$"{X}|l:A"]);
            Check("... no pick naming a copy installed, the first copy not off on our page needing a mod after the id's first slot, a mod after us needing the "
                + "id: the game's own choice (the first copy) loads, no swap, the other set aside (no duplicate failure)",
                ok && plan.GetValueOrDefault(X) is { HadPick: false, Swapped: false, Why: SameIdLoad.Reason.None } entry && entry.Loads == a && entry.SetAside.SequenceEqual([b])
                && a.state == None && b.state == Aside && list[1] == a, Say(plan, a, b));
        }
        {
            // One Workshop item with two manifests of the id (one copy, w:5) after another item: the pick is that copy, its
            // first manifest swapped into the id's first slot; its second set aside too (the game would fail it).
            var (w6, w5a, w5b) = (Made(X, "W6", item: 6), Made(X, "W5a", item: 5), Made(X, "W5b", item: 5));
            List<Mod> list = [us, w6, w5a, w5b];
            var (ok, plan) = Plan(list, $"{X}|w:5");
            Check("... one Workshop item with two manifests of the id, picked: its first loads, swapped there, the rest set aside",
                ok && plan.GetValueOrDefault(X) is { HadPick: true, Swapped: true } entry && entry.Loads == w5a && list[1] == w5a
                && w6.state == Aside && w5b.state == Aside, Say(plan, w6, w5a, w5b));
        }
        {
            // A copy set aside with a message of the game's on it (a Steam branch error): off its errors, kept for its panel.
            var (a, b) = (Made(X, "A"), Made(X, "B"));
            var message = Error("MOD_ERROR.STEAM_BRANCH_UNSUPPORTED");
            b.errors = [message];
            try
            {
                var (ok, _) = Plan([us, a, b], $"{X}|l:A");
                Check("... a copy set aside: the game's messages on it go from its errors (as the game clears its duplicates'), kept for its info panel",
                    ok && b.state == Aside && b.errors == null && BranchErrors.Dropped(b).Contains(message), $"errors {b.errors?.Count}, kept {BranchErrors.Dropped(b).Count}");
            }
            finally
            {
                BranchErrors.TestForget(b);
            }
        }
        {
            // The self-check on a plan, against the states the game left: as planned, the picked copy failing for its own
            // reasons, two copies loaded, a copy set aside back to load, a duplicate error after us, a mod needing the id
            // missing it, the picked copy not the id's first, and the plan's own reason.
            var (a, b, user) = (Made(X, "A"), Made(X, "B"), Made("SmmSameIdUser", "U", needs: [X]));
            List<Mod> list = [us, a, b, user];
            var (_, plan) = Plan(list, $"{X}|l:A");
            if (plan.GetValueOrDefault(X) is not { } entry)
            {
                Check("... the self-check: a plan to check", false, Say(plan, a, b));
                return;
            }
            var verdicts = new List<string>();
            SameIdLoad.Result Judge(Action game, List<Mod>? mods = null, SameIdLoad.Entry? other = null)
            {
                (a.state, b.state, user.state) = (ModLoadState.Loaded, Aside, ModLoadState.Loaded);
                a.errors = b.errors = user.errors = null;
                game();
                var verdict = SameIdLoad.Judge(X, other ?? entry, mods ?? list, 0);
                verdicts.Add($"{verdict}");
                return verdict;
            }
            Check("... the self-check: taken as planned; the picked copy failed for its own reasons; not taken for two loaded, a set-aside copy back to "
                + "load, a duplicate error after us, a mod needing the id missing it, or the pick not the id's first; a plan with a reason: not applied",
                Judge(() => { }) == SameIdLoad.Result.Taken
                && Judge(() =>
                {
                    a.state = ModLoadState.Failed;
                    a.errors = [Error("MOD_ERROR.GAME_VERSION_UNSUPPORTED")];
                }) == SameIdLoad.Result.PickedFailed
                && Judge(() => b.state = ModLoadState.Loaded) == SameIdLoad.Result.NotTaken
                && Judge(() => b.state = None) == SameIdLoad.Result.NotTaken
                && Judge(() => b.errors = [Error("MOD_ERROR.DUPLICATE_ID")]) == SameIdLoad.Result.NotTaken
                && Judge(() =>
                {
                    user.state = ModLoadState.Failed;
                    user.errors = [Error("MOD_ERROR.MISSING_DEPENDENCY", X)];
                }) == SameIdLoad.Result.NotTaken
                && Judge(() => { }, [us, b, a, user]) == SameIdLoad.Result.NotTaken
                && Judge(() => { }, other: entry with { Why = SameIdLoad.Reason.DependencyAfterSlot }) == SameIdLoad.Result.NotApplied,
                string.Join(", ", verdicts));
        }
    }

    // The fixture twins this launch: the picked copy (SmmTestTwinB, sorted second) loaded, the other set aside as the
    // game's duplicate with no messages, no duplicate error anywhere, the picked copy first of the id in the game's list
    // (so Test Twin User, needing 1.1.0, loaded), swapped there; the saved list has both copies' entries on, ours first;
    // the self-check says taken; and the twins add nothing to the MODDED label's errors. First, every id Apply planned for
    // (real Workshop twins with dev.cs test --workshop a,b --twins too): what loads, whether it was swapped, the self-check.
    static void TestSameIdPick()
    {
        foreach (var (id, e) in SameIdLoad.Plan)
        {
            Results.Add($"INFO same ID: {id} loads {(e.Loads == null ? "none" : Copies.CopyOf(e.Loads))}, swapped {e.Swapped} ({e.From}->{e.To}), "
                + $"had pick {e.HadPick}, why {e.Why}, set aside {string.Join(",", e.SetAside.Select(Copies.CopyOf))}, self-check {SameIdLoad.ResultOf(id)}");
            Check($"same ID, {id}: the self-check taken (or the picked copy failed for its own reasons), the canary fine",
                SameIdLoad.ResultOf(id) is SameIdLoad.Result.Taken or SameIdLoad.Result.PickedFailed && !SameIdLoad.CanaryFailed,
                $"{SameIdLoad.ResultOf(id)}, canary failed {SameIdLoad.CanaryFailed}");
        }
        var copies = ModManager.Mods.Where(m => m.manifest?.id == TwinId).ToList();
        if (copies.Count != 2 || ModManager.Mods.FirstOrDefault(m => m.manifest?.id == "SmmTestTwinUser") is not { } user
            || copies.FirstOrDefault(m => Copies.CopyOf(m) == "l:SmmTestTwinA") is not { } a || copies.FirstOrDefault(m => Copies.CopyOf(m) == "l:SmmTestTwinB") is not { } b)
        {
            TwinsMissing("same ID, the fixture twins");
            return;
        }
        string States() => string.Join(", ", new[] { a, b, user }.Select(m => $"{Copies.CopyOf(m)} {m.state} ({string.Join(",", m.errors?.Select(e => e.LocEntryKey) ?? [])})"));
        Check("same ID, the fixture twins: the picked copy (SmmTestTwinB, sorted second) loaded, the other set aside as the game's duplicate with no messages, "
            + "no duplicate error anywhere, the picked copy the id's first in the game's list, Test Twin User (needs 1.1.0) loaded",
            b.state == ModLoadState.Loaded && a.state == ModLoadState.DisabledDuplicate && a.errors == null && copies[0] == b && user.state == ModLoadState.Loaded
            && !ModManager.Mods.Any(m => m.errors?.Any(e => e.LocEntryKey == "MOD_ERROR.DUPLICATE_ID") == true), States());
        var plan = SameIdLoad.Plan.GetValueOrDefault(TwinId);
        Check("... swapped into the id's first slot, as picked", plan is { Swapped: true, HadPick: true, Why: SameIdLoad.Reason.None } entry
            && entry.Loads == b && entry.SetAside.SequenceEqual([a]) && entry.To < entry.From,
            plan == null ? "no plan" : $"loads {Copies.KeyOf(plan.Loads)}, swapped {plan.Swapped} ({plan.From}->{plan.To}), had pick {plan.HadPick}, why {plan.Why}");
        var list = SaveManager.Instance.SettingsSave.ModSettings?.ModList ?? [];
        Check("... the saved list: both copies' entries on, ours first; the self-check: taken",
            list.Count(e => e.Id == TwinId) == 2 && list.Where(e => e.Id == TwinId).All(e => e.IsEnabled) && list.FirstOrDefault()?.Id == ModEntry.ModId
            && SameIdLoad.ResultOf(TwinId) == SameIdLoad.Result.Taken && !SameIdLoad.Unchosen(TwinId),
            $"{string.Join(", ", list.Select(e => $"{e.Id}={e.IsEnabled}"))}; self-check {SameIdLoad.ResultOf(TwinId)}");
        // The label turns red for a mod that failed or has an error: Test Addon alone (it needs a mod never installed).
        var red = ModManager.Mods.Where(m => m.state == ModLoadState.Failed || (m.errors?.Count ?? 0) > 0).Select(m => m.manifest?.id).ToList();
        Check("... the twins add nothing to the main menu's MODDED errors: Test Addon's alone", new[] { a, b, user }.All(m => m.state != ModLoadState.Failed
            && (m.errors?.Count ?? 0) == 0) && red.All(id => id == "SmmTestAddon"), $"{States()}; failed or with errors: {string.Join(", ", red)}");
    }

    // Settle's part on a made-up saved list: the game's rebuilt list gave both copies of an id the first entry's on (the
    // local one was off at launch): put back, the source that's on first in the id's places, ours at the top; saved
    // once (a second pass changes nothing). And this launch's Settle ran before the baseline: ours first in it, and
    // nothing of the twins changed since launch.
    static void TestSameIdSettle()
    {
        const string X = "SmmSameIdX";
        static SettingsSaveMod E(string id, ModSource source, bool on) => new() { Id = id, Source = source, IsEnabled = on };
        List<SettingsSaveMod> list = [E("SmmSameIdOther", ModSource.ModsDirectory, true), E(X, ModSource.ModsDirectory, true), E(X, ModSource.SteamWorkshop, true),
            E(ModEntry.ModId, ModSource.ModsDirectory, true)];
        List<(string, ModSource, bool)> read = [(X, ModSource.ModsDirectory, false), (X, ModSource.SteamWorkshop, true), (ModEntry.ModId, ModSource.ModsDirectory, true)];
        var first = SameIdLoad.SettleList(list, read);
        var again = SameIdLoad.SettleList(list, read);
        var said = string.Join(", ", list.Select(e => $"{e.Id}/{e.Source}={e.IsEnabled}"));
        Check("same ID, settling a rebuilt list: each source's on/off as the game read it at launch, the one on first in the id's places, ours at the top; "
            + "once (a second pass has nothing to save)", first && !again
            && said == $"{ModEntry.ModId}/ModsDirectory=True, SmmSameIdOther/ModsDirectory=True, {X}/SteamWorkshop=True, {X}/ModsDirectory=False", said);
        // A copy new this launch (its source not in the list the game read): it keeps what the rebuild gave it (the
        // id's first entry's, as the game does without us), and nothing else changes.
        List<SettingsSaveMod> withNew = [E(ModEntry.ModId, ModSource.ModsDirectory, true), E(X, ModSource.ModsDirectory, false), E(X, ModSource.SteamWorkshop, false)];
        var newChanged = SameIdLoad.SettleList(withNew, [(ModEntry.ModId, ModSource.ModsDirectory, true), (X, ModSource.ModsDirectory, false)]);
        Check("... a copy new this launch (its source not in the list read): it keeps the rebuild's, nothing to save", !newChanged
            && withNew.Select(e => e.IsEnabled).SequenceEqual([true, false, false]), string.Join(", ", withNew.Select(e => $"{e.Id}/{e.Source}={e.IsEnabled}")));
        // Ours installed twice, a copy read off at launch (unticked on the game's page): every entry of ours on, as we loaded
        // (so one still loads if the copy running is removed), whatever the launch read; once.
        List<SettingsSaveMod> ours = [E(ModEntry.ModId, ModSource.ModsDirectory, true), E(ModEntry.ModId, ModSource.SteamWorkshop, false), E(X, ModSource.ModsDirectory, true)];
        List<(string, ModSource, bool)> oursRead = [(ModEntry.ModId, ModSource.ModsDirectory, true), (ModEntry.ModId, ModSource.SteamWorkshop, false), (X, ModSource.ModsDirectory, true)];
        var oursFirst = SameIdLoad.SettleList(ours, oursRead);
        var oursAgain = SameIdLoad.SettleList(ours, oursRead);
        Check("... ours installed twice, a copy read off at launch: every entry of ours on, whatever the launch read; once (a second pass has nothing to save)",
            oursFirst && !oursAgain && ours.Where(e => e.Id == ModEntry.ModId).All(e => e.IsEnabled) && ours.Count(e => e.Id == ModEntry.ModId) == 2,
            string.Join(", ", ours.Select(e => $"{e.Id}/{e.Source}={e.IsEnabled}")) + $"; first {oursFirst}, again {oursAgain}");
        // This launch: Settle ran before the baseline (its moves are no change since launch, nor an undo step), ours first in
        // it and in the saved list. Where ours didn't load first (Better Mod Menu listed before us, with --bmm), it moved us.
        // Our data from before copies_off (the fixture's file) read as it was, only the pick on, before the baseline too: in
        // launch's snapshot and our file, and no change since launch.
        var loadedFirst = ModManager.Mods.FirstOrDefault()?.manifest?.id == ModEntry.ModId;
        const string offA = $"{TwinId}|l:SmmTestTwinA";
        var migrated = Copies.TwinGroups().All(g => g.Id != TwinId) || History.Loaded is { } launch && Store.PicksIn(launch.Store).Off?.Contains(offA) == true
            && FileCopiesOff().Contains(offA) && Store.Data.Version == 1;
        Check("... this launch's settling: before the baseline, which has ours first, as the saved list does; the twins' copy not picked off there (copies_off, "
            + "read from a file from before it, in our file too); no change since launch for the twins, nothing moved since",
            SameIdLoad.Settled && History.Loaded?.Mods.FirstOrDefault()?.Id == ModEntry.ModId && !HistoryBar.Describe().Any(l => l.Contains("Test Twin"))
            && SaveManager.Instance.SettingsSave.ModSettings?.ModList.FirstOrDefault()?.Id == ModEntry.ModId && HistoryBar.Moves().Count == 0
            && !LoadOrder.OrderChanged && migrated, $"{string.Join(" | ", HistoryBar.Describe())}; off now {string.Join(", ", CopiesOff())}, in our file "
            + $"{string.Join(", ", FileCopiesOff())}, version {Store.Data.Version}");
        if (loadedFirst)
            Results.Add("INFO same ID, settling: ours loaded first this launch, so Settle had nothing of ours to move");
    }

    // copies_off (StoreData.CopiesOff) at the main menu, as saved: sorted, read back the same by a fresh load and from a
    // snapshot. Then made-up copies of an id (A, B and C, in the game's list for a moment, B the one loaded), and what
    // Reconcile makes of data: from before copies_off (only the pick on: every other copy off, once), with none off
    // (nothing seeded; a copy new since its last session reads ticked, with no entry), with the pick in it (it comes
    // out), and with every copy in it and the pick gone (the copy that loaded picked, and out of it). The version stays.
    // Last, a copy in it that's alone on its switch now: its entry goes, so a twin arriving later doesn't take over, and
    // the pick still names the copy gone, which loads once it's back.
    static void TestCopiesOff()
    {
        if (Copies.TwinGroups().All(g => g.Id != TwinId))
        {
            TwinsMissing("copies_off");
            return;
        }
        var (now, file, fresh) = (CopiesOff(), FileCopiesOff(), Store.LoadFrom(Store.Dir, DateTime.Now).Data?.CopiesOff);
        Check("copies_off: the fixture twins' copy not picked off, written sorted, read back the same by a fresh load and from a snapshot",
            now.Contains($"{TwinId}|l:SmmTestTwinA") && !now.Contains($"{TwinId}|l:SmmTestTwinB") && file.SequenceEqual(now)
            && file.SequenceEqual(file.Order(StringComparer.Ordinal)) && fresh?.SequenceEqual(now) == true
            && Store.PicksIn(Store.EditsJson()).Off?.SetEquals(now) == true,
            $"now {string.Join(", ", now)}; file {string.Join(", ", file)}; fresh {string.Join(", ", fresh ?? [])}");
        const string X = "SmmSameIdOff";
        var (a, b, c) = (Made(X, "A"), Made(X, "B", ModLoadState.Loaded), Made(X, "C"));
        GameMods().AddRange([a, b, c]);
        try
        {
            static List<string> OffOf(StoreData data) => [.. (data.CopiesOff ?? []).Where(k => Copies.IdOf(k) == X)];
            static string? PickOf(StoreData data) => data.CopyPicks?.FirstOrDefault(k => Copies.IdOf(k) == X);
            static string Say(StoreData data) => $"pick {PickOf(data)}, off [{string.Join(", ", OffOf(data))}], record {string.Join(",", data.Copies?.GetValueOrDefault(X) ?? [])}";
            var old = new StoreData { CopyPicks = [$"{X}|l:A"] };
            var (first, second) = (Copies.Reconcile(old), Copies.Reconcile(old));
            var none = new StoreData { CopyPicks = [$"{X}|l:A"], CopiesOff = [], Copies = new() { [X] = ["l:A", "l:B"] } };
            Copies.Reconcile(none);
            var picked = new StoreData { CopyPicks = [$"{X}|l:A"], CopiesOff = [$"{X}|l:A", $"{X}|l:C"] };
            Copies.Reconcile(picked);
            var all = new StoreData { CopyPicks = [$"{X}|l:Gone"], CopiesOff = [$"{X}|l:A", $"{X}|l:B", $"{X}|l:C"] };
            Copies.Reconcile(all);
            Check("copies_off: data from before it gets every copy but the pick off, once; none off seeds nothing, a copy new since its last session reading "
                + "ticked (no entry); a pick in it comes out; every copy in it with the pick gone: the copy that loaded picked, and out of it; the version stays",
                first && !second && OffOf(old).SequenceEqual([$"{X}|l:B", $"{X}|l:C"]) && PickOf(old) == $"{X}|l:A" && old.Version == 1
                && OffOf(none).Count == 0 && none.Copies?.GetValueOrDefault(X)?.Contains("l:C") == true
                && OffOf(picked).SequenceEqual([$"{X}|l:C"]) && PickOf(picked) == $"{X}|l:A"
                && OffOf(all).SequenceEqual([$"{X}|l:A", $"{X}|l:C"]) && PickOf(all) == $"{X}|l:B",
                $"old {Say(old)} (changed {first}, then {second}); none {Say(none)}; picked {Say(picked)}; all {Say(all)}");
        }
        finally
        {
            foreach (var made in new[] { a, b, c })
                GameMods().Remove(made);
        }

        // A copy in copies_off from when it had a twin (that one uninstalled since, the pick still naming it), alone on its
        // switch now: it reads ticked and its entry goes at the menu (Reconcile), a profile's entry the same (HoldCopies),
        // while the pick and the profile's copy still name the copy gone, which loads again once it's back. A new twin
        // arriving before the next launch instead doesn't take over, wherever the game lists it: the copy that was there
        // loads (our record has it, not the new one), the new one set aside, and at the menu both are ticked, the pick
        // still the copy gone. That one back too: it loads, and the active profile naming it isn't Unsaved.
        const string Y = "SmmSameIdAlone";
        var alone = Made(Y, "A", ModLoadState.Loaded);
        var data = new StoreData { CopyPicks = [$"{Y}|l:C"], CopiesOff = [$"{Y}|l:A"], Copies = new() { [Y] = ["l:A", "l:C"] } };
        GameMods().Add(alone);
        bool tickedAlone;
        var held = new Profile { Name = "Alone", Mods = [new ProfileMod(Y, ModSource.ModsDirectory, true, "l:C", CopiesOff: ["l:A"])] };
        try
        {
            tickedAlone = Copies.TickedIn(alone, [], new Picks(data.CopyPicks, data.Copies, data.CopiesOff));
            Copies.Reconcile(data);
            Profiles.TestHold(held, active: true);
        }
        finally
        {
            GameMods().Remove(alone);
        }
        var aloneOff = data.CopiesOff?.Where(k => Copies.IdOf(k) == Y).ToList() ?? [];
        var alonePicked = data.CopyPicks?.FirstOrDefault(k => Copies.IdOf(k) == Y);
        var heldEntry = held.Mods.Single();
        var us = Made(ModEntry.ModId, "SpireModManager");
        // A launch with these copies (one id's) and our data as given: its plan for the id.
        SameIdLoad.Entry? Launch(StoreData from, params Mod[] copies) => SameIdLoad.Run(new List<Mod>(copies.Prepend(us)), () => from.CopyPicks?.ToHashSet(),
            m => m == us, log: false, readOff: () => from.CopiesOff?.ToHashSet(), readRecord: () => from.Copies).Plan.GetValueOrDefault(copies[0].manifest?.id ?? "");
        var (backA, backC) = (Made(Y, "A"), Made(Y, "C"));
        var back = Launch(data, backA, backC) is { HadPick: true } backPlan && backPlan.Loads == backC && backA.state == ModLoadState.DisabledDuplicate;
        var (again, come) = (Made(Y, "A"), Made(Y, "D"));
        // The new twin listed first by the game: the copy that was there still loads (swapped into the id's first slot).
        var plan = Launch(data, come, again);
        var launched = plan?.Loads == again && again.state == ModLoadState.None && come.state == ModLoadState.DisabledDuplicate;
        var launchSay = $"loads {(plan?.Loads is { } loads ? Copies.CopyOf(loads) : "none")}, {Copies.CopyOf(again)} {again.state}, {Copies.CopyOf(come)} {come.state}";
        again.state = ModLoadState.Loaded;
        GameMods().AddRange([again, come]); // two: the count the copies are worked out by differs from each before (Copies.ById)
        string? pickAfter, loadsAfter;
        bool bothTicked;
        try
        {
            Copies.Reconcile(data);
            pickAfter = data.CopyPicks?.FirstOrDefault(k => Copies.IdOf(k) == Y);
            var picks = new Picks(data.CopyPicks, data.Copies, data.CopiesOff);
            bothTicked = Copies.TickedIn(again, [], picks) && Copies.TickedIn(come, [], picks);
            loadsAfter = picks.Picked(Y, ModSource.ModsDirectory);
        }
        finally
        {
            GameMods().Remove(again);
            GameMods().Remove(come);
        }
        Check("copies_off: a copy in it alone on its switch now (its twin uninstalled) reads ticked and loses its entry at the menu, a profile's entry "
            + "the same, the pick and the profile's copy still the copy gone, which loads when it's back; a new twin arriving before the next launch, "
            + "listed first, doesn't take over (the copy that was there loads, the new one set aside; then both ticked, the old one loading, the pick still "
            + "the copy gone)",
            tickedAlone && aloneOff.Count == 0 && alonePicked == $"{Y}|l:C" && heldEntry is { Copy: "l:C", CopiesOff: [] } && back && launched
            && pickAfter == $"{Y}|l:C" && loadsAfter == "l:A" && bothTicked,
            $"ticked {tickedAlone}, off after [{string.Join(", ", aloneOff)}], picked {alonePicked}; profile {heldEntry.Copy} off "
            + $"[{string.Join(", ", heldEntry.CopiesOff ?? [])}]; back loads {back}; launch: {launchSay}; then pick {pickAfter}, loads {loadsAfter}, "
            + $"both ticked {bothTicked}");

        // Then another profile naming the copy that loads is applied, and the first one again (as a switch, Discard or an
        // import applies one): the copy gone is the pick again (Write picks only among those installed). Back, it loads, and
        // the profile naming it isn't Unsaved.
        string? Pick() => Store.Data.CopyPicks?.FirstOrDefault(k => Copies.IdOf(k) == Y);
        var reapplied = WithCopies(data, Y, [again, come], () =>
        {
            Profiles.TestApply([heldEntry with { Copy = "l:A" }]);
            var other = Pick();
            Profiles.TestApply([heldEntry]);
            (data.CopyPicks, data.CopiesOff) = (Store.TidyPicks([Pick()]), CopiesOff().Where(k => Copies.IdOf(k) == Y).ToList());
            return $"{other}, then {Pick()}";
        });
        var (thenA, thenC, thenD) = (Made(Y, "A"), Made(Y, "C"), Made(Y, "D"));
        var thenPlan = Launch(data, thenD, thenA, thenC);
        if (thenPlan?.Loads is { } thenLoads)
            thenLoads.state = ModLoadState.Loaded;
        var thenUnsaved = Unsaved(data, heldEntry, thenA, thenC, thenD);
        Check("copies: the pick missing (two copies, a new twin), another profile naming the copy loading applied, then the first again: the pick is the "
            + "copy gone again; back, it loads, and the profile naming it isn't Unsaved",
            reapplied == $"{Y}|l:A, then {Y}|l:C" && thenPlan is { HadPick: true } && thenPlan.Loads == thenC && thenUnsaved == null,
            $"picks {reapplied}; back: loads {PlanLoads(thenPlan)}, unsaved {thenUnsaved ?? "none"}");

        // Three copies from one source, the picked one missing for a launch: the other two still share the switch. The pick
        // and the profile's copy stay the copy gone, as the mod list now has it (what Save keeps; an import turning it off
        // shows it); the one the game loaded loads meanwhile, out of copies_off and the profile's. Back, the pick loads, and
        // the profile naming it isn't Unsaved; a fourth copy before then doesn't take over.
        const string Z = "SmmSameIdThree";
        var three = new StoreData { CopyPicks = [$"{Z}|l:B"], CopiesOff = [$"{Z}|l:A", $"{Z}|l:C"], Copies = new() { [Z] = ["l:A", "l:B", "l:C"] } };
        var threeHeld = new Profile { Name = "Three", Mods = [new ProfileMod(Z, ModSource.ModsDirectory, true, "l:B", CopiesOff: ["l:A", "l:C"])] };
        var (missA, missC) = (Made(Z, "A"), Made(Z, "C"));
        if (Launch(three, missA, missC)?.Loads is { } meanwhile)
            meanwhile.state = ModLoadState.Loaded;
        // Profiles reads our data: it has the made-up copies' meanwhile, as that launch's menu has them.
        var (threeLoads, threeNow) = WithCopies(three, Z, [missA, missC], () =>
        {
            Copies.Reconcile(three);
            Profiles.TestHold(threeHeld, active: true);
            return (new Picks(three.CopyPicks, three.Copies, three.CopiesOff).Picked(Z, ModSource.ModsDirectory),
                Profiles.PlanImport([new(Z, false, "Z", "")]).Mods.FirstOrDefault(m => m.Id == Z)?.Copy);
        });
        var (threePick, threeOff) = (three.CopyPicks?.FirstOrDefault(k => Copies.IdOf(k) == Z), three.CopiesOff?.Where(k => Copies.IdOf(k) == Z).ToList() ?? []);
        var (zA, zB, zC) = (Made(Z, "A"), Made(Z, "B"), Made(Z, "C"));
        var threePlan = Launch(three, zA, zB, zC);
        if (threePlan?.Loads is { } threeLoaded)
            threeLoaded.state = ModLoadState.Loaded;
        var threeEntry = threeHeld.Mods.Single();
        var threeUnsaved = Unsaved(three, threeEntry, zA, zB, zC);
        Check("copies: three from one source, the picked one missing for a launch: the pick and the profile's copy stay it, as the mod list has it; the "
            + "copy loaded loads meanwhile, out of copies_off and the profile's; back, the pick loads, and the profile naming it isn't Unsaved",
            threePick == $"{Z}|l:B" && threeLoads == "l:A" && threeOff.SequenceEqual([$"{Z}|l:C"]) && threeEntry is { Copy: "l:B", CopiesOff: ["l:C"] }
            && threeNow == "l:B" && threePlan is { HadPick: true } && threePlan.Loads == zB && threeUnsaved == null,
            $"pick {threePick}, loads {threeLoads}, off [{string.Join(", ", threeOff)}], profile {threeEntry.Copy} off [{string.Join(", ", threeEntry.CopiesOff ?? [])}], "
            + $"now {threeNow}; back: loads {PlanLoads(threePlan)}, unsaved {threeUnsaved ?? "none"}");
        var (fourA, fourC, fourD) = (Made(Z, "A"), Made(Z, "C"), Made(Z, "D"));
        var fourPlan = Launch(three, fourD, fourA, fourC);
        Check("... then a fourth copy installed before the next launch (the game closed), listed first: the copy loaded meanwhile loads again",
            fourPlan is { HadPick: false } && fourPlan.Loads == fourA, $"loads {PlanLoads(fourPlan)}");

        // Three copies from one source, B missing, C the pick and loaded, A and B off. A profile naming B with C off, applied
        // (a switch, Discard or an import): the pick stays A, the copy Write picks, and C stays off, so the copy the page says
        // loads is the one the next launch loads. Then one naming B with A off: C, loading in B's place, is the copy it loads,
        // so B is the pick again (out of copies_off), and the page and the launch agree again. Then the active profile naming
        // B with C off has C on, as our data has it; another profile keeps C off.
        const string W = "SmmSameIdNamed";
        var named = new StoreData { CopyPicks = [$"{W}|l:C"], CopiesOff = [$"{W}|l:A", $"{W}|l:B"], Copies = new() { [W] = ["l:A", "l:B", "l:C"] } };
        static ProfileMod NamesB(string off) => new(W, ModSource.ModsDirectory, true, "l:B", CopiesOff: [off]);
        var (namedActive, namedOther) = (new Profile { Name = "Active", Mods = [NamesB("l:C")] }, new Profile { Name = "Other", Mods = [NamesB("l:C")] });
        var (namedOff, namedOn) = WithCopies(named, W, [Made(W, "A", ModLoadState.DisabledDuplicate), Made(W, "C", ModLoadState.Loaded)], () =>
        {
            // Our data for the id now, and the copy the page says loads after a relaunch.
            (StoreData Data, string Loading) NamedNow() => (new StoreData { CopyPicks = Store.TidyPicks(Store.Data.CopyPicks?.Where(k => Copies.IdOf(k) == W)),
                CopiesOff = [.. CopiesOff().Where(k => Copies.IdOf(k) == W)], Copies = named.Copies }, Copies.Loading(W) is { } loading ? Copies.CopyOf(loading) : "none");
            Profiles.TestApply([NamesB("l:C")]);
            var cOff = NamedNow();
            Profiles.TestApply([NamesB("l:A")]);
            var cOn = NamedNow();
            Profiles.TestHold(namedActive, active: true);
            Profiles.TestHold(namedOther, active: false);
            return (cOff, cOn);
        });
        var (namedOffPlan, namedOnPlan) = (Launch(namedOff.Data, Made(W, "A"), Made(W, "C")), Launch(namedOn.Data, Made(W, "A"), Made(W, "C")));
        var (namedActiveOff, namedOtherOff) = (namedActive.Mods.Single().CopiesOff ?? [], namedOther.Mods.Single().CopiesOff ?? []);
        static string NamedSay(StoreData data) => $"pick {string.Join(", ", data.CopyPicks ?? [])}, off [{string.Join(", ", data.CopiesOff ?? [])}]";
        Check("copies: three from one source, the pick loaded and another missing: a profile naming the missing one with the loaded one off, applied, keeps "
            + "the copy Write picks and the loaded one off, and the copy the page says loads is the one the next launch loads; one naming it with the loaded one "
            + "on makes it the pick again (out of copies_off), page and launch agreeing; then the active profile naming it has the loaded one on, another "
            + "keeps it off",
            namedOff.Data.CopyPicks?.SequenceEqual([$"{W}|l:A"]) == true && namedOff.Data.CopiesOff?.SequenceEqual([$"{W}|l:B", $"{W}|l:C"]) == true
            && namedOff.Loading == "l:A" && PlanLoads(namedOffPlan) == namedOff.Loading
            && namedOn.Data.CopyPicks?.SequenceEqual([$"{W}|l:B"]) == true && namedOn.Data.CopiesOff?.SequenceEqual([$"{W}|l:A"]) == true
            && namedOn.Loading == "l:C" && PlanLoads(namedOnPlan) == namedOn.Loading
            && namedActiveOff.Count == 0 && namedOtherOff.SequenceEqual(["l:C"]),
            $"C off: {NamedSay(namedOff.Data)}, page {namedOff.Loading}, launch {PlanLoads(namedOffPlan)}; C on: {NamedSay(namedOn.Data)}, page {namedOn.Loading}, "
            + $"launch {PlanLoads(namedOnPlan)}; active off [{string.Join(", ", namedActiveOff)}], other off [{string.Join(", ", namedOtherOff)}]");
        // The stand-in has to be the copy the next launch loads with no pick (our record's first not off), not only a copy
        // the profile has on. C the pick and loaded, nothing off: a profile naming B with nothing off keeps C (with B the
        // pick, A would load next). A loaded, C the pick and A off: the same profile makes B the pick again, A loading in its
        // place, page and launch agreeing.
        (StoreData Data, string Loading) NamesBOn(StoreData data, ModLoadState a, ModLoadState c) => WithCopies(data, W, [Made(W, "A", a), Made(W, "C", c)], () =>
        {
            Profiles.TestApply([new(W, ModSource.ModsDirectory, true, "l:B", CopiesOff: [])]);
            return (new StoreData { CopyPicks = Store.TidyPicks(Store.Data.CopyPicks?.Where(k => Copies.IdOf(k) == W)), CopiesOff = [.. CopiesOff().Where(k => Copies.IdOf(k) == W)],
                Copies = named.Copies }, Copies.Loading(W) is { } loading ? Copies.CopyOf(loading) : "none");
        });
        var keepsC = NamesBOn(new StoreData { CopyPicks = [$"{W}|l:C"], CopiesOff = [], Copies = named.Copies }, ModLoadState.DisabledDuplicate, ModLoadState.Loaded);
        var backB = NamesBOn(new StoreData { CopyPicks = [$"{W}|l:C"], CopiesOff = [$"{W}|l:A"], Copies = named.Copies }, ModLoadState.Loaded, ModLoadState.DisabledDuplicate);
        var (keepsCPlan, backBPlan) = (Launch(keepsC.Data, Made(W, "A"), Made(W, "C")), Launch(backB.Data, Made(W, "A"), Made(W, "C")));
        Check("copies: a profile naming the missing copy makes it the pick again only where the copy loading in its place is the one the next launch loads "
            + "with no pick: kept C where A would load next, B again where A loads in its place, the page and the launch agreeing both times",
            keepsC.Data.CopyPicks?.SequenceEqual([$"{W}|l:C"]) == true && keepsC.Loading == "l:C" && PlanLoads(keepsCPlan) == "l:C"
            && backB.Data.CopyPicks?.SequenceEqual([$"{W}|l:B"]) == true && backB.Loading == "l:A" && PlanLoads(backBPlan) == "l:A",
            $"C loaded: {NamedSay(keepsC.Data)}, page {keepsC.Loading}, launch {PlanLoads(keepsCPlan)}; A loaded: {NamedSay(backB.Data)}, page {backB.Loading}, "
            + $"launch {PlanLoads(backBPlan)}");
        // Our record orders a source's own copies, never the game's choice between sources: with no pick, a local copy the
        // game lists first loads though the record has a Workshop copy first; of two Workshop copies listed first, the one
        // the record has (not the new one) loads.
        const string V = "SmmSameIdSources";
        var sources = new StoreData { Copies = new() { [V] = ["w:12", "l:L"] } };
        var localFirst = Launch(sources, Made(V, "L"), Made(V, "W12", item: 12));
        var workshopFirst = Launch(sources, Made(V, "W13", item: 13), Made(V, "W12", item: 12), Made(V, "L"));
        Check("copies: with no pick, our record orders a source's own copies, not the game's choice between sources: the local copy listed first loads "
            + "though the record has a Workshop copy first; of two Workshop copies listed first, the one the record has loads",
            PlanLoads(localFirst) == "l:L" && PlanLoads(workshopFirst) == "w:12", $"local first: loads {PlanLoads(localFirst)}; Workshop first: loads {PlanLoads(workshopFirst)}");
    }

    // The copy a plan loads, for a check's details.
    static string PlanLoads(SameIdLoad.Entry? plan) => plan?.Loads is { } loads ? Copies.CopyOf(loads) : "none";

    // The active profile's unsaved changes with these copies of one id installed, our data for it as data has it and the
    // profile's entry for it as given (WithCopies): null for none, else what they are.
    static string? Unsaved(StoreData data, ProfileMod entry, params Mod[] copies) => Profiles.Edited ? "the profile was Unsaved before"
        : WithCopies(data, entry.Id, copies, () =>
        {
            History.Quietly(now => now.Profiles.FirstOrDefault(p => p.Name == now.ActiveProfile)?.Mods.Add(entry));
            Profiles.Track();
            var (on, off, switched) = Profiles.UnsavedChanges();
            return !Profiles.Edited ? null : $"on {on.Count}, off {off.Count}, switched {string.Join(", ", switched.Select(s => $"{Copies.CopyOf(s.From)} to {Copies.CopyOf(s.To)}"))}";
        });

    // What read gives with these copies of one id installed and our data's record, pick and copies off for it as data has
    // them (in our data, every undo snapshot and the game's list for a moment, quietly, as a launch would find them; then
    // taken out again, with the id's profile entries).
    static T WithCopies<T>(StoreData data, string id, Mod[] copies, Func<T> read)
    {
        bool Ours(string key) => Copies.IdOf(key) == id;
        GameMods().AddRange(copies);
        Copies.TestRecount();
        try
        {
            History.Quietly(now =>
            {
                (now.Copies ??= [])[id] = [.. data.Copies?.GetValueOrDefault(id) ?? []];
                now.CopyPicks = Copies.WithPicks(now.CopyPicks, data.CopyPicks?.Where(Ours) ?? []);
                now.CopiesOff = Store.TidyOff([.. now.CopiesOff ?? [], .. data.CopiesOff?.Where(Ours) ?? []], now.CopyPicks);
            });
            return read();
        }
        finally
        {
            foreach (var copy in copies)
                GameMods().Remove(copy);
            Copies.TestRecount();
            History.Quietly(now =>
            {
                now.Copies?.Remove(id);
                now.CopyPicks = Store.TidyPicks(now.CopyPicks?.Where(k => !Ours(k)));
                now.CopiesOff = now.CopiesOff?.Where(k => !Ours(k)).ToList();
                foreach (var profile in now.Profiles)
                {
                    profile.Mods.RemoveAll(m => m.Id == id);
                    profile.Edited?.RemoveAll(m => m.Id == id);
                }
            });
            Profiles.Track();
        }
    }

    // The launch record per copy (LaunchGuard): a copy round-trips, a record from before copies still reads; a switch to
    // another copy is one Switched change and offers Restore (the game's list alone would be the same); Restore before our
    // data loads writes the pick into the file and takes it out of copies_off, the rest of it kept, a backup first; the
    // list it writes has ours first;
    // the notice says "switched copy: 1 → 2".
    static async Task TestSameIdLaunchGuard(NMainMenu menu, string outDir)
    {
        const string X = "SmmSameIdX";
        static LaunchGuard.Entry Copy(string copy, bool on) => new(X, ModSource.SteamWorkshop, on, "1.0", copy);
        var ours = new LaunchGuard.Entry(ModEntry.ModId, ModSource.ModsDirectory, true, "1.0");
        List<LaunchGuard.Entry> working = [ours, Copy("w:1", true), Copy("w:2", false)];
        List<LaunchGuard.Entry> tried = [ours, Copy("w:1", false), Copy("w:2", true)];
        var dir = Path.Combine(outDir, "same-id-launch");
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
        LaunchGuard.Begin(dir, new(DateTime.Now, working));
        var back = LaunchGuard.Read(Path.Combine(dir, LaunchGuard.PendingFile));
        var old = Path.Combine(dir, "old.json");
        File.WriteAllText(old, "{\"at\":\"2026-10-14T09:30:00\",\"mods\":[{\"id\":\"SmmTestLib\",\"source\":\"ModsDirectory\",\"enabled\":true,\"version\":\"1.0\"}]}");
        Check("same ID, the launch record: a copy round-trips; a record from before copies were told apart still reads",
            back?.Mods.SequenceEqual(working) == true && LaunchGuard.Read(old) is { Mods: [{ Copy: null, Enabled: true }] },
            string.Join(", ", back?.Mods.Select(m => $"{m.Id}/{m.Copy}={m.Enabled}") ?? []));
        var changes = LaunchGuard.Changes(tried, working);
        var offer = LaunchGuard.CrashOffer(new(DateTime.Now, tried), new(DateTime.Now.AddHours(-1), working),
            [.. LaunchGuard.Written(working).Select(e => (e.Id, e.Source, e.IsEnabled))]);
        Check("... a switch to another copy: one Switched change (from, to), and Restore offered though the game's list would be the same",
            changes is [{ Kind: LaunchGuard.Kind.Switched, From: "w:1", To: "w:2" } switched] && switched.Id == X && offer != null,
            string.Join(", ", changes.Select(c => $"{c.Kind} {c.Id} {c.From} {c.To}")) + $"; offer {offer != null}");
        // The first launch after the update, against a working record from before copies were told apart (each copy its
        // id's switch, no Copy): no change at all.
        List<LaunchGuard.Entry> oldWorking = [ours, new(X, ModSource.SteamWorkshop, true, "1.0"), new(X, ModSource.SteamWorkshop, true, "1.0")];
        var sinceOld = LaunchGuard.Changes([ours, Copy("w:1", false), Copy("w:2", true)], oldWorking);
        Check("... against a record from before copies were told apart: no change", sinceOld.Count == 0,
            string.Join(", ", sinceOld.Select(c => $"{c.Kind} {c.Id} {c.From} {c.To}")));
        var written = LaunchGuard.Written([Copy("w:1", true), Copy("w:2", false), ours]);
        Check("... the list a Restore writes: ours first, the copies' one switch on", written.FirstOrDefault()?.Id == ModEntry.ModId
            && written.Where(e => e.Id == X).All(e => e.IsEnabled), string.Join(", ", written.Select(e => $"{e.Id}={e.IsEnabled}")));
        var data = Path.Combine(dir, "data");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "manager.json"), "{\n  \"version\": 1,\n  \"mods\": { \"SmmTestUi\": { \"notes\": \"kept\" } },\n"
            + "  \"copy_picks\": [\"SmmSameIdY|w:5\", \"SmmSameIdX|w:2\"],\n  \"copies_off\": [\"SmmSameIdX|w:1\", \"SmmSameIdY|w:6\"]\n}");
        var patched = LaunchGuard.PatchPicks(data, LaunchGuard.PicksOf(working));
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(data, "manager.json")));
        var picks = (json?["copy_picks"] as JsonArray)?.Select(p => (string?)p).ToList() ?? [];
        var off = (json?["copies_off"] as JsonArray)?.Select(p => (string?)p).ToList() ?? [];
        var backups = Directory.Exists(Path.Combine(data, "backups")) ? Directory.GetFiles(Path.Combine(data, "backups"), "manager.*.json").Length : 0;
        // A file from before copies_off keeps none: only the pick reads as on there.
        var before = Path.Combine(dir, "data-before");
        Directory.CreateDirectory(before);
        File.WriteAllText(Path.Combine(before, "manager.json"), "{\n  \"version\": 1,\n  \"copy_picks\": [\"SmmSameIdX|w:2\"]\n}");
        var beforePatched = LaunchGuard.PatchPicks(before, LaunchGuard.PicksOf(working));
        var beforeJson = JsonNode.Parse(File.ReadAllText(Path.Combine(before, "manager.json"))) as JsonObject;
        Check("... Restore before our data loads (a failed start): the working copy picked again in the file and taken out of copies_off, the rest of it "
            + "kept, a backup first; a file from before copies_off gets none",
            patched && picks.SequenceEqual(["SmmSameIdX|w:1", "SmmSameIdY|w:5"]) && off.SequenceEqual(["SmmSameIdY|w:6"])
            && (string?)json?["mods"]?["SmmTestUi"]?["notes"] == "kept" && backups == 1 && beforePatched && beforeJson?.ContainsKey("copies_off") == false
            && (beforeJson?["copy_picks"] as JsonArray)?.Select(p => (string?)p).SequenceEqual(["SmmSameIdX|w:1"]) == true,
            $"patched {patched}, picks {string.Join(", ", picks)}, off {string.Join(", ", off)}, {backups} backups; before copies_off: {beforeJson?.ToJsonString()}");
        LaunchGuard.Notice = (new LaunchGuard.Setup(DateTime.Now.AddHours(-1), working), changes);
        LaunchGuard.ShowNotice(menu);
        await Wait(menu, 1);
        var texts = string.Join(" | ", PopupTexts());
        NModalContainer.Instance?.Clear();
        await Wait(menu, 0.3);
        Check("... the notice names the switch, by Workshop item: \"(switched copy: 1 → 2)\"", texts.Contains("(switched copy: 1 → 2)"), texts);
    }

    // A message the game puts on a copy we set aside (made up here: a Steam branch error on the fixture's copy not
    // picked) goes from its errors, so the MODDED label stays clear, to its info panel, marked as the game's; the late
    // pass before the label (BranchErrorsLatePatch) covers it.
    static void TestSameIdErrors()
    {
        if (ModManager.Mods.FirstOrDefault(m => m.manifest?.id == TwinId && SameIdLoad.IsSetAside(m)) is not { } aside)
        {
            TwinsMissing("same ID, a message on a copy set aside (no fixture twin set aside)");
            return;
        }
        var error = new LocString("main_menu_ui", "MOD_ERROR.STEAM_BRANCH_UNSUPPORTED");
        error.Add("id", TwinId);
        error.Add("supportedBranch", "public");
        error.Add("currentBranch", "public-beta");
        aside.errors = [error];
        try
        {
            var dropped = BranchErrors.DropFromOffMods(late: true);
            var notes = ModDetails.GameNotes(aside);
            Check("same ID, a message on a copy set aside: off its errors (the MODDED label stays clear), into its panel as the game's; the late pass covers it",
                aside.errors == null && dropped >= 1 && BranchErrors.Dropped(aside).Contains(error)
                && notes.Any(n => n.Contains("only supports Steam branch public"))
                && ModDetails.Card(aside).Contains(GameText.Caption("Error while loading - Can be ignored if mod is disabled")),
                $"errors {aside.errors?.Count}, dropped {dropped}; {string.Join(" | ", notes)}");
        }
        finally
        {
            aside.errors = null;
            BranchErrors.TestForget(aside);
        }
    }

    // Load Order with ours pinned first: our row's grip a lock that says why, its moves greyed, a drag of it moving
    // nothing; another mod's Move to top lands under ours; a move of ours, or of a group with it, leaves ours first;
    // OursFirst on a list from before (an old undo step's) puts ours on top, the rest in its order. Put back after.
    static async Task TestSameIdLoadOrder(Node menu, NModdingScreen screen, Control content)
    {
        List<NModMenuRow> Rows() => content.GetChildren().OfType<NModMenuRow>().ToList();
        List<string> Ids() => Rows().Select(r => r.Mod?.manifest?.id ?? "").ToList();
        var pinned = Ids().TakeWhile(id => id == ModEntry.ModId).Count();
        if (!Store.Data.LoadOrderView || pinned == 0 || Rows().Count < pinned + 3 || Rows()[0].Mod is not { } self)
        {
            Check("same ID, Load Order: ours first", false, string.Join(", ", Ids()));
            return;
        }
        var start = Ids();
        var startMods = Rows().Select(r => r.Mod).OfType<Mod>().ToList(); // to put back by copy: two copies of a mod share an id
        var ours = Rows()[0];
        var handle = ours.GetNode<Control>("SpireModManagerHandle");
        var actions = LoadOrder.Actions(content, ours, () => { });
        Check("same ID, Load Order: our row pinned first, a lock for its grip saying why, its moves greyed",
            !handle.GetChildren().OfType<ColorRect>().Any() && handle.GetChildren().OfType<Panel>().Count() == 2
            && Hover.TipOf(handle) == "Spire Mod Manager always loads first, so it can configure the rest of the list properly."
            && actions.Where(a => a.Label is not ("Disable" or "Enable")).All(a => a.Disabled),
            $"{string.Join(", ", actions.Select(a => $"{a.Label}{(a.Disabled ? " (greyed)" : "")}"))}; tip {Hover.TipOf(handle)}");
        var from = handle.GetGlobalRect().GetCenter();
        var to = new Vector2(from.X, Rows()[pinned + 2].GetGlobalRect().End.Y - 4);
        ours.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, GlobalPosition = from, Position = from });
        ours.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseMotion { GlobalPosition = to, Position = to });
        ours.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, GlobalPosition = to, Position = to });
        await Wait(menu, 0.3);
        var undragged = Ids().SequenceEqual(start);
        var row = Rows()[pinned + 2];
        var moved = row.Mod?.manifest?.id;
        LoadOrder.Actions(content, row, () => CategoryList.Redraw(content)).FirstOrDefault(a => a.Label == "Move to top")?.OnSelect?.Invoke();
        await Wait(menu, 0.3);
        Check("... a drag of our row moves nothing; another mod's Move to top lands right under ours (that very row, a copy of a mod installed twice too)",
            undragged && Rows()[pinned] == row && Ids()[pinned] == moved && Ids()[0] == ModEntry.ModId, string.Join(", ", Ids()));
        LoadOrder.Move(self, 3, Rows());
        var afterMove = Ids()[0];
        if (Rows()[pinned + 1].Mod is { } next)
            LoadOrder.MoveGroup([self, next], Rows().Count, Rows());
        CategoryList.Redraw(content);
        await Wait(menu, 0.3);
        SettingsSaveMod E(string id) => new() { Id = id, Source = ModSource.ModsDirectory };
        List<SettingsSaveMod> old = [E("SmmSameIdA"), E(ModEntry.ModId), E("SmmSameIdB")];
        var reordered = LoadOrder.OursFirst(old);
        Check("... a move of ours, or of a group with it, leaves ours first; a list from before (an old undo step's) gets ours on top, the rest in its order",
            afterMove == ModEntry.ModId && Ids()[0] == ModEntry.ModId && reordered && old.Select(e => e.Id).SequenceEqual([ModEntry.ModId, "SmmSameIdA", "SmmSameIdB"]),
            string.Join(", ", Ids()));
        // An undo step from before ours was kept first (an older version of this mod saved the list so): undone back to, the
        // order is that step's, ours first all the same (History.ApplyOrder). Then undone to where it started.
        if (SaveManager.Instance.SettingsSave.ModSettings is { } settings && Rows().Skip(pinned).FirstOrDefault()?.Mod is { } mover)
        {
            var list = settings.ModList;
            var oursEntries = list.Where(e => e.Id == ModEntry.ModId).ToList();
            list.RemoveAll(e => e.Id == ModEntry.ModId);
            list.InsertRange(Math.Min(2, list.Count), oursEntries);
            SaveManager.Instance.SaveSettings();
            History.Record(); // as that older version left it
            var oldOrder = list.Select(e => e.Id).Where(id => id != ModEntry.ModId).ToList();
            LoadOrder.Move(mover, Rows().Count - 1, Rows());
            History.Undo();
            var undone = list.Select(e => e.Id).ToList();
            History.Undo();
            CategoryList.Redraw(content);
            await Wait(menu, 0.3);
            Check("... an undo step from before ours was kept first: its order back, ours first all the same",
                undone.FirstOrDefault() == ModEntry.ModId && undone.Where(id => id != ModEntry.ModId).SequenceEqual(oldOrder), string.Join(", ", undone));
        }
        for (var i = 0; i < startMods.Count; i++)
            LoadOrder.Move(startMods[i], i, Rows());
        CategoryList.Redraw(content);
        await Wait(menu, 0.3);
        Check("... put back: the order as before, nothing pending", Ids().SequenceEqual(start) && !ModEntry.HasPendingChanges(), string.Join(", ", Ids()));
    }

    // The fixture twins on our main list, a box per copy. At launch (the update's reading of our file: only the pick
    // ticked, as before): the picked copy (B) on, Enabled; the other off (copies_off), Disabled, no duplicate problem, its
    // box's tip saying the other is on too and which would load, its panel naming the enabled copy; Enable all over it
    // says it enables a second copy; the view switch's tip says how the game's page treats them. Ticking the other copy
    // (A) picks it, the first staying ticked: the game's list still on, one undo step; A reads Pending relaunch, loading
    // in B's place, and B Won't load (the copy enabled last loads); A's title marked as changed, B's white. Undo, redo,
    // Revert all and its undo; in Load Order, Enable says what the box says, and an order undo leaves the pick. Vanilla
    // View: one box per switch (both ticked), its tick switching every copy and keeping the pick and copies_off; back on
    // our page, the picked copy's box alone. A profile holds each copy's box: saved with both ticked, switched to one from
    // before copies had a box each (only its copy on) and back; one naming no copy keeps the pick (only it on) and holds
    // the boxes from then on; Discard puts the profile's back. A shared list naming the id once (no link) sets both
    // copies; one with Workshop links sets the copies it links; the not-installed copies it names are listed by item.
    // Restore picks the copy that loaded then, and ticks it. The relaunch check sees the picked copy's problem and the
    // other copy losing to it. A tick on one of both selected copies turns both on, the clicked one picked; Enable
    // selected over both, the first in order. A session that can't save our data: the tick is the game's (the pick stays).
    // The self-check finding the pick not taken: every copy says it. The picked copy failing: how to load the other one. A
    // new copy arriving is ticked as the game has it, the pick staying (its popup says which loads); with the switch off it
    // arrives off, and a tick on another copy leaves it off. An update of a copy isn't a new copy; one a saved pick still
    // names (back after an uninstall) doesn't take over, the active profile holding the copy there, so its arriving on is
    // the profile's edit; two copies of a mod new to the game are new mods.
    static async Task TestSameIdCopies(Node menu, NModdingScreen screen, Control content, string outDir)
    {
        List<NModMenuRow> Rows() => content.GetChildren().OfType<NModMenuRow>().ToList();
        NModMenuRow? RowOf(string copy) => Rows().FirstOrDefault(r => r.Mod is { } m && m.manifest?.id == TwinId && Copies.CopyOf(m) == copy);
        if (Layout.Current != Layout.View.Categories || Profiles.Active == null)
        {
            Check("same ID on our page: on the main list, a profile active", false, $"{Layout.Current}, {Profiles.Active?.Name}");
            return;
        }
        if (RowOf("l:SmmTestTwinA") is not { Mod: { } a } rowA || RowOf("l:SmmTestTwinB") is not { Mod: { } b } rowB)
        {
            TwinsMissing("same ID on our page");
            return;
        }
        if (Profiles.Edited)
            Profiles.Save(); // start clean
        static bool Ticked(NModMenuRow row) => row.GetNode<NTickbox>("Tickbox").IsTicked;
        async Task Tick(NModMenuRow row, bool on)
        {
            var box = row.GetNode<NTickbox>("Tickbox");
            box.IsTicked = on;
            box.EmitSignal(NTickbox.SignalName.Toggled, box);
            await Wait(menu, 0.3);
        }
        static string Named(Mod mod) => (mod.manifest?.name ?? "") + Copies.Which(mod);
        string? Picked() => Copies.Picked(TwinId, ModSource.ModsDirectory);
        // Read again each time: an undo puts new entries in the game's list.
        List<SettingsSaveMod> Entries() => SaveManager.Instance.SettingsSave.ModSettings?.ModList.Where(e => e.Id == TwinId).ToList() ?? [];
        bool SwitchOn() => Entries() is { Count: > 0 } entries && entries.All(e => e.IsEnabled);
        string Say() => $"pick {Picked()}, boxes A {Ticked(rowA)} B {Ticked(rowB)}, on A {Copies.Ticked(a)} B {Copies.Ticked(b)}, switch {SwitchOn()}, "
            + $"off {string.Join(",", CopiesOff())}; status A {ModDetails.Status(a)}, B {ModDetails.Status(b)}; "
            + $"lines A {ModDetails.CopyLine(a, false)?.Text}, B {ModDetails.CopyLine(b, false)?.Text}; {CopiesBroken() ?? "kept"}";
        // The picks in our file, as our Init reads them at the next launch (not just in memory).
        string[] FilePicks() => [.. (JsonNode.Parse(File.ReadAllText(Store.FilePath))?["copy_picks"] as JsonArray ?? []).Select(p => (string?)p).OfType<string>()];
        bool OnDisk(string copy) => FilePicks().Contains($"{TwinId}|{copy}");
        var keyA = $"{TwinId}|l:SmmTestTwinA";
        var offAtStart = CopiesOff();
        bool OffAsAtStart() => CopiesOff().SequenceEqual(offAtStart) && FileCopiesOff().SequenceEqual(offAtStart);

        var retired = Rows().FirstOrDefault(r => r.Mod?.manifest?.id == "SmmTestRetired");
        var enableAll = CategoryList.OnOffItems(content, [rowA, .. retired == null ? Array.Empty<NModMenuRow>() : [retired]])[0];
        // Both copies in an Enable all: one is off, so there's something to do.
        var bothDone = CategoryList.OnOffItems(content, [rowA, rowB])[0].Disabled;
        var toggleTip = Hover.TipOf(screen.GetNode<Control>("SpireModManagerViewToggle"));
        var boxTip = Hover.TipOf(rowA.GetNode<Control>("Tickbox"));
        Check("same ID on our page: the picked copy on (Enabled), the other off (Disabled, no duplicate problem, in copies_off, in our file too), its box's "
            + "tip saying the other is also enabled and which loads, its panel naming the enabled copy; Enable all over it says it enables a second copy, over "
            + "both it isn't greyed; the view switch's tip says how the game's page treats them",
            Picked() == "l:SmmTestTwinB" && Ticked(rowB) && !Ticked(rowA) && ModDetails.Status(b).Contains("Enabled") && ModDetails.Status(a).Contains("Disabled")
            && ModDetails.Problems(a).Count == 0 && boxTip == Draft(CopyOnTip, ("other", Named(b)), ("loads", Named(a)))
            && Hover.TipOf(rowB.GetNode<Control>("Tickbox")) == "" && ModDetails.CopyLine(a, links: false)?.Text == $"{Named(b)} is the enabled copy"
            && enableAll.Tip == Draft(BulkEnablesTip, ("mods", a.manifest?.name ?? ""))
            && toggleTip.Contains("\nIn vanilla, mods ") && toggleTip.Contains("These will use the Spire Mod Manager configuration as a fallback.")
            && !bothDone && OnDisk("l:SmmTestTwinB") && offAtStart.Contains(keyA) && FileCopiesOff().Contains(keyA) && CopiesBroken() == null,
            $"{Say()}; box tip {boxTip}; Enable all tip {enableAll.Tip}; both done {bothDone}; on disk {string.Join(", ", FilePicks())} "
            + $"(off {string.Join(", ", FileCopiesOff())}); toggle tip {toggleTip}");

        var (steps, picksWere) = (History.Steps(History.Track.List), Store.Data.CopyPicks);
        await Tick(rowA, true);
        var (unsavedOn, unsavedOff, _) = Profiles.UnsavedChanges();
        Check("ticking the other copy picks it (copy_picks, and out of copies_off, in our file too), the first staying ticked, the game's list still on, one "
            + "undo step; it reads Pending relaunch, loading in place of the first, which reads Won't load (one copy of a source loads); only its title marked "
            + "as changed, the first's white; the profile's unsaved lines and the pending line follow",
            Picked() == "l:SmmTestTwinA" && Store.Data.CopyPicks?.Contains(keyA) == true && OnDisk("l:SmmTestTwinA") && !CopiesOff().Contains(keyA)
            && !FileCopiesOff().Contains(keyA) && Ticked(rowA) && Ticked(rowB) && SwitchOn() && History.Steps(History.Track.List) == steps + 1
            && ModDetails.Status(a).Contains("Pending relaunch") && ModDetails.Status(b).Contains(StatusWontLoad)
            && ModDetails.CopyLine(a, links: false)?.Text == $"Loads on relaunch, in place of {Named(b)}"
            && ModDetails.CopyLine(b, links: false)?.Text == Draft(CopyLoses, ("other", Named(a)), ("why", WhySource))
            && rowA.GetNode<Control>("Title").Modulate == RowTitles.ChangedTitle && rowB.GetNode<Control>("Title").Modulate == Colors.White
            && unsavedOn.Any(m => m.Name == Named(a)) && !unsavedOff.Any(m => m.Name == Named(b)) && ModEntry.HasPendingChanges()
            && screen.GetNode<Control>("%PendingChangesLabel").Visible && CopiesBroken() == null,
            $"{Say()}; unsaved +{string.Join(",", unsavedOn.Select(m => m.Name))} -{string.Join(",", unsavedOff.Select(m => m.Name))}");
        await Capture(screen, outDir, "19-same-id-switched");
        History.Undo();
        await Wait(menu, 0.3);
        var undone = Picked() == "l:SmmTestTwinB" && Ticked(rowB) && !Ticked(rowA) && SwitchOn() && (Store.Data.CopyPicks ?? []).SequenceEqual(picksWere ?? [])
            && !Profiles.Edited && OnDisk("l:SmmTestTwinB") && OffAsAtStart();
        History.Redo();
        await Wait(menu, 0.3);
        var redone = Picked() == "l:SmmTestTwinA" && Ticked(rowA) && Ticked(rowB) && OnDisk("l:SmmTestTwinA") && !CopiesOff().Contains(keyA);
        History.RevertMods();
        await Wait(menu, 0.3);
        var reverted = Picked() == "l:SmmTestTwinB" && Ticked(rowB) && !Ticked(rowA) && History.AtEntered && OnDisk("l:SmmTestTwinB") && OffAsAtStart();
        History.Undo(); // the revert
        await Wait(menu, 0.3);
        var revertUndone = Picked() == "l:SmmTestTwinA" && Ticked(rowA) && Ticked(rowB);
        History.Undo(); // the tick
        await Wait(menu, 0.3);
        Check("... undo puts both back (the pick, copies_off and the game's list, in our file too), redo again; Revert all goes back to the pick the page "
            + "opened with, and undoes", undone && redone && reverted && revertUndone && Picked() == "l:SmmTestTwinB" && OnDisk("l:SmmTestTwinB") && OffAsAtStart(),
            $"undo {undone}, redo {redone}, revert {reverted}, its undo {revertUndone}; on disk {string.Join(", ", FilePicks())}; {Say()}");

        // Load Order: the pick through its Enable item, then a move; the order's undo leaves the pick.
        Click(screen.GetNode<NButton>("SpireModManagerLoadOrderToggle"));
        await Wait(menu, 0.5);
        if (RowOf("l:SmmTestTwinA") is { } orderRow)
        {
            var enable = LoadOrder.Actions(content, orderRow, () => CategoryList.Redraw(content))[0];
            var enableTip = enable.Tip;
            enable.OnSelect?.Invoke();
            await Wait(menu, 0.3);
            var pickedHere = Picked() == "l:SmmTestTwinA" && Copies.Ticked(a) && Copies.Ticked(b);
            var offHere = CopiesOff();
            if (Rows().Skip(1).FirstOrDefault(r => r.Mod?.manifest?.id is { } id && id != TwinId && id != ModEntry.ModId)?.Mod is { } mover)
                LoadOrder.Move(mover, Rows().Count - 1, Rows());
            CategoryList.Redraw(content);
            await Wait(menu, 0.3);
            History.Undo(); // the move: this view's undo steps through the order only
            await Wait(menu, 0.3);
            Check("... in Load Order: Enable on the other copy says the first is also enabled and which loads, and picks it beside the first; an order undo "
                + "leaves the pick and copies_off",
                enableTip == Draft(CopyOnTip, ("other", Named(b)), ("loads", Named(a))) && pickedHere && Picked() == "l:SmmTestTwinA" && CopiesOff().SequenceEqual(offHere)
                && !LoadOrder.OrderChanged, $"tip {enableTip}; picked {pickedHere}; {Say()}");
        }
        Click(screen.GetNode<NButton>("SpireModManagerLoadOrderToggle"));
        await Wait(menu, 0.5);
        History.Undo(); // the pick made in Load Order
        await Wait(menu, 0.3);

        // Vanilla View: the game's own page, one switch for both copies (with Better Mod Menu, its page: not ours to tick).
        // Its tick leaves copies_off as it is, so back on our page the same copies are ticked.
        if (!Bmm.Present)
        {
            var vanilla = await WithVanillaView(screen, menu, () => Ticked(rowA) && Ticked(rowB));
            Click(screen.GetNode<BaseButton>("SpireModManagerViewToggle"));
            await Wait(menu, 0.5);
            await Tick(rowA, false); // the game's own tickbox: every entry of the id and source
            var allOff = Entries().All(e => !e.IsEnabled) && !Ticked(rowB) && Picked() == "l:SmmTestTwinB" && OffAsAtStart();
            await Tick(rowA, true);
            var allOn = SwitchOn() && Ticked(rowB) && Picked() == "l:SmmTestTwinB" && OffAsAtStart();
            Click(screen.GetNode<BaseButton>("SpireModManagerViewToggle"));
            await Wait(menu, 0.5);
            Check("... Vanilla View: both boxes ticked (the game's one switch); its tick switches every copy and keeps the pick and copies_off; back on our "
                + "page, the picked copy's box alone",
                vanilla && allOff && allOn && Ticked(rowB) && !Ticked(rowA) && Picked() == "l:SmmTestTwinB" && CopiesBroken() == null,
                $"vanilla {vanilla}, off {allOff}, on {allOn}; {Say()}");
            History.Undo(); // the two ticks there
            History.Undo();
            await Wait(menu, 0.3);
        }
        else
            Results.Add("SKIP same ID, Vanilla View's tick: Better Mod Menu's page there");

        // A profile holds each copy's box. The profiles read now: an undo puts new ones in our data.
        if (Profiles.Active is { } home && Store.Data.Profiles.FirstOrDefault(p => p != home) is { } other)
        {
            var counted = Profiles.OnCount();
            await Tick(rowA, true);
            var countedOnce = Profiles.OnCount() == counted; // two copies of one mod on: one mod
            Profiles.Save();
            var savedEntry = home.Mods.FirstOrDefault(m => m.Id == TwinId);
            var saved = savedEntry is { Copy: "l:SmmTestTwinA", CopiesOff: [] };
            // Its saved state, not an unsaved edit an earlier test left (a switch writes the edit).
            var (otherWas, otherEdit) = (other.Mods.ToList(), other.Edited);
            other.Edited = null;
            other.Mods.RemoveAll(m => m.Id == TwinId);
            other.Mods.Add(new ProfileMod(TwinId, ModSource.ModsDirectory, true, "l:SmmTestTwinB")); // from before copies had a box each: its copy alone
            Profiles.SwitchTo(other, screen);
            await Wait(menu, 0.3);
            var there = Picked() == "l:SmmTestTwinB" && Ticked(rowB) && !Ticked(rowA) && CopiesOff().Contains(keyA);
            Profiles.SwitchTo(home, screen);
            await Wait(menu, 0.3);
            var back = Picked() == "l:SmmTestTwinA" && Ticked(rowA) && Ticked(rowB) && !CopiesOff().Contains(keyA);
            other.Mods.RemoveAll(m => m.Id == TwinId);
            other.Mods.Add(new ProfileMod(TwinId, ModSource.ModsDirectory, true)); // no copy: as from before copies, or Better Mod Menu
            Profiles.SwitchTo(other, screen);
            await Wait(menu, 0.3);
            // The pick kept, and only it on (as before copies had a box each); from then on the profile holds the boxes: a
            // tick on the other copy is its unsaved edit; unticked again, none.
            var kept = Picked() == "l:SmmTestTwinA" && Ticked(rowA) && !Ticked(rowB);
            var held = other.Mods.FirstOrDefault(m => m.Id == TwinId) is { Copy: "l:SmmTestTwinA", CopiesOff: ["l:SmmTestTwinB"] };
            await Tick(rowB, true);
            var heldEdit = Profiles.Edited;
            await Tick(rowB, false);
            held &= heldEdit && !Profiles.Edited && Picked() == "l:SmmTestTwinA";
            Profiles.SwitchTo(home, screen);
            await Wait(menu, 0.3);
            await Tick(rowA, false);
            var (editOn, editOff, editSwitched) = Profiles.UnsavedChanges();
            var edited = Profiles.Edited && editOn.Count == 0 && editOff.Select(m => m.Name).SequenceEqual([Named(a)]) && editSwitched.Count == 0;
            Profiles.Discard(screen);
            await Wait(menu, 0.3);
            Check("... a profile holds each copy's box: saved with both ticked (one mod in the count), switched to one from before copies had a box each (its "
                + "copy alone) and back; one naming no copy keeps the pick, only it on, and holds the boxes from then on (a tick on the other copy is its "
                + "edit); an untick is the one unsaved line; Discard puts the profile's back",
                countedOnce && saved && there && back && kept && held && edited && Picked() == "l:SmmTestTwinA" && Ticked(rowA) && Ticked(rowB) && !Profiles.Edited,
                $"saved {savedEntry} ({string.Join(",", savedEntry?.CopiesOff ?? [])}); counted once {countedOnce}, there {there}, back {back}, kept {kept}, "
                + $"held {held}, edited {edited} (+{string.Join(",", editOn.Select(m => m.Name))} -{string.Join(",", editOff.Select(m => m.Name))} "
                + $"~{editSwitched.Count}); {Say()}");
            other.Mods.Clear();
            other.Mods.AddRange(otherWas);
            other.Edited = otherEdit;
            await Tick(rowA, false); // the fixture's pick alone again (the copy that loaded)
            Profiles.Save(); // the profile back on it
        }
        else
            Results.Add("SKIP same ID, profiles: one profile only");

        // Shared lists: a row for the id with no link names both copies (two copies installed by hand look alike in a list),
        // so it sets their switch alone: off, the entry keeping its pick and copies off; on, each copy as its box is, the
        // pick kept. Rows for copies not installed are listed by item when two share an id.
        var once = Profiles.PlanImport([new(TwinId, false, "Test Twin", "")]);
        var onceOn = Profiles.PlanImport([new(TwinId, true, "Test Twin", "")]);
        var twoGone = Profiles.PlanImport([new("SmmSameIdGone", true, "Gone", ProfileCsv.WorkshopLink(41)), new("SmmSameIdGone", false, "Gone", ProfileCsv.WorkshopLink(42))]);
        var preview = ProfileShare.PreviewFor([new("SmmSameIdGone", true, "Gone", ProfileCsv.WorkshopLink(41)), new("SmmSameIdGone", false, "Gone", ProfileCsv.WorkshopLink(42))], "Two gone");
        static string Planned(Profiles.ImportPlan plan, string id) => string.Join(", ", plan.Mods.Where(m => m.Id == id)
            .Select(m => $"{m.Source} {m.Enabled} {m.Copy ?? "no copy"} off [{string.Join(",", m.CopiesOff ?? [])}]"))
            + $" (on {plan.On}, off {plan.Off}, same {plan.Same}, switched {plan.Switched.Count}, missing {plan.Missing.Count})";
        Check("... a shared list naming the id once (no link) sets both copies' switch: off keeps the pick and copies off; on, each copy as its box is, the "
            + "pick kept; two copies not installed are both listed, by item",
            once.Mods.SingleOrDefault(m => m.Id == TwinId) is { Enabled: false, Copy: "l:SmmTestTwinB", CopiesOff: ["l:SmmTestTwinA"] } && once is { Off: 1, Same: 1 }
            && onceOn.Mods.SingleOrDefault(m => m.Id == TwinId) is { Enabled: true, Copy: "l:SmmTestTwinB", CopiesOff: ["l:SmmTestTwinA"] } && onceOn is { On: 0, Off: 0, Same: 2 }
            && twoGone.Missing.Count == 2 && preview.Contains("Gone (41)") && preview.Contains("Gone (42)")
            && twoGone.Mods.SingleOrDefault(m => m.Id == "SmmSameIdGone") is { Enabled: true, Copy: "w:41" },
            $"once {Planned(once, TwinId)}; on {Planned(onceOn, TwinId)}; gone {Planned(twoGone, "SmmSameIdGone")}; {preview}");
        // The profile shared (the off copy's row too, or the enabled one's alone, as by default) and read back on this mod
        // list: nothing changes (rows with no link can't say which copy each is, so they don't turn the off one on). Local
        // copies have no link.
        var shared = (Profiles.Active is { } sharing ? Profiles.ShareRows(sharing) : []).Where(r => r.Id == TwinId).ToList();
        List<ProfileCsv.Entry> Read(IEnumerable<ProfileCsv.Row> rows) => [.. rows.Select(r => new ProfileCsv.Entry(r.Id, r.Enabled, r.Name, ""))];
        var (readAll, readOn) = (Profiles.PlanImport(Read(shared)), Profiles.PlanImport(Read(shared.Where(r => r.Enabled))));
        var sharePreview = ProfileShare.PreviewFor(Read(shared), "Shared");
        Check("... the profile shared with one twin off (its row too, or the enabled one's alone) and read back here: nothing changes (no copy turned on, "
            + "the pick and copies off kept; the preview says None)",
            shared.Count == 2 && shared.Count(r => r.Enabled) == 1
            && new[] { readAll, readOn }.All(p => p.Mods.SingleOrDefault(m => m.Id == TwinId) is { Enabled: true, Copy: "l:SmmTestTwinB", CopiesOff: ["l:SmmTestTwinA"] }
                && p is { On: 0, Off: 0, Same: 2, Switched.Count: 0 })
            && sharePreview.Contains("Changes compared to the current profile:\n  •  None"),
            $"rows {string.Join(" | ", shared.Select(r => $"{r.Id} {r.Version} {r.Enabled}"))}; all {Planned(readAll, TwinId)}; on {Planned(readOn, TwinId)}; {sharePreview}");

        // Two Workshop items of one mod (made up, in the game's list for a moment, not announced): each says its item; a
        // shared list naming both sets each copy by its link, the one on picked; a row on naming an item not installed is
        // the pick for when it is; both rows on, both on, the first row's picked. The fixture's two local copies say their
        // folders. Both on with the other picked first (a friend's list loading the other copy) changes no box, but which
        // copy loads: the preview says so in Changes' words, not None. And a Workshop item of another mod installed once
        // (111), listed by another item of it (222, a re-upload): the row sets the one installed, nothing missing.
        var (w124, w125) = (Made("SmmSameIdW", "SmmSameIdW124", ModLoadState.None, 124), Made("SmmSameIdW", "SmmSameIdW125", ModLoadState.None, 125));
        var w111 = Made("SmmSameIdOnce", "SmmSameIdOnce111", ModLoadState.None, 111);
        GameMods().AddRange([w124, w125, w111]);
        try
        {
            var byLink = Profiles.PlanImport([new("SmmSameIdW", false, "W", ProfileCsv.WorkshopLink(124)), new("SmmSameIdW", true, "W", ProfileCsv.WorkshopLink(125))]);
            var notYet = Profiles.PlanImport([new("SmmSameIdW", false, "W", ProfileCsv.WorkshopLink(124)), new("SmmSameIdW", true, "W", ProfileCsv.WorkshopLink(126))]);
            var bothLinked = Profiles.PlanImport([new("SmmSameIdW", true, "W", ProfileCsv.WorkshopLink(124)), new("SmmSameIdW", true, "W", ProfileCsv.WorkshopLink(125))]);
            Check("... two Workshop copies say their items, \" (124)\"; a list naming both sets each by its link, the one on picked, or one not installed yet; "
                + "both on: both, the first row's picked; two local copies say their folders, \" (SmmTestTwinA)\"",
                Copies.Which(w124) == " (124)" && Copies.Which(w125) == " (125)" && Copies.Which(a) == " (SmmTestTwinA)"
                && byLink.Mods.SingleOrDefault(m => m.Id == "SmmSameIdW") is { Source: ModSource.SteamWorkshop, Enabled: true, Copy: "w:125", CopiesOff: ["w:124"] }
                && notYet.Mods.SingleOrDefault(m => m.Id == "SmmSameIdW") is { Enabled: true, Copy: "w:126" }
                && bothLinked.Mods.SingleOrDefault(m => m.Id == "SmmSameIdW") is { Enabled: true, Copy: "w:124", CopiesOff: [] },
                $"{Copies.Which(w124)}, {Copies.Which(w125)}, {Copies.Which(a)}; {Planned(byLink, "SmmSameIdW")}; {Planned(notYet, "SmmSameIdW")}; "
                + Planned(bothLinked, "SmmSameIdW"));
            List<ProfileCsv.Entry> otherFirst = [new("SmmSameIdW", true, "W", ProfileCsv.WorkshopLink(125)), new("SmmSameIdW", true, "W", ProfileCsv.WorkshopLink(124))];
            var (switchedTo, switchPreview) = (Profiles.PlanImport(otherFirst), ProfileShare.PreviewFor(otherFirst, "Other first"));
            var switchLine = Draft(CopySwitchLine, ("mod", "SmmSameIdW"), ("to", Copies.Named(w125)), ("from", Copies.Named(w124)));
            Check("... both Workshop copies on, the other one first in the list: no box changes, but the copy that loads does, and the preview says so in "
                + "Changes' words, not None", Copies.Picked("SmmSameIdW", ModSource.SteamWorkshop) == "w:124"
                && switchedTo is { On: 0, Off: 0, Same: 2, Switched: [var (from, to)] } && from == w124 && to == w125
                && switchedTo.Mods.SingleOrDefault(m => m.Id == "SmmSameIdW") is { Enabled: true, Copy: "w:125", CopiesOff: [] }
                && switchPreview.Contains($"Changes compared to the current profile:\n  •  {switchLine}") && !switchPreview.Contains("  •  None"),
                $"{Planned(switchedTo, "SmmSameIdW")}; {switchPreview}");
            List<ProfileCsv.Entry> reupload = [new("SmmSameIdOnce", false, "Once", ProfileCsv.WorkshopLink(222))];
            var (once222, once222Preview) = (Profiles.PlanImport(reupload), ProfileShare.PreviewFor(reupload, "Re-upload"));
            Check("... a mod installed once from the Workshop (item 111), listed by another item of it (222): the row sets the one installed (off), nothing "
                + "missing, nothing to subscribe to", once222.Mods.SingleOrDefault(m => m.Id == "SmmSameIdOnce") is { Source: ModSource.SteamWorkshop, Enabled: false }
                && once222 is { Off: 1, Missing.Count: 0 } && !once222Preview.Contains("Not installed"), $"{Planned(once222, "SmmSameIdOnce")}; {once222Preview}");
        }
        finally
        {
            GameMods().Remove(w124);
            GameMods().Remove(w125);
            GameMods().Remove(w111);
        }

        // A local copy beside two Workshop items of one mod (made up, as found at launch; the local copy newer, so the game
        // keeps it on either branch), all ticked, as the active profile has them: the other Workshop item picked changes
        // nothing that loads, so it's no unsaved change, as Changes has none. Then one Workshop item newer than the local
        // copy, the other picked: on the beta the newer loads (the game's rule), and the picked one says so, not that it's
        // its source's one copy that loads; on stable the local copy still loads.
        const string Mix = "SmmSameIdMix";
        var mixLocal = Made(Mix, "MixLocal", ModLoadState.Loaded, version: "2.0.0");
        var (mix61, mix62) = (Made(Mix, "Mix61", ModLoadState.DisabledDuplicate, 61), Made(Mix, "Mix62", ModLoadState.DisabledDuplicate, 62));
        GameMods().AddRange([mixLocal, mix61, mix62]);
        Copies.TestRecount(); // as many as the Workshop items above
        var (mixPicks, mixOff) = (Store.Data.CopyPicks, Store.Data.CopiesOff);
        try
        {
            History.Quietly(data =>
            {
                if (data.Profiles.FirstOrDefault(p => p.Name == data.ActiveProfile) is { } active)
                    foreach (var list in new[] { active.Mods, active.Edited })
                        list?.AddRange([new ProfileMod(Mix, ModSource.ModsDirectory, true), new ProfileMod(Mix, ModSource.SteamWorkshop, true, "w:61", CopiesOff: [])]);
            });
            Profiles.Track();
            var mixClean = !Profiles.Edited && Copies.Picked(Mix, ModSource.SteamWorkshop) == "w:61" && Copies.Loading(Mix) == mixLocal;
            Dependencies.Write([(mix62, true)], pick: [mix62]); // as a tick on it after unticking it, or a profile naming it
            Profiles.Track();
            var mixSwitched = Profiles.UnsavedChanges().Switched.Where(s => s.To.manifest?.id == Mix).ToList();
            var pickOnly = Copies.Picked(Mix, ModSource.SteamWorkshop) == "w:62" && Copies.Loading(Mix) == mixLocal && !Profiles.Edited && mixSwitched.Count == 0
                && !HistoryBar.Describe().Any(l => l.Contains(Mix));
            var pickSay = $"clean before {mixClean}; pick {Copies.Picked(Mix, ModSource.SteamWorkshop)}, loads {Copies.KeyOf(Copies.Loading(Mix))}, edited {Profiles.Edited}, "
                + $"switched {string.Join(", ", mixSwitched.Select(s => $"{Copies.CopyOf(s.From)}->{Copies.CopyOf(s.To)}"))}; "
                + string.Join(" | ", HistoryBar.Describe().Where(l => l.Contains(Mix)));
            Check("... a local copy (newer) beside two Workshop items, all ticked and saved in the profile: the other Workshop item picked changes nothing that "
                + "loads, so it's no unsaved change (and no line in Changes)", mixClean && pickOnly, pickSay);
            if (mix61.manifest is { } newer)
                newer.version = "3.0.0";
            var w = Compat.WorkshopBeatsOlderLocal;
            var mixLoads = w ? mix61 : mixLocal;
            var lost = Copies.LosesTo(mix62);
            // As an update while the game runs makes it: Changes says the newer loads instead of the local copy that loaded.
            var updated = HistoryBar.Describe().Contains(Draft(CopySwitchLine, ("mod", Mix), ("to", Copies.Named(mix61)), ("from", Copies.Named(mixLocal))));
            Check("... one Workshop item newer than the local copy, the other picked: " + (w ? "on the beta the newer loads (the game's rule), the picked "
                + "one says it loads instead as the higher version, and Changes lists the switch" : "on stable the local copy still loads, and the picked one says why"),
                lost is { } why && why.Loads == mixLoads && why.Why == (w ? Copies.LoseReason.HigherVersion : Copies.LoseReason.LocalCopy)
                && ModDetails.CopyLine(mix62, false)?.Text == Draft(CopyLoses, ("other", Copies.Named(mixLoads)), ("why", w ? WhyHigher : WhyLocal)) && updated == w,
                $"beta rule {w}; loses to {Copies.KeyOf(lost?.Loads)} ({lost?.Why}); line {ModDetails.CopyLine(mix62, false)?.Text}; "
                + string.Join(" | ", HistoryBar.Describe().Where(l => l.Contains(Mix))));
        }
        finally
        {
            GameMods().Remove(mixLocal);
            GameMods().Remove(mix61);
            GameMods().Remove(mix62);
            Copies.TestRecount();
            (Store.Data.CopyPicks, Store.Data.CopiesOff) = (mixPicks, mixOff);
            Store.SaveViewState();
            History.Quietly(data =>
            {
                data.CopyPicks = Store.TidyPicks(data.CopyPicks?.Where(k => Copies.IdOf(k) != Mix));
                foreach (var profile in data.Profiles)
                {
                    profile.Mods.RemoveAll(m => m.Id == Mix);
                    profile.Edited?.RemoveAll(m => m.Id == Mix);
                }
            });
            Profiles.Track();
        }

        // Restore of a working setup (the crash dialog's) with our data loaded: its copies picked again, and so ticked (out
        // of copies_off), in our file too.
        if (SaveManager.Instance.SettingsSave.ModSettings is { } restoring)
        {
            var listWas = restoring.ModList.Select(e => new SettingsSaveMod { Id = e.Id, Source = e.Source, IsEnabled = e.IsEnabled }).ToList();
            var (picksBefore, offBefore) = (Store.Data.CopyPicks, Store.Data.CopiesOff);
            var first = true;
            var working = new List<LaunchGuard.Entry>();
            foreach (var m in History.CurrentMods())
            {
                // The twins' first entry the copy that loaded then (A), the second the other; Test Addon off, so no check opens.
                var twin = m.Id == TwinId ? (first ? "l:SmmTestTwinA" : "l:SmmTestTwinB") : null;
                working.Add(new LaunchGuard.Entry(m.Id, m.Source, m.Id == TwinId ? first : m.Enabled && m.Id != "SmmTestAddon", null, twin));
                if (m.Id == TwinId)
                    first = false;
            }
            var relaunched = 0;
            ModEntry.TestRelaunch = _ => relaunched++;
            try
            {
                LaunchGuard.Restore(working, menu);
                await Wait(menu, 0.3);
                Check("... Restore of a working setup with our data loaded: the copy that loaded then picked again, and ticked (out of copies_off), in our file too",
                    Store.Data.CopyPicks?.Contains(keyA) == true && OnDisk("l:SmmTestTwinA") && !CopiesOff().Contains(keyA) && !FileCopiesOff().Contains(keyA),
                    $"picks {string.Join(", ", Store.Data.CopyPicks ?? [])}; on disk {string.Join(", ", FilePicks())}; off {string.Join(", ", FileCopiesOff())}; "
                    + $"relaunched {relaunched}");
            }
            finally
            {
                ModEntry.TestRelaunch = null;
                RelaunchCheck.Close();
                restoring.ModList.Clear();
                restoring.ModList.AddRange(listWas);
                SaveManager.Instance.SaveSettings();
                (Store.Data.CopyPicks, Store.Data.CopiesOff) = (picksBefore, offBefore);
                Store.SaveViewState();
                Profiles.RefreshTickboxes(screen);
                CategoryList.Redraw(content);
                await Wait(menu, 0.3);
            }
        }

        // The relaunch check with both copies ticked, the one picked to load next given a need it can't meet: its problem,
        // and the other copy losing to it (only one copy of an id loads).
        await Tick(rowA, true);
        var needsWere = a.manifest?.dependencies;
        if (a.manifest is { } manifest)
            manifest.dependencies = [new ModDependency(Missing)];
        try
        {
            RelaunchCheck.Show(screen);
            await Wait(menu, 0.3);
            var body = (menu.GetTree().Root.GetNodeOrNull(RelaunchCheck.Name)?.FindChild("Body", true, false) as RichTextLabel)?.GetParsedText() ?? "";
            RelaunchCheck.Cancel();
            await Wait(menu, 0.3);
            Check("... the relaunch check sees the picked copy's problem, by its copy's name, and the other copy losing to it",
                body.Split('\n').Any(l => l.Contains($"{Named(a)}  ") && l.Contains(Missing)) && body.Contains($"{Named(b)}  {Draft(RelaunchLoses, ("other", Named(a)))}"), body);
        }
        finally
        {
            if (a.manifest is { } restored)
                restored.dependencies = needsWere;
        }
        History.Undo(); // the pick
        await Wait(menu, 0.3);

        // Both copies off, then a tick on one of the two rows selected: both on (each box of the rows), the row ticked the
        // copy that loads.
        Dependencies.Set([b], false, screen);
        CategoryList.Redraw(content);
        await Wait(menu, 0.3);
        var beforePicks = History.Steps(History.Track.List);
        Selection.Add(content, [rowA, rowB]);
        await Tick(rowA, true);
        var clickedWins = Picked() == "l:SmmTestTwinA" && Copies.Ticked(a) && Copies.Ticked(b) && SwitchOn();
        Selection.Clear(content, record: false);
        while (History.Steps(History.Track.List) > beforePicks && History.CanUndo)
            History.Undo(); // the group tick, then the picks
        History.Undo(); // the off
        await Wait(menu, 0.3);
        Check("... both copies off, a tick on one of both selected: both on, the clicked one the copy that loads; undone, as before",
            clickedWins && Picked() == "l:SmmTestTwinB" && SwitchOn() && Ticked(rowB) && !Ticked(rowA) && OffAsAtStart(), $"clicked wins {clickedWins}; {Say()}");
        // Both off, Enable selected over both (no row clicked): both on, the first in order (A, found at launch and sorted
        // first) the copy that loads, as both were turned on.
        Dependencies.Set([b], false, screen);
        CategoryList.Redraw(content);
        await Wait(menu, 0.3);
        CategoryList.OnOffItems(content, [rowA, rowB], selected: true)[0].OnSelect?.Invoke();
        await Wait(menu, 0.3);
        var firstWins = Picked() == "l:SmmTestTwinA" && Copies.Ticked(b) && Copies.Ticked(a) && SwitchOn();
        History.Undo(); // Enable selected
        History.Undo(); // the off
        await Wait(menu, 0.3);
        Check("... both off, Enable selected over both: both on, the first in order the copy that loads; undone, as before",
            firstWins && Picked() == "l:SmmTestTwinB" && SwitchOn() && Ticked(rowB) && !Ticked(rowA) && OffAsAtStart(), $"first wins {firstWins}; {Say()}");

        // A session that can't save our data: a tick on a copy sharing its switch is the game's own (every copy's switch),
        // as which of them are ticked is ours.
        Store.TestReadOnly(true);
        try
        {
            await Tick(rowA, true);
            Check("... a session that can't save our data: ticking the other copy is the game's tick, the pick and copies_off stay",
                Picked() == "l:SmmTestTwinB" && !Ticked(rowA) && Ticked(rowB) && SwitchOn() && OffAsAtStart(), Say());
        }
        finally
        {
            Store.TestReadOnly(false);
        }

        // The self-check finding the pick not taken: every copy says it, and a relaunch is no change for them (the other
        // copy picked, pending until then, isn't).
        var pendingBefore = ModEntry.HasPendingChanges();
        await Tick(rowA, true);
        var pendingPicked = ModEntry.HasPendingChanges();
        SameIdLoad.TestResult(TwinId, SameIdLoad.Result.NotTaken);
        var notApplied = (ModDetails.CopyLine(a, false)?.Text, ModDetails.CopyLine(b, false)?.Text);
        var pendingUnchosen = ModEntry.HasPendingChanges();
        SameIdLoad.TestResult(TwinId, null);
        History.Undo(); // the pick
        await Wait(menu, 0.3);
        const string couldnt = "Spire Mod Manager couldn't choose which copy was loaded. If the game is on a different version, Spire Mod Manager may need to be updated.";
        Check("... the pick not taken (the self-check): both copies say it, and the other copy picked is no relaunch away",
            notApplied == (couldnt, couldnt) && SameIdLoad.ResultOf(TwinId) == SameIdLoad.Result.Taken && !pendingBefore && pendingPicked && !pendingUnchosen
            && Picked() == "l:SmmTestTwinB", $"{notApplied}; then {SameIdLoad.ResultOf(TwinId)}; pending {pendingBefore}, picked {pendingPicked}, not taken {pendingUnchosen}");

        // The picked copy failing for its own reasons (a newer game needed), the other copy off: how to load that one.
        var (bState, bErrors) = (b.state, b.errors);
        try
        {
            b.state = ModLoadState.Failed;
            b.errors = [new LocString("main_menu_ui", "MOD_ERROR.GAME_VERSION_UNSUPPORTED")];
            var failedLine = ModDetails.CopyLine(b, false)?.Text;
            Check("... the picked copy failed for its own reasons: its panel says the other copy is installed, and how to load it instead",
                failedLine == Draft(FailedOther, ("other", Named(a))), failedLine ?? "none");
            // The other copy ticked too: still how to load it, not "this copy is the one that loads" (it failed).
            BothTickedQuietly(true);
            string? failedBoth;
            try
            {
                failedBoth = ModDetails.CopyLine(b, false)?.Text;
            }
            finally
            {
                BothTickedQuietly(false);
            }
            Check("... failed for its own reasons with the other copy ticked too: still how to load that one, not that this copy loads",
                failedBoth == Draft(FailedOther, ("other", Named(a))), failedBoth ?? "none");
        }
        finally
        {
            (b.state, b.errors) = (bState, bErrors);
        }

        // A made-up copy of the twins gone again: its row, the game's lists and our data for it (quietly, in every undo
        // snapshot, the profiles' copies off too).
        void Forget(Mod gone)
        {
            var name = Copies.CopyOf(gone);
            if (Rows().FirstOrDefault(r => r.Mod == gone) is { } goneRow)
            {
                content.RemoveChild(goneRow);
                goneRow.QueueFree();
            }
            GameForgets(gone);
            History.Quietly(data =>
            {
                data.Copies?.GetValueOrDefault(TwinId)?.Remove(name);
                data.Mods.Remove($"{TwinId}|{name}");
                data.CopiesOff = data.CopiesOff?.Where(k => k != $"{TwinId}|{name}").ToList();
                foreach (var profile in data.Profiles)
                    foreach (var list in new[] { profile.Mods, profile.Edited })
                        for (var i = 0; i < (list?.Count ?? 0); i++)
                            if (list?[i] is { Id: TwinId, CopiesOff: { } off } entry && off.Contains(name))
                                list[i] = entry with { CopiesOff = [.. off.Where(c => c != name)] };
            });
            Profiles.Track();
        }

        // A new copy of the mod arrives, its switch on: ticked, as the game has it, and the pick stays (it doesn't take over);
        // the popup says it's enabled and which copy loads. It won't load, so nothing is pending; no undo step; the banner
        // shows, and its title is white (a ticked copy).
        var copy = new Mod
        {
            path = "C:/SmmSameIdTest/SmmTestTwinC", modSource = ModSource.ModsDirectory, state = ModLoadState.AddedAtRuntime,
            manifest = new ModManifest { id = TwinId, name = "Test Twin", version = "1.2.0" },
        };
        var arrivalSteps = History.Steps(History.Track.List);
        NewModPopupPatch.TestReset();
        NModalContainer.Instance?.Clear();
        GameInstalls(copy);
        await Wait(menu, 0.4);
        var popup = string.Join(" | ", PopupTexts());
        NModalContainer.Instance?.Clear();
        var noStep = History.Steps(History.Track.List) == arrivalSteps;
        CategoryList.Redraw(content);
        await Wait(menu, 0.3);
        var copyTitle = Rows().FirstOrDefault(r => r.Mod == copy)?.GetNode<Control>("Title").Modulate;
        var banner = screen.GetNode<Control>("ModsBorder").GetNodeOrNull<Control>(LoadOrder.SameIdName) != null;
        var arrived = Picked() == "l:SmmTestTwinB" && Copies.Ticked(copy) && !Copies.Loads(copy) && Installed.Mods.Contains(copy) && !ModEntry.HasPendingChanges()
            && noStep && (banner || !Layout.Wide) && copyTitle == Colors.White && CopiesBroken() == null;
        var arrivedSay = $"pick {Picked()}, ticked {Copies.Ticked(copy)}, loads {Copies.Loads(copy)}, pending {ModEntry.HasPendingChanges()}, no step {noStep}, "
            + $"banner {banner}, title {copyTitle}; {CopiesBroken() ?? "kept"}";
        Forget(copy);
        CategoryList.Redraw(content);
        await Wait(menu, 0.3);
        Check("... a new copy arriving with the switch on is ticked, as the game has it, and the pick stays: nothing pending (it won't load), no undo step, "
            + "the banner up, its title white; the popup says it's enabled and which copy loads",
            arrived && popup.Contains(Draft(NewCopyOn, ("mods", "Test Twin (SmmTestTwinC)"), ("loads", Named(b)))), $"{arrivedSay}; {popup}");
        // With the switch off (no copy ticked), a new copy arrives off. A tick on another copy then brings that one alone,
        // the new one off beside it (copies_off). Undone, as before.
        Dependencies.Set([b], false, screen);
        CategoryList.Redraw(content);
        await Wait(menu, 0.3);
        var copyOff = new Mod
        {
            path = "C:/SmmSameIdTest/SmmTestTwinD", modSource = ModSource.ModsDirectory, state = ModLoadState.AddedAtRuntime,
            manifest = new ModManifest { id = TwinId, name = "Test Twin", version = "1.3.0" },
        };
        NewModPopupPatch.TestReset();
        NModalContainer.Instance?.Clear();
        GameInstalls(copyOff);
        await Wait(menu, 0.4);
        var offPopup = string.Join(" | ", PopupTexts());
        NModalContainer.Instance?.Clear();
        var arrivedOff = !Copies.Ticked(copyOff) && !SwitchOn();
        await Tick(rowB, true);
        var aloneOn = Ticked(rowB) && !Copies.Ticked(copyOff) && !Ticked(rowA) && CopiesOff().Contains($"{TwinId}|l:SmmTestTwinD") && Picked() == "l:SmmTestTwinB"
            && SwitchOn();
        History.Undo(); // the tick
        History.Undo(); // the off
        await Wait(menu, 0.3);
        Forget(copyOff);
        CategoryList.Redraw(content);
        await Wait(menu, 0.3);
        Check("... with the switch off, a new copy arrives off (the popup says it's disabled); a tick on another copy brings that one alone, the new one "
            + "staying off; undone, as before",
            arrivedOff && aloneOn && offPopup.Contains(Draft(NewCopyOff, ("mods", "Test Twin (SmmTestTwinD)"))) && Picked() == "l:SmmTestTwinB" && Ticked(rowB)
            && !Ticked(rowA) && SwitchOn() && OffAsAtStart(), $"arrived off {arrivedOff}, alone on {aloneOn}; {Say()}; {offPopup}");
        var update = new Mod
        {
            path = a.path, modSource = ModSource.ModsDirectory, state = ModLoadState.AddedAtRuntime,
            manifest = new ModManifest { id = TwinId, name = "Test Twin", version = "1.0.1" },
        };
        NewModPopupPatch.TestReset();
        NModalContainer.Instance?.Clear();
        GameInstalls(update);
        await Wait(menu, 0.4);
        var updatePopup = string.Join(" | ", PopupTexts());
        NModalContainer.Instance?.Clear();
        var notNew = !Installed.Mods.Contains(update);
        if (Rows().FirstOrDefault(r => r.Mod == update) is { } updateRow)
        {
            content.RemoveChild(updateRow);
            updateRow.QueueFree();
        }
        GameForgets(update);
        CategoryList.Redraw(content);
        await Wait(menu, 0.3);
        Check("... an update of a copy (its own folder again) isn't a new copy: the popup's usual words", notNew
            && updatePopup.Contains("New mods were detected") && !NewCopyWords(updatePopup), $"not new {notNew}; {updatePopup}");

        // A mod installed once (made up, as found at launch) whose saved pick still names a second copy, uninstalled since
        // (picks are never pruned; our record never had it: new to us), and an active profile naming no copy (as from
        // before copies). That copy comes mid-session: ticked, it doesn't take over, and the profile holds the copy there
        // with the new one off, so its arriving on is the profile's unsaved edit; unticking the copy there makes the new
        // one load; an update of the new copy keeps that pick and isn't announced as a new copy again. Then two copies of a
        // mod new to the game, one after the other (the first one's popup held back: one every 30 s): the new-mods words,
        // not a new copy's, both ticked, and the copy that came first keeps the pick (the record's order puts the second
        // first).
        const string Back = "SmmSameIdBack", Pair = "SmmSameIdPair";
        var backFirst = Made(Back, "Back1", ModLoadState.Loaded);
        var (backSecond, backUpdate) = (Made(Back, "Back2", ModLoadState.AddedAtRuntime), Made(Back, "Back2", ModLoadState.AddedAtRuntime, version: "1.0.1"));
        var (pairFirst, pairSecond) = (Made(Pair, "PairB", ModLoadState.AddedAtRuntime), Made(Pair, "PairA", ModLoadState.AddedAtRuntime));
        GameMods().Add(backFirst);
        History.Quietly(data =>
        {
            data.CopyPicks = Copies.WithPicks(data.CopyPicks, [$"{Back}|l:Back2"]);
            if (data.Profiles.FirstOrDefault(p => p.Name == data.ActiveProfile) is { } active)
                foreach (var list in new[] { active.Mods, active.Edited })
                    list?.Add(new ProfileMod(Back, ModSource.ModsDirectory, true));
        });
        string? BackPicked() => Copies.Picked(Back, ModSource.ModsDirectory);
        ProfileMod? Held() => Profiles.Active?.Mods.FirstOrDefault(m => m.Id == Back);
        var backSteps = History.Steps(History.Track.List);
        NewModPopupPatch.TestReset();
        NModalContainer.Instance?.Clear();
        GameInstalls(backSecond);
        await Wait(menu, 0.4);
        var backPopup = string.Join(" | ", PopupTexts());
        NModalContainer.Instance?.Clear();
        var cameBack = BackPicked() == "l:Back1" && Copies.Ticked(backFirst) && Copies.Ticked(backSecond) && !Copies.Loads(backSecond)
            && Held() is { Copy: "l:Back1", CopiesOff: ["l:Back2"] } && Profiles.Edited && History.Steps(History.Track.List) == backSteps
            && backPopup.Contains(Draft(NewCopyOn, ("mods", $"{Back} (Back2)"), ("loads", $"{Back} (Back1)")));
        var cameSay = $"pick {BackPicked()}, on {Copies.Ticked(backFirst)}/{Copies.Ticked(backSecond)}, loads {Copies.Loads(backSecond)}, held {Held()?.Copy} "
            + $"off [{string.Join(",", Held()?.CopiesOff ?? [])}], edited {Profiles.Edited}, steps {History.Steps(History.Track.List) - backSteps}; {backPopup}";
        Dependencies.Set([backFirst], false, screen); // as its tick does (CopyTickPatch), the profile and history after
        Profiles.Track();
        History.Record();
        var switched = BackPicked() == "l:Back2" && !Copies.Ticked(backFirst) && Copies.Ticked(backSecond) && Profiles.Edited
            && History.Steps(History.Track.List) == backSteps + 1;
        NewModPopupPatch.TestReset();
        GameInstalls(backUpdate);
        await Wait(menu, 0.4);
        var backUpdatePopup = string.Join(" | ", PopupTexts());
        NModalContainer.Instance?.Clear();
        var updateKeeps = BackPicked() == "l:Back2" && Installed.Mods.Contains(backUpdate) && backUpdatePopup.Contains("New mods were detected")
            && !NewCopyWords(backUpdatePopup);
        GameInstalls(pairFirst); // within 30 s of the last popup: none for it
        await Wait(menu, 0.2);
        NewModPopupPatch.TestReset();
        NModalContainer.Instance?.Clear();
        GameInstalls(pairSecond);
        await Wait(menu, 0.4);
        var pairPopup = string.Join(" | ", PopupTexts());
        NModalContainer.Instance?.Clear();
        var pairPicked = Copies.Picked(Pair, ModSource.ModsDirectory);
        var pairNew = pairPopup.Contains("New mods were detected") && !NewCopyWords(pairPopup) && pairPicked == "l:PairB"
            && Copies.Ticked(pairFirst) && Copies.Ticked(pairSecond);
        History.Undo(); // the switch
        await Wait(menu, 0.3);
        // Back to the copy there; the new copy's arriving on is still the profile's edit, and nothing else about Back (the
        // pair below arrived on since: its edits too).
        var (backOn, backOff, backSwitched) = Profiles.UnsavedChanges();
        var backUndone = BackPicked() == "l:Back1" && backOn.Where(m => m.Id.StartsWith(Back)).Select(m => m.Name).Distinct().SequenceEqual([$"{Back} (Back2)"])
            && !backOff.Any(m => m.Id.StartsWith(Back)) && !backSwitched.Any(s => s.To.manifest?.id == Back);
        // Put back: the made-up copies, their rows and our data for them (quietly, in every undo snapshot).
        foreach (var gone in new[] { backSecond, backUpdate, pairFirst, pairSecond })
        {
            if (Rows().FirstOrDefault(r => r.Mod == gone) is { } goneRow)
            {
                content.RemoveChild(goneRow);
                goneRow.QueueFree();
            }
            GameForgets(gone);
        }
        GameMods().Remove(backFirst);
        static bool MadeUp(string? id) => id is Back or Pair;
        SaveManager.Instance.SettingsSave.ModSettings?.ModList.RemoveAll(e => MadeUp(e.Id));
        History.Quietly(data =>
        {
            data.Copies?.Remove(Back);
            data.Copies?.Remove(Pair);
            foreach (var key in data.Mods.Keys.Where(k => MadeUp(Copies.IdOf(k))).ToList())
                data.Mods.Remove(key);
            data.CopyPicks = Store.TidyPicks(data.CopyPicks?.Where(k => !MadeUp(Copies.IdOf(k))));
            data.CopiesOff = data.CopiesOff?.Where(k => !MadeUp(Copies.IdOf(k))).ToList();
            foreach (var profile in data.Profiles)
            {
                profile.Mods.RemoveAll(m => MadeUp(m.Id));
                profile.Edited?.RemoveAll(m => MadeUp(m.Id));
            }
        });
        Profiles.Track();
        CategoryList.Redraw(content);
        await Wait(menu, 0.3);
        Check("... a copy new to our record mid-session that a saved pick still names arrives ticked and doesn't take over (its popup says which copy "
            + "loads); the active profile, naming no copy, holds the one there with the new one off, so its arriving on is the profile's edit; unticking the "
            + "copy there makes the new one load, one undo step; an update of the new copy keeps that pick and isn't a new copy again",
            cameBack && switched && updateKeeps && backUndone,
            $"{cameSay}; switched {switched}, update keeps {updateKeeps} ({backUpdatePopup}), undone {backUndone} "
            + $"(+{string.Join(",", backOn.Select(m => m.Name))} -{string.Join(",", backOff.Select(m => m.Name))} ~{backSwitched.Count})");
        Check("... two copies of a mod new to the game, one after the other: the new-mods words, both ticked, and the copy that came first picked",
            pairNew, $"pick {pairPicked}; {pairPopup}");

        // A copy uninstalled and back mid-session (made up: our record has it, from when both were installed, the pick the
        // copy there), the active profile saved with both ticked: it arrives ticked and keeps the profile's tick, so
        // nothing is unsaved, as when it's back before a launch.
        const string Again = "SmmSameIdAgain";
        var (againThere, againBack) = (Made(Again, "Again1", ModLoadState.Loaded), Made(Again, "Again2", ModLoadState.AddedAtRuntime));
        GameMods().Add(againThere);
        Copies.TestRecount(); // one in for one out above
        History.Quietly(data =>
        {
            (data.Copies ??= [])[Again] = ["l:Again1", "l:Again2"];
            data.CopyPicks = Copies.WithPicks(data.CopyPicks, [$"{Again}|l:Again1"]);
            if (data.Profiles.FirstOrDefault(p => p.Name == data.ActiveProfile) is { } active)
                foreach (var list in new[] { active.Mods, active.Edited })
                    list?.Add(new ProfileMod(Again, ModSource.ModsDirectory, true, "l:Again1", CopiesOff: []));
        });
        Profiles.Track();
        var againClean = !Profiles.Edited;
        NewModPopupPatch.TestReset();
        NModalContainer.Instance?.Clear();
        GameInstalls(againBack);
        await Wait(menu, 0.4);
        NModalContainer.Instance?.Clear();
        Profiles.Track();
        var againHeld = Profiles.Active?.Mods.FirstOrDefault(m => m.Id == Again);
        var againOn = Profiles.UnsavedChanges().On.Where(m => m.Id.StartsWith(Again)).Select(m => m.Name).ToList();
        var keptTick = againClean && Copies.Ticked(againBack) && Copies.Picked(Again, ModSource.ModsDirectory) == "l:Again1" && againHeld is { Copy: "l:Again1", CopiesOff: [] }
            && !Profiles.Edited && againOn.Count == 0;
        var againSay = $"clean before {againClean}; ticked {Copies.Ticked(againBack)}, pick {Copies.Picked(Again, ModSource.ModsDirectory)}, held {againHeld?.Copy} "
            + $"off [{string.Join(",", againHeld?.CopiesOff ?? [])}], edited {Profiles.Edited} (+{string.Join(",", againOn)})";
        void Uninstall(Mod gone)
        {
            if (Rows().FirstOrDefault(r => r.Mod == gone) is { } left)
            {
                content.RemoveChild(left);
                left.QueueFree();
            }
            GameForgets(gone);
            Copies.TestRecount();
        }
        // Gone and back again, the pick naming it now (as a launch without it leaves the pick): our record has it, so it
        // stays the pick, and loads after a relaunch.
        Uninstall(againBack);
        History.Quietly(data => data.CopyPicks = Copies.WithPicks(data.CopyPicks, [$"{Again}|l:Again2"]));
        var againReturn = Made(Again, "Again2", ModLoadState.AddedAtRuntime);
        NewModPopupPatch.TestReset();
        GameInstalls(againReturn);
        await Wait(menu, 0.4);
        NModalContainer.Instance?.Clear();
        var pickKept = Copies.Picked(Again, ModSource.ModsDirectory) == "l:Again2" && Copies.Loads(againReturn) && !Copies.Loads(againThere);
        Uninstall(againReturn);
        GameMods().Remove(againThere);
        Copies.TestRecount();
        SaveManager.Instance.SettingsSave.ModSettings?.ModList.RemoveAll(e => e.Id == Again);
        History.Quietly(data =>
        {
            data.Copies?.Remove(Again);
            foreach (var key in data.Mods.Keys.Where(k => Copies.IdOf(k) == Again).ToList())
                data.Mods.Remove(key);
            data.CopyPicks = Store.TidyPicks(data.CopyPicks?.Where(k => Copies.IdOf(k) != Again));
            data.CopiesOff = data.CopiesOff?.Where(k => Copies.IdOf(k) != Again).ToList();
            foreach (var profile in data.Profiles)
            {
                profile.Mods.RemoveAll(m => m.Id == Again);
                profile.Edited?.RemoveAll(m => m.Id == Again);
            }
        });
        Profiles.Track();
        CategoryList.Redraw(content);
        await Wait(menu, 0.3);
        Check("... a copy uninstalled and back mid-session (our record has it), the profile saved with both ticked: it arrives ticked and keeps the "
            + "profile's tick, nothing unsaved (as when it's back before a launch)", keptTick, againSay);
        Check("... gone and back again mid-session, the saved pick naming it (our record has it): it stays the pick, loading after a relaunch", pickKept);
        Check("... as it started: the fixture's pick, the other copy off (copies_off as the page opened with, in our file too), nothing changed since the "
            + "page opened for the twins", Picked() == "l:SmmTestTwinB" && Ticked(rowB) && !Ticked(rowA) && SwitchOn() && !Profiles.Edited && OffAsAtStart()
            && CopiesBroken() == null, Say());
    }

    // The words of the duplicate copies, word for word as settings_ui.json has them: drafts until Jordan's copy pass, so
    // here once for every check that reads them (an edit there is one edit here). Draft fills in their values.
    const string SelfLockedTip = "Spire Mod Manager is installed more than once, so its copies stay enabled. If one is removed, another still loads. "
        + "To turn Spire Mod Manager off, disable them all on the vanilla Modding page.";
    const string SelfLockedOtherOffTip = "Spire Mod Manager stays enabled. {other} is disabled, so Spire Mod Manager won't load if this copy is removed. Enable it too.";
    const string SameIdText = "{count} mods with the same ID are enabled. Only one copy of each loads.";
    const string BannerLoads = "{mod}: loads", BannerWontLoad = "{mod}: won't load";
    const string RelaunchLoses = "{other} loads instead";
    const string CopySwitchLine = "{mod}: {to} loads instead of {from}";
    const string ClashTip = "Another copy of this mod is enabled: {others}. Only {loads} loads.";
    const string StatusWontLoad = "Won't load (same ID)";
    const string CopyLoses = "{other} loads instead. {why} Disable one to choose.";
    const string WhyLocal = "The game loads the local copy.", WhyHigher = "The game loads the higher version.", WhySource = "Only one copy from the same source loads.";
    const string LoadsAlso = "{other} is also enabled - this copy is the one that loads.";
    const string FailedOther = "{other} is also installed. To load that copy instead, enable it and disable this one.";
    const string SelfRuns = "This copy is running. {other} stays enabled too, in case this one is removed.";
    const string SelfBackup = "{other} is running. This copy stays enabled, in case that one is removed.";
    const string NewCopyOff = "A new copy of {mods} was installed and is disabled.";
    const string NewSelfCopy = "A new copy of Spire Mod Manager (this mod) was installed. Its copies stay enabled, and only {loads} loads.";

    // The banners' Go to and Show the same way: drafts until the copy pass, word for word as settings_ui.json has them.
    const string GoToProblem = "Go to", GoToProblemTip = "Open the next affected mod";
    const string ShowFilter = "Show";

    // Jordan's words from his copy pass, read as the game shows them (settings_ui.json, or a translation over it) rather
    // than quoted, so a later edit there needs none here. Draft fills in the values by the names the code passes.
    static string CopyOnTip => Template("SPIRE_MOD_MANAGER.COPY_ON_TIP");
    static string BulkEnablesTip => Template("SPIRE_MOD_MANAGER.BULK_ENABLES_COPIES_TIP");
    static string NewCopyOn => Template("SPIRE_MOD_MANAGER.NEW_COPY_ON_BODY");
    static string MissingSearchHint => Template("SPIRE_MOD_MANAGER.MISSING_SEARCH_HINT");
    static string MissingSearchHintOne => Menus.Text("SPIRE_MOD_MANAGER.MISSING_SEARCH_HINT_ONE");
    static string ShowFilterTip => Menus.Text("SPIRE_MOD_MANAGER.SHOW_FILTER_TIP");
    static string ShowFilterFindTip => Menus.Text("SPIRE_MOD_MANAGER.SHOW_FILTER_FIND_TIP");

    // A text with its {values} still in it, for Draft (Menus.Text formats it, and these need their values).
    static string Template(string key) => new LocString("settings_ui", key).GetRawText();

    // Whether a popup has a new copy's words (NewModPopupPatch), whichever mods they name: each text's longest run
    // between its values.
    static bool NewCopyWords(string popup) => new[] { "SPIRE_MOD_MANAGER.NEW_COPY_ON_BODY", "SPIRE_MOD_MANAGER.NEW_COPIES_ON_BODY",
            "SPIRE_MOD_MANAGER.NEW_COPY_OFF_BODY", "SPIRE_MOD_MANAGER.NEW_SELF_COPY_BODY" }
        .Select(key => Template(key).Split('{', '}').Where((_, i) => i % 2 == 0).MaxBy(s => s.Length) ?? "")
        .Any(words => words.Length > 0 && popup.Contains(words));

    static string Draft(string text, params (string Name, string Value)[] values) =>
        values.Aggregate(text, (filled, value) => filled.Replace($"{{{value.Name}}}", value.Value));

    // The fixture twins both ticked at launch (BothTickedQuietly: as a launch would have them, A set aside, B loaded): B,
    // the pick, loads and A won't, with no duplicate error and nothing pending or changed; A's title white, not the
    // game's grey for a copy set aside; its status and panel say B loads instead, one copy of a source loading; both chips
    // yellow, naming the copy that loads; the banner and its card; the relaunch check, gone with the banner's Dismiss;
    // Find takes the copy that loads, and a need of the copy that won't load is no problem. Unticking the copy that loads:
    // the other one loads (picked), one undo step, pending; undo, redo, Revert all. Unticking the one that doesn't load: a
    // change, nothing pending. The last ticked copy unticked: the switch off, the pick kept; then a tick on the other
    // brings it alone. Sharing: a row per ticked copy, the one that loads first; read back, both ticked. Titles: the
    // game's colour in Vanilla View, and for a ticked copy whose state isn't one the game greys. Put back as it was.
    static async Task TestSameIdBothOn(Node menu, NModdingScreen screen, Control content, string outDir)
    {
        List<NModMenuRow> Rows() => content.GetChildren().OfType<NModMenuRow>().ToList();
        NModMenuRow? RowOf(string copy) => Rows().FirstOrDefault(r => r.Mod is { } m && m.manifest?.id == TwinId && Copies.CopyOf(m) == copy);
        if (Layout.Current != Layout.View.Categories || Profiles.Active is not { } active)
        {
            Check("same ID, both ticked: on the main list, a profile active", false, $"{Layout.Current}, {Profiles.Active?.Name}");
            return;
        }
        if (RowOf("l:SmmTestTwinA") is not { Mod: { } a } rowA || RowOf("l:SmmTestTwinB") is not { Mod: { } b } rowB)
        {
            TwinsMissing("same ID, both ticked");
            return;
        }
        if (Profiles.Edited)
            Profiles.Save(); // start clean
        static bool Ticked(NModMenuRow row) => row.GetNode<NTickbox>("Tickbox").IsTicked;
        async Task Tick(NModMenuRow row, bool on)
        {
            var box = row.GetNode<NTickbox>("Tickbox");
            box.IsTicked = on;
            box.EmitSignal(NTickbox.SignalName.Toggled, box);
            await Wait(menu, 0.3);
        }
        static string Named(Mod mod) => (mod.manifest?.name ?? "") + Copies.Which(mod);
        string? Picked() => Copies.Picked(TwinId, ModSource.ModsDirectory);
        List<SettingsSaveMod> Entries() => SaveManager.Instance.SettingsSave.ModSettings?.ModList.Where(e => e.Id == TwinId).ToList() ?? [];
        bool SwitchOn() => Entries() is { Count: > 0 } entries && entries.All(e => e.IsEnabled);
        var (keyA, keyB) = ($"{TwinId}|l:SmmTestTwinA", $"{TwinId}|l:SmmTestTwinB");
        static Color Title(NModMenuRow row) => row.GetNode<Control>("Title").Modulate;
        // The colour the game gave the title at launch (RowTitles keeps it).
        static Color GameTitle(NModMenuRow row) => (Color)row.GetNode<Control>("Title").GetMeta("spire_mod_manager_title_color");
        // Changes about the twins (not Test Twin User).
        List<string> TwinChanges() => HistoryBar.Describe().Where(l => l.Contains(Named(a)) || l.Contains(Named(b))).ToList();
        var border = screen.GetNode<Control>("ModsBorder");
        Control? SameId() => border.GetNodeOrNull<Control>(LoadOrder.SameIdName);
        string Say() => $"pick {Picked()}, boxes A {Ticked(rowA)} B {Ticked(rowB)}, loads A {Copies.Loads(a)} B {Copies.Loads(b)}, switch {SwitchOn()}, "
            + $"off {string.Join(",", CopiesOff())}, pending {ModEntry.HasPendingChanges()}; status A {ModDetails.Status(a)}, B {ModDetails.Status(b)}; "
            + $"lines A {ModDetails.CopyLine(a, false)?.Text}, B {ModDetails.CopyLine(b, false)?.Text}; changes {string.Join(" | ", TwinChanges())}; "
            + $"titles A {Title(rowA)} B {Title(rowB)}; {CopiesBroken() ?? "kept"}";
        async Task<string> RelaunchBody()
        {
            RelaunchCheck.Show(screen);
            await Wait(menu, 0.3);
            var body = (menu.GetTree().Root.GetNodeOrNull(RelaunchCheck.Name)?.FindChild("Body", true, false) as RichTextLabel)?.GetParsedText() ?? "";
            RelaunchCheck.Cancel();
            await Wait(menu, 0.3);
            return body;
        }
        var loses = $"{Named(a)}  {Draft(RelaunchLoses, ("other", Named(b)))}";

        // As the page has them: A off (copies_off), its title the game's grey for a copy set aside. Then both ticked.
        var greyOff = !Ticked(rowA) && Title(rowA) == MegaCrit.Sts2.Core.Helpers.StsColors.gray && a.state == ModLoadState.DisabledDuplicate;
        var offWere = CopiesOff();
        BothTickedQuietly(true);
        Profiles.RefreshTickboxes(screen);
        CategoryList.Redraw(content);
        await Wait(menu, 0.3);
        var bothOff = CopiesOff();
        var steps = History.Steps(History.Track.List);
        var chips = new[] { a, b }.Select(m => Tags.For(m).FirstOrDefault(c => c.Text == "Same ID")).ToList();
        var bannerText = (SameId()?.FindChild("SameIdWarning", true, false) as Label)?.Text;
        var card = await BannerCard(menu, screen, SameId());
        Check("same ID, both copies ticked at launch: the pick (B) loads and A won't, no duplicate error, nothing pending or changed; A's title white, not the "
            + "game's grey for a copy set aside; A reads Won't load, B loading in its place (one copy of a source loads); both chips yellow, naming the copy that "
            + "loads; the banner and its card say which loads; Find takes the copy that loads",
            greyOff && Ticked(rowA) && Ticked(rowB) && Picked() == "l:SmmTestTwinB" && Copies.Loads(b) && !Copies.Loads(a) && SwitchOn() && !bothOff.Contains(keyA)
            && b.state == ModLoadState.Loaded && a.state == ModLoadState.DisabledDuplicate
            && !ModManager.Mods.Any(m => m.errors?.Any(e => e.LocEntryKey == "MOD_ERROR.DUPLICATE_ID") == true)
            && !ModEntry.HasPendingChanges() && TwinChanges().Count == 0 && !Profiles.Edited && Title(rowA) == Colors.White && Title(rowB) == Colors.White
            && ModDetails.Status(a).Contains(StatusWontLoad) && ModDetails.Status(b).Contains("Enabled")
            && ModDetails.CopyLine(a, false)?.Text == Draft(CopyLoses, ("other", Named(b)), ("why", WhySource))
            && ModDetails.CopyLine(b, false)?.Text == Draft(LoadsAlso, ("other", Named(a)))
            && chips[0] is { } chipA && chipA.Color == Tags.SameIdClash && chipA.Tip == Draft(ClashTip, ("others", Named(b)), ("loads", Named(b)))
            && chips[1] is { } chipB && chipB.Color == Tags.SameIdClash && chipB.Tip == Draft(ClashTip, ("others", Named(a)), ("loads", Named(b)))
            && (!Layout.Wide || bannerText == Draft(SameIdText, ("count", "2")) && card.Contains(Draft(BannerLoads, ("mod", Named(b))))
                && card.Contains(Draft(BannerWontLoad, ("mod", Named(a)))))
            && Dependencies.Find(TwinId) == b && History.Steps(History.Track.List) == steps && CopiesBroken() == null,
            $"grey before {greyOff}; {Say()}; chips {string.Join(" / ", chips.Select(c => $"{c?.Color} {c?.Tip}"))}; banner {bannerText} ({string.Join(" | ", card)}); "
            + $"find {Copies.KeyOf(Dependencies.Find(TwinId))}; edited {Profiles.Edited}");
        await Capture(screen, outDir, "19b-same-id-both-on");

        // The relaunch check: A, under the copy that loads in its place. The banner's Dismiss takes it out of there too.
        var body = await RelaunchBody();
        (SameId()?.FindChild("DismissSameIdButton", true, false) as BaseButton)?.EmitSignal(BaseButton.SignalName.Pressed);
        await Wait(menu, 0.3);
        var dismissed = SameId() == null && !(await RelaunchBody()).Contains(loses);
        Store.Data.SameIdDismissed = null;
        Store.SaveViewState();
        CategoryList.Redraw(content);
        await Wait(menu, 0.3);
        Check("... the relaunch check lists the ticked copy that won't load, naming the one that loads; after the banner's Dismiss, neither",
            body.Contains(loses) && dismissed && (SameId() != null || !Layout.Wide), $"dismissed {dismissed}; {body}");

        // A need of A that can't be met: no problem of its (it won't load), in its panel or the relaunch check.
        var needsWere = a.manifest?.dependencies;
        if (a.manifest is { } manifest)
            manifest.dependencies = [new ModDependency(Missing)];
        try
        {
            var problems = ModDetails.DependencyProblems(a);
            var withNeed = await RelaunchBody();
            var line = withNeed.Split('\n').FirstOrDefault(l => l.Contains($"{Named(a)}  "))?.Trim();
            Check("... a need of the ticked copy that won't load is no problem: none in its panel, its line in the relaunch check the copy that loads alone",
                problems.Count == 0 && line == $"•  {loses}", $"problems {string.Join(" | ", problems)}; line {line}");
        }
        finally
        {
            if (a.manifest is { } restored)
                restored.dependencies = needsWere;
        }

        // Unticking the copy that loads: the other one is the pick (the copy that loaded is off), a relaunch away.
        var (onBefore, offBefore, _) = HistoryBar.Counts();
        steps = History.Steps(History.Track.List);
        await Tick(rowB, false);
        var (onNow, offNow, _) = HistoryBar.Counts();
        Check("... unticking the copy that loads: the other one loads (picked; B in copies_off, in our file too), one undo step; A reads Pending relaunch, "
            + "loading in B's place, and B too (it loaded, and won't); Changes: B off; pending; no banner",
            Picked() == "l:SmmTestTwinA" && CopiesOff().Contains(keyB) && FileCopiesOff().Contains(keyB) && SwitchOn() && Ticked(rowA) && !Ticked(rowB)
            && ModDetails.Status(a).Contains("Pending relaunch") && ModDetails.CopyLine(a, false)?.Text == $"Loads on relaunch, in place of {Named(b)}"
            && ModDetails.Status(b).Contains("Pending relaunch") && TwinChanges().SequenceEqual([$"Disabled: {Named(b)}"]) && onNow == onBefore && offNow == offBefore + 1
            && History.Steps(History.Track.List) == steps + 1 && ModEntry.HasPendingChanges() && SameId() == null && CopiesBroken() == null,
            $"{Say()}; counts +{onNow - onBefore} -{offNow - offBefore}");
        History.Undo();
        await Wait(menu, 0.3);
        var undone = Ticked(rowA) && Ticked(rowB) && Picked() == "l:SmmTestTwinB" && CopiesOff().SequenceEqual(bothOff) && FileCopiesOff().SequenceEqual(bothOff);
        History.Redo();
        await Wait(menu, 0.3);
        var redone = Picked() == "l:SmmTestTwinA" && Ticked(rowA) && !Ticked(rowB);
        History.RevertMods();
        await Wait(menu, 0.3);
        var reverted = Ticked(rowA) && Ticked(rowB) && Picked() == "l:SmmTestTwinB" && History.AtEntered && CopiesOff().SequenceEqual(bothOff);
        History.Undo(); // the revert
        await Wait(menu, 0.3);
        var revertUndone = Picked() == "l:SmmTestTwinA" && !Ticked(rowB);
        History.Undo(); // the untick
        await Wait(menu, 0.3);
        Check("... undone: both ticked, B picked, copies_off as before (in our file too); redo; Revert all goes back to both ticked, and undoes",
            undone && redone && reverted && revertUndone && Ticked(rowA) && Ticked(rowB) && Picked() == "l:SmmTestTwinB" && CopiesOff().SequenceEqual(bothOff),
            $"undo {undone}, redo {redone}, revert {reverted}, its undo {revertUndone}; {Say()}");

        // The copy that loads switched with no box changed (as a profile naming the other copy does): Changes says which
        // loads instead, and the main menu's note counts it. B says A loads as only one copy from a source does (not that A
        // was enabled last: nobody enabled it, the profile's copy is picked). Put back.
        Dependencies.Write([(a, true)], pick: [a]);
        Profiles.RefreshTickboxes(screen);
        await Wait(menu, 0.3);
        var switchLine = Draft(CopySwitchLine, ("mod", a.manifest?.name ?? ""), ("to", Copies.Named(a)), ("from", Copies.Named(b)));
        var (switchChanges, pendingNote, switchedPending) = (HistoryBar.Describe(), MenuButton.PendingText(), ModEntry.HasPendingChanges());
        var hintedLine = ModDetails.CopyLine(b, false)?.Text;
        // Not counted where a relaunch can't apply the pick (the plan left the game's choice, or the self-check found it
        // not taken), nor pending, so the menu's dot and Changes agree; one the game decided before us still is both (Settle
        // put us first). Its switch turned off is pending all the same.
        var stuck = new List<string>();
        var planWas = SameIdLoad.Plan.GetValueOrDefault(TwinId);
        var switchWas = Entries().Select(e => (Entry: e, e.IsEnabled)).ToList();
        try
        {
            if (planWas != null)
                foreach (var why in new[] { SameIdLoad.Reason.DependencyAfterSlot, SameIdLoad.Reason.SetAsideByGame, SameIdLoad.Reason.DecidedBeforeUs })
                {
                    SameIdLoad.TestPlan(TwinId, planWas with { Why = why });
                    stuck.Add($"{why} {HistoryBar.CopySwitchCount()} {ModEntry.HasPendingChanges()}");
                }
            SameIdLoad.TestPlan(TwinId, planWas);
            SameIdLoad.TestResult(TwinId, SameIdLoad.Result.NotTaken);
            stuck.Add($"not taken {HistoryBar.CopySwitchCount()} {ModEntry.HasPendingChanges()}");
            switchWas.ForEach(s => s.Entry.IsEnabled = false);
            stuck.Add($"switch off {ModEntry.HasPendingChanges()}");
        }
        finally
        {
            switchWas.ForEach(s => s.Entry.IsEnabled = s.IsEnabled);
            SameIdLoad.TestPlan(TwinId, planWas);
            SameIdLoad.TestResult(TwinId, null);
        }
        Check("... a switch a relaunch can't apply isn't counted, nor pending: the plan left the game's choice, or the self-check found the pick not "
            + "taken; one the game decided before us is both; the id's switch turned off is pending all the same", string.Join(", ", stuck)
            == "DependencyAfterSlot 0 False, SetAsideByGame 0 False, DecidedBeforeUs 1 True, not taken 0 False, switch off True", string.Join(", ", stuck));
        Dependencies.Write([(b, true)], pick: [b]);
        Profiles.RefreshTickboxes(screen);
        await Wait(menu, 0.3);
        Check("... the copy that loads switched with no box changed (a profile naming the other copy): Changes says which loads instead, the main "
            + "menu's note counts it, pending; the other copy says it loads instead as only one copy of a source does; switched back, none of it",
            switchChanges.Contains(switchLine) && pendingNote.StartsWith("1 ") && switchedPending && hintedLine == Draft(CopyLoses, ("other", Named(a)), ("why", WhySource))
            && Picked() == "l:SmmTestTwinB" && Ticked(rowA) && Ticked(rowB) && !HistoryBar.Describe().Contains(switchLine) && !ModEntry.HasPendingChanges(),
            $"note {pendingNote}; changes {string.Join(" | ", switchChanges)}; B's line {hintedLine}; then {Say()}");

        // Unticking the copy that doesn't load: a change, but what loads is the same, so nothing is pending.
        await Tick(rowA, false);
        var pendingLine = screen.GetNode<Control>("%PendingChangesLabel").Visible;
        Check("... unticking the ticked copy that doesn't load: a change (Changes: A off), nothing pending, the pick kept, no banner",
            Picked() == "l:SmmTestTwinB" && !Ticked(rowA) && Ticked(rowB) && CopiesOff().Contains(keyA) && TwinChanges().SequenceEqual([$"Disabled: {Named(a)}"])
            && !ModEntry.HasPendingChanges() && !pendingLine && SameId() == null && CopiesBroken() == null, $"{Say()}; pending line {pendingLine}");

        // From one ticked copy (A off now): the last one unticked turns the switch off and keeps the pick and copies_off; a
        // tick on the other copy then brings it alone, the copy enabled last.
        var offOne = CopiesOff();
        await Tick(rowB, false);
        var allOff = Entries().All(e => !e.IsEnabled) && !Ticked(rowA) && !Ticked(rowB) && Picked() == "l:SmmTestTwinB" && CopiesOff().SequenceEqual(offOne);
        var allOffSay = Say();
        await Tick(rowA, true);
        var aloneOn = Ticked(rowA) && !Ticked(rowB) && CopiesOff().Contains(keyB) && !CopiesOff().Contains(keyA) && Picked() == "l:SmmTestTwinA" && SwitchOn()
            && CopiesBroken() == null;
        var aloneSay = Say();
        History.Undo(); // the tick
        History.Undo(); // the last untick
        History.Undo(); // the first untick
        await Wait(menu, 0.3);
        Check("... the last ticked copy unticked: the switch off, the pick and copies_off kept; then a tick on the other copy: only it on, picked; undone, both ticked",
            allOff && aloneOn && Ticked(rowA) && Ticked(rowB) && Picked() == "l:SmmTestTwinB" && CopiesOff().SequenceEqual(bothOff),
            $"all off {allOff} ({allOffSay}); alone on {aloneOn} ({aloneSay}); now {Say()}");

        // Sharing: a row per copy, both enabled, the one that loads first (a list read by its first row on gets that one).
        // Read back by id (local copies have no link): both ticked, the pick kept.
        var rows = Profiles.ShareRows(Profiles.Active ?? active).Where(r => r.Id == TwinId).ToList(); // an undo makes the profiles anew
        var readBack = Profiles.PlanImport([.. rows.Select(r => new ProfileCsv.Entry(r.Id, r.Enabled, r.Name, ""))]);
        Check("... sharing with both ticked: a row per copy, both enabled, the one that loads first; read back, both stay ticked, the pick kept",
            rows.Count == 2 && rows.All(r => r.Enabled) && rows[0].Version == b.manifest?.version
            && readBack.Mods.SingleOrDefault(m => m.Id == TwinId) is { Enabled: true, Copy: "l:SmmTestTwinB", CopiesOff: [] } && readBack is { On: 0, Off: 0, Same: 2 },
            $"{string.Join(" | ", rows.Select(r => $"{r.Id} {r.Version} {r.Enabled}"))}; read back {string.Join(", ", readBack.Mods.Where(m => m.Id == TwinId))} "
            + $"(on {readBack.On}, off {readBack.Off}, same {readBack.Same})");

        // Titles: a ticked copy whose state isn't one the game greys (made up: failed) keeps the game's colour; Vanilla View
        // shows the game's colours.
        var stateWas = a.state;
        a.state = ModLoadState.Failed;
        RowTitles.MarkChanged(Rows(), false);
        var failedTitle = Title(rowA) == GameTitle(rowA);
        a.state = stateWas;
        RowTitles.MarkChanged(Rows(), false);
        var vanilla = Bmm.Present || await WithVanillaView(screen, menu, () => Title(rowA) == GameTitle(rowA) && Title(rowA) != Colors.White);
        Check("... titles: white for a ticked copy the game greyed; the game's colour for one whose state it doesn't grey, and in Vanilla View",
            failedTitle && vanilla && Title(rowA) == Colors.White, $"failed {failedTitle}, vanilla {vanilla}; {Say()}");

        BothTickedQuietly(false);
        Profiles.RefreshTickboxes(screen);
        CategoryList.Redraw(content);
        await Wait(menu, 0.3);
        Check("... put back: A off again (copies_off as the page had it), B the pick, nothing changed", !Ticked(rowA) && Ticked(rowB) && Picked() == "l:SmmTestTwinB"
            && CopiesOff().SequenceEqual(offWere) && FileCopiesOff().SequenceEqual(offWere) && TwinChanges().Count == 0 && !Profiles.Edited && !ModEntry.HasPendingChanges()
            && CopiesBroken() == null, Say());
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
        await PanelFor(menu, screen, ui);
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
        Check("tag suggestions: a heading over them says what they are and how to take several",
            TagSuggestions.Heading() == "Currently used tags; Shift-click to add multiple", TagSuggestions.Heading());
        TagSuggestions.Press("Fast", shift: true);
        await Wait(menu, 0.2);
        Check("tag suggestions: a Shift-click takes one, the box keeps focus and the list stays for more", box.Text == "Strategy, Story-heavy, Fast, " && box.HasFocus()
            && TagSuggestions.Current != null && TagSuggestions.Labels().Contains("Co-op"), $"\"{box.Text}\": {Listed()}");
        TagSuggestions.Press("Co-op");
        await Wait(menu, 0.3);
        Check("tag suggestions: a click takes one and finishes: the box left, the list closed, the tags saved, tidied", TagSuggestions.Current == null && !box.HasFocus()
            && Store.InfoFor("SmmTestUi")?.Tags?.SequenceEqual(["Strategy", "Story-heavy", "Fast", "Co-op"]) == true, string.Join(", ", Store.InfoFor("SmmTestUi")?.Tags ?? []));

        Selection.Clear(content);
        Selection.Add(content, [lib, other]);
        // The menu opens at the mouse: over the second row, as a right-click there would. A headless run has no mouse of
        // its own (its place is whatever the popups before left, out to infinity and past it).
        Hover.TestMouse = () => other.GetGlobalRect().GetCenter();
        var opened = Selection.Mods.Count == 2 ? CategoryList.ShowBulkMenu(content) : null;
        Hover.TestMouse = null;
        if (opened is CanvasLayer bulk && bulk.FindChild("Tags", true, false)?.GetNodeOrNull<LineEdit>("Box") is { } bulkBox)
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

    // Tag pills (TagPills) in the panel's tags box, with made-up tags (the data is put back after): finished tags drawn
    // as pills where the box draws its text, their text smaller unless the caret or a selection is at one (a click on
    // one lands in its tag and opens it), the word being typed plain, the colours, the commas invisible;
    // a typed comma makes a pill; Backspace and Delete by Jordan's rule (a fresh press next to a pill takes it and its
    // separator as one undo step, a held key's repeats stop at it); Copy, Cut and Paste of whole tags; a double-click
    // takes the whole tag; all of that after an emoji too, and the pills with a right-to-left tag first; a comma the full
    // box has no room for takes no undo step; Ctrl+Z takes back a picked suggestion; a long list scrolls, its pills with
    // it; leaving the box still tidies. Keys and clicks go through Godot's input as a player's do (a held key's repeats are Echo
    // events), each edit landed at its frame's end before the next; the clipboard is the test's own.
    static async Task TestTagPills(Node menu, NModdingScreen screen, Control content, string outDir)
    {
        if (content.GetChildren().OfType<NModMenuRow>().FirstOrDefault(r => r.Mod?.manifest?.id == "SmmTestUi") is not { } ui)
        {
            Results.Add("SKIP tag pills: test mods not installed");
            return;
        }
        var before = Store.EditsJson();
        try
        {
            Store.Batch(() =>
            {
                Store.EditMod("SmmTestUi", info => info.Tags = null); // its box showing, not its pills at rest
                Store.EditMod("SmmPillsA", info => info.Tags = ["Zebra-stripes"]);
            });
            CategoryList.Select(screen, ui);
            await PanelFor(menu, screen, ui);
            await Wait(menu, 0.5);
            var box = screen.GetNode<LineEdit>("%ModInfoContainer/SpireModManagerYours/Tags/Box");
            box.GrabFocus();
            await Wait(menu, 0.2);
            if (!box.HasFocus() || !box.IsEditing())
            {
                Results.Add($"FAIL tag pills: the tags box didn't take the keyboard (focus {box.HasFocus()}, editing {box.IsEditing()})");
                return;
            }
            TagPills.TestClipboard = "";
            var cream = new Color(1, 0.964706f, 0.886275f);
            var inBox = new Rect2(Vector2.Zero, box.Size);
            string Pills() => string.Join(", ", TagPills.TestPills(box).Select(p => p.Tag));
            string Say() => $"\"{box.Text}\" caret {box.CaretColumn}, pills: {Pills()}";
            async Task Typed(string text, int? caret = null)
            {
                box.Text = text;
                box.CaretColumn = caret ?? TagPills.Columns(text);
                box.EmitSignal(LineEdit.SignalName.TextChanged, text);
                await Wait(menu, 0.05);
            }
            async Task Send(Key key, bool pressed, bool echo = false, bool ctrl = false, char unicode = '\0')
            {
                Input.ParseInputEvent(new InputEventKey { Keycode = key, Unicode = unicode, CtrlPressed = ctrl, Pressed = pressed, Echo = echo });
                await Wait(menu, 0.05);
            }
            async Task Tap(Key key, bool ctrl = false, char unicode = '\0')
            {
                await Send(key, true, ctrl: ctrl, unicode: unicode);
                await Send(key, false, ctrl: ctrl, unicode: unicode);
            }
            async Task Repeats(Key key, int count) // a held key, after its press
            {
                for (var i = 0; i < count; i++)
                    await Send(key, true, echo: true);
            }
            // A click at a point in the box, through the viewport as a player's (works headless).
            async Task ClickAt(Vector2 local, bool doubleClick = false)
            {
                var at = ViewportPoint(box, box.GetGlobalTransform() * local);
                foreach (var pressed in new[] { true, false })
                {
                    box.GetViewport().PushInput(new InputEventMouseButton
                        { ButtonIndex = MouseButton.Left, Pressed = pressed, DoubleClick = doubleClick && pressed, Position = at, GlobalPosition = at }, true);
                    await Wait(menu, 0.05);
                }
            }

            await Typed("co-op, Favorites, wor");
            var drawn = TagPills.TestPills(box);
            Check("tag pills: finished tags are pills, left to right inside the box; the word being typed isn't one",
                Pills() == "co-op, Favorites" && drawn[0].Rect.End.X < drawn[1].Rect.Position.X && drawn.All(p => inBox.Encloses(p.Rect)),
                $"{Say()}: {string.Join(", ", drawn.Select(p => p.Rect))} in {inBox}");
            var ink = TagPills.TestInk(box);
            var (boxSize, pillSize) = (box.GetThemeFontSize("font_size"), box.GetThemeFontSize("font_size") - TagPills.Smaller);
            (string, Color, int)[] inks = [("co-op", Tags.CustomColor, pillSize), (", ", new Color(cream, 0), boxSize), ("Favorites", Tags.CustomColor, pillSize),
                (", ", new Color(cream, 0), boxSize), ("wor", cream, boxSize)];
            var ownInk = box.GetThemeColor("font_color");
            box.Select(7, 16);
            await Wait(menu, 0.05);
            var unselected = string.Concat(TagPills.TestInk(box).Select(r => r.Text));
            var selectedPill = TagPills.TestPills(box).ElementAtOrDefault(1).Rect;
            box.Deselect();
            Check("tag pills: tag text in the tag colour and smaller, the commas invisible (alpha 0), the word being typed cream at the box's size, "
                + "over the box's own text (clear while it's typed in); a selected tag is left to the box", ink.Count == inks.Length
                && ink.Zip(inks).All(r => r.First.Text == r.Second.Item1 && r.First.Color.IsEqualApprox(r.Second.Item2) && r.First.Size == r.Second.Item3)
                && ownInk.A == 0 && unselected == "co-op, , wor",
                $"{string.Join(" | ", ink.Select(r => $"\"{r.Text}\" {r.Color} {r.Size}px"))}; the box's own {ownInk}; with Favorites selected: \"{unselected}\"");
            // The box lays its caret and selection out at its own size, so the pill the caret is in, or a selection
            // touches, is drawn at that size, the others smaller.
            box.CaretColumn = 10; // in "Favorites"
            await Wait(menu, 0.05);
            var (openPills, openInk) = (TagPills.TestPills(box), TagPills.TestInk(box));
            Check("tag pills: a pill with the caret in it, or a selection touching it, is drawn at the box's size, the others smaller",
                openPills is [var openCoop, var openFavorites] && drawn is [var smallCoop, var smallFavorites] && openCoop.Rect == smallCoop.Rect
                && openFavorites.Rect.Size.X > smallFavorites.Rect.Size.X && openFavorites.Rect == selectedPill
                && openInk.Find(r => r.Text == "Favorites").Size == boxSize && openInk.Find(r => r.Text == "co-op").Size == pillSize,
                $"{string.Join(", ", openPills.Select(p => $"{p.Tag} {p.Rect}"))} (smaller {string.Join(", ", drawn.Select(p => p.Rect))}, selected {selectedPill}); "
                + string.Join(" | ", openInk.Select(r => $"\"{r.Text}\" {r.Size}px")));

            // Where the pills are drawn, by where the box puts the caret for a click: a click in a smaller pill's middle
            // lands inside its tag and opens the pill to the box's size; then 1 px into each end of its tag lands on that
            // end (more than about half a letter of drift would miss it), its middle inside it. In the box's columns (an
            // emoji is one, and two chars of the C# string).
            async Task<List<string>> Misses(bool ends = true)
            {
                var list = new List<string>();
                foreach (var (tag, smaller) in TagPills.TestPills(box)) // the caret at the end: in none of them
                {
                    var start = TagPills.Column(box.Text, box.Text.IndexOf(tag, StringComparison.Ordinal));
                    var end = start + TagPills.Columns(tag);
                    await ClickAt(smaller.GetCenter());
                    var inSmaller = box.CaretColumn;
                    var rect = TagPills.TestPills(box).FirstOrDefault(p => p.Tag == tag).Rect;
                    var middle = rect.GetCenter().Y;
                    var (left, right) = (start, end);
                    if (ends)
                    {
                        await ClickAt(new Vector2(rect.Position.X + TagPills.PadLeft + 1, middle));
                        left = box.CaretColumn;
                        await ClickAt(new Vector2(rect.End.X - TagPills.PadRight - 1, middle));
                        right = box.CaretColumn;
                    }
                    await ClickAt(rect.GetCenter());
                    if (inSmaller <= start || inSmaller >= end || rect.Size.X <= smaller.Size.X || left != start || right != end
                        || box.CaretColumn <= start || box.CaretColumn >= end)
                        list.Add($"{tag} ({start}..{end}) at {smaller}: middle {inSmaller}; opened at {rect}: {left}, {right}, middle {box.CaretColumn}");
                }
                return list;
            }
            await Typed("co-op, Favorites, Story-heavy, wor");
            var missed = await Misses();
            Check("tag pills: drawn where the box draws the text: a click at either end of a pill's tag puts the caret there, in its middle inside it",
                TagPills.TestPills(box).Count == 3 && missed.Count == 0, missed.Count > 0 ? string.Join("; ", missed) : Say());
            if (TagPills.TestPills(box) is [_, (_, var favorites), ..])
            {
                await ClickAt(favorites.GetCenter());
                await Capture(screen, outDir, "19c-tag-pills-caret"); // the caret between two of our letters
            }
            if (TagPills.TestPills(box) is [.., (_, var storyHeavy)])
                await ClickAt(new Vector2(storyHeavy.Position.X + storyHeavy.Size.X / 4, storyHeavy.GetCenter().Y), doubleClick: true); // on "Story"
            Check("tag pills: a double-click selects the whole tag", box.GetSelectedText() == "Story-heavy", $"\"{box.GetSelectedText()}\" selected");
            await WaitReal(menu, 0.7); // not a triple-click with the next
            box.Deselect();

            // An emoji is one column of the box but two chars of the C# string: after one, the pills are still where the
            // box draws the text, and the keys, a double-click and a picked suggestion take the right tag. A right-to-left
            // tag first: the box lays the line out right to left (its text direction, Auto), the pills with it (a click
            // at the edge of a right-to-left run can land either side of it, so only the middles there).
            await Typed("🎮 games, co-op, Favorites, wor");
            var missedAfterEmoji = await Misses();
            Check("tag pills: after an emoji, still drawn where the box draws the text: a click at either end of a pill's tag puts the caret there, in its middle inside it",
                TagPills.TestPills(box).Count == 3 && missedAfterEmoji.Count == 0, missedAfterEmoji.Count > 0 ? string.Join("; ", missedAfterEmoji) : Say());
            await Typed("מועדפים, co-op, Favorites, wor");
            var missedRtl = await Misses(ends: false);
            Check("tag pills: a right-to-left tag first: the pills go where the box lays the line out right to left (a click in a pill's middle lands inside its tag)",
                TagPills.TestPills(box).Count == 3 && missedRtl.Count == 0, missedRtl.Count > 0 ? string.Join("; ", missedRtl) : Say());
            await Typed("🎮 games, B, C, ");
            if (TagPills.TestPills(box) is [(_, var games), ..])
                await ClickAt(new Vector2(games.Position.X + games.Size.X * 3 / 4, games.GetCenter().Y), doubleClick: true); // on "games"
            Check("tag pills: after an emoji, a double-click selects the whole tag, the emoji with it", box.GetSelectedText() == "🎮 games",
                $"\"{box.GetSelectedText()}\" selected; {Say()}");
            await WaitReal(menu, 0.7); // not a triple-click with the next
            box.Deselect();
            await Typed("🎮, A, B, ");
            await Tap(Key.Backspace);
            Check("tag pills: after an emoji, a fresh Backspace at the end takes the last pill and its separator", (box.Text, box.CaretColumn) == ("🎮, A, ", 6), Say());
            await Typed("🎮 games, B, C, ", caret: 12); // just before "C"
            await Tap(Key.Backspace);
            Check("tag pills: after an emoji, a Backspace just before a pill takes the one before it", (box.Text, box.CaretColumn) == ("🎮 games, C, ", 9), Say());
            await Typed("🎮, A, B, ", caret: 3); // at the start of "A"
            await Tap(Key.Delete);
            Check("tag pills: after an emoji, a Delete at a pill's start takes that pill and its separator", (box.Text, box.CaretColumn) == ("🎮, B, ", 3), Say());
            await Typed("🎮, A, B, ");
            await Tap(Key.A, ctrl: true);
            var all = box.GetSelectedText();
            await Tap(Key.X, ctrl: true);
            Check("tag pills: after an emoji, Ctrl+A then Ctrl+X cuts every tag", box.Text == "" && TagPills.TestClipboard == "🎮, A, B",
                $"\"{all}\" selected, then {Say()}, clipboard \"{TagPills.TestClipboard}\"");
            await Typed("🎮, zebr");
            await Tap(Key.Down);
            var litAfterEmoji = TagSuggestions.Highlighted;
            await Tap(Key.Enter);
            Check("tag pills: after an emoji, a picked suggestion takes the place of the word being typed", box.Text == "🎮, Zebra-stripes, ",
                $"lit {litAfterEmoji}: {Say()}");

            await Typed("co-op, Favorites, wor");
            await Tap(Key.Comma, unicode: ',');
            var comma = (box.Text, Pills());
            var gaps = TagPills.TestPills(box).Zip(TagPills.TestPills(box).Skip(1), (a, b) => b.Rect.Position.X - a.Rect.End.X).ToList();
            await Typed("co-op, Fast");
            await Tap(Key.Comma, unicode: '，');
            Check("tag pills: a typed comma, or a full-width one, makes a pill: \", \" after its tag, a gap before the next",
                comma == ("co-op, Favorites, wor, ", "co-op, Favorites, wor") && gaps.Count == 2 && gaps.All(g => g >= 2) && box.Text == "co-op, Fast, ",
                $"{comma}, gaps {string.Join(", ", gaps.Select(g => $"{g:0.0}"))}; then \"{box.Text}\"");

            // Backspace: held from the end of the word being typed, it takes the word and stops at the pill; a fresh press
            // there takes the pill and its ", " (one undo step), and holding on stops at the next.
            await Typed("co-op, Favorites, wor");
            await Send(Key.Backspace, true);
            await Repeats(Key.Backspace, 6);
            var held = (box.Text, box.CaretColumn);
            await Send(Key.Backspace, false);
            await Send(Key.Backspace, true);
            var fresh = box.Text;
            await Repeats(Key.Backspace, 3);
            var heldOn = box.Text;
            await Send(Key.Backspace, false);
            await Tap(Key.Z, ctrl: true);
            Check("tag pills: a held Backspace stops at the first pill (the word being typed goes, the pill stays)", held == ("co-op, Favorites, ", 18), $"{held}");
            Check("tag pills: a fresh Backspace there takes the whole pill and its separator, and one Ctrl+Z puts it back",
                fresh == "co-op, " && box.Text == "co-op, Favorites, ", $"\"{fresh}\", then \"{box.Text}\"");
            Check("tag pills: Backspace held on after that press stops at the next pill", heldOn == "co-op, ", heldOn);
            // Delete, its mirror: held from inside a tag, it takes the letters after the caret and stops at the separator
            // before the next pill; a fresh press there takes that separator and pill, and holding on stops at the next.
            await Typed("co-op, Favorites, Story-heavy, Fast, wor", caret: 10); // after "Fav"
            await Send(Key.Delete, true);
            await Repeats(Key.Delete, 8);
            var heldDelete = (box.Text, box.CaretColumn);
            await Send(Key.Delete, false);
            await Send(Key.Delete, true);
            var freshDelete = (box.Text, box.CaretColumn);
            await Repeats(Key.Delete, 3);
            var heldOnDelete = box.Text;
            await Send(Key.Delete, false);
            await Tap(Key.Z, ctrl: true);
            Check("tag pills: a held Delete stops at the first pill (the tag's letters after the caret go, the next pill stays)",
                heldDelete == ("co-op, Fav, Story-heavy, Fast, wor", 10), $"{heldDelete}");
            Check("tag pills: a fresh Delete there takes the whole pill and its separator, and one Ctrl+Z puts it back",
                freshDelete == ("co-op, Fav, Fast, wor", 10) && box.Text == "co-op, Fav, Story-heavy, Fast, wor", $"{freshDelete}, then \"{box.Text}\"");
            Check("tag pills: Delete held on after that press stops at the next pill", heldOnDelete == "co-op, Fav, Fast, wor", heldOnDelete);
            // At a pill's own edge (Jordan, 21 Oct): Delete at its start, or Backspace at its end, takes it whole with the
            // separator after it, one undo step; held there, it stops (nothing goes).
            await Typed("co-op, Favorites, wor", caret: 7); // at the start of "Favorites"
            await Send(Key.Delete, true);
            var deleteAtStart = (box.Text, box.CaretColumn);
            await Send(Key.Delete, false);
            await Tap(Key.Z, ctrl: true);
            var deleteUndone = box.Text;
            await Typed("co-op, Favorites, wor", caret: 7);
            await Repeats(Key.Delete, 3);
            var deleteHeld = box.Text;
            await Send(Key.Delete, false);
            Check("tag pills: a fresh Delete at a pill's start takes the whole pill and its separator (Ctrl+Z puts it back); held there, nothing goes",
                deleteAtStart == ("co-op, wor", 7) && deleteUndone == "co-op, Favorites, wor" && deleteHeld == "co-op, Favorites, wor",
                $"{deleteAtStart}, undone \"{deleteUndone}\", held \"{deleteHeld}\"");
            await Typed("co-op, Favorites, wor", caret: 16); // at the end of "Favorites"
            await Send(Key.Backspace, true);
            var backAtEnd = (box.Text, box.CaretColumn);
            await Send(Key.Backspace, false);
            await Typed("co-op, Favorites, wor", caret: 16);
            await Repeats(Key.Backspace, 3);
            var backHeld = box.Text;
            await Send(Key.Backspace, false);
            Check("tag pills: a fresh Backspace at a pill's end takes the whole pill and its separator; held there, nothing goes",
                backAtEnd == ("co-op, wor", 7) && backHeld == "co-op, Favorites, wor", $"{backAtEnd}, held \"{backHeld}\"");

            await Typed("co-op, Favorites, ");
            await Tap(Key.Backspace, ctrl: true);
            var wordBack = box.Text;
            await Typed("co-op, Favorites, wor", caret: 5);
            await Tap(Key.Delete, ctrl: true);
            Check("tag pills: Ctrl+Backspace and Ctrl+Delete next to a pill take it whole too", wordBack == "co-op, " && box.Text == "co-op, wor",
                $"\"{wordBack}\", \"{box.Text}\"");

            // Copy, Cut and Paste of whole tags (part of a tag is the box's own).
            await Typed("co-op, Favorites, wor");
            box.Select(0, 5);
            await Tap(Key.C, ctrl: true);
            var one = TagPills.TestClipboard;
            box.Select(0, 16);
            await Tap(Key.C, ctrl: true);
            var two = TagPills.TestClipboard;
            await Typed("co-op, Favorites, ");
            box.Select(7, 16);
            await Tap(Key.C, ctrl: true);
            Check("tag pills: Ctrl+C on whole tags copies them with a comma after when another value follows, the last one without; "
                + "the box's own drag and right-click menu are off (their copies wouldn't keep to that)",
                one == "co-op," && two == "co-op, Favorites," && TagPills.TestClipboard == "Favorites" && !box.DragAndDropSelectionEnabled && !box.ContextMenuEnabled,
                $"\"{one}\", \"{two}\", \"{TagPills.TestClipboard}\"; drag {box.DragAndDropSelectionEnabled}, menu {box.ContextMenuEnabled}");
            await Typed("co-op, Favorites, wor");
            box.Select(7, 16);
            await Tap(Key.X, ctrl: true);
            var cut = (box.Text, TagPills.TestClipboard);
            await Tap(Key.Z, ctrl: true);
            Check("tag pills: Ctrl+X takes the pill and its separator; Ctrl+Z puts it back", cut == ("co-op, wor", "Favorites,") && box.Text == "co-op, Favorites, wor",
                $"{cut}, then \"{box.Text}\"");
            TagPills.TestClipboard = "x, y\nz";
            await Typed("");
            await Tap(Key.V, ctrl: true);
            var pasted = (box.Text, Pills());
            TagPills.TestClipboard = "Co-op, Fast";
            await Typed("co-op, ");
            await Tap(Key.V, ctrl: true);
            var skipped = box.Text;
            await Tap(Key.Z, ctrl: true);
            var unpasted = box.Text;
            var full = string.Join(", ", Enumerable.Range(1, 23).Select(i => $"tag{i:00}")) + ", "; // 161 of the box's 200
            TagPills.TestClipboard = "longer-tag-one, longer-tag-two, longer-tag-three";
            await Typed(full);
            await Tap(Key.V, ctrl: true);
            Check("tag pills: pasting a list makes pills: the tags the box hasn't got, as many whole ones as fit, one undo step",
                pasted == ("x, y, z, ", "x, y, z") && skipped == "co-op, Fast, " && unpasted == "co-op, " && box.Text == full + "longer-tag-one, longer-tag-two, ",
                $"{pasted}, \"{skipped}\", \"{unpasted}\", then {box.Text.Length} characters: ...{box.Text[Math.Max(0, box.Text.Length - 40)..]}");
            TagPills.TestClipboard = "Roguelike\vDeckbuilder, Co-op"; // \v: Word's line break (Shift+Enter)
            await Typed("");
            await Tap(Key.V, ctrl: true);
            Check("tag pills: a pasted list drops the characters below a space, as the box's own paste does", box.Text == "RoguelikeDeckbuilder, Co-op, ",
                Say().Replace("\v", "\\v"));
            TagPills.TestClipboard = "Co-op, Fast";
            await Typed("Co-op, Fast, Story");
            box.Select(13, 18); // "Story"
            await Tap(Key.V, ctrl: true);
            var overSelection = (box.Text, box.HasSelection());
            await Tap(Key.Z, ctrl: true);
            Check("tag pills: a paste over a selection with nothing new to add still takes the selection, one undo step",
                overSelection == ("Co-op, Fast, ", false) && box.Text == "Co-op, Fast, Story", $"{overSelection}, then \"{box.Text}\"");
            // After an emoji (two chars, one column), Paste splits the text and Copy reads the selection in the right place.
            TagPills.TestClipboard = "x, y";
            await Typed("🎮, A, ");
            await Tap(Key.V, ctrl: true);
            var pastedAfter = box.Text;
            await Typed("🎮, A, B");
            box.Select(3, 4); // "A"
            await Tap(Key.C, ctrl: true);
            Check("tag pills: after an emoji, a paste lands at the caret and Ctrl+C on a whole tag copies it with its comma",
                pastedAfter == "🎮, A, x, y, " && TagPills.TestClipboard == "A,", $"\"{pastedAfter}\", copied \"{TagPills.TestClipboard}\"");
            // A comma typed over a selected separator changes nothing, so it takes no undo step either.
            await Typed("A, B, wordx");
            await Tap(Key.Backspace); // the box's own: the word's last letter
            await Tap(Key.Z, ctrl: true);
            box.Select(1, 3); // ", "
            var sameAnnounced = 0;
            void SameAnnounced(string _) => sameAnnounced++;
            box.TextChanged += SameAnnounced;
            await Tap(Key.Comma, unicode: ',');
            box.TextChanged -= SameAnnounced;
            var same = box.Text;
            await Tap(Key.Y, ctrl: true);
            Check("tag pills: a comma typed over a selected separator changes nothing and takes no undo step: Ctrl+Y still redoes",
                same == "A, B, wordx" && sameAnnounced == 0 && box.Text == "A, B, word", $"\"{same}\", text_changed {sameAnnounced} times, then \"{box.Text}\"");
            // A comma the full box has no room for changes nothing, so it takes no undo step (one would clear the redo).
            var full200 = string.Join(", ", Enumerable.Range(1, 28).Select(i => $"tag{i:00}")) + ", word"; // the box's 200
            await Typed(full200);
            await Tap(Key.Backspace); // the box's own: the word's last letter
            await Tap(Key.Z, ctrl: true);
            var announced = 0;
            void Announced(string _) => announced++;
            box.TextChanged += Announced;
            await Tap(Key.Comma, unicode: ',');
            box.TextChanged -= Announced;
            var unchanged = box.Text == full200;
            await Tap(Key.Y, ctrl: true);
            Check("tag pills: a comma the full box has no room for changes nothing and takes no undo step: Ctrl+Y still redoes",
                full200.Length == 200 && unchanged && announced == 0 && box.Text == full200[..^1],
                $"{full200.Length} characters, unchanged {unchanged}, text_changed {announced} times, then ...{box.Text[Math.Max(0, box.Text.Length - 12)..]}");

            // A picked suggestion is one undo step (setting the text would have wiped the box's undo).
            await Typed("zebr");
            await Tap(Key.Down);
            var lit = TagSuggestions.Highlighted;
            await Tap(Key.Enter);
            var picked = box.Text;
            await Tap(Key.Z, ctrl: true);
            var unpicked = box.Text;
            await Tap(Key.Y, ctrl: true);
            Check("tag pills: Ctrl+Z takes back a picked suggestion, Ctrl+Y brings it back", lit == "Zebra-stripes" && picked == "Zebra-stripes, "
                && unpicked == "zebr" && box.Text == "Zebra-stripes, " && box.HasFocus(), $"lit {lit}: \"{picked}\", \"{unpicked}\", \"{box.Text}\"");

            // A long list: scrolled to the caret at its end, the first pills out of sight; Home brings them back.
            await Typed(string.Join(", ", Enumerable.Range(1, 20).Select(i => $"tag-{i:00}")) + ", wor");
            var atEnd = TagPills.TestPills(box);
            var scrolled = box.GetScrollOffset();
            await Capture(screen, outDir, "19d-tag-pills-scrolled");
            await Tap(Key.Home);
            var atStart = TagPills.TestPills(box);
            Check("tag pills: a long list scrolls, its pills with it, none drawn outside the box", scrolled < 0 && box.GetScrollOffset() == 0
                && atEnd.LastOrDefault().Tag == "tag-20" && atEnd.All(p => p.Tag != "tag-01") && atStart.FirstOrDefault().Tag == "tag-01" && atStart.All(p => p.Tag != "tag-20")
                && atEnd.Concat(atStart).All(p => inBox.Encloses(p.Rect)),
                $"scrolled {scrolled}: {string.Join(", ", atEnd.Select(p => $"{p.Tag} {p.Rect}"))}; at the start: {string.Join(", ", atStart.Select(p => $"{p.Tag} {p.Rect}"))} in {inBox}");

            await Typed("co-op, Favorites, , co-op, wor");
            await Tap(Key.Comma, unicode: ',');
            box.ReleaseFocus();
            await Wait(menu, 0.3);
            Check("tag pills: leaving the box still saves the tags tidied, its own text cream again", Store.InfoFor("SmmTestUi")?.Tags?.SequenceEqual(["co-op", "Favorites", "wor"]) == true
                && box.Text == "co-op, Favorites, wor" && box.GetThemeColor("font_color").IsEqualApprox(cream),
                $"\"{box.Text}\": {string.Join(", ", Store.InfoFor("SmmTestUi")?.Tags ?? [])}; {box.GetThemeColor("font_color")}");
        }
        finally
        {
            TagPills.TestClipboard = null;
            Store.RestoreEdits(before);
            CategoryList.Apply(content);
            ModDetails.Refresh(screen);
        }
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
        await Wait(menu, 0.1); // well under Hover.Delay: at once
        Check("hovering Changes shows the details at once", screen.HasNode("SpireModManagerChangesPanel"));
        var summary = screen.GetNodeOrNull("SpireModManagerChangesPanel")?.FindChildren("*", "RichTextLabel", true, false).OfType<RichTextLabel>().FirstOrDefault()?.GetParsedText() ?? "";
        Check("the summary is titled and names the mod turned off", summary.StartsWith("Changes since launch") && summary.Contains("Test UI"), summary);
        // Its own place, kept (Hover turns only the cards it places itself): just above the lines, right edges together.
        var summaryBox = screen.GetNodeOrNull("SpireModManagerChangesPanel")?.FindChildren("*", "PanelContainer", true, false).OfType<Control>().FirstOrDefault()?.GetGlobalRect();
        var changesAt = changes.GetGlobalRect();
        Check("the summary keeps its own place: just above the lines, right edges together", summaryBox is { } summaryAt
            && Math.Abs(summaryAt.End.Y - (changesAt.Position.Y - 8)) < 0.5f && Math.Abs(summaryAt.End.X - changesAt.End.X) < 0.5f, $"{summaryBox} over {changesAt}");
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
            Check("the selected row: an even gold rim, rounded, a little wider than the row",
                shownLook.GetThemeStylebox("panel") is StyleBoxFlat { CornerRadiusTopLeft: RowTitles.CornerRadius } shownBox && EvenGoldRim(shownBox)
                && shownLook.Modulate == Colors.White && shownLook.OffsetLeft == RowTitles.LookInset.X);
        await Capture(screen, outDir, "14c-opened-from-changes");
        back?.EmitSignal(BaseButton.SignalName.Pressed);
        await Wait(menu, 0.2);
        Check("Back returns to the list", HistoryBar.ListOpen(info) && back is { Visible: false });
        HistoryBar.OpenFromList(screen, "SmmTestUi");
        await Wait(menu, 0.2);
        if (content.GetChildren().OfType<NModMenuRow>().FirstOrDefault(r => r.Mod?.manifest?.id is { } id && id != "SmmTestUi") is { } another)
        {
            CategoryList.Select(screen, another);
            await PanelFor(menu, screen, another);
        }
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
        await TestTwinOrder(menu, screen, content); // on the page just entered: its Revert all touches only that test's moves

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
            // Saved since, too: a new save time, the same profile (its id; #97: it read as added and deleted).
            var savedAt = renamed.SavedAt;
            renamed.SavedAt = DateTime.Now.AddMinutes(1);
            lines = HistoryBar.Describe();
            Check("renamed and saved since launch: still one renamed line", lines.Contains($"Renamed profile {was} → Renamed in test")
                && !lines.Contains("Added profile Renamed in test") && !lines.Contains($"Deleted profile {was}"), string.Join(" | ", lines));
            renamed.SavedAt = savedAt;
            Profiles.Rename(renamed, was);
            Check("renamed back, no renamed line", !HistoryBar.Describe().Any(l => l.Contains("Renamed in test")), string.Join(" | ", HistoryBar.Describe()));
        }
    }

    // The fixture twins (one id and source) in Load Order: which copy takes which of their entries is part of the order as
    // undo knows it. Side by side, a drop of the second above the first saves the same list: one undo step, and no move
    // for Changes (the game reads the same list; which copy loads is the pick), no "was" line, nothing pending; undo and
    // redo swap them back and again. Split by another mod, a drop of the second above the first changes the list and the
    // copies' turn at once: undo puts both back, so the rows, Changes and pending are as before the drop, and Revert all
    // goes back to the page's opening order the same way. (The copies' turn once stayed behind: the rows came back
    // swapped, and Changes, the pending line and the main menu's dot showed moves a relaunch wouldn't make.) Run on a page
    // just entered, so Revert all touches only these moves; back on the main list at the end.
    static async Task TestTwinOrder(Node menu, NModdingScreen screen, Control content)
    {
        List<NModMenuRow> Rows() => content.GetChildren().OfType<NModMenuRow>().ToList();
        List<Mod> Order() => Rows().Select(r => r.Mod).OfType<Mod>().ToList();
        static string Short(Mod m) => m.manifest?.id == TwinId ? Copies.CopyOf(m) : m.manifest?.id ?? "";
        static string Named(Mod m) => (m.manifest?.name ?? "") + Copies.Which(m);
        string Moves() => string.Join(", ", HistoryBar.Moves().Select(m => $"{Short(m.Mod)} {m.Before}→{m.After}"));
        string Was() => string.Join(",", Rows().Select(r => r.GetNodeOrNull<Label>("SpireModManagerWas")?.Text ?? ""));
        var line = screen.GetNode<Control>("%PendingChangesLabel");
        int Steps() => History.Steps(History.Track.Order);
        string Say() => $"rows {string.Join(", ", Order().Select(Short))}; moves [{Moves()}]; order changed {LoadOrder.OrderChanged}, pending {ModEntry.HasPendingChanges()} "
            + $"(line {line.Visible}); order steps {Steps()}; at the page's opening state {History.AtEntered}";
        var toggle = screen.GetNode<NButton>("SpireModManagerLoadOrderToggle");
        Click(toggle);
        await Wait(menu, 0.5);
        var twins = Order().Where(m => m.manifest?.id == TwinId && m.modSource == ModSource.ModsDirectory).ToList();
        if (twins.Count != 2 || Order().FirstOrDefault(m => m.manifest?.id == "SmmTestFixes") is not { } fixes)
        {
            if (twins.Count != 2)
                TwinsMissing("two copies' turn in Load Order");
            else
                Check("two copies' turn in Load Order: Test Fixes to move between them", false);
            Click(toggle);
            await Wait(menu, 0.5);
            return;
        }
        var (first, second) = (twins[0], twins[1]);
        var (start, list, steps, was) = (Order(), History.CurrentMods(), Steps(), Was());
        var (pending, changed) = (ModEntry.HasPendingChanges(), HistoryBar.ChangedMods().Overlaps(twins));
        bool NothingMoved(List<Mod> rows) => Order().SequenceEqual(rows) && Moves() == "" && !LoadOrder.OrderChanged && ModEntry.HasPendingChanges() == pending
            && line.Visible == pending && Was() == was && HistoryBar.ChangedMods().Overlaps(twins) == changed
            && !HistoryBar.Describe().Any(l => l.StartsWith("Moved: ") && twins.Any(t => l.Contains(Named(t))));

        // Side by side: the second dropped above the first.
        var sideBySide = Order().IndexOf(second) == Order().IndexOf(first) + 1 && NothingMoved(start);
        var startSaid = Say();
        LoadOrder.Move(second, Order().IndexOf(first), Rows());
        CategoryList.Redraw(content);
        await Wait(menu, 0.3);
        var swapped = Order();
        Check("two copies side by side in Load Order: a drop of the second above the first swaps their rows in one undo step, the saved list the same; no move "
            + "for Changes or Show: Changed, no \"was\" line, nothing pending (a relaunch loads the same)",
            sideBySide && swapped.IndexOf(second) == swapped.IndexOf(first) - 1 && History.CurrentMods().SequenceEqual(list) && Steps() == steps + 1
            && !History.AtEntered && NothingMoved(swapped), $"at the start: {startSaid}; now {Say()}");
        History.Undo();
        await Wait(menu, 0.3);
        var undone = NothingMoved(start) && Steps() == steps && History.AtEntered;
        var undoneSaid = Say();
        History.Redo();
        await Wait(menu, 0.3);
        var redone = NothingMoved(swapped);
        History.Undo();
        await Wait(menu, 0.3);
        Check("... undo swaps them back, redo again, undo back (the copies' turn is in the step)", undone && redone && NothingMoved(start) && History.AtEntered,
            $"undone {undone} ({undoneSaid}), redone {redone}; now {Say()}");

        // Split by Test Fixes, then the second dropped above the first: the list and the copies' turn in one step.
        var rest = LoadOrder.Mods(Rows());
        rest.Remove(fixes);
        LoadOrder.Move(fixes, rest.IndexOf(second), Rows());
        CategoryList.Redraw(content);
        await Wait(menu, 0.3);
        var split = Order();
        var (splitMoves, splitWas, splitPending, splitSteps, splitSaid) = (Moves(), Was(), ModEntry.HasPendingChanges(), Steps(), Say());
        LoadOrder.Move(second, Order().IndexOf(first), Rows());
        CategoryList.Redraw(content);
        await Wait(menu, 0.3);
        var dropped = Order();
        var droppedSaid = Say();
        History.Undo();
        await Wait(menu, 0.3);
        Check("two copies split by another mod: a drop of the second above the first is one step; undo puts the rows back, and Changes, the \"was\" lines "
            + "and pending as before the drop (no move a relaunch wouldn't make)",
            split.IndexOf(fixes) == split.IndexOf(first) + 1 && split.IndexOf(second) == split.IndexOf(fixes) + 1
            && dropped.IndexOf(second) == dropped.IndexOf(first) - 1 && Order().SequenceEqual(split) && Moves() == splitMoves && Was() == splitWas
            && ModEntry.HasPendingChanges() == splitPending && line.Visible == splitPending && Steps() == splitSteps,
            $"split: {splitSaid}; dropped: {droppedSaid}; undone: {Say()}");
        History.Redo();
        await Wait(menu, 0.3);
        var redoneDrop = Order().SequenceEqual(dropped);
        History.RevertMods();
        await Wait(menu, 0.3);
        Check("... redo, then Revert all: the page's opening order back, the copies' turn too; no move, \"was\" line or pending relaunch left, the twins "
            + "not under Show: Changed", redoneDrop && NothingMoved(start) && History.AtEntered, $"redone {redoneDrop}; reverted: {Say()}");
        Click(toggle);
        await Wait(menu, 0.5);
    }

    // Missing game resources: everything this run used loaded (bar the self-test's own made-up paths, under
    // res://SpireModManager/selftest/); a path that isn't there gives null without throwing
    // (noted, and logged, once); a missing font falls back to Godot's own; AddButton's plain stand-in is a working
    // button with the label, at the asked size, with the hidden "Dot" callers show; the category header made in
    // code has the parts a redraw reads.
    static void TestFallbacks(NModdingScreen screen)
    {
        var missed = ModEntry.MissingResources.Where(p => !p.StartsWith("res://SpireModManager/selftest/")).ToList();
        Check("every game resource used so far loaded", missed.Count == 0, string.Join(", ", missed));
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
        // That fresh start takes Better Mod Menu's data instead when it has some, as a first run does (#100); not
        // after a backup was loaded, nor read-only, nor when it has none. The notice says where the data came from.
        var fromBmm = new StoreData { Categories = [new Category { Name = "From BMM" }] };
        var imported = Store.WithImport(started, () => fromBmm);
        Check("no backup, but Better Mod Menu's data: imported again, and the notice says so", imported is { Imported: true } && imported.Data == fromBmm
            && Store.NoticeText(imported)?.GetFormattedText().Contains("imported again from Better Mod Menu") == true
            && Store.WithImport(recovered, () => fromBmm).Data != fromBmm && Store.WithImport(started with { ReadOnly = true }, () => fromBmm).Data != fromBmm
            && Store.WithImport(started, () => null) == started, Store.NoticeText(imported)?.GetFormattedText() ?? "no notice");

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
        // copies_off by hand: nulls, keys naming no copy, ours, repeats and the pick (which always counts as on) dropped,
        // sorted; copies not installed kept (as picks are never pruned); the file untouched. Empty stays empty (none off),
        // and none stays none (a file from before it: only the pick on).
        const string offs = "{\"copy_picks\":[\"SmmX|l:B\"],\"copies_off\":[null,\"bad\",\"SmmX|l:C\",\"SmmX|l:A\",\"SmmX|l:A\",\"SmmX|l:B\",\"SpireModManager|l:Other\"]}";
        var offsDir = Folder("copies-off", offs);
        var offsRead = Store.LoadFrom(offsDir, now);
        var (emptyOff, noOff) = (Store.LoadFrom(Folder("copies-off-empty", "{\"copies_off\":[]}"), now), Store.LoadFrom(Folder("copies-off-none", "{}"), now));
        Check("copies_off in a hand-edited file: nulls, keys naming no copy, ours, repeats and the pick dropped, sorted, the file untouched; empty stays empty, "
            + "none stays none", offsRead.Data?.CopiesOff is ["SmmX|l:A", "SmmX|l:C"] && File.ReadAllText(Manager(offsDir)) == offs
            && emptyOff.Data?.CopiesOff is [] && noOff.Data is { CopiesOff: null },
            $"{string.Join(", ", offsRead.Data?.CopiesOff ?? [])}; empty {emptyOff.Data?.CopiesOff?.Count}, none {noOff.Data?.CopiesOff?.Count}");
        // The copies record by hand: an id's null list (Copies.Key would throw on every row) or one left empty goes, nulls
        // and repeats in one go, the holder still first.
        var record = Store.LoadFrom(Folder("copies-null", "{\"copies\":{\"X\":null,\"Y\":[\"l:B\",null,\"l:A\",\"l:B\"],\"Z\":[null]}}"), now).Data?.Copies;
        Check("the copies record in a hand-edited file: a null or emptied list dropped, nulls and repeats in one dropped, the holder first",
            record is { } kept && kept.Keys.SequenceEqual(["Y"]) && kept["Y"] is ["l:B", "l:A"], string.Join("; ", record?.Select(p => $"{p.Key}: {p.Value?.Count}") ?? []));
        Check("no file and nothing set aside: a first run", Store.LoadFrom(Folder("first", null), now).Data == null
            && Store.LoadFrom(Path.Combine(root, "no-folder"), now).Data == null);

        // Saving: the old file goes to backups/ first, the newest ten kept; a backup that fails (here a file sits
        // where the backups folder goes) doesn't stop the save.
        var saving = Folder("saving", Good("Old"), Enumerable.Range(1, 11).Select(i => ($"manager.20200101-0000{i:00}.json", Good($"Old {i}"))).ToArray());
        Store.WriteTo(saving, new StoreData { Categories = [new Category { Name = "New" }] }, backUp: true);
        var copies = Directory.GetFiles(Path.Combine(saving, "backups")).Order().ToList();
        Check("a save backs up the old file first, named in UTC (#99), the newest ten kept", Category(Store.LoadFrom(saving, now)) == "New" && copies.Count == 10
            && File.ReadAllText(copies[^1]) == Good("Old") && copies[^1].EndsWith("Z.json") && !copies.Any(f => f.EndsWith("-000001.json") || f.EndsWith("-000002.json")),
            string.Join(", ", copies.Select(Path.GetFileName)));
        var blocked = Path.Combine(root, "blocked");
        Directory.CreateDirectory(blocked);
        File.WriteAllText(Manager(blocked), Good("Old"));
        File.WriteAllText(Path.Combine(blocked, "backups"), "a file where the backups folder goes");
        Store.WriteTo(blocked, new StoreData { Categories = [new Category { Name = "New" }] }, backUp: true);
        Check("a backup that fails doesn't stop the save", Category(Store.LoadFrom(blocked, now)) == "New");
        // Backups by when they were made, the old local-time names and the new UTC ones together (#99): in a timezone
        // ahead of UTC a local name reads later than a UTC one made after it.
        var made = DateTime.UtcNow;
        string Local(DateTime utc) => $"manager.{utc.ToLocalTime():yyyyMMdd-HHmmss}.json";
        string Utc(DateTime utc) => $"manager.{utc:yyyyMMdd-HHmmss}Z.json";
        var order = Store.NewestFirst([Local(made.AddHours(-2)), Utc(made.AddHours(-1)), Local(made.AddHours(-3)), Utc(made)]).ToList();
        Check("backups newest first, local-time and UTC names together", order.SequenceEqual([Utc(made), Utc(made.AddHours(-1)), Local(made.AddHours(-2)), Local(made.AddHours(-3))]),
            string.Join(", ", order));

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
            && text.Contains("Error reading your saved data") && text.Contains("October 11, 2026, 22:15") && text.Contains(Path.GetFileName(setAside)) && Store.Notice == null, text);
        // Its "mod's data folder" opens the data folder.
        var openedFolder = "";
        Store.TestOpenFolder = folder => openedFolder = folder;
        (popup?.FindChild("Description", true, false) as RichTextLabel)?.EmitSignal(RichTextLabel.SignalName.MetaClicked, "folder");
        Store.TestOpenFolder = null;
        Check("the notice's \"mod's data folder\" opens the mod's data folder", openedFolder == Store.Dir
            && popup?.FindChild("Description", true, false) is RichTextLabel { MouseFilter: Control.MouseFilterEnum.Stop }, openedFolder);
        await Capture(popup ?? screen, outDir, "19-data-notice");
        NModalContainer.Instance?.Clear();
        screen.Visible = false;
        screen.Visible = true;
        await Wait(menu, 0.3);
        Check("the notice shows once", NModalContainer.Instance?.OpenModal == null);
        // The longest of the three, the read-only one, as the game's popup shows it (for a look: does it fit?).
        if (Store.ShowNotice(recovered with { ReadOnly = true }))
        {
            await Wait(menu, 0.4);
            await Capture(NModalContainer.Instance?.OpenModal as Node ?? screen, outDir, "19b-data-notice-read-only");
            CheckMark("the notice about our unreadable data file (not the game's save error)", NModalContainer.Instance?.OpenModal as Control);
            NModalContainer.Instance?.Clear();
            await Wait(menu, 0.3);
        }
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
    // buttons, however wide the font. ModEntry.Font looked its fonts up again after the switch (the language's own), and
    // then gives the same one each time it's asked, without looking again. Back in the starting language, its fonts again
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
            var lookups = ModEntry.FontLookups;
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
            const string regular = "res://themes/kreon_regular_shared.tres";
            var font = ModEntry.Font(regular);
            var looked = ModEntry.FontLookups;
            var again = ModEntry.Font(regular);
            Check($"in {language}: our fonts looked up again after the switch (the language's own), then kept: the same font each time it's asked",
                looked > lookups && font.ResourcePath == GameFont(language, FontType.Regular, false) && ReferenceEquals(again, font) && ModEntry.FontLookups == looked,
                $"{looked - lookups} lookups since the switch, {font.ResourcePath}; asked again: {(ReferenceEquals(again, font) ? "the same" : again.ResourcePath)}, "
                + $"{ModEntry.FontLookups - looked} more lookups");
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
        Click(screen.GetNode<BaseButton>("SpireModManagerViewToggle"));
        await Wait(menu, 0.5);
        var ok = check();
        Click(screen.GetNode<BaseButton>("SpireModManagerViewToggle"));
        await Wait(menu, 0.5);
        return ok;
    }

    static Node? Header(Control content, string category) => content.GetNodeOrNull($"Category {category}");

    // Every text in the popup open now (the game's modal), for checking its wording.
    // The card a [url] link in a text brings up (Hover.WatchLinks), hovered with the mouse on the text, then let go: its
    // words, or "" for none.
    static async Task<string> LinkCard(RichTextLabel text, string link)
    {
        Hover.TestMouse = () => text.GetGlobalRect().GetCenter();
        try
        {
            text.EmitSignal(RichTextLabel.SignalName.MetaHoverStarted, link);
            await WaitReal(text, Hover.Delay + 0.3); // the link card's delay
            var card = text.GetTree().Root.FindChild(LaunchGuard.MoreCardName, true, false)?.FindChildren("*", "RichTextLabel", true, false)
                .OfType<RichTextLabel>().FirstOrDefault()?.GetParsedText() ?? "";
            text.EmitSignal(RichTextLabel.SignalName.MetaHoverEnded, link);
            return card;
        }
        finally
        {
            Hover.TestMouse = null;
            Hover.Hide();
        }
    }

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

    // A link's press (the page link at the top).
    static void Click(BaseButton button) => button.EmitSignal(BaseButton.SignalName.Pressed);

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

    // The colour the control's external-link mark is drawn in at a fresh redraw (LinkMark.TestDrawn): null where none
    // is drawn.
    static async Task<Color?> MarkDrawn(Node node, Control control)
    {
        LinkMark.TestForget(control);
        control.QueueRedraw();
        await Wait(node, 0.1);
        return LinkMark.TestDrawn(control);
    }

    // A point given as GetGlobalRect gives it, as the viewport's own input reads it (Viewport.PushInput in local
    // coordinates): through the control's canvas, so a scaled window or a layer moved doesn't put the click elsewhere.
    static Vector2 ViewportPoint(Control control, Vector2 global) => control.GetGlobalTransformWithCanvas() * (control.GetGlobalTransform().AffineInverse() * global);

    // A mod installed while the game runs, made up, the way the game does it: read into its mod list (not loaded),
    // then announced. Gone again: off both lists.
    // The game announcing a mod installed while it runs: its OnModDetected event, raised as the game raises it (the
    // beta through InvokeOnModDetected, the stable branch inline).
    static void GameInstalls(Mod mod)
    {
        GameMods().Add(mod);
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
        if (typeof(ModManager).GetMethod("InvokeOnModDetected", flags) is { } invoke)
            invoke.Invoke(null, [mod]);
        else
            (typeof(ModManager).GetField(nameof(ModManager.OnModDetected), flags)?.GetValue(null) as Action<Mod>)?.Invoke(mod);
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
    static bool OnInSnapshot(History.Snapshot? snapshot, string id, bool enabled = true)
    {
        if (snapshot == null || JsonNode.Parse(snapshot.Store) is not JsonObject store)
            return false;
        var active = (string?)store["active_profile"];
        return (store["profiles"] as JsonArray ?? []).Any(p => (string?)p?["name"] == active
            && (p?["mods"] as JsonArray ?? []).Any(m => (string?)m?["id"] == id && (bool?)m?["enabled"] == enabled));
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

    // What a control's hover tip says (Hover.Tip; none: empty), or null for no control.
    static string? TipOf(Node? node) => node is Control control ? Hover.TipOf(control) : null;

    // Everything a banner says: its text, its buttons' words and their tips.
    static List<string> BannerWords(Node? banner) => banner?.FindChildren("*", "", true, false)
        .SelectMany(n => n switch { Label label => [label.Text], Button button => new[] { button.Text, Hover.TipOf(button) }, _ => [] }).ToList() ?? [];

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

    static void Check(string name, bool ok, string detail = "", [System.Runtime.CompilerServices.CallerMemberName] string caller = "")
    {
        Seen(caller, 0);
        Results.Add($"{(ok ? "PASS" : "FAIL")} {name}{(ok || detail.Length == 0 ? "" : $": {detail}")}");
    }

    // Where a run's time goes: per test (the method that waited or checked), from its first to its last wait or check,
    // and how much of that was fixed waits. Written as INFO lines after the results (notes#73: measure first).
    static readonly System.Diagnostics.Stopwatch RunClock = System.Diagnostics.Stopwatch.StartNew();
    static readonly Dictionary<string, (double First, double Last, double Waited, int Waits)> Times = [];

    static void Seen(string caller, double waited)
    {
        var now = RunClock.Elapsed.TotalSeconds;
        var (first, _, total, count) = Times.GetValueOrDefault(caller, (now, now, 0, 0));
        Times[caller] = (first, now + waited, total + waited, count + (waited > 0 ? 1 : 0));
    }

    static IEnumerable<string> Timing()
    {
        var waited = Times.Values.Sum(t => t.Waited);
        yield return $"INFO time: {RunClock.Elapsed.TotalSeconds:0} s since the self-test started, {waited:0} s of it in fixed waits ({Times.Values.Sum(t => t.Waits)})";
        foreach (var (name, t) in Times.OrderByDescending(p => p.Value.Last - p.Value.First).Take(25))
            yield return $"INFO time: {name} {t.Last - t.First:0.0} s, waits {t.Waited:0.0} s ({t.Waits})";
    }

    // The fixture's mod that won't load: Test Addon (on) needs SmmTestMissing, a mod id nothing has. Null when it isn't
    // installed (--real) or is off, so the checks that need a mod that won't load skip.
    static Mod? WontLoad() => Dependencies.Find("SmmTestAddon") is { } addon && Copies.Ticked(addon) ? addon : null;

    // Test Addon's dependency: never installed, so it's shown by its id.
    const string Missing = "SmmTestMissing";

    // The text of the hover card `name` that hovering `target` brings up (empty if none), the mouse put back after.
    static async Task<string> HoverCard(Node menu, Control target, string name, double seconds = Hover.Delay + 0.2) =>
        (await SizedHoverCard(menu, target, name, seconds: seconds)).Text;

    // The same, with the card's width (0 if none). shown: run while the card is up (a screenshot).
    static async Task<(string Text, float Width)> SizedHoverCard(Node menu, Control target, string name, Func<Task>? shown = null,
        double seconds = Hover.Delay + 0.2)
    {
        Hover.TestMouse = () => target.GetGlobalRect().GetCenter();
        target.EmitSignal(Control.SignalName.MouseEntered);
        await WaitReal(menu, seconds); // the card's delay (Hover.Delay, or SlowDelay for Late/Slow cards)
        if (shown != null)
            await shown();
        var card = target.GetTree().Root.FindChild(name, true, false);
        var seen = (card?.FindChildren("*", "RichTextLabel", true, false).OfType<RichTextLabel>().FirstOrDefault()?.GetParsedText() ?? "",
            card?.FindChildren("*", "PanelContainer", true, false).OfType<Control>().FirstOrDefault()?.Size.X ?? 0);
        Hover.TestMouse = () => new Vector2(5, 5);
        target.EmitSignal(Control.SignalName.MouseExited);
        await WaitReal(menu, 0.1); // the card's wind-down (Hover.WindDown): gone before the real mouse takes over
        Hover.TestMouse = null; // the real mouse again (menus open where it says)
        return seen;
    }

    // What a row's pencil, buttons and line show, part by part in order: the pencil and buttons with their tips, then
    // the line's tags (folded ones as "+N"), note and author, and whether the author ends the line. A spacer or an empty
    // note shows nothing, so isn't listed: two rows that show the same things read the same, however they were made.
    static string LineOf(NModMenuRow row)
    {
        var parts = new List<string>();
        if (row.GetNodeOrNull<Button>("SpireModManagerEdit") is { Visible: true } pencil)
            parts.Add($"pencil {pencil.Text} ({Hover.TipOf(pencil)})");
        foreach (var name in new[] { RowStrip.ActionsName, RowStrip.OrderActionsName })
            if (row.GetNodeOrNull<Control>(name) is { Visible: true } buttons)
                parts.AddRange(buttons.GetChildren().OfType<Button>().Where(b => b.Visible)
                    .Select(b => $"button {b.Text}{(b.Disabled ? " (disabled)" : "")} ({Hover.TipOf(b)})"));
        if (row.GetNodeOrNull<Control>("SpireModManagerStrip") is { Visible: true } line)
            foreach (var part in line.GetChildren().OfType<Control>().Where(c => c.Visible))
                parts.Add(part switch
                {
                    PanelContainer tag => $"tag {tag.GetChildOrNull<Label>(0)?.Text}",
                    RichTextLabel { Text.Length: > 0 } note => $"note {note.GetParsedText()}",
                    Label author => $"author {author.Text}{(Math.Abs(author.GetGlobalRect().End.X - line.GetGlobalRect().End.X) < 1 ? " at the end" : "")}",
                    _ => "",
                });
        return string.Join(" | ", parts.Where(p => p.Length > 0));
    }

    // A row without its line, buttons and pencil, so the next RowStrip.Apply makes them afresh, as on a new page.
    static void Unmake(NModMenuRow row)
    {
        foreach (var name in new[] { "SpireModManagerStrip", RowStrip.ActionsName, RowStrip.OrderActionsName, "SpireModManagerEdit" })
            if (row.GetNodeOrNull(name) is { } part)
            {
                row.RemoveChild(part);
                part.QueueFree();
            }
    }

    // RowStrip's timed parts (RowStrip.Costs), in the order a row makes them: "title 1.2 ms, pencil 30.5 ms, ...".
    static string Parts(IReadOnlyDictionary<string, double> costs) => string.Join(", ",
        new[] { "title", "pencil", "buttons", "tags", "needs", "author", "added", "rest" }.Select(part => $"{part} {costs.GetValueOrDefault(part):0.0}"))
        + " ms";

    // How a part of a row's line differs from how per-node overrides styled it (before #46): a node of its kind beside
    // the line, given the items of the part's look as overrides (its look: the nearest Theme on it or above it within
    // the row: its own, its tag's or its line's), and every theme item of that kind compared. Lists the items that
    // differ, none when the shared Theme looks exactly as the overrides did, page and all.
    static List<string> UnlikeOverrides(NModMenuRow row, Control part)
    {
        Theme? look = null;
        for (Node? at = part; at != null && at != row && look == null; at = at.GetParent())
            look = (at as Control)?.Theme;
        var type = part.GetClass();
        if (look == null || ClassDB.Instantiate(type).AsGodotObject() is not Control plain)
            return [$"{part.Name}: no look"];
        foreach (var name in look.GetColorList(type))
            plain.AddThemeColorOverride(name, look.GetColor(name, type));
        foreach (var name in look.GetConstantList(type))
            plain.AddThemeConstantOverride(name, look.GetConstant(name, type));
        foreach (var name in look.GetFontList(type))
            plain.AddThemeFontOverride(name, look.GetFont(name, type));
        foreach (var name in look.GetFontSizeList(type))
            plain.AddThemeFontSizeOverride(name, look.GetFontSize(name, type));
        foreach (var name in look.GetStyleboxList(type))
            plain.AddThemeStyleboxOverride(name, look.GetStylebox(name, type));
        row.AddChild(plain);
        var all = ThemeDB.GetDefaultTheme();
        var unlike = all.GetColorList(type).Where(n => part.GetThemeColor(n) != plain.GetThemeColor(n))
            .Concat(all.GetConstantList(type).Where(n => part.GetThemeConstant(n) != plain.GetThemeConstant(n)))
            .Concat(all.GetFontList(type).Where(n => part.GetThemeFont(n) != plain.GetThemeFont(n)))
            .Concat(all.GetFontSizeList(type).Where(n => part.GetThemeFontSize(n) != plain.GetThemeFontSize(n)))
            .Concat(all.GetStyleboxList(type).Where(n => part.GetThemeStylebox(n) != plain.GetThemeStylebox(n)))
            .Concat(all.GetIconList(type).Where(n => part.GetThemeIcon(n) != plain.GetThemeIcon(n)))
            .Select(n => $"{part.Name} {n}").ToList();
        row.RemoveChild(plain);
        plain.Free();
        return unlike;
    }

    // Where the hover card `name` that hovering `target` brings up sits on screen (null if none), the mouse put back after.
    static async Task<Rect2?> HoverCardBox(Node menu, Control target, string name, double seconds = Hover.Delay + 0.2)
    {
        Rect2? box = null;
        await SizedHoverCard(menu, target, name, () =>
        {
            box = target.GetTree().Root.FindChild(name, true, false)?.FindChildren("*", "PanelContainer", true, false).OfType<Control>().FirstOrDefault()?.GetGlobalRect();
            return Task.CompletedTask;
        }, seconds);
        return box;
    }

    // Null if a card Hover placed itself for `trigger`, hovered at its middle, is exactly where it should be: whole on
    // screen; Hover.Gap above the trigger (up), or below it, Hover.Gap off it and at least 20 under the mouse; its left
    // edge 16 right of the mouse, or (left) its right edge 16 left of it. So over neither the trigger nor the mouse.
    // area: what the card keeps off, when more than the trigger (Hover.Watch's area). Otherwise where it is.
    static string? CardMisplaced(Rect2? box, Control trigger, bool up, bool left, Rect2? area = null)
    {
        if (box is not { } card)
            return "no card";
        var (at, screen) = (area ?? trigger.GetGlobalRect(), trigger.GetViewportRect());
        var mouse = trigger.GetGlobalRect().GetCenter();
        var y = up ? card.End.Y - (at.Position.Y - Hover.Gap) : card.Position.Y - Math.Max(mouse.Y + 20, at.End.Y + Hover.Gap);
        var x = left ? card.End.X - (mouse.X - 16) : card.Position.X - (mouse.X + 16);
        return screen.Encloses(card) && Math.Abs(x) < 0.5f && Math.Abs(y) < 0.5f ? null : $"card {card} for {at}, on {screen.Size}";
    }

    // Null if the open menu `menu`, opened from `from` (the mouse, a point; or a picker, MenuPanel.Around), is exactly
    // where it should be: whole on screen; its top at from's bottom, or (up) its bottom at from's top; its left edge at
    // from's left, or (left) its right edge at from's right. So over neither the mouse nor the picker. Otherwise where it is.
    static string? MenuMisplaced(Node? menu, Rect2 from, bool up, bool left)
    {
        if (menu?.GetNodeOrNull<Control>("Catcher/Panel") is not { } panel)
            return "no menu";
        var (box, screen) = (panel.GetGlobalRect(), panel.GetViewportRect());
        var y = up ? box.End.Y - from.Position.Y : box.Position.Y - from.End.Y;
        var x = left ? box.End.X - from.End.X : box.Position.X - from.Position.X;
        return screen.Encloses(box) && Math.Abs(x) < 0.5f && Math.Abs(y) < 0.5f ? null : $"menu {box} from {from}, on {screen.Size}";
    }

    // The selected row's look: a gold rim the same width on every side.
    static bool EvenGoldRim(StyleBoxFlat box) => box.BorderColor == RowTitles.Gold
        && new[] { box.BorderWidthTop, box.BorderWidthBottom, box.BorderWidthLeft, box.BorderWidthRight }.All(w => w == RowTitles.SelectedRim);

    // Wait lets the UI settle (a redraw, a deferred call, a layout pass): a tenth of the time it names, at least 0.05 s
    // (three frames at 60 fps). Waits that are the time itself (a hover card's delay, a tween, a popup's timer, an ease)
    // are WaitReal. Measured (notes#73): 272 s of a 289 s run was fixed waits. SPIRE_MOD_MANAGER_WAIT_SCALE sets the
    // fraction (1: the old full waits, to compare).
    static readonly double WaitScale = double.TryParse(System.Environment.GetEnvironmentVariable("SPIRE_MOD_MANAGER_WAIT_SCALE"),
        System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var scale) && scale > 0 ? scale : 0.1;

    // Waits for the thing, not a time: frame by frame until ready() holds, for at most `timeout` seconds; false if it
    // never does (the check after it then says what's wrong).
    static async Task<bool> WaitUntil(Node node, Func<bool> ready, double timeout = 3, [System.Runtime.CompilerServices.CallerMemberName] string caller = "")
    {
        var (tree, start) = (node.GetTree(), RunClock.Elapsed.TotalSeconds);
        var held = true;
        while (!ready())
        {
            if (RunClock.Elapsed.TotalSeconds - start > timeout)
            {
                held = false;
                break;
            }
            await node.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        }
        Seen(caller, RunClock.Elapsed.TotalSeconds - start);
        return held;
    }

    // After a row is picked: the info panel filled for its copy, our fields in it (the stable branch fills it a little
    // later than the beta). Vanilla View fills only the game's part, so nothing to wait for there.
    static async Task<bool> PanelFor(Node menu, NModdingScreen screen, NModMenuRow? row, [System.Runtime.CompilerServices.CallerMemberName] string caller = "")
    {
        var panel = screen.GetNodeOrNull<Control>("%ModInfoContainer");
        var description = panel?.GetNodeOrNull<RichTextLabel>("ModDescription");
        var ready = await WaitUntil(menu, () => Store.Data.VanillaView || row?.Mod is not { } mod
            || panel != null && ModDetails.ShownKey(panel) == Copies.Key(mod) && panel.HasNode("SpireModManagerYours") && description?.IsFinished() != false, caller: caller);
        if (!ready)
            Results.Add($"INFO {caller}: the panel didn't show {row?.Mod?.manifest?.id} in 3 s: view {Layout.Current}, vanilla {Store.Data.VanillaView}, "
                + $"shown \"{(panel == null ? "no panel" : ModDetails.ShownKey(panel))}\", ours {panel?.HasNode("SpireModManagerYours")}, text done {description?.IsFinished()}, "
                + $"visible {screen.IsVisibleInTree()}");
        return ready;
    }

    // A wait that is the time itself (a hover card's delay, a tween, a popup's timer, an ease): never shortened.
    static async Task WaitReal(Node node, double seconds, [System.Runtime.CompilerServices.CallerMemberName] string caller = "")
    {
        Seen(caller, seconds);
        await node.ToSignal(node.GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);
    }

    static async Task Wait(Node node, double seconds, [System.Runtime.CompilerServices.CallerMemberName] string caller = "")
    {
        if (WaitScale != 1)
            seconds = Math.Max(seconds * WaitScale, 0.05);
        Seen(caller, seconds);
        await node.ToSignal(node.GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);
    }

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
        if (node is LineEdit box && TagPills.TestPills(box) is { Count: > 0 } pills)
            dump.Append(" pills ").Append(string.Join(", ", pills.Select(p => $"{p.Tag} ({p.Rect.Position.X:0},{p.Rect.Position.Y:0} {p.Rect.Size.X:0}x{p.Rect.Size.Y:0} in the box)")));
        if (node is NTickbox tickbox)
            dump.Append(tickbox.IsTicked ? " ticked" : " unticked");
        dump.AppendLine();
        foreach (var child in node.GetChildren())
            DumpTree(child, depth + 1, dump);
    }
}
