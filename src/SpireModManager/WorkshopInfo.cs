using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Multiplayer.Transport.Steam;
using MegaCrit.Sts2.Core.Platform.Steam;
using Steamworks;

namespace SpireModManager;

// Workshop details for subscribed mods: one batched Steam query per session, cached. Without Steam, there are none.
static class WorkshopInfo
{
    public record Details(DateTime Created, DateTime Updated, IReadOnlyList<string> Tags, IReadOnlyList<(string Min, string Max)> Branches,
        long Size, ulong? Subscribers)
    {
        // The page's "Required items" (Steam's children): what it needs on the Workshop, which its manifest may not say
        // (YuWanCard needs RitsuLib only there). The game doesn't know them.
        public IReadOnlyList<ulong> Required { get; init; } = [];
    }

    static readonly Dictionary<ulong, Details> Cache = [];

    // The titles of required items that aren't installed (no Details of their own), from a second query.
    static readonly Dictionary<ulong, string> Titles = [];

    public static string? Title(ulong id) => Titles.GetValueOrDefault(id);
    static bool _started;

    // Raised once the details arrive, so an open info panel can refill.
    public static event Action? Loaded;

    public static Details? For(Mod? mod) => Compat.WorkshopId(mod) is { } id && Cache.TryGetValue(id, out var details) ? details : null;

#if DEVTOOLS
    // Self-test: sets or clears a made-up mod's details.
    public static void TestSet(ulong id, Details? details)
    {
        if (details == null)
            Cache.Remove(id);
        else
            Cache[id] = details;
    }

    public static void TestTitle(ulong id, string? title)
    {
        if (title == null)
            Titles.Remove(id);
        else
            Titles[id] = title;
    }
#endif

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

    // Its required items, as the query returned them.
    static List<ulong> RequiredItems(UGCQueryHandle_t handle, uint index, uint count)
    {
        var children = new PublishedFileId_t[count];
        return count > 0 && SteamUGC.GetQueryUGCChildren(handle, index, children, count) ? [.. children.Select(c => c.m_PublishedFileId).Where(c => c != 0)] : [];
    }

    static async Task Query()
    {
        var ids = ModManager.Mods.Select(Compat.WorkshopId).OfType<ulong>().Select(id => new PublishedFileId_t(id)).Distinct().ToArray();
        if (ids.Length == 0)
            return;
        var handle = SteamUGC.CreateQueryUGCDetailsRequest(ids, (uint)ids.Length);
        SteamUGC.SetReturnChildren(handle, true);
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
                        SteamUGC.GetQueryUGCStatistic(result.m_handle, i, EItemStatistic.k_EItemStatistic_NumSubscriptions, out var subscribers) ? subscribers : null)
                    {
                        Required = RequiredItems(result.m_handle, i, d.m_unNumChildren),
                    };
            Log.Info($"[{ModEntry.ModId}] Workshop details for {Cache.Count} of {ids.Length} mods");
            Loaded?.Invoke();
            var missing = Cache.Values.SelectMany(d => d.Required).Where(r => !Cache.ContainsKey(r)).Distinct().ToArray();
            if (missing.Length > 0)
                await QueryTitles(missing);
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

    // The names of required items that aren't installed, so the panel can say which mod is missing. Without them it
    // shows their Workshop number.
    static async Task QueryTitles(ulong[] missing)
    {
        var handle = SteamUGC.CreateQueryUGCDetailsRequest([.. missing.Select(id => new PublishedFileId_t(id))], (uint)missing.Length);
        try
        {
            using var call = new SteamCallResult<SteamUGCQueryCompleted_t>(SteamUGC.SendQueryUGCRequest(handle), SteamInitializer.DisconnectToken);
            var result = await call.Task;
            if (result.m_eResult != EResult.k_EResultOK)
                return;
            for (uint i = 0; i < result.m_unNumResultsReturned; i++)
                if (SteamUGC.GetQueryUGCResult(handle, i, out var d) && d.m_eResult == EResult.k_EResultOK && d.m_rgchTitle is { Length: > 0 } title)
                    Titles[d.m_nPublishedFileId.m_PublishedFileId] = title;
            Loaded?.Invoke();
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.ModId}] Required Workshop items' names unavailable: {e.Message}");
        }
        finally
        {
            SteamUGC.ReleaseQueryUGCRequest(handle);
        }
    }
}
