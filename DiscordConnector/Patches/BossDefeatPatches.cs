using System;

using DiscordConnector.Leaderboards;
using DiscordConnector.Records;

using HarmonyLib;

namespace DiscordConnector.Patches;

internal static class BossDefeatPatches
{
    // The handler needs the actual received transport and decoded envelope, not a claimed peer ID alone.
    [HarmonyPatch(typeof(ZRoutedRpc), nameof(ZRoutedRpc.HandleRoutedRPC))]
    internal static class Decoded
    {
        [ThreadStatic] internal static ZRoutedRpc.RoutedRPCData? Current;
        [HarmonyPrefix, HarmonyPriority(Priority.First), HarmonyBefore("MidnightsFX.NetworkPerformanceSystem")]
        internal static void Prefix(ZRoutedRpc.RoutedRPCData __0, out ZRoutedRpc.RoutedRPCData? __state)
        {
            __state = Current;
            Current = __0 != null && __0.m_methodHash == "SetGlobalKey".GetStableHashCode() && __0.m_targetZDO.IsNone() ? __0 : null;
        }
        [HarmonyFinalizer, HarmonyPriority(Priority.Last), HarmonyAfter("MidnightsFX.NetworkPerformanceSystem")]
        internal static void Finalizer(ZRoutedRpc.RoutedRPCData? __state) => Current = __state;
    }

    [HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.RPC_SetGlobalKey), typeof(long), typeof(string))]
    internal static class GlobalKey
    {
        [HarmonyPrefix]
        internal static void Prefix(long __0, string __1, out ManualCommandRouting.Observation? __state)
        {
            __state = null;
            var runtime = ActivityRuntime.Current;
            if (runtime == null || !runtime.Options.BossKills || !ActivityCollector.IsBossKey(__1)) return;
            try
            {
                var envelope = Decoded.Current;
                var routes = ActivityPatches.ManualRoutes;
                DateTime utc = DateTime.UtcNow; double time = ActivityRuntime.Now;
                if (envelope == null || envelope.m_senderPeerID != __0 || envelope.m_targetPeerID != ZNet.GetUID() ||
                    routes.Current?.Transport is not ZRpc rpc || !StablePlayerResolver.World(out string world) ||
                    !StablePlayerResolver.Transport(rpc, utc, out var peer, out var actor) || peer == null || actor == null ||
                    peer.m_uid != __0 || !ReferenceEquals(rpc.GetSocket(), peer.m_socket)) return;
                var recipients = runtime.Collector.BossRecipients(actor, utc, time);
                if (recipients.Length == 0) return;
                string key = __1;
                ZDOID character = peer.m_characterID;
                __state = routes.ObserveSignal(__0, peer.m_uid, actor.ConnectionToken, world, envelope.m_msgID, key,
                    RoutingObservationKind.BossDefeat, time, () =>
                    {
                        if (!ReferenceEquals(ActivityRuntime.Current, runtime) || !runtime.Options.BossKills || !StablePlayerResolver.World(out string current) || current != world ||
                            peer.m_characterID != character || !StablePlayerResolver.Resolve(character, DateTime.UtcNow, out var live) || live?.ConnectionToken != actor.ConnectionToken) return;
                        runtime.Collector.ObserveBoss(world, key, recipients, utc, time);
                    }, new ObservationPrincipal(actor.PlayerId, actor.ConnectionToken, false));
            }
            catch (Exception error) { ActivityPatches.Fail(error); }
        }

        [HarmonyPostfix]
        internal static void Postfix(ManualCommandRouting.Observation? __state, bool __runOriginal)
        {
            try { if (__runOriginal) ActivityPatches.ManualRoutes.Complete(__state, null); }
            catch (Exception error) { ActivityPatches.Fail(error); }
        }

        [HarmonyFinalizer]
        internal static void Finalizer(ManualCommandRouting.Observation? __state, Exception? __exception)
        {
            try
            {
                if (__exception != null) ActivityPatches.ManualRoutes.Complete(__state, __exception);
            }
            catch (Exception error) { ActivityPatches.Fail(error); }
        }
    }
}
