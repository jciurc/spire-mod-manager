using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Localization.Fonts;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.addons.mega_text;

namespace SpireModManager;

[ModInitializer(nameof(Init))]
public static class ModEntry
{
    public const string ModId = "SpireModManager";

    const string ButtonScenePath = $"res://{ModId}/scenes/menu_button.tscn";
    const float ButtonArtHeight = 110; // event_button.png is 284x110

    // Patch classes that didn't apply, by name: a game update renamed or removed what they hook.
    static readonly List<string> _failedPatches = [];
    public static IReadOnlyList<string> FailedPatches => _failedPatches;

    // Dev runs: the patches SPIRE_MOD_MANAGER_SKIP_PATCHES leaves out (they count as failed).
    public static IReadOnlyList<string> SkippedPatches { get; private set; } = [];

    // The patches our Modding page can't do without: the page itself; the main menu's startup (the baseline for
    // undo, Changes and the load order); the tickbox hook (profiles and undo follow it); the info panel's contents;
    // the selected mod (view switches and panel refreshes; our code calls the same game method). With one of them
    // missing, the page stays the game's, with a line saying the mod needs an update, rather than half of ours.
    // The others only add to it (looks, shortcuts, new-mod handling); without one, the page still runs.
    public static readonly IReadOnlyList<string> PageNeeds =
        [nameof(MainMenuPatch), nameof(ModdingScreenPatch), nameof(ModToggledPatch), nameof(ModInfoFillPatch), nameof(RowSelectedPatch)];

    public static bool PageWorks(IEnumerable<string> failed) => !failed.Any(name => PageNeeds.Contains(name));

    // Our page on the Modding screen, or the game's with the needs-update line.
    public static bool OurPage => PageWorks(_failedPatches);

    // That line, across the top of the game's page, above its list and info panel (their boxes: 255..1727, from y 83).
    public const string NeedsUpdateName = "SpireModManagerNeedsUpdate";

    public static Label ShowNeedsUpdate(Control screen)
    {
        if (screen.GetNodeOrNull<Label>(NeedsUpdateName) is { } shown)
            return shown;
        var line = new Label
        {
            Name = NeedsUpdateName, Text = Menus.Text("SPIRE_MOD_MANAGER.NEEDS_UPDATE"), VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore, Position = new Vector2(255, 24), Size = new Vector2(1727 - 255, 48),
        };
        line.AddThemeFontOverride("font", Font("res://themes/kreon_bold_shared.tres"));
        line.AddThemeFontSizeOverride("font_size", 22);
        line.AddThemeColorOverride("font_color", new Color(0.937f, 0.784f, 0.318f));
        line.AddThemeColorOverride("font_outline_color", new Color(0.0756f, 0.12084f, 0.18f));
        line.AddThemeConstantOverride("outline_size", 6);
        screen.AddChild(line);
        return line;
    }

    // Each patch class on its own, so one a game update breaks leaves the others working (PatchAll stops at the
    // first failure). The launch record goes first, then watching for new mods: neither needs a patch.
    public static void Init()
    {
        try
        {
            LaunchGuard.Begin();
        }
        catch (Exception e)
        {
            Log.Error($"[{ModId}] Could not record this launch, so a launch that stops before the main menu won't be noticed: {e}");
        }
        try
        {
            Installed.Watch();
        }
        catch (Exception e)
        {
            Log.Error($"[{ModId}] Could not watch for mods installed while the game runs: {e}");
        }
        var harmony = new Harmony(ModId);
        // Dev runs can leave some out, as if a game update broke them, to see what players would get
        // (SPIRE_MOD_MANAGER_SKIP_PATCHES=ModInfoFillPatch,RowLookPatch).
        var skip = SkippedPatches = IsDevRun
            ? (System.Environment.GetEnvironmentVariable("SPIRE_MOD_MANAGER_SKIP_PATCHES") ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            : [];
        foreach (var type in AccessTools.GetTypesFromAssembly(typeof(ModEntry).Assembly))
        {
            try
            {
                if (!type.IsDefined(typeof(HarmonyPatch), false))
                    continue;
                if (skip.Contains(type.Name))
                    throw new InvalidOperationException("left out for this dev run (SPIRE_MOD_MANAGER_SKIP_PATCHES)");
                harmony.CreateClassProcessor(type).Patch();
            }
            catch (Exception e)
            {
                _failedPatches.Add(type.Name);
                Log.Error($"[{ModId}] Could not patch {type.Name}; this game version may need an update of the mod: {e}");
            }
        }
        Log.Info($"[{ModId}] Initialized{(IsDevRun ? " (dev run)" : "")}"
            + (_failedPatches.Count > 0 ? $", without {string.Join(", ", _failedPatches)}" : ""));
    }

    // Our own version, from the manifest the game loaded ("0.1.0"; the loaded copy's, if a second one is installed);
    // null if it isn't found.
    public static string? Version => ModManager.Mods.Where(m => m.manifest?.id == ModId)
        .OrderByDescending(m => m.state == ModLoadState.Loaded).FirstOrDefault()?.manifest?.version;

    static readonly HashSet<string> Missing = [];

    // The resources that didn't load this session, for the self-test.
    public static IReadOnlyCollection<string> MissingResources => Missing;

    // One of the game's resources we use (a scene, texture or font; our scenes too, since they use the game's), or
    // null if it's gone or broken: a game update can move one, and a scene fails to load when something it uses
    // moved. Never throws. Each missing one is logged once and not tried again.
    public static T? Load<T>(string path) where T : Resource
    {
        if (Missing.Contains(path))
            return null;
        if (ResourceLoader.Load(path) is T resource)
            return resource;
        NoteMissing(path);
        return null;
    }

    // One of the game's fonts, or Godot's own if it's gone, so text still shows and can be measured. In a language the
    // game draws in its own font (Chinese, Japanese, Korean, Thai, Russian, Polish), Kreon gives way to that font, as
    // on the game's labels: Kreon has none of their letters. Like the game's labels, a whole label takes the
    // language's regular font even for Kreon bold; only rich text's bold_font (richBold) takes its bold one.
    public static Font Font(string path, bool richBold = false) => LanguageFont(path, richBold) ?? Load<Font>(path) ?? ThemeDB.FallbackFont;

    static bool _noLanguageFonts;

    // The game's font for its language in place of Kreon, or null: Kreon is right for the language, the path isn't
    // Kreon, or a game update took the game's font swap away (logged once; Kreon from then on).
    static Font? LanguageFont(string path, bool richBold)
    {
        if ((path != "res://themes/kreon_bold_shared.tres" && path != "res://themes/kreon_regular_shared.tres") || _noLanguageFonts
            || LocManager.Instance is not { Language: { } language })
            return null;
        try
        {
            return GameLanguageFont(language, richBold && path == "res://themes/kreon_bold_shared.tres");
        }
        catch (Exception e)
        {
            _noLanguageFonts = true;
            Log.Warn($"[{ModId}] The game's fonts for other languages weren't found, so our text is in Kreon in every language (logged once): {e.Message}");
            return null;
        }
    }

    // On its own, so a game update without FontManager fails in LanguageFont's try, not where that is compiled.
    [MethodImpl(MethodImplOptions.NoInlining)]
    static Font? GameLanguageFont(string language, bool bold) => FontManager.GetSubstituteFont(language, bold ? FontType.Bold : FontType.Regular);

    static void NoteMissing(string what)
    {
        if (Missing.Add(what))
            Log.Warn($"[{ModId}] Missing or broken game resource, a plain stand-in is used (logged once): {what}");
    }

    // Set by tools/dev.cs for its sandboxed runs, so they can't be mistaken for the real game. Never in the
    // Workshop build (no DEVTOOLS): there the dev switches, SPIRE_MOD_MANAGER_SKIP_PATCHES included, do nothing.
#if DEVTOOLS
    public static bool IsDevRun { get; } = System.Environment.GetEnvironmentVariable("SPIRE_MOD_MANAGER_DEV") == "1";
#else
    public static bool IsDevRun => false;
#endif

    // "[DEV]" in the window title (taskbar, Alt+Tab) and a small tag at the top of the screen.
    public static void MarkDevRun(SceneTree tree)
    {
        if (!IsDevRun || tree.Root.HasNode("SpireModManagerDevTag"))
            return;
        if (!tree.Root.Title.StartsWith("[DEV]"))
            tree.Root.Title = "[DEV] " + tree.Root.Title;
        var layer = new CanvasLayer { Name = "SpireModManagerDevTag", Layer = 128 };
        var tag = new Label
        {
            Text = "DEV SANDBOX",
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        tag.AddThemeColorOverride("font_color", new Color(1, 0.35f, 0.35f, 0.8f));
        tag.AddThemeFontSizeOverride("font_size", 18);
        layer.AddChild(tag);
        tag.SetAnchorsPreset(Control.LayoutPreset.CenterTop);
        tag.OffsetLeft = -100;
        tag.OffsetRight = 100;
        tag.OffsetTop = 4;
        tree.Root.AddChild(layer);
    }

    // Set by the self-test: Relaunch calls it instead, so the way to a relaunch can be tested without ending the
    // test run.
    public static Action? TestRelaunch;

    // Starts a new copy of the game with the same arguments, then quits through the game's own path
    // so settings (mod toggles) and profile data are saved first. The new process inherits Steam's
    // environment (SteamAppId), so it attaches to the running Steam client without a Steam relaunch.
    public static void Relaunch()
    {
        if (TestRelaunch is { } test)
        {
            test();
            return;
        }
        var args = RelaunchArgs();
        Log.Info($"[{ModId}] Relaunching with args: {string.Join(' ', args)}");
        OS.SetRestartOnExit(true, args);
        if (NGame.Instance is { } game)
            game.Quit();
        else
            ((SceneTree)Engine.GetMainLoop()).Quit();
    }

    // This session's arguments plus the current window mode, so the new copy opens the way this one looks
    // instead of starting in a small window until the game applies its display settings. Window flags
    // from an earlier relaunch are replaced, not repeated.
    public static string[] RelaunchArgs()
    {
        var args = new List<string>();
        var given = OS.GetCmdlineArgs();
        for (var i = 0; i < given.Length; i++)
        {
            if (given[i] is "--fullscreen" or "-f" or "--maximized" or "-m" or "--windowed" or "-w")
                continue;
            if (given[i] is "--resolution" or "--position")
            {
                i++; // and its value
                continue;
            }
            args.Add(given[i]);
        }
        switch (DisplayServer.WindowGetMode())
        {
            case DisplayServer.WindowMode.Fullscreen or DisplayServer.WindowMode.ExclusiveFullscreen:
                args.Add("--fullscreen");
                break;
            case DisplayServer.WindowMode.Maximized:
                args.Add("--maximized");
                break;
            default:
                var size = DisplayServer.WindowGetSize();
                var position = DisplayServer.WindowGetPosition();
                args.AddRange(["--windowed", "--resolution", $"{size.X}x{size.Y}", "--position", $"{position.X},{position.Y}"]);
                break;
        }
        return args.ToArray();
    }

    // True when the saved mod list differs from what this session loaded, i.e. a relaunch would change
    // something: the vanilla Modding screen's pending-changes test, plus the load order and mods installed
    // since launch that are on.
    public static bool HasPendingChanges()
    {
        var settings = SaveManager.Instance.SettingsSave.ModSettings;
        return LoadOrder.OrderChanged || Installed.Mods.Any(Installed.On) || ModManager.Mods.Any(mod =>
        {
            var disabled = settings?.IsModDisabled(mod) ?? false;
            return (mod.state == ModLoadState.Disabled && !disabled) || (mod.state == ModLoadState.Loaded && disabled);
        });
    }

    // Adds a button in the style of the vanilla Get Mods button, sized freely (the art is a 9-slice).
    // If the scene doesn't load, or isn't the game's button any more, a plain button with the same label, size
    // and action stands in (see AddPlainButton), so the page keeps working. plain: that one on purpose (the self-test).
    public static Control AddButton(Node parent, string locKey, Vector2 size, Action onPressed, bool plain = false) =>
        AddTextButton(parent, new LocString("settings_ui", locKey).GetFormattedText(), size, onPressed, plain);

    // The same with its label as given (a text made in code, not one key's).
    public static Control AddTextButton(Node parent, string text, Vector2 size, Action onPressed, bool plain = false)
    {
        var node = plain ? null : Load<PackedScene>(ButtonScenePath)?.Instantiate();
        if (node is not NButton button || button.GetNodeOrNull<NinePatchRect>("Visuals/Image/Frame") is not { } frame
            || button.GetNodeOrNull<MegaLabel>("Visuals/Label") is not { } label)
        {
            if (node != null)
            {
                NoteMissing($"{ButtonScenePath} (not the game's button)");
                node.Free();
            }
            return AddPlainButton(parent, text, size, onPressed);
        }
        parent.AddChild(button);
        button.Size = size;
        button.PivotOffset = size / 2;

        // Draw the art at its native height and scale it down, so the slanted corners keep their shape.
        var scale = size.Y / ButtonArtHeight;
        frame.Scale = new Vector2(scale, scale);
        frame.Size = size / scale;

        label.SetTextAutoSize(text);
        button.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(_ => onPressed()));
        return button;
    }

    // AddButton's stand-in: one of our small text buttons at the asked size, with a hidden red "Dot" in its top
    // right corner where the scene has the game's notification dot (callers show it while a relaunch is pending).
    static Button AddPlainButton(Node parent, string text, Vector2 size, Action onPressed)
    {
        var button = Menus.SmallButton(text, onPressed);
        button.ClipText = true; // a long label doesn't make it wider than asked
        button.AddThemeFontSizeOverride("font_size", 20);
        parent.AddChild(button);
        button.Size = size;
        button.PivotOffset = size / 2;
        var dot = new Label { Name = "Dot", Text = "●", Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore, Position = new Vector2(size.X - 18, -10) };
        dot.AddThemeColorOverride("font_color", new Color(1, 0.3f, 0.25f));
        dot.AddThemeFontSizeOverride("font_size", 18);
        button.AddChild(dot);
        return button;
    }

    // A new label for a button AddButton made, either kind.
    public static void Relabel(Control button, string locKey) => SetText(button, new LocString("settings_ui", locKey).GetFormattedText());

    public static void SetText(Control button, string text)
    {
        if (button is Button plain)
            plain.Text = text;
        else
            button.GetNodeOrNull<MegaLabel>("Visuals/Label")?.SetTextAutoSize(text);
    }
}
