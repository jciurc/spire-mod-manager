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
// read differently from the automatic ones. Hovering a chip explains it. All are searchable. On a row, tags that
// don't fit fold into a "+N" chip whose card lists them.
static class Tags
{
    public record Chip(string Text, Color Color, bool Custom = false, string? Tip = null);

    static readonly Color Gameplay = new(0.95f, 0.55f, 0.3f);
    static readonly Color Library = new(0.937f, 0.784f, 0.318f);
    static readonly Color Cosmetic = new(0.78f, 0.62f, 0.95f);
    static readonly Color Local = new(0.7f, 0.74f, 0.8f);
    static readonly Color New = new(0.5f, 0.84f, 0.78f);
    public static readonly Color CustomColor = new(0.75f, 0.9f, 0.8f);
    static readonly Color Folded = new(0.8f, 0.78f, 0.72f); // "+N", the tags that didn't fit on a row

    // Workshop tags that mean a mod is about looks (lower case, matched as whole words).
    static readonly string[] CosmeticWords = ["cosmetic", "cosmetics", "skin", "skins", "visual", "visuals", "ui", "texture", "textures", "art", "animation", "animations"];

    const float ChipMax = 140; // px of text on a chip
    const float Margin = 6; // either side of a chip's text

    // A redraw asks for every row's, and a search several times over: inside one, each mod's are worked out once
    // (Lookups).
    public static IReadOnlyList<Chip> For(Mod? mod) => mod == null ? [] : Lookups.Current?.Chips(mod, Make) ?? Make(mod);

    static List<Chip> Make(Mod mod)
    {
        if (mod.manifest is not { } manifest)
            return [];
        // Always in this order: New, Local, Library, Gameplay, Cosmetic, then the player's own.
        var chips = new List<Chip>();
        if (Installed.Mods.Contains(mod))
            chips.Add(new Chip(Menus.Text("SPIRE_MOD_MANAGER.TAG_NEW"), New, Tip: Menus.Text("SPIRE_MOD_MANAGER.TAG_NEW_TIP")));
        if (mod.workshopId == null)
            chips.Add(new Chip(Menus.Text("SPIRE_MOD_MANAGER.TAG_LOCAL"), Local, Tip: Menus.Text("SPIRE_MOD_MANAGER.TAG_LOCAL_TIP")));
        if (Dependencies.NeededBy(manifest.id).Any(m => m != mod))
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


    // A chip: a fill tinted with the chip's colour (no outline), small text in that colour. Hovering shows what
    // it means on a card (with, when it's clickable, how to search for it underneath). clicked: what a click
    // does (told whether Shift was held).
    public static Control Make(Chip chip, Action<bool>? clicked = null)
    {
        var tip = chip.Tip ?? Menus.Text(chip.Custom ? "SPIRE_MOD_MANAGER.TAG_CUSTOM_TIP" : "SPIRE_MOD_MANAGER.TAG_AUTO_TIP");
        var box = new PanelContainer
        {
            MouseFilter = Control.MouseFilterEnum.Pass, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        };
        // A long tag is cut with "…" on the chip: its card starts with the whole of it.
        var cut = ModEntry.Font("res://themes/kreon_regular_shared.tres").GetStringSize(chip.Text, fontSize: 12).X > ChipMax;
        var about = (cut ? $"[b]{Escape(chip.Text)}[/b]\n" : "") + Escape(tip);
        var card = clicked == null ? about : $"{about}\n\n{Hint(Menus.Text("SPIRE_MOD_MANAGER.SEARCH_CLICK_TIP"))}";
        Hover.Watch(box, "SpireModManagerTagCard", () => card, width: 320);
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
            ContentMarginLeft = Margin, ContentMarginRight = Margin, ContentMarginTop = 0, ContentMarginBottom = 0,
        };
        style.SetCornerRadiusAll(2);
        box.AddThemeStyleboxOverride("panel", style);
        var label = new Label { Text = chip.Text, MouseFilter = Control.MouseFilterEnum.Ignore };
        label.AddThemeFontOverride("font", ModEntry.Font("res://themes/kreon_regular_shared.tres"));
        label.AddThemeFontSizeOverride("font_size", 12);
        label.AddThemeColorOverride("font_color", chip.Color);
        if (cut)
            (label.ClipText, label.TextOverrunBehavior, label.CustomMinimumSize) = (true, TextServer.OverrunBehavior.TrimEllipsis, new Vector2(ChipMax, 0));
        box.AddChild(label);
        return box;
    }

    // "+3" in place of the tags that don't fit on a row's line; its card lists them, one per line.
    public static Control More(IReadOnlyList<Chip> rest) =>
        Make(new Chip(MoreText(rest.Count), Folded, Tip: string.Join("\n", rest.Select(c => c.Text))));

    // That chip's width for a count: its text and the margins either side.
    public static float MoreWidth(int count) =>
        ModEntry.Font("res://themes/kreon_regular_shared.tres").GetStringSize(MoreText(count), fontSize: 12).X + 2 * Margin;

    static string MoreText(int count) => Menus.Format("SPIRE_MOD_MANAGER.TAGS_MORE", ("count", Menus.Number(count)));
}
