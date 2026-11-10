using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using DiscordConnector.Leaderboards;
using DiscordConnector.Records;

using HarmonyLib;

namespace DiscordConnector.Patches;

internal static class NativeSignalPatches
{
    private const string NpsOwnerHint = "MidnightsFX.NetworkPerformanceSystem";
    private static readonly int KillHash = "RPC_RegisterKill".GetStableHashCode();
    private static readonly int TriggerHash = "SetTrigger".GetStableHashCode();
    private static readonly int DodgeHash = "RPC_HitWhileDodging".GetStableHashCode();
    private static NativeSignalCollector? collector;
    private static Func<LeaderboardsOptions>? options;
    private static string world = "";
    private static readonly string epoch = Guid.NewGuid().ToString("N");
    private static long generation;
    private static bool available;
    private static string checkedWorld = "", checkedSession = "";
    private static bool checkedAvailable;
    private static readonly ReportMetric[] Metrics = { ReportMetric.ReportedKillCredits, ReportMetric.ObservedConsumableUses, ReportMetric.ReportedDodgeHitNotifications };
    private static readonly Dictionary<ReportMetric, CoverageState> coverage = new();

    internal static void Configure(NativeSignalCollector capture, Func<LeaderboardsOptions> flags)
    {
        Stop(); collector = capture; options = flags;
    }

    internal static void SetWorld(string current)
    {
        if (world == current) return;
        world = current; generation++; coverage.Clear(); collector?.Reset();
        available = current.Length != 0 && current == checkedWorld && checkedAvailable;
    }

    internal static void SetSession(string current, string sessionEpoch, string owner)
    {
        if (current.Length == 0 || sessionEpoch.Length == 0) { SetWorld(""); return; }
        if (checkedWorld == current && checkedSession == sessionEpoch) { SetWorld(current); return; }
        available = checkedAvailable = false;
        generation++; coverage.Clear(); collector?.Reset();
        ActivityPatches.ClearIngress(); ActivityPatches.ResetManualRoutes();
        world = checkedWorld = current; checkedSession = sessionEpoch;
        InspectHooks(owner);
        checkedAvailable = available;
    }

    internal static void InvalidateOptions()
    {
        generation++; coverage.Clear(); ActivityPatches.InvalidateIngress();
    }

    internal static void Stop()
    {
        available = false; options = null; collector = null; world = "";
        checkedWorld = checkedSession = ""; checkedAvailable = false;
        generation++; coverage.Clear(); ActivityPatches.ResetManualRoutes();
    }

    internal static void UpdateCoverage(DateTime utc)
    {
        var runtime = ActivityRuntime.Current;
        if (runtime == null || options == null || world.Length == 0) return;
        LeaderboardsOptions flags = options();
        foreach (var metric in Metrics)
        {
            var state = !flags.Includes(metric) ? CoverageState.Disabled : available || metric == ReportMetric.ReportedKillCredits && SimulationCombatPatches.Present ? CoverageState.Partial : CoverageState.Unavailable;
            if (!coverage.TryGetValue(metric, out var old) || old != state)
            {
                coverage[metric] = state;
                runtime.Collector.Coverage(metric, state, utc, state == CoverageState.Unavailable ? CoverageReason.CapabilityLost : null);
            }
        }
    }

    private static MethodBase? ReceiveTarget() => AccessTools.Method(typeof(ZRoutedRpc), nameof(ZRoutedRpc.RPC_RoutedRPC), new[] { typeof(ZRpc), typeof(ZPackage) });
    private static MethodBase? LocalTarget() => AccessTools.Method(typeof(ZRoutedRpc), nameof(ZRoutedRpc.InvokeRoutedRPC),
        new[] { typeof(long), typeof(ZDOID), typeof(string), typeof(object[]) });
    private static MethodBase? RouteTarget(string name) => AccessTools.Method(typeof(ZRoutedRpc), name, new[] { typeof(ZRoutedRpc.RoutedRPCData) });

    private static bool Enabled(LeaderboardsOptions flags, ReportedSignalKind kind) => kind == ReportedSignalKind.KillCredit ?
        flags.ReportedKillCredits : kind == ReportedSignalKind.ConsumableUse ? flags.ConsumableUses : flags.ReportedDodgeHits;

    private static RoutingObservationKind RoutingKind(ReportedSignalKind kind) => kind == ReportedSignalKind.KillCredit ?
        RoutingObservationKind.KillCredit : kind == ReportedSignalKind.ConsumableUse ? RoutingObservationKind.ConsumableUse : RoutingObservationKind.DodgeHitNotification;

    private static readonly string[] NpsOrder = { NpsOwnerHint };

    private static string? OwnHooks(IEnumerable<Patch> installed, string owner, Type[] hooks, string stage)
    {
        var own = installed.Where(p => p.owner == owner).ToArray();
        var expected = stage == "Postfix" ? hooks.Where(h => h.GetMethod(stage, BindingFlags.NonPublic | BindingFlags.Static) != null).ToArray() : hooks;
        if (own.Length != expected.Length) return $"own-hook-count stage={stage} expected={expected.Length} actual={own.Length}";
        foreach (Type hook in expected)
        {
            var method = hook.GetMethod(stage, BindingFlags.NonPublic | BindingFlags.Static);
            if (method == null) return $"missing-hook-method hook={hook.Name}.{stage}";
            var patch = own.SingleOrDefault(p => p.PatchMethod == method);
            bool before = stage == "Prefix" && hook != typeof(ActivityPatches.Routed);
            bool after = stage == "Finalizer" && (hook == typeof(ActivityPatches.ManualReceive) || hook == typeof(Decoded) || hook == typeof(Local) || hook == typeof(BossDefeatPatches.Decoded));
            if (patch == null) return $"missing-own-hook hook={hook.Name}.{stage}";
            if (patch.priority != (before ? Priority.First : Priority.Last) ||
                !patch.before.SequenceEqual(before ? NpsOrder : Array.Empty<string>()) ||
                !patch.after.SequenceEqual(after ? NpsOrder : Array.Empty<string>()))
                return $"own-hook-order hook={hook.Name}.{stage} {Constraints(patch)} expectedPriority={(before ? Priority.First : Priority.Last)}" +
                    $" expectedBefore=[{(before ? NpsOwnerHint : "")}] expectedAfter=[{(after ? NpsOwnerHint : "")}]";
        }
        return null;
    }

    private static string Constraints(Patch patch) =>
        $"owner={patch.owner} priority={patch.priority} before=[{string.Join(",", patch.before)}] after=[{string.Join(",", patch.after)}]";

    // Inventory the installed hooks, never target/patch factories. Unknown replacement IL is not a supported path.
    internal static void InspectHooks(string ownOwner)
    {
        available = false;
        int targets = 0; bool knownOwner = false;
        string inspectedMethod = "unresolved", foreignOwners = "";
        try
        {
            var required = new[] {
                (nameof(ZRoutedRpc.RPC_RoutedRPC), ReceiveTarget(), new[] { typeof(ActivityPatches.ManualReceive), typeof(ActivityPatches.Routed), typeof(ActivityPatches.EmoteData) }),
                (nameof(ZRoutedRpc.RouteRPC), RouteTarget(nameof(ZRoutedRpc.RouteRPC)), new[] { typeof(Decoded), typeof(ActivityPatches.ManualDecodedChat) }),
                (nameof(ZRoutedRpc.HandleRoutedRPC), RouteTarget(nameof(ZRoutedRpc.HandleRoutedRPC)), new[] { typeof(Decoded), typeof(ActivityPatches.ManualDecodedChat), typeof(BossDefeatPatches.Decoded) }),
                (nameof(ZRoutedRpc.InvokeRoutedRPC), LocalTarget(), new[] { typeof(Local) })
            };
            bool supported = true;
            void Reject(string condition)
            {
                supported = false;
                ManualReportDiagnostics.NativeCapabilityTarget(inspectedMethod, condition, foreignOwners);
            }
            foreach (var (name, method, hooks) in required)
            {
                inspectedMethod = nameof(ZRoutedRpc) + "." + name; foreignOwners = "";
                if (method == null) { Reject("missing-target"); continue; }
                if (method is not MethodInfo info || info.ReturnType != typeof(void)) { Reject("unexpected-target-signature expectedReturn=void"); continue; }
                targets++;
                var patches = Harmony.GetPatchInfo(method);
                if (patches == null) { Reject("missing-patch-info"); continue; }
                foreignOwners = string.Join(",", patches.Prefixes.Concat(patches.Postfixes).Concat(patches.Finalizers).Concat(patches.Transpilers)
                    .Where(p => p.owner != ownOwner).Select(p => p.owner).Distinct().OrderBy(owner => owner, StringComparer.Ordinal));
                string? failure = patches.Transpilers.Count != 0 ? "transpiler-present owners=[" + string.Join(",", patches.Transpilers.Select(p => p.owner).Distinct()) + "]" :
                    OwnHooks(patches.Prefixes, ownOwner, hooks, "Prefix") ?? OwnHooks(patches.Postfixes, ownOwner, hooks, "Postfix") ??
                    OwnHooks(patches.Finalizers, ownOwner, hooks, "Finalizer");
                if (failure != null) { Reject(failure); continue; }
                Type hook = hooks[0];
                var prefix = patches.Prefixes.Single(p => p.owner == ownOwner && p.PatchMethod == hook.GetMethod("Prefix", BindingFlags.NonPublic | BindingFlags.Static));
                var finalizer = patches.Finalizers.Single(p => p.owner == ownOwner && p.PatchMethod == hook.GetMethod("Finalizer", BindingFlags.NonPublic | BindingFlags.Static));
                if (patches.Postfixes.Any(p => p.owner != ownOwner))
                    failure = "foreign-postfix owners=[" + string.Join(",", patches.Postfixes.Where(p => p.owner != ownOwner).Select(p => p.owner).Distinct()) + "]";
                // Pinned NPS cleanup returns the original exception; retain its ordering checks below.
                else if (patches.Finalizers.Any(p => p.owner != ownOwner && (p.owner != NpsOwnerHint || hook == typeof(Local))))
                    failure = "foreign-finalizer owners=[" + string.Join(",", patches.Finalizers.Where(p => p.owner != ownOwner).Select(p => p.owner).Distinct()) + "]";
                foreach (var patch in patches.Prefixes.Concat(patches.Postfixes).Concat(patches.Finalizers))
                {
                    if (patch.owner == ownOwner) continue;
                    // The source GUID supplies a hint only: the actual installed owner and constraints must agree.
                    if (patch.owner != NpsOwnerHint || hook == typeof(Local) || patch.priority >= prefix.priority)
                    {
                        failure ??= (patch.owner != NpsOwnerHint ? "foreign-owner" : hook == typeof(Local) ? "foreign-local-hook" : "foreign-priority") +
                            $" {Constraints(patch)} expectedOwner={NpsOwnerHint} expectedPriorityBelow={prefix.priority}";
                        continue;
                    }
                    knownOwner = true;
                    if (!prefix.before.Contains(patch.owner) || prefix.after.Contains(patch.owner) ||
                        !finalizer.after.Contains(patch.owner) || finalizer.before.Contains(patch.owner) ||
                        patch.before.Contains(ownOwner) && patches.Prefixes.Contains(patch) ||
                        patch.after.Contains(ownOwner) && patches.Finalizers.Contains(patch))
                        failure ??= $"foreign-order {Constraints(patch)} expectedBeforeOwn=False expectedFinalizerAfterOwn=False ownOwner={ownOwner}";
                }
                if (failure != null) Reject(failure);
            }
            available = supported && targets == 4;
        }
        catch (Exception error)
        {
            available = false;
            ManualReportDiagnostics.NativeCapabilityTarget(inspectedMethod, "inspection-exception type=" + error.GetType().Name, foreignOwners);
        }
        ManualReportDiagnostics.NativeCapability(available, targets, knownOwner);
    }

    [HarmonyPatch]
    internal static class Local
    {
        internal sealed class Capture
        {
            internal ManualCommandRouting.Frame? Frame;
            internal Capture? Previous;
            internal long Recipient;
            internal ZDOID Character;
            internal string Method = "";
        }
        internal static Capture? Current;
        [HarmonyTargetMethods]
        internal static IEnumerable<MethodBase> Targets()
        { var target = LocalTarget(); if (target != null) yield return target; }

        [HarmonyPrefix, HarmonyPriority(Priority.First), HarmonyBefore(NpsOwnerHint)]
        internal static void Prefix(long __0, ZDOID __1, string __2, out Capture? __state, object[]? __3 = null)
        {
            __state = null;
            try
            {
                // Every nested native invocation masks its parent's provenance, even when irrelevant/disabled.
                __state = new Capture { Previous = Current, Recipient = __0, Character = __1, Method = __2 };
                Current = __state;
                bool genuine = false;
                try
                {
                    genuine = collector != null && options != null && available && __0 != 0 &&
                        StablePlayerResolver.World(out string current) && current == world &&
                        (__2 == "RPC_RegisterKill" && __1.IsNone() && options().ReportedKillCredits ||
                         __2 == "RPC_HitWhileDodging" && !__1.IsNone() && __3?.Length == 0 && options().ReportedDodgeHits);
                }
                finally { __state.Frame = genuine ? ActivityPatches.ManualRoutes.EnterLocal(ZNet.GetUID()) : ActivityPatches.ManualRoutes.Enter(null); }
                if (genuine) ManualReportDiagnostics.SignalProbe(NativeSignalProbe.Local);
            }
            catch (Exception error) { ManualReportDiagnostics.Emit(ManualReportStage.NativeSignal, ManualReportReason.Failed, error); }
        }

        [HarmonyFinalizer, HarmonyPriority(Priority.Last), HarmonyAfter(NpsOwnerHint)]
        internal static void Finalizer(Capture? __state, Exception? __exception)
        {
            if (__state == null) return;
            try { ActivityPatches.ManualRoutes.Finish(__state.Frame, __exception); }
            catch (Exception error) { ManualReportDiagnostics.Emit(ManualReportStage.NativeSignal, ManualReportReason.Failed, error); }
            finally { Current = __state.Previous; }
        }
    }

    [HarmonyPatch]
    internal static class Decoded
    {
        [HarmonyTargetMethods]
        internal static IEnumerable<MethodBase> Targets()
        {
            var route = RouteTarget(nameof(ZRoutedRpc.RouteRPC)); if (route != null) yield return route;
            var handle = RouteTarget(nameof(ZRoutedRpc.HandleRoutedRPC)); if (handle != null) yield return handle;
        }

        [HarmonyPrefix, HarmonyPriority(Priority.First), HarmonyBefore(NpsOwnerHint)]
        internal static void Prefix(ZRoutedRpc.RoutedRPCData __0, out ManualCommandRouting.Observation? __state)
        {
            __state = null;
            try
            {
                if (__0 == null || collector == null || options == null) return;
                ReportedSignalKind kind;
                if (__0.m_methodHash == KillHash) kind = ReportedSignalKind.KillCredit;
                else if (__0.m_methodHash == TriggerHash) kind = ReportedSignalKind.ConsumableUse;
                else if (__0.m_methodHash == DodgeHash) kind = ReportedSignalKind.DodgeHitNotification;
                else return;
                var flags = options();
                bool enabled = Enabled(flags, kind);
                if (!enabled) { ManualReportDiagnostics.SignalProbe(NativeSignalProbe.Disabled); return; }
                if (!available) { ManualReportDiagnostics.SignalProbe(NativeSignalProbe.Unavailable); return; }
                ZPackage payload = __0.m_parameters;
                if (payload == null || !NativeSignalCollector.ValidPayload(kind, enabled, payload.Size(), () => payload.GetArray()))
                { ManualReportDiagnostics.SignalProbe(NativeSignalProbe.Payload); return; }
                ManualReportDiagnostics.SignalProbe(NativeSignalProbe.Decoded);
                var routes = ActivityPatches.ManualRoutes;
                var frame = routes.Current;
                bool local = frame != null && frame.ServerUid != 0;
                if (kind == ReportedSignalKind.KillCredit && local && SimulationCombatPatches.SuppressReportedKill) return;
                if (local && (Local.Current == null || !ReferenceEquals(Local.Current.Frame, frame) ||
                    Local.Current.Recipient != __0.m_targetPeerID || Local.Current.Character != __0.m_targetZDO ||
                    Local.Current.Method.GetStableHashCode() != __0.m_methodHash)) return;
                object? transport = frame?.Transport;
                long server = ZNet.GetUID(), claimed = __0.m_senderPeerID, recipient = __0.m_targetPeerID, message = __0.m_msgID;
                ZDOID nativeCharacter = __0.m_targetZDO;
                var character = (nativeCharacter.UserID, nativeCharacter.ID);
                DateTime utc = DateTime.UtcNow;
                if (!StablePlayerResolver.World(out string current) || current != world ||
                    local && frame!.ServerUid != server) { ManualReportDiagnostics.SignalProbe(NativeSignalProbe.Identity); return; }
                if (!StablePlayerResolver.NativeSignalSnapshot(utc, out var peers, out var characters))
                { ManualReportDiagnostics.SignalProbe(NativeSignalProbe.Identity); return; }
                NativeSignalPeer? sender, target;
                NativeDodgeTarget? dodge = null;
                if (kind == ReportedSignalKind.DodgeHitNotification)
                {
                    if (!StablePlayerResolver.NativeDodgeOwner(nativeCharacter, out long owner) ||
                        !NativeSignalCollector.TryDodgeIdentity(local, transport, server, claimed, recipient, character, owner, peers, characters, out sender, out dodge))
                    { ManualReportDiagnostics.SignalProbe(NativeSignalProbe.Identity); return; }
                    target = dodge!.Beneficiary;
                }
                else if (!NativeSignalCollector.TryIdentity(kind, local, transport, server, claimed, recipient, character, peers, characters, out sender, out target))
                { ManualReportDiagnostics.SignalProbe(NativeSignalProbe.Identity); return; }
                var capture = collector;
                long version = generation;
                var net = ZNet.instance; var nativeWorld = ZNet.World; var zdos = ZDOMan.instance;
                string sessionEpoch = checkedSession;
                string session = local ? (kind == ReportedSignalKind.DodgeHitNotification ? checkedSession : epoch) + ":" + ObservationValues.Number(server) : sender!.Actor!.ConnectionToken;
                var principal = new ObservationPrincipal(local ? "server" : sender!.Actor!.PlayerId, session, local);
                string token = NativeSignalCollector.Token(kind, current, local, session, message, target!);
                string targetKey = ObservationValues.Key(target!.Actor!.PlayerId, ObservationValues.Number(target.Character.User), ObservationValues.Number(target.Character.Id));
                if (dodge != null)
                {
                    token = ObservationValues.Key(token, ObservationValues.Number(dodge.OwnerUid));
                    targetKey = ObservationValues.Key(targetKey, ObservationValues.Number(dodge.OwnerUid));
                }
                var record = new ReportedSignalRecord(kind, current, target.Actor!, token, utc);
                Action commit = () =>
                {
                    if (collector != capture || generation != version || world != current || options == null ||
                        !Enabled(options(), kind) ||
                        !available || !StablePlayerResolver.World(out string liveWorld) || liveWorld != current || ZNet.GetUID() != server)
                    { capture.Reject(record, ObservationRejection.Stale); ManualReportDiagnostics.SignalProbe(NativeSignalProbe.Stale); return; }
                    if (dodge != null && (sessionEpoch != checkedSession || !ReferenceEquals(net, ZNet.instance) ||
                        !ReferenceEquals(nativeWorld, ZNet.World) || !ReferenceEquals(zdos, ZDOMan.instance)))
                    { capture.Reject(record, ObservationRejection.Stale); ManualReportDiagnostics.SignalProbe(NativeSignalProbe.Stale); return; }
                    if (!StablePlayerResolver.NativeSignalSnapshot(DateTime.UtcNow, out var livePeers, out var liveCharacters))
                    { capture.Reject(record, ObservationRejection.Stale); ManualReportDiagnostics.SignalProbe(NativeSignalProbe.Stale); return; }
                    NativeSignalPeer? liveSender, liveTarget;
                    if (dodge != null)
                    {
                        if (!StablePlayerResolver.NativeDodgeOwner(nativeCharacter, out long liveOwner) ||
                            !NativeSignalCollector.TryDodgeIdentity(local, transport, server, claimed, recipient, character, liveOwner,
                                livePeers, liveCharacters, out liveSender, out var liveDodge) ||
                            liveDodge!.OwnerUid != dodge.OwnerUid || !NativeSignalCollector.SamePeer(dodge.Owner, liveDodge.Owner))
                        { capture.Reject(record, ObservationRejection.Stale); ManualReportDiagnostics.SignalProbe(NativeSignalProbe.Stale); return; }
                        liveTarget = liveDodge.Beneficiary;
                    }
                    else if (!NativeSignalCollector.TryIdentity(kind, local, transport, server, claimed, recipient, character,
                        livePeers, liveCharacters, out liveSender, out liveTarget))
                    { capture.Reject(record, ObservationRejection.Stale); ManualReportDiagnostics.SignalProbe(NativeSignalProbe.Stale); return; }
                    if (!NativeSignalCollector.IsCurrent(current, version, sender, target, liveWorld, generation, liveSender, liveTarget))
                    { capture.Reject(record, ObservationRejection.Stale); ManualReportDiagnostics.SignalProbe(NativeSignalProbe.Stale); return; }
                    capture.Observe(record, principal);
                };
                __state = local ? routes.ObserveLocalSignal(claimed, session, current, message, targetKey, RoutingKind(kind), ActivityRuntime.Now, commit, principal) :
                    routes.ObserveSignal(claimed, sender!.Uid, session, current, message, targetKey,
                        RoutingKind(kind), ActivityRuntime.Now, commit, principal);
            }
            catch (Exception error) { ManualReportDiagnostics.Emit(ManualReportStage.NativeSignal, ManualReportReason.Failed, error); }
        }

        [HarmonyFinalizer, HarmonyPriority(Priority.Last), HarmonyAfter(NpsOwnerHint)]
        internal static void Finalizer(ManualCommandRouting.Observation? __state, Exception? __exception)
        {
            // A copied decoded witness is sufficient even if NPS skipped vanilla; exceptions still reject the root.
            try { ActivityPatches.ManualRoutes.Complete(__state, __exception); }
            catch (Exception error) { ManualReportDiagnostics.Emit(ManualReportStage.NativeSignal, ManualReportReason.Failed, error); }
        }
    }
}
