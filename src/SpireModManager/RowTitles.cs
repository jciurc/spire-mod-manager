using Godot;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;
using MegaCrit.Sts2.addons.mega_text;

namespace SpireModManager;

// Row titles in our views: the alias with the original name small and dim beside it, cut with "…" to fit, a notepad
// before it when the mod has notes, and gold for mods turned on or off since launch. Vanilla View: the game's.
static class RowTitles
{
    const int OriginalNameSize = 16;
    const float TitleShift = 18; // room before the title in our views (the notepad sits there)
    const string ShiftMeta = "spire_mod_manager_title_shift";
    const string FocusMeta = "spire_mod_manager_game_focus"; // the row's focus mode as the game's scene made it
    const string TickMeta = "spire_mod_manager_tick_shift";
    const float TickShift = 8;
    const string NotesCard = "SpireModManagerNotesCard";
    public const string NoteTipName = "SpireModManagerNoteTip"; // the notepad's "Contains note"

    public const float RowHeight = 56; // our views (the game's: 72), with RowGap between rows
    public const int RowGap = 8;
    const string MarkName = "SpireModManagerEditedMark", HotName = "SpireModManagerTitleHover", RestName = "SpireModManagerOriginalName";
    const string FullMeta = "spire_mod_manager_full_title", FittedMeta = "spire_mod_manager_fitted_title";

    public static void Apply(IEnumerable<NModMenuRow> rows, bool vanilla)
    {
        foreach (var row in rows)
        {
            Unfitted.Remove(row.GetInstanceId()); // this redraw's fit replaces one still waiting (none in Vanilla View)
            var name = row.Mod?.manifest?.name ?? row.Mod?.manifest?.id ?? "";
            var title = row.GetNode<MegaRichTextLabel>("Title");
            // The game's label refits its text on every box or text change, and a view switch changes the box several
            // times: off while it's set, then fitted once (Fit in our views, FitVanilla in Vanilla View).
            title.AutoSizeEnabled = false;
            var info = vanilla ? null : Store.InfoFor(row.Mod?.manifest?.id);
            // Our views: smaller text in a shorter row with a smaller tickbox and no Steam/folder icon; the
            // title stretches with the (wider) row.
            row.CustomMinimumSize = new Vector2(0, vanilla ? 72 : RowHeight);
            row.GetNode<Control>("PlatformIcon").Visible = vanilla;
            // Focusable in our views, as on the beta, so going to a mod lights its row through focus (stable's row isn't).
            // Vanilla View: as the game's scene has it.
            if (!row.HasMeta(FocusMeta))
                row.SetMeta(FocusMeta, (int)row.FocusMode);
            row.FocusMode = vanilla ? (Control.FocusModeEnum)(int)row.GetMeta(FocusMeta) : Control.FocusModeEnum.All;
            Look(row, RowState.Selected(row), RowState.Focused(row));
            var tickbox = row.GetNode<Control>("Tickbox");
            tickbox.PivotOffset = tickbox.Size / 2;
            tickbox.Scale = vanilla ? Vector2.One : new Vector2(0.68f, 0.68f);
            // A little to the left in our views: the pencil goes past it, at the row's far right.
            var tickShift = vanilla ? 0 : -TickShift;
            var tickWas = row.HasMeta(TickMeta) ? (float)row.GetMeta(TickMeta) : 0;
            (tickbox.OffsetLeft, tickbox.OffsetRight) = (tickbox.OffsetLeft + tickShift - tickWas, tickbox.OffsetRight + tickShift - tickWas);
            row.SetMeta(TickMeta, tickShift);
            // The right anchor only on a view switch: set on every redraw it flipped back and forth, refitting the text
            // each time (most of a redraw's time).
            if (title.AnchorRight != (vanilla ? 0 : 1))
                (title.AnchorRight, title.OffsetRight) = vanilla ? (0f, 562f) : (1f, -90f);
            // Relative moves only: Load Order shifts the title too, for its grip.
            var shift = vanilla ? 0 : TitleShift;
            title.OffsetLeft += shift - (row.HasMeta(ShiftMeta) ? (float)row.GetMeta(ShiftMeta) : 0);
            row.SetMeta(ShiftMeta, shift);
            title.BbcodeEnabled = true;
            // The plain name in Vanilla View; in ours Fit sets the text (setting it here too refitted it every redraw).
            if (vanilla)
                title.Text = Escape(name);
            if (title.HasThemeColorOverride("default_color")) // (removing one restyles the text, even when there is none)
                title.RemoveThemeColorOverride("default_color"); // Better Mod Menu's name colour (its page restyles its own)
            Layout.FontSize(title, vanilla ? Layout.RowTitleVanillaSize : Layout.RowTitleSize);
            // Shown again by Fit, in our views.
            foreach (var part in new[] { MarkName, HotName, RestName })
                if (row.GetNodeOrNull<Control>(part) is { } old)
                    old.Visible = false;
            if (!vanilla)
            {
                // The notepad: notes on the main list only. An alias alone puts none: it already shows beside the name.
                var marked = !Store.Data.LoadOrderView && info?.Notes is { Length: > 0 };
                // Fitted once this redraw has placed the title (Load Order moves it right).
                var shown = row;
                Callable.From(() => Fit(shown, name, info?.Alias, marked)).CallDeferred();
            }
        }
        MarkChanged(rows, vanilla);
    }

    // Cuts the title to fit, then places the notepad and the hover area; a row out of sight is fitted once it shows.
    static void Fit(NModMenuRow row, string name, string? alias, bool marked)
    {
        if (!GodotObject.IsInstanceValid(row) || row.Mod is not { } mod || Store.Data.VanillaView) // (switched since it was asked for)
            return;
        var title = row.GetNode<MegaRichTextLabel>("Title");
        if (!row.IsVisibleInTree() || title.Size.X <= 4)
        {
            FitLater(row, title, () => Fit(row, name, alias, marked));
            return;
        }
        var font = title.GetThemeFont("normal_font");
        float Width(string text, int size) => Measure(font, text, size);
        var markSize = Icons.Notepad.GetSize();
        var room = title.Size.X - 4;
        var main = Cut(font, alias ?? name, Layout.RowTitleSize, room);
        var text = Escape(main);
        var mainWidth = Width(main, Layout.RowTitleSize);
        (float X, float Width, bool Cut)? originalAt = null; // where the original name went, if shown
        if (alias != null)
        {
            var space = Width(" ", Layout.RowTitleSize);
            const int gap = 2;
            var left = room - mainWidth - gap * space;
            var original = left > Width("……", OriginalNameSize) ? Cut(font, name, OriginalNameSize, left) : "";
            if (original.Length > 0)
            {
                text += new string(' ', gap) + $"[color=#fff6e280][font_size={OriginalNameSize}]{Escape(original)}[/font_size][/color]";
                originalAt = (mainWidth + gap * space, Width(original, OriginalNameSize), original != name);
            }
        }
        AutoSize(title, text); // in our views, smaller only for text taller than the row has room for (some languages' letters)
        var lineHeight = font.GetHeight(Layout.RowTitleSize);
        var top = title.Position.Y + Math.Max(0, (title.Size.Y - lineHeight) / 2); // the text sits in the middle of the title's box

        // Hovering the text (not the whole row): the mod's card.
        var hot = row.GetNodeOrNull<Control>(HotName);
        if (hot == null)
        {
            hot = new Control { Name = HotName, MouseFilter = Control.MouseFilterEnum.Pass };
            row.AddChild(hot);
            // Cut short: the card starts with the whole title. (Alt: the card with nothing cut.)
            var watched = hot;
            string? Card(bool full) => row.Mod is { } shown
                ? (watched.GetMeta(FullMeta, "").AsString() is { Length: > 0 } title ? $"[b]{Escape(title)}[/b]\n" : "") + ModDetails.Card(shown, full) : null;
            Hover.Watch(hot, NotesCard, () => Card(false), full: () => Card(true));
        }
        // Over the main title only (the alias), not the original name beside it.
        (hot.Position, hot.Size, hot.Visible) = (new Vector2(title.Position.X, top), new Vector2(mainWidth, lineHeight), true);
        hot.SetMeta(FullMeta, main != (alias ?? name) ? alias ?? name : "");
        // The original name, when cut short: the whole of it in a plain tooltip (a second over it).
        var rest = row.GetNodeOrNull<Control>(RestName);
        if (rest == null)
        {
            rest = new Control { Name = RestName, MouseFilter = Control.MouseFilterEnum.Pass };
            row.AddChild(rest);
            var shown = rest;
            Hover.Watch(rest, "SpireModManagerFullText", () => shown.GetMeta(FullMeta, "").AsString() is { Length: > 0 } whole ? Escape(whole) : null, Hover.Timing.Slow);
        }
        rest.Visible = originalAt is { Cut: true };
        if (originalAt is { } at)
        {
            (rest.Position, rest.Size) = (new Vector2(title.Position.X + at.X, top), new Vector2(at.Width, lineHeight));
            rest.SetMeta(FullMeta, name);
        }

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
                // A plain tooltip, not the mod's card (the title's hover has that).
                Hover.Watch(mark, NoteTipName, () => Escape(Menus.Text("SPIRE_MOD_MANAGER.CONTAINS_NOTE")), Hover.Timing.Plain);
            }
            mark.Visible = true;
            mark.Position = new Vector2(title.Position.X - markSize.X - 5, top + (lineHeight - markSize.Y) / 2 - 1); // in the room before the title
        }
    }

    // A row out of sight may not be laid out yet (a folded category's has no width), and its title cut to that showed
    // no name (#151): its fit waits until the row shows and has its width.
    static readonly Dictionary<ulong, Action> Unfitted = [];
    const string WatchedMeta = "spire_mod_manager_fit_watched";

    static void FitLater(NModMenuRow row, MegaRichTextLabel title, Action fit)
    {
        var id = row.GetInstanceId();
        Unfitted[id] = fit;
        if (row.HasMeta(WatchedMeta))
            return;
        row.SetMeta(WatchedMeta, true);
        void Check()
        {
            if (!Unfitted.ContainsKey(id))
                return;
            Callable.From(() =>
            {
                if (GodotObject.IsInstanceValid(row) && row.IsVisibleInTree() && Unfitted.Remove(id, out var waiting))
                    waiting();
            }).CallDeferred();
        }
        row.VisibilityChanged += Check;
        title.Resized += Check;
        row.TreeExiting += () => Unfitted.Remove(id);
    }

    // Vanilla View, once the redraw has done with the rows (their titles' boxes change until then): the game's
    // fitting, before Better Mod Menu restyles the names on its page.
    public static void FitVanilla(IEnumerable<NModMenuRow> rows)
    {
        foreach (var row in rows)
        {
            var title = row.GetNode<MegaRichTextLabel>("Title");
            AutoSize(title, title.Text);
        }
    }

    // Turns the game's fitting back on once the title has its place and text. Layout.Refit fits it once if its box, size
    // or text changed meanwhile (FittedMeta), so the label doesn't queue a fitting of its own as well.
    static void AutoSize(MegaRichTextLabel title, string text)
    {
        var fitted = title.Text == text && title.GetMeta(FittedMeta, "").AsString() == text;
        title.AutoSizeEnabled = !Layout.CanRefit;
        title.Text = text;
        title.AutoSizeEnabled = true;
        Layout.Refit(title, now: true, newText: !fitted);
        if (!fitted)
            title.SetMeta(FittedMeta, text);
    }

    // The text as it fits in width at this size: whole, or cut short with "…" (empty when not even that fits).
    // Row titles, and the info panel's title and source line.
    public static string Cut(Font font, string full, int size, float width)
    {
        float Width(string text) => Measure(font, text, size);
        if (Width(full) <= width)
            return full;
        var length = full.Length;
        while (length > 0 && Width(full[..length].TrimEnd() + "…") > width)
            length--;
        return length > 0 ? full[..length].TrimEnd() + "…" : "";
    }

    // Text widths per font and size, measured once (shaping is slow, and the font's own cache is too small for a long
    // list); cleared now and then, as cut texts add many.
    static readonly Dictionary<(Font Font, int Size, string Text), float> Widths = [];

    static float Measure(Font font, string text, int size)
    {
        if (Widths.TryGetValue((font, size, text), out var width))
            return width;
        if (Widths.Count >= 10000)
            Widths.Clear();
        return Widths[(font, size, text)] = font.GetStringSize(text, fontSize: size).X;
    }

    // Our views: a mod turned on since launch gets a bright gold title, one turned off a dim one; otherwise the game's
    // colour.
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

    // The row's highlight in our views: one rounded shape for every state, a little wider than the game's; a faint tint
    // under the mouse, a brighter one with an even gold rim for the mod in the info panel. Vanilla View: the game's.
    public const int CornerRadius = 4;
    public const int SelectedRim = 2;
    public static readonly Vector4 LookInset = new(-6, -3, 6, 3); // left, top, right, bottom offsets from the row
    const string GameLookMeta = "spire_mod_manager_game_look", GameBoxMeta = "spire_mod_manager_game_box";
    static readonly Color Gamelike = MegaCrit.Sts2.Core.Helpers.StsColors.darkBlue, GameSelected = MegaCrit.Sts2.Core.Helpers.StsColors.blue;
    public static readonly Color Gold = new(0.937f, 0.784f, 0.318f, 0.9f);
    static StyleBoxFlat? _hover, _selected;

    static StyleBoxFlat Box(Color fill, Color? rim = null)
    {
        var box = new StyleBoxFlat { BgColor = fill };
        box.SetCornerRadiusAll(CornerRadius);
        if (rim is { } edge)
        {
            box.BorderColor = edge;
            box.SetBorderWidthAll(SelectedRim);
        }
        return box;
    }

    // Called on every change of the row's state (focus, selection; patches on the game's row) and each redraw.
    public static void Look(NModMenuRow row, bool selected, bool focused)
    {
        if (!RowState.Found)
            return; // a game update renamed the row's own state: we can't tell its look, so it stays the game's
        if (row.GetNodeOrNull<Panel>("SelectionHighlight") is not { } look)
            return;
        if (!look.HasMeta(GameLookMeta))
        {
            look.SetMeta(GameLookMeta, look.GetThemeStylebox("panel"));
            look.SetMeta(GameBoxMeta, new Vector4(look.OffsetLeft, look.OffsetTop, look.OffsetRight, look.OffsetBottom));
        }
        if (Store.Data.VanillaView)
        {
            // The game's panel, size and colours (as its row would set them).
            look.AddThemeStyleboxOverride("panel", (StyleBox)look.GetMeta(GameLookMeta));
            var box = (Vector4)look.GetMeta(GameBoxMeta);
            (look.OffsetLeft, look.OffsetTop, look.OffsetRight, look.OffsetBottom) = (box.X, box.Y, box.Z, box.W);
            look.Modulate = selected ? new Color(GameSelected, 0.25f) : focused ? new Color(Gamelike, 0.25f) : Colors.Transparent;
            return;
        }
        _hover ??= Box(new Color(0.45f, 0.62f, 0.85f, 0.1f));
        _selected ??= Box(new Color(0.16f, 0.3f, 0.48f, 0.55f), Gold);
        (look.OffsetLeft, look.OffsetTop, look.OffsetRight, look.OffsetBottom) = (LookInset.X, LookInset.Y, LookInset.Z, LookInset.W);
        look.AddThemeStyleboxOverride("panel", selected ? _selected : _hover);
        look.Modulate = selected || focused ? Colors.White : Colors.Transparent;
    }

    static string Escape(string text) => text.Replace("[", "[lb]");
}
