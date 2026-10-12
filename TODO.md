# TODO

Jordan's notes of 2026-10-07 (from `spire manager notes.txt` in the scratch folder), ticked off as they land.

## Vanilla View

- [x] "Category View" -> "Spire Manager" on vanilla page only
- [x] move "Spire Manager" button to bottom left corner, same positioning/padding as main menu button
- [x] display changes indicator on button
- [x] remove Load Order button
- [x] remove Relaunch Game button
- [x] persist and scroll to selected item when switching views, (auto expand category, load order page included)


## Category View

- [x] let's expand the content area even more
- [x] let's reduce font size and margins across the board
- [x] move page header closer to top left with smaller font
- [x] change "Installed Mods" -> "Spire Mod Manager" (non vanilla view only)
- [x] remove blue border around content panels
- [x] remove steam and folder icons
- [x] remove make/get mods buttons in our view
- [x] "Vanilla View" -> "Vanilla Manager"
- [x] let's use a dropdown for Profile selection, and left justify the profile name
- [x] update "Profile" label to "Profile:" [name, V <- dropdown handle icon]
- [x] color code and change "Needs" -> "Needs:"
- [x] (fix) items are clickable outside of the container (above and below)
- [x] (fix) container height/scrollbar does not shrink when collapsing categories
- [x] (fix) element focus is not dismissed when clicking an empty area
- [x] (fix) slight overlap with scrollbar and details panel
- [x] (fix) inline category (and load order) buttons are not centered vertically


## load order view

- [x] should have distinct view, maybe remove details panel and center?
- [x] Remove Vanilla button
- [x] Update "Category View" -> "Back"
- [x] remove steam/folder icons
- [x] clicking/holding drag handle should also select the item
- [x] remove Auto sort explanation <- we'll reintroduce it later
- [x] change incompatibility warning to "X mods will be automatically reordered (vanilla behavior) on launch based on dependencies" [Fix now]
- [x] let's remove the drag prevention when dragging into incompatible order. we'll just display the warning and let the user handle it
- [x] update "Needs X (loaded before), Y" -> "Needs: X before, Y" and color code before/after
- [x] color code connectors
- [x] remember last selected item when going back to category view
- [x] multi selectable/draggable items with shift/ctrl/cmd
- [x] (fix) load order view ui/state is not cleared when changing back to category
- [x] (fix) container doesn't scroll when dragging near bounds
- [x] (fix) remove needs connector when on category view


## Changes

- [x] Move button to above details panel left aligned
- [x] Replace button with tooltip
- [x] Show diff count on button, "Changes +4-3"
- [x] Changes overlay is anchored to Changes text
- [x] Turned On/Off -> Enabled/Disabled
- [x] soft/pastel color coding in overlay, green=add, red=remove, mod name colored by tag(or category?)
- [x] move revert button next to undo/redo
- [x] include internal changes within overlay, x items edited (category/tag/details change), x profile changes
- [x] show "Switched from X profile to Y" in changelist instead of all mod changes, (include changes that differ from selected profile. diff count will still show total mod additions/removals)


## undo

- [x] show hotkeys near buttons
- [x] show history position and count near buttons
- [x] load order and list view should have separate history states
- [x] undo history notes mod changes and load order changes separately


## tags

- [x] indicator for system tag vs custom tag <- something subtle, not sure what
- [x] let's do market research on additional tag features (DESIGN.md, Research: tags)


## details panel

- [x] reduce screenshot size to thumbnail
- [x] move details closer to title
- [x] remove Notes button for editable inline textarea
- [x] editable tag field in panel
- [x] Alias will be inline editable in the list - needs design thought here - small pencil to left of title?
- [x] replace workshop page button with full clickable url positioned under mod title
- [x] local mods can show "Local mod" text instead of url
- [x] remove "From" item
- [x] show compatibility game version numbers, if available


## Backlog

- [ ] table view with sortable columns
- [ ] load order position as column
- [ ] mod patchnotes, seeded by workshop if available
- [ ] selectable changes since X for mod patchnotes
- [x] moveable main menu button accessible with right click
- [ ] REMINDER: copy and design pass before publishing
- [ ] REMINDER: performance and code quality pass
- [ ] "Config" button next to mods with their own config page
- [ ] A settings page for Spire Mod Manager, don't know exactly what goes here yet. maybe profile management, and
- [ ] undo state specific to profiles
- [x] save, discard button specific to profiles
- [ ] mod manager accessible during gameplay
- [ ] save run and relaunch option during gameplay
- [ ] newly installed mods indicator
- [ ] mod recently updated indicator
- [ ] saveable load order presets
- [ ] show installed/removed mods that differ from preset
- [ ] larger previewable screenshots on hover in details panel
- [x] (fix) relaunch button briefly loads new game instance in lower resolution windowed mode instead of fullscreen (borderless)

# Round 2

Jordan's notes of 2026-10-07, second batch (same file), plus play feedback.

## Layout

- [x] (fix) drag is choppy and bounces at the very top and bottom of the list
- [x] move Spire Manager button (main menu and mod menu) slightly down to line up with Vanilla / Load Order buttons
- [x] Ctrl+Z / Ctrl+Y shown under their buttons, formatted "Ctrl + Z"
- [x] remove history counter
- [x] move Changes diff to the right of the "Spire Mod Manager" title
- [x] equal x/y padding in the profile dropdown
- [x] shift content panels up: equal margin below header and above footer buttons
- [x] remove dependency connectors in Category view
- [x] narrower list, wider details
- [x] "Back" -> "Return"
- [x] "Vanilla Manager" is cramped: "Vanilla List"
- [x] Vanilla button in the Spire Manager button's spot (lower left); Load Order shifts over
- [x] Vanilla button on the Load Order page too (same spot)
- [x] Spire Mod Manager title and Changes diff in the same place on both views
- [x] "Load Order" title centred in the header on the Load Order page
- [x] remove the restart warning on the Load Order page
- [x] remove Relaunch on the Load Order page
- [x] Auto sort to the right of the search bar; less whitespace around the order warning
- [x] vanilla version/date watermark legible on our page (light text)
- [x] clicking anywhere outside the current input (header, details, footer, blank space) drops focus

## Details panel

- [x] add "Dependencies: x, y"
- [x] thumbnail is not small enough
- [x] link text: "Source: Workshop ID ## (clickable)" / "Source: Local mod"
- [x] terms line up with the panel header
- [x] order: Author, description, Version, Game version, then the rest
- [x] "Game version" -> "Compatibility: x.y.z or newer" (+ stable/beta/current if it applies)
- [x] error indicator and message for incompatible mods (version, conflict, dependency, corrupt...)
- [x] no system tags in details
- [x] tags and notes fields look like the other terms while inactive
- [x] pencil right of the Tags and Notes terms
- [x] tag field: one line of space; notes: four lines
- [x] tag placeholder only while active: "Custom tags separated by commas.."
- [x] notes placeholder: "Custom notes.."

## Tags

- [x] remove the "Visual only" tag

## Rows

- [x] long titles truncated, with tooltip
- [x] inline name editing shows the original name behind the field (fix)
- [x] alias pencil only on row hover
- [x] a bit more space between pencil and name
- [x] clicking the pencil again closes (and accepts) the edit
- [x] clear button on the inline editor

## Profiles

- [x] Save and Revert next to Profile
- [x] unsaved changes: "Profile: [dropdown]  Unsaved • [Save] [Save as new] [Discard]"
- [x] Save as new replaces New, Rename and Delete
- [x] unsaved profile kept as its own entry "Name (edited [time])" until saved or discarded; nothing destructive or unrevertable (UX to be worked out)

## Load Order (play feedback)

- [x] dependency connectors stretch and shrink while dragging
- [x] mod names in "Needs:" a different colour from "before/after"
- [x] no inline name editing on the Load Order page
- [x] whole row draggable with the move cursor; the right-side actions keep the normal cursor
- [x] tickboxes disabled on the Load Order page (ordering and profiles only)
- [x] disabled mods dimmer on the Load Order page
- [x] order warning sticky at the top

## Category view (play feedback)

- [x] Revert all goes back to the state when the Modding page was opened (the profile, or the profile *edited), not launch
- [x] changed mods (since launch) shown in a different colour

## Backlog (round 2)

- [ ] tag for mods that need multiplayer sync (everyone must have them)
- [ ] minimap scroll bar (like VS Code) marking notable items
- [ ] banner when disabling a mod that enabled mods depend on (e.g. BaseLib)
- [ ] view enabled only / disabled only (sticky items when toggling)
- [ ] view edited mods
- [ ] enable all / disable all
- [ ] filter by author
- [x] stylized Spire Mod Manager button (Jordan designs)
- [ ] export/import/share mod collections and configuration
- [ ] import BMM profiles from a settings page
- [ ] export as BMM csv (or save into BMM's folder)
- [ ] toggle between BMM and Spire Manager
- [ ] keyboard navigation: up/down, space to select
- [ ] multi-select and bulk actions in Category view
- [x] profile and category dropdowns use a radio-style mark (single choice)
- [ ] auto update and polling
- [ ] link to the game's patch notes

# Round 3

Jordan's notes of 2026-10-11.

## Profiles

- [x] short date in the edited timestamp (moot: unsaved profiles show "Name *" now, no timestamp)
- [x] dropdown: mod count and last updated next to each profile name
- [x] Load Order page: drop the profile bar (done: dropped), OR give load order its own profile system; each main profile links to the last load-order profile (if it has no link yet); show the linked load profile in the main profile's details

## Changes

- [x] tooltip lists the added/removed mods
- [x] clicking Changes shows the full changes in the right-side panel

## Main list

- [ ] select/deselect all for the whole page and per category
- [ ] multi-select on the main page: move to a category, select/deselect
- [ ] drag and drop: whole categories, single mods, multi-selections
- [ ] author name on the row (right side, above the category button)
- [ ] category button shows the category's name
- [ ] colour codes for categories and/or tags
- [x] "Needs: X": X green when enabled, red when not
- [ ] (fix) the changed (gold) title colour overrides custom colours
- [x] (fix) thumbnail in the right-side panel still wrong

## Relaunch

- [ ] warning before relaunch when enabled mods miss dependencies or are out of order: list the problem mods; offer enable required mods (listed), disable the incompatible ones, relaunch anyway, close
- [x] main menu: tooltip or small "requires restart" text by the pending dot

## Backlog (round 3)

- [ ] in-game error log: collect and show errors (ours and other mods')
- [ ] load-order profiles of their own, linked from main profiles (pairs with saveable load order presets)

# Round 4

Jordan's notes of 2026-10-11 (evening), in his order.

- [x] (fix) "(off)" stays on a dependency that was just turned on (pending relaunch)
- [x] "Required for N mods" on mods others depend on
- [x] dimmer colour for "turned off, pending" vs "turned on, pending"
- [x] confirm, cancel and clear buttons on text inputs (profile name/rename, tags, notes)
- [x] Duplicate as a profile option
- [x] Save as new and Duplicate open the name box with the original name plus " (1)" (or the next free number)
- [x] renaming to a taken name warns
- [x] (fix) rename box stays open when Delete is clicked mid-rename
- [x] (fix) clicking the picker while its list is open closes it and opens it again
- [x] no visible "Name (edited x)" profiles: an unsaved profile shows "Name *" and keeps its edit behind the scenes
- [x] Save, Save as new and Discard ("Discard", not "Revert") beside the "Unsaved" label, which gets its own colour
- [x] no pencils by Tags and Notes; at rest, subtle "Click to add tags" / "Click to add notes"
- [x] placeholders without the dots ("Custom notes")
- [x] Alias field (below Tags, above Notes) in the info panel
- [x] Tags field right below the mod's details, with some margin; a bigger gap between Alias and Notes
- [x] row right-click menu: Add/Edit alias, Add/Edit notes, Add/Edit tags, each turning the menu into an inline editor (Save / Cancel back to the menu)
- [x] main menu button: Jordan's art (Pictures\symphony\design\spire manager button.png), our label, a hover look
- [x] right-click the main menu button to move it (soon, no rush)

# Round 5

Jordan's notes of 2026-10-11 (late).

- [x] (fix) Undo and Revert all buttons get out of sync
- [x] (fix) Load Order: hover dependency lines don't clear or follow when scrolling with the wheel (not dragging)
- [x] Load Order: a row is in the dragging state as soon as it's pressed (no need to move first)
- [x] a third count for load order in Changes, in blue: "Changes +0 -3 +0"; the tooltip's "Load order:" line in the same blue
- [x] Changes back down, left of the undo buttons
- [x] clicking Changes, Undo, Redo or Revert all shows the full changes list in the right panel
- [x] (fix) Undo empties the right panel
- [x] when only the profile changed, the tooltip still lists Enabled / Disabled / Load order lines
- [x] (fix) alias pencil fades when hovered
- [x] Load Order page: no Vanilla List button
- [x] "Main Menu" label over the back arrow (our pages, not Vanilla View)
- [x] a little bottom margin under the undo buttons and their hints
- [x] inline alias box higher, not covering the tags

# Round 6

Jordan's notes of 2026-10-12.

## Rows
- [x] remove the inline alias pencil (alias via the right-click menu or the side panel)
- [x] subtle notes icon on rows with custom edits
- [x] hovering a mod title (1 s) shows its custom notes
- [x] "Required for N enabled mods"
- [x] pill tooltips: Gameplay "This mod affects gameplay"; Library "This mod is a framework used by other mods - often does nothing on its own"
- [x] Cosmetic tag when known: "This mod adds visuals, such as character skins, battle animations, UI, or textures"

## Right-click menu
- [x] two columns: categories on the left, the fields with inline editors on the right (no nested page)
- [x] right-clicking elsewhere closes the menu and passes the right-click on to what's under the mouse
- [x] clicking the Category button again closes its menu (like the profile list)
- [x] New category scrolls to the new category's name box (bottom of the list)

## Side panel
- [x] order: original name (heading); link "<id> v<version> (Workshop <UID>)" without a label; Enabled / Disabled / Pending restart (green / grey / orange); description; author, published, updated, size, subscribers, compatibility, Workshop tags; dependencies; Alias, Category (dropdown), Tags (system pills, then custom tag pills/editor), Notes
- [x] fields: Alias, Notes, then Tags; CTAs "Add alias", "Add notes", "Add tags"; same look, text and placeholders in the right-click menu; one shared component
- [x] compatibility: no repeated branches (old betas all read "beta")
- [x] notes trimmed when the editor closes
- [x] clicking the panel's title scrolls the mod into view in the list
- [x] clicking a dependency's name scrolls to it and highlights it (without opening it in the panel)

## Text inputs
- [x] a bit of padding in all text inputs
- [x] no Clear buttons: an inline X instead

## Changes
- [x] two lines: "Mods enabled N (+X -Y) ↕moved", then "Changes N" (moved + edited + profile changes; the tooltip lists them apart)
- [x] a bit of space above "Click for the full list"
- [x] Undo, Redo, Revert all don't open the changes list; only clicking the Changes text does
- [x] clicking the Changes text again closes the list
- [x] (fix) with the list open, a tickbox click switches the panel to that mod; it should stay until Close or a click on a row
- [x] alias edits in the changes tooltip and list
- [x] undo buttons centred vertically with the page buttons

## Profiles
- [x] the unsaved * in the profile list in red
- [x] hovering "Unsaved" shows that profile's own changes

## Search
- [x] case- and whitespace-insensitive
- [x] (fix) after filtering while scrolled down, the first match hides behind the search bar

## Other
- [x] category mod count right after the category name
- [x] "Main Menu" label a few pixels higher
- [x] (fix) Move button: the button jumps to the mouse and then doesn't follow; the hint stays on screen (even on other pages); Esc puts it back

## Backlog (round 6)
- [ ] click a tag to filter by it; Shift-click adds more tags
- [ ] profile details panel (start of the options page): name, updated date, Save/Rename/Duplicate/Delete, a description, linked load-order profile, enabled mods, unsaved changes as a diff

# Round 7

Jordan's notes of 2026-10-12 (later).

- [x] panel fields: Alias, Category, Tags, Notes
- [x] "Changes N" without load-order moves (they're next to "Mods enabled")
- [x] the profile's mod count next to the picker (the list keeps its details)
- [x] (fix) mods subscribed while the page is open appear at the bottom in the game's plain row style; they should look like ours, in place, at once

# Round 8

Jordan's notes of 2026-10-12 (evening).

## Rows
- [ ] all rows a little shorter
- [ ] pills in the order Local, Library, Gameplay, Cosmetic, then custom; a "Local" tag for local mods
- [ ] "Required for N" before "Needs: X" (both pages)
- [ ] a "notepad" icon instead of ✎ for rows with an alias or notes; none in Load Order
- [ ] a styled pencil button (right side of the row) opening the edit menu; none in Load Order
- [ ] hover card for every mod: original name (id), enabled state, error, author, updated, custom tags and notes
- [ ] (fix) the hover card sometimes stays open while using other parts of the page

## Load Order
- [ ] no tickboxes; Enable/Disable in the right-click menu
- [ ] moved rows show "old → new" position, columns kept even
- [ ] "Automatically sort for compatibility" left of Auto sort
- [ ] warning text: "For compatibility, N mod(s) will be automatically reordered by the base game on relaunch."
- [ ] a "!" with a tooltip on the Load Order button when that page has a warning

## Main list
- [ ] an error banner when there's a problem (e.g. a required mod is off)

## Side panel
- [ ] subtle darker background, no outline, a hover style on the clickable fields and the category picker
- [ ] version in grey right of the title (no "v")
- [ ] source line "<id> (<Workshop id>)" or "<id> (Local)"; Enabled/Disabled at the same size, grouped with it
- [ ] dates as "October 12, 2026"
- [ ] automatic tag pills next to the Tags label
- [ ] compatibility above dependencies, in that group
- [ ] thumbnail twice as big, a little further left

## Changes
- [ ] text left-justified, a little lower; the ↕ count to the right of "Changes"
- [ ] tooltip titled "Changes since launch"; it names the enabled/disabled mods when there are fewer than 20
- [ ] a mod's name in the changes list opens that mod; a Back button (where Close is) returns to the list; Back goes when another mod is picked or the page is left

## Other
- [ ] Ctrl+S saves the current profile
- [x] new mod popup: "New Mod Added" / "New mods were detected, and will require a restart to be enabled."; at most one per 30 s

## Backlog (round 8)
- [ ] tag suggestions: tags already used across mods in a dropdown under the tag box, filtered as you type, arrow keys to pick, comma accepts
- [ ] back/forward view history: previous active mod (opened in the panel), Load Order page changes; Vanilla View skipped
