using System.Text.Json.Nodes;
using Godot;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;

namespace SpireModManager;

// Along the bottom right, before Relaunch Game:
// - "Changes +4 -3 ↕2": mods turned on (green) and off (red) and moved in the load order (blue) since
//   launch. Hovering shows a summary; clicking (or Undo, Redo, Revert all) opens the full list in the right
//   panel: switched profile, each mod enabled or disabled, each move, edited mods, profile changes.
// - Undo, Redo (their shortcuts underneath), Revert all.
static class HistoryBar
{
    public const string BarName = "SpireModManagerHistory";
    public const string ChangesName = "SpireModManagerChanges";
    const string PanelName = "SpireModManagerChangesPanel";
    public const string ViewName = "SpireModManagerChangesView";
    const string HiddenMeta = "spire_mod_manager_hidden_by_changes";
    static readonly Color Cream = new(1, 0.964706f, 0.886275f);
    static readonly Color Gold = new(0.937f, 0.784f, 0.318f);
    const string Added = "#a6e3a1", Removed = "#f2a3a3", Moved = "#8ec5ff", Dim = "#fff6e280";

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
            ScrollActive = false, Size = new Vector2(280, 28), MouseFilter = Control.MouseFilterEnum.Stop,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand, HorizontalAlignment = HorizontalAlignment.Right,
        };
        changes.AddThemeFontOverride("normal_font", ResourceLoader.Load<Font>("res://themes/kreon_regular_shared.tres"));
        changes.AddThemeFontSizeOverride("normal_font_size", 18);
        changes.AddThemeColorOverride("default_color", Cream);
        screen.AddChild(changes);
        changes.MouseEntered += () => ShowSummary(screen, changes);
        changes.MouseExited += () => screen.GetNodeOrNull(PanelName)?.QueueFree();
        changes.GuiInput += e =>
        {
            if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false })
                ShowList(screen);
        };

        void Update()
        {
            undo.Disabled = !History.CanUndo;
            redo.Disabled = !History.CanRedo;
            revert.Disabled = History.AtEntered;
            var (on, off, moved) = Counts();
            changes.Text = $"{Menus.Text("SPIRE_MOD_MANAGER.CHANGES")} [color={Added}]+{on}[/color] [color={Removed}]-{off}[/color] [color={Moved}]↕{moved}[/color]";
            if (info.GetNodeOrNull<Control>(ViewName) is { Visible: true } view)
                FillList(view);
        }
        // After undo/redo/revert, everything on screen redraws from the restored state, and the right panel
        // shows what's changed now (in Load Order, which has no panel, it just empties).
        void Restored()
        {
            ModDetails.Forget(info); // its fields show the old state; don't save them over the restored one
            Profiles.RefreshTickboxes(screen);
            CategoryList.Redraw(content);
            Profiles.NotifySwitched();
            if (!ShowList(screen))
                info.Clear();
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
        };
        History.Changed += Update;
        History.Restored += Restored;
        Profiles.Switched += Update;
        Screen = screen;
        bar.TreeExiting += () =>
        {
            if (Screen == screen)
                Screen = null;
            History.Changed -= Update;
            History.Restored -= Restored;
            Profiles.Switched -= Update;
        };
        Update();
    }

    // The hover summary, just above the Changes line: the switch, how many on/off/moved, edits.
    static void ShowSummary(NModdingScreen screen, Control anchor)
    {
        screen.GetNodeOrNull(PanelName)?.QueueFree();
        var panel = new PanelContainer { Name = PanelName, MouseFilter = Control.MouseFilterEnum.Ignore };
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.05f, 0.08f, 0.12f, 0.97f), BorderColor = new Color(Gold, 0.5f),
            ContentMarginLeft = 16, ContentMarginRight = 16, ContentMarginTop = 10, ContentMarginBottom = 10,
        };
        style.SetBorderWidthAll(1);
        style.SetCornerRadiusAll(6);
        panel.AddThemeStyleboxOverride("panel", style);
        var lines = Summary();
        if (Layout.Current == Layout.View.Categories && Describe().Count > 0)
            lines.Add($"[color={Dim}]{Menus.Text("SPIRE_MOD_MANAGER.CHANGES_CLICK")}[/color]");
        var text = new RichTextLabel
        {
            BbcodeEnabled = true, FitContent = true, ScrollActive = false, CustomMinimumSize = new Vector2(380, 0),
            MouseFilter = Control.MouseFilterEnum.Ignore, Text = string.Join("\n", lines),
        };
        Fonts(text, 17);
        panel.AddChild(text);
        screen.AddChild(panel);
        // Above the line (it sits at the bottom of the screen), right edges together.
        panel.ResetSize();
        Callable.From(() =>
        {
            if (GodotObject.IsInstanceValid(panel))
                panel.Position = new Vector2(anchor.Position.X + anchor.Size.X - panel.Size.X, anchor.Position.Y - panel.Size.Y - 8);
        }).CallDeferred();
    }

    // Mods turned on, off, and moved in the load order, compared with launch.
    public static (int On, int Off, int Moved) Counts()
    {
        var changed = ModChanges(History.Loaded?.Mods ?? []);
        return (changed.Count(c => c.Enabled), changed.Count(c => !c.Enabled), Moves().Count);
    }

    // The summary's lines (BBCode): a switched profile, counts, edits.
    static List<string> Summary()
    {
        if (History.Loaded is not { } loaded)
            return [];
        var lines = new List<string>();
        if (Switch() is var (from, to))
            lines.Add(Menus.Format("SPIRE_MOD_MANAGER.CHANGE_SWITCH", ("from", $"[b]{Escape(from)}[/b]"), ("to", $"[b]{Escape(to)}[/b]")));
        var (on, off, moved) = Counts();
        if (on > 0)
            lines.Add($"[color={Added}]{Menus.Format("SPIRE_MOD_MANAGER.SUMMARY_ON", ("count", on.ToString()))}[/color]");
        if (off > 0)
            lines.Add($"[color={Removed}]{Menus.Format("SPIRE_MOD_MANAGER.SUMMARY_OFF", ("count", off.ToString()))}[/color]");
        if (moved > 0)
            lines.Add($"[color={Moved}]{Menus.Format("SPIRE_MOD_MANAGER.SUMMARY_ORDER", ("count", moved.ToString()))}[/color]");
        var (edited, profiles) = DataChanges(loaded.Store, Store.EditsJson());
        if (edited.Count > 0)
            lines.Add(Escape(Menus.Format(edited.Count == 1 ? "SPIRE_MOD_MANAGER.CHANGE_EDITED_SINGLE" : "SPIRE_MOD_MANAGER.CHANGE_EDITED", ("count", edited.Count.ToString()))));
        if (profiles.Count > 0)
            lines.Add(Escape(Menus.Format(profiles.Count == 1 ? "SPIRE_MOD_MANAGER.CHANGE_PROFILES_SINGLE" : "SPIRE_MOD_MANAGER.CHANGE_PROFILES", ("count", profiles.Count.ToString()))));
        if (lines.Count == 0)
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
            entries.Add(new Entry(enabled ? "on" : "off", Menus.Format(key, ("mod", Name(mod))), Escape(Name(mod))));
        }
        foreach (var (mod, before, after) in Moves())
            entries.Add(new Entry("order", Menus.Format("SPIRE_MOD_MANAGER.CHANGE_MOVED", ("mod", Name(mod)), ("from", before.ToString()), ("to", after.ToString())),
                $"{Escape(Name(mod))}  [color={Dim}]{before} → {after}[/color]"));
        var (edited, profiles) = DataChanges(loaded.Store, Store.EditsJson());
        foreach (var id in edited)
        {
            var name = Dependencies.Find(id)?.manifest?.name ?? id;
            entries.Add(new Entry("edited", Menus.Format("SPIRE_MOD_MANAGER.CHANGE_EDITED_ONE", ("mod", name)), Escape(name)));
        }
        foreach (var line in profiles)
            entries.Add(new Entry("profiles", line, Escape(line)));
        return entries;
    }

    // Shows the full list in the right panel (our main list only). False when there's no panel to use.
    public static bool ShowList(NModdingScreen screen)
    {
        if (Layout.Current != Layout.View.Categories)
            return false;
        var info = screen.GetNode<Control>("%ModInfoContainer");
        var view = info.GetNodeOrNull<Control>(ViewName) ?? AddList(info);
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

    // Puts the panel back as it was (picking a mod, or leaving our main list).
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

        var list = new RichTextLabel
        {
            Name = "List", BbcodeEnabled = true, ScrollActive = true, SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            SelectionEnabled = false,
        };
        Fonts(list, 18);
        view.AddChild(list);
        return view;
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
        Section("SPIRE_MOD_MANAGER.SECTION_ORDER", Moved, "order");
        Section("SPIRE_MOD_MANAGER.SECTION_EDITED", "#e8cf7a", "edited");
        Section("SPIRE_MOD_MANAGER.SECTION_PROFILES", "#e8cf7a", "profiles");
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

    // Mods whose category, alias, notes or tags changed (ids); profiles added or removed (lines).
    static (List<string> Edited, List<string> Profiles) DataChanges(string before, string after)
    {
        try
        {
            var a = JsonNode.Parse(before)?.AsObject();
            var b = JsonNode.Parse(after)?.AsObject();
            var modsA = a?["mods"] as JsonObject ?? [];
            var modsB = b?["mods"] as JsonObject ?? [];
            var edited = modsA.Select(p => p.Key).Union(modsB.Select(p => p.Key))
                .Where(id => !JsonNode.DeepEquals(modsA[id], modsB[id])).ToList();
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

    static void Fonts(RichTextLabel text, int size)
    {
        text.AddThemeFontOverride("normal_font", ResourceLoader.Load<Font>("res://themes/kreon_regular_shared.tres"));
        text.AddThemeFontOverride("bold_font", ResourceLoader.Load<Font>("res://themes/kreon_bold_shared.tres"));
        text.AddThemeFontSizeOverride("normal_font_size", size);
        text.AddThemeFontSizeOverride("bold_font_size", size);
        text.AddThemeColorOverride("default_color", Cream);
    }

    static void Style(Label label, int size, Color color)
    {
        label.AddThemeFontOverride("font", ResourceLoader.Load<Font>("res://themes/kreon_regular_shared.tres"));
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", color);
    }
}
