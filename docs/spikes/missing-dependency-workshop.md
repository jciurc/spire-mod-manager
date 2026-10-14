# Spike: a missing dependency links to the Workshop

Status: proposal, 2026-10-13. No code changed.

## The problem

Jordan (backlog, round 12): "a dependency that isn't installed: click it to open its Workshop page (needs a
way from a mod id to its Workshop id; not every mod is on the Workshop, then plain text with
'(not installed)'); spike first".

A mod's manifest names what it needs by mod id ("BaseLib"). A Workshop page is found by its Workshop id
(3737335127). Nothing installed connects the two when the dependency isn't installed.

## What happens today

### Where a missing dependency shows

- **Row line:** "Needs: BaseLib (not installed)", in red (`RowStrip.cs:181-207`). Not clickable: the line
  lets clicks through to the row (`RowStrip.cs:69`, `RowStrip.cs:91`).
- **Info panel, problems:** "Needs BaseLib, which isn't installed", plain red text (`ModDetails.cs:264-278`).
- **Info panel, Dependencies line:** every dependency is a `mod:<id>` link (`ModDetails.cs:172-181`). For a
  missing one:
  - a click does nothing, because `CategoryList.Reveal` finds no row (`CategoryList.cs:138-143`);
  - hovering shows no card, because `Dependencies.Find` returns null (`CategoryList.cs:185-187`).
- **Main list banner:** "1 enabled mod needs a mod that isn't installed." Its Show button steps to the
  mod (`LoadOrder.cs:250-291`).
- **Relaunch check:** lists the same problem (`RelaunchCheck.cs:29-35`).

### What we already ask Steam

- **`WorkshopInfo`:** one batched `CreateQueryUGCDetailsRequest` per session for every installed Workshop
  mod (`WorkshopInfo.cs:52-86`), started from the main menu (`Patches.cs:35`).
  - It goes through the game's Steam wrapper, the same call the game makes at startup for branch support.
  - It keeps dates, tags, branches, size and subscribers.
  - It doesn't ask for "children" (Required Items).
  - Without Steam (the dev sandbox, offline) it does nothing.
- **Links today:**
  - The source line opens `filedetails/?id=<id>` in the browser (`ModDetails.cs:74-79`).
  - Patch notes uses `OS.ShellOpen` (`ModDetails.cs:425-426`).
  - The game's Get Mods button uses `PlatformUtil.OpenUrl`: the Steam overlay when it's enabled, the browser
    otherwise (the game's `SteamPlatformUtilStrategy.OpenUrl`).

## What can map a mod id to a Workshop item

- **The manifest: no.** A dependency is `{ id, min_version }` and nothing else (the game's `ModDependency`).
- **Key-value tags or metadata: no.** Mega Crit's ModUploader v0.2.0 (`src/UploadCommand.cs` in
  megacrit/sts2-mod-uploader) sets:
  - title, description, visibility, content descriptors, tags, branch range, content and previews;
  - **Required Items**, from `workshop.json` `"dependencies"`, given as Workshop ids.

  It never calls `AddItemKeyValueTag` or `SetItemMetadata`. So no item carries its manifest id in a field we
  can query, and the manifest inside the uploaded content isn't searchable.
- **Required Items: yes, often.** Steam calls them "children".
  - We can read them with `SetReturnChildren` and `GetQueryUGCChildren`, both in the game's
    Steamworks.NET.
  - Two items checked on 2026-10-13:
    - "The Flaw | Infested Automaton" (3816658424) requires RitsuLib (3747602295);
    - "Ascendants" (3815639093) requires BaseLib (3737335127).
  - Steam asks subscribers to subscribe to required items too. So a dependency is usually missing because:
    - the author didn't list it;
    - it was unsubscribed later;
    - the mod that needs it is a local copy;
    - or it isn't on the Workshop at all.
- **Text search: yes, but loose.**
  - `CreateQueryAllUGCRequest(k_EUGCQuery_RankedByTextSearch, ...)` with `SetSearchText` matches titles and
    descriptions.
  - The website search for "BaseLib" found 1,446 items, with the exact title "BaseLib" (3737335127) first.
  - Libraries' titles often equal their id; other mods' titles often don't.
- **The search page in a browser: always available.**
  `https://steamcommunity.com/workshop/browse/?appid=2868840&searchtext=<id>` works (checked with "BaseLib").

## Options

1. **Search page only.** A missing dependency's name opens the Workshop search for its id.
   - Pros: no Steam requests, nothing to match, never a wrong page. An empty result is itself the answer:
     "not on the Workshop".
   - Cons: always one more step (the player picks the item). It can't tell "on the Workshop" from "not", so
     the backlog's "plain text when not on the Workshop" isn't possible.
   - Effort: small (about 30 lines, 2 keys, 2 checks).
2. **Required Items, matched by title, with the search page as the fallback.**
   - Add `SetReturnChildren(handle, true)` to the existing startup query and keep each mod's Required Items.
     That is the same single request, slightly larger.
   - A Required Item is a Workshop id, and a dependency is a mod id. A count (one missing dependency, one
     Required Item not installed) doesn't tie them: a stale or unrelated Required Item would send the player
     to the wrong page. No installed copy can tie them either, since a missing dependency has none.
   - The item's title can. On the first click, one request asks for the titles of that mod's Required Items
     that aren't installed. An item whose title equals the dependency's id (ignoring case, spaces and
     punctuation) opens its page. BaseLib's title does.
   - Otherwise it falls back to option 1, or to plain text (question 1).
   - Pros: a direct page only when the author's list and the item's title agree; nothing extra at startup.
   - Cons:
     - It depends on authors listing Required Items, and on titles that equal the id. "BaseLib (StS2)"
       would get the search page.
     - A local mod has none.
     - A mod installed after the startup query has none until the next launch (see the first PR).
     - One request on the first click for each mod, and the click waits for it.
   - Effort: small to medium (about 100 lines, 2 to 3 keys, 4 to 5 checks).
3. **A Steam text search on click.** Search the Workshop for the id, take an exact (normalized) title match,
   and fall back to option 1.
   - Pros: works for local mods too, and gives a direct page when the title is the id.
   - Cons:
     - One request per click.
     - Fuzzy: titles like "BaseLib (StS2)" or translated titles miss.
     - A wrong match sends the player to the wrong mod, which is worse than a search page.
   - Effort: medium (about 100 lines plus a cooldown).
4. **A built-in table of well-known libraries** (id to Workshop id).
   - Pros: instant, works offline.
   - Cons: needs upkeep, goes stale, picks favourites, and is ours to get wrong.
   - Not recommended.

## Recommendation

- **Option 2, with the search page (option 1) as the fallback.** It gives a direct page when the author's
  Required Items and the item's title agree, and the search page otherwise. It never links on a count alone.
- Leave option 3 for later, and only if local mods with missing dependencies turn out to be common.
- Drop option 4.

## The UI

- **Info panel, Dependencies line** (`ModDetails.cs:172-181`), first:
  - A missing dependency reads "BaseLib (not installed)": the name in our link blue (`#99ccff`, as Patch
    notes), "(not installed)" in the problem red.
  - **Its mod has Required Items that aren't installed:** a click checks their titles (once per mod), then
    opens the matching item's page, or the Workshop search when none matches. Hover card: "Not installed.
    Click to find it on the Workshop."
  - **No Required Items to check:** the name and "(not installed)" as plain text, as the backlog says. A quiet
    "Search the Workshop" link may follow (question 1).
- **Problems line:** stays plain text. It's the summary, and the link is one line below.
- **Row line and relaunch check:** not in the first PR. The row line passes clicks to the row (selecting the
  mod), and its text is small. The banner's Show already brings you to the mod, and the panel has the link.
- **Opening the page:**
  - `PlatformUtil.OpenUrl` (the Steam overlay, as Get Mods does): the player can subscribe without leaving
    the game.
  - Or `OS.ShellOpen` (the browser), as our source line and Patch notes do today.
  - Question 2.
- **After subscribing:**
  - Steam installs it and the game announces it, and our "New Mod Added" popup shows.
  - The dependency is then found (`Dependencies.Find` looks at `ModManager.Mods`, where the game puts new
    mods), so the panel's problem goes and the mod shows as pending a relaunch.
  - The relaunch spike notes a bug in tracking new mods (`Installed.Detected`, fix proposed in PR #14). It
    affects Changes and the pending dot, not this lookup.
- **Offline, or no Steam:** no Required Items arrive, so no missing dependency has any to check. The
  search link still opens the browser, which may fail on its own if there's no connection. We don't try to
  detect that. In the dev sandbox (`--force-steam off`) checks seed Required Items and titles through test
  setters, as the Workshop details tests already do (`WorkshopInfo.TestSet`).

## Privacy and limits

- **The first PR adds no request at startup.** It only asks the one existing startup query to include
  Required Items.
- **The titles request** (and option 3, if it's ever built):
  - only on a click;
  - one request;
  - cached for the session;
  - `SetAllowCachedResponse` set;
  - nothing asked again for two minutes after a failure (the rule the old patch notes requests had).
- **No background lookups and no scraping of web pages.** Everything goes through the Steam client. The
  only thing sent is a Workshop id or the dependency's mod id.

## Open questions for Jordan

1. A dependency we can't place (no Required Item): plain "(not installed)" only, as the backlog says, or add
   a quiet "Search the Workshop" link? (Proposed: add it. It's the only help we can give a local mod.)
2. Open Workshop pages in the Steam overlay (subscribe without leaving the game) or in the browser (as the
   source link and Patch notes do now)? (Proposed: the overlay for this link. Move the other Workshop links
   in the copy pass, so they all agree.)
3. Link from the row's "Needs:" line and the relaunch check too, or the info panel only? (Proposed: the panel
   first.)
4. Later, a direct "Subscribe" button (`SteamUGC.SubscribeItem`) instead of opening the page? (Proposed: no.
   It acts on the player's Steam account without them seeing the item.)

## A first PR

Branch `dependency-workshop-link`:

- **`WorkshopInfo.cs`:**
  - `SetReturnChildren(handle, true)` on the existing query (`WorkshopInfo.cs:57`).
  - That query runs once per session, for the Workshop mods installed then (`WorkshopInfo.cs:34-39`,
    `WorkshopInfo.cs:52-57`). A mod installed later has no Required Items until the next launch, so its
    missing dependencies get the search link or plain text. No refresh in the first PR.
  - `Details` gains `Required` (Workshop ids), read with `GetQueryUGCChildren` for each result that has
    `m_unNumChildren > 0`.
  - The titles request for one mod's Required Items, with the limits in "Privacy and limits".
  - A pure `DependencyPage(string id, titles)` returns the page URL of the item whose title matches the id,
    or null.
- **`ModDetails.cs`:**
  - Missing dependencies in the Dependencies line become `dep:<id>` links (or plain text, per question 1).
  - The click goes in the existing `MetaClicked` handler (`ModDetails.cs:129-135`).
  - The hover card goes through `Hover.WatchLinks`, as Workshop tags do (`ModDetails.cs:136-137`).
- **The search URL:** built with `Uri.EscapeDataString(id)`.
- **Strings:** `DEP_WORKSHOP_TIP` ("Not installed. Click to find it on the Workshop.") and, if wanted,
  `DEP_SEARCH` ("Search the Workshop") with `DEP_SEARCH_TIP`.
- **Self-test checks** (deterministic, with made-up Workshop ids and titles through test setters):
  - "a missing dependency opens the Required Item whose title is its id";
  - "a Required Item with another title isn't linked, even when it's the only one: the Workshop search
    instead";
  - "a missing dependency with no Required Items offers the Workshop search for its id";
  - "the Workshop search link escapes the mod id";
  - "an installed dependency still scrolls to its row".
- **Real play only:** a Workshop mod whose Required Item is unsubscribed shows the link, and it opens the
  right page. Add this to the private-upload test (RELEASE-PLAN §2 item 12).
