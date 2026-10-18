using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Audio;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Debug;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;
using MegaCrit.Sts2.Core.Nodes.Screens.Settings;
using MegaCrit.Sts2.Core.Saves;

namespace SpireModManager;

// Each patch body runs in try/catch: a throw would also skip other mods' postfixes on the same method, or the game's
// own code after a prefix. Priority.First and HarmonyBefore run ours ahead of Better Mod Menu's, which can throw.
// Each class is applied on its own (ModEntry.Init): one a game update breaks leaves the rest working.

// Main menu: loads our data, adds our button (MenuButton), then shows any startup notices.
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
            Copies.Start(); // mods installed more than once: a data entry per copy
            LoadOrder.RememberLoadedOrder(); // first main menu: the game's startup sorting is done
            Profiles.Prepare(); // one-time conversions belong to the baseline, not the undo history
            History.Start();
            Bmm.ForgetDismissedWarning(); // the dismissed Better Mod Menu banner, if the two changed outside our page
            WorkshopInfo.Start(); // background Steam query; does nothing without Steam
            Prewarm.Start(__instance); // the Modding page's text, a little each frame from a second on
#if DEVTOOLS
            ModEntry.MarkDevRun(__instance.GetTree());
#endif

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

// Clears the launch record (LaunchGuard) once the main menu is reached, then settles the copies of mods installed
// twice (SameIdLoad.Settle: the game's rebuilt list repaired, our entries first, the self-check), before our data loads
// and the undo history starts. A prefix, so a throw in the menu's own _Ready can't skip it.
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
        try
        {
            SameIdLoad.Settle();
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.ModId}] Could not settle the mods installed twice after launch (the saved list stays as the game wrote it): {e}");
        }
    }
}

// The main menu's MODDED warning (and the line in a run) reads every mod's errors: the Steam branch errors of mods that
// are off, and the messages of copies we set aside as duplicates, go first, again, should a game update ever check
// branches after our Init (BranchErrors; in the game's order today there's nothing left to drop).
[HarmonyPatch(typeof(NDebugInfoLabelManager), "UpdateText")]
static class BranchErrorsLatePatch
{
    static void Prefix()
    {
        try
        {
            BranchErrors.DropFromOffMods(late: true);
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.ModId}] Could not drop the Steam branch error of mods that are off before the main menu's warning: {e}");
        }
    }
}

// The game couldn't start (a mod failed in its startup): instead of its dialog with only Quit, ours offers the mods of
// the last launch that worked, when this launch changed any (LaunchGuard.AskAfterCrash). Nothing to offer, or ours
// failing: the game's own dialog. After other mods' prefixes (Mod Doctor's writes its findings first).
[HarmonyPatch(typeof(NGame), "GameStartupError")]
static class StartupErrorPatch
{
    static bool _games; // the game's own dialog from here on

    [HarmonyPriority(Priority.Low)]
    static bool Prefix(NGame __instance, ref Task __result)
    {
        try
        {
            if (_games || LaunchGuard.CrashOffer() is not { } offer)
                return true;
            __result = LaunchGuard.AskAfterCrash(__instance, offer);
            return false;
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.ModId}] Could not look at the last working launch after the failed start: {e}");
            return true;
        }
    }

    // The game's own dialog after all (ours couldn't show): its method again, which this prefix now lets through.
    public static Task GamesDialog(NGame game)
    {
        _games = true;
        return HarmonyLib.AccessTools.Method(typeof(NGame), "GameStartupError")?.Invoke(game, null) as Task ?? Task.CompletedTask;
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

        // The stable branch's scene holds a placeholder row (no mod) that the game's _Ready queues to free at the end of
        // the frame, after our first redraw would have counted it. Out of the list now; still freed then. (The beta's
        // scene has none. A row without a mod that isn't on its way out stays: not ours to remove.)
        try
        {
            var content = __instance.GetNode<Control>("%ModsScrollContainer/Mask/Content");
            foreach (var placeholder in content.GetChildren().OfType<NModMenuRow>().Where(r => r.Mod == null && r.IsQueuedForDeletion()).ToList())
                content.RemoveChild(placeholder);
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.ModId}] Could not take out the game's placeholder row: {e}");
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
            // Refills the info panel when Workshop details arrive, and redraws our views' list: chips and searches by
            // Workshop tags and branches (Cosmetic, Stable, Beta) follow them.
            var screen = __instance;
            void Refill() => Callable.From(() =>
            {
                if (!GodotObject.IsInstanceValid(screen))
                    return;
                ModDetails.Refresh(screen);
                if (!Store.Data.VanillaView)
                    CategoryList.Apply(screen.GetNode<Control>("%ModsScrollContainer/Mask/Content"));
            }).CallDeferred();
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
            if (Installed.JustArrived)
                History.KeepCurrent(); // the game announcing a mod installed just now: no step (Profiles.TakeInInstalled)
            else
                History.Record(); // a tickbox change, one step (Track only saves when the active profile changed)
            // Every row's box as its view reads it: in Vanilla View the game's tickbox sets every entry of the mod's id
            // and source, so another copy (a second Workshop item) switched too; on our views a switch to another copy
            // (CopyTickPatch) turned the old one off.
            Profiles.RefreshTickboxes(__instance);
            var content = __instance.GetNode<Control>("%ModsScrollContainer/Mask/Content");
            RowTitles.MarkChanged(content.GetChildren().OfType<NModMenuRow>(), Store.Data.VanillaView);
            CategoryList.RefreshStrips(content);
            ModDetails.Refresh(__instance); // its problems ("Needs X, which is off") follow too
            Layout.UpdateDot(__instance); // the line too: the game only checks mods it loaded
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.ModId}] Could not update the active profile: {e}");
        }
    }
}

// The game checks every mod for a pending change after adding each row in its page's _Ready (the saved list walked per
// mod, per row: ~25-90 ms with 250 mods), then hides the line at the end of _Ready anyway. The checks are skipped while
// the game's _Ready runs; the patches on the check (ours, Better Mod Menu's) still run each time, as before.
[HarmonyPatch(typeof(NModdingScreen))]
static class SetupCheckPatch
{
    static bool _building;

    [HarmonyPrefix, HarmonyPatch(nameof(NModdingScreen._Ready))]
    static void Building() => _building = true;

    // Over before any other _Ready postfix runs (theirs and ours may call the check); the finalizer, if _Ready throws.
    [HarmonyPostfix, HarmonyPatch(nameof(NModdingScreen._Ready)), HarmonyPriority(Priority.First + 1)]
    static void Ready() => _building = false;

    [HarmonyFinalizer, HarmonyPatch(nameof(NModdingScreen._Ready))]
    static void Finished() => _building = false;

    [HarmonyPrefix, HarmonyPatch(nameof(NModdingScreen.OnModEnabledOrDisabled))]
    static bool Check() => !_building;
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

// Ctrl shortcuts on our Modding page: Z undo, Y or Shift+Z redo, F search, S save profile, A pick all shown, D let go
// of the picked mods. F8 opens and closes the page. Unhandled keys only, so a focused text box keeps its own.
[HarmonyPatch(typeof(NInputManager), "_UnhandledKeyInput")]
static class ShortcutsPatch
{
    // The key that opens and closes the page: the game binds nothing to it (its debug keys skip it), unless the player
    // moves one of its keys there. Not changeable yet.
    public const Key ManagerKey = Key.F8;

    // Escape closes our open menu or dialog, ends the main menu button's move mode, or lets go of picked mods, in
    // place of the game's Escape (which would leave the page). Ctrl+D lets go of picked mods, in place of the game's D.
    static bool Prefix(InputEvent inputEvent)
    {
        try
        {
            var handled = inputEvent switch
            {
                InputEventKey { Keycode: Key.Escape, Pressed: true, Echo: false } => CloseOurs() || LetGo(),
                InputEventKey { Keycode: ManagerKey, Pressed: true, Echo: false, CtrlPressed: false, ShiftPressed: false, AltPressed: false, MetaPressed: false } =>
                    OpenOrClose(),
                InputEventKey { Keycode: Key.D, Pressed: true, Echo: false, CtrlPressed: true, ShiftPressed: false, AltPressed: false } => Deselect(),
                _ => false,
            };
            if (!handled)
                return true;
            ((SceneTree)Engine.GetMainLoop()).Root.SetInputAsHandled();
            return false;
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.ModId}] A key failed, so the game's own runs: {e}");
            return true;
        }
    }

    // Our open menu or dialog, or the main menu button's move mode: true if there was one (it's closed now).
    static bool CloseOurs() => MenuPanel.Close() || RelaunchCheck.Cancel() || ProfileShare.Close() || MenuButton.CancelMove();

    // F8 on the main menu opens our page, as the main menu button does; on the Modding page it goes back, as the page's
    // Back button does, each with its sound. Anything of ours that's open closes first instead, as with Escape. Elsewhere it's left to the
    // game, which does nothing with it: on any other screen (by the game's own reckoning, where a popup counts as one),
    // during a fade, and while a text box has the keyboard (a text box doesn't take F8, so it would reach here).
    static bool OpenOrClose()
    {
        if (Typing() || NGame.Instance?.Transition is not { InTransition: false })
            return false;
        switch (ActiveScreenContext.Instance.GetCurrentScreen())
        {
            case NMainMenu menu:
                if (!CloseOurs())
                {
                    SfxCmd.Play(FmodSfx.uiClick); // the main menu button's click
                    MenuButton.Open(menu);
                }
                return true;
            case NModdingScreen screen:
                if (CloseOurs())
                    return true;
                if (screen.GetNodeOrNull<NBackButton>("BackButton") is not { IsEnabled: true } back)
                    return false;
                SfxCmd.Play(FmodSfx.backButton); // its press's sound, which ForceClick skips
                back.ForceClick();
                return true;
            default:
                return false;
        }
    }

    static bool Typing() => ((SceneTree)Engine.GetMainLoop()).Root.GuiGetFocusOwner() is LineEdit { Editable: true } or TextEdit { Editable: true };

    static bool LetGo() => HistoryBar.Screen is { } screen && GodotObject.IsInstanceValid(screen) && screen.IsVisibleInTree()
        && Selection.Clear(screen.GetNode<Control>("%ModsScrollContainer/Mask/Content"));

    // Ctrl+D on our page with mods picked: an open menu closes (the bulk menu edits the picks it opened with), then
    // they're let go of.
    static bool Deselect()
    {
        if (Store.Data.VanillaView || RelaunchCheck.IsOpen || ProfileShare.IsOpen || Selection.Mods.Count == 0
            || HistoryBar.Screen is not { } screen || !GodotObject.IsInstanceValid(screen) || !screen.IsVisibleInTree())
            return false;
        MenuPanel.Close();
        return LetGo();
    }

    static void Postfix(InputEvent inputEvent)
    {
        try
        {
            if (inputEvent is not InputEventKey { Pressed: true, Echo: false, CtrlPressed: true } key
                || HistoryBar.Screen is not { } screen || !GodotObject.IsInstanceValid(screen) || !screen.IsVisibleInTree()
                || Store.Data.VanillaView || RelaunchCheck.IsOpen || ProfileShare.IsOpen)
                return;
            // These change the picks or what they show, so an open menu (the bulk menu edits the picks it opened with)
            // closes first.
            if (key.Keycode is Key.Z or Key.Y or Key.A)
                MenuPanel.Close();
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
// every 30 seconds. A new copy of a mod installed from the same source doesn't load in place of the one there (its pick
// stays), so it gets its own words; with other new mods, both.
[HarmonyPatch(typeof(NGame), "OnNewModDetected")]
static class NewModPopupPatch
{
    const ulong EveryMs = 30_000;
    static ulong _lastShown;
    // By copy (Copies.SwitchKey): an update of a copy installed this session is a new Mod of a copy already announced.
    static readonly HashSet<string> Announced = [];

    static bool Prefix(Mod mod)
    {
        try
        {
            var now = Time.GetTicksMsec();
            if ((_lastShown != 0 && now - _lastShown < EveryMs) || NModalContainer.Instance is not { } modals
                || modals.GetChildren().OfType<NErrorPopup>().Any(p => !p.IsQueuedForDeletion())) // a closed one stays till the frame ends
                return false;
            var arrived = Installed.Mods.Append(mod).Distinct().Where(m => !Announced.Contains(Copies.SwitchKey(m))).ToList();
            var keys = arrived.Select(Copies.SwitchKey).ToHashSet();
            // A new copy is one Installed took as new (an update of either copy isn't: it keeps the new-mods text), beside a
            // copy of its id and source from before these (two copies that came together are a new mod).
            var copies = arrived.Where(m => Installed.Mods.Contains(m) && Copies.Twins(m).Any(t => !keys.Contains(Copies.SwitchKey(t)))).ToList();
            var title = new LocString("settings_ui", "SPIRE_MOD_MANAGER.NEW_MOD_TITLE");
            var copyBody = new LocString("settings_ui", "SPIRE_MOD_MANAGER.NEW_COPY_BODY");
            copyBody.Add("mods", string.Join(", ", copies.Select(m => (m.manifest?.name ?? m.manifest?.id) + Copies.Which(m))));
            var popup = copies.Count == 0 ? NErrorPopup.Create(title, new LocString("settings_ui", "SPIRE_MOD_MANAGER.NEW_MOD_BODY"), null, showReportBugButton: false)
                : copies.Count == arrived.Count ? NErrorPopup.Create(title, copyBody, null, showReportBugButton: false)
                : Both(title, copyBody);
            if (popup != null)
            {
                Menus.Mark(popup);
                modals.Add(popup);
                _lastShown = now;
                Announced.UnionWith(keys);
            }
            return false;
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.ModId}] Could not show the new mod popup, showing the game's: {e}");
            return true;
        }
    }

#if DEVTOOLS
    // Self-test: the next mod installed gets its popup, as if none was shown in the last 30 s.
    public static void TestReset() => _lastShown = 0;
#endif

    // New mods and a new copy: both texts, a blank line between, through the popup's plain-text Create, found by name
    // (where it's missing, the new mods' text alone).
    static NErrorPopup? Both(LocString title, LocString copyBody)
    {
        var body = $"{Menus.Text("SPIRE_MOD_MANAGER.NEW_MOD_BODY")}\n\n{copyBody.GetFormattedText()}";
        if (AccessTools.Method(typeof(NErrorPopup), nameof(NErrorPopup.Create), [typeof(string), typeof(string), typeof(bool)]) is { } create)
            return create.Invoke(null, [title.GetFormattedText(), body, false]) as NErrorPopup;
        return NErrorPopup.Create(title, new LocString("settings_ui", "SPIRE_MOD_MANAGER.NEW_MOD_BODY"), null, showReportBugButton: false);
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

// The tickbox of one of several picked rows on the main list sets them all to the state it took, as one undo step,
// their rows kept under a Show: choice. In place of the game's toggle for that row; other rows' tickboxes, and Load
// Order's, switch only their own mod. This mod stays on, and where two copies of a mod are picked, the clicked row's
// copy is the one turned on (Dependencies.Set). Falls back to the game's toggle on error.
[HarmonyPatch(typeof(NModMenuRow), "OnTickboxToggled")]
static class GroupTickPatch
{
    [HarmonyPriority(Priority.First)]
    static bool Prefix(NModMenuRow __instance, NTickbox tickbox)
    {
        try
        {
            if (Layout.Current != Layout.View.Categories || !Selection.Several || !Selection.Contains(__instance.Mod)
                || CategoryList.ScreenOf(__instance) is not { } screen || HistoryBar.Screen != screen)
                return true;
            ListFilter.KeepCopies(Selection.Mods.Select(m => m.manifest?.id));
            Dependencies.Set(Selection.Mods, tickbox.IsTicked, screen, winner: __instance.Mod);
            CategoryList.Redraw(screen.GetNode<Control>("%ModsScrollContainer/Mask/Content"), keep: true);
            return false;
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.ModId}] Could not switch the picked mods together; the game's tickbox switches only this mod: {e}");
            return true;
        }
    }
}

// On our views, the tickbox of a mod installed more than once switches that copy: ticked, it becomes the copy that loads
// and the id's other copies, of any source, go off; unticked, the mod goes off and keeps its pick. One undo step: the
// game's check runs as after its own toggle, and ModToggledPatch records it once. The game's own toggle would switch
// every copy of the id and source (Vanilla View still does). After GroupTickPatch (a tick on several picked rows is
// that one's: then __runOriginal is false) and still ahead of Better Mod Menu's. Falls back to the game's toggle on
// error, and in a session that can't save our data when the tick would change the pick.
[HarmonyPatch(typeof(NModMenuRow), "OnTickboxToggled")]
static class CopyTickPatch
{
    [HarmonyPriority(Priority.First - 1)]
    static bool Prefix(NModMenuRow __instance, NTickbox tickbox, bool __runOriginal)
    {
        try
        {
            if (!__runOriginal || Store.Data.VanillaView || __instance.Mod is not { manifest.id: { } id } mod || id == ModEntry.ModId
                || Copies.Others(mod).Count == 0 || CategoryList.ScreenOf(__instance) is not { } screen || HistoryBar.Screen != screen)
                return true;
            if (tickbox.IsTicked && Store.ReadOnly && Dependencies.ChangesPick(mod))
                return true;
            var wereOn = Copies.Others(mod).Where(Dependencies.IsOn).ToList();
            Dependencies.Write([(mod, tickbox.IsTicked)], winner: mod);
            Profiles.RefreshTickboxes(screen);
            screen.OnModEnabledOrDisabled();
            // The copy this switched off: its title turns (ModToggledPatch) and its row glows, as a banner's Show does.
            foreach (var row in screen.GetNode("%ModsScrollContainer/Mask/Content").GetChildren().OfType<NModMenuRow>()
                .Where(r => r.Mod is { } other && wereOn.Contains(other) && !Dependencies.IsOn(other) && r.IsVisibleInTree()))
                CategoryList.Flash(row);
            return false;
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.ModId}] Could not switch a copy of a mod installed twice; the game's tickbox switches every copy of it: {e}");
            return true;
        }
    }
}

// Keeps a ticked or unticked row in the list under the Show: choice (ListFilter.KeepCopies: with every copy of its id,
// which a tick switches together), so it doesn't vanish under the mouse. A prefix, so the page bar's redraw counts it.
[HarmonyPatch(typeof(NModMenuRow), "OnTickboxToggled")]
static class TickKeepPatch
{
    static void Prefix(NModMenuRow __instance)
    {
        try
        {
            ListFilter.KeepCopies([__instance.Mod?.manifest?.id]);
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

// Adds the drop-in translations (Translations) to a language's tables as the game loads them. Apply skips English (the
// fallback tables loaded under every language, and the metrics tables); allowOverride is a second guard.
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
