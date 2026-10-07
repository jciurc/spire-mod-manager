using Godot;
using MegaCrit.Sts2.Core.Modding;

namespace SpireModManager;

// Small labels on each mod. Two are worked out from the mods themselves:
// - Gameplay, from the manifest's affects_gameplay (what multiplayer compares; mods without it get no tag);
// - Library, when another installed mod depends on it.
// The player's own tags are typed in the info panel and drawn outline-only, so they read differently from
// the automatic (filled) ones; hovering a chip says which it is. All tags are searchable.
static class Tags
{
    public record Chip(string Text, Color Color, bool Custom = false);

    static readonly Color Gameplay = new(0.95f, 0.55f, 0.3f);
    static readonly Color Library = new(0.937f, 0.784f, 0.318f);
    static readonly Color Custom = new(0.75f, 0.9f, 0.8f);

    public static List<Chip> For(Mod? mod)
    {
        if (mod?.manifest is not { } manifest)
            return [];
        var chips = new List<Chip>();
        if (manifest.affectsGameplay)
            chips.Add(new Chip(Menus.Text("SPIRE_MOD_MANAGER.TAG_GAMEPLAY"), Gameplay));
        if (ModManager.Mods.Any(m => m != mod && Dependencies.Of(m).Contains(manifest.id)))
            chips.Add(new Chip(Menus.Text("SPIRE_MOD_MANAGER.TAG_LIBRARY"), Library));
        foreach (var tag in Store.InfoFor(manifest.id)?.Tags ?? [])
            chips.Add(new Chip(tag, Custom, Custom: true));
        return chips;
    }

    // "a, b ,c" -> ["a", "b", "c"], blanks and repeats dropped; null when there are none.
    public static List<string>? Parse(string text)
    {
        var tags = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return tags.Count > 0 ? tags : null;
    }

    // A chip: tinted fill, border in the chip's colour, small text.
    public static Control Make(Chip chip)
    {
        var box = new PanelContainer
        {
            MouseFilter = Control.MouseFilterEnum.Pass, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            TooltipText = Menus.Text(chip.Custom ? "SPIRE_MOD_MANAGER.TAG_CUSTOM_TIP" : "SPIRE_MOD_MANAGER.TAG_AUTO_TIP"),
        };
        var style = new StyleBoxFlat
        {
            // Automatic tags are filled; the player's own are outline only.
            BgColor = new Color(chip.Color, chip.Custom ? 0f : 0.18f), BorderColor = new Color(chip.Color, chip.Custom ? 0.6f : 0.8f),
            ContentMarginLeft = 6, ContentMarginRight = 6, ContentMarginTop = 0, ContentMarginBottom = 0,
        };
        style.SetBorderWidthAll(1);
        style.SetCornerRadiusAll(8);
        box.AddThemeStyleboxOverride("panel", style);
        var label = new Label { Text = chip.Text, MouseFilter = Control.MouseFilterEnum.Ignore };
        label.AddThemeFontOverride("font", ResourceLoader.Load<Font>("res://themes/kreon_regular_shared.tres"));
        label.AddThemeFontSizeOverride("font_size", 13);
        label.AddThemeColorOverride("font_color", chip.Color);
        box.AddChild(label);
        return box;
    }
}
