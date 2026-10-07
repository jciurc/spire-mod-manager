using Godot;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;
using MegaCrit.Sts2.addons.mega_text;

namespace SpireModManager;

// Where everything sits in each view.
// - Categories: a wide mod list and info panel, smaller text, our own title, plain panels without the
//   blue border, no Make/Get Mods. Visibly not the vanilla page.
// - Load Order: the list alone, centred; no info panel.
// - Vanilla: the game's own positions, sizes, title and panels (saved the first time we change them),
//   with just a "Spire Manager" button in the lower-left corner to come back.
static class Layout
{
    public enum View { Categories, LoadOrder, Vanilla }

    const string OriginalMeta = "spire_mod_manager_original";
    const string StyleMeta = "spire_mod_manager_original_style";
    const string FocusMeta = "spire_mod_manager_focus_release";
    const string FontMeta = "spire_mod_manager_original_font";

    // Screen-space boxes (left, top, right, bottom) in the game's 1920x1080 layout.
    static readonly Vector4 ListBox = new(175, 80, 1215, 990);
    static readonly Vector4 InfoBox = new(1290, 80, 1900, 990); // clear of the list's scrollbar (list right + 6..54)
    static readonly Vector4 CentredListBox = new(400, 80, 1500, 990);
    public const float BottomRowY = 999; // top of the 70px-tall buttons along the bottom

    // Text sizes in our views (the game's in brackets): rows 20 (32), info title 32 (60), info text 18 (24).
    public const int RowTitleSize = 20, RowTitleVanillaSize = 32;
    const int InfoTitleSize = 32, InfoTextSize = 18, PageTitleSize = 34;

    public static View Current => Store.Data.VanillaView ? View.Vanilla : Store.Data.LoadOrderView ? View.LoadOrder : View.Categories;
    public static bool Wide => Current != View.Vanilla;

    public static void Apply(NModdingScreen screen)
    {
        var view = Current;
        var list = screen.GetNode<Control>("ModsBorder");
        var info = screen.GetNode<Control>("%ModInfoContainer");
        Place(list, view == View.LoadOrder ? CentredListBox : ListBox);
        Place(info, InfoBox);
        info.Visible = view != View.LoadOrder;

        // Page title: ours, smaller, top-left; the game's in Vanilla View.
        var title = list.GetNode<MegaRichTextLabel>("%InstalledModsTitle");
        Place(title, new Vector4(0, -66, 700, -14));
        ViewFont(title, PageTitleSize);
        title.SetTextAutoSize(new LocString("settings_ui", Wide ? "SPIRE_MOD_MANAGER.PAGE_TITLE" : "MODDING_SCREEN.INSTALLED_MODS_TITLE").GetFormattedText());
        foreach (var name in new[] { "%MakeModsButton", "%GetModsButton" })
            list.GetNode<Control>(name).Visible = !Wide;

        // Plain dark panels without the blue border in our views.
        PanelStyle(list);
        PanelStyle(info);

        // Inside the info panel: title, image and description, fitted to its new size.
        var width = info.Size.X;
        // Title and link (left) beside a thumbnail of the mod image (right); description below; the
        // player's tags and notes and Open Folder along the bottom (ModDetails).
        Place(info.GetNode<Control>("ModTitle"), new Vector4(20, 10, width - 230, 52));
        Place(info.GetNode<Control>("ModImage"), new Vector4(width - 210, 12, width - 20, 118));
        Place(info.GetNode<Control>("ModDescription"), new Vector4(22, 128, width - 22,
            info.Size.Y - ModDetails.ButtonsFromBottom - ModDetails.YoursHeight - 8));
        ViewFont(info.GetNode<MegaRichTextLabel>("ModTitle"), InfoTitleSize);
        ViewFont(info.GetNode<MegaRichTextLabel>("ModDescription"), InfoTextSize);

        var scroll = list.GetNode<Control>("ModsScrollContainer");

        // The scene's mask clips drawing only (clip_children): rows scrolled out of view still took clicks
        // above and below the list. Invisible blockers just outside the mask catch those clicks first.
        // (ClipContents on the mask breaks its drawing.) The profile bar and search sit above them.
        var mask = scroll.GetNode<Control>("Mask");
        if (mask.GetNodeOrNull("SpireModManagerBlockTop") == null)
            foreach (var (name, top) in new[] { ("SpireModManagerBlockTop", true), ("SpireModManagerBlockBottom", false) })
            {
                var blocker = new Control { Name = name, MouseFilter = Control.MouseFilterEnum.Stop };
                mask.AddChild(blocker);
                (blocker.AnchorTop, blocker.AnchorBottom, blocker.AnchorRight) = (top ? 0 : 1, top ? 0 : 1, 1);
                (blocker.OffsetTop, blocker.OffsetBottom) = top ? (-2000, 0) : (0, 2000);
            }

        // A click on an empty part of the screen or list lets go of keyboard focus (search, rename boxes).
        if (!screen.HasMeta(FocusMeta))
        {
            screen.SetMeta(FocusMeta, true);
            void Release(InputEvent e)
            {
                if (e is InputEventMouseButton { Pressed: true })
                    screen.GetViewport().GuiReleaseFocus();
            }
            screen.GuiInput += Release;
            scroll.GuiInput += Release;
        }

        // Profile bar and search sit at the top of the list in our views; Vanilla View hides them and gives
        // the list its full height back.
        if (!scroll.HasMeta(OriginalMeta))
            scroll.SetMeta(OriginalMeta, scroll.OffsetTop);
        var bar = list.GetNodeOrNull<Control>("SpireModManagerProfileBar");
        var search = list.GetNodeOrNull<Control>("SpireModManagerSearch");
        foreach (var top in new[] { bar, search })
            if (top != null)
                top.Visible = Wide;
        scroll.OffsetTop = Wide && (search ?? bar) is { } lowest ? lowest.OffsetBottom + 6 : (float)scroll.GetMeta(OriginalMeta);

        // Bottom row. Categories: Vanilla Manager and Load Order on the left; Undo/Redo/Changes and Relaunch
        // on the right. Load Order: Back on the left, the same right side. Vanilla: only "Spire Manager",
        // in the corner where the main menu button sits.
        var left = list.OffsetLeft;
        var vanillaToggle = screen.GetNodeOrNull<Control>("SpireModManagerViewToggle");
        var orderToggle = screen.GetNodeOrNull<Control>("SpireModManagerLoadOrderToggle");
        if (vanillaToggle != null)
        {
            vanillaToggle.Visible = view != View.LoadOrder;
            vanillaToggle.Position = view == View.Vanilla ? new Vector2(18, 1080 - 16 - vanillaToggle.Size.Y) : new Vector2(left, BottomRowY);
        }
        if (orderToggle != null)
        {
            orderToggle.Visible = view != View.Vanilla;
            orderToggle.Position = new Vector2(view == View.LoadOrder ? left : left + 210, BottomRowY);
        }
        if (screen.GetNodeOrNull<Control>("SpireModManagerBmmNotice") is { } notice)
            notice.Position = new Vector2(left, BottomRowY + 1);

        var right = view == View.LoadOrder ? list.OffsetRight : info.OffsetRight;
        if (screen.GetNodeOrNull<Control>("SpireModManagerRelaunchButton") is { } relaunch)
        {
            relaunch.Visible = Wide;
            relaunch.Position = new Vector2(right - relaunch.Size.X, BottomRowY);
            right = relaunch.Position.X - 12;
        }
        if (screen.GetNodeOrNull<Control>(HistoryBar.BarName) is { } history)
        {
            history.Position = new Vector2(right - history.Size.X, BottomRowY);
            history.Visible = Wide;
            right = history.Position.X - 12;
        }

        // "Changes +4 -3": above the info panel's left edge; in Load Order, above the list's right end.
        if (screen.GetNodeOrNull<Control>(HistoryBar.ChangesName) is { } changes)
        {
            changes.Visible = Wide;
            changes.Position = view == View.LoadOrder
                ? new Vector2(list.OffsetRight - changes.Size.X, list.OffsetTop - 34)
                : new Vector2(info.OffsetLeft + 4, info.OffsetTop - 34);
        }
        History.NotifyChanged(); // the buttons act on this view's history

        // The game's "takes effect after a restart" line fits between the left and right buttons in our
        // views (its text shrinks to fit), and goes back to its own place in Vanilla View.
        var leftEnd = new[] { vanillaToggle, orderToggle }.Where(c => c is { Visible: true })
            .Select(c => c is { } button ? button.Position.X + button.Size.X : left).DefaultIfEmpty(left).Max() + 12;
        Place(screen.GetNode<Control>("%PendingChangesLabel"), new Vector4(leftEnd, 1000, right, 1068));
        UpdateDot(screen);
    }

    // Vanilla View: the game's notification dot on "Spire Manager" while a relaunch is pending.
    public static void UpdateDot(Node screen)
    {
        if (screen.GetNodeOrNull<Control>("SpireModManagerViewToggle/Dot") is { } dot)
            dot.Visible = Current == View.Vanilla && ModEntry.HasPendingChanges();
    }

    // Wide: the given box. Vanilla: the box the scene had.
    static void Place(Control control, Vector4 wide)
    {
        if (!control.HasMeta(OriginalMeta))
            control.SetMeta(OriginalMeta, new Vector4(control.OffsetLeft, control.OffsetTop, control.OffsetRight, control.OffsetBottom));
        var box = Wide ? wide : (Vector4)control.GetMeta(OriginalMeta);
        (control.OffsetLeft, control.OffsetTop, control.OffsetRight, control.OffsetBottom) = (box.X, box.Y, box.Z, box.W);
    }

    static void PanelStyle(Control panel)
    {
        if (!panel.HasMeta(StyleMeta))
            panel.SetMeta(StyleMeta, panel.GetThemeStylebox("panel"));
        if (!Wide)
        {
            panel.AddThemeStyleboxOverride("panel", (StyleBox)panel.GetMeta(StyleMeta));
            return;
        }
        var plain = new StyleBoxFlat { BgColor = new Color(0.03f, 0.05f, 0.08f, 0.55f) };
        plain.SetCornerRadiusAll(6);
        panel.AddThemeStyleboxOverride("panel", plain);
    }

    // Our size in our views; in Vanilla View whatever the scene had (its font size and auto-size maximum,
    // saved the first time).
    static void ViewFont(MegaRichTextLabel label, int size)
    {
        if (!label.HasMeta(FontMeta))
            label.SetMeta(FontMeta, new Vector2I(label.GetThemeFontSize("normal_font_size"), label.MaxFontSize));
        var original = (Vector2I)label.GetMeta(FontMeta);
        if (Wide)
        {
            FontSize(label, size);
            return;
        }
        label.AddThemeFontSizeOverride("normal_font_size", original.X);
        label.AddThemeFontSizeOverride("bold_font_size", original.X);
        label.MaxFontSize = original.Y;
        label.SetTextAutoSize(label.Text);
    }

    public static void FontSize(MegaRichTextLabel label, int size)
    {
        label.AddThemeFontSizeOverride("normal_font_size", size);
        label.AddThemeFontSizeOverride("bold_font_size", size);
        label.MaxFontSize = size;
        label.SetTextAutoSize(label.Text);
    }
}
