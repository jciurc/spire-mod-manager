# Spike: relaunch messaging

Status: proposal, 2026-10-13. No code changed.

## The problem

Jordan (backlog, round 15): "rework 'requires relaunch' messaging: our own warning instead of the vanilla one,
on our page only; a quick relaunch option on the main menu, or in the profile switcher; spike first".

Today our page shows the game's own line, "Disabling/Enabling mods will only take effect after a restart."
It says "restart" while our button says "Relaunch". It follows the game's rule, which misses some of what we
count as pending. And to relaunch, you have to open the Modding page first.

## What happens today

### The game

From the installed 0.111 beta's `sts2.dll`, decompiled with ilspycmd:

- **The line:** `%PendingChangesLabel` on `NModdingScreen`, text key `MODDING_SCREEN.PENDING_CHANGES_WARNING`.
- **The rule** (`NModdingScreen.OnModEnabledOrDisabled`): the line shows when a mod in `ModManager.Mods` loaded
  this session but is now off in the saved list, or was `Disabled` and is now on. Mods that are `Failed`,
  `DisabledDuplicate` or `AddedAtRuntime` never count, and neither does load order.
- **When it is checked:** on every tickbox change, and in `OnNewModDetected`. That runs once per row while
  the page builds, and again for each mod installed while the page is open.
- **It starts hidden:** `_Ready` ends with `_pendingChangesWarning.Visible = false`, after the rows are added.
  So a page built while something is already pending shows no line until the next tickbox change.
  - The page is built once per main menu (`NMainMenuSubmenuStack` keeps `_moddingScreen`).
  - So this happens whenever the main menu is rebuilt, for example after a run. Our main menu note then says
    "Relaunch to apply your mod changes" while the page itself says nothing.
- **Mods installed while the game runs:** only Steam's `ItemInstalled_t` callback reports them
  (`ModManager.OnSteamWorkshopItemInstalled`).
  - `ReadModsInDirRecursive` adds the new mod to `ModManager.Mods` first. Then it is marked
    `AddedAtRuntime` and announced through `OnModDetected`.
  - A folder dropped into `mods\` is not noticed until the next launch.

### Our mod

- **Relaunch Game button:** main list only, bottom right (`Patches.cs:58-63`, placed by `Layout.cs:145-151`).
  - It runs `RelaunchCheck.Relaunch` (`RelaunchCheck.cs:20-26`). That relaunches at once, or first lists the
    enabled mods that can't load.
  - The check needs the Modding page: `Show` sizes itself from it, and fixes redraw its list
    (`RelaunchCheck.cs:37-47`, `RelaunchCheck.cs:94-99`).
  - The restart itself (`ModEntry.Relaunch`, `ModEntry.cs:64-76`) needs no page.
- **Our pending rule:** `ModEntry.HasPendingChanges` (`ModEntry.cs:113-124`) is the game's rule plus two more
  cases:
  - a changed load order (`LoadOrder.OrderChanged`, `LoadOrder.cs:33-38`);
  - mods installed this session that are on (`Installed.Mods`, `Installed.cs:35-36`).
- **The game's line on our page:** `Layout.cs:176-180` moves it between the left and right buttons on the
  main list. In Load Order it is made transparent (Jordan's round 6 call: no restart line and no Relaunch
  there).
  - `ModToggledPatch` forces the line on for our two extra cases (`Patches.cs:165-166`).
  - Redraws and profile switches ask the game to check again (`CategoryList.cs:243-252`, `Profiles.cs:84`,
    `Profiles.cs:117`, `Profiles.cs:145`).
- **Dots:**
  - Main menu: the game's dot plus a coral "Relaunch to apply your mod changes" note, refreshed when the menu
    stack changes (`MenuButton.cs:25-44`).
  - Vanilla View: a dot on our toggle button (`Layout.cs:297-302`).
- **Main menu right-click menu:** Move button, Reset position (`MenuButton.cs:46-60`).
- **Profile switch:** `Profiles.SwitchTo` (`Profiles.cs:77-86`) writes the mod list and asks the game to
  check again. Switching needs the page: `Profiles.Apply` ticks its tickboxes (`Profiles.cs:199-222`).
- **Words in use today:**
  - "Relaunch Game" (`settings_ui.json:3`);
  - "Pending restart" (`:86`);
  - "Enabled, loads after relaunch" (`:150`);
  - "...will require a restart to be enabled." (`:160`);
  - "Relaunch to apply your mod changes" (`:166`);
  - the game's "...after a restart."

### Found while reading: new mods are never tracked in real play

`Installed.Detected` (`Installed.cs:21-30`) drops a mod when `ModManager.Mods` already has one with the same
id and source. The check is meant to catch "an update of a mod that was here at launch". But the game adds a
new mod to `ModManager.Mods` *before* announcing it, so the check finds the new mod itself and drops every
one.

- **Effects in real play** (from the decompiled code, not yet seen in game):
  - no New tag and no "Installed" section in Changes;
  - a new mod that is on doesn't count as pending (no dot, no note, no line).
- **Why the tests pass:** the self-test announces a made-up mod that was never added to `ModManager.Mods`
  (`SelfTest.cs:923-930`).
- **Proposed fix: PR #14** (open, not merged). Only another copy that was here at launch counts as an
  update: not the mod itself, and not `AddedAtRuntime`. Skipping only the mod itself isn't enough: another
  copy added this session, with the same id and source, would still drop it. Its self-test adds the mod to
  the game's list first, the way the game does, and fails two checks with the old code.
- **Status:** still to verify with a real subscription while the game runs. The dev sandbox has no Steam.

## What "pending" should mean

"Pending" means a relaunch would change what you play with. One rule, used by the page line, the main menu
note, every dot and the hover card (option 3 below).

| Change since launch | Counts today | Proposal |
|---|---|---|
| A loaded mod turned off, or a disabled one turned on | yes (the game's rule) | yes |
| A mod installed this session that is on | meant to (see the finding above) | yes |
| A mod installed this session that is off | no | no |
| Load order: enabled mods in a new order | yes | yes |
| Load order: only disabled mods moved, or a move the game undoes anyway (a mod above what it needs) | yes (any change to the saved list) | **no**: compare the order enabled mods would load in (saved order, then the game's dependency sort, `Dependencies.Fixed`) with the order they loaded in this session |
| A failed mod turned off | no | no (it only clears its error) |
| A Workshop update downloaded while the game runs | no | ask Jordan: "1 mod updated" is true, but Steam doesn't always download updates mid-session (unverified) |
| A profile switch that leaves the same mods on | no | no |
| Aliases, notes, tags, categories, saving a profile | no | no |

The stricter load-order rule also covers entries the game or we add to the saved list for new mods
(`NewModTogglePatch`, `Profiles.Apply`). Today those grow the list, and any longer list counts as "order
changed".

## Options

Two separate choices: the message on our page, and where a quick relaunch lives.

### The message on our page

1. **Reword the game's line through our text table** (override `MODDING_SCREEN.PENDING_CHANGES_WARNING`).
   - Pros: a few lines of JSON.
   - Cons:
     - It changes the vanilla page too, against "on our page only" and "vanilla untouched".
     - It keeps the game's rule and the hidden-on-a-new-page bug.
     - Other languages would mix our English with their own text.
   - Effort: minutes. Not recommended.
2. **Our own line in the game's slot, main list only.**
   - The game's line goes transparent there, as it already is in Load Order.
   - Ours shows whenever `HasPendingChanges()` is true, refreshed wherever the dot is.
   - Vanilla View keeps the game's line untouched.
   - Pros: our words and our rule; it fixes the hidden-on-a-new-page case for free.
   - Cons: a second label to keep in step (one refresh function).
   - Effort: small (about 60 to 80 lines, 2 keys, 3 to 4 checks).
3. **Option 2, plus the Relaunch Game button carries the state.**
   - The button's own dot shows while pending (the button scene already has one, as on the main menu).
   - Hovering the line or the button shows a card, "After a relaunch:", with the mods turned on and off, new
     mods, and "New load order". It lists exactly what the pending rule counts. The Changes summary's
     counts (`HistoryBar.Counts`, `HistoryBar.cs:141-145`) don't fit: their moves include disabled mods
     (`HistoryBar.cs:426-441`), and their on/off list includes a failed mod turned off.
   - Pros: the line says *that* a relaunch is needed, the card says *what* it will change, and the button
     says *where* to go.
   - Cons: the card overlaps the Changes summary a little. It is scoped to "what a relaunch applies", not
     "everything since launch".
   - Effort: small to medium (option 2 plus 40 to 60 lines).

### Where a quick relaunch lives

1. **Main menu right-click menu: "Relaunch now"** on our button, beside Move and Reset.
   - Pros: no clutter; one line in `MenuButton.cs:51-59`.
   - Cons: hard to find.
   - Effort: tiny, once the relaunch check can run without the page (below).
2. **Main menu note becomes actionable:** "Relaunch to apply your mod changes", then a small "Relaunch now"
   button. Both show only while pending.
   - Pros: shown exactly when it's needed, where Jordan already looks.
   - Cons: one more control near the corner art.
   - Effort: small. The relaunch check has to work without the Modding page: its dialog sized from the
     viewport, and its fixes redrawing the page only if one exists (about 40 lines in `RelaunchCheck.cs`).
3. **Switch profile from the main menu:** right-click our button, pick a profile, then "Switch and relaunch".
   - Pros: the real "quick" flow: no page at all to change setups.
   - Cons: profiles need a version of `Apply` that works without the page (no tickboxes to tick, no undo
     history there), and unsaved edits and Better Mod Menu have to behave the same as on the page.
   - Effort: medium (about 150 lines and its tests).
4. **A toast after switching profiles on our page:** "Switched to Co-op. Relaunch to apply." with a button.
   - Pros: says what just happened.
   - Cons:
     - Relaunch Game is already on screen.
     - The new line (option 2 above) already appears.
     - We have no toast element, so this means a new kind of UI.
   - Effort: small to medium. Not recommended.

## Recommendation

- **Page:** option 3, built in two steps: the line first (option 2), then the button's dot and the card.
  Adopt the stricter pending rule from the table in the same step. It changes what the line says, so the
  two belong together.
- **Main menu:** options 1 and 2 together. Both depend on the same relaunch-check change, and after that
  each is a few lines.
- **Profile switcher:** option 3 later, if Jordan wants it for 0.1.0 (question 3). Not option 4.
- **The bug:** merge the `Installed.Detected` fix (PR #14, open) first, and verify it with a real
  subscription. Without it, new mods never count as pending in real play.

## Wording

One verb everywhere: "relaunch" (this also covers RELEASE-PLAN §2 item 8, the copy pass). Sentence case,
short.

| Where | Today | Proposed |
|---|---|---|
| Line on our main list | Disabling/Enabling mods will only take effect after a restart. (the game's) | Relaunch to apply your mod changes (the existing `RELAUNCH_NOTE`, same as the main menu) |
| Card title | (none) | After a relaunch |
| Card lines | (none) | + Name / − Name / New: Name / New load order |
| Info panel status | Pending restart | Pending relaunch |
| New mod popup | New mods were detected, and will require a restart to be enabled. | New mods were found. Relaunch to load them. |
| Main menu button | (none) | Relaunch now |
| Main menu right-click menu | (none) | Relaunch now |
| Profile list on the main menu (if built) | (none) | Switch and relaunch |
| Relaunch Game button | Relaunch Game | unchanged |

## Open questions for Jordan

1. Moving only disabled mods, or a move the game undoes anyway: should that count as pending? (Proposed: no.)
2. Main menu: a visible "Relaunch now" beside the note, the right-click item, or both? (Proposed: both.)
3. Switching profiles from the main menu ("Switch and relaunch"): for 0.1.0, later, or not at all?
4. Should the Relaunch Game button look quieter when nothing is pending, or stay as it is? (Proposed: as it
   is, plus the dot when pending. A relaunch is also how other mods' settings apply.)
5. Load Order: still no line and no Relaunch (your round 6 call), or the line only?
6. Vanilla View keeps the game's own line untouched. OK?
7. Workshop updates downloaded mid-session: count them as pending ("1 mod updated")?

## A first PR

Branch `relaunch-line`, the page message only (options 2 and 3 in two commits):

- **`Layout.cs`:**
  - in our views, the game's `%PendingChangesLabel` goes transparent (extending `Layout.cs:180`);
  - our `SpireModManagerPending` label sits in the same box (`Layout.cs:179`), coral like the main menu
    note;
  - a `Layout.UpdatePending(screen)` sets the label and the Relaunch button's dot, next to `UpdateDot`.
- **Refresh points:** `ModToggledPatch` (beside `Layout.UpdateDot`, `Patches.cs:164`), `Layout.Apply`, and
  `Profiles.Switched`.
- **`ModEntry.HasPendingChanges`:** the stricter load-order rule. `LoadOrder.OrderChanged` stays as it is
  for the Load Order "was" column.
- **Hover card:** "After a relaunch", built from the pending changes themselves, not `HistoryBar.Counts`.
  One function returns them (mods turned on and off, new mods that are on, whether the load order
  changed), and `HasPendingChanges` is true when it returns any.
- **Strings** (`settings_ui.json`): `PENDING_CARD_TITLE` and `PENDING_NEW_ORDER`; `RELAUNCH_NOTE` is reused.
  The copy-pass changes ("Pending relaunch", the popup) go in the copy-pass PR, not here.
- **Self-test checks:**
  - "the main list shows our relaunch line, not the game's, after a mod is turned on or off";
  - "turning it back clears our relaunch line";
  - "Vanilla View shows the game's line, not ours";
  - "a page opened with a change pending shows our line" (hide the game's line, then run Layout.Apply);
  - "moving only a disabled mod is not pending, and the card doesn't say New load order";
  - "the Relaunch button's dot follows pending".

A second PR, `menu-relaunch`, makes `RelaunchCheck` work without the page and adds "Relaunch now" to the
main menu (note and right-click menu). Its checks: "the main menu offers Relaunch now only while pending",
and "the relaunch check opens from the main menu and lists a mod that can't load".
