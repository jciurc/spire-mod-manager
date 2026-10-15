# Workshop pictures: shot list

Real screenshots from Jordan's own game (docs/dev/DESIGN.md: no generated art). Dev runs can't make them:
they have no Steam, so the info panel shows no Workshop details, and they keep other mods off.

## Before any shot

- Launch through Steam on public-beta, with your usual mods on. A long list is the point: it shows why
  categories and search matter.
- Install the build you're about to upload.
- Play in 16:9 (1920x1080 or 2560x1440).
- Check that nothing private shows: notes, profile names and category names will be public. Names that read
  well to strangers help: "Characters", "Quality of life", "Libraries", "Visuals"; a profile called "Co-op".
- With Better Mod Menu on too, the yellow "Multiple mod managers" warning sits above the list on the Modding page.
  Before the Modding page shots, Dismiss it. It isn't a change to your mods, so there's nothing to undo; it comes back once one of the
  two is disabled and enabled again.
- F12 is Steam's screenshot key (Steam Settings > In Game). Find the shots in Steam's screenshot manager
  (View > Screenshots, Slay the Spire 2, Show on disk). Steam saves JPGs. "Save an uncompressed copy" in the
  same settings also keeps a PNG, which `shrink.cs` turns into a sharper JPG.
- Put the ones you pick in `workshop/previews/`, named in order (`01-main-list.jpg`, `02-load-order.jpg`, ...).
  The uploader adds new files in name order, so the numbers set the order on the Workshop page.
- Undo every change you make for a shot (Ctrl+Z, Discard or Revert all) before the next one.

## The shots

1. **`01-main-list`: the main list and the info panel.** The first picture people see; it shows the page at a
   glance.
   - Main list (not Load Order, not Vanilla List). Three or four categories with a few mods each, one of them
     collapsed.
   - At least one mod with an alias and your own tags, so the fainter tag chips show.
   - Select a popular Workshop mod that needs a library (a character mod that needs BaseLib, for example). The
     panel then shows its Workshop dates, subscribers, Workshop tags and Dependencies.
   - Keep the mouse off the rows, so no hover card covers them.
2. **`02-load-order`: Load Order with dependency lines.** No other manager shows this.
   - Load Order view. Start dragging a mod that needs a library, hold it halfway, and press F12: the list dims,
     the mods it's linked to stay lit, and the lines between them are green where the order works and red
     where it doesn't.
   - Hovering a library that several mods need also works (lines to each of them).
   - Optional: first drag a mod above the library it needs, so the "will be automatically reordered" warning
     and Fix now show in the same shot.
3. **`03-profiles`: a profile with unsaved changes.** Profiles that show their diff and never lose work.
   - Main list, a profile with a good name active.
   - Turn one mod on and two off. "Unsaved •" appears with Save, Save as new and Discard.
   - Hover "Unsaved •" so its card lists the + and − changes. Discard afterwards.
4. **`04-changes`: the Changes list.** The safety net: everything since launch, and undo.
   - Make a handful of changes: two mods on, one off, one moved in Load Order, a note edited.
   - Back on the main list, click the Changes line at the bottom right. The full list opens in the right
     panel; Undo, Redo and Revert all sit beside the Changes line.
   - Revert all afterwards.
5. **`05-search-select`: search and several mods at once.** Organizing a long list quickly.
   - Search for `#Library`, or `@` and an author with several mods, so the bar reads "Search matches (n)".
   - Ctrl-click three or four matches (they turn blue), then right-click one. The menu shows the categories
     on the left and "Editing N selected mods" with tags and notes on the right.
6. **`06-relaunch-check`: the check before relaunching.** It catches a broken launch before it happens.
   - Turn off a library that an enabled mod needs, then click Relaunch Game.
   - "Before you relaunch" lists the mods that won't load, with Enable needed mods and Disable mods that
     can't load. Take the shot, press Esc, and undo.
7. **`07-main-menu` (optional): the main menu button.** Shows where to find it.
   - After any change, go back to the main menu: the button has the dot and "Relaunch to apply mod
     changes" beside it. Worth it only if the menu looks good behind it.

## The thumbnail: `image.png`

Square, 512 to 1024 px, a PNG under 1 MB, in `workshop/`. Jordan's own art. The
Workshop shows it small in its lists (about 200 px), so big shapes and few words read best. Not the main menu
button's old picture (once `images/main_menu_button.png`, gone from the repo and the mod since the button became
the game's own text button): that was an edited still of the game's own main-menu background.

## Sizes and converting

Steam takes pictures under 1 MB each. `dotnet run workshop/prepare.cs` checks all of them before an upload.

- **Previews:** `dotnet run workshop/shrink.cs` turns everything in `workshop/previews/` into JPGs at most
  1920 px wide and under 1 MB, in place (Windows only). JPGs that already fit stay as they are. Without it:
  open the picture in Paint, Resize to 1920 px wide (by pixels, keep the aspect ratio), then Save as JPEG.
- **`image.png`:** in Paint, select a square and Crop, Resize to 1024 x 1024, and Save as PNG. If it's still
  over 1 MB, resize to 768 x 768.
