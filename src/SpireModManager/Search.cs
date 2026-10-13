using Godot;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;

namespace SpireModManager;

/// A search box over the list. Every word typed must appear (any case) in the mod's name, id, author,
// alias, notes, category or tags, or the whole query, spaces ignored, in one of them. "#tag" must be one of
// the mod's tags, whole, and "@author" its author (#"two words" and @"two words" for spaces); clicking a tag
// or an author searches for it (again: clears it), Shift-click adds or removes it. Escape clears it.
static class Search
{
    const float Height = 32;
    static readonly Color Cream = new(1, 0.964706f, 0.886275f);

    public static bool Matches(Mod? mod, string filter)
    {
        if (mod?.manifest is not { } manifest)
            return false;
        var query = Parse(filter);
        if (query.Tokens.Count > 0)
        {
            var tags = Tags.For(mod).Select(c => c.Text).ToList();
            bool Has((string Kind, string Value) token) => token.Kind == "#"
                ? tags.Contains(token.Value, StringComparer.OrdinalIgnoreCase)
                : string.Equals(manifest.author, token.Value, StringComparison.OrdinalIgnoreCase);
            if (!query.Tokens.All(Has))
                return false;
            if (query.Rest.Length == 0)
                return true;
        }
        var haystack = Lookups.Current?.Haystack(mod, HaystackOf) ?? HaystackOf(mod);
        // Every word somewhere (any case), or the whole query with spaces ignored within one field: "mod fixes"
        // finds "ModFixes" too.
        return query.Words.All(word => haystack.All.Contains(word, StringComparison.OrdinalIgnoreCase))
            || haystack.Squeezed.Any(f => f.Contains(query.Squeezed, StringComparison.OrdinalIgnoreCase));
    }

    // A search taken apart: its "#tag" and "@author" tokens, then the rest as words and with its spaces taken out.
    // Every row is matched against the same text, so the last one is kept.
    record Query(string Text, IReadOnlyList<(string Kind, string Value)> Tokens, string Rest, string[] Words, string Squeezed);
    static Query? _lastQuery;

    static Query Parse(string filter)
    {
        if (_lastQuery?.Text == filter)
            return _lastQuery;
        var tokens = Token.Matches(filter).Select(m => (Kind: m.Groups[1].Value, Value: m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value)).ToList();
        var rest = tokens.Count > 0 ? Token.Replace(filter, " ").Trim() : filter;
        return _lastQuery = new Query(filter, tokens, rest, rest.Split(' ', StringSplitOptions.RemoveEmptyEntries), Squeeze(rest));
    }

    // What plain words are looked for in: the mod's name, id, author, alias, notes, category, tags and Workshop
    // tags, all in one text (each word somewhere) and each with its spaces taken out (the whole query in one).
    public record Haystack(string All, string[] Squeezed);

    static Haystack HaystackOf(Mod mod)
    {
        var info = Store.InfoFor(mod.manifest?.id);
        string?[] fields = [mod.manifest?.name, mod.manifest?.id, mod.manifest?.author, info?.Alias, info?.Notes, info?.Category,
            .. Tags.For(mod).Select(c => c.Text), .. WorkshopInfo.For(mod)?.Tags ?? []];
        return new Haystack(string.Join('\n', fields), [.. fields.OfType<string>().Select(Squeeze)]);
    }

    static readonly System.Text.RegularExpressions.Regex Token = new(@"([#@])(?:""([^""]+)""|(\S+))");

    // A tag or author clicked: search for it; with Shift, add it to what's searched already, or take it out
    // (Toggled has the rules).
    public static void ByTag(Node near, string tag, bool add) => Put(near, "#", tag, add);

    public static void ByAuthor(Node near, string author, bool add) => Put(near, "@", author, add);

    // Plain words (a Workshop tag): found in any field, like typing them.
    public static void ByText(Node near, string text, bool add) => Put(near, "", text, add);

    static void Put(Node near, string kind, string value, bool add)
    {
        if (CategoryList.ScreenOf(near)?.GetNodeOrNull<LineEdit>("ModsBorder/SpireModManagerSearch") is not { } box)
            return;
        var token = kind.Length == 0 ? value : value.Contains(' ') ? $"{kind}\"{value}\"" : $"{kind}{value}";
        var text = Toggled(box.Text, token, add);
        box.Text = text;
        box.CaretColumn = text.Length;
        box.EmitSignal(LineEdit.SignalName.TextChanged, text);
    }

    // The search after a term is clicked, so that clicking again undoes it. A click searches for just the term, or
    // clears the search if the term is all of it already. Shift-click adds the term, or takes it out (every time it
    // appears) if it's there already, keeping the rest. Terms are compared as #tag, @author or plain words, any
    // case, quotes ignored: #"two words" is the same term as #"Two Words". One space between terms either way.
    static string Toggled(string search, string term, bool add)
    {
        var terms = Term.Matches(search).Select(m => m.Value).ToList();
        var wanted = Term.Matches(term).Select(m => m.Value).ToList();
        if (wanted.Count == 0)
            return search;
        // A plain term can be several words (a Workshop tag like "Tools & APIs"): they count when found side by side.
        bool At(int i) => i + wanted.Count <= terms.Count && Enumerable.Range(0, wanted.Count).All(j => Same(terms[i + j], wanted[j]));
        if (!add)
            return terms.Count == wanted.Count && At(0) ? "" : string.Join(' ', wanted);
        var before = terms.Count;
        for (var i = 0; i < terms.Count;)
        {
            if (At(i))
                terms.RemoveRange(i, wanted.Count);
            else
                i++;
        }
        return string.Join(' ', terms.Count < before ? terms : terms.Concat(wanted));
    }

    // A search's terms as typed: #tag and @author (quoted or not), or a plain word.
    static readonly System.Text.RegularExpressions.Regex Term = new(@"[#@](?:""[^""]+""|\S+)|(?:(?![#@](?:""[^""]+""|\S))\S)+");

    // Two terms are the same when they're the same kind with the same value, any case, quotes aside.
    static bool Same(string a, string b) => string.Equals(Plain(a), Plain(b), StringComparison.OrdinalIgnoreCase);

    // #"two words" reads as #two words, so quoted and bare terms compare alike; plain words stay as they are.
    static string Plain(string term) => Token.Match(term) is { Success: true, Index: 0 } m && m.Length == term.Length
        ? m.Groups[1].Value + (m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value)
        : term;

    static string Squeeze(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c)));

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
        box.AddThemeFontOverride("font", ModEntry.Font("res://themes/kreon_regular_shared.tres"));
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
                box.Clear();
                content.SetMeta(CategoryList.FilterMeta, "");
                CategoryList.Apply(content);
                box.AcceptEvent();
            }
        };
    }
}
