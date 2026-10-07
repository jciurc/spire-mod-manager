using Godot;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;

namespace SpireModManager;

// A menu entry: a label and action, or a heading (Action null) that separates groups.
record MenuItem(string Label, Action? OnSelect, bool Checked = false, bool Disabled = false)
{
    public static MenuItem Heading(string label) => new(label, null);
}

static class Menus
{
    static readonly Color Cream = new(1, 0.964706f, 0.886275f);
    static readonly Color Gold = new(0.937f, 0.784f, 0.318f);

    // A right-click menu at the mouse, styled to match our headers. Freed when it closes.
    public static PopupMenu Show(Node owner, IEnumerable<MenuItem> items)
    {
        var menu = new PopupMenu { Name = "SpireModManagerMenu" };
        Style(menu);
        var actions = new List<Action?>();
        foreach (var item in items)
        {
            if (item.OnSelect == null)
            {
                menu.AddSeparator(item.Label);
                actions.Add(null);
                continue;
            }
            if (item.Checked)
            {
                menu.AddCheckItem(item.Label, actions.Count);
                menu.SetItemChecked(menu.ItemCount - 1, true);
            }
            else
                menu.AddItem(item.Label, actions.Count);
            menu.SetItemDisabled(menu.ItemCount - 1, item.Disabled);
            actions.Add(item.OnSelect);
        }
        menu.IdPressed += id => actions[(int)id]?.Invoke();
        menu.PopupHide += menu.QueueFree;
        owner.AddChild(menu);
        menu.Position = (Vector2I)owner.GetViewport().GetMousePosition();
        menu.Popup();
        return menu;
    }

    // The game's own yes/no popup. True if confirmed.
    public static async Task<bool> Confirm(LocString title, LocString body)
    {
        if (NGenericPopup.Create() is not { } popup || NModalContainer.Instance is not { } modals)
            return false;
        modals.Add(popup);
        return await popup.WaitForConfirmation(body, title,
            new LocString("main_menu_ui", "GENERIC_POPUP.cancel"), new LocString("main_menu_ui", "GENERIC_POPUP.confirm"));
    }

    public static string Text(string key) => new LocString("settings_ui", key).GetFormattedText();

    static void Style(PopupMenu menu)
    {
        var panel = new StyleBoxFlat
        {
            BgColor = new Color(0.0756f, 0.12084f, 0.18f, 0.97f),
            BorderColor = new Color(Gold, 0.6f),
            ContentMarginLeft = 12, ContentMarginRight = 12, ContentMarginTop = 8, ContentMarginBottom = 8,
        };
        panel.SetBorderWidthAll(2);
        panel.SetCornerRadiusAll(6);
        var hover = new StyleBoxFlat { BgColor = new Color(0.2f, 0.32f, 0.42f) };
        hover.SetCornerRadiusAll(4);
        menu.AddThemeStyleboxOverride("panel", panel);
        menu.AddThemeStyleboxOverride("hover", hover);
        menu.AddThemeFontOverride("font", ResourceLoader.Load<Font>("res://themes/kreon_regular_shared.tres"));
        menu.AddThemeFontOverride("font_separator", ResourceLoader.Load<Font>("res://themes/kreon_bold_shared.tres"));
        menu.AddThemeFontSizeOverride("font_size", 24);
        menu.AddThemeFontSizeOverride("font_separator_size", 20);
        menu.AddThemeColorOverride("font_color", Cream);
        menu.AddThemeColorOverride("font_hover_color", Colors.White);
        menu.AddThemeColorOverride("font_disabled_color", new Color(Cream, 0.4f));
        menu.AddThemeColorOverride("font_separator_color", Gold);
        menu.AddThemeConstantOverride("v_separation", 10);
    }
}
