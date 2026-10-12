using MegaCrit.Sts2.Core.Localization;

namespace SpireModManager;

// Edits to the player's categories. Each saves at once; the list is redrawn by the caller.
static class Categories
{
    public static Category? Find(string name) => Store.Data.Categories.FirstOrDefault(c => c.Name == name);

    public static void Assign(string modId, string? category) => Store.EditMod(modId, info => info.Category = category);

    public static Category Add()
    {
        var category = new Category { Name = UniqueName() };
        Store.Data.Categories.Add(category);
        Store.Save();
        return category;
    }

    // False if the name is empty or taken. Mods in the category follow the new name.
    public static bool Rename(Category category, string name)
    {
        name = name.Trim();
        if (name.Length == 0 || (name != category.Name && Find(name) != null))
            return false;
        foreach (var info in Store.Data.Mods.Values.Where(i => i.Category == category.Name))
            info.Category = name;
        category.Name = name;
        Store.Save();
        return true;
    }

    // Why a name can't be used, or null: empty, or another category has it.
    public static string? NameProblem(string name, Category? renaming = null)
    {
        name = name.Trim();
        if (name.Length == 0)
            return Menus.Text("SPIRE_MOD_MANAGER.NAME_EMPTY");
        return Store.Data.Categories.Any(c => c != renaming && c.Name == name) ? Menus.Format("SPIRE_MOD_MANAGER.NAME_TAKEN", ("name", name)) : null;
    }

    public static void Move(Category category, int step)
    {
        var list = Store.Data.Categories;
        var to = Math.Clamp(list.IndexOf(category) + step, 0, list.Count - 1);
        list.Remove(category);
        list.Insert(to, category);
        Store.Save();
    }

    // Its mods go back to Uncategorized.
    public static void Delete(Category category)
    {
        foreach (var (id, info) in Store.Data.Mods.Where(p => p.Value.Category == category.Name).ToList())
        {
            info.Category = null;
            if (info is { Alias: null, Notes: null, Tags: null })
                Store.Data.Mods.Remove(id); // nothing of ours left on it
        }
        Store.Data.Categories.Remove(category);
        Store.Save();
    }

    public static int CountMods(Category category) => Store.Data.Mods.Values.Count(i => i.Category == category.Name);

    static string UniqueName()
    {
        var name = new LocString("settings_ui", "SPIRE_MOD_MANAGER.NEW_CATEGORY_NAME").GetFormattedText();
        for (var n = 2; Find(name) != null; n++)
            name = $"{new LocString("settings_ui", "SPIRE_MOD_MANAGER.NEW_CATEGORY_NAME").GetFormattedText()} {n}";
        return name;
    }
}
