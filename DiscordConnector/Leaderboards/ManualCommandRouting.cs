using System;
using System.Collections.Generic;
using System.Diagnostics;
using DiscordConnector.Records;

namespace DiscordConnector.Leaderboards;

internal enum RoutingObservationKind { ManualReport, KillCredit, ConsumableUse, DodgeHitNotification, BossDefeat }

internal sealed class ManualCommandRouting
{
    internal sealed class Frame
    {
        internal readonly object? Transport;
        internal readonly long ServerUid;
        internal readonly Frame? Previous;
        internal readonly int Depth;
        internal readonly List<Observation> Pending = new();
        internal readonly HashSet<(RoutingObservationKind Kind, bool Local, string Session, string World, long Message, string Target)> Rejected = new();
        internal bool Failed, Closed, Witnessed;
        internal Frame(object? transport, long serverUid, Frame? previous)
        { Transport = transport; ServerUid = serverUid; Previous = previous; Depth = (previous?.Depth ?? 0) + 1; }
    }

    internal sealed class Observation
    {
        internal readonly Frame Owner;
        internal readonly (RoutingObservationKind Kind, bool Local, string Session, string World, long Message, string Target) Key;
        internal readonly double Time;
        internal readonly Action Invoke;
        internal readonly ObservationPrincipal? Principal;
        internal bool Completed;
        internal bool LossRecorded;
        internal Observation(Frame owner, RoutingObservationKind kind, bool local, string session, string world,
            long message, string target, double time, Action invoke, ObservationPrincipal? principal)
        { Owner = owner; Key = (kind, local, session, world, message, target); Time = time; Invoke = invoke; Principal = principal; }
    }

    private sealed class Replay
    {
        private readonly int capacity;
        private readonly double ttl;
        private readonly HashSet<(RoutingObservationKind, bool, string, string, long, string)> recent = new();
        private readonly Queue<((RoutingObservationKind, bool, string, string, long, string) Key, double Time)> order = new();
        internal int Count => recent.Count;
        internal int Nodes => order.Count;
        internal Replay(int capacity, double ttl) { this.capacity = capacity; this.ttl = ttl; }
        internal void Prune(double time)
        {
            for (int examined = 0; examined < 32 && order.Count != 0; examined++)
            {
                double age = time - order.Peek().Time;
                if (age <= ttl) break;
                recent.Remove(order.Dequeue().Key);
            }
        }
        internal bool Available(Observation observation, out ObservationRejection reason)
        {
            reason = recent.Contains(observation.Key) ? ObservationRejection.Duplicate :
                recent.Count >= capacity ? ObservationRejection.ReplayCapacity : ObservationRejection.None;
            return reason == ObservationRejection.None;
        }
        internal bool Add(Observation observation)
        {
            if (!Available(observation, out _)) return false;
            recent.Add(observation.Key); order.Enqueue((observation.Key, observation.Time)); return true;
        }
        internal void Clear() { recent.Clear(); order.Clear(); }
    }

    private Frame? current;
    private int overflow;
    private readonly Replay manual = new(256, 7), signals = new(4096, 60);
    private readonly ObservationAdmission admission = new();
    private double lastSignalTime = double.NegativeInfinity;
    internal ObservationRejection? LastRejection { get; private set; }
    private readonly Func<double> seconds;
    private readonly ObservationLoss loss;
    internal ManualCommandRouting(Func<double>? seconds = null, ObservationLoss? loss = null)
    {
        this.seconds = seconds ?? (() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
        this.loss = loss ?? ObservationLoss.Process;
    }
    internal Frame? Current => overflow == 0 ? current : null;
    internal int SignalReplayCount => signals.Count;
    internal int SignalReplayNodes => signals.Nodes;

    internal Frame? Enter(object? transport) => Enter(transport, 0);
    internal Frame? EnterLocal(long serverUid) => Enter(null, serverUid);
    private Frame? Enter(object? transport, long serverUid)
    {
        if (overflow != 0 || current?.Depth >= 16)
        {
            if (current != null) current.Failed = true;
            LastRejection = ObservationRejection.PendingOverflow;
            ManualReportDiagnostics.SignalProbe(NativeSignalProbe.Overflow);
            overflow++;
            return null;
        }
        return current = new Frame(transport, serverUid, current);
    }

    internal Observation? Observe(long claimedSender, long actualSender, string session, string world, long message,
        string text, double monotonic, Action invoke)
    {
        Frame? frame = Current;
        if (frame?.Transport == null || frame.Failed || frame.Closed || actualSender == 0 || claimedSender != actualSender ||
            string.IsNullOrEmpty(session) || string.IsNullOrEmpty(world) || (text != "!dc daily" && text != "!dc weekly") ||
            double.IsNaN(monotonic) || double.IsInfinity(monotonic)) return null;
        return Observe(frame, RoutingObservationKind.ManualReport, false, session, world, message, "", monotonic, invoke, null);
    }

    internal Observation? ObserveSignal(long claimedSender, long actualSender, string session, string world, long message,
        string targetKey, RoutingObservationKind kind, double monotonic, Action invoke, ObservationPrincipal principal)
    {
        Frame? frame = Current;
        if (frame?.Transport == null || actualSender == 0 || claimedSender != actualSender ||
            principal == null || principal.ServerLocal || principal.ConnectionToken != session ||
            (kind != RoutingObservationKind.KillCredit && kind != RoutingObservationKind.ConsumableUse && kind != RoutingObservationKind.DodgeHitNotification && kind != RoutingObservationKind.BossDefeat)) return null;
        return Observe(frame, kind, false, session, world, message, targetKey, monotonic, invoke, principal);
    }

    internal Observation? ObserveLocalSignal(long claimedSender, string epoch, string world, long message,
        string targetKey, double monotonic, Action invoke, ObservationPrincipal principal) =>
        ObserveLocalSignal(claimedSender, epoch, world, message, targetKey, RoutingObservationKind.KillCredit, monotonic, invoke, principal);

    internal Observation? ObserveLocalSignal(long claimedSender, string epoch, string world, long message,
        string targetKey, RoutingObservationKind kind, double monotonic, Action invoke, ObservationPrincipal principal)
    {
        Frame? frame = Current;
        if (frame == null || frame.Transport != null || frame.ServerUid == 0 || claimedSender != frame.ServerUid ||
            principal == null || !principal.ServerLocal || principal.ConnectionToken != epoch ||
            (kind != RoutingObservationKind.KillCredit && kind != RoutingObservationKind.DodgeHitNotification)) return null;
        return Observe(frame, kind, true, epoch, world, message, targetKey, monotonic, invoke, principal);
    }

    private Observation? Observe(Frame frame, RoutingObservationKind kind, bool local, string session, string world,
        long message, string target, double time, Action invoke, ObservationPrincipal? principal)
    {
        if (frame.Failed || frame.Closed || string.IsNullOrEmpty(session) || string.IsNullOrEmpty(world) ||
            invoke == null ||
            kind != RoutingObservationKind.ManualReport && (session.Length > 128 || world.Length > 256 || string.IsNullOrWhiteSpace(world) ||
                world.IndexOf('\0') >= 0 || string.IsNullOrEmpty(target) || target.Length > 256)) return null;
        var observation = new Observation(frame, kind, local, session, world, message, target, time, invoke, principal);
        if (kind != RoutingObservationKind.ManualReport)
        {
            if (!ObservationValues.Finite(time) || time < 0 || time < lastSignalTime)
            { LastRejection = ObservationRejection.Stale; Reject(observation, ObservationRejection.Stale); return null; }
            lastSignalTime = time;
        }
        else if (!ObservationValues.Finite(time)) return null;
        Replay replay = kind == RoutingObservationKind.ManualReport ? manual : signals;
        replay.Prune(time);
        if (!replay.Available(observation, out var reason))
        { LastRejection = reason; Reject(observation, reason); if (kind != RoutingObservationKind.ManualReport) ManualReportDiagnostics.SignalProbe(NativeSignalProbe.Replay); return null; }
        LastRejection = null;
        frame.Witnessed = true; return observation;
    }

    // A decoded observation, not vanilla's __runOriginal flag, witnesses a replaced router's work.
    internal void Complete(Observation? observation, Exception? error)
    {
        if (observation != null && (observation.Owner.Closed || !ReferenceEquals(Current, observation.Owner))) return;
        if (error != null)
        {
            if (Current != null) Current.Failed = true;
            return;
        }
        if (observation == null || observation.Completed || observation.Owner.Closed || !ReferenceEquals(Current, observation.Owner)) return;
        observation.Completed = true;
        Frame frame = observation.Owner;
        for (Frame? scope = frame; scope != null; scope = scope.Previous)
            if (scope.Pending.Exists(p => p.Key == observation.Key)) { LastRejection = ObservationRejection.Duplicate; return; }
        int pending = 0;
        for (Frame? scope = frame; scope != null; scope = scope.Previous) pending += scope.Pending.Count;
        if (pending >= 16) { frame.Failed = true; LastRejection = ObservationRejection.PendingOverflow; Reject(observation, ObservationRejection.PendingOverflow); ManualReportDiagnostics.SignalProbe(NativeSignalProbe.Overflow); return; }
        frame.Pending.Add(observation);
    }

    internal void Finish(Frame? frame, Exception? error)
    {
        if (frame == null)
        {
            if (error != null && current != null) current.Failed = true;
            if (overflow != 0) overflow--;
            return;
        }
        if (frame.Closed) return;
        frame.Closed = true;
        if (!ReferenceEquals(current, frame)) { RejectPending(frame, ObservationRejection.Stale); current = null; overflow = 0; frame.Pending.Clear(); return; }
        current = frame.Previous;
        if (frame.Witnessed)
        {
            if (current != null) current.Witnessed = true;
            ManualReportDiagnostics.Emit(ManualReportStage.Enclosing, error != null || frame.Failed ?
                ManualReportReason.Failed : ManualReportReason.Completed, error);
        }
        if (error != null || frame.Failed)
        {
            if (current != null) current.Failed = true;
            RejectPending(frame, LastRejection == ObservationRejection.PendingOverflow ? ObservationRejection.PendingOverflow : ObservationRejection.Stale);
            frame.Pending.Clear();
            return;
        }
        if (current != null)
        {
            if (current.Pending.Count + frame.Pending.Count > 16)
            { current.Failed = true; LastRejection = ObservationRejection.PendingOverflow; RejectPending(frame, ObservationRejection.PendingOverflow); ManualReportDiagnostics.SignalProbe(NativeSignalProbe.Overflow); }
            else current.Pending.AddRange(frame.Pending);
        }
        else
        {
            foreach (Observation observation in frame.Pending)
            {
                bool signal = observation.Key.Kind != RoutingObservationKind.ManualReport;
                double now = 0;
                if (signal)
                {
                    now = seconds();
                    double age = now - observation.Time;
                    if (double.IsNaN(age) || double.IsInfinity(age) || now < lastSignalTime || age < 0 || age > 5)
                    { LastRejection = ObservationRejection.Stale; Reject(observation, ObservationRejection.Stale); ManualReportDiagnostics.SignalProbe(NativeSignalProbe.Stale); continue; }
                }
                Replay replay = signal ? signals : manual;
                // Cleanup is charged once, at observation admission, never another cache scan at root unwind.
                if (!replay.Available(observation, out var reason)) { LastRejection = reason; Reject(observation, reason); continue; }
                if (signal && !admission.TryAdmit(observation.Principal!, now, out reason)) { LastRejection = reason; Reject(observation, reason); continue; }
                replay.Add(observation);
                LastRejection = null;
                try { observation.Invoke(); }
                catch (Exception failure)
                {
                    if (signal) loss.RecordUncertain(observation.Key.World, Metric(observation.Key.Kind));
                    ManualReportDiagnostics.Emit(ManualReportStage.Callback, ManualReportReason.Failed, failure);
                }
            }
        }
        frame.Pending.Clear();
    }

    private static ReportMetric Metric(RoutingObservationKind kind) => kind == RoutingObservationKind.KillCredit ?
        ReportMetric.ReportedKillCredits : kind == RoutingObservationKind.ConsumableUse ?
        ReportMetric.ObservedConsumableUses : kind == RoutingObservationKind.BossDefeat ? ReportMetric.ParticipantBossCredits : ReportMetric.ReportedDodgeHitNotifications;

    private void Reject(Observation observation, ObservationRejection reason)
    {
        if (observation.Key.Kind == RoutingObservationKind.ManualReport || !ObservationLoss.IsLoss(reason) || observation.LossRecorded) return;
        if (observation.Completed)
        {
            observation.LossRecorded = true;
            loss.Record(observation.Key.World, Metric(observation.Key.Kind), reason);
            return;
        }
        Frame root = observation.Owner;
        while (root.Previous != null) root = root.Previous;
        if (root.Rejected.Contains(observation.Key)) { observation.LossRecorded = true; return; }
        if (root.Rejected.Count >= 16) { loss.MarkIncomplete(); return; }
        root.Rejected.Add(observation.Key); observation.LossRecorded = true;
        loss.Record(observation.Key.World, Metric(observation.Key.Kind), reason);
    }

    private void RejectPending(Frame frame, ObservationRejection reason)
    { foreach (var observation in frame.Pending) Reject(observation, reason); }

    internal void Reset()
    {
        for (Frame? frame = current; frame != null; frame = frame.Previous)
        { RejectPending(frame, ObservationRejection.Stale); frame.Closed = true; frame.Failed = true; frame.Pending.Clear(); }
        current = null; overflow = 0; manual.Clear(); signals.Clear(); admission.Reset();
        lastSignalTime = double.NegativeInfinity; LastRejection = null;
    }
}
