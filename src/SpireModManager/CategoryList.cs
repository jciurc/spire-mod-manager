using Godot;
using MegaCrit.Sts2.Core.Audio;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;

namespace SpireModManager;

// Groups the Modding screen's rows under collapsible category headers, inside the game's own row container, so
// scrolling, selection, the info panel and tickboxes stay the game's. Vanilla View removes them and restores the order.
static class CategoryList
{
    const string HeaderScenePath = $"res://{ModEntry.ModId}/scenes/category_header.tscn";
    const string HeaderGroup = "SpireModManagerCategoryHeader";
    const float DragThreshold = 12; // a press that moves further than this is a scroll, not a click
    const string RowMenuMeta = "spire_mod_manager_menu";
    const string CategoryMeta = "spire_mod_manager_category"; // on headers: the category name, "" for Uncategorized
    public const string FilterMeta = "spire_mod_manager_filter"; // on the row container: the search text

    // The search text (trimmed), or "" with none.
    public static string Filter(Control content) => content.HasMeta(FilterMeta) ? (string)content.GetMeta(FilterMeta) : "";
    const string GameGapMeta = "spire_mod_manager_game_gap", GameSidesMeta = "spire_mod_manager_game_sides";
    static readonly Dictionary<string, float> HeaderWidths = [];

    // Our main menu button opens the page on our view, not Vanilla View: it saves that choice just before. The game's
    // own way in leaves the saved view as it is.
    public static void PreferOurs()
    {
        if (!Store.Data.VanillaView)
            return;
        Store.Data.VanillaView = false;
        Store.Save();
    }

    public static void Attach(NModdingScreen screen)
    {
        var content = screen.GetNode<Control>("%ModsScrollContainer/Mask/Content");

        // The way to the other page (a link at the top, right of the title) and Load Order (a button along the bottom);
        // Layout places them. Each turns the other off; Load Order's again goes back to categories, and its label says so.
        LinkButton? vanilla = null;
        Control? loadOrder = null;
        // Whether Vanilla View was laid out last, so the saved view is laid out if it changed while the page was closed.
        var shownVanilla = false;
        void Switch(bool toVanilla) => Show(toVanilla && !Store.Data.VanillaView, !toVanilla && !Store.Data.LoadOrderView);
        void Show(bool toVanilla, bool toLoadOrder)
        {
            var data = Store.Data;
            (data.VanillaView, data.LoadOrderView, shownVanilla) = (toVanilla, toLoadOrder, toVanilla);
            // A picked set belongs to the view it was picked in: let go of it, and of its undo steps.
            Selection.Clear(content, record: false);
            History.ForgetPicks();
            Store.Save();
            PageLinkText(vanilla);
            Label(loadOrder, data.LoadOrderView ? "SPIRE_MOD_MANAGER.BACK" : "SPIRE_MOD_MANAGER.LOAD_ORDER");
            Profiles.RefreshTickboxes(screen); // the boxes as this view reads them (copies of a mod installed twice)
            Apply(content);
            Layout.Apply(screen);
            RestoreSelection(screen, content);
        }
        // Load Order is a side trip: the page always opens on the main list (Vanilla View, a preference, stays).
        if (Store.Data.LoadOrderView)
        {
            Store.Data.LoadOrderView = false;
            Store.Save();
        }
        shownVanilla = Store.Data.VanillaView; // laid out below
        // The page bar's Show: choice stays; rows it kept for a change made last time go by it again (ListFilter).
        ListFilter.Release();
        screen.VisibilityChanged += () =>
        {
            if (!screen.IsVisibleInTree())
                return;
            var released = ListFilter.Release();
            if (Store.Data.LoadOrderView)
                Switch(false);
            else if (Store.Data.VanillaView != shownVanilla)
                Show(Store.Data.VanillaView, false); // changed while the page was closed: our main menu button
            else if (released)
                Apply(content);
        };
        vanilla = PageLink(screen, () => Switch(true));
        // Ours, in the bottom-left corner, in place of the game's back arrow (Layout): back to the main menu as that goes.
        var mainMenu = ModEntry.AddButton(screen, "SPIRE_MOD_MANAGER.MAIN_MENU", new Vector2(200, 70), () => Layout.GoBack(screen, sound: false));
        mainMenu.Name = Layout.MainMenuName;
        loadOrder = ModEntry.AddButton(screen, "SPIRE_MOD_MANAGER.LOAD_ORDER", new Vector2(200, 70), () => Switch(false));
        loadOrder.Name = "SpireModManagerLoadOrderToggle";
        // Labels for the saved view.
        PageLinkText(vanilla);
        Label(loadOrder, Store.Data.LoadOrderView ? "SPIRE_MOD_MANAGER.BACK" : "SPIRE_MOD_MANAGER.LOAD_ORDER");

        Profiles.RefreshTickboxes(screen); // the game ticked them its way (one switch for copies of one id and source)
        Apply(content);
    }

    // The way to the other page, as a plain link (Jordan: no button look) at the top, right of the page's title; like
    // all our links, its colour and the pointing hand, never an underline.
    static LinkButton PageLink(NModdingScreen screen, Action onPressed)
    {
        var link = new LinkButton
        {
            Name = "SpireModManagerViewToggle", Underline = LinkButton.UnderlineMode.Never, FocusMode = Control.FocusModeEnum.None,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
        };
        link.AddThemeFontOverride("font", ModEntry.Font("res://themes/kreon_regular_shared.tres"));
        link.AddThemeFontSizeOverride("font_size", PageLinkSize);
        link.AddThemeColorOverride("font_color", new Color(ModDetails.ModLinkHex));
        link.AddThemeColorOverride("font_hover_color", Colors.White);
        link.AddThemeColorOverride("font_pressed_color", Colors.White);
        link.Pressed += () =>
        {
            SfxCmd.Play(FmodSfx.uiClick);
            onPressed();
        };
        screen.AddChild(link);
        return link;
    }

    public const int PageLinkSize = 18;

    // Its words for the page now shown: "Go to vanilla modding page" on ours, "Go to Spire Mod Manager page" on the
    // game's. On ours, while a mod is installed twice from one source, its card says how that page's tickbox treats
    // the copies; on the game's, no card (Jordan).
    static void PageLinkText(LinkButton? link)
    {
        if (link == null)
            return;
        link.Text = Menus.Text(Store.Data.VanillaView ? "SPIRE_MOD_MANAGER.GO_TO_OURS" : "SPIRE_MOD_MANAGER.GO_TO_VANILLA");
        string? tip = null;
        if (!Store.Data.VanillaView)
        {
            tip = Menus.Text("SPIRE_MOD_MANAGER.VANILLA_VIEW_TIP");
            if (Copies.TwinGroups() is { Count: > 0 } twins)
                tip += "\n" + Menus.Format("SPIRE_MOD_MANAGER.VANILLA_VIEW_SAME_ID", ("mods", string.Join(", ", twins.Select(g => g.Id).Distinct()
                    .Select(id => Dependencies.Find(id) is { } mod ? Store.InfoFor(mod)?.Alias ?? mod.manifest?.name ?? id : id))));
        }
        Hover.Tip(link, tip);
    }

    // On our views, the tip on the tickbox of a copy of a mod installed more than once (and on Load Order's Enable or
    // Disable for it): a copy that's off while another is on, which of them would load with this one on too
    // (Copies.LoadingIf, the rule's: of two copies from one source, the higher version; of this mod's, the game's own
    // pick). Null otherwise: a copy that's on says what it does in the info panel (ModDetails.CopyLine). Nothing is
    // turned off by enabling a copy (Dependencies.Write).
    public static string? CopyTip(Mod mod)
    {
        if (Store.Data.VanillaView || mod.manifest?.id is not { } id || Copies.Others(mod) is not { Count: > 0 } others)
            return null;
        if (Copies.Ticked(mod) || others.FirstOrDefault(Copies.Ticked) is not { } ticked)
            return null;
        // The copy that loads now among the others (a copy that's off can load only through a switch it shares: then
        // another that's on).
        var loading = Copies.Loading(id) is { } now && others.Contains(now) && Copies.Ticked(now) ? now : ticked;
        return Menus.Format("SPIRE_MOD_MANAGER.COPY_ON_TIP", ("other", Copies.Named(loading)), ("loads", Copies.Named(Copies.LoadingIf(mod) ?? mod)));
    }

    // Made once: asked of every row on every redraw and tick (a text passed as one is converted anew each time).
    static readonly NodePath TickboxPath = "Tickbox";
    static readonly StringName CopyBoxMeta = "spire_mod_manager_copy_box"; // on rows whose box CopyBoxes has set

    // Each row's tickbox where its id is installed more than once, on our main list: its tip (CopyTip). Vanilla View keeps
    // the game's boxes, so their tips go; Load Order hides them (on and off are in its row menu, OrderingOnly). After
    // every tick too (RefreshStrips): a tick changes the tips.
    static void CopyBoxes(IEnumerable<NModMenuRow> rows)
    {
        var vanilla = Store.Data.VanillaView;
        foreach (var row in rows)
        {
            if (row.Mod is not { } mod)
                continue;
            var copy = !vanilla && Copies.Others(mod).Count > 0;
            if (!copy && !row.HasMeta(CopyBoxMeta))
                continue; // most rows: a mod installed once, its box never set here
            row.SetMeta(CopyBoxMeta, true);
            Hover.Tip(row.GetNode<NTickbox>(TickboxPath), copy ? CopyTip(mod) : null);
        }
    }

    static void Label(Control? button, string key)
    {
        if (button != null)
            ModEntry.Relabel(button, key);
    }

    // The mod selected on the Modding screen (kept by a patch on OnRowSelected), so a view switch can keep it.
    public static Mod? Selected { get; set; }

    public static NModdingScreen? ScreenOf(Node node)
    {
        for (var n = node.GetParent(); n != null; n = n.GetParent())
            if (n is NModdingScreen screen)
                return screen;
        return null;
    }

    // Selects a row the way a click does: highlight and info panel.
    public static void Select(Control content, NModMenuRow row)
    {
        if (ScreenOf(content) is { } screen)
            Select(screen, row);
    }

    // Another mod's postfix throwing here (Better Mod Menu 1.1.1 does on the 0.111 beta) mustn't stop ours: the
    // game's part and our patch have run by then. Logged once.
    static bool _selectFailed;

    public static void Select(NModdingScreen screen, NModMenuRow row)
    {
        try
        {
            screen.OnRowSelected(row);
        }
        catch (Exception e)
        {
            if (!_selectFailed)
                Log.Warn($"[{ModEntry.ModId}] Another mod failed while a row was selected (logged once): {e.Message}");
            _selectFailed = true;
        }
    }

    // After a view switch: the same mod stays selected, its category opens if it was collapsed, and the
    // list scrolls to it. Nothing selected: the info panel empties (it was filled for the old view).
    static void RestoreSelection(NModdingScreen screen, Control content)
    {
        if (content.GetChildren().OfType<NModMenuRow>().FirstOrDefault(r => r.Mod == Selected && Selected != null) is not { } row)
        {
            Compat.Clear(screen.GetNode<NModInfoContainer>("%ModInfoContainer"));
            return;
        }
        Expand(content, row);
        Select(screen, row);
        ScrollSoon(content, row, flash: false);
    }

    // Opens the collapsed category of a hidden row on the main list.
    static void Expand(Control content, NModMenuRow row)
    {
        if (Store.Data.VanillaView || Store.Data.LoadOrderView || row.Visible)
            return;
        if (Store.InfoFor(row.Mod)?.Category is { } name && Categories.Find(name) is { } category)
            category.Collapsed = false;
        else
            Store.Data.UncategorizedCollapsed = false;
        Store.Save();
        Apply(content);
    }

    // Scrolls a mod's row into view, opening its category and clearing a choice or search that hides it. flash: a short
    // gold glow; focus: the "selected" look without changing the info panel.
    public static void Reveal(Control content, string modId, bool flash, bool focus = false)
    {
        if (Copies.RowOf(content, modId) is not { } row)
            return;
        ListFilter.Bring(row.Mod); // hidden until the next redraw (the search's clearing, or Expand's)
        var filter = Filter(content);
        if (filter.Length > 0 && !Store.Data.LoadOrderView && !ListFilter.Finds(row.Mod, filter)) // Load Order hides no row
            Search.Clear(content);
        Expand(content, row);
        ScrollSoon(content, row, flash, focus);
    }

    // A mod's row in the "selected" look while its name is hovered elsewhere (the info panel, the changes
    // list); only if it's in view: nothing scrolls. Off again: the look it had.
    const string LitMeta = "spire_mod_manager_lit";

    public static void Highlight(Control content, string modId, bool on)
    {
        if (Copies.RowOf(content, modId) is not { } row
            || row.GetNodeOrNull<CanvasItem>("SelectionHighlight") is not { } look)
            return;
        if (!on)
        {
            if (row.HasMeta(LitMeta))
            {
                row.RemoveMeta(LitMeta);
                RowTitles.Look(row, RowState.Selected(row), RowState.Focused(row)); // its own state again
            }
            return;
        }
        var view = ScrollContainer(content).GetGlobalRect();
        if (!row.IsVisibleInTree() || !view.Intersects(row.GetGlobalRect()) || row.HasMeta(LitMeta) || look.Modulate.A > 0.1f)
            return; // not in view, lit already, or showing a look of its own (selected, under the mouse)
        row.SetMeta(LitMeta, true);
        RowTitles.Look(row, RowState.Selected(row), focused: true);
    }

    // Mod names (links "mod:<id>") in a text: hovering one lights its row and, after Hover's delay, shows the
    // mod's card; clicking scrolls to it with the selected look.
    public static void WatchNames(RichTextLabel label, Func<Control?> content)
    {
        label.MetaHoverStarted += meta =>
        {
            if (meta.AsString() is { } target && target.StartsWith("mod:") && content() is { } list)
                Highlight(list, target[4..], true);
        };
        label.MetaHoverEnded += meta =>
        {
            if (meta.AsString() is { } target && target.StartsWith("mod:") && content() is { } list)
                Highlight(list, target[4..], false);
        };
        Hover.WatchLinks(label, "SpireModManagerNotesCard",
            meta => meta.StartsWith("mod:") && Linked(meta[4..]) is { } mod ? ModDetails.Card(mod) : null,
            width: ModDetails.CardWidth, full: meta => meta.StartsWith("mod:") && Linked(meta[4..]) is { } mod ? ModDetails.Card(mod, full: true) : null);
    }

    // The mod a link names: a copy by its key ("<id>|<copy>"), or a mod by its id (Dependencies.Find's copy).
    static Mod? Linked(string key) => key.Contains('|') ? Copies.ModOf(key) : Dependencies.Find(key);

    // Once positions settle after this frame's layout.
    public static void ScrollSoon(Control content, Control row, bool flash, bool focus = false)
    {
        content.GetTree().CreateTimer(0.05).Timeout += () =>
        {
            if (!GodotObject.IsInstanceValid(row))
                return;
            ScrollTo(content, row);
            if (flash)
                Flash(row);
            if (focus)
                row.GrabFocus();
        };
    }

    // A short gold glow over a row (a banner's Show).
    public static void Flash(Control row)
    {
        var glow = new ColorRect { Color = new Color(0.937f, 0.784f, 0.318f, 0.35f), MouseFilter = Control.MouseFilterEnum.Ignore };
        row.AddChild(glow);
        glow.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        var tween = glow.CreateTween();
        tween.TweenProperty(glow, "color:a", 0f, 1.4).SetDelay(0.3);
        tween.TweenCallback(Callable.From(glow.QueueFree));
    }

    // The game's scroll container eases its content towards a target position; these move that target.
    static Control ScrollContainer(Control content) => content.GetParent().GetParent<Control>();

    // The room kept before the first row and after the last: in our views, as far as the rows' highlight reaches.
    static (float Top, float Bottom) Ends => Store.Data.VanillaView ? (0, 0) : (-RowTitles.LookInset.Y, RowTitles.LookInset.W);

    // Kept within the list's ends (that room included): past them the container springs back, which looks like a bounce.
    public static void ScrollBy(Control content, float pixels)
    {
        var scroll = ScrollContainer(content);
        var bottom = Math.Min(0, content.GetParent<Control>().Size.Y - content.Size.Y - Ends.Top - Ends.Bottom);
        scroll.Set("_targetDragPosY", Math.Clamp((float)scroll.Get("_targetDragPosY") + pixels, bottom, 0));
    }

    // The frame the rows were last kept in place in: the scroll target before its first move, and how far the list's
    // top has moved since.
    static (ulong Frame, float From, float Moved) _kept;

    // The list's top moved down by pixels (up, for less than 0) as the banners above it changed: the rows scroll up as
    // much at once, the container's target and the rows together, so the ones on screen stay where they were with no
    // easing and no frame drawn between. Worked out from where the frame began, so moves that cancel out within it (a
    // banner laid out on one line, then wrapped again) leave the list where it was, at its ends too. Kept within the
    // list's ends; a list already past one, springing back, isn't snapped or pushed further. Not while the page is
    // hidden with the list at its top (a banner raised from the main menu, as a mod installs or the relaunch check's
    // fixes redraw it): no row is under the mouse, so the top stays the top and the page opens there, its first header
    // in view. Hidden and scrolled part way, the rows keep their place as ever.
    public static void KeepInPlace(Control content, float pixels)
    {
        if (ScrollContainer(content) is not NScrollableContainer scroll)
            return;
        var target = (float)scroll.Get("_targetDragPosY");
        var frame = Engine.GetProcessFrames();
        if (_kept.Frame != frame)
            _kept = (frame, target, 0);
        _kept.Moved += pixels;
        var from = _kept.From;
        if (from >= 0 && !scroll.IsVisibleInTree())
            return; // at its top as the frame began: the moves below are worked out from there too
        var bottom = Math.Min(0, content.GetParent<Control>().Size.Y - content.Size.Y - Ends.Top - Ends.Bottom);
        var moved = Math.Clamp(from - _kept.Moved, Math.Min(bottom, from), Math.Max(0, from)) - target;
        if (moved == 0)
            return;
        scroll.Set("_targetDragPosY", target + moved);
        content.Position += new Vector2(0, moved);
        // The scrollbar as the container sets it while it scrolls, which it won't now: the rows are where it aims.
        if (bottom < 0)
            scroll.Scrollbar.SetValueWithoutAnimation(Math.Clamp((content.Position.Y - Ends.Top) / bottom, 0, 1) * 100);
    }

    // At once: the game's container stops scrolling once the rows fit (DisableScrollingIfContentFits), so a
    // target alone would never be reached once a search makes the list short.
    public static void ScrollToTop(Control content)
    {
        if (ScrollContainer(content) is NScrollableContainer scroll)
            scroll.InstantlyScrollToTop();
    }

    // The row a third of the way down the visible list.
    public static void ScrollTo(Control content, Control row)
    {
        var scroll = ScrollContainer(content);
        var top = Math.Min(0, -row.Position.Y + scroll.Size.Y / 3);
        var bottom = Math.Min(0, scroll.Size.Y - content.Size.Y - Ends.Top - Ends.Bottom);
        scroll.Set("_targetDragPosY", Math.Max(top, bottom));
    }

    // Redraws after a change that may need a relaunch, and lets the screen recheck its warning. Rows kept for one mod's
    // change go by the Show: choice again (ListFilter.Release), unless keep (not the player's change).
    public static void Redraw(Control content, bool keep = false)
    {
        if (!keep)
            ListFilter.Release();
        Apply(content);
        for (var node = content.GetParent(); node != null; node = node.GetParent())
            if (node is NModdingScreen screen)
            {
                screen.OnModEnabledOrDisabled();
                return;
            }
    }

    public static void Apply(Control content)
    {
        using var lookups = Lookups.Begin(); // every row asks the same questions (is it on, who needs it): answered once
        // The row container only grows by itself: shrink its height to fit once this redraw settles.
        Callable.From(() =>
        {
            content.OffsetBottom = content.OffsetTop;
            // Once resized: a list short enough to fit stops the game's scrolling wherever it was, so back to the top,
            // then shrink it again (moving it keeps its size).
            Callable.From(() =>
            {
                if (!GodotObject.IsInstanceValid(content) || content.Size.Y + Ends.Top + Ends.Bottom > ScrollContainer(content).Size.Y)
                    return;
                ScrollToTop(content);
                content.OffsetBottom = content.OffsetTop;
            }).CallDeferred();
        }).CallDeferred();
        // Our views: taller rows with less space between them, inset from the list's sides so their wider highlight
        // isn't cut. Vanilla View: the game's values.
        if (!content.HasMeta(GameGapMeta))
        {
            content.SetMeta(GameGapMeta, content.GetThemeConstant("separation"));
            content.SetMeta(GameSidesMeta, new Vector2(content.OffsetLeft, content.OffsetRight));
        }
        var sides = (Vector2)content.GetMeta(GameSidesMeta);
        content.AddThemeConstantOverride("separation", Store.Data.VanillaView ? (int)content.GetMeta(GameGapMeta) : RowTitles.RowGap);
        (content.OffsetLeft, content.OffsetRight) = Store.Data.VanillaView ? (sides.X, sides.Y) : (sides.X - RowTitles.LookInset.X, sides.Y - RowTitles.LookInset.Z);
        // The same room at the top and bottom (the scroll container's padding), so the first and last rows' highlights
        // show whole.
        if (ScrollContainer(content) is NScrollableContainer scroller)
            scroller.UpdatePadding(Ends.Top, Ends.Bottom);
        foreach (var header in content.GetChildren().Where(c => c.IsInGroup(HeaderGroup)))
        {
            content.RemoveChild(header);
            header.QueueFree();
        }
        // Better Mod Menu's group headers removed and its row controls hidden, on our views.
        var screen = ScreenOf(content);
        if (!Store.Data.VanillaView && screen != null)
        {
            Bmm.RemoveGroups(content);
            Bmm.Show(screen, false);
        }

        // Vanilla order is the game's mod order, which is how the screen first added the rows.
        var order = ModManager.Mods.Select((mod, i) => (mod, i)).ToDictionary(p => p.mod, p => p.i);
        var rows = content.GetChildren().OfType<NModMenuRow>()
            .OrderBy(r => r.Mod is { } mod && order.TryGetValue(mod, out var i) ? i : int.MaxValue)
            .ToList();
        RowTitles.Apply(rows, Store.Data.VanillaView);

        // Right-click a row for its menu (category, alias, tags, notes; the bulk menu for picked mods; on/off and
        // moves in Load Order). Rows outlive Apply, so connect once.
        foreach (var row in rows.Where(r => !r.HasMeta(RowMenuMeta)))
        {
            row.SetMeta(RowMenuMeta, true);
            row.GuiInput += e =>
            {
                // Main list picking (Load Order handles its own, with dragging): Ctrl or Shift picks without
                // opening the mod in the panel; a plain click lets go of the picked set.
                if (!Store.Data.LoadOrderView && !Store.Data.VanillaView && e is InputEventMouseButton { ButtonIndex: MouseButton.Left } click)
                {
                    if (click.CtrlPressed || click.ShiftPressed || click.MetaPressed)
                    {
                        if (click.Pressed)
                            Selection.Pick(content, row, toggle: !click.ShiftPressed, range: click.ShiftPressed);
                        row.AcceptEvent();
                    }
                    else if (!click.Pressed)
                        Selection.Clear(content);
                    return;
                }
                if (e is not InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: false })
                    return;
                if (Store.Data.LoadOrderView)
                    LoadOrder.ShowRowMenu(content, row, () => Redraw(content));
                else if (Selection.Several && Selection.Contains(row.Mod))
                    ShowBulkMenu(content);
                else
                {
                    Selection.Clear(content); // a right-click elsewhere is about that one mod
                    ShowRowMenu(content, row);
                }
            };
        }

        // Search hides the rows that don't match and opens categories with matches; on the main list the Show: choice
        // too (a row shows when it matches both). A row kept for an edit by hand counts as matching both (ListFilter).
        var filter = Filter(content);
        var searching = filter.Length > 0;
        var filtering = searching || ListFilter.Current != ListFilter.Choice.All;
        ListFilter.Searching(filter);
        // Worked out for every row on first use, then looked up: a redraw asks about each row up to four times.
        HashSet<NModMenuRow>? matching = null;
        bool Matches(NModMenuRow row) =>
            (matching ??= rows.Where(r => ListFilter.Finds(r.Mod, filter) && ListFilter.Shows(r.Mod)).ToHashSet()).Contains(row);

        LoadOrder.UpdateToolbar(content, rows, () => Redraw(content));
        if (Store.Data.LoadOrderView)
        {
            void RedrawAll() => Redraw(content);
            LoadOrder.Apply(content, rows, RedrawAll);
            RowStrip.Apply(rows, StripActions(content), EdgeAction(content));
            return;
        }
        LoadOrder.Undecorate(rows);
        LoadOrder.Reset(content); // no Load Order lines or dimming outside it

        if (Store.Data.VanillaView)
            RowStrip.Hide(rows);
        else
            RowStrip.Apply(rows, StripActions(content), EdgeAction(content));
        CopyBoxes(rows); // Vanilla View: the game's boxes again

        if (Store.Data.VanillaView)
        {
            for (var i = 0; i < rows.Count; i++)
            {
                content.MoveChild(rows[i], i);
                rows[i].Visible = true; // every row, as the game's page shows them (no search box there; with Better Mod Menu its page decides)
            }
            RowTitles.FitVanilla(rows);
            if (screen != null)
                Bmm.Refresh(screen);
            return;
        }

        var categories = Store.Data.Categories;
        var known = categories.Select(c => c.Name).ToHashSet();
        string? CategoryOf(NModMenuRow row) =>
            Store.InfoFor(row.Mod)?.Category is { } name && known.Contains(name) ? name : null;
        if (filtering)
            ListFilter.Showed(rows.Where(Matches).Select(r => r.Mod)); // the rows a change to one mod keeps

        var index = 0;
        var headed = false; // a category's header is shown (the divider before Uncategorized follows them)
        void Add(Control node)
        {
            node.AddToGroup(HeaderGroup);
            content.AddChild(node);
            content.MoveChild(node, index++);
        }
        // A search for #missing or #uninstalled: the profile's mods that aren't installed have no rows, so a line at the
        // list's top says how many, with a link to their list.
        if (searching && Search.Wants(filter, "missing", "uninstalled") && Profiles.Missing() is { Count: > 0 } missing)
            Add(MissingHint(content, missing.Count));

        void AddSection(string title, List<NModMenuRow> members, bool collapsed, Action<bool> setCollapsed, Category? category = null)
        {
            if (filtering && !members.Any(Matches))
            {
                foreach (var row in members)
                {
                    content.MoveChild(row, index++);
                    row.Visible = false;
                }
                return;
            }
            headed |= category != null;
            var muted = category == null;
            var header = ModEntry.Load<PackedScene>(HeaderScenePath)?.Instantiate<Control>() ?? PlainHeader();
            Add(header);
            header.Name = category == null ? "Uncategorized" : $"Category {category.Name}";
            header.SetMeta(CategoryMeta, category?.Name ?? "");
            var titleLabel = header.GetNode<Label>("Title");
            // Our (smaller-text) views: a shorter header.
            header.CustomMinimumSize = new Vector2(0, 36);
            titleLabel.AddThemeFontSizeOverride("font_size", 18);
            header.GetNode<Label>("Count").AddThemeFontSizeOverride("font_size", 15);
            // The scene's Kreon, or the game's font for its language (ModEntry.Font).
            titleLabel.AddThemeFontOverride("font", ModEntry.Font("res://themes/kreon_bold_shared.tres"));
            header.GetNode<Label>("Count").AddThemeFontOverride("font", ModEntry.Font("res://themes/kreon_regular_shared.tres"));
            // The bar as wide as a row's highlight, its edges in line with a highlighted row's (Jordan, 23 Oct).
            var background = header.GetNode<ColorRect>("Background");
            (background.OffsetLeft, background.OffsetRight) = Store.Data.VanillaView ? (0, 0) : (RowTitles.LookInset.X, RowTitles.LookInset.Z);
            if (muted)
            {
                // Not a category the player made: cream instead of gold, fainter background.
                titleLabel.AddThemeColorOverride("font_color", new Color(1, 0.964706f, 0.886275f, 0.7f));
                background.Color = new Color(0.0756f, 0.12084f, 0.18f, 0.3f);
            }
            // The count right after the name, as it shows: a long one is cut short of the header's buttons.
            var count = header.GetNode<Label>("Count");
            count.Text = Menus.Number(filtering ? members.Count(Matches) : members.Count);
            count.HorizontalAlignment = HorizontalAlignment.Left;
            count.AnchorLeft = 0;
            Menus.CutToFit(titleLabel, title, () => count.OffsetLeft = titleLabel.OffsetLeft
                + titleLabel.GetThemeFont("font").GetStringSize(titleLabel.Text, fontSize: 18).X + 10);
            // The header menu's actions inline, right-aligned: the category's own (not for Uncategorized), then
            // Enable all / Disable all at the far right, over the rows' tickboxes.
            var shown = members.Where(Matches).ToList();
            var actions = Menus.InlineButtons(category == null ? [] : HeaderActions(content, category, compact: true), "Actions");
            actions.AddThemeConstantOverride("separation", 4);
            if (category != null)
                actions.AddChild(new Control { CustomMinimumSize = new Vector2(10, 0), MouseFilter = Control.MouseFilterEnum.Ignore });
            foreach (var item in OnOffItems(content, shown))
            {
                var button = Menus.SmallButton(item.Label, item.OnSelect ?? (() => { }), item.Disabled);
                button.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
                Hover.Tip(button, item.Tip, markup: item.TipMarkup); // Disable all where this mod is among them: it stays on
                actions.AddChild(button);
            }
            actions.Alignment = BoxContainer.AlignmentMode.End;
            header.AddChild(actions);
            actions.AnchorLeft = actions.AnchorRight = 1;
            actions.AnchorBottom = 1;
            actions.GrowHorizontal = Control.GrowDirection.Begin;
            (actions.OffsetLeft, actions.OffsetRight, actions.OffsetTop, actions.OffsetBottom) = (-16, -16, 5, -5);
            // Measured once per set of labels in the buttons' font (it changes with the game's language): shaping text is slow.
            var widthKey = $"{ModEntry.Font("res://themes/kreon_regular_shared.tres").GetInstanceId()}\n"
                + string.Join("\n", actions.GetChildren().Select(c => c is Button b ? b.Text : "|"));
            if (!HeaderWidths.TryGetValue(widthKey, out var actionsWidth))
                HeaderWidths[widthKey] = actionsWidth = actions.GetCombinedMinimumSize().X;
            titleLabel.OffsetRight = -16 - actionsWidth - 30;
            foreach (var row in members)
                content.MoveChild(row, index++);

            void Show(bool isCollapsed)
            {
                var open = searching || !isCollapsed;
                header.GetNode<Control>("Arrow").RotationDegrees = open ? 90 : 0;
                foreach (var row in members)
                    row.Visible = open && Matches(row);
            }
            Show(collapsed);

            var baseColor = background.Color;
            header.MouseEntered += () => background.Color = baseColor.Lightened(0.15f);
            header.MouseExited += () => background.Color = baseColor;

            var pressedAt = Vector2.Zero;
            header.GuiInput += e =>
            {
                if (e is InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: false })
                    ShowHeaderMenu(content, category, members.Where(Matches).ToList());
                if (e is not InputEventMouseButton { ButtonIndex: MouseButton.Left } click)
                    return;
                if (click.Pressed)
                    pressedAt = click.GlobalPosition;
                // Not while searching: every section shows open then (Show), so a fold would change out of sight.
                else if (click.GlobalPosition.DistanceTo(pressedAt) < DragThreshold && !searching)
                {
                    setCollapsed(!collapsed);
                    Store.SaveViewState(); // view state: no undo step
                    Apply(content); // the page bar's Expand all and Collapse all follow
                    // The header made again under a still mouse is lit, as the one clicked was.
                    Callable.From(() => content.GetViewport()?.UpdateMouseCursorState()).CallDeferred();
                }
            };
        }

        // Each category's rows by the name they show (an alias, else the mod's name), as the game's language sorts
        // them, any case; two of one name keep the game's order. This mod first in whichever category it's in, whatever
        // its name (Jordan), its copies by name among themselves. Each row's category read once.
        var names = StringComparer.Create(Menus.Culture, ignoreCase: true);
        var byCategory = rows.OrderBy(r => r.Mod?.manifest?.id == ModEntry.ModId ? 0 : 1)
            .ThenBy(r => Store.InfoFor(r.Mod)?.Alias ?? r.Mod?.manifest?.name ?? r.Mod?.manifest?.id ?? "", names).ToLookup(CategoryOf);
        foreach (var category in categories)
            AddSection(category.Name, byCategory[category.Name].ToList(),
                category.Collapsed, c => category.Collapsed = c, category);

        var uncategorized = byCategory[null].ToList();
        if (uncategorized.Count == 0)
            return;
        // Not while searching; under a Show: choice, between categories it shows and Uncategorized if that shows.
        if (headed && !searching && (!filtering || uncategorized.Any(Matches)))
            Add(Divider());
        AddSection(Menus.Text("SPIRE_MOD_MANAGER.UNCATEGORIZED"), uncategorized,
            Store.Data.UncategorizedCollapsed, c => Store.Data.UncategorizedCollapsed = c);
    }

    // The small buttons at the right of each row's strip: moves in Load Order; otherwise its category, opening
    // its menu (the pencil past the tickbox is EdgeAction). Public for the self-test's made-up rows.
    public static Func<NModMenuRow, IEnumerable<MenuItem>> StripActions(Control content)
    {
        // The rows in place, read once for all the strips (Load Order has placed them by then).
        List<NModMenuRow>? placed = null;
        return Store.Data.LoadOrderView
            ? row => LoadOrder.Actions(content, row, () => Redraw(content), compact: true, placed ??= content.GetChildren().OfType<NModMenuRow>().ToList())
            : row => [CategoryButton(content, row)];
    }

    // A row's category button: a long name cut with ".." to RowStrip.CategoryMax, its card then starting with the whole
    // name (as a long tag's does).
    static MenuItem CategoryButton(Control content, NModMenuRow row)
    {
        var (name, tip) = (CategoryName(Copies.KeyOf(row.Mod)), Menus.Text("SPIRE_MOD_MANAGER.CATEGORY_TIP"));
        var shown = Menus.SmallText(name, RowStrip.CategoryMax);
        return shown == name ? new MenuItem(name, () => ShowRowMenu(content, row), Tip: tip)
            : new MenuItem(shown, () => ShowRowMenu(content, row), Tip: $"[b]{Hover.Escape(name)}[/b]\n{Hover.Escape(tip)}", TipMarkup: true);
    }

    // The main list's pencil, past the tickbox at the row's far right: the mod's menu. None in Load Order.
    public static Func<NModMenuRow, MenuItem>? EdgeAction(Control content) => Store.Data.LoadOrderView
        ? null
        : row => new MenuItem("✎", () => ShowRowMenu(content, row), Tip: Menus.Text("SPIRE_MOD_MANAGER.EDIT_TIP"), Flat: true);

    // The mod's category, or "Uncategorized".
    public static string CategoryName(string? modId) =>
        Store.InfoFor(modId)?.Category is { } name && Categories.Find(name) != null ? name : Menus.Text("SPIRE_MOD_MANAGER.UNCATEGORIZED");

    // After a tickbox change: the rows' dependency notes ("(off)", "Required for") and the copies' boxes (their tips)
    // follow at once, without a full redraw. (Their title colours: RowTitles.MarkChanged.)
    public static void RefreshStrips(Control content)
    {
        using var lookups = Lookups.Begin();
        var rows = content.GetChildren().OfType<NModMenuRow>().ToList();
        LoadOrder.UpdateToolbar(content, rows, () => Redraw(content)); // the banner and the "!" follow on/off too
        if (Store.Data.VanillaView)
            return;
        RowStrip.Apply(rows, StripActions(content), EdgeAction(content));
        if (!Store.Data.LoadOrderView)
            CopyBoxes(rows);
    }

    // A mod's right-click menu: its category on the left, its alias, tags and notes on the right; its row stays under
    // a Show: choice (ListFilter.Keep).
    public static Node? ShowRowMenu(Control content, NModMenuRow row)
    {
        if (Store.Data.VanillaView || Copies.KeyOf(row.Mod) is not { Length: > 0 } id)
            return null;
        return MenuPanel.Show(content, () => CategoryItems(content, id), side: _ => ModField.Column(id, _ =>
        {
            ListFilter.Keep(id);
            Edited(content);
        }));
    }

    // The bulk menu for two or more picked mods: their category (marked if shared), on/off, and their tags and notes.
    // Edits keep their rows under a Show: choice (ListFilter.Keep); Enable selected and Disable selected apply it at once.
    public static Node? ShowBulkMenu(Control content)
    {
        var ids = Selection.Mods.Select(Copies.Key).Where(key => key.Length > 0).Distinct().ToList(); // a key per copy
        if (Store.Data.VanillaView || ids.Count == 0)
            return null;
        return MenuPanel.Show(content, () => BulkItems(content, ids), side: _ => ModField.BulkColumn(ids, _ =>
        {
            ListFilter.Keep(ids);
            Edited(content);
        }));
    }

    static IReadOnlyList<MenuItem> BulkItems(Control content, List<string> ids)
    {
        var shared = ids.Select(id => Store.InfoFor(id)?.Category is { } name && Categories.Find(name) != null ? name : null).Distinct().ToList();
        var common = shared.Count == 1 ? shared[0] : "\n"; // not all the same: nothing marked
        var items = new List<MenuItem> { MenuItem.Heading(Menus.Text("SPIRE_MOD_MANAGER.CATEGORY")) };
        foreach (var category in Store.Data.Categories)
            items.Add(new MenuItem(category.Name, () => Move(category.Name), Checked: category.Name == common, Radio: true));
        items.Add(new MenuItem(Menus.Text("SPIRE_MOD_MANAGER.UNCATEGORIZED"), () => Move(null), Checked: common == null, Radio: true));
        items.Add(MenuItem.Heading(""));
        items.Add(new MenuItem(Menus.Text("SPIRE_MOD_MANAGER.NEW_CATEGORY"), () => NewCategory(content, ids)));
        items.Add(MenuItem.Heading(""));
        // The picked rows themselves: a copy picked without its twin switches alone.
        items.AddRange(OnOffItems(content, content.GetChildren().OfType<NModMenuRow>().Where(r => Selection.Contains(r.Mod)), selected: true));
        return items;

        void Move(string? category)
        {
            Store.Batch(() =>
            {
                foreach (var id in ids)
                    Categories.Assign(id, category);
            });
            ListFilter.Keep(ids);
            Edited(content);
        }
    }

    // After one of a mod's details changed: the rows and the info panel show it.
    public static void Edited(Control content)
    {
        Apply(content);
        if (ScreenOf(content) is { } screen)
            ModDetails.Refresh(screen);
    }

    // The categories to put a mod in (its current one marked), Uncategorized, and a new one. A move keeps its row
    // under a Show: choice (ListFilter.Keep).
    public static IReadOnlyList<MenuItem> CategoryItems(Control content, string id)
    {
        var current = Store.InfoFor(id)?.Category is { } name && Categories.Find(name) != null ? name : null;
        var items = new List<MenuItem> { MenuItem.Heading(Menus.Text("SPIRE_MOD_MANAGER.CATEGORY")) };
        foreach (var category in Store.Data.Categories)
            items.Add(new MenuItem(category.Name, () => Move(category.Name), Checked: category.Name == current, Radio: true));
        items.Add(new MenuItem(Menus.Text("SPIRE_MOD_MANAGER.UNCATEGORIZED"), () => Move(null), Checked: current == null, Radio: true));
        items.Add(MenuItem.Heading(""));
        items.Add(new MenuItem(Menus.Text("SPIRE_MOD_MANAGER.NEW_CATEGORY"), () => NewCategory(content, [id])));
        return items;

        void Move(string? category)
        {
            Categories.Assign(id, category);
            ListFilter.Keep(id);
            Edited(content);
        }
    }

    // New category, from a mod's menu or the bulk one: made with these mods in it as one undo step, their rows kept under
    // a Show: choice (ListFilter.Keep), and its name box opened (at the bottom of the categories: scrolled into view).
    static void NewCategory(Control content, List<string> ids)
    {
        Category? made = null;
        Store.Batch(() =>
        {
            made = Categories.Add();
            foreach (var id in ids)
                Categories.Assign(id, made.Name);
        });
        ListFilter.Keep(ids);
        Edited(content);
        if (made != null && BeginRename(content, made)?.GetParent() is Control header)
            ScrollSoon(content, header, flash: false);
    }

    // Enable all / Disable all / Select all (the mods shown under it: all of them, or the search's matches), then,
    // for the player's own categories, its actions. Uncategorized has only the first three.
    public static Node? ShowHeaderMenu(Control content, Category? category, List<NModMenuRow> members)
    {
        var items = OnOffItems(content, members);
        items.Add(new MenuItem(Menus.Text("SPIRE_MOD_MANAGER.SELECT_ALL"), () => Selection.Add(content, members), Disabled: members.Count == 0));
        if (category != null)
            items.AddRange([MenuItem.Heading(""), .. HeaderActions(content, category)]);
        return Menus.Show(content, items);
    }

    // Enable all / Disable all for some rows (a category's, or the page's), as one undo step; each greyed out
    // when there's nothing for it to do. Disable all leaves this mod on, every copy of it, and says so where it's among
    // them (Jordan: its own box, a selection that has it and the bulk menu can turn it off, Disable all can't). Enable
    // selected / Disable selected for the picked mods, this one too. Each box of these rows, a copy of a mod installed
    // more than once too (Dependencies.Write), so Enable all ticks every one; turning on a second copy of a mod says so
    // (only one copy of each loads).
    public static List<MenuItem> OnOffItems(Control content, IEnumerable<NModMenuRow> members, bool selected = false)
    {
        var mods = members.Select(r => r.Mod).OfType<Mod>().ToList();
        bool Kept(Mod mod) => !selected && mod.manifest?.id == ModEntry.ModId;
        void Set(bool on)
        {
            if (ScreenOf(content) is not { } screen)
                return;
            Dependencies.Set(on ? mods : mods.Where(m => !Kept(m)), on, screen);
            Redraw(content);
        }
        return
        [
            new(Menus.Text(selected ? "SPIRE_MOD_MANAGER.ENABLE_SELECTED" : "SPIRE_MOD_MANAGER.ENABLE_ALL"), () => Set(true),
                Disabled: mods.All(Copies.Ticked), Tip: EnablesCopiesTip(mods)),
            new(Menus.Text(selected ? "SPIRE_MOD_MANAGER.DISABLE_SELECTED" : "SPIRE_MOD_MANAGER.DISABLE_ALL"), () => Set(false),
                Disabled: mods.All(m => !Copies.Ticked(m) || Kept(m)),
                Tip: mods.Any(Kept) ? Menus.Text("SPIRE_MOD_MANAGER.DISABLE_ALL_KEEPS_SELF") : null),
        ];
    }

    // Where turning these mods on leaves two or more copies of an id ticked, one of them turned on by it (copies outside
    // these rows count): a tip naming those mods (without which copy), as only one copy of each loads, this mod too. Null
    // for none.
    static string? EnablesCopiesTip(List<Mod> mods)
    {
        var enabling = mods.Select(Copies.SwitchKey).ToHashSet();
        var named = new List<string>();
        foreach (var turned in mods.Where(m => m.manifest?.id != null && Copies.Others(m).Count > 0 && !Copies.Ticked(m))
            .DistinctBy(m => m.manifest?.id))
        {
            var ticked = Dependencies.EachCopy(Copies.Others(turned).Prepend(turned)).Count(c => enabling.Contains(Copies.SwitchKey(c)) || Copies.Ticked(c));
            if (ticked > 1)
                named.Add(Store.InfoFor(turned)?.Alias ?? turned.manifest?.name ?? turned.manifest?.id ?? "");
        }
        return named.Count == 0 ? null : Menus.Format("SPIRE_MOD_MANAGER.BULK_ENABLES_COPIES_TIP", ("mods", string.Join(", ", named.Distinct())));
    }

    // Rename, up, down, delete: the header's right-click menu, and its inline buttons (compact labels).
    static List<MenuItem> HeaderActions(Control content, Category category, bool compact = false)
    {
        var list = Store.Data.Categories;
        var index = list.IndexOf(category);
        var suffix = compact ? "_SHORT" : "";
        return [
            new MenuItem(Menus.Text("SPIRE_MOD_MANAGER.RENAME"), () => BeginRename(content, category)),
            new MenuItem(Menus.Text("SPIRE_MOD_MANAGER.MOVE_UP" + suffix), () => Edit(() => Categories.Move(category, -1)), Disabled: index == 0),
            new MenuItem(Menus.Text("SPIRE_MOD_MANAGER.MOVE_DOWN" + suffix), () => Edit(() => Categories.Move(category, 1)), Disabled: index == list.Count - 1),
            new MenuItem(Menus.Text("SPIRE_MOD_MANAGER.DELETE"), () => _ = Delete()),
        ];

        void Edit(Action change)
        {
            change();
            Apply(content);
        }

        // Asks first when the category still holds mods; they move to Uncategorized.
        async Task Delete()
        {
            var count = Categories.CountMods(category);
            if (count > 0)
            {
                var body = new LocString("settings_ui", count == 1 ? "SPIRE_MOD_MANAGER.DELETE_CATEGORY_BODY_ONE" : "SPIRE_MOD_MANAGER.DELETE_CATEGORY_BODY");
                body.Add("name", Hover.Escape(category.Name)); // the dialog's text is BBCode; a name can come from a shared list
                body.Add("count", Menus.Number(count));
                if (!await Menus.Confirm(new LocString("settings_ui", "SPIRE_MOD_MANAGER.DELETE_CATEGORY_TITLE"), body))
                    return;
            }
            Edit(() => Categories.Delete(category));
        }
    }

    // Swaps the header's title for a text box; Enter or clicking away renames, Escape cancels.
    public static LineEdit? BeginRename(Control content, Category category)
    {
        if (content.GetChildren().OfType<Control>().FirstOrDefault(c =>
                c.IsInGroup(HeaderGroup) && c.HasMeta(CategoryMeta) && (string)c.GetMeta(CategoryMeta) == category.Name)
            is not { } header)
            return null;
        var title = header.GetNode<Label>("Title");
        var edit = new LineEdit { Name = "RenameBox", Text = category.Name, Flat = true, SelectAllOnFocus = true, MaxLength = 40, ClearButtonEnabled = true };
        edit.AddThemeFontOverride("font", title.GetThemeFont("font"));
        edit.AddThemeFontSizeOverride("font_size", title.GetThemeFontSize("font_size"));
        edit.AddThemeColorOverride("font_color", title.GetThemeColor("font_color"));
        header.AddChild(edit);
        edit.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        edit.OffsetLeft = title.OffsetLeft;
        edit.OffsetRight = title.OffsetRight;
        edit.OffsetTop = 6;
        edit.OffsetBottom = -6;
        title.Visible = false;
        if (header.GetNodeOrNull<Control>("Count") is { } count)
            count.Visible = false; // it sits after the name, under the box (the header made again when the box closes has it)
        edit.GrabFocus();

        // An empty or taken name says so at the box's right end; Enter then does nothing, and clicking away
        // keeps the old name.
        var warning = new Label { Name = "NameWarning", HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore };
        warning.AddThemeFontOverride("font", ModEntry.Font("res://themes/kreon_regular_shared.tres"));
        warning.AddThemeFontSizeOverride("font_size", 15);
        warning.AddThemeColorOverride("font_color", new Color(1f, 0.5f, 0.44f));
        edit.AddChild(warning);
        warning.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        warning.OffsetRight = -34; // clear of the box's ✕
        string? Problem() => Categories.NameProblem(edit.Text, category);
        void Check() => warning.Text = Problem() ?? "";
        edit.TextChanged += _ => Check();

        var done = false;
        void Finish(bool commit)
        {
            if (done)
                return;
            done = true;
            if (commit && Problem() == null)
                Categories.Rename(category, edit.Text);
            Callable.From(() => Apply(content)).CallDeferred(); // not while the box is handling its own signal
        }
        edit.TextSubmitted += _ =>
        {
            if (Problem() == null)
                Finish(true);
        };
        edit.FocusExited += () => Finish(true);
        edit.GuiInput += e =>
        {
            if (e is InputEventKey { Keycode: Key.Escape, Pressed: true })
                Finish(false);
        };
        return edit;
    }

    // The header scene's parts made in code, for when the scene doesn't load (it uses the game's arrow and fonts):
    // the same names, places and colours, with a "▶" for the arrow.
    public static Control PlainHeader()
    {
        var header = new Control
        {
            CustomMinimumSize = new Vector2(0, 56), MouseFilter = Control.MouseFilterEnum.Pass, MouseDefaultCursorShape = Control.CursorShape.PointingHand,
        };
        var background = new ColorRect
        {
            Name = "Background", Color = new Color(0.0756f, 0.12084f, 0.18f, 0.6f), MouseFilter = Control.MouseFilterEnum.Ignore,
            AnchorRight = 1, AnchorBottom = 1,
        };
        header.AddChild(background);
        var arrow = new Label
        {
            Name = "Arrow", Text = "▶", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore, AnchorTop = 0.5f, AnchorBottom = 0.5f,
            OffsetLeft = 12, OffsetTop = -13, OffsetRight = 38, OffsetBottom = 13, PivotOffset = new Vector2(13, 13),
        };
        arrow.AddThemeColorOverride("font_color", new Color(0.937f, 0.784f, 0.318f));
        header.AddChild(arrow);
        var title = new Label
        {
            Name = "Title", VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore, AnchorRight = 1, AnchorBottom = 1, OffsetLeft = 52, OffsetRight = -90,
        };
        title.AddThemeFontOverride("font", ModEntry.Font("res://themes/kreon_bold_shared.tres"));
        title.AddThemeColorOverride("font_color", new Color(0.937f, 0.784f, 0.318f));
        header.AddChild(title);
        var count = new Label
        {
            Name = "Count", HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore, AnchorRight = 1, AnchorBottom = 1, OffsetRight = -16,
        };
        count.AddThemeFontOverride("font", ModEntry.Font("res://themes/kreon_regular_shared.tres"));
        count.AddThemeColorOverride("font_color", new Color(1, 0.964706f, 0.886275f, 0.6f));
        header.AddChild(count);
        return header;
    }

    // A thin gold line marking where the player's categories end and "Uncategorized" begins.
    static Control Divider()
    {
        var divider = new Control { CustomMinimumSize = new Vector2(0, 12), MouseFilter = Control.MouseFilterEnum.Pass };
        var line = new ColorRect { Color = new Color(0.937f, 0.784f, 0.318f, 0.5f), MouseFilter = Control.MouseFilterEnum.Ignore };
        divider.AddChild(line);
        line.SetAnchorsPreset(Control.LayoutPreset.HcenterWide);
        line.OffsetLeft = 24;
        line.OffsetRight = -24;
        line.OffsetTop = -1;
        line.OffsetBottom = 1;
        return divider;
    }

    public const string MissingHintName = "SpireModManagerMissingHint";

    // How many of the profile's mods aren't installed, in faint cream, its [url=missing] opening their list
    // (MissingMods) in our links' colour (no underline; the pointing hand).
    static Control MissingHint(Control content, int count)
    {
        var text = Menus.Format(count == 1 ? "SPIRE_MOD_MANAGER.MISSING_SEARCH_HINT_ONE" : "SPIRE_MOD_MANAGER.MISSING_SEARCH_HINT",
            ("count", Menus.Number(count)));
        var line = new RichTextLabel
        {
            Name = "Line", BbcodeEnabled = true, FitContent = true, ScrollActive = false, SelectionEnabled = false, MetaUnderlined = false,
            MouseFilter = Control.MouseFilterEnum.Pass, // the wheel still scrolls the list
            Text = text.Replace("[url=missing]", $"[url=missing][color={ModDetails.ModLinkHex}]").Replace("[/url]", "[/color][/url]"),
        };
        Hover.Fonts(line, 16);
        line.AddThemeColorOverride("default_color", new Color(1, 0.964706f, 0.886275f, 0.75f));
        line.MetaClicked += meta =>
        {
            if (meta.AsString() == "missing" && ScreenOf(content) is { } screen)
                MissingMods.Show(screen);
        };
        var hint = new MarginContainer { Name = MissingHintName, MouseFilter = Control.MouseFilterEnum.Pass };
        foreach (var (side, room) in new[] { ("margin_left", 16), ("margin_right", 16), ("margin_top", 8), ("margin_bottom", 8) })
            hint.AddThemeConstantOverride(side, room);
        hint.AddChild(line);
        return hint;
    }
}
