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
internal static class ModEntry
{
    public const string ModId = "SpireModManager";

    // One key per mod for counting: its id. A mod installed twice (a local build beside the Workshop copy, this one too)
    // counts once, as the game loads only one copy. Profiles keep every copy of this mod on, so whichever stays runs.
    public static string CountKey(string? id) => id ?? "";

    const string ButtonScenePath = $"res://{ModId}/scenes/menu_button.tscn";
    const float ButtonArtHeight = 110; // event_button.png is 284x110

    // Patch classes that didn't apply, by name: a game update renamed or removed what they hook.
    static readonly List<string> _failedPatches = [];
    public static IReadOnlyList<string> FailedPatches => _failedPatches;

#if DEVTOOLS
    // Dev runs: the patches SPIRE_MOD_MANAGER_SKIP_PATCHES leaves out (they count as failed).
    public static IReadOnlyList<string> SkippedPatches { get; private set; } = [];
#endif

    // The patches our Modding page can't do without: the page, the main menu's startup, the tickbox hook, the info
    // panel and the selected mod. Without one, the page stays the game's, with a line saying the mod needs an update;
    // the others only add to it.
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
    // first failure). First which copy of a mod installed twice loads (it has to be before the game tries the next
    // mod), then the launch record (of the copies that load), watching for new mods and the branch errors of mods
    // that are off: none needs a patch.
    public static void Init()
    {
        try
        {
            SameIdLoad.Apply();
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModId}] Could not choose which copy of a mod installed twice loads, so the game's own choice applies: {e}");
        }
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
        try
        {
            BranchErrors.DropFromOffMods();
        }
        catch (Exception e)
        {
            Log.Error($"[{ModId}] Could not drop the Steam branch error of mods that are off, so the main menu may show it: {e}");
        }
        var harmony = new Harmony(ModId);
#if DEVTOOLS
        // Dev runs can leave some out, as if a game update broke them, to see what players would get
        // (SPIRE_MOD_MANAGER_SKIP_PATCHES=ModInfoFillPatch,RowLookPatch).
        var skip = SkippedPatches = IsDevRun
            ? (System.Environment.GetEnvironmentVariable("SPIRE_MOD_MANAGER_SKIP_PATCHES") ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            : [];
#endif
        foreach (var type in AccessTools.GetTypesFromAssembly(typeof(ModEntry).Assembly))
        {
            try
            {
                if (!type.IsDefined(typeof(HarmonyPatch), false))
                    continue;
#if DEVTOOLS
                if (skip.Contains(type.Name))
                    throw new InvalidOperationException("left out for this dev run (SPIRE_MOD_MANAGER_SKIP_PATCHES)");
#endif
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
#if DEVTOOLS
        SelfTest.EarlyStart(); // dev.cs --crash: a made-up failed start, its dialog answered
#endif
    }

    // Our own version, from the manifest the game loaded ("0.1.0"; the loaded copy's, if a second one is installed);
    // null if it isn't found.
    public static string? Version => VersionOf(ModId);

    // A mod's copy as the API speaks of it: the one loaded, else the first installed (one that arrived while the game
    // runs too); null if none is.
    public static Mod? CopyOf(string? id) => id == null ? null
        : ModManager.Mods.Concat(SpireModManager.Installed.Mods).Where(m => m.manifest?.id == id).OrderByDescending(m => m.state == ModLoadState.Loaded).FirstOrDefault();

    // That copy's version, from its manifest ("0.1.0"), or null.
    public static string? VersionOf(string? id) => CopyOf(id)?.manifest?.version;

    static readonly HashSet<string> Missing = [];

#if DEVTOOLS
    // Self-test: the resources that didn't load this session.
    public static IReadOnlyCollection<string> MissingResources => Missing;
#endif

    // A game resource we use (a scene, texture or font; ours too, as they use the game's), or null if a game update
    // moved or broke it, or it isn't a T. Never throws; each missing one is logged once and not tried again; one of
    // another type is logged once per type asked for (it's there: asked for as what it is, it loads).
    public static T? Load<T>(string path) where T : Resource
    {
        if (Missing.Contains(path))
            return null;
        var loaded = ResourceLoader.Load(path);
        if (loaded is T resource)
            return resource;
        if (loaded == null)
            NoteMissing(path);
        else if (OtherType.Add($"{path} as {typeof(T).Name}"))
            Log.Warn($"[{ModId}] Game resource {path} is a {loaded.GetClass()}, not a {typeof(T).Name}, so it isn't used there (logged once)");
        return null;
    }

    static readonly HashSet<string> OtherType = [];

    // One of the game's fonts, or Godot's own if it's gone. In languages the game draws in its own font (Chinese,
    // Japanese, Korean, Thai, Russian, Polish), Kreon gives way to that font as on the game's labels; only rich text's
    // bold (richBold) takes its bold one.
    // Each is looked up once per language and kept (Fonts): a page build asks ~1,600 times, and every lookup went
    // through Godot's resource loader.
    public static Font Font(string path, bool richBold = false)
    {
#if DEVTOOLS
        FontAsks++;
#endif
        var language = LocManager.Instance?.Language;
        if (language != _fontsLanguage)
        {
            Fonts.Clear(); // the new language may draw in another font
            _fontsLanguage = language;
        }
        if (Fonts.TryGetValue((path, richBold), out var kept) && GodotObject.IsInstanceValid(kept))
            return kept;
#if DEVTOOLS
        FontLookups++;
#endif
        return Fonts[(path, richBold)] = LanguageFont(path, richBold) ?? Load<Font>(path) ?? ThemeDB.FallbackFont;
    }

    static readonly Dictionary<(string Path, bool RichBold), Font> Fonts = [];
    static string? _fontsLanguage; // the language Fonts were looked up in

#if DEVTOOLS
    // Self-test: how often Font was asked for one, and how often it looked one up rather than giving the one it kept.
    public static int FontAsks { get; private set; }
    public static int FontLookups { get; private set; }
#endif

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

    // Set by tools/dev.cs for its sandboxed runs. The Workshop build (no DEVTOOLS) has no dev switches.
#if DEVTOOLS
    public static bool IsDevRun { get; } = System.Environment.GetEnvironmentVariable("SPIRE_MOD_MANAGER_DEV") == "1";
#else
    public static bool IsDevRun => false;
#endif

#if DEVTOOLS
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
#endif

#if DEVTOOLS
    // Self-test: Relaunch and Quit call them instead, so the test run doesn't end. TestRelaunch gets Relaunch's plain.
    public static Action<bool>? TestRelaunch;
    public static Action? TestQuit;
#endif

    // Starts a new copy of the game with the same arguments, then quits through the game's own path
    // so settings (mod toggles) and profile data are saved first. The new process inherits Steam's
    // environment (SteamAppId), so it attaches to the running Steam client without a Steam relaunch.
    // plain: a game that didn't start (its startup error) quits without the game's path, which would save the profile
    // and progress of a half-started game; whoever calls it saved the settings already.
    public static void Relaunch(bool plain = false)
    {
#if DEVTOOLS
        if (TestRelaunch is { } test)
        {
            test(plain);
            return;
        }
#endif
        var args = RelaunchArgs();
        Log.Info($"[{ModId}] Relaunching with args: {string.Join(' ', args)}");
        OS.SetRestartOnExit(true, args);
        if (!plain && NGame.Instance is { } game)
            game.Quit();
        else
            ((SceneTree)Engine.GetMainLoop()).Quit();
    }

    // A plain quit, as the game's startup error dialog quits.
    public static void Quit()
    {
#if DEVTOOLS
        if (TestQuit is { } test)
        {
            test();
            return;
        }
#endif
        ((SceneTree)Engine.GetMainLoop()).Quit();
    }

    // This session's arguments plus the current window mode, so the new copy opens the way this one looks
    // instead of starting in a small window until the game applies its display settings, and the renderer it was
    // started with (the game's launch_opengl.bat and launch_vulkan.bat pass it as an engine option, which
    // OS.GetCmdlineArgs leaves out, so the new copy would open on the default one). A Steam invite it was started from
    // is left out.
    public static string[] RelaunchArgs() => RelaunchArgs(OS.GetCmdlineArgs(), System.Environment.GetCommandLineArgs(),
        DisplayServer.WindowGetMode(), DisplayServer.WindowGetSize(), DisplayServer.WindowGetPosition());

    // The same from what it's given, for the self-test: the game's arguments, the process's own (for the engine's
    // renderer options), and the window's mode, size and position.
    public static string[] RelaunchArgs(string[] given, string[] process, DisplayServer.WindowMode mode, Vector2I size, Vector2I position)
    {
        var args = new List<string>();
        for (var i = 0; i < given.Length; i++)
        {
            // Engine options, which OS.GetCmdlineArgs doesn't pass on: dropped in case a game version does, so the
            // window's own come once.
            if (given[i] is "--fullscreen" or "-f" or "--maximized" or "-m" or "--windowed" or "-w")
                continue;
            if (given[i] is "--resolution" or "--position")
            {
                i++; // and its value
                continue;
            }
            // The Steam invite the game was started from (Steam adds it): passed on, the new copy would join that lobby
            // again. Read as the game's CommandLineHelper reads it: leading dashes ignored, the lobby id after it (not a
            // flag) or after an "=".
            var key = given[i].TrimStart('-');
            if (key == "+connect_lobby")
            {
                if (i + 1 < given.Length && !given[i + 1].StartsWith('-') && !given[i + 1].StartsWith('+'))
                    i++;
                continue;
            }
            if (key.StartsWith("+connect_lobby="))
                continue;
            args.Add(given[i]);
        }
        // The renderer, as the process was started with it ("--rendering-driver opengl3", "--rendering-method gl_compatibility").
        foreach (var option in new[] { "--rendering-driver", "--rendering-method" })
            if (!args.Contains(option) && Array.IndexOf(process, option) is var at and >= 0 && at + 1 < process.Length && !process[at + 1].StartsWith('-'))
                args.AddRange([option, process[at + 1]]);
        switch (mode)
        {
            case DisplayServer.WindowMode.Fullscreen or DisplayServer.WindowMode.ExclusiveFullscreen:
                args.Add("--fullscreen");
                break;
            case DisplayServer.WindowMode.Maximized:
                args.Add("--maximized");
                break;
            // A window with a size; minimized (or headless, 0x0) gets nothing, and the game's display settings apply.
            case DisplayServer.WindowMode.Windowed when size.X > 0 && size.Y > 0:
                args.AddRange(["--windowed", "--resolution", $"{size.X}x{size.Y}", "--position", $"{position.X},{position.Y}"]);
                break;
        }
        return args.ToArray();
    }

    // True when the saved mod list differs from what this session loaded, i.e. a relaunch would change
    // something: the vanilla Modding screen's pending-changes test per copy, by which copy of an id loads (Copies.Loads:
    // ticking a second copy that won't load changes nothing), plus the load order and mods installed since launch that
    // would load. A copy set aside counts too (Loads knows whether it loads next time); one that failed doesn't, as in
    // the game's test. An id the launch couldn't choose for, or couldn't swap its copy in for, reads as the game's own
    // choice (Copies.Next), so only a change to what that loads is pending.
    public static bool HasPendingChanges()
    {
        if (LoadOrder.OrderChanged)
            return true;
        var (off, ticks) = (Dependencies.SavedOff(), Ticks.Now);
        if (Installed.Mods.Any(m => Copies.LoadsIn(m, off, ticks)))
            return true;
        return ModManager.Mods.Any(mod => mod.state is (ModLoadState.Disabled or ModLoadState.Loaded or ModLoadState.DisabledDuplicate)
            && (mod.state == ModLoadState.Loaded) != Copies.LoadsIn(mod, off, ticks));
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
