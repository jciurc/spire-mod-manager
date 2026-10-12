using Godot;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;
using MegaCrit.Sts2.addons.mega_text;

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

    public static void Attach(NModdingScreen screen)
    {
        var content = screen.GetNode<Control>("%ModsScrollContainer/Mask/Content");

        // Two view switches along the bottom (Layout places them): Vanilla and Load Order. Each turns the
        // other off; pressing the lit one again goes back to categories, and its label says so.
        NButton? vanilla = null, loadOrder = null;
        void Switch(bool toVanilla)
        {
            var data = Store.Data;
            (data.VanillaView, data.LoadOrderView) = toVanilla ? (!data.VanillaView, false) : (false, !data.LoadOrderView);
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

    static void Label(NButton? button, string key) =>
        button?.GetNode<MegaLabel>("Visuals/Label").SetTextAutoSize(new LocString("settings_ui", key).GetFormattedText());

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
    public static void Select(Control content, NModMenuRow row) => ScreenOf(content)?.OnRowSelected(row);

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
        screen.OnRowSelected(row);
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
    // by. The info panel doesn't change.
    public static void Reveal(Control content, string modId, bool flash)
    {
        if (content.GetChildren().OfType<NModMenuRow>().FirstOrDefault(r => r.Mod?.manifest?.id == modId) is not { } row)
            return;
        Expand(content, row);
        ScrollSoon(content, row, flash);
    }

    // Once positions settle after this frame's layout.
    static void ScrollSoon(Control content, Control row, bool flash)
    {
        content.GetTree().CreateTimer(0.05).Timeout += () =>
        {
            if (!GodotObject.IsInstanceValid(row))
                return;
            ScrollTo(content, row);
            if (flash)
                Flash(row);
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

    public static void ScrollToTop(Control content) => ScrollContainer(content).Set("_targetDragPosY", 0f);

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
        // The row container only grows by itself; shrink its height to fit once this redraw settles
        // (collapsing a category or filtering must shorten the scroll range too). Only the height: its width
        // comes from its anchors, and its minimum width is 0.
        Callable.From(() => content.OffsetBottom = content.OffsetTop).CallDeferred();
        foreach (var header in content.GetChildren().Where(c => c.IsInGroup(HeaderGroup)))
        {
            content.RemoveChild(header);
            header.QueueFree();
        }

        // Vanilla order is the game's mod order, which is how the screen first added the rows.
        var order = ModManager.Mods.Select((mod, i) => (mod, i)).ToDictionary(p => p.mod, p => p.i);
        var rows = content.GetChildren().OfType<NModMenuRow>()
            .OrderBy(r => r.Mod is { } mod && order.TryGetValue(mod, out var i) ? i : int.MaxValue)
            .ToList();
        RowTitles.Apply(rows, Store.Data.VanillaView);

        // Right-click a row to pick its category. Rows outlive Apply, so connect once.
        foreach (var row in rows.Where(r => !r.HasMeta(RowMenuMeta)))
        {
            row.SetMeta(RowMenuMeta, true);
            row.GuiInput += e =>
            {
                if (e is not InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: false })
                    return;
                if (Store.Data.LoadOrderView)
                    LoadOrder.ShowRowMenu(content, row, () => Redraw(content));
                else
                    ShowRowMenu(content, row);
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
            RowStrip.Apply(rows, StripActions(content));
            return;
        }
        LoadOrder.Undecorate(rows);
        LoadOrder.Reset(content); // no Load Order lines, dimming or multi-selection outside it
        LoadOrder.ClearSelection(content);

        if (Store.Data.VanillaView)
            RowStrip.Remove(rows);
        else
            RowStrip.Apply(rows, StripActions(content));

        if (Store.Data.VanillaView)
        {
            for (var i = 0; i < rows.Count; i++)
            {
                content.MoveChild(rows[i], i);
                rows[i].Visible = Matches(rows[i]);
            }
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
            var header = ResourceLoader.Load<PackedScene>(HeaderScenePath).Instantiate<Control>();
            Add(header);
            header.Name = category == null ? "Uncategorized" : $"Category {category.Name}";
            header.SetMeta(CategoryMeta, category?.Name ?? "");
            var titleLabel = header.GetNode<Label>("Title");
            titleLabel.Text = title;
            // Our (smaller-text) views: a shorter header.
            header.CustomMinimumSize = new Vector2(0, 36);
            titleLabel.AddThemeFontSizeOverride("font_size", 18);
            header.GetNode<Label>("Count").AddThemeFontSizeOverride("font_size", 15);
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
            if (category != null)
            {
                // The header menu's actions inline, right-aligned.
                var actions = Menus.InlineButtons(HeaderActions(content, category, compact: true), "Actions");
                actions.Alignment = BoxContainer.AlignmentMode.End;
                header.AddChild(actions);
                actions.AnchorLeft = actions.AnchorRight = 1;
                actions.AnchorBottom = 1;
                (actions.OffsetLeft, actions.OffsetRight, actions.OffsetTop, actions.OffsetBottom) = (-304, -16, 5, -5);
                titleLabel.OffsetRight = -350;
            }
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
                if (category != null && e is InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: false })
                    ShowHeaderMenu(content, category);
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

    // The small buttons at the right of each row's strip: moves in Load Order; otherwise a pencil (the mod's
    // alias, tags and notes) and its category, both opening its menu.
    static Func<NModMenuRow, IEnumerable<MenuItem>> StripActions(Control content) => Store.Data.LoadOrderView
        ? row => LoadOrder.Actions(content, row, () => Redraw(content), compact: true)
        : row => [
            new MenuItem("✎", () => ShowRowMenu(content, row), Tip: Menus.Text("SPIRE_MOD_MANAGER.EDIT_TIP")),
            new MenuItem(CategoryName(row.Mod?.manifest?.id), () => ShowRowMenu(content, row), Tip: Menus.Text("SPIRE_MOD_MANAGER.CATEGORY_TIP")),
        ];

    // The mod's category, or "Uncategorized".
    public static string CategoryName(string? modId) =>
        Store.InfoFor(modId)?.Category is { } name && Categories.Find(name) != null ? name : Menus.Text("SPIRE_MOD_MANAGER.UNCATEGORIZED");

    // After a tickbox change: the rows' dependency notes ("(off)", "Required for") and their title colours
    // follow at once, without a full redraw.
    public static void RefreshStrips(Control content)
    {
        if (Store.Data.VanillaView)
            return;
        var rows = content.GetChildren().OfType<NModMenuRow>().ToList();
        RowStrip.Apply(rows, StripActions(content));
    }

    // A mod's right-click menu: its category on the left (pick one, or a new one, named in place), its alias,
    // notes and tags on the right, edited right there.
    public static Node? ShowRowMenu(Control content, NModMenuRow row)
    {
        if (Store.Data.VanillaView || row.Mod?.manifest?.id is not { } id)
            return null;
        return MenuPanel.Show(content, () => CategoryItems(content, id), side: _ => ModField.Column(id, _ => Edited(content)));
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

    public static Node? ShowHeaderMenu(Control content, Category category) =>
        Menus.Show(content, HeaderActions(content, category));

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
                var body = new LocString("settings_ui", "SPIRE_MOD_MANAGER.DELETE_CATEGORY_BODY");
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
