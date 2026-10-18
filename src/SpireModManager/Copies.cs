using Godot;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;

namespace SpireModManager;

// A mod installed more than once with one id (a Workshop item and a local fix, two Workshop items of one mod): each
// copy is its own mod in our data, with its own category, alias, notes and tags. A copy is "w:<Workshop item id>" or
// "l:<folder>". One copy of an id, the holder, keeps the plain id's entry; every other copy has "<id>|<copy>", seeded
// from the plain entry the first time it's seen (StoreData.Copies lists them, the holder first). A mod installed once
// has no record and its data stays under its id, so files without copies never change. The game's own list can't
// tell same-source copies apart (it keeps id, source and on/off): this is our data only.
static class Copies
{
    // Every mod the game found, and those installed this session.
    static IEnumerable<Mod> All => ModManager.Mods.Union(Installed.Mods);

    public static string CopyOf(Mod mod) => Compat.WorkshopId(mod) is { } item ? $"w:{item}"
        : "l:" + (Path.GetFileName(mod.path?.TrimEnd('/', '\\') ?? "") is { Length: > 0 } folder ? folder : mod.modSource.ToString());

    // The key our data has for this copy: its id, or "<id>|<copy>" for a copy that isn't its id's holder.
    public static string Key(Mod mod) => mod.manifest?.id is not { } id ? ""
        : Store.Data.Copies?.TryGetValue(id, out var copies) == true && copies.Count > 0 && CopyOf(mod) is var copy && copies[0] != copy
            ? $"{id}|{copy}" : id;

    public static string? KeyOf(Mod? mod) => mod == null ? null : Key(mod);

    // The mod id in a key.
    public static string IdOf(string key) => key.Split('|')[0];

    // The installed copy a key names; a plain id with no holder installed: its first copy.
    public static Mod? ModOf(string? key) => key == null ? null
        : All.FirstOrDefault(m => Key(m) == key) ?? All.FirstOrDefault(m => m.manifest?.id == key);

    // The row a key names (a copy's, or for a plain id, its holder's or else its first copy's: a dependency link names
    // only the id).
    public static NModMenuRow? RowOf(Control content, string? key)
    {
        if (key == null)
            return null;
        var rows = content.GetChildren().OfType<NModMenuRow>().ToList();
        return rows.FirstOrDefault(r => r.Mod is { } m && Key(m) == key) ?? rows.FirstOrDefault(r => r.Mod?.manifest?.id == key);
    }

    // Brings the record in line with the copies installed: at the first main menu, before the undo baseline (so it's
    // neither an undo step nor a change since launch), and quietly for a mod installed mid-session. True if it changed.
    public static bool Reconcile(StoreData data)
    {
        var changed = false;
        foreach (var group in All.Where(m => m.manifest?.id != null).GroupBy(m => m.manifest?.id ?? ""))
        {
            var id = group.Key;
            var installed = group.Select(CopyOf).Distinct().ToList();
            data.Copies ??= [];
            if (!data.Copies.TryGetValue(id, out var copies))
            {
                if (installed.Count < 2)
                    continue;
                // First split: the plain entry stays with a Workshop copy before a local one, the oldest item first
                // (fixed, so a version bump never moves it).
                data.Copies[id] = copies = [.. installed.OrderBy(c => c.StartsWith("w:") ? 0 : 1)
                    .ThenBy(c => c.StartsWith("w:") && ulong.TryParse(c[2..], out var item) ? item : ulong.MaxValue).ThenBy(c => c, StringComparer.Ordinal)];
                foreach (var copy in copies.Skip(1))
                    Seed(data, id, copy);
                changed = true;
                continue;
            }
            foreach (var copy in installed.Where(c => !copies.Contains(c)))
            {
                copies.Add(copy);
                Seed(data, id, copy);
                changed = true;
            }
            // The holder gone: its data moves to its own key, the next installed copy's to the plain id. Nothing is lost.
            if (copies[0] is var holder && !installed.Contains(holder) && copies.FirstOrDefault(installed.Contains) is { } next)
            {
                var (plain, own) = (data.Mods.GetValueOrDefault(id), data.Mods.GetValueOrDefault($"{id}|{next}"));
                Put(data, $"{id}|{holder}", plain);
                Put(data, id, own);
                data.Mods.Remove($"{id}|{next}");
                copies.Remove(next);
                copies.Insert(0, next);
                changed = true;
            }
        }
        return changed;
    }

    // A copy's own entry, from the plain one, unless it has one already (or the plain id has none).
    static void Seed(StoreData data, string id, string copy)
    {
        if (data.Mods.TryGetValue(id, out var plain) && !data.Mods.ContainsKey($"{id}|{copy}"))
            data.Mods[$"{id}|{copy}"] = new ModInfo { Category = plain.Category, Alias = plain.Alias, Notes = plain.Notes, Tags = plain.Tags?.ToList() };
    }

    static void Put(StoreData data, string key, ModInfo? info)
    {
        if (info == null)
            data.Mods.Remove(key);
        else
            data.Mods[key] = info;
    }

    static bool _started;

    // At the first main menu, with the store loaded: before Profiles.Prepare and History.Start. Once a session.
    public static void Start()
    {
        if (_started)
            return;
        _started = true;
        try
        {
            if (Reconcile(Store.Data))
                Store.Save();
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.ModId}] Could not sort out mods installed more than once: {e}");
        }
        Installed.Added += () =>
        {
            try
            {
                // Measured on a copy first: Quietly writes into every undo snapshot, worth it only for a real change.
                if (Reconcile(Store.Clone(Store.Data)))
                    History.Quietly(data => Reconcile(data));
            }
            catch (Exception e)
            {
                Log.Error($"[{ModEntry.ModId}] Could not sort out a newly installed copy of a mod: {e}");
            }
        };
    }
}
