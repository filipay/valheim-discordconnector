# Combat tracking checks

From the repository root:

```sh
DOTNET_ROLL_FORWARD=Major dotnet run --project tests/CombatTracking/CombatTracking.csproj
```

Expected: 30 pure correlation checks and 37 adapter checks pass. The initial red run failed because the production correlation file did not exist; subsequent runs exercise the linked production state and patch code. Adapter game, Harmony, plugin and persistence surfaces are **contract-only doubles**, not runtime integration or durable-store tests. Persistence owns its real LiteDB tests.

## Execution evidence used

Local extraction root: `/private/var/folders/fy/095_frw57s99z6m359s4bxy40000gn/T/opencode/discordconnector-build/decompiled/`.

- `FINDINGS.md:3–15`: official public dedicated-server 1.0.20 executable provenance and original assembly SHA256.
- `Character.cs:2423–2497`: ApplyDamage observes current hit, changes health, then invokes callbacks; no CheckDeath call. The tracker never reads `m_lastHit`.
- `Character.cs:809–846,2872–2910`: owner updates and deferred/animated death ordering.
- `Character.cs:2913–3005`: OnDeath owner guard, callbacks and destruction; immutable death metadata is captured in the prefix, never persisted there. Successful postfix observation and successful finalizers are both required.
- `ZNetScene.cs:116–129`, `ZNetView.cs:267–271`: destruction resets the victim view before the OnDeath postfix. A confirmed inside-damage death supplies captured zero-HP evidence when the damage postfix can no longer read that view.
- `Heightmap.FindBiome.cs:3–10`: lookup uses the victim's actual position and requires a loaded heightmap. `None` is skipped; no biome is fabricated. This is loaded-heightmap classification, not a claim about generator/terrain boundaries being identical.
- Earlier signature inventory `discordconnector-api-inspect/api.txt:1408–1420,1564–1565,1584–1586,1614,1710–1738,1889,1923–1937,1244–1248`: peer character identity, server/world identity, ownership, prefab, biome and hit attacker APIs.
- Existing `DiscordConnector/Handlers.cs:12,38`: stable account identity uses the socket hostname, not a displayed name.

## Conservative limits

- Requires opt-in config, dedicated server, simulation-mod GUID, accessible `General.Enabled` not false, actual victim server ownership, untamed nonplayer classification, and one connected peer matching the invocation attacker character ID and stable socket identity. Presence never proves ownership coverage.
- No DoT-origin, contributor, proximity, name, or RPC-sender attribution. No ownership takeover or simulation-mod assembly dependency.
- Any new hit (including a zero-health hit), observed revival or owner transfer invalidates the prior candidate. Thus postzero hits can undercount even though vanilla does not replace its own last-hit field after zero HP.
- Deferred candidates expire after one minute; unusually long modded death animations are skipped. Successful nested damage/death observations wait for all tracked enclosing invocations. Per-thread state assumes the game's usual same-thread callback sequencing; cross-thread mod behavior is unverified.
- The last-priority tracker finalizer preserves game exceptions and never emits on an observed failure. Arbitrary mods running later at equal/lower Harmony priority can still change results or throw; that ordering is not established by these tests.
- Immutable prefix metadata avoids accessing destroyed Unity objects. Normal ownership changes and SetHealth revival are intercepted; mods bypassing those APIs, custom OnDeath overrides that do not call the patched base, or unobserved lifecycle resets are outside proven coverage.
- Durable ID policy belongs to CombatStore: it derives its own world/victim hash, overriding the tracker's transient `world:victim` key. Duplicate false is benign; other storage failures are logged by exception type without IDs or exception messages.

## Actual checks performed

- Harness: 30 correlation + 37 adapter checks passed (67 total).
- `bash /private/var/folders/fy/095_frw57s99z6m359s4bxy40000gn/T/opencode/discordconnector-build/build-server-current.sh`: production net481 Release build against publicized current official 1.0.20 references, no production paths configured. Passed; parent owns final delivery rebuild.
- No live game, Harmony installation, Discord send, production access, commit, push, or branch change. PR421 ZNetPatches was not edited by this lane.
