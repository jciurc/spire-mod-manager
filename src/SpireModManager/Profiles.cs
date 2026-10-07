using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;
using MegaCrit.Sts2.Core.Saves;

namespace SpireModManager;

// A profile is a snapshot of every mod's on/off state, by id + source like the game's mod list.
// The active profile follows the mod list (every tickbox change is copied into it), so switching never
// loses anything: the old profile is brought up to date, then the new one is written to the mod list.
// Mods a profile has never seen are off in it. Spire Mod Manager itself is always on.
static class Profiles
{
    static bool _applying;

    // Raised after a switch, so the profile bar shows the new name whoever switched.
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

    // Copies the current mod list into the active profile. Mods not installed right now keep their entry.
    public static void Track()
    {
        if (_applying || Active is not { } profile)
            return;
        var current = Current();
        var updated = profile.Mods.Where(pm => !current.Any(c => c.Id == pm.Id && c.Source == pm.Source)).Concat(current).ToList();
        if (updated.Count == profile.Mods.Count && updated.All(profile.Mods.Contains))
            return;
        profile.Mods = updated;
        Store.Save();
    }

    public static void SwitchTo(Profile target, NModdingScreen screen)
    {
        var from = Store.Data.ActiveProfile;
        Track();
        var settings = SaveManager.Instance.SettingsSave.ModSettings ??= new ModSettings();
        _applying = true;
        try
        {
            foreach (var mod in ModManager.Mods)
            {
                if (mod.manifest?.id is not { } id)
                    continue;
                var enabled = id == ModEntry.ModId ||
                    (target.Mods.FirstOrDefault(pm => pm.Id == id && pm.Source == mod.modSource)?.Enabled ?? false);
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
        Store.Data.ActiveProfile = target.Name;
        Track(); // records mods the profile hadn't seen (now off)
        if (from != null && from != target.Name)
            History.NoteSwitch(from, target.Name); // "Switched from X to Y" in Changes
        screen.OnModEnabledOrDisabled(); // pending-changes warning
        Switched?.Invoke();
    }

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
