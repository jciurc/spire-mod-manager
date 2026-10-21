using Godot;
using MegaCrit.Sts2.Core.Logging;

namespace SpireModManager;

// The tags box while it's edited (ModField): each finished tag, one with a comma after it, drawn as a pill like its
// chip at rest and in its colour, the commas faint, the word being typed plain. The box stays one LineEdit holding
// "A, B, word", so typing, the caret, selection, undo, the IME and saving are Godot's and ModField's as before. The
// pills and the text are drawn in the box's Draw signal, which runs right after LineEdit's own drawing, from the same
// text shaped the same way, so they sit where it put its text, caret and scroll; its own glyphs are transparent
// meanwhile (its selected ones, the caret, the IME's underline and the placeholder stay its own). The keys that know
// about tags (Use): Backspace and Delete take a whole pill, Copy and Cut keep to whole tags, Paste and a typed comma
// (a full-width one too) make pills, and a double-click selects the whole tag.
static class TagPills
{
    // px of a pill either side of its tag: ", " leaves about 8 between tags at 18, and its comma sits at the left of that
    public const float PadLeft = 3, PadRight = 1;
    const float Faint = 0.3f; // the commas' alpha
    static readonly StringName CopyAction = "ui_copy", CutAction = "ui_cut", PasteAction = "ui_paste", BackAction = "ui_text_backspace",
        BackWordAction = "ui_text_backspace_word", DeleteAction = "ui_text_delete", DeleteWordAction = "ui_text_delete_word";

    static bool _failed; // drawing threw once: every tags box shows its plain text from then on
    static LineEdit? _doubleClicked; // the box a double-click went to, until its button comes up

    // Pills in the box from now on, and its own glyphs transparent while it has focus.
    public static void Attach(LineEdit box)
    {
        var ink = box.GetThemeColor("font_color"); // its own (cream): the word being typed, and the commas faint
        var shaper = new TextLine { Direction = TextServer.Direction.Ltr, TextOverrunBehavior = TextServer.OverrunBehavior.NoTrimming };
        // Dragging text would strand ", ,", and the right-click menu's Copy, Cut and Paste don't come through Use.
        (box.DragAndDropSelectionEnabled, box.ContextMenuEnabled) = (false, false);
        box.FocusEntered += () => Ink(box, ink);
        box.FocusExited += () => Ink(box, ink);
        box.Draw += () => Paint(box, shaper, ink);
    }

#if DEVTOOLS
    // Self-test: what the box drew last (nothing out of focus): its pills left to right, each tag with its rect in the
    // box, and its text in runs of one colour (not the selected part: LineEdit draws that).
    const string PillsMeta = "SpireModManagerTagPills", InkMeta = "SpireModManagerTagInk";

    public static List<(string Tag, Rect2 Rect)> TestPills(LineEdit box) => box.HasMeta(PillsMeta)
        ? box.GetMeta(PillsMeta).AsGodotArray().Chunk(2).Select(p => (p[0].AsString(), p[1].AsRect2())).ToList() : [];

    public static List<(string Text, Color Color)> TestInk(LineEdit box) => box.HasMeta(InkMeta)
        ? box.GetMeta(InkMeta).AsGodotArray().Chunk(2).Select(p => (p[0].AsString(), p[1].AsColor())).ToList() : [];

    static void Record(LineEdit box, List<(string Tag, Rect2 Rect)> pills, List<(string Text, Color Color)> ink)
    {
        box.SetMeta(PillsMeta, new Godot.Collections.Array(pills.SelectMany(p => new Variant[] { p.Tag, p.Rect })));
        box.SetMeta(InkMeta, new Godot.Collections.Array(ink.SelectMany(r => new Variant[] { r.Text, r.Color })));
    }

    // Self-test: the clipboard Copy, Cut and Paste use while it's set (a headless run has none, and a windowed run's is
    // your desktop's). What LineEdit copies itself still goes to the real one.
    public static string? TestClipboard { get; set; }

    static string Clipboard
    {
        get => TestClipboard ?? DisplayServer.ClipboardGet();
        set
        {
            if (TestClipboard != null)
                TestClipboard = value;
            else
                DisplayServer.ClipboardSet(value);
        }
    }
#else
    static string Clipboard { get => DisplayServer.ClipboardGet(); set => DisplayServer.ClipboardSet(value); }
#endif

    // Whether Paint draws the box's text: while it has focus, unless drawing failed or the box is composing text Paint
    // can't see (Godot's own Alt+number input, which the IME knows nothing of).
    static bool Own(LineEdit box) => !_failed && box.HasFocus() && !(box.HasImeText() && DisplayServer.ImeGetText().Length == 0);

    // The box's own glyphs transparent while Paint draws its text, in ink otherwise.
    static void Ink(LineEdit box, Color ink)
    {
        if (!GodotObject.IsInstanceValid(box))
            return; // freed before a deferred call
        var color = Own(box) ? Colors.Transparent : ink;
        if (box.GetThemeColor("font_color") != color)
            box.AddThemeColorOverride("font_color", color);
    }

    // Over what LineEdit just drew: the pills, then the text in its colours. Any failure: the plain box, logged once.
    static void Paint(LineEdit box, TextLine shaper, Color ink)
    {
        if (_failed)
            return;
        try
        {
            if (Own(box) != (box.GetThemeColor("font_color").A == 0))
                Callable.From(() => Ink(box, ink)).CallDeferred(); // not now: a redraw asked for while drawing is lost
            Draw(box, shaper, ink);
        }
        catch (Exception e)
        {
            _failed = true;
            Callable.From(() => Ink(box, ink)).CallDeferred();
            Log.Warn($"[{ModEntry.ModId}] Could not draw the tags as pills, so the tags box shows them as plain text (logged once): {e.Message}");
        }
    }

    static void Draw(LineEdit box, TextLine shaper, Color ink)
    {
#if DEVTOOLS
        var pills = new List<(string Tag, Rect2 Rect)>();
        var inked = new List<(string Text, Color Color)>();
#endif
        var text = box.Text;
        var ime = box.HasImeText() ? DisplayServer.ImeGetText() : "";
        if (!box.HasFocus() || (text.Length == 0 && ime.Length == 0) || (box.HasImeText() && ime.Length == 0))
        {
#if DEVTOOLS
            Record(box, pills, inked);
#endif
            return;
        }
        var caret = box.CaretColumn;
        var line = text[..caret] + ime + text[caret..]; // what LineEdit shapes: the composition at the caret
        int InLine(int column) => column >= caret ? column + ime.Length : column;
        shaper.Clear();
        shaper.AddString(line, box.GetThemeFont("font"), box.GetThemeFontSize("font_size"), box.Language);
        var server = TextServerManager.GetPrimaryInterface();
        var rid = shaper.GetRid();
        // Where LineEdit puts the line (line_edit.cpp, NOTIFICATION_DRAW, left-aligned, its whole pixels kept): by its
        // "normal" box whatever it draws, between the margins and the clear icon, centred, scrolled by GetScrollOffset
        // (pixels, 0 or less).
        var normal = box.GetThemeStylebox("normal");
        var left = (int)normal.GetMargin(Side.Left);
        var right = (int)((int)box.Size.X - normal.GetMargin(Side.Right)) - (box.ClearButtonEnabled && box.Editable ? box.GetThemeIcon("clear").GetWidth() : 0);
        var height = server.ShapedTextGetSize(rid).Y;
        var top = (int)(normal.GetOffset().Y + ((int)((int)box.Size.Y - normal.GetMinimumSize().Y) - height) / 2);
        var x0 = left + box.GetScrollOffset();
        var band = new Rect2(left - PadLeft, 0, right - left + PadLeft + PadRight, box.Size.Y); // the text's, and a pill's padding (in the margins)

        foreach (var (start, end, finished) in Segments(text))
        {
            if (!finished)
                continue;
            foreach (var span in server.ShapedTextGetSelection(rid, InLine(start), InLine(end)))
            {
                var rect = new Rect2(x0 + span.X, top, span.Y - span.X, height).GrowIndividual(PadLeft, 0, PadRight, 0).Intersection(band);
                if (!rect.HasArea())
                    continue;
                box.DrawStyleBox(Tags.PillBox, rect);
#if DEVTOOLS
                pills.Add((text[start..end], rect));
#endif
            }
        }

        // The glyphs as LineEdit draws them (only those wholly inside the band), unless it's drawing its own just now
        // (the switch to transparent waits for the frame's end).
        if (box.GetThemeColor("font_color").A == 0)
        {
            var inks = Inks(text, ink);
            Color[] lineInks = [.. inks[..caret], .. Enumerable.Repeat(ink, ime.Length), .. inks[caret..]];
            var (from, to) = box.HasSelection() ? (box.GetSelectionFromColumn(), box.GetSelectionToColumn()) : (0, 0);
            var canvas = box.GetCanvasItem();
            var at = new Vector2(x0, top + (float)server.ShapedTextGetAscent(rid));
#if DEVTOOLS
            var inkedUpTo = 0; // in line: a cluster of several glyphs is its text once
#endif
            foreach (var glyph in server.ShapedTextGetGlyphs(rid))
            {
                int start = glyph["start"].AsInt32(), end = glyph["end"].AsInt32(), repeat = glyph["repeat"].AsInt32();
                var advance = glyph["advance"].AsSingle();
                var tint = start >= 0 && start < lineInks.Length ? lineInks[start] : ink;
                var selected = to > from && start >= from && end <= to; // LineEdit draws those, in its selected colour
                for (var i = 0; i < repeat; i++)
                {
                    if (!selected && MathF.Ceiling(at.X) >= left && at.X + advance <= right)
                    {
                        DrawGlyph(server, canvas, glyph, at, tint);
#if DEVTOOLS
                        if (end > inkedUpTo)
                        {
                            var piece = line[Math.Max(start, inkedUpTo)..end];
                            if (inked.Count > 0 && inked[^1].Color == tint)
                                inked[^1] = (inked[^1].Text + piece, tint);
                            else
                                inked.Add((piece, tint));
                            inkedUpTo = end;
                        }
#endif
                    }
                    at.X += advance;
                }
                if (at.X >= right)
                    break;
            }
        }
#if DEVTOOLS
        Record(box, pills, inked);
#endif
    }

    // One glyph: from its font, or as a box with its code where no font has it.
    static void DrawGlyph(TextServer server, Rid canvas, Godot.Collections.Dictionary glyph, Vector2 at, Color color)
    {
        var (font, size, index) = (glyph["font_rid"].AsRid(), glyph["font_size"].AsInt64(), glyph["index"].AsInt64());
        at += glyph["offset"].AsVector2();
        if (font.IsValid)
            server.FontDrawGlyph(font, canvas, size, at, index, color);
        else if ((glyph["flags"].AsInt64() & (long)(TextServer.GraphemeFlag.Virtual | TextServer.GraphemeFlag.EmbeddedObject)) == 0)
            server.DrawHexCodeBox(canvas, size, at, index, color);
    }

    // The colour of each column of the text: a finished tag's in the tag colour, the word being typed in ink, the rest
    // (each comma and the spaces about it) faint.
    static Color[] Inks(string text, Color ink)
    {
        var inks = new Color[text.Length];
        Array.Fill(inks, new Color(ink, Faint));
        foreach (var (start, end, finished) in Segments(text))
            Array.Fill(inks, finished ? Tags.CustomColor : ink, start, end - start);
        return inks;
    }

    // A key or click in the box (ModField's GuiInput, after the suggestions'): true when it was taken; accepted then, it
    // never reaches LineEdit (the gui_input signal comes before its own handling). Keys match LineEdit's own actions the
    // way it does (exactly), and composing (IME) text is left alone.
    public static bool Use(LineEdit box, InputEvent e)
    {
        switch (e)
        {
            // LineEdit selects the word on the press, and on the release drops any other selection: the whole tag after.
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true, DoubleClick: true }:
                _doubleClicked = box;
                return false;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false } when _doubleClicked == box:
                _doubleClicked = null;
                Callable.From(() => WholeTag(box)).CallDeferred();
                return false;
        }
        if (e is not InputEventKey key || box.HasImeText())
            return false;
        if (key.IsAction(CopyAction, true) || key.IsAction(CutAction, true))
            return Copy(box, key);
        if (!key.Pressed || !box.IsEditing())
            return false;
        if (key.IsAction(PasteAction, true))
            return Paste(box);
        if (Comma(key.Unicode) && !key.CtrlPressed && !key.AltPressed && !key.MetaPressed)
        {
            var (from, to) = Selected(box);
            Replace(box, from, to, ", ");
            return true;
        }
        if (box.HasSelection())
            return false;
        var (text, caret) = (box.Text, box.CaretColumn);
        var pill = key.IsAction(BackAction, true) || key.IsAction(BackWordAction, true) ? PillBefore(text, caret)
            : key.IsAction(DeleteAction, true) || key.IsAction(DeleteWordAction, true) ? PillAfter(text, caret) : null;
        if (pill is not { } range)
            return false;
        // A fresh press takes the pill and its separator, one undo step; a held key's repeats stop at it.
        if (!key.Echo)
            box.DeleteText(range.From, range.To);
        return true;
    }

    // A comma typed: ASCII, or a Chinese or Japanese IME's full-width one. Each becomes ", ".
    public static bool Comma(long unicode) => unicode is ',' or '，' or '、';

    // Puts text in place of from..to, the caret after it, as one undo step: DeleteText has the box announce the change
    // and take its undo step at the end of the frame, after the insert (InsertTextAtCaret alone does neither).
    public static void Replace(LineEdit box, int from, int to, string text)
    {
        box.Deselect();
        box.DeleteText(from, to);
        box.CaretColumn = from;
        box.InsertTextAtCaret(text);
    }

    static (int From, int To) Selected(LineEdit box) =>
        box.HasSelection() ? (box.GetSelectionFromColumn(), box.GetSelectionToColumn()) : (box.CaretColumn, box.CaretColumn);

    // Ctrl+C or Ctrl+X on whole tags: the copy rule's text on the clipboard, and Cut takes the tags with a separator.
    // The key's release is taken too: LineEdit copies again on it (its action matches either way).
    static bool Copy(LineEdit box, InputEventKey key)
    {
        if (!box.HasSelection() || Copied(box.Text, box.GetSelectionFromColumn(), box.GetSelectionToColumn()) is not { } copied)
            return false; // part of a tag: native
        if (!key.Pressed)
            return true;
        Clipboard = copied;
        if (key.IsAction(CutAction, true))
        {
            var (from, to) = CutRange(box.Text, box.GetSelectionFromColumn(), box.GetSelectionToColumn());
            box.Deselect();
            box.DeleteText(from, to); // one undo step
        }
        return true;
    }

    // Ctrl+V of a list: its tags the box hasn't got, as pills, in place of the selection, as many whole ones as fit.
    // Text with no separator in it: native.
    static bool Paste(LineEdit box)
    {
        var (from, to) = Selected(box);
        var rest = box.Text[..from] + box.Text[to..];
        var room = box.MaxLength > 0 ? box.MaxLength - rest.Length : int.MaxValue;
        if (PasteText(rest, from, Clipboard, room) is not { } paste)
            return false;
        if (paste.Length > 0)
            Replace(box, from, to, paste);
        return true;
    }

    // Widens a selection inside one tag (a double-click's word, "Story" of "Story-heavy") to the whole tag.
    static void WholeTag(LineEdit box)
    {
        if (!GodotObject.IsInstanceValid(box) || !box.HasSelection())
            return;
        var (from, to) = (box.GetSelectionFromColumn(), box.GetSelectionToColumn());
        foreach (var (start, end, _) in Segments(box.Text))
        {
            if (start > from || to > end)
                continue;
            box.Select(start, end);
            box.CaretColumn = end;
            return;
        }
    }

    // --- The text rules: columns of the box's text, no Godot ---

    // Its tags as column ranges, in order, blanks left out; Finished: a comma follows (a pill). An unfinished last one
    // is the word being typed.
    static List<(int Start, int End, bool Finished)> Segments(string text)
    {
        var list = new List<(int, int, bool)>();
        for (var start = 0; ;)
        {
            var comma = text.IndexOf(',', start);
            int s = start, e = comma < 0 ? text.Length : comma;
            while (s < e && char.IsWhiteSpace(text[s]))
                s++;
            while (e > s && char.IsWhiteSpace(text[e - 1]))
                e--;
            if (e > s)
                list.Add((s, e, comma >= 0));
            if (comma < 0)
                return list;
            start = comma + 1;
        }
    }

    // The clipboard text for a selection of whole tags: "A, B", and a comma after the last when another value follows
    // it, so a paste makes it a pill ("A, B, " copies as "A, B": blanks aren't values). Null when the selection starts
    // or ends inside a tag, or holds none: copied natively.
    static string? Copied(string text, int from, int to)
    {
        var all = Segments(text);
        var picked = all.Where(t => t.End > from && t.Start < to).ToList();
        if (picked.Count == 0 || picked[0].Start < from || picked[^1].End > to)
            return null;
        var more = all.Any(t => t.Start > picked[^1].End);
        return string.Join(", ", picked.Select(t => text[t.Start..t.End])) + (more ? "," : "");
    }

    // What Cut takes for a selection of whole tags (Copied isn't null): from the first to the next value, the
    // separator with it, or to the end.
    static (int From, int To) CutRange(string text, int from, int to)
    {
        var all = Segments(text);
        var picked = all.Where(t => t.End > from && t.Start < to).ToList();
        var next = all.FirstOrDefault(t => t.Start > picked[^1].End);
        return (picked[0].Start, next == default ? text.Length : next.Start);
    }

    // What a Backspace at the caret takes: the pill before it and its separator, when only spaces lie between that
    // pill's comma and the caret. Null: native.
    static (int From, int To)? PillBefore(string text, int caret)
    {
        var comma = text[..caret].TrimEnd().Length - 1;
        if (comma < 0 || text[comma] != ',')
            return null;
        var pill = Segments(text).LastOrDefault(t => t.End <= comma);
        return pill.Finished && text.IndexOf(',', pill.End) == comma ? (pill.Start, SeparatorEnd(text, comma)) : null;
    }

    // What a Delete at the caret takes, PillBefore's mirror: the pill after it and the separator before that pill, when
    // only spaces lie between the caret and the separator's comma. Null: native, so a held Delete from inside a tag
    // takes its letters and stops at its comma, as a held Backspace does from inside one.
    static (int From, int To)? PillAfter(string text, int caret)
    {
        var comma = text.Length - text[caret..].TrimStart().Length;
        if (comma == text.Length || text[comma] != ',')
            return null;
        var pill = Segments(text).FirstOrDefault(t => t.Start > comma);
        return pill.Finished && text.LastIndexOf(',', pill.Start - 1) == comma ? (text[..comma].TrimEnd().Length, pill.End) : null;
    }

    // Past a comma and the spaces after it.
    static int SeparatorEnd(string text, int comma)
    {
        var end = comma + 1;
        while (end < text.Length && char.IsWhiteSpace(text[end]))
            end++;
        return end;
    }

    // What a paste of clip at the caret inserts: its tags (between commas, full-width ones, lines or tabs), less those
    // the text has (any case) and repeats, with a separator before them where a tag ends at the caret and one after,
    // so they're pills; dropped from the end until it fits in room. Null when clip has no separator: pasted natively.
    static string? PasteText(string text, int caret, string clip, int room)
    {
        var parts = clip.Split([',', '，', '、', '\n', '\r', '\t']);
        if (parts.Length < 2)
            return null;
        var have = Segments(text).Select(t => text[t.Start..t.End]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tags = parts.Select(p => p.Trim()).Where(p => p.Length > 0 && !have.Contains(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var head = text[..caret];
        var before = head.TrimEnd().Length == 0 ? "" : head.TrimEnd().EndsWith(',') ? (char.IsWhiteSpace(head[^1]) ? "" : " ") : ", ";
        var after = text[caret..].TrimStart().StartsWith(',') ? "" : ", ";
        while (tags.Count > 0 && before.Length + string.Join(", ", tags).Length + after.Length > room)
            tags.RemoveAt(tags.Count - 1);
        return tags.Count == 0 ? "" : before + string.Join(", ", tags) + after;
    }
}
