using System.Text;
using System.Text.Json.Nodes;
using Godot;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;
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
            await Step(() => TestCrashCulprit(menu, outDir)); // who stopped the start (Culprits), on real failures (SelfTest.Crash.cs)
            await Step(() => TestSameIdCanary()); // which copy of a mod installed twice loads (SameIdLoad)
            await Step(() => TestCopyRule(outDir)); // the rule every view and the launch share (Copies.Next), on made-up copies
            await Step(() => TestSameIdTie());
            await Step(() => TestSameIdPlan());
            await Step(() => TestSameIdLaunch());
            await Step(() => TestSameIdSettle());
            await Step(() => TestCopiesData(outDir));
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
            await Step(() => TestMenuButtonPendingCard(menu, screen, content, outDir));
            await Step(() => TestSearch(menu, screen, content, outDir));
            await Step(() => TestBranchTags());
            await Step(() => TestDescriptionSearch());
            await Step(() => TestSearchGrammar());
            await Step(() => TestFoundIn(menu, screen, content, outDir));
            await Step(() => TestHiddenTags(content));
            await Step(() => TestRedrawCost(menu, screen, content));
            await Step(() => TestManyMods(menu, screen, content));
            await Step(() => TestNewMod(menu, screen, content, outDir));
            await Step(() => TestWorkshopTitles(menu, screen, content, outDir));
            await Step(() => TestLoadOrder(menu, screen, content, outDir));
            await Step(() => TestLoadOrderFind(menu, screen, content, outDir));
            await Step(() => TestListBanner(menu, screen, content, outDir));
            await Step(() => TestManagersBanner(menu, screen, content, outDir));
            await Step(() => TestSelfIncluded(menu, screen, content, outDir));
            await Step(() => TestPageBar(menu, screen, content, outDir));
            await Step(() => TestListFilters(menu, screen, content, outDir));
            await Step(() => TestRelaunchCheck(menu, screen, content, outDir));
            await Step(() => TestLongDetails(menu, screen, content, outDir));
            await Step(() => TestDescriptionCut(menu, screen, content, outDir));
            await Step(() => TestCutWhole(content));
            await Step(() => TestWorkshopHead(menu, screen, outDir));
            await Step(() => TestOtherModPanel(menu, screen, content, outDir));
            await Step(() => TestGameChanges(menu, screen, content, outDir));
            await Step(() => TestModifiedContent(menu, screen, content, outDir));
            await Step(() => TestBulkEdit(menu, screen, content, outDir));
            await Step(() => TestPicks(menu, screen, content, outDir));
            await Step(() => TestCopies(menu, screen, content, outDir));
            await Step(() => TestSameIdCopies(menu, screen, content, outDir));
            await Step(() => TestSameIdBothOn(menu, screen, content, outDir)); // both copies ticked: which loads, and the words for it
            await Step(() => TestCopyOutcome(menu, screen, content, outDir)); // every view from the one rule, in each state it decides
            await Step(() => TestCopyUnticked(menu, screen, content));
            await Step(() => TestCopyGroups(menu));
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
            ModifiedContent.Close();
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
            line.Text == "Spire Mod Manager is incompatible with this game version, or may require an update" && ModEntry.ShowNeedsUpdate(stand) == line
            && stand.GetChildCount() == 1 && line.Position.Y + line.Size.Y <= 83, $"{line.Text} at {line.Position}, {line.Size}");
        stand.Free();
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
            // A first run (no data yet, so nothing off): both copies ticked, and the higher version (B, sorted second)
            // loads, swapped into the id's first slot, the other set aside (no duplicate error); nothing pending for them.
            var twins = ModManager.Mods.Where(m => m.manifest?.id == TwinId).ToList();
            if (twins.FirstOrDefault(m => Copies.CopyOf(m) == "l:SmmTestTwinA") is { } a && twins.FirstOrDefault(m => Copies.CopyOf(m) == "l:SmmTestTwinB") is { } b)
                Check("first run: a mod installed twice, no data yet: both copies ticked, the higher version loads (swapped into the id's first slot), the "
                    + "other set aside with no duplicate error; no copy_picks, nothing off, nothing pending for them",
                    SameIdLoad.Plan.GetValueOrDefault(TwinId) is { Swapped: true } plan && plan.Loads == b && b.state == ModLoadState.Loaded
                    && a.state == ModLoadState.DisabledDuplicate && !ModManager.Mods.Any(m => m.errors?.Any(e => e.LocEntryKey == "MOD_ERROR.DUPLICATE_ID") == true)
                    && Store.Data.CopyPicks == null && Copies.Ticked(a) && Copies.Ticked(b) && Copies.Loads(b) && !Copies.Loads(a)
                    && Store.Data.CopiesOff?.Any(k => Copies.IdOf(k) == TwinId) == false,
                    $"A {a.state}, B {b.state}; off {string.Join(", ", Store.Data.CopiesOff ?? [])}; plan {SameIdLoad.Plan.GetValueOrDefault(TwinId)}");
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
                    Check("a failed start: the repair of the game's rebuilt mod list in memory ran as the error showed, with the list as the game read it "
                        + "and before the first main menu", LaunchGuard.RepairedBeforeMenu && LaunchGuard.ReadList != null && !SameIdLoad.Settled,
                        $"repaired {LaunchGuard.RepairedBeforeMenu}, list read {LaunchGuard.ReadList != null}, settled {SameIdLoad.Settled}");
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

    // The SmartFormat version tools/loc.cs judges translations with (its #:package; check.cs keeps the two equal).
    const string ToolParser = "3.3.0";

    // Each hidden tag against the list's rows, by what it means told from what the page shows or from where the search's
    // decisions say it comes from, not by HiddenTags' own rules: the row's box (#enabled), its chips (#local, #new,
    // #duplicate), its category as the list files it; a dependency missing or off by Dependencies.Check (#unmet), NeededOff,
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
            ("renamed", m => Store.InfoFor(m)?.Alias is { Length: > 0 }), ("note", m => !string.IsNullOrWhiteSpace(Store.InfoFor(m)?.Notes)),
            ("uncategorized", m => CategoryList.CategoryName(Copies.KeyOf(m)) == Menus.Text("SPIRE_MOD_MANAGER.UNCATEGORIZED")),
            ("unmet", Unmet), ("needed", Needed), ("order", Misordered), ("duplicate", m => Chip(m, "SPIRE_MOD_MANAGER.TAG_SAME_ID")),
            ("clash", clashing.Contains), ("aside", Copies.Loses), ("managers", managers.Contains), ("failed", Failed), ("gameerror", GameError),
            ("error", Error), ("warning", Warning), ("problem", m => Error(m) || Warning(m)),
        ];
        static string Key(Mod mod) => $"{mod.manifest?.id}/{mod.modSource}";
        var wrong = meant.Select(t => (t.Tag, Found: mods.Where(m => Search.Matches(m, "#" + t.Tag)).ToList(), Meant: mods.Where(t.Means).ToList()))
            .Where(t => !t.Found.SequenceEqual(t.Meant))
            .Select(t => $"#{t.Tag} finds {string.Join(",", t.Found.Select(Key))}, means {string.Join(",", t.Meant.Select(Key))}").ToList();
        return (wrong, string.Join(" ", meant.Select(t => $"#{t.Tag} {mods.Count(m => Search.Matches(m, "#" + t.Tag))}")));
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

    // The fixture's two copies of one mod id (SmmTestTwinA 1.0.0, SmmTestTwinB 1.1.0 needing Test Framework, the higher
    // version and the fixture's legacy pick, so the one that loads) and Test Twin User, which needs 1.1.0 or newer. dev.cs
    // lists the id after the framework, so A sorts first.
    const string TwinId = "SmmTestTwin";

    // The copy of an id that loads after a relaunch (Copies.Next); null for none.
    static string? LoadingCopy(string id) => Copies.Loading(id) is { } loads ? Copies.CopyOf(loads) : null;

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

    // What every id and source installed more than once keeps, whatever was done to its copies: no copy_picks in our data
    // (read once, never written again), its switch on exactly while a copy of it is ticked, and then exactly one copy of
    // the id loading. Null when every one keeps it, else what broke first.
    static string? CopiesBroken()
    {
        var gameOff = Dependencies.SavedOff();
        var installed = Dependencies.EachCopy(ModManager.Mods.Union(Installed.Mods)).ToList();
        if (Store.Data.CopyPicks != null)
            return $"copy_picks still in our data: {string.Join(", ", Store.Data.CopyPicks)}";
        foreach (var (id, source, copies) in Copies.TwinGroups())
        {
            var on = !gameOff.Contains((id, source));
            var ticked = installed.Where(m => m.manifest?.id == id && m.modSource == source && Copies.Ticked(m)).Select(Copies.CopyOf).ToList();
            if (on != ticked.Count > 0)
                return $"{id}/{source}: the switch {(on ? "on" : "off")}, ticked {string.Join(", ", ticked)}";
            if (on && installed.Where(m => m.manifest?.id == id && Copies.Loads(m)).Select(Copies.CopyOf).ToList() is { Count: not 1 } loading)
                return $"{id}/{source}: {loading.Count} copies load ({string.Join(", ", loading)})";
        }
        return null;
    }

    // The words of the duplicate copies, word for word as settings_ui.json has them: drafts until Jordan's copy pass, so
    // here once for every check that reads them (an edit there is one edit here). Draft fills in their values.
    const string SameIdText = "{count} mods with the same ID are enabled - only one copy of each will be loaded";
    const string CopyGroup = "{id}:", CopyLineText = "{mod} {version} ({source}) - {state}", CopyActive = "Active", CopyIgnored = "Ignored";
    const string RelaunchCopiesIntro = "Some mods with duplicate IDs are enabled, and won't be loaded";
    const string CopySwitchLine = "{mod}: {to} loads instead of {from}";
    const string ClashTip = "Other copies of this mod are enabled\n{others}\n\n{loads} will be used, determined by version number and date of install - other copies will be ignored";
    const string StatusWontLoad = "Duplicate - Won't load";
    const string CopyLoses = "{other} will be used due to {why} - disable copies you don't want to use";
    const string CopyLosesPlain = "{other} will be used, determined by version number and date of install - disable copies you don't want to use";
    const string WhyLocal = "the game prioritizing local copies", WhyHigher = "its higher version";
    const string WhyFirst = "the same version and an earlier install date";
    const string WhyUnreadable = "this copy's version being unreadable";
    const string CopyBlocked = "This copy would load before a required dependency. {other} is currently compatible and will be loaded instead.";
    const string LoadsBlocked = "The copy {other} would load before a required dependency, so this copy will be loaded instead";
    const string LoadsUnticked = "This copy loads though it's disabled: it shares the game's switch with {other}, which is enabled, and the game prioritizes "
        + "it over {local} as the higher version";
    const string LoadsAlso = "{other} is also enabled - only this copy will be loaded";
    const string LoadsAfter = "Loads on relaunch, in place of {other}";
    const string FailedOther = "This mod shares an ID with {other}";
    const string NotApplied = "Spire Mod Manager couldn't choose which copy was loaded. If the game is on a different version, Spire Mod Manager may need to be updated.";
    const string OtherEnabled = "{other} is the enabled copy";
    const string RelaunchSelfOff = "Spire Mod Manager (this mod) will be disabled on relaunch. To use it again, enable it on the vanilla modding page, "
        + "and it will load after the next restart.";
    const string NewCopyOff = "A copy of {mods} was recently installed, and is disabled";

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
    static string DuplicateChip => Menus.Text("SPIRE_MOD_MANAGER.TAG_SAME_ID");
    static string CustomTagTip => Template("SPIRE_MOD_MANAGER.TAG_CUSTOM_COUNT_TIP");
    static string CustomTagTipOne => Menus.Text("SPIRE_MOD_MANAGER.TAG_CUSTOM_COUNT_TIP_ONE");
    static string MoreTags(int count) => count == 1 ? Menus.Text("SPIRE_MOD_MANAGER.MORE_TAGS_ONE") : Draft(Template("SPIRE_MOD_MANAGER.MORE_TAGS"), ("count", $"{count}"));

    // A text with its {values} still in it, for Draft (Menus.Text formats it, and these need their values).
    static string Template(string key) => new LocString("settings_ui", key).GetRawText();

    static string Draft(string text, params (string Name, string Value)[] values) =>
        values.Aggregate(text, (filled, value) => filled.Replace($"{{{value.Name}}}", value.Value));

    // The duplicates grouped by ID (ModDetails.CopyGroups) as the same-ID banner's card and the Relaunch check read (no
    // markup). Grouped: what an ID's group should read, its line, then a line per copy in the order given (the one that
    // loads first): its name (alias, else name), its manifest's version, which copy (Local, the Workshop item, a local
    // twin's folder), and Active for the one that loads, else Ignored. GroupIn: what an ID's group reads in lines, up to
    // the next ID (none there: empty). RelaunchCopies: the Relaunch check's lines after the duplicates' intro (none
    // without it).
    static List<string> Grouped(string id, params (Mod Mod, bool Active)[] copies) => [Draft(CopyGroup, ("id", id)), .. copies.Select(c => GroupLine(c.Mod, c.Active))];

    static string GroupLine(Mod mod, bool active)
    {
        var version = mod.manifest?.version ?? "";
        // No version: the line reads without it, and without the gap it'd leave.
        var line = version.Length > 0 ? CopyLineText : CopyLineText.Replace("{version} ", "");
        return "  •  " + Draft(line, ("mod", Store.InfoFor(mod)?.Alias ?? mod.manifest?.name ?? mod.manifest?.id ?? ""), ("version", version),
            ("source", Copies.Source(mod)), ("state", active ? CopyActive : CopyIgnored));
    }

    static List<string> GroupIn(IEnumerable<string> lines, string id)
    {
        var from = lines.SkipWhile(l => l != Draft(CopyGroup, ("id", id))).ToList();
        return from.Count == 0 ? [] : [from[0], .. from.Skip(1).TakeWhile(l => l.StartsWith("  •  ", StringComparison.Ordinal))];
    }

    static List<string> RelaunchCopies(string body) => [.. body.Split('\n').SkipWhile(l => l != RelaunchCopiesIntro).Skip(1)];

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
    static List<string> PopupTexts() => NModalContainer.Instance?.OpenModal is Node popup
        ? popup.FindChildren("*", "", true, false).Select(n => n is Label l ? l.Text : n is RichTextLabel r ? r.Text : "").ToList() : [];

    // Whether the game's auto-size reads a text's markup (MegaLabelHelper.ParseBbcode throws at a tag out of its pair).
    static bool GameReads(string bbcode)
    {
        try
        {
            MegaLabelHelper.ParseBbcode(bbcode);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    // Null if label drew its game boxes (GameText) as meant: count of them, each in its look (Border wide on every side,
    // Radius round), Margin inside its table on every side, the table within the label's width (no side cut off), no
    // wider than its text room (wider, the line break after it wrapped onto an empty line of its own) and taller than a
    // line of text (it's the table's line); else what's wrong.
    static string? BoxesWrong(RichTextLabel label, int count)
    {
        var boxes = GameText.Drawn(label);
        var line = label.GetThemeFont("normal_font").GetHeight(label.GetThemeFontSize("normal_font_size"));
        var bar = label.GetVScrollBar();
        var room = label.Size.X - label.GetThemeStylebox("normal").GetMinimumSize().X - (bar.Visible ? bar.Size.X : 0);
        static bool Even(StyleBoxFlat look) => Enum.GetValues<Side>().All(s => look.GetBorderWidth(s) == GameText.Border)
            && Enum.GetValues<Corner>().All(c => look.GetCornerRadius(c) == GameText.Radius);
        return boxes.Count != count ? $"{boxes.Count} boxes, not {count}"
            : boxes.FirstOrDefault(b => b.Box != b.Table.Grow(-GameText.Margin) || b.Table.Position.X < 0 || b.Table.End.X > label.Size.X
                || label.GetLineWidth(b.Line) > room + 0.5f || b.Table.Size.Y < line * 1.5f || !Even(GameText.Style(b.Dim))) is { Table.Size.X: > 0 } wrong
                ? $"table {wrong.Table} on a line {label.GetLineWidth(wrong.Line)} wide, box {wrong.Box}, in {label.Size} ({room} for text)" : null;
    }

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
    // the line's tags and "and N more..", note and author, and whether the author ends the line. A spacer or an empty note
    // shows nothing, so isn't listed: two rows that show the same things read the same, however they were made.
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
                    Label more when more.Name.ToString() == RowStrip.MoreName => $"{more.Name} {more.Text}",
                    Label author => $"author {author.Text}{(Math.Abs(author.GetGlobalRect().End.X - line.GetGlobalRect().End.X) < 1 ? " at the end" : "")}",
                    _ => "",
                });
        return string.Join(" | ", parts.Where(p => p.Length > 0));
    }

    // RowStrip's timed parts (RowStrip.Costs), in the order a row makes them: "title 1.2 ms, pencil 30.5 ms, ...".
    static string Parts(IReadOnlyDictionary<string, double> costs) => string.Join(", ",
        new[] { "title", "pencil", "buttons", "tags", "needs", "author", "added", "rest" }.Select(part => $"{part} {costs.GetValueOrDefault(part):0.0}"))
        + " ms";

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
