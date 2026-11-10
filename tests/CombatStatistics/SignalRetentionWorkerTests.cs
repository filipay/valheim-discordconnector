using System.Reflection;
using DiscordConnector.Records;
using LiteDB;

internal static partial class Program
{
    private static void WorkerTail(ActivityStore worker, Task tail) => typeof(ActivityStore)
        .GetField("tail", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(worker, tail);

    private static ObservationLossSnapshot RetentionLossSnapshot() => new("world", ReportMetric.ReportedKillCredits, "retention",
        new Dictionary<ObservationRejection, long> { [ObservationRejection.QueueFull] = 1 }, 0, Start);

    private static void RetentionQueueBounds(string directory)
    {
        using LiteDatabase db = new(Path.Combine(directory, "records.db"));
        new ReportedSignalStore(db).Insert(Signal("expired", timestamp: Start.AddDays(-91)));
        ObservationLoss loss = NewLoss();
        using ActivityStore worker = new(db, new CombatStore(db), new PlayerActionStore(db), new CookingStore(db), _ => { }, observationLoss: loss);
        TaskCompletionSource barrier = new(TaskCreationOptions.RunContinuationsAsynchronously);
        WorkerTail(worker, barrier.Task);
        try
        {
            for (int i = 0; i < 255; i++) Check(worker.Enqueue(new ActivityBatch()), "fill ordinary capacity");
            Check(worker.MaintainSignalRetention(Start, 0).IsCompletedSuccessfully, "full ordinary queue skips maintenance");
            Equal(255, worker.PendingWork.Batches, "maintenance cannot spend reserved diagnostic slot");
            Task diagnostic = worker.ReconcileLossAsync(RetentionLossSnapshot());
            Equal(256, worker.PendingWork.Batches, "diagnostics retain the last slot");
            Check(!diagnostic.IsCompleted, "diagnostics remain on blocked tail");
            Check(worker.MaintainSignalRetention(Start, 60).IsCompletedSuccessfully, "completely full queue also skips");
            Equal(256, worker.PendingWork.Batches, "no 257th job");
        }
        finally { barrier.SetResult(); }
        worker.QueryAsync("world", Start, End, AllSignalOptions).GetAwaiter().GetResult();
        worker.MaintainSignalRetention(Start, 119).GetAwaiter().GetResult();
        Equal(1, SignalDetailCount(db), "full skip consumes its cadence, not a per-frame retry");
        worker.MaintainSignalRetention(Start, 120).GetAwaiter().GetResult();
        Equal(0, SignalDetailCount(db), "later bounded opportunity prunes expired detail");
    }

    private static void RetentionCadenceAndDrain(string directory)
    {
        using LiteDatabase db = new(Path.Combine(directory, "records.db"));
        ReportedSignalStore fixture = new(db);
        fixture.Insert(Signal("cutoff", timestamp: Start.AddDays(-90)));
        using ActivityStore worker = new(db, new CombatStore(db), new PlayerActionStore(db), new CookingStore(db), _ => { }, observationLoss: NewLoss());
        TaskCompletionSource barrier = new(TaskCreationOptions.RunContinuationsAsynchronously);
        WorkerTail(worker, barrier.Task);
        Task? maintenance = null;
        try
        {
            maintenance = worker.MaintainSignalRetention(Start, 0);
            Check(!maintenance.IsCompleted, "maintenance runs only on existing tail");
            worker.MaintainSignalRetention(Start.AddDays(1), 60).GetAwaiter().GetResult();
            Equal(1, worker.PendingWork.Batches, "only one pending maintenance even after sixty seconds");
            for (int i = 0; i < 16; i++) Check(worker.Enqueue(new ActivityBatch(signals: [Signal("quota" + i)], signalPrincipal: Principal())), "principal reservations admitted");
            Check(!worker.Enqueue(new ActivityBatch(signals: [Signal("quota17")], signalPrincipal: Principal()), out var reason) && reason == ObservationRejection.PrincipalQuota,
                "maintenance never resets outstanding principal quotas");
            for (int i = 0; i < 238; i++) Check(worker.Enqueue(new ActivityBatch()), "maintenance counts against ordinary capacity");
            Check(!worker.Enqueue(new ActivityBatch()), "255 ordinary jobs includes maintenance");
            worker.ReconcileLossAsync(RetentionLossSnapshot());
            Equal(256, worker.PendingWork.Batches, "diagnostic reservation survives admitted maintenance");
        }
        finally { barrier.SetResult(); maintenance?.GetAwaiter().GetResult(); }
        worker.QueryAsync("world", Start, End, AllSignalOptions).GetAwaiter().GetResult();
        Equal(17, SignalDetailCount(db), "admission captures immutable UTC, not later pending caller time");
        worker.MaintainSignalRetention(Start.AddDays(1), 59).GetAwaiter().GetResult();
        Equal(17, SignalDetailCount(db), "59 seconds cannot admit");
        worker.MaintainSignalRetention(Start.AddDays(1), 60).GetAwaiter().GetResult();
        Equal(16, SignalDetailCount(db), "60 seconds may prune using the newly captured UTC");
        fixture.Insert(Signal("drain", timestamp: Start.AddDays(-88).AddTicks(-1)));
        TaskCompletionSource drainBarrier = new(TaskCreationOptions.RunContinuationsAsynchronously);
        WorkerTail(worker, drainBarrier.Task);
        try
        {
            Task admitted = worker.MaintainSignalRetention(Start.AddDays(2), 120);
            Task drain = worker.BeginDrain();
            Check(ReferenceEquals(admitted, drain) && !drain.IsCompleted, "BeginDrain joins admitted maintenance");
            worker.MaintainSignalRetention(Start.AddYears(1), 180).GetAwaiter().GetResult();
            Equal(1, worker.PendingWork.Batches, "seal rejects further maintenance without another job");
            drainBarrier.SetResult(); drain.GetAwaiter().GetResult();
        }
        finally { drainBarrier.TrySetResult(); }
        Equal(16, SignalDetailCount(db), "clean drain prunes only detail older than captured cutoff");
        Equal((0, 0), worker.PendingWork, "all reservations released");
        SignalReservationsReleased(worker);
    }

    private static void RetentionWorkerClocks(string directory)
    {
        using LiteDatabase db = new(Path.Combine(directory, "records.db"));
        ReportedSignalStore fixture = new(db);
        fixture.Insert(Signal("expired", timestamp: Start.AddDays(-91)));
        fixture.Insert(Signal("current"));
        using ActivityStore worker = new(db, new CombatStore(db), new PlayerActionStore(db), new CookingStore(db), _ => { }, observationLoss: NewLoss());
        foreach (double invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -1d })
            worker.MaintainSignalRetention(Start, invalid).GetAwaiter().GetResult();
        Equal(2, SignalDetailCount(db), "invalid monotonic clocks cannot prune or consume initial opportunity");
        Throws<ArgumentException>(() => worker.MaintainSignalRetention(DateTime.SpecifyKind(Start, DateTimeKind.Local), 0));
        worker.MaintainSignalRetention(Start, 100).GetAwaiter().GetResult();
        Equal(1, SignalDetailCount(db), "first valid opportunity prunes expired detail");
        foreach (double invalid in new[] { 0d, 99d, 159.999, double.NaN, double.PositiveInfinity })
            worker.MaintainSignalRetention(Start.AddYears(1), invalid).GetAwaiter().GetResult();
        Equal(1, SignalDetailCount(db), "backward/invalid clocks cannot reset cadence");
        worker.MaintainSignalRetention(Start.AddYears(1), 160).GetAwaiter().GetResult();
        Equal(0, SignalDetailCount(db), "forward wall clock expires existing detail");
        fixture.Insert(Signal("corrected"));
        worker.MaintainSignalRetention(Start, 220).GetAwaiter().GetResult();
        Equal(1L, worker.QueryAsync("world", Start, End, AllSignalOptions).GetAwaiter().GetResult().Rows[ReportMetric.ReportedKillCredits].Single().Count,
            "corrected wall clock retains new current detail");
    }

    private static void RetentionWorkerOrdering(string directory)
    {
        using LiteDatabase db = new(Path.Combine(directory, "records.db"));
        using ActivityStore worker = new(db, new CombatStore(db), new PlayerActionStore(db), new CookingStore(db), _ => { }, observationLoss: NewLoss());
        TaskCompletionSource barrier = new(TaskCreationOptions.RunContinuationsAsynchronously);
        WorkerTail(worker, barrier.Task);
        DateTime old = Start.AddDays(-91);
        Task<CombinedReportSnapshot>? before = null, after = null;
        try
        {
            Check(worker.Enqueue(new ActivityBatch(signals: [Signal("before", timestamp: old)], signalPrincipal: Principal())), "backdated insert queued before maintenance");
            before = worker.QueryAsync("world", old, End, AllSignalOptions);
            worker.MaintainSignalRetention(Start, 0);
            Check(worker.Enqueue(new ActivityBatch(signals: [Signal("after", timestamp: old)], signalPrincipal: Principal())), "backdated insert queued after maintenance");
            after = worker.QueryAsync("world", old, End, AllSignalOptions);
        }
        finally { barrier.SetResult(); }
        var first = before!.GetAwaiter().GetResult();
        Equal(1L, first.Rows[ReportMetric.ReportedKillCredits].Single().Count, "earlier queued insert/query sees original detail");
        var last = after!.GetAwaiter().GetResult();
        Equal(1L, last.Rows[ReportMetric.ReportedKillCredits].Single().Count, "earlier detail pruned, later insert stored normally");
        worker.MaintainSignalRetention(Start, 60).GetAwaiter().GetResult();
        Equal(0, SignalDetailCount(db), "next pass prunes the later expired insert");
    }

    private static void RetentionWorkerFailure(string directory)
    {
        using LiteDatabase db = new(Path.Combine(directory, "records.db"));
        ObservationLoss loss = NewLoss();
        List<string> warnings = [];
        using ManualResetEventSlim entered = new(), release = new();
        Action<string> warn = message =>
        {
            warnings.Add(message); entered.Set(); release.Wait(); throw new IOException("hostile logger token");
        };
        DatabaseDiagnostics diagnostics = new(warn);
        using ActivityStore worker = new(db, new CombatStore(db), new PlayerActionStore(db), new CookingStore(db), warn, diagnostics, loss);
        db.Dispose();
        Task? failed = null;
        try
        {
            failed = worker.MaintainSignalRetention(Start, 0);
            Check(entered.Wait(TimeSpan.FromSeconds(10)), "real database failure reaches maintenance warning");
            Check(!failed.IsCompleted, "executing job stays pending through throwing warning callback");
            worker.MaintainSignalRetention(Start.AddYears(1), 60).GetAwaiter().GetResult();
            Equal(1, worker.PendingWork.Batches, "executing maintenance prevents another admission");
        }
        finally { release.Set(); }
        Throws<Exception>(() => failed!.GetAwaiter().GetResult());
        Equal(0, worker.PendingWork.Batches, "failure releases maintenance slot");
        Check(diagnostics.HasFailures, "maintenance failure remains visible in shutdown diagnostics");
        Equal(0, loss.Retained, "maintenance failure is not missing or uncertain gameplay captures");
        worker.MaintainSignalRetention(Start, 59).GetAwaiter().GetResult();
        Equal(1, warnings.Count, "failure cannot retry before admission cadence");
        Throws<Exception>(() => worker.MaintainSignalRetention(Start, 60).GetAwaiter().GetResult());
        Equal(2, warnings.Count, "every admitted failure warns without a second throttle");
        new DatabaseDiagnostics(warnings.Add).SignalRetentionFailure();
        Equal(3, warnings.Count, "diagnostic instances do not share a static throttle");
        Check(warnings.All(message => message == "Database failure stage=signal-retention; maintenance failed; no gameplay replay."), "fixed sanitized maintenance reason, no exception text");
        Equal(0, worker.PendingWork.Batches, "repeated failures release the slot and pending marker");
    }

    private static void RetentionAttachmentFailure(string directory)
    {
        using LiteDatabase db = new(Path.Combine(directory, "records.db"));
        new ReportedSignalStore(db).Insert(Signal("expired", timestamp: Start.AddDays(-91)));
        using ActivityStore worker = new(db, new CombatStore(db), new PlayerActionStore(db), new CookingStore(db), _ => { }, observationLoss: NewLoss());
        WorkerTail(worker, null!);
        Throws<NullReferenceException>(() => worker.MaintainSignalRetention(Start, 0));
        Equal(0, worker.PendingWork.Batches, "attachment failure releases slot");
        WorkerTail(worker, Task.CompletedTask);
        worker.MaintainSignalRetention(Start, 59).GetAwaiter().GetResult();
        Equal(1, SignalDetailCount(db), "attachment failure keeps cadence");
        worker.MaintainSignalRetention(Start, 60).GetAwaiter().GetResult();
        Equal(0, SignalDetailCount(db), "attachment failure releases single-pending marker");
    }
}
