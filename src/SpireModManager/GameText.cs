namespace SpireModManager;

// The game's words inside our text, set apart as another voice than ours: a caption naming the source, in a soft orange,
// then the words alone in a box outlined and faintly filled in the same orange. The box is a one-cell table, so a label
// that sizes its text to fit (the game's popup) sizes the box too. BBCode: callers pass the words ready to show.
static class GameText
{
    public const string Hex = "#f5b56b";

    // Over the box: the source's name ("Notice from the game", "From the game").
    public static string Caption(string text, bool centred = false) =>
        centred ? $"[center][color={Hex}]{text}[/color][/center]" : $"[color={Hex}]{text}[/color]";

    // The words in their box. dim: fainter, for words that aren't a problem now (a mod that's off). centred: the words
    // centred in the box (a centred label's alignment doesn't reach into a table's cell). Godot wraps a cell's lines to
    // its width less both paddings but centres them over the whole width, then shifts them by the left padding: so
    // centred, the sides' padding all goes on the right, and the lines wrap the same and sit in the middle.
    public static string Box(string bbcode, bool dim = false, bool centred = false) =>
        $"[table=1][cell expand=1 border={Hex}{(dim ? "66" : "b3")} bg={Hex}{(dim ? "0f" : "1f")} padding={(centred ? "0,6,24,6" : "12,6,12,6")}]{(centred ? $"[center]{bbcode}[/center]" : bbcode)}[/cell][/table]";
}
