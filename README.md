# Spire Mod Manager

A mod manager for Slay the Spire 2. It replaces the game's Modding page with one made for long mod lists:
categories, filters, profiles, a load order view, your own notes and tags, and a quick relaunch. The game's
own list stays one click away.

By symphony ([@jciurc](https://github.com/jciurc) on GitHub).

## Install

Subscribe to it on the Steam Workshop; the game installs it for you.

- **The public-beta branch, game version v0.111.0 or newer, for now.** On the stable branch the game won't
  load it. To switch: right-click Slay the Spire 2 in your Steam library, then Properties, Betas, public-beta.
- No other mods needed.
- It doesn't change gameplay, so the people you play multiplayer with don't need it.

## Features

- **Main menu button:** "Mod Profile: Default (12 mods)" in the lower-left corner names your active profile and
  opens the page. Right-click it to move it or to relaunch the game. A dot shows when a relaunch is pending.
- **Quick with long lists:** timed with about 250 mods, searching and switching views take a fraction of a
  second.
- **Categories:** sort mods into your own collapsible categories, from a button on each row or the mod's
  right-click menu.
- **Filters:** show All, Enabled, Disabled or Changed mods (changed since the game started), together with the
  search. Enable all and Disable all act on the mods shown.
- **Profiles:** save which mods are on and switch between sets. When the list differs from the saved profile,
  it shows "Unsaved" with Save, Save as new and Discard; hover it to see what changed.
- **Load Order view:** drag mods into order, one or several at a time. Lines show what each mod needs and what
  needs it, green where the order works and red where it doesn't. Fix now and Auto sort put things right.
- **Warnings that name the mods:** a banner says when enabled mods need a mod that's disabled or not
  installed, and Enable required mods turns on the ones you have.
- **Your notes on each mod:** an alias, notes and your own tags, plus automatic ones (New, Local, Library,
  Gameplay, Cosmetic).
- **Search:** names, aliases, notes, tags, authors, categories and Workshop tags; `#tag` and `@author` narrow
  it down. Ctrl+F jumps to the search box.
- **Select several mods** with Ctrl-click, Shift-click or Ctrl+A, then set their category, tags or notes, or turn
  them all on or off.
- **Changes, undo and redo:** the bar at the bottom counts what changed since the game started. Ctrl+Z and
  Ctrl+Y undo and redo; Revert all puts things back the way they were when you opened the page.
- **Relaunch Game:** restarts in place, in the same window mode, so changes apply without going back through
  Steam. It checks first for mods that won't load and offers to fix them.
- **Workshop details in the info panel:** dates, size, subscribers, tags, supported branches and a link to the
  mod's change notes, with problems shown first in red.
- **Vanilla List:** the game's own Modding page, unchanged, one click away.

**Better Mod Menu:** both can be on at once, and they're tested together. On first run Spire Mod Manager copies
Better Mod Menu's groups, aliases, notes and profiles (its own file is never changed). A warning says when more
than one mod manager is enabled, since both change the same page.

## Your data

Categories, aliases, notes, tags and profiles are saved in one file per Steam account. On Windows:
`%APPDATA%\SlayTheSpire2\steam\<your Steam ID>\mod_data\SpireModManager\manager.json`, with the last 10
backups in the `backups` folder next to it. It isn't in Steam Cloud, so it doesn't follow you to another PC or a Steam Deck. Which
mods are on, and their load order, stay where the game keeps them.

To uninstall, unsubscribe on the Workshop. The game's own Modding page keeps working, with your mods as you
last set them. Delete the data folder above if you want everything gone.

## Known limits

- Public-beta branch only, for now, until v0.111.0 or newer reaches the stable branch.
- Slay the Spire 2 is in Early Access, and a game update can break parts of the mod until a fix is out. If it
  breaks the page itself, you get the game's own Modding page, with a line saying the mod needs an update.
- Only Better Mod Menu has been tested alongside it. Use one mod manager at a time otherwise.
- Relaunch Game is tested on Windows only. Mouse and keyboard only; no controller support yet.
- Its own text is English only for now. Translations are welcome: see [TRANSLATING.md](TRANSLATING.md).

## Bugs and ideas

[Open an issue](https://github.com/jciurc/spire-mod-manager/issues/new/choose), or comment on the Workshop
page. For a bug, it helps to include your game version and branch, what you did and what happened, and your
log file, `%APPDATA%\SlayTheSpire2\logs\godot.log` (it can show your Windows user name in file paths).

## Contributing

Translations: [TRANSLATING.md](TRANSLATING.md). Code, building and testing: [CONTRIBUTING.md](CONTRIBUTING.md).

The watchdog (`.github/workflows/watchdog.yml`, `tools/watchdog.cs`) guards the public repo against floods.
Every 30 minutes it counts the pull requests, issues and comments that outside accounts made in the last hour.
Over 5 opened or 30 comments, it locks those conversations to collaborators and opens a `watchdog` issue
listing the stronger switches, such as limiting the whole repo to collaborators for a day. It runs only while
the repository variable `WATCHDOG` is `on`. Actions > Watchdog > Run workflow runs it by hand at any time: a
dry run by default, but with "dry-run" unticked it locks and reports for real.

## License

MIT (see [LICENSE](LICENSE)). Slay the Spire 2 and its files belong to Mega
Crit; none of them are included.
