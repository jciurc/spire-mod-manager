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

    // Whether a popup has a new copy's words (NewModPopupPatch), whichever mods they name: each text's longest run
    // between its values.
    static bool NewCopyWords(string popup) => new[] { "SPIRE_MOD_MANAGER.NEW_COPY_ON_BODY", "SPIRE_MOD_MANAGER.NEW_COPIES_ON_BODY",
            "SPIRE_MOD_MANAGER.NEW_COPY_OFF_BODY", "SPIRE_MOD_MANAGER.NEW_SELF_COPY_BODY" }
        .Select(key => Template(key).Split('{', '}').Where((_, i) => i % 2 == 0).MaxBy(s => s.Length) ?? "")
        .Any(words => words.Length > 0 && popup.Contains(words));

    // The fixture twins both ticked at launch (BothTickedQuietly: as a launch would have them, A set aside, B loaded): B,
    // the pick, loads and A won't, with no duplicate error and nothing pending or changed; A's title white, not the
    // game's grey for a copy set aside; its status and panel say B loads instead, one copy of a source loading; both chips
    // yellow, naming the copy that loads, and both copy lines in that yellow (A off beside B: its own colour); the banner
    // and its card; the relaunch check, gone with the banner's Dismiss;
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
        // The copy lines' colour: A off beside B isn't in a clash (its own dim colour, B none); both ticked, each in the
        // Duplicate chip's warning yellow, B (the copy that loads) too.
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
        Check("same ID, the copy lines: A off beside B in its own colour (not the clash's), B none; both ticked, each in the Duplicate chip's warning yellow, "
            + "the copy that loads too", offColor != null && offColor != clash && onAlone == null && lineColors == (clash, clash),
            $"off {offColor}, B alone {onAlone}; both {lineColors.A} / {lineColors.B} (yellow {clash})");
        var bannerText = (SameId()?.FindChild("SameIdWarning", true, false) as Label)?.Text;
        var card = await BannerCard(menu, screen, SameId());
        Check("same ID, both copies ticked at launch: the pick (B) loads and A won't, no duplicate error, nothing pending or changed; A's title white, not the "
            + "game's grey for a copy set aside; A reads Won't load, B loading in its place (one copy of a source loads); both Duplicate chips yellow (B's, "
            + "the copy that loads, too), naming the copy that loads; the banner and its card say which loads; Find takes the copy that loads",
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
}
