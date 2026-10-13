using Godot;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;

namespace SpireModManager;

// The "Spire Mod Manager" button on the main menu: Jordan's skyline art in the lower-left corner (or
// wherever the player moved it: right-click > Move button, then click to place it), with the game's
// notification dot and a short "Relaunch to apply" note beside it while a relaunch is pending. Its
// right-click menu also has Relaunch game, the Modding page's Relaunch Game from here.
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
        // Our Modding page couldn't hook in at all (ModdingScreenPatch didn't apply), so it can't say why it's the
        // game's: the note does, always, in place of the relaunch note.
        var outdated = ModEntry.FailedPatches.Contains(nameof(ModdingScreenPatch));
        var note = new Label
        {
            Name = "PendingNote", Text = Menus.Text(outdated ? "SPIRE_MOD_MANAGER.NEEDS_UPDATE" : "SPIRE_MOD_MANAGER.RELAUNCH_NOTE"),
            MouseFilter = Control.MouseFilterEnum.Ignore,
            VerticalAlignment = VerticalAlignment.Center, Position = new Vector2(size.X + 10, 0), Size = new Vector2(260, size.Y),
        };
        note.AddThemeFontOverride("font", ModEntry.Font("res://themes/kreon_regular_shared.tres"));
        note.AddThemeFontSizeOverride("font_size", 18);
        note.AddThemeColorOverride("font_color", Coral);
        note.AddThemeColorOverride("font_outline_color", new Color(0.0756f, 0.12084f, 0.18f));
        note.AddThemeConstantOverride("outline_size", 6);
        button.AddChild(note);

        void Refresh()
        {
            button.Visible = !menu.SubmenuStack.SubmenusOpen;
            dot.Visible = ModEntry.HasPendingChanges();
            note.Visible = dot.Visible || outdated;
        }
        Refresh();
        menu.SubmenuStack.Connect(NSubmenuStack.SignalName.StackModified, Callable.From(Refresh));
        // A change made on the main menu itself (a fix in the relaunch check) can start or end a pending relaunch.
        // Changes on a page over the menu are caught as it closes, above.
        void Changed()
        {
            if (!menu.SubmenuStack.SubmenusOpen)
                Refresh();
        }
        History.Changed += Changed;
        button.TreeExiting += () => History.Changed -= Changed;

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
                // A divider, then what the Modding page's Relaunch Game does: first the check for mods that won't
                // load (its dialog over the main menu), then the relaunch.
                MenuItem.Heading(""),
                new(Menus.Text("SPIRE_MOD_MANAGER.RELAUNCH_GAME"), () => RelaunchCheck.Relaunch(menu)),
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

    // Move mode: the button follows the mouse (by how far it moves, so it doesn't jump); a left click puts it
    // down there (remembered), a right-click or Escape puts it back. Its own layer above the whole menu
    // shows the hint and keeps clicks off the menu underneath; the mouse and keys are read every frame, so
    // nothing else on the menu can take them first. Leaving the menu cancels it.
    public const string MoveLayerName = "SpireModManagerMoveLayer";

    // While moving: cancels it (Escape, through ShortcutsPatch, which then keeps the key from the game; the game's
    // own Escape on the main menu opens its save profile screen). False when not moving.
    static Action? _cancelMove;

    public static bool CancelMove()
    {
        if (_cancelMove is not { } cancel)
            return false;
        cancel();
        return true;
    }

    static void BeginMove(NMainMenu menu, Control button, Vector2 size)
    {
        if (menu.HasNode(MoveLayerName))
            return;
        var layer = new CanvasLayer { Name = MoveLayerName, Layer = 100 };
        menu.AddChild(layer);
        var screen = menu.GetViewportRect().Size;
        var blocker = new Control { MouseFilter = Control.MouseFilterEnum.Stop, MouseDefaultCursorShape = Control.CursorShape.Move, Size = screen };
        layer.AddChild(blocker);
        var hint = new Label
        {
            Text = Menus.Text("SPIRE_MOD_MANAGER.MOVE_HINT"), HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore, Position = new Vector2(screen.X / 2 - 450, 90), Size = new Vector2(900, 40),
        };
        hint.AddThemeFontOverride("font", ModEntry.Font("res://themes/kreon_bold_shared.tres"));
        hint.AddThemeFontSizeOverride("font_size", 22);
        hint.AddThemeColorOverride("font_color", new Color(1, 0.964706f, 0.886275f));
        hint.AddThemeColorOverride("font_outline_color", new Color(0.0756f, 0.12084f, 0.18f));
        hint.AddThemeConstantOverride("outline_size", 8);
        blocker.AddChild(hint);

        var start = button.Position;
        var startAnchors = Store.Data.MenuButtonAt;
        button.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
        (button.Position, button.Size) = (start, size);
        Vector2 Mouse() => Hover.Mouse(menu);
        // The cursor jumps to the button's left end, halfway up: it holds the button by its edge. (The warped
        // position isn't reported until the next mouse event, so start from where it was sent.)
        var grip = button.GetGlobalRect().Position + new Vector2(24, size.Y / 2);
        if (Hover.TestMouse == null)
            menu.GetViewport().WarpMouse(grip);
        var mouseAtStart = Hover.TestMouse?.Invoke() ?? grip;
        // Buttons already held when move mode starts (the click on "Move button") don't count until released.
        var leftWasDown = true;
        var rightWasDown = true;
        var tree = menu.GetTree();

        void Frame()
        {
            if (!GodotObject.IsInstanceValid(layer) || !layer.IsInsideTree() || !menu.IsVisibleInTree() || menu.SubmenuStack.SubmenusOpen)
            {
                End(keep: false);
                return;
            }
            var offset = Mouse() - mouseAtStart;
            button.Position = new Vector2(Math.Clamp(start.X + offset.X, 0, screen.X - size.X), Math.Clamp(start.Y + offset.Y, 0, screen.Y - size.Y));
            var left = Input.IsMouseButtonPressed(MouseButton.Left);
            var right = Input.IsMouseButtonPressed(MouseButton.Right);
            if (Input.IsKeyPressed(Key.Escape) || (rightWasDown && !right && !leftWasDown))
                End(keep: false);
            else if (leftWasDown && !left && !rightWasDown)
                End(keep: true);
            (leftWasDown, rightWasDown) = (left, right);
        }
        var done = false;
        var following = false;
        void End(bool keep)
        {
            if (done)
                return;
            done = true;
            _cancelMove = null;
            if (following)
                tree.ProcessFrame -= Frame;
            if (keep)
            {
                Store.Data.MenuButtonAt = new ScreenPoint(button.Position.X, button.Position.Y);
                Store.Save();
            }
            else
            {
                Store.Data.MenuButtonAt = startAnchors;
                Place(button, size);
            }
            if (GodotObject.IsInstanceValid(layer))
            {
                layer.GetParent()?.RemoveChild(layer);
                layer.QueueFree();
            }
        }
        _cancelMove = () => End(keep: false);
        // The held buttons from the menu click: seen released on the first frames, then the next press counts.
        // Not if it has already ended: an Escape in the same frame as the click.
        Callable.From(() =>
        {
            if (done)
                return;
            leftWasDown = Input.IsMouseButtonPressed(MouseButton.Left);
            rightWasDown = Input.IsMouseButtonPressed(MouseButton.Right);
            tree.ProcessFrame += Frame;
            following = true;
        }).CallDeferred();
    }
}
