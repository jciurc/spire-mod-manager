using Godot;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;

namespace SpireModManager;

// The main list's page bar under the search box: Enable all / Disable all (on the mods shown, one undo step), the
// Show: choice (ListFilter), and Expand all / Collapse all. With two or more mods picked, it acts on them: their count
// on a chip that lets go of them, and Enable selected / Disable selected. With no categories yet, a hint below says
// how to add one.
static class PageBar
{
    public const string Name = "SpireModManagerPageBar", HintName = "SpireModManagerCategoryHint";
    public const float Height = 28;
    const float HintHeight = 22;

    public static void Update(Control content, Control border, List<NModMenuRow> rows)
    {
        foreach (var name in new[] { Name, HintName })
            if (border.GetNodeOrNull(name) is { } old)
            {
                border.RemoveChild(old); // now: a second redraw in the same frame adds a new one
                old.QueueFree();
            }
        if (Layout.Current != Layout.View.Categories)
            return;

        var filter = CategoryList.Filter(content);
        // Picked mods (two or more) first: then the bar acts on them.
        var picked = Selection.Several;
        var shown = picked ? rows.Where(r => Selection.Contains(r.Mod)).ToList()
            : rows.Where(r => (filter.Length == 0 || Search.Matches(r.Mod, filter)) && ListFilter.Shows(r.Mod)).ToList();
        var choice = ListFilter.Chosen;
        var categories = Store.Data.Categories;
        void Fold(bool collapsed)
        {
            foreach (var category in categories)
                category.Collapsed = collapsed;
            Store.Data.UncategorizedCollapsed = collapsed;
            Store.Save();
            CategoryList.Apply(content);
        }
        var anyCollapsed = categories.Any(c => c.Collapsed) || Store.Data.UncategorizedCollapsed;
        var anyOpen = categories.Any(c => !c.Collapsed) || !Store.Data.UncategorizedCollapsed;

        var bar = new HBoxContainer { Name = Name, MouseFilter = Control.MouseFilterEnum.Ignore };
        bar.AddThemeConstantOverride("separation", 8);
        // A search's matches are counted as such, whatever the choice (both apply); the choice's rows by its name.
        var scopeKey = picked ? "SPIRE_MOD_MANAGER.PAGE_SELECTED" : filter.Length > 0 ? "SPIRE_MOD_MANAGER.PAGE_MATCHES" : choice switch
        {
            ListFilter.Choice.Enabled => "SPIRE_MOD_MANAGER.PAGE_ENABLED",
            ListFilter.Choice.Disabled => "SPIRE_MOD_MANAGER.PAGE_DISABLED",
            ListFilter.Choice.Changed => "SPIRE_MOD_MANAGER.PAGE_CHANGED",
            _ => "SPIRE_MOD_MANAGER.PAGE_ALL",
        };
        var scope = new Label
        {
            Name = "Scope", VerticalAlignment = VerticalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore,
            Text = Menus.Format(scopeKey, ("count", Menus.Number(shown.Count))),
        };
        Quiet(scope);
        bar.AddChild(picked ? Chip(content, scope) : scope);
        bar.AddChild(Menus.InlineButtons(CategoryList.OnOffItems(content, shown, selected: picked), "OnOff"));
        bar.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore });

        // "Show:" and its choices, the chosen one lit; a little apart from the folding buttons after them.
        var show = new HBoxContainer { Name = "Show", MouseFilter = Control.MouseFilterEnum.Ignore, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        show.AddThemeConstantOverride("separation", 2);
        var label = new Label
        {
            Name = "ShowLabel", Text = Menus.Text("SPIRE_MOD_MANAGER.SHOW_LABEL"), VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        Quiet(label);
        show.AddChild(label);
        show.AddChild(new Control { CustomMinimumSize = new Vector2(2, 0), MouseFilter = Control.MouseFilterEnum.Ignore });
        foreach (var (option, key, tip, name) in new[]
        {
            (ListFilter.Choice.All, "SPIRE_MOD_MANAGER.SHOW_ALL", "SPIRE_MOD_MANAGER.SHOW_ALL_TIP", "ShowAllButton"),
            (ListFilter.Choice.Enabled, "SPIRE_MOD_MANAGER.SHOW_ENABLED", "SPIRE_MOD_MANAGER.SHOW_ENABLED_TIP", "ShowEnabledButton"),
            (ListFilter.Choice.Disabled, "SPIRE_MOD_MANAGER.SHOW_DISABLED", "SPIRE_MOD_MANAGER.SHOW_DISABLED_TIP", "ShowDisabledButton"),
            (ListFilter.Choice.Changed, "SPIRE_MOD_MANAGER.SHOW_CHANGED", "SPIRE_MOD_MANAGER.SHOW_CHANGED_TIP", "ShowChangedButton"),
        })
        {
            var button = Menus.ChoiceButton(Menus.Text(key), option == choice, () => ListFilter.Choose(content, option), name);
            Hover.Tip(button, Menus.Text(tip));
            button.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            show.AddChild(button);
        }
        show.AddChild(new Control { CustomMinimumSize = new Vector2(6, 0), MouseFilter = Control.MouseFilterEnum.Ignore });
        bar.AddChild(show);
        bar.AddChild(Menus.InlineButtons([
            new(Menus.Text("SPIRE_MOD_MANAGER.EXPAND_ALL"), () => Fold(false), Disabled: !anyCollapsed || filter.Length > 0),
            new(Menus.Text("SPIRE_MOD_MANAGER.COLLAPSE_ALL"), () => Fold(true), Disabled: !anyOpen || filter.Length > 0),
        ], "Folding"));
        foreach (var (group, names) in new[] { ("OnOff", new[] { "EnableAllButton", "DisableAllButton" }), ("Folding", ["ExpandAllButton", "CollapseAllButton"]) })
            foreach (var (button, name) in bar.GetNode(group).GetChildren().OfType<Button>().Zip(names))
                button.Name = name;

        border.AddChild(bar);
        bar.SetAnchorsPreset(Control.LayoutPreset.TopWide);
        var scroll = border.GetNode<Control>("ModsScrollContainer");
        (bar.OffsetLeft, bar.OffsetRight, bar.OffsetTop, bar.OffsetBottom) = (scroll.OffsetLeft + 12, scroll.OffsetRight - 16, 0, Height);

        if (categories.Count > 0)
            return;
        // The hint, in the bar's quiet style, lined up with "Installed mods" (Layout stacks it below the bar).
        var hint = new Label
        {
            Name = HintName, VerticalAlignment = VerticalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore,
            ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            Text = Menus.Text("SPIRE_MOD_MANAGER.NO_CATEGORIES_HINT"),
        };
        Quiet(hint);
        border.AddChild(hint);
        hint.SetAnchorsPreset(Control.LayoutPreset.TopWide);
        (hint.OffsetLeft, hint.OffsetRight, hint.OffsetTop, hint.OffsetBottom) = (bar.OffsetLeft, bar.OffsetRight, 0, HintHeight);
    }

    // "Selected (N)" while two or more mods are picked: on a chip in the picked rows' blue, with a × that lets go of
    // them (as Esc and Ctrl+D do), lit under the mouse.
    static Control Chip(Control content, Label scope)
    {
        var chip = new PanelContainer
        {
            Name = "SelectionChip", MouseFilter = Control.MouseFilterEnum.Stop, MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        };
        var look = new StyleBoxFlat { BgColor = Selection.Tint with { A = 0.3f }, ContentMarginLeft = 8, ContentMarginRight = 7 };
        look.SetCornerRadiusAll(RowTitles.CornerRadius);
        chip.AddThemeStyleboxOverride("panel", look);
        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 6);
        chip.AddChild(row);
        var clear = new Label { Name = "Clear", Text = "×", VerticalAlignment = VerticalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore };
        foreach (var label in new[] { scope, clear })
        {
            Quiet(label);
            label.AddThemeColorOverride("font_color", Cream with { A = 0.9f }); // brighter than the bar's other words
            row.AddChild(label);
        }
        chip.MouseEntered += () => look.BgColor = Selection.Tint with { A = 0.45f };
        chip.MouseExited += () => look.BgColor = Selection.Tint with { A = 0.3f };
        chip.GuiInput += input =>
        {
            if (input is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false })
            {
                chip.AcceptEvent();
                // After this input: letting go rebuilds the bar, this chip included.
                Callable.From(() => Selection.Clear(content)).CallDeferred();
            }
        };
        Hover.Tip(chip, $"{Menus.Text("SPIRE_MOD_MANAGER.CLEAR_SELECTION")}\n{Menus.Text("SPIRE_MOD_MANAGER.CLEAR_SELECTION_KEYS")}");
        return chip;
    }

    static readonly Color Cream = new(1, 0.964706f, 0.886275f);

    // The bar's own words: small, faint cream.
    static void Quiet(Label label)
    {
        label.AddThemeFontOverride("font", ModEntry.Font("res://themes/kreon_regular_shared.tres"));
        label.AddThemeFontSizeOverride("font_size", 15);
        label.AddThemeColorOverride("font_color", Cream with { A = 0.55f });
    }
}
