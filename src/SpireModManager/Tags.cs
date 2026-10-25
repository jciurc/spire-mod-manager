using Godot;
using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Platform;

namespace SpireModManager;

// Tag chips on each mod: the automatic ones (New, Local, Duplicate, Stable or Beta, Library, Gameplay, Cosmetic) and the
// player's own.
static class Tags
{
    // Search: what a click on it searches for, if not its tag.
    public record Chip(string Text, Color Color, bool Custom = false, string? Tip = null, string? Search = null);

    static readonly Color Gameplay = new(0.95f, 0.55f, 0.3f);
    static readonly Color Library = new(0.937f, 0.784f, 0.318f);
    static readonly Color Cosmetic = new(0.78f, 0.62f, 0.95f);
    static readonly Color Local = new(0.7f, 0.74f, 0.8f);
    static readonly Color New = new(0.5f, 0.84f, 0.78f);
    static readonly Color Stable = new(0.58f, 0.84f, 0.5f);
    static readonly Color Beta = new(0.5f, 0.7f, 1f);
    // Copies of an id ticked together: the same-ID banner's warning yellow, light enough for text (the banner's own fill
    // is too dark to read on a card). The copy lines say it in this too (ModDetails.CopyLine); the self-test reads it.
    public static readonly Color SameIdClash = new(0.95f, 0.78f, 0.35f);
    // Duplicate while it isn't a clash: a muted tan, neutral beside the other chips and well apart from the clash's yellow
    // and Gameplay's orange. The self-test reads it.
    public static readonly Color Duplicate = new(0.76f, 0.65f, 0.53f);
    public static readonly Color CustomColor = new(0.75f, 0.9f, 0.8f);

    // Workshop tag words that mark a mod as cosmetic (lower case, whole words): art and sound, as its card says. The
    // Workshop's own categories for the game have Cosmetics and Audio (Oct 2026); the rest are for tags set other ways.
    // Not "ui": that more often means an interface tool than a new look.
    static readonly string[] CosmeticWords = ["cosmetic", "cosmetics", "skin", "skins", "reskin", "reskins", "visual", "visuals", "texture", "textures", "art",
        "animation", "animations", "audio", "sound", "sounds", "sfx", "music"];

    const float ChipMax = 140; // px of text on a chip
    const float Margin = 6; // either side of a chip's text

    // A mod's chips, worked out once per redraw (Lookups).
    public static IReadOnlyList<Chip> For(Mod? mod) => mod == null ? [] : Lookups.Current?.Chips(mod, Make) ?? Make(mod);

    static List<Chip> Make(Mod mod)
    {
        if (mod.manifest is not { } manifest)
            return [];
        // Order: New, Local, Duplicate, Stable or Beta, Library, Gameplay, Cosmetic, then the player's own.
        var chips = new List<Chip>();
        if (IsNew(mod))
            chips.Add(new Chip(Menus.Text("SPIRE_MOD_MANAGER.TAG_NEW"), New, Tip: Menus.Text("SPIRE_MOD_MANAGER.TAG_NEW_TIP")));
        if (IsLocal(mod))
            chips.Add(new Chip(Menus.Text("SPIRE_MOD_MANAGER.TAG_LOCAL"), Local, Tip: Menus.Text("SPIRE_MOD_MANAGER.TAG_LOCAL_TIP")));
        // Duplicate: another copy of this mod id installed (Copies). Yellow on every copy ticked while another is (only one
        // of them loads), the one that loads too: they all share the problem, this mod's copies as any mod's
        // (Copies.Clashes). Tan otherwise. Its tip names the others (while yellow, the ticked ones, and the copy that
        // loads); a click lists every mod installed more than once.
        if (manifest.id is { } id && Copies.Others(mod) is { Count: > 0 } others)
        {
            var clash = Copies.Clashes(mod);
            string Names(IEnumerable<Mod> copies, string between = ", ") => string.Join(between, copies.DistinctBy(Copies.CopyOf).Select(Copies.Named));
            var ticked = others.Where(Copies.Ticked).DistinctBy(Copies.CopyOf).ToList();
            var tip = clash // the enabled copies one to a line, under the tip's first line (Jordan, 23 Oct); one: "Another copy" (24 Oct)
                ? Menus.Format(ticked.Count == 1 ? "SPIRE_MOD_MANAGER.TAG_SAME_ID_CLASH_TIP_ONE" : "SPIRE_MOD_MANAGER.TAG_SAME_ID_CLASH_TIP",
                    ("others", Names(ticked, "\n")), ("loads", Copies.Named(Copies.Loading(id) ?? mod)))
                : Menus.Format("SPIRE_MOD_MANAGER.TAG_SAME_ID_TIP", ("others", Names(others)));
            chips.Add(new Chip(Menus.Text("SPIRE_MOD_MANAGER.TAG_SAME_ID"), clash ? SameIdClash : Duplicate, Tip: tip));
        }
        // A branch only when the mod is marked for one of the two (most are for both, and #stable, #beta find them anyway).
        // A click searches for what the chip means: marked for this branch and not the other.
        var (stable, beta) = Branches(mod);
        var (stableName, betaName) = (Menus.Text("SPIRE_MOD_MANAGER.TAG_STABLE"), Menus.Text("SPIRE_MOD_MANAGER.TAG_BETA"));
        if (stable && !beta)
            chips.Add(new Chip(stableName, Stable, Tip: Menus.Text("SPIRE_MOD_MANAGER.TAG_STABLE_TIP"), Search: $"{Term(stableName)} -{Term(betaName)}"));
        else if (beta && !stable)
            chips.Add(new Chip(betaName, Beta, Tip: Menus.Text("SPIRE_MOD_MANAGER.TAG_BETA_TIP"), Search: $"{Term(betaName)} -{Term(stableName)}"));
        if (Dependencies.NeededBy(manifest.id).Any(m => m != mod))
            chips.Add(new Chip(Menus.Text("SPIRE_MOD_MANAGER.TAG_LIBRARY"), Library, Tip: Menus.Text("SPIRE_MOD_MANAGER.TAG_LIBRARY_TIP")));
        if (manifest.affectsGameplay)
            chips.Add(new Chip(Menus.Text("SPIRE_MOD_MANAGER.TAG_GAMEPLAY"), Gameplay, Tip: Menus.Text("SPIRE_MOD_MANAGER.TAG_GAMEPLAY_TIP")));
        if (IsCosmetic(mod))
            chips.Add(new Chip(Menus.Text("SPIRE_MOD_MANAGER.TAG_COSMETIC"), Cosmetic, Tip: Menus.Text("SPIRE_MOD_MANAGER.TAG_COSMETIC_TIP")));
        foreach (var tag in Store.InfoFor(mod)?.Tags ?? [])
            chips.Add(new Chip(tag, CustomColor, Custom: true));
        return chips;
    }

    // The New chip's mods (installed this session) and the Local chip's (not from the Workshop); #new and #local too.
    public static bool IsNew(Mod mod) => Installed.Mods.Contains(mod);

    public static bool IsLocal(Mod mod) => Compat.WorkshopId(mod) == null;

    // What a #tag search finds on a mod: its chips' text, its Workshop tags (a click on one in the panel searches it; your
    // own "Utility" and the Workshop's are one tag), and Stable and Beta for each branch it's marked for, chip or not (so
    // "#stable -#beta" finds the mods marked for stable only), in the game's language and in English (a guide's "#stable"
    // works in any language). A Workshop tag named like one of the automatic chips isn't that chip: "#library" is the
    // mods others need, and a Workshop "Beta" doesn't hide a stable-only mod from its own Stable chip's search.
    public static IReadOnlyList<string> Searchable(Mod mod)
    {
        var texts = For(mod).Select(c => c.Text).ToList();
        if (WorkshopInfo.For(mod)?.Tags is { Count: > 0 } workshop)
        {
            var automatic = Automatic().ToHashSet(StringComparer.OrdinalIgnoreCase);
            texts.AddRange(workshop.Where(t => !automatic.Contains(t)));
        }
        var (stable, beta) = Branches(mod);
        if (stable)
            texts.AddRange([Menus.Text("SPIRE_MOD_MANAGER.TAG_STABLE"), "Stable"]);
        if (beta)
            texts.AddRange([Menus.Text("SPIRE_MOD_MANAGER.TAG_BETA"), "Beta"]);
        if (Copies.Others(mod).Count > 0)
            texts.Add("Duplicate");
        return texts;
    }

    // Whether a #tag finds the mod through its Workshop tags (Searchable's: not one named like an automatic chip).
    public static bool FromWorkshop(Mod mod, string tag) => WorkshopInfo.For(mod)?.Tags is { Count: > 0 } workshop
        && workshop.Contains(tag, StringComparer.OrdinalIgnoreCase) && !Automatic().Contains(tag, StringComparer.OrdinalIgnoreCase);

    // The automatic chips' names, in the game's language and in English.
    static IEnumerable<string> Automatic() =>
        new[]
        {
            "SPIRE_MOD_MANAGER.TAG_NEW", "SPIRE_MOD_MANAGER.TAG_LOCAL", "SPIRE_MOD_MANAGER.TAG_SAME_ID", "SPIRE_MOD_MANAGER.TAG_STABLE",
            "SPIRE_MOD_MANAGER.TAG_BETA", "SPIRE_MOD_MANAGER.TAG_LIBRARY", "SPIRE_MOD_MANAGER.TAG_GAMEPLAY", "SPIRE_MOD_MANAGER.TAG_COSMETIC",
        }.Select(Menus.Text).Concat(["New", "Local", "Duplicate", "Stable", "Beta", "Library", "Gameplay", "Cosmetic"]);

    // The game's stable and beta branches a mod is marked for, by the game's own rule over the Workshop's supported
    // range (on another branch the game still loads it, with an error).
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

    // A tag as a search term: #name, quoted where it has to be (Search.TermFor).
    static string Term(string name) => Search.TermFor("#", name);

    // A usage hint for the foot of a card: small, bold, faint.
    public static string Hint(string text) => $"[b][font_size=14][color=#fff6e280]{Escape(text)}[/color][/font_size][/b]";

    static string Escape(string text) => text.Replace("[", "[lb]");

    // Its Workshop tags say art or sound, or its pack replaces some of the game's (GameChanges.ReplacesArt: a local reskin
    // has no tags, and a Workshop one's tags may not say it).
    static bool IsCosmetic(Mod mod) => IsCosmetic(WorkshopInfo.For(mod)?.Tags) || GameChanges.ReplacesArt(mod);

    public static bool IsCosmetic(IEnumerable<string>? workshopTags) =>
        workshopTags?.SelectMany(t => t.ToLowerInvariant().Split([' ', '&', '/', '-', ','], StringSplitOptions.RemoveEmptyEntries))
            .Any(word => CosmeticWords.Contains(word)) == true;

    // "a, b ,c" -> ["a", "b", "c"], blanks and repeats dropped; null when there are none.
    public static List<string>? Parse(string text)
    {
        var tags = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return tags.Count > 0 ? tags : null;
    }

    // A chip's look, one Theme per colour (and font: it changes with the game's language), shared by every chip of that
    // colour: its box, and its text (the label inherits it). The player's own tags are fainter than the automatic ones.
    static readonly Dictionary<(Font Font, Color Color, bool Custom), Theme> Looks = [];

    static Theme Look(Font font, Color color, bool custom)
    {
        if (Looks.TryGetValue((font, color, custom), out var look))
            return look;
        look = new Theme();
        look.SetStylebox("panel", "PanelContainer", Box(color, custom));
        look.SetFont("font", "Label", font);
        look.SetFontSize("font_size", "Label", 12);
        look.SetColor("font_color", "Label", color);
        return Looks[(font, color, custom)] = look;
    }

    // A chip's box; PillBox is a player's tag's, drawn behind the tags being typed (TagPills).
    static StyleBoxFlat Box(Color color, bool custom)
    {
        var style = new StyleBoxFlat
        {
            BgColor = new Color(color, custom ? 0.1f : 0.2f),
            ContentMarginLeft = Margin, ContentMarginRight = Margin, ContentMarginTop = 0, ContentMarginBottom = 0,
        };
        style.SetCornerRadiusAll(2);
        return style;
    }

    public static readonly StyleBoxFlat PillBox = Box(CustomColor, custom: true);

    // A chip with its hover card; clicked gets whether Shift was held.
    public static Control Make(Chip chip, Action<bool>? clicked = null)
    {
        var font = ModEntry.Font("res://themes/kreon_regular_shared.tres");
        var box = new PanelContainer
        {
            MouseFilter = Control.MouseFilterEnum.Pass, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter, Theme = Look(font, chip.Color, chip.Custom),
        };
        // A long tag is cut with ".."; its card then starts with the whole tag. Made when it shows: a tag of the player's
        // says how many mods have it, which an edit on another row changes without remaking this one.
        var cut = RowTitles.Measure(font, chip.Text, 12) > ChipMax;
        string Card()
        {
            var tip = chip.Tip ?? (chip.Custom ? CustomTip(chip.Text) : Menus.Text("SPIRE_MOD_MANAGER.TAG_AUTO_TIP"));
            var about = (cut ? $"[b]{Escape(chip.Text)}[/b]\n" : "") + Escape(tip);
            return clicked == null ? about : $"{about}\n\n{Hint(Menus.Text("SPIRE_MOD_MANAGER.SEARCH_CLICK_TIP"))}";
        }
        Hover.Watch(box, "SpireModManagerTagCard", Card, width: 320);
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
        var label = new Label { Text = chip.Text, MouseFilter = Control.MouseFilterEnum.Ignore };
        if (cut)
        {
            label.CustomMinimumSize = new Vector2(ChipMax, 0);
            Menus.CutToFit(label, chip.Text);
        }
        box.AddChild(label);
        return box;
    }

    // What the card of a tag of the player's says: "Your custom tag, applied to 3 mods", counting the installed copies
    // that have it among their own tags (any case, as a search finds them).
    static string CustomTip(string tag)
    {
        var count = Dependencies.EachCopy(ModManager.Mods.Union(Installed.Mods))
            .Count(m => Store.InfoFor(m)?.Tags?.Contains(tag, StringComparer.OrdinalIgnoreCase) == true);
        return Menus.Format(count == 1 ? "SPIRE_MOD_MANAGER.TAG_CUSTOM_COUNT_TIP_ONE" : "SPIRE_MOD_MANAGER.TAG_CUSTOM_COUNT_TIP", ("count", Menus.Number(count)));
    }

    // A chip in a card's text (the tags a row's "and 3 more.." stands for): its words in its colour on its box's tint,
    // smaller than the card's text, as the chip is beside the row's.
    public static string Pill(Chip chip) =>
        $"[font_size=14][bgcolor=#{new Color(chip.Color, chip.Custom ? 0.1f : 0.2f).ToHtml()}][color=#{chip.Color.ToHtml(false)}]"
        + $"\u00a0{Escape(chip.Text)}\u00a0[/color][/bgcolor][/font_size]";
}
