# Server statistics: as-built behavior and manual rollout

Updated 2026-10-25 for plugin **3.1.3**. Implemented: **partial, opt-in SERVER-OBSERVED combat**, additive persistence and daily/weekly reports. Legacy statistics remain unchanged; other requested metrics are deferred, with no approximate proxies substituted.

Passing isolated checks and a real game-reference build do **not** prove live Valheim/Harmony/mod/Discord behavior. The manual steps below have not been performed against the user's server.

## Legacy implementation and compatibility

- **Version:** plugin constant is **3.1.3**, not the documentation package version ([Plugin.cs](../DiscordConnector/Plugin.cs#L19-L27)).
- **Storage:** **LiteDB 5.0.21, not SQLite**; legacy `records.db` collections are `deaths`, `joins`, `leaves`, `shouts`, `pings`, and `player_name`. Legacy events store ID, name, host-local `DateTime.Now`, player ID and position; online time is derived, not a separate event collection ([database](../DiscordConnector/Records/Database.cs), [record](../DiscordConnector/Records/Classes/SimpleStat.cs#L9-L33), [dependency](../DiscordConnector/DiscordConnector.csproj#L104)).
- **Identity:** records use the peer socket's `GetHostName()` (e.g. Steam/Xbox-prefixed identity), not display name or transient `m_uid`. Preserve that stable socket identity; combat attribution must resolve to a connected peer unambiguously. Legacy aggregation can group by ID, name, or both; displayed results retain name/count ([handlers](../DiscordConnector/Handlers.cs#L11-L56), [grouping](../DiscordConnector/Records/Database.cs), [result](../DiscordConnector/Records/Classes/CountResult.cs)).
- **Events:** joins/leaves come through `ZNet` patches; repeated character registration infers player deaths, not killer/cause. Shout/ping handlers record chat activity. Recording toggles are separate from announcement toggles ([patches](../DiscordConnector/Patches/ZNetPatches.cs), [handlers](../DiscordConnector/Handlers.cs#L19-L331), [chat RPC](../DiscordConnector/RPC/Server.cs#L7-L75)).
- **Boards:** exactly five legacy selectable statistics: deaths, sessions (join count), shouts, pings, and derived online time. Five configurable board slots are disabled by default, with 600-minute intervals from startup, not calendar delivery schedules ([statistics](../DiscordConnector/Leaderboards/Leaderboard.cs#L129-L167), [config](../DiscordConnector/Config/CustomLeaderBoardConfig.cs#L11-L59), [timers](../DiscordConnector/Plugin.cs#L75-L127)).
- **Rank count:** legacy default is **3**, not 5; legacy configuration/output is preserved. Default **5 applies to new combat reports only** ([legacy defaults](../DiscordConnector/Config/CustomLeaderBoardConfig.cs#L21-L25), [combat defaults](../DiscordConnector/Config/CombatConfig.cs#L15-L25)).
- **Filtering:** host-local Today/Yesterday/PastWeek/current-week filters, no explicit legacy timezone setting. Existing date-part comparisons can fail across month/year boundaries; PastWeek spans today plus seven preceding dates; Monday-week Sunday adjustment writes the wrong end variable. Online-time pairing filters joins but does not correctly clip sessions to report boundaries ([date helpers](../DiscordConnector/Leaderboards/Leaderboard.cs#L182-L240), [queries/date comparisons](../DiscordConnector/Records/Database.cs)).
- **Other legacy limits:** board slot 5 still invokes board 4; legacy inserts remain fire-and-forget. Combat access now waits for shared database initialization; it does not replace the legacy collections. The board/date issues are audit findings, not fixes delivered here ([timer](../DiscordConnector/Plugin.cs#L119-L127), [database lifecycle](../DiscordConnector/Records/Database.cs)).

## Requested feature matrix

“None” means no legacy connector event collection/leaderboard for that feature. Combat is implemented with the limits below, **not runtime-verified completeness**. Deferred rows need exact action/identity evidence from client instrumentation or a compatible mod API—not a generic server toggle. No delivery-time estimates are established.

| # | Requested statistic | Legacy support / event evidence | Player attribution and decision |
|---|---|---|---|
| 1 | Sailing time | None; driven ships are client-owned [S2]. | **Deferred.** Requires an exact boarded/steering interval and player identity from client instrumentation or a mod API. Position near water/a ship is not sailing time. |
| 2 | True unique minimap exploration | None; public positions are not the player's explored minimap. | **Deferred.** Requires actual newly revealed minimap cells, union/deduplication and player identity from the client or an exact map API. Sampled visited cells are not exploration. |
| 3 | Resources gathered, excluding transfers/repick | None; ItemLedger observes some movements and infers unseen flows [S3]. | **Deferred.** Requires fresh-source provenance, quantities, pickup history and actor identity across inventory/container transfers. A generic pickup event cannot exclude transfers/repick. |
| 4 | Actual building material costs | None; a replicated piece is not an inventory debit [S3, S4]. | **Deferred.** Requires successful placement plus actual materials charged and payer identity, respecting free-build/modded costs, from client instrumentation or an exact action API. Recipe cost is not consumption. |
| 5 | Actual upgrade material costs | None; upgrading is explicitly unseen by ItemLedger [S3]. | **Deferred.** Requires the completed upgrade transaction, actual debit and payer identity via client instrumentation or a mod API. Quality changes/recipe estimates are insufficient. |
| 6 | Crops harvested | None; server-owned pickables offer partial visibility [S4]. | **Deferred.** Requires confirmed successful crop harvest, yield, unique action and requesting player from a verified action/identity API or client instrumentation. Pickup/drop alone is insufficient. |
| 7 | Crops planted | None; client-created plant/piece state offers partial visibility [S3, S4]. | **Deferred.** Requires confirmed valid placement, crop classification and authenticated actor, with replay rejection, via client instrumentation or a verified mod action API. |
| 8 | Completed food | None; cooking output is partly visible but this server path records no crafter [S4]. | **Deferred.** Requires an exact completed recipe/output event and responsible player, across relevant stations/client crafting. Input, retrieval and eating are different actions; an exact mod API or client instrumentation is needed [S3]. |
| 9 | Creature kills | None; legacy player deaths are not creature kills. Current source correlates `ApplyDamage` with `OnDeath`. | **Opt-in, partial server observations:** only server-owned, nonplayer, untamed targets; attributable lethal damage and completed death callbacks must agree. Attacker must resolve uniquely to a ready connected peer/socket identity. No guessed credit for unknown/indirect/client-owned cases; runtime unverified. |
| 10 | Boss kills | None; current combat source uses `IsBoss()` and separates boss from nonboss totals. | Same bounded observations as #9. Global progression keys are not per-player/repeated kills. **Not complete boss-kill coverage.** |
| 11 | Creature / biome breakdown | None; current source captures target prefab and `Heightmap.FindBiome` at the death callback. | Only accepted combat events; empty creature/biome drops the observation. Reports aggregate creature types (including bosses) and death-location biomes across accepted players, not a full per-player cross-tab. Runtime unverified. |

Legacy **deaths, sessions, online time, shouts and pings remain available** through their existing configuration. Death records are inferred player respawns; online time uses recorded join/leave pairs and has the boundary limitations above. Neither is a substitute for new kill attribution.

## What the optional simulation mod changes—and does not

Reviewed source: `MistrCech/valheim-serverside` at **`b2deeb7ebf1efdac2a09394da1984f08c7da5469`**, not an assumed version installed on the user's server.

- [S1] [Plugin identity and dedicated-server guard](https://github.com/MistrCech/valheim-serverside/blob/b2deeb7ebf1efdac2a09394da1984f08c7da5469/src/Valheim_Serverside/ServersidePlugin.cs#L16-L55): GUID **`MVP.Valheim_Serverside_Simulations`**. This is a **server-only** mod; [its README](https://github.com/MistrCech/valheim-serverside/blob/b2deeb7ebf1efdac2a09394da1984f08c7da5469/README.md#L9-L25) says players keep vanilla clients. GUID presence alone does not prove it is enabled or that a target is server-owned.
- [S2] [Ship ownership](https://github.com/MistrCech/valheim-serverside/blob/b2deeb7ebf1efdac2a09394da1984f08c7da5469/src/Valheim_Serverside/Features/Core.cs#L733-L779): driven ships go to the driver's client; undriven ships return to the server, with container-use and missing-driver exceptions.
- [S3] [ItemLedger's stated observation limits](https://github.com/MistrCech/valheim-serverside/blob/b2deeb7ebf1efdac2a09394da1984f08c7da5469/src/Valheim_Serverside/Features/ItemLedger.cs#L13-L56): vanilla clients do not expose their bags; **crafting, upgrading, eating and buying from a trader are not seen**. Recipes, lucky yields and some flows are inferred. This is not an exact gathering/crafting/consumption event API.
- [S4] [Core ownership policy](https://github.com/MistrCech/valheim-serverside/blob/b2deeb7ebf1efdac2a09394da1984f08c7da5469/src/Valheim_Serverside/Features/Core.cs#L451-L504) skips nonpersistent objects and does not immediately reclaim every valid client owner. [Client-created ownership exceptions](https://github.com/MistrCech/valheim-serverside/blob/b2deeb7ebf1efdac2a09394da1984f08c7da5469/README.md#L427) explicitly include player-created objects and client arrows/fireballs; special fire-source reclamation is not universal ownership. [Pickable/cooking patches](https://github.com/MistrCech/valheim-serverside/blob/b2deeb7ebf1efdac2a09394da1984f08c7da5469/src/Valheim_Serverside/Features/Core.cs#L45-L111) show partial server execution, including cooking output with no recorded crafter.

Consequently, more server simulation permits a conservative observation scope, **not complete telemetry**. Health changes, disappearance, proximity, object ownership and forwarded hit requests alone do not prove a confirmed kill or its player. No client companion, minimap surrogate or proximity-based credit is implied by this scope.

## Implemented combat and persistence

- [Tracking](../DiscordConnector/Patches/CombatPatches.cs) requires explicit enablement, a dedicated server and the optional simulation-mod GUID (respecting its `General/Enabled` when exposed). The mod is not a mandatory dependency for legacy functionality. Presence provides eligibility only: each accepted victim must actually be server-owned, untamed and nonplayer, with valid world/victim identity.
- [Correlation](../DiscordConnector/Records/CombatCorrelation.cs) requires lethal applied damage **and** a successfully completed death callback, including all tracked enclosing invocations. The hit's attacker character must resolve uniquely to a ready, connected peer and stable socket identity, rechecked before insertion. Unknown/unmapped/ambiguous/disconnected attackers, environmental deaths and attacker-less DoT are skipped; there is no contributor, proximity, RPC-sender or stale-last-hit credit.
- A new hit (even at zero HP), intercepted owner transfer or revival invalidates prior attribution. Delayed death correlation expires after one minute. Exceptions/skipped originals discard affected candidates. Cross-thread mods, custom death overrides bypassing the base method, direct state writes and later conflicting Harmony patches remain outside proven coverage. Client-provided attacker identity is **not cheat-proof** ([tracker limitations](../tests/CombatTracking/README.md#L24-L32)).
- Exact database path: **`BepInEx/config/games.nwest.valheim.discordconnector/records.db`**. [CombatStore](../DiscordConnector/Records/CombatStore.cs) adds only `combat_kills` and `combat_reports` to that same LiteDB file. Events retain world/victim IDs, stable player ID/name, creature, biome, boss flag and UTC timestamp. A **store-local UTC-tick mapper** preserves submillisecond precision without changing legacy BSON dates/global mapping; no old-record migration or deletion is introduced.
- Atomic unique-key inserts deduplicate hashed world/victim identity across concurrent inserts and restart. Atomic durable report claims prevent repeat attempts for the same world/timezone/period. These are reservations, **not delivery acknowledgments**. Database initialization/indexing is coordinated; combat access waits for initialization, with no fallback database ([Database](../DiscordConnector/Records/Database.cs), [persistence checks](../tests/CombatStatistics/README.md)).
- A failed combat insert is logged by exception type and that event is dropped, without clearing unrelated completed/pending observations; other completed events are still processed. There is no automatic event-storage retry ([per-event failure handling](../DiscordConnector/Patches/CombatPatches.cs#L173-L198)).

## Configuration: native and legacy files are separate

The new `[Server-Observed Combat]` section binds **`BaseUnityPlugin.Config`**, whose native GUID is **`nwesterhausen.DiscordConnector`**. Its generated file is **`BepInEx/config/nwesterhausen.DiscordConnector.cfg`**, not a file inside the legacy directory ([plugin binding](../DiscordConnector/Plugin.cs#L19-L45), [keys/defaults](../DiscordConnector/Config/CombatConfig.cs)).

Generated defaults in that **native** file:

```ini
[Server-Observed Combat]
Enabled = false
Daily Enabled = false
Weekly Enabled = false
Time Zone = Europe/Dublin
Top Count = 5
```

`Enabled` controls new observation collection. Daily/weekly flags independently control reports over stored combat observations; report settings are captured at startup, so restart after editing. `Top Count` accepts 1–16. Invalid timezone/top count disables reports, not tracking ([CombatConfig](../DiscordConnector/Config/CombatConfig.cs), [report validation](../DiscordConnector/Leaderboards/CombatReports.cs#L32-L58)).

Legacy files remain under **`BepInEx/config/games.nwest.valheim.discordconnector/`**; the main filename is **`discordconnector.cfg`**, not `main.cfg`. Legacy board/toggle/message files and JSON export are unchanged and **do not expose the new combat section**. This discoverability gap is accepted; combat keys in a legacy file will not bind ([legacy paths/bindings](../DiscordConnector/Config/PluginConfig.cs#L31-L68), [JSON export](../DiscordConnector/Config/PluginConfig.cs#L301-L310)). No webhook URLs or account IDs belong in this page.

## Reports: calendar periods, partial data, no delivery retry

- [CombatReports](../DiscordConnector/Leaderboards/CombatReports.cs) creates one combined summary per enabled completed day/week: separate top-player rankings for nonboss and boss kills, plus creature-type (including bosses) and biome totals across accepted observations. Ranking identity is the stable socket ID; display uses the latest observed name, with stable-ID tie breaks. Output is escaped, bounded/truncated, and explicitly labeled **SERVER-OBSERVED / partial coverage**. Empty periods produce no message.
- Periods use configured local calendar boundaries converted separately to UTC: **`[startUtc, endUtc)`**, including Dublin DST's 23/25-hour days. Weeks start Monday and cover Monday–Sunday. Deterministic boundary policy: nonexistent local times advance to the **first valid instant**; repeated/ambiguous boundaries use the **earlier UTC occurrence**. Santiago midnight-gap and Havana repeated-midnight cases are covered by tests ([boundary conversion](../DiscordConnector/Leaderboards/CombatReports.cs#L133-L160), [regressions](../tests/CombatReporting/Program.cs#L301-L348)). Legacy host-local filtering is unchanged.
- [Plugin.Update](../DiscordConnector/Plugin.cs#L141-L172) checks about once per minute on the main thread. World snapshots, **synchronous database queries**, rendering and claims run there; large histories can stall a frame. There is no background-query performance guarantee.
- Reports use the existing **`leaderboardsAll` webhook-event filter** (`Webhook.Event.LeaderboardsAll`) and existing routing. The internal [SendRenderedMessage](../DiscordConnector/DiscordApi.cs#L119-L135) sends the already escaped/bounded report literally as content or an embed description: **no template/variable expansion or configured embed metadata**, and **mentions disabled**. Legacy message/template/mention behavior is unchanged. Enable the appropriate route without exposing its webhook value; this is not a new webhook destination.
- Startup/world changes baseline both calendars without querying history. Subsequent checks consider only the latest completed period: **no startup backlog, offline replay or historical catch-up**. A claim is persisted before calling the existing async sender: **at-most-once attempt, not guaranteed delivery**. Crashes after claiming and failed sends can lose a report; failures are **not automatically retried**.

## Verification and build evidence

| Item | Evidence | Limit |
|---|---|---|
| Upstream PR421 | All six changed production/test/workflow files match [head `79906411cf8f20b00fa66a2ae1f50b2a46500159`](https://github.com/nwesterhausen/valheim-discordconnector/commit/79906411cf8f20b00fa66a2ae1f50b2a46500159): `ZNetPatches.cs`, the four startup harness files and `build-on-push-pr.yml`. | Crossplay waits for the active current session/join code, then sends once; after 180 seconds it warns and suppresses late startup. Stop/destroy cancels pending work; noncrossplay remains immediate. No live crossplay guarantee. |
| Focused checks | **121 independently rerun Release checks passed:** 14 persistence + 30 correlation + 38 adapter + 25 reporting + 14 startup. | Persistence uses real LiteDB with disposable fixtures. Adapter uses **contract-only game/Harmony/plugin/store doubles**. Reporting combines scheduler doubles and real LiteDB tests with captured **production DiscordApi/MessageTransformer/EmbedBuilder/Webhook payload serialization**, including literal combat and unchanged legacy behavior; routes are empty, so **no HTTP requests** occur. Remaining game/config/logging surfaces and startup game/Harmony/clock/sender are stubbed. No production DB used ([reporting harness](../tests/CombatReporting/CombatReporting.csproj), [payload checks](../tests/CombatReporting/Program.cs#L260-L300)). |
| Real server build | Official Steam dedicated-server **1.0.20, build 25909431**, publicized game references, net481 Release and ILRepack independently verified: **0 errors, 10 existing nullable warnings**; packages succeeded. | Supersedes earlier 0.221.12-reference compilation. Steam's current version is **not the user's confirmed Docker version**. Compilation is not runtime integration. |
| Unchecked | No live game, Harmony patch sequencing, simulation-mod integration, PlayFab/crossplay or Discord delivery validation. | Final source/artifact verification and manual staging remain necessary before rollout. |

Harness commands from the repository root (SDK/runtime requirements and limits: [persistence](../tests/CombatStatistics/README.md), [tracking](../tests/CombatTracking/README.md), [startup](../tests/ServerStartRegression/README.md)):

```sh
dotnet run --project tests/CombatStatistics/CombatStatistics.csproj --configuration Release
dotnet run --project tests/CombatTracking/CombatTracking.csproj --configuration Release
dotnet run --project tests/CombatReporting/CombatReporting.csproj --configuration Release
dotnet run --project tests/ServerStartRegression/ServerStartRegression.csproj --configuration Release
```

Local validation used `DOTNET_ROLL_FORWARD=Major` because only a newer runtime was installed. These net8 harnesses do not replace the net481 plugin build. For a normal local build, prepare compatible publicized Valheim assemblies plus BepInEx/Unity dependencies, then follow the repository's [build setup](../README.md#building) and [path derivation](../DeterminePaths.targets):

```sh
dotnet build DiscordConnector.sln --configuration Release /p:GamePath="<local-compatible-Valheim-install>"
```

Use a disposable local installation for building/testing, not a production mount. Temporary local build scripts and extraction paths are **not portable deliverables**. Use the final reviewed/repacked `DiscordConnector.dll`, not an arbitrary intermediate build.

## Manual-only Docker staging and rollout

For the user's **And The Norse** server in `valheim-server` Docker, with BepInEx and crossplay. These are operator steps, **not actions already executed**. Do not assume any fixed host filesystem path or infer the installed game version from the build above.

1. **Inspect first:** identify the actual container/image, host volume/bind mounts, mounted BepInEx config/plugin directories, current DLL location, game version, BepInEx version and simulation-mod version/config. Verify compatibility with the build and mod source; stop if it is unknown. Do not expose config secrets in logs or screenshots.
2. **Clean stop, then consistent backups:** shut the server down normally and confirm it has exited before copying files. Back up the complete config directory, `records.db` **plus any companion log/journal files**, all existing database backups, the installed plugin DLL and any identified duplicate versions. Preserve worlds and their existing backups; do not delete records, replace worlds or copy an open live database as a “consistent” backup.
3. **Stage a separate copy first:** test against an isolated copy with separate volumes, not the production world/database, and verify game/mod/BepInEx compatibility. Disable outbound webhook routes for initial checks. No real Discord delivery is validated by this document or the harnesses.
4. **Install only the final DLL:** copy only final `DiscordConnector.dll` into the **actual mounted `BepInEx/plugins` path**. Do not copy game/reference assemblies or a database from the build. Remove a conflicting duplicate plugin version only after identifying and backing up its exact path; never broadly delete plugins.
5. **Start with all new flags off:** retain the generated defaults above. On staging, confirm the plugin loads, existing history/stats remain available, and PR421 emits startup only once the current crossplay session is active with its ready join code. Any optional end-to-end Discord smoke test is a separate operator-controlled action; do not expose the code/webhook in public diagnostics.
6. **Opt in deliberately:** cleanly stop staging, edit the **native** `nwesterhausen.DiscordConnector.cfg`, and restart with `Enabled = true` only alongside a compatible enabled simulation mod. Enable daily/weekly reports only if wanted, verify timezone/top count, and allow `leaderboardsAll` in the existing route. Leave unsupported metrics off/unimplemented; this does not make combat complete.
7. **Verify staged behavior:** use an attributable player kill of a confirmed server-owned untamed NPC, plus a negative environmental/attacker-less case that must not gain player credit. Confirm persistence across a clean restart and use the persistence harness for replay/dedup proof; an ordinary restart alone does not re-create the same victim event. Exercise daily/weekly/DST logic through the harnesses—**never manipulate the live server clock** to force a report. Unexpected results block rollout.
8. **Production is a separate manual decision:** only after staging succeeds, repeat clean-stop/backup/install checks against the identified production mounts. Keep new flags off initially and enable them only after the same compatibility/smoke checks. No production/container/world/config commands or real webhook requests were executed for this work.

### Rollback

Cleanly stop the server, restore the identified previous DLL and matching configuration, remove only the identified backed-up conflicting version, then restart and verify legacy behavior. Normally **retain the additive database**: the old plugin can continue using its legacy collections while new collections remain unused. Do not blindly overwrite a live or newer database. Restore a database only as a deliberate recovery operation from a known consistent, matching backup set (including required companion files), accepting any loss of post-backup events; preserve the current files first.
