# Observation Pipeline Redesign Implementation Plan

Historical plan: its preserve-history policy is superseded only for the three reported-signal detail collections by the approved [November 3 retention design](../specs/2026-11-02-reporting-review-fixes-design.md). Other histories remain untouched.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. Leave changes uncommitted: no commit or branch operation is authorized.

**Goal:** Fix the validated review findings and add an honest, independent reported dodge-hit notification counter without requiring client mods or simulation takeover.

**Architecture:** Classify cheaply before authenticated, fairly bounded admission; retain existing origin scopes, collectors and serialized database worker. Persist cumulative diagnostic counters idempotently, independently of non-replayed gameplay writes. Revalidate native identity/ownership at dispatch and isolate lifecycle/reporting failures.

**Tech Stack:** Existing C#/net481 plugin, Harmony, BepInEx and LiteDB 5.0.21; existing net8 executable test harnesses and available .NET SDK. No new dependency.

**Spec:** `docs/superpowers/specs/2026-11-02-observation-pipeline-redesign-design.md` (user approved November 2).

## Global Constraints

- No required client/simulation mod, ownership takeover, enlarged simulation area, inventory manipulation or new client RPC protocol.
- Preserve manual authorization, Flex/Toast configuration, shared 60-second cooldown, seven-second grace, calendar/DST, claims and at-most-once delivery. Never replay gameplay batches or sends; only absolute diagnostic snapshots may reconcile.
- Preserve old enum values, collection meanings, configuration values and public contracts. New dodge metric, kind, collection and flag are additive and default off; do not repurpose avoidance history.
- Retain one serialized worker, 256 pending batches, two report jobs, 128 physical documents/batch, 8 MiB outstanding signal charge at 4 KiB/record, shared routing depth/pending limits of sixteen, and the 1,900 UTF-16-unit report ceiling.
- Keep separate manual replay (256 keys/seven seconds), signal replay (4,096 keys/60 seconds), raw activity replay (4,096 keys/seven seconds); prune at most 32 expired entries per admission. Staged signals expire after five monotonic seconds.
- No history/WAL deletion, retention migration, automatic pruning, dependency installation, commits, branch changes, deployment or live server operation. Preserve all existing dirty/untracked files and root trackers.
- The current DLL and historical 179 checks do not validate this redesign. A replacement DLL requires a separate request; native compilation/runtime tests must not be claimed from adapter tests.

## Review Focus

1. An unrelated or disabled nested RPC must mask its parent's origin yet consume no observation replay/quota or parameter copy; cooking and the next manual command still work (Tasks 1–2).
2. Reconnect churn and uncertain writes must not mint fairness capacity, replay gameplay, or double diagnostic counts (Tasks 2–3).
3. A dodge beneficiary, reporter and character owner can be three different identities; ownership/session changes must invalidate pending work (Task 5).
4. A manual/coverage/cleanup exception must not disable unrelated automatic scheduling or prevent database admission sealing (Task 4).
5. Seventeen nonempty metrics, hostile names and long qualifiers must all fit without false zeroes or implying complete/unique dodges (Task 6).

## File map and execution order

| Area | Files / responsibility |
|---|---|
| Native ingress and identity | Modify `DiscordConnector/Patches/{ActivityPatches,NativeSignalPatches,StablePlayerResolver,ActivityRuntime}.cs`: cheap filters, bounded snapshots, exact native witnesses and session capability. |
| Admission | Modify `DiscordConnector/Leaderboards/ManualCommandRouting.cs`, `DiscordConnector/Records/{NativeSignalCollector,ActivityStore}.cs`; create `DiscordConnector/Records/ObservationAdmission.cs`: fixed bounded per-principal policy, not another queue/event framework. |
| Diagnostics | Create `DiscordConnector/Records/ObservationLoss.cs`; modify `ActivityModels.cs`, `ActivityCollector.cs`, `ActivityStore.cs`, `DatabaseDiagnostics.cs`, routing diagnostics and native coverage wiring. Own cumulative counter contracts here. |
| Lifecycle | Modify `DiscordConnector/Plugin.cs`, `Records/Database.cs`, `Patches/ActivityRuntime.cs`, and only required report-poll seams in `Leaderboards/CombatReports.cs`. |
| Dodge integration | Modify `ReportedSignalRecord.cs`, `ReportedSignalStore.cs`, `NativeSignalCollector.cs`, `ActivityModels.cs`, native patches/resolver, `Config/ObservedLeaderboardsConfig.cs`, `Plugin.cs` and `CombatReports.cs`. |
| Tests | Create `tests/NativeAdapters/{NativeAdapters.csproj,Program.cs,NativeStubs.cs}`; extend existing Tracking, Statistics, Reporting and Startup harnesses and explicit source links. Real production adapters, not identically named replacements. |
| Documentation | Update `docs/server-statistics-audit.md`, modding guide and `build/litedb-wal-backport/README.md`; preserve historical evidence with explicit dates. |

Execute Tasks 1 → 2 → 3 → 4 → 5 → 6 → 7, with a scoped review after each independently tested deliverable. They share interfaces/files: do not dispatch parallel writers. Record pre-task dirty-file snapshots, scope-only diffs (including untracked files), actual checks and unresolved limits under `.superpowers/sdd/2026-11-02-observation-pipeline-redesign/`. Parent reconciles and owns the final full-suite run; task owners run only assigned harnesses. No ceremonial full-suite reruns per task.

## Verification commands

Working directory: your repository checkout. Supply an installed compatible SDK, writable scratch workspace and verified backport DLL. Check prerequisites and dependency hash; stop rather than silently install missing software. Historical local execution paths are evidence, not required prerequisites. Define in the execution shell:

```sh
: "${DOTNET_ROOT:?Set to your installed SDK directory}"
: "${WORKSPACE:?Set to a writable scratch directory}"
: "${LITEDB_BACKPORT:?Set to the verified backport DLL path}"
export DOTNET_ROOT
export DOTNET_ROLL_FORWARD=Major DOTNET_GENERATE_ASPNET_CERTIFICATE=false
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
W="$WORKSPACE"
L="$LITEDB_BACKPORT"
run_check() {
  "$DOTNET_ROOT/dotnet" run --project "tests/$1/$1.csproj" --configuration Release --no-restore \
    -p:UseSharedCompilation=false \
    "-p:CustomAfterMicrosoftCommonTargets=$PWD/build/litedb-wal-backport/LiteDbBackport.targets" \
    "-p:LiteDbBackportPath=$L" "-p:OutputPath=$W/checks/$2/$1/"
}
```

`L` must hash to `4785209fa11dcbca8e45ca476a29799e37b270bf99f8bf896acf953c935658cc`; verify copied dependencies in each consuming output too. Each run must exit zero and print all named assertions passed; record stdout/exit code separately from expected outcomes. The new adapter project should reuse installed/cached references. If it needs an assets-file restore, use `dotnet restore tests/NativeAdapters/NativeAdapters.csproj --ignore-failed-sources` with the same certificate-safe environment and no new package versions; stop if prerequisites are unavailable. SDK restore is not permission to install software or download game assemblies.

### Task 1: Real-adapter coverage and cheap native classification

**Files:** native ingress/resolver files above; new NativeAdapters harness and minimal native/Harmony doubles. Keep existing Tracking stubs for their old scope.

**Interfaces:** retain `ActivityPatches.Routed.Prefix/Finalizer` and shared `ManualReceive` signatures; retain `NativeSignalCollector.ValidPayload(ReportedSignalKind,bool,int,Func<byte[]>)`. Change the internal snapshot method from void to `bool StablePlayerResolver.NativeSignalSnapshot(DateTime utc, out NativeSignalPeer[] peers, out (long User,uint Id)[] characters)`; false means bounded evidence could not be obtained, with empty outputs, and every capture/dispatch caller must reject. New internal `bool ActivityPatches.ShouldObserve(int methodHash, LeaderboardsOptions options)` classifies `SetTrigger`/rolls, `RPC_RequestOwn`/pickups and `RPC_AddItem`/cooking using static hashes. It does not authenticate or reserve replay.

- [ ] Add counting native doubles and compile the actual `ActivityPatches.cs`, `NativeSignalPatches.cs`, `StablePlayerResolver.cs` plus required real helper/config files. New tests invoke real prefixes/finalizers and prove existing unfiltered behavior fails: unrelated RPCs cause peer/parameter work or consume replay; enabled non-eat SetTrigger snapshots peers before schema validation.
- [ ] Run `run_check NativeAdapters task-1-red`; record the expected failing assertions, not just a missing-type compilation error.
- [ ] Move relevance/flag/header classification ahead of nested parameter materialization, transport expansion and replay reservation. Preserve an invalid masking ingress frame before any early return. Use a bounded independent package view only with verified native buffer APIs; no gameplay cursor reads/restores, unsafe backing-buffer assumptions or second decoded-signal transport decoder. If native APIs cannot avoid one envelope copy, retain that bounded copy and document it rather than claiming zero copying.
- [ ] Validate decoded payload sizes/schema before `NativeSignalSnapshot`; retain origin authenticity and fresh dispatch validation. Replace snapshot's nested `Resolve` calls with one indexed traversal, detecting duplicate account/UID/full-character/socket/transport mappings. No cross-observation identity cache. Bound snapshot input to 256 peers and 256 player-list records; overflow fails unavailable, never truncates identity evidence.
- [ ] Assert disabled/all-unrelated traffic does no parameter copy/full-peer snapshot and leaves replay count unchanged; malformed/trailing/oversized and non-eat payloads do no full snapshot; cursor positions stay unchanged. Verify cooking AddItem provenance, forged sender, nested invalid masking, exceptional unwind and next independent manual recovery. Assert traversal counts grow linearly at 1/16/64 peers and reject 257, rather than timing performance.
- [ ] Run `run_check NativeAdapters task-1-green` and `run_check CombatTracking task-1-green`; inspect source links to ensure no tested adapter was replaced by a stub. Scoped source review; hand off classifier/snapshot contracts.

### Task 2: Fair bounded admission across replay and executing work

**Files:** `ObservationAdmission.cs`, shared routing, raw activity ingress, native collector, `ActivityBatch`/store; minimal authenticated-origin caller wiring in `Patches/NativeSignalPatches.cs`; adapter/Tracking/Statistics tests.

**Interfaces:** introduce immutable `ObservationPrincipal(string accountId,string connectionToken,bool serverLocal)`; values are derived from authenticated origin, not payload beneficiary. `ObservationAdmission.TryAdmit(ObservationPrincipal principal,double monotonic,out ObservationRejection reason)` returns an explicit reason. `ObservationRejection` distinguishes Duplicate, ReplayCapacity, PrincipalQuota, PrincipalCapacity, PendingOverflow, Stale and QueueFull. Duplicate detection stays in existing replay owners; quotas charge only new, completed eligible observations. Add optional `ObservationPrincipal? signalPrincipal=null` to `ActivityBatch`; mixed-principal signal batches reject. Existing non-signal callers remain source compatible.

- [ ] Write fixtures showing one account can currently consume global replay/queued signal capacity and reconnect can bypass a connection-only limiter. Run failing checks in Tracking/Statistics; assert the failure is the admission policy, not absence of API scaffolding.
- [ ] Implement per-world account and connection limits: at most 128 retained principals; at most 128 new signal replay identities per stable account and per connection per rolling 60 seconds. Raw activity uses independent 128 identities per account/connection over seven seconds. Server-local origin has its own 128-key allowance per window, not a peer. Retain disconnected identity/tombstone charges until window expiry; never evict live replay keys to admit new ones. Bound all indexing/FIFO state; inspect at most 32 expired entries per admission. Clear world-specific state only on world replacement, without changing old durable dedup.
- [ ] Reserve per-account outstanding signal work inside `ActivityStore.Enqueue`: maximum sixteen batches/64 KiB per account and per current connection, including executing work. Server-local allowance is also sixteen/64 KiB. Retain global limits and report slots; release reservations in every completion/attachment-failure path. This deliberately limits bursts without promising fairness against many cooperating accounts. Manual replay/auth/cooldown does not spend these quotas.
- [ ] Keep duplicate suppression distinct from full-cache refusal at observe and root dispatch. Shared overflow still discards the entire enclosing scope without accepting gameplay or consuming its replay key. Charge quotas at successful root completion before callback; enqueue refusal is a loss, never a retry opportunity. Do not count Handle+Route duplicate witnesses as new quota work.
- [ ] Test exact 128/129 and sixteen/seventeen boundaries, two honest peers during flood, 129th principal, disconnected/reconnected account, expired tombstones, backward/invalid clock, world reset, failed jobs and queue attachment failure. Verify no charge leak and no manual starvation. Run `run_check CombatTracking task-2-green`, `run_check CombatStatistics task-2-green`, `run_check NativeAdapters task-2-green`; scoped review freezes principal/reason contracts.

### Task 3: Cumulative, idempotent loss and uncertainty diagnostics

**Files:** `ObservationLoss.cs`, collector/models/store/diagnostics, routing diagnostics/native coverage, Statistics/Tracking/Reporting fixtures.

**Interfaces:** `ObservationLoss.Record(string world,ReportMetric metric,ObservationRejection reason,long count=1)` saturates confirmed rejections; `RecordUncertain(string world,ReportMetric metric,long count=1)` is separate. Immutable `ObservationLossSnapshot` carries world, metric, process epoch, cumulative reason totals, uncertainty total and UTC observation time. `ActivityStore.ReconcileLossAsync(ObservationLossSnapshot snapshot)` returns `Task` acknowledgment through the same worker. It never contains gameplay records. Use a new `server_observation_v2_loss` collection keyed by world/metric/process epoch; old coverage remains readable and is not rewritten.

- [ ] Add failing real-LiteDB tests for three coalesced drops becoming one, full-cache new-message versus duplicate, and ambiguous commit followed by repeated diagnostic submission. Run `run_check CombatStatistics task-3-red` and relevant Tracking assertions.
- [ ] Thread explicit rejection reasons from Task 2 into saturating per-kind totals. Count discarded new pending observations once, not repeated stages. Unknown/malformed traffic is bounded aggregate diagnostics, not proof a gameplay event was lost. Report a failed gameplay write as uncertain where commit outcome is unknown; do not claim it is absent or resubmit it.
- [ ] Write absolute diagnostic snapshots with per-field monotonic maximum, preserving cumulative totals across reopen; use existing saturating arithmetic. Keep each process epoch distinct and aggregate once across epochs. Coverage coalescing retains numerical deltas; no conversion of N losses into one state update. Query exposes known losses and uncertainty separately without replacing historical coverage counters.
- [ ] Retain at most 64 world/metric snapshots in memory, one latest value per key. Retain unacknowledged values on enqueue/write/ack ambiguity; retry only an absolute diagnostic snapshot on the next ten-second maintenance opportunity, at most one pending diagnostic job. Reserve one existing batch slot for diagnostic work (gameplay admission max 255 while this facility is active); never create another worker or exceed 256 total. Reject overflow of diagnostic state with a bounded incomplete-coverage warning. Shutdown makes one final submission attempt before admission seals, not an unbounded retry loop.
- [ ] Test acknowledgment lost after commit, repeated/lower snapshots, multiple epochs, flush/reset/world change, saturation, 65th state key, collector enqueue refusal, failed DB write and reopen. Assert gameplay insertion/send invocation count is unchanged by reconciliation. Run Statistics, Tracking and Reporting with `task-3-green`; scoped review checks duplicate-free aggregation and narrow replay exception.

### Task 4: Session capability, maintenance allocation and lifecycle containment

**Files:** `Plugin.cs`, native patches/runtime, `Records/Database.cs`, only required report-poll test seams; NativeAdapters/Startup/Tracking tests.

**Interfaces:** retain `NativeSignalPatches.InspectHooks(string owner)`, `SetWorld(string world)` and `UpdateCoverage(DateTime utc)`. Introduce `NativeSignalPatches.SetSession(string world,string sessionEpoch,string owner)` to invalidate capability and inspect exactly once on each first usable/new session. `Database.BeginShutdown()` remains idempotent and asynchronous. Use internal plugin stage methods `UpdateObservationState(DateTime,double)`, `UpdateReportState(DateTime,double,string)` and `BeginObservationShutdown()` as directly exercised seams, not a new lifecycle framework.

- [ ] Add failure-injection adapter tests: initial unsupported hook inventory, supported next session, later unsupported session; manual poll/coverage exceptions; cleanup throwing before drain initiation. Demonstrate current failure paths before changing production bodies. Compile actual production Plugin lifecycle methods in the adapter harness with minimal collaborator doubles, not a copied algorithm.
- [ ] Move startup hook inspection to first usable world/session and every session/world transition; cache the checked epoch. Invalidate before collecting during transitions. Include exact targets/own hooks, priorities/constraints and unsupported prefixes/transpilers; preserve pinned NPS handling. No per-frame/RPC reflection; arbitrary mid-session hot-patching is explicitly unsupported.
- [ ] Cache immutable effective option snapshots until bound values change; static metric arrays and no allocation of update lists before work is due. Poll coverage at most once/second, loss flush every ten seconds, and immediately invalidate on observed world/session/config transitions. Position/gameplay sampling retains its existing cadence. Add structural/counting assertions for no equivalent per-frame options/list/metric-array allocation; do not invent live GC/frame-time results.
- [ ] Isolate diagnostics, world/observation maintenance, coverage, manual polling, automatic polling and automatic tick failures. Log the actual fixed stage with existing bounded redaction. A failed world lookup invalidates observation context and skips world-dependent work; never reuse a stale world. Manual/coverage failure must not dispose the automatic scheduler. Preserve its own invalid-calendar disable and no retry of uncertain claim/send.
- [ ] Make all cleanup paths independent and ensure admission sealing/drain initiation occurs in `finally` even if reset/flush/dispose/logging throws. Expose fixed pending-batch/report and known-failure status in shutdown diagnostics. Observe async completion without blocking Unity; record completion as drained, not all-writes-successful. Preserve DB ownership and close only after queues/readers finish. No exit-durability promise or invented host hook.
- [ ] Run `run_check NativeAdapters task-4-green`, `run_check ServerStartRegression task-4-green`, `run_check CombatTracking task-4-green`. Test successful and failed admitted writes, reader drain, repeated shutdown, earlier cleanup failures and no premature database close; document abrupt-exit and unflushed-memory limits. Scoped review.

### Task 5: Add the partial reported dodge-hit signal

**Files:** signal record/store/models, native collector/resolver/patches, config/Plugin, Tracking/Statistics/NativeAdapters and source links.

**Interfaces:** append `ReportedSignalKind.DodgeHitNotification`, `RoutingObservationKind.DodgeHitNotification`, `ReportMetric.ReportedDodgeHitNotifications`; append optional `reportedDodgeHits=false` to `LeaderboardsOptions`. Bind `[Server-Reported Combat] Dodge Hit Notifications Enabled=false`. Add separate `reported_dodge_hit_notifications_v1` history. Extend `ObserveLocalSignal` with an explicit `RoutingObservationKind kind` overload while retaining the existing local-kill overload. Add immutable `NativeDodgeTarget` evidence: `NativeSignalPeer Beneficiary`, `(long User,uint Id) Character`, `long OwnerUid`, `NativeSignalPeer? Owner`. Define `bool NativeSignalCollector.TryDodgeIdentity(bool local, object? actualTransport, long serverUid, long claimedSender, long recipient, (long User,uint Id) character, long actualOwner, IReadOnlyList<NativeSignalPeer> peers, IReadOnlyList<(long User,uint Id)> characters, out NativeSignalPeer? reporter, out NativeDodgeTarget? target)`. The adapter supplies independently resolved native ownership; unavailable evidence never becomes an invented owner. Do not overload kill-credit target identity rules.

- [ ] Add real adapter failing cases for exact `RPC_HitWhileDodging`, zero-size parameter package, separate reporter/beneficiary/owner and local-origin envelope creation. Pin the supplied assembly SHA and NPS commit from the spec; metadata is not runtime reward proof.
- [ ] Accept only exact empty payload (size zero; disabled/one-byte payload must not copy or snapshot). Resolve beneficiary from unique current full player character and recipient from that character's actual nonzero ZDO owner. Support owner=beneficiary, third ready authenticated peer or current server epoch. Missing local view/ownership evidence is unavailable, not a guessed mapping. Authenticate reporter independently; do not assume it is a player attacker or dodger.
- [ ] Capture/revalidate reporter, beneficiary and distinct owner sessions, full character, owner UID, server epoch/world and enabled flag before dispatch. Genuine local native invocation must target this exact method/full character/nonzero owner; no fabricated peer. Preserve scope masking, successful enclosing unwind and skipped-original witness semantics. Dedup repeated Handle/Route/retransmit only; distinct message IDs remain separate notifications regardless of time proximity.
- [ ] Extend all fixed arrays/kind mappings, coverage and token identity without changing previous enum values or collections. Apply Task 2 quotas to reporting origin, not beneficiary, and Task 3 counters to this new kind. Existing avoidance remains unavailable; existing rolls unchanged.
- [ ] Test sender/dodger/owner all distinct; server owner; owner transfer/reconnect/stale full character; absent view; broadcast/unknown owner; disabled and trailing payload; forged sender; duplicate versus distinct notifications; exception and genuine local scopes. Assert same-client-only dispatch produces no server observation and no synthetic count. Run adapter, Tracking and Statistics `task-5-green`; scoped review.

### Task 6: Seventeen-section reports, growth evidence and portable operator docs

**Files:** `CombatReports.cs`, real-store Reporting fixtures, Statistics growth fixture, audit/modding/build README. No visual redesign/new destination.

**Interfaces:** heading `Reported dodge-hit notifications`; compact qualifier must preserve partial visibility, possible multiple notifications per dodge, and no proof of accepted rewards or unique attacks. Query through existing combined snapshot/options. Add optional `--growth-check` mode to Statistics harness, not a production maintenance API.

- [ ] Extend the existing mixed real-store fixture to all seventeen sections and independent new flag/history. Add worst-case previews, hostile names, positive fractions, losses/uncertainty and full qualifier budget. Run Reporting to observe the missing-section/budget failures.
- [ ] Reserve one compact row per enabled nonempty metric before extra ranks; retain global 1,900 UTF-16 ceiling, literal escaping/no mentions, deterministic ordering, calendar/DST/claim/manual behavior. Do not call the counter perfect dodges, successful rewards, distinct attacks or a complete statistic. Render losses/uncertainty honestly without crowding out required sections.
- [ ] Implement a disposable growth check at 1,000 and 10,000 unique signal rows with ten accounts, repeat inserts and indexed half-open day queries. Assert exact dedup/count/window results and output DB bytes, insert/query elapsed time and row counts; no universal latency threshold. Use a newly created temporary DB and remove only that owned fixture on completion; never open server records.db. Document append-only growth and measurement limits, not automatic retention.
- [ ] Replace machine-specific SDK quickstart in backport README with explicit user-supplied executable/workspace variables, retaining pins/hash/license, opt-in behavior and testing-only claims. Validate arguments against actual build.py help without downloading or building. Keep logger support docs associated with actual logger behavior; preserve potentially user-owned root trackers.
- [ ] Run Reporting and Statistics `task-6-green`; run the explicit growth mode once using the same SDK/backport arguments. Check local documentation links and `git diff --check`. Update audit's coverage, flags, quota values, diagnostic reconciliation and shutdown limits. Scoped review.

### Task 7: Reconcile, final verification and handoff

**Files:** only necessary integration corrections, current audit and ignored execution evidence. No automatic new testing DLL.

- [ ] Compare all phase snapshots/diffs and explicit source links, including untracked files. Resolve findings in one bounded fix wave; no unrelated cleanup. Check public contracts/config/defaults, unchanged prior histories and absence of hidden simulation/client dependencies.
- [ ] Run the full suite once after writers settle: `run_check CombatStatistics final`, `run_check CombatTracking final`, `run_check CombatReporting final`, `run_check ServerStartRegression final`, `run_check NativeAdapters final`. Save exact commands, exit codes, counts, logs and copied dependency hashes. Expected: all assertions pass/exit zero; previous 179 is not the target count.
- [ ] Request one independent cumulative source review against approved design, phase diffs and actual test results. Fix Important/Critical findings, rerun only affected checks and preserve still-valid evidence. Record dispositions for non-bugs/policy matters: no automatic retention, no unverified Unity blocking, logger docs are related, transport scan was linear but old nested snapshot quadratic, no claim the explicit opt-in backport is a proven Mono fix.
- [ ] Finish with changed files, actual verification outcomes, concise git status/diff summary, uncommitted status and remaining live checks. Do not replace `artifacts/server-leaderboards/DiscordConnector.dll`: it remains the previous testing artifact until a new build is requested. If requested later, reuse guarded isolated compile/ILRepack and merged-metadata verification after source settles, then hash-verify before local publication. Live crossplay/NPS, resource profiling and orderly-stop validation require separate authorization.

## Plan self-review and acceptance

Coverage: spec sections 1–2 → Task 1; fair admission → Task 2; diagnostic reconciliation → Task 3; capability/lifecycle → Task 4; native notification → Task 5; report/growth/portability → Task 6; reviewability/final evidence → Task 7. All five Review Focus cases have explicit fixtures. Interfaces are internal/additive; enum/collection compatibility and source-linked callers must be reviewed per phase. Quotas and polling constants are engineering choices documented here, not claims about measured server capacity. Phase reviewers may reject these choices if fixtures show starvation, but must not silently loosen global bounds or expand operational authority.

Status: plan prepared for user review and execution-method selection; not executed. Only the approved design and this plan were changed for this planning step.
