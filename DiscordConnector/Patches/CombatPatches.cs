using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

using BepInEx.Bootstrap;

using DiscordConnector.Records;

using HarmonyLib;

namespace DiscordConnector.Patches;

// Observations of server-owned creatures only, not complete world kill coverage.
internal static class CombatPatches
{
    private const string SimulationMod = "MVP.Valheim_Serverside_Simulations";
    [ThreadStatic] private static CombatCorrelation<DeathData>? _correlation;
    private static CombatCorrelation<DeathData> Correlation => _correlation ??= new();

    internal sealed class DeathData
    {
        internal CombatSnapshot Snapshot = null!;
        internal string Creature = "";
        internal string Biome = "";
        internal bool IsBoss;
    }

    internal sealed class Observation
    {
        internal CombatCorrelation<DeathData>.Invocation Invocation = null!;
        internal DeathData? Death;
    }

    private static bool Enabled()
    {
        var net = ZNet.instance;
        if (DiscordConnectorPlugin.StaticCombatConfig?.Enabled != true || !net ||
            !net.IsDedicated() || !net.IsServer() ||
            !Chainloader.PluginInfos.TryGetValue(SimulationMod, out var plugin)) return false;
        if (plugin.Instance != null)
            foreach (var setting in plugin.Instance.Config)
                if (setting.Key.Section == "General" && setting.Key.Key == "Enabled")
                    return setting.Value.BoxedValue is bool enabled && enabled;
        // Mod presence is only eligibility; every observation also verifies actual ownership.
        return true;
    }

    private static string WorldId() => ZNet.instance.GetWorldUID().ToString(CultureInfo.InvariantCulture);

    private static CombatSnapshot Snapshot(Character victim)
    {
        var net = ZNet.instance;
        var view = victim.m_nview;
        var zdo = view ? view.GetZDO() : null;
        var id = victim.GetZDOID();
        long owner = zdo?.GetOwner() ?? 0;
        bool eligible = Enabled() && zdo != null && id.ID != 0 && id.UserID != 0 &&
            net.GetWorldUID() != 0 && owner == ZNet.GetUID() && view != null && view.IsOwner() &&
            !victim.IsPlayer() && !victim.IsTamed();
        return new CombatSnapshot(WorldId(), id.ID == 0 ? "" : id.ToString(), owner, victim.GetHealth(), eligible, DateTime.UtcNow);
    }

    private static bool Player(string attackerId, out string playerId, out string playerName)
    {
        playerId = "";
        playerName = "";
        ZNetPeer? match = null;
        foreach (var peer in ZNet.instance.GetPeers())
        {
            if (peer.m_characterID.ID == 0 || peer.m_characterID.ToString() != attackerId) continue;
            // Ambiguous mappings are rejected even if one matching peer is disconnected.
            if (match != null) return false;
            match = peer;
        }
        if (match == null || match.m_server || !match.IsReady() || match.m_socket == null ||
            !match.m_socket.IsConnected()) return false;
        playerId = match.m_socket.GetHostName();
        playerName = match.m_playerName;
        if (string.IsNullOrWhiteSpace(playerId) || string.IsNullOrWhiteSpace(playerName)) return false;
        // A second connected character using the same stable identity is also ambiguous.
        foreach (var peer in ZNet.instance.GetPeers())
            if (!ReferenceEquals(peer, match) && peer.IsReady() && peer.m_socket != null &&
                peer.m_socket.IsConnected() && peer.m_socket.GetHostName() == playerId) return false;
        return true;
    }

    private static void Failed(Exception error)
    {
        _correlation?.Clear();
        // Exception messages may contain account IDs or database paths.
        DiscordConnectorPlugin.StaticLogger.LogError($"Combat observation failed ({error.GetType().Name}); attribution dropped.");
    }

    [HarmonyPatch(typeof(Character), nameof(Character.ApplyDamage))]
    internal static class Damage
    {
        [HarmonyPrefix, HarmonyPriority(Priority.Last)]
        internal static void Prefix(Character __instance, HitData hit, out Observation? __state)
        {
            __state = null;
            try
            {
                if (!Enabled()) { _correlation?.Clear(); return; }
                var before = Snapshot(__instance);
                string attacker = hit != null && hit.m_attacker.ID != 0 && hit.m_attacker.UserID != 0
                    ? hit.m_attacker.ToString() : "";
                if (!Player(attacker, out string playerId, out string playerName)) playerId = "";
                __state = new Observation { Invocation = Correlation.BeginDamage(before, attacker, playerId, playerName) };
            }
            catch (Exception error) { Failed(error); }
        }

        [HarmonyPostfix, HarmonyPriority(Priority.Last)]
        internal static void Postfix(Character __instance, Observation? __state, bool __runOriginal)
        {
            if (__state == null || !__runOriginal) return;
            try
            {
                CombatSnapshot? after = __instance && __instance.m_nview && __instance.m_nview.IsValid()
                    ? Snapshot(__instance) : null;
                Correlation.DamageApplied(__state.Invocation, after);
            }
            catch (Exception error) { Failed(error); }
        }

        [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
        internal static void Finalizer(Observation? __state, Exception? __exception) => Finish(__state, __exception);
    }

    [HarmonyPatch(typeof(Character), nameof(Character.OnDeath))]
    internal static class Death
    {
        [HarmonyPrefix, HarmonyPriority(Priority.Last)]
        internal static void Prefix(Character __instance, out Observation? __state)
        {
            __state = null;
            try
            {
                if (!Enabled()) { _correlation?.Clear(); return; }
                var before = Snapshot(__instance);
                var biome = before.Valid ? Heightmap.FindBiome(__instance.transform.position) : Heightmap.Biome.None;
                // OnDeath destroys/reset its view before returning; never re-read it in the postfix.
                var data = new DeathData
                {
                    Snapshot = before,
                    Creature = before.Valid ? __instance.m_nview.GetPrefabName() : "",
                    Biome = biome == Heightmap.Biome.None ? "" : biome.ToString(),
                    IsBoss = before.Valid && __instance.IsBoss()
                };
                __state = new Observation { Invocation = Correlation.BeginDeath(before), Death = data };
            }
            catch (Exception error) { Failed(error); }
        }

        [HarmonyPostfix, HarmonyPriority(Priority.Last)]
        internal static void Postfix(Observation? __state, bool __runOriginal)
        {
            if (__state?.Death == null || !__runOriginal) return;
            try
            {
                var data = __state.Death;
                if (string.IsNullOrWhiteSpace(data.Creature) || string.IsNullOrWhiteSpace(data.Biome)) return;
                Correlation.DeathCompleted(__state.Invocation, data.Snapshot, data);
            }
            catch (Exception error) { Failed(error); }
        }

        [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
        internal static void Finalizer(Observation? __state, Exception? __exception) => Finish(__state, __exception);
    }

    private static void Finish(Observation? state, Exception? error)
    {
        if (state == null) return;
        try
        {
            foreach (var kill in Correlation.End(state.Invocation, error == null))
            {
                if (!Enabled() || !Player(kill.AttackerId, out string playerId, out _) || playerId != kill.PlayerId)
                    continue;
                try
                {
                    var death = kill.Death;
                    DiscordConnectorPlugin.StaticDatabase.Combat.Insert(new CombatRecord
                    {
                        Id = kill.Id, WorldId = kill.Before.WorldId, VictimId = kill.Before.VictimId,
                        PlayerId = kill.PlayerId, PlayerName = kill.PlayerName, Creature = death.Creature,
                        Biome = death.Biome, IsBoss = death.IsBoss, TimestampUtc = death.Snapshot.ObservedUtc
                    });
                }
                catch (Exception failure)
                {
                    DiscordConnectorPlugin.StaticLogger.LogError($"Combat record persistence failed ({failure.GetType().Name}); event dropped.");
                }
            }
        }
        catch (Exception failure) { Failed(failure); }
    }

    [HarmonyPatch(typeof(Character), nameof(Character.SetHealth))]
    internal static class Revival
    {
        [HarmonyPrefix]
        internal static void Prefix(Character __instance, float health)
        {
            try
            {
                if (_correlation != null && ZNet.instance && health > 0 && __instance.GetHealth() <= 0)
                    _correlation.Invalidate(WorldId(), __instance.GetZDOID().ToString());
            }
            catch (Exception error) { Failed(error); }
        }
    }

    [HarmonyPatch]
    internal static class Ownership
    {
        [HarmonyTargetMethods]
        internal static IEnumerable<MethodBase> Targets() => new[]
        {
            AccessTools.Method(typeof(ZDO), nameof(ZDO.SetOwner)),
            AccessTools.Method(typeof(ZDO), nameof(ZDO.SetOwnerInternal))
        };

        [HarmonyPrefix]
        internal static void Prefix(ZDO __instance, long __0)
        {
            try
            {
                if (_correlation != null && ZNet.instance && __instance.GetOwner() != __0)
                    _correlation.Invalidate(WorldId(), __instance.m_uid.ToString());
            }
            catch (Exception error) { Failed(error); }
        }
    }

    [HarmonyPatch(typeof(ZNet), nameof(ZNet.Shutdown))]
    internal static class Reset
    {
        [HarmonyPrefix]
        internal static void Prefix() => _correlation?.Clear();
    }
}
