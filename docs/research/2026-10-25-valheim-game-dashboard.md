# Exact-project research: valheim-game-dashboard

**2026-10-26 addendum:** [consolidated feasibility and requested food rankings](2026-10-26-activity-feasibility-and-food.md) supersedes this note's open creator-normalization question: the current game-generated typed player-list bridge is statically resolved, while uniqueness/live compatibility and headless grid remain checks. Food collection/companion is not approved; implementation remains paused. Earlier validation below applies to the earlier artifacts, not the new addendum.

## Question, scope and disposition

**Question:** What does `https://github.com/MrGuato/valheim-game-dashboard/` actually collect, and which mechanisms improve our paused server-activity design without changing its metric definitions or infrastructure?
**Date:** 2026-10-25. **Source pin:** `28871399317c09b32a83c5c2028254c38c5b4487` (`main`), branded **Longhouse**. This is the exact requested repository, not the similarly named server repository.
**Current status: PAUSED pending review.** Earlier approval of sampled moving-helm time, labeled inferred pickups, estimated recipe materials and observed exploration remains the definition baseline. The user's later “wait” and research request do not renew implementation authority or approve optional features.

**Recommendation:** retain the connector's C#/LiteDB/calendar architecture. Borrow profile-ZDO linkage and registry-baseline knowledge, but correct the plan's routed-request observation and indexed creator-platform corroboration. Do not transplant name-keyed identities, removal tallies, standing-count deltas, dashboard infrastructure or mock gameplay statistics.
The reference implements none of our four primary metric contracts; it offers useful mechanisms and counterexamples, not a ready-made activity collector. Evidence for its actual plugin surface is [player sampling][player], [removal tally][tally] and [creator census][census]; the live snapshot surface is [snapshot output][output].

**License:** MIT, copyright 2026 Jonathan; retain copyright/permission notice if future copies contain substantial source portions. No substantial implementation code is copied here. Neither license review nor source availability permits redistribution of Valheim assemblies; the reference itself excludes those references from redistribution. [License][license] · [build guidance][mod-build]

## Approach and evidence limits

Read the existing authorized clone at `/private/var/folders/fy/095_frw57s99z6m359s4bxy40000gn/T/opencode/valheim-game-dashboard-research`; its HEAD ref identifies the pin above. The clone was obtained for the requested research, not as an implementation dependency.
Inspected complete `mods/Longhouse.Server/ServerPlugin.cs`, plugin/build metadata and documentation; `server/{index,state,snapshot,logparse,logsource,world,a2s,config,mods}.js`; both test files; relevant browser rendering/load paths and mock data; deployment composition; README and license. Citations below resolve to the pinned GitHub SHA, not temporary local filenames.
Classify claims as **source fact**, **transfer decision/inference**, or **unknown**. Static inspection is not a running-server, Harmony, crossplay, Discord, performance or persistence-crash experiment. No downloaded source was executed; no test/build/deployment commands were run.

For current-game mechanisms, the existing official **1.0.20 / build 25909431** `assembly_valheim.dll` was inspected with existing `ilspycmd --disable-updatecheck`, not by running the game. Binary symbols cited below identify that build, not the user's installed server or headless scene availability.
Binary location: `/private/var/folders/fy/095_frw57s99z6m359s4bxy40000gn/T/opencode/discordconnector-build/official-server/valheim_server_Data/Managed/assembly_valheim.dll`.
The [revised design](../superpowers/specs/2026-10-25-server-activity-statistics-design.md) names G1–G7; the [paused plan](../superpowers/plans/2026-10-25-server-activity-statistics.md) keeps five tasks and the existing frozen APIs/budgets.

## Current end-to-end flow — source-established, not runtime-verified

1. Vanilla game/container logs provide connection/character registration, inferred player deaths, raid starts, save notifications, versions and selected clock readings. Regex parsing normalizes these into collector events; these are not an authoritative inventory/action stream. [Parser][parser]
2. Optional **server-only BepInEx plugin** polls connected peers, emits `hello`, `player`, `tally`, `pieces` as `LH1 {json}` via `ZLog`, and subscribes to ZDO destruction. It adds no client companion or custom network port. [Plugin lifecycle][plugin-flow] · [emitter][emitter]
3. Node follows Docker logs through the supplied proxy or tails a file. Docker timestamps are normalized; reconnect replays a bounded tail. Plain-file timestamps use collector-local time; missing stamps fall back to receipt time. [Log transport][logs] · [file mode][file-logs]
4. Node also polls Steam **A2S_INFO** over UDP and container state; a zero-player response closes inferred open sessions. It attempts world-save reads separately. These inputs do not reveal individual pickup/placement/crafting transactions. [Collector][collector] · [A2S][a2s]
5. Parsed events mutate one `State`: players, sessions/deaths, plugin status and cumulative tallies. Dirty state is saved as JSON using temporary write + rename, normally every 30 seconds; detail retention is 35 days for sessions/deaths. [State][state-save] · [pruning][prune] · [save timer][collector]
6. `/api/snapshot.json` builds a **current view** from state, A2S/container status and world-clock/key heuristics. Browser fetches that view and renders supported fields. No raw accepted-action ledger is exposed. [HTTP][api] · [snapshot][output]
7. Initial API failure/static hosting selects mock data and explicitly shows **“Preview · mock data”**; no live refresh loop starts then. After initial live success, later failed fetches skip rendering, retaining the old live view/badge; its age display continues increasing. Do not describe this as mock silently rendered as live. [Load/fallback][fallback] · [age display][age]

Text equivalent: game logs + optional plugin `LH1` → log follower/parser → Node mutable state/JSON; A2S/container/world-save observations → snapshot builder → HTTP JSON → browser. This describes the reference, **not proposed connector components**. No diagram rendering was attempted.
Source packaging declares Node >=20 with no npm dependencies; plugin references net48/BepInEx/game assemblies. These are reference facts, not reasons to add Node, Docker-socket access, UI, A2S or another service to our net481 connector. [Package][package] · [plugin props][props] · [composition][compose]

## Actual metrics versus advertised/demo fields

| Surface | Actual source and meaning | Boundary / transfer verdict |
|---|---|---|
| Online count, ping, uptime, version | A2S/container/log-derived server status. [Collector][collector] · [server snapshot][server-view] | Useful operational status, not personal activity evidence; no new infrastructure here. |
| Sessions, playtime, longest session | Name-keyed spawn/disconnect/start handling, interval duration and open-session extension. [State sessions][sessions] · [profile snapshot][profiles] | Server-observed/inferred attendance, not verified personal lifetime. Existing legacy boards remain untouched. |
| Player deaths | Character registration `0:0` parsed as death, incremented by name. [Parser][parser] · [sessions][sessions] | Not killer/cause/combat attribution. |
| Raids and saves | Log event/feed and last-save clock. [Parser][parser] · [state events][state-events] | Raid start is not result, participants or damage. |
| Day/time and next save | Log clock preferred; otherwise old-format save heuristics/extrapolation; configured save interval. [Server view][server-view] · [world reader][world] | Not authoritative parsing of current saves or player exploration. |
| Boss progression | Global-key/manual flags; first scan avoids announcing existing state; later observation may attach current game day; participants always empty. [Boss view][bosses] | Not historic kill date, repeated kills or participant credit. |
| Rolling weekly comparison | **Only playtime, sessions, deaths, unique players** across rolling 7-day windows. [Weekly implementation][week] | No weekly kills or built totals, despite README claim/demo fields. [README][claims] · [mock][mock-core] |
| Biome/interior/sleep/health | Connected character ZDO position/status; WorldGenerator biome, high-Y interior heuristic, replicated health/in-bed. [Sampling][player] | Latest observed context, not explored cells or completed actions. |
| Appearance/visible gear | Replicated hashes/selected quality fields converted to display data. [Sampling][player] · [normalization][normalization] | Not inventory, food buffs, equipment-acquisition or upgrade transactions. |
| Creature/boss counts | Each connected attacker-name marker can receive credit on creature ZDO removal. [Tally][tally] | Cooperative removal tally, not final hitter/confirmed death; cannot replace existing combat. |
| “Creatures slain” chart | Sums those per-player cooperative tallies; name→typical-biome lookup. [Chart aggregation][mobs] · [biome lookup][normalization] | A multi-attacker removal can contribute more than one count; chart biome is not death-location biome. |
| Pieces standing | Counts creator-bearing registry ZDOs for cached builders; live output is `piecesStanding`. [Census][census] · [profiles][profiles] | No `Piece` component filter, no placement timestamp, materials or spending. |
| Skills/food/crafting/distance/gathered materials | UI can render fields supplied by mock data; live snapshot does not produce them. [UI fields][ui-fields] · [mock profile][mock-core] · [mock materials/food][mock-extra] · [output][output] | Visual capability/demo content is not collection feasibility. README itself excludes real-player skills/food/lifetime totals. [README limits][readme-limits] |
| Upgrade/build/kill feed details | Mock feed includes upgraded equipment and placement/participant stories absent from live event producers. [Mock feed][mock-extra] · [state events][state-events] | No evidence for successful upgrades, exact constructions, death causes or historic participants. |

## Attribution, worlds and persistence: important counterexamples

**Profile ID is not account identity.** Plugin resolves `peer.m_characterID` → character ZDO → `ZDOVars.s_playerID`, then caches the first display name for that number. It does not demonstrate normalization to our stable socket account IDs. [Player linkage][player]
Node's primary player map is keyed by name; FIFO pending hashed connection IDs are paired to subsequent names. Browser player IDs are hashes of display names. Same-name accounts, renames or reordered connections are therefore not contracts we can borrow. Hashing an ID does not validate its pairing. [Name map/save][state-save] · [FIFO sessions][sessions] · [profile IDs][profiles]

**World label is not world-scoped history.** Node's start handler sets a global world label and closes sessions but retains players/cumulative tallies and boss observation state. The plugin clears selected dictionaries on a new `ZNet` instance, not `_builderNames`; first-name creator mappings can persist. [State start][sessions] · [plugin reset][plugin-flow]
Our collector must key world/account/object explicitly and clear world-sensitive identity/baseline/correlation caches; a current-world label cannot retroactively scope prior records.

**Log receipt/replay is not event deduplication.** `LH1` JSON carries no stable account/world/event ID, source-action timestamp or receipt acknowledgment; an enclosing log timestamp is not each tallied action's time. Tallies are cleared after logging, not after a collector acknowledgment. [Emitter/tallies][emitter] · [tally flush][tally]
Node skips older timestamps and remembers count/order at the last timestamp, resuming a bounded tail (default 300,000 lines). This is an order-based replay heuristic, not a unique per-action transactional insert. Unrecoverable truncation, changed ordering or unsaved state can lose/reapply data; no runtime failure experiment was conducted. [Replay][replay] · [tail config][config] · [logs][logs]
JSON temporary rename limits partially replaced files, but no cross-record transaction, unique mutation key or atomic report claim appears in that persistence path. We keep LiteDB transactions/durable keys, UTC ticks and the existing report-claim collection. [JSON persistence][state-save]

**Latest state is not a period event.** Live status expires after ten minutes; appearance/gear persist after logout. Creator census overwrites standing counts; tally lines add cumulative counts with no per-action event times. [Plugin state application][mod-state] · [profile freshness][profiles]
Those representations cannot establish which accepted action belongs in a Dublin calendar day/week. Retain immutable observation timestamps and world/account/freshness at capture, clipped sailing intervals and first-cell timestamps; never derive placements or spending by subtracting two status snapshots.
Reference weekly windows are rolling UTC-millisecond durations; heatmap bins use timezone formatting. They are not our completed local-calendar periods/DST/at-most-once delivery scheduler. [Week][week] · [heatmap][heatmap]

**World save parsing is heuristic.** Reader interprets initial bytes as version/net time and scans raw file bytes for boss-key strings; deployment comments specifically describe the mount for pre-1.0 worlds. Current-version compatibility is not established by broad version sanity checks or README's tested-version claim. [Reader][world] · [mount comment][compose] · [README requirements][requirements]
Prefer known in-process game symbols for future approved state queries; do not import this parser, a second database or a world-file dependency.

## Four primary metrics: concrete transfer and remaining gaps

### 1. Sampled moving-helm seconds
Reference has no ship/control sampling or sailing-time producer: its actual peer payload is position-derived context, appearance/gear/health/sleep. A mock “Sailing the longship” label is narrative data, not evidence. [Payload][player] · [mock profile][mock-core]
Keep G1 official-build `ShipControlls.GetUser/HaveValidUser`, onboard validity and fresh ship position observations, stable actor mapping, 1-second cadence, 0.5 m/s horizontal threshold and 2.5-second gap reset.
Change-only output can reduce redundant storage **after** accepted continuity checks; it cannot make stale endpoints fresh or justify copying the reference's 30-second sampler/300-second heartbeat. [Cadence][plugin-flow] · [heartbeat][heartbeat]
Unknown: installed ownership/update cadence and mod/Harmony behavior. No live sampling claim follows from compilation or this audit.

### 2. Labeled inferred floor pickups
Reference has no pickup ownership-request, stack-payload, provenance or merge/despawn correlation producer. Its gathered-material chart is mock/UI support. [Plugin surface][plugin-flow] · [mock material quantities][mock-extra] · [UI gathered label][ui-world]
**Required plan correction, official-build G7:** server `ZRoutedRpc.RPC_RoutedRPC(ZRpc,ZPackage)` can see a routed request even when the target item is client-owned and only forwarded. `HandleRoutedRPC`/owner-local `ItemDrop.RPC_RequestOwn` alone miss that traffic.
Parse a private copy of `RoutedRPCData` (message ID, embedded sender/target peers, target ZDO, method hash, parameters); observer parsing preserves original bytes/cursors, while normal game execution may consume the package. Match actual ingress `ZRpc` to one connected ready peer before comparing direct-client embedded sender. The inspected official method has no explicit sender-vs-connection comparison; never assert embedded IDs are authenticated or normalize unsupported relays by guesswork.
Finalize only successful-original telemetry; restore nested ingress state; dedup local/broadcast/forwarded observations. Forwarded request alone counts0; request/grant plus accepted reduction/deletion may mature as **inferred** under approved exclusions. It does not prove inventory acceptance.
Keep game `ItemDrop.LoadFromZDO(ItemData,ZDO,int)` payload decoding, 5s request/2s exclusion hold, mutation dedup and pre-pool snapshots. Known merge/despawn/consumption/conflicting actor cases are rejected; unchanged/full-inventory ownership handoff counts0.
Residual unseen-cause/late-packet misclassification remains disclosed. Fresh source requires actual lineage proof; unknown/redropped/refunded items never become fresh. Do not reintroduce an impossible exact remote inventory witness or silently drop useful approved inference.

### 3. Newly created pieces and estimated recipe materials
Borrow registry access for **startup baseline only**, not the periodic census. Reference counts all known creator-bearing ZDOs, so construction candidates need explicit `ZNetScene.GetPrefab(...).GetComponent<Piece>()` filtering and new lifecycle evidence. [Census][census]
Official-build G4/G7: `Piece.SetCreator(long,PlatformUserID)` stores profile creator plus `s_creatorIndex`, which indexes `ZNet.World.m_playerHistory`; entry `ZNet.CrossNetworkUserInfo.m_id` is `PlatformUserID`, not a raw creator string.
Validate index bounds, missing/conflicting history and duplicate profiles; establish a verified bridge from that platform representation to the existing connected socket account. Do not invent hostname/platform prefixes or quietly remove platform corroboration. An unverifiable useful bridge is a concrete rejection/stop condition; combat's existing identity rules do not change.
Preserve delayed fields, baseline/new/replay dedup, free-build exclusions, growth/refund handling and standard recipe labeling. Standing totals/quality/equipment do not prove actual material debit; no census delta becomes a placement.

### 4. Durable server-observed exploration
Reference's `WorldGenerator.GetBiome(position)` gives sampled context, not a minimap grid, cell reveal or per-account history. It supplies no exploration tracker or headless active-grid proof. [Biome lookup][player]
Keep official G5 runtime `Minimap` texture size/pixel size/radius, game rounding/bounded integer circle, 2-second samples and durable world/account/32×32 first-timestamp chunks. No global shared union, map defaults, path interpolation or demo distance conversion.
**Unknown remains material:** headless active runtime parameters. Detect missing/mismatched grid, fail closed with diagnostic/report limitation, and stop for review if useful primary delivery cannot be established rather than silently omitting it.

## Destruction seam and combat: useful visibility, incompatible semantics

Reference subscribes to public `ZDOMan.m_onZDODestroyed`; the inspected official G3 `HandleDestroyedZDO` invokes that delegate before registry removal/pool release. This is a useful snapshot seam, not proof an object was killed, picked up or successfully removed. [Subscription][destroy-hook] · [tally use][tally]
Keep planned Harmony destruction prefix/postfix/finalizer by default. Delegate substitution is optional only if demonstrably simpler: retain ingress sender context and successful-original gating, unsubscribe from the **exact registered instance**, and tolerate another mod overwriting the delegate or a subscriber throwing. Callback alone lacks sender/reason and cannot prove the original finished.
Only if selected, add `CallbackFailureDoesNotCount` and `CallbackUnsubscribesExactInstanceAndPreservesIngress`; do not implement a second callback framework preemptively.
Do not transplant the reference's `s_attackers + displayName` credit. It loops every currently connected marked peer on removal, so participation/pre-install marks are not attributable final hits or confirmed death callbacks. [Tally][tally]
Our existing combat source/ownership/lethal-damage/death rules remain intact. `PreinstallAttackerMarksDoNotChangeCombat` is an inspection assertion for unchanged semantics, not a request to add attacker-mark support.

## Secondary activities: exact remaining gaps

The reference's producer/snapshot surfaces establish no crop, successful-cook or upgrade collector. Mock food/crafting/upgrade stories and visible equipment quality cannot close these gaps. [Plugin surface][plugin-flow] · [snapshot output][output] · [mock feed][mock-extra]
Binary mechanisms in this table are G6/G7 from official 1.0.20/build25909431, not proof the installed server executes every owner path.

| Candidate | Observable supported subset | Missing contract / exclusions |
|---|---|---|
| Crop planting | New initial `Plant` with validated creator/lifecycle and reliable crop prefab classification. | Distinguish crop/tree/decorative plant; `Plant.Grow()` creates replacement without copying creator. Growth is not another planting; no seed-debit claim. |
| Crop harvest | `Pickable.RPC_Pick(long,int)` requester plus owner-side accepted transition/yield and reliable crop classification. | Forwarded request is visible, not acceptance; owner `SetPicked` sender is not necessarily harvester. Require unique generation/respawn cycle, actual yield, no repeated broadcast credit. |
| Station insertion | `CookingStation.RPC_AddItem(long,string,bool)` request correlated to a slot actually populated. | Could be a separately named accepted insertion count, not food cooked; reject disallowed item/full slot/failed execution. |
| Station retrieval | `RPC_RemoveDoneItem(long,Vector3,int)` plus actual output removal. | Collector is not necessarily inserter/cook; not completion attribution. |
| Completed food | Owner `UpdateCooking` accepted `NotDone→Done`, item conversion and quantity. | Need durable station/slot-generation→validated inserter provenance across restart/ownership/reuse. Exclude burnt/cleared/unknown slots; current slot data has no inserter identity. |
| Successful equipment upgrade | Client-local `InventoryGui.DoCrafting(Player)` includes success, break and quality-reduction paths. | Server gear quality alone cannot prove transaction, payer or debit. Needs separately approved exact companion/mod action contract; no disguised upgrade spending estimate. |

No blanket “client-owned means invisible”: routed requests can be observed while execution/accepted result remains client-side. Conversely, visibility of a request does not prove any secondary action completed.
These subsets are **not release additions**. No client companion/protocol/dependency is authorized by researching them, and no exact witness is newly required for the already-approved **inferred pickup** definition.

## Ranked optional later backlog — unapproved, not extra release tasks

Ranking is an agent recommendation based on evidence cost/value, not a user decision. Existing required reason/freshness handling stays required; this table proposes no additional diagnostics subsystem.

| Rank | Optional feature / smallest useful scope | Evidence needed before approval/implementation |
|---|---|---|
| 1 | Expose concise coverage/freshness summary using existing required reasons. | Honest “observed since”, gaps and unavailable metrics; no new telemetry service or per-sample raw log. Reference freshness separation is useful. [Profiles][profiles] |
| 2 | Sampled biome context/breakdown for accepted observations only. | World/account/fresh position, explicit interval/gap definition; not personal explored area or lifetime biome time. [Sampling][player] |
| 3 | Piece-filtered **standing census**, separate from built counts. | Validated world/creator/platform bridge and Piece prefab filter, bounded on-demand scan; never periodic unbounded work or placement delta. [Census counterexample][census] |
| 4 | Sessions/playtime comparison or weekday heatmap over existing durable intervals. | Stable identities, clipped intervals, coverage and timezone/DST contract; do not silently fix/change legacy boards. Reference name/FIFO/rolling-window model is not reusable as-is. [Sessions][sessions] · [week][week] · [heatmap][heatmap] |
| 5 | Raid starts / **current** boss flags. | Exact named event/state source and observation time, no outcome/participants/historical kill-date claim; no raw save-key parser adoption. [Events][parser] · [boss flags][bosses] |
| 6 | Accepted crop planting/harvest or station insertion/retrieval subsets. | Resolve the classification/acceptance/identity/dedup gaps above and use the literal action name. Completion metrics require stronger provenance. |
| 7 | Last-known gear/health snapshot. | Staleness/privacy/account contract; low statistics value, not inventory/crafting/upgrade history. [Status application][mod-state] |

Completed cooking and successful upgrade spending rank below these until provenance/contracts exist. None expands the four-metric release or its report budget now.

## Decisions and actual spec/plan deltas

These are proposed engineering clarifications for review under the existing definitions. Five implementation tasks, internal APIs, budgets and C#/LiteDB/report transport stay unchanged; no new service, client protocol or dependency.

| Finding | Revised design/plan action | Expected evidence after resumption |
|---|---|---|
| Owner-local item hook misses forwarded traffic | Observe validated `ZRoutedRpc.RPC_RoutedRPC` ingress; clone/preserve package; actual connection vs embedded sender; owner-local supplemental only. | `ForwardedClientOwnedPickupRequestIsObservedOnce`, `ForgedSenderAndPackageMutationAreRejected`: one request, expected inferred quantity, request-only0, forged sender0, bytes/cursors unchanged. |
| Profile number/name does not establish account | Keep existing socket identity; privately validate creator history index/platform bridge, duplicate/conflicting/missing evidence. | `SameNameAccountsRemainSeparate`, `DuplicateProfileOrConflictingCreatorHistoryRejected`, `CreatorHistoryIndexBoundsAndPlatformMapping`; stop rather than waive guard. |
| Delegate offers pre-pool state, not reason/success | Retain Harmony default; callback only for proven simplification with exact-instance unsubscribe/context/success gating. | Conditional callback failure/unsubscribe tests only if adopted. |
| Creator census includes non-Piece/old/replaced objects | Registry startup baseline only; Piece-prefab filter and lifecycle; clear world identity/baselines; no census deltas. | `CreatorBearingNonPieceIsNotConstruction`, `ExistingAndReplacedPiecesAreNotNewPlacement`, `WorldSwitchClearsIdentityAndBaselines`. |
| Removal tally is not final hitter | Do not change existing combat; no mark/name-based attribution. | `PreinstallAttackerMarksDoNotChangeCombat` inspection and existing combat regressions. |
| Latest status/heartbeat is not continuous history | Immutable capture time/world/account/freshness; retain fresh helm endpoints/cadence and cell first timestamps. | `HeartbeatDoesNotRefreshHelmEndpoint`, existing gap/restart/isolation/period tests. |
| No runtime minimap evidence | Preserve runtime-only detection/fail-closed/report limitation and useful-coverage stop. | `MissingRuntimeGridDisablesOnlyExploration`, `GridBoundsAndMismatchFailClosed`; no field-initializer fallback. |
| UI/demo overstates live producers | Document source limits; no imported Node/UI/A2S infrastructure, gathering proxy or optional metrics. | Source-to-design self-review; parent independent documentation review. |

## Checks, limits and handoff

**Performed:** local source reads, HEAD-ref pin inspection, G7 routed-ingress/creator-symbol decompilation in the existing official build, and source grep of test declarations. No downloaded source was run; no tests/builds/runtime checks were executed for this research.
**Test-count correction:** pinned files contain **14 top-level `test(...)` declarations: 9 collector + 5 mod tests**, not the handoff's 16. This count is static, not a pass count. [Collector tests][tests-collector] · [Mod tests][tests-mods]
Those tests cover log parsing, session arithmetic/restart closure, snapshot privacy/basic heatmap, timestamp normalization, plugin-payload application, staleness/normalization. They do not establish C# plugin execution, Harmony sequencing, remote pickup causality, creator platform bridges, crash-safe event uniqueness or live browser-failure recovery. [Test bodies][tests-collector] · [test bodies][tests-mods]
Reference `package.json` declares `node --test server/test/*.test.js`; it was **read, not run**. Our plan's five harness commands and current-reference build are expected future implementation checks, not research evidence. [Package][package]
**Self-review performed:** all four primary definitions preserved; ingress/creator/Piece/status-history corrections appear in core spec and Task2/4, not only this appendix; five task dependencies/internal signatures/budgets retained; source links pinned; optional backlog unapproved; no live/installed-version claim.
**Final documentation validation (2026-10-26):** parent read the revised artifacts; a fresh read-only source/plan review returned no material findings. Static checks passed for six documents and 64 pinned-source/local targets, reference definitions, line ranges and whitespace. Tracked diff is empty; only documentation/tracking files are untracked. These checks do not establish runtime behavior. Root `findings.md`, `task_plan.md`, `progress.md` record parent-owned progress.
**Still material:** headless active grid, verified platform→socket normalization, useful forwarded request/grant/mutation coverage and installed mod/Harmony behavior. No exact client inventory-success witness is a prerequisite for approved inference. Residual inferred-pickup misclassification remains visible.
Implementation remains paused pending review/resumption. No production access, commits/pushes, deployment, external writes or artifact delivery is implied. Assemblies/source are not redistributed; future substantial MIT source reuse needs retained notice.

## Pinned source references

[license]: https://github.com/MrGuato/valheim-game-dashboard/blob/28871399317c09b32a83c5c2028254c38c5b4487/LICENSE#L1-L21
[claims]: https://github.com/MrGuato/valheim-game-dashboard/blob/28871399317c09b32a83c5c2028254c38c5b4487/README.md#L50-L60
[readme-limits]: https://github.com/MrGuato/valheim-game-dashboard/blob/28871399317c09b32a83c5c2028254c38c5b4487/README.md#L98-L109
[requirements]: https://github.com/MrGuato/valheim-game-dashboard/blob/28871399317c09b32a83c5c2028254c38c5b4487/README.md#L146-L154
[plugin-flow]: https://github.com/MrGuato/valheim-game-dashboard/blob/28871399317c09b32a83c5c2028254c38c5b4487/mods/Longhouse.Server/ServerPlugin.cs#L56-L100
[heartbeat]: https://github.com/MrGuato/valheim-game-dashboard/blob/28871399317c09b32a83c5c2028254c38c5b4487/mods/Longhouse.Server/ServerPlugin.cs#L24-L35
[player]: https://github.com/MrGuato/valheim-game-dashboard/blob/28871399317c09b32a83c5c2028254c38c5b4487/mods/Longhouse.Server/ServerPlugin.cs#L104-L154
[destroy-hook]: https://github.com/MrGuato/valheim-game-dashboard/blob/28871399317c09b32a83c5c2028254c38c5b4487/mods/Longhouse.Server/ServerPlugin.cs#L77-L80
[tally]: https://github.com/MrGuato/valheim-game-dashboard/blob/28871399317c09b32a83c5c2028254c38c5b4487/mods/Longhouse.Server/ServerPlugin.cs#L159-L195
[census]: https://github.com/MrGuato/valheim-game-dashboard/blob/28871399317c09b32a83c5c2028254c38c5b4487/mods/Longhouse.Server/ServerPlugin.cs#L199-L211
[emitter]: https://github.com/MrGuato/valheim-game-dashboard/blob/28871399317c09b32a83c5c2028254c38c5b4487/mods/Longhouse.Server/ServerPlugin.cs#L216-L218
[props]: https://github.com/MrGuato/valheim-game-dashboard/blob/28871399317c09b32a83c5c2028254c38c5b4487/mods/Directory.Build.props#L1-L28
[mod-build]: https://github.com/MrGuato/valheim-game-dashboard/blob/28871399317c09b32a83c5c2028254c38c5b4487/mods/README.md#L42-L54
[collector]: https://github.com/MrGuato/valheim-game-dashboard/blob/28871399317c09b32a83c5c2028254c38c5b4487/server/index.js#L42-L80
[replay]: https://github.com/MrGuato/valheim-game-dashboard/blob/28871399317c09b32a83c5c2028254c38c5b4487/server/index.js#L19-L40
[api]: https://github.com/MrGuato/valheim-game-dashboard/blob/28871399317c09b32a83c5c2028254c38c5b4487/server/index.js#L113-L137
[state-save]: https://github.com/MrGuato/valheim-game-dashboard/blob/28871399317c09b32a83c5c2028254c38c5b4487/server/state.js#L8-L69
[sessions]: https://github.com/MrGuato/valheim-game-dashboard/blob/28871399317c09b32a83c5c2028254c38c5b4487/server/state.js#L82-L158
[state-events]: https://github.com/MrGuato/valheim-game-dashboard/blob/28871399317c09b32a83c5c2028254c38c5b4487/server/state.js#L159-L219
[mod-state]: https://github.com/MrGuato/valheim-game-dashboard/blob/28871399317c09b32a83c5c2028254c38c5b4487/server/state.js#L179-L219
[prune]: https://github.com/MrGuato/valheim-game-dashboard/blob/28871399317c09b32a83c5c2028254c38c5b4487/server/state.js#L222-L228
[server-view]: https://github.com/MrGuato/valheim-game-dashboard/blob/28871399317c09b32a83c5c2028254c38c5b4487/server/snapshot.js#L39-L73
[bosses]: https://github.com/MrGuato/valheim-game-dashboard/blob/28871399317c09b32a83c5c2028254c38c5b4487/server/snapshot.js#L75-L92
[week]: https://github.com/MrGuato/valheim-game-dashboard/blob/28871399317c09b32a83c5c2028254c38c5b4487/server/snapshot.js#L94-L109
[profiles]: https://github.com/MrGuato/valheim-game-dashboard/blob/28871399317c09b32a83c5c2028254c38c5b4487/server/snapshot.js#L111-L146
[mobs]: https://github.com/MrGuato/valheim-game-dashboard/blob/28871399317c09b32a83c5c2028254c38c5b4487/server/snapshot.js#L149-L157
[heatmap]: https://github.com/MrGuato/valheim-game-dashboard/blob/28871399317c09b32a83c5c2028254c38c5b4487/server/snapshot.js#L159-L172
[output]: https://github.com/MrGuato/valheim-game-dashboard/blob/28871399317c09b32a83c5c2028254c38c5b4487/server/snapshot.js#L180-L193
[parser]: https://github.com/MrGuato/valheim-game-dashboard/blob/28871399317c09b32a83c5c2028254c38c5b4487/server/logparse.js#L10-L55
[logs]: https://github.com/MrGuato/valheim-game-dashboard/blob/28871399317c09b32a83c5c2028254c38c5b4487/server/logsource.js#L6-L87
[file-logs]: https://github.com/MrGuato/valheim-game-dashboard/blob/28871399317c09b32a83c5c2028254c38c5b4487/server/logsource.js#L90-L117
[config]: https://github.com/MrGuato/valheim-game-dashboard/blob/28871399317c09b32a83c5c2028254c38c5b4487/server/config.js#L10-L35
[a2s]: https://github.com/MrGuato/valheim-game-dashboard/blob/28871399317c09b32a83c5c2028254c38c5b4487/server/a2s.js#L1-L60
[world]: https://github.com/MrGuato/valheim-game-dashboard/blob/28871399317c09b32a83c5c2028254c38c5b4487/server/world.js#L38-L117
[normalization]: https://github.com/MrGuato/valheim-game-dashboard/blob/28871399317c09b32a83c5c2028254c38c5b4487/server/mods.js#L1-L78
[tests-collector]: https://github.com/MrGuato/valheim-game-dashboard/blob/28871399317c09b32a83c5c2028254c38c5b4487/server/test/collector.test.js#L1-L104
[tests-mods]: https://github.com/MrGuato/valheim-game-dashboard/blob/28871399317c09b32a83c5c2028254c38c5b4487/server/test/mods.test.js#L1-L84
[package]: https://github.com/MrGuato/valheim-game-dashboard/blob/28871399317c09b32a83c5c2028254c38c5b4487/package.json#L1-L13
[compose]: https://github.com/MrGuato/valheim-game-dashboard/blob/28871399317c09b32a83c5c2028254c38c5b4487/deploy/docker-compose.yml#L9-L44
[ui-fields]: https://github.com/MrGuato/valheim-game-dashboard/blob/28871399317c09b32a83c5c2028254c38c5b4487/assets/dashboard.js#L132-L269
[ui-world]: https://github.com/MrGuato/valheim-game-dashboard/blob/28871399317c09b32a83c5c2028254c38c5b4487/assets/dashboard.js#L312-L333
[fallback]: https://github.com/MrGuato/valheim-game-dashboard/blob/28871399317c09b32a83c5c2028254c38c5b4487/assets/dashboard.js#L438-L475
[age]: https://github.com/MrGuato/valheim-game-dashboard/blob/28871399317c09b32a83c5c2028254c38c5b4487/assets/dashboard.js#L428-L435
[mock-core]: https://github.com/MrGuato/valheim-game-dashboard/blob/28871399317c09b32a83c5c2028254c38c5b4487/assets/mock-data.js#L1-L111
[mock-extra]: https://github.com/MrGuato/valheim-game-dashboard/blob/28871399317c09b32a83c5c2028254c38c5b4487/assets/mock-data.js#L179-L223
