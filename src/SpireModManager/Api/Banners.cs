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
    /// <summary>A banner, not yet in the tree. Its text wraps at the width it's given, and it grows to fit: in a container it fills the width the container gives it (in an HBoxContainer, give it SizeFlags.ExpandFill); under a plain Control, give it a width with anchors or Size, and watch its MinimumSizeChanged for its height. If it can't be made, a plain panel with the text comes back.</summary>
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
        var (bar, _) = BannerBar.Make(options.Text ?? "", null, buttons, options.Kind == BannerKind.Warning ? Look.Warning : Look.Problem,
            options.Name ?? "SpireModManagerSharedBanner", "Text", width: null);
        made = bar;
        return bar;
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
