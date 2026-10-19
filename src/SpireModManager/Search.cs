using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Godot;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;

namespace SpireModManager;

// The search box over the list. Every word must appear (any case) in a mod's name, id, author, description, alias,
// notes, category or tags, or the whole query without spaces in one of them; punctuation and symbols count as spaces, in the
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
            // A word that is a whole tag finds it too: "stable" finds what #stable finds, though no chip shows it.
            _ => Found(Text(), term.Spaced, term.Joined) || (tags ??= Tags.Searchable(mod)).Contains(term.Spaced, StringComparer.OrdinalIgnoreCase),
        };
        if (query.Terms.Any(t => t.Not && Has(t)) || !query.Terms.Where(t => !t.Not && t.Kind.Length > 0).All(Has))
            return false;
        // Every word somewhere, or the whole query's words without spaces in one field ("mod fixes" finds "ModFixes").
        return query.Terms.Where(t => !t.Not && t.Kind.Length == 0).All(Has)
            || Text().Squeezed.Any(f => f.Contains(query.Squeezed, StringComparison.OrdinalIgnoreCase));
    }

    // A typed word's pieces (the parts between its punctuation) side by side in one field: with a space between them
    // ("test-fixes" finds "Test Fixes") or none ("spire-mod" finds "SpireMod", "don't" and "dont" find "Don't").
    static bool Found(Haystack text, string spaced, string joined) => text.All.Contains(spaced, StringComparison.OrdinalIgnoreCase)
        || text.Squeezed.Any(f => f.Contains(joined, StringComparison.OrdinalIgnoreCase));

    // Whether a search says anything: "-", "!!!" or a lone quote alone count as an empty box.
    public static bool HasTerms(string filter) => Parse(filter).Terms.Count > 0;

    const string Exact = "\"";

    // One term of a search: a tag ("#"), an author ("@"), exact text (Exact), or a word as typed (""; Spaced and Joined:
    // its pieces with a space between and with none); Not when a "-" leads it.
    record Term(string Kind, string Value, bool Not, string Spaced = "", string Joined = "");

    // A parsed search: its terms, and the words of those that must match without spaces. The last one is kept.
    record Query(string Text, IReadOnlyList<Term> Terms, string Squeezed);
    static Query? _lastQuery;

    static Query Parse(string filter)
    {
        if (_lastQuery?.Text == filter)
            return _lastQuery;
        var terms = new List<Term>();
        foreach (Match m in Token.Matches(filter))
        {
            var not = m.Groups["not"].Success;
            if (m.Groups["kind"].Success)
                terms.Add(new Term(m.Groups["kind"].Value, m.Groups["quoted"].Success ? m.Groups["quoted"].Value : m.Groups["bare"].Value, not));
            else if (m.Groups["exact"].Length > 0)
                terms.Add(new Term(Exact, m.Groups["exact"].Value, not));
        }
        foreach (var word in Token.Replace(filter, " ").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var not = word.Length > 1 && word[0] == '-'; // a "-" alone is nothing
            if (Fold(not ? word[1..] : word, " ").Trim() is { Length: > 0 } spaced)
                terms.Add(new Term("", word, not, spaced, spaced.Replace(" ", "")));
        }
        var squeezed = string.Concat(terms.Where(t => !t.Not && t.Kind.Length == 0).Select(t => t.Joined));
        return _lastQuery = new Query(filter, terms, squeezed);
    }

    // The text a search is matched against: every field as it is (for exact text), the same with punctuation and
    // symbols taken out (for words; fields a line apart, so a match stays in one), and each field without them or spaces.
    public record Haystack(string Raw, string All, string[] Squeezed);

    static Haystack HaystackOf(Mod mod)
    {
        var info = Store.InfoFor(mod);
        string?[] fields = [mod.manifest?.name, mod.manifest?.id, mod.manifest?.author, mod.manifest?.description, info?.Alias, info?.Notes, info?.Category,
            .. Tags.For(mod).Select(c => c.Text), .. WorkshopInfo.For(mod)?.Tags ?? []];
        var folded = fields.OfType<string>().Select(f => Fold(f, "")).ToList();
        return new Haystack(string.Join('\n', fields), string.Join('\n', folded), [.. folded.Select(Squeeze)]);
    }

    // Letters and numbers stay, with the accents on them; a run of spaces is one space; punctuation and symbols become
    // between (nothing for a mod's text, so "Don't" reads "Dont"; a space for a search, so "spire-mod" is two words).
    // Compatibility forms first, so full-width "ＡＢＣ" reads "ABC" and "Ⅱ" reads "II".
    static string Fold(string text, string between)
    {
        if (!text.IsNormalized(NormalizationForm.FormKC))
            text = text.Normalize(NormalizationForm.FormKC);
        var folded = new StringBuilder(text.Length);
        Span<char> pair = stackalloc char[2];
        var kept = false; // the last rune stayed, so an accent after it stays too (not one left by a symbol, like an emoji's)
        foreach (var rune in text.EnumerateRunes())
        {
            var accent = Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
                or UnicodeCategory.EnclosingMark;
            if (Rune.IsLetterOrDigit(rune) || Rune.IsNumber(rune) || accent && kept)
            {
                folded.Append(pair[..rune.EncodeToUtf16(pair)]);
                kept = true;
                continue;
            }
            kept = false;
            var gap = Rune.IsWhiteSpace(rune) ? " " : between;
            if (gap.Length > 0 && (folded.Length == 0 || folded[^1] != ' '))
                folded.Append(gap);
        }
        return folded.ToString();
    }

    // A #tag or @author (quoted for spaces), or "exact text" (its closing quote may be still to type); a "-" before
    // either, at a term's start, leaves out what it matches. A tag glued to a word ("foo#Library") counts; a lone "#"
    // is plain ("C#").
    static readonly Regex Token =
        new(@"(?<not>(?<!\S)-)?(?:(?<kind>[#@])(?:""(?<quoted>[^""]+)""|(?<bare>\S+))|""(?<exact>[^""]*)(?:""|$))");

    // A clicked tag or author: searches for it; with Shift, adds it or takes it out.
    public static void ByTag(Node near, string tag, bool add) => Put(near, "#", tag, add);

    // A clicked chip: its own search if it has one (Stable: "#Stable -#Beta"), else its tag.
    public static void ByChip(Node near, Tags.Chip chip, bool add)
    {
        if (chip.Search is { } search)
            Put(near, "", search, add);
        else
            ByTag(near, chip.Text, add);
    }

    public static void ByAuthor(Node near, string author, bool add) => Put(near, "@", author, add);

    // Plain words (a Workshop tag): found in any field, like typing them.
    public static void ByText(Node near, string text, bool add) => Put(near, "", text, add);

    static void Put(Node near, string kind, string value, bool add)
    {
        if (CategoryList.ScreenOf(near)?.GetNodeOrNull<LineEdit>("ModsBorder/SpireModManagerSearch") is not { } box)
            return;
        var token = kind.Length == 0 ? value : value.Any(char.IsWhiteSpace) ? $"{kind}\"{value}\"" : $"{kind}{value}";
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
        if (terms.Count < before)
            return string.Join(' ', terms.Select(Closed));
        // Left out until now ("-#tag"): it's put in instead.
        if (wanted.Count == 1)
            terms.RemoveAll(t => Same(t, "-" + wanted[0]));
        return string.Join(' ', terms.Select(Closed).Concat(wanted));
    }

    // A quoted term still being typed gets its closing quote, so a term added after it stays a term of its own.
    static string Closed(string term) => Unclosed.IsMatch(term) ? term + "\"" : term;

    static readonly Regex Unclosed = new(@"^-?[#@]?""[^""]*$");

    // A search's terms as typed: #tag and @author (quoted or not), "exact text", each maybe after a "-" (at a term's
    // start, as Token reads it), or a plain word.
    static readonly Regex Typed =
        new(@"(?:(?<!\S)-)?[#@](?:""[^""]+""|\S+)|(?:(?<!\S)-)?""[^""]*(?:""|$)|(?:(?![#@](?:""[^""]+""|\S))\S)+");

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
            using var lookups = Lookups.Begin(); // the redraw and Load Order's find share one set of answers
            content.SetMeta(CategoryList.FilterMeta, HasTerms(text) ? text.Trim() : ""); // "-" or "!!!" alone: no search yet
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
