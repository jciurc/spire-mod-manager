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
        try
        {
            await Wait(menu, 3);
            Check("no popup open", NModalContainer.Instance?.OpenModal == null,
                NModalContainer.Instance?.OpenModal?.GetType().Name ?? "");
            Check("main menu button", menu.GetNodeOrNull("SpireModManagerMenuButton") != null);
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
            await TestProfileEditing(menu, screen, outDir);
            await TestNotes(menu, screen, content, outDir);
            await TestSearch(menu, screen, content, outDir);
            await TestLoadOrder(menu, screen, content, outDir);
            await TestHistory(menu, screen, content, outDir);
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
        Check("row menu lists categories", rowMenu?.ItemCount == Store.Data.Categories.Count + 4, $"{rowMenu?.ItemCount} items");
        await Capture(screen, outDir, "5-row-menu");
        rowMenu?.Hide();

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
        var name = bar.GetNode<LineEdit>("ProfileName");
        var before = ModListState();
        var count = Store.Data.Profiles.Count;
        var start = Profiles.Active;

        bar.FindChild("NewProfileButton", true, false)?.EmitSignal(BaseButton.SignalName.Pressed);
        await Wait(menu, 0.5);
        Check("New adds a profile and switches to it", Store.Data.Profiles.Count == count + 1 && Profiles.Active != start
            && name.Text == Profiles.Active?.Name, name.Text);

        name.GrabFocus();
        name.Text = "Brother co-op";
        name.EmitSignal(LineEdit.SignalName.TextSubmitted, name.Text);
        await Wait(menu, 0.5);
        Check("rename from the bar", Profiles.Active?.Name == "Brother co-op" && name.Text == "Brother co-op");

        // The game's confirm popup, shown for the record; deletion itself goes through Profiles.Delete.
        var body = new LocString("settings_ui", "SPIRE_MOD_MANAGER.DELETE_PROFILE_BODY");
        body.Add("name", "Brother co-op");
        _ = Menus.Confirm(new LocString("settings_ui", "SPIRE_MOD_MANAGER.DELETE_PROFILE_TITLE"), body);
        await Wait(menu, 1);
        await Capture(screen, outDir, "7-delete-profile-confirm");
        NModalContainer.Instance?.Clear();
        await Wait(menu, 0.5);

        if (Profiles.Active is { } created)
            Profiles.Delete(created, screen);
        await Wait(menu, 0.5);
        Check("delete the active profile switches to a neighbour", Store.Data.Profiles.Count == count && Profiles.Active != null
            && Store.Data.Profiles.All(p => p.Name != "Brother co-op") && name.Text == Profiles.Active.Name);
        if (start != null && Profiles.Active != start)
            Profiles.SwitchTo(start, screen);
        Check("mod list unchanged after new/rename/delete", before.SequenceEqual(ModListState()));
        Check("last profile can't be deleted", Store.Data.Profiles.Count > 1 || !Profiles.Delete(Store.Data.Profiles[0], screen));
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
        Check("panel titles with the alias", panel.GetNode<RichTextLabel>("ModTitle").Text == "The Manager");
        Check("panel keeps the original name and shows notes", description.Contains("Spire Mod Manager") && description.Contains("Fixture note"));
        Check("Workshop button hidden for a local mod", panel.GetNodeOrNull<Control>("WorkshopPageButton")?.Visible == false);
        await Capture(screen, outDir, "8-details-with-notes");

        screen.OnRowSelected(other);
        await Wait(menu, 0.5);
        Click(panel.GetNode<NButton>("EditNotesButton"));
        await Wait(menu, 0.5);
        var editor = panel.GetNodeOrNull("NotesEditor");
        Check("Edit Notes opens the editor", editor != null);
        if (editor == null)
            return;
        editor.GetNode<LineEdit>("Alias").Text = "Fixes";
        editor.GetNode<TextEdit>("Notes").Text = "Typed in the editor";
        editor.GetNode<LineEdit>("Tags").Text = "co-op, Favorites, co-op";
        await Capture(screen, outDir, "9-notes-editor");
        Click(editor.GetNode<NButton>("SaveButton"));
        await Wait(menu, 0.5);
        Check("save stores alias and notes", Store.InfoFor("LocalModFixes") is { Alias: "Fixes", Notes: "Typed in the editor" });
        Check("save stores tags, repeats dropped", Store.InfoFor("LocalModFixes")?.Tags?.SequenceEqual(["co-op", "Favorites"]) == true,
            string.Join(", ", Store.InfoFor("LocalModFixes")?.Tags ?? []));
        Check("save updates row and panel", RowTitle("LocalModFixes").StartsWith("Fixes")
            && panel.GetNode<RichTextLabel>("ModDescription").Text.Contains("Typed in the editor"));
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
            && Tags.For(Dependencies.Find("SmmTestUi")).Select(c => c.Text).SequenceEqual(["Visual only"]));
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
            && !content.GetChildren().Any(c => c.IsInGroup("SpireModManagerCategoryHeader") && c.Name != "OrderToolbar"), string.Join(", ", start));
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
        void Send(InputEvent e) => handle.EmitSignal(Control.SignalName.GuiInput, e);
        Send(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, GlobalPosition = from, Position = from });
        Send(new InputEventMouseMotion { GlobalPosition = to, Position = to });
        await Wait(menu, 0.3);
        await Capture(screen, outDir, "11-load-order-drag");
        Send(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, GlobalPosition = to, Position = to });
        await Wait(menu, 0.5);
        var dragged = Ids();
        Check("drag moves the row below the third", dragged.IndexOf(first.Mod?.manifest?.id ?? "") == 2 && dragged.SequenceEqual(Saved()),
            string.Join(", ", dragged));

        // Put the original order back: nothing pending again.
        for (var i = 0; i < start.Count; i++)
            if (Rows().FirstOrDefault(r => r.Mod?.manifest?.id == start[i])?.Mod is { } mod)
                LoadOrder.Move(mod, i, Rows());
        CategoryList.Redraw(content);
        await Wait(menu, 0.5);
        Check("restoring the order clears pending", Ids().SequenceEqual(start) && !ModEntry.HasPendingChanges());

        await TestDependencies(menu, screen, content, outDir);

        Click(screen.GetNode<NButton>("SpireModManagerLoadOrderToggle"));
        await Wait(menu, 0.5);
        Check("category view takes the handles off", !Rows().Any(r => r.HasNode("SpireModManagerHandle"))
            && content.GetChildren().Any(c => c.IsInGroup("SpireModManagerCategoryHeader")));
    }

    // Fixture mods: SmmTestContent and SmmTestUi need SmmTestLib; BrandedMod needs BaseLib, which isn't there.
    static async Task TestDependencies(Node menu, NModdingScreen screen, Control content, string outDir)
    {
        List<NModMenuRow> Rows() => content.GetChildren().OfType<NModMenuRow>().ToList();
        List<string> Ids() => Rows().Select(r => r.Mod?.manifest?.id ?? "").ToList();
        NModMenuRow? Row(string id) => Rows().FirstOrDefault(r => r.Mod?.manifest?.id == id);
        string Note(string id) => Row(id)?.GetNodeOrNull<Label>("SpireModManagerStrip/Needs")?.Text ?? "";
        if (Row("SmmTestLib") is not { Mod: { } lib } || Row("SmmTestContent") is not { Mod: { } dependent })
        {
            Results.Add("SKIP dependencies: test mods not installed");
            return;
        }
        Check("dependency note names what it needs", Note("SmmTestContent").Contains("Test Framework"), Note("SmmTestContent"));
        Check("missing dependency is called out", Note("BrandedMod").Contains("BaseLib") && Note("BrandedMod").Contains("not installed"),
            Note("BrandedMod"));

        // Force a bad order (as an old saved list might have): the needing mod above its framework.
        LoadOrder.Move(dependent, Ids().IndexOf("SmmTestLib"), Rows());
        CategoryList.Redraw(content);
        await Wait(menu, 0.5);
        Check("bad order: banner and warning", content.GetNodeOrNull("OrderToolbar")?.FindChild("OrderProblems", true, false) != null && Note("SmmTestContent").Contains("loads after this"),
            Note("SmmTestContent"));
        await Capture(screen, outDir, "12-order-problem");

        var before = Ids();
        content.GetNode("OrderToolbar").FindChild("FixOrderButton", true, false)?.EmitSignal(BaseButton.SignalName.Pressed);
        await Wait(menu, 0.5);
        var after = Ids();
        Check("Fix order puts the framework first and nothing else moves",
            after.IndexOf("SmmTestLib") < after.IndexOf("SmmTestContent") && after.IndexOf("SmmTestLib") < after.IndexOf("SmmTestUi")
            && content.GetNodeOrNull("OrderToolbar")?.FindChild("OrderProblems", true, false) == null
            && after.Where(id => id != "SmmTestLib").SequenceEqual(before.Where(id => id != "SmmTestLib")),
            string.Join(", ", after));

        // Now a move above the framework is blocked: by the inline/menu actions and by dragging.
        var rowContent = Row("SmmTestContent");
        Check("moving above what it needs is disabled", rowContent != null && LoadOrder.MoveBreaks(dependent, 0, Rows())
            && LoadOrder.Actions(content, rowContent, () => { })[0].Disabled);
        var handle = rowContent?.GetNode<Control>("SpireModManagerHandle");
        if (handle != null)
        {
            var from = handle.GetGlobalRect().GetCenter();
            var to = new Vector2(from.X, Rows()[0].GetGlobalRect().Position.Y + 4);
            handle.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, GlobalPosition = from });
            handle.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseMotion { GlobalPosition = to });
            await Wait(menu, 0.3);
            await Capture(screen, outDir, "13-blocked-drag");
            var links = content.GetParent().GetChildren().Count(c => c.IsInGroup("SpireModManagerLinks"));
            Check("dragging dims unrelated rows, lights linked ones, draws links",
                Row("LocalModFixes")?.Modulate.A < 1 && Row("SmmTestLib")?.Modulate.A == 1 && links == 1, $"{links} links");
            handle.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, GlobalPosition = to });
            await Wait(menu, 0.5);
        }
        Check("a blocked drag doesn't move the row", Ids().SequenceEqual(after), string.Join(", ", Ids()));

        // Auto sort: libraries, then gameplay, then visual-only; dependencies satisfied; one undo puts it back.
        var beforeSort = Ids();
        content.GetNode("OrderToolbar").FindChild("AutoSortButton", true, false)?.EmitSignal(BaseButton.SignalName.Pressed);
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
        Check("dropping restores the list look", Rows().All(r => r.Modulate == Colors.White)
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
        Press("ChangesButton");
        await Wait(menu, 0.3);
        await Capture(screen, outDir, "14-changes");
        Press("CloseButton");

        Press("UndoButton");
        await Wait(menu, 0.3);
        Check("undo turns it back on, tickbox included", On() && tickbox.IsTicked);
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
        Check("Revert all restores the mod list as loaded", History.Loaded?.Mods.SequenceEqual(History.CurrentMods()) == true
            && HistoryBar.Describe().Count == 0 && On());
        Press("UndoButton");
        await Wait(menu, 0.3);
        Check("Revert all can be undone", beforeRevert.SequenceEqual(History.CurrentMods()), string.Join(", ", History.CurrentMods()));
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
