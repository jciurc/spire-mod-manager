using System.Text;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;
using MegaCrit.Sts2.Core.Nodes.Screens.Settings;
using MegaCrit.Sts2.Core.Saves;

namespace SpireModManager;

// The self-test's checks of the main menu and the start: our button, first runs, the launch guard, translations.
static partial class SelfTest
{
    // dev.cs test with SPIRE_MOD_MANAGER_SKIP_PATCHES naming a patch our page needs (not MainMenuPatch: this test
    // starts from it): the Modding page is the game's, with the needs-update line and none of ours, the info panel
    // included. Without the screen's own patch, the line is the main menu button's note instead.
    static async Task TestGamePage(NMainMenu menu, string outDir)
    {
        Results.Add($"INFO left out: {string.Join(", ", ModEntry.FailedPatches)}");
        const string text = "Spire Mod Manager is incompatible with this game version, or may require an update";
        if (ModEntry.FailedPatches.Contains(nameof(ModdingScreenPatch)))
        {
            var note = menu.GetNodeOrNull<Label>("SpireModManagerMenuButton/PendingNote");
            Check("no Modding screen patch: the main menu button's note says the mod needs an update", note is { Visible: true, Text: text }, note?.Text ?? "no note");
            await Capture(menu, outDir, "1-main-menu-needs-update");
            return;
        }
        var screen = menu.SubmenuStack.PushSubmenuType<NModdingScreen>();
        await Wait(menu, 2);
        Check("the game's page, with the needs-update line", screen.GetNodeOrNull<Label>(ModEntry.NeedsUpdateName) is { Visible: true, Text: text },
            screen.GetNodeOrNull<Label>(ModEntry.NeedsUpdateName)?.Text ?? "no line");
        var border = screen.GetNode<Control>("ModsBorder");
        Check("none of ours on it: no buttons, bars or headers; the game's boxes", screen.GetNodeOrNull("SpireModManagerViewToggle") == null
            && screen.GetNodeOrNull("SpireModManagerRelaunchButton") == null && border.GetNodeOrNull("SpireModManagerProfileBar") == null
            && !screen.GetNode("%ModsScrollContainer/Mask/Content").GetChildren().Any(c => c.IsInGroup("SpireModManagerCategoryHeader"))
            && border.OffsetLeft == 255 && border.OffsetRight == 994, $"{border.OffsetLeft}..{border.OffsetRight}");
        if (screen.GetNode("%ModsScrollContainer/Mask/Content").GetChildren().OfType<NModMenuRow>().FirstOrDefault() is { } row)
        {
            CategoryList.Select(screen, row);
            await PanelFor(menu, screen, row);
            await Wait(menu, 0.5);
            Check("a mod shown in the panel: the game's details only", screen.GetNode("%ModInfoContainer").GetNodeOrNull(ModDetails.HeadName) == null);
            // Our row look never ran (it saves the game's look the first time it does).
            Check("the selected row keeps the game's highlight", row.GetNodeOrNull<Control>("SelectionHighlight") is { } look
                && !look.HasMeta("spire_mod_manager_game_look"));
        }
        await Capture(screen, outDir, "2-game-page-needs-update");
    }

    // No data at all: one profile, the mod list as it is; no categories, and the hint under the page bar saying how
    // to make one. Made that way (a mod's menu, New category), the hint goes; deleting the category brings it back.
    static async Task TestNoData(Node menu, NModdingScreen screen, Control content, string outDir)
    {
        var data = Store.Data;
        Check("no data: nothing imported, no categories, nothing kept per mod", data.ImportedFrom == null && data.Categories.Count == 0 && data.Mods.Count == 0,
            $"imported from {data.ImportedFrom}, {data.Categories.Count} categories, {data.Mods.Count} mods");
        Check("no data: one profile, Default, active and matching the mod list", data.Profiles.Count == 1
            && Profiles.Active?.Name == Menus.Text("SPIRE_MOD_MANAGER.DEFAULT_PROFILE") && !Profiles.Edited, string.Join(", ", data.Profiles.Select(p => p.Name)));
        var headers = content.GetChildren().Where(c => c.IsInGroup("SpireModManagerCategoryHeader")).Select(c => c.Name.ToString()).ToList();
        Check("no data: the list is Uncategorized alone", headers.SequenceEqual(["Uncategorized"]), string.Join(", ", headers));

        Label? Hint() => screen.FindChild(PageBar.HintName, true, false) as Label;
        var bar = screen.FindChild(PageBar.Name, true, false) as Control;
        var scroll = screen.GetNode<Control>("ModsBorder/ModsScrollContainer");
        var text = Menus.Text("SPIRE_MOD_MANAGER.NO_CATEGORIES_HINT");
        Check("no data: the hint, under the page bar and above the list", Hint() is { Visible: true } hint && hint.Text == text
            && bar != null && hint.OffsetTop >= bar.OffsetBottom && scroll.OffsetTop >= hint.OffsetBottom,
            $"\"{Hint()?.Text}\" at {Hint()?.OffsetTop}..{Hint()?.OffsetBottom}, bar to {bar?.OffsetBottom}, list from {scroll.OffsetTop}");
        await Capture(screen, outDir, "first-run-1-no-data");

        // The way the hint says: a mod's right-click menu, New category (named in place; Enter keeps the name).
        var listTop = scroll.OffsetTop;
        var row = content.GetChildren().OfType<NModMenuRow>().FirstOrDefault(r => r.Visible && r.Mod?.manifest?.id != null);
        var modId = row?.Mod?.manifest?.id;
        if (row == null || modId == null || CategoryList.ShowRowMenu(content, row) is not { } rowMenu)
        {
            Results.Add("FAIL no data: no mod's menu to make a category from");
            return;
        }
        await Wait(menu, 0.3);
        MenuPanel.Press(rowMenu, Menus.Text("SPIRE_MOD_MANAGER.NEW_CATEGORY"));
        await Wait(menu, 0.5);
        var made = Store.Data.Categories.FirstOrDefault();
        Check("a category made from a mod's menu: the mod in it, the hint gone, the list moved up",
            made != null && Store.InfoFor(modId)?.Category == made.Name && Header(content, made.Name) != null && Hint() == null && scroll.OffsetTop < listTop,
            $"{Store.Data.Categories.Count} categories, hint {Hint() != null}, list from {listTop} to {scroll.OffsetTop}");
        await Capture(screen, outDir, "first-run-2-category-made");
        if (made == null)
            return;
        if (Header(content, made.Name)?.GetNodeOrNull<LineEdit>("RenameBox") is { } box)
            box.EmitSignal(LineEdit.SignalName.TextSubmitted, box.Text);
        await Wait(menu, 0.3);
        // One undo step, as from the bulk menu: an undo takes the category and the mod's move back together; a redo both
        // again.
        History.Undo();
        await Wait(menu, 0.3);
        var undone = Store.Data.Categories.Count == 0 && Store.InfoFor(modId)?.Category == null;
        History.Redo();
        await Wait(menu, 0.3);
        Check("a category made from a mod's menu is one undo step: an undo takes the category and the move back, a redo both again",
            undone && Store.Data.Categories.FirstOrDefault()?.Name is { } redone && Store.InfoFor(modId)?.Category == redone,
            $"undone {undone}; {Store.Data.Categories.Count} categories, the mod's {Store.InfoFor(modId)?.Category}");
        made = Store.Data.Categories.FirstOrDefault() ?? made; // undo restores the data as new objects
        Categories.Delete(made);
        CategoryList.Apply(content);
        await Wait(menu, 0.3);
        Check("deleting the last category brings the hint back", Store.Data.Categories.Count == 0 && Hint() is { Visible: true } && scroll.OffsetTop == listTop,
            $"{Store.Data.Categories.Count} categories, list from {scroll.OffsetTop}");
    }

    // Better Mod Menu's data, as BetterModMenuImport maps it: its groups are our categories (in order, its collapsed
    // one collapsed), a mod's group its category, its aliases and notes ours (empty ones left out), its profiles ours
    // (every installed mod on but the ones it had off), its current one active.
    static async Task TestImported(Node menu, NModdingScreen screen, Control content, string outDir)
    {
        var data = Store.Data;
        Check("imported from Better Mod Menu's account folder", data.ImportedFrom?.Replace('\\', '/').EndsWith("mod_data/BetterModMenu/mod_profiles.json") == true,
            data.ImportedFrom ?? "nothing imported");
        if (data.ImportedFrom != null && !Bmm.Present) // its file is only read; with it on, it saves its own changes there
            Check("its file is left as it was", File.GetLastWriteTime(data.ImportedFrom) < System.Diagnostics.Process.GetCurrentProcess().StartTime);
        var categories = string.Join(", ", data.Categories.Select(c => c.Collapsed ? $"{c.Name} (collapsed)" : c.Name));
        Check("its groups are our categories, in order, the collapsed one collapsed", categories == "Frameworks, Content (collapsed), Menus", categories);
        string Fields(string id) => Store.InfoFor(id) is { } info ? $"{id}: {info.Category ?? "-"} / {info.Alias ?? "-"} / {info.Notes ?? "-"}" : $"{id}: none";
        Check("a mod's group is its category; aliases and notes come along, empty ones left out", data.Mods.Count == 4
            && Store.InfoFor("SmmTestLib") is { Category: "Frameworks", Alias: "Framework", Notes: "Keep on: Test Content and Test UI need it.", Tags: null }
            && Store.InfoFor("SmmTestContent") is { Category: "Content", Alias: "Content pack", Notes: null }
            && Store.InfoFor("SmmTestUi") is { Category: null, Alias: null, Notes: "Turn off for co-op." }
            && Store.InfoFor(Bmm.ModId) is { Category: "Menus", Alias: null, Notes: null },
            string.Join("; ", data.Mods.Keys.Order().Select(Fields)));

        var names = string.Join(", ", data.Profiles.Select(p => p.Name));
        Check("its profiles are ours, its current one active, matching the mod list", names == "Framework only, Everything" && data.ActiveProfile == "Everything"
            && !Profiles.Edited,
            $"{names}; active {data.ActiveProfile}");
        string[] testMods = ["SmmTestLib", "SmmTestContent", "SmmTestUi"];
        bool? On(string profile, string id) => data.Profiles.FirstOrDefault(p => p.Name == profile)?.Mods.FirstOrDefault(m => m.Id == id)?.Enabled;
        Check("in each, every installed mod on but the ones it had off", data.Profiles.All(p => p.Disabled == null)
            && On("Framework only", "SmmTestLib") == true && On("Framework only", "SmmTestContent") == false && On("Framework only", "SmmTestUi") == false
            && On("Framework only", ModEntry.ModId) == true && testMods.All(id => On("Everything", id) == true),
            string.Join("; ", data.Profiles.Select(p => $"{p.Name}: {string.Join(", ", testMods.Select(id => $"{id} {On(p.Name, id)}"))}")));

        // On the page: its groups as headers, its alias on the row, the notepad on a mod with notes; no hint.
        NModMenuRow? Row(string id) => content.GetChildren().OfType<NModMenuRow>().FirstOrDefault(r => r.Mod?.manifest?.id == id);
        var headers = content.GetChildren().Select(c => c.Name.ToString()).Where(n => n.StartsWith("Category ") || n == "Uncategorized").ToList();
        var libTitle = Row("SmmTestLib")?.GetNode<RichTextLabel>("Title").Text ?? "";
        Check("on the page: its groups as headers (the collapsed one closed), its alias on the row, the notepad for notes, no hint",
            headers.SequenceEqual(["Category Frameworks", "Category Content", "Category Menus", "Uncategorized"]) && Row("SmmTestContent") is { Visible: false }
            && libTitle.StartsWith("Framework") && Row("SmmTestUi")?.GetNodeOrNull<Control>("SpireModManagerEditedMark") is { Visible: true }
            && screen.FindChild(PageBar.HintName, true, false) == null,
            $"{string.Join(", ", headers)}; Test Content shown {Row("SmmTestContent")?.Visible}; title {libTitle}");
        var bar = screen.GetNode("ModsBorder/SpireModManagerProfileBar");
        var picker = (Button)bar.FindChild("ProfilePicker", true, false);
        Check("the profile bar shows its current profile", picker.Text == "Everything", picker.Text);
        await Capture(screen, outDir, "first-run-bmm-1-imported");
        await RealClick(menu, picker);
        var list = MenuPanel.Current;
        var labels = list == null ? [] : MenuPanel.Labels(list);
        Check("the profile list has both of its profiles", labels.Contains("Framework only") && labels.Contains("Everything"), string.Join(", ", labels));
        await Capture(screen, outDir, "first-run-bmm-2-profiles");
        await RealClick(menu, picker);
    }

    // A second launch, as far as our data goes: the file the first run wrote is read back. Nothing is imported or
    // made again: an edit since then is still there, and there's no second set of profiles.
    static void TestSecondLoad()
    {
        Store.EditMod("SmmTestUi", info => info.Tags = ["since first run"]);
        var before = Store.EditsJson();
        string Summary() => $"profiles {string.Join(", ", Store.Data.Profiles.Select(p => p.Name))}; {Store.Data.Categories.Count} categories; " +
            $"{Store.Data.Mods.Count} mods; imported from {Store.Data.ImportedFrom ?? "nothing"}";
        var was = Summary();
        Store.Reload();
        Profiles.Prepare(); // as at launch
        Check("a second load reads our file back: nothing imported or added again", Store.EditsJson() == before
            && Store.InfoFor("SmmTestUi")?.Tags?.SequenceEqual(["since first run"]) == true, $"{was} -> {Summary()}");
    }

    // Regressions: clicks outside the list, list height after collapsing,
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
        Check("the list keeps its width (a little in from the sides, for the rows' highlight)", Math.Abs(content.Size.X - (content.GetParent<Control>().Size.X + 2 * RowTitles.LookInset.X)) < 1,
            $"{content.Size.X} of {content.GetParent<Control>().Size.X}");
        Check("collapsing everything shrinks the list", content.Size.Y < open && Math.Abs(content.Size.Y - content.GetCombinedMinimumSize().Y) < 1,
            $"{open} -> {content.Size.Y} (min {content.GetCombinedMinimumSize().Y})");
        // Rows folded at launch were never laid out: no width yet. A redraw then, and a category opened by its header,
        // must still show its mods' names (#151: they were cut to fit no width, and the header only shows the rows).
        foreach (var row in content.GetChildren().OfType<NModMenuRow>())
            row.Size = new Vector2(0, row.Size.Y);
        CategoryList.Apply(content);
        await Wait(menu, 0.3);
        var folded = content.GetChildren().OfType<Control>().First(c => c.IsInGroup("SpireModManagerCategoryHeader") && c.Name.ToString().StartsWith("Category "));
        var foldedName = folded.Name.ToString(); // read now: the click redraws the list, and the header is made again
        folded.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true });
        folded.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false });
        await Wait(menu, 0.3);
        var opened = content.GetChildren().OfType<NModMenuRow>().Where(r => r.Visible).ToList();
        string Shown(NModMenuRow row) => row.GetNode<RichTextLabel>("Title").GetParsedText().Trim();
        Check("a category folded since launch shows its mods' names when opened", opened.Count > 0 && opened.All(r => Shown(r).Length > 0),
            $"{foldedName}: {string.Join(" | ", opened.Select(r => $"{r.Mod?.manifest?.id}=\"{Shown(r)}\""))}");
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

        if (content.GetChildren().OfType<NModMenuRow>().FirstOrDefault(r => r.Visible) is { } shown
            && shown.GetNodeOrNull<Control>(RowStrip.ActionsName)?.GetChildOrNull<Control>(0) is { } button)
        {
            var tick = shown.GetNode<Control>("Tickbox");
            var tickY = tick.GetGlobalTransform() * (tick.Size / 2);
            Check("row buttons line up with the tickbox", Math.Abs(button.GetGlobalRect().GetCenter().Y - tickY.Y) < 2,
                $"button {button.GetGlobalRect()}, tickbox centre {tickY}");
            var pencil = shown.GetNodeOrNull<Button>("SpireModManagerEdit");
            Check("the pencil: flat, past the tickbox, in line with it", pencil?.GetThemeStylebox("normal") is StyleBoxEmpty
                && pencil.GetGlobalRect().Position.X >= tick.GetGlobalRect().End.X - 2 && Math.Abs(pencil.GetGlobalRect().GetCenter().Y - tickY.Y) < 2,
                $"pencil {pencil?.GetGlobalRect()}, tickbox {tick.GetGlobalRect()}");
        }
    }

    // dev.cs test --bmm: Better Mod Menu loaded too. On our views its parts are hidden and our layout holds (its
    // re-layout paused); Vanilla View is its page; going back and forth doesn't drift; profiles leave it alone.
    static async Task TestBetterModMenu(NMainMenu menu, NModdingScreen screen, Control content, string outDir)
    {
        var ids = ModManager.Mods.Select(m => m.manifest?.id).ToList();
        var border = screen.GetNode<Control>("ModsBorder");
        var scroll = border.GetNode<Control>("ModsScrollContainer");
        var info = screen.GetNode<Control>("%ModInfoContainer");
        var mask = scroll.GetNode<Control>("Mask");
        var chrome = screen.GetNodeOrNull<Control>("BetterModMenuChrome");
        Results.Add($"INFO Better Mod Menu at {ids.IndexOf(Bmm.ModId)}, ours at {ids.IndexOf(ModEntry.ModId)} in the load order; " +
            $"its bars {(chrome == null ? "not built (it failed on this game version)" : "built")}");
        static bool Shown(Node? node) => node is CanvasItem { Visible: true };
        List<Node> RowControls() => content.GetChildren().OfType<NModMenuRow>()
            .Select(r => r.FindChild("RowCustomControls", true, false)).OfType<Node>().ToList();
        bool Groups() => content.GetChildren().Any(c => c.Name.ToString().StartsWith("ModGroup"));
        static string Box(Control c) => $"{c.OffsetLeft},{c.OffsetTop},{c.OffsetRight},{c.OffsetBottom}";

        void OursHold(string when, float left)
        {
            Check($"{when}: the managers banner shows (no step-aside notice, no warning in the bottom row), with our search and toggles",
                Shown(border.GetNodeOrNull(LoadOrder.ManagersName)) && screen.GetNodeOrNull("SpireModManagerBmmNotice") == null
                && screen.FindChild("SpireModManagerBmmWarning", true, false) == null
                && screen.GetNodeOrNull("SpireModManagerViewToggle") != null && screen.GetNodeOrNull("ModsBorder/SpireModManagerSearch") != null);
            Check($"{when}: Better Mod Menu's bars, row controls and panel buttons hidden",
                !Shown(chrome) && !RowControls().Any(Shown) && !Shown(info.GetNodeOrNull("BetterModMenuSelectedModActions")),
                $"bars {Shown(chrome)}, row controls {RowControls().Count(Shown)}");
            Check($"{when}: none of its groups in our list, every row there", !Groups()
                && content.GetChildren().OfType<NModMenuRow>().Count() == ModManager.Mods.Count);
            Check($"{when}: the list where ours goes, not pushed down or clipped", border.OffsetLeft == left && border.OffsetTop == 72
                && !border.HasMeta("better_mod_menu_top_bar_room") && !mask.ClipContents, $"list {Box(border)}, clip {mask.ClipContents}");
            Check($"{when}: the rows below our bars, inside the panel", border.GetGlobalRect().Grow(1).Encloses(scroll.GetGlobalRect())
                && scroll.Size.Y > 500, $"rows {scroll.GetGlobalRect()}, panel {border.GetGlobalRect()}");
            Check($"{when}: row titles ours (no colour of its own)", content.GetChildren().OfType<NModMenuRow>()
                .All(r => r.GetNode<RichTextLabel>("Title") is { BbcodeEnabled: true } t && !t.HasThemeColorOverride("default_color")));
        }

        // Opening on our main list, after its deferred work.
        OursHold("main list", Layout.CornerX);
        var toggles = new[] { "SpireModManagerViewToggle", "SpireModManagerLoadOrderToggle" }.Select(name => screen.GetNode<Control>(name)).ToList();
        Check("the link to its page says Go to vanilla modding page", toggles[0] is LinkButton { Text: var linkText } && linkText == Menus.Text("SPIRE_MOD_MANAGER.GO_TO_VANILLA"));
        Check("the page link is a little smaller than the row text, never underlined (our links: colour and the pointer), like our other links",
            toggles[0] is LinkButton { Underline: LinkButton.UnderlineMode.Never, MouseDefaultCursorShape: Control.CursorShape.PointingHand } pageLink
            && pageLink.GetThemeFontSize("font_size") == CategoryList.PageLinkSize
            && screen.FindChildren("SpireModManager*", "LinkButton", true, false).OfType<LinkButton>().All(l => l.Underline == LinkButton.UnderlineMode.Never)
            && CategoryList.PageLinkSize < 20, $"{(toggles[0] as LinkButton)?.Underline}");

        // Where it leads, in its tip: the game's own page (as Better Mod Menu shows it, with that loaded), and back.
        var vanillaTip = Hover.TipOf(toggles[0]);
        var backTip = "";
        var linkAt = toggles[0].GetGlobalRect();
        (Rect2 At, string Overlaps) back = default;
        await WithVanillaView(screen, menu, () =>
        {
            var link = screen.GetNode<Control>("SpireModManagerViewToggle");
            backTip = Hover.TipOf(link);
            // Clear of the game's page: its list, its title and its buttons beside it (the beta's page shows them).
            var parts = new[] { "%ModsScrollContainer", "%InstalledModsTitle", "%MakeModsButton", "%GetModsButton", "%ModInfoContainer" }
                .Select(name => screen.GetNodeOrNull<Control>(name)).OfType<Control>().Where(c => c.IsVisibleInTree());
            back = (link.GetGlobalRect(), string.Join(", ", parts.Where(c => c.GetGlobalRect().Intersects(link.GetGlobalRect())).Select(c => c.Name.ToString())));
            return true;
        });
        Check("the link back from the game's page is where ours was (under the mouse that clicked it), clear of the game's page",
            back.At.Size.X > 0 && back.At.Position.DistanceTo(linkAt.Position) < 1 && back.Overlaps == "", $"{linkAt} / back {back.At}, over: {back.Overlaps}");
        // While a mod is installed twice from one source (the fixture twins), a second line says how that page's tickbox
        // treats its copies (its words are checked in TestSameIdCopies).
        var tipLines = vanillaTip.Split('\n');
        Check("the vanilla page link's card says it's the game's own page; the link back from it has none",
            tipLines[0] == Menus.Text("SPIRE_MOD_MANAGER.VANILLA_VIEW_TIP")
            && (Copies.TwinGroups().Count > 0 ? tipLines is [_, var copies] && copies.StartsWith("In vanilla, mods ") : tipLines.Length == 1)
            && string.IsNullOrEmpty(backTip), $"{vanillaTip} | {backTip}");
        var bannerBox = border.GetNode<Control>(LoadOrder.ManagersName).GetGlobalRect();
        var searchBox = border.GetNode<Control>("SpireModManagerSearch").GetGlobalRect();
        var pendingBox = screen.GetNode<Control>("%PendingChangesLabel").GetGlobalRect();
        var buttonsEnd = toggles[1].GetGlobalRect().End.X;
        Check("the managers banner under the search box, above the list; the restart line right after the view buttons (no warning between)",
            bannerBox.Position.Y >= searchBox.End.Y && bannerBox.End.Y <= scroll.GetGlobalRect().Position.Y
            && pendingBox.Position.X > buttonsEnd && pendingBox.Position.X <= buttonsEnd + 14,
            $"banner {bannerBox}, search {searchBox}, list from {scroll.GetGlobalRect().Position.Y}, buttons to {buttonsEnd}, restart line {pendingBox}");

        // Resizes don't set off its re-layout on our views.
        var before = $"{Box(border)} | {Box(scroll)}";
        foreach (var control in new[] { scroll, border.GetNode<Control>("%InstalledModsTitle"), info })
            control.EmitSignal(Control.SignalName.Resized);
        await Wait(menu, 0.3);
        Check("our views: a resize leaves the list where it is", $"{Box(border)} | {Box(scroll)}" == before, $"{before} -> {Box(border)} | {Box(scroll)}");

        // A mod shown in the panel: ours, with the description where ours goes (its Fill postfix runs first).
        var row = content.GetChildren().OfType<NModMenuRow>().First(r => r.Visible && r.Mod?.manifest?.version is { Length: > 0 });
        CategoryList.Select(screen, row);
        await PanelFor(menu, screen, row);
        await Wait(menu, 0.5);
        var description = info.GetNode<Control>("ModDescription");
        Check("our views: the panel's title and description ours", info.GetNode<RichTextLabel>("ModTitle").Text.Contains("[font_size=")
            && description.AnchorBottom == 0 && description.GetGlobalRect().End.Y <= info.GetGlobalRect().End.Y + 1,
            $"anchor {description.AnchorBottom}, description {description.GetGlobalRect()}, panel {info.GetGlobalRect()}");
        await Capture(screen, outDir, "2-better-mod-menu-ours");

        Click(screen.GetNode<NButton>("SpireModManagerLoadOrderToggle"));
        await Wait(menu, 0.5);
        OursHold("Load Order", 400);
        Click(screen.GetNode<NButton>("SpireModManagerLoadOrderToggle"));
        await Wait(menu, 0.5);

        // Vanilla View: its page.
        var make = border.GetNode<Control>("%MakeModsButton");
        async Task<string> ToVanilla()
        {
            Click(screen.GetNode<BaseButton>("SpireModManagerViewToggle"));
            await Wait(menu, 1);
            Check("its page: none of ours (headers, strips, managers banner), the game's icons hidden, the list clipped",
                !content.GetChildren().Any(c => c.IsInGroup("SpireModManagerCategoryHeader"))
                && !content.GetChildren().OfType<NModMenuRow>().Any(r => Shown(r.GetNodeOrNull("SpireModManagerStrip")))
                && !Shown(border.GetNodeOrNull(LoadOrder.ManagersName)) && mask.ClipContents
                && content.GetChildren().OfType<NModMenuRow>().All(r => !Shown(r.GetNode("PlatformIcon"))));
            Check("its page: the same mod still selected", CategoryList.Selected == row.Mod && RowState.Selected(row));
            var get = border.GetNode<Control>("%GetModsButton");
            Check("its page: Make Mods and Get Mods as the game's own page has them (shown on the beta, hidden on stable)",
                make.Visible == (Compat.InfoClear != null) && get.Visible == make.Visible, $"Make Mods {make.Visible}, Get Mods {get.Visible}");
            if (chrome != null)
            {
                Rect2 Rect(string name) => screen.FindChild(name, true, false) is Control c ? c.GetGlobalRect() : new Rect2();
                var (top, groups, search, list) = (Rect("BetterModMenuTopBar"), Rect("BetterModMenuGroupBar"), Rect("BetterModMenuSearchBar"), scroll.GetGlobalRect());
                var title = border.GetNode<Control>("%InstalledModsTitle").GetGlobalRect();
                // On the stable branch Better Mod Menu 1.1.1 makes no row controls at all, with or without us: its row
                // postfix was built against the beta, where ModManifest is a record with ==, and stable's has none
                // (MissingMethodException as it compiles). So none there is its own, not a row of ours hiding them.
                var stableWithoutRowControls = Compat.InfoClear == null && RowControls().Count == 0;
                Check("its page: its bars, row controls and panel buttons show", Shown(chrome) && (RowControls().Any(Shown) || stableWithoutRowControls)
                    && Shown(info.GetNodeOrNull("BetterModMenuSelectedModActions")),
                    $"bars {Shown(chrome)}, row controls {RowControls().Count(Shown)} of {RowControls().Count}, panel buttons "
                    + (info.GetNodeOrNull("BetterModMenuSelectedModActions") is { } actions ? $"shown {Shown(actions)}" : "none"));
                // (Its own sizes on this game version: the group bar is taller than the room it keeps for it, and the
                // search bar can end past the panel. Reported, not checked: the same without us.)
                Check("its page: its top bar clear of the title, its group bar above the list, its search below, the list in its panel",
                    !top.Intersects(title) && groups.Position.Y < list.Position.Y && search.Position.Y >= list.End.Y - 1
                    && list.Position.Y >= border.GetGlobalRect().Position.Y,
                    $"title {title}, top bar {top}, groups {groups}, list {list}, search {search}, panel {border.GetGlobalRect()}");
                Results.Add($"INFO its groups: {content.GetChildren().Count(c => c.Name.ToString().StartsWith("ModGroupHeader_"))}; " +
                    $"group bar over the list by {Math.Max(0, groups.End.Y - list.Position.Y)}, search past the panel by {Math.Max(0, search.End.Y - border.GetGlobalRect().End.Y)}");
            }
            return $"list {border.GetGlobalRect()}, scroll {scroll.GetGlobalRect()}, Make Mods {make.GetGlobalRect()}";
        }
        async Task ToOurs(string when)
        {
            Click(screen.GetNode<BaseButton>("SpireModManagerViewToggle"));
            await Wait(menu, 1);
            OursHold(when, Layout.CornerX);
        }
        var first = await ToVanilla();
        await Capture(screen, outDir, "3-better-mod-menu-page");
        await ToOurs("back on the main list");
        var second = await ToVanilla();
        Check("its page the same the second time (nothing drifts)", second == first, $"{first} -> {second}");
        await ToOurs("back on the main list again");

        // Profiles leave it on or off as it is (both use the game's one mod list).
        bool? BmmOn() => SaveManager.Instance.SettingsSave.ModSettings?.ModList.FirstOrDefault(m => m.Id == Bmm.ModId)?.IsEnabled;
        if (Profiles.Active is { } start && Store.Data.Profiles.FirstOrDefault(p => p != start) is { } other)
        {
            var was = BmmOn();
            Profiles.SwitchTo(other, screen);
            await Wait(menu, 0.5);
            var then = BmmOn();
            Profiles.SwitchTo(start, screen);
            await Wait(menu, 0.5);
            Check("a profile switch leaves Better Mod Menu as it was", then == was && BmmOn() == was, $"{was} -> {then} -> {BmmOn()}");
        }
    }

    // The main menu button is the game's own text button, a copy of its Settings button: the same class, font, size and
    // outline (no picture), with the game's notification dot on its label. It reads the active profile and the profile
    // bar's count, as wide as its words, whole on screen. A long name is cut with "..", one mod reads "1 mod", and a
    // saved position off the screen still puts it all on screen.
    static void TestMenuButtonLook(NMainMenu menu)
    {
        if (menu.GetNodeOrNull<Control>("SpireModManagerMenuButton") is not { } button)
            return;
        var label = button.GetNodeOrNull<Label>("Label");
        var settings = menu.GetNodeOrNull<Label>("MainMenuTextButtons/SettingsButton/Label");
        Check("main menu button: the game's text button in its Settings button's font and outline, at a smaller size, no picture (but its hover carets)",
            button is NMainMenuTextButton && label != null && settings != null && label.GetThemeFont("font") == settings.GetThemeFont("font")
            && label.GetThemeFontSize("font_size") == MenuButton.TextSize && MenuButton.TextSize < settings.GetThemeFontSize("font_size")
            && label.GetThemeConstant("outline_size") == settings.GetThemeConstant("outline_size")
            && label.GetThemeColor("font_outline_color") == settings.GetThemeColor("font_outline_color")
            && !button.FindChildren("*", "TextureRect", true, false).Any(t => t.GetParent()?.Name != "Dot" && t.Name.ToString() is not (MenuButton.CaretLeftName or MenuButton.CaretRightName)),
            $"{button.GetType().Name}, size {label?.GetThemeFontSize("font_size")} vs {settings?.GetThemeFontSize("font_size")}");
        Check("main menu button reads the active profile and the profile bar's count", MenuButtonText(menu) == MenuButtonExpected(),
            $"{MenuButtonText(menu)} vs {MenuButtonExpected()}");
        Check("main menu button: hovering it says what it opens, names the profile in gold, and gives the key that opens it too",
            Hover.TipOf(button) == MenuCard($"[gold]{Profiles.Active?.Name}[/gold]") && MenuButton.Tip("[b]Odd") == MenuCard("[gold][lb]b]Odd[/gold]"), Hover.TipOf(button));
        var screen = button.GetViewportRect();
        Check("main menu button: as wide as its words, whole on screen", MenuButtonMisfit(menu) == null, MenuButtonMisfit(menu) ?? "");
        // The game's hover carets reach 54 from the text either side (NMainMenu.MainMenuButtonFocused): inside the button.
        Check("main menu button: room for the hover carets inside it", label != null
            && button.GetGlobalRect().Encloses(label.GetGlobalRect().GrowIndividual(54, 0, 54, 0)), $"{label?.GetGlobalRect()} in {button.GetGlobalRect()}");
        Check("main menu button: the game's notification dot on its label, shown only with a relaunch pending",
            button.FindChild("Dot", true, false) is Control dot && dot.GetParent() == label && dot.Visible == ModEntry.HasPendingChanges()
            && dot.FindChildren("*", "TextureRect", true, false).Count == 1);

        if (label == null)
            return;
        var (font, size) = (label.GetThemeFont("font"), label.GetThemeFontSize("font_size"));
        var cut = MenuButton.Text("A profile with a really quite long name", 3, font, size);
        Check("main menu button: a long profile name cut with \"..\", one mod, thousands", cut.StartsWith("A profile") && cut.EndsWith(".. (3 mods)")
            && MenuButton.Text("Default", 1, font, size) == "Default (1 mod)"
            && MenuButton.Text("Default", 1234, font, size) == "Default (1,234 mods)",
            $"{cut} | {MenuButton.Text("Default", 1, font, size)}");
        var saved = Store.Data.MenuButtonAt; // in memory only: nothing is saved
        Store.Data.MenuButtonAt = new ScreenPoint(5000, 5000);
        MenuButton.Place(button);
        var corner = button.GetGlobalRect();
        var note = button.GetNodeOrNull<Control>("PendingNote");
        var noteThere = note?.GetGlobalRect() ?? default;
        Store.Data.MenuButtonAt = saved;
        MenuButton.Place(button);
        var noteHere = note?.GetGlobalRect() ?? default;
        // The "Relaunch to apply" note (shown or not): centred above the button, on screen.
        bool Above(Rect2 noteAt, Rect2 buttonAt) => noteAt.End.Y <= buttonAt.Position.Y + 1 && Math.Abs(noteAt.GetCenter().X - buttonAt.GetCenter().X) < 1;
        Check("main menu button: a saved place past the screen's corner keeps it whole on screen, its note centred above it",
            screen.Encloses(corner) && corner.End.DistanceTo(screen.End) < 1 && note != null && screen.Encloses(noteThere) && Above(noteThere, corner),
            $"{corner} in {screen}, note {noteThere}");
        Check("main menu button: back in its place, its note centred above it, on screen", note != null && screen.Encloses(noteHere)
            && Above(noteHere, button.GetGlobalRect()), $"note {noteHere}, button {button.GetGlobalRect()}");
    }

    // A real hover on the main menu button (a mouse motion through the viewport): its own carets come out either side of
    // its text, in their colour, and fade once the mouse leaves. (RitsuLib's main menu scroller hid the menu's shared
    // pair over it.)
    static async Task TestMenuButtonCarets(NMainMenu menu, string outDir)
    {
        if (menu.GetNodeOrNull<Control>("SpireModManagerMenuButton") is not { } button || button.GetChild(0) is not Control label
            || button.GetNodeOrNull<Control>(MenuButton.CaretLeftName) is not { } left || button.GetNodeOrNull<Control>(MenuButton.CaretRightName) is not { } right)
        {
            Check("main menu button: its own hover carets", false, "missing");
            return;
        }
        // A mouse motion onto it, or away, through the viewport. With no window (headless) there is no mouse: its own
        // mouse_entered and mouse_exited instead, which the game's hover handler takes the same way.
        var headless = DisplayServer.GetName() == "headless";
        var away = new Vector2(1800, 100);
        void MoveTo(Vector2 at) => menu.GetViewport().PushInput(new InputEventMouseMotion { Position = at, GlobalPosition = at });
        void Enter()
        {
            if (headless)
                button.EmitSignal(Control.SignalName.MouseEntered);
            else
                MoveTo(button.GetGlobalRect().GetCenter());
        }
        void Leave()
        {
            if (headless)
                button.EmitSignal(Control.SignalName.MouseExited);
            else
                MoveTo(away);
        }
        Leave();
        await Wait(menu, 0.4);
        Enter();
        await WaitReal(menu, 0.5);
        var text = label.GetGlobalRect();
        Check("main menu button: a hover brings its own carets out either side of its text, in their own colour",
            left.IsVisibleInTree() && right.IsVisibleInTree() && left.Modulate.IsEqualApprox(MenuButton.CaretColor) && right.Modulate.IsEqualApprox(MenuButton.CaretColor)
            && left.GetGlobalRect().End.X <= text.Position.X && right.GlobalPosition.X >= text.End.X
            && Math.Abs(left.GlobalPosition.Y - (button.GlobalPosition.Y + 5)) < 1,
            $"left {left.GetGlobalRect()} a {left.Modulate.A:0.##}, right {right.GetGlobalRect()} a {right.Modulate.A:0.##}, text {text}");
        await Capture(menu, outDir, "1f-menu-button-carets");
        Leave();
        await WaitReal(menu, 0.5);
        Check("main menu button: its carets fade once the mouse leaves", left.Modulate.A < 0.05f && right.Modulate.A < 0.05f,
            $"{left.Modulate.A:0.##}, {right.Modulate.A:0.##}");
        // A pass across it in one frame (on the way to the game's buttons): the carets, put up a frame late, stay down.
        Enter();
        Leave();
        await Wait(menu, 0.4);
        Check("main menu button: a pass across it in one frame leaves its carets down", left.Modulate.A < 0.05f && right.Modulate.A < 0.05f,
            $"{left.Modulate.A:0.##}, {right.Modulate.A:0.##}");
    }

    // The main menu button's card for a profile (as written: the key and right-click in bold), with the key that opens the
    // page, or without one. shown: as the card shows it, without the bold's tags.
    static string MenuCard(string profile, bool shown = false)
    {
        string Bold(string words) => shown ? words : $"[b]{words}[/b]";
        return ModEntry.FailedPatches.Contains(nameof(ShortcutsPatch))
            ? $"Spire Mod Manager - View and manage your mods\n{Bold("Right-click")} for more options\n\nCurrently using profile {profile}"
            : $"Spire Mod Manager - View and manage your mods\n{Bold(OS.GetKeycodeString(ShortcutsPatch.ManagerKey))} to open, or {Bold("right-click")} for more options\n\nCurrently using profile {profile}";
    }

    // The main menu button's card: nothing at the usual delay (the mouse crosses the button on its way to the game's), then
    // after a second, the normal card, its profile name in gold.
    static async Task TestMenuButtonCard(NMainMenu menu)
    {
        if (menu.GetNodeOrNull<Control>("SpireModManagerMenuButton") is not { } button)
            return;
        var early = await HoverCard(menu, button, Hover.TipName);
        var card = ""; // its BBCode, read while it shows (it's freed as it closes)
        var late = await SizedHoverCard(menu, button, Hover.TipName, () =>
        {
            card = menu.GetTree().Root.FindChild(Hover.TipName, true, false)?.FindChildren("*", "RichTextLabel", true, false).OfType<RichTextLabel>().FirstOrDefault()?.Text ?? "";
            return Task.CompletedTask;
        }, seconds: Hover.SlowDelay + 0.2);
        Check("main menu button: its card waits a second, then names the profile in gold, the key on its own line", early == "" && Hover.SlowDelay == 1.0
            && late.Text == MenuCard($"{Profiles.Active?.Name}", shown: true)
            && !card.Contains("[gold]") && card.Contains("[color=#") && card.Contains($"]{Profiles.Active?.Name}[/color]"), $"early \"{early}\", then \"{late.Text}\"");

        // Where the card opens, chosen each time from where the button is then (CardMisplaced: exactly where Hover puts
        // it, over neither the button nor the mouse, on screen): above it in its lower-left corner, with no room below;
        // moved to the top-right corner, below it and left of the mouse; to the bottom-right corner, above it and left of
        // the mouse. With the relaunch note showing (above the button in its corner, below it at the top), off the note
        // too. Moved in memory only: nothing is saved.
        var saved = Store.Data.MenuButtonAt;
        var note = button.GetNodeOrNull<Control>("PendingNote");
        var noteShown = note?.Visible ?? false;
        async Task<string?> CardAt(ScreenPoint? at, bool up, bool left, bool withNote = false)
        {
            Store.Data.MenuButtonAt = at;
            MenuButton.Place(button);
            if (note == null)
                return "no note";
            note.Visible = withNote;
            var area = withNote ? button.GetGlobalRect().Merge(note.GetGlobalRect()) : (Rect2?)null;
            return CardMisplaced(await HoverCardBox(menu, button, Hover.TipName, Hover.SlowDelay + 0.2), button, up, left, area);
        }
        try
        {
            var corner = await CardAt(null, up: true, left: false);
            Check("main menu button: in its corner, its card opens above it, not pushed up over it and the mouse", corner == null, corner ?? "");
            var topRight = await CardAt(new ScreenPoint(5000, -5000), up: false, left: true);
            var bottomRight = await CardAt(new ScreenPoint(5000, 5000), up: true, left: true);
            Check("main menu button: moved, its card opens where there's room (top right: below, left of the mouse; bottom right: above, left of it)",
                topRight == null && bottomRight == null, $"top right: {topRight ?? "fine"}; bottom right: {bottomRight ?? "fine"}");
            var noteAbove = await CardAt(null, up: true, left: false, withNote: true);
            var noteBelow = await CardAt(new ScreenPoint(5000, -5000), up: false, left: true, withNote: true);
            Check("main menu button: its card keeps off the relaunch note too (above it in the corner, below it at the top)",
                noteAbove == null && noteBelow == null, $"corner: {noteAbove ?? "fine"}; top: {noteBelow ?? "fine"}");
        }
        finally
        {
            if (note != null)
                note.Visible = noteShown;
            Store.Data.MenuButtonAt = saved;
            MenuButton.Place(button);
        }
    }

    // Our main menu button opens the page on our view even when Vanilla View was the last one shown; the game's way in
    // (from its Settings) keeps the last view. The page closed for real, then opened each way: the
    // game reuses the page, so this is the page shown again (on its first opening, ours was read as it was made). Then
    // F3, which opens and closes it.
    static async Task TestMenuButtonOpensOurs(NMainMenu menu, NModdingScreen screen)
    {
        if (menu.GetNodeOrNull<Control>("SpireModManagerMenuButton") is not { } button)
            return;
        async Task<string?> Reopened(Action open, bool vanilla)
        {
            menu.SubmenuStack.Pop();
            await Wait(menu, 0.5);
            var closed = !screen.IsVisibleInTree() && button.IsVisibleInTree();
            open();
            await Wait(menu, 1);
            var headers = screen.GetNode<Control>("%ModsScrollContainer/Mask/Content").GetChildren().Any(c => c.IsInGroup("SpireModManagerCategoryHeader"));
            return closed && screen.IsVisibleInTree() && Store.Data.VanillaView == vanilla && headers != vanilla
                ? null : $"closed {closed}, shown {screen.IsVisibleInTree()}, vanilla {Store.Data.VanillaView}, headers {headers}";
        }
        var toggle = screen.GetNode<BaseButton>("SpireModManagerViewToggle");
        Click(toggle);
        await Wait(menu, 1);
        var game = await Reopened(() => menu.SubmenuStack.PushSubmenuType<NModdingScreen>(), vanilla: true);
        Check("the game's way in keeps Vanilla View, the last one shown", game == null, game ?? "");
        var ours = await Reopened(() => button.EmitSignal(NClickableControl.SignalName.Released, button), vanilla: false);
        Check("our main menu button opens our view, after Vanilla View", ours == null, ours ?? "");
        if (Store.Data.VanillaView) // never leave the tests after this on Vanilla View
        {
            Click(toggle);
            await Wait(menu, 1);
        }

        // F3 (ShortcutsPatch.ManagerKey): on the page, in either view, back to the main menu as its Back button goes; on
        // the main menu, our view, as the button opens it. A menu of ours open closes first, on the page and on the main
        // menu. On another of the game's pages, or while the search box has the keyboard, it does nothing.
        async Task Press()
        {
            foreach (var pressed in new[] { true, false })
            {
                Input.ParseInputEvent(new InputEventKey { Keycode = ShortcutsPatch.ManagerKey, Pressed = pressed });
                await Wait(menu, 0.2);
            }
            await Wait(menu, 0.3);
        }
        bool OnMainMenu() => !menu.SubmenuStack.SubmenusOpen && !screen.IsVisibleInTree() && button.IsVisibleInTree() && !MenuPanel.IsOpen;
        bool OnOurView() => menu.SubmenuStack.Peek() == screen && screen.IsVisibleInTree() && !Store.Data.VanillaView && !MenuPanel.IsOpen
            && screen.GetNode<Control>("%ModsScrollContainer/Mask/Content").GetChildren().Any(c => c.IsInGroup("SpireModManagerCategoryHeader"));
        string Where() => $"screen {ActiveScreenContext.Instance.GetCurrentScreen()?.GetType().Name ?? "none"}, page shown {screen.IsVisibleInTree()}, "
            + $"vanilla {Store.Data.VanillaView}, menu open {MenuPanel.IsOpen}";
        screen.GetViewport().GuiReleaseFocus(); // the key reaches the page, not a text box
        Click(toggle);
        await Wait(menu, 1);
        await Press();
        Check("F3 on the page goes back to the main menu, as its Back button does (from Vanilla View too)", OnMainMenu(), Where());
        var settings = menu.SubmenuStack.PushSubmenuType<NSettingsScreen>();
        await Wait(menu, 0.5);
        await Press();
        Check("F3 on another of the game's pages (Settings) does nothing", menu.SubmenuStack.Peek() == settings && settings.IsVisibleInTree()
            && !screen.IsVisibleInTree(), Where());
        menu.SubmenuStack.Pop();
        await Wait(menu, 0.5);
        await Press();
        await Wait(menu, 0.7); // our view's redraw
        Check("F3 on the main menu opens our view of the page, as the button does, after Vanilla View", OnOurView(), Where());

        MenuPanel.Show(screen.GetNode<Control>("%ModsScrollContainer/Mask/Content"), () => [new MenuItem("One", () => { })]);
        await Wait(menu, 0.1);
        var pageMenu = MenuPanel.IsOpen;
        await Press();
        var pageMenuClosed = OnOurView();
        await Press();
        Check("F3 with a menu open on the page closes the menu first, staying; the next goes back to the main menu",
            pageMenu && pageMenuClosed && OnMainMenu(), $"opened {pageMenu}, closed and stayed {pageMenuClosed}; {Where()}");
        var buttonMenu = await ButtonMenu(menu, button) != null;
        await Press();
        var buttonMenuClosed = OnMainMenu();
        await Press();
        await Wait(menu, 0.7);
        Check("F3 with the main menu button's menu open closes the menu first, staying; the next opens the page",
            buttonMenu && buttonMenuClosed && OnOurView(), $"opened {buttonMenu}, closed and stayed {buttonMenuClosed}; {Where()}");

        if (screen.GetNodeOrNull<LineEdit>("ModsBorder/SpireModManagerSearch") is { } search)
        {
            var text = search.Text;
            search.GrabFocus();
            await Press();
            Check("F3 while typing in the search box does nothing to the page: it stays, the box keeps the keyboard and its text",
                OnOurView() && search.HasFocus() && search.Text == text, $"focused {search.HasFocus()}, \"{search.Text}\"; {Where()}");
            screen.GetViewport().GuiReleaseFocus();
        }
        else
            Results.Add("FAIL F3 while typing in the search box: no search box");
    }

    // Right-click the main menu button, Move button, click somewhere: it stays there; Reset puts it back.
    static async Task TestMenuButtonMove(NMainMenu menu, string outDir)
    {
        if (menu.GetNodeOrNull<Control>("SpireModManagerMenuButton") is not { } button)
            return;
        var corner = button.Position;
        var from = button.GetGlobalRect().GetCenter();
        var fake = from;
        Hover.TestMouse = () => fake;
        void Mouse(Vector2 at) => fake = at;
        async Task Click(MouseButton which, Vector2 at)
        {
            MouseButtonAt(menu, which, true, at);
            await Wait(menu, 0.1);
            MouseButtonAt(menu, which, false, at);
            await Wait(menu, 0.2);
        }
        async Task<Node?> OpenMenu()
        {
            Mouse(from);
            button.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton { ButtonIndex = MouseButton.Right, Pressed = false, Position = from, GlobalPosition = from });
            await Wait(menu, 0.3);
            return MenuPanel.Current;
        }

        var popup = await OpenMenu();
        Check("right-click offers Move button", popup != null && MenuPanel.Labels(popup).Contains("Move button"));
        if (popup == null)
            return;
        MenuPanel.Press(popup, "Move button");
        await Wait(menu, 0.2);
        // Relative: the real cursor may be anywhere while the test runs.
        var begin = button.Position;
        var at = fake;
        Check("Move button starts move mode", menu.HasNode(MenuButton.MoveLayerName));
        var to = at + new Vector2(500, -500);
        Mouse(to);
        await Wait(menu, 0.2);
        Check("the button follows the mouse, by as much as it moves", button.Position.DistanceTo(begin + new Vector2(500, -500)) < 2, $"{begin} -> {button.Position}");
        await Capture(menu, outDir, "1b-move-button");
        await Click(MouseButton.Left, to);
        Check("a click puts it down, remembered; move mode ends", Store.Data.MenuButtonAt != null && !menu.HasNode(MenuButton.MoveLayerName)
            && button.Position.DistanceTo(begin + new Vector2(500, -500)) < 2, $"{button.Position}, saved {Store.Data.MenuButtonAt}");
        var placed = button.Position;

        // Moving again and pressing Escape leaves it where it was put.
        from = button.GetGlobalRect().GetCenter();
        if (await OpenMenu() is { } second)
        {
            MenuPanel.Press(second, "Move button");
            await Wait(menu, 0.2);
            Mouse(fake + new Vector2(-200, 100));
            await Wait(menu, 0.2);
            Input.ParseInputEvent(new InputEventKey { Keycode = Key.Escape, Pressed = true });
            await Wait(menu, 0.2);
            Input.ParseInputEvent(new InputEventKey { Keycode = Key.Escape, Pressed = false });
            Check("Escape cancels: back where it was put, move mode ends", button.Position == placed
                && !menu.HasNode(MenuButton.MoveLayerName), $"{button.Position} vs {placed}");
            await Wait(menu, 0.3);
            Check("that Escape stays ours: the game doesn't act on it (no save profile screen)", !menu.SubmenuStack.SubmenusOpen);
        }

        // Escape in the same frame as Move button, before move mode starts following the mouse (a frame later): it ends
        // there, and no per-frame handler of move mode is left running.
        int Following() => menu.GetTree().GetSignalConnectionList(SceneTree.SignalName.ProcessFrame)
            .Count(c => c["callable"].AsCallable().Delegate?.Method.DeclaringType?.DeclaringType == typeof(MenuButton));
        if (await OpenMenu() is { } quick)
        {
            MenuPanel.Press(quick, "Move button");
            menu.GetViewport().PushInput(new InputEventKey { Keycode = Key.Escape, Pressed = true });
            var ended = !menu.HasNode(MenuButton.MoveLayerName);
            Mouse(fake + new Vector2(300, -200));
            await Wait(menu, 0.3);
            Check("Escape in the same frame as Move button: move mode ends, nothing left following the mouse", ended
                && !menu.HasNode(MenuButton.MoveLayerName) && Following() == 0 && button.Position == placed && !menu.SubmenuStack.SubmenusOpen,
                $"{Following()} frame handlers, {button.Position} vs {placed}");
        }

        if (await OpenMenu() is { } third)
            MenuPanel.Press(third, "Reset position");
        await Wait(menu, 0.2);
        Check("Reset position puts it back in the corner", Store.Data.MenuButtonAt == null && button.Position == corner, $"{button.Position} vs {corner}");
        Hover.TestMouse = null;
    }

    // The main menu button's right-click menu, open (null if it didn't open).
    static async Task<Node?> ButtonMenu(NMainMenu menu, Control button)
    {
        var at = button.GetGlobalRect().GetCenter();
        Hover.TestMouse = () => at;
        button.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton { ButtonIndex = MouseButton.Right, Pressed = false, Position = at, GlobalPosition = at });
        await Wait(menu, 0.3);
        Hover.TestMouse = null;
        return MenuPanel.Current;
    }

    // The main menu button's right-click menu: under a heading with our name and version, Move button, Reset position, Open
    // translations folder, a divider, Relaunch game (last and on its own). Relaunch game checks for mods that won't load first, as the Modding page's button does (fixture: Test Addon
    // needs SmmTestMissing, never installed): its dialog shows over the main menu, and its fixes work there before the
    // page has been opened. ModEntry.TestRelaunch counts each relaunch instead of quitting the test.
    static async Task TestMenuButtonRelaunch(NMainMenu menu, string outDir)
    {
        if (menu.GetNodeOrNull<Control>("SpireModManagerMenuButton") is not { } button)
            return;

        // Where its menu opens, from the mouse on the button (MenuMisplaced), chosen from where the button is: in its
        // lower-left corner, above the mouse (right of it), not pushed up over it; moved to the top-right corner, below it
        // and left of it; to the bottom-right corner, above it and left of it. Moved in memory only: nothing is saved.
        var saved = Store.Data.MenuButtonAt;
        async Task<string?> MenuAt(ScreenPoint? at, bool up, bool left)
        {
            Store.Data.MenuButtonAt = at;
            MenuButton.Place(button);
            var mouse = new Rect2(button.GetGlobalRect().GetCenter(), Vector2.Zero);
            var misplaced = MenuMisplaced(await ButtonMenu(menu, button), mouse, up, left);
            MenuPanel.Close();
            return misplaced;
        }
        try
        {
            var corner = await MenuAt(null, up: true, left: false);
            Check("main menu button's menu: in its corner, it opens above the mouse, not pushed up over it", corner == null, corner ?? "");
            var topRight = await MenuAt(new ScreenPoint(5000, -5000), up: false, left: true);
            var bottomRight = await MenuAt(new ScreenPoint(5000, 5000), up: true, left: true);
            Check("main menu button's menu: moved, it opens where there's room (top right: below the mouse, left of it; bottom right: above it, left of it)",
                topRight == null && bottomRight == null, $"top right: {topRight ?? "fine"}; bottom right: {bottomRight ?? "fine"}");
        }
        finally
        {
            Store.Data.MenuButtonAt = saved;
            MenuButton.Place(button);
        }

        var popup = await ButtonMenu(menu, button);
        var labels = popup == null ? [] : MenuPanel.Labels(popup);
        var manage = $"Manage mods{(ModEntry.FailedPatches.Contains(nameof(ShortcutsPatch)) ? "" : $" ({OS.GetKeycodeString(ShortcutsPatch.ManagerKey)})")}";
        Check("main menu button's menu: Manage mods (with its key), Move button, Reset position, Open translations folder, Relaunch game",
            labels.SequenceEqual([manage, "Move button", "Reset position", "Open translations folder", "Relaunch game"]), string.Join(", ", labels));
        if (popup == null)
            return;
        string? Label(Node item) => item.IsInGroup(MenuPanel.ItemGroup) ? (string)item.GetMeta("label") : null;
        var items = popup.FindChild("Items", true, false)?.GetChildren().OfType<Control>().ToList() ?? [];

        // At the top, our name and version (from the manifest the game loaded), a heading with a line under it: not an
        // item, so nothing in it takes the mouse or the focus, and a click on it leaves the menu open.
        var version = ModEntry.Version;
        var header = $"Spire Mod Manager {version}";
        var top = items.FirstOrDefault();
        var parts = top?.FindChildren("*", "Control", true, false).OfType<Control>().Prepend(top).ToList() ?? [];
        Check("main menu button's menu: \"Spire Mod Manager <version>\" first (no \"v\"), a heading, not an item", version != null && top != null
            && top.IsInGroup(MenuPanel.HeadingGroup) && (string)top.GetMeta("label") == header && MenuPanel.Headings(popup).FirstOrDefault() == header
            && !labels.Contains(header) && top.FindChildren("*", "Label", true, false).OfType<Label>().Any(l => l.Text == header)
            && top.FindChildren("*", "ColorRect", true, false).Count == 1 && items.Count > 1 && Label(items[1]) == manage
            && parts.All(c => c.MouseFilter == Control.MouseFilterEnum.Ignore && c.FocusMode == Control.FocusModeEnum.None),
            $"{string.Join(" | ", MenuPanel.Headings(popup))}; version {version}");
        // A real click on it, through the viewport (and the menu's Catcher), landing where it was sent: the menu stays open,
        // nothing runs.
        var placed = Store.Data.MenuButtonAt;
        var (landed, mouse) = (false, Vector2.Zero);
        if (top != null)
        {
            var at = top.GetGlobalRect().GetCenter();
            MouseButtonAt(menu, MouseButton.Left, true, at);
            (landed, mouse) = (Landed(menu, at), menu.GetViewport().GetMousePosition());
            await Wait(menu, 0.1);
            MouseButtonAt(menu, MouseButton.Left, false, at);
            await Wait(menu, 0.2);
        }
        Check("a click on the header, landing on it, does nothing: the menu stays open", top != null && landed && MenuPanel.Current == popup
            && !menu.SubmenuStack.SubmenusOpen && !menu.HasNode(MenuButton.MoveLayerName) && Store.Data.MenuButtonAt == placed
            && menu.GetTree().Root.GetNodeOrNull(RelaunchCheck.Name) == null, $"sent to {top?.GetGlobalRect().GetCenter()}, the mouse at {mouse}");

        // The divider: the one entry between Open translations folder and Relaunch game, a line under the first and above
        // the second.
        var above = items.FindIndex(i => Label(i) == "Open translations folder");
        var divider = above >= 0 && above + 2 < items.Count && Label(items[above + 2]) == "Relaunch game" ? items[above + 1] : null;
        Check("a divider right under Open translations folder, above Relaunch game", divider != null && Label(divider) == null
            && divider.FindChildren("*", "ColorRect", true, false).Count == 1
            && divider.GetGlobalRect().Position.Y >= items[above].GetGlobalRect().End.Y
            && divider.GetGlobalRect().End.Y <= items[above + 2].GetGlobalRect().Position.Y,
            string.Join(" | ", items.Select(i => $"{Label(i) ?? "(line)"} {i.GetGlobalRect()}")));
        await Capture(menu, outDir, "1c-menu-button-menu");

        var relaunches = 0;
        ModEntry.TestRelaunch = plain => relaunches += plain ? 0 : 1; // through the game's quit (a plain one doesn't count)
        try
        {
            Node? Dialog() => menu.GetTree().Root.GetNodeOrNull(RelaunchCheck.Name);
            string Body() => (Dialog()?.FindChild("Body", true, false) as RichTextLabel)?.GetParsedText() ?? "";
            void Press(string name) => Dialog()?.FindChild(name, true, false)?.EmitSignal(BaseButton.SignalName.Pressed);
            MenuPanel.Press(popup, "Relaunch game");
            await Wait(menu, 0.3);
            if (Dialog() == null)
            {
                Check("Relaunch game, nothing wrong: relaunches at once", relaunches == 1 && !MenuPanel.IsOpen, $"{relaunches} relaunches");
                return;
            }
            Check("Relaunch game checks first: its dialog over the main menu, no relaunch yet", relaunches == 0 && !MenuPanel.IsOpen
                && menu.IsVisibleInTree() && !menu.SubmenuStack.SubmenusOpen && Body().Length > 0, $"{relaunches} relaunches: {Body()}");
            await Capture(menu, outDir, "1d-relaunch-check-main-menu");
            if (WontLoad() is not { } addon || Dialog()?.FindChild("DisableBrokenButton", true, false) == null)
            {
                Press("RelaunchNowButton");
                await Wait(menu, 0.2);
                Check("its Relaunch anyway reaches the relaunch", relaunches == 1, $"{relaunches} relaunches");
                return;
            }

            // A fix with no Modding page yet: the active profile and the pending dot follow, and it's one undo step of its
            // own (undoing it leaves the button's Reset position in place).
            var before = ModListState();
            var steps = (History.Steps(History.Track.List), History.Steps(History.Track.Order));
            var dot = (Control)button.FindChild("Dot", true, false);
            var text = MenuButtonText(menu);
            Press("DisableBrokenButton");
            await Wait(menu, 0.3);
            Check("from the main menu, Disable mods that can't load: off, the profile and the dot follow, then a plain Relaunch",
                !Copies.Ticked(addon) && Profiles.Edited && dot.Visible == ModEntry.HasPendingChanges()
                && Dialog()?.FindChild("RelaunchNowButton", true, false) is Button { Text: "Relaunch Game" }, Body());
            Check("the main menu button's count follows a change made on the main menu, as wide as its words", MenuButtonText(menu) == MenuButtonExpected()
                && MenuButtonText(menu) != text && MenuButtonMisfit(menu) == null,
                $"{text} -> {MenuButtonText(menu)} vs {MenuButtonExpected()}; {MenuButtonMisfit(menu)}");
            Press("RelaunchNowButton");
            await Wait(menu, 0.2);
            Check("its Relaunch reaches the relaunch", relaunches == 1, $"{relaunches} relaunches");
            // Cancel (the relaunch here only counted, so the dialog is still up) takes the fix back with no Modding page
            // to redraw, and forgets it: both histories as they were when the dialog opened, nothing to redo.
            bool AsBefore() => ModListState().SequenceEqual(before) && !Profiles.Edited && Store.Data.MenuButtonAt == null
                && dot.Visible == ModEntry.HasPendingChanges();
            string NotAsBefore() => $"{string.Join(", ", before.Except(ModListState()))}; edited {Profiles.Edited}, button at {Store.Data.MenuButtonAt}";
            Press("CancelRelaunchButton");
            await Wait(menu, 0.3);
            Check("from the main menu, Cancel takes back that fix, and only it", Dialog() == null && AsBefore(), NotAsBefore());
            Check("and forgets it: the undo histories as the dialog found them, nothing to redo",
                (History.Steps(History.Track.List), History.Steps(History.Track.Order)) == steps && !History.CanRedo,
                $"{steps} -> ({History.Steps(History.Track.List)}, {History.Steps(History.Track.Order)}), redo {History.CanRedo}");
        }
        finally
        {
            ModEntry.TestRelaunch = null;
            RelaunchCheck.Close();
        }
    }

    // Crash-safe launch (LaunchGuard). This launch: the record our initializer wrote is in Store's folder and gone at the
    // main menu, which saved this launch's mods as the last working setup; the menu hook is a prefix, first. A real crash
    // can't happen here, so the rest runs on a made-up folder with made-up mod lists: a launch that never reached the
    // menu leaves its record, the next one reads it and, at its menu, what changed since the last working setup. No
    // notice when the next launch loaded the same mods, nor for updates alone; a failed save of the working setup still
    // clears the record; an unreadable record, or one with a mod that has no id or an unknown source, counts as none.
    // Then the notice (the game's popup, our mark) with those changes, waiting while another popup is up, and shown
    // once. Restore: the working list back, a mod added since off, ours on, and a relaunch (counted, not done); with a
    // mod left on that needs one added since, the relaunch check's dialog instead. The list is put back.
    static async Task TestLaunchGuard(NMainMenu menu, string outDir)
    {
        var ownDir = LaunchGuard.Dir ?? "(none)";
        Check("launch record: in the folder Store uses", ownDir == Store.Dir, $"{ownDir} vs {Store.Dir}");
        static string Listed(IEnumerable<LaunchGuard.Entry>? mods) => mods == null ? "(none)" : string.Join(", ", mods.Select(m => $"{m.Id}/{m.Source}={m.Enabled}"));
        var savedNow = Listed(History.CurrentMods().Where(m => m.Id != null).Select(m => new LaunchGuard.Entry(m.Id, m.Source, m.Enabled, null)));
        // The record has each copy of a mod installed twice from one source (the fixture twins), on for the one that
        // loaded; the saved list one switch for them: on if one copy was.
        var workingThen = LaunchGuard.Read(Path.Combine(ownDir, LaunchGuard.WorkingFile))?.Mods;
        // The record keeps the order the mods loaded in; the saved list has ours first since (Settle), where Better Mod
        // Menu loaded before us (--bmm).
        var savedThen = Listed(workingThen?.OrderBy(m => m.Id == ModEntry.ModId ? 0 : 1)
            .Select(m => m with { Enabled = workingThen.Any(o => o.Id == m.Id && o.Source == m.Source && o.Enabled) }));
        Check("launch record: gone at the main menu, and this launch's mods saved as the last working setup",
            !File.Exists(Path.Combine(ownDir, LaunchGuard.PendingFile)) && savedThen == savedNow, $"saved {savedThen}; now {savedNow}");
        var hook = HarmonyLib.Harmony.GetPatchInfo(HarmonyLib.AccessTools.Method(typeof(NMainMenu), nameof(NMainMenu._Ready)))?.Prefixes
            .FirstOrDefault(p => p.owner == ModEntry.ModId);
        Check("launch record: cleared by a prefix on the main menu, first in line", hook?.priority == HarmonyLib.Priority.First, $"{hook?.priority}");

        static LaunchGuard.Entry Mod(string id, bool on = true, string? version = "1.0") => new(id, ModSource.ModsDirectory, on, version);
        List<LaunchGuard.Entry> working = [Mod(ModEntry.ModId), Mod("SmmTestLib"), Mod("SmmTestContent"), Mod("SmmTestUi"), Mod("SmmTestFixes", on: false)];
        // Ours updated and moved (never a change); the framework updated (a version with a bracket, which the notice
        // shows as text), and after Test Content now; Test UI off, Test Fixes on; a new mod on, and a new one off (no change).
        List<LaunchGuard.Entry> tried = [Mod("SmmTestContent"), Mod("SmmTestLib", version: "1.1 [b]"), Mod(ModEntry.ModId, version: "2.0"),
            Mod("SmmTestUi", on: false), Mod("SmmTestFixes"), Mod("SmmTestNew"), Mod("SmmTestNewOff", on: false)];
        static string Kinds(List<LaunchGuard.Change> changes) => string.Join(", ", changes.Select(c => $"{c.Kind} {c.Id} {c.From} {c.To}".Trim()));
        Check("launch changes: updated, disabled, enabled, new, and the load order; never ours, nor a new mod that's off",
            Kinds(LaunchGuard.Changes(tried, working)) == "Updated SmmTestLib 1.0 1.1 [b], Disabled SmmTestUi, Enabled SmmTestFixes, New SmmTestNew, Order",
            Kinds(LaunchGuard.Changes(tried, working)));
        List<LaunchGuard.Entry> offMoved = [working[0], working[4], working[1], working[2], working[3]];
        Check("launch changes: none for the same setup, nor for a disabled mod that moved",
            LaunchGuard.Changes(working, working).Count == 0 && LaunchGuard.Changes(offMoved, working).Count == 0,
            Kinds(LaunchGuard.Changes(offMoved, working)));

        var dir = Path.Combine(outDir, "launch-guard");
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
        var (pending, workingFile) = (Path.Combine(dir, LaunchGuard.PendingFile), Path.Combine(dir, LaunchGuard.WorkingFile));
        var now = new DateTime(2026, 10, 14, 9, 30, 0);
        var first = LaunchGuard.Begin(dir, new(now, working));
        Check("a first launch finds no record and writes its own", first.Failed == null && first.Working == null && File.Exists(pending));
        var none = LaunchGuard.Reached(dir, new(now.AddMinutes(1), working), first.Failed, first.Working);
        Check("its menu deletes the record and saves the last working setup", none.Count == 0 && !File.Exists(pending)
            && LaunchGuard.Read(workingFile)?.At == now.AddMinutes(1));
        LaunchGuard.Begin(dir, new(now.AddMinutes(5), tried)); // never reaches the menu
        List<LaunchGuard.Entry> unsubscribed = [.. tried.Where(m => m.Id != "SmmTestNew")]; // gone before the next launch
        var next = LaunchGuard.Begin(dir, new(now.AddMinutes(10), unsubscribed));
        Check("the next launch reads the record of the one that stopped, and the last working setup",
            next.Failed?.At == now.AddMinutes(5) && Listed(next.Failed?.Mods) == Listed(tried) && next.Working?.At == now.AddMinutes(1),
            $"failed {next.Failed?.At}, working {next.Working?.At}");
        var found = LaunchGuard.Reached(dir, new(now.AddMinutes(11), unsubscribed), next.Failed, next.Working);
        Check("at its menu, with other mods than the one that stopped: what that one changed, and this setup is the last working one now",
            Kinds(found) == Kinds(LaunchGuard.Changes(tried, working)) && LaunchGuard.Read(workingFile)?.At == now.AddMinutes(11), Kinds(found));
        LaunchGuard.Begin(dir, new(now.AddMinutes(12), tried)); // never reaches the menu (closed while loading)
        var again = LaunchGuard.Begin(dir, new(now.AddMinutes(13), tried));
        var same = LaunchGuard.Reached(dir, new(now.AddMinutes(14), tried), again.Failed, again.Working);
        Check("the same mods as the launch that stopped reach the menu: no notice", again.Failed != null && same.Count == 0 && !File.Exists(pending),
            Kinds(same));
        List<LaunchGuard.Entry> updated = [.. working.Select(m => m.Id == "SmmTestLib" ? m with { Version = "1.1" } : m)];
        var updates = LaunchGuard.Reached(dir, new(now.AddMinutes(15), [.. updated.Select(m => m.Id == "SmmTestLib" ? m with { Version = "1.2" } : m)]),
            new(now.AddMinutes(14), updated), new(now, working));
        Check("updates alone (Restore can't undo one): no notice", updates.Count == 0, Kinds(updates));
        LaunchGuard.Begin(dir, new(now.AddMinutes(16), tried));
        Directory.CreateDirectory(workingFile + ".tmp"); // the working setup can't be written
        try
        {
            var unsaved = LaunchGuard.Reached(dir, new(now.AddMinutes(17), working), null, null);
            Check("a working setup that can't be saved: the record still goes, the old setup stays", unsaved.Count == 0 && !File.Exists(pending)
                && LaunchGuard.Read(workingFile)?.At == now.AddMinutes(15));
        }
        catch (Exception e)
        {
            Check("a working setup that can't be saved: the record still goes, the old setup stays", false, e.Message);
        }
        Directory.Delete(workingFile + ".tmp");
        // A record that can't be deleted at the menu (#65): the working setup is still saved, and the next launch
        // takes the record, now older than that setup, as none.
        LaunchGuard.Begin(dir, new(now.AddMinutes(18), tried));
        List<LaunchGuard.Change>? locked = null;
        string? lockedError = null;
        using (new FileStream(pending, FileMode.Open, System.IO.FileAccess.Read, FileShare.None))
            try
            {
                locked = LaunchGuard.Reached(dir, new(now.AddMinutes(19), tried), null, null);
            }
            catch (Exception e)
            {
                lockedError = e.Message;
            }
        var afterLock = LaunchGuard.Begin(dir, new(now.AddMinutes(19.5), unsubscribed));
        Check("a record that can't be deleted at the menu: the working setup still saved, the next launch takes the record as none",
            lockedError == null && locked?.Count == 0 && LaunchGuard.Read(workingFile)?.At == now.AddMinutes(19) && afterLock.Failed == null
            && afterLock.Working?.At == now.AddMinutes(19), lockedError ?? $"failed {afterLock.Failed?.At}, working {afterLock.Working?.At}");
        File.WriteAllText(pending, "{\"at\":\"2026-10-14T09:"); // a write cut off
        var torn = LaunchGuard.Begin(dir, new(now.AddMinutes(20), tried));
        Check("an unreadable record counts as none", torn.Failed == null && torn.Working != null);
        var odd = Path.Combine(dir, "odd.json");
        File.WriteAllText(odd, "{\"at\":\"2026-10-14T09:30:00\",\"mods\":[{\"id\":null,\"source\":\"ModsDirectory\",\"enabled\":true}]}");
        var noId = LaunchGuard.Read(odd);
        File.WriteAllText(odd, "{\"at\":\"2026-10-14T09:30:00\",\"mods\":[{\"id\":\"SmmTestLib\",\"source\":99,\"enabled\":true}]}");
        Check("a record with a mod that has no id, or an unknown source, counts as none", noId == null && LaunchGuard.Read(odd) == null);
        File.WriteAllText(odd, "{\"mods\":[{\"id\":\"SmmTestLib\",\"source\":\"ModsDirectory\",\"enabled\":true}]}");
        var noTime = LaunchGuard.Read(odd);
        File.WriteAllText(odd, "{\"at\":\"2026-10-14T09:30:00\",\"mods\":[{\"id\":\"SmmTestLib\",\"source\":\"ModsDirectory\",\"enabled\":true}]}");
        Check("a record with no time counts as none (Copilot, #65); the same with its time reads", noTime == null && LaunchGuard.Read(odd) is { Mods.Count: 1 });

        // The notice; while another popup is up, it waits (the first notice stands in for one), then shows; shown once.
        LaunchGuard.Notice = (new LaunchGuard.Setup(now, working), found);
        LaunchGuard.ShowNotice(menu);
        await Wait(menu, 1);
        var texts = string.Join(" | ", PopupTexts());
        var parsed = string.Join(" | ", NModalContainer.Instance?.OpenModal is Node shown
            ? shown.FindChildren("*", "RichTextLabel", true, false).OfType<RichTextLabel>().Select(r => r.GetParsedText()) : []);
        Check("the notice: its title, the changes by name, a version's bracket as text, Restore and Keep", LaunchGuard.Notice == null
            && texts.Contains("The last launch stopped before the main menu") && texts.Contains("Test Framework") && parsed.Contains("(updated: 1.0 → 1.1 [b])")
            && texts.Contains("SmmTestNew") && texts.Split(" | ").Contains("Restore") && texts.Contains("Keep"), $"{texts} || {parsed}");
        CheckMark("launch notice", NModalContainer.Instance?.OpenModal as Control);
        await Capture(menu, outDir, "1e-launch-notice");
        var firstPopup = NModalContainer.Instance?.OpenModal;
        LaunchGuard.Notice = (new LaunchGuard.Setup(now, working), found);
        LaunchGuard.ShowNotice(menu);
        await Wait(menu, 0.3);
        var waited = LaunchGuard.Notice != null && NModalContainer.Instance?.OpenModal == firstPopup;
        NModalContainer.Instance?.Clear();
        await WaitReal(menu, 1.5); // ShowNotice tries again a second after it found a popup up (LaunchGuard.ShowNotice's CreateTimer(1))
        Check("the notice waits while another popup is up, then shows", waited && LaunchGuard.Notice == null
            && NModalContainer.Instance?.OpenModal is { } later && later != firstPopup, $"waited {waited}");
        NModalContainer.Instance?.Clear();
        await Wait(menu, 0.5);
        LaunchGuard.ShowNotice(menu);
        await Wait(menu, 0.3);
        Check("the notice is shown once", NModalContainer.Instance?.OpenModal == null, NModalContainer.Instance?.OpenModal?.GetType().Name ?? "");

        // Restore, to the list as it is but reversed, Test Fixes, Test Addon (it can't load) and ours off, and Test UI
        // missing (installed since). Then again with Test Framework missing too, which Test Content (on) needs.
        var settings = SaveManager.Instance.SettingsSave.ModSettings;
        var listWas = settings?.ModList.Select(e => new SettingsSaveMod { Id = e.Id, Source = e.Source, IsEnabled = e.IsEnabled }).ToList() ?? [];
        var relaunches = 0;
        ModEntry.TestRelaunch = plain => relaunches += plain ? 0 : 1; // through the game's quit (a plain one doesn't count)
        try
        {
            var target = History.CurrentMods().Where(m => m.Id != "SmmTestUi").Reverse()
                .Select(m => new LaunchGuard.Entry(m.Id, m.Source, m.Enabled && m.Id is not ("SmmTestFixes" or "SmmTestAddon" or ModEntry.ModId), null)).ToList();
            LaunchGuard.Restore(target, menu);
            await Wait(menu, 0.3);
            var after = string.Join(", ", History.CurrentMods().Select(m => $"{m.Id}={m.Enabled}"));
            // Ours first whatever the order (LoadOrder.OursFirst: it has to load before any copy it picks for).
            var expected = string.Join(", ", target.OrderBy(m => m.Id == ModEntry.ModId ? 0 : 1).Select(m => $"{m.Id}={m.Enabled || m.Id == ModEntry.ModId}")
                .Concat(Enumerable.Repeat("SmmTestUi=False", Dependencies.Find("SmmTestUi") != null ? 1 : 0)));
            Check("Restore: the working list back in its order (ours first), a mod added since off, ours on, then a relaunch",
                relaunches == 1 && !RelaunchCheck.IsOpen && after == expected, $"{relaunches} relaunches; {after}; expected {expected}");
            RelaunchCheck.Close();

            if (Dependencies.Find("SmmTestLib") == null || !target.Any(m => m.Id == "SmmTestContent" && m.Enabled))
                Results.Add("SKIP Restore with a mod that needs one added since: no Test Framework, or Test Content off");
            else
            {
                relaunches = 0;
                LaunchGuard.Restore([.. target.Where(m => m.Id != "SmmTestLib")], menu);
                await Wait(menu, 0.3);
                var body = (menu.GetTree().Root.GetNodeOrNull(RelaunchCheck.Name)?.FindChild("Body", true, false) as RichTextLabel)?.GetParsedText() ?? "";
                Check("Restore with a mod left on that needs one added since: the relaunch check's dialog, no relaunch yet",
                    relaunches == 0 && RelaunchCheck.IsOpen && body.Contains("Test Framework"), $"{relaunches} relaunches: {body}");
            }
        }
        finally
        {
            ModEntry.TestRelaunch = null;
            RelaunchCheck.Close();
            if (settings != null)
            {
                settings.ModList.Clear();
                settings.ModList.AddRange(listWas);
                SaveManager.Instance.SaveSettings();
            }
        }
    }

    // The game's startup error (StartupErrorPatch): LaunchGuard.CrashOffer offers the last working setup only when this
    // launch changed mods since it, beyond updates, and says when the game changed too. The dialog in place of the
    // game's: its title, the changes, Quit (nothing written) and Restore and restart (the working list, then a plain
    // relaunch). Quit and the relaunch are caught here, and the saved list is put back.
    static async Task TestCrashOffer(NMainMenu menu, string outDir)
    {
        // Before the first main menu the list in memory is the game's rebuilt one: a mod kept with its local copy on and
        // its Workshop copy off reads both on (the id's first entry's switch), and Disable suspects, or the window's close,
        // would save that. Repaired as a startup error shows or the game quits first: each source's own switch back, the
        // rest as it was; nothing after the first main menu (Settle did it), nor without the list as the game read it, but
        // for the entry of a mod we keep off that the list dropped (a Workshop one not listed without Steam), put back off.
        // The last two on a list the repair would change (ours not first, an id's entry that's off before one that's on), so
        // each check fails if its guard goes. Our record a file of the test's: empty, then with that one standing entry.
        static SettingsSaveMod Saved(string id, ModSource source, bool on) => new() { Id = id, Source = source, IsEnabled = on };
        const string kept = "SmmCrashKept";
        ModSettings Rebuilt() => new() { ModList = [Saved(ModEntry.ModId, ModSource.ModsDirectory, true), Saved(kept, ModSource.ModsDirectory, true),
            Saved(kept, ModSource.SteamWorkshop, true), Saved("SmmCrashOther", ModSource.ModsDirectory, false)] };
        ModSettings Disordered() => new() { ModList = [Saved(kept, ModSource.SteamWorkshop, false), Saved(kept, ModSource.ModsDirectory, true),
            Saved(ModEntry.ModId, ModSource.ModsDirectory, true)] };
        List<(string, ModSource, bool)> asRead = [(ModEntry.ModId, ModSource.ModsDirectory, true), (kept, ModSource.ModsDirectory, true),
            (kept, ModSource.SteamWorkshop, false), ("SmmCrashOther", ModSource.ModsDirectory, false)];
        var (repaired, settled, unread) = (Rebuilt(), Disordered(), Disordered());
        var record = Path.Combine(outDir, "crash-offer-repair", TurnedOff.FileName);
        if (File.Exists(record))
            File.Delete(record);
        bool changed, settledChanged, unreadChanged;
        try
        {
            TurnedOff.TestFile = record;
            changed = LaunchGuard.RepairBeforeMenu(repaired, asRead, settled: false);
            settledChanged = LaunchGuard.RepairBeforeMenu(settled, asRead, settled: true);
            TurnedOff.TestSteam = false;
            TurnedOff.Add([OffEntry("SmmCrashKeptOff") with { Switches = [new(ModSource.SteamWorkshop, true, [])] }]);
            unreadChanged = LaunchGuard.RepairBeforeMenu(unread, null, settled: false);
        }
        finally
        {
            (TurnedOff.TestFile, TurnedOff.TestSteam) = (null, null);
        }
        static string Switches(ModSettings s) => string.Join(", ", s.ModList.Select(e => $"{e.Id}/{e.Source}={e.IsEnabled}"));
        Check("before the first main menu: the game's rebuilt list in memory repaired before anything saves it (a Workshop copy kept off reads off again, "
            + "the rest as it was); not after the first main menu, nor without the list as the game read it, but for a turned-off mod's dropped entry, off",
            changed && Switches(repaired) == $"{ModEntry.ModId}/ModsDirectory=True, {kept}/ModsDirectory=True, {kept}/SteamWorkshop=False, SmmCrashOther/ModsDirectory=False"
            && SameIdLoad.SettleList(Disordered().ModList, asRead)
            && !settledChanged && Switches(settled) == Switches(Disordered())
            && unreadChanged && Switches(unread) == Switches(Disordered()) + ", SmmCrashKeptOff/SteamWorkshop=False",
            $"repaired: {Switches(repaired)}; settled: {Switches(settled)}; unread: {Switches(unread)}");
        // This launch reached the menu (Settled), so the patches' own call leaves the game's list alone, though it holds an
        // id's entry that's off before one that's on (made up here, then taken out): the repair would reorder them.
        var live = SaveManager.Instance.SettingsSave.ModSettings ?? new ModSettings();
        var pair = new[] { Saved("SmmCrashMenu", ModSource.ModsDirectory, false), Saved("SmmCrashMenu", ModSource.SteamWorkshop, true) };
        live.ModList.AddRange(pair);
        try
        {
            var menuList = Switches(live);
            LaunchGuard.RepairBeforeMenu();
            Check("... at the main menu, the repair before it leaves the game's list alone", SameIdLoad.Settled && Switches(live) == menuList,
                $"settled {SameIdLoad.Settled}; {Switches(live)}");
        }
        finally
        {
            live.ModList.RemoveAll(e => e.Id == "SmmCrashMenu");
        }
        var now = DateTime.Now;
        static LaunchGuard.Entry Mod(string id, bool on, string? version = "1.0") => new(id, ModSource.ModsDirectory, on, version);
        LaunchGuard.Setup Setup(List<LaunchGuard.Entry> mods, string game = "0.111.0 (abc)") => new(now, mods, game);
        List<LaunchGuard.Entry> working = [Mod(ModEntry.ModId, true), Mod("SmmTestFixes", true), Mod("SmmTestLib", true)];
        var none = LaunchGuard.CrashOffer(Setup(working), null);
        var same = LaunchGuard.CrashOffer(Setup(working), Setup(working));
        var updated = LaunchGuard.CrashOffer(Setup([Mod(ModEntry.ModId, true), Mod("SmmTestFixes", true, "1.1"), Mod("SmmTestLib", true)]), Setup(working));
        var added = LaunchGuard.CrashOffer(Setup([.. working, Mod("SmmTestUi", true)]), Setup(working));
        var newGame = LaunchGuard.CrashOffer(Setup([.. working, Mod("SmmTestUi", true)], "0.112.0 (def)"), Setup(working));
        Check("after a failed start: nothing to offer without a working setup, with the same mods or with only an update; "
            + "a mod new since: offered; a game update since: said",
            none == null && same == null && updated == null && added is { GameChanged: false } offered
            && offered.Changes.Any(c => c.Id == "SmmTestUi" && c.Kind == LaunchGuard.Kind.New) && newGame is { GameChanged: true },
            $"none {none != null}, same {same != null}, updated {updated != null}, added {added?.Changes.Count}, new game {newGame?.GameChanged}");
        // The list Restore would write against the one the launch read: no offer when they load the same (after a
        // Restore, or an order the game's sorting sets), whatever the order of the mods off.
        static (string, ModSource, bool) E(string id, bool on) => (id, ModSource.ModsDirectory, on);
        List<(string, ModSource, bool)> read = [E(ModEntry.ModId, true), E("A", true), E("B", true), E("C", false), E("D", false)];
        Check("after a failed start: the same load with the mods off in another order; not with mods on in another order, one switched, or one missing (it would load)",
            LaunchGuard.SameLoad(read, read) && LaunchGuard.SameLoad(read, [read[0], read[1], read[2], read[4], read[3]])
            && !LaunchGuard.SameLoad(read, [read[0], read[2], read[1], read[3], read[4]]) && !LaunchGuard.SameLoad(read, [.. read[..3], E("C", true), read[4]])
            && !LaunchGuard.SameLoad(read, read[..4]));

        var settings = SaveManager.Instance.SettingsSave.ModSettings;
        if (settings == null || NGame.Instance is not { } game || Dependencies.Find("SmmTestFixes") is not { } fixes || !Copies.Ticked(fixes))
        {
            Results.Add("SKIP the dialog after a failed start: no mod list, game, or Test Fixes on");
            return;
        }
        var listWas = settings.ModList.Select(e => new SettingsSaveMod { Id = e.Id, Source = e.Source, IsEnabled = e.IsEnabled }).ToList();
        // This launch as it is; the working setup the same but with Test Fixes off (so it was enabled since).
        var current = History.CurrentMods().Select(m => new LaunchGuard.Entry(m.Id, m.Source, m.Enabled, null)).ToList();
        var before = current.Select(m => m.Id == "SmmTestFixes" ? m with { Enabled = false } : m).ToList();
        // The guard below compares with the list as the game read it (Begin's copy), not the one it put in place once the
        // mods loaded, which names every installed mod: dev.cs's list names only a few.
        var listNow = SaveManager.Instance.SettingsSave.ModSettings?.ModList.Count ?? 0;
        Check("after a failed start: the list compared is the one the game read, from before it put its own in place",
            LaunchGuard.ReadList is { } readList && readList.Count < listNow, $"read {LaunchGuard.ReadList?.Count}, now {listNow}");
        // An order the game's own sorting sets (a dependency an update added) against the list a Restore wrote: no
        // offer, or each Restore would fail the same and offer it again. The same order as the player's own: offered.
        // Mods listed once: the game sorts an id by its last entry, so swapping one of two copies' entries loads nothing differently.
        var on = current.Select((m, i) => (m, i)).Where(p => p.m.Enabled && p.m.Id is not (ModEntry.ModId or ModDoctor.ModId) && current.Count(c => c.Id == p.m.Id) == 1)
            .Select(p => p.i).Take(2).ToList();
        if (on.Count == 2)
        {
            var sorted = current.ToList();
            (sorted[on[0]], sorted[on[1]]) = (sorted[on[1]], sorted[on[0]]);
            var restored = LaunchGuard.Written(current).Select(e => (e.Id, e.Source, e.IsEnabled)).ToList();
            var reordered = restored.ToList();
            var (first, second) = (reordered.FindIndex(e => e.Id == current[on[0]].Id), reordered.FindIndex(e => e.Id == current[on[1]].Id));
            (reordered[first], reordered[second]) = (reordered[second], reordered[first]);
            var forced = LaunchGuard.CrashOffer(new(now, sorted), new(now.AddHours(-1), current), restored);
            var chosen = LaunchGuard.CrashOffer(new(now, sorted), new(now.AddHours(-1), current), reordered);
            Check("after a failed start: no offer for a load order the game's sorting sets (the list read is the one Restore writes); "
                + "offered for one the player's list sets", forced == null && chosen is { } order && order.Changes.Any(c => c.Kind == LaunchGuard.Kind.Order),
                $"forced {forced != null}, chosen {string.Join(", ", chosen?.Changes.Select(c => c.Kind) ?? [])}");
        }
        else
            Results.Add("SKIP the offer for a sorted load order: fewer than two fixture mods on");
        if (LaunchGuard.CrashOffer(new(now, current), new(now.AddHours(-1), before)) is not { } offer)
        {
            Check("the dialog after a failed start: an offer to show", false);
            return;
        }
        var (quits, relaunches) = (0, 0);
        ModEntry.TestQuit = () => quits++;
        ModEntry.TestRelaunch = plain => relaunches += plain ? 1 : 0; // a plain quit (the game's would save a half-started game)
        var transitionWas = game.Transition.Visible; // the dialog's setup hides the startup transition
        var pendingPath = Path.Combine(LaunchGuard.Dir ?? OS.GetUserDataDir(), LaunchGuard.PendingFile);
        var root = menu.GetTree().Root;
        // The game's own message for a failed start (its dialog, which ours replaces), as each dialog has it: from the
        // game's text, and in English from its file. Not empty: the game has one.
        var gamesMessage = LaunchGuard.GamesMessage(plain: false).Trim();
        var gamesMessagePlain = LaunchGuard.GamesMessage(plain: true).Trim();
        // Styled: "Notice from the game" centred, then the message alone in an outlined, faintly filled box (the middle cell
        // of three, centred), our text after. Plain (no markup): the same words over the message.
        var boxed = System.Text.RegularExpressions.Regex.Match(gamesMessage, @"^\[center\]\[color=#[0-9a-f]{6}\]Notice from the game\[/color\]\[/center\]\n"
            + @"\[table=3\]\[cell [^\]]+\]\[table=1\].*?\[/table\]\[/cell\]\[cell expand=1 border=#[0-9a-f]{8} bg=#[0-9a-f]{8} padding=[0-9,]+\]\[center\]\p{Cf}"
            + @"(?<message>.+)\[/center\]\[/cell\]\[cell [^\]]+\]\[table=1\].*?\[/table\]\[/cell\]\[/table\]$");
        Check("the game's own message for a failed start, as the dialogs show it: under \"Notice from the game\" (centred), alone in its outlined box; "
            + "plain, under the same words; its markup read by the game's auto-size",
            boxed.Success && GameReads(gamesMessage) && boxed.Groups["message"].Value.Length > 30 && !boxed.Groups["message"].Value.Contains("Notice from the game")
            && gamesMessagePlain.StartsWith("Notice from the game:\n") && gamesMessagePlain.Length > 50,
            $"{gamesMessage} | {gamesMessagePlain}");
        // Our popup's button (marked: no other popup's), by its node and signal names (the same in every game version so far).
        static void Press(string button)
        {
            if (NModalContainer.Instance?.OpenModal is Node popup && popup.FindChild(Menus.MarkName, true, false) != null
                && popup.GetNodeOrNull($"VerticalPopup/{button}") is { } found)
                found.EmitSignal("Released", found);
        }
        string PlainTexts() => root.GetNodeOrNull<ConfirmationDialog>(LaunchGuard.PlainDialogName) is { Visible: true } plain
            ? $"{plain.Title} | {plain.DialogText} | {plain.OkButtonText} | {plain.CancelButtonText}" : "";
        void Answer(StringName signal) => root.GetNodeOrNull<ConfirmationDialog>(LaunchGuard.PlainDialogName)?.EmitSignal(signal);
        try
        {
            // Disable suspects records the mods it turns off: in a file of the test's, so no notice or banner of them follows
            TurnedOff.TestFile = Path.Combine(outDir, "crash-offer", TurnedOff.FileName);
            // A launch record as a failed start leaves one (none is left at the menu): an answer, Quit too, deletes it.
            File.WriteAllText(pendingPath, "{}");
            var asked = LaunchGuard.AskAfterCrash(game, new LaunchGuard.Crash(offer, null, null, offer.Working));
            await Wait(menu, 0.8);
            var texts = string.Join(" | ", PopupTexts());
            CheckMark("dialog after a failed start", NModalContainer.Instance?.OpenModal as Control);
            await Capture(menu, outDir, "1f-crash-dialog");
            // The game's message in its box (GameText), as in the panel and the cards; the popup still fits its text.
            var words = (NModalContainer.Instance?.OpenModal as Node)?.GetNodeOrNull<RichTextLabel>("VerticalPopup/Description");
            var dialogBox = words == null ? "no text" : BoxesWrong(words, 1);
            Check("the game's box in the dialog after a failed start: as in the panel and the cards (an even 2 px border, round corners, 2 px inside its "
                + "table, no side cut off); the dialog's text fits it", dialogBox == null && words != null && words.GetContentHeight() <= words.Size.Y + 1,
                $"{dialogBox}; {words?.GetContentHeight()} tall in {words?.Size}");
            Press("NoButton");
            await Wait(menu, 0.3);
            Check("the dialog after a failed start: its title, the game's own message (marked as the game's), what changed, Quit and Restore and restart; "
                + "Quit writes no list, deletes the launch's record and quits",
                asked.IsCompleted && texts.Contains("The game couldn't start") && texts.Contains(gamesMessage) && texts.Contains("Test Fixes") && texts.Contains("Restore and restart")
                && quits == 1 && relaunches == 0 && Copies.Ticked(fixes) && !File.Exists(pendingPath),
                $"{texts}; quits {quits}, relaunches {relaunches}, record left {File.Exists(pendingPath)}");
            asked = LaunchGuard.AskAfterCrash(game, new LaunchGuard.Crash(offer, null, null, offer.Working));
            await Wait(menu, 0.8);
            Press("YesButton");
            await Wait(menu, 0.3);
            Check("the dialog after a failed start: Restore and restart writes the working list (Test Fixes off again), then relaunches",
                asked.IsCompleted && relaunches == 1 && quits == 1 && !Copies.Ticked(fixes), $"quits {quits}, relaunches {relaunches}, Test Fixes on {Copies.Ticked(fixes)}");

            // Where the game's text can't load (a mod broke it): Godot's plain dialog, in English.
            settings.ModList.Clear();
            settings.ModList.AddRange(listWas.Select(e => new SettingsSaveMod { Id = e.Id, Source = e.Source, IsEnabled = e.IsEnabled }));
            LaunchGuard.TestNoLanguage = true;
            asked = LaunchGuard.AskAfterCrash(game, new LaunchGuard.Crash(offer, null, null, offer.Working));
            await Wait(menu, 0.5);
            var plain = PlainTexts();
            Answer(AcceptDialog.SignalName.Canceled);
            await Wait(menu, 0.3);
            Check("the plain dialog after a failed start (no game text): its title, the game's own message in English, what changed, Quit and Restore and restart; "
                + "Quit writes nothing and quits",
                asked.IsCompleted && plain.Contains("The game couldn't start") && plain.Contains(gamesMessagePlain) && plain.Contains("Test Fixes (enabled)") && plain.Contains("Restore and restart")
                && plain.Contains("Quit") && quits == 2 && relaunches == 1 && Copies.Ticked(fixes), $"{plain}; quits {quits}, relaunches {relaunches}");
            asked = LaunchGuard.AskAfterCrash(game, new LaunchGuard.Crash(offer, null, null, offer.Working));
            await Wait(menu, 0.5);
            Answer(AcceptDialog.SignalName.Confirmed);
            await Wait(menu, 0.3);
            Check("the plain dialog after a failed start: Restore and restart writes the working list, then relaunches; the dialog goes",
                asked.IsCompleted && relaunches == 2 && quits == 2 && !Copies.Ticked(fixes) && root.GetNodeOrNull(LaunchGuard.PlainDialogName) == null,
                $"quits {quits}, relaunches {relaunches}, Test Fixes on {Copies.Ticked(fixes)}, dialog left {root.GetNodeOrNull(LaunchGuard.PlainDialogName) != null}");
            // Mod Doctor's suspects (made up: Test UI, which this launch has on, among them, ours left out as we read them
            // (our own reading decides about us), Mod Doctor never turned off, and one not installed): three named, the rest
            // under "more", whose card names them; a third button as a quiet link under the game's two, which turns them off
            // and restarts, the rest of this launch's list as it was. Escape stays the game's Quit.
            LaunchGuard.TestNoLanguage = false;
            ModDoctor.TestSuspects = [ModEntry.ModId, ModDoctor.ModId, "SmmTestUi", "SmmTestFixes", "SmmTestLib", "SmmTestNotThere"];
            void ListAsItWas()
            {
                settings.ModList.Clear();
                settings.ModList.AddRange(listWas.Select(e => new SettingsSaveMod { Id = e.Id, Source = e.Source, IsEnabled = e.IsEnabled }));
            }
            bool Listed(string id, bool on) => settings.ModList.Any(e => e.Id == id && e.IsEnabled == on);
            // Every entry of an id off, whatever its source (a mod listed from two sources has two).
            bool AllOff(string id) => settings.ModList.Any(e => e.Id == id) && settings.ModList.Where(e => e.Id == id).All(e => !e.IsEnabled);
            ListAsItWas();
            var libWasOn = Listed("SmmTestLib", true);
            asked = LaunchGuard.AskAfterCrash(game, new LaunchGuard.Crash(offer, null, null, offer.Working));
            await Wait(menu, 0.8);
            var popup = NModalContainer.Instance?.OpenModal as Control;
            var third = popup?.FindChild(LaunchGuard.LinkName, true, false) as Control;
            var (quitButton, restoreButton) = (popup?.GetNodeOrNull<Control>("VerticalPopup/NoButton"), popup?.GetNodeOrNull<Control>("VerticalPopup/YesButton"));
            // A quiet link under the two, centred on the popup's art, overlapping neither: orange, a pointer over it, lit and
            // underlined on hover.
            var art = popup?.GetNodeOrNull<Control>("VerticalPopup");
            var between = third is LinkButton { Underline: LinkButton.UnderlineMode.Never, MouseDefaultCursorShape: Control.CursorShape.PointingHand } link
                && art != null && quitButton != null && restoreButton != null && link.GetGlobalRect().Position.Y >= restoreButton.GetGlobalRect().End.Y - 4
                && Math.Abs(link.GetGlobalRect().GetCenter().X - art.GetGlobalRect().GetCenter().X) < 2 && link.Size.X > 100
                && !link.GetGlobalRect().Intersects(quitButton.GetGlobalRect()) && !link.GetGlobalRect().Intersects(restoreButton.GetGlobalRect())
                && link.GetThemeColor("font_color") is { R: > 0.9f, G: > 0.6f and < 0.8f, B: < 0.5f } && link.GetThemeColor("font_hover_color") != link.GetThemeColor("font_color")
                && art.GetNodeOrNull<Control>(Menus.MarkName) is { } mark && mark.GetGlobalRect().Position.Y >= link.GetGlobalRect().End.Y; // our mark under it, clear of it
            var suspectTexts = string.Join(" | ", PopupTexts());
            var suspectsLine = System.Text.RegularExpressions.Regex.Match(suspectTexts, @"Mod Doctor suspects: [^|\n]*").Value;
            await Capture(menu, outDir, "1h-crash-dialog-suspects");
            var suspectsCard = popup?.GetNodeOrNull<RichTextLabel>("VerticalPopup/Description") is { } description
                ? await LinkCard(description, "spire-mod-manager-more-suspects") : "";
            Check("the dialog with Mod Doctor's suspects, ours left out: three named, the rest counted as a link whose card names them; a third button, "
                + "\"Disable suspect mods and restart\", a quiet orange link under Quit and Restore and restart, centred",
                third is LinkButton { Visible: true, Text: "Disable suspect mods and restart" } && between && suspectsLine.Contains("Test UI")
                && suspectsLine.Contains("Test Fixes") && suspectsLine.Contains("2 more") && !suspectsLine.Contains("Spire Mod Manager")
                && suspectsCard.Contains("Test Framework") && suspectsCard.Contains("SmmTestNotThere") && !suspectsCard.Contains("Test Fixes"),
                $"third {third != null}, between {between}; {suspectTexts}; card: {suspectsCard}");
            Input.ParseInputEvent(new InputEventKey { Keycode = Key.Escape, Pressed = true });
            await Wait(menu, 0.1);
            Input.ParseInputEvent(new InputEventKey { Keycode = Key.Escape, Pressed = false });
            await Wait(menu, 0.3);
            Check("Escape on that dialog is still the game's Quit, not the third button", asked.IsCompleted && quits == 3 && relaunches == 2
                && Listed("SmmTestUi", true) == listWas.Any(e => e.Id == "SmmTestUi" && e.IsEnabled), $"quits {quits}, relaunches {relaunches}");
            if (!asked.IsCompleted)
                Press("NoButton");
            // On stable the same Escape then reaches the main menu, where it's the profile button's hotkey (pauseAndBack):
            // the profile screen opens. A player's game would have quit; here, back to the main menu for what follows.
            for (var i = 0; i < 3 && menu.SubmenuStack.SubmenusOpen; i++)
            {
                menu.SubmenuStack.Pop();
                await Wait(menu, 0.3);
            }
            ListAsItWas();
            asked = LaunchGuard.AskAfterCrash(game, new LaunchGuard.Crash(offer, null, null, offer.Working));
            await Wait(menu, 0.8);
            if ((NModalContainer.Instance?.OpenModal as Node)?.FindChild(LaunchGuard.LinkName, true, false) is BaseButton suspectsButton)
                suspectsButton.EmitSignal(BaseButton.SignalName.Pressed);
            await Wait(menu, 0.3);
            Check("Disable suspect mods and restart: this launch's list with Test UI, Test Fixes and Test Framework off (not ours, not Mod Doctor, "
                + "nothing added for a mod not installed), then a restart; the launch's record goes",
                asked.IsCompleted && relaunches == 3 && quits == 3 && AllOff("SmmTestUi") && AllOff("SmmTestFixes")
                && (!libWasOn || AllOff("SmmTestLib")) && Listed(ModEntry.ModId, true) && !settings.ModList.Any(e => e.Id == "SmmTestNotThere")
                && !File.Exists(pendingPath),
                $"quits {quits}, relaunches {relaunches}; {string.Join(", ", settings.ModList.Select(e => $"{e.Id}={e.IsEnabled}"))}");
            // The plain dialog: the third answer as one more button.
            ListAsItWas();
            LaunchGuard.TestNoLanguage = true;
            asked = LaunchGuard.AskAfterCrash(game, new LaunchGuard.Crash(offer, null, null, offer.Working));
            await Wait(menu, 0.5);
            var plainDialog = root.GetNodeOrNull<ConfirmationDialog>(LaunchGuard.PlainDialogName);
            var plainThird = plainDialog?.FindChildren("*", "Button", true, false).OfType<Button>().Any(b => b.Text == "Disable suspect mods and restart") == true;
            plainDialog?.EmitSignal(AcceptDialog.SignalName.CustomAction, LaunchGuard.LinkAction);
            await Wait(menu, 0.3);
            Check("the plain dialog with suspects: the third button, which turns them off and restarts",
                plainThird && asked.IsCompleted && relaunches == 4 && quits == 3 && AllOff("SmmTestUi") && Listed(ModEntry.ModId, true),
                $"button {plainThird}, quits {quits}, relaunches {relaunches}");
            ModDoctor.TestSuspects = null;
            ListAsItWas();

            // At its longest: more changes than it lists and a game update since, captured to see that it all fits.
            LaunchGuard.TestNoLanguage = false;
            List<LaunchGuard.Entry> many = [.. Enumerable.Range(1, 7).Select(i => Mod($"SmmTestMadeUp{i}", true))];
            if (LaunchGuard.CrashOffer(Setup([.. working, .. many], "0.112.0 (def)"), Setup(working)) is { } longest)
            {
                asked = LaunchGuard.AskAfterCrash(game, new LaunchGuard.Crash(longest, null, null, longest.Working));
                await Wait(menu, 0.8);
                await Capture(menu, outDir, "1g-crash-dialog-longest");
                var longTexts = string.Join(" | ", PopupTexts());
                var moreCard = (NModalContainer.Instance?.OpenModal as Node)?.GetNodeOrNull<RichTextLabel>("VerticalPopup/Description") is { } longDescription
                    ? await LinkCard(longDescription, "spire-mod-manager-more-changes") : "";
                var noThird = (NModalContainer.Instance?.OpenModal as Node)?.FindChild(LaunchGuard.LinkName, true, false) == null;
                Press("NoButton");
                await Wait(menu, 0.3);
                Check("the dialog at its longest (7 changes, a game update since): 4 listed, 3 counted, the update said, the game's message first; "
                    + "no suspects, so no third button",
                    asked.IsCompleted && longTexts.Contains("3 more") && longTexts.Contains("The game itself was updated") && longTexts.Contains(gamesMessage) && noThird, longTexts);
                Check("the dialog at its longest: \"3 more\" is a link whose card names the three changes it counts",
                    moreCard.Contains("SmmTestMadeUp5") && moreCard.Contains("SmmTestMadeUp7") && !moreCard.Contains("SmmTestMadeUp4"), moreCard);
            }
            else
                Check("the dialog at its longest: an offer for 7 new mods and a game update since", false, "no offer");
        }
        finally
        {
            ModEntry.TestQuit = null;
            ModEntry.TestRelaunch = null;
            LaunchGuard.TestNoLanguage = false;
            ModDoctor.TestSuspects = null;
            TurnedOff.TestFile = null;
            game.Transition.Visible = transitionWas;
            NModalContainer.Instance?.Clear();
            root.GetNodeOrNull(LaunchGuard.PlainDialogName)?.QueueFree();
            File.Delete(pendingPath);
            settings.ModList.Clear();
            settings.ModList.AddRange(listWas);
            SaveManager.Instance.SaveSettings();
        }
    }

    // The main menu button's Open translations folder: the folder, in our data folder beside manager.json, is made if it
    // isn't there (a fresh sandbox has none; an empty one is removed first) and handed to the system's file browser (to Translations.TestOpen here, so
    // no window opens).
    static async Task TestTranslationsFolder(NMainMenu menu)
    {
        if (menu.GetNodeOrNull<Control>("SpireModManagerMenuButton") is not { } button)
            return;
        var folder = Translations.Folder;
        if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
            Directory.Delete(folder); // an empty one left over: gone, so the press has to make it
        var before = Directory.Exists(folder);
        string? opened = null;
        Translations.TestOpen = path => opened = path;
        try
        {
            if (await ButtonMenu(menu, button) is { } popup)
            {
                // It opens the system's file browser (#126): the external-link mark after its label, in the label's
                // colour, and only there in the menu.
                var labels = popup.FindChildren("*", "Label", true, false).OfType<Label>().ToList();
                var item = labels.FirstOrDefault(l => l.Text == "Open translations folder");
                var mark = item == null ? null : LinkMark.Rect(item);
                var itemSize = item?.GetThemeFontSize("font_size") ?? 0;
                var itemText = item == null ? 0 : item.GetThemeFont("font").GetStringSize(item.Text, fontSize: itemSize).X;
                Check("Open translations folder: the external-link mark a small gap after its label, 14 px for its 20 px text; no other item has one",
                    item != null && itemSize == 20 && mark is { } at && at.Position.X >= itemText + 2 && at.Position.X <= itemText + 7 && at.Size == new Vector2(14, 14)
                    && at.End.X <= item.Size.X + 0.5f && labels.Where(l => l.IsInGroup(LinkMark.Group)).SequenceEqual([item]),
                    $"mark {mark} after text {itemText:0.#}, label {item?.Size}; marked: {string.Join(", ", labels.Where(l => l.IsInGroup(LinkMark.Group)).Select(l => l.Text))}");
                if (item != null)
                    Results.Add($"INFO Open translations folder's mark: {mark} in its {item.Size} label, the text's baseline "
                        + $"{LinkMark.Baseline(item, item.Text, item.GetThemeFont("font"), itemSize):0.##} (the font's ascent {item.GetThemeFont("font").GetAscent(itemSize):0.##})");
                var drawn = item == null ? null : await MarkDrawn(menu, item);
                Check("Open translations folder's mark: drawn in the label's colour", item != null && drawn == item.GetThemeColor("font_color"),
                    $"drawn {drawn?.ToString() ?? "nothing"}, label {item?.GetThemeColor("font_color")}");
                MenuPanel.Press(popup, "Open translations folder");
            }
            await Wait(menu, 0.2);
            Check("Open translations folder: made in the data folder and opened, the menu closed", !before && opened == folder && Directory.Exists(folder)
                && folder.Replace('\\', '/').EndsWith($"/mod_data/{ModEntry.ModId}/translations") && !MenuPanel.IsOpen,
                $"opened {opened ?? "nothing"}; {folder} there before: {before}");
            // The example: how-to lines, then every English text of ours, under a name no language has.
            var example = Path.Combine(folder, Translations.ExampleFile);
            var text = File.Exists(example) ? File.ReadAllText(example) : "";
            Dictionary<string, string>? pairs = null;
            try
            {
                pairs = JsonSerializer.Deserialize<Dictionary<string, string>>(text, new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip });
            }
            catch (JsonException)
            {
            }
            var english = JsonSerializer.Deserialize<Dictionary<string, string>>(Godot.FileAccess.GetFileAsString($"res://{ModEntry.ModId}/localization/eng/settings_ui.json"));
            Check("the translations folder has the example: how-to lines, then every English text, never read (no language's name)",
                text.StartsWith("// An example") && pairs != null && english != null && pairs.Count == english.Count && english.All(e => pairs.GetValueOrDefault(e.Key) == e.Value)
                && !Path.GetFileNameWithoutExtension(Translations.ExampleFile).All(char.IsLetter), $"{text.Length} chars, {pairs?.Count} texts of {english?.Count}");
            // Its codes: the languages this game has, every one but English (stable has fewer than beta), within 118 columns.
            var codeLines = Translations.CodeLines().ToList();
            var listed = codeLines.SelectMany(l => System.Text.RegularExpressions.Regex.Matches(l, @"(?<![\w(])[a-z]{3}(?=[ ,.])").Select(m => m.Value)).ToList();
            Check("the example's codes: the game's languages but English, each once, wrapped within 118 columns, in the example",
                listed.Order().SequenceEqual(LocManager.Languages.Where(c => c != "eng").Order()) && codeLines.All(l => l.Length <= 118 && text.Contains(l)),
                $"{string.Join(" ", listed)} vs {string.Join(" ", LocManager.Languages)}");
            // Written again while it's as we left it (an older version's, say); once edited, it's the player's.
            File.WriteAllText(example, "// an older example\n{}");
            File.SetLastWriteTimeUtc(example, Translations.ExampleTime);
            Translations.Open();
            var refreshed = File.ReadAllText(example) == Translations.ExampleText();
            File.WriteAllText(example, "// my translation, started here\n{}");
            Translations.Open();
            Check("the example: written again while it's as we left it, kept once it's edited", refreshed
                && File.ReadAllText(example) == "// my translation, started here\n{}", $"refreshed {refreshed}");
            File.Delete(example);
            Translations.Open();
        }
        finally
        {
            Translations.TestOpen = null;
            MenuPanel.Close();
        }
    }

    // The page's text shaped ahead on the main menu (Prewarm): only texts with a letter Kreon lacks (a Chinese alias
    // here; the fixtures' own text is Latin), each once, and the pass gets to the end.
    static async Task TestPrewarm(NMainMenu menu)
    {
        const string Alias = "测试别名";
        var before = Store.InfoFor("SmmTestContent")?.Alias;
        Store.EditMod("SmmTestContent", info => info.Alias = Alias);
        try
        {
            Prewarm.Start(menu);
            for (var i = 0; i < 50 && !Prewarm.Done; i++)
                await WaitReal(menu, 0.1); // Prewarm starts a second after Start (StartDelay), then works a frame at a time
            // Once if Kreon lacks its letters; not at all where a mod gave the font Chinese ones (RitsuLib-based mods can).
            var font = ModEntry.Font("res://themes/kreon_regular_shared.tres");
            var lacks = Alias.EnumerateRunes().Any(r => !font.HasChar(r.Value));
            Check("the page's text is shaped ahead on the main menu: a Chinese alias once (none if the font has its letters), nothing all-Latin, to the end",
                Prewarm.Done && Prewarm.Shaped.Count(t => t == Alias) == (lacks ? 1 : 0) && !Prewarm.Shaped.Any(t => t.All(c => c < 0x80)),
                $"done {Prewarm.Done}, the font lacks its letters {lacks}: {string.Join(" | ", Prewarm.Shaped)}");
        }
        finally
        {
            Store.EditMod("SmmTestContent", info => info.Alias = before);
        }
    }

    // A key's text in our packed English, as written (placeholders and all), whatever the game's language is now.
    static string EnglishText(string key) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(Godot.FileAccess.GetFileAsString($"res://{ModEntry.ModId}/localization/eng/settings_ui.json"))
            ?.GetValueOrDefault(key) ?? "";

    // Drop-in translations (Translations) in German, which has no packed translation, so our text is all English there
    // until now: a file in the translations folder, read as the game switches language (its own switch, as in its
    // settings, without the menu rebuild). Its text shows, in the game's table and on the main menu button's menu, and its
    // {count} takes German numbers; a key it leaves out, an empty text, a number, a broken placeholder and one English
    // doesn't fill in (renamed or added) show in English, and a key English doesn't have is left out.
    // Back in English, English. A file that isn't JSON, isn't an object or is over 1 MB changes nothing and throws
    // nothing. One saved as deu.json.txt (a browser's "Save as", the .txt hidden) is read. The file is removed and the
    // language set back afterwards.
    static async Task TestTranslations(NMainMenu menu)
    {
        // The tool reads texts as the game does only with the game's parser: when the game ships another version, update
        // the package in tools/loc.cs and ToolParser here.
        var parser = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "SmartFormat")
            ?.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+')[0];
        Check($"translation tools: the game's text parser is SmartFormat {ToolParser}, the one tools/loc.cs uses", parser == ToolParser,
            $"the game has {parser ?? "no SmartFormat loaded"}");
        var start = LocManager.Instance.Language;
        if (start != "eng" || Godot.FileAccess.FileExists($"res://{ModEntry.ModId}/localization/deu/settings_ui.json"))
        {
            Results.Add($"SKIP drop-in translations: the game is in {start}, or German is packed");
            return;
        }
        const string Translated = "SPIRE_MOD_MANAGER.RELAUNCH_GAME", LeftOut = "SPIRE_MOD_MANAGER.RESET_POSITION",
            Broken = "SPIRE_MOD_MANAGER.MOVE_BUTTON", Empty = "SPIRE_MOD_MANAGER.MOVE_HINT";
        const string NotOurs = Translated + "X"; // a key mistyped
        const string Renamed = "SPIRE_MOD_MANAGER.PAGE_DISABLED", Added = "SPIRE_MOD_MANAGER.OPEN_TRANSLATIONS";
        const string Counted = "SPIRE_MOD_MANAGER.PAGE_ALL", NotText = "SPIRE_MOD_MANAGER.NEEDS_UPDATE";
        // A plural (the game's formatter takes one only of a number; ours pass text) and a misspelled formatter: both parse,
        // and both fail on screen (shown raw, an error logged and reported on every draw).
        const string Plural = "SPIRE_MOD_MANAGER.MISSING_WARNING", Misspelled = "SPIRE_MOD_MANAGER.SUBSCRIBE_ALL";
        const string OnButton = "SPIRE_MOD_MANAGER.MAIN_MENU_PROFILE", OnButtonOne = "SPIRE_MOD_MANAGER.MAIN_MENU_PROFILE_ONE";
        // Listed twice: a good copy, then a blank one; and a blank one, then a good one (tools/loc.cs reads them alike).
        const string TwiceKept = "SPIRE_MOD_MANAGER.NOTES_TERM", TwiceLater = "SPIRE_MOD_MANAGER.NOTES_ADD";
        const string German = "Spiel neu starten (Änderungen, 模组)"; // not ASCII: read as UTF-8, with a byte order mark
        string[] keys = [Translated, LeftOut, Broken, Empty];
        static string Read(string key)
        {
            try
            {
                return Menus.Text(key);
            }
            catch (Exception e)
            {
                return e.Message;
            }
        }
        var english = keys.ToDictionary(k => k, Read);
        string Shown() => string.Join(" | ", keys.Select(Read));
        bool AllEnglish() => keys.All(k => Read(k) == english[k]);
        var file = Path.Combine(Translations.Folder, "deu.json");
        // The file written, then the game switched to German. The exception's message if the switch threw.
        string? Switch(string text)
        {
            File.WriteAllText(file, text, new UTF8Encoding(true));
            try
            {
                LocManager.Instance.SetLanguage("deu");
                return null;
            }
            catch (Exception e)
            {
                return e.Message;
            }
        }
        try
        {
            Directory.CreateDirectory(Translations.Folder);
            var threw = Switch($$"""
                {
                  // a comment and a trailing comma, as a hand-edited file may have
                  "{{Translated}}": "{{German}}",
                  "{{Counted}}": "Installierte Mods ({count})",
                  "{{OnButton}}": "Mod-Profil: {name} ({count} Mods)",
                  "{{OnButtonOne}}": "Mod-Profil: {name} (1 Mod)",
                  "{{Broken}}": "Knopf {verschieben",
                  "{{Empty}}": " ",
                  "{{Renamed}}": "Deaktivierte Mods ({anzahl})",
                  "{{Added}}": "Ordner {ordner}",
                  "{{Plural}}": "Dieses Profil hat {count} aktivierte {count:plural:Mod|Mods}, die nicht installiert sind.",
                  "{{Misspelled}}": "Alle abonnieren ({count:plurl:eins|mehr})",
                  "{{NotText}}": 5,
                  "{{NotOurs}}": "Nicht unser Text",
                  "{{TwiceKept}}": "Notizen",
                  "{{TwiceKept}}": " ",
                  "{{TwiceLater}}": "",
                  "{{TwiceLater}}": "Notizen hinzufügen",
                }
                """);
            Check("a drop-in translation: its text shows in its language", threw == null && Read(Translated) == German, threw ?? Shown());
            var counted = Menus.Format(Counted, ("count", Menus.Number(1234)));
            Check("a drop-in translation: its {count} filled in, in German numbers", counted == "Installierte Mods (1.234)", counted);
            Check("a drop-in translation: a key it leaves out, an empty text and a broken placeholder show in English",
                Read(LeftOut) == english[LeftOut] && Read(Empty) == english[Empty] && Read(Broken) == english[Broken], Shown());
            var table = LocManager.Instance.GetTable("settings_ui");
            Check("a drop-in translation: a renamed placeholder, an added one and a number show in English",
                !table.IsLocalKey(Renamed) && !table.IsLocalKey(Added) && !table.IsLocalKey(NotText),
                $"{table.GetRawText(Renamed)} | {table.GetRawText(Added)} | {table.GetRawText(NotText)}");
            var plural = Menus.Format(Plural, ("count", Menus.Number(5)));
            Check("a drop-in translation: a plural form and a misspelled formatter show in English (the game can't format them)",
                !table.IsLocalKey(Plural) && !table.IsLocalKey(Misspelled) && plural == EnglishText(Plural).Replace("{count}", "5"),
                $"{table.GetRawText(Plural)} | {table.GetRawText(Misspelled)} | {plural}");
            Check("a drop-in translation: a key English doesn't have is left out", !table.HasEntry(NotOurs));
            Check("a drop-in translation: a key listed twice shows its last copy that isn't blank",
                Read(TwiceKept) == "Notizen" && Read(TwiceLater) == "Notizen hinzufügen", $"{Read(TwiceKept)} | {Read(TwiceLater)}");
            var popup = menu.GetNodeOrNull<Control>("SpireModManagerMenuButton") is { } button ? await ButtonMenu(menu, button) : null;
            var labels = popup == null ? [] : MenuPanel.Labels(popup);
            MenuPanel.Close();
            Check("a drop-in translation on screen: the main menu button's menu has it", labels.Contains(German),
                string.Join(", ", labels));
            // The button itself follows the language as it changes, its count in German numbers.
            var on = Profiles.OnCount();
            var german = on == 1 ? $"Mod-Profil: {Profiles.Active?.Name} (1 Mod)" : $"Mod-Profil: {Profiles.Active?.Name} ({Menus.Number(on)} Mods)";
            var germanSize = menu.GetNodeOrNull<Label>("SpireModManagerMenuButton/Label")?.GetThemeFontSize("font_size");
            Check("a drop-in translation on screen: the main menu button reads it, as wide as its words, still at its smaller size",
                MenuButtonText(menu) == german && MenuButtonMisfit(menu) == null && germanSize == MenuButton.TextSize,
                $"{MenuButtonText(menu)} vs {german}; {MenuButtonMisfit(menu)}; size {germanSize}");
            LocManager.Instance.SetLanguage(start);
            await Wait(menu, 0.1);
            Check("a drop-in translation: back in English, English", AllEnglish() && MenuButtonText(menu) == MenuButtonExpected()
                && MenuButtonMisfit(menu) == null, $"{Shown()} | {MenuButtonText(menu)}; {MenuButtonMisfit(menu)}");

            foreach (var (what, text) in new[]
            {
                ("not JSON", $"{{ \"{Translated}\": \"Spiel neu starten\""),
                ("not an object", "[\"Spiel neu starten\"]"),
                ("over 1 MB", $"{{ \"{Translated}\": \"Spiel neu starten\", \"{NotOurs}\": \"{new string('x', 1_100_000)}\" }}"),
            })
            {
                threw = Switch(text);
                Check($"a drop-in file {what}: changes nothing, throws nothing", threw == null && LocManager.Instance.Language == "deu" && AllEnglish(),
                    threw ?? Shown());
            }

            File.Delete(file);
            File.WriteAllText(file + ".txt", $"{{ \"{Translated}\": \"Spiel neu starten\" }}");
            LocManager.Instance.SetLanguage("deu");
            Check("a drop-in file saved as deu.json.txt: read all the same", Read(Translated) == "Spiel neu starten", Shown());
        }
        finally
        {
            File.Delete(file);
            File.Delete(file + ".txt");
            LocManager.Instance.SetLanguage(start);
        }
    }

    // Mod Doctor's file (no Mod Doctor here: written by hand, as it would for this process), read where it writes now,
    // mod_data/ModDoctor/startup.json, and at the user folder's top where it wrote before its move, the new place first.
    // In a folder of the test's own, as the game's user folder (the real game's too).
    static async Task TestModDoctorFile(Node menu, string outDir)
    {
        var top = Path.Combine(outDir, "mod-doctor-file");
        string[] files = [Path.Combine(top, "mod_data", ModDoctor.ModId, "startup.json"), Path.Combine(top, "mod_doctor_startup.json")];
        ModDoctor.TestFolder = top;
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        void Write(string file, string suspect)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file) ?? top);
            File.WriteAllText(file, new System.Text.Json.Nodes.JsonObject
            {
                ["version"] = 1, ["processId"] = System.Environment.ProcessId, ["processStartUtc"] = self.StartTime.ToUniversalTime().ToString("o"),
                ["fatal"] = new System.Text.Json.Nodes.JsonObject { ["kind"] = "test", ["message"] = "made up", ["step"] = "test", ["suspects"] = new System.Text.Json.Nodes.JsonArray(suspect) },
                ["failedMods"] = new System.Text.Json.Nodes.JsonArray(),
            }.ToJsonString());
        }
        var seen = new List<string>();
        try
        {
            Write(files[0], "SmmNewPlace");
            seen.Add($"new {string.Join(",", (await ModDoctor.WaitForFindings(menu)).Suspects)}");
            File.Delete(files[0]);
            Write(files[1], "SmmOldPlace");
            seen.Add($"old {string.Join(",", (await ModDoctor.WaitForFindings(menu)).Suspects)}");
            Write(files[0], "SmmNewPlace");
            seen.Add($"both {string.Join(",", (await ModDoctor.WaitForFindings(menu)).Suspects)}");
        }
        finally
        {
            foreach (var file in files)
                File.Delete(file);
            ModDoctor.TestFolder = null;
        }
        Check("Mod Doctor's file: read where it writes now, else where it wrote before its move; the new place first",
            seen.SequenceEqual(["new SmmNewPlace", "old SmmOldPlace", "both SmmNewPlace"]), string.Join(" | ", seen));

        // Its record of the mods it turned off (mod_data/ModDoctor/<platform>/<account id>/turned-off.json): a turn-off at or
        // after a failed launch's start counts (our notice about that launch stays out: its note says it), one before it
        // doesn't, nor a file of another version, nor none. Its times carry their offset: one written in UTC compares as
        // the same moment.
        var account = MegaCrit.Sts2.Core.Saves.UserDataPathProvider.GetAccountScopedBasePath(null).Replace("user://", "");
        var record = Path.Combine(top, "mod_data", ModDoctor.ModId, account, "turned-off.json");
        var turnedOff = DateTimeOffset.Now.AddMinutes(-5);
        void Record(int version, DateTimeOffset at)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(record) ?? top);
            File.WriteAllText(record, new System.Text.Json.Nodes.JsonObject
            {
                ["version"] = version,
                ["mods"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
                {
                    ["id"] = "SmmTurnedOff", ["name"] = "Turned Off", ["at"] = at.ToString("o"), ["reason"] = "Threw", ["sources"] = new System.Text.Json.Nodes.JsonArray("ModsDirectory"),
                }),
            }.ToJsonString());
        }
        ModDoctor.TestFolder = top;
        var said = new List<string>();
        try
        {
            said.Add($"none {ModDoctor.TurnedOffSince(DateTime.Now.AddHours(-1))}");
            Record(1, turnedOff);
            said.Add($"after {ModDoctor.TurnedOffSince(turnedOff.LocalDateTime.AddMinutes(-1))}");
            said.Add($"before {ModDoctor.TurnedOffSince(turnedOff.LocalDateTime.AddMinutes(1))}");
            Record(1, turnedOff.ToUniversalTime());
            said.Add($"utc {ModDoctor.TurnedOffSince(turnedOff.LocalDateTime.AddMinutes(-1))}");
            Record(2, turnedOff);
            said.Add($"v2 {ModDoctor.TurnedOffSince(turnedOff.LocalDateTime.AddMinutes(-1))}");
        }
        finally
        {
            File.Delete(record);
            ModDoctor.TestFolder = null;
        }
        Check("Mod Doctor's record of the mods it turned off: one turned off after a failed launch's start counts (our notice about it stays out), one before "
            + "doesn't, nor another version or none; a time in UTC the same moment",
            said.SequenceEqual(["none False", "after True", "before False", "utc True", "v2 False"]), string.Join(" | ", said));
    }

    // The first-run import reads Better Mod Menu's file as it does: comments and trailing commas, its old format (just
    // a list of profiles), nothing from a broken file, and of several files the one it would load. Sample texts, and
    // files in our output folder: the sandbox's data isn't touched.
    static void TestBetterModMenuImport(string outDir)
    {
        static StoreData? Import(string text)
        {
            try
            {
                return BetterModMenuImport.Parse(text);
            }
            catch (Exception)
            {
                return null;
            }
        }
        static string List(IEnumerable<string> items, string separator = ", ") => string.Join(separator, items) is { Length: > 0 } text ? text : "none";
        static string Fields(ModInfo info) => List(new[] { ("category", info.Category), ("alias", info.Alias), ("notes", info.Notes) }
            .Where(f => f.Item2 != null).Select(f => $"{f.Item1} {f.Item2}"));
        // What came out, in words: each profile with the mods it turns off, the active one, categories, each mod's fields.
        static string Summary(StoreData? data) => data == null ? "refused" : string.Join(" / ",
            "profiles " + List(data.Profiles.Select(p => $"{p.Name} (off: {List(p.Disabled?.Select(r => r.Id) ?? [])})"), "; "),
            "active " + (data.ActiveProfile ?? "none"),
            "categories " + List(data.Categories.Select(c => c.Collapsed ? $"{c.Name} (collapsed)" : c.Name), "; "),
            "mods " + List(data.Mods.OrderBy(m => m.Key, StringComparer.Ordinal).Select(m => $"{m.Key} ({Fields(m.Value)})"), "; "));

        var json5 = Summary(Import("""
            // Better Mod Menu's settings, edited by hand
            {
              "Profiles": [
                { "Name": "Co-op", "DisabledMods": ["ModA", "ModB",], },
                { "Name": "Solo", "DisabledMods": [], },
              ],
              "CurrentProfileIndex": 1,
              "CustomGroups": ["Content", "Visual",], // a trailing comma here too
              "ModGroups": { "ModA": "Content", },
              "CollapsedGroups": ["Visual"],
              "ModAnnotations": { "ModB": { "Alias": "Bee", "Notes": "", }, },
            }
            """));
        Check("Better Mod Menu import: line comments and trailing commas (.json5)", json5 == "profiles Co-op (off: ModA, ModB); Solo (off: none) / active Solo"
            + " / categories Content; Visual (collapsed) / mods ModA (category Content); ModB (alias Bee)", json5);
        var jsonc = Summary(Import("""
            /* Better Mod Menu's settings */
            {
              "Profiles": [ { "Name": "Main", "DisabledMods": [ "ModC" ] } ], /* one profile */
              "CurrentProfileIndex": 0,
              "CustomGroups": [ "Tools" ],
              "ModGroups": { "ModC": "Tools" },
              "ModAnnotations": { "ModC": { "Alias": "", "Notes": "Check after updates" } }
            }
            """));
        Check("Better Mod Menu import: block comments (.jsonc)", jsonc == "profiles Main (off: ModC) / active Main / categories Tools"
            + " / mods ModC (category Tools, notes Check after updates)", jsonc);
        var legacy = Summary(Import("""
            [
              { "Name": "Old favourites", "DisabledMods": ["ModD"] },
              { "DisabledMods": [] }
            ]
            """));
        Check("Better Mod Menu import: its old format (a bare list of profiles) gives those profiles, no categories", legacy
            == "profiles Old favourites (off: ModD); Profile 2 (off: none) / active none / categories none / mods none", legacy);
        // Better Mod Menu lets two profiles share a name; ours go by name, so the later one is numbered as a copy is, and
        // the current index still finds it.
        var twins = Summary(Import("""
            {
              "Profiles": [
                { "Name": "Profile 3", "DisabledMods": ["ModA"] },
                { "Name": "Profile 3", "DisabledMods": ["ModB"] },
                { "Name": "Modded (1)", "DisabledMods": [] },
                { "Name": "Modded (1)", "DisabledMods": [] }
              ],
              "CurrentProfileIndex": 1,
              "CustomGroups": ["Tools", "Tools"]
            }
            """));
        Check("Better Mod Menu import: two profiles of one name stay two (the later numbered as a copy is), the current one still current; a group named twice once",
            twins == "profiles Profile 3 (off: ModA); Profile 3 (1) (off: ModB); Modded (1) (off: none); Modded (2) (off: none) / active Profile 3 (1)"
            + " / categories Tools / mods none", twins);
        var broken = new[] { """{ "Profiles": [ { "Name": "Cut off" """, "\"Profiles\"", "{ Profiles: [] }" }.Select(text => Summary(Import(text))).ToList();
        Check("Better Mod Menu import: a broken file is refused (cut off, not an object or list, unquoted names)", broken.All(s => s == "refused"),
            string.Join(" | ", broken));

        // Which file: beside its mod files before the account's folder, even for a less preferred name; then in a
        // folder .json5, .jsonc, .json.
        var root = Path.Combine(outDir, "bmm-import");
        var (portable, account) = (Path.Combine(root, "portable"), Path.Combine(root, "account"));
        static string In(string folder, string extension) => Path.Combine(folder, "mod_profiles" + extension);
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
        Directory.CreateDirectory(portable);
        Directory.CreateDirectory(account);
        foreach (var file in new[] { In(portable, ".jsonc"), In(portable, ".json"), In(account, ".json"), In(account, ".json5") })
            File.WriteAllText(file, "[]");
        var found = new List<string?> { BetterModMenuImport.FindFile([portable, account]) };
        File.Delete(In(portable, ".jsonc"));
        found.Add(BetterModMenuImport.FindFile([portable, account]));
        File.Delete(In(portable, ".json"));
        found.Add(BetterModMenuImport.FindFile([portable, account]));
        Directory.Delete(root, recursive: true);
        found.Add(BetterModMenuImport.FindFile([portable, account]));
        Check("Better Mod Menu import: the file it would load (its mod folder first, then .json5, .jsonc, .json)",
            found.SequenceEqual([In(portable, ".jsonc"), In(portable, ".json"), In(account, ".json5"), null]), string.Join(" | ", found));
    }

    // The card a [url] link in a text brings up (Hover.WatchLinks), hovered with the mouse on the text, then let go: its
    // words, or "" for none.
    static async Task<string> LinkCard(RichTextLabel text, string link)
    {
        Hover.TestMouse = () => text.GetGlobalRect().GetCenter();
        try
        {
            text.EmitSignal(RichTextLabel.SignalName.MetaHoverStarted, link);
            await WaitReal(text, Hover.Delay + 0.3); // the link card's delay
            var card = text.GetTree().Root.FindChild(LaunchGuard.MoreCardName, true, false)?.FindChildren("*", "RichTextLabel", true, false)
                .OfType<RichTextLabel>().FirstOrDefault()?.GetParsedText() ?? "";
            text.EmitSignal(RichTextLabel.SignalName.MetaHoverEnded, link);
            return card;
        }
        finally
        {
            Hover.TestMouse = null;
            Hover.Hide();
        }
    }
}
