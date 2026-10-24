namespace SpireModManager.Api;

/// <summary>The mods the game knows about.</summary>
public static class Mods
{
    /// <summary>The version in a mod's manifest ("0.1.0"): its loaded copy's, else (still loading, or off) its first installed copy's; null if it isn't installed.</summary>
    public static string? Version(string modId) => ApiGuard.Run<string?>("Mods.Version", null, () => ModEntry.VersionOf(modId));

    /// <summary>What the player keeps about a mod (its loaded copy's entry, else its first installed copy's; the id's when it isn't installed); never null: empty when there's nothing. Read it when you draw; it changes as the player edits.</summary>
    public static ModNotes Details(string modId) => ApiGuard.Run("Mods.Details", new ModNotes(), () =>
    {
        var (alias, notes, tags, category) = SharedCards.Details(modId);
        return new ModNotes { Alias = alias, Notes = notes, Tags = tags, Category = category };
    });

    /// <summary>For self-tests: while notes is set, Details(modId) and the mod's card (ModCards) read it in place of what the player keeps (trimmed, blanks as none, tags once each; the card shows its category, tags and notes, never the alias). Null clears it. Nothing is saved, and SMM's own page and search keep the player's; it's gone when the game closes.</summary>
    public static void TestDetails(string modId, ModNotes? notes) => ApiGuard.Run("Mods.TestDetails", () =>
    {
        if (notes == null)
            SharedCards.NoStandIn(modId);
        else
            SharedCards.StandIn(modId, notes.Alias, notes.Notes, notes.Tags, notes.Category);
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
