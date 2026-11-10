# Reporting review fixes

Status: approved November 3, 2026. The user selected automatic 90-day retention; implementation, a new local testing DLL and local regression checks are the goal. No commit, push, deployment, live Discord send or production-database operation is authorized.

## Outcome and approaches

Finish the actionable review items without rebuilding the observation pipeline. Retain the existing authentication, quotas, reported-versus-confirmed distinctions, visual style, manual authorization and scheduler claims.

- **Targeted fixes (recommended):** a structured report boundary, bounded diagnostics and retention on the existing worker, adapter CI and corrected durable documentation. This addresses the findings without changing collection semantics.
- **Another broad rewrite:** would replace already-tested routing, storage and delivery together, increasing risk without improving the requested presentation or retention policy. Not proposed.
- **Documentation only:** cannot address the brittle embed conversion, missing CI coverage or requested retention. Rejected.

## Structured reports and delivery

One immutable internal `LeaderboardReport` contains `Title`, `Description`, ordered `(Heading, Value)` sections and `PlainText`. Construct these together from the same ranked, escaped, globally budgeted rows; never reconstruct fields by parsing Markdown. `CombatReports.BuildReport(...)` returns this object; automatic and manual query/send paths carry it to `DiscordApi.SendRenderedMessage(Event, LeaderboardReport)`. `Render(...)` may remain as `BuildReport(...)?.PlainText` for compatibility, not as an intermediate production transport format. Adapt historical string callbacks from the object, never the reverse.

Keep the trophy title, configured leaderboard color, full-width fields, dates/timezone and all reported-signal/damage/cooking/loss qualifications. Preserve the existing 1,900 UTF-16-unit literal budget and first-row-per-nonempty-category rule. Respect Discord's 25-field, 256-title/name, 1,024-field-value, 4,096-description and 6,000-total limits. Disabled embeds or failed embed construction send the exact original literal with mentions disabled. Do not retry an uncertain HTTP send, add a route or alter report claims.

Embed-construction fallback emits a fixed, sanitized warning at most once per 60 monotonic seconds per process. Do not log exception text, report contents, names, IDs or webhook addresses in this warning. A logger failure cannot break fallback. Rate limiting is bounded scalar state, not a per-player cache.

## Diagnostics and native header probe

Retain the existing default-off debug toggle and bounded named report-data diagnostics. The user explicitly permits display names and values for personal debugging; keep control-character sanitization, line/row limits and exclusion of account IDs, sessions, payloads and webhook credentials. Record a durable audit-document TODO to remove or anonymize named logs before wider distribution, alongside the code TODO; turning off the toggle stops these logs.

The public header peek already saves/restores the borrowed cursor in `finally` and filters before full copying. Add only a bounded, throttled short/malformed-header probe: a fixed reason and bounded byte count, never raw bytes or identity. Keep early size checks, exact four `long` + `uint` + `int` layout, disabled-path behavior, nested invalid-origin masking, cooking provenance and finalizer behavior. Do not introduce private-buffer reflection. Explicitly initialize intentionally default-valued native test-double fields to eliminate their CS0649 warning without changing their behavior.

## Automatic 90-day signal-detail retention

**User decision:** automatically expire eligible detail strictly older than 90 UTC days. Equality at the cutoff is retained. This supersedes earlier preserve-all-history policy only for:

- `reported_kill_credits_v1`
- `observed_consumable_uses_v1`
- `reported_dodge_hit_notifications_v1`

No automatic deletion of confirmed combat, legacy last-hit/events, activity provenance, exploration membership, cooking generations, coverage/loss records, report claims, database files or WAL files. No compaction or database replacement. Storage may reuse freed pages rather than immediately shrink the file.

**Engineering proposal:** retain a minimal durable tombstone for each expired signal's existing immutable dedup key, with no player name/account or raw token. Atomic tombstone insertion plus detail deletion prevents recount after changed-timestamp replay or restart. On insertion, check live detail and tombstones before applying the expired-timestamp branch; an existing live key must not also acquire a tombstone through a backdated replay. A persisted monotonic retention horizon prevents later backdated writes from recreating expired detail: accept their dedup identity only, not their expired detail. A backward wall clock must not lower the persisted horizon. Tombstones are not automatically expired; this bounds detailed history, not total database growth or all event collections.

Maintenance uses the existing serialized store worker, never a Unity-thread database operation or new background queue/timer. Admit at most one maintenance job per 60 seconds, at most 256 detail records total across the three collections per job. The existing queue limits and reserved diagnostic capacity remain intact; a full queue skips maintenance until a later tick, not a tight retry loop. One maintenance transaction commits the horizon advance, tombstone inserts and detail deletions together; publish a cached horizon only after commit. Failure rolls back that maintenance batch, not unrelated gameplay writes, whose existing partial-batch semantics remain unchanged. Clean shutdown seals and drains admitted maintenance using existing ownership rules. No unbounded startup sweep or shutdown purge. Queue saturation or a large backlog can delay physical cleanup beyond 90 days; the persisted horizon still fences late writes.

With a reasonably correct UTC clock, 90 days cannot intersect current/previous supported local day/week periods, including DST transitions and the existing grace interval. A falsely advanced clock can irreversibly advance the horizon and delete newer detail; correcting the clock backward cannot restore it. Persist and expose the actual retention horizon; historical-range queries must clearly indicate expired signal detail rather than imply complete lifetime history. Inject the clock/cutoff for disposable-database fixtures; do not rewrite timestamp fixtures around today's date.

## CI, portability and review hygiene

Add the real-production-linked `NativeAdapters` harness to `.github/workflows/combat-regressions.yml`, retaining all four existing Release harnesses and .NET 8. Check that the new structured-report dependency links compile in both reporting and adapter harnesses. A local run is not proof that GitHub Actions ran.

Replace executable instructions tied to `/Users/filip` or ignored SDK workspaces with caller-supplied SDK/workspace paths and prerequisites. Keep exact historical evidence as historical, not a clean-checkout prerequisite. Document the opt-in LiteDB backport and testing-only status honestly; do not imply an ordinary build includes it or that Linux Mono is verified.

Document why logger changes and their support templates belong together and why the Splatform reference is needed. Provide a short review map for the existing large commit and subsequent uncommitted fixes; do not rewrite published/local history or split commits without explicit authorization. Earlier designs retain their historical decisions with a supersession link to the new retention policy.

## Validation and delivery

- Reporting: literal/embed agreement from the structured object, hostile names, limits for all 17 categories, exact fallback and no mentions, automatic/manual empty behavior, rate-limited safe warnings and unchanged claims/calendar semantics.
- Adapter: truncated headers, partial read failures, cursor restoration, bounded diagnostic cadence, disabled fast paths, unchanged cooking/nested/manual provenance and no unintended test-double behavior change.
- Storage: 90-day equality, transaction failure/restart, changed-timestamp replay after pruning, backdated insertion before/after a queued prune, monotonic horizon, bounded admission/drain, all three signal kinds, historical-range qualification and untouched non-signal collections. Include current/previous period DST boundaries.
- CI/docs: five harness entries, portable commands, code/document privacy TODO and explicit finding dispositions. Existing per-account/connection limits already exist; do not replace them because the review called them absent. Client-reported dodge notifications remain forgeable and partial, not successful unique dodges.
- After scoped implementation reviews, run the five harnesses once against reconciled source, then one independent cumulative review. Build and verify a new isolated testing DLL using the established safe recipe and publish only the local four-file bundle with checksum, license and provenance.

Live PS5/Valheim/Harmony/Discord behavior, attacker-controlled multiplicity, performance under real traffic and process-exit durability remain separately authorized operational checks—not acceptance claims from local stubs or metadata.
