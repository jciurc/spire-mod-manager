# Contributing

Bug reports, ideas, translations and pull requests are all welcome. This is a small project with one
maintainer, so replies can take a few days.

- **Bugs and ideas:** [open an issue](https://github.com/jciurc/spire-mod-manager/issues/new/choose). The bug
  form asks for what helps most: game version and branch, what you did, and your `godot.log`.
- **Translations:** see [TRANSLATING.md](TRANSLATING.md). No code needed.
- **Code:** read on. For anything bigger than a fix, open an issue first so we can agree on the approach.

## What you need

- **Windows.** The project builds against the game's Windows data folder, and the dev tools use hard links
  and `%APPDATA%`.
- **Slay the Spire 2 on the public-beta branch, v0.111.0 or newer.** The mod compiles against the game's own
  `sts2.dll` and Godot assemblies. They're proprietary, so they aren't in this repository and must never be
  committed; the build reads them from your installed game.
- **The [.NET 10 SDK](https://dotnet.microsoft.com/download).** The build scripts are .NET 10 file-based apps
  (`dotnet run build.cs`). The mod itself targets .NET 9, like the game.
- **Where your game is:** the build looks in the Steam library `D:\SteamLibrary\steamapps`. If yours is
  elsewhere, set the environment variable `STS2_STEAMAPPS` to its `steamapps` folder, for example
  `C:\Program Files (x86)\Steam\steamapps`. build.cs, the project and tools/dev.cs all read it.

## Building

- `dotnet run build.cs` builds and installs into the game's `mods` folder (close the game first).
  `--no-install` only builds, into `out/`.
- `dotnet run build.cs --dist` makes the release for the Workshop and never installs: `dist/SpireModManager/`
  holds just the `.dll`, `.json` and `.pck`, and the manifest gets `ReleaseVersion` (set by hand in build.cs).
- Player text lives in `localization/<lang>/*.json` and pictures in `images/`, packed into
  `SpireModManager.pck`.

## Trying it and testing

`tools/dev.cs` runs the game apart from your real play, so you can keep playing through Steam meanwhile. It
needs to know what it touches on your PC:

- **A dev copy of the game:** `sts2-dev` at the root of the game's drive (`D:\sts2-dev` for the default
  library), made of hard links to the installed game's files, so it takes no extra space. `STS2_DEV_DIR` picks
  another folder on the same drive (hard links can't cross drives). It has its own `mods` folder: the fresh
  build plus links to your other local mods. Builds never go into your real `mods` folder this way.
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
  - `--real` (maintainer only): a read-only tour with a copy of your own mods and data. Its output holds your
    own mod list, so it never goes into a pull request.
- `dotnet run tools/dev.cs launch`: opens the sandboxed game to try things by hand (`--first-run`,
  `--first-run-bmm` and `--workshop` as for `test`).
- `SPIRE_MOD_MANAGER_SKIP_PATCHES=ModToggledPatch` (patch class names, comma-separated) before `test` or
  `launch`: those patches count as failed, as after a game update that broke them, to see the game's page and
  its "needs an update" line.
- `dotnet run tools/pck.cs list|extract`: look inside the game's pack (scenes, images).

Only one dev copy can run at a time; `tools/dev.cs` won't start while one is running.

## Pull requests

1. Branch from `dev`, and open the pull request into `dev`. One change per pull request.
2. Run `dotnet run tools/check.cs` (seconds): text files parse, every text key the code uses is in `eng`, the
   fixtures are valid, and nothing over 1 MB or token-like is committed. CI runs the same checks.
3. For code changes, run `dotnet run tools/dev.cs test` and put its pass and fail counts in the pull request.
4. Say what a player will notice, in plain words. Screenshots are welcome: drag them into the description.

CI (`.github/workflows/ci.yml`) runs the checks and compiles the tool scripts on GitHub's Linux machines. The
mod itself can't build there, since the game's files aren't in the repository, so the build and the in-game
test only run on a PC with the game installed. On a first pull request from a fork, CI waits until the
maintainer approves the run. Copilot reviews each pull request; resolve or answer its comments before merging.

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
- `localization/`: player text, one folder per language. `images/` and `scenes/`: packed into the `.pck`.
- `tools/`: the dev scripts (`dev.cs`, `check.cs`, `pck.cs`, `shot.cs`) and the self-test fixtures.
- `workshop/`: the Steam Workshop listing and upload steps.
- `docs/spikes/`: write-ups of how the game behaves, done before building on it. `docs/dev/`: the
  maintainer's working notes.
