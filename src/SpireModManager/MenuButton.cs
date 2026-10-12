using Godot;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;

namespace SpireModManager;

// The "Spire Mod Manager" button on the main menu: Jordan's skyline art in the lower-left corner (or
// wherever the player moved it: right-click > Move button, then click to place it), with the game's
// notification dot and a short "Relaunch to apply" note beside it while a relaunch is pending.
static class MenuButton
{
    const string Name = "SpireModManagerMenuButton";
    static readonly Color Coral = new(0.96f, 0.62f, 0.47f);

    public static void Add(NMainMenu menu)
    {
        // The game's button art if ours is missing.
        var art = ModEntry.Art("main_menu_button.png");
        var size = art?.GetSize() ?? new Vector2(260, 70);
        var button = ModEntry.AddButton(menu, "SPIRE_MOD_MANAGER.MAIN_MENU_BUTTON", size,
            () => menu.SubmenuStack.PushSubmenuType<NModdingScreen>(), art);
        button.Name = Name;
        Place(button, size);

        var dot = button.GetNode<Control>("Dot");
        var note = new Label
        {
            Name = "PendingNote", Text = Menus.Text("SPIRE_MOD_MANAGER.RELAUNCH_NOTE"), MouseFilter = Control.MouseFilterEnum.Ignore,
            VerticalAlignment = VerticalAlignment.Center, Position = new Vector2(size.X + 10, 0), Size = new Vector2(260, size.Y),
        };
        note.AddThemeFontOverride("font", ResourceLoader.Load<Font>("res://themes/kreon_regular_shared.tres"));
        note.AddThemeFontSizeOverride("font_size", 18);
        note.AddThemeColorOverride("font_color", Coral);
        note.AddThemeColorOverride("font_outline_color", new Color(0.0756f, 0.12084f, 0.18f));
        note.AddThemeConstantOverride("outline_size", 6);
        button.AddChild(note);

        void Refresh()
        {
            button.Visible = !menu.SubmenuStack.SubmenusOpen;
            dot.Visible = note.Visible = ModEntry.HasPendingChanges();
        }
        Refresh();
        menu.SubmenuStack.Connect(NSubmenuStack.SignalName.StackModified, Callable.From(Refresh));

        button.GuiInput += e =>
        {
            if (e is not InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: false })
                return;
            button.AcceptEvent();
            Menus.Show(menu, [
                new(Menus.Text("SPIRE_MOD_MANAGER.MOVE_BUTTON"), () => BeginMove(menu, button, size)),
                new(Menus.Text("SPIRE_MOD_MANAGER.RESET_POSITION"), () =>
                {
                    Store.Data.MenuButtonAt = null;
                    Store.Save();
                    Place(button, size);
                }, Disabled: Store.Data.MenuButtonAt == null),
            ]);
        };
    }

    // Where the player put it (kept on screen), or the lower-left corner, its bottom level with the Modding
    // screen's bottom row of buttons.
    static void Place(Control button, Vector2 size)
    {
        if (Store.Data.MenuButtonAt is { } at)
        {
            button.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
            var screen = button.GetViewportRect().Size;
            button.Position = new Vector2(Math.Clamp(at.X, 0, screen.X - size.X), Math.Clamp(at.Y, 0, screen.Y - size.Y));
            button.Size = size;
            return;
        }
        button.SetAnchorsPreset(Control.LayoutPreset.BottomLeft);
        button.OffsetLeft = Layout.CornerX;
        button.OffsetBottom = Layout.BottomRowY + 70 - 1080;
        button.OffsetRight = button.OffsetLeft + size.X;
        button.OffsetTop = button.OffsetBottom - size.Y;
    }

    // The button follows the mouse; a click puts it down there (saved), a right-click or Escape puts it back.
    // A see-through layer over the whole menu takes those clicks so nothing underneath is pressed.
    static void BeginMove(NMainMenu menu, Control button, Vector2 size)
    {
        var start = button.Position;
        var grab = size / 2;
        var layer = new Control { Name = "SpireModManagerMoveLayer", MouseFilter = Control.MouseFilterEnum.Stop, MouseDefaultCursorShape = Control.CursorShape.Move };
        menu.AddChild(layer);
        layer.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        var hint = new Label { Text = Menus.Text("SPIRE_MOD_MANAGER.MOVE_HINT"), HorizontalAlignment = HorizontalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore };
        hint.AddThemeFontOverride("font", ResourceLoader.Load<Font>("res://themes/kreon_bold_shared.tres"));
        hint.AddThemeFontSizeOverride("font_size", 22);
        hint.AddThemeColorOverride("font_color", new Color(1, 0.964706f, 0.886275f));
        hint.AddThemeColorOverride("font_outline_color", new Color(0.0756f, 0.12084f, 0.18f));
        hint.AddThemeConstantOverride("outline_size", 8);
        layer.AddChild(hint);
        var width = layer.GetViewportRect().Size.X;
        (hint.Position, hint.Size) = (new Vector2(width / 2 - 450, 90), new Vector2(900, 40));

        button.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
        button.Size = size;
        var screen = layer.GetViewportRect().Size;
        void Follow()
        {
            var mouse = layer.GetGlobalMousePosition() - grab;
            button.Position = new Vector2(Math.Clamp(mouse.X, 0, screen.X - size.X), Math.Clamp(mouse.Y, 0, screen.Y - size.Y));
        }
        var done = false;
        void End(bool keep)
        {
            if (done)
                return;
            done = true;
            if (keep)
            {
                Store.Data.MenuButtonAt = new ScreenPoint(button.Position.X, button.Position.Y);
                Store.Save();
            }
            else
                button.Position = start;
            if (!keep && Store.Data.MenuButtonAt == null)
                Place(button, size);
            layer.QueueFree();
        }
        Follow();
        layer.GuiInput += e =>
        {
            switch (e)
            {
                case InputEventMouseMotion:
                    Follow();
                    break;
                case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false }:
                    Follow();
                    End(keep: true);
                    break;
                case InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: false }:
                    End(keep: false);
                    break;
            }
            layer.AcceptEvent();
        };
        layer.FocusMode = Control.FocusModeEnum.All;
        layer.GrabFocus();
        layer.GuiInput += e =>
        {
            if (e is InputEventKey { Keycode: Key.Escape, Pressed: true })
                End(keep: false);
        };
    }
}
