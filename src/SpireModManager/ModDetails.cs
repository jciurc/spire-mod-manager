using Godot;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;
using MegaCrit.Sts2.addons.mega_text;

namespace SpireModManager;

// The info panel in our views, top to bottom:
// - the mod's own name (click: scroll to it in the list), a thumbnail beside it;
// - "<id> v<version> (Workshop <id>)", linking to the Workshop page (or "(Local mod)");
// - Enabled / Disabled / Pending restart, then anything wrong (red);
// - the author's description;
// - author, dates, size, subscribers, compatibility, Workshop tags;
// - dependencies both ways (click a name: scroll to it in the list and flash it);
// - the player's own Alias, Category, Tags and Notes (ModField), then Open Folder.
// Vanilla View: the panel exactly as the game makes it.
static class ModDetails
{
    static readonly Color Cream = new(1, 0.964706f, 0.886275f);
    static readonly Color Gold = new(0.937f, 0.784f, 0.318f);
    const string ModIdMeta = "spire_mod_manager_mod_id";
    const string FolderMeta = "spire_mod_manager_folder";
    const string FieldsName = "SpireModManagerYours";
    const string LinkName = "SpireModManagerLink";
    const string WiredMeta = "spire_mod_manager_wired";
    const string Enabled = "#a6e3a1", Disabled = "#fff6e299", Pending = "#f5b56b", Problem = "#ff8070";

    // Along the inside of the panel's bottom edge, whatever its size.
    public const float ButtonsFromBottom = 72;
    public const float Inset = 22; // left edge of everything in the panel: title, link, text, fields
    public const float FieldsGap = 18;

    // The panel's own fields, one set per panel.
    static readonly Dictionary<ulong, List<ModField>> Fields = [];

    // After the game fills the panel for a mod.
    public static void Fill(NModInfoContainer container, Mod mod)
    {
        if (Store.Data.VanillaView)
        {
            HideButtons(container);
            return;
        }
        HistoryBar.CloseList(container); // a mod picked: the panel is about it again
        CommitEdits(container); // whatever was typed for the previous mod
        Wire(container);
        var id = mod.manifest?.id;
        container.SetMeta(ModIdMeta, id ?? "");
        container.SetMeta(FolderMeta, mod.path);

        // The game titles it with the name; ours too (the alias is in the fields below).
        container.GetNode<MegaRichTextLabel>("ModTitle").Text = mod.manifest?.name ?? id ?? "";
        container.GetNode<MegaRichTextLabel>("ModDescription").Text = Describe(mod);

        var link = container.GetNodeOrNull<LinkButton>(LinkName) ?? AddLink(container);
        link.Visible = true;
        var version = mod.manifest?.version is { Length: > 0 } v ? $" v{v}" : "";
        if (mod.workshopId is { } workshopId)
        {
            link.Text = $"{id}{version} ({Menus.Format("SPIRE_MOD_MANAGER.WORKSHOP_ID", ("id", workshopId.ToString()))})";
            link.Uri = $"https://steamcommunity.com/sharedfiles/filedetails/?id={workshopId}";
            (link.Disabled, link.Underline) = (false, LinkButton.UnderlineMode.OnHover);
        }
        else
        {
            link.Text = $"{id}{version} ({Menus.Text("SPIRE_MOD_MANAGER.LOCAL_MOD")})";
            link.Uri = "";
            (link.Disabled, link.Underline) = (true, LinkButton.UnderlineMode.Never);
        }

        var yours = container.GetNodeOrNull<Control>(FieldsName) ?? AddFields(container);
        yours.Visible = id != null;
        foreach (var field in Fields.GetValueOrDefault(container.GetInstanceId(), []))
            field.Load();
        if (yours.GetNodeOrNull<Button>("Category/Picker") is { } picker)
            picker.Text = CategoryList.CategoryName(id) + "  ▾";
        Callable.From(() => PlaceFields(container)).CallDeferred();

        var folder = container.GetNodeOrNull<NButton>("OpenFolderButton") ?? AddButton(container, "OpenFolderButton",
            "SPIRE_MOD_MANAGER.OPEN_FOLDER", 20, () => OS.ShellOpen(Meta(container, FolderMeta)));
        folder.Visible = Directory.Exists(mod.path);
    }

    // Clicking the title scrolls to the mod in the list; clicking a dependency's name scrolls to that one and
    // flashes it (the panel stays on this mod). Connected once per panel.
    static void Wire(NModInfoContainer container)
    {
        if (container.HasMeta(WiredMeta))
            return;
        container.SetMeta(WiredMeta, true);
        Control? Content() => container.GetParent()?.GetNodeOrNull<Control>("%ModsScrollContainer/Mask/Content");
        var title = container.GetNode<Control>("ModTitle");
        title.MouseFilter = Control.MouseFilterEnum.Stop;
        title.MouseDefaultCursorShape = Control.CursorShape.PointingHand;
        title.GuiInput += e =>
        {
            if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false } && Content() is { } content)
                CategoryList.Reveal(content, Meta(container, ModIdMeta), flash: false);
        };
        var description = container.GetNode<RichTextLabel>("ModDescription");
        description.MetaUnderlined = false;
        description.MetaClicked += meta =>
        {
            if (meta.AsString() is { } target && target.StartsWith("mod:") && Content() is { } content)
                CategoryList.Reveal(content, target[4..], flash: true);
        };
    }

    // Status, problems, the author's description, facts, dependencies; blank lines between the groups.
    static string Describe(Mod mod)
    {
        var text = new System.Text.StringBuilder();
        void Fact(string key, string value) => text.Append($"[gold]{Menus.Text(key)}[/gold]: {value}\n");

        text.Append(Status(mod)).Append('\n');
        foreach (var problem in Problems(mod))
            text.Append($"[color={Problem}]{Escape(problem)}[/color]\n");
        text.Append('\n').Append(mod.manifest?.description ?? Menus.Text("SPIRE_MOD_MANAGER.NO_DESCRIPTION")).Append("\n\n");

        Fact("SPIRE_MOD_MANAGER.AUTHOR", Escape(mod.manifest?.author ?? Menus.Text("SPIRE_MOD_MANAGER.UNKNOWN")));
        var workshop = WorkshopInfo.For(mod);
        if (workshop != null)
        {
            Fact("SPIRE_MOD_MANAGER.PUBLISHED", $"{workshop.Created:yyyy-MM-dd}");
            Fact("SPIRE_MOD_MANAGER.UPDATED", $"{workshop.Updated:yyyy-MM-dd} ({Menus.Format("SPIRE_MOD_MANAGER.DAYS_AGO",
                ("days", ((int)(DateTime.Now - workshop.Updated).TotalDays).ToString()))})");
        }
        var bytes = workshop?.Size is > 0 and var size ? size : FolderSize(mod.path);
        if (bytes > 0)
            Fact("SPIRE_MOD_MANAGER.SIZE", Bytes(bytes));
        if (workshop?.Subscribers is { } subscribers)
            Fact("SPIRE_MOD_MANAGER.SUBSCRIBERS", subscribers.ToString("N0"));
        Fact("SPIRE_MOD_MANAGER.COMPATIBILITY", Compatibility(mod));
        if (workshop?.Tags.Count > 0)
            Fact("SPIRE_MOD_MANAGER.WORKSHOP_TAGS", Escape(string.Join(", ", workshop.Tags)));

        string Links(IEnumerable<string> ids) => string.Join(", ", ids.Select(d =>
            $"[url=mod:{d}][color=#cfe3ff]{Escape(Dependencies.Find(d)?.manifest?.name ?? d)}[/color][/url]"));
        var needs = Dependencies.Of(mod);
        var neededBy = ModManager.Mods.Where(m => m != mod && Dependencies.Of(m).Contains(mod.manifest?.id ?? ""))
            .Select(m => m.manifest?.id ?? "").Distinct().ToList();
        if (needs.Count > 0 || neededBy.Count > 0)
            text.Append('\n');
        if (needs.Count > 0)
            Fact("SPIRE_MOD_MANAGER.DEPENDENCIES", Links(needs));
        if (neededBy.Count > 0)
            Fact("SPIRE_MOD_MANAGER.NEEDED_BY", Links(neededBy));
        return text.ToString().TrimEnd();
    }

    // The card shown after hovering a row: its names, state, anything wrong, author, last update, and the
    // player's alias, tags and notes.
    public static string Card(Mod mod)
    {
        var id = mod.manifest?.id ?? "";
        var info = Store.InfoFor(id);
        var lines = new List<string> { $"[b]{Escape(mod.manifest?.name ?? id)}[/b]  [color=#fff6e280]({Escape(id)})[/color]", Status(mod) };
        lines.AddRange(Problems(mod).Select(p => $"[color={Problem}]{Escape(p)}[/color]"));
        string Fact(string key, string value) => $"[color=#e8cf7a]{Menus.Text(key)}[/color]: {value}";
        lines.Add(Fact("SPIRE_MOD_MANAGER.AUTHOR", Escape(mod.manifest?.author ?? Menus.Text("SPIRE_MOD_MANAGER.UNKNOWN"))));
        if (WorkshopInfo.For(mod) is { } workshop)
            lines.Add(Fact("SPIRE_MOD_MANAGER.UPDATED", Date(workshop.Updated)));
        if (info?.Alias is { } alias)
            lines.Add(Fact("SPIRE_MOD_MANAGER.ALIAS_TERM", Escape(alias)));
        if (info?.Tags is { Count: > 0 } tags)
            lines.Add(Fact("SPIRE_MOD_MANAGER.TAGS_TERM", Escape(string.Join(", ", tags))));
        if (info?.Notes is { } notes)
            lines.Add($"[color=#e8cf7a]{Menus.Text("SPIRE_MOD_MANAGER.NOTES_TERM")}[/color]\n{Escape(notes)}");
        return string.Join("\n", lines);
    }

    // "October 12, 2026".
    public static string Date(DateTime date) => date.ToString("MMMM d, yyyy", System.Globalization.CultureInfo.InvariantCulture);

    // Enabled (green), Disabled (grey), or Pending restart (orange) when it's been turned on or off since it
    // loaded; Failed to load (red) when it's on but couldn't load.
    public static string Status(Mod mod)
    {
        var on = Dependencies.IsOn(mod);
        var loaded = mod.state == ModLoadState.Loaded;
        var (key, color) = on && mod.state == ModLoadState.Failed ? ("SPIRE_MOD_MANAGER.STATUS_FAILED", Problem)
            : on != loaded ? ("SPIRE_MOD_MANAGER.STATUS_PENDING", Pending)
            : on ? ("SPIRE_MOD_MANAGER.STATUS_ENABLED", Enabled)
            : ("SPIRE_MOD_MANAGER.STATUS_DISABLED", Disabled);
        return $"[color={color}]{Menus.Text(key)}[/color]";
    }

    // What stops the mod from working, in plain words: the game's own load errors (wrong game version or
    // branch, bad files...), a duplicate copy, and, for a mod that's on, dependencies missing or off.
    public static List<string> Problems(Mod mod)
    {
        var problems = (mod.errors ?? []).Select(e => e.GetFormattedText()).ToList();
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

    // "0.110.1 or newer (this game: 0.111.0, beta)", plus the Workshop's supported branches, each once.
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
            text += "; " + Menus.Format("SPIRE_MOD_MANAGER.BRANCHES", ("branches", string.Join(", ", branches.Select(Range).Distinct())));
        return text;

        // A supported range; an empty end is open.
        static string Range((string Min, string Max) b) => (Branch(b.Min), Branch(b.Max)) switch
        {
            var (min, max) when min == max => min,
            ("", var max) => Menus.Format("SPIRE_MOD_MANAGER.BRANCH_UP_TO", ("branch", max)),
            (var min, "") => Menus.Format("SPIRE_MOD_MANAGER.BRANCH_FROM", ("branch", min)),
            var (min, max) => $"{min}–{max}",
        };
    }

    // The game's branch names in player words: "public" is the stable game; every beta reads "beta".
    static string Branch(string name) => name switch
    {
        "public" => Menus.Text("SPIRE_MOD_MANAGER.BRANCH_STABLE"),
        "none" or "" => "",
        _ when name.Contains("beta") => Menus.Text("SPIRE_MOD_MANAGER.BRANCH_BETA"),
        _ => name,
    };

    static readonly Dictionary<string, long> FolderSizes = [];

    // A local mod's size on disk, measured once per folder.
    static long FolderSize(string path)
    {
        if (FolderSizes.TryGetValue(path, out var size))
            return size;
        try
        {
            size = Directory.Exists(path) ? new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0;
        }
        catch (Exception)
        {
            size = 0;
        }
        return FolderSizes[path] = size;
    }

    static string Bytes(long bytes) => bytes switch
    {
        >= 1 << 30 => $"{bytes / (double)(1 << 30):0.0} GB",
        >= 1 << 20 => $"{bytes / (double)(1 << 20):0.0} MB",
        >= 1 << 10 => $"{bytes / (double)(1 << 10):0} KB",
        _ => $"{bytes} B",
    };

    // Fills the panel again for the mod it shows (after a tickbox change: its status may have changed). Not
    // while the panel shows the changes list: that stays until Close or a click on a row.
    public static void Refresh(Node screen)
    {
        if (Store.Data.VanillaView || screen.GetNodeOrNull<NModInfoContainer>("%ModInfoContainer") is not { } container
            || HistoryBar.ListOpen(container) || CategoryList.Selected is not { } mod || Meta(container, ModIdMeta) != mod.manifest?.id)
            return;
        container.Fill(mod);
    }

    // Vanilla View or no mod shown: none of our additions.
    public static void HideButtons(Node container)
    {
        CommitEdits(container);
        foreach (var name in new[] { "OpenFolderButton", LinkName, FieldsName })
            if (container.GetNodeOrNull<Control>(name) is { } control)
                control.Visible = false;
    }

    // After an undo or revert: the fields show the old state, so they must not be saved over the restored one.
    public static void Forget(Node container) => container.SetMeta(ModIdMeta, "");

    // Saves whatever is being typed in the panel's fields (before it shows another mod).
    static void CommitEdits(Node container)
    {
        foreach (var field in Fields.GetValueOrDefault(container.GetInstanceId(), []))
            field.Commit();
    }

    static string Meta(Node node, string key) => node.HasMeta(key) ? (string)node.GetMeta(key) : "";

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
        var link = new LinkButton { Name = LinkName, FocusMode = Control.FocusModeEnum.None };
        link.AddThemeFontOverride("font", ResourceLoader.Load<Font>("res://themes/kreon_regular_shared.tres"));
        link.AddThemeFontSizeOverride("font_size", 16);
        link.AddThemeColorOverride("font_color", new Color(0.6f, 0.8f, 1f));
        link.AddThemeColorOverride("font_hover_color", new Color(0.8f, 0.9f, 1f));
        link.AddThemeColorOverride("font_disabled_color", new Color(Cream, 0.6f));
        container.AddChild(link);
        link.Position = new Vector2(Inset, 56);
        return link;
    }

    // The player's own fields: Alias, Category (a picker), Tags and Notes.
    static Control AddFields(NModInfoContainer container)
    {
        var box = new VBoxContainer { Name = FieldsName };
        box.AddThemeConstantOverride("separation", 12);
        container.AddChild(box);
        box.AnchorRight = 1;
        (box.OffsetLeft, box.OffsetRight) = (Inset, -Inset);

        Control? Content() => container.GetParent()?.GetNodeOrNull<Control>("%ModsScrollContainer/Mask/Content");
        string? ModId() => Meta(container, ModIdMeta) is { Length: > 0 } id ? id : null;
        void Saved(ModField.Kind kind)
        {
            if (Content() is { } content)
                CategoryList.Apply(content); // rows: alias, tags, the ✎ mark
        }
        var fields = new List<ModField>();
        void Add(ModField.Kind kind)
        {
            var field = new ModField(kind, ModId, Saved);
            fields.Add(field);
            box.AddChild(field.Root);
        }
        Add(ModField.Kind.Alias);
        box.AddChild(CategoryPicker(container, Content, ModId));
        Add(ModField.Kind.Tags);
        Add(ModField.Kind.Notes);
        Fields[container.GetInstanceId()] = fields;
        container.TreeExiting += () => Fields.Remove(container.GetInstanceId());
        return box;
    }

    // "Category" and the mod's category as a picker: the same list as its right-click menu.
    static Control CategoryPicker(NModInfoContainer container, Func<Control?> content, Func<string?> modId)
    {
        var line = new VBoxContainer { Name = "Category" };
        line.AddThemeConstantOverride("separation", 4);
        var term = new Label { Text = Menus.Text("SPIRE_MOD_MANAGER.CATEGORY"), CustomMinimumSize = new Vector2(0, 26), VerticalAlignment = VerticalAlignment.Center };
        Style(term, "res://themes/kreon_regular_shared.tres", 18, Gold);
        line.AddChild(term);
        var picker = new Button
        {
            Name = "Picker", Flat = true, Alignment = HorizontalAlignment.Left, FocusMode = Control.FocusModeEnum.None,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand, SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin,
        };
        Style(picker, "res://themes/kreon_regular_shared.tres", 18, Cream);
        picker.AddThemeColorOverride("font_hover_color", Colors.White);
        picker.AddThemeColorOverride("font_pressed_color", Cream);
        var pad = new StyleBoxEmpty { ContentMarginLeft = 10, ContentMarginRight = 10, ContentMarginTop = 4, ContentMarginBottom = 4 };
        foreach (var state in new[] { "normal", "hover", "pressed", "focus" })
            picker.AddThemeStyleboxOverride(state, pad);
        picker.Pressed += () =>
        {
            if (content() is { } list && modId() is { } id)
                Menus.Show(container, CategoryList.CategoryItems(list, id), at: picker.GlobalPosition + new Vector2(0, picker.Size.Y + 2),
                    toggleKey: "panel-category");
        };
        line.AddChild(picker);
        return line;
    }

    // The fields sit right under the mod's details: the description shrinks to its text (up to the room left
    // above the fields), and the fields follow it. Once the text is laid out, so deferred.
    static void PlaceFields(Control container)
    {
        if (container.GetNodeOrNull<Control>(FieldsName) is not { } yours)
            return;
        var description = container.GetNode<RichTextLabel>("ModDescription");
        var top = description.OffsetTop;
        var height = yours.GetCombinedMinimumSize().Y;
        var room = container.Size.Y - ButtonsFromBottom - height - FieldsGap - top;
        var used = Math.Min(description.GetContentHeight() + 6, Math.Max(room, 80));
        description.OffsetBottom = top + used;
        yours.OffsetTop = top + used + FieldsGap;
        yours.OffsetBottom = yours.OffsetTop + height;
    }

    public static void SetAlias(string modId, string alias) =>
        Store.EditMod(modId, info => info.Alias = alias.Trim() is { Length: > 0 } a ? a : null);

    static string Escape(string text) => text.Replace("[", "[lb]");

    static void Style(Control control, string font, int size, Color color)
    {
        control.AddThemeFontOverride("font", ResourceLoader.Load<Font>(font));
        control.AddThemeFontSizeOverride("font_size", size);
        control.AddThemeColorOverride("font_color", color);
    }
}
