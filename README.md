# Spire Mod Manager

A mod manager for Slay the Spire 2, aimed at the Steam Workshop. Successor to Quick Relaunch (folded in).

Current state: scaffold. Adds a **Restart** button to Settings > Modding that relaunches the game in place,
so mod toggles apply without going through Steam (`NGame.Quit` saves first, then `OS.SetRestartOnExit`
starts a new copy with the same arguments; the child inherits `SteamAppId`).

- `affects_gameplay: false`, no dependencies.
- Build and install: `dotnet run build.cs` (needs the .NET 10 SDK; close the game first). `--no-install` only builds.
- Dev runs, apart from real play (a hard-linked game copy in D:sts2-dev with its own mods folder, no Steam,
  separate save data and log), so the game can be played through Steam meanwhile: `dotnet run tools/dev.cs test` runs the in-game
  self-test against a fixture (screenshots and UI dumps in out/selftest; add `--headless` for no window, dumps
  only); `dotnet run tools/dev.cs launch` opens the game in the sandbox to try things by hand.
- Player text lives in `localization/<lang>/*.json`, packed into `SpireModManager.pck`.
