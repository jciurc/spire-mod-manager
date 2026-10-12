# What's new

Short notes for Jordan: what to look for after each install. Newest first.

## Not installed yet

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
