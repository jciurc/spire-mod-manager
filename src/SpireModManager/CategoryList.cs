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

        NButton? toggle = null;
        toggle = ModEntry.AddButton(screen, ToggleLabel(), new Vector2(220, 70), () =>
        {
            Store.Data.VanillaView = !Store.Data.VanillaView;
            Store.Save();
            toggle?.GetNode<MegaLabel>("Visuals/Label").SetTextAutoSize(new LocString("settings_ui", ToggleLabel()).GetFormattedText());
            Apply(content);
        });
        toggle.Name = "SpireModManagerViewToggle";
        // Left end of the pending-changes row (y 1012-1056), aligned with the mod list's left edge (x 255).
        toggle.Position = new Vector2(255, 1034 - 35);

        Apply(content);
    }

    static string ToggleLabel() => Store.Data.VanillaView ? "SPIRE_MOD_MANAGER.CATEGORY_VIEW" : "SPIRE_MOD_MANAGER.VANILLA_VIEW";

    public static void Apply(Control content)
    {
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
        ModDetails.ApplyRowTitles(rows, Store.Data.VanillaView);

        // Right-click a row to pick its category. Rows outlive Apply, so connect once.
        foreach (var row in rows.Where(r => !r.HasMeta(RowMenuMeta)))
        {
            row.SetMeta(RowMenuMeta, true);
            row.GuiInput += e =>
            {
                if (e is InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: false })
                    ShowRowMenu(content, row);
            };
        }

        // Search: rows that don't match hide; while searching, categories open to show their matches.
        var filter = content.HasMeta(FilterMeta) ? (string)content.GetMeta(FilterMeta) : "";
        var filtering = filter.Length > 0;
        bool Matches(NModMenuRow row) => !filtering || Search.Matches(row.Mod, filter);

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
            if (muted)
            {
                // Not a category the player made: cream instead of gold, fainter background.
                titleLabel.AddThemeColorOverride("font_color", new Color(1, 0.964706f, 0.886275f, 0.7f));
                header.GetNode<ColorRect>("Background").Color = new Color(0.0756f, 0.12084f, 0.18f, 0.3f);
            }
            header.GetNode<Label>("Count").Text = (filtering ? members.Count(Matches) : members.Count).ToString();
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

    // Categories to move the mod into (its current one ticked), or a new one, named in place.
    public static PopupMenu? ShowRowMenu(Control content, NModMenuRow row)
    {
        if (Store.Data.VanillaView || row.Mod?.manifest?.id is not { } id)
            return null;
        var current = Store.InfoFor(id)?.Category is { } name && Categories.Find(name) != null ? name : null;
        var items = new List<MenuItem> { MenuItem.Heading(Menus.Text("SPIRE_MOD_MANAGER.CATEGORY")) };
        foreach (var category in Store.Data.Categories)
            items.Add(new MenuItem(category.Name, () => Move(category.Name), Checked: category.Name == current));
        items.Add(new MenuItem(Menus.Text("SPIRE_MOD_MANAGER.UNCATEGORIZED"), () => Move(null), Checked: current == null));
        items.Add(MenuItem.Heading(""));
        items.Add(new MenuItem(Menus.Text("SPIRE_MOD_MANAGER.NEW_CATEGORY"), () =>
        {
            var category = Categories.Add();
            Move(category.Name);
            BeginRename(content, category);
        }));
        return Menus.Show(content, items);

        void Move(string? category)
        {
            Categories.Assign(id, category);
            Apply(content);
        }
    }

    public static PopupMenu ShowHeaderMenu(Control content, Category category)
    {
        var list = Store.Data.Categories;
        var index = list.IndexOf(category);
        return Menus.Show(content, [
            new MenuItem(Menus.Text("SPIRE_MOD_MANAGER.RENAME"), () => BeginRename(content, category)),
            new MenuItem(Menus.Text("SPIRE_MOD_MANAGER.MOVE_UP"), () => Edit(() => Categories.Move(category, -1)), Disabled: index == 0),
            new MenuItem(Menus.Text("SPIRE_MOD_MANAGER.MOVE_DOWN"), () => Edit(() => Categories.Move(category, 1)), Disabled: index == list.Count - 1),
            new MenuItem(Menus.Text("SPIRE_MOD_MANAGER.DELETE"), () => _ = Delete()),
        ]);

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
        var edit = new LineEdit { Name = "RenameBox", Text = category.Name, Flat = true, SelectAllOnFocus = true, MaxLength = 40 };
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

        var done = false;
        void Finish(bool commit)
        {
            if (done)
                return;
            done = true;
            if (commit)
                Categories.Rename(category, edit.Text); // empty or taken: keeps the old name
            Callable.From(() => Apply(content)).CallDeferred(); // not while the box is handling its own signal
        }
        edit.TextSubmitted += _ => Finish(true);
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
