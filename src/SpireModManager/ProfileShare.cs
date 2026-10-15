using System.Text;
using System.Text.RegularExpressions;
using Godot;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;

namespace SpireModManager;

// A mod list as CSV text, in the order of Better Mod Menu's columns (its mod list export): Mod Id, Name, Version,
// Enabled (TRUE/FALSE), then the ones the player picks (Options): Category, Workshop Id, Alias, Notes. The Workshop
// column holds just the id (Jordan, 2026-10-15: Steam may serve an item's page at another address); reading takes
// Better Mod Menu's Workshop Link too.
// - Writing: RFC 4180 (a value with a comma, quote or line break in quotes, quotes doubled), CRLF line ends. A value
//   a spreadsheet would run as a formula (starting with = + - @, a tab or a CR) gets a ' in front, as Better Mod
//   Menu writes it: spreadsheets show it as text, and reading takes it off again.
// - Reading: the same, forgiving: a UTF-8 BOM, CRLF or LF, blank lines, a ; or tab between values (a spreadsheet
//   saved in a language that uses the comma for decimals). Columns go by their header, in any order and any case;
//   only Mod Id (or Id) and Enabled are needed. Enabled: TRUE/FALSE, 1/0 or yes/no, in any case.
static class ProfileCsv
{
    // What a shared list holds besides each mod's id, name, version and Enabled (Jordan, 2026-10-15: the category on, the
    // rest off at first). Disabled: rows for the mods that are off too; without it they're left out.
    public record Options(bool Category = true, bool Disabled = false, bool Alias = false, bool Notes = false);

    public static string[] Header(Options options) =>
    [
        "Mod Id", "Name", "Version", "Enabled", .. options.Category ? ["Category"] : Array.Empty<string>(), "Workshop Id",
        .. options.Alias ? ["Alias"] : Array.Empty<string>(), .. options.Notes ? ["Notes"] : Array.Empty<string>(),
    ];

    // One mod to write. WorkshopId: "" for a mod that isn't from the Workshop.
    public record Row(string Id, string Name, string Version, bool Enabled, string Category, string WorkshopId, string Alias = "", string Notes = "");

    // One mod read: the parts an import uses.
    public record Entry(string Id, bool Enabled, string Name, string WorkshopLink);

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
        return text.ToString();
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
            if (Bool(Cell(enabled)) is not { } on)
                return Problem(Menus.Format("SPIRE_MOD_MANAGER.IMPORT_BAD_ENABLED", ("mod", modId), ("value", Cell(enabled))));
            mods.Add(new Entry(modId, on, Cell(name), WorkshopFrom(Cell(link))));
        }
        return mods.Count == 0 ? Problem(Menus.Text("SPIRE_MOD_MANAGER.IMPORT_NO_MODS")) : new Result(mods, null);
    }

    static bool? Bool(string value) => value.ToLowerInvariant() switch
    {
        "true" or "1" or "yes" => true,
        "false" or "0" or "no" => false,
        _ => null,
    };
}

// Sharing a profile as text, and making one from shared text (ProfileCsv), from the profile list or the picker's
// right-click menu. Two dialogs over the page, like the relaunch check's; Close (or Cancel) and Esc close them.
// - Share profile: a row of tick boxes for what the list holds (ProfileCsv.Options: Include category, disabled, alias,
//   notes; kept for the session, or across launches with Remember selection at the row's end), the active profile's
//   list in a read-only box (select any of it), Save (the system's save dialog: spire-mods-<profile>-<time>.txt, a .txt
//   as some players are wary of .csv; the text is the same), Copy (the button says "Copied" for a moment) and Close.
// - Import profile: a box to paste into and Load file (the system's open dialog, .txt or .csv). As the text changes,
//   it says what the list would do compared with now and names the listed mods that aren't installed, each with its Workshop page
//   (opened in the browser). A problem with the text shows in red. Create profile makes a new profile from it (named
//   after the file, or "Imported profile") and switches to it: one undo step.
static class ProfileShare
{
    public const string Name = "SpireModManagerShare";
    static readonly Color Cream = new(1, 0.964706f, 0.886275f);
    static readonly Color Gold = new(0.937f, 0.784f, 0.318f);
    static readonly Color ErrorColor = new(1f, 0.5f, 0.44f);
    const string GoldHex = "#efc851", DimHex = "#fff6e299", LinkHex = "#99ccff";

    public static bool IsOpen => Modal.IsOpen(Name);

    // The share dialog's tick boxes: as last left this session, or else as kept from an earlier one (Remember selection,
    // Store.Data.ShareChoices), or else the defaults.
    static ProfileCsv.Options? _options;

    static ProfileCsv.Options Choices => _options ??= Store.Data.ShareChoices is { } kept
        ? new ProfileCsv.Options(kept.Category, kept.Disabled, kept.Alias, kept.Notes) : new ProfileCsv.Options();

    // The self-test: the tick boxes as a new launch finds them.
    public static void ForgetSessionChoices() => _options = null;

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
        // What the list holds, in a row under the words (each tick rewrites it), and at the row's right end Remember
        // selection (Jordan, 2026-10-15).
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
        var status = Status();
        column.AddChild(status);

        var buttons = Buttons();
        // Save, then Copy (Jordan, 2026-10-15).
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
            copy!.Text = Menus.Text("SPIRE_MOD_MANAGER.COPIED");
            copy.GetTree().CreateTimer(1.5).Timeout += () =>
            {
                if (GodotObject.IsInstanceValid(copy))
                    copy.Text = Menus.Text("SPIRE_MOD_MANAGER.COPY");
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
        var status = Status();
        column.AddChild(status);
        var preview = Body("Preview");
        (preview.FitContent, preview.ScrollActive, preview.CustomMinimumSize) = (false, true, new Vector2(0, 250));
        preview.MouseFilter = Control.MouseFilterEnum.Stop; // its links, and the wheel
        preview.MetaClicked += meta =>
        {
            if (meta.AsString() is var link && ProfileCsv.IsWorkshopLink(link))
                OS.ShellOpen(link);
        };
        column.AddChild(preview);

        // The file loaded and its text: the profile is named after the file while the text is still that.
        (string? file, string? loaded) = (null, null);
        ProfileCsv.Result? read = null;
        void Update()
        {
            read = box.Text.Trim().Length == 0 ? null : ProfileCsv.Read(box.Text);
            if (box.Text != loaded)
                file = null;
            Say(status, read?.Error ?? "", error: true);
            preview.Text = read is { Error: null } ? Preview(Profiles.PlanImport(read.Mods), Profiles.ImportName(file)) : "";
        }
        box.TextChanged += Update;

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

    // The preview: the new profile's name, what it changes (a line for each count that isn't 0), and the mods it names
    // that aren't installed.
    static string Preview(Profiles.ImportPlan plan, string name)
    {
        var lines = new List<string> { Menus.Format("SPIRE_MOD_MANAGER.IMPORT_NEW_NAME", ("name", $"[color={GoldHex}]{Escape(name)}[/color]")) };
        void Count(int count, string one, string many)
        {
            if (count > 0)
                lines.Add("  •  " + Menus.Format(count == 1 ? one : many, ("count", Menus.Number(count))));
        }
        Count(plan.On, "SPIRE_MOD_MANAGER.IMPORT_ON_ONE", "SPIRE_MOD_MANAGER.IMPORT_ON");
        Count(plan.Off, "SPIRE_MOD_MANAGER.IMPORT_OFF_ONE", "SPIRE_MOD_MANAGER.IMPORT_OFF");
        Count(plan.Same, "SPIRE_MOD_MANAGER.IMPORT_SAME_ONE", "SPIRE_MOD_MANAGER.IMPORT_SAME");
        Count(plan.Unlisted, "SPIRE_MOD_MANAGER.IMPORT_UNLISTED_ONE", "SPIRE_MOD_MANAGER.IMPORT_UNLISTED");
        if (plan.Missing.Count > 0)
        {
            lines.Add("");
            lines.Add(Menus.Format("SPIRE_MOD_MANAGER.IMPORT_MISSING", ("count", Menus.Number(plan.Missing.Count))));
            var off = Escape(Menus.Text("SPIRE_MOD_MANAGER.IMPORT_MISSING_OFF"));
            // The name itself opens its Workshop page, when the list gives one (Jordan, 2026-10-15): a few steps to find it.
            string Named(ProfileCsv.Entry m) => $"[b]{Escape(m.Name.Length > 0 ? m.Name : m.Id)}[/b]";
            lines.AddRange(plan.Missing.Select(m => "  •  " + (m.WorkshopLink.Length > 0 ? $"[url={m.WorkshopLink}][color={LinkHex}]{Named(m)}[/color][/url]" : Named(m))
                + (m.Enabled ? "" : $" [color={DimHex}]{off}[/color]")));
        }
        return string.Join("\n", lines);
    }

    // A saved list's file name (Jordan, 2026-10-15): spire-mods-<profile>-<yyyyMMddHHmmss>.txt, the profile's name with
    // the characters a file name can't have as "_". (The prefix also keeps it clear of the names Windows keeps for
    // devices, such as CON.) Profiles.ImportName reads the profile's name back out of it.
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

    // The dialog's frame over the page (any other dialog closes), its title in; returns the column to fill. Null if
    // there's nowhere to show it. width: Share profile's is wider, for its row of tick boxes on one line.
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
        Hover.Fonts(body, 18);
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
    static Label Status()
    {
        var status = new Label { Name = "Status", Visible = false, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        status.AddThemeFontOverride("font", ModEntry.Font("res://themes/kreon_regular_shared.tres"));
        status.AddThemeFontSizeOverride("font_size", 17);
        return status;
    }

    static void Say(Label status, string text, bool error)
    {
        status.Text = text;
        status.Visible = text.Length > 0;
        status.AddThemeColorOverride("font_color", error ? ErrorColor : new Color(Cream, 0.7f));
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

    // One tick box: a small square (a gold ✓ in it when on) before its words, the whole of it one toggle. toggled: the
    // new state.
    const float BoxSize = 18, BoxRaise = 4;

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
        look.SetBorderWidthAll(1);
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
        box.AddChild(tick);
        tick.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
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
