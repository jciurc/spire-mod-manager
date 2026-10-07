using Godot;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;

namespace SpireModManager;

// A search box under the profile bar. Every word typed must appear in the mod's name, id, author,
// alias, notes or category (any case). Escape clears it.
static class Search
{
    const float Height = 38;
    static readonly Color Cream = new(1, 0.964706f, 0.886275f);

    public static bool Matches(Mod? mod, string filter)
    {
        if (mod?.manifest is not { } manifest)
            return false;
        var info = Store.InfoFor(manifest.id);
        var haystack = string.Join('\n', [manifest.name, manifest.id, manifest.author, info?.Alias, info?.Notes, info?.Category,
            .. Tags.For(mod).Select(c => c.Text)]);
        return filter.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .All(word => haystack.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    public static void Attach(NModdingScreen screen)
    {
        var border = screen.GetNode<Control>("ModsBorder");
        var scroll = border.GetNode<Control>("ModsScrollContainer");
        var content = scroll.GetNode<Control>("Mask/Content");

        var box = new LineEdit
        {
            Name = "SpireModManagerSearch",
            PlaceholderText = Menus.Text("SPIRE_MOD_MANAGER.SEARCH"),
            ClearButtonEnabled = true,
            MaxLength = 60,
        };
        box.AddThemeFontOverride("font", ResourceLoader.Load<Font>("res://themes/kreon_regular_shared.tres"));
        box.AddThemeFontSizeOverride("font_size", 18);
        box.AddThemeColorOverride("font_color", Cream);
        box.AddThemeColorOverride("font_placeholder_color", new Color(Cream, 0.45f));
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.0756f, 0.12084f, 0.18f, 0.6f),
            ContentMarginLeft = 14, ContentMarginRight = 8,
        };
        style.SetCornerRadiusAll(4);
        box.AddThemeStyleboxOverride("normal", style);
        box.AddThemeStyleboxOverride("focus", style);
        border.AddChild(box);
        box.SetAnchorsPreset(Control.LayoutPreset.TopWide);
        box.OffsetLeft = scroll.OffsetLeft;
        box.OffsetRight = scroll.OffsetRight;
        // Under the profile bar (Layout then starts the list below both).
        var top = border.GetNodeOrNull<Control>("SpireModManagerProfileBar") is { } bar ? bar.OffsetBottom + 8 : scroll.OffsetTop;
        box.OffsetTop = top;
        box.OffsetBottom = top + Height;

        box.TextChanged += text =>
        {
            content.SetMeta(CategoryList.FilterMeta, text.Trim());
            CategoryList.Apply(content);
        };
        box.GuiInput += e =>
        {
            if (e is InputEventKey { Keycode: Key.Escape, Pressed: true } && box.Text.Length > 0)
            {
                box.Clear();
                content.SetMeta(CategoryList.FilterMeta, "");
                CategoryList.Apply(content);
                box.AcceptEvent();
            }
        };
    }
}
