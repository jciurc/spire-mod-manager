# Spire Mod Manager

A mod manager for Slay the Spire 2, aimed at the Steam Workshop. Successor to Quick Relaunch (folded in).

Current state: scaffold. Adds a **Restart** button to Settings > Modding that relaunches the game in place,
so mod toggles apply without going through Steam (`NGame.Quit` saves first, then `OS.SetRestartOnExit`
starts a new copy with the same arguments; the child inherits `SteamAppId`).

- `affects_gameplay: false`, no dependencies.
- Build and install: `python build.py` (needs the .NET 9 SDK; close the game first). `--no-install` only builds.
- Player text lives in `localization/<lang>/*.json`, packed into `SpireModManager.pck`.
