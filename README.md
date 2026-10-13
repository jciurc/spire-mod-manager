# Pull request screenshots

Pictures for pull requests, and nothing else. `tools/shot.cs` (on `dev`) shrinks each one to a JPG, commits
it here in a folder named after the branch it belongs to, and prints the Markdown that shows it in a pull
request. This branch shares no history with the others, has no rules, and is never merged.

Fixture runs only (`dotnet run tools/dev.cs test --window`). A `--real` shot shows your own mods and data, and
this repository is public, so `tools/shot.cs` refuses `real-*` pictures.
