using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;

namespace SpireModManager;

// Better Mod Menu alongside us. It rebuilds the same Modding screen, so: in our views (main list, Load Order) it
// is paused and its parts hidden; in Vanilla View its page stands in for the game's (it replaces that page
// anyway). Nothing of it is patched: this reaches a few of its members by reflection (looked up once). If one is
// missing (an update renamed it), it's logged once and that step does nothing.
static class Bmm
{
    public const string ModId = "BetterModMenu", HarmonyId = ModId;
    const string Chrome = "BetterModMenuChrome", Actions = "BetterModMenuSelectedModActions", RowControls = "RowCustomControls";
    public const string WarningName = "SpireModManagerBmmWarning";
    const string GroupPrefix = "ModGroup", RoomMeta = "better_mod_menu_top_bar_room";

    static readonly Type? Context = AccessTools.TypeByName("BetterModMenu.Patches.ModdingScreenContext");
    static readonly FieldInfo? CurrentScreen = Context == null ? null : AccessTools.Field(Context, "_currentScreenRef");
    static readonly MethodInfo? Track = Context == null ? null : AccessTools.Method(Context, "TrackCurrentScreen");
    static readonly MethodInfo? Session = Context == null ? null : AccessTools.Method(Context, "GetSession");
    static readonly MethodInfo? IsCurrentScreen = Context == null ? null : AccessTools.Method(Context, "IsCurrentScreen");
    static readonly MethodInfo? RefreshGroups = AccessTools.TypeByName("BetterModMenu.Patches.NModdingScreenPatch") is { } screenPatch
        ? AccessTools.Method(screenPatch, "RefreshGroupsUI", []) : null;
    static readonly MethodInfo? ChromeLayout = AccessTools.TypeByName("BetterModMenu.Patches.ModdingScreenActionFlow") is { } flow
        ? AccessTools.Method(flow, "UpdateChromeLayout") : null;
    static readonly MethodInfo? RowNames = AccessTools.TypeByName("BetterModMenu.Patches.NModMenuRowPatch") is { } rowPatch
        ? AccessTools.Method(rowPatch, "RefreshVisibleModNames") : null;
    static readonly HashSet<string> Missing = [];

    // Loaded: its patch class exists (whatever its mod id).
    public static bool Present { get; } = AccessTools.TypeByName("BetterModMenu.Patches.NModdingScreenPatch") != null;

    // Our views: it stops re-laying out and regrouping the screen.
    public static void Detach()
    {
        if (Need(CurrentScreen, "ModdingScreenContext._currentScreenRef") is { } field)
            Try("detach", () => field.SetValue(null, null));
    }

    // Vanilla View: its page again, built from the game's layout (its saved list size forgotten; Layout has put
    // the game's boxes back and taken its push down of the list away), its parts shown, then regrouped and laid out.
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

    // Its page after one of our redraws (rows reordered, titles and icons reset): its names, groups and layout
    // again. Does nothing while it's paused.
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

    // On our views while it's loaded, top right beside the page title (Layout places it).
    public static Label Warning(Control screen)
    {
        if (screen.GetNodeOrNull<Label>(WarningName) is { } existing)
            return existing;
        var warning = new Label
        {
            Name = WarningName, Text = Menus.Text("SPIRE_MOD_MANAGER.BMM_WARNING"),
            AutowrapMode = TextServer.AutowrapMode.WordSmart, VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right, MaxLinesVisible = 2, MouseFilter = Control.MouseFilterEnum.Stop,
            MouseDefaultCursorShape = Control.CursorShape.Help,
        };
        warning.AddThemeFontOverride("font", ModEntry.Font("res://themes/kreon_regular_shared.tres"));
        warning.AddThemeFontSizeOverride("font_size", 18);
        warning.AddThemeColorOverride("font_color", new Color(0.937f, 0.784f, 0.318f));
        screen.AddChild(warning);
        Hover.Watch(warning, "SpireModManagerBmmCard", () => Menus.Text("SPIRE_MOD_MANAGER.BMM_WARNING_TIP"));
        return warning;
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

    // Its push down of the list panel taken back: once per page, to make room for its bar above the list, it
    // moved the panel down (shorter) and its other parts up by as much. Layout then puts the boxes it knows; the
    // rest (Make/Get Mods, anything else in there) need this. It pushes again on its page.
    public static void Unpush(Control list)
    {
        if (!list.HasMeta(RoomMeta))
            return;
        var height = (float)list.GetMeta(RoomMeta);
        var scroll = list.GetNode<Control>("ModsScrollContainer");
        // By offsets, not Position: that rebuilds them from the cached size, which for a part just made can still be its
        // minimum before layout (a wrapping label: a letter a line, over a thousand pixels tall).
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
            if (Missing.Add(what))
                Log.Warn($"[{ModEntry.ModId}] Better Mod Menu: could not {what}: {(e as TargetInvocationException)?.InnerException?.Message ?? e.Message}");
        }
    }
}
