using System.Text.Json.Nodes;
using Godot;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;
using MegaCrit.Sts2.Core.Saves;

namespace SpireModManager;

// The self-test's checks of picked mods and of a mod installed more than once: which copy loads, and its words.
static partial class SelfTest
{
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

        // Both copies on: the same-id banner, yellow, saying only one loads, its card which copy; a Duplicate chip on each
        // whose tip names the other copy and the one that loads; Show steps between the two; Dismiss hides it for this pair
        // through a redraw (view state: no change, no undo step). The copy that won't load reads "Duplicate - Won't load",
        // not "Pending relaunch", before a launch too.
        var border = screen.GetNode<Control>("ModsBorder");
        Control? SameId() => border.GetNodeOrNull<Control>(LoadOrder.SameIdName);
        CategoryList.Redraw(content);
        await Wait(menu, 0.3);
        var bothOn = Copies.Ticked(local) && Copies.Ticked(copy);
        var bannerText = (SameId()?.FindChild("SameIdWarning", true, false) as Label)?.Text;
        var chips = Tags.For(local).Concat(Tags.For(copy)).Where(c => c.Text == DuplicateChip).ToList();
        var card = await BannerCard(menu, screen, SameId());
        Check("both copies on: a yellow banner (\"2 mods with the same ID are enabled. Only one copy will be loaded.\"), its card saying which loads (the local "
            + "copy: the same version); a Duplicate chip on each naming the other and the copy that loads",
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
        // The hidden tags on the list as it is: both copies #clash, #duplicate and #warning; the Workshop copy (the local one
        // loads) #aside; each finding the rows it means.
        var asMeant = HiddenTagsAsMeant(content);
        Check("both copies on: hidden tags on the list as it is (both #clash, #duplicate, #warning; the Workshop copy #aside), each finding the rows it means",
            asMeant.Wrong.Count == 0 && new[] { local, copy }.All(m => Search.Matches(m, "#clash") && Search.Matches(m, "#duplicate") && Search.Matches(m, "#warning"))
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
        // the chip searches #Duplicate: both copies, no other mod.
        Dependencies.Set([copy], false, screen);
        CategoryList.Redraw(content);
        await Wait(menu, 0.3);
        var forgotten = Store.Data.SameIdDismissed == null && SameId() == null;
        Dependencies.Set([copy], true, screen);
        CategoryList.Redraw(content);
        await Wait(menu, 0.3);
        var beside = Copies.Ticked(copy) && Copies.Ticked(local) && SameId() != null;
        bool NothingStored() => Store.Data.CopyPicks == null && !CopiesOff().Any(k => Copies.IdOf(k) == id);
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
        var sameIdSearch = Search.TermFor("#", DuplicateChip);
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
        Check("a Workshop copy enabled beside the local one that wins reads \"Duplicate - Won't load\", before a launch too and once the game set it aside; "
            + "its panel says the local copy loads instead, and why; the local copy's that it's the one that loads",
            beforeStatus.Contains(StatusWontLoad) && setAside.Contains(StatusWontLoad) && beforeLine == losesLine && setAsideLine == losesLine
            && Line(local) == Draft(LoadsAlso, ("other", Named(copy))), $"{beforeStatus} / {setAside}; lines {beforeLine} / {setAsideLine}; local {Line(local)}");
        // For the screenshot: the two rows, both ticked, and the Workshop copy's panel with its line and reason.
        if (content.GetChildren().OfType<NModMenuRow>().FirstOrDefault(r => r.Mod == copy) is { } shownCopy)
        {
            CategoryList.Select(content, shownCopy);
            await PanelFor(menu, screen, shownCopy);
            CategoryList.ScrollTo(content, shownCopy);
            await Wait(menu, 0.2);
            await Capture(screen, outDir, "19a-copies-local-workshop");
        }

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
            updated = Copies.Loads(copy) == w && Copies.Loads(local) == !w && lost?.Why == (w ? Copies.Why.Higher : Copies.Why.Local)
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
        var newerChips = new[] { local, copy }.Select(m => Tags.For(m).FirstOrDefault(c => c.Text == DuplicateChip)?.Tip).ToList();
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

    // The fixture twins both ticked, or A off again, as if the launch had them so: copies_off without A, or with it, in
    // every undo snapshot, launch's too (History.Quietly), and the active profile's entry for them likewise, so it's no
    // change, no undo step and no unsaved edit. What loaded is as it was: A set aside (DisabledDuplicate), B.
    static void BothTickedQuietly(bool both)
    {
        const string a = "l:SmmTestTwinA";
        History.Quietly(data =>
        {
            var off = (data.CopiesOff ?? []).Where(k => k != $"{TwinId}|{a}");
            data.CopiesOff = Store.TidyOff(both ? off : off.Append($"{TwinId}|{a}"));
            if (data.Profiles.FirstOrDefault(p => p.Name == data.ActiveProfile) is { } active)
                foreach (var list in new[] { active.Mods, active.Edited })
                    for (var i = 0; i < (list?.Count ?? 0); i++)
                        if (list?[i] is { Id: TwinId, Source: ModSource.ModsDirectory } entry)
                            list[i] = entry with { CopiesOff = both ? [] : [a] };
        });
    }

    // SameIdLoad's look at how the game loads (this launch): our Init found us the first mod still to load, in a
    // List<Mod>, with the DisabledDuplicate state there. It ran before the launch record: the record names each copy of
    // the fixture twins, on for the one that loads, and every other entry its copy too (N3: a later launch knows which
    // copy was there first).
    static void TestSameIdCanary()
    {
        var (at, first) = SameIdLoad.Self;
        Check("same ID, the canary: our Init the first mod still to load, the game's list a List<Mod>, DisabledDuplicate there; the choice applies",
            SameIdLoad.Canary == true && first == ModEntry.ModId && at >= 0 && at < ModManager.Mods.Count && ModManager.Mods[at].manifest?.id == ModEntry.ModId
            && ModManager.Mods is List<Mod> && Enum.TryParse<ModLoadState>("DisabledDuplicate", out _) && !SameIdLoad.CanaryFailed,
            $"canary {SameIdLoad.Canary}, first still to load {first} at {at}");
        var record = LaunchGuard.Read(Path.Combine(LaunchGuard.Dir ?? "", LaunchGuard.WorkingFile))?.Mods ?? [];
        var unnamed = record.Where(m => m.Copy == null).Select(m => m.Id).ToList();
        Check("... the launch record names every entry's copy (so a later launch knows which copy was there before a second one came)",
            record.Count > 0 && unnamed.Count == 0, $"{record.Count} entries; without a copy: {string.Join(", ", unnamed)}");
        if (ModManager.Mods.Count(m => m.manifest?.id == TwinId) != 2)
        {
            TwinsMissing("same ID, the launch record's copies");
            return;
        }
        var twins = record.Where(m => m.Id == TwinId).ToList();
        Check("... it ran before the launch record: the record names each copy of the fixture twins, on for the one that loads (SmmTestTwinB)",
            twins.Count == 2 && twins.All(m => m.Copy != null) && twins.Where(m => m.Enabled).Select(m => m.Copy).SequenceEqual(["l:SmmTestTwinB"]),
            string.Join(", ", twins.Select(m => $"{m.Copy}={m.Enabled}")));
    }

    // The game's own local-vs-Workshop rule on a made-up list, as ModManager.RemoveDisabledMods plays it (its decompiled
    // code, mirrored): a copy whose switch is off Disabled; each Workshop copy still to load compared with the first local
    // copy on in the list (the game's read order), the beta taking out the local copy where the Workshop version is
    // higher, else (a tie, or a version it can't read) the Workshop copy; stable every Workshop copy beside a local one.
    static void GameRule(IReadOnlyList<Mod> mods, HashSet<(string?, ModSource)> off)
    {
        var first = new Dictionary<string, Mod>();
        foreach (var mod in mods)
            if (off.Contains((mod.manifest?.id, mod.modSource)))
                mod.state = ModLoadState.Disabled;
            else if (mod.modSource == ModSource.ModsDirectory && mod.manifest?.id is { } id)
                first.TryAdd(id, mod);
        foreach (var workshop in mods.Where(m => m.modSource == ModSource.SteamWorkshop && m.state == ModLoadState.None))
        {
            if (workshop.manifest?.id is not { } id || !first.TryGetValue(id, out var local))
                continue;
            var (w, l) = (Copies.GameVersion(workshop), Copies.GameVersion(local));
            if (Compat.WorkshopBeatsOlderLocal && w != null && l != null && w.CompareTo(l) > 0)
                local.state = ModLoadState.DisabledDuplicate;
            else
                workshop.state = ModLoadState.DisabledDuplicate;
        }
    }

    // The game's sort on a made-up list in the order it reads the mods, as ModManager.SortModList plays it (its decompiled
    // code, mirrored): those still to load (None), each waiting for the first of them with each id it needs; Kahn's
    // algorithm over .NET's PriorityQueue, keyed by each id's last entry in the saved list (none: 999999999); a cycle's mods
    // after the rest, in read order; then the mods it doesn't load (off, or set aside by its rule), in read order.
    static List<Mod> SortModList(IReadOnlyList<Mod> read, IReadOnlyList<SettingsSaveMod> saved)
    {
        var list = read.Where(m => m.state == ModLoadState.None).ToList();
        var waiting = new List<int>();
        var dependents = new Dictionary<Mod, List<Mod>>();
        foreach (var mod in list)
        {
            var count = 0;
            foreach (var dependency in mod.manifest?.dependencies ?? [])
                if (list.FirstOrDefault(m => m.manifest?.id == dependency.id) is { } found)
                {
                    count++;
                    if (!dependents.TryGetValue(found, out var them))
                        dependents[found] = them = [];
                    them.Add(mod);
                }
            waiting.Add(count);
        }
        var rank = new Dictionary<string, int>();
        for (var i = 0; i < saved.Count; i++)
            rank[saved[i].Id] = i;
        int Rank(Mod mod) => rank.TryGetValue(mod.manifest?.id ?? "", out var at) ? at : 999999999;
        var ready = new PriorityQueue<Mod, int>();
        for (var i = 0; i < list.Count; i++)
            if (waiting[i] == 0)
                ready.Enqueue(list[i], Rank(list[i]));
        var sorted = new List<Mod>();
        while (ready.TryDequeue(out var mod, out _))
        {
            sorted.Add(mod);
            foreach (var next in dependents.GetValueOrDefault(mod) ?? [])
                if (--waiting[list.IndexOf(next)] == 0)
                    ready.Enqueue(next, Rank(next));
        }
        return [.. sorted, .. list.Where(m => !sorted.Contains(m)), .. read.Where(m => m.state != ModLoadState.None)];
    }

    // The rule on made-up copies: each case of the design's table, through the page's Copies.Next and the launch's
    // SameIdLoad.Run on the same copies (the game's local-vs-Workshop rule played first, GameRule), on this branch's rule
    // (the beta's or stable's): both name the same copy, and the losing ticked copies get their reasons. Then the version
    // reading (P5; letters straight after a number, N10), the game's own reading, a Mod's reading kept, the order the
    // game reads local copies in (FirstRead, against Godot's own sort on real folders), the guard for a mod that needs a
    // minimum version (N4), and the fallback where the next launch can't swap a copy in (P7), each with such a mod
    // installed while the game runs too; a copy with two manifests of different versions (NewestOf); and the page after a
    // launch, by the order the next launch sorts in (made up by the game's sort, mirrored, SortModList, which the page's
    // model of it matches before the slot): the fallback it names (a dependency switched off since, a Workshop item with
    // two manifests, and on the beta a ticked copy the game's rule set aside), whether the copy blocked loads next time
    // (its dependency then first, or off, or waiting on one installed since, or moved; one installed from both sources,
    // blocked at every launch), no other ticked copy that fits, the game's rule leaving one copy, who counts as needing
    // the id, which copy of an id a mod waits for, a fallback needing a mod that isn't installed, the fixture's twins with
    // Test Framework off; then none that fits (the tie order), a fallback below a minimum version (at it, a needer off, a
    // version the game can't read), the fallback's copies still to load first, a needer the next launch brings back from
    // the game's rule, on the beta a copy's two manifests in two folders and a fallback with both sources left, a copy's
    // two manifests in one folder, who needs a minimum version, a needer's update (installed once, and twice), and a
    // fallback where a mod needs a minimum version the game can't read.
    static void TestCopyRule(string outDir)
    {
        var beta = Compat.WorkshopBeatsOlderLocal;
        var us = Made(ModEntry.ModId, "SpireModManager");
        var cases = 0;
        string Id() => $"SmmRule{++cases}";
        static string Shown(Mod? mod) => mod == null ? "none" : Copies.Shown(Copies.CopyOf(mod));
        // What loads of these copies (one id), on the page and at a launch (the game's local-vs-Workshop rule played on them
        // and extra first), and why each ticked copy that doesn't load doesn't: off, copies (CopyOf) in copies_off; record,
        // our record of them, in its order; switchOff, sources whose switch is off; extra, mods after them in the list
        // (dependents), extraOff: whether their switches are off; saved: the saved list the next launch sorts by (its
        // entries that are off are off too); launch: false where the page alone is asked (its Launch then the page's);
        // redraw: the page asked inside a redraw of its own lists (Lookups, the switches from saved alone), which reads who
        // needs whom from its own table. Late: what blocks the copy Blocked.
        (string Page, string Launch, string Why, string? Late) Case(Mod[] copies, string[]? off = null, string[]? record = null, ModSource[]? switchOff = null,
            Mod[]? extra = null, bool extraOff = false, List<SettingsSaveMod>? saved = null, bool launch = true, bool redraw = false)
        {
            var id = copies[0].manifest?.id ?? "";
            var gameOff = (switchOff ?? []).Select(s => ((string?)id, s))
                .Concat(extraOff ? (extra ?? []).Select(m => (m.manifest?.id, m.modSource)) : []).Concat(Dependencies.SavedOff(saved ?? [])).ToHashSet();
            Ticks Ticks() => new(off?.Select(c => $"{id}|{c}"), record == null ? null : new Dictionary<string, List<string>> { [id] = [.. record] });
            var listed = copies.Concat(extra ?? []).ToList();
            GameMods().AddRange(listed);
            Copies.TestRecount();
            Copies.Outcome page;
            try
            {
                if (redraw)
                {
                    using var lookups = Lookups.Begin(ModManager.Mods, Installed.Mods, saved ?? [], Ticks());
                    page = Copies.Next(id);
                }
                else
                    page = Copies.Next(id, gameOff, Ticks(), saved);
            }
            finally
            {
                foreach (var made in listed)
                    GameMods().Remove(made);
                Copies.TestRecount();
            }
            var why = string.Join(", ", page.Losing.Select(p => $"{Copies.Shown(p.Key)} {p.Value}").Order(StringComparer.Ordinal));
            if (!launch)
                return (Shown(page.Loads), Shown(page.Loads), why, page.Late);
            GameRule(listed, gameOff);
            var plan = SameIdLoad.Run(new List<Mod>([us, .. listed]), Ticks, m => m == us, log: false).Plan.GetValueOrDefault(id);
            return (Shown(page.Loads), Shown(plan?.Loads ?? copies.SingleOrDefault(m => m.state == ModLoadState.None)), why, page.Late);
        }
        void Row(string name, (string Page, string Launch, string Why, string? Late) got, string loads, string why) => Check($"the rule, {name}: {loads} loads"
            + (why.Length > 0 ? $" ({why})" : ""), got.Page == loads && got.Launch == loads && got.Why == why,
            $"page {got.Page}, launch {got.Launch}; losing {(got.Why.Length > 0 ? got.Why : "none")}");
        Mod L(string id, string folder, string version) => Made(id, folder, version: version);
        Mod W(string id, ulong item, string version) => Made(id, $"W{item}", item: item, version: version);
        static SettingsSaveMod Entry(string id, ModSource source = ModSource.ModsDirectory, bool on = true) => new() { Id = id, Source = source, IsEnabled = on };
        static Mod NeedsAt(string id, string folder, string needs, string min)
        {
            var made = Made(id, folder);
            if (made.manifest is { } manifest)
                manifest.dependencies = [new ModDependency(needs, min)];
            return made;
        }

        var x = Id();
        Row("one copy ticked of two, the other higher but off", Case([L(x, "A", "1.0.0"), L(x, "B", "2.0.0")], off: ["l:B"], record: ["l:A", "l:B"]), "A", "");
        x = Id();
        Row("two of one source ticked: the higher version", Case([L(x, "A", "1.0.0"), L(x, "B", "1.1.0")], record: ["l:A", "l:B"]), "B", "A Higher");
        x = Id();
        Row("two of one source ticked, equal versions (\"1.2\" reads as 1.2.0): the copy installed first, our record's first",
            Case([L(x, "A", "1.2"), L(x, "B", "1.2.0")], record: ["l:B", "l:A"]), "B", "A First");
        x = Id();
        Row("... with the record the other way round", Case([L(x, "A", "1.2"), L(x, "B", "1.2.0")], record: ["l:A", "l:B"]), "A", "B First");
        x = Id();
        Row("... one of them not in the record: the one it has", Case([L(x, "A", "1.0.0"), L(x, "B", "1.0.0")], record: ["l:B", "l:Gone"]), "B", "A First");
        x = Id();
        Row("... two Workshop items in no record: the older item", Case([W(x, 300, "1.0.0"), W(x, 200, "1.0.0")]), "200", "300 First");
        x = Id();
        Row("a version that can't be read against one that can: the readable one", Case([L(x, "A", "abc"), L(x, "B", "0.0.1")]), "B", "A Unreadable");
        x = Id();
        Row("none readable: the first in the record", Case([L(x, "A", "abc"), L(x, "B", "beta")], record: ["l:B", "l:A"]), "B", "A First");
        x = Id();
        Row("\"1.2\" against \"1.1.0\": read leniently, the higher", Case([L(x, "A", "1.2"), L(x, "B", "1.1.0")]), "A", "B Higher");
        x = Id();
        Row("a release above its dev build", Case([L(x, "A", "0.1.5-dev.9"), L(x, "B", "0.1.5")]), "B", "A Higher");
        x = Id();
        Row("dev.10 above dev.9", Case([L(x, "A", "0.1.5-dev.10"), L(x, "B", "0.1.5-dev.9")]), "A", "B Higher");
        x = Id();
        Row("\"1.2.25(beta)\" below \"1.2.25\"", Case([L(x, "A", "1.2.25(beta)"), L(x, "B", "1.2.25")]), "B", "A Higher");
        x = Id();
        Row("every copy the record knows off (the ticked one missing): they all read ticked", Case([L(x, "A", "1.0.0"), L(x, "B", "1.1.0")],
            off: ["l:A", "l:B"], record: ["l:Gone", "l:A", "l:B"]), "B", "A Higher");
        x = Id();
        Row("an off key for a copy the record doesn't have (a hand edit) doesn't count", Case([L(x, "A", "1.0.0"), L(x, "C", "1.1.0")],
            off: ["l:C"], record: ["l:A", "l:Gone"]), "C", "A Higher");
        x = Id();
        Row($"local and Workshop both ticked, the Workshop copy higher ({(beta ? "the beta: it" : "stable: the local copy")})",
            Case([L(x, "L", "1.0.0"), W(x, 101, "2.0.0")]), beta ? "101" : "L", beta ? "L Higher" : "101 Local");
        x = Id();
        Row("local and Workshop both ticked, the local copy higher", Case([L(x, "L", "2.0.0"), W(x, 101, "1.0.0")]), "L", beta ? "101 Higher" : "101 Local");
        x = Id();
        Row("local and Workshop both ticked, a version the game can't read (\"1.2\"): the local copy", Case([L(x, "L", "1.2"), W(x, 101, "1.3.0")]), "L", "101 Local");
        x = Id();
        Row("a local copy and two Workshop items both higher, all ticked" + (beta ? ": the game takes out the local one, then the higher of the two" : ""),
            Case([L(x, "L", "1.0.0"), W(x, 101, "2.0.0"), W(x, 102, "3.0.0")]), beta ? "102" : "L", beta ? "101 Higher, L Higher" : "101 Local, 102 Local");
        x = Id();
        Row("a local copy, a lower Workshop item and a higher one, all ticked" + (beta ? " (the lower one lost to the local copy, which doesn't load: no reason)" : ""),
            Case([L(x, "L", "2.0.0"), W(x, 101, "1.0.0"), W(x, 102, "3.0.0")]), beta ? "102" : "L", beta ? "101 Other, L Higher" : "101 Local, 102 Local");
        x = Id();
        // A copy that's off never loads nor stops another from loading (Jordan, 23 Oct): on the beta the game's rule lets W2
        // beat L through W1's switch, so the launch brings L back (the rule over the ticked copies alone).
        Row("W1 ticked (1.0), W2 not (3.0), L ticked (2.0)" + (beta ? ": W2 off doesn't count, so L, as the rule goes without it (W2 shares W1's switch and beats L)" : ""),
            Case([W(x, 101, "1.0.0"), W(x, 102, "3.0.0"), L(x, "L", "2.0.0")], off: ["w:102"], record: ["w:101", "w:102", "l:L"]),
            "L", beta ? "101 Higher" : "101 Local");
        // With a ticked copy left by the game's rule, a copy that's off still mustn't decide which one: the rule over the
        // ticked copies alone, here too.
        x = Id();
        Row("W1 ticked (1.0), W2 not (3.0), L1 ticked (2.0, read first), L2 ticked (1.5)" + (beta ? ": without W2, L1 is left beside L2, and the higher loads" : ""),
            Case([W(x, 101, "1.0.0"), W(x, 102, "3.0.0"), L(x, "L1", "2.0.0"), L(x, "L2", "1.5.0")], off: ["w:102"], record: ["w:101", "w:102", "l:L1", "l:L2"]),
            "L1", beta ? "101 Higher, L2 Higher" : "101 Local, L2 Higher");
        x = Id();
        Row("L1 not ticked (3.0, read first), L2 ticked (1.0), W1 ticked (1.5)" + (beta ? ": without L1, W1 beats L2" : ""),
            Case([L(x, "L1", "3.0.0"), L(x, "L2", "1.0.0"), W(x, 101, "1.5.0")], off: ["l:L1"], record: ["l:L1", "l:L2", "w:101"]),
            beta ? "101" : "L2", beta ? "L2 Higher" : "101 Local");
        // The rule over the ticked copies leaves both sources (the beta: W beats B, C is left): the first ticked copy's source
        // decides, in the launch's order, the copies the game's rule leaves (still to load) before those it beat, whatever
        // the session's list order (W first here).
        x = Id();
        Row("W ticked (1.5, first in the list), A not ticked (3.0, read first), B ticked (1.0), C ticked (1.2)"
            + (beta ? ": both sources left, the local one first as the launch has it, so C" : ""),
            Case([W(x, 101, "1.5.0"), L(x, "A", "3.0.0"), L(x, "B", "1.0.0"), L(x, "C", "1.2.0")], off: ["l:A"], record: ["l:A", "l:B", "l:C", "w:101"]),
            "C", beta ? "101 Other, B Other" : "101 Local, B Higher");
        // The page reads the copies in the next launch's order (the plan's Order), not this session's, which Apply's swap
        // changed (the beta: L2 swapped ahead of W2 across sources). Every copy ticked: W2 beats L1 (read first), W1 loses
        // to it, and of W2 and L2 left the first decides the source: W2, as the next launch has it too. The launch: the
        // game's rule in its read order (the mods folder first, L1 before L2), then Run over the list as its sort leaves
        // it (L1 and W1, set aside, after W2 and L2), which loads W2 and sets L2 aside.
        if (beta)
        {
            x = Id();
            var (pw2, pl2, pl1, pw1) = (W(x, 102, "3.0.0"), L(x, "L2", "1.5.0"), L(x, "L1", "2.0.0"), W(x, 101, "1.0.0"));
            SameIdLoad.TestPlan(x, new SameIdLoad.Entry(pl2, [pw2], true, 2, 1, SameIdLoad.Reason.None, 0, Order: ["w:102", "l:L2", "l:L1", "w:101"]));
            string page;
            try
            {
                page = PageOf([pl2, pw2, pl1, pw1]);
            }
            finally
            {
                SameIdLoad.TestPlan(x, null);
            }
            var (lw2, ll2, ll1, lw1) = (W(x, 102, "3.0.0"), L(x, "L2", "1.5.0"), L(x, "L1", "2.0.0"), W(x, 101, "1.0.0"));
            GameRule([ll1, ll2, lw2, lw1], []);
            var ruled = (ll1.state, ll2.state, lw2.state, lw1.state);
            var launched = SameIdLoad.Run(new List<Mod> { us, lw2, ll2, ll1, lw1 }, () => Ticks.All, m => m == us, log: false).Plan.GetValueOrDefault(x);
            Check("the rule, ... the page in the next launch's order (the plan's), not this session's swapped one: W2, as the launch, where the game's rule "
                + "leaves W2 and L2 (L1 and W1 set aside) and Run sets L2 aside",
                page == "102" && launched is { SetAside: [var dup] } && launched.Loads == lw2 && dup == ll2
                && ruled == (ModLoadState.DisabledDuplicate, ModLoadState.None, ModLoadState.None, ModLoadState.DisabledDuplicate),
                $"page {page}, launch {Shown(launched?.Loads)}, set aside {string.Join(",", launched?.SetAside.Select(Shown) ?? [])}; "
                + $"the game's rule left L1 {ruled.Item1}, L2 {ruled.Item2}, W2 {ruled.Item3}, W1 {ruled.Item4}");
        }
        // An id the launch couldn't choose for (its self-check: not taken): the game's own choice, the copy that loaded this
        // session only where the next launch can load it again. Here the Workshop copy loaded, and the local copy, higher,
        // beats it next launch.
        x = Id();
        SameIdLoad.TestResult(x, SameIdLoad.Result.NotTaken);
        try
        {
            Row("an id the launch couldn't choose for, its Workshop copy (1.0) loaded this session, the local copy (2.0) on: the local copy, which beats it next launch",
                Case([Made(x, "W101", ModLoadState.Loaded, item: 101, version: "1.0.0"), L(x, "L", "2.0.0")], launch: false), "L", beta ? "101 Higher" : "101 Local");
        }
        finally
        {
            SameIdLoad.TestResult(x, null);
        }
        // The same after a swap the self-check found not taken: Apply swapped B (2.0) into A's slot, the game loaded A there
        // all the same and failed B as its duplicate. The next launch's sort ranks the two alike, so the game's choice is A
        // again: the page names A, not B, first in the list since the swap. And where a first copy fails for its own reason
        // (A needing a mod that isn't installed, no choice made, as where the canary failed: the verdict stands in), the
        // game's loop loads the next, B, as it does next launch. Both fail on 8ff2e3d, whose page took the first of the id's
        // copies in the next launch's order.
        x = Id();
        SameIdLoad.TestResult(x, SameIdLoad.Result.NotTaken);
        try
        {
            var (swapped, kept) = (Made(x, "B", ModLoadState.Failed, version: "2.0.0"), Made(x, "A", ModLoadState.Loaded));
            SameIdLoad.TestPlan(x, new SameIdLoad.Entry(swapped, [kept], true, 2, 1, SameIdLoad.Reason.None, 0, Order: ["l:A", "l:B"]));
            Row("an id the launch couldn't choose for after a swap (B swapped into A's slot, A loaded there, B failed as a duplicate): A, the copy that loaded",
                Case([swapped, kept], launch: false), "A", "B Other");
        }
        finally
        {
            SameIdLoad.TestPlan(x, null);
            SameIdLoad.TestResult(x, null);
        }
        x = Id();
        SameIdLoad.TestResult(x, SameIdLoad.Result.NotTaken);
        try
        {
            Row("an id the launch couldn't choose for, its first copy failed for its own reason (A needing a mod that isn't installed): B, which loaded after it",
                Case([Made(x, "A", ModLoadState.Failed, needs: [$"{x}Missing"]), Made(x, "B", ModLoadState.Loaded)], launch: false), "B", "A Other");
        }
        finally
        {
            SameIdLoad.TestResult(x, null);
        }
        if (beta)
        {
            // At the launch, as the game leaves them (sorted, its rule played: W1 and L set aside after the mods still to
            // load): L brought back into W2's slot, so mods that need the id find it there, W2 set aside, W1 left aside.
            x = Id();
            var (w1, w2, l, needer) = (W(x, 101, "1.0.0"), W(x, 102, "3.0.0"), L(x, "L", "2.0.0"), Made("SmmRuleNeedsBack", "NeedsBack"));
            if (needer.manifest is { } neederManifest)
                neederManifest.dependencies = [new ModDependency(x, null)];
            GameRule([w1, w2, l], []);
            var launch = new List<Mod> { us, w2, needer, l, w1 }; // its sort: the copies it beat last, in its read order (local first)
            var ticks = new Ticks([$"{x}|w:102"], new Dictionary<string, List<string>> { [x] = ["w:101", "w:102", "l:L"] });
            var plan = SameIdLoad.Run(launch, () => ticks, m => m == us, log: false).Plan;
            var run = plan.GetValueOrDefault(x);
            Check("the rule, ... at the launch: L brought back into W2's slot (before the mod that needs the id), W2 and W1 set aside",
                run?.Loads == l && l.state == ModLoadState.None && launch[1] == l && launch[3] == w2 && w2.state == ModLoadState.DisabledDuplicate
                && w1.state == ModLoadState.DisabledDuplicate && run.Swapped,
                $"plan {(run == null ? "none" : $"{Shown(run.Loads)}, swapped {run.Swapped}")}; list {string.Join(", ", launch.Select(m => $"{Shown(m)} {m.state}"))}");
            // The launch record (the last working setup Restore goes back to): of the Workshop copies, none of which loads,
            // the ticked W1, not W2 (off, first in the list since the swap), so a Restore can't turn W2 back on.
            var recorded = SameIdLoad.LoadsAtLaunch(launch, plan).GetValueOrDefault((x, ModSource.SteamWorkshop));
            Check("the rule, ... the launch record's Workshop copy: W1, the one Apply didn't set aside, not W2 (off)", recorded == "w:101", recorded ?? "none");
        }
        x = Id();
        Row("two local copies and a newer Workshop one, all ticked" + (beta ? ": the game takes out the first local copy read; the first ticked left decides the source" : ""),
            Case([L(x, "L1", "1.0.0"), L(x, "L2", "1.5.0"), W(x, 101, "2.0.0")]), "L2", beta ? "101 Other, L1 Other" : "101 Local, L1 Higher");
        // N4: a mod that loads needs the id with a minimum version: a copy the game can't read ranks below one it can.
        x = Id();
        var needs = Made("SmmRuleNeeds", "Needs");
        if (needs.manifest is { } needsManifest)
            needsManifest.dependencies = [new ModDependency(x, "1.0.0")];
        Row("a lenient version higher, and a mod on that needs the id with a minimum version: the one the game can read",
            Case([L(x, "A", "1.3"), L(x, "B", "1.2.0")], extra: [needs]), "B", "A Unreadable");
        x = Id();
        Row("... with no such mod: the higher", Case([L(x, "A", "1.3"), L(x, "B", "1.2.0")]), "A", "B Higher");
        // Such a mod installed while the game runs: the game reads it into its own list (ModManager.Mods, at AddedAtRuntime;
        // its OnSteamWorkshopItemInstalled, on both branches), so who needs the id on the page (Dependencies.NeededBy, and a
        // redraw's Lookups) has it, as the next launch has it as a mod after us still to load.
        x = Id();
        var arrived = Made("SmmRuleArrived", "Arrived", ModLoadState.AddedAtRuntime);
        if (arrived.manifest is { } arrivedManifest)
            arrivedManifest.dependencies = [new ModDependency(x, "1.0.0")];
        Mod[] Lenient() => [L(x, "A", "1.3"), L(x, "B", "1.2.0")];
        var (outside, inside) = (Case(Lenient(), extra: [arrived], launch: false), Case(Lenient(), extra: [arrived], launch: false, redraw: true));
        arrived.state = ModLoadState.None; // the next launch
        var nextLaunch = Case(Lenient(), extra: [arrived]).Launch;
        Check("the rule, ... such a mod installed while the game runs (in the game's own list): the page counts it, in a redraw and out, as the next launch "
            + "does: B loads (A Unreadable)", outside is ("B", "B", "A Unreadable", null) && inside == outside && nextLaunch == "B",
            $"page {outside}, in a redraw {inside}; the next launch {nextLaunch}");

        // One copy with two manifests of the id at launch (a Workshop item with one in two of its folders), 1.0.0 and 2.0.0,
        // beside another item at 1.5.0, all ticked, in either order in the list: the page and the launch both read the
        // copy's higher manifest, so its item loads, the launch loading that manifest and setting the other aside; and the
        // page, reading the list as the launch's swap left it, still names it. An update while the game runs that lowers the
        // version (a Mod added after the one found at launch) decides by its own, the lower.
        string PageOf(IEnumerable<Mod> mods)
        {
            var listed = mods.ToList();
            GameMods().AddRange(listed);
            Copies.TestRecount();
            try
            {
                return Shown(Copies.Next(listed[0].manifest?.id ?? "", [], Ticks.All).Loads);
            }
            finally
            {
                foreach (var made in listed)
                    GameMods().Remove(made);
                Copies.TestRecount();
            }
        }
        var twoManifests = new List<string>();
        var twoRead = true;
        foreach (var higherFirst in new[] { false, true })
        {
            x = Id();
            var (older, newer, other) = (Made(x, "W1a", item: 1, version: "1.0.0"), Made(x, "W1b", item: 1, version: "2.0.0"), W(x, 2, "1.5.0"));
            List<Mod> list = higherFirst ? [us, newer, other, older] : [us, older, other, newer];
            var page = PageOf(list.Skip(1));
            var plan = SameIdLoad.Run(list, () => Ticks.All, m => m == us, log: false).Plan.GetValueOrDefault(x);
            var after = PageOf(list.Skip(1));
            twoRead &= page == "1" && plan?.Loads == newer && newer.state == ModLoadState.None && older.state == ModLoadState.DisabledDuplicate && after == "1";
            twoManifests.Add($"{(higherFirst ? "higher" : "lower")} first: page {page}, launch {Shown(plan?.Loads)} {plan?.Loads?.manifest?.version} "
                + $"(the other {older.state}), after it {after}");
        }
        x = Id();
        var rolledBack = PageOf([Made(x, "W1", ModLoadState.Loaded, 1, "2.0.0"), Made(x, "W2", ModLoadState.DisabledDuplicate, 2, "1.5.0"),
            Made(x, "W1", ModLoadState.AddedAtRuntime, 1, "1.0.0")]);
        twoManifests.Add($"updated to a lower version while the game runs: {rolledBack}");
        Check("the rule, one copy with two manifests of the id at launch (1.0.0, 2.0.0) beside another copy (1.5.0), in either order: the page and the launch "
            + "read its higher manifest, the launch loads that one and sets the other aside, and after its swap the page still names the copy; an update "
            + "while the game runs decides by its own version", twoRead && rolledBack == "2", string.Join("; ", twoManifests));

        // P7: the next launch can't put B, chosen, in the id's first slot (what it needs sorts after the slot there) while a
        // mod that needs the id is on: the fallback, A, with no plan of this launch's to go by; a higher copy ticked, or the
        // mod needing it off, as normal. B needs D, installed from both sources with its Workshop copy off: the game lists
        // the mods that are off last, so D's last entry, which it ranks D by, is after the id's at every launch.
        x = Id();
        var user = Made("SmmRuleUser", "User", needs: [x]);
        {
            var dep = $"{x}D";
            Mod[] Two() => [L(x, "A", "1.0.0"), Made(x, "B", version: "1.1.0", needs: [dep])];
            Mod[] Needed(Mod needer) => [needer, Made(dep, "D"), Made(dep, "DW", ModLoadState.Disabled, item: 9)];
            List<SettingsSaveMod> Saved(bool userOn = true) => [Entry(x), Entry(x), Entry("SmmRuleUser", on: userOn), Entry(dep), Entry(dep, ModSource.SteamWorkshop, false)];
            var blocked = Case(Two(), extra: Needed(user), saved: Saved(), launch: false);
            Row("the copy chosen needing a mod the next launch sorts after the id's first slot (listed last through its Workshop copy, off), on the page: "
                + "the fallback, A", blocked, "A", "B Blocked");
            Check("the rule, ... the page names what blocks B: D", blocked.Late == dep, blocked.Late ?? "nothing");
            Row("... a higher copy ticked since: that one, as normal", Case([.. Two(), L(x, "C", "1.2.0")], extra: Needed(user), saved: Saved(), launch: false),
                "C", "A Higher, B Higher");
            Row("... the mod that needs the id off since: as normal", Case(Two(), extra: Needed(user), saved: Saved(userOn: false), launch: false), "B", "A Higher");
            // Another mod that needs it installed while the game runs, in the game's own list (not the saved one): blocked again.
            var arrivedUser = Made("SmmRuleArrivedUser", "ArrivedUser", ModLoadState.AddedAtRuntime, needs: [x]);
            List<SettingsSaveMod> unlisted = [Entry(x), Entry(x), Entry(dep), Entry(dep, ModSource.SteamWorkshop, false)];
            var (inRedraw, outOfOne) = (Case(Two(), extra: Needed(arrivedUser), saved: unlisted, launch: false, redraw: true),
                Case(Two(), extra: Needed(arrivedUser), saved: unlisted, launch: false));
            Check("the rule, ... the mod that needed it gone, another needing it installed while the game runs: the fallback again, in a redraw and out: "
                + "A loads (B Blocked)", inRedraw is ("A", "A", "B Blocked", _) && outOfOne == inRedraw, $"in a redraw {inRedraw}, out of one {outOfOne}");
            // The same with a saved list that ranks D before the id: D sorts first, so B, in a redraw and out (the redraw's
            // order from its own saved list: the game's, which has none of these, ranks them all last, and D after A).
            List<SettingsSaveMod> depFirst = [Entry(dep), Entry(dep, ModSource.SteamWorkshop, false), Entry(x), Entry(x)];
            var (firstInRedraw, firstOutOfOne) = (Case(Two(), extra: Needed(arrivedUser), saved: depFirst, launch: false, redraw: true),
                Case(Two(), extra: Needed(arrivedUser), saved: depFirst, launch: false));
            Check("the rule, ... the same with a saved list that ranks D before the id: D sorts first, so B, in a redraw and out: B loads (A Higher)",
                firstInRedraw is ("B", "B", "A Higher", null) && firstOutOfOne == firstInRedraw, $"in a redraw {firstInRedraw}, out of one {firstOutOfOne}");
        }

        // The page, worked out as the next launch goes (its order, SameIdLoad.Fallback), by the switches (off) and ticks now
        // and the saved list the next launch sorts by, with these mods in the game's list (session: as this launch left
        // them): what it names, why each ticked copy that doesn't load doesn't, what blocks the copy Blocked (Late), and the
        // made-up ids the page's model of the next launch's order (Dependencies.NextOrder) sorts before the id's first slot.
        (string Loads, string Why, string? Late, HashSet<string?> Before) Page(string id, List<Mod> session, HashSet<(string?, ModSource)> off, Ticks ticks,
            List<SettingsSaveMod> saved)
        {
            try
            {
                GameMods().AddRange(session);
                Copies.TestRecount();
                var page = Copies.Next(id, off, ticks, saved);
                return (Shown(page.Loads), string.Join(", ", page.Losing.Select(p => $"{Copies.Shown(p.Key)} {p.Value}").Order(StringComparer.Ordinal)), page.Late,
                    Before(Dependencies.NextOrder(off, saved), id, session));
            }
            finally
            {
                foreach (var made in session)
                    GameMods().Remove(made);
                Copies.TestRecount();
            }
        }
        // Of these mods' ids (ours aside), those an order has before the id's first slot.
        static HashSet<string?> Before(IEnumerable<Mod> order, string id, IEnumerable<Mod> made) =>
            [.. order.Select(m => m.manifest?.id).TakeWhile(each => each != id).Intersect(made.Select(m => m.manifest?.id).Where(each => each != ModEntry.ModId))];
        // The page after a launch: the launch is Run on a made-up list (Launch); the session is that list as the launch left
        // it, its swap made (and any mod installed since, AddedAtRuntime), with the launch's plan; the saved list, else as
        // the game rebuilds it from the launch's list.
        SameIdLoad.Entry? Launch(string id, List<Mod> list, Ticks? ticks = null) =>
            SameIdLoad.Run(list, () => ticks ?? Ticks.All, m => m == us, log: false).Plan.GetValueOrDefault(id);
        (string Loads, string Why, string? Late, HashSet<string?> Before) After(List<Mod> launched, SameIdLoad.Entry? plan, HashSet<(string?, ModSource)> off,
            Ticks ticks, List<SettingsSaveMod>? saved = null)
        {
            if (plan == null)
                return ("no plan", "", null, []);
            var id = launched[plan.To].manifest?.id ?? "";
            SameIdLoad.TestPlan(id, plan);
            try
            {
                return Page(id, [.. launched.Where(m => m != us)], off, ticks, saved ?? Rebuilt(launched, off));
            }
            finally
            {
                SameIdLoad.TestPlan(id, null);
            }
        }
        // The saved list as the game rebuilds it from a launch's list, in the order that loaded (Settle saves it; a mod
        // installed since has no entry yet), with the switches now.
        static List<SettingsSaveMod> Rebuilt(IEnumerable<Mod> launched, HashSet<(string?, ModSource)> off) => [.. launched.Where(m => m.state != ModLoadState.AddedAtRuntime)
            .Select(m => new SettingsSaveMod(m) { IsEnabled = !off.Contains((m.manifest?.id, m.modSource)) })];
        // The next launch, made up as the game makes it: fresh mods in the order it reads them (read, ours among them), the
        // switches off Disabled and its local-vs-Workshop rule played (GameRule), sorted from the saved list (SortModList),
        // then Run on that: the copy of the id that loads, the plan, the made-up ids the sort has before the id's first slot,
        // which the page's model of it has to have too, and the list as that launch leaves it.
        (Mod? Loads, SameIdLoad.Entry? Plan, HashSet<string?> Before, List<Mod> Sorted) NextLaunch(string id, List<Mod> inReadOrder, List<SettingsSaveMod> saved,
            HashSet<(string?, ModSource)> off, Ticks? ticks = null)
        {
            GameRule(inReadOrder, off);
            var sorted = SortModList(inReadOrder, saved);
            var plan = Launch(id, sorted, ticks);
            return (plan?.Loads ?? sorted.FirstOrDefault(m => m.manifest?.id == id && m.state == ModLoadState.None), plan, Before(sorted, id, inReadOrder), sorted);
        }
        string Rule(SameIdLoad.Entry? plan, (string Loads, string Why, string? Late, HashSet<string?> Before) page,
            (Mod? Loads, SameIdLoad.Entry? Plan, HashSet<string?> Before, List<Mod> Sorted) next) =>
            $"this launch {Shown(plan?.Loads)}, blocked {plan?.Blocked ?? "none"}; the page {page.Loads} ({page.Why}), blocked by {page.Late ?? "nothing"}, before the "
            + $"slot {string.Join(",", page.Before.Order())}; the next launch {Shown(next.Loads)}, before the slot {string.Join(",", next.Before.Order())}";
        // A copy that fit at the launch, needing a mod switched off since: off, that mod doesn't load, so the copy can't take
        // the slot next launch, and the page names the next one, as that launch loads. A 1.0, C 2.0 needing D, B 3.0 needing
        // E (listed after the id: after the slot), a mod needing the id. The launch: B blocked by E, C fits (D loads before
        // the slot), so C. Since: D off, and E needing F, installed while the game runs (no saved entry yet: the next launch
        // ranks it after every listed mod, so E after the slot again). A, on the page and at the next launch. Its first form
        // (E late through its Workshop copy, off, listed last) failed on 237b64b, whose page's Fits went by D's place this
        // session alone, so C fit and the page named C.
        x = Id();
        {
            var (dep, late, lib) = ($"{x}D", $"{x}E", $"{x}F");
            Mod[] Three() => [L(x, "A", "1.0.0"), Made(x, "B", version: "3.0.0", needs: [late]), Made(x, "C", version: "2.0.0", needs: [dep])];
            Mod User() => Made($"{x}User", "User", needs: [x]);
            var thisLaunch = Three();
            List<Mod> atLaunch = [us, Made(dep, "D"), thisLaunch[0], thisLaunch[2], User(), Made(late, "E", needs: [lib]), thisLaunch[1]];
            var plan = Launch(x, atLaunch);
            HashSet<(string?, ModSource)> off = [(dep, ModSource.ModsDirectory)];
            var page = After([.. atLaunch, Made(lib, "F", ModLoadState.AddedAtRuntime)], plan, off, Ticks.All);
            var next = NextLaunch(x, [.. Three(), Made(dep, "D"), Made(late, "E", needs: [lib]), Made(lib, "F"), us, User()], Rebuilt(atLaunch, off), off);
            Check("the rule, ... after such a launch, the copy that fit there needing a mod switched off since (it doesn't load): the next copy that fits, on "
                + "the page and at the next launch: A (B Blocked, C Other)",
                plan?.Loads == thisLaunch[2] && page is ("A", "B Blocked, C Other", _, _) && Shown(next.Loads) == "A" && page.Before.SetEquals(next.Before),
                Rule(plan, page, next));
        }
        // A Workshop item with two manifests of the id, the one at the slot needing nothing, the newer one a mod that loads
        // after the slot: item 1 with A1 1.0 and A2 2.0 needing D, B 3.0 needing D, C 1.5, a mod needing the id, D sorted
        // after the slot (listed after the id; it needs E, installed while the game runs, which the next launch ranks after
        // every listed mod, so D after the slot there too). The launch reads item 1 by A2 (NewestOf), which can't take the
        // slot: B blocked, then not A, so C. The page the same. Its first form failed on 237b64b: its page's Fits let any copy
        // of the one at the slot fit, A2 too, so the page named A, which the launch set aside.
        x = Id();
        {
            var (dep, lib) = ($"{x}D", $"{x}E");
            var c = W(x, 3, "1.5.0");
            List<Mod> atLaunch = [us, Made(x, "W1a", item: 1, version: "1.0.0"), c, Made($"{x}User", "User", needs: [x]), Made(dep, "D", needs: [lib]),
                Made(x, "W1b", item: 1, version: "2.0.0", needs: [dep]), Made(x, "W2", item: 2, version: "3.0.0", needs: [dep])];
            var plan = Launch(x, atLaunch);
            var page = After([.. atLaunch, Made(lib, "E", ModLoadState.AddedAtRuntime)], plan, [], Ticks.All);
            Check("the rule, ... a Workshop item with two manifests, the one at the slot needing nothing, the newer one a mod after the slot: the item "
                + "read by the newer one, so the next copy, C, at the launch and on the page (1 Other, 2 Blocked)",
                plan is { Why: SameIdLoad.Reason.DependencyAfterSlot, Blocked: "w:2" } && plan.Loads == c && page is ("3", "1 Other, 2 Blocked", _, _),
                $"launch {Shown(plan?.Loads)}, blocked {plan?.Blocked ?? "none"}; the page {page.Loads} ({page.Why})");
        }
        // The beta: a ticked copy the game's rule set aside comes back as the fallback. W2 off (3.0) on W1's switch, W1
        // (2.5) needing E after the slot (E needs F, installed while the game runs: the next launch ranks F after every
        // listed mod, and E after it), L1 (2.0), a mod needing the id. The game's rule sets L1 aside (both Workshop copies
        // are higher); the rule over the ticked copies picks W1, blocked; without it, L1, brought back, at the launch and on
        // the page. Its first form failed on 237b64b: its page's pool was the ticked copies the game's rule left, W1 alone,
        // empty without W1, so the page named the game's own choice, W2, which is off. Stable takes out both Workshop copies
        // (L1 at once).
        if (beta)
        {
            x = Id();
            var (late, lib) = ($"{x}E", $"{x}F");
            var (w2, w1, l1) = (W(x, 182, "3.0.0"), Made(x, "W181", item: 181, version: "2.5.0", needs: [late]), L(x, "L1", "2.0.0"));
            GameRule([l1, w2, w1], []);
            // its sort: L1, set aside, last
            List<Mod> atLaunch = [us, w2, Made(late, "E", needs: [lib]), w1, Made($"{x}User", "User", needs: [x]), l1];
            var ticks = new Ticks([$"{x}|w:182"], new Dictionary<string, List<string>> { [x] = ["w:181", "w:182"] });
            var plan = Launch(x, atLaunch, ticks);
            var page = After([.. atLaunch, Made(lib, "F", ModLoadState.AddedAtRuntime)], plan, [], ticks);
            Check("the rule, ... the beta: W1 blocked, past W2, off, and L1, which the game's rule set aside, brought back, at the launch and on the page (181 Blocked)",
                plan is { Why: SameIdLoad.Reason.DependencyAfterSlot, Blocked: "w:181" } && plan.Loads == l1 && page is ("L1", "181 Blocked", _, _),
                $"launch {Shown(plan?.Loads)}, blocked {plan?.Blocked ?? "none"}; the page {page.Loads} ({page.Why})");
        }
        else
            Results.Add("SKIP the rule, a blocked copy's fallback past a copy that's off: only the beta's rule lets one beat a ticked copy (stable takes out "
                + "every Workshop copy beside a local one)");

        // Whether a copy blocked at the slot loads next time, by the order the next launch sorts in, not this launch's: A
        // 1.0, C 2.0 needing D, a mod needing the id. Each row has that next launch made up by the game's sort (NextLaunch,
        // from the order it reads the mods in and the saved list), which loads what the page names and has what the page's
        // model of it has before the slot.
        // (a) The common case: D on, sorted after the slot at the launch (the list it read ranked D after the id), so A. The
        // saved list has the order that loaded, where the id's last entry is C's, after D: the next launch sorts D first and
        // loads C. Fails on bed319a: its page assumed the next launch sorts as this one did, so A.
        x = Id();
        {
            var dep = $"{x}D";
            Mod[] Two() => [L(x, "A", "1.0.0"), Made(x, "C", version: "2.0.0", needs: [dep])];
            Mod User() => Made($"{x}User", "User", needs: [x]);
            var thisLaunch = Two();
            List<Mod> atLaunch = [us, thisLaunch[0], User(), Made(dep, "D"), thisLaunch[1]];
            var plan = Launch(x, atLaunch);
            var page = After(atLaunch, plan, [], Ticks.All);
            var next = NextLaunch(x, [.. Two(), Made(dep, "D"), us, User()], Rebuilt(atLaunch, []), []);
            Check("the rule, ... (a) a copy blocked at the launch, its dependency on: the saved list ranks the id by that copy's entry, after the "
                + "dependency, so the next launch sorts the dependency first and loads it, on the page too: C (A Higher)",
                plan is { Why: SameIdLoad.Reason.DependencyAfterSlot, Blocked: "l:C" } && plan.Loads == thisLaunch[0] && page is ("C", "A Higher", null, _)
                && Shown(next.Loads) == "C" && page.Before.SetEquals(next.Before), Rule(plan, page, next));
        }
        // (b) The same launch, D switched off since: a dependency that's off doesn't stop the copy chosen (it loads and fails,
        // as a mod installed once does, and the red banner says so), so C, at the next launch whether its sort puts A or C in
        // the slot (C waits for nothing then). Fails on 32e3d69: its page and Run counted a dependency that's off as after
        // the slot (the game lists the mods that are off last), so the fallback, A.
        x = Id();
        {
            var dep = $"{x}D";
            Mod[] Two() => [L(x, "A", "1.0.0"), Made(x, "C", version: "2.0.0", needs: [dep])];
            Mod User() => Made($"{x}User", "User", needs: [x]);
            var thisLaunch = Two();
            List<Mod> atLaunch = [us, thisLaunch[0], User(), Made(dep, "D"), thisLaunch[1]];
            var plan = Launch(x, atLaunch);
            HashSet<(string?, ModSource)> off = [(dep, ModSource.ModsDirectory)];
            var page = After(atLaunch, plan, off, Ticks.All);
            var next = NextLaunch(x, [.. Two(), Made(dep, "D"), us, User()], Rebuilt(atLaunch, off), off);
            var tied = Two();
            var tiedPlan = Launch(x, [us, tied[1], tied[0], User(), Made(dep, "D", ModLoadState.Disabled)]);
            Check("the rule, ... (b) the same, the dependency switched off since: the copy chosen all the same, on the page and at the next launch, with "
                + "C sorted into the slot or after it: C (A Higher)",
                page is ("C", "A Higher", null, _) && Shown(next.Loads) == "C" && page.Before.SetEquals(next.Before)
                && tiedPlan is { Why: SameIdLoad.Reason.None, Swapped: false } && tiedPlan.Loads == tied[1],
                $"{Rule(plan, page, next)}; C in the slot: {Shown(tiedPlan?.Loads)}, why {tiedPlan?.Why}");
        }
        // (c) A reason the next launch has where this one had none: D waits on E, installed while the game runs (in the game's
        // list, with no saved entry yet: the next launch ranks it after every listed mod), so E and D sort after the slot
        // there. C was off at the launch and ticked since: the launch loaded A, no block, and the next launch blocks C, so A,
        // the page naming D (what C needs that sorts after the slot), not E. Its first form (E late through its Workshop
        // copy, off, listed last) failed on bed319a, whose page predicted a block only where this launch's plan had one, so C.
        x = Id();
        {
            var (dep, lib) = ($"{x}D", $"{x}E");
            Mod[] Two() => [L(x, "A", "1.0.0"), Made(x, "C", version: "2.0.0", needs: [dep])];
            Mod User() => Made($"{x}User", "User", needs: [x]);
            var thisLaunch = Two();
            List<Mod> atLaunch = [us, thisLaunch[0], User(), Made(dep, "D", needs: [lib]), thisLaunch[1]];
            var plan = Launch(x, atLaunch, new Ticks([$"{x}|l:C"], new Dictionary<string, List<string>> { [x] = ["l:A", "l:C"] }));
            var page = After([.. atLaunch, Made(lib, "E", ModLoadState.AddedAtRuntime)], plan, [], Ticks.All);
            var next = NextLaunch(x, [.. Two(), Made(dep, "D", needs: [lib]), Made(lib, "E"), us, User()], Rebuilt(atLaunch, []), []);
            Check("the rule, ... (c) a copy ticked since whose dependency waits on a mod installed while the game runs (no saved entry yet): the next "
                + "launch sorts both after the slot: the fallback, A (C Blocked, by D)",
                plan is { Why: SameIdLoad.Reason.None } && plan.Loads == thisLaunch[0] && page is ("A", "C Blocked", _, _) && page.Late == dep
                && Shown(next.Loads) == "A" && page.Before.SetEquals(next.Before), Rule(plan, page, next));
        }
        // (d) The Load Order page moving the id after its dependency: D listed last (its Workshop copy off, which the game's
        // rebuild lists after the rest), so C blocked at the launch and, unmoved, at the next one; the id's rows dragged
        // below D's since: D first, so C. Fails on bed319a: its page went by this launch's order unless the Load Order page's
        // history had a move (LoadOrder.OrderChanged, which a made-up list isn't), so A.
        x = Id();
        {
            var dep = $"{x}D";
            Mod[] Two() => [L(x, "A", "1.0.0"), Made(x, "C", version: "2.0.0", needs: [dep])];
            Mod User() => Made($"{x}User", "User", needs: [x]);
            var thisLaunch = Two();
            List<Mod> atLaunch = [us, thisLaunch[0], User(), Made(dep, "D"), thisLaunch[1], Made(dep, "DW", ModLoadState.Disabled, item: 7)];
            var plan = Launch(x, atLaunch);
            HashSet<(string?, ModSource)> off = [(dep, ModSource.SteamWorkshop)];
            var unmoved = After(atLaunch, plan, off, Ticks.All);
            List<SettingsSaveMod> moved = [Entry(ModEntry.ModId), Entry($"{x}User"), Entry(dep), Entry(dep, ModSource.SteamWorkshop, false), Entry(x), Entry(x)];
            var page = After(atLaunch, plan, off, Ticks.All, moved);
            var next = NextLaunch(x, [.. Two(), Made(dep, "D"), us, User(), Made(dep, "DW", item: 7)], moved, off);
            Check("the rule, ... (d) a copy blocked at the launch by a dependency listed last: the fallback, A, unmoved; the id moved below the dependency on "
                + "the Load Order page: the next launch sorts the dependency first, so C (A Higher)",
                plan is { Blocked: "l:C" } && unmoved is ("A", "C Blocked", _, _) && page is ("C", "A Higher", null, _) && Shown(next.Loads) == "C"
                && page.Before.SetEquals(next.Before), $"unmoved, the page {unmoved.Loads} ({unmoved.Why}); {Rule(plan, page, next)}");
        }
        // (f) The limit (DESIGN.md): a copy blocked by a dependency installed from both sources, one copy of it off or set
        // aside by the game's rule for the other (the local one, higher), stays blocked at every launch. The game's rebuild
        // lists the mods it took out after every other and ranks an id by its last entry, so D ranks after the id each time.
        // Three launches, each A, C blocked by D; the page after each names the same (the copy's line, COPY_BLOCKED, names D)
        // and has what that launch has before the slot.
        foreach (var how in new[] { "off", "set aside" })
        {
            x = Id();
            var dep = $"{x}D";
            Mod[] Two() => [L(x, "A", "1.0.0"), Made(x, "C", version: "2.0.0", needs: [dep])];
            // In the order the game reads them: the mods folder (by folder), then the Workshop's.
            List<Mod> Read() => [.. Two(), Made(dep, "D", version: "2.0.0"), us, Made($"{x}User", "User", needs: [x]), Made(dep, "DW", item: 13, version: "1.0.0")];
            HashSet<(string?, ModSource)> off = how == "off" ? [(dep, ModSource.SteamWorkshop)] : [];
            var saved = new List<SettingsSaveMod> { Entry(ModEntry.ModId), Entry(x), Entry(x), Entry($"{x}User"), Entry(dep),
                Entry(dep, ModSource.SteamWorkshop, how != "off") };
            var launches = new List<string>();
            var held = true;
            HashSet<string?>? foreseen = null; // what the page after the last launch had before the slot
            for (var launch = 1; launch <= 3; launch++)
            {
                var next = NextLaunch(x, Read(), saved, off);
                saved = Rebuilt(next.Sorted, off);
                var page = After(next.Sorted, next.Plan, off, Ticks.All, saved);
                held &= Shown(next.Loads) == "A" && next.Plan is { Blocked: "l:C" } && page is ("A", "C Blocked", _, _) && page.Late == dep
                    && (foreseen == null || foreseen.SetEquals(next.Before));
                foreseen = page.Before;
                launches.Add($"launch {launch}: {Shown(next.Loads)} (blocked {next.Plan?.Blocked ?? "none"}), before the slot {string.Join(",", next.Before.Order())}; "
                    + $"saved {string.Join(",", saved.Select(e => $"{e.Id}/{e.Source}"))}; the page after it {page.Loads} ({page.Why}, by {page.Late ?? "nothing"}), "
                    + $"before the slot {string.Join(",", page.Before.Order())}");
            }
            Check($"the rule, ... (f) the limit: a copy blocked by a dependency installed from both sources, its Workshop copy {how}: blocked at every launch, "
                + "the fallback, A, on the page after each (C Blocked, by D)", held, string.Join(" | ", launches));
        }
        // (e) No other ticked copy can take the slot: the first of the id's copies in the next launch's order that can, not
        // in this launch's. A 1.0 not ticked, C 2.0 ticked needing D, which was off at the launch (C loaded, from the slot)
        // and is on since, its entry listed last (as off, at the launch): C waits on D, so the next launch sorts A into the
        // slot, blocks C and loads A. Fails on 32e3d69: its page took the game's own choice from this launch's order, C first.
        x = Id();
        {
            var dep = $"{x}D";
            Mod[] Two() => [L(x, "A", "1.0.0"), Made(x, "C", version: "2.0.0", needs: [dep])];
            Mod User() => Made($"{x}User", "User", needs: [x]);
            var ticks = new Ticks([$"{x}|l:A"], new Dictionary<string, List<string>> { [x] = ["l:A", "l:C"] });
            var thisLaunch = Two();
            List<Mod> atLaunch = [us, thisLaunch[1], thisLaunch[0], User(), Made(dep, "D", ModLoadState.Disabled)];
            var plan = Launch(x, atLaunch, ticks);
            var page = After(atLaunch, plan, [], ticks);
            var next = NextLaunch(x, [.. Two(), Made(dep, "D"), us, User()], Rebuilt(atLaunch, []), [], ticks);
            Check("the rule, ... (e) the copy chosen blocked with no other ticked copy that fits: the first copy in the next launch's order that does, "
                + "though it isn't ticked, on the page and at the next launch: A (C Blocked)",
                plan?.Loads == thisLaunch[1] && page is ("A", "C Blocked", _, _) && page.Late == dep && Shown(next.Loads) == "A" && page.Before.SetEquals(next.Before),
                Rule(plan, page, next));
        }
        // The game's local-vs-Workshop rule leaves one copy, L (2.0, needing D, which is off; W 1.0 beside it), a mod needing
        // the id: the launch doesn't look (Run's way out: one copy left, nothing to do), and the page names the same, L,
        // which loads and fails for D (a dependency that's off is never late, so this pins that here; the page's own way out
        // matters only in a cycle). Fails on 32e3d69: its page blocked L for D and named W, which the game's rule sets aside.
        x = Id();
        {
            var dep = $"{x}D";
            Row("the game's rule leaving one copy (L 2.0 needing D, off; W 1.0), a mod needing the id: L, as at the launch, which doesn't look",
                Case([Made(x, "L", version: "2.0.0", needs: [dep]), W(x, 101, "1.0.0")], extra: [Made($"{x}User", "User", needs: [x]), Made(dep, "D", ModLoadState.Disabled)],
                    saved: [Entry(x), Entry(x, ModSource.SteamWorkshop), Entry($"{x}User"), Entry(dep, on: false)]), "L", beta ? "101 Higher" : "101 Local");
        }
        // Who the next launch counts as needing the id: only mods still to load when it comes to the id, as Run asks. C 2.0
        // needing D (on, listed last: after the slot), A 1.0. (1) The id's only needer a copy of Y that isn't ticked, Y
        // sorting first: Run sets it aside before it comes to the id, so nothing needs the id there and C loads, after D, no
        // swap. (2) The needer a Workshop copy of U the game's rule sets aside (a local copy higher): the same. Fails on
        // 32e3d69: its page counted every mod that needs the id by its switch, so the fallback, A.
        x = Id();
        {
            var (dep, y, u) = ($"{x}D", $"{x}Y", $"{x}U");
            Mod[] Two() => [L(x, "A", "1.0.0"), Made(x, "C", version: "2.0.0", needs: [dep])];
            Mod[] Ys() => [Made(y, "Y1", version: "1.0.0", needs: [x]), Made(y, "Y2", version: "2.0.0")];
            Mod[] Us() => [Made(u, "UL", version: "2.0.0"), Made(u, "UW", item: 9, version: "1.0.0", needs: [x])];
            HashSet<(string?, ModSource)> off = [(dep, ModSource.SteamWorkshop)];
            var ticks = new Ticks([$"{y}|l:Y1"], new Dictionary<string, List<string>> { [y] = ["l:Y1", "l:Y2"] });
            List<SettingsSaveMod> Saved(string other) => [Entry(ModEntry.ModId), Entry(other), Entry(other, other == u ? ModSource.SteamWorkshop : ModSource.ModsDirectory),
                Entry(x), Entry(x), Entry(dep), Entry(dep, ModSource.SteamWorkshop, false)];
            var ys = Ys();
            var twin = Page(x, [ys[1], ys[0], .. Two(), Made(dep, "D"), Made(dep, "DW", item: 10)], off, ticks, Saved(y));
            var twinNext = NextLaunch(x, [.. Two(), Made(dep, "D"), us, .. Ys(), Made(dep, "DW", item: 10)], Saved(y), off, ticks);
            var aside = Page(x, [.. Us(), .. Two(), Made(dep, "D"), Made(dep, "DW", item: 10)], off, Ticks.All, Saved(u));
            var asideNext = NextLaunch(x, [.. Two(), Made(dep, "D"), us, .. Us(), Made(dep, "DW", item: 10)], Saved(u), off);
            Check("the rule, ... the only mod needing the id one the next launch sets aside before it comes to the id (a copy of a mod installed twice "
                + "that isn't ticked; a Workshop copy the game's rule takes out): nothing needs the id there, so C, on the page and at the next launch",
                twin is ("C", "A Higher", null, _) && Shown(twinNext.Loads) == "C" && twin.Before.SetEquals(twinNext.Before)
                && aside is ("C", "A Higher", null, _) && Shown(asideNext.Loads) == "C" && aside.Before.SetEquals(asideNext.Before),
                $"the twin's: the page {twin.Loads} ({twin.Why}), the next launch {Shown(twinNext.Loads)}; set aside: the page {aside.Loads} ({aside.Why}), "
                + $"the next launch {Shown(asideNext.Loads)}");
        }
        // Which copy of an id a mod waits for: the first the game reads, not the first in this session's list. C 2.0 needs
        // D, which needs Y; Y1 (1.0, read first) needs Z, listed last (its Workshop copy off), Y2 (2.0) nothing and loads,
        // so it's first of Y this session; Y and D listed before the id. The game has D wait for Y1, so after Z, past the
        // slot: C blocked, A, on the page and at the next launch. Two Workshop items the same way, in the order Steam listed
        // them at Init (Copies.ReadItems: 202 before 201), or by their ids without it (201 first: C). Fails on 32e3d69: its
        // page had D wait for Y2, the first of Y in the order it sorts, so C.
        x = Id();
        {
            var (dep, y, z) = ($"{x}D", $"{x}Y", $"{x}Z");
            Mod[] Two() => [L(x, "A", "1.0.0"), Made(x, "C", version: "2.0.0", needs: [dep])];
            Mod User() => Made($"{x}User", "User", needs: [x]);
            Mod[] Rest() => [Made(dep, "D", needs: [y]), User(), Made(z, "Z")];
            Mod[] Ys(bool workshop) => workshop ? [Made(y, "W202", item: 202, version: "1.0.0", needs: [z]), Made(y, "W201", item: 201, version: "2.0.0")]
                : [Made(y, "Y1", version: "1.0.0", needs: [z]), Made(y, "Y2", version: "2.0.0")];
            HashSet<(string?, ModSource)> off = [(z, ModSource.SteamWorkshop)];
            List<SettingsSaveMod> saved = [Entry(ModEntry.ModId), Entry(y), Entry(y), Entry(dep), Entry(x), Entry(x), Entry($"{x}User"), Entry(z),
                Entry(z, ModSource.SteamWorkshop, false)];
            (string Loads, string Why, string? Late, HashSet<string?> Before) Session(bool workshop)
            {
                var ys = Ys(workshop);
                return Page(x, [ys[1], ys[0], .. Two(), .. Rest(), Made(z, "ZW", item: 11)], off, Ticks.All, saved);
            }
            var local = Session(false);
            var localNext = NextLaunch(x, [.. Two(), Made(dep, "D", needs: [y]), us, User(), .. Ys(false), Made(z, "Z"), Made(z, "ZW", item: 11)], saved, off);
            var itemsWere = Copies.TestItems([202, 201]);
            (string Loads, string Why, string? Late, HashSet<string?> Before) listed, unlisted;
            try
            {
                listed = Session(true);
                Copies.TestItems(null);
                unlisted = Session(true);
            }
            finally
            {
                Copies.TestItems(itemsWere);
            }
            var listedNext = NextLaunch(x, [.. Two(), Made(dep, "D", needs: [y]), us, User(), Made(z, "Z"), .. Ys(true), Made(z, "ZW", item: 11)], saved, off);
            Check("the rule, ... a mod waiting for the copy of an id the game reads first, not the one first this session: C blocked behind Y1, so A, on the "
                + "page and at the next launch; Workshop items in the order Steam listed them at Init the same, by their ids without it C",
                local is ("A", "C Blocked", _, _) && Shown(localNext.Loads) == "A" && local.Before.SetEquals(localNext.Before)
                && listed is ("A", "C Blocked", _, _) && Shown(listedNext.Loads) == "A" && listed.Before.SetEquals(listedNext.Before) && unlisted is ("C", "A Higher", null, _),
                $"local: the page {local.Loads} ({local.Why}), before the slot {string.Join(",", local.Before.Order())}, the next launch {Shown(localNext.Loads)} "
                + $"({string.Join(",", localNext.Before.Order())}); Steam's order: the page {listed.Loads}, the next launch {Shown(listedNext.Loads)}; by id: the page {unlisted.Loads}");
        }
        // A copy that can't take the slot in a blocked one's place needs everything installed, on and before the slot: C 3.0
        // blocked by D (listed last), B 2.0 needing a mod that isn't installed, A 1.0: A, on the page and at the launch.
        // Fails on 32e3d69: both let a dependency that isn't installed through, so B, which fails for it.
        x = Id();
        {
            var dep = $"{x}D";
            Row("a blocked copy's fallback past a ticked copy needing a mod that isn't installed",
                Case([L(x, "A", "1.0.0"), Made(x, "B", version: "2.0.0", needs: [$"{x}Missing"]), Made(x, "C", version: "3.0.0", needs: [dep])],
                    extra: [Made($"{x}User", "User", needs: [x]), Made(dep, "D"), Made(dep, "DW", ModLoadState.Disabled, item: 12)],
                    saved: [Entry(x), Entry(x), Entry(x), Entry($"{x}User"), Entry(dep), Entry(dep, ModSource.SteamWorkshop, false)]), "A", "B Other, C Blocked");
        }
        // The fixture's twins with Test Framework off, made up: A 1.0 not ticked, B 1.1 ticked needing Lib (installed, off),
        // a mod needing the id at 1.1.0 or newer. A dependency that's off doesn't stop the copy chosen: B, which loads and
        // fails for Lib, and the red banner says so (Dependencies.Unmet), as for a mod installed once; at the launch B too,
        // whether the sort puts A or B in the slot. Fails on 32e3d69: it blocked B for Lib and loaded A, below the needer's
        // minimum, with no banner.
        x = Id();
        {
            var lib = $"{x}Lib";
            Mod[] Two() => [L(x, "A", "1.0.0"), Made(x, "B", version: "1.1.0", needs: [lib])];
            Mod User()
            {
                var made = Made($"{x}User", "User");
                if (made.manifest is { } manifest)
                    manifest.dependencies = [new ModDependency(x, "1.1.0")];
                return made;
            }
            var ticks = new Ticks([$"{x}|l:A"], new Dictionary<string, List<string>> { [x] = ["l:A", "l:B"] });
            List<SettingsSaveMod> saved = [Entry(ModEntry.ModId), Entry(lib, on: false), Entry(x), Entry(x), Entry($"{x}User")];
            var copies = Two();
            List<Mod> session = [.. copies, User(), Made(lib, "Lib")];
            GameMods().AddRange(session);
            Copies.TestRecount();
            string page;
            bool unmet;
            try
            {
                using var lookups = Lookups.Begin(ModManager.Mods, Installed.Mods, saved, ticks);
                (page, unmet) = (Shown(Copies.Next(x).Loads), Dependencies.Unmet(copies[1]));
            }
            finally
            {
                foreach (var made in session)
                    GameMods().Remove(made);
                Copies.TestRecount();
            }
            var (slotted, behind) = (Two(), Two());
            var inSlot = Launch(x, [us, slotted[1], slotted[0], User(), Made(lib, "Lib", ModLoadState.Disabled)], ticks);
            var afterSlot = Launch(x, [us, behind[0], behind[1], User(), Made(lib, "Lib", ModLoadState.Disabled)], ticks);
            Check("the rule, ... the fixture's twins with Test Framework off: B, the copy chosen, on the page with the red banner's problem (needs a mod "
                + "that's off), and at the launch whether it sorts into the slot or after it",
                page == "B" && unmet && inSlot?.Loads == slotted[1] && afterSlot?.Loads == behind[1],
                $"the page {page}, unmet {unmet}; the launch, B in the slot {Shown(inSlot?.Loads)}, after it {Shown(afterSlot?.Loads)}");
        }
        // No copy can take a blocked one's place: the first of the id's other copies by the tie order, as the next launch has
        // it. C 2.0 ticked needing D, A 1.0 and B 1.5 not ticked, A needing a mod that isn't installed, B needing E; D and E
        // on, listed after the id, a mod needing the id; our record has B first. The next launch sorts A into the slot (it
        // waits for nothing), C and B after what they need, so none fits: B, the first by the tie order, on the page and at
        // the next launch. Fails on ec684b6: its page and Run took the first in the next launch's order, A (32e3d69's page
        // the first in this session's, A too).
        x = Id();
        {
            var (dep, late) = ($"{x}D", $"{x}E");
            Mod[] Three() => [Made(x, "A", needs: [$"{x}Missing"]), Made(x, "B", version: "1.5.0", needs: [late]), Made(x, "C", version: "2.0.0", needs: [dep])];
            Mod User() => Made($"{x}User", "User", needs: [x]);
            var ticks = new Ticks([$"{x}|l:A", $"{x}|l:B"], new Dictionary<string, List<string>> { [x] = ["l:B", "l:A", "l:C"] });
            List<SettingsSaveMod> saved = [Entry(ModEntry.ModId), Entry(x), Entry(x), Entry(x), Entry($"{x}User"), Entry(dep), Entry(late)];
            var page = Page(x, [.. Three(), User(), Made(dep, "D"), Made(late, "E")], [], ticks, saved);
            var next = NextLaunch(x, [.. Three(), Made(dep, "D"), Made(late, "E"), us, User()], saved, [], ticks);
            Check("the rule, ... a blocked copy with no other that can take the slot: the first of the others by the tie order (our record's), not the first "
                + "in the next launch's order, on the page and at the next launch: B (C Blocked)",
                page is ("B", "C Blocked", _, _) && page.Late == dep && Shown(next.Loads) == "B" && page.Before.SetEquals(next.Before),
                $"the page {page.Loads} ({page.Why}), blocked by {page.Late ?? "nothing"}, before the slot {string.Join(",", page.Before.Order())}; the next launch "
                + $"{Shown(next.Loads)}, before the slot {string.Join(",", next.Before.Order())}");
        }
        // Nor can a copy below a minimum version of the id that a mod still to load needs (as the game reads it: that mod
        // would fail), nor one whose version the game can't read. C 3.0 blocked by D (listed after the id), B 2.0 ticked, A
        // 2.5.0 not, our record B first, a mod needing the id at 2.5.0 or newer and one needing 3.0.0, off: A (at the
        // minimum, which the game takes), on the page and at the launch. Then A "2.6" ticked (above 2.5.0 read leniently),
        // B 2.5.0 not: B. Both fail on ec684b6, which didn't look at versions: B, which the mod fails for
        // (DEPENDENCY_VERSION_UNSUPPORTED), then A (DEPENDENCY_VERSION_INVALID).
        x = Id();
        {
            var dep = $"{x}D";
            Row("a blocked copy's fallback past a ticked copy below a minimum version a mod needs (2.5.0; one needing 3.0.0 off): the copy at it, though it isn't ticked",
                Case([L(x, "A", "2.5.0"), L(x, "B", "2.0.0"), Made(x, "C", version: "3.0.0", needs: [dep])], off: ["l:A"], record: ["l:B", "l:A", "l:C"],
                    extra: [NeedsAt($"{x}User", "User", x, "2.5.0"), NeedsAt($"{x}Off", "Off", x, "3.0.0"), Made(dep, "D")],
                    saved: [Entry(x), Entry(x), Entry(x), Entry($"{x}User"), Entry($"{x}Off", on: false), Entry(dep)]), "A", "B Other, C Blocked");
        }
        x = Id();
        {
            var dep = $"{x}D";
            Row("... past a ticked copy whose version the game can't read (\"2.6\"), a mod needing 2.5.0: the copy the game can read, though it isn't ticked",
                Case([L(x, "A", "2.6"), L(x, "B", "2.5.0"), Made(x, "C", version: "3.0.0", needs: [dep])], off: ["l:B"], record: ["l:A", "l:B", "l:C"],
                    extra: [NeedsAt($"{x}User", "User", x, "2.5.0"), Made(dep, "D")], saved: [Entry(x), Entry(x), Entry(x), Entry($"{x}User"), Entry(dep)]),
                "B", "A Other, C Blocked");
        }
        // The fallback's copies still to load before those the game's rule set aside, each by the tie order. L1 3.0 (read
        // first) blocked by E (listed after the id), L2 1.0, L3 1.5 and W 2.0, all ticked, our record W first, a mod needing
        // the id. The game's rule sets W aside (below L1). Without L1: L2, L3, then W, and on the beta W beats L2 (the first
        // local read), so L3 is left first: L3, on the page and at the launch (with W first, W comes back). Stable takes W out
        // either way: L3.
        x = Id();
        {
            var late = $"{x}E";
            Row("a blocked copy's fallback: the copies still to load before one the game's rule set aside, L3",
                Case([Made(x, "L1", version: "3.0.0", needs: [late]), L(x, "L2", "1.0.0"), L(x, "L3", "1.5.0"), W(x, 601, "2.0.0")], record: ["w:601", "l:L1", "l:L2", "l:L3"], extra: [Made($"{x}User", "User", needs: [x]), Made(late, "E")],
                    saved: [Entry(x), Entry(x), Entry(x), Entry(x, ModSource.SteamWorkshop), Entry($"{x}User"), Entry(late)]),
                "L3", beta ? "601 Other, L1 Blocked, L2 Other" : "601 Local, L1 Blocked, L2 Other");
        }
        // Who needs the id, a copy of another mod installed more than once that the next launch brings back from the game's
        // rule: Y sorts before the id, its local copy L (2.0, ticked) needing it, its Workshop copies W1 (1.0, ticked) and
        // W2 (3.0, not ticked, on through W1's switch). The beta's rule sets L aside for W2; the launch's rule over the
        // ticked copies brings L back, which needs the id when the launch comes to it: C (2.0, needing D, listed after the
        // id) blocked, A, on the page and at the next launch. Stable sets the Workshop copies aside, and L, waiting for the
        // id, sorts after it: A too. Fails on ec684b6 on the beta: its page counted only the mods in the next launch's order,
        // which hasn't L, so C.
        x = Id();
        {
            var (dep, y) = ($"{x}D", $"{x}Y");
            Mod[] Two() => [L(x, "A", "1.0.0"), Made(x, "C", version: "2.0.0", needs: [dep])];
            Mod[] Ys() => [Made(y, "YL", version: "2.0.0", needs: [x]), Made(y, "W301", item: 301, version: "1.0.0"), Made(y, "W302", item: 302, version: "3.0.0")];
            var ticks = new Ticks([$"{y}|w:302"], new Dictionary<string, List<string>> { [y] = ["w:301", "w:302", "l:YL"] });
            List<SettingsSaveMod> saved = [Entry(ModEntry.ModId), Entry(y), Entry(y, ModSource.SteamWorkshop), Entry(y, ModSource.SteamWorkshop), Entry(x), Entry(x), Entry(dep)];
            var page = Page(x, [.. Ys(), .. Two(), Made(dep, "D")], [], ticks, saved);
            var next = NextLaunch(x, [.. Two(), Made(dep, "D"), us, .. Ys()], saved, [], ticks);
            Check("the rule, ... a mod needing the id that the next launch brings back from the game's rule (another mod's ticked copy, its higher one off): "
                + "it counts, so C is blocked: A, on the page and at the next launch (C Blocked)",
                page is ("A", "C Blocked", _, _) && page.Late == dep && Shown(next.Loads) == "A" && page.Before.SetEquals(next.Before),
                $"the page {page.Loads} ({page.Why}), before the slot {string.Join(",", page.Before.Order())}; the next launch {Shown(next.Loads)}, before the slot "
                + string.Join(",", next.Before.Order()));
        }
        if (beta)
        {
            // The beta: the launch looks wherever the game's rule leaves two or more Mods of the id, a copy's two manifests in
            // two folders too. L 1.0 (local, ticked), item 401 with W401a 2.0 needing nothing and W401b 2.1 needing D (listed
            // after the id), a mod needing the id. The game's rule sets L aside for item 401; the launch reads the item by
            // W401b, blocked by D, and brings L back: L, on the page and at the next launch. Fails on ec684b6: its page
            // counted copies (one left) and didn't look, so 401.
            x = Id();
            {
                var dep = $"{x}D";
                Mod[] Three() => [L(x, "L", "1.0.0"), Made(x, "W401a", item: 401, version: "2.0.0"), Made(x, "W401b", item: 401, version: "2.1.0", needs: [dep])];
                Mod User() => Made($"{x}User", "User", needs: [x]);
                List<SettingsSaveMod> saved = [Entry(ModEntry.ModId), Entry(x), Entry(x, ModSource.SteamWorkshop), Entry(x, ModSource.SteamWorkshop), Entry($"{x}User"), Entry(dep)];
                var page = Page(x, [.. Three(), User(), Made(dep, "D")], [], Ticks.All, saved);
                var three = Three();
                var next = NextLaunch(x, [Made(dep, "D"), three[0], us, User(), three[1], three[2]], saved, []);
                Check("the rule, ... the beta: the game's rule leaving one copy with two manifests in two folders, the newer needing a mod after the slot: the "
                    + "launch looks, as the page does: L, brought back (401 Blocked)",
                    page is ("L", "401 Blocked", _, _) && page.Late == dep && Shown(next.Loads) == "L" && page.Before.SetEquals(next.Before),
                    $"the page {page.Loads} ({page.Why}), blocked by {page.Late ?? "nothing"}; the next launch {Shown(next.Loads)}");
            }
            // The beta: a blocked copy's fallback goes by the tie order where both sources are left. L1 1.0 (read first), W1
            // 2.0, L2 1.5, W2 3.0 needing E (listed after the id), all ticked, a mod needing the id; our record has L2 before
            // W1. The game's rule sets L1 aside; the first ticked copy left, W1, makes it the Workshop's: W2, blocked; without
            // it the rule leaves W1 and L2, and the tie order puts L2 first: L2, on the page and at the launch. Fails on
            // ec684b6: both went by the order of the copies (W1).
            x = Id();
            {
                var late = $"{x}E";
                Row("the beta: a blocked copy's fallback with both sources left: by the tie order (our record's), L2",
                    Case([W(x, 501, "2.0.0"), L(x, "L1", "1.0.0"), L(x, "L2", "1.5.0"), Made(x, "W502", item: 502, version: "3.0.0", needs: [late])],
                        record: ["l:L2", "w:501", "w:502", "l:L1"], extra: [Made($"{x}User", "User", needs: [x]), Made(late, "E")],
                        saved: [Entry(x), Entry(x), Entry(x, ModSource.SteamWorkshop), Entry(x, ModSource.SteamWorkshop), Entry($"{x}User"), Entry(late)]),
                    "L2", "501 Other, 502 Blocked, L1 Other");
            }
        }
        else
            Results.Add("SKIP the rule, the launch looking at a copy's two manifests in two folders, and a blocked copy's fallback with both sources left: only the beta's rule "
                + "lets a Workshop copy beat a local one");
        // Who needs the id with a minimum version, as the next launch has them: U installed from both sources, its local copy
        // (2.0) needing nothing, its Workshop copy (1.0) needing the id at 1.0.0 or newer, which the game's rule sets aside:
        // nothing needs a minimum version, so A's "1.3", read leniently above B's 1.2.0, on the page and at the launch.
        // 32e3d69 counted the Workshop copy by its switch, so B (the copy the game can read).
        x = Id();
        {
            var (ul, uw) = (Made($"{x}U", "UL", version: "2.0.0"), Made($"{x}U", "UW", item: 14, version: "1.0.0"));
            if (uw.manifest is { } manifest)
                manifest.dependencies = [new ModDependency(x, "1.0.0")];
            Row("a minimum version needed only by a copy the game's rule sets aside (a Workshop copy beside a higher local one): not counted, so the higher",
                Case([L(x, "A", "1.3"), L(x, "B", "1.2.0")], extra: [ul, uw]), "A", "B Higher");
        }
        // ... nor a mod whose update since doesn't need the id (its launch manifest still in the game's list, the update read
        // after it at the same path): C (2.0, needing D, listed after the id) loads, on the page and at the next launch,
        // which reads the update. 32e3d69 counted the launch manifest, so A.
        x = Id();
        {
            var dep = $"{x}D";
            Mod[] Two() => [L(x, "A", "1.0.0"), Made(x, "C", version: "2.0.0", needs: [dep])];
            List<SettingsSaveMod> saved = [Entry(ModEntry.ModId), Entry(x), Entry(x), Entry($"{x}U"), Entry(dep)];
            var page = Page(x, [Made($"{x}U", "U", needs: [x]), .. Two(), Made(dep, "D"), Made($"{x}U", "U", ModLoadState.AddedAtRuntime, version: "2.0.0")], [], Ticks.All, saved);
            var next = NextLaunch(x, [.. Two(), Made(dep, "D"), us, Made($"{x}U", "U", version: "2.0.0")], saved, []);
            Check("the rule, ... a mod that needed the id at launch and doesn't since its update (read while the game runs, at the same path): not counted, so "
                + "C, on the page and at the next launch", page is ("C", "A Higher", null, _) && Shown(next.Loads) == "C" && page.Before.SetEquals(next.Before),
                $"the page {page.Loads} ({page.Why}); the next launch {Shown(next.Loads)}");
        }
        // ... nor a copy of a mod installed twice whose update since doesn't need the id: Y (local Y1 and Y2) sorts before
        // the id, Y1 loading by its update (2.0, read while the game runs), which needs nothing, where its launch manifest
        // (1.0) needs the id: C, on the page and at the next launch, which reads the update.
        x = Id();
        {
            var (dep, y) = ($"{x}D", $"{x}Y");
            Mod[] Two() => [L(x, "A", "1.0.0"), Made(x, "C", version: "2.0.0", needs: [dep])];
            List<SettingsSaveMod> saved = [Entry(ModEntry.ModId), Entry(y), Entry(y), Entry(x), Entry(x), Entry(dep)];
            var page = Page(x, [Made(y, "Y1", needs: [x]), L(y, "Y2", "1.5.0"), .. Two(), Made(dep, "D"), Made(y, "Y1", ModLoadState.AddedAtRuntime, version: "2.0.0")], [],
                Ticks.All, saved);
            var next = NextLaunch(x, [.. Two(), Made(dep, "D"), us, L(y, "Y1", "2.0.0"), L(y, "Y2", "1.5.0")], saved, []);
            Check("the rule, ... a copy of a mod installed twice that needed the id at launch and doesn't since its update: not counted, so C, on the page and "
                + "at the next launch", page is ("C", "A Higher", null, _) && Shown(next.Loads) == "C" && page.Before.SetEquals(next.Before),
                $"the page {page.Loads} ({page.Why}); the next launch {Shown(next.Loads)}");
        }
        // A copy with two manifests in one folder, as the game reads it (a Mod each): L with L1 1.0 needing nothing and L2 2.0
        // needing D (listed after the id), W 0.5 beside it, which the game's rule sets aside, a mod needing the id. The
        // launch has both of L's Mods to load, reads L by L2, blocked by D, and brings W back: W, on the page and at the
        // launch. Fails on 8ff2e3d: its page counted L's folders (one) and its order folded them to L2, so it didn't look: L.
        x = Id();
        {
            var dep = $"{x}D";
            Row("a copy with two manifests in one folder, the newer needing a mod after the slot, the game's rule leaving that copy alone: the launch looks, as the "
                + "page does: W, brought back", Case([L(x, "L", "1.0.0"), Made(x, "L", version: "2.0.0", needs: [dep]), W(x, 701, "0.5.0")],
                    extra: [Made($"{x}User", "User", needs: [x]), Made(dep, "D")],
                    saved: [Entry(x), Entry(x), Entry(x, ModSource.SteamWorkshop), Entry($"{x}User"), Entry(dep)]), "701", "L Blocked");
        }
        // The fallback's pick by the game's reading of versions wherever a mod still to load needs the id with a minimum
        // version, as Run has it, though the game can't read that minimum ("1.2": the mod fails whatever loads): L with two
        // manifests in one folder (3.0, and 3.1 needing D, listed after the id), the copy the game's rule leaves, blocked;
        // items 801 ("2.6", read leniently) and 802 (2.5.0) beside it: 802, on the page and at the launch.
        x = Id();
        {
            var dep = $"{x}D";
            Row("a blocked copy's fallback where a mod needs the id at a minimum version the game can't read: the copy whose version it can read",
                Case([L(x, "L", "3.0.0"), Made(x, "L", version: "3.1.0", needs: [dep]), W(x, 801, "2.6"), W(x, 802, "2.5.0")],
                    extra: [NeedsAt($"{x}User", "User", x, "1.2"), Made(dep, "D")],
                    saved: [Entry(x), Entry(x), Entry(x, ModSource.SteamWorkshop), Entry(x, ModSource.SteamWorkshop), Entry($"{x}User"), Entry(dep)]),
                "802", "801 Other, L Blocked");
        }

        // The version reading: as the game reads it where it can, else leniently.
        static int Compare(string a, string b) => Math.Sign((Copies.Version(Made("SmmRuleVersion", "A", version: a)) ?? throw new InvalidOperationException($"{a} unread"))
            .CompareTo(Copies.Version(Made("SmmRuleVersion", "B", version: b))));
        var read = new List<string>();
        void Same(string a, string b) => read.Add($"{a} = {b}: {(Compare(a, b) == 0 ? "ok" : "NO")}");
        void Below(string a, string b) => read.Add($"{a} < {b}: {(Compare(a, b) < 0 && Compare(b, a) > 0 ? "ok" : "NO")}");
        Same("1.2", "1.2.0");
        Same("v1.2.3", "1.2.3");
        Same("1.0.0+a", "1.0.0+b");
        Below("1.1.0", "1.1.0.1");
        Below("0.4.1.beta", "0.4.1");
        Below("0.4.0", "0.4.1.beta");
        Same("0.4.1.beta", "0.4.1-beta");
        Same("1.2.25(beta)", "1.2.25-beta");
        Same("v1.2.3-beta-0-g9863091", "1.2.3-beta.0.g9863091");
        Below("1.0.0", "99999999999.0.0");
        // N10: letters straight after a number start a prerelease (no separator, or one).
        Below("1.0b", "1.0");
        Same("1.2a", "1.2.0-a");
        Same("1.2.3 beta", "1.2.3-beta");
        string[] none = ["abc", "", "beta", "v"];
        var unread = none.Where(v => Copies.Version(Made("SmmRuleVersion", "A", version: v)) != null).ToList();
        if (Copies.Version(new Mod { path = "", manifest = new ModManifest { id = "SmmRuleVersion" } }) != null)
            unread.Add("no version");
        Check("the version reading (P5): \"1.2\" is 1.2.0, a leading v dropped, metadata ignored, more numbers above, a prerelease after a "
            + "separator or straight after a number (\"1.0b\" below 1.0), a number past int's range read; none where no number leads",
            read.All(r => r.EndsWith(": ok")) && unread.Count == 0, $"{string.Join("; ", read)}; read where none should be: {string.Join(", ", unread)}");
        // Every version the game reads, ordered exactly as the game orders them.
        string[] strict = ["0.1.5-dev.3", "0.1.5-dev.9", "0.1.5-dev.10", "0.1.5", "1.0.0-beta", "1.0.0-dev", "1.0.0-1", "1.0.0-alpha.1", "1.0.0-alpha",
            "1.0.0", "1.2.0", "1.10.0", "v2.0.0", "1.0.0+meta", "1.0.0-rc.1+b", "0.0.0"];
        var disagree = new List<string>();
        foreach (var a in strict)
            foreach (var b in strict)
                if (Copies.GameVersion(Made("SmmRuleVersion", "A", version: a)) is { } ga && Copies.GameVersion(Made("SmmRuleVersion", "B", version: b)) is { } gb)
                {
                    if (Math.Sign(ga.CompareTo(gb)) != Compare(a, b))
                        disagree.Add($"{a} vs {b}");
                }
                else
                    disagree.Add($"{a} or {b} unread by the game");
        Check("... every version the game reads is ordered exactly as the game orders it", disagree.Count == 0, string.Join(", ", disagree));
        Check("... the game's own reading: \"1.2\" none, a number past int's range none (no throw)",
            Copies.GameVersion(Made("SmmRuleVersion", "A", version: "1.2")) == null && Copies.GameVersion(Made("SmmRuleVersion", "A", version: "99999999999.0.0")) == null
            && Copies.GameVersion(Made("SmmRuleVersion", "A", version: "1.2.0")) != null);
        var once = Made("SmmRuleVersion", "Once", version: "1.2.3");
        var (readsBefore, first, second) = (Copies.TestReads, Copies.Version(once), Copies.Version(once));
        var readsOnce = Copies.TestReads - readsBefore;
        if (once.manifest is { } changed)
            changed.version = "2.0.0";
        Check("... a Mod's versions read once and kept, read again if its manifest's version changes",
            readsOnce == 1 && first == second && Copies.Version(once)?.ToString() == "2.0.0" && Copies.TestReads - readsBefore == 2,
            $"{readsOnce} reads for two asks, then {Copies.TestReads - readsBefore}");

        // The order the game reads local copies in, so which one its rule compares Workshop copies with: Godot's own sort
        // of a folder's subfolders (on real folders here: capitals, a number, an underscore, small letters), a folder's
        // manifest before its subfolders'.
        var root = Path.Combine(outDir, "read-order");
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
        string[] names = ["beta", "Gamma", "Alpha", "_under", "10", "9", "alpha2"];
        foreach (var name in names)
            Directory.CreateDirectory(Path.Combine(root, name));
        var godot = DirAccess.GetDirectoriesAt(root).ToList();
        var locals = names.Select(name => Made("SmmRuleRead", name)).ToList();
        var order = new List<string>();
        while (locals.Count > 0)
        {
            var next = Copies.FirstRead(locals);
            order.Add(Copies.Shown(Copies.CopyOf(next)));
            locals.Remove(next);
        }
        var nested = Copies.FirstRead([Made("SmmRuleRead", "Alpha2"), Made("SmmRuleRead", "Alpha/inner"), Made("SmmRuleRead", "Alpha")]);
        Check("the first local copy the game reads (FirstRead): folders in Godot's own order, a folder's manifest before its subfolders'",
            order.SequenceEqual(godot) && nested.path == "C:/SmmSameIdTest/Alpha", $"ours {string.Join(", ", order)}; Godot's {string.Join(", ", godot)}; nested {nested.path}");
    }

    // A tie when the copy holding the id's entry is gone for a launch (P4; the design's risk 3): our record hands its
    // front to the next copy installed, the one that loads meanwhile, so that copy ranks first among equal versions from
    // then on, and the old holder back at the same version switches nothing. Made-up copies, our data tried on a copy
    // (Reconcile); without the move, the old holder would load again.
    static void TestSameIdTie()
    {
        const string id = "SmmSameIdTie";
        var (holder, next) = (Made(id, "Tie1"), Made(id, "Tie2"));
        var data = Store.Clone(Store.Data);
        (data.Copies ??= [])[id] = ["l:Tie1", "l:Tie2"];
        string? Loads() => Copies.Next(id, new HashSet<(string?, ModSource)>(), new Ticks(data.CopiesOff, data.Copies)).Loads is { } loads ? Copies.CopyOf(loads) : null;
        GameMods().Add(next);
        Copies.TestRecount();
        try
        {
            var before = Loads(); // the holder gone: the copy left
            Copies.Reconcile(data);
            var record = string.Join(",", data.Copies?.GetValueOrDefault(id) ?? []);
            GameMods().Add(holder);
            Copies.TestRecount();
            var back = Loads();
            Check("a tie with the holder gone for a launch (P4): our record puts the copy left first, so with the old holder back at the same version that one "
                + "still loads", before == "l:Tie2" && record == "l:Tie2,l:Tie1" && back == "l:Tie2", $"meanwhile {before}; record {record}; back {back}");
        }
        finally
        {
            GameMods().Remove(next);
            GameMods().Remove(holder);
            Copies.TestRecount();
        }
    }

    // SameIdLoad's plan on made-up lists, as the game's loop has them while our Init runs (us at the first slot still to
    // load): the copy chosen first (no swap), later (swapped through the list's array: the game's loop carries on), a copy
    // decided before us, the game's local-vs-Workshop rule leaving one copy (nothing to do), a dependency of the chosen copy
    // that's on after the id's first slot (no swap; with a mod after us needing the id, the fallback: the best other
    // ticked copy that can take the slot, the one there too only where what it needs loads before it, not one needing a
    // mod that isn't installed nor one below a minimum version a mod needs; none: the first copy left by the tie order that
    // can, else the first; the rest set aside, the plan saying which copy was blocked by what; one off or not installed:
    // swapped, or left at the slot; at the slot with no mod needing the id: left there), the canary (not a List<Mod>, not
    // us first, not the game's own list), three copies, two Workshop copies and a local one, a local and a Workshop copy
    // only, our own second copy, none of the copies left ticked (the ticked ones the game's rule set aside: the rule again
    // over them, the copy it leaves brought back; none to bring back: the game's choice), a copy brought back with the
    // game's manifest messages and in its own slot (the self-check: taken), one Workshop item with two manifests, a file
    // from before copies_off (its legacy pick alone ticked; missing, the others), a set-aside copy's messages kept for its
    // panel; then the self-check's verdicts (Judge) on what the game made of a plan. Which copy wins by version and tie is
    // TestCopyRule's.
    static void TestSameIdPlan()
    {
        const string X = "SmmSameIdX";
        var us = Made(ModEntry.ModId, "SpireModManager");
        var reads = 0;
        // off: copies_off keys as our file has it; record: our record of X's copies.
        (bool Canary, Dictionary<string, SameIdLoad.Entry> Plan) PlanOf(IReadOnlyList<Mod> mods, bool live = true, IEnumerable<string>? off = null, string[]? record = null)
        {
            var run = SameIdLoad.Run(mods, () =>
            {
                reads++;
                return new Ticks(off, record == null ? null : new Dictionary<string, List<string>> { [X] = [.. record] });
            }, m => m == us, log: false, live: live);
            return (run.Canary, run.Plan);
        }
        // The game's list is a List<Mod> (a collection expression for a read-only list would make a list of its own kind).
        (bool Canary, Dictionary<string, SameIdLoad.Entry> Plan) Plan(List<Mod> mods, IEnumerable<string>? off = null, string[]? record = null) =>
            PlanOf(mods, off: off, record: record);
        string[] OffKeys(params string[] copies) => [.. copies.Select(c => $"{X}|{c}")];
        static string Copy(Mod? m) => m == null ? "none" : Copies.CopyOf(m);
        static string Say(Dictionary<string, SameIdLoad.Entry> plan, params Mod[] mods) =>
            string.Join("; ", plan.Select(p => $"{p.Key}: loads {Copy(p.Value.Loads)}, aside {string.Join(",", p.Value.SetAside.Select(Copy))}, "
                + $"swapped {p.Value.Swapped} ({p.Value.From}->{p.Value.To}), why {p.Value.Why}, blocked {p.Value.Blocked} by {p.Value.Late}"))
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
            var (ok, plan) = PlanOf(list, off: OffKeys("l:B"), record: ["l:A", "l:B"]);
            Check("same ID, the plan: the copy chosen (the other off) in the id's first slot loads there, the other set aside as the game's own duplicate, no swap",
                ok && plan.GetValueOrDefault(X) is { Swapped: false, Why: SameIdLoad.Reason.None } entry && entry.Loads == a
                && a.state == None && b.state == Aside && list[1] == a, Say(plan, a, b));
        }
        {
            // As in the game's loop: our Init runs while it's on us; the swap must leave its enumerator going.
            var (a, b, c) = (Made(X, "A"), Made(X, "B", version: "1.1.0"), Made("SmmSameIdOther", "C"));
            List<Mod> list = [us, a, c, b];
            var version = typeof(List<Mod>).GetField("_version", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var before = version?.GetValue(list);
            var (ok, seen, thrown) = (false, new List<Mod>(), (string?)null);
            Dictionary<string, SameIdLoad.Entry> plan = [];
            try
            {
                foreach (var mod in list)
                    if (mod == us)
                        (ok, plan) = PlanOf(list);
                    else
                        seen.Add(mod);
            }
            catch (InvalidOperationException e)
            {
                thrown = e.Message;
            }
            Check("... the copy chosen later (both ticked, the higher version): swapped into the id's first slot through the list's array (its version "
                + "unchanged: the game's loop carries on), the other set aside",
                ok && thrown == null && before != null && before.Equals(version?.GetValue(list)) && plan.GetValueOrDefault(X) is { Swapped: true, From: 3, To: 1 } entry
                && entry.Loads == b && list[1] == b && list[3] == a && seen.SequenceEqual([b, c, a]) && a.state == Aside && b.state == None,
                $"{thrown ?? "no throw"}; {Say(plan, a, b)}");
        }
        {
            var (a, b) = (Made(X, "A", ModLoadState.Loaded), Made(X, "B", version: "1.1.0"));
            var (ok, plan) = Plan([a, us, b]);
            // The copy that loaded is the one chosen, or no copy after us: nothing to say, nor any entry for the self-check
            // to find "not taken" (the game failed the copy before us as a duplicate already). With one after us that ties
            // with it: taken, the game's duplicate error before us notwithstanding.
            var (c, d, e) = (Made(X, "C", ModLoadState.Loaded), Made(X, "D", ModLoadState.Failed), Made(X, "E"));
            d.errors = [Error("MOD_ERROR.DUPLICATE_ID")];
            var (okNone, planNone) = Plan([c, d, us]);
            List<Mod> later = [c, d, us, e];
            var (okAfter, planAfter) = PlanOf(later, record: ["l:C", "l:E"]);
            var judged = planAfter.GetValueOrDefault(X) is { } set ? SameIdLoad.Judge(X, set, later, 2) : (SameIdLoad.Result?)null;
            Check("... a copy that sorted before us and loaded: the copies after us set aside (no duplicate error), a higher one chosen waits for the next "
                + "launch; nothing after us: nothing to say; a tie after us: taken, the game's duplicate error before us notwithstanding",
                ok && plan.GetValueOrDefault(X) is { Why: SameIdLoad.Reason.DecidedBeforeUs } entry && entry.Loads == a && b.state == Aside
                && okNone && planNone.Count == 0
                && okAfter && planAfter.GetValueOrDefault(X) is { Why: SameIdLoad.Reason.None } after && after.Loads == c && e.state == Aside
                && judged == SameIdLoad.Result.Taken,
                $"{Say(plan, a, b)} || {Say(planNone, c, d)} || {Say(planAfter, c, d, e)}, self-check {judged}");
        }
        {
            // The game's local-vs-Workshop rule left one copy (the Workshop ones off, or set aside by it): nothing to do,
            // nothing said (the page foresees it).
            var (local, w21, w22) = (Made(X, "L"), Made(X, "W21", ModLoadState.Disabled, 21), Made(X, "W22", ModLoadState.Disabled, 22));
            var (okOff, planOff) = Plan([us, local, w21, w22]);
            var (w41, w42, local3) = (Made(X, "W41", Aside, 41, "1.0.0"), Made(X, "W42", None, 42, "3.0.0"), Made(X, "L3", Aside, version: "2.0.0"));
            var (okAside, planAside) = Plan([us, w41, w42, local3]);
            Check("... the game's local-vs-Workshop rule left one copy (the others off, or set aside by it): nothing said, nothing flipped",
                okOff && planOff.Count == 0 && local.state == None && okAside && planAside.Count == 0 && w42.state == None,
                $"{Say(planOff, local, w21, w22)} || {Say(planAside, w41, w42, local3)}");
        }
        {
            var (a, dep, b) = (Made(X, "A"), Made("SmmSameIdDep", "Dep"), Made(X, "B", version: "1.1.0", needs: ["SmmSameIdDep"]));
            var (ok, plan) = Plan([us, a, dep, b]);
            var (a2, dep2, b2, user) = (Made(X, "A"), Made("SmmSameIdDep", "Dep"), Made(X, "B", version: "1.1.0", needs: ["SmmSameIdDep"]),
                Made("SmmSameIdUser", "U", needs: [X]));
            List<Mod> blocked = [us, a2, dep2, b2, user];
            var (okUser, planUser) = PlanOf(blocked);
            var (a3, b3, user3) = (Made(X, "A"), Made(X, "B", version: "1.1.0", needs: ["SmmSameIdNotInstalled"]), Made("SmmSameIdUser", "U", needs: [X]));
            List<Mod> gone = [us, a3, b3, user3];
            var (okGone, planGone) = PlanOf(gone);
            Check("... the copy chosen needing a mod after the id's first slot: no swap, the flip alone; with a mod after us needing the id, the other "
                + "copy (the fallback, at the slot) loads, the one chosen set aside (no duplicate failure), the plan naming the copy blocked and its "
                + "dependency; needing a mod not installed: swapped all the same",
                ok && plan.GetValueOrDefault(X) is { Swapped: false, Why: SameIdLoad.Reason.None } entry && entry.Loads == b && a.state == Aside && b.state == None
                && okUser && planUser.GetValueOrDefault(X) is { Why: SameIdLoad.Reason.DependencyAfterSlot, Swapped: false, Blocked: "l:B", Late: "SmmSameIdDep" } user1
                && user1.Loads == a2 && user1.SetAside.SequenceEqual([b2]) && a2.state == None && b2.state == Aside && blocked[1] == a2
                && okGone && planGone.GetValueOrDefault(X) is { Swapped: true } && gone[1] == b3 && a3.state == Aside,
                $"{Say(plan, a, b)} || {Say(planUser, a2, b2)} || {Say(planGone, a3, b3)}");
        }
        {
            var (a, b) = (Made(X, "A"), Made(X, "B", version: "1.1.0"));
            Mod[] array = [us, a, b];
            var (ok, plan) = PlanOf(array);
            var (c, d) = (Made(X, "C"), Made(X, "D", version: "1.1.0"));
            var (notFirst, planNotFirst) = Plan([c, us, d]);
            var (e, f) = (Made(X, "E"), Made(X, "F", version: "1.1.0"));
            var (copied, planCopied) = PlanOf(new List<Mod> { us, e, f }, live: false);
            Check("... the canary: no flip when the game's list isn't a List<Mod>, when the first mod still to load isn't us, nor when the list is a "
                + "copy of the one the game loads from",
                !ok && plan.Count == 0 && a.state == None && b.state == None && !notFirst && planNotFirst.Count == 0 && d.state == None
                && !copied && planCopied.Count == 0 && e.state == None && f.state == None,
                $"{Say(plan, a, b)} || {Say(planNotFirst, c, d)} || {Say(planCopied, e, f)}");
        }
        {
            var (a, b, c) = (Made(X, "A"), Made(X, "B"), Made(X, "C", version: "1.2.0"));
            List<Mod> list = [us, a, b, c];
            var (ok, plan) = PlanOf(list);
            Check("... three copies from one source: the one chosen loads, swapped into the first slot, the two others set aside",
                ok && plan.GetValueOrDefault(X) is { Swapped: true } entry && entry.Loads == c && entry.SetAside.Count == 2 && list[1] == c
                && a.state == Aside && b.state == Aside && c.state == None, Say(plan, a, b, c));
        }
        {
            var (w1, w2, local) = (Made(X, "W1", item: 21), Made(X, "W2", item: 22, version: "1.1.0"), Made(X, "L"));
            List<Mod> list = [us, w1, w2, local];
            var (ok, plan) = PlanOf(list);
            Check("... two Workshop copies and a local one, all still to load: exactly one loads, of the first ticked copy's source the higher",
                ok && plan.GetValueOrDefault(X)?.Loads == w2 && new[] { w1, w2, local }.Count(m => m.state == None) == 1 && list[1] == w2, Say(plan, w1, w2, local));
        }
        {
            var (local, workshop) = (Made(X, "L"), Made(X, "W", Aside, 31));
            var (ok, plan) = Plan([us, local, workshop]);
            var (us2, solo) = (Made(ModEntry.ModId, "SpireModManagerOther"), Made("SmmSameIdSolo", "S"));
            reads = 0;
            var (okOurs, planOurs) = Plan([us, us2, solo]);
            Check("... a local and a Workshop copy only (the game's rule chose): nothing to do; our own second copy is left alone, and no id twice "
                + "means our data isn't even read", ok && plan.Count == 0 && local.state == None && okOurs && planOurs.Count == 0 && us2.state == None && reads == 0,
                $"{Say(plan, local, workshop)} || {Say(planOurs, us2)}; read {reads} times");
        }
        {
            // None of the copies left to load ticked, the ticked ones set aside by the game's rule: the rule again over the
            // ticked copies alone (a tie at 1.0.0 takes out W1), so L, brought back into the id's first slot, the copies
            // still to come set aside (Jordan, 23 Oct: a copy that's off never loads).
            var (w1, w2, w3, local) = (Made(X, "W1", Aside, 51), Made(X, "W2", None, 52), Made(X, "W3", None, 53), Made(X, "L", Aside));
            List<Mod> list = [us, w1, w2, w3, local];
            var (ok, plan) = PlanOf(list, off: OffKeys("w:52", "w:53"), record: ["w:51", "w:52", "w:53"]);
            Check("... none of the copies left to load ticked, ticked ones the game's rule set aside: the rule again over the ticked copies, L brought back "
                + "into the id's first slot, the copies still to come set aside",
                ok && plan.GetValueOrDefault(X) is { Swapped: true, Why: SameIdLoad.Reason.None, From: 4, To: 2 } entry && entry.Loads == local
                && local.state == None && list[2] == local && list[4] == w2 && w2.state == Aside && w3.state == Aside && w1.state == Aside
                && entry.SetAside.SequenceEqual([w2, w3]),
                Say(plan, w1, w2, w3, local));
        }
        {
            // None ticked among the copies after us, and none the rule set aside (the ticked one's switch off: Disabled):
            // the game's own choice, the first, no swap, the rest set aside so the game doesn't fail them as duplicates.
            var (w1, w2, w3) = (Made(X, "W1", ModLoadState.Disabled, 51), Made(X, "W2", None, 52), Made(X, "W3", None, 53));
            var (ok, plan) = Plan([us, w2, w3, w1], off: OffKeys("w:52", "w:53"), record: ["w:51", "w:52", "w:53"]);
            Check("... none of the copies left ticked, none set aside to bring back: the game's own choice (the first) loads, no swap, the rest set aside",
                ok && plan.GetValueOrDefault(X) is { Swapped: false, Why: SameIdLoad.Reason.None } entry && entry.Loads == w2 && w2.state == None && w3.state == Aside,
                Say(plan, w1, w2, w3));
        }
        {
            // The rule over the ticked copies picks a copy the game's rule set aside that can't take the first slot (its
            // dependency D after it) while a mod still to load needs the id: the next ticked copy still to come loads
            // there, not the game's own choice, W2, which is off; the plan says what was blocked by what.
            var (w1, w2, l1, l2) = (Made(X, "W1", Aside, 91, "1.0.0"), Made(X, "W2", None, 92, "3.0.0"), Made(X, "L1", Aside, version: "2.0.0", needs: ["SmmSameIdD"]),
                Made(X, "L2", None, version: "1.5.0"));
            var (needer, dep) = (Made("SmmSameIdNeeder", "M", needs: [X]), Made("SmmSameIdD", "D"));
            List<Mod> list = [us, w2, l2, needer, dep, l1, w1];
            var (ok, plan) = PlanOf(list, off: OffKeys("w:92"), record: ["w:91", "w:92"]);
            // The mirror: a local copy that's off read first (3.0), and the Workshop copy the rule over the ticked ones picks
            // blocked: the ticked local copy still to come loads, not the one that's off.
            var (m1, m2, mw) = (Made(X, "M1", None, version: "3.0.0"), Made(X, "M2", None, version: "1.0.0"), Made(X, "MW", Aside, 93, "1.5.0", needs: ["SmmSameIdD"]));
            var (needer2, dep2) = (Made("SmmSameIdNeeder", "M", needs: [X]), Made("SmmSameIdD", "D"));
            List<Mod> mirror = [us, m1, m2, needer2, dep2, mw];
            var (okMirror, planMirror) = PlanOf(mirror, off: OffKeys("l:M1"), record: ["l:M1", "l:M2", "w:93"]);
            Check("... the rule over the ticked copies picks a copy that can't take the first slot while a mod needs the id: the next ticked copy still to "
                + "come loads there (swapped), not the copy that's off, set aside; the plan names the blocked one",
                ok && plan.GetValueOrDefault(X) is { Why: SameIdLoad.Reason.DependencyAfterSlot, Swapped: true, Blocked: "l:L1", Late: "SmmSameIdD" } entry
                && entry.Loads == l2 && list[1] == l2 && w2.state == Aside && l1.state == Aside && w1.state == Aside
                && okMirror && planMirror.GetValueOrDefault(X) is { Swapped: true } mirrored
                && (Compat.WorkshopBeatsOlderLocal ? mirrored is { Why: SameIdLoad.Reason.DependencyAfterSlot, Blocked: "w:93", Late: "SmmSameIdD" }
                    : mirrored is { Why: SameIdLoad.Reason.None, Blocked: null }) // stable: its rule takes MW out of the ticked copies, so M2, no block
                && mirrored.Loads == m2 && mirror[1] == m2 && m1.state == Aside && mw.state == Aside,
                $"{Say(plan, w1, w2, l1, l2)} || {Say(planMirror, m1, m2, mw)}");
        }
        {
            // The fallback goes the same way whether or not some copy is off: every copy ticked (W0 0.5 first, W1 3.0 needing D
            // after the slot, W2 2.0), a mod needing the id: W2, the best that fits, swapped into the first slot, not the
            // game's own choice W0. And past a pick that's blocked too: W0 off, W1 (3.0) and W2 (2.0) both needing D, W3
            // (1.0) not: W3.
            var (w0, w1, w2) = (Made(X, "W0", None, 160, "0.5.0"), Made(X, "W1", None, 161, "3.0.0", "SmmSameIdD"), Made(X, "W2", None, 162, "2.0.0"));
            List<Mod> all = [us, w0, Made("SmmSameIdNeeder", "M", needs: [X]), Made("SmmSameIdD", "D"), w1, w2];
            var (okAll, planAll) = PlanOf(all, record: ["w:160", "w:161", "w:162"]);
            var (v0, v1, v2, v3) = (Made(X, "V0", None, 170, "0.5.0"), Made(X, "V1", None, 171, "3.0.0", "SmmSameIdD"), Made(X, "V2", None, 172, "2.0.0", "SmmSameIdD"),
                Made(X, "V3", None, 173, "1.0.0"));
            List<Mod> past = [us, v0, Made("SmmSameIdNeeder", "M", needs: [X]), Made("SmmSameIdD", "D"), v1, v2, v3];
            var (okPast, planPast) = PlanOf(past, off: OffKeys("w:170"), record: ["w:170", "w:171", "w:172", "w:173"]);
            Check("... a blocked pick's fallback: the best other ticked copy that fits, whether or not a copy is off (W2, not W0), and past one blocked too (V3)",
                okAll && planAll.GetValueOrDefault(X) is { Why: SameIdLoad.Reason.DependencyAfterSlot, Blocked: "w:161", Swapped: true } a && a.Loads == w2 && all[1] == w2
                && w0.state == Aside && w1.state == Aside
                && okPast && planPast.GetValueOrDefault(X) is { Why: SameIdLoad.Reason.DependencyAfterSlot, Blocked: "w:171", Swapped: true } p && p.Loads == v3 && past[1] == v3
                && v0.state == Aside && v1.state == Aside && v2.state == Aside,
                $"{Say(planAll, w0, w1, w2)} || {Say(planPast, v0, v1, v2, v3)}");
            // Each plan keeps the id's copies in the order before the swap: the next launch's, for the page.
            Check("... the plan keeps the copies' order from before the swap (the next launch's)", planAll.GetValueOrDefault(X)?.Order is ["w:160", "w:161", "w:162"],
                string.Join(",", planAll.GetValueOrDefault(X)?.Order ?? []));
        }
        {
            // The blocked pick's fallback is the rule over the other ticked copies, so a copy that's off decides nothing
            // there either. (a) L1 off (1.0, read first) lost to W1 (2.0) at the game's rule; the rule over the ticked copies
            // picks L2 (2.5), blocked by E: without L2, L3 (2.2) beats W1, so L3, not W1. (b) W2 off (3.0) and W1 (2.5, needs E)
            // beat L1 (2.0) at the game's rule; W1 is blocked: without it, L1, brought back, not W2. On stable (b)'s rule over
            // the ticked copies picks L1 straight away.
            var (aw1, al2, al3, al1) = (Made(X, "W1", None, 191, "2.0.0"), Made(X, "L2", None, version: "2.5.0", needs: ["SmmSameIdE"]), Made(X, "L3", None, version: "2.2.0"),
                Made(X, "L1", Aside, version: "1.0.0"));
            List<Mod> listA = [us, aw1, al2, al3, Made("SmmSameIdNeeder", "M", needs: [X]), Made("SmmSameIdE", "E"), al1];
            var (okA, planA) = PlanOf(listA, off: OffKeys("l:L1"), record: ["l:L1", "l:L2", "l:L3", "w:191"]);
            var (bw2, bw1, bl1) = (Made(X, "W2", None, 182, "3.0.0"), Made(X, "W1", None, 181, "2.5.0", "SmmSameIdE"), Made(X, "L1", Aside, version: "2.0.0"));
            List<Mod> listB = [us, bw2, bw1, Made("SmmSameIdNeeder", "M", needs: [X]), Made("SmmSameIdE", "E"), bl1];
            var (okB, planB) = PlanOf(listB, off: OffKeys("w:182"), record: ["w:181", "w:182"]);
            Check("... the blocked pick's fallback is the rule over the other ticked copies: a copy that's off decides nothing (L3, not W1), and a ticked copy "
                + "it beat comes back (L1, not W2, which is off)",
                okA && planA.GetValueOrDefault(X) is { Why: SameIdLoad.Reason.DependencyAfterSlot, Blocked: "l:L2" } pa && pa.Loads == al3 && listA[1] == al3
                && aw1.state == Aside && al2.state == Aside
                && okB && planB.GetValueOrDefault(X) is { } pb && pb.Loads == bl1 && listB[1] == bl1 && bl1.state == None && bw2.state == Aside && bw1.state == Aside
                && (Compat.WorkshopBeatsOlderLocal ? pb is { Why: SameIdLoad.Reason.DependencyAfterSlot, Blocked: "w:181" } : pb.Why == SameIdLoad.Reason.None),
                $"{Say(planA, aw1, al2, al3, al1)} || {Say(planB, bw2, bw1, bl1)}");
        }
        {
            // The copy at the id's first slot can't stay there when what it needs loads after it: C (2.0) at the slot needs
            // D, which is off (after every mod still to load); B (3.0), chosen, is blocked by E. The fallback passes over C
            // to A, swapped in, C set aside, so C doesn't load to fail for D. Fails on 237b64b: its Run's Fits let the copy
            // at the slot fit whatever it needs, so C loaded there and failed MISSING_DEPENDENCY.
            var (c, a, b) = (Made(X, "C", version: "2.0.0", needs: ["SmmSameIdD"]), Made(X, "A"), Made(X, "B", version: "3.0.0", needs: ["SmmSameIdE"]));
            List<Mod> list = [us, c, a, Made("SmmSameIdE", "E"), b, Made("SmmSameIdNeeder", "M", needs: [X]), Made("SmmSameIdD", "D", ModLoadState.Disabled)];
            var (ok, plan) = PlanOf(list);
            Check("... the copy at the id's first slot needing a mod that's off, the copy chosen blocked: the fallback passes over it, A swapped in, C set aside",
                ok && plan.GetValueOrDefault(X) is { Why: SameIdLoad.Reason.DependencyAfterSlot, Blocked: "l:B", Late: "SmmSameIdE", Swapped: true, To: 1 } entry
                && entry.Loads == a && list[1] == a && list[2] == c && a.state == None && c.state == Aside && b.state == Aside && entry.SetAside.SequenceEqual([c, b]),
                Say(plan, c, a, b));
        }
        {
            // A dependency of the copy chosen that's off doesn't stop it: it loads and fails, as a mod installed once does,
            // and the page's warnings say so. After the slot it's swapped in, a mod needing the id notwithstanding; at the
            // slot it stays. Fails on 32e3d69: its Run counted the mod that's off as after the slot (the game lists the mods
            // that are off last), so A in both.
            var (a, b) = (Made(X, "A"), Made(X, "B", version: "1.1.0", needs: ["SmmSameIdOff"]));
            List<Mod> after = [us, a, Made("SmmSameIdUser", "U", needs: [X]), b, Made("SmmSameIdOff", "Off", ModLoadState.Disabled)];
            var (okAfter, planAfter) = PlanOf(after);
            var (a2, b2) = (Made(X, "A"), Made(X, "B", version: "1.1.0", needs: ["SmmSameIdOff"]));
            List<Mod> at = [us, b2, a2, Made("SmmSameIdUser", "U", needs: [X]), Made("SmmSameIdOff", "Off", ModLoadState.Disabled)];
            var (okAt, planAt) = PlanOf(at);
            Check("... the copy chosen needing a mod that's off, a mod after us needing the id: not blocked (it loads and fails, as a mod installed once "
                + "does): after the slot swapped in, at the slot left there",
                okAfter && planAfter.GetValueOrDefault(X) is { Why: SameIdLoad.Reason.None, Swapped: true } moved && moved.Loads == b && after[1] == b && a.state == Aside
                && okAt && planAt.GetValueOrDefault(X) is { Why: SameIdLoad.Reason.None, Swapped: false } kept && kept.Loads == b2 && at[1] == b2 && a2.state == Aside,
                $"{Say(planAfter, a, b)} || {Say(planAt, a2, b2)}");
        }
        {
            // A copy can take a blocked one's place only with everything it needs installed, on and before the slot: C (3.0)
            // blocked by E, B (2.0) needing a mod that isn't installed, so A. Fails on 32e3d69: its Fits let a dependency
            // that isn't installed through, so B, which fails for it.
            var (a, b, c) = (Made(X, "A"), Made(X, "B", version: "2.0.0", needs: ["SmmSameIdNotInstalled"]), Made(X, "C", version: "3.0.0", needs: ["SmmSameIdE"]));
            List<Mod> list = [us, a, b, Made("SmmSameIdNeeder", "M", needs: [X]), Made("SmmSameIdE", "E"), c];
            var (ok, plan) = PlanOf(list);
            Check("... a blocked pick's fallback passes over a ticked copy needing a mod that isn't installed: A, not B",
                ok && plan.GetValueOrDefault(X) is { Why: SameIdLoad.Reason.DependencyAfterSlot, Blocked: "l:C", Swapped: false } entry && entry.Loads == a && list[1] == a
                && b.state == Aside && c.state == Aside, Say(plan, a, b, c));
        }
        {
            // Nor over one below a minimum version of the id a mod still to load needs (as the game reads it): C (3.0) blocked
            // by E, B (2.0) ticked, A (2.5.0) not, our record B first, M needing the id at 2.5.0 or newer, N at 3.0.0, off: A,
            // at the slot (the game takes a copy at the minimum). Fails on ec684b6: its Fits didn't look at versions, so B,
            // which M fails for.
            var (a, b, c) = (Made(X, "A", version: "2.5.0"), Made(X, "B", version: "2.0.0"), Made(X, "C", version: "3.0.0", needs: ["SmmSameIdE"]));
            var (needer, offNeeder) = (Made("SmmSameIdNeeder", "M"), Made("SmmSameIdNeederOff", "N", ModLoadState.Disabled));
            if (needer.manifest is { } manifest)
                manifest.dependencies = [new ModDependency(X, "2.5.0")];
            if (offNeeder.manifest is { } offManifest)
                offManifest.dependencies = [new ModDependency(X, "3.0.0")];
            List<Mod> list = [us, a, b, needer, offNeeder, Made("SmmSameIdE", "E"), c];
            var (ok, plan) = PlanOf(list, off: OffKeys("l:A"), record: ["l:B", "l:A", "l:C"]);
            Check("... nor over one below a minimum version a mod still to load needs: A (2.5.0), not B (2.0), for a mod needing 2.5.0 (one needing 3.0.0 off)",
                ok && plan.GetValueOrDefault(X) is { Why: SameIdLoad.Reason.DependencyAfterSlot, Blocked: "l:C", Swapped: false } entry && entry.Loads == a && list[1] == a
                && b.state == Aside && c.state == Aside, Say(plan, a, b, c));
        }
        {
            // No other ticked copy can take a blocked one's place: of the copies still to load other than the blocked one, by
            // the tie order (our record's), the first that can, ticked or not (C 3.0 ticked, blocked by D; B 1.5 ticked,
            // needing D too; A 1.0 not ticked, needing nothing: A, swapped into the slot); none can: the first (C at the slot
            // blocked by D after it, as only a cycle leaves them, A and B needing D too, our record C, B, A: B, swapped in, not
            // A, first in the list, nor C); none but the blocked copy's (a Workshop item with two manifests, the other copy
            // off): its first, at the slot. The first fails on 32e3d69 (it took the first copy still to load, B, which loads
            // before D and fails), the second on ec684b6 (the list's order: A).
            var (b, a, c) = (Made(X, "B", version: "1.5.0", needs: ["SmmSameIdD"]), Made(X, "A"), Made(X, "C", version: "3.0.0", needs: ["SmmSameIdD"]));
            List<Mod> list = [us, b, a, Made("SmmSameIdNeeder", "M", needs: [X]), Made("SmmSameIdD", "D"), c];
            var (ok, plan) = PlanOf(list, off: OffKeys("l:A"), record: ["l:A", "l:B", "l:C"]);
            var (b2, a2, c2) = (Made(X, "B", version: "1.5.0", needs: ["SmmSameIdD"]), Made(X, "A", needs: ["SmmSameIdD"]), Made(X, "C", version: "3.0.0", needs: ["SmmSameIdD"]));
            List<Mod> none = [us, c2, a2, b2, Made("SmmSameIdNeeder", "M", needs: [X]), Made("SmmSameIdD", "D")];
            var (okNone, planNone) = PlanOf(none, off: OffKeys("l:A"), record: ["l:C", "l:B", "l:A"]);
            var (w5a, w5b) = (Made(X, "W5a", item: 5), Made(X, "W5b", item: 5, version: "2.0.0", needs: ["SmmSameIdD"]));
            List<Mod> one = [us, w5a, Made("SmmSameIdNeeder", "M", needs: [X]), Made("SmmSameIdD", "D"), w5b, Made(X, "L", ModLoadState.Disabled)];
            var (okOne, planOne) = PlanOf(one);
            Check("... a blocked pick with no other ticked copy that can take the slot: the first copy still to load that can, ticked or not (A, swapped "
                + "in); none can: the first other by the tie order (B, swapped in past A and the blocked C at the slot); none but its own: its first manifest, "
                + "at the slot",
                ok && plan.GetValueOrDefault(X) is { Why: SameIdLoad.Reason.DependencyAfterSlot, Blocked: "l:C", Swapped: true } entry && entry.Loads == a && list[1] == a
                && b.state == Aside && c.state == Aside
                && okNone && planNone.GetValueOrDefault(X) is { Why: SameIdLoad.Reason.DependencyAfterSlot, Blocked: "l:C", Swapped: true } first && first.Loads == b2
                && none[1] == b2 && c2.state == Aside && a2.state == Aside
                && okOne && planOne.GetValueOrDefault(X) is { Blocked: "w:5", Swapped: false } own && own.Loads == w5a && w5b.state == Aside,
                $"{Say(plan, b, a, c)} || {Say(planNone, b2, a2, c2)} || {Say(planOne, w5a, w5b)}");
        }
        {
            // The copy chosen at the slot needing a mod that's on after it (as a dependency cycle leaves them) while no mod
            // still to load needs the id: it stays and loads there, the other set aside, no reason (nothing looks for the id
            // at its first slot).
            var (c, a) = (Made(X, "C", version: "2.0.0", needs: ["SmmSameIdD"]), Made(X, "A"));
            List<Mod> list = [us, c, a, Made("SmmSameIdD", "D")];
            var (ok, plan) = PlanOf(list);
            Check("... the copy chosen at the slot needing a mod after it, no mod needing the id: it loads there, the other set aside",
                ok && plan.GetValueOrDefault(X) is { Why: SameIdLoad.Reason.None, Swapped: false, From: 1, To: 1 } entry && entry.Loads == c && list[1] == c
                && a.state == Aside, Say(plan, c, a));
        }
        {
            // A copy brought back loads with the game's manifest messages, which its rule cleared when it beat the copy: an
            // old-style dependency list's MIGRATION_REQUIRED, read again from the copy's own manifest (another JSON file in
            // its folder passed over).
            var dir = Path.Combine(Path.GetTempPath(), "SmmSameIdRevive");
            Directory.CreateDirectory(dir);
            try
            {
                File.WriteAllText(Path.Combine(dir, "notes.json"), "[1, 2]");
                File.WriteAllText(Path.Combine(dir, "mod.json"), $$"""{"id": "{{X}}", "name": "{{X}}", "version": "2.0.0", "dependencies": ["SmmSameIdNone"]}""");
                var (w1, w2, local) = (Made(X, "W1", Aside, 71), Made(X, "W2", None, 72, "3.0.0"), Made(X, "L", Aside, version: "2.0.0"));
                local.path = dir.Replace('\\', '/');
                local.errors = []; // as the game's rule left it: cleared
                var (ok, plan) = Plan([us, w2, local, w1], off: OffKeys("w:72"), record: ["w:71", "w:72"]);
                Check("... a copy brought back gets the game's manifest messages again (an old-style dependency list's), which the rule cleared",
                    ok && plan.GetValueOrDefault(X)?.Loads == local && local.state == None && local.errors?.Any(e => e.LocEntryKey == "MOD_ERROR.MIGRATION_REQUIRED") == true,
                    $"{Say(plan, w1, w2, local)}; errors {string.Join(",", local.errors?.Select(e => e.LocEntryKey) ?? [])}");
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        {
            // A copy brought back that can't take the first slot (a dependency of it later in the list) while no mod still to
            // load needs the id: it loads from its own slot, and the self-check takes that as planned (no mod looked for the
            // id at its first slot; one that did and missed it would fail Misses).
            var (w1, w2, dep, local) = (Made(X, "W1", Aside, 81), Made(X, "W2", None, 82, "3.0.0"), Made("SmmSameIdDep", "D"),
                Made(X, "L", Aside, version: "2.0.0", needs: ["SmmSameIdDep"]));
            List<Mod> list = [us, w2, dep, local, w1];
            var (ok, plan) = PlanOf(list, off: OffKeys("w:82"), record: ["w:81", "w:82"]);
            var entry = plan.GetValueOrDefault(X);
            (local.state, dep.state) = (ModLoadState.Loaded, ModLoadState.Loaded); // as the game's loop leaves them
            var verdict = entry == null ? (SameIdLoad.Result?)null : SameIdLoad.Judge(X, entry, list, 0);
            Check("... a copy brought back that stays in its own slot (its dependency later in the list, no mod needing the id): it loads there, and the "
                + "self-check says taken", ok && entry is { Swapped: false, Why: SameIdLoad.Reason.None } && entry.Loads == local && list[3] == local
                && w2.state == Aside && verdict == SameIdLoad.Result.Taken, $"{Say(plan, w1, w2, local)}; self-check {verdict}");
        }
        {
            // One Workshop item with two manifests of the id (one copy, w:5) beside another item at the same version: the
            // older item, the copy installed first by age; its first manifest (both the same version) swapped into the id's
            // first slot, its second set aside too (the game would fail it).
            var (w6, w5a, w5b) = (Made(X, "W6", item: 6), Made(X, "W5a", item: 5), Made(X, "W5b", item: 5));
            List<Mod> list = [us, w6, w5a, w5b];
            var (ok, plan) = PlanOf(list);
            Check("... one Workshop item with two manifests of the id: one copy, its first manifest loads, swapped there, the rest set aside",
                ok && plan.GetValueOrDefault(X) is { Swapped: true } entry && entry.Loads == w5a && list[1] == w5a
                && w6.state == Aside && w5b.state == Aside, Say(plan, w6, w5a, w5b));
        }
        {
            // A file from before copies_off (0.1.4): its pick alone ticked (Copies.LegacyOff over our record), though another
            // copy is higher; the pick missing, the copies the record knows all read ticked, and the higher loads.
            var legacy = Copies.LegacyOff([$"{X}|l:A"], new Dictionary<string, List<string>> { [X] = ["l:A", "l:B", "l:C"] });
            var (a, b, c) = (Made(X, "A"), Made(X, "B", version: "1.1.0"), Made(X, "C", version: "1.2.0"));
            var (ok, plan) = Plan([us, a, b, c], off: legacy, record: ["l:A", "l:B", "l:C"]);
            var (b2, c2) = (Made(X, "B", version: "1.1.0"), Made(X, "C", version: "1.2.0"));
            var (okMissing, planMissing) = Plan([us, b2, c2], off: legacy, record: ["l:A", "l:B", "l:C"]);
            Check("... a file from before copies_off: its pick alone ticked (every other copy in the record off), though another copy is higher; the pick "
                + "missing, the others read ticked and the higher loads",
                legacy.SequenceEqual([$"{X}|l:B", $"{X}|l:C"]) && ok && plan.GetValueOrDefault(X)?.Loads == a && b.state == Aside && c.state == Aside
                && okMissing && planMissing.GetValueOrDefault(X)?.Loads == c2 && b2.state == Aside,
                $"legacy off {string.Join(", ", legacy)}; {Say(plan, a, b, c)} || {Say(planMissing, b2, c2)}");
        }
        {
            // A copy set aside with a message of the game's on it (a Steam branch error): off its errors, kept for its panel.
            var (a, b) = (Made(X, "A"), Made(X, "B"));
            var message = Error("MOD_ERROR.STEAM_BRANCH_UNSUPPORTED");
            b.errors = [message];
            try
            {
                var (ok, _) = Plan([us, a, b], off: OffKeys("l:B"), record: ["l:A", "l:B"]);
                Check("... a copy set aside: the game's messages on it go from its errors (as the game clears its duplicates'), kept for its info panel",
                    ok && b.state == Aside && b.errors == null && BranchErrors.Dropped(b).Contains(message), $"errors {b.errors?.Count}, kept {BranchErrors.Dropped(b).Count}");
            }
            finally
            {
                BranchErrors.TestForget(b);
            }
        }
        {
            // The self-check on a plan, against the states the game left: as planned, the chosen copy failing for its own
            // reasons, two copies loaded, a copy set aside back to load, a duplicate error after us, a mod needing the id
            // missing it, the chosen copy not the id's first, and the plan's own reason.
            var (a, b, user) = (Made(X, "A"), Made(X, "B"), Made("SmmSameIdUser", "U", needs: [X]));
            List<Mod> list = [us, a, b, user];
            var (_, plan) = PlanOf(list, off: OffKeys("l:B"), record: ["l:A", "l:B"]);
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
            Check("... the self-check: taken as planned; the chosen copy failed for its own reasons; not taken for two loaded, a set-aside copy back to "
                + "load, a duplicate error after us, a mod needing the id missing it, or the chosen copy not the id's first; a plan with a reason: not applied",
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

    // The fixture twins this launch: B (the higher version, and the fixture's legacy pick: A off) loaded though it sorts
    // second, the other set aside as the game's duplicate with no messages, no duplicate error anywhere, B first of the id
    // in the game's list (so Test Twin User, needing 1.1.0, loaded), swapped there; the saved list has both copies' entries
    // on, ours first; the self-check says taken; and the twins add nothing to the MODDED label's errors. First, every id
    // Apply planned for (real Workshop twins with dev.cs test --workshop a,b --twins too): what loads, whether it was
    // swapped, the self-check.
    static void TestSameIdLaunch()
    {
        foreach (var (id, e) in SameIdLoad.Plan)
        {
            Results.Add($"INFO same ID: {id} loads {(e.Loads == null ? "none" : Copies.CopyOf(e.Loads))}, swapped {e.Swapped} ({e.From}->{e.To}), "
                + $"why {e.Why}, set aside {string.Join(",", e.SetAside.Select(Copies.CopyOf))}, self-check {SameIdLoad.ResultOf(id)}");
            Check($"same ID, {id}: the self-check taken (or the chosen copy failed for its own reasons), the canary fine",
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
        Check("same ID, the fixture twins: B (the higher version, A off by the fixture's legacy pick) loaded, sorted second, the other set aside as the "
            + "game's duplicate with no messages, no duplicate error anywhere, B the id's first in the game's list, Test Twin User (needs 1.1.0) loaded",
            b.state == ModLoadState.Loaded && a.state == ModLoadState.DisabledDuplicate && a.errors == null && copies[0] == b && user.state == ModLoadState.Loaded
            && !ModManager.Mods.Any(m => m.errors?.Any(e => e.LocEntryKey == "MOD_ERROR.DUPLICATE_ID") == true), States());
        var plan = SameIdLoad.Plan.GetValueOrDefault(TwinId);
        Check("... swapped into the id's first slot", plan is { Swapped: true, Why: SameIdLoad.Reason.None } entry
            && entry.Loads == b && entry.SetAside.SequenceEqual([a]) && entry.To < entry.From,
            plan == null ? "no plan" : $"loads {Copies.KeyOf(plan.Loads)}, swapped {plan.Swapped} ({plan.From}->{plan.To}), why {plan.Why}");
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
    // local one was off at launch): put back, the source that's on first in the id's places, ours at the top; saved once
    // (a second pass changes nothing), and for its order alone. Ours the same way: a copy of ours read off at launch stays
    // off. And this launch's Settle ran before the baseline: ours first in it, and nothing of the twins changed since
    // launch; our file from before copies_off (the fixture's) read as it was, A off, before the baseline too, copy_picks
    // gone.
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
        // The rebuild in another order than the list read (the game sorted it, or we swapped a copy in): Settle saves it,
        // which the game does later if at all (leaving its settings screen, a control remapped, quitting), so the next
        // launch sorts by it, as the page foresees, after a crash too; the same order, or no list read, isn't a reason to.
        List<(string, ModSource, bool)> readList = [(ModEntry.ModId, ModSource.ModsDirectory, true), ("SmmSameIdA", ModSource.ModsDirectory, true),
            ("SmmSameIdB", ModSource.ModsDirectory, true)];
        List<SettingsSaveMod> Rebuild(params string[] ids) => [.. ids.Select(id => E(id, ModSource.ModsDirectory, true))];
        Check("... the rebuilt list in another order than the launch read: saved for that alone (Reordered); in the same order, or with none read: not",
            SameIdLoad.Reordered(Rebuild(ModEntry.ModId, "SmmSameIdB", "SmmSameIdA"), readList) && !SameIdLoad.Reordered(Rebuild(ModEntry.ModId, "SmmSameIdA", "SmmSameIdB"), readList)
            && !SameIdLoad.Reordered(Rebuild("SmmSameIdB", "SmmSameIdA"), null));
        // A copy new this launch (its source not in the list the game read): it keeps what the rebuild gave it (the
        // id's first entry's, as the game does without us), and nothing else changes.
        List<SettingsSaveMod> withNew = [E(ModEntry.ModId, ModSource.ModsDirectory, true), E(X, ModSource.ModsDirectory, false), E(X, ModSource.SteamWorkshop, false)];
        var newChanged = SameIdLoad.SettleList(withNew, [(ModEntry.ModId, ModSource.ModsDirectory, true), (X, ModSource.ModsDirectory, false)]);
        Check("... a copy new this launch (its source not in the list read): it keeps the rebuild's, nothing to save", !newChanged
            && withNew.Select(e => e.IsEnabled).SequenceEqual([true, false, false]), string.Join(", ", withNew.Select(e => $"{e.Id}/{e.Source}={e.IsEnabled}")));
        // Ours from two sources, the Workshop entry read off at launch (the player unticked it) and turned on by the rebuild
        // (it copies the local entry's on): Repair puts it off again, and nothing turns it back on; once.
        List<SettingsSaveMod> ours = [E(ModEntry.ModId, ModSource.ModsDirectory, true), E(ModEntry.ModId, ModSource.SteamWorkshop, true), E(X, ModSource.ModsDirectory, true)];
        List<(string, ModSource, bool)> oursRead = [(ModEntry.ModId, ModSource.ModsDirectory, true), (ModEntry.ModId, ModSource.SteamWorkshop, false), (X, ModSource.ModsDirectory, true)];
        var oursFirst = SameIdLoad.SettleList(ours, oursRead);
        var oursAgain = SameIdLoad.SettleList(ours, oursRead);
        Check("... ours from two sources, the Workshop copy read off at launch and turned on by the rebuild: put off again, and it stays off; once",
            oursFirst && !oursAgain && ours.FirstOrDefault(e => e.Id == ModEntry.ModId && e.Source == ModSource.SteamWorkshop) is { IsEnabled: false }
            && ours.FirstOrDefault(e => e.Id == ModEntry.ModId && e.Source == ModSource.ModsDirectory) is { IsEnabled: true },
            string.Join(", ", ours.Select(e => $"{e.Id}/{e.Source}={e.IsEnabled}")) + $"; first {oursFirst}, again {oursAgain}");
        // This launch: Settle ran before the baseline (its moves are no change since launch, nor an undo step), ours first in
        // it and in the saved list. Where ours didn't load first (Better Mod Menu listed before us, with --bmm), it moved us.
        // Our data from before copies_off (the fixture's file) read as it was, A off (its legacy pick B), before the baseline
        // too: in launch's snapshot and our file, copy_picks gone from both, and no change since launch.
        var loadedFirst = ModManager.Mods.FirstOrDefault()?.manifest?.id == ModEntry.ModId;
        const string offA = $"{TwinId}|l:SmmTestTwinA";
        var fileJson = JsonNode.Parse(File.ReadAllText(Store.FilePath)) as JsonObject;
        var migrated = Copies.TwinGroups().All(g => g.Id != TwinId) || History.Loaded is { } launch && Store.TicksIn(launch.Store).Off.Contains(offA)
            && !launch.Store.Contains("copy_picks") && FileCopiesOff().Contains(offA) && fileJson?.ContainsKey("copy_picks") == false
            && (int?)fileJson?["version"] == 1 && Store.Data.Version == 1;
        Check("... this launch's settling: before the baseline, which has ours first, as the saved list does; the twins' copy A off there (copies_off, "
            + "read from a file from before it, in our file too, copy_picks gone, the version still 1); no change since launch for the twins, nothing moved since",
            SameIdLoad.Settled && History.Loaded?.Mods.FirstOrDefault()?.Id == ModEntry.ModId && !HistoryBar.Describe().Any(l => l.Contains("Test Twin"))
            && SaveManager.Instance.SettingsSave.ModSettings?.ModList.FirstOrDefault()?.Id == ModEntry.ModId && HistoryBar.Moves().Count == 0
            && !LoadOrder.OrderChanged && migrated, $"{string.Join(" | ", HistoryBar.Describe())}; off now {string.Join(", ", CopiesOff())}, in our file "
            + $"{string.Join(", ", FileCopiesOff())}, copy_picks in the file {fileJson?.ContainsKey("copy_picks")}, version {Store.Data.Version}");
        if (loadedFirst)
            Results.Add("INFO same ID, settling: ours loaded first this launch, so Settle had nothing of ours to move");
    }

    // Our data for copies at the main menu (copies_off, StoreData.CopiesOff, and the record, StoreData.Copies), as saved:
    // sorted, read back the same by a fresh load and from a snapshot. Then made-up copies of ids (in the game's list for a
    // moment) and what Reconcile, a launch (SameIdLoad.Run) and profiles make of data:
    // - a file from before copies_off (0.1.4): its pick alone ticked, every other copy in the record off, installed or not,
    //   so with the pick missing the others read ticked and the pick back loads alone; copy_picks gone; the file's version
    //   and mods as they were; a copy new at that launch reads ticked at Init and after (N2); a dev build's file (both
    //   fields): copy_picks dropped, copies_off as it was;
    // - a copy alone on its switch keeps its copies_off entry and reads ticked;
    // - the ticked copy missing for a launch: the others read ticked (Best of them loads), copies_off unchanged; back, it
    //   alone;
    // - a copy new to the record joining: a lone copy in copies_off leaves it (a lower new copy never takes over; a higher
    //   one does by version), in our data and in each profile that had it off (N1); with the ticked copy missing, the
    //   known ones leave it, and back, every copy ticked (risk 16); copies new at once join in age order (TieOrder);
    // - profiles: one naming a missing copy, applied, writes its copies off whole (P9); switched to with its ticked copy
    //   missing, then that copy back, as it has it and still Saved (P8); saving with a copy missing keeps the others' off
    //   (P8); an old profile's fill leaves out copies new this session (N2), and with its copy missing as one joins, the
    //   installed ones as well, as Join leaves an entry that had them off (Saved, Discard keeping the higher loading),
    //   whether Join took them out of our data too or our data's pick was another copy; an inactive profile saved
    //   before a higher copy came loads that copy when switched to (P18, pinned);
    // - a tick with the switch off and the ticked copy missing: that copy off when it's back (N6).
    static void TestCopiesData(string outDir)
    {
        if (Copies.TwinGroups().All(g => g.Id != TwinId))
        {
            TwinsMissing("copies_off");
            return;
        }
        var (now, file, fresh) = (CopiesOff(), FileCopiesOff(), Store.LoadFrom(Store.Dir, DateTime.Now).Data?.CopiesOff);
        Check("copies_off: the fixture twins' copy that doesn't load (A) off, written sorted, read back the same by a fresh load and from a snapshot",
            now.Contains($"{TwinId}|l:SmmTestTwinA") && !now.Contains($"{TwinId}|l:SmmTestTwinB") && file.SequenceEqual(now)
            && file.SequenceEqual(file.Order(StringComparer.Ordinal)) && fresh?.SequenceEqual(now) == true
            && Store.TicksIn(Store.EditsJson()).Off.SetEquals(now),
            $"now {string.Join(", ", now)}; file {string.Join(", ", file)}; fresh {string.Join(", ", fresh ?? [])}");
        var us = Made(ModEntry.ModId, "SpireModManager");
        static Dictionary<string, List<string>> RecordOf(string id, params string[] copies) => new() { [id] = [.. copies] };
        static List<string> OffOf(StoreData data, string id) => [.. (data.CopiesOff ?? []).Where(k => Copies.IdOf(k) == id).Select(k => Copies.Shown(k[(k.IndexOf('|') + 1)..]))];
        static string Say(StoreData data, string id) => $"off [{string.Join(", ", OffOf(data, id))}], record {string.Join(",", data.Copies?.GetValueOrDefault(id) ?? [])}, "
            + $"picks {string.Join(",", data.CopyPicks ?? [])}";
        // A launch with these copies (one id's) and our data as given: the copy that loads (the plan's, or the one left).
        string Launch(StoreData from, params Mod[] copies)
        {
            var plan = SameIdLoad.Run(new List<Mod>(copies.Prepend(us)), () => new Ticks(from.CopiesOff ?? Copies.LegacyOff(from.CopyPicks, from.Copies), from.Copies),
                m => m == us, log: false).Plan.GetValueOrDefault(copies[0].manifest?.id ?? "");
            return plan?.Loads is { } loads ? Copies.Shown(Copies.CopyOf(loads)) : copies.SingleOrDefault(m => m.state == ModLoadState.None) is { } only
                ? Copies.Shown(Copies.CopyOf(only)) : "none";
        }
        // What the page has with these copies in the game's list and our data as given: what loads, and which are ticked.
        (string Loads, string Ticked) Page(StoreData data, params Mod[] copies)
        {
            GameMods().AddRange(copies);
            Copies.TestRecount();
            try
            {
                var ticks = new Ticks(data.CopiesOff ?? Copies.LegacyOff(data.CopyPicks, data.Copies), data.Copies);
                var id = copies[0].manifest?.id ?? "";
                return (Copies.LoadingIn(id, [], ticks) is { } loads ? Copies.Shown(Copies.CopyOf(loads)) : "none",
                    string.Join(",", copies.Where(m => Copies.TickedIn(m, [], ticks)).Select(m => Copies.Shown(Copies.CopyOf(m)))));
            }
            finally
            {
                foreach (var copy in copies)
                    GameMods().Remove(copy);
                Copies.TestRecount();
            }
        }
        // Reconcile with these copies in the game's list (as a menu with them installed).
        bool Reconciled(StoreData data, params Mod[] copies)
        {
            GameMods().AddRange(copies);
            Copies.TestRecount();
            try
            {
                return Copies.Reconcile(data);
            }
            finally
            {
                foreach (var copy in copies)
                    GameMods().Remove(copy);
                Copies.TestRecount();
            }
        }

        // 0.1.4: a file with copy_picks and copies, read through the store, reconciled and saved again.
        const string X = "SmmSameIdOff";
        var legacyDir = Path.Combine(outDir, "copies-legacy");
        if (Directory.Exists(legacyDir))
            Directory.Delete(legacyDir, recursive: true);
        Directory.CreateDirectory(legacyDir);
        const string mods = "{ \"SmmSameIdOff\": { \"alias\": \"Off\" }, \"SmmSameIdOff|l:B\": { \"notes\": \"b\" } }";
        File.WriteAllText(Path.Combine(legacyDir, "manager.json"), $"{{ \"version\": 1, \"mods\": {mods}, \"copies\": {{ \"{X}\": [\"l:A\", \"l:B\", \"l:C\"] }}, "
            + $"\"copy_picks\": [\"{X}|l:A\"] }}");
        var legacy = Store.LoadFrom(legacyDir, DateTime.Now).Data ?? new StoreData();
        var (a, b, c) = (Made(X, "A"), Made(X, "B", ModLoadState.Loaded, version: "1.1.0"), Made(X, "C", version: "1.2.0"));
        var (firstChange, secondChange) = (Reconciled(legacy, a, b, c), Reconciled(legacy, a, b, c));
        Store.WriteTo(legacyDir, legacy, backUp: false);
        var written = JsonNode.Parse(File.ReadAllText(Path.Combine(legacyDir, "manager.json"))) as JsonObject;
        var modsSame = JsonNode.DeepEquals(written?["mods"], JsonNode.Parse(mods));
        var pickAlone = Page(legacy, Made(X, "A"), Made(X, "B", version: "1.1.0"), Made(X, "C", version: "1.2.0"));
        var pickMissing = Page(legacy, Made(X, "B", version: "1.1.0"), Made(X, "C", version: "1.2.0"));
        var oneOther = Page(legacy, Made(X, "B", version: "1.1.0"));
        var missingLaunch = Launch(legacy, Made(X, "B", version: "1.1.0"), Made(X, "C", version: "1.2.0"));
        Check("our data, a 0.1.4 file: copies_off from copy_picks and the record (every recorded copy but the pick off), once; copy_picks gone from the "
            + "saved file, its version still 1, mods as they were; the pick alone ticked and loading though the others are higher; the pick missing, the "
            + "same entries, the others reading ticked (the higher loads, page and launch); one other copy installed alone: ticked",
            firstChange && !secondChange && OffOf(legacy, X).SequenceEqual(["B", "C"]) && legacy.CopyPicks == null && written?.ContainsKey("copy_picks") == false
            && (int?)written?["version"] == 1 && modsSame && pickAlone == ("A", "A") && pickMissing == ("C", "B,C") && missingLaunch == "C" && oneOther == ("B", "B"),
            $"{Say(legacy, X)} (changed {firstChange}, then {secondChange}); mods kept {modsSame}; file {written?.ToJsonString()}; pick alone {pickAlone}, "
            + $"missing {pickMissing} (launch {missingLaunch}), one other {oneOther}");
        // N2: the same file, a copy new at that launch (not in the record yet): ticked at Init and after Reconcile.
        var newAtUpdate = new StoreData { CopyPicks = [$"{X}|l:A"], Copies = RecordOf(X, "l:A", "l:B") };
        var initTicked = new Ticks(Copies.LegacyOff(newAtUpdate.CopyPicks, newAtUpdate.Copies), newAtUpdate.Copies).Ticked(X, "l:D", ["l:A", "l:B", "l:D"]);
        Reconciled(newAtUpdate, Made(X, "A"), Made(X, "B"), Made(X, "D", version: "1.2.0"));
        var afterTicked = Page(newAtUpdate, Made(X, "A"), Made(X, "B"), Made(X, "D", version: "1.2.0"));
        Check("... a copy new at the update's launch (not in the record): ticked at Init and after the menu's Reconcile (the legacy reading made before it "
            + "joins), so what loads doesn't change at the menu", initTicked && !OffOf(newAtUpdate, X).Contains("D") && afterTicked == ("D", "A,D"),
            $"Init ticked {initTicked}; {Say(newAtUpdate, X)}; page {afterTicked}");
        // A dev build's file: copy_picks and copies_off both (the pick never in copies_off): copy_picks dropped only.
        var dev = new StoreData { CopyPicks = [$"{X}|l:A"], CopiesOff = [$"{X}|l:C"], Copies = RecordOf(X, "l:A", "l:B", "l:C") };
        Reconciled(dev, Made(X, "A"), Made(X, "B"), Made(X, "C"));
        Check("... a dev build's file (copy_picks and copies_off): copy_picks dropped, copies_off as it was", dev.CopyPicks == null && OffOf(dev, X).SequenceEqual(["C"]),
            Say(dev, X));

        // A copy alone on its switch now (its twin uninstalled) keeps its copies_off entry, and reads ticked.
        const string Y = "SmmSameIdAlone";
        var alone = new StoreData { CopiesOff = [$"{Y}|l:A"], Copies = RecordOf(Y, "l:A", "l:B") };
        Reconciled(alone, Made(Y, "A"));
        Check("... a copy alone on its switch now (its twin gone): it keeps its copies_off entry, and reads ticked and loads",
            OffOf(alone, Y).SequenceEqual(["A"]) && Page(alone, Made(Y, "A")) == ("A", "A"), Say(alone, Y));

        // The ticked copy missing for a launch: two copies, then three.
        const string Z = "SmmSameIdMissing";
        var two = new StoreData { CopiesOff = [$"{Z}|l:B"], Copies = RecordOf(Z, "l:A", "l:B") };
        Reconciled(two, Made(Z, "B"));
        var (twoMissing, twoBack, twoBackLaunch) = (Page(two, Made(Z, "B")), Page(two, Made(Z, "A"), Made(Z, "B")), Launch(two, Made(Z, "A"), Made(Z, "B")));
        Check("... the ticked copy missing for a launch (two copies): the other reads ticked and loads, copies_off unchanged; back: it alone ticked, and loads",
            twoMissing == ("B", "B") && OffOf(two, Z).SequenceEqual(["B"]) && twoBack == ("A", "A") && twoBackLaunch == "A",
            $"missing {twoMissing}; {Say(two, Z)}; back {twoBack} (launch {twoBackLaunch})");
        const string W = "SmmSameIdThree";
        var three = new StoreData { CopiesOff = [$"{W}|l:B", $"{W}|l:C"], Copies = RecordOf(W, "l:A", "l:B", "l:C") };
        var (threeMissing, threeLaunch) = (Page(three, Made(W, "B"), Made(W, "C", version: "1.1.0")), Launch(three, Made(W, "B"), Made(W, "C", version: "1.1.0")));
        var threeBack = Page(three, Made(W, "A"), Made(W, "B"), Made(W, "C", version: "1.1.0"));
        Check("... three copies, the ticked one missing: the other two read ticked and the higher loads, page and launch; back: it alone",
            threeMissing == ("C", "B,C") && threeLaunch == "C" && threeBack == ("A", "A"), $"missing {threeMissing} (launch {threeLaunch}); back {threeBack}");

        // A copy new to the record joining. A lone copy in copies_off (its ticked twin unsubscribed since), and a lower new
        // copy (game closed): the lone copy leaves copies_off, both ticked, it loads; a higher new copy loads by version.
        // Two profiles that had it off too keep it on (N1): the active one stays Saved, and switched to, the other loads it.
        const string V = "SmmSameIdLone";
        ProfileMod Holds() => new(V, ModSource.ModsDirectory, true, CopiesOff: ["l:A"]);
        var lone = new StoreData
        {
            CopiesOff = [$"{V}|l:A"], Copies = RecordOf(V, "l:A", "l:B"), ActiveProfile = "Here",
            Profiles = [new Profile { Name = "Here", Mods = [Holds()] }, new Profile { Name = "There", Mods = [Holds()], Edited = [Holds()] }],
        };
        var loneInit = Launch(lone, Made(V, "A", version: "1.1.0"), Made(V, "C"));
        Reconciled(lone, Made(V, "A", version: "1.1.0"), Made(V, "C"));
        var lonePage = Page(lone, Made(V, "A", version: "1.1.0"), Made(V, "C"));
        var held = lone.Profiles.SelectMany(p => p.Mods.Concat(p.Edited ?? [])).Select(m => m.CopiesOff ?? ["null"]).ToList();
        var higher = new StoreData { CopiesOff = [$"{V}|l:A"], Copies = RecordOf(V, "l:A", "l:B") };
        var higherInit = Launch(higher, Made(V, "A", version: "1.1.0"), Made(V, "C", version: "1.2.0"));
        Reconciled(higher, Made(V, "A", version: "1.1.0"), Made(V, "C", version: "1.2.0"));
        var higherPage = Page(higher, Made(V, "A", version: "1.1.0"), Made(V, "C", version: "1.2.0"));
        var loneEntry = lone.Profiles[0].Mods.Single();
        var loneUnsaved = Unsaved(lone, loneEntry, Made(V, "A", ModLoadState.Loaded, version: "1.1.0"), Made(V, "C"));
        var switchedTo = WithCopies(lone, V, [Made(V, "A", ModLoadState.Loaded, version: "1.1.0"), Made(V, "C")], () =>
        {
            Profiles.TestApply([.. lone.Profiles[1].Edited ?? []]);
            return LoadingCopy(V);
        });
        Check("... a lone copy in copies_off, a new lower copy joins (game closed): the lone copy loads at Init and after the menu (both ticked, it out of "
            + "copies_off), and two profiles that had it off have it on (the active one Saved, the other loading it when switched to); a higher new copy "
            + "loads by version",
            loneInit == "A" && OffOf(lone, V).Count == 0 && lonePage == ("A", "A,C") && held.All(h => h.Count == 0) && loneUnsaved == null && switchedTo == "l:A"
            && higherInit == "C" && higherPage == ("C", "A,C"),
            $"lower: Init {loneInit}, {Say(lone, V)}, page {lonePage}, profiles off [{string.Join(" | ", held.Select(h => string.Join(",", h)))}], unsaved "
            + $"{loneUnsaved ?? "none"}, switched to {switchedTo}; higher: Init {higherInit}, page {higherPage}");
        // The ticked copy missing, a new copy joining: the known ones leave copies_off (they read ticked), and once the
        // missing copy is back, every copy is ticked: no stored pick outlives a copy joining (risk 16).
        const string U = "SmmSameIdJoin";
        var join = new StoreData { CopiesOff = [$"{U}|l:B", $"{U}|l:C"], Copies = RecordOf(U, "l:A", "l:B", "l:C") };
        Reconciled(join, Made(U, "B"), Made(U, "C"), Made(U, "D"));
        var joinBack = Page(join, Made(U, "A", version: "1.1.0"), Made(U, "B"), Made(U, "C"), Made(U, "D"));
        Check("... the ticked copy missing, a new copy joining: the known copies leave copies_off; the missing one back, every copy ticked, the highest loads",
            OffOf(join, U).Count == 0 && join.Copies?.GetValueOrDefault(U)?.LastOrDefault() == "l:D" && joinBack == ("A", "A,B,C,D"), $"{Say(join, U)}; back {joinBack}");
        // Copies new at once join in the order ties go by: found at launch, then the older Workshop item, then by name.
        const string T = "SmmSameIdOrder";
        var order = new StoreData { Copies = new() { [T] = ["w:900"] } };
        Reconciled(order, Made(T, "W900", item: 900), Made(T, "W500", item: 500), Made(T, "W100", item: 100), Made(T, "Zed"), Made(T, "Abe"));
        Check("... copies new at once join the record in the order ties go by (the older Workshop item first, then local ones by name), not the list's",
            order.Copies?.GetValueOrDefault(T)?.SequenceEqual(["w:900", "w:100", "w:500", "l:Abe", "l:Zed"]) == true, string.Join(",", order.Copies?.GetValueOrDefault(T) ?? []));

        // Profiles. One naming a copy that's missing (A) on, and B and C off, applied as a switch, Discard or an import
        // applies it: copies_off for the group is exactly the profile's (B and C read ticked meanwhile), and the profile is
        // Saved; A back, it alone loads (P9, P8).
        const string S = "SmmSameIdNamed";
        var named = new StoreData { CopiesOff = [], Copies = RecordOf(S, "l:A", "l:B", "l:C") };
        var namesA = new ProfileMod(S, ModSource.ModsDirectory, true, CopiesOff: ["l:B", "l:C"]);
        var (appliedOff, appliedTicked) = WithCopies(named, S, [Made(S, "B", ModLoadState.Loaded), Made(S, "C")], () =>
        {
            Profiles.TestApply([namesA]);
            return (string.Join(",", CopiesOff().Where(k => Copies.IdOf(k) == S).Select(k => Copies.Shown(k[(k.IndexOf('|') + 1)..]))),
                string.Join(",", ModManager.Mods.Where(m => m.manifest?.id == S && Copies.Ticked(m)).Select(m => Copies.Shown(Copies.CopyOf(m)))));
        });
        var appliedData = new StoreData { CopiesOff = [$"{S}|l:B", $"{S}|l:C"], Copies = named.Copies };
        var appliedUnsaved = Unsaved(appliedData, namesA, Made(S, "B", ModLoadState.Loaded), Made(S, "C"));
        var appliedBack = Page(appliedData, Made(S, "A"), Made(S, "B"), Made(S, "C", version: "1.1.0"));
        var appliedBackUnsaved = Unsaved(appliedData, namesA, Made(S, "A"), Made(S, "B", ModLoadState.Loaded), Made(S, "C", version: "1.1.0"));
        Check("... a profile naming a missing copy on and the others off, applied: copies_off for the group is exactly its (the others reading ticked "
            + "meanwhile), the profile Saved; the copy back, it alone loads, still Saved",
            appliedOff == "B,C" && appliedTicked == "B,C" && appliedUnsaved == null && appliedBack == ("A", "A") && appliedBackUnsaved == null,
            $"off {appliedOff}, ticked {appliedTicked}, unsaved {appliedUnsaved ?? "none"}; back {appliedBack}, unsaved {appliedBackUnsaved ?? "none"}");
        // Saving the active profile while a copy is missing keeps the other's off: Current names every recorded copy.
        const string Q = "SmmSameIdSave";
        var saving = new StoreData { CopiesOff = [$"{Q}|l:B"], Copies = RecordOf(Q, "l:A", "l:B") };
        var saved = WithCopies(saving, Q, [Made(Q, "B", ModLoadState.Loaded)], () => Profiles.TestCurrent().FirstOrDefault(m => m.Id == Q));
        Check("... saving while the ticked copy is missing (the other installed alone): the profile keeps the other off", saved is { Enabled: true, CopiesOff: ["l:B"] },
            $"{saved} [{string.Join(",", saved?.CopiesOff ?? [])}]");
        // A profile from before copies had a box each (0.1.4: Copy, no CopiesOff): every recorded copy but its copy off,
        // missing (C) or not (B), leaving out a copy new to the record this session (D: it reads ticked in our data).
        const string R = "SmmSameIdOld";
        var oldProfile = new Profile { Name = "Old", Mods = [new ProfileMod(R, ModSource.ModsDirectory, true, "l:A")] };
        Copies.TestJoined(R, "l:D", true);
        try
        {
            WithCopies(new StoreData { CopiesOff = [], Copies = RecordOf(R, "l:A", "l:B", "l:C", "l:D") }, R, [Made(R, "A"), Made(R, "B"), Made(R, "D")],
                () => Profiles.TestHold(oldProfile));
        }
        finally
        {
            Copies.TestJoined(R, "l:D", false);
        }
        Check("... a 0.1.4 profile (its copy, no copies off): every recorded copy but its own off, missing or not, but not one new to the record this session",
            oldProfile.Mods.Single() is { Copy: "l:A", CopiesOff: ["l:B", "l:C"] }, string.Join(",", oldProfile.Mods.Single().CopiesOff ?? ["null"]));
        // The same with its copy (A) missing at the update and a new lower copy (C) joining at that launch: Join took B,
        // which the read rule had ticked, out of our data's copies_off, so B and C are ticked and B loads. The profile's fill
        // leaves B on too, so the active profile is Saved, and Discard (or a switch back) keeps B loading, not C.
        const string J = "SmmSameIdOldJoin";
        var oldJoin = new StoreData { CopyPicks = [$"{J}|l:A"], Copies = RecordOf(J, "l:A", "l:B") };
        Mod[] OldJoinCopies() => [Made(J, "B", ModLoadState.Loaded, version: "1.2.0"), Made(J, "C")];
        Reconciled(oldJoin, OldJoinCopies());
        var oldJoinPage = Page(oldJoin, OldJoinCopies());
        var oldJoinProfile = new Profile { Name = "Old", Mods = [new ProfileMod(J, ModSource.ModsDirectory, true, "l:A")] };
        Copies.TestJoined(J, "l:C", true);
        try
        {
            WithCopies(oldJoin, J, OldJoinCopies(), () => Profiles.TestHold(oldJoinProfile));
        }
        finally
        {
            Copies.TestJoined(J, "l:C", false);
        }
        var oldJoinEntry = oldJoinProfile.Mods.Single();
        var oldJoinUnsaved = Unsaved(oldJoin, oldJoinEntry, OldJoinCopies());
        var oldJoinDiscard = WithCopies(oldJoin, J, OldJoinCopies(), () =>
        {
            Profiles.TestApply([oldJoinEntry]);
            return LoadingCopy(J);
        });
        Check("... a 0.1.4 profile whose copy is missing at the update, a new lower copy joining then: the other recorded copy stays on in the profile, as in "
            + "our data (both ticked, the higher loading), so the active profile is Saved, and Discard keeps it loading",
            OffOf(oldJoin, J).Count == 0 && oldJoinPage == ("B", "B,C") && oldJoinEntry.CopiesOff is [] && oldJoinUnsaved == null && oldJoinDiscard == "l:B",
            $"{Say(oldJoin, J)}; page {oldJoinPage}; profile off [{string.Join(",", oldJoinEntry.CopiesOff ?? ["null"])}], unsaved {oldJoinUnsaved ?? "none"}, "
            + $"Discard loads {oldJoinDiscard}");
        // The same with our data's pick another copy than the profile's (B: picked after the profile was saved with A), so
        // Join takes nothing out of our data (B is on there). The profile's fill still leaves B on: Join goes by each
        // profile entry's own copies off, not our data's, and an entry filled in before the join (B off) loses B the same
        // way. So switched to, B keeps loading, not the lower C, and as the active profile it's Saved (it ticks what our
        // data ticks while A is missing).
        const string H = "SmmSameIdOldOther";
        var oldOther = new StoreData
        {
            CopyPicks = [$"{H}|l:B"], Copies = RecordOf(H, "l:A", "l:B"),
            Profiles = [new Profile { Name = "Filled", Mods = [new ProfileMod(H, ModSource.ModsDirectory, true, CopiesOff: ["l:B"])] }],
        };
        Mod[] OldOtherCopies() => [Made(H, "B", ModLoadState.Loaded, version: "1.2.0"), Made(H, "C")];
        Reconciled(oldOther, OldOtherCopies());
        var oldOtherPage = Page(oldOther, OldOtherCopies());
        var oldOtherFilled = oldOther.Profiles.Single().Mods.Single().CopiesOff;
        var oldOtherProfile = new Profile { Name = "Old", Mods = [new ProfileMod(H, ModSource.ModsDirectory, true, "l:A")] };
        Copies.TestJoined(H, "l:C", true);
        try
        {
            WithCopies(oldOther, H, OldOtherCopies(), () => Profiles.TestHold(oldOtherProfile));
        }
        finally
        {
            Copies.TestJoined(H, "l:C", false);
        }
        var oldOtherEntry = oldOtherProfile.Mods.Single();
        var oldOtherUnsaved = Unsaved(oldOther, oldOtherEntry, OldOtherCopies());
        var oldOtherApplied = WithCopies(oldOther, H, OldOtherCopies(), () =>
        {
            Profiles.TestApply([oldOtherEntry]);
            return LoadingCopy(H);
        });
        Check("... the same with our data's pick another copy, so Join takes nothing out of our data: the profile's fill still leaves the other recorded "
            + "copy on, as Join leaves a profile entry that had it off, so switched to, the higher keeps loading, and as the active profile it's Saved",
            OffOf(oldOther, H) is ["A"] && oldOtherPage == ("B", "B,C") && oldOtherFilled is [] && oldOtherEntry.CopiesOff is [] && oldOtherUnsaved == null
            && oldOtherApplied == "l:B",
            $"{Say(oldOther, H)}; page {oldOtherPage}; the entry filled before [{string.Join(",", oldOtherFilled ?? ["null"])}]; profile off "
            + $"[{string.Join(",", oldOtherEntry.CopiesOff ?? ["null"])}], unsaved {oldOtherUnsaved ?? "none"}, switched to loads {oldOtherApplied}");
        // An inactive profile saved before a higher copy arrived has never seen it: it reads it ticked, and switched to, the
        // higher copy loads (P18, pinned).
        const string P = "SmmSameIdLater";
        var later = new StoreData { CopiesOff = [$"{P}|l:A"], Copies = RecordOf(P, "l:A", "l:B") };
        Reconciled(later, Made(P, "A"), Made(P, "B", version: "1.1.0"), Made(P, "C", version: "1.2.0"));
        var laterLoads = WithCopies(later, P, [Made(P, "A"), Made(P, "B", ModLoadState.Loaded, version: "1.1.0"), Made(P, "C", version: "1.2.0")], () =>
        {
            Profiles.TestApply([new ProfileMod(P, ModSource.ModsDirectory, true, CopiesOff: ["l:A"])]);
            return LoadingCopy(P);
        });
        Check("... an inactive profile saved before a higher copy came, switched to: it has never seen that copy, so it's ticked, and it loads",
            laterLoads == "l:C", $"loads {laterLoads}");

        // N3: a second copy subscribed with the game closed, the same version: neither is in our record yet, so the copy
        // the last launch had (the launch record names every entry's copy) ranks first, at Init and in the first split, and
        // keeps loading; with no record of it, the older item would.
        const string N = "SmmSameIdFirst";
        var beforeWas = LaunchGuard.Before;
        string firstAtInit, firstOrder, firstPage, olderAtInit;
        var firstSplit = new StoreData();
        try
        {
            var before = new Dictionary<string, HashSet<string>> { [N] = ["w:900"] };
            LaunchGuard.TestBefore(before);
            var plan = SameIdLoad.Run(new List<Mod> { us, Made(N, "W100", item: 100), Made(N, "W900", item: 900) }, () => new Ticks(null, null, before),
                m => m == us, log: false).Plan.GetValueOrDefault(N);
            firstAtInit = plan?.Loads is { } loads ? Copies.CopyOf(loads) : "none";
            Reconciled(firstSplit, Made(N, "W100", item: 100), Made(N, "W900", item: 900));
            firstOrder = string.Join(",", firstSplit.Copies?.GetValueOrDefault(N) ?? []);
            firstPage = Page(firstSplit, Made(N, "W100", item: 100), Made(N, "W900", item: 900)).Loads;
            var older = SameIdLoad.Run(new List<Mod> { us, Made(N, "W100", item: 100), Made(N, "W900", item: 900) }, () => new Ticks(null, null),
                m => m == us, log: false).Plan.GetValueOrDefault(N);
            olderAtInit = older?.Loads is { } olderLoads ? Copies.CopyOf(olderLoads) : "none";
        }
        finally
        {
            LaunchGuard.TestBefore(beforeWas);
        }
        Check("... a second copy at the same version subscribed with the game closed: the copy the last launch had loads at Init and is first in the "
            + "first split, so it keeps loading (with no launch record, the older item would)",
            firstAtInit == "w:900" && firstOrder == "w:900,w:100" && firstPage == "900" && olderAtInit == "w:100",
            $"Init {firstAtInit}, record {firstOrder}, page {firstPage}; with no record {olderAtInit}");
        // N10: a session that can't save our data goes by our file's record for ties (a holder moved here isn't saved).
        string? tieSaving, tieReadOnly;
        var twinRecord = Store.Data.Copies?.GetValueOrDefault(TwinId);
        if (twinRecord is { Count: > 1 } && SameIdLoad.InitTicks?.Record?.GetValueOrDefault(TwinId) is { } fileRecord)
        {
            var swapped = twinRecord.AsEnumerable().Reverse().ToList();
            if (Store.Data.Copies is { } dataCopies)
                dataCopies[TwinId] = swapped;
            try
            {
                tieSaving = string.Join(",", Ticks.Now.TieList(TwinId) ?? []);
                Store.TestReadOnly(true);
                tieReadOnly = string.Join(",", Ticks.Now.TieList(TwinId) ?? []);
            }
            finally
            {
                Store.TestReadOnly(false);
                if (Store.Data.Copies is { } copies)
                    copies[TwinId] = twinRecord;
            }
            Check("... a session that can't save our data: ties go by our file's record, which the next launch reads; one that can, by our data's",
                tieSaving == string.Join(",", swapped) && tieReadOnly == string.Join(",", fileRecord), $"saving {tieSaving}, read-only {tieReadOnly}, file {string.Join(",", fileRecord)}");
        }
        else
            TwinsMissing("copies, ties in a read-only session");
        var settings = SaveManager.Instance.SettingsSave.ModSettings;
        // P11: the tip on a Workshop copy whose switch is off names what a click on it loads: the click turns it on alone
        // (Write), so the other Workshop copy, ticked by its key, doesn't count.
        const string K = "SmmSameIdTip";
        var tip = new StoreData { CopiesOff = [$"{K}|w:301"], Copies = RecordOf(K, "l:L", "w:301", "w:302") };
        var (tipLoads, clickLoads) = WithCopies(tip, K, [Made(K, "L", version: "1.0.0"), Made(K, "W301", item: 301, version: "2.0.0"), Made(K, "W302", item: 302, version: "3.0.0")], () =>
        {
            settings?.ModList.Add(new SettingsSaveMod { Id = K, Source = ModSource.SteamWorkshop, IsEnabled = false });
            try
            {
                var w301 = ModManager.Mods.FirstOrDefault(m => m.manifest?.id == K && Copies.CopyOf(m) == "w:301");
                var ifTicked = w301 == null ? null : Copies.LoadingIf(w301);
                if (w301 != null)
                    Dependencies.Write([(w301, true)]);
                return (ifTicked == null ? "none" : Copies.Shown(Copies.CopyOf(ifTicked)), LoadingCopy(K) is { } now ? Copies.Shown(now) : "none");
            }
            finally
            {
                settings?.ModList.RemoveAll(e => e.Id == K);
                SaveManager.Instance.SaveSettings();
            }
        });
        Check("... the tip on a Workshop copy with its switch off names what a click on it loads (it comes on alone, the other Workshop copy off)",
            tipLoads == clickLoads && tipLoads == (Compat.WorkshopBeatsOlderLocal ? "301" : "L"), $"tip {tipLoads}, after the click {clickLoads}");

        // N6: the switch off, its ticked copy (A) missing, the other ticked on: A goes off too, so back it doesn't load
        // beside it.
        const string O = "SmmSameIdSwitch";
        var switchOff = new StoreData { CopiesOff = [$"{O}|l:B"], Copies = RecordOf(O, "l:A", "l:B") };
        var (offAfter, backLoads) = WithCopies(switchOff, O, [Made(O, "B")], () =>
        {
            settings?.ModList.Add(new SettingsSaveMod { Id = O, Source = ModSource.ModsDirectory, IsEnabled = false });
            try
            {
                if (ModManager.Mods.FirstOrDefault(m => m.manifest?.id == O) is { } onlyB)
                    Dependencies.Write([(onlyB, true)]);
                var off = string.Join(",", CopiesOff().Where(k => Copies.IdOf(k) == O).Select(k => Copies.Shown(k[(k.IndexOf('|') + 1)..])));
                return (off, Page(new StoreData { CopiesOff = [.. CopiesOff().Where(k => Copies.IdOf(k) == O)], Copies = switchOff.Copies },
                    Made(O, "A", version: "1.2.0"), Made(O, "B")));
            }
            finally
            {
                settings?.ModList.RemoveAll(e => e.Id == O);
                SaveManager.Instance.SaveSettings();
            }
        });
        Check("... the switch off, its ticked copy missing, the other ticked on: the missing one goes off too, so back (higher) it doesn't load beside it",
            offAfter == "A" && backLoads == ("B", "B"), $"off {offAfter}; back {backLoads}");
    }

    // The active profile's unsaved changes with these copies of one id installed, our data for it as data has it and the
    // profile's entry for it as given (WithCopies): null for none, else what they are.
    static string? Unsaved(StoreData data, ProfileMod entry, params Mod[] copies) => Profiles.Edited ? "the profile was Unsaved before"
        : WithCopies(data, entry.Id, copies, () =>
        {
            History.Quietly(now => now.Profiles.FirstOrDefault(p => p.Name == now.ActiveProfile)?.Mods.Add(entry));
            Profiles.Track();
            var (on, off) = Profiles.UnsavedChanges();
            return !Profiles.Edited ? null : $"on {string.Join(",", on.Select(m => m.Name))}, off {string.Join(",", off.Select(m => m.Name))}";
        });

    // What read gives with these copies of one id installed and our data's record and copies off for it as data has them
    // (in our data, every undo snapshot and the game's list for a moment, quietly, as a launch would find them; then taken
    // out again, with the id's profile entries).
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
                now.CopiesOff = Store.TidyOff([.. now.CopiesOff?.Where(k => !Ours(k)) ?? [], .. data.CopiesOff?.Where(Ours) ?? []]);
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
    // another copy is one Switched change and offers Restore (the game's list alone would be the same), and one copy
    // swapped for another of a mod installed once isn't; Restore before our data loads makes the working copy the only one
    // of its group ticked in the file (copies installed since, and our record's missing then and now, off too), copy_picks
    // gone, the rest of it kept, a backup first; on a file from before copies_off, the other groups keep their legacy
    // reading; the list it writes has ours first; the notice says "switched copy: 1 → 2".
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
        // A mod installed once, its one copy swapped for another (a re-upload): nothing Restore could undo.
        var swapped = LaunchGuard.Changes([ours, new("SmmSameIdOnce", ModSource.SteamWorkshop, true, "1.0", "w:8")],
            [ours, new("SmmSameIdOnce", ModSource.SteamWorkshop, true, "1.0", "w:7")]);
        Check("... a switch to another copy: one Switched change (from, to), and Restore offered though the game's list would be the same; a mod "
            + "installed once, its copy swapped for another: no change",
            changes is [{ Kind: LaunchGuard.Kind.Switched, From: "w:1", To: "w:2" } switched] && switched.Id == X && offer != null && swapped.Count == 0,
            string.Join(", ", changes.Select(c => $"{c.Kind} {c.Id} {c.From} {c.To}")) + $"; offer {offer != null}; swapped once {string.Join(", ", swapped.Select(c => c.Kind))}");
        // The first launch after the update, against a working record from before copies were told apart (each copy its
        // id's switch, no Copy): no change at all.
        List<LaunchGuard.Entry> oldWorking = [ours, new(X, ModSource.SteamWorkshop, true, "1.0"), new(X, ModSource.SteamWorkshop, true, "1.0")];
        var sinceOld = LaunchGuard.Changes([ours, Copy("w:1", false), Copy("w:2", true)], oldWorking);
        Check("... against a record from before copies were told apart: no change", sinceOld.Count == 0,
            string.Join(", ", sinceOld.Select(c => $"{c.Kind} {c.Id} {c.From} {c.To}")));
        var written = LaunchGuard.Written([Copy("w:1", true), Copy("w:2", false), ours]);
        Check("... the list a Restore writes: ours first, the copies' one switch on", written.FirstOrDefault()?.Id == ModEntry.ModId
            && written.Where(e => e.Id == X).All(e => e.IsEnabled), string.Join(", ", written.Select(e => $"{e.Id}={e.IsEnabled}")));
        // In the file: our record has a third copy missing then and now (w:3), and one installed since (w:4) is in the list.
        var since = Made(X, "W4", item: 4);
        GameMods().Add(since);
        var data = Path.Combine(dir, "data");
        bool patched, legacyPatched;
        JsonObject? json, legacyJson;
        int backups;
        try
        {
            Directory.CreateDirectory(data);
            File.WriteAllText(Path.Combine(data, "manager.json"), "{\n  \"version\": 1,\n  \"mods\": { \"SmmTestUi\": { \"notes\": \"kept\" } },\n"
                + "  \"copies\": { \"SmmSameIdX\": [\"w:1\", \"w:2\", \"w:3\"] },\n  \"copy_picks\": [\"SmmSameIdY|w:5\", \"SmmSameIdX|w:2\"],\n"
                + "  \"copies_off\": [\"SmmSameIdX|w:1\", \"SmmSameIdY|w:6\"]\n}");
            patched = LaunchGuard.PatchOff(data, working);
            json = JsonNode.Parse(File.ReadAllText(Path.Combine(data, "manager.json"))) as JsonObject;
            backups = Directory.Exists(Path.Combine(data, "backups")) ? Directory.GetFiles(Path.Combine(data, "backups"), "manager.*.json").Length : 0;
            // A file from before copies_off: the other group's legacy reading (Y: its pick w:5, w:6 off) kept.
            var before = Path.Combine(dir, "data-before");
            Directory.CreateDirectory(before);
            File.WriteAllText(Path.Combine(before, "manager.json"), "{\n  \"version\": 1,\n  \"copies\": { \"SmmSameIdX\": [\"w:1\", \"w:2\"], "
                + "\"SmmSameIdY\": [\"w:5\", \"w:6\"] },\n  \"copy_picks\": [\"SmmSameIdX|w:2\", \"SmmSameIdY|w:5\"]\n}");
            legacyPatched = LaunchGuard.PatchOff(before, working);
            legacyJson = JsonNode.Parse(File.ReadAllText(Path.Combine(before, "manager.json"))) as JsonObject;
        }
        finally
        {
            GameMods().Remove(since);
            Copies.TestRecount();
        }
        static List<string?> Strings(JsonNode? node) => (node as JsonArray)?.Select(p => (string?)p).ToList() ?? [];
        Check("... Restore before our data loads (a failed start): in the file, copies_off makes the working copy the only one ticked (the copies the "
            + "setup had, our record's missing then and now, and one installed since all off), copy_picks gone, the rest kept, a backup first; on a file "
            + "from before copies_off, the other groups keep their legacy reading",
            patched && Strings(json?["copies_off"]).SequenceEqual(["SmmSameIdX|w:2", "SmmSameIdX|w:3", "SmmSameIdX|w:4", "SmmSameIdY|w:6"])
            && json?.ContainsKey("copy_picks") == false && (string?)json?["mods"]?["SmmTestUi"]?["notes"] == "kept" && backups == 1
            && legacyPatched && Strings(legacyJson?["copies_off"]).SequenceEqual(["SmmSameIdX|w:2", "SmmSameIdX|w:4", "SmmSameIdY|w:6"])
            && legacyJson?.ContainsKey("copy_picks") == false,
            $"patched {patched}: {json?.ToJsonString()}, {backups} backups; before copies_off: {legacyJson?.ToJsonString()}");
        LaunchGuard.Notice = (new LaunchGuard.Setup(DateTime.Now.AddHours(-1), working), changes);
        LaunchGuard.ShowNotice(menu);
        await Wait(menu, 1);
        var texts = string.Join(" | ", PopupTexts());
        NModalContainer.Instance?.Clear();
        await Wait(menu, 0.3);
        Check("... the notice names the switch, by Workshop item: \"(switched copy: 1 → 2)\"", texts.Contains("(switched copy: 1 → 2)"), texts);
    }

    // A message the game puts on a copy we set aside (made up here: a Steam branch error on the fixture's copy set
    // aside) goes from its errors, so the MODDED label stays clear, to its info panel, marked as the game's; the late
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
                && ModDetails.Card(aside).Contains(GameText.Caption("Error while loading - Can be ignored if disabled")),
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
            && Hover.TipOf(handle) == "Spire Mod Manager always loads first, so it can configure the rest of the list properly"
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

    // The fixture twins on our main list, a box per copy. At launch (the update's reading of our file, from before
    // copies_off: only its legacy pick ticked): B on, Enabled, loading; A off (copies_off), Disabled, no duplicate problem,
    // its box's tip saying the other is on too and which would load (B: the higher version), its panel naming the enabled
    // copy; Enable all over it says it enables a second copy; the view switch's tip says how the game's page treats them.
    // Ticking A: both ticked, the game's list still on, one undo step, B still loading (the higher version), A Won't load
    // saying so, nothing pending; A's title marked as changed, B's white. Undo, redo, Revert all and its undo; in Load
    // Order, Enable says what the box says, and an order undo leaves the ticks. Vanilla View: one box per switch (both
    // ticked), its tick switching every copy and keeping copies_off; back on our page, B's box alone. A profile holds each
    // copy's box: saved with both ticked, switched to one from before copies had a box each (only its copy on) and back;
    // one naming no copy gets the copies off now, and holds the boxes from then on; Discard puts the profile's back. A
    // shared list naming the id once (no link) sets both copies; one with Workshop links sets the copies it links; the
    // not-installed copies it names are listed by item; the order of its rows changes nothing. Restore makes the copy that
    // loaded then the only one ticked. The relaunch check sees the loading copy's problem and the other copy losing to it.
    // A tick on one of both selected copies turns both on, the higher loading; Enable selected over both, the same. A
    // session that can't save our data: the tick is the game's. The self-check finding the choice not taken: every copy
    // says it, and the game's own choice reads as what loads. The loading copy failing: how to load the other one. New
    // copies arriving ticked as the game has them: a lower one leaves B loading, a higher one loads after a relaunch (the
    // popup names it); with the switch off one arrives off, and a tick on another copy leaves it off. An update of a copy
    // isn't a new copy. A copy new to our record mid-session ties with the one there: the one there loads (installed
    // first), the active profile has the new one off, so its arriving on is the profile's edit; two copies of a mod new to
    // the game are new mods, the first to come loading. A lone copy in copies_off (its ticked twin gone) meets a lower new
    // copy: it stays ticked and loads, in every undo step and profile (Revert all keeps it). A copy uninstalled and back
    // keeps its tick: ticked, or not (P1), the profile Saved either way.
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
        string? Loads() => LoadingCopy(TwinId);
        // Read again each time: an undo puts new entries in the game's list.
        List<SettingsSaveMod> Entries() => SaveManager.Instance.SettingsSave.ModSettings?.ModList.Where(e => e.Id == TwinId).ToList() ?? [];
        bool SwitchOn() => Entries() is { Count: > 0 } entries && entries.All(e => e.IsEnabled);
        string Say() => $"loads {Loads()}, boxes A {Ticked(rowA)} B {Ticked(rowB)}, on A {Copies.Ticked(a)} B {Copies.Ticked(b)}, switch {SwitchOn()}, "
            + $"off {string.Join(",", CopiesOff())}; status A {ModDetails.Status(a)}, B {ModDetails.Status(b)}; "
            + $"lines A {ModDetails.CopyLine(a, false)?.Text}, B {ModDetails.CopyLine(b, false)?.Text}; {CopiesBroken() ?? "kept"}";
        // Our file as our Init reads it at the next launch (not just in memory): no copy_picks in it.
        bool NoPicksOnDisk() => JsonNode.Parse(File.ReadAllText(Store.FilePath)) is JsonObject file && !file.ContainsKey("copy_picks");
        var (keyA, keyB) = ($"{TwinId}|l:SmmTestTwinA", $"{TwinId}|l:SmmTestTwinB");
        var offAtStart = CopiesOff();
        bool OffAsAtStart() => CopiesOff().SequenceEqual(offAtStart) && FileCopiesOff().SequenceEqual(offAtStart);

        var retired = Rows().FirstOrDefault(r => r.Mod?.manifest?.id == "SmmTestRetired");
        var enableAll = CategoryList.OnOffItems(content, [rowA, .. retired == null ? Array.Empty<NModMenuRow>() : [retired]])[0];
        // Both copies in an Enable all: one is off, so there's something to do.
        var bothDone = CategoryList.OnOffItems(content, [rowA, rowB])[0].Disabled;
        var toggleTip = Hover.TipOf(screen.GetNode<Control>("SpireModManagerViewToggle"));
        var boxTip = Hover.TipOf(rowA.GetNode<Control>("Tickbox"));
        Check("same ID on our page: B on (Enabled, loading), A off (Disabled, no duplicate problem, in copies_off, in our file too), its box's tip saying "
            + "the other is also enabled and which would load with both on (B, the higher version), its panel naming the enabled copy; Enable all over it "
            + "says it enables a second copy, over both it isn't greyed; the view switch's tip says how the game's page treats them; no copy_picks in our file",
            Loads() == "l:SmmTestTwinB" && Ticked(rowB) && !Ticked(rowA) && ModDetails.Status(b).Contains("Enabled") && ModDetails.Status(a).Contains("Disabled")
            && ModDetails.Problems(a).Count == 0 && boxTip == Draft(CopyOnTip, ("other", Named(b)), ("loads", Named(b)))
            && Hover.TipOf(rowB.GetNode<Control>("Tickbox")) == "" && ModDetails.CopyLine(a, links: false)?.Text == Draft(OtherEnabled, ("other", Named(b)))
            && enableAll.Tip == Draft(BulkEnablesTip, ("mods", a.manifest?.name ?? ""))
            && toggleTip.Contains("\nIn vanilla, mods ") && toggleTip.Contains("These will use the Spire Mod Manager configuration as a fallback.")
            && !bothDone && NoPicksOnDisk() && offAtStart.Contains(keyA) && FileCopiesOff().Contains(keyA) && CopiesBroken() == null,
            $"{Say()}; box tip {boxTip}; Enable all tip {enableAll.Tip}; both done {bothDone}; off on disk {string.Join(", ", FileCopiesOff())}; "
            + $"toggle tip {toggleTip}");

        var steps = History.Steps(History.Track.List);
        await Tick(rowA, true);
        var (unsavedOn, unsavedOff) = Profiles.UnsavedChanges();
        Check("ticking A: both ticked (A out of copies_off, in our file too), the game's list still on, one undo step; B still loads (the higher version), "
            + "A reads Won't load, as the game prioritizes a higher version, B that it's the one that loads; nothing pending; only A's title marked as "
            + "changed, B's white; the profile's unsaved lines name A",
            Loads() == "l:SmmTestTwinB" && !CopiesOff().Contains(keyA) && !FileCopiesOff().Contains(keyA) && NoPicksOnDisk() && Ticked(rowA) && Ticked(rowB)
            && SwitchOn() && History.Steps(History.Track.List) == steps + 1 && ModDetails.Status(a).Contains(StatusWontLoad) && ModDetails.Status(b).Contains("Enabled")
            && ModDetails.CopyLine(a, links: false)?.Text == Draft(CopyLoses, ("other", Named(b)), ("why", WhyHigher))
            && ModDetails.CopyLine(b, links: false)?.Text == Draft(LoadsAlso, ("other", Named(a)))
            && rowA.GetNode<Control>("Title").Modulate == RowTitles.ChangedTitle && rowB.GetNode<Control>("Title").Modulate == Colors.White
            && unsavedOn.Any(m => m.Name == Named(a)) && !unsavedOff.Any(m => m.Name == Named(b)) && ModEntry.HasPendingChanges() == LoadOrder.OrderChanged
            && screen.GetNode<Control>("%PendingChangesLabel").Visible == ModEntry.HasPendingChanges() && CopiesBroken() == null,
            $"{Say()}; unsaved +{string.Join(",", unsavedOn.Select(m => m.Name))} -{string.Join(",", unsavedOff.Select(m => m.Name))}; pending "
            + $"{ModEntry.HasPendingChanges()} (order moved {LoadOrder.OrderChanged})");
        await Capture(screen, outDir, "19-same-id-switched");
        History.Undo();
        await Wait(menu, 0.3);
        var undone = Loads() == "l:SmmTestTwinB" && Ticked(rowB) && !Ticked(rowA) && SwitchOn() && !Profiles.Edited && OffAsAtStart();
        History.Redo();
        await Wait(menu, 0.3);
        var redone = Loads() == "l:SmmTestTwinB" && Ticked(rowA) && Ticked(rowB) && !CopiesOff().Contains(keyA);
        History.RevertMods();
        await Wait(menu, 0.3);
        var reverted = Ticked(rowB) && !Ticked(rowA) && History.AtEntered && OffAsAtStart();
        History.Undo(); // the revert
        await Wait(menu, 0.3);
        var revertUndone = Ticked(rowA) && Ticked(rowB);
        History.Undo(); // the tick
        await Wait(menu, 0.3);
        Check("... undo puts it back (copies_off and the game's list, in our file too), redo again; Revert all goes back to the ticks the page opened with, "
            + "and undoes", undone && redone && reverted && revertUndone && Loads() == "l:SmmTestTwinB" && OffAsAtStart(),
            $"undo {undone}, redo {redone}, revert {reverted}, its undo {revertUndone}; {Say()}");

        // Load Order: A through its Enable item, then a move; the order's undo leaves the ticks.
        Click(screen.GetNode<NButton>("SpireModManagerLoadOrderToggle"));
        await Wait(menu, 0.5);
        if (RowOf("l:SmmTestTwinA") is { } orderRow)
        {
            var enable = LoadOrder.Actions(content, orderRow, () => CategoryList.Redraw(content))[0];
            var enableTip = enable.Tip;
            enable.OnSelect?.Invoke();
            await Wait(menu, 0.3);
            var tickedHere = Copies.Ticked(a) && Copies.Ticked(b) && Loads() == "l:SmmTestTwinB";
            var offHere = CopiesOff();
            if (Rows().Skip(1).FirstOrDefault(r => r.Mod?.manifest?.id is { } id && id != TwinId && id != ModEntry.ModId)?.Mod is { } mover)
                LoadOrder.Move(mover, Rows().Count - 1, Rows());
            CategoryList.Redraw(content);
            await Wait(menu, 0.3);
            History.Undo(); // the move: this view's undo steps through the order only
            await Wait(menu, 0.3);
            Check("... in Load Order: Enable on A says the other is also enabled and which loads (B), and ticks it beside B; an order undo leaves copies_off",
                enableTip == Draft(CopyOnTip, ("other", Named(b)), ("loads", Named(b))) && tickedHere && CopiesOff().SequenceEqual(offHere)
                && !LoadOrder.OrderChanged, $"tip {enableTip}; ticked {tickedHere}; {Say()}");
        }
        Click(screen.GetNode<NButton>("SpireModManagerLoadOrderToggle"));
        await Wait(menu, 0.5);
        History.Undo(); // the tick made in Load Order
        await Wait(menu, 0.3);

        // Vanilla View: the game's own page, one switch for both copies (with Better Mod Menu, its page: not ours to tick).
        // Its tick leaves copies_off as it is, so back on our page the same copies are ticked.
        if (!Bmm.Present)
        {
            var vanilla = await WithVanillaView(screen, menu, () => Ticked(rowA) && Ticked(rowB));
            Click(screen.GetNode<BaseButton>("SpireModManagerViewToggle"));
            await Wait(menu, 0.5);
            await Tick(rowA, false); // the game's own tickbox: every entry of the id and source
            var allOff = Entries().All(e => !e.IsEnabled) && !Ticked(rowB) && OffAsAtStart();
            await Tick(rowA, true);
            var allOn = SwitchOn() && Ticked(rowB) && Loads() == "l:SmmTestTwinB" && OffAsAtStart();
            Click(screen.GetNode<BaseButton>("SpireModManagerViewToggle"));
            await Wait(menu, 0.5);
            Check("... Vanilla View: both boxes ticked (the game's one switch); its tick switches every copy and keeps copies_off; back on our page, B's box alone",
                vanilla && allOff && allOn && Ticked(rowB) && !Ticked(rowA) && Loads() == "l:SmmTestTwinB" && CopiesBroken() == null,
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
            var saved = savedEntry is { Copy: null, CopiesOff: [] };
            // Its saved state, not an unsaved edit an earlier test left (a switch writes the edit).
            var (otherWas, otherEdit) = (other.Mods.ToList(), other.Edited);
            other.Edited = null;
            other.Mods.RemoveAll(m => m.Id == TwinId);
            other.Mods.Add(new ProfileMod(TwinId, ModSource.ModsDirectory, true, "l:SmmTestTwinB")); // from before copies had a box each: its copy alone
            Profiles.SwitchTo(other, screen);
            await Wait(menu, 0.3);
            var there = Loads() == "l:SmmTestTwinB" && Ticked(rowB) && !Ticked(rowA) && CopiesOff().Contains(keyA)
                && other.Mods.FirstOrDefault(m => m.Id == TwinId) is { CopiesOff: ["l:SmmTestTwinA"] };
            Profiles.SwitchTo(home, screen);
            await Wait(menu, 0.3);
            var back = Ticked(rowA) && Ticked(rowB) && !CopiesOff().Contains(keyA) && Loads() == "l:SmmTestTwinB";
            other.Mods.RemoveAll(m => m.Id == TwinId);
            other.Mods.Add(new ProfileMod(TwinId, ModSource.ModsDirectory, true)); // no copy: as Better Mod Menu's
            Profiles.SwitchTo(other, screen);
            await Wait(menu, 0.3);
            // The copies off now (none: both ticked), and from then on the profile holds the boxes: a tick off on one copy
            // is its unsaved edit; ticked again, none.
            var kept = Ticked(rowA) && Ticked(rowB);
            var held = other.Mods.FirstOrDefault(m => m.Id == TwinId) is { Copy: null, CopiesOff: [] };
            await Tick(rowB, false);
            var heldEdit = Profiles.Edited;
            await Tick(rowB, true);
            held &= heldEdit && !Profiles.Edited;
            Profiles.SwitchTo(home, screen);
            await Wait(menu, 0.3);
            await Tick(rowA, false);
            var (editOn, editOff) = Profiles.UnsavedChanges();
            var edited = Profiles.Edited && editOn.Count == 0 && editOff.Select(m => m.Name).SequenceEqual([Named(a)]);
            Profiles.Discard(screen);
            await Wait(menu, 0.3);
            Check("... a profile holds each copy's box: saved with both ticked (one mod in the count, no copy named), switched to one from before copies "
                + "had a box each (its copy alone: the other off) and back; one naming no copy gets the copies off now, and holds the boxes from then on (an "
                + "untick on a copy is its edit); an untick is the one unsaved line; Discard puts the profile's back",
                countedOnce && saved && there && back && kept && held && edited && Ticked(rowA) && Ticked(rowB) && !Profiles.Edited,
                $"saved {savedEntry} ({string.Join(",", savedEntry?.CopiesOff ?? [])}); counted once {countedOnce}, there {there}, back {back}, kept {kept}, "
                + $"held {held}, edited {edited} (+{string.Join(",", editOn.Select(m => m.Name))} -{string.Join(",", editOff.Select(m => m.Name))}); {Say()}");
            other.Mods.Clear();
            other.Mods.AddRange(otherWas);
            other.Edited = otherEdit;
            await Tick(rowA, false); // A off again, as the launch had it
            Profiles.Save(); // the profile back on it
        }
        else
            Results.Add("SKIP same ID, profiles: one profile only");

        // Shared lists: a row for the id with no link names both copies (two copies installed by hand look alike in a list),
        // so it sets their switch alone: off, the entry keeping its copies off; on, each copy as its box is. Rows for copies
        // not installed are listed by item when two share an id.
        var once = Profiles.PlanImport([new(TwinId, false, "Test Twin", "")]);
        var onceOn = Profiles.PlanImport([new(TwinId, true, "Test Twin", "")]);
        var twoGone = Profiles.PlanImport([new("SmmSameIdGone", true, "Gone", ProfileCsv.WorkshopLink(41)), new("SmmSameIdGone", false, "Gone", ProfileCsv.WorkshopLink(42))]);
        var preview = ProfileShare.PreviewFor([new("SmmSameIdGone", true, "Gone", ProfileCsv.WorkshopLink(41)), new("SmmSameIdGone", false, "Gone", ProfileCsv.WorkshopLink(42))], "Two gone");
        static string Planned(Profiles.ImportPlan plan, string id) => string.Join(", ", plan.Mods.Where(m => m.Id == id)
            .Select(m => $"{m.Source} {m.Enabled} {m.Copy ?? "no copy"} off [{string.Join(",", m.CopiesOff ?? [])}]"))
            + $" (on {plan.On}, off {plan.Off}, same {plan.Same}, missing {plan.Missing.Count})";
        Check("... a shared list naming the id once (no link) sets both copies' switch: off keeps copies off; on, each copy as its box is; two copies "
            + "not installed are both listed, by item",
            once.Mods.SingleOrDefault(m => m.Id == TwinId) is { Enabled: false, Copy: null, CopiesOff: ["l:SmmTestTwinA"] } && once is { Off: 1, Same: 1 }
            && onceOn.Mods.SingleOrDefault(m => m.Id == TwinId) is { Enabled: true, Copy: null, CopiesOff: ["l:SmmTestTwinA"] } && onceOn is { On: 0, Off: 0, Same: 2 }
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
            + "copies off kept; the preview says None)",
            shared.Count == 2 && shared.Count(r => r.Enabled) == 1
            && new[] { readAll, readOn }.All(p => p.Mods.SingleOrDefault(m => m.Id == TwinId) is { Enabled: true, Copy: null, CopiesOff: ["l:SmmTestTwinA"] }
                && p is { On: 0, Off: 0, Same: 2 })
            && sharePreview.Contains("Changes compared to the current profile:\n  •  None"),
            $"rows {string.Join(" | ", shared.Select(r => $"{r.Id} {r.Version} {r.Enabled}"))}; all {Planned(readAll, TwinId)}; on {Planned(readOn, TwinId)}; {sharePreview}");

        // Two Workshop items of one mod (made up, in the game's list for a moment, not announced): each says its item; a
        // shared list naming both sets each copy by its link, the one off in copies off; a row on naming an item not
        // installed sets nothing installed; both rows on, both on. The fixture's two local copies say their folders. Both
        // on with the other one first in the list: no box changes, nothing changes (the rows' order says nothing). And a
        // Workshop item of another mod installed once (111), listed by another item of it (222, a re-upload): the row sets
        // the one installed, nothing missing.
        var (w124, w125) = (Made("SmmSameIdW", "SmmSameIdW124", ModLoadState.None, 124), Made("SmmSameIdW", "SmmSameIdW125", ModLoadState.None, 125));
        var w111 = Made("SmmSameIdOnce", "SmmSameIdOnce111", ModLoadState.None, 111);
        GameMods().AddRange([w124, w125, w111]);
        try
        {
            var byLink = Profiles.PlanImport([new("SmmSameIdW", false, "W", ProfileCsv.WorkshopLink(124)), new("SmmSameIdW", true, "W", ProfileCsv.WorkshopLink(125))]);
            var notYet = Profiles.PlanImport([new("SmmSameIdW", false, "W", ProfileCsv.WorkshopLink(124)), new("SmmSameIdW", true, "W", ProfileCsv.WorkshopLink(126))]);
            var bothLinked = Profiles.PlanImport([new("SmmSameIdW", true, "W", ProfileCsv.WorkshopLink(124)), new("SmmSameIdW", true, "W", ProfileCsv.WorkshopLink(125))]);
            Check("... two Workshop copies say their items, \" (124)\"; a list naming both sets each by its link, the one off in copies off (no copy named); "
                + "a row on for an item not installed leaves the installed ones as they are; both on: both; two local copies say their folders, \" (SmmTestTwinA)\"",
                Copies.Which(w124) == " (124)" && Copies.Which(w125) == " (125)" && Copies.Which(a) == " (SmmTestTwinA)"
                && byLink.Mods.SingleOrDefault(m => m.Id == "SmmSameIdW") is { Source: ModSource.SteamWorkshop, Enabled: true, Copy: null, CopiesOff: ["w:124"] }
                && notYet.Mods.SingleOrDefault(m => m.Id == "SmmSameIdW") is { Enabled: true, Copy: null, CopiesOff: ["w:124"] }
                && bothLinked.Mods.SingleOrDefault(m => m.Id == "SmmSameIdW") is { Enabled: true, Copy: null, CopiesOff: [] },
                $"{Copies.Which(w124)}, {Copies.Which(w125)}, {Copies.Which(a)}; {Planned(byLink, "SmmSameIdW")}; {Planned(notYet, "SmmSameIdW")}; "
                + Planned(bothLinked, "SmmSameIdW"));
            List<ProfileCsv.Entry> otherFirst = [new("SmmSameIdW", true, "W", ProfileCsv.WorkshopLink(125)), new("SmmSameIdW", true, "W", ProfileCsv.WorkshopLink(124))];
            var (switchedTo, switchPreview) = (Profiles.PlanImport(otherFirst), ProfileShare.PreviewFor(otherFirst, "Other first"));
            Check("... both Workshop copies on, the other one first in the list: no box changes, and nothing else does (the preview says None)",
                switchedTo is { On: 0, Off: 0, Same: 2 } && switchedTo.Mods.SingleOrDefault(m => m.Id == "SmmSameIdW") is { Enabled: true, Copy: null, CopiesOff: [] }
                && switchPreview.Contains("Changes compared to the current profile:\n  •  None"), $"{Planned(switchedTo, "SmmSameIdW")}; {switchPreview}");
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
            Copies.TestRecount();
        }

        // A local copy beside two Workshop items of one mod (made up, as found at launch; the local copy newer, so the game
        // keeps it on either branch), all ticked, as the active profile has them: the local copy loads, no unsaved change.
        // Then one Workshop item newer than the local copy: on the beta it loads (the game's rule), the local copy saying
        // the game prioritizes a higher version, the other item (lower than the local copy, which doesn't load) with no
        // reason; Changes lists the switch, as an update while the game runs makes it. On stable the local copy still loads,
        // and both items say why.
        const string Mix = "SmmSameIdMix";
        var mixLocal = Made(Mix, "MixLocal", ModLoadState.Loaded, version: "2.0.0");
        var (mix61, mix62) = (Made(Mix, "Mix61", ModLoadState.DisabledDuplicate, 61), Made(Mix, "Mix62", ModLoadState.DisabledDuplicate, 62));
        GameMods().AddRange([mixLocal, mix61, mix62]);
        Copies.TestRecount(); // as many as the Workshop items above
        var mixOff = Store.Data.CopiesOff;
        try
        {
            History.Quietly(data =>
            {
                if (data.Profiles.FirstOrDefault(p => p.Name == data.ActiveProfile) is { } active)
                    foreach (var list in new[] { active.Mods, active.Edited })
                        list?.AddRange([new ProfileMod(Mix, ModSource.ModsDirectory, true), new ProfileMod(Mix, ModSource.SteamWorkshop, true, CopiesOff: [])]);
            });
            Profiles.Track();
            var mixClean = !Profiles.Edited && Copies.Loading(Mix) == mixLocal && !HistoryBar.Describe().Any(l => l.Contains(Mix));
            Check("... a local copy (newer) beside two Workshop items, all ticked and saved in the profile: the local copy loads, no unsaved change (and no "
                + "line in Changes)", mixClean, $"loads {Copies.KeyOf(Copies.Loading(Mix))}, edited {Profiles.Edited}; {string.Join(" | ", HistoryBar.Describe().Where(l => l.Contains(Mix)))}");
            if (mix61.manifest is { } newer)
                newer.version = "3.0.0";
            var w = Compat.WorkshopBeatsOlderLocal;
            var mixLoads = w ? mix61 : mixLocal;
            var (lostLocal, lost62) = (Copies.LosesTo(mixLocal), Copies.LosesTo(mix62));
            // As an update while the game runs makes it: Changes says the newer loads instead of the local copy that loaded.
            var updated = HistoryBar.Describe().Contains(Draft(CopySwitchLine, ("mod", Mix), ("to", Copies.Named(mix61)), ("from", Copies.Named(mixLocal))));
            Check("... one Workshop item newer than the local copy: " + (w ? "on the beta it loads (the game's rule), the local copy says the game prioritizes "
                + "a higher version, the other item has no reason (it lost to the local copy, which doesn't load), and Changes lists the switch"
                : "on stable the local copy still loads, and each item says the game prioritizes a local copy"),
                Copies.Loading(Mix) == mixLoads && updated == w
                && (w ? lostLocal is { Why: Copies.Why.Higher } && lostLocal.Value.Loads == mix61 && lost62 is { Why: Copies.Why.Other }
                    && ModDetails.CopyLine(mix62, false)?.Text == Draft(Template("SPIRE_MOD_MANAGER.COPY_LOSES_PLAIN"), ("other", Copies.Named(mix61)))
                    : lostLocal == null && lost62 is { Why: Copies.Why.Local } && Copies.LosesTo(mix61) is { Why: Copies.Why.Local }
                    && ModDetails.CopyLine(mix62, false)?.Text == Draft(CopyLoses, ("other", Copies.Named(mixLocal)), ("why", WhyLocal))),
                $"beta rule {w}; loads {Copies.KeyOf(Copies.Loading(Mix))}; local {lostLocal?.Why}, 62 {lost62?.Why}; line {ModDetails.CopyLine(mix62, false)?.Text}; "
                + string.Join(" | ", HistoryBar.Describe().Where(l => l.Contains(Mix))));
        }
        finally
        {
            GameMods().Remove(mixLocal);
            GameMods().Remove(mix61);
            GameMods().Remove(mix62);
            Copies.TestRecount();
            Store.Data.CopiesOff = mixOff;
            Store.SaveViewState();
            History.Quietly(data =>
            {
                foreach (var profile in data.Profiles)
                {
                    profile.Mods.RemoveAll(m => m.Id == Mix);
                    profile.Edited?.RemoveAll(m => m.Id == Mix);
                }
            });
            Profiles.Track();
        }

        // Restore of a working setup (the crash dialog's) with our data loaded: the copy that loaded then (A) the only one
        // of the twins ticked (B into copies_off, A out), in our file too.
        if (SaveManager.Instance.SettingsSave.ModSettings is { } restoring)
        {
            var listWas = restoring.ModList.Select(e => new SettingsSaveMod { Id = e.Id, Source = e.Source, IsEnabled = e.IsEnabled }).ToList();
            var offBefore = Store.Data.CopiesOff;
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
                Check("... Restore of a working setup with our data loaded: the copy that loaded then (A) the only one ticked (B in copies_off, A out), in "
                    + "our file too", CopiesOff().Contains(keyB) && !CopiesOff().Contains(keyA) && FileCopiesOff().Contains(keyB) && !FileCopiesOff().Contains(keyA)
                    && Loads() == "l:SmmTestTwinA", $"off {string.Join(", ", CopiesOff())}; on disk {string.Join(", ", FileCopiesOff())}; relaunched {relaunched}");
            }
            finally
            {
                ModEntry.TestRelaunch = null;
                RelaunchCheck.Close();
                restoring.ModList.Clear();
                restoring.ModList.AddRange(listWas);
                SaveManager.Instance.SaveSettings();
                Store.Data.CopiesOff = offBefore;
                Store.SaveViewState();
                Profiles.RefreshTickboxes(screen);
                CategoryList.Redraw(content);
                await Wait(menu, 0.3);
            }
        }

        // The relaunch check with both copies ticked, the one that loads (B) given a need it can't meet: its problem, and
        // the other copy losing to it (only one copy of an id loads).
        await Tick(rowA, true);
        var needsWere = b.manifest?.dependencies;
        if (b.manifest is { } manifest)
            manifest.dependencies = [new ModDependency(Missing)];
        try
        {
            RelaunchCheck.Show(screen);
            await Wait(menu, 0.3);
            var body = (menu.GetTree().Root.GetNodeOrNull(RelaunchCheck.Name)?.FindChild("Body", true, false) as RichTextLabel)?.GetParsedText() ?? "";
            RelaunchCheck.Cancel();
            await Wait(menu, 0.3);
            Check("... the relaunch check sees the loading copy's problem, by its copy's name, and the other copy losing to it",
                body.Split('\n').Any(l => l.Contains($"{Named(b)}  ") && l.Contains(Missing)) && body.Contains($"{Named(a)}  {Draft(RelaunchLoses, ("other", Named(b)))}"), body);
        }
        finally
        {
            if (b.manifest is { } restored)
                restored.dependencies = needsWere;
        }
        History.Undo(); // the tick
        await Wait(menu, 0.3);

        // Both copies off, then a tick on one of the two rows selected: both on (each box of the rows), the higher loading
        // whichever was clicked.
        Dependencies.Set([b], false, screen);
        CategoryList.Redraw(content);
        await Wait(menu, 0.3);
        var beforeTicks = History.Steps(History.Track.List);
        Selection.Add(content, [rowA, rowB]);
        await Tick(rowA, true);
        var bothOnLoadsB = Loads() == "l:SmmTestTwinB" && Copies.Ticked(a) && Copies.Ticked(b) && SwitchOn();
        Selection.Clear(content, record: false);
        while (History.Steps(History.Track.List) > beforeTicks && History.CanUndo)
            History.Undo(); // the group tick, then the selection
        History.Undo(); // the off
        await Wait(menu, 0.3);
        Check("... both copies off, a tick on one of both selected: both on, the higher loading (the one not clicked); undone, as before",
            bothOnLoadsB && Loads() == "l:SmmTestTwinB" && SwitchOn() && Ticked(rowB) && !Ticked(rowA) && OffAsAtStart(), $"both on, B loading {bothOnLoadsB}; {Say()}");
        // Both off, Enable selected over both (no row clicked): both on, the higher loading.
        Dependencies.Set([b], false, screen);
        CategoryList.Redraw(content);
        await Wait(menu, 0.3);
        CategoryList.OnOffItems(content, [rowA, rowB], selected: true)[0].OnSelect?.Invoke();
        await Wait(menu, 0.3);
        var enabledBoth = Loads() == "l:SmmTestTwinB" && Copies.Ticked(b) && Copies.Ticked(a) && SwitchOn();
        History.Undo(); // Enable selected
        History.Undo(); // the off
        await Wait(menu, 0.3);
        Check("... both off, Enable selected over both: both on, the higher loading; undone, as before",
            enabledBoth && Loads() == "l:SmmTestTwinB" && SwitchOn() && Ticked(rowB) && !Ticked(rowA) && OffAsAtStart(), $"both on {enabledBoth}; {Say()}");

        // A session that can't save our data: a tick on a copy sharing its switch is the game's own (every copy's switch),
        // as which of them are ticked is ours.
        Store.TestReadOnly(true);
        try
        {
            await Tick(rowA, true);
            Check("... a session that can't save our data: ticking the other copy is the game's tick, copies_off stays",
                Loads() == "l:SmmTestTwinB" && !Ticked(rowA) && Ticked(rowB) && SwitchOn() && OffAsAtStart(), Say());
        }
        finally
        {
            Store.TestReadOnly(false);
        }

        // The self-check finding the choice not taken: every copy says it, and what loads reads as the game's own choice
        // (the copy that loaded), so A alone ticked, pending until then, isn't.
        var pendingBefore = ModEntry.HasPendingChanges();
        await Tick(rowA, true);
        await Tick(rowB, false);
        var pendingAlone = ModEntry.HasPendingChanges() && Loads() == "l:SmmTestTwinA";
        SameIdLoad.TestResult(TwinId, SameIdLoad.Result.NotTaken);
        var notApplied = (ModDetails.CopyLine(a, false)?.Text, ModDetails.CopyLine(b, false)?.Text);
        var (pendingUnchosen, loadsUnchosen) = (ModEntry.HasPendingChanges(), Loads());
        SameIdLoad.TestResult(TwinId, null);
        History.Undo(); // B off
        History.Undo(); // A on
        await Wait(menu, 0.3);
        const string couldnt = "Spire Mod Manager couldn't choose which copy was loaded. If the game is on a different version, Spire Mod Manager may need to be updated.";
        Check("... the choice not taken (the self-check): both copies say it, the copy that loaded reads as the one that loads, and the other alone ticked "
            + "is no relaunch away", notApplied == (couldnt, couldnt) && SameIdLoad.ResultOf(TwinId) == SameIdLoad.Result.Taken && pendingBefore == LoadOrder.OrderChanged
            && pendingAlone && pendingUnchosen == LoadOrder.OrderChanged && loadsUnchosen == "l:SmmTestTwinB" && Loads() == "l:SmmTestTwinB",
            $"{notApplied}; then {SameIdLoad.ResultOf(TwinId)}; pending {pendingBefore}, A alone {pendingAlone}, not taken {pendingUnchosen} (loads {loadsUnchosen})");

        // The copy that loads failing for its own reasons (a newer game needed), the other copy off: how to load that one.
        var (bState, bErrors) = (b.state, b.errors);
        try
        {
            b.state = ModLoadState.Failed;
            b.errors = [new LocString("main_menu_ui", "MOD_ERROR.GAME_VERSION_UNSUPPORTED")];
            var failedLine = ModDetails.CopyLine(b, false)?.Text;
            Check("... the loading copy failed for its own reasons: its panel says the other copy is installed, and how to load it instead",
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

        // New copies of the mod arriving, the switch on: ticked, as the game has them; no undo step; the banner shows, the
        // title white (a ticked copy). A lower one (1.0.5): B still loads, nothing pending, the popup says it's enabled and
        // B loads. A higher one (1.2.0): it loads after a relaunch, pending, and the popup names it.
        async Task<(bool Ok, string Say, string Popup)> Arrives(string folder, string version, bool takesOver)
        {
            var copy = new Mod
            {
                path = $"C:/SmmSameIdTest/{folder}", modSource = ModSource.ModsDirectory, state = ModLoadState.AddedAtRuntime,
                manifest = new ModManifest { id = TwinId, name = "Test Twin", version = version },
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
            var loading = takesOver ? copy : b;
            var ok = Copies.Ticked(copy) && Copies.Loads(copy) == takesOver && Copies.Loads(b) == !takesOver && Installed.Mods.Contains(copy)
                && ModEntry.HasPendingChanges() == (takesOver || LoadOrder.OrderChanged) && noStep && (banner || !Layout.Wide) && copyTitle == Colors.White
                && popup.Contains(Draft(NewCopyOn, ("mods", $"Test Twin ({folder})"), ("loads", Named(loading)))) && CopiesBroken() == null;
            var said = $"loads {Loads()}, ticked {Copies.Ticked(copy)}, pending {ModEntry.HasPendingChanges()}, no step {noStep}, banner {banner}, title {copyTitle}; "
                + $"{CopiesBroken() ?? "kept"}";
            Forget(copy);
            CategoryList.Redraw(content);
            await Wait(menu, 0.3);
            return (ok, said, popup);
        }
        var lower = await Arrives("SmmTestTwinC", "1.0.5", takesOver: false);
        Check("... a new lower copy arriving with the switch on is ticked, as the game has it, and B still loads: nothing pending, no undo step, the banner "
            + "up, its title white; the popup says it's enabled and B loads", lower.Ok, $"{lower.Say}; {lower.Popup}");
        var higher = await Arrives("SmmTestTwinE", "1.2.0", takesOver: true);
        Check("... a new higher copy arriving with the switch on is ticked and loads after a relaunch (the higher version): pending, no undo step, the "
            + "banner up; the popup names it as the copy that loads", higher.Ok, $"{higher.Say}; {higher.Popup}");
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
        var aloneOn = Ticked(rowB) && !Copies.Ticked(copyOff) && !Ticked(rowA) && CopiesOff().Contains($"{TwinId}|l:SmmTestTwinD") && Loads() == "l:SmmTestTwinB"
            && SwitchOn();
        History.Undo(); // the tick
        History.Undo(); // the off
        await Wait(menu, 0.3);
        Forget(copyOff);
        CategoryList.Redraw(content);
        await Wait(menu, 0.3);
        Check("... with the switch off, a new copy arrives off (the popup says it's disabled); a tick on another copy brings that one alone, the new one "
            + "staying off (though higher); undone, as before",
            arrivedOff && aloneOn && offPopup.Contains(Draft(NewCopyOff, ("mods", "Test Twin (SmmTestTwinD)"))) && Loads() == "l:SmmTestTwinB" && Ticked(rowB)
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

        // A mod installed once (made up, as found at launch) and an active profile naming no copy of it (as from before
        // copies). A second copy comes mid-session at the same version: ticked; the copy there loads (a tie: the copy
        // installed first, our record's first), and the active profile has the new one off, so its arriving on is the
        // profile's unsaved edit; unticking the copy there makes the new one load, one undo step; an update of the new copy
        // isn't announced as a new copy again. Then two copies of a mod new to the game, one after the other (the first
        // one's popup held back: one every 30 s): the new-mods words, not a new copy's, both ticked, and the copy that came
        // first loads (installed first).
        const string Back = "SmmSameIdBack", Pair = "SmmSameIdPair";
        var backFirst = Made(Back, "Back1", ModLoadState.Loaded);
        var (backSecond, backUpdate) = (Made(Back, "Back2", ModLoadState.AddedAtRuntime), Made(Back, "Back2", ModLoadState.AddedAtRuntime));
        var (pairFirst, pairSecond) = (Made(Pair, "PairB", ModLoadState.AddedAtRuntime), Made(Pair, "PairA", ModLoadState.AddedAtRuntime));
        GameMods().Add(backFirst);
        History.Quietly(data =>
        {
            if (data.Profiles.FirstOrDefault(p => p.Name == data.ActiveProfile) is { } active)
                foreach (var list in new[] { active.Mods, active.Edited })
                    list?.Add(new ProfileMod(Back, ModSource.ModsDirectory, true));
        });
        string? BackLoads() => LoadingCopy(Back);
        ProfileMod? Held() => Profiles.Active?.Mods.FirstOrDefault(m => m.Id == Back);
        var backSteps = History.Steps(History.Track.List);
        NewModPopupPatch.TestReset();
        NModalContainer.Instance?.Clear();
        GameInstalls(backSecond);
        await Wait(menu, 0.4);
        var backPopup = string.Join(" | ", PopupTexts());
        NModalContainer.Instance?.Clear();
        var cameBack = BackLoads() == "l:Back1" && Copies.Ticked(backFirst) && Copies.Ticked(backSecond) && !Copies.Loads(backSecond)
            && Store.Data.Copies?.GetValueOrDefault(Back) is ["l:Back1", "l:Back2"] && Held() is { Copy: null, CopiesOff: ["l:Back2"] } && Profiles.Edited
            && History.Steps(History.Track.List) == backSteps && backPopup.Contains(Draft(NewCopyOn, ("mods", $"{Back} (Back2)"), ("loads", $"{Back} (Back1)")));
        var cameSay = $"loads {BackLoads()}, on {Copies.Ticked(backFirst)}/{Copies.Ticked(backSecond)}, record {string.Join(",", Store.Data.Copies?.GetValueOrDefault(Back) ?? [])}, "
            + $"held off [{string.Join(",", Held()?.CopiesOff ?? [])}], edited {Profiles.Edited}, steps {History.Steps(History.Track.List) - backSteps}; {backPopup}";
        Dependencies.Set([backFirst], false, screen); // as its tick does (CopyTickPatch), the profile and history after
        Profiles.Track();
        History.Record();
        var switched = BackLoads() == "l:Back2" && !Copies.Ticked(backFirst) && Copies.Ticked(backSecond) && Profiles.Edited
            && History.Steps(History.Track.List) == backSteps + 1;
        NewModPopupPatch.TestReset();
        GameInstalls(backUpdate);
        await Wait(menu, 0.4);
        var backUpdatePopup = string.Join(" | ", PopupTexts());
        NModalContainer.Instance?.Clear();
        var updateKeeps = BackLoads() == "l:Back2" && Installed.Mods.Contains(backUpdate) && backUpdatePopup.Contains("New mods were detected")
            && !NewCopyWords(backUpdatePopup);
        GameInstalls(pairFirst); // within 30 s of the last popup: none for it
        await Wait(menu, 0.2);
        NewModPopupPatch.TestReset();
        NModalContainer.Instance?.Clear();
        GameInstalls(pairSecond);
        await Wait(menu, 0.4);
        var pairPopup = string.Join(" | ", PopupTexts());
        NModalContainer.Instance?.Clear();
        var pairLoads = LoadingCopy(Pair);
        var pairNew = pairPopup.Contains("New mods were detected") && !NewCopyWords(pairPopup) && pairLoads == "l:PairB"
            && Copies.Ticked(pairFirst) && Copies.Ticked(pairSecond) && Store.Data.Copies?.GetValueOrDefault(Pair) is ["l:PairB", "l:PairA"];
        History.Undo(); // the switch
        await Wait(menu, 0.3);
        // Back to the copy there; the new copy's arriving on is still the profile's edit, and nothing else about Back (the
        // pair below arrived on since: its edits too).
        var (backOn, backOff) = Profiles.UnsavedChanges();
        var backUndone = BackLoads() == "l:Back1" && backOn.Where(m => m.Id.StartsWith(Back)).Select(m => m.Name).Distinct().SequenceEqual([$"{Back} (Back2)"])
            && !backOff.Any(m => m.Id.StartsWith(Back));
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
        Check("... a copy new to our record mid-session at the same version arrives ticked and the copy there loads (installed first; its popup says which "
            + "copy loads); the active profile, naming no copy, has the new one off, so its arriving on is the profile's edit; unticking the copy there makes "
            + "the new one load, one undo step; an update of the new copy isn't a new copy again",
            cameBack && switched && updateKeeps && backUndone,
            $"{cameSay}; switched {switched}, update keeps {updateKeeps} ({backUpdatePopup}), undone {backUndone} "
            + $"(+{string.Join(",", backOn.Select(m => m.Name))} -{string.Join(",", backOff.Select(m => m.Name))})");
        Check("... two copies of a mod new to the game, one after the other: the new-mods words, both ticked, the copy that came first first in our record "
            + "and loading", pairNew, $"loads {pairLoads}, record {string.Join(",", Store.Data.Copies?.GetValueOrDefault(Pair) ?? [])}; {pairPopup}");

        // A lone copy in copies_off (its ticked twin unsubscribed since: our record has both), in the active profile's
        // copies off and every undo snapshot's, as a launch would leave them; a new copy at a lower version arrives
        // mid-session (P2, N1): the lone copy leaves copies_off everywhere, stays ticked and loads, the popup naming it;
        // Changes lists nothing about it, and Revert all keeps it loading.
        const string Lone = "SmmSameIdLoneMid";
        var loneThere = Made(Lone, "Lone1", ModLoadState.Loaded, version: "1.1.0");
        var loneCome = Made(Lone, "Lone3", ModLoadState.AddedAtRuntime);
        GameMods().Add(loneThere);
        Copies.TestRecount();
        History.Quietly(data =>
        {
            (data.Copies ??= [])[Lone] = ["l:Lone1", "l:Lone2"];
            data.CopiesOff = Store.TidyOff([.. data.CopiesOff ?? [], $"{Lone}|l:Lone1"]);
            if (data.Profiles.FirstOrDefault(p => p.Name == data.ActiveProfile) is { } active)
                foreach (var list in new[] { active.Mods, active.Edited })
                    list?.Add(new ProfileMod(Lone, ModSource.ModsDirectory, true, CopiesOff: ["l:Lone1"]));
        });
        Profiles.Track();
        var loneClean = !Profiles.Edited && LoadingCopy(Lone) == "l:Lone1";
        NewModPopupPatch.TestReset();
        NModalContainer.Instance?.Clear();
        GameInstalls(loneCome);
        await Wait(menu, 0.4);
        var lonePopup = string.Join(" | ", PopupTexts());
        NModalContainer.Instance?.Clear();
        Profiles.Track();
        var loneEverywhere = !CopiesOff().Contains($"{Lone}|l:Lone1") && History.Loaded is { } loneLaunch && !Store.TicksIn(loneLaunch.Store).Off.Contains($"{Lone}|l:Lone1")
            && Profiles.Active?.Mods.FirstOrDefault(m => m.Id == Lone) is { CopiesOff: ["l:Lone3"] };
        var loneSay = $"loads {LoadingCopy(Lone)}, ticked {Copies.Ticked(loneThere)}/{Copies.Ticked(loneCome)}, off {string.Join(",", CopiesOff().Where(k => k.StartsWith(Lone)))}, "
            + $"held [{string.Join(",", Profiles.Active?.Mods.FirstOrDefault(m => m.Id == Lone)?.CopiesOff ?? [])}]; {string.Join(" | ", HistoryBar.Describe().Where(l => l.Contains(Lone)))}";
        var loneKeeps = LoadingCopy(Lone) == "l:Lone1" && Copies.Ticked(loneThere) && Copies.Ticked(loneCome)
            && !HistoryBar.Describe().Any(l => l.Contains($"{Lone} (Lone1)")) && lonePopup.Contains(Draft(NewCopyOn, ("mods", $"{Lone} (Lone3)"), ("loads", $"{Lone} (Lone1)")));
        History.RevertMods();
        await Wait(menu, 0.3);
        var loneReverted = LoadingCopy(Lone) == "l:Lone1" && Copies.Ticked(loneThere);
        History.Undo(); // the revert
        await Wait(menu, 0.3);
        if (Rows().FirstOrDefault(r => r.Mod == loneCome) is { } loneRow)
        {
            content.RemoveChild(loneRow);
            loneRow.QueueFree();
        }
        GameForgets(loneCome);
        GameMods().Remove(loneThere);
        Copies.TestRecount();
        SaveManager.Instance.SettingsSave.ModSettings?.ModList.RemoveAll(e => e.Id == Lone);
        History.Quietly(data =>
        {
            data.Copies?.Remove(Lone);
            foreach (var key in data.Mods.Keys.Where(k => Copies.IdOf(k) == Lone).ToList())
                data.Mods.Remove(key);
            data.CopiesOff = data.CopiesOff?.Where(k => Copies.IdOf(k) != Lone).ToList();
            foreach (var profile in data.Profiles)
            {
                profile.Mods.RemoveAll(m => m.Id == Lone);
                profile.Edited?.RemoveAll(m => m.Id == Lone);
            }
        });
        Profiles.Track();
        CategoryList.Redraw(content);
        await Wait(menu, 0.3);
        Check("... a lone copy in copies_off (its ticked twin gone), a new lower copy arriving mid-session: the lone copy leaves copies_off (now and in "
            + "the launch's state) and the active profile's copies off, stays ticked and loads, the popup naming it; Changes says nothing of it; Revert all "
            + "keeps it loading", loneClean && loneEverywhere && loneKeeps && loneReverted, $"clean before {loneClean}; {loneSay}; reverted {loneReverted}; {lonePopup}");

        // A copy uninstalled and back mid-session (made up: our record has it, from when both were installed), the active
        // profile saved with both ticked: it arrives ticked and keeps the profile's tick, so nothing is unsaved, as when it's
        // back before a launch. Then gone again, unticked meanwhile (in copies_off, and the profile's): back mid-session it
        // stays unticked (it's known: no copy's key is cleared), nothing pending, the profile still Saved (P1).
        const string Again = "SmmSameIdAgain";
        var (againThere, againBack) = (Made(Again, "Again1", ModLoadState.Loaded), Made(Again, "Again2", ModLoadState.AddedAtRuntime, version: "1.1.0"));
        GameMods().Add(againThere);
        Copies.TestRecount(); // one in for one out above
        History.Quietly(data =>
        {
            (data.Copies ??= [])[Again] = ["l:Again1", "l:Again2"];
            if (data.Profiles.FirstOrDefault(p => p.Name == data.ActiveProfile) is { } active)
                foreach (var list in new[] { active.Mods, active.Edited })
                    list?.Add(new ProfileMod(Again, ModSource.ModsDirectory, true, CopiesOff: []));
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
        var keptTick = againClean && Copies.Ticked(againBack) && againHeld is { CopiesOff: [] } && !Profiles.Edited && againOn.Count == 0;
        var againSay = $"clean before {againClean}; ticked {Copies.Ticked(againBack)}, held off [{string.Join(",", againHeld?.CopiesOff ?? [])}], edited {Profiles.Edited} "
            + $"(+{string.Join(",", againOn)})";
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
        // Gone again, unticked meanwhile (as a launch without it, the player having unticked it before), in our data and
        // the profile's; back, it stays as it was: off, not loading though higher, nothing pending, Saved.
        Uninstall(againBack);
        History.Quietly(data =>
        {
            data.CopiesOff = Store.TidyOff([.. data.CopiesOff ?? [], $"{Again}|l:Again2"]);
            if (data.Profiles.FirstOrDefault(p => p.Name == data.ActiveProfile) is { } active)
                foreach (var list in new[] { active.Mods, active.Edited })
                    for (var i = 0; i < (list?.Count ?? 0); i++)
                        if (list?[i] is { Id: Again } entry)
                            list[i] = entry with { CopiesOff = ["l:Again2"] };
        });
        Profiles.Track();
        var againReturn = Made(Again, "Again2", ModLoadState.AddedAtRuntime, version: "1.1.0");
        NewModPopupPatch.TestReset();
        GameInstalls(againReturn);
        await Wait(menu, 0.4);
        NModalContainer.Instance?.Clear();
        Profiles.Track();
        var stayedOff = !Copies.Ticked(againReturn) && CopiesOff().Contains($"{Again}|l:Again2") && LoadingCopy(Again) == "l:Again1" && !Copies.Loads(againReturn)
            && !Profiles.Edited && ModEntry.HasPendingChanges() == LoadOrder.OrderChanged;
        var offSay = $"ticked {Copies.Ticked(againReturn)}, off {string.Join(",", CopiesOff().Where(k => k.StartsWith(Again)))}, loads {LoadingCopy(Again)}, edited "
            + $"{Profiles.Edited}, pending {ModEntry.HasPendingChanges()}";
        Uninstall(againReturn);
        GameMods().Remove(againThere);
        Copies.TestRecount();
        SaveManager.Instance.SettingsSave.ModSettings?.ModList.RemoveAll(e => e.Id == Again);
        History.Quietly(data =>
        {
            data.Copies?.Remove(Again);
            foreach (var key in data.Mods.Keys.Where(k => Copies.IdOf(k) == Again).ToList())
                data.Mods.Remove(key);
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
        Check("... an unticked copy missing at launch, back mid-session: still unticked (it's known: its key stays), not loading though higher, nothing "
            + "pending, the active profile still Saved", stayedOff, offSay);
        Check("... as it started: B loading, A off (copies_off as the page opened with, in our file too), nothing changed since the page opened for the twins",
            Loads() == "l:SmmTestTwinB" && Ticked(rowB) && !Ticked(rowA) && SwitchOn() && !Profiles.Edited && OffAsAtStart() && CopiesBroken() == null, Say());
    }

    // Whether a popup has a new copy's words (NewModPopupPatch), whichever mods they name: each text's longest run
    // between its values.
    static bool NewCopyWords(string popup) => new[] { "SPIRE_MOD_MANAGER.NEW_COPY_ON_BODY", "SPIRE_MOD_MANAGER.NEW_COPIES_ON_BODY",
            "SPIRE_MOD_MANAGER.NEW_COPY_OFF_BODY" }
        .Select(key => Template(key).Split('{', '}').Where((_, i) => i % 2 == 0).MaxBy(s => s.Length) ?? "")
        .Any(words => words.Length > 0 && popup.Contains(words));

    // The fixture twins both ticked at launch (BothTickedQuietly: as a launch would have them, A set aside, B loaded): B,
    // the higher version, loads and A won't, with no duplicate error and nothing pending or changed; A's title white, not
    // the game's grey for a copy set aside; its status and panel say B loads instead, the higher version; both chips
    // yellow, naming the copy that loads, and both copy lines in that yellow (A off beside B: its own colour); the banner
    // and its card; the relaunch check, gone with the banner's Dismiss;
    // Find takes the copy that loads, and a need of the copy that won't load is no problem. Unticking the copy that loads:
    // the other one loads, one undo step, pending; undo, redo, Revert all. A higher version arriving for A mid-session
    // (an update) switches which loads with no box changed: Changes and the main menu say so, unless the next launch
    // couldn't make the switch. Unticking the one that doesn't load: a change, nothing pending. The last ticked copy unticked:
    // the switch off, copies_off kept; then a tick on the other brings it alone. Sharing: a row per ticked copy, the one that loads first; read back, both ticked. Titles: the
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
        string? Loads() => LoadingCopy(TwinId);
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
        string Say() => $"loads {Loads()}, boxes A {Ticked(rowA)} B {Ticked(rowB)}, loads A {Copies.Loads(a)} B {Copies.Loads(b)}, switch {SwitchOn()}, "
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
        // Their Duplicate chips with one copy ticked (no clash): the plain one's muted tan, not the Local chip's grey beside it.
        var plainChips = new[] { a, b }.Select(m => Tags.For(m)).ToList();
        Check("same ID, one copy ticked: both Duplicate chips in the plain one's muted tan, apart from the Local chip's grey beside them and the clash's yellow",
            plainChips.All(chips => chips.FirstOrDefault(c => c.Text == DuplicateChip) is { } duplicate && duplicate.Color == Tags.Duplicate
                && duplicate.Color != Tags.SameIdClash && chips.FirstOrDefault(c => c.Text == Menus.Text("SPIRE_MOD_MANAGER.TAG_LOCAL")) is { } local
                && local.Color != duplicate.Color),
            string.Join(" / ", plainChips.Select(chips => string.Join(", ", chips.Select(c => $"{c.Text} {c.Color.ToHtml(false)}")))));
        var offWere = CopiesOff();
        // The copy lines' colour: every one in the Duplicate chip's warning yellow (Jordan: "ALL duplicate messages"): A off
        // beside B (B has none then), and both ticked, B (the copy that loads) too.
        var clash = $"#{Tags.SameIdClash.ToHtml(false)}";
        var (offColor, onAlone) = (ModDetails.CopyLine(a, false)?.Color, ModDetails.CopyLine(b, false));
        BothTickedQuietly(true);
        Profiles.RefreshTickboxes(screen);
        CategoryList.Redraw(content);
        await Wait(menu, 0.3);
        var bothOff = CopiesOff();
        var steps = History.Steps(History.Track.List);
        var chips = new[] { a, b }.Select(m => Tags.For(m).FirstOrDefault(c => c.Text == DuplicateChip)).ToList();
        var lineColors = (A: ModDetails.CopyLine(a, false)?.Color, B: ModDetails.CopyLine(b, false)?.Color);
        Check("same ID, the copy lines all in the Duplicate chip's warning yellow: A off beside B, B none then; both ticked, each, the copy that loads too",
            offColor == clash && onAlone == null && lineColors == (clash, clash),
            $"off {offColor}, B alone {onAlone}; both {lineColors.A} / {lineColors.B} (yellow {clash})");
        var bannerText = (SameId()?.FindChild("SameIdWarning", true, false) as Label)?.Text;
        var card = await BannerCard(menu, screen, SameId());
        Check("same ID, both copies ticked at launch: B (the higher version) loads and A won't, no duplicate error, nothing pending or changed; A's title "
            + "white, not the game's grey for a copy set aside; A reads Won't load, B loading in its place (the game prioritizes a higher version); both Duplicate chips yellow (B's, "
            + "the copy that loads, too), naming the copy that loads; the banner and its card say which loads; Find takes the copy that loads",
            greyOff && Ticked(rowA) && Ticked(rowB) && Loads() == "l:SmmTestTwinB" && Copies.Loads(b) && !Copies.Loads(a) && SwitchOn() && !bothOff.Contains(keyA)
            && b.state == ModLoadState.Loaded && a.state == ModLoadState.DisabledDuplicate
            && !ModManager.Mods.Any(m => m.errors?.Any(e => e.LocEntryKey == "MOD_ERROR.DUPLICATE_ID") == true)
            && !ModEntry.HasPendingChanges() && TwinChanges().Count == 0 && !Profiles.Edited && Title(rowA) == Colors.White && Title(rowB) == Colors.White
            && ModDetails.Status(a).Contains(StatusWontLoad) && ModDetails.Status(b).Contains("Enabled")
            && ModDetails.CopyLine(a, false)?.Text == Draft(CopyLoses, ("other", Named(b)), ("why", WhyHigher))
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

        // Unticking the copy that loads: the other one loads (the copy that loaded is off), a relaunch away.
        var (onBefore, offBefore, _) = HistoryBar.Counts();
        steps = History.Steps(History.Track.List);
        await Tick(rowB, false);
        var (onNow, offNow, _) = HistoryBar.Counts();
        Check("... unticking the copy that loads: the other one loads (B in copies_off, in our file too), one undo step; A reads Pending relaunch, "
            + "loading in B's place, and B too (it loaded, and won't); Changes: B off; pending; no banner",
            Loads() == "l:SmmTestTwinA" && CopiesOff().Contains(keyB) && FileCopiesOff().Contains(keyB) && SwitchOn() && Ticked(rowA) && !Ticked(rowB)
            && ModDetails.Status(a).Contains("Pending relaunch") && ModDetails.CopyLine(a, false)?.Text == $"Loads on relaunch, in place of {Named(b)}"
            && ModDetails.Status(b).Contains("Pending relaunch") && TwinChanges().SequenceEqual([$"Disabled: {Named(b)}"]) && onNow == onBefore && offNow == offBefore + 1
            && History.Steps(History.Track.List) == steps + 1 && ModEntry.HasPendingChanges() && SameId() == null && CopiesBroken() == null,
            $"{Say()}; counts +{onNow - onBefore} -{offNow - offBefore}");
        History.Undo();
        await Wait(menu, 0.3);
        var undone = Ticked(rowA) && Ticked(rowB) && Loads() == "l:SmmTestTwinB" && CopiesOff().SequenceEqual(bothOff) && FileCopiesOff().SequenceEqual(bothOff);
        History.Redo();
        await Wait(menu, 0.3);
        var redone = Loads() == "l:SmmTestTwinA" && Ticked(rowA) && !Ticked(rowB);
        History.RevertMods();
        await Wait(menu, 0.3);
        var reverted = Ticked(rowA) && Ticked(rowB) && Loads() == "l:SmmTestTwinB" && History.AtEntered && CopiesOff().SequenceEqual(bothOff);
        History.Undo(); // the revert
        await Wait(menu, 0.3);
        var revertUndone = Loads() == "l:SmmTestTwinA" && !Ticked(rowB);
        History.Undo(); // the untick
        await Wait(menu, 0.3);
        Check("... undone: both ticked, B loading, copies_off as before (in our file too); redo; Revert all goes back to both ticked, and undoes",
            undone && redone && reverted && revertUndone && Ticked(rowA) && Ticked(rowB) && Loads() == "l:SmmTestTwinB" && CopiesOff().SequenceEqual(bothOff),
            $"undo {undone}, redo {redone}, revert {reverted}, its undo {revertUndone}; {Say()}");

        // The copy that loads switched with no box changed, by a version change mid-session (A updated: the game adds a Mod
        // for it with its new manifest, 1.2.0, after the one found at launch): Changes says A loads instead of B, the main
        // menu's note counts it, pending; B says A loads instead as the higher version. Put back.
        var update = new Mod
        {
            path = a.path, modSource = ModSource.ModsDirectory, state = ModLoadState.AddedAtRuntime,
            manifest = new ModManifest { id = TwinId, name = a.manifest?.name, version = "1.2.0" },
        };
        GameMods().Add(update);
        Copies.TestRecount();
        var switchLine = Draft(CopySwitchLine, ("mod", a.manifest?.name ?? ""), ("to", Copies.Named(a)), ("from", Copies.Named(b)));
        List<string> switchChanges;
        string pendingNote;
        bool switchedPending;
        string? hintedLine;
        // Where the next launch can't put A in the id's first slot while Test Twin User needs the id (the update needing Test
        // Twin Late, made up: installed while the game runs, with no saved entry yet, so the next launch sorts it after every
        // listed mod), B loads as now: not counted, nor pending (announced, the new mod itself would be). A plan that
        // blocked A at this launch alone is as normal, as the next launch's order decides; one the game decided before us
        // too (Settle put us first). The self-check finding the choice not taken: the game's choice, not counted. Its switch
        // turned off is pending all the same. Fails on bed319a: its page took the block from this launch's plan alone, so
        // the first two the other way round.
        var stuck = new List<string>();
        var planWas = SameIdLoad.Plan.GetValueOrDefault(TwinId);
        var switchWas = Entries().Select(e => (Entry: e, e.IsEnabled)).ToList();
        var late = new Mod
        {
            path = "C:/SmmSameIdTest/TwinLate", modSource = ModSource.ModsDirectory, state = ModLoadState.AddedAtRuntime,
            manifest = new ModManifest { id = "SmmTestTwinLate", name = "Test Twin Late", version = "1.0.0" },
        };
        var updateNeeds = update.manifest?.dependencies;
        try
        {
            (switchChanges, pendingNote, switchedPending, hintedLine) = (HistoryBar.Describe(), MenuButton.PendingText(), ModEntry.HasPendingChanges(),
                ModDetails.CopyLine(b, false)?.Text);
            if (update.manifest is { } needing)
                needing.dependencies = [new ModDependency("SmmTestTwinLate")];
            GameMods().Add(late);
            Copies.TestRecount();
            // the block needs a mod on that needs the id
            var userOn = Dependencies.Find("SmmTestTwinUser") is { } twinUser && Copies.Ticked(twinUser);
            stuck.Add($"blocked {HistoryBar.CopySwitchCount()} {ModEntry.HasPendingChanges()}" + (userOn ? "" : " (Test Twin User off)"));
            GameMods().Remove(late);
            if (update.manifest is { } plain)
                plain.dependencies = updateNeeds;
            Copies.TestRecount();
            if (planWas != null)
            {
                foreach (var (name, entry) in new[] { ("blocked at this launch alone", planWas with { Why = SameIdLoad.Reason.DependencyAfterSlot, Blocked = "l:SmmTestTwinA",
                        Late = "SmmTestLib" }),
                    ("decided before us", planWas with { Why = SameIdLoad.Reason.DecidedBeforeUs }) })
                {
                    SameIdLoad.TestPlan(TwinId, entry);
                    stuck.Add($"{name} {HistoryBar.CopySwitchCount()} {ModEntry.HasPendingChanges()}");
                }
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
            GameMods().Remove(late);
            if (update.manifest is { } restored)
                restored.dependencies = updateNeeds;
            GameMods().Remove(update);
            Copies.TestRecount();
        }
        Check("... a switch the next launch can't make isn't counted, nor pending: blocked at the next launch, or the self-check finding the choice not "
            + "taken; blocked at this launch alone, or decided before us: both; the id's switch turned off is pending all the same",
            string.Join(", ", stuck) == "blocked 0 False, blocked at this launch alone 1 True, decided before us 1 True, not taken 0 False, switch off True",
            string.Join(", ", stuck));
        Check("... the copy that loads switched with no box changed (A updated mid-session to a higher version): Changes says A loads instead, the main "
            + "menu's note counts it, pending; B says A loads instead as the higher version; put back, none of it",
            switchChanges.Contains(switchLine) && pendingNote.StartsWith("1 ") && switchedPending && hintedLine == Draft(CopyLoses, ("other", Named(a)), ("why", WhyHigher))
            && Loads() == "l:SmmTestTwinB" && Ticked(rowA) && Ticked(rowB) && !HistoryBar.Describe().Contains(switchLine) && !ModEntry.HasPendingChanges(),
            $"note {pendingNote}; changes {string.Join(" | ", switchChanges)}; B's line {hintedLine}; then {Say()}");

        // Unticking the copy that doesn't load: a change, but what loads is the same, so nothing is pending.
        await Tick(rowA, false);
        var pendingLine = screen.GetNode<Control>("%PendingChangesLabel").Visible;
        Check("... unticking the ticked copy that doesn't load: a change (Changes: A off), nothing pending, B loading, no banner",
            Loads() == "l:SmmTestTwinB" && !Ticked(rowA) && Ticked(rowB) && CopiesOff().Contains(keyA) && TwinChanges().SequenceEqual([$"Disabled: {Named(a)}"])
            && !ModEntry.HasPendingChanges() && !pendingLine && SameId() == null && CopiesBroken() == null, $"{Say()}; pending line {pendingLine}");

        // From one ticked copy (A off now): the last one unticked turns the switch off (nothing loads) and keeps copies_off;
        // a tick on the other copy then brings it alone.
        var offOne = CopiesOff();
        await Tick(rowB, false);
        var allOff = Entries().All(e => !e.IsEnabled) && !Ticked(rowA) && !Ticked(rowB) && Loads() == null && CopiesOff().SequenceEqual(offOne);
        var allOffSay = Say();
        await Tick(rowA, true);
        var aloneOn = Ticked(rowA) && !Ticked(rowB) && CopiesOff().Contains(keyB) && !CopiesOff().Contains(keyA) && Loads() == "l:SmmTestTwinA" && SwitchOn()
            && CopiesBroken() == null;
        var aloneSay = Say();
        History.Undo(); // the tick
        History.Undo(); // the last untick
        History.Undo(); // the first untick
        await Wait(menu, 0.3);
        Check("... the last ticked copy unticked: the switch off, nothing loading, copies_off kept; then a tick on the other copy: only it on, loading; undone, both ticked",
            allOff && aloneOn && Ticked(rowA) && Ticked(rowB) && Loads() == "l:SmmTestTwinB" && CopiesOff().SequenceEqual(bothOff),
            $"all off {allOff} ({allOffSay}); alone on {aloneOn} ({aloneSay}); now {Say()}");

        // Sharing: a row per copy, both enabled, the one that loads first (a list read by its first row on gets that one).
        // Read back by id (local copies have no link): both ticked.
        var rows = Profiles.ShareRows(Profiles.Active ?? active).Where(r => r.Id == TwinId).ToList(); // an undo makes the profiles anew
        var readBack = Profiles.PlanImport([.. rows.Select(r => new ProfileCsv.Entry(r.Id, r.Enabled, r.Name, ""))]);
        Check("... sharing with both ticked: a row per copy, both enabled, the one that loads first; read back, both stay ticked",
            rows.Count == 2 && rows.All(r => r.Enabled) && rows[0].Version == b.manifest?.version
            && readBack.Mods.SingleOrDefault(m => m.Id == TwinId) is { Enabled: true, Copy: null, CopiesOff: [] } && readBack is { On: 0, Off: 0, Same: 2 },
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
        Check("... put back: A off again (copies_off as the page had it), B loading, nothing changed", !Ticked(rowA) && Ticked(rowB) && Loads() == "l:SmmTestTwinB"
            && CopiesOff().SequenceEqual(offWere) && FileCopiesOff().SequenceEqual(offWere) && TwinChanges().Count == 0 && !Profiles.Edited && !ModEntry.HasPendingChanges()
            && CopiesBroken() == null, Say());
    }

    // The one rule on the page (Copies.Next), so no two views can disagree: the fixture twins in each state that decides
    // differently, every view naming the copy Next loads and giving the others its reason. The views: Next itself, the
    // statuses, the copy lines (the other copy and the reason), the Duplicate chips' tips, the same-ID banner's card, the
    // relaunch check, Changes (a switch of which copy loads with no box changed), pending (the main menu's dot) and the
    // row strips (Test Twin User's line naming the copy it needs that loads, #aside on the copy that won't). Each state
    // made quietly, as a launch would have had the boxes (nothing changed since launch but which copy loads), then put
    // back. The states: both ticked (B, the higher version); A alone (A, after a relaunch); equal versions (A, the copy
    // installed first, our record's first, whatever the game's list order: P4); A's version unreadable (B: P5); A's
    // "1.2", read leniently above B's 1.1.0 but not by the game, while Test Twin User needs 1.1.0 or newer (B: the N4
    // guard; without it A); the self-check finding the choice not taken (the game's own choice, B, every line saying so);
    // the next launch unable to swap B in (A after a relaunch, both lines naming what B needs: P7). Screenshots: the panel
    // of the copy that won't load, with the higher version loading; the banner's card; a tie; an unreadable version; the
    // blocked copy.
    static async Task TestCopyOutcome(Node menu, NModdingScreen screen, Control content, string outDir)
    {
        List<NModMenuRow> Rows() => content.GetChildren().OfType<NModMenuRow>().ToList();
        NModMenuRow? RowOf(Mod mod) => Rows().FirstOrDefault(r => r.Mod == mod);
        if (Layout.Current != Layout.View.Categories || Profiles.Active == null)
        {
            Check("the one rule on the page: on the main list, a profile active", false, $"{Layout.Current}, {Profiles.Active?.Name}");
            return;
        }
        var twins = Rows().Select(r => r.Mod).OfType<Mod>().Where(m => m.manifest?.id == TwinId).ToList();
        if (twins.FirstOrDefault(m => Copies.CopyOf(m) == "l:SmmTestTwinA") is not { } a || twins.FirstOrDefault(m => Copies.CopyOf(m) == "l:SmmTestTwinB") is not { } b)
        {
            TwinsMissing("the one rule on the page");
            return;
        }
        if (Profiles.Edited)
            Profiles.Save(); // start clean
        var border = screen.GetNode<Control>("ModsBorder");
        Control? SameId() => border.GetNodeOrNull<Control>(LoadOrder.SameIdName);
        List<string> TwinChanges() => HistoryBar.Describe().Where(l => l.Contains(Copies.Named(a)) || l.Contains(Copies.Named(b))).ToList();
        async Task<string> RelaunchBody()
        {
            RelaunchCheck.Show(screen);
            await Wait(menu, 0.3);
            var body = (menu.GetTree().Root.GetNodeOrNull(RelaunchCheck.Name)?.FindChild("Body", true, false) as RichTextLabel)?.GetParsedText() ?? "";
            RelaunchCheck.Cancel();
            await Wait(menu, 0.3);
            return body;
        }
        // The twins' copies off (none: both ticked), as if the launch had them so: in every undo snapshot and the active
        // profile's entry, so no box changed since launch and nothing is unsaved.
        static void OffQuietly(params string[] off) => History.Quietly(data =>
        {
            data.CopiesOff = Store.TidyOff((data.CopiesOff ?? []).Where(k => Copies.IdOf(k) != TwinId).Concat(off.Select(c => $"{TwinId}|{c}")));
            if (data.Profiles.FirstOrDefault(p => p.Name == data.ActiveProfile) is { } active)
                foreach (var list in new[] { active.Mods, active.Edited })
                    for (var i = 0; i < (list?.Count ?? 0); i++)
                        if (list?[i] is { Id: TwinId, Source: ModSource.ModsDirectory } entry)
                            list[i] = entry with { CopiesOff = [.. off] };
        });
        void SetA(string? version)
        {
            if (a.manifest is { } manifest)
                manifest.version = version;
        }
        string Named(Mod mod) => Copies.Named(mod);
        var user = Dependencies.Find("SmmTestTwinUser"); // needs the id: its row strip names the copy that loads
        // What every view says now, against what the rule should give: loads, the copy that loads; losing, a ticked copy
        // that won't, and its line; loadsLine, the line of the copy that loads (null: none to check); switched, whether
        // which copy loads changed since launch with no box changed (Changes and pending say so). shot: the panel of the
        // copy that won't load (or of the copy that loads, with none); cardShot: the banner's card.
        async Task State(string name, Mod loads, Mod? losing, string? losingLine, string? loadsLine, bool switched, string? shot = null, string? cardShot = null)
        {
            Profiles.RefreshTickboxes(screen);
            CategoryList.Redraw(content);
            await Wait(menu, 0.3);
            // The row for the screenshots first, so the banner's card shows over its panel too.
            var shownRow = shot == null ? null : RowOf(losing ?? loads);
            if (shownRow != null)
            {
                CategoryList.Select(content, shownRow);
                await PanelFor(menu, screen, shownRow);
                CategoryList.ScrollTo(content, shownRow);
                await Wait(menu, 0.2);
            }
            var other = loads == a ? b : a;
            var clash = Copies.Ticked(a) && Copies.Ticked(b);
            string? Line(Mod mod) => ModDetails.CopyLine(mod, false)?.Text;
            var chips = new[] { a, b }.Select(m => Tags.For(m).FirstOrDefault(c => c.Text == DuplicateChip)?.Tip ?? "").ToList();
            var card = clash && Layout.Wide ? await BannerCard(menu, screen, SameId(), cardShot, outDir) : [];
            var body = await RelaunchBody();
            var changes = TwinChanges();
            var switchLine = Draft(CopySwitchLine, ("mod", a.manifest?.name ?? ""), ("to", Named(loads)), ("from", Named(b)));
            var wrong = new List<string>();
            void Want(bool ok, string what)
            {
                if (!ok)
                    wrong.Add(what);
            }
            Want(LoadingCopy(TwinId) == Copies.CopyOf(loads), $"Next loads {LoadingCopy(TwinId) ?? "none"}");
            Want(!ModDetails.Status(loads).Contains(StatusWontLoad), $"its status {ModDetails.Status(loads)}");
            if (loadsLine != null)
                Want(Line(loads) == loadsLine, $"its line \"{Line(loads)}\"");
            if (losing != null)
            {
                Want(ModDetails.Status(losing).Contains(StatusWontLoad), $"the other's status {ModDetails.Status(losing)}");
                Want(Line(losing) == losingLine, $"the other's line \"{Line(losing)}\"");
                Want(body.Contains($"{Named(losing)}  {Draft(RelaunchLoses, ("other", Named(loads)))}"), $"the relaunch check: {body}");
            }
            else
                Want(!body.Contains(Draft(RelaunchLoses, ("other", Named(loads)))), $"the relaunch check names a copy that won't load: {body}");
            if (clash)
            {
                Want(chips[0] == Draft(ClashTip, ("others", Named(b)), ("loads", Named(loads))) && chips[1] == Draft(ClashTip, ("others", Named(a)), ("loads", Named(loads))),
                    $"the chips \"{string.Join("\" / \"", chips)}\"");
                if (Layout.Wide)
                    Want(card.Contains(Draft(BannerLoads, ("mod", Named(loads)))) && card.Contains(Draft(BannerWontLoad, ("mod", Named(other)))),
                        $"the banner's card: {string.Join(" | ", card)}");
            }
            Want(changes.Contains(switchLine) == switched, $"Changes: {string.Join(" | ", changes)}");
            Want(ModEntry.HasPendingChanges() == (switched || LoadOrder.OrderChanged), $"pending {ModEntry.HasPendingChanges()} (moved since launch {LoadOrder.OrderChanged})");
            // The row strip: Test Twin User's line names the copy of the id it needs that loads (its name alone, then a word
            // where there's a problem), and only the copy that won't load has the aside keyword (the info panel's Other keywords).
            var note = (user == null ? null : RowOf(user))?.GetNodeOrNull<RichTextLabel>("SpireModManagerStrip/Needs")?.GetParsedText();
            var needs = $"{Menus.Text("SPIRE_MOD_MANAGER.NEEDS_LABEL")}: {loads.manifest?.name}";
            var words = new[] { "SPIRE_MOD_MANAGER.DEP_MISSING", "SPIRE_MOD_MANAGER.DEP_OFF", "SPIRE_MOD_MANAGER.DEP_AFTER" }.Select(Menus.Text);
            Want(user == null || note == needs || words.Any(w => note == $"{needs} {w}"),
                $"Test Twin User's row strip \"{note ?? "none"}\"");
            Want(new[] { a, b }.All(m => HiddenTags.Of(m).Contains("aside") == (m == losing)),
                $"the copies' keywords {string.Join(" / ", new[] { a, b }.Select(m => string.Join(",", HiddenTags.Of(m))))}");
            if (shot != null && shownRow != null)
                await Capture(screen, outDir, shot);
            Check($"the one rule on the page, {name}: every view names {Named(loads)} as the copy that loads"
                + (losing == null ? "" : $", and says why {Named(losing)} won't"), wrong.Count == 0, string.Join("; ", wrong));
        }

        var offWere = CopiesOff();
        var heldOff = offWere.Where(k => Copies.IdOf(k) == TwinId).Select(k => k[(k.IndexOf('|') + 1)..]).ToArray();
        var (versionWas, aState, bState) = (a.manifest?.version, a.state, b.state);
        var planWas = SameIdLoad.Plan.GetValueOrDefault(TwinId);
        var needed = user != null && Copies.Ticked(user);
        try
        {
            OffQuietly();
            await State("both ticked", b, a, Draft(CopyLoses, ("other", Named(b)), ("why", WhyHigher)), Draft(LoadsAlso, ("other", Named(a))), false,
                "20a-rule-higher", "20c-rule-banner-card");
            OffQuietly("l:SmmTestTwinB");
            await State("A alone ticked", a, null, null, Draft(LoadsAfter, ("other", Named(b))), true);
            OffQuietly();
            SetA(b.manifest?.version);
            await State("equal versions (P4): the copy installed first, our record's first", a, b,
                Draft(CopyLoses, ("other", Named(a)), ("why", WhyFirst)), Draft(LoadsAfter, ("other", Named(b))), true, "20b-rule-tie");
            // The next launch's order the other way round (the plan's Order, which the page reads the copies in): a tie still
            // goes by our record, not by it.
            var orderWas = SameIdLoad.Plan.GetValueOrDefault(TwinId);
            if (orderWas != null)
                SameIdLoad.TestPlan(TwinId, orderWas with { Order = ["l:SmmTestTwinB", "l:SmmTestTwinA"] });
            Copies.TestRecount();
            var reversed = LoadingCopy(TwinId);
            SameIdLoad.TestPlan(TwinId, orderWas);
            Copies.TestRecount();
            Check("... equal versions with the next launch's order the other way round: the same copy, our record's first", orderWas != null && reversed == "l:SmmTestTwinA",
                $"{reversed ?? "none"}; a plan {orderWas != null}");
            SetA("abc");
            await State("A's version unreadable (P5)", b, a, Draft(CopyLoses, ("other", Named(b)), ("why", WhyUnreadable)),
                Draft(LoadsAlso, ("other", Named(a))), false, "20d-rule-unreadable");
            SetA("1.2");
            if (needed)
                await State("A's \"1.2\" read leniently above B, while Test Twin User needs 1.1.0 or newer: the copy the game can read (N4)", b, a,
                    Draft(CopyLoses, ("other", Named(b)), ("why", WhyUnreadable)), Draft(LoadsAlso, ("other", Named(a))), false);
            else
                await State("A's \"1.2\" read leniently above B (nothing needs a minimum version)", a, b, Draft(CopyLoses, ("other", Named(a)), ("why", WhyHigher)),
                    Draft(LoadsAfter, ("other", Named(b))), true);
            SetA(versionWas);
            SameIdLoad.TestResult(TwinId, SameIdLoad.Result.NotTaken);
            await State("the self-check finding the choice not taken: the game's own choice", b, a, NotApplied, NotApplied, false);
            SameIdLoad.TestResult(TwinId, null);
            // The next launch can't swap B in (it needs Test Twin Late too, made up: installed while the game runs, with no
            // saved entry yet, so that launch sorts it after every listed mod) while Test Twin User needs the id: A, the
            // fallback, after a relaunch in B's place, both lines naming Test Twin Late. Fails on bed319a: its page took a
            // block from this launch's plan alone (here none), so B. B needs Test Framework as well: on and listed after the
            // twins, the next launch sorts it after the slot too, and the lines name it instead.
            var listed = SaveManager.Instance.SettingsSave.ModSettings?.ModList ?? [];
            var libAfter = !Dependencies.SavedOff().Contains(("SmmTestLib", ModSource.ModsDirectory))
                && listed.FindLastIndex(e => e.Id == "SmmTestLib") is var lib && lib >= 0 && lib >= listed.FindLastIndex(e => e.Id == TwinId);
            if (!needed || libAfter)
                Results.Add($"SKIP the one rule on the page, the copy the next launch can't swap in: Test Twin User on {needed}, Test Framework on and listed after the twins {libAfter}");
            else
            {
                var late = new Mod
                {
                    path = "C:/SmmSameIdTest/TwinLate", modSource = ModSource.ModsDirectory, state = ModLoadState.AddedAtRuntime,
                    manifest = new ModManifest { id = "SmmTestTwinLate", name = "Test Twin Late", version = "1.0.0" },
                };
                var needsWere = b.manifest?.dependencies;
                GameMods().Add(late);
                if (b.manifest is { } manifest)
                    manifest.dependencies = [new ModDependency("SmmTestLib"), new ModDependency("SmmTestTwinLate")];
                Copies.TestRecount();
                try
                {
                    await State("the copy the next launch can't swap in (P7): the fallback, after a relaunch", a, b,
                        Draft(CopyBlocked, ("other", Named(a)), ("dep", Named(late))), Draft(LoadsBlocked, ("other", Named(b)), ("dep", Named(late))), true,
                        "20e-rule-blocked");
                }
                finally
                {
                    if (b.manifest is { } restored)
                        restored.dependencies = needsWere;
                    GameMods().Remove(late);
                    Copies.TestRecount();
                }
            }
        }
        finally
        {
            SetA(versionWas);
            (a.state, b.state) = (aState, bState);
            SameIdLoad.TestPlan(TwinId, planWas);
            SameIdLoad.TestResult(TwinId, null);
            OffQuietly(heldOff);
            Profiles.RefreshTickboxes(screen);
            CategoryList.Redraw(content);
            await Wait(menu, 0.3);
        }
        Check("... put back: the twins as the page had them, B loading, nothing changed or unsaved", CopiesOff().SequenceEqual(offWere) && LoadingCopy(TwinId) == "l:SmmTestTwinB"
            && TwinChanges().Count == 0 && !Profiles.Edited && CopiesBroken() == null, $"off {string.Join(",", CopiesOff())}; loads {LoadingCopy(TwinId)}; "
            + $"changes {string.Join(" | ", TwinChanges())}; edited {Profiles.Edited}; {CopiesBroken() ?? "kept"}");
    }

    // The beta's edge where a copy that's off would load (P7, P17): W1 ticked (1.0.0), W2 not (3.0.0) but on W1's switch,
    // and a local copy L ticked (2.0.0). The game sees W2 on through the switch they share: W2 beats L, W1 loses to L,
    // and only W2 is left. A copy that's off doesn't count (Jordan, 23 Oct), so the launch brings L back: the rule as it
    // goes without W2. Made-up copies of a made-up id, as such a launch has them (L loaded, W1 and W2 set aside): L the
    // copy that loads, naming W1; W1 that L loads instead, the higher version, and Duplicate - Won't load; W2 that L is the
    // enabled copy; the same-ID banner shows (W1 and L ticked). Then as a launch before this had them (W2 loaded): L loads
    // after a relaunch in W2's place. Stable has no such edge (every Workshop copy beside a local one is out): a SKIP there.
    static async Task TestCopyUnticked(Node menu, NModdingScreen screen, Control content)
    {
        if (!Compat.WorkshopBeatsOlderLocal)
        {
            Results.Add("SKIP a copy that's off doesn't count: only the beta's rule has the edge (stable takes out every Workshop copy beside a local one)");
            return;
        }
        const string id = "SmmUnticked";
        var w1 = Made(id, "W101", ModLoadState.DisabledDuplicate, item: 101, version: "1.0.0");
        var w2 = Made(id, "W102", ModLoadState.DisabledDuplicate, item: 102, version: "3.0.0");
        var l = Made(id, "SmmUntickedL", ModLoadState.Loaded, version: "2.0.0");
        GameMods().AddRange([w1, w2, l]);
        Copies.TestRecount();
        History.Quietly(data =>
        {
            (data.Copies ??= [])[id] = ["w:101", "w:102", "l:SmmUntickedL"];
            data.CopiesOff = Store.TidyOff([.. data.CopiesOff ?? [], $"{id}|w:102"]);
        });
        try
        {
            CategoryList.RefreshStrips(content);
            await Wait(menu, 0.3);
            string? Line(Mod mod) => ModDetails.CopyLine(mod, false)?.Text;
            var banner = screen.GetNode<Control>("ModsBorder").GetNodeOrNull<Control>(LoadOrder.SameIdName);
            Check("a copy that's off doesn't count (the beta: W1 ticked 1.0.0, W2 not, 3.0.0, on W1's switch, L ticked 2.0.0): L loads, as the rule goes "
                + "without W2, naming W1; W1: L loads instead, the higher version, Duplicate - Won't load; W2: L is the enabled copy; the same-ID banner",
                LoadingCopy(id) == "l:SmmUntickedL" && !Copies.Ticked(w2) && Copies.Ticked(w1) && Copies.Ticked(l)
                && Line(l) == Draft(LoadsAlso, ("other", Copies.Named(w1)))
                && ModDetails.Status(w1).Contains(StatusWontLoad) && Line(w1) == Draft(CopyLoses, ("other", Copies.Named(l)), ("why", WhyHigher))
                && Line(w2) == Draft(OtherEnabled, ("other", Copies.Named(l)))
                && (!Layout.Wide || banner != null),
                $"loads {LoadingCopy(id)}; ticked W1 {Copies.Ticked(w1)} W2 {Copies.Ticked(w2)} L {Copies.Ticked(l)}; statuses {ModDetails.Status(w1)} / "
                + $"{ModDetails.Status(w2)} / {ModDetails.Status(l)}; lines {Line(w1)} | {Line(w2)} | {Line(l)}; banner {banner != null}");
            // The first session after an update to this: the launch before it loaded W2. L loads after a relaunch, in its place.
            (w2.state, l.state) = (ModLoadState.Loaded, ModLoadState.DisabledDuplicate);
            Copies.TestRecount();
            CategoryList.RefreshStrips(content);
            await Wait(menu, 0.2);
            Check("... with W2 loaded at launch (a launch before this): L loads after a relaunch, in W2's place; W1 still loses to L",
                LoadingCopy(id) == "l:SmmUntickedL" && Line(l) == Draft(LoadsAfter, ("other", Copies.Named(w2)))
                && Line(w1) == Draft(CopyLoses, ("other", Copies.Named(l)), ("why", WhyHigher)),
                $"loads {LoadingCopy(id)}; lines {Line(w1)} | {Line(w2)} | {Line(l)}");
        }
        finally
        {
            foreach (var made in new[] { w1, w2, l })
                GameMods().Remove(made);
            Copies.TestRecount();
            History.Quietly(data =>
            {
                data.Copies?.Remove(id);
                data.CopiesOff = data.CopiesOff?.Where(k => Copies.IdOf(k) != id).ToList();
            });
            CategoryList.RefreshStrips(content);
            await Wait(menu, 0.2);
        }
    }

    // The fixture twins (one id and source) in Load Order: which copy takes which of their entries is part of the order as
    // undo knows it. Side by side, a drop of the second above the first saves the same list: one undo step, and no move
    // for Changes (the game reads the same list; which copy loads goes by the copies, not their places), no "was" line, nothing pending; undo and
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
}
