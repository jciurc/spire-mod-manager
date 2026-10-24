using Godot;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;

namespace SpireModManager;

// The missing-mods list (the missing-mods banner's Show): the active profile's mods that are on but not installed, as
// they were when it opened. Each: its name (its Workshop page, where the profile knows the item), then Subscribe, its
// progress as Steam downloads it, or what came of it. Subscribe to all for every one on the Workshop not on its way yet.
// Closing it stops nothing: Steam keeps downloading, and the banner counts down as the game finds each mod.
static class MissingMods
{
    public const string Name = "SpireModManagerMissingMods";
    static readonly Color Cream = new(1, 0.964706f, 0.886275f), Gold = new(0.937f, 0.784f, 0.318f), Dim = new(1, 0.964706f, 0.886275f, 0.55f);
    static readonly Color Failed = new(1, 0.5f, 0.44f), Done = new(0.6f, 0.86f, 0.55f);

    public static bool IsOpen => Modal.IsOpen(Name);

    // True when there was one to close.
    public static bool Close() => Modal.Close(Name);

    public static void Show(NModdingScreen screen)
    {
        var listed = Profiles.Missing();
        if (Modal.Open(screen, Name, 640, 12) is not { } column)
            return;
        column.AddChild(Modal.Title("SPIRE_MOD_MANAGER.MISSING_TITLE"));
        var body = new Label
        {
            Name = "Body", Text = Menus.Text(Subscriptions.Available ? "SPIRE_MOD_MANAGER.MISSING_BODY" : "SPIRE_MOD_MANAGER.MISSING_BODY_NO_STEAM"),
            AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(588, 0),
        };
        Style(body, 18, Cream);
        column.AddChild(body);
        var rows = new VBoxContainer { Name = "Rows", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        rows.AddThemeConstantOverride("separation", 8);
        var scroll = new ScrollContainer
        {
            Name = "Scroll", CustomMinimumSize = new Vector2(588, Math.Min(320, 20 + 40 * listed.Count)),
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        scroll.AddChild(rows);
        column.AddChild(scroll);
        var buttons = new HBoxContainer { Name = "Buttons", Alignment = BoxContainer.AlignmentMode.End };
        buttons.AddThemeConstantOverride("separation", 8);
        var all = Menus.SmallButton("", () => Subscriptions.Subscribe(Ready(listed)), name: "SubscribeAllButton");
        Hover.Tip(all, Menus.Text("SPIRE_MOD_MANAGER.SUBSCRIBE_TIP"));
        buttons.AddChild(all);
        buttons.AddChild(Menus.SmallButton(Menus.Text("SPIRE_MOD_MANAGER.CLOSE"), () => Close(), name: "CloseMissingButton"));
        column.AddChild(buttons);

        void Fill()
        {
            if (!GodotObject.IsInstanceValid(rows))
                return;
            foreach (var old in rows.GetChildren())
            {
                rows.RemoveChild(old);
                old.QueueFree();
            }
            foreach (var mod in listed)
                rows.AddChild(Row(mod));
            var ready = Ready(listed).Count;
            all.Text = Menus.Format("SPIRE_MOD_MANAGER.SUBSCRIBE_ALL", ("count", Menus.Number(ready)));
            all.Disabled = ready == 0 || !Subscriptions.Available;
        }
        Fill();
        // Statuses as they change, and mods as the game finds them, until it closes.
        Subscriptions.Changed += Fill;
        SpireModManager.Installed.Added += Fill;
        column.TreeExiting += () =>
        {
            Subscriptions.Changed -= Fill;
            SpireModManager.Installed.Added -= Fill;
        };
    }

    // The listed mods on the Workshop that Subscribe would start: not installed, not on their way, not done.
    static List<ulong> Ready(List<Profiles.MissingMod> listed) => [.. listed.Where(m => !IsInstalled(m)).Select(m => m.Item).OfType<ulong>()
        .Where(item => Subscriptions.Of(item).State is Subscriptions.State.None or Subscriptions.State.Failed)];

    static bool IsInstalled(Profiles.MissingMod mod) => ModManager.Mods.Any(m => m.manifest?.id == mod.Id && m.modSource == mod.Source);

    // One mod: its name (a link to its Workshop page, with the external-link mark), then what can be done or what's
    // happening: Subscribe; Subscribing.. or its download's progress; installed; why it failed (and Subscribe again).
    static Control Row(Profiles.MissingMod mod)
    {
        var row = new HBoxContainer { Name = mod.Id, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", 10);
        var name = new RichTextLabel
        {
            Name = "Name", BbcodeEnabled = true, FitContent = true, ScrollActive = false, AutowrapMode = TextServer.AutowrapMode.Off,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            MouseFilter = Control.MouseFilterEnum.Pass, ClipContents = true,
        };
        Hover.Fonts(name, 18);
        var shown = Hover.Escape(mod.Name);
        name.Text = mod.Item is { } item
            ? $"[url={ProfileCsv.WorkshopLink(item)}][color={ModDetails.ModLinkHex}]{shown}{LinkMark.Tag(18, ModDetails.ModLinkHex)}[/color][/url]" : shown;
        name.MetaUnderlined = false; // our links: their colour and the pointing hand
        name.MetaClicked += meta =>
        {
            if (meta.AsString() is var link && ProfileCsv.IsWorkshopLink(link))
                OS.ShellOpen(link);
        };
        row.AddChild(name);

        if (IsInstalled(mod))
            row.AddChild(Status("SPIRE_MOD_MANAGER.SUBSCRIBE_INSTALLED", Done));
        else if (mod.Item is not { } workshop)
            row.AddChild(Status("SPIRE_MOD_MANAGER.MISSING_NOT_WORKSHOP", Dim));
        else
        {
            var status = Subscriptions.Of(workshop);
            switch (status.State)
            {
                case Subscriptions.State.Subscribing:
                    row.AddChild(Status("SPIRE_MOD_MANAGER.SUBSCRIBE_SUBSCRIBING", Dim));
                    break;
                case Subscriptions.State.Downloading:
                    row.AddChild(Bar(status.Progress));
                    row.AddChild(Text(Menus.Format("SPIRE_MOD_MANAGER.SUBSCRIBE_DOWNLOADING", ("percent", Percent(status.Progress))), Cream, "Status"));
                    break;
                case Subscriptions.State.Installed:
                    row.AddChild(Status("SPIRE_MOD_MANAGER.SUBSCRIBE_INSTALLED", Done));
                    break;
                default:
                    if (status.State == Subscriptions.State.Failed)
                        row.AddChild(Text(status.Error ?? "", Failed, "Status"));
                    var subscribe = Menus.SmallButton(Menus.Text("SPIRE_MOD_MANAGER.SUBSCRIBE"), () => Subscriptions.Subscribe([workshop]),
                        disabled: !Subscriptions.Available, name: "SubscribeButton");
                    Hover.Tip(subscribe, Menus.Text("SPIRE_MOD_MANAGER.SUBSCRIBE_TIP"));
                    subscribe.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
                    row.AddChild(subscribe);
                    break;
            }
        }
        return row;
    }

    // A download's share as the player's language writes a percentage.
    public static string Percent(float progress) => (Math.Clamp(progress, 0, 1)).ToString("P0", Menus.Culture);

    static ProgressBar Bar(float progress)
    {
        var bar = new ProgressBar
        {
            Name = "Progress", MinValue = 0, MaxValue = 1, Step = 0, Value = progress, ShowPercentage = false,
            CustomMinimumSize = new Vector2(140, 10), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter, MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        StyleBoxFlat Box(Color color)
        {
            var box = new StyleBoxFlat { BgColor = color };
            box.SetCornerRadiusAll(3);
            return box;
        }
        bar.AddThemeStyleboxOverride("background", Box(new Color(Cream, 0.12f)));
        bar.AddThemeStyleboxOverride("fill", Box(Gold));
        return bar;
    }

    static Label Status(string key, Color color) => Text(Menus.Text(key), color, "Status");

    static Label Text(string text, Color color, string name)
    {
        var label = new Label { Name = name, Text = text, VerticalAlignment = VerticalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore };
        Style(label, 16, color);
        return label;
    }

    static void Style(Control control, int size, Color color)
    {
        control.AddThemeFontOverride("font", ModEntry.Font("res://themes/kreon_regular_shared.tres"));
        control.AddThemeFontSizeOverride("font_size", size);
        control.AddThemeColorOverride("font_color", color);
    }
}
