using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.ProfileScreen;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;

// The self-test's stand-ins for the two settings libraries SettingsLink opens (TestSettingsButton): the members it reaches
// by reflection, under the "SmmTest." prefix so a run with the real libraries installed never mixes the two.

namespace SmmTest.BaseLib.Config
{
    internal static class ModConfigRegistry
    {
        static readonly Dictionary<string, object> ModConfigs = [];

        public static object? Get(string modId) => ModConfigs.GetValueOrDefault(modId);
        public static List<object> GetAll() => [.. ModConfigs.Values];
        public static void Register(string key, TestConfig config) => ModConfigs[key] = config;
        public static void Clear() => ModConfigs.Clear();
    }

    internal static class BaseLibConfig
    {
        public static string? LastModConfigModId { get; set; }
    }

    // A mod's config: its key, and whether BaseLib lists it.
    internal sealed class TestConfig(string modId, bool listed = true)
    {
        public string ModId => modId;
        public bool VisibleInModList() => listed;
    }
}

namespace SmmTest.BaseLib.Config.UI
{
    // Only its type: pushing it pushes a real screen of the game's (SettingsStandIns.Patch).
    internal sealed class NModConfigSubmenu;
}

namespace SmmTest.STS2RitsuLib.Settings
{
    internal sealed class ModSettingsPage
    {
        public string ModId { get; init; } = "";
        public string Id { get; init; } = "";
        public string? ParentPageId { get; init; }
        public int VisibleOnHostSurfaces { get; init; } = 3; // MainMenu and in a run
        public Assembly? SourceAssembly { get; init; }
        public List<object> Sections { get; init; } = [new()];
        public Func<bool>? VisibleWhen { get; init; }
    }

    // A new list on each register, as RitsuLib's snapshot is.
    internal static class ModSettingsRegistry
    {
        static IReadOnlyList<ModSettingsPage> _pages = [];

        public static IReadOnlyList<ModSettingsPage> GetPages() => _pages;
        public static void Register(ModSettingsPage page) => _pages = [.. _pages, page];
        public static void Clear() => _pages = [];
    }

    internal sealed record OpenResult(bool Success, string Code, string Message);

    // Its overlay is only "open": the game's current screen is something else meanwhile (SettingsStandIns.Patch).
    internal static class ModSettingsNavigator
    {
        public static string? Answer; // null opens it; else the code it answers with
        public static (string ModId, string PageId)? Asked;
        public static bool OverlayOpen;

        public static OpenResult RequestOpenByIds(string modId, string pageId, string sectionId, string anchorId)
        {
            Asked = (modId, pageId);
            if (Answer is { } code)
                return new OpenResult(false, code, "made up by the self-test");
            OverlayOpen = true;
            return new OpenResult(true, "ok", "");
        }

        public static void Clear() => (Answer, Asked, OverlayOpen) = (null, null, false);
    }
}

namespace SpireModManager
{
    static class SettingsStandIns
    {
        const string HarmonyId = ModEntry.ModId + ".SelfTestSettings";

        // BaseLib's submenu pushed as the game's profile screen; RitsuLib's open overlay as the game's current screen.
        public static void Patch()
        {
            var harmony = new Harmony(HarmonyId);
            harmony.Patch(AccessTools.Method(typeof(NMainMenuSubmenuStack), nameof(NMainMenuSubmenuStack.GetSubmenuType), [typeof(Type)]),
                prefix: new HarmonyMethod(typeof(SettingsStandIns), nameof(SubmenuType)));
            harmony.Patch(AccessTools.Method(typeof(ActiveScreenContext), nameof(ActiveScreenContext.GetCurrentScreen)),
                postfix: new HarmonyMethod(typeof(SettingsStandIns), nameof(CurrentScreen)));
        }

        public static void Unpatch()
        {
            new Harmony(HarmonyId).UnpatchAll(HarmonyId);
            SmmTest.BaseLib.Config.ModConfigRegistry.Clear();
            SmmTest.BaseLib.Config.BaseLibConfig.LastModConfigModId = null;
            SmmTest.STS2RitsuLib.Settings.ModSettingsRegistry.Clear();
            SmmTest.STS2RitsuLib.Settings.ModSettingsNavigator.Clear();
        }

        static void SubmenuType(ref Type type)
        {
            if (type == typeof(SmmTest.BaseLib.Config.UI.NModConfigSubmenu))
                type = typeof(NProfileScreen);
        }

        static void CurrentScreen(ref IScreenContext? __result)
        {
            if (SmmTest.STS2RitsuLib.Settings.ModSettingsNavigator.OverlayOpen && NGame.Instance?.MainMenu is { } mainMenu)
                __result = mainMenu;
        }
    }
}
