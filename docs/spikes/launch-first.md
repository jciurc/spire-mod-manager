# Spike: loading first (crash-safe launch, and loading without a restart)

Status: proposal, 2026-10-13. No code changed. Game facts are from the installed v0.111.0 beta's `sts2.dll` (commit
41cef1ea), decompiled with ilspycmd; their line numbers are the decompile's. Ours are `dev`'s (34d91cb). Nothing here
was run in game: it all comes from reading code, and the first PR's checks are where it gets tested.

## The problem

Two of Jordan's round 21 items, spiked together as the backlog asks ("spike them together"):

- **Crash-safe launch** (JORDAN, important). A launch that never reaches the main menu leaves a marker. At the next
  menu, a notice lists what changed since the last launch that worked, with "Restore last working setup" and "Keep".
  After two failed launches in a row, roll back once on our own and say so. Never loop.
  - RELEASE-PLAN §2 item 14: this spike is a 0.1.0 blocker. Building it is one too if the spike shows it's small.
- **Load mods without a restart** (JORDAN: "one of the biggest pain points as a player"), per kind of mod: PCK only,
  Harmony only, content.

Both stand on the same groundwork: loading before every other mod, and steering the rest of the launch from there.
Round 21's finding says Jolyne-kujo's STS2-ModManager does that with a Harmony prefix on `ModManager.TryLoadMod`.

## What happens today

### How the game loads mods

1. `NGame.GameStartup` (`NGame.cs:649-716`) awaits `OneTimeInitialization.ExecuteVeryEarly`. That reads
   `settings.save`, then awaits `ModManager.Initialize` (`ModManager.cs:74-157`).
2. `Initialize` finds the mods (the `mods` folder, then Steam subscriptions) and checks Workshop branch support. It
   marks the mods that are off in `mod_list` as `Disabled` (`RemoveDisabledMods`), and sorts the rest (below).
3. **The loop:** `foreach (Mod mod2 in _mods) TryLoadMod(mod2);` (`ModManager.cs:124-127`), one mod at a time on the
   main thread. The log shows "Loading assembly DLL", "Loading Godot PCK", "Calling initializer method" and
   "Finished mod initialization" for each. Ours loads 20th of 28 in Jordan's game today.
4. **The rebuild:** after the loop, `Initialize` replaces `mod_list` with a new list: the mods in load order, then
   the ones that are off (`ModManager.cs:135-151`). On/off is copied from the old list by id only, and entries for
   mods that aren't installed are dropped.
5. Straight on, with no frame drawn in between (`NGame.cs:651-661`): the dev console, save migrations, cloud sync,
   then `ExecuteEssential`: localization (mods' text merged in), `ModelDb.Init` (every mod's models created), net ids.
6. Audio, profile and progress, then the main menu: `NMainMenu._Ready` runs (our `MainMenuPatch`). After that, in
   the background: the remaining atlases and `ModelDb.Preload` (failures there are only logged).

**One mod's turn, `TryLoadMod`** (`ModManager.cs:623-908`):
- A mod whose `state` isn't `None` returns at once and is announced through `OnModDetected` (`:625-629`). That is how
  a mod that's off gets skipped.
- Checks: game version, a duplicate id, dependencies already `Loaded`. A failure marks it `Failed`.
- Its DLL goes into the game's own `AssemblyLoadContext` (`:802`), most likely the default one, which can't unload.
  Its PCK is mounted with `ProjectSettings.LoadResourcePack`.
- Each `[ModInitializer]` method runs; with none, Harmony's `PatchAll`. Then `state = Loaded` and `OnModDetected`.

**Sorting, `SortModList`** (`ModManager.cs:193-317`): dependencies first. Among the mods that are ready, the lower
index in `mod_list` goes first. That index is looked up by id only, and a mod listed twice keeps its *last* place
(`:236`). A mod missing from the list gets 999999999, after every listed mod. Mods that are off go at the end.

### What a failed launch looks like

| What goes wrong | What the game does | Reaches the menu |
|---|---|---|
| A mod's initializer (or its `PatchAll`) throws | caught: the mod stays `Loaded` with a "failed to load" error; patches it made before throwing stay | yes |
| Its DLL's types or its PCK fail to load | caught: the mod is `Failed`, the next one loads | yes |
| A mod's model constructor throws (`ModelDb.Init`), or its localization file is broken | the game's startup error popup, then quit (`NGame.cs:574-641`); a broken localization file quits with no popup | no |
| A native crash, a stack overflow, `Environment.FailFast`, an exception on a mod's own thread | the process dies | no |
| A mod hangs in its initializer | "Not responding", until the player kills it | no |
| The player closes the window while it loads | `NGame.Quit` (the close request, `NGame.cs:970-976`): settings saved, then quit | no |

- So the usual bad mod, one that throws in its initializer, never stops a launch. A launch stops on a content or text
  error after every mod has loaded, or on a crash or a hang inside one mod.
- The startup error quits the tree directly (`NGame.cs:615-639`); a close goes through `NGame.Quit`. So the two can
  be told apart.
- The startup error also raises `TaskHelper.UnobservedFault` (`TaskHelper.cs:14`) with the exception. Any mod can
  subscribe to it.

### settings.save while the game runs

- One live object, `SaveManager.Instance.SettingsSave.ModSettings`, written from memory by `SaveSettings`: on quit,
  on a settings change, by the consent popup, and by us (`LoadOrder.Save`, `History.Apply`, `Profiles.Apply`,
  `Dependencies.Set`). An edit to the file on disk while the game runs is overwritten.
- Local only: settings are kept out of Steam Cloud on purpose. The write is a backup copy, a temp file, then a rename.
- `mod_list` is read only at startup. Mid-session only the Modding page reads it.
- So "restore the last working setup" means: change the live list, save, relaunch (`ModEntry.Relaunch`). History,
  Profiles and Load Order already work this way.

### Our mod

- `ModEntry.Init` (`ModEntry.cs:66-100`): watches for new mods (`Installed.Watch`, an `OnModDetected` handler), then
  applies our patch classes one by one.
- `MainMenuPatch` (`Patches.cs:25-59`) is a **postfix** on `NMainMenu._Ready` (`Priority.First`). It loads our data,
  takes the loaded order as the baseline (`LoadOrder.RememberLoadedOrder`), starts History and adds our button.
  - If the game's `_Ready` throws, or another mod's prefix does, Godot logs it and the menu still shows, but
    postfixes don't run.
- **Nothing keeps us anywhere in `mod_list`:**
  - Auto sort puts us last: we're `affects_gameplay: false` (`build.cs:35`), its third tier (`LoadOrder.cs:435-443`).
  - Undoing a move restores the old order (`History.cs:212-224`).
  - Profiles and Enable/Disable all hold us on (`Profiles.cs:265`, `Dependencies.cs:64`), but not in place.
  - On a first install we're not in `mod_list`, so we load after every listed mod.
- **Our data folder** is account-scoped (`Store.cs:92`). `Store` reads it on first use, not in `Init`, because "the
  account folder needs Steam's user id" (`Store.cs:86`). But the game reads `settings.save` from that same account
  folder before any mod loads, so it's known by `Init` (the first PR checks it).
- **Patterns to reuse:** `Relaunch` (`ModEntry.cs:200-214`: `OS.SetRestartOnExit`, then `NGame.Quit`); the relaunch
  check's dialog (`RelaunchCheck.cs:39-70`); the unreadable-data notice, timed after the game's own popups
  (`Patches.cs:43-50`).

### Other mods that load first

- **STS2 Mod Manager 启动前模组管理** (Jolyne-kujo/STS2-ModManager, MIT, mod id `ModTheSpire2`). Read from source:
  - Its bootstrap has no `[ModInitializer]`, so the game runs its `PatchAll`. That patches `TryLoadMod` with a prefix,
    found by name (`AccessTools.TypeByName("MegaCrit.Sts2.Core.Modding.ModManager")`).
  - Still inside its own turn, it shows a native Win32 picker with its own message loop, so the game's loop can't move
    on until it closes. It saves the choice to `mod_list`, and sets the mods ticked there from `Disabled` to `None`.
  - Its prefix then skips every later call from the game's loop, and **replays** the chosen mods itself, calling
    `TryLoadMod` by reflection in the picker's order.
  - It must be first: its README says to move it to the top by hand, and from then on it saves itself at index 0.
  - Gaps, from reading the code:
    - the picker's order probably doesn't survive a normal quit (the game's rebuild writes the old order back);
    - the replay ignores dependency order;
    - mods unticked there never reach `TryLoadMod`, so their state stays `None`;
    - only `TargetInvocationException` is caught around the replay.
  - The picker shows on every launch, in Chinese, on Windows only.
- **ModTheSpire2** (grassdog0/MTS2, no licence, the same mod id): an external launcher (an .exe) that rewrites
  `settings.save` before starting the game, plus an in-game companion. Its "hot apply" only flips enabled flags and
  saves; it loads nothing mid-session.
- **Mod Launch Manager** (3788556097, 4.2k subscribers, closed source; from its description): a screen inside the game
  before other mods initialize, and newly found mods load in the same launch. It keeps itself first in the saved
  order, and has an experimental option that skips and unloads a DLL mod whose initialization fails. It doesn't say
  how.
- **Mod List Hot Reload** (3794780403, 32 subscribers, closed source): loads new DLL and PCK mods when you leave the
  mod list, through its own collectible `AssemblyLoadContext`. Its description lists what still needs a restart:
  Godot scripts, model registration, cached PCK content, native libraries, framework chains.
- **MOD管理器** (xiaozz, 136 subscribers): a one-time rollback to the last working setup after a startup crash
  (DESIGN.md's survey). No source.

## Findings

### Loading first: what the game allows

| We want to | The game allows it | Evidence |
|---|---|---|
| load before every other mod | yes: no dependencies, and index 0 in `mod_list` | `SortModList` |
| see the whole planned order from our initializer | yes: `ModManager.Mods`, already sorted | `ModManager.cs:46` |
| hear how each later mod went | yes, with no patch: `ModManager.OnModDetected`, after every attempt | `ModManager.cs:60` |
| keep a later mod from loading, this launch | yes, with no patch: set its `state` (a public field) to `Disabled` before its turn | `ModManager.cs:625-629` |
| keep it off next launch too | yes: its saved entry's `IsEnabled = false`; the rebuild keeps it (by id) | `ModManager.cs:135-151` |
| turn on a mod that's off, this launch | badly: it sits at the end, outside the dependency sort | `SortModList` |
| load the rest in another order, this launch | only with a `TryLoadMod` prefix and a replay (Jolyne's way) | `ModManager.cs:124-127` |
| change the order for next launch | yes, after the rebuild: at the main menu | `ModManager.cs:146` |
| show our own screen before the rest load | only a native window: no frame is drawn before `ModelDb.Init` | `NGame.cs:651-661` |
| load a mod after startup | no: `TryLoadMod` refuses, and the mod is `AddedAtRuntime` | `ModManager.cs:607-621` |
| unload a DLL or a PCK | no: no collectible context, and Godot 4.5 can't unmount a pack | `ModManager.cs:802` |

**Being first, in practice**

- A mod with no dependencies at index 0 is the first one out of the sort. We declare none (`build.cs:36`).
- **First install:** we're not in `mod_list`, so we load after the listed mods. We move ourselves to index 0 at the
  first main menu, and that counts from the second launch. A first launch is never covered.
- **Two copies of ours** (Jordan's local build and the Workshop one, or any player with both): the game keeps the
  *last* place for an id, and its rebuild moves the duplicate copy to the end every launch. So every entry with our
  id has to go to the front, every session.
- **Others that write the order** can move us: our own Auto sort and undo (above), Better Mod Menu's arrows, Load
  Order Manager, ModListSorter (A-Z on every exit), LoadOrderSync (at launch), MTS2's launcher (before launch).
  - Putting ourselves back at each main menu covers all of them except the ones that write on exit or before
    launch. With those, we load wherever they put us.
- **Two mods that both pin themselves first** (Mod Launch Manager, Jolyne's) most likely take turns: whichever saved
  last is first next launch. Right behind one of them, we still cover every mod after us.
- If the player hasn't agreed to load mods, nothing loads, ours included.
- Loaded first, our initializer runs before libraries such as BaseLib. It touches no other mod (Better Mod Menu is
  reached later, by reflection), so that's fine.

**Holding a later mod back: two ways**

1. **Its `state`, with no patch.** Set to `Disabled` before its turn, the game skips it exactly as it skips a mod
   that's off: no DLL, no PCK, no initializer, no models, no text.
   - Mods that need it fail with the game's own "needs X" error, which our relaunch check and banner already explain.
   - Nothing is added to or removed from the list the loop walks (that would throw).
2. **A prefix on `TryLoadMod`** (Jolyne's way). Made during our turn, it catches every later call: the loop is
   sequential, and the method is too large to inline. It's only needed to load in another order this launch, or to
   replay after a picker.
   - Every later mod passes through it. An exception that escapes it reaches `Initialize`, so the game shows its
     startup error. Since we're first, that repeats every launch until the player removes us.
   - A logic slip that returns false skips every later mod without a word. Jolyne's returns false for every later
     call once configured, even when its replay couldn't find the method to call.
   - Two gates, theirs and ours, on one private method is the riskiest pairing there is.
   - It reaches a private method by name, so a game update that renames `TryLoadMod` turns it off. Our per-class
     patching already copes with that.

Crash-safe launch needs only the first way.

### Crash-safe launch

**What the next launch can know.** Loaded first, our initializer can keep a record of the launch in our data folder:
- the planned order (`ModManager.Mods`), with each mod's on/off and version, and its DLL's and PCK's size and date
  (that catches an update that didn't change the version). The version comes from `manifest.version`: `mod.version`
  is only set in the mod's own turn;
- one line per finished mod, from `OnModDetected`, appended and flushed. A killed process keeps what it flushed, so
  no fsync is needed;
- the exception, from `TaskHelper.UnobservedFault`, and the mods it names (an assembly in its stack trace, or a
  `res://<id>/` path in its message);
- "closed" if `NGame.Quit` runs before the menu;
- cleared when the menu is reached, by a **prefix** on `NMainMenu._Ready`. `MainMenuPatch` is a postfix, which a throw
  in the game's `_Ready` would skip, leaving a launch that worked marked as failed.

At the menu we also save the setup that worked: the saved `mod_list` right after the game's rebuild (the order it
really loaded in), with the versions and file dates.

So after a failed launch, the next one knows:
- **that** it failed, and how:
  - it died or hung inside a mod: named, as the first planned mod after the last finished one;
  - the game's startup error: with the exception, which often names a mod;
  - it died after every mod had finished (a native crash while registering content): no mod named;
  - or it was closed;
- **what changed** since the last launch that worked: mods new, turned on or updated (and mods turned off, or moved,
  which can't be undone within a launch);
- whether **we** were first. Mods before us are outside the record.

**When the next launch can act.** Our initializer runs before the rest, so a decision made there applies to that
same launch: hold the suspects back through their `state`, turn them off in the saved list, save. No relaunch, ever.

**The sketch's timing, with a crash that repeats every launch:**
1. A new mod crashes launch 1.
2. Launch 2 has the same mods, so it crashes the same way. The notice after one failure is never seen.
3. Launch 3: two in a row, so the rollback holds the new mod back, and the menu says so.

- The notice after one failure only shows when the next launch gets through anyway: a one-off crash, a close, or a
  fix made outside the game (an unsubscribe in Steam).
- The player sees two crashes before any help. When the record names the mod (the launch died inside it, or the
  error names it), launch 2 could hold back just that mod: one crash instead of two (question 2).
- With closes recorded as closes, the second failure isn't needed to rule out a player closing the game (question 3).

**What a rollback can undo within a launch, and what it can't.**
- Can: turn off what's on now and wasn't then (new mods, mods turned on), and turn off updated mods. Steam only has
  the new version, so off is all it can do for those (question 6).
- Can't: turn back on what was turned off since (it would load outside the dependency sort), or put back the order
  (the game's rebuild replaces it). Those need the menu and a relaunch: that is the notice's "Restore last working
  setup".
- Can't find a suspect when nothing changed and no mod is named, as when a game update broke a mod's content. Then
  there is nothing to hold back (question 4: a one-off safe mode).

**Never loop.**
- We never relaunch on our own. Every automatic step happens inside a launch, before the mods it affects load.
- At most one automatic rollback per run of failed launches. A launch that reaches the menu ends the run.
- On our own we only turn mods off. Turning on and the order are the player's, from the notice.
- A record we can't read counts as none: a normal launch. All of this runs in one try, so an error loads everything.
- We never hold ourselves back. An update of ours can't be undone from inside it.

**Cost:** one small write in our initializer, a line per mod, then one write and one delete at the menu. A few
milliseconds with Jordan's 28 mods; with 250, worth timing (`--real`).

### Loading without a restart

The game never loads a mod after startup. A mod installed while it runs is marked `AddedAtRuntime`, and the game
shows its "mod not loaded" popup (`ModManager.cs:607-621`, `NGame.cs:1313`); `TryLoadMod` refuses once `Initialize`
is done. Anything mid-session would be our own loader doing later what `TryLoadMod` does.

| Kind | On mid-session | Off mid-session |
|---|---|---|
| PCK only (art, text) | Mounting works. Its text needs the mod marked `Loaded` and the language reloaded (`LocManager.SetLanguage`). Art the game already loaded (atlases, menu art) keeps the old version. | Impossible: Godot 4.5 can't unmount a pack. |
| Harmony only (UI, QoL) | Load its DLL into the game's context, then run its initializer or `PatchAll`, as `TryLoadMod` does. It works only if the initializer doesn't assume startup: methods it patches that already ran (the menu's `_Ready`) won't run again, pool additions throw "too late", and a call the JIT already inlined misses the patch. | `Harmony.UnpatchAll(owner)` removes its patches, found by owner (`GetAllPatchedMethods`, `GetPatchInfo`, then the patch's assembly to the mod). Its event subscriptions, nodes and static state stay, and so does the DLL. A second load of the same DLL in one session can't replace the first. |
| Content (cards, relics, characters, powers) | Not practical: `ModelDb`, the pools, net ids and many caches are built once; characters are a fixed list; libraries such as BaseLib keep registries of their own; and a late net id breaks multiplayer with anyone who loaded at startup. | Not practical: saves, progress and runs refer to the models by id. |

- **Telling the kinds apart** without loading a DLL: the manifest (`has_dll`, `has_pck`, `affects_gameplay`, any
  dependency on a content library), and a metadata scan of the DLL (`System.Reflection.Metadata`, part of the
  runtime) for model classes and calls such as `ModHelper.AddModelToPool`. It's a heuristic: a mod that registers
  content through a library of its own looks like Harmony only.
- **A choice made at launch** (the backlog's "a change made at launch needs no second launch") needs a screen before
  the rest load. No frame is drawn between the loop and `ModelDb.Init`, so a Godot screen can't show there.
  - Jolyne's answer is a native Win32 window: Windows only, with the game's window frozen behind it.
  - How Mod Launch Manager shows its screen inside the game is unknown.
  - A choice without a screen does work: a decision made earlier, or a key held at launch, applied by holding mods
    back through their `state`.
- **What the others reach:** MTS2's hot apply loads nothing, and its own notes conclude that true hot loading isn't
  supported. Mod List Hot Reload uses a collectible context of its own and lists what it can't do; Mod Launch Manager
  calls its unload "best effort". Neither has source to read.
- Whatever can't apply now, the page should say exactly which changes still need a relaunch: the relaunch spike's
  "After a relaunch" card (`docs/spikes/relaunch-messaging.md`).

## Options

### The groundwork

1. **Stay first, hold mods back through their `state`, no patch on `TryLoadMod`.**
   - Pros: public members only, nothing of ours between the game and every later mod. Works beside Jolyne's gate: a
     mod we've set to `Disabled` returns at once even when its replay calls it.
   - Cons: no other order and no extra mods within a launch.
   - Effort: small (about 60 to 80 lines: staying first, the pin on Load Order, our place left out of the pending
     rule; 5 checks).
2. **Option 1, plus a `TryLoadMod` prefix and a replay** (Jolyne's way; MIT, reusable with its notice).
   - Pros: any order and any set of mods within a launch: the base for a picker at launch.
   - Cons: the risks above, and nothing in crash-safe launch needs it.
   - Effort: medium, plus the picker. Only ever together with a picker.
3. **Don't load first.**
   - Pros: no contest for the first place.
   - Cons: crashes in the mods before us leave no record, and a rollback needs a relaunch (a new way to loop).

### Crash-safe launch

1. **Record and notice only**, nothing automatic.
   - Pros: nothing can go wrong within a launch.
   - Cons: a crash that repeats never reaches the notice (the timing above). It isn't the feature.
2. **Jordan's sketch:** the notice after one failed launch; after two, a rollback within the launch of what changed
   (the groundwork's hold-back), once, and said at the menu.
   - Pros: the sketch as written; no relaunch of our own; undone from the notice with one click.
   - Cons: two crashes before help. It finds no suspect when nothing changed.
   - Effort: medium. About 350 to 450 lines in three PRs (see the recommendation), plus a test run over several
     launches.
3. **Option 2, and hold back a named mod after one failure.**
   - Pros: one crash, not two, in the usual case (a mod that crashes while it loads, or breaks the content step).
   - Cons: a named mod can be a victim (another mod's patch broke it). The notice offers it back with one click.
   - Effort: option 2 plus about 20 lines.
4. **Roll back by rewriting `mod_list` and relaunching** (without loading first).
   - Cons: an extra launch every time, and a relaunch we start on our own is exactly how a loop starts.
   - Not recommended.

### Loading without a restart

1. **A relaunch stays the way**, and the page says exactly what it will apply (the relaunch spike's card).
   - Effort: in that spike.
2. **Harmony-only mods on and off mid-session**, opt-in per mod and labelled experimental.
   - Pros: the QoL mods players toggle most.
   - Cons: each mod's initializer is untested that late; a mod is half applied until the relaunch; reports about
     other mods' behaviour land on us; off is only partial.
   - Effort: medium to large (a classifier, a loader that mirrors `TryLoadMod`, patch owners for off, the page's
     wording, fixture DLLs), then trials with real mods.
3. **PCK-only mods mid-session.**
   - Pros: mounting is simple.
   - Cons: on only, never off; art already loaded doesn't change, so it looks broken.
   - Not recommended on its own.
4. **A picker at launch** (a native window, plus the groundwork's option 2).
   - Cons: Windows only, not our look, on every launch; Mod Launch Manager and Jolyne's already do it.
   - Not recommended.

## Recommendation

- **Groundwork:** option 1. Stay first, hold mods back through their `state`, and patch nothing on `TryLoadMod`. Sit
  right behind another launch-first manager rather than fight it (question 5).
- **Crash-safe launch:** option 2, plus option 3 if Jordan agrees (question 2). Three PRs:
  1. `launch-record`: staying first, the launch record and the last working setup; log lines only. Small (about 200
     lines with the groundwork, plus the crash fixture and the run over several launches in `tools/dev.cs`).
  2. `launch-rollback`: the rollback after two failed launches, and its notice with "Turn them back on". Small to
     medium.
  3. `launch-notice`: the notice after one failed launch, with "Restore last working setup" (and relaunch) and
     "Keep". Small.
- **0.1.0 vs later:** the whole is medium, so by RELEASE-PLAN's rule it ships in the first update. **0.1.0 gets PR 1**
  (question 1):
  - the rollback only helps once we're first, which takes one launch after install. With PR 1 in 0.1.0, the update's
    rollback works from its first launch;
  - Jordan's own play before the release shows whether the record names the right mod, before anything acts on it.
- **Loading without a restart:** option 1 now. Option 2 after the rollback ships, first as a prototype on fixture DLLs
  and a few of Jordan's QoL mods, with no promise in the Workshop description until it holds up. Content mods stay
  restart-only.
- **Licences:** none of this copies code. If a replay gate is ever built, Jolyne's `StartupLoadGate` (MIT, "Copyright
  (c) 2026 Jolyne-kujo") can be reused, keeping its notice in the file and in a THIRD-PARTY-NOTICES file. MTS2 has no
  licence: its behaviour only, as here. Mod Launch Manager and Mod List Hot Reload: their Workshop descriptions only.

## Wording

| Where | Proposed |
|---|---|
| Load Order, our row's pin (hover) | Spire Mod Manager always loads first, so it can tell which mod a launch stopped in. |
| After a rollback: title | Your last 2 launches stopped before the main menu |
| Its text | To get you here, the mods changed since the last launch that worked are turned off: |
| Its lines | Name (new) / Name (turned on) / Name (updated: 1.2.0 → 1.3.0); then "It stopped while loading Name." when known |
| Its buttons | Turn them back on / OK |
| After one failed launch: title | The last launch stopped before the main menu |
| Its text | Changed since the last launch that worked ({date}): |
| Its buttons | Restore last working setup / Keep |
| Restore's tooltip | Puts back the mods and the load order from {date}, then relaunches. |
| Log line (PR 1) | The last launch didn't reach the main menu (2 in a row): it stopped while loading Name. |

## Open questions for Jordan

1. **0.1.0:** only PR 1 (staying first and the launch record, log lines only), with the rollback and the notices in
   the first update? Or the rollback (PR 2) in 0.1.0 too? (Proposed: PR 1 only. The whole is medium, and your own
   play tests the record first.)
2. **One crash or two:** when the record names the mod (the launch died inside it, or the game's error names it), hold
   that mod back on the very next launch, or always wait for two failures as in your sketch? (Proposed: at once for a
   named mod, with "Turn it back on" in the notice; two failures for anything else.)
3. **Closing while it loads:** should a launch closed with the window's close button before the menu count as failed?
   (Proposed: no, it's recorded as a close. A hang killed from Task Manager still counts.)
4. **When the rollback didn't help,** or found nothing to hold back: stop there, as in your sketch, or allow one
   safe-mode launch with only Spire Mod Manager (the others held back for that launch only, the saved list
   untouched), so you reach the page to fix it? And safe mode on demand later, holding Shift as the game starts
   (Windows and Proton)? (Proposed: safe mode once per run of failed launches; Shift later.)
5. **Another launch-first manager** ticked (Mod Launch Manager, either ModTheSpire2): stay right behind it, or take
   first place? (Proposed: behind it. Two mods pinning themselves first take turns, and the managers banner already
   covers running two managers.)
6. **Updated mods:** may the rollback turn off a mod that was only updated since the last launch that worked? Steam
   keeps only the new version, so it can't go back. (Proposed: yes, marked "updated" in the notice.)
7. **The notice after one failed launch,** when this launch got through: show it every time, or only when the failed
   launch had changes since the last one that worked? (Proposed: only then; otherwise a log line.)
8. **Loading without a restart:** the relaunch path now (the relaunch spike's card), then a Harmony-only experiment
   after the rollback ships, opt-in and labelled experimental? Or no hot loading at all? Content mods stay
   restart-only either way. (Proposed: the first.)

## A first PR

Branch `launch-record`, for 0.1.0. Nothing is held back or turned off yet; the only change players see is our pinned
row.

- **`LaunchGuard.cs` (new):**
  - `Begin()`, first in `ModEntry.Init`, in its own try (an error is logged and changes nothing):
    - reads the last launch's record. If there is one, it logs how that launch ended and what changed since the last
      launch that worked;
    - writes `launch.json` in our data folder: the start time, the game version, our place, the planned mods
      (id, source, on/off, manifest version, DLL and PCK size and date), and **the failures in a row so far**: if the
      last record is still there and doesn't end in "closed", its count plus one, and where each of those launches
      stopped (a short chain, kept to the last few). The count goes back to zero only when a launch reaches the menu
      (the record is deleted then), so two launches that both die still read as two on the third;
    - subscribes to `OnModDetected` (a line per mod in `launch.progress`, flushed) and to `TaskHelper.UnobservedFault`
      (the exception, and the mods it names). Both handlers **fail open**: everything inside a try, nothing thrown
      back into the game's loop; a write that fails switches the record off for this launch (logged once), never a
      mod's loading;
    - logs our assembly's load context (`Name`, `IsCollectible`): the decompile only implies the default one.
  - **Writes are atomic** where a crash mid-write would lose the chain: `launch.json` and `last-working.json` are
    written to a temporary file and moved into place (`File.Replace`), so a reader sees the old or the new, never half.
    `launch.progress` is append-only, one flushed line per mod; a torn last line is ignored.
  - `MenuReachedPatch`, a new patch class: a prefix on `NMainMenu._Ready` (`Priority.First`, once per session). It
    writes `last-working.json` and deletes the record, ahead of `MainMenuPatch`.
  - **The last working setup is the game's own list as it now stands:** the game rewrites `ModList` in load order
    once its loop ends (`ModManager.Initialize`, step 8), and `KeepFirst` (below) may then move us. The snapshot is
    taken after both, so restoring it later never counts our pin or the game's own rewrite as a change. Suspects are
    worked out by (id, source), not by position: a mod that only moved isn't a suspect.
  - A prefix on `NGame.Quit` that appends a "closed" line to `launch.progress` (no rewrite of the record), before the
    menu only.
  - Pure functions: `Read(dir)` (an unreadable record gives null) and `Decide(record, lastWorking)`, which returns how
    the last launch ended, the failures in a row, and the suspects. This PR only logs the result; PR 2 acts on it.
- **Staying first** (`LaunchGuard.KeepFirst`):
  - in `MenuReachedPatch`, so before `LoadOrder.RememberLoadedOrder` and `History.Start`: every entry with our id
    moves to the front, and settings are saved if anything moved. That's part of the baseline: no undo step, no
    relaunch dot;
  - behind a known launch-first manager that's on (`ModTheSpire2`; Mod Launch Manager's id once it's looked up, as on
    TODO round 21's test list), not ahead of it (question 5);
  - also in `LoadOrder.Save` and `History.Apply`, so Auto sort, Fix order and undo keep us first;
  - `LoadOrder.OrderChanged` and the pending rule ignore our own place. A first install loads us late, and pinning
    mustn't show "Relaunch to apply".
- **Load Order:** our row gets a pin with the hover above, and no moves (its menu items disabled, a drag refused like
  a blocked move).
- **`tools/dev.cs`:**
  - the sandbox's `mod_list` lists ours first. `--bmm` still lists Better Mod Menu first: that is a first install's
    order, where we're not first and the record says so;
  - a fixture DLL mod, `SmmTestCrash`: a tiny project under `tools/fixtures/src`, built against the game's `sts2.dll`,
    and off in `mod_list` unless a run turns it on. Its initializer reads `SMM_TEST_CRASH`:
    - `throw`: the game catches it;
    - `kill`: `Process.Kill` on itself, a process dying mid-load like a native crash (`Environment.FailFast` can open
      Windows Error Reporting and wait);
    - unset: nothing;
  - `test --launches`: several launches of one sandbox, its data kept between them:
    1. a normal launch, which saves the last working setup;
    2. `SmmTestCrash` turned on, with `kill`: the process dies;
    3. `kill` again: it dies too, and its record carries two failures in a row;
    4. with `throw`: it reaches the menu, and the self-test checks what it read of launches 2 and 3;
  - a plain `test` reset already deletes our data folder (`tools/dev.cs:235-237`), records included.
- **Self-test checks** (plain run):
  - "our initializer wrote the launch record: the planned order, ours first, every fixture mod listed";
  - "the main menu deleted the record and saved the last working setup, matching the saved mod list after the
    game's rewrite and our pin (ours first)";
  - "the record is in the folder Store uses" (the account folder is known in `Init`);
  - "the menu hook is a prefix, first in line" (from Harmony's patch info);
  - `Decide`, with made-up records:
    - "a record still there is a failed launch; two in a row count two, read from the count carried into the newest
      record (the first one was overwritten); a launch that reached the menu starts from zero";
    - "a progress file that ends in 'closed' is a close, not a failure, and doesn't add to the count";
    - "a torn last progress line is ignored; a half-written record can't happen (written by replace)";
    - "a handler that throws (a made-up fault) changes nothing in the game's loop and switches the record off";
    - "the mod named is the first planned one after the last finished line";
    - "suspects are mods new, turned on, or updated (version, or DLL date) since; not mods turned off, never ours";
    - "an unreadable or empty record counts as none";
  - "`KeepFirst`: a list with our id twice, one copy at the end, has both at the front, in their order";
  - "behind a known launch-first manager that's on, we're second";
  - "Load Order: our row has no moves, and Auto sort and an undone move leave us first";
  - "pinning at the first menu is no change: no relaunch dot, no undo step".
- **`--launches` checks:**
  - "a launch killed while loading `SmmTestCrash` leaves its record, and the next launch names `SmmTestCrash` as
    where it stopped";
  - "killed twice in a row, the third launch reads two failures in a row" (the chain survives the overwrite);
  - "a mod whose initializer throws is no failed launch" (launch 4 clears its own record at its menu);
  - "after that, the record is gone and the last working setup has `SmmTestCrash` on".
- **`--real`:** an INFO line with the time from our `Init` to the main menu, with the record's writes, over Jordan's
  mods.
- **Real play only** (the dev sandbox has no Steam): with both a local and a Workshop copy of ours, we're still first
  on the second launch. Add it to the private-upload test (RELEASE-PLAN §2 item 12).

A second PR, `launch-rollback` (the first update), holds the suspects back after two failed launches and adds the
notice. Its checks:
- "`--launches`: killed twice in a row, the third launch reaches the menu with `SmmTestCrash` off, and the notice
  lists it";
- "Turn them back on ticks it again, as one undoable change";
- "a fourth failed launch holds nothing more back".

A fixture for the startup error (a model whose constructor throws) comes with it: the game's popup waits for a click,
so the runner has to close it.
