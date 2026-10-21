using System.Runtime.CompilerServices;
using Godot;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.CommonUi;

namespace SpireModManager;

// Our pop-up menus (right-click menus, the profile list, the category picker): an overlay inside the page, not an
// engine popup. Rows have an optional mark, a label, a badge and a dim detail; headings separate groups; an optional
// side column holds a mod's own fields. A press outside or Escape closes it (a right-click outside also reaches
// what's underneath). One at a time. It opens below right of the mouse (or below a picker), and above (left of) it where
// there's no room below (right), as Hover's cards do: never over the mouse while there's room on the other side. It
// closes when its owner leaves the tree, or is hidden. The API's menus (PopupMenus, keyboard) also take the keyboard:
// the game's cancel closes them, and the screen's hotkeys are off while one is open.
static class MenuPanel
{
    static readonly Color Cream = Look.Cream;
    static readonly Color Gold = Look.Gold;
    static readonly StringName CancelAction = "ui_cancel", PauseAction = "mega_pause_and_back"; // the game's (MegaInput.cancel, .pauseAndBack)
    public const string RootName = "SpireModManagerMenu";
    public const string ItemGroup = "SpireModManagerMenuItem";
    public const string HeadingGroup = "SpireModManagerMenuHeading";
    public const int Depth = 90; // its layer: over the page and its hover cards (Hover.Depth)

    static Node? _open;
    static string? _openKey;
    static Action? _letGo; // what the open menu holds on to (its owner's signal, a frame check, the hotkeys), let go as it closes
    static bool _noHotkeyBlock; // the game's hotkey manager didn't take a block: not tried again

    public static bool IsOpen => _open != null && GodotObject.IsInstanceValid(_open) && _open.IsInsideTree();

    // The open menu, if any.
    public static Node? Current => IsOpen ? _open : null;

    // items is called again on every redraw, so it can change; side builds the side column. With a toggleKey, showing
    // the same menu again closes it. from: what it opens from (a picker, Around), or null for the mouse.
    public static Node? Show(Node owner, Func<IReadOnlyList<MenuItem>> items, Rect2? from = null, string? toggleKey = null,
        Func<Action, Control>? side = null, bool keyboard = false)
    {
        var again = toggleKey != null && IsOpen && _openKey == toggleKey;
        Close();
        if (again)
            return null;

        // On the scene root, not in the page: closing it mid-edit redraws the list, which mustn't be removing it then.
        // It closes when its owner goes (one connection, let go as it closes: a page opening menus all session mustn't
        // pile them up), or once its owner is hidden (checked each frame; an owner hidden already doesn't count).
        var layer = new CanvasLayer { Name = RootName, Layer = Depth };
        owner.GetTree().Root.AddChild(layer);
        Action letGo = () => { };
        try
        {
            var gone = Callable.From(() =>
            {
                if (_open == layer)
                    Close();
            });
            owner.Connect(Node.SignalName.TreeExiting, gone, (uint)GodotObject.ConnectFlags.OneShot);
            letGo += () =>
            {
                if (GodotObject.IsInstanceValid(owner) && owner.IsConnected(Node.SignalName.TreeExiting, gone))
                    owner.Disconnect(Node.SignalName.TreeExiting, gone);
            };
            if (owner is CanvasItem shown && shown.IsVisibleInTree())
            {
                var ownerTree = owner.GetTree();
                void Hidden()
                {
                    if (_open == layer && (!GodotObject.IsInstanceValid(shown) || !shown.IsVisibleInTree()))
                        Close();
                }
                ownerTree.ProcessFrame += Hidden;
                letGo += () => ownerTree.ProcessFrame -= Hidden;
            }
            var viewport = owner.GetViewport();
            var catcher = new Control { Name = "Catcher", MouseFilter = Control.MouseFilterEnum.Stop, Size = viewport.GetVisibleRect().Size };
            layer.AddChild(catcher);

            var panel = new PanelContainer { Name = "Panel" };
            var style = new StyleBoxFlat
            {
                BgColor = new Color(0.0756f, 0.12084f, 0.18f, 0.98f), BorderColor = new Color(Gold, 0.6f),
                ContentMarginLeft = 8, ContentMarginRight = 8, ContentMarginTop = 8, ContentMarginBottom = 8,
                ShadowColor = new Color(0, 0, 0, 0.4f), ShadowSize = 8,
            };
            style.SetBorderWidthAll(2);
            style.SetCornerRadiusAll(6);
            panel.AddThemeStyleboxOverride("panel", style);
            catcher.AddChild(panel);

            var columns = new HBoxContainer { Name = "Columns" };
            columns.AddThemeConstantOverride("separation", 12);
            panel.AddChild(columns);
            var list = new VBoxContainer { Name = "Items" };
            list.AddThemeConstantOverride("separation", 2);
            columns.AddChild(list);
            Fill(list, items);
            if (side != null)
            {
                columns.AddChild(new ColorRect { Color = new Color(Gold, 0.25f), CustomMinimumSize = new Vector2(1, 0), MouseFilter = Control.MouseFilterEnum.Ignore });
                columns.AddChild(side(() => Close()));
            }

            catcher.GuiInput += e =>
            {
                if (keyboard && (e.IsActionPressed(CancelAction) || e.IsActionPressed(PauseAction)))
                {
                    catcher.AcceptEvent();
                    Close();
                    return;
                }
                if (e is not InputEventMouseButton { Pressed: true } press || panel.GetGlobalRect().HasPoint(press.GlobalPosition))
                    return;
                catcher.AcceptEvent();
                Close();
                // A right-click (or the wheel) carries on to what's underneath; a left click only closes.
                if (press.ButtonIndex != MouseButton.Left)
                    Callable.From(() => Input.ParseInputEvent(press)).CallDeferred();
            };

            // The keyboard is the menu's: focus on it, so the game's cancel reaches it, and the screen's hotkeys blocked while
            // it's open (let go as it closes, or if whoever holds it frees it first).
            if (keyboard)
            {
                catcher.FocusMode = Control.FocusModeEnum.All;
                catcher.GrabFocus();
                if (!_noHotkeyBlock)
                {
                    try
                    {
                        if (BlockHotkeys(layer) is { } release)
                        {
                            letGo += release;
                            catcher.TreeExiting += release;
                        }
                    }
                    catch (Exception e)
                    {
                        _noHotkeyBlock = true;
                        Log.Warn($"[{ModEntry.ModId}] Could not block the screen's hotkeys while a menu is open, so they stay on (logged once): {e.Message}");
                    }
                }
            }
            (_open, _openKey, _letGo) = (layer, toggleKey, letGo);

            // From the mouse (a point) by default: its top-left corner there, or, where that runs off the bottom (right) and
            // less would the other way, its bottom (right) edge there instead. From a picker the same off its edges: under it
            // where there's room below, over it where there isn't; from its left edge where there's room to the right, to its
            // right edge where there isn't. It turns only while it opens (its first sizes, through the second frame): growing
            // later (tags wrapping, the notes box), it only slides back on screen, so what the player is using stays under the
            // mouse. Kept on screen.
#if DEVTOOLS
            var anchor = from ?? new Rect2(Hover.TestMouse?.Invoke() ?? viewport.GetMousePosition(), Vector2.Zero); // (the self-test says where the mouse is)
#else
            var anchor = from ?? new Rect2(viewport.GetMousePosition(), Vector2.Zero);
#endif
            var (up, left, settled) = (false, false, false);
            void Fit()
            {
                if (!GodotObject.IsInstanceValid(panel))
                    return;
                var size = panel.GetCombinedMinimumSize();
                panel.Size = size;
                float Y(bool above) => above ? anchor.Position.Y - size.Y : anchor.End.Y;
                float X(bool back) => back ? anchor.End.X - size.X : anchor.Position.X;
                if (!settled && Hover.Turns(Y(up), size.Y, catcher.Size.Y, Y(!up)))
                    up = !up;
                if (!settled && Hover.Turns(X(left), size.X, catcher.Size.X, X(!left)))
                    left = !left;
                panel.Position = new Vector2(Math.Clamp(X(left), 0, Math.Max(0, catcher.Size.X - size.X)),
                    Math.Clamp(Y(up), 0, Math.Max(0, catcher.Size.Y - size.Y)));
            }
            panel.MinimumSizeChanged += Fit;
            panel.Position = new Vector2(anchor.Position.X, anchor.End.Y);
            Callable.From(Fit).CallDeferred();
            var (tree, frames) = (viewport.GetTree(), 0);
            void Settle()
            {
                if (GodotObject.IsInstanceValid(panel) && ++frames < 2)
                    return;
                settled = true;
                tree.ProcessFrame -= Settle;
            }
            tree.ProcessFrame += Settle;
            return layer;
        }
        catch
        {
            // Half-built (a bad item, a game update): none of it stays, least of all its catcher over the whole screen.
            if (_open == layer)
                Close();
            else
            {
                letGo();
                layer.GetParent()?.RemoveChild(layer);
                layer.QueueFree();
            }
            throw;
        }
    }

    // The screen's hotkeys off (the game's NHotkeyManager), and the way to turn them back on, once; null with no manager.
    // On its own, kept out of Show: a game update that changes the manager costs the API's menus their block, not every
    // menu its opening.
    [MethodImpl(MethodImplOptions.NoInlining)]
    static Action? BlockHotkeys(Node layer)
    {
        if (NHotkeyManager.Instance is not { } hotkeys)
            return null;
        hotkeys.AddBlockingScreen(layer);
        var released = false;
        return () =>
        {
            if (released)
                return;
            released = true;
            try
            {
                if (GodotObject.IsInstanceValid(hotkeys))
                    hotkeys.RemoveBlockingScreen(layer);
            }
            catch (Exception e)
            {
                Log.Warn($"[{ModEntry.ModId}] Could not turn the screen's hotkeys back on after a menu: {e.Message}");
            }
        };
    }

    // What a menu from a button (a picker) opens from: the button and `gap` over and under it, so the menu drops that
    // far below it, from its left edge, or opens that far above it.
    public static Rect2 Around(Control button, float gap) => button.GetGlobalRect().GrowIndividual(0, gap, 0, gap);

    // Closes the open menu, if any. True if one was open.
    public static bool Close()
    {
        var letGo = _letGo;
        _letGo = null;
        letGo?.Invoke();
        if (!IsOpen || _open is not { } open)
        {
            (_open, _openKey) = (null, null);
            return false;
        }
        (_open, _openKey) = (null, null);
        open.GetParent()?.RemoveChild(open); // now: a press right after must not land on it
        open.QueueFree();
        return true;
    }

    static void Fill(VBoxContainer list, Func<IReadOnlyList<MenuItem>> build)
    {
        var items = build();
        var marks = items.Any(i => i.Checked || i.Radio);
        foreach (var item in items)
            list.AddChild(item.OnSelect == null ? Heading(item.Label) : Row(item, marks));
    }

    // A heading: its text in gold over a line, or just the line (a divider). Not an item: no hover, no click, no focus.
    static Control Heading(string text)
    {
        var box = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        box.AddThemeConstantOverride("separation", 2);
        if (text.Length > 0)
        {
            box.AddToGroup(HeadingGroup);
            box.SetMeta("label", text);
            var label = new Label { Text = text, MouseFilter = Control.MouseFilterEnum.Ignore };
            Style(label, "res://themes/kreon_bold_shared.tres", 16, Gold);
            var margin = new MarginContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
            margin.AddThemeConstantOverride("margin_left", 12);
            margin.AddThemeConstantOverride("margin_top", 4);
            margin.AddChild(label);
            box.AddChild(margin);
        }
        box.AddChild(new ColorRect { Color = new Color(Gold, 0.3f), CustomMinimumSize = new Vector2(0, 1), MouseFilter = Control.MouseFilterEnum.Ignore });
        return box;
    }

    // One item: hover lights it, a click closes the menu and runs it.
    static Control Row(MenuItem item, bool marks)
    {
        var row = new PanelContainer
        {
            Name = item.Label, MouseFilter = Control.MouseFilterEnum.Stop,
            MouseDefaultCursorShape = item.Disabled ? Control.CursorShape.Arrow : Control.CursorShape.PointingHand,
        };
        row.AddToGroup(ItemGroup);
        row.SetMeta("label", item.Label);
        row.SetMeta("disabled", item.Disabled);
        StyleBoxFlat Box(Color color)
        {
            var box = new StyleBoxFlat { BgColor = color, ContentMarginLeft = 12, ContentMarginRight = 14, ContentMarginTop = 6, ContentMarginBottom = 6 };
            box.SetCornerRadiusAll(4);
            return box;
        }
        var normal = Box(Colors.Transparent);
        var hover = Box(new Color(0.2f, 0.32f, 0.42f));
        row.AddThemeStyleboxOverride("panel", normal);

        var line = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        line.AddThemeConstantOverride("separation", 8);
        row.AddChild(line);
        var color = item.Disabled ? new Color(Cream, 0.35f) : Cream;
        if (marks)
        {
            // A dot where only one can be the choice (profiles, a mod's category), a tick otherwise.
            var mark = new Label
            {
                Text = item.Checked ? item.Radio ? "●" : "✓" : item.Radio ? "○" : "",
                CustomMinimumSize = new Vector2(18, 0), HorizontalAlignment = HorizontalAlignment.Center,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            Style(mark, "res://themes/kreon_regular_shared.tres", 15, item.Checked ? Gold : new Color(Cream, 0.35f));
            line.AddChild(mark);
        }
        var label = new Label { Text = item.Label, MouseFilter = Control.MouseFilterEnum.Ignore };
        Style(label, "res://themes/kreon_regular_shared.tres", 20, item.Checked ? Gold : color);
        if (item.External)
            LinkMark.Add(label);
        line.AddChild(label);
        if (item.Badge is { } badge)
        {
            var mark = new Label { Name = "Badge", Text = badge.Text, MouseFilter = Control.MouseFilterEnum.Ignore };
            Style(mark, "res://themes/kreon_bold_shared.tres", 20, badge.Color);
            line.AddChild(mark);
        }
        line.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore });
        if (item.Detail is { Length: > 0 } detail)
        {
            var dim = new Label { Text = detail, MouseFilter = Control.MouseFilterEnum.Ignore, VerticalAlignment = VerticalAlignment.Center };
            Style(dim, "res://themes/kreon_regular_shared.tres", 15, new Color(Cream, 0.5f));
            line.AddChild(new Control { CustomMinimumSize = new Vector2(16, 0), MouseFilter = Control.MouseFilterEnum.Ignore });
            line.AddChild(dim);
        }
        if (item.Disabled)
            return row;

        row.MouseEntered += () => row.AddThemeStyleboxOverride("panel", hover);
        row.MouseExited += () => row.AddThemeStyleboxOverride("panel", normal);
        row.GuiInput += e =>
        {
            if (e is not InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false })
                return;
            row.AcceptEvent();
            Close();
            item.OnSelect?.Invoke();
        };
        return row;
    }

    // A menu's labels (Headings: its headings'; Rows: with whether each is dimmed), and a click on one: false if there's
    // no such row, or it's dimmed. Every build: other mods' self-tests use them through the API.
    public static List<string> Labels(Node menu) => Rows(menu).Select(r => r.Label).ToList();

    public static List<(string Label, bool Disabled)> Rows(Node menu) =>
        menu.FindChildren("*", "", true, false).Where(n => n.IsInGroup(ItemGroup))
            .Select(n => ((string)n.GetMeta("label"), n.GetMeta("disabled", false).AsBool())).ToList();

    public static List<string> Headings(Node menu) =>
        menu.FindChildren("*", "", true, false).Where(n => n.IsInGroup(HeadingGroup)).Select(n => (string)n.GetMeta("label")).ToList();

    public static bool Press(Node menu, string label)
    {
        if (menu.FindChildren("*", "", true, false).FirstOrDefault(n => n.IsInGroup(ItemGroup) && (string)n.GetMeta("label") == label) is not Control row
            || row.GetMeta("disabled", false).AsBool())
            return false;
        row.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false });
        return true;
    }

    static void Style(Control control, string font, int size, Color color)
    {
        control.AddThemeFontOverride("font", ModEntry.Font(font));
        control.AddThemeFontSizeOverride("font_size", size);
        control.AddThemeColorOverride("font_color", color);
    }
}
