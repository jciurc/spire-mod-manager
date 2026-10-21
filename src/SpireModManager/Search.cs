using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Godot;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;

namespace SpireModManager;

// The search box over the list. Every word must appear (any case) in a mod's name, id, author, alias, notes, category
// or tags, or start a word of its description, or the whole query without spaces be in one of the short fields. The
// punctuation of sentences counts as spaces, in the search and in the mod's text ("dont" finds "Don't", "spire-mod"
// finds "Spire Mod"); other symbols are part of a word ("C#"). "#tag" matches a whole tag (or a hidden one: "#enabled",
// "#problem", HiddenTags), "@author" the author, and "text in quotes" that exact text, spaces, symbols and case too. A
// "-" or NOT before any of them, or before a group in brackets, leaves out the mods that match it ("#stable -#beta");
// OR or "|" between two finds either. Brackets go first, then NOT, then OR, then the space (or AND) between terms:
// "#a OR #b #c" is "(#a OR #b) #c". Clicking a tag or author searches for it, Shift-click adds or removes it.
static class Search
{
    const float Height = 32;
    static readonly Color Cream = new(1, 0.964706f, 0.886275f);

    public static bool Matches(Mod? mod, string filter)
    {
        if (mod?.manifest is not { } manifest)
            return false;
        if (Parse(filter).Root is not { } root)
            return true;
        Haystack? haystack = null;
        Haystack Text() => haystack ??= Lookups.Current?.Haystack(mod, HaystackOf) ?? HaystackOf(mod);
        IReadOnlyList<string>? tags = null;
        bool Has(Term term) => term.Kind switch
        {
            // A hidden tag first (no list of the mod's tags needed), then its own tags: a tag of the player's named like a
            // hidden one finds its mods too.
            "#" => term.Hidden?.Invoke(mod) == true || (tags ??= Tags.Searchable(mod)).Contains(term.Value, StringComparer.OrdinalIgnoreCase),
            "@" => string.Equals(manifest.author, term.Value, StringComparison.OrdinalIgnoreCase),
            Exact => Text().Raw.Contains(term.Value, StringComparison.Ordinal),
            // A word that is a whole tag finds it too: "stable" finds what #stable finds, though no chip shows it.
            _ => Found(Text(), term.Spaced, term.Joined) || (tags ??= Tags.Searchable(mod)).Contains(term.Spaced, StringComparer.OrdinalIgnoreCase),
        };
        bool Test(Clause clause) => clause switch
        {
            Term term => Has(term),
            // Every word somewhere, or all of them without spaces in one field ("mod fixes" finds "ModFixes").
            Words words => words.Each.All(Has) || Text().Squeezed.Any(f => f.Contains(words.Squeezed, StringComparison.OrdinalIgnoreCase)),
            Not not => !Test(not.Of),
            AnyOf any => any.Of.Any(Test),
            AllOf all => all.Of.All(Test),
            _ => false,
        };
        return Test(root);
    }

    // A typed word's pieces (the parts between its punctuation) side by side in one field: with a space between them
    // ("test-fixes" finds "Test Fixes") or none ("spire-mod" finds "SpireMod", "don't" and "dont" find "Don't").
    // In the description, each at the start of one of its words, punctuation gone or as spaces ("dont" finds "Don't",
    // "per-card" finds "per-card" and "percard").
    static bool Found(Haystack text, string spaced, string joined) => text.All.Contains(spaced, StringComparison.OrdinalIgnoreCase)
        || text.Squeezed.Any(f => f.Contains(joined, StringComparison.OrdinalIgnoreCase))
        || StartsWord(text.Description, spaced) || StartsWord(text.DescriptionSpaced, spaced) || StartsWord(text.Description, joined);

    // Whether a word is in folded text at a word's start: after a space or at the start, or anywhere in a script written
    // without spaces (Chinese, Japanese, Thai...), where words can't be told apart.
    static bool StartsWord(string text, string word)
    {
        if (word.Length == 0)
            return false;
        for (var at = text.IndexOf(word, StringComparison.OrdinalIgnoreCase); at >= 0; at = text.IndexOf(word, at + 1, StringComparison.OrdinalIgnoreCase))
            if (at == 0 || text[at - 1] == ' ' || Spaceless(text[at - 1]) || Spaceless(word[0]))
                return true;
        return false;
    }

    static bool Spaceless(char c) => c is >= '⺀' and <= '鿿' or >= '豈' and <= '﫿' or >= 'ｦ' and <= 'ﾟ'
        or >= '฀' and <= '໿' or >= 'က' and <= '႟' or >= 'ក' and <= '៿' || char.IsSurrogate(c);

    // Whether a search says anything: "-", "!!!", "()", OR or a lone quote alone count as an empty box.
    public static bool HasTerms(string filter) => Parse(filter).Root != null;

    // Whether a search looks for one of these tags (any case; not where it leaves the tag out): the main list's line
    // for #missing and #uninstalled, which no row matches.
    public static bool Wants(string filter, params string[] tags)
    {
        bool In(Clause? clause, bool left) => clause switch
        {
            Term { Kind: "#" } term => !left && tags.Contains(term.Value, StringComparer.OrdinalIgnoreCase),
            Not not => In(not.Of, !left),
            AnyOf any => any.Of.Any(c => In(c, left)),
            AllOf all => all.Of.Any(c => In(c, left)),
            _ => false,
        };
        return In(Parse(filter).Root, left: false);
    }

    const string Exact = "\"";

    // A search as a tree, tested against each mod.
    abstract record Clause;

    // One term: a tag ("#"), an author ("@"), exact text (Exact), or a word as typed (""; Spaced and Joined: its pieces
    // with a space between and with none). Hidden: what a hidden tag of a tag's name finds.
    sealed record Term(string Kind, string Value, string Spaced = "", string Joined = "", Func<Mod, bool>? Hidden = null) : Clause;

    // A group's plain words, each to be found (or Squeezed, all of them without spaces, in one field).
    sealed record Words(IReadOnlyList<Term> Each, string Squeezed) : Clause;

    sealed record Not(Clause Of) : Clause;

    sealed record AnyOf(IReadOnlyList<Clause> Of) : Clause;

    sealed record AllOf(IReadOnlyList<Clause> Of) : Clause;

    // A parsed search (Root null: nothing to search). The last one is kept: a redraw asks about every row.
    record Query(string Text, Clause? Root);
    static Query? _lastQuery;

    // What a piece of a search is: a term, a bracket, or an operator (OR and "|"; AND; NOT and a "-" before a term).
    enum Piece { Term, Open, Close, Or, And, Not }

    // A piece as typed, and its term if it's one.
    record Lexed(Piece Piece, string Text, Term? Term = null);

    // Never an error, as a search box should be: a missing ")" closes at the end, "()" is nothing, an OR or AND with
    // nothing on one side is left out, and so is a NOT (or "-") before nothing. Terms side by side, or with AND between,
    // must all match; OR binds closer ("#a OR #b #c" is "(#a OR #b) #c"), NOT closer still. Without operators, a search
    // is what it always was: every term, and its plain words together (Words).
    static Query Parse(string filter)
    {
        if (_lastQuery?.Text == filter)
            return _lastQuery;
        var pieces = Lex(filter);
        var at = 0;
        Piece? Next() => at < pieces.Count ? pieces[at].Piece : null;
        // Up to the ")" closing the group (none at the top: Lex drops one that closes nothing).
        Clause? All(bool grouped)
        {
            var each = new List<Clause>();
            while (Next() is { } next && !(grouped && next == Piece.Close))
            {
                if (next is Piece.And or Piece.Or or Piece.Close)
                    at++; // with nothing before it
                else if (Either() is { } clause)
                    each.Add(clause);
            }
            return Together(each);
        }
        Clause? Either()
        {
            var clause = Negated();
            while (Next() == Piece.Or)
            {
                at++;
                if (Next() is null or Piece.Close or Piece.And)
                    break; // nothing after it
                if (Negated() is { } other)
                    clause = clause switch { null => other, AnyOf any => new AnyOf([.. any.Of, other]), _ => new AnyOf([clause, other]) };
            }
            return clause;
        }
        Clause? Negated()
        {
            if (Next() != Piece.Not)
                return Single();
            at++;
            return Negated() is { } clause ? new Not(clause) : null;
        }
        Clause? Single()
        {
            if (Next() == Piece.Open)
            {
                at++;
                var group = All(grouped: true);
                if (Next() == Piece.Close)
                    at++;
                return group;
            }
            return Next() == Piece.Term && pieces[at++].Term is { } term && Says(term) ? term : null;
        }
        return _lastQuery = new Query(filter, All(grouped: false));
    }

    // Whether a term has something to look for: not a word of punctuation alone ("!!!") or "".
    static bool Says(Term term) => term.Kind switch { "" => term.Spaced.Length > 0, Exact => term.Value.Length > 0, _ => true };

    // Terms that must all match: the plain words among them as one Words, last (the others are quicker to look for).
    static Clause? Together(List<Clause> each)
    {
        var words = each.OfType<Term>().Where(t => t.Kind.Length == 0).ToList();
        var all = each.Where(c => c is not Term { Kind: "" }).ToList();
        if (words.Count > 1)
            all.Add(new Words(words, string.Concat(words.Select(w => w.Joined))));
        else
            all.AddRange(words);
        return all.Count switch { 0 => null, 1 => all[0], _ => new AllOf(all) };
    }

    // A search's pieces in order: #tag or #"two words", @author (quoted the same), "exact text" (its closing quote may be
    // still to type), a word, a bracket, OR, "|", AND or NOT. A "-" at a term's start leaves out the term or group after
    // it ("-" alone is nothing, and a second one is part of a word: "--x" leaves out "-x"). A word ends at a space, a "|",
    // a quote, and a #tag or @author glued to it ("foo#Library" is two terms; a lone "#" is plain: "C#"); a word or a
    // bare #tag also ends at a ")" while a group is open, and a bare #tag or @author at a quote or a "(" too, which start
    // the next term ("#Library(lib)" is the tag and a group; #"x (y)" keeps its brackets). A ")" that closes nothing is
    // nothing.
    static List<Lexed> Lex(string text)
    {
        var pieces = new List<Lexed>();
        var depth = 0;
        var dashed = -1; // just after a "-" that leaves out: no second one there
        bool Ends(int at) => char.IsWhiteSpace(text[at]) || text[at] == '|' || text[at] == ')' && depth > 0;
        bool Marked(int at) => text[at] is '#' or '@' && at + 1 < text.Length && !Ends(at + 1);
        for (var at = 0; at < text.Length;)
        {
            var (from, c) = (at, text[at]);
            if (char.IsWhiteSpace(c) || c == ')' && depth == 0)
                at++;
            else if (c is '(' or ')' or '|')
            {
                depth += c switch { '(' => 1, ')' => -1, _ => 0 };
                pieces.Add(new Lexed(c switch { '(' => Piece.Open, ')' => Piece.Close, _ => Piece.Or }, c.ToString()));
                at++;
            }
            else if (c == '-' && at != dashed && at + 1 < text.Length && !char.IsWhiteSpace(text[at + 1]) && text[at + 1] is not ('|' or ')'))
            {
                pieces.Add(new Lexed(Piece.Not, "-"));
                dashed = ++at;
            }
            else if (c == '"')
            {
                var end = text.IndexOf('"', at + 1);
                at = end < 0 ? text.Length : end + 1;
                pieces.Add(new Lexed(Piece.Term, text[from..at], new Term(Exact, text[(from + 1)..(end < 0 ? at : end)])));
            }
            else if (Marked(at))
            {
                var close = text[at + 1] == '"' ? text.IndexOf('"', at + 2) : -1;
                string value;
                if (close > at + 2)
                    (value, at) = (text[(at + 2)..close], close + 1);
                else
                {
                    at++;
                    while (at < text.Length && !Ends(at) && text[at] is not ('"' or '('))
                        at++;
                    value = text[(from + 1)..at];
                }
                pieces.Add(new Lexed(Piece.Term, text[from..at], new Term(c.ToString(), value, Hidden: c == '#' ? HiddenTags.Find(value) : null)));
            }
            else
            {
                at++;
                while (at < text.Length && !Ends(at) && text[at] != '"' && !Marked(at))
                    at++;
                var word = text[from..at];
                var spaced = Fold(word, " ").Trim();
                pieces.Add(word switch
                {
                    "OR" => new Lexed(Piece.Or, word),
                    "AND" => new Lexed(Piece.And, word),
                    "NOT" => new Lexed(Piece.Not, word),
                    _ => new Lexed(Piece.Term, word, new Term("", word, spaced, spaced.Replace(" ", ""))),
                });
            }
        }
        // OR, AND and NOT in capitals are operators anywhere; in small letters they're words ("rock and stone"), but
        // operators between two terms that aren't words (#tags, @authors, quotes, groups: "#warning or #error").
        static Piece? Keyword(Lexed piece) => piece.Term is { Kind: "" } word
            ? word.Value.ToLowerInvariant() switch { "or" => Piece.Or, "and" => Piece.And, "not" => Piece.Not, _ => null } : null;
        static bool Joins(Lexed piece) => piece.Piece is Piece.Or or Piece.And or Piece.Not || Keyword(piece) != null;
        for (var i = 0; i < pieces.Count; i++)
            if (Keyword(pieces[i]) is { } op && pieces.Take(i).LastOrDefault(p => !Joins(p)) is { Piece: Piece.Close } or { Term.Kind: not "" }
                && pieces.Skip(i + 1).FirstOrDefault(p => !Joins(p)) is { Piece: Piece.Open } or { Term.Kind: not "" })
                pieces[i] = new Lexed(op, pieces[i].Text);
        return pieces;
    }

    // The text a search is matched against: every field as it is (for exact text), the same with punctuation and
    // symbols taken out (for words; fields a line apart, so a match stays in one), and each field without them or spaces.
    // The description apart (Description, folded with punctuation gone and as spaces): long text, so a word must start
    // one of its words ("ui" doesn't find "build"), and it isn't squeezed ("hero" doesn't find "the roster").
    public record Haystack(string Raw, string All, string[] Squeezed, string Description, string DescriptionSpaced);

    static Haystack HaystackOf(Mod mod)
    {
        var info = Store.InfoFor(mod);
        string?[] fields = [mod.manifest?.name, mod.manifest?.id, mod.manifest?.author, info?.Alias, info?.Notes, info?.Category,
            .. Tags.For(mod).Select(c => c.Text), .. WorkshopInfo.For(mod)?.Tags ?? []];
        var folded = fields.OfType<string>().Select(f => Fold(f, "")).ToList();
        // As the panel shows it: its markup's pairs taken out ("[gold]Art[/gold]" is "Art", not "gold"), a picture's
        // path with its tag, and [lb] and [rb] as the brackets they show.
        var description = mod.manifest?.description is { Length: > 0 } text
            ? ModDetails.WithoutTags(Picture.Replace(text, "")).Replace("[lb]", "[").Replace("[rb]", "]") : "";
        // What it changes in the game ("The Regent (energy orb)", "creature"), searched as its description is.
        var words = (description + " " + GameChanges.Words(mod)).Trim();
        return new Haystack(string.Join('\n', fields.Append(description)), string.Join('\n', folded), [.. folded.Select(Squeeze)],
            Fold(words, ""), Fold(words, " "));
    }

    static readonly Regex Picture = new(@"\[img[^\]]*\].*?\[/img\]", RegexOptions.Singleline | RegexOptions.IgnoreCase);

    // Letters, numbers and symbols stay ("C#", "C++", "&"), with the accents on them; a run of spaces is one space; the
    // punctuation of sentences (Grammatical) becomes between (nothing for a mod's text, so "Don't" reads "Dont"; a space
    // for a search, so "spire-mod" is two words). Compatibility forms first, so full-width "ＡＢＣ" reads "ABC" and "Ⅱ"
    // reads "II".
    static string Fold(string text, string between)
    {
        if (!text.IsNormalized(NormalizationForm.FormKC))
            text = text.Normalize(NormalizationForm.FormKC);
        var folded = new StringBuilder(text.Length);
        Span<char> pair = stackalloc char[2];
        var kept = false; // the last rune stayed, so an accent after it stays too (not one left after punctuation)
        foreach (var rune in text.EnumerateRunes())
        {
            var accent = Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
                or UnicodeCategory.EnclosingMark;
            if (accent ? kept : !Rune.IsWhiteSpace(rune) && !Grammatical(rune) && !Rune.IsControl(rune))
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

    // The punctuation of sentences, which a search reads past (Jordan: only grammatical characters): brackets, quotes,
    // dashes, and . , ; : ! ? ' … · ¡ ¿ / and the CJK 、。・. Every other symbol is part of a word ("C#" searches "c#").
    static bool Grammatical(Rune rune) => Rune.GetUnicodeCategory(rune) is UnicodeCategory.OpenPunctuation or UnicodeCategory.ClosePunctuation
        or UnicodeCategory.InitialQuotePunctuation or UnicodeCategory.FinalQuotePunctuation or UnicodeCategory.DashPunctuation
        || rune.Value is '.' or ',' or ';' or ':' or '!' or '?' or '\'' or '"' or '…' or '·' or '¡' or '¿' or '/' or '、' or '。' or '・';

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

    // A banner's Show: its search in the box, or the box emptied when it holds that search already (as a chip's click).
    public static void Toggle(Node near, string search) => Put(near, "", search, add: false);

    // Whether a search is this one, as a click compares them (Toggled): a banner's Show is lit while the box holds its own.
    public static bool Holds(string search, string wanted) => Units(wanted) is { Count: > 0 } pieces && Alike(Units(search), pieces);

    static void Put(Node near, string kind, string value, bool add)
    {
        if (CategoryList.ScreenOf(near)?.GetNodeOrNull<LineEdit>("ModsBorder/SpireModManagerSearch") is not { } box)
            return;
        var text = Toggled(box.Text, kind.Length == 0 ? value : TermFor(kind, value), add);
        box.Text = text;
        box.CaretColumn = text.Length;
        box.EmitSignal(LineEdit.SignalName.TextChanged, text);
    }

    // A tag or author as a search term: quoted where it has a space, a bracket or a "|", which would end it or open a group.
    public static string TermFor(string kind, string value) =>
        value.Any(c => char.IsWhiteSpace(c) || c is '(' or ')' or '|') ? $"{kind}\"{value}\"" : $"{kind}{value}";

    // The search after clicking a term, so a second click undoes it: a click searches for just the term (or clears it),
    // Shift-click adds or removes it. Terms compare by kind and value, any case, quotes ignored, and a "-" or NOT before
    // one is part of it. Brackets and operators stay as typed (Units).
    static string Toggled(string search, string term, bool add)
    {
        var units = Units(search);
        var wanted = Units(term);
        if (wanted.Count == 0)
            return search;
        if (!add)
            return Alike(units, wanted) ? "" : Joined(wanted);
        if (Remove(units, wanted))
            return Joined(units);
        // Left out until now ("-#tag"): it's put in instead.
        if (wanted.Count == 1)
            Remove(units, [wanted[0] with { Left = "-" + wanted[0].Left, Nots = wanted[0].Nots + 1 }]);
        return Joined([.. units, .. wanted]);
    }

    // A search's pieces as a click moves them: each term or "(" with the "-" or NOT that leaves it out (Left; Nots, how
    // many), and each ")" and OR or AND. A NOT before nothing stays a piece of its own. Operators are written back in
    // capitals ("-" and "|" as they are), which are operators anywhere: a lowercase one, an operator only between two
    // tags, could be left beside a word by a removal and read as one, flipping what's left.
    record Unit(Lexed Piece, string Left = "", int Nots = 0);

    static List<Unit> Units(string search)
    {
        var units = new List<Unit>();
        var (left, nots) = ("", 0);
        foreach (var piece in Lex(search))
        {
            if (piece.Piece == Piece.Not)
            {
                (left, nots) = (left + (piece.Text == "-" ? "-" : "NOT "), nots + 1);
                continue;
            }
            if (piece.Piece is not (Piece.Term or Piece.Open) && nots > 0)
                units.Add(new Unit(new Lexed(Piece.Not, left.TrimEnd())));
            units.Add(piece.Piece switch
            {
                Piece.Term or Piece.Open => new Unit(piece, left, nots),
                Piece.Or => new Unit(piece with { Text = piece.Text == "|" ? "|" : "OR" }),
                Piece.And => new Unit(piece with { Text = "AND" }),
                _ => new Unit(piece),
            });
            (left, nots) = ("", 0);
        }
        if (nots > 0)
            units.Add(new Unit(new Lexed(Piece.Not, left.TrimEnd())));
        return units;
    }

    // Two pieces are the same when they're left out alike and are the same kind with the same value, any case, quotes
    // aside (#"Library" is #library), or the same bracket or operator (OR is "|").
    static bool Same(Unit a, Unit b) => a.Nots == b.Nots && a.Piece.Piece == b.Piece.Piece
        && (a.Piece.Term is not { } x || b.Piece.Term is { } y && x.Kind == y.Kind && string.Equals(x.Value, y.Value, StringComparison.OrdinalIgnoreCase));

    static bool Alike(List<Unit> a, List<Unit> b) => a.Count == b.Count && a.Zip(b).All(p => Same(p.First, p.Second));

    // Takes each run of these pieces out of the search, with the OR (else AND) that joined it to the rest, so what's left
    // reads the same ("#a OR #b #c" without #b is "#a #c"); then brackets left empty, and an operator left with nothing
    // on one side. False if they weren't there.
    static bool Remove(List<Unit> units, List<Unit> these)
    {
        var found = false;
        bool Is(int at, Piece piece) => at >= 0 && at < units.Count && units[at].Piece.Piece == piece;
        for (var i = 0; i < units.Count;)
        {
            if (i + these.Count > units.Count || !Enumerable.Range(0, these.Count).All(j => Same(units[i + j], these[j])))
            {
                i++;
                continue;
            }
            found = true;
            var (from, count) = (i, these.Count);
            // The operator joining it: an OR beside it (OR binds closer than AND), else an AND; the one before it first.
            foreach (var (at, op) in new[] { (i - 1, Piece.Or), (i + count, Piece.Or), (i - 1, Piece.And), (i + count, Piece.And) })
                if (Is(at, op))
                {
                    (from, count) = (Math.Min(from, at), count + 1);
                    break;
                }
            units.RemoveRange(from, count);
            i = from;
        }
        if (!found)
            return false;
        // Whether the piece there ends a term or group (a term or ")"), or starts one (a term or "(").
        bool Closes(int at) => Is(at, Piece.Term) || Is(at, Piece.Close);
        bool Opens(int at) => Is(at, Piece.Term) || Is(at, Piece.Open);
        for (var i = 0; i < units.Count;)
        {
            if (Is(i, Piece.Open) && Is(i + 1, Piece.Close))
            {
                // An emptied group goes with what joined it, as a term does ("#a OR (#b) #c" without #b is "#a #c").
                var (start, length) = Is(i - 1, Piece.Or) ? (i - 1, 3) : Is(i + 2, Piece.Or) ? (i, 3)
                    : Is(i - 1, Piece.And) ? (i - 1, 3) : Is(i + 2, Piece.And) ? (i, 3) : (i, 2);
                units.RemoveRange(start, length);
                i = Math.Max(0, start - 1);
                continue;
            }
            var gone = units[i].Piece.Piece switch
            {
                Piece.Or or Piece.And when !Closes(i - 1) || !Opens(i + 1) => 1,
                Piece.Not => 1, // before nothing (Units)
                _ => 0,
            };
            if (gone == 0)
                i++;
            else
            {
                units.RemoveRange(i, gone);
                i = Math.Max(0, i - 1); // the piece before may be left hanging now
            }
        }
        return true;
    }

    // Pieces as one search: a space between each, none inside brackets ("(#a OR #b)"); a quote still being typed gets its
    // closing quote, so a term added after it stays a term of its own.
    static string Joined(IEnumerable<Unit> units)
    {
        var text = new StringBuilder();
        Piece? last = null;
        foreach (var unit in units)
        {
            if (last is not (null or Piece.Open) && unit.Piece.Piece != Piece.Close)
                text.Append(' ');
            text.Append(unit.Left).Append(Unclosed.IsMatch(unit.Piece.Text) ? unit.Piece.Text + "\"" : unit.Piece.Text);
            last = unit.Piece.Piece;
        }
        return text.ToString();
    }

    static readonly Regex Unclosed = new(@"^[#@]?""[^""]*$");

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
            MaxLength = 200,
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
