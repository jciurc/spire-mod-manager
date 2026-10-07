using Godot;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;

namespace SpireModManager;

// Undo, Redo and Changes, left of Relaunch Game. Changes opens a panel over the mod list listing what
// differs from what this session loaded (mods turned on or off, load order) with Revert all, which puts
// the game's mod list back as it was at launch (and can itself be undone).
static class HistoryBar
{
    public const string BarName = "SpireModManagerHistory";
    const string PanelName = "SpireModManagerChanges";
    static readonly Color Cream = new(1, 0.964706f, 0.886275f);
    static readonly Color Gold = new(0.937f, 0.784f, 0.318f);

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
        var changes = Menus.SmallButton(Menus.Text("SPIRE_MOD_MANAGER.CHANGES"), () => Toggle(screen), name: "ChangesButton");
        foreach (var button in new[] { undo, redo, changes })
        {
            button.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            button.AddThemeFontSizeOverride("font_size", 18);
            bar.AddChild(button);
        }

        void Update()
        {
            undo.Disabled = !History.CanUndo;
            redo.Disabled = !History.CanRedo;
            if (screen.GetNodeOrNull<Control>(PanelName) is { } open)
                Fill(screen, open);
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

    public static Control? Toggle(NModdingScreen screen)
    {
        if (screen.GetNodeOrNull<Control>(PanelName) is { } open)
        {
            open.QueueFree();
            return null;
        }
        var border = screen.GetNode<Control>("ModsBorder");
        var panel = new PanelContainer { Name = PanelName };
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.0756f, 0.12084f, 0.18f, 0.97f), BorderColor = new Color(Gold, 0.6f),
            ContentMarginLeft = 24, ContentMarginRight = 24, ContentMarginTop = 16, ContentMarginBottom = 16,
        };
        style.SetBorderWidthAll(2);
        panel.AddThemeStyleboxOverride("panel", style);
        screen.AddChild(panel);
        panel.Position = new Vector2(border.OffsetLeft + 40, border.OffsetTop + 60);
        panel.Size = new Vector2(border.OffsetRight - border.OffsetLeft - 80, 0);
        Fill(screen, panel);
        return panel;
    }

    // Heading, one line per change, then Revert all and Close.
    static void Fill(NModdingScreen screen, Control panel)
    {
        foreach (var child in panel.GetChildren())
        {
            panel.RemoveChild(child);
            child.QueueFree();
        }
        var lines = new VBoxContainer { Name = "Lines" };
        lines.AddThemeConstantOverride("separation", 6);
        panel.AddChild(lines);
        lines.AddChild(Text(Menus.Text("SPIRE_MOD_MANAGER.CHANGES_TITLE"), 26, Gold, bold: true));

        var changes = Describe();
        foreach (var line in changes.DefaultIfEmpty(Menus.Text("SPIRE_MOD_MANAGER.NO_CHANGES")))
            lines.AddChild(Text(line, 20, Cream));

        var buttons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        buttons.AddThemeConstantOverride("separation", 8);
        buttons.AddChild(Menus.SmallButton(Menus.Text("SPIRE_MOD_MANAGER.REVERT_ALL"), History.RevertMods,
            disabled: changes.Count == 0, name: "RevertAllButton"));
        buttons.AddChild(Menus.SmallButton(Menus.Text("SPIRE_MOD_MANAGER.CLOSE"), () => panel.QueueFree(), name: "CloseButton"));
        lines.AddChild(buttons);
    }

    // What differs from launch, in words: one line per mod switched on or off, one for the load order.
    public static List<string> Describe()
    {
        if (History.Loaded is not { } loaded)
            return [];
        var before = loaded.Mods.ToDictionary(m => (m.Id, m.Source));
        var now = History.CurrentMods();
        string Name(string id) => Dependencies.Find(id)?.manifest?.name ?? id;
        bool Installed(History.ModEntry m) => ModManager.Mods.Any(x => x.manifest?.id == m.Id && x.modSource == m.Source);

        var lines = new List<string>();
        foreach (var mod in now.Where(Installed))
        {
            var was = before.TryGetValue((mod.Id, mod.Source), out var b) ? b.Enabled : true; // no entry = on
            if (was != mod.Enabled)
                lines.Add(Menus.Format(mod.Enabled ? "SPIRE_MOD_MANAGER.CHANGE_ON" : "SPIRE_MOD_MANAGER.CHANGE_OFF", ("mod", Name(mod.Id))));
        }
        var orderBefore = loaded.Mods.Where(Installed).Select(m => (m.Id, m.Source)).ToList();
        var orderNow = now.Where(Installed).Select(m => (m.Id, m.Source)).ToList();
        var moved = orderNow.Where((m, i) => i >= orderBefore.Count || orderBefore[i] != m).Count();
        if (moved > 0)
            lines.Add(Menus.Format("SPIRE_MOD_MANAGER.CHANGE_ORDER", ("count", moved.ToString())));
        return lines;
    }

    static Label Text(string text, int size, Color color, bool bold = false)
    {
        var label = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        label.AddThemeFontOverride("font", ResourceLoader.Load<Font>(bold ? "res://themes/kreon_bold_shared.tres" : "res://themes/kreon_regular_shared.tres"));
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", color);
        return label;
    }
}
