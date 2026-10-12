using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;
using MegaCrit.Sts2.Core.Saves;

namespace SpireModManager;

// A profile is a snapshot of every mod's on/off state, by id + source like the game's mod list.
// Changing the mod list doesn't change the saved profile: it becomes the profile's unsaved edit, shown as
// "Unsaved" with Save, Save as new and Revert. Switching away without saving keeps the edit as a profile
// of its own, "Name (edited 14:32)", so it is never lost.
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
            // Better Mod Menu stays off in converted profiles: this mod replaces it.
            profile.Mods = Installed().Select(m => new ProfileMod(m.Id, m.Source, !disabled.Contains(m.Id) && m.Id != "BetterModMenu")).ToList();
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

    // Writes a profile's saved state to the mod list and makes it active. Leaving a profile with unsaved
    // changes keeps them as a profile of their own, "Name (edited 14:32)", next to it.
    public static void SwitchTo(Profile target, NModdingScreen screen)
    {
        var from = Active;
        Track();
        if (from != null && from != target && from.Edited is { } edit)
        {
            var list = Store.Data.Profiles;
            list.Insert(list.IndexOf(from) + 1, new Profile { Name = UniqueName(EditedName(from)), Mods = edit });
            (from.Edited, from.EditedAt) = (null, null);
        }
        Apply(target.Mods, screen);
        Store.Data.ActiveProfile = target.Name;
        Store.Save();
        Track(); // mods the profile hasn't seen are off now; that's still its saved state
        if (from != null && from != target)
            History.NoteSwitch(from.Name, target.Name); // "Switched from X to Y" in Changes
        screen.OnModEnabledOrDisabled(); // pending-changes warning
        Switched?.Invoke();
    }

    // "Modded (edited 14:32)", or with the date when it wasn't today.
    static string EditedName(Profile profile)
    {
        var at = profile.EditedAt ?? DateTime.Now;
        var when = at.Date == DateTime.Today ? at.ToString("HH:mm") : at.ToString("MMM d HH:mm");
        return Menus.Format("SPIRE_MOD_MANAGER.EDITED_PROFILE", ("name", profile.Name), ("when", when));
    }

    // The unsaved edit becomes the profile's saved state.
    public static void Save()
    {
        if (Active is not { } profile)
            return;
        profile.Mods = Merge(profile.Mods, Current());
        (profile.Edited, profile.EditedAt) = (null, null);
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

    // The current mod list as a new profile, which becomes active; the old profile goes back to its saved
    // state (its edit now lives in the new one).
    public static Profile SaveAsNew(NModdingScreen screen)
    {
        var from = Active;
        var profile = Add();
        if (from != null)
            (from.Edited, from.EditedAt) = (null, null);
        Store.Data.ActiveProfile = profile.Name;
        Store.Save();
        if (from != null)
            History.NoteSwitch(from.Name, profile.Name);
        screen.OnModEnabledOrDisabled();
        Switched?.Invoke();
        return profile;
    }

    static void Apply(List<ProfileMod> mods, NModdingScreen screen)
    {
        var settings = SaveManager.Instance.SettingsSave.ModSettings ??= new ModSettings();
        _applying = true;
        try
        {
            foreach (var mod in ModManager.Mods)
            {
                if (mod.manifest?.id is not { } id)
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

    static bool EnabledIn(List<ProfileMod> mods, string id, ModSource source) =>
        mods.FirstOrDefault(pm => pm.Id == id && pm.Source == source)?.Enabled ?? false;

    // Same on/off for every installed mod (a mod a profile hasn't seen counts as off; this mod is always on).
    static bool Same(List<ProfileMod> current, List<ProfileMod> profile) =>
        current.All(c => c.Id == ModEntry.ModId || c.Enabled == EnabledIn(profile, c.Id, c.Source));

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

    // A new profile starting from the current mod list.
    public static Profile Add(string? name = null)
    {
        var profile = new Profile { Name = name ?? UniqueName(), Mods = Current() };
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

    // The name itself, or "name 2", "name 3"... when it's taken.
    static string UniqueName(string name)
    {
        var candidate = name;
        for (var n = 2; Store.Data.Profiles.Any(p => p.Name == candidate); n++)
            candidate = $"{name} {n}";
        return candidate;
    }

    static string UniqueName()
    {
        for (var n = Store.Data.Profiles.Count + 1; ; n++)
        {
            var loc = new LocString("settings_ui", "SPIRE_MOD_MANAGER.NEW_PROFILE_NAME");
            loc.Add("n", n);
            var name = loc.GetFormattedText();
            if (Store.Data.Profiles.All(p => p.Name != name))
                return name;
        }
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
