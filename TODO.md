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
- [ ] moveable main menu button accessible with right click
- [ ] REMINDER: copy and design pass before publishing
- [ ] REMINDER: performance and code quality pass
- [ ] "Config" button next to mods with their own config page
- [ ] A settings page for Spire Mod Manager, don't know exactly what goes here yet. maybe profile management, and
- [ ] undo state specific to profiles
- [ ] save, discard button specific to profiles
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
- [ ] stylized Spire Mod Manager button (Jordan designs)
- [ ] export/import/share mod collections and configuration
- [ ] import BMM profiles from a settings page
- [ ] export as BMM csv (or save into BMM's folder)
- [ ] toggle between BMM and Spire Manager
- [ ] keyboard navigation: up/down, space to select
- [ ] multi-select and bulk actions in Category view
- [ ] profile and category dropdowns use a radio-style mark (single choice)
- [ ] auto update and polling
- [ ] link to the game's patch notes

# Round 3

Jordan's notes of 2026-10-11.

## Profiles

- [ ] short date in the edited timestamp
- [ ] dropdown: mod count and last updated next to each profile name
- [ ] Load Order page: drop the profile bar, OR give load order its own profile system; each main profile links to the last load-order profile (if it has no link yet); show the linked load profile in the main profile's details

## Changes

- [ ] tooltip lists the added/removed mods
- [ ] clicking Changes shows the full changes in the right-side panel

## Main list

- [ ] select/deselect all for the whole page and per category
- [ ] multi-select on the main page: move to a category, select/deselect
- [ ] drag and drop: whole categories, single mods, multi-selections
- [ ] author name on the row (right side, above the category button)
- [ ] category button shows the category's name
- [ ] colour codes for categories and/or tags
- [ ] "Needs: X": X green when enabled, red when not
- [ ] (fix) the changed (gold) title colour overrides custom colours
- [ ] (fix) thumbnail in the right-side panel still wrong

## Relaunch

- [ ] warning before relaunch when enabled mods miss dependencies or are out of order: list the problem mods; offer enable required mods (listed), disable the incompatible ones, relaunch anyway, close
- [ ] main menu: tooltip or small "requires restart" text by the pending dot

## Backlog (round 3)

- [ ] in-game error log: collect and show errors (ours and other mods')

# Round 4

Jordan's notes of 2026-10-11 (evening), in his order.

- [ ] (fix) "(off)" stays on a dependency that was just turned on (pending relaunch)
- [ ] "Required for N mods" on mods others depend on
- [ ] dimmer colour for "turned off, pending" vs "turned on, pending"
- [ ] confirm, cancel and clear buttons on text inputs (profile name/rename, tags, notes)
- [ ] Duplicate as a profile option
- [ ] Save as new and Duplicate open the name box with the original name plus " (1)" (or the next free number)
- [ ] renaming to a taken name warns
- [ ] (fix) rename box stays open when Delete is clicked mid-rename
- [ ] (fix) clicking the picker while its list is open closes it and opens it again
- [ ] no visible "Name (edited x)" profiles: an unsaved profile shows "Name *" and keeps its edit behind the scenes
- [ ] Save, Save as new and Discard ("Discard", not "Revert") beside the "Unsaved" label, which gets its own colour
- [ ] no pencils by Tags and Notes; at rest, subtle "Click to add tags" / "Click to add notes"
- [ ] placeholders without the dots ("Custom notes")
- [ ] Alias field (below Tags, above Notes) in the info panel
- [ ] Tags field right below the mod's details, with some margin; a bigger gap between Alias and Notes
- [ ] row right-click menu: Add/Edit alias, Add/Edit notes, Add/Edit tags, each turning the menu into an inline editor (Save / Cancel back to the menu)
- [ ] main menu button: Jordan's art (Pictures\symphony\design\spire manager button.png), our label, a hover look
- [ ] right-click the main menu button to move it (soon, no rush)
