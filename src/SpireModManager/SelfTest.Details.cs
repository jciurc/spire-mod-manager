using Godot;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;
using MegaCrit.Sts2.Core.Saves;

namespace SpireModManager;

// The self-test's checks of a mod's details: the info panel, what a mod changes, the relaunch check, bulk edits.
static partial class SelfTest
{
    // Relaunch with mods that won't load (fixture: Test Addon needs SmmTestMissing, never installed; here the framework
    // is turned off too): the dialog lists them; Enable needed mods, then Disable mods that can't load, fix
    // them in place; Cancel and Esc close it. Never presses Relaunch (that would quit the test).
    static async Task TestRelaunchCheck(Node menu, NModdingScreen screen, Control content, string outDir)
    {
        if (content.GetChildren().OfType<NModMenuRow>().FirstOrDefault(r => r.Mod?.manifest?.id == "SmmTestLib") is not { Mod: { } lib } libRow
            || WontLoad() is not { } addon)
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
        Check("relaunch check lists the mods that won't load", Body().Contains("Requires mod to be installed: " + Missing)
            && Body().Contains("Requires mod to be enabled: Test Framework"), Body());
        // Several of a kind: one line naming them all, in the plural (a made-up mod, never installed).
        var several = ModDetails.DependencyProblems(new Mod
        {
            path = "", modSource = ModSource.ModsDirectory,
            manifest = new ModManifest { id = "SmmPluralTest", name = "Plural Test", dependencies = [new("SmmGoneA"), new("SmmGoneB"), new("SmmTestLib")] },
        });
        Check("a mod needing several of a kind: one line naming them all, in the plural",
            several.SequenceEqual(["Requires mods to be installed: SmmGoneA, SmmGoneB", "Requires mod to be enabled: Test Framework"]), string.Join(" | ", several));
        // Cancel first, at the row's left; the fixes and Relaunch anyway at its right.
        var row = Dialog()?.FindChild("Buttons", true, false) as Control;
        var rowButtons = row?.GetChildren().OfType<Button>().ToList() ?? [];
        Check("its Cancel comes first, at the left, the fixes and Relaunch anyway at the right",
            rowButtons.Select(b => b.Name.ToString()).SequenceEqual(["CancelRelaunchButton", "EnableNeededButton", "DisableBrokenButton", "RelaunchNowButton"])
            && row != null && rowButtons[0].GetGlobalRect().Position.X - row.GetGlobalRect().Position.X < 1
            && row.GetGlobalRect().End.X - rowButtons[^1].GetGlobalRect().End.X < 1,
            string.Join(", ", rowButtons.Select(b => $"{b.Name} {b.GetGlobalRect().Position.X:0}-{b.GetGlobalRect().End.X:0}")) + $" in {row?.GetGlobalRect()}");
        await Capture(screen, outDir, "15-relaunch-check");
        Press("EnableNeededButton");
        await Wait(menu, 0.3);
        Check("Enable required mods fixes the off ones, the list updates", Copies.Ticked(lib) && !Body().Contains("Test Framework") && Body().Contains(Missing), Body());
        Press("DisableBrokenButton");
        await Wait(menu, 0.3);
        Check("Disable mods that can't load: nothing left, plain Relaunch", !Copies.Ticked(addon)
            && Dialog()?.FindChild("RelaunchNowButton", true, false) is Button { Text: "Relaunch Game" }, Body());
        // Cancel takes both fixes back: Test Framework off again (as this test set it), Test Addon on (the fixture's).
        // It forgets them too: nothing to redo.
        Press("CancelRelaunchButton");
        await Wait(menu, 0.3);
        Check("Cancel closes it and takes back the fixes made in it", Dialog() == null && !Copies.Ticked(lib) && Copies.Ticked(addon)
            && !tickbox.IsTicked && !History.CanRedo, $"framework on {Copies.Ticked(lib)}, addon on {Copies.Ticked(addon)}");
        // Esc the same, after one fix; the page stays.
        RelaunchCheck.Show(screen);
        await Wait(menu, 0.2);
        Press("EnableNeededButton");
        await Wait(menu, 0.3);
        var fixedIt = Copies.Ticked(lib);
        Input.ParseInputEvent(new InputEventKey { Keycode = Key.Escape, Pressed = true });
        await Wait(menu, 0.2);
        Input.ParseInputEvent(new InputEventKey { Keycode = Key.Escape, Pressed = false });
        await Wait(menu, 0.2);
        Check("Esc closes it the same way, the fix taken back, and stays on the page", fixedIt && Dialog() == null && screen.IsVisibleInTree()
            && !Copies.Ticked(lib) && Copies.Ticked(addon), $"fixed {fixedIt}, framework on {Copies.Ticked(lib)}");
        // Cancel with nothing done takes nothing back: the undo step before the dialog (this test's untick) stays.
        RelaunchCheck.Show(screen);
        await Wait(menu, 0.2);
        Press("CancelRelaunchButton");
        await Wait(menu, 0.3);
        Check("Cancel with no fix made changes nothing", Dialog() == null && !Copies.Ticked(lib) && Copies.Ticked(addon));
        History.Undo(); // this test's untick: Test Framework on again, as the fixture has it
        await Wait(menu, 0.3);
        Check("then one undo puts the framework back on, as the fixture has it", Copies.Ticked(lib) && tickbox.IsTicked);
    }

    // A made-up mod with very long everything (name, id, author, version, description, alias, many tags, notes),
    // shown in the list and the panel (screenshot 16-long-details). The panel's title stays on one line with its
    // version and the source line keeps its status, both cut with "…" (the whole text a second over them); the
    // row keeps its author and its height, what doesn't fit as "and N more...". A redraw fits no row title again, its cut one
    // and an aliased one included, in Vanilla View and in our main list. It's removed again afterwards.
    static async Task TestLongDetails(Node menu, NModdingScreen screen, Control content, string outDir)
    {
        const string id = "SmmVeryLongModIdentifierThatGoesOnAndOnForTheSakeOfTesting";
        const string name = "The Extraordinarily Long-Named Compendium of Every Card, Relic, Potion and Event Ever Imagined (Deluxe Edition)";
        var words = "lorem ipsum dolor sit amet consectetur adipiscing elit sed do eiusmod tempor incididunt ut labore et dolore magna aliqua".Split(' ');
        string Text(int count) => string.Join(' ', Enumerable.Range(0, count).Select(i => words[i % words.Length]));
        var mod = new Mod
        {
            path = "", modSource = ModSource.ModsDirectory, state = ModLoadState.AddedAtRuntime,
            manifest = new ModManifest
            {
                id = id, name = name,
                author = "A Collective of Very Enthusiastic Modders With An Unusually Long Team Name", version = "12.345.6789-beta.12+build.98765",
                description = Text(220), minGameVersion = "0.111.0",
            },
        };
        GameInstalls(mod);
        await Wait(menu, 0.4);
        NModalContainer.Instance?.Clear();
        Store.EditMod(id, info =>
        {
            info.Alias = "My favourite enormous content pack that I keep meaning to rename to something shorter";
            info.Notes = Text(120);
            info.Tags = ["content", "big", "really-long-tag-name-for-testing", "favourites", "co-op", "needs-checking-after-every-update", "deluxe",
                "cards", "relics", "events", "potions", "late-game"];
        });
        CategoryList.Apply(content);
        await Wait(menu, 0.3);
        if (content.GetChildren().OfType<NModMenuRow>().FirstOrDefault(r => r.Mod == mod) is { } row)
        {
            CategoryList.Select(screen, row);
            CategoryList.Reveal(content, id, flash: false);
            await Wait(menu, 0.5);
            await Capture(screen, outDir, "16-long-details");
            var panel = screen.GetNode<Control>("%ModInfoContainer");
            var title = panel.GetNode<RichTextLabel>("ModTitle");
            var titleText = title.GetParsedText();
            var titleSize = title.GetThemeFontSize("normal_font_size");
            Check("long details: the panel shows the made-up mod (see 16-long-details)", titleText.StartsWith("The Extraordinarily"));
            Check("long details: the panel's title on one line at 24 or more, the name cut with \"…\", the version showing",
                title.GetLineCount() == 1 && titleSize >= 24 && titleText.Contains('…') && titleText.Contains("12.345")
                && title.GetContentWidth() <= title.Size.X + 1 && panel.GetGlobalRect().Encloses(title.GetGlobalRect()),
                $"{title.GetLineCount()} lines at {titleSize}, {title.GetContentWidth()} of {title.Size.X}: {titleText}");
            var source = panel.GetNode<Control>("SpireModManagerSource");
            var link = source.GetNode<LinkButton>("SpireModManagerLink");
            var status = (Label)source.FindChild("Status", true, false);
            Check("long details: the source line stops short of the thumbnail, the id cut with \"…\", the dot and status showing",
                link.Text.StartsWith("SmmVeryLong") && link.Text.Contains('…') && link.Text.EndsWith("(Local)") && status.Text.Length > 0
                && status.GetGlobalRect().End.X <= title.GetGlobalRect().End.X + 2 && panel.GetGlobalRect().Encloses(status.GetGlobalRect()),
                $"\"{link.Text}\" · {status.Text}: status {status.GetGlobalRect()}, title {title.GetGlobalRect()}");
            // A second over the cut title or id: the whole of it, in a plain tooltip.
            async Task<string> Tooltip(Control target, string card, double seconds)
            {
                Hover.TestMouse = () => target.GetGlobalRect().GetCenter();
                target.EmitSignal(Control.SignalName.MouseEntered);
                await WaitReal(menu, seconds);
                var text = screen.GetNodeOrNull(card)?.FindChildren("*", "RichTextLabel", true, false).OfType<RichTextLabel>().FirstOrDefault()?.GetParsedText() ?? "";
                Hover.TestMouse = () => new Vector2(5, 5);
                target.EmitSignal(Control.SignalName.MouseExited);
                await Wait(menu, 0.1);
                return text;
            }
            var wholeTitle = await Tooltip(title, "SpireModManagerFullText", Hover.SlowDelay + 0.2);
            var wholeId = await Tooltip(link, "SpireModManagerFullText", Hover.SlowDelay + 0.2);
            Check("long details: a second over the cut title or id shows the whole of it", wholeTitle.StartsWith(name) && wholeTitle.EndsWith("12.345.6789-beta.12+build.98765")
                && wholeId == id, $"{wholeTitle} / {wholeId}");

            var longAuthor = row.GetNodeOrNull<Label>("SpireModManagerStrip/Author");
            var longTag = row.GetNodeOrNull("SpireModManagerStrip")?.GetChildren().OfType<PanelContainer>().Select(c => c.GetChildOrNull<Label>(0)).FirstOrDefault(l => l?.Text.StartsWith("really-long") == true);
            Check("long details: the row's author and a long tag are cut with an ellipsis", longAuthor is { ClipText: true, TextOverrunBehavior: TextServer.OverrunBehavior.TrimEllipsis } && longTag is { ClipText: true });
            // Many tags: the line ends short of the row's buttons, with the author in it, the row its usual height. Tags
            // first, then keywords: what doesn't fit is "and N more..." (here every keyword too), whose card has those tags
            // as pills, "Additional keywords:" and those keywords, then the click hint (16b-more-card).
            var strip = row.GetNode<Control>("SpireModManagerStrip");
            var lineEnd = row.GetGlobalRect().End.X + strip.OffsetRight;
            Check("long details: many tags: the author still shows, inside the line", longAuthor is { Visible: true } && longAuthor.GetGlobalRect().End.X <= lineEnd + 1
                && strip.GetGlobalRect().End.X <= lineEnd + 1, $"author {longAuthor?.GetGlobalRect()}, line {strip.GetGlobalRect()}, ends at {lineEnd}");
            var folded = strip.GetChildren().OfType<PanelContainer>().Where(c => !c.Visible).Select(c => c.GetChildOrNull<Label>(0)?.Text ?? "").ToList();
            var rowWords = RowStrip.Keywords(mod);
            var more = strip.GetNodeOrNull<Label>(RowStrip.MoreName);
            var moreCard = more == null ? "" : (await SizedHoverCard(menu, more, "SpireModManagerTagCard", () => Capture(screen, outDir, "16b-more-card"))).Text;
            Check("long details: what doesn't fit is \"and N more...\" (tags first), its card those tags as pills, then \"Additional keywords:\" and those "
                + "keywords, then the click hint; the row its usual height", more is { Visible: true } && folded.Count > 0 && rowWords.Count > 0 && folded.Last() == "late-game"
                && more.Text == MoreTags(folded.Count + rowWords.Count) && strip.GetNodeOrNull<Control>(RowStrip.KeywordsName) is { Visible: false }
                && moreCard.Split('\n') is [var pills, " ", var cardWords, "", var cardHint] && folded.All(t => pills.Contains($"\u00a0{t}\u00a0"))
                && cardWords == RowStrip.KeywordsLine(rowWords) && cardHint == Menus.Text("SPIRE_MOD_MANAGER.MORE_TAGS_CLICK")
                && row.Size.Y == RowTitles.RowHeight, $"\"{more?.Text}\", {folded.Count} tags and {rowWords.Count} keywords folded, height {row.Size.Y}, "
                + $"card: {moreCard.ReplaceLineEndings(" / ")}");
            // Every other row: all its tags and keywords where they fit, no "and N more..."; otherwise its count is what's left out.
            var lines = content.GetChildren().OfType<NModMenuRow>().Where(r => r != row && r.Visible && r.HasNode("SpireModManagerStrip")).Select(r =>
            {
                var line = r.GetNode("SpireModManagerStrip");
                var (hidden, all) = (line.GetChildren().OfType<PanelContainer>().Count(c => !c.Visible), RowStrip.Keywords(r.Mod));
                var shown = line.GetNodeOrNull<Label>(RowStrip.KeywordsName) is { Visible: true } on ? on.Text[RowStrip.KeywordsLine([]).Length..] : "";
                var fine = all.Count == 0 || string.Join(", ", all).StartsWith(shown);
                return (Fits: hidden == 0 && shown == string.Join(", ", all), Right: fine && (line.GetNodeOrNull<Label>(RowStrip.MoreName) is { } rest
                    ? rest.Text == MoreTags(hidden + all.Count - (shown.Length > 0 ? shown.Split(", ").Length : 0)) : hidden == 0 && shown == string.Join(", ", all)), Id: r.Mod?.manifest?.id);
            }).ToList();
            Check("rows with room show every tag and keyword and no \"and N more...\"; the others count what's left out", lines.Any(l => l.Fits) && lines.All(l => l.Right),
                string.Join(", ", lines.Where(l => !l.Right).Select(l => l.Id)));
            // A click on "and N more...", through the viewport as a player's (headless too, the row hovered first): it goes on
            // to the row, which it selects, so the info panel shows all of them.
            if (content.GetChildren().OfType<NModMenuRow>().FirstOrDefault(r => r != row && r.Mod != null) is { } elsewhere)
                CategoryList.Select(screen, elsewhere);
            await Wait(menu, 0.2);
            row.EmitSignal(Control.SignalName.MouseEntered);
            var moreAt = more == null ? Vector2.Zero : ViewportPoint(more, more.GetGlobalRect().GetCenter());
            foreach (var pressed in new[] { true, false })
                row.GetViewport().PushInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = pressed, Position = moreAt, GlobalPosition = moreAt }, true);
            await Wait(menu, 0.3);
            row.EmitSignal(Control.SignalName.MouseExited);
            Check("long details: a click on \"and N more...\" selects the mod, as a click on the row does", more != null && CategoryList.Selected == mod && RowState.Selected(row),
                $"selected {CategoryList.Selected?.manifest?.id}");
            if (CategoryList.Selected != mod)
                CategoryList.Select(screen, row);
            await Wait(menu, 0.3);
            // A tag of the player's: its card says so, and how many mods have it, counted as it shows: one, then two once
            // another row's mod has it too, in another case (that mod's tags put back after).
            var tagChip = longTag?.GetParent<Control>();
            var oneCard = tagChip == null ? "" : await Tooltip(tagChip, "SpireModManagerTagCard", Hover.Delay + 0.15);
            var otherKey = Copies.KeyOf(content.GetChildren().OfType<NModMenuRow>().Select(r => r.Mod).FirstOrDefault(m => m != null && m != mod)) ?? "";
            var otherTags = Store.InfoFor(otherKey)?.Tags;
            Store.EditMod(otherKey, info => info.Tags = [.. otherTags ?? [], "Really-Long-Tag-Name-For-Testing"]);
            var twoCard = tagChip == null ? "" : await Tooltip(tagChip, "SpireModManagerTagCard", Hover.Delay + 0.15);
            Store.EditMod(otherKey, info => info.Tags = otherTags);
            Check("long details: a tag of the player's: its card says it's theirs and how many mods have it (any case), counted as it shows",
                otherKey.Length > 0 && oneCard.Split('\n').Contains(CustomTagTipOne) && twoCard.Split('\n').Contains(Draft(CustomTagTip, ("count", "2"))),
                $"{oneCard.ReplaceLineEndings(" / ")}, then with {otherKey}: {twoCard.ReplaceLineEndings(" / ")}");
            Hover.TestMouse = null;
            // Load Order and back: the view switch sets the title's size to 32 again, then fills the panel again. The
            // title is drawn at the size it was fitted at before, not left at 32 (on two lines).
            Click(screen.GetNode<NButton>("SpireModManagerLoadOrderToggle"));
            await Wait(menu, 0.4);
            Click(screen.GetNode<NButton>("SpireModManagerLoadOrderToggle"));
            await Wait(menu, 0.5);
            var refitSize = title.GetThemeFontSize("normal_font_size");
            Check("long details: after Load Order and back, the panel's title is drawn at the size it was fitted at, on one line",
                title.GetParsedText() == titleText && refitSize == titleSize && title.GetLineCount() == 1 && title.GetContentWidth() <= title.Size.X + 1,
                $"{title.GetLineCount()} lines at {refitSize} (was {titleSize}), {title.GetContentWidth()} of {title.Size.X}: {title.GetParsedText()}");
            // Vanilla View: the game's auto-size shrinks the long name in the panel's title and in the row's (from 60 and
            // 32). A redraw and a re-layout there write those sizes straight in again, the text unchanged: the titles keep
            // their fitted sizes. The redraw leaves the row titles as they are, each at the size fitted for its text and
            // box (the long one too): none has a size written or is fitted again. (With Better Mod Menu, its page styles
            // them its own way.)
            if (!Bmm.Present)
            {
                var rowTitle = row.GetNode<RichTextLabel>("Title");
                (int Panel, int Row) Sizes() => (title.GetThemeFontSize("normal_font_size"), rowTitle.GetThemeFontSize("normal_font_size"));
                Click(screen.GetNode<BaseButton>("SpireModManagerViewToggle"));
                await Wait(menu, 0.5);
                var fitted = Sizes();
                var refitted = new List<string>();
                Layout.TestFitting = label =>
                {
                    if (label.GetParent() is NModMenuRow fittedRow)
                        refitted.Add(fittedRow.Mod?.manifest?.id ?? "?");
                };
                CategoryList.Apply(content);
                Layout.Apply(screen);
                await Wait(menu, 0.3);
                Layout.TestFitting = null;
                var again = Sizes();
                var description = panel.GetNode<RichTextLabel>("ModDescription");
                var underlined = description.MetaUnderlined;
                Click(screen.GetNode<BaseButton>("SpireModManagerViewToggle"));
                await Wait(menu, 0.5);
                // The game's scene leaves Godot's default: underlined. Our page filled the panel first (WatchLinks takes it off).
                Check("Vanilla View: the game's panel underlines its links as the game has it, after our page filled it; ours don't",
                    underlined && !description.MetaUnderlined, $"vanilla {underlined}, ours {description.MetaUnderlined}");
                Check("long details: in Vanilla View the long name is fitted in the panel's title and the row's, and stays so after a redraw and a re-layout",
                    fitted is (< 60, < 32) && again == fitted, $"panel {fitted.Panel}, row {fitted.Row}; after {again.Panel}, {again.Row}");
                Check("long details: a redraw in Vanilla View fits no row title again, the long one included (each stays at its own fitted size)",
                    refitted.Count == 0, $"{refitted.Count} fitted again: {string.Join(", ", refitted.Distinct().Take(8))}");
                // The same for a panel our page fills before anything recorded the game's looks (one fresh from the game's
                // scene, hidden on the menu, filled in our view): the page's own panel has them recorded at its _Ready.
                var scene = screen.SceneFilePath is { Length: > 0 } scenePath ? ModEntry.Load<PackedScene>(scenePath)?.Instantiate() : null;
                var freshSay = "no panel in the game's scene";
                if (scene?.GetNodeOrNull<NModInfoContainer>("%ModInfoContainer") is { } fresh)
                {
                    var freshText = fresh.GetNode<RichTextLabel>("ModDescription");
                    var sceneUnderline = freshText.MetaUnderlined;
                    fresh.GetParent()?.RemoveChild(fresh);
                    fresh.Visible = false;
                    menu.AddChild(fresh);
                    ModDetails.Fill(fresh, mod);
                    var oursUnderline = freshText.MetaUnderlined;
                    ModDetails.Looks(fresh, game: true);
                    freshSay = $"scene {sceneUnderline}, ours {oursUnderline}, vanilla {freshText.MetaUnderlined}";
                    await Wait(menu, 0.1); // its fields' deferred placing first
                    fresh.QueueFree();
                }
                scene?.Free();
                Check("Vanilla View: a panel our page filled before the game's looks were recorded gets the game's underline back",
                    freshSay == "scene True, ours False, vanilla True", freshSay);
            }
            // Our main list: the same for a title with an alias (the alias, the name beside it) or cut with "…" (the long
            // one). Apply put the plain name back while the auto-size was off and Fit set the whole text again, so the
            // label fitted itself once more on every redraw: its own fitting, which TestFitting doesn't see, so a title
            // whose text or size changes during the redraw counts too (read before the titles are fitted, deferred, and
            // after).
            const string aliased = "SmmTestUi";
            var aliasWas = Store.InfoFor(aliased)?.Alias;
            Store.EditMod(aliased, info => info.Alias = "UI bits");
            CategoryList.Reveal(content, aliased, flash: false);
            CategoryList.Apply(content);
            await Wait(menu, 0.3);
            var titles = content.GetChildren().OfType<NModMenuRow>().Select(r => (Id: r.Mod?.manifest?.id ?? "?", Label: r.GetNode<RichTextLabel>("Title"))).ToList();
            List<string> Titles() => titles.Select(t => $"{t.Id}: {t.Label.Text} at {t.Label.GetThemeFontSize("normal_font_size")}").ToList();
            var settled = Titles();
            var fittedAgain = new List<string>();
            Layout.TestFitting = label =>
            {
                if (label.GetParent() is NModMenuRow fittedRow)
                    fittedAgain.Add(fittedRow.Mod?.manifest?.id ?? "?");
            };
            CategoryList.Apply(content);
            var during = Titles();
            await Wait(menu, 0.3);
            Layout.TestFitting = null;
            var changed = during.Concat(Titles()).Except(settled).ToList();
            Check("long details: a second redraw in our main list fits no row title again, aliased and cut ones included",
                fittedAgain.Count == 0 && changed.Count == 0,
                $"{fittedAgain.Count} fitted again: {string.Join(", ", fittedAgain.Distinct().Take(8))}; changed: {string.Join(" | ", changed.Take(4))}");
            var aliasedTitle = titles.Where(t => t.Id == aliased).Select(t => t.Label).FirstOrDefault();
            Check("long details: after it, the aliased title still shows the alias with the name beside it, at the rows' size",
                aliasedTitle != null && aliasedTitle.GetParsedText().StartsWith("UI bits") && aliasedTitle.GetParsedText().Contains("Test UI")
                && aliasedTitle.GetThemeFontSize("normal_font_size") == Layout.RowTitleSize,
                $"{aliasedTitle?.GetParsedText()} at {aliasedTitle?.GetThemeFontSize("normal_font_size")}");
            Store.EditMod(aliased, info => info.Alias = aliasWas);
            content.RemoveChild(row);
            row.QueueFree();
        }
        else
            Results.Add("FAIL long details: no row for the made-up mod");
        GameForgets(mod);
        Store.EditMod(id, info => (info.Alias, info.Notes, info.Tags, info.Category) = (null, null, null, null));
        SaveManager.Instance.SettingsSave.ModSettings?.ModList.RemoveAll(e => e.Id == id);
        History.Record();
        CategoryList.Redraw(content);
        Compat.Clear(screen.GetNode<NModInfoContainer>("%ModInfoContainer"));
        await Wait(menu, 0.3);
    }

    // A long description in the info panel (Jordan, 22 Oct): three lines at most, cut at a word with "…", the tags it
    // leaves open closed there (the facts after it keep their own look), the whole of it in a card over those lines; a
    // short one as it is, with no card; the game's panel in Vanilla View untouched. Then the fade at an edge the panel's
    // text runs on past (a made-up mod needing sixty mods that aren't installed, so it scrolls): below at the top, both
    // midway, above at the end, none once it all fits and back after; in the panel's colour, over the text and not its
    // scrollbar, drawn rather than a control. Screenshots 16d-description-cut, 16e-description-card, 16f-fade-bottom,
    // 16g-fade-both, 16h-fade-top.
    static async Task TestDescriptionCut(Node menu, NModdingScreen screen, Control content, string outDir)
    {
        var words = ("Every card in the game gets a fresh coat of paint: brighter colours, sharper edges and a little more contrast on the "
            + "busiest scenes, so that each picture reads at a glance").Split(' ');
        string Text(int from, int count) => string.Join(' ', Enumerable.Range(from, count).Select(i => words[i % words.Length]));
        // The game's gold and an italic running on past the cut: both closed where it's cut.
        var description = $"[gold]{Text(0, 12)}[/gold] {Text(12, 30)} [i]{Text(42, 60)}[/i] {Text(102, 20)}";
        var plain = ModDetails.WithoutTags(description);
        var mod = new Mod
        {
            path = "", modSource = ModSource.ModsDirectory, state = ModLoadState.Loaded,
            manifest = new ModManifest
            {
                id = "SmmLongDescriptionTest", name = "Long Description Test", author = "Someone Careful", description = description,
                dependencies = [.. Enumerable.Range(1, 60).Select(i => new ModDependency($"SmmFadeTestNeeds{i:00}"))],
            },
        };
        var info = screen.GetNode<NModInfoContainer>("%ModInfoContainer");
        var label = info.GetNode<RichTextLabel>("ModDescription");
        var bar = label.GetVScrollBar();
        try
        {
            info.Fill(mod);
            await Wait(menu, 0.5);
            var parsed = label.GetParsedText();
            var start = parsed.IndexOf(PanelDescription.Mark);
            var end = start < 0 ? -1 : parsed.IndexOf(PanelDescription.Mark, start + 1);
            var shown = end > start ? parsed[(start + 1)..end] : "";
            var (first, last) = end > start ? (label.GetCharacterLine(start), label.GetCharacterLine(end)) : (-1, -1);
            Check("a long description: three lines in the panel, cut at a word with \"…\", the rest of the panel's text after it",
                last - first + 1 == PanelDescription.Lines && shown.EndsWith('…') && shown.Length > 100 && plain.StartsWith(shown[..^1])
                && shown.Length - 1 < plain.Length && plain[shown.Length - 1] == ' ' && shown[^2] != ' ' && parsed[(end + 1)..].StartsWith("\n\n"),
                $"lines {first}..{last}: \"{shown}\"");
            var bbcode = label.Text;
            var cut = bbcode.IndexOf(PanelDescription.Mark) is var from and >= 0 && bbcode.IndexOf(PanelDescription.Mark, from + 1) is var to and > 0 ? bbcode[from..to] : "";
            int Count(string text, string tag) => text.Split(tag).Length - 1;
            Check("a long description cut: the tags it leaves open closed there (the game's gold, an italic), the facts after it as they were",
                Count(cut, "[gold]") == 1 && Count(cut, "[/gold]") == 1 && Count(cut, "[i]") == 1 && Count(cut, "[/i]") == 1
                && bbcode.Contains($"[gold]{Menus.Text("SPIRE_MOD_MANAGER.DEPENDENCIES")}[/gold]: "), cut);
            await Capture(screen, outDir, "16d-description-cut");

            // The card: over the three lines, where the label has them; the whole description in it.
            var hover = label.GetNodeOrNull<Control>(PanelDescription.HoverName);
            var lineTop = label.GetThemeStylebox("normal").GetOffset().Y - (float)bar.Value;
            var placed = hover == null ? "no card area" : $"{hover.Position.Y:0.#}..{hover.Position.Y + hover.Size.Y:0.#} for lines at "
                + $"{lineTop + label.GetLineOffset(first):0.#}..{lineTop + label.GetLineOffset(last) + label.GetLineHeight(last):0.#}";
            var card = hover == null ? "" : (await SizedHoverCard(menu, hover, PanelDescription.CardName, () => Capture(screen, outDir, "16e-description-card"))).Text;
            Check("a long description cut: the whole of it in a card over its three lines (the rest of the panel's text not its trigger)",
                hover is { Visible: true, MouseFilter: Control.MouseFilterEnum.Pass } && Math.Abs(hover.Position.Y - (lineTop + label.GetLineOffset(first))) < 1
                && Math.Abs(hover.Position.Y + hover.Size.Y - (lineTop + label.GetLineOffset(last) + label.GetLineHeight(last))) < 1 && card == plain,
                $"{placed}; card: {card}");

            // The fade: the text runs on below (sixty mods it needs), so there at the top.
            var panelFill = (info.GetThemeStylebox("panel") as StyleBoxFlat)?.BgColor ?? Colors.White;
            bar.Value = 0;
            await Wait(menu, 0.2);
            var atTop = ScrollFade.TestDrawn(label);
            await Capture(screen, outDir, "16f-fade-bottom");
            bar.Value = (bar.MaxValue - bar.Page) / 2;
            await Wait(menu, 0.2);
            var midway = ScrollFade.TestDrawn(label);
            await Capture(screen, outDir, "16g-fade-both");
            bar.Value = bar.MaxValue;
            await Wait(menu, 0.2);
            var atEnd = ScrollFade.TestDrawn(label);
            await Capture(screen, outDir, "16h-fade-top");
            var room = label.Size.X - bar.Size.X;
            Check("the panel's text scrolled: a fade below while there's more below, at both edges midway, above at the end",
                bar.Visible && atTop is (false, true, _) && midway is (true, true, _) && atEnd is (true, false, var endRoom)
                && Math.Abs(endRoom.Size.X - room) < 0.5f && Math.Abs(endRoom.Size.Y - label.Size.Y) < 0.5f,
                $"scrollbar {bar.Visible} ({bar.MaxValue:0} tall, {bar.Page:0} shown): top {atTop}, midway {midway}, end {atEnd}; the text's room {room:0.#}");
            Check("the fade in the panel's colour, deepening to the edge, over the text and not its scrollbar; drawn, so it takes no clicks",
                ScrollFade.Shade(0) is var edge && edge.R == panelFill.R && edge.G == panelFill.G && edge.B == panelFill.B && edge.A > 0.5f && ScrollFade.Shade(1).A == 0
                && atEnd is { Room.End.X: var right } && right <= bar.Position.X + 0.5f && label.GetChildren().OfType<Control>().All(c => c == hover),
                $"edge {ScrollFade.Shade(0)}, the panel {panelFill}; fade to {atEnd?.Room.End.X:0.#}, scrollbar from {bar.Position.X:0.#}; "
                + $"the label's controls: {string.Join(", ", label.GetChildren().OfType<Control>().Select(c => c.Name))}");
            // It follows the panel's size: room for it all, none; back to its size, a fade again.
            var (top, bottom) = (label.OffsetTop, label.OffsetBottom);
            label.OffsetBottom = top + label.GetContentHeight() + 20;
            await Wait(menu, 0.2);
            var fits = ScrollFade.TestDrawn(label);
            label.OffsetBottom = bottom;
            await Wait(menu, 0.2);
            var again = ScrollFade.TestDrawn(label);
            Check("the fade follows the panel's size: none while all its text fits, back once it doesn't",
                fits is (false, false, _) && again is { } back && (back.Above || back.Below), $"fits {fits}, then {again}");

            // A short description: as it is, no card.
            if (Dependencies.Find("SmmTestFixes") is { manifest.description: { Length: > 0 } own } fixes)
            {
                info.Fill(fixes);
                await Wait(menu, 0.3);
                Check("a short description shows as it is, with no card over it", label.Text.Contains(own) && !label.Text.Contains(PanelDescription.Mark)
                    && hover is { Visible: false } && PanelDescription.TestPlaced(label) == null, label.Text.Split('\n').FirstOrDefault() ?? "");
            }
            else
                Results.Add("SKIP a short description as it is: Test Fixes has none");

            // Vanilla View: the game's panel, its own text for the mod, whole; no card, no fade.
            info.Fill(mod);
            await Wait(menu, 0.3);
            Click(screen.GetNode<BaseButton>("SpireModManagerViewToggle"));
            await Wait(menu, 0.5);
            info.Fill(mod); // the game's own fill there
            await Wait(menu, 0.3);
            var (game, gameHover, gameFade) = (label.Text, hover?.Visible, ScrollFade.TestDrawn(label));
            Click(screen.GetNode<BaseButton>("SpireModManagerViewToggle"));
            await Wait(menu, 0.5);
            Check("Vanilla View: the game's own panel shows the description whole, uncut; no card over it, no fade",
                game.Contains(Text(102, 20)) && !game.Contains(PanelDescription.Mark) && gameHover == false && gameFade is (false, false, _),
                $"card area shown {gameHover}, fade {gameFade}: {game[..Math.Min(80, game.Length)]}");
        }
        finally
        {
            Compat.Clear(info);
        }
    }

    // A Workshop mod's head (made-up details; the sandbox has no Steam): Author, Updated (how long ago, and a link
    // to its change notes on the Workshop), Compatibility (its Workshop branches not listed). "Ago" in hours under a day,
    // singular for one. Dates and numbers in the game's language. Its Other keywords after its Workshop tags, then
    // Modified content (screenshot 8b).
    static async Task TestWorkshopHead(Node menu, NModdingScreen screen, string outDir)
    {
        var mod = new Mod
        {
            path = "", modSource = ModSource.SteamWorkshop, state = ModLoadState.AddedAtRuntime,
            manifest = new ModManifest { id = "SmmWorkshopTest", name = "Workshop Test", author = "Someone Careful" },
        };
        Compat.SetWorkshopId(mod, 42);
        Installed.Detected(mod); // so the panel can find it by id
        WorkshopInfo.TestSet(42, new WorkshopInfo.Details(DateTime.Now.AddDays(-30), DateTime.Now.AddDays(-4), ["QoL", "Utility"], [("public", "public-beta")],
            1_572_864, 1234)); // 1.5 MB
        var info = screen.GetNode<NModInfoContainer>("%ModInfoContainer");
        info.Fill(mod);
        await Wait(menu, 0.3);
        var head = info.GetNodeOrNull<RichTextLabel>("SpireModManagerHead");
        var text = head?.GetParsedText() ?? "";
        Check("a Workshop mod's head: Author, Updated (4 days ago • Changelog), Compatibility", text.IndexOf("Author") < text.IndexOf("Updated")
            && text.IndexOf("Updated") < text.IndexOf("Compatibility") && text.Contains("(4 days ago)  •\u00a0\u00a0Changelog") && head?.Text.Contains("[url=patchnotes]") == true, text);
        // Marked for branches on the Workshop: no "Workshop branches" after its Compatibility (Jordan, 22 Oct: "it's redundant
        // to compatibility and tags").
        var compatibility = text.Split('\n').FirstOrDefault(l => l.StartsWith(Menus.Text("SPIRE_MOD_MANAGER.COMPATIBILITY") + ": ")) ?? "";
        Check("a Workshop mod marked for branches: its Compatibility ends with this game's version, the branches not listed",
            compatibility.EndsWith(")") && !compatibility.Contains(';') && !text.Contains("Workshop branches"), text);
        var link = info.FindChild("SpireModManagerLink", true, false) as LinkButton;
        Check("the source line: id (SteamID n)", link?.Text == "SmmWorkshopTest (SteamID 42)", link?.Text ?? "");

        // Both leave the game (#126): the external-link mark after each, part of the link and in its colour, and each
        // still goes where it went.
        if (link != null && head != null)
        {
            var linkSize = link.GetThemeFontSize("font_size");
            var linkText = link.GetThemeFont("font").GetStringSize(link.Text, fontSize: linkSize).X;
            var mark = LinkMark.Rect(link);
            Check("the Workshop link: the external-link mark a small gap after its text, 11 px for its 16 px text, inside the link's own (clickable) box",
                linkSize == 16 && mark is { } at && at.Position.X >= linkText + 2 && at.Position.X <= linkText + 6 && at.Size == new Vector2(11, 11)
                && at.End.X <= link.Size.X + 0.5f && at.Position.Y >= 0 && at.End.Y <= link.Size.Y + 0.5f
                && link.IsInGroup(LinkMark.Group) && link.GetSignalConnectionList(CanvasItem.SignalName.Draw).Count > 0,
                $"mark {mark} after text {linkText:0.#}, link {link.Size}");
            // The mark as drawn: the link's colour with the mouse off it, then (a mouse motion through the viewport; none
            // headless) its hover colour while the mouse is on it, and its own again once it leaves.
            var (normal, lit) = (link.GetThemeColor("font_color"), link.GetThemeColor("font_hover_color"));
            var headless = DisplayServer.GetName() == "headless";
            static void MoveTo(Vector2 at) => Input.ParseInputEvent(new InputEventMouseMotion { Position = at, GlobalPosition = at });
            var linkRect = link.GetGlobalRect();
            var away = link.GetViewport().GetMousePosition(); // where the mouse was, unless that's on the link: above it then
            if (linkRect.Grow(2).HasPoint(away))
                away = linkRect.Position - new Vector2(0, 30);
            if (!headless)
            {
                MoveTo(away);
                await Wait(menu, 0.1);
            }
            var unlit = await MarkDrawn(menu, link);
            Check("the Workshop link's mark: drawn in the link's own colour while it isn't lit",
                link.GetDrawMode() == BaseButton.DrawMode.Normal && unlit == normal,
                $"{link.GetDrawMode()}, drawn {unlit?.ToString() ?? "nothing"}; the link's {normal}");
            if (headless)
                Results.Add("SKIP the Workshop link's mark lit with the link: there's no mouse headless");
            else
            {
                // Redrawn by the link itself as it lights and goes out (no QueueRedraw here).
                LinkMark.TestForget(link);
                MoveTo(linkRect.GetCenter());
                await Wait(menu, 0.1);
                var (onMode, onDrawn, under) = (link.GetDrawMode(), LinkMark.TestDrawn(link), link.GetViewport().GuiGetHoveredControl());
                LinkMark.TestForget(link);
                MoveTo(away);
                await Wait(menu, 0.1);
                var (offMode, offDrawn) = (link.GetDrawMode(), LinkMark.TestDrawn(link));
                Check("the Workshop link's mark: redrawn in the link's hover colour while the mouse is on the link, and in its own once it leaves",
                    lit != normal && onMode == BaseButton.DrawMode.Hover && onDrawn == lit && offMode == BaseButton.DrawMode.Normal && offDrawn == normal,
                    $"on it ({under?.Name.ToString() ?? "nothing"} under the mouse at {linkRect.GetCenter()}): {onMode}, drawn {onDrawn?.ToString() ?? "nothing"}; "
                    + $"off it: {offMode}, drawn {offDrawn?.ToString() ?? "nothing"}; the link's {normal}, lit {lit}");
            }
            // The status after the link: the link as wide as its mark's end, then the line's separation, the dot and its
            // separation.
            var source = info.GetNode<Control>("SpireModManagerSource");
            var status = (Label)source.FindChild("Status", true, false);
            var dot = (Control)source.FindChild("Dot", true, false);
            var statusAt = link.GetGlobalRect().End.X + source.GetThemeConstant("separation") + dot.Size.X + dot.GetParent<Control>().GetThemeConstant("separation");
            var title = info.GetNode<Control>("ModTitle");
            Check("the Workshop link still opens the mod's Workshop page, and the status after it moves over only for the mark, within the title's width",
                link is { Disabled: false, Uri: "https://steamcommunity.com/sharedfiles/filedetails/?id=42" } && mark is { } shown
                && Math.Abs(link.Size.X - shown.End.X) <= 0.5f && Math.Abs(status.GlobalPosition.X - statusAt) <= 1
                && status.GetGlobalRect().End.X <= title.GetGlobalRect().End.X + 2,
                $"{link.Uri}; mark {mark}, link {link.GetGlobalRect()}, status {status.GetGlobalRect()} (expected at {statusAt:0.#}), title {title.GetGlobalRect()}");

            var bbcode = head.Text;
            var tag = LinkMark.Tag(16, "#99ccff");
            var changelog = bbcode.IndexOf("[url=patchnotes]", StringComparison.Ordinal);
            var tagAt = changelog < 0 ? -1 : bbcode.IndexOf(tag, changelog, StringComparison.Ordinal);
            Check("Changelog: the external-link mark right after its text, inside its link and its colour, 11 px for the head's 16 px text",
                tagAt >= 0 && bbcode[..tagAt].EndsWith("[url=patchnotes][color=#99ccff]Changelog") && bbcode[(tagAt + tag.Length)..].StartsWith("[/color][/url]")
                && head.GetThemeFontSize("normal_font_size") == 16 && LinkMark.Size(16) == 11, bbcode);
            // The [img] finds the mark by its path, and shows it: a line of just the mark is as wide as the gap and 11 px.
            var probe = new RichTextLabel { BbcodeEnabled = true, FitContent = true, AutowrapMode = TextServer.AutowrapMode.Off, Text = tag };
            Hover.Fonts(probe, 16);
            info.AddChild(probe);
            var gapOnly = probe.GetThemeFont("normal_font").GetStringSize("\u00a0", fontSize: 16).X;
            var probeWidth = probe.GetContentWidth();
            info.RemoveChild(probe);
            probe.QueueFree();
            Check("Changelog's mark: its [img] path loads the mark itself, which shows 11 px wide after the gap",
                ModEntry.Load<Texture2D>(Icons.ExternalLink(11).ResourcePath)?.GetInstanceId() == Icons.ExternalLink(11).GetInstanceId()
                && Icons.ExternalLink(11).GetSize() == new Vector2(11, 11) && Math.Abs(probeWidth - (gapOnly + 11)) <= 1,
                $"path {Icons.ExternalLink(11).ResourcePath}, a line of it {probeWidth} wide (gap {gapOnly:0.#})");
            Check("Changelog still opens the mod's change notes: the head's link is still patchnotes, its click still handled",
                bbcode.Split("[url=patchnotes]").Length == 2 && head.GetSignalConnectionList(RichTextLabel.SignalName.MetaClicked).Count > 0
                && head.MouseFilter != Control.MouseFilterEnum.Ignore);
        }
        Check("how long ago: hours under a day, one day singular, green under two days",
            ModDetails.Ago(DateTime.Now.AddMinutes(-20)).Contains("(less than an hour ago)") && ModDetails.Ago(DateTime.Now.AddHours(-3)).Contains("(3 hours ago)") && ModDetails.Ago(DateTime.Now.AddMinutes(-70)).Contains("(1 hour ago)")
            && ModDetails.Ago(DateTime.Now.AddHours(-30)).Contains("(1 day ago)") && ModDetails.Ago(DateTime.Now.AddHours(-30)).Contains("#a6e3a1")
            && !ModDetails.Ago(DateTime.Now.AddDays(-3)).Contains("#a6e3a1"));

        // Dates, sizes and counts follow the game's language: in English as they always read; in German, the game's
        // German date pattern and separators (our words stay English there, as we have no German text).
        var english = info.GetNode<RichTextLabel>("ModDescription").GetParsedText();
        Check("in English: October 11, 2026; Size: 1.5 MB; Subscribers: 1,234; the profile list's Oct 11", ModDetails.Date(new DateTime(2026, 10, 11)) == "October 11, 2026"
            && english.Contains("Size: 1.5 MB") && english.Contains("Subscribers: 1,234") && Profiles.ShortDate(new DateTime(2026, 10, 11)) == "Oct 11", english);
        var facts = english.Split('\n');
        var tagsAt = Array.IndexOf(facts, $"{Menus.Text("SPIRE_MOD_MANAGER.WORKSHOP_TAGS")}: QoL, Utility");
        Check("a Workshop mod: Other keywords right after its Workshop tags, #workshop among them, then Modified content (see 8b-other-keywords)",
            tagsAt >= 0 && tagsAt + 2 < facts.Length && facts[tagsAt + 1].StartsWith($"{Menus.Text("SPIRE_MOD_MANAGER.OTHER_KEYWORDS")}: #")
            && facts[tagsAt + 1].Contains("#workshop") && facts[tagsAt + 2].StartsWith($"{Menus.Text("SPIRE_MOD_MANAGER.MODIFIED_CONTENT")}: "), english);
        await Capture(screen, outDir, "8b-other-keywords");
        var language = LocManager.Instance.Language;
        try
        {
            LocManager.Instance.SetLanguage("deu"); // the game's own switch, without the save or the menu rebuild
            info.Fill(mod);
            var german = info.GetNode<RichTextLabel>("ModDescription").GetParsedText();
            Check("with the game in German: 11. Oktober 2026; 1,5 MB; 1.234", ModDetails.Date(new DateTime(2026, 10, 11)) == "11. Oktober 2026"
                && german.Contains("1,5 MB") && german.Contains("1.234"), $"{ModDetails.Date(new DateTime(2026, 10, 11))} / {german}");
            var longAgo = ModDetails.Ago(DateTime.Now.AddDays(-1234));
            Check("with the game in German, other counts too: (1.234 days ago)", longAgo.Contains("(1.234 days ago)"), longAgo);
            // The profile list's date too, in the game's order and month (#98: it followed Windows' language).
            Check("with the game in German, the profile list's date: 11. Okt.", Profiles.ShortDate(new DateTime(2026, 10, 11)) == "11. Okt.",
                Profiles.ShortDate(new DateTime(2026, 10, 11)));
            // A date pattern that doesn't read (an unclosed quote; SetLanguage below reloads the tables without it):
            // all in English, not a German month in the English pattern.
            LocManager.Instance.GetTable("run_history").MergeWith(new() { ["INFO.DATE_FORMAT"] = "d 'MMMM" });
            Check("a broken date pattern in German: October 11, 2026", ModDetails.Date(new DateTime(2026, 10, 11)) == "October 11, 2026", ModDetails.Date(new DateTime(2026, 10, 11)));
        }
        finally
        {
            LocManager.Instance.SetLanguage(language);
        }
        WorkshopInfo.TestSet(42, null);
        Installed.Forget(mod);
        Compat.Clear(info);
    }

    // What a mod changes in the game (GameChanges; Jordan: a mod that turned the Regent into another character was hard
    // to track down). The files: a real mod's (EnderLilis's 16 at the game's paths, as its pack has them) told by
    // character and what they are; a pack written here, read as the game's packs are, with one of the game's files in
    // it, in a made-up mod's folder: that file counted, the mod's own and the engine's not, and found by searching
    // "regent". The code: our own patches, by the game's classes, in our info panel. Screenshot 17c.
    // EnderLilis's pack entries at the game's paths (and one of the engine's), as its pack has them.
    static readonly string[] Lilis =
    [
        "animations/character_select/regent/characterselect_regent.png.import", "images/packed/character_select/char_select_regent.png.import",
        "images/packed/sprite_fonts/regent_energy_icon.png.import", "images/packed/sprite_fonts/star_icon.png.import",
        "images/ui/combat/energy_counters/regent/regent_orb_layer_1.png.import", "images/ui/combat/energy_counters/regent/regent_orb_layer_2.png.import",
        "images/ui/combat/energy_counters/regent/regent_orb_layer_3.png.import", "images/ui/combat/energy_counters/regent/regent_orb_layer_5.png.import",
        "images/ui/combat/energy_star.png.import", "images/ui/combat/energy_star_layer_2.png.import", "images/ui/combat/energy_star_layer_3.png.import",
        "images/ui/hands/multiplayer_hand_regent_paper.png.import", "images/ui/hands/multiplayer_hand_regent_point.png.import",
        "images/ui/hands/multiplayer_hand_regent_rock.png.import", "images/ui/hands/multiplayer_hand_regent_scissors.png.import",
        "images/ui/top_panel/character_icon_regent.png.import", ".godot/imported/char_select_regent.png-0123.ctex",
    ];

    static async Task TestGameChanges(Node menu, NModdingScreen screen, Control content, string outDir)
    {
        (string, string)[] characters = [("ironclad", "The Ironclad"), ("regent", "The Regent")];
        var assets = Lilis.Select(GameChanges.Asset).OfType<string>().Distinct().ToList();
        var subjects = GameChanges.FileSubjects(assets, characters);
        Check("a mod's files at the game's paths: counted once each (not the engine's), told by character and what they are, most first",
            assets.Count == 16 && subjects.SequenceEqual(["The Regent (energy orb, hands, character select, sprite fonts, top panel)", "combat", "sprite fonts"]),
            $"{assets.Count}: {string.Join(" | ", subjects)}");
        // And the compiler's classes inside one (an async method's state machine, the lambdas' "<>c"), here this test's.
        var nested = typeof(SelfTest).GetNestedTypes(System.Reflection.BindingFlags.NonPublic);
        string[] classes = [GameChanges.Class(typeof(NModdingScreen)), GameChanges.Class(typeof(MegaCrit.Sts2.Core.Models.CharacterModel)),
            GameChanges.Class(typeof(NModMenuRow)), GameChanges.Class(typeof(MegaCrit.Sts2.Core.Models.ModelDb)),
            GameChanges.Class(nested.FirstOrDefault(t => t.Name.StartsWith("<TestGameChanges>d__"))), GameChanges.Class(nested.FirstOrDefault(t => t.Name == "<>c"))];
        Check("the game's classes as words: modding screen, character (no \"Model\"), mod menu row, model db; a compiler-made one by the class it's in",
            classes.SequenceEqual(["modding screen", "character", "mod menu row", "model db", "self test", "self test"]), string.Join(" | ", classes));

        // A pack of a made-up mod: one of the game's files (the Regent's rock hand), one of its own, one of the engine's.
        var folder = Path.Combine(outDir, "game-changes-mod");
        Directory.CreateDirectory(folder);
        var gameFile = "images/ui/hands/multiplayer_hand_regent_rock.png.import";
        WritePack(Path.Combine(folder, "SmmChangesTest.pck"), [gameFile, "SmmChangesTest/thing.png.import", ".godot/imported/thing.png-0123.ctex"]);
        var made = new Mod { path = folder, modSource = ModSource.ModsDirectory, state = ModLoadState.Loaded,
            manifest = new ModManifest { id = "SmmChangesTest", name = "Changes Test", author = "Someone Careful" } };
        var changes = GameChanges.For(made);
        var regent = MegaCrit.Sts2.Core.Models.ModelDb.AllCharacters.First(c => c.Id.Entry == "REGENT").Title.GetFormattedText();
        Check("a pack read as the game's are: the game's file it has counted, its own and the engine's not; the Regent's hands; found by \"regent\" and \"hands\"",
            changes.Files == 1 && changes.FileSubjects.SequenceEqual([$"{regent} (hands)"]) && changes.Code == 0
            && SpireModManager.Search.Matches(made, "regent") && SpireModManager.Search.Matches(made, "hands") && !SpireModManager.Search.Matches(made, "ironclad"),
            $"{changes.Files} files: {string.Join(" | ", changes.FileSubjects)}; code {changes.Code}");
        Check("search's words for what a mod changes are kept for the session, the same as worked out afresh",
            GameChanges.Words(made) == changes.Words && GameChanges.Words(made) == GameChanges.Words(made), GameChanges.Words(made));

        // Our own patches, in our info panel.
        if (content.GetChildren().OfType<NModMenuRow>().FirstOrDefault(r => r.Mod is { manifest.id: ModEntry.ModId, state: ModLoadState.Loaded }) is not { Mod: { } ours } row)
        {
            Results.Add("SKIP what a mod changes in the panel: our row isn't there");
            return;
        }
        var own = GameChanges.For(ours);
        CategoryList.Select(screen, row);
        await PanelFor(menu, screen, row);
        await Wait(menu, 0.4);
        var description = screen.GetNode<NModInfoContainer>("%ModInfoContainer").GetNode<RichTextLabel>("ModDescription");
        var shown = description.GetParsedText();
        var modifiedRow = shown.Split('\n').FirstOrDefault(l => l.StartsWith(Menus.Text("SPIRE_MOD_MANAGER.MODIFIED_CONTENT") + ": ")) ?? "";
        var fit = ModifiedFit(description, modifiedRow, own.Areas, $" ({Draft(Template("SPIRE_MOD_MANAGER.GAME_CODE_COUNT"), ("count", $"{own.Code}"))})");
        Check("our own patches: the game's code we change, by class (modding screen among them), in our info panel's Modified content: the most "
            + "whole areas that fit on its line, and how many places", own.Code > 0 && own.Files == 0 && own.CodeSubjects.Contains("modding screen")
            && fit is { Whole: > 0, Most: true, OneLine: true }, $"{own.Code}: {string.Join(", ", own.CodeSubjects)}; {fit} | {shown.Replace("\n", " / ")}");
        await Capture(screen, outDir, "17c-game-changes");
    }

    // The info panel's Modified content (Jordan, 22 Oct), on a made-up mod with both kinds: a pack with EnderLilis's
    // files and, as its code, our own patches. GameChanges keeps what it found (each file with its area, each game method
    // with how it's patched). The row: right under Other keywords, one line, the most areas that fit whole, then how many
    // more and both counts, subtle; search still finds the areas cut from it. Its card: Jordan's words with the count,
    // the files' sentence, the areas, the click hint. A click: the dialog (the mod's name, the note, the areas, the files
    // and the code by area with their counts, how each place is patched), scrolled when long; Close and Esc close it. A
    // fixture mod with nothing found: "No information found", subtle, a card saying what that means, and its dialog says
    // so. Screenshots 17d (the row), 17e (its card), 17f (the dialog), 17g (nothing found).
    static async Task TestModifiedContent(Node menu, NModdingScreen screen, Control content, string outDir)
    {
        const string id = "SmmBothTest", name = "Both Kinds Test";
        var folder = Path.Combine(outDir, "modified-content-mod");
        Directory.CreateDirectory(folder);
        WritePack(Path.Combine(folder, $"{id}.pck"), [.. Lilis, $"{id}/thing.png.import"]);
        var mod = new Mod
        {
            path = folder, modSource = ModSource.ModsDirectory, state = ModLoadState.Loaded,
            manifest = new ModManifest { id = id, name = name, author = "Spire Mod Manager tests", version = "1.0.0",
                description = "A made-up mod that replaces some of the game's files and changes its code." },
        };
        var ours = ModManager.Mods.FirstOrDefault(m => m.manifest?.id == ModEntry.ModId && m.state == ModLoadState.Loaded);
        var info = screen.GetNode<NModInfoContainer>("%ModInfoContainer");
        var description = info.GetNode<RichTextLabel>("ModDescription");
        var term = Menus.Text("SPIRE_MOD_MANAGER.MODIFIED_CONTENT");
        Node? Dialog() => menu.GetTree().Root.GetNodeOrNull(ModifiedContent.Name);
        GameChanges.TestAssemblies = m => m == mod ? [typeof(ModEntry).Assembly] : null;
        try
        {
            GameInstalls(mod);
            await Wait(menu, 0.4);
            NModalContainer.Instance?.Clear();
            CategoryList.Apply(content);
            await Wait(menu, 0.3);
            if (content.GetChildren().OfType<NModMenuRow>().FirstOrDefault(r => r.Mod == mod) is not { } row || ours == null)
            {
                Results.Add("FAIL modified content: no row for the made-up mod, or none for ours");
                return;
            }
            var changes = GameChanges.For(mod);

            // What GameChanges keeps: each of the game's files the pack has, with its area (FileSubjects' words, in their
            // order, each area's by path); each game method with how it's patched, the same as our own mod's.
            var characters = MegaCrit.Sts2.Core.Models.ModelDb.AllCharacters.Select(c => (c.Id.Entry.ToLowerInvariant(), c.Title.GetFormattedText())).ToList();
            var paths = changes.ReplacedFiles.Select(f => f.Path).ToList();
            var assets = Lilis.Select(GameChanges.Asset).OfType<string>().ToHashSet();
            var files = changes.ReplacedFiles;
            var together = files.Where((f, i) => i == 0 || f.Area != files[i - 1].Area).Count() == changes.FileSubjects.Count; // each area's files in one run
            var byPath = files.GroupBy(f => f.Area).All(g => g.Select(f => f.Path).SequenceEqual(g.Select(f => f.Path).Order(StringComparer.Ordinal)));
            Check("modified content: GameChanges keeps each of the game's files a pack replaces, with its area, by area and then path (not the mod's own)",
                changes.Files > 1 && paths.All(assets.Contains) && !paths.Contains($"{id}/thing.png") && together && byPath
                && changes.FileSubjects.SequenceEqual(GameChanges.FileSubjects(paths, characters)),
                string.Join(" | ", files.Select(f => $"{f.Area}: {f.Path}")));
            GameChanges.Place? PlaceOf(string type, string method) => changes.Places.FirstOrDefault(p => p.Class == type && p.Method == method);
            var ready = PlaceOf(nameof(NModdingScreen), nameof(NModdingScreen._Ready));
            var fill = PlaceOf(nameof(NModInfoContainer), nameof(NModInfoContainer.Fill));
            var readyKinds = GameChanges.Kinds.Before | GameChanges.Kinds.After | GameChanges.Kinds.Wrapped;
            Check("modified content: and each game method its code patches, as Class.Method in its area, with how (our page's _Ready: before, after and wrapped)",
                changes.Code == changes.Places.Count && changes.Places.SequenceEqual(GameChanges.For(ours).Places)
                && ready is { Area: "modding screen" } && ready.Kinds == readyKinds && fill?.Kinds == GameChanges.Kinds.After
                && changes.Places.Select(p => p.Area).Distinct().SequenceEqual(changes.CodeSubjects),
                $"{changes.Code} places; _Ready {ready}, Fill {fill}");

            CategoryList.Select(screen, row);
            CategoryList.Reveal(content, id, flash: false);
            await PanelFor(menu, screen, row);
            await Wait(menu, 0.4);
            var parsed = description.GetParsedText();
            var facts = parsed.Split('\n');
            var at = Array.FindIndex(facts, l => l.StartsWith(term + ": "));
            var shown = at >= 0 ? facts[at] : "";
            var areas = changes.Areas;
            var counts = $" ({Draft(Template("SPIRE_MOD_MANAGER.MODIFIED_COUNTS"), ("files", Draft(Template("SPIRE_MOD_MANAGER.GAME_FILES_COUNT"), ("count", $"{changes.Files}"))),
                ("places", Draft(Template("SPIRE_MOD_MANAGER.GAME_CODE_COUNT"), ("count", $"{changes.Code}"))))})";
            // The Regent's area, with its five kinds of files, is too long for the line on its own: as many of them as fit.
            var fit = ModifiedFit(description, shown, areas, counts);
            Check("modified content: right under Other keywords, one line: the most that fit (whole areas, or of the first's what), then how many more, "
                + "and both counts (see 17d)", at > 0 && facts[at - 1].StartsWith(Menus.Text("SPIRE_MOD_MANAGER.OTHER_KEYWORDS") + ": ")
                && (fit.Whole is > 0 and var whole && whole < areas.Count || fit.Items > 0) && fit.Most && fit.OneLine,
                $"\"{shown}\" ({fit}, {areas.Count} areas); before: \"{(at > 0 ? facts[at - 1] : "")}\"");
            var bbcode = description.Text.Split('\n').FirstOrDefault(l => l.Contains($"[url={ModifiedContent.Meta}]")) ?? "";
            Check("modified content: the whole row is the link for its card and click, its counts subtle, and it's no longer in two rows",
                bbcode.StartsWith($"[url={ModifiedContent.Meta}][gold]{term}[/gold]: ") && bbcode.EndsWith($"[color=#fff6e299]{counts.Trim()}[/color][/url]")
                && !parsed.Contains("Changes game code") && !parsed.Contains("Replaces game files"), bbcode);
            var cut = areas[^1];
            Check("modified content: search still finds the mod by an area the row leaves out", !shown.Contains(cut) && SpireModManager.Search.Matches(mod, cut), cut);
            await Capture(screen, outDir, "17d-modified-content");

            // Its card, with the mouse on the row.
            var rowAt = RowPoint(description, shown);
            async Task<string> Card(string shot)
            {
                Hover.TestMouse = () => rowAt;
                description.EmitSignal(RichTextLabel.SignalName.MetaHoverStarted, ModifiedContent.Meta);
                await WaitReal(menu, Hover.Delay + 0.2);
                await Capture(screen, outDir, shot);
                var text = screen.GetNodeOrNull(ModifiedContent.CardName)?.FindChildren("*", "RichTextLabel", true, false).OfType<RichTextLabel>().FirstOrDefault()?.GetParsedText() ?? "";
                Hover.TestMouse = () => new Vector2(5, 5);
                description.EmitSignal(RichTextLabel.SignalName.MetaHoverEnded, ModifiedContent.Meta);
                await WaitReal(menu, 0.1);
                Hover.TestMouse = null;
                return text;
            }
            var card = await Card("17e-modified-content-card");
            var hint = Menus.Text("SPIRE_MOD_MANAGER.MODIFIED_CLICK_TIP");
            var cardLines = card.Split('\n');
            var cardShows = cardLines.Length == 6 ? Enumerable.Range(1, areas.Count).FirstOrDefault(n => cardLines[3] == AreasListed(areas, n)) : 0;
            Check("modified content's card: Jordan's words with the count, the files' sentence, the areas (more than the row, then how many more), the click hint (see 17e)",
                cardLines is [var code, var replaced, " ", _, "", var cardHint]
                && code == Draft(Template("SPIRE_MOD_MANAGER.MODIFIED_CODE_TIP"), ("count", $"{changes.Code}"))
                && replaced == Draft(Template("SPIRE_MOD_MANAGER.MODIFIED_FILES_ALSO_TIP"), ("count", $"{changes.Files}"))
                && cardShows > fit.Whole && cardShows > 1 && cardHint == hint, $"{cardShows} areas: {card.ReplaceLineEndings(" / ")}");

            // A click: the dialog.
            description.EmitSignal(RichTextLabel.SignalName.MetaClicked, ModifiedContent.Meta);
            await WaitUntil(menu, () => Dialog()?.FindChild("Scroll", true, false) is Control { CustomMinimumSize.Y: > 0 });
            await Wait(menu, 0.2);
            var title = (Dialog()?.FindChild("Title", true, false) as Label)?.Text ?? "";
            var list = Dialog()?.FindChild("List", true, false) as RichTextLabel;
            var listText = list?.GetParsedText() ?? "";
            var note = (Dialog()?.FindChild("Note", true, false) as RichTextLabel)?.GetParsedText() ?? "";
            var lines = listText.Split('\n').ToList();
            int LineOf(string text, int from = 0) => lines.FindIndex(from, l => l == text);
            var filesAt = LineOf($"{Menus.Text("SPIRE_MOD_MANAGER.GAME_FILES")}  •  {Draft(Template("SPIRE_MOD_MANAGER.GAME_FILES_COUNT"), ("count", $"{changes.Files}"))}");
            var codeAt = LineOf($"{Menus.Text("SPIRE_MOD_MANAGER.GAME_CODE")}  •  {Draft(Template("SPIRE_MOD_MANAGER.GAME_CODE_COUNT"), ("count", $"{changes.Code}"))}");
            // Each area's heading with its count, then its paths (or Class.Method and how), in order.
            List<string> Expected<T>(IEnumerable<IGrouping<string, T>> groups, Func<int, string> count, Func<T, string> item) =>
                [.. groups.SelectMany(g => new[] { $"    {g.Key}  •  {count(g.Count())}" }.Concat(g.Select(i => "        " + item(i))))];
            var fileLines = Expected(changes.ReplacedFiles.GroupBy(f => f.Area), ModifiedContent.FilesCount, f => f.Path);
            var codeLines = Expected(changes.Places.GroupBy(p => p.Area), ModifiedContent.PlacesCount, p => $"{p.Class}.{p.Method}  ({ModifiedContent.KindWords(p.Kinds)})");
            var readyLine = $"        NModdingScreen._Ready  ({Menus.Text("SPIRE_MOD_MANAGER.PATCH_BEFORE")}, {Menus.Text("SPIRE_MOD_MANAGER.PATCH_AFTER")}, {Menus.Text("SPIRE_MOD_MANAGER.PATCH_WRAPPED")})";
            Check("modified content's dialog: the mod's name, the note, every area, the files by area with counts and paths, the code by area with counts, "
                + "Class.Method and how, what those words mean (see 17f)",
                title == Draft(Template("SPIRE_MOD_MANAGER.MODIFIED_TITLE"), ("name", name)) && note == Menus.Text("SPIRE_MOD_MANAGER.MODIFIED_NOTE")
                && lines.Count > 2 && lines[0] == Menus.Text("SPIRE_MOD_MANAGER.MODIFIED_AREAS") && lines[1] == string.Join(", ", areas)
                && filesAt > 1 && lines.Skip(filesAt + 1).Take(fileLines.Count).SequenceEqual(fileLines)
                && codeAt > filesAt && lines.Skip(codeAt + 1).Take(codeLines.Count).SequenceEqual(codeLines) && lines.Contains(readyLine)
                && lines[^1] == Menus.Text("SPIRE_MOD_MANAGER.PATCH_KINDS"),
                $"\"{title}\" / \"{note}\" / {listText.ReplaceLineEndings(" / ")[..Math.Min(1200, listText.Length)]}");
            var scroll = Dialog()?.FindChild("Scroll", true, false) as ScrollContainer;
            Check("modified content's dialog: a long list scrolls, the dialog on screen", list != null && scroll != null
                && list.GetContentHeight() > scroll.Size.Y + 20 && scroll.GetVScrollBar().Visible && scroll.Size.Y <= 560
                && Dialog()?.FindChild("Column", true, false) is Control column && column.GetViewportRect().Encloses(column.GetGlobalRect()),
                $"list {list?.GetContentHeight()}, scroll {scroll?.Size}, bar {scroll?.GetVScrollBar().Visible}");
            await Capture(Dialog() ?? screen, outDir, "17f-modified-content-dialog");
            if (scroll != null && list != null && codeAt > 0)
            {
                scroll.ScrollVertical = (int)list.GetParagraphOffset(codeAt); // down to the code: Class.Method and how
                await Wait(menu, 0.1);
                await Capture(Dialog() ?? screen, outDir, "17f2-modified-content-dialog-code");
            }
            Dialog()?.FindChild("CloseModifiedButton", true, false)?.EmitSignal(BaseButton.SignalName.Pressed);
            await Wait(menu, 0.2);
            var closed = Dialog() == null;
            description.EmitSignal(RichTextLabel.SignalName.MetaClicked, ModifiedContent.Meta);
            await Wait(menu, 0.2);
            var reopened = Dialog() != null;
            Input.ParseInputEvent(new InputEventKey { Keycode = Key.Escape, Pressed = true });
            await Wait(menu, 0.2);
            Input.ParseInputEvent(new InputEventKey { Keycode = Key.Escape, Pressed = false });
            await Wait(menu, 0.2);
            Check("modified content's dialog: Close closes it; so does Esc, staying on the page", closed && reopened && Dialog() == null && screen.IsVisibleInTree(),
                $"closed {closed}, reopened {reopened}, open after Esc {Dialog() != null}");

            // Nothing found: a fixture mod with no pack and no code.
            if (content.GetChildren().OfType<NModMenuRow>().FirstOrDefault(r => r.Mod?.manifest?.id == "SmmTestUi") is { Mod: { } plain } plainRow)
            {
                CategoryList.Select(screen, plainRow);
                await PanelFor(menu, screen, plainRow);
                await Wait(menu, 0.3);
                var none = Menus.Text("SPIRE_MOD_MANAGER.MODIFIED_NONE");
                var noneParsed = description.GetParsedText().Split('\n');
                var noneAt = Array.FindIndex(noneParsed, l => l.StartsWith(term + ": "));
                rowAt = RowPoint(description, noneAt < 0 ? "" : noneParsed[noneAt]);
                var noneCard = await Card("17g-modified-content-none");
                var expected = new List<string> { Menus.Text("SPIRE_MOD_MANAGER.MODIFIED_NONE_TIP") };
                if (plain.state != ModLoadState.Loaded)
                    expected.Add(Menus.Text("SPIRE_MOD_MANAGER.MODIFIED_NOT_LOADED"));
                description.EmitSignal(RichTextLabel.SignalName.MetaClicked, ModifiedContent.Meta);
                await Wait(menu, 0.2);
                var noneList = (Dialog()?.FindChild("List", true, false) as RichTextLabel)?.GetParsedText() ?? "";
                ModifiedContent.Close();
                Check("modified content with nothing found: \"No information found\", subtle, right under Other keywords; its card says what that means; "
                    + "its dialog says so, no lists (see 17g)",
                    !GameChanges.For(plain).Any && noneAt > 0 && noneParsed[noneAt] == $"{term}: {none}" && noneParsed[noneAt - 1].StartsWith(Menus.Text("SPIRE_MOD_MANAGER.OTHER_KEYWORDS") + ": ")
                    && description.Text.Contains($"[url={ModifiedContent.Meta}][gold]{term}[/gold]: [color=#fff6e299]{none}[/color][/url]")
                    && noneCard == string.Join("\n", expected) + "\n\n" + hint && noneList.Split('\n').Take(2).SequenceEqual([Menus.Text("SPIRE_MOD_MANAGER.MODIFIED_AREAS"), none])
                    && !noneList.Contains(Menus.Text("SPIRE_MOD_MANAGER.GAME_FILES")) && !noneList.Contains(Menus.Text("SPIRE_MOD_MANAGER.GAME_CODE")),
                    $"\"{(noneAt >= 0 ? noneParsed[noneAt] : "no row")}\"; card \"{noneCard.ReplaceLineEndings(" / ")}\"; dialog \"{noneList.ReplaceLineEndings(" / ")}\"");
            }
            else
                Results.Add("SKIP modified content with nothing found: test mods not installed");
            content.RemoveChild(row);
            row.QueueFree();
        }
        finally
        {
            GameChanges.TestAssemblies = null;
            Hover.TestMouse = null;
            ModifiedContent.Close();
            GameForgets(mod);
            SaveManager.Instance.SettingsSave.ModSettings?.ModList.RemoveAll(e => e.Id == id);
            History.Record();
            CategoryList.Redraw(content);
            Compat.Clear(info);
            await Wait(menu, 0.3);
        }
    }

    // The first `count` areas, then how many more, in the strings' words.
    static string AreasListed(IReadOnlyList<string> areas, int count, string? first = null)
    {
        var listed = string.Join(", ", areas.Skip(1).Take(count - 1).Prepend(first ?? areas[0]));
        return (areas.Count - count) switch
        {
            0 => listed,
            1 => Draft(Template("SPIRE_MOD_MANAGER.MODIFIED_MORE_ONE"), ("areas", listed)),
            var more => Draft(Template("SPIRE_MOD_MANAGER.MODIFIED_MORE"), ("areas", listed), ("count", $"{more}")),
        };
    }

    // How the panel's Modified content row (shown: its line of the panel's text; counts: " (...)" after the areas) lists
    // the areas: the whole ones it shows, or (Whole 0) how many of the first's what ("The Regent (energy orb, hands, …)").
    // Most: one more (area, or what) would run past the line (the label's room less a scrollbar's, there or not), shaped
    // in the panel's font. OneLine: its first and last letters on one line, no wider than the label's room.
    static (int Whole, int Items, bool Most, bool OneLine) ModifiedFit(RichTextLabel description, string shown, IReadOnlyList<string> areas, string counts)
    {
        var term = Menus.Text("SPIRE_MOD_MANAGER.MODIFIED_CONTENT");
        var (font, size) = (description.GetThemeFont("normal_font"), description.GetThemeFontSize("normal_font_size"));
        float Width(string text)
        {
            using var line = new TextLine();
            line.AddString(text, font, size);
            return line.GetLineWidth();
        }
        var room = description.Size.X - description.GetThemeStylebox("normal").GetMinimumSize().X;
        var lineRoom = room - description.GetVScrollBar().GetCombinedMinimumSize().X - 2;
        var start = shown.Length > 0 ? description.GetParsedText().IndexOf(shown, StringComparison.Ordinal) : -1;
        var at = start >= 0 ? description.GetCharacterLine(start) : -1;
        var oneLine = at >= 0 && description.GetCharacterLine(start + shown.Length - 1) == at && description.GetLineWidth(at) <= room;
        string Row(int count, string? first = null) => $"{term}: {AreasListed(areas, count, first)}{counts}";
        var whole = Enumerable.Range(1, areas.Count).FirstOrDefault(n => shown == Row(n));
        if (whole > 0)
            return (whole, 0, whole == areas.Count || Width(Row(whole + 1)) > lineRoom, oneLine);
        var open = areas.Count > 0 ? areas[0].IndexOf(" (", StringComparison.Ordinal) : -1;
        if (open <= 0)
            return (0, 0, false, oneLine);
        var (subject, whats) = (areas[0][..open], areas[0][(open + 2)..^1].Split(", "));
        string Cut(int kept) => Row(1, $"{subject} ({string.Join(", ", whats.Take(kept))}, …)");
        var items = Enumerable.Range(1, whats.Length - 1).FirstOrDefault(k => shown == Cut(k));
        return (0, items, items > 0 && Width(Row(1)) > lineRoom && (items == whats.Length - 1 || Width(Cut(items + 1)) > lineRoom), oneLine);
    }

    // A point on a row of the panel's text (its line, near its start), for a card's mouse.
    static Vector2 RowPoint(RichTextLabel description, string row)
    {
        var start = row.Length > 0 ? description.GetParsedText().IndexOf(row, StringComparison.Ordinal) : -1;
        var line = start >= 0 ? description.GetCharacterLine(start) : -1;
        return description.GetGlobalRect().Position
            + new Vector2(120, line >= 0 ? description.GetLineOffset(line) + description.GetLineHeight(line) / 2 - (float)description.GetVScrollBar().Value : 0);
    }

    // A Godot pack (format v3, as GameChanges reads one) listing these paths, each with no content.
    static void WritePack(string file, string[] paths)
    {
        using var w = new BinaryWriter(File.Create(file));
        w.Write("GDPC"u8.ToArray());
        w.Write(3); // format
        w.Write(4); w.Write(5); w.Write(1); // engine version
        w.Write(0); // flags
        w.Write(0L); // where the files start
        var directoryAt = w.BaseStream.Position + 8 + 16 * 4;
        w.Write(directoryAt);
        for (var i = 0; i < 16; i++)
            w.Write(0); // reserved
        w.Write(paths.Length);
        foreach (var path in paths)
        {
            var name = System.Text.Encoding.UTF8.GetBytes(path);
            var padded = (name.Length + 3) / 4 * 4;
            w.Write(padded);
            w.Write(name);
            w.Write(new byte[padded - name.Length]);
            w.Write(0L); // offset
            w.Write(0L); // size
            w.Write(new byte[16]); // MD5
            w.Write(0); // flags
        }
    }

    // Another mod's part in the info panel (Undo And Restart adds a settings panel at a fixed (0,318), over our
    // fields; here a made-up one the same size): our views put it between the details and our fields, or out of sight
    // when there's no room (never hidden: its Visible is its mod's); Vanilla View leaves it where its mod put it. Its
    // mod's own hide holds, in a fill or outside one; its show too when there's room; and behind the changes list too.
    // Removed again afterwards.
    static async Task TestOtherModPanel(Node menu, NModdingScreen screen, Control content, string outDir)
    {
        if (content.GetChildren().OfType<NModMenuRow>().FirstOrDefault(r => r.Mod?.manifest?.id == "SmmTestUi") is not { Mod: { } mod } row)
        {
            Results.Add("SKIP another mod's panel: test mods not installed");
            return;
        }
        var info = screen.GetNode<NModInfoContainer>("%ModInfoContainer");
        CategoryList.Select(screen, row); // a short description and no picture, so it fits
        await PanelFor(menu, screen, row);
        await Wait(menu, 0.3);
        var home = new Vector2(0, 318);
        var other = new PanelContainer { Name = "OtherModSettingsPanel", Position = home, CustomMinimumSize = new Vector2(560, 174) };
        info.AddChild(other);
        var (details, yours) = (info.GetNode<Control>("ModDescription"), info.GetNode<Control>("SpireModManagerYours"));
        // shownByItsMod: what its mod sets in its own Fill postfix.
        async Task<string> Refill(bool? shownByItsMod = null)
        {
            info.Fill(mod); // the panel filled again, as when a mod is picked
            if (shownByItsMod is { } shown)
                other.Visible = shown;
            await Wait(menu, 0.3);
            return $"panel {other.GetGlobalRect()} shown {other.Visible}, details {details.GetGlobalRect()}, fields {yours.GetGlobalRect()}";
        }
        // No room: above the screen and out of keyboard reach, still shown as far as its mod can tell.
        bool OutOfSight() => other.Visible && other.GetGlobalRect().End.Y < 0 && other.FocusBehaviorRecursive == Control.FocusBehaviorRecursiveEnum.Disabled;

        var where = await Refill();
        Check("another mod's panel: the only part counted as another mod's (not the game's, ours or Better Mod Menu's)",
            Layout.OtherParts(info).SequenceEqual([other]), string.Join(", ", Layout.OtherParts(info).Select(c => c.Name)));
        var (panel, text, fields) = (other.GetGlobalRect(), details.GetGlobalRect(), yours.GetGlobalRect());
        Check("another mod's panel goes between the details and our fields, overlapping neither", other.Visible
            && !panel.Intersects(text) && !panel.Intersects(fields) && panel.Position.Y >= text.End.Y && panel.End.Y <= fields.Position.Y, where);
        await Capture(screen, outDir, "17-other-mod-panel");

        other.CustomMinimumSize = new Vector2(560, 600);
        where = await Refill();
        var parkedLine = info.GetNodeOrNull<Label>(ModDetails.OthersName);
        Check("another mod's panel with no room for it: out of sight, a line under the details says so, our fields right under it", OutOfSight()
            && parkedLine is { Visible: true } && parkedLine.Text == "1 more from other mods: see the vanilla Modding page"
            && Math.Abs(parkedLine.OffsetTop - details.OffsetBottom - ModDetails.FieldsGap) < 1
            && Math.Abs(yours.OffsetTop - parkedLine.OffsetBottom - ModDetails.FieldsGap) < 1, $"{where}; line \"{parkedLine?.Text}\" shown {parkedLine?.Visible}");
        var inVanilla = "";
        Check("another mod's panel in Vanilla View: back where its mod put it, shown, in keyboard reach", await WithVanillaView(screen, menu, () =>
        {
            inVanilla = $"{other.Position}, shown {other.Visible}, focus {other.FocusBehaviorRecursive}";
            return other.Position == home && other.Visible && other.FocusBehaviorRecursive == Control.FocusBehaviorRecursiveEnum.Inherited;
        }), inVanilla);
        Check("another mod's panel back on our views: out of sight again", OutOfSight(), $"{other.GetGlobalRect()}, shown {other.Visible}");

        // Out of sight for lack of room, then hidden by its mod: outside a fill (a setting of its own, say), then in one
        // (another mod picked). Each time room comes back without a fill (as when the changes list closes), its mod's
        // hide holds. Its mod showing it again, with room: shown, in its place.
        foreach (var inFill in new[] { false, true })
        {
            other.CustomMinimumSize = new Vector2(560, 600);
            await Refill(shownByItsMod: true);
            if (inFill)
                await Refill(shownByItsMod: false);
            else
                other.Visible = false;
            other.CustomMinimumSize = new Vector2(560, 174);
            other.ResetSize();
            ModDetails.PlaceFields(info);
            Check($"another mod's panel out of sight, then hidden by its mod {(inFill ? "in" : "outside")} a fill: still hidden with room again",
                !other.Visible && !Layout.OtherParts(info).Contains(other), $"shown {other.Visible}, size {other.Size}");
        }
        where = await Refill(shownByItsMod: true);
        Check("another mod's panel shown again by its mod, with room: shown between the details and our fields, in keyboard reach", other.Visible
            && other.GetGlobalRect().Position.Y >= details.GetGlobalRect().End.Y && other.GetGlobalRect().End.Y <= yours.GetGlobalRect().Position.Y
            && other.FocusBehaviorRecursive == Control.FocusBehaviorRecursiveEnum.Inherited, where);

        // Behind the changes list: out of sight too (never hidden), so what its mod sets while the list is open holds
        // when it closes. Its mod hides it: still hidden after. Its mod shows it again: shown in its place after. An
        // overlay of another mod's (stretching over the panel) the same way, back over the panel when the list closes.
        var place = other.GetGlobalRect();
        var overlay = new Control { Name = "OtherModOverlay", MouseFilter = Control.MouseFilterEnum.Ignore };
        info.AddChild(overlay);
        overlay.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        var covers = overlay.GetGlobalRect();
        bool Behind(Control part) => part.GetGlobalRect().End.Y < 0 && part.FocusBehaviorRecursive == Control.FocusBehaviorRecursiveEnum.Disabled;
        bool InPlace(Control part, Rect2 rect) => part.GetGlobalRect().Position.DistanceTo(rect.Position) < 1 && part.GetGlobalRect().Size.DistanceTo(rect.Size) < 1
            && part.FocusBehaviorRecursive == Control.FocusBehaviorRecursiveEnum.Inherited;
        string Seen() => $"panel {other.GetGlobalRect()} shown {other.Visible} focus {other.FocusBehaviorRecursive} (was {place}), overlay {overlay.GetGlobalRect()} (was {covers})";
        HistoryBar.ShowList(screen);
        await Wait(menu, 0.2);
        Check("another mod's panel behind the changes list: out of sight and keyboard reach, shown as far as its mod can tell; its overlay too",
            HistoryBar.ListOpen(info) && other.Visible && Behind(other) && overlay.Visible && Behind(overlay), Seen());
        ModDetails.PlaceFields(info); // as a resize does while the list is open
        Check("behind the changes list: no \"more from other mods\" line over it", info.GetNodeOrNull<Control>(ModDetails.OthersName) is not { Visible: true });
        other.Visible = false; // its mod hides it
        HistoryBar.CloseList(info);
        await Wait(menu, 0.2);
        Check("another mod's panel hidden by its mod while the changes list was open: still hidden when it closes; its overlay back over the panel",
            !HistoryBar.ListOpen(info) && !other.Visible && InPlace(other, place) && overlay.Visible && InPlace(overlay, covers), Seen());
        HistoryBar.ShowList(screen);
        await Wait(menu, 0.2);
        other.Visible = true; // its mod shows it again
        await Wait(menu, 0.2);
        Check("another mod's panel shown by its mod while the changes list is open: still out of sight", HistoryBar.ListOpen(info) && other.Visible && Behind(other), Seen());
        HistoryBar.CloseList(info);
        await Wait(menu, 0.2);
        Check("and when the list closes: shown in its place, in keyboard reach", !HistoryBar.ListOpen(info) && other.Visible && InPlace(other, place), Seen());
        info.RemoveChild(overlay);
        overlay.QueueFree();

        info.RemoveChild(other);
        other.QueueFree();
        Compat.Clear(info);
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
        if (Row("SmmTestFixes") is not { } fixes || Uncategorized().Count < 3)
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
        Check("Ctrl-click lets go of one; the bar counts the rest", picked.Count == all - 1 && !picked.Contains("SmmTestFixes")
            && (screen.FindChild(PageBar.Name, true, false)?.FindChild("Scope", true, false) as Label)?.Text == $"Selected ({all - 1})");

        var bulk = CategoryList.ShowBulkMenu(content);
        await Wait(menu, 0.3);
        Check("the bulk menu: Editing N selected mods, tags and notes, no alias", bulk != null
            && bulk.FindChild("Editing", true, false) is Label { Text: var editing } && editing == $"Editing {all - 1} selected mods"
            && bulk.FindChild("Tags", true, false) != null && bulk.FindChild("Notes", true, false) != null && bulk.FindChild("Alias", true, false) == null);
        var info = bulk?.FindChild("Info", true, false) as TextureRect;
        // The heading and its i are one hover target, only as wide as the two; it and both terms open one card, at once
        // (read 0.05 s after the mouse arrives): how tags and notes work for all of them.
        const double atOnce = 0.05;
        var group = bulk?.FindChild("Heading", true, false) as Control;
        var editingLabel = group?.GetNodeOrNull<Label>("Editing");
        Check("the bulk menu's heading and its i are one hover target, as wide as the two", group is { MouseFilter: Control.MouseFilterEnum.Pass }
            && editingLabel != null && info != null && group.GetGlobalRect().Encloses(editingLabel.GetGlobalRect()) && group.GetGlobalRect().Encloses(info.GetGlobalRect())
            && group.Size.X <= editingLabel.Size.X + info.Size.X + 9 && info.MouseFilter == Control.MouseFilterEnum.Ignore,
            $"group {group?.GetGlobalRect()}, heading {editingLabel?.GetGlobalRect()}, i {info?.GetGlobalRect()}");
        var infoCard = group == null ? "" : (await SizedHoverCard(menu, group, ModField.TipName, () => Capture(screen, outDir, "18c-bulk-info"), atOnce)).Text;
        var bulkTerms = new List<string>();
        foreach (var f in new[] { "Tags", "Notes" })
            if ((bulk?.FindChild("Fields", true, false) ?? bulk)?.GetNodeOrNull<Label>($"{f}/Term/Label") is { } term)
                bulkTerms.Add(await HoverCard(menu, term, ModField.TipName, atOnce));
        var howItWorks = $"{Menus.Text("SPIRE_MOD_MANAGER.TAGS_TERM")}\n{Menus.Text("SPIRE_MOD_MANAGER.TAGS_BULK_HINT")}\n\n"
            + $"{Menus.Text("SPIRE_MOD_MANAGER.NOTES_TERM")}\n{Menus.Text("SPIRE_MOD_MANAGER.NOTES_BULK_HINT")}";
        Check("the bulk menu: its heading and \"i\" and both terms open the same card at once, how tags and notes work for all of them",
            info is { Visible: true, Texture: not null } && infoCard == howItWorks && bulkTerms.Count == 2 && bulkTerms.All(t => t == howItWorks),
            $"info: {infoCard}; terms: {string.Join(" | ", bulkTerms)}");
        await Capture(screen, outDir, "18-bulk-menu");
        var tool = Store.Data.Categories.First().Name;
        if (bulk != null)
            MenuPanel.Press(bulk, tool);
        await Wait(menu, 0.3);
        Check("a category for all of them at once", picked.All(id => Store.InfoFor(id)?.Category == tool) && Store.InfoFor("SmmTestFixes")?.Category == null,
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

        // Notes that differ: a warning beside Notes counts the mods with notes, the ones typing would overwrite; the
        // same notes all round, no warning.
        var notesBefore = Store.InfoFor(picked[0])?.Notes;
        Label? Mixed(Node? bulkMenu) => bulkMenu?.FindChild("Mixed", true, false) as Label;
        var sameNotes = CategoryList.ShowBulkMenu(content);
        await Wait(menu, 0.2);
        var quiet = Mixed(sameNotes) is { Visible: false };
        MenuPanel.Close();
        Store.EditMod(picked[0], info => info.Notes = "Only mine");
        var differing = CategoryList.ShowBulkMenu(content);
        await Wait(menu, 0.2);
        Check("bulk notes: no warning while they're the same; when they differ, one beside Notes counting the mods with notes",
            quiet && Mixed(differing) is { Visible: true, Text: "1 mod has differing notes and will be overwritten!" }
            && (differing?.FindChild("Notes", true, false)?.GetNodeOrNull<TextEdit>("Box")?.PlaceholderText == Menus.Text("SPIRE_MOD_MANAGER.NOTES_BULK_ADD")),
            $"same: {(quiet ? "none" : "shown")}; differ: {Mixed(differing)?.Text ?? "none"}");
        await Capture(screen, outDir, "18b-bulk-notes-differ");
        MenuPanel.Close();
        Store.EditMod(picked[0], info => info.Notes = notesBefore);

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
}
