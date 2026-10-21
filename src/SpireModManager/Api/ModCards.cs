using Godot;

namespace SpireModManager.Api;

/// <summary>SMM's card for a mod, as the Modding page's rows show on hover, for your own controls: its picture, name, version, state and problems, author, description, and the player's category, tags and notes.</summary>
public static class ModCards
{
    /// <summary>Shows modId's card while the pointer rests on trigger, with SMM's hover timing (Alt shows it in full), one card at a time across mods. Its loaded copy, else its first installed; built when it opens. Nothing shows for a mod that isn't installed. A second call for the same trigger replaces the mod. A trigger that ignores the mouse (a Label, by default) is set to pass it on (MouseFilter.Pass), so it can be hovered.</summary>
    public static void Watch(Control trigger, string modId) => ApiGuard.Run("ModCards.Watch", () => SharedCards.Watch(trigger, modId));

    /// <summary>Shows the card of the mod a link names while the pointer rests on that link: modIdForLink gets a link's meta (its [url=...] value) and gives the mod's id, or null for no card. For [url=mod:id] links: meta =&gt; meta.StartsWith("mod:") ? meta[4..] : null. A second call for the same label replaces the mapping. The label's look is left as it is; a mapping that throws shows no card, and is logged once.</summary>
    public static void WatchLinks(RichTextLabel label, Func<string, string?> modIdForLink) =>
        ApiGuard.Run("ModCards.WatchLinks", () => SharedCards.WatchLinks(label, modIdForLink));

    /// <summary>The card's BBCode as it would show now (not in full); null if the mod isn't installed. For self-tests.</summary>
    public static string? Text(string modId) => ApiGuard.Run<string?>("ModCards.Text", null, () => SharedCards.Text(modId));

    /// <summary>For self-tests: opens the card a Watch set on trigger at once, as a hover would, wherever the real mouse is (below the trigger). It stays up until Close, or until the trigger leaves the tree or is hidden. False if there's no Watch on trigger, or its mod isn't installed.</summary>
    public static bool Show(Control trigger) => ApiGuard.Run("ModCards.Show", false, () => SharedCards.ShowNow(trigger));

    /// <summary>For self-tests: closes the open card (SMM's cards are one at a time, across mods); true if one was open.</summary>
    public static bool Close() => ApiGuard.Run("ModCards.Close", false, () =>
    {
        var open = Hover.ShownName != null;
        Hover.Hide();
        return open;
    });
}
