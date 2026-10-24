# Design

The decisions in force, and why. Ideas and plans are in the issues; the older notes are in git history.

## Principles

Ease of use is the heart of the mod, not just its utility.

- **Intuitive:** sorting, filtering and organizing the mod list should need no explanation.
- **Careful with data:** think through how it loads, merges, updates and resolves conflicts.
- **Nondestructive:** undo and redo, a diff of pending changes, save versus discard.
- **Fast:** heavy work (Workshop queries, scans) runs once and is cached; redraws use `Lookups`. What every row
  has shares one look (a `Theme` on it, not per-node overrides), and a row makes only the parts it shows. Fonts
  (`ModEntry.Font`, once per language) and the node paths, meta keys and theme names that per-row code passes to
  Godot are made once, not converted from text on each call.
- **Function first,** visual design after. Reuse the game's own elements; no AI-generated textures.
- **Clearly ours:** our views use more of the screen and smaller text than the game's page.
- **Leaving the game shows:** our links and menu items that open a web page or the system's file browser have the
  external-link mark after their text (`LinkMark.cs`); not the copy of the game's Get Mods button, nor the game's popups.

## The game's data comes first

- The vanilla modding page keeps working untouched, one click away (the link by our page's title).
- The game's `mod_list` is the truth for what's on and the load order. Edits go to it at once and take effect
  on relaunch; nothing of ours can disagree with the game's tickboxes.
- One exception, because the game has one switch for them: copies of a mod installed twice from one source (two
  Workshop items). Which of them are ticked is ours (`copies_off`). Which copy loads is worked out at every launch,
  never stored, and only the ticked copies count: a copy that's off never beats a ticked one, and loads only where no
  ticked copy can (on through a ticked twin's switch, it still takes part in the game's sort, so it can hold the id's
  first slot where the ticked copy can't take it). First the game's local-vs-Workshop rule over the ticked copies (where the game's own
  rule, which sees only the switches, set aside the copy this leaves, the launch brings it back), then of one source
  the highest ticked version (the copy installed first on a tie). Where that copy can't take
  the id's first slot (something it needs that's on sorts after the slot) while a mod still to load needs the id, the
  copy the same rule picks without it, and without any other copy that can't take the slot (one needing a mod that's
  missing, off or after the slot, or below a minimum version a mod still to load needs), loads instead; none: the first
  copy left that can, else the first, both by the tie order (the game's sort can put copies of one id either way
  round). A dependency that's off or missing never stops the copy chosen: it loads and fails, and the dependency
  warnings say so, as for a mod installed once. It all runs in our Init, before the game tries the copies. Our page
  works out where the slot falls from the next launch's order: the game sorts by the saved list it rebuilt from the
  order that loaded (saved at the main menu, in the order rule's form: Load order), ranking an id by its last entry,
  then puts what a mod needs first (the first copy of it the game reads). Our page has a tickbox per copy, and two
  copies of an id can both be ticked (only one loads). The game's page keeps its one switch.
- Where the page and the launch can still differ, or older data reads differently (all rare):
  - Data from the dev builds before this, whose pick was missing: the copy standing in for it stays ticked, so once
    the pick is back both are ticked and the higher version loads, where those builds loaded the pick. A stand-in
    can't be told from a copy ticked by hand.
  - A session that can't save our data ties by the file's record order, as the next launch does. But a copy new to
    the record that ties with the one there, installed with the game closed, goes by the copy the last launch had on
    the page, and by the items' age at the next launch, whose launch record names both.
  - With two local copies and a Workshop one, the game's rule can leave copies of both sources. Our choice goes by the
    source of the first ticked copy in the order of the id's copies this launch had before its swap (the copies the
    rule leaves first, then those it beat), which the page takes as the next launch's. The game's sort ties copies of
    one id and doesn't keep their order, so anything that changes the sort around them (a mod installed, removed,
    switched on or off, or moved) can reorder them, and the page can then be wrong for one launch. A blocked copy's
    fallback goes by the tie order within each of the two, on the page and at the launch, so it isn't affected.
  - Whether the next launch can put a copy in the id's first slot: the page reads the saved list as it is now. We save
    the game's rebuild at the main menu, in the order rule's form (the game itself later, if at all: leaving its
    settings screen, remapping a control, quitting); after a session that ends before that (a crash at launch), the
    next launch reads the list from before and sorts as this launch did, so a copy blocked this launch can be blocked
    again while the page named it.
    Ids the list doesn't have yet tie in the game's sort, which the page takes in the game's list order. The game reads
    Workshop items in Steam's order, which the page has from launch: one subscribed since comes after the others there,
    wherever Steam puts it. A mod uninstalled since still counts, as the game's list keeps it.
  - A copy in a dependency cycle, or waiting on one, can make the game fail every copy of the id (its cycle check goes
    by id), whatever the page and the launch choose.
  - An id the launch couldn't choose for (the canary failed, or the self-check found the choice not taken): the page
    names the copy that loaded, where the next launch can load it again (else the first copy the game's rule leaves).
    An order change since (a mod it needs moved after the id) can make the next launch load another.
  - A Workshop item with two manifests of one id in one folder, read while the game runs (installed or updated), counts once until
    the next launch. That launch reads both and so checks whether the copy can take the id's first slot, where the page
    doesn't, so the page can name a copy that launch blocks.
  - On the beta, the page compares a Workshop copy with the local copy's newest manifest, where the game compares it
    with the first it read. They differ only where a local folder has two manifests of the id whose versions read
    differently.
  - Where no ticked copy can take a blocked copy's place, each other copy counts by its newest manifest. A Workshop item
    with manifests of the id in two folders whose newest needs a mod after the slot is then put in the slot and fails,
    though its older manifest could have loaded there. Two manifests of one copy at the same version go by the list
    order, which the page takes from the next launch's predicted order and the launch from its own, so the two can
    differ when the game's sort puts them the other way round.
  - A copy whose entries sit with another source's before what it needs (two Workshop copies, off, beside a local copy
    that loads; one of them ticked later) is blocked at the next launch, and loads at the launch after, once the mod
    ranks where that copy sorted. The page names the copy loading instead meanwhile.
- Profiles key mods by id and source, like `mod_list` (plus, for copies from one source, which are ticked). A shared
  list's rows set a mod installed once by its id; where its id is installed more than once, a row with a Workshop
  link sets that copy alone. A newly installed mod joins the active profile, enabled.
  With no profiles yet, an ordinary one named "Default" is made from the current list.
- The mod managers (this one, Better Mod Menu) are never turned on or off by a profile.
- Spire Mod Manager is first in whichever category it's in. Its copies have a box each, like any other mod's (its
  copies from one source share one). Disable all leaves it on; its own box, a selection that has it and the bulk menu
  can turn it off. With every copy of it off, a red banner says so and Relaunch asks first, both with Enable now for
  the copy running.
- Its copies get the same messages as any mod's, and the same clash warnings with two of them ticked. Which of them
  loads is the game's own choice, as it loads before it could choose: between sources the game's rule, with its
  reason; between copies from one source, with none.
- Our own data is one JSON file per Steam account, written atomically, with 10 backups.

## Load order

- One global order, in its own view. Disabled mods are greyed out but can still be moved (the game's sort puts a mod
  with no copy on after all the rest, so such a move lasts until the next launch).
- Spire Mod Manager always loads first, pinned at the top: its Init has to run before any copy it chooses for.
- Fix now moves dependencies ahead of what needs them, otherwise keeping the order (the game's own rule).
- Auto sort: libraries, then gameplay mods, then the rest, then the same dependency sweep; the player's order
  holds within each tier.
- A mod installed more than once is one place in the order: the game ranks every copy of an id by the id's last saved
  entry. Its rows sit together and move together (a drag of any of them, the menu's moves past another mod's whole
  block, a selection), a drop among its own rows orders only its copies, and a drop never splits another mod's rows.
  Fix now and Auto sort gather a mod's rows at its copy that loads. A copy installed while the game runs shows at the
  end of its mod's rows, where its entry goes and the game sorts it.
- The order rule: every write of the saved list keeps each mod's entries in one run, in their order, where its last
  anchor is, an entry of a copy the game sorts (a mod with none: where its first entry is), so the mod ranks there
  (`LoadOrder.Normalize`). The game's rebuild lists the copies it took out before sorting (off, or set aside by its
  local-vs-Workshop rule) after all the rest, so without it a mod with such a copy would rank last at every launch. At
  the main menu the anchors are where the game put each mod this launch (`SameIdLoad.LaunchAnchors`; where the list
  doesn't line up with the game's mods, by source, with a warning); in every other write, which sources the next launch
  sorts (`LoadOrder.Anchors`). Our writers keep it (the main menu, every write in the session, the launch record,
  Restore's list), and from the first main menu on, a prefix on the game's own settings save keeps it for every other
  writer. A new entry for a mod the list has goes right after its entries (`LoadOrder.Add`). The main menu saves the
  list only where it differs from the one the launch read, so a launch that keeps the order saves nothing.
- At the main menu, a mod whose copy this launch blocked (it needs a mod that loads after the id's first slot), or
  brought back in its own place behind such a mod, goes right after that mod's entries, where the game's sort would
  have put the copy, so it can take the slot at the next launch (`SameIdLoad.Heal`; a mod whose own copy waits too
  moves first). The launch record keeps the order that loaded, without it, so Restore brings back that launch.
- Where the order can still be off (all rare):
  - The first launch after updating to this version sorts by the list from before, where a mod with a copy taken out
    ranks last. Its main menu keeps the mod's entries together where it loaded, so it keeps that place from then on,
    and one drag puts it anywhere.
  - A list another mod rewrote before the main menu loads that way, and the mod keeps the place it loaded at. A
    rewrite after it is put in the rule's form as the game saves it.
  - A mod with every copy off loses its place at each launch: the game sorts it after all the rest, and the rule
    leaves a mod with no copy loading at its first entry there.
  - A drag of a mod above a mod that one of its copies needs, where that copy doesn't load but takes part in the
    game's sort (a copy that's off beside a ticked one of the same source), holds for one launch: the game sorts that
    copy after what it needs, and the mod ranks there from the next main menu on.
  - Restore from a launch record kept before this version puts the copies taken out beside the copy that launch put
    in the mod's first slot. On the beta, with two copies of one source either side of the game's rule, that's one
    launch off.

## Code

- Few, flat files, one per feature or view.
- Fail soft: every patch body runs in try/catch; if a patch breaks after a game update, the game's own page
  shows, with a line saying the mod needs an update.
- `affects_gameplay: false`. Anything that changes gameplay belongs in another mod.
- Player text lives in the loc tables, merged into the game's own (`SPIRE_MOD_MANAGER.` keys). Missing keys
  fall back to `eng` one by one, and `tools/check.cs` rejects keys that `eng` doesn't have.
- Translations in the repo come from native speakers. Machine translations can be hosted elsewhere: a player's
  own file in `mod_data` is merged over the packed one when the game loads a language.

## Releases

- Both branches, stable and public-beta, game v0.107.1 or newer: one build, with what only one branch has
  reached by name (`Compat.cs`), and the release test run on each (RELEASING.md).
- Each first upload goes up private, gets tested, then goes public (unlisted as a soft launch).
- Workshop pictures are real screenshots, never generated art.
- Mouse and keyboard only for now; controller support is planned.
- Open source under MIT. Bugs go to GitHub issues; Workshop comments get read too.
- The main menu button is the game's own text button ("Default (12 mods)"), with no picture; its right-click
  menu is headed "Spire Mod Manager v<version>".
- F3 (the game uses it only as a debug key in its trailer mode) opens the modding page from the main menu and goes back from it (anything of ours
  that's open closes first, as with Escape); nothing on other screens or while typing. One constant for now.
- Left to other mods: multiplayer host sync, forced A-Z ordering, a grid view.
