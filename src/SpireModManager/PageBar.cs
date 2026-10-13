using Godot;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;

namespace SpireModManager;

// Under the search box on the main list, above the categories: controls for the whole page. Enable all /
// Disable all act on the mods shown (all of them, or the search's matches; the label says which), as one
// undo step; Expand all / Collapse all open or close every category. Made again on each redraw.
// With no categories yet (a first run), a quiet line under it says how to make one; it goes with the first.
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

        var filter = content.HasMeta(CategoryList.FilterMeta) ? (string)content.GetMeta(CategoryList.FilterMeta) : "";
        // Picked mods (two or more) first: then the bar acts on them.
        var picked = Selection.Several;
        var shown = picked ? rows.Where(r => Selection.Contains(r.Mod)).ToList()
            : rows.Where(r => filter.Length == 0 || Search.Matches(r.Mod, filter)).ToList();
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
        var scope = new Label
        {
            Name = "Scope", VerticalAlignment = VerticalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore,
            Text = Menus.Format(picked ? "SPIRE_MOD_MANAGER.PAGE_SELECTED" : filter.Length == 0 ? "SPIRE_MOD_MANAGER.PAGE_ALL" : "SPIRE_MOD_MANAGER.PAGE_MATCHES",
                ("count", shown.Count.ToString())),
        };
        scope.AddThemeFontOverride("font", ResourceLoader.Load<Font>("res://themes/kreon_regular_shared.tres"));
        scope.AddThemeFontSizeOverride("font_size", 15);
        scope.AddThemeColorOverride("font_color", new Color(1, 0.964706f, 0.886275f, 0.55f));
        bar.AddChild(scope);
        List<MenuItem> onOff = [.. CategoryList.OnOffItems(content, shown)];
        if (picked)
            onOff.Add(new MenuItem(Menus.Text("SPIRE_MOD_MANAGER.CLEAR_SELECTION"), () => Selection.Clear(content)));
        bar.AddChild(Menus.InlineButtons(onOff, "OnOff"));
        bar.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore });
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
        // In the bar's own quiet style, lined up with its "Installed mods" (Layout stacks it below the bar). It names
        // the menu entry the way the menu does.
        var hint = new Label
        {
            Name = HintName, VerticalAlignment = VerticalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore,
            ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            Text = Menus.Format("SPIRE_MOD_MANAGER.NO_CATEGORIES_HINT", ("item", Menus.Text("SPIRE_MOD_MANAGER.NEW_CATEGORY"))),
        };
        hint.AddThemeFontOverride("font", ResourceLoader.Load<Font>("res://themes/kreon_regular_shared.tres"));
        hint.AddThemeFontSizeOverride("font_size", 15);
        hint.AddThemeColorOverride("font_color", new Color(1, 0.964706f, 0.886275f, 0.55f));
        border.AddChild(hint);
        hint.SetAnchorsPreset(Control.LayoutPreset.TopWide);
        (hint.OffsetLeft, hint.OffsetRight, hint.OffsetTop, hint.OffsetBottom) = (bar.OffsetLeft, bar.OffsetRight, 0, HintHeight);
    }
}
