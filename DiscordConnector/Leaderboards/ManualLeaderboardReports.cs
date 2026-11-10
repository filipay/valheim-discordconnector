using System;
using System.Threading.Tasks;

using DiscordConnector.Records;

namespace DiscordConnector.Leaderboards;

// Main-thread admission/polling only; storage uses the existing bounded activity worker, never scheduled claims.
internal sealed class ManualLeaderboardReports : IDisposable
{
    private readonly TimeZoneInfo? zone;
    private readonly int topCount;
    private readonly Func<string, DateTime, DateTime, Task<CombinedReportSnapshot>> query;
    private readonly Func<DateTime, string[]> flushThrough;
    private readonly Action<LeaderboardReport> send;
    private readonly Action<string> warn;
    private readonly Action<string>? diagnostic;
    private Job? pending;
    private double nextRequest;
    private bool disposed;

    private sealed class Job
    {
        internal readonly string World;
        internal readonly CombatReports.Period Period;
        internal readonly double Ready;
        internal Task<LeaderboardReport?>? Query;
        internal volatile ManualReportStage Stage = ManualReportStage.Query;
        internal string[] Limitations = Array.Empty<string>();
        internal bool Obsolete, GraceLogged, ObsoleteLogged;
        internal Job(string world, CombatReports.Period period, double ready)
        { World = world; Period = period; Ready = ready; }
    }

    internal ManualLeaderboardReports(string timeZoneId, int topCount,
        Func<string, DateTime, DateTime, Task<CombinedReportSnapshot>> query,
        Func<DateTime, string[]> flushThrough, Action<LeaderboardReport> send, Action<string> warn, Action<string>? diagnostic = null)
    {
        this.topCount = topCount; this.query = query; this.flushThrough = flushThrough;
        this.send = send; this.warn = warn;
        this.diagnostic = diagnostic;
        try
        {
            if (topCount < 1 || topCount > 16) throw new ArgumentOutOfRangeException(nameof(topCount));
            zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (Exception error)
        {
            ManualReportDiagnostics.Emit(ManualReportStage.Request, ManualReportReason.InvalidZone, error);
            warn($"Manual previews disabled ({error.GetType().Name}); tracking is unchanged.");
        }
    }

    internal bool Request(string text, bool authorized, DateTime nowUtc, double monotonic, string world)
    {
        if (!authorized) { ManualReportDiagnostics.Emit(ManualReportStage.Request, ManualReportReason.Unauthorized); return false; }
        if (disposed) { ManualReportDiagnostics.Emit(ManualReportStage.Request, ManualReportReason.Disposed); return false; }
        if (zone == null) { ManualReportDiagnostics.Emit(ManualReportStage.Request, ManualReportReason.InvalidZone); return false; }
        if (string.IsNullOrEmpty(world)) { ManualReportDiagnostics.Emit(ManualReportStage.Request, ManualReportReason.InvalidWorld); return false; }
        if (text != "!dc daily" && text != "!dc weekly") { ManualReportDiagnostics.Emit(ManualReportStage.Request, ManualReportReason.Unsupported); return false; }
        if (pending != null) { ManualReportDiagnostics.Emit(ManualReportStage.Request, ManualReportReason.Busy); return false; }
        if (monotonic < nextRequest) { ManualReportDiagnostics.Emit(ManualReportStage.Request, ManualReportReason.Cooldown); return false; }
        pending = new Job(world, CombatReports.CurrentPeriod(nowUtc, zone, text == "!dc weekly"), monotonic + 7);
        nextRequest = monotonic + 60;
        ManualReportDiagnostics.Emit(ManualReportStage.Request, ManualReportReason.Accepted);
        return true;
    }

    internal void Poll(double monotonic, string world)
    {
        Job? job = pending;
        if (disposed || job == null) return;
        ManualReportStage stage = ManualReportStage.Query;
        try
        {
            job.Obsolete |= !string.Equals(job.World, world, StringComparison.Ordinal);
            if (job.Obsolete)
            {
                if (!job.ObsoleteLogged) { job.ObsoleteLogged = true; ManualReportDiagnostics.Emit(ManualReportStage.Lifecycle, ManualReportReason.Obsolete); }
                // Obsolete in-flight work retains the single slot until its completion is observed.
                if (job.Query != null && !job.Query.IsCompleted) return;
                if (job.Query?.IsFaulted == true) _ = job.Query.Exception;
                pending = null;
                return;
            }
            if (monotonic < job.Ready)
            {
                if (!job.GraceLogged) { job.GraceLogged = true; ManualReportDiagnostics.Emit(ManualReportStage.Grace, ManualReportReason.Waiting); }
                return;
            }
            if (job.Query == null)
            {
                stage = ManualReportStage.Flush;
                ManualReportDiagnostics.Emit(stage, ManualReportReason.Started);
                job.Limitations = flushThrough(job.Period.EndUtc);
                stage = ManualReportStage.Query;
                ManualReportDiagnostics.Emit(stage, ManualReportReason.Started);
                job.Query = QueryReport(job);
            }
            if (!job.Query.IsCompleted) return;
            stage = job.Stage;
            LeaderboardReport report = job.Query.GetAwaiter().GetResult()!;
            pending = null;
            stage = ManualReportStage.Dispatch;
            ManualReportDiagnostics.Emit(stage, ManualReportReason.Invoking);
            send(report);
            ManualReportDiagnostics.Emit(stage, ManualReportReason.Completed);
        }
        catch (Exception error)
        {
            pending = null;
            ManualReportDiagnostics.Emit(stage, ManualReportReason.Failed, error);
            warn($"Manual preview failed ({error.GetType().Name}); no automatic retry. Tracking is unchanged.");
        }
    }

    private async Task<LeaderboardReport?> QueryReport(Job job)
    {
        CombinedReportSnapshot snapshot = await query(job.World, job.Period.StartUtc, job.Period.EndUtc).ConfigureAwait(false);
        ManualReportDiagnostics.Emit(ManualReportStage.Query, ManualReportReason.Completed);
        if (!string.Equals(snapshot.WorldId, job.World, StringComparison.Ordinal))
            throw new InvalidOperationException("Manual report world mismatch.");
        job.Stage = ManualReportStage.Render;
        LeaderboardReport? report = CombatReports.BuildReport(snapshot, job.Period, zone!.Id, topCount, job.Limitations, manual: true);
        if (DiscordConnectorPlugin.StaticConfig.DebugLeaderboardOperations)
            CombatReports.LogReport(snapshot, report, job.Period, topCount, true, diagnostic);
        return report;
    }

    public void Dispose()
    {
        ManualReportDiagnostics.Emit(ManualReportStage.Lifecycle, ManualReportReason.Disposed);
        disposed = true;
        Task? task = pending?.Query;
        if (task != null) _ = task.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        pending = null;
    }
}
