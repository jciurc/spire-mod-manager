# Spire Mod Manager: design

Living document. Game facts live in the game-mod skill's `games/slay-the-spire-2.md`; background in
`C:\repos\scratch\STS2-MOD-MANAGER-HANDOFF.md`.

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
- Principles: flat, few files; fail soft (try/catch in every UI patch, `Priority.First` on shared
  `_Ready` postfixes); player text in loc tables; `affects_gameplay: false`.

## Features

Jordan's list goes here.

## Open

- Languages beyond `eng` for v1.
- Coexistence with Better Mod Menu: step aside or warn when it's loaded.
- Workshop publishing flow, preview image, tags; building against beta vs stable.
- Ideas raised, not committed: profiles linked to save slots, "Play vanilla" via `--nomods`,
  auto-enable missing dependencies, `affects_gameplay` shown for multiplayer, profile share codes,
  importing BMM profiles.
