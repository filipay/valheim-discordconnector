# Leaderboard embeds and temporary report diagnostics

Approved direction: user requested the existing leaderboard embed style and permitted display names in temporary diagnostic output, with a TODO to anonymize or remove it later.

- Match the existing trophy title, configured leaderboard color and full-width category fields. Keep dates, coverage and all current accuracy qualifications in the description. Reuse existing types and style; do not expand configured templates into literal report data.
- Preserve the existing 1,900 UTF-16-unit rendered report, ranking, scheduling, claims, manual authorization and collection. Both scheduled and manual reports use the same presentation.
- Respect `DiscordEmbedsEnabled`. Disabled embeds or construction failures use the original text, always with mentions disabled. Discord field/count/total limits must hold for all seventeen sections.
- Use `DebugLeaderboardOperations` for bounded report diagnostics: available categories, display names and numeric values, coverage, and emitted rows/fields. Include empty results. Escape control characters to prevent forged log lines. Never include account IDs, connection details, webhook URLs or raw database documents. Add a TODO to anonymize or remove these personal-use diagnostics after validation.
- Alternatives: description-only embeds retain today's cramped presentation; replacing the entire renderer/legacy template adds unnecessary changes and risks losing qualifications. Selected: adapt the existing bounded report and reuse the legacy style only.
- No commit, new DLL, deployment or live Discord send is authorized by this change. Logs contain display names when the debugging toggle is enabled.
