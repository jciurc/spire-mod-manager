# TODO

Jordan's notes of 2026-10-07 (from `spire manager notes.txt` in the scratch folder), ticked off as they land.

## Vanilla View

- [x] "Category View" -> "Spire Manager" on vanilla page only
- [x] move "Spire Manager" button to bottom left corner, same positioning/padding as main menu button
- [x] display changes indicator on button
- [x] remove Load Order button
- [x] remove Relaunch Game button
- [ ] persist and scroll to selected item when switching views, (auto expand category, load order page included)


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
- [ ] clicking/holding drag handle should also select the item
- [ ] remove Auto sort explanation <- we'll reintroduce it later
- [ ] change incompatibility warning to "X mods will be automatically reordered (vanilla behavior) on launch based on dependencies" [Fix now]
- [ ] let's remove the drag prevention when dragging into incompatible order. we'll just display the warning and let the user handle it
- [x] update "Needs X (loaded before), Y" -> "Needs: X before, Y" and color code before/after
- [ ] color code connectors
- [ ] remember last selected item when going back to category view
- [ ] multi selectable/draggable items with shift/ctrl/cmd
- [ ] (fix) load order view ui/state is not cleared when changing back to category
- [ ] (fix) container doesn't scroll when dragging near bounds
- [ ] (fix) remove needs connector when on category view


## Changes

- [ ] Move button to above details panel left aligned
- [ ] Replace button with tooltip
- [ ] Show diff count on button, "Changes +4-3"
- [ ] Changes overlay is anchored to Changes text
- [ ] Turned On/Off -> Enabled/Disabled
- [ ] soft/pastel color coding in overlay, green=add, red=remove, mod name colored by tag(or category?)
- [ ] move revert button next to undo/redo
- [ ] include internal changes within overlay, x items edited (category/tag/details change), x profile changes
- [ ] show "Switched from X profile to Y" in changelist instead of all mod changes, (include changes that differ from selected profile. diff count will still show total mod additions/removals)


## undo

- [ ] show hotkeys near buttons
- [ ] show history position and count near buttons
- [ ] load order and list view should have separate history states
- [ ] undo history notes mod changes and load order changes separately


## tags

- [ ] indicator for system tag vs custom tag <- something subtle, not sure what
- [ ] let's do market research on additional tag features


## details panel

- [ ] reduce screenshot size to thumbnail
- [ ] move details closer to title
- [ ] remove Notes button for editable inline textarea
- [ ] editable tag field in panel
- [ ] Alias will be inline editable in the list - needs design thought here - small pencil to left of title?
- [ ] replace workshop page button with full clickable url positioned under mod title
- [ ] local mods can show "Local mod" text instead of url
- [ ] remove "From" item
- [ ] show compatibility game version numbers, if available


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
- [ ] (fix) relaunch button briefly loads new game instance in lower resolution windowed mode instead of fullscreen (borderless)
