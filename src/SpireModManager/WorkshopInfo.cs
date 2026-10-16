using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Multiplayer.Transport.Steam;
using MegaCrit.Sts2.Core.Platform.Steam;
using Steamworks;

namespace SpireModManager;

// Workshop details for subscribed mods: one batched Steam query per session (the same call the game
// makes at startup to check branch support), cached in memory. Without Steam (dev sandbox, offline)
// there is simply nothing, and everything that uses it shows less.
static class WorkshopInfo
{
    public record Details(DateTime Created, DateTime Updated, IReadOnlyList<string> Tags, IReadOnlyList<(string Min, string Max)> Branches,
        long Size, ulong? Subscribers);

    static readonly Dictionary<ulong, Details> Cache = [];
    static bool _started;

    // Raised once the details arrive, so an open info panel can refill.
    public static event Action? Loaded;

    public static Details? For(Mod? mod) => Compat.WorkshopId(mod) is { } id && Cache.TryGetValue(id, out var details) ? details : null;

    // Self-test only: details for a made-up Workshop id (the dev sandbox has no Steam), or none again.
    public static void TestSet(ulong id, Details? details)
    {
        if (details == null)
            Cache.Remove(id);
        else
            Cache[id] = details;
    }

    public static void Start()
    {
        if (_started || !SteamInitializer.Initialized)
            return;
        _started = true;
        _ = Query();
    }

    // The game branches the author marked as supported (min, max; empty = open-ended), if any.
    static List<(string Min, string Max)> SupportedBranches(UGCQueryHandle_t handle, uint index)
    {
        var branches = new List<(string, string)>();
        for (uint i = 0; i < SteamUGC.GetNumSupportedGameVersions(handle, index); i++)
            if (SteamUGC.GetSupportedGameVersionData(handle, index, i, out var min, out var max, 999u))
                branches.Add((min, max));
        return branches;
    }

    static async Task Query()
    {
        var ids = ModManager.Mods.Select(Compat.WorkshopId).OfType<ulong>().Select(id => new PublishedFileId_t(id)).Distinct().ToArray();
        if (ids.Length == 0)
            return;
        var handle = SteamUGC.CreateQueryUGCDetailsRequest(ids, (uint)ids.Length);
        try
        {
            using var call = new SteamCallResult<SteamUGCQueryCompleted_t>(SteamUGC.SendQueryUGCRequest(handle), SteamInitializer.DisconnectToken);
            var result = await call.Task;
            if (result.m_eResult != EResult.k_EResultOK)
            {
                Log.Warn($"[{ModEntry.ModId}] Workshop details query failed: {result.m_eResult}");
                return;
            }
            for (uint i = 0; i < result.m_unNumResultsReturned; i++)
                if (SteamUGC.GetQueryUGCResult(handle, i, out var d) && d.m_eResult == EResult.k_EResultOK)
                    Cache[d.m_nPublishedFileId.m_PublishedFileId] = new Details(
                        DateTimeOffset.FromUnixTimeSeconds(d.m_rtimeCreated).LocalDateTime,
                        DateTimeOffset.FromUnixTimeSeconds(d.m_rtimeUpdated).LocalDateTime,
                        d.m_rgchTags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                        SupportedBranches(result.m_handle, i), d.m_nFileSize,
                        SteamUGC.GetQueryUGCStatistic(result.m_handle, i, EItemStatistic.k_EItemStatistic_NumSubscriptions, out var subscribers) ? subscribers : null);
            Log.Info($"[{ModEntry.ModId}] Workshop details for {Cache.Count} of {ids.Length} mods");
            Loaded?.Invoke();
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.ModId}] Workshop details unavailable: {e.Message}");
        }
        finally
        {
            SteamUGC.ReleaseQueryUGCRequest(handle);
        }
    }
}
