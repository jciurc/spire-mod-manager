# Changelog

What changed in each Workshop release, for players. Each version's first line is its Workshop change note
(`changeNote` in `workshop/workshop.json`), word for word.

## 0.2.0 (2026-10-22)

0.2.0:
- mods that share the same ID can now be edited independently
- Mods that share the same ID (duplicate) will only load one copy, with an explanation of which and why
- search supports OR, NOT and brackets, e.g. (#gameplay OR #library) #enabled
- additional keywords are generated based on a mod's status to help narrow searches even more
- while searching, a mod's hover card says where the search found it, e.g. "Search term found in: author, tags"
- warning banner show button now filters to the list of affected mods, the previous show functionality moved to "Go to" button
- Vanilla menu button was moved to the top of the page and now has a simple link style
- mods are sorted alphabetically within each category 
- tags show as their styled pills within the input while editing them; they can be deleted using backspace
- the info panel now explains mods that affect base game content, such as a character reskin; click it for the details
- long mod descriptions in the info panel show 3 lines; hover them to read the rest
- "Notes" is now "Note"; exported lists use a Note column, and older lists still import their notes
- filter buttons, unsaved profile indicator, and restart message in the main menu, now all show the count of their respective mods
- fixed dropdowns from clipping long lists; content now wraps to additional columns if overflowing
- other mods can now use Spire Mod Manager's menus, banners and mod cards (API version 1)

- A mod installed more than once (a Workshop item and a local copy, or two from one source) has a box per copy, and each
  box turns just that copy on or off: enabling one no longer disables the others, so disable the copies you don't want.
  With two or more enabled, only one loads. Between a local copy and a Workshop one, the game's own rule decides: the
  local copy on stable, and on the beta the higher version, else the local copy. Copies from one source share one switch
  in the game, so Spire Mod Manager decides between them: the higher version, and with the same version (or none that
  can be read), the one installed first. The page says which. A copy that won't load reads "Duplicate - Won't load" (it
  was "Not loaded (same ID)"), and its info panel names the copy that will be used and why: "Touhou Fairies (Local)
  will be used due to the game prioritizing local copies - disable copies you don't want to use", or due to "its higher
  version", "the same version and an earlier install date" or "this copy's version being unreadable". The copy that
  loads says "Touhou Fairies (3813186417) is also enabled - only this copy will be loaded", and the box of a disabled
  copy, beside an enabled one, says which copy would load if you enabled it as well.
- Only the enabled copies count: a disabled copy never loads in place of an enabled one, including on the beta, where
  the game's one switch for a mod's Workshop copies would otherwise let a disabled copy win. Where the copy that would
  load needs a mod that loads after it, while another enabled mod needs that copy, another enabled copy that can load
  in time loads instead, if there is one, chosen by the same rules. Its info panel says so: "This copy would load
  before a required dependency. Touhou Fairies (Local) is currently compatible and will be loaded instead." A copy
  whose required mod is disabled or not installed still loads, and the usual warnings say what it needs, as for a mod
  installed once.
- The Same ID tag is now Duplicate, in a muted tan. While copies of a mod are enabled together, it turns the warning
  yellow on every enabled copy, the one that loads too, as does the info panel's line about the copies, and its card
  lists the other enabled copies, one to a line, and the one that will be used. The yellow banner reads "2 mods with
  the same ID are enabled - only one copy of each will be loaded". Its card groups the enabled copies by ID, each with
  its version and source, the one that loads first: "TouhouFairies:", then "Touhou Fairies 0.1.1 (Local) - Active" and
  "Touhou Fairies 0.1.0 (3813186417) - Ignored". "Before you relaunch" lists them the same way, under "Some mods with
  duplicate IDs are enabled, and won't be loaded", and its Disable unused mods turns off the Ignored copies. Enable
  all, which now enables every copy, says when it would enable more than one copy of a mod. An enabled copy's name is
  no longer greyed out.
- A newly installed copy of a mod you have now arrives enabled, as a new mod does (disabled only where your copies of it
  from the same source all are), and the rules above decide which copy loads: of copies from one source, a higher
  version takes over by itself, whether it was just installed or an update made it higher. Installed while the game
  runs, the New Mod Added popup says whether the new copy is enabled and, beside another enabled copy, which copy will
  be loaded: "A copy of Touhou Fairies was recently installed and is enabled. Only Touhou Fairies (Local) will be
  loaded. Disable copies you don't want to use." A change of which copy loads with no box changed shows in Changes, as
  one copy loading instead of another, and counts in the main menu's restart note.
- Profiles keep each copy's box, and a profile you switch to counts a copy it hasn't seen as enabled where it has that
  mod enabled. Importing a list, a row with a Workshop link sets only that copy of a mod you have more than once,
  leaving a copy installed by hand as it is, and where the list would leave your Workshop copy and the one installed by
  hand both enabled, the preview names the mod under "You currently have a manual installation of one of the listed
  mods. Both copies will remain installed, but only one should be enabled to prevent a clash." Updating from 0.1.4 loses
  nothing: each copy keeps its category, alias, note and tags, and the copies enabled before are the ones enabled now,
  in every profile too, so the same copy loads.
- With Spire Mod Manager disabled (every copy of it, where it's installed more than once), the red banner shows and
  Relaunch now asks first: "Spire Mod Manager (this mod) will be disabled on relaunch. To use it again, enable it on the
  vanilla modding page, and it will load after the next restart." The dialog's Enable now, like the banner's, turns the
  copy that's running back on. Installed more than once, its copies come first in their category; as before, their boxes
  can be unticked (two from one source share one) and which of them loads is left to the game. Two of them enabled
  clash like any mod's copies: the yellow Duplicate tag, the yellow banner and the same lines in the info panel.
  Disable all, the page's or a category's, still leaves every copy of it on, but a selection that includes it now
  turns it off with the rest.
- Search: OR (or `|`) finds either side, NOT leaves a term out as "-" does, and brackets group terms:
  `(#gameplay OR #library) #enabled`. A space or AND means both, and OR is read first, so `#a OR #b #c` is
  `(#a OR #b) #c`. In capitals, OR, AND and NOT are always operators (put them in quotes to search for the word); in
  lowercase, only between tags, authors, quotes or brackets, so `#warning or #error` works and `rock and stone` is
  still three words. A search without them finds what it did before, an unfinished one never fails (a missing
  bracket closes at the end), and the box takes 200 characters, up from 60.
- Keywords find mods by state, in English in every language: `#enabled`, `#disabled`, `#workshop`, `#local`,
  `#installed`, `#loaded`, `#notloaded`, `#pending`, `#new`, `#changed`, `#moved`, `#edited`, `#renamed`, `#note`,
  `#duplicate`, `#aside` and `#uncategorized`, and for problems `#unmet`, `#needed`, `#order`, `#clash`, `#managers`,
  `#failed` and `#gameerror`, with `#error`, `#warning` and `#problem` gathering them. A tag of yours with the same
  name finds its mods too. They work without the `#` as well (`enabled -workshop`), as a whole word: `load` doesn't
  find every loaded mod. `#missing` (or `#uninstalled`) shows a line counting the profile's mods that aren't
  installed, with "See list" to list them. The info panel lists the keywords that find its mod under "Other
  keywords". Click one to search for it, or Shift-click to add it to the search.
- On the warning banners, the old Show, which opens the next affected mod, is now Go to, and it steps through the
  banner's mods in the order the list shows them, from the mod in the info panel down. A new Show lists just the
  banner's mods by putting its search in the box (`#unmet OR #needed`, `#clash`, `#managers`, `#order`); press it
  again to clear it. In Load Order, where every row stays, it finds them instead. The missing-mods banner's Show
  still opens its list. A mod you fix while a search lists it stays in the list until the search changes, so it
  doesn't vanish from under the mouse.
- The page has its own Main Menu button in the bottom-left corner, beside Load Order (and beside Return in Load
  Order), where the game's back arrow was; Escape and F3 still leave the page. The Vanilla List button is gone: "Go to
  vanilla modding page", a link right of the page's title, opens the game's page, and "Go to Spire Mod Manager page",
  in the same spot there, comes back. The info panel's line about other mods' parts that don't fit points there too
  ("2 more from other mods: see the vanilla modding page"). Mods are listed alphabetically by alias or name within
  each category, with Spire Mod Manager first in its category. The main menu button's card says F3 opens the page and
  right-click has more options.
- Editing a mod's tags, each finished tag shows as a pill while you type. Backspace or Delete next to a pill removes
  the whole tag (Ctrl+Z puts it back), and holding the key stops at a tag instead of running on through it. A comma
  finishes a tag, pasting a list adds each as a tag (skipping repeats), Copy and Cut take whole tags, and a
  double-click selects one. The suggestions have a heading, "Tags in use; Shift-click to add multiple": a click adds a
  tag and finishes editing, a Shift-click adds it and keeps the list open, and Ctrl+Z takes back a picked one. Save
  and Cancel sit at the right end of the Tags line.
- A mod's info panel says what it changes in the game itself, on one line before Other keywords: "Modified content:
  The Regent (energy orb), merchant room and 4 more", the parts of the game it touches. Hovering it says how many
  places in the game's code it changes and how many of the game's files it replaces, and names the parts, and a click
  opens "Expected content from" the mod: its affected elements, alphabetically in columns, the files it replaces,
  with their paths, and the places in the code it changes, each marked before, after, rewrite or final in its own
  colour, with a legend saying what each means. Changes to the game's code show only while the mod is loaded, and
  where nothing is found the line reads "Modified content: No information found". Search finds a mod by these too, so
  "regent" lists the mods that change the Regent.
- The info panel keeps more in view: a long description shows three lines, ending in "..", and hovering them shows all
  of it; text with more to scroll fades at that edge; and Compatibility no longer adds "Workshop branches", as a mod
  marked for one branch has its Stable or Beta tag.
- On a mod's row, tags that don't fit end the line as "and 3 more", whose card shows the rest, with
  "Click to view in info panel" (the "+3" chip is gone). The row's hover card starts with the mod's picture, where it
  has one, and while you search it ends with where the search found the mod ("Search term found in: author, tags"). A
  gap follows its warnings, your alias, tags and note sit together, and the note follows "Note:" on the same line.
- A mod's notes are its Note now: the info panel's field, the cards, the pencil's "Edit alias, tags and note" and the
  bulk edit's "Edit note for selected mods". An exported list's column is Note, and import reads Note or Notes, so
  the notes in older lists and Better Mod Menu's still come in.
- Counts: the filters read All (20), Enabled (15), Disabled (4) and Changed (3), after the search, and keep their
  width as the numbers change. The profile bar's "Unsaved (14) •" says how many changes aren't saved (each
  profile's mod count is in the profile list). While a relaunch is pending, the main menu's note counts the mods
  turned on, off or moved ("15 mod changes will be applied after a restart", or "1 mod change will be applied after a
  restart"), and the main menu button's card ends with a summary of those changes.
- Menus taller than the screen go into columns, scrolling if there are more than fit across, instead of running off
  it. Menu items show their tips, such as Disable all in the category that holds Spire Mod Manager: "Spire Mod
  Manager (this mod) will remain enabled".
- The Cosmetic and Gameplay tags' cards are reworded, and the Cosmetic tag now also comes from Workshop tags for
  audio, sound, music or reskins, and no longer from "UI". A tag of your own says how many mods have it: "Your custom
  tag, applied to 3 mods".
- Import and export: the import preview reads "3 mods sorted into categories" ("1 mod sorted into categories" for
  one), and the dialog keeps its size as an error comes and goes. Import profile now comes before Export profile in
  the profile list and its right-click menu, and an exported list no longer ends with a line break.
- A Workshop mod's title on its row is a soft blue (dimmer while disabled), a local mod's stays white, and the
  category bars line up with a selected row's box; the vanilla modding page keeps the game's colours.
- Our links no longer underline: their colour and the pointing hand mark them, the info panel's source link included,
  while the vanilla modding page keeps the game's look. The game's own words, in the info panel, the hover cards and
  the dialog when the game can't start, sit in a box with an even border and rounded corners, no longer cut off at
  its sides. The caption over a disabled mod's load errors reads "Error while loading - Can be ignored if disabled". A
  mod's hover card closes when its row scrolls out of view under the mouse, and a row scrolled out of the list no
  longer counts as hovered.
- Fixes: deleting the active profile, or making a new category from one mod's menu, is one undo step; Escape and F3
  close the missing mods list; an unfinished `#"quoted tag` no longer hides every mod; clicking a category header while
  searching no longer folds it out of sight; leaving Load Order mid-drag no longer drops the mod at the top; a mod
  installed while the main menu is open shows the main menu's restart note and dot at once, instead of after your next
  change; Load Order's search box keeps its width as you type, instead of narrowing once a search finds something; text
  cut short to fit, such as a long name on its row, no longer splits an emoji or an accented letter; a long category
  name, as an imported profile can bring, is cut with ".." on its mods' category buttons and in the info panel's
  category picker instead of squeezing the row or running past the panel; and the external-link mark after the info
  panel's Workshop link, and after Open translations folder, sits on the text's line when another mod adds fonts to the
  game (it sat a little low). A mod with its local copy enabled and its Workshop copy disabled no longer comes back
  with both enabled, or both disabled, after Disable suspect mods on the dialog when the game can't start, or after
  closing the game before it reaches the main menu.
- With Mod Doctor: when it turns off the mod that stopped a launch and restarts the game, the main menu no longer also
  shows Spire Mod Manager's notice about that launch.
- For other mods: Spire Mod Manager has an API (version 1), so mods built on it can use its menus, banners and mod
  cards, and show the alias, note, tags and category you've given a mod.

## 0.1.4 (2026-10-20)

0.1.4:
- fixed profile import not including shared categories, aliases or notes
- import preview now shows category changes count

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
