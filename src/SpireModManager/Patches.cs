using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;

namespace SpireModManager;

// Every patch body is wrapped in try/catch: postfixes on one method share a call chain, so an exception
// here would also skip other mods' postfixes. Priority.First keeps ours from being skipped by theirs
// (Better Mod Menu 1.1.1 throws in its NModdingScreen._Ready postfix on the 0.111 beta).

// "Spire Mod Manager" button in the main menu's lower-right corner, just above the vanilla
// "MODDED (n)" label, with a dot when a relaunch is pending.
[HarmonyPatch(typeof(NMainMenu), nameof(NMainMenu._Ready))]
static class MainMenuPatch
{
    [HarmonyPriority(Priority.First)]
    static void Postfix(NMainMenu __instance)
    {
        try
        {
            var menu = __instance;
            var button = ModEntry.AddButton(menu, "SPIRE_MOD_MANAGER.MAIN_MENU_BUTTON", new Vector2(260, 70),
                () => menu.SubmenuStack.PushSubmenuType<NModdingScreen>());
            button.Name = "SpireModManagerMenuButton";

            // Below the mod list that pops up when hovering "MODDED (n)", so that list draws on top.
            menu.MoveChild(button, menu.GetNode("%ModWarningContainer").GetIndex());

            // Anchored to the bottom-right like "MODDED (n)" (right edge -8, top -33), 10px above it.
            button.SetAnchorsPreset(Control.LayoutPreset.BottomRight);
            button.OffsetRight = -8;
            button.OffsetBottom = -43;
            button.OffsetLeft = button.OffsetRight - 260;
            button.OffsetTop = button.OffsetBottom - 70;

            var dot = button.GetNode<Control>("Dot");
            void Refresh()
            {
                button.Visible = !menu.SubmenuStack.SubmenusOpen;
                dot.Visible = ModEntry.HasPendingChanges();
            }
            Refresh();
            menu.SubmenuStack.Connect(NSubmenuStack.SignalName.StackModified, Callable.From(Refresh));
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.ModId}] Could not add the main menu button: {e}");
        }
    }
}

// "Relaunch Game" button at the right end of the Modding screen's pending-changes row.
[HarmonyPatch(typeof(NModdingScreen), nameof(NModdingScreen._Ready))]
static class ModdingScreenPatch
{
    [HarmonyPriority(Priority.First)]
    static void Postfix(NModdingScreen __instance)
    {
        try
        {
            var size = new Vector2(220, 70);
            var button = ModEntry.AddButton(__instance, "SPIRE_MOD_MANAGER.RELAUNCH_BUTTON", size, ModEntry.Relaunch);
            button.Name = "SpireModManagerRelaunchButton";

            // Screen-space slot: right edge matches the mod info panel (x 1727), centred on the pending-changes row (y 1012-1056).
            button.Position = new Vector2(1727 - size.X, 1034 - size.Y / 2);
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.ModId}] Could not add the relaunch button: {e}");
        }
    }
}
