using System.Text;
using System.Text.RegularExpressions;
using Godot;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;

namespace SpireModManager;

// A mod list as CSV text, in Better Mod Menu's columns (its mod list export), so a list moves between the two
// managers: Mod Id, Name, Version, Enabled (TRUE/FALSE), Group, Workshop Link, Alias, Notes.
// - Writing: RFC 4180 (a value with a comma, quote or line break in quotes, quotes doubled), CRLF line ends. A value
//   a spreadsheet would run as a formula (starting with = + - @, a tab or a CR) gets a ' in front, as Better Mod
//   Menu writes it: spreadsheets show it as text, and reading takes it off again.
// - Reading: the same, forgiving: a UTF-8 BOM, CRLF or LF, blank lines, a ; or tab between values (a spreadsheet
//   saved in a language that uses the comma for decimals). Columns go by their header, in any order and any case;
//   only Mod Id (or Id) and Enabled are needed. Enabled: TRUE/FALSE, 1/0 or yes/no, in any case.
static class ProfileCsv
{
    public static readonly string[] Header = ["Mod Id", "Name", "Version", "Enabled", "Group", "Workshop Link", "Alias", "Notes"];

    // One mod to write. Alias and notes are always written empty (see Profiles.ShareRows).
    public record Row(string Id, string Name, string Version, bool Enabled, string Group, string WorkshopLink);

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

    public static string Write(IEnumerable<Row> rows)
    {
        var text = new StringBuilder();
        Line(text, Header);
        foreach (var row in rows)
            Line(text, [row.Id, row.Name, row.Version, row.Enabled ? "TRUE" : "FALSE", row.Group, row.WorkshopLink, "", ""]);
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
        var start = text.StartsWith('﻿') ? 1 : 0;
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

    // The character between values: a comma, unless the first line has more semicolons or tabs than commas.
    static char Separator(string text, int start)
    {
        var end = text.IndexOfAny(['\r', '\n'], start);
        var first = end < 0 ? text[start..] : text[start..end];
        return new[] { ',', ';', '\t' }.MaxBy(c => first.Count(x => x == c) + (c == ',' ? 1 : 0)); // a tie: the comma
    }

    public static Result Read(string text)
    {
        static Result Problem(string error) => new([], error);
        if (text.Trim('﻿', ' ', '\t', '\r', '\n').Length == 0)
            return Problem(Menus.Text("SPIRE_MOD_MANAGER.IMPORT_EMPTY"));
        var records = Records(text);
        if (records.Count == 0) // only empty values
            return Problem(Menus.Text("SPIRE_MOD_MANAGER.IMPORT_EMPTY"));
        var header = records[0].Select(h => h.Trim()).ToList();
        int Column(params string[] names) => header.FindIndex(h => names.Any(n => string.Equals(h, n, StringComparison.OrdinalIgnoreCase)));
        var (id, enabled, name, link) = (Column("Mod Id", "Id", "ModId"), Column("Enabled"), Column("Name"), Column("Workshop Link"));
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
                return Problem(Menus.Format("SPIRE_MOD_MANAGER.IMPORT_BAD_ENABLED", ("row", Menus.Number(r + 1)), ("value", Cell(enabled))));
            mods.Add(new Entry(modId, on, Cell(name), CleanLink(Cell(link))));
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
// - Share profile: the active profile's list in a read-only box (select any of it), Copy (the button says "Copied" for
//   a moment), Save CSV… (the system's save dialog, named after the profile) and Close.
// - Import profile: a box to paste into and Load CSV… (the system's open dialog). As the text changes, it says what
//   the list would do compared with now and names the listed mods that aren't installed, each with its Workshop page
//   (opened in the browser). A problem with the text shows in red. Create profile makes a new profile from it (named
//   after the file, or "Imported profile") and switches to it: one undo step.
static class ProfileShare
{
    public const string Name = "SpireModManagerShare";
    static readonly Color Cream = new(1, 0.964706f, 0.886275f);
    static readonly Color Gold = new(0.937f, 0.784f, 0.318f);
    static readonly Color ErrorColor = new(1f, 0.5f, 0.44f);
    const string GoldHex = "#efc851", DimHex = "#fff6e299";

    public static bool IsOpen => Root()?.GetNodeOrNull(Name) != null;

    public static void ShowShare(NModdingScreen screen)
    {
        if (Profiles.Active is not { } profile || Dialog(screen, "SPIRE_MOD_MANAGER.SHARE_PROFILE") is not { } column)
            return;
        var csv = ProfileCsv.Write(Profiles.ShareRows(profile));

        var body = Body("Body");
        body.Text = Menus.Format("SPIRE_MOD_MANAGER.SHARE_BODY", ("name", $"[color={GoldHex}]{Escape(profile.Name)}[/color]"));
        column.AddChild(body);
        var box = TextBox("CsvText", editable: false);
        box.Text = csv;
        column.AddChild(box);
        var status = Status();
        column.AddChild(status);

        var buttons = Buttons();
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
        AddButton(buttons, "SPIRE_MOD_MANAGER.SAVE_CSV", () => Browse(column, save: true, FileName(profile.Name), path =>
        {
            var error = SaveTo(path, csv);
            Say(status, error == null ? Menus.Format("SPIRE_MOD_MANAGER.SAVED_AS", ("file", Path.GetFileName(path)))
                : Menus.Format("SPIRE_MOD_MANAGER.SAVE_FAILED", ("error", error)), error != null);
        }), "SaveCsvButton");
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
        box.PlaceholderText = Menus.Text("SPIRE_MOD_MANAGER.IMPORT_HINT");
        column.AddChild(box);
        var status = Status();
        column.AddChild(status);
        var preview = Body("Preview");
        (preview.FitContent, preview.ScrollActive, preview.CustomMinimumSize) = (false, true, new Vector2(0, 170));
        preview.MouseFilter = Control.MouseFilterEnum.Stop; // its links, and the wheel
        preview.MetaClicked += meta =>
        {
            if (meta.AsString() is var link && ProfileCsv.IsWorkshopLink(link))
                OS.ShellOpen(link);
        };
        column.AddChild(preview);

        string? file = null; // the file loaded, for the profile's name
        ProfileCsv.Result? read = null;
        void Update()
        {
            read = box.Text.Trim().Length == 0 ? null : ProfileCsv.Read(box.Text);
            if (box.Text.Trim().Length == 0)
                file = null;
            Say(status, read?.Error ?? "", error: true);
            preview.Text = read is { Error: null } ? Preview(Profiles.PlanImport(read.Mods), Profiles.ImportName(file)) : "";
        }
        box.TextChanged += Update;

        var buttons = Buttons();
        AddButton(buttons, "SPIRE_MOD_MANAGER.LOAD_CSV", () => Browse(column, save: false, "", path =>
        {
            try
            {
                box.Text = File.ReadAllText(path); // a UTF-8 BOM is taken off here
                file = path;
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
            var page = Escape(Menus.Text("SPIRE_MOD_MANAGER.WORKSHOP_PAGE"));
            var off = Escape(Menus.Text("SPIRE_MOD_MANAGER.IMPORT_MISSING_OFF"));
            lines.AddRange(plan.Missing.Select(m => $"  •  [b]{Escape(m.Name.Length > 0 ? m.Name : m.Id)}[/b]"
                + (m.Enabled ? "" : $" [color={DimHex}]{off}[/color]")
                + (m.WorkshopLink.Length > 0 ? $"  [url={m.WorkshopLink}]{page}[/url]" : "")));
        }
        return string.Join("\n", lines);
    }

    // A profile's name as a file name: characters a file name can't have become "_".
    public static string FileName(string profile)
    {
        var bad = Path.GetInvalidFileNameChars().Concat(['<', '>', ':', '"', '/', '\\', '|', '?', '*']).ToHashSet();
        var name = new string(profile.Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return (name.Length > 0 ? name : Menus.Text("SPIRE_MOD_MANAGER.IMPORTED_PROFILE")) + ".csv";
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

    // The system's file dialog (Godot's own where there's none): save or open a .csv, starting in Documents.
    static void Browse(Node parent, bool save, string fileName, Action<string> chosen)
    {
        var dialog = new FileDialog
        {
            Name = "FileDialog", UseNativeDialog = true, Access = FileDialog.AccessEnum.Filesystem,
            FileMode = save ? FileDialog.FileModeEnum.SaveFile : FileDialog.FileModeEnum.OpenFile,
            Title = Menus.Text(save ? "SPIRE_MOD_MANAGER.SAVE_CSV" : "SPIRE_MOD_MANAGER.LOAD_CSV"),
            Filters = [$"*.csv ; {Menus.Text("SPIRE_MOD_MANAGER.CSV_FILES")}"],
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

    // The dialog's frame over the page, its title in; returns the column to fill. Null if there's nowhere to show it.
    static VBoxContainer? Dialog(Node owner, string titleKey)
    {
        Close();
        RelaunchCheck.Close();
        if (Root() is not { } root)
            return null;
        var layer = new CanvasLayer { Name = Name, Layer = 85 };
        root.AddChild(layer);
        owner.TreeExiting += () => Close();

        // Dim everything behind, and take its clicks.
        var size = owner.GetViewport().GetVisibleRect().Size; // anchors don't size controls right under a CanvasLayer here
        layer.AddChild(new ColorRect { Color = new Color(0, 0, 0, 0.6f), MouseFilter = Control.MouseFilterEnum.Stop, Size = size });
        var center = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore, Size = size };
        layer.AddChild(center);
        var panel = new PanelContainer { CustomMinimumSize = new Vector2(820, 0) };
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.05f, 0.08f, 0.12f, 0.98f), BorderColor = new Color(Gold, 0.5f),
            ContentMarginLeft = 26, ContentMarginRight = 26, ContentMarginTop = 20, ContentMarginBottom = 20,
            ShadowColor = new Color(0, 0, 0, 0.4f), ShadowSize = 10,
        };
        style.SetBorderWidthAll(1);
        style.SetCornerRadiusAll(8);
        panel.AddThemeStyleboxOverride("panel", style);
        center.AddChild(panel);
        var column = new VBoxContainer { Name = "Column" };
        column.AddThemeConstantOverride("separation", 12);
        panel.AddChild(column);

        var title = new Label { Name = "Title", Text = Menus.Text(titleKey) };
        title.AddThemeFontOverride("font", ModEntry.Font("res://themes/kreon_bold_shared.tres"));
        title.AddThemeFontSizeOverride("font_size", 26);
        title.AddThemeColorOverride("font_color", Cream);
        column.AddChild(title);
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

    static void Spacer(HBoxContainer buttons) =>
        buttons.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore });

    static string Escape(string text) => text.Replace("[", "[lb]");

    // True when there was one to close.
    public static bool Close()
    {
        if (Root()?.GetNodeOrNull(Name) is not { } layer)
            return false;
        layer.GetParent().RemoveChild(layer);
        layer.QueueFree();
        return true;
    }

    static Node? Root() => (Engine.GetMainLoop() as SceneTree)?.Root;
}
