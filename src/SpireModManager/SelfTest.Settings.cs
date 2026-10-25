using Godot;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;
using MegaCrit.Sts2.Core.Nodes.Screens.ProfileScreen;
using BaseRegistry = SmmTest.BaseLib.Config.ModConfigRegistry;
using BaseConfig = SmmTest.BaseLib.Config.TestConfig;
using RitsuPage = SmmTest.STS2RitsuLib.Settings.ModSettingsPage;
using RitsuRegistry = SmmTest.STS2RitsuLib.Settings.ModSettingsRegistry;
using Navigator = SmmTest.STS2RitsuLib.Settings.ModSettingsNavigator;

namespace SpireModManager;

// The self-test's checks of the info panel's Settings button (SettingsLink), against stand-ins of RitsuLib's and
// BaseLib's registries (SelfTest.SettingsStandIns.cs).
static partial class SelfTest
{
    // Which page a mod's button opens (made-up mods, then ours): its own RitsuLib page, then its BaseLib page, then a
    // RitsuLib page only matched by id; pages RitsuLib wouldn't list, or mirrors, never. Then on the panel: its place and
    // tip, BaseLib's submenu over our page and back with the page as it was (or read again where the mod list changed),
    // RitsuLib's overlay with the page's keys waiting, a page that didn't open (greyed), one that's gone, and a mod with
    // none (greyed, saying so: the button always shows).
    static async Task TestSettingsButton(Node menu, NModdingScreen screen, Control content, string outDir)
    {
        NModMenuRow? Row(string id) => content.GetChildren().OfType<NModMenuRow>().FirstOrDefault(r => r.Mod?.manifest?.id == id);
        if (Row(ModEntry.ModId) is not { Mod: { } self } selfRow || screen.GetParent() is not NSubmenuStack stack)
        {
            Results.Add("SKIP the Settings button: our own row or the main menu's screens not there");
            return;
        }
        var ours = typeof(SelfTest).Assembly;
        static Mod Made(string id) => new()
        {
            path = "", modSource = ModSource.ModsDirectory, state = ModLoadState.Loaded,
            manifest = new ModManifest { id = id, name = "Settings Test", author = "Someone Careful" },
        };
        static string Said(SettingsLink.Target? t) => t == null ? "none" : $"{t.Library} {t.Key} {t.PageId}";
        var info = screen.GetNode<NModInfoContainer>("%ModInfoContainer");
        var shownBefore = CategoryList.Selected;
        var (history, listWas) = (History.TestKeep(), History.CurrentMods());
        SettingsStandIns.Patch();
        SettingsLink.TestPrefix = "SmmTest.";
        SettingsLink.TestReset();
        try
        {
            // Which page wins, for a made-up mod whose code is "ours" (TestOwner).
            var made = Made("SmmSettingsMade");
            SettingsLink.TestOwner = a => a == ours ? made : null;
            Check("Settings: none for a mod no library keeps a page for", SettingsLink.For(made) == null, Said(SettingsLink.For(made)));
            RitsuRegistry.Register(new RitsuPage { ModId = "smmsettingsmade", Id = "by-id" }); // by id only (no code of its)
            Check("Settings: a RitsuLib page under its id (any case) when it's the only one, found without a fresh start",
                Said(SettingsLink.For(made)) == "RitsuLib smmsettingsmade by-id", Said(SettingsLink.For(made)));
            BaseRegistry.Register("Its Own Key", new BaseConfig("Its Own Key")); // a key of its own, its code's (Alchemist's)
            Check("Settings: its BaseLib page (a key of its own, found by its code) wins over a RitsuLib page only under its id",
                Said(SettingsLink.For(made)) == "BaseLib Its Own Key ", Said(SettingsLink.For(made)));
            RitsuRegistry.Register(new RitsuPage { ModId = "Some Id", Id = "own", SourceAssembly = ours });
            Check("Settings: its own RitsuLib page (its code registered it) wins over its BaseLib page",
                Said(SettingsLink.For(made)) == "RitsuLib Some Id own", Said(SettingsLink.For(made)));

            // Pages RitsuLib wouldn't show on the main menu, and its mirrors of other libraries' pages: never.
            (string What, RitsuPage Page, bool Counts)[] pages =
            [
                ("a mirror of BaseLib's pages", new RitsuPage { ModId = "x", Id = "baselib", SourceAssembly = ours }, false),
                ("a mirror of ModConfig's pages", new RitsuPage { ModId = "x", Id = "modconfig", SourceAssembly = ours }, false),
                ("a mirror of JmcModLib's pages", new RitsuPage { ModId = "x", Id = "jmcmodlib-pages", SourceAssembly = ours }, false),
                ("a page under another", new RitsuPage { ModId = "x", Id = "sub", ParentPageId = "top", SourceAssembly = ours }, false),
                ("a page only shown in a run", new RitsuPage { ModId = "x", Id = "run", VisibleOnHostSurfaces = 2, SourceAssembly = ours }, false),
                ("an empty page", new RitsuPage { ModId = "x", Id = "empty", Sections = [], SourceAssembly = ours }, false),
                ("a page its condition hides", new RitsuPage { ModId = "x", Id = "hidden", VisibleWhen = () => false, SourceAssembly = ours }, false),
                ("a page whose condition throws (RitsuLib shows it)", new RitsuPage { ModId = "x", Id = "throws",
                    VisibleWhen = () => throw new InvalidOperationException("made up"), SourceAssembly = ours }, true),
            ];
            foreach (var (what, page, counts) in pages)
            {
                RitsuRegistry.Clear();
                BaseRegistry.Clear();
                RitsuRegistry.Register(page);
                SettingsLink.TestReset();
                Check($"Settings: {what} {(counts ? "counts" : "never counts")}", (SettingsLink.For(made) != null) == counts, Said(SettingsLink.For(made)));
            }
            RitsuRegistry.Clear();
            BaseRegistry.Register("SmmSettingsMade", new BaseConfig("SmmSettingsMade", listed: false));
            SettingsLink.TestReset();
            Check("Settings: a BaseLib page it doesn't list never counts", SettingsLink.For(made) == null, Said(SettingsLink.For(made)));
            var notLoaded = Made("SmmSettingsOff");
            notLoaded.state = ModLoadState.Disabled;
            BaseRegistry.Register("SmmSettingsOff", new BaseConfig("SmmSettingsOff"));
            SettingsLink.TestReset();
            Check("Settings: none for a copy that didn't load this session", SettingsLink.For(notLoaded) == null, Said(SettingsLink.For(notLoaded)));
            SettingsLink.TestOwner = null;
            BaseRegistry.Clear();
            SettingsLink.TestReset();

            // On the panel: ours, with no page first (greyed, saying so), then a BaseLib page.
            CategoryList.Select(screen, selfRow);
            await PanelFor(menu, screen, selfRow);
            await Wait(menu, 0.3);
            Control? Button() => info.GetNodeOrNull<Control>(ModDetails.SettingsName);
            var none = Button() is { } noneButton ? (await SizedHoverCard(menu, noneButton, Hover.TipName, () => Capture(screen, outDir, "17a2-settings-none"))).Text : "";
            Check("Settings: a mod with none: the button greyed, its tip saying none were found", NoSettings(Button()) && none == NoneTip,
                $"shown {Button()?.Visible}, greyed {Greyed(Button())}, tip {(Button() is { } noneTipped ? Hover.TipOf(noneTipped) : "no button")}, card \"{none}\"");
            BaseRegistry.Register(ModEntry.ModId, new BaseConfig(ModEntry.ModId));
            info.Fill(self);
            await Wait(menu, 0.3);
            var folder = info.GetNode<Control>("OpenFolderButton");
            var button = Button();
            Check("Settings: the button right of Open Folder, its tip naming the library", button is { Visible: true } && !Greyed(button) && folder.Visible
                && button.OffsetLeft == folder.OffsetRight + 12 && !button.GetGlobalRect().Intersects(folder.GetGlobalRect())
                && Hover.TipOf(button) == "Change this mod's settings (BaseLib)",
                $"button {button?.Visible} at {button?.OffsetLeft}, folder {folder.Visible} to {folder.OffsetRight}; tip {(button == null ? "" : Hover.TipOf(button))}");
            await Capture(screen, outDir, "17a-settings-button");
            void Press()
            {
                if (Button() is NButton pressed)
                    Click(pressed);
                else if (Button() is BaseButton plain)
                    Click(plain);
            }

            // A change to undo first (a fixture mod's tick), so a fresh visit would show.
            var fixture = content.GetChildren().OfType<NModMenuRow>().FirstOrDefault(r => r.Mod?.manifest?.id is { } id && id.StartsWith("SmmTest"));
            void Tick()
            {
                if (fixture?.GetNodeOrNull<NTickbox>("Tickbox") is not { } box)
                    return;
                box.IsTicked = !box.IsTicked;
                box.EmitSignal(NTickbox.SignalName.Toggled, box);
            }
            Tick();
            await Wait(menu, 0.3);
            var (changes, entered) = (HistoryBar.Describe(), History.TestEnters);
            Press();
            await Wait(menu, 0.3);
            Check("Settings: BaseLib's submenu over our page, on this mod", stack.Peek() is NProfileScreen && !screen.Visible
                && SmmTest.BaseLib.Config.BaseLibConfig.LastModConfigModId == ModEntry.ModId,
                $"on top {stack.Peek()?.GetType().Name}, our page shown {screen.Visible}, its mod {SmmTest.BaseLib.Config.BaseLibConfig.LastModConfigModId}");
            stack.Pop();
            await Wait(menu, 0.3);
            var undoable = fixture == null || History.CanUndo;
            Check("Settings: back from BaseLib's submenu, the page as it was: the same visit (the change still to undo), the same mod shown",
                screen.Visible && History.TestEnters == entered && undoable && HistoryBar.Describe().SequenceEqual(changes)
                && CategoryList.Selected == self && Button() is { Visible: true },
                $"shown {screen.Visible}, entered anew {History.TestEnters != entered}, can undo {History.CanUndo}, changes {string.Join(" | ", HistoryBar.Describe())}, "
                + $"were {string.Join(" | ", changes)}; shown {CategoryList.Selected?.manifest?.id}");
            if (fixture != null)
            {
                // The mod list changed while it was covered (here by its tick undone by hand): read again, as a fresh visit.
                Press();
                await Wait(menu, 0.3);
                Tick();
                stack.Pop();
                await Wait(menu, 0.3);
                Check("Settings: back from a screen that changed the mod list, the page reads it again (a fresh visit)",
                    screen.Visible && History.TestEnters != entered && !History.CanUndo && History.CurrentMods().SequenceEqual(listWas),
                    $"entered anew {History.TestEnters != entered}, can undo {History.CanUndo}, list as at the start {History.CurrentMods().SequenceEqual(listWas)}");
            }
            else
                Results.Add("SKIP Settings: the mod list changed under it: no fixture mod");

            // Gone since the panel filled (its library forgot it): a press greys it as for a mod with none, and nothing opens.
            BaseRegistry.Clear();
            Press();
            await Wait(menu, 0.3);
            Check("Settings: a page gone since the panel filled: a press greys the button, saying none were found", NoSettings(Button())
                && stack.Peek() == screen, $"greyed {Greyed(Button())}, on top {stack.Peek()?.GetType().Name}");

            // RitsuLib's own page for ours: its overlay over our page, which stays shown but takes no keys.
            RitsuRegistry.Register(new RitsuPage { ModId = ModEntry.ModId, Id = "main", SourceAssembly = ours });
            info.Fill(self);
            await Wait(menu, 0.3);
            Check("Settings: RitsuLib's page, its tip naming it", Button() is { Visible: true } b && Hover.TipOf(b) == "Change this mod's settings (RitsuLib)",
                Button() is { } tipped ? Hover.TipOf(tipped) : "no button");
            entered = History.TestEnters;
            Press();
            await Wait(menu, 0.3);
            Check("Settings: RitsuLib's overlay opened on its page, our page under it taking no keys",
                Navigator.Asked == (ModEntry.ModId, "main") && screen.IsVisibleInTree() && !PageVisits.OnTop(screen),
                $"asked {Navigator.Asked}, shown {screen.IsVisibleInTree()}, on top {PageVisits.OnTop(screen)}");
            Navigator.OverlayOpen = false;
            await Wait(menu, 0.3);
            Check("Settings: the overlay closed, our page takes keys again, the same visit", PageVisits.OnTop(screen) && History.TestEnters == entered,
                $"on top {PageVisits.OnTop(screen)}, entered anew {History.TestEnters != entered}");
            if (fixture != null)
            {
                Press();
                await Wait(menu, 0.3);
                Tick();
                await Wait(menu, 0.1);
                Navigator.OverlayOpen = false;
                await Wait(menu, 0.3);
                var changed = History.TestEnters != entered;
                Tick(); // as it was
                await Wait(menu, 0.3);
                Check("Settings: the overlay closed after the mod list changed under it: the page reads it again", changed,
                    $"entered anew {changed}");
            }

            // Didn't open: greyed, its tip pointing at the log, for the session. Gone: greyed as for a mod with none.
            Navigator.Clear();
            Navigator.Answer = "exception";
            Press();
            await Wait(menu, 0.3);
            var greyed = Greyed(Button());
            Check("Settings: a page that didn't open: the button greyed, its tip saying the log has why", Button() is { Visible: true } failed
                && greyed && Hover.TipOf(failed) == "Settings page couldn't be opened - see game log for more details" && SettingsLink.FailedFor(self),
                $"greyed {greyed}, tip {(Button() is { } t ? Hover.TipOf(t) : "no button")}");
            SettingsLink.TestReset();
            info.Fill(self);
            await Wait(menu, 0.3);
            Navigator.Answer = "page-not-found";
            Press();
            await Wait(menu, 0.3);
            Check("Settings: a page RitsuLib no longer has: greyed, saying none were found", NoSettings(Button()),
                $"greyed {Greyed(Button())}, tip {(Button() is { } goneTipped ? Hover.TipOf(goneTipped) : "no button")}");
        }
        finally
        {
            SettingsStandIns.Unpatch();
            SettingsLink.TestPrefix = "";
            SettingsLink.TestOwner = null;
            SettingsLink.TestReset();
            while (stack.Peek() is { } top && top != screen && top is NProfileScreen)
                stack.Pop();
            History.TestPutBack(history);
            CategoryList.Selected = shownBefore;
            info.Fill(shownBefore ?? self);
            await Wait(menu, 0.3);
        }
        Check("Settings: with no library there, greyed, saying none were found", NoSettings(Shown(info)) && History.CurrentMods().SequenceEqual(listWas),
            $"greyed {Greyed(Shown(info))}, list as at the start {History.CurrentMods().SequenceEqual(listWas)}");
        static Control? Shown(Control info) => info.GetNodeOrNull<Control>(ModDetails.SettingsName);
    }

    const string NoneTip = "No settings were found for this mod";

    static bool Greyed(Control? button) => button is NClickableControl clickable ? !clickable.IsEnabled && clickable.Modulate == StsColors.gray
        : button is BaseButton { Disabled: true };

    // Shown, greyed, its tip saying none were found (Jordan, 24 Oct: the button always shows).
    static bool NoSettings(Control? button) => button is { Visible: true } && Greyed(button) && Hover.TipOf(button) == NoneTip;
}
