using Godot;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;
using MegaCrit.Sts2.Core.Nodes.Screens.Settings;
using MegaCrit.Sts2.addons.mega_text;

namespace SpireModManager;

// "Spire Mod Manager" on the game's Settings screen: a way to our page that carries the mod's name. Off to the side of
// the settings list, in the top-right corner, level with the tabs. A copy of the screen's own Mod Settings button, so
// it looks and moves like it, or our small button if a game update took theirs away. It opens the Modding page as that
// button does, on the Settings screen's stack, so Back returns to Settings. Only on the main menu's Settings: the one a
// run opens (pause menu) sits on the run's stack, which has no Modding page.
static class SettingsButton
{
    public const string Name = "SpireModManagerSettingsButton";
    public const string TipName = "SpireModManagerSettingsTip";

    // The size of the game's button art (552x134) at 48 px high. Its right edge 12 px in from the screen's; level with the
    // tabs (y 72..162). At 4:3 the screen is 1680 wide and the tab row ends at x 1462 (with a controller's hint icon), so
    // it starts just after. The label keeps clear of the art's slanted ends.
    public static readonly Vector2 Size = new(200, 48);
    const float FromRight = 12, TabsMiddle = 117, LabelInset = 14;

    public static void Add(NSettingsScreen screen)
    {
        if (screen.GetParent() is not NMainMenuSubmenuStack stack || screen.HasNode(Name)) // readied again: one is enough
            return;
        var text = Menus.Text("SPIRE_MOD_MANAGER.SETTINGS_BUTTON");
        void Open() => stack.PushSubmenuType<NModdingScreen>();
        Control button = (Control?)CopyGameButton(screen, text, Open) ?? PlainButton(screen, text, Open);
        button.SetAnchorsPreset(Control.LayoutPreset.TopRight);
        (button.OffsetLeft, button.OffsetRight) = (-FromRight - Size.X, -FromRight);
        (button.OffsetTop, button.OffsetBottom) = (TabsMiddle - Size.Y / 2, TabsMiddle + Size.Y / 2);
        button.PivotOffset = Size / 2; // hover and press scale it about its middle
        Hover.Watch(button, TipName, () => Menus.Text("SPIRE_MOD_MANAGER.SETTINGS_BUTTON_TIP"));
    }

    // The screen's Mod Settings button, copied without its connections (the game's opens the page itself; a copy of that
    // would open it twice) and out of controller navigation (its neighbours are the list's). Null if it isn't there.
    static NButton? CopyGameButton(NSettingsScreen screen, string text, Action onPressed)
    {
        const int flags = (int)(Node.DuplicateFlags.Groups | Node.DuplicateFlags.Scripts | Node.DuplicateFlags.UseInstantiation);
        if (screen.GetNodeOrNull<NButton>("%ModdingButton")?.Duplicate(flags) is not NButton button)
            return null;
        if (button.GetNodeOrNull<MegaLabel>("Label") is not { } label)
        {
            button.Free();
            return null;
        }
        button.Name = Name;
        button.UniqueNameInOwner = false;
        button.FocusMode = Control.FocusModeEnum.None;
        button.CustomMinimumSize = Size;
        button.Size = Size;
        if (button.GetNodeOrNull<Control>("Image") is { } image)
            image.CustomMinimumSize = Vector2.Zero; // 64 high in the list: taller than ours, the art would sit low
        (label.OffsetLeft, label.OffsetRight) = (LabelInset, -LabelInset);
        screen.AddChild(button); // its _Ready sets the game's label: ours after
        label.SetTextAutoSize(text);
        button.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(_ => onPressed()));
        return button;
    }

    static Button PlainButton(NSettingsScreen screen, string text, Action onPressed)
    {
        var button = Menus.SmallButton(text, onPressed, name: Name);
        button.ClipText = true;
        button.AddThemeFontSizeOverride("font_size", 20);
        screen.AddChild(button);
        return button;
    }
}
