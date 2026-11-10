using System;
using System.Collections.Generic;
using System.Reflection;

using DiscordConnector.Leaderboards;
using DiscordConnector.Records;

using HarmonyLib;

using UnityEngine;

namespace DiscordConnector.Patches;

internal static class ActivityPatches
{
    internal sealed class BeforeZdo
    {
        internal long Owner;
        internal uint Revision, IncomingRevision;
        internal ushort OwnerRevision;
        internal FloorItemSignal? Item;
    }
    internal sealed class Ingress
    {
        internal Ingress? Previous;
        internal ActivityActor? Actor;
        internal long Sender;
        internal ZDOID Target;
        internal string Token = "";
        internal string World = "";
        internal int MethodHash;
        internal string? CookingItem;
        internal bool Ran, Failed, Data;
        internal ObservationPrincipal? Principal;
        internal double Time;
        internal readonly List<Action> Pending = new();
        internal readonly List<Action> Invalidations = new();
        internal readonly List<Action> Removals = new();
        internal readonly Dictionary<ZDOID, BeforeZdo> Before = new();
    }
    [ThreadStatic] private static Ingress? _ingress;
    [ThreadStatic] private static ManualCommandRouting? _manualRouting;
    internal static ManualCommandRouting ManualRoutes => _manualRouting ??= new ManualCommandRouting(() => ActivityRuntime.Now);
    internal static void ResetManualRoutes() => _manualRouting?.Reset();
    [ThreadStatic] private static int _growth;
    [ThreadStatic] private static int _itemExclusion, _refund;
    private static readonly Dictionary<string, double> Messages = new(StringComparer.Ordinal);
    private static readonly Queue<(string Token, double Time)> MessageOrder = new();
    private static readonly ObservationAdmission RawAdmission = new(7);
    private static double lastRawTime = double.NegativeInfinity;
    internal static ObservationRejection? RawRejection { get; private set; }
    private static readonly int TriggerHash = "SetTrigger".GetStableHashCode();
    private static readonly int RequestOwnHash = "RPC_RequestOwn".GetStableHashCode();
    private static readonly int AddItemHash = "RPC_AddItem".GetStableHashCode();
    internal static Ingress? Sender => _ingress;
    internal static Action<ZRpc, ActivityActor, string, string>? ManualCommand;
    internal static EmoteReportTriggers? EmoteTriggers;

    internal static void ClearIngress()
    {
        InvalidateIngress();
        Messages.Clear(); MessageOrder.Clear(); RawAdmission.Reset(); lastRawTime = double.NegativeInfinity; RawRejection = null;
    }

    internal static void InvalidateIngress()
    {
        for (Ingress? frame = _ingress; frame != null; frame = frame.Previous) frame.Failed = true;
    }

    internal static bool ShouldObserve(int methodHash, LeaderboardsOptions options) =>
        methodHash == TriggerHash && options.DodgeRolls ||
        methodHash == RequestOwnHash && options.FloorPickups ||
        methodHash == AddItemHash && options.CookingCompletions;

    internal static void Stage(Action capture, Action? invalidate = null)
    {
        if (_ingress == null) { capture(); return; }
        if (_ingress.Pending.Count >= 4096) { _ingress.Failed = true; invalidate?.Invoke(); return; }
        _ingress.Pending.Add(capture);
        if (invalidate != null) _ingress.Invalidations.Add(invalidate);
    }

    private static void StageRemoval(Action removed)
    {
        if (_ingress == null) { removed(); return; }
        if (_ingress.Removals.Count < 4096) _ingress.Removals.Add(removed);
        else ActivityRuntime.Current?.Collector.Coverage(ReportMetric.Biomes,
            CoverageState.Partial, DateTime.UtcNow, CoverageReason.Dropped);
    }

    internal static void Fail(Exception error)
    {
        if (_ingress != null) _ingress.Failed = true;
        ActivityRuntime.Current?.Failed(error);
    }

    private static void Finish(Ingress? frame, Exception? error)
    {
        if (frame == null) return;
        _ingress = frame.Previous;
        if (error != null || !frame.Ran || frame.Failed)
        {
            if (_ingress != null) _ingress.Failed = true;
            if (frame.Data) ActivityRuntime.Current?.Collector.DiscardPickupCandidates();
            foreach (var invalidate in frame.Invalidations) invalidate();
            // Native lifecycle proof is independent of pickup sender authentication.
            if (error == null && frame.Ran)
                foreach (var removed in frame.Removals) StageRemoval(removed);
            return;
        }
        if (frame.Principal != null) Stage(() => CommitRaw(frame));
        else foreach (var capture in frame.Pending) Stage(capture);
        if (_ingress != null)
            foreach (var invalidate in frame.Invalidations)
                if (_ingress.Invalidations.Count < 4096) _ingress.Invalidations.Add(invalidate);
                else { _ingress.Failed = true; invalidate(); }
        foreach (var removed in frame.Removals) StageRemoval(removed);
    }

    private static bool ReplayAvailable(string token, out ObservationRejection reason)
    {
        reason = Messages.ContainsKey(token) ? ObservationRejection.Duplicate :
            Messages.Count >= 4096 ? ObservationRejection.ReplayCapacity : ObservationRejection.None;
        return reason == ObservationRejection.None;
    }

    private static bool Available(string token, double time)
    {
        if (!ObservationValues.Finite(time) || time < 0 || time < lastRawTime)
        { RawRejection = ObservationRejection.Stale; return false; }
        lastRawTime = time;
        for (int examined = 0; examined < 32 && MessageOrder.Count != 0; examined++)
        {
            if (time - MessageOrder.Peek().Time <= 7) break;
            Messages.Remove(MessageOrder.Dequeue().Token);
        }
        bool available = ReplayAvailable(token, out var reason);
        RawRejection = available ? null : reason;
        return available;
    }

    private static void CommitRaw(Ingress frame)
    {
        double now = ActivityRuntime.Now, age = now - frame.Time;
        ObservationRejection reason = ObservationRejection.Stale;
        if (!ObservationValues.Finite(age) || now < lastRawTime || age < 0 || age > 5 ||
            !ReplayAvailable(frame.Token, out reason) || !RawAdmission.TryAdmit(frame.Principal!, now, out reason))
        {
            RawRejection = reason;
            RejectRaw(frame, reason);
            foreach (var invalidate in frame.Invalidations) invalidate();
            return;
        }
        Messages.Add(frame.Token, now); MessageOrder.Enqueue((frame.Token, now)); RawRejection = null;
        foreach (var capture in frame.Pending) capture();
    }

    private static void RejectRaw(Ingress frame, ObservationRejection reason) => ActivityRuntime.Current?.Collector.Reject(frame.World,
        frame.MethodHash == TriggerHash ? ReportMetric.ObservedDodgeRolls : frame.MethodHash == RequestOwnHash ?
            ReportMetric.InferredPickups : ReportMetric.CookingCompletions, reason);

    // Peek only the fixed header; relevant envelopes still use an independent GetArray copy.
    [HarmonyPatch(typeof(ZRoutedRpc), nameof(ZRoutedRpc.RPC_RoutedRPC))]
    internal static class Routed
    {
        [HarmonyPrefix, HarmonyPriority(Priority.Last)]
        internal static void Prefix(ZRpc rpc, ZPackage pkg, out Ingress? __state)
        {
            // Even disabled/unavailable ingress must mask an enclosing authenticated sender.
            var frame = new Ingress { Previous = _ingress, Failed = true };
            __state = frame; _ingress = frame;
            ManualReportDiagnostics.Probe(ManualIngressProbe.Received);
            var runtime = ActivityRuntime.Current;
            if (runtime == null) { ManualReportDiagnostics.Probe(ManualIngressProbe.RuntimeUnavailable); return; }
            if (!StablePlayerResolver.World(out string world)) { ManualReportDiagnostics.Probe(ManualIngressProbe.WorldUnavailable); return; }
            int size = 0;
            bool readingHeader = false;
            try
            {
                var options = runtime.Options;
                if (!options.DodgeRolls && !options.FloorPickups && !options.CookingCompletions) return;
                size = pkg.Size();
                if (size > 1024 * 1024)
                { ManualReportDiagnostics.Probe(ManualIngressProbe.SizeOrTransport); return; }
                int position = pkg.GetPos();
                if (position < 0 || position > size || size - position < 40)
                { ManualReportDiagnostics.HeaderProbe(false, size); return; }
                int methodHash;
                readingHeader = true;
                try
                {
                    pkg.ReadLong(); pkg.ReadLong(); pkg.ReadLong(); pkg.ReadLong();
                    pkg.ReadUInt(); methodHash = pkg.ReadInt();
                }
                finally { pkg.SetPos(position); }
                readingHeader = false;
                if (!ShouldObserve(methodHash, options)) return;
                var copy = new ZPackage(pkg.GetArray()); copy.SetPos(pkg.GetPos());
                var data = new ZRoutedRpc.RoutedRPCData { m_msgID = copy.ReadLong(), m_senderPeerID = copy.ReadLong(),
                    m_targetPeerID = copy.ReadLong() };
                if (!ReadKnownId(copy, out data.m_targetZDO)) { ManualReportDiagnostics.Probe(ManualIngressProbe.UnknownId); return; }
                data.m_methodHash = copy.ReadInt();
                if (!ShouldObserve(data.m_methodHash, options)) return;
                if (!PackageFits(copy)) { ManualReportDiagnostics.Probe(ManualIngressProbe.PackageBounds); return; }
                data.m_parameters = copy.ReadPackage();
                if (copy.GetPos() != copy.Size() ||
                    !StablePlayerResolver.Transport(rpc, DateTime.UtcNow, out var peer, out var actor) ||
                    peer == null || actor == null || data.m_senderPeerID != peer.m_uid ||
                    data.m_targetPeerID != 0 && data.m_targetPeerID != ZNet.GetUID() &&
                    !ReadyTarget(data.m_targetPeerID)) { ManualReportDiagnostics.Probe(ManualIngressProbe.Envelope); return; }
                string token = actor.ConnectionToken + ":" + ObservationValues.Number(data.m_msgID);
                frame.Failed = false; frame.Actor = actor; frame.Sender = peer.m_uid; frame.Target = data.m_targetZDO;
                frame.Token = token; frame.World = world; frame.MethodHash = data.m_methodHash;
                DateTime utc = DateTime.UtcNow; double time = ActivityRuntime.Now;
                var parameters = data.m_parameters;
                if (data.m_methodHash == AddItemHash)
                {
                    string input = parameters.ReadString(); parameters.ReadBool();
                    if (parameters.GetPos() == parameters.Size())
                    { frame.CookingItem = input; frame.Principal = new(actor.PlayerId, actor.ConnectionToken, false); frame.Time = time; }
                    parameters.SetPos(0);
                }
                if (data.m_methodHash == TriggerHash && options.DodgeRolls &&
                    data.m_targetZDO == peer.m_characterID && data.m_targetPeerID == 0 && parameters.ReadString() == "dodge" &&
                    parameters.GetPos() == parameters.Size())
                {
                    frame.Principal = new(actor.PlayerId, actor.ConnectionToken, false); frame.Time = time;
                    var action = new PlayerActionRecord(world, actor, PlayerActionKind.DodgeRoll, token, utc);
                    ZDOID target = data.m_targetZDO;
                    Stage(() => { if (StablePlayerResolver.Resolve(target, utc, out var current) && current!.ConnectionToken == actor.ConnectionToken)
                        runtime.Collector.ObserveAction(action); else runtime.Collector.Reject(world, ReportMetric.ObservedDodgeRolls, ObservationRejection.Stale); });
                }
                else if (data.m_methodHash == RequestOwnHash && options.FloorPickups &&
                    parameters.Size() == 0 && !data.m_targetZDO.IsNone())
                {
                    var zdo = ZDOMan.instance.GetZDO(data.m_targetZDO);
                    if (zdo != null && data.m_targetPeerID == zdo.GetOwner())
                    {
                        var item = Item(zdo, FloorItemEvent.Request, peer.m_uid, token, utc, time, actor);
                        if (item != null)
                        {
                            frame.Principal = new(actor.PlayerId, actor.ConnectionToken, false); frame.Time = time;
                            Stage(() => { if (runtime.Options.FloorPickups) runtime.Collector.ObserveItem(item); });
                        }
                    }
                }
                // Only a validated schema proves a new relevant observation was refused, not arbitrary raw traffic.
                if (frame.Principal != null && !Available(token, time))
                {
                    RejectRaw(frame, RawRejection!.Value); frame.Failed = true; frame.Pending.Clear();
                    ManualReportDiagnostics.Probe(ManualIngressProbe.Replay);
                }
            }
            catch (Exception)
            {
                if (readingHeader) ManualReportDiagnostics.HeaderProbe(true, size);
                ManualReportDiagnostics.Probe(ManualIngressProbe.DecodeFailure);
                if (__state != null) __state.Failed = true;
            }
        }

        [HarmonyPostfix, HarmonyPriority(Priority.Last)]
        internal static void Postfix(Ingress? __state, bool __runOriginal)
        { if (__state != null) __state.Ran = __runOriginal; }

        [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
        internal static void Finalizer(Ingress? __state, Exception? __exception)
        { try { Finish(__state, __exception); } catch (Exception error) { Fail(error); } }

        private static bool ReadyTarget(long uid)
        {
            int matches = 0;
            foreach (var peer in ZNet.instance.GetPeers())
                if (peer.m_uid == uid && !peer.m_server && peer.IsReady() && peer.m_socket != null && peer.m_socket.IsConnected()) matches++;
            return matches == 1;
        }
    }

    // Capture the real connection before NPS can relay from a prefix and skip vanilla receive.
    // This scope deliberately does not depend on the activity observer's raw decoder or Failed flag.
    [HarmonyPatch(typeof(ZRoutedRpc), nameof(ZRoutedRpc.RPC_RoutedRPC), typeof(ZRpc), typeof(ZPackage))]
    internal static class ManualReceive
    {
        [HarmonyPrefix, HarmonyPriority(Priority.First), HarmonyBefore("MidnightsFX.NetworkPerformanceSystem")]
        internal static void Prefix(ZRpc __0, out ManualCommandRouting.Frame? __state)
        {
            __state = null;
            try
            {
                __state = ManualRoutes.Enter(__0);
                ManualReportDiagnostics.SignalProbe(NativeSignalProbe.Receive);
            }
            catch (Exception error) { ManualReportDiagnostics.Emit(ManualReportStage.Enclosing, ManualReportReason.Failed, error); }
        }

        [HarmonyFinalizer, HarmonyPriority(Priority.Last), HarmonyAfter("MidnightsFX.NetworkPerformanceSystem")]
        internal static void Finalizer(ManualCommandRouting.Frame? __state, Exception? __exception)
        {
            try { ManualRoutes.Finish(__state, __exception); }
            catch (Exception error) { ManualReportDiagnostics.Emit(ManualReportStage.Enclosing, ManualReportReason.Failed, error); }
        }
    }

    [HarmonyPatch(typeof(Chat), nameof(Chat.RPC_ChatMessage))]
    internal static class ManualChat
    {
        [HarmonyPrefix]
        internal static void Prefix(int __2, string __4)
        {
            if (__2 == (int)Talker.Type.Shout && (__4 == "!dc daily" || __4 == "!dc weekly"))
                ManualReportDiagnostics.Emit(ManualReportStage.NativeChat, ManualRoutes.Current?.Transport == null ?
                    ManualReportReason.MissingIngress : ManualReportReason.Received);
        }
    }

    [HarmonyPatch]
    internal static class ManualDecodedChat
    {
        [HarmonyTargetMethods]
        internal static IEnumerable<MethodBase> Targets() => new[]
        {
            AccessTools.Method(typeof(ZRoutedRpc), nameof(ZRoutedRpc.RouteRPC)),
            AccessTools.Method(typeof(ZRoutedRpc), nameof(ZRoutedRpc.HandleRoutedRPC))
        };

        // Copy before NPS's High/Normal router prefixes can consume or replace native routing.
        [HarmonyPrefix, HarmonyPriority(Priority.First), HarmonyBefore("MidnightsFX.NetworkPerformanceSystem")]
        internal static void Prefix(ZRoutedRpc.RoutedRPCData __0, out ManualCommandRouting.Observation? __state)
        {
            __state = null;
            try
            {
                if (__0 == null || __0.m_methodHash != "ChatMessage".GetStableHashCode() || !__0.m_targetZDO.IsNone()) return;
                ZPackage parameters = __0.m_parameters;
                if (parameters == null || parameters.Size() < 16 || parameters.Size() > 65536) return;
                var copy = new ZPackage(parameters.GetArray());
                copy.SetPos(0);
                copy.ReadVector3();
                int type = copy.ReadInt();
                new UserInfo().Deserialize(ref copy); // Supplied name/platform identity never authorize a command.
                string text = copy.ReadString();
                if (copy.GetPos() != copy.Size() || type != (int)Talker.Type.Shout || (text != "!dc daily" && text != "!dc weekly")) return;
                ManualReportDiagnostics.Emit(ManualReportStage.Ingress, ManualReportReason.Decoded);
                if (ManualRoutes.Current?.Transport is not ZRpc rpc)
                { ManualReportDiagnostics.Emit(ManualReportStage.Forward, ManualReportReason.MissingIngress); return; }
                if (!StablePlayerResolver.World(out string world) ||
                    !StablePlayerResolver.Transport(rpc, DateTime.UtcNow, out var peer, out var actor) || peer == null || actor == null ||
                    !ReferenceEquals(rpc.GetSocket(), peer.m_socket))
                { ManualReportDiagnostics.Emit(ManualReportStage.Forward, ManualReportReason.InvalidIngress); return; }
                if (__0.m_senderPeerID != peer.m_uid)
                { ManualReportDiagnostics.Emit(ManualReportStage.Forward, ManualReportReason.Mismatch); return; }
                __state = ManualRoutes.Observe(__0.m_senderPeerID, peer.m_uid, actor.ConnectionToken, world, __0.m_msgID,
                    text, UnityEngine.Time.realtimeSinceStartupAsDouble, () =>
                    {
                        ManualReportDiagnostics.Emit(ManualReportStage.Callback, ManualCommand == null ? ManualReportReason.MissingCallback : ManualReportReason.Invoking);
                        ManualCommand?.Invoke(rpc, actor, world, text);
                    });
                if (__state == null) ManualReportDiagnostics.Emit(ManualReportStage.Forward, ManualReportReason.InvalidIngress);
            }
            catch (Exception error) { ManualReportDiagnostics.Emit(ManualReportStage.Forward, ManualReportReason.Failed, error); }
        }

        [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
        internal static void Finalizer(ManualCommandRouting.Observation? __state, Exception? __exception)
        {
            try
            {
                if (__state != null)
                    ManualReportDiagnostics.Emit(ManualReportStage.Forward, __exception != null ? ManualReportReason.Failed :
                        ManualReportReason.Observed, __exception);
                // Skipping vanilla is normal when NPS has already relayed. Actual exceptions still fail closed.
                ManualRoutes.Complete(__state, __exception);
            }
            catch (Exception error) { ManualReportDiagnostics.Emit(ManualReportStage.Forward, ManualReportReason.Failed, error); }
        }
    }

    // Independent scalar-only emote observer. Routed receive is an exception barrier only;
    // it neither decodes chat nor supplies identity to an enclosed ZDOData observation.
    [HarmonyPatch]
    internal static class EmoteData
    {
        internal sealed class Capture
        {
            internal readonly EmoteReportTriggers Helper;
            internal readonly EmoteReportTriggers.Frame? Scope;
            internal readonly bool Data;
            internal bool Ran;
            internal ZRpc? Transport;
            internal ActivityActor? Actor;
            internal string World = "";
            internal ZDOID Character;
            internal EmoteReportTriggers.Snapshot Before;
            internal Capture(EmoteReportTriggers helper, bool data)
            { Helper = helper; Scope = helper.Enter(); Data = data; }
        }

        [HarmonyTargetMethods]
        internal static IEnumerable<MethodBase> Targets() => new[]
        {
            AccessTools.Method(typeof(ZDOMan), nameof(ZDOMan.RPC_ZDOData)),
            AccessTools.Method(typeof(ZRoutedRpc), nameof(ZRoutedRpc.RPC_RoutedRPC))
        };

        private static EmoteReportTriggers.Snapshot Snapshot(ZDO? zdo) => zdo == null ? default :
            new EmoteReportTriggers.Snapshot(zdo.m_uid.ToString(), zdo.DataRevision,
                zdo.GetInt(ZDOVars.s_emoteID, 0), zdo.GetString(ZDOVars.s_emote, ""));

        [HarmonyPrefix, HarmonyPriority(Priority.First), HarmonyBefore("MidnightsFX.NetworkPerformanceSystem")]
        internal static void Prefix(object __instance, ZRpc __0, out Capture? __state)
        {
            __state = null;
            var helper = EmoteTriggers;
            if (helper == null || !helper.Enabled) return;
            try
            {
                string world = StablePlayerResolver.World(out string current) ? current : "";
                helper.SetWorld(world);
                var capture = new Capture(helper, __instance is ZDOMan);
                __state = capture;
                if (__instance is not ZDOMan manager || capture.Scope == null ||
                    !StablePlayerResolver.Transport(__0, DateTime.UtcNow, out var peer, out var actor) || peer == null || actor == null ||
                    !ReferenceEquals(__0.GetSocket(), peer.m_socket) || !ZNet.instance.IsAdmin(actor.PlayerId)) return;
                capture.Transport = __0; capture.Actor = actor; capture.World = world; capture.Character = peer.m_characterID;
                capture.Before = Snapshot(manager.GetZDO(capture.Character));
            }
            catch (Exception error)
            {
                if (__state?.Scope != null) __state.Scope.Failed = true;
                ManualReportDiagnostics.Emit(ManualReportStage.Emote, ManualReportReason.Failed, error);
            }
        }

        private static bool Current(ZRpc rpc, ActivityActor captured, string world, ZDOID character)
        {
            return StablePlayerResolver.World(out string currentWorld) && currentWorld == world &&
                StablePlayerResolver.Transport(rpc, DateTime.UtcNow, out var peer, out var actor) && peer != null && actor != null &&
                ReferenceEquals(rpc.GetSocket(), peer.m_socket) && peer.m_characterID == character &&
                actor.PlayerId == captured.PlayerId && actor.ConnectionToken == captured.ConnectionToken;
        }

        [HarmonyPostfix, HarmonyPriority(Priority.Last)]
        internal static void Postfix(Capture? __state, bool __runOriginal)
        { if (__state != null) __state.Ran = __runOriginal; }

        [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
        internal static void Finalizer(Capture? __state, Exception? __exception)
        {
            if (__state == null) return;
            Capture capture = __state;
            Exception? failure = __exception;
            try
            {
                if (capture.Data && capture.Ran && failure == null && capture.Transport is ZRpc rpc && capture.Actor is ActivityActor actor)
                {
                    string world = capture.World; ZDOID character = capture.Character;
                    if (!Current(rpc, actor, world, character))
                    { if (capture.Scope != null) capture.Scope.Failed = true; }
                    else
                    {
                        var after = Snapshot(ZDOMan.instance.GetZDO(character));
                        capture.Helper.Stage(capture.Scope, world, actor.ConnectionToken, character.ToString(), capture.Before, after,
                            UnityEngine.Time.realtimeSinceStartupAsDouble, command =>
                            {
                                if (!Current(rpc, actor, world, character)) return;
                                ManualReportDiagnostics.Emit(ManualReportStage.Emote, ManualReportReason.Observed);
                                // The existing plugin entry point repeats native IsAdmin immediately before Request.
                                ManualCommand?.Invoke(rpc, actor, world, command);
                            });
                    }
                }
            }
            catch (Exception error)
            { failure = error; ManualReportDiagnostics.Emit(ManualReportStage.Emote, ManualReportReason.Failed, error); }
            finally
            {
                try { capture.Helper.Finish(capture.Scope, !capture.Data || capture.Ran, failure); }
                catch (Exception error) { ManualReportDiagnostics.Emit(ManualReportStage.Emote, ManualReportReason.Failed, error); }
            }
        }
    }

    [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.RPC_ZDOData))]
    internal static class Data
    {
        [HarmonyPrefix, HarmonyPriority(Priority.Last)]
        internal static void Prefix(ZRpc rpc, ZPackage pkg, out Ingress? __state)
        {
            __state = null;
            var runtime = ActivityRuntime.Current;
            if (runtime == null || !StablePlayerResolver.World(out _)) return;
            try
            {
                var options = runtime.Options;
                var frame = new Ingress { Previous = _ingress, Data = true };
                __state = frame; _ingress = frame;
                // Keep an exception barrier for native creation/removal, without decoding unused replication.
                if (!ActivityCollector.NeedsZdoData(options)) return;
                frame.Failed = true;
                if (pkg.Size() > 4 * 1024 * 1024 || !StablePlayerResolver.Transport(rpc, DateTime.UtcNow, out var peer, out var actor) || peer == null) return;
                int position = pkg.GetPos();
                try
                {
                    int invalid = pkg.ReadInt();
                    if (invalid < 0 || invalid > 4096) return;
                    for (int i = 0; i < invalid; i++) if (!ReadKnownId(pkg, out _)) return;
                    DateTime utc = DateTime.UtcNow; double time = ActivityRuntime.Now;
                    while (true)
                    {
                        if (!ReadKnownId(pkg, out var id)) return;
                        if (id.IsNone()) break;
                        if (frame.Before.Count >= 4096 || frame.Before.ContainsKey(id)) return;
                        pkg.ReadUShort(); uint revision = pkg.ReadUInt(); pkg.ReadLong(); pkg.ReadVector3();
                        if (pkg.Size() - pkg.GetPos() < 4) return;
                        int length = pkg.ReadInt();
                        if (length < 0 || length > pkg.Size() - pkg.GetPos()) return;
                        pkg.SetPos(pkg.GetPos() + length);
                        var zdo = ZDOMan.instance.GetZDO(id);
                        frame.Before.Add(id, new BeforeZdo { Owner = zdo?.GetOwner() ?? 0, Revision = zdo?.DataRevision ?? 0,
                            IncomingRevision = revision, OwnerRevision = zdo?.OwnerRevision ?? 0,
                            Item = !options.FloorPickups || zdo == null ? null : Item(zdo, FloorItemEvent.Snapshot, peer.m_uid,
                                "before:" + ObservationValues.Number(revision), utc, time) });
                    }
                    if (pkg.GetPos() != pkg.Size()) return;
                    frame.Sender = peer.m_uid; frame.Actor = actor; frame.Failed = false;
                }
                finally { pkg.SetPos(position); }
            }
            catch (Exception) { if (__state != null) __state.Failed = true; }
        }

        [HarmonyPostfix, HarmonyPriority(Priority.Last)]
        internal static void Postfix(Ingress? __state, bool __runOriginal)
        { if (__state != null) __state.Ran = __runOriginal; }

        [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
        internal static void Finalizer(Ingress? __state, Exception? __exception)
        { try { Finish(__state, __exception); } catch (Exception error) { Fail(error); } }
    }

    [HarmonyPatch(typeof(ZDO), nameof(ZDO.Deserialize))]
    internal static class Deserialize
    {
        internal sealed class Capture { internal FloorItemSignal? Before, Item; internal string World = "", Id = ""; internal long Sender; internal uint Revision; internal long Owner; internal double Time; internal bool Ran, TrackPosition; }
        [HarmonyPrefix]
        internal static void Prefix(out Capture? __state) => __state = null;
        [HarmonyPostfix, HarmonyPriority(Priority.Last)]
        internal static void Postfix(ZDO __instance, bool __runOriginal, ref Capture? __state)
        {
            __state = null;
            try
            {
                var scope = _ingress; var runtime = ActivityRuntime.Current;
                if (!__runOriginal || runtime == null || scope == null || scope.Failed || !scope.Data ||
                    !scope.Before.TryGetValue(__instance.m_uid, out var before) || __instance.DataRevision != before.IncomingRevision ||
                    __instance.DataRevision <= before.Revision || !StablePlayerResolver.World(out string world)) return;
                var options = runtime.Options;
                if (!ActivityCollector.NeedsZdoData(options)) return;
                var capture = new Capture { World = world, Id = __instance.m_uid.ToString(), Sender = scope.Sender,
                    Owner = __instance.GetOwner(), Revision = __instance.DataRevision, Time = ActivityRuntime.Now, Ran = true };
                capture.TrackPosition = (options.Exploration || options.HealthLost || options.Biomes || options.BossKills || options.OnFoot || options.BiomeTime || options.PortalTrips || options.LongestBowHit) && __instance.GetLong(ZDOVars.s_playerID, 0) != 0;
                if (!capture.TrackPosition && options.Sailing)
                {
                    var prefab = ZNetScene.instance ? ZNetScene.instance.GetPrefab(__instance.GetPrefab()) : null;
                    capture.TrackPosition = prefab && prefab.GetComponent<Ship>();
                }
                if (options.FloorPickups)
                {
                    if (before.Owner == capture.Owner) capture.Before = before.Item;
                    capture.Item = Item(__instance, FloorItemEvent.Snapshot, scope.Sender,
                        "revision:" + ObservationValues.Number(__instance.DataRevision), DateTime.UtcNow, capture.Time);
                    if (capture.Item == null && before.Item != null)
                        capture.Item = Copy(before.Item, FloorItemEvent.Excluded, scope.Sender, before.Item.Token, DateTime.UtcNow, capture.Time);
                }
                if (!capture.TrackPosition && capture.Before == null && capture.Item == null) return;
                __state = capture;
            }
            catch (Exception error) { Fail(error); }
        }

        [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
        internal static void Finalizer(Capture? __state, Exception? __exception)
        {
            if (__state == null || __exception != null || !__state.Ran) return;
            try
            {
                var runtime = ActivityRuntime.Current; if (runtime == null) return;
                var s = __state;
                Stage(() => { if (runtime.Options.FloorPickups)
                    { if (s.Before != null) runtime.Collector.ObserveItem(s.Before); if (s.Item != null) runtime.Collector.ObserveItem(s.Item); }
                    if (s.TrackPosition) runtime.AcceptedPosition(s.World, s.Id, s.Revision, s.Owner, s.Sender, s.Time); });
            }
            catch (Exception error) { Fail(error); }
        }
    }

    [HarmonyPatch]
    internal static class Owner
    {
        internal sealed class Capture { internal FloorItemSignal? Before, After; internal string World = "", Id = ""; internal long Owner; internal bool Ran; }
        [HarmonyTargetMethods]
        internal static IEnumerable<MethodBase> Targets() => new[]
        { AccessTools.Method(typeof(ZDO), nameof(ZDO.SetOwner)), AccessTools.Method(typeof(ZDO), nameof(ZDO.SetOwnerInternal)) };

        [HarmonyPrefix, HarmonyPriority(Priority.Last)]
        internal static void Prefix(ZDO __instance, long __0, out Capture? __state)
        {
            __state = null;
            try
            {
                var runtime = ActivityRuntime.Current;
                if (runtime == null || __instance.GetOwner() == __0 || !StablePlayerResolver.World(out string world)) return;
                long sender = _ingress != null && !_ingress.Failed ? _ingress.Sender : 0;
                if (_ingress != null && !_ingress.Data && _ingress.Target == __instance.m_uid && __instance.GetOwner() == ZNet.GetUID())
                    sender = __instance.GetOwner(); // Actual owner-local grant, not a claimed embedded owner.
                __state = new Capture { World = world, Id = __instance.m_uid.ToString(), Owner = __instance.GetOwner(),
                    Before = runtime.Options.FloorPickups ? Item(__instance, FloorItemEvent.Snapshot, sender, "owner-before", DateTime.UtcNow, ActivityRuntime.Now) : null };
            }
            catch (Exception error) { Fail(error); }
        }

        [HarmonyPostfix, HarmonyPriority(Priority.Last)]
        internal static void Postfix(ZDO __instance, Capture? __state, bool __runOriginal)
        {
            if (__state == null) return;
            try
            {
                __state.Ran = __runOriginal && __state.Owner != __instance.GetOwner();
                if (ActivityRuntime.Current?.Options.FloorPickups == true && __state.Before != null) __state.After = Item(__instance, FloorItemEvent.Snapshot, __state.Before.Sender,
                    "owner:" + __instance.OwnerRevision, DateTime.UtcNow, ActivityRuntime.Now);
            }
            catch (Exception error) { Fail(error); }
        }

        [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
        internal static void Finalizer(Capture? __state, Exception? __exception)
        {
            if (__state == null || !__state.Ran || __exception != null) return;
            try
            {
                var runtime = ActivityRuntime.Current; if (runtime == null) return;
                var s = __state;
                // Invalidating provenance is conservative even when a later enclosing invocation fails.
                runtime.OwnershipChanged(s.World, s.Id);
                if (s.After != null) Stage(() => { if (runtime.Options.FloorPickups) runtime.Collector.ObserveItem(s.After); });
            }
            catch (Exception error) { Fail(error); }
        }
    }

    [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.CreateNewZDO), typeof(ZDOID), typeof(Vector3), typeof(int))]
    internal static class Created
    {
        internal sealed class Capture { internal string World = ""; internal ZDOID Id; internal DateTime Utc; internal double Time; internal bool Excluded; }
        [HarmonyPrefix]
        internal static void Prefix(out Capture? __state) => __state = null;
        [HarmonyPostfix, HarmonyPriority(Priority.Last)]
        internal static void Postfix(ZDO __result, bool __runOriginal, ref Capture? __state)
        {
            __state = null;
            try
            {
                var options = ActivityRuntime.Current?.Options;
                if (__runOriginal && __result != null && options != null && (options.Construction || options.CropsPlanted) && StablePlayerResolver.World(out string world))
                    __state = new Capture { World = world, Id = __result.m_uid, Utc = DateTime.UtcNow, Time = ActivityRuntime.Now,
                        Excluded = _growth > 0 };
            }
            catch (Exception error) { Fail(error); }
        }
        [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
        internal static void Finalizer(Capture? __state, Exception? __exception)
        {
            if (__state == null || __exception != null) return;
            try { var s = __state; Stage(() => ActivityRuntime.Current?.NewZdo(s.World, s.Id, s.Utc, s.Time, s.Excluded)); }
            catch (Exception error) { Fail(error); }
        }
    }

    [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.HandleDestroyedZDO))]
    internal static class Destroyed
    {
        internal sealed class Capture { internal string World = "", Id = ""; internal FloorItemSignal? Item; internal bool Ran; }
        [HarmonyPrefix, HarmonyPriority(Priority.Last)]
        internal static void Prefix(ZDOMan __instance, ZDOID uid, out Capture? __state)
        {
            __state = null;
            try
            {
                var runtime = ActivityRuntime.Current;
                if (runtime == null || !StablePlayerResolver.World(out string world)) return;
                var zdo = __instance.GetZDO(uid); if (zdo == null) return;
                long sender = _ingress != null && !_ingress.Failed ? _ingress.Sender : 0;
                __state = new Capture { World = world, Id = uid.ToString(), Item = runtime.Options.FloorPickups ? Item(zdo, FloorItemEvent.Removed, sender,
                    "removed:" + ObservationValues.Number(zdo.DataRevision), DateTime.UtcNow, ActivityRuntime.Now) : null };
                if (_itemExclusion > 0 && __state.Item != null)
                    ActivityRuntime.Current.Collector.ObserveItem(Copy(__state.Item, FloorItemEvent.Excluded, sender,
                        "excluded-removal", __state.Item.Utc, __state.Item.Time));
            }
            catch (Exception error) { Fail(error); }
        }
        [HarmonyPostfix, HarmonyPriority(Priority.Last)]
        internal static void Postfix(ZDOMan __instance, ZDOID uid, Capture? __state, bool __runOriginal)
        { if (__state != null) __state.Ran = __runOriginal && __instance.GetZDO(uid) == null; }
        [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
        internal static void Finalizer(Capture? __state, Exception? __exception)
        {
            if (__state == null || !__state.Ran || __exception != null) return;
            try
            {
                var runtime = ActivityRuntime.Current; if (runtime == null) return;
                var s = __state;
                if (s.Item != null) Stage(() => { if (runtime.Options.FloorPickups) runtime.Collector.ObserveItem(s.Item); });
                StageRemoval(() => runtime.Removed(s.World, s.Id));
            }
            catch (Exception error) { Fail(error); }
        }
    }

    [HarmonyPatch]
    internal static class Exclusions
    {
        internal sealed class Capture { internal FloorItemSignal? Before; internal bool Scoped; }
        [HarmonyTargetMethods]
        internal static IEnumerable<MethodBase> Targets() => new[]
        {
            AccessTools.Method(typeof(ItemDrop), nameof(ItemDrop.AutoStackItems)),
            AccessTools.Method(typeof(ItemDrop), nameof(ItemDrop.TimedDestruction)),
            AccessTools.Method(typeof(ItemDrop), nameof(ItemDrop.RemoveOne)),
            AccessTools.Method(typeof(ItemDrop), nameof(ItemDrop.Eat)),
            AccessTools.Method(typeof(ItemDrop), nameof(ItemDrop.OnPlayerDrop))
        };
        [HarmonyPrefix]
        internal static void Prefix(ItemDrop __instance, MethodBase __originalMethod, out Capture __state)
        {
            __state = new Capture();
            try
            {
                var runtime = ActivityRuntime.Current;
                if (runtime == null || !runtime.Options.FloorPickups || !__instance.m_nview || !__instance.m_nview.IsValid()) return;
                bool drop = __originalMethod.Name == nameof(ItemDrop.OnPlayerDrop);
                __state.Before = Item(__instance.m_nview.GetZDO(), drop ? FloorItemEvent.PlayerDrop : FloorItemEvent.Excluded,
                    0, "excluded", DateTime.UtcNow, ActivityRuntime.Now);
                if (drop) { if (__state.Before != null) runtime.Collector.ObserveItem(__state.Before); }
                else { _itemExclusion++; __state.Scoped = true; }
            }
            catch (Exception error) { Fail(error); }
        }
        [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
        internal static void Finalizer(ItemDrop __instance, Capture __state)
        {
            if (!__state.Scoped) return;
            _itemExclusion--;
            try
            {
                // No-op SlowUpdate destruction/stack attempts do not suppress legitimate pickups.
                var before = __state.Before; var runtime = ActivityRuntime.Current;
                if (before == null || runtime == null || !runtime.Options.FloorPickups || !__instance || !__instance.m_nview || !__instance.m_nview.IsValid()) return;
                var after = Item(__instance.m_nview.GetZDO(), FloorItemEvent.Excluded, 0, "excluded", DateTime.UtcNow, ActivityRuntime.Now);
                if (after != null && after.Stack != before.Stack) runtime.Collector.ObserveItem(after);
            }
            catch (Exception error) { Fail(error); }
        }
    }

    [HarmonyPatch(typeof(Piece), nameof(Piece.DropResources))]
    internal static class Refund
    {
        [HarmonyPrefix] internal static void Prefix() => _refund++;
        [HarmonyFinalizer] internal static void Finalizer() => _refund--;
    }

    [HarmonyPatch(typeof(ItemDrop), nameof(ItemDrop.SaveToZDO))]
    internal static class RefundedItem
    {
        [HarmonyPostfix]
        internal static void Postfix(ZDO zdo, int index, bool __runOriginal)
        {
            if (_refund == 0 || index != -1 || !__runOriginal || ActivityRuntime.Current?.Options.FloorPickups != true) return;
            try
            {
                var item = Item(zdo, FloorItemEvent.PlayerDrop, 0, "refund", DateTime.UtcNow, ActivityRuntime.Now);
                if (item != null) ActivityRuntime.Current?.Collector.ObserveItem(item);
            }
            catch (Exception error) { Fail(error); }
        }
    }

    [HarmonyPatch(typeof(Plant), nameof(Plant.Grow))]
    internal static class Growth
    {
        [HarmonyPrefix]
        internal static void Prefix(out Ingress __state)
        { _growth++; __state = new Ingress { Previous = _ingress }; _ingress = __state; }
        [HarmonyPostfix] internal static void Postfix(Ingress __state, bool __runOriginal) => __state.Ran = __runOriginal;
        [HarmonyFinalizer]
        internal static void Finalizer(Ingress __state, Exception? __exception)
        { _growth--; try { Finish(__state, __exception); } catch (Exception error) { Fail(error); } }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.PlacePiece))]
    internal static class Placement
    {
        [HarmonyPrefix]
        internal static void Prefix(out Ingress __state)
        {
            __state = new Ingress { Previous = _ingress };
            _ingress = __state;
        }
        [HarmonyPostfix] internal static void Postfix(Ingress __state, bool __runOriginal) => __state.Ran = __runOriginal;
        [HarmonyFinalizer]
        internal static void Finalizer(Ingress __state, Exception? __exception)
        { try { Finish(__state, __exception); } catch (Exception error) { Fail(error); } }
    }

    internal static FloorItemSignal? Item(ZDO zdo, FloorItemEvent kind, long sender, string token, DateTime utc,
        double time, ActivityActor? actor = null)
    {
        if (ActivityRuntime.Current?.Options.FloorPickups != true ||
            !StablePlayerResolver.World(out string world) || !ZNetScene.instance || zdo.m_uid.ID == 0) return null;
        var prefab = ZNetScene.instance.GetPrefab(zdo.GetPrefab());
        var drop = prefab ? prefab.GetComponent<ItemDrop>() : null;
        byte[] payload = zdo.GetByteArray(ZDOVars.s_itemData);
        if (!drop || payload == null || payload.Length <= 2 || payload.Length > 65536) return null;
        var data = drop.m_itemData.Clone(); data.m_stack = 0;
        try { ItemDrop.LoadFromZDO(data, zdo); }
        catch (Exception) { return null; }
        if (data.m_stack <= 0 || data.m_stack > data.m_shared.m_maxStackSize) return null;
        int stack = data.m_stack; data.m_stack = 0;
        var canonical = new ZPackage(); data.Save(canonical);
        string signature = ObservationValues.Key(prefab.name, Convert.ToBase64String(canonical.GetArray()));
        return new FloorItemSignal(kind, world, zdo.m_uid.ToString(), prefab.name, signature, stack, zdo.GetOwner(),
            zdo.DataRevision, sender, token, utc, time, actor);
    }

    private static FloorItemSignal Copy(FloorItemSignal s, FloorItemEvent kind, long sender, string token, DateTime utc, double time) =>
        new(kind, s.World, s.Item, s.Prefab, s.Signature, s.Stack, s.Owner, s.Revision, sender, token, utc, time);

    private static bool PackageFits(ZPackage copy)
    {
        int position = copy.GetPos();
        if (copy.Size() - position < 4) return false;
        int length = copy.ReadInt();
        bool fits = length >= 0 && length <= copy.Size() - copy.GetPos();
        copy.SetPos(position); return fits;
    }

    private static bool ReadKnownId(ZPackage copy, out ZDOID id)
    {
        long user = copy.ReadLong(); uint value = copy.ReadUInt(); id = default;
        // Native ReadZDOID registers unknown user keys; telemetry must not mutate that registry.
        if (!ZDOID.m_userIDs.Contains(user)) return false;
        id = new ZDOID(user, value); return true;
    }
}
