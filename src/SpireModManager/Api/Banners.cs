using Godot;

namespace SpireModManager.Api;

/// <summary>A banner's colour (values fixed; new kinds only added).</summary>
public enum BannerKind
{
    /// <summary>Something is wrong: SMM's red.</summary>
    Problem = 0,

    /// <summary>Worth knowing: SMM's amber.</summary>
    Warning = 1,
}

/// <summary>What a banner says and offers; every property is optional.</summary>
public sealed class BannerOptions
{
    /// <summary>The banner's text (plain, shown as it is); it wraps to the width the banner is given.</summary>
    public string Text { get; init; } = "";

    /// <summary>Its colour.</summary>
    public BannerKind Kind { get; init; } = BannerKind.Problem;

    /// <summary>Adds SMM's Dismiss button: a press hides the banner (Visible = false), then calls this. Remembering it, and showing the banner again, are yours.</summary>
    public Action? OnDismiss { get; init; }

    /// <summary>The banner's node name; carry your mod id.</summary>
    public string? Name { get; init; }
}

/// <summary>SMM's banners as a strip for you to place (SMM's own stacking on the Modding page stays SMM's).</summary>
public static class Banners
{
    /// <summary>A banner, not yet in the tree. In a VBoxContainer, MarginContainer or PanelContainer it fills the width it's given, and in an HBoxContainer the width its other controls leave it; its text wraps there and it grows to fit. Under a plain Control it takes the width its anchors and offsets give it (one line if they give none): watch its MinimumSizeChanged for its height. It fits again whenever its parent resizes. If it can't be made, a plain panel with the text comes back.</summary>
    public static Control Create(BannerOptions options) => ApiGuard.Run<Control?>("Banners.Create", null, () => Make(options)) ?? StandIn(options);

    static Control Make(BannerOptions options)
    {
        PanelContainer? made = null;
        var buttons = new List<Control>();
        if (options.OnDismiss is { } dismissed)
        {
            var safely = ApiGuard.Callback("a banner's OnDismiss", dismissed);
            buttons.Add(Menus.SmallButton(Menus.Text("SPIRE_MOD_MANAGER.SHARED_DISMISS"), () =>
            {
                if (made != null && GodotObject.IsInstanceValid(made))
                    made.Visible = false;
                safely();
            }, name: "Dismiss"));
        }
        var (bar, fit) = BannerBar.Make(options.Text ?? "", null, buttons, options.Kind == BannerKind.Warning ? Look.Warning : Look.Problem,
            options.Name ?? "SpireModManagerSharedBanner", "Text", Room);
        made = bar;
        // Fitted to its parent's width as it's added, and again whenever that changes (let go as it leaves).
        Control? watched = null;
        bar.TreeEntered += () =>
        {
            if (bar.GetParent() is Control parent)
                (watched = parent).Resized += fit;
            fit();
        };
        bar.TreeExiting += () =>
        {
            if (watched != null && GodotObject.IsInstanceValid(watched))
                watched.Resized -= fit;
            watched = null;
        };
        return bar;
    }

    // The width the banner's parent gives it (what its text may take, after the bar's margins and buttons): a container's
    // inside, less what the controls beside it in a row take; a plain Control's anchored span. With none, one line.
    static float Room(Control bar)
    {
        var room = bar.GetParent() switch
        {
            MarginContainer margins => margins.Size.X - margins.GetThemeConstant("margin_left") - margins.GetThemeConstant("margin_right"),
            PanelContainer panel => panel.Size.X - (panel.GetThemeStylebox("panel")?.GetMinimumSize().X ?? 0),
            HBoxContainer row => row.Size.X - row.GetChildren().OfType<Control>().Where(c => c != bar && c.Visible && !c.TopLevel)
                .Sum(c => c.GetCombinedMinimumSize().X + row.GetThemeConstant("separation")),
            Container container => container.Size.X,
            Control parent => parent.Size.X * (bar.AnchorRight - bar.AnchorLeft) + bar.OffsetRight - bar.OffsetLeft,
            _ => 0,
        };
        return room > 0 ? room : float.PositiveInfinity;
    }

    // What a caller gets when making one failed (logged): the text on a plain panel, so what it says still shows.
    static Control StandIn(BannerOptions? options)
    {
        try
        {
            var panel = new PanelContainer { Name = options?.Name ?? "SpireModManagerSharedBanner" };
            panel.AddChild(new Label { Name = "Text", Text = options?.Text ?? "", AutowrapMode = TextServer.AutowrapMode.WordSmart });
            return panel;
        }
        catch (Exception)
        {
            return new Control { Visible = false };
        }
    }
}
