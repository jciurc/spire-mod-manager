using Godot;

namespace SpireModManager.Api;

/// <summary>One menu row; a heading or a divider only through Heading and Divider.</summary>
public sealed class MenuEntry
{
    /// <summary>The row's words.</summary>
    public string Label { get; init; } = "";

    /// <summary>What a click does; the menu closes first. Null: the row is dimmed and does nothing.</summary>
    public Action? OnSelect { get; init; }

    /// <summary>Dimmed and not clickable.</summary>
    public bool Disabled { get; init; }

    internal bool IsHeading { get; init; }

    /// <summary>A gold heading over a line (your mod's name and version, for example).</summary>
    public static MenuEntry Heading(string label) => new() { Label = label, IsHeading = true };

    /// <summary>Just the line.</summary>
    public static MenuEntry Divider() => new() { IsHeading = true };
}

/// <summary>Where a menu opens; every property is optional.</summary>
public sealed class MenuOptions
{
    /// <summary>What it opens off (a button's rect: it drops below it, or above where there's no room). Null: at the mouse.</summary>
    public Rect2? From { get; init; }
}

/// <summary>SMM's pop-up menus: an overlay on its own layer, not an engine popup. One open at a time across mods. A press outside or Esc closes it; the screen's hotkeys are off and the keyboard is the menu's while it's open. It closes on its own when its owner leaves the tree or stops being visible.</summary>
public static class PopupMenus
{
    /// <summary>Opens a menu at the mouse (closing any open one, whichever mod's); returns it, or null if there's nowhere to show it. SMM owns it.</summary>
    public static Node? Show(Node owner, IReadOnlyList<MenuEntry> entries) => Show(owner, entries, new MenuOptions());

    /// <summary>The same, opening where the options say.</summary>
    public static Node? Show(Node owner, IReadOnlyList<MenuEntry> entries, MenuOptions options) => ApiGuard.Run<Node?>("PopupMenus.Show", null, () =>
    {
        if (!GodotObject.IsInstanceValid(owner) || !owner.IsInsideTree())
            return null;
        var items = entries.Select(Item).ToList(); // as they are now: the caller's list may change after
        return MenuPanel.Show(owner, () => items, options.From, keyboard: true);
    });

    /// <summary>True while a menu is open, any mod's.</summary>
    public static bool IsOpen => ApiGuard.Run("PopupMenus.IsOpen", false, () => MenuPanel.IsOpen);

    /// <summary>The open menu, any mod's, or null.</summary>
    public static Node? Current => ApiGuard.Run<Node?>("PopupMenus.Current", null, () => MenuPanel.Current);

    /// <summary>Closes the open menu, whichever mod's; true if one was open.</summary>
    public static bool Close() => ApiGuard.Run("PopupMenus.Close", false, MenuPanel.Close);

    /// <summary>A menu's rows in order, with whether each is dimmed (for self-tests; every build). Headings are left out.</summary>
    public static IReadOnlyList<(string Label, bool Disabled)> Rows(Node menu) =>
        ApiGuard.Run<IReadOnlyList<(string, bool)>>("PopupMenus.Rows", [], () => MenuPanel.Rows(menu));

    /// <summary>A menu's headings in order (for self-tests).</summary>
    public static IReadOnlyList<string> Headings(Node menu) => ApiGuard.Run<IReadOnlyList<string>>("PopupMenus.Headings", [], () => MenuPanel.Headings(menu));

    /// <summary>Clicks a row by its label, as a left click would; false if there's no such row, or it's dimmed.</summary>
    public static bool Press(Node menu, string label) => ApiGuard.Run("PopupMenus.Press", false, () => MenuPanel.Press(menu, label));

    // A row as SMM's menus have it: a heading (or a divider: one with no words), a dimmed row that does nothing, or a row
    // whose action runs guarded once the menu has closed.
    static MenuItem Item(MenuEntry entry) => entry.IsHeading ? MenuItem.Heading(entry.Label ?? "")
        : entry.OnSelect is { } select && !entry.Disabled ? new MenuItem(entry.Label ?? "", ApiGuard.Callback("a PopupMenus entry's OnSelect", select))
        : new MenuItem(entry.Label ?? "", () => { }, Disabled: true);
}
