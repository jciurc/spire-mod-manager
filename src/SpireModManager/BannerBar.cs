using Godot;

namespace SpireModManager;

// A banner: its text (hovering it shows card, when there is one), then its buttons, on a rounded bar in its colour. The
// text takes its one-line width, or the room width(bar) leaves it after the margins and the buttons, wrapping there (a
// wrapping label asks for no width of its own); fit does that again, for whoever places it (the Modding page's banners
// under the search box, LoadOrder). With no width (other mods' banners, through the API's Banners, in layouts SMM
// can't measure), the text fills the bar and wraps at whatever width the bar is given, the buttons at its end; fit
// does nothing.
static class BannerBar
{
    public const float Height = 34, Gap = 6;
    public const string CardName = "SpireModManagerBannerCard";

    public static (PanelContainer Bar, Action Fit) Make(string message, Func<string?>? card, IReadOnlyList<Control> buttons, Color color,
        string name, string textName, Func<Control, float>? width)
    {
        var bar = new PanelContainer { Name = name };
        var look = new StyleBoxFlat { BgColor = color, ContentMarginLeft = 12, ContentMarginRight = 8, ContentMarginTop = 4, ContentMarginBottom = 4 };
        look.SetCornerRadiusAll(RowTitles.CornerRadius); // rounded like the rows and the search box
        bar.AddThemeStyleboxOverride("panel", look);
        var line = new HBoxContainer { Name = "Line" }; // nothing in it expands: all of it sits at the left
        line.AddThemeConstantOverride("separation", (int)Gap);
        bar.AddChild(line);
        var text = new Label
        {
            Name = textName, Text = message, VerticalAlignment = VerticalAlignment.Center, AutowrapMode = TextServer.AutowrapMode.WordSmart,
            MouseFilter = card == null ? Control.MouseFilterEnum.Ignore : Control.MouseFilterEnum.Pass,
        };
        text.AddThemeFontOverride("font", ModEntry.Font("res://themes/kreon_regular_shared.tres"));
        text.AddThemeFontSizeOverride("font_size", 16);
        text.AddThemeColorOverride("font_color", Look.Cream);
        line.AddChild(text);
        // Hovering the text: its card at once, under the banner from the text's left edge. (A redraw makes a new banner,
        // so a card open over the old one goes with it.)
        if (card != null)
            Hover.Watch(text, CardName, card, Hover.Timing.Instant, width: 440,
                place: () => (new Vector2(text.GlobalPosition.X, bar.GlobalPosition.Y + bar.Size.Y + 6), false, false));
        foreach (var button in buttons)
        {
            button.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            line.AddChild(button);
        }
        if (width == null)
        {
            text.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            return (bar, () => { });
        }
        var oneLine = Mathf.Ceil(text.GetThemeFont("font").GetStringSize(message, fontSize: 16).X + text.GetThemeStylebox("normal").GetMinimumSize().X) + 1;
        void Fit()
        {
            if (!GodotObject.IsInstanceValid(bar))
                return;
            var room = width(bar) - look.GetMinimumSize().X - buttons.Where(b => b.Visible).Sum(b => b.GetCombinedMinimumSize().X + Gap);
            text.CustomMinimumSize = new Vector2(Math.Min(oneLine, Math.Max(room, 0)), 0);
        }
        Fit();
        return (bar, Fit);
    }
}
