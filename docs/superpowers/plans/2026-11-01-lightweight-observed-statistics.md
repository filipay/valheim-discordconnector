# Lightweight Observed Statistics Implementation Plan

Historical plan: its preserve-history policy is superseded only for the three reported-signal detail collections by the approved [November 3 retention design](../specs/2026-11-02-reporting-review-fixes-design.md). Other histories remain untouched.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove Dedicated Simulation prerequisites and add honestly labelled native kill-credit and consumable-use observations, while investigating—not promising—cauldron and preparation-table activity.

**Architecture:** Extend the existing decoded-routing observation scope, immutable batches and serialized LiteDB worker. New reported-signal records stay separate from confirmed combat and existing action history. Native ownership and accepted-transition safeguards remain; nothing creates more world simulation.

**Tech Stack:** Existing C#/net481, BepInEx/Harmony, native Valheim APIs, LiteDB 5.0.21 with the approved opt-in WAL-flush backport, existing console test projects.

**Spec:** [Approved design](../specs/2026-11-01-lightweight-observed-statistics-design.md).

## Global Constraints

- No required client mod, custom client handler, forced ownership, enlarged simulation area or extra world-object instantiation.
- No mandatory NPS, ServersideQoL, Jotunn or Dedicated Simulation dependency. Installed mods may affect visibility; their presence is not evidence of an action.
- Preserve existing configuration keys and default-off collection. New flags also default off.
- Separate confirmed observations from client-reported presentation/credit signals. Do not combine them into a misleading total.
- No database deletion, history reinterpretation, automatic replay or deployment. Existing unrelated uncommitted changes remain intact.
- Preserve the global 1,900 UTF-16-unit budget, escaped literal text and disabled mentions.
- Work in the existing user-selected checkout/branch unless separately instructed. No commits, pushes, branch changes, live experiments or deployment without explicit current authorization.
- Retain working Flex/Toast authorization, 60-second manual cooldown, seven-second flush grace, scheduler/claim namespace, local calendar/DST semantics, asynchronous owned shutdown and direct BepInEx logging.
- Preserve manual-trigger configuration values; this work does not enable triggers or collection automatically. The guide's October 31 incident warning is historical: subsequent November 1 manual delivery succeeded, without proving every runtime path.

## Review Focus

1. A recipient differs from the authenticated reporting peer: credit the uniquely resolved target, not the sender; Task 3 pins this.
2. NPS skips vanilla routing and several hooks see one message: one accepted observation, but an enclosing exception yields none; Task 3 pins this.
3. A closed database, restarted collector or full batch must not create replay credit or bypass durable dedup; Tasks 1 and 3 pin this.
4. Removing the GUID gate must not admit client-owned combat/stations or hide previously valid history; Task 2 pins this.
5. Sixteen nonempty report sections, hostile labels and unavailable metrics must fit without losing qualifications or confusing reported credits with confirmed deaths; Task 4 pins this.

## Files and dependency boundaries

- Task 1 owns `Records/ReportedSignalRecord.cs`, `Records/ReportedSignalStore.cs`, `ActivityModels.cs`, `ActivityStore.cs`, diagnostic batch accounting and `tests/CombatStatistics/*`.
- Task 2 owns simulation-gate removal in existing combat/cooking/config/runtime/plugin code and `tests/CombatTracking/*`; it consumes Task 1's extended options.
- Task 3 owns decoded native signal hooks, shared routing helper and signal collector; it takes the Tracking harness only after Task 2 hands it off. It may extend existing manual-routing tests in Reporting, handing that project to Task 4 afterward.
- Task 4 owns report/config integration and `tests/CombatReporting/*`, after Tasks 1–3. Do not run overlapping writers on these shared files.
- Task 5 is a bounded crafting feasibility deliverable, independent of shipping the supported metrics. Task 6 owns final integration review, documentation and isolated build.
- Execute sequentially where files overlap; do not redesign the database worker, existing manual feature or sampling collectors.

## Frozen contracts and bounds

- Append `ReportedKillCredits` and `ObservedConsumableUses` to `ReportMetric`, preserving existing enum values. Append optional `bool reportedKillCredits=false, bool consumableUses=false` to `LeaderboardsOptions`; expose same-named getter properties and map them independently in `Includes`.
- `ReportedSignalKind { KillCredit, ConsumableUse }`; immutable `ReportedSignalRecord(ReportedSignalKind kind, string worldId, ActivityActor actor, string token, DateTime timestampUtc)`. One record means one signal, never item quantity or distinct death. Validate UTC/nonblank fields with existing helpers; bound token to 256 characters. Do not store raw packets or unnecessary creature/modifier payloads.
- `ReportedSignalStore(LiteDatabase db)` borrows the connection; `bool Insert(ReportedSignalRecord record)` and `CombinedReportSnapshot Query(string world, DateTime startUtc, DateTime endUtc, LeaderboardsOptions options)` run only on the existing worker. Two collections: `reported_kill_credits_v1`, `observed_consumable_uses_v1`. Keys derive from world/kind/token using the existing exact-key hash; inserts are immutable and deduplicated. Reuse local UTC-tick serialization, ordinal identity checks, half-open windows and streaming top-16 aggregation.
- Append optional `ReportedSignalRecord[]? signals=null` to `ActivityBatch`; defensive-copy it and count one physical document per signal. Keep 128 physical documents/batch, 256 pending batches and two report jobs. `ActivityStore` constructs its borrowed `ReportedSignalStore` internally, writes signals in capture order, merges query rows and lists the two new collections. Add a fixed diagnostic `Signal` stage/count without logging payloads. Replace snapshot coverage's hardcoded `Take(14)` with the number of defined metrics; no unbounded coverage history.
- Canonical durable token: hash of kind/world/origin/session/message ID/target stable account/current full character ID. Remote session comes from the authenticated connection token; local origin uses a per-process random epoch plus server UID. Never derive a token from a display name, message time alone or caller-supplied account.
- Shared routing retains maximum depth 16 and pending observations 16 per outer receive. Preserve manual replay capacity 256/TTL seven seconds; maintain a separate signal replay cache of maximum 4,096 keys/TTL 60 seconds so ordinary signals cannot exhaust the manual replay cache. Shared nesting/pending overflow still fails the enclosing scope closed, including any manual observation; document and test that deliberate bound. At capacity reject and increment bounded existing coverage counters; no arbitrary eviction that recounts a current message. Durable keys still prevent replay after cache expiry while the same connection/session identity remains.
- New `NativeSignalCollector(Func<ActivityBatch,bool> enqueue, Action<string> warn)` exposes `bool Observe(ReportedSignalRecord record)` and `Reset()`. Submit accepted records to the existing bounded worker; on rejection record a saturating low-cardinality drop count, never retry/replay automatically. No extra task/queue per observation.
- Parameters: copy at most 4,096 bytes for kill credits and at most 64 bytes for `SetTrigger`; require exact payload exhaustion. Native kill payload is `string creatureName, int bossNumber, int modifiers, int attackers, bool cheatsUsed` (sender comes from envelope). Bound creature text to 256 characters, boss number nonnegative, modifiers to defined native values 0–5, attackers 1–1,024; these are admission limits, not facts proving a kill. `SetTrigger` payload is one exact string `eat`.
- Header/section budgets must handle all sixteen enabled nonempty metrics within 1,900 UTF-16 units. Top count remains a maximum, not a guarantee of five rows in every section.

### Modding-guide review safeguards

- New signal records reject (do not truncate identities) world/account strings over 256 UTF-16 units, names over 128, connection tokens over 128 and record tokens over 256. Charge 4,096 bytes per queued signal, including conservative string/object overhead: at most 128 signals/512 KiB per batch and 8 MiB of outstanding signal charges across queued and executing batches. Reserve under the existing admission lock, release in the job's `finally`, and reject overflow without replay. This bounds the new signal workload, not every preexisting worker payload.
- Signal replay cleanup examines at most 32 oldest entries per admission using bounded insertion order; no full-cache scans or auxiliary unbounded history. At capacity reject until cleanup creates space. Decoded staged signals older than five monotonic seconds are discarded before callback/admission; accepted immutable database jobs are not expired or retried. Test exact boundaries, saturation and charge release on failures/drain.
- Before enabling native observation, produce a pinned signature/call-path table for remote receive, forwarded Route/Handle witnesses, genuine local four-argument envelope creation, NPS relay/skip and exceptional unwind. Explain why typed local handlers alone miss forwarded signals. A decoded witness proves only the supported reported signal, not effective gameplay success; missing path evidence leaves that capability unavailable, with no raw-packet fallback.
- Inspect exact native targets and actual loaded Harmony patch owner IDs/priority constraints after initialization using `GetPatchInfo`, without invoking patch factories. Emit one bounded capability summary and fixed first-hook/admission/rejection counters through existing diagnostics, not per-packet logs. The known NPS plugin GUID is an ordering hint, not proof of its Harmony owner or live execution. Missing/unsupported hooks fail unavailable; compilation and a patch inventory do not establish execution.
- Test detached native-admission decisions: absent actual connection, remote claimed-server UID, nested local/remote masking, stale or ambiguous target mappings, world/session replacement, disabled-before-copy and oversized/malformed/trailing payloads. Keep native wiring inspection separate from these policy tests; no new fake-game framework.
- Explicitly test mixed manual/signal pending and depth overflow, nested exceptions, restoration, separate replay budgets, world reset and disposal. The current failing scope emits nothing and consumes no replay identity; the next independent valid manual request retains its original authorization/cooldown behavior.

### Task 1: Separate reported-signal persistence

**Files:** create `DiscordConnector/Records/{ReportedSignalRecord,ReportedSignalStore}.cs`; modify `DiscordConnector/Records/{ActivityModels,ActivityStore,DatabaseDiagnostics}.cs`; extend `tests/CombatStatistics/{Program,ActivityStoreTests,DatabaseDiagnosticsTests}.cs` and `tests/CombatStatistics/CombatStatistics.csproj`; update explicit source links in other affected harness projects when needed, without running their suites here.

**Interfaces:** consumes `ActivityActor`, `ObservationValues`, existing batch/worker/query contracts; produces the frozen records/store/options/metrics/batch extensions above. Existing `ActivityStore` constructor stays compatible.

- [ ] Add compact `ReportedSignalsRestartAndIsolation`: insert duplicate kind/world/token twice → count 1; same token in the other kind/world remains separate; reopen → count unchanged; exact `[start,end)` boundaries and case-distinct accounts; old combat/action rows never appear in new metrics. Add `SignalBatchAccounting`: 128 one-document signals accepted as a batch, 129 rejected before enqueue; new coverage metrics survive snapshot construction; redacted diagnostics include only stage/count.
- [ ] Run the Statistics harness below and observe the intended missing-contract/behavior failure.
- [ ] Implement the frozen contracts, additive collections and worker integration; retain shutdown/rollback behavior and existing collection allowlist checks deliberately.
- [ ] Add the reviewed string-length and outstanding-signal byte-charge boundaries to the storage fixture, including failed jobs and sealed admissions releasing reservations. Keep accounting inside the existing worker, not a second queue.
- [ ] Run Statistics again: all existing and new cases pass. Record actual command/output, changes and contracts; leave uncommitted.

### Task 2: Native authority instead of a plugin prerequisite

**Files:** modify `Patches/{CombatPatches,CookingPatches,ActivityRuntime}.cs`, `Records/CookingTracker.cs`, `Config/{CombatConfig,ObservedLeaderboardsConfig,ActivityConfig}.cs`, `Plugin.cs`; remove `Patches/SimulationEligibility.cs` once all consumers/source links are removed; extend Tracking's existing integration/participation/cooking cases and source links.

**Interfaces:** `CookingTracker.SetAvailability(bool enabled)` replaces the plugin-capability argument; `ObservedLeaderboardsConfig.CaptureSnapshot(ActivityConfig activities, bool combatEnabled, bool explorationGridAvailable, bool dodgeAvoidanceAvailable)` drops `simulationAvailable`. Existing `Snapshot` remains own-flag-only. Task 1's two new optional flags remain false until Task 4 supplies their real configuration bindings—do not introduce temporary keys. Confirmed combat still requires the old combat master plus its own flag; reported credits/consumables and cooking do not.

- [ ] Extend existing cases with `NoSimulationNativeOwnership`: no MVP plugin, enabled confirmed-combat flag and actual eligible server-owned victim → valid credit/damage; client-owned/tamed/player/ineligible victim → none. Add cooking assertions for enabled server-owned accepted insertion/completion with no plugin, nonowned station rejection and disable/re-enable provenance fencing. Preserve historical rows when current capture is unavailable.
- [ ] Run Tracking; confirm the current GUID prerequisite fails these assertions.
- [ ] Remove GUID gates everywhere, including legacy combat eligibility, periodic option refresh, cooking patch checks and coverage explanations. Per-event ownership/identity/provenance checks stay. Keep avoidance unavailable without a validated adapter; do not add one. Remove capability-loss resets tied solely to plugin presence; own-flag/world/actual ownership resets remain.
- [ ] Run Tracking: existing and new cases pass. Grep application source for `MVP.Valheim_Serverside_Simulations`, `SimulationEligibility`, `simulationAvailable` and stale requires-simulation descriptions; no remaining capture dependency. Record result and hand off Tracking files.

### Task 3: Reuse decoded routing for reported credits and consumables

**Files:** extend `Leaderboards/ManualCommandRouting.cs` rather than add another transport decoder; create `Records/NativeSignalCollector.cs`, `Patches/NativeSignalPatches.cs`; modify the existing `ActivityPatches.ManualReceive` scope and minimal `StablePlayerResolver`/`Plugin` wiring; add focused `tests/CombatTracking/NativeSignalTests.cs` and source links; extend existing manual-routing cases in Reporting only after exclusive handoff.

**Interfaces:** add internal `RoutingObservationKind { ManualReport, KillCredit, ConsumableUse }` and `ManualCommandRouting.ObserveSignal(long claimedSender, long actualSender, string session, string world, long message, string targetKey, RoutingObservationKind kind, double monotonic, Action invoke)` returning the existing `Observation?`; this method requires a remote frame and permits only the two signal kinds. Existing `Observe(...text...)` and manual behavior remain remote-only. Typed replay keys include kind/target; manual and signal cache budgets are separate. Add `EnterLocal(long serverUid)` returning a distinctly local frame, and `ObserveLocalSignal(long claimedSender, string epoch, string world, long message, string targetKey, double monotonic, Action invoke)` accepting only local kill credits with the claimed sender equal to that frame's server UID. Local frames mask any outer remote transport; nested remote frames mask local provenance. Local observations never pass through a fabricated remote peer. Extend the same helper's completion/root-unwind semantics for both origins.

- [ ] Add compact `NativeSignalRouting`: sender Alice reporting to target Bob credits Bob; invalid sender/target/character/session rejected; repeated Handle/Route observations and retransmits count once; separate targets/kinds remain separate; skipped original with valid decoded witness counts, enclosing exception does not; overflow, stale scopes and cache pressure do not steal manual capacity. Exercise the real helper/collector with no fake native game universe. Extend manual tests to prove unchanged authorization/cooldown/query counts.
- [ ] Apply the modding-guide safeguards above: pinned native path table, actual hook inventory, bounded branch diagnostics, detached admission cases and explicit mixed-kind overflow/restoration tests. Record missing runtime evidence honestly; do not substitute the old 159 checks.
- [ ] Observe red, then implement the helper extension and thin decoded hooks on `ZRoutedRpc.RouteRPC`/`HandleRoutedRPC`, before NPS's known prefixes. Copy only declared parameters, never reconstruct native envelopes. Existing actual receive scope supplies inbound `ZRpc`.
- [ ] For local kill credits only, tightly filter the four-argument native `InvokeRoutedRPC(long targetPeerID, ZDOID targetZDO, string methodName, object[] parameters)` on the dedicated server, `RPC_RegisterKill` and `ZDOID.None`; establish local context around envelope creation/decoded hooks, unwind exception-safely, and never count both overloads independently. Remote frames must not be mistaken for local origin.
- [ ] Resolve one ready connected nonserver kill target with nonzero full character, `targetPeerID == peer.m_uid == peer.m_characterID.UserID`, unique character/account/socket mappings and current matching player-list character. Reject broadcast target 0, stale/mismatched player history and ambiguity. Remote reporting peer must match actual transport/claimed sender, but need not equal target. Local envelope sender must match the current server UID inside the genuine local scope.
- [ ] For consumables require current authenticated sender's full character as target ZDO and matching peer UID; local animation/render callbacks are not a remote trigger. Consume only exact `eat`; other triggers, no target, stale character and client-claimed server origin yield no record. Revalidate captured connection/world/character before dispatch; immutable target actor for kill credits, sender actor for consumables. Use server UTC receipt time.
- [ ] Wire startup/reset/disposal and fixed coverage/drop summaries into existing Plugin lifecycle. Hooks must return before payload copies when their own flag is off. Keep no-throw observational finalizers, immutable captures and unchanged game forwarding. Native API/source review explicitly checks target/sender binding and cursor preservation; pure tests alone do not establish it.
- [ ] Run focused Tracking and Reporting harnesses for this shared-routing change, record actual results and hand off Reporting. No live server/Discord calls.

### Task 4: Native configuration and honest sixteen-section reports

**Files:** modify `DiscordConnector/Config/ObservedLeaderboardsConfig.cs`, `DiscordConnector/Leaderboards/CombatReports.cs`, minimal `DiscordConnector/Plugin.cs` inclusion/coverage wiring, `tests/CombatReporting/{Program,Stubs}.cs` and `tests/CombatReporting/CombatReporting.csproj` as applicable; no visual redesign or additional delivery path.

**Interfaces:** bind `[Server-Reported Combat] Kill Credits Enabled=false` and `[Server-Observed Actions] Consumable Uses Enabled=false` in existing `nwesterhausen.DiscordConnector.cfg`. Extend `Snapshot`/`CaptureSnapshot` with Task 1's two own flags. Existing keys/values remain untouched.

- [ ] Add one real-store mixed fixture containing all sixteen metrics: reported credit, confirmed participation and distinct death remain different counts/sections; consumables count signals, not food units. Assert exact maximum 1,900 UTF-16 length, qualifiers survive, hostile names cannot mention/format unexpectedly, all nonempty sections represented, positive counts not rendered as zero, default-off flags hide only their own new metrics.
- [ ] Observe red, then add headings `Reported kill credits` and `Observed consumable uses`; include concise qualification that client-originated signals are not verified outcomes. Rebudget globally, reducing optional extra rows before sacrificing a section/qualification. No new boss-credit section, reported creature breakdown or combined kill total.
- [ ] Run Reporting, reusing existing DST/no-backlog/claim/manual/empty/no-mentions cases. Record actual results. Current-period previews and automatic completed-period reports use the same new projections without new claims/timers.

### Task 5: Bounded crafting feasibility gate, not speculative collectors

**Files:** create `docs/research/2026-11-01-crafting-signal-feasibility.md`; update only the relevant audit acceptance status. No production crafting hook, flag, metric or record until this gate passes and an implementation supplement is approved.

**Interfaces:** a per-station evidence table for cauldron and `piece_preptable`: inspected game/asset version and hash, start/done prefab identity, networked component, sharing with other actions, observable source identity and actual signal unit.

- [ ] Inspect available serialized asset references read-only. Record unavailable assets explicitly; generated prefab catalogues and names alone cannot satisfy the gate.
- [ ] Write the bounded operator experiment specified in the design with idle/completed/cancelled/multicraft/two-player/other-station/failure controls, expected distinguishing observations, finite capture duration/record/byte budget and privacy constraints. Any temporary observer must be separately approved, bounded and removable; no full packet dumps or raw identities.
- [ ] If no live experiment is authorized or signal evidence is inadequate, finish this task as `conditional/unavailable`, naming the missing evidence. This does not block Tasks 1–4 or authorize guessed support.
- [ ] If evidence passes, submit a small implementation supplement defining the station-specific signal, attribution, dedup, label and tests for approval before adding code. Never call completion effects successful items or substitute feast placement/output deposits.

### Task 6: Reconcile, verify and deliver a testing artifact

**Files:** final integration in assigned existing files only; update `docs/server-statistics-audit.md`, the approved plan's progress/evidence, and `artifacts/server-leaderboards/{DiscordConnector.dll,SHA256SUMS.txt,LICENSE.LiteDB.txt,provenance.json}` only after verified build.

- [ ] Inspect combined diff and all constructor/option-copy callers, enum-based metric limits, collection allowlists, diagnostic stage/count bounds and config/help text. Confirm no stale simulation dependency, no extra worker/ownership writes/world scan, and no accidental mutation of unrelated existing work. Independently review native trust boundaries and cross-task integration before final checks.
- [ ] Run the four existing harnesses once on reconciled source using the approved patched LiteDB, isolated output directories and commands below. Fix only demonstrated failures and rerun affected checks; do not repeat unaffected suites for ceremony.
- [ ] Perform one isolated Release/net481 build with the established late publish no-ops, empty plugin-copy destinations, separate outputs/intermediates and disabled candidate-assembly fallback. Recheck actual resolved/copied LiteDB hash, merged WAL Flush/ObjectId compatibility and prior manual/emote/logger/shutdown seams. Never invoke repository publication/copy targets unguarded.
- [ ] Verify original packaged outputs, references and installed-file stand-ins unchanged; inspect the actual build log, current artifact checksum and provenance before replacing the local testing bundle. Report warnings and exclusions accurately. Retain old artifact backup; do not deploy it.
- [ ] Document flags, distinct meanings, expected partial coverage, crafting gate status and a small separately authorized operator checklist. No additional load/swap guarantee, native liveness claim or live success claim without measurements.

## Verification commands and ownership

Working directory: your repository checkout. Supply an installed compatible SDK directory, a writable scratch workspace and the explicitly built backport DLL; no ignored local SDK or previous execution workspace is a prerequisite. Each lane runs only its assigned harness. Parent owns the final four-harness pass after writers reconcile.

```sh
: "${DOTNET_ROOT:?Set to your installed SDK directory}"
: "${WORKSPACE:?Set to a writable scratch directory}"
: "${LITEDB_BACKPORT:?Set to the verified backport DLL path}"
export DOTNET_ROOT
export DOTNET_ROLL_FORWARD=Major DOTNET_GENERATE_ASPNET_CERTIFICATE=false
# For each assigned NAME: CombatStatistics, CombatTracking, CombatReporting, ServerStartRegression
"$DOTNET_ROOT/dotnet" run --project "tests/$NAME/$NAME.csproj" --configuration Release --no-restore \
  -p:UseSharedCompilation=false \
  -p:CustomAfterMicrosoftCommonTargets="$PWD/build/litedb-wal-backport/LiteDbBackport.targets" \
  -p:LiteDbBackportPath="$LITEDB_BACKPORT" \
  -p:OutputPath="$WORKSPACE/checks/$NAME/"
git diff --check
```

Expected: every selected harness exits 0 with all its cases passed; copied LiteDB for consuming projects hashes `4785209fa11dcbca8e45ca476a29799e37b270bf99f8bf896acf953c935658cc`; diff check exits 0. Check paths/runtime first; missing references require restoration, not silent use of stale binaries. Native build must use a freshly inspected isolated response file based on the established `review-fixes-native` safeguards—not a generic unguarded `dotnet build`.

## Plan self-review and current evidence

Design coverage: metrics/storage → Task 1; dependency/authority → Task 2; native routing/trust/dedup → Task 3; configuration/reporting → Task 4; both conditional stations → Task 5; integrated checks/rollout limits → Task 6. Shared files/harnesses have sequential handoffs. All new contracts above are internal; no client or external API changes are proposed. No commit steps are authorized.

Implementation authorized November 2 after explicit scope confirmation. The modding-guide review safeguards above address the review's bounds, native coverage, runtime inventory and mixed-scope test gaps. No new execution evidence exists yet; append actual results during execution. Prior 159-case and native-build results belong to the previously delivered build.
