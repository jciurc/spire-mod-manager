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
    const string PickedName = "SpireModManagerPicked";
    const float HandleWidth = 84; // the row title shifts right by this much in this view
    const float LinkGutter = 18; // left strip for the dependency lines
    const string LinkGroup = "SpireModManagerLinks";
    const string DraggingMeta = "spire_mod_manager_dragging";
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

    static int IndexOf(Mod? mod, List<SettingsSaveMod> list) =>
        mod?.manifest?.id is { } id && list.FindIndex(m => m.Id == id && m.Source == mod.modSource) is >= 0 and var i ? i : int.MaxValue;

    // Rows in load order with handles and numbers; `matches` is the search filter.
    public static void Apply(Control content, List<NModMenuRow> rows, Func<NModMenuRow, bool> matches, Action redraw)
    {
        var list = SaveManager.Instance.SettingsSave.ModSettings?.ModList ?? [];
        var ordered = rows.OrderBy(r => IndexOf(r.Mod, list)).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var row = ordered[i];
            content.MoveChild(row, i);
            row.Visible = matches(row);
            Decorate(content, row, i + 1, redraw);
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
            foreach (var node in new[] { handle, row.GetNodeOrNull(NumberName), row.GetNodeOrNull(PickedName) })
                if (node != null)
                {
                    row.RemoveChild(node);
                    node.QueueFree();
                }
            row.GetNode<Control>("Title").OffsetLeft -= HandleWidth;
            ModDetails.PlacePencil(row);
            OrderingOnly(row, false);
        }
    }

    // Load Order only orders: the whole row is a drag handle (move cursor), the tickbox is locked, and
    // mods that are off are dimmed further. Off again in other views.
    static void OrderingOnly(NModMenuRow row, bool on)
    {
        row.MouseDefaultCursorShape = on ? Control.CursorShape.Move : Control.CursorShape.Arrow;
        var tickbox = row.GetNode<NTickbox>("Tickbox");
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
        var list = SaveManager.Instance.SettingsSave.ModSettings?.ModList ?? [];
        return rows.Select(r => r.Mod).OfType<Mod>().OrderBy(m => IndexOf(m, list)).ToList();
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

    // Top, up, down, bottom; a move that would break a dependency is disabled.
    // compact: the short labels used for the inline buttons on each row.
    public static List<MenuItem> Actions(Control content, NModMenuRow row, Action redraw, bool compact = false)
    {
        var rows = content.GetChildren().OfType<NModMenuRow>().ToList();
        var at = rows.IndexOf(row);
        MenuItem Item(string key, int to) => new(Menus.Text(key + (compact ? "_SHORT" : "")), () =>
        {
            if (row.Mod is { } mod)
                Move(mod, to, rows);
            redraw();
        }, Disabled: to < 0 || to >= rows.Count || to == at);
        return [
            Item("SPIRE_MOD_MANAGER.MOVE_TO_TOP", 0),
            Item("SPIRE_MOD_MANAGER.MOVE_UP", at - 1),
            Item("SPIRE_MOD_MANAGER.MOVE_DOWN", at + 1),
            Item("SPIRE_MOD_MANAGER.MOVE_TO_BOTTOM", rows.Count - 1),
        ];
    }

    public static PopupMenu ShowRowMenu(Control content, NModMenuRow row, Action redraw) =>
        Menus.Show(content, Actions(content, row, redraw));

    // Above the list, outside its scrolling (so it stays put): Auto sort at the right end of the search
    // box, and, when enabled mods sit above mods they need, a red line saying how many with "Fix now".
    // (The game reorders those at launch anyway; fixing makes the saved order say what will happen.)
    // Outside Load Order both hide.
    public const string WarningName = "SpireModManagerOrderWarning", AutoSortName = "AutoSortButton";

    public static void UpdateToolbar(Control content, List<NModMenuRow> rows, Action redraw)
    {
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
        sort.Visible = on && search != null;
        if (search != null)
        {
            const float sortWidth = 110;
            (sort.OffsetTop, sort.OffsetBottom) = (search.OffsetTop, search.OffsetBottom);
            sort.OffsetRight = border.GetNode<Control>("ModsScrollContainer").OffsetRight;
            sort.OffsetLeft = sort.OffsetRight - sortWidth;
            search.OffsetRight = on ? sort.OffsetLeft - 8 : sort.OffsetRight;
        }

        var problems = on ? Dependencies.OrderViolations(Mods(rows)).Select(v => v.Mod).Distinct().Count() : 0;
        if (border.GetNodeOrNull(WarningName) is { } old)
        {
            border.RemoveChild(old); // now, not at the end of the frame: a redraw in the same frame adds a new one
            old.QueueFree();
        }
        if (problems > 0)
        {
            var bar = new PanelContainer { Name = WarningName };
            bar.AddThemeStyleboxOverride("panel", new StyleBoxFlat
            {
                BgColor = new Color(0.45f, 0.1f, 0.1f, 0.6f), ContentMarginLeft = 12, ContentMarginRight = 8, ContentMarginTop = 4, ContentMarginBottom = 4,
            });
            var line = new HBoxContainer();
            line.AddThemeConstantOverride("separation", 6);
            bar.AddChild(line);
            var text = new Label
            {
                Name = "OrderProblems", Text = Menus.Format("SPIRE_MOD_MANAGER.ORDER_PROBLEMS", ("count", problems.ToString())),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, VerticalAlignment = VerticalAlignment.Center,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
            };
            text.AddThemeFontOverride("font", ResourceLoader.Load<Font>("res://themes/kreon_regular_shared.tres"));
            text.AddThemeFontSizeOverride("font_size", 16);
            line.AddChild(text);
            var fix = Menus.SmallButton(Menus.Text("SPIRE_MOD_MANAGER.FIX_ORDER"), () => Do(FixOrder), name: "FixOrderButton");
            fix.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            line.AddChild(fix);
            border.AddChild(bar);
            bar.SetAnchorsPreset(Control.LayoutPreset.TopWide);
            var scroll = border.GetNode<Control>("ModsScrollContainer");
            (bar.OffsetLeft, bar.OffsetRight) = (scroll.OffsetLeft, scroll.OffsetRight);
            var top = (search ?? scroll).OffsetBottom + 6;
            (bar.OffsetTop, bar.OffsetBottom) = (top, top + 34);
        }
        Layout.PlaceList(border);
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

    // Position number and a grip on the left of the row. Dragging the grip moves the row: a gold line
    // shows where it will land, and releasing saves. Made once per row; the number updates every redraw.
    static void Decorate(Control content, NModMenuRow row, int position, Action redraw)
    {
        if (row.GetNodeOrNull<Label>(NumberName) is { } number)
        {
            number.Text = position.ToString();
            return;
        }
        row.GetNode<Control>("Title").OffsetLeft += HandleWidth;
        ModDetails.PlacePencil(row);

        // Left to right: the link gutter (dependency lines), the grip, the position number, then the title.
        number = new Label
        {
            Name = NumberName, Text = position.ToString(), HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore,
            AnchorBottom = 1, OffsetLeft = LinkGutter + 30, OffsetRight = LinkGutter + 56,
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

        // Hover: show how this mod is linked to others.
        row.MouseEntered += () =>
        {
            if (!content.HasMeta(DraggingMeta))
                ShowLinks(content, row, strong: false);
        };
        row.MouseExited += () =>
        {
            if (!content.HasMeta(DraggingMeta))
                HideLinks(content);
        };

        // Multi-select highlight, behind everything else in the row.
        var picked = new ColorRect
        {
            Name = PickedName, Color = new Color(0.35f, 0.6f, 0.95f, 0.18f), Visible = false,
            MouseFilter = Control.MouseFilterEnum.Ignore, AnchorRight = 1, AnchorBottom = 1,
        };
        row.AddChild(picked);
        row.MoveChild(picked, 0);

        // The whole row is the drag handle (the grip just shows it). A press picks the row (Ctrl toggles,
        // Shift picks a range: those only change the selection); moving past a few pixels starts the drag,
        // and the drop saves. A click without a drag on a row of a multi-selection picks just that row.
        // Rows outlive this view, so the handler does nothing outside Load Order.
        var pressed = false;
        var dragging = false;
        var pressAt = Vector2.Zero;
        var mouseY = 0f; // where the last mouse event put the pointer
        ColorRect? marker = null;
        row.GuiInput += e =>
        {
            if (!Store.Data.LoadOrderView)
                return;
            switch (e)
            {
                case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } modified
                    when modified.CtrlPressed || modified.ShiftPressed || modified.MetaPressed:
                    Pick(content, row, toggle: modified.CtrlPressed || modified.MetaPressed, range: modified.ShiftPressed);
                    row.AcceptEvent();
                    break;
                case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } press:
                    pressed = true;
                    pressAt = press.GlobalPosition;
                    mouseY = press.GlobalPosition.Y;
                    if (row.Mod is not { } grabbed || !Picked.Contains(grabbed))
                        Pick(content, row, toggle: false, range: false);
                    CategoryList.Select(content, row); // grabbing a row selects it too
                    row.AcceptEvent(); // not a scroll of the list
                    break;
                case InputEventMouseMotion motion when pressed:
                    mouseY = motion.GlobalPosition.Y;
                    if (!dragging && motion.GlobalPosition.DistanceTo(pressAt) > DragStart)
                    {
                        dragging = true;
                        marker = new ColorRect { Color = Gold, Size = new Vector2(content.Size.X, 5), MouseFilter = Control.MouseFilterEnum.Ignore };
                        content.GetParent().AddChild(marker); // the scroll mask: not a container, so it can sit anywhere
                        content.GetTree().ProcessFrame += DragFrame;
                        BeginDragLook(content, row);
                    }
                    row.AcceptEvent(); // the marker, lines and edge scrolling update every frame (DragFrame)
                    break;
                case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false } release when pressed:
                    pressed = false;
                    row.AcceptEvent();
                    if (!dragging)
                    {
                        Pick(content, row, toggle: false, range: false);
                        break;
                    }
                    dragging = false;
                    content.GetTree().ProcessFrame -= DragFrame;
                    marker?.QueueFree();
                    marker = null;
                    EndDragLook(content);
                    var rows = content.GetChildren().OfType<NModMenuRow>().ToList();
                    // Drops are allowed even where they break a dependency: the red line and the warning bar say so.
                    // A multi-selection moves as one block.
                    var gapAt = Target(content, release.GlobalPosition.Y).Index;
                    if (Picked.Count > 1 && row.Mod is { } lead && Picked.Contains(lead))
                    {
                        MoveGroup(Picked, gapAt, rows);
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
            marker.Color = Destination(gap) is { } at && row.Mod is { } moving
                && MoveBreaks(moving, at, content.GetChildren().OfType<NModMenuRow>().ToList()) ? Blocked : Gold;
        }

        // The index the row would take after leaving its slot, or null for its own gap.
        int? Destination(int gap)
        {
            var from = content.GetChildren().OfType<NModMenuRow>().ToList().IndexOf(row);
            return gap == from || gap == from + 1 ? null : gap > from ? gap - 1 : gap;
        }
    }


    // Multi-selection in Load Order: Ctrl toggles a row, Shift selects the range from the last picked row,
    // a plain click picks just one. Dragging any picked row moves them all.
    static readonly HashSet<Mod> Picked = [];
    static Mod? _anchor;

    public static IReadOnlyCollection<Mod> Selection => Picked;

    public static void Pick(Control content, NModMenuRow row, bool toggle, bool range)
    {
        if (row.Mod is not { } mod)
            return;
        var rows = content.GetChildren().OfType<NModMenuRow>().ToList();
        if (range && _anchor != null && rows.FindIndex(r => r.Mod == _anchor) is >= 0 and var from)
        {
            var to = rows.IndexOf(row);
            Picked.Clear();
            for (var i = Math.Min(from, to); i <= Math.Max(from, to); i++)
                if (rows[i].Visible && rows[i].Mod is { } m)
                    Picked.Add(m);
        }
        else if (toggle)
        {
            if (!Picked.Remove(mod))
                Picked.Add(mod);
            _anchor = mod;
        }
        else
        {
            Picked.Clear();
            Picked.Add(mod);
            _anchor = mod;
        }
        ShowPicked(content);
    }

    public static void ClearSelection(Control content)
    {
        Picked.Clear();
        _anchor = null;
        ShowPicked(content);
    }

    static void ShowPicked(Control content)
    {
        var several = Picked.Count > 1; // one picked row is just the selection the game already shows
        foreach (var row in content.GetChildren().OfType<NModMenuRow>())
            if (row.GetNodeOrNull<Control>(PickedName) is { } highlight)
                highlight.Visible = several && row.Mod is { } m && Picked.Contains(m);
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
    // the gutter, into the other row. Each link gets its own lane so they don't overlap.
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
                Points = [new(LinkGutter, CenterY(row)), new(lane, CenterY(row)), new(lane, CenterY(linked[i])), new(LinkGutter, CenterY(linked[i]))],
            };
            line.AddToGroup(LinkGroup);
            mask.AddChild(line);
        }
    }

    public static void HideLinks(Control content)
    {
        foreach (var line in content.GetParent().GetChildren().Where(c => c.IsInGroup(LinkGroup)))
            line.QueueFree();
    }

    // While dragging: the list dims, the dragged row and every row linked to it stay lit, links drawn strong.
    static void BeginDragLook(Control content, NModMenuRow row)
    {
        content.SetMeta(DraggingMeta, true);
        var rows = content.GetChildren().OfType<NModMenuRow>().ToList();
        // Everything being moved (the row, or the whole multi-selection) plus whatever they're linked to.
        var moving = rows.Where(r => r == row || (Picked.Count > 1 && r.Mod is { } m && Picked.Contains(m))).ToList();
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
