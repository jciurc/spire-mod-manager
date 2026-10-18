# Design

The decisions in force, and why. Ideas and plans are in the issues; the older notes are in git history.

## Principles

Ease of use is the heart of the mod, not just its utility.

- **Intuitive:** sorting, filtering and organizing the mod list should need no explanation.
- **Careful with data:** think through how it loads, merges, updates and resolves conflicts.
- **Nondestructive:** undo and redo, a diff of pending changes, save versus discard.
- **Fast:** heavy work (Workshop queries, scans) runs once and is cached; redraws use `Lookups`.
- **Function first,** visual design after. Reuse the game's own elements; no AI-generated textures.
- **Clearly ours:** our views use more of the screen and smaller text than the game's page.

## The game's data comes first

- The vanilla Modding page keeps working untouched, one click away (Vanilla List).
- The game's `mod_list` is the truth for what's on and the load order. Edits go to it at once and take effect
  on relaunch; nothing of ours can disagree with the game's tickboxes.
- One exception, because the game has one switch for them: which copy loads of a mod installed twice from one
  source (two Workshop items). That's our pick (`copy_picks`), applied in our Init, before the game tries the
  copies; our page has a tickbox per copy, one copy of an id on. The game's page keeps its one switch.
- Profiles key mods by id and source, like `mod_list` (plus the picked copy). A newly installed mod joins the active profile, enabled.
  With no profiles yet, an ordinary one named "Default" is made from the current list.
- The mod managers (this one, Better Mod Menu) are never turned on or off by a profile.
- Our own data is one JSON file per Steam account, written atomically, with 10 backups.

## Load order

- One global order, in its own view. Disabled mods are greyed out but can still be moved.
- Spire Mod Manager always loads first, pinned at the top: its Init has to run before any copy it picks for.
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
- F8, which the game leaves free, opens the Modding page from the main menu and goes back from it (anything of ours
  that's open closes first, as with Escape); nothing on other screens or while typing. One constant for now.
- Left to other mods: multiplayer host sync, forced A-Z ordering, a grid view.
