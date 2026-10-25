using Godot;

namespace SpireModManager;

// Small icons drawn in code: a notepad page, an "i" in a ring, the external-link mark and our padlock.
static class Icons
{
    static Texture2D? _notepad, _info;
    static readonly Dictionary<int, Texture2D> ExternalLinks = []; // by size
    static readonly Color PadlockGold = new(0.937f, 0.784f, 0.318f, 0.8f);

    // Our gold padlock, an arched shackle over a body (16 by 19 px), added to holder about a point of it: at, a fraction
    // of its size each way, then moved by offset px. It marks what of ours stays as it is: our rows' grip in Load Order
    // (they stay first). Only drawn: the mouse goes through.
    public static void AddPadlock(Control holder, Vector2 at, Vector2 offset = default)
    {
        void Part(Rect2 rect, StyleBox look)
        {
            var part = new Panel
            {
                AnchorLeft = at.X, AnchorRight = at.X, AnchorTop = at.Y, AnchorBottom = at.Y, MouseFilter = Control.MouseFilterEnum.Ignore,
                OffsetLeft = offset.X + rect.Position.X, OffsetRight = offset.X + rect.End.X,
                OffsetTop = offset.Y + rect.Position.Y, OffsetBottom = offset.Y + rect.End.Y,
            };
            part.AddThemeStyleboxOverride("panel", look);
            holder.AddChild(part);
        }
        var arch = new StyleBoxFlat { BgColor = Colors.Transparent, BorderColor = PadlockGold, DrawCenter = false };
        (arch.BorderWidthLeft, arch.BorderWidthRight, arch.BorderWidthTop, arch.BorderWidthBottom) = (2, 2, 2, 0);
        (arch.CornerRadiusTopLeft, arch.CornerRadiusTopRight) = (5, 5);
        Part(new Rect2(-5, -10, 10, 10), arch);
        var fill = new StyleBoxFlat { BgColor = PadlockGold };
        fill.SetCornerRadiusAll(2);
        Part(new Rect2(-8, -3, 16, 12), fill);
    }

    // A 14x14 ring with an "i" in it: a dot, then a stem.
    public static Texture2D Info => _info ??= Draw(14, 14, (x, y, w, h) =>
    {
        var (dx, dy) = (x + 0.5f - w / 2f, y + 0.5f - h / 2f);
        var r = MathF.Sqrt(dx * dx + dy * dy);
        var ring = MathF.Max(0, 1 - MathF.Abs(r - 5.5f)); // 1 px ring on the pixel centres, so it stays crisp
        var letter = x is 6 or 7 && (y is 3 or 4 || (y >= 6 && y <= 10));
        return letter ? 1f : ring;
    });

    // An 8x10 page: an outline with a folded top-right corner and two ruled lines.
    public static Texture2D Notepad => _notepad ??= Draw(8, 10, (x, y, w, h) =>
    {
        const int fold = 3;
        var inFold = x >= w - fold && y < fold;
        var edge = !inFold && (x == 0 || y == h - 1 || (x == w - 1 && y >= fold) || (y == 0 && x < w - fold));
        var foldLine = x - (w - fold) == y || (y == fold - 1 && x >= w - fold) || (x == w - fold && y < fold);
        var ruled = y is 4 or 6 && x >= 2 && x <= w - 3;
        return edge || foldLine ? 1f : ruled ? 0.7f : 0f;
    });

    // The external-link mark, size px square (LinkMark puts it after a link's text): a square open at its top-right
    // corner, and an arrow from its middle out through that corner. One of each size, made when first asked for.
    public static Texture2D ExternalLink(int size)
    {
        if (ExternalLinks.TryGetValue(size, out var made))
            return made;
        var mark = Draw(size, size, (x, y, w, _) =>
        {
            var last = w - 1;
            var right = last - 2; // the square's right edge; its top edge is row 2, so the arrow's tip is 2 px past its corner
            var reach = (w - 2) / 2; // how far its top and right edges come towards that corner
            var arm = (w - 1) * 2 / 5; // the arrowhead: along the top row and down the last column
            var square = (x == 0 && y >= 2) || (y == last && x <= right) || (y == 2 && x <= reach) || (x == right && y >= last - reach);
            var head = (y == 0 && x >= last - arm) || (x == last && y <= arm);
            var shaft = x + y == last && x >= right / 2;
            return square || head || shaft ? 1f : 0f;
        });
        // A path no file has: Godot's resource cache answers it before looking for one, so a RichTextLabel's [img]
        // can show the mark (LinkMark.Tag).
        mark.ResourcePath = $"res://{ModEntry.ModId}/drawn/external_link_{size}";
        return ExternalLinks[size] = mark;
    }

    static Texture2D Draw(int width, int height, Func<int, int, int, int, float> alpha)
    {
        var image = Image.CreateEmpty(width, height, false, Image.Format.Rgba8);
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                image.SetPixel(x, y, new Color(1, 1, 1, alpha(x, y, width, height)));
        return ImageTexture.CreateFromImage(image);
    }
}
