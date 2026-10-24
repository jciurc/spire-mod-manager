using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Godot;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Saves;

namespace SpireModManager;

// Mod Doctor (a separate troubleshooting mod) writes what it found about a failed start to mod_data/ModDoctor/startup.json
// in the game's user folder (mod_doctor_startup.json at the folder's top before its 2026-10 move): at the game's startup error, then again a frame or so later with the failure, the mods it
// suspects and the one it would turn off. Read only, and only this process's (its id and start time match); without Mod
// Doctor there's no file. Its record of the mods it turned off is read too.
static class ModDoctor
{
    public const string ModId = "ModDoctor";
    // Where it writes, newest first: the old place is read only when the new one has nothing, for a Mod Doctor from
    // before the move (a stale file is skipped anyway: it's another process's). The old one can go once both are out.
    static readonly string[] Files = [Path.Combine("mod_data", ModId, "startup.json"), "mod_doctor_startup.json"];
    const int FramesToWait = 30; // about half a second: its suspects come a frame or so after the error
    const int ExplanationMost = 600;

    // Why it picked a mod, by its own names (StartupFix.Reason): the mod's code threw, or it's updated or added since the
    // last main menu
    public enum Reason { Threw, Updated, Added }

    // The mod it would turn off. Name: only for the log (we name mods by their manifest).
    public record Pick(string Id, string? Name, Reason Reason);

    // What it found: the mods it suspects, its pick, and why in the player's words (plain text, cut at a word past 600
    // characters; the styled dialog escapes its "[")
    public record Findings(IReadOnlyList<string> Suspects, Pick? Culprit, string? Explanation)
    {
        public static readonly Findings None = new([], null, null);
    }

#if DEVTOOLS
    // Self-test: the suspects Mod Doctor would name, without it; or a folder to read its file from instead of the game's
    // user folder (the real game's too); or Mod Doctor counted as loaded (true) or not (false) whatever the sandbox has
    // (null: as it is)
    public static List<string>? TestSuspects;
    public static string? TestFolder;
    public static bool? TestLoaded;
#endif

    // What it found, once it has written it. With no file for this process the wait ends at once unless a Mod Doctor copy
    // is loaded (its file can come later: none at its prefix when its report class didn't apply, though its write from
    // the log still comes). A file that doesn't parse is read again until the frames run out (it writes in place, not
    // atomically). None if it isn't there, or hasn't found anything in time.
    public static async Task<Findings> WaitForFindings(Node node)
    {
#if DEVTOOLS
        if (TestSuspects is { } made)
            return new Findings([.. made], null, null);
#endif
        try
        {
            var loaded = Loaded();
            for (var i = 0; i < FramesToWait; i++)
            {
                switch (Read())
                {
                    case null when !loaded:
                        return Findings.None; // no file for this process: Mod Doctor isn't there
                    case { Ready: true } found:
                        return found.Findings;
                }
                await node.ToSignal(node.GetTree(), SceneTree.SignalName.ProcessFrame);
            }
            MegaCrit.Sts2.Core.Logging.Log.Info($"[{ModEntry.ModId}] Mod Doctor wrote no findings in time, so the dialog goes without them");
        }
        catch (Exception e)
        {
            MegaCrit.Sts2.Core.Logging.Log.Warn($"[{ModEntry.ModId}] Could not read Mod Doctor's findings: {e.Message}");
        }
        return Findings.None;
    }

    static bool Loaded()
    {
#if DEVTOOLS
        if (TestLoaded is { } loaded)
            return loaded;
#endif
        return ModManager.Mods.Any(m => m.manifest?.id == ModId && m.state == ModLoadState.Loaded);
    }

    // Whether Mod Doctor turned a mod off at or after a time: from a failed start's dialog it turns off the mod that broke
    // it and restarts, and its note at the next main menu says so, with a way to turn it back on. False without it, or for
    // one this doesn't understand.
    public static bool TurnedOffSince(DateTime since)
    {
        try
        {
            var from = new DateTimeOffset(since);
            return TurnedOff().Any(m => DateTimeOffset.TryParse(Text(m["at"]), CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) && at >= from);
        }
        catch (Exception e)
        {
            MegaCrit.Sts2.Core.Logging.Log.Warn($"[{ModEntry.ModId}] Could not read Mod Doctor's record of the mods it turned off: {e.Message}");
            return false;
        }
    }

    // The mods it turned off that are still its to keep off, by id, for Restore and profiles: by its own rule
    // (StartupFix.Notes), a mod installed with no copy on in the game's list (list: as read; null, as it is now), and only
    // while Mod Doctor is loaded, when that rule runs and its note shows (its record stays behind it otherwise, with
    // nothing to say why a mod stays off). None without its record, or for one this doesn't understand.
    public static List<string> TurnedOffIds(IReadOnlyList<(string Id, ModSource Source, bool On)>? list)
    {
        try
        {
            if (!Loaded())
                return [];
            list ??= SaveManager.Instance.SettingsSave.ModSettings?.ModList.Select(e => (e.Id, e.Source, e.IsEnabled)).ToList();
            var copies = ModManager.Mods.Union(Installed.Mods).Select(m => (Id: m.manifest?.id, m.modSource)).Where(c => c.Id is { Length: > 0 }).ToList();
            return [.. TurnedOff().Select(m => Text(m["id"])).OfType<string>().Where(id => id.Length > 0).Distinct()
                .Where(id => copies.Any(c => c.Id == id) && !copies.Any(c => c.Id == id && Culprits.OnAsRead(id, c.modSource, list)))];
        }
        catch (Exception e)
        {
            MegaCrit.Sts2.Core.Logging.Log.Warn($"[{ModEntry.ModId}] Could not read Mod Doctor's record of the mods it turned off, so Restore may turn them on: {e.Message}");
            return [];
        }
    }

    // Its record's entries: the account's turned-off.json (mod_data/ModDoctor/<platform>/<account id>/, as the game names
    // the account's folder: {"version": 1, "mods": [{"id", "at", ...}]}, "at" an ISO 8601 time with its offset). None
    // without it, or of another version.
    static IEnumerable<JsonObject> TurnedOff()
    {
        var top = OS.GetUserDataDir();
#if DEVTOOLS
        top = TestFolder ?? top;
#endif
        var account = UserDataPathProvider.GetAccountScopedBasePath(null).Replace("user://", "");
        var file = Path.Combine(top, "mod_data", ModId, account, "turned-off.json");
        if (!File.Exists(file) || JsonNode.Parse(File.ReadAllText(file)) is not JsonObject json || (int?)json["version"] != 1 || json["mods"] is not JsonArray mods)
            return [];
        return mods.OfType<JsonObject>();
    }

    // A string value, or null for anything else (a cast on a number throws)
    static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    // This process's file: Ready once the failure is in it ("fatal" set), with what it found. Not ready while it has no
    // failure, or doesn't parse yet. Null for none, another process's, or one of another version.
    static (bool Ready, Findings Findings)? Read()
    {
        var top = OS.GetUserDataDir();
#if DEVTOOLS
        top = TestFolder ?? top;
#endif
        var file = Files.Select(name => Path.Combine(top, name)).FirstOrDefault(File.Exists);
        if (file == null)
            return null;
        JsonObject? json;
        try
        {
            json = JsonNode.Parse(File.ReadAllText(file)) as JsonObject;
        }
        catch (JsonException)
        {
            return (false, Findings.None); // half a file: read again next frame
        }
        if (json == null || (int?)json["version"] != 1)
            return null;
        using var self = Process.GetCurrentProcess();
        if ((int?)json["processId"] != System.Environment.ProcessId
            || !DateTime.TryParse((string?)json["processStartUtc"], null, DateTimeStyles.RoundtripKind, out var started)
            || Math.Abs((started.ToUniversalTime() - self.StartTime.ToUniversalTime()).TotalSeconds) > 2)
            return null;
        if (json["fatal"] is not JsonObject fatal)
            return (false, Findings.None);
        var suspects = fatal["suspects"] is JsonArray ids ? ids.Select(Text).OfType<string>().ToList() : [];
        return (true, new Findings(suspects, CulpritIn(fatal["culprit"]), Explained(Text(fatal["explanation"]))));
    }

    // Its pick, field by field: an object whose "id" is a string. A reason it doesn't give, or one this doesn't know, reads
    // as Updated: its builds from before it wrote one, ranked as a guess.
    static Pick? CulpritIn(JsonNode? node)
    {
        if (node is not JsonObject culprit || Text(culprit["id"]) is not { Length: > 0 } id)
            return null;
        var reason = Text(culprit["reason"]) switch
        {
            nameof(Reason.Threw) => Reason.Threw,
            nameof(Reason.Added) => Reason.Added,
            _ => Reason.Updated,
        };
        return new Pick(id, Text(culprit["name"]), reason);
    }

    // Its explanation trimmed, line breaks kept; past 600 characters, cut at a word
    static string? Explained(string? text)
    {
        if (text?.Trim() is not { Length: > 0 } trimmed)
            return null;
        if (trimmed.Length <= ExplanationMost)
            return trimmed;
        var start = Menus.Prefix(trimmed, ExplanationMost);
        var word = start.LastIndexOfAny([' ', '\n', '\t']);
        return Menus.CutShort(word > 0 ? start[..word] : start);
    }
}
