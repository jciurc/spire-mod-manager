using Godot;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;
using MegaCrit.Sts2.addons.mega_text;

namespace SpireModManager;

// Row titles in our views: the alias with the original name small and dim beside it (or just the name),
// cut short with "…" when it doesn't fit, a small notepad when the player has an alias or notes for it
// (main list), and the mod's card after hovering the row for a second. Titles of mods turned on or off
// since launch are gold. Vanilla View: exactly as the game makes them.
static class RowTitles
{
    const int OriginalNameSize = 16;
    const float TitleShift = 12; // a little room before the title in our views
    const string ShiftMeta = "spire_mod_manager_title_shift";
    const string HoverMeta = "spire_mod_manager_notes_hover";
    const string NotesCard = "SpireModManagerNotesCard";
    const double NotesDelay = 1.0;

    public const float RowHeight = 48; // our views (the game's: 72)
    const string MarkName = "SpireModManagerEditedMark";

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
            WatchHover(row);
            // The notepad: the player has an alias or notes for it (main list only).
            var marked = !vanilla && !Store.Data.LoadOrderView && info is { Alias: not null } or { Notes: not null };
            if (row.GetNodeOrNull<TextureRect>(MarkName) is { } oldMark)
                oldMark.Visible = false;
            if (!vanilla)
            {
                // Fitted once this redraw has placed the title (Load Order moves it right).
                var shown = row;
                Callable.From(() => Fit(shown, name, info?.Alias, marked)).CallDeferred();
            }
        }
        MarkChanged(rows, vanilla);
    }

    static void Fit(NModMenuRow row, string name, string? alias, bool marked)
    {
        if (!GodotObject.IsInstanceValid(row))
            return;
        var title = row.GetNode<MegaRichTextLabel>("Title");
        var font = title.GetThemeFont("normal_font");
        float Width(string text, int size) => font.GetStringSize(text, fontSize: size).X;
        const float markRoom = 22;
        var room = title.Size.X - 4 - (marked ? markRoom : 0);
        var main = Cut(alias ?? name, Layout.RowTitleSize, room);
        var text = Escape(main);
        var used = Width(main, Layout.RowTitleSize);
        if (alias != null)
        {
            var left = room - Width(main + "  ", Layout.RowTitleSize);
            var original = left > Width("……", OriginalNameSize) ? Cut(name, OriginalNameSize, left) : "";
            if (original.Length > 0)
            {
                text += $"  [color=#fff6e280][font_size={OriginalNameSize}]{Escape(original)}[/font_size][/color]";
                used += Width("  ", Layout.RowTitleSize) + Width(original, OriginalNameSize);
            }
        }
        title.SetTextAutoSize(text);

        if (!marked)
            return;
        var mark = row.GetNodeOrNull<TextureRect>(MarkName);
        if (mark == null)
        {
            mark = new TextureRect
            {
                Name = MarkName, Texture = Icons.Notepad, StretchMode = TextureRect.StretchModeEnum.Keep,
                MouseFilter = Control.MouseFilterEnum.Ignore, Modulate = new Color(0.91f, 0.81f, 0.48f, 0.75f),
            };
            row.AddChild(mark);
        }
        mark.Visible = true;
        mark.Position = new Vector2(title.Position.X + used + 9, title.Position.Y + 6);

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

    // A second over a row shows the mod's card (ModDetails.Card): names, state, problems, author, update, the
    // player's tags and notes. It goes as soon as the mouse is off the row (or the row is gone).
    static void WatchHover(NModMenuRow row)
    {
        if (row.HasMeta(HoverMeta))
            return;
        row.SetMeta(HoverMeta, 0);
        row.MouseEntered += () =>
        {
            var token = (int)row.GetMeta(HoverMeta) + 1;
            row.SetMeta(HoverMeta, token);
            row.GetTree().CreateTimer(NotesDelay).Timeout += () =>
            {
                if (!GodotObject.IsInstanceValid(row) || (int)row.GetMeta(HoverMeta) != token || Store.Data.VanillaView
                    || row.Mod is not { } mod || CategoryList.ScreenOf(row) is not { } screen || !Hovered(row))
                    return;
                Hover.Show(screen, NotesCard, ModDetails.Card(mod), Hover.Mouse(row) + new Vector2(16, 20), keep: () => Hovered(row));
            };
        };
        row.MouseExited += () =>
        {
            if (Hovered(row))
                return; // onto one of its own buttons
            row.SetMeta(HoverMeta, (int)row.GetMeta(HoverMeta) + 1);
        };
    }

    static bool Hovered(NModMenuRow row) =>
        GodotObject.IsInstanceValid(row) && row.IsVisibleInTree() && !MenuPanel.IsOpen && row.GetGlobalRect().HasPoint(Hover.Mouse(row));

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

    static string Escape(string text) => text.Replace("[", "[lb]");
}
