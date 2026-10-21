using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Localization.Fonts;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using SpireModManager.Api;

namespace SpireModManager;

// The API (src/SpireModManager/Api) as another mod sees it: only it is public, in the shapes a mod can bind to (and the
// lookups Mod Doctor's soft binding makes find each member), and each member does what its doc comment says. Every API
// member is named here (tools/check.cs checks), so none ships untested.
static partial class SelfTest
{
    static async Task TestApi(NMainMenu menu)
    {
        TestApiShape();
        await TestApiMenus(menu);
        await TestApiBanners(menu);
        TestApiLooks();
        await TestApiMods(menu);
    }

    static void TestApiShape()
    {
        var assembly = typeof(ModEntry).Assembly;
        var exported = assembly.GetExportedTypes().Select(t => t.FullName ?? "").Order().ToList();
        string[] api = ["ApiInfo", "BannerKind", "BannerOptions", "Banners", "Fonts", "GameResources", "MenuEntry", "MenuOptions", "ModCards", "ModNotes", "Mods",
            "Palette", "PopupMenus"];
        Check("API: only SpireModManager.Api's types are public, all of them", exported.SequenceEqual(api.Select(n => "SpireModManager.Api." + n).Order()),
            string.Join(", ", exported));
        Check("API: the assembly's version stays 1.0.0.0 (dependents bind to it)", assembly.GetName().Version == new Version(1, 0, 0, 0), $"{assembly.GetName().Version}");
        Check("API: ApiInfo.Version is a static readonly int field, 1", typeof(ApiInfo).GetField(nameof(ApiInfo.Version)) is { IsStatic: true, IsInitOnly: true } field
            && field.FieldType == typeof(int) && ApiInfo.Version == 1);
        Check("API: every option class is sealed, with a public parameterless constructor",
            new[] { typeof(MenuEntry), typeof(MenuOptions), typeof(BannerOptions), typeof(ModNotes) }.All(t => t.IsSealed && t.GetConstructor(Type.EmptyTypes) != null));
        Check("API: BannerKind's names and values are as shipped", Enum.GetNames<BannerKind>().SequenceEqual(["Problem", "Warning"])
            && (int)BannerKind.Problem == 0 && (int)BannerKind.Warning == 1);

        // Soft binding, as Mod Doctor's code does it: by name, with the exact parameter types, to delegates.
        T? Method<T>(string type, string name, params Type[] args) where T : Delegate =>
            assembly.GetType("SpireModManager.Api." + type)?.GetMethod(name, BindingFlags.Public | BindingFlags.Static, null, args, null)?.CreateDelegate<T>();
        T? Getter<T>(string type, string name) where T : Delegate =>
            assembly.GetType("SpireModManager.Api." + type)?.GetProperty(name, BindingFlags.Public | BindingFlags.Static)?.GetMethod?.CreateDelegate<T>();
        var version = assembly.GetType("SpireModManager.Api.ApiInfo")?.GetField("Version", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        Check("API: a soft binder finds what it binds (ApiInfo.Version, PopupMenus.Close and IsOpen, ModCards.Watch, WatchLinks and Text)",
            version is 1 && Method<Func<bool>>("PopupMenus", "Close") != null && Getter<Func<bool>>("PopupMenus", "IsOpen") != null
            && Method<Action<Control, string>>("ModCards", "Watch", typeof(Control), typeof(string)) != null
            && Method<Action<RichTextLabel, Func<string, string?>>>("ModCards", "WatchLinks", typeof(RichTextLabel), typeof(Func<string, string?>)) != null
            && Method<Func<string, string?>>("ModCards", "Text", typeof(string)) != null);
    }

    // The screen's hotkey blockers (the game's NHotkeyManager._blockingScreens), to see a menu let go of them.
    static int HotkeyBlockers() =>
        typeof(NHotkeyManager).GetField("_blockingScreens", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(NHotkeyManager.Instance) is System.Collections.ICollection held
            ? held.Count : -1;

    // SMM's menu layers on the scene root (none stays after a menu closes, or fails).
    static int MenuLayers(Node menu) => menu.GetTree().Root.GetChildren().Count(c => c.Name.ToString().StartsWith(MenuPanel.RootName));

    static async Task TestApiMenus(NMainMenu menu)
    {
        var owner = new Control { Name = "SmmApiMenuOwner", Size = new Vector2(10, 10) };
        menu.AddChild(owner);
        var (ran, blockers) = (0, HotkeyBlockers());
        List<MenuEntry> Entries() =>
        [
            MenuEntry.Heading("Api Test"),
            new MenuEntry { Label = "Run", OnSelect = () => ran++ },
            new MenuEntry { Label = "Reset position", OnSelect = () => ran += 100, Disabled = true },
            MenuEntry.Divider(),
            new MenuEntry { Label = "Throws", OnSelect = () => throw new InvalidOperationException("a test entry that throws") },
        ];
        try
        {
            var shown = PopupMenus.Show(owner, Entries());
            await Wait(menu, 0.1);
            Check("API menus: Show opens one; IsOpen and Current agree; the screen's hotkeys are blocked", shown != null && PopupMenus.IsOpen
                && PopupMenus.Current == shown && HotkeyBlockers() == blockers + 1, $"blockers {blockers} -> {HotkeyBlockers()}");
            Check("API menus: Rows lists each row with whether it's dimmed, Headings the headings (a divider has none)", shown != null
                && PopupMenus.Rows(shown).SequenceEqual([("Run", false), ("Reset position", true), ("Throws", false)])
                && PopupMenus.Headings(shown).SequenceEqual(["Api Test"]),
                shown == null ? "no menu" : string.Join(", ", PopupMenus.Rows(shown)) + " | " + string.Join(", ", PopupMenus.Headings(shown)));
            Check("API menus: Press on a dimmed row is false and runs nothing; on a row, the menu closes, then its action runs, and the hotkeys are back",
                shown != null && !PopupMenus.Press(shown, "Reset position") && ran == 0 && PopupMenus.Press(shown, "Run") && ran == 1 && !PopupMenus.IsOpen
                && HotkeyBlockers() == blockers, $"ran {ran}, open {PopupMenus.IsOpen}, blockers {HotkeyBlockers()}");

            shown = PopupMenus.Show(owner, Entries());
            Check("API menus: an action that throws is logged, and the menu still closes", shown != null && PopupMenus.Press(shown, "Throws")
                && !PopupMenus.IsOpen && ApiGuard.HasLogged($"a PopupMenus entry's OnSelect from {typeof(SelfTest).Assembly.GetName().Name}"));
            // Named by the callback's own code, not by who's on the stack: one of .NET's (it throws outside a no-GC region).
            var foreign = PopupMenus.Show(owner, [new MenuEntry { Label = "Foreign", OnSelect = GC.EndNoGCRegion }]);
            Check("API menus: a callback that throws is logged naming the mod whose code it is (here .NET's own)", foreign != null
                && PopupMenus.Press(foreign, "Foreign") && ApiGuard.HasLogged($"a PopupMenus entry's OnSelect from {typeof(GC).Assembly.GetName().Name}"));

            var escaped = PopupMenus.Show(owner, Entries());
            await Wait(menu, 0.1);
            Input.ParseInputEvent(new InputEventKey { Keycode = Key.Escape, Pressed = true });
            await Wait(menu, 0.1);
            Input.ParseInputEvent(new InputEventKey { Keycode = Key.Escape, Pressed = false });
            Check("API menus: the game's cancel (Escape) closes one, and the hotkeys are back", escaped != null && !PopupMenus.IsOpen && HotkeyBlockers() == blockers,
                $"opened {escaped != null}, open {PopupMenus.IsOpen}, blockers {HotkeyBlockers()}");

            var first = PopupMenus.Show(owner, Entries());
            var second = PopupMenus.Show(owner, Entries());
            await Wait(menu, 0.1);
            Check("API menus: opening a second closes the first", first != null && !GodotObject.IsInstanceValid(first) || first?.IsInsideTree() == false,
                $"first valid {first != null && GodotObject.IsInstanceValid(first)}");
            second?.QueueFree(); // the caller frees it
            await Wait(menu, 0.1);
            Check("API menus: freed by the caller, it lets go of the hotkeys", second != null && !PopupMenus.IsOpen && HotkeyBlockers() == blockers,
                $"opened {second != null}, blockers {HotkeyBlockers()}");

            var hidden = PopupMenus.Show(owner, Entries());
            owner.Visible = false;
            await Wait(menu, 0.1);
            Check("API menus: hiding its owner closes it within a frame", hidden != null && !PopupMenus.IsOpen && HotkeyBlockers() == blockers);
            owner.Visible = true;

            // One that fails as it's built (its rows throw): nothing of it stays, least of all its catcher over the screen.
            var (layers, holds) = (MenuLayers(menu), owner.GetSignalConnectionList(Node.SignalName.TreeExiting).Count);
            var threw = false;
            try
            {
                MenuPanel.Show(owner, () => throw new InvalidOperationException("a test menu whose rows throw"), keyboard: true);
            }
            catch (InvalidOperationException)
            {
                threw = true;
            }
            Check("API menus: one that fails as it's built leaves nothing behind (no layer over the screen, no hold on its owner or the hotkeys)",
                threw && MenuLayers(menu) == layers && !PopupMenus.IsOpen && owner.GetSignalConnectionList(Node.SignalName.TreeExiting).Count == holds
                && HotkeyBlockers() == blockers, $"threw {threw}, layers {layers} -> {MenuLayers(menu)}, blockers {HotkeyBlockers()}");
            // A caller without nullable checks: a null label is no words (a heading of none is the line), never a failure.
            var unnamed = new MenuEntry { OnSelect = () => { } };
            typeof(MenuEntry).GetProperty(nameof(MenuEntry.Label))?.SetValue(unnamed, null);
            var blank = typeof(MenuEntry).GetMethod(nameof(MenuEntry.Heading))?.Invoke(null, [null]) is MenuEntry nameless
                ? PopupMenus.Show(owner, [nameless, unnamed]) : null;
            Check("API menus: null labels (a caller without nullable checks) are no words, not a failure", blank != null && PopupMenus.IsOpen
                && PopupMenus.Rows(blank).SequenceEqual([("", false)]) && PopupMenus.Headings(blank).Count == 0);
            PopupMenus.Close();

            var (exits, opens) = (owner.GetSignalConnectionList(Node.SignalName.TreeExiting).Count, 0);
            for (var i = 0; i < 50; i++)
            {
                if (PopupMenus.Show(owner, Entries()) != null)
                    opens++;
                PopupMenus.Close();
            }
            Check("API menus: 50 opens on one owner leave its tree_exiting connections as they were (no leak)",
                opens == 50 && owner.GetSignalConnectionList(Node.SignalName.TreeExiting).Count == exits && HotkeyBlockers() == blockers,
                $"{opens} opened, {exits} -> {owner.GetSignalConnectionList(Node.SignalName.TreeExiting).Count}, blockers {HotkeyBlockers()}");

            var offThread = await Task.Run(() => PopupMenus.Show(owner, Entries()));
            Check("API menus: Show from a worker thread returns null and logs once", offThread == null && !PopupMenus.IsOpen && ApiGuard.HasLogged("PopupMenus.Show"));

            var dropped = PopupMenus.Show(owner, Entries(), new MenuOptions { From = new Rect2(40, 40, 100, 30) });
            await Wait(menu, 0.1);
            var under = (dropped?.FindChild("Panel", true, false) as Control)?.GetGlobalRect() ?? new Rect2();
            Check("API menus: From opens it off the rect: under it, from its left edge, where there's room", dropped != null
                && under.Position.Y >= 70 - 0.5f && Math.Abs(under.Position.X - 40) < 0.5f, $"{under}");
            owner.QueueFree();
            await Wait(menu, 0.1);
            Check("API menus: its owner freed, it closes and lets go of the hotkeys", dropped != null && !PopupMenus.IsOpen && !PopupMenus.Close()
                && HotkeyBlockers() == blockers, $"blockers {HotkeyBlockers()}");
        }
        finally
        {
            PopupMenus.Close();
            if (GodotObject.IsInstanceValid(owner))
                owner.QueueFree();
        }
    }

    static async Task TestApiBanners(NMainMenu menu)
    {
        var box = new VBoxContainer { Name = "SmmApiBannerBox", Position = new Vector2(100, 100), Size = new Vector2(600, 0) };
        menu.AddChild(box);
        try
        {
            var dismissed = 0;
            var text = string.Join(" ", Enumerable.Repeat("A long warning from another mod, long enough to wrap.", 6));
            var banner = Banners.Create(new BannerOptions { Text = text, Kind = BannerKind.Warning, OnDismiss = () => dismissed++, Name = "SmmApiTestBanner" });
            box.AddChild(banner);
            await Wait(menu, 0.2);
            var narrow = banner.Size.Y;
            box.Size = new Vector2(1700, 0);
            await Wait(menu, 0.2);
            var (wide, wideWidth) = (banner.Size.Y, banner.Size.X);
            box.Size = new Vector2(600, 0);
            await Wait(menu, 0.2);
            Check("API banners: in a 600-wide box a long text wraps and the banner grows; widened, it shrinks again; narrowed, it follows",
                narrow > wide && wide > 0 && wideWidth >= 1600 && banner.Size.X <= 600.5f && Math.Abs(banner.Size.Y - narrow) < 1,
                $"{narrow:0} high at 600, {wide:0} at 1700 ({wideWidth:0} wide), back to {banner.Size.X:0}x{banner.Size.Y:0}");
            Check("API banners: Name is set; Warning is the palette's warning", banner.Name == "SmmApiTestBanner"
                && (banner as PanelContainer)?.GetThemeStylebox("panel") is StyleBoxFlat { BgColor: var warning } && warning == Palette.Warning);
            if (banner.FindChild("Dismiss", true, false) is BaseButton dismiss)
                dismiss.EmitSignal(BaseButton.SignalName.Pressed);
            Check("API banners: Dismiss hides it and calls OnDismiss once", !banner.Visible && dismissed == 1, $"visible {banner.Visible}, dismissed {dismissed}");
            var problem = Banners.Create(new BannerOptions { Text = "A problem" });
            box.AddChild(problem);
            Check("API banners: Problem (the default) is the palette's problem colour, with no Dismiss without OnDismiss",
                (problem as PanelContainer)?.GetThemeStylebox("panel") is StyleBoxFlat { BgColor: var red } && red == Palette.Problem && problem.FindChild("Dismiss", true, false) == null);

            // In a MarginContainer it wraps inside the margins, never pushing the container wider; under a plain Control, in
            // the span its anchors and offsets give it.
            var margins = new MarginContainer { Name = "SmmApiBannerMargins" };
            margins.AddThemeConstantOverride("margin_left", 16);
            margins.AddThemeConstantOverride("margin_right", 16);
            var column = new VBoxContainer { Name = "SmmApiBannerColumn", Position = new Vector2(100, 300), Size = new Vector2(600, 0) };
            menu.AddChild(column);
            column.AddChild(margins);
            var inside = Banners.Create(new BannerOptions { Text = text });
            margins.AddChild(inside);
            var plain = new Control { Name = "SmmApiBannerPlain", Position = new Vector2(100, 600), Size = new Vector2(600, 200) };
            menu.AddChild(plain);
            var anchored = Banners.Create(new BannerOptions { Text = text });
            anchored.AnchorRight = 1;
            (anchored.OffsetLeft, anchored.OffsetRight) = (20, -20);
            plain.AddChild(anchored);
            await Wait(menu, 0.2);
            Check("API banners: in a MarginContainer it wraps inside the margins without widening it; under a plain Control, in its anchors' span",
                margins.Size.X <= 600.5f && inside.Size.X <= 568.5f && inside.Size.Y > BannerBar.Height + 4 && anchored.Size.X <= 560.5f
                && anchored.GetGlobalRect().End.X <= plain.GetGlobalRect().End.X - 19.5f,
                $"margins {margins.Size.X:0}, inside {inside.Size.X:0}x{inside.Size.Y:0}, anchored {anchored.Size.X:0}");
            column.QueueFree();
            plain.QueueFree();

            // A caller without nullable checks: made from nothing, the stand-in (a plain panel), logged.
            var standIn = typeof(Banners).GetMethod(nameof(Banners.Create))?.Invoke(null, [null]) as Control;
            Check("API banners: one that can't be made (null options) is a plain panel instead, logged", standIn is PanelContainer && ApiGuard.HasLogged("Banners.Create"));
            standIn?.Free();
        }
        finally
        {
            box.QueueFree();
        }
    }

    static void TestApiLooks()
    {
        var label = new Label();
        Fonts.Style(label, bold: true, 19, Palette.Gold);
        Check("API fonts: Regular and Bold are fonts; Style sets a label's font, size and colour", Fonts.Regular() != null && Fonts.Bold() != null
            && label.HasThemeFontOverride("font") && label.GetThemeFontSize("font_size") == 19 && label.GetThemeColor("font_color") == Palette.Gold);
        label.Free();
        // Asked again after a language change, the language's own font; Kreon again once it's back.
        var start = LocManager.Instance.Language;
        var kreon = Fonts.Regular().ResourcePath;
        if (FontManager.GetSubstituteFont("zhs", FontType.Regular) == null)
            Results.Add("SKIP API fonts in Chinese: the game has none of its own for it");
        else
        {
            string chinese, back;
            try
            {
                LocManager.Instance.SetLanguage("zhs");
                chinese = Fonts.Regular().ResourcePath;
            }
            finally
            {
                LocManager.Instance.SetLanguage(start);
            }
            back = Fonts.Regular().ResourcePath;
            Check("API fonts: asked again after a switch to Chinese, Regular is its font; switched back, Kreon again",
                chinese != kreon && back == kreon && kreon.Contains("kreon"), $"{kreon} / {chinese} / {back}");
        }
        Check("API palette: its colours are SMM's", Palette.Cream == Look.Cream && Palette.Gold == Look.Gold && Palette.Navy == Look.Navy
            && Palette.DeepNavy == Look.DeepNavy && Palette.Warning == Look.Warning && Palette.Problem == Look.Problem);
        const string missing = "res://SpireModManager/selftest/api_missing.png";
        Check("API resources: GameResources.Load on a missing path is null, noted once", GameResources.Load<Texture2D>(missing) == null
            && GameResources.Load<Texture2D>(missing) == null && ModEntry.MissingResources.Count(p => p == missing) == 1);
        const string kreonFile = "res://themes/kreon_regular_shared.tres";
        Check("API resources: asked for as another type, null but not missing: asked for as what it is, it still loads (for SMM too)",
            GameResources.Load<Texture2D>(kreonFile) == null && !ModEntry.MissingResources.Contains(kreonFile) && GameResources.Load<Font>(kreonFile) != null);
    }

    static async Task TestApiMods(NMainMenu menu)
    {
        Check("API mods: Version is the manifest's, null for a mod that isn't installed", Mods.Version(ModEntry.ModId) == ModEntry.Version
            && ModEntry.Version != null && Mods.Version("SmmApiNotInstalled") == null, Mods.Version(ModEntry.ModId) ?? "null");
        // SMM's own entry has an alias, notes and a category in the fixture; tags, padded and twice, for the moment.
        var info = Store.InfoFor(ModEntry.CopyOf(ModEntry.ModId));
        var keptTags = info?.Tags;
        ModNotes details;
        try
        {
            if (info != null)
                info.Tags = [" Beta ", "beta", " ", "Gamma"];
            details = Mods.Details(ModEntry.ModId);
        }
        finally
        {
            if (info != null)
                info.Tags = keptTags;
        }
        var none = Mods.Details("SmmApiNotInstalled");
        Check("API mods: Details is what the player keeps (alias, notes, category, trimmed; tags once each, no blanks); empty for a mod with nothing",
            info is { Alias: { } alias, Notes: { } notes, Category: { } category } && details.Alias == alias.Trim() && details.Notes == notes.Trim()
            && details.Category == category.Trim() && details.Tags.SequenceEqual(["Beta", "Gamma"])
            && none is { Alias: null, Notes: null, Category: null, Tags.Count: 0 },
            $"{details.Alias} / {details.Notes} / {details.Category} / {string.Join(",", details.Tags)}");
        var described = ModDetails.Described("[blue]Frost[/blue] [sine]waves[/sine] [b]" + string.Join(" ", Enumerable.Repeat("bold words", 40)), 60);
        Check("API cards: a description's game tags are drawn as the game's (colours; moving ones as their text), and what a cut leaves open is closed",
            described.StartsWith("[color=#87ceeb]Frost[/color] waves [b]bold") && described.EndsWith("…[/b]") && !described.Contains("sine"), described);

        var ours = ModCards.Text(ModEntry.ModId) ?? "";
        var ourAlias = Store.InfoFor(ModEntry.ModId)?.Alias;
        Check("API cards: Text has the mod's own name and version, its category, never the player's alias",
            ours.Contains("Spire Mod Manager") && ModEntry.Version is { } v && ours.Contains(v) && ours.Contains(Menus.Text("SPIRE_MOD_MANAGER.CATEGORY"))
            && (ourAlias == null || !ours.Contains(ourAlias)), ours.Length > 300 ? ours[..300] : ours);
        Check("API cards: no card for a mod that isn't installed", ModCards.Text("SmmApiNotInstalled") == null);

        var trigger = new Label { Name = "SmmApiCardTrigger", Text = "card", Position = new Vector2(300, 300), Size = new Vector2(200, 30) };
        menu.AddChild(trigger);
        var links = new RichTextLabel { Name = "SmmApiCardLinks", BbcodeEnabled = true, Text = "[url=mod:SmmTestLib]Lib[/url]", Position = new Vector2(300, 400), Size = new Vector2(200, 40) };
        menu.AddChild(links);
        try
        {
            ModCards.Watch(trigger, ModEntry.ModId);
            var hoverable = trigger.MouseFilter == Control.MouseFilterEnum.Pass; // a Label ignores the mouse until then
            var card = await HoverCard(menu, trigger, SharedCards.CardName);
            ModCards.Watch(trigger, "SmmTestLib");
            var repointed = await HoverCard(menu, trigger, SharedCards.CardName);
            Check("API cards: Watch makes a Label hoverable and shows the mod's card on hover; a second Watch points it at another mod",
                hoverable && card.Contains("Spire Mod Manager") && card.Contains("symphony") && repointed.Contains("Test Framework") && !repointed.Contains("symphony"),
                $"{(card.Length > 80 ? card[..80] : card)} / {(repointed.Length > 80 ? repointed[..80] : repointed)}");

            var underlined = links.MetaUnderlined;
            ModCards.WatchLinks(links, meta => meta.StartsWith("mod:") ? meta[4..] : null);
            var linked = await LinkCard(menu, links, "mod:SmmTestLib");
            Check("API cards: WatchLinks shows the card of the mod a link names, and leaves the label's look as it was",
                linked.Contains("Test Framework") && links.MetaUnderlined == underlined, linked.Length > 80 ? linked[..80] : linked);
            var handlers = links.GetSignalConnectionList(RichTextLabel.SignalName.MetaHoverStarted).Count;
            ModCards.WatchLinks(links, meta => meta.StartsWith("mod:") ? ModEntry.ModId : null); // every link: SMM's card
            var relinked = await LinkCard(menu, links, "mod:SmmTestLib");
            Check("API cards: WatchLinks again on the same label swaps the mapping, adding no handlers",
                links.GetSignalConnectionList(RichTextLabel.SignalName.MetaHoverStarted).Count == handlers && relinked.Contains("symphony")
                && !relinked.Contains("Test Framework"), $"{handlers} handlers -> {links.GetSignalConnectionList(RichTextLabel.SignalName.MetaHoverStarted).Count}");
            ModCards.WatchLinks(links, meta => throw new InvalidOperationException("a test mapping that throws"));
            var failed = await LinkCard(menu, links, "mod:SmmTestLib");
            Check("API cards: a WatchLinks mapping that throws shows no card, and is logged naming the mod", failed.Length == 0
                && ApiGuard.HasLogged($"a ModCards.WatchLinks mapping from {typeof(SelfTest).Assembly.GetName().Name}"), failed);
        }
        finally
        {
            Hover.Hide();
            trigger.QueueFree();
            links.QueueFree();
        }
    }

    // The card a link shows once the pointer has rested on it ("" for none); hidden again after.
    static async Task<string> LinkCard(Node menu, RichTextLabel links, string meta)
    {
        Hover.TestMouse = () => links.GetGlobalRect().GetCenter();
        links.EmitSignal(RichTextLabel.SignalName.MetaHoverStarted, meta);
        await WaitReal(menu, Hover.Delay + 0.3); // the link card's delay
        var shown = menu.GetTree().Root.FindChild(SharedCards.CardName, true, false)?.FindChildren("*", "RichTextLabel", true, false)
            .OfType<RichTextLabel>().FirstOrDefault()?.GetParsedText() ?? "";
        links.EmitSignal(RichTextLabel.SignalName.MetaHoverEnded, meta);
        Hover.TestMouse = null;
        Hover.Hide();
        return shown;
    }
}
