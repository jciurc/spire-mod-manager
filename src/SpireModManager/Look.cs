using Godot;

namespace SpireModManager;

// SMM's colours in one place: the API's Palette reads them, and so do the files as they're touched (no sweeping change).
static class Look
{
    public static readonly Color Cream = new(1, 0.964706f, 0.886275f);
    public static readonly Color Gold = new(0.937f, 0.784f, 0.318f);
    public static readonly Color Navy = new(0.0756f, 0.12084f, 0.18f);
    public static readonly Color DeepNavy = new(0.05f, 0.08f, 0.12f);
    public static readonly Color Warning = new(0.6f, 0.44f, 0.06f, 0.9f);
    public static readonly Color Problem = new(0.45f, 0.1f, 0.1f, 0.6f);
}
