# Reporting Review Fixes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Finish the validated reporting review fixes, automatically retain 90 days of reported-signal detail without reopening durable duplicates, and deliver a verified local testing DLL.

**Architecture:** Carry one immutable report from ranked rendering through text/embed delivery. Add bounded diagnostics and transactional signal-detail expiry to the existing serialized database worker; preserve collection, authorization and delivery semantics. Changes extend the current uncommitted embed/header fixes rather than replacing them.

**Tech Stack:** Existing C# net481 plugin, net8 executable harnesses, LiteDB 5.0.21, Harmony, Discord webhook models and GitHub Actions; no new dependencies.

**Spec:** `docs/superpowers/specs/2026-11-02-reporting-review-fixes-design.md` (approved November 3).

## Global Constraints

- Preserve all pre-existing modified/untracked files. No commits, branch changes, pushes, deployment, live Discord calls, production database access or database/WAL deletion.
- Keep all 17 categories, first compact row per nonempty category, exact escaped text and the 1,900 UTF-16-unit literal ceiling. Discord ceilings: 25 fields, 256 title/name, 1,024 field value, 4,096 description, 6,000 total.
- Keep manual authorization/cooldown/grace, automatic calendar/claims and at-most-once delivery; no HTTP retry or new route.
- Named debug data remains default-off and bounded under the existing toggle; never log account/session identifiers, raw payloads or webhook credentials. Preserve code and document removal/anonymization TODOs.
- Each fixed-reason warning is limited to once per 60 monotonic seconds per process using scalar state; logging failure cannot affect gameplay or fallback.
- Automatic retention is 90 UTC days, strict older-than, only the three signal-detail collections specified in the design. Permanent minimal hashed-key tombstones preserve deduplication. All other history remains untouched.
- Maintenance commits horizon/tombstones/deletions atomically: at most 256 detail rows total/job, at most one admitted job/60 seconds, at most one pending maintenance job, existing worker and queue bounds, no startup sweep/shutdown purge/new timer.
- Persist a nondecreasing horizon; publish its cached value only after commit. Never retry gameplay writes. Clock error can irreversibly expire detail; tombstones still grow and physical cleanup can lag.
- Test only disposable databases. Stub/metadata checks are not live Harmony, PS5, Discord, throughput or process-exit durability evidence.

## Review Focus

1. A duplicate with a changed/backdated timestamp must not recreate expired detail or tombstone an existing live row (Task 2).
2. A transaction failing after tombstone insertion must leave detail and persisted/cached horizon unchanged, including after restart (Task 2).
3. Queue saturation, backward clocks and shutdown must not bypass maintenance bounds, diagnostic reservation or drain ownership (Task 3).
4. Hostile Markdown-like names and all 17 sections must produce matching structured/text/embed content without parsing or enabling mentions (Task 1).
5. Partial native header reads and a throwing logger must preserve the borrowed cursor, nested masking and subsequent valid observations (Task 4).

## File map and execution order

Paths below are repository-relative. Follow existing files' style; no generic framework or unrelated formatting.

| Task | Files / responsibility |
|---|---|
| 1 — designer owns presentation | Create `DiscordConnector/Leaderboards/LeaderboardReport.cs`; modify `Leaderboards/{CombatReports,ManualLeaderboardReports}.cs`, `DiscordApi.cs`, `Plugin.cs`; Reporting tests/source links and NativeAdapters collaborator/source-link updates. |
| 2 — signal storage | Modify `Records/ReportedSignalStore.cs`; add focused `tests/CombatStatistics/SignalRetentionTests.cs` and its project/runner links. Keep retention state/tombstone helpers inside the signal store unless cohesive separation is actually needed. |
| 3 — worker/query integration | Modify `Records/{ActivityStore,ActivityModels,DatabaseDiagnostics}.cs`, `Plugin.cs`, minimal `CombatReports.cs` horizon qualification; Statistics/Reporting/NativeAdapters fixtures and links. |
| 4 — native diagnostics | Modify `Patches/ActivityPatches.cs`, `Leaderboards/ManualReportDiagnostics.cs` only if its bounded helper is reused, and `tests/NativeAdapters/{Program,NativeStubs}.cs`. |
| 5 — CI/docs | Modify `.github/workflows/combat-regressions.yml`, `docs/server-statistics-audit.md`, `docs/research/reports/Valheim modding patterns.md`, relevant `docs/superpowers/{plans,specs}/*.md` executable instructions and supersession notes; backport README only where needed. |
| 6 — final delivery | Ignored per-plan evidence/build workspace and `artifacts/server-leaderboards/` four-file bundle; audit artifact evidence. No generated binaries in Git. |

Tasks 1–4 are sequential because their writer/test scopes overlap. Task 5 follows settled interfaces; Task 6 follows reviews. Implementers own focused red/green checks; parent reconciles writer results and owns the final five-harness pass and artifact publication. Each task saves before-file hashes, a scoped diff against those dirty bytes, and a report under `.superpowers/sdd/2026-11-03-reporting-review-fixes/`.

## Verification commands

Run from repository root with an existing compatible SDK. `DOTNET` is its executable; `W` is this plan's ignored workspace; `LITEDB` is the approved source-built dependency, SHA-256 `4785209fa11dcbca8e45ca476a29799e37b270bf99f8bf896acf953c935658cc`. Do not download/install tools automatically. Working local SDK/dependency paths can be recovered from earlier ignored evidence, but are not clean-checkout prerequisites.

```sh
export DOTNET_ROOT="$(dirname "$DOTNET")" DOTNET_ROLL_FORWARD=Major
export DOTNET_GENERATE_ASPNET_CERTIFICATE=false DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
run_check() {
  "$DOTNET" run --project "tests/$1/$1.csproj" --configuration Release --no-restore \
    -p:UseSharedCompilation=false \
    "-p:CustomAfterMicrosoftCommonTargets=$PWD/build/litedb-wal-backport/LiteDbBackport.targets" \
    "-p:LiteDbBackportPath=$LITEDB" "-p:OutputPath=$W/checks/$PHASE/$1/"
}
```

Set `PHASE` to a fresh task/red/green identifier; capture full output/exit code. Expected green is exit zero and every named check passing; verify copied LiteDB hashes for consuming projects. Missing restore assets are a prerequisite failure, not permission for new dependencies. Keep the existing pinned WAL patch bytes, including valid blank diff-context lines.

### Task 1: Structured report transport and safe embed fallback

**Interfaces:** Immutable internal `LeaderboardReport` exposes `string Title`, `string Description`, `IReadOnlyList<(string Heading, string Value)> Sections`, `string PlainText`; defensively copy sections. `CombatReports.BuildReport(snapshot, period, zone, topCount, limitations, bool manual=false, Action<string>? diagnostic=null)` returns `LeaderboardReport?`. Existing `Render(...)` becomes the compatible `BuildReport(...)?.PlainText` wrapper. Production automatic/manual query/send paths use `Action<LeaderboardReport>` and `Task<LeaderboardReport?>`; adapt legacy string callbacks only from DTO to `PlainText`. `DiscordApi.SendRenderedMessage(Webhook.Event, LeaderboardReport)` consumes fields directly. No text-to-model adapter remains.

- [ ] Add Reporting fixtures asserting all 17 sections' headings/values equal the same DTO's literal pieces, Markdown-like hostile names cannot create categories, and old golden literals/ranking/empty/calendar/claims remain unchanged. Add fallback cadence assertions: first failure warns, another at 59 seconds does not, at 60 seconds warns; disabled embeds never warn; a throwing logger still sends exact literal with mentions disabled.
- [ ] Run `run_check CombatReporting`; record the expected behavioral failures before changing production bodies.
- [ ] Designer implements DTO construction in the current budget-selection pass and direct embed fields, retaining trophy/configured color/full-width layout and every qualifier. Use one monotonic scalar throttle for the fixed embed-construction warning, not exception/report data; keep warning emission inside a protected logging call. Keep the transport's original no-mentions object on both branches.
- [ ] Update all actual production callbacks and source-linked test collaborators, not only test formatter helpers. Run `run_check CombatReporting` and `run_check NativeAdapters`; inspect the scoped diff and obtain an independent spec/quality review before continuing.

### Task 2: Transactional retention and permanent signal deduplication

**Interfaces:** Extend `ReportedSignalStore` with `DateTime? RetentionHorizonUtc { get; }` and `int PruneExpired(DateTime nowUtc, int limit=256)`. It owns the borrowed database and existing three detail collections plus additive `server_reported_signal_tombstones_v1` and `server_reported_signal_retention_v1`. Tombstone `_id` is exactly the existing opaque dedup key; no actor/token fields. One global state record stores the UTC-tick horizon. `Insert(ReportedSignalRecord)` retains its boolean contract; a newly accepted expired identity returns true but persists only its tombstone. `Query(...)` propagates the actual horizon; it must not read the clock or silently change old timestamp fixtures.

- [ ] Add real disposable-LiteDB tests for all three kinds: cutoff equality survives, older detail is removed, 257 eligible rows remove only 256 total, restart preserves horizon/dedup, a changed-timestamp replay after prune is refused, and a backdated duplicate of live detail adds no tombstone. Check other collections and case-distinct world IDs remain correct.
- [ ] Add a real transaction-failure fixture using a temporary conflicting index/constraint: failure after candidate tombstone work rolls back every deletion and persisted/cached horizon; reopen and assert identical rows/state. Add backward-clock and huge-forward-clock cases with explicit irreversible-horizon expectations. Run `run_check CombatStatistics` to establish red.
- [ ] Implement indexed timestamp candidate selection with bounded materialization, one transaction per maintenance batch, strict `< max(previousHorizon, nowUtc-90 days)` comparison, and cache publication only after successful commit. Validate UTC/limit input; enforce `1..256`. Check detail/tombstone duplicates before expired insertion; expired new identity writes only the minimal tombstone. Never mutate unrelated collections or compact the file.
- [ ] Run `run_check CombatStatistics` green and review the scoped storage diff independently. Do not manufacture a whole-gameplay transaction or retry on failure.

### Task 3: Bounded maintenance, drain and historical-range qualification

**Interfaces:** Append optional `DateTime? signalRetentionHorizonUtc=null` to `CombinedReportSnapshot` and expose `SignalRetentionHorizonUtc`. `ActivityStore.MaintainSignalRetention(DateTime nowUtc, double monotonic)` returns `Task`, appending admitted maintenance to the existing tail under the existing gate. Capture immutable time on admission. Keep admission state separate from diagnostic acknowledgement state: one pending maintenance, 60-second admission cadence, no eviction/reset of existing quota state. Plugin invokes it from bounded maintenance polling, not from every query or shutdown. Existing `BeginDrain()` joins it. Propagate the store horizon through combined query snapshots into Task 1's description/text qualification.

- [ ] Test full queues skip admission and preserve the reserved diagnostic slot; pending/executing maintenance prevents another; at 59 seconds no second admission, at 60 it may run; invalid/backward monotonic time does not create a retry loop. Exercise a blocked job, clean seal/drain, failure release, and queued backdated insert before versus after maintenance. Run focused Statistics tests red.
- [ ] Implement maintenance without another queue/timer or Unity-thread DB calls. Preserve the existing total 256-job accounting, diagnostic capacity, per-principal reservations and failure classification. Diagnostics for maintenance failure are fixed/sanitized; do not classify maintenance rollback as missing gameplay captures.
- [ ] Add Reporting fixtures for a queried range intersecting the actual persisted horizon: both literal and embed explicitly say signal detail before that horizon expired; recent supported local day/week reports under correct UTC retain their observations, including Dublin DST transitions and seven-second grace. A backward clock never removes the qualification. Empty automatic reports remain quiet.
- [ ] Run `run_check CombatStatistics`, `run_check CombatReporting`, `run_check NativeAdapters`; verify all snapshot/callback links and independent review. No new query API silently promises complete lifetime detail.

### Task 4: Bounded malformed-header probe and warning cleanup

**Interfaces:** Keep `ActivityPatches.Routed.Prefix`'s public 40-byte peek and finally restoration unchanged. Add a fixed-reason `short-header` / `header-read-failed` probe using a 60-second monotonic scalar throttle and a byte count clamped to the existing 1 MiB ingress ceiling. Reuse the current fixed-stage diagnostics helper where it preserves those guarantees; otherwise keep the tiny throttle local. No raw exception, payload, sender or identity.

- [ ] Extend actual NativeAdapters fixtures: every truncated length 0–39, injected partial-read failure at each header component, a throwing logger, bursts within/after 60 seconds, disabled fast path, nested invalid-origin restoration and subsequent valid cooking/manual work. Assert no irrelevant full copy and restored cursor before the original handler. Run `run_check NativeAdapters` red for the absent probe.
- [ ] Add the protected bounded probe without changing admission, schema, ownership or finalizers. Explicitly initialize only the intentionally default-valued native-double fields producing CS0649; do not suppress all warnings or alter gameplay doubles.
- [ ] Run `run_check NativeAdapters` and inspect build output for those specific warning removals. Review the scoped change; preserve unrelated nullable warnings honestly rather than fixing outside scope.

### Task 5: CI, portable instructions and durable review map

**Interfaces:** No runtime API changes. CI must retain Statistics, Tracking, Reporting and Startup Release commands and add NativeAdapters under .NET 8 in the existing combat-regressions workflow.

- [ ] Add the workflow command. Check all five project paths exist and local commands match workflow options; validate YAML with an existing parser if available, otherwise record structural/manual validation rather than install tooling or claim GitHub ran.
- [ ] Update audit/guide with retention scope, tombstone growth, clock/backlog limitations, actual horizon, debug-name removal TODO and rollback/backup cautions. Add a concise review map linking logger/support-template changes and actual Splatform callers/reference evidence; do not split or rewrite commits.
- [ ] Replace machine-specific executable plan/README examples with explicit caller SDK/workspace variables and prerequisites. Preserve exact historical logs as historical evidence. Link superseded preserve-all-signal-history decisions to the new approved policy without changing their historical approval claims.
- [ ] Check local Markdown targets, search affected executable examples for `/Users/filip`, run `git diff --check`, and review docs/config diff. No invented test, runtime or Mono-backport assurances.

### Task 6: Reconcile, review and publish the local testing DLL

- [ ] Reconcile all writer scopes and unresolved findings. Run `run_check` once for each of CombatStatistics, CombatTracking, CombatReporting, ServerStartRegression and NativeAdapters against settled source; record actual counts/exits and dependency hashes rather than reuse historical 442 results.
- [ ] Obtain one independent cumulative spec/quality review from the ordered scope diffs and actual final source. If findings require fixes, apply one bounded correction wave, rerun affected checks and obtain scoped re-review; reuse unaffected valid evidence.
- [ ] Reuse the proven isolated Release/net481/ILRepack recipe in a new ignored build workspace: explicit approved LiteDB override and before-compile/before-merge hash guards, `CandidateAssemblyFiles=false`, `ExecutePrebuild=false`, empty plugin-copy destinations, separate outputs and five last message-only publication overrides. Snapshot current source/config, references, protected files, dependency inputs and previous four-file public bundle. Stop on real compile/API errors rather than silently changing source or restoring unrelated outputs.
- [ ] Verify actual native build log, merged implementation/WAL metadata, staged size/hash and preservation manifests. Metadata is not runtime execution. Back up the previous bundle and atomically replace only `DiscordConnector.dll`, license, checksums and provenance under ignored `artifacts/server-leaderboards/`; independently verify public checksums. Record new artifact evidence in the audit.
- [ ] Report files changed, current Git status/diff summary, actual checks/build warnings and DLL path/hash. Leave all changes uncommitted. Explicitly state live PS5/Harmony/Discord, real-traffic performance and exit durability were not checked.

## Self-review

Spec coverage: structured delivery/fallback Task 1; all expiry/dedup/transaction rules Task 2; bounds/drain/historical horizon Task 3; native probe/warnings Task 4; CI/privacy/portability/review map Task 5; cumulative tests/review/build Task 6. All five Review Focus cases have explicit owning fixtures. DTO, horizon and maintenance signatures are consistent across producers/consumers. No commit or operational step is authorized; existing uncommitted work is the baseline, not expendable scaffolding.
