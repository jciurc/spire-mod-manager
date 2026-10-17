using Godot;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;

namespace SpireModManager;

// The search box over the list. Every word must appear (any case) in a mod's name, id, author, alias, notes,
// category or tags, or the whole query without spaces in one of them; punctuation and symbols count as spaces, in the
// search and in the mod's text ("dont" finds "Don't", "spire-mod" finds "Spire Mod"). "#tag" matches a whole tag,
// "@author" the author, and "text in quotes" that exact text, spaces, symbols and case too. A "-" before any of them
// leaves out the mods that match it ("#stable -#beta"). Clicking a tag or author searches for it, Shift-click adds or
// removes it.
static class Search
{
    const float Height = 32;
    static readonly Color Cream = new(1, 0.964706f, 0.886275f);

    public static bool Matches(Mod? mod, string filter)
    {
        if (mod?.manifest is not { } manifest)
            return false;
        var query = Parse(filter);
        Haystack? haystack = null;
        Haystack Text() => haystack ??= Lookups.Current?.Haystack(mod, HaystackOf) ?? HaystackOf(mod);
        IReadOnlyList<string>? tags = null;
        bool Has(Term term) => term.Kind switch
        {
            "#" => (tags ??= Tags.Searchable(mod)).Contains(term.Value, StringComparer.OrdinalIgnoreCase),
            "@" => string.Equals(manifest.author, term.Value, StringComparison.OrdinalIgnoreCase),
            Exact => Text().Raw.Contains(term.Value, StringComparison.Ordinal),
            _ => term.Words.All(word => Text().All.Contains(word, StringComparison.OrdinalIgnoreCase)),
        };
        if (query.Terms.Any(t => t.Not && Has(t)) || !query.Terms.Where(t => !t.Not && t.Kind.Length > 0).All(Has))
            return false;
        // Every word somewhere, or the whole query's words without spaces in one field ("mod fixes" finds "ModFixes").
        return query.Terms.Where(t => !t.Not && t.Kind.Length == 0).All(Has)
            || Text().Squeezed.Any(f => f.Contains(query.Squeezed, StringComparison.OrdinalIgnoreCase));
    }

    const string Exact = "\"";

    // One term of a search: a tag ("#"), an author ("@"), exact text (Exact), or a word as typed ("", Words: its pieces
    // between punctuation); Not when a "-" leads it.
    record Term(string Kind, string Value, bool Not, string[] Words);

    // A parsed search: its terms, and the words of those that must match without spaces. The last one is kept.
    record Query(string Text, IReadOnlyList<Term> Terms, string Squeezed);
    static Query? _lastQuery;

    static Query Parse(string filter)
    {
        if (_lastQuery?.Text == filter)
            return _lastQuery;
        var terms = new List<Term>();
        foreach (System.Text.RegularExpressions.Match m in Token.Matches(filter))
        {
            var not = m.Groups["not"].Success;
            if (m.Groups["kind"].Success)
                terms.Add(new Term(m.Groups["kind"].Value, m.Groups["quoted"].Success ? m.Groups["quoted"].Value : m.Groups["bare"].Value, not, []));
            else if (m.Groups["exact"].Length > 0)
                terms.Add(new Term(Exact, m.Groups["exact"].Value, not, []));
        }
        foreach (var word in Token.Replace(filter, " ").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var not = word.Length > 1 && word[0] == '-'; // a "-" alone is nothing
            if (Words(not ? word[1..] : word) is { Length: > 0 } words)
                terms.Add(new Term("", word, not, words));
        }
        var squeezed = string.Concat(terms.Where(t => !t.Not && t.Kind.Length == 0).SelectMany(t => t.Words));
        return _lastQuery = new Query(filter, terms, squeezed);
    }

    // The text a search is matched against: every field in one as it is (for exact text), the same with punctuation
    // and symbols taken out (for words), and each field without them or spaces.
    public record Haystack(string Raw, string All, string[] Squeezed);

    static Haystack HaystackOf(Mod mod)
    {
        var info = Store.InfoFor(mod.manifest?.id);
        string?[] fields = [mod.manifest?.name, mod.manifest?.id, mod.manifest?.author, info?.Alias, info?.Notes, info?.Category,
            .. Tags.For(mod).Select(c => c.Text), .. WorkshopInfo.For(mod)?.Tags ?? []];
        var raw = string.Join('\n', fields);
        return new Haystack(raw, Fold(raw, ""), [.. fields.OfType<string>().Select(f => Squeeze(Fold(f, "")))]);
    }

    // Letters, digits and accents stay; spaces become one space; punctuation and symbols become between (nothing for a
    // mod's text, so "Don't" reads "Dont"; a space for a search, so "spire-mod" is two words).
    static string Fold(string text, string between)
    {
        var folded = new System.Text.StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            if (System.Text.Rune.IsLetterOrDigit(rune) || System.Text.Rune.GetUnicodeCategory(rune) is System.Globalization.UnicodeCategory.NonSpacingMark
                or System.Globalization.UnicodeCategory.SpacingCombiningMark or System.Globalization.UnicodeCategory.EnclosingMark)
                folded.Append(rune.ToString());
            else
                folded.Append(System.Text.Rune.IsWhiteSpace(rune) ? " " : between);
        }
        return folded.ToString();
    }

    static string[] Words(string typed) => Fold(typed, " ").Split(' ', StringSplitOptions.RemoveEmptyEntries);

    // A #tag or @author (quoted for spaces), or "exact text" (its closing quote may be still to type); a "-" before
    // either, at a term's start, leaves out what it matches. A tag glued to a word ("foo#Library") counts; a lone "#"
    // is plain ("C#").
    static readonly System.Text.RegularExpressions.Regex Token =
        new(@"(?<not>(?<!\S)-)?(?:(?<kind>[#@])(?:""(?<quoted>[^""]+)""|(?<bare>\S+))|""(?<exact>[^""]*)(?:""|$))");

    // A clicked tag or author: searches for it; with Shift, adds it or takes it out.
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

    // The search after clicking a term, so a second click undoes it: a click searches for just the term (or clears it),
    // Shift-click adds or removes it. Terms compare by kind and value, any case, quotes ignored.
    static string Toggled(string search, string term, bool add)
    {
        var terms = Typed.Matches(search).Select(m => m.Value).ToList();
        var wanted = Typed.Matches(term).Select(m => m.Value).ToList();
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

    // A search's terms as typed: #tag and @author (quoted or not), "exact text", each maybe after a "-", or a plain word.
    static readonly System.Text.RegularExpressions.Regex Typed =
        new(@"-?[#@](?:""[^""]+""|\S+)|-?""[^""]*(?:""|$)|(?:(?![#@](?:""[^""]+""|\S))\S)+");

    // Two terms are the same when they're the same kind with the same value, any case, quotes aside.
    static bool Same(string a, string b) => string.Equals(Plain(a), Plain(b), StringComparison.OrdinalIgnoreCase);

    // #"two words" reads as #two words, so quoted and bare terms compare alike; other terms stay as they are.
    static string Plain(string term) => Token.Match(term) is { Success: true, Index: 0 } m && m.Length == term.Length && m.Groups["kind"].Success
        ? (m.Groups["not"].Success ? "-" : "") + m.Groups["kind"].Value + (m.Groups["quoted"].Success ? m.Groups["quoted"].Value : m.Groups["bare"].Value)
        : term;

    static string Squeeze(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c)));

    // Empties the box and shows every row again (Escape, or going to a row the search hides).
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
            if (Store.Data.LoadOrderView)
                FindBar.Typed(content); // every row stays: to the first match
            else
                CategoryList.ScrollToTop(content); // the matches start at the top, not hidden above the view
        };
        box.GuiInput += e =>
        {
            if (e is InputEventKey { Keycode: Key.Escape, Pressed: true } && box.Text.Length > 0)
            {
                Clear(content);
                box.AcceptEvent();
            }
            // Load Order: Enter goes to the next match, Shift+Enter the one before.
            else if (e is InputEventKey { Keycode: Key.Enter or Key.KpEnter, Pressed: true } enter && Store.Data.LoadOrderView && box.Text.Length > 0)
            {
                FindBar.Step(content, enter.ShiftPressed ? -1 : 1);
                box.AcceptEvent();
            }
        };
    }
}
