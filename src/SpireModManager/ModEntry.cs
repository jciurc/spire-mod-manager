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
        Log.Info($"[{ModId}] Initialized");
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
        return ModManager.Mods.Any(mod =>
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
