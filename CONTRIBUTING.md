# Contributing

Bug reports, ideas, translations and pull requests are all welcome. This is a small project with one
maintainer, so replies can take a few days.

- **Bugs and ideas:** [open an issue](https://github.com/jciurc/spire-mod-manager/issues/new/choose). The bug
  form asks for what helps most: game version and branch, what you did, and your `godot.log`.
- **Translations:** see [TRANSLATING.md](TRANSLATING.md). No code needed.
- **Code:** read on. For anything bigger than a fix, open an issue first so we can agree on the approach.

The [issues](https://github.com/jciurc/spire-mod-manager/issues) are the to-do list: bugs, ideas and planned
work. The ones labelled `good first issue` are a good start; say on the issue that you're taking it.

## What you need

- **Windows.** The project builds against the game's Windows data folder, and the dev tools use hard links
  and `%APPDATA%`.
- **Slay the Spire 2, v0.107.1 or newer, on either branch.** The mod compiles against the game's own
  `sts2.dll` and Godot assemblies. They're proprietary, so they aren't in this repository and must never be
  committed; the build reads them from your installed game. The mod runs on both branches: what only the
  public-beta has goes through `Compat.cs`, by name (`tools/check.cs` checks). The dev tools run the beta's game,
  or the stable branch's with `--stable` (`tools/dev.cs` says how each gets its copy).
- **The [.NET 10 SDK](https://dotnet.microsoft.com/download).** The build scripts are .NET 10 file-based apps
  (`dotnet run build.cs`). The mod itself targets .NET 9, like the game.
- **Where your game is:** found on its own. build.cs and tools/dev.cs look through Steam's list of libraries
  for the one holding Slay the Spire 2, and stop with a message if none does. Building another way (an IDE, a
  plain `dotnet build`) tries Steam's default folder, then `SteamLibrary` at the root of drives D: to F:. To
  pick a library yourself, set the environment variable `STS2_STEAMAPPS` to its `steamapps` folder, for
  example `D:\SteamLibrary\steamapps`.

## Building

- `dotnet run build.cs` builds and installs into the game's `mods` folder (close the game first).
  `--no-install` only builds, into `out/`.
- `dotnet run build.cs --dist` makes the release for the Workshop and never installs: `dist/SpireModManager/`
  holds just the `.dll`, `.json` and `.pck`, and the manifest gets `ReleaseVersion` (set by hand in build.cs).
  That DLL leaves out the in-game self-test, the dev-run switches and debug information; every other build
  keeps them.
- Player text lives in `localization/<lang>/*.json`, packed into `SpireModManager.pck` (pictures would go in
  `images/`, packed the same way; there are none now). A player can also drop a translation file into the mod's data folder, which comes before the
  packed one (TRANSLATING.md, "Drop-in translation files"; `Translations.cs`).

## Trying it and testing

`tools/dev.cs` runs the game apart from your real play, so you can keep playing through Steam meanwhile. It
needs to know what it touches on your PC:

- **A dev copy of the game:** `sts2-dev` at the root of the game's drive (`D:\sts2-dev` for the default
  library), made of hard links to the installed game's files, so it takes no extra space. `STS2_DEV_DIR` picks
  another folder on the same drive (hard links can't cross drives). It has its own `mods` folder: the fresh
  build, the empty test mods from `tools/fixtures/mods` and, for `--bmm`, Better Mod Menu if it's in your game's
  own `mods` folder (a Workshop subscription lives elsewhere and isn't picked up). None of your other mods, so a
  run is the same on every PC. Builds never go into your real `mods` folder this way.
- **Separate save data and log:** the dev copy runs without Steam, so the game keeps its data in
  `%APPDATA%\SlayTheSpire2\default\1\` instead of your Steam account's folder, and its log goes to
  `out\dev.log`. Your real saves and mod lists are never touched. It's muted, 1920x1080 windowed, with
  "[DEV]" in its title.

Commands:

- `dotnet run tools/dev.cs test`: the in-game self-test against the fixture mods and data in
  `tools/fixtures`, headless. Results and UI dumps go to `out/selftest`. Exit code 1 on any failure.
  - `--window`: with a window, for screenshots (it stays behind other windows).
  - `--bmm`: alongside Better Mod Menu, if you have it installed (`--bmm-after`: loaded after ours).
  - `--first-run`: a short suite for a first launch with no data; `--first-run-bmm`: the same, with Better Mod
    Menu's data (`tools/fixtures/bmm`) to import.
  - `--workshop <id>[,<id>...]`: adds those Steam Workshop items for that run, to test beside other mod
    managers. Only items you're subscribed to can be copied in. Their code runs: dev.cs writes only in the dev
    copy and the sandbox, but what a mod writes is up to it, and files at the top of
    `%APPDATA%\SlayTheSpire2` (such as `mod_configs`) are shared with your real game. Subscribing also installs
    the item in your real game, where a new mod starts enabled.
  - `--crash-prepare`, then `--crash`: the offer after a failed start, end to end. The first run only reaches
    the main menu, so the last working setup is saved. The second keeps that data, turns Test Retired on and
    fails the start with a made-up error. Our dialog replaces the game's and is answered with Restore and
    restart (the relaunch is caught). For a real failure, add `--workshop` with the mods that cause it to
    `--crash`.
  - `--real` (maintainer only): a read-only tour with your own mods (all switched off) and a copy of your data,
    in place of the test mods. Its output holds your own mod list, so it never goes into a pull request.
- `dotnet run tools/dev.cs launch`: opens the sandboxed game to try things by hand (`--first-run`,
  `--first-run-bmm` and `--workshop` as for `test`).
- `dotnet run tools/dev.cs smoke`: the release build from `dotnet run build.cs --dist`, which has no self-test
  inside, in the sandbox until the main menu has loaded. It fails if the build is older than the checked-out
  commit, the mod didn't start or kept its dev switches, the main menu never loaded, the game exited on its own,
  or the mod logged an error or a warning.
- `SPIRE_MOD_MANAGER_SKIP_PATCHES=ModToggledPatch` (patch class names, comma-separated) before `test` or
  `launch`: those patches count as failed, as after a game update that broke them, to see the game's page and
  its "needs an update" line.
- `dotnet run tools/pck.cs list|extract`: look inside the game's pack (scenes, images).

Only one dev copy can run at a time; `tools/dev.cs` won't start while one is running.

## Pull requests

1. Branch from `dev`, and open the pull request into `dev`. One change per pull request.
2. Run `dotnet run tools/check.cs` (seconds): text files parse, every text key the code uses is in `eng`, the
   fixtures are valid, no code uses a null-forgiving `!` (narrow with `is { } x` or a checked local instead),
   and nothing over 1 MB or token-like is committed. CI runs the same checks.
3. For code changes, run `dotnet run tools/dev.cs test` and put its pass and fail counts in the pull request.
4. Say what a player will notice, in plain words. Screenshots are welcome: drag them into the description.
5. Name the issue it closes ("Fixes #123"), so merging closes it.

CI (`.github/workflows/ci.yml`) runs the checks and compiles the tool scripts on GitHub's Linux machines. The
mod itself can't build there, since the game's files aren't in the repository, so the build and the in-game
test only run on a PC with the game installed. On pull requests from forks, CI may wait until the maintainer
approves the run. Copilot reviews each pull request; resolve or answer its comments before merging.

## Branches

- **`dev`:** the default branch. Changes arrive only through pull requests.
- **`main`:** releases, from `dev` by pull request (from 0.1.0).
- **`staging`** (maintainer): `dev` plus open pull requests, for builds the maintainer plays. Never merged.
- **`pr-screenshots`** (maintainer): pictures for pull requests (`tools/shot.cs`). Never merged.

## Where things are

- `src/SpireModManager/`: the mod. `ModEntry.cs` is the entry point and `Patches.cs` holds the Harmony
  patches into the game. Each view or feature has its own file: `CategoryList.cs` (the main list),
  `LoadOrder.cs`, `ModDetails.cs` (the info panel), `Profiles.cs` and `ProfileBar.cs`, `Search.cs`,
  `History.cs` (undo and redo) and so on. `Store.cs` saves `manager.json`. `SelfTest.cs` is the in-game
  self-test, which does nothing unless `tools/dev.cs` asks for it.
- `localization/`: player text, one folder per language. `scenes/` (and `images/`, if any): packed into the `.pck`.
- `tools/`: the dev scripts (`dev.cs`, `check.cs`, `pck.cs`, `shot.cs`) and the self-test fixtures.
- `workshop/`: the Steam Workshop listing and upload steps.
- `docs/dev/`: the maintainer's notes: the design decisions in force, how to release, and the self-test runner.
- `CHANGELOG.md`: what changed in each Workshop release.
