# Spike: two mod managers at once

Status: proposal, 2026-10-13. No code changed. Written against `dev` plus #32 (the banner's Dismiss, branch
`managers-dismiss`), which isn't merged yet. Line numbers are `dev`'s (62d73be); #32's additions are named,
not numbered. Better Mod Menu's are from its source (1.1.1, Jordan's `local-fix` checkout).

## The problem

Jordan (backlog, round 20): "think about possible conflicts or issues with running both bmm and mod manager
together (could be other managers too) and decide on how to handle it best for the user".

Today, with Better Mod Menu ticked as well as ours:

- our views pause it and hide its parts, by reflection (`Bmm.cs`);
- Vanilla View shows its page instead of the game's;
- a red banner says "Multiple mod managers are currently enabled, which may cause unexpected behavior.", with
  Show (its row) and, in #32, Dismiss.

Nothing of this looks for any other manager: everything keys on Better Mod Menu's id or its type names.

## What happens today

### Our side

- **The bridge** (`Bmm.cs`). Nothing of Better Mod Menu is patched; we reach a few of its members by
  reflection, looked up once.
  - `Present` (`Bmm.cs:33`): its patch class exists, found by type name, whatever its mod id.
  - `Ticked` (`Bmm.cs:37`): a mod with the id `BetterModMenu` is on in the saved list, loaded or not.
  - `BothTicked` (#32): that, and ours is on too. The banner shows while it's true.
  - Called from `Layout.Apply` (`Layout.cs:56-62`, `Layout.cs:107-110`, `Layout.cs:194-201`),
    `CategoryList.Apply` (`CategoryList.cs:290-297`, `CategoryList.cs:362-371`), and a deferred re-layout after
    its `_Ready` postfix (`Patches.cs:166-193`, only when `Present`).
- **What each step does, and what happens if Better Mod Menu renames what it uses:**

  | Step | Does | Uses | If it's gone |
  |---|---|---|---|
  | `Detach` (`Bmm.cs:40-44`) | our views: it stops re-laying out | field `ModdingScreenContext._currentScreenRef` | logged once; its re-layout on every resize keeps running on our views, and pushes our list down again |
  | `Attach` (`Bmm.cs:48-65`) | Vanilla View: its page again | `GetSession`, `TrackCurrentScreen` | logged once; its bars show but its page isn't laid out or regrouped |
  | `Refresh` (`Bmm.cs:69-79`) | its page after our redraws | `IsCurrentScreen`, `RefreshGroupsUI`, `UpdateChromeLayout` | logged once; its groups and bars go stale after a change |
  | `RestyleRows` (`Bmm.cs:125-132`) | its name colours back | `RefreshVisibleModNames` | logged once; plain row names on its page |
  | `Show` (`Bmm.cs:82-94`) | hides its bars, panel buttons and row controls on our views | node names `BetterModMenuChrome`, `BetterModMenuSelectedModActions`, `RowCustomControls` | **not logged**: its parts show on our views |
  | `RemoveGroups` (`Bmm.cs:97-106`) | its group headers out of our list | name prefix `ModGroup` | **not logged**: its headers sit in our list |
  | `Unpush` (`Bmm.cs:111-122`) | takes back its push down of the list | meta `better_mod_menu_top_bar_room` | **not logged**: the list sits lower |

  If it renames its namespace, `Present` is false and none of this runs. The banner still shows (it goes by
  mod id), but its page sits over ours.
- **Profiles never toggle it** (`Profiles.cs:262`, `Profiles.cs:284-286`), as for ours. The `--bmm` self-test
  checks this (`SelfTest.cs:692-703`).
- **Other on/off actions do.**
  - Enable all and Disable all (`CategoryList.cs:629-644`, through `Dependencies.Set`, `Dependencies.cs:56-69`)
    hold only ours on. WHATS-NEW lists this as a choice for review.
  - Undo, redo and Revert all restore the whole saved list, its entry included (`History.cs:218-224`).
- **The game's saved list.** One live object, `SaveManager.Instance.SettingsSave.ModSettings`. Every write
  changes that object and then calls `SaveSettings`. We write it in:
  - `Profiles.Apply` (`Profiles.cs:254-282`);
  - `Dependencies.Set` (`Dependencies.cs:56-69`);
  - `LoadOrder.Save` (`LoadOrder.cs:407-418`);
  - `History.Apply` (`History.cs:218-224`);
  - `NewModTogglePatch` (`Patches.cs:433-451`).
- **Our own file** (`manager.json`) is ours alone. Its first-run import reads Better Mod Menu's file once
  (`Store.cs:108-112`, `Store.cs:406-511`) and never writes it.

### Better Mod Menu's side

- **Its patches** (one `PatchAll` under the id `BetterModMenu`, `Main.cs:21-22`):

  | Method | Its patch | Ours on the same method |
  |---|---|---|
  | `NModdingScreen._Ready` | postfix: builds its bars, groups and layout (`NModdingScreenPatch.cs:33-56`) | postfix, `Priority.First`; a deferred call lays ours out again after its postfix, and pauses it before its own deferred step (`Patches.cs:166-193`) |
  | `NModdingScreen.OnModEnabledOrDisabled` | postfix: copies the saved list into its selected profile and writes its file (`NModdingScreenPatch.cs:16-22`) | postfix, `HarmonyBefore` it (`Patches.cs:202`) |
  | `NModdingScreen.OnRowSelected` | postfix: its panel buttons (`NModdingScreenPatch.cs:24-31`) | postfix, `HarmonyBefore` it (1.1.1 throws there on the beta) |
  | `NModdingScreen._ExitTree` | postfix: forgets the screen | none |
  | `NModInfoContainer.Fill` | postfix: its title style (`NModInfoContainerPatch.cs:15-22`) | prefix first; postfix `HarmonyAfter` it, so our title wins |
  | `NModMenuRow.OnTickboxToggled` | prefix: skips the game's handler during its own bulk changes (`NModMenuRowPatch.cs:11-17`) | prefix (`NewModTogglePatch`) |
  | `NModMenuRow._Ready` | postfix: name colour, its row arrows and group picker (`NModMenuRowPatch.cs:19-114`) | none |

- **Its pause.** Our `Detach` clears its "current screen". That stops its re-layout on resize
  (`ModdingScreenChromeOps.cs:284-288`), its deferred steps (`ModdingScreenActionFlow.cs:57-66`) and its row
  arrows (`NModMenuRowPatch.cs:423-433`). It does **not** stop its patches: the `OnModEnabledOrDisabled`,
  `OnRowSelected`, `Fill` and row `_Ready` postfixes check nothing of the kind, so they run on our views too.
- **Its profiles.** A profile is a set of disabled mod ids, with no source and no order
  (`ProfileManager.cs:88-98`).
  - Its selected profile always follows the live list. Every `OnModEnabledOrDisabled` copies the list into
    it and writes its file, unless it's in one of its own bulk changes (`NModdingScreenPatch.cs:16-22`,
    `ProfileManager.cs:157-161`). Switching away copies the list into the profile being left first
    (`ModdingScreenProfileOps.cs:22-23`).
  - Applying a profile sets every saved entry by id, managers included, then saves its file and the game's
    (`ModdingScreenProfileOps.cs:29-38`). It then calls `OnModEnabledOrDisabled` inside its own bulk change
    (`NModdingScreenPatch.cs:328-347`).
- **Its load order.** Its row arrows move a mod past the next mod of the same group only, then call
  `SaveSettings` (`ModdingScreenListOps.cs:11-36`, `ModOrderRules.cs:29-36`). No `OnModEnabledOrDisabled`.
- **Its startup** (`Main.cs:12-24`): loads its file, backing it up and the game's mod list once per launch
  (`ProfileManager.cs:191-200`, 12 kept); reads each mod's manifest once; patches. Light.
- **Its own features we hide on our views:** profiles, groups, search, backup, CSV export, log viewer, name
  styles, and a Config button for RitsuLib and BaseLib mods (`ModConfigProviderAdapter.cs`). All still on its
  page.

### Other mods on the same page

A read-only scan of the mod DLLs installed in Jordan's game (2026-10-13) for the screen's type names. A name in
a DLL means the mod uses the type. It doesn't prove a patch.

| Mod | Uses | What the names suggest |
|---|---|---|
| Better Mod Menu (and `BetterModMenuFork`, which never loads) | screen, panel, rows | a manager |
| JmcModLib 1.9.0 (library) | screen, panel | patch classes named `ModdingScreenRestartButtonPatch` and `...StackPatch`: a restart button on this page |
| Bit of Everything 2.4.3 | screen, rows, `ModSettings.ModList`, `SaveSettings` | unknown: it reads or writes the saved list |
| RitsuLib (library) | rows | unknown |
| Undo And Restart, its UI Modified copy, a skin manager, RichCustomSteamPresence | panel only | parts in the info panel (Undo And Restart's box is already handled: `Layout.cs:355-362`) |

RELEASE-PLAN §6 counts five other managers on the Workshop. It doesn't name them. Apart from Better Mod Menu,
nothing scanned here is clearly one.

## Conflict scenarios

1. **Both write the game's saved list.** There is one live list. Both managers change that same object and
   call `SaveSettings`, which writes it as it is now. No write is lost between the two, so it isn't "the last
   save wins". The real conflicts are about what each manager keeps on top of the list (2 to 4).
2. **Profiles: its selected profile follows ours.**
   - Every change to the list on our page ends in a call to `OnModEnabledOrDisabled`: the game's tickbox
     handler makes one, and so do our `Redraw` (Enable all, Load Order moves, undo: `CategoryList.cs:243-252`)
     and our profile switch (`Profiles.cs:136`). Its postfix then copies the list into its selected profile and
     saves its file.
   - Example: switching our profile to "Co-op" saves our Co-op's mods into its selected profile, "Default".
   - Example: after switching its profile to "Co-op" on its page, an undo on ours (`History.cs:132-157`, then
     the redraw in `HistoryBar.cs:99-107`) puts the old mods into its "Co-op".
   - The other way is safe. Its profile switch shows in ours as "Name *" (an unsaved edit, through
     `ModToggledPatch` and `Profiles.Track`) and one undo step. Our saved profiles never change.
   - We can't fix its side from ours. Even if we held its autosave off on our views, it copies the list into
     the profile it leaves on its next switch. Its model is "the selected profile is the live list".
   - Recoverable: its launch backups (12 kept) and its Load Backup button.
3. **Its profiles can turn ours off.** Applying one of its profiles sets every entry by id. A profile last
   copied while ours was unticked turns ours off at the next relaunch. The same goes for itself. Ours never
   toggles it through profiles, but Enable all and Disable all do (by design for now).
4. **Load order.** Its arrows record no step in our history (no `OnModEnabledOrDisabled`). The next step we
   record takes them in. Switching back from its page is one: it saves our file, and every save records
   (`CategoryList.cs:31-42`, `Store.cs:284-291`). So its moves become one Load Order undo step, and Changes
   counts them as moved. Nothing is lost. Its group-only moves and our free moves don't disagree about the
   list itself.
5. **Both patch the Modding screen.**
   - The order is pinned where it matters (table above). The `--bmm` and `--bmm-after` runs cover both load
     orders.
   - A rename on its side breaks one step of the bridge. The steps by member log once; the steps by node name
     fail silently (table in "Our side").
   - A patch we don't know about runs anyway. Two of its patches touch methods we don't patch (`_ExitTree`,
     row `_Ready`).
   - Godot signals it connects (its `Resized` handlers) are in no patch list. Only the pause stops them.
6. **Its page in Vanilla View.** Works (tested both ways round, and switching back and forth doesn't drift:
   `SelfTest.cs:649-690`). While it's loaded there is no plain game page. Its Config and Alias/Notes buttons
   exist only there.
7. **First-run import.** It runs once, when we have no file. Its data keeps changing afterwards; re-import is a
   round 2 backlog item.
8. **Performance.**
   - Per change on our page, its postfix serialises and writes its file once. Not on a search keystroke or a
     view switch (those use `CategoryList.Apply`, which doesn't call `OnModEnabledOrDisabled`).
   - Its `Fill`, `OnRowSelected` and row `_Ready` postfixes run on our views, and we hide or overwrite what they
     make.
   - In Vanilla View, our redraws also regroup and re-lay out its page (`Bmm.Refresh`).
   - None of this is measured. The `--real` timings (`SelfTest.cs:188-220`) time `CategoryList.Apply`, which
     never reaches its postfix.
9. **A fork or a renamed copy.** `Present` goes by type name and `Ticked` by mod id. A fork with another id
   that loads would be paused, but get no banner, and profiles would toggle it.

## Finding other managers

- **Harmony is shared.** The game ships it (`0Harmony.dll` 2.4.2, in `data_sts2_windows_x86_64`), and its
  patch records are shared by every mod. So `Harmony.GetAllPatchedMethods()` lists every mod's patches, and
  `Harmony.GetPatchInfo(method)` gives each patch's owner (its Harmony id) and its method.
- **Which methods.** Everything on `NModdingScreen`, `NModMenuRow` and `NModInfoContainer`, plus the saved
  list's own type, `ModSettings`. Not the main menu, `NGame` or `NInputManager`: many unrelated mods patch
  those. Not `SaveManager.SaveSettings` either: it saves every game setting, not just mods.
- **Two kinds**, by what they patch:
  - **Manager:** it takes part in turning mods on and off (`OnModEnabledOrDisabled`, `OnTickboxToggled`, or
    `ModSettings`). Better Mod Menu is one.
  - **Page add-on:** it only builds or fills the page (`_Ready`, `Fill`, row `_Ready`). JmcModLib's restart
    button would be one.
- **Owner to mod**, in order:
  1. the owner id is a mod's manifest id (Better Mod Menu's is);
  2. the patch method's DLL sits in a mod's folder (`Mod.path`; this needs the game to load mod DLLs from
     their path, to check in the first PR);
  3. otherwise the Harmony id as text, with no row to go to.
- **When.** Once, at the first main menu (every mod has initialised by then, and patches don't change).
- **Limits.**
  - Only loaded mods. A manager ticked this session loads after a relaunch. Until then only a known id
    (Better Mod Menu's) finds it.
  - No Godot signals, and no plain writes to the saved list from elsewhere (a mod's own settings page, say).
    Our side copes with plain writes already: the list is the truth, and our profile shows the difference
    as unsaved.
  - It says what a mod hooks, not what it does. The kinds are a heuristic.

## Options

1. **Warn only** (today, dismissable with #32).
   - Pros: honest, cheap, nothing to break.
   - Cons:
     - It names no way out. "Unexpected behavior" doesn't say what, or what to do.
     - Better Mod Menu only.
   - Effort: done.
2. **Detect the overlap and pause the other's parts on our views** (today's approach for Better Mod Menu,
   generalised).
   - Pros: our views look and behave as tested.
   - Cons:
     - Pausing needs to know the other mod's internals. For an unknown mod, all we can do is hide nodes it
       added, which hides real features (a Config button, Undo And Restart's box, which we deliberately keep:
       `Layout.cs:355-362`). Its code keeps running.
     - Each known manager needs its own bridge, and each bridge breaks on that mod's updates.
   - What does generalise, safely: laying out again after another mod's `_Ready` postfix. Today it happens
     only when `Bmm.Present` (`Patches.cs:166-193`).
   - Effort: the generic re-layout is small (about 10 lines, 1 check). A bridge per manager is medium each.
3. **A one-click "use only one".** A "Disable Better Mod Menu" button on the banner unticks it.
   - It's one undoable change (Ctrl+Z, or tick it again). It applies after a relaunch, like any change.
   - Its file stays as it is, so turning it back on later finds its profiles and groups.
   - Pros: the real answer to profiles (scenario 2). Two managers' profiles can't agree, so most players
     should pick one.
   - Cons:
     - A third button on the banner.
     - Its selected profile gets "Better Mod Menu off" copied in while it's still loaded (scenario 3). After
       it's back on, picking that profile again in its menu would turn it off.
   - Effort: small (about 30 lines, 2 keys, 3 checks).
4. **A supported pairing with a tested matrix.** Better Mod Menu 1.1.x is supported: `--bmm`, `--bmm-after`
   and `--bmm-local` stay part of every pull request's test.
   - Pros: we already run them. Saying so in the Workshop description sets expectations.
   - Cons:
     - Its updates reach players through the Workshop before our tests see them. Today a renamed node fails
       silently.
     - Covers one manager.
   - Add: a health check at page open, covering every member **and node name** the bridge uses. Out of date,
     the banner says its page may overlap ours.
   - Effort: small to medium (about 50 lines, 1 key, 2 checks).

## Recommendation

For the player: one manager is the setup we recommend. Two still work: the banner says so plainly, offers one
click to keep only ours, and can be dismissed by anyone happy with both. In practice that's options 1, 3 and 4
together, and the safe part of 2.

- **Detect managers generically** (Harmony, above). The banner names the manager it found. A known id
  (Better Mod Menu's) still counts while it's ticked but not loaded.
- **Add "Disable <name>"** to the banner (option 3), beside Show and Dismiss. Its tooltip says what it does
  and how to undo it.
- **Keep the Better Mod Menu bridge**, and add the health check (option 4). No bridges for other managers
  until one is popular enough to earn it.
- **Page add-ons get no banner.** They are libraries and panel parts that don't turn mods on or off, and a
  warning that never goes away for them would be noise. Log them once at startup. Later, maybe a line in that
  mod's info panel (question 2).
- **Lay out again after any other mod's `_Ready` postfix** on the page, not only Better Mod Menu's. Measure it
  first: it adds one `CategoryList.Apply` per page open.
- **Say it plainly in the Workshop description:** both managers use the game's one mod list, and Better Mod
  Menu's selected profile follows any change made here.

## Wording

| Where | Today | Proposed |
|---|---|---|
| Banner | Multiple mod managers are currently enabled, which may cause unexpected behavior. | unchanged |
| Banner text's hover card | (none) | Both use the game's one mod list. A change here also changes {name}'s selected profile. |
| Show tooltip | Shows Better Mod Menu in the list | Shows {name} in the list |
| Dismiss tooltip (#32) | Hides this warning. It comes back if Better Mod Menu or Spire Mod Manager is disabled and then both are enabled again. | the same with {name} |
| New button | (none) | Disable {name} |
| Its tooltip | (none) | Disables {name} from the next relaunch. Its profiles and settings are kept. Ctrl+Z, or enabling it again, undoes this. |
| Bridge out of date (second PR) | (none) | This version of {name} isn't fully supported yet: its page may overlap ours. |

## Open questions for Jordan

1. "Disable" only the other manager, or also "Use {name} instead" (disable ours)? (Proposed: only the other.
   The banner is on our page, so its reader is using ours, and ours can still be unticked by hand.)
2. Page add-ons such as JmcModLib's restart button: a log line only, a line in that mod's info panel, or the
   banner? (Proposed: the log line now; the panel line later, with the "Config" backlog item.)
3. Two other managers at once: one banner naming the first ("{name} and 1 more"), or one per manager?
   (Proposed: one banner. Show steps through them.)
4. With the bridge out of date: keep our page and warn, or open on Vanilla List (its page) until it's fixed?
   (Proposed: keep ours and warn.)
5. Enable all and Disable all: keep including Better Mod Menu (today), or hold it as profiles do? (Proposed:
   keep. It's a visible, undoable click, and the banner's Disable covers the deliberate case.)
6. Should a supported Better Mod Menu version go in the Workshop description, or just "works alongside"?

## A first PR

Branch `managers-detect`, on top of #32:

- **`Managers.cs` (new):**
  - `Found`: the other mods patching the Modding page, each with its kind, its patched methods and its mod (or
    null). Worked out once on first use, after the first main menu. `Rescan()` for the self-test.
  - Known ids (`Bmm.ModId`) count as managers by id, loaded or not.
  - `OtherTicked`: the first manager whose mod is on in the saved list. It replaces `Bmm.BothTicked` (#32) in
    `LoadOrder.UpdateToolbar` and `Bmm.ForgetDismissedWarning`, which moves here. Ours must be on too, as now.
  - Logs the list once: "Other mods patching the Modding page: BetterModMenu (manager: _Ready,
    OnModEnabledOrDisabled, ...), JmcModLib (page: _Ready)".
- **`LoadOrder.ManagersBanner`:** Show and its tooltip name the found manager; a "Disable" button runs
  `Dependencies.Set([mod], false, screen)` and the redraw, one undo step (as Enable needed mods does).
- **`Store`:** `ManagersWarningDismissed` stays one flag. It's forgotten when no other manager is ticked.
- **Strings** (`settings_ui.json`): `DISABLE_MANAGER` and `DISABLE_MANAGER_TIP`; `SHOW_MANAGERS_TIP` and
  `DISMISS_MANAGERS_TIP` take `{name}`; `MANAGERS_CARD` for the banner text's card.
- **Self-test checks** (plain run, with test Harmony patches applied and removed by the test):
  - "a test patch on `OnModEnabledOrDisabled` under the id `SmmTestUi` counts as a manager, tied to that
    fixture mod; ours never counts";
  - "a test patch only on `NModInfoContainer.Fill` counts as a page add-on: no banner";
  - "with the test manager ticked, the banner names it in Show's tooltip and its Disable button";
  - "Disable unticks it as one undo step: the banner goes and the relaunch line shows; Undo ticks it again
    and the banner comes back";
  - "Dismiss keeps the banner away until no other manager is ticked" (#32's checks, now through `Managers`);
  - "removing the test patches leaves no banner" (the test ends as it started).
- **Self-test checks with `--bmm`:**
  - "Better Mod Menu is found as a manager from its patches (`OnModEnabledOrDisabled`, `OnTickboxToggled`),
    tied to its row";
  - an INFO line with every other owner on the page's methods, so a new one shows up in the results.

A second PR, `bmm-bridge-health`:

- the health check (members and node names) with the out-of-date banner text;
- the generic re-layout after other mods' `_Ready` postfixes, with a `--real` timing before and after;
- checks:
  - "`--bmm`: the bridge finds every member and node it uses";
  - "a missing member (set by the test) shows the out-of-date text";
  - "another mod's `_Ready` postfix that moves the list is laid out again after it";
  - an INFO line with Better Mod Menu's selected profile before and after a change on our page. It records
    scenario 2, so a change in its behaviour shows up.
