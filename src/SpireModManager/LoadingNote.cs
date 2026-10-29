using System.Diagnostics;
using System.Text.Json;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Localization.Fonts;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Saves;

namespace SpireModManager;

// The screen stays black before the game's logo while it loads the mods, one after another on the main thread, with no
// frame drawn until the last one is done. Where that runs long (After: 15 s from the game's start, Jordan's choice), a
// line in the middle of the screen says how many mods are loading, with a spinner that turns as each one loads, so the
// game doesn't look hung. Shown only then: a quick start looks just as the game's own does. It's drawn straight onto
// the window (no nodes: the game's scene isn't up yet) and forced onto the screen between mods; it goes at the first
// frame the game draws itself. We load first (SameIdLoad keeps our entries first), so it covers nearly every mod. The
// game's localization isn't loaded yet either, so its words come from our own files in our pack.
static class LoadingNote
{
#if DEVTOOLS
    // Dev runs can show it sooner (SPIRE_MOD_MANAGER_LOADING_AFTER=0: from the first mod after us).
    static readonly double After = double.TryParse(System.Environment.GetEnvironmentVariable("SPIRE_MOD_MANAGER_LOADING_AFTER"),
        System.Globalization.CultureInfo.InvariantCulture, out var after) ? after : 15;
    public static int Draws { get; private set; }
    public static bool Shown => _shownAt != null;
    public static bool Gone => _gone;
#else
    const double After = 15;
#endif
    const int FontSize = 26;
    const ulong RedrawMs = 100; // the spinner's quickest step
    static readonly string Spinner = @"|/-\";
    static readonly Color Cream = new(1, 0.964706f, 0.886275f, 0.85f);

    static Rid _canvas, _item;
    static Font? _font;
    static string? _text;
    static ulong _lastDraw;
    static int _turn;
    static double? _shownAt;
    static bool _gone;

    // Before and after each mod the game loads (LoadingNotePatch): the note once loading has run past After, redrawn
    // with the spinner turned. Nothing once the game's loading is done, or after anything went wrong.
    public static void Step()
    {
        if (_gone || ModManager.State == ModManagerState.Initialized)
            return;
        try
        {
            var seconds = Since();
            if (seconds < After || Time.GetTicksMsec() - _lastDraw < RedrawMs)
                return;
            if (_shownAt == null)
            {
                _shownAt = seconds;
                Log.Info($"[{ModEntry.ModId}] Mods are taking a while to load ({seconds:0.0} s so far): showing how many are loading");
            }
            Draw(Count());
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.ModId}] Could not show the note while mods load, so the screen stays black until they're done: {e.Message}");
            Remove();
        }
    }

    // Seconds since the game's process started: the black screen starts with it.
    static double Since() => (DateTime.Now - Started.Value).TotalSeconds;

    static readonly Lazy<DateTime> Started = new(() =>
    {
        using var self = Process.GetCurrentProcess();
        return self.StartTime;
    });

    // The mods the game is loading this launch: all but the ones off (Disabled, and DisabledDuplicate where the game has
    // it).
    static int Count() => ModManager.Mods.Count(m => m.state is ModLoadState.None or ModLoadState.Loaded or ModLoadState.Failed);

    // The line, centred, the spinner after it (apart, so the line doesn't shift as it turns), then the frame forced onto
    // the screen. The first time, a canvas of our own over the window's, and the note's removal at the game's first frame.
    public static void Draw(int count)
    {
        if (Engine.GetMainLoop() is not SceneTree tree)
            return;
        var root = tree.Root;
        if (!_canvas.IsValid)
        {
            _canvas = RenderingServer.CanvasCreate();
            RenderingServer.ViewportAttachCanvas(root.GetViewportRid(), _canvas);
            RenderingServer.ViewportSetCanvasStacking(root.GetViewportRid(), _canvas, 1000, 0);
            _item = RenderingServer.CanvasItemCreate();
            RenderingServer.CanvasItemSetParent(_item, _canvas);
            tree.ProcessFrame += Remove;
        }
        _font ??= LanguageFont();
        _text ??= Words(count);
        RenderingServer.CanvasItemClear(_item);
        var size = root.GetVisibleRect().Size;
        var width = _font.GetStringSize(_text, fontSize: FontSize).X;
        var at = new Vector2((size.X - width) / 2, size.Y / 2 + FontSize / 2f);
        _font.DrawString(_item, at, _text, fontSize: FontSize, modulate: Cream);
        _font.DrawString(_item, at + new Vector2(width + 14, 0), Spinner[_turn++ % Spinner.Length].ToString(), fontSize: FontSize, modulate: Cream);
        RenderingServer.ForceDraw();
        _lastDraw = Time.GetTicksMsec();
#if DEVTOOLS
        Draws++;
#endif
    }

    // At the game's first frame of its own (or after a failure): the canvas freed, and how long loading took logged.
    public static void Remove()
    {
        if (Engine.GetMainLoop() is SceneTree tree)
            tree.ProcessFrame -= Remove;
        if (_item.IsValid)
            RenderingServer.FreeRid(_item);
        if (_canvas.IsValid)
            RenderingServer.FreeRid(_canvas);
        (_item, _canvas, _gone) = (default, default, true);
        if (_shownAt is { } shown)
            Log.Info($"[{ModEntry.ModId}] Mods loaded in {Since():0.0} s; the note showed from {shown:0.0} s");
    }

#if DEVTOOLS
    // Self-test: shown again, and the words looked up again (another count).
    public static void TestReset() => (_gone, _text, _lastDraw, _shownAt) = (false, null, 0, null);
#endif

    // The player's language as the game will pick it (LocManager.Initialize): the setting, else the system's, else
    // English.
    static string Language()
    {
        try
        {
            return SaveManager.Instance.SettingsSave.Language is { Length: > 0 } set ? set : PlatformUtil.GetThreeLetterLanguageCode() ?? "eng";
        }
        catch (Exception)
        {
            return "eng";
        }
    }

    // The game's font for the language (one Kreon has no letters for), else Kreon, else Godot's own.
    static Font LanguageFont()
    {
        try
        {
            return FontManager.GetSubstituteFont(Language(), FontType.Regular) ?? ModEntry.Load<Font>("res://themes/kreon_regular_shared.tres") ?? ThemeDB.FallbackFont;
        }
        catch (Exception)
        {
            return ThemeDB.FallbackFont;
        }
    }

    // "Loading 214 mods" in the language, from our own file in our pack, else English's.
    static string Words(int count)
    {
        var key = count == 1 ? "SPIRE_MOD_MANAGER.LOADING_MODS_ONE" : "SPIRE_MOD_MANAGER.LOADING_MODS";
        foreach (var language in new[] { Language(), "eng" })
        {
            var file = $"res://{ModEntry.ModId}/localization/{language}/settings_ui.json";
            if (!Godot.FileAccess.FileExists(file))
                continue;
            using var json = JsonDocument.Parse(Godot.FileAccess.GetFileAsString(file));
            if (json.RootElement.TryGetProperty(key, out var text) && text.GetString() is { Length: > 0 } words)
                return words.Replace("{count}", count.ToString());
        }
        return $"Loading {count} mods";
    }
}

// Each mod the game loads, before and after: LoadingNote's chance to draw.
[HarmonyPatch(typeof(ModManager), "TryLoadMod")]
static class LoadingNotePatch
{
    static void Prefix() => LoadingNote.Step();

    static void Postfix() => LoadingNote.Step();
}
