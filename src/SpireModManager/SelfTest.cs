using System.Text;
using Godot;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Modding;
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
        if (System.Environment.GetEnvironmentVariable("SPIRE_MOD_MANAGER_SELFTEST_MODE") == "real")
        {
            await RunReal(menu, outDir);
            return;
        }
        try
        {
            await Wait(menu, 3);
            Check("no popup open", NModalContainer.Instance?.OpenModal == null,
                NModalContainer.Instance?.OpenModal?.GetType().Name ?? "");
            Check("main menu button", menu.GetNodeOrNull("SpireModManagerMenuButton") != null);
            var relaunchArgs = ModEntry.RelaunchArgs();
            Check("relaunch passes the window mode once", relaunchArgs.Count(a => a is "--fullscreen" or "--maximized" or "--windowed") == 1
                && relaunchArgs.Contains("--force-steam"), string.Join(" ", relaunchArgs));
            Check("dev run marked", menu.GetTree().Root.Title.StartsWith("[DEV]") && menu.GetTree().Root.HasNode("SpireModManagerDevTag"),
                menu.GetTree().Root.Title);
            await Capture(menu, outDir, "1-main-menu");

            var screen = menu.SubmenuStack.PushSubmenuType<NModdingScreen>();
            await Wait(menu, 2);
            var content = screen.GetNode<Control>("%ModsScrollContainer/Mask/Content");
            var rows = content.GetChildren().OfType<NModMenuRow>().ToList();
            Check("relaunch button", screen.GetNodeOrNull("SpireModManagerRelaunchButton") != null);
            if (ModEntry.BetterModMenuLoaded) // dev.cs test --bmm
            {
                Check("steps aside for Better Mod Menu", screen.GetNodeOrNull("SpireModManagerBmmNotice") != null
                    && screen.GetNodeOrNull("ModsBorder/SpireModManagerProfileBar") == null
                    && !content.GetChildren().Any(c => c.IsInGroup("SpireModManagerCategoryHeader")));
                await Capture(screen, outDir, "2-better-mod-menu");
                return;
            }
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
                    Profiles.Save(); // the fixture launches on an unsaved edit (its list differs from the profile)
                var before = ModListState();
                Profiles.SwitchTo(other, screen);
                await Wait(menu, 1);
                Results.Add($"INFO switched to {other.Name}: {ModListState().Count(s => s.EndsWith("=on"))} on");
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

            Click(screen.GetNode<NButton>("SpireModManagerViewToggle"));
            await Wait(menu, 1);
            Check("vanilla view hides headers", !content.GetChildren().Any(c => c.IsInGroup("SpireModManagerCategoryHeader")));
            var border = screen.GetNode<Control>("ModsBorder");
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
            await TestLoadOrder(menu, screen, content, outDir);
            await TestHistory(menu, screen, content, outDir);

            // Leaving from Load Order: the page comes back on the main list.
            Click(screen.GetNode<NButton>("SpireModManagerLoadOrderToggle"));
            await Wait(menu, 0.3);
            screen.Visible = false;
            screen.Visible = true;
            await Wait(menu, 0.3);
            Check("reopening after Load Order shows the main list", Layout.Current == Layout.View.Categories
                && screen.GetNode<Control>("%ModInfoContainer").Visible);
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
        Check("the list keeps its full width", Math.Abs(content.Size.X - content.GetParent<Control>().Size.X) < 1, $"{content.Size.X} of {content.GetParent<Control>().Size.X}");
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

        if (content.GetChildren().OfType<NModMenuRow>().FirstOrDefault(r => r.Visible)?.GetNodeOrNull<Control>("SpireModManagerStrip") is { } strip
            && strip.GetNodeOrNull<Control>("Actions")?.GetChildOrNull<Control>(0) is { } button)
            Check("inline buttons are centred in the strip", Math.Abs(button.GetGlobalRect().GetCenter().Y - strip.GetGlobalRect().GetCenter().Y) < 2,
                $"{button.GetGlobalRect()} in {strip.GetGlobalRect()}");
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
        Check("row menu: alias, notes, tags, then categories", rowItems.Count == Store.Data.Categories.Count + 5 && rowItems[0] == "Add alias",
            string.Join(", ", rowItems));
        await Capture(screen, outDir, "5-row-menu");
        if (rowMenu != null)
        {
            // "Add alias" turns the menu into an editor; Save stores it and brings the menu back, now "Edit alias".
            MenuPanel.Press(rowMenu, "Add alias");
            await Wait(menu, 0.3);
            var editor = rowMenu.FindChild("AliasEditor", true, false);
            Check("a menu item opens its editor in the menu", editor != null && MenuPanel.Labels(rowMenu).Count == 0);
            await Capture(screen, outDir, "5b-menu-editor");
            if (editor?.GetNode<LineEdit>("Field") is { } field)
                field.Text = "Menu alias";
            editor?.FindChild("Save", true, false)?.EmitSignal(BaseButton.SignalName.Pressed);
            await Wait(menu, 0.3);
            Check("its Save stores the alias and goes back to the menu", Store.InfoFor(modId)?.Alias == "Menu alias"
                && MenuPanel.Labels(rowMenu).FirstOrDefault() == "Edit alias", string.Join(", ", MenuPanel.Labels(rowMenu)));
            ModDetails.SetAlias(modId, "");
            CategoryList.Apply(content);
            rowMenu.Hide();
        }

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

        Categories.Move(category, -1);
        CategoryList.Apply(content);
        await Wait(menu, 0.5);
        var order = content.GetChildren().Select(c => c.Name.ToString()).ToList();
        Check("moved up: header order follows", order.IndexOf("Category Renamed") < order.IndexOf($"Category {Store.Data.Categories.Last().Name}"));
        Check("mod row sits under its header", content.GetChildren().IndexOf(row) == order.IndexOf("Category Renamed") + 1);
        var headerMenu = CategoryList.ShowHeaderMenu(content, category);
        await Wait(menu, 0.5);
        await Capture(screen, outDir, "6-header-menu");
        headerMenu.Hide();

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
            && !name.Visible && picker.Visible && ModListState().Count(s => s.EndsWith("=on")) == 1, string.Join(", ", ModListState()));

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
        var list = screen.FindChildren("SpireModManagerMenu", "", true, false).OfType<PopupPanel>().FirstOrDefault(p => p.Visible);
        Check("the picker opens the profile list, with details", list != null && MenuPanel.Labels(list).Count == Store.Data.Profiles.Count,
            list == null ? "no list" : string.Join(", ", MenuPanel.Labels(list)));
        await Capture(screen, outDir, "7b-profile-list");
        await RealClick(menu, picker);
        Check("a second press on the picker closes it", !screen.FindChildren("SpireModManagerMenu", "", true, false).OfType<PopupPanel>().Any(p => p.Visible));
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
        Check("a library says how many mods need it", Note("SmmTestLib").Contains("Required for 2 mods"), Note("SmmTestLib"));
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
            Profiles.Save(); // start clean: the fixture launches on an unsaved edit (its list differs from the profile)
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
        await Toggle(wasOn); // and back, saved, for the tests after
        Press("SaveProfileButton");
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
        Check("row shows alias beside the name", RowTitle(ModEntry.ModId).StartsWith("The Manager") && RowTitle(ModEntry.ModId).Contains("Spire Mod Manager"),
            RowTitle(ModEntry.ModId));
        screen.OnRowSelected(ours);
        await Wait(menu, 0.5);
        var description = panel.GetNode<RichTextLabel>("ModDescription").Text;
        var yours = panel.GetNode<Control>("SpireModManagerYours");
        Check("panel titles with the alias", panel.GetNode<RichTextLabel>("ModTitle").Text == "The Manager");
        Check("panel keeps the original name; notes in the notes field", description.Contains("Spire Mod Manager")
            && yours.GetNode<TextEdit>("Notes").Text == "Fixture note");
        Check("a local mod says so instead of a Workshop link", panel.FindChild("SpireModManagerLink", true, false) is LinkButton { Disabled: true, Text: "Local mod" });
        Check("the thumbnail stays small (the scene's minimum size lifted)", panel.GetNode<Control>("ModImage").Size.X <= 130, $"{panel.GetNode<Control>("ModImage").Size}");
        await Capture(screen, outDir, "8-details-with-notes");

        // Tags and notes typed in the panel save when focus leaves.
        screen.OnRowSelected(other);
        await Wait(menu, 0.5);
        yours.GetNode<TextEdit>("Notes").Text = "Typed in the panel";
        yours.GetNode<LineEdit>("Tags").Text = "co-op, Favorites, co-op";
        await Capture(screen, outDir, "9-notes-editing");
        yours.GetNode<TextEdit>("Notes").EmitSignal(Control.SignalName.FocusExited);
        yours.GetNode<LineEdit>("Tags").EmitSignal(Control.SignalName.FocusExited);
        await Wait(menu, 0.3);
        Check("panel saves notes", Store.InfoFor("LocalModFixes")?.Notes == "Typed in the panel");
        Check("panel saves tags, repeats dropped", Store.InfoFor("LocalModFixes")?.Tags?.SequenceEqual(["co-op", "Favorites"]) == true,
            string.Join(", ", Store.InfoFor("LocalModFixes")?.Tags ?? []));

        // Fields sit right under the details; Cancel puts the text back; the alias field retitles.
        var details = panel.GetNode<Control>("ModDescription");
        Check("the fields sit right below the details", Math.Abs(yours.OffsetTop - details.OffsetBottom - ModDetails.FieldsGap) < 1
            && yours.OffsetBottom <= panel.Size.Y - ModDetails.ButtonsFromBottom + 1, $"{details.OffsetBottom} -> {yours.OffsetTop}..{yours.OffsetBottom}");
        var notes = yours.GetNode<TextEdit>("Notes");
        notes.GrabFocus();
        await Wait(menu, 0.1);
        Check("editing shows Save, Cancel and Clear", yours.FindChild("NotesButtons", true, false) is Control { Visible: true });
        notes.Text = "Not wanted";
        yours.FindChild("NotesCancel", true, false)?.EmitSignal(BaseButton.SignalName.Pressed);
        await Wait(menu, 0.2);
        Check("Cancel puts the notes back", notes.Text == "Typed in the panel" && Store.InfoFor("LocalModFixes")?.Notes == "Typed in the panel", notes.Text);
        var aliasField = yours.GetNode<LineEdit>("Alias");
        aliasField.Text = "Panel alias";
        aliasField.EmitSignal(Control.SignalName.FocusExited);
        await Wait(menu, 0.4);
        Check("the alias field saves and retitles", Store.InfoFor("LocalModFixes")?.Alias == "Panel alias"
            && panel.GetNode<RichTextLabel>("ModTitle").Text == "Panel alias" && RowTitle("LocalModFixes").StartsWith("Panel alias"));

        // The row's pencil edits the alias in place.
        Check("rows have the alias pencil", Row("LocalModFixes")?.HasNode("SpireModManagerPencil") == true);
        if (Row("LocalModFixes") is { } fixesRow && ModDetails.BeginAliasEdit(fixesRow) is { } aliasBox)
        {
            aliasBox.Text = "Fixes";
            aliasBox.EmitSignal(LineEdit.SignalName.TextSubmitted, aliasBox.Text);
        }
        await Wait(menu, 0.5);
        Check("pencil saves the alias, row and panel follow", Store.InfoFor("LocalModFixes")?.Alias == "Fixes" && RowTitle("LocalModFixes").StartsWith("Fixes")
            && panel.GetNode<RichTextLabel>("ModTitle").Text == "Fixes");
        Check("Vanilla View shows plain names", await WithVanillaView(screen, menu, () => RowTitle(ModEntry.ModId) == "Spire Mod Manager"));
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
        Check("automatic tags", Tags.For(Dependencies.Find("SmmTestLib")).Select(c => c.Text).SequenceEqual(["Gameplay", "Library"])
            && Tags.For(Dependencies.Find("SmmTestUi")).Count == 0);
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

        // Moves use a mod with no dependencies either way, so no order rule can block them.
        const string free = "LocalModFixes";
        if (Rows().FirstOrDefault(r => r.Mod?.manifest?.id == free)?.Mod is { } freeMod)
            LoadOrder.Move(freeMod, 0, Rows());
        CategoryList.Redraw(content);
        await Wait(menu, 0.5);
        Check("move to top saves the order", Ids()[0] == free && Saved()[0] == free, string.Join(", ", Ids()));
        Check("an order change is pending", pending.Visible && ModEntry.HasPendingChanges());

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

        // Multi-select: pick the first and third rows, drag the first's grip below the last row; both move,
        // in their order.
        var all = Rows();
        LoadOrder.Pick(content, all[0], toggle: false, range: false);
        LoadOrder.Pick(content, all[2], toggle: true, range: false);
        var pickedIds = new[] { all[0], all[2] }.Select(r => r.Mod?.manifest?.id ?? "").ToList();
        Check("Ctrl-click picks a second row, both highlighted", LoadOrder.Selection.Count == 2
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
        LoadOrder.ClearSelection(content);

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
            screen.OnRowSelected(keptRow);
            Store.Data.UncategorizedCollapsed = true;
        }
        LoadOrder.ShowLinks(content, Rows()[0], strong: false);
        Click(screen.GetNode<NButton>("SpireModManagerLoadOrderToggle"));
        await Wait(menu, 0.5);
        var selected = Rows().FirstOrDefault(r => r.Mod == CategoryList.Selected);
        Check("category view takes the handles off", !Rows().Any(r => r.HasNode("SpireModManagerHandle"))
            && content.GetChildren().Any(c => c.IsInGroup("SpireModManagerCategoryHeader")));
        Check("back from Load Order: titles in place (after the alias pencil)", Rows().All(r => r.GetNode<Control>("Title").OffsetLeft == 28),
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
            screen.OnRowSelected(libSelected);
        await Wait(menu, 0.3);
        var facts = panel.GetNode<RichTextLabel>("ModDescription").Text;
        Check("panel: needed by, Open Folder", facts.Contains("Needed by")
            && facts.Contains("Test Content") && facts.Contains("Test UI") && panel.GetNode<Control>("OpenFolderButton").Visible, facts);

        // Force a bad order (as an old saved list might have): the needing mod above its framework.
        LoadOrder.Move(dependent, Ids().IndexOf("SmmTestLib"), Rows());
        CategoryList.Redraw(content);
        await Wait(menu, 0.5);
        Check("bad order: banner and warning", screen.FindChild("OrderProblems", true, false) != null && Note("SmmTestContent").Contains("Test Framework after"),
            Note("SmmTestContent"));
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
            && !LoadOrder.Actions(content, rowContent, () => { })[0].Disabled);
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
        Check("a mod turned off since launch has a changed-colour title", row.GetNode<Control>("Title").Modulate == ModDetails.ChangedOffTitle);
        var changes = screen.GetNode<Control>(HistoryBar.ChangesName);
        changes.EmitSignal(Control.SignalName.MouseEntered);
        await Wait(menu, 0.3);
        Check("hovering Changes shows the details", screen.HasNode("SpireModManagerChangesPanel"));
        await Capture(screen, outDir, "14-changes");
        changes.EmitSignal(Control.SignalName.MouseExited);
        await Wait(menu, 0.2);

        Press("UndoButton");
        await Wait(menu, 0.3);
        Check("undo turns it back on, tickbox included", On() && tickbox.IsTicked && row.GetNode<Control>("Title").Modulate != ModDetails.ChangedOffTitle);
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
            && History.AtEntered && HistoryBar.Counts() == (0, 0) && On(),
            $"mods {History.Entered?.Mods.SequenceEqual(History.CurrentMods())}, at {History.AtEntered}, counts {HistoryBar.Counts()}, on {On()}");
        Press("UndoButton"); // the list view's history: on/off comes back; the order has its own history
        await Wait(menu, 0.3);
        Check("Revert all can be undone", beforeRevert.Select(m => (m.Id, m.Source, m.Enabled)).ToHashSet()
            .SetEquals(History.CurrentMods().Select(m => (m.Id, m.Source, m.Enabled))), string.Join(", ", History.CurrentMods()));

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
