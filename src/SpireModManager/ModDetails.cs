using Godot;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.addons.mega_text;

namespace SpireModManager;

// The player's alias and notes for a mod, kept visibly apart from the author's own text:
// - rows show the alias with the original name small and dim beside it (not in Vanilla View);
// - the info panel titles with the alias, lists the original name, and adds a "Your notes" section
//   in its own colour, plus Edit Notes and Workshop Page buttons along the bottom.
static class ModDetails
{
    const string NotesColor = "#9fd8ff"; // player-written text: light blue, never the author's cream/gold
    static readonly Color Cream = new(1, 0.964706f, 0.886275f);
    static readonly Color Gold = new(0.937f, 0.784f, 0.318f);

    // After the game fills the panel for a mod.
    public static void Fill(NModInfoContainer container, Mod mod)
    {
        if (Store.Data.VanillaView) // the panel exactly as the game fills it
        {
            HideButtons(container);
            return;
        }
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
        if (info?.Notes is { } notes)
            description.Text += $"\n[color={NotesColor}][b]{Menus.Text("SPIRE_MOD_MANAGER.YOUR_NOTES")}[/b]\n{notes}[/color]\n";

        // The buttons are made once per panel and read the shown mod from the panel's metadata.
        container.SetMeta(ModIdMeta, id ?? "");
        container.SetMeta(ModNameMeta, name);
        container.SetMeta(WorkshopIdMeta, mod.workshopId?.ToString() ?? "");
        container.SetMeta(FolderMeta, mod.path);
        var edit = container.GetNodeOrNull<NButton>("EditNotesButton") ?? AddButton(container, "EditNotesButton",
            "SPIRE_MOD_MANAGER.EDIT_NOTES", 20, () => OpenEditor(container, Meta(container, ModIdMeta), Meta(container, ModNameMeta)));
        var workshop = container.GetNodeOrNull<NButton>("WorkshopPageButton") ?? AddButton(container, "WorkshopPageButton",
            "SPIRE_MOD_MANAGER.WORKSHOP_PAGE", 210,
            () => PlatformUtil.OpenUrl($"https://steamcommunity.com/sharedfiles/filedetails/?id={Meta(container, WorkshopIdMeta)}"));
        var folder = container.GetNodeOrNull<NButton>("OpenFolderButton") ?? AddButton(container, "OpenFolderButton",
            "SPIRE_MOD_MANAGER.OPEN_FOLDER", 400, () => OS.ShellOpen(Meta(container, FolderMeta)));
        edit.Visible = id != null;
        workshop.Visible = mod.workshopId != null;
        folder.Visible = Directory.Exists(mod.path);
    }

    // Where it's from, anything unusual about how it loaded, and its dependencies both ways.
    static string Facts(Mod mod)
    {
        string Line(string key, string value) => $"\n[gold]{Menus.Text(key)}[/gold]: {value}";
        var facts = Line("SPIRE_MOD_MANAGER.SOURCE", mod.workshopId is { } ws
            ? Menus.Format("SPIRE_MOD_MANAGER.SOURCE_WORKSHOP", ("id", ws.ToString()))
            : Menus.Text("SPIRE_MOD_MANAGER.SOURCE_LOCAL"));
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
        return facts + "\n";
    }

    public static void HideButtons(Node container)
    {
        foreach (var name in new[] { "EditNotesButton", "WorkshopPageButton", "OpenFolderButton" })
            if (container.GetNodeOrNull<Control>(name) is { } button)
                button.Visible = false;
    }

    const string ModIdMeta = "spire_mod_manager_mod_id";
    const string ModNameMeta = "spire_mod_manager_mod_name";
    const string WorkshopIdMeta = "spire_mod_manager_workshop_id";
    const string FolderMeta = "spire_mod_manager_folder";

    static string Meta(Node node, string key) => node.HasMeta(key) ? (string)node.GetMeta(key) : "";

    // Along the inside of the panel's bottom edge, whatever its size (Layout ends the description above them).
    const float ButtonsFromBottom = 80;

    static NButton AddButton(Control parent, string name, string locKey, float x, Action onPressed)
    {
        var button = ModEntry.AddButton(parent, locKey, new Vector2(180, 56), onPressed);
        button.Name = name;
        button.AnchorTop = button.AnchorBottom = 1;
        (button.OffsetLeft, button.OffsetRight, button.OffsetTop, button.OffsetBottom) =
            (x, x + 180, -ButtonsFromBottom, -ButtonsFromBottom + 56);
        return button;
    }

    // Alias and notes editor laid over the info panel. Save stores and refreshes; Cancel leaves things as they were.
    public static Control? OpenEditor(NModInfoContainer container, string modId, string originalName)
    {
        if (modId.Length == 0 || container.HasNode("NotesEditor"))
            return null;
        var info = Store.InfoFor(modId);
        var editor = new Panel { Name = "NotesEditor" };
        editor.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = new Color(0.0756f, 0.12084f, 0.18f, 0.98f) });
        container.AddChild(editor);
        editor.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        // The panel's own text draws over anything added to it, so hide it while editing.
        var covered = container.GetChildren().OfType<CanvasItem>().Where(c => c != editor && c.Visible).ToList();
        foreach (var item in covered)
            item.Visible = false;
        editor.TreeExiting += () =>
        {
            foreach (var item in covered.Where(GodotObject.IsInstanceValid))
                item.Visible = true;
        };

        var y = 24f;
        Label Caption(string key)
        {
            var label = new Label { Text = Menus.Text(key), Position = new Vector2(24, y) };
            Style(label, "res://themes/kreon_bold_shared.tres", 28, Gold);
            editor.AddChild(label);
            y += 44;
            return label;
        }

        Caption("SPIRE_MOD_MANAGER.ALIAS");
        var alias = new LineEdit
        {
            Name = "Alias", Text = info?.Alias ?? "", PlaceholderText = originalName, MaxLength = 80,
            Position = new Vector2(24, y), Size = new Vector2(container.Size.X - 48, 52),
        };
        Style(alias, "res://themes/kreon_regular_shared.tres", 26, Cream);
        editor.AddChild(alias);
        y += 76;

        Caption("SPIRE_MOD_MANAGER.TAGS");
        var tags = new LineEdit
        {
            Name = "Tags", Text = string.Join(", ", info?.Tags ?? []), PlaceholderText = Menus.Text("SPIRE_MOD_MANAGER.TAGS_HINT"),
            MaxLength = 200, Position = new Vector2(24, y), Size = new Vector2(container.Size.X - 48, 52),
        };
        Style(tags, "res://themes/kreon_regular_shared.tres", 24, Cream);
        editor.AddChild(tags);
        y += 76;

        Caption("SPIRE_MOD_MANAGER.YOUR_NOTES");
        var notes = new TextEdit
        {
            Name = "Notes", Text = info?.Notes ?? "", WrapMode = TextEdit.LineWrappingMode.Boundary,
            Position = new Vector2(24, y), Size = new Vector2(container.Size.X - 48, container.Size.Y - ButtonsFromBottom - y - 16),
        };
        Style(notes, "res://themes/kreon_regular_shared.tres", 24, new Color(NotesColor));
        editor.AddChild(notes);

        AddButton(editor, "SaveButton", "SPIRE_MOD_MANAGER.SAVE", 20, () =>
        {
            Save(modId, alias.Text, notes.Text, tags.Text);
            editor.QueueFree();
            Refresh(container, modId);
        });
        AddButton(editor, "CancelButton", "SPIRE_MOD_MANAGER.CANCEL", 210, editor.QueueFree);
        alias.GrabFocus();
        return editor;
    }

    // Empty text clears the alias, notes or tags. A mod with nothing left keeps its category.
    public static void Save(string modId, string alias, string notes, string tags = "")
    {
        if (!Store.Data.Mods.TryGetValue(modId, out var info))
            Store.Data.Mods[modId] = info = new ModInfo();
        info.Alias = alias.Trim() is { Length: > 0 } a ? a : null;
        info.Notes = notes.Trim() is { Length: > 0 } n ? n : null;
        info.Tags = Tags.Parse(tags);
        Store.Save();
    }

    // Row titles: the alias with the original name beside it, or just the name in Vanilla View.
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
        }
    }

    // Row titles and the panel, after an edit.
    static void Refresh(NModInfoContainer container, string modId)
    {
        if (container.GetParent()?.GetNodeOrNull<Control>("%ModsScrollContainer/Mask/Content") is not { } content)
            return;
        CategoryList.Apply(content);
        if (content.GetChildren().OfType<NModMenuRow>().FirstOrDefault(r => r.Mod?.manifest?.id == modId)?.Mod is { } mod)
            container.Fill(mod);
    }

    static string Escape(string text) => text.Replace("[", "[lb]");

    static void Style(Control control, string font, int size, Color color)
    {
        control.AddThemeFontOverride("font", ResourceLoader.Load<Font>(font));
        control.AddThemeFontSizeOverride("font_size", size);
        control.AddThemeColorOverride("font_color", color);
    }
}
