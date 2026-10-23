using System.Text.Json.Nodes;
using Godot;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;
using MegaCrit.Sts2.Core.Saves;

namespace SpireModManager;

// The self-test's checks of the main list's banners: needed mods off, this mod among the others, other mod managers.
static partial class SelfTest
{
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
        // Its Go to steps through both sides (the mods that won't load, the disabled one they need) as the list shows them,
        // from the row below the mod in the panel and round to the top: categories top to bottom, each one's rows top to
        // bottom, a folded one's where they sit (Go to opens it). Test UI in the first category and Test Content in the
        // second put the one that sorts later first. Each press finds the banner again: opening a category makes a new one.
        var folds = (Each: Store.Data.Categories.Select(c => c.Collapsed).ToList(), Other: Store.Data.UncategorizedCollapsed);
        var filed = (Ui: Store.InfoFor("SmmTestUi")?.Category, Content: Store.InfoFor("SmmTestContent")?.Category);
        if (Store.Data.Categories is [var first, var second, ..])
            Store.Batch(() =>
            {
                Store.EditMod("SmmTestUi", info => info.Category = first.Name);
                Store.EditMod("SmmTestContent", info => info.Category = second.Name);
            });
        foreach (var each in Store.Data.Categories)
            each.Collapsed = true;
        Store.Data.UncategorizedCollapsed = true;
        CategoryList.Apply(content);
        if (Row(ModEntry.ModId) is { } notAbout)
            CategoryList.Select(screen, notAbout);
        await Wait(menu, 0.2);
        var placed = Rows();
        int At(Mod? mod) => placed.FindIndex(r => r.Mod == mod);
        var (ours, uiAt, contentAt) = (At(Row(ModEntry.ModId)?.Mod), At(Row("SmmTestUi")?.Mod), At(dependent));
        var steps = new List<Mod?>();
        var opened = true;
        while (steps.Count < 12 && (steps.Count < 2 || steps[^1] != steps[0]))
        {
            screen.FindChild(LoadOrder.WarningName, true, false)?.FindChild("GoToProblemButton", true, false)?.EmitSignal(BaseButton.SignalName.Pressed);
            await Wait(menu, 0.3);
            steps.Add(CategoryList.Selected);
            opened &= Rows().FirstOrDefault(r => r.Mod == CategoryList.Selected) is { Visible: true };
        }
        var cycle = steps.Take(steps.Count - 1).ToList(); // the last press back at the first
        var stepped = cycle.Select(m => m?.manifest?.id ?? "").ToList();
        for (var i = 0; i < folds.Each.Count && i < Store.Data.Categories.Count; i++)
            Store.Data.Categories[i].Collapsed = folds.Each[i];
        Store.Data.UncategorizedCollapsed = folds.Other;
        Store.Batch(() =>
        {
            Store.EditMod("SmmTestUi", info => info.Category = filed.Ui);
            Store.EditMod("SmmTestContent", info => info.Category = filed.Content);
        });
        CategoryList.Apply(content);
        await Wait(menu, 0.3);
        Check("main list banner: Go to steps through the mods that won't load and the disabled one they need in the rows' order on screen, from the row "
            + "below the panel's mod and round, each once, folded categories opened; Test UI (first category) before Test Content (second), though it sorts after",
            steps.Count > 1 && steps[^1] == steps[0] && cycle.Distinct().Count() == cycle.Count && opened
            && cycle.SequenceEqual(cycle.OrderBy(m => At(m) > ours ? 0 : 1).ThenBy(At))
            && new[] { "SmmTestLib", "SmmTestContent", "SmmTestUi" }.All(stepped.Contains) && uiAt < contentAt,
            $"{string.Join(", ", cycle.Select(m => $"{m?.manifest?.id} ({At(m)})"))}; ours at {ours}; opened {opened}; {Store.Data.Categories.Count} categories");

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
    // the panel says which copy runs, and there's no same-ID warning for it; a Disable all that covers it says it stays
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
        // red banner. The panel says which copy runs and that the other stays on; no same-ID banner or yellow chip (both on
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
        // grey "Duplicate - Won't load" all the same, not a red "Failed to load" (it's kept on by design, and won't load).
        var (stateWas, errorsWas, backupStatus) = (copy.state, copy.errors, ModDetails.Status(copy));
        copy.state = ModLoadState.Failed;
        copy.errors = [new LocString("main_menu_ui", "MOD_ERROR.DUPLICATE_ID")];
        var failedStatus = ModDetails.Status(copy);
        (copy.state, copy.errors) = (stateWas, errorsWas);
        Check("... the backup copy failed by the game as a duplicate (made up): grey Duplicate - Won't load as before, not a red Failed to load",
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
        var ourChips = new[] { self, copy }.Select(m => Tags.For(m).FirstOrDefault(c => c.Text == DuplicateChip)).ToList();
        Check("... no same-ID banner or yellow Duplicate chip for this mod: both on is as it should be",
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
            !Copies.Ticked(self) && first && text == "Spire Mod Manager (this mod) will be disabled on relaunch, and this menu will no longer be accessible until enabled again"
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
            && text == "Multiple mod managers are currently enabled, and may cause unexpected behavior", $"\"{text}\"; {Where()}");
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
        // From our row, first in load order: Go to's next stop below it is Better Mod Menu's (the list's order).
        if (Rows().FirstOrDefault(r => r.Mod?.manifest?.id == ModEntry.ModId) is { } ourRow)
            CategoryList.Select(screen, ourRow);
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

    // Everything a banner says: its text, its buttons' words and their tips.
    static List<string> BannerWords(Node? banner) => banner?.FindChildren("*", "", true, false)
        .SelectMany(n => n switch { Label label => [label.Text], Button button => new[] { button.Text, Hover.TipOf(button) }, _ => [] }).ToList() ?? [];
}
