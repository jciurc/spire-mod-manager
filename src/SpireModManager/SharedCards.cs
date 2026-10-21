using Godot;

namespace SpireModManager;

// What the API's ModCards and Mods.Details stand on: SMM's card for a mod (ModDetails.SharedCard) on another mod's
// controls and links, and what the player keeps about a mod, for another mod to read. A mod by id: its loaded copy, else
// its first installed (ModEntry.CopyOf).
static class SharedCards
{
    public const string CardName = "SpireModManagerSharedCard";
    const string ModIdMeta = "spire_mod_manager_card_mod";
    const float Width = 420;

    public static string? Text(string modId, bool full = false) => ModEntry.CopyOf(modId) is { } mod ? ModDetails.SharedCard(mod, full) : null;

    // Watched once per trigger; another call points it at another mod (a card open for the old one closes).
    public static void Watch(Control trigger, string modId)
    {
        var watched = trigger.HasMeta(ModIdMeta);
        var before = watched ? trigger.GetMeta(ModIdMeta).AsString() : null;
        trigger.SetMeta(ModIdMeta, modId);
        if (!watched)
            Hover.Watch(trigger, CardName, () => Text(trigger.GetMeta(ModIdMeta, "").AsString()), Hover.Timing.Normal, Width,
                full: () => Text(trigger.GetMeta(ModIdMeta, "").AsString(), full: true));
        else if (before != modId && Hover.ShownName == CardName)
            Hover.Hide();
    }

    // The card for whichever mod a link names (forMeta: its id for a link's meta, or null for none); the label's own look
    // is left as it is (Hover.WatchLinks takes our links' underline off).
    public static void WatchLinks(RichTextLabel label, Func<string, string?> forMeta)
    {
        string? Id(string meta)
        {
            try
            {
                return forMeta(meta);
            }
            catch (Exception)
            {
                return null; // the caller's mapping failed: no card for that link
            }
        }
        var underlined = label.MetaUnderlined;
        Hover.WatchLinks(label, CardName, meta => Id(meta) is { } id ? Text(id) : null, Width, full: meta => Id(meta) is { } id ? Text(id, full: true) : null);
        label.MetaUnderlined = underlined;
    }

    // What the player keeps about a mod: its copy's entry (else the id's), trimmed, blanks as none, tags once each.
    public static (string? Alias, string? Notes, List<string> Tags, string? Category) Details(string modId)
    {
        var info = (ModEntry.CopyOf(modId) is { } mod ? Store.InfoFor(mod) : null) ?? Store.InfoFor(modId);
        static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        var tags = (info?.Tags ?? []).Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var category = Clean(info?.Category) is { } named && Categories.Find(named) != null ? named : null;
        return (Clean(info?.Alias), Clean(info?.Notes), tags, category);
    }
}
