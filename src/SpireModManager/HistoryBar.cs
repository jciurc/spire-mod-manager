using System.Text.Json.Nodes;
using Godot;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;

namespace SpireModManager;

// Two pieces:
// - The undo bar, left of Relaunch Game: Undo, Redo (their shortcuts underneath), Revert.
// - "Changes +4 -3" right of the page title: how many mods are on and off
//   compared with launch. Hovering it shows the details, anchored to it: switched profile, each mod
//   enabled or disabled, load order moves, edited mods and profiles.
static class HistoryBar
{
    public const string BarName = "SpireModManagerHistory";
    public const string ChangesName = "SpireModManagerChanges";
    const string PanelName = "SpireModManagerChangesPanel";
    static readonly Color Cream = new(1, 0.964706f, 0.886275f);
    static readonly Color Gold = new(0.937f, 0.784f, 0.318f);
    const string Added = "#a6e3a1", Removed = "#f2a3a3", GameplayName = "#f5c08f", VisualName = "#a8d0f0";

    // The screen our keyboard shortcuts act on, while it exists.
    public static NModdingScreen? Screen { get; private set; }

    public static void Attach(NModdingScreen screen)
    {
        var content = screen.GetNode<Control>("%ModsScrollContainer/Mask/Content");

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
            ScrollActive = false, Size = new Vector2(400, 28), MouseFilter = Control.MouseFilterEnum.Stop,
            MouseDefaultCursorShape = Control.CursorShape.Help,
        };
        changes.AddThemeFontOverride("normal_font", ResourceLoader.Load<Font>("res://themes/kreon_regular_shared.tres"));
        changes.AddThemeFontSizeOverride("normal_font_size", 18);
        changes.AddThemeColorOverride("default_color", Cream);
        screen.AddChild(changes);
        changes.MouseEntered += () => ShowDetails(screen, changes);
        changes.MouseExited += () => screen.GetNodeOrNull(PanelName)?.QueueFree();

        void Update()
        {
            undo.Disabled = !History.CanUndo;
            redo.Disabled = !History.CanRedo;
            revert.Disabled = Describe().Count == 0;
            var (on, off) = Counts();
            changes.Text = $"{Menus.Text("SPIRE_MOD_MANAGER.CHANGES")} [color={Added}]+{on}[/color] [color={Removed}]-{off}[/color]";
        }
        // After undo/redo/revert, everything on screen redraws from the restored state.
        void Restored()
        {
            Profiles.RefreshTickboxes(screen);
            CategoryList.Redraw(content);
            Profiles.NotifySwitched();
            screen.GetNode<NModInfoContainer>("%ModInfoContainer").Clear();
        }
        History.Changed += Update;
        History.Restored += Restored;
        Screen = screen;
        bar.TreeExiting += () =>
        {
            if (Screen == screen)
                Screen = null;
            History.Changed -= Update;
            History.Restored -= Restored;
        };
        Update();
    }

    // The details panel, just under the Changes line.
    static void ShowDetails(NModdingScreen screen, Control anchor)
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
        var text = new RichTextLabel
        {
            BbcodeEnabled = true, FitContent = true, ScrollActive = false, CustomMinimumSize = new Vector2(420, 0),
            MouseFilter = Control.MouseFilterEnum.Ignore, Text = string.Join("\n", Lines().Select(l => l.Rich)),
        };
        text.AddThemeFontOverride("normal_font", ResourceLoader.Load<Font>("res://themes/kreon_regular_shared.tres"));
        text.AddThemeFontOverride("bold_font", ResourceLoader.Load<Font>("res://themes/kreon_bold_shared.tres"));
        text.AddThemeFontSizeOverride("normal_font_size", 17);
        text.AddThemeFontSizeOverride("bold_font_size", 17);
        text.AddThemeColorOverride("default_color", Cream);
        panel.AddChild(text);
        screen.AddChild(panel);
        panel.Position = anchor.Position + new Vector2(0, anchor.Size.Y + 4);
    }

    // Mods on and off compared with launch.
    public static (int On, int Off) Counts()
    {
        var changed = ModChanges(History.Loaded?.Mods ?? []);
        return (changed.Count(c => c.Enabled), changed.Count(c => !c.Enabled));
    }

    // The overlay's lines as plain text (also what the self-test reads).
    public static List<string> Describe() => Lines().Where(l => l.Counts).Select(l => l.Plain).ToList();

    record Line(string Plain, string Rich, bool Counts = true);

    static List<Line> Lines()
    {
        if (History.Loaded is not { } loaded)
            return [];
        var lines = new List<Line>();

        // On a different profile than at launch: one "switched" line, then only what changed after the last
        // switch (instead of every mod the switch turned on or off).
        var since = loaded.Mods;
        if (History.LastSwitch is { } switched && History.LaunchProfile is { } launch && Store.Data.ActiveProfile is { } active
            && active != launch)
        {
            lines.Add(new Line(Menus.Format("SPIRE_MOD_MANAGER.CHANGE_SWITCH", ("from", launch), ("to", active)),
                Menus.Format("SPIRE_MOD_MANAGER.CHANGE_SWITCH", ("from", $"[b]{Escape(launch)}[/b]"), ("to", $"[b]{Escape(active)}[/b]"))));
            since = switched.After;
        }
        foreach (var (mod, enabled) in ModChanges(since))
        {
            var key = enabled ? "SPIRE_MOD_MANAGER.CHANGE_ON" : "SPIRE_MOD_MANAGER.CHANGE_OFF";
            var name = mod.manifest?.name ?? mod.manifest?.id ?? "";
            var nameColor = mod.manifest?.affectsGameplay ?? true ? GameplayName : VisualName;
            lines.Add(new Line(Menus.Format(key, ("mod", name)),
                $"[color={(enabled ? Added : Removed)}]{Menus.Format(key, ("mod", $"[color={nameColor}]{Escape(name)}[/color]"))}[/color]"));
        }

        var installed = (Func<History.ModEntry, bool>)(m => ModManager.Mods.Any(x => x.manifest?.id == m.Id && x.modSource == m.Source));
        var orderBefore = loaded.Mods.Where(installed).Select(m => (m.Id, m.Source)).ToList();
        var orderNow = History.CurrentMods().Where(installed).Select(m => (m.Id, m.Source)).ToList();
        var moved = orderNow.Where((m, i) => i >= orderBefore.Count || orderBefore[i] != m).Count();
        if (moved > 0)
            lines.Add(Plain(Menus.Format("SPIRE_MOD_MANAGER.CHANGE_ORDER", ("count", moved.ToString()))));

        var (edited, profiles) = DataChanges(loaded.Store, Store.EditsJson());
        if (edited > 0)
            lines.Add(Plain(Menus.Format("SPIRE_MOD_MANAGER.CHANGE_EDITED", ("count", edited.ToString()))));
        if (profiles > 0)
            lines.Add(Plain(Menus.Format("SPIRE_MOD_MANAGER.CHANGE_PROFILES", ("count", profiles.ToString()))));

        if (lines.Count == 0)
            lines.Add(new Line("", Menus.Text("SPIRE_MOD_MANAGER.NO_CHANGES"), Counts: false));
        return lines;
    }

    static Line Plain(string text) => new(text, Escape(text));

    // Installed mods whose on/off differs from the given list (no entry = on).
    static List<(Mod Mod, bool Enabled)> ModChanges(IReadOnlyList<History.ModEntry> before)
    {
        var then = before.ToDictionary(m => (m.Id, m.Source), m => m.Enabled);
        var now = History.CurrentMods().ToDictionary(m => (m.Id, m.Source), m => m.Enabled);
        return ModManager.Mods.Where(m => m.manifest?.id != null)
            .Select(m => (Mod: m, Key: (m.manifest?.id ?? "", m.modSource)))
            .Select(p => (p.Mod, Then: then.GetValueOrDefault(p.Key, true), Now: now.GetValueOrDefault(p.Key, true)))
            .Where(p => p.Then != p.Now)
            .Select(p => (p.Mod, p.Now))
            .ToList();
    }

    // Mods whose category, alias, notes or tags changed; profiles added, removed or renamed.
    static (int Edited, int Profiles) DataChanges(string before, string after)
    {
        try
        {
            var a = JsonNode.Parse(before)?.AsObject();
            var b = JsonNode.Parse(after)?.AsObject();
            var modsA = a?["mods"] as JsonObject ?? [];
            var modsB = b?["mods"] as JsonObject ?? [];
            var edited = modsA.Select(p => p.Key).Union(modsB.Select(p => p.Key))
                .Count(id => !JsonNode.DeepEquals(modsA[id], modsB[id]));
            HashSet<string> Names(JsonObject? o) => (o?["profiles"] as JsonArray ?? []).Select(p => (string?)p?["name"] ?? "").ToHashSet();
            var profiles = Names(a).Except(Names(b)).Count() + Names(b).Except(Names(a)).Count();
            return (edited, profiles);
        }
        catch (Exception)
        {
            return (0, 0);
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
