using Godot;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Multiplayer.Transport.Steam;
using MegaCrit.Sts2.Core.Platform.Steam;
using Steamworks;

namespace SpireModManager;

// Subscribing to Workshop mods from inside the game, only on the player's click (a profile's mods that aren't installed:
// the import preview, the missing-mods list). Steam downloads each; the game finds it as it lands (its ItemInstalled
// callback, then OnModDetected), as any mod installed while it runs, and it loads after a relaunch.
static class Subscriptions
{
    public enum State { None, Subscribing, Downloading, Installed, Failed }

    // Progress: 0..1 while downloading. Error: why it failed, in the player's words.
    public record Status(State State, float Progress = 0, string? Error = null);

    static readonly Dictionary<ulong, Status> Known = [];
    static bool _watching;

    // Raised when any status changes (an open list redraws).
    public static event Action? Changed;

    // Whether subscribing works here: Steam running and signed in.
    public static bool Available =>
#if DEVTOOLS
        TestSteam != null ||
#endif
        SteamInitializer.Initialized;

    // An item's status: what we started, else what Steam says (subscribed before, already installed).
    public static Status Of(ulong item)
    {
        if (Installed(item))
            return new Status(State.Installed);
        if (Known.TryGetValue(item, out var known))
            return known;
        return new Status(State.None);
    }

    // Whether the game has a mod from this item (installed at launch or since).
    static bool Installed(ulong item) => ModManager.Mods.Any(m => Compat.WorkshopId(m) == item);

    // Subscribes to each item not installed or on its way already. Never throws: a failure shows on the item.
    public static void Subscribe(IEnumerable<ulong> items)
    {
        foreach (var item in items.Distinct())
        {
            if (Of(item).State is State.Subscribing or State.Downloading or State.Installed)
                continue;
            Set(item, new Status(State.Subscribing));
            _ = One(item);
        }
        Watch();
    }

    static async Task One(ulong item)
    {
        try
        {
            var result = await Ask(item);
            if (result == EResult.k_EResultOK)
                Set(item, new Status(State.Downloading));
            else
            {
                Log.Warn($"[{ModEntry.ModId}] Steam couldn't subscribe to Workshop item {item}: {result}");
                Set(item, new Status(State.Failed, Error: Reason(result)));
            }
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.ModId}] Could not subscribe to Workshop item {item}: {e.Message}");
            Set(item, new Status(State.Failed, Error: Failure("SPIRE_MOD_MANAGER.SUBSCRIBE_FAILED_STEAM")));
        }
    }

    // Steam's answer to a subscription.
    static async Task<EResult> Ask(ulong item)
    {
#if DEVTOOLS
        if (TestSteam is { } test)
            return await test.Subscribe(item);
#endif
        using var call = new SteamCallResult<RemoteStorageSubscribePublishedFileResult_t>(SteamUGC.SubscribeItem(new PublishedFileId_t(item)), SteamInitializer.DisconnectToken);
        return (await call.Task).m_eResult;
    }

    // Why Steam said no, for the player, as the line shows it.
    static string Reason(EResult result) => Failure(result switch
    {
        EResult.k_EResultFileNotFound or EResult.k_EResultAccessDenied => "SPIRE_MOD_MANAGER.SUBSCRIBE_FAILED_GONE",
        EResult.k_EResultNoConnection or EResult.k_EResultNotLoggedOn or EResult.k_EResultTimeout => "SPIRE_MOD_MANAGER.SUBSCRIBE_FAILED_OFFLINE",
        _ => "SPIRE_MOD_MANAGER.SUBSCRIBE_FAILED_STEAM",
    });

    // "Couldn't subscribe: <why>"; a mod that's gone says so in a line of its own.
    static string Failure(string reason) => reason == "SPIRE_MOD_MANAGER.SUBSCRIBE_FAILED_GONE" ? Menus.Text(reason)
        : Menus.Format("SPIRE_MOD_MANAGER.SUBSCRIBE_FAILED", ("reason", Menus.Text(reason)));

    static void Set(ulong item, Status status)
    {
        if (Known.TryGetValue(item, out var was) && was == status)
            return;
        Known[item] = status;
        Changed?.Invoke();
    }

    // While anything is on its way: its progress from Steam, and installed once the game has it. A quarter second apart,
    // until nothing is left to watch.
    static void Watch()
    {
        if (_watching || Engine.GetMainLoop() is not SceneTree tree)
            return;
        _watching = true;
        void Tick()
        {
            try
            {
                foreach (var (item, status) in Known.Where(p => p.Value.State is State.Subscribing or State.Downloading).ToList())
                {
                    if (Installed(item))
                        Set(item, new Status(State.Installed));
                    else if (status.State == State.Downloading && Progress(item) is { } progress)
                        Set(item, status with { Progress = progress });
                }
            }
            catch (Exception e)
            {
                Log.Warn($"[{ModEntry.ModId}] Could not follow a Workshop download: {e.Message}");
            }
            if (Known.Values.Any(s => s.State is State.Subscribing or State.Downloading))
                tree.CreateTimer(0.25).Timeout += Tick;
            else
                _watching = false;
        }
        tree.CreateTimer(0.25).Timeout += Tick;
    }

    // How much of the item Steam has downloaded, or null if it doesn't say.
    static float? Progress(ulong item)
    {
#if DEVTOOLS
        if (TestSteam is { } test)
            return test.Progress(item);
#endif
        return SteamUGC.GetItemDownloadInfo(new PublishedFileId_t(item), out var done, out var total) && total > 0 ? (float)done / total : null;
    }

#if DEVTOOLS
    // Self-test: Steam made up (the sandbox runs without it): its answer to each subscription, and each item's progress.
    public interface ITestSteam
    {
        Task<EResult> Subscribe(ulong item);
        float? Progress(ulong item);
    }

    public static ITestSteam? TestSteam { get; set; }

    // Self-test: forgets every status.
    public static void TestReset()
    {
        Known.Clear();
        Changed?.Invoke();
    }
#endif
}
