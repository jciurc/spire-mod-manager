using Godot;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;
using MegaCrit.Sts2.addons.mega_text;

namespace SpireModManager;

// Our views use more of the screen than the vanilla Modding screen: a wider mod list and info panel and
// smaller text, which also makes it obvious which page you're on. Vanilla View puts the game's own
// positions and sizes back (saved the first time we change them).
static class Layout
{
    const string OriginalMeta = "spire_mod_manager_original";

    // Screen-space boxes (left, top, right, bottom) in the game's 1920x1080 layout.
    static readonly Vector4 ListBox = new(190, 128, 1110, 990);
    static readonly Vector4 InfoBox = new(1140, 128, 1880, 990);
    static readonly Vector4 PendingBox = new(190, 1000, 1880, 1068);
    public const float BottomRowY = 999; // top of the 70px-tall buttons along the bottom

    // Text sizes in our views: (vanilla, ours).
    public const int RowTitleSize = 24, RowTitleVanillaSize = 32;
    const int InfoTitleSize = 40, InfoTextSize = 20;

    public static bool Wide => !Store.Data.VanillaView;

    public static void Apply(NModdingScreen screen)
    {
        var list = screen.GetNode<Control>("ModsBorder");
        var info = screen.GetNode<Control>("%ModInfoContainer");
        Place(list, ListBox);
        Place(info, InfoBox);
        Place(screen.GetNode<Control>("%PendingChangesLabel"), PendingBox);

        // Inside the info panel: title, image and description, fitted to its new size.
        var width = info.Size.X;
        Place(info.GetNode<Control>("ModTitle"), new Vector4(20, 12, width - 20, 72));
        Place(info.GetNode<Control>("ModImage"), new Vector4(20, 80, width - 20, 400));
        Place(info.GetNode<Control>("ModDescription"), new Vector4(24, 410, width - 24, info.Size.Y - 92));
        FontSize(info.GetNode<MegaRichTextLabel>("ModTitle"), Wide ? InfoTitleSize : 60);
        FontSize(info.GetNode<MegaRichTextLabel>("ModDescription"), Wide ? InfoTextSize : 24);

        // Profile bar and search sit at the top of the list in our views; Vanilla View hides them and gives
        // the list its full height back.
        var scroll = list.GetNode<Control>("ModsScrollContainer");
        if (!scroll.HasMeta(OriginalMeta))
            scroll.SetMeta(OriginalMeta, scroll.OffsetTop);
        var bar = list.GetNodeOrNull<Control>("SpireModManagerProfileBar");
        var search = list.GetNodeOrNull<Control>("SpireModManagerSearch");
        foreach (var top in new[] { bar, search })
            if (top != null)
                top.Visible = Wide;
        scroll.OffsetTop = Wide && (search ?? bar) is { } lowest ? lowest.OffsetBottom + 8 : (float)scroll.GetMeta(OriginalMeta);

        // Our own buttons along the bottom follow the boxes.
        var left = list.OffsetLeft;
        Move(screen, "SpireModManagerViewToggle", left);
        Move(screen, "SpireModManagerLoadOrderToggle", left + 210);
        Move(screen, "SpireModManagerBmmNotice", left);
        if (screen.GetNodeOrNull<Control>("SpireModManagerRelaunchButton") is { } relaunch)
            relaunch.Position = new Vector2(info.OffsetRight - relaunch.Size.X, BottomRowY);
    }

    // Wide: the given box. Vanilla: the box the scene had.
    static void Place(Control control, Vector4 wide)
    {
        if (!control.HasMeta(OriginalMeta))
            control.SetMeta(OriginalMeta, new Vector4(control.OffsetLeft, control.OffsetTop, control.OffsetRight, control.OffsetBottom));
        var box = Wide ? wide : (Vector4)control.GetMeta(OriginalMeta);
        (control.OffsetLeft, control.OffsetTop, control.OffsetRight, control.OffsetBottom) = (box.X, box.Y, box.Z, box.W);
    }

    static void Move(Node screen, string name, float x)
    {
        if (screen.GetNodeOrNull<Control>(name) is { } control)
            control.Position = new Vector2(x, control is Label ? BottomRowY + 1 : BottomRowY);
    }

    public static void FontSize(MegaRichTextLabel label, int size)
    {
        label.AddThemeFontSizeOverride("normal_font_size", size);
        label.AddThemeFontSizeOverride("bold_font_size", size);
        label.MaxFontSize = size;
        label.SetTextAutoSize(label.Text);
    }
}
