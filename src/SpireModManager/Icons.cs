using Godot;

namespace SpireModManager;

// Tiny icons drawn in code (no image files): a notepad page for rows with the player's alias or notes, and a
// red gem for mods updated lately.
static class Icons
{
    static Texture2D? _notepad, _gem;

    // A 10x12 page: an outline with a folded top-right corner and three ruled lines.
    public static Texture2D Notepad => _notepad ??= Draw(10, 12, (x, y, w, h) =>
    {
        const int fold = 3;
        var inFold = x >= w - fold && y < fold;
        var edge = !inFold && (x == 0 || y == h - 1 || (x == w - 1 && y >= fold) || (y == 0 && x < w - fold));
        var foldLine = x - (w - fold) == y || (y == fold - 1 && x >= w - fold) || (x == w - fold && y < fold);
        var ruled = y is 4 or 6 or 8 && x >= 2 && x <= w - 3;
        return edge || foldLine ? 1f : ruled ? 0.7f : 0f;
    });

    // A 9x9 red gem (a diamond): a dark rim, a light upper facet with a glint, a deeper lower one. In its own
    // colours (leave the TextureRect's modulate white).
    public static Texture2D Gem => _gem ??= Draw(9, 9, (x, y, w, h) =>
    {
        var (dx, dy) = (x - 4, y - 4);
        var distance = Math.Abs(dx) + Math.Abs(dy);
        if (distance > 4)
            return Colors.Transparent;
        if (distance == 4)
            return new Color(0.42f, 0.09f, 0.08f); // rim
        if ((dx, dy) == (-1, -2))
            return new Color(1f, 0.85f, 0.8f); // glint
        return dy < 0 ? new Color(1f, 0.47f, 0.42f) : dy == 0 ? new Color(0.89f, 0.27f, 0.24f) : new Color(0.66f, 0.17f, 0.15f);
    });

    static Texture2D Draw(int width, int height, Func<int, int, int, int, float> alpha) =>
        Draw(width, height, (x, y, w, h) => new Color(1, 1, 1, alpha(x, y, w, h)));

    static Texture2D Draw(int width, int height, Func<int, int, int, int, Color> color)
    {
        var image = Image.CreateEmpty(width, height, false, Image.Format.Rgba8);
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                image.SetPixel(x, y, color(x, y, width, height));
        return ImageTexture.CreateFromImage(image);
    }
}
