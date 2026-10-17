using Godot;
using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Platform;

namespace SpireModManager;

// Tag chips on each mod: the automatic ones (New, Local, Stable or Beta, Library, Gameplay, Cosmetic) and the
// player's own.
static class Tags
{
    public record Chip(string Text, Color Color, bool Custom = false, string? Tip = null);

    static readonly Color Gameplay = new(0.95f, 0.55f, 0.3f);
    static readonly Color Library = new(0.937f, 0.784f, 0.318f);
    static readonly Color Cosmetic = new(0.78f, 0.62f, 0.95f);
    static readonly Color Local = new(0.7f, 0.74f, 0.8f);
    static readonly Color New = new(0.5f, 0.84f, 0.78f);
    static readonly Color Stable = new(0.58f, 0.84f, 0.5f);
    static readonly Color Beta = new(0.5f, 0.7f, 1f);
    public static readonly Color CustomColor = new(0.75f, 0.9f, 0.8f);
    static readonly Color Folded = new(0.8f, 0.78f, 0.72f); // the "+N" chip

    // Workshop tag words that mark a mod as cosmetic (lower case, whole words).
    static readonly string[] CosmeticWords = ["cosmetic", "cosmetics", "skin", "skins", "visual", "visuals", "ui", "texture", "textures", "art", "animation", "animations"];

    const float ChipMax = 140; // px of text on a chip
    const float Margin = 6; // either side of a chip's text

    // A mod's chips, worked out once per redraw (Lookups).
    public static IReadOnlyList<Chip> For(Mod? mod) => mod == null ? [] : Lookups.Current?.Chips(mod, Make) ?? Make(mod);

    static List<Chip> Make(Mod mod)
    {
        if (mod.manifest is not { } manifest)
            return [];
        // Order: New, Local, Stable or Beta, Library, Gameplay, Cosmetic, then the player's own.
        var chips = new List<Chip>();
        if (Installed.Mods.Contains(mod))
            chips.Add(new Chip(Menus.Text("SPIRE_MOD_MANAGER.TAG_NEW"), New, Tip: Menus.Text("SPIRE_MOD_MANAGER.TAG_NEW_TIP")));
        if (Compat.WorkshopId(mod) == null)
            chips.Add(new Chip(Menus.Text("SPIRE_MOD_MANAGER.TAG_LOCAL"), Local, Tip: Menus.Text("SPIRE_MOD_MANAGER.TAG_LOCAL_TIP")));
        // A branch only when the mod is limited to one of the two (most load on both, and #stable, #beta find them anyway).
        var (stable, beta) = Branches(mod);
        if (stable && !beta)
            chips.Add(new Chip(Menus.Text("SPIRE_MOD_MANAGER.TAG_STABLE"), Stable, Tip: Menus.Text("SPIRE_MOD_MANAGER.TAG_STABLE_TIP")));
        else if (beta && !stable)
            chips.Add(new Chip(Menus.Text("SPIRE_MOD_MANAGER.TAG_BETA"), Beta, Tip: Menus.Text("SPIRE_MOD_MANAGER.TAG_BETA_TIP")));
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

    // What a #tag search finds on a mod: its chips' text, and Stable and Beta for each branch it loads on, chip or not
    // (so "#stable -#beta" finds the mods only stable loads).
    public static IReadOnlyList<string> Searchable(Mod mod)
    {
        var texts = For(mod).Select(c => c.Text).ToList();
        var (stable, beta) = Branches(mod);
        if (stable)
            texts.Add(Menus.Text("SPIRE_MOD_MANAGER.TAG_STABLE"));
        if (beta)
            texts.Add(Menus.Text("SPIRE_MOD_MANAGER.TAG_BETA"));
        return texts;
    }

    // The game's stable and beta branches a mod loads on, by the game's own rule over the Workshop's supported range.
    // No range set, or no Workshop details (a local mod, or before they arrive), is every branch.
    public static (bool Stable, bool Beta) Branches(Mod mod) =>
        WorkshopInfo.For(mod)?.Branches is { Count: > 0 } ranges ? Supports(ranges) : (true, true);

    static bool _noBranchRule;

    // Every branch if a game update took the game's rule away (logged once).
    static (bool, bool) Supports(IReadOnlyList<(string Min, string Max)> ranges)
    {
        if (_noBranchRule)
            return (true, true);
        try
        {
            return GameSupports(ranges);
        }
        catch (Exception e)
        {
            _noBranchRule = true;
            Log.Warn($"[{ModEntry.ModId}] The game's branch rule for mods wasn't found, so no mod is tagged Stable or Beta "
                + $"(logged once): {e.Message}");
            return (true, true);
        }
    }

    // On its own, so a game update without the rule fails in Supports' try, not where that is compiled.
    [MethodImpl(MethodImplOptions.NoInlining)]
    static (bool, bool) GameSupports(IReadOnlyList<(string, string)> ranges) =>
        (ModManager.EvaluateBranchSupport(PlatformBranch.Production, ranges).IsSupported,
            ModManager.EvaluateBranchSupport(PlatformBranch.PublicBeta, ranges).IsSupported);

    // A usage hint for the foot of a card: small, bold, faint.
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

    // A chip with its hover card; clicked gets whether Shift was held.
    public static Control Make(Chip chip, Action<bool>? clicked = null)
    {
        var tip = chip.Tip ?? Menus.Text(chip.Custom ? "SPIRE_MOD_MANAGER.TAG_CUSTOM_TIP" : "SPIRE_MOD_MANAGER.TAG_AUTO_TIP");
        var box = new PanelContainer
        {
            MouseFilter = Control.MouseFilterEnum.Pass, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        };
        // A long tag is cut with "…"; its card then starts with the whole tag.
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
            // The player's own tags are fainter than the automatic ones.
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

    // The "+N" chip for tags that don't fit on a row; its card lists them.
    public static Control More(IReadOnlyList<Chip> rest) =>
        Make(new Chip(MoreText(rest.Count), Folded, Tip: string.Join("\n", rest.Select(c => c.Text))));

    // The "+N" chip's width: its text plus both margins.
    public static float MoreWidth(int count) =>
        ModEntry.Font("res://themes/kreon_regular_shared.tres").GetStringSize(MoreText(count), fontSize: 12).X + 2 * Margin;

    static string MoreText(int count) => Menus.Format("SPIRE_MOD_MANAGER.TAGS_MORE", ("count", Menus.Number(count)));
}
