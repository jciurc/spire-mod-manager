using Godot;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;

namespace SpireModManager;

/// A search box over the list. Every word typed must appear (any case) in the mod's name, id, author,
// alias, notes, category or tags, or the whole query, spaces ignored, in one of them. "#tag" must be one of
// the mod's tags, whole, and "@author" its author (#"two words" and @"two words" for spaces); clicking a tag
// or an author searches for it, Shift-click adds it. Escape clears it.
static class Search
{
    const float Height = 32;
    static readonly Color Cream = new(1, 0.964706f, 0.886275f);

    public static bool Matches(Mod? mod, string filter)
    {
        if (mod?.manifest is not { } manifest)
            return false;
        var tokens = Token.Matches(filter).Select(m => (Kind: m.Groups[1].Value, Value: m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value)).ToList();
        if (tokens.Count > 0)
        {
            var tags = Tags.For(mod).Select(c => c.Text).ToList();
            bool Has((string Kind, string Value) token) => token.Kind == "#"
                ? tags.Contains(token.Value, StringComparer.OrdinalIgnoreCase)
                : string.Equals(manifest.author, token.Value, StringComparison.OrdinalIgnoreCase);
            if (!tokens.All(Has))
                return false;
            filter = Token.Replace(filter, " ").Trim();
            if (filter.Length == 0)
                return true;
        }
        var info = Store.InfoFor(manifest.id);
        string?[] fields = [manifest.name, manifest.id, manifest.author, info?.Alias, info?.Notes, info?.Category,
            .. Tags.For(mod).Select(c => c.Text), .. WorkshopInfo.For(mod)?.Tags ?? []];
        var haystack = string.Join('\n', fields);
        // Every word somewhere (any case), or the whole query with spaces ignored within one field: "mod fixes"
        // finds "ModFixes" too.
        var squeezed = Squeeze(filter);
        return filter.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(word => haystack.Contains(word, StringComparison.OrdinalIgnoreCase))
            || fields.Any(f => f != null && Squeeze(f).Contains(squeezed, StringComparison.OrdinalIgnoreCase));
    }

    static readonly System.Text.RegularExpressions.Regex Token = new(@"([#@])(?:""([^""]+)""|(\S+))");

    // A tag or author clicked: search for it; with Shift, add it to what's searched already (after a space).
    public static void ByTag(Node near, string tag, bool add) => Put(near, "#", tag, add);

    public static void ByAuthor(Node near, string author, bool add) => Put(near, "@", author, add);

    // Plain words (a Workshop tag): found in any field, like typing them.
    public static void ByText(Node near, string text, bool add) => Put(near, "", text, add);

    static void Put(Node near, string kind, string value, bool add)
    {
        if (CategoryList.ScreenOf(near)?.GetNodeOrNull<LineEdit>("ModsBorder/SpireModManagerSearch") is not { } box)
            return;
        var token = kind.Length == 0 ? value : value.Contains(' ') ? $"{kind}\"{value}\"" : $"{kind}{value}";
        var text = !add || box.Text.Trim().Length == 0 ? token : box.Text.Contains(token) ? box.Text : $"{box.Text.TrimEnd()} {token}";
        box.Text = text;
        box.CaretColumn = text.Length;
        box.EmitSignal(LineEdit.SignalName.TextChanged, text);
    }

    static string Squeeze(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c)));

    // Empties the box and shows every row again: Escape, or a banner's Show going to a row the search leaves out.
    public static void Clear(Control content)
    {
        CategoryList.ScreenOf(content)?.GetNodeOrNull<LineEdit>("ModsBorder/SpireModManagerSearch")?.Clear();
        content.SetMeta(CategoryList.FilterMeta, "");
        CategoryList.Apply(content);
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
        box.AddThemeFontSizeOverride("font_size", 16);
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
            CategoryList.ScrollToTop(content); // the matches start at the top, not hidden above the view
        };
        box.GuiInput += e =>
        {
            if (e is InputEventKey { Keycode: Key.Escape, Pressed: true } && box.Text.Length > 0)
            {
                Clear(content);
                box.AcceptEvent();
            }
        };
    }
}
