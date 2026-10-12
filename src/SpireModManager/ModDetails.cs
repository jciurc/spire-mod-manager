using Godot;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;
using MegaCrit.Sts2.addons.mega_text;

namespace SpireModManager;

// The info panel in our views, top to bottom: the title (the player's alias if any), "Source:" (the
// Workshop page or "Local mod"), a thumbnail beside them, problems, the author's description and facts,
// then the player's own Tags, Alias and Notes right below them, edited in place, and Open Folder.
// Rows show the alias with the original name small and dim beside it, and a pencil to edit it in place.
// Vanilla View: the panel and rows exactly as the game makes them.
static class ModDetails
{
    const string NotesColor = "#9fd8ff";
    static readonly Color Cream = new(1, 0.964706f, 0.886275f);
    static readonly Color Gold = new(0.937f, 0.784f, 0.318f);
    const string ModIdMeta = "spire_mod_manager_mod_id";
    const string FolderMeta = "spire_mod_manager_folder";
    const string PencilName = "SpireModManagerPencil";
    const float PencilWidth = 28;
    const string PencilHoverMeta = "spire_mod_manager_pencil_hover";

    // After the game fills the panel for a mod.
    public static void Fill(NModInfoContainer container, Mod mod)
    {
        if (Store.Data.VanillaView)
        {
            HideButtons(container);
            return;
        }
        CommitEdits(container); // whatever was typed for the previous mod
        var id = mod.manifest?.id;
        var info = Store.InfoFor(id);
        var name = mod.manifest?.name ?? id ?? "";
        var title = container.GetNode<MegaRichTextLabel>("ModTitle");
        var description = container.GetNode<MegaRichTextLabel>("ModDescription");
        if (info?.Alias is { } alias)
            title.Text = alias;
        description.Text = Describe(mod, info?.Alias != null ? name : null);

        container.SetMeta(ModIdMeta, id ?? "");
        container.SetMeta(FolderMeta, mod.path);

        // Under the title: "Source: Workshop ID 123" linking to its page, or "Source: Local mod".
        var source = container.GetNodeOrNull<Control>(SourceName) ?? AddSource(container);
        source.Visible = true;
        var link = source.GetNode<LinkButton>("SpireModManagerLink");
        if (mod.workshopId is { } workshopId)
        {
            link.Text = Menus.Format("SPIRE_MOD_MANAGER.WORKSHOP_ID", ("id", workshopId.ToString()));
            link.Uri = $"https://steamcommunity.com/sharedfiles/filedetails/?id={workshopId}";
            link.Disabled = false;
            link.Underline = LinkButton.UnderlineMode.OnHover;
        }
        else
        {
            link.Text = Menus.Text("SPIRE_MOD_MANAGER.LOCAL_MOD");
            link.Uri = "";
            link.Disabled = true;
            link.Underline = LinkButton.UnderlineMode.Never;
        }

        var yours = container.GetNodeOrNull<Control>(FieldsName) ?? AddYours(container);
        yours.Visible = id != null;
        yours.GetNode<LineEdit>("Tags").Text = string.Join(", ", info?.Tags ?? []);
        yours.GetNode<LineEdit>("Alias").Text = info?.Alias ?? "";
        yours.GetNode<TextEdit>("Notes").Text = info?.Notes ?? "";
        Callable.From(() => PlaceFields(container)).CallDeferred();

        var folder = container.GetNodeOrNull<NButton>("OpenFolderButton") ?? AddButton(container, "OpenFolderButton",
            "SPIRE_MOD_MANAGER.OPEN_FOLDER", 20, () => OS.ShellOpen(Meta(container, FolderMeta)));
        folder.Visible = Directory.Exists(mod.path);
    }

    // The panel text in our views, top to bottom: anything wrong (red), the author, the description, then
    // the facts: version, compatibility, the original name under an alias, Workshop dates and tags,
    // dependencies both ways. Our tags aren't repeated here (the player's own are edited below; the
    // automatic ones are on the row).
    static string Describe(Mod mod, string? originalName)
    {
        var text = new System.Text.StringBuilder();
        void Fact(string key, string value) => text.Append($"[gold]{Menus.Text(key)}[/gold]: {value}\n");

        if (Problems(mod) is { Count: > 0 } problems)
            text.Append(string.Join("", problems.Select(p => $"[red]{p}[/red]\n"))).Append('\n');
        Fact("SPIRE_MOD_MANAGER.AUTHOR", mod.manifest?.author ?? Menus.Text("SPIRE_MOD_MANAGER.UNKNOWN"));
        text.Append('\n').Append(mod.manifest?.description ?? Menus.Text("SPIRE_MOD_MANAGER.NO_DESCRIPTION")).Append("\n\n");

        Fact("SPIRE_MOD_MANAGER.VERSION", mod.manifest?.version ?? Menus.Text("SPIRE_MOD_MANAGER.UNKNOWN"));
        Fact("SPIRE_MOD_MANAGER.COMPATIBILITY", Compatibility(mod));
        if (originalName != null)
            Fact("SPIRE_MOD_MANAGER.NAME", originalName);
        if (WorkshopInfo.For(mod) is { } workshop)
        {
            Fact("SPIRE_MOD_MANAGER.UPDATED", $"{workshop.Updated:yyyy-MM-dd} ({Menus.Format("SPIRE_MOD_MANAGER.DAYS_AGO",
                ("days", ((int)(DateTime.Now - workshop.Updated).TotalDays).ToString()))})");
            Fact("SPIRE_MOD_MANAGER.PUBLISHED", $"{workshop.Created:yyyy-MM-dd}");
            if (workshop.Tags.Count > 0)
                Fact("SPIRE_MOD_MANAGER.WORKSHOP_TAGS", string.Join(", ", workshop.Tags));
        }
        string Names(IEnumerable<string> ids) => string.Join(", ", ids.Select(d => Dependencies.Find(d)?.manifest?.name ?? d));
        if (Dependencies.Of(mod) is { Count: > 0 } needs)
            Fact("SPIRE_MOD_MANAGER.DEPENDENCIES", Names(needs));
        var neededBy = ModManager.Mods.Where(m => m != mod && Dependencies.Of(m).Contains(mod.manifest?.id ?? ""))
            .Select(m => m.manifest?.id ?? "").Distinct().ToList();
        if (neededBy.Count > 0)
            Fact("SPIRE_MOD_MANAGER.NEEDED_BY", Names(neededBy));
        return text.ToString().TrimEnd();
    }

    // What stops the mod from working, in plain words: the game's own load errors (wrong game version or
    // branch, bad files...), a duplicate copy, and, for a mod that's on, dependencies missing or off.
    public static List<string> Problems(Mod mod)
    {
        var problems = (mod.errors ?? []).Select(e => e.GetFormattedText()).ToList();
        if (mod.state == ModLoadState.Failed && problems.Count == 0)
            problems.Add(Menus.Text("SPIRE_MOD_MANAGER.STATE_FAILED"));
        if (mod.state == ModLoadState.DisabledDuplicate)
            problems.Add(Menus.Text("SPIRE_MOD_MANAGER.STATE_DUPLICATE"));
        if (Dependencies.IsOn(mod))
            foreach (var id in Dependencies.Of(mod))
            {
                var dependency = Dependencies.Find(id);
                if (dependency == null || !Dependencies.IsOn(dependency))
                    problems.Add(Menus.Format(dependency == null ? "SPIRE_MOD_MANAGER.PROBLEM_DEP_MISSING" : "SPIRE_MOD_MANAGER.PROBLEM_DEP_OFF",
                        ("mod", dependency?.manifest?.name ?? id)));
            }
        return problems;
    }

    // "0.110.1 or newer (this game: 0.111.0, beta)", plus the Workshop's supported branches when it lists any.
    static string Compatibility(Mod mod)
    {
        var text = mod.manifest?.minGameVersion is { Length: > 0 } minimum
            ? Menus.Format("SPIRE_MOD_MANAGER.GAME_VERSION_MIN", ("version", minimum))
            : Menus.Text("SPIRE_MOD_MANAGER.ANY_VERSION");
        var game = MegaCrit.Sts2.Core.Debug.ReleaseInfoManager.Instance.SemVer?.ToString();
        var branch = Branch(PlatformUtil.GetPlatformBranch().ToName());
        var current = string.Join(", ", new[] { game, branch }.Where(s => !string.IsNullOrEmpty(s)));
        if (current.Length > 0)
            text += $" [color=#fff6e299]({Menus.Format("SPIRE_MOD_MANAGER.THIS_GAME", ("version", current))})[/color]";
        if (WorkshopInfo.For(mod)?.Branches is { Count: > 0 } branches)
            text += "; " + Menus.Format("SPIRE_MOD_MANAGER.BRANCHES", ("branches", string.Join(", ", branches.Select(b =>
                b.Min == b.Max ? Branch(b.Min) : $"{Branch(b.Min)}–{Branch(b.Max)}"))));
        return text;
    }

    // The game's branch names in player words: "public" is the stable game, "public-beta" the beta.
    static string Branch(string name) => name switch
    {
        "public" => Menus.Text("SPIRE_MOD_MANAGER.BRANCH_STABLE"),
        "public-beta" => Menus.Text("SPIRE_MOD_MANAGER.BRANCH_BETA"),
        "none" or "" => "",
        _ => name,
    };

    // Fills the panel again for the mod it shows (after a tickbox change: its problems may have changed).
    public static void Refresh(Node screen)
    {
        if (Store.Data.VanillaView || screen.GetNodeOrNull<NModInfoContainer>("%ModInfoContainer") is not { } container
            || CategoryList.Selected is not { } mod || Meta(container, ModIdMeta) != mod.manifest?.id)
            return;
        container.Fill(mod);
    }

    // Vanilla View or no mod shown: none of our additions.
    public static void HideButtons(Node container)
    {
        CommitEdits(container);
        foreach (var name in new[] { "OpenFolderButton", SourceName, FieldsName })
            if (container.GetNodeOrNull<Control>(name) is { } control)
                control.Visible = false;
    }

    static string Meta(Node node, string key) => node.HasMeta(key) ? (string)node.GetMeta(key) : "";

    // Along the inside of the panel's bottom edge, whatever its size.
    public const float ButtonsFromBottom = 72;
    public const float Inset = 22; // left edge of everything in the panel: title, source, text, fields
    const string SourceName = "SpireModManagerSource";

    static NButton AddButton(Control parent, string name, string locKey, float x, Action onPressed)
    {
        var button = ModEntry.AddButton(parent, locKey, new Vector2(180, 52), onPressed);
        button.Name = name;
        button.AnchorTop = button.AnchorBottom = 1;
        (button.OffsetLeft, button.OffsetRight, button.OffsetTop, button.OffsetBottom) =
            (x, x + 180, -ButtonsFromBottom + 6, -ButtonsFromBottom + 58);
        return button;
    }

    static Control AddSource(Control container)
    {
        var line = new HBoxContainer { Name = SourceName };
        line.AddThemeConstantOverride("separation", 6);
        var term = new Label { Text = Menus.Text("SPIRE_MOD_MANAGER.SOURCE") };
        Style(term, "res://themes/kreon_regular_shared.tres", 16, Gold);
        line.AddChild(term);
        var link = new LinkButton { Name = "SpireModManagerLink", FocusMode = Control.FocusModeEnum.None, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        link.AddThemeFontOverride("font", ResourceLoader.Load<Font>("res://themes/kreon_regular_shared.tres"));
        link.AddThemeFontSizeOverride("font_size", 16);
        link.AddThemeColorOverride("font_color", new Color(0.6f, 0.8f, 1f));
        link.AddThemeColorOverride("font_hover_color", new Color(0.8f, 0.9f, 1f));
        link.AddThemeColorOverride("font_disabled_color", Cream);
        line.AddChild(link);
        container.AddChild(line);
        line.Position = new Vector2(Inset, 54);
        return line;
    }

    // The player's own fields: Tags, Alias and Notes, each a gold term with its field below. At rest a field
    // reads like plain text (or a subtle "Click to add tags"); clicking it shows the box, its hint, and Save,
    // Cancel and Clear on the term's line. Enter saves (Ctrl+Enter in Notes), Escape cancels, and clicking
    // away saves too, so typed text is never lost. Switching to another mod saves first.
    const string FieldsName = "SpireModManagerYours";
    public const float YoursHeight = 280, FieldsGap = 18;

    static Control AddYours(Control container)
    {
        var box = new VBoxContainer { Name = FieldsName };
        box.AddThemeConstantOverride("separation", 2);
        container.AddChild(box);
        box.AnchorRight = 1;
        (box.OffsetLeft, box.OffsetRight) = (Inset, -Inset);

        StyleBoxFlat Look(bool active)
        {
            var style = new StyleBoxFlat
            {
                BgColor = active ? new Color(0.0756f, 0.12084f, 0.18f, 0.7f) : Colors.Transparent,
                BorderColor = active ? new Color(Gold, 0.45f) : Colors.Transparent,
                ContentMarginLeft = 6, ContentMarginRight = 6, ContentMarginTop = 2, ContentMarginBottom = 2,
            };
            style.SetBorderWidthAll(1);
            style.SetCornerRadiusAll(4);
            return style;
        }

        // A field: the term line (with its buttons, shown while editing) and the box itself.
        void Field(Control field, string termKey, string restKey, string editKey, Func<string> stored, float gapBefore)
        {
            if (gapBefore > 0)
                box.AddChild(new Control { CustomMinimumSize = new Vector2(0, gapBefore), MouseFilter = Control.MouseFilterEnum.Ignore });
            void Set(string text)
            {
                if (field is LineEdit line)
                    line.Text = text;
                else
                    ((TextEdit)field).Text = text;
            }
            void Hint(string key)
            {
                if (field is LineEdit line)
                    line.PlaceholderText = Menus.Text(key);
                else
                    ((TextEdit)field).PlaceholderText = Menus.Text(key);
            }

            var term = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
            term.AddThemeConstantOverride("separation", 6);
            var label = new Label { Text = Menus.Text(termKey), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            Style(label, "res://themes/kreon_regular_shared.tres", 18, Gold);
            term.AddChild(label);
            var cancelling = false;
            var buttons = Menus.InlineButtons([
                new(Menus.Text("SPIRE_MOD_MANAGER.SAVE"), () => field.ReleaseFocus()), // leaving the field saves
                new(Menus.Text("SPIRE_MOD_MANAGER.CANCEL"), Cancel),
                new(Menus.Text("SPIRE_MOD_MANAGER.CLEAR"), () =>
                {
                    Set("");
                    field.GrabFocus();
                }),
            ], $"{field.Name}Buttons");
            foreach (var (button, id) in buttons.GetChildren().OfType<Button>().Zip(new[] { "Save", "Cancel", "Clear" }))
                button.Name = $"{field.Name}{id}";
            buttons.Visible = false;
            term.AddChild(buttons);
            box.AddChild(term);

            Style(field, "res://themes/kreon_regular_shared.tres", 18, Cream);
            field.AddThemeColorOverride("font_placeholder_color", new Color(Cream, 0.32f));
            field.AddThemeStyleboxOverride("normal", Look(false));
            field.AddThemeStyleboxOverride("focus", Look(true));
            field.MouseDefaultCursorShape = Control.CursorShape.Ibeam;
            Hint(restKey);
            box.AddChild(field);

            void Cancel()
            {
                cancelling = true;
                Set(stored());
                field.ReleaseFocus();
            }
            field.FocusEntered += () =>
            {
                Hint(editKey);
                buttons.Visible = true;
            };
            field.FocusExited += () =>
            {
                Hint(restKey);
                buttons.Visible = false;
                if (!cancelling)
                    CommitField(container, field.Name);
                cancelling = false;
            };
            field.GuiInput += e =>
            {
                switch (e)
                {
                    case InputEventKey { Keycode: Key.Escape, Pressed: true }:
                        Cancel();
                        field.AcceptEvent();
                        break;
                    case InputEventKey { Keycode: Key.Enter or Key.KpEnter, Pressed: true } enter when field is LineEdit || enter.CtrlPressed:
                        field.ReleaseFocus();
                        field.AcceptEvent();
                        break;
                }
            };
        }

        string Stored(Func<ModInfo?, string> read) => read(Store.InfoFor(Meta(container, ModIdMeta)));
        Field(new LineEdit { Name = "Tags", MaxLength = 200, CustomMinimumSize = new Vector2(0, 32) }, "SPIRE_MOD_MANAGER.YOUR_TAGS",
            "SPIRE_MOD_MANAGER.TAGS_REST", "SPIRE_MOD_MANAGER.TAGS_HINT", () => Stored(i => string.Join(", ", i?.Tags ?? [])), 0);
        Field(new LineEdit { Name = "Alias", MaxLength = 80, CustomMinimumSize = new Vector2(0, 32) }, "SPIRE_MOD_MANAGER.ALIAS",
            "SPIRE_MOD_MANAGER.ALIAS_REST", "SPIRE_MOD_MANAGER.ALIAS_HINT", () => Stored(i => i?.Alias ?? ""), 8);
        Field(new TextEdit
        {
            Name = "Notes", WrapMode = TextEdit.LineWrappingMode.Boundary, CustomMinimumSize = new Vector2(0, 4 * 24 + 8),
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        }, "SPIRE_MOD_MANAGER.YOUR_NOTES", "SPIRE_MOD_MANAGER.NOTES_REST", "SPIRE_MOD_MANAGER.NOTES_HINT", () => Stored(i => i?.Notes ?? ""), 16);
        return box;
    }

    // The fields sit right under the mod's details: the description shrinks to its text (up to the room
    // left above the fields), and the fields follow it. Once the text is laid out, so deferred.
    static void PlaceFields(Control container)
    {
        if (container.GetNodeOrNull<Control>(FieldsName) is not { } yours)
            return;
        var description = container.GetNode<RichTextLabel>("ModDescription");
        var top = description.OffsetTop;
        var room = container.Size.Y - ButtonsFromBottom - YoursHeight - FieldsGap - top;
        var height = Math.Min(description.GetContentHeight() + 6, room);
        description.OffsetBottom = top + height;
        yours.OffsetTop = top + height + FieldsGap;
        yours.OffsetBottom = yours.OffsetTop + YoursHeight;
    }

    // After an undo or revert: the fields show the old state, so they must not be saved over the restored one.
    public static void Forget(Node container) => container.SetMeta(ModIdMeta, "");

    // Saves the field being edited (if any) for the mod it belongs to: before the panel shows another mod.
    // Only that field: the others may show text that has since changed elsewhere (the row's pencil).
    static void CommitEdits(Node container)
    {
        if (container.GetNodeOrNull<Control>(FieldsName) is { } yours)
            foreach (var field in new[] { "Tags", "Alias", "Notes" })
                if (yours.GetNode<Control>(field).HasFocus())
                    CommitField(container, field);
    }

    // Saves one field if it changed. A new alias also retitles the panel and the row.
    static void CommitField(Node container, string field)
    {
        if (container.GetNodeOrNull<Control>(FieldsName) is not { } yours || Meta(container, ModIdMeta) is not { Length: > 0 } id)
            return;
        var info = Store.InfoFor(id);
        string? Trimmed(string text) => text.Trim() is { Length: > 0 } t ? t : null;
        switch (field)
        {
            case "Tags" when Tags.Parse(yours.GetNode<LineEdit>("Tags").Text) is var tags && !(tags ?? []).SequenceEqual(info?.Tags ?? []):
                Edit(id, i => i.Tags = tags);
                yours.GetNode<LineEdit>("Tags").Text = string.Join(", ", tags ?? []); // as saved: repeats and blanks dropped
                break;
            case "Alias" when Trimmed(yours.GetNode<LineEdit>("Alias").Text) is var alias && alias != info?.Alias:
                Edit(id, i => i.Alias = alias);
                if (container is NModInfoContainer panel && CategoryList.Selected is { } mod && mod.manifest?.id == id)
                    Callable.From(() => panel.Fill(mod)).CallDeferred(); // the title and "Name:" line follow
                break;
            case "Notes" when Trimmed(yours.GetNode<TextEdit>("Notes").Text) is var notes && notes != info?.Notes:
                Edit(id, i => i.Notes = notes);
                break;
            default:
                return;
        }
        RefreshRows(container);
    }

    public static void Save(string modId, string alias, string notes, string tags = "") => Edit(modId, info =>
    {
        info.Alias = alias.Trim() is { Length: > 0 } a ? a : null;
        info.Notes = notes.Trim() is { Length: > 0 } n ? n : null;
        info.Tags = Tags.Parse(tags);
    });

    public static void SetAlias(string modId, string alias) =>
        Edit(modId, info => info.Alias = alias.Trim() is { Length: > 0 } a ? a : null);

    static void Edit(string modId, Action<ModInfo> change)
    {
        if (!Store.Data.Mods.TryGetValue(modId, out var info))
            Store.Data.Mods[modId] = info = new ModInfo();
        change(info);
        Store.Save();
    }

    // Row titles (alias, original name, pencil) after an edit made in the panel.
    static void RefreshRows(Node container)
    {
        if (container.GetParent()?.GetNodeOrNull<Control>("%ModsScrollContainer/Mask/Content") is { } content)
            CategoryList.Apply(content);
    }

    // Row titles: the alias with the original name beside it and a pencil to edit it, or just the name in
    // Vanilla View.
    public static void ApplyRowTitles(IEnumerable<NModMenuRow> rows, bool vanilla)
    {
        foreach (var row in rows)
        {
            var name = row.Mod?.manifest?.name ?? row.Mod?.manifest?.id ?? "";
            var title = row.GetNode<MegaRichTextLabel>("Title");
            var alias = vanilla ? null : Store.InfoFor(row.Mod?.manifest?.id)?.Alias;
            // Our views: smaller text in a shorter row with a smaller tickbox and no Steam/folder icon;
            // the title stretches with the (wider) row.
            row.CustomMinimumSize = new Vector2(0, vanilla ? 72 : 52);
            row.GetNode<Control>("PlatformIcon").Visible = vanilla;
            var tickbox = row.GetNode<Control>("Tickbox");
            tickbox.PivotOffset = tickbox.Size / 2;
            tickbox.Scale = vanilla ? Vector2.One : new Vector2(0.72f, 0.72f);
            title.AnchorRight = vanilla ? 0 : 1;
            title.OffsetRight = vanilla ? 562 : -90;
            title.BbcodeEnabled = true;
            title.Text = Escape(name);
            Layout.FontSize(title, vanilla ? Layout.RowTitleVanillaSize : Layout.RowTitleSize);
            Pencil(row, title, !vanilla && !Store.Data.LoadOrderView); // Load Order is for ordering only
            if (!vanilla)
            {
                // Fitted once this redraw has placed the title (Load Order moves it right).
                var shown = row;
                Callable.From(() => FitTitle(shown, name, alias)).CallDeferred();
            }
        }
        MarkChanged(rows, vanilla);
    }

    // The alias with the original name small and dim beside it, or just the name, cut short with "…" when
    // it doesn't fit (the original name goes first); hovering the row then shows it in full.
    const int OriginalNameSize = 16;

    static void FitTitle(NModMenuRow row, string name, string? alias)
    {
        if (!GodotObject.IsInstanceValid(row))
            return;
        var title = row.GetNode<MegaRichTextLabel>("Title");
        var font = title.GetThemeFont("normal_font");
        float Width(string text, int size) => font.GetStringSize(text, fontSize: size).X;
        var room = title.Size.X - 4;
        var main = Fit(alias ?? name, Layout.RowTitleSize, room);
        var text = Escape(main);
        var cut = main != (alias ?? name);
        if (alias != null)
        {
            var left = room - Width(main + "  ", Layout.RowTitleSize);
            var original = left > Width("……", OriginalNameSize) ? Fit(name, OriginalNameSize, left) : "";
            cut |= original != name;
            if (original.Length > 0)
                text += $"  [color=#fff6e280][font_size={OriginalNameSize}]{Escape(original)}[/font_size][/color]";
        }
        title.SetTextAutoSize(text);
        row.TooltipText = cut ? (alias != null ? $"{alias}\n{name}" : name) : "";

        string Fit(string full, int size, float width)
        {
            if (Width(full, size) <= width)
                return full;
            var length = full.Length;
            while (length > 0 && Width(full[..length].TrimEnd() + "…", size) > width)
                length--;
            return length > 0 ? full[..length].TrimEnd() + "…" : "";
        }
    }

    // Our views: a mod turned on since launch has a bright gold title, one turned off a dim one, so what a
    // relaunch will change stands out. Otherwise the colour the game gave it (white, grey when off, red when it failed).
    public static readonly Color ChangedTitle = new(1f, 0.82f, 0.45f); // turned on: bright gold
    public static readonly Color ChangedOffTitle = new(0.72f, 0.6f, 0.42f, 0.8f); // turned off: a dim, dusky gold
    const string TitleColorMeta = "spire_mod_manager_title_color";

    public static void MarkChanged(IEnumerable<NModMenuRow> rows, bool vanilla)
    {
        var loaded = History.Loaded?.Mods.DistinctBy(m => (m.Id, m.Source)).ToDictionary(m => (m.Id, m.Source), m => m.Enabled);
        foreach (var row in rows)
        {
            var title = row.GetNode<Control>("Title");
            if (!title.HasMeta(TitleColorMeta))
                title.SetMeta(TitleColorMeta, title.Modulate);
            var changed = !vanilla && loaded != null && row.Mod is { manifest.id: { } id } mod
                && loaded.GetValueOrDefault((id, mod.modSource), true) != Dependencies.IsOn(mod);
            title.Modulate = !changed ? (Color)title.GetMeta(TitleColorMeta)
                : row.Mod is { } turned && Dependencies.IsOn(turned) ? ChangedTitle : ChangedOffTitle;
        }
    }

    // The small pencil left of a row's title: edit the alias in place. The title moves right to make room
    // (once; other code shifts it too, so only ever relative changes).
    static void Pencil(NModMenuRow row, Control title, bool show)
    {
        var pencil = row.GetNodeOrNull<Button>(PencilName);
        if (!show)
        {
            if (pencil != null)
            {
                row.RemoveChild(pencil);
                pencil.QueueFree();
                title.OffsetLeft -= PencilWidth;
            }
            return;
        }
        if (pencil == null)
        {
            pencil = new Button
            {
                Name = PencilName, Text = "✎", Flat = true, FocusMode = Control.FocusModeEnum.None,
                MouseDefaultCursorShape = Control.CursorShape.PointingHand, TooltipText = Menus.Text("SPIRE_MOD_MANAGER.EDIT_ALIAS"),
            };
            pencil.AddThemeFontSizeOverride("font_size", 15);
            pencil.AddThemeColorOverride("font_color", new Color(NotesColor) with { A = 0.6f });
            pencil.AddThemeColorOverride("font_hover_color", new Color(NotesColor));
            row.AddChild(pencil);
            title.OffsetLeft += PencilWidth;
            // A second click while editing closes the box (and keeps what was typed).
            pencil.Pressed += () =>
            {
                if (row.GetNodeOrNull<Control>("AliasBox") is { } open)
                    open.ReleaseFocus();
                else
                    BeginAliasEdit(row);
            };
            // Shown while the mouse is over the row (or the box is open); still there to click when faded out.
            pencil.Modulate = Colors.Transparent;
            if (!row.HasMeta(PencilHoverMeta))
            {
                row.SetMeta(PencilHoverMeta, true);
                row.MouseEntered += () => ShowPencil(row, true);
                row.MouseExited += () => ShowPencil(row, row.HasNode("AliasBox"));
            }
        }
        PlacePencil(row);
    }

    static void ShowPencil(NModMenuRow row, bool show)
    {
        if (row.GetNodeOrNull<Control>(PencilName) is { } pencil)
            pencil.Modulate = show ? Colors.White : Colors.Transparent;
    }

    // Just left of the title, wherever the title currently starts (Load Order moves it for its grip).
    public static void PlacePencil(NModMenuRow row)
    {
        if (row.GetNodeOrNull<Button>(PencilName) is not { } pencil)
            return;
        pencil.Position = new Vector2(row.GetNode<Control>("Title").OffsetLeft - PencilWidth, 2);
        pencil.Size = new Vector2(PencilWidth - 6, 28); // a small gap before the title
    }

    // A text box over the row's title: Enter or clicking away saves the alias (empty removes it), Escape cancels.
    public static LineEdit? BeginAliasEdit(NModMenuRow row)
    {
        if (row.Mod?.manifest?.id is not { } id || row.HasNode("AliasBox"))
            return null;
        var title = row.GetNode<Control>("Title");
        var box = new LineEdit
        {
            Name = "AliasBox", Text = Store.InfoFor(id)?.Alias ?? "", PlaceholderText = row.Mod?.manifest?.name ?? id,
            SelectAllOnFocus = true, MaxLength = 80, ClearButtonEnabled = true,
        };
        Style(box, "res://themes/kreon_regular_shared.tres", 18, new Color(NotesColor));
        var field = new StyleBoxFlat { BgColor = new Color(0.05f, 0.08f, 0.12f), BorderColor = new Color(NotesColor) with { A = 0.5f }, ContentMarginLeft = 6, ContentMarginRight = 4 };
        field.SetBorderWidthAll(1);
        field.SetCornerRadiusAll(4);
        box.AddThemeStyleboxOverride("normal", field);
        box.AddThemeStyleboxOverride("focus", field);
        row.AddChild(box);
        title.Visible = false; // not showing through behind the box
        ShowPencil(row, true);
        box.Position = new Vector2(title.OffsetLeft, 2);
        box.Size = new Vector2(Math.Max(240, row.Size.X - title.OffsetLeft - 160), 30);
        box.GrabFocus();

        var done = false;
        void Finish(bool save)
        {
            if (done)
                return;
            done = true;
            if (save)
                SetAlias(id, box.Text);
            row.RemoveChild(box);
            box.QueueFree();
            title.Visible = true;
            if (CategoryList.ScreenOf(row) is { } screen)
                Callable.From(() =>
                {
                    CategoryList.Apply(screen.GetNode<Control>("%ModsScrollContainer/Mask/Content"));
                    if (row.Mod is { } mod && Meta(screen.GetNode("%ModInfoContainer"), ModIdMeta) == id)
                        screen.GetNode<NModInfoContainer>("%ModInfoContainer").Fill(mod);
                }).CallDeferred();
        }
        box.TextSubmitted += _ => Finish(true);
        box.FocusExited += () => Finish(true);
        box.GuiInput += e =>
        {
            if (e is InputEventKey { Keycode: Key.Escape, Pressed: true })
                Finish(false);
        };
        return box;
    }

    static string Escape(string text) => text.Replace("[", "[lb]");

    static void Style(Control control, string font, int size, Color color)
    {
        control.AddThemeFontOverride("font", ResourceLoader.Load<Font>(font));
        control.AddThemeFontSizeOverride("font_size", size);
        control.AddThemeColorOverride("font_color", color);
    }
}
