namespace SpireModManager;

// Self-test: where the time of a redraw, a view switch or the page's first open goes, part by part, added up while On.
// Lap ends the part being timed and starts the next, so the parts of one call chain add up to its time. Time counts
// one call as a part of its own, inside a lap or out of one (deferred work, a signal), and leaves it out of the lap
// around it. Lap's calls vanish from the Workshop build.
static class Costs
{
#if DEVTOOLS
    public static readonly Dictionary<string, double> Parts = [];
    static readonly System.Diagnostics.Stopwatch Watch = new();
    static bool _on;
    static double _timed; // ms since the last lap that Time counted already

    // Turned on: from no parts, the first one starting now.
    public static bool On
    {
        get => _on;
        set
        {
            if (value)
                Parts.Clear();
            (_on, _timed) = (value, 0);
            Watch.Restart();
        }
    }

    static void Add(string part, double ms) => Parts[part] = Parts.GetValueOrDefault(part) + ms;
#endif

    [System.Diagnostics.Conditional("DEVTOOLS")]
    public static void Lap(string part)
    {
#if DEVTOOLS
        if (!_on)
            return;
        Add(part, Watch.Elapsed.TotalMilliseconds - _timed);
        _timed = 0;
        Watch.Restart();
#endif
    }

    public static void Time(string part, Action action)
    {
#if DEVTOOLS
        if (_on)
        {
            var (from, before) = (Watch.Elapsed.TotalMilliseconds, _timed);
            action();
            var spent = Watch.Elapsed.TotalMilliseconds - from;
            Add(part, spent - (_timed - before)); // less the Time calls inside it
            _timed = before + spent;
            return;
        }
#endif
        action();
    }
}
