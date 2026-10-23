using Godot;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;
using MegaCrit.Sts2.Core.Saves;

namespace SpireModManager;

// A profile is a snapshot of every mod's on/off state, by id + source like the game's mod list; for a mod installed
// more than once from one source (one switch in the game's list), each copy's own box and which of them loads too.
// Changing the mod list doesn't change the saved profile: it becomes the profile's unsaved edit, shown as
// "Unsaved" with Save, Save as new and Discard. Switching away keeps the edit with its profile ("Name *"
// in the list); coming back restores it, so it is never lost.
// A mod a profile has never seen (installed since it was last used) joins it as it is in the mod list, which for a
// new mod is on, the game's default:
// - the active profile takes it in at launch, or as soon as it's installed while the game runs, quietly: it isn't
//   an unsaved edit, and neither undo nor Revert all takes it away;
// - switching to a profile that has never seen a mod leaves the mod as it is, and the profile keeps it that way
//   from then on (so a switch never turns a new mod off, or on).
// Spire Mod Manager itself is always on.
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
            changed |= HoldCopies(profile, profile.Name == data.ActiveProfile ? Picks.Now : null);
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
    // profile has the new copy off (it arrives ticked where its switch is on), every profile holds the copy picked (the
    // one that was there), as at launch, and the other profiles read the new copy as on, as they read a mod they've never
    // seen. A copy uninstalled and back keeps the tick the profile saved for it, as the same reinstall before a launch
    // does. Quietly (see History.Quietly). Fails soft: it runs inside the game's mod detection, beside Changes' own
    // handler.
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
                // Each snapshot's active profile by that snapshot's own picks: undo brings them back together.
                foreach (var each in data.Profiles)
                    HoldCopies(each, each.Name == data.ActiveProfile ? new Picks(data.CopyPicks, data.Copies, data.CopiesOff) : null);
                took |= TakeIn(data.Profiles.FirstOrDefault(p => p.Name == data.ActiveProfile), arrived: true, copy);
            });
            // Its being on is the profile's unsaved edit. The arrival isn't the player's step, and the edit follows the
            // mod list (worked out again wherever undo lands): now only, not as a step. At the frame's end, once the
            // rest of the arrival has run (a second copy's entry seeded, its pick held), so the edit is worked out once.
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
            if (i >= 0 && profile.Mods[i] is var entry && entry.Copy != name && entry.CopiesOff?.Contains(name) != true)
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

    // Gives a profile's entries for a mod installed more than once from one source the copy picked now and the copies off
    // now where they name none (profiles from before copies were told apart, Better Mod Menu's, which keep the pick there
    // is), as TakeIn does for a mod it has never seen; and an entry from before copies had a box each its copies off as
    // it read then (all but the copy it names). From then on a tick on another copy is a change to the profile, as for any
    // mod. The active profile's entry naming a pick that's missing has the copy standing in for it on, as our data does
    // (Copies.StandIn); another profile keeps the copies it has off. ours: the picks of the data the profile is active in
    // (our data's, or an undo snapshot's), null for a profile that isn't. At launch, before the undo history starts, and as
    // a profile is switched to. True if any changed.
    static bool HoldCopies(Profile profile, Picks? ours)
    {
        var changed = false;
        // A mod now installed once from a source (its twin gone): its copy isn't off in any entry, as for our data
        // (Copies.Reconcile), so a twin installed later doesn't take over through a profile that still has it off. The copy
        // an entry names stays, as our pick does: one missing for a launch loads again through the profile once it's back.
        foreach (var alone in Copies.Alone())
            if (alone.manifest?.id is { } aloneId)
            {
                var copy = Copies.CopyOf(alone);
                foreach (var list in new[] { profile.Mods, profile.Edited })
                    for (var i = 0; i < (list?.Count ?? 0); i++)
                        if (list?[i] is { } entry && entry.Id == aloneId && entry.Source == alone.modSource && entry.CopiesOff?.Contains(copy) == true)
                        {
                            list[i] = entry with { CopiesOff = entry.CopiesOff?.Where(c => c != copy).ToList() };
                            changed = true;
                        }
            }
        foreach (var (id, source, copies) in Copies.TwinGroups())
            if (Copies.Chosen(id, source) is { } pick)
                foreach (var list in new[] { profile.Mods, profile.Edited })
                    for (var i = 0; i < (list?.Count ?? 0); i++)
                        if (list?[i] is { } entry && entry.Id == id && entry.Source == source && (entry.Copy == null || entry.CopiesOff == null))
                        {
                            list[i] = entry.Copy is { } copy
                                ? entry with { CopiesOff = [.. copies.Where(c => c != copy).Order(StringComparer.Ordinal)] }
                                : entry with { Copy = pick, CopiesOff = entry.CopiesOff ?? OffNow(id, copies) };
                            changed = true;
                        }
        // The active profile, applied, keeps the stand-in loading; once the pick is back, the same copies are ticked as in our
        // data. Another profile keeps its own choice: switched to, it loads the copy Write picks for it, and the missing pick
        // comes back only where that copy is the stand-in (KeepNamed), so nothing it has off comes on.
        if (ours == null)
            return changed;
        foreach (var (id, source, _) in Copies.TwinGroups())
            if (Copies.StandIn(id, source, ours) is { } standIn)
                foreach (var list in new[] { profile.Mods, profile.Edited })
                    for (var i = 0; i < (list?.Count ?? 0); i++)
                        if (list?[i] is { } entry && entry.Id == id && entry.Source == source && entry.Copy == standIn.Pick
                            && entry.CopiesOff?.Contains(standIn.Copy) == true)
                        {
                            list[i] = entry with { CopiesOff = entry.CopiesOff?.Where(c => c != standIn.Copy).ToList() };
                            changed = true;
                        }
        return changed;
    }

#if DEVTOOLS
    // Self-test: HoldCopies on a made-up profile, as the active one or another.
    public static bool TestHold(Profile profile, bool active) => HoldCopies(profile, active ? Picks.Now : null);

    // Self-test: a profile's entries applied as a switch, Discard or an import applies them, with no page to tick.
    public static void TestApply(List<ProfileMod> mods) => Apply(mods, null);
#endif

    // Whether HoldCopies has an entry to fill in: one naming no copy, or no copies off, for a mod installed more than once
    // from one source.
    static bool Unheld(Profile profile) => Copies.TwinGroups().Any(g =>
        profile.Mods.Concat(profile.Edited ?? []).Any(m => m.Id == g.Id && m.Source == g.Source && (m.Copy == null || m.CopiesOff == null)));

    // The copies of an id and source that are off now while its switch is on (StoreData.CopiesOff; data from before it:
    // all but the pick), sorted.
    static List<string> OffNow(string id, IEnumerable<string> copies)
    {
        var off = (Store.Data.CopiesOff ?? Copies.LegacyOff(Store.Data)).ToHashSet();
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
        Apply(target.Edited ?? target.Mods, screen); // mods it has never seen stay as they are,
        TakeIn(target); // and it keeps them that way from now on
        HoldCopies(target, Picks.Now); // the copies picked and off too, where it names none
        Store.Data.ActiveProfile = target.Name;
        Store.Save();
        Track(); // the list as it is now, against the profile (normally the same)
        Redraw(screen);
        Switched?.Invoke();
    }

    // The active profile's unsaved changes: copies turned on and off compared with its saved state (keys, for links to
    // them, and names; a copy of a mod installed twice says which), and mods installed more than once where another copy
    // loads after a relaunch, no box of theirs changed (the copy that loads as the profile has it, the one now), as
    // Changes has them (HistoryBar.CopySwitches): a pick that changes nothing that loads isn't one.
    public static (List<(string Id, string Name)> On, List<(string Id, string Name)> Off, List<(Mod From, Mod To)> Switched) UnsavedChanges()
    {
        if (Active is not { Edited: not null } profile)
            return ([], [], []);
        var saved = Index(profile.Mods);
        var (on, off) = (new List<(string, string)>(), new List<(string, string)>());
        var ticked = new HashSet<string>(); // ids with a box changed
        foreach (var copy in AllMods())
        {
            if (copy.manifest?.id is not { } id || Manager(id) || saved.GetValueOrDefault((id, copy.modSource)) is not { } entry)
                continue;
            var now = Copies.Ticked(copy);
            if (CopyOn(copy, entry) != now)
            {
                (now ? on : off).Add((Copies.LinkKey(copy), (copy.manifest?.name ?? id) + Copies.Which(copy)));
                ticked.Add(id);
            }
        }
        var current = Index(Current());
        var switched = new List<(Mod, Mod)>();
        foreach (var id in Copies.TwinGroups().Select(g => g.Id).Distinct())
            if (!Manager(id) && !ticked.Contains(id) && LoadingAs(id, saved) is { } from && LoadingAs(id, current) is { } to && Copies.CopyOf(from) != Copies.CopyOf(to))
                switched.Add((from, to));
        return (on, off, switched);
    }

    // The copy of an id that loads after a relaunch as these entries have it (each source's switch, the copy it loads and
    // those it has off; a source they don't name as it is now), by the same rule as now (Copies.LoadingIn).
    static Mod? LoadingAs(string id, Dictionary<(string, ModSource), ProfileMod> entries)
    {
        var gameOff = Dependencies.SavedOff();
        var (stored, off) = (Store.Data.CopyPicks ?? [], Store.Data.CopiesOff ?? Copies.LegacyOff(Store.Data));
        foreach (var source in AllMods().Where(m => m.manifest?.id == id).Select(m => m.modSource).Distinct().ToList())
        {
            if (entries.GetValueOrDefault((id, source)) is not { } entry)
                continue;
            if (entry.Enabled)
                gameOff.Remove((id, source));
            else
                gameOff.Add((id, source));
            if (Group(id, source) is not { } copies || PickOf(entry) is not { } pick)
                continue;
            stored = [.. stored.Where(k => Copies.Parse(k) is not { } other || other.Id != id || other.Source != source), $"{id}|{pick}"];
            // An entry naming no copies off has only the copy it loads on.
            var keys = copies.Select(c => $"{id}|{c}").ToHashSet();
            off = [.. off.Where(k => !keys.Contains(k)), .. copies.Where(c => c != pick && (entry.CopiesOff is not { } named || named.Contains(c))).Select(c => $"{id}|{c}")];
        }
        return Copies.LoadingIn(id, gameOff, new Picks(stored, Store.Data.Copies, off));
    }

    // Whether a copy is on as a profile's entry for its id and source has it: the entry on and, for a mod installed more
    // than once from that source, the copy on (On).
    static bool CopyOn(Mod copy, ProfileMod entry) => copy.manifest?.id == ModEntry.ModId || Copies.Twins(copy).Count == 0
        ? entry.Enabled : On(entry, Copies.CopyOf(copy));

    // Whether an entry for a mod installed more than once from its source has this copy on: the entry on, and the copy it
    // loads (PickOf), or one its CopiesOff doesn't name (none: an entry from before copies had a box each, only the copy it
    // loads). A copy it has never seen is on.
    static bool On(ProfileMod entry, string copy) => entry.Enabled && (PickOf(entry) == copy || entry.CopiesOff is { } off && !off.Contains(copy));

    // The copy an entry for a mod installed more than once from its source loads: the one it names where that's installed;
    // else the pick now, unless the entry has that one off: then its first copy installed it doesn't.
    static string? PickOf(ProfileMod entry)
    {
        if (entry.Copy is { } copy && Installs(entry.Id, entry.Source, copy))
            return copy;
        var now = Copies.Picked(entry.Id, entry.Source);
        return entry.CopiesOff is { } off && now != null && off.Contains(now)
            ? Group(entry.Id, entry.Source)?.FirstOrDefault(c => !off.Contains(c)) ?? now : now;
    }

    // Whether this copy of an id and source is installed, beside another from that source.
    static bool Installs(string id, ModSource source, string copy) => Group(id, source)?.Contains(copy) == true;

    // The copies of an id and source installed more than once from it (Copies.CopyOf), in list order; null for none.
    static List<string>? Group(string id, ModSource source) =>
        Copies.TwinGroups().Where(g => g.Id == id && g.Source == source).Select(g => g.Copies).FirstOrDefault();

    // The installed copy of an id and source a copy name is, or null.
    static Mod? CopyNamed(string id, ModSource source, string copy) =>
        AllMods().FirstOrDefault(m => m.manifest?.id == id && m.modSource == source && Copies.CopyOf(m) == copy);

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
        Apply(profile.Mods, screen);
        HoldCopies(profile, Picks.Now);
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
    // the game's list), the one that loads first, so a list read by its first row on gets that one. Alias, notes and the
    // mods that are off go in only when the share dialog's tick boxes say so (ProfileCsv.Options). Mods the profile
    // keeps that aren't installed aren't listed: nothing to name them by.
    public static List<ProfileCsv.Row> ShareRows(Profile profile)
    {
        var mods = Index(profile.Edited ?? profile.Mods);
        var places = new Dictionary<(string, ModSource), int>();
        var list = SaveManager.Instance.SettingsSave.ModSettings?.ModList ?? [];
        for (var i = 0; i < list.Count; i++)
            places.TryAdd((list[i].Id, list[i].Source), i);
        bool Loads(Mod m) => Copies.Twins(m).Count > 0 && m.manifest?.id is { } id
            && (mods.GetValueOrDefault((id, m.modSource)) is { } entry ? PickOf(entry) : Copies.Picked(id, m.modSource)) == Copies.CopyOf(m);
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
    // copies it doesn't name (RowsOf), left as they are. Switched: mods installed more than once where another copy loads
    // after the import with no box changed (the copy that loads now, then), as Changes words it. Missing: ones it names
    // that aren't installed, by id (a row for an id installed sets that copy: the preview never offers to subscribe to a
    // second copy of a mod the player has); the profile keeps those too (by id, as Workshop mods if the list links them,
    // else local), so installing one later and switching to it sets it. A mod installed while this profile is active
    // arrives on; if the list had it off, that shows as an unsaved change. The mod managers are in none of the counts: a
    // profile never turns them on or off. BothCopies: listed mods on from the Workshop whose copy installed by hand stays
    // on (or is turned on), so both copies are on once the Workshop one is there too; the preview says so (one copy should
    // be on).
    public record ImportPlan(List<ProfileMod> Mods, int On, int Off, int Same, int Unlisted, List<ProfileCsv.Entry> Missing, ImportExtras Extras,
        List<ProfileCsv.Entry> BothCopies, List<(Mod From, Mod To)> Switched);

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
        var ticked = new HashSet<string>(); // ids with a box changed
        foreach (var mod in current.Where(c => !Manager(c.Id)))
        {
            var copies = all.Where(m => m.manifest?.id == mod.Id && m.modSource == mod.Source).ToList();
            var local = all.Any(m => m.manifest?.id == mod.Id && m.modSource == ModSource.ModsDirectory);
            var rows = copies.ToDictionary(c => c, c => RowsOf(c, byId[mod.Id], local));
            if (Planned(mod, copies, rows, byId[mod.Id]) is not { } planned)
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
                        ticked.Add(mod.Id);
                        break;
                    default:
                        off++;
                        ticked.Add(mod.Id);
                        break;
                }
        }
        // Where no box of a mod installed more than once changes, the copy that loads still can (a list whose copy that
        // loads is another): as UnsavedChanges and Changes have it.
        var (was, will) = (Index(current), Index(mods));
        var switched = new List<(Mod, Mod)>();
        foreach (var id in Copies.TwinGroups().Select(g => g.Id).Distinct())
            if (!Manager(id) && !ticked.Contains(id) && LoadingAs(id, was) is { } from && LoadingAs(id, will) is { } to && Copies.CopyOf(from) != Copies.CopyOf(to))
                switched.Add((from, to));
        // Not installed, by id and link, so a list naming two copies of a mod not installed names both.
        var ids = current.Select(c => c.Id).ToHashSet();
        var missing = entries.Where(e => !Manager(e.Id) && !ids.Contains(e.Id)).DistinctBy(e => (e.Id, e.WorkshopLink)).ToList();
        // Each id and source not installed on if one of its rows is; where two Workshop rows name it, the one on is the copy
        // that loads once installed.
        foreach (var rows in missing.GroupBy(e => (e.Id, Source: e.WorkshopLink.Length > 0 ? ModSource.SteamWorkshop : ModSource.ModsDirectory)))
        {
            // A Workshop mod keeps its item ("w:<item>": the copy that loads once installed), so it can be subscribed to
            // from the missing-mods list later.
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
        return new ImportPlan(mods, on, off, same, unlisted, missing, PlanExtras(entries), both, switched);
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
    // whether they're on, each keeping its box as it reads with the switch on (the Copy and CopiesOff Current gave mod),
    // so a list shared from this mod list reads back as it is. Of copies from one source, the one that loads is the copy
    // the first row on links (one installed first; a Workshop item not installed yet loads once it is), else the pick now
    // if it's on, else the first on; the rest go in CopiesOff (none on: the copy and copies off as they are, so turned on
    // elsewhere the same copies come back). Null when no row names a copy of it.
    static ProfileMod? Planned(ProfileMod mod, List<Mod> copies, Dictionary<Mod, List<ProfileCsv.Entry>> rows, IEnumerable<ProfileCsv.Entry> listed)
    {
        if (copies.All(c => rows[c].Count == 0))
            return null;
        bool Alike(Mod copy) => rows[copy] is { Count: > 0 } own && own.All(e => e.WorkshopLink.Length == 0) && copies.Any(c => c != copy && rows[c].SequenceEqual(own));
        var on = copies.Where(c => rows[c].Count == 0 ? Copies.Ticked(c)
            : rows[c].Any(e => e.Enabled) && (!Alike(c) || On(mod with { Enabled = true }, Copies.CopyOf(c)))).ToList();
        if (copies.Count < 2 || on.Count == 0)
            return mod with { Enabled = on.Count > 0 };
        static string? LinkOf(Mod copy) => Compat.WorkshopId(copy) is { } item ? ProfileCsv.WorkshopLink(item) : null;
        var linked = listed.Where(e => e.Enabled && e.WorkshopLink.Length > 0).ToList();
        var notInstalled = mod.Source == ModSource.SteamWorkshop ? linked.Where(e => !copies.Any(c => LinkOf(c) == e.WorkshopLink)) : [];
        var loads = linked.Select(e => on.FirstOrDefault(c => LinkOf(c) == e.WorkshopLink)).OfType<Mod>().Select(Copies.CopyOf).FirstOrDefault()
            ?? notInstalled.Select(e => CopyOfLink(e.WorkshopLink)).FirstOrDefault()
            ?? on.Select(Copies.CopyOf).FirstOrDefault(c => c == PickOf(mod)) ?? Copies.CopyOf(on[0]);
        return mod with { Enabled = true, Copy = loads, CopiesOff = [.. copies.Except(on).Select(Copies.CopyOf).Distinct().Order(StringComparer.Ordinal)] };
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
        // As it would be after switching to it: this mod on (a switch keeps it on; the active profile's as it is now), Better
        // Mod Menu as it is now (a profile leaves it alone), every other mod as the profile has it.
        var on = profile == Active ? OnCount()
            : Current().Where(c => c.Id == ModEntry.ModId || (c.Id == Bmm.ModId ? c.Enabled : mods.GetValueOrDefault((c.Id, c.Source))?.Enabled ?? c.Enabled))
                .Select(c => ModEntry.CountKey(c.Id)).Distinct().Count();
        return Menus.Format(on == 1 ? "SPIRE_MOD_MANAGER.PROFILE_MODS_ONE" : "SPIRE_MOD_MANAGER.PROFILE_MODS", ("count", Menus.Number(on)));
    }

    // Each copy's box as the profile has it (Dependencies.Write: a local and a Workshop copy both on included), and of
    // copies from one source, the one it loads as their pick, or the copy it names where that's missing and the one it
    // loads is the copy standing in for it (KeepNamed).
    static void Apply(List<ProfileMod> mods, NModdingScreen? screen)
    {
        var wants = Index(mods);
        _applying = true;
        try
        {
            var (wanted, picks) = (new List<(Mod, bool)>(), new List<Mod>());
            foreach (var copy in AllMods())
            {
                if (copy.manifest?.id is not { } id || id == Bmm.ModId) // Better Mod Menu stays as it is
                    continue;
                // This mod is always on; a mod the profile has never seen stays as it is.
                if (id == ModEntry.ModId)
                    wanted.Add((copy, true));
                else if (wants.GetValueOrDefault((id, copy.modSource)) is { } entry)
                {
                    wanted.Add((copy, CopyOn(copy, entry)));
                    if (Copies.Twins(copy).Count > 0 && PickOf(entry) == Copies.CopyOf(copy))
                        picks.Add(copy);
                }
            }
            Dependencies.Write(wanted, picks);
            KeepNamed(wants.Values);
            if (screen != null)
                RefreshTickboxes(screen);
        }
        finally
        {
            _applying = false;
        }
    }

    // Write picks only among the copies installed: the copy each entry on names, where our record has it (one missing, or
    // one alone on its switch), is the pick after all, so once its copies are all installed again it's the one that loads,
    // as the profile has it. A missing one goes back only where the copy that would stand in for it (Copies.StandIn: the
    // one loaded, else our record's first not off) is the copy the next launch loads with no pick (Copies.NoPickChoice:
    // our record's first not off, as Write left copies_off), which Write has on. Elsewhere Write's pick stays: with the
    // missing copy as the pick, the page would say the stand-in loads while the next launch loads another, or the stand-in
    // would come on though the profile has it off. A copy named that's in copies_off comes out of it as it becomes the
    // pick (the pick is always on). Saved with Write's change: the caller's save is one undo step.
    static void KeepNamed(IEnumerable<ProfileMod> entries)
    {
        static string KeyOf(ProfileMod entry) => $"{entry.Id}|{entry.Copy}";
        var named = entries.Where(e => e.Enabled && !Manager(e.Id) && e.Copy is { } copy && Store.Data.Copies?.GetValueOrDefault(e.Id)?.Contains(copy) == true).ToList();
        if (named.Count == 0 || Store.ReadOnly)
            return;
        var implied = new Picks(Copies.WithPicks(Store.Data.CopyPicks, named.Select(KeyOf)), Store.Data.Copies, Store.Data.CopiesOff);
        var kept = named.Where(e => Copies.StandIn(e.Id, e.Source, implied) is not { } standIn || standIn.Copy == Copies.NoPickChoice(e.Id, e.Source, implied))
            .Select(KeyOf).ToList();
        if (kept.Count == 0 || Copies.WithPicks(Store.Data.CopyPicks, kept) is not { } picks)
            return;
        var off = Store.TidyOff(Store.Data.CopiesOff, picks);
        if (picks.SequenceEqual(Store.Data.CopyPicks ?? []) && (off ?? []).SequenceEqual(Store.Data.CopiesOff ?? []))
            return;
        (Store.Data.CopyPicks, Store.Data.CopiesOff) = (picks, off);
        Store.SaveViewState();
    }

    // Mod managers: this one (always on) and Better Mod Menu (left as it is). A profile never turns them on or off,
    // and they aren't counted or listed as its changes.
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
    // than once from one source, any copy's own box or the copy of the id that loads after a relaunch (LoadingAs: a pick
    // that changes nothing that loads is no difference; only copies installed count). A mod the profile has never seen
    // never differs (switching to the profile leaves it as it is); the managers don't count. now: the current list's.
    static bool Differs(ProfileMod current, Dictionary<(string, ModSource), ProfileMod> profile, Dictionary<(string, ModSource), ProfileMod> now) =>
        !Manager(current.Id) && profile.GetValueOrDefault((current.Id, current.Source)) is { } saved
        && (saved.Enabled != current.Enabled || current.Enabled && Group(current.Id, current.Source) is { } copies
            && (copies.Any(c => On(saved, c) != On(current, c)) || CopyOf(LoadingAs(current.Id, profile)) != CopyOf(LoadingAs(current.Id, now))));

    static string? CopyOf(Mod? copy) => copy == null ? null : Copies.CopyOf(copy);

    // Same on/off for every installed mod.
    static bool Same(List<ProfileMod> current, List<ProfileMod> profile)
    {
        var (saved, now) = (Index(profile), Index(current));
        return !current.Any(c => Differs(c, saved, now));
    }

    // The current list, plus the old list's entries for mods that aren't installed right now, and the copies an entry
    // had off that aren't installed now (so one that comes back is off again).
    static List<ProfileMod> Merge(List<ProfileMod> old, List<ProfileMod> current)
    {
        var installed = current.Select(c => (c.Id, c.Source)).ToHashSet();
        var was = Index(old);
        return old.Where(pm => !installed.Contains((pm.Id, pm.Source))).Concat(current.Select(now =>
            was.GetValueOrDefault((now.Id, now.Source))?.CopiesOff?.Where(c => c != now.Copy && now.CopiesOff?.Contains(c) != true
                && CopyNamed(now.Id, now.Source, c) == null).ToList() is { Count: > 0 } gone
                ? now with { CopiesOff = [.. (now.CopiesOff ?? []).Concat(gone).Distinct().Order(StringComparer.Ordinal)] } : now)).ToList();
    }

    // Ticks the screen's tickboxes to match the saved mod list (setting them doesn't fire their toggle), as the view reads
    // it: Vanilla View as the game does (one switch for every copy of an id and source), our views per copy (its own box:
    // copies of a mod from one source can be on together). Again on every view switch, so no box is stale.
    public static void RefreshTickboxes(NModdingScreen screen)
    {
        var (off, picks, vanilla) = (Dependencies.SavedOff(), Picks.Now, Store.Data.VanillaView);
        foreach (var row in screen.GetNode("%ModsScrollContainer/Mask/Content").GetChildren().OfType<NModMenuRow>())
        {
            if (row.Mod is not { } mod)
                continue;
            var on = vanilla ? !off.Contains((mod.manifest?.id, mod.modSource)) : Copies.TickedIn(mod, off, picks);
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

    // The mod list now, one entry per id and source as the game keeps it; where the id is installed more than once from
    // that source, the copy picked (Copy: the one that loads, or a pick that's missing, Copies.Chosen) and the copies off
    // beside it (CopiesOff), those kept while its switch is off too, so switching back ticks the same copies.
    static List<ProfileMod> Current()
    {
        var off = Dependencies.SavedOff();
        var groups = Copies.TwinGroups();
        return Installed()
            .Select(m => new ProfileMod(m.Id, m.Source, !off.Contains((m.Id, m.Source)), m.Id == ModEntry.ModId ? null : Copies.Chosen(m.Id, m.Source),
                CopiesOff: groups.Count == 0 || Group(m.Id, m.Source) is not { } copies ? null : OffNow(m.Id, copies)))
            .ToList();
    }
}
