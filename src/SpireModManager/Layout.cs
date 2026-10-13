using Godot;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
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
    const string BoxMeta = "spire_mod_manager_original_box", BarMeta = "spire_mod_manager_original_bar", AnchorMeta = "spire_mod_manager_original_anchor";

    // Screen-space boxes (left, top, right, bottom) in the game's 1920x1080 layout.
    // The panels sit midway between the page title and the bottom row of buttons.
    static readonly Vector4 ListBox = new(175, 72, 1080, 984);
    static readonly Vector4 InfoBox = new(1155, 72, 1900, 984); // clear of the list's scrollbar (list right + 6..54)
    static readonly Vector4 CentredListBox = new(400, 72, 1500, 984);
    public const float BottomRowY = 999; // top of the 70px-tall buttons along the bottom
    public const float CornerX = 18; // the lower-left corner button (here and on the main menu)
    const float PageLeft = 175, TitleTop = 10, TitleBottom = 62;
    const float UndoDrop = 7; // the undo buttons themselves (not their hints) centred on the bottom row

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
        // Better Mod Menu loaded too: paused while we place things (its re-layout follows every resize), and its push
        // down of the list taken back. Vanilla View gives it the page again at the end.
        if (Bmm.Present)
        {
            Bmm.Detach();
            Bmm.Unpush(list);
        }
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

        PlaceInfo(info);
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

        // Better Mod Menu clips the mask for its page; ours draw without it (the blockers above do its job). Its
        // scrollbar lane (wider, midway to the info panel) is its page's too.
        if (Bmm.Present)
            mask.ClipContents = !Wide;
        if ((scroll as NScrollableContainer)?.Scrollbar is { } bar)
            SceneBar(bar, Wide);

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
            history.Position = new Vector2(right - history.Size.X, BottomRowY + UndoDrop);
            history.Visible = Wide;
            right = right - buttonsWidth - 20;
        }

        // "Mods enabled 23 (+1 -3)" over "Changes 5  ↕2", left-aligned, just left of Undo.
        if (screen.GetNodeOrNull<RichTextLabel>(HistoryBar.ChangesName) is { } changes)
        {
            changes.Visible = Wide;
            changes.Position = new Vector2(changes.Position.X, BottomRowY + 41 - changes.Size.Y / 2); // two lines, a little below the row's middle
            changes.SetMeta(HistoryBar.RightMeta, right);
            HistoryBar.FitChanges(changes);
            right = changes.Position.X - 16;
        }
        if (view != View.Categories)
        {
            HistoryBar.CloseList(info); // the full changes list lives in the main list's panel
            HistoryBar.HideBack(info);
        }
        History.NotifyChanged(); // the buttons act on this view's history

        // The game's "takes effect after a restart" line fits between the left and right buttons in
        // Categories (its text shrinks to fit), and goes back to its own place in Vanilla View. Our wording on our
        // views, the game's in Vanilla View: the game sets its text once, in _Ready (before our postfix places it),
        // and after that only shows or hides it.
        var pending = screen.GetNode<MegaRichTextLabel>("%PendingChangesLabel");
        Place(pending, new Vector4(leftEnd + 12, 1000, right, 1068));
        pending.SetTextAutoSize(new LocString("settings_ui", Wide ? "SPIRE_MOD_MANAGER.PENDING_RESTART" : "MODDING_SCREEN.PENDING_CHANGES_WARNING").GetFormattedText());
        pending.Modulate = view == View.LoadOrder ? Colors.Transparent : Colors.White; // the game sets Visible itself
        Watermark(screen);
        BackLabel(screen);
        UpdateDot(screen);

        // Better Mod Menu: hidden on our views; its own page in Vanilla View.
        if (Bmm.Present)
        {
            if (Wide)
                Bmm.Show(screen, false);
            else
                Bmm.Attach(screen);
        }
    }

    // "Main Menu" over the game's back arrow on our pages (it leaves the whole Modding page); not in Vanilla View.
    static void BackLabel(Control screen)
    {
        if (!screen.HasNode("BackButton"))
            return;
        if (screen.GetNodeOrNull<Label>("SpireModManagerBackLabel") is not { } label)
        {
            label = new Label
            {
                Name = "SpireModManagerBackLabel", Text = Menus.Text("SPIRE_MOD_MANAGER.MAIN_MENU"),
                HorizontalAlignment = HorizontalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            label.AddThemeFontOverride("font", ResourceLoader.Load<Font>("res://themes/kreon_bold_shared.tres"));
            label.AddThemeFontSizeOverride("font_size", 18);
            label.AddThemeColorOverride("font_color", new Color(1, 0.964706f, 0.886275f, 0.85f));
            label.AddThemeColorOverride("font_outline_color", new Color(0.0756f, 0.12084f, 0.18f));
            label.AddThemeConstantOverride("outline_size", 6);
            screen.AddChild(label);
        }
        label.Visible = Wide;
        // Centred over the arrow where it rests (-40,726, 200x110; it slides in from off-screen, so not read
        // from its position).
        label.Size = new Vector2(150, 24);
        label.Position = new Vector2(0, 726 - 27);
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

    // Across the top of the list in our views, stacked: the profile bar (main list only), the search box (with
    // Auto sort in Load Order), the red banners when shown (two mod managers on, then the page's own warning:
    // the one that stays put above the one that comes and goes as the list changes), and on the main list the page
    // bar and its hint (only made when there are no categories); the list starts below them. Vanilla View hides them
    // and gives the list its full height back.
    public static void PlaceList(Control list)
    {
        // The scene's box each time (Better Mod Menu moves and resizes it on its page); in our views, the top set below.
        var scroll = list.GetNode<Control>("ModsScrollContainer");
        if (!scroll.HasMeta(BoxMeta))
            scroll.SetMeta(BoxMeta, new Vector4(scroll.OffsetLeft, scroll.OffsetTop, scroll.OffsetRight, scroll.OffsetBottom));
        var box = (Vector4)scroll.GetMeta(BoxMeta);
        (scroll.OffsetLeft, scroll.OffsetRight, scroll.OffsetBottom) = (box.X, box.Z, box.W);
        var y = box.Y;
        // Stacks one control below the last; null or hidden ones take no room.
        void Stack(Control? control, bool shown, float gap)
        {
            if (control == null)
                return;
            control.Visible = shown;
            if (!shown)
                return;
            var height = control.OffsetBottom - control.OffsetTop;
            (control.OffsetTop, control.OffsetBottom) = (y, y + height);
            y += height + gap;
        }
        // Profiles belong to the main list; Load Order only orders, so it starts with the search box.
        Stack(list.GetNodeOrNull<Control>("SpireModManagerProfileBar"), Current == View.Categories, 8);
        var search = list.GetNodeOrNull<Control>("SpireModManagerSearch");
        Stack(search, Wide, 6);
        // Auto sort and its hint, at the search box's right end: level with it.
        if (search != null)
            foreach (var name in new[] { LoadOrder.AutoSortName, LoadOrder.AutoSortHintName })
                if (list.GetNodeOrNull<Control>(name) is { } beside)
                    (beside.OffsetTop, beside.OffsetBottom) = (search.OffsetTop, search.OffsetBottom);
        Stack(list.GetNodeOrNull<Control>(LoadOrder.ManagersName), Wide, 6);
        Stack(list.GetNodeOrNull<Control>(LoadOrder.WarningName), Wide, 6);
        Stack(list.GetNodeOrNull<Control>(PageBar.Name), Current == View.Categories, 4);
        Stack(list.GetNodeOrNull<Control>(PageBar.HintName), Current == View.Categories, 4);
        scroll.OffsetTop = Wide ? y : box.Y;
    }

    // Vanilla View: the game's notification dot on "Spire Manager" while a relaunch is pending.
    public static void UpdateDot(Node screen)
    {
        if (screen.GetNodeOrNull<Control>("SpireModManagerViewToggle/Dot") is { } dot)
            dot.Visible = Current == View.Vanilla && ModEntry.HasPendingChanges();
    }

    // Inside the info panel, fitted to its size: the head (title, source and short facts, left) beside a
    // large thumbnail of the mod image (right); the description below both, then the player's fields
    // (ModDetails.PlaceFields); Open Folder along the bottom. With the changes list's Back button showing (top
    // right), the thumbnail and everything under it move down to make room. A mod without a picture: the
    // description starts right under the head.
    public static void PlaceInfo(Control info)
    {
        const float top = 16;
        var width = info.Size.X;
        var inset = ModDetails.Inset;
        var (thumbWidth, thumbHeight, thumbRight) = (ModDetails.ThumbWidth, ModDetails.ThumbHeight, ModDetails.ThumbRight);
        var picture = info.GetNode<TextureRect>("ModImage").Texture != null;
        var drop = picture && info.GetNodeOrNull<Control>(HistoryBar.BackName) is { Visible: true } ? 36 : 0;
        var head = info.GetNodeOrNull<RichTextLabel>(ModDetails.HeadName) is { Visible: true } facts ? facts.OffsetTop + facts.GetContentHeight() : 86;
        var below = Math.Max(head, picture ? top + drop + thumbHeight : 0) + 16;
        Place(info.GetNode<Control>("ModTitle"), new Vector4(inset, 10, width - thumbWidth - thumbRight - 20, 52));
        // The scene gives the image a 500x380 minimum size, which beats any smaller box: lifted in our views.
        var image = info.GetNode<Control>("ModImage");
        if (!image.HasMeta(MinSizeMeta))
            image.SetMeta(MinSizeMeta, image.CustomMinimumSize);
        image.CustomMinimumSize = Wide ? Vector2.Zero : (Vector2)image.GetMeta(MinSizeMeta);
        // The picture at its own shape inside the box, against the box's right edge.
        var shown = image is TextureRect { Texture: { } texture } && texture.GetHeight() > 0
            ? new Vector2(thumbWidth, thumbWidth * texture.GetHeight() / texture.GetWidth()) is var wide && wide.Y <= thumbHeight
                ? wide : new Vector2(thumbHeight * texture.GetWidth() / texture.GetHeight(), thumbHeight)
            : new Vector2(thumbWidth, thumbHeight);
        Place(image, new Vector4(width - thumbRight - shown.X, top + drop, width - thumbRight, top + drop + shown.Y));
        // Better Mod Menu anchors the description to the panel's bottom on its page: the scene's anchor back first.
        var description = info.GetNode<Control>("ModDescription");
        if (!description.HasMeta(AnchorMeta))
            description.SetMeta(AnchorMeta, description.AnchorBottom);
        description.AnchorBottom = (float)description.GetMeta(AnchorMeta);
        Place(description, new Vector4(inset, below, width - inset,
            info.Size.Y - ModDetails.ButtonsFromBottom - ModDetails.FieldsGap));
    }

    // The list's scrollbar as the scene had it (box, minimum width, mouse filter; saved the first time), back if
    // restore. Better Mod Menu widens it and moves it midway to the info panel on its page.
    static void SceneBar(Control bar, bool restore)
    {
        if (!bar.HasMeta(BarMeta))
            bar.SetMeta(BarMeta, new Godot.Collections.Array
                { new Vector4(bar.OffsetLeft, bar.OffsetTop, bar.OffsetRight, bar.OffsetBottom), bar.CustomMinimumSize, (int)bar.MouseFilter });
        if (!restore)
            return;
        var saved = (Godot.Collections.Array)bar.GetMeta(BarMeta);
        bar.CustomMinimumSize = (Vector2)saved[1];
        var box = (Vector4)saved[0];
        (bar.OffsetLeft, bar.OffsetTop, bar.OffsetRight, bar.OffsetBottom) = (box.X, box.Y, box.Z, box.W);
        bar.MouseFilter = (Control.MouseFilterEnum)(int)saved[2];
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
