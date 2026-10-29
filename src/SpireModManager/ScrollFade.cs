using System.Runtime.CompilerServices;
using Godot;

namespace SpireModManager;

// A soft fade over the info panel's scrolling box at an edge its content runs on past, above or below (Jordan
// wanted a cue beside the scrollbar that there's more, such as a shadow or glow at the edge): the panel's colour,
// deepening to the edge, over the content and not its scrollbar. It's drawn with the box, in a canvas item of
// its own over everything in it (as GameText draws its boxes behind a label's text), so it follows every scroll
// and resize, and it's no control: it takes no clicks. Our views only.
static class ScrollFade
{
    public const float Height = 36;
    const float Most = 0.9f; // the fade's alpha at the very edge
    const int Steps = 6; // bands, each a straight ramp: together an eased one
    const int OverAll = 1 << 20; // the fade's draw index: after the box's children, so over them

    sealed class Drawing
    {
        public Rid Item;
        public bool Above, Below; // drawn last time: more content that way
        public Rect2 Room;
    }

    static readonly ConditionalWeakTable<Control, Drawing> Watched = [];

    // Once per box; bar is the one that scrolls it.
    public static void Watch(Control box, ScrollBar bar)
    {
        if (Watched.TryGetValue(box, out _))
            return;
        var drawing = new Drawing();
        Watched.Add(box, drawing);
        box.Draw += () => Draw(box, bar, drawing);
        // A scroll moves the content, not the box: drawn again then too.
        bar.ValueChanged += _ => box.QueueRedraw();
        bar.Changed += box.QueueRedraw;
        bar.VisibilityChanged += box.QueueRedraw;
        box.TreeExiting += () =>
        {
            if (drawing.Item.IsValid)
                RenderingServer.FreeRid(drawing.Item);
            drawing.Item = default;
        };
    }

    static void Draw(Control box, ScrollBar bar, Drawing drawing)
    {
        if (!drawing.Item.IsValid)
        {
            drawing.Item = RenderingServer.CanvasItemCreate();
            RenderingServer.CanvasItemSetParent(drawing.Item, box.GetCanvasItem());
            RenderingServer.CanvasItemSetDrawIndex(drawing.Item, OverAll);
        }
        RenderingServer.CanvasItemClear(drawing.Item);
        var shown = !Store.Data.VanillaView && bar.Visible;
        (drawing.Above, drawing.Below) = shown ? (bar.Value > 0.5, bar.Value + bar.Page < bar.MaxValue - 0.5) : (false, false);
        drawing.Room = new Rect2(Vector2.Zero, box.Size - new Vector2(bar.Visible ? bar.Size.X : 0, 0));
        var height = Math.Min(Height, drawing.Room.Size.Y / 3);
        if (drawing.Above)
            Band(drawing.Item, drawing.Room.Position.Y, height, drawing.Room.Size.X);
        if (drawing.Below)
            Band(drawing.Item, drawing.Room.End.Y, -height, drawing.Room.Size.X);
    }

    // The fade's colour along it, from its edge (0) into the content (1): the panel's, easing out to nothing.
    public static Color Shade(float along) => new(Layout.PanelColor, Most * (1 - along) * (1 - along));

    // From edge into the content by height (a negative height goes up).
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
    // Self-test: where the box last drew a fade (above, below), over what room, in the box's coordinates.
    public static (bool Above, bool Below, Rect2 Room)? TestDrawn(Control box) =>
        Watched.TryGetValue(box, out var drawing) ? (drawing.Above, drawing.Below, drawing.Room) : null;
#endif
}
