using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;

namespace SpireModManager;

// A mod's own settings, where a library it uses keeps a page for it: the info panel's Settings button (Jordan, 24 Oct).
// Only frameworks are known here, never one mod: RitsuLib (its overlay, over our page) and BaseLib (its submenu, pushed
// on our page's stack). All by reflection, looked up once by type full name; a library or member that isn't there
// leaves its pages out, with one Info line. Which wins for a mod with both: its own RitsuLib page (one its own code
// registered), then its BaseLib page, then a RitsuLib page only matched by id. RitsuLib's mirror copies of other
// libraries' pages are never a mod's own. Only for the copy of a mod that loaded this session: one that didn't can't have
// registered. Main thread only, as both libraries' registries are.
static class SettingsLink
{
    public enum Library { RitsuLib, BaseLib }

    // What the button opens: RitsuLib's (mod id, page id), or BaseLib's registry key.
    public sealed record Target(Library Library, string Key, string? PageId);

#if DEVTOOLS
    // Self-test: the stand-ins' namespace prefix ("SmmTest."), so the real lookups run against them and a run with the
    // real libraries can't mix the two.
    public static string TestPrefix = "";
    static string Prefix => TestPrefix;
#else
    static string Prefix => "";
#endif

    // RitsuLib's mirrors of other libraries' pages (each under the mirrored mod's id): never a mod's own.
    static readonly string[] MirrorIds = ["baselib", "baselib-generated", "modconfig"];
    static bool Mirror(string id) => MirrorIds.Contains(id, StringComparer.OrdinalIgnoreCase) || id.StartsWith("jmcmodlib", StringComparison.OrdinalIgnoreCase);

    // Pages that didn't open this session (greyed, with the failure tip) and failures already logged, per mod copy.
    static readonly HashSet<string> Failed = [];

    // Detection per mod copy, cleared when a registry changes (Fingerprint).
    static readonly Dictionary<string, Target?> Found = [];
    static object? _fingerprint;

    static string CopyKey(Mod mod) => $"{mod.manifest?.id}|{Copies.CopyOf(mod)}";

    public static bool FailedFor(Mod mod) => Failed.Contains(CopyKey(mod));

    // The page this mod's Settings button opens, or null for none.
    public static Target? For(Mod mod)
    {
        if (!Ran(mod))
            return null;
        var fingerprint = Fingerprint();
        if (!Equals(fingerprint, _fingerprint))
        {
            (_fingerprint, Owners) = (fingerprint, null);
            Found.Clear();
        }
        var key = CopyKey(mod);
        if (Found.TryGetValue(key, out var known))
            return known;
        Target? found = null;
        try
        {
            found = Find(mod);
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.ModId}] Couldn't tell whether {mod.manifest?.id} has settings: {e.Message}");
        }
        return Found[key] = found;
    }

    // This copy loaded this session (or failed after its code ran, so it may have registered).
    static bool Ran(Mod mod) => mod.state == ModLoadState.Loaded || mod.state == ModLoadState.Failed && Compat.Assemblies(mod).Any();

    static Target? Find(Mod mod)
    {
        var id = mod.manifest?.id;
        if (id == null)
            return null;
        // 1. Its own RitsuLib page: one its own code registered.
        var pages = Ritsu.Pages();
        if (pages.FirstOrDefault(p => p.Source is { } source && OwnerOf(source) == mod) is { } own)
            return new Target(Library.RitsuLib, own.ModId, own.Id);
        // 2. Its BaseLib page.
        if (Base.KeyFor(mod) is { } key)
            return new Target(Library.BaseLib, key, null);
        // 3. A RitsuLib page registered under its id by other code (a mirror never counts).
        if (pages.FirstOrDefault(p => p.Source == null && string.Equals(p.ModId, id, StringComparison.OrdinalIgnoreCase)) is { } byId)
            return new Target(Library.RitsuLib, byId.ModId, byId.Id);
        return null;
    }

    // Changes when a mod registers a page in either library: RitsuLib's page snapshot (a new list on each register) and
    // BaseLib's registry count.
    static object Fingerprint() => (Ritsu.Snapshot(), Base.Count());

    // Which loaded mod an assembly belongs to: one in its assemblies, else the one whose folder holds the file (the
    // longest such folder, and only where a single loaded mod has it: a Workshop item with several manifests shares one).
    static Dictionary<Assembly, Mod?>? Owners;

    static Mod? OwnerOf(Assembly assembly)
    {
        Owners ??= [];
        if (Owners.TryGetValue(assembly, out var known))
            return known;
        var mods = ModManager.Mods.Where(Ran).ToList();
        var owner = mods.FirstOrDefault(m => Compat.Assemblies(m).Contains(assembly));
#if DEVTOOLS
        owner = TestOwner?.Invoke(assembly) ?? owner;
#endif
        if (owner == null && !assembly.IsDynamic && assembly.Location is { Length: > 0 } location)
        {
            var file = Path.GetFullPath(location);
            static string Folder(string path) => Path.GetFullPath(path).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            var holding = mods.Where(m => m.path is { Length: > 0 } && file.StartsWith(Folder(m.path), StringComparison.OrdinalIgnoreCase))
                .GroupBy(m => Folder(m.path)).OrderByDescending(g => g.Key.Length).FirstOrDefault();
            owner = holding is { } group && group.Count() == 1 ? group.First() : null;
        }
        return Owners[assembly] = owner;
    }

#if DEVTOOLS
    // Self-test: the stand-ins live in this assembly, so which fixture mod "registered" one is said here.
    public static Func<Assembly, Mod?>? TestOwner;

    // Self-test: forget what was found and failed (a fresh session's view).
    public static void TestReset()
    {
        Found.Clear();
        Failed.Clear();
        (_fingerprint, Owners) = (null, null);
        Ritsu.Reset();
        Base.Reset();
    }
#endif

    // Opens this mod's settings from our page. Our page keeps what it shows: fields being typed in are saved first, no
    // menu or card of ours stays up, and no text box of ours keeps the keyboard under the library's screen.
    public static void Open(NModInfoContainer container, Mod mod)
    {
        var key = CopyKey(mod);
        Target? target = null;
        try
        {
            if (Screen(container) is not { } page)
                throw new InvalidOperationException("the panel isn't on a screen");
            ModDetails.CommitEdits(container);
            MenuPanel.Close();
            Hover.Hide();
            page.GetViewport().GuiReleaseFocus();
            Found.Remove(key); // the press may come long after the panel filled
            target = For(mod);
            if (target == null)
                return;
            var list = History.CurrentMods();
            switch (target.Library)
            {
                case Library.RitsuLib:
                    Ritsu.Open(target, mod, page, list);
                    break;
                case Library.BaseLib:
                    Base.Open(target, page, list);
                    break;
            }
            Log.Info($"[{ModEntry.ModId}] Opened {mod.manifest?.id}'s settings ({target.Library})");
        }
        catch (Exception e)
        {
            Fail(mod, target, e is TargetInvocationException { InnerException: { } inner } ? inner.Message : e.Message);
        }
        finally
        {
            // Gone (none now, as when its library forgot it after the panel filled), or didn't open: greyed, its tip saying which.
            if (Found.GetValueOrDefault(key) == null || Failed.Contains(key))
                ModDetails.PlaceSettings(container, mod);
        }
    }

    // The screen the panel is on (our Modding page).
    static NSubmenu? Screen(Node node)
    {
        for (var at = node.GetParent(); at != null; at = at.GetParent())
            if (at is NSubmenu screen)
                return screen;
        return null;
    }

    // Didn't open: greyed with the failure tip for the rest of the session, and one warning.
    static void Fail(Mod mod, Target? target, string why)
    {
        if (Failed.Add(CopyKey(mod)))
            Log.Warn($"[{ModEntry.ModId}] Couldn't open {mod.manifest?.id}'s settings ({target?.Library.ToString() ?? "none"}): {why}");
    }

    // Where nothing turned out to be there to open (RitsuLib's mod-not-found, page-not-found): greyed as for a mod with none,
    // an Info line.
    static void Gone(Mod mod, string why)
    {
        Found[CopyKey(mod)] = null;
        Log.Info($"[{ModEntry.ModId}] {mod.manifest?.id} has no settings page to open here now ({why})");
    }

    // RitsuLib: STS2RitsuLib.Settings (ModSettingsRegistry.GetPages, ModSettingsNavigator.RequestOpenByIds).
    static class Ritsu
    {
        public sealed record Page(string ModId, string Id, Assembly? Source);

        static bool _resolved;
        static MethodInfo? _pages, _open;
        static PropertyInfo? _modId, _id, _parent, _surfaces, _source, _sections, _visibleWhen;

        public static void Reset() => _resolved = false;

        static bool Resolve()
        {
            if (_resolved)
                return _open != null;
            _resolved = true;
            (_pages, _open) = (null, null);
            var registry = Bmm.TypeNamed(Prefix + "STS2RitsuLib.Settings.ModSettingsRegistry");
            var navigator = Bmm.TypeNamed(Prefix + "STS2RitsuLib.Settings.ModSettingsNavigator");
            var page = Bmm.TypeNamed(Prefix + "STS2RitsuLib.Settings.ModSettingsPage");
            if (registry == null || navigator == null || page == null)
                return false; // not installed, or off: nothing to say
            var pages = registry.GetMethod("GetPages", BindingFlags.Public | BindingFlags.Static, []);
            var open = navigator.GetMethod("RequestOpenByIds", BindingFlags.Public | BindingFlags.Static, [typeof(string), typeof(string), typeof(string), typeof(string)]);
            (_modId, _id, _parent, _surfaces, _source, _sections, _visibleWhen) = (page.GetProperty("ModId"), page.GetProperty("Id"),
                page.GetProperty("ParentPageId"), page.GetProperty("VisibleOnHostSurfaces"), page.GetProperty("SourceAssembly"),
                page.GetProperty("Sections"), page.GetProperty("VisibleWhen"));
            var missing = new (string Name, object? Member)[]
            {
                ("ModSettingsRegistry.GetPages", pages), ("ModSettingsNavigator.RequestOpenByIds", open), ("ModSettingsPage.ModId", _modId),
                ("ModSettingsPage.Id", _id), ("ModSettingsPage.ParentPageId", _parent), ("ModSettingsPage.VisibleOnHostSurfaces", _surfaces),
                ("ModSettingsPage.SourceAssembly", _source), ("ModSettingsPage.Sections", _sections),
            }.FirstOrDefault(m => m.Member == null).Name;
            if (missing != null)
            {
                Log.Info($"[{ModEntry.ModId}] RitsuLib's settings can't be opened from here ({missing} missing)");
                return false;
            }
            (_pages, _open) = (pages, open);
            return true;
        }

        // The registry's snapshot (a new one after each register), for the fingerprint.
        public static object? Snapshot() => Resolve() ? _pages?.Invoke(null, null) : null;

        // The top-level pages it would show on the main menu, with something in them, in its own order.
        public static List<Page> Pages()
        {
            if (Snapshot() is not System.Collections.IEnumerable all)
                return [];
            var pages = new List<Page>();
            foreach (var page in all)
            {
                if (page == null || _modId?.GetValue(page) is not string modId || _id?.GetValue(page) is not string id || Mirror(id)
                    || _parent?.GetValue(page) is string { Length: > 0 }
                    || _surfaces?.GetValue(page) is not { } surfaces || (Convert.ToInt32(surfaces) & 1) == 0 // MainMenu = 1
                    || _sections?.GetValue(page) is not System.Collections.ICollection { Count: > 0 } || !Visible(page))
                    continue;
                pages.Add(new Page(modId, id, _source?.GetValue(page) as Assembly));
            }
            return pages;
        }

        // Its own condition for showing the page, where it has one (mod code: a throw counts as shown, as RitsuLib's own
        // list does).
        static bool Visible(object page)
        {
            try
            {
                return _visibleWhen?.GetValue(page) is not Func<bool> when || when();
            }
            catch (Exception)
            {
                return true;
            }
        }

        // Its overlay over our page; Esc or its back closes it. Our page isn't hidden, so its return is watched for:
        // where the mod list changed under it (RitsuLib's own load order or Workshop pages), the page reads it again.
        public static void Open(Target target, Mod mod, Control page, IReadOnlyList<History.ModEntry> list)
        {
            if (!Resolve())
                throw new InvalidOperationException("RitsuLib isn't there");
            var result = _open?.Invoke(null, [target.Key, target.PageId, null, null]);
            var type = result?.GetType();
            if (type?.GetProperty("Success")?.GetValue(result) is true)
            {
                PageVisits.WatchOverlay(page, list);
                return;
            }
            var code = type?.GetProperty("Code")?.GetValue(result) as string ?? "no result";
            var message = type?.GetProperty("Message")?.GetValue(result) as string;
            if (code is "mod-not-found" or "page-not-found")
                Gone(mod, code);
            else
                Fail(mod, target, message is { Length: > 0 } ? $"{code}: {message}" : code);
        }
    }

    // BaseLib: BaseLib.Config (ModConfigRegistry, BaseLibConfig.LastModConfigModId) and its submenu
    // BaseLib.Config.UI.NModConfigSubmenu, which opens on the mod it last remembers.
    static class Base
    {
        static bool _resolved;
        static MethodInfo? _get, _all;
        static FieldInfo? _configs;
        static PropertyInfo? _last;
        static Type? _submenu;

        public static void Reset() => _resolved = false;

        static bool Resolve()
        {
            if (_resolved)
                return _submenu != null;
            _resolved = true;
            _submenu = null;
            var registry = Bmm.TypeNamed(Prefix + "BaseLib.Config.ModConfigRegistry");
            var config = Bmm.TypeNamed(Prefix + "BaseLib.Config.BaseLibConfig");
            var submenu = Bmm.TypeNamed(Prefix + "BaseLib.Config.UI.NModConfigSubmenu");
            if (registry == null || config == null || submenu == null)
                return false;
            (_get, _all) = (registry.GetMethod("Get", BindingFlags.Public | BindingFlags.Static, [typeof(string)]),
                registry.GetMethod("GetAll", BindingFlags.Public | BindingFlags.Static, []));
            _configs = registry.GetField("ModConfigs", BindingFlags.NonPublic | BindingFlags.Static);
            _last = config.GetProperty("LastModConfigModId", BindingFlags.Public | BindingFlags.Static);
            var missing = new (string Name, object? Member)[]
            {
                ("ModConfigRegistry.Get", _get), ("ModConfigRegistry.GetAll", _all), ("BaseLibConfig.LastModConfigModId", _last),
            }.FirstOrDefault(m => m.Member == null).Name;
            if (missing != null)
            {
                Log.Info($"[{ModEntry.ModId}] BaseLib's settings can't be opened from here ({missing} missing)");
                return false;
            }
            _submenu = submenu;
            return true;
        }

        public static int Count() => !Resolve() ? -1
            : _configs?.GetValue(null) is System.Collections.ICollection configs ? configs.Count
            : _all?.Invoke(null, null) is System.Collections.ICollection all ? all.Count : -1;

        // Its registry key: its manifest id's config, else the first whose code is the mod's (a key of its own, as
        // Alchemist's "The Alchemist"). Only one BaseLib lists (VisibleInModList; HasVisibleSettings on older builds).
        public static string? KeyFor(Mod mod)
        {
            if (!Resolve())
                return null;
            var config = _get?.Invoke(null, [mod.manifest?.id ?? ""]);
            if (config == null && _all?.Invoke(null, null) is System.Collections.IEnumerable all)
                config = all.Cast<object?>().FirstOrDefault(c => c != null && OwnerOf(c.GetType().Assembly) == mod);
            if (config == null)
                return null;
            var type = config.GetType();
            var listed = type.GetMethod("VisibleInModList", BindingFlags.Public | BindingFlags.Instance, [])
                ?? type.GetMethod("HasVisibleSettings", BindingFlags.Public | BindingFlags.Instance, []);
            if (listed?.Invoke(config, null) is false)
                return null;
            return type.GetProperty("ModId")?.GetValue(config) as string;
        }

        // Its submenu pushed on our page's stack, on that mod (the id goes in before the push, which shows it at once).
        // Back pops it, and our page shows again. A push that threw after it got on the stack is popped.
        public static void Open(Target target, Control page, IReadOnlyList<History.ModEntry> list)
        {
            if (!Resolve() || _submenu == null)
                throw new InvalidOperationException("BaseLib isn't there");
            if (page.GetParent() is not NSubmenuStack stack)
                throw new InvalidOperationException("not on the main menu's screens");
            _last?.SetValue(null, target.Key);
            PageVisits.Cover(page, list);
            try
            {
                stack.PushSubmenuType(_submenu);
            }
            catch (Exception)
            {
                if (stack.Peek() is { } top && top != page && top.GetType() == _submenu)
                    stack.Pop();
                if (stack.Peek() == page && !page.Visible)
                    page.Visible = true;
                throw;
            }
        }
    }
}

// Our page covered by another screen is not our page closed (Jordan, 24 Oct, the Settings button): its open and leave
// work (Revert all's baseline, the changes list's way back, the Show: choice's kept rows, Load Order) waits while it's
// only covered. Hidden by the game's Push (SubmenuPushPatch) with our page still on top of its stack is covered: Push hides
// the old top before it pushes, and Pop pops before it hides. Hidden any other way is closed. Shown again after a cover is
// a return, unless the game's mod list changed while it was covered (another screen saved it): then the page reads it
// again (Changed), as a fresh open would.
static class PageVisits
{
    static bool _covered;
    static IReadOnlyList<History.ModEntry>? _list;

    // Inside the game's NSubmenuStack.Push (any mod's screen pushed on any stack).
    public static bool Pushing;

    // Raised when the mod list changed under a screen that covered our page: the page reads it again.
    public static event Action? Changed;

    // Each page handler's hide: whether this is a cover (not a close). The same answer for every handler of one hide.
    public static bool Hidden(Control page)
    {
        var covered = Pushing && page.GetParent() is NSubmenuStack stack && stack.Peek() == page;
        if (covered && !_covered)
            _list ??= History.CurrentMods();
        else if (!covered)
            _list = null;
        _covered = covered;
        return covered;
    }

    // Each page handler's show: whether it's a return from being covered (the page keeps what it had). A changed mod list
    // is read again once, after the handlers.
    public static bool Returning(Control page)
    {
        if (!_covered)
            return false;
        if (_list is { } before)
        {
            _list = null;
            Callable.From(() => Compare(before)).CallDeferred();
        }
        return true;
    }

    // The settings button's BaseLib path: the list as it was before the push (Hidden takes it if none).
    public static void Cover(Control page, IReadOnlyList<History.ModEntry> list) => _list = list;

    static void Compare(IReadOnlyList<History.ModEntry> before)
    {
        if (!History.CurrentMods().SequenceEqual(before))
        {
            Log.Info($"[{ModEntry.ModId}] The mod list changed while another screen covered the page: read again");
            Changed?.Invoke();
        }
    }

    // A screen over our page that doesn't hide it (RitsuLib's overlay): until the game's current screen is our page again,
    // then the list compared as above. The page's keys wait meanwhile (OnTop).
    public static void WatchOverlay(Control page, IReadOnlyList<History.ModEntry> list)
    {
        var tree = page.GetTree();
        var (opened, waited) = (false, 0);
        void Tick()
        {
            if (!GodotObject.IsInstanceValid(page))
            {
                tree.ProcessFrame -= Tick;
                return;
            }
            var top = ActiveScreenContext.Instance.GetCurrentScreen() == page;
            if (!opened)
            {
                opened = !top; // the overlay opens on a later frame
                if (!opened && ++waited > 120) // it never came up (RitsuLib logs why): nothing to watch
                    tree.ProcessFrame -= Tick;
                return;
            }
            if (!top)
                return;
            tree.ProcessFrame -= Tick;
            Compare(list);
        }
        tree.ProcessFrame += Tick;
    }

    // Our page is the screen the game's input goes to: not covered by another (RitsuLib's overlay counts, as it makes
    // itself the current screen while it's open).
    public static bool OnTop(Control page) => ActiveScreenContext.Instance.GetCurrentScreen() == page;
}
