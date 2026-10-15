## Summary

<!-- What changed and why, in a few short bullets. Name the issue it closes: "Fixes #123". -->

## What to look for

<!-- What a player will notice in the game, in plain words. "Nothing visible" is fine. -->

## Screenshots

<!-- Encouraged, not required: the change in the game, or the nearest screen if it isn't visual. Drag images in here. Maintainer: `dotnet run tools/dev.cs test --window`, then `dotnet run tools/shot.cs out/selftest/<name>.png` (fixture runs only, never a real-* shot). -->

## For review

<!-- Choices made along the way that someone should agree with, and anything uncertain. Also what Claude's review (`/code-review`, run before opening this) found and what you did about each finding. -->

## Testing

<!-- `dotnet run tools/check.cs` for every pull request. For code changes, the pass and fail counts from out/selftest/results.txt (see CONTRIBUTING.md). The --real run is the maintainer's: its output holds their own mods and data. -->

| Run | Pass | Fail |
| --- | --- | --- |
| Fixture (`dotnet run tools/dev.cs test`) | | |
| `--bmm` (with Better Mod Menu installed) | | |
| `--real` (maintainer only) | | |

## Notes

<!-- Follow-ups, risks, anything else. -->
