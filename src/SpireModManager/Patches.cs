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
using MegaCrit.Sts2.Core.Nodes.Screens.Settings;
using MegaCrit.Sts2.Core.Saves;

namespace SpireModManager;

// Each patch body runs in try/catch: a throw would also skip other mods' postfixes on the same method, or the game's
// own code after a prefix. Priority.First and HarmonyBefore run ours ahead of Better Mod Menu's, which can throw.
// Each class is applied on its own (ModEntry.Init): one a game update breaks leaves the rest working.

// Main menu: loads our data, adds the "Mod Profile" button (MenuButton), then shows any startup notices.
[HarmonyPatch(typeof(NMainMenu), nameof(NMainMenu._Ready))]
static class MainMenuPatch
{
    [HarmonyPriority(Priority.First)]
    static void Postfix(NMainMenu __instance)
    {
        try
        {
            // Loads (on first run, imports) our data now, while the menu is idle.
            _ = Store.Data;
            LoadOrder.RememberLoadedOrder(); // first main menu: the game's startup sorting is done
            Profiles.Prepare(); // one-time conversions belong to the baseline, not the undo history
            History.Start();
            Bmm.ForgetDismissedWarning(); // the dismissed Better Mod Menu banner, if the two changed outside our page
            WorkshopInfo.Start(); // background Steam query; does nothing without Steam
            ModEntry.MarkDevRun(__instance.GetTree());

            MenuButton.Add(__instance);
            // After the game's own popups: the notice about an unreadable saved file, then the one about a last
            // launch that stopped before the main menu.
            var menu = __instance;
            menu.GetTree().CreateTimer(1.5).Timeout += () =>
            {
                if (!GodotObject.IsInstanceValid(menu) || !menu.IsVisibleInTree())
                    return;
                Store.ShowNotice();
                LaunchGuard.ShowNotice(menu);
            };

#if DEVTOOLS
            SelfTest.MaybeStart(__instance);
#endif
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.ModId}] Could not add the main menu button: {e}");
        }
    }
}

// Clears the launch record (LaunchGuard) once the main menu is reached. A prefix, so a throw in the menu's own _Ready
// can't skip it.
[HarmonyPatch(typeof(NMainMenu), nameof(NMainMenu._Ready))]
static class MenuReachedPatch
{
    [HarmonyPriority(Priority.First)]
    static void Prefix()
    {
        try
        {
            LaunchGuard.MenuReached();
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.ModId}] Could not clear the launch record: {e}");
        }
    }
}

// Modding page: our relaunch button, profiles, search, categories and undo bar.
[HarmonyPatch(typeof(NModdingScreen), nameof(NModdingScreen._Ready))]
static class ModdingScreenPatch
{
    [HarmonyPriority(Priority.First)]
    static void Postfix(NModdingScreen __instance)
    {
        // A patch the page needs didn't apply: the game's page, with one line saying so.
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

            // Right edge on the info panel's (x 1727), centred on the pending-changes row (y 1012-1056).
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
            // Refills the info panel when Workshop details arrive.
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
            // The notice about an unreadable saved file, once, when the page shows (it's added hidden and reused).
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

        // Better Mod Menu's _Ready postfix runs after ours and rebuilds the page: a deferred call puts ours back
        // (or keeps its page in Vanilla View).
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

// After each tickbox change: the active profile, undo history, rows and info panel follow, and a changed load order
// (or a mod installed this session) shows the pending-changes line too; the game only checks its loaded mods.
[HarmonyPatch(typeof(NModdingScreen), nameof(NModdingScreen.OnModEnabledOrDisabled))]
static class ModToggledPatch
{
    [HarmonyBefore(Bmm.HarmonyId)] // its postfix writes its profile file, which may throw
    static void Postfix(NModdingScreen __instance)
    {
        try
        {
            // Not during the screen's setup: it calls this once per row as it opens.
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

// A mod subscribed while the page is open: once the game adds its row, a redraw puts it in its category (keep: not
// the player's change, so rows kept under the Show: choice stay).
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
                        CategoryList.Redraw(__instance.GetNode<Control>("%ModsScrollContainer/Mask/Content"), keep: true);
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

// Info panel: our details and fields, after the game (and Better Mod Menu) fill it.
[HarmonyPatch(typeof(NModInfoContainer), nameof(NModInfoContainer.Fill))]
static class ModInfoFillPatch
{
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

// Hides our info panel buttons when no mod is shown. Only where the game has Clear; on the stable branch, Compat.Clear
// does it.
[HarmonyPatch(typeof(NModInfoContainer), "Clear")]
static class ModInfoClearPatch
{
    static bool Prepare() => Compat.InfoClear != null;

    static void Postfix(NModInfoContainer __instance)
    {
        // Also runs as the page closes: a throw there could leave the main menu half restored.
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

// Ctrl shortcuts on our Modding page: Z undo, Y or Shift+Z redo, F search, S save profile, A pick all shown. Unhandled
// keys only, so a focused text box keeps its own.
[HarmonyPatch(typeof(NInputManager), "_UnhandledKeyInput")]
static class ShortcutsPatch
{
    // Escape closes our open menu or dialog, or lets go of picked mods, instead of leaving the page.
    static bool Prefix(InputEvent inputEvent)
    {
        try
        {
            if (inputEvent is not InputEventKey { Keycode: Key.Escape, Pressed: true, Echo: false } || !(MenuPanel.Close() || RelaunchCheck.Cancel() || ProfileShare.Close() || MenuButton.CancelMove() || LetGo()))
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
                || Store.Data.VanillaView || RelaunchCheck.IsOpen || ProfileShare.IsOpen)
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

// The game's "mod not loaded" popup for a mod installed while it runs: ours ("New Mod Added") instead, at most one
// every 30 seconds.
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

// Adds a saved entry for a mod installed this session before its tickbox toggles: the game only updates existing
// entries, so unticking one did nothing.
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

// Keeps a ticked or unticked row in the list under the Show: choice (ListFilter.Keep), so it doesn't vanish under the
// mouse. A prefix, so the page bar's redraw counts it.
[HarmonyPatch(typeof(NModMenuRow), "OnTickboxToggled")]
static class TickKeepPatch
{
    static void Prefix(NModMenuRow __instance)
    {
        try
        {
            ListFilter.Keep(__instance.Mod?.manifest?.id);
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.ModId}] Could not keep a ticked row in the list: {e}");
        }
    }
}

// Our row highlight (RowTitles.Look) whenever the game changes a row's focus or selection.
[HarmonyPatch(typeof(NModMenuRow))]
static class RowLookPatch
{
    [HarmonyPostfix, HarmonyPatch("OnFocus")]
    static void Focused(NModMenuRow __instance) => Apply(__instance, focused: true);

    [HarmonyPostfix, HarmonyPatch("OnUnfocus")]
    static void Unfocused(NModMenuRow __instance) => Apply(__instance, focused: false);

    [HarmonyPostfix, HarmonyPatch(nameof(NModMenuRow.SetSelected))]
    static void Selected(NModMenuRow __instance) => Apply(__instance, focused: null);

    // focused null: read from the row, inside the try (it's the game's private state).
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

// Adds the drop-in translations (Translations) to a language's tables as the game loads them. Not the English fallback
// or metrics tables (allowOverride false); Apply also skips English.
[HarmonyPatch(typeof(LocManager), "LoadTablesFromPath")]
static class TranslationsPatch
{
    static void Postfix(string language, bool allowOverride, (Dictionary<string, LocTable>, bool, List<LocValidationError>) __result)
    {
        try
        {
            if (allowOverride)
                Translations.Apply(language, __result.Item1);
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.ModId}] Could not add the drop-in translation for {language}: {e}");
        }
    }
}

// A row's private selected and focused state, looked up on first use: if a game update renames one, it reads as false
// (logged once) and rows keep the game's look.
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

    // Both found on this game version.
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
