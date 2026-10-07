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
    public record Details(DateTime Created, DateTime Updated, IReadOnlyList<string> Tags);

    static readonly Dictionary<ulong, Details> Cache = [];
    static bool _started;

    // Raised once the details arrive, so an open info panel can refill.
    public static event Action? Loaded;

    public static Details? For(Mod? mod) => mod?.workshopId is { } id && Cache.TryGetValue(id, out var details) ? details : null;

    public static void Start()
    {
        if (_started || !SteamInitializer.Initialized)
            return;
        _started = true;
        _ = Query();
    }

    static async Task Query()
    {
        var ids = ModManager.Mods.Where(m => m.workshopId.HasValue).Select(m => new PublishedFileId_t(m.workshopId ?? 0)).Distinct().ToArray();
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
                        d.m_rgchTags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
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
