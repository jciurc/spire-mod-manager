using Godot;

namespace SpireModManager;

// Tiny icons drawn in code (no image files): a notepad page for rows with the player's alias or notes, and a
// round pip for mods updated lately.
static class Icons
{
    static Texture2D? _notepad, _pip;

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

    // An 8x8 dot with a soft edge (tinted by the TextureRect's modulate).
    public static Texture2D Pip => _pip ??= Draw(8, 8, (x, y, w, h) =>
    {
        var distance = new Vector2(x + 0.5f - w / 2f, y + 0.5f - h / 2f).Length();
        return Math.Clamp(w / 2f - distance + 0.5f, 0f, 1f);
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
