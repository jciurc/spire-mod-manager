using Godot;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;
using MegaCrit.Sts2.Core.Saves;

namespace SpireModManager;

// The game's load order is the order of its saved mod list (settings.save mod_list); dependencies still
// load before the mods that need them. This view lists the rows in that order with a position number and a
// drag handle, disabled mods included (greyed by the game, still movable). Moves save the list at once;
// they take effect on relaunch, so they count as pending changes.
static class LoadOrder
{
    const string HandleName = "SpireModManagerHandle";
    const string NumberName = "SpireModManagerNumber";
    const string WasName = "SpireModManagerWas";
    const float WasWidth = 44; // "12 →", kept for every row so the numbers line up
    const float HandleWidth = 84 + WasWidth; // the row title shifts right by this much in this view
    const float LinkGutter = 18; // left strip for the dependency lines
    const float LinkRadius = 5; // their rounded corners (the rows' are RowTitles.CornerRadius)
    const string LinkGroup = "SpireModManagerLinks";
    const string DraggingMeta = "spire_mod_manager_dragging";
    const string ScrollHookMeta = "spire_mod_manager_scroll_hook";
    const string WiredMeta = "spire_mod_manager_order_wired";
    static readonly Color Gold = new(0.937f, 0.784f, 0.318f);
    static readonly Color Blocked = new(0.9f, 0.25f, 0.25f);
    static readonly Color LinkFine = new(0.62f, 0.86f, 0.62f);
    const float DragStart = 6; // px the mouse moves with the button down before a press becomes a drag
    const float EdgeScroll = 60, EdgeScrollSpeed = 700; // drag near an edge: px from it, px per second at the edge
    static List<string>? _loadedOrder;

    // The order this session loaded with, taken once the game has finished its own startup sorting.
    public static void RememberLoadedOrder() => _loadedOrder ??= Order().ToList();

    public static bool OrderChanged => _loadedOrder != null && !_loadedOrder.SequenceEqual(Order());

    static IEnumerable<string> Order() =>
        SaveManager.Instance.SettingsSave.ModSettings?.ModList.Select(m => $"{m.Id}/{m.Source}") ?? [];

    static string Key(Mod? mod) => $"{mod?.manifest?.id}/{mod?.modSource}";

    static int IndexOf(Mod? mod, Dictionary<(string, ModSource), int> places) =>
        mod?.manifest?.id is { } id && places.TryGetValue((id, mod.modSource), out var i) ? i : int.MaxValue;

    // Each saved entry's place in the list (the first, for one listed twice), so sorting looks each mod up once
    // instead of searching the list for it.
    static Dictionary<(string, ModSource), int> Places(List<SettingsSaveMod> list)
    {
        var places = new Dictionary<(string, ModSource), int>();
        for (var i = 0; i < list.Count; i++)
            places.TryAdd((list[i].Id, list[i].Source), i);
        return places;
    }

    // Rows in load order with handles and numbers; `matches` is the search filter.
    public static void Apply(Control content, List<NModMenuRow> rows, Func<NModMenuRow, bool> matches, Action redraw)
    {
        // Scrolling (the wheel too) moves the rows under a still mouse: the hover lines follow.
        if (!content.HasMeta(ScrollHookMeta))
        {
            content.SetMeta(ScrollHookMeta, true);
            content.ItemRectChanged += () =>
            {
                if (Store.Data.LoadOrderView && !content.HasMeta(DraggingMeta))
                    HoverLinks(content);
            };
        }
        var places = Places(SaveManager.Instance.SettingsSave.ModSettings?.ModList ?? []);
        var ordered = rows.OrderBy(r => IndexOf(r.Mod, places)).ToList();
        // Where each one was at launch, among the mods installed now (1-based).
        var installed = ordered.Select(r => Key(r.Mod)).ToHashSet();
        var launch = (_loadedOrder ?? []).Where(installed.Contains).Distinct().Select((key, i) => (key, i)).ToDictionary(p => p.key, p => p.i + 1);
        for (var i = 0; i < ordered.Count; i++)
        {
            var row = ordered[i];
            content.MoveChild(row, i);
            row.Visible = matches(row);
            Decorate(content, row, i + 1, launch.GetValueOrDefault(Key(row.Mod)), redraw);
            OrderingOnly(row, true);
        }
    }

    // Takes the handles and numbers off again (other views).
    public static void Undecorate(IEnumerable<NModMenuRow> rows)
    {
        foreach (var row in rows)
        {
            if (row.GetNodeOrNull(HandleName) is not { } handle)
                continue;
            // Out of the tree now, not at the end of the frame: a second redraw in the same frame must not
            // find them and shift the title back again.
            foreach (var node in new[] { handle, row.GetNodeOrNull(NumberName), row.GetNodeOrNull(WasName) })
                if (node != null)
                {
                    row.RemoveChild(node);
                    node.QueueFree();
                }
            row.GetNode<Control>("Title").OffsetLeft -= HandleWidth;
            OrderingOnly(row, false);
        }
    }

    // Load Order is about order: the whole row is a drag handle (move cursor), no tickbox (Enable/Disable is
    // in the right-click menu), and mods that are off are dimmed further. Off again in other views.
    static void OrderingOnly(NModMenuRow row, bool on)
    {
        row.MouseDefaultCursorShape = on ? Control.CursorShape.Move : Control.CursorShape.Arrow;
        var tickbox = row.GetNode<NTickbox>("Tickbox");
        tickbox.Visible = !on; // on and off are set from the right-click menu here
        tickbox.SetEnabled(!on);
        tickbox.MouseFilter = on ? Control.MouseFilterEnum.Ignore : Control.MouseFilterEnum.Stop;
        row.Modulate = Rest(row);
    }

    // A row's look when nothing is being dragged.
    static Color Rest(NModMenuRow row) =>
        Store.Data.LoadOrderView && row.Mod is { } mod && !Dependencies.IsOn(mod) ? new Color(1, 1, 1, 0.4f) : Colors.White;

    // Moves a mod to a new position among the rows (0 = loads first) and saves the game's settings.
    public static void Move(Mod mod, int to, IList<NModMenuRow> rows)
    {
        var mods = Mods(rows);
        mods.Remove(mod);
        mods.Insert(Math.Clamp(to, 0, mods.Count), mod);
        Save(mods);
    }

    // The installed mods in saved order.
    public static List<Mod> Mods(IEnumerable<NModMenuRow> rows)
    {
        var places = Places(SaveManager.Instance.SettingsSave.ModSettings?.ModList ?? []);
        return rows.Select(r => r.Mod).OfType<Mod>().OrderBy(m => IndexOf(m, places)).ToList();
    }

    // Would putting `mod` at `to` load it before something it needs, or after something that needs it?
    // Only pairs that are both on count, and only pairs involving the moved mod (old problems elsewhere
    // don't block unrelated moves).
    public static bool MoveBreaks(Mod mod, int to, IList<NModMenuRow> rows)
    {
        if (!Dependencies.IsOn(mod))
            return false;
        int Involving(List<Mod> order) => Dependencies.OrderViolations(order).Count(v => v.Mod == mod || v.Needs == mod);
        var order = Mods(rows);
        var before = Involving(order);
        order.Remove(mod);
        order.Insert(Math.Clamp(to, 0, order.Count), mod);
        return Involving(order) > before;
    }

    // Moves each needed mod just ahead of what needs it, keeping everything else in place.
    public static void FixOrder(IList<NModMenuRow> rows) => Save(Dependencies.Fixed(Mods(rows)));

    // Disable (or Enable), then top, up, down, bottom; a move that would break a dependency is disabled.
    // compact: the short labels used for the inline buttons on each row, moves only. placed: the rows in their
    // places, when the caller has them already (every row's buttons reading them again was slow with many mods).
    public static List<MenuItem> Actions(Control content, NModMenuRow row, Action redraw, bool compact = false, List<NModMenuRow>? placed = null)
    {
        var rows = placed ?? content.GetChildren().OfType<NModMenuRow>().ToList();
        var at = rows.IndexOf(row);
        MenuItem Item(string key, int to) => new(Menus.Text(key + (compact ? "_SHORT" : "")), () =>
        {
            if (row.Mod is { } mod)
                Move(mod, to, rows);
            redraw();
        }, Disabled: to < 0 || to >= rows.Count || to == at);
        var moves = new List<MenuItem>
        {
            Item("SPIRE_MOD_MANAGER.MOVE_TO_TOP", 0),
            Item("SPIRE_MOD_MANAGER.MOVE_UP", at - 1),
            Item("SPIRE_MOD_MANAGER.MOVE_DOWN", at + 1),
            Item("SPIRE_MOD_MANAGER.MOVE_TO_BOTTOM", rows.Count - 1),
        };
        if (compact || row.Mod is not { } it)
            return moves;
        // No tickboxes in this view: on and off live here. The game's own handler does the work.
        var on = Dependencies.IsOn(it);
        moves.Insert(0, new MenuItem(Menus.Text(on ? "SPIRE_MOD_MANAGER.DISABLE_MOD" : "SPIRE_MOD_MANAGER.ENABLE_MOD"), () =>
        {
            var tickbox = row.GetNode<NTickbox>("Tickbox");
            tickbox.IsTicked = !on;
            tickbox.EmitSignal(NTickbox.SignalName.Toggled, tickbox);
            redraw();
        }));
        return moves;
    }

    public static Node? ShowRowMenu(Control content, NModMenuRow row, Action redraw) =>
        Menus.Show(content, Actions(content, row, redraw));

    // Above the list, outside its scrolling (so it stays put): Auto sort at the right end of the search
    // box with a word on what it does, and a red banner when something needs attention. In Load Order:
    // enabled mods sitting above mods they need, with "Fix now" (the game reorders those at launch anyway;
    // fixing makes the saved order say what will happen). On the main list: enabled mods that need a mod
    // that is off or missing, with a way to each and a fix for the off ones. Elsewhere, a "!" on the Load
    // Order button says that page has a warning. Above that one on both, while Better Mod Menu is ticked too: a
    // banner saying two mod managers are on, with a way to its row and a way to dismiss it.
    public const string WarningName = "SpireModManagerOrderWarning", ManagersName = "SpireModManagerManagersWarning", AutoSortName = "AutoSortButton";
    public const string AutoSortHintName = "AutoSortHint";
    const string BadgeName = "WarningBadge";
    static int _nextProblem; // the main banner's "Show" steps through the mods with problems

    public static void UpdateToolbar(Control content, List<NModMenuRow> rows, Action redraw)
    {
        using var lookups = Lookups.Begin(); // the warnings ask about every mod (also when called on its own)
        var border = content.GetParent().GetParent().GetParent<Control>();
        var search = border.GetNodeOrNull<Control>("SpireModManagerSearch");
        void Do(Action<IList<NModMenuRow>> change)
        {
            change(content.GetChildren().OfType<NModMenuRow>().ToList());
            redraw();
        }

        var on = Store.Data.LoadOrderView;
        if (border.GetNodeOrNull<Button>(AutoSortName) is not { } sort)
        {
            sort = Menus.SmallButton(Menus.Text("SPIRE_MOD_MANAGER.AUTO_SORT"), () => Do(AutoSort), name: AutoSortName);
            border.AddChild(sort);
            sort.AnchorLeft = sort.AnchorRight = 1; // offsets from the right edge, like the search box's right end
        }
        if (border.GetNodeOrNull<Label>(AutoSortHintName) is not { } hint)
        {
            hint = new Label
            {
                Name = AutoSortHintName, Text = Menus.Text("SPIRE_MOD_MANAGER.AUTO_SORT_HINT"), HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            hint.AddThemeFontOverride("font", ModEntry.Font("res://themes/kreon_regular_shared.tres"));
            hint.AddThemeFontSizeOverride("font_size", 15);
            hint.AddThemeColorOverride("font_color", new Color(1, 0.964706f, 0.886275f, 0.55f));
            border.AddChild(hint);
            hint.AnchorLeft = hint.AnchorRight = 1;
        }
        sort.Visible = hint.Visible = on && search != null;
        // Across here; up and down they follow the search box, in Layout.PlaceList (at the end), once it's in its place
        // for this view. (Read from it here, on the way into Load Order it was still where the main list has it, under
        // the profile bar, and the hint sat over the first row until the next redraw.)
        if (search != null)
        {
            const float sortWidth = 110, gap = 8;
            var hintWidth = hint.GetThemeFont("font").GetStringSize(hint.Text, fontSize: 15).X;
            sort.OffsetRight = border.GetNode<Control>("ModsScrollContainer").OffsetRight;
            sort.OffsetLeft = sort.OffsetRight - sortWidth;
            hint.OffsetRight = sort.OffsetLeft - gap;
            hint.OffsetLeft = hint.OffsetRight - hintWidth;
            search.OffsetRight = on ? hint.OffsetLeft - gap * 2 : sort.OffsetRight;
        }

        var mods = Mods(rows);
        var misordered = Dependencies.OrderViolations(mods).Select(v => v.Mod).Distinct().Count();
        var orderText = Menus.Format(misordered == 1 ? "SPIRE_MOD_MANAGER.ORDER_PROBLEMS_ONE" : "SPIRE_MOD_MANAGER.ORDER_PROBLEMS", ("count", misordered.ToString()));
        Badge(content, misordered > 0 && !on ? orderText : null);
        foreach (var name in new[] { WarningName, ManagersName })
            if (border.GetNodeOrNull(name) is { } old)
            {
                border.RemoveChild(old); // now, not at the end of the frame: a redraw in the same frame adds a new one
                old.QueueFree();
            }
        // Better Mod Menu and ours both ticked, on our views, unless dismissed. A tickbox change comes through here
        // (RefreshStrips), so the banner shows and goes at once, without a relaunch. Dismissed, it comes back once the
        // two aren't both ticked (in any view), ready for the next time they are: tickboxes, profiles, undo and redo, and
        // the fixes all redraw through here.
        Bmm.ForgetDismissedWarning();
        if (Layout.Wide && Bmm.BothTicked && !Store.Data.ManagersWarningDismissed)
            ManagersBanner(content, border, search, redraw);
        if (on && misordered > 0)
            Banner(border, search, orderText, [Menus.SmallButton(Menus.Text("SPIRE_MOD_MANAGER.FIX_ORDER"), () => Do(FixOrder), name: "FixOrderButton")]);
        else if (Layout.Current == Layout.View.Categories)
            ListBanner(content, border, search, mods, redraw);
        PageBar.Update(content, border, rows);
        Layout.PlaceList(border);
    }

    // Main list: enabled mods that won't load as things stand.
    static void ListBanner(Control content, Control border, Control? search, List<Mod> mods, Action redraw)
    {
        bool Needs(Mod mod, Func<Mod?, bool> problem) => Dependencies.IsOn(mod) && Dependencies.Of(mod).Any(id => problem(Dependencies.Find(id)));
        var needOff = mods.Where(m => Needs(m, d => d != null && !Dependencies.IsOn(d))).ToList();
        var needMissing = mods.Where(m => Needs(m, d => d == null)).ToList();
        var broken = needOff.Concat(needMissing).Distinct().ToList();
        if (broken.Count == 0)
            return;
        string Part(string key, int count) => Menus.Format(count == 1 ? key + "_ONE" : key, ("count", count.ToString()));
        var text = string.Join(" ", new[]
        {
            needOff.Count > 0 ? Part("SPIRE_MOD_MANAGER.LIST_PROBLEMS_OFF", needOff.Count) : null,
            needMissing.Count > 0 ? Part("SPIRE_MOD_MANAGER.LIST_PROBLEMS_MISSING", needMissing.Count) : null,
        }.OfType<string>());

        var show = Menus.SmallButton(Menus.Text("SPIRE_MOD_MANAGER.SHOW_PROBLEM"), () => GoTo(content, broken[_nextProblem++ % broken.Count]),
            name: "ShowProblemButton");
        show.TooltipText = Menus.Text("SPIRE_MOD_MANAGER.SHOW_PROBLEM_TIP");
        List<Control> buttons = [show];
        var needed = Dependencies.NeededOff(mods);
        if (needed.Count > 0)
        {
            var enable = Menus.SmallButton(Menus.Text("SPIRE_MOD_MANAGER.ENABLE_NEEDED"), () =>
            {
                if (CategoryList.ScreenOf(content) is not { } screen)
                    return;
                Dependencies.Set(needed, true, screen);
                redraw();
            }, name: "EnableNeededButton");
            enable.TooltipText = Menus.Format("SPIRE_MOD_MANAGER.ENABLE_NEEDED_TIP",
                ("mods", string.Join(", ", needed.Select(m => m.manifest?.name ?? m.manifest?.id))));
            buttons.Add(enable);
        }
        Banner(border, search, text, buttons);
    }

    // Better Mod Menu ticked as well as us: the same kind of banner and Show, which goes to its row, then Dismiss for
    // a player happy to run both.
    static void ManagersBanner(Control content, Control border, Control? search, Action redraw)
    {
        var show = Menus.SmallButton(Menus.Text("SPIRE_MOD_MANAGER.SHOW_PROBLEM"), () =>
        {
            if (Dependencies.Find(Bmm.ModId) is { } mod)
                GoTo(content, mod);
        }, name: "ShowManagersButton");
        show.TooltipText = Menus.Text("SPIRE_MOD_MANAGER.SHOW_MANAGERS_TIP");
        var dismiss = Menus.SmallButton(Menus.Text("SPIRE_MOD_MANAGER.DISMISS_MANAGERS"), () =>
        {
            Store.Data.ManagersWarningDismissed = true;
            Store.Save(); // view state: no undo step, not a change (Store.EditsJson)
            redraw();
        }, name: "DismissManagersButton");
        dismiss.TooltipText = Menus.Text("SPIRE_MOD_MANAGER.DISMISS_MANAGERS_TIP");
        Banner(border, search, Menus.Text("SPIRE_MOD_MANAGER.MANAGERS_WARNING"), [show, dismiss], ManagersName, "ManagersWarning");
    }

    // A banner's Show: the mod's row scrolled into view (its category opened) with a short glow, and the mod in the
    // info panel. A search that leaves the row out is cleared first: opening its category wouldn't show it.
    static void GoTo(Control content, Mod mod)
    {
        if (content.GetChildren().OfType<NModMenuRow>().FirstOrDefault(r => r.Mod == mod) is not { } row)
            return;
        var filter = content.HasMeta(CategoryList.FilterMeta) ? (string)content.GetMeta(CategoryList.FilterMeta) : "";
        if (filter.Length > 0 && !Search.Matches(mod, filter))
            Search.Clear(content);
        CategoryList.Reveal(content, mod.manifest?.id ?? "", flash: true);
        CategoryList.Select(content, row);
    }

    // A red line under the search box: text, then buttons on the right. name and textName: the line's and its text's
    // node names (the page's own warning by default).
    static void Banner(Control border, Control? search, string message, List<Control> buttons, string name = WarningName, string textName = "OrderProblems")
    {
        var bar = new PanelContainer { Name = name };
        bar.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.45f, 0.1f, 0.1f, 0.6f), ContentMarginLeft = 12, ContentMarginRight = 8, ContentMarginTop = 4, ContentMarginBottom = 4,
        });
        var line = new HBoxContainer();
        line.AddThemeConstantOverride("separation", 6);
        bar.AddChild(line);
        var text = new Label
        {
            Name = textName, Text = message, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            VerticalAlignment = VerticalAlignment.Center, AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        text.AddThemeFontOverride("font", ModEntry.Font("res://themes/kreon_regular_shared.tres"));
        text.AddThemeFontSizeOverride("font_size", 16);
        line.AddChild(text);
        foreach (var button in buttons)
        {
            button.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            line.AddChild(button);
        }
        border.AddChild(bar);
        bar.SetAnchorsPreset(Control.LayoutPreset.TopWide);
        var scroll = border.GetNode<Control>("ModsScrollContainer");
        (bar.OffsetLeft, bar.OffsetRight) = (scroll.OffsetLeft, scroll.OffsetRight);
        var top = (search ?? scroll).OffsetBottom + 6;
        (bar.OffsetTop, bar.OffsetBottom) = (top, top + 34);
    }

    // The "!" on the Load Order button, with the page's warning as its tooltip; null takes it off.
    static void Badge(Control content, string? warning)
    {
        if (CategoryList.ScreenOf(content)?.GetNodeOrNull<Control>("SpireModManagerLoadOrderToggle") is not { } toggle)
            return;
        toggle.TooltipText = warning ?? "";
        if (toggle.GetNodeOrNull<Control>(BadgeName) is not { } badge)
        {
            badge = new PanelContainer { Name = BadgeName, MouseFilter = Control.MouseFilterEnum.Ignore, Size = new Vector2(22, 22) };
            var style = new StyleBoxFlat { BgColor = Blocked, BorderColor = new Color(0.1f, 0.02f, 0.02f, 0.9f) };
            style.SetBorderWidthAll(2);
            style.SetCornerRadiusAll(11);
            badge.AddThemeStyleboxOverride("panel", style);
            var mark = new Label
            {
                Text = "!", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            mark.AddThemeFontOverride("font", ModEntry.Font("res://themes/kreon_bold_shared.tres"));
            mark.AddThemeFontSizeOverride("font_size", 16);
            mark.AddThemeColorOverride("font_color", Colors.White);
            badge.AddChild(mark);
            toggle.AddChild(badge);
            badge.Position = new Vector2(toggle.Size.X - 26, 2); // the top right corner, inside the slanted edge
        }
        badge.Visible = warning != null;
    }

    // First pass at sorting by impact: libraries (mods others depend on), then gameplay mods, then
    // visual-only mods, keeping the player's order within each tier; then dependencies move ahead of what
    // needs them. One undoable step. (Later: finer tiers, e.g. content vs QoL, from Workshop tags.)
    public static void AutoSort(IList<NModMenuRow> rows)
    {
        var mods = Mods(rows);
        int Tier(Mod mod) =>
            ModManager.Mods.Any(m => m != mod && Dependencies.Of(m).Contains(mod.manifest?.id ?? "")) ? 0
            : mod.manifest?.affectsGameplay ?? true ? 1
            : 2;
        Save(Dependencies.Fixed(mods.OrderBy(Tier).ToList())); // OrderBy is stable: same-tier mods keep their order
    }

    // Writes a whole new order: installed mods as given, then entries for mods that aren't installed.
    static void Save(List<Mod> mods)
    {
        if (SaveManager.Instance.SettingsSave.ModSettings is not { } settings)
            return;
        var list = settings.ModList;
        var entries = mods.Select(m => list.FirstOrDefault(e => e.Id == m.manifest?.id && e.Source == m.modSource) ?? new SettingsSaveMod(m)).ToList();
        entries.AddRange(list.Where(e => !entries.Contains(e)));
        list.Clear();
        list.AddRange(entries);
        SaveManager.Instance.SaveSettings();
        History.Record();
    }

    // Position number and a grip on the left of the row. Dragging the row (anywhere, not just the grip) moves
    // it: a gold line shows where it will land, and releasing saves. Made on each visit to Load Order
    // (Undecorate takes them off again); the number updates every redraw.
    // wasAt: where it was at launch (0: unknown); shown dimly as "5 →" when it moved.
    static void Decorate(Control content, NModMenuRow row, int position, int wasAt, Action redraw)
    {
        var was = wasAt > 0 && wasAt != position ? $"{wasAt} →" : "";
        if (row.GetNodeOrNull<Label>(NumberName) is { } number)
        {
            number.Text = position.ToString();
            row.GetNode<Label>(WasName).Text = was;
            return;
        }
        row.GetNode<Control>("Title").OffsetLeft += HandleWidth;

        // Left to right: the link gutter (dependency lines), the grip, the launch position (if it moved), the
        // position now, then the title.
        var wasLabel = new Label
        {
            Name = WasName, Text = was, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore, AnchorBottom = 1, OffsetLeft = LinkGutter + 30, OffsetRight = LinkGutter + 30 + WasWidth,
        };
        wasLabel.AddThemeColorOverride("font_color", new Color(Gold, 0.4f));
        wasLabel.AddThemeFontSizeOverride("font_size", 15);
        row.AddChild(wasLabel);
        number = new Label
        {
            Name = NumberName, Text = position.ToString(), HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore,
            AnchorBottom = 1, OffsetLeft = LinkGutter + 30 + WasWidth, OffsetRight = LinkGutter + 56 + WasWidth,
        };
        number.AddThemeColorOverride("font_color", new Color(Gold, 0.8f));
        number.AddThemeFontSizeOverride("font_size", 20);
        row.AddChild(number);

        // Three short bars: the usual "grab here" grip (the whole row drags; see below).
        var handle = new Control
        {
            Name = HandleName, AnchorBottom = 1, OffsetLeft = LinkGutter, OffsetRight = LinkGutter + 30,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        for (var i = 0; i < 3; i++)
            handle.AddChild(new ColorRect
            {
                Color = new Color(Gold, 0.7f), AnchorTop = 0.5f, AnchorBottom = 0.5f,
                OffsetLeft = 6, OffsetRight = 24, OffsetTop = -9 + i * 8, OffsetBottom = -6 + i * 8,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            });
        row.AddChild(handle);

        // The handlers below are connected once per row: rows outlive this view, and the grip and numbers above
        // come back on every visit (connecting again would start one drag per visit).
        if (row.HasMeta(WiredMeta))
            return;
        row.SetMeta(WiredMeta, true);

        // Hover: show how this mod is linked to others (Load Order only; rows outlive the view).
        row.MouseEntered += () =>
        {
            if (Store.Data.LoadOrderView && !content.HasMeta(DraggingMeta))
                ShowLinks(content, row, strong: false);
        };
        row.MouseExited += () =>
        {
            if (!content.HasMeta(DraggingMeta))
                HideLinks(content);
        };

        // The whole row is the drag handle (the grip just shows it). A press picks the row and starts the drag
        // at once (lit links, the drop line); the drop saves. Ctrl and Shift only change the selection. A
        // press and release without moving is a click: on a row of a multi-selection it picks just that row.
        // Rows outlive this view, so the handler does nothing outside Load Order.
        var dragging = false;
        var moved = false; // past a few pixels: a drag, not a click
        var pressAt = Vector2.Zero;
        var mouseY = 0f; // where the last mouse event put the pointer
        Panel? marker = null;
        row.GuiInput += e =>
        {
            if (!Store.Data.LoadOrderView)
                return;
            switch (e)
            {
                case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } modified
                    when modified.CtrlPressed || modified.ShiftPressed || modified.MetaPressed:
                    Selection.Pick(content, row, toggle: modified.CtrlPressed || modified.MetaPressed, range: modified.ShiftPressed);
                    row.AcceptEvent();
                    break;
                case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } press:
                    pressAt = press.GlobalPosition;
                    mouseY = press.GlobalPosition.Y;
                    if (!Selection.Contains(row.Mod))
                        Selection.Pick(content, row, toggle: false, range: false);
                    CategoryList.Select(content, row); // grabbing a row selects it too
                    dragging = true;
                    moved = false;
                    marker = new Panel { SelfModulate = Gold, Size = new Vector2(content.Size.X, 5), MouseFilter = Control.MouseFilterEnum.Ignore };
                    var bar = new StyleBoxFlat { BgColor = Colors.White }; // coloured by SelfModulate: gold, or red (DragFrame)
                    bar.SetCornerRadiusAll(2); // slightly rounded ends, like the rows
                    marker.AddThemeStyleboxOverride("panel", bar);
                    content.GetParent().AddChild(marker); // the scroll mask: not a container, so it can sit anywhere
                    content.GetTree().ProcessFrame += DragFrame;
                    BeginDragLook(content, row);
                    row.AcceptEvent(); // not a scroll of the list
                    break;
                case InputEventMouseMotion motion when dragging:
                    mouseY = motion.GlobalPosition.Y;
                    moved |= motion.GlobalPosition.DistanceTo(pressAt) > DragStart;
                    row.AcceptEvent(); // the marker, lines and edge scrolling update every frame (DragFrame)
                    break;
                case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false } release when dragging:
                    dragging = false;
                    row.AcceptEvent();
                    content.GetTree().ProcessFrame -= DragFrame;
                    marker?.QueueFree();
                    marker = null;
                    EndDragLook(content);
                    if (!moved)
                    {
                        Selection.Pick(content, row, toggle: false, range: false);
                        ShowLinks(content, row, strong: false); // still hovering it
                        break;
                    }
                    var rows = content.GetChildren().OfType<NModMenuRow>().ToList();
                    // Drops are allowed even where they break a dependency: the red line and the warning bar say so.
                    // A multi-selection moves as one block.
                    var gapAt = Target(content, release.GlobalPosition.Y).Index;
                    if (Selection.Several && Selection.Contains(row.Mod))
                    {
                        MoveGroup(Selection.Mods, gapAt, rows);
                        redraw();
                    }
                    else if (Destination(gapAt) is { } to && row.Mod is { } mod)
                    {
                        Move(mod, to, rows);
                        redraw();
                    }
                    break;
            }
        };

        // Every frame while dragging: near the top or bottom edge the list scrolls, faster the closer the
        // mouse is to the edge (smooth, and also while the mouse is still); the marker follows the gap under
        // the mouse.
        void DragFrame()
        {
            if (!GodotObject.IsInstanceValid(content) || !content.IsInsideTree() || marker == null)
            {
                ((SceneTree)Engine.GetMainLoop()).ProcessFrame -= DragFrame;
                return;
            }
            var view = content.GetParent<Control>().GetGlobalRect();
            var depth = Math.Max(view.Position.Y + EdgeScroll - mouseY, 0) - Math.Max(mouseY - (view.End.Y - EdgeScroll), 0);
            if (depth != 0)
                CategoryList.ScrollBy(content, Math.Clamp(depth / EdgeScroll, -1.5f, 1.5f) * EdgeScrollSpeed * (float)content.GetProcessDeltaTime());

            var (gap, y) = Target(content, mouseY);
            // Kept inside the list's visible area (it clips), so the gap above the first row still shows.
            marker.GlobalPosition = new Vector2(content.GlobalPosition.X, Math.Clamp(y - 2, view.Position.Y, view.End.Y - 5));
            ShowLinks(content, row, strong: true, drop: (gap, marker.GlobalPosition.Y + 2));
            // Red where dropping would load it before something it needs (or after something needing it).
            marker.SelfModulate = Destination(gap) is { } at && row.Mod is { } moving
                && MoveBreaks(moving, at, content.GetChildren().OfType<NModMenuRow>().ToList()) ? Blocked : Gold;
        }

        // The index the row would take after leaving its slot, or null for its own gap.
        int? Destination(int gap)
        {
            var from = content.GetChildren().OfType<NModMenuRow>().ToList().IndexOf(row);
            return gap == from || gap == from + 1 ? null : gap > from ? gap - 1 : gap;
        }
    }


    // Moves a group to a gap (index among all rows, as Target gives), keeping their relative order.
    public static void MoveGroup(IReadOnlyCollection<Mod> group, int gap, IList<NModMenuRow> rows)
    {
        var order = Mods(rows);
        var moving = order.Where(group.Contains).ToList();
        var rest = order.Where(m => !group.Contains(m)).ToList();
        var shown = rows.Select(r => r.Mod).OfType<Mod>().ToList();
        // Rest-of-list position: the unpicked rows above the gap.
        var insertAt = shown.Take(gap).Count(m => !group.Contains(m));
        rest.InsertRange(Math.Clamp(insertAt, 0, rest.Count), moving);
        Save(rest);
    }

    // Mods linked to this one by a dependency, either way, among the shown rows.
    static List<NModMenuRow> Linked(Control content, NModMenuRow row)
    {
        if (row.Mod?.manifest?.id is not { } id)
            return [];
        var needs = Dependencies.Of(row.Mod).ToHashSet();
        return content.GetChildren().OfType<NModMenuRow>()
            .Where(r => r != row && r.Visible && r.Mod?.manifest?.id is { } other
                && (needs.Contains(other) || Dependencies.Of(r.Mod).Contains(id)))
            .ToList();
    }

    // Tree-style lines in the left gutter from this row to each linked row: out from the row, along
    // the gutter, into the other row, with slightly rounded corners. Each link gets its own lane so they don't overlap.
    // While dragging, `drop` is where the row would land (gap index, screen y): its end of each line follows
    // the drop marker, stretching and shrinking, and the colour says whether that drop works.
    public static void ShowLinks(Control content, NModMenuRow row, bool strong, (int Gap, float Y)? drop = null)
    {
        HideLinks(content);
        var mask = content.GetParent<Control>();
        float CenterY(Control r) => r == row && drop is { } d ? d.Y - mask.GlobalPosition.Y : r.GetGlobalRect().GetCenter().Y - mask.GlobalPosition.Y;
        var linked = Linked(content, row);
        var rows = content.GetChildren().OfType<NModMenuRow>().ToList();
        for (var i = 0; i < linked.Count; i++)
        {
            // Green when the pair is in a working order (what's needed loads first), red when it isn't.
            var needed = Dependencies.Of(row.Mod).Contains(linked[i].Mod?.manifest?.id ?? "");
            var other = rows.IndexOf(linked[i]);
            var fine = drop is { } at
                ? needed ? other < at.Gap : other >= at.Gap
                : needed ? other < rows.IndexOf(row) : other > rows.IndexOf(row);
            var lane = 4 + Math.Min(i, 3) * 3;
            var line = new Line2D
            {
                Width = strong ? 3 : 2, DefaultColor = new Color(fine ? LinkFine : Blocked, strong ? 1f : 0.6f),
                Points = Rounded([new(LinkGutter, CenterY(row)), new(lane, CenterY(row)), new(lane, CenterY(linked[i])), new(LinkGutter, CenterY(linked[i]))], LinkRadius),
            };
            line.AddToGroup(LinkGroup);
            mask.AddChild(line);
        }
    }

    // A path with each corner cut by a short curve (radius at most half of each side, so curves never overlap);
    // the ends stay put. A few points per corner: the lines are made again on every hover and dragged frame.
    static Vector2[] Rounded(Vector2[] path, float radius)
    {
        const int steps = 4;
        var points = new List<Vector2> { path[0] };
        for (var i = 1; i < path.Length - 1; i++)
        {
            var (before, corner, after) = (path[i - 1], path[i], path[i + 1]);
            var r = Math.Min(radius, Math.Min(corner.DistanceTo(before), corner.DistanceTo(after)) / 2);
            if (r < 0.5f)
            {
                points.Add(corner); // no room (a drag can put both ends level): square
                continue;
            }
            var (from, to) = (corner + (before - corner).Normalized() * r, corner + (after - corner).Normalized() * r);
            for (var s = 0; s <= steps; s++)
            {
                var t = (float)s / steps;
                points.Add(from.Lerp(corner, t).Lerp(corner.Lerp(to, t), t)); // a curve from side to side, pulled toward the corner
            }
        }
        points.Add(path[^1]);
        return [.. points];
    }

    // Lines for whichever row is under the mouse now, or none.
    static void HoverLinks(Control content)
    {
        var mouse = content.GetGlobalMousePosition();
        var mask = content.GetParent<Control>().GetGlobalRect();
        if (mask.HasPoint(mouse) && content.GetChildren().OfType<NModMenuRow>().FirstOrDefault(r => r.Visible && r.GetGlobalRect().HasPoint(mouse)) is { } row)
            ShowLinks(content, row, strong: false);
        else
            HideLinks(content);
    }

    public static void HideLinks(Control content)
    {
        var mask = content.GetParent();
        foreach (var line in mask.GetChildren().Where(c => c.IsInGroup(LinkGroup)).ToList())
        {
            mask.RemoveChild(line); // now: they are redrawn every frame while dragging
            line.QueueFree();
        }
    }

    // While dragging: the list dims, the dragged row and every row linked to it stay lit, links drawn strong.
    static void BeginDragLook(Control content, NModMenuRow row)
    {
        content.SetMeta(DraggingMeta, true);
        var rows = content.GetChildren().OfType<NModMenuRow>().ToList();
        // Everything being moved (the row, or the whole multi-selection) plus whatever they're linked to.
        var moving = rows.Where(r => r == row || (Selection.Several && Selection.Contains(r.Mod))).ToList();
        var lit = moving.SelectMany(r => Linked(content, r)).Concat(moving).ToHashSet();
        foreach (var r in rows)
            r.Modulate = lit.Contains(r) ? Colors.White : new Color(1, 1, 1, 0.35f);
        foreach (var r in moving)
            r.Modulate = new Color(1.25f, 1.2f, 1.05f); // the ones being moved, a touch brighter
        ShowLinks(content, row, strong: true);
    }

    static void EndDragLook(Control content) => Reset(content);

    // Back to a plain list: no dimming, no lines. Also used when leaving Load Order.
    public static void Reset(Control content)
    {
        if (content.HasMeta(DraggingMeta))
            content.RemoveMeta(DraggingMeta);
        foreach (var r in content.GetChildren().OfType<NModMenuRow>())
            r.Modulate = Rest(r);
        HideLinks(content);
    }

    // The gap the mouse is nearest: the index a dropped row would take, and that gap's screen y.
    static (int Index, float Y) Target(Control content, float mouseY)
    {
        var rows = content.GetChildren().OfType<NModMenuRow>().Where(r => r.Visible).ToList();
        for (var i = 0; i < rows.Count; i++)
        {
            var rect = rows[i].GetGlobalRect();
            if (mouseY < rect.GetCenter().Y)
                return (content.GetChildren().OfType<NModMenuRow>().ToList().IndexOf(rows[i]), rect.Position.Y - 4);
        }
        var last = rows.LastOrDefault()?.GetGlobalRect();
        return (content.GetChildren().OfType<NModMenuRow>().Count(), last is { } r ? r.End.Y + 4 : content.GlobalPosition.Y);
    }
}
