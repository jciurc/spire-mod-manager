using System.Runtime.CompilerServices;
using Godot;

namespace SpireModManager;

// The game's words inside our text, set apart as another voice than ours: a caption naming the source, in a soft orange,
// then the words alone in a box outlined and faintly filled in the same orange. The box's room is a table, the words in
// its middle cell, so a label that sizes its text to fit (the game's popup) sizes the box too, and the words wrap inside
// it. The table itself is clear: the box is drawn behind the words (Watch), Margin inside the table's edges, with an even
// Border all round and round corners. (A cell's own border is a thin line along the label's edges, cut off at the sides,
// with no width or radius to set.) BBCode: callers pass the words ready to show.
static class GameText
{
    public const string Hex = "#f5b56b";
    public const int Margin = 2, Border = 2, Radius = 4; // Jordan, 25 Oct: "increase game message radius to 4px"
    const int Inset = 16; // from the table's side to the words: the margin, the border and the room inside it

    // At the start of each box's words, a character that shows nothing and takes no room, one for each look: how Watch
    // finds the boxes in a label's text.
    const char Mark = (char)0x2060, DimMark = (char)0x2063; // the word joiner, the invisible separator
    const char ZeroWidth = (char)0x200b; // the zero-width space: in a box's side cells (Side), not a mark
    static readonly char[] Marks = [Mark, DimMark];

    // A cell's colour of #00000000 means the theme's: this one is as clear, and its own.
    const string Clear = "#ffffff00";

    // The theme's gaps around a table's cells, none in a label while its text has our boxes (Watch), and the label's own
    // again once it hasn't, so a mod's own tables keep theirs. A box whose words wrap fills the label's width, and the gap
    // took its table past the edge: the line break after it wrapped onto a line of its own, an empty line under the box.
    // Below the cell, the gap put the words off its middle.
    static readonly StringName[] TableGaps = ["table_h_separation", "table_v_separation"];

    // Over the box: the source's name ("Notice from the game", "From the game").
    public static string Caption(string text, bool centred = false) =>
        centred ? $"[center][color={Hex}]{text}[/color][/center]" : $"[color={Hex}]{text}[/color]";

    // The words in their box. dim: fainter, for words that aren't a problem now (a mod that's off). The room at the box's
    // sides is a cell of its own each side (Side), not the words' cell's padding: Godot lays a cell's words out as wide
    // as the label's room and adds that padding on top, so words that filled the room took the table past the label's
    // edge (the right side cut off, and the line break after it wrapped onto an empty line of its own). The padding holds
    // the room above and below the words.
    // centred: the words centred in the box (a centred label's alignment doesn't reach into a table's cell), for the
    // game's popup. Its auto-size measures the text as one paragraph as wide as the label, wider than a box's words, so the
    // popup's text is fitted again as laid out (LaunchGuard.SetBody).
    public static string Box(string bbcode, bool dim = false, bool centred = false)
    {
        var words = (dim ? DimMark : Mark) + bbcode;
        return $"[table=3]{Side}[cell expand=1 border={Clear} bg={Clear} padding=0,10,0,10]{(centred ? $"[center]{words}[/center]" : words)}[/cell]{Side}[/table]";
    }

    // A cell at the box's side, Inset wide: a table of its own in it, one cell padded that much around a zero-width space
    // (a space's width depends on the font; padding is what it's given, and a table's counts in its width). Every tag in
    // pairs, as the game's auto-size reads them (MegaLabelHelper.ParseBbcode).
    static readonly string Side = $"[cell border={Clear} bg={Clear}][table=1][cell border={Clear} bg={Clear} padding={Inset},0,0,0]{ZeroWidth}[/cell][/table][/cell]";

    static readonly StyleBoxFlat Lit = Look("b3", "1f"), Faint = Look("66", "0f");

    static StyleBoxFlat Look(string border, string fill)
    {
        var look = new StyleBoxFlat { BorderColor = Color.FromHtml(Hex + border), BgColor = Color.FromHtml(Hex + fill) };
        look.SetBorderWidthAll(Border);
        look.SetCornerRadiusAll(Radius);
        return look;
    }

    // What Watch draws in a label: a canvas item of its own, behind the label's text, and each box's line, table and box.
    // Gaps: the label's own table gaps from before it was watched (null: the theme's); Gapless: they're off now.
    sealed class Drawing
    {
        public Rid Item;
        public readonly List<(int Line, Rect2 Table, Rect2 Box, bool Dim)> Boxes = [];
        public int?[] Gaps = [];
        public bool Gapless;
    }

    static readonly ConditionalWeakTable<RichTextLabel, Drawing> Watched = [];

    // The boxes in label's text, drawn behind its words each time the label draws (its text, size or scroll changed), so
    // they keep to its layout; its tables without the theme's gaps while it has any (TableGaps). Once per label; a label
    // with no box in its text draws none.
    public static void Watch(RichTextLabel label)
    {
        if (Watched.TryGetValue(label, out _))
            return;
        var drawing = new Drawing { Gaps = [.. TableGaps.Select(gap => label.HasThemeConstantOverride(gap) ? label.GetThemeConstant(gap) : (int?)null)] };
        Watched.Add(label, drawing);
        Fit(label, drawing, label.GetParsedText()); // a card's text is in it already
        label.Draw += () => Draw(label, drawing);
        label.TreeExiting += () =>
        {
            if (drawing.Item.IsValid)
                RenderingServer.FreeRid(drawing.Item);
            drawing.Item = default;
        };
    }

    // The label's table gaps for its text now, as Watch sets them when the label next draws: for what measures the text
    // before then (the info panel places its fields under it).
    public static void Fit(RichTextLabel label)
    {
        if (Watched.TryGetValue(label, out var drawing))
            Fit(label, drawing, label.GetParsedText());
    }

    // None while the text has a box, the label's own otherwise. True if they changed: the text is laid out again.
    static bool Fit(RichTextLabel label, Drawing drawing, string text)
    {
        var boxed = text.IndexOfAny(Marks) >= 0;
        if (boxed == drawing.Gapless)
            return false;
        drawing.Gapless = boxed;
        for (var i = 0; i < TableGaps.Length; i++)
        {
            if ((boxed ? 0 : drawing.Gaps[i]) is { } gap)
                label.AddThemeConstantOverride(TableGaps[i], gap);
            else
                label.RemoveThemeConstantOverride(TableGaps[i]);
        }
        return true;
    }

    static void Draw(RichTextLabel label, Drawing drawing)
    {
        drawing.Boxes.Clear();
        if (drawing.Item.IsValid)
            RenderingServer.CanvasItemClear(drawing.Item);
        var text = label.GetParsedText();
        // Its text gained or lost its boxes since (the game's popup sets its own text): laid out again with the gaps for
        // it, and drawn again, asked for after this draw (one asked for while the label draws is dropped).
        if (Fit(label, drawing, text))
        {
            label.CallDeferred(CanvasItem.MethodName.QueueRedraw);
            return;
        }
        if (text.IndexOfAny(Marks) < 0)
            return;
        if (!drawing.Item.IsValid)
        {
            drawing.Item = RenderingServer.CanvasItemCreate();
            RenderingServer.CanvasItemSetParent(drawing.Item, label.GetCanvasItem());
            RenderingServer.CanvasItemSetDrawBehindParent(drawing.Item, true);
        }
        foreach (var (line, table, dim) in Tables(label, text))
        {
            var box = table.Grow(-Margin);
            (dim ? Faint : Lit).Draw(drawing.Item, box);
            drawing.Boxes.Add((line, table, box, dim));
        }
    }

    // Each box's table in the label's own coordinates, as the label has laid it out. Godot puts a table on a line of its
    // own, as one object: so it's that line, as tall, and as wide, kept to the label's text room (a table can run past
    // it), on whole pixels of the screen inside its edges so the border is as crisp on every side.
    static IEnumerable<(int Line, Rect2 Table, bool Dim)> Tables(RichTextLabel label, string text)
    {
        var style = label.GetThemeStylebox("normal");
        var bar = label.GetVScrollBar();
        var room = new Rect2(style.GetOffset(), label.Size - style.GetMinimumSize() - new Vector2(bar.Visible ? bar.Size.X : 0, 0));
        // A label taller than its text places the text as its alignment says (the game's popup centres it).
        var spare = Math.Max(0, room.Size.Y - label.GetContentHeight());
        var top = room.Position.Y - (float)bar.Value + label.VerticalAlignment switch
        {
            VerticalAlignment.Center => spare / 2,
            VerticalAlignment.Bottom => spare,
            _ => 0,
        };
        var across = label.HorizontalAlignment switch { HorizontalAlignment.Center => 0.5f, HorizontalAlignment.Right => 1f, _ => 0f };
        for (var at = text.IndexOfAny(Marks); at >= 0; at = text.IndexOfAny(Marks, at + 1))
        {
            var line = label.GetCharacterLine(at);
            if (line < 0)
                continue; // not laid out
            var width = label.GetLineWidth(line);
            var left = Math.Max(room.Position.X, room.Position.X + MathF.Floor((room.Size.X - width) * across));
            var right = Math.Min(room.End.X, left + width);
            yield return (line, Snapped(label, new Rect2(left, top + label.GetLineOffset(line), right - left, label.GetLineHeight(line))), text[at] == DimMark);
        }
    }

    static Rect2 Snapped(CanvasItem item, Rect2 rect)
    {
        var screen = item.GetScreenTransform();
        var back = screen.AffineInverse();
        var (from, to) = (back * (screen * rect.Position).Ceil(), back * (screen * rect.End).Floor());
        return new Rect2(from, to - from);
    }

#if DEVTOOLS
    // Self-test: the boxes Watch drew in a label when it last drew (each one's line, its table, its box, and whether it's
    // dim), in the label's coordinates, and the look of each kind.
    public static IReadOnlyList<(int Line, Rect2 Table, Rect2 Box, bool Dim)> Drawn(RichTextLabel label) =>
        Watched.TryGetValue(label, out var drawing) ? drawing.Boxes : [];

    public static StyleBoxFlat Style(bool dim) => dim ? Faint : Lit;
#endif
}
