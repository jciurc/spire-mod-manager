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
    /// <summary>A banner, not yet in the tree. In a container it fills the width it's given and grows as its text wraps; under a plain Control, size it with anchors and watch its MinimumSizeChanged.</summary>
    public static Control Create(BannerOptions options) => ApiGuard.Run<Control>("Banners.Create", new Control { Visible = false }, () =>
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
        var (bar, fit) = BannerBar.Make(options.Text, null, buttons, options.Kind == BannerKind.Warning ? Look.Warning : Look.Problem,
            options.Name ?? "SpireModManagerSharedBanner", "Text", b => b.GetParent() is Control parent ? parent.Size.X : b.Size.X);
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
    });
}
