using Godot;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;
using MegaCrit.Sts2.addons.mega_text;

namespace SpireModManager;

// The info panel in our views, top to bottom: the title (the player's alias if any), the Workshop page
// link or "Local mod", a thumbnail of the mod's image beside them, the author's description and facts,
// then the player's own tags and notes, edited right there (saved on Enter or clicking away), and Open
// Folder. Player-written text is light blue, never the author's cream/gold.
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
    const float PencilWidth = 22;

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

        var yours = container.GetNodeOrNull<Control>("SpireModManagerYours") ?? AddYours(container);
        yours.Visible = id != null;
        yours.GetNode<LineEdit>("Tags").Text = string.Join(", ", info?.Tags ?? []);
        yours.GetNode<TextEdit>("Notes").Text = info?.Notes ?? "";

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

    // Vanilla View or no mod shown: none of our additions.
    public static void HideButtons(Node container)
    {
        CommitEdits(container);
        foreach (var name in new[] { "OpenFolderButton", SourceName, "SpireModManagerYours" })
            if (container.GetNodeOrNull<Control>(name) is { } control)
                control.Visible = false;
    }

    static string Meta(Node node, string key) => node.HasMeta(key) ? (string)node.GetMeta(key) : "";

    // Along the inside of the panel's bottom edge, whatever its size.
    public const float ButtonsFromBottom = 72, YoursHeight = 214;
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

    // "Tags" and "Notes", gold like the other terms, each with a pencil; below each, the player's text.
    // At rest the fields look like plain text (the panel's cream); clicking one (or its pencil) shows the
    // box and its hint. Saved on Enter (tags) or when focus leaves; switching to another mod saves first.
    static Control AddYours(Control container)
    {
        var box = new VBoxContainer { Name = "SpireModManagerYours" };
        box.AddThemeConstantOverride("separation", 2);
        container.AddChild(box);
        box.AnchorTop = box.AnchorBottom = box.AnchorRight = 1;
        (box.OffsetLeft, box.OffsetRight, box.OffsetTop, box.OffsetBottom) = (Inset, -Inset, -ButtonsFromBottom - YoursHeight, -ButtonsFromBottom);

        StyleBoxFlat Field(bool active)
        {
            var style = new StyleBoxFlat
            {
                BgColor = active ? new Color(0.0756f, 0.12084f, 0.18f, 0.7f) : Colors.Transparent,
                BorderColor = active ? new Color(NotesColor) with { A = 0.35f } : Colors.Transparent,
                ContentMarginLeft = 6, ContentMarginRight = 6, ContentMarginTop = 2, ContentMarginBottom = 2,
            };
            style.SetBorderWidthAll(1);
            style.SetCornerRadiusAll(4);
            return style;
        }
        void Editable(Control field, string hintKey, Action<string> setHint)
        {
            Style(field, "res://themes/kreon_regular_shared.tres", 18, Cream);
            field.AddThemeColorOverride("font_placeholder_color", new Color(Cream, 0.4f));
            field.AddThemeStyleboxOverride("normal", Field(false));
            field.AddThemeStyleboxOverride("focus", Field(true));
            field.FocusEntered += () => setHint(Menus.Text(hintKey));
            field.FocusExited += () => setHint("");
            field.MouseDefaultCursorShape = Control.CursorShape.Ibeam;
        }
        void Caption(string key, Control field)
        {
            var line = new HBoxContainer();
            line.AddThemeConstantOverride("separation", 6);
            var label = new Label { Text = Menus.Text(key) };
            Style(label, "res://themes/kreon_regular_shared.tres", 18, Gold);
            line.AddChild(label);
            var pencil = new Button
            {
                Text = "✎", Flat = true, FocusMode = Control.FocusModeEnum.None, MouseDefaultCursorShape = Control.CursorShape.PointingHand,
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            };
            pencil.AddThemeFontSizeOverride("font_size", 15);
            pencil.AddThemeColorOverride("font_color", new Color(Gold, 0.6f));
            pencil.AddThemeColorOverride("font_hover_color", Gold);
            pencil.Pressed += () => field.GrabFocus();
            line.AddChild(pencil);
            box.AddChild(line);
            box.AddChild(field);
        }

        var tags = new LineEdit { Name = "Tags", MaxLength = 200, CustomMinimumSize = new Vector2(0, 32) };
        Editable(tags, "SPIRE_MOD_MANAGER.TAGS_HINT", hint => tags.PlaceholderText = hint);
        Caption("SPIRE_MOD_MANAGER.YOUR_TAGS", tags);

        var notes = new TextEdit
        {
            Name = "Notes", WrapMode = TextEdit.LineWrappingMode.Boundary, CustomMinimumSize = new Vector2(0, 4 * 24 + 8),
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        Editable(notes, "SPIRE_MOD_MANAGER.NOTES_HINT", hint => notes.PlaceholderText = hint);
        Caption("SPIRE_MOD_MANAGER.YOUR_NOTES", notes);

        tags.TextSubmitted += _ =>
        {
            CommitEdits(container);
            tags.ReleaseFocus();
        };
        tags.FocusExited += () => CommitEdits(container);
        notes.FocusExited += () => CommitEdits(container);
        return box;
    }

    // After an undo or revert: the fields show the old state, so they must not be saved over the restored one.
    public static void Forget(Node container) => container.SetMeta(ModIdMeta, "");

    // Saves the panel's tag and note fields for the mod they belong to, if they changed.
    static void CommitEdits(Node container)
    {
        if (container.GetNodeOrNull<Control>("SpireModManagerYours") is not { } yours || Meta(container, ModIdMeta) is not { Length: > 0 } id)
            return;
        var info = Store.InfoFor(id);
        var tags = Tags.Parse(yours.GetNode<LineEdit>("Tags").Text);
        var notes = yours.GetNode<TextEdit>("Notes").Text.Trim() is { Length: > 0 } n ? n : null;
        if (notes == info?.Notes && (tags ?? []).SequenceEqual(info?.Tags ?? []))
            return;
        Edit(id, i =>
        {
            i.Tags = tags;
            i.Notes = notes;
        });
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
            title.BbcodeEnabled = alias != null;
            title.Text = alias == null ? name : $"{Escape(alias)}  [color=#fff6e280][font_size=16]{Escape(name)}[/font_size][/color]";
            Layout.FontSize(title, vanilla ? Layout.RowTitleVanillaSize : Layout.RowTitleSize);
            Pencil(row, title, !vanilla && !Store.Data.LoadOrderView); // Load Order is for ordering only
        }
        MarkChanged(rows, vanilla);
    }

    // Our views: a mod turned on or off since launch has a soft gold title, so what a relaunch will change
    // stands out. Otherwise the colour the game gave it (white, grey when off, red when it failed).
    public static readonly Color ChangedTitle = new(1f, 0.82f, 0.45f);
    const string TitleColorMeta = "spire_mod_manager_title_color";

    public static void MarkChanged(IEnumerable<NModMenuRow> rows, bool vanilla)
    {
        var loaded = History.Loaded?.Mods.ToDictionary(m => (m.Id, m.Source), m => m.Enabled);
        foreach (var row in rows)
        {
            var title = row.GetNode<Control>("Title");
            if (!title.HasMeta(TitleColorMeta))
                title.SetMeta(TitleColorMeta, title.Modulate);
            var changed = !vanilla && loaded != null && row.Mod is { manifest.id: { } id } mod
                && loaded.GetValueOrDefault((id, mod.modSource), true) != Dependencies.IsOn(mod);
            title.Modulate = changed ? ChangedTitle : (Color)title.GetMeta(TitleColorMeta);
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
            pencil.Pressed += () => BeginAliasEdit(row);
        }
        PlacePencil(row);
    }

    // Just left of the title, wherever the title currently starts (Load Order moves it for its grip).
    public static void PlacePencil(NModMenuRow row)
    {
        if (row.GetNodeOrNull<Button>(PencilName) is not { } pencil)
            return;
        pencil.Position = new Vector2(row.GetNode<Control>("Title").OffsetLeft - PencilWidth, 2);
        pencil.Size = new Vector2(PencilWidth, 28);
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
            SelectAllOnFocus = true, MaxLength = 80,
        };
        Style(box, "res://themes/kreon_regular_shared.tres", 18, new Color(NotesColor));
        row.AddChild(box);
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
