# The API

`SpireModManager.Api` is the public part of `SpireModManager.dll`. Our other mods call it instead of keeping copies
of SMM's tools, so they share one look and behaviour, a fix in SMM fixes them all, and only one menu is open at a
time across every mod. Everything else in the assembly is `internal`.

The surface only grows: a mod built against an older SMM keeps working against every newer one. Each member's doc
comment is its contract; the exact signatures are recorded in `src/SpireModManager/PublicAPI.Shipped.txt` (released)
and `PublicAPI.Unshipped.txt` (not yet released).

## What's in it (API version 1)

| Class | What it does |
| --- | --- |
| `ApiInfo` | `Version`, the API version: [VERSIONS.md](../../src/SpireModManager/Api/VERSIONS.md) names the first release with each. |
| `Palette` | SMM's colours: Cream, Gold, Navy, DeepNavy, Warning, Problem. |
| `Fonts` | Kreon regular and bold, or the language's font; `Style` sets a control's font, size and colour. |
| `GameResources` | `Load<T>`: a game resource, or null (logged once) if it isn't there. |
| `Mods` | `Version` of an installed mod; `Details`, what the player keeps about it (alias, notes, tags, category); `TestDetails`, a stand-in for those in a self-test (never saved). |
| `PopupMenus` | SMM's right-click menu (`MenuEntry`, with an optional icon; `MenuOptions`): one open at a time across mods, Escape closes it, the screen's hotkeys wait, and one taller than the screen goes into columns. `Rows`, `Headings` and `Press` are for self-tests. |
| `Banners` | SMM's banner strip (`BannerOptions`, `BannerKind`), with an optional Dismiss button at its end, for the caller to place: its text wraps at the width it's given. |
| `ModCards` | SMM's card for a mod, shown on hover over a control (`Watch`) or a link (`WatchLinks`); `Text`, `Show` and `Close` are for self-tests (`Show` opens a watched card without the mouse). |

## Rules for changing it

**Allowed after a member ships:**
- a new static class, static method or overload (if no existing call becomes ambiguous when its caller recompiles);
- a new `init` property on an option class;
- a new enum member with a new explicit value;
- `[Obsolete("use X")]` on a member that keeps working;
- visual tuning that keeps each element's role.

**Never:**
- renaming or removing a type or member, or changing a signature in any way (types, nullability, `ref`/`out`,
  constraints, static vs instance);
- optional parameters or changed defaults (both are copied into the caller): add an overload instead;
- renumbering or reusing an enum value; turning a field into a property, `static readonly` into `const`, or `init`
  into `set`;
- unsealing an option class, or removing its parameterless constructor;
- changing what a member does for a caller that didn't ask for it;
- changing the namespace, the assembly name or `AssemblyVersion` (pinned at `1.0.0.0`: dependents bind to it).

**Shapes callers get:** static classes, sealed option classes with `init` properties, enums with explicit values,
`Action` and `Func<>`, value tuples, Godot types. **Never:** records, structs, interfaces, base classes, events,
`const`, `required`, game types (they differ between the game's branches) or Harmony types.

**Node names and tree shapes aren't API.** The only names that count are the ones a caller passes in (`Name`).
Callers' self-tests use the test hooks (`PopupMenus.Rows` and the like) instead.

## How it behaves

- **No member throws.** Each body runs through `ApiGuard`: on a failure it logs once per member, naming the calling
  mod, and returns a stand-in (null, false, an empty list, Godot's fallback font, a plain panel with the banner's
  text, or nothing). Callbacks a caller hands in (`OnSelect`, `OnDismiss`, a `WatchLinks` mapping) are guarded the
  same way, logged once per member and mod, naming the mod whose code the callback is.
- **Main thread only**, apart from `ApiInfo.Version` and `Palette`, which are safe from any thread. From another
  thread a member logs once and returns its stand-in (`Mods.Version` gives null).
- **When:** `ApiInfo`, `Palette`, `GameResources` and `Mods.Version` work from a mod's `Init`. `Fonts` there gives
  Kreon (there's no language yet), so ask again each time you build, never keep a font across a language change.
  Menus, banners and cards work from the main menu on.
- **Works when SMM's own start failed.** Nothing in the API reads what `ModEntry.Init` sets, or needs SMM's patches.
- **SMM owns the shared state.** Opening a menu closes the open one, whichever mod's it is. Every connection SMM
  makes on a caller's node ends when either side goes. A banner belongs to the caller once added; a menu belongs to
  SMM, and a caller may free it.

## Using it from another mod

**A mod that needs SMM** (Content Options) declares the dependency and builds against SMM's reference assembly:
- `build.cs` writes `"dependencies": [{ "id": "SpireModManager", "min_version": "<VERSION>" }]` into the manifest
  (spelled `min_version`: the game ignores `minVersion`), the version read from `lib/SpireModManager/VERSION`.
- The repo commits `lib/SpireModManager/` from SMM's `dist/api/`: `SpireModManager.dll` (the reference assembly),
  `SpireModManager.xml` (the doc comments) and `VERSION`. The csproj references it with `Private="false"`, so the
  installed SMM is the one that runs.
- Raising the floor is copying a newer `dist/api/` in, a reviewed diff. To try an addition before it's released, copy
  in a dev build's `out/api/` instead (every `build.cs` leaves one): the same three files, its `VERSION` a dev
  version such as `0.1.5-dev.3`, and `runtime/`, that build, for the dependent's dev runs to load in place of the
  Workshop's copy (it's about 1 MB: keep it out of git). Tested staging builds' drops are kept outside the repo, in
  the maintainer's `session-tools/smm-api/<version>/`. A dependent's release build refuses a `VERSION` with a
  prerelease part, so a release always names a released SMM.
- The game loads SMM first, and refuses the dependent with its own error if SMM is missing, off or older.

**A mod that only uses SMM when it's there** (Mod Doctor) keeps `dependencies: []` and binds softly, once, after every
mod has loaded (never in its own `Init`): it finds SMM's assembly among the loaded mods, reads `ApiInfo.Version` with
`GetField`, and makes delegates of the members it wants with `GetMethod(name, flags, null, exactTypes, null)` and
`CreateDelegate`. A null means "not there, use our own". It stores no SMM type, and wraps each call in its own try.
SMM's self-test makes the same lookups.

## What guards it

- **Build:** PublicApiAnalyzers fail the build when a public signature is added without being recorded in
  `PublicAPI.Unshipped.txt`, or a recorded one changes or disappears; a public member without a doc comment fails too
  (CS1591). `dotnet format analyzers src/SpireModManager/SpireModManager.csproj --diagnostics RS0016 --severity error`
  records new members.
- **`tools/check.cs`** (CI, no build): only `Api/` is public; its files keep the shapes above; no member is ever
  removed (no `*REMOVED*` line), and the shipped surface at the latest release tag is all still there;
  `ApiInfo.Version` matches VERSIONS.md; `AssemblyVersion` stays `1.0.0.0`; every member is named in the self-test's
  code.
- **A release** has the API shipped: on a pull request into `main`, `check.cs` fails while
  `PublicAPI.Unshipped.txt` has lines or VERSIONS.md's last row reads "unreleased", and the release build
  (`build.cs --dist`) refuses the same (`--unshipped-api` builds a trial, never to upload).
- **The self-test** (`SelfTest.Api.cs`): the assembly as another mod sees it (only the API exported, the version,
  sealed options, enum values, Mod Doctor's lookups), then each member doing what its doc comment says.

## Adding to it

1. Add the member, with a doc comment, in `src/SpireModManager/Api/`; record it in `PublicAPI.Unshipped.txt` (the
   command above).
2. The first addition after a release raises the API version: `ApiInfo.Version` goes up by one, and VERSIONS.md gets
   a row for it reading "unreleased".
3. Test it in `SelfTest.Api.cs`, naming the member.

Releasing it is in [RELEASING.md](RELEASING.md), step 1 and step 9.
