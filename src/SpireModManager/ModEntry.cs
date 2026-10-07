using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.addons.mega_text;

namespace SpireModManager;

[ModInitializer(nameof(Init))]
public static class ModEntry
{
    public const string ModId = "SpireModManager";

    const string ButtonScenePath = $"res://{ModId}/scenes/menu_button.tscn";
    const float ButtonArtHeight = 110; // event_button.png is 284x110

    public static void Init()
    {
        new Harmony(ModId).PatchAll(typeof(ModEntry).Assembly);
        Log.Info($"[{ModId}] Initialized{(IsDevRun ? " (dev run)" : "")}");
    }

    public static bool BetterModMenuLoaded =>
        ModManager.Mods.Any(m => m.manifest?.id == "BetterModMenu" && m.state == ModLoadState.Loaded);

    // Bottom-left of the Modding screen, where our view toggle would be.
    public static void AddBetterModMenuNotice(Control screen)
    {
        var notice = new Label
        {
            Name = "SpireModManagerBmmNotice",
            Text = new LocString("settings_ui", "SPIRE_MOD_MANAGER.BMM_NOTICE").GetFormattedText(),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            VerticalAlignment = VerticalAlignment.Center,
            Position = new Vector2(255, 1000),
            Size = new Vector2(560, 68),
        };
        notice.AddThemeFontOverride("font", ResourceLoader.Load<Font>("res://themes/kreon_regular_shared.tres"));
        notice.AddThemeFontSizeOverride("font_size", 18);
        notice.AddThemeColorOverride("font_color", new Color(0.937f, 0.784f, 0.318f));
        screen.AddChild(notice);
    }

    // Set by tools/dev.cs for its sandboxed runs, so they can't be mistaken for the real game.
    public static bool IsDevRun { get; } = System.Environment.GetEnvironmentVariable("SPIRE_MOD_MANAGER_DEV") == "1";

    // "[DEV]" in the window title (taskbar, Alt+Tab) and a small tag at the top of the screen.
    public static void MarkDevRun(SceneTree tree)
    {
        if (!IsDevRun || tree.Root.HasNode("SpireModManagerDevTag"))
            return;
        if (!tree.Root.Title.StartsWith("[DEV]"))
            tree.Root.Title = "[DEV] " + tree.Root.Title;
        var layer = new CanvasLayer { Name = "SpireModManagerDevTag", Layer = 128 };
        var tag = new Label
        {
            Text = "DEV SANDBOX",
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        tag.AddThemeColorOverride("font_color", new Color(1, 0.35f, 0.35f, 0.8f));
        tag.AddThemeFontSizeOverride("font_size", 18);
        layer.AddChild(tag);
        tag.SetAnchorsPreset(Control.LayoutPreset.CenterTop);
        tag.OffsetLeft = -100;
        tag.OffsetRight = 100;
        tag.OffsetTop = 4;
        tree.Root.AddChild(layer);
    }

    // Starts a new copy of the game with the same arguments, then quits through the game's own path
    // so settings (mod toggles) and profile data are saved first. The new process inherits Steam's
    // environment (SteamAppId), so it attaches to the running Steam client without a Steam relaunch.
    public static void Relaunch()
    {
        Log.Info($"[{ModId}] Relaunching with args: {string.Join(' ', OS.GetCmdlineArgs())}");
        OS.SetRestartOnExit(true, OS.GetCmdlineArgs());
        if (NGame.Instance is { } game)
            game.Quit();
        else
            ((SceneTree)Engine.GetMainLoop()).Quit();
    }

    // True when the saved mod list differs from what this session loaded, i.e. a relaunch would change
    // something. Same test as the vanilla Modding screen's pending-changes warning.
    public static bool HasPendingChanges()
    {
        var settings = SaveManager.Instance.SettingsSave.ModSettings;
        return LoadOrder.OrderChanged || ModManager.Mods.Any(mod =>
        {
            var disabled = settings?.IsModDisabled(mod) ?? false;
            return (mod.state == ModLoadState.Disabled && !disabled) || (mod.state == ModLoadState.Loaded && disabled);
        });
    }

    // Adds a button in the style of the vanilla Get Mods button, sized freely (the art is a 9-slice).
    public static NButton AddButton(Node parent, string locKey, Vector2 size, Action onPressed)
    {
        var button = ResourceLoader.Load<PackedScene>(ButtonScenePath).Instantiate<NButton>();
        parent.AddChild(button);
        button.Size = size;
        button.PivotOffset = size / 2;

        // Draw the art at its native height and scale it down, so the slanted corners keep their shape.
        var frame = button.GetNode<NinePatchRect>("Visuals/Image/Frame");
        var scale = size.Y / ButtonArtHeight;
        frame.Scale = new Vector2(scale, scale);
        frame.Size = size / scale;

        button.GetNode<MegaLabel>("Visuals/Label").SetTextAutoSize(new LocString("settings_ui", locKey).GetFormattedText());
        button.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(_ => onPressed()));
        return button;
    }
}
