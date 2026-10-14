# Spire Mod Manager: publish-readiness plan

## 1. Where it stands

**What it does**
- **Entry and views:** a movable main-menu button with a pending-relaunch dot. The Modding screen has three views: the main list, Vanilla View (remembered) and Load Order (a side trip). The selected mod carries across view switches.
- **Organizing:** collapsible categories with inline actions; aliases, notes and tags (automatic: New, Local, Library, Gameplay, Cosmetic; plus your own); search with `#tag` and `@author`; multi-select and bulk editing; a page bar with Enable all/Disable all.
- **Info panel:** Workshop details (dates, size, subscribers, tags, branches), a Patch notes link, problems in red, dependencies and "Required for", your editable fields, and Open Folder.
- **Profiles:** save, switch, unsaved diff, discard, new, duplicate, rename, delete; imported once from Better Mod Menu.
- **Load Order:** drag (including blocks), dependency lines, a warning when the game will reorder mods plus Fix now, and Auto sort.
- **Safety net:** Changes list, undo/redo (200 steps, two histories), Revert all, and a relaunch check that offers to enable needed mods or disable mods that can't load.
- **Relaunch:** an in-place restart through the game's quit path. It keeps the window and attaches to Steam.
- **Data:** one `manager.json` per Steam account with an atomic write and 10 backups. The game's `mod_list` stays the source of truth for on/off and order. Strings live in `localization/eng/settings_ui.json`.

**State**
- Version: 0.0.44 installed; Round 15 (0.0.45) built but not installed (`WHATS-NEW.md` "Not installed yet"). `build.cs:24` still says `0.0.44` with the pre-release comment.
- Git: only the `dev` branch; no `main`, no remote, no tags, no LICENSE. The working tree is clean.
- Tests: `SelfTest.cs` has about 220 checks. **Correction to the auditors:** `out/selftest/results.txt` holds the 7-check `--real` tour from 14:04, not the full fixture suite. No saved result of the full suite exists for the current build, so re-run it on the release build.
- Timing: redraw measured at 14.9 ms for 56 mods. That run had half the mods off (`tools/dev.cs:188-190`), so the dependency-heavy paths were never timed.
- Publishing: no uploader, workspace, preview image, in-game `mod_image.png`, listing copy or staging step yet.

## 2. Blockers for 1.0

1. **Branch targeting (otherwise most players get a load error).** `min_game_version` is `0.111.0` (public-beta). The stable `public` branch appears to still be 0.107.1, where the game marks the mod Failed (`GAME_VERSION_UNSUPPORTED`).
   - Fix: ship beta-only on purpose. Set `"minBranch": "public-beta", "maxBranch": null` in `workshop.json` on every content upload, put "(Beta)" in the Workshop title (not in the manifest name), and say "public-beta v0.111.0+ only" in the description.
   - Before uploading, confirm stable's version on SteamDB or in Steam news.
2. **Release version and branch.**
   - Set `version` to `1.0.0` and refresh the manifest `description` (`build.cs:23-24`). The current text says "an in-place Restart" and only mentions profiles and sorting.
   - Add a release mode to `build.cs` that never auto-bumps.
   - Create `main` from `dev`, and tag `v1.0.0`.
3. **Clean upload folder.** `out/` holds `bin/` (pdb, deps.json), `dev.log` and `selftest/real-*.txt`, which contain your real categories, mod list and profile names.
   - Fix: a `build.cs --dist` that writes `dist/SpireModManager/` with only the `.dll`, `.json` and `.pck`. The uploader's `content/` is filled from that folder only.
4. **Fail soft when a game patch renames something.** **Fixed (2026-10-13).** This is Early Access, and 0.112 could break half the page for every user.
   - Done: each patch class is applied on its own (`ModEntry.FailedPatches`). `ModEntry.PageNeeds` lists the ones the page can't run without (main menu, Modding screen, tickbox, info panel fill, row selected); if one failed, the page stays the game's with one line, "Spire Mod Manager needs an update for this game version." (beside the main menu button instead if the screen patch itself failed). Dev runs can try it: `SPIRE_MOD_MANAGER_SKIP_PATCHES=ModInfoFillPatch`.
   - `ModEntry.cs:23-24`: `PatchAll` is all-or-nothing, and `Installed.Watch()` runs after it.
     - Fix: call `Watch()` first, then patch each `[HarmonyPatch]` class in its own try/catch (`harmony.CreateClassProcessor(type).Patch()`). Record which patches failed. If any UI patch failed, keep the vanilla layout and show one line on the screen: "Spire Mod Manager needs an update for this game version".
   - `Patches.cs:360-368` `RowState`: the static reflection initializers throw `TypeInitializationException` on every redraw if `_isSelected` or `IsFocused` is renamed. `RowTitles.cs:37` calls them outside any try.
     - Fix: resolve them lazily and allow null (return false, log once).
   - **Correction to the recap**, which named only ModInfoClearPatch: four patch bodies are unwrapped, so the `Patches.cs:15` header claim is false.
     - `ModInfoClearPatch` (`Patches.cs:201`) runs inside `NSubmenuStack.Pop`. An exception there can leave the main menu with its backstop on and our button hidden.
     - `ShortcutsPatch.Prefix` (`Patches.cs:217`) runs on every Escape in the game.
     - `RowSelectedPatch` (`Patches.cs:270`).
     - The outer part of `NewModPatch`.
     - Fix: wrap all of them. The Escape prefix should `return true` on catch.
5. **Opening the screen with many mods is slow (about O(n⁴)).** **Fixed (2026-10-12).** `ModToggledPatch` (`Patches.cs:131`) does a full refresh for every row the game adds during `_Ready`. Measured in a benchmark: 0.7 s at 150 mods, 5.8 s at 250.
   - Fix: add `if (HistoryBar.Screen != __instance) return;`, the same guard `NewModPatch` already has. This is a one-line fix.
6. **Load Order drag handlers pile up.** **Fixed (2026-10-12), with the self-test.** `LoadOrder.Decorate` (`LoadOrder.cs:429, 449`) reconnects `MouseEntered`, `MouseExited` and `GuiInput` on every visit to Load Order, and `Undecorate` only removes the labels. After K visits, one press starts K drags and edge-scrolling runs K times faster.
   - Fix: connect once per row behind a meta flag (as `RowMenuMeta` does).
   - Add a self-test: visit Load Order twice, then check that one drag gives one History step.
7. **Data safety in `Store.cs`.** **Fixed (2026-10-13), with the self-test.**
   - The backup copy (`Store.cs:167-175`) shares a try with the real write. If the copy fails (a file lock or antivirus), every save that session is skipped silently.
     - Fix: give the backup step its own try/catch.
   - If `manager.json` can't be read, the session runs read-only on every launch, edits are silently lost, and only the log says so.
     - Fix: move the bad file aside as `manager.broken-<time>.json`, load the newest readable backup, and show a one-time notice on the Modding screen.
   - After loading, replace null collections with empty ones.
8. **Copy pass** (your own reminder, `TODO.md:101`).
   - Use one verb: "Relaunch" everywhere ("Pending restart" becomes "Pending relaunch"; also `NEW_MOD_BODY` and the manifest).
   - Use one name: "Spire Mod Manager", not "Spire Manager".
   - "Vanilla List" or "Vanilla View": pick one.
   - Use "enabled/disabled" consistently, not "on/off" on the same page.
   - Make tooltip punctuation consistent.
   - "New category" casing.
   - Make a profile rename show as a rename in Changes, not "Added X" plus "Removed Y".
   - Then freeze the English text for 0.1.0, so a translator never works on text that still moves (the translation plan in §4).
   - A second pass on 2026-10-14 (TODO round 22) read the text added in rounds 19-22: the list filters, the banners and their cards, the unsaved list, Relaunch game and the data notices. The English freeze for 0.1.0 now includes it: 237 keys. Still Jordan's call: the "Spire Manager" button on the Vanilla List page.
9. **First run, untested on both paths.**
   - (a) No `manager.json` and no Better Mod Menu.
   - (b) Better Mod Menu data present, with BMM on and with BMM off.
   - Add fixtures for both (today's fixtures always ship a `manager.json`).
   - Add an empty-state hint for making a category.
10. **Uninstall and disable behaviour, checked and documented.**
    - Unsubscribe and untick, then confirm the vanilla page is intact and `mod_list` keeps the last on/off state and order.
    - Confirm that `mod_data/SpireModManager/` stays behind.
    - Put this in the description.
11. **Workshop listing:**
    - preview `image.png`: square, 512–1024 px, under 1 MB;
    - BBCode description (8000 characters max);
    - tags `["QoL", "Utility"]`;
    - first change note starting with "1.0.0";
    - `"dependencies": []` (explicit, because v0.2.0 of the uploader wipes Required Items when the field is absent).
12. **Real Workshop test.** Upload privately, subscribe on your account, and check `workshopId`, the details, the Patch notes link and the source line. Dev runs use `--force-steam off`, so this path has never run.
13. **Share and import as text** (Jordan, 2026-10-13, after the survey of other managers in DESIGN.md). Mod Profile Manager and Mod Preset Sync have share codes, and Load Order Manager has clipboard JSON, so players will expect it.
    - Copy a profile to the clipboard as one line of text: a prefix and version (`SMM1:`) plus the compressed list. It should fit one Discord message for about 250 mods.
    - Import shows what would change before anything does (enable, disable, missing, local only, game version), then makes a new profile, so it can be undone.
    - Details in TODO round 21.
14. **Crash-safe launch** (Jordan, 2026-10-13: spike first). The spike is the blocker. Building it is one too if the spike shows it's small; otherwise it ships in the first update.
    - A launch that never reaches the main menu leaves a marker. At the next menu, offer to restore the last setup that worked. After two failed launches in a row, roll back once on our own and say so. Never loop.
    - It needs us first in `mod_list`. The spike decides whether a mod loaded first can stop the rest loading in the same launch, or must restore `mod_list` and relaunch.
    - Found since (2026-10-13): it can. STS2-ModManager (MIT) patches `ModManager.TryLoadMod` while the first mod initializes, holds back the later mods, then loads the chosen ones. So a crashing mod can be skipped without a second launch.
    - Details in TODO round 21.
15. **List filters** (Jordan, 2026-10-14: "for 0.1.0"). "Show: All · Enabled · Disabled · Changed" in the main list's page bar.
    - Enabled and Disabled go by the tickboxes (what loads after a relaunch); Changed shows the mods the Changes list names.
    - It works with the search (both must match) and the categories (each counts its rows shown). Load Order and Vanilla List show every row.
    - Built on branch `list-filters`. Details in TODO round 22.
16. **Affected mods on the warning banners' hover** (Jordan, 2026-10-14). Hovering a warning banner lists the mods it means, not just how many.
    - Being built on branch `banner-hover`. Details in TODO round 22.

## 3. Should do before 1.0

- **Profiles and new mods:** a mod the profile has never seen counts as off. So every new subscription makes the profile "Unsaved •", and Discard turns it off. Pick a rule (see Questions) and implement it.
- **In-game picture:** add `res://SpireModManager/mod_image.png` as an imported texture (`.import` plus `.ctex` in the pck). Today our own panel shows no image.
  - `build.cs` only packs raw PNGs. Copy the layout BaseLib and others use.
- **Fallbacks for missing game resources:**
  - `ModEntry.AddButton` (`ModEntry.cs:152`) has no null check. A broken `menu_button.tscn` dependency throws in `CategoryList.Attach`, which loses categories, Relaunch and Open Folder.
  - `ProfileBar.DownArrow` (`ProfileBar.cs:293`) dereferences a texture that can be null, after the bar is already added (a half-wired bar stays on screen).
  - Fix: a null-checking loader, a plain Button fallback, a "▾" glyph, and adding the bar only once it is fully built.
- **Redraw and search cost:** build per-redraw lookups once (the set of disabled mods, a reverse-dependency count, per-mod tags and search text). Today `RowStrip.RequiredFor` and `Search.Matches` rescan `ModManager.Mods` for every row.
  - Add a fixture of about 200 mods and a `--real` variant with everything enabled.
  - Time the screen open and one search keystroke.
- **Long text that still overflows** (TODO Round 15): the panel title and version, the source line, and rows with many tags hiding the author.
- **Undo And Restart compatibility:** it adds a panel at a fixed (0,318) in the info panel, over our fields. Re-place foreign children of the info panel below the description, then check in game.
- **Warn about managers that rewrite `mod_list`** (recommended by the survey; to confirm with Jordan). Moved up from §4's "detect other mod managers by id and warn".
  - Widen the round 19 "Multiple mod managers are currently enabled" warning past Better Mod Menu: ModListSorter, LoadOrderSync, Mod Launch Manager, Load Order Manager, Mod Preset Sync and Mod Profile Manager (see §6).
  - Manifest ids: Load Order Manager's is `LoadOrderManager`; the others need subscribing to read them (TODO round 21).
  - Decide it with the two-managers spike (TODO round 20, `docs/spikes/two-managers.md`).
- **Test beside the other mod managers** (recommended by the comparison session). Most of them patch the same screen or rewrite `mod_list`, and only Better Mod Menu has been tested with ours.
  - Suggested: at least the first four before 0.1.0 (the most players or the most overlap): Load Order Manager, AutoModSubscriber, Better Mod Menu with Better Mod Menu Fix (Public-Beta), and Mod Launch Manager.
  - For each: buttons and panels over ours; their Harmony patches on the same screen and rows (their exceptions shouldn't break ours); who wins when both write `mod_list`, at quit and at Relaunch; the first load slot; hot loading.
  - The dev sandbox has no Steam: a `tools/dev.cs` option that copies Workshop items into it (like `--bmm`) covers them all.
  - Check the two-managers spike (#36) against the list. The list, by priority: TODO round 21.
- **Mark our popups as ours:** add "Spire Mod Manager" to New Mod Added, Delete profile and Delete category. The New Mod popup replaces the game's popup on BMM's page too.
- **Relaunch wording:** our own pending line instead of the game's "restart" line (the wording part only).
- **Review queue:** go through `WHATS-NEW.md` "For your review" (about 37 items) and prune its stale entries (see §8).
- **Round 14 note:** answer the cut-off "notepad ... if the original" ask.
- **Platforms and displays:** test 16:10 (Steam Deck), 21:9 and 4K, and relaunch under Proton. State "mouse and keyboard only" in the description.
- **Bug reports and translations:** `TRANSLATING.md` says "open a pull request", but there is no remote. Publish the repo or name a contact (Workshop discussions).
- **Code-quality pass** (`TODO.md:102`), with dead code in its own commit:
  - stale comments (the Tags.cs pip, the Tags.Make border, the ModDetails "flash");
  - the false `Patches.cs:15` header;
  - the unused key `SPIRE_MOD_MANAGER.VERSION`;
  - the stale README and DESIGN status text.
- **Localization formatting:** **Fixed (2026-10-13), with the self-test.**
  - Dates use the invariant culture, so month names are always English (`ModDetails.cs:241`).
  - Size units are hard-coded (`ModDetails.cs:332-338`).
  - `HistoryBar.cs:85-86` builds lines by concatenation.
  - Fix: use `LocManager.Instance.CultureInfo` and placeholders.
  - Done: dates use the game's own date pattern (`run_history` `INFO.DATE_FORMAT`, as its run history does) in the game's culture (`Menus.Culture`); sizes and subscriber counts use its separators, with each unit a key (`"{size} MB"`); `MODS_ENABLED` and `CHANGES` hold their `{count}`. Still in the Windows culture: the profile list's "Oct 11" (`Profiles.Details`, left for after the new-mods-profile branch).
- **Optional:** compile `SelfTest.cs` and the test hooks out of dist builds.

## 4. After 1.0

- **Views:** table view with sortable columns (including a load-order position column); filters for enabled, disabled and edited mods; minimap scroll bar; back/forward history.
- **Organizing:** drag and drop on the main list; colours for categories and tags; a tag manager (rename or merge); tag suggestions; Workshop tags as one-click own tags; tag-driven actions; click-to-search toggles; bulk-clear notes.
- **Profiles and load order:** a settings page with a profile details panel; undo per profile; load-order presets or overrides per profile; optional sync with the game's save slots; Steam Cloud sync. (Share codes moved to §2.)
- **Load order smarts:** finer Auto sort tiers; linked pushing while dragging.
- **Compatibility:** an audit beyond dependencies; status tags (Failed, Duplicate, Missing); click a missing dependency to open its Workshop page (spike first). (The managers warning moved to §3.)
- **Interop:** BMM re-import, csv export and toggle; a "Config" button for mods that have one; detect Quick Relaunch (only if it is ever published); import other managers' data after share codes (Load Order Manager's `presets.json`, MPM1 codes, BMM CSV).
- **Workshop:** Update now and Subscribe/Unsubscribe in the info panel (TODO round 2's "auto update and polling", redefined); one package for every branch once the range widens past beta (Load Order Manager's loader picks a build per game version).
- **Languages:** Simplified Chinese right after 0.1.0. Most mods on this Workshop are Chinese; English only for 0.1.0 stands.
  - **Translation plan** (Jordan, 2026-10-13: no AI translation; Simplified Chinese first, by a native speaker; community contributions later). The rest is recommended by the comparison session.
    - Freeze the English text for 0.1.0 after the copy pass (§2, 8). On `dev` today: 219 keys, about 860 words. The Workshop description adds about 1,000-1,500.
    - A kit for the translator: a spreadsheet, not JSON (key, English, where it appears, notes on placeholders and length limits, translation). A `tools/loc.cs export|import` moves it to and from `localization/<lang>/settings_ui.json`. Ideally it also flags translations whose English changed since (a hash of the English per translated key).
    - Screenshots of every screen, ideally from a dev mode that shows each key in place of its text.
    - A glossary from the game's own Simplified Chinese tables (`tools/pck.cs` extracts them), so our words match what players already see.
    - Who: Jordan asks the Chinese modding community first (Bilibili, QQ). The paid fallback is a native zh-CN freelancer or agency with game UI experience, "human translation only" in the brief; roughly low hundreds of USD (an estimate). Either way, a second native player checks it in game.
    - A translation can lag a release: a key it lacks shows in English. The font bug with other languages is fixed in its own PR (branch `locale-fonts`).
    - The Workshop description in English and Simplified Chinese, in one description (the norm here; Better Mod Menu does it).
    - Later: the spreadsheet by email or Discord until the repo is public, then GitHub pull requests. At 2-3 languages, Weblate or Crowdin (both free for open-source projects).
    - Details in TODO round 21.
- **Bigger features:** load mods without a restart (Jordan, 2026-10-13: a spike first, with crash-safe launch; §2, 14 has a finding for both); the manager during a run plus "save run and relaunch"; delete or uninstall a mod; an in-game error log; keyboard navigation and controller support; "Play vanilla" via `--nomods`; a link to the game's patch notes.

## 5. Publishing: how it works and the checklist

**How it works**
- The game has no uploader. Mega Crit's `ModUploader` CLI (github.com/megacrit/sts2-mod-uploader, v0.2.0) uses Steamworks with app 2868840.
- A workspace holds:
  - `workshop.json`: title, description, visibility, changeNote, tags, dependencies, minBranch, maxBranch;
  - `image.png`: required, under 1 MB;
  - `content/`: the files players install;
  - `previews/`: optional, under 1 MB each;
  - `mod_id.txt`: written after the first upload.
- `ModUploader.exe upload -w <ws>` creates the item the first time, or updates it by `mod_id.txt` later.
- The uploader **always** sets the branch range. An empty or missing `minBranch` means "all branches", and the newest valid upload wins, so set it every time. Branch order: public < public-beta < private-beta < dev-test.
- Once 1.0.0 is on the Workshop, your local 0.0.x copy loses the duplicate check. Use versions like `1.0.1-dev.N` for local builds.

**Release checklist**
1. All §2 blockers fixed on `dev`. The full self-test (all ~220 checks) passes on the release build, plus `--bmm` and `--real`.
2. Version `0.1.0` and manifest description set in `build.cs`. Release mode has no auto-bump.
3. Merge to `main`, tag `v0.1.0`, and run `dotnet run build.cs --dist`. It stops with an error unless `dist/SpireModManager/` holds exactly the `.dll`, `.json` and `.pck`.
4. Account: Steam Workshop legal agreement accepted (steamcommunity.com/sharedfiles/workshoplegalagreement); the account is not a limited user.
5. Get ModUploader v0.2.0 (win-x64) and run `ModUploader.exe new -w <ws>`. Keep the workspace outside the repo or gitignored, and keep `mod_id.txt` safe.
6. Copy `dist/SpireModManager/*` into `content/`.
7. Fill `workshop.json`:
   - title "Spire Mod Manager (Beta)";
   - description: pitch, features, "public-beta 0.111+", BMM coexistence, data location and backups, uninstall, mouse/keyboard only, where to report bugs;
   - `"tags": ["QoL","Utility"]`, `"dependencies": []`, `"minBranch": "public-beta"`, `"visibility": "private"`;
   - `"changeNote": "0.1.0: first release ..."`.
8. `image.png`: square, under 1 MB, your own art. `previews/`: JPG shots of the main list, Load Order lines, the info panel, Changes/undo and profiles.
9. Upload. On the item's Change Notes page, check that the branch range reads public-beta and up.
10. Clean-install test: remove the local `mods/SpireModManager`, move `mod_data/SpireModManager` aside, subscribe, and launch. Run through first run, the BMM import, Workshop details, Patch notes, relaunch, and unsubscribe.
11. Switch to `public` (or `unlisted` for a soft launch).
12. **Rollback:**
    - Keep the last good `dist/` zipped per tag.
    - For a bad update, re-upload the previous tag's content with a change note.
    - For a game patch that breaks it, upload a fix, or narrow the branch range so the broken build is served to no one.
    - Set visibility to private as a last resort. Never `remove`: that loses the item id and its subscribers.
13. **Updates:** bump `ReleaseVersion` in `build.cs`, rebuild with `--dist`, replace `content/`, set changeNote, set minBranch again, and upload.

## 6. Risks

- **Early Access churn:** every beta patch can rename the private members and node paths we depend on. Expect quick-turnaround updates; blocker 4 limits the damage.
- **Beta-only audience:** public-branch players will be confused. Widen the range the day 0.111+ reaches `public`.
- **Branch fields:** one upload without `minBranch` serves a broken build to public players.
- **Other mod managers** (Better Mod Menu, Load Order Manager, Mod Launch Manager, Mod Profile Manager, Mod Preset Sync, Better Mod List and more; the survey is in DESIGN.md) may patch the same screen, untested together. Likely the main source of bug reports. The test list is in §3.
- **Managers that rewrite `mod_list`** quietly undo our Load Order page: ModListSorter (A-Z on every exit), LoadOrderSync (at launch), Mod Launch Manager (pins itself first), Load Order Manager, Mod Preset Sync and Mod Profile Manager. The player sees their order change back with no word why. §3's warning covers it.
- **BMM users:** both run together now (round 17), but two managers on one page, with separate profiles, may confuse. The description should say what the warning means, and that BMM 1.1.1 has its own errors on the beta. The bridge reaches BMM internals by reflection; an update there can make our list jump again (the `--bmm` self-test catches it).
- **Data is local only** (not in Steam Cloud). It doesn't follow players between PCs or a Steam Deck.
- **Platforms:** relaunch is tested only on Windows; macOS, Linux and Deck are untested.
- **Support:** without a public repo, bugs arrive as Workshop comments.
- **Your own setup:** your local dev copy and the Workshop copy collide on version, and profiles saved with the local copy record the local source.

## 7. Decisions (Jordan, 2026-10-13)

1. **Version:** the first release is **0.1.0** (not 1.0.0). Read "1.0.0" elsewhere in this plan as 0.1.0.
2. **Branches:** beta-only at launch (`minBranch: public-beta`); see how it runs on stable later and widen the range then.
3. **New mods and profiles:** a newly installed mod joins the active profile as enabled.
4. **Source:** published, open source, not necessarily right away. License still to pick (MIT recommended: short, permissive, the same as Better Mod Menu). Bugs go to GitHub issues; Workshop comments get read now and then too.
5. **Preview art:** real screenshots for the previews; no generated art. Jordan may make the thumbnail.
6. **Visibility:** upload as private first (Steam's closest thing to a draft), test, then public. Unlisted is the fallback.
7. **Input:** mouse and keyboard only for 0.1.0, said in the description. Controller support is in the backlog.
8. **Round 14 note:** dropped; the notepad is where it should be.
9. **Repository:** private `jciurc/spire-mod-manager` on GitHub; work goes through branches and pull requests into `dev`, with CI.

## 8. TODO housekeeping

**Tick (done):**
- Backlog r1: larger previews on hover (Round 10).
- Round 2: multi-select and bulk in Category view (Round 11).
- Round 3: multi-select on the main page (Round 11).
- Backlog r1: mod patch notes (now a Workshop link, Round 15).
- Round 2: import BMM profiles (first-run import done; split out "re-import later" as its own backlog item).
- DESIGN Open: BMM coexistence; auto-enable missing dependencies; affects_gameplay for multiplayer; BMM import.
- DESIGN research ideas 1 (click a chip to filter) and 5 (bulk tagging).
- DESIGN load-order lines and drag feedback.

**Drop (obsolete or superseded):**
- Backlog r1: "selectable changes since X for patch notes".
- The recently-updated mark items (Updated tag, red pip, gem, the link, patch notes fetched per mod), all removed.
- Round 10: long-details test and long-titles backlog (superseded by Round 15; keep only the Round 15 long-text item).
- Round 1: "show Switched from X to Y" (superseded by Round 5).
- Backlog r1: "show installed/removed mods that differ from preset" (merge into the Round 8 profiles item).

**Keep, redefined:**
- Round 2: "auto update and polling" (once on the drop list as unclear) is now Workshop actions in the info panel: Update now, Subscribe/Unsubscribe (§4, TODO round 21).

**Rewrite:**
- `DESIGN.md`: "Status (2026-10-06)" says 68 checks (now about 220), lists the "Visual only" tag and a Workshop Page button, and says inline actions are rough. The "Draft layer" and "Defaults profile" sections describe what wasn't built. The content picker is now its own project.
- `README.md`: the Changes format, the automatic tags (add New, Local, Cosmetic), and the panel order.
- `WHATS-NEW.md` "For your review" stale entries: the ✎ mark, the panel head contents, the bare Workshop number, the patch-notes request and red link, the update gem, and the "(edited 14:19)" profiles entry.
- Split `WHATS-NEW.md` into a public `CHANGELOG.md` and dev notes.
- `TRANSLATING.md`: the pull-request route (depends on Question 4); mention that plurals are English-only (`_ONE` keys).

**Add:**
- The §2 blockers (Harmony per class, RowState, unwrapped patches, the ModToggled guard, Load Order handlers, Store backup and recovery, `--dist`, first-run fixtures).
- The release checklist above as "Round 16: release".