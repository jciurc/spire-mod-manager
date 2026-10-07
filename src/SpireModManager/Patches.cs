using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;

namespace SpireModManager;

// Every patch body is wrapped in try/catch: postfixes on one method share a call chain, so an exception
// here would also skip other mods' postfixes. Priority.First keeps ours from being skipped by theirs
// (Better Mod Menu 1.1.1 throws in its NModdingScreen._Ready postfix on the 0.111 beta).

// "Spire Mod Manager" button in the main menu's lower-left corner (the lower right holds the vanilla
// "MODDED (n)" label and its hover list), with a dot when a relaunch is pending.
[HarmonyPatch(typeof(NMainMenu), nameof(NMainMenu._Ready))]
static class MainMenuPatch
{
    [HarmonyPriority(Priority.First)]
    static void Postfix(NMainMenu __instance)
    {
        try
        {
            // Load (and on first run import) saved data while the menu is idle, not on first screen open.
            _ = Store.Data;
            ModEntry.MarkDevRun(__instance.GetTree());

            var menu = __instance;
            var button = ModEntry.AddButton(menu, "SPIRE_MOD_MANAGER.MAIN_MENU_BUTTON", new Vector2(260, 70),
                () => menu.SubmenuStack.PushSubmenuType<NModdingScreen>());
            button.Name = "SpireModManagerMenuButton";

            // Mirrors the profile button's margins in the top-left corner (18, 16).
            button.SetAnchorsPreset(Control.LayoutPreset.BottomLeft);
            button.OffsetLeft = 18;
            button.OffsetBottom = -16;
            button.OffsetRight = button.OffsetLeft + 260;
            button.OffsetTop = button.OffsetBottom - 70;

            var dot = button.GetNode<Control>("Dot");
            void Refresh()
            {
                button.Visible = !menu.SubmenuStack.SubmenusOpen;
                dot.Visible = ModEntry.HasPendingChanges();
            }
            Refresh();
            menu.SubmenuStack.Connect(NSubmenuStack.SignalName.StackModified, Callable.From(Refresh));

            SelfTest.MaybeStart(menu);
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.ModId}] Could not add the main menu button: {e}");
        }
    }
}

// Modding screen: "Relaunch Game" at the right end of the pending-changes row, profiles and categories.
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

        // Better Mod Menu rebuilds this same screen; two managers on one list fight. Step aside and say so.
        if (ModEntry.BetterModMenuLoaded)
        {
            ModEntry.AddBetterModMenuNotice(__instance);
            return;
        }

        try
        {
            ProfileBar.Attach(__instance);
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.ModId}] Could not set up profiles: {e}");
        }

        try
        {
            Search.Attach(__instance); // below the profile bar: each pushes the list down
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.ModId}] Could not set up search: {e}");
        }

        try
        {
            CategoryList.Attach(__instance);
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.ModId}] Could not set up categories: {e}");
        }
    }
}

// Every tickbox change (and the game's own pending check) passes through here: the active profile follows it.
[HarmonyPatch(typeof(NModdingScreen), nameof(NModdingScreen.OnModEnabledOrDisabled))]
static class ModToggledPatch
{
    static void Postfix()
    {
        try
        {
            Profiles.Track();
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.ModId}] Could not update the active profile: {e}");
        }
    }
}

// Info panel: alias, the player's notes and the Edit Notes / Workshop Page buttons, after the game fills it.
[HarmonyPatch(typeof(NModInfoContainer), nameof(NModInfoContainer.Fill))]
static class ModInfoFillPatch
{
    static void Postfix(NModInfoContainer __instance, Mod mod)
    {
        if (ModEntry.BetterModMenuLoaded)
            return;
        try
        {
            ModDetails.Fill(__instance, mod);
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.ModId}] Could not add notes to the info panel: {e}");
        }
    }
}

// No mod shown: hide our panel buttons too.
[HarmonyPatch(typeof(NModInfoContainer), nameof(NModInfoContainer.Clear))]
static class ModInfoClearPatch
{
    static void Postfix(NModInfoContainer __instance)
    {
        foreach (var name in new[] { "EditNotesButton", "WorkshopPageButton" })
            if (__instance.GetNodeOrNull<Control>(name) is { } button)
                button.Visible = false;
    }
}
