using Godot;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;
using MegaCrit.Sts2.Core.Saves;

namespace SpireModManager;

// The Load Order view: rows in the saved mod_list order (the game's load order; dependencies still load first), with
// a number and a drag handle, disabled mods included. Moves save at once and apply on relaunch.
static class LoadOrder
{
    const string HandleName = "SpireModManagerHandle";
    const string NumberName = "SpireModManagerNumber";
    const string WasName = "SpireModManagerWas";
    const float WasWidth = 44; // "12 →", kept for every row so the numbers line up
    const float HandleWidth = 84 + WasWidth; // the row title shifts right by this much in this view
    const float LinkGutter = 18; // left strip for the dependency lines
    const float LinkRadius = 5; // their rounded corners (the rows' are RowTitles.CornerRadius)
    // Soft edges for the lines, drawn across their width: Line2D's own Antialiased does nothing in the game's renderer,
    // so the line is a pixel wider and fades out at both edges.
    static GradientTexture2D? _linkEdges;
    static GradientTexture2D LinkEdges => _linkEdges ??= new GradientTexture2D
    {
        Width = 4, Height = 32, FillFrom = new Vector2(0, 0), FillTo = new Vector2(0, 1),
        Gradient = new Gradient { Offsets = [0f, 0.3f, 0.7f, 1f], Colors = [new Color(1, 1, 1, 0), Colors.White, Colors.White, new Color(1, 1, 1, 0)] },
    };
    const string LinkGroup = "SpireModManagerLinks";
    const string DraggingMeta = "spire_mod_manager_dragging";
    const string ScrollHookMeta = "spire_mod_manager_scroll_hook";
    const string WiredMeta = "spire_mod_manager_order_wired";
    static readonly Color Gold = new(0.937f, 0.784f, 0.318f);
    static readonly Color Blocked = new(0.9f, 0.25f, 0.25f);
    static readonly Color LinkFine = new(0.62f, 0.86f, 0.62f);
    // The banners' fills: a deep red for a problem with the mods, a deep amber (the game's gold, darkened) for a warning.
    // Dark enough for the cream text to read on both (about 4.8:1 for the amber over the page).
    public static readonly Color ProblemColor = new(0.45f, 0.1f, 0.1f, 0.6f), WarningColor = new(0.6f, 0.44f, 0.06f, 0.9f);
    const float DragStart = 6; // px the mouse moves with the button down before a press becomes a drag
    const float EdgeScroll = 60, EdgeScrollSpeed = 700; // drag near an edge: px from it, px per second at the edge
    static List<(string? Id, ModSource Source)>? _loadedOrder;

    // The order this session loaded with, taken once the game has finished its own startup sorting.
    public static void RememberLoadedOrder() => _loadedOrder ??= Order().ToList();

    // Moved since launch, as Changes counts it (↕): each installed copy once, by its own place (MovedSince). Not the raw
    // saved list, which can hold repeats and entries for mods that aren't installed: those made the main menu ask for a
    // relaunch while Changes showed nothing.
    public static bool OrderChanged => HistoryBar.Moves().Count > 0;

    static IEnumerable<(string? Id, ModSource Source)> Order() =>
        SaveManager.Instance.SettingsSave.ModSettings?.ModList.Select(m => ((string?)m.Id, m.Source)) ?? [];

    static int IndexOf(Mod? mod, Dictionary<Mod, int> places) => mod != null && places.TryGetValue(mod, out var i) ? i : int.MaxValue;

    // Each installed copy's place in the saved list now, looked up once per sort.
    public static Dictionary<Mod, int> Places(IEnumerable<Mod> mods) => Places(Order(), mods, CopyOrder);

    // Each of these copies' place in a saved list (its index there; none: not listed). The game's entries can't tell two
    // copies of one id and source apart (two Workshop items of one mod), so they take that key's entries in turn: in the
    // order they were last saved (asSaved: a copy order, CopyOrder's or a snapshot's), else as the game lists them. A
    // copy the game lists twice (found at launch, and again on an update) is one.
    public static Dictionary<Mod, int> Places(IEnumerable<(string? Id, ModSource Source)> saved, IEnumerable<Mod> mods,
        IReadOnlyDictionary<(string?, ModSource), IReadOnlyList<string>>? asSaved = null)
    {
        var free = new Dictionary<(string?, ModSource), Queue<int>>();
        var i = 0;
        foreach (var key in saved)
        {
            if (!free.TryGetValue(key, out var queue))
                free[key] = queue = new Queue<int>();
            queue.Enqueue(i++);
        }
        var wanted = mods.ToList();
        var byCopy = new Dictionary<(string?, ModSource, string), int>();
        foreach (var group in Copied(wanted))
        {
            var order = asSaved?.GetValueOrDefault(group.Key) ?? [];
            foreach (var copy in group.OrderBy(m => Turn(order, Copies.CopyOf(m)))) // stable: the rest as listed
                if (free.TryGetValue(group.Key, out var queue) && queue.TryDequeue(out var at))
                    byCopy[(group.Key.id, group.Key.modSource, Copies.CopyOf(copy))] = at;
        }
        var places = new Dictionary<Mod, int>();
        foreach (var mod in wanted)
            if (byCopy.TryGetValue((mod.manifest?.id, mod.modSource, Copies.CopyOf(mod)), out var at))
                places[mod] = at;
        return places;
    }

    // Every installed copy (and these) once, by id and source, as the game lists them.
    static IEnumerable<IGrouping<(string? id, ModSource modSource), Mod>> Copied(IEnumerable<Mod> mods) =>
        Dependencies.EachCopy(ModManager.Mods.Union(Installed.Mods).Concat(mods)).GroupBy(m => (m.manifest?.id, m.modSource));

    // A copy's place in a copy order (one it doesn't name: after those it does).
    static int Turn(IReadOnlyList<string> order, string copy)
    {
        for (var k = 0; k < order.Count; k++)
            if (order[k] == copy)
                return k;
        return int.MaxValue;
    }

    // The order this session last saved the copies of each id and source in (Save), for Places: the drag of one copy
    // past another stays where it was dropped, though the game's entries are alike. Part of the order as undo knows it
    // (History's snapshots hold it beside the saved list, and an undo, redo or Revert all puts it back), so a drop that
    // only swaps two copies is a step of its own. Each save puts in a new list, so a snapshot can share them.
    static readonly Dictionary<(string?, ModSource), IReadOnlyList<string>> CopyOrder = [];

    // None saved: every copy in the game's order, as at launch.
    public static readonly IReadOnlyDictionary<(string?, ModSource), IReadOnlyList<string>> NoCopyOrder =
        new Dictionary<(string?, ModSource), IReadOnlyList<string>>();

    // The copy order now, for a snapshot (none at launch: the copies as the game lists them).
    public static IReadOnlyDictionary<(string?, ModSource), IReadOnlyList<string>> CopyOrderNow() =>
        CopyOrder.Count == 0 ? NoCopyOrder : new Dictionary<(string?, ModSource), IReadOnlyList<string>>(CopyOrder);

    // A snapshot's copy order back (History: undo, redo, Revert all).
    public static void RestoreCopyOrder(IReadOnlyDictionary<(string?, ModSource), IReadOnlyList<string>> order)
    {
        CopyOrder.Clear();
        foreach (var (key, copies) in order)
            CopyOrder[key] = copies;
    }

    // Whether two copy orders put the installed copies of each id and source in the same turn: one can leave a key to
    // the game's order that the other names in that same order.
    public static bool SameCopyOrder(IReadOnlyDictionary<(string?, ModSource), IReadOnlyList<string>> a,
        IReadOnlyDictionary<(string?, ModSource), IReadOnlyList<string>> b)
    {
        if (a.Count == 0 && b.Count == 0)
            return true; // most players: no copies saved in an order of their own
        var keys = a.Keys.Union(b.Keys).ToHashSet();
        foreach (var group in Copied([]).Where(g => keys.Contains(g.Key)))
        {
            var copies = group.Select(Copies.CopyOf).ToList();
            var (inA, inB) = (a.GetValueOrDefault(group.Key) ?? [], b.GetValueOrDefault(group.Key) ?? []);
            if (!copies.OrderBy(c => Turn(inA, c)).SequenceEqual(copies.OrderBy(c => Turn(inB, c))))
                return false;
        }
        return true;
    }

    // Where each copy that moved since launch was then (1-based, among the copies installed now), for "5 →": the moves
    // Changes lists (MovedSince). None before the menu.
    public static Dictionary<Mod, int> LaunchPlaces(IEnumerable<Mod> mods) =>
        MovedSince(_loadedOrder ?? [], NoCopyOrder, mods).ToDictionary(m => m.Mod, m => m.Before);

    // These copies whose place changed since a saved list (and the copy order it had): (copy, place then, now), 1-based
    // among them as Load Order numbers its rows, in load order now. Copies of one id and source that have only traded
    // their key's places are no move: the list the game reads is the same there, and which of them loads is the pick,
    // not the row, so a relaunch would change nothing (an undo or a drag back can leave them so).
    public static List<(Mod Mod, int Before, int After)> MovedSince(IEnumerable<(string? Id, ModSource Source)> saved,
        IReadOnlyDictionary<(string?, ModSource), IReadOnlyList<string>> copyOrder, IEnumerable<Mod> mods)
    {
        var wanted = mods.ToList();
        var (before, after) = (Ranks(Places(saved, wanted, copyOrder)), Ranks(Places(wanted)));
        var traded = wanted.GroupBy(m => (m.manifest?.id, m.modSource))
            .Where(g => g.Select(Copies.CopyOf).Distinct().Count() > 1
                && g.Where(before.ContainsKey).Select(m => before[m]).ToHashSet().SetEquals(g.Where(after.ContainsKey).Select(m => after[m])))
            .SelectMany(g => g).ToHashSet();
        return [.. after.OrderBy(p => p.Value).Where(p => !traded.Contains(p.Key) && before.TryGetValue(p.Key, out var was) && was != p.Value)
            .Select(p => (p.Key, before[p.Key], p.Value))];
    }

    // Places as 1-based ranks, the copies not listed left out.
    public static Dictionary<Mod, int> Ranks(Dictionary<Mod, int> places)
    {
        var ranks = new Dictionary<Mod, int>();
        var rank = 0;
        int? last = null;
        foreach (var (mod, at) in places.OrderBy(p => p.Value))
        {
            if (at != last)
                rank++; // two Mods of one copy share its place
            ranks[mod] = rank;
            last = at;
        }
        return ranks;
    }

    // Rows in load order with handles and numbers, every one shown: a search here finds rather than filters (FindBar).
    public static void Apply(Control content, List<NModMenuRow> rows, Action redraw)
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
        var mods = rows.Select(r => r.Mod).OfType<Mod>().ToList();
        var places = Places(mods);
        var ordered = rows.OrderBy(r => IndexOf(r.Mod, places)).ToList();
        // Where each one was at launch, among the copies installed now (1-based).
        var launch = LaunchPlaces(mods);
        for (var i = 0; i < ordered.Count; i++)
        {
            var row = ordered[i];
            content.MoveChild(row, i);
            row.Visible = true;
            Decorate(content, row, i + 1, row.Mod is { } mod ? launch.GetValueOrDefault(mod) : 0, redraw);
            OrderingOnly(row, true);
        }
    }

    // Hides the handles and numbers in other views, kept for the next visit (making them was most of a switch here).
    public static void Undecorate(IEnumerable<NModMenuRow> rows)
    {
        foreach (var row in rows)
        {
            if (row.GetNodeOrNull<Control>(HandleName) is not { Visible: true } handle)
                continue;
            foreach (var node in new[] { handle, row.GetNodeOrNull<Control>(NumberName), row.GetNodeOrNull<Control>(WasName) })
                if (node != null)
                    node.Visible = false;
            row.GetNode<Control>("Title").OffsetLeft -= HandleWidth;
            OrderingOnly(row, false);
        }
    }

    // Ordering only: the whole row drags (move cursor), no tickbox (on/off is in the right-click menu), and off mods are
    // dimmed further. Undone in other views.
    static void OrderingOnly(NModMenuRow row, bool on)
    {
        row.MouseDefaultCursorShape = on ? Control.CursorShape.Move : Control.CursorShape.Arrow;
        var tickbox = row.GetNode<NTickbox>("Tickbox");
        tickbox.Visible = !on; // on and off are set from the right-click menu here
        tickbox.SetEnabled(!on);
        tickbox.MouseFilter = on ? Control.MouseFilterEnum.Ignore : Control.MouseFilterEnum.Stop;
        row.Modulate = Rest(row);
    }

    // A row's look when nothing is dragged: an off mod is dimmed. While finding, non-matches fade below even an off
    // match, so fading never reads as off. Public for the self-test.
    public const float OffAlpha = 0.4f, UnmatchedAlpha = 0.2f, UnmatchedOffAlpha = 0.1f;

    static Color Rest(NModMenuRow row)
    {
        if (!Store.Data.LoadOrderView || row.Mod is not { } mod)
            return Colors.White;
        var on = Dependencies.IsOn(mod);
        if (row.GetParent() is Control content && CategoryList.Filter(content) is { Length: > 0 } filter && !Search.Matches(mod, filter))
            return new Color(1, 1, 1, on ? UnmatchedAlpha : UnmatchedOffAlpha);
        return on ? Colors.White : new Color(1, 1, 1, OffAlpha);
    }

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
        var mods = rows.Select(r => r.Mod).OfType<Mod>().ToList();
        var places = Places(mods);
        return mods.OrderBy(m => IndexOf(m, places)).ToList();
    }

    // Whether putting mod at to breaks an order between two enabled mods involving it (old problems elsewhere don't
    // block unrelated moves).
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

    // Disable (or Enable), then top, up, down, bottom (greyed where they can't move it; ours, pinned first, never move,
    // and nothing moves above them). compact: the inline moves only; placed: the rows in place, when the caller already
    // has them.
    public static List<MenuItem> Actions(Control content, NModMenuRow row, Action redraw, bool compact = false, List<NModMenuRow>? placed = null)
    {
        var rows = placed ?? content.GetChildren().OfType<NModMenuRow>().ToList();
        var (at, pinned, ours) = (rows.IndexOf(row), Pinned(rows), Ours(row));
        MenuItem Item(string key, int to) => new(Menus.Text(key + (compact ? "_SHORT" : "")), () =>
        {
            if (row.Mod is { } mod)
                Move(mod, to, rows);
            redraw();
            // The buttons are kept through a move (Menus.Refill), and Godot checks what's under a still mouse only when it
            // moves: the button clicked would stay lit on its row, moved away, and the one now under the mouse not. Once the
            // rows are in their new places, the hover goes where the mouse is.
            Callable.From(() => content.GetViewport()?.UpdateMouseCursorState()).CallDeferred();
        }, Disabled: ours || to < pinned || to >= rows.Count || to == at);
        var moves = new List<MenuItem>
        {
            Item("SPIRE_MOD_MANAGER.MOVE_TO_TOP", pinned),
            Item("SPIRE_MOD_MANAGER.MOVE_UP", at - 1),
            Item("SPIRE_MOD_MANAGER.MOVE_DOWN", at + 1),
            Item("SPIRE_MOD_MANAGER.MOVE_TO_BOTTOM", rows.Count - 1),
        };
        if (compact || row.Mod is not { } it)
            return moves;
        // No tickboxes in this view: on and off live here. The game's own handler does the work (CopyTickPatch for a
        // copy of a mod installed twice: enabling it disables the others, as its tip says).
        var on = Dependencies.IsOn(it);
        moves.Insert(0, new MenuItem(Menus.Text(on ? "SPIRE_MOD_MANAGER.DISABLE_MOD" : "SPIRE_MOD_MANAGER.ENABLE_MOD"), () =>
        {
            var tickbox = row.GetNode<NTickbox>("Tickbox");
            tickbox.IsTicked = !on;
            tickbox.EmitSignal(NTickbox.SignalName.Toggled, tickbox);
            redraw();
        }, Tip: on ? null : CategoryList.SwitchCopyTip(it)));
        return moves;
    }

    public static Node? ShowRowMenu(Control content, NModMenuRow row, Action redraw) =>
        Menus.Show(content, Actions(content, row, redraw));

    // The bar above the list (it doesn't scroll): Auto sort at the search box's right end, and banners: this mod
    // unticked (red), Better Mod Menu ticked too (yellow), and the page's warning (red: misordered mods with Fix now in
    // Load Order, mods that won't load on the main list). Elsewhere a "!" on the Load Order button. Hovering a banner
    // lists its mods (BannerCard).
    public const string WarningName = "SpireModManagerOrderWarning", ManagersName = "SpireModManagerManagersWarning", AutoSortName = "AutoSortButton";
    public const string SameIdName = "SpireModManagerSameIdWarning";
    public const string SelfName = "SpireModManagerSelfWarning";
    public const string AutoSortHintName = "AutoSortHint", BannerCardName = "SpireModManagerBannerCard";
    const string BadgeName = "WarningBadge";
    const int CardLines = 12; // a banner's card names at most this many mods, then says how many more
    // Each banner's height when last taken down, by name and text (the main list's warning and Load Order's share a
    // name): the next one saying the same starts as tall, so a wrapped banner a redraw makes again keeps its height until
    // it's laid out (later, on a hidden page) rather than moving the list under it up and back (Layout.KeepRows).
    static readonly Dictionary<string, float> Heights = [];
    static readonly StringName HeightKeyMeta = "spire_mod_manager_banner_key";

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
        var finding = FindBar.Update(border, content, on && search != null);
        // Across here; up and down they follow the search box, placed in Layout.PlaceList once it's in place for this
        // view (read here, it was still where the main list has it).
        if (search != null)
        {
            const float sortWidth = 110, gap = 8;
            var hintWidth = hint.GetThemeFont("font").GetStringSize(hint.Text, fontSize: 15).X;
            sort.OffsetRight = border.GetNode<Control>("ModsScrollContainer").OffsetRight;
            sort.OffsetLeft = sort.OffsetRight - sortWidth;
            hint.OffsetRight = sort.OffsetLeft - gap;
            hint.OffsetLeft = hint.OffsetRight - hintWidth;
            search.OffsetRight = on ? hint.OffsetLeft - gap * 2 : sort.OffsetRight;
            // While searching: "6 matches · ‹ ›" between the search box and the hint.
            if (border.GetNodeOrNull<Control>(FindBar.Name) is { } find)
            {
                find.OffsetRight = hint.OffsetLeft - gap * 2;
                find.OffsetLeft = find.OffsetRight - Math.Max(FindBar.Width, find.GetCombinedMinimumSize().X); // a longer count gets room
                if (finding)
                    search.OffsetRight = find.OffsetLeft - gap;
            }
        }

        var mods = Mods(rows);
        var violations = Dependencies.OrderViolations(mods);
        var misordered = violations.Select(v => v.Mod).Distinct().Count();
        var orderText = Menus.Format(misordered == 1 ? "SPIRE_MOD_MANAGER.ORDER_PROBLEMS_ONE" : "SPIRE_MOD_MANAGER.ORDER_PROBLEMS", ("count", Menus.Number(misordered)));
        // The "!"'s card: the warning alone (the mods it's about are Load Order's own card).
        Badge(content, misordered > 0 && !on ? orderText : null);
        foreach (var name in new[] { WarningName, ManagersName, SameIdName, SelfName })
            if (border.GetNodeOrNull<Control>(name) is { } old)
            {
                Heights[old.GetMeta(HeightKeyMeta, name).AsString()] = old.OffsetBottom - old.OffsetTop;
                border.RemoveChild(old); // now, not at the end of the frame: a redraw in the same frame adds a new one
                old.QueueFree();
            }
        // Better Mod Menu and ours both ticked, on our views, unless dismissed; it shows and goes at once on a tickbox
        // change. A dismissal is forgotten once the two aren't both ticked.
        Bmm.ForgetDismissedWarning();
        if (Layout.Wide && Dependencies.Find(ModEntry.ModId) is { } self && !Dependencies.IsOn(self))
            SelfBanner(content, border, search, self, redraw);
        if (Layout.Wide && Bmm.BothTicked && !Store.Data.ManagersWarningDismissed)
            ManagersBanner(content, border, search, redraw);
        // Two or more copies of one mod id on (the game loads one), on our views, unless dismissed for this set.
        // A dismissal is forgotten once no copies clash, so the problem coming back shows it again (as Dismiss's tip says).
        var clashing = Copies.Clashing();
        if (clashing.Count == 0 && Store.Data.SameIdDismissed != null)
        {
            Store.Data.SameIdDismissed = null;
            Store.SaveViewState();
        }
        if (Layout.Wide && clashing.Count > 0 && Store.Data.SameIdDismissed != Copies.Signature(clashing))
            SameIdBanner(content, border, search, clashing);
        if (on && misordered > 0)
            Banner(border, search, orderText, () => OrderCard(content),
                [Menus.SmallButton(Menus.Text("SPIRE_MOD_MANAGER.FIX_ORDER"), () => Do(FixOrder), name: "FixOrderButton")]);
        else if (Layout.Current == Layout.View.Categories)
            ListBanner(content, border, search, mods, redraw);
        PageBar.Update(content, border, rows);
        Layout.PlaceList(border);
    }

    // Main list: enabled mods that won't load as things stand.
    static void ListBanner(Control content, Control border, Control? search, List<Mod> mods, Action redraw)
    {
        var broken = Unloadable(mods);
        if (broken.Count == 0)
            return;
        var needOff = broken.Count(b => b.Needs.Any(d => d.Dep != null));
        var needMissing = broken.Count(b => b.Needs.Any(d => d.Dep == null));
        string Part(string key, int count) => Menus.Format(count == 1 ? key + "_ONE" : key, ("count", Menus.Number(count)));
        var text = string.Join(" ", new[]
        {
            needOff > 0 ? Part("SPIRE_MOD_MANAGER.LIST_PROBLEMS_OFF", needOff) : null,
            needMissing > 0 ? Part("SPIRE_MOD_MANAGER.LIST_PROBLEMS_MISSING", needMissing) : null,
        }.OfType<string>());

        // Show steps through each mod that won't load, then the disabled mods it needs, from the one after the mod in
        // the info panel.
        var stops = broken.SelectMany(b => b.Needs.Select(d => d.Dep).OfType<Mod>().Prepend(b.Mod)).Distinct().ToList();
        var show = Menus.SmallButton(Menus.Text("SPIRE_MOD_MANAGER.SHOW_PROBLEM"),
            () => GoTo(content, Next(stops)), name: "ShowProblemButton");
        Hover.Tip(show, Menus.Text("SPIRE_MOD_MANAGER.SHOW_PROBLEM_TIP"));
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
            Hover.Tip(enable, Menus.Format("SPIRE_MOD_MANAGER.ENABLE_NEEDED_TIP",
                ("mods", string.Join(", ", needed.Select(m => (m.manifest?.name ?? m.manifest?.id) + Copies.Which(m)))))); // which copy, of one installed twice
            buttons.Add(enable);
        }
        Banner(border, search, text, () => NeedsCard(content), buttons);
    }

    // Enabled mods that won't load, each with what it needs that's disabled (Dep) or not installed (Dep null); the ones
    // needing a disabled mod first.
    static List<(Mod Mod, List<(string Id, Mod? Dep)> Needs)> Unloadable(IEnumerable<Mod> mods) =>
        mods.Where(Dependencies.IsOn)
            .Select(m => (Mod: m, Needs: Dependencies.Of(m).Distinct().Select(id => (Id: id, Dep: Dependencies.Find(id)))
                .Where(d => d.Dep == null || !Dependencies.IsOn(d.Dep)).ToList()))
            .Where(b => b.Needs.Count > 0)
            .OrderBy(b => b.Needs.All(d => d.Dep == null)) // stable: each group keeps its order
            .ToList();

    // This mod unticked: the page goes with it at the relaunch, so a red banner says so, with Show and Enable now.
    // Ticking it again (or undo) takes the banner away.
    static void SelfBanner(Control content, Control border, Control? search, Mod self, Action redraw)
    {
        var show = Menus.SmallButton(Menus.Text("SPIRE_MOD_MANAGER.SHOW_PROBLEM"), () => GoTo(content, self), name: "ShowSelfButton");
        Hover.Tip(show, Menus.Text("SPIRE_MOD_MANAGER.SHOW_SELF_TIP"));
        var enable = Menus.SmallButton(Menus.Text("SPIRE_MOD_MANAGER.ENABLE_SELF"), () =>
        {
            if (CategoryList.ScreenOf(content) is not { } screen)
                return;
            Dependencies.Set([self], true, screen);
            redraw();
        }, name: "EnableSelfButton");
        Hover.Tip(enable, Menus.Text("SPIRE_MOD_MANAGER.ENABLE_SELF_TIP"));
        Banner(border, search, Menus.Text("SPIRE_MOD_MANAGER.SELF_OFF"), () => null, [show, enable], SelfName, "SelfOff");
    }

    // Better Mod Menu ticked as well as us: a yellow warning, with Show stepping through the managers' rows (Better Mod
    // Menu first) and Dismiss for a player happy to run both.
    static void ManagersBanner(Control content, Control border, Control? search, Action redraw)
    {
        var show = Menus.SmallButton(Menus.Text("SPIRE_MOD_MANAGER.SHOW_PROBLEM"), () =>
        {
            var managers = Bmm.TickedManagers().OrderBy(m => m.manifest?.id == ModEntry.ModId).ToList();
            if (managers.Count > 0)
                GoTo(content, Next(managers));
        }, name: "ShowManagersButton");
        Hover.Tip(show, Menus.Text("SPIRE_MOD_MANAGER.SHOW_PROBLEM_TIP")); // the same words as the other banner's: no manager named
        var dismiss = Menus.SmallButton(Menus.Text("SPIRE_MOD_MANAGER.DISMISS_MANAGERS"), () =>
        {
            Store.Data.ManagersWarningDismissed = true;
            Store.Save(); // view state: no undo step, not a change (Store.EditsJson)
            CategoryList.Apply(content); // no mod changed, so rows kept under a filter stay (a Redraw lets them go)
        }, name: "DismissManagersButton");
        Hover.Tip(dismiss, Menus.Text("SPIRE_MOD_MANAGER.DISMISS_MANAGERS_TIP"));
        Banner(border, search, Menus.Text("SPIRE_MOD_MANAGER.MANAGERS_WARNING"), ManagersCard, [show, dismiss], ManagersName, "ManagersWarning", WarningColor);
    }

    // Copies of one mod id both on, in Jordan's words: a yellow warning, Show stepping through those copies, Dismiss for
    // this set (a new one shows it again). Its card lists them, saying which copy each is.
    static void SameIdBanner(Control content, Control border, Control? search, List<Mod> clashing)
    {
        var show = Menus.SmallButton(Menus.Text("SPIRE_MOD_MANAGER.SHOW_PROBLEM"), () => GoTo(content, Next(clashing)), name: "ShowSameIdButton");
        Hover.Tip(show, Menus.Text("SPIRE_MOD_MANAGER.SHOW_PROBLEM_TIP"));
        var dismiss = Menus.SmallButton(Menus.Text("SPIRE_MOD_MANAGER.DISMISS_MANAGERS"), () =>
        {
            Store.Data.SameIdDismissed = Copies.Signature(clashing);
            Store.SaveViewState(); // view state: no undo step, not a change (Store.EditsJson)
            CategoryList.Apply(content);
        }, name: "DismissSameIdButton");
        Hover.Tip(dismiss, Menus.Text("SPIRE_MOD_MANAGER.DISMISS_MANAGERS_TIP"));
        Banner(border, search, Menus.Format("SPIRE_MOD_MANAGER.SAME_ID_WARNING", ("count", Menus.Number(clashing.Count))),
            () => BannerCard("SPIRE_MOD_MANAGER.BANNER_SAME_ID_TITLE", clashing.Select(m => Shown(m) + Copies.Which(m)).ToList()),
            [show, dismiss], SameIdName, "SameIdWarning", WarningColor);
    }

    // The banners' cards, worked out when one opens. Main list: each enabled mod that won't load, with what it needs.
    static string? NeedsCard(Control content)
    {
        using var lookups = Lookups.Begin();
        string Need((string Id, Mod? Dep) need) => need.Dep is { } dep
            ? Menus.Format("SPIRE_MOD_MANAGER.BANNER_NEEDS_OFF", ("mod", Shown(dep)))
            : Menus.Format("SPIRE_MOD_MANAGER.BANNER_NEEDS_MISSING", ("mod", need.Id));
        return BannerCard("SPIRE_MOD_MANAGER.BANNER_CARD_TITLE", Unloadable(Mods(content.GetChildren().OfType<NModMenuRow>()))
            .Select(b => Menus.Format("SPIRE_MOD_MANAGER.BANNER_NEEDS", ("mod", Shown(b.Mod)), ("needs", string.Join(", ", b.Needs.Select(Need))))).ToList());
    }

    // Load Order: each enabled mod placed before a mod it needs, in load order.
    static string? OrderCard(Control content)
    {
        using var lookups = Lookups.Begin();
        return BannerCard("SPIRE_MOD_MANAGER.BANNER_CARD_TITLE", OrderLines(Dependencies.OrderViolations(Mods(content.GetChildren().OfType<NModMenuRow>()))));
    }

    // A line for each mod placed before a mod it needs, in load order: the order card's lines, and the "!"'s elsewhere.
    static List<string> OrderLines(List<(Mod Mod, Mod Needs)> violations) => violations.GroupBy(v => v.Mod)
        .Select(g => Menus.Format("SPIRE_MOD_MANAGER.BANNER_LOADS_BEFORE", ("mod", Shown(g.Key)), ("needs", string.Join(", ", g.Select(v => Shown(v.Needs)).Distinct()))))
        .ToList();

    // Two managers: the mod managers that are on, by the names the list gives them.
    static string? ManagersCard()
    {
        using var lookups = Lookups.Begin();
        return BannerCard("SPIRE_MOD_MANAGER.BANNER_MANAGERS_TITLE", Bmm.TickedManagers().Select(Shown).ToList());
    }

    // A title, then one mod a line (Capped, "and N more" dimmed); none for no lines.
    static string? BannerCard(string title, List<string> lines)
    {
        if (lines.Count == 0)
            return null;
        var shown = Capped(lines).Select(Escape).ToList();
        if (lines.Count > CardLines)
            shown[^1] = $"[color=#fff6e280]{shown[^1]}[/color]";
        return $"[b]{Escape(Menus.Text(title))}[/b]\n{string.Join("\n", shown)}";
    }

    // The first CardLines lines, then how many more, if any.
    static IEnumerable<string> Capped(List<string> lines) => lines.Count <= CardLines ? lines
        : lines.Take(CardLines).Append(Menus.Format("SPIRE_MOD_MANAGER.BANNER_CARD_MORE", ("count", Menus.Number(lines.Count - CardLines))));

    // A mod as its row names it: the player's alias, else its name.
    static string Shown(Mod mod) => Store.InfoFor(mod)?.Alias ?? mod.manifest?.name ?? mod.manifest?.id ?? "";

    static string Escape(string text) => text.Replace("[", "[lb]");

    // A banner's Show: the mod's row scrolled into view (its category opened, a search that leaves it out cleared) with
    // a short glow, and the mod in the info panel.
    static void GoTo(Control content, Mod mod)
    {
        if (content.GetChildren().OfType<NModMenuRow>().FirstOrDefault(r => r.Mod == mod) is not { } row)
            return;
        // This very copy against the search: Reveal looks at the first row with the id, and a local and a Workshop copy
        // share one (their search text can differ).
        var filter = CategoryList.Filter(content);
        if (filter.Length > 0 && !Store.Data.LoadOrderView && !Search.Matches(mod, filter)) // Load Order hides no row
            Search.Clear(content);
        CategoryList.Reveal(content, Copies.Key(mod), flash: true);
        CategoryList.Select(content, row);
    }

    // A Show button's next stop: the mod after the one in the info panel, round to the first at the end (and the first
    // when that mod isn't one of them, or no mod is there).
    static Mod Next(List<Mod> stops) =>
        stops[((CategoryList.Selected is { } selected ? stops.IndexOf(selected) : -1) + 1) % stops.Count];

    // A banner under the search box: its text, then its buttons; red unless color says otherwise. card: what hovering
    // the text shows. name, textName: the node names (the page's warning by default).
    static void Banner(Control border, Control? search, string message, Func<string?> card, List<Control> buttons, string name = WarningName,
        string textName = "OrderProblems", Color? color = null)
    {
        const float height = 34, gap = 6;
        var bar = new PanelContainer { Name = name };
        var look = new StyleBoxFlat
        {
            BgColor = color ?? ProblemColor, ContentMarginLeft = 12, ContentMarginRight = 8, ContentMarginTop = 4, ContentMarginBottom = 4,
        };
        look.SetCornerRadiusAll(RowTitles.CornerRadius); // rounded like the rows and the search box
        bar.AddThemeStyleboxOverride("panel", look);
        var line = new HBoxContainer(); // nothing in it expands: all of it sits at the left
        line.AddThemeConstantOverride("separation", (int)gap);
        bar.AddChild(line);
        var text = new Label
        {
            Name = textName, Text = message, VerticalAlignment = VerticalAlignment.Center, AutowrapMode = TextServer.AutowrapMode.WordSmart,
            MouseFilter = Control.MouseFilterEnum.Pass,
        };
        text.AddThemeFontOverride("font", ModEntry.Font("res://themes/kreon_regular_shared.tres"));
        text.AddThemeFontSizeOverride("font_size", 16);
        line.AddChild(text);
        // Hovering the text: its card at once, under the banner from the text's left edge. (A redraw makes a new banner,
        // so a card open over the old one goes with it.)
        Hover.Watch(text, BannerCardName, card, Hover.Timing.Instant, width: 440,
            place: () => (new Vector2(text.GlobalPosition.X, bar.GlobalPosition.Y + bar.Size.Y + 6), false, false));
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
        var key = $"{name}\n{message}";
        bar.SetMeta(HeightKeyMeta, key);
        (bar.OffsetTop, bar.OffsetBottom) = (top, top + Heights.GetValueOrDefault(key, height));

        // The text takes its one-line width, or what the buttons leave, wrapping there (a wrapping label asks for no
        // width of its own). The room comes from the bar's anchors, not its size, which the text may have widened.
        var oneLine = Mathf.Ceil(text.GetThemeFont("font").GetStringSize(message, fontSize: 16).X + text.GetThemeStylebox("normal").GetMinimumSize().X) + 1;
        void Fit()
        {
            var room = border.Size.X + bar.OffsetRight - bar.OffsetLeft - look.GetMinimumSize().X - buttons.Sum(b => b.GetCombinedMinimumSize().X + gap);
            text.CustomMinimumSize = new Vector2(Math.Min(oneLine, Math.Max(room, 0)), 0);
        }
        Fit();
        border.Resized += Fit;
        bar.TreeExiting += () => border.Resized -= Fit; // a redraw makes a new bar
        // Wrapped, the bar grows to fit the lines and what's under it moves down (Layout stacks by each one's height).
        // Hidden (Vanilla View), it leaves the list alone: Better Mod Menu may have laid it out there.
        bar.MinimumSizeChanged += () =>
        {
            var fits = Math.Max(height, bar.GetCombinedMinimumSize().Y);
            if (!bar.IsVisibleInTree() || bar.OffsetBottom - bar.OffsetTop == fits)
                return;
            bar.OffsetBottom = bar.OffsetTop + fits;
            Layout.PlaceList(border);
        };
    }

    // The "!" on the Load Order button, with the page's warning in its hover card; null takes it off.
    static void Badge(Control content, string? warning)
    {
        if (CategoryList.ScreenOf(content)?.GetNodeOrNull<Control>("SpireModManagerLoadOrderToggle") is not { } toggle)
            return;
        Hover.Tip(toggle, warning);
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

    // Auto sort by impact: libraries, then gameplay mods, then visual-only mods, keeping the player's order within each
    // tier; then dependencies move ahead of what needs them. One undo step.
    public static void AutoSort(IList<NModMenuRow> rows)
    {
        var mods = Mods(rows);
        // Who needs each id, worked out once rather than searched for per mod.
        var neededBy = ModManager.Mods.SelectMany(m => Dependencies.Of(m).Where(id => id != null).Select(id => (Id: id, Mod: m)))
            .ToLookup(p => p.Id, p => p.Mod);
        int Tier(Mod mod) =>
            neededBy[mod.manifest?.id ?? ""].Any(m => m != mod) ? 0
            : mod.manifest?.affectsGameplay ?? true ? 1
            : 2;
        Save(Dependencies.Fixed(mods.OrderBy(Tier).ToList())); // OrderBy is stable: same-tier mods keep their order
    }

    // Our entries (one per installed copy) at the top of a saved list, everything else in its order: the game sorts a
    // mod by its id's last saved place and we need nothing, so we load first and our Init chooses which copy of a mod
    // installed twice loads before the game tries it (SameIdLoad). Every writer of the order keeps it so. True if moved.
    public static bool OursFirst(List<SettingsSaveMod> list)
    {
        var ours = list.Where(e => e.Id == ModEntry.ModId).ToList();
        if (list.Take(ours.Count).All(e => e.Id == ModEntry.ModId))
            return false;
        var rest = list.Where(e => e.Id != ModEntry.ModId).ToList();
        list.Clear();
        list.AddRange(ours.Concat(rest));
        return true;
    }

    // How many rows of ours lead the list (one per installed copy): pinned there.
    static int Pinned(IList<NModMenuRow> rows) => rows.TakeWhile(r => r.Mod?.manifest?.id == ModEntry.ModId).Count();

    static bool Ours(NModMenuRow row) => row.Mod?.manifest?.id == ModEntry.ModId;

    // Writes a whole new order: installed mods as given (ours first, OursFirst), then entries for mods that aren't
    // installed.
    static void Save(List<Mod> mods)
    {
        if (SaveManager.Instance.SettingsSave.ModSettings is not { } settings)
            return;
        var list = settings.ModList;
        // Each installed copy its own entry, in the list's order: two copies with one id and source (two Workshop items
        // of one mod) take that key's first and second entries. Both took the first before, which left the second over,
        // added again at the end: the list grew by one with every save.
        var spare = new Dictionary<(string?, ModSource), Queue<SettingsSaveMod>>();
        foreach (var entry in list)
        {
            if (!spare.TryGetValue((entry.Id, entry.Source), out var queue))
                spare[(entry.Id, entry.Source)] = queue = new Queue<SettingsSaveMod>();
            queue.Enqueue(entry);
        }
        // A copy with no entry left (a second item of a mod saved once) gets a new one, on or off as its twin's.
        var known = Dependencies.SavedEntries(list);
        var entries = mods.Select(m => spare.TryGetValue((m.manifest?.id, m.modSource), out var queue) && queue.TryDequeue(out var entry) ? entry
            : known.TryGetValue((m.manifest?.id, m.modSource), out var twin) ? new SettingsSaveMod(m) { IsEnabled = twin.IsEnabled }
            : new SettingsSaveMod(m)).ToList();
        var kept = entries.ToHashSet();
        entries.AddRange(list.Where(e => !kept.Contains(e)));
        list.Clear();
        list.AddRange(entries);
        OursFirst(list);
        // Which copy took which of its key's entries, for Places to read them back so.
        foreach (var group in mods.GroupBy(m => (m.manifest?.id, m.modSource)))
            if (group.Select(Copies.CopyOf).Distinct().ToList() is { Count: > 1 } copies)
                CopyOrder[group.Key] = copies;
        SaveManager.Instance.SaveSettings();
        History.Record();
    }

    // The position number and grip on the row's left; dragging the row anywhere moves it, with a gold line where it
    // lands. Made on the first visit, hidden in other views. wasAt: its place at launch (0: unknown), shown as "5 →".
    static void Decorate(Control content, NModMenuRow row, int position, int wasAt, Action redraw)
    {
        var was = wasAt > 0 && wasAt != position ? $"{wasAt} →" : "";
        if (row.GetNodeOrNull<Label>(NumberName) is { } number)
        {
            number.Text = position.ToString();
            row.GetNode<Label>(WasName).Text = was;
            if (number.Visible)
                return;
            foreach (var name in new[] { HandleName, NumberName, WasName })
                row.GetNode<Control>(name).Visible = true;
            row.GetNode<Control>("Title").OffsetLeft += HandleWidth;
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

        // Three short bars: the usual "grab here" grip (the whole row drags; see below). Ours: a lock instead, as it
        // stays first (OursFirst), with why on hovering it.
        var handle = new Control
        {
            Name = HandleName, AnchorBottom = 1, OffsetLeft = LinkGutter, OffsetRight = LinkGutter + 30,
            MouseFilter = Ours(row) ? Control.MouseFilterEnum.Pass : Control.MouseFilterEnum.Ignore,
        };
        if (Ours(row))
        {
            AddLock(handle);
            Hover.Tip(handle, Menus.Text("SPIRE_MOD_MANAGER.LOADS_FIRST_TIP"));
        }
        else
            for (var i = 0; i < 3; i++)
                handle.AddChild(new ColorRect
                {
                    Color = new Color(Gold, 0.7f), AnchorTop = 0.5f, AnchorBottom = 0.5f,
                    OffsetLeft = 6, OffsetRight = 24, OffsetTop = -9 + i * 8, OffsetBottom = -6 + i * 8,
                    MouseFilter = Control.MouseFilterEnum.Ignore,
                });
        row.AddChild(handle);

        // The handlers below are connected once per row: rows outlive this view (connecting again would start one
        // drag per visit).
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

        // The whole row drags (the grip just shows it): a press picks it and starts the drag, the drop saves, and a press
        // without moving is a click. Rows outlive this view, so it does nothing outside Load Order.
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
                case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } when Ours(row) && !dragging:
                    // Ours stays first: a press only selects it.
                    Selection.Pick(content, row, toggle: false, range: false);
                    CategoryList.Select(content, row);
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
                    var gapAt = Target(content, rows, release.GlobalPosition.Y).Index;
                    if (Selection.Several && Selection.Contains(row.Mod))
                    {
                        MoveGroup(Selection.Mods, gapAt, rows);
                        redraw();
                    }
                    else if (Destination(gapAt, rows) is { } to && row.Mod is { } mod)
                    {
                        Move(mod, to, rows);
                        redraw();
                    }
                    break;
            }
        };

        // Every frame while dragging: the list scrolls near an edge (faster nearer it), and the marker follows the gap.
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

            // The rows, and whether each mod is on, read once for the frame (outside a redraw each check walked the saved list).
            using var lookups = Lookups.Begin();
            var rows = content.GetChildren().OfType<NModMenuRow>().ToList();
            var (gap, y) = Target(content, rows, mouseY);
            // Kept inside the list's visible area (it clips), so the gap above the first row still shows.
            marker.GlobalPosition = new Vector2(content.GlobalPosition.X, Math.Clamp(y - 2, view.Position.Y, view.End.Y - 5));
            ShowLinks(content, row, strong: true, drop: (gap, marker.GlobalPosition.Y + 2), rows);
            // Red where dropping would load it before something it needs (or after something needing it).
            marker.SelfModulate = Destination(gap, rows) is { } at && row.Mod is { } moving
                && MoveBreaks(moving, at, rows) ? Blocked : Gold;
        }

        // The index the row would take after leaving its slot, or null for its own gap.
        int? Destination(int gap, List<NModMenuRow> rows)
        {
            var from = rows.IndexOf(row);
            return gap == from || gap == from + 1 ? null : gap > from ? gap - 1 : gap;
        }
    }


    // Moves a group to a gap (index among all rows, as Target gives), keeping their relative order.
    public static void MoveGroup(IReadOnlyCollection<Mod> group, int gap, IList<NModMenuRow> rows)
    {
        var order = Mods(rows);
        var picked = group.ToHashSet();
        var moving = order.Where(picked.Contains).ToList();
        var rest = order.Where(m => !picked.Contains(m)).ToList();
        var shown = rows.Select(r => r.Mod).OfType<Mod>().ToList();
        // Rest-of-list position: the unpicked rows above the gap.
        var insertAt = shown.Take(gap).Count(m => !picked.Contains(m));
        rest.InsertRange(Math.Clamp(insertAt, 0, rest.Count), moving);
        Save(rest);
    }

    // Mods linked to this one by a dependency, either way, among the shown rows.
    static List<NModMenuRow> Linked(NModMenuRow row, IReadOnlyList<NModMenuRow> rows)
    {
        if (row.Mod?.manifest?.id is not { } id)
            return [];
        var needs = Dependencies.Of(row.Mod).ToHashSet();
        return rows
            .Where(r => r != row && r.Visible && r.Mod?.manifest?.id is { } other
                && (needs.Contains(other) || Dependencies.Of(r.Mod).Contains(id)))
            .ToList();
    }

    // Tree-style lines in the left gutter from this row to each linked row, each in its own lane. While dragging, drop
    // is where the row would land: the lines' ends follow the marker, and their colour says whether that drop works.
    // rows: the list's rows, when the caller has them already.
    public static void ShowLinks(Control content, NModMenuRow row, bool strong, (int Gap, float Y)? drop = null, List<NModMenuRow>? rows = null)
    {
        HideLinks(content);
        var mask = content.GetParent<Control>();
        float CenterY(Control r) => r == row && drop is { } d ? d.Y - mask.GlobalPosition.Y : r.GetGlobalRect().GetCenter().Y - mask.GlobalPosition.Y;
        rows ??= content.GetChildren().OfType<NModMenuRow>().ToList();
        var linked = Linked(row, rows);
        var needs = Dependencies.Of(row.Mod);
        var self = rows.IndexOf(row);
        for (var i = 0; i < linked.Count; i++)
        {
            // Green when the pair is in a working order (what's needed loads first), red when it isn't.
            var needed = needs.Contains(linked[i].Mod?.manifest?.id ?? "");
            var other = rows.IndexOf(linked[i]);
            var fine = drop is { } at
                ? needed ? other < at.Gap : other >= at.Gap
                : needed ? other < self : other > self;
            var lane = 4 + Math.Min(i, 3) * 4; // 4 px apart: the soft-edged lines are up to 4 px wide
            var line = new Line2D
            {
                Width = strong ? 4 : 3, DefaultColor = new Color(fine ? LinkFine : Blocked, strong ? 1f : 0.6f),
                Texture = LinkEdges, TextureMode = Line2D.LineTextureMode.Stretch, // soft edges (LinkEdges)
                JointMode = Line2D.LineJointMode.Round, // the corners' short segments meet as a curve
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
        using var lookups = Lookups.Begin(); // each picked row's links ask about every row
        var rows = content.GetChildren().OfType<NModMenuRow>().ToList();
        // Everything being moved (the row, or the whole multi-selection) plus whatever they're linked to.
        var moving = rows.Where(r => r == row || (Selection.Several && Selection.Contains(r.Mod))).ToList();
        var lit = moving.SelectMany(r => Linked(r, rows)).Concat(moving).ToHashSet();
        foreach (var r in rows)
            r.Modulate = lit.Contains(r) ? Colors.White : new Color(1, 1, 1, 0.35f);
        foreach (var r in moving)
            r.Modulate = new Color(1.25f, 1.2f, 1.05f); // the ones being moved, a touch brighter
        ShowLinks(content, row, strong: true, rows: rows);
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

    // The gap the mouse is nearest: the index a dropped row would take, and that gap's screen y. Never above our rows,
    // pinned first.
    static (int Index, float Y) Target(Control content, List<NModMenuRow> all, float mouseY)
    {
        var pinned = Pinned(all);
        var rows = all.Skip(pinned).Where(r => r.Visible).ToList();
        for (var i = 0; i < rows.Count; i++)
        {
            var rect = rows[i].GetGlobalRect();
            if (mouseY < rect.GetCenter().Y)
                return (all.IndexOf(rows[i]), rect.Position.Y - 4);
        }
        var last = (rows.LastOrDefault() ?? all.Take(pinned).LastOrDefault())?.GetGlobalRect();
        return (all.Count, last is { } r ? r.End.Y + 4 : content.GlobalPosition.Y);
    }

    // A small padlock in the grip's place: a gold body under an arched shackle.
    static void AddLock(Control handle)
    {
        var shackle = new Panel
        {
            AnchorTop = 0.5f, AnchorBottom = 0.5f, OffsetLeft = 10, OffsetRight = 20, OffsetTop = -10, OffsetBottom = 0,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        var arch = new StyleBoxFlat { BgColor = Colors.Transparent, BorderColor = new Color(Gold, 0.8f), DrawCenter = false };
        (arch.BorderWidthLeft, arch.BorderWidthRight, arch.BorderWidthTop, arch.BorderWidthBottom) = (2, 2, 2, 0);
        (arch.CornerRadiusTopLeft, arch.CornerRadiusTopRight) = (5, 5);
        shackle.AddThemeStyleboxOverride("panel", arch);
        handle.AddChild(shackle);
        var body = new Panel
        {
            AnchorTop = 0.5f, AnchorBottom = 0.5f, OffsetLeft = 7, OffsetRight = 23, OffsetTop = -3, OffsetBottom = 9,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        var fill = new StyleBoxFlat { BgColor = new Color(Gold, 0.8f) };
        fill.SetCornerRadiusAll(2);
        body.AddThemeStyleboxOverride("panel", fill);
        handle.AddChild(body);
    }
}
