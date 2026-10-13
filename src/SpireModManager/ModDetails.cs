using Godot;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Localization;

namespace SpireModManager;

// The info panel in our views, top to bottom:
// - the head, left of a large thumbnail: the mod's own name (click: scroll to it in the list) with its version
//   in grey; "<id> (<Workshop id>)", linking to the Workshop page (or "(Local)"), and Enabled / Disabled /
//   Pending relaunch; author (click: search for their mods), last update (and a link to its Workshop change notes), compatibility;
// - anything wrong (red), the author's description;
// - size, published, subscribers, Workshop tags;
// - dependencies both ways (click a name: scroll to it in the list, its row taking the selected look);
// - the player's own Alias, Category, Tags and Notes (ModField), then Open Folder.
// Vanilla View: the panel exactly as the game makes it.
static class ModDetails
{
    static readonly Color Cream = new(1, 0.964706f, 0.886275f);
    static readonly Color Gold = new(0.937f, 0.784f, 0.318f);
    const string ModIdMeta = "spire_mod_manager_mod_id";
    const string FolderMeta = "spire_mod_manager_folder";
    const string WorkshopMeta = "spire_mod_manager_workshop";
    const string FieldsName = "SpireModManagerYours";
    const string LinkName = "SpireModManagerLink", SourceName = "SpireModManagerSource";
    public const string HeadName = "SpireModManagerHead";
    const string WiredMeta = "spire_mod_manager_wired";
    const string FullMeta = "spire_mod_manager_full_text"; // on the title and the link: their whole text when cut short, else ""
    const string Enabled = "#a6e3a1", Disabled = "#fff6e299", Pending = "#f5b56b", Problem = "#ff8070";
    const string GoldHex = "#efc851", LinkHex = "#99ccff";
    const int VersionSize = 20;
    const int TitleMinSize = 24; // a long name shrinks this far (from Layout's 32), then is cut short

    // Along the inside of the panel's bottom edge, whatever its size.
    public const float ButtonsFromBottom = 72;
    public const float Inset = 22; // left edge of everything in the panel: title, link, text, fields
    public const float FieldsGap = 18;
    public const float ThumbWidth = 256, ThumbHeight = 144, ThumbRight = Inset; // the mod's picture, top right

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
        if (Meta(container, ModIdMeta) != (id ?? ""))
            HistoryBar.HideBack(container); // another mod: no way back to the changes list (the same one refilled keeps it)
        container.SetMeta(ModIdMeta, id ?? "");
        container.SetMeta(FolderMeta, mod.path);
        container.SetMeta(WorkshopMeta, mod.workshopId?.ToString() ?? ""); // this copy's (a local one can share its id)

        // The game titles it with the name; ours too, with the version in grey beside it (the alias is in the
        // fields below). Its text is fitted once the panel is placed (FitTitle).
        var title = container.GetNode<MegaRichTextLabel>("ModTitle");
        title.BbcodeEnabled = true;
        container.GetNode<MegaRichTextLabel>("ModDescription").Text = Describe(mod);

        // "<id> (<Workshop id>)", a link to its Workshop page, or "<id> (Local)"; then whether it's on. The link's
        // text is fitted once the panel is placed (FitSource).
        var source = container.GetNodeOrNull<Control>(SourceName) ?? AddSource(container);
        source.Visible = true;
        var link = source.GetNode<LinkButton>(LinkName);
        string where;
        if (mod.workshopId is { } workshopId)
        {
            where = Menus.Format("SPIRE_MOD_MANAGER.SOURCE_STEAM", ("id", workshopId.ToString()));
            link.Uri = $"https://steamcommunity.com/sharedfiles/filedetails/?id={workshopId}";
            (link.Disabled, link.Underline) = (false, LinkButton.UnderlineMode.OnHover);
        }
        else
        {
            where = Menus.Text("SPIRE_MOD_MANAGER.SOURCE_LOCAL");
            link.Uri = "";
            (link.Disabled, link.Underline) = (true, LinkButton.UnderlineMode.Never);
        }
        var (state, color) = StatusParts(mod);
        var status = (Label)source.FindChild("Status", true, false);
        status.Text = state;
        status.AddThemeColorOverride("font_color", Color.FromHtml(color));

        // Beside the thumbnail: who made it, which game versions, when it was last updated.
        var head = container.GetNodeOrNull<RichTextLabel>(HeadName) ?? AddHead(container);
        head.Visible = true;
        head.Text = HeadFacts(mod);
        Layout.PlaceInfo(container); // the description goes under the head, or under the picture if there is one
        // The title and source line keep to the title's box (left of the thumbnail), now it's placed.
        FitTitle(title, mod.manifest?.name ?? id ?? "", mod.manifest?.version);
        FitSource(source, id ?? "", where, title.Size.X);

        var yours = container.GetNodeOrNull<Control>(FieldsName) ?? AddFields(container);
        yours.Visible = id != null;
        foreach (var field in Fields.GetValueOrDefault(container.GetInstanceId(), []))
            field.Load();
        if (yours.GetNodeOrNull<Button>("Category/Picker") is { } picker)
            picker.Text = CategoryList.CategoryName(id) + "  ▾";
        Callable.From(() => PlaceFields(container)).CallDeferred();

        var folder = container.GetNodeOrNull<Control>("OpenFolderButton") ?? AddButton(container, "OpenFolderButton",
            "SPIRE_MOD_MANAGER.OPEN_FOLDER", 20, () => OS.ShellOpen(Meta(container, FolderMeta)));
        folder.Visible = Directory.Exists(mod.path);
    }

    // The title on one line: the name, then its version (smaller, grey). A long name shrinks with the game's auto-size
    // down to TitleMinSize, then is cut with "…" so the version still shows (a very long version is cut too, to what
    // the name leaves or a third of the line). Cut short: the whole of it a second over the title.
    static void FitTitle(MegaRichTextLabel title, string name, string? version)
    {
        // The auto-size measures all of the text at one size, the version too (drawn smaller), and keeps it on one
        // line: whatever fits that way at TitleMinSize keeps at least that size.
        var font = title.GetThemeFont("normal_font");
        float Width(string text) => font.GetStringSize(text, fontSize: TitleMinSize).X;
        var room = title.Size.X - 4;
        const string gap = "  ";
        var number = version?.TrimStart('v', 'V') ?? "";
        var shownNumber = number.Length > 0 && Width(name + gap + number) > room
            ? RowTitles.Cut(font, number, TitleMinSize, Math.Max(room / 3, room - Width(name + gap))) : number;
        var shownName = RowTitles.Cut(font, name, TitleMinSize, shownNumber.Length > 0 ? room - Width(gap + shownNumber) : room);
        title.Text = Escape(shownName)
            + (shownNumber.Length > 0 ? $"{gap}[color={Disabled}][font_size={VersionSize}]{Escape(shownNumber)}[/font_size][/color]" : "");
        // Drawn at the size the auto-size picks for this text (TitleMinSize or more). Setting the text runs it only when
        // the text changed, and it skips writing a size equal to the one it wrote last; a view switch sets 32 straight in
        // between (Layout.FontSize), so a long title would stay at 32, on two lines. Its last size forgotten, it writes
        // the size again.
        title.Set(MegaRichTextLabel.PropertyName._lastSetSize, 0);
        title.CallDeferred(MegaRichTextLabel.MethodName.AdjustFontSize);
        title.SetMeta(FullMeta, shownName != name || shownNumber != number ? name + (number.Length > 0 ? gap + number : "") : "");
    }

    // "<id> (<where>)", then the dot and status, within width: a long id is cut with "…" so the rest still shows.
    // Cut short: the whole id a second over the link.
    static void FitSource(Control source, string id, string where, float width)
    {
        var link = source.GetNode<LinkButton>(LinkName);
        var font = link.GetThemeFont("font");
        var size = link.GetThemeFontSize("font_size");
        var after = $" ({where})";
        var room = width - source.GetThemeConstant("separation") - source.GetNode<Control>("Raised").GetCombinedMinimumSize().X
            - font.GetStringSize(after, fontSize: size).X;
        var shown = RowTitles.Cut(font, id, size, room);
        link.Text = shown + after;
        link.SetMeta(FullMeta, shown != id ? id : "");
    }

    // Clicking the title scrolls to the mod in the list, a dependency's name to that one, each row taking the
    // selected look (the panel stays on this mod). Hovering a name lights its row if it's in view, then shows
    // its card. Connected once per panel.
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
                CategoryList.Reveal(content, Meta(container, ModIdMeta), flash: false, focus: true);
        };
        // Cut short (FitTitle, our views): the whole name and version in a plain tooltip.
        Hover.Watch(title, "SpireModManagerFullText", () => !Store.Data.VanillaView && title.GetMeta(FullMeta, "").AsString() is { Length: > 0 } whole
            ? Escape(whole) : null, Hover.Timing.Slow);
        var description = container.GetNode<RichTextLabel>("ModDescription");
        description.MetaUnderlined = false;
        description.MetaClicked += meta =>
        {
            if (meta.AsString() is { } target && target.StartsWith("mod:") && Content() is { } content)
                CategoryList.Reveal(content, target[4..], flash: false, focus: true);
            else if (meta.AsString() is { } tag && tag.StartsWith("wtag:"))
                Search.ByText(container, tag[5..], Input.IsKeyPressed(Key.Shift)); // a Workshop tag: plain words
        };
        Hover.WatchLinks(description, "SpireModManagerTagCard",
            meta => meta.StartsWith("wtag:") ? Tags.Hint(Menus.Text("SPIRE_MOD_MANAGER.SEARCH_CLICK_TIP")) : null, width: 320);
        CategoryList.WatchNames(description, Content); // hovering a name: its row lit (if in view), then its card
        // Hovering the thumbnail: the whole picture (our views).
        var image = container.GetNode<TextureRect>("ModImage");
        image.MouseFilter = Control.MouseFilterEnum.Pass;
        Hover.Watch(image, "SpireModManagerPictureCard", () => null, picture: () => Store.Data.VanillaView ? null : image.Texture);
    }

    // Anything wrong, the author's description, more facts (size, published, subscribers, Workshop tags), then
    // dependencies; blank lines between the groups. (Name, version, source, status, author, update and
    // compatibility are in the head above.)
    static string Describe(Mod mod)
    {
        var text = new System.Text.StringBuilder();
        void Fact(string key, string value) => text.Append($"[gold]{Menus.Text(key)}[/gold]: {value}\n");

        var problems = Problems(mod);
        foreach (var problem in problems)
            text.Append($"[color={Problem}]{Escape(problem)}[/color]\n");
        if (problems.Count > 0)
            text.Append('\n');
        text.Append(mod.manifest?.description ?? Menus.Text("SPIRE_MOD_MANAGER.NO_DESCRIPTION")).Append("\n\n");

        var workshop = WorkshopInfo.For(mod);
        var bytes = workshop?.Size is > 0 and var size ? size : FolderSize(mod.path);
        if (bytes > 0)
            Fact("SPIRE_MOD_MANAGER.SIZE", Bytes(bytes));
        if (workshop != null)
            Fact("SPIRE_MOD_MANAGER.PUBLISHED", Date(workshop.Created));
        if (workshop?.Subscribers is { } subscribers)
            Fact("SPIRE_MOD_MANAGER.SUBSCRIBERS", Menus.Number(subscribers));
        if (workshop?.Tags.Count > 0)
            Fact("SPIRE_MOD_MANAGER.WORKSHOP_TAGS", string.Join(", ", workshop.Tags.Select(t => $"[url=wtag:{t}]{Escape(t)}[/url]"))); // click: search for it
        if (bytes > 0 || workshop != null)
            text.Append('\n');

        // What it needs, and what needs it.
        string Links(IEnumerable<string> ids) => string.Join(", ", ids.Select(d =>
            $"[url=mod:{d}][color=#cfe3ff]{Escape(Dependencies.Find(d)?.manifest?.name ?? d)}[/color][/url]"));
        var needs = Dependencies.Of(mod);
        var neededBy = ModManager.Mods.Where(m => m != mod && Dependencies.Of(m).Contains(mod.manifest?.id ?? ""))
            .Select(m => m.manifest?.id ?? "").Distinct().ToList();
        if (needs.Count > 0)
            Fact("SPIRE_MOD_MANAGER.DEPENDENCIES", Links(needs));
        if (neededBy.Count > 0)
            Fact("SPIRE_MOD_MANAGER.NEEDED_BY", Links(neededBy));
        return text.ToString().TrimEnd();
    }

    // The head's short facts: author (click: search for their mods), the last update (how long ago; its change
    // notes on the Workshop), and which game versions it's made for.
    static string HeadFacts(Mod mod)
    {
        string Fact(string key, string value) => $"[color={GoldHex}]{Menus.Text(key)}[/color]: {value}";
        var author = mod.manifest?.author is { Length: > 0 } who ? $"[url=author:{who}]{Escape(Short(who))}[/url]" : Menus.Text("SPIRE_MOD_MANAGER.UNKNOWN");
        var lines = new List<string> { Fact("SPIRE_MOD_MANAGER.AUTHOR", author) };
        // The last update, how long ago, and a link to its change notes on the Workshop.
        if (WorkshopInfo.For(mod) is { } workshop)
            lines.Add(Fact("SPIRE_MOD_MANAGER.UPDATED", $"{Date(workshop.Updated)} {Ago(workshop.Updated)}  "
                + $"[url=patchnotes][color={LinkHex}]{Menus.Text("SPIRE_MOD_MANAGER.PATCH_NOTES")}[/color][/url]"));
        lines.Add(Fact("SPIRE_MOD_MANAGER.COMPATIBILITY", Compatibility(mod)));
        return string.Join("\n", lines);
    }

    // "(5 days ago)", "(1 day ago)", "(3 hours ago)"; green when it's under two days old.
    public static string Ago(DateTime when)
    {
        var age = DateTime.Now - when;
        var text = age.TotalHours < 1 ? Menus.Text("SPIRE_MOD_MANAGER.UNDER_AN_HOUR_AGO")
            : age.TotalHours < 24
            ? (int)age.TotalHours is var hours && hours == 1 ? Menus.Text("SPIRE_MOD_MANAGER.HOUR_AGO") : Menus.Format("SPIRE_MOD_MANAGER.HOURS_AGO", ("hours", hours.ToString()))
            : (int)age.TotalDays is var days && days == 1 ? Menus.Text("SPIRE_MOD_MANAGER.DAY_AGO") : Menus.Format("SPIRE_MOD_MANAGER.DAYS_AGO", ("days", days.ToString()));
        return $"[color={(age.TotalHours < 48 ? Enabled : Disabled)}]({text})[/color]";
    }

    // The card shown after hovering a row's title: its id and state, anything wrong, author, last update, and
    // the player's alias, tags and notes.
    // full: nothing cut short (the card held with Alt); otherwise long alias, tags, notes and author are cut.
    public static string Card(Mod mod, bool full = false)
    {
        string Cut(string text, int max) => full || text.Length <= max ? text : text[..max].TrimEnd() + "…";
        var id = mod.manifest?.id ?? "";
        var info = Store.InfoFor(id);
        var lines = new List<string> { $"[b]{Escape(id)}[/b]  [color=#fff6e280]•[/color]  {Status(mod)}" };
        lines.AddRange(Problems(mod).Select(p => $"[color={Problem}]{Escape(p)}[/color]"));
        string Fact(string key, string value) => $"[color=#e8cf7a]{Menus.Text(key)}[/color]: {value}";
        lines.Add(Fact("SPIRE_MOD_MANAGER.AUTHOR", Escape(Cut(mod.manifest?.author ?? Menus.Text("SPIRE_MOD_MANAGER.UNKNOWN"), 80))));
        if (WorkshopInfo.For(mod) is { } workshop)
            lines.Add(Fact("SPIRE_MOD_MANAGER.UPDATED", Date(workshop.Updated)));
        if (info?.Alias is { } alias)
        {
            const string halfLine = "[font_size=8] [/font_size]"; // a little air around it
            lines.AddRange([halfLine, Fact("SPIRE_MOD_MANAGER.ALIAS_TERM", Escape(Cut(alias, 80))), halfLine]);
        }
        if (info?.Tags is { Count: > 0 } tags)
            lines.Add(Fact("SPIRE_MOD_MANAGER.TAGS_TERM", Escape(Cut(string.Join(", ", tags), 160))));
        if (info?.Notes is { } notes)
            lines.Add($"[color=#e8cf7a]{Menus.Text("SPIRE_MOD_MANAGER.NOTES_TERM")}[/color]\n{Escape(Cut(notes, 300))}");
        return string.Join("\n", lines);
    }

    // Text cut after 200 characters (some authors write a paragraph where their name goes).
    public static string Short(string text) => text.Length <= 200 ? text : text[..200].TrimEnd() + "…";

    // "October 12, 2026" in English, "12. Oktober 2026" in German: the game's own date pattern (the one its run
    // history uses, translated with the rest of the game) in the game's language. All in English if a game update
    // renames that text or it doesn't read as a date pattern ("October 12", not "Oktober 12" in an English pattern).
    public static string Date(DateTime date)
    {
        var english = date.ToString("MMMM d, yyyy", System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            return LocString.GetIfExists("run_history", "INFO.DATE_FORMAT")?.GetRawText() is { } pattern ? date.ToString(pattern, Menus.Culture) : english;
        }
        catch (Exception)
        {
            return english;
        }
    }

    // Enabled (green), Disabled (grey), or Pending relaunch (orange) when it's been turned on or off since it
    // loaded; Failed to load (red) when it's on but couldn't load.
    public static string Status(Mod mod)
    {
        var (text, color) = StatusParts(mod);
        return $"[color={color}]{text}[/color]";
    }

    static (string Text, string Color) StatusParts(Mod mod)
    {
        var on = Dependencies.IsOn(mod);
        var loaded = mod.state == ModLoadState.Loaded;
        var (key, color) = on && mod.state == ModLoadState.Failed ? ("SPIRE_MOD_MANAGER.STATUS_FAILED", Problem)
            : on != loaded ? ("SPIRE_MOD_MANAGER.STATUS_PENDING", Pending)
            : on ? ("SPIRE_MOD_MANAGER.STATUS_ENABLED", Enabled)
            : ("SPIRE_MOD_MANAGER.STATUS_DISABLED", Disabled);
        return (Menus.Text(key), color);
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

    // "1.5 MB": the number in the game's language, the unit and its place from the loc table.
    static string Bytes(long bytes)
    {
        var (key, size) = bytes switch
        {
            >= 1 << 30 => ("SPIRE_MOD_MANAGER.SIZE_GB", (bytes / (double)(1 << 30)).ToString("0.0", Menus.Culture)),
            >= 1 << 20 => ("SPIRE_MOD_MANAGER.SIZE_MB", (bytes / (double)(1 << 20)).ToString("0.0", Menus.Culture)),
            >= 1 << 10 => ("SPIRE_MOD_MANAGER.SIZE_KB", (bytes / (double)(1 << 10)).ToString("0", Menus.Culture)),
            _ => ("SPIRE_MOD_MANAGER.SIZE_BYTES", bytes.ToString(Menus.Culture)),
        };
        return Menus.Format(key, ("size", size));
    }

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
        foreach (var name in new[] { "OpenFolderButton", SourceName, HeadName, FieldsName })
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

    static Control AddButton(Control parent, string name, string locKey, float x, Action onPressed)
    {
        var button = ModEntry.AddButton(parent, locKey, new Vector2(180, 52), onPressed);
        button.Name = name;
        button.AnchorTop = button.AnchorBottom = 1;
        (button.OffsetLeft, button.OffsetRight, button.OffsetTop, button.OffsetBottom) =
            (x, x + 180, -ButtonsFromBottom + 6, -ButtonsFromBottom + 58);
        return button;
    }

    // Under the title: the link (or local id), a dim dot, the status, all one size.
    static Control AddSource(Control container)
    {
        var line = new HBoxContainer { Name = SourceName, MouseFilter = Control.MouseFilterEnum.Ignore };
        line.AddThemeConstantOverride("separation", 8);
        var link = new LinkButton { Name = LinkName, FocusMode = Control.FocusModeEnum.None };
        link.AddThemeFontOverride("font", ModEntry.Font("res://themes/kreon_regular_shared.tres"));
        link.AddThemeFontSizeOverride("font_size", 16);
        link.AddThemeColorOverride("font_color", new Color(0.6f, 0.8f, 1f));
        link.AddThemeColorOverride("font_hover_color", new Color(0.8f, 0.9f, 1f));
        link.AddThemeColorOverride("font_disabled_color", new Color(Cream, 0.6f));
        // A long id cut short (FitSource): the whole of it in a plain tooltip.
        Hover.Watch(link, "SpireModManagerFullText", () => link.GetMeta(FullMeta, "").AsString() is { Length: > 0 } whole ? Escape(whole) : null, Hover.Timing.Slow);
        line.AddChild(link);
        // The dot and status a pixel higher: plain labels sit lower than the link's text.
        var raised = new MarginContainer { Name = "Raised", MouseFilter = Control.MouseFilterEnum.Ignore };
        raised.AddThemeConstantOverride("margin_top", -1);
        raised.AddThemeConstantOverride("margin_bottom", 1);
        var words = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        words.AddThemeConstantOverride("separation", 8);
        raised.AddChild(words);
        var dot = new Label { Name = "Dot", Text = "·", MouseFilter = Control.MouseFilterEnum.Ignore, VerticalAlignment = VerticalAlignment.Center };
        Style(dot, "res://themes/kreon_regular_shared.tres", 16, new Color(Cream, 0.4f));
        words.AddChild(dot);
        var status = new Label { Name = "Status", MouseFilter = Control.MouseFilterEnum.Ignore, VerticalAlignment = VerticalAlignment.Center };
        Style(status, "res://themes/kreon_regular_shared.tres", 16, Cream);
        words.AddChild(status);
        line.AddChild(raised);
        container.AddChild(line);
        line.Position = new Vector2(Inset, 56);
        return line;
    }

    // Left of the thumbnail, under the source line.
    static RichTextLabel AddHead(Control container)
    {
        var head = new RichTextLabel
        {
            Name = HeadName, BbcodeEnabled = true, FitContent = true, ScrollActive = false, AutowrapMode = TextServer.AutowrapMode.WordSmart,
            MouseFilter = Control.MouseFilterEnum.Pass, MetaUnderlined = false,
        };
        Hover.Fonts(head, 16);
        head.MetaClicked += meta =>
        {
            if (meta.AsString() is { } target && target.StartsWith("author:"))
                Search.ByAuthor(container, target[7..], Input.IsKeyPressed(Key.Shift));
            else if (meta.AsString() == "patchnotes" && Meta(container, WorkshopMeta) is { Length: > 0 } workshop)
                OS.ShellOpen($"https://steamcommunity.com/sharedfiles/filedetails/changelog/{workshop}"); // the shown mod's change notes page
        };
        Hover.WatchLinks(head, "SpireModManagerTagCard",
            meta => meta.StartsWith("author:") ? Tags.Hint(Menus.Text("SPIRE_MOD_MANAGER.SEARCH_CLICK_TIP")) : null, width: 320);
        container.AddChild(head);
        head.AnchorRight = 1;
        (head.OffsetLeft, head.OffsetTop, head.OffsetRight, head.OffsetBottom) = (Inset, 88, -(ThumbWidth + ThumbRight + 20), 88);
        return head;
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
                CategoryList.Apply(content); // rows: alias, tags, the notepad
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
            Name = "Picker", Alignment = HorizontalAlignment.Left, FocusMode = Control.FocusModeEnum.None,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand, SizeFlagsHorizontal = Control.SizeFlags.Fill,
        };
        Style(picker, "res://themes/kreon_regular_shared.tres", 18, Cream);
        picker.AddThemeColorOverride("font_hover_color", Colors.White);
        picker.AddThemeColorOverride("font_pressed_color", Cream);
        // The same box as the other fields: darker at rest, lighter under the mouse.
        foreach (var (state, shade) in new[] { ("normal", ModField.Shade.Rest), ("hover", ModField.Shade.Hover), ("pressed", ModField.Shade.Active), ("focus", ModField.Shade.Rest) })
            picker.AddThemeStyleboxOverride(state, ModField.Look(shade));
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
    public static void PlaceFields(Control container)
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

    static string Escape(string text) => text.Replace("[", "[lb]");

    static void Style(Control control, string font, int size, Color color)
    {
        control.AddThemeFontOverride("font", ModEntry.Font(font));
        control.AddThemeFontSizeOverride("font_size", size);
        control.AddThemeColorOverride("font_color", color);
    }
}
