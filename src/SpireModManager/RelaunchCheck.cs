using Godot;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;

namespace SpireModManager;

// Before Relaunch Game: enabled mods that won't load as things stand (a mod they need is off or not
// installed). Nothing wrong: relaunch at once. Otherwise a dialog lists them and offers to enable the needed
// mods, disable the ones that can't load, relaunch anyway, or cancel (Esc too). A fix updates the list in
// place (and is undoable like any change); once nothing is wrong it offers a plain Relaunch.
static class RelaunchCheck
{
    public const string Name = "SpireModManagerRelaunchCheck";
    static readonly Color Cream = new(1, 0.964706f, 0.886275f);
    static readonly Color Gold = new(0.937f, 0.784f, 0.318f);
    const string ProblemHex = "#ff8070";

    public static bool IsOpen => Root()?.GetNodeOrNull(Name) != null;

    public static void Relaunch(NModdingScreen screen)
    {
        if (Problems().Count == 0)
            ModEntry.Relaunch();
        else
            Show(screen);
    }

    // Enabled mods (installed now or this session) with a dependency that is off or missing, and why.
    static List<(Mod Mod, string Why)> Problems() =>
        ModManager.Mods.Union(Installed.Mods).Where(m => m.manifest?.id != null && Dependencies.IsOn(m))
            .SelectMany(m => Dependencies.Of(m).Select(id => (Id: id, Mod: Dependencies.Find(id)))
                .Where(d => d.Mod == null || !Dependencies.IsOn(d.Mod))
                .Select(d => (m, Menus.Format(d.Mod == null ? "SPIRE_MOD_MANAGER.PROBLEM_DEP_MISSING" : "SPIRE_MOD_MANAGER.PROBLEM_DEP_OFF",
                    ("mod", d.Mod?.manifest?.name ?? d.Id)))))
            .ToList();

    public static void Show(NModdingScreen screen)
    {
        Close();
        if (Root() is not { } root)
            return;
        var layer = new CanvasLayer { Name = Name, Layer = 85 };
        root.AddChild(layer);
        screen.TreeExiting += () => Close();

        // Dim everything behind, and take its clicks.
        var size = screen.GetViewport().GetVisibleRect().Size; // anchors don't size controls right under a CanvasLayer here
        var dim = new ColorRect { Color = new Color(0, 0, 0, 0.6f), MouseFilter = Control.MouseFilterEnum.Stop, Size = size };
        layer.AddChild(dim);
        var center = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore, Size = size };
        layer.AddChild(center);

        var panel = new PanelContainer { CustomMinimumSize = new Vector2(640, 0) };
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
        column.AddThemeConstantOverride("separation", 14);
        panel.AddChild(column);
        Fill(screen, column);
    }

    // The dialog's contents for the current state (again after each fix).
    static void Fill(NModdingScreen screen, VBoxContainer column)
    {
        foreach (var child in column.GetChildren())
        {
            column.RemoveChild(child);
            child.QueueFree();
        }
        var problems = Problems();
        var title = new Label { Name = "Title", Text = Menus.Text(problems.Count > 0 ? "SPIRE_MOD_MANAGER.RELAUNCH_CHECK_TITLE" : "SPIRE_MOD_MANAGER.RELAUNCH_CHECK_OK_TITLE") };
        title.AddThemeFontOverride("font", ModEntry.Font("res://themes/kreon_bold_shared.tres"));
        title.AddThemeFontSizeOverride("font_size", 26);
        title.AddThemeColorOverride("font_color", Cream);
        column.AddChild(title);

        var body = new RichTextLabel { Name = "Body", BbcodeEnabled = true, FitContent = true, ScrollActive = false, MouseFilter = Control.MouseFilterEnum.Ignore };
        Hover.Fonts(body, 18);
        string Escape(string text) => text.Replace("[", "[lb]");
        body.Text = problems.Count == 0
            ? Menus.Text("SPIRE_MOD_MANAGER.RELAUNCH_CHECK_OK")
            : Menus.Text("SPIRE_MOD_MANAGER.RELAUNCH_CHECK_INTRO") + "\n" + string.Join("\n", problems.GroupBy(p => p.Mod).Select(g =>
                $"  •  [b]{Escape(g.Key.manifest?.name ?? g.Key.manifest?.id ?? "")}[/b]  [color={ProblemHex}]{Escape(string.Join("; ", g.Select(p => p.Why)))}[/color]"));
        column.AddChild(body);

        var content = screen.GetNode<Control>("%ModsScrollContainer/Mask/Content");
        void Fix(IEnumerable<Mod> mods, bool on)
        {
            Dependencies.Set(mods, on, screen);
            CategoryList.Redraw(content); // one undoable change
            Fill(screen, column);
        }
        var buttons = new HBoxContainer { Name = "Buttons", Alignment = BoxContainer.AlignmentMode.End };
        buttons.AddThemeConstantOverride("separation", 8);
        void Add(string key, Action action, string name)
        {
            var button = Menus.SmallButton(Menus.Text(key), action, name: name);
            button.AddThemeFontSizeOverride("font_size", 18);
            button.CustomMinimumSize = new Vector2(0, 34);
            buttons.AddChild(button);
        }
        var needed = Dependencies.NeededOff(ModManager.Mods.Union(Installed.Mods));
        if (needed.Count > 0)
            Add("SPIRE_MOD_MANAGER.ENABLE_NEEDED", () => Fix(needed, true), "EnableNeededButton");
        if (problems.Count > 0)
            Add("SPIRE_MOD_MANAGER.DISABLE_BROKEN", () => Fix(problems.Select(p => p.Mod).Distinct(), false), "DisableBrokenButton");
        buttons.AddChild(new Control { CustomMinimumSize = new Vector2(24, 0), MouseFilter = Control.MouseFilterEnum.Ignore });
        Add("SPIRE_MOD_MANAGER.CANCEL", () => Close(), "CancelRelaunchButton");
        Add(problems.Count > 0 ? "SPIRE_MOD_MANAGER.RELAUNCH_ANYWAY" : "SPIRE_MOD_MANAGER.RELAUNCH_BUTTON", ModEntry.Relaunch, "RelaunchNowButton");
        column.AddChild(buttons);
    }

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
