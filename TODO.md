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
- [x] newly installed mods indicator (a "New" tag)
- [x] mod recently updated indicator (an "Updated" tag, Workshop updates in the last 7 days)
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

- [x] tag for mods that need multiplayer sync (everyone must have them): the Gameplay tag, its tooltip says so
- [ ] minimap scroll bar (like VS Code) marking notable items
- [x] banner when disabling a mod that enabled mods depend on (e.g. BaseLib): the main-list banner (round 8)
- [ ] view enabled only / disabled only (sticky items when toggling)
- [ ] view edited mods
- [x] enable all / disable all (per category, header right-click; whole page: see round 3)
- [x] filter by author (search covers authors)
- [x] stylized Spire Mod Manager button (Jordan designs)
- [ ] export/import/share mod collections and configuration
- [ ] import BMM profiles from a settings page
- [ ] export as BMM csv (or save into BMM's folder)
- [ ] toggle between BMM and Spire Manager
- [ ] keyboard navigation: up/down, space to select
- [ ] controller support (Jordan, 2026-10-13: backlog; 0.1.0 is mouse and keyboard only)
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

- [x] select/deselect all per category (header right-click: Enable all / Disable all)
- [x] select/deselect all for the whole page (Round 9: a bar between search and categories)
- [ ] multi-select on the main page: move to a category, select/deselect
- [ ] drag and drop: whole categories, single mods, multi-selections
- [x] author name on the row (Round 9: end of the tag line instead)
- [x] category button shows the category's name
- [ ] colour codes for categories and/or tags
- [x] "Needs: X": X green when enabled, red when not
- [x] (fix) the changed (gold) title colour overrides custom colours (Jordan: the Needs: names were all orange; fixed already)
- [x] (fix) thumbnail in the right-side panel still wrong

## Relaunch

- [x] warning before relaunch when enabled mods miss dependencies or are out of order: list the problem mods; offer enable required mods (listed), disable the incompatible ones, relaunch anyway, close (dependencies only: the game fixes the order itself)
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
- [x] click a tag to filter by it; Shift-click adds more tags (searches #tag)
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
- [x] all rows a little shorter
- [x] pills in the order Local, Library, Gameplay, Cosmetic, then custom; a "Local" tag for local mods
- [x] "Required for N" before "Needs: X" (both pages)
- [x] a "notepad" icon instead of ✎ for rows with an alias or notes; none in Load Order
- [x] a styled pencil button (right side of the row) opening the edit menu; none in Load Order
- [x] hover card for every mod: original name (id), enabled state, error, author, updated, custom tags and notes
- [x] (fix) the hover card sometimes stays open while using other parts of the page

- [x] no tickboxes; Enable/Disable in the right-click menu
- [x] moved rows show "old → new" position, columns kept even
- [x] "Automatically sort for compatibility" left of Auto sort
- [x] warning text: "For compatibility, N mod(s) will be automatically reordered by the base game on relaunch."
- [x] a "!" with a tooltip on the Load Order button when that page has a warning

## Main list
- [x] an error banner when there's a problem (e.g. a required mod is off)

- [x] subtle darker background, no outline, a hover style on the clickable fields and the category picker
- [x] version in grey right of the title (no "v")
- [x] source line "<id> (<Workshop id>)" or "<id> (Local)"; Enabled/Disabled at the same size, grouped with it
- [x] dates as "October 12, 2026"
- [x] automatic tag pills next to the Tags label
- [x] compatibility above dependencies, in that group
- [x] thumbnail twice as big, a little further left

- [x] text left-justified, a little lower; the ↕ count to the right of "Changes"
- [x] tooltip titled "Changes since launch"; it names the enabled/disabled mods when there are fewer than 20
- [x] a mod's name in the changes list opens that mod; a Back button (where Close is) returns to the list; Back goes when another mod is picked or the page is left

## Other
- [x] Ctrl+S saves the current profile
- [x] new mod popup: "New Mod Added" / "New mods were detected, and will require a restart to be enabled."; at most one per 30 s

## Added during round 8
- [x] (bug) a newly installed mod isn't tracked right in the changes (enabled/disabled); show "Installed X" as its own change, with its (pending) on/off state under it
- [x] the pencil button subtle: no background, no outline
- [x] (fix) the category and pencil buttons don't line up vertically with the tickbox

## Backlog (round 8)
- [ ] profiles and mods installed mid-session: they aren't in the profile until a relaunch (switching profiles leaves them as they are)
- [ ] tag suggestions: tags already used across mods in a dropdown under the tag box, filtered as you type, arrow keys to pick, comma accepts
- [ ] back/forward view history: previous active mod (opened in the panel), Load Order page changes; Vanilla View skipped

# Round 9

Jordan's notes of 2026-10-12 (late evening).

- [x] install the latest (game closed)
- [x] version 0.0.N, up by one per install (now 0.0.40); proper semver at release
- [x] whole-page controls in a new bar under the search box, above the categories (Enable all / Disable all)
- [x] category Enable all / Disable all inline, right of the rename/move buttons
- [x] author on the row, somewhere other than above the Category button (end of the tag line, dim)
- [x] then the relaunch check (round 3 note)

# Round 10

Jordan's notes of 2026-10-12 (night), from the details page screenshot.

## Side panel
- [x] Enabled status sits a little low: centre it on the source link's line
- [x] thumbnail a little further right, the picture right-justified in its box
- [x] Size moves to the top of the Published group; Compatibility goes under Updated (where Size was)
- [x] alias placeholder: "Your nickname for the mod"
- [x] clicking a mod name in the panel: the row gets the "selected" look (blue, like hover / keyboard) when it scrolls into view, without becoming the panel's mod
- [x] hovering a dependency's name highlights its row (only if in view; no scrolling)
- [x] mod names in the panel: the mod's card after 0.5 s
- [x] author clickable to search (Shift: add to the search; a space only when there's a search already); tags the same
- [x] the panel's tags: same tooltip as the rows'

## Rows
- [x] smaller notepad, right after the alias/title, before the original name
- [x] the hover card only over the mod's title, after 0.5 s
- [x] hovering the notepad: the card after 0.1 s
- [x] grace period: after a card closes, for 2x its delay, hovering anything with a card shows it at once
- [x] a little more room between the title and the tags
- [x] recently updated: a red pip left of the name instead of the Updated tag

## Tooltips
- [x] tags use the styled card; Gameplay: "This mod affects gameplay. All players will need it installed for multiplayer."; then, after a gap, the search hint at the bottom in a subtle subheading font

## Patch notes
- [x] "Recently updated" link on the details page (after the status) opens the mod's patch notes (when there are any): id, author, update date, the notes

## Tests
- [x] a mod with very long details (title, description, tags, notes...) as an ongoing screenshot of the edge case; not solved yet

## Added during round 10
- [x] search placeholder: just "Search"
- [x] main menu move hint: "Click to place · right-click or Esc to cancel"
- [x] Move button: the cursor jumps to the button's left end, halfway up

## Backlog (round 10)
- [ ] long titles, descriptions, tags and other text: how they should fit
- [x] hover the thumbnail for 0.2 s: the full image (at most 800 wide or high)

# Round 11 (after round 10)

Jordan's note of 2026-10-12 (night).

- [x] bulk editing on the main list: select mods (e.g. all of Uncategorized, Ctrl-click to drop one), right-click, change their category; the edit menu has the same controls minus Alias, which becomes "Editing N selected mods"

# Round 12

Jordan's notes of 2026-10-12 (night).

- [x] the "Updated on" card wraps too early: 120 px wider
- [x] a slight radius (about 2 px) on the row's focus/active look
- [x] tags down another 2 px
- [x] recently updated: a gem instead of the round pip, a pixel further left; it goes once the mod is opened (the panel's "Recently updated" stays)
- [x] author on the row: no "by", not clickable (quick search only from the panel, which gets the search hint); author text cut after 200 characters
- [x] network: keep requests few and safe; the pip only from data we already have; patch notes fetched only for the mod opened
- [x] Workshop tags searchable as plain text (click them in the panel)
- [x] tags, "Needs / Required for" and the row's author a size smaller
- [x] side panel: "Needed by" reads "Required for"
- [x] tag pills: 2 px corners, no outline (the player's own fainter)
- [x] one search hint for every click-to-search: "Click: search this term; Shift-click: add to search"

## Backlog (round 12)
- [ ] delete / uninstall a mod from the manager
- [ ] a dependency that isn't installed: click it to open its Workshop page (needs a way from a mod id to its Workshop id; not every mod is on the Workshop, then plain text with "(not installed)"); spike first

# Round 13

Jordan's notes of 2026-10-12 (night).

- [x] one tooltip manager with the rules in one place: 400 ms delay everywhere, a 250 ms grace period after a card closes; the notepad's card instant (no delay, no grace: shows and hides with the mouse)
- [x] one card at a time: a newer card closes the one before
- [x] a 20 ms wind-down after leaving a trigger before the card goes

## Backlog (round 13)
- [ ] a "Spire Mod Manager" mark on vanilla-looking popups of ours (delete profile, other destructive confirmations)

# Round 14

Jordan's notes of 2026-10-12 (late night).

- [x] install the latest (0.0.43)
- [x] no "recently updated" mark on rows
- [x] side panel: the patch notes link moves to the end of the Updated line, after "(N days ago)": "Patch notes" in blue
- [x] head order: Author, Compatibility, Updated
- [x] the notepad to the left of the mod's name (Jordan's note was cut off after "if the original": ask)

## Backlog (round 14)
- [ ] click-to-search toggles: clicking a term that's the whole search clears it; with another search, a click replaces it (a second click removes it); Shift-click adds the term, or removes it if it's there already (tidying spaces), keeping the rest

# Round 15

Jordan's notes of 2026-10-12 (late night), with screenshots.

## Info panel
- [x] patch notes: "Couldn't load" in the real game: drop the patch notes page (no more requests); "Patch notes" opens the mod's change notes on the Workshop instead
- [x] head order back to Author, Updated, Compatibility
- [x] "(N days ago)": green under 48 hours; "1 day ago"; under 24 hours "N hour(s) ago"
- [x] status still ~1 px lower than the source link
- [x] source text: "<id> (SteamID <workshop id>)"
- [x] alias: "Add alias" at rest, "An alternative title for the mod" while typing
- [x] notes (value and editor) 2 pt smaller

## Rows
- [x] notepad even smaller, 1 px up and 1 px left
- [x] only the main title (alias) opens the card, not the original name beside it
- [x] the pencil to the right of the tickbox
- [x] the title is clipped by the tag line: taller rows with less space between them (same pitch)
- [x] highlights: hover dimmer, selected (the panel's mod) more visible / sunken; hover, focus, selected and picked the same size, 2 px wider each side, 4 px corners

## Tooltips and text
- [x] grace period also between kinds of card (title card -> tag card)
- [x] long titles, authors, tags (and similar): cut with "…" (by the room there when possible); the full text in a native tooltip after 1 s; inside our cards a "Hold Alt to see full text" hint, Alt shows it all in a bigger card
- [x] the mod card: half a line before and after the alias

## Other
- [x] bug: a search can say "Search matches (2)" while the list shows nothing
- [x] "All mods (n)" -> "Installed mods (n)"
- [x] Gameplay tag: "...will need it enabled for multiplayer."
- [x] Library tag: "This is a framework intended to be used by other mods; often has no effect when installed alone"
- [ ] (backlog, round 15) rework the "requires relaunch" messaging: our own warning instead of the vanilla one, on our page only; a quick relaunch from the main menu or the profile switcher; spike first

## Backlog (round 15)
- [ ] long text, still: the info panel's title (shrinks, version cut) and source line (runs off the panel); a row with many tags (the line clips at its edge, the author can be cut off)

# Round 16: release (from RELEASE-PLAN.md)

Blockers, in order (details and fixes in RELEASE-PLAN.md §2):
- [ ] branch targeting: beta-only (min_game_version 0.111.0; minBranch public-beta on every upload), "(Beta)" in the Workshop title (Jordan: beta first)
- [ ] version 0.1.0 (Jordan, 2026-10-13) + manifest description; release mode in build.cs (no bump); main branch + v0.1.0 tag
- [ ] build.cs --dist: only the dll, json and pck
- [ ] fail soft on game updates: patch each class on its own, log what failed, keep the vanilla page with a notice; RowState lazy and null-safe; wrap the four unguarded patch bodies
- [x] screen open with many mods: ModToggledPatch waits for our screen (was a full refresh per row)
- [x] Load Order handlers connected once per row (+ self-test)
- [ ] Store: backup step on its own; an unreadable manager.json moved aside, the newest backup loaded, a notice; null collections
- [ ] copy pass (relaunch wording, one name, Vanilla List/View, enabled/disabled, tooltips, casing, profile rename in Changes)
- [ ] first run: fixtures with no manager.json, and with BMM data (BMM on / off); an empty-state hint
- [ ] uninstall / disable checked and documented
- [ ] Workshop listing: image, description, tags, change note, dependencies []
- [ ] a private Workshop upload tested end to end

# Round 17

Jordan's note of 2026-10-12 (late night).

- [x] Better Mod Menu: no more stepping aside. Both can be on: our views as usual, and Better Mod Menu's page stands in for the vanilla page (it replaces the vanilla page anyway); a warning that running both may behave unexpectedly

# Round 18

Jordan's answers and note of 2026-10-13 (decisions in RELEASE-PLAN.md §7). Unattended night: work through the
backlog by judgement, Jordan reviews in the morning.

- [x] decisions recorded (0.1.0, beta first, new mods join the profile enabled, open source later, real screenshots, private upload first, controller in the backlog)
- [x] the working state on `dev` tagged as the MVP (tag `mvp`)
- [x] a private GitHub repo (jciurc/spire-mod-manager); work on branches, pull requests into `dev`; CI in #8
- [ ] screenshots for the Workshop previews: a shot list for your real game in #4 (`workshop/SHOTS.md`); the dev sandbox can't take them (no Steam, other mods off, the DEV tag)
- [ ] new mods join the active profile as enabled (#7)
- [x] then the release blockers and backlog, picked by judgement: the pull requests below

Pull requests from the night (each built and run through the in-game self-test; the fixture, `--bmm` and the
relevant extra modes pass on each. All of them merged together pass too, on branch `night-integration`: fixture
299, `--bmm` 336, `--bmm-after --bmm-local` 336, `--first-run` 11, `--first-run-bmm` 14 and 13 with `--bmm`, `--real` 8):

| PR | branch | for |
| --- | --- | --- |
| #1 | bmm-warning-near-button | your note: Vanilla List name kept, the warning beside the view buttons |
| #2 | first-run | release blocker 9: first run tested on both paths; the empty-state hint |
| #3 | release-prep | blockers 2-3: `build.cs --dist` (0.1.0), manifest description |
| #4 | workshop-listing | blockers 11-12: description, workshop.json, shot list, publishing steps |
| #5 | fail-soft | blocker 4: per-class patching, the game's page with a needs-update line |
| #6 | store-safety | blocker 7: recover an unreadable manager.json, one-time notice |
| #7 | new-mods-profile | your decision: new mods join the active profile; backlog r8 (mid-session mods) |
| #8 | ci | CI: offline checks on GitHub; the in-game self-test on a runner on your PC |
| #9 | search-toggles | backlog r14: click-to-search toggles |
| #10 | popup-mark | backlog r13: "Spire Mod Manager" on our game-style popups |
| #11 | long-text | backlog r15: long titles, ids and tag lines |
| #12 | resource-fallbacks | release plan §3: fallbacks for missing game resources |
| #13 | dev-tools-bmm-local | dev.cs `--bmm-local` from worktrees |
| #14 | installed-runtime | bug: mods installed while the game runs were never tracked in real play |
| #15 | perf | release plan §3: redraw and search cost with long lists |
| #16 | bmm-import | Better Mod Menu import: json5/jsonc, comments, its old format, portable mode |
| #17 | tag-suggestions | backlog r8: tag suggestions |
| #18 | spikes | backlog r12 and r15 spikes: relaunch messaging; a missing dependency's Workshop page |
| #19 | copy-pass | blocker 8: copy pass; renamed profiles read as renamed |
| #20 | code-quality | TODO reminder and release plan §3: code-quality pass |
| #21 | loc-format | release plan §3: dates and numbers in the game's language |
| #22 | undo-restart-compat | release plan §3: Undo And Restart's panel over our fields |

Merge order close to the one `night-integration` used (conflicts were small, mostly SelfTest.cs and the eng text
file; perf with long-text and copy-pass with loc-format each needed a one-line fix, now on those branches):
#8, #3, #4, #13, #1, #5, #6, #7, #2, #14, #16, #9, #10, #11, #12, #15, #17, #19, #20, #21, #22, #18.

Found tonight, not done yet:
- [ ] try #14 with a real Workshop subscription while the game runs (dev runs have no Steam)
- [ ] try #22 with Undo And Restart (UI Modified) in your game
- [ ] the in-game self-test on PRs: register a self-hosted runner and set SELF_HOSTED_STS2 (README "CI", #8)
- [ ] the profile bar's background is 0 px tall (its anchors are set without offsets); decide whether it should show (#12)
- [ ] redraw with ~250 mods is about 49 ms after #15; the rest is Godot-side per row (titles, fitting)
- [ ] backups are picked by name (local time); a clock change can put them out of order (#6)
- [ ] a broken manager.json with no readable backup doesn't retry the Better Mod Menu import (#6)
- [ ] the relaunch messaging and missing-dependency spikes (#18): your answers, then their first PRs
- [ ] the profile list's "Oct 11" still follows Windows' language, not the game's (#21 notes the one-liner)
- [ ] a profile renamed and saved in the same session still reads as added + deleted in Changes (needs a stable profile id)
- [ ] the Workshop description quotes labels a copy pass changes; re-read it before uploading (#4, #19)
