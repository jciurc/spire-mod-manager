using Godot;

namespace SpireModManager.Api;

/// <summary>The game's fonts as SMM uses them: Kreon, or the game's own font in languages it draws in another (Chinese, Japanese, Korean, Thai, Russian, Polish).</summary>
public static class Fonts
{
    const string RegularPath = "res://themes/kreon_regular_shared.tres", BoldPath = "res://themes/kreon_bold_shared.tres";

    /// <summary>Kreon regular, or the language's font. Shared and cached: never edit it; ask again after a language change. Kreon before localization loads.</summary>
    public static Font Regular() => ApiGuard.Run("Fonts.Regular", ThemeDB.FallbackFont, () => ModEntry.Font(RegularPath));

    /// <summary>Kreon bold, or the language's regular font (as the game's own labels do).</summary>
    public static Font Bold() => ApiGuard.Run("Fonts.Bold", ThemeDB.FallbackFont, () => ModEntry.Font(BoldPath));

    /// <summary>A Label's or Button's font, size and colour, as overrides.</summary>
    public static void Style(Control control, bool bold, int size, Color color) => ApiGuard.Run("Fonts.Style", () =>
    {
        control.AddThemeFontOverride("font", ModEntry.Font(bold ? BoldPath : RegularPath));
        control.AddThemeFontSizeOverride("font_size", size);
        control.AddThemeColorOverride("font_color", color);
    });
}
