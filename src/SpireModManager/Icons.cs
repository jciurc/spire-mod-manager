using Godot;

namespace SpireModManager;

// Tiny icons drawn in code (no image files): a notepad page for rows with the player's alias or notes, and an
// "i" in a ring for a note on how something works.
static class Icons
{
    static Texture2D? _notepad, _info;

    // A 14x14 ring with an "i" in it: a dot, then a stem.
    public static Texture2D Info => _info ??= Draw(14, 14, (x, y, w, h) =>
    {
        var (dx, dy) = (x + 0.5f - w / 2f, y + 0.5f - h / 2f);
        var r = MathF.Sqrt(dx * dx + dy * dy);
        var ring = MathF.Max(0, 1 - MathF.Abs(r - 6f)); // a 1 px ring, soft at its edges
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

    static Texture2D Draw(int width, int height, Func<int, int, int, int, float> alpha)
    {
        var image = Image.CreateEmpty(width, height, false, Image.Format.Rgba8);
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                image.SetPixel(x, y, new Color(1, 1, 1, alpha(x, y, width, height)));
        return ImageTexture.CreateFromImage(image);
    }
}
