using Godot;

namespace SpireModManager;

// One of the player's own details for a mod (alias, notes, tags), built and behaving the same wherever it's
// edited: the info panel and a mod's right-click menu.
// - A gold term; Save and Cancel at the right of its line while editing.
// - At rest: the value as plain text (tags as pills), or a subtle call to action ("Add alias").
// - Editing: a padded box with a hint and an inline ✕ to clear. Enter saves (Ctrl+Enter in notes), Escape
//   or Cancel puts the value back, clicking away saves, so typed text is never lost.
// - Tags also show the automatic ones (Gameplay, Library, Cosmetic) as filled pills above the player's own.
sealed class ModField
{
    public enum Kind { Alias, Notes, Tags }

    static readonly Color Cream = new(1, 0.964706f, 0.886275f);
    static readonly Color Gold = new(0.937f, 0.784f, 0.318f);

    public Control Root { get; }
    readonly Kind _kind;
    readonly Func<string?> _modId;
    readonly Action<Kind> _saved;
    readonly Control _box; // a LineEdit, or a TextEdit for notes
    readonly HBoxContainer _buttons;
    readonly HFlowContainer? _system, _pills; // tags only
    bool _cancelling;

    // modId: the mod being shown (it can change: the info panel). saved: after a value is stored.
    public ModField(Kind kind, Func<string?> modId, Action<Kind> saved)
    {
        (_kind, _modId, _saved) = (kind, modId, saved);
        var root = new VBoxContainer { Name = kind.ToString() };
        root.AddThemeConstantOverride("separation", 4);
        Root = root;

        var term = new HBoxContainer { Name = "Term", MouseFilter = Control.MouseFilterEnum.Ignore, CustomMinimumSize = new Vector2(0, 26) };
        term.AddThemeConstantOverride("separation", 6);
        var label = new Label { Text = Menus.Text(LocKey("TERM")), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, VerticalAlignment = VerticalAlignment.Center };
        Style(label, "res://themes/kreon_regular_shared.tres", 18, Gold);
        term.AddChild(label);
        _buttons = Menus.InlineButtons([
            new(Menus.Text("SPIRE_MOD_MANAGER.SAVE"), () => _box?.ReleaseFocus()), // leaving the box saves
            new(Menus.Text("SPIRE_MOD_MANAGER.CANCEL"), Cancel),
        ], "Buttons");
        foreach (var (button, id) in _buttons.GetChildren().OfType<Button>().Zip(new[] { "Save", "Cancel" }))
            button.Name = id;
        _buttons.Visible = false;
        term.AddChild(_buttons);
        root.AddChild(term);

        if (kind == Kind.Tags)
        {
            _system = Flow("SystemTags");
            root.AddChild(_system);
            _pills = Flow("CustomTags");
            _pills.MouseFilter = Control.MouseFilterEnum.Stop;
            _pills.MouseDefaultCursorShape = Control.CursorShape.Ibeam;
            _pills.GuiInput += e =>
            {
                if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false })
                    BeginEdit();
            };
            root.AddChild(_pills);
        }

        _box = kind == Kind.Notes
            ? new TextEdit { Name = "Box", WrapMode = TextEdit.LineWrappingMode.Boundary, CustomMinimumSize = new Vector2(0, 4 * 24 + 14), ScrollFitContentHeight = false }
            : new LineEdit { Name = "Box", MaxLength = kind == Kind.Alias ? 80 : 200, CustomMinimumSize = new Vector2(0, 34) };
        Style(_box, "res://themes/kreon_regular_shared.tres", 18, Cream);
        _box.AddThemeColorOverride("font_placeholder_color", new Color(Cream, 0.35f));
        _box.AddThemeStyleboxOverride("normal", Look(false));
        _box.AddThemeStyleboxOverride("focus", Look(true));
        _box.AddThemeStyleboxOverride("read_only", Look(false));
        _box.MouseDefaultCursorShape = Control.CursorShape.Ibeam;
        root.AddChild(_box);

        // Notes has no built-in clear button: a small ✕ in its top-right corner while editing.
        Button? clear = null;
        if (_box is TextEdit notes)
        {
            clear = new Button
            {
                Name = "Clear", Text = "✕", Flat = true, Visible = false, FocusMode = Control.FocusModeEnum.None,
                MouseDefaultCursorShape = Control.CursorShape.PointingHand, TooltipText = Menus.Text("SPIRE_MOD_MANAGER.CLEAR"),
            };
            clear.AddThemeFontSizeOverride("font_size", 14);
            clear.AddThemeColorOverride("font_color", new Color(Cream, 0.55f));
            clear.AddThemeColorOverride("font_hover_color", Cream);
            notes.AddChild(clear);
            clear.SetAnchorsPreset(Control.LayoutPreset.TopRight);
            (clear.OffsetLeft, clear.OffsetRight, clear.OffsetTop, clear.OffsetBottom) = (-30, -4, 4, 28);
            clear.Pressed += () =>
            {
                notes.Clear();
                notes.GrabFocus();
            };
            notes.TextChanged += () => clear.Visible = notes.HasFocus() && notes.Text.Length > 0;
        }

        Hint(false);
        _box.FocusEntered += () =>
        {
            Hint(true);
            _buttons.Visible = true;
            if (_box is LineEdit line)
                line.ClearButtonEnabled = true;
            if (clear != null)
                clear.Visible = ((TextEdit)_box).Text.Length > 0;
        };
        _box.FocusExited += () =>
        {
            Hint(false);
            _buttons.Visible = false;
            if (_box is LineEdit line)
                line.ClearButtonEnabled = false;
            if (clear != null)
                clear.Visible = false;
            if (!_cancelling)
                Save();
            _cancelling = false;
            ShowRest();
        };
        _box.GuiInput += e =>
        {
            switch (e)
            {
                case InputEventKey { Keycode: Key.Escape, Pressed: true }:
                    Cancel();
                    _box.AcceptEvent();
                    break;
                case InputEventKey { Keycode: Key.Enter or Key.KpEnter, Pressed: true } enter when _box is LineEdit || enter.CtrlPressed:
                    _box.ReleaseFocus();
                    _box.AcceptEvent();
                    break;
            }
        };
        Load();
    }

    // A mod's alias, tags and notes in a column (its right-click menu), in the info panel's order.
    public static Control Column(string modId, Action<Kind> saved)
    {
        var column = new VBoxContainer { Name = "Fields", CustomMinimumSize = new Vector2(400, 0) };
        column.AddThemeConstantOverride("separation", 12);
        foreach (var kind in new[] { Kind.Alias, Kind.Tags, Kind.Notes })
            column.AddChild(new ModField(kind, () => modId, saved).Root);
        return column;
    }

    public bool Editing => _box.HasFocus();

    // Shows the stored value for the mod (unless it's being edited).
    public void Load()
    {
        if (!Editing)
        {
            SetText(Stored());
            ShowRest();
        }
        if (_system != null)
        {
            Clear(_system);
            foreach (var chip in Tags.For(Dependencies.Find(_modId() ?? "")).Where(c => !c.Custom))
                _system.AddChild(Tags.Make(chip));
            _system.Visible = _system.GetChildCount() > 0;
        }
    }

    // Saves what's being typed now (before the panel shows another mod).
    public void Commit()
    {
        if (Editing)
            Save();
    }

    // Opens the box for typing (tags: from their pills).
    public void BeginEdit()
    {
        _box.Visible = true;
        if (_pills != null)
            _pills.Visible = false;
        _box.GrabFocus();
    }

    // At rest: tags with a value show as pills; everything else shows its box (text or the call to action).
    void ShowRest()
    {
        if (_pills == null || Editing)
            return;
        Clear(_pills);
        var tags = Store.InfoFor(_modId())?.Tags ?? [];
        foreach (var tag in tags)
            _pills.AddChild(Tags.Make(new Tags.Chip(tag, Tags.CustomColor, Custom: true)));
        _pills.Visible = tags.Count > 0;
        _box.Visible = tags.Count == 0;
    }

    void Cancel()
    {
        _cancelling = true;
        SetText(Stored());
        _box.ReleaseFocus();
    }

    string Stored()
    {
        var info = Store.InfoFor(_modId());
        return _kind switch
        {
            Kind.Alias => info?.Alias ?? "",
            Kind.Notes => info?.Notes ?? "",
            _ => string.Join(", ", info?.Tags ?? []),
        };
    }

    // Stores the typed value if it changed: trimmed, empty meaning none; tags tidied (repeats and blanks dropped).
    void Save()
    {
        if (_modId() is not { Length: > 0 } id)
            return;
        var text = GetText().Trim();
        SetText(_kind == Kind.Tags ? string.Join(", ", Tags.Parse(text) ?? []) : text);
        if (GetText() == Stored())
            return;
        Store.EditMod(id, info =>
        {
            var value = text.Length > 0 ? text : null;
            switch (_kind)
            {
                case Kind.Alias:
                    info.Alias = value;
                    break;
                case Kind.Notes:
                    info.Notes = value;
                    break;
                default:
                    info.Tags = Tags.Parse(text);
                    break;
            }
        });
        var kind = _kind;
        Callable.From(() => _saved(kind)).CallDeferred(); // it redraws; not in the middle of a focus change or a removal
    }

    string LocKey(string part) => $"SPIRE_MOD_MANAGER.{_kind.ToString().ToUpperInvariant()}_{part}";

    // At rest the call to action ("Add notes"), while editing the hint.
    void Hint(bool editing)
    {
        var text = Menus.Text(LocKey(editing ? "HINT" : "ADD"));
        if (_box is LineEdit line)
            line.PlaceholderText = text;
        else
            ((TextEdit)_box).PlaceholderText = text;
    }

    string GetText() => _box is LineEdit line ? line.Text : ((TextEdit)_box).Text;

    void SetText(string text)
    {
        if (_box is LineEdit line)
            line.Text = text;
        else
            ((TextEdit)_box).Text = text;
    }

    // Same padding at rest and while editing, so the text doesn't jump; only the box appears.
    static StyleBoxFlat Look(bool active)
    {
        var style = new StyleBoxFlat
        {
            BgColor = active ? new Color(0.0756f, 0.12084f, 0.18f, 0.85f) : Colors.Transparent,
            BorderColor = active ? new Color(Gold, 0.45f) : Colors.Transparent,
            ContentMarginLeft = 10, ContentMarginRight = 10, ContentMarginTop = 6, ContentMarginBottom = 6,
        };
        style.SetBorderWidthAll(1);
        style.SetCornerRadiusAll(4);
        return style;
    }

    static HFlowContainer Flow(string name)
    {
        var flow = new HFlowContainer { Name = name, MouseFilter = Control.MouseFilterEnum.Ignore };
        flow.AddThemeConstantOverride("h_separation", 6);
        flow.AddThemeConstantOverride("v_separation", 4);
        return flow;
    }

    static void Clear(Node node)
    {
        foreach (var child in node.GetChildren())
        {
            node.RemoveChild(child);
            child.QueueFree();
        }
    }

    static void Style(Control control, string font, int size, Color color)
    {
        control.AddThemeFontOverride("font", ResourceLoader.Load<Font>(font));
        control.AddThemeFontSizeOverride("font_size", size);
        control.AddThemeColorOverride("font_color", color);
    }
}
