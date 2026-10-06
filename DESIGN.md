# Spire Mod Manager: design

Living document. Game facts live in the game-mod skill's `games/slay-the-spire-2.md`; background in
`C:\repos\gaming\slay the spire\STS2-MOD-MANAGER-HANDOFF.md`.

## Decided (2026-10-04)

- **Name:** id `SpireModManager` (permanent), title "Spire Mod Manager". Published on the Steam Workshop.
- **Quick Relaunch:** folded in and retired. Restart lives only here; never install both (two buttons).
- **Layout:** our own scene, with a one-click switch to the vanilla Modding page. Backwards compatible:
  the vanilla page must keep working untouched, and anything we store must stay consistent with the
  game's `mod_list` so the vanilla tickboxes always show the truth.
- **Load order:**
  - Global sort order is the default and our recommendation.
  - Disabled mods appear greyed out but can still be moved.
  - Sort order gets **its own panel**, separate from the categories layout.
  - Stretch goal: optional per-profile manual override of the order.
- **Profiles** key mods by id + source, matching the game's `mod_list`.
- **Languages:** English only for v1, set up for drop-in community translations (`TRANSLATING.md`).
  Mod text must merge into the game's existing tables (`LocManager` only loads table names the game
  has; mods can't add new ones); keys are prefixed `SPIRE_MOD_MANAGER.`. Missing keys fall back to
  `eng` per key, and `build.py` rejects keys that aren't in `eng`.
- Principles: flat, few files; fail soft (try/catch in every UI patch, `Priority.First` on shared
  `_Ready` postfixes); player text in loc tables; `affects_gameplay: false`.

## Principles (Jordan, 2026-10-05)

Ease of use is the heart of the mod, not just its utility.

- **Very intuitive** sorting, filtering and organizing of the mod list.
- **Careful data handling:** think through how we load, merge, update and resolve conflicts.
- **Nondestructive UX:** undo/redo, presets, defaults, save vs discard changes, a visual diff of pending changes.
- **Fast:** work on a local draft layer over the game's `mod_list`, and do heavy work (Workshop queries,
  scans, audits) in the background, once, cached. Efficient lookups. It has to *feel* good.

## Features

Phases are a proposal; v1 is what ships first on the Workshop.

### v1: core manager
- **Main menu entry:** "Spire Mod Manager" button in the lower-left corner, kept separate from the
  main menu links, with the game's notification dot when a relaunch is pending. (The lower right
  holds the vanilla "MODDED (n)" label and its hover list of loaded mods; too crowded.)
- **Relaunch:** button reads "Relaunch Game" for now; better messaging later. Pending-changes state
  is shared by the menu indicator and the manager.
- **Own manager scene** + one-click switch to the vanilla Modding page.
- **Draft layer:** every edit (toggle, move, category, tag) goes to a draft; Save / Discard,
  undo/redo, and a diff view (enabled, disabled, moved) before applying to `mod_list`.
- **Profiles** (presets), by id + source; "Defaults" as a built-in profile.
- **Load order panel**, separate from categories: drag and drop, disabled mods greyed but movable,
  global order by default.
- **Categories** (custom, collapsible) and **tags** (custom + predefined) with clear markers,
  especially for gameplay-changing mods (`affects_gameplay`).
- **Search and filter** over name, alias, notes, author, tags, category, state.
- **Alias and notes** per mod, visibly marked as user-written, original info one click away.
- **Mod details:** manifest (version, game version, dependencies, `affects_gameplay`) plus Workshop
  data: dates published/updated, author's description, preview image, tags, supported game
  versions. The game already runs one batched `SteamUGC.CreateQueryUGCDetailsRequest` at startup
  (`ModManager.CheckSteamBranchSupport`), and each `Mod` has `workshopId`, so we can do the same
  once and cache it.

### v2: smarter
- **Compatibility audit:** missing/disabled dependencies, duplicates (`DisabledDuplicate`),
  load failures, unsupported game version or branch, mods patching the same things, whole-game
  mods clearly marked. Explain each issue in plain words with a fix.
- **Order presets** and **auto/smart sort** (dependencies first, then rules; may need Workshop data).
- Per-profile load order override (stretch).
- **Optional profile sync with the game's save slots** (`profile1..3`, `profile.save.last_profile_id`):
  a mod profile can be linked to a save slot, so switching slots offers to switch mods (and relaunch).

### Later, maybe a standalone mod: content picker
- Enable or disable individual cards, relics, events and so on from any mod, without disabling
  the mod. Choose which third-party content shows up in a run, e.g. per character. Likely hooks:
  the game's mod content pools (`ModHelper.ModPoolContent`). Gameplay-affecting, so it must not
  live in the manager itself (the manager stays `affects_gameplay: false`).

## Open

- Coexistence with Better Mod Menu: step aside or warn when it's loaded.
- Workshop publishing flow, preview image, tags; building against beta vs stable.
- Ideas raised, not committed: "Play vanilla" via `--nomods`,
  auto-enable missing dependencies, `affects_gameplay` shown for multiplayer, profile share codes,
  importing BMM profiles.
