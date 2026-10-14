using Godot;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;

namespace SpireModManager;

// Before Relaunch Game (the Modding page's button, or Relaunch game in the main menu button's right-click
// menu): enabled mods that won't load as things stand (a mod they need is off or not installed). Nothing
// wrong: relaunch at once. Otherwise a dialog over the page it came from lists them and offers to enable the
// needed mods, disable the ones that can't load, relaunch anyway, or cancel (Esc too). A fix updates the list
// in place (and is undoable like any change); once nothing is wrong it offers a plain Relaunch. Cancel (first, at the
// left: Jordan, 2026-10-14) takes back the fixes made in the dialog, so cancelling leaves everything as it was.
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

    // Enabled mods (installed now or this session) with a dependency that is off or missing, and why.
    static List<(Mod Mod, string Why)> Problems() =>
        ModManager.Mods.Union(Installed.Mods).Where(m => m.manifest?.id != null && Dependencies.IsOn(m))
            .SelectMany(m => Dependencies.Of(m).Select(id => (Id: id, Mod: Dependencies.Find(id)))
                .Where(d => d.Mod == null || !Dependencies.IsOn(d.Mod))
                .Select(d => (m, Menus.Format(d.Mod == null ? "SPIRE_MOD_MANAGER.PROBLEM_DEP_MISSING" : "SPIRE_MOD_MANAGER.PROBLEM_DEP_OFF",
                    ("mod", d.Mod?.manifest?.name ?? d.Id)))))
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
        column.AddChild(Modal.Title(problems.Count > 0 ? "SPIRE_MOD_MANAGER.RELAUNCH_CHECK_TITLE" : "SPIRE_MOD_MANAGER.RELAUNCH_CHECK_OK_TITLE"));

        var body = new RichTextLabel { Name = "Body", BbcodeEnabled = true, FitContent = true, ScrollActive = false, MouseFilter = Control.MouseFilterEnum.Ignore };
        Hover.Fonts(body, 18);
        string Escape(string text) => text.Replace("[", "[lb]");
        body.Text = problems.Count == 0
            ? Menus.Text("SPIRE_MOD_MANAGER.RELAUNCH_CHECK_OK")
            : Menus.Text("SPIRE_MOD_MANAGER.RELAUNCH_CHECK_INTRO") + "\n" + string.Join("\n", problems.GroupBy(p => p.Mod).Select(g =>
                $"  •  [b]{Escape(g.Key.manifest?.name ?? g.Key.manifest?.id ?? "")}[/b]  [color={ProblemHex}]{Escape(string.Join("; ", g.Select(p => p.Why)))}[/color]"));
        column.AddChild(body);

        // The Modding page, open or hidden behind the main menu (the game keeps it for the next visit): a fix ticks
        // its boxes, and its redraw records the fix. Before the page has been opened this session there is none: the
        // active profile and the history take the change directly.
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
