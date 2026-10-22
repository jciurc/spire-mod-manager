using System.Runtime.CompilerServices;
using Godot;

namespace SpireModManager;

// The game's words inside our text, set apart as another voice than ours: a caption naming the source, in a soft orange,
// then the words alone in a box outlined and faintly filled in the same orange. The box's room is a one-cell table, so a
// label that sizes its text to fit (the game's popup) sizes the box too, and the words wrap inside it. The table itself
// is clear: the box is drawn behind the words (Watch), Margin inside the table's edges, with an even Border all round and
// round corners. (A cell's own border is a thin line along the label's edges, cut off at the sides, with no width or
// radius to set.) BBCode: callers pass the words ready to show.
static class GameText
{
    public const string Hex = "#f5b56b";
    public const int Margin = 2, Border = 2, Radius = 2;

    // At the start of each box's words, a character that shows nothing and takes no room, one for each look: how Watch
    // finds the boxes in a label's text.
    const char Mark = (char)0x2060, DimMark = (char)0x2063; // the word joiner, the invisible separator
    static readonly char[] Marks = [Mark, DimMark];

    // A cell's colour of #00000000 means the theme's: this one is as clear, and its own.
    const string Clear = "#ffffff00";

    // The theme's gaps around a table's cells, none in a label with our boxes (Watch). A box whose words wrap fills the
    // label's width, and the gap took its table past the edge: the line break after it wrapped onto a line of its own, an
    // empty line under the box. Below the cell, the gap put the words off its middle.
    public static readonly StringName[] TableGaps = ["table_h_separation", "table_v_separation"];

    // Over the box: the source's name ("Notice from the game", "From the game").
    public static string Caption(string text, bool centred = false) =>
        centred ? $"[center][color={Hex}]{text}[/color][/center]" : $"[color={Hex}]{text}[/color]";

    // The words in their box. dim: fainter, for words that aren't a problem now (a mod that's off). centred: the words
    // centred in the box (a centred label's alignment doesn't reach into a table's cell). Godot wraps a cell's lines to
    // its width less both paddings but centres them over the whole width, then shifts them by the left padding: so
    // centred, the sides' padding all goes on the right, and the lines wrap the same and sit in the middle. The paddings
    // hold the margin and the border too, so the words keep their room inside the border.
    public static string Box(string bbcode, bool dim = false, bool centred = false)
    {
        var words = (dim ? DimMark : Mark) + bbcode;
        return $"[table=1][cell expand=1 border={Clear} bg={Clear} padding={(centred ? "0,10,32,10" : "16,10,16,10")}]"
            + $"{(centred ? $"[center]{words}[/center]" : words)}[/cell][/table]";
    }

    static readonly StyleBoxFlat Lit = Look("b3", "1f"), Faint = Look("66", "0f");

    static StyleBoxFlat Look(string border, string fill)
    {
        var look = new StyleBoxFlat { BorderColor = Color.FromHtml(Hex + border), BgColor = Color.FromHtml(Hex + fill) };
        look.SetBorderWidthAll(Border);
        look.SetCornerRadiusAll(Radius);
        return look;
    }

    // What Watch draws in a label: a canvas item of its own, behind the label's text, and each box's line, table and box.
    sealed class Drawing
    {
        public Rid Item;
        public readonly List<(int Line, Rect2 Table, Rect2 Box, bool Dim)> Boxes = [];
    }

    static readonly ConditionalWeakTable<RichTextLabel, Drawing> Watched = [];

    // The boxes in label's text, drawn behind its words each time the label draws (its text, size or scroll changed), so
    // they keep to its layout; its tables without the theme's gaps (TableGaps). Once per label; a label with no box in
    // its text draws none.
    public static void Watch(RichTextLabel label)
    {
        if (Watched.TryGetValue(label, out _))
            return;
        foreach (var gap in TableGaps)
            label.AddThemeConstantOverride(gap, 0);
        var drawing = new Drawing();
        Watched.Add(label, drawing);
        label.Draw += () => Draw(label, drawing);
        label.TreeExiting += () =>
        {
            if (drawing.Item.IsValid)
                RenderingServer.FreeRid(drawing.Item);
            drawing.Item = default;
        };
    }

    static void Draw(RichTextLabel label, Drawing drawing)
    {
        drawing.Boxes.Clear();
        if (drawing.Item.IsValid)
            RenderingServer.CanvasItemClear(drawing.Item);
        var text = label.GetParsedText();
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
