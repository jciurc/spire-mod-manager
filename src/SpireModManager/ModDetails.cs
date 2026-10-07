using Godot;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
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
        {
            title.Text = alias;
            description.Text = $"[gold]{Menus.Text("SPIRE_MOD_MANAGER.NAME")}[/gold]: {name}\n{description.Text}";
        }
        var tags = string.Join(", ", Tags.For(mod).Select(c => c.Text));
        if (tags.Length > 0)
            description.Text = $"[gold]{Menus.Text("SPIRE_MOD_MANAGER.TAGS")}[/gold]: {tags}\n{description.Text}";
        description.Text += Facts(mod);

        container.SetMeta(ModIdMeta, id ?? "");
        container.SetMeta(FolderMeta, mod.path);

        // Under the title: the Workshop page as a link, or "Local mod".
        var link = container.GetNodeOrNull<LinkButton>("SpireModManagerLink") ?? AddLink(container);
        link.Visible = true;
        if (mod.workshopId is { } workshopId)
        {
            link.Text = $"steamcommunity.com/sharedfiles/filedetails/?id={workshopId}";
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

    // Game version it was made for, dates, anything unusual about how it loaded, dependencies both ways.
    static string Facts(Mod mod)
    {
        string Line(string key, string value) => $"\n[gold]{Menus.Text(key)}[/gold]: {value}";
        var facts = "";
        if (mod.manifest?.minGameVersion is { Length: > 0 } minimum)
            facts += Line("SPIRE_MOD_MANAGER.GAME_VERSION", Menus.Format("SPIRE_MOD_MANAGER.GAME_VERSION_MIN", ("version", minimum)));
        if (WorkshopInfo.For(mod) is { } workshop)
        {
            facts += Line("SPIRE_MOD_MANAGER.UPDATED", $"{workshop.Updated:yyyy-MM-dd} ({Menus.Format("SPIRE_MOD_MANAGER.DAYS_AGO",
                ("days", ((int)(DateTime.Now - workshop.Updated).TotalDays).ToString()))})");
            facts += Line("SPIRE_MOD_MANAGER.PUBLISHED", $"{workshop.Created:yyyy-MM-dd}");
            if (workshop.Tags.Count > 0)
                facts += Line("SPIRE_MOD_MANAGER.WORKSHOP_TAGS", string.Join(", ", workshop.Tags));
        }
        if (mod.state is ModLoadState.Failed or ModLoadState.DisabledDuplicate)
            facts += Line("SPIRE_MOD_MANAGER.STATE", Menus.Text(mod.state == ModLoadState.Failed
                ? "SPIRE_MOD_MANAGER.STATE_FAILED" : "SPIRE_MOD_MANAGER.STATE_DUPLICATE"));
        string Names(IEnumerable<string> ids) => string.Join(", ", ids.Select(d => Dependencies.Find(d)?.manifest?.name ?? d));
        if (Dependencies.Of(mod) is { Count: > 0 } needs)
            facts += Line("SPIRE_MOD_MANAGER.NEEDS_LABEL", Names(needs));
        var neededBy = ModManager.Mods.Where(m => m != mod && Dependencies.Of(m).Contains(mod.manifest?.id ?? ""))
            .Select(m => m.manifest?.id ?? "").Distinct().ToList();
        if (neededBy.Count > 0)
            facts += Line("SPIRE_MOD_MANAGER.NEEDED_BY", Names(neededBy));
        return facts.Length > 0 ? "\n" + facts + "\n" : "";
    }

    // Vanilla View or no mod shown: none of our additions.
    public static void HideButtons(Node container)
    {
        CommitEdits(container);
        foreach (var name in new[] { "OpenFolderButton", "SpireModManagerLink", "SpireModManagerYours" })
            if (container.GetNodeOrNull<Control>(name) is { } control)
                control.Visible = false;
    }

    static string Meta(Node node, string key) => node.HasMeta(key) ? (string)node.GetMeta(key) : "";

    // Along the inside of the panel's bottom edge, whatever its size.
    public const float ButtonsFromBottom = 72, YoursHeight = 190;

    static NButton AddButton(Control parent, string name, string locKey, float x, Action onPressed)
    {
        var button = ModEntry.AddButton(parent, locKey, new Vector2(180, 52), onPressed);
        button.Name = name;
        button.AnchorTop = button.AnchorBottom = 1;
        (button.OffsetLeft, button.OffsetRight, button.OffsetTop, button.OffsetBottom) =
            (x, x + 180, -ButtonsFromBottom + 6, -ButtonsFromBottom + 58);
        return button;
    }

    static LinkButton AddLink(Control container)
    {
        var link = new LinkButton { Name = "SpireModManagerLink", FocusMode = Control.FocusModeEnum.None };
        link.AddThemeFontOverride("font", ResourceLoader.Load<Font>("res://themes/kreon_regular_shared.tres"));
        link.AddThemeFontSizeOverride("font_size", 15);
        link.AddThemeColorOverride("font_color", new Color(0.6f, 0.8f, 1f));
        link.AddThemeColorOverride("font_hover_color", new Color(0.8f, 0.9f, 1f));
        link.AddThemeColorOverride("font_disabled_color", new Color(Cream, 0.55f));
        container.AddChild(link);
        link.Position = new Vector2(24, 56);
        return link;
    }

    // "Your tags" and "Your notes", edited in place above Open Folder. Saved on Enter (tags) or when focus
    // leaves; switching to another mod saves first.
    static Control AddYours(Control container)
    {
        var box = new VBoxContainer { Name = "SpireModManagerYours" };
        box.AddThemeConstantOverride("separation", 4);
        container.AddChild(box);
        box.AnchorTop = box.AnchorBottom = box.AnchorRight = 1;
        (box.OffsetLeft, box.OffsetRight, box.OffsetTop, box.OffsetBottom) = (22, -22, -ButtonsFromBottom - YoursHeight, -ButtonsFromBottom);

        Label Caption(string key)
        {
            var label = new Label { Text = Menus.Text(key) };
            Style(label, "res://themes/kreon_bold_shared.tres", 16, new Color(NotesColor));
            box.AddChild(label);
            return label;
        }
        StyleBoxFlat Field()
        {
            var style = new StyleBoxFlat { BgColor = new Color(0.0756f, 0.12084f, 0.18f, 0.7f), BorderColor = new Color(NotesColor) with { A = 0.35f },
                ContentMarginLeft = 8, ContentMarginRight = 8, ContentMarginTop = 4, ContentMarginBottom = 4 };
            style.SetBorderWidthAll(1);
            style.SetCornerRadiusAll(4);
            return style;
        }

        Caption("SPIRE_MOD_MANAGER.YOUR_TAGS");
        var tags = new LineEdit { Name = "Tags", PlaceholderText = Menus.Text("SPIRE_MOD_MANAGER.TAGS_HINT"), MaxLength = 200 };
        Style(tags, "res://themes/kreon_regular_shared.tres", 16, new Color(NotesColor));
        tags.AddThemeStyleboxOverride("normal", Field());
        tags.AddThemeStyleboxOverride("focus", Field());
        box.AddChild(tags);

        Caption("SPIRE_MOD_MANAGER.YOUR_NOTES");
        var notes = new TextEdit
        {
            Name = "Notes", WrapMode = TextEdit.LineWrappingMode.Boundary, PlaceholderText = Menus.Text("SPIRE_MOD_MANAGER.NOTES_HINT"),
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        Style(notes, "res://themes/kreon_regular_shared.tres", 16, new Color(NotesColor));
        notes.AddThemeStyleboxOverride("normal", Field());
        notes.AddThemeStyleboxOverride("focus", Field());
        box.AddChild(notes);

        tags.TextSubmitted += _ => CommitEdits(container);
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
