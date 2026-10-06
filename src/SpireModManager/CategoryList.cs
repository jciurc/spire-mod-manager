using Godot;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;
using MegaCrit.Sts2.addons.mega_text;

namespace SpireModManager;

// Groups the vanilla Modding screen's rows under collapsible category headers. The headers go into the
// screen's own row container (a VBoxContainer inside its scroll area), so scrolling, selection, the info
// panel and the tickboxes stay the game's. Vanilla view removes the headers and restores the order.
static class CategoryList
{
    const string HeaderScenePath = $"res://{ModEntry.ModId}/scenes/category_header.tscn";
    const string HeaderGroup = "SpireModManagerCategoryHeader";
    const float DragThreshold = 12; // a press that moves further than this is a scroll, not a click

    public static void Attach(NModdingScreen screen)
    {
        var content = screen.GetNode<Control>("%ModsScrollContainer/Mask/Content");

        NButton? toggle = null;
        toggle = ModEntry.AddButton(screen, ToggleLabel(), new Vector2(220, 70), () =>
        {
            Store.Data.VanillaView = !Store.Data.VanillaView;
            Store.Save();
            toggle?.GetNode<MegaLabel>("Visuals/Label").SetTextAutoSize(new LocString("settings_ui", ToggleLabel()).GetFormattedText());
            Apply(content);
        });
        toggle.Name = "SpireModManagerViewToggle";
        // Left end of the pending-changes row (y 1012-1056), aligned with the mod list's left edge (x 255).
        toggle.Position = new Vector2(255, 1034 - 35);

        Apply(content);
    }

    static string ToggleLabel() => Store.Data.VanillaView ? "SPIRE_MOD_MANAGER.CATEGORY_VIEW" : "SPIRE_MOD_MANAGER.VANILLA_VIEW";

    static void Apply(Control content)
    {
        foreach (var header in content.GetChildren().Where(c => c.IsInGroup(HeaderGroup)))
        {
            content.RemoveChild(header);
            header.QueueFree();
        }

        // Vanilla order is the game's mod order, which is how the screen first added the rows.
        var order = ModManager.Mods.Select((mod, i) => (mod, i)).ToDictionary(p => p.mod, p => p.i);
        var rows = content.GetChildren().OfType<NModMenuRow>()
            .OrderBy(r => r.Mod is { } mod && order.TryGetValue(mod, out var i) ? i : int.MaxValue)
            .ToList();

        if (Store.Data.VanillaView)
        {
            for (var i = 0; i < rows.Count; i++)
            {
                content.MoveChild(rows[i], i);
                rows[i].Visible = true;
            }
            return;
        }

        var categories = Store.Data.Categories;
        var known = categories.Select(c => c.Name).ToHashSet();
        string? CategoryOf(NModMenuRow row) =>
            Store.InfoFor(row.Mod?.manifest?.id)?.Category is { } name && known.Contains(name) ? name : null;

        var index = 0;
        void AddSection(string title, List<NModMenuRow> members, bool collapsed, Action<bool> setCollapsed)
        {
            var header = ResourceLoader.Load<PackedScene>(HeaderScenePath).Instantiate<Control>();
            header.AddToGroup(HeaderGroup);
            content.AddChild(header);
            content.MoveChild(header, index++);
            header.GetNode<Label>("Title").Text = title;
            header.GetNode<Label>("Count").Text = members.Count.ToString();
            foreach (var row in members)
                content.MoveChild(row, index++);

            void Show(bool isCollapsed)
            {
                header.GetNode<Control>("Arrow").RotationDegrees = isCollapsed ? 0 : 90;
                foreach (var row in members)
                    row.Visible = !isCollapsed;
            }
            Show(collapsed);

            var background = header.GetNode<ColorRect>("Background");
            var baseColor = background.Color;
            header.MouseEntered += () => background.Color = baseColor.Lightened(0.15f);
            header.MouseExited += () => background.Color = baseColor;

            var pressedAt = Vector2.Zero;
            header.GuiInput += e =>
            {
                if (e is not InputEventMouseButton { ButtonIndex: MouseButton.Left } click)
                    return;
                if (click.Pressed)
                    pressedAt = click.GlobalPosition;
                else if (click.GlobalPosition.DistanceTo(pressedAt) < DragThreshold)
                {
                    collapsed = !collapsed;
                    Show(collapsed);
                    setCollapsed(collapsed);
                    Store.Save();
                }
            };
        }

        foreach (var category in categories)
            AddSection(category.Name, rows.Where(r => CategoryOf(r) == category.Name).ToList(),
                category.Collapsed, c => category.Collapsed = c);

        var uncategorized = rows.Where(r => CategoryOf(r) == null).ToList();
        if (uncategorized.Count > 0)
            AddSection(new LocString("settings_ui", "SPIRE_MOD_MANAGER.UNCATEGORIZED").GetFormattedText(), uncategorized,
                Store.Data.UncategorizedCollapsed, c => Store.Data.UncategorizedCollapsed = c);
    }
}
