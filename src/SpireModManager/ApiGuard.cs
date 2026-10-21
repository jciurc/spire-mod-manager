using System.Diagnostics;
using Godot;
using MegaCrit.Sts2.Core.Logging;

namespace SpireModManager;

// Every call another mod makes into the API comes through here: on the main thread only (from anywhere else nothing
// happens and the stand-in comes back), and never throwing back at the caller: a failure, the caller's or ours, is
// logged once per member, naming the mod that called. Callbacks a caller hands in (a menu entry's action, a banner's
// dismiss) run guarded the same way.
static class ApiGuard
{
    static readonly HashSet<string> Logged = [];

    static bool OnMainThread => OS.GetThreadCallerId() == OS.GetMainThreadId();

    public static T Run<T>(string member, T standIn, Func<T> body)
    {
        if (!OnMainThread)
        {
            Once(member, "called off the main thread, so nothing was done");
            return standIn;
        }
        try
        {
            return body();
        }
        catch (Exception e)
        {
            Once(member, e.ToString());
            return standIn;
        }
    }

    public static void Run(string member, Action body) => Run<object?>(member, null, () =>
    {
        body();
        return null;
    });

    public static Action Callback(string member, Action action) => () =>
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            Once(member, e.ToString());
        }
    };

    static void Once(string member, string what)
    {
        if (Logged.Add(member))
            Log.Warn($"[{ModEntry.ModId}] API {member}, called from {Caller()}: {what}");
    }

    // The first frame outside SMM (and the runtime, Godot and Harmony): the mod that called, or handed in the callback.
    static string Caller()
    {
        var ours = typeof(ApiGuard).Assembly;
        foreach (var frame in new StackTrace().GetFrames())
            if (frame.GetMethod()?.DeclaringType?.Assembly is { } assembly && assembly != ours && assembly.GetName().Name is { } name
                && !name.StartsWith("System") && name is not ("GodotSharp" or "0Harmony" or "mscorlib"))
                return name;
        return "an unknown caller";
    }

#if DEVTOOLS
    // Self-test: whether a member has logged its one warning.
    public static bool HasLogged(string member) => Logged.Contains(member);
#endif
}
