using Godot;

namespace SpireModManager.Api;

/// <summary>SMM's colours, for every mod. A value may be tuned later; what each one is for never changes.</summary>
public static class Palette
{
    /// <summary>Text: cream (1, 0.965, 0.886).</summary>
    public static Color Cream => Look.Cream;

    /// <summary>Headings, chosen things, borders (0.937, 0.784, 0.318).</summary>
    public static Color Gold => Look.Gold;

    /// <summary>Menus, small buttons, text outlines: navy (0.076, 0.121, 0.18).</summary>
    public static Color Navy => Look.Navy;

    /// <summary>Dialogs and hover cards: a deeper navy (0.05, 0.08, 0.12).</summary>
    public static Color DeepNavy => Look.DeepNavy;

    /// <summary>A warning banner (0.6, 0.44, 0.06, 0.9).</summary>
    public static Color Warning => Look.Warning;

    /// <summary>A problem banner (0.45, 0.1, 0.1, 0.6).</summary>
    public static Color Problem => Look.Problem;
}
