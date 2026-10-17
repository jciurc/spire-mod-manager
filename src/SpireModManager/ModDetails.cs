using Godot;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Localization;
using System.Text.RegularExpressions;

namespace SpireModManager;

// The info panel in our views, top to bottom: the head beside the thumbnail (name and version, id and Workshop link,
// status, author, last update, compatibility); anything wrong; the description; size, published, subscribers and
// Workshop tags; dependencies both ways; the player's Alias, Category, Tags and Notes; Open Folder.
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
    public const string ModLinkHex = "#cfe3ff"; // a mod's name, or its author, that a click acts on: light blue
    const string Quiet = "#fff6e299"; // no value to show (an unknown author, no description): subtle, so it doesn't read as one
    const string Bullet = "[color=#fff6e280]•[/color]"; // between facts on one line: dim
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
            Looks(container, game: true);
            return;
        }
        HistoryBar.CloseList(container); // a mod picked: the panel is about it again
        CommitEdits(container); // whatever was typed for the previous mod
        Wire(container);
        Looks(container, game: false);
        var id = mod.manifest?.id;
        if (Meta(container, ModIdMeta) != (id ?? ""))
            HistoryBar.HideBack(container); // another mod: no way back to the changes list (the same one refilled keeps it)
        container.SetMeta(ModIdMeta, id ?? "");
        container.SetMeta(FolderMeta, mod.path);
        container.SetMeta(WorkshopMeta, Compat.WorkshopId(mod)?.ToString() ?? ""); // this copy's (a local one can share its id)

        // The name, with the version in grey beside it (fitted in FitTitle once the panel is placed).
        var title = container.GetNode<MegaRichTextLabel>("ModTitle");
        title.BbcodeEnabled = true;
        container.GetNode<MegaRichTextLabel>("ModDescription").Text = Describe(mod);

        // "<id> (<Workshop id>)" linking to its Workshop page, or "<id> (Local)", then its status (fitted: FitSource).
        var source = container.GetNodeOrNull<Control>(SourceName) ?? AddSource(container);
        source.Visible = true;
        var link = source.GetNode<LinkButton>(LinkName);
        string where;
        if (Compat.WorkshopId(mod) is { } workshopId)
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

    // The name and its version (smaller, grey) on one line: a long name shrinks to TitleMinSize, then is cut with "…"
    // so the version shows. Cut short: the whole text as a tooltip.
    static void FitTitle(MegaRichTextLabel title, string name, string? version)
    {
        // The auto-size measures it all at one size, so cut to what fits at TitleMinSize.
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
        // Lets the auto-size pick the size (TitleMinSize or more).
        Layout.Refit(title);
        title.SetMeta(FullMeta, shownName != name || shownNumber != number ? name + (number.Length > 0 ? gap + number : "") : "");
    }

    // "<id> (<where>)" then the status, within width: a long id is cut with "…" (the whole id as a tooltip).
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

    // The title's pointer and the links' underline: ours in our views (the title is a link to its row), the game's in
    // Vanilla View, as it left them before we first changed them.
    public static void Looks(NModInfoContainer container, bool game)
    {
        var title = container.GetNode<Control>("ModTitle");
        var description = container.GetNode<RichTextLabel>("ModDescription");
        if (!container.HasMeta(GameLooksMeta))
            container.SetMeta(GameLooksMeta, new Godot.Collections.Array { (int)title.MouseFilter, (int)title.MouseDefaultCursorShape, description.MetaUnderlined });
        var was = container.GetMeta(GameLooksMeta).AsGodotArray();
        title.MouseFilter = game ? (Control.MouseFilterEnum)was[0].AsInt32() : Control.MouseFilterEnum.Stop;
        title.MouseDefaultCursorShape = game ? (Control.CursorShape)was[1].AsInt32() : Control.CursorShape.PointingHand;
        description.MetaUnderlined = game && was[2].AsBool();
    }

    const string GameLooksMeta = "spire_mod_manager_game_looks";

    // Clicks and hovers, connected once per panel: the title or a dependency's name scrolls to its row, a Workshop tag
    // searches, and hovering a name shows its card.
    static void Wire(NModInfoContainer container)
    {
        if (container.HasMeta(WiredMeta))
            return;
        container.SetMeta(WiredMeta, true);
        Control? Content() => container.GetParent()?.GetNodeOrNull<Control>("%ModsScrollContainer/Mask/Content");
        var title = container.GetNode<Control>("ModTitle");
        title.GuiInput += e =>
        {
            if (!Store.Data.VanillaView && e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false } && Content() is { } content)
                CategoryList.Reveal(content, Meta(container, ModIdMeta), flash: false, focus: true);
        };
        // Cut short (FitTitle, our views): the whole name and version in a plain tooltip.
        Hover.Watch(title, "SpireModManagerFullText", () => !Store.Data.VanillaView && title.GetMeta(FullMeta, "").AsString() is { Length: > 0 } whole
            ? Escape(whole) : null, Hover.Timing.Slow);
        var description = container.GetNode<RichTextLabel>("ModDescription");
        description.MetaClicked += meta =>
        {
            if (Store.Data.VanillaView)
                return; // the game's panel: its own text, none of our links
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

    // Anything wrong, the description, more facts (size, published, subscribers, Workshop tags), then dependencies.
    static string Describe(Mod mod)
    {
        var text = new System.Text.StringBuilder();
        void Fact(string key, string value) => text.Append($"[gold]{Menus.Text(key)}[/gold]: {value}\n");

        var problems = Problems(mod);
        foreach (var problem in problems)
            text.Append($"[color={Problem}]{Escape(problem)}[/color]\n");
        if (problems.Count > 0)
            text.Append('\n');
        text.Append(mod.manifest?.description is { Length: > 0 } description ? description : Faint("SPIRE_MOD_MANAGER.NO_DESCRIPTION")).Append("\n\n");

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
            $"[url=mod:{d}][color={ModLinkHex}]{Escape(Dependencies.Find(d)?.manifest?.name ?? d)}[/color][/url]"));
        var needs = Dependencies.Of(mod);
        var neededBy = ModManager.Mods.Where(m => m != mod && Dependencies.Of(m).Contains(mod.manifest?.id ?? ""))
            .Select(m => m.manifest?.id ?? "").Distinct().ToList();
        if (needs.Count > 0)
            Fact("SPIRE_MOD_MANAGER.DEPENDENCIES", Links(needs));
        if (neededBy.Count > 0)
            Fact("SPIRE_MOD_MANAGER.NEEDED_BY", Links(neededBy));
        return text.ToString().TrimEnd();
    }

    // The head's facts: author (click: search for their mods), last update (and its change notes), game versions.
    static string HeadFacts(Mod mod)
    {
        string Fact(string key, string value) => $"[color={GoldHex}]{Menus.Text(key)}[/color]: {value}";
        var author = mod.manifest?.author is { Length: > 0 } who ? $"[url=author:{who}][color={ModLinkHex}]{Escape(Short(who))}[/color][/url]" : Faint("SPIRE_MOD_MANAGER.UNKNOWN");
        var lines = new List<string> { Fact("SPIRE_MOD_MANAGER.AUTHOR", author) };
        // The last update, how long ago, a dim bullet, and Changelog: its change notes on the Workshop.
        if (WorkshopInfo.For(mod) is { } workshop)
            lines.Add(Fact("SPIRE_MOD_MANAGER.UPDATED", $"{Date(workshop.Updated)} {Ago(workshop.Updated)}  {Bullet}\u00a0\u00a0" // no break after the bullet: it stays with the link
                + $"[url=patchnotes][color={LinkHex}]{Menus.Text("SPIRE_MOD_MANAGER.CHANGELOG")}[/color][/url]"));
        lines.Add(Fact("SPIRE_MOD_MANAGER.COMPATIBILITY", Compatibility(mod)));
        return string.Join("\n", lines);
    }

    // "(5 days ago)", "(1 day ago)", "(3 hours ago)"; green when it's under two days old.
    public static string Ago(DateTime when)
    {
        var age = DateTime.Now - when;
        var text = age.TotalHours < 1 ? Menus.Text("SPIRE_MOD_MANAGER.UNDER_AN_HOUR_AGO")
            : age.TotalHours < 24
            ? (int)age.TotalHours is var hours && hours == 1 ? Menus.Text("SPIRE_MOD_MANAGER.HOUR_AGO") : Menus.Format("SPIRE_MOD_MANAGER.HOURS_AGO", ("hours", Menus.Number(hours)))
            : (int)age.TotalDays is var days && days == 1 ? Menus.Text("SPIRE_MOD_MANAGER.DAY_AGO") : Menus.Format("SPIRE_MOD_MANAGER.DAYS_AGO", ("days", Menus.Number(days)));
        return $"[color={(age.TotalHours < 48 ? Enabled : Disabled)}]({text})[/color]";
    }

    // A row's hover card: id and state, anything wrong, author, last update, and the player's alias, tags and notes.
    // full: nothing cut short (held with Alt).
    public static string Card(Mod mod, bool full = false)
    {
        string Cut(string text, int max) => full || text.Length <= max ? text : text[..max].TrimEnd() + "…";
        var id = mod.manifest?.id ?? "";
        var info = Store.InfoFor(id);
        var lines = new List<string> { $"[b]{Escape(id)}[/b]  {Bullet}  {Status(mod)}" };
        lines.AddRange(Problems(mod).Select(p => $"[color={Problem}]{Escape(p)}[/color]"));
        string Fact(string key, string value) => $"[color=#e8cf7a]{Menus.Text(key)}[/color]: {value}";
        lines.Add(Fact("SPIRE_MOD_MANAGER.AUTHOR", mod.manifest?.author is { Length: > 0 } author ? Escape(Cut(author, 80)) : Faint("SPIRE_MOD_MANAGER.UNKNOWN")));
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

    // The date in the game's language, in its run history's pattern ("12. Oktober 2026"); English if that's missing
    // or unusable.
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

    // Enabled (green), Disabled (grey), Pending relaunch (orange) if changed since it loaded, or Failed to load (red).
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

    // What stops the mod working: the game's load errors, a duplicate copy, and (if on) dependencies missing or off.
    // Plain text, escaped where it's shown: the game's errors lose the tags it marks parts with ("Mod [red]{id}[/red]
    // only supports..."), which would show as text in our lines (one colour, escaped).
    public static List<string> Problems(Mod mod)
    {
        var problems = (mod.errors ?? []).Select(e => WithoutTags(e.GetFormattedText())).ToList();
        if (mod.state == ModLoadState.DisabledDuplicate)
            problems.Add(Menus.Text("SPIRE_MOD_MANAGER.STATE_DUPLICATE"));
        problems.AddRange(DependencyProblems(mod));
        return problems;
    }

    // The text between tags in pairs ([red]...[/red], with any settings on the first), nested ones too. Only pairs: the
    // game's effects come in pairs, a bracket of a mod's own ("[CN] Pack") doesn't, and stays.
    static string WithoutTags(string text)
    {
        while (PairedTag.Replace(text, "$2") is var plain && plain != text)
            text = plain;
        return text;
    }

    static readonly Regex PairedTag = new(@"\[([a-z_]+)(?:[ =][^\]]*)?\](.*?)\[/\1\]", RegexOptions.Singleline);

    // An enabled mod's missing or disabled dependencies, a line for each kind ("Requires mods to be installed: A, B").
    public static List<string> DependencyProblems(Mod mod)
    {
        if (!Dependencies.IsOn(mod))
            return [];
        var needs = Dependencies.Of(mod).Distinct().Select(id => (Id: id, Mod: Dependencies.Find(id))).ToList();
        var missing = needs.Where(d => d.Mod == null).Select(d => d.Id).ToList();
        var off = needs.Where(d => d.Mod is { } found && !Dependencies.IsOn(found)).Select(d => d.Mod?.manifest?.name ?? d.Id).ToList();
        var lines = new List<string>();
        if (missing.Count > 0)
            lines.Add(Menus.Format(missing.Count == 1 ? "SPIRE_MOD_MANAGER.PROBLEM_DEP_MISSING_ONE" : "SPIRE_MOD_MANAGER.PROBLEM_DEP_MISSING",
                ("mods", string.Join(", ", missing))));
        if (off.Count > 0)
            lines.Add(Menus.Format(off.Count == 1 ? "SPIRE_MOD_MANAGER.PROBLEM_DEP_OFF_ONE" : "SPIRE_MOD_MANAGER.PROBLEM_DEP_OFF",
                ("mods", string.Join(", ", off))));
        return lines;
    }

    // A text standing in for a value that isn't there ("unknown", "No description"), subtle.
    static string Faint(string key) => $"[color={Quiet}]{Escape(Menus.Text(key))}[/color]";

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

    // Fills the panel again for its mod (after a tickbox change), unless it's showing the changes list.
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
            ListFilter.Keep(ModId()); // its row stays under the list's Show: choice
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

    // Places the player's fields right under the description, which shrinks to its text; other mods' panel parts go
    // between while the description keeps a few lines, the rest out of sight (Layout.FitOthers). Our views only.
    public static void PlaceFields(Control container)
    {
        if (!Layout.Wide || HistoryBar.ListOpen(container) || container.GetNodeOrNull<Control>(FieldsName) is not { } yours)
            return;
        const float fewLines = 80;
        var description = container.GetNode<RichTextLabel>("ModDescription");
        var top = description.OffsetTop;
        var height = yours.GetCombinedMinimumSize().Y;
        var room = container.Size.Y - ButtonsFromBottom - height - FieldsGap - top;
        var text = description.GetContentHeight() + 6;
        var others = Layout.FitOthers(container, room - Math.Min(text, fewLines));
        room -= others.Sum(o => o.Size.Y + FieldsGap);
        var used = Math.Min(text, Math.Max(room, fewLines));
        description.OffsetBottom = top + used;
        var y = top + used + FieldsGap;
        foreach (var other in others)
        {
            Layout.PlaceOther(other, y);
            y += other.Size.Y + FieldsGap;
        }
        yours.OffsetTop = y;
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
