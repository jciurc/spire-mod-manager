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
    otherwise (decompiled `SteamPlatformUtilStrategy.OpenUrl`).

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
2. **Required Items from the query we already make, with the search page as the fallback.**
   - Add `SetReturnChildren(handle, true)` to the existing startup query and keep each mod's Required Items.
     That is the same single request, slightly larger.
   - A missing dependency of a Workshop mod resolves when that mod has exactly one missing dependency and
     exactly one Required Item that isn't installed. Its name then links straight to that item's page.
   - Otherwise it falls back to option 1, or to plain text (question 1).
   - Pros: no extra request; exact whenever the author listed it (both samples did); known before the
     click, so the link can say where it goes.
   - Cons:
     - It depends on authors listing Required Items.
     - A local mod has none.
     - Several missing dependencies on one mod need titles to pair them up: one more query, on click, and only
       for those items.
   - Effort: small to medium (about 80 lines, 2 to 3 keys, 3 to 4 checks).
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

- **Option 2, with the search page (option 1) as the fallback.** It answers the common case exactly at no
  extra cost, and it never guesses.
- Leave option 3 for later, and only if local mods with missing dependencies turn out to be common.
- Drop option 4.

## The UI

- **Info panel, Dependencies line** (`ModDetails.cs:172-181`), first:
  - A missing dependency reads "BaseLib (not installed)": the name in our link blue (`#99ccff`, as Patch
    notes), "(not installed)" in the problem red.
  - **Known Workshop item:** a click opens its page. Hover card: "Not installed. Click to open its Workshop
    page."
  - **Unknown:** the name and "(not installed)" as plain text, as the backlog says. A quiet "Search the
    Workshop" link may follow (question 1).
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
  - The relaunch spike notes a likely bug in tracking new mods (`Installed.Detected`). It affects Changes and
    the pending dot, not this lookup.
- **Offline, or no Steam:** no Required Items arrive, so every missing dependency counts as unknown. The
  search link still opens the browser, which may fail on its own if there's no connection. We don't try to
  detect that. In the dev sandbox (`--force-steam off`) checks seed Required Items through
  `WorkshopInfo.TestSet`, as the Workshop details tests already do.

## Privacy and limits

- **The first PR adds no request.** It only asks the one existing startup query to include Required Items.
- **Any later query** (titles to pair several missing dependencies, or option 3):
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
  - `Details` gains `Required` (Workshop ids), read with `GetQueryUGCChildren` for each result that has
    `m_unNumChildren > 0`.
  - A pure `DependencyPage(Mod needer, string id)` returns the item's page URL, or null when it can't place
    it.
- **`ModDetails.cs`:**
  - Missing dependencies in the Dependencies line become `dep:<id>` links (or plain text, per question 1).
  - The click goes in the existing `MetaClicked` handler (`ModDetails.cs:129-135`).
  - The hover card goes through `Hover.WatchLinks`, as Workshop tags do (`ModDetails.cs:136-137`).
- **The search URL:** built with `Uri.EscapeDataString(id)`.
- **Strings:** `DEP_WORKSHOP_TIP` ("Not installed. Click to open its Workshop page.") and, if wanted,
  `DEP_SEARCH` ("Search the Workshop") with `DEP_SEARCH_TIP`.
- **Self-test checks** (deterministic, with made-up Workshop ids through `WorkshopInfo.TestSet`):
  - "a missing dependency listed as a Required Item links to that item's Workshop page";
  - "two missing dependencies and two unknown Required Items aren't guessed";
  - "a missing dependency with no Required Items offers the Workshop search for its id";
  - "the Workshop search link escapes the mod id";
  - "an installed dependency still scrolls to its row".
- **Real play only:** a Workshop mod whose Required Item is unsubscribed shows the link, and it opens the
  right page. Add this to the private-upload test (RELEASE-PLAN §2 item 12).
