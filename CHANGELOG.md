# Changelog

What changed in each Workshop release, for players. Each version's first line is its Workshop change note
(`changeNote` in `workshop/workshop.json`), word for word.

## 0.1.1 (2026-10-16)

0.1.1: the main menu button's hover wings show again alongside RitsuLib, now in pink; the author in the info panel reads as a link; and the Changed filter's tip says what it shows.

- The main menu button's hover carets are its own now, so a mod that runs the main menu's buttons (RitsuLib's main
  menu scroller) no longer hides them. They're pink, set apart from the game's gold.
- The author in a mod's info panel is light blue, like the mod names there: a click searches for their mods.
- The Changed filter's tip lists what it shows: mods enabled or disabled, installed, moved, or edited since launch.

## 0.1.0 (2026-10-16)

0.1.0: first release. Compatible with stable or beta-branch (game v0.107.1 or newer).

Spire Mod Manager expands the game's Modding page for long mod lists (the game's own page stays one click away):

- Your own collapsible categories, search over names, notes, tags and authors, and filters for enabled,
  disabled and changed mods.
- Profiles of which mods are on, with unsaved changes you can save, save as new or discard; export and import
  them as a list.
- A Load Order view: drag mods into order, see what each needs, Fix now and Auto sort.
- Aliases, notes and your own tags on each mod; Workshop details in the info panel.
- Warnings that name the mods needing a disabled or missing mod, and a check before relaunching.
- Changes since launch, undo and redo, and Revert all.
- Relaunch Game restarts in place, and a notice after a launch that didn't reach the main menu offers to go
  back to the last setup that worked.
- Runs alongside Better Mod Menu, and imports its groups, aliases, notes and profiles on first run.
