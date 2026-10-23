using Godot;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Localization.Fonts;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;
using MegaCrit.Sts2.Core.Saves;

namespace SpireModManager;

// The self-test's checks of our data: undo and redo, missing resources, the store's safety, each language's fonts.
static partial class SelfTest
{
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
}
