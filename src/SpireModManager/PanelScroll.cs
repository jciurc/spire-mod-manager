using Godot;
using MegaCrit.Sts2.addons.mega_text;

namespace SpireModManager;

// The info panel's lower part in our views: everything under the head (the description, other mods' parts, the
// player's fields) scrolls in one box (Jordan, 28 Oct: all of it under the Compatibility line, together). The
// game's description moves into it, its own scrolling off; Vanilla View puts it back where the scene had it. The
// game finds it by its own reference, so the move doesn't reach the game's code.
static class PanelScroll
{
    public const string BoxName = "SpireModManagerScroll";
    const string HomeMeta = "spire_mod_manager_scroll_home"; // the description's place in the scene, for Vanilla View

    // The game's description, wherever it is now.
    public static MegaRichTextLabel Description(Node info) =>
        info.GetNodeOrNull<MegaRichTextLabel>("ModDescription") ?? info.GetNode<MegaRichTextLabel>($"{BoxName}/Body/ModDescription");

    public static ScrollContainer? Box(Node info) => info.GetNodeOrNull<ScrollContainer>(BoxName);

    // What scrolls: the description, the line for other mods' parts left out, and our fields, placed by ModDetails.PlaceFields.
    public static Control? Body(Node info) => info.GetNodeOrNull<Control>($"{BoxName}/Body");

    // The width the content has: the box's, less a scrollbar's room whether one shows or not, so text doesn't wrap again
    // when one comes.
    public static float Width(ScrollContainer box) => box.Size.X - box.GetVScrollBar().GetCombinedMinimumSize().X - 4;

    // Our views: the box shown, the description in it. Returns the box, for the caller to place.
    public static ScrollContainer Enter(Control info)
    {
        var box = Box(info) ?? Add(info);
        box.Visible = true;
        if (info.GetNodeOrNull<MegaRichTextLabel>("ModDescription") is { } description)
        {
            if (!description.HasMeta(HomeMeta))
                description.SetMeta(HomeMeta, new Godot.Collections.Array
                {
                    description.GetIndex(), description.ScrollActive, description.AnchorLeft, description.AnchorTop,
                    description.AnchorRight, description.AnchorBottom, description.OffsetLeft, description.OffsetTop,
                    description.OffsetRight, description.OffsetBottom,
                });
            description.Reparent(box.GetNode("Body"), false);
            description.ScrollActive = false; // the box scrolls it
            Place(box, description, 0, box.Size.Y);
        }
        return box;
    }

    // At top in the box's content, height tall, the content's width. Its anchors too: Better Mod Menu sets the
    // description's on a fill.
    public static void Place(ScrollContainer box, Control part, float top, float height)
    {
        part.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
        (part.OffsetLeft, part.OffsetTop, part.OffsetRight, part.OffsetBottom) = (0, top, Width(box), top + height);
    }

    // Vanilla View: the description back where the scene had it, as it had it; the box hidden.
    public static void Leave(Control info)
    {
        if (Box(info) is not { } box)
            return;
        box.Visible = false;
        if (box.GetNodeOrNull<MegaRichTextLabel>("Body/ModDescription") is not { } description)
            return;
        var home = description.GetMeta(HomeMeta).AsGodotArray();
        description.Reparent(info, false);
        info.MoveChild(description, Math.Min(home[0].AsInt32(), info.GetChildCount() - 1));
        description.ScrollActive = home[1].AsBool();
        (description.AnchorLeft, description.AnchorTop, description.AnchorRight, description.AnchorBottom) =
            (home[2].AsSingle(), home[3].AsSingle(), home[4].AsSingle(), home[5].AsSingle());
        (description.OffsetLeft, description.OffsetTop, description.OffsetRight, description.OffsetBottom) =
            (home[6].AsSingle(), home[7].AsSingle(), home[8].AsSingle(), home[9].AsSingle());
    }

    static ScrollContainer Add(Control info)
    {
        var box = new ScrollContainer
        {
            Name = BoxName, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, FollowFocus = true,
            MouseFilter = Control.MouseFilterEnum.Pass,
        };
        box.AddThemeStyleboxOverride("panel", new StyleBoxEmpty());
        box.AddChild(new Control { Name = "Body", MouseFilter = Control.MouseFilterEnum.Pass });
        info.AddChild(box);
        // Scrolled: other mods' parts follow (they stay the panel's: ModDetails.PlaceFields).
        box.GetVScrollBar().ValueChanged += _ => ModDetails.PlaceFields(info);
        ScrollFade.Watch(box, box.GetVScrollBar()); // more out of sight above or below: a fade at that edge
        return box;
    }
}
