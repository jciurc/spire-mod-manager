using Godot;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;

namespace SpireModManager;

// The check before Relaunch Game: enabled mods that won't load (a mod they need is off or missing). With none it
// relaunches at once; otherwise a dialog lists them, with fixes, Relaunch anyway and Cancel. Cancel (first, at the
// left) undoes the fixes made in the dialog.
static class RelaunchCheck
{
    public const string Name = "SpireModManagerRelaunchCheck";
    const string ProblemHex = "#ff8070";

    public static bool IsOpen => Modal.IsOpen(Name);

    static History.Point? _mark; // the history as the dialog opened (History.Mark): Cancel undoes back to it

    // owner: the page asking (the Modding screen or the main menu); the dialog closes when it goes.
    public static void Relaunch(Node owner)
    {
        if (Problems().Count == 0)
            ModEntry.Relaunch();
        else
            Show(owner);
    }

    // Enabled mods with a dependency that's off or missing, each with the info panel's reason.
    static List<(Mod Mod, string Why)> Problems() =>
        ModManager.Mods.Union(Installed.Mods).Where(m => m.manifest?.id != null)
            .SelectMany(m => ModDetails.DependencyProblems(m).Select(why => (m, why)))
            .ToList();

    public static void Show(Node owner)
    {
        if (!IsOpen)
            _mark = History.Mark();
        if (Modal.Open(owner, Name, 640, 14) is { } column)
            Fill(owner, column);
    }

    // The dialog's contents for the current state (again after each fix).
    static void Fill(Node owner, VBoxContainer column)
    {
        foreach (var child in column.GetChildren())
        {
            column.RemoveChild(child);
            child.QueueFree();
        }
        var problems = Problems();
        column.AddChild(Modal.Title("SPIRE_MOD_MANAGER.RELAUNCH_CHECK_TITLE")); // the same once all is well

        var body = new RichTextLabel { Name = "Body", BbcodeEnabled = true, FitContent = true, ScrollActive = false, MouseFilter = Control.MouseFilterEnum.Ignore };
        Hover.Fonts(body, 18);
        string Escape(string text) => text.Replace("[", "[lb]");
        body.Text = problems.Count == 0
            ? Menus.Text("SPIRE_MOD_MANAGER.RELAUNCH_CHECK_OK")
            : Menus.Text("SPIRE_MOD_MANAGER.RELAUNCH_CHECK_INTRO") + "\n" + string.Join("\n", problems.GroupBy(p => p.Mod).Select(g =>
                $"  •  [b]{Escape(g.Key.manifest?.name ?? g.Key.manifest?.id ?? "")}[/b]  [color={ProblemHex}]{Escape(string.Join("; ", g.Select(p => p.Why)))}[/color]"));
        column.AddChild(body);

        // The Modding page, open or hidden behind the main menu: a fix ticks its boxes and its redraw records it. Before
        // the page is opened this session there's none, and the profile and history take the change directly.
        var screen = owner as NModdingScreen ?? HistoryBar.Screen;
        void Fix(IEnumerable<Mod> mods, bool on)
        {
            if (screen != null && GodotObject.IsInstanceValid(screen))
            {
                Dependencies.Set(mods, on, screen);
                CategoryList.Redraw(screen.GetNode<Control>("%ModsScrollContainer/Mask/Content")); // one undoable change
            }
            else
            {
                Dependencies.Set(mods, on, null);
                Profiles.Track();
                History.Record(); // one undoable change (Track only records when the active profile changed)
            }
            Fill(owner, column);
        }
        var buttons = new HBoxContainer { Name = "Buttons" };
        buttons.AddThemeConstantOverride("separation", 8);
        void Add(string key, Action action, string name)
        {
            var button = Menus.SmallButton(Menus.Text(key), action, name: name);
            button.AddThemeFontSizeOverride("font_size", 18);
            button.CustomMinimumSize = new Vector2(0, 34);
            buttons.AddChild(button);
        }
        // Cancel at the left, then the fixes and Relaunch at the right.
        Add("SPIRE_MOD_MANAGER.CANCEL", () => Cancel(), "CancelRelaunchButton");
        buttons.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore });
        var needed = Dependencies.NeededOff(ModManager.Mods.Union(Installed.Mods));
        if (needed.Count > 0)
            Add("SPIRE_MOD_MANAGER.ENABLE_NEEDED", () => Fix(needed, true), "EnableNeededButton");
        if (problems.Count > 0)
            Add("SPIRE_MOD_MANAGER.DISABLE_BROKEN", () => Fix(problems.Select(p => p.Mod).Distinct(), false), "DisableBrokenButton");
        if (needed.Count > 0 || problems.Count > 0) // a gap between the fixes and Relaunch
            buttons.AddChild(new Control { CustomMinimumSize = new Vector2(24, 0), MouseFilter = Control.MouseFilterEnum.Ignore });
        Add(problems.Count > 0 ? "SPIRE_MOD_MANAGER.RELAUNCH_ANYWAY" : "SPIRE_MOD_MANAGER.RELAUNCH_BUTTON", ModEntry.Relaunch, "RelaunchNowButton");
        column.AddChild(buttons);
    }

    // Cancel or Esc: the fixes made in the dialog undone, then closed. True when there was one to close.
    public static bool Cancel()
    {
        if (!IsOpen)
            return false;
        if (_mark is { } mark)
            History.UndoTo(mark);
        _mark = null;
        return Close();
    }

    // Closed as it is, fixes kept (the page it came from going; the self-test). True when there was one to close.
    public static bool Close()
    {
        _mark = null;
        return Modal.Close(Name);
    }
}
