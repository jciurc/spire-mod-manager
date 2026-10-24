# Design

The decisions in force, and why. Ideas and plans are in the issues; the older notes are in git history.

## Principles

Ease of use is the heart of the mod, not just its utility.

- **Intuitive:** sorting, filtering and organizing the mod list should need no explanation.
- **Careful with data:** think through how it loads, merges, updates and resolves conflicts.
- **Nondestructive:** undo and redo, a diff of pending changes, save versus discard.
- **Fast:** heavy work (Workshop queries, scans) runs once and is cached; redraws use `Lookups`. What every row
  has shares one look (a `Theme` on it, not per-node overrides), and a row makes only the parts it shows. Fonts
  (`ModEntry.Font`, once per language) and the node paths, meta keys and theme names that per-row code passes to
  Godot are made once, not converted from text on each call.
- **Function first,** visual design after. Reuse the game's own elements; no AI-generated textures.
- **Clearly ours:** our views use more of the screen and smaller text than the game's page.
- **Leaving the game shows:** our links and menu items that open a web page or the system's file browser have the
  external-link mark after their text (`LinkMark.cs`); not the copy of the game's Get Mods button, nor the game's popups.

## The game's data comes first

- The vanilla modding page keeps working untouched, one click away (the link by our page's title).
- The game's `mod_list` is the truth for what's on and the load order. Edits go to it at once and take effect
  on relaunch; nothing of ours can disagree with the game's tickboxes.
- One exception, because the game has one switch for them: copies of a mod installed twice from one source (two
  Workshop items). Which of them are ticked is ours (`copies_off`). Which of the ticked ones loads is worked out at
  every launch, never stored: the game's local-vs-Workshop rule on its switches, then of one source the highest ticked
  version (the copy installed first on a tie), applied in our Init, before the game tries the copies. Our page has a
  tickbox per copy, and two copies of an id can both be ticked (only one loads). The game's page keeps its one switch.
- Where the page and the launch can still differ, or older data reads differently (all rare):
  - Data from the dev builds before this, whose pick was missing: the copy standing in for it stays ticked, so once
    the pick is back both are ticked and the higher version loads, where those builds loaded the pick. A stand-in
    can't be told from a copy ticked by hand.
  - A session that can't save our data ties by the file's record order, as the next launch does. But a copy new to
    the record that ties with the one there, installed with the game closed, goes by the copy the last launch had on
    the page, and by the items' age at the next launch, whose launch record names both.
  - With two local copies and a Workshop one, the game's rule can leave copies of both sources. Our choice goes by the
    source of the first ticked copy in the game's list, an order that isn't stable across launches.
- Profiles key mods by id and source, like `mod_list` (plus, for copies from one source, which are ticked). A shared
  list's rows set a mod installed once by its id; where its id is installed more than once, a row with a Workshop
  link sets that copy alone. A newly installed mod joins the active profile, enabled.
  With no profiles yet, an ordinary one named "Default" is made from the current list.
- The mod managers (this one, Better Mod Menu) are never turned on or off by a profile.
- Spire Mod Manager is first in whichever category it's in. Its copies have a box each, like any other mod's (its
  copies from one source share one). Disable all leaves it on; its own box, a selection that has it and the bulk menu
  can turn it off. With every copy of it off, a red banner says so and Relaunch asks first, both with Enable now for
  the copy running.
- Its copies get the same messages as any mod's, and the same clash warnings with two of them ticked. Which of them
  loads is the game's own choice, as it loads before it could choose: between sources the game's rule, with its
  reason; between copies from one source, with none.
- Our own data is one JSON file per Steam account, written atomically, with 10 backups.
- A start that fails gets our dialog in place of the game's (its message first, in its box). It names the mod that
  stopped it, from the error's frames, Harmony's failures (the mod whose patch Harmony was applying, recorded as the
  call fails, so a framework only for its own patch when Harmony's call was recorded) and Mod Doctor's pick (never
  Spire Mod Manager itself, which its own reading decides), our own checks first, with "most likely"
  where the evidence is weaker; it offers to turn that mod off (every switch of it, and the mods that need it) and
  restart, or to restore the last working setup when that can help. Nothing else changes: every other mod keeps the switches the game read. A turn-off counts only once
  the save is checked and it's recorded (`turned-off.json`), and Restore and profiles keep the mod off until it's
  turned back on.

## Load order

- One global order, in its own view. Disabled mods are greyed out but can still be moved.
- Spire Mod Manager always loads first, pinned at the top: its Init has to run before any copy it chooses for.
- Fix now moves dependencies ahead of what needs them, otherwise keeping the order (the game's own rule).
- Auto sort: libraries, then gameplay mods, then the rest, then the same dependency sweep; the player's order
  holds within each tier.

## Code

- Few, flat files, one per feature or view.
- Fail soft: every patch body runs in try/catch; if a patch breaks after a game update, the game's own page
  shows, with a line saying the mod needs an update.
- `affects_gameplay: false`. Anything that changes gameplay belongs in another mod.
- Player text lives in the loc tables, merged into the game's own (`SPIRE_MOD_MANAGER.` keys). Missing keys
  fall back to `eng` one by one, and `tools/check.cs` rejects keys that `eng` doesn't have.
- Translations in the repo come from native speakers. Machine translations can be hosted elsewhere: a player's
  own file in `mod_data` is merged over the packed one when the game loads a language.

## Releases

- Both branches, stable and public-beta, game v0.107.1 or newer: one build, with what only one branch has
  reached by name (`Compat.cs`), and the release test run on each (RELEASING.md).
- Each first upload goes up private, gets tested, then goes public (unlisted as a soft launch).
- Workshop pictures are real screenshots, never generated art.
- Mouse and keyboard only for now; controller support is planned.
- Open source under MIT. Bugs go to GitHub issues; Workshop comments get read too.
- The main menu button is the game's own text button ("Default (12 mods)"), with no picture; its right-click
  menu is headed "Spire Mod Manager v<version>".
- F3 (the game uses it only as a debug key in its trailer mode) opens the modding page from the main menu and goes back from it (anything of ours
  that's open closes first, as with Escape); nothing on other screens or while typing. One constant for now.
- Left to other mods: multiplayer host sync, forced A-Z ordering, a grid view.
