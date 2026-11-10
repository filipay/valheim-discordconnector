using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;

using LiteDB;

namespace DiscordConnector.Records;

internal enum DatabaseStage { Activities, Chunks, Action, Legacy, Cooking, Coverage, Query, Claim, Drain, Signal, Loss }

internal sealed class DatabaseDiagnostics
{
    private readonly Action<string> warn;
    private readonly DatabaseFailureBudget budget = new();
    private volatile bool hasFailures;
    internal bool HasFailures => hasFailures;

    internal DatabaseDiagnostics(Action<string> warn) => this.warn = warn;

    internal void SignalRetentionFailure()
    {
        hasFailures = true;
        try { warn("Database failure stage=signal-retention; maintenance failed; no gameplay replay."); }
        catch { /* Logging cannot poison the worker or replace the maintenance failure. */ }
    }

    internal void Report(DatabaseStage stage, Exception exception, bool rollback = false, ActivityBatch? batch = null,
        int queueBatches = -1, int queueReports = -1, bool? disposed = null)
    {
        hasFailures = true;
        try
        {
            if (!budget.TryTake(exception, Stopwatch.GetTimestamp() / Stopwatch.Frequency, out long suppressed)) return;
            warn($"Database failure operation={(rollback ? "rollback" : "execute")} stage={stage.ToString().ToLowerInvariant()} " +
                FormattableString.Invariant($"activities={batch?.Activities.Count ?? 0} chunks={batch?.Chunks.Count ?? 0} actions={batch?.Actions.Count ?? 0} cooking={batch?.Cooking.Count ?? 0} coverage={batch?.Coverage.Count ?? 0} signals={batch?.Signals.Count ?? 0} ") +
                FormattableString.Invariant($"queueBatches={queueBatches} queueReports={queueReports} thread={Environment.CurrentManagedThreadId} disposed={(disposed.HasValue ? (disposed.Value ? "true" : "false") : "unknown")} suppressed={suppressed} ") + Describe(exception));
        }
        catch { /* Diagnostic formatting and callbacks must never replace a database failure. */ }
    }

    internal void Shutdown(string state, int batches, int reports, int unacknowledged, bool incomplete)
    {
        try
        {
            warn(FormattableString.Invariant($"Database shutdown state={state} pendingBatches={batches} pendingReports={reports} knownFailures={HasFailures} lossUnacknowledged={unacknowledged} lossIncomplete={incomplete}; drained does not mean all writes succeeded."));
        }
        catch { }
    }

    internal static string Describe(Exception exception)
    {
        StringBuilder text = new();
        Exception? current = exception;
        for (int depth = 0; current != null && depth < 4; depth++, current = current.InnerException)
        {
            if (depth != 0) text.Append(" inner=");
            text.Append(Bounded(current.GetType().FullName ?? current.GetType().Name));
            if (current is LiteException lite) text.Append(" code=").Append(lite.ErrorCode.ToString(CultureInfo.InvariantCulture));
            // Never use Exception.StackTrace: it includes source paths. Reflection metadata only, no arguments.
            StackTrace stack = new(current, false);
            text.Append(" stack=[");
            for (int frame = 0; frame < Math.Min(stack.FrameCount, 6); frame++)
            {
                if (frame != 0) text.Append('>');
                var method = stack.GetFrame(frame)?.GetMethod();
                text.Append(Bounded(method?.DeclaringType?.FullName ?? "unknown")).Append('.').Append(Bounded(method?.Name ?? "unknown"));
            }
            text.Append(']');
            if (stack.FrameCount > 6) text.Append(" stackTruncated=true");
        }
        if (current != null) text.Append(" innerTruncated=true");
        return text.ToString();
    }

    private static string Bounded(string value) => value.Length <= 128 ? value : value.Substring(0, 128);

    internal static void Rollback(Action rollback, Action<Exception> report)
    {
        try { rollback(); }
        catch (Exception secondary)
        {
            try { report(secondary); }
            catch { /* The caller rethrows the original exception, even if logging fails. */ }
        }
    }
}

internal sealed class DatabaseFailureBudget
{
    // Fixed budgets keep a later disposed-engine symptom visible even during repeated LiteDB failures.
    private readonly long[] windowStart = new long[3], suppressed = new long[3];
    private readonly int[] emitted = new int[3];

    internal bool TryTake(Exception exception, long seconds, out long skipped)
    {
        int bucket = exception is LiteException ? 0 : exception is ObjectDisposedException ? 1 : 2;
        lock (emitted)
        {
            if (emitted[bucket] == 0 || seconds - windowStart[bucket] >= 60) { windowStart[bucket] = seconds; emitted[bucket] = 0; }
            if (emitted[bucket] >= 4)
            {
                if (suppressed[bucket] < long.MaxValue) suppressed[bucket]++;
                skipped = 0; return false;
            }
            emitted[bucket]++;
            skipped = suppressed[bucket]; suppressed[bucket] = 0;
            return true;
        }
    }
}
