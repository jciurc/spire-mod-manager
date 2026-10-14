# Spire Mod Manager

A mod manager for Slay the Spire 2, made for the Steam Workshop. It replaces the game's Modding page with
its own (the vanilla page stays one click away) and folds in Quick Relaunch's in-place restart.
`affects_gameplay: false`, no dependencies.

## What it does

- **Main menu:** a "Spire Mod Manager" button in the lower-left corner (right-click to move it), with the
  game's dot and a short note while a relaunch is pending.
- **Main list:** mods grouped in your own collapsible categories; search over names, aliases, notes, tags,
  authors, categories and Workshop tags; automatic tags (New, Local, Library, Gameplay, Cosmetic), your own
  tags; "Needs:" and "Required for" notes on each row; mods turned on or off since launch stand out.
- **Profiles:** saved on/off sets. Changes show as "Unsaved" with Save, Save as new and Discard; an unsaved
  profile keeps its changes when you switch away ("Name *"). New, Duplicate, Rename, Delete. A newly installed
  mod joins the active profile, on; switching to a profile that hasn't seen it leaves it as it is.
- **Load Order:** drag rows (or several, with Ctrl/Shift) or use Top/Up/Down/Bottom; dependency lines;
  a warning with "Fix now" when mods sit above what they need; Auto sort.
- **Info panel:** name and version, source (a link to the Workshop page) and status; author, last update
  (with a Patch notes link) and compatibility beside the picture; then problems (load errors, missing or
  disabled dependencies), description, size, published date, subscribers, Workshop tags, dependencies both
  ways; your alias, category, tags and notes, edited in place (also from a mod's right-click menu).
- **Changes, undo, redo, revert:** "Mods enabled N (+on -off)" over "Changes N ↕moved" since launch, with
  the full list in the info panel; Ctrl+Z / Ctrl+Y; Revert all goes back to how things were when you opened
  the page.
- **Relaunch Game:** restarts in place in the same window mode, so changes apply without going through Steam.
- **Better Mod Menu:** its data is imported on first run. Both can run together: its page takes the place of
  the game's list (the "Better Mod Menu" button), a warning says some things may behave unexpectedly, and
  profiles never turn it on or off.

Your data is one JSON file per Steam account (`mod_data/SpireModManager/manager.json`, with backups; one
that can't be read is kept aside as `manager.broken-<time>.json` and the newest backup loads, with a notice);
the game's own mod list stays the source of truth for what's on and the load order.

## Building

- `dotnet run build.cs` builds and installs into the game's `mods` folder (needs the .NET 10 SDK; close the
  game first). `--no-install` only builds.
- `dotnet run build.cs --dist` makes the release for the Workshop and never installs. The manifest gets
  `ReleaseVersion` (set by hand in build.cs) instead of the dev number, and `dist/SpireModManager/` holds
  just the `.dll`, `.json` and `.pck` to upload (RELEASE-PLAN.md §5).
- Player text lives in `localization/<lang>/*.json` and pictures in `images/`, packed into
  `SpireModManager.pck`. See TRANSLATING.md for adding a language.
- The game is looked for in the Steam library `D:\SteamLibrary\steamapps`. If yours is elsewhere, set the
  environment variable `STS2_STEAMAPPS` to its `steamapps` folder (for example
  `C:\Program Files (x86)\Steam\steamapps`); build.cs, the project and tools/dev.cs all read it.

## Developing

Dev runs happen apart from real play, so the game can be played through Steam meanwhile: a hard-linked
copy of the game in `sts2-dev` at the root of the game's drive (`STS2_DEV_DIR` picks another folder on that
drive; hard links can't cross drives) with its own mods folder, no Steam, separate save data and log, muted,
1920x1080 windowed, "[DEV]" in its title.

- `dotnet run tools/dev.cs test`: the in-game self-test against fixture mods and data, headless. Results and
  UI dumps go to `out/selftest`; add `--window` for screenshots (the window stays behind others), `--bmm` to
  test alongside Better Mod Menu (`--bmm-after`: loaded after ours; `--bmm-local`: its local-fix build), `--real` for a read-only tour with a copy of your own mods and data.
  `--first-run` runs a short suite for a first launch with no data; `--first-run-bmm` does the same with Better
  Mod Menu's data to import (`tools/fixtures/bmm`), with it installed but off, or on with `--bmm` as well.
  `--workshop <id>[,<id>...]` adds those Steam Workshop items, to test beside other mod managers. They're
  copied into the dev copy's mods for that run only and switched on after ours, in the order given. Only
  subscribed items are there to copy; a missing one stops the run. Their code runs: dev.cs writes only in the
  dev copy and the sandbox, but what a mod writes is up to it.
- `dotnet run tools/dev.cs launch`: opens the sandboxed game to try things by hand (`--first-run` or
  `--first-run-bmm`: starting from that state; `--workshop` as for `test`).
- `SPIRE_MOD_MANAGER_SKIP_PATCHES=ModToggledPatch` (patch class names, comma-separated) before `test` or `launch`:
  those patches count as failed, as after a game update that broke them, to see the game's page and its
  "needs an update" line.
- `dotnet run tools/pck.cs list|extract`: look inside the game's pack (scenes, images).
- `dotnet run tools/shot.cs out/selftest/<name>.png`: put a screenshot on a pull request. It's shrunk to a JPG,
  committed to the `pr-screenshots` branch and printed as Markdown to paste. Fixture shots only: `real-*`
  shots show your own mods and data, so they're refused.

## Branches

- **Feature branches:** one per change. Each gets a pull request into `dev`.
- **`staging`:** everything at once. Each feature branch is merged in as soon as it builds and passes the
  self-test, whether or not its pull request is merged yet, and `dev` is merged in after pull requests land.
  Builds for Jordan's game are installed from here, and the dev version bump (0.0.N) lives here. No rules;
  it's never merged into anything.
- **`dev`:** the record. Changes arrive only through pull requests (a quick read and Copilot's text review).
- **`main`:** releases, from `dev` by pull request (created at 0.1.0).
- **`pr-screenshots`:** pictures for pull requests only (`tools/shot.cs`), one folder per branch. It shares no
  history with the others, has no rules and is never merged.

Notes: TODO.md (requests and their state), DESIGN.md (decisions and background), WHATS-NEW.md (what to
look for after each install), workshop/README.md (publishing to the Steam Workshop).

## CI

Pull requests into `dev` or `main`, and pushes to them, run `.github/workflows/ci.yml`: the **checks**, on
GitHub's Linux machines. They run `dotnet run tools/check.cs` (text files parse, every text key the code
uses is in `eng`, the test fixtures are valid, nothing over 1 MB and no tokens committed; text in `eng` that
no code uses shows as a warning), then compile the tool scripts without running them. It takes seconds, so
run `dotnet run tools/check.cs` yourself before opening a pull request.

The mod itself can't build on GitHub's machines: it compiles against the game's own `sts2.dll` and Godot
assemblies, which are proprietary, so they aren't in the repo and must never be committed. The build and
the in-game test only run where the game is installed.

The in-game self-test can run from GitHub on your PC: `.github/workflows/selftest.yml` builds with
`--no-install`, runs `tools/dev.cs test`, then `--bmm`, and attaches each run's `out/selftest/*.txt` and
`out/dev.log`. Never `--real`: that output holds your own mods and data. It starts only by hand (Actions >
Self-test > Run workflow), never from a push or a pull request. To let your PC run it:

1. On GitHub: Settings > Actions > Runners > New self-hosted runner, Windows x64. Follow its download and
   `config.cmd` steps; when it asks for labels, add `sts2`. (It gets `self-hosted`, `Windows` and `X64` on
   its own, and labels ignore case, so `windows` matches.)
2. When it asks whether to run as a service, say no. Start it with `run.cmd` from a terminal, as your
   normal user, so the game can open its window and reach the game folder and your `%APPDATA%` (a service
   runs as another account, without a desktop). It takes jobs while that terminal is open.

- One runner only: the test uses one sandbox (the dev copy and the dev save data). A CI test and a dev run
  of your own can't overlap either (`tools/dev.cs` won't start while the dev copy is running).
- Security: on a public repo, any fork's pull request can reach a self-hosted runner. The fork changes the
  workflows in its own copy, whatever this repo's say. So on a public repo keep no runner registered (run
  `dotnet run tools/dev.cs test` locally), or register it only in an organization runner group limited to
  this repository and to `selftest.yml` on `dev`. Fork pull requests also wait for approval before any
  workflow runs (Settings > Actions > General > "Require approval for all outside collaborators").

## License

MIT (see LICENSE), the art in `images/` included. Slay the Spire 2 and its files belong to Mega Crit; none
of them are in this repository.
