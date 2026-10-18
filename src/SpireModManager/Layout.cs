using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;
using MegaCrit.Sts2.addons.mega_text;

namespace SpireModManager;

// Where everything sits in each view: Categories (wide list and info panel, smaller text, plain panels), Load Order
// (the list alone, centred) and Vanilla (the game's own layout, saved the first time we change it).
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
    const string HomeMeta = "spire_mod_manager_home", SeenMeta = "spire_mod_manager_seen", ParkedMeta = "spire_mod_manager_parked";
    const string ListMeta = "spire_mod_manager_parked_by_list"; // where a part was before the changes list moved it (ParkOthers)
    const string ShownMeta = "spire_mod_manager_game_shown"; // whether the game's own page shows a part of its own
    const float ParkedY = -10000; // other mods' parts with no room, far above the panel (Park)

    // The info panel's own parts as the game's scene makes them (by name), noted the first time we see the panel.
    static HashSet<string>? _sceneParts;

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
        // The first call runs in our _Ready postfix (Priority.First, before other mods' postfixes and before any mod
        // fills the panel), so its parts are still the scene's own.
        _sceneParts ??= info.GetChildren().Select(c => c.Name.ToString()).ToHashSet();
        if (info is NModInfoContainer panel)
            ModDetails.Looks(panel, game: Store.Data.VanillaView); // also when no mod is picked (no fill)
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

        // Page title: ours, smaller, at the list's top-left in our views; the game's in Vanilla View.
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
        // The game's Make Mods and Get Mods: hidden in our views, and in Vanilla View as its own page has them (the
        // stable branch's page hides both, the beta's shows them).
        foreach (var name in new[] { "%MakeModsButton", "%GetModsButton" })
        {
            var button = list.GetNode<Control>(name);
            if (!button.HasMeta(ShownMeta))
                button.SetMeta(ShownMeta, button.Visible);
            button.Visible = !Wide && button.GetMeta(ShownMeta).AsBool();
        }

        // Plain dark panels without the blue border in our views.
        PanelStyle(list);
        PanelStyle(info);

        PlaceInfo(info);
        ViewFont(info.GetNode<MegaRichTextLabel>("ModTitle"), InfoTitleSize);
        ViewFont(info.GetNode<MegaRichTextLabel>("ModDescription"), InfoTextSize);

        var scroll = list.GetNode<Control>("ModsScrollContainer");

        // The mask only clips drawing, so rows scrolled out of view still took clicks above and below the list:
        // invisible blockers just outside it catch those first (ClipContents on the mask breaks its drawing).
        var mask = scroll.GetNode<Control>("Mask");
        if (mask.GetNodeOrNull("SpireModManagerBlockTop") == null)
            foreach (var (name, top) in new[] { ("SpireModManagerBlockTop", true), ("SpireModManagerBlockBottom", false) })
            {
                var blocker = new Control { Name = name, MouseFilter = Control.MouseFilterEnum.Stop };
                mask.AddChild(blocker);
                (blocker.AnchorTop, blocker.AnchorBottom, blocker.AnchorRight) = (top ? 0 : 1, top ? 0 : 1, 1);
                (blocker.OffsetTop, blocker.OffsetBottom) = top ? (-2000, 0) : (0, 2000);
            }

        // Better Mod Menu clips the mask and widens the scrollbar lane for its page; ours do without.
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

        // Bottom row: Vanilla List and Load Order from the lower-left corner on the main list (Spire Manager alone in
        // Vanilla View, Return alone in Load Order); Changes, Undo/Redo/Revert and Relaunch on the right (main list).
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

        // The game's "takes effect after a restart" line: between the button groups with our wording in our views, in
        // its own place with its own text in Vanilla View (the game sets its text once, in _Ready).
        var pending = screen.GetNode<MegaRichTextLabel>("%PendingChangesLabel");
        Place(pending, new Vector4(leftEnd + 12, 1000, right, 1068));
        pending.SetTextAutoSize(new LocString("settings_ui", Wide ? "SPIRE_MOD_MANAGER.PENDING_RESTART" : "MODDING_SCREEN.PENDING_CHANGES_WARNING").GetFormattedText());
        pending.Modulate = view == View.LoadOrder ? Colors.Transparent : Colors.White; // Visible: UpdateDot, below
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
            label.AddThemeFontOverride("font", ModEntry.Font("res://themes/kreon_bold_shared.tres"));
            label.AddThemeFontSizeOverride("font_size", 18);
            label.AddThemeColorOverride("font_color", new Color(1, 0.964706f, 0.886275f, 0.85f));
            label.AddThemeColorOverride("font_outline_color", new Color(0.0756f, 0.12084f, 0.18f));
            label.AddThemeConstantOverride("outline_size", 6);
            screen.AddChild(label);
        }
        label.Visible = Wide;
        // Centred over the arrow's resting place (-40,726, 200x110), not its position: it slides in from off-screen.
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
        label.AddThemeFontOverride("font", ModEntry.Font("res://themes/kreon_bold_shared.tres"));
        label.AddThemeFontSizeOverride("font_size", 26);
        label.AddThemeColorOverride("font_color", new Color(0.937f, 0.784f, 0.318f));
        screen.AddChild(label);
        return label;
    }

    // Stacks the top of the list in our views: the profile bar (main list), the search box (with Auto sort in Load
    // Order), the banners, and the page bar with its hint; the list starts below. Vanilla View gives the list its full
    // height back.
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
            foreach (var name in new[] { LoadOrder.AutoSortName, LoadOrder.AutoSortHintName, FindBar.Name })
                if (list.GetNodeOrNull<Control>(name) is { } beside)
                    (beside.OffsetTop, beside.OffsetBottom) = (search.OffsetTop, search.OffsetBottom);
        var bannersTop = y;
        Stack(list.GetNodeOrNull<Control>(LoadOrder.SelfName), Wide, 6);
        Stack(list.GetNodeOrNull<Control>(LoadOrder.ManagersName), Wide, 6);
        Stack(list.GetNodeOrNull<Control>(LoadOrder.SameIdName), Wide, 6);
        Stack(list.GetNodeOrNull<Control>(LoadOrder.WarningName), Wide, 6);
        var banners = y - bannersTop;
        Stack(list.GetNodeOrNull<Control>(PageBar.Name), Current == View.Categories, 4);
        Stack(list.GetNodeOrNull<Control>(PageBar.HintName), Current == View.Categories, 4);
        scroll.OffsetTop = Wide ? y : box.Y;
        KeepRows(list, scroll, banners);
    }

    // What the list showed when last placed (the page, view, Show: choice and search), the banners' height then, and the
    // frame it began showing that.
    static ((ulong Page, View View, ListFilter.Choice Choice, string Search) Showing, float Banners, ulong Since) _placed;

    // A banner coming, going or rewrapping moves the list's top by its change in height: the rows scroll by as much at
    // once, so the ones on screen stay where they were (CategoryList.KeepInPlace), at the list's top too (the row under
    // the mouse, just ticked, stays under it) while the page shows; hidden, a list at its top stays there and only the
    // banners' height is taken. Not when the list shows something else than last time (another page or view, a Show:
    // choice, a search): that starts where it always did, for the rest of its frame too, where a banner made at the old
    // view's width can still change height. Scrolls on purpose (Reveal, Load Order's find) come later and set their own
    // target.
    static void KeepRows(Control list, Control scroll, float banners)
    {
        if (scroll.GetNodeOrNull<Control>("Mask/Content") is not { } content)
            return;
        var showing = (list.GetInstanceId(), Current, ListFilter.Chosen, CategoryList.Filter(content));
        var frame = Engine.GetProcessFrames();
        if (showing != _placed.Showing)
            _placed = (showing, banners, frame);
        else if (banners != _placed.Banners)
        {
            if (frame != _placed.Since)
                CategoryList.KeepInPlace(content, banners - _placed.Banners);
            _placed.Banners = banners;
        }
    }

    // Vanilla View: the game's notification dot on "Spire Manager" while a relaunch is pending.
    // The page's "after a restart" line and the view toggle's dot, by the main menu button's rule (the game's own check
    // looks only at mods it loaded): on every build, show and change, so a page rebuilt after a run agrees with the menu.
    public static void UpdateDot(Node screen)
    {
        var pending = ModEntry.HasPendingChanges();
        if (screen.GetNodeOrNull<Control>("%PendingChangesLabel") is { } line)
            line.Visible = pending;
        if (screen.GetNodeOrNull<Control>("SpireModManagerViewToggle/Dot") is { } dot)
            dot.Visible = Current == View.Vanilla && pending;
    }

    // Places the info panel's parts: the head (title, source, facts) beside a large thumbnail, the description below,
    // other mods' parts, then the player's fields and Open Folder. The changes list's Back button pushes them down.
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
        if (!Wide)
            PutOthersBack(info);
    }

    // Other mods' parts in the info panel (Undo And Restart adds one right where our fields are): not the game's own,
    // ours or Better Mod Menu's, and not ones that stretch with the panel.
    public static List<Control> OtherParts(Control info) =>
        AllOthers(info).Where(c => c.AnchorLeft == c.AnchorRight && c.AnchorTop == c.AnchorBottom && c.Visible).ToList();

    // All of them: shown or not, stretching or not (the changes list moves them all out of sight: ParkOthers).
    static IEnumerable<Control> AllOthers(Control info) => _sceneParts is not { } scene ? []
        : info.GetChildren().OfType<Control>().Where(c => c.Name.ToString() is var name && !scene.Contains(name)
            && !name.StartsWith("SpireModManager") && name != "OpenFolderButton" && !name.StartsWith("BetterModMenu"));

    // Our views: the parts that fit in the room, in their mods' order; the rest go out of sight (Park). A part new this
    // frame may not be laid out yet, so it waits a frame.
    public static List<Control> FitOthers(Control info, float room)
    {
        var fitting = new List<Control>();
        var frame = (long)Engine.GetProcessFrames();
        var settling = false;
        foreach (var part in OtherParts(info))
        {
            if (!part.HasMeta(HomeMeta)) // where its mod put it, for Vanilla View
                part.SetMeta(HomeMeta, new Vector4(part.OffsetLeft, part.OffsetTop, part.OffsetRight, part.OffsetBottom));
            if (!part.HasMeta(SeenMeta))
                part.SetMeta(SeenMeta, frame);
            var settled = (long)part.GetMeta(SeenMeta) < frame;
            settling |= !settled;
            var height = part.Size.Y + ModDetails.FieldsGap;
            if (height <= room)
            {
                room -= height;
                fitting.Add(part);
                Unpark(part);
            }
            else if (settled)
                Park(part);
        }
        if (settling)
            info.GetTree().CreateTimer(0).Timeout += () =>
            {
                if (GodotObject.IsInstanceValid(info))
                    ModDetails.PlaceFields(info);
            };
        return fitting;
    }

    // At our left edge, y down the panel, its size as its mod made it (moved by its offsets, whatever its anchors).
    public static void PlaceOther(Control part, float y)
    {
        var (dx, dy) = (ModDetails.Inset - part.Position.X, y - part.Position.Y);
        (part.OffsetLeft, part.OffsetTop, part.OffsetRight, part.OffsetBottom) =
            (part.OffsetLeft + dx, part.OffsetTop + dy, part.OffsetRight + dx, part.OffsetBottom + dy);
    }

    // How many of other mods' parts are out of sight for lack of room (not the ones new this frame, still settling).
    public static int Parked(Control info) => OtherParts(info).Count(p => p.HasMeta(ParkedMeta));

    // Out of sight for lack of room: far above the panel and out of keyboard reach. Never hidden, so its mod's own
    // Visible still holds when room comes back.
    static void Park(Control part)
    {
        if (!part.HasMeta(ParkedMeta))
            part.SetMeta(ParkedMeta, (long)part.FocusBehaviorRecursive);
        part.FocusBehaviorRecursive = Control.FocusBehaviorRecursiveEnum.Disabled;
        PlaceOther(part, ParkedY);
    }

    // Room again (or Vanilla View): in keyboard reach as before; placed by the caller.
    static void Unpark(Control part)
    {
        if (!part.HasMeta(ParkedMeta))
            return;
        part.FocusBehaviorRecursive = (Control.FocusBehaviorRecursiveEnum)(long)part.GetMeta(ParkedMeta);
        part.RemoveMeta(ParkedMeta);
    }

    // The changes list covers the panel: every other mod's part goes out of sight too (never hidden), and where it was
    // is noted. Returns them all, so the list leaves their Visible alone.
    public static List<Control> ParkOthers(Control info)
    {
        var others = AllOthers(info).ToList();
        foreach (var part in others.Where(p => !p.HasMeta(ParkedMeta)))
        {
            part.SetMeta(ListMeta, new Vector4(part.OffsetLeft, part.OffsetTop, part.OffsetRight, part.OffsetBottom));
            Park(part);
        }
        return others;
    }

    // The list closed: each part it moved goes back where it was and into keyboard reach (unless Vanilla View already
    // put it back); the panel is placed again after.
    public static void UnparkOthers(Node info)
    {
        foreach (var part in info.GetChildren().OfType<Control>().Where(c => c.HasMeta(ListMeta)))
        {
            var box = (Vector4)part.GetMeta(ListMeta);
            part.RemoveMeta(ListMeta);
            if (!part.HasMeta(ParkedMeta))
                continue;
            // Moved back by as much as it went, its box kept: a resize its mod made meanwhile stays.
            var (dx, dy) = (box.X - part.OffsetLeft, box.Y - part.OffsetTop);
            (part.OffsetLeft, part.OffsetTop, part.OffsetRight, part.OffsetBottom) =
                (part.OffsetLeft + dx, part.OffsetTop + dy, part.OffsetRight + dx, part.OffsetBottom + dy);
            Unpark(part);
        }
    }

    // Vanilla View: other mods' parts where their mods put them, in reach again if we moved them out of sight.
    static void PutOthersBack(Control info)
    {
        foreach (var part in info.GetChildren().OfType<Control>().Where(c => c.HasMeta(HomeMeta)))
        {
            var home = (Vector4)part.GetMeta(HomeMeta);
            (part.OffsetLeft, part.OffsetTop, part.OffsetRight, part.OffsetBottom) = (home.X, home.Y, home.Z, home.W);
            part.RemoveMeta(HomeMeta); // noted again on our views, in case its mod moves it meanwhile
            Unpark(part);
        }
    }

    // The list's scrollbar as the scene had it (saved the first time), restored if asked; Better Mod Menu changes it
    // for its page.
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

    // Our size in our views; the scene's (saved the first time) in Vanilla View.
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
        Refit(label);
    }

    // Our size as the most the game's auto-size may pick, then fitted again (Refit). Left alone when already set (each
    // override restyles the text, and every row title passes here each redraw) or when it's the auto-size's own pick.
    public static void FontSize(MegaRichTextLabel label, int size)
    {
        var drawn = label.GetThemeFontSize("normal_font_size");
        if (label.MaxFontSize == size && label.GetThemeFontSize("bold_font_size") == drawn
            && (drawn == size || ((_refits ??= Refits()) && drawn == (int)label.Get(LastSetSize))))
            return;
        label.AddThemeFontSizeOverride("normal_font_size", size);
        label.AddThemeFontSizeOverride("bold_font_size", size);
        label.MaxFontSize = size;
#if DEVTOOLS
        TestFitting?.Invoke(label);
#endif
        Refit(label);
    }

    // Runs the game's auto-size again after a size written directly: setting the same text doesn't, and it skips a size
    // equal to its last. Deferred, once text and box are final; with auto-size off, RowTitles.AutoSize runs it instead
    // (`now`, `newText`). Its members are private, reached by name: if a game update renames them, sizes stay as written.
    public static void Refit(MegaRichTextLabel label, bool now = false, bool newText = false)
    {
        if (!(_refits ??= Refits()))
            return;
        if (!label.AutoSizeEnabled)
        {
            label.Set(LastSetSize, 0);
            return;
        }
        var drawn = label.GetThemeFontSize("normal_font_size");
        if (now && !newText && (int)label.Get(LastSetSize) == drawn && label.Size == (Vector2)label.Get(LastAdjustedSize))
            return;
        label.Set(LastSetSize, drawn);
#if DEVTOOLS
        TestFitting?.Invoke(label);
#endif
        if (now)
            label.Call(AdjustFontSize);
        else
            label.CallDeferred(AdjustFontSize);
    }

    static readonly StringName LastSetSize = "_lastSetSize", LastAdjustedSize = "_lastAdjustedSize", AdjustFontSize = "AdjustFontSize";
    static bool? _refits;

    // Whether Refit can fit a label (its members found, looked up once).
    public static bool CanRefit => _refits ??= Refits();

#if DEVTOOLS
    // Self-test: told of each label FontSize writes a size to or Refit fits again.
    public static Action<MegaRichTextLabel>? TestFitting { get; set; }
#endif

    // Finds Refit's members (AdjustFontSize by its signature); a lookup that throws counts as not found.
    static bool Refits()
    {
        const string missing = "MegaRichTextLabel's auto-size not found; text keeps the sizes we write";
        try
        {
            var type = typeof(MegaRichTextLabel);
            if (AccessTools.Field(type, "_lastSetSize")?.FieldType == typeof(int) && AccessTools.Field(type, "_lastAdjustedSize")?.FieldType == typeof(Vector2)
                && AccessTools.Method(type, "AdjustFontSize", []) != null)
                return true;
            Log.Warn($"[{ModEntry.ModId}] {missing}");
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.ModId}] {missing}: {e.Message}");
        }
        return false;
    }
}
