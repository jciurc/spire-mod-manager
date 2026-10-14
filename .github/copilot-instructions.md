Small repo with one maintainer. Keep PR reviews light and one-pass.

Review code as written (text-level only) with general repo context.
Do not run builds, tests, or deep validation.

Only flag likely real issues:
- logic flaws or contradictions
- unclosed/incorrect branches or loop flow
- obvious missed edge cases
- clear code hygiene problems that could cause bugs

On pull requests from forks, also point out any change to `.github/`, `build.cs`, `tools/` or `workshop/*.cs`:
those run on the maintainer's PC or in CI.

Ignore nits, style preferences, optional refactors, and process advice.
If nothing notable: "Surface review complete — no obvious issues."
