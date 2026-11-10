# Combat persistence regressions

From the repository root:

```sh
DOTNET_ROLL_FORWARD=Major dotnet run --project tests/CombatStatistics/CombatStatistics.csproj
```

The net8 console harness links the actual persistence sources and legacy record classes, uses the existing LiteDB 5.0.21 dependency, and creates disposable temporary databases. Only game/config/logging dependencies are stubbed. No production database or Discord endpoint is used.

2026-10-25 TDD evidence: tests were written first and run against a temporary test-only throwing contract because the combat sources did not exist. After correcting a test API compilation error, the command above exited 1 with 1/14 passing: missing `Database.Combat` and unimplemented store operations failed. The temporary contract was removed when production files were added. The final command exits 0 with 14/14 passing. The final disposed-engine test also rejects unrelated exception types, unlike the initial red version.

Coverage: six legacy collection sentinels survive real `Database` initialization/insertion/reopen; indexes; duplicate races across two stores and restart; exact world identity; delimiter-safe victim identities; half-open year-crossing and single-tick UTC windows; account identity across renames/shared display names; invalid input; disposed-engine errors; real oversized-index write errors and rollback; atomic durable report claims; immediate access/shutdown; explicit initialization failure with no fallback database.

Lifecycle follow-up: removing the initialization-failure test's swallowed disposal exception reproduced the cleanup regression (13/14 passed, exit 1). After the fix, the same command passes 14/14 (exit 0), including two nonthrowing disposal calls after failed initialization and no fallback database before or after shutdown.

Implementation details relevant to consumers:
- `Insert` derives `Id` from the length-delimited world/victim identity, ignoring any caller-provided ID. Hashes avoid default LiteDB collation affecting identity.
- New collections are `combat_kills` and `combat_reports` only. Their `TimestampUtc` BSON fields store UTC ticks, using a store-local mapper; legacy BSON dates/global mapping are untouched. Use `CombatStore.Query` to materialize UTC records.
- Only duplicate-key errors return `false`; validation, initialization access and other store errors surface to the caller. `Dispose` still waits for initialization and releases resources, but does not rethrow an initialization failure already recorded in the log and exposed by `Combat`.
- Report claims are durable reservations, not delivery acknowledgments; delivery/retry policy belongs to the reporting lane.

The parent owns full plugin compilation, cross-lane regressions and live-runtime validation. This harness does not validate game callbacks, scheduling or webhook delivery.
