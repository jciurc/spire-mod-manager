using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
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
            LoadOrder.RememberLoadedOrder(); // first main menu: the game's startup sorting is done
            Profiles.Prepare(); // one-time conversions belong to the baseline, not the undo history
            History.Start();
            WorkshopInfo.Start(); // background Steam query; does nothing without Steam
            ModEntry.MarkDevRun(__instance.GetTree());

            MenuButton.Add(__instance);

            SelfTest.MaybeStart(__instance);
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

        try
        {
            HistoryBar.Attach(__instance);
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.ModId}] Could not set up undo: {e}");
        }

        try
        {
            Layout.Apply(__instance); // last: it places everything above
            // Workshop details arriving after the page opened: the info panel shows them (dates, tags).
            var screen = __instance;
            void Refill() => Callable.From(() => ModDetails.Refresh(screen)).CallDeferred();
            WorkshopInfo.Loaded += Refill;
            screen.TreeExiting += () => WorkshopInfo.Loaded -= Refill;
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.ModId}] Could not lay out the screen: {e}");
        }
    }
}

// Every tickbox change (and the game's own pending check) passes through here: the active profile follows
// it, and a changed load order also counts as pending (the game only checks on/off).
[HarmonyPatch(typeof(NModdingScreen), nameof(NModdingScreen.OnModEnabledOrDisabled))]
static class ModToggledPatch
{
    static void Postfix(NModdingScreen __instance)
    {
        if (ModEntry.BetterModMenuLoaded) // paused while it runs the screen (see ModdingScreenPatch)
            return;
        try
        {
            Profiles.Track();
            History.Record(); // a tickbox change (Track only saves when the active profile changed)
            var content = __instance.GetNode<Control>("%ModsScrollContainer/Mask/Content");
            RowTitles.MarkChanged(content.GetChildren().OfType<NModMenuRow>(), Store.Data.VanillaView);
            CategoryList.RefreshStrips(content);
            ModDetails.Refresh(__instance); // its problems ("Needs X, which is off") follow too
            Layout.UpdateDot(__instance);
            if (LoadOrder.OrderChanged)
                __instance.GetNode<Control>("%PendingChangesLabel").Visible = true;
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
        ModDetails.HideButtons(__instance);
    }
}

// Keyboard shortcuts on our Modding screen: Ctrl+Z undo, Ctrl+Y or Ctrl+Shift+Z redo, Ctrl+F search.
// Unhandled keys only, so a focused text box keeps its own Ctrl+Z.
[HarmonyPatch(typeof(NInputManager), "_UnhandledKeyInput")]
static class ShortcutsPatch
{
    // Escape closes an open menu of ours, and nothing else happens (the game would leave the page).
    static bool Prefix(InputEvent inputEvent)
    {
        if (inputEvent is not InputEventKey { Keycode: Key.Escape, Pressed: true, Echo: false } || !MenuPanel.Close())
            return true;
        ((SceneTree)Engine.GetMainLoop()).Root.SetInputAsHandled();
        return false;
    }

    static void Postfix(InputEvent inputEvent)
    {
        try
        {
            if (inputEvent is not InputEventKey { Pressed: true, Echo: false, CtrlPressed: true } key
                || HistoryBar.Screen is not { } screen || !GodotObject.IsInstanceValid(screen) || !screen.IsVisibleInTree()
                || Store.Data.VanillaView)
                return;
            switch (key.Keycode)
            {
                case Key.Z when key.ShiftPressed:
                case Key.Y:
                    History.Redo();
                    break;
                case Key.Z:
                    History.Undo();
                    break;
                case Key.F:
                    screen.GetNodeOrNull<Control>("ModsBorder/SpireModManagerSearch")?.GrabFocus();
                    break;
                default:
                    return;
            }
            screen.GetViewport().SetInputAsHandled();
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.ModId}] Shortcut failed: {e}");
        }
    }
}

// Remembers the selected mod so switching views can keep it selected.
[HarmonyPatch(typeof(NModdingScreen), nameof(NModdingScreen.OnRowSelected))]
static class RowSelectedPatch
{
    static void Postfix(NModMenuRow row) => CategoryList.Selected = row.Mod;
}
