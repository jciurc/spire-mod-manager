using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;
using MegaCrit.Sts2.addons.mega_text;

namespace SpireModManager;

// The main menu's "Default (12 mods)" button: the game's own main-menu text button (a copy of its Settings button, so
// the same font, colours, outline, shadow and hover, with the carets either side, at a smaller size), naming the active
// profile and how many mods are on; hovering it for a second says what it opens and names the profile. It always
// opens our view of the page, even when Vanilla View was the last one shown (the game's way in, from its Settings,
// keeps the last one). In the lower-left corner, or wherever the player
// moved it (right-click > Move button, then click to place it), with the game's notification dot and a short "Relaunch
// to apply" note centred above it while a relaunch is pending. Its right-click menu, under our name and version, also
// has Open translations folder, and Relaunch game, the Modding page's Relaunch Game from here.
static class MenuButton
{
    const string Name = "SpireModManagerMenuButton";
    static readonly Color Coral = new(0.96f, 0.62f, 0.47f);
    // A long profile name is cut to this width with "…" (at the button's font size).
    const float NameWidth = 360;
    // Room either side of the text, inside the button (the game's are wider than their words too); the hover carets
    // sit in it (the game puts them out to 54 from the text, NMainMenu.MainMenuButtonFocused), so wherever the button
    // is kept on screen, they are too. Public for the self-test.
    public const float Margin = 56;
    // The text's size: smaller than the game's buttons (Jordan, 2026-10-14; 26 since 2026-10-15). Public for the self-test.
    public const int TextSize = 26;
    // The pending note: this far above the button.
    const float NoteGap = 0;
    // If the game's button is gone: one of ours (AddTextButton), this size.
    static readonly Vector2 FallbackSize = new(460, 70);

    // The game's main menu draws carets either side of its text button under the mouse; ours gets them too.
    static readonly MethodInfo? CaretsOn = AccessTools.Method(typeof(NMainMenu), "MainMenuButtonFocused");
    static readonly MethodInfo? CaretsOff = AccessTools.Method(typeof(NMainMenu), "MainMenuButtonUnfocused");

    public static void Add(NMainMenu menu)
    {
        void Open()
        {
            CategoryList.PreferOurs(); // our view, whatever was last shown
            menu.SubmenuStack.PushSubmenuType<NModdingScreen>();
        }
        var button = Native(menu, Open) ?? ModEntry.AddTextButton(menu, "", FallbackSize, Open);
        button.Name = Name;
        // The game's label, or none for the fallback (its text then goes through ModEntry.SetText, its size fixed).
        var label = button is NMainMenuTextButton ? button.GetChild<MegaLabel>(0) : null;
        var height = label != null ? Math.Max(button.CustomMinimumSize.Y, 50) : FallbackSize.Y;
        button.CustomMinimumSize = label != null ? new Vector2(0, height) : FallbackSize;

        // The notification dot (none if the game's is gone: the note still shows).
        if (button.FindChild("Dot", true, false) is not Control dot)
            button.AddChild(dot = new Control { Name = "Dot", MouseFilter = Control.MouseFilterEnum.Ignore });
        // Our Modding page couldn't hook in at all (ModdingScreenPatch didn't apply), so it can't say why it's the
        // game's: the note does, always, in place of the relaunch note.
        var outdated = ModEntry.FailedPatches.Contains(nameof(ModdingScreenPatch));
        var note = new Label
        {
            Name = "PendingNote", MouseFilter = Control.MouseFilterEnum.Ignore, VerticalAlignment = VerticalAlignment.Center,
        };
        note.AddThemeFontOverride("font", ModEntry.Font("res://themes/kreon_regular_shared.tres"));
        note.AddThemeFontSizeOverride("font_size", 18);
        note.AddThemeColorOverride("font_color", Coral);
        note.AddThemeColorOverride("font_outline_color", new Color(0.0756f, 0.12084f, 0.18f));
        note.AddThemeConstantOverride("outline_size", 6);
        button.AddChild(note);

        // The text, and with it the button's width: its words and the margins. Kept in place (Place), and on screen.
        void Retext()
        {
            // The page's own line while a relaunch is pending, word for word (Jordan, 2026-10-16: one text, one less
            // to translate).
            note.Text = Menus.Text(outdated ? "SPIRE_MOD_MANAGER.NEEDS_UPDATE" : "SPIRE_MOD_MANAGER.PENDING_RESTART");
            // What it opens, and the profile in gold; a second's wait, as the mouse crosses it for the game's buttons.
            Hover.Tip(button, Tip(Profiles.Active?.Name ?? ""), Hover.Timing.Late, markup: true);
            if (label == null)
                ModEntry.SetText(button, Text(ModEntry.Font("res://themes/kreon_bold_shared.tres"), TextSize)); // the name cut as for ours; the fallback draws at its own size
            else
            {
                label.RefreshFont(); // the language's own font, as the game's buttons take theirs
                label.Text = Text(label.GetThemeFont("font"), label.GetThemeFontSize("font_size"));
                // Measured now, not from label.Size: Godot resizes a label for new text a frame later, and not at all
                // while it's hidden (on a page over the menu). Its box is set to that width, centred, so its size
                // follows at once.
                var words = label.GetCombinedMinimumSize();
                (label.OffsetLeft, label.OffsetRight) = (-words.X / 2, words.X / 2);
                label.PivotOffset = new Vector2(words.X, Math.Max(label.Size.Y, words.Y)) / 2; // hover grows it from the middle
                button.CustomMinimumSize = new Vector2(words.X + 2 * Margin, height);
            }
            Place(button);
        }
        Retext();

        void Refresh()
        {
            button.Visible = !menu.SubmenuStack.SubmenusOpen;
            dot.Visible = ModEntry.HasPendingChanges();
            note.Visible = dot.Visible || outdated;
        }
        Refresh();
        // Back from a page over the menu: the profile or its mods may have changed there.
        menu.SubmenuStack.Connect(NSubmenuStack.SignalName.StackModified, Callable.From(() =>
        {
            Retext();
            Refresh();
        }));
        // A change made on the main menu itself (a fix in the relaunch check) can start or end a pending relaunch. Changes
        // on a page over the menu are caught as it closes, above, but the text follows at once (it's hidden meanwhile).
        void Changed()
        {
            Retext();
            if (!menu.SubmenuStack.SubmenusOpen)
                Refresh();
        }
        History.Changed += Changed;
        Profiles.Switched += Retext;
        // The language: its text and font. The game goes through its list of these as it calls them, so ours leaves it a
        // frame later, never during that.
        LocManager.LocaleChangeCallback language = () =>
        {
            if (GodotObject.IsInstanceValid(button))
                Retext();
        };
        LocManager.Instance.SubscribeToLocaleChange(language);
        button.TreeExiting += () =>
        {
            History.Changed -= Changed;
            Profiles.Switched -= Retext;
            Callable.From(() => LocManager.Instance.UnsubscribeToLocaleChange(language)).CallDeferred();
        };

        button.GuiInput += e =>
        {
            if (e is not InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: false })
                return;
            button.AcceptEvent();
            Menus.Show(menu, [
                // Whose menu it is: our name and version, a heading (not an item), over a line.
                MenuItem.Heading(ModEntry.Version is { } version
                    ? Menus.Format("SPIRE_MOD_MANAGER.MENU_HEADER", ("version", version))
                    : Menus.Text("SPIRE_MOD_MANAGER.POPUP_MARK")),
                new(Menus.Text("SPIRE_MOD_MANAGER.MOVE_BUTTON"), () => BeginMove(menu, button)),
                new(Menus.Text("SPIRE_MOD_MANAGER.RESET_POSITION"), () =>
                {
                    Store.Data.MenuButtonAt = null;
                    Store.Save();
                    Place(button);
                }, Disabled: Store.Data.MenuButtonAt == null),
                // Where drop-in translations go (Translations), made if needed and opened.
                new(Menus.Text("SPIRE_MOD_MANAGER.OPEN_TRANSLATIONS"), Translations.Open),
                // A divider, then what the Modding page's Relaunch Game does: first the check for mods that won't
                // load (its dialog over the main menu), then the relaunch. Last and on its own, as with nothing wrong
                // it quits at once: a slip from the item above shouldn't land on it.
                MenuItem.Heading(""),
                new(Menus.Text("SPIRE_MOD_MANAGER.RELAUNCH_GAME"), () => RelaunchCheck.Relaunch(menu)),
            ]);
        };
    }

    // "Default (12 mods)": the active profile, its name cut short if long (in this font, at this size), and
    // how many mods are on, the count the profile bar shows beside the name.
    public static string Text(Font font, int size) => Text(Profiles.Active?.Name ?? "", Profiles.OnCount(), font, size);

    // The same for any name and count (the self-test).
    public static string Text(string name, int on, Font font, int size) =>
        Menus.Format(on == 1 ? "SPIRE_MOD_MANAGER.MAIN_MENU_PROFILE_ONE" : "SPIRE_MOD_MANAGER.MAIN_MENU_PROFILE",
            ("name", RowTitles.Cut(font, name, size, NameWidth)), ("count", Menus.Number(on)));

    // Its card's text (BBCode) for a profile: "View and manage installed mods. Currently using mod profile Default."
    public static string Tip(string profile) => Menus.Format("SPIRE_MOD_MANAGER.MAIN_MENU_TIP", ("profile", Hover.Escape(profile)));

    // A copy of the game's Settings button on the main menu, with our action (and none of the game's), its label for our
    // text, and a copy of the game's Timeline notification dot on that label, hidden; null if a game update moved them.
    static Control? Native(NMainMenu menu, Action onPressed)
    {
        if (menu.GetNodeOrNull<NMainMenuTextButton>("MainMenuTextButtons/SettingsButton") is not { } settings
            || settings.Duplicate((int)(Node.DuplicateFlags.Scripts | Node.DuplicateFlags.UseInstantiation)) is not NMainMenuTextButton button)
            return null;
        if (button.GetChildCount() == 0 || button.GetChild(0) is not MegaLabel label)
        {
            button.Free();
            return null;
        }
        label.AutoSizeEnabled = false; // our size, not one fitted to the game's box
        label.AddThemeFontSizeOverride("font_size", TextSize);
        if (menu.GetNodeOrNull<Control>("%TimelineNotificationDot")?.Duplicate() is Control dot)
        {
            dot.UniqueNameInOwner = false;
            dot.Name = "Dot";
            dot.Visible = false;
            label.AddChild(dot);
        }
        menu.AddChild(button);
        button.Released += _ => onPressed();
        // As the game does for its own: the carets go to it once it has taken the focus, and fade as it loses it.
        bool Here() => GodotObject.IsInstanceValid(menu) && menu.IsInsideTree() && GodotObject.IsInstanceValid(button);
        button.Focused += _ => Callable.From(() =>
        {
            if (Here())
                CaretsOn?.Invoke(menu, [button]);
        }).CallDeferred();
        button.Unfocused += _ =>
        {
            if (Here())
                CaretsOff?.Invoke(menu, [button]);
        };
        return button;
    }

    // Where the player put it (kept on screen), or the lower-left corner, level with the Modding screen's bottom row
    // of buttons (70 tall from BottomRowY). Again whenever its text changes its width. Public for the self-test.
    public static void Place(Control button)
    {
        var size = button.CustomMinimumSize;
        var screen = button.GetViewportRect().Size;
        if (Store.Data.MenuButtonAt is { } at)
        {
            button.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
            button.Position = new Vector2(Math.Clamp(at.X, 0, Math.Max(0, screen.X - size.X)), Math.Clamp(at.Y, 0, Math.Max(0, screen.Y - size.Y)));
            button.Size = size;
        }
        else
        {
            button.SetAnchorsPreset(Control.LayoutPreset.BottomLeft);
            button.OffsetLeft = Layout.CornerX;
            button.OffsetBottom = Layout.BottomRowY + 35 + size.Y / 2 - 1080;
            button.OffsetRight = button.OffsetLeft + size.X;
            button.OffsetTop = button.OffsetBottom - size.Y;
        }
        // The note centred above it (Jordan, 2026-10-14), kept on screen: below it if there's no room above.
        if (button.GetNodeOrNull<Label>("PendingNote") is { } note)
        {
            var words = note.GetCombinedMinimumSize();
            var x = Math.Clamp(button.Position.X + (size.X - words.X) / 2, 0, Math.Max(0, screen.X - words.X)) - button.Position.X;
            var above = button.Position.Y >= words.Y + NoteGap;
            note.HorizontalAlignment = HorizontalAlignment.Center;
            (note.Position, note.Size) = (new Vector2(x, above ? -words.Y - NoteGap : size.Y + NoteGap), words);
        }
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

    static void BeginMove(NMainMenu menu, Control button)
    {
        if (menu.HasNode(MoveLayerName))
            return;
        var size = button.CustomMinimumSize;
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
                Place(button); // where it is: the note takes its side
            }
            else
            {
                Store.Data.MenuButtonAt = startAnchors;
                Place(button);
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
