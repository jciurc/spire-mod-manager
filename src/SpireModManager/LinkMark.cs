using Godot;

namespace SpireModManager;

// The external-link mark (Icons.ExternalLink) after the text of what takes the player out of the game: the info
// panel's Workshop link and Changelog, an import's Workshop links, Open translations folder. As tall as the text's
// capitals, a no-break space after it, in its colour. It's part of the link, so hovering or clicking it is hovering or
// clicking the link, and nothing around the link moves but by its width. Not on Open Folder, a copy of the game's Get
// Mods button (no room in its art, and the game's own has none), nor in the data notice's folder link (the game's
// popup, whose text sizes itself).
static class LinkMark
{
    public const string Group = "SpireModManagerLinkMark"; // the controls that draw one (Add)
    const float CapHeight = 708 / 1024f; // Kreon's, in ems
    const string Gap = "\u00a0"; // between the text and the mark: no line break there

    // Its size in pixels after text at fontSize: the text's cap height (11 at 16).
    public static int Size(int fontSize) => Math.Max(7, (int)MathF.Round(fontSize * CapHeight));

    // The mark in a RichTextLabel's BBCode, for inside the link's [url] and [color] (its underline, if any, stays that
    // colour), right after its text, its foot on the text's baseline like a capital (centred on the line, the default, it
    // sat low: the line's middle is below the capitals'). Not for the game's MegaRichTextLabel, which measures an [img]
    // through the game's asset cache.
    public static string Tag(int fontSize, string color) =>
        $"{Gap}[img color={color} valign=bottom,baseline]{Icons.ExternalLink(Size(fontSize)).ResourcePath}[/img]";

    // The mark drawn by a one-line LinkButton or Label after its text, in its text's colour at each redraw (a link's
    // hover colour while it's lit). A LinkButton shows it while it links somewhere: enabled, with a Uri.
    public static void Add(Control control)
    {
        control.AddToGroup(Group);
        control.Draw += () =>
        {
            if (Rect(control) is not { } rect)
                return;
            var tint = Tint(control);
            control.DrawTexture(Icons.ExternalLink((int)rect.Size.X), rect.Position, tint);
#if DEVTOOLS
            control.SetMeta(DrawnMeta, tint);
#endif
        };
        Fit(control);
    }

#if DEVTOOLS
    // Self-test: the colour the control last drew its mark in (null: none since TestForget, or ever).
    const string DrawnMeta = "SpireModManagerLinkMarkDrawn";
    public static Color? TestDrawn(Control control) => control.HasMeta(DrawnMeta) ? control.GetMeta(DrawnMeta).AsColor() : null;
    public static void TestForget(Control control) => control.RemoveMeta(DrawnMeta);
#endif

    // The control's width with the mark (the text's without it): again after its text, or a LinkButton's Uri or
    // Disabled, changes.
    public static void Fit(Control control) => control.CustomMinimumSize = new Vector2(Rect(control)?.End.X ?? 0, control.CustomMinimumSize.Y);

    // The width the mark adds after the text: the gap, then the mark. 0 where it doesn't show.
    public static float Room(Control control)
    {
        if (!Shows(control))
            return 0;
        var fontSize = control.GetThemeFontSize("font_size");
        return control.GetThemeFont("font").GetStringSize(Gap, fontSize: fontSize).X + Size(fontSize);
    }

    // Where the mark goes in the control: after its text and the gap, its foot on the text's baseline, on whole pixels so
    // its 1 px lines stay crisp. Null where it doesn't show.
    public static Rect2? Rect(Control control)
    {
        if (!Shows(control))
            return null;
        var font = control.GetThemeFont("font");
        var fontSize = control.GetThemeFontSize("font_size");
        var text = control is LinkButton link ? link.Text : (control as Label)?.Text ?? "";
        var size = Size(fontSize);
        return new Rect2(MathF.Round(font.GetStringSize(text + Gap, fontSize: fontSize).X), MathF.Round(Baseline(control, text, font, fontSize)) - size, size, size);
    }

    // How far below the control's top it draws its text's baseline, from the text's ascent and height as they're shaped
    // (Layout.Line), not the font's own, which count every fallback: a mod that adds a taller one to the game's shared
    // Kreon put the mark low (Jordan, 22 Oct: "source external icon needs to move up 3-4px to line up with url"). A
    // LinkButton draws its text at its top; a Label places its line in its box by its vertical alignment, as Godot's
    // Label does (the room left, halved and cut to whole pixels for Center).
    public static float Baseline(Control control, string text, Font font, int fontSize)
    {
        var (ascent, height) = Layout.Line(text, font, fontSize);
        if (control is not Label label)
            return ascent;
        var style = label.GetThemeStylebox("normal");
        var room = label.Size.Y - style.GetMinimumSize().Y - height;
        var offset = label.VerticalAlignment switch
        {
            VerticalAlignment.Center => (int)(room / 2),
            VerticalAlignment.Bottom => room,
            _ => 0,
        };
        return style.GetMargin(Side.Top) + offset + ascent;
    }

    // The colour the control draws its text in now: a LinkButton's for its state, picked as LinkButton picks it; a
    // Label's font colour.
    static Color Tint(Control control)
    {
        if (control is not LinkButton link)
            return control.GetThemeColor("font_color");
        return link.GetDrawMode() switch
        {
            BaseButton.DrawMode.Hover => link.GetThemeColor("font_hover_color"),
            BaseButton.DrawMode.Pressed or BaseButton.DrawMode.HoverPressed =>
                link.GetThemeColor(link.HasThemeColor("font_pressed_color") ? "font_pressed_color" : "font_color"),
            BaseButton.DrawMode.Disabled => link.GetThemeColor("font_disabled_color"),
            _ => link.GetThemeColor(link.HasFocus() ? "font_focus_color" : "font_color"),
        };
    }

    static bool Shows(Control control) => control.IsInGroup(Group) && (control is not LinkButton link || (!link.Disabled && link.Uri.Length > 0));
}
