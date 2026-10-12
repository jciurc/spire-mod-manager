# What's new

Short notes for Jordan: what to look for after each install. Newest first.

## Not installed yet

- (nothing yet)

## Installed 2026-10-12 (evening): version 0.0.40

Version
- The mod now reads **0.0.40** (title in the info panel, game's mod list). The last number goes up by one with every install, so you can tell when there's a new build.

Rows
- A little shorter. Tags always in the order Local (new: hand-installed mods), Library, Gameplay, Cosmetic, then yours.
- "Required for N" comes before "Needs:".
- A small notepad after the name marks your alias or notes (main list only).
- A quiet pencil (no box) and the Category button, on the right, in line with the tickbox. The pencil opens the edit menu.
- Hover any row for a second: a card with name (id), on/off, problems, author, updated date, alias, tags and notes. Cards close as soon as the mouse leaves.

Load Order
- No tickboxes. Right-click a mod to **Enable / Disable** it (above the moves).
- A moved mod shows where it was at launch, dimmed: "6 → 3". The numbers stay in even columns.
- "Automatically sort for compatibility" sits beside Auto sort.
- The warning reads "For compatibility, N mods will be automatically reordered by the base game on relaunch."
- When the order has that problem, a red **!** on the Load Order button (main list) shows the warning on hover.

Main list
- A red banner when enabled mods need a mod that is off or not installed. **Show** steps through those mods. **Enable needed mods** turns on the ones that are installed, as one undo step.

Info panel
- Title with the version in grey beside it (no "v").
- Under it: "SpireModManager (3456789012)" (links to the Workshop page) or "SpireModManager (Local)", then **Enabled / Disabled / Pending restart** at the same size.
- Author, Updated (with "N days ago") and Size beside a thumbnail twice as big, a little further from the edge. A mod without a picture: the description moves up.
- Dates read "October 12, 2026". Compatibility sits with Dependencies / Needed by at the bottom of the details.
- Alias, Category, Tags and Notes sit in soft dark boxes: lighter under the mouse, darker while typing, no outlines. The automatic tag pills are on the Tags line, beside the word.

Changes
- Left-aligned and a little lower: "Mods enabled 5 (+1 -0)" over "Changes 2  ↕3".
- The hover card is titled "Changes since launch". Under fewer than 20 mods turned on, off or installed, it lists them by name.
- In the full list, click a mod's name to open it (the list says so). **Back** (top right) returns to the list. It goes away when you pick another mod or leave the page.
- Mods installed while the game runs get their own "Installed" section, with whether each will load after relaunch. Unticking one now sticks (before, the game ignored it and loaded the mod anyway). They also count as pending.

Other
- **Ctrl+S** saves the profile (the Save button's tooltip says so).
- Redraws are about a third faster on your full list.
- New-mod popup: "New Mod Added" / "New mods were detected, and will require a restart to be enabled."; at most one every 30 seconds.

## Installed 2026-10-12

Latest
- Panel fields in the order Alias, Category, Tags, Notes (the right-click menu: Alias, Tags, Notes).
- "Changes N" no longer counts load-order moves (they're the ↕ on the line above).
- The profile bar shows how many mods are on, beside the profile's name.
- Mods you subscribe to while the page is open show up in our row style, in their category, right away.

Fixes
- Main menu **Move button** works: the button moves by as much as the mouse does (no jump), a click puts it down, right-click or Esc cancels, and the hint goes away (also when you leave the menu).

Rows
- No pencil; a faint ✎ after the name marks mods with your alias, notes or tags.
- Hover a row for a second: a card with your notes (and the full name if it was cut short).
- "Required for N enabled mods". Tag tooltips; a purple **Cosmetic** tag when the Workshop tags say skins/visuals/UI/textures.
- Category headers: the mod count right after the name.

Right-click a mod (or its Category button)
- Two columns: categories on the left, the mod's **Alias, Notes, Tags** on the right, edited right there.
- Right-clicking elsewhere closes it and right-clicks what's under the mouse; clicking the same button again closes it; Esc closes it.
- New category scrolls to its name box.

Info panel
- Name; "SpireModManager v0.1.0 (Workshop …)" as the link; **Enabled / Disabled / Pending restart**; problems; description; author, dates, size, subscribers, compatibility (each branch once), Workshop tags; dependencies.
- Click the name to find the mod in the list; click a dependency to scroll to it with a short glow.
- Fields: **Alias, Category** (a picker), **Notes, Tags** (automatic tags as pills above yours). "Add alias" / "Add notes" / "Add tags" at rest; padded boxes with an inline ✕; Save and Cancel while editing; notes trimmed.

Changes
- Two lines by Undo: "Mods enabled 23 (+1 -3) ↕2" and "Changes 5" (moves, edits, profile changes). Undo buttons now centred on the row.
- Hover shows each kind, edits by field (alias, notes…). Click opens the full list (edits list the new alias); click again closes it.
- Undo, Redo and Revert all no longer open the list or empty the panel; with the list open, ticking a mod keeps it open.

Profiles
- Unsaved profiles have a red * in the list; hover "Unsaved" to see that profile's own changes (+ / −).
- Name boxes (profiles, category rename) have an inline ✕ instead of a Clear button.

Search
- Any case, and spaces ignored ("mod fixes" finds "ModFixes"); after each change the list starts at the top.

"Main Menu" sits a little higher.

## Installed 2026-10-11, evening

Main menu
- Your skyline button, with our label; it brightens on hover like the game's buttons.
- Right-click it: **Move button** (it follows the mouse, click to place, right-click or Esc to cancel) or **Reset position**.
- While a relaunch is pending: the dot plus a short coral "Relaunch to apply your mod changes".

Main list
- Rows: libraries say "Required for 3 mods"; "Needs: X" is green when X is on, red when it's off.
- Turning a mod on or off updates the rows' notes and the info panel at once (no leftover "(off)").
- Mods turned on since launch: bright gold title; turned off: a dimmer, dusky gold.
- Right-click a mod: **Add/Edit alias, notes, tags** right in the menu (Save or Cancel bring the menu back), then its category (a dot marks the current one).
- The alias pencil stays visible while you hover it; the inline alias box no longer covers the tags.
- Renaming a category to a taken name warns and isn't accepted.

Profiles
- "Unsaved •" (coral) with **Save / Save as new / Discard** beside it; **New / Duplicate / Rename / Delete** on the right.
- New, Duplicate, Save as new and Rename open a name box with Save / Cancel / Clear; copies are offered as "Name (1)", "(2)"...
- Empty or taken names warn and can't be saved; Delete can't be clicked mid-rename (the other buttons hide).
- Leaving a profile with unsaved changes keeps them with it: "Name *" in the list, and they're back when you return.
- The list opens under the picker with each profile's mod count and save date; clicking the picker again closes it.

Info panel
- **Tags, Alias, Notes** right under the mod's details. At rest they read like text (or a subtle "Click to add tags"); clicking one shows Save / Cancel / Clear. Enter saves (Ctrl+Enter for notes), Esc cancels.
- Workshop branch ranges read "up to beta" / "stable and later".

Changes, undo, revert
- "Changes +0 -3 ↕2" now sits left of Undo; ↕ (blue) counts mods moved in the load order.
- Hover: a summary (switched profile, Enabled / Disabled / Load order counts, edits). Click: the full list in the right panel (each mod, each move with old and new place, edited mods, profiles added or removed).
- Undo, Redo and Revert all also open that list (Undo no longer empties the panel).
- Undo and Revert all now cover the same span (since you opened the page), so they agree.
- The undo buttons sit a little higher.

Load Order
- A press starts the drag at once (dimmed list, drop line); a click without moving just selects.
- Hover lines follow when you scroll with the wheel.
- No profile bar and no Vanilla List button; Return sits in the corner.
- "Main Menu" over the back arrow (also on the main list).

Fixes found in the audit
- With Better Mod Menu on, a tickbox change no longer adds our tags and Category buttons to its rows.
- The info panel refills when Workshop details arrive after the page opened.
- Clearing everything on a mod (or deleting its category) no longer counts as an edit.
- The panel no longer writes stale text over an alias set from the row's pencil.

## Installed 2026-10-11

- Thumbnail in the info panel stays small (the game's 500x380 minimum size was overriding ours).
- Profile bar: plain name with "Unsaved •" beside it; buttons on the right.
- Leaving the Modding page from Load Order reopens it on the main list.

# For your review

Choices I made on my own; say if any should change.

- **New** makes a blank profile (only Spire Mod Manager on), since Duplicate copies the current one.
- **Clicking away from a name box cancels** (profiles; categories keep the old name if the new one is taken). Tags, alias and notes save when you click away, so typed text is never lost.
- **"Unsaved •" is coral**, different from the gold names and the red warnings.
- **Old "Name (edited 14:19)" profiles** from the previous build are ordinary profiles now; delete any you don't want.
- **Profile list details**: "5 mods · Oct 11" counts mods that are on (not counting this one); profiles from before this build show no date until saved once.
- **Load-order count** uses "↕2" rather than "+2", so it doesn't read as mods added.
- **Hints without dots**: "Click to add tags", "Custom notes".
- **The changes list** only exists on the main list (Load Order has no right panel); there, hover the count for the summary.
- **Revert all and Undo** also undo profile creation and deletion made during the visit (they restore all our data as it was when you opened the page).
- **Load Order profiles**: I took your first option and removed the profile bar there; separate load-order profiles are in the backlog with load order presets.
- **Main menu button art** is used only on the main menu; the corner button on our pages keeps the game's art.
- **Notes inside the right-click menu** use a 4-line box in the menu, the same as tags and alias.
- **The ✎ mark** on rows means "has your alias, notes or tags"; it isn't clickable (alias editing is in the menu and the panel now).
- **"Mods enabled"** counts mods that are on, not counting Spire Mod Manager itself.
- **Dependencies, clicked**, scroll the list and glow the row but don't change the panel; the panel's name, clicked, just scrolls.
- **Load Order "was" column**: launch positions count only mods installed now, so a mod added since launch shows no "was".
- **The main-list banner** covers dependency problems only (off or missing). Load errors stay in the panel; say if you want them in the banner too.
- **The pencil** is the plain ✎ glyph, faint until hovered (then gold), with no box.
- **Panel head**: the space beside the bigger thumbnail holds Author, Updated and Size. Published, Subscribers and Workshop tags stay in the details below.
- **Workshop number** shows bare, "(3456789012)", per your note; it's still the link to the page.
- **Field boxes** are always faintly visible now, so the fields read as clickable. The category picker matches.
- **"Mods enabled N"** includes mods installed this session that are on, since N is what will load after a relaunch. The (+ -) counts only cover mods that were here at launch.
- **Back from a mod opened via Changes** sits top right. When the mod has a picture, the picture moves down to make room.
- **The changes list** says "Click a mod to open it." under its title.
