using Godot;
using MegaCrit.Sts2.Core.Modding;
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
    const float HandleWidth = 64; // the row title shifts right by this much in this view
    static readonly Color Gold = new(0.937f, 0.784f, 0.318f);
    static readonly Color Blocked = new(0.9f, 0.25f, 0.25f);
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
        }
    }

    // Takes the handles and numbers off again (other views).
    public static void Undecorate(IEnumerable<NModMenuRow> rows)
    {
        foreach (var row in rows)
        {
            if (row.GetNodeOrNull(HandleName) is not { } handle)
                continue;
            handle.QueueFree();
            row.GetNodeOrNull(NumberName)?.QueueFree();
            row.GetNode<Control>("Title").OffsetLeft -= HandleWidth;
        }
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
        }, Disabled: to < 0 || to >= rows.Count || to == at || (row.Mod is { } m && MoveBreaks(m, to, rows)));
        return [
            Item("SPIRE_MOD_MANAGER.MOVE_TO_TOP", 0),
            Item("SPIRE_MOD_MANAGER.MOVE_UP", at - 1),
            Item("SPIRE_MOD_MANAGER.MOVE_DOWN", at + 1),
            Item("SPIRE_MOD_MANAGER.MOVE_TO_BOTTOM", rows.Count - 1),
        ];
    }

    public static PopupMenu ShowRowMenu(Control content, NModMenuRow row, Action redraw) =>
        Menus.Show(content, Actions(content, row, redraw));

    // When enabled mods sit above mods they need: a red bar at the top of the list with a Fix button.
    // The game reorders them at launch anyway; this makes the saved order say what will happen.
    public static void AddBanner(Control content, List<NModMenuRow> rows, Action redraw, Action<Control> add)
    {
        var problems = Dependencies.OrderViolations(Mods(rows)).Select(v => v.Mod).Distinct().Count();
        if (problems == 0)
            return;
        var banner = new PanelContainer { Name = "OrderBanner", CustomMinimumSize = new Vector2(0, 48) };
        banner.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.45f, 0.1f, 0.1f, 0.6f), ContentMarginLeft = 12, ContentMarginRight = 8,
        });
        var line = new HBoxContainer();
        banner.AddChild(line);
        var text = new Label { Text = Menus.Format("SPIRE_MOD_MANAGER.ORDER_PROBLEMS", ("count", problems.ToString())),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, VerticalAlignment = VerticalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart };
        text.AddThemeFontOverride("font", ResourceLoader.Load<Font>("res://themes/kreon_regular_shared.tres"));
        text.AddThemeFontSizeOverride("font_size", 18);
        line.AddChild(text);
        line.AddChild(Menus.SmallButton(Menus.Text("SPIRE_MOD_MANAGER.FIX_ORDER"), () =>
        {
            FixOrder(content.GetChildren().OfType<NModMenuRow>().ToList());
            redraw();
        }, name: "FixOrderButton"));
        add(banner);
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

        number = new Label
        {
            Name = NumberName, Text = position.ToString(), HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore,
            Position = new Vector2(30, 0), Size = new Vector2(30, 72),
        };
        number.AddThemeColorOverride("font_color", new Color(Gold, 0.8f));
        number.AddThemeFontSizeOverride("font_size", 20);
        row.AddChild(number);

        // Three short bars: the usual "grab here" grip. Stops the mouse so dragging it doesn't scroll the list.
        var handle = new Control
        {
            Name = HandleName, Position = new Vector2(0, 0), Size = new Vector2(30, 72),
            MouseFilter = Control.MouseFilterEnum.Stop, MouseDefaultCursorShape = Control.CursorShape.Move,
        };
        for (var i = 0; i < 3; i++)
            handle.AddChild(new ColorRect
            {
                Color = new Color(Gold, 0.7f), Position = new Vector2(6, 27 + i * 8), Size = new Vector2(18, 3),
                MouseFilter = Control.MouseFilterEnum.Ignore,
            });
        row.AddChild(handle);

        var dragging = false;
        ColorRect? marker = null;
        handle.GuiInput += e =>
        {
            switch (e)
            {
                case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true }:
                    dragging = true;
                    marker = new ColorRect { Color = Gold, Size = new Vector2(content.Size.X, 3), MouseFilter = Control.MouseFilterEnum.Ignore };
                    content.GetParent().AddChild(marker); // the scroll mask: not a container, so it can sit anywhere
                    handle.AcceptEvent();
                    break;
                case InputEventMouseMotion motion when dragging && marker != null:
                    var (gap, y) = Target(content, motion.GlobalPosition.Y);
                    marker.GlobalPosition = new Vector2(content.GlobalPosition.X, y - 1);
                    // Red where dropping would load it before something it needs (or after something needing it).
                    marker.Color = Destination(gap) is { } at && row.Mod is { } moving
                        && MoveBreaks(moving, at, content.GetChildren().OfType<NModMenuRow>().ToList()) ? Blocked : Gold;
                    handle.AcceptEvent();
                    break;
                case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false } release when dragging:
                    dragging = false;
                    marker?.QueueFree();
                    marker = null;
                    var rows = content.GetChildren().OfType<NModMenuRow>().ToList();
                    if (Destination(Target(content, release.GlobalPosition.Y).Index) is { } to && row.Mod is { } mod
                        && !MoveBreaks(mod, to, rows))
                    {
                        Move(mod, to, rows);
                        redraw();
                    }
                    handle.AcceptEvent();
                    break;
            }
        };

        // The index the row would take after leaving its slot, or null for its own gap.
        int? Destination(int gap)
        {
            var from = content.GetChildren().OfType<NModMenuRow>().ToList().IndexOf(row);
            return gap == from || gap == from + 1 ? null : gap > from ? gap - 1 : gap;
        }
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
