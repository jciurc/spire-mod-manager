using System.Runtime.CompilerServices;
using Godot;

namespace SpireModManager;

// A soft fade over the info panel's text at an edge it runs on past, above or below, while it scrolls (Jordan: "some
// visual indicator showing there's more content below or above along with the scrollbar. maybe a upwards dropshadow or
// glow at edge of container"): the panel's colour, deepening to the edge, over the text and not its scrollbar. It's
// drawn with the label, in a canvas item of its own over the label's text (as GameText draws its boxes behind it), so
// it follows every scroll and resize, and it's no control: it takes no clicks. Our views only.
static class ScrollFade
{
    public const float Height = 36;
    const float Most = 0.9f; // the fade's alpha at the very edge
    const int Steps = 6; // bands, each a straight ramp: together an eased one

    sealed class Drawing
    {
        public Rid Item;
        public bool Above, Below; // drawn last time: more text that way
        public Rect2 Room;
    }

    static readonly ConditionalWeakTable<RichTextLabel, Drawing> Watched = [];

    // Once per label.
    public static void Watch(RichTextLabel label)
    {
        if (Watched.TryGetValue(label, out _))
            return;
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
        if (!drawing.Item.IsValid)
        {
            drawing.Item = RenderingServer.CanvasItemCreate();
            RenderingServer.CanvasItemSetParent(drawing.Item, label.GetCanvasItem());
        }
        RenderingServer.CanvasItemClear(drawing.Item);
        var bar = label.GetVScrollBar();
        var shown = !Store.Data.VanillaView && bar.Visible;
        (drawing.Above, drawing.Below) = shown ? (bar.Value > 0.5, bar.Value + bar.Page < bar.MaxValue - 0.5) : (false, false);
        drawing.Room = new Rect2(Vector2.Zero, label.Size - new Vector2(bar.Visible ? bar.Size.X : 0, 0));
        var height = Math.Min(Height, drawing.Room.Size.Y / 3);
        if (drawing.Above)
            Band(drawing.Item, drawing.Room.Position.Y, height, drawing.Room.Size.X);
        if (drawing.Below)
            Band(drawing.Item, drawing.Room.End.Y, -height, drawing.Room.Size.X);
    }

    // The fade's colour along it, from its edge (0) into the text (1): the panel's, easing out to nothing.
    public static Color Shade(float along) => new(Layout.PanelColor, Most * (1 - along) * (1 - along));

    // From edge into the text by height (a negative height goes up).
    static void Band(Rid item, float edge, float height, float width)
    {
        for (var i = 0; i < Steps; i++)
        {
            var (from, to) = (i / (float)Steps, (i + 1) / (float)Steps);
            var (y0, y1) = (edge + height * from, edge + height * to);
            RenderingServer.CanvasItemAddPolygon(item, [new(0, y0), new(width, y0), new(width, y1), new(0, y1)],
                [Shade(from), Shade(from), Shade(to), Shade(to)]);
        }
    }

#if DEVTOOLS
    // Self-test: where the label last drew a fade (above, below), over what room, in the label's coordinates.
    public static (bool Above, bool Below, Rect2 Room)? TestDrawn(RichTextLabel label) =>
        Watched.TryGetValue(label, out var drawing) ? (drawing.Above, drawing.Below, drawing.Room) : null;
#endif
}
