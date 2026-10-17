using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;

namespace SpireModManager;

// Better Mod Menu alongside us: paused with its parts hidden in our views; its page stands in for the game's in
// Vanilla View. Reached by reflection, looked up once; a missing member is logged once and that step skipped.
static class Bmm
{
    public const string ModId = "BetterModMenu", HarmonyId = ModId;
    const string Chrome = "BetterModMenuChrome", Actions = "BetterModMenuSelectedModActions", RowControls = "RowCustomControls";
    const string GroupPrefix = "ModGroup", RoomMeta = "better_mod_menu_top_bar_room";

    static readonly Type? Context = TypeNamed("BetterModMenu.Patches.ModdingScreenContext");
    static readonly FieldInfo? CurrentScreen = Context == null ? null : AccessTools.Field(Context, "_currentScreenRef");
    static readonly MethodInfo? Track = Context == null ? null : AccessTools.Method(Context, "TrackCurrentScreen");
    static readonly MethodInfo? Session = Context == null ? null : AccessTools.Method(Context, "GetSession");
    static readonly MethodInfo? IsCurrentScreen = Context == null ? null : AccessTools.Method(Context, "IsCurrentScreen");
    static readonly MethodInfo? RefreshGroups = TypeNamed("BetterModMenu.Patches.NModdingScreenPatch") is { } screenPatch
        ? AccessTools.Method(screenPatch, "RefreshGroupsUI", []) : null;
    static readonly MethodInfo? ChromeLayout = TypeNamed("BetterModMenu.Patches.ModdingScreenActionFlow") is { } flow
        ? AccessTools.Method(flow, "UpdateChromeLayout") : null;
    static readonly MethodInfo? RowNames = TypeNamed("BetterModMenu.Patches.NModMenuRowPatch") is { } rowPatch
        ? AccessTools.Method(rowPatch, "RefreshVisibleModNames") : null;
    static readonly HashSet<string> Missing = [];

    // Loaded: its patch class exists (whatever its mod id).
    public static bool Present { get; } = TypeNamed("BetterModMenu.Patches.NModdingScreenPatch") != null;

    // A type by its full name from whichever loaded assembly has it. Not AccessTools.TypeByName: when the type isn't
    // there (most players), that scans every type of every loaded mod twice, ~200 ms with 250 mods.
    static Type? TypeNamed(string fullName)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            try
            {
                if (assembly.GetType(fullName, throwOnError: false) is { } type)
                    return type;
            }
            catch (Exception)
            {
                // an assembly that can't be read for types: not Better Mod Menu's
            }
        return null;
    }

    // Ticked in the mod list (on in the game's saved list), loaded now or not: either way it loads after a relaunch.
    public static bool Ticked => Dependencies.Find(ModId) is { } mod && Dependencies.IsOn(mod);

    // Both managers ticked (LoadOrder's banner says so); profiles keep ours on, but the player can untick its row.
    public static bool BothTicked => Ticked && Dependencies.Find(ModEntry.ModId) is { } ours && Dependencies.IsOn(ours);

    // The mod managers we know of that are ticked, ours first: what the banner's card names.
    public static IEnumerable<Mod> TickedManagers() =>
        new[] { ModEntry.ModId, ModId }.Select(Dependencies.Find).OfType<Mod>().Where(Dependencies.IsOn);

    // Forgets a dismissed banner once the two aren't both ticked, so it shows again next time (view state, no undo).
    // Checked on every toolbar update and once at launch.
    public static void ForgetDismissedWarning()
    {
        if (!Store.Data.ManagersWarningDismissed || BothTicked)
            return;
        Store.Data.ManagersWarningDismissed = false;
        Store.SaveViewState();
    }

    // Our views: it stops re-laying out and regrouping the screen.
    public static void Detach()
    {
        if (Need(CurrentScreen, "ModdingScreenContext._currentScreenRef") is { } field)
            Try("detach", () => field.SetValue(null, null));
    }

    // Vanilla View: its page again, built from the game's layout, with its parts shown, regrouped and laid out.
    public static void Attach(NModdingScreen screen)
    {
        if (!Present)
            return;
        Show(screen, true);
        if (Need(Session, "ModdingScreenContext.GetSession") is { } session)
            Try("forget its layout", () =>
            {
                var state = session.Invoke(null, [screen]);
                if (state == null)
                    return;
                AccessTools.Property(state.GetType(), "OriginalModsScrollPosition")?.SetValue(state, null);
                AccessTools.Property(state.GetType(), "OriginalModsScrollSize")?.SetValue(state, null);
            });
        if (Need(Track, "ModdingScreenContext.TrackCurrentScreen") is { } track)
            Try("attach", () => track.Invoke(null, [screen]));
        Refresh(screen);
    }

    // Its names, groups and layout again after one of our redraws; does nothing while it's paused.
    public static void Refresh(NModdingScreen screen)
    {
        if (!Present || Need(IsCurrentScreen, "ModdingScreenContext.IsCurrentScreen") is not { } current
            || current.Invoke(null, [screen]) is not true)
            return;
        RestyleRows(screen);
        if (Need(RefreshGroups, "NModdingScreenPatch.RefreshGroupsUI") is { } refresh)
            Try("regroup", () => refresh.Invoke(null, null));
        if (Need(ChromeLayout, "ModdingScreenActionFlow.UpdateChromeLayout") is { } layout)
            Try("lay out", () => layout.Invoke(null, [screen])); // (an older build stops before its own layout step)
    }

    // Its parts on the screen: the bars above the list, the info panel's buttons, each row's own controls.
    public static void Show(NModdingScreen screen, bool shown)
    {
        if (!Present)
            return;
        if (screen.GetNodeOrNull<CanvasItem>(Chrome) is { } chrome)
            chrome.Visible = shown;
        if (screen.GetNodeOrNull<CanvasItem>($"%ModInfoContainer/{Actions}") is { } actions)
            actions.Visible = shown;
        var content = screen.GetNode<Control>("%ModsScrollContainer/Mask/Content");
        foreach (var row in content.GetChildren().OfType<NModMenuRow>())
            if (row.FindChild(RowControls, true, false) is CanvasItem controls)
                controls.Visible = shown;
    }

    // Our views: its group headers out of our list (it makes them again on its page).
    public static void RemoveGroups(Control content)
    {
        if (!Present)
            return;
        foreach (var group in content.GetChildren().Where(c => c.Name.ToString().StartsWith(GroupPrefix)).ToList())
        {
            content.RemoveChild(group);
            group.QueueFree();
        }
    }

    // Takes back its push-down of the list panel (made to fit its bar) for the parts Layout doesn't place itself.
    public static void Unpush(Control list)
    {
        if (!list.HasMeta(RoomMeta))
            return;
        var height = (float)list.GetMeta(RoomMeta);
        var scroll = list.GetNode<Control>("ModsScrollContainer");
        // By offsets, not Position: a part just made can still have its pre-layout minimum size.
        foreach (var child in list.GetChildren().OfType<Control>().Where(c => c != scroll))
            (child.OffsetTop, child.OffsetBottom) = (child.OffsetTop + height, child.OffsetBottom + height);
        list.RemoveMeta(RoomMeta);
    }

    // Its page: rows styled its way (our redraws reset them).
    static void RestyleRows(NModdingScreen screen)
    {
        foreach (var row in screen.GetNode<Control>("%ModsScrollContainer/Mask/Content").GetChildren().OfType<NModMenuRow>())
            if (row.GetNodeOrNull<CanvasItem>("PlatformIcon") is { } icon)
                icon.Visible = false;
        if (Need(RowNames, "NModMenuRowPatch.RefreshVisibleModNames") is { } names)
            Try("restyle rows", () => names.Invoke(null, [screen]));
    }

    static T? Need<T>(T? member, string name) where T : class
    {
        if (member == null && Present && Missing.Add(name))
            Log.Warn($"[{ModEntry.ModId}] Better Mod Menu: {name} not found; that step is skipped");
        return member;
    }

    static void Try(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            if (Missing.Add(what)) // the whole exception, once: it happens inside Better Mod Menu, and its stack says where
                Log.Warn($"[{ModEntry.ModId}] Better Mod Menu: could not {what}: {(e as TargetInvocationException)?.InnerException ?? e}");
        }
    }
}
