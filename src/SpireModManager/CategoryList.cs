using Godot;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;

namespace SpireModManager;

// Groups the vanilla Modding screen's rows under collapsible category headers. The headers go into the
// screen's own row container (a VBoxContainer inside its scroll area), so scrolling, selection, the info
// panel and the tickboxes stay the game's. Vanilla view removes the headers and restores the order.
static class CategoryList
{
    const string HeaderScenePath = $"res://{ModEntry.ModId}/scenes/category_header.tscn";
    const string HeaderGroup = "SpireModManagerCategoryHeader";
    const float DragThreshold = 12; // a press that moves further than this is a scroll, not a click
    const string RowMenuMeta = "spire_mod_manager_menu";
    const string CategoryMeta = "spire_mod_manager_category"; // on headers: the category name, "" for Uncategorized
    public const string FilterMeta = "spire_mod_manager_filter"; // on the row container: the search text
    const string GameGapMeta = "spire_mod_manager_game_gap", GameSidesMeta = "spire_mod_manager_game_sides";
    static readonly Dictionary<string, float> HeaderWidths = [];

    public static void Attach(NModdingScreen screen)
    {
        var content = screen.GetNode<Control>("%ModsScrollContainer/Mask/Content");

        // Two view switches along the bottom (Layout places them): Vanilla and Load Order. Each turns the
        // other off; pressing the lit one again goes back to categories, and its label says so.
        Control? vanilla = null, loadOrder = null;
        void Switch(bool toVanilla)
        {
            var data = Store.Data;
            (data.VanillaView, data.LoadOrderView) = toVanilla ? (!data.VanillaView, false) : (false, !data.LoadOrderView);
            Selection.Clear(content); // a picked set belongs to the view it was picked in
            Store.Save();
            Label(vanilla, data.VanillaView ? "SPIRE_MOD_MANAGER.CATEGORY_VIEW" : "SPIRE_MOD_MANAGER.VANILLA_VIEW");
            Label(loadOrder, data.LoadOrderView ? "SPIRE_MOD_MANAGER.BACK" : "SPIRE_MOD_MANAGER.LOAD_ORDER");
            Apply(content);
            Layout.Apply(screen);
            RestoreSelection(screen, content);
        }
        // Load Order is a side trip: the page always opens on the main list (Vanilla View, a preference, stays).
        if (Store.Data.LoadOrderView)
        {
            Store.Data.LoadOrderView = false;
            Store.Save();
        }
        screen.VisibilityChanged += () =>
        {
            if (screen.IsVisibleInTree() && Store.Data.LoadOrderView)
                Switch(false);
        };
        vanilla = ModEntry.AddButton(screen, "SPIRE_MOD_MANAGER.VANILLA_VIEW", new Vector2(200, 70), () => Switch(true));
        vanilla.Name = "SpireModManagerViewToggle";
        loadOrder = ModEntry.AddButton(screen, "SPIRE_MOD_MANAGER.LOAD_ORDER", new Vector2(200, 70), () => Switch(false));
        loadOrder.Name = "SpireModManagerLoadOrderToggle";
        // Labels for the saved view.
        Label(vanilla, Store.Data.VanillaView ? "SPIRE_MOD_MANAGER.CATEGORY_VIEW" : "SPIRE_MOD_MANAGER.VANILLA_VIEW");
        Label(loadOrder, Store.Data.LoadOrderView ? "SPIRE_MOD_MANAGER.BACK" : "SPIRE_MOD_MANAGER.LOAD_ORDER");

        Apply(content);
    }

    static void Label(Control? button, string key)
    {
        if (button != null)
            ModEntry.Relabel(button, key);
    }

    // The mod selected on the Modding screen (kept by a patch on OnRowSelected), so a view switch can keep it.
    public static Mod? Selected { get; set; }

    public static NModdingScreen? ScreenOf(Node node)
    {
        for (var n = node.GetParent(); n != null; n = n.GetParent())
            if (n is NModdingScreen screen)
                return screen;
        return null;
    }

    // Selects a row the way a click does: highlight and info panel.
    public static void Select(Control content, NModMenuRow row)
    {
        if (ScreenOf(content) is { } screen)
            Select(screen, row);
    }

    // Another mod's postfix throwing here (Better Mod Menu 1.1.1 does on the 0.111 beta) mustn't stop ours: the
    // game's part and our patch have run by then. Logged once.
    static bool _selectFailed;

    public static void Select(NModdingScreen screen, NModMenuRow row)
    {
        try
        {
            screen.OnRowSelected(row);
        }
        catch (Exception e)
        {
            if (!_selectFailed)
                Log.Warn($"[{ModEntry.ModId}] Another mod failed while a row was selected (logged once): {e.Message}");
            _selectFailed = true;
        }
    }

    // After a view switch: the same mod stays selected, its category opens if it was collapsed, and the
    // list scrolls to it. Nothing selected: the info panel empties (it was filled for the old view).
    static void RestoreSelection(NModdingScreen screen, Control content)
    {
        if (content.GetChildren().OfType<NModMenuRow>().FirstOrDefault(r => r.Mod == Selected && Selected != null) is not { } row)
        {
            screen.GetNode<NModInfoContainer>("%ModInfoContainer").Clear();
            return;
        }
        Expand(content, row);
        Select(screen, row);
        ScrollSoon(content, row, flash: false);
    }

    // A row hidden in a collapsed category (main list): the category opens.
    static void Expand(Control content, NModMenuRow row)
    {
        if (Store.Data.VanillaView || Store.Data.LoadOrderView || row.Visible)
            return;
        if (Store.InfoFor(row.Mod?.manifest?.id)?.Category is { } name && Categories.Find(name) is { } category)
            category.Collapsed = false;
        else
            Store.Data.UncategorizedCollapsed = false;
        Store.Save();
        Apply(content);
    }

    // Scrolls a mod's row into view (its category opens if collapsed); flash: a short gold glow to find it
    // by; focus: the row takes the "selected" look (as under the mouse or the keyboard) without becoming the
    // info panel's mod. The info panel doesn't change.
    public static void Reveal(Control content, string modId, bool flash, bool focus = false)
    {
        if (content.GetChildren().OfType<NModMenuRow>().FirstOrDefault(r => r.Mod?.manifest?.id == modId) is not { } row)
            return;
        Expand(content, row);
        ScrollSoon(content, row, flash, focus);
    }

    // A mod's row in the "selected" look while its name is hovered elsewhere (the info panel, the changes
    // list); only if it's in view: nothing scrolls. Off again: the look it had.
    const string LitMeta = "spire_mod_manager_lit";

    public static void Highlight(Control content, string modId, bool on)
    {
        if (content.GetChildren().OfType<NModMenuRow>().FirstOrDefault(r => r.Mod?.manifest?.id == modId) is not { } row
            || row.GetNodeOrNull<CanvasItem>("SelectionHighlight") is not { } look)
            return;
        if (!on)
        {
            if (row.HasMeta(LitMeta))
            {
                row.RemoveMeta(LitMeta);
                RowTitles.Look(row, RowState.Selected(row), RowState.Focused(row)); // its own state again
            }
            return;
        }
        var view = ScrollContainer(content).GetGlobalRect();
        if (!row.IsVisibleInTree() || !view.Intersects(row.GetGlobalRect()) || row.HasMeta(LitMeta) || look.Modulate.A > 0.1f)
            return; // not in view, lit already, or showing a look of its own (selected, under the mouse)
        row.SetMeta(LitMeta, true);
        RowTitles.Look(row, RowState.Selected(row), focused: true);
    }

    // Mod names (links "mod:<id>") in a text: hovering one lights its row and, after Hover's delay, shows the
    // mod's card; clicking scrolls to it with the selected look.
    public static void WatchNames(RichTextLabel label, Func<Control?> content)
    {
        label.MetaHoverStarted += meta =>
        {
            if (meta.AsString() is { } target && target.StartsWith("mod:") && content() is { } list)
                Highlight(list, target[4..], true);
        };
        label.MetaHoverEnded += meta =>
        {
            if (meta.AsString() is { } target && target.StartsWith("mod:") && content() is { } list)
                Highlight(list, target[4..], false);
        };
        Hover.WatchLinks(label, "SpireModManagerNotesCard",
            meta => meta.StartsWith("mod:") && Dependencies.Find(meta[4..]) is { } mod ? ModDetails.Card(mod) : null,
            full: meta => meta.StartsWith("mod:") && Dependencies.Find(meta[4..]) is { } mod ? ModDetails.Card(mod, full: true) : null);
    }

    // Once positions settle after this frame's layout.
    static void ScrollSoon(Control content, Control row, bool flash, bool focus = false)
    {
        content.GetTree().CreateTimer(0.05).Timeout += () =>
        {
            if (!GodotObject.IsInstanceValid(row))
                return;
            ScrollTo(content, row);
            if (flash)
                Flash(row);
            if (focus)
                row.GrabFocus();
        };
    }

    static void Flash(Control row)
    {
        var glow = new ColorRect { Color = new Color(0.937f, 0.784f, 0.318f, 0.35f), MouseFilter = Control.MouseFilterEnum.Ignore };
        row.AddChild(glow);
        glow.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        var tween = glow.CreateTween();
        tween.TweenProperty(glow, "color:a", 0f, 1.4).SetDelay(0.3);
        tween.TweenCallback(Callable.From(glow.QueueFree));
    }

    // The game's scroll container eases its content towards a target position; these move that target.
    static Control ScrollContainer(Control content) => content.GetParent().GetParent<Control>();

    // Kept within the list's ends: past them the container springs back, which looks like a bounce.
    public static void ScrollBy(Control content, float pixels)
    {
        var scroll = ScrollContainer(content);
        var bottom = Math.Min(0, content.GetParent<Control>().Size.Y - content.Size.Y);
        scroll.Set("_targetDragPosY", Math.Clamp((float)scroll.Get("_targetDragPosY") + pixels, bottom, 0));
    }

    // At once: the game's container stops scrolling once the rows fit (DisableScrollingIfContentFits), so a
    // target alone would never be reached once a search makes the list short.
    public static void ScrollToTop(Control content)
    {
        if (ScrollContainer(content) is NScrollableContainer scroll)
            scroll.InstantlyScrollToTop();
    }

    // The row a third of the way down the visible list.
    public static void ScrollTo(Control content, Control row)
    {
        var scroll = ScrollContainer(content);
        var top = Math.Min(0, -row.Position.Y + scroll.Size.Y / 3);
        var bottom = Math.Min(0, scroll.Size.Y - content.Size.Y);
        scroll.Set("_targetDragPosY", Math.Max(top, bottom));
    }

    // After a change that may need a relaunch: redraw, and let the screen recheck its pending warning.
    public static void Redraw(Control content)
    {
        Apply(content);
        for (var node = content.GetParent(); node != null; node = node.GetParent())
            if (node is NModdingScreen screen)
            {
                screen.OnModEnabledOrDisabled();
                return;
            }
    }

    public static void Apply(Control content)
    {
        using var lookups = Lookups.Begin(); // every row asks the same questions (is it on, who needs it): answered once
        // The row container only grows by itself; shrink its height to fit once this redraw settles
        // (collapsing a category or filtering must shorten the scroll range too). Only the height: its width
        // comes from its anchors, and its minimum width is 0.
        Callable.From(() =>
        {
            content.OffsetBottom = content.OffsetTop;
            // Once it has its new size (one more step): short enough to fit, the game's container has stopped
            // scrolling wherever it was, maybe with every row above the view, so back to the top (a search,
            // Collapse all, a collapsed category). Moving it keeps its size, so shrink it again after.
            Callable.From(() =>
            {
                if (!GodotObject.IsInstanceValid(content) || content.Size.Y > ScrollContainer(content).Size.Y)
                    return;
                ScrollToTop(content);
                content.OffsetBottom = content.OffsetTop;
            }).CallDeferred();
        }).CallDeferred();
        // Our views: taller rows, less space between them (the same pitch, more room inside a row), and the rows a
        // little in from the list's sides, so their wider highlight isn't cut off by its edges. Vanilla View: the
        // game's own values back.
        if (!content.HasMeta(GameGapMeta))
        {
            content.SetMeta(GameGapMeta, content.GetThemeConstant("separation"));
            content.SetMeta(GameSidesMeta, new Vector2(content.OffsetLeft, content.OffsetRight));
        }
        var sides = (Vector2)content.GetMeta(GameSidesMeta);
        content.AddThemeConstantOverride("separation", Store.Data.VanillaView ? (int)content.GetMeta(GameGapMeta) : RowTitles.RowGap);
        (content.OffsetLeft, content.OffsetRight) = Store.Data.VanillaView ? (sides.X, sides.Y) : (sides.X - RowTitles.LookInset.X, sides.Y - RowTitles.LookInset.Z);
        foreach (var header in content.GetChildren().Where(c => c.IsInGroup(HeaderGroup)))
        {
            content.RemoveChild(header);
            header.QueueFree();
        }
        // Better Mod Menu's group headers too, on our views (it makes them again on its page, Vanilla View), and its
        // row controls hidden (rows added since the page opened have them showing).
        var screen = ScreenOf(content);
        if (!Store.Data.VanillaView && screen != null)
        {
            Bmm.RemoveGroups(content);
            Bmm.Show(screen, false);
        }

        // Vanilla order is the game's mod order, which is how the screen first added the rows.
        var order = ModManager.Mods.Select((mod, i) => (mod, i)).ToDictionary(p => p.mod, p => p.i);
        var rows = content.GetChildren().OfType<NModMenuRow>()
            .OrderBy(r => r.Mod is { } mod && order.TryGetValue(mod, out var i) ? i : int.MaxValue)
            .ToList();
        RowTitles.Apply(rows, Store.Data.VanillaView);

        // Right-click a row for its menu (category, alias, tags, notes; the bulk menu for picked mods; on/off and
        // moves in Load Order). Rows outlive Apply, so connect once.
        foreach (var row in rows.Where(r => !r.HasMeta(RowMenuMeta)))
        {
            row.SetMeta(RowMenuMeta, true);
            row.GuiInput += e =>
            {
                // Main list picking (Load Order handles its own, with dragging): Ctrl or Shift picks without
                // opening the mod in the panel; a plain click lets go of the picked set.
                if (!Store.Data.LoadOrderView && !Store.Data.VanillaView && e is InputEventMouseButton { ButtonIndex: MouseButton.Left } click)
                {
                    if (click.CtrlPressed || click.ShiftPressed || click.MetaPressed)
                    {
                        if (click.Pressed)
                            Selection.Pick(content, row, toggle: !click.ShiftPressed, range: click.ShiftPressed);
                        row.AcceptEvent();
                    }
                    else if (!click.Pressed)
                        Selection.Clear(content);
                    return;
                }
                if (e is not InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: false })
                    return;
                if (Store.Data.LoadOrderView)
                    LoadOrder.ShowRowMenu(content, row, () => Redraw(content));
                else if (Selection.Several && Selection.Contains(row.Mod))
                    ShowBulkMenu(content);
                else
                {
                    Selection.Clear(content); // a right-click elsewhere is about that one mod
                    ShowRowMenu(content, row);
                }
            };
        }

        // Search: rows that don't match hide; while searching, categories open to show their matches.
        var filter = content.HasMeta(FilterMeta) ? (string)content.GetMeta(FilterMeta) : "";
        var filtering = filter.Length > 0;
        bool Matches(NModMenuRow row) => !filtering || Search.Matches(row.Mod, filter);

        LoadOrder.UpdateToolbar(content, rows, () => Redraw(content));
        if (Store.Data.LoadOrderView)
        {
            void RedrawAll() => Redraw(content);
            LoadOrder.Apply(content, rows, Matches, RedrawAll);
            RowStrip.Apply(rows, StripActions(content), EdgeAction(content));
            return;
        }
        LoadOrder.Undecorate(rows);
        LoadOrder.Reset(content); // no Load Order lines or dimming outside it

        if (Store.Data.VanillaView)
            RowStrip.Remove(rows);
        else
            RowStrip.Apply(rows, StripActions(content), EdgeAction(content));

        if (Store.Data.VanillaView)
        {
            for (var i = 0; i < rows.Count; i++)
            {
                content.MoveChild(rows[i], i);
                rows[i].Visible = Bmm.Present || Matches(rows[i]); // Better Mod Menu's page: its search and groups decide
            }
            if (screen != null)
                Bmm.Refresh(screen);
            return;
        }

        var categories = Store.Data.Categories;
        var known = categories.Select(c => c.Name).ToHashSet();
        string? CategoryOf(NModMenuRow row) =>
            Store.InfoFor(row.Mod?.manifest?.id)?.Category is { } name && known.Contains(name) ? name : null;

        var index = 0;
        void Add(Control node)
        {
            node.AddToGroup(HeaderGroup);
            content.AddChild(node);
            content.MoveChild(node, index++);
        }

        void AddSection(string title, List<NModMenuRow> members, bool collapsed, Action<bool> setCollapsed, Category? category = null)
        {
            if (filtering && !members.Any(Matches))
            {
                foreach (var row in members)
                {
                    content.MoveChild(row, index++);
                    row.Visible = false;
                }
                return;
            }
            var muted = category == null;
            var header = ModEntry.Load<PackedScene>(HeaderScenePath)?.Instantiate<Control>() ?? PlainHeader();
            Add(header);
            header.Name = category == null ? "Uncategorized" : $"Category {category.Name}";
            header.SetMeta(CategoryMeta, category?.Name ?? "");
            var titleLabel = header.GetNode<Label>("Title");
            titleLabel.Text = title;
            // Our (smaller-text) views: a shorter header.
            header.CustomMinimumSize = new Vector2(0, 36);
            titleLabel.AddThemeFontSizeOverride("font_size", 18);
            header.GetNode<Label>("Count").AddThemeFontSizeOverride("font_size", 15);
            // The scene's Kreon, or the game's font for its language (ModEntry.Font).
            titleLabel.AddThemeFontOverride("font", ModEntry.Font("res://themes/kreon_bold_shared.tres"));
            header.GetNode<Label>("Count").AddThemeFontOverride("font", ModEntry.Font("res://themes/kreon_regular_shared.tres"));
            if (muted)
            {
                // Not a category the player made: cream instead of gold, fainter background.
                titleLabel.AddThemeColorOverride("font_color", new Color(1, 0.964706f, 0.886275f, 0.7f));
                header.GetNode<ColorRect>("Background").Color = new Color(0.0756f, 0.12084f, 0.18f, 0.3f);
            }
            // The count right after the name.
            var count = header.GetNode<Label>("Count");
            count.Text = (filtering ? members.Count(Matches) : members.Count).ToString();
            count.HorizontalAlignment = HorizontalAlignment.Left;
            count.AnchorLeft = 0;
            count.OffsetLeft = titleLabel.OffsetLeft + titleLabel.GetThemeFont("font").GetStringSize(title, fontSize: 18).X + 10;
            // The header menu's actions inline, right-aligned: the category's own (not for Uncategorized), then
            // Enable all / Disable all at the far right, over the rows' tickboxes.
            var shown = members.Where(Matches).ToList();
            var actions = Menus.InlineButtons(category == null ? [] : HeaderActions(content, category, compact: true), "Actions");
            actions.AddThemeConstantOverride("separation", 4);
            if (category != null)
                actions.AddChild(new Control { CustomMinimumSize = new Vector2(10, 0), MouseFilter = Control.MouseFilterEnum.Ignore });
            foreach (var item in OnOffItems(content, shown))
            {
                var button = Menus.SmallButton(item.Label, item.OnSelect ?? (() => { }), item.Disabled);
                button.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
                actions.AddChild(button);
            }
            actions.Alignment = BoxContainer.AlignmentMode.End;
            header.AddChild(actions);
            actions.AnchorLeft = actions.AnchorRight = 1;
            actions.AnchorBottom = 1;
            actions.GrowHorizontal = Control.GrowDirection.Begin;
            (actions.OffsetLeft, actions.OffsetRight, actions.OffsetTop, actions.OffsetBottom) = (-16, -16, 5, -5);
            // Measured once per set of labels in the buttons' font (it changes with the game's language): shaping text is slow.
            var widthKey = $"{ModEntry.Font("res://themes/kreon_regular_shared.tres").GetInstanceId()}\n"
                + string.Join("\n", actions.GetChildren().Select(c => c is Button b ? b.Text : "|"));
            if (!HeaderWidths.TryGetValue(widthKey, out var actionsWidth))
                HeaderWidths[widthKey] = actionsWidth = actions.GetCombinedMinimumSize().X;
            titleLabel.OffsetRight = -16 - actionsWidth - 30;
            foreach (var row in members)
                content.MoveChild(row, index++);

            void Show(bool isCollapsed)
            {
                var open = filtering || !isCollapsed;
                header.GetNode<Control>("Arrow").RotationDegrees = open ? 90 : 0;
                foreach (var row in members)
                    row.Visible = open && Matches(row);
            }
            Show(collapsed);

            var background = header.GetNode<ColorRect>("Background");
            var baseColor = background.Color;
            header.MouseEntered += () => background.Color = baseColor.Lightened(0.15f);
            header.MouseExited += () => background.Color = baseColor;

            var pressedAt = Vector2.Zero;
            header.GuiInput += e =>
            {
                if (e is InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: false })
                    ShowHeaderMenu(content, category, members.Where(Matches).ToList());
                if (e is not InputEventMouseButton { ButtonIndex: MouseButton.Left } click)
                    return;
                if (click.Pressed)
                    pressedAt = click.GlobalPosition;
                else if (click.GlobalPosition.DistanceTo(pressedAt) < DragThreshold)
                {
                    collapsed = !collapsed;
                    Show(collapsed);
                    setCollapsed(collapsed);
                    Store.Save();
                }
            };
        }

        foreach (var category in categories)
            AddSection(category.Name, rows.Where(r => CategoryOf(r) == category.Name).ToList(),
                category.Collapsed, c => category.Collapsed = c, category);

        var uncategorized = rows.Where(r => CategoryOf(r) == null).ToList();
        if (uncategorized.Count == 0)
            return;
        if (categories.Count > 0 && !filtering)
            Add(Divider());
        AddSection(Menus.Text("SPIRE_MOD_MANAGER.UNCATEGORIZED"), uncategorized,
            Store.Data.UncategorizedCollapsed, c => Store.Data.UncategorizedCollapsed = c);
    }

    // The small buttons at the right of each row's strip: moves in Load Order; otherwise its category, opening
    // its menu (the pencil past the tickbox is EdgeAction).
    static Func<NModMenuRow, IEnumerable<MenuItem>> StripActions(Control content)
    {
        // The rows in their places, read once for all the strips (by the first: Load Order has placed them by then),
        // not by every row again.
        List<NModMenuRow>? placed = null;
        return Store.Data.LoadOrderView
            ? row => LoadOrder.Actions(content, row, () => Redraw(content), compact: true, placed ??= content.GetChildren().OfType<NModMenuRow>().ToList())
            : row => [new MenuItem(CategoryName(row.Mod?.manifest?.id), () => ShowRowMenu(content, row), Tip: Menus.Text("SPIRE_MOD_MANAGER.CATEGORY_TIP"))];
    }

    // The main list's pencil, past the tickbox at the row's far right: the mod's menu. None in Load Order.
    static Func<NModMenuRow, MenuItem>? EdgeAction(Control content) => Store.Data.LoadOrderView
        ? null
        : row => new MenuItem("✎", () => ShowRowMenu(content, row), Tip: Menus.Text("SPIRE_MOD_MANAGER.EDIT_TIP"), Flat: true);

    // The mod's category, or "Uncategorized".
    public static string CategoryName(string? modId) =>
        Store.InfoFor(modId)?.Category is { } name && Categories.Find(name) != null ? name : Menus.Text("SPIRE_MOD_MANAGER.UNCATEGORIZED");

    // After a tickbox change: the rows' dependency notes ("(off)", "Required for") follow at once, without a
    // full redraw. (Their title colours: RowTitles.MarkChanged.)
    public static void RefreshStrips(Control content)
    {
        using var lookups = Lookups.Begin();
        var rows = content.GetChildren().OfType<NModMenuRow>().ToList();
        LoadOrder.UpdateToolbar(content, rows, () => Redraw(content)); // the banner and the "!" follow on/off too
        if (Store.Data.VanillaView)
            return;
        RowStrip.Apply(rows, StripActions(content), EdgeAction(content));
    }

    // A mod's right-click menu: its category on the left (pick one, or a new one, named in place), its alias,
    // tags and notes on the right, edited right there.
    public static Node? ShowRowMenu(Control content, NModMenuRow row)
    {
        if (Store.Data.VanillaView || row.Mod?.manifest?.id is not { } id)
            return null;
        return MenuPanel.Show(content, () => CategoryItems(content, id), side: _ => ModField.Column(id, _ => Edited(content)));
    }

    // Two or more picked mods edited together: their category (marked when they share one), on or off; and on
    // the right their tags and notes, under "Editing N selected mods" (where one mod's alias would be).
    public static Node? ShowBulkMenu(Control content)
    {
        var ids = Selection.Mods.Select(m => m.manifest?.id).OfType<string>().ToList();
        if (Store.Data.VanillaView || ids.Count == 0)
            return null;
        return MenuPanel.Show(content, () => BulkItems(content, ids), side: _ => ModField.BulkColumn(ids, _ => Edited(content)));
    }

    static IReadOnlyList<MenuItem> BulkItems(Control content, List<string> ids)
    {
        var shared = ids.Select(id => Store.InfoFor(id)?.Category is { } name && Categories.Find(name) != null ? name : null).Distinct().ToList();
        var common = shared.Count == 1 ? shared[0] : "\n"; // not all the same: nothing marked
        var items = new List<MenuItem> { MenuItem.Heading(Menus.Text("SPIRE_MOD_MANAGER.CATEGORY")) };
        foreach (var category in Store.Data.Categories)
            items.Add(new MenuItem(category.Name, () => Move(category.Name), Checked: category.Name == common, Radio: true));
        items.Add(new MenuItem(Menus.Text("SPIRE_MOD_MANAGER.UNCATEGORIZED"), () => Move(null), Checked: common == null, Radio: true));
        items.Add(MenuItem.Heading(""));
        items.Add(new MenuItem(Menus.Text("SPIRE_MOD_MANAGER.NEW_CATEGORY"), () =>
        {
            Category? made = null;
            Store.Batch(() =>
            {
                made = Categories.Add();
                foreach (var id in ids)
                    Categories.Assign(id, made.Name);
            });
            Edited(content);
            if (made != null && BeginRename(content, made)?.GetParent() is Control header)
                ScrollSoon(content, header, flash: false);
        }));
        items.Add(MenuItem.Heading(""));
        items.AddRange(OnOffItems(content, content.GetChildren().OfType<NModMenuRow>().Where(r => ids.Contains(r.Mod?.manifest?.id ?? ""))));
        return items;

        void Move(string? category)
        {
            Store.Batch(() =>
            {
                foreach (var id in ids)
                    Categories.Assign(id, category);
            });
            Edited(content);
        }
    }

    // After one of a mod's details changed: the rows and the info panel show it.
    public static void Edited(Control content)
    {
        Apply(content);
        if (ScreenOf(content) is { } screen)
            ModDetails.Refresh(screen);
    }

    // The categories to put a mod in (its current one marked), Uncategorized, and a new one.
    public static IReadOnlyList<MenuItem> CategoryItems(Control content, string id)
    {
        var current = Store.InfoFor(id)?.Category is { } name && Categories.Find(name) != null ? name : null;
        var items = new List<MenuItem> { MenuItem.Heading(Menus.Text("SPIRE_MOD_MANAGER.CATEGORY")) };
        foreach (var category in Store.Data.Categories)
            items.Add(new MenuItem(category.Name, () => Move(category.Name), Checked: category.Name == current, Radio: true));
        items.Add(new MenuItem(Menus.Text("SPIRE_MOD_MANAGER.UNCATEGORIZED"), () => Move(null), Checked: current == null, Radio: true));
        items.Add(MenuItem.Heading(""));
        items.Add(new MenuItem(Menus.Text("SPIRE_MOD_MANAGER.NEW_CATEGORY"), () =>
        {
            var category = Categories.Add();
            Move(category.Name);
            // Its name box, at the bottom of the categories: scrolled into view.
            if (BeginRename(content, category)?.GetParent() is Control header)
                ScrollSoon(content, header, flash: false);
        }));
        return items;

        void Move(string? category)
        {
            Categories.Assign(id, category);
            Edited(content);
        }
    }

    // Enable all / Disable all / Select all (the mods shown under it: all of them, or the search's matches), then,
    // for the player's own categories, its actions. Uncategorized has only the first three.
    public static Node? ShowHeaderMenu(Control content, Category? category, List<NModMenuRow> members)
    {
        var items = OnOffItems(content, members);
        items.Add(new MenuItem(Menus.Text("SPIRE_MOD_MANAGER.SELECT_ALL"), () => Selection.Add(content, members), Disabled: members.Count == 0));
        if (category != null)
            items.AddRange([MenuItem.Heading(""), .. HeaderActions(content, category)]);
        return Menus.Show(content, items);
    }

    // Enable all / Disable all for some rows (a category's, or the page's), as one undo step; each greyed out
    // when there's nothing for it to do. This mod stays on.
    public static List<MenuItem> OnOffItems(Control content, IEnumerable<NModMenuRow> members)
    {
        var mods = members.Select(r => r.Mod).OfType<Mod>().ToList();
        void Set(bool on)
        {
            if (ScreenOf(content) is not { } screen)
                return;
            Dependencies.Set(mods, on, screen);
            Redraw(content);
        }
        return
        [
            new(Menus.Text("SPIRE_MOD_MANAGER.ENABLE_ALL"), () => Set(true), Disabled: mods.All(Dependencies.IsOn)),
            new(Menus.Text("SPIRE_MOD_MANAGER.DISABLE_ALL"), () => Set(false), Disabled: mods.All(m => !Dependencies.IsOn(m) || m.manifest?.id == ModEntry.ModId)),
        ];
    }

    // Rename, up, down, delete: the header's right-click menu, and its inline buttons (compact labels).
    static List<MenuItem> HeaderActions(Control content, Category category, bool compact = false)
    {
        var list = Store.Data.Categories;
        var index = list.IndexOf(category);
        var suffix = compact ? "_SHORT" : "";
        return [
            new MenuItem(Menus.Text("SPIRE_MOD_MANAGER.RENAME"), () => BeginRename(content, category)),
            new MenuItem(Menus.Text("SPIRE_MOD_MANAGER.MOVE_UP" + suffix), () => Edit(() => Categories.Move(category, -1)), Disabled: index == 0),
            new MenuItem(Menus.Text("SPIRE_MOD_MANAGER.MOVE_DOWN" + suffix), () => Edit(() => Categories.Move(category, 1)), Disabled: index == list.Count - 1),
            new MenuItem(Menus.Text("SPIRE_MOD_MANAGER.DELETE"), () => _ = Delete()),
        ];

        void Edit(Action change)
        {
            change();
            Apply(content);
        }

        // Asks first when the category still holds mods; they move to Uncategorized.
        async Task Delete()
        {
            var count = Categories.CountMods(category);
            if (count > 0)
            {
                var body = new LocString("settings_ui", count == 1 ? "SPIRE_MOD_MANAGER.DELETE_CATEGORY_BODY_ONE" : "SPIRE_MOD_MANAGER.DELETE_CATEGORY_BODY");
                body.Add("name", category.Name);
                body.Add("count", count);
                if (!await Menus.Confirm(new LocString("settings_ui", "SPIRE_MOD_MANAGER.DELETE_CATEGORY_TITLE"), body))
                    return;
            }
            Edit(() => Categories.Delete(category));
        }
    }

    // Swaps the header's title for a text box; Enter or clicking away renames, Escape cancels.
    public static LineEdit? BeginRename(Control content, Category category)
    {
        if (content.GetChildren().OfType<Control>().FirstOrDefault(c =>
                c.IsInGroup(HeaderGroup) && c.HasMeta(CategoryMeta) && (string)c.GetMeta(CategoryMeta) == category.Name)
            is not { } header)
            return null;
        var title = header.GetNode<Label>("Title");
        var edit = new LineEdit { Name = "RenameBox", Text = category.Name, Flat = true, SelectAllOnFocus = true, MaxLength = 40, ClearButtonEnabled = true };
        edit.AddThemeFontOverride("font", title.GetThemeFont("font"));
        edit.AddThemeFontSizeOverride("font_size", title.GetThemeFontSize("font_size"));
        edit.AddThemeColorOverride("font_color", title.GetThemeColor("font_color"));
        header.AddChild(edit);
        edit.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        edit.OffsetLeft = title.OffsetLeft;
        edit.OffsetRight = title.OffsetRight;
        edit.OffsetTop = 6;
        edit.OffsetBottom = -6;
        title.Visible = false;
        edit.GrabFocus();

        // An empty or taken name says so at the box's right end; Enter then does nothing, and clicking away
        // keeps the old name.
        var warning = new Label { Name = "NameWarning", HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore };
        warning.AddThemeFontOverride("font", ModEntry.Font("res://themes/kreon_regular_shared.tres"));
        warning.AddThemeFontSizeOverride("font_size", 15);
        warning.AddThemeColorOverride("font_color", new Color(1f, 0.5f, 0.44f));
        edit.AddChild(warning);
        warning.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        warning.OffsetRight = -34; // clear of the box's ✕
        string? Problem() => Categories.NameProblem(edit.Text, category);
        void Check() => warning.Text = Problem() ?? "";
        edit.TextChanged += _ => Check();

        var done = false;
        void Finish(bool commit)
        {
            if (done)
                return;
            done = true;
            if (commit && Problem() == null)
                Categories.Rename(category, edit.Text);
            Callable.From(() => Apply(content)).CallDeferred(); // not while the box is handling its own signal
        }
        edit.TextSubmitted += _ =>
        {
            if (Problem() == null)
                Finish(true);
        };
        edit.FocusExited += () => Finish(true);
        edit.GuiInput += e =>
        {
            if (e is InputEventKey { Keycode: Key.Escape, Pressed: true })
                Finish(false);
        };
        return edit;
    }

    // The header scene's parts made in code, for when the scene doesn't load (it uses the game's arrow and fonts):
    // the same names, places and colours, with a "▶" for the arrow.
    public static Control PlainHeader()
    {
        var header = new Control
        {
            CustomMinimumSize = new Vector2(0, 56), MouseFilter = Control.MouseFilterEnum.Pass, MouseDefaultCursorShape = Control.CursorShape.PointingHand,
        };
        var background = new ColorRect
        {
            Name = "Background", Color = new Color(0.0756f, 0.12084f, 0.18f, 0.6f), MouseFilter = Control.MouseFilterEnum.Ignore,
            AnchorRight = 1, AnchorBottom = 1,
        };
        header.AddChild(background);
        var arrow = new Label
        {
            Name = "Arrow", Text = "▶", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore, AnchorTop = 0.5f, AnchorBottom = 0.5f,
            OffsetLeft = 12, OffsetTop = -13, OffsetRight = 38, OffsetBottom = 13, PivotOffset = new Vector2(13, 13),
        };
        arrow.AddThemeColorOverride("font_color", new Color(0.937f, 0.784f, 0.318f));
        header.AddChild(arrow);
        var title = new Label
        {
            Name = "Title", VerticalAlignment = VerticalAlignment.Center, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            MouseFilter = Control.MouseFilterEnum.Ignore, AnchorRight = 1, AnchorBottom = 1, OffsetLeft = 52, OffsetRight = -90,
        };
        title.AddThemeFontOverride("font", ModEntry.Font("res://themes/kreon_bold_shared.tres"));
        title.AddThemeColorOverride("font_color", new Color(0.937f, 0.784f, 0.318f));
        header.AddChild(title);
        var count = new Label
        {
            Name = "Count", HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore, AnchorRight = 1, AnchorBottom = 1, OffsetRight = -16,
        };
        count.AddThemeFontOverride("font", ModEntry.Font("res://themes/kreon_regular_shared.tres"));
        count.AddThemeColorOverride("font_color", new Color(1, 0.964706f, 0.886275f, 0.6f));
        header.AddChild(count);
        return header;
    }

    // A thin gold line marking where the player's categories end and "Uncategorized" begins.
    static Control Divider()
    {
        var divider = new Control { CustomMinimumSize = new Vector2(0, 12), MouseFilter = Control.MouseFilterEnum.Pass };
        var line = new ColorRect { Color = new Color(0.937f, 0.784f, 0.318f, 0.5f), MouseFilter = Control.MouseFilterEnum.Ignore };
        divider.AddChild(line);
        line.SetAnchorsPreset(Control.LayoutPreset.HcenterWide);
        line.OffsetLeft = 24;
        line.OffsetRight = -24;
        line.OffsetTop = -1;
        line.OffsetBottom = 1;
        return divider;
    }
}
