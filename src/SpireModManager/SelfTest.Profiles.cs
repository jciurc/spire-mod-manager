using Godot;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;
using MegaCrit.Sts2.Core.Saves;

namespace SpireModManager;

// The self-test's checks of categories and profiles: editing them, unsaved and pending changes, sharing, subscribing.
static partial class SelfTest
{
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
        var (unsavedOn, unsavedOff) = Profiles.UnsavedChanges();
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
        Check("CSV: the columns follow the tick boxes (the note's is Note)", ProfileCsv.Header(new()).SequenceEqual(["Mod Id", "Name", "Version", "Enabled", "Category", "Workshop Id"])
            && ProfileCsv.Header(new(Category: false, Alias: true, Notes: true)).SequenceEqual(["Mod Id", "Name", "Version", "Enabled", "Workshop Id", "Alias", "Note"]),
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
        var restored = Ticked("IncludeNotes") && Ticked("IncludeCategory") && Ticked("RememberChoices") && ProfileCsv.Records(Csv()).FirstOrDefault()?.Contains("Note") == true;
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
        Check("our own list: its category, alias and note read back", ours is [{ Category: "Tools", Alias: "Aye", Notes: "Some notes" }],
            string.Join(" | ", ours.Select(e => $"{e.Category}/{e.Alias}/{e.Notes}")));
        // The note's column: Note in our lists, Notes in Better Mod Menu's and our older ones, any case.
        string Planned(string column) => Profiles.PlanImport(ProfileCsv.Read($"Mod Id,Enabled,{column}\nSmmTestNoteOnly,TRUE,From the list\n").Mods).Extras.Named.FirstOrDefault().Notes ?? "";
        Check("an import fills in a note from a Note column or a Notes one, any case", new[] { "Note", "Notes", "nOTES" }.All(c => Planned(c) == "From the list"),
            string.Join(" | ", new[] { "Note", "Notes" }.Select(Planned)));
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
}
