# Observation pipeline redesign

Historical design: its preserve-history policy is superseded only for the three reported-signal detail collections by the approved [November 3 retention design](2026-11-02-reporting-review-fixes-design.md). Other histories remain untouched.

Status: approved by the user November 2, including idempotent diagnostic-counter reconciliation and a separately labelled partial reported dodge-hit counter. Implementation awaits implementation-plan review and execution-method selection. No new DLL, deployment or data maintenance is authorized by this document alone.

## Intent and decisions

Harden server-only DiscordConnector statistics against unnecessary RPC work, traffic monopolization, silent observation loss, patch-order drift and unrelated scheduler failures. Add a separately labelled **reported dodge-hit notifications** counter, not an exact perfect-dodge statistic. Keep vanilla crossplay clients, native ownership, existing history and working manual reports.

The user requested fixes for both critical reviews and explicitly selected a broader redesign rather than isolated patches. After native investigation, the user chose the partial reported dodge counter. The user also explicitly allowed idempotent reconciliation of cumulative diagnostic counters after uncertain writes, without replaying gameplay observations or Discord sends. Quotas, lifecycle containment and measurement choices below are proposed engineering decisions, not previously approved user policy.

## Approaches

1. **Targeted patches:** move several checks and add diagnostics without restructuring admission. Smaller diff, but repeated identity work, inconsistent loss accounting and adapter-test gaps remain easier to reintroduce.
2. **Cohesive observation-pipeline redesign — selected:** separate cheap classification, authenticated admission, bounded staging and immutable persistence while reusing the existing routing scopes, collectors and database worker. Broader coordinated change, with focused native-adapter tests.
3. **Replace networking/storage or require client instrumentation:** potentially richer outcome evidence, but violates vanilla-client constraints and creates unnecessary migration/protocol risk. Rejected.

This is a project-sized change. Implement it in bounded phases, not a second parallel pipeline.

## Invariants and non-goals

- No required client/simulation mod, ownership takeover, enlarged simulation area, inventory manipulation or new client RPC protocol.
- Preserve manual admin authorization, Flex/Toast configuration, shared 60-second cooldown, seven-second grace, calendar/DST boundaries, automatic claims and at-most-once delivery semantics. No automatic replay of uncertain gameplay writes or sends. Only idempotent cumulative diagnostic-counter reconciliation is permitted as specified below.
- Preserve old collection meanings, enum values, public contracts and configuration values. New metric/configuration is additive and default off. Do not reinterpret existing avoidance history.
- Keep one serialized database worker, current global memory/depth/pending bounds and fail-closed native identity checks. No dependency or generalized event framework is needed.
- No history deletion, retention migration, vacuum, WAL deletion, commits, branch changes or deployment. No claim of complete or cheat-proof client-originated statistics.

## Proposed pipeline

### 1. Cheap classification before costly work

Use cached method hashes and effective enabled-feature snapshots. In raw activity ingress, inspect only the minimum bounded envelope header required to decide relevance before copying nested parameter payloads, resolving all peers or reserving replay identity. Retain authentication before accepting observations. Unsupported/disabled traffic must not consume activity replay capacity.

Do not mutate borrowed package cursors: inspect an independently bounded view/copy using verified native APIs. Preserve nested failed-ingress masking and finalizer unwind even when observation is skipped. Keep cooking `RPC_AddItem` transport provenance and independent manual-origin scopes intact. Never build a replacement raw-envelope decoder for the newer decoded signals.

For decoded signals, classify method and flag, enforce exact payload bounds/schema, then resolve identities. Non-eat SetTrigger, oversized, malformed and trailing payloads must be rejected before peer snapshots. Zero-payload dodge notifications require exact exhaustion too.

### 2. Identity resolution and dispatch revalidation

Replace repeated whole-peer scans inside whole-peer snapshots with one bounded traversal/index per observation where feasible. Detect duplicates/ambiguity, never pick an arbitrary matching peer. Cache no identity across connection, world, character or ownership changes without invalidation. Retain fresh dispatch-time checks of authenticated origin, beneficiary, character, world and session.

Remove per-frame allocation of equivalent options, metric arrays and eligibility lists. Cache immutable effective settings and compute coverage only when relevant state changes or a bounded poll is due. Tests should assert avoided operations/allocations structurally; do not claim measured live frame-time gains without measurements.

### 3. Fair admission and accurate loss accounting

Keep separate manual and signal replay budgets. Distinguish duplicate suppression from capacity rejection, expiry, malformed data, quota rejection and enqueue failure. A suppressed duplicate is not a lost new observation.

Add bounded per-connection admission/replay quotas so one authenticated peer cannot occupy the entire global signal budget or all downstream signal batch/byte admission capacity. Reserve/release outstanding-work charges inside the existing worker admission/completion lifecycle; no second queue. Keep manual report capacity independent. Apply quotas before expensive identity expansion where safe. Disconnect invalidates admission but does not evict unexpired replay identities: retain their accounting until expiry, including across connection epochs. Bound quota/tombstone state globally, reject new state when full, and prevent reconnect churn from obtaining unlimited fresh capacity by charging retained entries to stable authenticated account identity as well as the current connection. Do not let identity/quota reclamation turn an already observed envelope into a new observation. Server-local observations use a separately bounded local-origin allowance, not a fabricated peer. The planner chooses documented constants from current global bounds and deterministic burst fixtures; no new tuning keys unless necessary. Legitimate bursts may be dropped and must be reported honestly. Quotas mitigate monopolization, not forged gameplay outcomes or distributed flooding.

Maintain saturating per-kind loss totals through routing, collector and durable coverage aggregation. Coalescing must preserve accumulated counts rather than turn many losses into one. Separate confirmed rejection/drop counts from unknown persistence outcomes; never label an uncertain gameplay write as definitely absent. Persist cumulative diagnostic snapshots under unique world/metric/process-epoch identities with monotonic, idempotent updates. Reconciliation may resend an absolute diagnostic total, never increment it again or replay the containing gameplay batch. Retain unacknowledged snapshots in bounded memory until acknowledged or shutdown; saturation and abrupt-exit loss remain explicitly incomplete, not a durability promise. Reset/flush/reopen aggregation must neither lose acknowledged totals nor count a snapshot twice. Define these counters as observed losses and uncertainties, not an assertion that every network event was seen. This narrow counter-reconciliation exception was explicitly approved by the user.

### 4. Capability and lifecycle isolation

Inspect exact loaded Harmony targets, owners, ordering and unsupported replacements after startup at first usable world/session and on session/world changes. Keep collection unavailable until inspection succeeds; invalidate stale capability during transitions. Do not reflect on every RPC/frame. Arbitrary runtime hot-patching between checks remains unsupported and documented.

Separate world/observation maintenance, coverage, manual polling and automatic scheduling exception boundaries. A manual poll/coverage failure must not permanently dispose automatic reporting. Log the actual failing stage with bounded, redacted diagnostics. Keep explicit scheduler self-disable for invalid calendar configuration; do not convert uncertain dispatch failures into automatic retries.

Make shutdown cleanup exception-safe so one collector failure cannot prevent admission sealing and database drain initiation. Preserve drain-before-close for admitted work. Expose bounded shutdown completion/failure/pending-work evidence, without claiming task completion means all writes succeeded. Never synchronously wait on Unity-dependent continuations. A task does not keep the process alive: abrupt termination and in-memory/unflushed observations can still be lost. A host-supported asynchronous graceful-stop hook may be used only if verified; no invented exit-durability guarantee.

### 5. Reported dodge-hit notifications

Use a new append-only metric/kind and separate additive history, leaving existing roll and experimental attack-side avoidance meanings unchanged. Proposed native key: `[Server-Reported Combat] Dodge Hit Notifications Enabled = false`. The report heading is **Reported dodge-hit notifications**; qualification states partial server visibility, possible multiple notifications per dodge, and no proof of accepted rewards or unique attacks. Retain the overall 1,900 UTF-16-unit message ceiling with all seventeen nonempty sections represented before extra ranks.

Native evidence: supplied Valheim assembly SHA-256 `72f77a0e9c70cf0329e9f1f58687bba5dc9fc77f203bc1826f3a3ca1a7b81312`, inspected as metadata only using the existing Cecil reader. `Player.HitWhileDodging()` invokes `RPC_HitWhileDodging` on its own view with zero payload arguments; the recipient is that target character's ZDO owner. Melee, area and projectile paths notify at dodge-invincibility rejection branches, but may notify repeatedly. The remote handler ignores sender, throttles on `m_beenHitWhileDodging`, and grants effects/stamina/adrenaline/skill. Its acceptance is not visible from a forwarded notification.

The reporting connection is the attack/projectile simulation peer, potentially simulating a creature; it is not necessarily the dodger. Authenticate that connection independently from the target dodger. Require a unique current player character and matching nonzero addressed ZDO owner; reject broadcast/ambiguous/stale ownership. Proposed engineering contract: the owner may be the dodger, a distinct current ready authenticated peer, or the server. Do not reuse kill-credit recipient-equals-beneficiary identity rules. The beneficiary remains the connected player's stable account/full character, while the envelope recipient must equal that character's actual owner. Capture and revalidate reporter and beneficiary sessions, plus a distinct owner's session where applicable; for server ownership validate the current server identity/epoch. Ownership changes invalidate staged observations. Do not infer an attacker identity absent from the payload. Permit only genuine server-local native invocation provenance for local simulation. If native ownership cannot be resolved unambiguously without a local live view, this case is unavailable rather than guessed.

Observe decoded Route/Handle entry before supported NPS prefixes, within successful enclosing origin scope. Envelope dedup removes repeated observer stages/retransmissions, not distinct native notifications. Never invent an attack-ID, time-window unique-dodge heuristic or reward-acceptance inference. Same-client simulation-to-dodger dispatch is invisible to the server and remains uncounted.

Pinned NPS commit `82e7396a007806b878c5db79f39a2ef274d0680d`: [owner classifier](https://github.com/MidnightsFX/Valheim-Network-Performance-System/blob/82e7396a007806b878c5db79f39a2ef274d0680d/NetworkPerformanceSystem/Runtime/RpcOwnerRouter.cs#L612-L631) excludes this method (hash 755978008; no declared classifier collision). [Receive prefix](https://github.com/MidnightsFX/Valheim-Network-Performance-System/blob/82e7396a007806b878c5db79f39a2ef274d0680d/NetworkPerformanceSystem/Patches/RpcOwnerRouterPatches.cs#L63-L70) therefore permits vanilla receive after restoring the cursor. [Relay replacement](https://github.com/MidnightsFX/Valheim-Network-Performance-System/blob/82e7396a007806b878c5db79f39a2ef274d0680d/NetworkPerformanceSystem/Patches/RoutedRpcPatches.cs#L102-L113) may skip original RouteRPC after native-equivalent relay; this proves neither delivery nor reward acceptance. Actual hook inventory remains required; unknown replacements fail unavailable.

### 6. Growth, build reproducibility and reviewability

Document append-only disk/query growth and provide a repeatable disposable-database growth/query check. Do not add automatic retention: pruning can change durable deduplication and historical semantics and requires a separate policy decision.

Replace README's machine-specific SDK quickstart with configurable SDK/workspace inputs; retain explicit opt-in LiteDB backport, source pins, license and testing-only claims. The build script already accepts these parameters. Reuse isolated publication safeguards; do not silently make the patched dependency the default or overwrite packages.

Keep existing root research trackers unless their ownership is established; update authoritative docs instead of deleting potentially user-owned work. Prepare scoped review evidence per implementation phase, including untracked files. Logger support-doc changes belong with the logger behavior they describe. Do not split commits, rewrite history or create branches without authorization.

## Verification and acceptance

The implementation plan must name exact commands, working directories and expected outcomes using an available SDK, not assume the previously broken runtime path. Existing evidence is under `.superpowers/sdd/2026-11-01-lightweight-observed-statistics/`; 179 historical helper/stub checks and the previous DLL are not evidence for this redesign.

- Add focused tests that compile the actual production ActivityPatches, NativeSignalPatches and StablePlayerResolver adapters against minimal native/Harmony test doubles. Do not replace those adapters with test stubs. Assert cursor preservation, disabled-before-copy, schema-before-peer-scan, irrelevant-traffic replay isolation, cooking provenance, origin masking and exception recovery.
- Test duplicate versus full-cache new-message loss, per-peer fairness, disconnect/world replacement, quota-state bounds, cumulative loss flush/reopen, queue failure and no blind replay.
- Test capability unavailable before first usable-session inspection, supported recovery and unsupported next-session patch inventory. Test stage failures independently and ensure manual failures do not dispose automatic scheduling.
- Test shutdown sealing/drain despite earlier cleanup exceptions and explicit unsuccessful writes; distinguish orderly drain from abrupt process exit.
- Test dodge remote/local origins, separate reporter/beneficiary, ownership changes, absent local view, zero payload, duplicates versus distinct notifications, same-client blind spot and skipped-original/exception paths. No test may imply notification equals accepted reward.
- Extend real-store reporting fixtures to seventeen metrics and independently enabled histories; retain escaping/no-mentions, positive counts, calendar/DST/claims, manual cooldown and global length constraints.
- Run focused checks per phase, then reconcile writers and run the full suite once. Perform one guarded native compile/merged-metadata inspection only after source settles if a new testing DLL is requested. Live crossplay/NPS execution, memory/latency and orderly server-stop tests need separate operational authorization.

Success: review findings have explicit fixes or documented non-bug/policy dispositions; unrelated RPCs do no heavy observation work or consume replay capacity; saturation is bounded and visible; failure stages are isolated; the new counter is honest and independent; stored history/manual behavior remain intact. No live-performance, complete-statistics or exit-durability claim follows from unit tests.
