# Valheim modding patterns

## Reporting follow-up (November 3)

Structured report data, not Markdown parsing, now supplies text and embed presentation. Preserve literal escaping, disabled mentions, all coverage qualifiers and the exact text fallback. Temporary named debug output remains behind the existing default-off toggle; TODO: remove or anonymize it after personal presentation testing. Never log account/session identifiers, raw packets or webhook URLs.

The approved [reporting follow-up design](../../superpowers/specs/2026-11-02-reporting-review-fixes-design.md) supersedes earlier preserve-all-history policy **only for the three reported-signal detail collections**: automatic strict 90-UTC-day expiry, permanent opaque deduplication identities, a persisted nondecreasing horizon and bounded transactional maintenance. Tombstones still grow; backlog can delay physical cleanup, and a false forward clock can irreversibly expire detail. No other history/provenance is pruned and no database compaction or WAL deletion is authorized. Historical queries must expose the actual retention horizon, not imply complete expired coverage.

**Reference guide · researched 2026-10-31 · server-only and crossplay first**

Read this before implementing a network observer, replicated-state feature, privileged command, or external integration. This guide answers: **what patterns can we reuse from established Valheim mods, and what assumptions must we verify ourselves?** It is not a proposed patch or a diagnosis of the current Discord dispatch stall.

The examples were selected for relevant, inspectable implementations—not a measured “top mods” ranking. Popular projects contain useful patterns **and** shortcuts. Pinned source links below are evidence; recommendations derived from them are explicitly described as such. A source snapshot is not proof of the operator’s installed binary or console compatibility.

## Start here: ten rules

1. **Choose the observation layer before writing a decoder.** Typed native arguments beat decoded routed fields; decoded fields beat a second handwritten wire parser.
2. **Write down the actual handler signature.** A parameter package is not necessarily a nested `ZPackage` argument.
3. **Observe forwarding when the server is not the recipient.** A local chat handler does not cover every message relayed to other players.
4. **Separate provenance, permission and outcome.** A claimed sender, valid hash, received request and successful gameplay action are four different things.
5. **Treat replicated ZDOs as state, not a lossless event log.** Baseline first observations and reset cursors at session/world boundaries.
6. **Check actual authority per operation.** Installing a simulation mod does not make every player, ship, creature or station server-owned.
7. **Keep Unity-facing work on the main thread.** Background work receives detached values, never borrowed game objects or packages.
8. **Do not synchronously wait for async work from Unity paths—including logging.** A small write returning immediately is not a safety guarantee.
9. **Verify the loaded patch set and runtime.** “Plugin loaded” is not proof that a particular hook ran.
10. **Use the smallest evidence path that tests the claim.** Pure tests, real-reference compilation and live compatibility checks establish different facts.

## 1. Decode at the right layer

### Source-backed examples

- **NPS** inspects `ZRoutedRpc.RoutedRPCData` at `RouteRPC`, including target peer, target ZDO and method hash. Its parser reads a vector directly for one handler, but calls `ReadPackage()` once for a handler whose parameter really is a package. It restores the borrowed cursor in `finally`. This is a concrete reason not to invent a universal “unwrap once” decoder. [NPS routed filter:248–266,331–355][nps-filter]
- **AutoCleanup** observes forwarded native chat, copies parameter bytes and reads `Vector3 → talker type → UserInfo → string`. It does **not** unwrap another routed envelope or package first. Its explanation of per-recipient chat is version-specific evidence, not a universal claim that one hook works forever. [ChatMessagePatch:11–35,89–119][cleanup-chat]
- **ServerSync** registers some messages on `ZRoutedRpc` and others on a connection’s `ZRpc`; these are different layers. Its buffering socket copies delayed packages because the originals are reused. [ConfigSync:245–319,791–805][serversync]
- **Jötunn CustomRPC** adds its own flags, compression, fragmentation and reassembly around payloads. That protocol must not be mistaken for vanilla encoding. [CustomRPC:162–215,306–405][jotunn-rpc]

### Decision table

| What the hook receives | Recommended handling | Avoid |
|---|---|---|
| Native handler arguments | Use the already decoded values | Deserialize them again |
| `RoutedRPCData` | Inspect typed routing fields; decode only `m_parameters` | Parse an outer envelope from the parameter bytes |
| Raw connection package | Prefer moving to a decoded hook; otherwise verify the exact frame | Assume every received package is your message |
| A handler parameter declared `ZPackage` | Unwrap that argument according to its schema | Always/never calling `ReadPackage()` |
| Borrowed package used synchronously | Copy it, or restore its cursor on every exit | Leave vanilla reading from your advanced cursor |
| Data retained for later | Own the bytes and capture immutable provenance | Retain a reused package or ambient connection |

**Recommended decoding contract:** identify method and target first; bound bytes before copying and lengths before allocation; validate enum/numeric values; decode the exact pinned schema; reject unsupported observation without changing normal game routing. A parser exception should not become a dropped vanilla message. A privileged action, however, must fail closed when its own validation fails.

Namespacing reduces accidental name collisions; integer hash collisions remain possible. Hashes do not authenticate senders or prove action success. Jötunn’s `GUID!Name` IDs illustrate namespacing, not security. [CustomRPC:59–72][jotunn-rpc]

## 2. Authenticate the connection, not the display data

AutoCleanup carries the actual incoming `ZRpc` into the decoded observer, matches the peer by reference equality, checks the packet’s claimed UID, then calls native `ZNet.IsAdmin` using the socket identity. `UserInfo` is explicitly not trusted as the actor. ServerSync similarly uses the currently handling connection’s socket identity for privileged config changes. [AutoCleanup listener:165–228][cleanup-listener]; [ServerSync:330–355][serversync]

**Recommended privileged-command boundary:**

```text
observe supported native message
  resolve current connected peer from actual incoming connection
  reject mismatched claimed sender
  validate method, target, arguments and current character when applicable
  derive account/platform identity through verified game APIs
  check native permission
  capture immutable request + world/connection generation
  admit bounded work; recheck generation before a delayed side effect
```

- Names are labels. Profile IDs, network UIDs, character ZDOIDs and platform/account IDs serve different lifetimes; do not silently substitute one for another.
- Nested receive scopes must restore the previous connection, including exception paths. AutoCleanup’s clear-to-null ambient scope is a useful minimal example, **not** a nesting-safe template. [RoutedRpcReceivePatch:6–47][cleanup-receive]
- Do not carry thread-local provenance across asynchronous work. Capture the required identity and generation instead.
- Deduplicate at the semantic boundary you mean. AutoCleanup suppresses only the immediately previous sender/text pair within three seconds; `A, B, A` can bypass that suppression. It is not exactly-once execution. [Listener:87–130][cleanup-listener]
- Keep **received → decoded → authenticated → authorized → admitted → dispatched → completed** distinct. A completed query or enqueued send is not delivery.
- Do not retry an uncertain side effect unless it is idempotent or has a durable acceptance/result key. A timeout can mean the operation already happened.

## 3. Crossplay and server-only compatibility

Jötunn’s compatibility documentation explains that new RPCs or changed formats require compatible handlers on participating endpoints. `NotEnforced` disables a compatibility check; it does not give vanilla clients a new implementation. ServerSync’s optional mod requirement likewise does not make an unmodified client understand its sync protocol. [Jötunn compatibility tutorial][jotunn-compat]; [ServerSync:129–131,1305–1363][serversync]

For unmodified PlayStation/Xbox/PC clients:

- Observe existing vanilla traffic or replicated state; use version-correct existing client handlers for responses.
- Do not introduce a custom client protocol through a convenience library and call it server-only.
- Separate **can join**, **has the handler**, **transport supported**, and **feature verified on console**.
- Verify Steam and PlayFab separately. ServerSync explicitly treats Steam socket casts and PlayFab identity preservation differently. [ConfigSync:810–826][serversync]
- Add a library only when its required functionality is actually needed. Native API use does not require adding Jötunn merely to compute known hashes or read existing ZDO keys.

## 4. Replicated state: convergence versus events

### Useful patterns in real mods

- **SharedExploration** reads live `Minimap` dimensions/radius, samples positions, and monotonically merges one world-union map. It skips table work only when both the table data revision and its own union revision match. [Init/Sample:87–181; table merge:228–285][shared]
- **ServersideQoL** detects emotes by comparing `s_emoteID`, then parsing `s_emote`; AutoStore subscribes to that event and follows native interaction with later state processing. It does not need to patch a remote client’s local `Player.StartEmote`. [PlayerRegistryProcessor:77–91][qol-player]; [AutoStore:219–335][qol-autostore]
- **LetMeSleep** derives a current sleeping population from native character `s_inBed` values and sends vanilla `ShowMessage`. This is state-based server logic, not an emote or chat-command protocol. [LetMeSleep:55–96][sleep]

### Recommended state-observer contract

```text
key = world generation + current connection/character identity
first observation or uncertain continuity: remember baseline; do not trigger
unchanged semantic state: do nothing
changed state: update cursor, including clear/stop values
recognized fresh transition: admit the explicitly supported action
disconnect, replacement, world change or lost provenance: invalidate baseline
```

Choose polling or an accepted-replication hook according to the needed guarantee. Polling can be simpler for current-state features; it can miss start/stop transitions between samples. An ingress hook gives stronger provenance but adds transport, nesting and compatibility obligations. Neither reconstructs intermediate states that never arrived.

For emotes, a changed ID alone is insufficient: native stop also changes the ID and clears the name. First snapshots, resets and numeric jumps are not historical commands. For this project those native facts were checked in the supplied 1.0.21 assembly; the QoL example independently demonstrates the replicated-field approach.

**Shortcuts not to inherit:**

- QoL initializes its emote cursor to zero. That is not a general first-observation baseline for preexisting nonzero state. Its profile/owner indexes are not a complete authenticated account ledger. [PlayerRegistryProcessor:216–238,318–335][qol-player]
- SharedExploration’s world-name filename and world-union bitmap do not preserve individual first-seen dates. Do not turn a shared map or standing-object census into personal daily/weekly activity. Its delete-old-then-move save sequence is not crash-atomic replacement. [Init:114–136; Save:311–332][shared]
- LetMeSleep’s sleeper-count-only cursor can miss a changed ratio when the denominator changes. Deduplicate semantic state, not an arbitrary convenient integer. [LetMeSleep:55–96][sleep]
- QoL manually increases a moving container’s data revision by 120 with an uncertainty comment. Do not adopt that as a general revision protocol. [ContainerRegistryProcessor:254–272][qol-container]

Use immutable captured IDs/values for delayed work. Revalidate authority before a mutation; never assume a retained ZDO/wrapper still represents the earlier observation. Maintain separate ownership and data continuity where the native model does so.

## 5. Ownership and accepted outcomes

MistrCech’s simulation policy takes ownership only under specified conditions and can transfer driven ships to clients. Its ore adapter checks that the queue actually increased; its cooking adapter lacks an equivalent accepted-slot check. These are valuable positive and negative examples. [Core:451–504,733–779][simulation-core]; [ItemLedger:1828–1912][item-ledger]

| Intended claim | Evidence needed |
|---|---|
| Request received | Valid supported native message and provenance |
| Input accepted | Successful execution plus the relevant before/after state transition |
| Processing completed | Observed completion for the same tracked generation |
| Player responsible | Captured, validated provenance retained through that generation |
| Inventory debit/receipt | Actual supported inventory outcome—not merely a world-item change |

Do not equate object presence with ownership, ownership with player intent, or a request with acceptance. The simulation ledger itself describes unseen vanilla bag actions and inferred crafting explanations. Exact eating, crafting, upgrades or spending cannot be promised just because another mod infers possible inventory balances. [ItemLedger:13–56][item-ledger]

For observations, report **observed**, **inferred**, **estimated**, **partial** or **unavailable** honestly. Do not repair missing evidence by silently widening the claim.

## 6. Harmony hooks and lifecycle

HarmonyX source runs prefixes before branching on the combined original-execution flag. A false-returning prefix does not automatically stop every later prefix; exceptions can. Ordering constraints match **Harmony owner IDs**, not necessarily BepInEx GUIDs. Verify the loaded runtime instead of transferring vanilla Harmony assumptions. [HarmonyManipulator:179–240][harmony]; [PatchSorter:107–123][harmony-sort]

NPS can relay a message itself and skip vanilla `RouteRPC`. Thus `__runOriginal == false` is not universal proof that nothing happened. Decide whether a hook measures original execution, observed intent, or effective handling. This source behavior is **not proof** that NPS caused our unresolved chat issue. [RoutedRpcPatches:102–144][nps-patches]

Recommended checklist:

- Resolve exact declaring type, overload, parameter types and return type. Treat a mismatch as a clear capability failure.
- Inspect `Harmony.GetPatchInfo(exactTarget)` for owners, kinds and priorities after relevant initialization; startup “loaded” alone is insufficient. Do not invoke patch factories or gameplay methods to inventory them.
- Keep passive observers non-mutating and exception-contained. Preserve original exceptions; a finalizer returning null can change gameplay behavior. [Harmony finalizer tests][harmony-finalizers]
- Declare real dependencies instead of relying on plugin load order. Jötunn documents premature manager access; ValheimPlus defers conflict discovery until `Start`. [Jötunn Main:43–100][jotunn-main]; [ValheimPlus:133–136][plus-main]
- Separate plugin lifetime from network-session lifetime. Unsubscribe exact delegates, stop admissions, fence stale completions and dispose owned resources in that order. Jötunn demonstrates network-session unsubscribe/reset. [SynchronizationManager:731–749][jotunn-sync]
- Avoid broad highest-priority hooks, blanket original suppression, synthetic players or invasive transport rewrites when an existing narrow native observation suffices.

## 7. Main-thread safety, async I/O and logging

Unity’s reference synchronization context queues posted work for the main thread and can block an off-thread `Send` until execution. BepInEx provides a dispatcher, but its callback list is not a bounded application queue. These reference snapshots are not proof of exact deployed binary behavior. [UnitySynchronizationContext:33–113][unity-context]; [ThreadingHelper:43–167][threading]

**Recommended boundary:** main thread validates/captures detached values → bounded I/O work → observed completion → only necessary game-facing work posted back. Never pass Unity objects, `ZNetPeer`, borrowed `ZPackage`, lazy enumeration of live state or mutable configuration into worker work.

- Never block Unity with `.Result`, `.Wait()` or `GetAwaiter().GetResult()` on potentially context-dependent tasks. ValheimPlus’s blocking startup HTTP is a cautionary example, not a pattern to copy. [HttpHelper:29–40][plus-http]
- Coroutines spread work across frames; they do not make blocking disk/network I/O asynchronous or guarantee a hard frame budget.
- Async methods execute synchronously until an incomplete await. Observe tasks and protect invocation/argument-evaluation boundaries; avoid unowned `async void` failures.
- Bound queued **bytes**, concurrency and per-frame work as well as item counts. Define overflow behavior and expiry. A queue plus an ACK flag is not a complete recovery protocol. [ValheimPlus RPCQueue:13–43][plus-queue]
- Prefer the existing plugin log source over a custom asynchronous file subsystem. Logging must not block gameplay, invoke arbitrary sinks under state locks, recurse on failure, or expose webhook URLs, tokens, raw payloads or config values.
- Log bounded state transitions, fixed rejection reasons, duration, queue pressure and suppression counts. A generic traffic counter does not prove a command-specific hook ran.
- Never synchronously drain work on Unity if its completion requires Unity. Shutdown must have a bounded, explicit cancellation/drain policy.

**Historical local caution (October 31):** the then-current logger had synchronous wrappers around captured async file writes. A disposable test reproduced blocking at 256 KiB/1 MiB, but **not** at 25/1,900 characters, on .NET 10/macOS rather than Linux Mono. The subsequently approved review fixes removed that custom file subsystem and synchronous waits in favor of BepInEx logging. The experiment established a real mechanism, not the proven cause of that live dispatch stall.

## 8. Verification before asking someone to deploy

| Evidence | Establishes | Does not establish |
|---|---|---|
| Small pure tests | Parser/state/permission policy, dedup, bounds and failure semantics | Real native hooks or transport delivery |
| Metadata inspection | Actual signatures, fields and method-body assumptions | Runtime patch installation/execution |
| Real-reference compile | API and target-framework compatibility at compile time | Mono behavior or game-loop liveness |
| Actual Harmony/runtime compatibility fixture | The tested ordering/skip/exception interaction | Full mod-stack game operation |
| Authorized minimal live check | The observed end-to-end scenario | Every player, failure mode or long-term reliability |

Pin game/build, Unity, BepInEx, actual loaded Harmony, relevant mods and reference hashes. Keep original and publicized references separate. Confirm build targets cannot deploy or overwrite packages: established mod projects contain automatic copy/publication hooks too. [Jötunn project:96–134][jotunn-project]

Ship an artifact digest with a trusted manifest stating source/toolchain/references/dependencies and actual checks. A checksum is not a signature. Never label old packages or a compile-only artifact as the newly tested build. Preserve license notices when distributing copied/substantial third-party material; conceptual patterns do not require transplanting implementations.

### Before the next networking feature

- [ ] State the user-visible outcome and permitted server/client installation model.
- [ ] Select the narrow native layer and write down its pinned signature/schema.
- [ ] Identify current transport/account/character provenance and authority.
- [ ] Define baseline, world/session reset, duplicate, unknown-outcome and overflow behavior.
- [ ] Check loaded patch owners and the actual transport, not just compilation references.
- [ ] Keep game access main-thread-only; make all I/O and logging nonblocking at that boundary.
- [ ] Name the first observable stage and each bounded rejection reason before live testing.
- [ ] Run the smallest logic check, real-reference compilation and separately authorized live check needed.
- [ ] Record what remains unverified; do not ask operators to repeat unchanged tests without a new discriminator.

## Application to this repository

Relevant entry points: `Patches/ActivityPatches.cs`, `Patches/StablePlayerResolver.cs`, `Leaderboards/ManualCommandRouting.cs`, `Leaderboards/EmoteReportTriggers.cs`, `Leaderboards/ManualLeaderboardReports.cs`, `DiscordApi.cs`, and `DiscordConnector.Common/Common/Logger.cs`.

**Historical incident (October 31):** one attempt reached emote observation, native authorization, admission and completed database query, then stopped after `DispatchInvoking`. That excluded earlier trigger/permission gates for that attempt, not every attempt, and did not prove HTTP began. After separately approved fixes, the operator demonstrated successful daily and weekly manual Discord delivery on November 1. Preserve current trigger settings rather than treating this historical warning as an instruction to disable or enable them. The earlier shout failure remains unexplained; successful emote delivery does not justify another transport fallback or establish all native paths.

Research notes: [lifecycle/threading](../research_notes/Valheim%20modding%20patterns/lifecycle.md), [networking](../research_notes/Valheim%20modding%20patterns/networking.md), [replicated state](../research_notes/Valheim%20modding%20patterns/replicated-state.md). Source examples are pinned below; recheck them and target binaries when APIs or mod versions change.

### November 2 application: bounded observations, not complete outcomes

The approved [observation design](../../superpowers/specs/2026-11-02-observation-pipeline-redesign-design.md) and [operator audit](../../server-statistics-audit.md) distinguish current source from the historical testing DLL. No new DLL was built for this redesign, and old 179-check/native-build evidence does not validate it. Source pins above remain historical evidence; no new native investigation/runtime attestation is implied.

- **Classify before copying/scanning; mask every nested origin.** Disabled/unrelated RPCs cannot inherit an outer sender or spend observation replay/quota. Exact payload exhaustion and authenticated transport remain trust boundaries. Reporter, beneficiary and actual owner are distinct identities, revalidated before dispatch; a missing local ownership witness is unavailable, not guessed.
- **Bound fairness as well as global work.** Current signal admission is **128/account and connection per rolling 60 seconds**; independent raw activity admission is **128/seven seconds**. Both policies retain at most 128 remote pairs plus a separate local allowance, with at most 16,512 retained charges and max-32 expiry work per admission. Reconnects/temporary world outages do not erase unexpired tombstones. Manual replay is 256/seven seconds; signal/raw replay caches are independently 4,096/60 and 4,096/seven seconds. Shared depth/pending limits are 16; staged signals expire after five seconds. The worker reserves **16 outstanding signal batches/64 KiB per account and connection**, a separate local allowance, at most 128 indexed remote origins, 4 KiB/signal, global 8 MiB and 128 physical documents/batch. One of the existing 256 batch slots is diagnostic-only (255 gameplay); two report slots remain independent. Many cooperating accounts and legitimate bursts can still exhaust these bounds.
- **Retry only absolute diagnostics.** Confirmed rejections and uncertain gameplay writes are not interchangeable. Failed batches can partially commit; uncertainty counts affected metric-captures, possibly already stored/duplicates, not missing rows. At most 64 world/metric keys, one acknowledgment in flight and ten-second normal submission spacing bound the cumulative owner. World/metric/process-epoch documents merge each field by maximum; older/repeated snapshots cannot double totals or clear newer dirty state. Only these absolute snapshots reconcile—never gameplay or sends. `KnownLosses`/`Rejections`/`UncertainWrites` are **world-lifetime**, not selected-report-period counts. Saturation, excess keys, unreconciled values and abrupt exit remain incomplete. Epoch documents/history grow append-only.
- **Inventory per usable session, not frame.** Exact targets and own hooks/kinds/priorities/order plus supported foreign patch owners are inspected at first usable session and real world/session changes. Until then capability is unavailable. Same numeric world UID with replaced runtime identity is a new session; temporary unavailability suspends capture without minting quota. Unknown replacements fail unavailable; arbitrary between-check hot patching is unsupported. Inventory proves installed hooks, not execution or reward acceptance.
- **Isolate maintenance/polling/cleanup failures.** A manual or coverage exception cannot disable unrelated automatic work. Shutdown independently contains cleanup, attempts final diagnostics and seals admissions/starts one owned background drain even when earlier cleanup throws; accepted work/readers precede close. Unity does not wait. Completion is not all writes succeeded; fixed status includes pending work, known failures, dirty totals and incompleteness. A Task cannot keep a process alive; forced exit/unflushed state and a stuck worker remain limits, not a verified asynchronous host-stop guarantee.
- **Label partial dodge notifications separately.** Own flag `[Server-Reported Combat] Dodge Hit Notifications Enabled = false` and separate `reported_dodge_hit_notifications_v1` history do not inherit roll/avoidance/combat/kill/eat flags. `RPC_HitWhileDodging` is a zero-payload notification: partial server visibility, potentially **multiple notifications per dodge**, **no proof of accepted rewards or unique attacks**, not perfect dodges or a complete statistic. Same-client dispatch is invisible; envelope dedup is not semantic attack/dodge dedup. Reported kill credits still are not deaths/final hits; eat presentation (food/potions/other) still is not inventory debit/food units. The single ≤1,900 UTF-16-unit report reserves one row for all seventeen enabled nonempty metrics before extras, preserves literal escaping/no mentions and labels diagnostics world-lifetime rather than period losses.

The optional [disposable growth check](../../../tests/CombatStatistics/GrowthCheck.cs) inserts 1,000/10,000 unique signal rows for ten accounts, repeats inserts, checks indexes and exact half-open day/account counts, and outputs checkpointed DB bytes plus insert/duplicate/query elapsed time. It creates/removes only its own new temporary fixture, never an existing server DB. It is local append-only growth evidence, **not a universal speed promise** or permission for retention pruning. See the audit for the explicit `--growth-check` command and [portable SDK/workspace recipe](../../../build/litedb-wal-backport/README.md). Retain pinned source/hash/license and explicit opt-in testing-only backport selection.

### Document verification

Historical research verification (October 31): all four Markdown files passed local-link, reference-definition, whitespace and conflict-marker checks. All 43 distinct pinned source URLs resolved, with cited line ranges within the returned files. A separate networking accuracy review found no material corrections after the hash-collision wording was qualified. URL/range checks are not semantic verification; other source claims rest on the three scoped research reviews and coordinator source inspection. That documentation-only work ran no application tests/builds/live checks. November 2 checks are recorded in Task 6's source-only report; pinned external URLs were not fetched again.

## Pinned sources

[nps-filter]: https://github.com/MidnightsFX/Valheim-Network-Performance-System/blob/82e7396a007806b878c5db79f39a2ef274d0680d/NetworkPerformanceSystem/Runtime/RoutedRpcFilter.cs#L19-L355
[nps-patches]: https://github.com/MidnightsFX/Valheim-Network-Performance-System/blob/82e7396a007806b878c5db79f39a2ef274d0680d/NetworkPerformanceSystem/Patches/RoutedRpcPatches.cs#L30-L144
[cleanup-chat]: https://github.com/MacroMaster101/valheim-auto-cleanup/blob/3d37a992b2161249c1e0cbb71039dcdd601013d6/src/ValheimAutoCleanup/Patches/ChatMessagePatch.cs#L11-L119
[cleanup-listener]: https://github.com/MacroMaster101/valheim-auto-cleanup/blob/3d37a992b2161249c1e0cbb71039dcdd601013d6/src/ValheimAutoCleanup/Server/ChatCommandListener.cs#L87-L228
[cleanup-receive]: https://github.com/MacroMaster101/valheim-auto-cleanup/blob/3d37a992b2161249c1e0cbb71039dcdd601013d6/src/ValheimAutoCleanup/Patches/RoutedRpcReceivePatch.cs#L6-L47
[serversync]: https://github.com/blaxxun-boop/ServerSync/blob/c57c2aa54e07cdcc7630d6068699ea781622323e/ConfigSync.cs
[jotunn-rpc]: https://github.com/Valheim-Modding/Jotunn/blob/f4bb47121f1685712b2bcf1279b61fe6de996074/JotunnLib/Entities/CustomRPC.cs#L59-L405
[jotunn-compat]: https://github.com/Valheim-Modding/Jotunn/blob/f4bb47121f1685712b2bcf1279b61fe6de996074/JotunnLib/Documentation/tutorials/networkcompatibility.md
[shared]: https://github.com/Shuiei/SharedExploration/blob/5b60b308adaed07ca3b3a5680605a32394867fd0/SharedExplorationPlugin.cs#L87-L332
[qol-player]: https://github.com/ArgusMagnus/ValheimServersideQoL/blob/50a2d79d10d8f52c59a18f4ca854667464c9ca9b/ServersideQoL/Processors/PlayerRegistryProcessor.cs#L14-L335
[qol-autostore]: https://github.com/ArgusMagnus/ValheimServersideQoL/blob/50a2d79d10d8f52c59a18f4ca854667464c9ca9b/ServersideQoL.AutoStore/ContainerProcessor.cs#L30-L335
[qol-container]: https://github.com/ArgusMagnus/ValheimServersideQoL/blob/50a2d79d10d8f52c59a18f4ca854667464c9ca9b/ServersideQoL/Processors/ContainerRegistryProcessor.cs#L254-L272
[sleep]: https://github.com/blockchaaain/LetMeSleep/blob/e0268f3f6932cbc1e5739a5f08983917ca07340f/LetMeSleep/LetMeSleep.cs#L43-L96
[simulation-core]: https://github.com/MistrCech/valheim-serverside/blob/b2deeb7ebf1efdac2a09394da1984f08c7da5469/src/Valheim_Serverside/Features/Core.cs#L451-L779
[item-ledger]: https://github.com/MistrCech/valheim-serverside/blob/b2deeb7ebf1efdac2a09394da1984f08c7da5469/src/Valheim_Serverside/Features/ItemLedger.cs
[harmony]: https://github.com/BepInEx/HarmonyX/blob/217cc408cc3909ee67c727a2d782bede84f6c2b1/Harmony/Public/Patching/HarmonyManipulator.cs#L179-L240
[harmony-sort]: https://github.com/BepInEx/HarmonyX/blob/217cc408cc3909ee67c727a2d782bede84f6c2b1/Harmony/Internal/PatchSorter.cs#L107-L123
[harmony-finalizers]: https://github.com/BepInEx/HarmonyX/blob/217cc408cc3909ee67c727a2d782bede84f6c2b1/HarmonyTests/Patching/FinalizerPatches.cs#L109-L247
[jotunn-main]: https://github.com/Valheim-Modding/Jotunn/blob/41cd418939707f9ae925e8c2927c195c1af0c906/JotunnLib/Main.cs#L43-L100
[jotunn-sync]: https://github.com/Valheim-Modding/Jotunn/blob/41cd418939707f9ae925e8c2927c195c1af0c906/JotunnLib/Managers/SynchronizationManager.cs#L731-L749
[plus-main]: https://github.com/Grantapher/ValheimPlus/blob/5c4470ac4cd0b914e2864a5fbcea164231b6f9a8/ValheimPlus/ValheimPlus.cs#L133-L136
[plus-http]: https://github.com/Grantapher/ValheimPlus/blob/5c4470ac4cd0b914e2864a5fbcea164231b6f9a8/ValheimPlus/Utility/HttpHelper.cs#L29-L40
[plus-queue]: https://github.com/Grantapher/ValheimPlus/blob/5c4470ac4cd0b914e2864a5fbcea164231b6f9a8/ValheimPlus/Utility/RPCQueue.cs#L13-L43
[unity-context]: https://github.com/Unity-Technologies/UnityCsReference/blob/bf6028387a4fc2ea6d41aa7d8998e774655aedf6/Runtime/Export/Scripting/UnitySynchronizationContext.cs#L33-L113
[threading]: https://github.com/BepInEx/BepInEx/blob/eda9ae89dd2c8bc9c7824d3658c13bcff4e5336e/BepInEx/ThreadingHelper.cs#L43-L167
[jotunn-project]: https://github.com/Valheim-Modding/Jotunn/blob/41cd418939707f9ae925e8c2927c195c1af0c906/JotunnLib/JotunnLib.csproj#L96-L134
