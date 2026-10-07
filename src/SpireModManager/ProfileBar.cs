using Godot;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;

namespace SpireModManager;

// Profile switcher across the top of the mod list: [Profile]  ◀ name ▶  [New].
// The list's scroll area is pushed down to make room. The name is editable in place (Enter to rename).
static class ProfileBar
{
    const float Height = 60;
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
        scroll.OffsetTop += Height + 8;

        var background = new ColorRect { Color = new Color(0.0756f, 0.12084f, 0.18f, 0.6f), MouseFilter = Control.MouseFilterEnum.Ignore };
        bar.AddChild(background);
        background.SetAnchorsPreset(Control.LayoutPreset.FullRect);

        var caption = new Label
        {
            Text = new LocString("settings_ui", "SPIRE_MOD_MANAGER.PROFILE").GetFormattedText(),
            VerticalAlignment = VerticalAlignment.Center,
            Position = new Vector2(16, 0),
            Size = new Vector2(100, Height),
        };
        Style(caption, "res://themes/kreon_regular_shared.tres", 22, new Color(Cream, 0.7f));
        bar.AddChild(caption);

        var name = new LineEdit
        {
            Alignment = HorizontalAlignment.Center,
            Flat = true,
            MaxLength = 40,
            CaretBlink = true,
            SelectAllOnFocus = true,
            ContextMenuEnabled = false, // right-click opens our profile menu instead
            Name = "ProfileName",
        };
        Style(name, "res://themes/kreon_bold_shared.tres", 28, Gold);
        foreach (var box in new[] { "normal", "focus", "read_only" })
            name.AddThemeStyleboxOverride(box, new StyleBoxEmpty());

        // Arrows and name between the caption and the New/Rename/Delete buttons.
        const float arrowSize = 34, left = 120, right = 236;
        var prev = Arrow("res://images/packed/common_ui/settings_tiny_left_arrow.png");
        var next = Arrow("res://images/packed/common_ui/settings_tiny_right_arrow.png");
        bar.AddChild(prev);
        bar.AddChild(name);
        bar.AddChild(next);
        prev.Position = new Vector2(left, (Height - arrowSize) / 2);
        prev.Size = new Vector2(arrowSize, arrowSize);
        // Full width between the arrows, vertically centred (a LineEdit draws its text at the top).
        name.SetAnchorsPreset(Control.LayoutPreset.HcenterWide);
        name.OffsetLeft = left + arrowSize + 8;
        name.OffsetRight = -(right + arrowSize + 8);
        name.OffsetTop = -24;
        name.OffsetBottom = 24;
        next.SetAnchorsPreset(Control.LayoutPreset.CenterRight);
        next.OffsetLeft = -(right + arrowSize);
        next.OffsetRight = -right;
        next.OffsetTop = -arrowSize / 2;
        next.OffsetBottom = arrowSize / 2;

        // New, Rename, Delete: inline at the right, and the same items on right-click of the name.
        List<MenuItem> Actions() => [
            new(Menus.Text("SPIRE_MOD_MANAGER.NEW_PROFILE"), () => Profiles.SwitchTo(Profiles.Add(), screen)),
            new(Menus.Text("SPIRE_MOD_MANAGER.RENAME"), () =>
            {
                name.GrabFocus();
                name.SelectAll();
            }),
            new(Menus.Text("SPIRE_MOD_MANAGER.DELETE"), () =>
            {
                if (Profiles.Active is { } active)
                    _ = Delete(active);
            }, Disabled: Store.Data.Profiles.Count <= 1),
        ];
        HBoxContainer? inline = null;

        void Refresh()
        {
            name.Text = Profiles.Active?.Name ?? "";
            var several = Store.Data.Profiles.Count > 1;
            prev.Visible = next.Visible = several;

            inline?.QueueFree(); // Delete's enabled state depends on how many profiles there are
            inline = Menus.InlineButtons(Actions(), "Actions");
            inline.Alignment = BoxContainer.AlignmentMode.End;
            inline.GetChild<Button>(0).Name = "NewProfileButton";
            bar.AddChild(inline);
            inline.AnchorLeft = inline.AnchorRight = 1;
            inline.AnchorBottom = 1;
            (inline.OffsetLeft, inline.OffsetRight, inline.OffsetTop, inline.OffsetBottom) = (-right + 4, -8, 12, -12);
        }

        void Cycle(int step)
        {
            var list = Store.Data.Profiles;
            var i = Profiles.Active is { } active ? list.IndexOf(active) : 0;
            Profiles.SwitchTo(list[((i + step) % list.Count + list.Count) % list.Count], screen);
        }

        void CommitRename()
        {
            if (Profiles.Active is { } active && !Profiles.Rename(active, name.Text))
                name.Text = active.Name; // empty or taken: put the old name back
            name.ReleaseFocus();
        }
        prev.Pressed += () => Cycle(-1);
        next.Pressed += () => Cycle(1);
        name.TextSubmitted += _ => CommitRename();
        name.FocusExited += () =>
        {
            if (Profiles.Active is { } active && name.Text != active.Name)
                CommitRename();
        };
        name.GuiInput += e =>
        {
            if (e is not InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: false })
                return;
            name.AcceptEvent();
            Menus.Show(bar, Actions());
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

    static TextureButton Arrow(string texture)
    {
        var arrow = new TextureButton
        {
            TextureNormal = ResourceLoader.Load<Texture2D>(texture),
            IgnoreTextureSize = true,
            StretchMode = TextureButton.StretchModeEnum.KeepAspectCentered,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
        };
        arrow.MouseEntered += () => arrow.SelfModulate = new Color(1.3f, 1.3f, 1.3f);
        arrow.MouseExited += () => arrow.SelfModulate = Colors.White;
        return arrow;
    }

    static void Style(Control control, string font, int size, Color color)
    {
        control.AddThemeFontOverride("font", ResourceLoader.Load<Font>(font));
        control.AddThemeFontSizeOverride("font_size", size);
        control.AddThemeColorOverride("font_color", color);
    }
}
