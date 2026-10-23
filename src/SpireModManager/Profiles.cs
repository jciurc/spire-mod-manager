using Godot;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;
using MegaCrit.Sts2.Core.Saves;

namespace SpireModManager;

// A profile is a snapshot of every mod's on/off state, by id + source like the game's mod list; for a mod installed
// more than once from one source (one switch in the game's list), each copy's own box too (which of them loads is worked
// out from those, as for our data: Copies.Next).
// Changing the mod list doesn't change the saved profile: it becomes the profile's unsaved edit, shown as
// "Unsaved" with Save, Save as new and Discard. Switching away keeps the edit with its profile ("Name *"
// in the list); coming back restores it, so it is never lost.
// A mod a profile has never seen (installed since it was last used) joins it as it is in the mod list, which for a
// new mod is on, the game's default:
// - the active profile takes it in at launch, or as soon as it's installed while the game runs, quietly: it isn't
//   an unsaved edit, and neither undo nor Revert all takes it away;
// - switching to a profile that has never seen a mod leaves the mod as it is, and the profile keeps it that way
//   from then on (so a switch never turns a new mod off, or on).
// The mod managers (this one, Better Mod Menu) are never turned on or off by a profile.
static class Profiles
{
    static bool _applying;
    static bool _watching;

    // Raised after a switch, save or discard, or when the list becomes (or stops being) an unsaved edit, so
    // the profile bar shows the right name and buttons whoever made the change.
    public static event Action? Switched;

    public static Profile? Active => Store.Data.Profiles.FirstOrDefault(p => p.Name == Store.Data.ActiveProfile);

    // First use: convert id-only profiles (Better Mod Menu's), and make sure one profile is active. Then (also on
    // each visit to the Modding page) the active profile takes in the mods it has never seen. At launch this runs
    // before the undo history starts, so that's part of the state the session starts from.
    public static void Prepare()
    {
        var data = Store.Data;
        var changed = false;
        foreach (var profile in data.Profiles.Where(p => p.Disabled != null))
        {
            var disabled = profile.Disabled?.Select(r => r.Id).ToHashSet() ?? [];
            profile.Mods = Installed().Select(m => new ProfileMod(m.Id, m.Source, !disabled.Contains(m.Id))).ToList();
            profile.Disabled = null;
            changed = true;
        }
        if (Active == null)
        {
            var profile = data.Profiles.FirstOrDefault() ?? Add(new LocString("settings_ui", "SPIRE_MOD_MANAGER.DEFAULT_PROFILE").GetFormattedText());
            data.ActiveProfile = profile.Name;
            changed = true;
        }
        if (TakeIn(Active))
            changed = true;
        foreach (var profile in data.Profiles)
            changed |= HoldCopies(profile);
        if (changed)
            Store.Save();
        if (!_watching)
        {
            // From now on, a mod installed while the game runs joins the active profile as it arrives.
            SpireModManager.Installed.Added += TakeInInstalled;
            _watching = true;
        }
        Track();
    }

    // A mod installed while the game runs: the active profile takes it in at once as off (the profile was saved before it
    // existed), so the mod arriving on is that profile's unsaved edit: Save keeps it on, Discard turns it off, and
    // unticking it is no change. Another copy of a mod from one source likewise, one new to our record: the active
    // profile has the new copy off (it arrives ticked where its switch is on), and the other profiles read it as on, as
    // they read a mod they've never seen (the copies they know keep their ticks: Copies.Join). A copy uninstalled and
    // back keeps the tick the profile saved for it, as the same reinstall before a launch does. Quietly (see
    // History.Quietly). Fails soft: it runs inside the game's mod detection, beside Changes' own handler.
    static void TakeInInstalled()
    {
        try
        {
            // Not an update of a copy installed this session (the game keeps its earlier Mod): the profile knows that one.
            var come = SpireModManager.Installed.Mods.LastOrDefault();
            var copy = come != null && Copies.Twins(come).Count > 0 && Copies.IsNewToRecord(come)
                && !ModManager.Mods.Union(SpireModManager.Installed.Mods).Any(m => m != come && Copies.SwitchKey(m) == Copies.SwitchKey(come)) ? come : null;
            if (Active is not { } profile || !Unseen(profile).Any() && !Store.Data.Profiles.Any(Unheld) && copy == null)
                return;
            var took = false;
            History.Quietly(data =>
            {
                foreach (var each in data.Profiles)
                    HoldCopies(each);
                took |= TakeIn(data.Profiles.FirstOrDefault(p => p.Name == data.ActiveProfile), arrived: true, copy);
            });
            // Its being on is the profile's unsaved edit. The arrival isn't the player's step, and the edit follows the
            // mod list (worked out again wherever undo lands): now only, not as a step. At the frame's end, once the
            // rest of the arrival has run (a second copy's entry seeded), so the edit is worked out once.
            if (took)
                Callable.From(() => History.AsCurrent(Track)).CallDeferred();
            Switched?.Invoke(); // the count beside the name
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.ModId}] Could not add a newly installed mod to the active profile: {e}");
        }
    }

    // Takes the installed mods a profile has never seen into its saved state, and into its unsaved edit if it has
    // one, as they are in the mod list now (so they don't count as an edit). arrived: installed while the game runs,
    // so into the saved state as off (it was saved before they existed), and their being on is an edit (Track); copy: a
    // new copy of a mod installed more than once from its source, off in the saved state's entry for its id and source
    // the same way. True if there were any.
    static bool TakeIn(Profile? profile, bool arrived = false, Mod? copy = null)
    {
        if (profile == null)
            return false;
        var took = false;
        if (arrived && copy is { manifest.id: { } id })
        {
            var (source, name) = (copy.modSource, Copies.CopyOf(copy));
            var i = profile.Mods.FindIndex(m => m.Id == id && m.Source == source);
            if (i >= 0 && profile.Mods[i] is var entry && entry.CopiesOff?.Contains(name) != true)
            {
                profile.Mods[i] = entry with { CopiesOff = [.. (entry.CopiesOff ?? []).Append(name).Order(StringComparer.Ordinal)] };
                took = true;
            }
        }
        if (Unseen(profile).ToList() is not { Count: > 0 } unseen)
            return took;
        profile.Mods.AddRange(arrived ? unseen.Select(m => m with { Enabled = false }) : unseen);
        if (profile.Edited is { } edit)
        {
            var known = Index(edit);
            foreach (var mod in unseen)
                if (known.TryAdd((mod.Id, mod.Source), mod))
                    edit.Add(mod);
        }
        return true;
    }

    // Gives a profile's entries from before copies had a box each (CopiesOff null), for a group of copies our record has
    // (Copies.RecordGroup, installed or not), their copies off as they read then, as TakeIn does for a mod it has never
    // seen: an entry naming its copy (0.1.4's) as our data reads after this session's joins (Held); one naming none
    // (Better Mod Menu's) the copies off now. From then on a tick on another copy is a change to the profile, as for any
    // mod. At launch, before the undo history starts, and before a profile is applied. True if any changed.
    static bool HoldCopies(Profile profile)
    {
        var changed = false;
        foreach (var list in new[] { profile.Mods, profile.Edited })
            for (var i = 0; i < (list?.Count ?? 0); i++)
                if (list?[i] is { CopiesOff: null } entry && !Manager(entry.Id) && Copies.RecordGroup(entry.Id, entry.Source) is { } copies)
                {
                    list[i] = entry with { CopiesOff = entry.Copy is { } copy ? Held(entry.Id, entry.Source, copies, copy) : OffNow(entry.Id, copies) };
                    changed = true;
                }
        return changed;
    }

    // A 0.1.4 entry's copies off, read as Copies.LegacyOff reads our data and then left as this session's joins left it
    // (Copies.Join): every recorded copy but the one it names, whether that's installed or not (one missing: the read rule
    // ticks the others until it's back), less the copies new to the record this session (they read ticked in our data).
    // Where one joined while every installed copy the record had was off here (the one named missing), those too: Join
    // takes them out of every profile entry that has them all off, by the entry's own copies off and not our data's (its
    // step 2), so this entry ends as it would have if it had been filled in before the join. Else the profile reads them
    // off beside the new copy: Unsaved at once where our data has them ticked, and applied it lets a lower new copy take
    // over.
    static List<string> Held(string id, ModSource source, List<string> copies, string copy)
    {
        var off = copies.Where(c => c != copy && !Copies.JoinedThisSession(id, c)).ToList();
        var had = AllMods().Where(m => m.manifest?.id == id && m.modSource == source).Select(Copies.CopyOf)
            .Where(c => copies.Contains(c) && !Copies.JoinedThisSession(id, c)).Distinct().ToList();
        if (copies.Any(c => Copies.JoinedThisSession(id, c)) && had.Count > 0 && had.All(off.Contains))
            off.RemoveAll(had.Contains);
        return [.. off.Order(StringComparer.Ordinal)];
    }

#if DEVTOOLS
    // Self-test: HoldCopies on a made-up profile.
    public static bool TestHold(Profile profile) => HoldCopies(profile);

    // Self-test: a profile's entries applied as a switch, Discard or an import applies them, with no page to tick.
    public static void TestApply(List<ProfileMod> mods) => Apply(mods, null);

    // Self-test: the mod list now as a profile saves it (Save merges it in).
    public static List<ProfileMod> TestCurrent() => Current();
#endif

    // Whether HoldCopies has an entry to fill in: one with no copies off for a group of copies our record has.
    static bool Unheld(Profile profile) =>
        profile.Mods.Concat(profile.Edited ?? []).Any(m => m.CopiesOff == null && !Manager(m.Id) && Copies.RecordGroup(m.Id, m.Source) != null);

    // The copies of a group that are off now while its switch is on (StoreData.CopiesOff; data from before it, through
    // its legacy picks), sorted.
    static List<string> OffNow(string id, IEnumerable<string> copies)
    {
        var off = (Store.Data.CopiesOff ?? Copies.LegacyOff(Store.Data.CopyPicks, Store.Data.Copies)).ToHashSet();
        return [.. copies.Where(c => off.Contains($"{id}|{c}")).Order(StringComparer.Ordinal)];
    }

    // Installed mods the profile has no entry for, as they are in the mod list now.
    static IEnumerable<ProfileMod> Unseen(Profile profile)
    {
        var known = Index(profile.Mods);
        return Current().Where(m => !known.ContainsKey((m.Id, m.Source)));
    }

    // Whether the mod list is the active profile's unsaved edit.
    public static bool Edited => Active?.Edited != null;

    // After any change to the mod list: when it differs from the active profile's saved state it is that
    // profile's unsaved edit (timestamped); back on the saved state, the profile is clean again. Nothing is
    // overwritten silently: saving is the player's choice.
    public static void Track()
    {
        if (_applying || Active is not { } profile || !Edit(profile))
            return;
        Store.Save();
        Switched?.Invoke(); // the bar shows "Unsaved" or not
    }

    // Sets a profile's unsaved edit from the mod list now: none when it matches the saved state, otherwise the list
    // (timestamped). True if it changed.
    static bool Edit(Profile? profile)
    {
        if (profile == null)
            return false;
        var current = Current();
        if (Same(current, profile.Mods))
        {
            if (profile.Edited == null)
                return false;
            (profile.Edited, profile.EditedAt) = (null, null);
            return true;
        }
        if (profile.Edited is { } edit && Same(current, edit))
            return false;
        profile.Edited = Merge(profile.Edited ?? profile.Mods, current);
        profile.EditedAt = DateTime.Now;
        return true;
    }

    // Writes a profile to the mod list and makes it active: its unsaved edit if it has one (so leaving a
    // profile and coming back finds it as it was), its saved state otherwise.
    public static void SwitchTo(Profile target, NModdingScreen screen)
    {
        _missingDismissed = null; // its missing-mods banner shows again
        Track(); // the profile being left keeps its edit
        HoldCopies(target); // copies off where it names none, so each group is applied whole
        Apply(target.Edited ?? target.Mods, screen); // mods it has never seen stay as they are,
        TakeIn(target); // and it keeps them that way from now on
        Store.Data.ActiveProfile = target.Name;
        Store.Save();
        Track(); // the list as it is now, against the profile (normally the same)
        Redraw(screen);
        Switched?.Invoke();
    }

    // The active profile's unsaved changes: copies turned on and off compared with its saved state (keys, for links to
    // them, and names; a copy of a mod installed twice says which). With the same copies and versions installed, the same
    // boxes mean the same copy loads, so no box changed is no change.
    public static (List<(string Id, string Name)> On, List<(string Id, string Name)> Off) UnsavedChanges()
    {
        if (Active is not { Edited: not null } profile)
            return ([], []);
        var saved = Index(profile.Mods);
        var (on, off) = (new List<(string, string)>(), new List<(string, string)>());
        foreach (var copy in AllMods())
        {
            if (copy.manifest?.id is not { } id || Manager(id) || saved.GetValueOrDefault((id, copy.modSource)) is not { } entry)
                continue;
            var now = Copies.Ticked(copy);
            if (CopyOn(copy, entry) != now)
                (now ? on : off).Add((Copies.LinkKey(copy), (copy.manifest?.name ?? id) + Copies.Which(copy)));
        }
        return (on, off);
    }

    // Whether a copy is on as a profile's entry for its id and source has it: the entry on and, for a mod installed more
    // than once from that source, the copy on (On).
    static bool CopyOn(Mod copy, ProfileMod entry) => copy.manifest?.id == ModEntry.ModId || Copies.Twins(copy).Count == 0
        ? entry.Enabled : On(entry, copy.manifest?.id ?? "", copy.modSource, Copies.CopyOf(copy));

    // Whether an entry for a mod installed more than once from its source has this copy on, by the rule our data's ticks
    // go by (Ticks.Ticked) over its CopiesOff: the entry on, and the copy not off there, or new to our record, or every
    // installed copy of the group our record knows off there too (the ticked ones missing: they all read on). An entry
    // with none off (before HoldCopies) has every copy on.
    static bool On(ProfileMod entry, string id, ModSource source, string copy) => entry.Enabled
        && (entry.CopiesOff is not { } off || new Ticks(off.Select(c => $"{id}|{c}"), Store.Data.Copies).Ticked(id, copy, TwinCopies(id, source) ?? []));

    // The copies of an id and source installed more than once from it (Copies.CopyOf), in list order; null for none.
    static List<string>? TwinCopies(string id, ModSource source) =>
        Copies.TwinGroups().Where(g => g.Id == id && g.Source == source).Select(g => g.Copies).FirstOrDefault();

    // The unsaved edit becomes the profile's saved state.
    public static void Save()
    {
        if (Active is not { } profile)
            return;
        profile.Mods = Merge(profile.Mods, Current());
        (profile.Edited, profile.EditedAt, profile.SavedAt) = (null, null, DateTime.Now);
        Store.Save();
        Switched?.Invoke();
    }

    // Back to the saved state; the edit is dropped (undoable, like every change).
    public static void Discard(NModdingScreen screen)
    {
        if (Active is not { } profile)
            return;
        _missingDismissed = null; // its missing-mods banner shows again
        HoldCopies(profile);
        Apply(profile.Mods, screen);
        (profile.Edited, profile.EditedAt) = (null, null);
        Store.Save();
        Redraw(screen);
        Switched?.Invoke();
    }

    // A mod a profile has on that isn't installed (a list imported, or a mod uninstalled since): its Workshop item where
    // the profile knows it ("w:<item>"), else none (a local mod, or one listed without a link).
    public record MissingMod(string Id, ModSource Source, ulong? Item, string Name);

    // The profile's (default: the active one's) missing mods, as it is now: its unsaved edit if it has one.
    public static List<MissingMod> Missing(Profile? profile = null)
    {
        if ((profile ?? Active) is not { } p)
            return [];
        var installed = Installed().Select(m => (m.Id, m.Source)).ToHashSet();
        return [.. (p.Edited ?? p.Mods).Where(m => m.Enabled && !Manager(m.Id) && !installed.Contains((m.Id, m.Source)))
            .DistinctBy(m => (m.Id, m.Source)).Select(m => new MissingMod(m.Id, m.Source, ItemOf(m.Copy), m.Name ?? m.Id))];
    }

    static ulong? ItemOf(string? copy) => copy is { } c && c.StartsWith("w:") && ulong.TryParse(c[2..], out var item) ? item : null;

    // "Disable missing mods": the active profile's missing mods taken out of it, its saved state and its edit, so it
    // no longer asks for them (a later install arrives off, as a mod it doesn't know). One undo step.
    public static void DisableMissing(NModdingScreen screen)
    {
        if (Active is not { } profile || Missing(profile).Select(m => (m.Id, m.Source)).ToHashSet() is not { Count: > 0 } missing)
            return;
        foreach (var list in new[] { profile.Mods, profile.Edited })
            list?.RemoveAll(entry => entry.Enabled && missing.Contains((entry.Id, entry.Source)));
        Store.Save();
        Redraw(screen);
        Switched?.Invoke();
    }

    // The missing-mods banner dismissed, for the active profile, this session: it shows again once the profile is
    // switched to again, or its edit discarded.
    static string? _missingDismissed;
    public static bool MissingDismissed => Active is { } profile && _missingDismissed == profile.Name;
    public static void DismissMissing() => _missingDismissed = Active?.Name;

    public enum NewKind { Blank, Duplicate, SaveAsNew }

    // A new profile, made active:
    // - Blank: nothing on but this mod;
    // - Duplicate: a copy of the active profile's saved state (the active one keeps any unsaved edit);
    // - SaveAsNew: the mod list as it is now (the active profile's edit moves into it), with the mods it keeps that
    //   aren't installed, as Save keeps them.
    public static Profile Create(NewKind kind, string name, NModdingScreen screen)
    {
        var from = Active;
        var mods = kind switch
        {
            NewKind.Blank => Installed().Select(m => new ProfileMod(m.Id, m.Source, m.Id == ModEntry.ModId)).ToList(),
            NewKind.Duplicate when from != null => from.Mods.ToList(),
            NewKind.SaveAsNew when from != null => Merge(from.Edited ?? from.Mods, Current()),
            _ => Current(),
        };
        var profile = new Profile { Name = name, Mods = mods, SavedAt = DateTime.Now };
        var list = Store.Data.Profiles;
        list.Insert(from != null && kind != NewKind.Blank ? list.IndexOf(from) + 1 : list.Count, profile);
        if (kind == NewKind.SaveAsNew)
        {
            if (from != null)
                (from.Edited, from.EditedAt) = (null, null);
            Store.Data.ActiveProfile = profile.Name;
            Store.Save();
            screen.OnModEnabledOrDisabled();
            Switched?.Invoke();
        }
        else
            SwitchTo(profile, screen);
        return profile;
    }

    // A profile to share (ProfileShare): every installed copy in load order, on or off as the profile has it (one it has
    // never seen, as it is now), each with its own Workshop Id, alias and notes; of copies from one source (one place in
    // the game's list), the one that loads as the profile has them first (Copies.Next on its switches and ticks), so a
    // list read by its first row on (0.1.4 takes that one) gets the same copy. Alias, notes and the mods that are off go
    // in only when the share dialog's tick boxes say so (ProfileCsv.Options). Mods the profile keeps that aren't
    // installed aren't listed: nothing to name them by.
    public static List<ProfileCsv.Row> ShareRows(Profile profile)
    {
        var entries = profile.Edited ?? profile.Mods;
        var mods = Index(entries);
        var places = new Dictionary<(string, ModSource), int>();
        var list = SaveManager.Instance.SettingsSave.ModSettings?.ModList ?? [];
        for (var i = 0; i < list.Count; i++)
            places.TryAdd((list[i].Id, list[i].Source), i);
        // The profile's switches over the mod list's (a mod it has never seen as it is now), and its ticks.
        var gameOff = Dependencies.SavedOff();
        foreach (var entry in entries)
            if (entry.Enabled)
                gameOff.Remove((entry.Id, entry.Source));
            else
                gameOff.Add((entry.Id, entry.Source));
        var ticks = Ticks.Of(entries);
        bool Loads(Mod m) => Copies.Twins(m).Count > 0 && m.manifest?.id is { } id && Copies.LoadingIn(id, gameOff, ticks) is { } loads
            && Copies.CopyOf(loads) == Copies.CopyOf(m);
        return AllMods().OrderBy(m => places.TryGetValue((m.manifest?.id ?? "", m.modSource), out var i) ? i : int.MaxValue).ThenBy(m => Loads(m) ? 0 : 1)
            .Select(m =>
            {
                var id = m.manifest?.id ?? "";
                var on = mods.GetValueOrDefault((id, m.modSource)) is { } entry ? CopyOn(m, entry) : Copies.Ticked(m);
                var info = Store.InfoFor(m); // this copy's
                return new ProfileCsv.Row(id, m.manifest?.name ?? "", m.manifest?.version ?? "", on, info?.Category ?? "",
                    Compat.WorkshopId(m) is { } workshop ? workshop.ToString() : "", info?.Alias ?? "", info?.Notes ?? "");
            }).ToList();
    }

    // What a shared list would do, compared with the mod list now (ProfileShare shows it first). Mods: the new
    // profile's entries. On, Off: installed copies it turns on and off; Same: ones already that way; Unlisted: installed
    // copies it doesn't name (RowsOf), left as they are (no box changed, the same copy loads). Missing: ones it names
    // that aren't installed, by id (a row for an id installed sets that copy: the preview never offers to subscribe to a
    // second copy of a mod the player has); the profile keeps those too (by id, as Workshop mods if the list links them,
    // else local), so installing one later and switching to it sets it. A mod installed while this profile is active
    // arrives on; if the list had it off, that shows as an unsaved change. The mod managers are in none of the counts: a
    // profile never turns them on or off. BothCopies: listed mods on from the Workshop whose copy installed by hand stays
    // on (or is turned on), so both copies are on once the Workshop one is there too; the preview says so (one copy should
    // be on).
    public record ImportPlan(List<ProfileMod> Mods, int On, int Off, int Same, int Unlisted, List<ProfileCsv.Entry> Missing, ImportExtras Extras,
        List<ProfileCsv.Entry> BothCopies);

    // What else a list gives: categories, aliases and notes, taken only where a mod has none of its own. Categories are
    // every profile's, so an import never moves a mod that's already in one, nor renames anything. Sorted: each mod's key
    // (installed copies by theirs, a mod not installed by its id, so it arrives in its category) and its category;
    // NewCategories: the ones to create, in list order; Named: the aliases and notes to fill in (null: left as it is).
    public record ImportExtras(List<(string Key, string Category)> Sorted, List<string> NewCategories, List<(string Key, string? Alias, string? Notes)> Named)
    {
        public int Details => Named.Sum(n => (n.Alias != null ? 1 : 0) + (n.Notes != null ? 1 : 0));
    }

    // Each id's first category, alias and notes in the list (rows for two copies of it may differ: the first that gives
    // one counts), for every copy of it installed, or for the id when none is.
    static ImportExtras PlanExtras(IReadOnlyList<ProfileCsv.Entry> entries)
    {
        var (sorted, created, named) = (new List<(string, string)>(), new List<string>(), new List<(string, string?, string?)>());
        foreach (var rows in entries.Where(e => !Manager(e.Id)).GroupBy(e => e.Id))
        {
            string First(Func<ProfileCsv.Entry, string> value) => rows.Select(value).FirstOrDefault(v => v.Length > 0) ?? "";
            var (category, alias, notes) = (First(e => e.Category), First(e => e.Alias), First(e => e.Notes));
            if (category.Length == 0 && alias.Length == 0 && notes.Length == 0)
                continue;
            var keys = AllMods().Where(m => m.manifest?.id == rows.Key).Select(Copies.Key).Distinct().ToList();
            foreach (var key in keys.Count > 0 ? keys : [rows.Key])
            {
                var info = Store.InfoFor(key);
                // A category of its own that's gone counts as none: the mod shows under Uncategorized. The list's category
                // is one of the player's, or one made already: by its name, or else the same name in another case ("tools" goes
                // into Tools).
                if (category.Length > 0 && !(info?.Category is { } own && Categories.Find(own) != null))
                {
                    bool Same(string name) => string.Equals(name, category, StringComparison.OrdinalIgnoreCase);
                    var name = Categories.Find(category)?.Name ?? Store.Data.Categories.FirstOrDefault(c => Same(c.Name))?.Name ?? created.FirstOrDefault(Same);
                    if (name == null)
                        created.Add(name = category);
                    sorted.Add((key, name));
                }
                var (newAlias, newNotes) = (alias.Length > 0 && string.IsNullOrEmpty(info?.Alias) ? alias : null,
                    notes.Length > 0 && string.IsNullOrEmpty(info?.Notes) ? notes : null);
                if (newAlias != null || newNotes != null)
                    named.Add((key, newAlias, newNotes));
            }
        }
        return new ImportExtras(sorted, created, named);
    }

    // Counted per copy, as Changes counts. Each installed copy is set by the rows that name it (RowsOf), so where its id
    // is installed more than once, a row linking a Workshop item sets that copy alone and leaves a copy installed by hand
    // beside it as it is.
    public static ImportPlan PlanImport(IReadOnlyList<ProfileCsv.Entry> entries)
    {
        var current = Current();
        var byId = entries.ToLookup(e => e.Id); // in list order
        var all = AllMods().ToList();
        var mods = new List<ProfileMod>();
        var (on, off, same, unlisted) = (0, 0, 0, 0);
        foreach (var mod in current.Where(c => !Manager(c.Id)))
        {
            var copies = all.Where(m => m.manifest?.id == mod.Id && m.modSource == mod.Source).ToList();
            var local = all.Any(m => m.manifest?.id == mod.Id && m.modSource == ModSource.ModsDirectory);
            var rows = copies.ToDictionary(c => c, c => RowsOf(c, byId[mod.Id], local));
            if (Planned(mod, copies, rows) is not { } planned)
            {
                unlisted += copies.Count;
                continue;
            }
            mods.Add(planned);
            foreach (var copy in copies)
                switch ((Listed: rows[copy].Count > 0, Then: Copies.Ticked(copy), Now: CopyOn(copy, planned)))
                {
                    case (false, _, _):
                        unlisted++;
                        break;
                    case var (_, then, now) when then == now:
                        same++;
                        break;
                    case (_, _, true):
                        on++;
                        break;
                    default:
                        off++;
                        break;
                }
        }
        // Not installed, by id and link, so a list naming two copies of a mod not installed names both.
        var ids = current.Select(c => c.Id).ToHashSet();
        var missing = entries.Where(e => !Manager(e.Id) && !ids.Contains(e.Id)).DistinctBy(e => (e.Id, e.WorkshopLink)).ToList();
        // Each id and source not installed on if one of its rows is; where two Workshop rows name it, the one on is the copy
        // that loads once installed.
        foreach (var rows in missing.GroupBy(e => (e.Id, Source: e.WorkshopLink.Length > 0 ? ModSource.SteamWorkshop : ModSource.ModsDirectory)))
        {
            // A Workshop mod keeps its item ("w:<item>": the row on, else the first), so it can be subscribed to from the
            // missing-mods list later.
            var loads = rows.FirstOrDefault(e => e.Enabled);
            var row = loads ?? rows.First();
            mods.Add(new ProfileMod(rows.Key.Id, rows.Key.Source, loads != null, rows.Key.Source == ModSource.SteamWorkshop ? CopyOfLink(row.WorkshopLink) : null,
                row.Name.Length > 0 ? row.Name : null));
        }
        // A Workshop row on for an id installed by hand: that copy on after the import (as planned, or as it is when the list
        // doesn't name it) beside the Workshop one.
        var both = entries.Where(e => !Manager(e.Id) && e.Enabled && e.WorkshopLink.Length > 0
                && current.Any(c => c.Id == e.Id && c.Source == ModSource.SteamWorkshop)) // a Workshop copy installed too (an import adds none)
            .Where(e => current.FirstOrDefault(c => c.Id == e.Id && c.Source == ModSource.ModsDirectory) is { } local
                && (mods.FirstOrDefault(m => m.Id == e.Id && m.Source == ModSource.ModsDirectory)?.Enabled ?? local.Enabled))
            .DistinctBy(e => e.Id).ToList();
        return new ImportPlan(mods, on, off, same, unlisted, missing, PlanExtras(entries), both);
    }

    // The copy a Workshop link names ("w:<item>"), or null for none.
    static string? CopyOfLink(string link) =>
        link.Length > 0 && ulong.TryParse(link[(link.LastIndexOf('=') + 1)..], out var item) ? $"w:{item}" : null;

    // The rows of a shared list (its id's) that set an installed copy. Its id installed once: every row of it, linked ones
    // first, whichever copy they link (a list names a mod by its id: a friend's Workshop copy is the one installed here by
    // hand). Else those linking its Workshop item; else those with no link, which name a copy installed by hand, or one
    // whose item can't be told: a Workshop copy takes them only where its id has no local copy installed (local: one is).
    // None: the list doesn't name it, so it's left as it is.
    static List<ProfileCsv.Entry> RowsOf(Mod copy, IEnumerable<ProfileCsv.Entry> listed, bool local)
    {
        var rows = listed.ToList();
        if (Copies.Others(copy).Count == 0)
            return [.. rows.OrderBy(e => e.WorkshopLink.Length > 0 ? 0 : 1)];
        if (Compat.WorkshopId(copy) is { } item)
        {
            var link = ProfileCsv.WorkshopLink(item);
            if (rows.Where(e => e.WorkshopLink == link).ToList() is { Count: > 0 } linked)
                return linked;
            if (copy.modSource == ModSource.SteamWorkshop && local)
                return [];
        }
        return [.. rows.Where(e => e.WorkshopLink.Length == 0)];
    }

    // A listed mod's entry for one id and source installed, from its copies' rows (RowsOf; a copy with none as it is now):
    // each copy on if one of its rows is, the entry on if a copy is. Copies given the same rows, none linked (two copies
    // installed by hand, or Workshop ones whose item can't be read), can't be told apart by them: those rows set only
    // whether they're on, each keeping its box as it reads with the switch on (the CopiesOff Current gave mod), so a list
    // shared from this mod list reads back as it is. Of copies from one source, the rest go in CopiesOff, beside the
    // copies our record has that aren't installed, kept as they are (no row can name them); which of those on loads is
    // worked out from that (Copies.Next). None on: the copies off as they are, so turned on elsewhere the same copies come
    // back. Null when no row names a copy of it.
    static ProfileMod? Planned(ProfileMod mod, List<Mod> copies, Dictionary<Mod, List<ProfileCsv.Entry>> rows)
    {
        if (copies.All(c => rows[c].Count == 0))
            return null;
        bool Alike(Mod copy) => rows[copy] is { Count: > 0 } own && own.All(e => e.WorkshopLink.Length == 0) && copies.Any(c => c != copy && rows[c].SequenceEqual(own));
        var on = copies.Where(c => rows[c].Count == 0 ? Copies.Ticked(c)
            : rows[c].Any(e => e.Enabled) && (!Alike(c) || On(mod with { Enabled = true }, mod.Id, mod.Source, Copies.CopyOf(c)))).ToList();
        if (copies.Count < 2 || on.Count == 0)
            return mod with { Enabled = on.Count > 0 };
        var installed = copies.Select(Copies.CopyOf).ToHashSet();
        return mod with
        {
            Enabled = true,
            CopiesOff = [.. copies.Except(on).Select(Copies.CopyOf).Concat((mod.CopiesOff ?? []).Where(c => !installed.Contains(c))).Distinct().Order(StringComparer.Ordinal)],
        };
    }

    // A new profile from a shared list, placed after the active one and switched to, as New does, with the list's
    // categories, aliases and notes where mods have none (the new categories after the player's): one undo step.
    public static Profile Import(string name, ImportPlan plan, NModdingScreen screen)
    {
        var from = Active;
        var profile = new Profile { Name = name, Mods = plan.Mods.ToList(), SavedAt = DateTime.Now };
        Store.Batch(() =>
        {
            foreach (var category in plan.Extras.NewCategories.Where(c => Categories.Find(c) == null))
                Store.Data.Categories.Add(new Category { Name = category });
            foreach (var (key, category) in plan.Extras.Sorted)
                Categories.Assign(key, category);
            foreach (var (key, alias, notes) in plan.Extras.Named)
                Store.EditMod(key, info => (info.Alias, info.Notes) = (alias ?? info.Alias, notes ?? info.Notes));
            var list = Store.Data.Profiles;
            list.Insert(from != null ? list.IndexOf(from) + 1 : list.Count, profile);
            SwitchTo(profile, screen);
        });
        return profile;
    }

    // An imported profile's name: the file's (ours, spire-mods-<profile>-<time>, gives the profile's), or "Imported
    // profile" (also for Better Mod Menu's time-named exports); "(1)" and on after it when that's taken.
    public static string ImportName(string? file)
    {
        var name = Path.GetFileNameWithoutExtension(file ?? "").Trim();
        if (System.Text.RegularExpressions.Regex.IsMatch(name, @"^mod_list\.\d{8}-\d{6}(-\d+)?$"))
            name = "";
        // Ours, "spire-mods-<profile>-<yyyyMMddHHmmss>" (ProfileShare.FileName): the profile's name.
        else if (System.Text.RegularExpressions.Regex.Match(name, $@"^{ProfileShare.FilePrefix}(.*?)-?\d{{14}}$") is { Success: true } ours)
            name = ours.Groups[1].Value.Trim();
        if (name.Length > 40) // the name box's limit
            name = Menus.Prefix(name, 40).Trim();
        if (name.Length == 0)
            name = Menus.Text("SPIRE_MOD_MANAGER.IMPORTED_PROFILE");
        return Store.Data.Profiles.Any(p => p.Name == name) ? CopyName(name) : name;
    }

    // Why a name can't be used, or null: empty, or another profile has it.
    public static string? NameProblem(string name, Profile? renaming = null)
    {
        name = name.Trim();
        if (name.Length == 0)
            return Menus.Text("SPIRE_MOD_MANAGER.NAME_EMPTY");
        return Store.Data.Profiles.Any(p => p != renaming && p.Name == name)
            ? Menus.Format("SPIRE_MOD_MANAGER.NAME_TAKEN", ("name", name))
            : null;
    }

    // "Modded" -> "Modded (1)", or the first free number; "Modded (1)" -> "Modded (2)".
    public static string CopyName(string name) => Numbered(name, n => Store.Data.Profiles.Any(p => p.Name == n));

    // The name with the first " (n)" not taken, in place of any it ends in (Better Mod Menu's import uses it too).
    public static string Numbered(string name, Func<string, bool> taken)
    {
        var bare = System.Text.RegularExpressions.Regex.Replace(name, @" \(\d+\)$", "");
        for (var n = 1; ; n++)
            if (!taken($"{bare} ({n})"))
                return $"{bare} ({n})";
    }

    // "Profile 3": the first free number.
    public static string NewName()
    {
        for (var n = Store.Data.Profiles.Count + 1; ; n++)
        {
            var name = Menus.Format("SPIRE_MOD_MANAGER.NEW_PROFILE_NAME", ("n", n.ToString()));
            if (Store.Data.Profiles.All(p => p.Name != name))
                return name;
        }
    }

    // "12 mods": how many are on in the mod list right now, this mod and Better Mod Menu included (the active profile's
    // count in the profile list, ModCount).
    public static string CountText()
    {
        var on = OnCount();
        return Menus.Format(on == 1 ? "SPIRE_MOD_MANAGER.PROFILE_MODS_ONE" : "SPIRE_MOD_MANAGER.PROFILE_MODS", ("count", Menus.Number(on)));
    }

    // That count as a number (the main menu button shows it too).
    public static int OnCount() => Current().Where(m => m.Enabled).Select(m => ModEntry.CountKey(m.Id)).Distinct().Count();

    // For the profile list: "12 mods · Oct 11": mods on as they'd be after switching to it (one it has never seen as
    // it is now), and when it was last saved, in the game's language and date order.
    public static string Details(Profile profile)
    {
        var text = ModCount(profile);
        return profile.SavedAt is { } saved ? $"{text} · {ShortDate(saved)}" : text;
    }

    public static string ShortDate(DateTime date) =>
        date.ToString(Menus.Culture.DateTimeFormat.MonthDayPattern.Replace("MMMM", "MMM"), Menus.Culture);

    // "12 mods": a profile's mods on (the list's count; Delete's question names it too).
    public static string ModCount(Profile profile)
    {
        var mods = Index(profile.Edited ?? profile.Mods);
        // As it would be after switching to it: the mod managers as they are now (a profile leaves them alone; the active
        // profile's as it is now), every other mod as the profile has it.
        var on = profile == Active ? OnCount()
            : Current().Where(c => Manager(c.Id) ? c.Enabled : mods.GetValueOrDefault((c.Id, c.Source))?.Enabled ?? c.Enabled)
                .Select(c => ModEntry.CountKey(c.Id)).Distinct().Count();
        return Menus.Format(on == 1 ? "SPIRE_MOD_MANAGER.PROFILE_MODS_ONE" : "SPIRE_MOD_MANAGER.PROFILE_MODS", ("count", Menus.Number(on)));
    }

    // Each copy's box as the profile has it (Dependencies.Write: a local and a Workshop copy both on included), and each
    // group of copies our record has (Copies.RecordGroup) its copies off as the profile has them, whole, copies not
    // installed included: so a profile naming a copy that's missing reads, and once it's back loads, as the profile has
    // it, as our data does (Ticks' read rule). The mod managers stay as they are, and so does a mod the profile has never
    // seen.
    static void Apply(List<ProfileMod> mods, NModdingScreen? screen)
    {
        var wants = Index(mods);
        _applying = true;
        try
        {
            var wanted = new List<(Mod, bool)>();
            foreach (var copy in AllMods())
                if (copy.manifest?.id is { } id && !Manager(id) && wants.GetValueOrDefault((id, copy.modSource)) is { } entry)
                    wanted.Add((copy, CopyOn(copy, entry)));
            var offs = wants.Values.Where(e => !Manager(e.Id) && e.CopiesOff != null && Copies.RecordGroup(e.Id, e.Source) != null)
                .ToDictionary(e => (e.Id, e.Source), e => e.CopiesOff ?? []);
            Dependencies.Write(wanted, offs);
            if (screen != null)
                RefreshTickboxes(screen);
        }
        finally
        {
            _applying = false;
        }
    }

    // Mod managers: this one and Better Mod Menu. A profile never turns them on or off, and they aren't counted or listed
    // as its changes.
    static bool Manager(string id) => id is ModEntry.ModId or Bmm.ModId;

    // A profile's entries by mod (the first, if listed twice); a mod it has never seen has none.
    static Dictionary<(string, ModSource), ProfileMod> Index(List<ProfileMod> mods)
    {
        var index = new Dictionary<(string, ModSource), ProfileMod>();
        foreach (var pm in mods)
            index.TryAdd((pm.Id, pm.Source), pm);
        return index;
    }

    // Whether a mod's on/off in the current list differs from the profile's: its switch or, on, for a mod installed more
    // than once from one source, any installed copy's own box (with the same copies and versions installed, the same boxes
    // mean the same copy loads). A mod the profile has never seen never differs (switching to the profile leaves it as it
    // is); the managers don't count.
    static bool Differs(ProfileMod current, Dictionary<(string, ModSource), ProfileMod> profile) =>
        !Manager(current.Id) && profile.GetValueOrDefault((current.Id, current.Source)) is { } saved
        && (saved.Enabled != current.Enabled || current.Enabled && TwinCopies(current.Id, current.Source) is { } copies
            && copies.Any(c => On(saved, current.Id, current.Source, c) != On(current, current.Id, current.Source, c)));

    // Same on/off for every installed mod.
    static bool Same(List<ProfileMod> current, List<ProfileMod> profile)
    {
        var saved = Index(profile);
        return !current.Any(c => Differs(c, saved));
    }

    // The current list, plus the old list's entries for mods that aren't installed right now, and the copies an entry had
    // off that our record has never had (so one that comes later is off again). Every copy the record has is in the
    // current entry's CopiesOff already (Current, by Copies.RecordGroup), so the old entry's word on those would override
    // what our data has.
    static List<ProfileMod> Merge(List<ProfileMod> old, List<ProfileMod> current)
    {
        var installed = current.Select(c => (c.Id, c.Source)).ToHashSet();
        var was = Index(old);
        return old.Where(pm => !installed.Contains((pm.Id, pm.Source))).Concat(current.Select(now =>
            was.GetValueOrDefault((now.Id, now.Source))?.CopiesOff?.Where(c => now.CopiesOff?.Contains(c) != true
                && Store.Data.Copies?.GetValueOrDefault(now.Id)?.Contains(c) != true).ToList() is { Count: > 0 } gone
                ? now with { CopiesOff = [.. (now.CopiesOff ?? []).Concat(gone).Distinct().Order(StringComparer.Ordinal)] } : now)).ToList();
    }

    // Ticks the screen's tickboxes to match the saved mod list (setting them doesn't fire their toggle), as the view reads
    // it: Vanilla View as the game does (one switch for every copy of an id and source), our views per copy (its own box:
    // copies of a mod from one source can be on together). Again on every view switch, so no box is stale.
    public static void RefreshTickboxes(NModdingScreen screen)
    {
        var (off, ticks, vanilla) = (Dependencies.SavedOff(), Ticks.Now, Store.Data.VanillaView);
        foreach (var row in screen.GetNode("%ModsScrollContainer/Mask/Content").GetChildren().OfType<NModMenuRow>())
        {
            if (row.Mod is not { } mod)
                continue;
            var on = vanilla ? !off.Contains((mod.manifest?.id, mod.modSource)) : Copies.TickedIn(mod, off, ticks);
            // Only where it differs (setting the box alone fires no toggle).
            if (row.GetNode<NTickbox>("Tickbox") is var tickbox && tickbox.IsTicked != on)
                tickbox.IsTicked = on;
        }
    }

    // After a profile wrote the mod list: the whole list redrawn, as after undo, and the pending-changes warning. The
    // tickboxes set above fire no toggle, so the list would otherwise show the old on/off.
    static void Redraw(NModdingScreen screen) => CategoryList.Redraw(screen.GetNode<Control>("%ModsScrollContainer/Mask/Content"));

    public static void NotifySwitched() => Switched?.Invoke();

    // A new profile starting from the current mod list (the first one, on first use).
    public static Profile Add(string? name = null)
    {
        var profile = new Profile { Name = name ?? NewName(), Mods = Current(), SavedAt = DateTime.Now };
        Store.Data.Profiles.Add(profile);
        Store.Save();
        return profile;
    }

    // Never the last profile. Deleting the active one switches to its neighbour (the next, else the one before), one undo
    // step: out of the list first, so the switch's save records both together. A Store.Batch can't: the switch's redraw
    // records a step of its own (ModToggledPatch), mid-batch.
    public static bool Delete(Profile profile, NModdingScreen screen)
    {
        var list = Store.Data.Profiles;
        if (list.Count <= 1 || !list.Contains(profile))
            return false;
        var (i, active) = (list.IndexOf(profile), profile == Active);
        list.Remove(profile);
        if (active)
            SwitchTo(list[Math.Min(i, list.Count - 1)], screen);
        Store.Save();
        Switched?.Invoke();
        return true;
    }

    public static bool Rename(Profile profile, string name)
    {
        name = name.Trim();
        if (name.Length == 0 || Store.Data.Profiles.Any(p => p != profile && p.Name == name))
            return false;
        if (Store.Data.ActiveProfile == profile.Name)
            Store.Data.ActiveProfile = name;
        profile.Name = name;
        Store.Save();
        return true;
    }

    // Every installed copy: the ones the game loaded at launch and the ones installed since (they load after a
    // relaunch), so a mod installed while the game runs belongs to profiles at once.
    static IEnumerable<Mod> AllMods() => Dependencies.EachCopy(ModManager.Mods.Union(SpireModManager.Installed.Mods));

    static IEnumerable<(string Id, ModSource Source)> Installed() =>
        AllMods().Select(m => (m.manifest?.id ?? "", m.modSource)).Distinct();

    // The mod list now, one entry per id and source as the game keeps it; for a group of copies our record has
    // (Copies.RecordGroup), the copies of it off (CopiesOff), installed or not, so saving while one is missing keeps its
    // tick; those kept while its switch is off too, so switching back ticks the same copies.
    static List<ProfileMod> Current()
    {
        var off = Dependencies.SavedOff();
        var record = Store.Data.Copies is { Count: > 0 } any ? any : null;
        return Installed()
            .Select(m => new ProfileMod(m.Id, m.Source, !off.Contains((m.Id, m.Source)),
                CopiesOff: record == null || Copies.RecordGroup(m.Id, m.Source, record) is not { } copies ? null : OffNow(m.Id, copies)))
            .ToList();
    }
}
