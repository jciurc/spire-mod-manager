# Releasing

How a version goes from `dev` to a tagged build ready for the Workshop. The upload itself, updates and rollback
are in [workshop/README.md](../../workshop/README.md).

1. **Test `dev`'s commit** with the full in-game suite: `dotnet run tools/dev.cs test` plain, `--bmm`,
   `--bmm-after`, `--first-run`, `--first-run-bmm --bmm`, and `--real`. The Workshop build has no self-test,
   so the suite runs on that commit's dev build.
2. **Set the version:** `ReleaseVersion` in `build.cs`. Release builds don't bump it on their own.
3. **Merge into `main`:** open a pull request from `dev` into `main` and merge it (merge commit) once the test
   passes. `main`'s ruleset refuses direct pushes, even a fast-forward.
4. **Check the tree:** `git rev-parse origin/main^{tree}` must equal the tested commit's tree.
5. **Tag** `main`'s merge commit: `git tag -a vX.Y.Z`, then push the tag.
6. **Build** in a worktree checked out at the tag: `dotnet run build.cs --dist`, then
   `dotnet run tools/dev.cs smoke`. Zip `dist/SpireModManager/` and keep it with the tag, for rollback.
7. **Changelog:** add the version's section to [CHANGELOG.md](../../CHANGELOG.md), for players, from the merged
   pull requests. Its first line is the Workshop change note.
8. **Upload:** [workshop/README.md](../../workshop/README.md), "Each upload".
9. **After the upload:** local builds must be versioned above it (`0.1.1-dev.N`), or the game loads the
   Workshop copy instead.

## Before each upload

- `minBranch` is set. Empty means every branch, and the newest upload a player's branch allows is the one
  they get.
- A game update can rename the members the patches reach. Run the self-test on the current game version.
- Known limits players should hear about stay in the description: managers that rewrite `mod_list` (#138),
  data that doesn't follow players between PCs (#146), relaunch tested on Windows only (#140).
