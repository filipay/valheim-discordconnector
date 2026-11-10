using System.Runtime.CompilerServices;
using DiscordConnector.Records;
using LiteDB;

internal static partial class Program
{
    private const string HostileDetail = "PRIVATE-player-world-account-key-token /private/database.db\nforged log";

    private static void SignalDiagnostics(string directory)
    {
        List<string> messages = [];
        DatabaseDiagnostics diagnostics = new(messages.Add);
        ActivityBatch batch = new(signals: [new(ReportedSignalKind.KillCredit, HostileDetail, Actor(HostileDetail, HostileDetail), HostileDetail, Start)], signalPrincipal: Principal());
        diagnostics.Report(DatabaseStage.Signal, DiagnosticException(0), batch: batch);
        string message = messages.Single();
        Check(message.Contains("stage=signal") && message.Contains("signals=1"), "signal stage and scalar count");
        Check(!message.Contains("PRIVATE") && !message.Contains("/private") && !message.Contains("forged") && !message.Contains('\n'), "no signal payload in diagnostics");
        using LiteDatabase db = new(Path.Combine(directory, "records.db"));
        using ActivityStore worker = new(db, new CombatStore(db), new PlayerActionStore(db), new CookingStore(db), messages.Add);
        Check(db.GetCollection("reported_kill_credits_v1").DropIndex("WorldId"), "replace normal world index for failure injection");
        Check(db.GetCollection("reported_kill_credits_v1").EnsureIndex("WorldId", true), "unique world failure index installed");
        Check(worker.Enqueue(new ActivityBatch(signals: [Signal("first"), Signal("index-failure")], signalPrincipal: Principal())), "failing signal job queued");
        worker.QueryAsync("world", Start, End, new(reportedKillCredits: true)).GetAwaiter().GetResult();
        Check(messages.Any(x => x.Contains("stage=signal") && x.Contains("signals=2") && x.Contains("LiteDB.LiteException code=")), "real unrelated unique-index failure diagnosed");
        Equal(1, db.GetCollection("reported_kill_credits_v1").Count(), "failure was not mistaken for durable replay");
        Equal(0, SignalCharge(worker), "failed job releases reservation before next report");
        Check(worker.Enqueue(new ActivityBatch(signals: [Signal("after-failure", ReportedSignalKind.ConsumableUse)], signalPrincipal: Principal())), "signal capacity reusable after failure");
        Equal(1L, worker.QueryAsync("world", Start, End, new(consumableUses: true)).GetAwaiter().GetResult()
            .Rows[ReportMetric.ObservedConsumableUses].Single().Count, "worker recovers after signal failure");
        Equal(0, SignalCharge(worker), "recovered job releases reservation");
        db.Dispose();
        Check(worker.Enqueue(new ActivityBatch(signals: [Signal("closed-database")], signalPrincipal: Principal())), "closed database failure admitted without retry");
        Throws<Exception>(() => worker.QueryAsync("world", Start, End, new(reportedKillCredits: true)).GetAwaiter().GetResult());
        Equal(0, SignalCharge(worker), "disposed database failure releases reservation");
        Check(messages.Any(x => x.Contains("stage=signal") && x.Contains("signals=1") &&
            (x.Contains("LiteDB.LiteException") || x.Contains("System.ObjectDisposedException"))), "closed engine signal failure diagnosed");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Exception DiagnosticException(int depth)
    {
        try
        {
            if (depth == 0) throw new LiteException(999, HostileDetail);
            throw new InvalidOperationException(HostileDetail, DiagnosticException(depth - 1));
        }
        catch (Exception exception) { exception.Data[HostileDetail] = HostileDetail; return exception; }
    }

    private static void DiagnosticRedaction(string _)
    {
        string detail = DatabaseDiagnostics.Describe(DiagnosticException(12));
        Check(detail.Contains("System.InvalidOperationException"), "full exception type");
        Check(detail.Contains("DiagnosticException"), "method-only captured stack");
        Check(detail.Contains("innerTruncated=true"), "inner chain bounded explicitly");
        Check(detail.Length < 8000, "bounded diagnostic size");
        Check(!detail.Contains("PRIVATE") && !detail.Contains("/private") && !detail.Contains("forged") && !detail.Contains('\n'), "no exception data, messages or paths");
        string lite = DatabaseDiagnostics.Describe(DiagnosticException(0));
        Check(lite.Contains("LiteDB.LiteException code=999"), "numeric LiteDB code, not message");
        Check(DatabaseDiagnostics.Describe(DiagnosticException(2)).Contains("inner=LiteDB.LiteException code=999"), "bounded inner type and code retained");
        Check(!DatabaseDiagnostics.Describe(new ObjectDisposedException(HostileDetail, HostileDetail)).Contains("PRIVATE"), "disposed object name omitted");
        Exception deepException;
        try { DeepDiagnosticStack(30); throw new Exception("Expected failure"); }
        catch (LiteException exception) { deepException = exception; }
        string deep = DatabaseDiagnostics.Describe(deepException);
        Check(deep.Contains("stackTruncated=true"), "stack frame cap explicit");
        Check(deep.Length < 8000, "deep stack bounded");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void DeepDiagnosticStack(int depth)
    {
        if (depth > 0) DeepDiagnosticStack(depth - 1);
        else throw new LiteException(110, HostileDetail);
    }

    private static void DiagnosticBudgets(string directory)
    {
        DatabaseFailureBudget budget = new();
        Exception lite = new LiteException(999, HostileDetail);
        for (int i = 0; i < 4; i++) Check(budget.TryTake(lite, 0, out long suppressed) && suppressed == 0, "initial budget");
        for (int i = 0; i < 20; i++) Check(!budget.TryTake(lite, 59, out _), "repeats suppressed");
        Check(budget.TryTake(new ObjectDisposedException(HostileDetail), 20, out _), "different disposed failure visible during LiteDB flood");
        Check(budget.TryTake(new InvalidOperationException(HostileDetail), 20, out _), "other error budget independent");
        Check(budget.TryTake(lite, 60, out long count) && count == 20, "new window reports suppression count");
        Check(budget.TryTake(lite, 61, out count) && count == 0, "suppression count consumed once");
    }

    private static void RollbackPreservesOriginal(string _)
    {
        Exception original = DiagnosticException(0);
        Exception secondary = new ObjectDisposedException(HostileDetail);
        List<string> messages = [];
        DatabaseDiagnostics diagnostics = new(messages.Add);
        Exception actual = Throws<Exception>(() => FailingDiagnosticTransaction(original, secondary, e => diagnostics.Report(DatabaseStage.Activities, e, rollback: true)));
        Check(ReferenceEquals(original, actual), "original object preserved");
        Check(actual.StackTrace!.Contains(nameof(FailingDiagnosticTransaction)), "original throwing stack preserved");
        Check(messages.Single().Contains("operation=rollback stage=activities"), "secondary logged separately");
        Check(messages.Single().Contains("System.ObjectDisposedException"), "secondary type retained");
        Check(!messages.Single().Contains("PRIVATE"), "secondary redacted");
        actual = Throws<Exception>(() => FailingDiagnosticTransaction(original, secondary, _ => throw new Exception(HostileDetail)));
        Check(ReferenceEquals(original, actual), "throwing rollback logger cannot mask original");
    }

    private static void FailingDiagnosticTransaction(Exception original, Exception secondary, Action<Exception> report)
    {
        try { throw original; }
        catch { DatabaseDiagnostics.Rollback(() => throw secondary, report); throw; }
    }

    private static void WorkerDiagnostics(string directory)
    {
        using LiteDatabase db = new(Path.Combine(directory, "records.db"));
        List<string> messages = [];
        using ActivityStore worker = new(db, new CombatStore(db), new PlayerActionStore(db), new CookingStore(db), messages.Add);
        // Fail a real activity insert through an oversized indexed account.
        Check(worker.Enqueue(new ActivityBatch(activities: [Piece(HostileDetail, Actor(new string('x', 4096), HostileDetail))])), "failing batch queued");
        worker.QueryAsync("world", Start, End, AllOptions()).GetAwaiter().GetResult();
        string first = messages.Single();
        Check(first.Contains("operation=execute stage=activities") && first.Contains("LiteDB.LiteException code="), "initiating write stage and code");
        Check(first.Contains("activities=1") && first.Contains("queueBatches=1") && first.Contains("disposed=false") && first.Contains("thread="), "scalar batch and worker context");
        Check(!first.Contains("PRIVATE") && !first.Contains(new string('x', 30)) && !first.Contains(directory), "record and path redaction");
        Equal(0, db.GetCollection("activity_observations").Count(), "transaction still rolls back");
        db.Dispose();
        Check(worker.Enqueue(new ActivityBatch(activities: [Piece("disposed")])), "disposed failure queued");
        Exception queryFailure = Throws<Exception>(() => worker.QueryAsync("world", Start, End, AllOptions()).GetAwaiter().GetResult());
        Check(queryFailure is LiteException or ObjectDisposedException, "disposed engine failure propagated unchanged");
        worker.Dispose();
        Throws<ObjectDisposedException>(() => worker.TryClaimReportAsync(HostileDetail, Start).GetAwaiter().GetResult());
        Check(messages.Any(x => x.Contains("stage=activities") && (x.Contains("LiteDB.LiteException") || x.Contains("System.ObjectDisposedException"))), "disposed engine write failure visible");
        Check(messages.Any(x => x.Contains("stage=query")), "query failure logged and propagated");
        Check(messages.Any(x => x.Contains("stage=claim")), "claim failure logged and propagated");
        Check(messages.Any(x => x.Contains("stage=drain") && x.Contains("disposed=true")), "drain failure has lifecycle state");
        Check(messages.All(x => !x.Contains("PRIVATE") && !x.Contains(directory)), "all failure paths redacted");
    }

    private static void DiagnosticCallbackFailure(string directory)
    {
        using LiteDatabase db = new(Path.Combine(directory, "records.db"));
        using ActivityStore worker = new(db, new CombatStore(db), new PlayerActionStore(db), new CookingStore(db), _ => throw new Exception(HostileDetail));
        Check(worker.Enqueue(new ActivityBatch(activities: [Piece("bad", Actor(new string('x', 4096)))])), "failure queued with throwing logger");
        Check(worker.Enqueue(new ActivityBatch(activities: [Piece("good")])), "subsequent batch queued");
        Equal(1L, worker.QueryAsync("world", Start, End, AllOptions()).GetAwaiter().GetResult().Rows[ReportMetric.NewPieces].Single().Count, "logging cannot poison serial tail");
        Throws<ArgumentException>(() => worker.QueryAsync("world", End, Start, AllOptions()).GetAwaiter().GetResult());
        Check(worker.TryClaimReportAsync("valid", Start).GetAwaiter().GetResult(), "report exception and logger cannot poison tail");
    }
}
