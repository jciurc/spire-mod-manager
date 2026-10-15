using Godot;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;

namespace SpireModManager;

// Load Order's search finds instead of filtering (Jordan, 2026-10-14: hidden rows made drag and drop inconsistent).
// Every row stays, and beside the search box "6 matches · ‹ ›" steps through the matching rows. It wraps at the
// ends, and each one it reaches is scrolled to and selected. Typing goes to the first match, or stays on the one
// selected while it still matches; Enter goes to the next, Shift+Enter to the one before.
static class FindBar
{
    public const string Name = "SpireModManagerFind";
    public const float Width = 168;
    static readonly Color Cream = new(1, 0.964706f, 0.886275f);

    static string Filter(Control content) => content.HasMeta(CategoryList.FilterMeta) ? (string)content.GetMeta(CategoryList.FilterMeta) : "";

    // The rows the search matches, in list order (none without a search).
    public static List<NModMenuRow> Found(Control content) => Filter(content) is { Length: > 0 } filter
        ? [.. content.GetChildren().OfType<NModMenuRow>().Where(r => Search.Matches(r.Mod, filter))]
        : [];

    // Made once, beside the search box (the toolbar places it); shown in Load Order while there's a search. True if shown.
    public static bool Update(Control border, Control content, bool loadOrder)
    {
        if (border.GetNodeOrNull<HBoxContainer>(Name) is not { } bar)
        {
            bar = new HBoxContainer { Name = Name, MouseFilter = Control.MouseFilterEnum.Ignore, Alignment = BoxContainer.AlignmentMode.End };
            bar.AddThemeConstantOverride("separation", 6);
            var count = new Label { Name = "Count", VerticalAlignment = VerticalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore };
            var pip = new Label { Name = "Pip", Text = "·", VerticalAlignment = VerticalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore };
            foreach (var (label, alpha) in new[] { (count, 0.75f), (pip, 0.45f) })
            {
                label.AddThemeFontOverride("font", ModEntry.Font("res://themes/kreon_regular_shared.tres"));
                label.AddThemeFontSizeOverride("font_size", 15);
                label.AddThemeColorOverride("font_color", new Color(Cream, alpha));
                bar.AddChild(label);
            }
            foreach (var (text, step, name, tip) in new[] { ("‹", -1, "PreviousMatch", "SPIRE_MOD_MANAGER.FIND_PREVIOUS"), ("›", 1, "NextMatch", "SPIRE_MOD_MANAGER.FIND_NEXT") })
            {
                var button = Menus.SmallButton(text, () => Step(content, step), name: name);
                button.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
                button.CustomMinimumSize = new Vector2(26, 0);
                Hover.Tip(button, Menus.Text(tip));
                bar.AddChild(button);
            }
            border.AddChild(bar);
            bar.AnchorLeft = bar.AnchorRight = 1; // offsets from the right edge, like Auto sort and its hint
        }
        bar.Visible = loadOrder && Filter(content).Length > 0;
        if (!bar.Visible)
            return false;
        var found = Found(content).Count;
        bar.GetNode<Label>("Count").Text = found == 0 ? Menus.Text("SPIRE_MOD_MANAGER.FIND_NONE")
            : found == 1 ? Menus.Text("SPIRE_MOD_MANAGER.FIND_MATCHES_ONE")
            : Menus.Format("SPIRE_MOD_MANAGER.FIND_MATCHES", ("count", Menus.Number(found)));
        foreach (var button in bar.GetChildren().OfType<Button>())
            button.Disabled = found == 0;
        return true;
    }

    // ‹ and ›: the match before or after the selected row, wrapping round at the ends.
    public static void Step(Control content, int direction)
    {
        var found = Found(content);
        if (found.Count == 0)
            return;
        var rows = content.GetChildren().OfType<NModMenuRow>().ToList();
        var at = CategoryList.Selected is { } selected ? rows.FindIndex(r => r.Mod == selected) : -1;
        var target = direction > 0
            ? found.FirstOrDefault(r => rows.IndexOf(r) > at) ?? found[0]
            : (at < 0 ? null : found.LastOrDefault(r => rows.IndexOf(r) < at)) ?? found[^1];
        Go(content, target);
    }

    // After typing: the selected row while it still matches, else the first match.
    public static void Typed(Control content)
    {
        var found = Found(content);
        if (found.Count == 0)
            return;
        Go(content, found.FirstOrDefault(r => r.Mod == CategoryList.Selected) ?? found[0]);
    }

    static void Go(Control content, NModMenuRow row)
    {
        CategoryList.Select(content, row);
        CategoryList.ScrollTo(content, row);
    }
}
