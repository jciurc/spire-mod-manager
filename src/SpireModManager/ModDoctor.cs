using System.Diagnostics;
using System.Text.Json.Nodes;
using Godot;

namespace SpireModManager;

// Mod Doctor (a separate troubleshooting mod) writes what it found about a failed start to mod_doctor_startup.json in the
// game's user folder: at the game's startup error, then again a frame or so later with the failure and the mods it
// suspects. Read only, and only this process's (its id and start time match); without Mod Doctor there's no file.
static class ModDoctor
{
    public const string ModId = "ModDoctor";
    const string FileName = "mod_doctor_startup.json";
    const int FramesToWait = 30; // about half a second: its suspects come a frame or so after the error

    // The mod ids it suspects, once it has written them; none if it isn't there, or hasn't named any in time.
    public static async Task<List<string>> Suspects(Node node)
    {
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
        var file = Path.Combine(OS.GetUserDataDir(), FileName);
        if (!File.Exists(file) || JsonNode.Parse(File.ReadAllText(file)) is not JsonObject json || (int?)json["version"] != 1)
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
