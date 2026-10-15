# What's new

Short notes for Jordan: what to look for after each install. Newest first.

## Waiting in pull requests

- **An example in the translations folder (translations-example):** right-click the main menu button, Open translations folder: `example-eng.json` is there, every English text under a few lines on how to make a translation from it. It never loads (no language has that name). Edit it and it's yours; left as it is, it's updated with the mod. Its date shows as 2000 in Explorer: that's how the mod tells it's untouched.

## Installed 2026-10-15: version 0.0.57

Everything below, merged as #71 to #79, plus the crash-safe launch draft (#65, still not merged; in this build again so you
can try it, as in 0.0.56).

- **Load Order search finds (load-order-find):** typing in Load Order's search no longer hides rows. Every mod stays, the first match is selected and scrolled to, and "6 matches · ‹ ›" beside the box steps through them (wrapping at the ends; Enter and Shift+Enter too). Drag and drop works with a search on.
- **The notepad and the "!" (note-icon-badge):** the small notepad before a mod's name now means notes only (an alias alone shows none; it's beside the original name already). Hovering it says "Contains note" in a plain tooltip after the usual delay; the title's card still has the notes. The "!" on the Load Order button now says just its warning ("For compatibility, …"), without the list of mods.
- **Main menu button (menu-button-trim):** it reads just "Default (12 mods)", smaller than before; "Relaunch to apply mod changes" sits centred above it while a relaunch is pending; hovering it says "Go to page for managing installed mods". The Settings screen's Spire Mod Manager button is gone.
- **Your copy review, first pass (copy-pass-2):** the 50 lines you marked to change, in your words. To look at: the page bar ("Mods installed (8)", "All enabled", "Changed since launch") and the filters' cards; "(disabled)" after a dependency that's off; the banners ("requires", Enable required mods); the info panel (Change Notes; a faint "unknown" or "No description"; "Requires mod to be enabled: Test Framework", one line naming them all); Delete profile ("Delete profile Default (12 mods)?"); Ctrl + z / y / s; the relaunch check's title staying "Before you relaunch". Bulk menu: the tags box says "Add tags to selected mods, comma-separated"; give two selected mods different notes and the warning "N mods have differing notes and will be overwritten!" shows beside Notes; hovering "Editing N selected mods" or its "i", Tags or Notes opens one card at once with your Tags and Notes explanations (no delay). The data notices (only after a broken data file) are in your paragraphs, with "mod's data folder" opening the folder.
- **The selected row (row-selected-flush, #71, merged):** the mod open in the info panel has an even 2 px gold rim on its brighter fill, and its edges line up with the picked rows' tint. Ctrl+click a few mods around it to compare.
- **Before you relaunch: Cancel takes your fixes back (relaunch-cancel):** untick a library an enabled mod needs and press Relaunch Game. Cancel is now first, at the left; Enable needed mods, Disable mods that can't load and Relaunch anyway are at the right. Press a fix, then Cancel (or Esc): the fix is taken back and the mods are as they were before the dialog (no Redo brings it back: as if you'd never opened it). The same from the main menu button's Relaunch game. Relaunch keeps the fixes, as before.
- **Every description in our card (hover-tips, #73, merged):** the small grey engine tooltips are gone from our buttons. Hover the Show filters (All, Enabled…), a banner's Show / Enable needed mods / Dismiss, the red "!" Load Order button, a row's category and ✎ buttons, Save while the profile is unsaved (Ctrl + S), or the ✕ in notes while editing: each says what it does in our gold-edged card, only as wide as its words. Better Mod Menu's own buttons keep theirs.
- **Alias, tags and notes (field-placeholders, #72, merged):** an empty box always says its call to action ("Add alias", in the bulk menu "Add tags to all of them"), focused or not. What a field is for moved to its gold term: hover "Alias", "Tags" or "Notes" for a card (in a mod's right-click menu too, over the menu). Select several mods and right-click one: an "i" beside "Editing N selected mods" explains how tags and notes work for all of them (draft words, yours to write on the review page). Tag chips inside a menu show their cards now too.

## Installed 2026-10-14 (afternoon): version 0.0.56

Everything below, plus the crash-safe launch draft (#65, not merged; it's in this build so you can try it before
deciding): after a launch that stopped before the main menu, if the mod list changed since, the next main menu shows
what changed with Restore (puts back the last working setup and relaunches) and Keep. Hard to see on purpose: it needs a
launch that never reached the menu.

- **Main menu button as text (menu-text-button):** the picture is gone. The button is the game's own main-menu text, like Singleplayer or Settings (same font, size and hover, with the little carets either side), and reads "Mod Profile: Default (12 mods)": the active profile and the count the profile bar shows (a long name ends in "…"; one mod reads "1 mod"). Switch profile or tick a mod and come back: it follows. Its width follows the text, at once, even for a much longer or shorter profile name; move it near the right edge and it still stays on screen, hover carets included. The dot and "Relaunch to apply your mod changes" still show beside it (on its left when it's at the right edge). Right-click: the menu now starts with "Spire Mod Manager v0.x.y" in gold over a line, which does nothing when clicked; the items below are as before.
- **A Spire Mod Manager button in Settings (settings-button):** open Settings from the main menu: "Spire Mod Manager" sits in the top-right corner, level with the General / Graphics / Sound / Input tabs, outside the settings list. It's a copy of the screen's own Mod Settings button, so it looks and grows on hover like it, and hovering it says "Opens Spire Mod Manager's page". Pressing it opens the Modding page; Back returns to Settings. Settings opened from the pause menu in a run has no such button (the game's Modding page can't open there). Try the 4:3 and 16:10 aspect ratios (Graphics tab): it should stay clear of the tabs.
- **Share and import a profile as CSV (share-csv):** open the profile list (or right-click the picker): Share profile and Import profile are at the end, under a divider. Share shows the active profile as CSV text in Better Mod Menu's columns (a row per mod in load order, its category as the group, no aliases or notes), with Copy (says "Copied"), Save CSV… (the system's save dialog) and Close. Import takes pasted text or Load CSV…, previews what it enables and disables compared with now and lists the mods you don't have (with their Workshop pages), then Create profile makes a new profile and switches to it. Ctrl+Z takes it away again. Better Mod Menu's own export imports too.
- **Drop-in translations** (branch `drop-in-translations`): right-click the main menu button: a new item above the divider, "Open translations folder", opens `mod_data\SpireModManager\translations` (made if it isn't there). Drop `<code>.json` in it (say `deu.json`, a copy of `localization/eng/settings_ui.json` with some values changed), switch the game to that language in Settings, and those texts show; the rest stay English. A broken file changes nothing; `godot.log` says why.

## Installed 2026-10-14 (morning): version 0.0.54

- **Another mod's box and the Changes list:** with the Changes (or Unsaved) list open over the info panel, another mod's part there (Undo And Restart's box) moves out of sight instead of being hidden, so if its own mod hides or shows it meanwhile, that holds when the list closes.
- **The data notices say where the file is:** if your data file can't be read, the notice now says the unreadable copy is kept "in the mod's data folder" (it said "beside it", where "it" pointed at nothing or at the backup).
- Nothing else to see: the Workshop description is refreshed for the upload, and `dev.cs --workshop <ids>` can copy subscribed Workshop mods into the test sandbox.

## Installed 2026-10-14 (early morning): version 0.0.53

- **Show filters:** the bar above the list has "Show: All · Enabled · Disabled · Changed". Enabled and Disabled go by the tickboxes; Changed shows the mods the Changes list names. They work with the search (a row must match both, counted as "Search matches") and the categories (each counts its rows shown; one with none hides), and Enable all / Disable all act on what's shown. Untick a mod under Enabled: it stays put until you press a choice again (or reopen the page, or change many mods at once: Enable all / Disable all, undo, a profile). A banner's Show, or a mod's name in the panel or in Changes, still brings up a mod the filter hides, and clears a search that hides it. Load Order and Vanilla List always show every mod; each launch starts on All.
- **Banners name their mods:** hover a banner's text and a card lists, at once, the mods it's about, one a line, by the names the list shows (aliases included). On the main list: each enabled mod that won't load and what it needs ("Test Content: needs Test Framework (disabled)", or "(not installed)"; several on one line). In Load Order: each mod placed before a mod it needs ("Test Content loads before Test Framework"). On the yellow managers banner: the mod managers that are on, as named by their mods. A long list stops after 12 with "and N more". The card is worked out when it opens, so after a tick it lists what's true now. The red "!" on the main list's Load Order button names them too: its tooltip has the same "loads before" lines under the warning.
- **Smoother lines in Load Order:** the dependency lines have soft edges (the game's renderer ignores Line2D's own antialiasing, so they fade at their edges instead), and sit 4 px apart so they don't touch.

## Installed 2026-10-14 (early morning): version 0.0.52

- **Load Order polish:** hover or select the top row in Load Order: its highlight has its rounded top and the selected look's top edge again (the list keeps a few pixels above the first row and below the last, so the bottom row at the end of a long list is whole too). With the game in Chinese or Japanese, "Ctrl + Z" and "Ctrl + Y" under Undo and Redo stay apart, each under its own button (a little smaller where they must). Nothing to see in the last one: names with an alias, or cut with "…", are no longer fitted again on every redraw.

## Installed 2026-10-13 (late night): version 0.0.51

- **Banners:** the "Multiple mod managers" banner is yellow now (a warning); the page's own warning stays red. In both, the buttons sit right after the text instead of at the far right. Hover Show on either banner: both tooltips say "Opens the next affected mod" (the buttons still say Show). And the managers banner no longer names Better Mod Menu: Dismiss's tip says the warning comes back if one of the mod managers is disabled and then enabled again.
- **Chinese, Russian and other scripts:** with the game in Chinese, Japanese, Korean, Thai, Russian or Polish, our text uses the game's font for that language, as its own labels do, instead of Kreon. Names, tags, notes and profile names in those letters show properly, not as boxes. Our own words stay English until the mod is translated. After changing the language, open the Modding page again.
- **Titles and numbers:** in Vanilla List, long names the game shrinks to fit stay that size after a redraw (they used to jump back to full size). Numbers of 1,000 or more (profile mod counts, category counts, the page bar, "Required for N") follow the game's language, e.g. 1.234 in German. Another mod's box in the info panel (Undo And Restart's) is moved out of sight when there's no room, so its own mod always decides whether it shows.

## Installed 2026-10-13 (late night): version 0.0.50

- **Faster with a long list:** switching between the main list, Load Order and Vanilla List takes about 0.1 s with your ~250 mods (about 0.5 s before). A redraw is about 21 ms (63 before) and a search keystroke 22 ms (69). Everything should look exactly as before.
- **Softer corners in Load Order:** hover a mod and the dependency lines bend round their corners instead of turning square. The drop line while dragging has rounded ends, and the red warning banners (on both pages) rounded corners. The rows already had the main list's rounded highlight; the top row can still look square on top, as the list's edge cuts it off (noted in TODO).

## Installed 2026-10-13 (night): version 0.0.49

- **Dismiss the managers warning:** the red "Multiple mod managers" banner has Dismiss beside Show. It stays away until Better Mod Menu or Spire Mod Manager is disabled, and comes back once both are enabled again (also after a launch where one of them is gone or off). Nothing to undo: dismissing isn't a change to your mods.

## Installed 2026-10-13 (evening): version 0.0.48

From staging: dev plus every open pull request. Round 19, then the fixes from tonight's Copilot reviews.

Round 19
- **"Unsaved •"**: its card opens at once on hover. Clicking it lists that profile's unsaved changes in the side panel (click a mod to open it); clicking again closes the list (#26).
- **The restart line** on our pages reads "Mod changes will be applied after a restart." Vanilla List keeps the game's wording (#28).
- **Two mod managers**: with Better Mod Menu ticked too, a red banner above the list says "Multiple mod managers are currently enabled, which may cause unexpected behavior.", with Show to go to its row. It appears and goes as you tick, without a relaunch. The old warning by the view buttons is gone (#28).
- **Load Order**: the Auto sort hint is in place as soon as the page opens, not over the first row (#27).
- **Main menu button**, right-click: Relaunch game, under a divider below Reset position. It runs the same check as the Modding page's button first (#29).

Fixes from the reviews
- **Long names in the panel** keep their fitted size after going to Load Order and back (they could jump to full size, on two lines) (#11).
- **Show in a banner** clears a search that hides the row it goes to (#28).
- **Escape** pressed the moment you pick Move button no longer leaves move mode listening in the background (#29).
- **Numbers** in the Changes line, its card and its list take the game's thousands separator (1.234 in German); a date falls back to English, not half-translated (#21).
- **Tag suggestions**: a tag counts once per mod in "most used", even if saved twice in different case (#17).
- **Undo And Restart's box** (or any other mod's part in the panel): if its mod hides it, it stays hidden when room comes back (#22).
- Nothing visible: the last direct font loads go through the fallback (#12).

## Pull requests of the night of 2026-10-13 (installed since 0.0.46)

Each PR's description has its own "What to look for" and "For review"; the short version:

- **#1 Better Mod Menu:** the button keeps its Vanilla List name; the warning sits right of the view buttons: "Better Mod Menu is running alongside Spire Mod Manager and may cause unexpected issues."
- **#5 Fail soft:** if a game update breaks a patch the page needs, the page stays the game's with "Spire Mod Manager needs an update for this game version." on top (beside the main menu button if the screen patch itself broke).
- **#6 Data safety:** an unreadable manager.json is kept aside and the newest backup loads, with a one-time popup (at the main menu or on the Modding page).
- **#7 New mods and profiles:** a new subscription joins your active profile, on; no more "Unsaved" from new mods, and Discard doesn't turn them off. Switching to a profile that never saw a mod leaves it as it is.
- **#14 Bug fix:** mods subscribed while the game runs were never tracked in real play (no New tag, no "Installed" line in Changes). Worth one real try.
- **#2 First run:** with no categories yet, a dim hint under the page bar says how to make one.
- **#9 Search:** clicking a tag again clears the search; Shift-click adds or removes it.
- **#10 Popups:** Delete profile, Delete category and New Mod Added say "Spire Mod Manager" in small faint letters under their buttons.
- **#11 Long text:** long names and ids in the panel are cut with "…" (version and status still showing); rows with many tags fold the rest into "+N".
- **#15 Speed:** redraws and search with long lists do much less work (your list: about 49 ms per redraw, from 66-70).
- **#17 Tag suggestions:** your own tags drop down under the tags box, most used first; arrows and Enter, Tab, a comma or a click take one.
- **#19 Copy:** "relaunch" everywhere ("Pending relaunch"), "disabled" in sentences, a renamed profile reads "Renamed profile Old → New" in Changes.
- **#21 Language:** dates and numbers follow the game's language (English unchanged).
- **#22 Undo And Restart:** its settings box sits under the description instead of over your fields.
- **#12 Fallbacks:** if a game update breaks a resource we load, plain buttons and text arrows instead of a broken page.
- **#16 Better Mod Menu import:** first runs also read its json5/jsonc files, comments, its old format and portable mode.
- Tools and docs: #3 `build.cs --dist` (0.1.0), #4 the Workshop kit (read `workshop/description.bbcode`; take the shots in `workshop/SHOTS.md`), #8 CI, #13 dev tools, #18 two design spikes with questions for you, #20 code cleanup.

## Installed 2026-10-12 (afternoon): version 0.0.45

Better Mod Menu (round 17)
- **Both can be on.** With Better Mod Menu enabled, our main list and Load Order work as usual. Its bars, row arrows and panel buttons are hidden there, and its groups don't show in our list.
- A gold line at the top right says **"Better Mod Menu is also on…"**. Hover it for what that means.
- The **Vanilla List** button is now called **Better Mod Menu** and opens its own page in place of the game's list. **Spire Manager** brings ours back. Switching back and forth should leave both pages where they were.
- **Profiles never turn Better Mod Menu on or off**, like our own mod, and don't count it. Its profiles and ours stay separate. Both act on the game's one mod list: a change here also lands in its current profile, and switching its profile shows up here as unsaved changes.
- Worth knowing: the Better Mod Menu you have installed (1.1.1) has its own errors on this beta (its panel buttons fail to load). Your local-fix build doesn't. Both pass our tests alongside us.

Search (round 15)
- **Fixed:** a search while scrolled down could show "Search matches (2)" over an empty list. The game's list stops scrolling once everything fits, which left the few matches above the view. The list now jumps to the top when it shrinks to fit (search, Collapse all, collapsing a category).

Rows (round 15)
- Taller rows with less space between them (same spacing overall), so the title no longer touches the tag line.
- The **pencil** sits past the tickbox, at the row's far right (the tickbox moved a little left).
- Highlights: one shape for hover, keyboard, selected and picked rows, 2 px wider each side, 4 px corners. Hover is fainter. The mod shown in the panel looks **sunken** (deeper blue, dark rim).
- The notepad is smaller, a pixel up and left. Only the main title (the alias) opens the card, not the original name beside it.
- Long authors and tags are cut with "…". Hover a cut author (or cut original name) for a second to see the whole name in a plain tooltip. A cut tag's card starts with the whole tag.

Tooltips (round 15)
- Moving from one card's trigger to another (a title to a tag) opens the next card at once.
- When a card's text is cut short (long notes, alias, tags), it says **"Hold Alt to see the full text"**. Holding Alt shows everything in a wider card.
- The mod card has a little air before and after the alias.

Info panel (round 15)
- **Patch notes** opens the mod's change notes on the Workshop in your browser (no more patch notes page, no more requests from the mod).
- Head order: Author, Updated, Compatibility. "Updated" says "(3 hours ago)", "(1 day ago)" or "(5 days ago)", in green when under two days.
- Source: "UndoAndRestartButton (SteamID 3794947020)". The status sits a pixel higher, level with it.
- Alias: "Add alias" at rest, "An alternative title for the mod" while typing. Notes are 2 pt smaller.

Fixes from the round 15 review
- Vanilla View keeps the game's own row spacing (a change this round had squeezed it).
- Rows sit a few pixels in from the list's sides, so the wider, rounded highlight isn't cut off at the edges.
- Hovering a mod's name in the panel or changes list lights its row visibly again.
- "Patch notes" works for a Workshop mod that also has a local copy with the same id.
- An update under an hour old reads "(less than an hour ago)".
- Holding Alt on a long card no longer leaves blank space at the bottom.

Text (round 15)
- "Installed mods (n)" on the bar under the search.
- Gameplay tag: "...All players will need it enabled for multiplayer." Library tag: "This is a framework intended to be used by other mods; often has no effect when installed alone".

## Installed 2026-10-12 (late night): version 0.0.44

Rows (round 14)
- No more red "recently updated" mark.
- The notepad (your alias or notes) sits **left of the mod's name**. Titles moved right a few pixels to make room on every row, so they stay lined up.

Info panel (round 14)
- Head order: Author, Compatibility, Updated. Updated ends with a blue **Patch notes** link for Workshop mods, e.g. "October 7, 2026 (4 days ago)  Patch notes". The link next to the status is gone.

## Installed 2026-10-12 (late night): version 0.0.43

Tooltips (round 13)
- One set of rules for every hover card: it shows after **0.4 s**. After one closes, the next opens at once for **0.25 s** (the grace period). Leaving waits **20 ms** before the card goes, so a wobble off the edge doesn't close it.
- The row's **notepad** card shows the moment you hover it and goes the moment you leave (no grace period after it).
- Only **one card at a time**: a newer card closes the one before.
- The Changes summary and the "Unsaved" card follow the same rules (they used to open instantly).

## Installed 2026-10-12 (late night): version 0.0.42

Rows (round 12)
- Recently updated: a small **red gem** before the title instead of the dot. It goes once you open the mod (remembered between sessions, until its next update). The panel still says "Recently updated".
- The update card ("Updated on the Workshop on …") is wider, so it doesn't wrap early.
- The row's hover / selected highlight and the blue tint of picked mods have slightly rounded corners.
- Tags: flat, small corners, no outline (your own tags fainter), a size smaller, 2 px lower. "Needs / Required for" and the author are a size smaller too.
- The author is just the name ("symphony", no "by") and isn't clickable on the row. Search by author from the info panel.

Info panel (round 12)
- The author searches on click, and its hover card says how. Names over 200 characters are cut.
- **Workshop tags** are clickable: they search as plain words, Shift adds them.
- "Needed by" now reads **"Required for"**.
- Every click-to-search shows the same hint: "Click: search this term; Shift-click: add to search".

Network (round 12)
- The update gem comes from the one Workshop details request the mod already makes per session (through the Steam client), so it costs nothing extra.
- Patch notes are the only web request. They're fetched only when you open them, once per mod per session. After a failed request (offline, or Steam saying "slow down"), nothing is asked again for two minutes.

Info panel
- Hover the thumbnail for a fifth of a second: the whole picture, up to 800 px on its longer side.

Picking several mods (main list)
- **Ctrl-click** rows to pick them, **Shift-click** for a range, **Ctrl+A** for every mod shown. A category header's right-click menu has **Select all**. Picked rows are tinted blue. A plain click or **Esc** lets go.
- **Right-click a picked mod**: on the left, the category for all of them (marked when they share one), New category, Enable all / Disable all. On the right, **"Editing N selected mods"** where the alias would be, then Tags and Notes:
  - Tags shows the tags they all share. Tags you add go on every one, tags you remove come off every one, and each mod keeps its other tags.
  - Notes shows their notes if they're all the same. If they differ, the box says so, and typing replaces them all.
- Every bulk change is one undo step.
- With mods picked, the bar under the search reads "Selected (N)": Enable all / Disable all act on them, plus **Clear selection**.

Info panel
- Click a mod's name (the title, or a dependency) to scroll to it in the list. It takes the blue "selected" look, but the panel stays on the mod you were reading.
- Hover a dependency's name: its row lights up if it's in view (nothing scrolls), and its card shows after half a second. The changes list's names do the same.
- Workshop mods: after the status, **Recently updated** (red, updated in the last week) or **Patch notes**. It opens the mod's change notes from the Workshop in the panel: name, id, author, last update, then each update's notes, newest first. **Back** returns.

Faster
- Redraws on your full list: about 21 ms, down from 37–49.

## Installed 2026-10-12 (night): version 0.0.41

Main list
- A bar under the search box: "All mods (53)" (or "Search matches (4)" while searching), **Enable all / Disable all** for those mods (one undo step), and **Expand all / Collapse all** on the right.
- Category headers: **Enable all / Disable all** at the far right, over the tickboxes (Uncategorized too). Greyed out when there's nothing to do.
- Search box says just "Search".

Rows
- The mod's card shows after **half a second over the title** (not the whole row). It starts with "id • Enabled".
- The notepad is smaller and sits right after the title, before the original name. Hovering it shows the card after a tenth of a second.
- **Grace period:** once a card closes, any other card (titles, notepads, tags, authors) opens at once for twice its delay. Moving down the list doesn't wait each time.
- A **red pip** before the title marks a Workshop update in the last week (it replaces the Updated tag). Hover it for the date.
- A little more room between the title and its tag line.
- The author ends the tag line: **click "by symphony"** to show their mods, Shift-click to add them to the search.

Tags
- Hovering a tag shows a styled card: what it means, then, after a gap, how to search for it (small, faint).
- Gameplay: "This mod affects gameplay. All players will need it installed for multiplayer."
- Tags in the info panel work like the rows' (card, click to search, Shift to add).
- Search: `#tag` matches a tag, `@author` an author (quotes for spaces: `@"Two Words"`).

Info panel
- Status sits on the source line's middle.
- The thumbnail sits a little further right, the picture against the box's right edge.
- Head: Author (click to search), Updated, Compatibility. Below the description: Size, Published, Subscribers, Workshop tags, then dependencies.
- Alias placeholder: "Your nickname for the mod".

Relaunch
- If mods that are on won't load (a needed mod is off or missing), **Relaunch Game** lists them first. **Enable needed mods** and **Disable mods that can't load** fix them right there (undoable). Then **Relaunch**, or **Relaunch anyway**, **Cancel**, or Esc. If nothing's wrong, it relaunches straight away as before.

Main menu
- Move button: "Click to place · right-click or Esc to cancel". The cursor jumps to the button's left end, halfway up, so you hold it by its edge.

## Installed 2026-10-12 (evening): version 0.0.40

Version
- The mod now reads **0.0.40** (title in the info panel, game's mod list). The last number goes up by one with every install, so you can tell when there's a new build.

Also in this build
- Click a tag on a row to show mods with it; Shift-click adds it to the search ("#tag" in search matches tags only).
- New tags: **New** (installed this session) and **Updated** (Workshop update in the last week).
- Category right-click menu: **Enable all / Disable all**.

Rows
- A little shorter. Tags always in the order Local (new: hand-installed mods), Library, Gameplay, Cosmetic, then yours.
- "Required for N" comes before "Needs:".
- A small notepad after the name marks your alias or notes (main list only).
- A quiet pencil (no box) and the Category button, on the right, in line with the tickbox. The pencil opens the edit menu.
- Hover any row for a second: a card with name (id), on/off, problems, author, updated date, alias, tags and notes. Cards close as soon as the mouse leaves.

Load Order
- No tickboxes. Right-click a mod to **Enable / Disable** it (above the moves).
- A moved mod shows where it was at launch, dimmed: "6 → 3". The numbers stay in even columns.
- "Automatically sort for compatibility" sits beside Auto sort.
- The warning reads "For compatibility, N mods will be automatically reordered by the base game on relaunch."
- When the order has that problem, a red **!** on the Load Order button (main list) shows the warning on hover.

Main list
- A red banner when enabled mods need a mod that is off or not installed. **Show** steps through those mods. **Enable needed mods** turns on the ones that are installed, as one undo step.

Info panel
- Title with the version in grey beside it (no "v").
- Under it: "SpireModManager (3456789012)" (links to the Workshop page) or "SpireModManager (Local)", then **Enabled / Disabled / Pending restart** at the same size.
- Author, Updated (with "N days ago") and Size beside a thumbnail twice as big, a little further from the edge. A mod without a picture: the description moves up.
- Dates read "October 12, 2026". Compatibility sits with Dependencies / Needed by at the bottom of the details.
- Alias, Category, Tags and Notes sit in soft dark boxes: lighter under the mouse, darker while typing, no outlines. The automatic tag pills are on the Tags line, beside the word.

Changes
- Left-aligned and a little lower: "Mods enabled 5 (+1 -0)" over "Changes 2  ↕3".
- The hover card is titled "Changes since launch". Under fewer than 20 mods turned on, off or installed, it lists them by name.
- In the full list, click a mod's name to open it (the list says so). **Back** (top right) returns to the list. It goes away when you pick another mod or leave the page.
- Mods installed while the game runs get their own "Installed" section, with whether each will load after relaunch. Unticking one now sticks (before, the game ignored it and loaded the mod anyway). They also count as pending.

Other
- **Ctrl+S** saves the profile (the Save button's tooltip says so).
- Redraws are about a third faster on your full list.
- New-mod popup: "New Mod Added" / "New mods were detected, and will require a restart to be enabled."; at most one every 30 seconds.

## Installed 2026-10-12

Latest
- Panel fields in the order Alias, Category, Tags, Notes (the right-click menu: Alias, Tags, Notes).
- "Changes N" no longer counts load-order moves (they're the ↕ on the line above).
- The profile bar shows how many mods are on, beside the profile's name.
- Mods you subscribe to while the page is open show up in our row style, in their category, right away.

Fixes
- Main menu **Move button** works: the button moves by as much as the mouse does (no jump), a click puts it down, right-click or Esc cancels, and the hint goes away (also when you leave the menu).

Rows
- No pencil; a faint ✎ after the name marks mods with your alias, notes or tags.
- Hover a row for a second: a card with your notes (and the full name if it was cut short).
- "Required for N enabled mods". Tag tooltips; a purple **Cosmetic** tag when the Workshop tags say skins/visuals/UI/textures.
- Category headers: the mod count right after the name.

Right-click a mod (or its Category button)
- Two columns: categories on the left, the mod's **Alias, Notes, Tags** on the right, edited right there.
- Right-clicking elsewhere closes it and right-clicks what's under the mouse; clicking the same button again closes it; Esc closes it.
- New category scrolls to its name box.

Info panel
- Name; "SpireModManager v0.1.0 (Workshop …)" as the link; **Enabled / Disabled / Pending restart**; problems; description; author, dates, size, subscribers, compatibility (each branch once), Workshop tags; dependencies.
- Click the name to find the mod in the list; click a dependency to scroll to it with a short glow.
- Fields: **Alias, Category** (a picker), **Notes, Tags** (automatic tags as pills above yours). "Add alias" / "Add notes" / "Add tags" at rest; padded boxes with an inline ✕; Save and Cancel while editing; notes trimmed.

Changes
- Two lines by Undo: "Mods enabled 23 (+1 -3) ↕2" and "Changes 5" (moves, edits, profile changes). Undo buttons now centred on the row.
- Hover shows each kind, edits by field (alias, notes…). Click opens the full list (edits list the new alias); click again closes it.
- Undo, Redo and Revert all no longer open the list or empty the panel; with the list open, ticking a mod keeps it open.

Profiles
- Unsaved profiles have a red * in the list; hover "Unsaved" to see that profile's own changes (+ / −).
- Name boxes (profiles, category rename) have an inline ✕ instead of a Clear button.

Search
- Any case, and spaces ignored ("mod fixes" finds "ModFixes"); after each change the list starts at the top.

"Main Menu" sits a little higher.

## Installed 2026-10-11, evening

Main menu
- Your skyline button, with our label; it brightens on hover like the game's buttons.
- Right-click it: **Move button** (it follows the mouse, click to place, right-click or Esc to cancel) or **Reset position**.
- While a relaunch is pending: the dot plus a short coral "Relaunch to apply your mod changes".

Main list
- Rows: libraries say "Required for 3 mods"; "Needs: X" is green when X is on, red when it's off.
- Turning a mod on or off updates the rows' notes and the info panel at once (no leftover "(off)").
- Mods turned on since launch: bright gold title; turned off: a dimmer, dusky gold.
- Right-click a mod: **Add/Edit alias, notes, tags** right in the menu (Save or Cancel bring the menu back), then its category (a dot marks the current one).
- The alias pencil stays visible while you hover it; the inline alias box no longer covers the tags.
- Renaming a category to a taken name warns and isn't accepted.

Profiles
- "Unsaved •" (coral) with **Save / Save as new / Discard** beside it; **New / Duplicate / Rename / Delete** on the right.
- New, Duplicate, Save as new and Rename open a name box with Save / Cancel / Clear; copies are offered as "Name (1)", "(2)"...
- Empty or taken names warn and can't be saved; Delete can't be clicked mid-rename (the other buttons hide).
- Leaving a profile with unsaved changes keeps them with it: "Name *" in the list, and they're back when you return.
- The list opens under the picker with each profile's mod count and save date; clicking the picker again closes it.

Info panel
- **Tags, Alias, Notes** right under the mod's details. At rest they read like text (or a subtle "Click to add tags"); clicking one shows Save / Cancel / Clear. Enter saves (Ctrl+Enter for notes), Esc cancels.
- Workshop branch ranges read "up to beta" / "stable and later".

Changes, undo, revert
- "Changes +0 -3 ↕2" now sits left of Undo; ↕ (blue) counts mods moved in the load order.
- Hover: a summary (switched profile, Enabled / Disabled / Load order counts, edits). Click: the full list in the right panel (each mod, each move with old and new place, edited mods, profiles added or removed).
- Undo, Redo and Revert all also open that list (Undo no longer empties the panel).
- Undo and Revert all now cover the same span (since you opened the page), so they agree.
- The undo buttons sit a little higher.

Load Order
- A press starts the drag at once (dimmed list, drop line); a click without moving just selects.
- Hover lines follow when you scroll with the wheel.
- No profile bar and no Vanilla List button; Return sits in the corner.
- "Main Menu" over the back arrow (also on the main list).

Fixes found in the audit
- With Better Mod Menu on, a tickbox change no longer adds our tags and Category buttons to its rows.
- The info panel refills when Workshop details arrive after the page opened.
- Clearing everything on a mod (or deleting its category) no longer counts as an edit.
- The panel no longer writes stale text over an alias set from the row's pencil.

## Installed 2026-10-11

- Thumbnail in the info panel stays small (the game's 500x380 minimum size was overriding ours).
- Profile bar: plain name with "Unsaved •" beside it; buttons on the right.
- Leaving the Modding page from Load Order reopens it on the main list.

# For your review

Choices I made on my own; say if any should change.

- **New** makes a blank profile (only Spire Mod Manager on), since Duplicate copies the current one.
- **Clicking away from a name box cancels** (profiles; categories keep the old name if the new one is taken). Tags, alias and notes save when you click away, so typed text is never lost.
- **"Unsaved •" is coral**, different from the gold names and the red warnings.
- **Old "Name (edited 14:19)" profiles** from the previous build are ordinary profiles now; delete any you don't want.
- **Profile list details**: "5 mods · Oct 11" counts mods that are on (not counting this one); profiles from before this build show no date until saved once.
- **Load-order count** uses "↕2" rather than "+2", so it doesn't read as mods added.
- **Hints without dots**: "Click to add tags", "Custom notes".
- **The changes list** only exists on the main list (Load Order has no right panel); there, hover the count for the summary.
- **Revert all and Undo** also undo profile creation and deletion made during the visit (they restore all our data as it was when you opened the page).
- **Load Order profiles**: I took your first option and removed the profile bar there; separate load-order profiles are in the backlog with load order presets.
- **Main menu button art** is used only on the main menu; the corner button on our pages keeps the game's art.
- **Notes inside the right-click menu** use a 4-line box in the menu, the same as tags and alias.
- **The ✎ mark** on rows means "has your alias, notes or tags"; it isn't clickable (alias editing is in the menu and the panel now).
- **"Mods enabled"** counts mods that are on, not counting Spire Mod Manager itself.
- **Dependencies, clicked**, scroll the list and glow the row but don't change the panel; the panel's name, clicked, just scrolls.
- **Load Order "was" column**: launch positions count only mods installed now, so a mod added since launch shows no "was".
- **The main-list banner** covers dependency problems only (off or missing). Load errors stay in the panel; say if you want them in the banner too.
- **The pencil** is the plain ✎ glyph, faint until hovered (then gold), with no box.
- **Panel head**: the space beside the bigger thumbnail holds Author, Updated and Size. Published, Subscribers and Workshop tags stay in the details below.
- **Workshop number** shows bare, "(3456789012)", per your note; it's still the link to the page.
- **Field boxes** are always faintly visible now, so the fields read as clickable. The category picker matches.
- **"Mods enabled N"** includes mods installed this session that are on, since N is what will load after a relaunch. The (+ -) counts only cover mods that were here at launch.
- **Back from a mod opened via Changes** sits top right. When the mod has a picture, the picture moves down to make room.
- **The changes list** says "Click a mod to open it." under its title.
- **Relaunch check** only looks at dependencies. Load order problems don't stop a relaunch, since the game sorts those itself.
- **The page bar** shows only on the main list, not in Load Order. Expand/Collapse all are greyed while searching (search opens every category anyway).
- **Category Enable/Disable all** sit at the far right of each header, over the tickbox column. Delete stays with Rename/Up/Down.
- **Patch notes** come from the Workshop's change notes page (one request per mod, only when you open them). Steam limits how often that page can be read, so it may say "Couldn't load" for a while after many requests.
- **"Patch notes"** shows for every Workshop mod. "Recently updated" (red) is the same link, used when the update is under a week old.
- **Clicking a name** in the panel no longer flashes the row gold. It gives the row the game's blue "selected" look instead (it really takes focus, like a keyboard selection).
- **Bulk menu extras**: besides the category, it offers New category and Enable all / Disable all for the picked mods. Ctrl+A picks every mod shown (in Load Order too, for dragging).
- **Bulk notes**: leaving the box empty when their notes differ changes nothing. Clearing everyone's notes at once isn't possible yet.
- **Your own tags** without an outline are a fainter fill of the same colour, to tell them from the automatic ones. Easy to tune.
- **The update gem** is cleared by opening the mod in the panel (any way: clicking its row, its name somewhere, the changes list).
- **"Across the board"** includes the thumbnail's full picture (0.2 s before, 0.4 s now), the Changes summary and the Unsaved card (both instant before).
- **Cut text tooltips** are our own plain tooltip with a 1 s delay, not Godot's. The game sets Godot's tooltip delay once at startup for the whole window, so a per-element 1 s wasn't possible with it. Ours also follows the one-card-at-a-time rule.
- **Sunken selected look**: a deeper blue fill with a dark rim, heavier along the top. Hover is a faint light-blue tint. Both easy to tune.
- **Better Mod Menu's button** keeps the name "Vanilla List" while it's loaded (your call in #1), though it opens Better Mod Menu's page.
- **Profiles leave Better Mod Menu alone** (on or off as it is), like our own mod. Before, converted Better Mod Menu profiles switched it off, which would now turn it off at the next relaunch for anyone using both.
- **The two-managers banner** shows on our pages while Better Mod Menu and Spire Mod Manager are both ticked (loaded or not), and not on Better Mod Menu's own page, so that page looks like its own. With our own row unticked it goes, since only one manager will load.
- **Its Dismiss** is kept like the view toggles: saved, but not a change to your mods (no undo step, no "Unsaved", not in Changes). Switching either one off forgets it, at once on our page or at the next launch otherwise. A switch off and back on that we never see (both between two launches, or on the game's own page while ours stands aside) ends where it started, so the dismiss stays.
- **A banner's Show** clears the whole search when the search hides the row it goes to (rather than leaving a hidden row selected). A search that already shows the row is kept.
- **Other mods' parts in the panel** (Undo And Restart's box) are handed back to their mod before each fill, so a mod's own hide wins. A mod that hides its part some other way, outside the panel's fill, could still see it shown again once room comes back.
- **Language**: when the game's date pattern is missing or broken, dates fall back to English ("October 11, 2026"). Load-order positions ("3 → 5") stay plain digits like the row numbers. The profile list's "5 mods" isn't grouped yet (it never reaches 1,000).
- **Missing dependency links** (spike #18): a Required Item is linked only when its title is the dependency's id; anything else opens a Workshop search. A mod installed during the session gets the search until the next launch.
- **Enable all / Disable all** still include Better Mod Menu like any other mod.
