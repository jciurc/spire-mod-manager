using Godot;

namespace SpireModManager.Api;

/// <summary>Game resources that never throw.</summary>
public static class GameResources
{
    /// <summary>The resource at a res:// path (the game's or your pck's), or null if it's missing or isn't a T; each missing path is logged once and not tried again.</summary>
    public static T? Load<T>(string path) where T : Resource => ApiGuard.Run<T?>("GameResources.Load", null, () => ModEntry.Load<T>(path));
}
