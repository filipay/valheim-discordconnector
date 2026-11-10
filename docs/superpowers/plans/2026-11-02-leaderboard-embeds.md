# Leaderboard Embeds Implementation Plan

> **For agentic workers:** Use a designer implementation lane and focused independent review. Track steps below.

**Goal:** Match existing leaderboard embeds and expose bounded report data for personal debugging.
**Architecture:** Adapt the existing bounded literal report at the shared send boundary; add optional diagnostics at the snapshot/render boundary for automatic and manual reports. Preserve plain text and existing delivery semantics.
**Tech Stack:** Existing C#, DiscordEmbed/EmbedBuilder, BepInEx logging and executable Reporting harness.
**Spec:** `docs/superpowers/specs/2026-11-02-leaderboard-embeds-design.md`.

## Global Constraints
- No new dependencies, collection changes, delivery routes or retries; no commit/build/deployment without authorization.
- Preserve 1,900 UTF-16 report budget, all qualifications and disabled mentions on embed and fallback paths.
- Debug output may contain display names, never account/connection identifiers or webhook URLs; existing debug toggle gates it.

## Review Focus
- All seventeen populated categories fit Discord field/count/total limits.
- Hostile names cannot inject mentions, formatting or forged log lines.
- Embed disabled/construction failure retains the exact original plain text and mention protection.
- Manual and scheduled reports retain dates, partial-data caveats and consistent diagnostics.
- Empty snapshots log a bounded empty result without creating automatic Discord spam.

## File map
- `DiscordConnector/DiscordApi.cs`: existing `SendRenderedMessage(Webhook.Event ev, string message)` embed adapter and protected fallback.
- `DiscordConnector/Leaderboards/CombatReports.cs`: snapshot/render diagnostics with an optional injected logger, preserving pure rendering for callers without one.
- `DiscordConnector/Plugin.cs` and manual report caller: minimal diagnostic callback wiring if needed; no lifecycle redesign.
- `tests/CombatReporting/{Program,Stubs}.cs` and project links as necessary: actual payload and diagnostic assertions.

### Task 1: Matching embeds and bounded diagnostic data
- [ ] Add failing focused tests for the five Review Focus cases; test actual production payload construction, not a copied stub algorithm.
- [ ] Extend the existing transport with trophy title, configured color and non-inline category fields. Keep qualifiers/dates in the description and unmodified plain text available for fallback; validate Discord limits.
- [ ] Add a bounded injectable diagnostic callback to the shared snapshot renderer, wired to the existing leaderboard debug toggle and logger for both report modes. Emit available data and rendered result counts, including empty snapshots; sanitize CR/LF/control characters and cap output. Include the requested removal/anonymization TODO.
- [ ] Run `CombatReporting` Release using the working local SDK and approved explicit LiteDB override recorded in `.superpowers/sdd/2026-11-02-observation-pipeline-redesign/checks/pre-commit/results.json`; use a fresh ignored output directory and `DOTNET_GENERATE_ASPNET_CERTIFICATE=false`. Expected: all assertions pass, approved dependency hash matches.
- [ ] Inspect diff and obtain focused independent review. Parent runs any other harness affected by changed source links/callers; do not rerun unrelated suites by default. Record actual commands/results separately.

## Self-review
All design requirements map to Task 1. Existing public send signature stays unchanged; optional diagnostic injection preserves existing callers. Five explicit regression classes cover payload limits, hostile input, fallback, shared reporting and empty results. No new project or operational action is required.
