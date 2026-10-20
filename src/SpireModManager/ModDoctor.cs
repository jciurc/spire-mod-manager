using System.Diagnostics;
using System.Text.Json.Nodes;
using Godot;

namespace SpireModManager;

// Mod Doctor (a separate troubleshooting mod) writes what it found about a failed start to mod_data/ModDoctor/startup.json
// in the game's user folder (mod_doctor_startup.json at the folder's top before its 2026-10 move): at the game's startup error, then again a frame or so later with the failure and the mods it
// suspects. Read only, and only this process's (its id and start time match); without Mod Doctor there's no file.
static class ModDoctor
{
    public const string ModId = "ModDoctor";
    // Where it writes, newest first: the old place is read only when the new one has nothing, for a Mod Doctor from
    // before the move (a stale file is skipped anyway: it's another process's). The old one can go once both are out.
    static readonly string[] Files = [Path.Combine("mod_data", ModId, "startup.json"), "mod_doctor_startup.json"];
    const int FramesToWait = 30; // about half a second: its suspects come a frame or so after the error

#if DEVTOOLS
    // Self-test: the suspects Mod Doctor would name, without it; or a folder to read its file from instead of the game's
    // user folder (the real game's too).
    public static List<string>? TestSuspects;
    public static string? TestFolder;
#endif

    // The mod ids it suspects, once it has written them; none if it isn't there, or hasn't named any in time.
    public static async Task<List<string>> Suspects(Node node)
    {
#if DEVTOOLS
        if (TestSuspects is { } made)
            return [.. made];
#endif
        try
        {
            for (var i = 0; i < FramesToWait; i++)
            {
                switch (Read())
                {
                    case null:
                        return []; // no file for this process: Mod Doctor isn't there
                    case { Ready: true } found:
                        return found.Suspects;
                }
                await node.ToSignal(node.GetTree(), SceneTree.SignalName.ProcessFrame);
            }
        }
        catch (Exception e)
        {
            MegaCrit.Sts2.Core.Logging.Log.Warn($"[{ModEntry.ModId}] Could not read Mod Doctor's findings: {e.Message}");
        }
        return [];
    }

    // This process's file: Ready once the failure is in it ("fatal" set), with its suspects. Null for none, another
    // process's, or one this doesn't understand.
    static (bool Ready, List<string> Suspects)? Read()
    {
        var top = OS.GetUserDataDir();
#if DEVTOOLS
        top = TestFolder ?? top;
#endif
        var file = Files.Select(name => Path.Combine(top, name)).FirstOrDefault(File.Exists);
        if (file == null || JsonNode.Parse(File.ReadAllText(file)) is not JsonObject json || (int?)json["version"] != 1)
            return null;
        using var self = Process.GetCurrentProcess();
        if ((int?)json["processId"] != System.Environment.ProcessId
            || !DateTime.TryParse((string?)json["processStartUtc"], null, System.Globalization.DateTimeStyles.RoundtripKind, out var started)
            || Math.Abs((started.ToUniversalTime() - self.StartTime.ToUniversalTime()).TotalSeconds) > 2)
            return null;
        if (json["fatal"] is not JsonObject fatal)
            return (false, []);
        return (true, fatal["suspects"] is JsonArray ids ? [.. ids.Select(id => (string?)id).OfType<string>()] : []);
    }
}
