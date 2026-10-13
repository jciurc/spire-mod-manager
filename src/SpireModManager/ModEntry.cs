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

    // Patch classes that didn't apply, by name: a game update renamed or removed what they hook.
    static readonly List<string> _failedPatches = [];
    public static IReadOnlyList<string> FailedPatches => _failedPatches;

    // The patches our Modding page can't do without: the page itself; the main menu's startup (the baseline for
    // undo, Changes and the load order); the tickbox hook (profiles and undo follow it); the info panel's contents;
    // the selected mod (view switches and panel refreshes; our code calls the same game method). With one of them
    // missing, the page stays the game's, with a line saying the mod needs an update, rather than half of ours.
    // The others only add to it (looks, shortcuts, new-mod handling); without one, the page still runs.
    public static readonly IReadOnlyList<string> PageNeeds =
        [nameof(MainMenuPatch), nameof(ModdingScreenPatch), nameof(ModToggledPatch), nameof(ModInfoFillPatch), nameof(RowSelectedPatch)];

    public static bool PageWorks(IEnumerable<string> failed) => !failed.Any(name => PageNeeds.Contains(name));

    // Our page on the Modding screen, or the game's with the needs-update line.
    public static bool OurPage => PageWorks(_failedPatches);

    // That line, across the top of the game's page, above its list and info panel (their boxes: 255..1727, from y 83).
    public const string NeedsUpdateName = "SpireModManagerNeedsUpdate";

    public static Label ShowNeedsUpdate(Control screen)
    {
        if (screen.GetNodeOrNull<Label>(NeedsUpdateName) is { } shown)
            return shown;
        var line = new Label
        {
            Name = NeedsUpdateName, Text = Menus.Text("SPIRE_MOD_MANAGER.NEEDS_UPDATE"), VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore, Position = new Vector2(255, 24), Size = new Vector2(1727 - 255, 48),
        };
        line.AddThemeFontOverride("font", ResourceLoader.Load<Font>("res://themes/kreon_bold_shared.tres"));
        line.AddThemeFontSizeOverride("font_size", 22);
        line.AddThemeColorOverride("font_color", new Color(0.937f, 0.784f, 0.318f));
        line.AddThemeColorOverride("font_outline_color", new Color(0.0756f, 0.12084f, 0.18f));
        line.AddThemeConstantOverride("outline_size", 6);
        screen.AddChild(line);
        return line;
    }

    // Each patch class on its own, so one a game update breaks leaves the others working (PatchAll stops at the
    // first failure). Watching for new mods goes first: it needs no patch.
    public static void Init()
    {
        try
        {
            Installed.Watch();
        }
        catch (Exception e)
        {
            Log.Error($"[{ModId}] Could not watch for mods installed while the game runs: {e}");
        }
        var harmony = new Harmony(ModId);
        foreach (var type in AccessTools.GetTypesFromAssembly(typeof(ModEntry).Assembly))
        {
            try
            {
                if (type.IsDefined(typeof(HarmonyPatch), false))
                    harmony.CreateClassProcessor(type).Patch();
            }
            catch (Exception e)
            {
                _failedPatches.Add(type.Name);
                Log.Error($"[{ModId}] Could not patch {type.Name}; this game version may need an update of the mod: {e}");
            }
        }
        Log.Info($"[{ModId}] Initialized{(IsDevRun ? " (dev run)" : "")}"
            + (_failedPatches.Count > 0 ? $", without {string.Join(", ", _failedPatches)}" : ""));
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
