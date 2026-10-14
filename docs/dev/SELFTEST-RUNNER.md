# The self-test on GitHub (maintainer)

`.github/workflows/selftest.yml` can run the in-game self-test from GitHub on a PC with the game installed: it
builds with `--no-install`, runs `tools/dev.cs test`, then `--bmm`, and attaches each run's
`out/selftest/*.txt` and `out/dev.log`. Never `--real`: that output holds your own mods and data. It starts
only by hand (Actions > Self-test > Run workflow), never from a push or a pull request.

**Security first:** on a public repository, any fork's pull request can reach a registered self-hosted runner,
because the fork can change the workflows in its own copy. Personal repositories have no runner groups to
fence one off. So keep no runner registered while the repository is public, and run
`dotnet run tools/dev.cs test` locally instead. Register one only for a run you start yourself, and remove it
afterwards (Settings > Actions > Runners).

To let your PC take a run:

1. Settings > Actions > Runners > New self-hosted runner, Windows x64. Follow its download and `config.cmd`
   steps; when it asks for labels, add `sts2`. (It gets `self-hosted`, `Windows` and `X64` on its own, and
   labels ignore case, so `windows` matches.)
2. When it asks whether to run as a service, say no. Start it with `run.cmd` from a terminal, as your normal
   user, so the game can open its window and reach the game folder and your `%APPDATA%` (a service runs as
   another account, without a desktop). It takes jobs while that terminal is open.

One runner only: the test uses one sandbox (the dev copy and the dev save data), and a CI run and a dev run of
your own can't overlap either.
