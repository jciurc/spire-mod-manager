using Godot;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;

namespace SpireModManager;

// The profile bar across the top of the mod list.
// - Left: "Profile: [name ▾]", opening the list of profiles ("Name *" for ones with unsaved changes, with
//   their mod count and save date), then Share profile and Import profile (ProfileShare); while the mod list
//   differs from the saved profile, "Unsaved •" (hover: the changes; click: listed in the info panel) and Save,
//   Save as new, Discard beside it.
// - Right: New, Duplicate, Rename, Delete (Rename and Delete also on right-click, with Share and Import).
// New, Duplicate, Save as new and Rename open a name box in the picker's place (with an inline ✕), Save
// and Cancel; a name that's empty or taken says so and can't be saved. Everything else hides meanwhile.
static class ProfileBar
{
    const float Height = 40;
    static readonly Color Gold = new(0.937f, 0.784f, 0.318f);
    static readonly Color Cream = new(1, 0.964706f, 0.886275f);
    static readonly Color UnsavedColor = new(0.96f, 0.62f, 0.47f); // a soft coral: not gold, not an error
    static readonly Color WarningColor = new(1f, 0.5f, 0.44f);
    static readonly Color UnsavedMark = new(1f, 0.42f, 0.42f); // the list's "*" for a profile with unsaved changes
    const string UnsavedCard = "SpireModManagerUnsavedCard";

    enum Naming { Rename, Blank, Duplicate, SaveAsNew }

    public static void Attach(NModdingScreen screen)
    {
        Profiles.Prepare();

        var border = screen.GetNode<Control>("ModsBorder");
        var scroll = border.GetNode<Control>("ModsScrollContainer");
        // Built off the page and added at the end, so a failure part way leaves no half-wired bar on screen.
        var bar = new Control { Name = "SpireModManagerProfileBar", MouseFilter = Control.MouseFilterEnum.Ignore };

        var background = new ColorRect { Color = new Color(0.0756f, 0.12084f, 0.18f, 0.6f), MouseFilter = Control.MouseFilterEnum.Ignore };
        bar.AddChild(background);

        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 8);
        bar.AddChild(row);

        var caption = new Label { Text = Menus.Text("SPIRE_MOD_MANAGER.PROFILE"), VerticalAlignment = VerticalAlignment.Center };
        Style(caption, "res://themes/kreon_regular_shared.tres", 17, new Color(Cream, 0.7f));
        row.AddChild(caption);

        // The picker: the active profile's name and a small down arrow (the game's settings arrow, turned; a "▾"
        // after the name if that's gone).
        var arrow = DownArrow();
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
        picker.Icon = arrow;
        row.AddChild(picker);
        // How many mods are on in it right now, beside the name.
        var count = new Label { Name = "ProfileCount", VerticalAlignment = VerticalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore };
        Style(count, "res://themes/kreon_regular_shared.tres", 15, new Color(Cream, 0.5f));
        row.AddChild(count);

        // The name box, in the picker's place while naming, with its buttons and a warning.
        var name = new LineEdit
        {
            Name = "ProfileName", Visible = false, MaxLength = 40, SelectAllOnFocus = true, ContextMenuEnabled = false, ClearButtonEnabled = true,
            CustomMinimumSize = new Vector2(300, 0), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        };
        Style(name, "res://themes/kreon_bold_shared.tres", 19, Gold);
        var field = new StyleBoxFlat
        {
            BgColor = new Color(0.05f, 0.08f, 0.12f), BorderColor = new Color(Gold, 0.6f),
            ContentMarginLeft = 10, ContentMarginRight = 8, ContentMarginTop = 3, ContentMarginBottom = 3,
        };
        field.SetBorderWidthAll(1);
        field.SetCornerRadiusAll(4);
        name.AddThemeStyleboxOverride("normal", field);
        name.AddThemeStyleboxOverride("focus", field);
        row.AddChild(name);
        var naming = Naming.Rename;
        var editing = false;
        var nameButtons = ButtonBox("NameButtons");
        nameButtons.Visible = false;
        row.AddChild(nameButtons);
        var warning = new Label { Name = "NameWarning", Visible = false, VerticalAlignment = VerticalAlignment.Center };
        Style(warning, "res://themes/kreon_regular_shared.tres", 15, WarningColor);
        row.AddChild(warning);

        // Unsaved changes: the label and what to do about them.
        var unsaved = new Label { Name = "Unsaved", Text = Menus.Text("SPIRE_MOD_MANAGER.UNSAVED"), VerticalAlignment = VerticalAlignment.Center };
        Style(unsaved, "res://themes/kreon_regular_shared.tres", 16, UnsavedColor);
        row.AddChild(unsaved);
        // Hovering it lists this profile's own unsaved changes (not everything since launch), at once (Jordan,
        // 2026-10-13); clicking lists them in the info panel, and a second click closes that again.
        unsaved.MouseFilter = Control.MouseFilterEnum.Stop;
        unsaved.MouseDefaultCursorShape = Control.CursorShape.PointingHand;
        Hover.Watch(unsaved, UnsavedCard, () =>
        {
            var lines = new List<string> { Menus.Format("SPIRE_MOD_MANAGER.UNSAVED_TITLE", ("name", Profiles.Active?.Name ?? "")) };
            lines.AddRange(HistoryBar.UnsavedLines(links: false));
            lines.Add($"\n[color=#fff6e280]{Menus.Text("SPIRE_MOD_MANAGER.UNSAVED_CLICK")}[/color]");
            return string.Join("\n", lines);
        }, Hover.Timing.Instant, width: 320, place: () => (unsaved.GlobalPosition + new Vector2(0, unsaved.Size.Y + 6), false, false));
        unsaved.GuiInput += e =>
        {
            if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false })
                HistoryBar.ToggleList(screen, HistoryBar.ListKind.Unsaved);
        };
        var unsavedButtons = ButtonBox("UnsavedButtons");
        row.AddChild(unsavedButtons);

        row.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore });

        void DeleteActive()
        {
            if (Profiles.Active is { } active)
                _ = Delete(active);
        }
        var manage = ButtonBox("Actions");
        row.AddChild(manage);

        // Share and Import, under a divider at the end of both menus (the bar has no room for more buttons).
        List<MenuItem> Sharing() => [
            MenuItem.Heading(""),
            new(Menus.Text("SPIRE_MOD_MANAGER.SHARE_PROFILE"), () => ProfileShare.ShowShare(screen)),
            new(Menus.Text("SPIRE_MOD_MANAGER.IMPORT_PROFILE"), () => ProfileShare.ShowImport(screen)),
        ];
        List<MenuItem> Actions() => [
            new(Menus.Text("SPIRE_MOD_MANAGER.RENAME"), () => BeginNaming(Naming.Rename)),
            new(Menus.Text("SPIRE_MOD_MANAGER.DELETE"), DeleteActive, Disabled: Store.Data.Profiles.Count <= 1),
            .. Sharing(),
        ];

        void Refresh()
        {
            var edited = Profiles.Edited;
            picker.Text = (Profiles.Active?.Name ?? "") + (arrow == null ? "  ▾" : "");
            picker.Visible = !editing;
            count.Visible = !editing;
            count.Text = Profiles.CountText();
            (name.Visible, nameButtons.Visible) = (editing, editing);
            if (!editing)
                warning.Visible = false;
            unsaved.Visible = unsavedButtons.Visible = edited && !editing;
            manage.Visible = !editing;
            if (manage.GetNodeOrNull<Button>("DeleteProfileButton") is { } delete)
                delete.Disabled = Store.Data.Profiles.Count <= 1;
        }

        // The profile list, the active one marked, then Share and Import; a second press on the picker closes it.
        picker.Pressed += () => Menus.Show(bar, Store.Data.Profiles.Select(p => new MenuItem(
                p.Name, () => Profiles.SwitchTo(p, screen), Checked: p == Profiles.Active, Radio: true, Detail: Profiles.Details(p),
                Badge: p.Edited != null ? (" *", UnsavedMark) : null)).Concat(Sharing()).ToList(),
            at: picker.GlobalPosition + new Vector2(0, picker.Size.Y + 4), toggleKey: "profiles");
        picker.GuiInput += e =>
        {
            if (e is InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: false })
                Menus.Show(bar, Actions());
        };

        void BeginNaming(Naming kind)
        {
            if (Profiles.Active is not { } active)
                return;
            naming = kind;
            editing = true;
            name.Text = kind switch
            {
                Naming.Rename => active.Name,
                Naming.Blank => Profiles.NewName(),
                _ => Profiles.CopyName(active.Name),
            };
            Refresh();
            CheckName();
            name.GrabFocus();
            name.SelectAll();
        }
        string? CheckName()
        {
            var problem = Profiles.NameProblem(name.Text, naming == Naming.Rename ? Profiles.Active : null);
            warning.Text = problem ?? "";
            warning.Visible = problem != null;
            if (nameButtons.GetNodeOrNull<Button>("SaveNameButton") is { } save)
                save.Disabled = problem != null;
            return problem;
        }
        void CommitName()
        {
            if (!editing || CheckName() != null || Profiles.Active is not { } active)
                return;
            var chosen = name.Text.Trim();
            editing = false;
            switch (naming)
            {
                case Naming.Rename:
                    Profiles.Rename(active, chosen);
                    break;
                case Naming.Blank:
                    Profiles.Create(Profiles.NewKind.Blank, chosen, screen);
                    break;
                case Naming.Duplicate:
                    Profiles.Create(Profiles.NewKind.Duplicate, chosen, screen);
                    break;
                case Naming.SaveAsNew:
                    Profiles.Create(Profiles.NewKind.SaveAsNew, chosen, screen);
                    break;
            }
            Refresh();
        }
        void EndNaming()
        {
            editing = false;
            Refresh();
        }
        name.TextChanged += _ => CheckName();
        name.TextSubmitted += _ => CommitName();
        name.GuiInput += e =>
        {
            if (e is InputEventKey { Keycode: Key.Escape, Pressed: true })
            {
                EndNaming();
                name.AcceptEvent();
            }
        };
        // Clicking away cancels: nothing is created or renamed without Save or Enter.
        name.FocusExited += () =>
        {
            if (editing)
                Callable.From(() =>
                {
                    // The name box's own buttons don't take focus; anything else that took it ends the naming.
                    if (editing && !name.HasFocus())
                        EndNaming();
                }).CallDeferred();
        };

        async Task Delete(Profile profile)
        {
            if (await Menus.Confirm(new LocString("settings_ui", "SPIRE_MOD_MANAGER.DELETE_PROFILE_TITLE"), DeleteBody(profile)))
                Profiles.Delete(profile, screen);
        }

        // The buttons, now that everything they use exists.
        AddButtons(nameButtons, [
            ("SaveNameButton", Menus.Text("SPIRE_MOD_MANAGER.SAVE"), () => CommitName()),
            ("CancelNameButton", Menus.Text("SPIRE_MOD_MANAGER.CANCEL"), EndNaming),
        ]);
        AddButtons(unsavedButtons, [
            ("SaveProfileButton", Menus.Text("SPIRE_MOD_MANAGER.SAVE"), Profiles.Save),
            ("SaveAsNewButton", Menus.Text("SPIRE_MOD_MANAGER.SAVE_AS_NEW"), () => BeginNaming(Naming.SaveAsNew)),
            ("DiscardButton", Menus.Text("SPIRE_MOD_MANAGER.DISCARD"), () => Profiles.Discard(screen)),
        ]);
        Hover.Tip(unsavedButtons.GetNode<Button>("SaveProfileButton"), Menus.Text("SPIRE_MOD_MANAGER.SAVE_KEYS"));
        AddButtons(manage, [
            ("NewProfileButton", Menus.Text("SPIRE_MOD_MANAGER.NEW_PROFILE"), () => BeginNaming(Naming.Blank)),
            ("DuplicateProfileButton", Menus.Text("SPIRE_MOD_MANAGER.DUPLICATE"), () => BeginNaming(Naming.Duplicate)),
            ("RenameProfileButton", Menus.Text("SPIRE_MOD_MANAGER.RENAME"), () => BeginNaming(Naming.Rename)),
            ("DeleteProfileButton", Menus.Text("SPIRE_MOD_MANAGER.DELETE"), DeleteActive),
        ]);

        // On the page now that it's all built, then placed as before: over the top of the list.
        border.AddChild(bar);
        bar.SetAnchorsPreset(Control.LayoutPreset.TopWide);
        bar.OffsetLeft = scroll.OffsetLeft;
        bar.OffsetRight = scroll.OffsetRight;
        bar.OffsetTop = scroll.OffsetTop;
        bar.OffsetBottom = scroll.OffsetTop + Height;
        background.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        row.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        row.OffsetLeft = 12;
        row.OffsetRight = -8;

        // The bar lives as long as the screen; the event is static, so unsubscribe when it goes.
        void Recount() => count.Text = Profiles.CountText();
        History.Changed += Recount; // every tickbox change
        Profiles.Switched += Refresh;
        bar.TreeExiting += () =>
        {
            Profiles.Switched -= Refresh;
            History.Changed -= Recount;
        };
        Refresh();
    }

    // Delete's question: "Delete profile Brother co-op (12 mods)?", then that the mods stay installed.
    public static LocString DeleteBody(Profile profile)
    {
        var body = new LocString("settings_ui", "SPIRE_MOD_MANAGER.DELETE_PROFILE_BODY");
        body.Add("name", Hover.Escape(profile.Name)); // a "[" in the name stays text, not markup
        body.Add("mods", Profiles.ModCount(profile));
        return body;
    }

    static HBoxContainer ButtonBox(string name)
    {
        var box = new HBoxContainer { Name = name, MouseFilter = Control.MouseFilterEnum.Ignore, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        box.AddThemeConstantOverride("separation", 4);
        return box;
    }

    static void AddButtons(HBoxContainer box, (string Name, string Text, Action OnPressed)[] buttons)
    {
        foreach (var (name, text, onPressed) in buttons)
        {
            var button = Menus.SmallButton(text, onPressed, name: name);
            button.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            box.AddChild(button);
        }
    }

    // The game's small gold settings arrow, turned to point down, as a texture for the picker; null if it's gone.
    static Texture2D? DownArrow()
    {
        if (ModEntry.Load<Texture2D>("res://images/packed/common_ui/settings_tiny_right_arrow.png")?.GetImage() is not { } image)
            return null;
        image.Decompress();
        image.Rotate90(ClockDirection.Clockwise);
        image.Resize(14, 14, Image.Interpolation.Lanczos);
        return ImageTexture.CreateFromImage(image);
    }

    static void Style(Control control, string font, int size, Color color)
    {
        control.AddThemeFontOverride("font", ModEntry.Font(font));
        control.AddThemeFontSizeOverride("font_size", size);
        control.AddThemeColorOverride("font_color", color);
    }
}
