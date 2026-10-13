using System.Text;
using System.Text.Json.Nodes;
using Godot;
using MegaCrit.Sts2.Core.Localization;
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
            await TestNotes(menu, screen, content, outDir);
            await TestPendingNotes(menu, screen, content);
            await TestSearch(menu, screen, content, outDir);
            await TestNewMod(menu, screen, content, outDir);
            await TestLoadOrder(menu, screen, content, outDir);
            await TestListBanner(menu, screen, content, outDir);
            await TestPageBar(menu, screen, content, outDir);
            await TestRelaunchCheck(menu, screen, content, outDir);
            await TestLongDetails(menu, screen, content, outDir);
            await TestWorkshopHead(menu, screen);
            await TestBulkEdit(menu, screen, content, outDir);
            await TestHistory(menu, screen, content, outDir);

            // Leaving from Load Order: the page comes back on the main list.
            Click(screen.GetNode<NButton>("SpireModManagerLoadOrderToggle"));
            await Wait(menu, 0.3);
            screen.Visible = false;
            screen.Visible = true;
            await Wait(menu, 0.3);
            Check("reopening after Load Order shows the main list", Layout.Current == Layout.View.Categories
                && screen.GetNode<Control>("%ModInfoContainer").Visible);

            await TestStoreSafety(menu, screen, outDir);
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
            await Wait(menu, 1);
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
            nameof(NewModPopupPatch), nameof(NewModTogglePatch)];
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
            Check($"{when}: the warning shows (no step-aside notice), with our search and toggles",
                Shown(screen.GetNodeOrNull(Bmm.WarningName)) && screen.GetNodeOrNull("SpireModManagerBmmNotice") == null
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
        var warningBox = screen.GetNode<Control>(Bmm.WarningName).GetGlobalRect();
        var pendingBox = screen.GetNode<Control>("%PendingChangesLabel").GetGlobalRect();
        Check("the warning sits beside the view buttons, clear of them and of the restart line",
            Math.Abs(warningBox.GetCenter().Y - toggles[1].GetGlobalRect().GetCenter().Y) < 2 && warningBox.Position.X > toggles[1].GetGlobalRect().End.X
            && !toggles.Any(t => t.GetGlobalRect().Intersects(warningBox)) && pendingBox.Position.X >= warningBox.End.X,
            $"warning {warningBox}, buttons {string.Join(", ", toggles.Select(t => t.GetGlobalRect()))}, restart line {pendingBox}");

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
            Check("its page: none of ours (headers, strips, warning), the game's icons hidden, the list clipped",
                !content.GetChildren().Any(c => c.IsInGroup("SpireModManagerCategoryHeader"))
                && !content.GetChildren().OfType<NModMenuRow>().Any(r => Shown(r.GetNodeOrNull("SpireModManagerStrip")))
                && !Shown(screen.GetNodeOrNull(Bmm.WarningName)) && mask.ClipContents
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
        }

        if (await OpenMenu() is { } third)
            MenuPanel.Press(third, "Reset position");
        await Wait(menu, 0.2);
        Check("Reset position puts it back in the corner", Store.Data.MenuButtonAt == null && button.Position == corner, $"{button.Position} vs {corner}");
        Hover.TestMouse = null;
    }

    // The main menu button's right-click menu: Move button, Reset position, a divider, Relaunch game. Relaunch game
    // checks for mods that won't load first, as the Modding page's button does (fixture: BrandedMod needs BaseLib, not
    // installed): its dialog shows over the main menu, and its fixes work there before the page has been opened.
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
            if (Dependencies.Find("BrandedMod") is not { } branded || !Dependencies.IsOn(branded) || Dialog()?.FindChild("DisableBrokenButton", true, false) == null)
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
                !Dependencies.IsOn(branded) && Profiles.Edited && dot.Visible == ModEntry.HasPendingChanges()
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

    // Row menu, new category renamed in place, assign, move, header menu, delete.
    static async Task TestCategories(Node menu, NModdingScreen screen, Control content, string outDir)
    {
        const string modId = "LocalModFixes";
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
            ModDetails.SetAlias(modId, "");
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

        // The game's confirm popup, shown for the record; deletion itself goes through Profiles.Delete.
        var body = new LocString("settings_ui", "SPIRE_MOD_MANAGER.DELETE_PROFILE_BODY");
        body.Add("name", "Brother co-op");
        _ = Menus.Confirm(new LocString("settings_ui", "SPIRE_MOD_MANAGER.DELETE_PROFILE_TITLE"), body);
        await Wait(menu, 1);
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
        Check("the picker opens the profile list, with details", list != null && MenuPanel.Labels(list).Count == Store.Data.Profiles.Count,
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
            $"edited {Profiles.Edited} (+{string.Join(", +", on)} -{string.Join(", -", off)}), in the launch state {ids.Count(id => OnInSnapshot(History.Loaded, id))} of {ids.Length}");
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

    // The fixture gives SpireModManager an alias and notes; LocalModFixes gets them through the editor.
    static async Task TestNotes(Node menu, NModdingScreen screen, Control content, string outDir)
    {
        var panel = screen.GetNode<NModInfoContainer>("%ModInfoContainer");
        NModMenuRow? Row(string id) => content.GetChildren().OfType<NModMenuRow>().FirstOrDefault(r => r.Mod?.manifest?.id == id);
        string RowTitle(string id) => Row(id)?.GetNode<RichTextLabel>("Title").Text ?? "";

        if (Row(ModEntry.ModId) is not { } ours || Row("LocalModFixes") is not { } other)
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
        Check("panel saves notes, trimmed", Store.InfoFor("LocalModFixes")?.Notes == "Typed in the panel" && notes.Text == "Typed in the panel", notes.Text);
        Check("panel saves tags, repeats dropped; at rest they're pills", Store.InfoFor("LocalModFixes")?.Tags?.SequenceEqual(["co-op", "Favorites"]) == true
            && yours.GetNode<Control>("Tags/CustomTags") is { Visible: true } && yours.GetNode("Tags/CustomTags/Pills").GetChildCount() == 2,
            string.Join(", ", Store.InfoFor("LocalModFixes")?.Tags ?? []));
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
        Check("Cancel puts the notes back", notes.Text == "Typed in the panel" && Store.InfoFor("LocalModFixes")?.Notes == "Typed in the panel", notes.Text);
        var alias = Line("Alias");
        alias.Text = "Fixes";
        alias.EmitSignal(Control.SignalName.FocusExited);
        await Wait(menu, 0.4);
        Check("the alias field saves, the row follows", Store.InfoFor("LocalModFixes")?.Alias == "Fixes" && RowTitle("LocalModFixes").StartsWith("Fixes"));

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
        Check("after the delay, the title's card: id and state first", CardShown() && CardText().StartsWith("LocalModFixes") && CardText().Contains("Enabled"), CardText());
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
        Check("the notepad's card at once, and only one card at a time", CardShown() && CardText().StartsWith("LocalModFixes") && Cards() == 1, $"{Cards()} cards");
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
        var texts = NModalContainer.Instance?.OpenModal is Node popup
            ? popup.FindChildren("*", "", true, false).Select(n => n is Label l ? l.Text : n is RichTextLabel r ? r.Text : "").ToList() : [];
        Check("new mod popup says \"New Mod Added\"", texts.Any(t => t.Contains("New Mod Added")), string.Join(" | ", texts.Where(t => t.Length > 0)));
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
        typeof(ModManager).GetMethod("InvokeOnModDetected", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?.Invoke(null, [fresh]);
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
            Check("unticking it is an unsaved edit of the profile", Profiles.Edited && Profiles.UnsavedChanges().Off.Contains("Fresh Mod"),
                $"edited {Profiles.Edited}, off: {string.Join(", ", Profiles.UnsavedChanges().Off)}");
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
        Installed.Forget(fresh);
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

        var byName = await Shown("mod fixes");
        Check("search by name", byName.SequenceEqual(["LocalModFixes"]), string.Join(", ", byName));
        await Capture(screen, outDir, "10-search");
        var byAlias = await Shown("the manager");
        Check("search by alias", byAlias.SequenceEqual([ModEntry.ModId]), string.Join(", ", byAlias));
        var byTag = await Shown("favorites");
        Check("search by own tag", byTag.SequenceEqual(["LocalModFixes"]), string.Join(", ", byTag));
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
        await Shown("");
        ClickChip("SmmTestLib", "Library", false);
        await Wait(menu, 0.3);
        Check("clicking a row's tag searches for it", box.Text == "#Library" && Visible().SequenceEqual(["SmmTestLib"]), $"{box.Text}: {string.Join(", ", Visible())}");
        ClickChip("SmmTestLib", "Gameplay", true);
        await Wait(menu, 0.3);
        Check("Shift-clicking another adds it", box.Text == "#Library #Gameplay" && Visible().SequenceEqual(["SmmTestLib"]), $"{box.Text}: {string.Join(", ", Visible())}");
        // Authors: from the info panel's author (the row's is plain text); Shift on a tag adds it after a space.
        await Shown("");
        var rowAuthor = content.GetChildren().OfType<NModMenuRow>().FirstOrDefault(r => r.Mod?.manifest?.id == "LocalModFixes")?.GetNodeOrNull<Label>("SpireModManagerStrip/Author");
        Check("the row's author is plain: just the name, not clickable", rowAuthor is { Text: "symphony", MouseFilter: Control.MouseFilterEnum.Ignore });
        var panelHead = screen.GetNode<Control>("%ModInfoContainer").GetNodeOrNull<RichTextLabel>("SpireModManagerHead");
        panelHead?.EmitSignal(RichTextLabel.SignalName.MetaClicked, "author:symphony");
        await Wait(menu, 0.3);
        Check("the panel's author searches for their mods", box.Text == "@symphony"
            && Visible().Count > 0 && Visible().All(id => Dependencies.Find(id)?.manifest?.author == "symphony"), $"{box.Text}: {string.Join(", ", Visible())}");
        ClickChip("LocalModFixes", "Local", true);
        await Wait(menu, 0.3);
        Check("Shift-click on a tag adds it after a space", box.Text == "@symphony #Local", box.Text);
        var local = await Shown("#local");
        Check("#tag matches the tag only, any case", local.Count > 1 && local.All(id => Tags.For(Dependencies.Find(id)).Any(c => c.Text == "Local")), string.Join(", ", local));
        var none = await Shown("zzz no such mod");
        Check("no match hides every row and header", none.Count == 0
            && !content.GetChildren().OfType<Control>().Any(c => c.IsInGroup("SpireModManagerCategoryHeader") && c.Visible));
        var cleared = await Shown("");
        Check("clearing restores the list", cleared.Count > 0);
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

        Click(screen.GetNode<NButton>("SpireModManagerLoadOrderToggle"));
        await Wait(menu, 0.5);
        var start = Ids();
        Check("load order view: saved order, handles, no headers", start.SequenceEqual(Saved())
            && Rows().All(r => r.HasNode("SpireModManagerHandle"))
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
        const string free = "LocalModFixes";
        if (Rows().FirstOrDefault(r => r.Mod?.manifest?.id == free)?.Mod is { } freeMod)
            LoadOrder.Move(freeMod, 0, Rows());
        CategoryList.Redraw(content);
        await Wait(menu, 0.5);
        Check("move to top saves the order", Ids()[0] == free && Saved()[0] == free, string.Join(", ", Ids()));
        Check("an order change is pending", pending.Visible && ModEntry.HasPendingChanges());
        Check("load order: no tickboxes", Rows().All(r => !r.GetNode<Control>("Tickbox").Visible));
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
        Check("pressing a row starts the drag look at once", Rows().Any(r => r.Modulate.A < 0.5f && r != pressed)
            && content.GetParent().GetChildren().OfType<ColorRect>().Any(c => c.Size.Y == 5));
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
        if (Rows().FirstOrDefault(r => r.Mod?.manifest?.id == "LocalModFixes") is { Mod: { } kept } keptRow)
        {
            CategoryList.Select(screen, keptRow);
            Store.Data.UncategorizedCollapsed = true;
        }
        LoadOrder.ShowLinks(content, Rows()[0], strong: false);
        Click(screen.GetNode<NButton>("SpireModManagerLoadOrderToggle"));
        await Wait(menu, 0.5);
        var selected = Rows().FirstOrDefault(r => r.Mod == CategoryList.Selected);
        Check("category view takes the handles off", !Rows().Any(r => r.HasNode("SpireModManagerHandle"))
            && content.GetChildren().Any(c => c.IsInGroup("SpireModManagerCategoryHeader")));
        Check("back from Load Order: titles in place", Rows().All(r => r.GetNode<Control>("Title").OffsetLeft == 18),
            string.Join(", ", Rows().Select(r => r.GetNode<Control>("Title").OffsetLeft)));
        Check("back from Load Order: no lines or dimming", !content.GetParent().GetChildren().Any(c => c.IsInGroup("SpireModManagerLinks"))
            && Rows().All(r => r.Modulate == Colors.White));
        Check("the selection survives the switch, its category opened", selected?.Mod?.manifest?.id == "LocalModFixes"
            && selected.Visible && !Store.Data.UncategorizedCollapsed);
    }

    // Fixture mods: SmmTestContent and SmmTestUi need SmmTestLib; BrandedMod needs BaseLib, which isn't there.
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
        Check("missing dependency is called out", Note("BrandedMod").Contains("BaseLib") && Note("BrandedMod").Contains("not installed"),
            Note("BrandedMod"));

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
        Check("Auto sort has its hint", screen.FindChild("AutoSortHint", true, false) is Label { Visible: true } hintLabel && hintLabel.Text.Contains("compatibility"));
        Check("order warning and Auto sort stay above the list, outside its scrolling",
            screen.FindChild("OrderProblems", true, false) is Node warning && !content.IsAncestorOf(warning)
            && screen.FindChild("AutoSortButton", true, false) is Control { Visible: true } sort && !content.IsAncestorOf(sort));
        await Capture(screen, outDir, "12-order-problem");

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
                Row("LocalModFixes")?.Modulate.A < 1 && Row("SmmTestLib")?.Modulate.A == 1 && links == 1, $"{links} links");
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
        Check("main list: a needed mod that is off gets a banner", text.Contains("need a mod that is turned off"), text);
        await Capture(screen, outDir, "2b-list-banner");
        screen.FindChild("EnableNeededButton", true, false)?.EmitSignal(BaseButton.SignalName.Pressed);
        await Wait(menu, 0.3);
        Check("Enable needed mods turns it on, banner says no more", Dependencies.IsOn(lib) && tickbox.IsTicked
            && screen.FindChild("EnableNeededButton", true, false) == null
            && (screen.FindChild("OrderProblems", true, false) as Label)?.Text.Contains("turned off") != true);
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
        Check("a bad order puts a '!' with the warning on the Load Order button", badge is { Visible: true }
            && screen.GetNode<Control>("SpireModManagerLoadOrderToggle").TooltipText.StartsWith("For compatibility"));
        await Capture(screen, outDir, "2c-load-order-badge");
        LoadOrder.FixOrder(Rows());
        CategoryList.Redraw(content);
        await Wait(menu, 0.3);
        Check("fixed: the '!' goes", screen.GetNodeOrNull<Control>("SpireModManagerLoadOrderToggle/WarningBadge") is not { Visible: true });
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
        var author = Rows().FirstOrDefault(r => r.Mod?.manifest?.id == "LocalModFixes")?.GetNodeOrNull<Label>("SpireModManagerStrip/Author")?.Text;
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

    // Relaunch with mods that won't load (fixture: BrandedMod needs BaseLib, not installed; here the framework
    // is turned off too): the dialog lists them; Enable needed mods, then Disable mods that can't load, fix
    // them in place; Cancel and Esc close it. Never presses Relaunch (that would quit the test).
    static async Task TestRelaunchCheck(Node menu, NModdingScreen screen, Control content, string outDir)
    {
        if (content.GetChildren().OfType<NModMenuRow>().FirstOrDefault(r => r.Mod?.manifest?.id == "SmmTestLib") is not { Mod: { } lib } libRow
            || Dependencies.Find("BrandedMod") is not { } branded || !Dependencies.IsOn(branded))
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
        Check("relaunch check lists the mods that won't load", Body().Contains("BaseLib, which isn't installed") && Body().Contains("Test Framework, which is off"), Body());
        await Capture(screen, outDir, "15-relaunch-check");
        Press("EnableNeededButton");
        await Wait(menu, 0.3);
        Check("Enable needed mods fixes the off ones, the list updates", Dependencies.IsOn(lib) && !Body().Contains("Test Framework") && Body().Contains("BaseLib"), Body());
        Press("DisableBrokenButton");
        await Wait(menu, 0.3);
        Check("Disable mods that can't load: nothing left, plain Relaunch", !Dependencies.IsOn(branded)
            && Dialog()?.FindChild("RelaunchNowButton", true, false) is Button { Text: "Relaunch Game" }, Body());
        Press("CancelRelaunchButton");
        await Wait(menu, 0.2);
        Check("Cancel closes it", Dialog() == null);
        History.Undo(); // BrandedMod back on, as the fixture has it
        await Wait(menu, 0.3);
        RelaunchCheck.Show(screen);
        await Wait(menu, 0.2);
        Input.ParseInputEvent(new InputEventKey { Keycode = Key.Escape, Pressed = true });
        await Wait(menu, 0.2);
        Input.ParseInputEvent(new InputEventKey { Keycode = Key.Escape, Pressed = false });
        Check("Esc closes it, and stays on the page", Dialog() == null && screen.IsVisibleInTree() && Dependencies.IsOn(branded));
    }

    // A snapshot of the edge case, not a fix (yet): a made-up mod with very long everything (name, author,
    // version, description, alias, tags, notes), shown in the list and the panel. The screenshot
    // 16-long-details shows how it looks today; it's removed again afterwards.
    static async Task TestLongDetails(Node menu, NModdingScreen screen, Control content, string outDir)
    {
        const string id = "SmmVeryLongModIdentifierThatGoesOnAndOnForTheSakeOfTesting";
        var words = "lorem ipsum dolor sit amet consectetur adipiscing elit sed do eiusmod tempor incididunt ut labore et dolore magna aliqua".Split(' ');
        string Text(int count) => string.Join(' ', Enumerable.Range(0, count).Select(i => words[i % words.Length]));
        var mod = new Mod
        {
            path = "", modSource = ModSource.ModsDirectory, state = ModLoadState.AddedAtRuntime,
            manifest = new ModManifest
            {
                id = id, name = "The Extraordinarily Long-Named Compendium of Every Card, Relic, Potion and Event Ever Imagined (Deluxe Edition)",
                author = "A Collective of Very Enthusiastic Modders With An Unusually Long Team Name", version = "12.345.6789-beta.12+build.98765",
                description = Text(220), minGameVersion = "0.111.0",
            },
        };
        typeof(ModManager).GetMethod("InvokeOnModDetected", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)?.Invoke(null, [mod]);
        await Wait(menu, 0.4);
        NModalContainer.Instance?.Clear();
        Store.EditMod(id, info =>
        {
            info.Alias = "My favourite enormous content pack that I keep meaning to rename to something shorter";
            info.Notes = Text(120);
            info.Tags = ["content", "big", "really-long-tag-name-for-testing", "favourites", "co-op", "needs-checking-after-every-update", "deluxe"];
        });
        CategoryList.Apply(content);
        await Wait(menu, 0.3);
        if (content.GetChildren().OfType<NModMenuRow>().FirstOrDefault(r => r.Mod == mod) is { } row)
        {
            CategoryList.Select(screen, row);
            CategoryList.Reveal(content, id, flash: false);
            await Wait(menu, 0.5);
            await Capture(screen, outDir, "16-long-details");
            Check("long details: the panel shows the made-up mod (see 16-long-details)", screen.GetNode<RichTextLabel>("%ModInfoContainer/ModTitle").GetParsedText().StartsWith("The Extraordinarily"));
            var longAuthor = row.GetNodeOrNull<Label>("SpireModManagerStrip/Author");
            var longTag = row.GetNodeOrNull("SpireModManagerStrip")?.GetChildren().OfType<PanelContainer>().Select(c => c.GetChildOrNull<Label>(0)).FirstOrDefault(l => l?.Text.StartsWith("really-long") == true);
            Check("long details: the row's author and a long tag are cut with an ellipsis", longAuthor is { ClipText: true, TextOverrunBehavior: TextServer.OverrunBehavior.TrimEllipsis } && longTag is { ClipText: true });
            content.RemoveChild(row);
            row.QueueFree();
        }
        else
            Results.Add("FAIL long details: no row for the made-up mod");
        Installed.Forget(mod);
        Store.EditMod(id, info => (info.Alias, info.Notes, info.Tags, info.Category) = (null, null, null, null));
        SaveManager.Instance.SettingsSave.ModSettings?.ModList.RemoveAll(e => e.Id == id);
        History.Record();
        CategoryList.Redraw(content);
        screen.GetNode<NModInfoContainer>("%ModInfoContainer").Clear();
        await Wait(menu, 0.3);
    }

    // A Workshop mod's head (made-up details; the sandbox has no Steam): Author, Updated (how long ago, and a link
    // to its change notes on the Workshop), Compatibility. "Ago" in hours under a day, singular for one.
    static async Task TestWorkshopHead(Node menu, NModdingScreen screen)
    {
        var mod = new Mod
        {
            path = "", modSource = ModSource.SteamWorkshop, workshopId = 42, state = ModLoadState.AddedAtRuntime,
            manifest = new ModManifest { id = "SmmWorkshopTest", name = "Workshop Test", author = "Someone Careful" },
        };
        Installed.Detected(mod); // so the panel can find it by id
        WorkshopInfo.TestSet(42, new WorkshopInfo.Details(DateTime.Now.AddDays(-30), DateTime.Now.AddDays(-4), [], [], 0, null));
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
        WorkshopInfo.TestSet(42, null);
        Installed.Forget(mod);
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
        if (Row("LocalModFixes") is not { } fixes || Uncategorized().Count < 3)
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
        Check("Ctrl-click lets go of one; the bar counts the rest", picked.Count == all - 1 && !picked.Contains("LocalModFixes")
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
        Check("a category for all of them at once", picked.All(id => Store.InfoFor(id)?.Category == tool) && Store.InfoFor("LocalModFixes")?.Category == null,
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
        if (rows.FirstOrDefault(r => r.Mod?.manifest?.id == "LocalModFixes")?.Mod is { } fixes)
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

    static void Check(string name, bool ok, string detail = "") =>
        Results.Add($"{(ok ? "PASS" : "FAIL")} {name}{(ok || detail.Length == 0 ? "" : $": {detail}")}");

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
