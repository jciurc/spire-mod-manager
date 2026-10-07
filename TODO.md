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

- [ ] (fix) drag is choppy and bounces at the very top and bottom of the list
- [ ] move Spire Manager button (main menu and mod menu) slightly down to line up with Vanilla / Load Order buttons
- [ ] Ctrl+Z / Ctrl+Y shown under their buttons, formatted "Ctrl + Z"
- [ ] remove history counter
- [ ] move Changes diff to the right of the "Spire Mod Manager" title
- [ ] equal x/y padding in the profile dropdown
- [ ] shift content panels up: equal margin below header and above footer buttons
- [ ] remove dependency connectors in Category view
- [ ] narrower list, wider details
- [ ] "Back" -> "Return"
- [ ] "Vanilla Manager" is cramped: "Vanilla List"
- [ ] Vanilla button in the Spire Manager button's spot (lower left); Load Order shifts over
- [ ] Vanilla button on the Load Order page too (same spot)
- [ ] Spire Mod Manager title and Changes diff in the same place on both views
- [ ] "Load Order" title centred in the header on the Load Order page
- [ ] remove the restart warning on the Load Order page
- [ ] remove Relaunch on the Load Order page
- [ ] Auto sort to the right of the search bar; less whitespace around the order warning
- [ ] vanilla version/date watermark legible on our page (light text)
- [ ] clicking anywhere outside the current input (header, details, footer, blank space) drops focus

## Details panel

- [ ] add "Dependencies: x, y"
- [ ] thumbnail is not small enough
- [ ] link text: "Source: Workshop ID ## (clickable)" / "Source: Local mod"
- [ ] terms line up with the panel header
- [ ] order: Author, description, Version, Game version, then the rest
- [ ] "Game version" -> "Compatibility: x.y.z or newer" (+ stable/beta/current if it applies)
- [ ] error indicator and message for incompatible mods (version, conflict, dependency, corrupt...)
- [ ] no system tags in details
- [ ] tags and notes fields look like the other terms while inactive
- [ ] pencil right of the Tags and Notes terms
- [ ] tag field: one line of space; notes: four lines
- [ ] tag placeholder only while active: "Custom tags separated by commas.."
- [ ] notes placeholder: "Custom notes.."

## Tags

- [ ] remove the "Visual only" tag

## Rows

- [ ] long titles truncated, with tooltip
- [ ] inline name editing shows the original name behind the field (fix)
- [ ] alias pencil only on row hover
- [ ] a bit more space between pencil and name
- [ ] clicking the pencil again closes (and accepts) the edit
- [ ] clear button on the inline editor

## Profiles

- [ ] Save and Revert next to Profile
- [ ] unsaved changes: "Profile: [dropdown]  Unsaved • [Save] [Save as new] [Discard]"
- [ ] Save as new replaces New, Rename and Delete
- [ ] unsaved profile kept as its own entry "Name (edited [time])" until saved or discarded; nothing destructive or unrevertable (UX to be worked out)

## Load Order (play feedback)

- [ ] dependency connectors stretch and shrink while dragging
- [ ] mod names in "Needs:" a different colour from "before/after"
- [ ] no inline name editing on the Load Order page
- [ ] whole row draggable with the move cursor; the right-side actions keep the normal cursor
- [ ] tickboxes disabled on the Load Order page (ordering and profiles only)
- [ ] disabled mods dimmer on the Load Order page
- [ ] order warning sticky at the top

## Category view (play feedback)

- [ ] Revert all goes back to the state when the Modding page was opened (the profile, or the profile *edited), not launch
- [ ] changed mods (since launch) shown in a different colour

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
