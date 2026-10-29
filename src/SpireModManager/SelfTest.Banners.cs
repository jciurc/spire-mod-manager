using System.Text.Json.Nodes;
using Godot;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;
using MegaCrit.Sts2.Core.Platform;
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
        // Test Addon on as well, its dependency not installed: both kinds at once, each a statement of its own.
        if (WontLoad() != null)
            Check("main list banner: both kinds of problem, a dot between them and no full stops",
                text.Split("  ·  ") is [var off, var missing] && off.Contains("require a mod that is disabled") && missing.Contains("a mod that isn't installed")
                && !text.Contains('.'), text);
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
        var orderWas = LoadOrder.Mods(Rows());
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
        // The order as it was: the fix leaves the library moved, and the tests after it start with nothing moved since
        // launch (a Revert all's undo, from either view, brings a move back with the rest).
        for (var i = 0; i < orderWas.Count; i++)
            LoadOrder.Move(orderWas[i], i, Rows());
        CategoryList.Redraw(content);
        await Wait(menu, 0.3);
        Check("... put back: the order as before the move, no move since launch", LoadOrder.Mods(Rows()).SequenceEqual(orderWas) && !LoadOrder.OrderChanged,
            string.Join(", ", HistoryBar.Moves().Select(m => $"{m.Mod.manifest?.id} {m.Before}→{m.After}")));

        // An enabled mod the game failed to load (Test Fixes, needing nothing, marked failed for the row): the banner says
        // so, its own part (Jordan, 25 Oct: "we have no error banner when an enabled mod fails loading"); its Show finds it
        // (#failed), and its Go to stops at it.
        if (Row("SmmTestFixes") is { Mod: { } failing } && Copies.Ticked(failing))
        {
            var stateWas = failing.state;
            failing.state = ModLoadState.Failed;
            try
            {
                CategoryList.Redraw(content);
                await Wait(menu, 0.3);
                var failedText = (screen.FindChild("OrderProblems", true, false) as Label)?.Text ?? "";
                screen.FindChild(LoadOrder.WarningName, true, false)?.FindChild("FilterProblemButton", true, false)?.EmitSignal(BaseButton.SignalName.Pressed);
                await Wait(menu, 0.3);
                var (failedSearch, fixesShown) = (searchBox.Text, Row("SmmTestFixes")?.Visible == true);
                screen.FindChild(LoadOrder.WarningName, true, false)?.FindChild("FilterProblemButton", true, false)?.EmitSignal(BaseButton.SignalName.Pressed);
                await Wait(menu, 0.3);
                if (Row(ModEntry.ModId) is { } before)
                    CategoryList.Select(screen, before);
                var stopsAt = new List<string>();
                for (var i = 0; i < 8; i++)
                {
                    screen.FindChild(LoadOrder.WarningName, true, false)?.FindChild("GoToProblemButton", true, false)?.EmitSignal(BaseButton.SignalName.Pressed);
                    await Wait(menu, 0.2);
                    stopsAt.Add(CategoryList.Selected?.manifest?.id ?? "");
                }
                Check("main list banner: an enabled mod that failed to load has its own part, \"1 enabled mod failed to load\"; its Show finds it (#failed), "
                    + "its Go to stops at it", failedText.Split("  ·  ").Contains("1 enabled mod failed to load") && failedSearch.Contains("#failed") && fixesShown
                    && stopsAt.Contains("SmmTestFixes"), $"\"{failedText}\"; Show \"{failedSearch}\", shown {fixesShown}; Go to {string.Join(", ", stopsAt)}");
            }
            finally
            {
                failing.state = stateWas;
                searchBox.Text = "";
                searchBox.EmitSignal(LineEdit.SignalName.TextChanged, "");
                CategoryList.Redraw(content);
                await Wait(menu, 0.3);
            }
            var failedGone = (screen.FindChild("OrderProblems", true, false) as Label)?.Text ?? "";
            Check("... loaded again: that part goes", !failedGone.Contains("failed to load"), failedGone);
        }
        else
            Results.Add("SKIP main list banner, a mod that failed to load: Test Fixes not installed, or off");

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
    // This mod among the others: the counts count it. Installed twice (a made-up Workshop copy beside this one), its
    // copies are boxes like any other's (Jordan), first in their category whatever the names, and get any copies' words
    // (Jordan, 23 Oct): the new copy's popup; both ticked, the copy lines, statuses and Duplicate chips of a clash, the
    // same-ID banner counting them, #clash, and Relaunch asking first; a click on one unticks its source alone, one undo
    // step, with the lines and the tip of a copy off beside one on; between sources, the game's rule and its reason
    // (P10); from one source, the game's pick with no reason. Disable all keeps both on; a selection that has it turns it
    // off (the page bar's Disable selected, a picked row's box, the bulk menu). Every copy off: the red banner, whose
    // Enable now turns on the copy running alone (P16), and Relaunch asks first, with the same Enable now; Cancel undoes
    // it. A Disable all that covers it says it stays on; and installed once and unticked on its own row, the red banner
    // above everything says it (and this page) goes at the relaunch, with Go to (its row) and Enable now. Enable now, and
    // undo, take the banner away.
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
        // counts name it once (#96: they counted each copy). The popup is any new copy's.
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
        Check("installed twice: both copies on, every count names it once; the new copy's popup is any copy's, saying it's enabled and which loads",
            Copies.Ticked(copy) && Profiles.OnCount() == on && ProfileCountShown() && Summary().StartsWith($"Mods enabled {Menus.Number(on)} ")
            && popup.Contains(Draft(NewCopyOn, ("mods", Named(copy)), ("loads", Named(self)))),
            $"copy on {Copies.Ticked(copy)}; {on} on before: profile {Profiles.OnCount()}, summary \"{Summary().Split('\n')[0]}\"; {popup}");
        // Boxes like any other's: enabled, no padlock (the one there was); ticked, so no tip (the panel says what each does).
        var ourRows = content.GetChildren().OfType<NModMenuRow>().Where(r => r.Mod?.manifest?.id == ModEntry.ModId).ToList();
        static NTickbox Tickbox(NModMenuRow r) => r.GetNode<NTickbox>("Tickbox");
        static string Tip(NModMenuRow r) => Hover.TipOf(Tickbox(r));
        string Box(NModMenuRow r) => $"{Copies.KeyOf(r.Mod)} ticked {Tickbox(r).IsTicked}, enabled {Tickbox(r).IsEnabled}, tip \"{Tip(r)}\", "
            + $"padlock {r.GetNodeOrNull("SpireModManagerSelfLock") != null}";
        bool Plain(NModMenuRow r) => Tickbox(r).IsEnabled && r.GetNodeOrNull("SpireModManagerSelfLock") == null;
        Check("installed twice: no padlock, a box each like any other's (enabled; ticked, so no tip), both ticked",
            ourRows.Count == 2 && ourRows.All(r => Plain(r) && Tickbox(r).IsTicked && Tip(r) == ""), string.Join(" | ", ourRows.Select(Box)));
        // First in its category, whatever the names: here a row whose name sorts before it comes after it.
        var nodes = content.GetChildren().OfType<Control>().ToList();
        var headerAt = nodes.Take(nodes.IndexOf(row)).ToList().FindLastIndex(n => n.IsInGroup("SpireModManagerCategoryHeader"));
        var section = nodes.Skip(headerAt + 1).TakeWhile(n => n is NModMenuRow).OfType<NModMenuRow>().ToList();
        var names = StringComparer.Create(Menus.Culture, ignoreCase: true);
        string Shown(NModMenuRow r) => Store.InfoFor(r.Mod)?.Alias ?? r.Mod?.manifest?.name ?? r.Mod?.manifest?.id ?? "";
        var sortsBefore = section.Skip(ourRows.Count).Where(r => names.Compare(Shown(r), Shown(row)) < 0).Select(Shown).ToList();
        Check("this mod first in its category whatever the names: both copies of it, then the rest by name (one before it by name here)",
            headerAt >= 0 && section.Take(ourRows.Count).All(r => r.Mod?.manifest?.id == ModEntry.ModId) && sortsBefore.Count > 0,
            $"section {string.Join(", ", section.Select(Shown))}; sorting before it {string.Join(", ", sortsBefore)}");
        CategoryList.ScrollTo(content, ourRows[0]); // our rows in view for the screenshot
        await Wait(menu, 0.2);
        await Capture(screen, outDir, "2q-self-twice-first");
        // Both ticked: a clash, as any mod's copies are (Jordan, 23 Oct). Which loads is the game's rule between sources,
        // this local copy here (on the beta as the higher version, on stable as the local copy), and the lines say so with
        // its reason; every ticked copy's line and Duplicate chip is in the clash's yellow, the copy that won't load reads
        // Duplicate - Won't load in orange, the same-ID banner counts both and its card groups them under the ID (this one
        // Active, then the other Ignored), and #clash finds both. No line of its own.
        var clashHex = $"#{Tags.SameIdClash.ToHtml(false)}";
        var why = Compat.WorkshopBeatsOlderLocal ? WhyHigher : WhyLocal;
        var (selfLine, copyLine) = (ModDetails.CopyLine(self, false), ModDetails.CopyLine(copy, false));
        var ourChips = new[] { self, copy }.Select(m => Tags.For(m).FirstOrDefault(c => c.Text == DuplicateChip)).ToList();
        var sameId = border.GetNodeOrNull<Control>(LoadOrder.SameIdName);
        var clashing = Copies.Clashing();
        // The banner's words and fill read before its card opens: a redraw can free the banner.
        var (sameIdText, sameIdYellow) = ((sameId?.FindChild("SameIdWarning", true, false) as Label)?.Text, Yellow(BannerFill(sameId)));
        var sameIdCard = await BannerCard(menu, screen, sameId);
        var findsClash = HiddenTags.Find("clash") is { } clashTag && clashTag(self) && clashTag(copy);
        Check("... both ticked, a clash like any copies': the lines any copies get (this one loads, the other loads instead, with the game's reason), both "
            + "in the clash's yellow, the other Duplicate - Won't load in orange; both Duplicate chips yellow, naming the other and the copy that loads; the "
            + "same-ID banner counting both, its card grouping them under the ID, this one Active, then the other Ignored; #clash finding both",
            selfLine?.Text == Draft(LoadsAlso, ("other", Named(copy))) && selfLine?.Color == clashHex
            && copyLine?.Text == Draft(CopyLoses, ("other", Named(self)), ("why", why)) && copyLine?.Color == clashHex
            && ModDetails.Status(copy) == $"[color=#f5b56b]{StatusWontLoad}[/color]" && !ModDetails.Status(self).Contains(StatusWontLoad)
            && ourChips[0] is { } selfChip && selfChip.Color == Tags.SameIdClash && selfChip.Tip == Draft(ClashTipOne, ("others", Named(copy)), ("loads", Named(self)))
            && ourChips[1] is { } copyChip && copyChip.Color == Tags.SameIdClash && copyChip.Tip == Draft(ClashTipOne, ("others", Named(self)), ("loads", Named(self)))
            && clashing.Contains(self) && clashing.Contains(copy) && sameIdText == Draft(SameIdText, ("count", Menus.Number(clashing.Count)))
            && sameIdYellow && GroupIn(sameIdCard, ModEntry.ModId).SequenceEqual(Grouped(ModEntry.ModId, (self, true), (copy, false))) && findsClash,
            $"lines {selfLine} / {copyLine}; status {ModDetails.Status(self)} / {ModDetails.Status(copy)}; chips {string.Join(" / ", ourChips.Select(c => $"{c?.Color} {c?.Tip}"))}; "
            + $"clashing {clashing.Count}, banner {sameIdText ?? "none"} ({string.Join(" | ", sameIdCard)}; expected "
            + $"{string.Join(" | ", Grouped(ModEntry.ModId, (self, true), (copy, false)))}); #clash {findsClash}");
        // The screenshot: the copy that won't load selected, its line in the clash's yellow, the chips and the banner above.
        // Then the row selected before, so the tests after start where they did.
        var selectedWas = CategoryList.Selected;
        if (ourRows.FirstOrDefault(r => r.Mod == copy) is { } shownRow)
        {
            CategoryList.Select(content, shownRow);
            await PanelFor(menu, screen, shownRow);
            CategoryList.ScrollTo(content, ourRows[0]);
            await Wait(menu, 0.2);
            await Capture(screen, outDir, "2r-self-twice-clash");
            if (content.GetChildren().OfType<NModMenuRow>().FirstOrDefault(r => r.Mod == selectedWas) is { } wasRow)
                CategoryList.Select(content, wasRow);
            await Wait(menu, 0.2);
        }
        // A click on one copy's box unticks that one alone (its source's entries off), one undo step; the other still loads,
        // so no red banner, and no clash. Its lines and tip are any copy's beside one that's on: the copy running has no
        // line, the other names the enabled copy, and its box which copy would load with it on too.
        bool SourceOn(ModSource source) => SaveManager.Instance.SettingsSave.ModSettings?.ModList.Where(e => e.Id == ModEntry.ModId && e.Source == source)
            .All(e => e.IsEnabled) ?? true;
        if (ourRows.FirstOrDefault(r => r.Mod == copy) is { } copyRow)
        {
            var (steps, movesWere) = (History.Steps(History.Track.List), HistoryBar.Moves().Count);
            Tickbox(copyRow).IsTicked = false;
            Tickbox(copyRow).EmitSignal(NTickbox.SignalName.Toggled, Tickbox(copyRow));
            await Wait(menu, 0.3);
            var offLines = (Running: ModDetails.CopyLine(self, false)?.Text, Other: ModDetails.CopyLine(copy, false)?.Text);
            var offTips = (Running: Tip(row), Other: Tip(copyRow));
            var alone = !Copies.Ticked(copy) && Copies.Ticked(self) && !SourceOn(ModSource.SteamWorkshop) && SourceOn(ModSource.ModsDirectory)
                && History.Steps(History.Track.List) == steps + 1 && Banner() == null && ourRows.All(Plain)
                && !Copies.Clashing().Any(m => m.manifest?.id == ModEntry.ModId);
            var aloneSay = $"{string.Join(" | ", ourRows.Select(Box))}; steps +{History.Steps(History.Track.List) - steps}; banner {Banner() != null}; "
                + $"lines {offLines.Running ?? "none"} / {offLines.Other}";
            // The entry made to turn it off (the game had none for a copy installed while it runs) goes with ours at the top.
            var entries = SaveManager.Instance.SettingsSave.ModSettings?.ModList ?? [];
            var oursLead = entries.TakeWhile(e => e.Id == ModEntry.ModId).Count() == entries.Count(e => e.Id == ModEntry.ModId)
                && entries.Any(e => e is { Id: ModEntry.ModId, Source: ModSource.SteamWorkshop, IsEnabled: false });
            var (movesNow, entriesSaid) = (HistoryBar.Moves().Count, string.Join(", ", entries.Select(e => $"{e.Id}/{e.Source}{(e.IsEnabled ? "" : " (off)")}")));
            History.Undo();
            await Wait(menu, 0.3);
            Check("... a click on one copy's box unticks it alone (its source's entries off), one undo step, no red banner (the other loads), no clash; "
                + "the lines and tip of any copy off beside one on: the copy running none, the other naming the enabled copy, its box which copy would load "
                + "with it on too; undone, both ticked",
                alone && offLines == (null, Draft(OtherEnabled, ("other", Named(self))))
                && offTips == ("", Draft(CopyOnTip, ("other", Named(self)), ("loads", Named(self))))
                && Copies.Ticked(copy) && Copies.Ticked(self) && Tickbox(copyRow).IsTicked, $"{aloneSay}; then {string.Join(" | ", ourRows.Select(Box))}");
            Check("... the entry made to turn it off goes with ours at the top, not last (the game sorts a mod by its id's last entry: this mod would load "
                + "last next launch), and no other mod moves for Changes", oursLead && movesNow == movesWere,
                $"entries {entriesSaid}; moves {movesWere} -> {movesNow}");
        }
        else
            Check("... a click on the Workshop copy's box: its row", false, string.Join(" | ", ourRows.Select(Box)));
        // The copy that won't load failed by the game as a duplicate (made up: two copies of ours from one source, the
        // second fails): "Duplicate - Won't load" all the same, not a red "Failed to load".
        var (stateWas, errorsWas, losingStatus) = (copy.state, copy.errors, ModDetails.Status(copy));
        copy.state = ModLoadState.Failed;
        copy.errors = [new LocString("main_menu_ui", "MOD_ERROR.DUPLICATE_ID")];
        var failedStatus = ModDetails.Status(copy);
        (copy.state, copy.errors) = (stateWas, errorsWas);
        Check("... the copy that won't load failed by the game as a duplicate (made up): Duplicate - Won't load as before, not a red Failed to load",
            losingStatus.Contains(StatusWontLoad) && failedStatus == losingStatus, $"{losingStatus} / failed {failedStatus}");
        // Between sources the game's rule says which of ours loads, and why (P10), in the lines any copies get: on the beta
        // the Workshop copy made higher loads after a relaunch, this one losing to it as the higher version; on stable a
        // local copy always wins, so with the Workshop copy running (made up), this one loads after a relaunch and the
        // Workshop copy loses to it as the local copy. From one source (the made-up copy made local), the game's pick, the
        // copy running, with no reason: the game goes by its own read order, so neither "installed first" nor "a higher
        // version" is ours to say.
        var (versionWas, selfState, sourceWas) = (copy.manifest?.version, self.state, copy.modSource);
        (string? Self, string? Copy) sourceLines, sameLines;
        (string Self, string Copy) sourceExpected, sameExpected;
        try
        {
            if (Compat.WorkshopBeatsOlderLocal)
            {
                if (copy.manifest is { } higher)
                    higher.version = "9.0.0";
                sourceExpected = (Draft(CopyLoses, ("other", Named(copy)), ("why", WhyHigher)), Draft(LoadsAfter, ("other", Named(self))));
            }
            else
            {
                (self.state, copy.state) = (ModLoadState.None, ModLoadState.Loaded);
                sourceExpected = (Draft(LoadsAfter, ("other", Named(copy))), Draft(CopyLoses, ("other", Named(self)), ("why", WhyLocal)));
            }
            sourceLines = (ModDetails.CopyLine(self, false)?.Text, ModDetails.CopyLine(copy, false)?.Text);
            if (copy.manifest is { } same)
                same.version = versionWas;
            (self.state, copy.state, copy.modSource) = (selfState, stateWas, ModSource.ModsDirectory);
            sameLines = (ModDetails.CopyLine(self, false)?.Text, ModDetails.CopyLine(copy, false)?.Text);
            // Named while both are local: two local copies are told apart by their folders.
            sameExpected = (Draft(LoadsAlso, ("other", Named(copy))), Draft(CopyLosesPlain, ("other", Named(self))));
        }
        finally
        {
            if (copy.manifest is { } manifest)
                manifest.version = versionWas;
            (self.state, copy.state, copy.modSource) = (selfState, stateWas, sourceWas);
        }
        var sourceCase = Compat.WorkshopBeatsOlderLocal ? "the beta: the Workshop copy, made higher, loads after a relaunch in place of the copy running"
            : "stable: this local copy loads after a relaunch, the Workshop copy made the one running";
        Check($"... between sources, which of its copies loads is the game's rule, with its reason, in the lines any copies get ({sourceCase})",
            sourceLines == sourceExpected, $"{sourceLines} (expected {sourceExpected})");
        Check("... two copies of it from one source (the made-up copy made local): the copy running loads, the game's pick, and the other's line gives no "
            + "reason (neither the copy installed first nor a higher version)", sameLines == sameExpected, $"{sameLines} (expected {sameExpected})");
        if (border.FindChild(PageBar.Name, true, false)?.FindChildren("*", "Button", true, false).OfType<Button>().FirstOrDefault(b => b.Text == "Disable all") is { } disableAll)
        {
            disableAll.EmitSignal(BaseButton.SignalName.Pressed);
            await Wait(menu, 0.3);
            var bothStay = Copies.Ticked(self) && Copies.Ticked(copy) && ourRows.All(r => Tickbox(r).IsTicked);
            History.Undo();
            await Wait(menu, 0.3);
            Check("... Disable all keeps both on (undone)", bothStay && Copies.Ticked(self) && Copies.Ticked(copy), string.Join(" | ", ourRows.Select(Box)));
        }
        else
            Check("... Disable all keeps both on: the page bar's Disable all", false);

        // A selection that has this mod turns it off with the rest (Jordan): the page bar's Disable selected, a picked row's
        // box, the bulk menu's Disable selected; each undone. This local copy and a mod installed once, picked.
        if (content.GetChildren().OfType<NModMenuRow>().FirstOrDefault(r => r.Visible && r.Mod is { manifest.id: { } id } m && id != ModEntry.ModId
            && id != Bmm.ModId && Copies.Others(m).Count == 0 && Copies.Ticked(m)) is { Mod: { } other } otherRow)
        {
            bool BothOff() => !Copies.Ticked(self) && !Copies.Ticked(other) && Copies.Ticked(copy);
            Selection.Clear(content, record: false);
            Selection.Add(content, [row, otherRow]);
            await Wait(menu, 0.2);
            var said = new List<string>();
            var disableSelected = border.FindChild(PageBar.Name, true, false)?.FindChildren("*", "Button", true, false).OfType<Button>()
                .FirstOrDefault(b => b.Text == "Disable selected");
            disableSelected?.EmitSignal(BaseButton.SignalName.Pressed);
            await Wait(menu, 0.3);
            said.Add($"page bar {BothOff()}");
            History.Undo();
            await Wait(menu, 0.3);
            Tickbox(otherRow).IsTicked = false;
            Tickbox(otherRow).EmitSignal(NTickbox.SignalName.Toggled, Tickbox(otherRow));
            await Wait(menu, 0.3);
            said.Add($"picked box {BothOff()}");
            History.Undo();
            await Wait(menu, 0.3);
            if (CategoryList.ShowBulkMenu(content) is { } bulk)
            {
                await Wait(menu, 0.2);
                MenuPanel.Press(bulk, "Disable selected");
                await Wait(menu, 0.3);
            }
            MenuPanel.Close();
            said.Add($"bulk menu {BothOff()}");
            History.Undo();
            await Wait(menu, 0.3);
            Selection.Clear(content, record: false);
            await Wait(menu, 0.2);
            Check("... a selection that has this mod turns it off with the rest: the page bar's Disable selected, a picked row's box, the bulk menu's "
                + "(each undone)", disableSelected != null && said.All(s => s.EndsWith(" True")) && Copies.Ticked(self) && Copies.Ticked(other) && Copies.Ticked(copy),
                $"{string.Join(", ", said)}; button {disableSelected != null}; now {Copies.Ticked(self)}/{Copies.Ticked(other)}/{Copies.Ticked(copy)}");
        }
        else
            Results.Add("SKIP a selection that has this mod: no other mod on, installed once");

        // Every copy of it off (a click on each box): the red banner, and its Enable now turns on the copy running alone,
        // not every copy (P16: on the beta the game's rule could load the other); undone, every copy off again. The
        // fixture's mod that can't load turned off first (one undo step under the others), so this mod off is the
        // relaunch check's only reason below.
        var addon = WontLoad();
        if (addon != null)
        {
            Dependencies.Set([addon], false, screen);
            CategoryList.Redraw(content);
            await Wait(menu, 0.3);
        }
        foreach (var ourRow in ourRows)
        {
            Tickbox(ourRow).IsTicked = false;
            Tickbox(ourRow).EmitSignal(NTickbox.SignalName.Toggled, Tickbox(ourRow));
            await Wait(menu, 0.3);
        }
        var allOff = !Copies.Ticked(self) && !Copies.Ticked(copy) && Copies.OursOff() && Banner() is { Visible: true }
            && (Banner()?.FindChild("SelfOff", true, false) as Label)?.Text == Menus.Text("SPIRE_MOD_MANAGER.SELF_OFF");
        await Capture(screen, outDir, "2o-self-twice-off-banner");
        Banner()?.FindChild("EnableSelfButton", true, false)?.EmitSignal(BaseButton.SignalName.Pressed);
        await Wait(menu, 0.3);
        var runningAlone = Copies.Ticked(self) && !Copies.Ticked(copy) && Banner() == null;
        History.Undo();
        await Wait(menu, 0.3);
        Check("... every copy of it off: the red banner; its Enable now turns on the copy running alone (not the other); undone, every copy off again",
            allOff && runningAlone && !Copies.Ticked(self) && !Copies.Ticked(copy) && Banner() != null,
            $"all off {allOff}, running alone {runningAlone}; now {Copies.Ticked(self)}/{Copies.Ticked(copy)}, banner {Banner() != null}");

        // Relaunch with every copy of it off asks first (ModEntry.TestRelaunch counts a relaunch instead of one): the
        // dialog says it goes on relaunch and how to get it back, with Enable now (its tip) and Relaunch anyway; Enable
        // now turns on the copy running alone and leaves "ready"; Cancel undoes it. With both copies on again (the two
        // unticks undone), Relaunch asks too, as for any mod's copies both on: both grouped under the ID, the copy that
        // won't load Ignored, nothing about this mod going, and Disable unused mods turns that copy off. With one copy on,
        // Relaunch relaunches at once.
        Node? Dialog() => menu.GetTree().Root.GetNodeOrNull(RelaunchCheck.Name);
        string Body() => (Dialog()?.FindChild("Body", true, false) as RichTextLabel)?.GetParsedText() ?? "";
        void Press(string name) => Dialog()?.FindChild(name, true, false)?.EmitSignal(BaseButton.SignalName.Pressed);
        string Buttons() => string.Join(", ", (Dialog()?.FindChild("Buttons", true, false) as Control)?.GetChildren().OfType<Button>()
            .Select(b => $"{b.Name} \"{b.Text}\"") ?? []);
        var relaunches = 0;
        ModEntry.TestRelaunch = _ => relaunches++;
        try
        {
            RelaunchCheck.Relaunch(screen);
            await Wait(menu, 0.3);
            var (asked, askedBody, askedButtons, enableTip) = (Dialog() != null && relaunches == 0, Body(), Buttons(), TipOf(Dialog()?.FindChild("EnableSelfButton", true, false)));
            await Capture(screen, outDir, "2p-self-off-relaunch");
            Press("EnableSelfButton");
            await Wait(menu, 0.3);
            var (readyBody, readyButtons, readyOn) = (Body(), Buttons(), Copies.Ticked(self) && !Copies.Ticked(copy));
            Press("CancelRelaunchButton");
            await Wait(menu, 0.3);
            var cancelled = Dialog() == null && !Copies.Ticked(self) && !Copies.Ticked(copy) && relaunches == 0;
            History.Undo(); // the second untick
            History.Undo(); // the first
            await Wait(menu, 0.3);
            Check("... Relaunch with every copy of it off asks first: the dialog says it goes on relaunch and how to get it back, with Enable now "
                + "(its tip) and Relaunch anyway; Enable now turns on the copy running alone and leaves \"ready\"; Cancel undoes it",
                asked && askedBody == RelaunchSelfOff && askedButtons == "CancelRelaunchButton \"Cancel\", EnableSelfButton \"Enable now\", RelaunchNowButton \"Relaunch anyway\""
                && enableTip == "Keep Spire Mod Manager (this mod) enabled" && readyOn && readyBody == Menus.Text("SPIRE_MOD_MANAGER.RELAUNCH_CHECK_OK")
                && readyButtons == "CancelRelaunchButton \"Cancel\", RelaunchNowButton \"Relaunch Game\"" && cancelled,
                $"asked {asked}: \"{askedBody}\" [{askedButtons}], tip \"{enableTip}\"; Enable now: on {readyOn}, \"{readyBody}\" [{readyButtons}]; "
                + $"cancelled {cancelled}");
            RelaunchCheck.Relaunch(screen);
            await Wait(menu, 0.3);
            var (bothAsked, bothBody, bothButtons) = (Dialog() != null && relaunches == 0 && Copies.Ticked(self) && Copies.Ticked(copy), Body(), Buttons());
            // Its Disable unused mods: the copy that won't load off, this one kept, so nothing is left ("ready"); Cancel
            // takes it back.
            Press("DisableUnusedButton");
            await Wait(menu, 0.3);
            var (unusedOff, unusedBody, unusedButtons) = (Copies.Ticked(self) && !Copies.Ticked(copy), Body(), Buttons());
            Press("CancelRelaunchButton");
            await Wait(menu, 0.3);
            var bothBack = Dialog() == null && Copies.Ticked(self) && Copies.Ticked(copy) && relaunches == 0;
            var oneOn = false;
            if (ourRows.FirstOrDefault(r => r.Mod == copy) is { } offRow)
            {
                Tickbox(offRow).IsTicked = false;
                Tickbox(offRow).EmitSignal(NTickbox.SignalName.Toggled, Tickbox(offRow));
                await Wait(menu, 0.3);
                oneOn = Copies.Ticked(self) && !Copies.Ticked(copy);
                RelaunchCheck.Relaunch(screen);
                await Wait(menu, 0.3);
                History.Undo(); // the Workshop copy on again
                await Wait(menu, 0.3);
            }
            var atOnce = oneOn && relaunches == 1 && Dialog() == null && Copies.Ticked(self) && Copies.Ticked(copy);
            var atOnceSay = $"one on {oneOn}, now on {Copies.Ticked(self)}/{Copies.Ticked(copy)}, dialog \"{(Dialog() == null ? "none" : Body())}\"";
            var bothExpected = RelaunchCopiesIntro + "\n" + string.Join("\n", Grouped(ModEntry.ModId, (self, true), (copy, false)));
            Check("... Relaunch with both copies of it on asks too, as for any mod's copies: the duplicates alone, grouped under the ID (this one Active, "
                + "then the other Ignored), nothing about this mod going; Disable unused mods, no Enable now or Disable mods that can't load; with one copy "
                + "on, Relaunch at once",
                bothAsked && bothBody == bothExpected
                && bothButtons == "CancelRelaunchButton \"Cancel\", DisableUnusedButton \"Disable unused mods\", RelaunchNowButton \"Relaunch anyway\"" && atOnce,
                $"asked {bothAsked}: \"{bothBody}\" (expected \"{bothExpected}\") [{bothButtons}]; at once {atOnce} ({relaunches} relaunches; {atOnceSay})");
            Check("... its Disable unused mods turns off the copy that won't load, not this one, and leaves \"ready\"; Cancel undoes it",
                unusedOff && unusedBody == Menus.Text("SPIRE_MOD_MANAGER.RELAUNCH_CHECK_OK")
                && unusedButtons == "CancelRelaunchButton \"Cancel\", RelaunchNowButton \"Relaunch Game\"" && bothBack,
                $"off {unusedOff}: \"{unusedBody}\" [{unusedButtons}]; back {bothBack}, now on {Copies.Ticked(self)}/{Copies.Ticked(copy)}");
        }
        finally
        {
            ModEntry.TestRelaunch = null;
            RelaunchCheck.Close();
        }
        if (addon != null)
        {
            History.Undo(); // the fixture's mod that can't load, on again
            await Wait(menu, 0.3);
        }
        // Gone again, its row too (the game made one as it announced it; a later search would find it).
        if (content.GetChildren().OfType<NModMenuRow>().FirstOrDefault(r => r.Mod == copy) is { } madeRow)
        {
            content.RemoveChild(madeRow);
            madeRow.QueueFree();
        }
        GameForgets(copy);
        SaveManager.Instance.SettingsSave.ModSettings?.ModList.RemoveAll(e => e.Id == ModEntry.ModId && e.Source == ModSource.SteamWorkshop);
        Profiles.Track();
        CategoryList.Redraw(content);
        await Wait(menu, 0.2);
        Check("... put back: installed once, on, the fixture's mod that can't load as it was", Copies.Others(self).Count == 0 && Copies.Ticked(self)
            && (addon == null || Copies.Ticked(addon)), $"others {Copies.Others(self).Count}, on {Copies.Ticked(self)}, addon on {addon != null && Copies.Ticked(addon)}");

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
        // row shows as above. On the main list its category is closed again too. Go to steps from the row selected, so
        // from this mod's (first in its category, and in Load Order): the next manager is Better Mod Menu.
        async Task ShowThroughSearch(string view)
        {
            if (Row(ModEntry.ModId) is { } ours)
            {
                CategoryList.Select(screen, ours);
                await PanelFor(menu, screen, ours);
            }
            const string query = "SmmTest"; // the test mods, not Better Mod Menu
            searchBox.Text = query;
            searchBox.EmitSignal(LineEdit.SignalName.TextChanged, query);
            await Wait(menu, 0.3);
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
    // page again enters it anew (History.Enter: no undo steps), so both undo histories are kept first and put back after:
    // the tests after it undo as they would have.
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
            var enteredAnew = History.Steps(History.Track.List) != history.Past[History.Track.List].Count;
            History.TestPutBack(history);
            static string Said(IEnumerable<History.ModEntry> mods) => string.Join(", ", mods.Select(m => m.Id + (m.Enabled ? "" : " (off)")));
            Check($"{what} keeping the rows in place: the saved list and Changes left as found, both undo histories put back",
                History.CurrentMods().SequenceEqual(listWas) && HistoryBar.Describe().SequenceEqual(changesWas)
                && History.Steps(History.Track.List) == history.Past[History.Track.List].Count && History.Steps(History.Track.Order) == history.Past[History.Track.Order].Count,
                $"entered anew by showing the page again {enteredAnew}; list {Said(History.CurrentMods())}, was {Said(listWas)}; "
                + $"changes {string.Join(" | ", HistoryBar.Describe())}, were {string.Join(" | ", changesWas)}");
        }
    }

    // Everything a banner says: its text, its buttons' words and their tips.
    static List<string> BannerWords(Node? banner) => banner?.FindChildren("*", "", true, false)
        .SelectMany(n => n switch { Label label => [label.Text], Button button => new[] { button.Text, Hover.TipOf(button) }, _ => [] }).ToList() ?? [];

    // The other-branch banner (Jordan, 28 Oct): on beta (made up), an enabled mod marked for stable only: a yellow banner
    // naming it, its card naming it, #warning and Show finding it; on stable, or for both branches, none. Dismiss: gone
    // for this set, forgotten once no enabled mod is off its branch.
    static async Task TestBranchBanner(Node menu, NModdingScreen screen, Control content, string outDir)
    {
        if (Dependencies.Find("SmmTestFixes") is not { } fixes || !Copies.Ticked(fixes))
        {
            Results.Add("SKIP the other-branch banner: no Test Fixes on");
            return;
        }
        var border = screen.GetNode<Control>("ModsBorder");
        var box = border.GetNode<LineEdit>("SpireModManagerSearch");
        Control? Banner() => border.GetNodeOrNull<Control>(LoadOrder.BranchName);
        string Said() => Banner()?.FindChild("BranchWarning", true, false) is Label { } label ? label.Text : "";
        void BannerPress(string name) => (Banner()?.FindChild(name, true, false) as BaseButton)?.EmitSignal(BaseButton.SignalName.Pressed);
        async Task On(PlatformBranch branch, (bool, bool)? marked)
        {
            if (marked is { } both)
                Tags.TestBranches["SmmTestFixes"] = both;
            else
                Tags.TestBranches.Remove("SmmTestFixes");
            Tags.TestBranch = branch;
            CategoryList.Redraw(content);
            await Wait(menu, 0.3);
        }
        var dismissedBefore = Store.Data.BranchDismissed;
        try
        {
            await On(PlatformBranch.PublicBeta, (true, false));
            var (shown, said, fill) = (Banner() is { Visible: true }, Said(), BannerFill(Banner()));
            var card = string.Join(" | ", await BannerCard(menu, screen, Banner(), "2y-branch-banner", outDir));
            var found = (Search.Matches(fixes, "#warning"), Search.Matches(fixes, "#\"stable only\""), Search.Matches(fixes, "#stable"), Search.Matches(fixes, "#beta"));
            BannerPress("FilterBranchButton");
            await Wait(menu, 0.3);
            var show = box.Text;
            box.Text = "";
            box.EmitSignal(LineEdit.SignalName.TextChanged, "");
            await Wait(menu, 0.3);
            Check("an enabled stable-only mod on beta: a yellow banner, \"1 enabled mod is listed for stable only - on beta, it loads with an error and may not "
                + "work\", its card naming it; #warning finds it, and Show (#enabled -#beta)",
                shown && said == "1 enabled mod is listed for stable only - on beta, it loads with an error and may not work" && Yellow(fill)
                && card.Contains("Enabled mods for another branch") && card.Contains("Test Fixes") && found == (true, true, true, false) && show == "#enabled -#beta",
                $"{shown} \"{said}\" {fill}; card {card}; found {found}; Show \"{show}\"");
            await On(PlatformBranch.Production, (true, false));
            var onStable = Banner() != null;
            await On(PlatformBranch.PublicBeta, (true, true));
            var forBoth = Banner() != null;
            await On(PlatformBranch.None, (false, true));
            var elsewhere = Banner() != null;
            Check("no other-branch banner for a stable-only mod on stable, a mod for both branches on beta, or on a branch that's neither",
                !onStable && !forBoth && !elsewhere, $"on stable {onStable}, for both {forBoth}, elsewhere {elsewhere}");
            await On(PlatformBranch.Production, (false, true));
            var betaOnly = Said();
            BannerPress("DismissBranchButton");
            await Wait(menu, 0.3);
            var gone = Banner() == null;
            CategoryList.Redraw(content);
            await Wait(menu, 0.3);
            var stays = Banner() == null;
            await On(PlatformBranch.Production, null);
            var forgotten = Store.Data.BranchDismissed == null;
            Check("a beta-only mod on stable: the banner says so; Dismiss: gone for this set, and stays gone; forgotten once no enabled mod is off its branch",
                betaOnly == "1 enabled mod is listed for beta only - on stable, it loads with an error and may not work" && gone && stays && forgotten,
                $"\"{betaOnly}\"; gone {gone}, stays {stays}, forgotten {forgotten}");
        }
        finally
        {
            Tags.TestBranches.Remove("SmmTestFixes");
            Tags.TestBranch = null;
            Store.Data.BranchDismissed = dismissedBefore;
            CategoryList.Redraw(content);
        }
    }
}
