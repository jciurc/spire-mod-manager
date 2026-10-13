# Spire Mod Manager

A mod manager for Slay the Spire 2, made for the Steam Workshop. It replaces the game's Modding page with
its own (the vanilla page stays one click away) and folds in Quick Relaunch's in-place restart.
`affects_gameplay: false`, no dependencies.

## What it does

- **Main menu:** a "Spire Mod Manager" button in the lower-left corner (right-click to move it), with the
  game's dot and a short note while a relaunch is pending.
- **Main list:** mods grouped in your own collapsible categories; search over names, aliases, notes, tags,
  authors, categories and Workshop tags; automatic tags (Gameplay, Library), your own tags; "Needs:" and
  "Required for" notes on each row; mods turned on or off since launch stand out.
- **Profiles:** saved on/off sets. Changes show as "Unsaved" with Save, Save as new and Discard; an unsaved
  profile keeps its changes when you switch away ("Name *"). New, Duplicate, Rename, Delete. A newly installed
  mod joins the active profile, on; switching to a profile that hasn't seen it leaves it as it is.
- **Load Order:** drag rows (or several, with Ctrl/Shift) or use Top/Up/Down/Bottom; dependency lines;
  a warning with "Fix now" when mods sit above what they need; Auto sort.
- **Info panel:** problems first (load errors, missing or disabled dependencies), author, description,
  version, compatibility, Workshop dates and tags, dependencies both ways; your tags, alias and notes,
  edited in place (also from a mod's right-click menu).
- **Changes, undo, redo, revert:** "Changes +on -off ↕moved" since launch, with the full list in the info
  panel; Ctrl+Z / Ctrl+Y; Revert all goes back to how things were when you opened the page.
- **Relaunch Game:** restarts in place in the same window mode, so changes apply without going through Steam.
- **Better Mod Menu:** its data is imported on first run. Both can run together: its page takes the place of
  the game's list (the "Better Mod Menu" button), a warning says some things may behave unexpectedly, and
  profiles never turn it on or off.

Your data is one JSON file per Steam account (`mod_data/SpireModManager/manager.json`, with backups);
the game's own mod list stays the source of truth for what's on and the load order.

## Building

- `dotnet run build.cs` builds and installs into the game's `mods` folder (needs the .NET 10 SDK; close the
  game first). `--no-install` only builds.
- Player text lives in `localization/<lang>/*.json` and pictures in `images/`, packed into
  `SpireModManager.pck`. See TRANSLATING.md for adding a language.

## Developing

Dev runs happen apart from real play, so the game can be played through Steam meanwhile: a hard-linked
copy of the game in `D:\sts2-dev` with its own mods folder, no Steam, separate save data and log, muted,
1920x1080 windowed, "[DEV]" in its title.

- `dotnet run tools/dev.cs test`: the in-game self-test against fixture mods and data, headless. Results and
  UI dumps go to `out/selftest`; add `--window` for screenshots (the window stays behind others), `--bmm` to
  test alongside Better Mod Menu (`--bmm-after`: loaded after ours; `--bmm-local`: its local-fix build), `--real` for a read-only tour with a copy of your own mods and data.
- `dotnet run tools/dev.cs launch`: opens the sandboxed game to try things by hand.
- `dotnet run tools/pck.cs list|extract`: look inside the game's pack (scenes, images).

Notes: TODO.md (requests and their state), DESIGN.md (decisions and background), WHATS-NEW.md (what to
look for after each install).
