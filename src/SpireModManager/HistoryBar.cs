using System.Text.Json.Nodes;
using Godot;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;

namespace SpireModManager;

// Along the bottom right, before Relaunch Game:
// - "Mods enabled 23 (+1 -3)" and "Changes 5  ↕2": mods on, and turned on or off since launch; then everything else
//   changed (installs, edits, profiles) and the load-order moves. Hovering shows a summary; clicking, the full list in
//   the right panel, where a mod's name opens it (with Back to the list).
// - Undo, Redo (their shortcuts underneath), Revert all (everything back to launch: History.RevertMods).
// The same place in the panel lists the active profile's unsaved changes ("Unsaved •" on the profile bar); one list
// at a time.
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

    // Which list the panel shows: everything changed since launch, or the active profile's unsaved changes.
    public enum ListKind { Changes, Unsaved }

    // The list the panel shows, or showed last (where Back returns to).
    static ListKind _list;

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
            // Cut to fit (with ".." if it must be), so its column is as wide as the button, not as the hint (FitHint).
            var hint = new Label { Name = "Keys", HorizontalAlignment = HorizontalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore };
            Menus.CutToFit(hint, keys == null ? " " : Menus.Text(keys));
            Style(hint, 13, new Color(Cream, 0.5f));
            column.AddChild(hint);
            bar.AddChild(column);
            FitHint(hint, button);
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
        // Hovering: the summary, at once, just above the lines, right edges together.
        Hover.Watch(changes, SummaryName, Summary, Hover.Timing.Instant, place: () => (changes.GlobalPosition + new Vector2(changes.Size.X, -8), true, true));
        changes.GuiInput += e =>
        {
            if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false })
                ToggleList(screen, ListKind.Changes);
        };

        void Update()
        {
            undo.Disabled = !History.CanUndo;
            redo.Disabled = !History.CanRedo;
            revert.Disabled = History.AtLaunch;
            var (on, off, moved) = Counts();
            // On after a relaunch, new mods and this one included, a mod installed twice once: the profile bar's count.
            var enabled = Profiles.OnCount();
            // The words and the count's place in them come from the loc table ("Mods enabled {count}"); the +, -
            // and ↕ counts stay after them in every language.
            changes.Text = $"{Menus.Format("SPIRE_MOD_MANAGER.MODS_ENABLED", ("count", Menus.Number(enabled)))} ([color={Added}]+{Menus.Number(on)}[/color] [color={Removed}]-{Menus.Number(off)}[/color])\n"
                + $"{Menus.Format("SPIRE_MOD_MANAGER.CHANGES", ("count", $"[color={Edited}]{Menus.Number(OtherChanges())}[/color]"))}  [color={Moved}]↕{Menus.Number(moved)}[/color]";
            FitChanges(changes);
            if (info.GetNodeOrNull<Control>(ViewName) is { Visible: true } view)
                FillList(view);
            Layout.UpdateDot(screen); // the "after a restart" line follows every change, as the main menu button does
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
        // After undo/redo of a selection step: only the tints and the page bar; an open menu (the bulk menu edits the
        // picks it opened with) closes.
        void Repicked()
        {
            MenuPanel.Close();
            Selection.Show(content);
        }
        // Undo starts afresh with each visit to the page (the game may reuse the screen).
        History.Enter();
        screen.VisibilityChanged += () =>
        {
            if (screen.IsVisibleInTree())
            {
                History.Enter();
                Update();
            }
            else
                HideBack(info); // leaving the page
        };
        History.Changed += Update;
        History.Restored += Restored;
        History.Repicked += Repicked;
        Profiles.Switched += Update;
        Installed.Added += Update;
        Screen = screen;
        bar.TreeExiting += () =>
        {
            if (Screen == screen)
                Screen = null;
            History.Changed -= Update;
            History.Restored -= Restored;
            History.Repicked -= Repicked;
            Profiles.Switched -= Update;
            Installed.Added -= Update;
        };
        Update();
    }

    // The hover summary's text, with how to see the full list (cta: on the page, where a click shows it).
    public static string Summary() => Summary(cta: true);

    public static string Summary(bool cta)
    {
        var lines = SummaryLines();
        if (cta && Layout.Current == Layout.View.Categories && Describe().Count > 0)
            lines.Add($"\n[color={Dim}]{Menus.Text("SPIRE_MOD_MANAGER.CHANGES_CLICK")}[/color]");
        return string.Join("\n", lines);
    }

    // Mods turned on, off, and moved in the load order, compared with launch.
    public static (int On, int Off, int Moved) Counts() => Counts(History.Loaded is { } loaded ? ModChanges(loaded) : []);

    // Mods where another copy loads with no box changed (a profile naming the other copy), since launch.
    public static int CopySwitchCount() => History.Loaded is { } loaded ? CopySwitches(loaded, ModChanges(loaded)).Count : 0;

    static (int On, int Off, int Moved) Counts(List<(Mod Mod, bool Enabled)> changed) =>
        (changed.Count(c => c.Enabled), changed.Count(c => !c.Enabled), Moves().Count);

    // The second line's count: mods installed since launch, edited mods, profiles added, deleted or renamed, a
    // switched profile, and mods where another copy loads with no box changed (moves are on the first line).
    static int OtherChanges()
    {
        if (History.Loaded is not { } loaded)
            return 0;
        var (edited, profiles, renamed) = DataChanges(loaded.Store, Store.EditsJson());
        return Installed.Mods.Count + edited.Count + profiles.Count + (Switch(renamed) != null ? 1 : 0) + CopySwitches(loaded, ModChanges(loaded)).Count;
    }

    // The summary's lines (BBCode): a switched profile, then each kind of change with its count.
    static List<string> SummaryLines()
    {
        if (History.Loaded is not { } loaded)
            return [];
        var lines = new List<string> { $"[b]{Menus.Text("SPIRE_MOD_MANAGER.CHANGES_TITLE")}[/b]" };
        var (edited, profiles, renamed) = DataChanges(loaded.Store, Store.EditsJson());
        if (Switch(renamed) is var (from, to))
            lines.Add(Menus.Format("SPIRE_MOD_MANAGER.CHANGE_SWITCH", ("from", $"[b]{Escape(from)}[/b]"), ("to", $"[b]{Escape(to)}[/b]")));
        var changed = ModChanges(loaded);
        lines.AddRange(CopySwitches(loaded, changed).Select(s => Escape(CopySwitch(s.From, s.To).Plain)));
        var (on, off, moved) = Counts(changed);
        // Fewer than NameLimit mods turned on, off or installed: each by name under its count.
        var named = changed.Count + Installed.Mods.Count < NameLimit;
        void Names(IEnumerable<string> names)
        {
            if (named)
                lines.AddRange(names.Order().Select(n => $"    {Escape(n)}"));
        }
        if (on > 0)
        {
            lines.Add($"[color={Added}]{Menus.Format("SPIRE_MOD_MANAGER.SUMMARY_ON", ("count", Menus.Number(on)))}[/color]");
            Names(changed.Where(c => c.Enabled).Select(c => Name(c.Mod) + Copies.Which(c.Mod)));
        }
        if (off > 0)
        {
            lines.Add($"[color={Removed}]{Menus.Format("SPIRE_MOD_MANAGER.SUMMARY_OFF", ("count", Menus.Number(off)))}[/color]");
            Names(changed.Where(c => !c.Enabled).Select(c => Name(c.Mod) + Copies.Which(c.Mod)));
        }
        if (Installed.Mods.Count is var installed and > 0)
        {
            lines.Add($"[color={New}]{Menus.Format(installed == 1 ? "SPIRE_MOD_MANAGER.SUMMARY_INSTALLED_ONE" : "SPIRE_MOD_MANAGER.SUMMARY_INSTALLED", ("count", Menus.Number(installed)))}[/color]");
            Names(Installed.Mods.Select(m => $"{Name(m)} ({Menus.Text(Copies.Ticked(m) ? "SPIRE_MOD_MANAGER.STATUS_ENABLED" : "SPIRE_MOD_MANAGER.STATUS_DISABLED")})"));
        }
        if (moved > 0)
            lines.Add($"[color={Moved}]{Menus.Format("SPIRE_MOD_MANAGER.SUMMARY_ORDER", ("count", Menus.Number(moved)))}[/color]");
        if (edited.Count > 0)
        {
            // Which details: "Edited: 2 mods (alias, notes)".
            var fields = edited.SelectMany(e => e.Fields).Distinct().Select(f => Menus.Text($"SPIRE_MOD_MANAGER.FIELD_{f.ToUpperInvariant()}"));
            lines.Add($"[color={Edited}]{Escape(Menus.Format(edited.Count == 1 ? "SPIRE_MOD_MANAGER.SUMMARY_EDITED_ONE" : "SPIRE_MOD_MANAGER.SUMMARY_EDITED",
                ("count", Menus.Number(edited.Count)), ("fields", string.Join(", ", fields))))}[/color]");
        }
        if (profiles.Count > 0)
            lines.Add($"[color={Edited}]{Escape(Menus.Format(profiles.Count == 1 ? "SPIRE_MOD_MANAGER.CHANGE_PROFILES_SINGLE" : "SPIRE_MOD_MANAGER.CHANGE_PROFILES",
                ("count", Menus.Number(profiles.Count))))}[/color]");
        if (lines.Count == 1) // just the title
            lines.Add(Menus.Text("SPIRE_MOD_MANAGER.NO_CHANGES"));
        return lines;
    }

    // The full list as plain lines, one per change.
    public static List<string> Describe() => Entries().Select(e => e.Plain).ToList();

    // The mods the full list names (Entries' sections, from the same rules): each one turned on or off, installed or
    // moved, each copy whose own details were edited (Copies.Key), and both copies where another loads with no box
    // changed. What the main list shows under "Changed" (ListFilter).
    public static HashSet<Mod> ChangedMods()
    {
        if (History.Loaded is not { } loaded)
            return [];
        var edited = EditedKeys();
        // Each copy turned on or off, switched to or from, or moved (each copy has its own place).
        static (string?, ModSource, string) Copy(Mod m) => (m.manifest?.id, m.modSource, Copies.CopyOf(m));
        var changed = ModChanges(loaded);
        var switched = changed.Select(c => Copy(c.Mod)).Concat(Moves().Select(m => Copy(m.Mod)))
            .Concat(CopySwitches(loaded, changed).SelectMany(s => new[] { Copy(s.From), Copy(s.To) })).ToHashSet();
        return [.. ModManager.Mods.Where(m => switched.Contains(Copy(m))), .. Installed.Mods,
            .. ModManager.Mods.Union(Installed.Mods).Where(m => edited.Contains(Copies.Key(m)))]; // by key: one copy's own edits
    }

    // Our data's keys (Copies.Key) of the copies whose own details were edited since launch.
    public static HashSet<string> EditedKeys() =>
        History.Loaded is { } loaded ? DataChanges(loaded.Store, Store.EditsJson()).Edited.Select(e => e.Id).ToHashSet() : [];

    record Entry(string Section, string Plain, string Rich);

    // Every change since launch, by section. (ChangedMods names the same mods: a new section naming mods goes there
    // too.)
    static List<Entry> Entries()
    {
        if (History.Loaded is not { } loaded)
            return [];
        var entries = new List<Entry>();
        var (edited, profiles, renamed) = DataChanges(loaded.Store, Store.EditsJson());
        if (Switch(renamed) is var (from, to))
            entries.Add(new Entry("", Menus.Format("SPIRE_MOD_MANAGER.CHANGE_SWITCH", ("from", from), ("to", to)),
                Menus.Format("SPIRE_MOD_MANAGER.CHANGE_SWITCH", ("from", $"[b]{Escape(from)}[/b]"), ("to", $"[b]{Escape(to)}[/b]"))));
        var changed = ModChanges(loaded);
        // Another copy loads with no box changed (ticked again in another order, a profile naming another copy): a line
        // of its own, beside the profile's.
        foreach (var (was, now) in CopySwitches(loaded, changed).OrderBy(s => Name(s.To)))
        {
            var (plain, rich) = CopySwitch(was, now);
            entries.Add(new Entry("", plain, rich));
        }
        foreach (var (mod, enabled) in changed.OrderBy(c => !c.Enabled).ThenBy(c => Name(c.Mod)))
        {
            var key = enabled ? "SPIRE_MOD_MANAGER.CHANGE_ON" : "SPIRE_MOD_MANAGER.CHANGE_OFF";
            var name = Name(mod) + Copies.Which(mod); // a copy says which ("Enabled: LucanaMod (3809131080)")
            entries.Add(new Entry(enabled ? "on" : "off", Menus.Format(key, ("mod", name)), Link(Copies.LinkKey(mod), name)));
        }
        foreach (var mod in Installed.Mods.OrderBy(Name))
        {
            // Under the name: whether it's enabled and loads after a relaunch, or is enabled beside a copy that loads instead.
            var ticked = Copies.Ticked(mod);
            var state = Menus.Text(ticked && Copies.Loads(mod) ? "SPIRE_MOD_MANAGER.INSTALLED_ON"
                : ticked && Copies.Loses(mod) ? "SPIRE_MOD_MANAGER.INSTALLED_WONT_LOAD" : "SPIRE_MOD_MANAGER.INSTALLED_OFF");
            entries.Add(new Entry("installed", Menus.Format("SPIRE_MOD_MANAGER.CHANGE_INSTALLED", ("mod", Name(mod)), ("state", state)),
                $"{Link(Copies.LinkKey(mod), Name(mod))}\n        [color={Dim}][font_size=15]{Escape(state)}[/font_size][/color]")); // a new copy: itself, not the copy that holds the id
        }
        foreach (var (mod, before, after) in Moves())
            entries.Add(new Entry("order", Menus.Format("SPIRE_MOD_MANAGER.CHANGE_MOVED", ("mod", Name(mod) + Copies.Which(mod)), ("from", before.ToString()), ("to", after.ToString())),
                $"{Link(Copies.LinkKey(mod), Name(mod) + Copies.Which(mod))}  [color={Dim}]{before} → {after}[/color]"));
        foreach (var (id, fields, alias) in edited)
        {
            var copy = Copies.ModOf(id); // id: a mod's key in our data
            var name = copy != null ? Name(copy) + Copies.Which(copy) : Copies.IdOf(id); // a copy says which
            // "Name  (alias "Fixes", notes)": the new alias itself, the other details by name.
            var details = fields.Select(f => f == "alias" && alias is { } a
                ? Menus.Format("SPIRE_MOD_MANAGER.FIELD_ALIAS_NOW", ("alias", a))
                : Menus.Text($"SPIRE_MOD_MANAGER.FIELD_{f.ToUpperInvariant()}"));
            var said = string.Join(", ", details);
            entries.Add(new Entry("edited", Menus.Format("SPIRE_MOD_MANAGER.CHANGE_EDITED_ONE", ("mod", name)) + $" ({said})",
                $"{Link(copy != null ? Copies.LinkKey(copy) : id, name)}  [color={Dim}]({Escape(said)})[/color]"));
        }
        foreach (var line in profiles)
            entries.Add(new Entry("profiles", line, Escape(line)));
        return entries;
    }

    // A mod's name that opens it (from the changes list).
    static string Link(string? id, string name) => id == null ? Escape(name) : $"[url=mod:{id}]{Escape(name)}[/url]";

    // Whether the panel shows a list (kind: that one).
    public static bool ListOpen(Node info, ListKind? kind = null) =>
        info.GetNodeOrNull<Control>(ViewName) is { Visible: true } && (kind == null || kind == _list);

    // A click on what opens a list (the Changes text, "Unsaved •"): shows it, or closes it on a second click. With
    // Back showing (a mod opened from the list), it goes back; with the other list showing, this one replaces it.
    public static void ToggleList(NModdingScreen screen, ListKind kind)
    {
        var info = screen.GetNode<Control>("%ModInfoContainer");
        if (ListOpen(info, kind))
            CloseList(info);
        else
            ShowList(screen, kind);
    }

    // Shows a list in the right panel (our main list only): the full list of changes, or the unsaved ones. False
    // when there's no panel to use.
    public static bool ShowList(NModdingScreen screen, ListKind kind = ListKind.Changes)
    {
        if (Layout.Current != Layout.View.Categories)
            return false;
        var info = screen.GetNode<Control>("%ModInfoContainer");
        var view = info.GetNodeOrNull<Control>(ViewName) ?? AddList(info);
        HideBack(info);
        _list = kind;
        if (!view.Visible)
        {
            // Everything else in the panel steps aside (and comes back when a mod is picked): ours and the game's hidden,
            // other mods' parts out of sight with their Visible left to their mods (Layout.ParkOthers).
            var others = Layout.ParkOthers(info);
            foreach (var child in info.GetChildren().OfType<Control>().Where(c => c != view && c.Visible && !others.Contains(c)))
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
        Layout.UnparkOthers(info);
        // The fields aren't placed while the list covers the panel (ModDetails.PlaceFields): placed again now.
        if (info is Control panel)
            Callable.From(() => ModDetails.PlaceFields(panel)).CallDeferred();
    }

    static Control AddList(Control info)
    {
        var view = new VBoxContainer { Name = ViewName, Visible = false };
        view.AddThemeConstantOverride("separation", 8);
        info.AddChild(view);
        view.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        (view.OffsetLeft, view.OffsetTop, view.OffsetRight, view.OffsetBottom) = (ModDetails.Inset, 14, -ModDetails.Inset, -16);

        var head = new HBoxContainer { Name = "Head" };
        var title = new Label { Text = Menus.Text("SPIRE_MOD_MANAGER.CHANGES_TITLE"), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        title.AddThemeFontOverride("font", ModEntry.Font("res://themes/kreon_bold_shared.tres"));
        title.AddThemeFontSizeOverride("font_size", 30);
        title.AddThemeColorOverride("font_color", Cream);
        // FillList titles it for the list it shows; a long profile name (in the unsaved list's title) wraps.
        (title.Name, title.AutowrapMode) = ("Title", TextServer.AutowrapMode.WordSmart);
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
        if (Copies.RowOf(content, modId) is not { } row)
            return;
        CategoryList.Reveal(content, modId, flash: true);
        CategoryList.Select(screen, row); // fills the panel, which closes the list
        var info = screen.GetNode<Control>("%ModInfoContainer");
        if (info.GetNodeOrNull<Control>(BackName) is not { } back)
        {
            back = Menus.SmallButton(Menus.Text("SPIRE_MOD_MANAGER.BACK_TO_CHANGES"), () => ShowList(screen, _list), name: BackName);
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

    // The active profile's unsaved changes, one line each, by name: "+ Name" turned on, "- Name" turned off (a plain
    // hyphen, in Changes' colours). links: each name opens the mod (the panel's list; the card has none).
    public static List<string> UnsavedLines(bool links)
    {
        var (on, off) = Profiles.UnsavedChanges();
        string Line(string sign, string color, (string Id, string Name) mod) =>
            $"[color={color}]{sign} {(links ? Link(mod.Id, mod.Name) : Escape(mod.Name))}[/color]";
        return on.OrderBy(m => m.Name).Select(m => Line("+", Added, m))
            .Concat(off.OrderBy(m => m.Name).Select(m => Line("-", Removed, m))).ToList();
    }

    // The active profile's unsaved changes, titled with its name. It follows the tickboxes while open: after Save
    // or Discard (or ticking back to the saved state) it says there are none, until it's closed.
    static void FillUnsaved(Control view)
    {
        view.GetNode<Label>("Head/Title").Text = Menus.Format("SPIRE_MOD_MANAGER.UNSAVED_LIST_TITLE", ("name", Profiles.Active?.Name ?? ""));
        var lines = UnsavedLines(links: true);
        view.GetNode<RichTextLabel>("List").Text = lines.Count > 0 ? string.Join("\n", lines) : Menus.Text("SPIRE_MOD_MANAGER.NO_UNSAVED");
    }

    // Sections with counts, each change on its own line (or the unsaved list's lines).
    static void FillList(Control view)
    {
        if (_list == ListKind.Unsaved)
        {
            FillUnsaved(view);
            return;
        }
        view.GetNode<Label>("Head/Title").Text = Menus.Text("SPIRE_MOD_MANAGER.CHANGES_TITLE");
        var entries = Entries();
        var text = new System.Text.StringBuilder();
        void Section(string key, string color, string id)
        {
            var items = entries.Where(e => e.Section == id).ToList();
            if (items.Count == 0)
                return;
            text.Append($"\n[color={color}][b]{Escape(Menus.Format(key, ("count", Menus.Number(items.Count))))}[/b][/color]\n");
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

    // The profile at launch and now, if they differ. The launch profile renamed since (old name -> new) is the
    // same profile, not a switch.
    static (string From, string To)? Switch(IReadOnlyDictionary<string, string> renamed) =>
        History.LaunchProfile is { } launch && Store.Data.ActiveProfile is { } active && active != renamed.GetValueOrDefault(launch, launch)
            ? (launch, active) : null;

    static string Name(Mod mod) => mod.manifest?.name ?? mod.manifest?.id ?? "";

    // Installed copies whose own box differs from a snapshot's (its list and copies off against now's, each read
    // against the copies installed today; no entry = on).
    static List<(Mod Mod, bool Enabled)> ModChanges(History.Snapshot before)
    {
        var (thenOff, thenTicks) = (Dependencies.SavedOff(before.Mods), Store.TicksIn(before.Store));
        var (nowOff, nowTicks) = (Dependencies.SavedOff(), Ticks.Now);
        // A mod installed since launch wasn't loaded then: off, though the saved list (which had no entry for it) reads
        // on. So arriving on counts as turned on, and turning it off again is no change.
        var arrived = Installed.Mods.Select(Copies.SwitchKey).ToHashSet();
        return Dependencies.EachCopy(ModManager.Mods)
            .Select(m => (Mod: m, Then: !arrived.Contains(Copies.SwitchKey(m)) && Copies.TickedIn(m, thenOff, thenTicks), Now: Copies.TickedIn(m, nowOff, nowTicks)))
            .Where(p => p.Then != p.Now)
            .Select(p => (p.Mod, p.Now))
            .ToList();
    }

    // Mods installed more than once whose copy that loads after a relaunch is another than at launch while none of their
    // boxes changed (changed: ModChanges for it), e.g. a copy updated to a higher version: the copy that loaded then, and
    // the one now. Then is the copy the game loaded, else the launch's lists' (launch: History.Loaded): those lists read
    // with today's manifests name the copy a Workshop update made the winner since (Copies.Newest), so the switch to it
    // went unlisted. Now is the one rule's (Copies.Next), as every view has it. Not this mod (the game decides which of
    // its copies loads), nor an id the launch couldn't choose for (SameIdLoad.Unchosen): the game's own choice loads then
    // and now.
    static List<(Mod From, Mod To)> CopySwitches(History.Snapshot launch, List<(Mod Mod, bool Enabled)> changed)
    {
        var (thenOff, thenTicks) = (Dependencies.SavedOff(launch.Mods), Store.TicksIn(launch.Store));
        var (nowOff, nowTicks) = (Dependencies.SavedOff(), Ticks.Now);
        var ticked = changed.Select(c => c.Mod.manifest?.id).ToHashSet();
        Mod? Then(string id) => ModManager.Mods.FirstOrDefault(m => m.manifest?.id == id && m.state == ModLoadState.Loaded) ?? Copies.LoadingIn(id, thenOff, thenTicks);
        var switches = new List<(Mod, Mod)>();
        foreach (var id in Dependencies.EachCopy(ModManager.Mods).Where(m => Copies.Others(m).Count > 0).Select(m => m.manifest?.id).OfType<string>().Distinct())
            if (id != ModEntry.ModId && !ticked.Contains(id) && !SameIdLoad.Unchosen(id) && Then(id) is { } from
                && Copies.LoadingIn(id, nowOff, nowTicks) is { } to && Copies.CopyOf(from) != Copies.CopyOf(to))
                switches.Add((from, to));
        return switches;
    }

    // A switch to another copy of a mod with no box changed, as Changes words it ("Test Twin: Test Twin (SmmTestTwinA)
    // loads instead of Test Twin Beta (SmmTestTwinB)"): plain, and rich with each copy's name opening it.
    public static (string Plain, string Rich) CopySwitch(Mod from, Mod to) =>
        (Menus.Format("SPIRE_MOD_MANAGER.CHANGE_COPY_SWITCH", ("mod", Name(to)), ("to", Copies.Named(to)), ("from", Copies.Named(from))),
            Menus.Format("SPIRE_MOD_MANAGER.CHANGE_COPY_SWITCH", ("mod", Escape(Name(to))), ("to", Link(Copies.LinkKey(to), Copies.Named(to))),
                ("from", Link(Copies.LinkKey(from), Copies.Named(from)))));

    // Installed copies whose place in the load order changed since launch: (copy, position then, now), 1-based among the
    // copies installed, as Load Order numbers its rows (two copies of one mod each their own place, LoadOrder.Places;
    // then with the launch's copy order, now with today's; two that only traded places are no move, LoadOrder.MovedSince).
    public static List<(Mod Mod, int Before, int After)> Moves() => History.Loaded is { } loaded
        ? LoadOrder.MovedSince(loaded.Mods.Select(m => ((string?)m.Id, m.Source)), loaded.CopyOrder, Dependencies.EachCopy(ModManager.Mods))
        : [];

    // Mods whose details changed: which ones (category, alias, notes, tags) and the alias now; profiles added,
    // deleted or renamed (lines), and the renames (old name -> new).
    static (IReadOnlyList<(string Id, IReadOnlyList<string> Fields, string? Alias)> Edited, List<string> Profiles,
        IReadOnlyDictionary<string, string> Renamed) DataChanges(string before, string after)
    {
        if (_compared is not { } last || last.Before != before || last.After != after)
            _compared = last = Compare(before, after);
        // Worded in the game's language as it is now (the comparison is kept, not its words).
        var profiles = last.Renamed.Select(r => Menus.Format("SPIRE_MOD_MANAGER.PROFILE_RENAMED", ("from", r.Key), ("to", r.Value)))
            .Concat(last.Added.Select(n => Menus.Format("SPIRE_MOD_MANAGER.PROFILE_ADDED", ("name", n))))
            .Concat(last.Gone.Select(n => Menus.Format("SPIRE_MOD_MANAGER.PROFILE_REMOVED", ("name", n))))
            .ToList();
        return (last.Edited, profiles, last.Renamed);
    }

    record Compared(string Before, string After, IReadOnlyList<(string Id, IReadOnlyList<string> Fields, string? Alias)> Edited,
        IReadOnlyDictionary<string, string> Renamed, IReadOnlyList<string> Added, IReadOnlyList<string> Gone);

    // The last comparison: the bar, its list, its summary and the Changed filter compare the same data in turn. Read
    // only, as every caller shares it.
    static Compared? _compared;
    static bool _compareFailed;

    static Compared Compare(string before, string after)
    {
        try
        {
            var a = JsonNode.Parse(before)?.AsObject();
            var b = JsonNode.Parse(after)?.AsObject();
            var modsA = a?["mods"] as JsonObject ?? [];
            var modsB = b?["mods"] as JsonObject ?? [];
            var edited = new List<(string Id, IReadOnlyList<string> Fields, string? Alias)>();
            foreach (var id in modsA.Select(p => p.Key).Union(modsB.Select(p => p.Key)))
            {
                var fields = new[] { "category", "alias", "notes", "tags" }
                    .Where(f => !JsonNode.DeepEquals(modsA[id]?[f], modsB[id]?[f])).ToList();
                if (fields.Count > 0)
                    edited.Add((id, fields, (string?)modsB[id]?["alias"]));
            }
            JsonArray ProfilesIn(JsonObject? o) => o?["profiles"] as JsonArray ?? [];
            HashSet<string> Names(JsonObject? o) => ProfilesIn(o).Select(p => (string?)p?["name"] ?? "").ToHashSet();
            JsonNode? ProfileIn(JsonObject? o, string name) => ProfilesIn(o).FirstOrDefault(p => (string?)p?["name"] == name);
            var added = Names(b).Except(Names(a)).ToList();
            var gone = Names(a).Except(Names(b)).ToList();
            // A name gone and a name added with the same id are one profile, renamed, saved since or not. Without ids
            // (a file from before them, at launch): the same save time, or for several never saved (imported, no save
            // time), the same mods.
            var renamed = new Dictionary<string, string>();
            foreach (var name in added.ToList())
            {
                bool Same(string old, string field) => JsonNode.DeepEquals(ProfileIn(a, old)?[field], ProfileIn(b, name)?[field]);
                var byId = (string?)ProfileIn(b, name)?["id"] is { Length: > 0 } id ? gone.Where(old => (string?)ProfileIn(a, old)?["id"] == id).ToList() : [];
                var candidates = byId.Count > 0 ? byId : gone.Where(old => Same(old, "saved_at")).ToList();
                if ((candidates.Count == 1 ? candidates : candidates.Where(old => Same(old, "mods")).ToList()) is [var was])
                {
                    renamed[was] = name;
                    added.Remove(name);
                    gone.Remove(was);
                }
            }
            return new Compared(before, after, edited, renamed, added, gone);
        }
        catch (Exception e)
        {
            if (!_compareFailed)
            {
                _compareFailed = true; // once a session: it's asked on every change
                Log.Warn($"[{ModEntry.ModId}] Could not compare our data with launch, so Changes leaves out edits and profiles: {e}");
            }
            return new Compared(before, after, [], new Dictionary<string, string>(), [], []);
        }
    }

    static string Escape(string text) => text.Replace("[", "[lb]");

    // A shortcut under its button: centred, no wider than the button less a little each side (in a wider font, two side
    // by side ran together), and smaller to fit down to HintSmallest; its line keeps its height.
    public const int HintInset = 4, HintSmallest = 9;
    static void FitHint(Label hint, Control button)
    {
        var room = button.GetCombinedMinimumSize().X - 2 * HintInset;
        var font = hint.GetThemeFont("font");
        var size = hint.GetThemeFontSize("font_size");
        hint.CustomMinimumSize = new Vector2(0, hint.GetMinimumSize().Y);
        while (size > HintSmallest && font.GetStringSize(Menus.WholeText(hint), fontSize: size).X > room)
            size--;
        hint.AddThemeFontSizeOverride("font_size", size);
    }

    static void Style(Label label, int size, Color color)
    {
        label.AddThemeFontOverride("font", ModEntry.Font("res://themes/kreon_regular_shared.tres"));
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", color);
    }
}
