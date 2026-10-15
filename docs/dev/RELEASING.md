# Releasing

How a version goes from `dev` to a tagged build ready for the Workshop. The upload itself, updates and rollback
are in [workshop/README.md](../../workshop/README.md).

1. **Set the version first:** `ReleaseVersion` in `build.cs`, in its own pull request into `dev`. Release builds
   don't bump it on their own, and setting it after testing would change the tree that was tested (step 4).
2. **Test that commit** with the full in-game suite: `dotnet run tools/dev.cs test` plain, `--bmm`,
   `--bmm-after`, `--first-run`, `--first-run-bmm --bmm`, and `--real`. The Workshop build has no self-test,
   so the suite runs on that commit's dev build.
3. **Merge into `main`:** open a pull request from `dev` into `main` and merge it (merge commit) once the test
   passes. `main`'s ruleset refuses direct pushes, even a fast-forward.
4. **Check the tree:** `git rev-parse origin/main^{tree}` must equal the tested commit's tree.
5. **Tag** `main`'s merge commit: `git tag -a vX.Y.Z`, then push the tag.
6. **Build** in a worktree checked out at the tag: `dotnet run build.cs --dist`, then
   `dotnet run tools/dev.cs smoke`. Zip `dist/SpireModManager/` into the main checkout's `dist/` (gitignored,
   e.g. `dist/SpireModManager-X.Y.Z.zip`), outside the temporary worktree, and keep it for rollback.
7. **By hand, once:** install those dist files into the real game and open the Modding page. The smoke run stops
   at the main menu, because opening the page needs the self-test.
8. **Changelog:** add the version's section to [CHANGELOG.md](../../CHANGELOG.md), for players, from the merged
   pull requests. Its first line, after the version, is `workshop.json`'s `changeNote`.
9. **Upload:** [workshop/README.md](../../workshop/README.md), "Each upload".
10. **After the upload:** local builds must be versioned above it, as the next version plus `-dev.N` (after
    0.1.0: `0.1.1-dev.1`), or the game loads the Workshop copy instead. Whether the game's version check
    accepts a `-dev` suffix isn't verified yet: check it before the first such install.

## Before each upload

- `minBranch` is set. Empty means every branch, and the newest upload a player's branch allows is the one
  they get.
- A game update can rename the members the patches reach. Run the self-test on the current game version.
- Known limits players should hear about stay in the description: managers that rewrite `mod_list` (#138),
  data that doesn't follow players between PCs (#146), relaunch tested on Windows only (#140).
