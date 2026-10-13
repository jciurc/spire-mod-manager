using Godot;

namespace SpireModManager;

// One of the player's own details for a mod (alias, notes, tags), built and behaving the same wherever it's
// edited: the info panel and a mod's right-click menu. Several mods at once (the bulk menu): tags edit the ones
// they all share (the rest of each mod's tags stay), notes show when they're the same and typing replaces all.
// - A gold term; Save and Cancel at the right of its line while editing.
// - At rest: the value as plain text (tags as pills), or a subtle call to action ("Add alias").
// - Editing: a padded box with a hint and an inline ✕ to clear. Enter saves (Ctrl+Enter in notes), Escape
//   or Cancel puts the value back, clicking away saves, so typed text is never lost.
// - Tags also show the automatic ones (New, Local, Library, Gameplay, Cosmetic) as filled pills beside the term.
// - The value sits in a softly darker box, lighter under the mouse, darker while editing; no outlines.
sealed class ModField
{
    public enum Kind { Alias, Notes, Tags }

    static readonly Color Cream = new(1, 0.964706f, 0.886275f);
    static readonly Color Gold = new(0.937f, 0.784f, 0.318f);

    public Control Root { get; }
    readonly Kind _kind;
    readonly Func<IReadOnlyList<string>> _modIds;
    readonly bool _bulk; // several mods
    readonly Action<Kind> _saved;
    readonly Control _box; // a LineEdit, or a TextEdit for notes
    readonly HBoxContainer _buttons;
    readonly HFlowContainer? _system, _pills; // tags only
    readonly PanelContainer? _pillsBox; // the player's pills at rest, in the same box as the text
    bool _cancelling;

    // modId: the mod being shown (it can change: the info panel). saved: after a value is stored.
    public ModField(Kind kind, Func<string?> modId, Action<Kind> saved) : this(kind, () => modId() is { Length: > 0 } id ? [id] : [], saved, bulk: false)
    {
    }

    // modIds: the mods it edits (one, or several: bulk).
    public ModField(Kind kind, Func<IReadOnlyList<string>> modIds, Action<Kind> saved, bool bulk)
    {
        (_kind, _modIds, _saved, _bulk) = (kind, modIds, saved, bulk);
        var root = new VBoxContainer { Name = kind.ToString() };
        root.AddThemeConstantOverride("separation", 4);
        Root = root;

        var term = new HBoxContainer { Name = "Term", MouseFilter = Control.MouseFilterEnum.Ignore, CustomMinimumSize = new Vector2(0, 26) };
        term.AddThemeConstantOverride("separation", 6);
        var label = new Label { Text = Menus.Text(LocKey("TERM")), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, VerticalAlignment = VerticalAlignment.Center };
        Style(label, "res://themes/kreon_regular_shared.tres", 18, Gold);
        term.AddChild(label);
        if (kind == Kind.Tags)
        {
            label.SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin;
            _system = Flow("SystemTags");
            (_system.SizeFlagsHorizontal, _system.SizeFlagsVertical) = (Control.SizeFlags.ExpandFill, Control.SizeFlags.ShrinkCenter);
            term.AddChild(_system);
        }
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
            _pillsBox = new PanelContainer { Name = "CustomTags", MouseFilter = Control.MouseFilterEnum.Stop, MouseDefaultCursorShape = Control.CursorShape.Ibeam };
            Hoverable(_pillsBox, "panel");
            _pillsBox.GuiInput += e =>
            {
                if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false })
                    BeginEdit();
            };
            _pills = Flow("Pills");
            _pillsBox.AddChild(_pills);
            root.AddChild(_pillsBox);
        }

        _box = kind == Kind.Notes
            ? new TextEdit { Name = "Box", WrapMode = TextEdit.LineWrappingMode.Boundary, CustomMinimumSize = new Vector2(0, 4 * 24 + 14), ScrollFitContentHeight = false }
            : new LineEdit { Name = "Box", MaxLength = kind == Kind.Alias ? 80 : 200, CustomMinimumSize = new Vector2(0, 34) };
        Style(_box, "res://themes/kreon_regular_shared.tres", kind == Kind.Notes ? 16 : 18, Cream); // notes a little smaller: they run longer
        _box.AddThemeColorOverride("font_placeholder_color", new Color(Cream, 0.35f));
        Hoverable(_box, "normal");
        _box.AddThemeStyleboxOverride("focus", Look(Shade.Active));
        _box.AddThemeStyleboxOverride("read_only", Look(Shade.Rest));
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

    // Several mods' tags and notes in a column (the bulk menu): "Editing N selected mods" where one mod's alias
    // would be.
    public static Control BulkColumn(IReadOnlyList<string> modIds, Action<Kind> saved)
    {
        var column = new VBoxContainer { Name = "Fields", CustomMinimumSize = new Vector2(400, 0) };
        column.AddThemeConstantOverride("separation", 12);
        var heading = new Label
        {
            Name = "Editing", Text = Menus.Format("SPIRE_MOD_MANAGER.EDITING_SELECTED", ("count", modIds.Count.ToString())),
            CustomMinimumSize = new Vector2(0, 34), VerticalAlignment = VerticalAlignment.Center,
        };
        Style(heading, "res://themes/kreon_bold_shared.tres", 20, Cream);
        column.AddChild(heading);
        foreach (var kind in new[] { Kind.Tags, Kind.Notes })
            column.AddChild(new ModField(kind, () => modIds, saved, bulk: true).Root);
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
            foreach (var chip in _bulk ? Enumerable.Empty<Tags.Chip>() : Tags.For(Dependencies.Find(_modIds().FirstOrDefault() ?? "")).Where(c => !c.Custom))
                _system.AddChild(Tags.Make(chip, add => Search.ByTag(Root, chip.Text, add)));
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
        if (_pillsBox != null)
            _pillsBox.Visible = false;
        _box.GrabFocus();
    }

    // At rest: tags with a value show as pills; everything else shows its box (text or the call to action).
    void ShowRest()
    {
        if (_pills == null || _pillsBox == null || Editing)
            return;
        Clear(_pills);
        var tags = SharedTags();
        foreach (var tag in tags)
            _pills.AddChild(Tags.Make(new Tags.Chip(tag, Tags.CustomColor, Custom: true), add => Search.ByTag(Root, tag, add))); // the box around them edits
        _pillsBox.Visible = tags.Count > 0;
        _box.Visible = tags.Count == 0;
    }

    void Cancel()
    {
        _cancelling = true;
        SetText(Stored());
        _box.ReleaseFocus();
    }

    // The value shown: the mod's; for several, what they have in common (tags all of them have; notes if all the same).
    string Stored()
    {
        var infos = _modIds().Select(Store.InfoFor).ToList();
        return _kind switch
        {
            Kind.Alias => infos.Count == 1 ? infos[0]?.Alias ?? "" : "",
            Kind.Notes => infos.Select(i => i?.Notes ?? "").Distinct().Count() == 1 ? infos[0]?.Notes ?? "" : "",
            _ => string.Join(", ", SharedTags()),
        };
    }

    List<string> SharedTags()
    {
        var lists = _modIds().Select(id => Store.InfoFor(id)?.Tags ?? []).ToList();
        return lists.Count == 0 ? [] : lists[0].Where(tag => lists.All(l => l.Contains(tag, StringComparer.OrdinalIgnoreCase))).ToList();
    }

    bool NotesDiffer => _kind == Kind.Notes && _modIds().Select(id => Store.InfoFor(id)?.Notes ?? "").Distinct().Count() > 1;

    // Stores the typed value if it changed: trimmed, empty meaning none; tags tidied (repeats and blanks dropped).
    void Save()
    {
        var ids = _modIds();
        if (ids.Count == 0)
            return;
        var text = GetText().Trim();
        SetText(_kind == Kind.Tags ? string.Join(", ", Tags.Parse(text) ?? []) : text);
        if (GetText() == Stored())
            return;
        var typed = Tags.Parse(text) ?? [];
        var dropped = SharedTags().Where(t => !typed.Contains(t, StringComparer.OrdinalIgnoreCase)).ToList();
        Store.Batch(() =>
        {
            foreach (var id in ids)
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
                        case Kind.Tags when !_bulk:
                            info.Tags = Tags.Parse(text);
                            break;
                        default:
                            // Several: drop the shared tags that were taken out, add the new ones; the rest stay.
                            var own = (info.Tags ?? []).Where(t => !dropped.Contains(t, StringComparer.OrdinalIgnoreCase)).ToList();
                            own.AddRange(typed.Where(t => !own.Contains(t, StringComparer.OrdinalIgnoreCase)));
                            info.Tags = own.Count > 0 ? own : null;
                            break;
                    }
                });
        });
        var kind = _kind;
        Callable.From(() => _saved(kind)).CallDeferred(); // it redraws; not in the middle of a focus change or a removal
    }

    string LocKey(string part) => $"SPIRE_MOD_MANAGER.{_kind.ToString().ToUpperInvariant()}_{part}";

    // At rest the call to action ("Add notes"), while editing the hint.
    void Hint(bool editing)
    {
        var text = Menus.Text(NotesDiffer ? "SPIRE_MOD_MANAGER.NOTES_BULK_MIXED" : LocKey((_bulk ? "BULK_" : "") + (editing ? "HINT" : "ADD")));
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

    public enum Shade { Rest, Hover, Active }

    // The box behind a value: softly darker than the panel at rest, lighter under the mouse, darker while
    // editing. Same padding throughout, so the text doesn't jump. (Also the panel's category picker.)
    public static StyleBoxFlat Look(Shade shade)
    {
        var style = new StyleBoxFlat
        {
            BgColor = shade switch
            {
                Shade.Hover => new Color(0.16f, 0.22f, 0.29f, 0.6f),
                Shade.Active => new Color(0.01f, 0.025f, 0.04f, 0.8f),
                _ => new Color(0.01f, 0.025f, 0.04f, 0.4f),
            },
            ContentMarginLeft = 10, ContentMarginRight = 10, ContentMarginTop = 6, ContentMarginBottom = 6,
        };
        style.SetCornerRadiusAll(4);
        return style;
    }

    // The rest look, and the hover look while the mouse is over it (text boxes have no hover style of their own).
    static void Hoverable(Control control, string styleName)
    {
        control.AddThemeStyleboxOverride(styleName, Look(Shade.Rest));
        control.MouseEntered += () => control.AddThemeStyleboxOverride(styleName, Look(Shade.Hover));
        control.MouseExited += () => control.AddThemeStyleboxOverride(styleName, Look(Shade.Rest));
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
        control.AddThemeFontOverride("font", ModEntry.Font(font));
        control.AddThemeFontSizeOverride("font_size", size);
        control.AddThemeColorOverride("font_color", color);
    }
}
