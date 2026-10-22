# API versions

Spire Mod Manager's API (`SpireModManager.Api`, [docs/dev/API.md](../../../docs/dev/API.md)): which release first
carried each version. A mod that uses a member asks for at least that release (`min_version`), and can read
`ApiInfo.Version` at run time. The last row reads "unreleased" while `src/SpireModManager/PublicAPI.Unshipped.txt`
holds members no release has carried yet.

| API version | First release |
| --- | --- |
| 1 | 0.2.0 |
