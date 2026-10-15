# Publishing to the Steam Workshop

This folder is the workspace for Mega Crit's ModUploader (`github.com/megacrit/sts2-mod-uploader`, v0.2.0).
The game has no uploader of its own. The uploader's behaviour below is checked against its v0.2.0 source. The
steps before an upload (test, merge, tag, build) are in [docs/dev/RELEASING.md](../docs/dev/RELEASING.md).

- `workshop.json`: what each upload sets on the item.
- `description.bbcode`: the description. Edit it here: the uploader reads the description only from
  `workshop.json`, as one string, so `prepare.cs` copies it in.
- `image.png` (required) and `previews/` (optional): Jordan's pictures, see SHOTS.md. Not in git so far.
- `content/`: the files players install, filled from `dist/` by `prepare.cs`. Gitignored.
- `mod_id.txt`: the item id, written by the uploader after the first upload. Commit it: without it, the next
  upload makes a second item.
- `prepare.cs` stages and checks this folder; `shrink.cs` fits screenshots under 1 MB.

## What an upload changes

Every upload sets these from this folder, over anything changed on the item's web page. So change the listing
here, not on the web.

- Title, description, visibility and tags.
- The branch range (`minBranch`, `maxBranch`), always. Empty or missing means every branch, and the newest
  upload that covers a player's branch is the one they get.
- Required Items: exactly the `dependencies` list (missing counts as empty).
- `content/` and `image.png`.
- Previews, if `previews/` exists: the item ends up with exactly its files, matched by name (a renamed file is
  removed and added again). Without the folder, the previews stay as they are.
- `changeNote` becomes a new entry on the item's Change Notes page.

The uploader's own notes call the branch fields unreliable and suggest the web page. It still sets them on
every upload, so keep them right here and check the page afterwards.

## Once

1. Accept the Steam Workshop legal agreement (`steamcommunity.com/sharedfiles/workshoplegalagreement`) on the
   account that uploads. It can't be a limited account.
2. Download `ModUploader-win-x64.zip` from the v0.2.0 release and unzip it outside the repo.
3. Don't run `ModUploader.exe new` on this folder: it already is the workspace, and `new` stops at the first
   file that exists. `ModUploader.exe new -w <another folder>` gives a fresh template to compare with.

## Each upload

1. [docs/dev/RELEASING.md](../docs/dev/RELEASING.md), steps 1 to 7: the full self-test, the version set in
   `build.cs`, `main` merged and tagged, the changelog written.
2. `dotnet run build.cs --dist` (added on the release-prep branch): `dist/SpireModManager/` with the `.dll`,
   `.json` and `.pck`.
3. In `workshop.json`, set `changeNote` to the version's first line in CHANGELOG.md, word for word (`"0.1.0: ..."`).
4. Check `minBranch`, every time. It is `"public-beta"` until v0.111.0 or newer reaches the stable branch
   (docs/dev/DESIGN.md, "Releases"). An upload without it serves the build to stable players, where it can't load.
5. `dotnet run workshop/prepare.cs`. It fills `content/`, copies the description in, checks the folder and
   lists what the upload will set. Fix anything it calls a problem.
6. With Steam running, in the uploader's folder (it needs its `steam_appid.txt` there, and writes
   `mod-uploader.log` there), run the line `prepare.cs` printed. It has the full path of your checkout's
   `workshop` folder: `.\ModUploader.exe upload -w "<your checkout>\workshop"`
7. First upload only: commit the new `mod_id.txt`.
8. On the item's Change Notes page, check that the branch range reads public-beta and up. If it doesn't, fix
   it on the page and here.

## The first upload: private, then test

`prepare.cs` holds the first upload to `"visibility": "private"` (docs/dev/DESIGN.md, "Releases").

1. Make it a clean install: remove your local `mods\SpireModManager`, and move
   `%APPDATA%\SlayTheSpire2\steam\<your id>\mod_data\SpireModManager` aside.
2. Subscribe with your own account and launch through Steam.
3. Check: first run with no data, the Better Mod Menu import, our own item's Workshop details (the source line,
   the Patch notes link), Relaunch Game, then unsubscribe (the game's page intact, the on/off states and order
   kept, our data folder left behind).
4. Put your data folder back.

## Going public

The description sends bug reports to the GitHub repo's issues and says the source is there, so while the repo
is private that line leads to a 404. Before the item goes public, either make the repo public or take the GitHub
sentence out of `description.bbcode` and upload again. The private first upload
is fine either way: only you see it.

Switch the item to Public on its page, and set `"visibility": "public"` here too: the next upload would make it
private again otherwise. Unlisted is the fallback for a soft launch.

## Updates

Bump the version in `build.cs`, then steps 2 to 8 above. Zip `dist/SpireModManager/` for each tag and keep it.

Your own game: a local copy in `mods\` and the Workshop copy have the same id, so the game loads the higher
version and marks the other a duplicate (equal versions: the local one wins). Give local builds versions like
`0.1.1-dev.N`.

## Rollback

- A bad update: put the previous tag's zip back into `dist/SpireModManager/`, run `prepare.cs`, and upload it
  with a change note that says so.
- A game update breaks it: upload a fix, or narrow the branch range so the broken build reaches no one.
- Last resort: make it private. Never `ModUploader.exe remove`: that deletes the item, its id and its
  subscribers.
