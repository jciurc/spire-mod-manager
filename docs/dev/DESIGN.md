# Spire Mod Manager: design

Living document: decisions and their background.

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
  `eng` per key, and `build.cs` rejects keys that aren't in `eng`. A player's drop-in file
  (`mod_data/SpireModManager/translations/<lang>.json`) is merged over the packed table each time the game loads
  a language (Jordan, 2026-10-14: AI translations hosted outside the repo, downloaded by the player).
- Principles: flat, few files; fail soft (try/catch in every UI patch, `Priority.First` on shared
  `_Ready` postfixes); player text in loc tables; `affects_gameplay: false`.

## Principles (Jordan, 2026-10-05)

Ease of use is the heart of the mod, not just its utility.

- **Very intuitive** sorting, filtering and organizing of the mod list.
- **Careful data handling:** think through how we load, merge, update and resolve conflicts.
- **Nondestructive UX:** undo/redo, presets, defaults, save vs discard changes, a visual diff of pending changes.
- **Fast:** work on a local draft layer over the game's `mod_list`, and do heavy work (Workshop queries,
  scans, audits) in the background, once, cached. Efficient lookups. It has to *feel* good.

## Status (2026-10-13)

Built and covered by the in-game self-test (`dotnet run tools/dev.cs test`, about 220 checks; `--real`
tours a copy of Jordan's own mods read-only): main menu button with pending dot, Relaunch (with a check
for mods that won't load), Better Mod Menu import, categories (collapse, assign/new/rename/move/delete
from inline buttons or right-click, divider before Uncategorized), profiles (switch, new, duplicate,
rename, delete; tickbox changes become the profile's unsaved edit, with Save, Save as new and Discard),
aliases, notes and own tags (rows, info panel, editor), automatic tags (New / Local / Library / Gameplay /
Cosmetic), info panel facts (source with its Workshop link, load problems, needs / required for, Open
Folder), search (names, aliases, notes, tags, authors, categories, Workshop tags; `#tag`, `@author`),
multi-select with bulk editing, the page bar (Enable/Disable all, Expand/Collapse all), hover cards, Load
Order view (drag and drop with dimming and dependency lines, inline/menu moves, Fix order, Auto sort by
tier, order changes pending), dependency notes per row, undo/redo with Ctrl+Z/Y, Changes since launch
with Revert all, Ctrl+F, wide layout with smaller text (Vanilla View restores the game's), running
alongside Better Mod Menu (its page in place of Vanilla View, its parts hidden on ours).

Built but only testable in real play (Steam): Workshop details (dates, size, subscribers, Workshop tags,
branches), the Workshop link on the source line, the Change Notes link.

Not built yet: compatibility audit beyond dependencies, finer auto-sort tiers, linked pushing while
dragging, profile/save-slot sync, design overhaul.

## Design guidelines (Jordan, 2026-10-06)

- Functionality and UX first, visual design after.
- No AI-generated textures. Reuse the game's own elements where they fit; plain generated containers
  (solid fill, border colour) are fine.
- Our views should look clearly different from vanilla: more of the screen, smaller text.

## Research: tags in other mod managers (2026-10-07)

From general knowledge of the tools, not a fresh survey; worth checking against current versions.

- **Mod Organizer 2:** categories (several per mod, one primary), coloured separators in the list, a filter
  panel (by category, enabled state, conflicts, has notes, update available), flag icons per row (notes,
  conflicts, updates), and highlighting of conflicting mods when one is selected.
- **Vortex:** a category tree (seeded from Nexus), notes, sortable/filterable attribute columns (category,
  type, version, source), and load-order rules ("load after X") shown as icons.
- **r2modman / Thunderstore:** site-defined categories (Libraries, Tools, Mods, Cosmetic...) used as
  filters and badges; flags like Deprecated.
- **Nexus / Steam Workshop:** author-set or community-voted tags, used to browse and filter. Workshop tags
  for StS2 mods mix language, type and purpose ("english", "QoL", "Tools & APIs").
- **Better Mod Menu:** name colours by Workshop tag, groups.

Ideas for us, roughly by value:
1. **Click a chip to filter** the list by that tag (and a tag list in search, e.g. `tag:co-op`). Built: a
   click searches for `#tag`, Shift-click adds it.
2. **Status tags** worked out automatically: New (installed since last session), Updated (Workshop update
   since last session), Failed, Duplicate, Missing dependency. Pairs with the "newly installed" and
   "recently updated" backlog items.
3. **Workshop tags as suggestions:** offer a mod's Workshop tags (minus language tags) as one-click own tags,
   and use them for the finer Auto sort tiers (content vs QoL vs visual).
4. **Tag manager:** rename or merge a tag across all mods; per-tag colour.
5. **Bulk tagging** with multi-select. Built (the bulk menu).
6. **Tag-driven actions:** "enable everything tagged co-op" as a profile shortcut; auto-category by tag.

## Research: other StS2 mod managers (2026-10-13)

A survey of the Steam Workshop at Jordan's request, from a comparison of the mod managers there. Subscriber counts
are from that day. What we do about it is in TODO round 21 and RELEASE-PLAN.md: Jordan's own calls are marked;
the rest are recommendations to confirm with Jordan.

- **Better Mod Menu** (3748029698): 101k subscribers, last updated 12 Jul. A companion, "Better Mod Menu Fix
  (Public-Beta)" (3775001896, 1.5k), patches the same beta bugs as Jordan's upstream pull requests. For us: the
  biggest one, and the one we already run beside (round 17) and import from.
- **Load Order Manager** (3747605109, Jianbao233, github.com/Jianbao233/STS2-LoadOrderManager): 90k, updated
  3 Oct. A button on the vanilla Modding page; up, down, top and bottom moves; Smart Sort; A-Z; presets (with
  `knownMods` and `autoEnableNewMods`); import and export as JSON on the clipboard; 14 languages
  (`i18n/*.lang`). One package for every branch: a tiny loader DLL picks
  `bin/g<version>/LoadOrderManager.Impl.dll`, which reaches the game only through `AccessTools` reflection.
  Presets go by id only (its README admits a local and a Workshop copy share one entry). No restart button.
  For us: nearly as many players as Better Mod Menu, and it rewrites `mod_list`, so it can undo our Load Order
  page. Its branch packaging, languages and presets file are worth learning from (the ideas, not the code).
- **Mod Launch Manager** (3788556097, Snowline): 4.2k, updated 12 Oct. The only one with our scope. A
  pre-launch screen (like ModTheSpire's) before other mods initialize, so newly detected mods load in the same
  launch; a best-effort unload of a DLL whose init fails; Workshop update check, download and subscribe from the
  game; aliases, groups and sub-groups, filters, profiles; dependencies enabled automatically; a choice of local
  or Workshop copy; friend-list sync; controller support; AI translation. Keeps itself first in `mod_list`. For
  us: the closest rival. Loading first is the groundwork for crash-safe launch and loading without a restart.
- **MOD管理器 | Mod Manager** (3813204995, xiaozz): 136. Grid and list views, card-art mods kept apart, Workshop
  subscribe, unsubscribe and update, dependencies filled in, and a one-time rollback to the last working setup
  after a startup crash. For us: small, but the crash rollback and the Workshop actions are worth having.
- **Smaller ones:**
  - Better Mod List (3796724666, 85): Workshop tags, update in place; beta only.
  - Mod Profile Manager (3749810410, 667): profiles and "MPM1:" share codes; needs RitsuLib.
  - Mod Preset Sync (3774789540, 3.7k): presets as text; downloads missing mods.
  - ModTheSpire2 (3747911678, 861): an external launcher (an .exe).
  - STS2 Mod Manager 启动前模组管理 (3799576017, 239, github.com/Jolyne-kujo/STS2-ModManager): a native window
    before other mods initialize; must be first in the order.
  - Mod List Hot Reload (3794780403, 32): loads mods mid-session through a collectible `AssemblyLoadContext`,
    and lists what can't reload.
  - For us: share codes are common (these two and Load Order Manager's JSON), so players will expect them. Mod
    Profile Manager and Mod Preset Sync rewrite `mod_list` too. Mod List Hot Reload shows what loading without a
    restart can and can't do.

**Multiplayer sync** is a niche of its own; leave it to them: AutoModSubscriber (97k), Multiplayer Mod Sync
(26k), Host Mod Sync.

**Order rewriters** sit next to it but are a different matter: LoadOrderSync (3757945718, re-sorts `mod_list`
at launch) and ModListSorter (3749297037, re-sorts `mod_list` A-Z on every exit) quietly undo our Load Order page.
They belong with the managers warning (TODO round 21), not with what we leave out.

**Only we have:** undo/redo with Changes since launch and Revert all; unsaved profile edits; drag and drop with
dependency lines; profiles by id and source; an in-place relaunch; the Better Mod Menu import and running beside
it.

What we take from it (details in TODO round 21):
1. **Share and import a profile as text** (Jordan): a 0.1.0 blocker.
2. **Crash-safe launch** (Jordan): a spike first; in 0.1.0 if it's small, else the first update.
3. **Warn about the managers that rewrite `mod_list`** (recommended): the round 19 managers warning, widened.
4. **Load mods without a restart** (Jordan): a spike, together with crash-safe launch.
5. Recommended, after 0.1.0: Simplified Chinese; one package for every branch; importing other managers' data;
   Workshop actions in the info panel.

**Leave out** (recommended): multiplayer host sync (AutoModSubscriber and Multiplayer Mod Sync own it), AI
translation, forced A-Z ordering, a grid view.

## Backlog: load order

- **Auto sort** by impact tier, then a dependency sweep, always respecting the player's manual order
  over the computed one: frameworks/libraries first, then content additions, then gameplay/QoL, then
  UI/visual-only. (Built so far: libraries, then gameplay mods, then the rest, then the dependency sweep;
  "Fix now" only moves dependencies ahead of what needs them.) Finer tiers need signals: dependency
  counts, `affects_gameplay`, Workshop tags, our categories/tags.
- **Dependency tree lines** (built): a tree-style line down the left side connecting a mod to its
  dependencies/dependents, shown on hover and while dragging.
- **Dragging feedback** (built): dim the list slightly while dragging; make the moving row and its
  landing spot obvious. Dragging a mod lights up every mod linked to it (dependencies and dependents),
  with the connecting line brighter, standing out from the dimmed list.
- **Linked pushing:** a row moves freely while the order stays valid; when it reaches a mod it is linked
  to, it pushes that mod along as a group. Dragging back without dropping unlinks them: the pushed mod
  returns to the position it started from once the dragged row passes that point again.

## Features

Phases are a proposal; v1 is what ships first on the Workshop.

### v1: core manager
- **Main menu entry:** "Spire Mod Manager" button in the lower-left corner, kept separate from the
  main menu links, with the game's notification dot when a relaunch is pending. (The lower right
  holds the vanilla "MODDED (n)" label and its hover list of loaded mods; too crowded.)
  - Since 2026-10-14 (Jordan): the game's own main-menu text button, reading "Default (12 mods)" (smaller than the game's; Jordan, later that day), with
    no picture; its right-click menu is headed "Spire Mod Manager v<version>".
- **Relaunch:** button reads "Relaunch Game" for now; better messaging later. Pending-changes state
  is shared by the menu indicator and the manager.
- **Settings entry** (2026-10-14, Jordan): removed the same day ("let's remove the additional manager button in vanilla
  settings page"); the main menu button's card says what it opens instead. It was: a button in the Settings screen's top-right corner,
  level with the tabs, outside the settings list; opens the Modding page on the Settings stack. Main menu only
  (a run's Settings can't open the page). Mouse only for now: controllers reach the page by the game's Mod Settings line.
- **Own manager scene** + one-click switch to the vanilla Modding page.
  - MVP (built): category headers inside the vanilla screen's own row list, "Vanilla View" toggle.
  - **Design overhaul:** the manager must look clearly like a third-party mod (ours), distinct from
    both the vanilla page and Better Mod Menu, while staying in the game's art style. Its own
    identity: title/branding, accent colour, layout.
- **Draft layer:** every edit (toggle, move, category, tag) goes to a draft; Save / Discard,
  undo/redo, and a diff view (enabled, disabled, moved) before applying to `mod_list`.
  - Built differently: edits go to `mod_list` at once (they take effect on relaunch). The active profile
    keeps a changed list as its unsaved edit (Save / Save as new / Discard); undo/redo, Revert all and the
    Changes list (enabled, disabled, installed, moved, edited) cover everything since launch.
- **Profiles** (presets), by id + source. No built-in "Defaults": with no profiles yet, an ordinary one
  named "Default" is made from the mod list as it is.
- **Load order panel**, separate from categories: drag and drop, disabled mods greyed but movable,
  global order by default.
- **Categories** (custom, collapsible) and **tags** (custom + predefined) with clear markers,
  especially for gameplay-changing mods (`affects_gameplay`).
  - "Uncategorized" sits last, below a divider, muted, and is hidden when empty (built).
- **Search and filter** over name, alias, notes, author, tags, category, state.
- **Alias and notes** per mod, visibly marked as user-written, original info one click away.
- **Mod details:** manifest (version, game version, dependencies, `affects_gameplay`) plus Workshop
  data: dates published/updated, author's description, preview image, tags, supported game
  versions. The game already runs one batched `SteamUGC.CreateQueryUGCDetailsRequest` at startup
  (`ModManager.CheckSteamBranchSupport`), and each `Mod` has `workshopId`, so we can do the same
  once and cache it.
  - **Link to the Workshop page** (`steam://url/CommunityFilePage/<workshopId>`, or the web URL via
    `PlatformUtil.OpenUrl` like the vanilla Get Mods button).

### v2: smarter
- **Compatibility audit:** missing/disabled dependencies, duplicates (`DisabledDuplicate`),
  load failures, unsupported game version or branch, mods patching the same things, whole-game
  mods clearly marked. Explain each issue in plain words with a fix.
- **Order presets** and **auto/smart sort** (dependencies first, then rules; may need Workshop data).
- Per-profile load order override (stretch).
- **Optional profile sync with the game's save slots** (`profile1..3`, `profile.save.last_profile_id`):
  a mod profile can be linked to a save slot, so switching slots offers to switch mods (and relaunch).

### Content picker: now its own mod project
- Enable or disable individual cards, relics, events and so on from any mod, without disabling
  the mod. Choose which third-party content shows up in a run, e.g. per character. Likely hooks:
  the game's mod content pools (`ModHelper.ModPoolContent`). Gameplay-affecting, so it must not
  live in the manager itself (the manager stays `affects_gameplay: false`).

## Open

- Workshop publishing flow, preview image, tags: planned in RELEASE-PLAN.md (beta only at first, §7).
- Ideas raised, not committed: "Play vanilla" via `--nomods`. (Profile share codes are a 0.1.0 blocker since
  TODO round 21.)
- Raised here and built since: enabling missing dependencies ("Enable needed mods", the relaunch
  check), `affects_gameplay` shown for multiplayer (the Gameplay tag), importing Better Mod Menu's
  profiles (first run).
