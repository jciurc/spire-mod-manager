using System.Text.Json.Nodes;
using Godot;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Debug;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;
using MegaCrit.Sts2.Core.Saves;

namespace SpireModManager;

// The self-test's checks of the main list: new mods, notes, search, many mods, Load Order, the page bar, its filters.
static partial class SelfTest
{
    // New mods and profiles. The fixture's Default profile doesn't list the test mods, which
    // are on (the game's default for a new mod): they joined it at launch, on, quietly (no unsaved edit, and so
    // already in the state the session started from, which Changes and Revert all compare with).
    // The page link on our title's baseline, with or without a taller fallback a mod adds to the game's shared fonts (for
    // its own script): the letters don't use it, though each font's own ascent and height grow (Jordan's game, 22 Oct: the
    // link 7 px low at 1440p).
    static void TestPageLinkBaseline(NModdingScreen screen)
    {
        if (screen.GetNodeOrNull<LinkButton>("SpireModManagerViewToggle") is not { } link)
        {
            Check("the page link stays on the title's baseline when a mod adds a taller fallback to the game's fonts", false, "no page link");
            return;
        }
        var fonts = new[] { screen.FindChild("InstalledModsTitle", true, false) is RichTextLabel title ? title.GetThemeFont("normal_font") : null,
            link.GetThemeFont("font") }.OfType<Font>().Distinct().ToList();
        var (linkY, before) = (link.Position.Y, fonts.Select(f => new Godot.Collections.Array<Font>(f.Fallbacks)).ToList());
        var ascent = fonts.Select(f => f.GetAscent(CategoryList.PageLinkSize)).ToList();
        var (tallY, tallAscent) = (0f, new List<float>());
        try
        {
            foreach (var font in fonts)
                font.Fallbacks = new Godot.Collections.Array<Font>(font.Fallbacks) { new FontVariation { BaseFont = ThemeDB.FallbackFont, SpacingTop = 24, SpacingBottom = 12 } };
            Layout.Apply(screen);
            (tallY, tallAscent) = (link.Position.Y, fonts.Select(f => f.GetAscent(CategoryList.PageLinkSize)).ToList());
        }
        finally
        {
            for (var i = 0; i < fonts.Count; i++)
                fonts[i].Fallbacks = before[i];
            Layout.Apply(screen);
        }
        Check("the page link stays on the title's baseline when a mod adds a taller fallback to the game's fonts (the letters don't use it)",
            fonts.Count > 0 && tallAscent.Zip(ascent).All(p => p.First > p.Second + 1) && Math.Abs(tallY - linkY) < 0.5 && Math.Abs(link.Position.Y - linkY) < 0.5,
            $"{fonts.Count} font(s), ascent {string.Join("/", ascent)} -> {string.Join("/", tallAscent)}, link y {linkY} -> {tallY} -> {link.Position.Y}");
    }

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
        // The fixture twins too, copy by copy, as the launch reads our file from before copies had a box each: its legacy
        // pick (B) on, the other off beside it, no copy named.
        if (Copies.TwinGroups().Any(g => g.Id == TwinId))
            Check("... the twins copy by copy: the legacy pick on, the other off beside it, as the launch has them",
                profile.Mods.FirstOrDefault(m => m.Id == TwinId && m.Source == ModSource.ModsDirectory) is { Enabled: true, Copy: null, CopiesOff: ["l:SmmTestTwinA"] },
                string.Join(", ", profile.Mods.Where(m => m.Id == TwinId).Select(m => $"{m.Source} {m.Enabled} {m.Copy} off [{string.Join(",", m.CopiesOff ?? [])}]")));
        var (on, off) = Profiles.UnsavedChanges();
        Check("quietly: no unsaved edit, and already so in the launch state", !Profiles.Edited && ids.All(id => OnInSnapshot(History.Loaded, id)),
            $"edited {Profiles.Edited} (+{string.Join(", +", on.Select(m => m.Name))} -{string.Join(", -", off.Select(m => m.Name))}), in the launch state {ids.Count(id => OnInSnapshot(History.Loaded, id))} of {ids.Length}");
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

    // The fixture gives SpireModManager an alias and notes; SmmTestFixes gets them through the editor.
    static async Task TestNotes(Node menu, NModdingScreen screen, Control content, string outDir)
    {
        var panel = screen.GetNode<NModInfoContainer>("%ModInfoContainer");
        NModMenuRow? Row(string id) => content.GetChildren().OfType<NModMenuRow>().FirstOrDefault(r => r.Mod?.manifest?.id == id);
        string RowTitle(string id) => Row(id)?.GetNode<RichTextLabel>("Title").Text ?? "";

        if (Row(ModEntry.ModId) is not { } ours || Row("SmmTestFixes") is not { } other)
        {
            Results.Add("SKIP notes: fixture mods missing");
            return;
        }
        Check("row shows alias beside the name, and the notepad mark", RowTitle(ModEntry.ModId).StartsWith("The Manager")
            && RowTitle(ModEntry.ModId).Contains("Spire Mod Manager") && ours.GetNodeOrNull<Control>("SpireModManagerEditedMark") is { Visible: true },
            RowTitle(ModEntry.ModId));
        Check("no alias pencil on rows", !ours.HasNode("SpireModManagerPencil"));
        // The notepad marks notes: an alias alone (it shows beside the original name already) puts none.
        if (Row("SmmTestContent") is { } aliasOnly && Store.InfoFor("SmmTestContent")?.Notes == null)
        {
            var aliasBefore = Store.InfoFor("SmmTestContent")?.Alias;
            Store.EditMod("SmmTestContent", info => info.Alias = "Alias only");
            CategoryList.Redraw(content);
            await Wait(menu, 0.3);
            Check("an alias alone puts no notepad on the row", RowTitle("SmmTestContent").StartsWith("Alias only")
                && aliasOnly.GetNodeOrNull<Control>("SpireModManagerEditedMark") is not { Visible: true }, RowTitle("SmmTestContent"));
            Store.EditMod("SmmTestContent", info => info.Alias = aliasBefore);
            CategoryList.Redraw(content);
            await Wait(menu, 0.2);
        }
        // The game's own load errors mark parts with its colour tags ("Mod [red]{id}[/red] only supports..."): the panel
        // shows their text, not the tags.
        if (other.Mod is { } erring)
        {
            var errorsBefore = erring.errors;
            var notesBefore = Store.InfoFor("SmmTestFixes")?.Notes;
            var branchError = new LocString("main_menu_ui", "MOD_ERROR.STEAM_BRANCH_UNSUPPORTED");
            branchError.Add("id", erring.manifest?.id ?? "");
            branchError.Add("supportedBranch", "public");
            branchError.Add("currentBranch", "public-beta");
            erring.errors = [branchError];
            try
            {
                CategoryList.Select(screen, other);
                await PanelFor(menu, screen, other);
                await Wait(menu, 0.5);
                var descriptionLabel = panel.GetNode<RichTextLabel>("ModDescription");
                var (shown, raw) = (descriptionLabel.GetParsedText(), descriptionLabel.Text);
                await Capture(screen, outDir, "17b-game-error-panel");
                // Its row's card in the shots too: the game's box, then the gap before Author; with a note, the card's other
                // gap, before it, to measure that one against and to see them together.
                Store.EditMod("SmmTestFixes", info => info.Notes = "The gap above this note");
                await HoverOver(other.GetNode<Control>("SpireModManagerTitleHover"), Hover.Delay + 0.2);
                await Capture(screen, outDir, "17b2-game-error-card");
                // The box drawn in both (GameText), and in the card one gap after it, as after anything else: from the box's
                // line to Author's as from Author's to the note's (an empty line after the box made it a line more).
                var cardText = CardLabel(screen, "SpireModManagerNotesCard");
                var panelBox = BoxesWrong(descriptionLabel, 1);
                string? cardBox = "no card";
                var (gapAfter, gapBefore, cardGap, lineTall) = (-1f, -1f, 0f, 0f);
                if (cardText != null)
                {
                    cardBox = BoxesWrong(cardText, 1);
                    lineTall = cardText.GetThemeFont("normal_font").GetHeight(cardText.GetThemeFontSize("normal_font_size"));
                    var cardWords = cardText.GetParsedText();
                    int CardLine(string key) => cardWords.IndexOf(Menus.Text(key) + ":") is >= 0 and var at ? cardText.GetCharacterLine(at) : -1;
                    float Between(int from, int to) => cardText.GetLineOffset(to) - cardText.GetLineOffset(from) - cardText.GetLineHeight(from);
                    if (GameText.Drawn(cardText) is [var boxed] && CardLine("SPIRE_MOD_MANAGER.AUTHOR") is > 0 and var authorLine
                        && CardLine("SPIRE_MOD_MANAGER.NOTES_TERM") is > 0 and var noteLine)
                        (gapAfter, gapBefore, cardGap) = (Between(boxed.Line, authorLine), Between(authorLine, noteLine), cardText.GetLineHeight(authorLine - 1));
                }
                await Leave(other.GetNode<Control>("SpireModManagerTitleHover"), Hover.Grace + 0.1);
                Results.Add($"INFO the row's card: {gapAfter} from the box's line to Author's, {gapBefore} from Author's to the note's; "
                    + $"a gap's line {cardGap} tall, a line of text {lineTall}");
                Check("the game's box in the panel and the row's card: an even 2 px border with round corners, 2 px inside its table on every side, "
                    + "no side cut off; in the card, one gap after it, as before the note", panelBox == null && cardBox == null && gapBefore > 0
                    && Math.Abs(gapAfter - gapBefore) <= 2, $"panel: {panelBox}; card: {cardBox}; {gapAfter} after the box, {gapBefore} before the note");
                Hover.TestMouse = null;
                var card = ModDetails.Card(erring); // the row's hover card shows the same (as BBCode: "[lb]" is a "[")
                Check("a game load error shows apart from ours, in the panel and the row's card: under \"Error while loading\", alone in its box, "
                    + "in the game's red (its own [red] in the panel, its colour in the card, no tags as text), not among our problems; its markup read by "
                    + "the game's auto-size", GameReads(raw) && GameReads(card) && shown.Contains("Error while loading") && shown.Contains("only supports Steam branch public") && !shown.Contains("[red]")
                    && raw.Contains(GameText.Caption("Error while loading")) && raw.Contains("[table=3]") && raw.Contains("[red]Mod ")
                    && card.Contains(GameText.Caption("Error while loading")) && card.Contains("[table=3]") && card.Contains("[color=#ff5555]Mod ")
                    && !card.Contains("[lb]red]") && !ModDetails.Problems(erring).Any(p => p.Contains("Steam branch")),
                    $"{shown.Split('\n')[0]} | card: {card.Split('\n').FirstOrDefault(l => l.Contains("branch"))}");
                var gapThenAuthor = $"[/table]\n{ModDetails.CardGap}\n{ModDetails.CardFact("SPIRE_MOD_MANAGER.AUTHOR", "")}";
                Check("the row's card and the API's: a gap after what's wrong, before Author", card.Contains(gapThenAuthor)
                    && ModDetails.SharedCard(erring).Contains(gapThenAuthor), card.Replace("\n", " | "));
                // A row's card has the mod's picture at its top, as the API's card does (where the game's Modding page finds
                // it), and none without one. No fixture mod has one: Test Fixes is lent one, in the game's resource cache,
                // while it has its error, so the box comes after a picture (each counted as the label counts it).
                if (erring.manifest?.id is { } fixesId && ours.Mod is { } plain)
                {
                    // A w x h picture lent to Test Fixes: a block on a frame, to see its edges.
                    ImageTexture Lend(int w, int h)
                    {
                        var art = Image.CreateEmpty(w, h, false, Image.Format.Rgba8);
                        art.Fill(new Color(0.18f, 0.3f, 0.45f));
                        art.FillRect(new Rect2I(w / 10, h / 6, w - w / 5, h - h / 3), new Color(0.85f, 0.62f, 0.32f));
                        var made = ImageTexture.CreateFromImage(art);
                        made.TakeOverPath($"res://{fixesId}/mod_image.png");
                        return made;
                    }
                    // A card as drawn: its text, its first line's size (the picture's) and whether the game's box after it is right.
                    async Task<(string Text, Vector2 First, string? Box)> Drawn(Control trigger, string name, string? shot = null)
                    {
                        await HoverOver(trigger, Hover.Delay + 0.2);
                        if (shot != null)
                            await Capture(screen, outDir, shot);
                        var label = CardLabel(screen, name);
                        var seen = label == null ? ("", Vector2.Zero, "no card") : (label.Text, new Vector2(label.GetLineWidth(0), label.GetLineHeight(0)), BoxesWrong(label, 1));
                        await Leave(trigger, Hover.Grace + 0.1);
                        return seen;
                    }
                    var hotTitle = other.GetNode<Control>("SpireModManagerTitleHover");
                    var apiTrigger = new Control { Name = "SmmPictureCardTrigger", Position = new Vector2(300, 300), Size = new Vector2(200, 30) }; // draws nothing
                    screen.AddChild(apiTrigger);
                    SharedCards.Watch(apiTrigger, fixesId);
                    var lent = Lend(256, 144);
                    try
                    {
                        var (shownCard, _, boxAfterPicture) = await Drawn(hotTitle, "SpireModManagerNotesCard", "17b3-card-thumbnail");
                        var (sharedCard, _, _) = await Drawn(apiTrigger, SharedCards.CardName, "17b4-api-card-thumbnail");
                        var picture = $"[img width=256 height=144]res://{fixesId}/mod_image.png[/img]\n";
                        Check("a row's card shows the mod's picture at its top when it has one, as the API's card does; none without one",
                            shownCard.StartsWith($"{picture}[b]{fixesId}[/b]") && sharedCard.StartsWith(picture) && !ModDetails.Card(plain).Contains("[img"),
                            $"{shownCard.Split('\n')[0]} / {sharedCard.Split('\n')[0]}");
                        Check("the game's box after the mod's picture in a card: drawn on its own line, as without one", boxAfterPicture == null, boxAfterPicture ?? "");
                        // Sized as Content Options' card had it: never scaled up, no wider than the card's text (the row's
                        // 380, the API's 420), no taller than the panel's thumbnail (144), keeping its shape. Drawn so too.
                        var wrong = new List<string>();
                        foreach (var (size, inCard, inShared, shot) in new (Vector2I, Vector2I, Vector2I, string?)[]
                        {
                            (new(1280, 400), new(380, 119), new(420, 131), "17b6-card-wide-picture"), // wide: as wide as the card's text
                            (new(300, 900), new(48, 144), new(48, 144), null), // tall: as tall as the thumbnail
                            (new(64, 40), new(64, 40), new(64, 40), "17b5-card-small-picture"), // small: as it is
                        })
                        {
                            lent.ResourcePath = "";
                            lent = Lend(size.X, size.Y);
                            foreach (var (trigger, name, want, whose) in new[] { (hotTitle, "SpireModManagerNotesCard", inCard, "row"), (apiTrigger, SharedCards.CardName, inShared, "API") })
                            {
                                var (text, first, _) = await Drawn(trigger, name, whose == "row" ? shot : null);
                                if (!text.StartsWith($"[img width={want.X} height={want.Y}]res://{fixesId}/mod_image.png[/img]\n")
                                    || Math.Abs(first.X - want.X) > 1 || Math.Abs(first.Y - want.Y) > 2)
                                    wrong.Add($"{size.X}x{size.Y} in the {whose} card: {text.Split('\n')[0]}, drawn {first.X:0.#}x{first.Y:0.#}");
                            }
                        }
                        Check("a card's picture: a wide one as wide as the card's text, a tall one 144 px high, a small one not scaled up; in the row's card "
                            + "and the API's, drawn at that size", wrong.Count == 0, string.Join("; ", wrong));
                    }
                    finally
                    {
                        lent.ResourcePath = "";
                        apiTrigger.QueueFree();
                    }
                }
            }
            finally
            {
                erring.errors = errorsBefore;
                Store.EditMod("SmmTestFixes", info => info.Notes = notesBefore);
            }
        }
        else
            Results.Add("SKIP a game load error in the panel: the fixture row has no mod");
        // A label's tables keep its gaps (its own, the theme's) but while its text has the game's box: a mod's own [table]
        // keeps them, and a label from a game error's text to another's gets them back (GameText.Watch).
        var tabled = new RichTextLabel { BbcodeEnabled = true, FitContent = true, CustomMinimumSize = new Vector2(400, 0), Text = "[table=2][cell]A[/cell][cell]B[/cell][/table]" };
        tabled.AddThemeConstantOverride("table_h_separation", 7);
        screen.AddChild(tabled);
        string TableGaps() => $"{tabled.GetThemeConstant("table_h_separation")}/{tabled.GetThemeConstant("table_v_separation")}"
            + (tabled.HasThemeConstantOverride("table_v_separation") ? " set" : "");
        var (gapsBefore, ownTable) = (TableGaps(), tabled.Text);
        GameText.Watch(tabled);
        await Wait(menu, 0.1);
        var gapsOwn = TableGaps();
        tabled.Text = GameText.Box("Error");
        await Wait(menu, 0.1);
        var (gapsBoxed, tabledBox) = (TableGaps(), BoxesWrong(tabled, 1));
        tabled.Text = ownTable;
        await Wait(menu, 0.1);
        var gapsBack = TableGaps();
        tabled.QueueFree();
        Check("a label's tables keep its own gaps but while its text has the game's box (none then, the box drawn): a mod's own [table] keeps them, "
            + "and from a game error's text to another's they come back", gapsOwn == gapsBefore && gapsBoxed == "0/0 set" && tabledBox == null && gapsBack == gapsBefore,
            $"{gapsBefore}, watched {gapsOwn}; with a box {gapsBoxed} ({tabledBox}); then {gapsBack}");
        // The game marks every Workshop mod outside its author's branches before it knows which are off (a dev run has
        // no Steam, so these are made up): a mod that's off loses that error and keeps its others, one on keeps it, and
        // the game's message still shows, marked as the game's, in the mod's card and panel.
        Check("our Init drops the Steam branch error of mods that are off", BranchErrors.Ran);
        if (ModManager.Mods.FirstOrDefault(m => m.state == ModLoadState.Disabled) is { } offMod
            && ModManager.Mods.FirstOrDefault(m => m.state == ModLoadState.Loaded && m.manifest?.id != ModEntry.ModId) is { } onMod)
        {
            var (offBefore, onBefore) = (offMod.errors, onMod.errors);
            static LocString Error(string key) => new("main_menu_ui", key);
            LocString Branch(Mod mod)
            {
                var error = Error("MOD_ERROR.STEAM_BRANCH_UNSUPPORTED");
                error.Add("id", mod.manifest?.id ?? "");
                error.Add("supportedBranch", "public");
                error.Add("currentBranch", "public-beta");
                return error;
            }
            offMod.errors = [Branch(offMod), Error("MOD_ERROR.DUPLICATE_ID")];
            onMod.errors = [Branch(onMod)];
            try
            {
                var dropped = BranchErrors.DropFromOffMods([offMod, onMod]);
                var offKeys = offMod.errors?.Select(e => e.LocEntryKey).ToList() ?? [];
                var onKeys = onMod.errors?.Select(e => e.LocEntryKey).ToList() ?? [];
                var card = ModDetails.Card(offMod);
                var gameMessage = $"Mod {offMod.manifest?.id} only supports Steam branch public";
                Check("the game's message taken off a mod that's off still shows in its card, apart from ours under its own caption, in a fainter box, "
                    + "not as a problem",
                    card.Contains(GameText.Caption("Error while loading - Can be ignored if disabled")) && card.Contains(gameMessage)
                    && card.Contains(GameText.Box("|", dim: true).Split('|')[0]) && !ModDetails.Problems(offMod).Any(p => p.Contains("only supports Steam branch")),
                    card.Split('\n').FirstOrDefault(l => l.Contains("branch")) ?? card.Split('\n')[0]);
                // Drawn in a card as the row's is (its width): a lit box for the error it keeps, then a dim one, each on its
                // own line (the first table counted as the label counts it); and a box whose words fill its room, over lines,
                // inside the card (the words' cell padding took it past the edge).
                async Task<string?> BoxesShown(string text, int count)
                {
                    Hover.ShowNow(other.GetNode<Control>("SpireModManagerTitleHover"), "SpireModManagerNotesCard", text, 380);
                    await WaitReal(menu, 0.2);
                    var wrong = CardLabel(screen, "SpireModManagerNotesCard") is { } boxCard ? BoxesWrong(boxCard, count)
                        ?? (count == 2 && !GameText.Drawn(boxCard).Select(b => b.Dim).SequenceEqual([false, true]) ? "not lit, then dim" : null) : "no card";
                    Hover.Hide();
                    return wrong;
                }
                var (twoBoxes, filledBox) = (await BoxesShown(card, 2), await BoxesShown(GameText.Box(string.Join(" ", Enumerable.Repeat("aa bb", 30))), 1));
                Check("in a card: a lit box for the game's error, then a dim one for the message taken off, each drawn on its own line; a box whose words "
                    + "fill its room stays inside the card", twoBoxes == null && filledBox == null, $"two boxes: {twoBoxes ?? "fine"}; filled: {filledBox ?? "fine"}");
                // The second pass, just before the main menu's warning reads the errors (BranchErrorsLatePatch): one
                // marked after our Init goes there too.
                if (menu.GetTree().Root.FindChildren("*", "", true, false).OfType<NDebugInfoLabelManager>().FirstOrDefault() is { } warning)
                {
                    offMod.errors = [Branch(offMod)];
                    HarmonyLib.AccessTools.Method(typeof(NDebugInfoLabelManager), "UpdateText")?.Invoke(warning, [null]);
                    Check("the main menu's warning: a Steam branch error put on a mod that's off after our Init goes before the warning reads it",
                        offMod.errors == null, $"{offMod.errors?.Count} left");
                }
                else
                    Results.Add("SKIP the late pass of the Steam branch error: no main menu warning in the tree");
                offMod.errors = [Error("MOD_ERROR.STEAM_BRANCH_UNSUPPORTED")];
                BranchErrors.DropFromOffMods([offMod]);
                Check("a mod that's off loses the game's Steam branch error (it doesn't load) and keeps its others; one on keeps it; "
                    + "none left is none, as the game keeps it", dropped == 1 && offKeys.SequenceEqual(["MOD_ERROR.DUPLICATE_ID"])
                    && onKeys.SequenceEqual(["MOD_ERROR.STEAM_BRANCH_UNSUPPORTED"]) && offMod.errors == null,
                    $"dropped {dropped}; off: {string.Join(",", offKeys)}; on: {string.Join(",", onKeys)}; then {offMod.errors?.Count}");
            }
            finally
            {
                (offMod.errors, onMod.errors) = (offBefore, onBefore);
                BranchErrors.TestForget(offMod);
            }
        }
        else
            Results.Add("SKIP the Steam branch error of a mod that's off: no fixture mod off, or none on");
        CategoryList.Select(screen, ours);
        await PanelFor(menu, screen, ours);
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
        Check("the panel's note field reads Note", yours.GetNode<Label>("Notes/Term/Label").Text == "Note", yours.GetNode<Label>("Notes/Term/Label").Text);
        var linkText = (panel.FindChild("SpireModManagerLink", true, false) as LinkButton)?.Text ?? "";
        Check("the source line: id (Local), then Enabled", panel.FindChild("SpireModManagerLink", true, false) is LinkButton { Disabled: true }
            && linkText == "SpireModManager (Local)" && (panel.FindChild("SpireModManagerSource", true, false)?.FindChild("Status", true, false) as Label)?.Text == "Enabled", linkText);
        Check("a local mod's source line links nowhere: no external-link mark, and no room kept for one",
            panel.FindChild("SpireModManagerLink", true, false) is LinkButton localLink && LinkMark.Rect(localLink) == null && localLink.CustomMinimumSize.X == 0);
        var headText = panel.GetNodeOrNull<RichTextLabel>("SpireModManagerHead")?.GetParsedText() ?? "";
        Check("the head: author and compatibility; the details: description, then size", headText.Contains("Author: symphony") && headText.Contains("Compatibility:")
            && !description.StartsWith("Enabled") && description.Contains("Size:") && !description.Contains("Compatibility"), $"{headText} / {description[..Math.Min(60, description.Length)]}");
        // Other keywords: right under Modified content, which a local mod (no Workshop tags) has right after Size (Jordan,
        // 23 Oct: the two swapped places), the hidden tags that find the mod as #name, in their order; the term in gold and
        // the keywords in the panel text's colour, as the other facts; a click on one searches it as a Workshop tag's does,
        // a second clears it; none in the game's panel (Vanilla View).
        var keywordsLabel = Menus.Text("SPIRE_MOD_MANAGER.OTHER_KEYWORDS");
        var facts = description.Split('\n');
        var keywordsAt = Array.FindIndex(facts, l => l.StartsWith(keywordsLabel + ": "));
        var keywords = $"{keywordsLabel}: {string.Join(", ", HiddenTagNames.Where(t => Search.Matches(ours.Mod, t)).Select(t => t.TrimStart('#')))}";
        Check("Size, then Modified content, then Other keywords: the hidden tags that find the mod, bare names with no \"#\", in their order (this mod: enabled, "
            + "local, installed, loaded... renamed, note)", keywordsAt > 1 && facts[keywordsAt - 2].StartsWith(Menus.Text("SPIRE_MOD_MANAGER.SIZE") + ": ")
            && facts[keywordsAt - 1].StartsWith(Menus.Text("SPIRE_MOD_MANAGER.MODIFIED_CONTENT") + ": ") && facts[keywordsAt] == keywords
            && keywords.StartsWith($"{keywordsLabel}: enabled, local, installed, loaded, ") && keywords.Contains("renamed, note") && !keywords.Contains('#')
            && !keywords.Split(", ").Contains("notes"), $"{(keywordsAt < 0 ? "no row" : facts[keywordsAt])}; wanted {keywords}");
        var panelText = panel.GetNode<RichTextLabel>("ModDescription");
        var marked = panelText.Text.Split('\n');
        var keywordsRow = marked.FirstOrDefault(l => l.Contains(keywordsLabel)) ?? "";
        var sizeTerm = $"[gold]{Menus.Text("SPIRE_MOD_MANAGER.SIZE")}[/gold]: ";
        var sizeRow = marked.FirstOrDefault(l => l.StartsWith(sizeTerm)) ?? "";
        Check("Other keywords look like the other facts: the term in gold, as Size's, the keywords in the panel text's colour (none of their own), each name a link",
            sizeRow.Length > 0 && !sizeRow.Contains("[color=") && keywordsRow.StartsWith($"[gold]{keywordsLabel}[/gold]: [url=htag:enabled]enabled[/url], ")
            && !keywordsRow.Contains("[color=") && keywordsRow.EndsWith("[/url]"), $"{keywordsRow} (Size's: {sizeRow})");
        var searchBox = screen.GetNode<LineEdit>("ModsBorder/SpireModManagerSearch");
        panelText.EmitSignal(RichTextLabel.SignalName.MetaClicked, "htag:enabled");
        var searched = searchBox.Text;
        panelText.EmitSignal(RichTextLabel.SignalName.MetaClicked, "htag:enabled");
        await Wait(menu, 0.3);
        Check("a click on an Other keyword searches it, as a Workshop tag's does; a second clears it", searched == "#enabled" && searchBox.Text == "",
            $"\"{searched}\", then \"{searchBox.Text}\"");
        var inVanilla = "";
        await WithVanillaView(screen, menu, () =>
        {
            inVanilla = panelText.Text;
            return true;
        });
        await PanelFor(menu, screen, ours);
        Check("Vanilla View: the game's panel has no Other keywords; ours has them again after", inVanilla.Length > 0 && !inVanilla.Contains(keywordsLabel)
            && !inVanilla.Contains("htag:") && panelText.Text.Contains(keywordsLabel), inVanilla[..Math.Min(80, inVanilla.Length)]);
        Check("fields in order: Alias, Category, Tags, Notes", string.Join(",", yours.GetChildren().Select(c => c.Name.ToString())) == "Alias,Category,Tags,Notes",
            string.Join(",", yours.GetChildren().Select(c => c.Name.ToString())));
        // The category picker drops its list right under it (2 below it, from its left edge) where it fits, otherwise
        // just above it; moved near the bottom of the screen, just above it, not pushed up over it. Moved for the press
        // only, and put back in the same frame.
        var categoryPicker = yours.GetNode<Button>("Category/Picker");
        var screenSize = categoryPicker.GetViewportRect().Size;
        async Task<(string? Misplaced, bool Up)> PickerMenu(float? top = null)
        {
            var home = categoryPicker.Position;
            if (top is { } y)
                categoryPicker.GlobalPosition = new Vector2(categoryPicker.GlobalPosition.X, y);
            var from = categoryPicker.GetGlobalRect().GrowIndividual(0, 2, 0, 2);
            categoryPicker.EmitSignal(BaseButton.SignalName.Pressed);
            categoryPicker.Position = home;
            await Wait(menu, 0.2);
            var open = MenuPanel.Current;
            var size = open?.GetNodeOrNull<Control>("Catcher/Panel")?.Size ?? Vector2.Zero;
            var (up, left) = (from.End.Y + size.Y > screenSize.Y, from.Position.X + size.X > screenSize.X);
            var misplaced = MenuMisplaced(open, from, up, left);
            MenuPanel.Close();
            return (misplaced, up);
        }
        var (ownPlace, ownUp) = await PickerMenu();
        Results.Add($"INFO the category picker's list opened {(ownUp ? "above" : "below")} it");
        Check("the category picker drops its list right under it where it fits, otherwise just above it", ownPlace == null, ownPlace ?? "");
        var (lowPlace, lowUp) = await PickerMenu(screenSize.Y - categoryPicker.Size.Y - 10);
        Check("the category picker near the bottom of the screen: its list opens just above it, not pushed up over it", lowUp && lowPlace == null,
            lowPlace ?? "below it");
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
        await WaitReal(menu, Hover.Delay + 0.15);
        var big = screen.GetNodeOrNull("SpireModManagerPictureCard")?.FindChildren("*", "TextureRect", true, false).OfType<TextureRect>().FirstOrDefault();
        Check("hovering the thumbnail shows the whole picture, at most 800 wide", big != null && big.Size.X == 800 && big.Size.Y == 450, $"{big?.Size}");
        Hover.TestMouse = () => new Vector2(5, 5);
        await WaitReal(menu, Hover.WindDown + Hover.Grace + 0.1); // the picture card gone and its grace over: the title's card below waits the delay
        Hover.TestMouse = null;
        image.Texture = null;
        Layout.PlaceInfo(panel);
        Check("no picture: the description starts under the head; with one, under the picture", description0 < 160 && withPicture >= 16 + ModDetails.ThumbHeight,
            $"{description0} / {withPicture}");
        await Capture(screen, outDir, "8-details-with-notes");

        // Notes and tags typed in the panel save when focus leaves; notes are trimmed, tags tidied.
        CategoryList.Select(screen, other);
        await PanelFor(menu, screen, other);
        await Wait(menu, 0.5);
        var keywordsBefore = panel.GetNode<RichTextLabel>("ModDescription").Text;
        notes.Text = "  Typed in the panel \n";
        notes.EmitSignal(Control.SignalName.FocusExited);
        var tags = Line("Tags");
        tags.Text = "co-op, Favorites, co-op";
        tags.EmitSignal(Control.SignalName.FocusExited);
        await Wait(menu, 0.3);
        Check("panel saves notes, trimmed", Store.InfoFor("SmmTestFixes")?.Notes == "Typed in the panel" && notes.Text == "Typed in the panel", notes.Text);
        var keywordsAfter = panel.GetNode<RichTextLabel>("ModDescription").Text;
        Check("Other keywords follow a save in the panel: note once it has a note", !keywordsBefore.Contains("[url=htag:note]note[/url]")
            && keywordsAfter.Contains("[url=htag:note]note[/url]"),
            $"before \"{keywordsBefore[Math.Max(0, keywordsBefore.Length - 160)..]}\", after \"{keywordsAfter[Math.Max(0, keywordsAfter.Length - 160)..]}\"");
        Check("panel saves tags, repeats dropped; at rest they're pills", Store.InfoFor("SmmTestFixes")?.Tags?.SequenceEqual(["co-op", "Favorites"]) == true
            && yours.GetNode<Control>("Tags/CustomTags") is { Visible: true } && yours.GetNode("Tags/CustomTags/Pills").GetChildCount() == 2,
            string.Join(", ", Store.InfoFor("SmmTestFixes")?.Tags ?? []));
        // A click on the pills opens the box ready for another tag: ", " after them and the caret after that (it was at
        // the start), so the suggestions don't take the last tag for the word being typed.
        // Clicking away leaves the tags as they were.
        yours.GetNode<Control>("Tags/CustomTags").EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false });
        await Wait(menu, 0.1);
        Check("a click on the tag pills opens the box ready for another tag", tags.Visible && tags.HasFocus() && tags.Text == "co-op, Favorites, " && tags.CaretColumn == tags.Text.Length
            && !TagSuggestions.Labels().Any(t => t.Contains("Favorites", StringComparison.OrdinalIgnoreCase)),
            $"\"{tags.Text}\" caret {tags.CaretColumn}, focus {tags.HasFocus()}, offered {string.Join(", ", TagSuggestions.Labels())}");
        // The look of the tags while they're typed (TagPills): three pills and a word being typed, then as they were.
        void Typed(string text)
        {
            tags.Text = text;
            tags.CaretColumn = text.Length;
            tags.EmitSignal(LineEdit.SignalName.TextChanged, text);
        }
        Typed("co-op, Favorites, Story-heavy, wor");
        await Wait(menu, 0.2);
        Results.Add($"INFO tag pills drawn: {string.Join(", ", TagPills.TestPills(tags).Select(p => $"{p.Tag} {p.Rect}"))}; text: "
            + string.Join(" | ", TagPills.TestInk(tags).Select(r => $"\"{r.Text}\" {r.Color} {r.Size}px")));
        await Capture(screen, outDir, "9a-tags-editing");
        tags.CaretColumn = 10; // in "Favorites": that pill at the box's size, the others smaller
        await Wait(menu, 0.1);
        await Capture(screen, outDir, "9a-tags-caret-in-pill");
        Typed("co-op, Favorites, ");
        tags.ReleaseFocus();
        await Wait(menu, 0.3);
        Check("clicking away after that keeps the tags as pills", Store.InfoFor("SmmTestFixes")?.Tags?.SequenceEqual(["co-op", "Favorites"]) == true
            && tags.Text == "co-op, Favorites" && yours.GetNode<Control>("Tags/CustomTags").Visible && !tags.Visible,
            $"\"{tags.Text}\" / {string.Join(", ", Store.InfoFor("SmmTestFixes")?.Tags ?? [])}");
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
        Check("Cancel puts the notes back", notes.Text == "Typed in the panel" && Store.InfoFor("SmmTestFixes")?.Notes == "Typed in the panel", notes.Text);
        var alias = Line("Alias");
        alias.Text = "Fixes";
        alias.EmitSignal(Control.SignalName.FocusExited);
        await Wait(menu, 0.4);
        Check("the alias field saves, the row follows", Store.InfoFor("SmmTestFixes")?.Alias == "Fixes" && RowTitle("SmmTestFixes").StartsWith("Fixes"));

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
            await WaitReal(menu, seconds);
        }
        // The mouse at a given point over a hover area (not its centre), as the area sees it coming in.
        async Task HoverAt(Control target, Vector2 at, double seconds)
        {
            Hover.TestMouse = () => at;
            target.EmitSignal(Control.SignalName.MouseEntered);
            await WaitReal(menu, seconds);
        }
        async Task Leave(Control target, double seconds)
        {
            Hover.TestMouse = () => new Vector2(5, 5);
            target.EmitSignal(Control.SignalName.MouseExited);
            await WaitReal(menu, seconds);
        }
        var hot = other.GetNode<Control>("SpireModManagerTitleHover");
        var mark = other.GetNode<Control>("SpireModManagerEditedMark");
        var markHot = other.GetNode<Control>(RowTitles.MarkHotName); // the notepad's hover area, over it (the notepad takes no mouse)
        var oursHot = ours.GetNode<Control>("SpireModManagerTitleHover");
        Check("the notepad sits left of the name, in the room before the title", mark.Position.X + mark.Size.X <= other.GetNode<Control>("Title").Position.X && mark.Position.X >= 0,
            $"mark at {mark.Position.X}, title text {other.GetNode<Control>("Title").Position.X}..+{hot.Size.X}");
        await HoverOver(hot, Hover.Delay - 0.15);
        Check("the title's card waits for the delay", !CardShown());
        await WaitReal(menu, 0.3);
        Check("after the delay, the title's card: id and state first", CardShown() && CardText().StartsWith("SmmTestFixes") && CardText().Contains("Enabled"), CardText());
        await Capture(screen, outDir, "9b-notes-hover");
        var fixesCard = other.Mod is { } fixes ? ModDetails.Card(fixes) : "";
        Check("the row's card ends with the player's alias, tags and note, together after one gap, the note on its term's line",
            fixesCard.EndsWith(string.Join("\n", "", ModDetails.CardGap, ModDetails.CardFact("SPIRE_MOD_MANAGER.ALIAS_TERM", "Fixes"),
                ModDetails.CardFact("SPIRE_MOD_MANAGER.TAGS_TERM", "co-op, Favorites"), ModDetails.CardFact("SPIRE_MOD_MANAGER.NOTES_TERM", "Typed in the panel")))
            && content.GetChildren().OfType<NModMenuRow>().All(r => r.Mod == null || ModDetails.Card(r.Mod) is var c && !c.EndsWith(ModDetails.CardGap) && !c.Contains($"{ModDetails.CardGap}\n{ModDetails.CardGap}")),
            fixesCard.Replace("\n", " | "));
        Hover.TestMouse = () => new Vector2(5, 5);
        await menu.ToSignal(menu.GetTree(), SceneTree.SignalName.ProcessFrame);
        Hover.TestMouse = () => hot.GetGlobalRect().GetCenter();
        await Wait(menu, 0.1);
        Check("a one-frame wobble off the title keeps the card (wind-down)", CardShown());
        await Leave(hot, 0.1);
        Check("the card goes once the mouse is off the title", !CardShown());
        await HoverOver(oursHot, 0.05);
        Check("within the grace period, the next title's card shows at once", CardShown() && CardText().StartsWith(ModEntry.ModId), CardText());
        await Leave(oursHot, 0.3);
        // The notepad: a plain "Contains note" after the usual delay, not the mod's card (that's the title's), and no
        // grace either way.
        bool TipShown() => screen.HasNode(RowTitles.NoteTipName);
        string TipText() => screen.GetNodeOrNull(RowTitles.NoteTipName)?.FindChildren("*", "RichTextLabel", true, false).OfType<RichTextLabel>()
            .FirstOrDefault()?.GetParsedText() ?? "";
        await HoverAt(markHot, mark.GetGlobalRect().GetCenter(), 0.1); // on the notepad itself
        var early = TipShown() || CardShown();
        await WaitReal(menu, Hover.Delay);
        Check("the notepad: a plain \"Contains note\" after the usual delay, not the mod's card", !early && TipShown() && TipText() == "Contains note"
            && Cards() == 0, $"early {early}, tip \"{TipText()}\", cards {Cards()}");
        await Leave(markHot, 0.1);
        Check("the notepad's tip goes with the mouse", !TipShown());
        await HoverOver(hot, 0.2);
        Check("no grace period after the notepad: the title waits again", !CardShown());
        await Leave(hot, 0.3);

        // The notepad's hover area (issue 124): the notepad is small, so the area reaches a little left of it (no further
        // than the row's edge), above and below (the title's line, as the title's area beside it), and not right, where the
        // title's area starts. The notepad is drawn as before, its own size, and takes no mouse; the area passes clicks on.
        var (markAt, areaAt, titleAt, rowAt) = (mark.GetGlobalRect(), markHot.GetGlobalRect(), hot.GetGlobalRect(), other.GetGlobalRect());
        Check("the notepad's hover area reaches a little left of it, above and below, not right into the title; clicks pass through it",
            markHot.Visible && areaAt.Encloses(markAt) && areaAt.Position.X < markAt.Position.X && areaAt.Position.X >= rowAt.Position.X
            && areaAt.Position.Y < markAt.Position.Y && areaAt.End.Y > markAt.End.Y && Math.Abs(areaAt.End.X - markAt.End.X) < 0.5f
            && areaAt.End.X <= titleAt.Position.X && Math.Abs(areaAt.Position.Y - titleAt.Position.Y) < 0.5f && Math.Abs(areaAt.End.Y - titleAt.End.Y) < 0.5f
            && mark.Size == Icons.Notepad.GetSize() && mark.MouseFilter == Control.MouseFilterEnum.Ignore && markHot.MouseFilter == Control.MouseFilterEnum.Pass,
            $"area {areaAt}, notepad {markAt} ({mark.MouseFilter}), title's area {titleAt}, row {rowAt}");
        // Just left of the notepad, off it but in the area: its "Contains note", as on the notepad.
        Vector2 LeftOfMark() => new((markHot.GetGlobalRect().Position.X + mark.GetGlobalRect().Position.X) / 2, mark.GetGlobalRect().GetCenter().Y);
        var leftOf = LeftOfMark();
        await HoverAt(markHot, leftOf, Hover.Delay + 0.15);
        Check("hovering just left of the notepad shows its \"Contains note\"", areaAt.HasPoint(leftOf) && !markAt.HasPoint(leftOf)
            && TipShown() && TipText() == "Contains note", $"at {leftOf}, area {areaAt}, notepad {markAt}, tip \"{TipText()}\"");
        await Leave(markHot, 0.1);
        // A click there, through the viewport as the game gets a player's (works headless too; only hover needs a window):
        // it lands on the area, which passes it on to the row, selected as by a click anywhere else on it. The game's row
        // takes a click only while the mouse is on it, so first its mouse_entered (a mouse motion doesn't hover headless).
        // The row in view first, as a player's click needs it.
        var listView = content.GetParent<Control>();
        if (!listView.GetGlobalRect().Encloses(markHot.GetGlobalRect()))
        {
            CategoryList.ScrollTo(content, other);
            await Wait(menu, 1.5);
        }
        CategoryList.Select(screen, ours);
        await PanelFor(menu, screen, ours);
        await Wait(menu, 0.3);
        var selectedBefore = RowState.Selected(other);
        var landed = false;
        void Landed(InputEvent e) => landed |= e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true };
        markHot.GuiInput += Landed;
        other.EmitSignal(Control.SignalName.MouseEntered);
        var clickAt = LeftOfMark();
        var inView = listView.GetGlobalRect().HasPoint(clickAt);
        var viewportAt = ViewportPoint(markHot, clickAt);
        foreach (var pressed in new[] { true, false })
            other.GetViewport().PushInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = pressed, Position = viewportAt, GlobalPosition = viewportAt }, true);
        await Wait(menu, 0.3);
        markHot.GuiInput -= Landed;
        other.EmitSignal(Control.SignalName.MouseExited);
        Check("a click just left of the notepad lands on its hover area and still selects the row", !selectedBefore && landed
            && RowState.Selected(other) && !RowState.Selected(ours) && CategoryList.Selected == other.Mod,
            $"at {clickAt} (in the list's view {inView}), selected before {selectedBefore}, landed on the area {landed}, "
            + $"selected after {RowState.Selected(other)} (ours {RowState.Selected(ours)})");
        Hover.TestMouse = null;

        // Where Hover puts a card it places itself (CardMisplaced: exactly there, over neither the trigger nor the mouse,
        // on screen), chosen each time a card opens from where its trigger is then: a probe with a tip, moved between
        // hovers. Mid-screen, below the trigger and right of the mouse; near the bottom, above the trigger instead of
        // pushed back up over it and the mouse; near the right edge, left of the mouse; in the bottom-right corner, both.
        var probe = new Control { Name = "SpireModManagerHoverProbe", Size = new Vector2(200, 40) };
        screen.AddChild(probe);
        Hover.Tip(probe, "Where a card opens");
        var view = probe.GetViewportRect().Size;
        async Task<string?> ProbeAt(Vector2 at, bool up, bool left)
        {
            probe.GlobalPosition = at;
            return CardMisplaced(await HoverCardBox(menu, probe, Hover.TipName), probe, up, left);
        }
        try
        {
            var middle = await ProbeAt(view / 2 - probe.Size / 2, up: false, left: false);
            Check("hover cards: mid-screen, a card opens below its trigger, right of the mouse", middle == null, middle ?? "");
            var bottom = await ProbeAt(new Vector2(100, view.Y - 50), up: true, left: false);
            Check("hover cards: near the bottom, a card opens above its trigger, not pushed up over it and the mouse", bottom == null, bottom ?? "");
            var right = await ProbeAt(new Vector2(view.X - 210, view.Y / 2 - 20), up: false, left: true);
            Check("hover cards: near the right edge, a card opens left of the mouse", right == null, right ?? "");
            var corner = await ProbeAt(view - new Vector2(210, 50), up: true, left: true);
            Check("hover cards: in the bottom-right corner, a card opens above its trigger and left of the mouse", corner == null, corner ?? "");
        }
        finally
        {
            probe.QueueFree();
        }
        // A row's card, on the lowest row in full view: below its title where it fits, otherwise above it.
        var list = content.GetParent<Control>().GetGlobalRect();
        if (content.GetChildren().OfType<NModMenuRow>().Where(r => r.IsVisibleInTree() && r.Mod != null && list.Encloses(r.GetGlobalRect()))
            .MaxBy(r => r.GetGlobalRect().Position.Y)?.GetNodeOrNull<Control>("SpireModManagerTitleHover") is { Visible: true } lowest)
        {
            var box = await HoverCardBox(menu, lowest, "SpireModManagerNotesCard");
            var (title, mouse) = (lowest.GetGlobalRect(), lowest.GetGlobalRect().GetCenter());
            var up = box is { } card && Math.Max(mouse.Y + 20, title.End.Y + Hover.Gap) + card.Size.Y > view.Y;
            var misplaced = CardMisplaced(box, lowest, up, left: false);
            Results.Add($"INFO the lowest row's card opened {(up ? "above" : "below")} its title");
            Check("a row's card near the bottom of the list: below its title where it fits, otherwise above it", misplaced == null, misplaced ?? "");
        }
        else
            Results.Add("SKIP a row's card near the bottom of the list: no row in full view");
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
        NewModPopupPatch.TestReset(); // TestSubscribeMissing's arrival had its popup, maybe within 30 s
        detected?.Invoke(NGame.Instance, [mod]);
        await Wait(menu, 0.3);
        var texts = PopupTexts();
        Check("new mod popup says \"New Mod Added\"", texts.Any(t => t.Contains("New Mod Added")), string.Join(" | ", texts.Where(t => t.Length > 0)));
        Check("new mod popup speaks of a relaunch, not a restart", texts.Any(t => t.Contains("relaunch")) && !texts.Any(t => t.Contains("restart")),
            string.Join(" | ", texts.Where(t => t.Length > 0)));
        CheckMark("new mod popup", NModalContainer.Instance?.OpenModal as Control);
        await Capture(screen, outDir, "10b-new-mod-popup");
        NModalContainer.Instance?.Clear();
        detected?.Invoke(NGame.Instance, [mod]);
        await Wait(menu, 0.3);
        Check("new mod popup: our text, at most one per 30 s", NModalContainer.Instance?.OpenModal == null);
        Check("the profile bar leaves the mod count to the profile list (none beside the name)", screen.FindChild("SpireModManagerProfileBar", true, false) is { } profileBar
            && profileBar.FindChild("ProfileCount", true, false) == null && Profiles.Active is { } listed && Profiles.Details(listed).Contains(Profiles.CountText()),
            Profiles.Active is { } a ? Profiles.Details(a) : "no profile");

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
        var countsBefore = HistoryBar.Counts();
        GameInstalls(fresh);
        await Wait(menu, 0.4);
        NModalContainer.Instance?.Clear();
        Check("a mod installed now is listed as installed, loading after relaunch",
            HistoryBar.Describe().Any(l => l.Contains("Installed: Fresh Mod") && l.Contains("pending relaunch")), string.Join(" | ", HistoryBar.Describe()));
        // It joins the active profile at once as off (the profile was saved before it existed), so its being on is the
        // profile's unsaved edit; Revert all's state has it, off, too. Turned on since launch: +1 on the first line.
        var countsNew = HistoryBar.Counts();
        Check("a mod installed now joins the active profile as off, so its being on is an unsaved edit; it counts as turned on (+1)",
            Profiles.Active?.Mods.Any(m => m.Id == "SmmFreshMod" && !m.Enabled) == true && Profiles.Edited
            && Profiles.UnsavedChanges().On.Any(m => m.Name == "Fresh Mod") && OnInSnapshot(History.Loaded, "SmmFreshMod", enabled: false)
            && countsNew.On == countsBefore.On + 1 && countsNew.Off == countsBefore.Off,
            $"saved off {Profiles.Active?.Mods.Any(m => m.Id == "SmmFreshMod" && !m.Enabled)}, edited {wasEdited} -> {Profiles.Edited}, "
            + $"off in the launch state {OnInSnapshot(History.Loaded, "SmmFreshMod", enabled: false)}, counts {countsBefore} -> {countsNew}");
        Check("a mod installed now gets a New tag, first", Tags.For(fresh).FirstOrDefault()?.Text == "New");
        var asMeant = HiddenTagsAsMeant(content);
        Check("a mod installed now: hidden tags on the list as it is (it #new, #changed and #pending), each finding the rows it means",
            asMeant.Wrong.Count == 0 && new[] { "#new", "#changed", "#pending" }.All(tag => Search.Matches(fresh, tag)),
            $"{string.Join(" | ", asMeant.Wrong)}; found {asMeant.Found}");
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
            var countsOff = HistoryBar.Counts();
            Check("unticking it matches the profile again (no edit), and the counts go back to before it came (no -1)",
                Profiles.Edited == wasEdited && !Profiles.UnsavedChanges().Off.Any(m => m.Name == "Fresh Mod") && countsOff == countsBefore,
                $"edited {wasEdited} -> {Profiles.Edited}, off: {string.Join(", ", Profiles.UnsavedChanges().Off.Select(m => m.Name))}, counts {countsBefore} -> {countsOff}");
            HistoryBar.ShowList(screen);
            await Wait(menu, 0.2);
            await Capture(screen, outDir, "10c-installed-change");
            HistoryBar.CloseList(screen.GetNode<Control>("%ModInfoContainer"));
            // Undone: before the untick the game had no entry for it (on), the profile's edit, +1 again.
            History.Undo();
            await Wait(menu, 0.3);
            Check("undoing the untick: the new mod on again, the profile's unsaved edit again, +1 again", SaveManager.Instance.SettingsSave.ModSettings?.IsModDisabled(fresh) == false
                && Profiles.Edited && HistoryBar.Counts() == countsNew,
                $"disabled {SaveManager.Instance.SettingsSave.ModSettings?.IsModDisabled(fresh)}, edited {wasEdited} -> {Profiles.Edited}, counts {HistoryBar.Counts()}");
            content.RemoveChild(freshRow);
            freshRow.QueueFree();
        }
        else
            Results.Add("FAIL a mod installed now gets a row");
        GameForgets(fresh);
        SaveManager.Instance.SettingsSave.ModSettings?.ModList.RemoveAll(e => e.Id == "SmmFreshMod");
        History.Quietly(data =>
        {
            foreach (var profile in data.Profiles)
            {
                profile.Mods.RemoveAll(m => m.Id == "SmmFreshMod");
                profile.Edited?.RemoveAll(m => m.Id == "SmmFreshMod");
            }
        });
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

    // Row titles by where the mod is from (Jordan, 23 Oct: "workshop ones will stand out a little"), on rows the game makes
    // for made-up mods: a Workshop and a local one it loaded, and one of each it left off. A Workshop title soft blue where
    // a local one is white, and that blue as dim as the game's grey where it greyed one, on the main list and in Load
    // Order; in Vanilla View the game's own colours. Then the two Workshop ones on the page's own list beside the
    // fixture's local rows (screenshot 10e; 10f the plain Duplicate chips beside the others), and a search for state
    // without "#" there (10g).
    static async Task TestWorkshopTitles(Node menu, NModdingScreen screen, Control content, string outDir)
    {
        static Mod Made(string id, string name, ModSource source, ModLoadState state) => new()
        {
            path = "", modSource = source, state = state,
            manifest = new ModManifest { id = id, name = name, author = "Someone Careful" },
        };
        Mod[] made = [Made("SmmTitleCloud", "Test Cloud Saves", ModSource.SteamWorkshop, ModLoadState.Loaded),
            Made("SmmTitleDark", "Test Dark Mode", ModSource.SteamWorkshop, ModLoadState.Disabled),
            Made("SmmTitleLocal", "Title Test Local", ModSource.ModsDirectory, ModLoadState.Loaded),
            Made("SmmTitleLocalOff", "Title Test Local Off", ModSource.ModsDirectory, ModLoadState.Disabled)];
        Compat.SetWorkshopId(made[0], 4800);
        Compat.SetWorkshopId(made[1], 4801);
        var gray = MegaCrit.Sts2.Core.Helpers.StsColors.gray;
        Color[] meant = [RowTitles.WorkshopTitle, RowTitles.WorkshopTitle * gray, Colors.White, gray];
        static Color Title(NModMenuRow row) => row.GetNode<Control>("Title").Modulate;
        static string Say(IEnumerable<NModMenuRow?> rows) => string.Join(", ", rows.Select(r => r == null ? "no row" : $"{r.Mod?.manifest?.name} #{Title(r).ToHtml()}"));
        var box = new VBoxContainer { Name = "SpireModManagerTitleRows", MouseFilter = Control.MouseFilterEnum.Ignore };
        screen.AddChild(box);
        (box.Position, box.Size) = (new Vector2(-3000, 0), new Vector2(1000, 0));
        var listed = new List<NModMenuRow>(); // the Workshop ones' rows on the page's own list
        var searchBox = screen.GetNode<LineEdit>("ModsBorder/SpireModManagerSearch");
        void Type(string text)
        {
            searchBox.Text = text;
            searchBox.EmitSignal(LineEdit.SignalName.TextChanged, text);
        }
        try
        {
            // The game's own rows, from its factory (found by name: a game update may change it).
            var create = typeof(NModMenuRow).GetMethod("Create", [typeof(NModdingScreen), typeof(Mod)]);
            var rows = made.Select(m => create?.Invoke(null, [screen, m]) as NModMenuRow).OfType<NModMenuRow>().ToList();
            foreach (var row in rows)
                box.AddChild(row);
            if (rows.Count != made.Length)
            {
                Results.Add($"SKIP Workshop titles: the game made {rows.Count} of {made.Length} rows");
                return;
            }
            Results.Add($"INFO row titles: the text's own colour #{rows[0].GetNode<RichTextLabel>("Title").GetThemeColor("default_color").ToHtml()}, "
                + $"so a Workshop title shows #{RowTitles.WorkshopTitle.ToHtml(false)}, dim #{(RowTitles.WorkshopTitle * gray).ToHtml(false)}");
            bool AsMeant() => rows.Select(Title).SequenceEqual(meant);
            RowTitles.Apply(rows, vanilla: false);
            var (mainList, mainSaid) = (AsMeant(), Say(rows));
            Click(screen.GetNode<NButton>("SpireModManagerLoadOrderToggle"));
            await Wait(menu, 0.5);
            var inLoadOrder = Layout.Current == Layout.View.LoadOrder;
            RowTitles.Apply(rows, vanilla: false); // as Load Order's redraw styles its rows
            var (loadOrder, loadSaid) = (inLoadOrder && AsMeant(), Say(rows));
            Click(screen.GetNode<NButton>("SpireModManagerLoadOrderToggle"));
            await Wait(menu, 0.5);
            var vanillaSaid = "Better Mod Menu's page";
            var vanilla = Bmm.Present || await WithVanillaView(screen, menu, () =>
            {
                RowTitles.Apply(rows, vanilla: true);
                vanillaSaid = Say(rows);
                return rows.Select(Title).SequenceEqual([Colors.White, gray, Colors.White, gray]);
            });
            RowTitles.Apply(rows, vanilla: false);
            Check("row titles: a Workshop mod's soft blue where a local one's is white, as dim as the game's grey where it greyed it, on the main list and "
                + "in Load Order; in Vanilla View the game's own colours", mainList && loadOrder && vanilla,
                $"main list {mainSaid}; Load Order ({inLoadOrder}) {loadSaid}; Vanilla View {vanillaSaid}; meant {string.Join(", ", meant.Select(c => $"#{c.ToHtml()}"))}");

            // On the page's own list: the game's handler for a mod that arrives, for the two Workshop ones.
            var detected = typeof(NModdingScreen).GetMethod("OnNewModDetected", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var before = content.GetChildren().OfType<NModMenuRow>().ToList();
            foreach (var mod in made[..2])
                detected?.Invoke(screen, [mod]);
            await Wait(menu, 0.4);
            listed.AddRange(content.GetChildren().OfType<NModMenuRow>().Except(before));
            NModMenuRow? Listed(string id) => content.GetChildren().OfType<NModMenuRow>().FirstOrDefault(r => r.Mod?.manifest?.id == id);
            var real = new[] { "SmmTitleCloud", "SmmTitleDark", "SmmTestFixes", "SmmTestRetired" }.Select(Listed).ToList();
            Check("row titles on the page's own list: the Workshop rows soft blue (dim where the game greyed it) beside the local rows' white and grey",
                listed.Count == 2 && real.All(r => r != null) && real.OfType<NModMenuRow>().Select(Title).SequenceEqual(meant), Say(real));
            // Test Dark Mode's box as a mod off at launch has it (made up mid-session, it's on): the shot shows that row.
            if (Listed("SmmTestFixes") is { } fixes)
                CategoryList.ScrollTo(content, fixes);
            if (Listed("SmmTitleDark")?.GetNode<NTickbox>("Tickbox") is { } darkBox)
                darkBox.IsTicked = false;
            await Wait(menu, 0.6);
            await Capture(screen, outDir, "10e-workshop-titles");
            if (Listed("SmmTitleDark")?.GetNode<NTickbox>("Tickbox") is { } darkBack)
                darkBack.IsTicked = true;
            if (content.GetChildren().OfType<NModMenuRow>().FirstOrDefault(r => r.Mod?.manifest?.id == TwinId) is { } twin)
            {
                CategoryList.ScrollTo(content, twin);
                await Wait(menu, 0.6);
                await Capture(screen, outDir, "10f-duplicate-chip");
            }

            // "enabled -workshop": the rows "#enabled -#workshop" shows, the enabled local mods.
            List<NModMenuRow> Shown() => [.. content.GetChildren().OfType<NModMenuRow>().Where(r => r.Visible)];
            Type("enabled -workshop");
            await Wait(menu, 0.3);
            var bare = Shown();
            await Capture(screen, outDir, "10g-search-without-hash");
            Type("#enabled -#workshop");
            await Wait(menu, 0.3);
            var hashed = Shown();
            Check("searching \"enabled -workshop\" on the list shows what \"#enabled -#workshop\" does: the enabled local mods, no Workshop one",
                bare.Count > 0 && bare.SequenceEqual(hashed) && bare.All(r => r.Mod is { } m && Copies.Ticked(m) && m.modSource != ModSource.SteamWorkshop)
                && !bare.Any(listed.Contains) && Listed("SmmTestFixes") is { } shownFixes && bare.Contains(shownFixes),
                $"{string.Join(", ", bare.Select(r => r.Mod?.manifest?.id))} vs {string.Join(", ", hashed.Select(r => r.Mod?.manifest?.id))}");
        }
        finally
        {
            Type("");
            foreach (var row in listed)
            {
                content.RemoveChild(row);
                row.QueueFree();
            }
            if (listed.Count > 0)
                CategoryList.Redraw(content);
            screen.RemoveChild(box);
            box.QueueFree();
        }
    }

    // A mod's description is searched too (Jordan: "contrast" didn't find Art Roller, whose description says it): its
    // words, the whole query without spaces, and quoted text; not as a tag. A mod with no description still searches.
    static void TestDescriptionSearch()
    {
        static Mod Made(string id, string? description, ModSource source = ModSource.ModsDirectory) => new()
        {
            path = "", modSource = source, state = ModLoadState.Loaded,
            manifest = new ModManifest { id = id, name = "Description Test", author = "Someone Careful", description = description },
        };
        // Words at a word's start (any start of one: "recolor"), with punctuation ("per-card"), quoted text as shown (its
        // markup out, [lb] and [rb] as brackets); not inside a word ("ui" in "quick", "olor"), not the markup ("gold") or a
        // picture's path ("banner"), not as a tag.
        string[] queries = ["contrast", "card art", "recolor", "\"art recoloring\"", "per-card", "ui", "gold", "olor", "#contrast", "-contrast", "zzz", "banner", "\"[v2]\""];
        var seen = new List<string>();
        foreach (var mod in new[] { Made("SmmDescTest", "Shared card art [gold]recoloring[/gold]: per-card hue, color, contrast; quick to build. [img]res://pics/banner.png[/img] [lb]v2[rb]"), Made("SmmDescNone", null) })
            seen.Add($"{mod.manifest?.id}: {string.Concat(queries.Select(q => Search.Matches(mod, q) ? "y" : "n"))}");
        // A script written without spaces: anywhere, words can't be told apart.
        var cjk = Made("SmmDescCjk", "卡牌对比度调整");
        seen.Add($"SmmDescCjk: {string.Concat(new[] { "对比", "调整", "卡牌对比度调整", "色" }.Select(q => Search.Matches(cjk, q) ? "y" : "n"))}");
        Check("search finds a mod's description by the starts of its words (not inside one, not its markup, not as a tag); "
            + "anywhere in a script without spaces; one with none still searches",
            seen.SequenceEqual(["SmmDescTest: yyyyynnnnnnny", "SmmDescNone: nnnnnnnnnynnn", "SmmDescCjk: yyyn"]), string.Join(" | ", seen));

        // A Workshop tag is a #tag (the panel's click on one searches that): the mods with it, not ones whose description
        // only says the word, which plain words still find.
        const ulong item = 4400;
        var tagged = Made("SmmDescTagged", null, ModSource.SteamWorkshop);
        Compat.SetWorkshopId(tagged, item);
        WorkshopInfo.TestSet(item, new WorkshopInfo.Details(DateTime.Now, DateTime.Now, ["Cards", "Tools & APIs", "Library"], [], 0, null));
        try
        {
            var says = Made("SmmDescSays", "Adds cards to every act.");
            var found = $"tagged {Search.Matches(tagged, "#cards")}/{Search.Matches(tagged, "#\"tools & apis\"")}/{Search.Matches(tagged, "cards")}/{Search.Matches(tagged, "#library")}, "
                + $"description only {Search.Matches(says, "#cards")}/{Search.Matches(says, "cards")}";
            Check("a Workshop tag is a #tag (its own mods, quoted for spaces), but not one named like an automatic chip (#library: "
                + "mods others need); a word finds it in tags and descriptions alike",
                found == "tagged True/True/True/False, description only False/True", found);
        }
        finally
        {
            WorkshopInfo.TestSet(item, null);
        }
    }

    // AND, OR, NOT and brackets on made-up mods (Workshop tags made up too): OR binds before the space ("#a OR #b #c" is
    // "(#a OR #b) #c"), "|" is OR, and NOT closer still; or/and/not in small letters are words, but operators between #tags,
    // @authors, quotes and groups; quotes are literal; a missing ")" closes at the end, an extra one is nothing, and so is an
    // operator with nothing on one side; in a bare #tag a ")" ends it only while a group is open.
    static void TestSearchGrammar()
    {
        static Mod Made(int i, string name, string author, string? description, List<string> tags)
        {
            var mod = new Mod
            {
                path = "", modSource = ModSource.SteamWorkshop, state = ModLoadState.Loaded,
                manifest = new ModManifest { id = $"SmmGrammarTest{i}", name = name, author = author, description = description },
            };
            Compat.SetWorkshopId(mod, 4500ul + (ulong)i);
            WorkshopInfo.TestSet(4500ul + (ulong)i, new WorkshopInfo.Details(DateTime.Now, DateTime.Now, tags, [], 0, null));
            return mod;
        }
        Mod[] mods = [Made(0, "Rock Stone", "Alice", "Heavy things.", ["Cards", "Two Kinds"]), Made(1, "Mod Fixes", "Bob", "Fight OR Flight, every act.", ["Relics"]),
            Made(2, "Stone Age", "Carl", null, ["Cards", "Relics"])];
        try
        {
            // Each query, and which of the three it finds.
            (string Query, string Finds)[] cases =
            [
                ("#cards OR #relics", "yyy"), ("#cards | #relics", "yyy"), ("#cards #relics", "nny"), ("#cards AND #relics", "nny"),
                ("#cards OR #relics @bob", "nyn"), ("(#cards OR #relics) @bob", "nyn"), ("#cards OR (#relics @bob)", "yyy"), ("@bob OR @alice #cards", "ynn"),
                ("#cards or #relics", "yyy"), ("#cards and #relics", "nny"), ("#cards not #relics", "ynn"), ("rock OR stone", "yny"), ("rock or stone", "nnn"),
                ("\"OR\"", "nyn"), ("\"or\"", "nnn"), ("#cards -(@alice OR @bob)", "nny"), ("-(#relics OR @carl)", "ynn"), ("NOT #relics", "ynn"),
                ("(@alice OR @bob", "yyn"), ("(#cards)) @carl", "nny"), ("#cards OR", "yny"), ("OR #relics", "nyy"), ("#cards AND", "yny"),
                ("#\"two kinds\" OR @carl", "yny"), ("(#cards @alice) OR (#relics @carl)", "yny"), ("stone -(rock)", "nny"), ("#relics)", "nnn"), ("(#relics)", "nyy"),
                ("#\"two kinds", "ynn"), ("@\"alice", "ynn"), ("#\"\" @bob", "nyn"), // a quote still to type runs to the end; an empty one is nothing
            ];
            var wrong = cases.Select(c => (c.Query, c.Finds, Found: string.Concat(mods.Select(m => Search.Matches(m, c.Query) ? "y" : "n"))))
                .Where(c => c.Found != c.Finds).Select(c => $"{c.Query}: {c.Found}, not {c.Finds}").ToList();
            Check($"search grammar: AND, OR, NOT and brackets, keywords in capitals or between terms that aren't words, forgiving ({cases.Length} queries)",
                wrong.Count == 0, string.Join(" | ", wrong));
            string[] empty = ["()", "OR", "NOT", "( OR )", "AND |", "-()"];
            Check("a search of brackets or operators alone is no search", !empty.Any(Search.HasTerms) && Search.HasTerms("#cards OR"),
                string.Join(", ", empty.Where(Search.HasTerms)));
        }
        finally
        {
            for (var i = 0; i < mods.Length; i++)
                WorkshopInfo.TestSet(4500ul + (ulong)i, null);
        }

        // The design's examples, row by row, then the rest of the grammar, on six more made-up mods in a made-up saved list:
        // A "RitsuLib" (Alice; Workshop, marked for stable only, its tag "Cards (Big)"; gameplay), B "Ritsu Cards" (Bob;
        // Workshop, beta only; the game failed it), C "Fight OR Flight" (Carl; a game error; D needs it), D "Stone Tools"
        // (Alice; off), E "Rock Kit" (Bob; needs F) and F "Spare Parts" (Carl; off). On: A, B, C and E. So B (failed), E
        // (needs a mod that's off) and F (that mod) are errors, C a warning; C and F are libraries.
        static Mod Designed(string letter, string name, string author, string? description, ModLoadState state, bool gameplay, params string[] needs) => new()
        {
            path = "", modSource = ModSource.ModsDirectory, state = state,
            manifest = new ModManifest
            {
                id = $"SmmDesign{letter}", name = name, author = author, description = description, affectsGameplay = gameplay,
                dependencies = [.. needs.Select(n => new ModDependency(n))],
            },
        };
        Mod[] designed = [Designed("A", "RitsuLib", "Alice", "Rock and stone.", ModLoadState.Loaded, true),
            Designed("B", "Ritsu Cards", "Bob", "Adds a lib of cards.", ModLoadState.Failed, false), Designed("C", "Fight OR Flight", "Carl", null, ModLoadState.Loaded, false),
            Designed("D", "Stone Tools", "Alice", null, ModLoadState.Disabled, false, "SmmDesignC"),
            Designed("E", "Rock Kit", "Bob", "Rock or stone, your pick.", ModLoadState.Loaded, false, "SmmDesignF"),
            Designed("F", "Spare Parts", "Carl", null, ModLoadState.Disabled, false)];
        (Mod Mod, ulong Item, List<(string, string)> Ranges, List<string> Tags)[] workshop =
            [(designed[0], 4700, [("public", "public")], ["Cards (Big)"]), (designed[1], 4701, [("public-beta", "")], [])];
        foreach (var (mod, item, ranges, tags) in workshop)
        {
            mod.modSource = ModSource.SteamWorkshop;
            Compat.SetWorkshopId(mod, item);
            WorkshopInfo.TestSet(item, new WorkshopInfo.Details(DateTime.Now, DateTime.Now, tags, ranges, 0, null));
        }
        designed[2].errors = [new LocString("main_menu_ui", "MOD_ERROR.DUPLICATE_ID")];
        List<SettingsSaveMod> saved = [.. designed.Select(m => new SettingsSaveMod(m) { IsEnabled = m.manifest?.id is not ("SmmDesignD" or "SmmDesignF") })];
        try
        {
            // Each query, and which of the six it finds.
            (string Query, string Finds)[] table =
            [
                ("ritsu lib", "yynnnn"), ("#beta OR #stable", "yyyyyy"), ("#beta | #stable", "yyyyyy"), ("(#warning OR #error) #enabled", "nyynyn"),
                ("#warning OR #error #enabled", "nyynyn"), ("#warning or #error", "nyynyy"), ("rock or stone", "nnnnyn"), ("\"OR\"", "nnynnn"),
                ("#enabled -(#gameplay OR #library)", "nynnyn"), ("(@Alice OR @Bob", "yynyyn"),
            ];
            // What the examples stand beside: each tag alone and both at once, the two without brackets, an AND of words.
            (string Query, string Finds)[] besides =
            [
                ("#beta", "nyyyyy"), ("#stable", "ynyyyy"), ("#beta #stable", "nnyyyy"), ("#warning", "nnynnn"), ("#error", "nynnyy"),
                ("#warning OR #error", "nyynyy"), ("#enabled", "yyynyn"), ("rock OR stone", "ynnyyn"), ("ritsu", "yynnnn"),
            ];
            // Precedence: NOT before OR before AND; brackets first.
            (string Query, string Finds)[] precedence =
            [
                ("-@alice OR @bob", "nyynyy"), ("NOT @alice OR @bob", "nyynyy"), ("-(@alice OR @bob)", "nnynny"), ("@alice OR @bob #enabled", "yynnyn"),
                ("@alice OR (@bob #enabled)", "yynyyn"), ("@carl @alice OR @bob", "nnnnnn"), ("@carl (@alice OR @bob)", "nnnnnn"), ("(@carl @alice) OR @bob", "nynnyn"),
            ];
            // Keywords and words: capitals anywhere; small letters between two terms that aren't words (#tags, @authors, quotes,
            // groups), and words beside a word ("rock and stone" is three); quotes literal, operators and all.
            (string Query, string Finds)[] keywords =
            [
                ("rock and stone", "ynnnnn"), ("rock AND stone", "ynnnyn"), ("@alice or @bob", "yynyyn"), ("\"Kit\" or \"Fight\"", "nnynyn"),
                ("(@carl) or (@bob)", "nyynyy"), ("#enabled not #library", "yynnyn"), ("#enabled and #gameplay", "ynnnnn"), ("rock or @carl", "nnnnnn"),
                ("rock OR @carl", "ynynyy"), ("\"or\"", "nnnnyn"), ("\"Fight OR Flight\"", "nnynnn"), ("\"#enabled\"", "nnnnnn"), ("\"(rock)\"", "nnnnnn"),
            ];
            // Forgiving: a missing ")" closes at the end, an extra one is nothing, "()" is nothing, an operator with nothing on a
            // side is dropped, a "-" or NOT before nothing is nothing; a bare #tag ends at a ")" only while a group is open, and
            // a quoted one keeps its brackets.
            (string Query, string Finds)[] forgiving =
            [
                ("((@alice OR @bob) -#library", "yynyyn"), ("(@alice) ) OR @bob", "yynyyn"), ("(@alice)) OR @bob", "yynyyn"), ("() @alice", "ynnynn"),
                ("@alice OR ()", "ynnynn"), ("@alice OR", "ynnynn"), ("OR @alice", "ynnynn"), ("@alice AND", "ynnynn"), ("AND @alice", "ynnynn"),
                ("@alice |", "ynnynn"), ("| @alice", "ynnynn"), ("@alice OR OR @bob", "yynyyn"), ("@alice -", "ynnynn"), ("- @alice", "ynnynn"),
                ("@alice NOT", "ynnynn"), ("NOT NOT @alice", "ynnynn"), ("(#library)", "nnynny"), ("(#library", "nnynny"), ("#library)", "nnnnnn"),
                ("(#library) @carl", "nnynny"), ("#\"cards (big)\"", "ynnnnn"), ("(#\"cards (big)\" OR @carl)", "ynynny"),
            ];
            List<string> Wrong((string Query, string Finds)[] cases) => [.. cases
                .Select(c => (c.Query, c.Finds, Found: string.Concat(designed.Select(m => Search.Matches(m, c.Query) ? "y" : "n"))))
                .Where(c => c.Found != c.Finds).Select(c => $"{c.Query}: {c.Found}, not {c.Finds}")];
            List<string> wrongTable, wrongBesides, wrongPrecedence, wrongKeywords, wrongForgiving;
            using (Lookups.Begin(designed, [], saved, Ticks.All))
                (wrongTable, wrongBesides, wrongPrecedence, wrongKeywords, wrongForgiving) = (Wrong(table), Wrong(besides), Wrong(precedence), Wrong(keywords), Wrong(forgiving));
            Check($"search grammar: the design's examples, row by row ({table.Length}), and the tags they're made of alone", wrongTable.Count + wrongBesides.Count == 0,
                string.Join(" | ", wrongTable.Concat(wrongBesides)));
            Check($"search grammar: brackets first, then NOT, then OR, then AND ({precedence.Length} queries)", wrongPrecedence.Count == 0, string.Join(" | ", wrongPrecedence));
            Check($"search grammar: OR, AND and NOT in capitals anywhere, in small letters only between terms that aren't words; quotes literal ({keywords.Length} queries)",
                wrongKeywords.Count == 0, string.Join(" | ", wrongKeywords));
            Check($"search grammar: never an error: a missing or extra \")\", \"()\", a dangling operator, a lone - or NOT, a bracket in a #tag ({forgiving.Length} queries)",
                wrongForgiving.Count == 0, string.Join(" | ", wrongForgiving));
            string[] nothing = ["()", "( )", "OR", "AND", "NOT", "-", "|", "( OR )", "AND |", "-()", "NOT ()", "(((", ")))", "\"", "\"\"", "#\"", "#\"\"", "@\""];
            Check("brackets, operators, a lone - or an empty quote alone: no search, every mod shown",
                !nothing.Any(Search.HasTerms) && nothing.All(q => designed.All(m => Search.Matches(m, q))), string.Join(", ", nothing.Where(Search.HasTerms)));
        }
        finally
        {
            foreach (var (_, item, _, _) in workshop)
                WorkshopInfo.TestSet(item, null);
        }
    }

    // Hidden tags on the fixture's list: each finds inside a redraw (Lookups) what it finds worked out on its own; #enabled
    // the ticked rows and #disabled the others, any case; #installed every row, #missing and #uninstalled none; each the
    // rows it means (HiddenTagsAsMeant); a tag of the player's named like each finds its mod too. Then what each means, on
    // made-up mods in a made-up saved list: A needs C (off) and D (after A), B needs a mod that isn't installed and the
    // game failed it, D has a game error, E is off but loaded, with a tag of its own named "Enabled"; and each name
    // without its "#".
    // Every hidden tag, in HiddenTags' order (the info panel's Other keywords lists them in it).
    static readonly string[] HiddenTagNames = ["#enabled", "#disabled", "#workshop", "#local", "#installed", "#uninstalled", "#missing", "#loaded", "#notloaded",
        "#pending", "#new", "#changed", "#moved", "#edited", "#renamed", "#note", "#uncategorized", "#unmet", "#needed", "#order", "#duplicate", "#clash", "#aside",
        "#managers", "#failed", "#gameerror", "#error", "#warning", "#problem"];

    static void TestHiddenTags(Control content)
    {
        var rows = content.GetChildren().OfType<NModMenuRow>().Select(r => r.Mod).OfType<Mod>().ToList();
        var tags = HiddenTagNames;
        List<string> Answers() => [.. tags.Select(t => $"{t} {string.Concat(rows.Select(m => Search.Matches(m, t) ? "y" : "n"))}")];
        var fresh = Answers();
        List<string> kept;
        using (Lookups.Begin())
            kept = Answers();
        var differ = fresh.Zip(kept).Where(p => p.First != p.Second).Select(p => $"{p.First} -> {p.Second}").ToList();
        Check("hidden tags: inside a redraw, the same mods as worked out one at a time", differ.Count == 0 && fresh.Count == kept.Count, string.Join(" | ", differ));
        bool Finds(string query, Func<Mod, bool> expected) => rows.All(m => Search.Matches(m, query) == expected(m));
        Check("#enabled finds the ticked rows and #Disabled the others; #installed every row, #missing and #UNINSTALLED none",
            rows.Any(Copies.Ticked) && rows.Any(m => !Copies.Ticked(m)) && Finds("#enabled", Copies.Ticked) && Finds("#Disabled", m => !Copies.Ticked(m))
            && Finds("#installed", _ => true) && Finds("#missing", _ => false) && Finds("#UNINSTALLED", _ => false));
        // Each of them on the fixture's rows, by what it means (HiddenTagsAsMeant), and the ones the fixture holds by name:
        // Test Addon needs a mod that isn't installed, the twins share an id (#sameid, the old name, finds them too), this
        // mod has its alias and note, Test Retired is off, every row is local.
        var asMeant = HiddenTagsAsMeant(content);
        bool Has(string tag, params string[] ids) => ids.All(id => rows.Any(m => m.manifest?.id == id && Search.Matches(m, tag)));
        var twins = rows.Where(m => m.manifest?.id == TwinId).ToList();
        Check("hidden tags on the fixture: each finds the rows it means (the box, chips, status, the banners' rules, changes since launch); Test Addon #unmet, "
            + "the twins #duplicate (and #sameid, its old name), this mod #renamed and #note, Test Retired #disabled and #notloaded, every row #local",
            asMeant.Wrong.Count == 0 && (WontLoad() == null || Has("#unmet", "SmmTestAddon")) && twins.Count == 2
            && twins.All(m => Search.Matches(m, "#duplicate") && Search.Matches(m, "#sameid")) && Finds("#sameid", m => Search.Matches(m, "#duplicate"))
            && Has("#renamed", ModEntry.ModId) && Has("#note", ModEntry.ModId) && Has("#disabled", "SmmTestRetired") && Has("#notloaded", "SmmTestRetired")
            && Finds("#local", _ => true), $"{string.Join(" | ", asMeant.Wrong)}; found {asMeant.Found}");
        Results.Add($"INFO hidden tags on the fixture, rows found: {asMeant.Found}");

        // A tag of the player's named like each of them (Test Retired given them all): each finds it too, those it isn't
        // (#enabled, #unmet, #missing...) as well, and so does the bare word, as for any tag. Its own tags back after.
        if (rows.FirstOrDefault(m => m.manifest?.id == "SmmTestRetired") is { } retired)
        {
            var tagsWere = Store.InfoFor(retired)?.Tags;
            var names = tags.Select(t => t[1..]).ToList();
            var hiddenOnly = names.Count(n => !Search.Matches(retired, "#" + n));
            Store.EditMod("SmmTestRetired", info => info.Tags = [.. names.Select(n => char.ToUpperInvariant(n[0]) + n[1..])]);
            try
            {
                var missed = names.Where(n => !Search.Matches(retired, "#" + n) || !Search.Matches(retired, n)).ToList();
                Check($"a tag of the player's named like a hidden one finds its mod too, for each of them ({hiddenOnly} of {names.Count} it isn't otherwise), "
                    + "#tag and bare word alike", hiddenOnly > names.Count / 2 && missed.Count == 0, string.Join(", ", missed));
            }
            finally
            {
                Store.EditMod("SmmTestRetired", info => info.Tags = tagsWere);
            }
        }
        else
            Results.Add("SKIP hidden tags beside a player's tags: Test Retired not installed");

        static Mod Made(string letter, ModLoadState state, ModSource source, params string[] needs) => new()
        {
            path = "", modSource = source, state = state,
            manifest = new ModManifest { id = $"SmmHidden{letter}", name = $"Hidden Test {letter}", dependencies = [.. needs.Select(n => new ModDependency(n))] },
        };
        Mod[] made = [Made("A", ModLoadState.Loaded, ModSource.ModsDirectory, "SmmHiddenC", "SmmHiddenD"),
            Made("B", ModLoadState.Failed, ModSource.SteamWorkshop, "SmmHiddenMissing"), Made("C", ModLoadState.Disabled, ModSource.ModsDirectory),
            Made("D", ModLoadState.Loaded, ModSource.ModsDirectory), Made("E", ModLoadState.Loaded, ModSource.ModsDirectory)];
        Compat.SetWorkshopId(made[1], 4600);
        made[3].errors = [new LocString("main_menu_ui", "MOD_ERROR.DUPLICATE_ID")];
        List<SettingsSaveMod> saved = [.. made.Select(m => new SettingsSaveMod(m) { IsEnabled = m.manifest?.id is not ("SmmHiddenC" or "SmmHiddenE") })];
        // A renamed, B with notes, C in a category the list has, D in one it doesn't: our data, until the end.
        var category = Store.Data.Categories.FirstOrDefault()?.Name ?? "";
        Store.Batch(() =>
        {
            Store.EditMod("SmmHiddenA", info => info.Alias = "Hidden Rock");
            Store.EditMod("SmmHiddenB", info => info.Notes = "A note");
            Store.EditMod("SmmHiddenC", info => info.Category = category);
            Store.EditMod("SmmHiddenD", info => info.Category = "SmmNoSuchCategory");
            Store.EditMod("SmmHiddenE", info => info.Tags = ["Enabled"]);
        });
        try
        {
            (string Query, string Finds)[] cases =
            [
                ("#enabled", "yynyy"), ("#disabled", "nnyny"), ("#workshop", "nynnn"), ("#local", "ynyyy"), ("#installed", "yyyyy"),
                ("#missing", "nnnnn"), ("#loaded", "ynnyy"), ("#notloaded", "nyynn"), ("#pending", "nnnny"), ("#unmet", "yynnn"), ("#needed", "nnynn"),
                ("#order", "ynnnn"), ("#failed", "nynnn"), ("#gameerror", "nnnyn"), ("#error", "yyynn"), ("#warning", "nnnyn"), ("#problem", "yyyyn"),
                ("#renamed", "ynnnn"), ("#note", "nynnn"), ("#notes", "nynnn"), ("#uncategorized", "yynyy"), ("#edited", "yyyyy"), ("#duplicate OR #sameid OR #clash OR #aside OR #managers", "nnnnn"),
                ("#enabled -#local", "nynnn"), ("#problem #local", "ynyyn"), ("(#unmet OR #needed) -#failed", "ynynn"),
            ];
            // Each name alone (Jordan, 23 Oct: "allow keywords to be searched without leading hashtag"), with AND, OR, NOT
            // and "-" too; only the whole name, as a whole tag: "load" isn't #loaded, nor "enable" #enabled (E's own tag
            // has the word).
            (string Query, string Finds)[] bare =
            [
                ("enabled", "yynyy"), ("Disabled", "nnyny"), ("workshop", "nynnn"), ("unmet", "yynnn"), ("enabled -local", "nynnn"), ("problem local", "ynyyn"),
                ("(unmet OR needed) -failed", "ynynn"), ("unmet | gameerror", "yynyn"), ("NOT workshop", "ynyyy"), ("loaded AND #local", "ynnyy"),
                ("load", "nnnnn"), ("notload", "nnnnn"), ("enable", "nnnny"), ("work", "nnnnn"),
            ];
            List<string> Wrong((string Query, string Finds)[] table) => [.. table.Select(c => (c.Query, c.Finds, Found: string.Concat(made.Select(m => Search.Matches(m, c.Query) ? "y" : "n"))))
                .Where(c => c.Found != c.Finds).Select(c => $"{c.Query}: {c.Found}, not {c.Finds}")];
            List<string> wrong, wrongBare, unlike;
            using (Lookups.Begin(made, [], saved, Ticks.All))
                (wrong, wrongBare, unlike) = (Wrong(cases), Wrong(bare), [.. HiddenTagNames.Where(t => made.Any(m => Search.Matches(m, t[1..]) != Search.Matches(m, t)))]);
            Check("hidden tags mean what they say: state, problems (#error, #warning, #problem gather them), our details (#notes still finds what #note does); "
                + "a tag of the player's named like one finds its mods too", wrong.Count == 0, string.Join(" | ", wrong));
            Check($"hidden tags without \"#\": each name alone finds what #name finds ({HiddenTagNames.Length} names), with AND, OR, NOT and \"-\" too; "
                + "a word that only starts a name finds none of its mods", unlike.Count == 0 && wrongBare.Count == 0,
                $"unlike #name: {string.Join(", ", unlike)}; {string.Join(" | ", wrongBare)}");
            // A word that is a name and in a mod's text finds both (as a word that is a tag and in a description does), and
            // the bare word brings the main list's line for a profile's mods that aren't installed, as #missing does.
            var says = new Mod
            {
                path = "", modSource = ModSource.ModsDirectory, state = ModLoadState.Loaded,
                manifest = new ModManifest { id = "SmmHiddenSays", name = "Hidden Test Says", description = "Finds Workshop items for you." },
            };
            var union = $"{Search.Matches(says, "workshop")}/{Search.Matches(says, "#workshop")}/{Search.Matches(made[1], "workshop")}/{Search.Matches(made[1], "\"Workshop\"")}";
            var wants = $"{Search.Wants("missing", "missing", "uninstalled")}/{Search.Wants("Missing mods", "missing")}/{Search.Wants("-missing", "missing")}/{Search.Wants("missingno", "missing")}";
            Check("a word that is a hidden tag's name finds the mods that say it and the ones the tag finds; the word \"missing\" asks for the missing mods' line, "
                + "not left out or as part of a word", union == "True/False/True/False" && wants == "True/True/False/False", $"{union}; wants {wants}");
        }
        finally
        {
            Store.Batch(() =>
            {
                foreach (var m in made)
                    Store.EditMod(m.manifest?.id ?? "", info => (info.Alias, info.Notes, info.Category, info.Tags) = (null, null, null, null));
            });
        }
    }

    // Stable only and Beta only, by the game's own branch rule over a Workshop mod's supported range (made-up mods and
    // details): a chip only on a mod marked for one branch, whose click searches for it (#"Stable only"); #stable and
    // #beta (and the plain words), its Other keywords, find every mod marked for a branch, "#stable -#beta" the ones for
    // stable only, as #"stable only" does. No range, or a local mod: both, no chip.
    static void TestBranchTags()
    {
        string[] queries = ["#stable", "#beta", "#stable -#beta", "stable", "stable -beta", "table", "#\"stable only\"", "#\"beta only\""];
        (string Name, List<(string, string)> Ranges)[] cases =
            [("stable only", [("public", "public")]), ("beta and up", [("public-beta", "")]), ("both", [("public", "public-beta")]), ("no range", [])];
        var seen = new List<string>();
        string Keywords(Mod mod) => string.Join(",", HiddenTags.Of(mod).Where(k => k is "stable" or "beta"));
        for (var i = 0; i < cases.Length; i++)
        {
            var mod = new Mod
            {
                path = "", modSource = ModSource.SteamWorkshop, state = ModLoadState.Loaded,
                manifest = new ModManifest { id = $"SmmBranchTest{i}", name = $"Branch Test {i}", author = "Someone Careful" },
            };
            var id = 4300ul + (ulong)i;
            Compat.SetWorkshopId(mod, id);
            WorkshopInfo.TestSet(id, new WorkshopInfo.Details(DateTime.Now, DateTime.Now, [], cases[i].Ranges, 0, null));
            try
            {
                var chips = string.Join("/", Tags.For(mod).Where(c => c.Text is "Stable only" or "Beta only").Select(c => $"{c.Text} ({c.Search})"));
                var found = string.Concat(queries.Select(q => Search.Matches(mod, q) ? "y" : "n"));
                seen.Add($"{cases[i].Name}: [{chips}] {found} {Keywords(mod)}");
            }
            finally
            {
                WorkshopInfo.TestSet(id, null);
            }
        }
        var local = Dependencies.Find("SmmTestFixes");
        seen.Add($"local: [{string.Join("/", Tags.For(local).Where(c => c.Text is "Stable only" or "Beta only").Select(c => c.Text))}] "
            + string.Concat(queries.Select(q => Search.Matches(local, q) ? "y" : "n")) + $" {Keywords(local!)}");
        Check("Stable only and Beta only by the game's branch rule: a chip on one-branch mods that searches for it; #stable, #beta, #stable -#beta "
            + "and the plain words find them, stable and beta among Other keywords; \"table\" only through a chip's text (as any chip)",
            seen.SequenceEqual(["stable only: [Stable only (#\"Stable only\")] ynyyyyyn stable", "beta and up: [Beta only (#\"Beta only\")] nynnnnny beta",
                "both: [] yynynnnn stable,beta", "no range: [] yynynnnn stable,beta", "local: [] yynynnnn stable,beta"]),
            string.Join(" | ", seen));
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

        var byName = await Shown("test fixes");
        Check("search by name", byName.SequenceEqual(["SmmTestFixes"]), string.Join(", ", byName));
        await Capture(screen, outDir, "10-search");
        // The alias's exact text: its words alone are in other mods' descriptions too ("the … mod manager").
        var byAlias = await Shown("\"The Manager\"");
        Check("search by alias", byAlias.SequenceEqual([ModEntry.ModId]), string.Join(", ", byAlias));
        var byTag = await Shown("favorites");
        Check("search by own tag", byTag.SequenceEqual(["SmmTestFixes"]), string.Join(", ", byTag));
        var libraries = await Shown("library");
        // The libraries: Test Framework, and both copies of the fixture's twin (Test Twin User needs it).
        List<string> Libraries() => ["SmmTestLib", .. ModManager.Mods.Where(m => m.manifest?.id == TwinId).Select(_ => TwinId)];
        Check("search by automatic tag", libraries.Order().SequenceEqual(Libraries().Order()), string.Join(", ", libraries));
        Check("automatic tags, in order", Tags.For(Dependencies.Find("SmmTestLib")).Select(c => c.Text).SequenceEqual(["Local", "Library", "Gameplay"])
            && Tags.For(Dependencies.Find("SmmTestUi")).Select(c => c.Text).SequenceEqual(["Local"]),
            string.Join(", ", Tags.For(Dependencies.Find("SmmTestLib")).Select(c => c.Text)));
        Check("Cosmetic: the Workshop's Cosmetics and Audio categories and art or sound words; not QoL, tools or UI",
            Tags.IsCosmetic(["Cosmetics"]) && Tags.IsCosmetic(["Characters", "Audio"]) && Tags.IsCosmetic(["Sound Effects"]) && Tags.IsCosmetic(["Card Art"])
            && !Tags.IsCosmetic(["QoL", "Tools & APIs", "Utility"]) && !Tags.IsCosmetic(["UI"]) && !Tags.IsCosmetic([]) && !Tags.IsCosmetic(null));
        // And a pack replacing the game's art or sound (Jordan, 25 Oct: a reskin whose tags didn't say so): images, sounds,
        // videos and fonts count, scenes, scripts and data don't. The fixture mods replace none of the game's files.
        string[] art = ["images/packed/character_select/char_select_ironclad.png", "sfx/hit.OGG", "fonts/kreon.ttf", "videos/intro.ogv"],
            notArt = ["scenes/combat/creature.tscn", "scripts/x.gd", "localization/eng/cards.json"];
        Check("Cosmetic from the game's files a pack replaces: art and sound (any case), not scenes, scripts or data; none for the fixture mods",
            art.All(GameChanges.IsArt) && !notArt.Any(GameChanges.IsArt) && !Tags.For(Dependencies.Find("SmmTestLib")).Any(c => c.Text == "Cosmetic"),
            $"art {string.Join(", ", art.Where(p => !GameChanges.IsArt(p)))}; not art {string.Join(", ", notArt.Where(GameChanges.IsArt))}");
        // Tags clicked on a row: "#Library" alone; Shift adds "#Gameplay" (both needed); "#Local" matches only the tag.
        List<string> Visible() => content.GetChildren().OfType<NModMenuRow>().Where(r => r.Visible).Select(r => r.Mod?.manifest?.id ?? "").ToList();
        void ClickChip(string modId, string tag, bool shift)
        {
            var chip = content.GetChildren().OfType<NModMenuRow>().FirstOrDefault(r => r.Mod?.manifest?.id == modId)?
                .GetNodeOrNull("SpireModManagerStrip")?.GetChildren().OfType<PanelContainer>().FirstOrDefault(c => c.GetChildOrNull<Label>(0)?.Text == tag);
            chip?.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, ShiftPressed = shift });
        }
        var all = await Shown("");
        ClickChip("SmmTestLib", "Library", false);
        await Wait(menu, 0.3);
        Check("clicking a row's tag searches for it", box.Text == "#Library" && Visible().Order().SequenceEqual(Libraries().Order()), $"{box.Text}: {string.Join(", ", Visible())}");
        ClickChip("SmmTestLib", "Gameplay", true);
        await Wait(menu, 0.3);
        Check("Shift-clicking another adds it", box.Text == "#Library #Gameplay" && Visible().Order().SequenceEqual(Libraries().Order()), $"{box.Text}: {string.Join(", ", Visible())}");
        // Clicking again undoes: Shift takes "#Gameplay" back out, then a plain click on the whole search clears it.
        ClickChip("SmmTestLib", "Gameplay", true);
        await Wait(menu, 0.3);
        Check("Shift-clicking a searched tag again removes it", box.Text == "#Library", box.Text);
        ClickChip("SmmTestLib", "Library", false);
        await Wait(menu, 0.3);
        Check("clicking the tag that is the whole search clears it", box.Text == "" && Visible().SequenceEqual(all), $"{box.Text}: {Visible().Count} shown");
        // Authors: from the info panel's author (the row's is plain text); Shift on a tag adds it after a space.
        await Shown("");
        var rowAuthor = content.GetChildren().OfType<NModMenuRow>().FirstOrDefault(r => r.Mod?.manifest?.id == "SmmTestFixes")?.GetNodeOrNull<Label>("SpireModManagerStrip/Author");
        Check("the row's author is plain: just the name, not clickable", rowAuthor is { Text: "symphony", MouseFilter: Control.MouseFilterEnum.Ignore });
        var panelHead = screen.GetNode<Control>("%ModInfoContainer").GetNodeOrNull<RichTextLabel>("SpireModManagerHead");
        Check("the panel's author is a link in the mod names' light blue", panelHead?.Text
            .Contains($"[url=author:symphony][color={ModDetails.ModLinkHex}]symphony[/color][/url]") == true, panelHead?.Text ?? "no head");
        panelHead?.EmitSignal(RichTextLabel.SignalName.MetaClicked, "author:symphony");
        await Wait(menu, 0.3);
        Check("the panel's author searches for their mods", box.Text == "@symphony"
            && Visible().Count > 0 && Visible().All(id => Dependencies.Find(id)?.manifest?.author == "symphony"), $"{box.Text}: {string.Join(", ", Visible())}");
        ClickChip("SmmTestFixes", "Local", true);
        await Wait(menu, 0.3);
        Check("Shift-click on a tag adds it after a space", box.Text == "@symphony #Local", box.Text);
        // The rules every click-to-search shares (row and panel tags, the author, Workshop tags), through the one
        // helper they all use: the search before, the click, the search after.
        string After(string before, Action click)
        {
            box.Text = before;
            click();
            return box.Text;
        }
        void Tag(string tag, bool shift) => SpireModManager.Search.ByTag(content, tag, shift);
        string[] clears = [After(" #library ", () => Tag("Library", false)), After("#\"Library\"", () => Tag("Library", false)),
            After("#\"tools & apis\"", () => Tag("Tools & APIs", false))];
        Check("click-to-search: a click on the whole search clears it (any case, quotes aside)", clears.All(s => s == ""), string.Join(" | ", clears));
        string[] replaces = [After("mod fixes", () => Tag("Library", false)), After("#Library #Gameplay", () => Tag("Library", false))];
        Check("click-to-search: a click replaces any other search", replaces.All(s => s == "#Library"), string.Join(" | ", replaces));
        string[] adds = [After("@symphony", () => Tag("Local", true)), After("mod", () => Tag("Tools & APIs", true))];
        Check("click-to-search: Shift-click adds the term, keeping the rest", adds.SequenceEqual(["@symphony #Local", "mod #\"Tools & APIs\""]), string.Join(" | ", adds));
        string[] removes = [After("@symphony #local mod", () => Tag("Local", true)), After("#Library #\"Tools & APIs\"", () => Tag("tools & apis", true))];
        Check("click-to-search: Shift-click removes a term already searched, any case", removes.SequenceEqual(["@symphony mod", "#Library"]), string.Join(" | ", removes));
        string[] quoted = [After("#\"two words\" @symphony", () => Tag("Two Words", true)),
            After("mod @\"Some One\"", () => SpireModManager.Search.ByAuthor(content, "some one", true))];
        Check("click-to-search: Shift-click removes quoted terms too", quoted.SequenceEqual(["@symphony", "mod"]), string.Join(" | ", quoted));
        string[] tidy = [After("  #Library   mod  ", () => Tag("Library", true)), After("mod   ", () => Tag("Gameplay", true))];
        Check("click-to-search: spaces tidied, one between terms", tidy.SequenceEqual(["mod", "mod #Gameplay"]), string.Join(" | ", tidy));
        string[] glued = [After("foo#Library", () => Tag("Library", true)), After("C# #Library", () => Tag("Library", true))];
        Check("click-to-search: a #tag glued to a word is a tag, a lone # stays plain", glued.SequenceEqual(["foo", "C#"]), string.Join(" | ", glued));
        var local = await Shown("#local");
        Check("#tag matches the tag only, any case", local.Count > 1 && local.All(id => Tags.For(Dependencies.Find(id)).Any(c => c.Text == "Local")), string.Join(", ", local));
        // A "-" before a word, #tag, @author or "exact text" leaves out the mods it matches; alone, every other mod shows.
        var notWord = await Shown("test -fixes");
        Check("a -word leaves out the mods with it", notWord.Contains("SmmTestContent") && !notWord.Contains("SmmTestFixes"), string.Join(", ", notWord));
        var notAuthor = await Shown("#local -@symphony");
        Check("-@author leaves out that author's mods", notAuthor.Count > 0 && !notAuthor.Contains("SmmTestFixes")
            && notAuthor.All(id => Dependencies.Find(id)?.manifest?.author != "symphony"), string.Join(", ", notAuthor));
        var notTag = await Shown("-#library");
        Check("-#tag alone: every mod but the tagged ones", !notTag.Contains("SmmTestLib") && notTag.Contains("SmmTestFixes"), string.Join(", ", notTag));
        // Punctuation and symbols count as spaces, in the search and in a mod's text; "exact text" keeps them, and case.
        var spaced = await Shown("test-fixes!");
        Check("punctuation in a search counts as spaces: test-fixes! finds Test Fixes", spaced.SequenceEqual(["SmmTestFixes"]), string.Join(", ", spaced));
        var aliasBefore = Store.InfoFor("SmmTestContent")?.Alias;
        Store.EditMod("SmmTestContent", info => info.Alias = "Don't Panic");
        try
        {
            var apostrophe = await Shown("dont panic");
            Check("punctuation in a mod's text is skipped: dont panic finds Don't Panic", apostrophe.SequenceEqual(["SmmTestContent"]), string.Join(", ", apostrophe));
            var exact = await Shown("\"Don't\"");
            var otherCase = await Shown("\"don't\"");
            var noApostrophe = await Shown("\"Dont\"");
            Check("\"exact text\" matches as typed: its symbols and case", exact.SequenceEqual(["SmmTestContent"]) && otherCase.Count == 0 && noApostrophe.Count == 0,
                $"{string.Join(", ", exact)} | {string.Join(", ", otherCase)} | {string.Join(", ", noApostrophe)}");
            var notExact = await Shown("test -\"Don't\"");
            Check("-\"exact text\" leaves out the mods with it", !notExact.Contains("SmmTestContent") && notExact.Contains("SmmTestFixes"), string.Join(", ", notExact));
            // A word's pieces sit together: "don't-panic" finds Don't Panic; "test-panic" isn't one, though "test panic"
            // (two words, each anywhere) finds Test Content; "-don't" leaves out only that one.
            var together = await Shown("don't-panic");
            var apart = await Shown("test-panic");
            var twoWords = await Shown("test panic");
            var notDont = await Shown("-don't");
            Check("a punctuated word's pieces sit together, and the same for a -word",
                together.SequenceEqual(["SmmTestContent"]) && apart.Count == 0 && twoWords.SequenceEqual(["SmmTestContent"])
                && !notDont.Contains("SmmTestContent") && notDont.Contains("SmmTestFixes"),
                $"{string.Join(", ", together)} | {string.Join(", ", apart)} | {string.Join(", ", twoWords)} | {string.Join(", ", notDont)}");
        }
        finally
        {
            Store.EditMod("SmmTestContent", info => info.Alias = aliasBefore);
        }
        // Only the punctuation of sentences is read past (Jordan); other symbols are part of a word: "C#" isn't "c".
        Store.EditMod("SmmTestContent", info => info.Alias = "C# & C++ Kit!");
        try
        {
            var symbols = string.Join(" | ", new[] { "c#", "C++", "&", "kit", "c# kit" }.Select(q => string.Join(",", content.GetChildren().OfType<NModMenuRow>().Where(r => r.Mod != null
                && SpireModManager.Search.Matches(r.Mod, q)).Select(r => r.Mod?.manifest?.id).Distinct().Order())));
            var plainC = content.GetChildren().OfType<NModMenuRow>().Count(r => r.Mod != null && SpireModManager.Search.Matches(r.Mod, "c"));
            Check("symbols are part of a word (C#, C++, &), sentence punctuation isn't (Kit!); a lone letter still finds more",
                symbols == "SmmTestContent | SmmTestContent | SmmTestContent | SmmTestContent | SmmTestContent" && plainC > 1, $"{symbols}; \"c\" finds {plainC}");
        }
        finally
        {
            Store.EditMod("SmmTestContent", info => info.Alias = aliasBefore);
        }
        var nothing = await Shown("!!!");
        Check("a search with no terms (\"!!!\", a \"-\" alone) is no search: every row", nothing.Count == (await Shown("")).Count
            && (await Shown("-")).Count == nothing.Count, $"{nothing.Count}");
        string[] whole = [After("-#Library mod", () => Tag("Gameplay", true)), After("\"two words\" #mod", () => Tag("mod", true))];
        Check("click-to-search: a -#tag and \"exact text\" stay whole terms", whole.SequenceEqual(["-#Library mod #Gameplay", "\"two words\""]),
            string.Join(" | ", whole));
        string[] flipped = [After("-#Gameplay mod", () => Tag("Gameplay", true)), After("\"Don't", () => Tag("Local", true)),
            After("", () => SpireModManager.Search.ByChip(content, new Tags.Chip("Stable only", Colors.White, Search: "#\"Stable only\""), false))];
        Check("click-to-search: Shift on a left-out tag puts it in, an open quote is closed first, a branch chip searches for what it means",
            flipped.SequenceEqual(["mod #Gameplay", "\"Don't\" #Local", "#\"Stable only\""]), string.Join(" | ", flipped));
        // Brackets, OR, "|" and AND are pieces of their own: a term taken out takes the OR (else AND) that joined it, so the
        // rest means what it did, and brackets left empty go; one added after an OR still being typed completes it.
        string[] operators = [After("#a OR #b #c", () => Tag("b", true)), After("(#a OR #b) #c", () => Tag("a", true)),
            After("#a AND #b OR #c", () => Tag("b", true)), After("(#a) #c", () => Tag("a", true)), After("#a | #b", () => Tag("a", true)),
            After("#a or #b", () => Tag("b", true)), After("#a OR", () => Tag("b", true)), After("-(#a OR #b) #c", () => Tag("c", true)),
            After("NOT #a", () => Tag("a", true)), After("#x", () => Tag("x (y)", true))];
        Check("click-to-search: brackets, OR, \"|\" and AND stay; a term taken out takes its OR (else AND) and empty brackets with it",
            operators.SequenceEqual(["#a #c", "(#b) #c", "#a AND #c", "#c", "#b", "#a", "#a OR #b", "-(#a OR #b)", "#a", "#x #\"x (y)\""]),
            string.Join(" | ", operators));
        // Operators written back in capitals (lowercase ones, operators only between two tags, could be left beside a word
        // and read as one); an emptied group taking the OR or AND that joined it, as a term does.
        string[] written = [After("#Gameplay not #Library", () => Tag("Gameplay", true)), After("#a or not #b", () => Tag("a", true)),
            After("foo (#a) or #b", () => Tag("a", true)), After("#c OR (#b) #a", () => Tag("b", true)), After("#c (#b) OR #a", () => Tag("b", true)),
            After("#a AND (#b) OR #c", () => Tag("b", true)), After("#a or #b", () => Tag("c", true))];
        Check("click-to-search: operators come back in capitals (a lowercase one could turn into a word); an emptied group goes with what joined it",
            written.SequenceEqual(["NOT #Library", "NOT #b", "foo #b", "#c #a", "#c #a", "#a AND #c", "#a OR #b #c"]), string.Join(" | ", written));
        // More of it: a term in brackets beside an OR, in an OR of three, an author; and through a row's chips, Shift taking
        // one out of an OR (in brackets too), a plain click searching for it alone.
        string[] grouped = [After("(#a) OR #b", () => Tag("a", true)), After("#a OR (#b)", () => Tag("b", true)), After("#a OR (#b OR #c)", () => Tag("b", true)),
            After("#a (#b | #c)", () => Tag("c", true)), After("#a OR #b OR #c", () => Tag("b", true)), After("#a AND (#b)", () => Tag("b", true)),
            After("@a OR @b", () => SpireModManager.Search.ByAuthor(content, "a", true)), After("#Library OR #Local", () => ClickChip("SmmTestLib", "Library", true)),
            After("(#Library OR #Gameplay) @symphony", () => ClickChip("SmmTestLib", "Gameplay", true)), After("#Library | #Local", () => ClickChip("SmmTestLib", "Local", false))];
        Check("click-to-search: a term taken out of brackets or an OR leaves the rest as it was, through a row's chips too; a plain click searches for it alone",
            grouped.SequenceEqual(["#b", "#a", "#a OR (#c)", "#a (#b)", "#a OR #c", "#a", "@b", "#Local", "(#Library) @symphony", "#Local"]), string.Join(" | ", grouped));
        // A bare #tag ends at a quote or a "(", which start the next term (a group, "exact text"), as a word does.
        var framework = ModEntry.CopyOf("SmmTestLib");
        Check("search: a bare #tag ends at a quote or a \"(\" (#Library\"Test Framework\", #Library(Framework) find Test Framework)",
            framework != null && SpireModManager.Search.Matches(framework, "#Library\"Test Framework\"") && SpireModManager.Search.Matches(framework, "#Library(Framework)")
            && !SpireModManager.Search.Matches(framework, "#Library\"no such words\""));
        // The box takes 200 characters (60 before the grammar): a search longer than 60 stays whole and finds its mods; more
        // than 200 is cut there.
        const string longSearch = "(#Library OR #Gameplay) @\"Spire Mod Manager tests\" -@nobody -@\"no one at all\" -\"no such words\"";
        var longFound = await Shown(longSearch);
        var longMeant = content.GetChildren().OfType<NModMenuRow>().Where(r => SpireModManager.Search.Matches(r.Mod, longSearch)).Select(r => r.Mod?.manifest?.id ?? "").ToList();
        box.Clear();
        box.InsertTextAtCaret(new string('x', 250));
        var (cut, max) = (box.Text.Length, box.MaxLength);
        Check($"the search box takes 200 characters: a search of {longSearch.Length} stays whole and finds its mods; 250 typed, 200 kept",
            max == 200 && cut == 200 && longFound.Count > 0 && longFound.SequenceEqual(longMeant),
            $"max {max}, kept {cut}; found {string.Join(", ", longFound)}, matching {string.Join(", ", longMeant)}");
        var none = await Shown("zzz no such mod");
        Check("no match hides every row and header", none.Count == 0
            && !content.GetChildren().OfType<Control>().Any(c => c.IsInGroup("SpireModManagerCategoryHeader") && c.Visible));
        var cleared = await Shown("");
        Check("clearing restores the list", cleared.Count > 0);
    }

    // Found in (Jordan: "when a search is applied, add explanation in hover card that says "Found in: a, b, c""): the
    // fields a search found a mod in, on made-up details: each of its terms' own, a hidden tag as other keywords, a -term
    // or NOT naming none, nothing for a search that leaves the mod out; what it changes in the game as modified content
    // (our own mod's). Found for the same mods as Matches finds, every row and many searches; on a twin, the next launch's
    // order worked out once at most for a search. Then on a row's card while the box holds a search: right after its id and
    // state, subtle, in the panel's words; none with the box empty; worked out as the card opens, never in a redraw, and
    // how long that takes (screenshot 10d-found-in).
    static async Task TestFoundIn(Node menu, NModdingScreen screen, Control content, string outDir)
    {
        const string id = "SmmFoundInTest";
        const ulong item = 4401;
        var mod = new Mod
        {
            path = "", modSource = ModSource.SteamWorkshop, state = ModLoadState.Loaded,
            manifest = new ModManifest { id = id, name = "Found In Test", author = "Someone Careful", description = "Recolors card art with more contrast." },
        };
        Compat.SetWorkshopId(mod, item);
        WorkshopInfo.TestSet(item, new WorkshopInfo.Details(DateTime.Now, DateTime.Now, ["Cards", "Utility"], [], 0, null));
        Store.EditMod(id, info => (info.Alias, info.Notes, info.Tags) = ("Art Kit", "Keep for the contrast slider", ["visuals"]));
        try
        {
            string Said(Mod of, string search) => Search.FoundIn(of, search) is { } fields ? $"[{string.Join(", ", fields)}]" : "not found";
            string[] searches = ["careful", "@\"someone careful\"", "contrast", "#cards", "#visuals", "#workshop", "kit", "\"Art Kit\"", "card art",
                "found in test", "SmmFoundInTest", "careful #cards", "careful -zzz", "-zzz", "NOT #local", "zzz OR careful", "(zzz | #utility) slider",
                "careful -contrast", "zzz", "workshop", "workshop kit", "loaded -local"];
            var seen = string.Join(" | ", searches.Select(s => $"{s}: {Said(mod, s)}"));
            string[] meant = ["careful: [Author]", "@\"someone careful\": [Author]", "contrast: [Description, Note]", "#cards: [WorkshopTags]",
                "#visuals: [Tags]", "#workshop: [Keywords]", "kit: [Alias]", "\"Art Kit\": [Alias]", "card art: [Description, WorkshopTags, Alias]",
                "found in test: [Name, Id]", "SmmFoundInTest: [Id]", "careful #cards: [Author, WorkshopTags]", "careful -zzz: [Author]", "-zzz: []",
                "NOT #local: []", "zzz OR careful: [Author]", "(zzz | #utility) slider: [WorkshopTags, Note]", "careful -contrast: not found", "zzz: not found",
                "workshop: [Keywords]", "workshop kit: [Keywords, Alias]", "loaded -local: [Keywords]"];
            Check("Found in: each term's own fields, in the panel's order (a hidden tag as other keywords, with its \"#\" or without); a -term or NOT names "
                + "none; nothing when the search leaves the mod out", seen == string.Join(" | ", meant), seen);
            // What it changes in the game: our own mod's code (the debug info label manager), which nothing else of it says.
            // With a hidden tag too (ours is loaded): modified content first, as the panel now shows it over Other keywords.
            if (Dependencies.Find(ModEntry.ModId) is { } ours && GameChanges.Words(ours).Contains("debug", StringComparison.OrdinalIgnoreCase))
                Check("Found in: what a mod changes in the game is its modified content; with a hidden tag, before other keywords, in the panel's order",
                    Said(ours, "debug") == "[Modified]" && Said(ours, "debug #loaded") == "[Modified, Keywords]", $"{Said(ours, "debug")} | {Said(ours, "debug #loaded")}");
            else
                Results.Add("SKIP Found in modified content: our own mod's changes don't name the debug info label manager here");
        }
        finally
        {
            Store.EditMod(id, info => (info.Alias, info.Notes, info.Tags) = (null, null, null));
            WorkshopInfo.TestSet(item, null);
        }

        // The same mods as Matches finds, every row: Found in only shows where the row is a match.
        var rows = content.GetChildren().OfType<NModMenuRow>().Select(r => r.Mod).OfType<Mod>().ToList();
        string[] queries = ["lib", "test fixes", "the manager", "co-op", "#Library", "#local", "@symphony", "#Gameplay lib", "test framework", "zzz",
            "-#local", "NOT test", "#enabled OR #problem", "(lib | fixes) -#gameplay", "\"Test\"", "framework testlib", "#workshop", "manager -zzz",
            "enabled", "duplicate -disabled", "unmet OR needed", "test notloaded"];
        var differ = rows.SelectMany(m => queries.Where(q => (Search.FoundIn(m, q) != null) != Search.Matches(m, q)).Select(q => $"{m.manifest?.id} \"{q}\"")).ToList();
        Check($"Found in finds the mods Matches finds ({rows.Count} rows, {queries.Length} searches)", rows.Count > 0 && differ.Count == 0, string.Join(", ", differ.Take(8)));

        // A twin's card: the next launch's order (where its copy's slot falls) worked out once at most for a search, however
        // many hidden tags it asks (Found in's Lookups).
        var twins = rows.Where(m => m.manifest?.id == TwinId).ToList();
        var orders = twins.SelectMany(m => queries.Select(q =>
        {
            var before = Dependencies.TestNextOrders;
            Search.FoundIn(m, q);
            return Dependencies.TestNextOrders - before;
        })).ToList();
        if (orders.Sum() == 0)
            Results.Add($"SKIP Found in on a twin: no search on its {twins.Count} rows works out the next launch's order");
        else
            Check("Found in on a twin works out the next launch's order once at most for a search", orders.Max() <= 1,
                $"{orders.Max()} times for one search, {orders.Sum()} in all");

        // How long a card's Found in takes: every row, every search, one at a time as a card asks.
        var watch = System.Diagnostics.Stopwatch.StartNew();
        foreach (var m in rows)
            foreach (var q in queries)
                Search.FoundIn(m, q);
        var each = watch.Elapsed.TotalMilliseconds / Math.Max(1, rows.Count * queries.Length);
        // One the runtime collected garbage during is timed again (up to twice): the pause (15 ms with Better Mod Menu on)
        // isn't Found in's.
        double Timed(Mod m, string q, int again = 2)
        {
            var collected = GC.CollectionCount(0);
            var one = System.Diagnostics.Stopwatch.StartNew();
            Search.FoundIn(m, q);
            return GC.CollectionCount(0) == collected || again == 0 ? one.Elapsed.TotalMilliseconds : Timed(m, q, again - 1);
        }
        var slowest = rows.SelectMany(m => queries.Select(q => (m, q))).Max(p => Timed(p.m, p.q));
        Results.Add($"INFO Found in: {each:0.000} ms a card on average, {slowest:0.00} ms at most ({rows.Count} rows x {queries.Length} searches)");
        Check("Found in stays quick as a card opens (under 5 ms each here)", slowest < 5, $"{each:0.000} ms on average, {slowest:0.00} ms at most");

        // On a row's card: Test Fixes by its author and its own tag.
        var box = screen.GetNode<LineEdit>("ModsBorder/SpireModManagerSearch");
        void Type(string text)
        {
            box.Text = text;
            box.EmitSignal(LineEdit.SignalName.TextChanged, text);
        }
        const string search = "favorites symphony";
        Type(search);
        await Wait(menu, 0.3);
        if (content.GetChildren().OfType<NModMenuRow>().FirstOrDefault(r => r.Visible && r.Mod?.manifest?.id == "SmmTestFixes") is not { Mod: { } fixes } row)
        {
            Results.Add($"FAIL Found in on a row's card: no row for Test Fixes while searching \"{search}\"");
            Type("");
            return;
        }
        Search.TestFoundIn = 0;
        CategoryList.Apply(content); // redraws with the search on: no row works out its fields
        CategoryList.Redraw(content);
        await Wait(menu, 0.3);
        var inRedraw = Search.TestFoundIn;
        var hot = row.GetNode<Control>("SpireModManagerTitleHover");
        var card = (await SizedHoverCard(menu, hot, "SpireModManagerNotesCard", () => Capture(screen, outDir, "10d-found-in"))).Text.Split('\n');
        var opened = Search.TestFoundIn;
        var bbcode = ModDetails.Card(fixes, search: search).Split('\n');
        var foundIn = Menus.Format("SPIRE_MOD_MANAGER.FOUND_IN", ("fields", $"{Menus.Text("SPIRE_MOD_MANAGER.FIELD_AUTHOR")}, {Menus.Text("SPIRE_MOD_MANAGER.FIELD_TAGS")}"));
        Check("Found in on a row's card while the box holds a search: at the bottom after a gap, subtle (\"Search term found in: author, tags\")",
            card.Length > 2 && card[0].StartsWith("SmmTestFixes") && card[^1] == foundIn && card[^1] == "Search term found in: author, tags"
            && card[^2] == " " && bbcode.Length > 2 && bbcode[^1] == $"[color=#fff6e299]{foundIn}[/color]" && bbcode[^2] == ModDetails.CardGap,
            string.Join(" | ", card));
        Check("Found in is worked out as the card opens, never in a redraw", inRedraw == 0 && opened > 0, $"{inRedraw} in two redraws, {opened} for the card");
        Type("");
        await Wait(menu, 0.3);
        var plain = (await HoverCard(menu, hot, "SpireModManagerNotesCard")).Split('\n');
        Check("no Found in on a row's card with the box empty", plain.Length > 1 && plain[0].StartsWith("SmmTestFixes")
            && !plain.Any(l => l.StartsWith(Menus.Format("SPIRE_MOD_MANAGER.FOUND_IN", ("fields", "")))), string.Join(" | ", plain));
    }

    // A redraw answers its questions once (Lookups) instead of once per row: the answers must be the same as asked
    // one at a time outside a redraw, for every mod shown. Then how long a redraw and a search keystroke take here
    // (about 50 mods): reported, with a generous bound. TestManyMods and the --real tour time longer lists.
    static async Task TestRedrawCost(Node menu, NModdingScreen screen, Control content)
    {
        var rows = content.GetChildren().OfType<NModMenuRow>().ToList();
        var mods = rows.Select(r => r.Mod).OfType<Mod>().ToList();
        var order = LoadOrder.Mods(rows).Select((m, i) => (m, i)).DistinctBy(p => p.m).ToDictionary(p => p.m, p => p.i);
        string[] queries = ["lib", "test fixes", "the manager", "co-op", "#Library", "#local", "@symphony", "#Gameplay lib", "test framework", "zzz"];
        static string Key(Mod? mod) => mod == null ? "none" : $"{mod.manifest?.id}/{mod.modSource}";
        List<string> Answers() =>
        [
            .. mods.Select(mod => $"{Key(mod)}: on {Copies.Ticked(mod)}, copy {Key(Dependencies.Find(mod.manifest?.id ?? ""))}, "
                + $"needed by {string.Join(",", Dependencies.NeededBy(mod.manifest?.id).Select(Key))}, "
                + $"needs {string.Join(",", Dependencies.Of(mod).Select(id => Key(Dependencies.Find(id))))}, "
                + $"tags {string.Join(",", Tags.For(mod).Select(c => $"{c.Text}/{c.Color}/{c.Custom}/{c.Tip}"))}, "
                + $"notes {RowStrip.RequiredFor(mod)} | {RowStrip.Needs(mod, order, false)} | {RowStrip.Needs(mod, order, true)}, "
                + $"matches {string.Concat(queries.Select(q => Search.Matches(mod, q) ? "y" : "n"))}"),
            $"order problems {string.Join(",", Dependencies.OrderViolations(LoadOrder.Mods(rows)).Select(v => $"{Key(v.Mod)}>{Key(v.Needs)}"))}",
            $"needed and off {string.Join(",", Dependencies.NeededOff(mods).Select(Key))}",
            $"words {Menus.Text("SPIRE_MOD_MANAGER.TAG_LIBRARY")} {Menus.Text("SPIRE_MOD_MANAGER.NEEDS_LABEL")}",
        ];
        var fresh = Answers();
        List<string> kept;
        using (Lookups.Begin())
            kept = Answers();
        var differ = fresh.Zip(kept).Where(p => p.First != p.Second).Select(p => $"{p.First} -> {p.Second}").ToList();
        Check("inside a redraw, the same answers as one at a time (on, copies, needs, tags, notes, search)",
            differ.Count == 0 && fresh.Count == kept.Count && Lookups.Current == null, string.Join(" | ", differ.Take(3)));

        var box = screen.GetNode<LineEdit>("ModsBorder/SpireModManagerSearch");
        void Type(string text)
        {
            box.Text = text;
            box.EmitSignal(LineEdit.SignalName.TextChanged, text);
        }
        static double Ms(Action action)
        {
            CleanHeap();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            for (var i = 0; i < 10; i++)
                action();
            return watch.Elapsed.TotalMilliseconds / 10;
        }
        var listMs = Ms(() => CategoryList.Apply(content));
        var keyMs = Ms(() =>
        {
            Type("lib"); // every row matched
            Type(""); // and the whole list back
        }) / 2;
        await Wait(menu, 0.3);
        Click(screen.GetNode<NButton>("SpireModManagerLoadOrderToggle"));
        await Wait(menu, 0.5);
        var orderMs = Ms(() => CategoryList.Apply(content));
        Click(screen.GetNode<NButton>("SpireModManagerLoadOrderToggle"));
        await Wait(menu, 0.5);
        Results.Add($"INFO redraw with {rows.Count} mods: main list {listMs:0.0} ms, a search keystroke {keyMs:0.0} ms, Load Order {orderMs:0.0} ms");
        Check("a redraw and a search keystroke stay quick (under 100 ms here)", Math.Max(listMs, Math.Max(keyMs, orderMs)) < 100,
            $"{listMs:0.0} / {keyMs:0.0} / {orderMs:0.0} ms");
    }

    // Three hundred made-up mods (a big list is about 250), no screen: what a redraw asks about every row (tags, "Needs",
    // "Required for", searches) and the page's warnings, answered as a redraw does (Lookups) and timed, then each
    // answer checked against the slow way (the saved list searched every time). Every seventh mod is off, every
    // tenth is a library the next ones need, every 25th also needs one that isn't installed, and one library has
    // a second copy that is on while the first is off. Then their rows (TestManyRows).
    static async Task TestManyMods(Node menu, NModdingScreen screen, Control content)
    {
        var mods = new List<Mod>();
        var saved = new List<SettingsSaveMod>();
        for (var i = 0; i < 300; i++)
        {
            List<ModDependency> needs = [];
            if (i % 10 != 0)
                needs.Add(new ModDependency($"SmmMany{i / 10 * 10:000}"));
            if (i % 3 == 0 && i >= 20)
                needs.Add(new ModDependency($"SmmMany{i / 10 * 10 - 10:000}"));
            if (i % 25 == 0)
                needs.Add(new ModDependency("SmmManyMissing"));
            var mod = new Mod
            {
                path = "", modSource = ModSource.ModsDirectory,
                manifest = new ModManifest
                {
                    id = $"SmmMany{i:000}", name = $"Many Mods {i}", author = $"Author {i % 12}", dependencies = needs, affectsGameplay = i % 2 == 0,
                    // About as long as a long real one (searched too).
                    description = string.Concat(Enumerable.Repeat($"Many Mods {i} adds [gold]cards[/gold], relics and events to every act. ", 8)),
                },
            };
            mods.Add(mod);
            saved.Add(new SettingsSaveMod(mod) { IsEnabled = i % 7 != 0 });
        }
        var copy = new Mod { path = "", modSource = ModSource.SteamWorkshop, manifest = new ModManifest { id = "SmmMany050", name = "Many Mods 50 (Workshop)" } };
        mods.Add(copy);
        saved.Add(new SettingsSaveMod(copy));
        saved[50].IsEnabled = false;

        // The slow way.
        bool On(Mod m) => !saved.Any(e => e.Id == m.manifest?.id && e.Source == m.modSource && !e.IsEnabled);
        Mod? Copy(string id) => mods.Where(m => m.manifest?.id == id).OrderByDescending(On).FirstOrDefault();
        int Needing(Mod mod, bool onOnly) => mods.Count(m => m != mod && (!onOnly || On(m)) && Dependencies.Of(m).Contains(mod.manifest?.id));

        var order = mods.Select((m, i) => (m, i)).ToDictionary(p => p.m, p => p.i);
        string[] queries = ["many 12", "#Library", "@\"Author 3\"", "author 3 many", "zzz"];
        var watch = System.Diagnostics.Stopwatch.StartNew();
        double Lap()
        {
            var ms = watch.Elapsed.TotalMilliseconds;
            watch.Restart();
            return ms;
        }
        var wrong = new List<string>();
        using (Lookups.Begin(mods, [], saved, Ticks.All))
        {
            var built = Lap();
            var notes = mods.ToDictionary(m => m, m => (Tags: Tags.For(m), Required: RowStrip.RequiredFor(m), Needs: RowStrip.Needs(m, order, loadOrder: false)));
            var strips = Lap();
            // A search redraw asks about each row about four times (its category's matches, count and rows, the page bar).
            foreach (var query in queries)
                for (var pass = 0; pass < 4; pass++)
                    foreach (var mod in mods)
                        Search.Matches(mod, query);
            var searches = Lap();
            var problems = Dependencies.OrderViolations(mods).Count + Dependencies.NeededOff(mods).Count;
            var warnings = Lap();
            Results.Add($"INFO 300 made-up mods (and a second copy of one): lookups {built:0.0} ms, tags and notes {strips:0.0} ms, "
                + $"{queries.Length} searches {searches:0.0} ms, warnings {warnings:0.0} ms ({problems} problems)");
            Check("300 made-up mods: every row's questions and five searches stay quick (under 100 ms)", built + strips + searches + warnings < 100,
                $"{built + strips + searches + warnings:0.0} ms");
            // Hidden tags that need the whole list (#problem asks for all of them) work it out once for the redraw.
            string[] hidden = ["#problem", "#needed OR #order", "#enabled -#local", "(#pending OR #unmet) many"];
            Lap();
            foreach (var query in hidden)
                for (var pass = 0; pass < 4; pass++)
                    foreach (var mod in mods)
                        Search.Matches(mod, query);
            var hiddenMs = Lap();
            Results.Add($"INFO 300 made-up mods: {hidden.Length} searches by hidden tags {hiddenMs:0.0} ms");
            Check("300 made-up mods: searches by hidden tags stay quick too (under 50 ms)", hiddenMs < 50, $"{hiddenMs:0.0} ms");

            string Times(string text, string word) => (text.Split(word).Length - 1).ToString();
            foreach (var mod in mods)
            {
                var id = mod.manifest?.id ?? "";
                var (tags, required, needs) = notes[mod];
                var deps = Dependencies.Of(mod);
                var expected = $"on {On(mod)}, copy {Copy(id)?.modSource}, library {Needing(mod, false) > 0}, required for {Needing(mod, true)}, "
                    + $"missing {(On(mod) ? deps.Count(d => Copy(d) == null) : 0)}, off {(On(mod) ? deps.Count(d => Copy(d) is { } dep && !On(dep)) : 0)}";
                var answered = $"on {Copies.Ticked(mod)}, copy {Dependencies.Find(id)?.modSource}, library {tags.Any(c => c.Text == "Library")}, "
                    + $"required for {(System.Text.RegularExpressions.Regex.Match(required, @"Required for (\d+)") is { Success: true } match ? match.Groups[1].Value : "0")}, "
                    + $"missing {Times(needs, "(not installed)")}, off {Times(needs, "(disabled)")}";
                if (answered != expected)
                    wrong.Add($"{id}: {answered}; the slow way: {expected}");
            }
        }
        Check("300 made-up mods: the same answers as the slow way (on, copies, libraries, required for, missing, off)",
            wrong.Count == 0 && Lookups.Current == null, $"{wrong.Count} differ: {string.Join(" | ", wrong.Take(3))}");
        await TestManyRows(menu, screen, content, mods, saved);
    }

    // The same mods as rows of a page (out of sight to the left of this one), in our row style: each row's line under
    // its title (tags, note, author), its buttons and its pencil made from nothing, as a new page makes them (after a
    // run the main menu is made again, and the page with it: #46), timed part by part (with the titles' styling before
    // them, and how often fonts were asked for and looked up), and redrawn with nothing to change. Then what the lines
    // show, against what the mods say: their tags, notes and authors (no keywords: those are the info panel's), the
    // author at the line's end, the pencil and the category button with their tips. Each part of a line styled exactly as its old
    // per-node overrides styled it (UnlikeOverrides), at the old sizes. Everything turned off (some notes go: no mod on
    // needs those libraries) and on again: the lines changed in place show what lines made afresh do, and then what
    // they first did; no line holds an empty note.
    static async Task TestManyRows(Node menu, NModdingScreen screen, Control content, List<Mod> mods, List<SettingsSaveMod> saved)
    {
        if (Bmm.Present)
        {
            Results.Add("SKIP 300 made-up rows: Better Mod Menu dresses every row the game makes, and leaves handlers on the page for each");
            return;
        }
        var box = new VBoxContainer { Name = "SpireModManagerManyRows", MouseFilter = Control.MouseFilterEnum.Ignore };
        screen.AddChild(box);
        // Wide enough that no line folds its tags (what folds is the long-details test's).
        (box.Position, box.Size) = (new Vector2(-3000, 0), new Vector2(1600, 0));
        try
        {
            // The game's own rows, from its factory (found by name: a game update may change it).
            var create = typeof(NModMenuRow).GetMethod("Create", [typeof(NModdingScreen), typeof(Mod)]);
            var rows = new List<NModMenuRow>();
            foreach (var mod in mods)
                if (create?.Invoke(null, [screen, mod]) is NModMenuRow made)
                {
                    box.AddChild(made);
                    rows.Add(made);
                }
            if (rows.Count != mods.Count)
            {
                Results.Add($"SKIP 300 made-up rows: the game made {rows.Count} of {mods.Count}");
                return;
            }
            void Build(List<SettingsSaveMod> list)
            {
                using (Lookups.Begin(mods, [], list, Ticks.All))
                    RowStrip.Apply(rows, CategoryList.StripActions(content), CategoryList.EdgeAction(content));
            }
            List<string> Expected(List<SettingsSaveMod> list)
            {
                using (Lookups.Begin(mods, [], list, Ticks.All))
                {
                    var order = LoadOrder.Mods(rows).Select((m, i) => (m, i)).DistinctBy(p => p.m).ToDictionary(p => p.m, p => p.i);
                    return [.. rows.Select(row =>
                    {
                        var says = string.Join("    ", new[] { RowStrip.RequiredFor(row.Mod), RowStrip.Needs(row.Mod, order, false) }.Where(s => s.Length > 0));
                        var by = ModDetails.Short(row.Mod?.manifest?.author ?? "");
                        return string.Join(" | ", new[]
                        {
                            $"pencil ✎ ({Menus.Text("SPIRE_MOD_MANAGER.EDIT_TIP")})",
                            $"button {CategoryList.CategoryName(Copies.KeyOf(row.Mod))} ({Menus.Text("SPIRE_MOD_MANAGER.CATEGORY_TIP")})",
                        }.Concat(Tags.For(row.Mod).Select(c => $"tag {c.Text}"))
                            .Append(says.Length > 0 ? $"note {System.Text.RegularExpressions.Regex.Replace(says, @"\[[^\]]+\]", "")}" : "")
                            .Append(by.Length > 0 ? $"author {by} at the end" : "").Where(p => p.Length > 0));
                    })];
                }
            }
            static string Differ(List<string> shown, List<string> wanted) => shown.Zip(wanted).Where(p => p.First != p.Second)
                .Select(p => $"{p.First} -- wanted: {p.Second}").FirstOrDefault() ?? (shown.Count == wanted.Count ? "" : $"{shown.Count} rows, wanted {wanted.Count}");

            var (titlesMs, madeMs, fontAsks, fontLookups) = (0.0, 0.0, 0, 0);
            RowStrip.Costs.Clear();
            var frameMs = await FrameMs(menu, () =>
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                RowTitles.Apply(rows, vanilla: false); // our row style first, as a redraw does (titles fitted at the frame's end)
                titlesMs = watch.Elapsed.TotalMilliseconds;
                RowStrip.Timing = true;
                (fontAsks, fontLookups) = (ModEntry.FontAsks, ModEntry.FontLookups);
                watch.Restart();
                Build(saved);
                madeMs = watch.Elapsed.TotalMilliseconds;
                (fontAsks, fontLookups) = (ModEntry.FontAsks - fontAsks, ModEntry.FontLookups - fontLookups);
                RowStrip.Timing = false;
            });
            CleanHeap();
            var again = System.Diagnostics.Stopwatch.StartNew();
            Build(saved);
            var keptMs = again.Elapsed.TotalMilliseconds;
            Results.Add($"INFO 300 made-up rows: lines, buttons and pencils made from nothing {madeMs:0} ms ({Parts(RowStrip.Costs)}), "
                + $"to the end of that frame {frameMs:0} ms; redrawn with nothing to change {keptMs:0.0} ms; their titles styled first {titlesMs:0} ms; "
                + $"fonts asked for {fontAsks} times while the lines were made, looked up {fontLookups}");
            await Wait(menu, 0.3);

            var first = rows.Select(LineOf).ToList();
            Check("300 made-up rows: each line shows its tags, note and author (at the line's end) and no keywords, with the pencil and the category button and their tips",
                first.SequenceEqual(Expected(saved)), Differ(first, Expected(saved)));
            var sample = rows[10]; // a library: three tags, "Required for", an author
            var line = sample.GetNodeOrNull<Control>("SpireModManagerStrip");
            var tag = line?.GetChildren().OfType<PanelContainer>().FirstOrDefault();
            var (pencil, button, tagText) = (sample.GetNodeOrNull<Control>("SpireModManagerEdit"),
                sample.GetNodeOrNull<Control>(RowStrip.ActionsName)?.GetChildOrNull<Control>(0), tag?.GetChildOrNull<Control>(0));
            var (note, author) = (line?.GetNodeOrNull<Control>("Needs"), line?.GetNodeOrNull<Control>("Author"));
            var unlike = new[] { pencil, button, line, tag, tagText, note, author }
                .SelectMany((part, i) => part == null ? [$"part {i} missing"] : UnlikeOverrides(sample, part)).ToList();
            Check("300 made-up rows: the pencil, a button, the line, a tag and its text, the note and the author styled as their overrides did "
                + "(every theme item of their kind), at the old sizes (18, 16, 12, 13, 13; gap 6)", unlike.Count == 0
                && pencil?.GetThemeFontSize("font_size") == 18 && pencil.GetThemeStylebox("normal") is StyleBoxEmpty
                && button?.GetThemeFontSize("font_size") == 16 && button.GetThemeStylebox("normal") is StyleBoxFlat
                && tagText?.GetThemeFontSize("font_size") == 12 && note?.GetThemeFontSize("normal_font_size") == 13
                && author?.GetThemeFontSize("font_size") == 13 && line?.GetThemeConstant("separation") == 6, string.Join("; ", unlike.Take(6)));
            // The old overrides' own values, written out here, so a look changed by mistake (a colour, a font, a box)
            // shows: UnlikeOverrides compares each part with its new look only.
            var (kreon, cream, gold) = (ModEntry.Font("res://themes/kreon_regular_shared.tres"), new Color(1, 0.964706f, 0.886275f), new Color(0.937f, 0.784f, 0.318f));
            var chip = Tags.For(sample.Mod).FirstOrDefault();
            var olds = new (string Part, bool Same)[]
            {
                ("pencil", pencil?.GetThemeFont("font") == kreon && pencil.GetThemeColor("font_color") == new Color(cream, 0.6f)
                    && pencil.GetThemeColor("font_hover_color") == gold && pencil.GetThemeColor("font_pressed_color") == gold
                    && pencil.GetThemeColor("font_disabled_color") == new Color(cream, 0.3f)
                    && pencil.GetThemeStylebox("hover") is StyleBoxEmpty { ContentMarginLeft: 4, ContentMarginRight: 4 }),
                ("button", button?.GetThemeFont("font") == kreon && button.GetThemeColor("font_color") == cream
                    && button.GetThemeColor("font_hover_color") == Colors.White && button.GetThemeColor("font_disabled_color") == new Color(cream, 0.3f)
                    && button.GetThemeStylebox("normal") is StyleBoxFlat { } normal && normal.BgColor == new Color(0.0756f, 0.12084f, 0.18f, 0.7f)
                    && normal.BorderColor == new Color(gold, 0.35f) && button.GetThemeStylebox("hover") is StyleBoxFlat { } hover
                    && hover.BgColor == new Color(0.2f, 0.32f, 0.42f) && hover.BorderColor == gold),
                ("tag", chip != null && tag?.GetThemeStylebox("panel") is StyleBoxFlat { CornerRadiusTopLeft: 2, ContentMarginTop: 0 } panel
                    && panel.BgColor == new Color(chip.Color, chip.Custom ? 0.1f : 0.2f) && tagText?.GetThemeFont("font") == kreon
                    && tagText.GetThemeColor("font_color") == chip.Color),
                ("note", note?.GetThemeFont("normal_font") == kreon && note.GetThemeColor("default_color") == new Color(0.937f, 0.784f, 0.318f, 0.75f)),
                ("author", author?.GetThemeFont("font") == kreon && author.GetThemeColor("font_color") == new Color(1, 0.964706f, 0.886275f, 0.45f)),
            };
            Check("300 made-up rows: the pencil, a button, a tag, the note and the author in the old overrides' own fonts, colours and boxes",
                olds.All(o => o.Same), "unlike: " + string.Join(", ", olds.Where(o => !o.Same).Select(o => o.Part)));

            var off = mods.Select(m => new SettingsSaveMod(m) { IsEnabled = false }).ToList();
            Build(off);
            await Wait(menu, 0.3);
            var changed = rows.Select(LineOf).ToList();
            var noted = rows.Count(r => r.HasNode("SpireModManagerStrip/Needs"));
            foreach (var row in rows)
                Unmake(row);
            Build(off);
            await Wait(menu, 0.3);
            var fresh = rows.Select(LineOf).ToList();
            Check("300 made-up rows, all turned off: the lines changed in place show what lines made afresh do (and what the mods say)",
                noted < rows.Count && changed.SequenceEqual(fresh) && fresh.SequenceEqual(Expected(off)),
                $"{rows.Count - noted} notes gone; {Differ(changed, fresh)} {Differ(fresh, Expected(off))}");
            Build(saved);
            await Wait(menu, 0.3);
            var back = rows.Select(LineOf).ToList();
            Check("300 made-up rows, on again: each line shows what it first did (the notes made again in place)", back.SequenceEqual(first), Differ(back, first));
            Check("300 made-up rows: no line holds an empty note", rows.All(r => r.GetNodeOrNull<RichTextLabel>("SpireModManagerStrip/Needs") is not { Text.Length: 0 }));

            // A row's buttons kept through a redraw (the same words and states) run that redraw's actions: an action holds
            // what its redraw knew (Load Order's rows). And a line is made again when a tag's card or search changes, its
            // words, colour and kind the same (the Duplicate tag's card names the other copies).
            var ran = "";
            using (Lookups.Begin(mods, [], saved, Ticks.All))
            {
                RowStrip.Apply([rows[0]], _ => [new MenuItem("Probe", () => ran = "first")]);
                RowStrip.Apply([rows[0]], _ => [new MenuItem("Probe", () => ran = "second")]);
            }
            var probeButtons = rows[0].GetNodeOrNull<HBoxContainer>(RowStrip.ActionsName)?.GetChildren().OfType<Button>().ToList() ?? [];
            if (probeButtons is [var probe])
                probe.EmitSignal(BaseButton.SignalName.Pressed);
            Check("300 made-up rows: buttons kept through a redraw run that redraw's actions, not the ones they were made with",
                probeButtons is [{ Text: "Probe" }] && ran == "second", $"{probeButtons.Count} buttons, ran \"{ran}\"");
            var sameId = new Tags.Chip("Duplicate", Colors.Yellow, Tip: "Another copy: A");
            var shows = RowStrip.Shows([sameId], "Someone");
            Check("300 made-up rows: a line is made again when a tag's card or search changes, its words, colour and kind the same",
                RowStrip.Shows([sameId with { Tip = "Another copy: B" }], "Someone") != shows && RowStrip.Shows([sameId with { Search = "#same" }], "Someone") != shows
                && RowStrip.Shows([sameId], "Someone") == shows, shows);
        }
        finally
        {
            screen.RemoveChild(box);
            box.QueueFree();
        }
    }

    // Load Order's search finds rather than filters: every row stays; beside the box, "N matches · ‹ ›"
    // steps through the matching rows (each selected and scrolled to), wrapping at the ends; typing goes to the first;
    // Enter and Shift+Enter step too; no search, no bar.
    static async Task TestLoadOrderFind(Node menu, NModdingScreen screen, Control content, string outDir)
    {
        var border = screen.GetNode<Control>("ModsBorder");
        var box = border.GetNode<LineEdit>("SpireModManagerSearch");
        var wasLoadOrder = Store.Data.LoadOrderView;
        if (!wasLoadOrder)
        {
            Click(screen.GetNode<NButton>("SpireModManagerLoadOrderToggle"));
            await Wait(menu, 0.5);
        }
        List<NModMenuRow> Rows() => content.GetChildren().OfType<NModMenuRow>().ToList();
        Control? Bar() => border.GetNodeOrNull<Control>(FindBar.Name);
        string Count() => Bar()?.GetNodeOrNull<Label>("Count")?.Text ?? "";
        void Press(string name) => Bar()?.GetNodeOrNull<Button>(name)?.EmitSignal(BaseButton.SignalName.Pressed);
        async Task Type(string text)
        {
            box.Text = text;
            box.EmitSignal(LineEdit.SignalName.TextChanged, text);
            await Wait(menu, 0.3);
        }
        async Task Key(bool shift)
        {
            box.EmitSignal(Control.SignalName.GuiInput, new InputEventKey { Keycode = Godot.Key.Enter, Pressed = true, ShiftPressed = shift });
            await Wait(menu, 0.2);
        }
        int At(List<NModMenuRow> found) => found.FindIndex(r => r.Mod == CategoryList.Selected);

        Check("Load Order, no search: no find bar", Bar() is not { Visible: true });
        var boxEnd = box.GetGlobalRect().End.X;
        if (Rows().FirstOrDefault(r => !SpireModManager.Search.Matches(r.Mod, "SmmTest")) is { } other)
            CategoryList.Select(screen, other); // a row the search won't match: typing goes to the first match
        await Type("SmmTest");
        var found = FindBar.Found(content);
        var view = border.GetNode<Control>("ModsScrollContainer").GetGlobalRect();
        Check("Load Order, a search: every row stays, \"N matches\" and ‹ › beside the box, the first match selected and in view",
            found.Count >= 3 && Rows().All(r => r.Visible) && Bar() is { Visible: true } bar && Count() == $"{found.Count} matches" && At(found) == 0
            && view.Intersects(found[0].GetGlobalRect()) && bar.GetGlobalRect().Position.X >= box.GetGlobalRect().End.X,
            $"{found.Count} found, count \"{Count()}\", at {At(found)}, rows shown {Rows().Count(r => r.Visible)}/{Rows().Count}");
        Check("Load Order: the search box keeps its width as a search starts (the find bar's room kept beside it)",
            Math.Abs(box.GetGlobalRect().End.X - boxEnd) < 0.5f, $"box ends at {boxEnd}, then {box.GetGlobalRect().End.X}");
        Check("the arrows say where they go", Bar() is { } arrows && Hover.TipOf(arrows.GetNode<Button>("PreviousMatch")) == "Go to previous found match"
            && Hover.TipOf(arrows.GetNode<Button>("NextMatch")) == "Go to next found match");
        // The rows it doesn't match fade below even a match that's off; a match keeps its usual look.
        float Expected(NModMenuRow r, bool finding) => (!finding || found.Contains(r), r.Mod is { } mod && Copies.Ticked(mod)) switch
        {
            (true, true) => 1, (true, false) => LoadOrder.OffAlpha, (false, true) => LoadOrder.UnmatchedAlpha, _ => LoadOrder.UnmatchedOffAlpha,
        };
        string Looks() => string.Join(", ", Rows().Select(r => $"{r.Mod?.manifest?.id} {r.Modulate.A:0.##}"));
        Check("finding: the rest fade (on 0.2, off 0.1) below a match that's off (0.4); matches look as usual",
            Rows().All(r => Math.Abs(r.Modulate.A - Expected(r, finding: true)) < 0.01f) && Rows().Any(r => !found.Contains(r))
            && Rows().Any(r => found.Contains(r) && r.Mod is { } mod && !Copies.Ticked(mod)), Looks());
        LoadOrder.Reset(content); // as a drag ends
        Check("finding: the same looks after a drag ends", Rows().All(r => Math.Abs(r.Modulate.A - Expected(r, finding: true)) < 0.01f), Looks());
        await Capture(screen, outDir, "11c-load-order-find");
        Press("NextMatch");
        await Wait(menu, 0.2);
        var second = At(found);
        Press("PreviousMatch");
        await Wait(menu, 0.2);
        var backToFirst = At(found);
        Press("PreviousMatch");
        await Wait(menu, 0.2);
        var wrappedToLast = At(found);
        Press("NextMatch");
        await Wait(menu, 0.2);
        Check("› and ‹ step through the matches, wrapping at the ends, each scrolled to", second == 1 && backToFirst == 0
            && wrappedToLast == found.Count - 1 && At(found) == 0 && view.Intersects(found[0].GetGlobalRect()),
            $"next {second}, back {backToFirst}, before the first {wrappedToLast}, after the last {At(found)}");
        await Key(shift: false);
        var byEnter = At(found);
        await Key(shift: true);
        Check("Enter goes to the next match, Shift+Enter the one before", byEnter == 1 && At(found) == 0, $"Enter {byEnter}, Shift+Enter {At(found)}");
        await Type("zzzz no such mod");
        Check("no match: \"No matches\", the arrows off, every row still shown", Count() == "No matches" && Rows().All(r => r.Visible)
            && Bar() is { } emptyBar && emptyBar.GetChildren().OfType<Button>().All(b => b.Disabled), Count());
        Check("no match: every row faded", Rows().All(r => r.Modulate.A <= LoadOrder.UnmatchedAlpha + 0.01f), Looks());
        await Type("");
        Check("search cleared: no find bar, the box the same width, its room kept", Bar() is not { Visible: true }
            && Math.Abs(box.GetGlobalRect().End.X - boxEnd) < 0.5f, $"box ends at {boxEnd}, then {box.GetGlobalRect().End.X}");
        Check("search cleared: every row its usual look again", Rows().All(r => Math.Abs(r.Modulate.A - Expected(r, finding: false)) < 0.01f), Looks());
        if (!wasLoadOrder)
        {
            Click(screen.GetNode<NButton>("SpireModManagerLoadOrderToggle"));
            await Wait(menu, 0.5);
        }
    }

    // Two copies of one mod with one id and source (with dev.cs test --workshop and two items of one mod, those; else the
    // fixture twins, SmmTestTwinA and B). The saved list keeps one entry per copy through a move and back (it grew by one
    // with every save), Changes and the main menu agree nothing moved, and the page's line follows the menu's rule. On our
    // main list (where a tick is its own undo step: Load Order's undo steps through the order) each copy has its own box,
    // the one loading ticked, the other off (copies_off): a click on the other's ticks it beside the first, as one undo
    // step, Changes naming only it, the saved list's entries all on (one switch for both), and the higher version loads
    // (the fixture's: the one loading already, so nothing pending); undo puts it back. Turning the mod off and on sets
    // every one of its entries (the game reads any entry off as off), copies_off kept. Both off, Enable needed from a mod
    // that needs the id brings back the copy that was ticked, not the other.
    static async Task TestTwinCopies(Node menu, NModdingScreen screen, Control content)
    {
        List<NModMenuRow> Rows() => content.GetChildren().OfType<NModMenuRow>().ToList();
        if (ModManager.Mods.Where(m => m.manifest?.id != null).GroupBy(m => (Id: m.manifest?.id ?? "", m.modSource))
            .Where(g => g.Select(Copies.CopyOf).Distinct().Count() > 1).OrderBy(g => g.Key.Id == TwinId ? 1 : 0).FirstOrDefault() is not { } twins)
        {
            TwinsMissing("two copies of one mod");
            return;
        }
        if (SaveManager.Instance.SettingsSave.ModSettings is not { } settings)
        {
            Results.Add("FAIL two copies of one mod: the game has no saved mod list");
            return;
        }
        Results.Add($"INFO two copies of one mod: {twins.Key.Id} ({string.Join(", ", twins.Select(Copies.CopyOf))})");
        var list = settings.ModList;
        var (id, source) = twins.Key;
        int Entries() => list.Count(e => e.Id == id && e.Source == source);
        bool LineFollows() => screen.GetNode<Control>("%PendingChangesLabel").Visible == ModEntry.HasPendingChanges();
        var (count, entries) = (list.Count, Entries());
        var other = Rows().Select(r => r.Mod).FirstOrDefault(m => m != null && m.manifest?.id != id && m.manifest?.id != ModEntry.ModId);
        if (other == null)
        {
            Check("two copies of one mod: another mod to move", false);
            return;
        }
        // Past its neighbour (not to the top: ours stay first).
        var at = Rows().FindIndex(r => r.Mod == other);
        LoadOrder.Move(other, at + 1 < Rows().Count ? at + 1 : at - 1, Rows());
        CategoryList.Redraw(content);
        await Wait(menu, 0.3);
        Check("two copies of one mod: a move keeps one entry each (the list doesn't grow), and Changes, the menu and the page's line agree",
            list.Count == count && Entries() == entries && HistoryBar.Moves().Count > 0 && LoadOrder.OrderChanged && ModEntry.HasPendingChanges() && LineFollows(),
            $"{list.Count} entries (was {count}), {id}: {Entries()} (was {entries}), moved {HistoryBar.Moves().Count}");
        LoadOrder.Move(other, at, Rows());
        CategoryList.Redraw(content);
        await Wait(menu, 0.3);
        Check("two copies of one mod: moved back, nothing moved for Changes or the menu, and the list is as long as before",
            list.Count == count && Entries() == entries && HistoryBar.Moves().Count == 0 && !LoadOrder.OrderChanged && LineFollows(),
            $"{list.Count} entries (was {count}), moved {HistoryBar.Moves().Count}, order changed {LoadOrder.OrderChanged}");
        var on = list.Where(e => e.Id == id && e.Source == source).Select(e => e.IsEnabled).ToList(); // to put back at the end

        // A click as the game's tickbox makes one (its ForceToggleTick is beta-only): the box flips, then Toggled fires.
        static void Toggle(NModMenuRow row)
        {
            var box = row.GetNode<NTickbox>("Tickbox");
            box.IsTicked = !box.IsTicked;
            box.EmitSignal("Toggled", box);
        }
        static bool Ticked(NModMenuRow row) => row.GetNode<NTickbox>("Tickbox").IsTicked;
        var toggle = screen.GetNode<NButton>("SpireModManagerLoadOrderToggle");
        Click(toggle); // to the main list
        await Wait(menu, 0.5);
        var twinRows = Rows().Where(r => r.Mod != null && twins.Contains(r.Mod)).ToList();
        if (twinRows.Count == 2 && LoadingCopy(id) is { } loads && twinRows.FirstOrDefault(r => r.Mod is { } m && Copies.CopyOf(m) == loads) is { Mod: { } loadsMod } loading
            && twinRows.FirstOrDefault(r => r != loading) is { Mod: { } otherMod } otherRow)
        {
            string Named(Mod mod) => (mod.manifest?.name ?? id) + Copies.Which(mod);
            var wasOn = Copies.Ticked(loadsMod);
            var offWere = CopiesOff();
            // Which loads with both ticked, for the fixture's twins written out rather than worked out by the rule under
            // test: Test Twin Beta (1.1.0, the copy loading now) over Test Twin (1.0.0), as the higher version. Other twins
            // (a --workshop --twins run) have versions not known here, so for them it's the rule's answer (Copies.Best).
            var both = id == TwinId ? "l:SmmTestTwinB"
                : Copies.CopyOf(Copies.Best([loadsMod, otherMod], Copies.Version, c => Copies.TieOrder(c, Store.Data.Copies?.GetValueOrDefault(id), null)).Mod);
            Check("two copies of one mod: a box each on our page, the loading copy's ticked as it's on, the other's not (copies_off)",
                Ticked(loading) == wasOn && !Ticked(otherRow) && !Copies.Ticked(otherMod) && offWere.Contains(Copies.SwitchKey(otherMod)) && CopiesBroken() == null,
                $"boxes {Ticked(loading)}/{Ticked(otherRow)}, on {wasOn}, off {string.Join(", ", offWere)}; {CopiesBroken()}");
            var steps = History.Steps(History.Track.List);
            var (onBefore, offBefore, _) = HistoryBar.Counts();
            Toggle(otherRow);
            await Wait(menu, 0.3);
            var lines = HistoryBar.Describe();
            var (onNow, offNow, _) = HistoryBar.Counts();
            var changed = HistoryBar.ChangedMods();
            Check("two copies of one mod: ticking the other copy ticks it beside the first, one undo step; Changes names only it, +1; the saved list's "
                + "entries all on; Show: Changed lists only that row; the higher version loads (pending only if that's another copy)",
                Ticked(otherRow) && Ticked(loading) == wasOn && Copies.Ticked(otherMod) && Copies.Ticked(loadsMod) == wasOn && LoadingCopy(id) == both
                && !CopiesOff().Contains(Copies.SwitchKey(otherMod)) && list.Where(e => e.Id == id && e.Source == source).All(e => e.IsEnabled)
                && History.Steps(History.Track.List) == steps + 1 && lines.Contains($"Enabled: {Named(otherMod)}") && !lines.Any(l => l.Contains(Named(loadsMod)))
                && onNow - onBefore == 1 && offNow == offBefore && changed.Contains(otherMod) && !changed.Contains(loadsMod)
                && ModEntry.HasPendingChanges() == (both != loads || LoadOrder.OrderChanged) && CopiesBroken() == null,
                $"boxes {Ticked(loading)}/{Ticked(otherRow)}, loads {LoadingCopy(id)} (expected {both}), steps {steps} -> "
                + $"{History.Steps(History.Track.List)}, counts +{onNow - onBefore} -{offNow - offBefore}, pending {ModEntry.HasPendingChanges()}; "
                + $"{string.Join(" | ", lines)}; {CopiesBroken()}");
            History.Undo();
            await Wait(menu, 0.3);
            Check("two copies of one mod: undo puts it back, boxes, copies_off and Changes too", Ticked(loading) == wasOn && !Ticked(otherRow)
                && LoadingCopy(id) == loads && CopiesOff().SequenceEqual(offWere) && !HistoryBar.Describe().Any(l => l.Contains(Named(otherMod))),
                $"boxes {Ticked(loading)}/{Ticked(otherRow)}, loads {LoadingCopy(id)}; {string.Join(" | ", HistoryBar.Describe())}");
            // The loading copy the only one ticked: off, the switch goes off (copies_off kept); on, it comes on alone.
            Dependencies.Set([loadsMod], false, screen);
            var offAll = list.Where(e => e.Id == id && e.Source == source).All(e => !e.IsEnabled) && CopiesOff().SequenceEqual(offWere);
            Dependencies.Set([loadsMod], true, screen);
            var onAll = list.Where(e => e.Id == id && e.Source == source).All(e => e.IsEnabled) && LoadingCopy(id) == loads && CopiesOff().SequenceEqual(offWere);
            Check("two copies of one mod: turning the loading copy off and on sets every one of the mod's entries, copies_off kept",
                offAll && onAll && CopiesBroken() == null, $"off {offAll}, on {onAll}; {string.Join(", ", CopiesOff())}; {CopiesBroken()}");
            // The other copy alone ticked, then off too (the switch off): the copy Enable needed turns on for a mod that needs
            // the id (Dependencies.Find) is the one that was ticked (its own tick), not the one the game loaded. Put back:
            // the loading copy on alone.
            Dependencies.Set([otherMod], true, screen);
            Dependencies.Set([loadsMod], false, screen);
            Dependencies.Set([otherMod], false, screen);
            var needed = Dependencies.Find(id);
            Dependencies.Set([loadsMod], true, screen);
            Check("two copies of one mod: both off, the other copy ticked last: the copy Enable needed turns on is that one, not the one loaded",
                needed == otherMod && LoadingCopy(id) == loads && CopiesOff().SequenceEqual(offWere) && CopiesBroken() == null,
                $"needed {Copies.KeyOf(needed)}; loads {LoadingCopy(id)}, off {string.Join(", ", CopiesOff())}; {CopiesBroken()}");
        }
        else
            Check($"two copies of one mod: a box per copy (rows {twinRows.Count}), one of them loading ({LoadingCopy(id)})", false);
        var restore = list.Where(e => e.Id == id && e.Source == source).ToList();
        for (var i = 0; i < restore.Count && i < on.Count; i++)
            restore[i].IsEnabled = on[i];
        SaveManager.Instance.SaveSettings();
        Profiles.RefreshTickboxes(screen);
        Click(toggle); // back to Load Order
        await Wait(menu, 0.5);
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
        // Auto sort's hint: level with the search box, just left of Auto sort, above the list (so clear of every row).
        void HintPlaced(string when)
        {
            var border = screen.GetNode<Control>("ModsBorder");
            var hint = border.GetNode<Control>(LoadOrder.AutoSortHintName);
            var (at, sort, search, list) = (hint.GetGlobalRect(), border.GetNode<Control>(LoadOrder.AutoSortName).GetGlobalRect(),
                border.GetNode<Control>("SpireModManagerSearch").GetGlobalRect(), border.GetNode<Control>("ModsScrollContainer").GetGlobalRect());
            Check($"{when}: Auto sort's hint level with the search box, left of Auto sort, above the rows", hint.Visible
                && Math.Abs(at.GetCenter().Y - search.GetCenter().Y) < 1 && at.End.X <= sort.Position.X && at.End.Y <= list.Position.Y,
                $"hint {at}, search {search}, Auto sort {sort}, rows from {list.Position.Y}");
        }

        // Straight from the main list, nothing picked (letting go of picked rows redraws on the way, which hid the bug):
        // the hint is in place from the first frame, not where the search box sits on the main list.
        Selection.Clear(content);
        Click(screen.GetNode<NButton>("SpireModManagerLoadOrderToggle"));
        await Wait(menu, 0.5);
        HintPlaced("opening Load Order");
        CategoryList.Redraw(content);
        await Wait(menu, 0.3);
        HintPlaced("Load Order redrawn");
        var start = Ids();
        var startMods = Rows().Select(r => r.Mod).OfType<Mod>().ToList(); // to put back by copy: two copies of a mod share an id
        Check("load order view: saved order, handles, no headers", start.SequenceEqual(Saved())
            && Rows().All(r => r.GetNodeOrNull<Control>("SpireModManagerHandle") is { Visible: true })
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

        // Ours lead the list (one row per copy), pinned: the moves below are of the rows after them.
        var pinned = Ids().TakeWhile(id => id == ModEntry.ModId).Count();
        Check("load order: ours first, pinned", pinned >= 1, string.Join(", ", start));
        // Moves use a mod with no dependencies either way, so no order rule can block them.
        const string free = "SmmTestFixes";
        // The first row after ours, which the move pushes down: its buttons (Top, Up, Down, Bottom) are given their new
        // states and actions in place (Menus.Refill), not made again.
        var passed = Rows()[pinned].Mod?.manifest?.id == free ? null : Rows()[pinned];
        List<ulong> ButtonIds() => passed?.GetNodeOrNull(RowStrip.OrderActionsName)?.GetChildren().Select(c => c.GetInstanceId()).ToList() ?? [];
        List<bool> ButtonsOff() => passed?.GetNodeOrNull(RowStrip.OrderActionsName)?.GetChildren().OfType<Button>().Select(b => b.Disabled).ToList() ?? [];
        var (passedIds, passedOff) = (ButtonIds(), ButtonsOff());
        if (Rows().FirstOrDefault(r => r.Mod?.manifest?.id == free)?.Mod is { } freeMod)
            LoadOrder.Move(freeMod, 0, Rows());
        CategoryList.Redraw(content);
        await Wait(menu, 0.5);
        Check("move to top saves the order, under ours", Ids()[pinned] == free && Saved()[pinned] == free && Ids()[0] == ModEntry.ModId,
            string.Join(", ", Ids()));
        if (passed != null)
            Check("the row the move pushed down keeps its buttons, given their new states (Top and Up free now)", passedIds.Count == 4
                && ButtonIds().SequenceEqual(passedIds) && passedOff.SequenceEqual([true, true, false, false]) && ButtonsOff().SequenceEqual([false, false, false, false]),
                $"{string.Join(",", passedIds)} -> {string.Join(",", ButtonIds())}; disabled {string.Join(",", passedOff)} -> {string.Join(",", ButtonsOff())}");
        else
            Results.Add($"SKIP a move's passed row keeps its buttons: {free} was first after ours already");
        Check("an order change is pending", pending.Visible && ModEntry.HasPendingChanges());
        Check("load order: no tickboxes", Rows().All(r => !r.GetNode<Control>("Tickbox").Visible));
        // The first row's highlight (the same box under the mouse) reaches a little above the row; with the list at its
        // top it shows whole, under the list's top edge, its rounded top not cut off.
        CategoryList.ScrollToTop(content);
        CategoryList.Select(screen, Rows()[0]);
        await Wait(menu, 0.2);
        var topLook = Rows()[0].GetNode<Control>("SelectionHighlight").GetGlobalRect();
        var listTop = content.GetParent<Control>().GetGlobalRect().Position.Y;
        Check("load order: the first row's highlight shows whole, under the list's top edge", topLook.Position.Y >= listTop - 0.01f
            && topLook.Position.Y < Rows()[0].GetGlobalRect().Position.Y, $"highlight from {topLook.Position.Y}, row from {Rows()[0].GetGlobalRect().Position.Y}, list from {listTop}");
        var wasText = Rows()[pinned].GetNodeOrNull<Label>("SpireModManagerWas")?.Text;
        Check("a moved row shows where it was at launch", wasText == $"{start.IndexOf(free) + 1} →",
            wasText ?? "none");
        if (Rows().FirstOrDefault(r => r.Mod?.manifest?.id == "SmmTestUi") is { Mod: { } uiMod } uiRow)
        {
            var toggle = LoadOrder.Actions(content, uiRow, () => CategoryList.Redraw(content))[0];
            toggle.OnSelect?.Invoke();
            await Wait(menu, 0.3);
            var offNow = !Copies.Ticked(uiMod);
            var again = LoadOrder.Actions(content, uiRow, () => CategoryList.Redraw(content))[0];
            again.OnSelect?.Invoke();
            await Wait(menu, 0.3);
            Check("load order menu: Disable, then Enable", toggle.Label == "Disable" && offNow && again.Label == "Enable" && Copies.Ticked(uiMod),
                $"{toggle.Label}, off {offNow}, {again.Label}");
        }
        // Its Up, a button kept through the move, moves it from where it is now: to the top (under ours) again. Then the moved mod goes
        // back to the top for the drags below.
        if (passed?.GetNodeOrNull(RowStrip.OrderActionsName)?.GetChildOrNull<Button>(1) is { } up)
        {
            up.EmitSignal(BaseButton.SignalName.Pressed);
            await Wait(menu, 0.5);
            var upped = Ids();
            Check("a kept Up button moves its row from where it is now (to the top, under ours)", upped[pinned] == passed.Mod?.manifest?.id && upped[pinned + 1] == free
                && ButtonIds().SequenceEqual(passedIds) && ButtonsOff().SequenceEqual([true, true, false, false]), string.Join(", ", upped.Take(pinned + 2)));
            if (Rows().FirstOrDefault(r => r.Mod?.manifest?.id == free)?.Mod is { } freeAgain)
                LoadOrder.Move(freeAgain, 0, Rows());
            CategoryList.Redraw(content);
            await Wait(menu, 0.5);
        }

        // Drag the first row after ours by its handle to just below the third (below the whole of the third's mod where it's
        // installed more than once: a drop never splits a mod's rows).
        var first = Rows()[pinned];
        var handle = first.GetNode<Control>("SpireModManagerHandle");
        var from = handle.GetGlobalRect().GetCenter();
        var third = pinned + 2;
        while (third + 1 < Rows().Count && Rows()[third + 1].Mod?.manifest?.id is { } next && next == Rows()[third].Mod?.manifest?.id)
            third++;
        var to = new Vector2(from.X, Rows()[third].GetGlobalRect().End.Y - 4);
        void Send(InputEvent e) => handle.GetParent().EmitSignal(Control.SignalName.GuiInput, e);
        Send(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, GlobalPosition = from, Position = from });
        Send(new InputEventMouseMotion { GlobalPosition = to, Position = to });
        await Wait(menu, 0.3);
        await Capture(screen, outDir, "11-load-order-drag");
        Send(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, GlobalPosition = to, Position = to });
        await Wait(menu, 0.5);
        var dragged = Ids();
        Check("drag moves the row below the third (and its mod's other rows)", dragged.IndexOf(first.Mod?.manifest?.id ?? "") == third && dragged.SequenceEqual(Saved()),
            string.Join(", ", dragged));

        // A press alone already shows the drag (dimmed list, drop line); releasing without moving changes nothing.
        var pressed = Rows()[pinned];
        var at = pressed.GetGlobalRect().GetCenter();
        var orderBefore = Ids();
        pressed.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, GlobalPosition = at });
        await Wait(menu, 0.1);
        Check("pressing a row starts the drag look at once (the drop line's ends slightly rounded)", Rows().Any(r => r.Modulate.A < 0.5f && r != pressed)
            && content.GetParent().GetChildren().OfType<Panel>().Any(c => c.Size.Y == 5 && c.GetThemeStylebox("panel") is StyleBoxFlat { CornerRadiusTopLeft: > 0 }));
        pressed.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, GlobalPosition = at });
        await Wait(menu, 0.3);
        Check("a click without moving leaves the order alone", Ids().SequenceEqual(orderBefore));
        // The list hidden mid-drag (the page left with the button held): the drag stops, its drop line gone, and a release
        // from the hidden list's top (Godot's, as the row hides) or after it shows again drops nothing.
        var scroller = content.GetParent().GetParent<Control>();
        var held = Rows()[pinned + 2]; // a drop at the top would move it
        var heldAt = held.GetGlobalRect().GetCenter();
        void Mouse(InputEvent e) => held.EmitSignal(Control.SignalName.GuiInput, e);
        var hiddenDrags = new List<string>();
        foreach (var atOnce in new[] { true, false })
        {
            var top = new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, GlobalPosition = new Vector2(heldAt.X, 10) };
            Mouse(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, GlobalPosition = heldAt });
            Mouse(new InputEventMouseMotion { GlobalPosition = heldAt + new Vector2(0, 40) });
            await Wait(menu, 0.1);
            scroller.Visible = false;
            int lines;
            try
            {
                if (atOnce)
                    Mouse(top);
                await Wait(menu, 0.1);
                lines = content.GetParent().GetChildren().OfType<Panel>().Count(c => c.Size.Y == 5 && !c.IsQueuedForDeletion());
            }
            finally
            {
                scroller.Visible = true; // a throw while it's hidden would hide the list for every later test
            }
            if (!atOnce)
                Mouse(top);
            await Wait(menu, 0.3);
            hiddenDrags.Add($"{(atOnce ? "released" : "held")}: {lines} lines, {(Ids().SequenceEqual(orderBefore) ? "kept" : "moved")}");
        }
        Check("the list hidden mid-drag: the drag stops (no drop line left) and no release drops the row",
            hiddenDrags.All(d => d.EndsWith(": 0 lines, kept")), string.Join("; ", hiddenDrags));

        // Multi-select: pick the first and third rows after ours, drag the first's grip below the last row; both move,
        // in their order, each with its other copies' rows (a mod installed more than once moves as one block).
        var all = Rows();
        Selection.Pick(content, all[pinned], toggle: false, range: false);
        Selection.Pick(content, all[pinned + 2], toggle: true, range: false);
        var pickedMods = new[] { all[pinned], all[pinned + 2] }.Select(r => r.Mod?.manifest?.id ?? "").ToHashSet();
        var pickedIds = all.Select(r => r.Mod?.manifest?.id ?? "").Where(pickedMods.Contains).ToList();
        Check("Ctrl-click picks a second row, both highlighted", Selection.Mods.Count == 2
            && Rows().Count(r => r.GetNodeOrNull<Control>("SpireModManagerPicked")?.Visible == true) == 2);
        var grip = all[pinned].GetNode<Control>("SpireModManagerHandle");
        var gripAt = grip.GetGlobalRect().GetCenter();
        var bottom = new Vector2(gripAt.X, all[^1].GetGlobalRect().End.Y - 2);
        grip.GetParent().EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, GlobalPosition = gripAt });
        grip.GetParent().EmitSignal(Control.SignalName.GuiInput, new InputEventMouseMotion { GlobalPosition = bottom });
        await Wait(menu, 0.2);
        await Capture(screen, outDir, "11b-multi-drag");
        grip.GetParent().EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, GlobalPosition = bottom });
        await Wait(menu, 0.5);
        Check("dragging a multi-selection moves the group, in order (each mod with all its rows)", Ids().TakeLast(pickedIds.Count).SequenceEqual(pickedIds),
            string.Join(", ", Ids()));
        Selection.Clear(content);

        // Put the original order back: nothing pending again.
        for (var i = 0; i < startMods.Count; i++)
            LoadOrder.Move(startMods[i], i, Rows());
        CategoryList.Redraw(content);
        await Wait(menu, 0.5);
        Check("restoring the order clears pending", Ids().SequenceEqual(start) && !ModEntry.HasPendingChanges());
        await TestTwinCopies(menu, screen, content);
        await TestSameIdLoadOrder(menu, screen, content);

        await TestDependencies(menu, screen, content, outDir);

        // Leaving Load Order: a plain category list again, the selected mod still selected, its collapsed
        // category opened, no lines or dimming left behind.
        if (Rows().FirstOrDefault(r => r.Mod?.manifest?.id == "SmmTestFixes") is { Mod: { } kept } keptRow)
        {
            CategoryList.Select(screen, keptRow);
            Store.Data.UncategorizedCollapsed = true;
        }
        LoadOrder.ShowLinks(content, Rows()[0], strong: false);
        Click(screen.GetNode<NButton>("SpireModManagerLoadOrderToggle"));
        await Wait(menu, 0.5);
        var selected = Rows().FirstOrDefault(r => r.Mod == CategoryList.Selected);
        Check("category view hides the handles", !Rows().Any(r => r.GetNodeOrNull<Control>("SpireModManagerHandle") is { Visible: true })
            && content.GetChildren().Any(c => c.IsInGroup("SpireModManagerCategoryHeader")));
        Check("back from Load Order: titles in place", Rows().All(r => r.GetNode<Control>("Title").OffsetLeft == 18),
            string.Join(", ", Rows().Select(r => r.GetNode<Control>("Title").OffsetLeft)));
        Check("back from Load Order: no lines or dimming", !content.GetParent().GetChildren().Any(c => c.IsInGroup("SpireModManagerLinks"))
            && Rows().All(r => r.Modulate == Colors.White));
        Check("the selection survives the switch, its category opened", selected?.Mod?.manifest?.id == "SmmTestFixes"
            && selected.Visible && !Store.Data.UncategorizedCollapsed);
        // Jordan (23 Oct): the category bar's edges in line with a highlighted row's, left and right.
        var bar = content.GetChildren().OfType<Control>().FirstOrDefault(c => c.IsInGroup("SpireModManagerCategoryHeader") && c.Visible)?.GetNodeOrNull<Control>("Background");
        var lit = selected?.GetNodeOrNull<Control>("SelectionHighlight");
        var (barRect, litRect) = (bar?.GetGlobalRect() ?? default, lit?.GetGlobalRect() ?? default);
        Check("category bars line up with a highlighted row, left and right edges", bar != null && lit is { Modulate.A: > 0.5f }
            && Math.Abs(barRect.Position.X - litRect.Position.X) < 0.5f && Math.Abs(barRect.End.X - litRect.End.X) < 0.5f,
            $"bar {barRect.Position.X:0.#}-{barRect.End.X:0.#}, highlight {litRect.Position.X:0.#}-{litRect.End.X:0.#}");
    }

    // Fixture mods: SmmTestContent and SmmTestUi need SmmTestLib; SmmTestAddon needs SmmTestMissing, which never is there.
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
        var orderWas = LoadOrder.Mods(Rows());
        Check("dependency note names what it needs", Note("SmmTestContent").Contains("Test Framework"), Note("SmmTestContent"));
        if (WontLoad() != null)
            Check("missing dependency is called out", Note("SmmTestAddon").Contains(Missing + " (not installed)"), Note("SmmTestAddon"));
        else
            Results.Add("SKIP missing dependency is called out: Test Addon not installed, or off");

        // The info panel: where it's from, and dependencies both ways.
        var panel = screen.GetNode<NModInfoContainer>("%ModInfoContainer");
        if (Row("SmmTestLib") is { } libSelected)
        {
            CategoryList.Select(screen, libSelected);
            await PanelFor(menu, screen, libSelected);
        }
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
        Check("the order warning's corners are rounded like the rows", screen.FindChild(LoadOrder.WarningName, true, false) is PanelContainer warningBar
            && warningBar.GetThemeStylebox("panel") is StyleBoxFlat { CornerRadiusTopLeft: RowTitles.CornerRadius });
        Check("the order warning is red", Red(BannerFill(screen.FindChild(LoadOrder.WarningName, true, false))),
            $"fill {BannerFill(screen.FindChild(LoadOrder.WarningName, true, false))}");
        var fixAfter = ButtonsAfterText(screen.FindChild(LoadOrder.WarningName, true, false));
        Check("Load Order: the order warning's Go to, Show and Fix now right after its text", fixAfter.Ok, fixAfter.Detail);
        // Its Go to goes to the mod loading too early; its Show finds it ("#order": every row in place, "1 match").
        void OrderPress(string name) => (screen.FindChild(LoadOrder.WarningName, true, false)?.FindChild(name, true, false) as BaseButton)
            ?.EmitSignal(BaseButton.SignalName.Pressed);
        if (Row(ModEntry.ModId) is { } elsewhere)
            CategoryList.Select(screen, elsewhere);
        OrderPress("GoToOrderButton");
        await Wait(menu, 0.3);
        var goneTo = CategoryList.Selected;
        OrderPress("FilterOrderButton");
        await Wait(menu, 0.3);
        var searchBox = screen.GetNode<LineEdit>("ModsBorder/SpireModManagerSearch");
        bool OrderLit() => screen.FindChild(LoadOrder.WarningName, true, false)?.FindChild("FilterOrderButton", true, false) is Button { } show
            && show.HasThemeStyleboxOverride("normal"); // the chosen look
        var (orderBox, orderFound, everyRow, orderLit) = (searchBox.Text, (screen.FindChild(FindBar.Name, true, false)?.GetNodeOrNull<Label>("Count"))?.Text,
            Rows().All(r => r.Visible), OrderLit());
        OrderPress("FilterOrderButton");
        await Wait(menu, 0.3);
        Check("Load Order: the order warning's Go to goes to Test Content; its Show puts \"#order\" in the box and finds it alone, every row in place; again, the box empty",
            goneTo == dependent && orderBox == "#order" && orderFound == "1 match" && everyRow && searchBox.Text == "",
            $"went to {goneTo?.manifest?.id}; box \"{orderBox}\", find \"{orderFound}\", every row {everyRow}; then \"{searchBox.Text}\"");
        var orderWords = (Go: (screen.FindChild(LoadOrder.WarningName, true, false)?.FindChild("GoToOrderButton", true, false) as Button)?.Text,
            Show: (screen.FindChild(LoadOrder.WarningName, true, false)?.FindChild("FilterOrderButton", true, false) as Button)?.Text);
        var orderShowTip = TipOf(screen.FindChild(LoadOrder.WarningName, true, false)?.FindChild("FilterOrderButton", true, false));
        Check("Load Order: the order warning's Show is lit while the box holds \"#order\", not after; its buttons read \"Go to\" and \"Show\"; its tip says it "
            + "finds them (Load Order hides no row)",
            orderLit && !OrderLit() && orderWords == (GoToProblem, ShowFilter) && orderShowTip == ShowFilterFindTip, $"lit {orderLit}, after {OrderLit()}; {orderWords}; {orderShowTip}");
        // The hidden tags on the list as it is: Test Content #order (and #moved: it was moved here), each finding the rows it means.
        var asMeant = HiddenTagsAsMeant(content);
        Check("bad order: hidden tags on the list as it is (Test Content #order and #moved), each finding the rows it means",
            asMeant.Wrong.Count == 0 && SpireModManager.Search.Matches(dependent, "#order") && SpireModManager.Search.Matches(dependent, "#moved")
            && !SpireModManager.Search.Matches(lib, "#order"), $"{string.Join(" | ", asMeant.Wrong)}; found {asMeant.Found}");
        Check("Auto sort has its hint", screen.FindChild("AutoSortHint", true, false) is Label { Visible: true } hintLabel && hintLabel.Text.Contains("compatibility"));
        Check("order warning and Auto sort stay above the list, outside its scrolling",
            screen.FindChild("OrderProblems", true, false) is Node warning && !content.IsAncestorOf(warning)
            && screen.FindChild("AutoSortButton", true, false) is Control { Visible: true } sort && !content.IsAncestorOf(sort));
        await Capture(screen, outDir, "12-order-problem");

        // Hovering the warning's text: a card naming each mod placed before a mod it needs, in load order, and nothing
        // else. Test UI moved there too joins it; ticked off (its menu's Disable) it leaves again. Enabled again, and the
        // move undone (this view's undo steps through the order only), it's back as it was.
        var orderCard = await BannerCard(menu, screen, screen.FindChild(LoadOrder.WarningName, true, false));
        Check("the order warning's card: \"Test Content loads before Test Framework\", nothing else",
            string.Join(" | ", orderCard) == "Affected mods | Test Content loads before Test Framework", string.Join(" | ", orderCard));
        // Both by the names the list shows: each with an alias, then as they were.
        var aliasesWere = (Content: Store.InfoFor("SmmTestContent")?.Alias, Lib: Store.InfoFor("SmmTestLib")?.Alias);
        Store.EditMod("SmmTestContent", info => info.Alias = "Content");
        Store.EditMod("SmmTestLib", info => info.Alias = "Framework");
        CategoryList.Redraw(content);
        await Wait(menu, 0.3);
        var aliased = string.Join(" | ", await BannerCard(menu, screen, screen.FindChild(LoadOrder.WarningName, true, false)));
        Store.EditMod("SmmTestContent", info => info.Alias = aliasesWere.Content);
        Store.EditMod("SmmTestLib", info => info.Alias = aliasesWere.Lib);
        CategoryList.Redraw(content);
        await Wait(menu, 0.3);
        Check("the order warning's card by the names the list shows: \"Content loads before Framework\" with both aliased",
            aliased == "Affected mods | Content loads before Framework", aliased);
        if (Row("SmmTestUi") is { Mod: { } ui } uiRow)
        {
            var idsWere = Ids();
            LoadOrder.Move(ui, Ids().IndexOf("SmmTestLib"), Rows());
            CategoryList.Redraw(content);
            await Wait(menu, 0.3);
            var both = string.Join(" | ", await BannerCard(menu, screen, screen.FindChild(LoadOrder.WarningName, true, false)));
            LoadOrder.Actions(content, uiRow, () => CategoryList.Redraw(content))[0].OnSelect?.Invoke(); // Disable
            await Wait(menu, 0.3);
            var off = string.Join(" | ", await BannerCard(menu, screen, screen.FindChild(LoadOrder.WarningName, true, false)));
            Check("the order warning's card follows: Test UI moved before the framework too, both in load order; ticked off, Test Content alone",
                both == "Affected mods | Test Content loads before Test Framework | Test UI loads before Test Framework"
                && off == "Affected mods | Test Content loads before Test Framework", $"{both}; then {off}");
            LoadOrder.Actions(content, uiRow, () => CategoryList.Redraw(content))[0].OnSelect?.Invoke(); // Enable
            await Wait(menu, 0.3);
            History.Undo();
            await Wait(menu, 0.3);
            Check("enabled again and the move undone: Test UI on, in its place", Copies.Ticked(ui) && Ids().SequenceEqual(idsWere), string.Join(", ", Ids()));
        }
        else
            Results.Add("FAIL the order warning's card: no row for Test UI");

        var before = Ids();
        screen.FindChild("FixOrderButton", true, false)?.EmitSignal(BaseButton.SignalName.Pressed);
        await Wait(menu, 0.5);
        var after = Ids();
        Check("Fix order puts the framework first and nothing else moves",
            after.IndexOf("SmmTestLib") < after.IndexOf("SmmTestContent") && after.IndexOf("SmmTestLib") < after.IndexOf("SmmTestUi")
            && screen.FindChild("OrderProblems", true, false) == null
            && after.Where(id => id != "SmmTestLib").SequenceEqual(before.Where(id => id != "SmmTestLib")),
            string.Join(", ", after));

        // A move above the framework is flagged but allowed: by the inline/menu actions and by dragging. Ours, pinned
        // first, counted before (counted after, a drop above ours would count none).
        var pinned = Ids().TakeWhile(id => id == ModEntry.ModId).Count();
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
                Row("SmmTestFixes")?.Modulate.A < 1 && Row("SmmTestLib")?.Modulate.A == 1 && links == 1, $"{links} links");
            Check("grabbing a row selects it", CategoryList.Selected == dependent);
            handle.GetParent().EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, GlobalPosition = to });
            await Wait(menu, 0.5);
        }
        // Dropped at the top, it lands under ours (pinned first).
        Check("a drag past a dependency goes through and is flagged, landing under ours", pinned > 0 && Ids()[0] == ModEntry.ModId
            && Ids()[pinned] == "SmmTestContent" && screen.FindChild("OrderProblems", true, false) != null, string.Join(", ", Ids()));
        screen.FindChild("FixOrderButton", true, false)?.EmitSignal(BaseButton.SignalName.Pressed);
        await Wait(menu, 0.5);
        string? SavedFirst() => SaveManager.Instance.SettingsSave.ModSettings?.ModList.FirstOrDefault()?.Id;
        Check("Fix order leaves ours first", Ids()[0] == ModEntry.ModId && SavedFirst() == ModEntry.ModId, string.Join(", ", Ids()));

        // Auto sort: libraries, then gameplay, then visual-only; dependencies satisfied; one undo puts it back.
        var beforeSort = Ids();
        screen.FindChild("AutoSortButton", true, false)?.EmitSignal(BaseButton.SignalName.Pressed);
        await Wait(menu, 0.5);
        int Tier(NModMenuRow r) => Tags.For(r.Mod).Any(c => c.Text == "Library") ? 0
            : Tags.For(r.Mod).Any(c => c.Text == "Gameplay") ? 1 : 2;
        var tiers = Rows().Skip(pinned).Select(Tier).ToList(); // ours stay first, whatever their tier
        Check("auto sort: ours first, then libraries, then gameplay, then visual-only, no dependency problems", Ids()[0] == ModEntry.ModId
            && SavedFirst() == ModEntry.ModId && Ids().Take(pinned).All(id => id == ModEntry.ModId) &&
            tiers.Zip(tiers.Skip(1)).All(p => p.First <= p.Second) && Dependencies.OrderViolations(LoadOrder.Mods(Rows())).Count == 0,
            string.Join(", ", Ids()));
        await Capture(screen, outDir, "13c-auto-sort");
        History.Undo();
        await Wait(menu, 0.5);
        Check("one undo restores the order from before auto sort", Ids().SequenceEqual(beforeSort), string.Join(", ", Ids()));
        await Wait(menu, 0.2);
        Check("dropping restores the list look (only mods that are off stay dim)", Rows().All(r => r.Modulate == (r.Mod is { } m && !Copies.Ticked(m) ? new Color(1, 1, 1, 0.4f) : Colors.White))
            && !content.GetParent().GetChildren().Any(c => c.IsInGroup("SpireModManagerLinks")));

        // Hovering the framework shows a line to each mod that needs it.
        if (Row("SmmTestLib") is { } libRow)
        {
            libRow.EmitSignal(Control.SignalName.MouseEntered);
            await Wait(menu, 0.2);
            var shown = content.GetParent().GetChildren().Count(c => c.IsInGroup("SpireModManagerLinks"));
            // Rounded corners: more points than the square path (out, along the lane, in), none on its two corners,
            // the ends where they were.
            static bool Rounded(Vector2[] p)
            {
                if (p.Length <= 4)
                    return false;
                var lane = p.Min(q => q.X);
                Vector2[] corners = [new(lane, p[0].Y), new(lane, p[^1].Y)];
                return p[0].X == p[^1].X && p[0].X > lane && !p.Any(q => corners.Any(c => q.DistanceTo(c) < 0.5f));
            }
            var paths = content.GetParent().GetChildren().OfType<Line2D>().Where(l => l.IsInGroup("SpireModManagerLinks")).Select(l => l.Points).ToList();
            Check("the hover lines have slightly rounded corners", paths.Count > 0 && paths.All(Rounded),
                string.Join("; ", paths.Select(p => string.Join(" ", p.Select(q => $"{q.X:0.#},{q.Y:0.#}")))));
            // The rows here wear the main list's rounded looks: faint under the mouse, brighter when selected, with an even
            // gold rim (the same width on every side, so its edges line up with the other rows' boxes).
            var selectedRow = Rows().FirstOrDefault(r => r.Mod == CategoryList.Selected && r != libRow);
            StyleBox? LookOf(NModMenuRow? r) => r?.GetNodeOrNull<Panel>("SelectionHighlight")?.GetThemeStylebox("panel");
            Check("load order rows: rounded like the main list's, under the mouse and selected; the selected one brighter, with an even gold rim",
                LookOf(libRow) is StyleBoxFlat { CornerRadiusTopLeft: RowTitles.CornerRadius, BorderWidthTop: 0, BorderWidthLeft: 0 } hoverLook
                && LookOf(selectedRow) is StyleBoxFlat { CornerRadiusTopLeft: RowTitles.CornerRadius } selectedBox && EvenGoldRim(selectedBox)
                && selectedBox.BgColor.A > hoverLook.BgColor.A
                && selectedRow?.GetNode<Panel>("SelectionHighlight") is { } selectedLook && selectedLook.Modulate == Colors.White
                && selectedLook.OffsetLeft == RowTitles.LookInset.X, $"selected: {selectedRow?.Mod?.manifest?.id ?? "none"}");
            await Capture(screen, outDir, "13b-hover-links");
            libRow.EmitSignal(Control.SignalName.MouseExited);
            await Wait(menu, 0.2);
            // Test Content, Test UI and the fixture's newer twin, Test Twin Beta.
            Check("hover shows links to each mod that needs it, and they go on leaving", shown == Dependencies.NeededBy("SmmTestLib").Count() && shown == 3
                && !content.GetParent().GetChildren().Any(c => c.IsInGroup("SpireModManagerLinks")), $"{shown} shown");
        }

        // The start's order back (Fix order left the framework and Test Content moved): the tests after this one start with
        // nothing moved since launch, as a Revert all's undo brings a move back with the rest.
        for (var i = 0; i < orderWas.Count; i++)
            LoadOrder.Move(orderWas[i], i, Rows());
        CategoryList.Redraw(content);
        await Wait(menu, 0.3);
        Check("dependencies put back: the order from the start, nothing moved since launch", LoadOrder.Mods(Rows()).SequenceEqual(orderWas) && !LoadOrder.OrderChanged,
            string.Join(", ", HistoryBar.Moves().Select(m => $"{m.Mod.manifest?.id} {m.Before}→{m.After}")));
    }

    // The bar under the search box: Enable all / Disable all for the page (or the search's matches),
    // Expand all / Collapse all. Category headers: the same two inline, last. Rows: the author at the end.
    static async Task TestPageBar(Node menu, NModdingScreen screen, Control content, string outDir)
    {
        List<NModMenuRow> Rows() => content.GetChildren().OfType<NModMenuRow>().ToList();
        Control? Bar() => screen.FindChild(PageBar.Name, true, false) as Control;
        void Press(string name) => Bar()?.FindChild(name, true, false)?.EmitSignal(BaseButton.SignalName.Pressed);
        Check("page bar on the main list", Bar() is { Visible: true } bar && bar.FindChild("Scope", true, false) is Label { Text: var all } && all.StartsWith("Mods installed"));
        // Each choice with how many mods it's for ("All (20)"), the same counts as the scope's for the chosen one, and
        // room for a 3-digit count (PageBar.RoomFor), so a count changing never moves the bar; the bar still fits its row.
        var filterNames = new[] { ("ShowAllButton", "All", ListFilter.Choice.All), ("ShowEnabledButton", "Enabled", ListFilter.Choice.Enabled),
            ("ShowDisabledButton", "Disabled", ListFilter.Choice.Disabled), ("ShowChangedButton", "Changed", ListFilter.Choice.Changed) };
        var filterTexts = filterNames.Select(f => (Bar()?.FindChild(f.Item1, true, false) as Button, f.Item2, Rows().Count(r => ListFilter.Matches(r.Mod, f.Item3)))).ToList();
        Check("the filters say how many mods each is for: All (n), Enabled (n), Disabled (n), Changed (n)",
            filterTexts.All(f => f.Item1?.Text == $"{f.Item2} ({Menus.Number(f.Item3)})") && filterTexts[0].Item3 == Rows().Count
            && filterTexts[1].Item3 + filterTexts[2].Item3 == Rows().Count(r => r.Mod != null),
            string.Join(" | ", filterTexts.Select(f => $"{f.Item1?.Text} / {f.Item3}")));
        Check("each filter keeps room for a 3-digit count: its width doesn't follow its count, and the bar fits its row",
            filterTexts.All(f => f.Item1 is { } b && b.CustomMinimumSize.X == PageBar.Roomy(b, f.Item2) && Math.Abs(b.Size.X - b.CustomMinimumSize.X) < 0.5f
                && b.CustomMinimumSize.X >= b.GetThemeFont("font").GetStringSize($"{f.Item2} ({new string('8', PageBar.RoomFor)})", fontSize: b.GetThemeFontSize("font_size")).X)
            && Bar() is { } fits && fits.GetCombinedMinimumSize().X <= fits.GetParent<Control>().Size.X - fits.OffsetLeft + fits.OffsetRight + 0.5f,
            $"{string.Join(", ", filterTexts.Select(f => $"{f.Item1?.Size.X:0}/{f.Item1?.CustomMinimumSize.X:0}"))}; bar {Bar()?.GetCombinedMinimumSize().X:0} in {(Bar() is { } room ? room.GetParent<Control>().Size.X - room.OffsetLeft + room.OffsetRight : 0):0}");
        await Capture(screen, outDir, "2d-page-bar");
        // What each filter shows: our hover card (not the engine's tooltip), only as wide as its words. And no engine
        // tooltip on anything of ours: every description we write is the same card. Other mods'
        // controls on the page (Better Mod Menu's row buttons) keep theirs. tools/check.cs guards the source too.
        if (Bar()?.FindChild("ShowAllButton", true, false) is Button allButton)
        {
            var (tip, width) = await SizedHoverCard(menu, allButton, Hover.TipName, () => Capture(screen, outDir, "2d2-filter-tip"));
            Check("a filter's description is our hover card, as wide as its words", tip == Menus.Text("SPIRE_MOD_MANAGER.SHOW_ALL_TIP")
                && width is > 0 and < 300 && allButton.TooltipText == "", $"\"{tip}\", {width:0} px");
        }
        bool Ours(Node node)
        {
            for (var at = node; at != null && at != screen; at = at.GetParent())
                if (at.Name.ToString().StartsWith("SpireModManager") || at.HasMeta("spire_mod_manager_tip"))
                    return true;
            return false;
        }
        var natives = screen.FindChildren("*", "Control", true, false).OfType<Control>().Where(c => c.TooltipText.Length > 0 && Ours(c))
            .Select(c => $"{c.Name}: {c.TooltipText}").ToList();
        Check("no engine tooltips on our controls: every description is our card", natives.Count == 0, string.Join(" | ", natives));

        var box = screen.GetNode<LineEdit>("ModsBorder/SpireModManagerSearch");
        void Find(string text)
        {
            box.Text = text;
            box.EmitSignal(LineEdit.SignalName.TextChanged, text);
        }
        Find("#Gameplay");
        await Wait(menu, 0.3);
        var matched = Rows().Where(r => SpireModManager.Search.Matches(r.Mod, "#Gameplay")).Select(r => r.Mod).OfType<Mod>().ToList();
        var others = Rows().Select(r => r.Mod).OfType<Mod>().Except(matched).ToDictionary(m => m, Copies.Ticked);
        Check("searching: the bar says it acts on the matches", (Bar()?.FindChild("Scope", true, false) as Label)?.Text.StartsWith("Search matches") == true);
        Press("DisableAllButton");
        await Wait(menu, 0.3);
        Check("Disable all turns off just the matches", matched.All(m => !Copies.Ticked(m)) && others.All(p => Copies.Ticked(p.Key) == p.Value),
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
        // A header's click folds its category and the bar's Expand all follows at once; while searching every category
        // shows open, so a click there folds nothing out of sight.
        if (Store.Data.Categories.FirstOrDefault(c => Rows().Any(r => Store.InfoFor(r.Mod)?.Category == c.Name)) is { } folding)
        {
            void ClickHeader()
            {
                foreach (var pressed in new[] { true, false })
                    Header(content, folding.Name)?.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = pressed });
            }
            Button? ExpandAll() => Bar()?.FindChild("ExpandAllButton", true, false) as Button;
            Find(Rows().First(r => Store.InfoFor(r.Mod)?.Category == folding.Name).Mod?.manifest?.id ?? "");
            await Wait(menu, 0.3);
            ClickHeader();
            var foldedSearching = folding.Collapsed;
            Find("");
            await Wait(menu, 0.3);
            ClickHeader();
            await Wait(menu, 0.3);
            Check("a header's click folds its category and the bar's Expand all comes on; while searching it folds nothing",
                !foldedSearching && folding.Collapsed && ExpandAll() is { Disabled: false }, $"searching {foldedSearching}, then {folding.Collapsed}, Expand all off {ExpandAll()?.Disabled}");
            ClickHeader();
            await Wait(menu, 0.3);
        }
        else
            Results.Add("SKIP a header's click: no category holds a mod");

        var headerButtons = (Header(content, Store.Data.Categories.First().Name) as Node)?.GetNodeOrNull("Actions")?.GetChildren().OfType<Button>().Select(b => b.Text).ToList() ?? [];
        Check("category headers: Enable all, Disable all inline, last", headerButtons.TakeLast(2).SequenceEqual(["Enable all", "Disable all"]), string.Join(",", headerButtons));
        var uncategorized = (content.GetNodeOrNull("Uncategorized")?.GetNodeOrNull("Actions")?.GetChildren().OfType<Button>().Select(b => b.Text).ToList()) ?? [];
        Check("Uncategorized: just Enable all, Disable all", uncategorized.SequenceEqual(["Enable all", "Disable all"]), string.Join(",", uncategorized));
        var author = Rows().FirstOrDefault(r => r.Mod?.manifest?.id == "SmmTestFixes")?.GetNodeOrNull<Label>("SpireModManagerStrip/Author")?.Text;
        Check("the author at the end of the row's line", author == "symphony", author ?? "none");

        // Mod names in the panel: hovering lights the row (it's in view); clicking scrolls to it, selected look.
        if (Rows().FirstOrDefault(r => r.Mod?.manifest?.id == "SmmTestLib") is { } libRow
            && Rows().FirstOrDefault(r => r.Mod?.manifest?.id == "SmmTestUi") is { } uiRow)
        {
            CategoryList.Select(screen, libRow);
            await PanelFor(menu, screen, libRow);
            await Wait(menu, 0.3);
            // Its row in view, as hovering lights only one on screen (the names sort apart).
            if (!content.GetParent<Control>().GetGlobalRect().Intersects(uiRow.GetGlobalRect()))
            {
                CategoryList.ScrollTo(content, uiRow);
                await Wait(menu, 1.5);
            }
            var description = screen.GetNode<RichTextLabel>("%ModInfoContainer/ModDescription");
            var look = uiRow.GetNode<Panel>("SelectionHighlight");
            var before = look.Modulate;
            description.EmitSignal(RichTextLabel.SignalName.MetaHoverStarted, "mod:SmmTestUi");
            var lit = look.Modulate;
            var litFill = (look.GetThemeStylebox("panel") as StyleBoxFlat)?.BgColor.A ?? 0;
            description.EmitSignal(RichTextLabel.SignalName.MetaHoverEnded, "mod:SmmTestUi");
            Check("hovering a mod's name in the panel lights its row (visibly), and lets go", lit.A * litFill > 0.05f && look.Modulate == before, $"{before} -> {lit} x {litFill} -> {look.Modulate}");
            description.EmitSignal(RichTextLabel.SignalName.MetaClicked, "mod:SmmTestUi");
            await WaitReal(menu, 0.1); // past ScrollSoon's 0.05 s: the row focused
            Check("clicking it gives the row the selected look; the panel stays", uiRow.HasFocus() && CategoryList.Selected?.manifest?.id == "SmmTestLib",
                $"focus {uiRow.HasFocus()} (focused: {screen.GetViewport().GuiGetFocusOwner()?.Name}), selected {CategoryList.Selected?.manifest?.id}");
        }
    }

    // The page bar's Show: All, Enabled, Disabled, Changed (ListFilter). Each shows exactly its rows (by the tickboxes;
    // the mods Changes names), with a search too (both must match), and the bar counts them; each category counts its
    // rows shown and has no header when it shows none. Choosing is no undo step and not pending. A row ticked under a
    // choice stays, counted, until a choice is pressed again or many mods change at once (undo, a profile, Disable all);
    // Enable all acts on the rows shown, which then go, as do the mods a profile switched to turns off; picked mods
    // edited in their menu stay; going to a mod shows its row, clearing a search that leaves it out.
    // Load Order and Vanilla List show every row; All shows every row again. (The page opening again:
    // TestListFilterReopen, near the end, as it starts a new visit for undo.)
    static async Task TestListFilters(Node menu, NModdingScreen screen, Control content, string outDir)
    {
        if (Layout.Current != Layout.View.Categories)
        {
            Results.Add("SKIP list filters: not on the main list");
            return;
        }
        List<NModMenuRow> Rows() => content.GetChildren().OfType<NModMenuRow>().ToList();
        Control? Bar() => screen.FindChild(PageBar.Name, true, false) as Control;
        async Task Press(string name)
        {
            Bar()?.FindChild(name, true, false)?.EmitSignal(BaseButton.SignalName.Pressed);
            await Wait(menu, 0.3);
        }
        static List<string> Ids(IEnumerable<NModMenuRow> rows) => [.. rows.Select(r => r.Mod?.manifest?.id ?? "").Order()];
        List<string> Shown() => Ids(Rows().Where(r => r.Visible));
        List<string> Where(Func<Mod, bool> rule) => Ids(Rows().Where(r => r.Mod is { } mod && rule(mod)));
        string Scope() => (Bar()?.FindChild("Scope", true, false) as Label)?.Text ?? "";
        // The choices as the bar shows them, the lit one (white text) with a *.
        // The choices by name ("All (20)" reads All), the lit one starred; and the counts they show, in order.
        IEnumerable<Button> ChoiceButtons() => Bar()?.FindChild("Show", true, false)?.GetChildren().OfType<Button>() ?? [];
        string Choices() => string.Join(" ", ChoiceButtons()
            .Select(b => System.Text.RegularExpressions.Regex.Replace(b.Text, @" \([\d,]+\)$", "") + (b.GetThemeColor("font_color") == Colors.White ? "*" : "")));
        List<int> ChoiceCounts() => ChoiceButtons().Select(b => System.Text.RegularExpressions.Regex.Match(b.Text, @"\(([\d,]+)\)$") is { Success: true } m
            ? int.Parse(m.Groups[1].Value.Replace(",", "")) : -1).ToList();
        string Say() => $"shown {string.Join(", ", Shown())} | {Scope()} | {Choices()}";
        // Each category's header: the count of its rows the rule shows, or no header when that's none. "" when all agree.
        string Headers(Func<Mod, bool> rule)
        {
            var wrong = new List<string>();
            foreach (var name in Store.Data.Categories.Select(c => (string?)c.Name).Append(null))
            {
                var count = Rows().Count(r => r.Mod is { } mod && rule(mod)
                    && (Store.InfoFor(mod.manifest?.id)?.Category is { } c && Categories.Find(c) != null ? c : null) == name);
                var header = content.GetNodeOrNull<Control>(name == null ? "Uncategorized" : $"Category {name}");
                var said = header?.GetNodeOrNull<Label>("Count")?.Text;
                if (count == 0 ? header != null : said != Menus.Number(count))
                    wrong.Add($"{name ?? "Uncategorized"}: {count} shown, header {said ?? "none"}");
            }
            return string.Join("; ", wrong);
        }
        var box = screen.GetNode<LineEdit>("ModsBorder/SpireModManagerSearch");
        async Task Find(string text)
        {
            box.Text = text;
            box.EmitSignal(LineEdit.SignalName.TextChanged, text);
            await Wait(menu, 0.3);
        }
        static bool On(Mod mod) => Copies.Ticked(mod);
        static bool Off(Mod mod) => !Copies.Ticked(mod);

        await Find("");
        if (Bar()?.FindChild("ExpandAllButton", true, false) is Button { Disabled: false })
            await Press("ExpandAllButton"); // every category open: a row shown is a row on screen
        var all = Ids(Rows());
        Check("the page bar: \"Show:\" All, Enabled, Disabled, Changed, All lit", Choices() == "All* Enabled Disabled Changed"
            && (Bar()?.FindChild("ShowLabel", true, false) as Label)?.Text == "Show:" && Shown().SequenceEqual(all), Say());
        // Its buttons all within the list's width, also with a selection's "Clear selection" (the widest the bar gets).
        var list = screen.GetNode<Control>("%ModsScrollContainer").GetGlobalRect();
        string Overflow() => string.Join(", ", Bar()?.FindChildren("*", "Button", true, false).OfType<Button>()
            .Where(b => b.GetGlobalRect().End.X > list.End.X + 0.5f).Select(b => $"{b.Text} to {b.GetGlobalRect().End.X:0}") ?? []);
        var alone = Overflow();
        Selection.Add(content, Rows());
        await Wait(menu, 0.3);
        var (picked, withPicked) = (Scope(), Overflow());
        Selection.Clear(content);
        await Wait(menu, 0.3);
        Check("the page bar fits the list's width, with a selection too", alone == "" && withPicked == "" && picked.StartsWith("Selected"),
            $"alone: {alone}; {picked}: {withPicked}; the list ends at {list.End.X:0}");

        // Choosing: view state, never an edit.
        (string, string, bool, bool, bool) State() =>
            (Store.EditsJson(), string.Join(" | ", HistoryBar.Describe()), ModEntry.HasPendingChanges(), Profiles.Edited, History.CanUndo);
        var before = State();
        var moves = 0;
        void Moved() => moves++; // the history moves when a step is added
        History.Changed += Moved;
        try
        {
            await Press("ShowEnabledButton");
            var on = Where(On);
            Check("Show: Enabled: just the enabled mods, the bar counting them, Enabled lit", Shown().SequenceEqual(on)
                && Scope() == $"All enabled ({on.Count})" && Choices() == "All Enabled* Disabled Changed", Say());
            Check("Show: Enabled: each category counts its enabled mods; none: no header", Headers(On) == "", Headers(On));

            await Press("ShowDisabledButton");
            var off = Where(Off);
            Results.Add($"INFO {on.Count} enabled, {off.Count} disabled of {all.Count}");
            Check("Show: Disabled: just the disabled mods, counted, Disabled lit", Shown().SequenceEqual(off)
                && Scope() == $"All disabled ({off.Count})" && Choices() == "All Enabled Disabled* Changed", Say());
            Check("Show: Disabled: each category counts its disabled mods; none: no header", Headers(Off) == "", Headers(Off));
            Results.Add($"INFO headers under Disabled: {content.GetChildren().Count(c => c.IsInGroup("SpireModManagerCategoryHeader") && c.Name != "Uncategorized")} "
                + $"of {Store.Data.Categories.Count} categories");

            // With a search: rows matching both, counted as the search's matches. "symphony" finds mods on (this one, Test
            // Fixes) and off (Test Retired, off in every run), so some rows show and some of its matches don't.
            await Find("symphony");
            var found = Where(m => SpireModManager.Search.Matches(m, "symphony"));
            var both = Where(m => Off(m) && SpireModManager.Search.Matches(m, "symphony"));
            Check("Show: Disabled with a search: the rows matching both, counted as search matches", both.Count > 0 && both.Count < found.Count
                && Shown().SequenceEqual(both) && Scope() == $"Search matches ({both.Count})" && Choices() == "All Enabled Disabled* Changed",
                $"{Say()}; expected {string.Join(", ", both)} of the search's {string.Join(", ", found)}");
            // The choices count within the search too: All its matches, Enabled and Disabled splitting them, the lit one
            // as many as it shows (the count before Enable all says the same).
            Check("with a search, each choice counts its matches: All the search's, Disabled what it shows",
                ChoiceCounts() is [var allCount, var onCount, var offCount, _] && allCount == found.Count && offCount == both.Count && onCount + offCount == found.Count,
                $"{string.Join(", ", ChoiceCounts())}; search {found.Count}, both {both.Count}");
            await Find("");

            CategoryList.Redraw(content);
            await Wait(menu, 0.3);
            Check("Show: Disabled outlasts a redraw", Shown().SequenceEqual(off) && Choices() == "All Enabled Disabled* Changed", Say());

            await Press("ShowChangedButton");
            await Press("ShowAllButton");
        }
        finally
        {
            History.Changed -= Moved;
        }
        Check("choosing is no undo step, not in Changes, not pending, no unsaved profile edit", moves == 0 && State() == before,
            $"history moved {moves}, same state {State() == before}");

        // A mod unticked under Enabled: its row stays where it was, counted, through a redraw that changes no mod too (a
        // search, a fold), until Enabled is pressed again. (Undone after: the untick is a step.)
        await Press("ShowEnabledButton");
        if (Rows().FirstOrDefault(r => r.Visible && r.Mod is { } m && On(m) && m.manifest?.id is { } id && id != ModEntry.ModId && id != Bmm.ModId)
            is { Mod: { } ticked } row)
        {
            var count = Scope();
            var tickbox = row.GetNode<NTickbox>("Tickbox");
            tickbox.IsTicked = false;
            tickbox.EmitSignal(NTickbox.SignalName.Toggled, tickbox);
            await Wait(menu, 0.3);
            Check("Show: Enabled, a mod unticked: its row stays, still counted", Off(ticked) && row.Visible && Scope() == count,
                $"{ticked.manifest?.id} on {On(ticked)}, shown {row.Visible}; {count} -> {Scope()}");
            await Capture(screen, outDir, "2h-show-enabled-kept");
            CategoryList.Apply(content);
            await Wait(menu, 0.3);
            Check("... and after a redraw that changes no mod, its category counting it", row.Visible && Scope() == count && Headers(m => On(m) || m == ticked) == "",
                $"shown {row.Visible}, {count} -> {Scope()}; {Headers(m => On(m) || m == ticked)}");
            await Press("ShowEnabledButton");
            Check("Enabled pressed again: the unticked mod goes", !row.Visible && Shown().SequenceEqual(Where(On)), Say());
            History.Undo();
            await Wait(menu, 0.3);
            Check("undone: enabled and shown again", On(ticked) && row.Visible && Shown().SequenceEqual(Where(On)), Say());
        }
        else
            Results.Add("SKIP list filters: no enabled mod to untick");

        // The same under a search (Show: All): with "#enabled" typed, a mod unticked keeps its row, counted among the
        // matches, through a redraw that changes no mod and a space more in the box; the search changed, it goes, and
        // coming back to "#enabled" doesn't bring it. Again with a Show: choice pressed: it goes. (Undone after.)
        await Press("ShowAllButton");
        await Find("#enabled");
        if (Rows().FirstOrDefault(r => r.Visible && r.Mod is { } m && On(m) && m.manifest?.id is { } id && id != ModEntry.ModId && id != Bmm.ModId)
            is { Mod: { } byHand } byHandRow)
        {
            var count = Scope();
            async Task Untick()
            {
                var tickbox = byHandRow.GetNode<NTickbox>("Tickbox");
                tickbox.IsTicked = false;
                tickbox.EmitSignal(NTickbox.SignalName.Toggled, tickbox);
                await Wait(menu, 0.3);
            }
            await Untick();
            var keptAtOnce = Off(byHand) && byHandRow.Visible && Scope() == count;
            CategoryList.Apply(content);
            await Wait(menu, 0.3);
            var keptRedrawn = byHandRow.Visible && Scope() == count;
            await Find("#enabled ");
            var keptSpaced = byHandRow.Visible;
            await Find("#enabled -zzz");
            var goneChanged = !byHandRow.Visible;
            await Find("#enabled");
            Check("a search (\"#enabled\"): a mod unticked keeps its row, counted, through a redraw and a space more; the search changed, it goes, and stays gone",
                keptAtOnce && keptRedrawn && keptSpaced && goneChanged && !byHandRow.Visible && Shown().SequenceEqual(Where(On)),
                $"at once {keptAtOnce}, redrawn {keptRedrawn}, a space more {keptSpaced}, changed {goneChanged}; {count} -> {Scope()}; {Say()}");
            History.Undo();
            await Wait(menu, 0.3);
            await Untick();
            var keptAgain = byHandRow.Visible;
            await Press("ShowAllButton");
            Check("a search (\"#enabled\"), a mod unticked (kept), a Show: choice pressed: it goes", keptAgain && !byHandRow.Visible && Shown().SequenceEqual(Where(On)),
                $"kept {keptAgain}; {Say()}");
            History.Undo();
            await Wait(menu, 0.3);
            Check("undone: enabled, and found again", On(byHand) && byHandRow.Visible, Say());
        }
        else
            Results.Add("SKIP list filters, a kept row under a search: no enabled mod to untick");
        await Find("");

        // A row kept for one mod's change goes at once with a change to many mods, which shows the choice's rows whole:
        // under Enabled, a mod unticked, then undo (of a second one unticked after it), a profile switched to and back (to
        // the untick, its unsaved edit), or Disable all. (Each undone, or ticked again, after.)
        await Press("ShowEnabledButton");
        var onRows = Rows().Where(r => r.Visible && r.Mod is { } m && On(m) && m.manifest?.id is { } id && id != ModEntry.ModId && id != Bmm.ModId).ToList();
        if (onRows is [{ Mod: { } kept } keptRow, { Mod: { } later } laterRow, ..])
        {
            async Task Tick(NModMenuRow which, bool on)
            {
                var tickbox = which.GetNode<NTickbox>("Tickbox");
                tickbox.IsTicked = on;
                tickbox.EmitSignal(NTickbox.SignalName.Toggled, tickbox);
                await Wait(menu, 0.3);
            }
            await Tick(keptRow, false);
            await Tick(laterRow, false);
            var stayed = keptRow.Visible && laterRow.Visible;
            History.Undo(); // the second
            await Wait(menu, 0.3);
            Check("Show: Enabled, two mods unticked (both kept), the second undone: the first goes at once", stayed && Off(kept) && On(later)
                && !keptRow.Visible && Shown().SequenceEqual(Where(On)), $"kept {stayed}; {Say()}");
            History.Undo(); // the first
            await Wait(menu, 0.3);

            if (Profiles.Active is { } home && Store.Data.Profiles.FirstOrDefault(p => p != home) is { } other)
            {
                await Tick(keptRow, false);
                stayed = keptRow.Visible;
                Profiles.SwitchTo(other, screen);
                await Wait(menu, 0.3);
                Profiles.SwitchTo(home, screen); // back to the untick, the profile's unsaved edit
                await Wait(menu, 0.3);
                Check($"Show: Enabled, a mod unticked (kept), {other.Name} switched to and back: it goes at once", stayed && Off(kept)
                    && !keptRow.Visible && Shown().SequenceEqual(Where(On)), $"kept {stayed}; {Say()}");
                await Tick(keptRow, true);
            }
            else
                Results.Add("SKIP list filters, a kept row and a profile: one profile only");

            await Press("ShowEnabledButton");
            await Tick(keptRow, false);
            stayed = keptRow.Visible;
            await Press("DisableAllButton");
            Check("Show: Enabled, a mod unticked (kept), Disable all: it goes at once with the rest", stayed && Off(kept)
                && !keptRow.Visible && Shown().SequenceEqual(Where(On)), $"kept {stayed}; {Say()}");
            History.Undo(); // Disable all
            History.Undo(); // the untick
            await Wait(menu, 0.3);
            Check("undone: the mods on again", On(kept) && Shown().SequenceEqual(Where(On)), Say());
        }
        else
            Results.Add("SKIP list filters, kept rows: fewer than two enabled mods to untick");

        // Enable all under Disabled: just the mods shown, as one step; they're enabled, so they go (a change to many at once).
        await Press("ShowDisabledButton");
        var wereOff = Rows().Where(r => r.Visible).Select(r => r.Mod).OfType<Mod>().ToList();
        if (wereOff.Count > 0)
        {
            // A copy of a mod installed twice among them (the fixture twins' copy that doesn't load) comes on beside the
            // other, which stays on: nothing turns another copy off. Which of them loads is then the higher version's (the
            // fixture's: the one loading already).
            var others = Rows().Select(r => r.Mod).OfType<Mod>().Except(wereOff).ToDictionary(m => m, On);
            var twin = wereOff.FirstOrDefault(m => Copies.Twins(m).Count > 0);
            var (loadsWas, offWas) = (twin?.manifest?.id is { } twinId ? LoadingCopy(twinId) : null, CopiesOff());
            await Press("EnableAllButton");
            Check("Show: Disabled, Enable all: just the mods shown, which then go; no other copy turns off (the one loading still loads, the higher version)",
                wereOff.All(On) && others.All(p => On(p.Key) == p.Value) && Shown().Count == 0 && Scope() == "All disabled (0)"
                && (twin?.manifest?.id is not { } turnedId || turnedId != TwinId || LoadingCopy(turnedId) == loadsWas && Copies.Ticked(twin)),
                $"{Say()}; copy {Copies.KeyOf(twin)}, loads {(twin?.manifest?.id is { } shownId ? LoadingCopy(shownId) : null)} (was {loadsWas})");
            History.Undo();
            await Wait(menu, 0.3);
            Check("undone: disabled and shown again (a copy's copies_off back)", wereOff.All(Off)
                && Shown().SequenceEqual(Ids(Rows().Where(r => r.Mod is { } m && wereOff.Contains(m)))) && CopiesOff().SequenceEqual(offWas)
                && (twin?.manifest?.id is not { } backId || LoadingCopy(backId) == loadsWas), Say());
        }
        else
            Results.Add("SKIP list filters, Enable all: no disabled mod");

        // A profile switched to under Enabled: the mods it turns off go at once, as for Enable all (its tickboxes fire no
        // toggle, so the switch redraws the list itself); switched back, they show again.
        await Press("ShowEnabledButton");
        if (Profiles.Active is { } start && Store.Data.Profiles.FirstOrDefault(p => p != start) is { } away)
        {
            var wereOn = Where(On);
            Profiles.SwitchTo(away, screen);
            await Wait(menu, 0.3);
            var nowOn = Where(On);
            Check($"Show: Enabled, switched to {away.Name}: the mods it turns off go at once, the bar and each category counting the rest",
                nowOn.Count < wereOn.Count && Shown().SequenceEqual(nowOn) && Scope() == $"All enabled ({nowOn.Count})" && Headers(On) == "",
                $"{wereOn.Count} on, then {nowOn.Count}; {Say()}; {Headers(On)}");
            Profiles.SwitchTo(start, screen);
            await Wait(menu, 0.3);
            Check("... and switched back: they show again", Where(On).SequenceEqual(wereOn) && Shown().SequenceEqual(wereOn), Say());
        }
        else
            Results.Add("SKIP list filters, profile: one profile only");

        // Changed: the mods the Changes list names (an edit made here to be sure of one; undone after).
        await Press("ShowAllButton");
        Store.EditMod("SmmTestUi", info => info.Notes = "List filter test");
        CategoryList.Edited(content);
        await Press("ShowChangedButton");
        var lines = HistoryBar.Describe();
        // Each line's mod: the one whose name it holds, the longest if several ("Better Mod Menu (local fix)" holds
        // "Better Mod Menu" too), every one of that name (the fixture's mod from both sources: its Workshop copy, read from
        // mods_STEAMTEST, has no item id, so both read "Test Dual (Local)"). Lines naming no mod (a switched profile,
        // profiles) name none.
        var mods = Rows().Select(r => r.Mod).OfType<Mod>().ToList();
        var named = lines.SelectMany(l => mods.Where(m => m.manifest?.name is { Length: > 0 } name && l.Contains(name)).GroupBy(m => m.manifest?.name?.Length ?? 0)
            .MaxBy(g => g.Key) ?? Enumerable.Empty<Mod>()).ToHashSet();
        var changed = Where(named.Contains);
        Check("Show: Changed: the mods Changes names, counted, Changed lit", Shown().SequenceEqual(changed) && changed.Contains("SmmTestUi")
            && Scope() == $"Changed since launch ({changed.Count})" && Choices() == "All Enabled Disabled Changed*", $"{Say()}; Changes: {string.Join(" | ", lines)}");
        Check("Show: Changed: each category counts its changed mods; none: no header", Headers(named.Contains) == "", Headers(named.Contains));
        Results.Add($"INFO Changed shows {changed.Count} of {all.Count} mods");
        await Capture(screen, outDir, "2i-show-changed");
        History.Undo(); // the note
        await Wait(menu, 0.3);

        // Picked mods edited together in their menu keep their rows, as one mod's menu does: under Changed, a tag given to
        // two and taken off again in their bulk menu (as at launch) leaves their rows while it's open; they go when
        // Changed is pressed again.
        string[] pair = ["SmmTestUi", "SmmTestContent"];
        bool InPair(Mod? mod) => pair.Contains(mod?.manifest?.id ?? "");
        if (Rows().Count(r => InPair(r.Mod)) != 2 || HistoryBar.ChangedMods().Any(InPair))
            Results.Add("SKIP list filters, bulk menu: the test mods missing, or changed already");
        else
        {
            Store.Batch(() =>
            {
                foreach (var id in pair)
                    Store.EditMod(id, info => info.Tags = ["filtered"]);
            });
            CategoryList.Edited(content);
            await Wait(menu, 0.3);
            var pairRows = Rows().Where(r => InPair(r.Mod)).ToList();
            Selection.Add(content, pairRows);
            if (pairRows.All(r => r.Visible) && CategoryList.ShowBulkMenu(content) is { } bulk
                && bulk.FindChild("Tags", true, false)?.GetNodeOrNull<LineEdit>("Box") is { } tags)
            {
                tags.Text = "";
                tags.EmitSignal(Control.SignalName.FocusExited);
                await Wait(menu, 0.3);
                Check("Show: Changed, a tag taken off two picked mods in their menu (as at launch): their rows stay while it's open",
                    pair.All(id => Store.InfoFor(id)?.Tags == null) && !HistoryBar.ChangedMods().Any(InPair) && pairRows.All(r => r.Visible)
                    && MenuPanel.IsOpen, Say());
            }
            else
                Check("Show: Changed, two mods given a tag: shown, and their bulk menu opens", false, Say());
            MenuPanel.Close();
            Selection.Clear(content);
            await Press("ShowChangedButton");
            Check("Changed pressed again: they go", pairRows.All(r => !r.Visible), Say());
        }

        // Going to a mod the choice leaves out (a name in the panel, a banner's Go to) shows its row; the choice stays.
        await Press("ShowDisabledButton");
        if (Rows().FirstOrDefault(r => r.Mod?.manifest?.id == ModEntry.ModId) is { } ours)
        {
            var hidden = !ours.Visible;
            CategoryList.Reveal(content, ModEntry.ModId, flash: false);
            await Wait(menu, 0.5);
            Check("Show: Disabled, going to an enabled mod: its row shows too, Disabled stays", hidden && ours.Visible
                && Choices() == "All Enabled Disabled* Changed" && Shown().SequenceEqual(Where(m => Off(m) || m == ours.Mod)), Say());
            await Press("ShowDisabledButton");
            Check("Disabled pressed again: it goes", !ours.Visible && Shown().SequenceEqual(Where(Off)), Say());
        }

        // Load Order and Vanilla List: every row, no page bar; the main list's choice is still there after.
        Click(screen.GetNode<NButton>("SpireModManagerLoadOrderToggle"));
        await Wait(menu, 0.5);
        Check("Load Order: every row, no page bar, whatever the main list shows", Layout.Current == Layout.View.LoadOrder
            && Shown().SequenceEqual(all) && Bar() == null, $"{Layout.Current}, shown {Shown().Count} of {all.Count}, bar {Bar() != null}");
        Click(screen.GetNode<NButton>("SpireModManagerLoadOrderToggle"));
        await Wait(menu, 0.5);
        Check("back on the main list: still Disabled", Layout.Current == Layout.View.Categories && Choices() == "All Enabled Disabled* Changed"
            && Shown().SequenceEqual(Where(Off)), Say());
        if (!Bmm.Present) // its page shows its own groups
        {
            Check("Vanilla List: every row, whatever the main list shows", await WithVanillaView(screen, menu, () => Shown().SequenceEqual(all)));
            // A search typed on the main list doesn't follow to Vanilla List, which has no box for it: every row there,
            // and the search still on the main list after. The game's panel title there: the game's pointer, no link.
            var searchBox = screen.GetNode<LineEdit>("ModsBorder/SpireModManagerSearch");
            void Search(string text)
            {
                searchBox.Text = text;
                searchBox.EmitSignal(LineEdit.SignalName.TextChanged, text);
            }
            Search("SmmTestLib");
            await Wait(menu, 0.5);
            var searched = Shown().Count;
            var title = screen.GetNode<Control>("%ModInfoContainer/ModTitle");
            var vanillaAll = false;
            var vanillaCursor = Control.CursorShape.PointingHand;
            await WithVanillaView(screen, menu, () =>
            {
                vanillaAll = Shown().SequenceEqual(all);
                vanillaCursor = title.MouseDefaultCursorShape;
                return true;
            });
            var searchKept = Shown().Count == searched && searchBox.Text == "SmmTestLib";
            var ourCursor = title.MouseDefaultCursorShape;
            Search("");
            await Wait(menu, 0.5);
            Check("Vanilla List with a search typed on the main list: every row, and the search still there after; its panel title has the "
                + "game's pointer (ours: the hand)", vanillaAll && searched < all.Count && searchKept && vanillaCursor != Control.CursorShape.PointingHand
                && ourCursor == Control.CursorShape.PointingHand, $"searched {searched} of {all.Count}, kept {searchKept}, cursor {vanillaCursor} then {ourCursor}");
        }

        await Press("ShowAllButton");
        Check("All: every row again, counted as installed mods", Shown().SequenceEqual(all) && Scope() == $"Mods installed ({all.Count})"
            && Choices() == "All* Enabled Disabled Changed", Say());

        // Going to a mod a search leaves out (a name in the panel, a line in Changes), as for a banner's Show: the search
        // clears, box and list, and its row shows; a search that finds it stays.
        if (Rows().FirstOrDefault(r => r.Mod?.manifest?.id == "SmmTestLib") is { } panelRow
            && Rows().FirstOrDefault(r => r.Mod?.manifest?.id == "SmmTestUi") is { Mod: { } linked } linkedRow)
        {
            CategoryList.Select(screen, panelRow);
            await PanelFor(menu, screen, panelRow);
            await Wait(menu, 0.3);
            var description = screen.GetNode<RichTextLabel>("%ModInfoContainer/ModDescription");
            string Filter() => content.HasMeta(CategoryList.FilterMeta) ? (string)content.GetMeta(CategoryList.FilterMeta) : "";
            string Said() => $"box \"{box.Text}\", filter \"{Filter()}\", shown {linkedRow.Visible}";
            const string misses = "SmmTestLib", finds = "SmmTest";
            await Find(misses);
            var hidden = !linkedRow.Visible && !SpireModManager.Search.Matches(linked, misses);
            description.EmitSignal(RichTextLabel.SignalName.MetaClicked, "mod:SmmTestUi");
            await Wait(menu, 0.3);
            Check("Show: All, a search that leaves a mod out: its name in the panel clears the search, box and list, and shows its row",
                hidden && box.Text == "" && Filter() == "" && linkedRow.Visible, $"hidden by the search {hidden}; {Said()}");
            await Find(finds);
            description.EmitSignal(RichTextLabel.SignalName.MetaClicked, "mod:SmmTestUi");
            await Wait(menu, 0.3);
            Check("... a search that finds it stays", box.Text == finds && Filter() == finds && linkedRow.Visible, Said());
            await Find(misses);
            hidden = !linkedRow.Visible;
            HistoryBar.OpenFromList(screen, "SmmTestUi");
            await Wait(menu, 0.3);
            Check("... and its line in Changes clears it too, the mod in the panel", hidden && box.Text == "" && Filter() == ""
                && linkedRow.Visible && CategoryList.Selected == linked, $"hidden by the search {hidden}; {Said()}; panel {CategoryList.Selected?.manifest?.id}");
            HistoryBar.HideBack(screen.GetNode<Control>("%ModInfoContainer"));
            await Find("");
        }
        else
            Results.Add("SKIP list filters, going to a mod through a search: the test mods missing");
    }

    // The page opening again: the choice stays, and the rows it kept for a change go by it again.
    static async Task TestListFilterReopen(Node menu, NModdingScreen screen, Control content)
    {
        if (Layout.Current != Layout.View.Categories
            || content.GetChildren().OfType<NModMenuRow>().FirstOrDefault(r => r.Mod?.manifest?.id == ModEntry.ModId) is not { } ours)
        {
            Results.Add("SKIP list filter reopen: not on the main list");
            return;
        }
        string Lit() => string.Join(" ", screen.FindChild(PageBar.Name, true, false)?.FindChild("Show", true, false)?.GetChildren().OfType<Button>()
            .Where(b => b.GetThemeColor("font_color") == Colors.White).Select(b => System.Text.RegularExpressions.Regex.Replace(b.Text, @" \([\d,]+\)$", "")) ?? []);
        ListFilter.Choose(content, ListFilter.Choice.Disabled);
        CategoryList.Reveal(content, ModEntry.ModId, flash: false); // kept beside the disabled ones
        await Wait(menu, 0.5);
        var kept = ours.Visible;
        screen.Visible = false;
        screen.Visible = true;
        await Wait(menu, 0.5);
        Check("the page opening again: Disabled still chosen and lit, a row kept for a change gone", kept && Lit() == "Disabled"
            && ListFilter.Current == ListFilter.Choice.Disabled && !ours.Visible, $"kept {kept}, lit {Lit()}, shown {ours.Visible}");
        ListFilter.Choose(content, ListFilter.Choice.All);
    }

    // The text of the hover card `name` open over a page, or null.
    static RichTextLabel? CardLabel(Node page, string name) =>
        page.GetNodeOrNull(name)?.FindChildren("*", "RichTextLabel", true, false).OfType<RichTextLabel>().FirstOrDefault();

    // Whether a history snapshot's active profile has the mod, on.
    static bool OnInSnapshot(History.Snapshot? snapshot, string id, bool enabled = true)
    {
        if (snapshot == null || JsonNode.Parse(snapshot.Store) is not JsonObject store)
            return false;
        var active = (string?)store["active_profile"];
        return (store["profiles"] as JsonArray ?? []).Any(p => (string?)p?["name"] == active
            && (p?["mods"] as JsonArray ?? []).Any(m => (string?)m?["id"] == id && (bool?)m?["enabled"] == enabled));
    }

    // A row without its line, buttons and pencil, so the next RowStrip.Apply makes them afresh, as on a new page.
    static void Unmake(NModMenuRow row)
    {
        foreach (var name in new[] { "SpireModManagerStrip", RowStrip.ActionsName, RowStrip.OrderActionsName, "SpireModManagerEdit" })
            if (row.GetNodeOrNull(name) is { } part)
            {
                row.RemoveChild(part);
                part.QueueFree();
            }
    }

    // How a part of a row's line differs from how per-node overrides styled it (before #46): a node of its kind beside
    // the line, given the items of the part's look as overrides (its look: the nearest Theme on it or above it within
    // the row: its own, its tag's or its line's), and every theme item of that kind compared. Lists the items that
    // differ, none when the shared Theme looks exactly as the overrides did, page and all.
    static List<string> UnlikeOverrides(NModMenuRow row, Control part)
    {
        Theme? look = null;
        for (Node? at = part; at != null && at != row && look == null; at = at.GetParent())
            look = (at as Control)?.Theme;
        var type = part.GetClass();
        if (look == null || ClassDB.Instantiate(type).AsGodotObject() is not Control plain)
            return [$"{part.Name}: no look"];
        foreach (var name in look.GetColorList(type))
            plain.AddThemeColorOverride(name, look.GetColor(name, type));
        foreach (var name in look.GetConstantList(type))
            plain.AddThemeConstantOverride(name, look.GetConstant(name, type));
        foreach (var name in look.GetFontList(type))
            plain.AddThemeFontOverride(name, look.GetFont(name, type));
        foreach (var name in look.GetFontSizeList(type))
            plain.AddThemeFontSizeOverride(name, look.GetFontSize(name, type));
        foreach (var name in look.GetStyleboxList(type))
            plain.AddThemeStyleboxOverride(name, look.GetStylebox(name, type));
        row.AddChild(plain);
        var all = ThemeDB.GetDefaultTheme();
        var unlike = all.GetColorList(type).Where(n => part.GetThemeColor(n) != plain.GetThemeColor(n))
            .Concat(all.GetConstantList(type).Where(n => part.GetThemeConstant(n) != plain.GetThemeConstant(n)))
            .Concat(all.GetFontList(type).Where(n => part.GetThemeFont(n) != plain.GetThemeFont(n)))
            .Concat(all.GetFontSizeList(type).Where(n => part.GetThemeFontSize(n) != plain.GetThemeFontSize(n)))
            .Concat(all.GetStyleboxList(type).Where(n => part.GetThemeStylebox(n) != plain.GetThemeStylebox(n)))
            .Concat(all.GetIconList(type).Where(n => part.GetThemeIcon(n) != plain.GetThemeIcon(n)))
            .Select(n => $"{part.Name} {n}").ToList();
        row.RemoveChild(plain);
        plain.Free();
        return unlike;
    }
}
