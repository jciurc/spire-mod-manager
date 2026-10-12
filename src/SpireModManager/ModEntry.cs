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
        Installed.Watch();
        Log.Info($"[{ModId}] Initialized{(IsDevRun ? " (dev run)" : "")}");
    }

    // A picture shipped in our pack (images/), loaded from its bytes: the pack holds plain PNGs, not
    // imported textures. Null if it's missing.
    public static Texture2D? Art(string file)
    {
        var bytes = Godot.FileAccess.GetFileAsBytes($"res://{ModId}/images/{file}");
        var image = new Image();
        return bytes.Length > 0 && image.LoadPngFromBuffer(bytes) == Error.Ok ? ImageTexture.CreateFromImage(image) : null;
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
        var args = RelaunchArgs();
        Log.Info($"[{ModId}] Relaunching with args: {string.Join(' ', args)}");
        OS.SetRestartOnExit(true, args);
        if (NGame.Instance is { } game)
            game.Quit();
        else
            ((SceneTree)Engine.GetMainLoop()).Quit();
    }

    // This session's arguments plus the current window mode, so the new copy opens the way this one looks
    // instead of starting in a small window until the game applies its display settings. Window flags
    // from an earlier relaunch are replaced, not repeated.
    public static string[] RelaunchArgs()
    {
        var args = new List<string>();
        var given = OS.GetCmdlineArgs();
        for (var i = 0; i < given.Length; i++)
        {
            if (given[i] is "--fullscreen" or "-f" or "--maximized" or "-m" or "--windowed" or "-w")
                continue;
            if (given[i] is "--resolution" or "--position")
            {
                i++; // and its value
                continue;
            }
            args.Add(given[i]);
        }
        switch (DisplayServer.WindowGetMode())
        {
            case DisplayServer.WindowMode.Fullscreen or DisplayServer.WindowMode.ExclusiveFullscreen:
                args.Add("--fullscreen");
                break;
            case DisplayServer.WindowMode.Maximized:
                args.Add("--maximized");
                break;
            default:
                var size = DisplayServer.WindowGetSize();
                var position = DisplayServer.WindowGetPosition();
                args.AddRange(["--windowed", "--resolution", $"{size.X}x{size.Y}", "--position", $"{position.X},{position.Y}"]);
                break;
        }
        return args.ToArray();
    }

    // True when the saved mod list differs from what this session loaded, i.e. a relaunch would change
    // something: the vanilla Modding screen's pending-changes test, plus the load order and mods installed
    // since launch that are on.
    public static bool HasPendingChanges()
    {
        var settings = SaveManager.Instance.SettingsSave.ModSettings;
        return LoadOrder.OrderChanged || Installed.Mods.Any(Installed.On) || ModManager.Mods.Any(mod =>
        {
            var disabled = settings?.IsModDisabled(mod) ?? false;
            return (mod.state == ModLoadState.Disabled && !disabled) || (mod.state == ModLoadState.Loaded && disabled);
        });
    }

    // Adds a button in the style of the vanilla Get Mods button, sized freely (the art is a 9-slice).
    // art: our own picture instead of the game's button art, drawn whole (no 9-slice); hover and press
    // still brighten and squash it like the game's buttons.
    public static NButton AddButton(Node parent, string locKey, Vector2 size, Action onPressed, Texture2D? art = null)
    {
        var button = ResourceLoader.Load<PackedScene>(ButtonScenePath).Instantiate<NButton>();
        parent.AddChild(button);
        button.Size = size;
        button.PivotOffset = size / 2;

        // Draw the art at its native height and scale it down, so the slanted corners keep their shape.
        var frame = button.GetNode<NinePatchRect>("Visuals/Image/Frame");
        if (art != null)
        {
            frame.Texture = art;
            (frame.PatchMarginLeft, frame.PatchMarginRight) = (0, 0);
        }
        var scale = size.Y / (art?.GetHeight() ?? ButtonArtHeight);
        frame.Scale = new Vector2(scale, scale);
        frame.Size = size / scale;

        button.GetNode<MegaLabel>("Visuals/Label").SetTextAutoSize(new LocString("settings_ui", locKey).GetFormattedText());
        button.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(_ => onPressed()));
        return button;
    }
}
