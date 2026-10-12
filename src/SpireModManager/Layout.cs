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
    const string WatermarkMeta = "spire_mod_manager_watermark";
    const string MinSizeMeta = "spire_mod_manager_min_size";

    // Screen-space boxes (left, top, right, bottom) in the game's 1920x1080 layout.
    // The panels sit midway between the page title and the bottom row of buttons.
    static readonly Vector4 ListBox = new(175, 72, 1080, 984);
    static readonly Vector4 InfoBox = new(1155, 72, 1900, 984); // clear of the list's scrollbar (list right + 6..54)
    static readonly Vector4 CentredListBox = new(400, 72, 1500, 984);
    public const float BottomRowY = 999; // top of the 70px-tall buttons along the bottom
    public const float CornerX = 18; // the lower-left corner button (here and on the main menu)
    const float PageLeft = 175, TitleTop = 10, TitleBottom = 62;

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
        var listBox = view == View.LoadOrder ? CentredListBox : ListBox;
        Place(list, listBox);
        Place(info, InfoBox);
        info.Visible = view != View.LoadOrder;

        // Page title: ours, smaller, top-left at the same spot in both our views (it belongs to the list, so
        // it's placed relative to it); the game's in Vanilla View.
        var title = list.GetNode<MegaRichTextLabel>("%InstalledModsTitle");
        var dx = PageLeft - listBox.X;
        Place(title, new Vector4(dx, TitleTop - listBox.Y, dx + 700, TitleBottom - listBox.Y));
        ViewFont(title, PageTitleSize);
        var titleText = new LocString("settings_ui", Wide ? "SPIRE_MOD_MANAGER.PAGE_TITLE" : "MODDING_SCREEN.INSTALLED_MODS_TITLE").GetFormattedText();
        title.SetTextAutoSize(titleText);

        // Load Order: its name centred over the list.
        var viewTitle = screen.GetNodeOrNull<Label>("SpireModManagerViewTitle") ?? AddViewTitle(screen);
        viewTitle.Visible = view == View.LoadOrder;
        viewTitle.Position = new Vector2((listBox.X + listBox.Z) / 2 - viewTitle.Size.X / 2, TitleTop + 8);
        foreach (var name in new[] { "%MakeModsButton", "%GetModsButton" })
            list.GetNode<Control>(name).Visible = !Wide;

        // Plain dark panels without the blue border in our views.
        PanelStyle(list);
        PanelStyle(info);

        // Inside the info panel: title, image and description, fitted to its new size.
        var width = info.Size.X;
        // Title and link (left) beside a thumbnail of the mod image (right); description below; the
        // player's tags and notes and Open Folder along the bottom (ModDetails).
        const float thumbWidth = 128, thumbHeight = 72; // 16:9, small: the title and text matter more
        var inset = ModDetails.Inset;
        Place(info.GetNode<Control>("ModTitle"), new Vector4(inset, 10, width - thumbWidth - 40, 52));
        // The scene gives the image a 500x380 minimum size, which beats any smaller box: lifted in our views.
        var image = info.GetNode<Control>("ModImage");
        if (!image.HasMeta(MinSizeMeta))
            image.SetMeta(MinSizeMeta, image.CustomMinimumSize);
        image.CustomMinimumSize = Wide ? Vector2.Zero : (Vector2)image.GetMeta(MinSizeMeta);
        Place(image, new Vector4(width - thumbWidth - 20, 16, width - 20, 16 + thumbHeight));
        Place(info.GetNode<Control>("ModDescription"), new Vector4(inset, 100, width - inset,
            info.Size.Y - ModDetails.ButtonsFromBottom - ModDetails.YoursHeight - ModDetails.FieldsGap));
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
            foreach (var area in new[] { screen, scroll, list, info })
                area.GuiInput += Release; // blank space anywhere: page, list, its header, the info panel
        }

        PlaceList(list);

        // Bottom row, left, from the lower-left corner (where the main menu button sits): "Vanilla List" then
        // "Load Order" on the main list; "Spire Manager" alone in Vanilla View; "Return" alone in Load Order.
        // Right: Changes, Undo/Redo/Revert, then Relaunch (main list only; Load Order has no Relaunch and no
        // restart line).
        var vanillaToggle = screen.GetNodeOrNull<Control>("SpireModManagerViewToggle");
        var orderToggle = screen.GetNodeOrNull<Control>("SpireModManagerLoadOrderToggle");
        var leftEnd = PageLeft;
        var x = CornerX;
        foreach (var (toggle, shown) in new[] { (vanillaToggle, view != View.LoadOrder), (orderToggle, view != View.Vanilla) })
        {
            if (toggle == null)
                continue;
            toggle.Visible = shown;
            toggle.Position = new Vector2(x, BottomRowY);
            if (shown)
            {
                leftEnd = x + toggle.Size.X;
                x = leftEnd + 12;
            }
        }
        if (screen.GetNodeOrNull<Control>("SpireModManagerBmmNotice") is { } notice)
            notice.Position = new Vector2(PageLeft, BottomRowY + 1);

        var right = view == View.LoadOrder ? list.OffsetRight : info.OffsetRight;
        if (screen.GetNodeOrNull<Control>("SpireModManagerRelaunchButton") is { } relaunch)
        {
            relaunch.Visible = view == View.Categories;
            relaunch.Position = new Vector2(right - relaunch.Size.X, BottomRowY);
            if (relaunch.Visible)
                right = relaunch.Position.X - 12;
        }
        if (screen.GetNodeOrNull<Control>(HistoryBar.BarName) is { } history)
        {
            var buttonsWidth = history.GetCombinedMinimumSize().X; // its buttons, not its (wider) box
            history.Position = new Vector2(right - history.Size.X, BottomRowY);
            history.Visible = Wide;
            right = right - buttonsWidth - 20;
        }

        // "Changes +4 -3 ↕2": just left of Undo, centred on the row.
        if (screen.GetNodeOrNull<RichTextLabel>(HistoryBar.ChangesName) is { } changes)
        {
            changes.Visible = Wide;
            changes.Position = new Vector2(right - changes.Size.X, BottomRowY + 35 - 14);
            right = changes.Position.X + changes.Size.X - Math.Max(changes.GetContentWidth(), 160) - 16; // its text is right-aligned
        }
        if (view != View.Categories)
            HistoryBar.CloseList(info); // the full changes list lives in the main list's panel
        History.NotifyChanged(); // the buttons act on this view's history

        // The game's "takes effect after a restart" line fits between the left and right buttons in
        // Categories (its text shrinks to fit), and goes back to its own place in Vanilla View.
        var pending = screen.GetNode<Control>("%PendingChangesLabel");
        Place(pending, new Vector4(leftEnd + 12, 1000, right, 1068));
        pending.Modulate = view == View.LoadOrder ? Colors.Transparent : Colors.White; // the game sets Visible itself
        Watermark(screen);
        UpdateDot(screen);
    }

    // The game's version and date in the top-right corner are dark grey, made for its brighter menu art; on
    // our darker pages they turn light, and back when the page closes or in Vanilla View.
    static void Watermark(Control screen)
    {
        Label? label = null;
        for (var node = screen.GetParent(); node != null && label == null; node = node.GetParent())
            label = node.GetNodeOrNull<Label>("ReleaseInfo");
        if (label is not { } release)
            return;
        // The scene tints it with self_modulate (black, 30%).
        if (!release.HasMeta(OriginalMeta))
            release.SetMeta(OriginalMeta, release.SelfModulate);
        void Set(bool light) =>
            release.SelfModulate = light ? new Color(1, 0.964706f, 0.886275f, 0.5f) : (Color)release.GetMeta(OriginalMeta);
        Set(Wide && screen.IsVisibleInTree());
        if (!screen.HasMeta(WatermarkMeta))
        {
            screen.SetMeta(WatermarkMeta, true);
            screen.VisibilityChanged += () => Set(Wide && screen.IsVisibleInTree());
            screen.TreeExiting += () => Set(false);
        }
    }

    static Label AddViewTitle(Control screen)
    {
        var label = new Label
        {
            Name = "SpireModManagerViewTitle", Text = Menus.Text("SPIRE_MOD_MANAGER.LOAD_ORDER"), Size = new Vector2(500, 40),
            HorizontalAlignment = HorizontalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        label.AddThemeFontOverride("font", ResourceLoader.Load<Font>("res://themes/kreon_bold_shared.tres"));
        label.AddThemeFontSizeOverride("font_size", 26);
        label.AddThemeColorOverride("font_color", new Color(0.937f, 0.784f, 0.318f));
        screen.AddChild(label);
        return label;
    }

    // Profile bar, search and (in Load Order, when shown) the order warning sit at the top of the list in our
    // views; the list starts below the lowest. Vanilla View hides them and gives the list its full height back.
    public static void PlaceList(Control list)
    {
        var scroll = list.GetNode<Control>("ModsScrollContainer");
        if (!scroll.HasMeta(OriginalMeta))
            scroll.SetMeta(OriginalMeta, scroll.OffsetTop);
        var tops = new[] { "SpireModManagerProfileBar", "SpireModManagerSearch" }.Select(n => list.GetNodeOrNull<Control>(n)).OfType<Control>().ToList();
        foreach (var top in tops)
            top.Visible = Wide;
        if (list.GetNodeOrNull<Control>(LoadOrder.WarningName) is { } warning)
            tops.Add(warning);
        scroll.OffsetTop = Wide && tops.Count > 0 ? tops.Max(t => t.OffsetBottom) + 6 : (float)scroll.GetMeta(OriginalMeta);
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
