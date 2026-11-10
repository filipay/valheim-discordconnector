# Lightweight observed statistics

Status: design approved November 1; implementation and a new local testing DLL explicitly authorized November 2. No live experiment, deployment or database repair is authorized.

## Outcome and constraints

Provide useful daily/weekly Discord statistics with vanilla crossplay clients, without Dedicated Simulation or another ownership/simulation replacement. Keep the working manual Flex/Toast triggers, native admin checks, automatic scheduler, existing database and accepted history.

- No required client mod, custom client handler, forced ownership, enlarged simulation area or extra world-object instantiation.
- No mandatory NPS, ServersideQoL, Jotunn or Dedicated Simulation dependency. Installed mods may affect visibility; their presence is not evidence of an action.
- Preserve existing configuration keys and default-off collection. New flags also default off.
- Separate confirmed observations from client-reported presentation/credit signals. Do not combine them into a misleading total.
- No database deletion, history reinterpretation, automatic replay or deployment. Existing unrelated uncommitted changes remain intact.

## Approaches considered and selected

1. **Simulate more of the world:** potentially improves authoritative coverage but adds ownership, compatibility and resource costs. Rejected by the user after memory/swap problems.
2. **Wait for exact client inventory/combat transactions:** would support stronger claims but needs client instrumentation unavailable to console clients. Rejected under the server-only constraint.
3. **Passive native observation:** reuse existing server processing and vanilla replicated messages, label each signal honestly, and measure coverage. Selected in conversation. It costs completeness: naturally server-owned combat/cooking may be sparse, while broader reported signals are not independently verified outcomes.

## Metric contracts

| Metric | Count and attribution | Explicit exclusions/limits |
|---|---|---|
| Reported kill participation credits | One validated native `RPC_RegisterKill` envelope addressed to one current, unambiguous connected player; group by the target's existing stable socket account. | Not distinct creature deaths, final blows or damage-qualified contributions. Native attacker selection uses historical display-name flags; target authentication cannot repair those semantics. |
| Observed consumable uses | One validated vanilla player `SetTrigger("eat")` signal, attributed to the actual sender's current character/account. Food and potions both count; item identity is unnecessary. | Presentation signal, not inventory-debit proof. No food-type/popularity board, current-buff inference or claim of cheat-proof consumption. |
| Confirmed native combat subset | Existing positive actual HP-loss and successful eligible death/contribution contracts, but without a simulation-plugin prerequisite. | Keep dedicated-server, actual ownership, untamed/nonplayer, actor, nested-success, lifecycle and dedup guards. Unattributed DOT stays unassigned; remote requested damage is not measured damage. |
| Rack/oven completions | Existing accepted insertion → durable slot generation/inserter → supported completion conversion, where the station is already instantiated and server-owned. | No simulation-plugin prerequisite; no forced station ownership. Existing restart fencing, unknown-slot rejection and burn/reuse rules remain. Not cauldron crafting, retrieval quantity or food eaten. |
| Cauldron crafting activity | Conditional: a distinctive replicated crafting effect with demonstrable source-player attribution. Validate start and completion-path signals separately. | Start can be cancelled; even the native completion-effect path can run after failed output insertion. Never label as successful crafts or output units. |
| Food Preparation Table crafting activity | Same conditional investigation, validated against this station's own effects. | `piece_preptable` is documented as a `CraftingStation`, not a rack/oven `CookingStation`; its outputs include non-food items. Placed feasts and servings do not establish new table crafts. |

The first four rows are implementation goals. The final two are feasibility gates, not promised counters. Existing rolls, construction/material estimates, sailing and per-player observed exploration remain separate and unchanged unless a demonstrated shared defect requires correction.

## Native routing and attribution

Use the existing actual-transport scope and native decoded-routing pattern rather than adding another raw envelope decoder or fallback transport.

- Observe forwarded remote-target notifications as well as locally generated server messages; a server-local `RPC_RegisterKill` handler alone misses forwarded traffic.
- For inbound messages, bind the actual `ZRpc` to one ready connected peer and verify the embedded sender matches that peer. Authenticate the reporter, not an unproven victim owner: kill-credit payloads have no victim ID.
- Resolve the kill-credit **target** using the native current character/peer mapping, rejecting missing, ambiguous or stale mappings. Reporter and recipient may legitimately differ.
- For local-origin notifications, establish genuine server origin separately; do not manufacture a remote transport identity or accept arbitrary messages that claim the server UID.
- For consumables, require the target ZDO to be the authenticated sender's current character, known `SetTrigger` schema and exact `eat` string. Unrelated creature animations do not count.
- Copy borrowed parameters, preserve gameplay cursors, validate payload bounds/schema, and stage observations through exception-safe scopes. Preserve NPS-compatible skipped-original semantics; `__runOriginal=false` is not itself proof that a message was unhandled.

Deduplicate repeated hook observations and retransmitted envelopes with bounded connection/session/world/message identity. Keep durable record identities distinct from the confirmed-death keys. Different native message IDs cannot be collapsed into a provably unique death without a victim ID; authenticated clients may also fabricate fresh notifications. Document both limits instead of claiming exactly-once real-world actions.

## Capture, persistence and reports

- Remove Dedicated Simulation checks from all relevant capture, coverage, configuration descriptions and documentation—not merely one patch guard. Retain existing combat master/own-flag semantics for confirmed combat; proposed reported-credit and consumable flags are independent own flags.
- Cooking remains independent of the combat master. Actual station ownership/accepted transitions remain mandatory.
- Experimental dodge avoidance remains unavailable until its own validated adapter exists; removing a plugin gate does not create one.
- Reuse the existing bounded main-thread capture and serialized database worker. Carry immutable actor/world/time/payload summaries, never borrowed packages or Unity objects into persistence.
- Add separate typed records/collection identities for reported credits and consumable signals. Keep UTC server observation timestamps, atomic insert/durable dedup and stable account/name snapshot rules. Do not merge or backfill old `combat_kills`, confirmed participation or existing action records into these metrics.
- Freeze exact new APIs, collection names, per-message payload limits, replay-cache limits and physical-batch accounting in the implementation plan before parallel writers start. Reuse existing limits where they fit; use explicit bounded admission/backpressure rather than unbounded caches or per-event tasks.
- Continue one automatic scheduler, the existing durable report-claim namespace, Dublin/local calendar behavior, at-most-once attempt semantics and separate manual previews. No additional service, webhook transport or timer.
- Add separately labelled report sections for reported credits and consumable uses. Preserve the global 1,900 UTF-16-unit budget, escaped literal text and disabled mentions. Test all enabled nonempty sections together; current fourteen-section budgeting must be revisited for the added sections, without pagination or silently presenting reported credits as confirmed kills.
- Keep useful historical rows readable under their own flags even when current capture is unavailable. Unknown/missing observations are not zero. Coverage notes must distinguish direct observations, reported signals and unavailable prerequisites without dumping packet logs.

## Cauldron and preparation-table acceptance gate

First inspect available serialized prefab/effect metadata without executing supplied game assemblies. Assemblies alone do not establish actual station effect references or whether they contain `ZNetView`.

If metadata is insufficient, propose a separately approved, bounded passive server experiment. Compare idle station browsing, one completed craft, cancellation, different recipes/multicraft, two nearby crafters, another station with similar effects, and an output/resource failure where practical.

Proceed only if the effect is distinguishable, replicated and attributable to a current authenticated player without proximity guessing. Establish the count's actual unit and known missed/extra cases. Otherwise leave that station unavailable and record the missing evidence. Do not silently substitute station-use time, animations, output deposits, feast placement or recipe estimates for crafting.

## Verification and rollout

Use proportionate extensions of the existing console harnesses, not a new fake-game framework:

1. Pure routing/collection cases: actual sender versus target, forged/stale/ambiguous identity rejection, duplicate envelope/hook observations, local versus forwarded origin, nested failure, world/session reset, bounds and disabled flags.
2. Real disposable LiteDB cases: restart dedup, UTC period boundaries, separate metric histories and batch accounting. Never open or repair the live database for tests.
3. Cooking/combat regressions: no plugin installed but genuinely owned observations accepted; nonowned victims/stations still rejected; current lifecycle/provenance safeguards remain.
4. Rendering/scheduler cases: all new and existing sections within budget, honest labels, safe mentions, unchanged manual authorization/cooldown and automatic claims.
5. One isolated native-reference Release/ILRepack build after review, with existing publish/copy no-ops and approved opt-in LiteDB WAL-flush backport retained. Record actual errors/warnings, checksum and provenance; do not overwrite unrelated packaged outputs.
6. Separately approved operator checks on the real server: compare immediate before/after reports for known consumable uses, cooperative kills and naturally server-owned cooking. Measure memory/frame/disk effects; no resource guarantee from static source alone.

Approval of this design leads to a task-by-task implementation plan. Live observation/deployment, database mutation, commits and pushes retain their separate authorization requirements.

## Evidence and current unknowns

- [Valheim modding patterns](../../research/reports/Valheim%20modding%20patterns.md): pinned mod examples, decoded routing, authenticated identity, state versus events, main-thread ownership and evidence limits.
- [Existing as-built audit](../../server-statistics-audit.md): current APIs, configuration, storage, report budgets and known runtime limits. Its simulation prerequisites describe the current build, not this proposed replacement.
- [Food/crafting feasibility research](../../research/2026-10-26-activity-feasibility-and-food.md): previous local-inventory findings; subsequent cauldron effect-path investigation refines, rather than establishes, network visibility.
- Metadata-only native inspection of supplied Valheim 1.0.21 `assembly_valheim.dll`, SHA-256 `72f77a0e9c70cf0329e9f1f58687bba5dc9fc77f203bc1826f3a3ca1a7b81312`: `Character.OnDeath`, `Game.RegisterKill`, `Humanoid.UseItem`, `ZSyncAnimation.SetTrigger`, `InventoryGui.DoCrafting`, `CraftingStation` and `EffectList.Create`. Kill credits omit victim identity; native `eat` covers consumables; crafting effects omit recipe/quantity/crafter and do not establish successful output.
- Preparation-table component reference: [Jotunn prefab catalogue](https://valheim-modding.github.io/Jotunn/data/prefabs/prefab-list.html). This is a community-generated catalogue, not version-pinned inspection of the supplied station assets.
- Current useful native combat/cooking coverage, effect replication/uniqueness, crossplay/mod hook execution and client-reported signal accuracy remain runtime unknowns. No new collector or replacement DLL has been produced for this design.
