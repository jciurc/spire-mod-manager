using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;

namespace SpireModManager;

// Game members that only one branch has, reached by name so one build loads on both: the beta (v0.111) has
// Mod.workshopId and NModInfoContainer.Clear; stable (v0.107) has neither.
static class Compat
{
    static readonly FieldInfo? WorkshopIdField = AccessTools.Field(typeof(Mod), "workshopId");
    public static readonly MethodInfo? InfoClear = AccessTools.Method(typeof(NModInfoContainer), "Clear");
    static readonly FieldInfo?[] InfoParts = new[] { "_title", "_description", "_image" }.Select(name => AccessTools.Field(typeof(NModInfoContainer), name)).ToArray();

    // Stable: each mod's Workshop id from its folder, worked out once.
    static readonly ConditionalWeakTable<Mod, StrongBox<ulong?>> FromFolder = new();

    // A Workshop mod's item id: the game's field (beta), or its folder's name in workshop\content\<app id>\ (stable).
    public static ulong? WorkshopId(Mod? mod)
    {
        if (mod == null)
            return null;
        if (WorkshopIdField != null)
            return WorkshopIdField.GetValue(mod) as ulong?;
        return FromFolder.GetValue(mod, m => new StrongBox<ulong?>(IdFromFolder(m))).Value;
    }

    static ulong? IdFromFolder(Mod mod)
    {
        if (mod.modSource != ModSource.SteamWorkshop || string.IsNullOrEmpty(mod.path))
            return null;
        var parts = mod.path.Split('\\', '/');
        for (var i = 0; i + 2 < parts.Length; i++)
            if (parts[i].Equals("content", StringComparison.OrdinalIgnoreCase) && ulong.TryParse(parts[i + 1], out _)
                && ulong.TryParse(parts[i + 2], out var id))
                return id;
        return null;
    }

#if DEVTOOLS
    // Self-test: gives a made-up mod a Workshop id.
    public static void SetWorkshopId(Mod mod, ulong id)
    {
        if (WorkshopIdField != null)
            WorkshopIdField.SetValue(mod, (ulong?)id);
        else
            mod.path = $@"C:\steamapps\workshop\content\2868840\{id}";
    }
#endif

    // Empties the info panel: the game's Clear (beta), or its title, text and picture cleared (stable).
    public static void Clear(NModInfoContainer info)
    {
        if (InfoClear != null)
        {
            InfoClear.Invoke(info, null);
            return;
        }
        foreach (var part in InfoParts)
            switch (part?.GetValue(info))
            {
                case RichTextLabel text:
                    text.Text = "";
                    break;
                case Label label:
                    label.Text = "";
                    break;
                case TextureRect picture:
                    picture.Texture = null;
                    break;
            }
        ModDetails.HideButtons(info); // what our patch on Clear does on the beta
    }
}
