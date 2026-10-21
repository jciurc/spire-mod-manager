namespace SpireModManager.Api;

/// <summary>The mods the game knows about.</summary>
public static class Mods
{
    /// <summary>The version in a mod's manifest ("0.1.0"): its loaded copy's, else (still loading, or off) its first installed copy's; null if it isn't installed.</summary>
    public static string? Version(string modId) => ApiGuard.Run<string?>("Mods.Version", null, () => ModEntry.VersionOf(modId));

    /// <summary>What the player keeps about a mod (its loaded copy's entry, else its first installed copy's, else the id's); never null: empty when there's nothing. Read it when you draw; it changes as the player edits.</summary>
    public static ModNotes Details(string modId) => ApiGuard.Run("Mods.Details", new ModNotes(), () =>
    {
        var (alias, notes, tags, category) = SharedCards.Details(modId);
        return new ModNotes { Alias = alias, Notes = notes, Tags = tags, Category = category };
    });
}

/// <summary>What the player keeps about a mod in SMM.</summary>
public sealed class ModNotes
{
    /// <summary>The name the player gave it, or null.</summary>
    public string? Alias { get; init; }

    /// <summary>The player's notes, or null.</summary>
    public string? Notes { get; init; }

    /// <summary>The player's own tags, in their order (the automatic ones, like Local or Library, aren't here).</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>The player's category for it, or null (Uncategorized).</summary>
    public string? Category { get; init; }
}
