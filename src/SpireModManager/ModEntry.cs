using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;
using MegaCrit.Sts2.addons.mega_text;

namespace SpireModManager;

[ModInitializer(nameof(Init))]
public static class ModEntry
{
    public const string ModId = "SpireModManager";

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
}

// Adds a "Relaunch Game" button to Settings > Modding, styled as a copy of the Get Mods button and placed
// at the right end of the pending-changes row.
// Runs first: postfixes share one call chain, so an exception in another mod's postfix on this method
// (Better Mod Menu 1.1.1 throws here on the 0.111 beta) would otherwise skip this one.
[HarmonyPatch(typeof(NModdingScreen), nameof(NModdingScreen._Ready))]
static class ModdingScreenPatch
{
    [HarmonyPriority(Priority.First)]
    static void Postfix(NModdingScreen __instance)
    {
        try
        {
            var template = __instance.GetNode<NButton>("%GetModsButton");
            // No signal flag: the copy must not inherit the Get Mods click handler.
            var button = (NButton)template.Duplicate((int)(Node.DuplicateFlags.Groups | Node.DuplicateFlags.Scripts | Node.DuplicateFlags.UseInstantiation));
            button.Name = "SpireModManagerRelaunchButton";
            button.UniqueNameInOwner = false;
            __instance.AddChild(button);

            // Screen-space slot: right edge matches the mod info panel (x 1727), centred on the pending-changes row (y 1012-1056).
            // Fixed size from the scene file (170x70): the template's runtime size and fade state aren't settled during _Ready.
            var size = new Vector2(170, 70);
            button.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
            button.Size = size;
            button.Position = new Vector2(1727 - size.X, 1034 - size.Y / 2);
            button.Visible = true;
            button.Modulate = Colors.White;

            button.GetNode<MegaLabel>("Visuals/Label")
                .SetTextAutoSize(new LocString("settings_ui", "SPIRE_MOD_MANAGER.RELAUNCH_BUTTON").GetFormattedText());
            button.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(_ => ModEntry.Relaunch()));
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.ModId}] Could not add the relaunch button: {e}");
        }
    }
}
