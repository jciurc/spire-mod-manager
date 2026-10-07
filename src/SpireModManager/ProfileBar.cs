using Godot;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;

namespace SpireModManager;

// Profile picker across the top of the mod list: "Profile: [name ▾]" on the left, opening a list of
// profiles; New, Rename and Delete on the right (also on right-click). Rename swaps the picker for a
// text box (Enter or clicking away renames). Layout starts the list below the bar.
static class ProfileBar
{
    const float Height = 40;
    static readonly Color Gold = new(0.937f, 0.784f, 0.318f);
    static readonly Color Cream = new(1, 0.964706f, 0.886275f);

    public static void Attach(NModdingScreen screen)
    {
        Profiles.Prepare();

        var border = screen.GetNode<Control>("ModsBorder");
        var scroll = border.GetNode<Control>("ModsScrollContainer");
        var bar = new Control { Name = "SpireModManagerProfileBar", MouseFilter = Control.MouseFilterEnum.Ignore };
        border.AddChild(bar);
        bar.SetAnchorsPreset(Control.LayoutPreset.TopWide);
        bar.OffsetLeft = scroll.OffsetLeft;
        bar.OffsetRight = scroll.OffsetRight;
        bar.OffsetTop = scroll.OffsetTop;
        bar.OffsetBottom = scroll.OffsetTop + Height;

        var background = new ColorRect { Color = new Color(0.0756f, 0.12084f, 0.18f, 0.6f), MouseFilter = Control.MouseFilterEnum.Ignore };
        bar.AddChild(background);
        background.SetAnchorsPreset(Control.LayoutPreset.FullRect);

        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 8);
        bar.AddChild(row);
        row.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        row.OffsetLeft = 12;
        row.OffsetRight = -8;

        var caption = new Label { Text = Menus.Text("SPIRE_MOD_MANAGER.PROFILE"), VerticalAlignment = VerticalAlignment.Center };
        Style(caption, "res://themes/kreon_regular_shared.tres", 17, new Color(Cream, 0.7f));
        row.AddChild(caption);

        // The picker: the active profile's name and a small down arrow (the game's settings arrow, turned).
        var picker = new Button
        {
            Name = "ProfilePicker", Flat = true, Alignment = HorizontalAlignment.Left, FocusMode = Control.FocusModeEnum.None,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            IconAlignment = HorizontalAlignment.Right, ExpandIcon = false,
        };
        Style(picker, "res://themes/kreon_bold_shared.tres", 19, Gold);
        picker.AddThemeColorOverride("font_hover_color", Colors.White);
        picker.AddThemeConstantOverride("h_separation", 8);
        picker.AddThemeConstantOverride("icon_max_width", 14);
        picker.Icon = DownArrow();
        row.AddChild(picker);

        // Rename: a text box in the picker's place while editing.
        var name = new LineEdit
        {
            Name = "ProfileName", Visible = false, MaxLength = 40, SelectAllOnFocus = true, ContextMenuEnabled = false,
            CustomMinimumSize = new Vector2(320, 0), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        };
        Style(name, "res://themes/kreon_bold_shared.tres", 19, Gold);
        row.AddChild(name);

        // Beside the picker: "Unsaved •" with Save and Discard while the mod list differs from the saved
        // profile, and Save as new always.
        var unsaved = new Label { Name = "Unsaved", Text = Menus.Text("SPIRE_MOD_MANAGER.UNSAVED"), VerticalAlignment = VerticalAlignment.Center };
        Style(unsaved, "res://themes/kreon_regular_shared.tres", 16, new Color(1f, 0.82f, 0.45f));
        row.AddChild(unsaved);
        HBoxContainer? inline = null;

        List<MenuItem> Actions() => [
            new(Menus.Text("SPIRE_MOD_MANAGER.RENAME"), BeginRename),
            new(Menus.Text("SPIRE_MOD_MANAGER.DELETE"), () =>
            {
                if (Profiles.Active is { } active)
                    _ = Delete(active);
            }, Disabled: Store.Data.Profiles.Count <= 1),
        ];

        void Refresh()
        {
            var edited = Profiles.Edited;
            picker.Text = Profiles.Active is { } active ? edited ? EditedName(active) : active.Name : "";
            name.Text = Profiles.Active?.Name ?? "";
            unsaved.Visible = edited;
            inline?.QueueFree();
            List<MenuItem> buttons = edited
                ? [
                    new(Menus.Text("SPIRE_MOD_MANAGER.SAVE"), Profiles.Save),
                    new(Menus.Text("SPIRE_MOD_MANAGER.SAVE_AS_NEW"), () => Profiles.SaveAsNew(screen)),
                    new(Menus.Text("SPIRE_MOD_MANAGER.DISCARD"), () => Profiles.Discard(screen)),
                ]
                : [new(Menus.Text("SPIRE_MOD_MANAGER.SAVE_AS_NEW"), () => Profiles.SaveAsNew(screen))];
            inline = Menus.InlineButtons(buttons, "Actions");
            foreach (var (button, id) in inline.GetChildren().OfType<Button>().Zip(edited
                ? new[] { "SaveProfileButton", "SaveAsNewButton", "DiscardButton" } : ["SaveAsNewButton"]))
                button.Name = id;
            row.AddChild(inline);
        }

        // The profile list: each profile, and under it its unsaved edit if it has one; then rename and delete.
        picker.Pressed += () =>
        {
            var items = new List<MenuItem>();
            foreach (var p in Store.Data.Profiles)
            {
                var active = p == Profiles.Active;
                items.Add(new MenuItem(p.Name, () => Profiles.SwitchTo(p, screen), Checked: active && !Profiles.Edited));
                if (p.Edited != null)
                    items.Add(new MenuItem(EditedName(p), () => Profiles.SwitchTo(p, screen, edited: true), Checked: active && Profiles.Edited));
            }
            items.Add(MenuItem.Heading(""));
            items.AddRange(Actions());
            Menus.Show(bar, items);
        };
        picker.GuiInput += e =>
        {
            if (e is InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: false })
                Menus.Show(bar, Actions());
        };

        void BeginRename()
        {
            picker.Visible = false;
            name.Visible = true;
            name.GrabFocus();
            name.SelectAll();
        }
        void CommitRename()
        {
            if (Profiles.Active is { } active && !Profiles.Rename(active, name.Text))
                name.Text = active.Name; // empty or taken: put the old name back
            name.Visible = false;
            picker.Visible = true;
            Refresh();
        }
        name.TextSubmitted += _ => CommitRename();
        name.FocusExited += () =>
        {
            if (name.Visible)
                CommitRename();
        };

        async Task Delete(Profile profile)
        {
            var body = new LocString("settings_ui", "SPIRE_MOD_MANAGER.DELETE_PROFILE_BODY");
            body.Add("name", profile.Name);
            if (await Menus.Confirm(new LocString("settings_ui", "SPIRE_MOD_MANAGER.DELETE_PROFILE_TITLE"), body))
                Profiles.Delete(profile, screen);
        }

        // The bar lives as long as the screen; the event is static, so unsubscribe when it goes.
        Profiles.Switched += Refresh;
        bar.TreeExiting += () => Profiles.Switched -= Refresh;
        Refresh();
    }

    // "Modded (edited 14:32)", or with the date when it wasn't today.
    static string EditedName(Profile profile)
    {
        var at = profile.EditedAt ?? DateTime.Now;
        var when = at.Date == DateTime.Today ? at.ToString("HH:mm") : at.ToString("MMM d HH:mm");
        return Menus.Format("SPIRE_MOD_MANAGER.EDITED_PROFILE", ("name", profile.Name), ("when", when));
    }

    // The game's small gold settings arrow, turned to point down, as a texture for the picker.
    static Texture2D DownArrow()
    {
        var image = ResourceLoader.Load<Texture2D>("res://images/packed/common_ui/settings_tiny_right_arrow.png").GetImage();
        image.Decompress();
        image.Rotate90(ClockDirection.Clockwise);
        image.Resize(14, 14, Image.Interpolation.Lanczos);
        return ImageTexture.CreateFromImage(image);
    }

    static void Style(Control control, string font, int size, Color color)
    {
        control.AddThemeFontOverride("font", ResourceLoader.Load<Font>(font));
        control.AddThemeFontSizeOverride("font_size", size);
        control.AddThemeColorOverride("font_color", color);
    }
}
