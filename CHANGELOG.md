# Changelog

What changed in each Workshop release, for players. Each version's first line is its Workshop change note
(`changeNote` in `workshop/workshop.json`), word for word.

## 0.1.4 (2026-10-20)

0.1.4: 
- fixed profile import not including shared categories, or custom user notes

- Importing a profile now applies the list's categories, aliases and notes. Our own lists carry them in their
  Category, Alias and Notes columns; Better Mod Menu's lists call categories groups. A mod takes the list's category,
  alias or notes only where it has none of its own: categories are shared by all your profiles, so mods you've
  already sorted stay where they are. Categories the list names that you don't have are added after yours, and
  mods that aren't installed yet go into theirs once they are. The import preview counts the mods sorted, the new
  categories and the aliases and notes added, and one undo takes the whole import back.

## 0.1.3 (2026-10-20)

0.1.3: 
- added option to restore last working mod list if the game fails to load
- mods that used the same ID can now be toggled and edited independently
- automatic tags for mods available on stable only (public) or beta only (public-beta)
- you can search by exact match and negative terms, with double quotes ("") and minus sign (-) respectively; As an example this would search for mods that have the cosmetic tag, but not the gameplay tag, with the exact spelling and casing MyMod: "MyMod" #cosmetic -#gameplay
- Added shortcut F3 to open the mod manager (only works from main menu)
- subscribe to an imported profile's missing mods from the game, with download progress
- search also looks in mod descriptions, and clicking a Workshop tag lists the mods with that tag

- When the game can't start after a change to your mods, Spire Mod Manager's dialog takes the place of the game's
  Quit-only one and offers to restore the last setup that worked and restart. The game's own message comes first,
  marked as the game's, then what changed since that launch, a line if the game itself was updated, and Mod
  Doctor's suspects if you have it.
- The crash dialog shows the game's own message in a box under "Notice from the game"; with Mod Doctor's suspects,
  "Disable suspect mods and restart" turns just those off, and "N more" names the rest when you hover it.
- A mod installed twice with the same ID (two Workshop items, or a Workshop item and a local copy) shows as two
  mods: each copy has its own row, category, alias, notes, tags and place in Load Order, and a Same ID tag.
  Changes and profiles say which copy, with its Workshop item id or "(Local)" after the name.
- Choose which of those copies loads: enabling one disables the others, in one undo step, and profiles, shared
  lists and the restore notice keep the choice. A yellow banner warns while two copies are on, and a copy the game
  set aside reads "Not loaded (same ID)" instead of "Pending relaunch". For this, Spire Mod Manager now always
  loads first, and Load Order keeps it at the top.
- Workshop mods marked for one Steam branch get a Stable or Beta tag; click it to list them. A mod that's switched
  off no longer turns the main menu's MODDED label red over its branch; its info panel still shows the game's
  message, marked as the game's.
- Importing a profile that names mods you don't have: subscribe to them from the import preview, with each
  download's progress. While the active profile has mods that aren't installed, a banner offers Show (a list to
  subscribe from), Disable missing mods and Dismiss.
- Search: a "-" before a term leaves out what it matches (`#stable -#beta`), sentence punctuation counts as a space
  (`dont` finds "Don't") but other symbols don't (`C#`), and "text in quotes" matches exactly, case included.
  Search also looks in mod descriptions, by the start of each word, and a Workshop tag clicked in the info panel
  lists the mods with that tag.
- Selected mods: Enable selected and Disable selected on the bar above the list, and any selected mod's tickbox
  sets them all. The bar counts them on a blue chip; click it, press Esc or Ctrl+D to let go. Selecting is a step
  in undo and redo.
- F3 opens or closes the page from the main menu, and the main menu button's right-click menu starts with Manage
  mods (F3).
- Hover cards and menus open above (or left of) what they're for when there's no room below, instead of covering
  it, the main menu button's card included, wherever it's moved.
- What's the game's and what's Spire Mod Manager's is easier to tell apart:
  - the game's load errors in the info panel sit in their own box under "Error while loading", and no longer show
    [red] tags as text;
  - the Vanilla List button's tip says it's the game's own page, and Vanilla List always shows every mod, even
    after a search on the main list;
  - a line in the info panel counts other mods' parts that have no room there, pointing to Vanilla List;
  - the saved-data notices and the main menu button's tip name Spire Mod Manager.
- Links that leave the game (the Workshop page, Changelog, the translations folder) end in the usual external-link
  mark.
- A mod installed while the game is open counts +1 in Mods enabled when it arrives on, and as an unsaved change to
  the profile.
- Warning banners no longer push the list's rows down as they appear, so a row doesn't jump out from under the
  mouse.
- Relaunching a game started from a Steam invite no longer joins that invite's lobby again.
- Renaming a category no longer shows its mod count behind the box. The notepad mark before a mod's name shows its
  notes card from a little around it too. A mod installed right after a popup closed gets its own New Mod Added
  popup.
- The Modding page builds its rows faster with long mod lists.
- For translators: `tools/loc.cs` shows what changed in the English since your translation and how the mod reads
  your file; see TRANSLATING.md. A drop-in text the game can't format (a plural form, a misspelled formatter) shows
  in English instead of breaking, and an empty one counts as not translated yet.

## 0.1.2 (2026-10-16)

0.1.2: the main menu button no longer asks for a restart when nothing changed (with two Workshop copies of one mod), and the Modding page opens faster with long mod lists, especially with Chinese, Japanese or Korean mod names.

- With two Workshop copies of one mod installed (the same mod id), the main menu button could ask for a restart
  while the Modding page showed no changes. Each copy keeps its own place in the saved load order now, and the
  page's "after a restart" line follows the same rule as the button.
- The Modding page opens faster with long mod lists. Mod names in Chinese, Japanese or Korean need a font lookup
  the first time they're drawn; that now happens on the main menu, a little at a time. Two slow checks at the
  page's start are gone too.

## 0.1.1 (2026-10-16)

0.1.1: the main menu button's hover wings show again alongside RitsuLib, now in pink; the author in the info panel reads as a link; and the Changed filter's tip says what it shows.

- The main menu button's hover carets are its own now, so a mod that runs the main menu's buttons (RitsuLib's main
  menu scroller) no longer hides them. They're pink, set apart from the game's gold.
- The author in a mod's info panel is light blue, like the mod names there: a click searches for their mods.
- The Changed filter's tip lists what it shows: mods enabled or disabled, installed, moved, or edited since launch.

## 0.1.0 (2026-10-16)

0.1.0: first release. Compatible with stable or beta-branch (game v0.107.1 or newer).

Spire Mod Manager expands the game's Modding page for long mod lists (the game's own page stays one click away):

- Your own collapsible categories, search over names, notes, tags and authors, and filters for enabled,
  disabled and changed mods.
- Profiles of which mods are on, with unsaved changes you can save, save as new or discard; export and import
  them as a list.
- A Load Order view: drag mods into order, see what each needs, Fix now and Auto sort.
- Aliases, notes and your own tags on each mod; Workshop details in the info panel.
- Warnings that name the mods needing a disabled or missing mod, and a check before relaunching.
- Changes since launch, undo and redo, and Revert all.
- Relaunch Game restarts in place, and a notice after a launch that didn't reach the main menu offers to go
  back to the last setup that worked.
- Runs alongside Better Mod Menu, and imports its groups, aliases, notes and profiles on first run.
