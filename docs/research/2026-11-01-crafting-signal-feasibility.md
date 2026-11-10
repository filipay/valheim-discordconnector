# Cauldron and preparation-table signal feasibility

Status: conditional/unavailable; inspected November 2, 2026. This gate does not block the separately supported lightweight statistics. No crafting collector or inventory-transfer technique is approved.

## Question and evidence

Can either station supply a distinctive, replicated, player-attributable crafting signal without a client mod or inventory manipulation?

| Station | Native evidence | Missing serialized evidence | Current verdict |
|---|---|---|---|
| Cauldron | Actual 1.0.21 assembly shows local recipe processing and start/done effects. The done-effects path can run even if output insertion fails. | Matching station/effect prefab references, network components, shared-effect use, authenticated source and defensible signal unit. | Unavailable; neither an attempt nor successful craft is currently collected. |
| `piece_preptable` | Component catalogue identifies a CraftingStation rather than a rack/oven CookingStation; not proof of the installed asset graph. | Its own matching start/done references and the same replication, attribution and unit evidence. | Unavailable independently of cauldron. |

The supplied assembly baseline is SHA-256 `72f77a0e9c70cf0329e9f1f58687bba5dc9fc77f203bc1826f3a3ca1a7b81312`. Assembly hashes do not establish serialized asset versions. See the [approved design](../superpowers/specs/2026-11-01-lightweight-observed-statistics-design.md#cauldron-and-preparation-table-acceptance-gate) and [modding guide](reports/Valheim%20modding%20patterns.md).

Read-only filename/archive inventories of the repository, `Downloads/valheim`, `Downloads/valheim-managed.tar.gz` and the existing staged game-reference directory found managed DLLs/mod packages, not matching serialized game assets. No assembly execution, extraction, new download or live observation was performed. Missing assets are not evidence that effects are absent or non-networked.

## Proposed experiment — not authorized or executed

First obtain matching serialized references and pin game, assets, loaded Harmony and mod versions. Then seek separate approval for a removable passive observer at exact verified hooks. No forced ownership, custom client protocol, inventory inspection or general packet dump.

One session: at most 20 minutes, 2,000 records, 512 KiB serialized capture and 256 bytes per record. Stop at the first limit; overflow invalidates affected comparisons. Keep only phase, relative time, bounded candidate effect identifier/discriminator, start/done path, attribution status and ephemeral participant/station aliases. No raw account IDs, names, coordinates, inventories or payloads.

For each station separately:

1. Idle browsing to distinguish UI/ambient effects.
2. One completed craft three times, then cancellation three times.
3. Two recipes and three-cycle multicraft to establish effects versus cycles, not item quantities.
4. Two nearby players alternating and overlapping actions, requiring authenticated attribution rather than nearest-player guesses.
5. Another station with similar effects to expose shared prefabs.
6. A safe practical resource/output failure, or explicitly mark this control untested.

Pass only when replication, distinctness, source identity, duplicate behavior and unit are demonstrated. Permitted labels are **observed station start-path effects** or **observed station done-path effects**, according to evidence—not successful items crafted. Remove the observer afterward and request approval of a small implementation supplement. No station-use, feast-placement, output-deposit or hidden-chest fallback.

## Actual checks and limits

Completed: bounded read-only file/archive inventories and review of existing pinned native findings. No new runtime claim, performance claim, source patch or DLL for crafting. Source/metadata establishes hypotheses; a separately authorized controlled observation is still necessary.
