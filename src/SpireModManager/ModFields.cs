using Godot;

namespace SpireModManager;

// One of the player's own fields for a mod (alias, notes, tags), the same in the info panel and a right-click menu.
// For several mods (the bulk menu), tags edit the ones all share and notes replace all (a warning counts the notes
// overwritten). At rest: the value (tags as pills) or a call to action. Editing: a box with ✕; Enter saves (Ctrl+Enter
// in notes), Escape or Cancel restores, clicking away saves. Tags also show the automatic ones, suggestions, and pills
// while typing (TagPills).
sealed class ModField
{
    public enum Kind { Alias, Notes, Tags }

    static readonly Color Cream = new(1, 0.964706f, 0.886275f);
    static readonly Color Gold = new(0.937f, 0.784f, 0.318f);
    static readonly Color Warning = new(0.961f, 0.71f, 0.42f); // amber: the bulk notes that typing will overwrite
    public const string TipName = "SpireModManagerFieldTip"; // the hover card of a term, or the bulk menu's i

    public Control Root { get; }
    readonly Kind _kind;
    readonly Func<IReadOnlyList<string>> _modIds;
    readonly bool _bulk; // several mods
    readonly Action<Kind> _saved;
    readonly Control _box; // a LineEdit, or a TextEdit for notes
    readonly HBoxContainer _buttons;
    readonly Label? _mixed; // bulk notes: "3 mods have differing notes and will be overwritten!" beside the term
    readonly HFlowContainer? _system, _pills; // tags only
    readonly PanelContainer? _pillsBox; // the player's pills at rest, in the same box as the text
    readonly TagSuggestions? _suggestions; // tags only
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
        // The term explains the field in our hover card; the box itself only ever invites ("Add note").
        var label = new Label
        {
            Name = "Label", Text = Menus.Text(LocKey("TERM")), SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin, VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Pass,
        };
        // Several mods: the terms and the heading's i open one card at once, saying how tags and notes work for all.
        if (bulk)
            Hover.Watch(label, TipName, BulkInfo, Hover.Timing.Instant, width: 380);
        else
        {
            var tip = Menus.Text(LocKey("HINT"));
            Hover.Watch(label, TipName, () => Hover.Escape(tip), width: 320);
        }
        Style(label, "res://themes/kreon_regular_shared.tres", 18, Gold);
        term.AddChild(label);
        if (bulk && kind == Kind.Notes)
        {
            // Beside the term while their notes differ: how many will be overwritten.
            var mixed = _mixed = new Label
            {
                Name = "Mixed", VerticalAlignment = VerticalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Pass, Visible = false,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsStretchRatio = 100, ClipText = true,
                TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            };
            Style(mixed, "res://themes/kreon_regular_shared.tres", 15, Warning);
            term.AddChild(mixed);
            // Cut short (Save and Cancel take the line's end while editing): the whole of it in the plain tooltip.
            Hover.Watch(mixed, "SpireModManagerFullText", () => mixed.GetThemeFont("font").GetStringSize(mixed.Text, fontSize: 15).X > mixed.Size.X
                ? Hover.Escape(mixed.Text) : null, Hover.Timing.Slow);
        }
        if (kind == Kind.Tags)
        {
            _system = Flow("SystemTags");
            (_system.SizeFlagsHorizontal, _system.SizeFlagsVertical) = (Control.SizeFlags.ExpandFill, Control.SizeFlags.ShrinkCenter);
            term.AddChild(_system);
        }
        // The term is only as wide as its word (its tooltip with it); the rest of the line pushes Save and Cancel right,
        // with the automatic tags or without them (none, or hidden while editing).
        term.AddChild(new Control { Name = "Space", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore });
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
        if (kind == Kind.Tags && _box is LineEdit tagsBox)
        {
            _suggestions = new TagSuggestions(tagsBox);
            TagPills.Attach(tagsBox);
        }

        // Notes has no built-in clear button: a small ✕ in its top-right corner while editing.
        Button? clear = null;
        if (_box is TextEdit notes)
        {
            clear = new Button
            {
                Name = "Clear", Text = "✕", Flat = true, Visible = false, FocusMode = Control.FocusModeEnum.None,
                MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            };
            Hover.Tip(clear, Menus.Text("SPIRE_MOD_MANAGER.CLEAR"));
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

        Hint();
        _box.FocusEntered += () =>
        {
            Hint();
            _buttons.Visible = true;
            if (_box is LineEdit line)
                line.ClearButtonEnabled = true;
            if (clear != null)
                clear.Visible = ((TextEdit)_box).Text.Length > 0;
        };
        _box.FocusExited += () =>
        {
            _buttons.Visible = false;
            if (_box is LineEdit line)
                line.ClearButtonEnabled = false;
            if (clear != null)
                clear.Visible = false;
            if (!_cancelling)
                Save();
            _cancelling = false;
            Hint(); // after saving: several mods' notes may be the same now
            ShowRest();
        };
        _box.GuiInput += e =>
        {
            // The suggestions' keys first: arrows, Escape closing the list, Enter taking a tag rather than saving. Then the
            // pills': Backspace and Delete on a whole pill, Copy, Cut and Paste, a comma, a double-click.
            if (_suggestions?.Use(e) == true || (_kind == Kind.Tags && _box is LineEdit tags && TagPills.Use(tags, e)))
            {
                _box.AcceptEvent();
                return;
            }
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

    // Several mods' tags and notes in a column (the bulk menu), headed "Editing N selected mods".
    public static Control BulkColumn(IReadOnlyList<string> modIds, Action<Kind> saved)
    {
        var column = new VBoxContainer { Name = "Fields", CustomMinimumSize = new Vector2(400, 0) };
        column.AddThemeConstantOverride("separation", 12);
        var heading = new Label
        {
            Name = "Editing", Text = Menus.Format("SPIRE_MOD_MANAGER.EDITING_SELECTED", ("count", Menus.Number(modIds.Count))),
            CustomMinimumSize = new Vector2(0, 34), VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore, // a Label's default, said outright: the Heading box below takes the mouse
        };
        Style(heading, "res://themes/kreon_bold_shared.tres", 20, Cream);
        // An "i" beside the heading, drawing the eye to the card on editing several mods. The heading and the i are one
        // hover target, as wide as the two; the terms below open the same card.
        var headingLine = new HBoxContainer
        {
            Name = "Heading", MouseFilter = Control.MouseFilterEnum.Pass, MouseDefaultCursorShape = Control.CursorShape.Help,
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin,
        };
        headingLine.AddThemeConstantOverride("separation", 8);
        headingLine.AddChild(heading);
        var info = new TextureRect
        {
            Name = "Info", Texture = Icons.Info, StretchMode = TextureRect.StretchModeEnum.KeepCentered, SelfModulate = new Color(Cream, 0.7f),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter, MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        headingLine.AddChild(info);
        Hover.Watch(headingLine, TipName, BulkInfo, Hover.Timing.Instant, width: 380);
        column.AddChild(headingLine);
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
            foreach (var chip in _bulk ? Enumerable.Empty<Tags.Chip>() : Tags.For(Copies.ModOf(_modIds().FirstOrDefault())).Where(c => !c.Custom))
                _system.AddChild(Tags.Make(chip, add => Search.ByChip(Root, chip, add)));
            _system.Visible = _system.GetChildCount() > 0;
        }
    }

    // Saves what's being typed now (before the panel shows another mod).
    public void Commit()
    {
        if (Editing)
            Save();
    }

    // Opens the box for typing. A click on the tag pills adds a tag: ", " goes after them with the caret after it, so
    // the suggestions offer other tags; saving tidies the ", " away.
    public void BeginEdit()
    {
        _box.Visible = true;
        if (_pillsBox != null)
            _pillsBox.Visible = false;
        if (_box is LineEdit line)
        {
            if (_kind == Kind.Tags && line.Text.Trim().Length > 0 && !line.Text.TrimEnd().EndsWith(','))
                line.Text = line.Text.TrimEnd() + ", ";
            line.CaretColumn = line.Text.Length;
        }
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

    // The call to action ("Add note"), at rest and while editing alike (what the field is for is the term's tooltip).
    // Several mods with differing notes: the warning beside the term, counting the mods whose notes typing overwrites.
    void Hint()
    {
        var text = Menus.Text(LocKey((_bulk ? "BULK_" : "") + "ADD"));
        if (_box is LineEdit line)
            line.PlaceholderText = text;
        else
            ((TextEdit)_box).PlaceholderText = text;
        if (_mixed == null)
            return;
        var withNotes = NotesDiffer ? _modIds().Count(id => Store.InfoFor(id)?.Notes is { Length: > 0 }) : 0;
        _mixed.Visible = withNotes > 0;
        _mixed.Text = withNotes == 1 ? Menus.Text("SPIRE_MOD_MANAGER.NOTES_BULK_MIXED_ONE")
            : Menus.Format("SPIRE_MOD_MANAGER.NOTES_BULK_MIXED", ("count", Menus.Number(withNotes)));
    }

    // The bulk menu's card (its i and both terms): how tags and notes work for several mods.
    public static string BulkInfo() =>
        $"[b]{Hover.Escape(Menus.Text("SPIRE_MOD_MANAGER.TAGS_TERM"))}[/b]\n{Hover.Escape(Menus.Text("SPIRE_MOD_MANAGER.TAGS_BULK_HINT"))}\n\n"
        + $"[b]{Hover.Escape(Menus.Text("SPIRE_MOD_MANAGER.NOTES_TERM"))}[/b]\n{Hover.Escape(Menus.Text("SPIRE_MOD_MANAGER.NOTES_BULK_HINT"))}";

    string GetText() => _box is LineEdit line ? line.Text : ((TextEdit)_box).Text;

    void SetText(string text)
    {
        if (_box is LineEdit line)
            line.Text = text;
        else
            ((TextEdit)_box).Text = text;
    }

    public enum Shade { Rest, Hover, Active }

    // The box behind a value: darker than the panel at rest, lighter under the mouse, darker while editing, with the
    // same padding throughout (also the panel's category picker).
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
