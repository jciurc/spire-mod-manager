using Godot;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Localization;
using System.Text.RegularExpressions;

namespace SpireModManager;

// The info panel in our views, top to bottom: the head beside the thumbnail (name and version, id and Workshop link,
// status, author, last update, compatibility); anything wrong; the description; size, published, subscribers, Workshop
// tags and other keywords (the hidden tags); dependencies both ways; the player's Alias, Category, Tags and Notes; Open
// Folder.
// Vanilla View: the panel exactly as the game makes it.
static class ModDetails
{
    static readonly Color Cream = new(1, 0.964706f, 0.886275f);
    static readonly Color Gold = new(0.937f, 0.784f, 0.318f);
    const string ModIdMeta = "spire_mod_manager_mod_id";
    const string FolderMeta = "spire_mod_manager_folder";
    const string WorkshopMeta = "spire_mod_manager_workshop";
    const string FieldsName = "SpireModManagerYours";
    public const string OthersName = "SpireModManagerOthersParked"; // "2 more from other mods: see the vanilla Modding page"
    const string LinkName = "SpireModManagerLink", SourceName = "SpireModManagerSource";
    public const string HeadName = "SpireModManagerHead";
    const string WiredMeta = "spire_mod_manager_wired";
    const string FullMeta = "spire_mod_manager_full_text"; // on the title and the link: their whole text when cut short, else ""
    const string Enabled = "#a6e3a1", Disabled = "#fff6e299", Pending = "#f5b56b", Problem = "#ff8070";
    const string GoldHex = "#efc851", LinkHex = "#99ccff";
    public const string ModLinkHex = "#cfe3ff"; // a mod's name, or its author, that a click acts on: light blue
    const string Quiet = "#fff6e299"; // no value to show (an unknown author, no description): subtle, so it doesn't read as one
    const string Fainter = "#fff6e280"; // Other keywords: half the panel text's alpha, below the facts without reading as one
    const string Bullet = "[color=#fff6e280]•[/color]"; // between facts on one line: dim
    const int VersionSize = 20;
    const int HeadSize = 16; // the head's text
    const int TitleMinSize = 24; // a long name shrinks this far (from Layout's 32), then is cut short

    // Along the inside of the panel's bottom edge, whatever its size.
    public const float ButtonsFromBottom = 72;
    public const float Inset = 22; // left edge of everything in the panel: title, link, text, fields
    public const float FieldsGap = 18;
    public const float ThumbWidth = 256, ThumbHeight = 144, ThumbRight = Inset; // the mod's picture, top right

    // The panel's own fields, one set per panel.
    static readonly Dictionary<ulong, List<ModField>> Fields = [];

#if DEVTOOLS
    // Self-test: the copy the panel was last filled for (its key), or "" for none.
    public static string ShownKey(Control container) => Meta(container, ModIdMeta);
#endif

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
        var key = Copies.Key(mod); // this copy's: a mod installed twice has an entry per copy
        if (Meta(container, ModIdMeta) != key)
            HistoryBar.HideBack(container); // another mod: no way back to the changes list (the same one refilled keeps it)
        container.SetMeta(ModIdMeta, key);
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
            (link.Disabled, link.Underline) = (false, LinkButton.UnderlineMode.Never); // our links: colour and the pointer
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
            picker.Text = CategoryList.CategoryName(key) + "  ▾";
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

    // "<id> (<where>)" then the status, within width: a long id is cut with "…" (the whole id as a tooltip). A link to
    // the Workshop page has the external-link mark after it (LinkMark).
    static void FitSource(Control source, string id, string where, float width)
    {
        var link = source.GetNode<LinkButton>(LinkName);
        var font = link.GetThemeFont("font");
        var size = link.GetThemeFontSize("font_size");
        var after = $" ({where})";
        var room = width - source.GetThemeConstant("separation") - source.GetNode<Control>("Raised").GetCombinedMinimumSize().X
            - font.GetStringSize(after, fontSize: size).X - LinkMark.Room(link);
        var shown = RowTitles.Cut(font, id, size, room);
        link.Text = shown + after;
        LinkMark.Fit(link);
        link.SetMeta(FullMeta, shown != id ? id : "");
    }

    // The title's pointer and the links' underline: ours in our views (the title is a link to its row), the game's in
    // Vanilla View, as it left them before we first changed them.
    public static void Looks(NModInfoContainer container, bool game)
    {
        var title = container.GetNode<Control>("ModTitle");
        var description = container.GetNode<RichTextLabel>("ModDescription");
        var was = GameLooks(container);
        title.MouseFilter = game ? (Control.MouseFilterEnum)was[0].AsInt32() : Control.MouseFilterEnum.Stop;
        title.MouseDefaultCursorShape = game ? (Control.CursorShape)was[1].AsInt32() : Control.CursorShape.PointingHand;
        description.MetaUnderlined = game && was[2].AsBool();
    }

    const string GameLooksMeta = "spire_mod_manager_game_looks";

    // The game's looks for Looks, recorded the first time anything of ours reaches the panel, before we change them.
    static Godot.Collections.Array GameLooks(NModInfoContainer container)
    {
        if (!container.HasMeta(GameLooksMeta))
        {
            var (title, description) = (container.GetNode<Control>("ModTitle"), container.GetNode<RichTextLabel>("ModDescription"));
            container.SetMeta(GameLooksMeta, new Godot.Collections.Array { (int)title.MouseFilter, (int)title.MouseDefaultCursorShape, description.MetaUnderlined });
        }
        return container.GetMeta(GameLooksMeta).AsGodotArray();
    }

    // Clicks and hovers, connected once per panel: the title or a dependency's name scrolls to its row, a Workshop tag or
    // another keyword searches, and hovering a name shows its card.
    static void Wire(NModInfoContainer container)
    {
        if (container.HasMeta(WiredMeta))
            return;
        container.SetMeta(WiredMeta, true);
        GameLooks(container); // first: WatchLinks takes the links' underline off, and Vanilla View puts back the game's
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
        static bool Searches(string meta) => meta.StartsWith("wtag:") || meta.StartsWith("htag:"); // a Workshop tag, a hidden one
        description.MetaClicked += meta =>
        {
            if (Store.Data.VanillaView)
                return; // the game's panel: its own text, none of our links
            if (meta.AsString() is { } target && target.StartsWith("mod:") && Content() is { } content)
                CategoryList.Reveal(content, target[4..], flash: false, focus: true);
            else if (meta.AsString() is { } tag && Searches(tag))
                Search.ByTag(container, tag[5..], Input.IsKeyPressed(Key.Shift)); // #tag, the mods with it
        };
        Hover.WatchLinks(description, "SpireModManagerTagCard",
            meta => Searches(meta) ? Tags.Hint(Menus.Text("SPIRE_MOD_MANAGER.SEARCH_CLICK_TIP")) : null, width: 320);
        CategoryList.WatchNames(description, Content); // hovering a name: its row lit (if in view), then its card
        // Hovering the thumbnail: the whole picture (our views).
        var image = container.GetNode<TextureRect>("ModImage");
        image.MouseFilter = Control.MouseFilterEnum.Pass;
        Hover.Watch(image, "SpireModManagerPictureCard", () => null, picture: () => Store.Data.VanillaView ? null : image.Texture);
    }

    // Anything wrong, the description, more facts (size, published, subscribers, Workshop tags, other keywords), then
    // dependencies.
    static string Describe(Mod mod)
    {
        var text = new System.Text.StringBuilder();
        void Fact(string key, string value) => text.Append($"[gold]{Menus.Text(key)}[/gold]: {value}\n");

        var problems = Problems(mod);
        foreach (var problem in problems)
            text.Append($"[color={Problem}]{Escape(problem)}[/color]\n");
        var copy = CopyLine(mod, links: true);
        if (copy is var (copyText, copyColor))
            text.Append($"[color={copyColor}]{copyText}[/color]\n");
        var game = GameBlock(mod, effects: true);
        text.Append(game);
        if (problems.Count > 0 || game.Length > 0 || copy != null)
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
        // The hidden tags that find it (Jordan: "so that way they're at least visible"), links as the Workshop tags are,
        // the whole line fainter.
        var keywords = HiddenTags.Of(mod);
        if (keywords.Count > 0)
            text.Append($"[color={Fainter}]{Menus.Text("SPIRE_MOD_MANAGER.OTHER_KEYWORDS")}: {string.Join(", ", keywords.Select(k => $"[url=htag:{k}]#{k}[/url]"))}[/color]\n");
        if (bytes > 0 || workshop != null || keywords.Count > 0)
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

        // What it changes in the game itself: the game's files it replaces and the game's code it patches (GameChanges).
        var changes = GameChanges.For(mod);
        string Changed(IReadOnlyList<string> subjects, int count, string many, string one)
        {
            const int Shown = 5;
            var listed = string.Join(", ", subjects.Take(Shown).Select(Escape));
            if (subjects.Count > Shown)
                listed += ", " + Menus.Format("SPIRE_MOD_MANAGER.BANNER_CARD_MORE", ("count", Menus.Number(subjects.Count - Shown)));
            return $"{listed} [color={Quiet}]({(count == 1 ? Menus.Text(one) : Menus.Format(many, ("count", Menus.Number(count))))})[/color]";
        }
        if (changes.Any && (needs.Count > 0 || neededBy.Count > 0))
            text.Append('\n');
        if (changes.Files > 0)
            Fact("SPIRE_MOD_MANAGER.GAME_FILES", Changed(changes.FileSubjects, changes.Files, "SPIRE_MOD_MANAGER.GAME_FILES_COUNT", "SPIRE_MOD_MANAGER.GAME_FILES_COUNT_ONE"));
        if (changes.Code > 0)
            Fact("SPIRE_MOD_MANAGER.GAME_CODE", Changed(changes.CodeSubjects, changes.Code, "SPIRE_MOD_MANAGER.GAME_CODE_COUNT", "SPIRE_MOD_MANAGER.GAME_CODE_COUNT_ONE"));
        return text.ToString().TrimEnd();
    }

    // The head's facts: author (click: search for their mods), last update (and its change notes), game versions.
    static string HeadFacts(Mod mod)
    {
        string Fact(string key, string value) => $"[color={GoldHex}]{Menus.Text(key)}[/color]: {value}";
        var author = mod.manifest?.author is { Length: > 0 } who ? $"[url=author:{who}][color={ModLinkHex}]{Escape(Short(who))}[/color][/url]" : Faint("SPIRE_MOD_MANAGER.UNKNOWN");
        var lines = new List<string> { Fact("SPIRE_MOD_MANAGER.AUTHOR", author) };
        // The last update, how long ago, a dim bullet, and Changelog: its change notes on the Workshop (with the
        // external-link mark, in the link).
        if (WorkshopInfo.For(mod) is { } workshop)
            lines.Add(Fact("SPIRE_MOD_MANAGER.UPDATED", $"{Date(workshop.Updated)} {Ago(workshop.Updated)}  {Bullet}\u00a0\u00a0" // no break after the bullet: it stays with the link
                + $"[url=patchnotes][color={LinkHex}]{Menus.Text("SPIRE_MOD_MANAGER.CHANGELOG")}{LinkMark.Tag(HeadSize, LinkHex)}[/color][/url]"));
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
        var info = Store.InfoFor(mod);
        var lines = new List<string> { $"[b]{Escape(id)}[/b]  {Bullet}  {Status(mod)}" };
        lines.AddRange(Problems(mod).Select(p => $"[color={Problem}]{Escape(p)}[/color]"));
        if (CopyLine(mod, links: false) is var (copyText, copyColor))
            lines.Add($"[color={copyColor}]{copyText}[/color]");
        if (GameBlock(mod, effects: false) is { Length: > 0 } game)
            lines.Add(game.TrimEnd('\n'));
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

    // The card other mods show for a mod (the API's ModCards): what the row's card says, with Jordan's list for it on
    // top: the mod's picture, its name as it gives it (never the player's alias: the caller's row shows that), its
    // version, author and description, then the player's category, every tag (the automatic ones too, in their
    // colours) and notes. full: nothing cut short (held with Alt).
    // standIn: a self-test's details in place of the player's (SharedCards.StandIn).
    public static string SharedCard(Mod mod, bool full = false, (string? Alias, string? Notes, List<string> Tags, string? Category)? standIn = null)
    {
        string Cut(string text, int max) => full || text.Length <= max ? text : text[..max].TrimEnd() + "…";
        var id = mod.manifest?.id ?? "";
        var info = Store.InfoFor(mod);
        var lines = new List<string>();
        var picture = $"res://{id}/mod_image.png"; // where the game's Modding page finds it
        if (id.Length > 0 && ResourceLoader.Exists(picture))
            lines.Add($"[img width=200]{picture}[/img]");
        var version = mod.manifest?.version is { Length: > 0 } v ? $"  [color=#fff6e280]{Escape(v)}[/color]" : "";
        lines.Add($"[b][font_size=20]{Escape(mod.manifest?.name is { Length: > 0 } name ? name : id)}[/font_size][/b]{version}");
        lines.Add($"{Escape(id)}  {Bullet}  {Status(mod)}");
        lines.AddRange(Problems(mod).Select(p => $"[color={Problem}]{Escape(p)}[/color]"));
        if (CopyLine(mod, links: false) is var (copyText, copyColor))
            lines.Add($"[color={copyColor}]{copyText}[/color]");
        if (GameBlock(mod, effects: false) is { Length: > 0 } game)
            lines.Add(game.TrimEnd('\n'));
        string Fact(string key, string value) => $"[color=#e8cf7a]{Menus.Text(key)}[/color]: {value}";
        lines.Add(Fact("SPIRE_MOD_MANAGER.AUTHOR", mod.manifest?.author is { Length: > 0 } author ? Escape(Cut(author, 80)) : Faint("SPIRE_MOD_MANAGER.UNKNOWN")));
        if (WorkshopInfo.For(mod) is { } workshop)
            lines.Add(Fact("SPIRE_MOD_MANAGER.UPDATED", Date(workshop.Updated)));
        // Its own words, as the panel shows them (markup kept), a few lines of them; cut outside a tag.
        if (mod.manifest?.description is { Length: > 0 } description)
            lines.AddRange(["", Described(description.Trim(), full ? int.MaxValue : 240)]);
        lines.Add("");
        var category = standIn is { } stand ? stand.Category ?? Menus.Text("SPIRE_MOD_MANAGER.UNCATEGORIZED") : CategoryList.CategoryName(Copies.Key(mod));
        lines.Add(Fact("SPIRE_MOD_MANAGER.CATEGORY", Escape(category)));
        var chips = standIn is { } instead
            ? [.. Tags.For(mod).Where(c => !c.Custom), .. instead.Tags.Select(t => new Tags.Chip(t, Tags.CustomColor, Custom: true))]
            : Tags.For(mod).ToList();
        if (chips.Count > 0)
            lines.Add(Fact("SPIRE_MOD_MANAGER.TAGS_TERM", string.Join("  ", chips.Select(c => $"[color=#{c.Color.ToHtml(false)}]{Escape(c.Text)}[/color]"))));
        if ((standIn is { } given ? given.Notes : info?.Notes) is { } notes)
            lines.Add($"[color=#e8cf7a]{Menus.Text("SPIRE_MOD_MANAGER.NOTES_TERM")}[/color]\n{Escape(Cut(notes, 300))}");
        return string.Join("\n", lines);
    }

    // A mod's description in a card of ours (a plain RichTextLabel, not the game's): the game label's colour tags as
    // colours, its moving ones ([sine], [jitter] and the like) dropped for their text, cut to about max characters, and
    // whatever it leaves open closed at its end.
    public static string Described(string description, int max) => Closed(CutMarkup(GameMarkup.Replace(description, m =>
        GameColors.TryGetValue(m.Groups[2].Value, out var color) ? m.Groups[1].Length > 0 ? "[/color]" : $"[color=#{color}]" : ""), max));

    static readonly Regex GameMarkup = new(@"\[(/?)(aqua|blue|gold|green|orange|pink|purple|red|sine|jitter|fade_in|fly_in|scramble|thinky_dots|ancient_banner)(?=[\]=\s])[^\]]*\]");

    // The game's (StsColors, as its RichText tags use them).
    static readonly Dictionary<string, string> GameColors = new()
    {
        ["aqua"] = "2aebbe", ["blue"] = "87ceeb", ["gold"] = "efc851", ["green"] = "7fff00", ["orange"] = "ffa518", ["pink"] = "ff78a0",
        ["purple"] = "ee82ee", ["red"] = "ff5555",
    };

    // Each tag Godot's rich text draws that's still open at the end, closed there (innermost first). As in Godot, a
    // closer closes the innermost open tag only when it names it; any other is drawn as text, and closes nothing.
    static string Closed(string bbcode)
    {
        var open = new List<string>();
        foreach (Match tag in OpeningOrClosing.Matches(bbcode))
        {
            var name = tag.Groups[2].Value;
            if (tag.Groups[1].Length == 0)
                open.Add(name);
            else if (open.Count > 0 && open[^1] == name)
                open.RemoveAt(open.Count - 1);
        }
        return open.Count == 0 ? bbcode : bbcode + string.Concat(Enumerable.Reverse(open).Select(name => $"[/{name}]"));
    }

    // A tag as Godot reads one: its name, then ], = or a space (so "[right-click]" is text, not [right]).
    static readonly Regex OpeningOrClosing = new(@"\[(/?)(b|i|u|s|code|center|left|right|fill|indent|url|hint|font|font_size|color|bgcolor|fgcolor|outline_size|outline_color|table|cell|ul|ol|lang|p|wave|tornado|shake|fade|rainbow|pulse|img|dropcap|opentype_features)(?=[\]=\s])[^\]]*\]");

    // Rich text cut to about max characters at a space, never inside a [tag] (Closed closes what it leaves open).
    static string CutMarkup(string text, int max)
    {
        if (text.Length <= max)
            return text;
        var at = text.LastIndexOf(' ', max);
        if (at <= 0)
            at = max;
        if (text.LastIndexOf('[', at - 1) is var open && open > text.LastIndexOf(']', at - 1))
            at = open;
        return text[..at].TrimEnd() + "…";
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

    // Enabled (green), Disabled (grey), Pending relaunch (orange) if whether it loads changes at the relaunch, Failed to
    // load (red), or Won't load (same ID).
    public static string Status(Mod mod)
    {
        var (text, color) = StatusParts(mod);
        return $"[color={color}]{text}[/color]";
    }

    // Won't load (same ID): ticked while another copy of its id loads after a relaunch (Copies.Loses), before a launch
    // too, not a relaunch away: orange, as nothing else says it won't load, but grey for a copy of this mod (kept on as a
    // backup on purpose). First, as it tells the next launch: a copy the game failed as a duplicate this launch says it
    // too (the game's error shows under From the game). Failed to load: ticked, and the game failed it. Pending
    // relaunch: whether it loads changes at the relaunch. Else Enabled or Disabled, by its box.
    // Pending relaunch, as the status says it: what loads after a relaunch isn't what loaded, for a copy neither set aside
    // as another loads nor failed (those statuses come first). #pending finds these.
    public static bool PendingRelaunch(Mod mod) => !Copies.Loses(mod) && !(Copies.Ticked(mod) && mod.state == ModLoadState.Failed)
        && Copies.Loads(mod) != (mod.state == ModLoadState.Loaded);

    static (string Text, string Color) StatusParts(Mod mod)
    {
        var ticked = Copies.Ticked(mod);
        var (key, color) = Copies.Loses(mod) ? ("SPIRE_MOD_MANAGER.STATUS_WONT_LOAD", mod.manifest?.id == ModEntry.ModId ? Disabled : Pending)
            : ticked && mod.state == ModLoadState.Failed ? ("SPIRE_MOD_MANAGER.STATUS_FAILED", Problem)
            : PendingRelaunch(mod) ? ("SPIRE_MOD_MANAGER.STATUS_PENDING", Pending)
            : ticked ? ("SPIRE_MOD_MANAGER.STATUS_ENABLED", Enabled)
            : ("SPIRE_MOD_MANAGER.STATUS_DISABLED", Disabled);
        return (Menus.Text(key), color);
    }

    // What stops the mod working, in our words: dependencies missing or off, for a mod that loads after a relaunch. A copy
    // that won't load as another copy of its id does isn't broken: its copy line says so (CopyLine). Plain text, escaped
    // where it's shown. The game's own load errors are its words, shown apart from ours (GameErrors).
    public static List<string> Problems(Mod mod) => DependencyProblems(mod);

    // For a mod installed more than once, a line about its copies (BBCode, another copy's name a link to its row where
    // links, plain where not), with its colour; null for none. The first that holds:
    // - the pick couldn't be applied this launch (SameIdLoad's self-check or canary): it says so (orange), on every copy;
    // - ticked while another copy loads after a relaunch: that one, and why (orange; the status says it won't load);
    // - ticked, not loaded now, and loading after a relaunch in place of the copy loaded now: so (orange);
    // - ticked and failed for its own reasons: how to load another copy instead (dim, after the red), another copy
    //   ticked or not (it isn't "the one that loads" when it failed);
    // - ticked with another copy ticked too: this one is the copy that loads (dim);
    // - off while another copy is on: the copy that loads (dim).
    // This mod's copies: SelfLine.
    public static (string Text, string Color)? CopyLine(Mod mod, bool links)
    {
        if (mod.manifest?.id is not { } id || Copies.Others(mod) is not { Count: > 0 } others)
            return null;
        string Other(Mod other)
        {
            var name = Escape(Copies.Named(other));
            return links ? $"[url=mod:{Copies.LinkKey(other)}][color={ModLinkHex}]{name}[/color][/url]" : name;
        }
        string Line(string key, Mod other) => Menus.Format(key, ("other", Other(other)));
        if (id == ModEntry.ModId)
            return SelfLine(mod, others, Other);
        if (SameIdLoad.Unchosen(id))
            return (Escape(Menus.Text("SPIRE_MOD_MANAGER.COPY_NOT_APPLIED")), Pending);
        if (Copies.LosesTo(mod) is { } lost)
            return (Why(lost.Why) is { } why ? Menus.Format("SPIRE_MOD_MANAGER.COPY_LOSES", ("other", Other(lost.Loads)), ("why", why))
                : Line("SPIRE_MOD_MANAGER.COPY_LOSES_PLAIN", lost.Loads), Pending);
        var ticked = Copies.Ticked(mod);
        if (ticked && mod.state is not (ModLoadState.Loaded or ModLoadState.Failed) && Copies.Loads(mod)
            && others.FirstOrDefault(o => o.state == ModLoadState.Loaded) is { } loaded)
            return (Line("SPIRE_MOD_MANAGER.COPY_LOADS_AFTER", loaded), Pending);
        if (ticked && mod.state == ModLoadState.Failed && mod.errors?.Any(e => e.LocEntryKey == "MOD_ERROR.DUPLICATE_ID") != true)
            return (Line("SPIRE_MOD_MANAGER.COPY_FAILED_OTHER", others.FirstOrDefault(Copies.Ticked) ?? others[0]), Disabled);
        if (others.FirstOrDefault(Copies.Ticked) is { } enabled)
            return ticked ? (Line("SPIRE_MOD_MANAGER.COPY_LOADS_ALSO", enabled), Disabled)
                // The one of them that loads (a copy that's off loads only through a switch it shares: then another).
                : (Line("SPIRE_MOD_MANAGER.COPY_OTHER_ENABLED", Copies.Loading(id) is { } loads && others.Contains(loads) && Copies.Ticked(loads) ? loads : enabled), Disabled);
        return null;
    }

    // This mod installed more than once: which copy runs now, and whether another stays on as a backup in case it's
    // removed (the boxes are locked on: Copies.Locked) or loads after a relaunch instead. Which loads is the game's choice
    // (SameIdLoad leaves ours to it), told apart only between a local copy and a Workshop one: of copies from one source,
    // the game takes the first in its own order, so no switch is foretold. Null while none of them is ticked (the red
    // banner says so) or none runs.
    static (string Text, string Color)? SelfLine(Mod mod, IReadOnlyList<Mod> others, Func<Mod, string> other)
    {
        // The switch lines' {why} only where one is known.
        string Line(string key, Mod named, string? why = null) => Menus.Format(key, ("other", other(named)), ("why", why ?? "")).TrimEnd();
        string? WhyLoses(Mod copy) => Copies.LosesTo(copy) is { } lost ? Why(lost.Why) : null;
        var ticked = Copies.Ticked(mod);
        var tickedOthers = others.Where(Copies.Ticked).ToList();
        if (!ticked && tickedOthers.Count == 0)
            return null;
        var loads = Copies.Loading(ModEntry.ModId);
        if (mod.state == ModLoadState.Loaded)
        {
            if (loads != null && others.Contains(loads) && (!ticked || loads.modSource != mod.modSource))
                return (Line("SPIRE_MOD_MANAGER.COPY_SELF_RUNS_SWITCH", loads, WhyLoses(mod)), Pending);
            return tickedOthers.Count > 0 ? (Line("SPIRE_MOD_MANAGER.COPY_SELF_RUNS", tickedOthers[0]), Disabled)
                : (Line("SPIRE_MOD_MANAGER.COPY_SELF_RUNS_OTHER_OFF", others[0]), Pending);
        }
        if (others.FirstOrDefault(o => o.state == ModLoadState.Loaded) is not { } running)
            return null;
        if (!ticked)
            return (Line("SPIRE_MOD_MANAGER.COPY_SELF_OFF", running), Pending);
        if (loads != null && Copies.CopyOf(loads) == Copies.CopyOf(mod) && (!Copies.Ticked(running) || running.modSource != mod.modSource))
            return (Line("SPIRE_MOD_MANAGER.COPY_SELF_BACKUP_SWITCH", running, WhyLoses(running)), Pending);
        return (Line("SPIRE_MOD_MANAGER.COPY_SELF_BACKUP", running), Disabled);
    }

    // Why a ticked copy doesn't load as another of its id does (Copies.LosesTo), in a sentence; null where none says it.
    static string? Why(Copies.LoseReason why) => why switch
    {
        Copies.LoseReason.LocalCopy => Escape(Menus.Text("SPIRE_MOD_MANAGER.COPY_WHY_LOCAL")),
        Copies.LoseReason.HigherVersion => Escape(Menus.Text("SPIRE_MOD_MANAGER.COPY_WHY_HIGHER")),
        Copies.LoseReason.EnabledLast => Escape(Menus.Text("SPIRE_MOD_MANAGER.COPY_WHY_SOURCE")),
        _ => null,
    };

    // The game's load errors for the mod, as its own panel shows them: its words, with the tags it marks parts with
    // ("Mod [red]{id}[/red] only supports..."). Shown apart from ours, under "From the game" (GameBlock).
    public static List<string> GameErrors(Mod mod) => [.. (mod.errors ?? []).Select(e => e.GetFormattedText())];

    // The game's messages we took off a mod that's off (BranchErrors), as plain text: not a problem (the mod doesn't
    // load), but nothing of the game's is hidden. Shown apart from ours, fainter (GameBlock).
    public static List<string> GameNotes(Mod mod) => [.. BranchErrors.Dropped(mod).Select(e => WithoutTags(e.GetFormattedText()))];

    // The game's words about the mod, set apart from ours (GameText): its load errors under "From the game", in its red
    // as its own panel has them; then the messages taken off a mod that's off, under their own caption, fainter. effects:
    // the label has the game's colour tags ([red], the panel's); our cards don't, so there the words go plain, in its red.
    // Empty when the game says nothing.
    static string GameBlock(Mod mod, bool effects)
    {
        var block = new System.Text.StringBuilder();
        if (GameErrors(mod) is { Count: > 0 } errors)
        {
            var words = effects ? string.Join("\n", errors.Select(e => $"[red]{e}[/red]")) : $"[color={GameRed}]{Escape(string.Join("\n", errors.Select(WithoutTags)))}[/color]";
            block.Append(GameText.Caption(Menus.Text("SPIRE_MOD_MANAGER.GAME_CAPTION"))).Append('\n').Append(GameText.Box(words)).Append('\n');
        }
        if (GameNotes(mod) is { Count: > 0 } notes)
            block.Append(GameText.Caption(Menus.Text("SPIRE_MOD_MANAGER.GAME_CAPTION_OFF"))).Append('\n')
                .Append(GameText.Box($"[color={Disabled}]{Escape(string.Join("\n", notes))}[/color]", dim: true)).Append('\n');
        return block.ToString();
    }

    const string GameRed = "#ff5555"; // the game's [red] (StsColors.red)

    // The text between tags in pairs ([red]...[/red], with any settings on the first), nested ones too. Only pairs: the
    // game's effects come in pairs, a bracket of a mod's own ("[CN] Pack") doesn't, and stays.
    public static string WithoutTags(string text)
    {
        while (PairedTag.Replace(text, "$2") is var plain && plain != text)
            text = plain;
        return text;
    }

    static readonly Regex PairedTag = new(@"\[([a-z_]+)(?:[ =][^\]]*)?\](.*?)\[/\1\]", RegexOptions.Singleline);

    // An enabled mod's missing or disabled dependencies, a line for each kind ("Requires mods to be installed: A, B").
    public static List<string> DependencyProblems(Mod mod)
    {
        if (!Copies.Loads(mod))
            return [];
        var needs = Dependencies.Of(mod).Distinct().Select(id => (Id: id, Mod: Dependencies.Find(id))).ToList();
        var missing = needs.Where(d => d.Mod == null).Select(d => d.Id).ToList();
        var off = needs.Where(d => d.Mod is { } found && !Copies.Loads(found)).Select(d => d.Mod?.manifest?.name ?? d.Id).ToList();
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
            || HistoryBar.ListOpen(container) || CategoryList.Selected is not { } mod || Meta(container, ModIdMeta) != Copies.Key(mod))
            return;
        container.Fill(mod);
    }

    // Vanilla View or no mod shown: none of our additions.
    public static void HideButtons(Node container)
    {
        CommitEdits(container);
        foreach (var name in new[] { "OpenFolderButton", SourceName, HeadName, FieldsName, OthersName })
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
        var link = new LinkButton { Name = LinkName, FocusMode = Control.FocusModeEnum.None, MouseDefaultCursorShape = Control.CursorShape.PointingHand };
        link.AddThemeFontOverride("font", ModEntry.Font("res://themes/kreon_regular_shared.tres"));
        link.AddThemeFontSizeOverride("font_size", 16);
        link.AddThemeColorOverride("font_color", new Color(0.6f, 0.8f, 1f));
        link.AddThemeColorOverride("font_hover_color", new Color(0.8f, 0.9f, 1f));
        link.AddThemeColorOverride("font_disabled_color", new Color(Cream, 0.6f));
        LinkMark.Add(link); // while it links to the Workshop page: the external-link mark, lit with the link
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
        Hover.Fonts(head, HeadSize);
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
                Menus.Show(container, CategoryList.CategoryItems(list, id), from: MenuPanel.Around(picker, 2), toggleKey: "panel-category");
        };
        line.AddChild(picker);
        return line;
    }

    // Places the player's fields right under the description, which shrinks to its text; other mods' panel parts go
    // between while the description keeps a few lines, the rest out of sight (Layout.FitOthers). Our views only.
    public static void PlaceFields(Control container)
    {
        if (!Layout.Wide || HistoryBar.ListOpen(container) || container.GetNodeOrNull<Control>(FieldsName) is not { } yours)
        {
            if (container.GetNodeOrNull<Control>(OthersName) is { } stale)
                stale.Visible = false; // the changes list or the narrow layout: no line placed
            return;
        }
        const float fewLines = 80;
        var description = container.GetNode<RichTextLabel>("ModDescription");
        var top = description.OffsetTop;
        var height = yours.GetCombinedMinimumSize().Y;
        var room = container.Size.Y - ButtonsFromBottom - height - FieldsGap - top;
        var text = description.GetContentHeight() + 6;
        var others = Layout.FitOthers(container, room - Math.Min(text, fewLines));
        room -= others.Sum(o => o.Size.Y + FieldsGap);
        // Other mods' parts with no room are out of sight (Layout.Park): a line says how many, and where they are.
        var parked = Layout.Parked(container);
        var line = container.GetNodeOrNull<Label>(OthersName) ?? AddOthersLine(container);
        line.Visible = parked > 0;
        if (parked > 0)
        {
            line.Text = Menus.Format("SPIRE_MOD_MANAGER.OTHERS_PARKED", ("count", Menus.Number(parked)));
            room -= line.GetCombinedMinimumSize().Y + FieldsGap;
        }
        var used = Math.Min(text, Math.Max(room, fewLines));
        description.OffsetBottom = top + used;
        var y = top + used + FieldsGap;
        foreach (var other in others)
        {
            Layout.PlaceOther(other, y);
            y += other.Size.Y + FieldsGap;
        }
        if (line.Visible)
        {
            (line.OffsetTop, line.OffsetBottom) = (y, y + line.GetCombinedMinimumSize().Y);
            y = line.OffsetBottom + FieldsGap;
        }
        yours.OffsetTop = y;
        yours.OffsetBottom = yours.OffsetTop + height;
    }

    static string Escape(string text) => text.Replace("[", "[lb]");

    // The line for other mods' parts out of sight, faint, across the panel at our left edge.
    static Label AddOthersLine(Control container)
    {
        var line = new Label { Name = OthersName, MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        Style(line, "res://themes/kreon_regular_shared.tres", 15, new Color(1, 0.964706f, 0.886275f, 0.6f));
        container.AddChild(line);
        line.SetAnchorsPreset(Control.LayoutPreset.TopWide);
        (line.OffsetLeft, line.OffsetRight) = (Inset, -Inset);
        return line;
    }

    static void Style(Control control, string font, int size, Color color)
    {
        control.AddThemeFontOverride("font", ModEntry.Font(font));
        control.AddThemeFontSizeOverride("font_size", size);
        control.AddThemeColorOverride("font_color", color);
    }
}
