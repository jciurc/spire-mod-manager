using Godot;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;
using MegaCrit.Sts2.addons.mega_text;

namespace SpireModManager;

// Row titles in our views: the alias with the original name small and dim beside it (or just the name),
// cut short with "…" when it doesn't fit; before it, a small notepad when the player has an alias or notes for it
// (main list). Hovering the title shows the mod's card (Hover's delay), the notepad at once. Titles of mods
// turned on or off since launch are gold. Vanilla View: exactly as the game makes them.
static class RowTitles
{
    const int OriginalNameSize = 16;
    const float TitleShift = 18; // room before the title in our views (the notepad sits there)
    const string ShiftMeta = "spire_mod_manager_title_shift";
    const string NotesCard = "SpireModManagerNotesCard";

    public const float RowHeight = 48; // our views (the game's: 72)
    const string MarkName = "SpireModManagerEditedMark", HotName = "SpireModManagerTitleHover";

    public static void Apply(IEnumerable<NModMenuRow> rows, bool vanilla)
    {
        foreach (var row in rows)
        {
            var name = row.Mod?.manifest?.name ?? row.Mod?.manifest?.id ?? "";
            var title = row.GetNode<MegaRichTextLabel>("Title");
            var info = vanilla ? null : Store.InfoFor(row.Mod?.manifest?.id);
            // Our views: smaller text in a shorter row with a smaller tickbox and no Steam/folder icon; the
            // title stretches with the (wider) row.
            row.CustomMinimumSize = new Vector2(0, vanilla ? 72 : RowHeight);
            row.GetNode<Control>("PlatformIcon").Visible = vanilla;
            Round(row.GetNode<Panel>("SelectionHighlight"), vanilla);
            var tickbox = row.GetNode<Control>("Tickbox");
            tickbox.PivotOffset = tickbox.Size / 2;
            tickbox.Scale = vanilla ? Vector2.One : new Vector2(0.68f, 0.68f);
            title.AnchorRight = vanilla ? 0 : 1;
            title.OffsetRight = vanilla ? 562 : -90;
            // Relative moves only: Load Order shifts the title too, for its grip.
            var shift = vanilla ? 0 : TitleShift;
            title.OffsetLeft += shift - (row.HasMeta(ShiftMeta) ? (float)row.GetMeta(ShiftMeta) : 0);
            row.SetMeta(ShiftMeta, shift);
            title.BbcodeEnabled = true;
            title.Text = Escape(name);
            Layout.FontSize(title, vanilla ? Layout.RowTitleVanillaSize : Layout.RowTitleSize);
            // Shown again by Fit, in our views.
            foreach (var part in new[] { MarkName, HotName })
                if (row.GetNodeOrNull<Control>(part) is { } old)
                    old.Visible = false;
            if (!vanilla)
            {
                // The notepad: the player has an alias or notes for it (main list only).
                var marked = !Store.Data.LoadOrderView && info is { Alias: not null } or { Notes: not null };
                // Fitted once this redraw has placed the title (Load Order moves it right).
                var shown = row;
                Callable.From(() => Fit(shown, name, info?.Alias, marked)).CallDeferred();
            }
        }
        MarkChanged(rows, vanilla);
    }

    // The title text cut to fit; then the notepad and the hover area over the text, placed by it.
    static void Fit(NModMenuRow row, string name, string? alias, bool marked)
    {
        if (!GodotObject.IsInstanceValid(row) || row.Mod is not { } mod)
            return;
        var title = row.GetNode<MegaRichTextLabel>("Title");
        var font = title.GetThemeFont("normal_font");
        float Width(string text, int size) => font.GetStringSize(text, fontSize: size).X;
        var markSize = Icons.Notepad.GetSize();
        var room = title.Size.X - 4;
        var main = Cut(alias ?? name, Layout.RowTitleSize, room);
        var text = Escape(main);
        var mainWidth = Width(main, Layout.RowTitleSize);
        var used = mainWidth;
        if (alias != null)
        {
            var space = Width(" ", Layout.RowTitleSize);
            const int gap = 2;
            var left = room - mainWidth - gap * space;
            var original = left > Width("……", OriginalNameSize) ? Cut(name, OriginalNameSize, left) : "";
            if (original.Length > 0)
            {
                text += new string(' ', gap) + $"[color=#fff6e280][font_size={OriginalNameSize}]{Escape(original)}[/font_size][/color]";
                used = mainWidth + gap * space + Width(original, OriginalNameSize);
            }
        }
        title.SetTextAutoSize(text);
        var lineHeight = font.GetHeight(Layout.RowTitleSize);
        var top = title.Position.Y + Math.Max(0, (title.Size.Y - lineHeight) / 2); // the text sits in the middle of the title's box

        // Hovering the text (not the whole row): the mod's card.
        var hot = row.GetNodeOrNull<Control>(HotName);
        if (hot == null)
        {
            hot = new Control { Name = HotName, MouseFilter = Control.MouseFilterEnum.Pass };
            row.AddChild(hot);
            Hover.Watch(hot, NotesCard, () => row.Mod is { } shown ? ModDetails.Card(shown) : null);
        }
        (hot.Position, hot.Size, hot.Visible) = (new Vector2(title.Position.X, top), new Vector2(used, lineHeight), true);

        if (marked)
        {
            var mark = row.GetNodeOrNull<TextureRect>(MarkName);
            if (mark == null)
            {
                mark = new TextureRect
                {
                    Name = MarkName, Texture = Icons.Notepad, StretchMode = TextureRect.StretchModeEnum.Keep,
                    MouseFilter = Control.MouseFilterEnum.Pass, Modulate = new Color(0.91f, 0.81f, 0.48f, 0.75f),
                };
                row.AddChild(mark);
                Hover.Watch(mark, NotesCard, () => row.Mod is { } shown ? ModDetails.Card(shown) : null, Hover.Timing.Instant);
            }
            mark.Visible = true;
            mark.Position = new Vector2(title.Position.X - markSize.X - 4, top + (lineHeight - markSize.Y) / 2); // in the room before the title
        }

        string Cut(string full, int size, float width)
        {
            if (Width(full, size) <= width)
                return full;
            var length = full.Length;
            while (length > 0 && Width(full[..length].TrimEnd() + "…", size) > width)
                length--;
            return length > 0 ? full[..length].TrimEnd() + "…" : "";
        }
    }

    // Our views: a mod turned on since launch has a bright gold title, one turned off a dim one, so what a
    // relaunch will change stands out. Otherwise the colour the game gave it (white, grey when off, red when
    // it failed).
    public static readonly Color ChangedTitle = new(1f, 0.82f, 0.45f); // turned on: bright gold
    public static readonly Color ChangedOffTitle = new(0.72f, 0.6f, 0.42f, 0.8f); // turned off: a dim, dusky gold
    const string TitleColorMeta = "spire_mod_manager_title_color";

    public static void MarkChanged(IEnumerable<NModMenuRow> rows, bool vanilla)
    {
        var loaded = History.Loaded?.Mods.DistinctBy(m => (m.Id, m.Source)).ToDictionary(m => (m.Id, m.Source), m => m.Enabled);
        foreach (var row in rows)
        {
            var title = row.GetNode<Control>("Title");
            if (!title.HasMeta(TitleColorMeta))
                title.SetMeta(TitleColorMeta, title.Modulate);
            var changed = !vanilla && loaded != null && row.Mod is { manifest.id: { } id } mod
                && loaded.GetValueOrDefault((id, mod.modSource), true) != Dependencies.IsOn(mod);
            title.Modulate = !changed ? (Color)title.GetMeta(TitleColorMeta)
                : row.Mod is { } turned && Dependencies.IsOn(turned) ? ChangedTitle : ChangedOffTitle;
        }
    }

    // The row's hover / selected look with slightly rounded corners in our views (the game's shape in Vanilla).
    public const int CornerRadius = 2;

    const string GameLookMeta = "spire_mod_manager_game_look";

    // (The game's look is its own override on the panel: kept, and put back in Vanilla View.)
    static void Round(Panel look, bool vanilla)
    {
        if (!look.HasMeta(GameLookMeta))
            look.SetMeta(GameLookMeta, look.GetThemeStylebox("panel"));
        var game = (StyleBox)look.GetMeta(GameLookMeta);
        if (vanilla)
        {
            look.AddThemeStyleboxOverride("panel", game);
            return;
        }
        if (game is not StyleBoxFlat flat || look.GetThemeStylebox("panel") is StyleBoxFlat current && current != game && current.CornerRadiusTopLeft == CornerRadius)
            return; // not a shape we can round, or rounded already
        var rounded = (StyleBoxFlat)flat.Duplicate();
        rounded.SetCornerRadiusAll(CornerRadius);
        look.AddThemeStyleboxOverride("panel", rounded);
    }

    static string Escape(string text) => text.Replace("[", "[lb]");
}
