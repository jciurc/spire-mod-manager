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
        if (SaveManager.Instance.SettingsSave.ModSettings is not { } settings)
            return;
        var list = settings.ModList;
        var mods = rows.Select(r => r.Mod).OfType<Mod>().OrderBy(m => IndexOf(m, list)).ToList();
        mods.Remove(mod);
        mods.Insert(Math.Clamp(to, 0, mods.Count), mod);

        // Installed mods in their new order first, then entries for mods that aren't installed.
        var entries = mods.Select(m => list.FirstOrDefault(e => e.Id == m.manifest?.id && e.Source == m.modSource) ?? new SettingsSaveMod(m)).ToList();
        entries.AddRange(list.Where(e => !entries.Contains(e)));
        list.Clear();
        list.AddRange(entries);
        SaveManager.Instance.SaveSettings();
    }

    public static PopupMenu ShowRowMenu(Control content, NModMenuRow row, Action redraw)
    {
        var rows = content.GetChildren().OfType<NModMenuRow>().ToList();
        var at = rows.IndexOf(row);
        void To(int index)
        {
            if (row.Mod is { } mod)
                Move(mod, index, rows);
            redraw();
        }
        return Menus.Show(content, [
            new MenuItem(Menus.Text("SPIRE_MOD_MANAGER.MOVE_TO_TOP"), () => To(0), Disabled: at == 0),
            new MenuItem(Menus.Text("SPIRE_MOD_MANAGER.MOVE_UP"), () => To(at - 1), Disabled: at == 0),
            new MenuItem(Menus.Text("SPIRE_MOD_MANAGER.MOVE_DOWN"), () => To(at + 1), Disabled: at == rows.Count - 1),
            new MenuItem(Menus.Text("SPIRE_MOD_MANAGER.MOVE_TO_BOTTOM"), () => To(rows.Count - 1), Disabled: at == rows.Count - 1),
        ]);
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
                    var (_, y) = Target(content, motion.GlobalPosition.Y);
                    marker.GlobalPosition = new Vector2(content.GlobalPosition.X, y - 1);
                    handle.AcceptEvent();
                    break;
                case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false } release when dragging:
                    dragging = false;
                    marker?.QueueFree();
                    marker = null;
                    var rows = content.GetChildren().OfType<NModMenuRow>().ToList();
                    var (to, _) = Target(content, release.GlobalPosition.Y);
                    var from = rows.IndexOf(row);
                    if (row.Mod is { } mod && to != from && to != from + 1)
                    {
                        Move(mod, to > from ? to - 1 : to, rows);
                        redraw();
                    }
                    handle.AcceptEvent();
                    break;
            }
        };
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
