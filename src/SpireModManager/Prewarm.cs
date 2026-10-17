using System.Diagnostics;
using Godot;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace SpireModManager;

// The Modding page's text shaped ahead, a little each frame while the main menu is idle. Kreon has no Chinese, Japanese
// or Korean letters (nor many symbols), so the first time Godot shapes a text with one, it asks Windows which installed
// font has them: ~15 ms a name, up to ~90, which was most of the page's first open with many such mods (2 s for 89).
// Godot keeps the answer for the session, whatever the label or size, so a text shaped here costs the page nothing.
// Only texts with a letter the font lacks are shaped, one a frame: a slow one is the only hitch, on an idle menu.
static class Prewarm
{
    const double StartDelay = 1; // seconds after the main menu is ready: its own setup and popups first
    const double SkipBudgetMs = 2; // per frame, for texts the font covers (nothing to shape)

    static int _pass; // a newer main menu's pass replaces an older one's

#if DEVTOOLS
    // Self-test: the texts the last pass shaped, and whether it got to the end.
    public static readonly List<string> Shaped = [];
    public static bool Done { get; private set; }
    public static double SlowestMs { get; private set; } // the longest frame's shaping: the worst hitch on the menu
#endif

    // Never throws: a pass that can't start leaves the page to shape its text as it opens.
    public static void Start(Node menu)
    {
        try
        {
            Schedule(menu);
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.ModId}] Could not start shaping the Modding page's text ahead (it's shaped when the page opens): {e.Message}");
        }
    }

    static void Schedule(Node menu)
    {
        var pass = ++_pass;
#if DEVTOOLS
        Shaped.Clear();
        Done = false;
        SlowestMs = 0;
#endif
        var tree = menu.GetTree();
        IEnumerator<(Font Font, string Text)>? texts = null;
        void Step()
        {
            try
            {
                if (pass != _pass || !GodotObject.IsInstanceValid(menu) || !menu.IsInsideTree())
                {
                    tree.ProcessFrame -= Step;
                    return;
                }
                texts ??= Texts().GetEnumerator();
                var watch = Stopwatch.StartNew();
                while (watch.Elapsed.TotalMilliseconds < SkipBudgetMs)
                {
                    if (!texts.MoveNext())
                    {
                        tree.ProcessFrame -= Step;
#if DEVTOOLS
                        Done = true;
#endif
                        return;
                    }
                    var (font, text) = texts.Current;
                    if (text.EnumerateRunes().All(r => font.HasChar(r.Value)))
                        continue;
                    font.GetStringSize(text, fontSize: 16);
#if DEVTOOLS
                    Shaped.Add(text);
                    SlowestMs = Math.Max(SlowestMs, watch.Elapsed.TotalMilliseconds);
#endif
                    return; // one a frame
                }
            }
            catch (Exception e)
            {
                tree.ProcessFrame -= Step;
                Log.Warn($"[{ModEntry.ModId}] Could not shape the Modding page's text ahead (it's shaped when the page opens): {e.Message}");
            }
        }
        tree.CreateTimer(StartDelay).Timeout += () =>
        {
            if (pass == _pass)
                tree.ProcessFrame += Step;
        };
    }

    // What the page's rows and headers show, in the fonts they show it in, each once: names and aliases (the titles),
    // authors and tags (the line under), category names (the headers, bold). Worked out as the pass goes.
    static IEnumerable<(Font Font, string Text)> Texts()
    {
        var regular = ModEntry.Font("res://themes/kreon_regular_shared.tres");
        var bold = ModEntry.Font("res://themes/kreon_bold_shared.tres");
        var seen = new HashSet<(Font, string)>();
        foreach (var mod in ModManager.Mods.Concat(Installed.Mods).ToList())
        {
            var id = mod.manifest?.id;
            var texts = new List<string> { mod.manifest?.name ?? id ?? "", Store.InfoFor(id)?.Alias ?? "", ModDetails.Short(mod.manifest?.author ?? "") };
            texts.AddRange(Tags.For(mod).Select(c => c.Text));
            foreach (var text in texts)
                if (text.Length > 0 && seen.Add((regular, text)))
                    yield return (regular, text);
        }
        foreach (var name in Store.Data.Categories.Select(c => c.Name).ToList())
            if (name.Length > 0 && seen.Add((bold, name)))
                yield return (bold, name);
    }
}
