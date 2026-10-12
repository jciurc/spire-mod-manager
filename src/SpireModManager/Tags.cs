using Godot;
using MegaCrit.Sts2.Core.Modding;

namespace SpireModManager;

// Small labels on each mod. These are worked out automatically:
// - New, for a mod installed since the game started (it loads after a relaunch);
// - Local, for a mod installed by hand rather than from the Workshop;
// - Library, when another installed mod depends on it;
// - Gameplay, from the manifest's affects_gameplay (what multiplayer compares; mods without it get no tag);
// - Cosmetic, when the mod's Workshop tags say it's about looks (skins, visuals, UI, textures).
// The player's own tags are typed in the info panel or a mod's right-click menu and drawn fainter, so they
// read differently from the automatic ones. Hovering a chip explains it. All are searchable.
static class Tags
{
    public record Chip(string Text, Color Color, bool Custom = false, string? Tip = null);

    static readonly Color Gameplay = new(0.95f, 0.55f, 0.3f);
    static readonly Color Library = new(0.937f, 0.784f, 0.318f);
    static readonly Color Cosmetic = new(0.78f, 0.62f, 0.95f);
    static readonly Color Local = new(0.7f, 0.74f, 0.8f);
    static readonly Color New = new(0.5f, 0.84f, 0.78f);
    public static readonly Color CustomColor = new(0.75f, 0.9f, 0.8f);

    // Workshop tags that mean a mod is about looks (lower case, matched as whole words).
    static readonly string[] CosmeticWords = ["cosmetic", "cosmetics", "skin", "skins", "visual", "visuals", "ui", "texture", "textures", "art", "animation", "animations"];

    public static List<Chip> For(Mod? mod)
    {
        if (mod?.manifest is not { } manifest)
            return [];
        // Always in this order: New, Local, Library, Gameplay, Cosmetic, then the player's own. (Updated lately is a
        // pip by the title: RowTitles.)
        var chips = new List<Chip>();
        if (Installed.Mods.Contains(mod))
            chips.Add(new Chip(Menus.Text("SPIRE_MOD_MANAGER.TAG_NEW"), New, Tip: Menus.Text("SPIRE_MOD_MANAGER.TAG_NEW_TIP")));
        if (mod.workshopId == null)
            chips.Add(new Chip(Menus.Text("SPIRE_MOD_MANAGER.TAG_LOCAL"), Local, Tip: Menus.Text("SPIRE_MOD_MANAGER.TAG_LOCAL_TIP")));
        if (ModManager.Mods.Any(m => m != mod && Dependencies.Of(m).Contains(manifest.id)))
            chips.Add(new Chip(Menus.Text("SPIRE_MOD_MANAGER.TAG_LIBRARY"), Library, Tip: Menus.Text("SPIRE_MOD_MANAGER.TAG_LIBRARY_TIP")));
        if (manifest.affectsGameplay)
            chips.Add(new Chip(Menus.Text("SPIRE_MOD_MANAGER.TAG_GAMEPLAY"), Gameplay, Tip: Menus.Text("SPIRE_MOD_MANAGER.TAG_GAMEPLAY_TIP")));
        if (IsCosmetic(mod))
            chips.Add(new Chip(Menus.Text("SPIRE_MOD_MANAGER.TAG_COSMETIC"), Cosmetic, Tip: Menus.Text("SPIRE_MOD_MANAGER.TAG_COSMETIC_TIP")));
        foreach (var tag in Store.InfoFor(manifest.id)?.Tags ?? [])
            chips.Add(new Chip(tag, CustomColor, Custom: true));
        return chips;
    }

    // How to use something, at the foot of a card: small, bold, faint.
    public static string Hint(string text) => $"[b][font_size=14][color=#fff6e280]{Escape(text)}[/color][/font_size][/b]";

    static string Escape(string text) => text.Replace("[", "[lb]");

    static bool IsCosmetic(Mod mod) =>
        WorkshopInfo.For(mod)?.Tags.SelectMany(t => t.ToLowerInvariant().Split([' ', '&', '/', '-', ','], StringSplitOptions.RemoveEmptyEntries))
            .Any(word => CosmeticWords.Contains(word)) == true;

    // "a, b ,c" -> ["a", "b", "c"], blanks and repeats dropped; null when there are none.
    public static List<string>? Parse(string text)
    {
        var tags = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return tags.Count > 0 ? tags : null;
    }

    public const double CardDelay = 0.5;

    // A chip: tinted fill, border in the chip's colour, small text. Hovering shows what it means on a card
    // (with, when it's clickable, how to search for it underneath). clicked: what a click does (told whether
    // Shift was held).
    public static Control Make(Chip chip, Action<bool>? clicked = null)
    {
        var tip = chip.Tip ?? Menus.Text(chip.Custom ? "SPIRE_MOD_MANAGER.TAG_CUSTOM_TIP" : "SPIRE_MOD_MANAGER.TAG_AUTO_TIP");
        var box = new PanelContainer
        {
            MouseFilter = Control.MouseFilterEnum.Pass, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        };
        var card = clicked == null ? Escape(tip) : $"{Escape(tip)}\n\n{Hint(Menus.Text("SPIRE_MOD_MANAGER.SEARCH_CLICK_TIP"))}";
        Hover.Watch(box, "SpireModManagerTagCard", CardDelay, () => card, width: 320);
        if (clicked != null)
        {
            (box.MouseFilter, box.MouseDefaultCursorShape) = (Control.MouseFilterEnum.Stop, Control.CursorShape.PointingHand);
            box.GuiInput += e =>
            {
                if (e is not InputEventMouseButton { ButtonIndex: MouseButton.Left } button)
                    return;
                box.AcceptEvent(); // not the row's click (or drag) too
                if (!button.Pressed)
                    clicked(button.ShiftPressed);
            };
        }
        var style = new StyleBoxFlat
        {
            // No outline, small corners; the player's own tags are fainter than the automatic ones.
            BgColor = new Color(chip.Color, chip.Custom ? 0.1f : 0.2f),
            ContentMarginLeft = 6, ContentMarginRight = 6, ContentMarginTop = 0, ContentMarginBottom = 0,
        };
        style.SetCornerRadiusAll(2);
        box.AddThemeStyleboxOverride("panel", style);
        var label = new Label { Text = chip.Text, MouseFilter = Control.MouseFilterEnum.Ignore };
        label.AddThemeFontOverride("font", ResourceLoader.Load<Font>("res://themes/kreon_regular_shared.tres"));
        label.AddThemeFontSizeOverride("font_size", 12);
        label.AddThemeColorOverride("font_color", chip.Color);
        box.AddChild(label);
        return box;
    }
}
