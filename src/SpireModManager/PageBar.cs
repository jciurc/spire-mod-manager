using Godot;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;

namespace SpireModManager;

// The main list's page bar under the search box: Enable all / Disable all (on the mods shown, one undo step), the
// Show: choice (ListFilter), and Expand all / Collapse all. With no categories yet, a hint below says how to add one.
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
        bar.AddChild(scope);
        List<MenuItem> onOff = [.. CategoryList.OnOffItems(content, shown)];
        if (picked)
            onOff.Add(new MenuItem(Menus.Text("SPIRE_MOD_MANAGER.CLEAR_SELECTION"), () => Selection.Clear(content)));
        bar.AddChild(Menus.InlineButtons(onOff, "OnOff"));
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
        foreach (var (group, names) in new[] { ("OnOff", new[] { "EnableAllButton", "DisableAllButton", "ClearSelectionButton" }), ("Folding", ["ExpandAllButton", "CollapseAllButton"]) })
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

    // The bar's own words: small, faint cream.
    static void Quiet(Label label)
    {
        label.AddThemeFontOverride("font", ModEntry.Font("res://themes/kreon_regular_shared.tres"));
        label.AddThemeFontSizeOverride("font_size", 15);
        label.AddThemeColorOverride("font_color", new Color(1, 0.964706f, 0.886275f, 0.55f));
    }
}
