using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;
using MegaCrit.Sts2.Core.Saves;

namespace SpireModManager;

// A profile is a snapshot of every mod's on/off state, by id + source like the game's mod list.
// Changing the mod list doesn't change the saved profile: it becomes the profile's unsaved edit, shown as
// "Unsaved" with Save, Save as new and Discard. Switching away keeps the edit with its profile ("Name *"
// in the list); coming back restores it, so it is never lost.
// Mods a profile has never seen are off in it. Spire Mod Manager itself is always on.
static class Profiles
{
    static bool _applying;

    // Raised after a switch, save or discard, or when the list becomes (or stops being) an unsaved edit, so
    // the profile bar shows the right name and buttons whoever made the change.
    public static event Action? Switched;

    public static Profile? Active => Store.Data.Profiles.FirstOrDefault(p => p.Name == Store.Data.ActiveProfile);

    // First use: convert id-only profiles (Better Mod Menu's), and make sure one profile is active.
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
        if (changed)
            Store.Save();
        Track();
    }

    // Whether the mod list is the active profile's unsaved edit.
    public static bool Edited => Active?.Edited != null;

    // After any change to the mod list: when it differs from the active profile's saved state it is that
    // profile's unsaved edit (timestamped); back on the saved state, the profile is clean again. Nothing is
    // overwritten silently: saving is the player's choice.
    public static void Track()
    {
        if (_applying || Active is not { } profile)
            return;
        var current = Current();
        if (Same(current, profile.Mods))
        {
            if (profile.Edited == null)
                return;
            (profile.Edited, profile.EditedAt) = (null, null);
        }
        else
        {
            if (profile.Edited is { } edit && Same(current, edit))
                return;
            profile.Edited = Merge(profile.Edited ?? profile.Mods, current);
            profile.EditedAt = DateTime.Now;
        }
        Store.Save();
        Switched?.Invoke(); // the bar shows "Unsaved" or not
    }

    // Writes a profile to the mod list and makes it active: its unsaved edit if it has one (so leaving a
    // profile and coming back finds it as it was), its saved state otherwise.
    public static void SwitchTo(Profile target, NModdingScreen screen)
    {
        Track(); // the profile being left keeps its edit
        Apply(target.Edited ?? target.Mods, screen);
        Store.Data.ActiveProfile = target.Name;
        Store.Save();
        Track(); // mods the profile hasn't seen are off now; that's still its saved state
        screen.OnModEnabledOrDisabled(); // pending-changes warning
        Switched?.Invoke();
    }

    // The active profile's unsaved changes: mods turned on and off compared with its saved state (names).
    public static (List<string> On, List<string> Off) UnsavedChanges()
    {
        if (Active is not { Edited: not null } profile)
            return ([], []);
        var changed = Current().Where(c => !Manager(c.Id) && c.Enabled != EnabledIn(profile.Mods, c.Id, c.Source)).ToList();
        string Name(ProfileMod m) => Dependencies.Find(m.Id)?.manifest?.name ?? m.Id;
        return (changed.Where(c => c.Enabled).Select(Name).ToList(), changed.Where(c => !c.Enabled).Select(Name).ToList());
    }

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
        Apply(profile.Mods, screen);
        (profile.Edited, profile.EditedAt) = (null, null);
        Store.Save();
        screen.OnModEnabledOrDisabled();
        Switched?.Invoke();
    }

    public enum NewKind { Blank, Duplicate, SaveAsNew }

    // A new profile, made active:
    // - Blank: nothing on but this mod;
    // - Duplicate: a copy of the active profile's saved state (the active one keeps any unsaved edit);
    // - SaveAsNew: the mod list as it is now (the active profile's edit moves into it).
    public static Profile Create(NewKind kind, string name, NModdingScreen screen)
    {
        var from = Active;
        var mods = kind switch
        {
            NewKind.Blank => Installed().Select(m => new ProfileMod(m.Id, m.Source, m.Id == ModEntry.ModId)).ToList(),
            NewKind.Duplicate when from != null => from.Mods.ToList(),
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
    public static string CopyName(string name)
    {
        var bare = System.Text.RegularExpressions.Regex.Replace(name, @" \(\d+\)$", "");
        for (var n = 1; ; n++)
            if (Store.Data.Profiles.All(p => p.Name != $"{bare} ({n})"))
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

    // Beside the picker: "12 mods", how many are on in the mod list right now (this mod not counted).
    public static string CountText()
    {
        var on = Current().Count(m => m.Enabled && !Manager(m.Id));
        return Menus.Format(on == 1 ? "SPIRE_MOD_MANAGER.PROFILE_MODS_ONE" : "SPIRE_MOD_MANAGER.PROFILE_MODS", ("count", on.ToString()));
    }

    // For the profile list: "12 mods · Oct 11" (mods on, when last saved).
    public static string Details(Profile profile)
    {
        var on = (profile.Edited ?? profile.Mods).Count(m => m.Enabled && !Manager(m.Id));
        var text = Menus.Format(on == 1 ? "SPIRE_MOD_MANAGER.PROFILE_MODS_ONE" : "SPIRE_MOD_MANAGER.PROFILE_MODS", ("count", on.ToString()));
        return profile.SavedAt is { } saved ? $"{text} · {saved:MMM d}" : text;
    }

    static void Apply(List<ProfileMod> mods, NModdingScreen screen)
    {
        var settings = SaveManager.Instance.SettingsSave.ModSettings ??= new ModSettings();
        _applying = true;
        try
        {
            foreach (var mod in ModManager.Mods)
            {
                if (mod.manifest?.id is not { } id || id == Bmm.ModId) // Better Mod Menu stays as it is
                    continue;
                var enabled = id == ModEntry.ModId || EnabledIn(mods, id, mod.modSource);
                var entry = settings.ModList.FirstOrDefault(e => e.Id == id && e.Source == mod.modSource);
                if (entry == null)
                    settings.ModList.Add(entry = new SettingsSaveMod(mod));
                entry.IsEnabled = enabled;
            }
            SaveManager.Instance.SaveSettings();
            RefreshTickboxes(screen);
        }
        finally
        {
            _applying = false;
        }
    }

    // Mod managers: this one (always on) and Better Mod Menu (left as it is). A profile never turns them on or off,
    // and they aren't counted or listed as its changes.
    static bool Manager(string id) => id is ModEntry.ModId or Bmm.ModId;

    static bool EnabledIn(List<ProfileMod> mods, string id, ModSource source) =>
        mods.FirstOrDefault(pm => pm.Id == id && pm.Source == source)?.Enabled ?? false;

    // Same on/off for every installed mod (a mod a profile hasn't seen counts as off; the managers don't count).
    static bool Same(List<ProfileMod> current, List<ProfileMod> profile) =>
        current.All(c => Manager(c.Id) || c.Enabled == EnabledIn(profile, c.Id, c.Source));

    // The current list, plus the old list's entries for mods that aren't installed right now.
    static List<ProfileMod> Merge(List<ProfileMod> old, List<ProfileMod> current) =>
        old.Where(pm => !current.Any(c => c.Id == pm.Id && c.Source == pm.Source)).Concat(current).ToList();

    // Ticks the screen's tickboxes to match the saved mod list (setting them doesn't fire their toggle).
    public static void RefreshTickboxes(NModdingScreen screen)
    {
        var settings = SaveManager.Instance.SettingsSave.ModSettings;
        foreach (var row in screen.GetNode("%ModsScrollContainer/Mask/Content").GetChildren().OfType<NModMenuRow>())
            if (row.Mod is { } mod)
                row.GetNode<NTickbox>("Tickbox").IsTicked = !(settings?.IsModDisabled(mod) ?? false);
    }

    public static void NotifySwitched() => Switched?.Invoke();

    // A new profile starting from the current mod list (the first one, on first use).
    public static Profile Add(string? name = null)
    {
        var profile = new Profile { Name = name ?? NewName(), Mods = Current(), SavedAt = DateTime.Now };
        Store.Data.Profiles.Add(profile);
        Store.Save();
        return profile;
    }

    // Never the last profile. Deleting the active one first switches to its neighbour.
    public static bool Delete(Profile profile, NModdingScreen screen)
    {
        var list = Store.Data.Profiles;
        if (list.Count <= 1 || !list.Contains(profile))
            return false;
        if (profile == Active)
        {
            var i = list.IndexOf(profile);
            SwitchTo(list[i == list.Count - 1 ? i - 1 : i + 1], screen);
        }
        list.Remove(profile);
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

    static IEnumerable<(string Id, ModSource Source)> Installed() =>
        ModManager.Mods.Where(m => m.manifest?.id != null).Select(m => (m.manifest?.id ?? "", m.modSource));

    static List<ProfileMod> Current()
    {
        var settings = SaveManager.Instance.SettingsSave.ModSettings;
        return ModManager.Mods.Where(m => m.manifest?.id != null)
            .Select(m => new ProfileMod(m.manifest?.id ?? "", m.modSource, !(settings?.IsModDisabled(m) ?? false)))
            .ToList();
    }
}
