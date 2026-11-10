using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace DiscordConnector.Leaderboards;

internal enum ManualReportStage { Ready, NativeChat, Ingress, IngressProbe, Forward, Enclosing, Callback, Authorization, Request, Grace, Flush, Query, Render, Dispatch, Routing, Lifecycle, Emote, NativeCapability, NativeSignal }
internal enum ManualReportReason { Wired, Received, MissingIngress, InvalidIngress, Skipped, Failed, Decoded, Mismatch, Staged, Invoking, MissingCallback, Passed, Denied, Unauthorized, Unsupported, InvalidWorld, InvalidZone, Disposed, Busy, Cooldown, Accepted, Waiting, Started, Completed, Obsolete, DispatchCounts, Summary, Observed }
internal enum ManualIngressProbe { Received, RuntimeUnavailable, WorldUnavailable, SizeOrTransport, UnknownId, PackageBounds, Envelope, Replay, DecodeFailure }
internal enum NativeSignalProbe { Receive, Local, Decoded, Disabled, Unavailable, Identity, Payload, Stale, Replay, Overflow, Dropped, Accepted }

internal sealed class ManualReportDiagnostics
{
    internal static ManualReportDiagnostics? Current;
    [ThreadStatic] private static bool dispatch;
    internal static bool InDispatch => dispatch;
    private readonly Action<string> info;
    private readonly Func<double> seconds;
    private readonly object gate = new();
    private readonly long[] probes = new long[9];
    private readonly long[] signalProbes = new long[12];
    private readonly bool[] signalSeen = new bool[12];
    private double window, nextProbe;
    private int emitted, earlyEmitted;
    private long suppressed;
    private bool ready;
    private static readonly object HeaderGate = new();
    private static double? lastShortHeader, lastHeaderReadFailure;

    internal ManualReportDiagnostics(Action<string> info, Func<double>? seconds = null)
    {
        this.info = info;
        this.seconds = seconds ?? (() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
        try { window = this.seconds(); } catch { window = 0; }
        nextProbe = window + 60;
    }

    // Diagnostics cannot replace a native/game/storage exception or affect admission/dispatch.
    internal static void Emit(ManualReportStage stage, ManualReportReason reason, Exception? error = null, int matched = 0, int invoked = 0)
    {
        try { Current?.Write(stage, reason, error, stage == ManualReportStage.Routing ?
            FormattableString.Invariant($" matched={matched} invoked={invoked}") : ""); } catch { }
    }

    private bool Write(ManualReportStage stage, ManualReportReason reason, Exception? error, string extra)
    {
        lock (gate)
        {
            double now = seconds();
            if (now - window >= 60 || now < window) { window = now; emitted = 0; earlyEmitted = 0; }
            // Recipient copies/rejections/probes cannot consume an accepted job's eight progress records.
            bool progress = stage == ManualReportStage.Request && reason == ManualReportReason.Accepted ||
                stage == ManualReportStage.Grace || stage == ManualReportStage.Flush || stage == ManualReportStage.Query ||
                stage == ManualReportStage.Render || stage == ManualReportStage.Dispatch || stage == ManualReportStage.Routing ||
                stage == ManualReportStage.Lifecycle && reason == ManualReportReason.Obsolete;
            if (emitted >= 16 || (progress ? emitted - earlyEmitted >= 8 : earlyEmitted >= 8))
            { if (suppressed < long.MaxValue) suppressed++; return false; }
            emitted++;
            if (!progress) earlyEmitted++;
            string text = $"ManualReport stage={stage} reason={reason}" + extra +
                " suppressed=" + suppressed.ToString(CultureInfo.InvariantCulture);
            if (error != null) text += " exception=" + SafeException(error);
            suppressed = 0;
            try { info(text); } catch { }
            return true;
        }
    }

    private static string SafeException(Exception error) => error switch
    {
        ObjectDisposedException => "ObjectDisposedException",
        ArgumentOutOfRangeException => "ArgumentOutOfRangeException",
        ArgumentException => "ArgumentException",
        InvalidOperationException => "InvalidOperationException",
        NullReferenceException => "NullReferenceException",
        FormatException => "FormatException",
        OverflowException => "OverflowException",
        IndexOutOfRangeException => "IndexOutOfRangeException",
        System.IO.IOException => "IOException",
        OperationCanceledException => "OperationCanceledException",
        _ => "OtherException"
    };

    internal static void Probe(ManualIngressProbe reason)
    {
        try
        {
            var current = Current;
            if (current == null || (uint)reason >= current.probes.Length) return;
            lock (current.gate)
                if (current.probes[(int)reason] < long.MaxValue) current.probes[(int)reason]++;
        }
        catch { }
    }

    internal static void HeaderProbe(bool readFailed, int size)
    {
        try
        {
            var current = Current;
            if (current == null) return;
            lock (HeaderGate)
            {
                double now = current.seconds();
                if (double.IsNaN(now) || double.IsInfinity(now) || now < 0) return;
                double? last = readFailed ? lastHeaderReadFailure : lastShortHeader;
                if (last.HasValue && now - last.Value < 60) return;
                // Reserve before logging; even a failing logger must consume the process-wide cadence.
                if (readFailed) lastHeaderReadFailure = now; else lastShortHeader = now;
                current.info("ActivityIngress reason=" + (readFailed ? "header-read-failed" : "short-header") +
                    " bytes=" + Math.Max(0, Math.Min(size, 1024 * 1024)).ToString(CultureInfo.InvariantCulture));
            }
        }
        catch { }
    }

    internal static void Poll()
    {
        try
        {
            var current = Current;
            if (current == null) return;
            lock (current.gate)
            {
                double now = current.seconds();
                if (now < current.nextProbe) return;
                current.nextProbe = now + 60;
                var counts = new StringBuilder();
                for (int i = 0; i < current.probes.Length; i++)
                    counts.Append(' ').Append((ManualIngressProbe)i).Append('=').Append(current.probes[i].ToString(CultureInfo.InvariantCulture));
                if (current.Write(ManualReportStage.IngressProbe, ManualReportReason.Summary, null, counts.ToString()))
                    Array.Clear(current.probes, 0, current.probes.Length);
                if (Array.Exists(current.signalProbes, count => count != 0))
                {
                    counts.Clear();
                    for (int i = 0; i < current.signalProbes.Length; i++)
                        counts.Append(' ').Append((NativeSignalProbe)i).Append('=').Append(current.signalProbes[i].ToString(CultureInfo.InvariantCulture));
                    if (current.Write(ManualReportStage.NativeSignal, ManualReportReason.Summary, null, counts.ToString()))
                        Array.Clear(current.signalProbes, 0, current.signalProbes.Length);
                }
            }
        }
        catch { }
    }

    internal static void SignalProbe(NativeSignalProbe reason)
    {
        try
        {
            var current = Current;
            if (current == null || (uint)reason >= current.signalProbes.Length) return;
            lock (current.gate)
            {
                int index = (int)reason;
                if (current.signalProbes[index] < long.MaxValue) current.signalProbes[index]++;
                if (current.signalSeen[index]) return;
                current.signalSeen[index] = true;
                current.Write(ManualReportStage.NativeSignal, ManualReportReason.Observed, null, " code=" + reason);
            }
        }
        catch { }
    }

    internal static void NativeCapability(bool supported, int targets, bool knownOwner)
    {
        try { Current?.Write(ManualReportStage.NativeCapability, supported ? ManualReportReason.Passed : ManualReportReason.Unsupported,
            null, FormattableString.Invariant($" targets={targets} knownOwner={knownOwner} execution=unverified")); } catch { }
    }

    internal static void NativeCapabilityTarget(string method, string condition, string foreignOwners)
    {
        // Startup inventory must remain visible even if runtime diagnostics exhausted their budget.
        try { Current?.info(($"ManualReport stage=NativeCapability reason=Unsupported method={method} condition={condition}" +
            $" foreignOwners=[{foreignOwners}] execution=unverified").Replace('\r', ' ').Replace('\n', ' ')); } catch { }
    }

    internal static void Dispatch(Action send)
    {
        bool previous = dispatch;
        dispatch = true;
        try { send(); }
        finally { dispatch = previous; }
    }

    internal static void Ready()
    {
        try
        {
            var current = Current;
            if (current == null) return;
            lock (current.gate)
            {
                if (current.ready) return;
                current.ready = true;
                current.info("ManualReport stage=Ready reason=Wired nativeRegistration=unverified");
            }
        }
        catch { }
    }
}
