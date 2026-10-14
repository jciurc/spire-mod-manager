using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;
using MegaCrit.Sts2.Core.Saves;

namespace SpireModManager;

// Every patch body runs inside try/catch: postfixes on one method share a call chain, so an exception here would
// also skip other mods' postfixes, and one in a prefix would skip the game's own code (ours return true then, so
// it still runs). Priority.First and HarmonyBefore keep ours from being skipped by theirs (Better Mod Menu 1.1.1
// throws in its NModdingScreen._Ready and OnRowSelected postfixes on the 0.111 beta). Better Mod Menu can run
// alongside: see Bmm.
// Each class is applied on its own (ModEntry.Init), so a game update that renames what one hooks leaves the others
// working; if it's one our page needs (ModEntry.PageNeeds), the Modding page stays the game's, saying so.

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
            Bmm.ForgetDismissedWarning(); // a dismissed managers banner, after a change our page never saw
            WorkshopInfo.Start(); // background Steam query; does nothing without Steam
            ModEntry.MarkDevRun(__instance.GetTree());

            MenuButton.Add(__instance);
            // After a launch that couldn't read our saved file: the notice here too, so it's seen even without a
            // visit to the Modding page (after the game's own startup popups; with one still open, it waits).
            var menu = __instance;
            menu.GetTree().CreateTimer(1.5).Timeout += () =>
            {
                if (GodotObject.IsInstanceValid(menu) && menu.IsVisibleInTree())
                    Store.ShowNotice();
            };

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
        // A patch the page needs didn't apply (a game update renamed what it hooks): the game's page as it is, with
        // one line saying so.
        try
        {
            if (!ModEntry.OurPage)
            {
                ModEntry.ShowNeedsUpdate(__instance);
                return;
            }
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.ModId}] Could not show the needs-update line: {e}");
            return;
        }

        try
        {
            var size = new Vector2(220, 70);
            var button = ModEntry.AddButton(__instance, "SPIRE_MOD_MANAGER.RELAUNCH_BUTTON", size, () => RelaunchCheck.Relaunch(__instance));
            button.Name = "SpireModManagerRelaunchButton";

            // Screen-space slot: right edge matches the mod info panel (x 1727), centred on the pending-changes row (y 1012-1056).
            button.Position = new Vector2(1727 - size.X, 1034 - size.Y / 2);
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.ModId}] Could not add the relaunch button: {e}");
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

        try
        {
            // After a launch that couldn't read our saved file: the notice, once, when the page shows. The game adds
            // this screen hidden, shows it as it's pushed and reuses it for later visits: checked at the end of this
            // frame and each time it shows.
            var screen = __instance;
            void Notice()
            {
                if (GodotObject.IsInstanceValid(screen) && screen.IsVisibleInTree())
                    Store.ShowNotice();
            }
            screen.VisibilityChanged += Notice;
            Callable.From(Notice).CallDeferred();
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.ModId}] Could not set up the notice about our saved file: {e}");
        }

        // Better Mod Menu's _Ready postfix runs after this one and rebuilds the page its way (rows restyled, grouped,
        // the list pushed down). Once that's done (deferred calls run in order, and its own deferred step comes after
        // this): ours again, or its page in Vanilla View.
        try
        {
            if (Bmm.Present)
            {
                var screen = __instance;
                Callable.From(() =>
                {
                    try
                    {
                        if (!GodotObject.IsInstanceValid(screen))
                            return;
                        CategoryList.Apply(screen.GetNode<Control>("%ModsScrollContainer/Mask/Content"));
                        Layout.Apply(screen);
                    }
                    catch (Exception e)
                    {
                        Log.Error($"[{ModEntry.ModId}] Could not lay out the screen after Better Mod Menu: {e}");
                    }
                }).CallDeferred();
            }
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.ModId}] Could not wait for Better Mod Menu's layout: {e}");
        }
    }
}

// Every tickbox change (and the game's own pending check) passes through here: the active profile follows
// it, and a changed load order also counts as pending (the game only checks on/off).
[HarmonyPatch(typeof(NModdingScreen), nameof(NModdingScreen.OnModEnabledOrDisabled))]
static class ModToggledPatch
{
    [HarmonyBefore(Bmm.HarmonyId)] // its postfix writes its profile file, which may throw
    static void Postfix(NModdingScreen __instance)
    {
        try
        {
            // Not while the screen is still adding its rows (it calls this once per row as it opens; ours are set
            // up after).
            if (HistoryBar.Screen != __instance)
                return;
            Profiles.Track();
            History.Record(); // a tickbox change (Track only saves when the active profile changed)
            var content = __instance.GetNode<Control>("%ModsScrollContainer/Mask/Content");
            RowTitles.MarkChanged(content.GetChildren().OfType<NModMenuRow>(), Store.Data.VanillaView);
            CategoryList.RefreshStrips(content);
            ModDetails.Refresh(__instance); // its problems ("Needs X, which is off") follow too
            Layout.UpdateDot(__instance);
            if (LoadOrder.OrderChanged || Installed.Mods.Any(Installed.On)) // the game only checks mods it loaded
                __instance.GetNode<Control>("%PendingChangesLabel").Visible = true;
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.ModId}] Could not update the active profile: {e}");
        }
    }
}

// A mod subscribed while the page is open: the game adds its row at the bottom, in its own style. Once it's
// in, a redraw puts it in its category, in our style.
[HarmonyPatch(typeof(NModdingScreen), "OnNewModDetected")]
static class NewModPatch
{
    static void Postfix(NModdingScreen __instance)
    {
        try
        {
            if (HistoryBar.Screen != __instance)
                return; // the screen's own setup (our parts come after)
            __instance.GetTree().CreateTimer(0.05).Timeout += () =>
            {
                try
                {
                    if (GodotObject.IsInstanceValid(__instance))
                        CategoryList.Redraw(__instance.GetNode<Control>("%ModsScrollContainer/Mask/Content"));
                }
                catch (Exception e)
                {
                    Log.Error($"[{ModEntry.ModId}] Could not place a newly installed mod: {e}");
                }
            };
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.ModId}] Could not wait to place a newly installed mod: {e}");
        }
    }
}

// Info panel: our details and the player's fields, after the game fills it (and after Better Mod Menu: on our
// views our title replaces its styled one, and our layout its description anchor). Before it, other mods' parts we
// hid for lack of room are given back for their mods to update (Layout.GiveBack).
[HarmonyPatch(typeof(NModInfoContainer), nameof(NModInfoContainer.Fill))]
static class ModInfoFillPatch
{
    [HarmonyPriority(Priority.First)] // before other mods' prefixes too
    static void Prefix(NModInfoContainer __instance)
    {
        try
        {
            Layout.GiveBack(__instance);
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.ModId}] Could not give back other mods' parts in the info panel: {e}");
        }
    }

    [HarmonyAfter(Bmm.HarmonyId)]
    static void Postfix(NModInfoContainer __instance, Mod mod)
    {
        try
        {
            if (ModEntry.OurPage) // the game's page otherwise (ModdingScreenPatch)
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
        // It also runs as the page closes (inside NSubmenuStack.Pop): an exception there could leave the main menu
        // half restored, our button hidden.
        try
        {
            ModDetails.HideButtons(__instance);
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.ModId}] Could not hide the info panel's buttons: {e}");
        }
    }
}

// Keyboard shortcuts on our Modding screen: Ctrl+Z undo, Ctrl+Y or Ctrl+Shift+Z redo, Ctrl+F search, Ctrl+S
// saves the profile, Ctrl+A picks every mod shown.
// Unhandled keys only, so a focused text box keeps its own Ctrl+Z.
[HarmonyPatch(typeof(NInputManager), "_UnhandledKeyInput")]
static class ShortcutsPatch
{
    // Escape closes an open menu or dialog of ours, or lets go of picked mods, and nothing else happens (the game
    // would leave the page). This runs on every Escape anywhere in the game: on an exception, the game's runs.
    static bool Prefix(InputEvent inputEvent)
    {
        try
        {
            if (inputEvent is not InputEventKey { Keycode: Key.Escape, Pressed: true, Echo: false } || !(MenuPanel.Close() || RelaunchCheck.Close() || MenuButton.CancelMove() || LetGo()))
                return true;
            ((SceneTree)Engine.GetMainLoop()).Root.SetInputAsHandled();
            return false;
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.ModId}] Escape failed, so the game's own Escape runs: {e}");
            return true;
        }
    }

    static bool LetGo() => HistoryBar.Screen is { } screen && GodotObject.IsInstanceValid(screen) && screen.IsVisibleInTree()
        && Selection.Clear(screen.GetNode<Control>("%ModsScrollContainer/Mask/Content"));

    static void Postfix(InputEvent inputEvent)
    {
        try
        {
            if (inputEvent is not InputEventKey { Pressed: true, Echo: false, CtrlPressed: true } key
                || HistoryBar.Screen is not { } screen || !GodotObject.IsInstanceValid(screen) || !screen.IsVisibleInTree()
                || Store.Data.VanillaView || RelaunchCheck.IsOpen)
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
                case Key.S:
                    if (Profiles.Active is { Edited: not null })
                        Profiles.Save();
                    break;
                case Key.A:
                    var content = screen.GetNode<Control>("%ModsScrollContainer/Mask/Content");
                    Selection.Add(content, content.GetChildren().OfType<NModMenuRow>());
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
    [HarmonyBefore(Bmm.HarmonyId)] // Better Mod Menu 1.1.1 throws in its postfix here
    static void Postfix(NModMenuRow row)
    {
        try
        {
            CategoryList.Selected = row.Mod;
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.ModId}] Could not remember the selected mod: {e}");
        }
    }
}

// The game's "mod not loaded" popup when a mod is installed while it runs: our wording ("New Mod Added") and
// mark, and at most one every 30 seconds (subscribing to several mods at once used to stack them up).
[HarmonyPatch(typeof(NGame), "OnNewModDetected")]
static class NewModPopupPatch
{
    const ulong EveryMs = 30_000;
    static ulong _lastShown;

    static bool Prefix()
    {
        try
        {
            var now = Time.GetTicksMsec();
            if ((_lastShown != 0 && now - _lastShown < EveryMs) || NModalContainer.Instance is not { } modals
                || modals.GetChildren().OfType<NErrorPopup>().Any())
                return false;
            if (NErrorPopup.Create(new LocString("settings_ui", "SPIRE_MOD_MANAGER.NEW_MOD_TITLE"),
                    new LocString("settings_ui", "SPIRE_MOD_MANAGER.NEW_MOD_BODY"), null, showReportBugButton: false) is { } popup)
            {
                Menus.Mark(popup);
                modals.Add(popup);
                _lastShown = now;
            }
            return false;
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.ModId}] Could not show the new mod popup, showing the game's: {e}");
            return true;
        }
    }
}

// The game's tickbox only updates a mod's existing entry in the saved list, and a mod installed while the
// game runs has none: unticking it changed nothing, and it would still load after a relaunch. Add the entry
// first (on, the game's default), so the tickbox has something to change.
[HarmonyPatch(typeof(NModMenuRow), "OnTickboxToggled")]
static class NewModTogglePatch
{
    static void Prefix(NModMenuRow __instance)
    {
        try
        {
            if (__instance.Mod is not { manifest.id: { } id } mod)
                return;
            var settings = SaveManager.Instance.SettingsSave.ModSettings ??= new ModSettings();
            if (!settings.ModList.Any(e => e.Id == id && e.Source == mod.modSource))
                settings.ModList.Add(new SettingsSaveMod(mod) { IsEnabled = true });
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.ModId}] Could not add a saved entry for a new mod: {e}");
        }
    }
}

// The row's highlight follows our look (RowTitles.Look) whenever the game changes its state: under the mouse or
// keyboard (focus), or the mod shown in the info panel (selected).
[HarmonyPatch(typeof(NModMenuRow))]
static class RowLookPatch
{
    [HarmonyPostfix, HarmonyPatch("OnFocus")]
    static void Focused(NModMenuRow __instance) => Apply(__instance, focused: true);

    [HarmonyPostfix, HarmonyPatch("OnUnfocus")]
    static void Unfocused(NModMenuRow __instance) => Apply(__instance, focused: false);

    [HarmonyPostfix, HarmonyPatch(nameof(NModMenuRow.SetSelected))]
    static void Selected(NModMenuRow __instance) => Apply(__instance, focused: null);

    // focused: null for "as it is" (read from the row, inside the try: it's the game's private state).
    static void Apply(NModMenuRow row, bool? focused)
    {
        try
        {
            if (ModEntry.OurPage) // the game's page otherwise (ModdingScreenPatch)
                RowTitles.Look(row, RowState.Selected(row), focused ?? RowState.Focused(row));
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.ModId}] Could not update a row's highlight: {e}");
        }
    }
}

// A row's own state, which the game keeps to itself. Looked up on first use rather than in static initializers:
// if a game update renames one, it reads as false (logged once) instead of throwing on every redraw.
static class RowState
{
    static AccessTools.FieldRef<NModMenuRow, bool>? _selected;
    static Func<NClickableControl, bool>? _focused;
    static bool _lookedUp;

    public static bool Selected(NModMenuRow row)
    {
        LookUp();
        return _selected is { } read && read(row);
    }

    public static bool Focused(NModMenuRow row)
    {
        LookUp();
        return _focused is { } read && read(row);
    }

    // Self-test: both found on this game version.
    public static bool Found
    {
        get
        {
            LookUp();
            return _selected != null && _focused != null;
        }
    }

    static void LookUp()
    {
        if (_lookedUp)
            return;
        _lookedUp = true;
        _selected = Find("NModMenuRow._isSelected", () => AccessTools.FieldRefAccess<NModMenuRow, bool>("_isSelected"));
        _focused = Find("NClickableControl.IsFocused", () => AccessTools.PropertyGetter(typeof(NClickableControl), "IsFocused") is { } getter
            ? AccessTools.MethodDelegate<Func<NClickableControl, bool>>(getter) : null);
    }

    // Null if it's gone or no longer fits (a field of another type).
    static T? Find<T>(string name, Func<T?> find) where T : class
    {
        try
        {
            if (find() is { } found)
                return found;
            Log.Warn($"[{ModEntry.ModId}] {name} not found; rows read as not selected or focused");
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.ModId}] {name} not found; rows read as not selected or focused: {e.Message}");
        }
        return null;
    }
}
