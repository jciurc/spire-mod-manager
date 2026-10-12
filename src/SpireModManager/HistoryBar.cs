using System.Text.Json.Nodes;
using Godot;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;

namespace SpireModManager;

// Along the bottom right, before Relaunch Game:
// - Two lines: "Mods enabled 23 (+1 -3)", how many mods are on and how many were turned on and off since
//   launch; then "Changes 5  ↕2": everything else that changed (mods installed since launch, edited mods,
//   profiles), then the load-order moves. Hovering shows them apart (naming the mods when there are few);
//   clicking opens (or closes) the full list in the right panel: switched profile, each mod enabled or
//   disabled, each mod installed (and whether it loads after a relaunch), each move, each edit, profiles
//   added or removed. A mod's name there opens it, with Back to the list.
// - Undo, Redo (their shortcuts underneath), Revert all.
static class HistoryBar
{
    public const string BarName = "SpireModManagerHistory";
    public const string ChangesName = "SpireModManagerChanges";
    const string SummaryName = "SpireModManagerChangesPanel";
    public const string ViewName = "SpireModManagerChangesView";
    public const string BackName = "SpireModManagerBackToChanges";
    const string HiddenMeta = "spire_mod_manager_hidden_by_changes";
    static readonly Color Cream = new(1, 0.964706f, 0.886275f);
    const int NameLimit = 20; // the hover summary names mods turned on, off or installed when there are fewer
    const string Added = "#a6e3a1", Removed = "#f2a3a3", Moved = "#8ec5ff", Edited = "#e8cf7a", New = "#7fd6c8", Dim = "#fff6e280";

    // The screen our keyboard shortcuts act on, while it exists.
    public static NModdingScreen? Screen { get; private set; }

    public static void Attach(NModdingScreen screen)
    {
        var content = screen.GetNode<Control>("%ModsScrollContainer/Mask/Content");
        var info = screen.GetNode<NModInfoContainer>("%ModInfoContainer");

        var bar = new HBoxContainer { Name = BarName, Alignment = BoxContainer.AlignmentMode.End, Size = new Vector2(300, 70) };
        bar.AddThemeConstantOverride("separation", 6);
        screen.AddChild(bar);
        var undo = Menus.SmallButton(Menus.Text("SPIRE_MOD_MANAGER.UNDO"), History.Undo, name: "UndoButton");
        var redo = Menus.SmallButton(Menus.Text("SPIRE_MOD_MANAGER.REDO"), History.Redo, name: "RedoButton");
        var revert = Menus.SmallButton(Menus.Text("SPIRE_MOD_MANAGER.REVERT_ALL"), History.RevertMods, name: "RevertAllButton");
        // Each button with its shortcut (if any) in small text underneath.
        foreach (var (button, keys) in new[] { (undo, "SPIRE_MOD_MANAGER.UNDO_KEYS"), (redo, "SPIRE_MOD_MANAGER.REDO_KEYS"), (revert, null) })
        {
            button.AddThemeFontSizeOverride("font_size", 17);
            var column = new VBoxContainer { SizeFlagsVertical = Control.SizeFlags.ShrinkCenter, MouseFilter = Control.MouseFilterEnum.Ignore };
            column.AddThemeConstantOverride("separation", 0);
            column.AddChild(button);
            var hint = new Label { Text = keys == null ? " " : Menus.Text(keys), HorizontalAlignment = HorizontalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore };
            Style(hint, 13, new Color(Cream, 0.5f));
            column.AddChild(hint);
            bar.AddChild(column);
        }

        var changes = new RichTextLabel
        {
            Name = ChangesName, BbcodeEnabled = true, FitContent = true, AutowrapMode = TextServer.AutowrapMode.Off,
            ScrollActive = false, Size = new Vector2(320, 50), MouseFilter = Control.MouseFilterEnum.Stop,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
        };
        Hover.Fonts(changes, 17);
        changes.AddThemeConstantOverride("line_separation", -2);
        screen.AddChild(changes);
        changes.MouseEntered += () => ShowSummary(screen, changes);
        changes.MouseExited += () => Hover.Hide(screen, SummaryName);
        changes.GuiInput += e =>
        {
            if (e is not InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false })
                return;
            // A second click closes the list again; with Back showing (a mod opened from the list), it goes back.
            if (ListOpen(info))
                CloseList(info);
            else
                ShowList(screen);
        };

        void Update()
        {
            undo.Disabled = !History.CanUndo;
            redo.Disabled = !History.CanRedo;
            revert.Disabled = History.AtEntered;
            var (on, off, moved) = Counts();
            var enabled = ModManager.Mods.Count(m => m.manifest?.id is { } id && id != ModEntry.ModId && Dependencies.IsOn(m))
                + Installed.Mods.Count(Installed.On); // on after a relaunch, new mods included
            changes.Text = $"{Menus.Text("SPIRE_MOD_MANAGER.MODS_ENABLED")} {enabled} ([color={Added}]+{on}[/color] [color={Removed}]-{off}[/color])\n"
                + $"{Menus.Text("SPIRE_MOD_MANAGER.CHANGES")} [color={Edited}]{OtherChanges()}[/color]  [color={Moved}]↕{moved}[/color]";
            FitChanges(changes);
            if (info.GetNodeOrNull<Control>(ViewName) is { Visible: true } view)
                FillList(view);
        }
        // After undo/redo/revert, everything on screen redraws from the restored state; the right panel stays
        // on what it showed (the changes list, or the mod, refilled).
        void Restored()
        {
            ModDetails.Forget(info); // its fields show the old state; don't save them over the restored one
            Profiles.RefreshTickboxes(screen);
            CategoryList.Redraw(content);
            Profiles.NotifySwitched();
            if (!ListOpen(info) && CategoryList.Selected is { } mod && Layout.Current == Layout.View.Categories)
                info.Fill(mod);
        }
        // Revert all goes back to how things were when the page opened (the game may reuse the screen).
        History.Enter();
        screen.VisibilityChanged += () =>
        {
            if (screen.IsVisibleInTree())
            {
                History.Enter();
                Update();
            }
            else
            {
                HideBack(info); // leaving the page
                PatchNotes.Close(info);
            }
        };
        History.Changed += Update;
        History.Restored += Restored;
        Profiles.Switched += Update;
        Installed.Added += Update;
        Screen = screen;
        bar.TreeExiting += () =>
        {
            if (Screen == screen)
                Screen = null;
            History.Changed -= Update;
            History.Restored -= Restored;
            Profiles.Switched -= Update;
            Installed.Added -= Update;
        };
        Update();
    }

    // The hover summary, just above the Changes lines, right edges together.
    static void ShowSummary(NModdingScreen screen, Control anchor)
    {
        var lines = Summary();
        if (Layout.Current == Layout.View.Categories && Describe().Count > 0)
            lines.Add($"\n[color={Dim}]{Menus.Text("SPIRE_MOD_MANAGER.CHANGES_CLICK")}[/color]");
        Hover.Show(screen, SummaryName, string.Join("\n", lines), anchor.GlobalPosition + new Vector2(anchor.Size.X, -8), right: true, above: true,
            keep: () => anchor.IsVisibleInTree() && anchor.GetGlobalRect().HasPoint(Hover.Mouse(anchor)));
    }

    // Mods turned on, off, and moved in the load order, compared with launch.
    public static (int On, int Off, int Moved) Counts()
    {
        var changed = ModChanges(History.Loaded?.Mods ?? []);
        return (changed.Count(c => c.Enabled), changed.Count(c => !c.Enabled), Moves().Count);
    }

    // The second line's count: mods installed since launch, edited mods, profiles added or removed, and a
    // switched profile (moves are on the first line).
    static int OtherChanges()
    {
        if (History.Loaded is not { } loaded)
            return 0;
        var (edited, profiles) = DataChanges(loaded.Store, Store.EditsJson());
        return Installed.Mods.Count + edited.Count + profiles.Count + (Switch() != null ? 1 : 0);
    }

    // The summary's lines (BBCode): a switched profile, then each kind of change with its count.
    static List<string> Summary()
    {
        if (History.Loaded is not { } loaded)
            return [];
        var lines = new List<string> { $"[b]{Menus.Text("SPIRE_MOD_MANAGER.CHANGES_TITLE")}[/b]" };
        if (Switch() is var (from, to))
            lines.Add(Menus.Format("SPIRE_MOD_MANAGER.CHANGE_SWITCH", ("from", $"[b]{Escape(from)}[/b]"), ("to", $"[b]{Escape(to)}[/b]")));
        var (on, off, moved) = Counts();
        // Fewer than NameLimit mods turned on, off or installed: each by name under its count.
        var changed = ModChanges(loaded.Mods);
        var named = changed.Count + Installed.Mods.Count < NameLimit;
        void Names(IEnumerable<string> names)
        {
            if (named)
                lines.AddRange(names.Order().Select(n => $"    {Escape(n)}"));
        }
        if (on > 0)
        {
            lines.Add($"[color={Added}]{Menus.Format("SPIRE_MOD_MANAGER.SUMMARY_ON", ("count", on.ToString()))}[/color]");
            Names(changed.Where(c => c.Enabled).Select(c => Name(c.Mod)));
        }
        if (off > 0)
        {
            lines.Add($"[color={Removed}]{Menus.Format("SPIRE_MOD_MANAGER.SUMMARY_OFF", ("count", off.ToString()))}[/color]");
            Names(changed.Where(c => !c.Enabled).Select(c => Name(c.Mod)));
        }
        if (Installed.Mods.Count is var installed and > 0)
        {
            lines.Add($"[color={New}]{Menus.Format(installed == 1 ? "SPIRE_MOD_MANAGER.SUMMARY_INSTALLED_ONE" : "SPIRE_MOD_MANAGER.SUMMARY_INSTALLED", ("count", installed.ToString()))}[/color]");
            Names(Installed.Mods.Select(m => $"{Name(m)} ({Menus.Text(Installed.On(m) ? "SPIRE_MOD_MANAGER.STATUS_ENABLED" : "SPIRE_MOD_MANAGER.STATUS_DISABLED")})"));
        }
        if (moved > 0)
            lines.Add($"[color={Moved}]{Menus.Format("SPIRE_MOD_MANAGER.SUMMARY_ORDER", ("count", moved.ToString()))}[/color]");
        var (edited, profiles) = DataChanges(loaded.Store, Store.EditsJson());
        if (edited.Count > 0)
        {
            // Which details: "Edited: 2 mods (alias, notes)".
            var fields = edited.SelectMany(e => e.Fields).Distinct().Select(f => Menus.Text($"SPIRE_MOD_MANAGER.FIELD_{f.ToUpperInvariant()}"));
            lines.Add($"[color={Edited}]{Escape(Menus.Format(edited.Count == 1 ? "SPIRE_MOD_MANAGER.SUMMARY_EDITED_ONE" : "SPIRE_MOD_MANAGER.SUMMARY_EDITED",
                ("count", edited.Count.ToString()), ("fields", string.Join(", ", fields))))}[/color]");
        }
        if (profiles.Count > 0)
            lines.Add($"[color={Edited}]{Escape(Menus.Format(profiles.Count == 1 ? "SPIRE_MOD_MANAGER.CHANGE_PROFILES_SINGLE" : "SPIRE_MOD_MANAGER.CHANGE_PROFILES",
                ("count", profiles.Count.ToString())))}[/color]");
        if (lines.Count == 1) // just the title
            lines.Add(Menus.Text("SPIRE_MOD_MANAGER.NO_CHANGES"));
        return lines;
    }

    // The full list as plain lines, one per change (also what the self-test reads).
    public static List<string> Describe() => Entries().Select(e => e.Plain).ToList();

    record Entry(string Section, string Plain, string Rich);

    // Every change since launch, by section.
    static List<Entry> Entries()
    {
        if (History.Loaded is not { } loaded)
            return [];
        var entries = new List<Entry>();
        if (Switch() is var (from, to))
            entries.Add(new Entry("", Menus.Format("SPIRE_MOD_MANAGER.CHANGE_SWITCH", ("from", from), ("to", to)),
                Menus.Format("SPIRE_MOD_MANAGER.CHANGE_SWITCH", ("from", $"[b]{Escape(from)}[/b]"), ("to", $"[b]{Escape(to)}[/b]"))));
        foreach (var (mod, enabled) in ModChanges(loaded.Mods).OrderBy(c => !c.Enabled).ThenBy(c => Name(c.Mod)))
        {
            var key = enabled ? "SPIRE_MOD_MANAGER.CHANGE_ON" : "SPIRE_MOD_MANAGER.CHANGE_OFF";
            entries.Add(new Entry(enabled ? "on" : "off", Menus.Format(key, ("mod", Name(mod))), Link(mod.manifest?.id, Name(mod))));
        }
        foreach (var mod in Installed.Mods.OrderBy(Name))
        {
            // Under the name: whether it loads after a relaunch.
            var state = Menus.Text(Installed.On(mod) ? "SPIRE_MOD_MANAGER.INSTALLED_ON" : "SPIRE_MOD_MANAGER.INSTALLED_OFF");
            entries.Add(new Entry("installed", Menus.Format("SPIRE_MOD_MANAGER.CHANGE_INSTALLED", ("mod", Name(mod)), ("state", state)),
                $"{Link(mod.manifest?.id, Name(mod))}\n        [color={Dim}][font_size=15]{Escape(state)}[/font_size][/color]"));
        }
        foreach (var (mod, before, after) in Moves())
            entries.Add(new Entry("order", Menus.Format("SPIRE_MOD_MANAGER.CHANGE_MOVED", ("mod", Name(mod)), ("from", before.ToString()), ("to", after.ToString())),
                $"{Link(mod.manifest?.id, Name(mod))}  [color={Dim}]{before} → {after}[/color]"));
        var (edited, profiles) = DataChanges(loaded.Store, Store.EditsJson());
        foreach (var (id, fields, alias) in edited)
        {
            var name = Dependencies.Find(id)?.manifest?.name ?? id;
            // "Name  (alias "Fixes", notes)": the new alias itself, the other details by name.
            var details = fields.Select(f => f == "alias" && alias is { } a
                ? Menus.Format("SPIRE_MOD_MANAGER.FIELD_ALIAS_NOW", ("alias", a))
                : Menus.Text($"SPIRE_MOD_MANAGER.FIELD_{f.ToUpperInvariant()}"));
            var said = string.Join(", ", details);
            entries.Add(new Entry("edited", Menus.Format("SPIRE_MOD_MANAGER.CHANGE_EDITED_ONE", ("mod", name)) + $" ({said})",
                $"{Link(id, name)}  [color={Dim}]({Escape(said)})[/color]"));
        }
        foreach (var line in profiles)
            entries.Add(new Entry("profiles", line, Escape(line)));
        return entries;
    }

    // A mod's name that opens it (from the changes list).
    static string Link(string? id, string name) => id == null ? Escape(name) : $"[url=mod:{id}]{Escape(name)}[/url]";

    public static bool ListOpen(Node info) => info.GetNodeOrNull<Control>(ViewName) is { Visible: true };

    // Shows the full list in the right panel (our main list only). False when there's no panel to use.
    public static bool ShowList(NModdingScreen screen)
    {
        if (Layout.Current != Layout.View.Categories)
            return false;
        var info = screen.GetNode<Control>("%ModInfoContainer");
        var view = info.GetNodeOrNull<Control>(ViewName) ?? AddList(info);
        HideBack(info);
        PatchNotes.Close(info);
        if (!view.Visible)
        {
            // Everything else in the panel steps aside (and comes back when a mod is picked).
            foreach (var child in info.GetChildren().OfType<Control>().Where(c => c != view && c.Visible))
            {
                child.SetMeta(HiddenMeta, true);
                child.Visible = false;
            }
            view.Visible = true;
        }
        FillList(view);
        return true;
    }

    // Puts the panel back as it was (Close, a row picked, or leaving our main list).
    public static void CloseList(Node info)
    {
        if (info.GetNodeOrNull<Control>(ViewName) is not { Visible: true } view)
            return;
        view.Visible = false;
        foreach (var child in info.GetChildren().OfType<Control>().Where(c => c.HasMeta(HiddenMeta)))
        {
            child.RemoveMeta(HiddenMeta);
            child.Visible = true;
        }
    }

    static Control AddList(Control info)
    {
        var view = new VBoxContainer { Name = ViewName, Visible = false };
        view.AddThemeConstantOverride("separation", 8);
        info.AddChild(view);
        view.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        (view.OffsetLeft, view.OffsetTop, view.OffsetRight, view.OffsetBottom) = (ModDetails.Inset, 14, -ModDetails.Inset, -16);

        var head = new HBoxContainer();
        var title = new Label { Text = Menus.Text("SPIRE_MOD_MANAGER.CHANGES_TITLE"), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        title.AddThemeFontOverride("font", ResourceLoader.Load<Font>("res://themes/kreon_bold_shared.tres"));
        title.AddThemeFontSizeOverride("font_size", 30);
        title.AddThemeColorOverride("font_color", Cream);
        head.AddChild(title);
        var close = Menus.SmallButton(Menus.Text("SPIRE_MOD_MANAGER.CLOSE"), () => CloseList(info), name: "CloseChangesButton");
        close.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        head.AddChild(close);
        view.AddChild(head);

        var hint = new Label { Name = "Hint", Text = Menus.Text("SPIRE_MOD_MANAGER.CHANGES_OPEN_HINT") };
        Style(hint, 15, new Color(Cream, 0.5f));
        view.AddChild(hint);

        // A mod's name opens it in the panel, with Back to return here.
        var list = new RichTextLabel
        {
            Name = "List", BbcodeEnabled = true, ScrollActive = true, SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            SelectionEnabled = false, MetaUnderlined = false,
        };
        Hover.Fonts(list, 18);
        list.MetaClicked += meta =>
        {
            if (meta.AsString() is { } target && target.StartsWith("mod:") && CategoryList.ScreenOf(info) is { } screen)
                OpenFromList(screen, target[4..]);
        };
        CategoryList.WatchNames(list, () => CategoryList.ScreenOf(info)?.GetNodeOrNull<Control>("%ModsScrollContainer/Mask/Content"));
        view.AddChild(list);
        return view;
    }

    // From the changes list: the mod in the panel (and found in the list), Back where Close was.
    public static void OpenFromList(NModdingScreen screen, string modId)
    {
        var content = screen.GetNode<Control>("%ModsScrollContainer/Mask/Content");
        if (content.GetChildren().OfType<NModMenuRow>().FirstOrDefault(r => r.Mod?.manifest?.id == modId) is not { } row)
            return;
        CategoryList.Reveal(content, modId, flash: true);
        screen.OnRowSelected(row); // fills the panel, which closes the list
        var info = screen.GetNode<Control>("%ModInfoContainer");
        if (info.GetNodeOrNull<Control>(BackName) is not { } back)
        {
            back = Menus.SmallButton(Menus.Text("SPIRE_MOD_MANAGER.BACK_TO_CHANGES"), () => ShowList(screen), name: BackName);
            info.AddChild(back);
            back.AnchorLeft = back.AnchorRight = 1;
            back.GrowHorizontal = Control.GrowDirection.Begin;
            (back.OffsetLeft, back.OffsetRight, back.OffsetTop) = (-ModDetails.Inset, -ModDetails.Inset, 21);
        }
        back.Visible = true;
        Layout.PlaceInfo(info); // the thumbnail moves down for it
        Callable.From(() => ModDetails.PlaceFields(info)).CallDeferred();
    }

    // Back goes once the panel shows something else: the list again, another mod, another view, another page.
    public static void HideBack(Control info)
    {
        if (info.GetNodeOrNull<Control>(BackName) is not { Visible: true } back)
            return;
        back.Visible = false;
        Layout.PlaceInfo(info);
        Callable.From(() => ModDetails.PlaceFields(info)).CallDeferred();
    }

    // The two lines left-aligned, their left edge kept still as the counts change (unless they grow wider),
    // ending at RightMeta (set by Layout: just left of Undo).
    public const string RightMeta = "spire_mod_manager_right";
    const float ChangesMinWidth = 240;

    public static void FitChanges(RichTextLabel changes)
    {
        if (!changes.HasMeta(RightMeta))
            return;
        var width = Math.Max(ChangesMinWidth, changes.GetContentWidth() + 4);
        changes.Size = new Vector2(width, changes.Size.Y);
        changes.Position = new Vector2((float)changes.GetMeta(RightMeta) - width, changes.Position.Y);
    }

    // Sections with counts, each change on its own line.
    static void FillList(Control view)
    {
        var entries = Entries();
        var text = new System.Text.StringBuilder();
        void Section(string key, string color, string id)
        {
            var items = entries.Where(e => e.Section == id).ToList();
            if (items.Count == 0)
                return;
            text.Append($"\n[color={color}][b]{Escape(Menus.Format(key, ("count", items.Count.ToString())))}[/b][/color]\n");
            foreach (var item in items)
                text.Append($"    {item.Rich}\n");
        }
        foreach (var line in entries.Where(e => e.Section == ""))
            text.Append(line.Rich).Append('\n');
        Section("SPIRE_MOD_MANAGER.SECTION_ON", Added, "on");
        Section("SPIRE_MOD_MANAGER.SECTION_OFF", Removed, "off");
        Section("SPIRE_MOD_MANAGER.SECTION_INSTALLED", New, "installed");
        Section("SPIRE_MOD_MANAGER.SECTION_ORDER", Moved, "order");
        Section("SPIRE_MOD_MANAGER.SECTION_EDITED", Edited, "edited");
        Section("SPIRE_MOD_MANAGER.SECTION_PROFILES", Edited, "profiles");
        if (entries.Count == 0)
            text.Append(Menus.Text("SPIRE_MOD_MANAGER.NO_CHANGES"));
        view.GetNode<RichTextLabel>("List").Text = text.ToString().TrimStart('\n');
    }

    // The profile at launch and now, if they differ.
    static (string From, string To)? Switch() =>
        History.LaunchProfile is { } launch && Store.Data.ActiveProfile is { } active && active != launch ? (launch, active) : null;

    static string Name(Mod mod) => mod.manifest?.name ?? mod.manifest?.id ?? "";

    // Installed mods whose on/off differs from the given list (no entry = on).
    static List<(Mod Mod, bool Enabled)> ModChanges(IReadOnlyList<History.ModEntry> before)
    {
        var then = before.DistinctBy(m => (m.Id, m.Source)).ToDictionary(m => (m.Id, m.Source), m => m.Enabled);
        var now = History.CurrentMods().DistinctBy(m => (m.Id, m.Source)).ToDictionary(m => (m.Id, m.Source), m => m.Enabled);
        return ModManager.Mods.Where(m => m.manifest?.id != null)
            .Select(m => (Mod: m, Key: (m.manifest?.id ?? "", m.modSource)))
            .Select(p => (p.Mod, Then: then.GetValueOrDefault(p.Key, true), Now: now.GetValueOrDefault(p.Key, true)))
            .Where(p => p.Then != p.Now)
            .Select(p => (p.Mod, p.Now))
            .ToList();
    }

    // Installed mods whose place in the load order changed since launch: (mod, position then, now), 1-based.
    static List<(Mod Mod, int Before, int After)> Moves()
    {
        if (History.Loaded is not { } loaded)
            return [];
        bool Installed(History.ModEntry m) => ModManager.Mods.Any(x => x.manifest?.id == m.Id && x.modSource == m.Source);
        var before = loaded.Mods.Where(Installed).Select(m => (m.Id, m.Source)).Distinct().ToList();
        var after = History.CurrentMods().Where(Installed).Select(m => (m.Id, m.Source)).Distinct().ToList();
        var moves = new List<(Mod, int, int)>();
        for (var i = 0; i < after.Count; i++)
        {
            var was = before.IndexOf(after[i]) + 1;
            if (was > 0 && was != i + 1 && ModManager.Mods.FirstOrDefault(m => m.manifest?.id == after[i].Id && m.modSource == after[i].Source) is { } mod)
                moves.Add((mod, was, i + 1));
        }
        return moves;
    }

    // Mods whose details changed: which ones (category, alias, notes, tags) and the alias now; profiles added
    // or removed (lines).
    static (List<(string Id, List<string> Fields, string? Alias)> Edited, List<string> Profiles) DataChanges(string before, string after)
    {
        try
        {
            var a = JsonNode.Parse(before)?.AsObject();
            var b = JsonNode.Parse(after)?.AsObject();
            var modsA = a?["mods"] as JsonObject ?? [];
            var modsB = b?["mods"] as JsonObject ?? [];
            var edited = new List<(string, List<string>, string?)>();
            foreach (var id in modsA.Select(p => p.Key).Union(modsB.Select(p => p.Key)))
            {
                var fields = new[] { "category", "alias", "notes", "tags" }
                    .Where(f => !JsonNode.DeepEquals(modsA[id]?[f], modsB[id]?[f])).ToList();
                if (fields.Count > 0)
                    edited.Add((id, fields, (string?)modsB[id]?["alias"]));
            }
            HashSet<string> Names(JsonObject? o) => (o?["profiles"] as JsonArray ?? []).Select(p => (string?)p?["name"] ?? "").ToHashSet();
            var profiles = Names(b).Except(Names(a)).Select(n => Menus.Format("SPIRE_MOD_MANAGER.PROFILE_ADDED", ("name", n)))
                .Concat(Names(a).Except(Names(b)).Select(n => Menus.Format("SPIRE_MOD_MANAGER.PROFILE_REMOVED", ("name", n))))
                .ToList();
            return (edited, profiles);
        }
        catch (Exception)
        {
            return ([], []);
        }
    }

    static string Escape(string text) => text.Replace("[", "[lb]");

    static void Style(Label label, int size, Color color)
    {
        label.AddThemeFontOverride("font", ResourceLoader.Load<Font>("res://themes/kreon_regular_shared.tres"));
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", color);
    }
}
