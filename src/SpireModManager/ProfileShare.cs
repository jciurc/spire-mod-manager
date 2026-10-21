using System.Text;
using System.Text.RegularExpressions;
using Godot;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;

namespace SpireModManager;

// A mod list as CSV, in Better Mod Menu's column order: Mod Id, Name, Version, Enabled (TRUE/FALSE), then the optional
// Category, Workshop Id (the id only, as Steam may move an item's page), Alias and Notes. Writing follows RFC 4180,
// with a ' before values a spreadsheet would run as a formula. Reading is forgiving: a BOM, CRLF or LF, a ; or tab
// separator, columns by header in any order. Only Mod Id and Enabled are needed, and a blank or missing Enabled means
// enabled. Category (Better Mod Menu's Group, where "Unassigned" is none), Alias and Notes are read when there.
static class ProfileCsv
{
    // What a shared list holds besides id, name, version and Enabled: the category by default, the rest off. Disabled:
    // rows for the mods that are off too.
    public record Options(bool Category = true, bool Disabled = false, bool Alias = false, bool Notes = false);

    public static string[] Header(Options options) =>
    [
        "Mod Id", "Name", "Version", "Enabled", .. options.Category ? ["Category"] : Array.Empty<string>(), "Workshop Id",
        .. options.Alias ? ["Alias"] : Array.Empty<string>(), .. options.Notes ? ["Notes"] : Array.Empty<string>(),
    ];

    // One mod to write. WorkshopId: "" for a mod that isn't from the Workshop.
    public record Row(string Id, string Name, string Version, bool Enabled, string Category, string WorkshopId, string Alias = "", string Notes = "");

    // One mod read: the parts an import uses ("" for a value the list doesn't give).
    public record Entry(string Id, bool Enabled, string Name, string WorkshopLink, string Category = "", string Alias = "", string Notes = "");

    // As long as the boxes that name them take: a category's rename box, a mod's alias box.
    const int CategoryMax = 40, AliasMax = 80;

    // What Read found: the mods, or why there are none (the text to show).
    public record Result(List<Entry> Mods, string? Error);

    const string WorkshopPage = "https://steamcommunity.com/sharedfiles/filedetails/?id=";

    public static string WorkshopLink(ulong id) => WorkshopPage + id;

    // A Workshop link rebuilt from the id in one (any link with "id=<number>"), or "" when there's none. Only links
    // made this way are ever opened: a pasted list can hold any address.
    public static string CleanLink(string link) =>
        Regex.Match(link, @"[?&]id=(\d{1,20})\b") is { Success: true } m && ulong.TryParse(m.Groups[1].Value, out var id) ? WorkshopLink(id) : "";

    public static bool IsWorkshopLink(string link) => link.StartsWith(WorkshopPage) && link.Length > WorkshopPage.Length && link[WorkshopPage.Length..].All(char.IsAsciiDigit);

    // A Workshop column's value as a link we make: an id alone (ours), or any link with "id=<number>" (Better Mod Menu's).
    static string WorkshopFrom(string value) =>
        value.Length is > 0 and <= 20 && value.All(char.IsAsciiDigit) && ulong.TryParse(value, out var id) ? WorkshopLink(id) : CleanLink(value);

    public static string Write(IEnumerable<Row> rows, Options options)
    {
        var text = new StringBuilder();
        Line(text, Header(options));
        foreach (var row in rows.Where(r => options.Disabled || r.Enabled))
            Line(text, [
                row.Id, row.Name, row.Version, row.Enabled ? "TRUE" : "FALSE", .. options.Category ? [row.Category] : Array.Empty<string>(),
                row.WorkshopId, .. options.Alias ? [row.Alias] : Array.Empty<string>(), .. options.Notes ? [row.Notes] : Array.Empty<string>(),
            ]);
        return text.ToString().TrimEnd('\r', '\n'); // no line break after the last row (a quoted value's own ends in '"')
    }

    static void Line(StringBuilder text, IReadOnlyList<string> values)
    {
        for (var i = 0; i < values.Count; i++)
        {
            if (i > 0)
                text.Append(',');
            var value = Guard(values[i]);
            if (value.IndexOfAny([',', '"', '\r', '\n']) >= 0)
                text.Append('"').Append(value.Replace("\"", "\"\"")).Append('"');
            else
                text.Append(value);
        }
        text.Append("\r\n");
    }

    static bool FormulaStart(char c) => c is '=' or '+' or '-' or '@' or '\t' or '\r';

    static string Guard(string value) => value.Length > 0 && FormulaStart(value[0]) ? "'" + value : value;

    static string Unguard(string value) => value.Length > 1 && value[0] == '\'' && FormulaStart(value[1]) ? value[1..] : value;

    // The records in CSV text, each a list of its values (blank lines skipped).
    public static List<List<string>> Records(string text)
    {
        var start = text.StartsWith('\uFEFF') ? 1 : 0;
        var separator = Separator(text, start);
        var records = new List<List<string>>();
        var record = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        void EndRecord()
        {
            record.Add(field.ToString());
            field.Clear();
            if (record.Count > 1 || record[0].Trim().Length > 0)
                records.Add(record);
            record = [];
        }
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c != '"')
                    field.Append(c);
                else if (i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else
                    quoted = false;
            }
            else if (c == '"')
                quoted = true;
            else if (c == separator)
            {
                record.Add(field.ToString());
                field.Clear();
            }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                    i++;
                EndRecord();
            }
            else
                field.Append(c);
        }
        if (field.Length > 0 || record.Count > 0)
            EndRecord();
        return records;
    }

    // The character between values: a comma, unless the first line with text has more semicolons or tabs than commas
    // (a two-column list has just one).
    static char Separator(string text, int start)
    {
        var first = text[start..].Split('\r', '\n').FirstOrDefault(line => line.Trim().Length > 0) ?? "";
        int Count(char c) => first.Count(x => x == c);
        var other = Count(';') >= Count('\t') ? ';' : '\t';
        return Count(other) > Count(',') ? other : ',';
    }

    public static Result Read(string text)
    {
        static Result Problem(string error) => new([], error);
        if (text.Trim('\uFEFF', ' ', '\t', '\r', '\n').Length == 0)
            return Problem(Menus.Text("SPIRE_MOD_MANAGER.IMPORT_EMPTY"));
        var records = Records(text);
        if (records.Count == 0) // only empty values
            return Problem(Menus.Text("SPIRE_MOD_MANAGER.IMPORT_EMPTY"));
        var header = records[0].Select(h => h.Trim()).ToList();
        int Column(params string[] names) => header.FindIndex(h => names.Any(n => string.Equals(h, n, StringComparison.OrdinalIgnoreCase)));
        var (id, enabled, name, link) = (Column("Mod Id", "Id", "ModId"), Column("Enabled"), Column("Name"), Column("Workshop Id", "Workshop Link", "WorkshopId"));
        var (category, alias, notes) = (Column("Category", "Group"), Column("Alias"), Column("Notes"));
        if (id < 0)
            return Problem(Menus.Text("SPIRE_MOD_MANAGER.IMPORT_NO_ID"));
        if (enabled < 0)
            return Problem(Menus.Text("SPIRE_MOD_MANAGER.IMPORT_NO_ENABLED"));
        var mods = new List<Entry>();
        for (var r = 1; r < records.Count; r++)
        {
            var record = records[r];
            string Cell(int i) => i >= 0 && i < record.Count ? Unguard(record[i].Trim()) : "";
            if (Cell(id) is not { Length: > 0 } modId)
                continue;
            if (Bool(Cell(enabled)) is not { } on) // (a row cut short: blank)
                return Problem(Menus.Format("SPIRE_MOD_MANAGER.IMPORT_BAD_ENABLED", ("mod", modId), ("value", Cell(enabled))));
            // Better Mod Menu writes "Unassigned", exactly, for a mod in no group (its UnassignedGroupName; its own rules
            // compare it case for case, so "unassigned" is a group there).
            var group = OneLine(Cell(category));
            mods.Add(new Entry(modId, on, Cell(name), WorkshopFrom(Cell(link)),
                group == "Unassigned" ? "" : Cut(group, CategoryMax), Cut(OneLine(Cell(alias)), AliasMax),
                Cell(notes).Replace("\r\n", "\n").Replace('\r', '\n')));
        }
        return mods.Count == 0 ? Problem(Menus.Text("SPIRE_MOD_MANAGER.IMPORT_NO_MODS")) : new Result(mods, null);
    }

    // At most `most` characters as Godot counts them (code points, as its boxes' MaxLength does), never half of one.
    static string Cut(string value, int most) =>
        value.EnumerateRunes().Count() <= most ? value : string.Concat(value.EnumerateRunes().Take(most).Select(r => r.ToString())).TrimEnd();

    // A category or alias on one line, as our boxes make them: line breaks and tabs in a quoted cell become spaces.
    static string OneLine(string value) => string.Join(" ", value.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    static bool? Bool(string value) => value.ToLowerInvariant() switch
    {
        "true" or "1" or "yes" or "" => true,
        "false" or "0" or "no" => false,
        _ => null,
    };
}

// Sharing a profile as text and making one from shared text (ProfileCsv), from the profile list or the picker's
// right-click menu: two dialogs over the page. Share shows the active profile's list with options (kept for the
// session, or across launches with Remember selection), Save (a .txt) and Copy. Import takes pasted text or a file,
// previews what the list would change and names the missing mods; Create profile makes it and switches to it (one
// undo step).
static class ProfileShare
{
    public const string Name = "SpireModManagerShare";
    static readonly Color Cream = new(1, 0.964706f, 0.886275f);
    static readonly Color Gold = new(0.937f, 0.784f, 0.318f);
    static readonly Color ErrorColor = new(1f, 0.5f, 0.44f);
    const string GoldHex = "#efc851", DimHex = "#fff6e299", LinkHex = "#99ccff";
    const int BodySize = 18; // the dialogs' text

    public static bool IsOpen => Modal.IsOpen(Name);

    // The share dialog's tick boxes: as last left this session, or else as kept from an earlier one (Remember selection,
    // Store.Data.ShareChoices), or else the defaults.
    static ProfileCsv.Options? _options;

    static ProfileCsv.Options Choices => _options ??= Store.Data.ShareChoices is { } kept
        ? new ProfileCsv.Options(kept.Category, kept.Disabled, kept.Alias, kept.Notes) : new ProfileCsv.Options();

#if DEVTOOLS
    // Self-test: the tick boxes as a new launch finds them.
    public static void ForgetSessionChoices() => _options = null;
#endif

    // Remember selection on: these tick boxes kept in our data (saved now), for the next launches too. Off: forgotten.
    static void Keep(bool on)
    {
        Store.Data.ShareChoices = on ? new ShareChoice(Choices.Category, Choices.Disabled, Choices.Alias, Choices.Notes) : null;
        Store.Save();
    }

    public static void ShowShare(NModdingScreen screen)
    {
        if (Profiles.Active is not { } profile || Dialog(screen, "SPIRE_MOD_MANAGER.SHARE_PROFILE", 920) is not { } column)
            return;
        var rows = Profiles.ShareRows(profile);
        var csv = ProfileCsv.Write(rows, Choices);

        var body = Body("Body");
        body.Text = Menus.Format("SPIRE_MOD_MANAGER.SHARE_BODY", ("name", $"[color={GoldHex}]{Escape(profile.Name)}[/color]"));
        column.AddChild(body);
        var box = TextBox("CsvText", editable: false);
        box.Text = csv;
        // The options row under the words (each tick rewrites the list), with Remember selection at its right end.
        var row = new HBoxContainer { Name = "OptionsRow" };
        row.AddThemeConstantOverride("separation", 22);
        var options = new HFlowContainer { Name = "Options", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; // wraps where a language's words run long
        options.AddThemeConstantOverride("h_separation", 22);
        options.AddThemeConstantOverride("v_separation", 4);
        void Set(Func<ProfileCsv.Options, ProfileCsv.Options> change)
        {
            _options = change(Choices);
            box.Text = csv = ProfileCsv.Write(rows, Choices);
            if (Store.Data.ShareChoices != null)
                Keep(true);
        }
        options.AddChild(Option("SPIRE_MOD_MANAGER.SHARE_CATEGORY", Choices.Category, on => Set(o => o with { Category = on }), "IncludeCategory"));
        options.AddChild(Option("SPIRE_MOD_MANAGER.SHARE_DISABLED", Choices.Disabled, on => Set(o => o with { Disabled = on }), "IncludeDisabled"));
        options.AddChild(Option("SPIRE_MOD_MANAGER.SHARE_ALIAS", Choices.Alias, on => Set(o => o with { Alias = on }), "IncludeAlias"));
        options.AddChild(Option("SPIRE_MOD_MANAGER.SHARE_NOTES", Choices.Notes, on => Set(o => o with { Notes = on }), "IncludeNotes"));
        row.AddChild(options);
        var remember = Option("SPIRE_MOD_MANAGER.SHARE_REMEMBER", Store.Data.ShareChoices != null, Keep, "RememberChoices");
        remember.SizeFlagsVertical = Control.SizeFlags.ShrinkBegin;
        row.AddChild(remember);
        column.AddChild(row);
        column.AddChild(box);
        var status = Status(reserved: true);
        column.AddChild(status);

        var buttons = Buttons();
        // Save, then Copy.
        AddButton(buttons, "SPIRE_MOD_MANAGER.SAVE_LIST", () => Browse(column, save: true, FileName(profile.Name, DateTime.Now), path =>
        {
            var error = SaveTo(path, csv);
            Say(status, error == null ? Menus.Format("SPIRE_MOD_MANAGER.SAVED_AS", ("file", Path.GetFileName(path)))
                : Menus.Format("SPIRE_MOD_MANAGER.SAVE_FAILED", ("error", error)), error != null);
        }), "SaveCsvButton");
        Button? copy = null;
        copy = AddButton(buttons, "SPIRE_MOD_MANAGER.COPY", () =>
        {
            DisplayServer.ClipboardSet(csv);
            if (copy is not { } button) // set as AddButton returns, before any press
                return;
            button.Text = Menus.Text("SPIRE_MOD_MANAGER.COPIED");
            button.GetTree().CreateTimer(1.5).Timeout += () =>
            {
                if (GodotObject.IsInstanceValid(button))
                    button.Text = Menus.Text("SPIRE_MOD_MANAGER.COPY");
            };
        }, "CopyButton");
        Spacer(buttons);
        AddButton(buttons, "SPIRE_MOD_MANAGER.CLOSE", () => Close(), "CloseShareButton");
        column.AddChild(buttons);
    }

    public static void ShowImport(NModdingScreen screen)
    {
        if (Dialog(screen, "SPIRE_MOD_MANAGER.IMPORT_PROFILE") is not { } column)
            return;
        var body = Body("Body");
        body.Text = Menus.Text("SPIRE_MOD_MANAGER.IMPORT_BODY");
        column.AddChild(body);
        var box = TextBox("CsvText", editable: true);
        box.CustomMinimumSize = new Vector2(0, 200); // shorter than Share's: the preview below gets the room
        box.PlaceholderText = Menus.Text("SPIRE_MOD_MANAGER.IMPORT_HINT");
        column.AddChild(box);
        // The message (an error) and the preview share one area of a fixed height: a message takes its lines from the
        // preview, so the dialog keeps its size (and place) as one comes and goes.
        var area = new VBoxContainer { Name = "Below", CustomMinimumSize = new Vector2(0, PreviewArea), SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        area.AddThemeConstantOverride("separation", column.GetThemeConstant("separation"));
        column.AddChild(area);
        var status = Status();
        area.AddChild(status);
        var preview = Body("Preview");
        (preview.FitContent, preview.ScrollActive, preview.CustomMinimumSize) = (false, true, new Vector2(0, 150));
        preview.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        preview.MouseFilter = Control.MouseFilterEnum.Stop; // its links, and the wheel
        preview.MetaUnderlined = false; // our links: their colour and the pointing hand
        preview.MetaClicked += meta =>
        {
            if (meta.AsString() is var link && ProfileCsv.IsWorkshopLink(link))
                OS.ShellOpen(link);
        };
        area.AddChild(preview);

        // The file loaded and its text: the profile is named after the file while the text is still that.
        (string? file, string? loaded) = (null, null);
        ProfileCsv.Result? read = null;
        Button? subscribe = null;
        void Update()
        {
            if (!GodotObject.IsInstanceValid(box))
                return;
            read = box.Text.Trim().Length == 0 ? null : ProfileCsv.Read(box.Text);
            if (box.Text != loaded)
                file = null;
            Say(status, read?.Error ?? "", error: true);
            var plan = read is { Error: null } ? Profiles.PlanImport(read.Mods) : null;
            preview.Text = plan != null && read != null ? Preview(read.Mods, plan, Profiles.ImportName(file)) : "";
            // Subscribe to the missing mods on the Workshop that aren't on their way: there while there are any, with Steam.
            if (subscribe != null)
            {
                var ready = plan != null ? Subscribable(plan).Count : 0;
                subscribe.Visible = ready > 0 && Subscriptions.Available;
                subscribe.Text = Menus.Format(ready == 1 ? "SPIRE_MOD_MANAGER.SUBSCRIBE_MISSING_ONE" : "SPIRE_MOD_MANAGER.SUBSCRIBE_MISSING", ("count", Menus.Number(ready)));
            }
        }
        box.TextChanged += Update;
        // The preview follows each subscription and each mod the game finds (it leaves Not installed), until it closes.
        Subscriptions.Changed += Update;
        SpireModManager.Installed.Added += Update;
        column.TreeExiting += () =>
        {
            Subscriptions.Changed -= Update;
            SpireModManager.Installed.Added -= Update;
        };

        var buttons = Buttons();
        AddButton(buttons, "SPIRE_MOD_MANAGER.LOAD_LIST", () => Browse(column, save: false, "", path =>
        {
            try
            {
                box.Text = File.ReadAllText(path); // a UTF-8 BOM is taken off here
                (file, loaded) = (path, box.Text); // as the box has it (its line ends)
                Update();
            }
            catch (Exception e)
            {
                Say(status, Menus.Format("SPIRE_MOD_MANAGER.LOAD_FAILED", ("error", e.Message)), error: true);
            }
        }), "LoadCsvButton");
        subscribe = AddButton(buttons, "SPIRE_MOD_MANAGER.SUBSCRIBE", () =>
        {
            if (read is { Error: null } ok)
                Subscriptions.Subscribe(Subscribable(Profiles.PlanImport(ok.Mods)));
        }, "SubscribeMissingButton");
        Hover.Tip(subscribe, Menus.Text("SPIRE_MOD_MANAGER.SUBSCRIBE_TIP"));
        subscribe.Visible = false;
        Spacer(buttons);
        AddButton(buttons, "SPIRE_MOD_MANAGER.CANCEL", () => Close(), "CancelImportButton");
        AddButton(buttons, "SPIRE_MOD_MANAGER.CREATE_PROFILE", () =>
        {
            Update();
            if (read is not { Error: null } ok)
            {
                if (read == null)
                    Say(status, Menus.Text("SPIRE_MOD_MANAGER.IMPORT_EMPTY"), error: true);
                return;
            }
            var name = Profiles.ImportName(file);
            Close();
            Profiles.Import(name, Profiles.PlanImport(ok.Mods), screen);
        }, "CreateProfileButton");
        column.AddChild(buttons);
        box.GrabFocus();
    }

#if DEVTOOLS
    // Self-test: the preview's BBCode for a list read.
    public static string PreviewFor(IReadOnlyList<ProfileCsv.Entry> listed, string name) => Preview(listed, Profiles.PlanImport(listed), name);
#endif

    // The preview: the new profile's name, what the list holds, what it changes compared with now (only the counts that
    // aren't 0, or "None"), and the listed mods that aren't installed.
    static string Preview(IReadOnlyList<ProfileCsv.Entry> listed, Profiles.ImportPlan plan, string name)
    {
        var lines = new List<string> { Menus.Format("SPIRE_MOD_MANAGER.IMPORT_NEW_NAME", ("name", $"[color={GoldHex}]{Escape(name)}[/color]")) };
        string Enabled(int count) => Menus.Format(count == 1 ? "SPIRE_MOD_MANAGER.IMPORT_ENABLED_COUNT_ONE" : "SPIRE_MOD_MANAGER.IMPORT_ENABLED_COUNT", ("count", Menus.Number(count)));
        string Disabled(int count) => Menus.Format(count == 1 ? "SPIRE_MOD_MANAGER.IMPORT_DISABLED_COUNT_ONE" : "SPIRE_MOD_MANAGER.IMPORT_DISABLED_COUNT", ("count", Menus.Number(count)));
        var mods = listed.DistinctBy(m => (m.Id, m.WorkshopLink)).ToList(); // each copy listed once, as Changes counts them
        var (enabled, disabled) = (mods.Count(m => m.Enabled), mods.Count(m => !m.Enabled));
        lines.Add("  •  " + (disabled > 0
            ? Menus.Format("SPIRE_MOD_MANAGER.IMPORT_CONTAINS_BOTH", ("enabled", Enabled(enabled)), ("disabled", Disabled(disabled)))
            : Menus.Format("SPIRE_MOD_MANAGER.IMPORT_CONTAINS", ("enabled", Enabled(enabled)))));
        lines.Add("");
        lines.Add(Menus.Text("SPIRE_MOD_MANAGER.IMPORT_CHANGES"));
        if (plan.On > 0)
            lines.Add("  •  " + Menus.Format("SPIRE_MOD_MANAGER.IMPORT_ON", ("count", Menus.Number(plan.On))));
        if (plan.Off > 0)
            lines.Add("  •  " + Menus.Format("SPIRE_MOD_MANAGER.IMPORT_OFF", ("count", Menus.Number(plan.Off))));
        var extras = plan.Extras;
        if (extras.Sorted.Count > 0)
            lines.Add("  •  " + Menus.Format(extras.Sorted.Count == 1 ? "SPIRE_MOD_MANAGER.IMPORT_SORTED_ONE" : "SPIRE_MOD_MANAGER.IMPORT_SORTED",
                ("count", Menus.Number(extras.Sorted.Count))));
        if (extras.NewCategories.Count > 0)
            lines.Add("  •  " + Menus.Format("SPIRE_MOD_MANAGER.IMPORT_NEW_CATEGORIES", ("count", Menus.Number(extras.NewCategories.Count))));
        if (extras.Details > 0)
            lines.Add("  •  " + Menus.Format("SPIRE_MOD_MANAGER.IMPORT_DETAILS", ("count", Menus.Number(extras.Details))));
        if (plan.On == 0 && plan.Off == 0 && extras.Sorted.Count == 0 && extras.Details == 0)
            lines.Add("  •  " + Menus.Text("SPIRE_MOD_MANAGER.IMPORT_NO_CHANGE"));
        if (plan.BothCopies.Count > 0)
        {
            lines.Add("");
            lines.Add(Menus.Text("SPIRE_MOD_MANAGER.IMPORT_BOTH_COPIES"));
            lines.AddRange(plan.BothCopies.Select(m => $"  •  [b]{Escape(m.Name.Length > 0 ? m.Name : m.Id)}[/b]"));
        }
        if (plan.Missing.Count > 0)
        {
            lines.Add("");
            lines.Add(Menus.Format("SPIRE_MOD_MANAGER.IMPORT_MISSING", ("count", Menus.Number(plan.Missing.Count))));
            var off = Escape(Menus.Text("SPIRE_MOD_MANAGER.IMPORT_MISSING_OFF"));
            // The name itself opens its Workshop page, when the list gives one (with the external-link mark, in the
            // link); two copies of one mod say which item.
            string Item(ProfileCsv.Entry m) => plan.Missing.Count(o => o.Id == m.Id) > 1 && m.WorkshopLink.Length > 0
                ? $" ({m.WorkshopLink[(m.WorkshopLink.LastIndexOf('=') + 1)..]})" : "";
            string Named(ProfileCsv.Entry m) => $"[b]{Escape((m.Name.Length > 0 ? m.Name : m.Id) + Item(m))}[/b]";
            lines.AddRange(plan.Missing.Select(m => "  •  "
                + (m.WorkshopLink.Length > 0 ? $"[url={m.WorkshopLink}][color={LinkHex}]{Named(m)}{LinkMark.Tag(BodySize, LinkHex)}[/color][/url]" : Named(m))
                + (m.Enabled ? "" : $" [color={DimHex}]{off}[/color]") + Progress(m)));
        }
        return string.Join("\n", lines);
    }

    // A missing mod's subscription, once one has started (Subscriptions): subscribing, its download, installed, or why it
    // failed. None before.
    static string Progress(ProfileCsv.Entry m)
    {
        if (ItemOf(m.WorkshopLink) is not { } item || Subscriptions.Of(item) is not { State: not Subscriptions.State.None } status)
            return "";
        var (text, color) = status.State switch
        {
            Subscriptions.State.Subscribing => (Menus.Text("SPIRE_MOD_MANAGER.SUBSCRIBE_SUBSCRIBING"), DimHex),
            Subscriptions.State.Downloading => (Menus.Format("SPIRE_MOD_MANAGER.SUBSCRIBE_DOWNLOADING", ("percent", MissingMods.Percent(status.Progress))), GoldHex),
            Subscriptions.State.Installed => (Menus.Text("SPIRE_MOD_MANAGER.SUBSCRIBE_INSTALLED"), "#99db8c"),
            _ => (status.Error ?? "", "#ff8070"),
        };
        return $"  [color={color}]{Escape(text)}[/color]";
    }

    // The Workshop item a link names, or null.
    static ulong? ItemOf(string link) => link.Length > 0 && ulong.TryParse(link[(link.LastIndexOf('=') + 1)..], out var item) ? item : null;

    // The listed mods not installed that Subscribe would start: on the Workshop, not on their way or done.
    static List<ulong> Subscribable(Profiles.ImportPlan plan) => [.. plan.Missing.Select(m => ItemOf(m.WorkshopLink)).OfType<ulong>().Distinct()
        .Where(item => Subscriptions.Of(item).State is Subscriptions.State.None or Subscriptions.State.Failed)];

    // A saved list's file name: spire-mods-<profile>-<yyyyMMddHHmmss>.txt, with characters a file name can't have as
    // "_" (the prefix also avoids Windows' device names, such as CON). Profiles.ImportName reads the name back out.
    public const string FilePrefix = "spire-mods-";

    public static string FileName(string profile, DateTime at)
    {
        var bad = Path.GetInvalidFileNameChars().Concat(['<', '>', ':', '"', '/', '\\', '|', '?', '*']).ToHashSet();
        var name = new string(profile.Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return $"{FilePrefix}{(name.Length > 0 ? name + "-" : "")}{at.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture)}.txt";
    }

    // Writes the text as UTF-8 with a BOM (so spreadsheets read accented names right), as Better Mod Menu does. The
    // error's message when it fails, or null.
    public static string? SaveTo(string path, string csv)
    {
        try
        {
            File.WriteAllText(path, csv, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            return null;
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.ModId}] Could not save {path}: {e.Message}");
            return e.Message;
        }
    }

    // The system's file dialog (Godot's own where there's none), starting in Documents: save a .txt, or open a .txt or
    // a .csv (Better Mod Menu's exports).
    static void Browse(Node parent, bool save, string fileName, Action<string> chosen)
    {
        var dialog = new FileDialog
        {
            Name = "FileDialog", UseNativeDialog = true, Access = FileDialog.AccessEnum.Filesystem,
            FileMode = save ? FileDialog.FileModeEnum.SaveFile : FileDialog.FileModeEnum.OpenFile,
            Title = Menus.Text(save ? "SPIRE_MOD_MANAGER.SAVE_LIST" : "SPIRE_MOD_MANAGER.LOAD_LIST"),
            Filters = save
                ? [$"*.txt ; {Menus.Text("SPIRE_MOD_MANAGER.TEXT_FILES")}"]
                : [$"*.txt, *.csv ; {Menus.Text("SPIRE_MOD_MANAGER.LIST_FILES")}"],
            CurrentDir = OS.GetSystemDir(OS.SystemDir.Documents),
        };
        if (save)
            dialog.CurrentFile = fileName;
        dialog.FileSelected += path =>
        {
            dialog.QueueFree();
            if (IsOpen)
                chosen(path);
        };
        dialog.Canceled += dialog.QueueFree;
        parent.AddChild(dialog);
        dialog.PopupCentered(new Vector2I(900, 600));
    }

    // The dialog's frame over the page, titled; returns the column to fill, or null if there's nowhere to show it.
    // Share profile's is wider, for its row of tick boxes.
    static VBoxContainer? Dialog(Node owner, string titleKey, float width = 820)
    {
        if (Modal.Open(owner, Name, width, 12) is not { } column)
            return null;
        column.AddChild(Modal.Title(titleKey));
        return column;
    }

    static RichTextLabel Body(string name)
    {
        var body = new RichTextLabel { Name = name, BbcodeEnabled = true, FitContent = true, ScrollActive = false, MouseFilter = Control.MouseFilterEnum.Ignore };
        Hover.Fonts(body, BodySize);
        return body;
    }

    // The CSV: no wrapping (a line per mod, scrolled sideways), selectable when read-only too.
    static TextEdit TextBox(string name, bool editable)
    {
        var box = new TextEdit { Name = name, Editable = editable, CustomMinimumSize = new Vector2(0, 280), MouseDefaultCursorShape = Control.CursorShape.Ibeam };
        box.AddThemeFontOverride("font", ModEntry.Font("res://themes/kreon_regular_shared.tres"));
        box.AddThemeFontSizeOverride("font_size", 15);
        box.AddThemeColorOverride("font_color", Cream);
        box.AddThemeColorOverride("font_readonly_color", Cream);
        box.AddThemeColorOverride("font_placeholder_color", new Color(Cream, 0.35f));
        var look = new StyleBoxFlat
        {
            BgColor = new Color(0.03f, 0.05f, 0.08f), BorderColor = new Color(Gold, 0.35f),
            ContentMarginLeft = 10, ContentMarginRight = 10, ContentMarginTop = 8, ContentMarginBottom = 8,
        };
        look.SetBorderWidthAll(1);
        look.SetCornerRadiusAll(4);
        foreach (var state in new[] { "normal", "focus", "read_only" })
            box.AddThemeStyleboxOverride(state, look);
        // Esc closes the dialog even from inside the box (Patches' Escape only sees keys nothing handled).
        box.GuiInput += e =>
        {
            if (e is InputEventKey { Keycode: Key.Escape, Pressed: true, Echo: false })
            {
                box.AcceptEvent();
                Callable.From(() => Close()).CallDeferred(); // not from inside the box's own input
            }
        };
        return box;
    }

    // One line under the box: a problem (red) or what happened (dim). Hidden when empty.
    // The import preview's area: the preview's 250 px and room for a two-line message above it.
    const float PreviewArea = 310;

    // A line saying what just happened or what's wrong. reserved: it always takes its one line, cut with "…" (the share
    // dialog's, right above its buttons), so a message coming and going moves nothing.
    const string ReservedMeta = "spire_mod_manager_reserved";

    static Label Status(bool reserved = false)
    {
        var status = new Label { Name = "Status", Visible = false, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        status.AddThemeFontOverride("font", ModEntry.Font("res://themes/kreon_regular_shared.tres"));
        status.AddThemeFontSizeOverride("font_size", 17);
        if (reserved)
        {
            (status.AutowrapMode, status.ClipText, status.TextOverrunBehavior) = (TextServer.AutowrapMode.Off, true, TextServer.OverrunBehavior.TrimEllipsis);
            status.SetMeta(ReservedMeta, true);
            status.Visible = true;
            status.Modulate = Colors.Transparent; // its line kept, nothing shown
            status.Text = " ";
        }
        return status;
    }

    static void Say(Label status, string text, bool error)
    {
        status.AddThemeColorOverride("font_color", error ? ErrorColor : new Color(Cream, 0.7f));
        if (status.HasMeta(ReservedMeta))
        {
            (status.Text, status.Modulate) = text.Length > 0 ? (text, Colors.White) : (" ", Colors.Transparent);
            return;
        }
        status.Text = text;
        status.Visible = text.Length > 0;
    }

    static HBoxContainer Buttons()
    {
        var buttons = new HBoxContainer { Name = "Buttons", Alignment = BoxContainer.AlignmentMode.End };
        buttons.AddThemeConstantOverride("separation", 8);
        return buttons;
    }

    static Button AddButton(HBoxContainer buttons, string key, Action action, string name)
    {
        var button = Menus.SmallButton(Menus.Text(key), action, name: name);
        button.AddThemeFontSizeOverride("font_size", 18);
        button.CustomMinimumSize = new Vector2(0, 34);
        buttons.AddChild(button);
        return button;
    }

    // One tick box: a small square (a gold ✓ when on) before its words, all one toggle.
    const float BoxSize = 18, BoxRaise = 4;
    // As heavy as the words beside it: a 2 px rim, and the ✓ thickened by an outline in its own colour (Kreon Bold
    // draws the ✓ no thicker).
    public const int BoxRim = 2, TickOutline = 1;
    // The ✓ a little above the middle: its point clear of the bottom rim, its tail free to rise past the top.
    public const float TickRaise = 2;

    static Button Option(string key, bool on, Action<bool> toggled, string name)
    {
        var button = new Button
        {
            Name = name, Text = Menus.Text(key), ToggleMode = true, ButtonPressed = on,
            FocusMode = Control.FocusModeEnum.None, MouseDefaultCursorShape = Control.CursorShape.PointingHand,
        };
        var room = new StyleBoxEmpty { ContentMarginLeft = BoxSize + 8 }; // the words after the square
        foreach (var state in new[] { "normal", "hover", "pressed", "hover_pressed", "focus", "disabled" })
            button.AddThemeStyleboxOverride(state, room);
        button.AddThemeFontOverride("font", ModEntry.Font("res://themes/kreon_regular_shared.tres"));
        button.AddThemeFontSizeOverride("font_size", 17);
        foreach (var color in new[] { "font_color", "font_pressed_color", "font_focus_color" })
            button.AddThemeColorOverride(color, new Color(Cream, 0.85f));
        foreach (var color in new[] { "font_hover_color", "font_hover_pressed_color" })
            button.AddThemeColorOverride(color, Colors.White);
        var box = new Panel { Name = "Box", MouseFilter = Control.MouseFilterEnum.Ignore, CustomMinimumSize = new Vector2(BoxSize, BoxSize) };
        var look = new StyleBoxFlat { BgColor = new Color(0.03f, 0.05f, 0.08f), BorderColor = new Color(Gold, 0.6f) };
        look.SetBorderWidthAll(BoxRim);
        look.SetCornerRadiusAll(3);
        box.AddThemeStyleboxOverride("panel", look);
        var tick = new Label
        {
            Name = "Tick", Text = "✓", Visible = on, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        tick.AddThemeFontOverride("font", ModEntry.Font("res://themes/kreon_regular_shared.tres")); // the face the menus draw their ✓ in
        tick.AddThemeFontSizeOverride("font_size", 15);
        tick.AddThemeColorOverride("font_color", Gold);
        tick.AddThemeConstantOverride("outline_size", TickOutline);
        tick.AddThemeColorOverride("font_outline_color", Gold);
        box.AddChild(tick);
        tick.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        (tick.OffsetTop, tick.OffsetBottom) = (-TickRaise, -TickRaise);
        button.AddChild(box);
        box.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.CenterLeft, Control.LayoutPresetMode.Minsize);
        (box.OffsetTop, box.OffsetBottom) = (box.OffsetTop - BoxRaise, box.OffsetBottom - BoxRaise); // level with the words: Kreon's letters sit high in their line
        button.Toggled += pressed =>
        {
            tick.Visible = pressed;
            toggled(pressed);
        };
        return button;
    }

    static void Spacer(HBoxContainer buttons) =>
        buttons.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore });

    static string Escape(string text) => text.Replace("[", "[lb]");

    // True when there was one to close.
    public static bool Close() => Modal.Close(Name);
}
