using DiscordConnector.Records;
using DiscordConnector.Leaderboards;
using LiteDB;

internal static partial class Program
{
    private static void CoalescedLossCoverage(string directory)
    {
        using LiteDatabase db = new(Path.Combine(directory, "records.db"));
        using ActivityStore worker = new(db, new CombatStore(db), new PlayerActionStore(db), new CookingStore(db), _ => { });
        ActivityCollector collector = new(worker.Enqueue, _ => { });
        collector.Tick(Start, 0, "world", new(floorPickups: true));
        for (int i = 0; i < 3; i++)
            collector.Coverage(ReportMetric.InferredPickups, CoverageState.Partial, Start, CoverageReason.Dropped);
        collector.FlushThrough(End);
        CombinedReportSnapshot result = worker.QueryAsync("world", Start, End, new(floorPickups: true)).GetAwaiter().GetResult();
        Equal(3L, result.Coverage.Single().Counters[CoverageReason.Dropped], "three drops must not coalesce into one");
        collector.Coverage(ReportMetric.InferredPickups, CoverageState.Partial, Start, CoverageReason.Dropped, 4);
        collector.Coverage(ReportMetric.InferredPickups, CoverageState.Partial, Start, CoverageReason.Invalid, 2);
        collector.Coverage(ReportMetric.InferredPickups, CoverageState.Observing, End);
        collector.FlushThrough(End);
        var note = worker.QueryAsync("world", Start, End, new(floorPickups: true)).GetAwaiter().GetResult().Coverage.Single();
        Equal(7L, note.Counters[CoverageReason.Dropped], "changed reason cannot overwrite an earlier numerical delta");
        Equal(2L, note.Counters[CoverageReason.Invalid], "independent historical counter");
        Equal(CoverageState.Observing, note.State, "latest state is retained for every coalesced reason");
    }

    private static ObservationLoss NewLoss(string epoch = "epoch") => new(_ => { }, epoch, () => Start);
    private static CoverageSnapshot LossNote(ActivityStore worker, string world = "world") =>
        worker.QueryAsync(world, Start, End, new(reportedKillCredits: true)).GetAwaiter().GetResult().Coverage.Single();

    private static void LossReplayRejections(string directory)
    {
        using LiteDatabase db = new(Path.Combine(directory, "records.db"));
        ObservationLoss loss = NewLoss();
        using ActivityStore worker = new(db, new CombatStore(db), new PlayerActionStore(db), new CookingStore(db), _ => { }, observationLoss: loss);
        ManualCommandRouting routing = new(() => 0, loss);
        int gameplayCalls = 0;
        void Attempt(int id, bool twice = false)
        {
            var frame = routing.Enter(new object());
            for (int witness = 0; witness < (twice ? 2 : 1); witness++)
                routing.Complete(routing.ObserveSignal(1, 1, "connection" + id / 128, "world", id, "beneficiary",
                    RoutingObservationKind.KillCredit, 0, () => gameplayCalls++, Principal("origin" + id / 128, "connection" + id / 128)), null);
            routing.Finish(frame, null);
        }
        for (int i = 0; i < 4096; i++) Attempt(i);
        Attempt(4096, true);
        Attempt(0, true);
        worker.MaintainLoss(0).GetAwaiter().GetResult();
        Equal(4096, gameplayCalls, "rejections and reconciliation invoke no gameplay callbacks");
        CoverageSnapshot note = LossNote(worker);
        Equal(1L, note.KnownLosses, "new full-cache refusal counted once across decoded stages; duplicate is not loss");
        Equal(1L, note.Rejections[ObservationRejection.ReplayCapacity], "exact Task 2 reason retained");
        Equal(0L, note.UncertainWrites, "capacity rejection is confirmed, not uncertain");
        Equal(0, db.GetCollection("reported_kill_credits_v1").Count(), "diagnostic submission never inserts gameplay");
    }

    private static void LossAmbiguousAcknowledgment(string directory)
    {
        using LiteDatabase db = new(Path.Combine(directory, "records.db"));
        ObservationLoss loss = NewLoss();
        using ActivityStore worker = new(db, new CombatStore(db), new PlayerActionStore(db), new CookingStore(db), _ => { }, observationLoss: loss);
        int gameplayEnqueues = 0, submits = 0;
        NativeSignalCollector rejected = new(_ => { gameplayEnqueues++; return false; }, _ => { }, loss);
        for (int i = 0; i < 3; i++) rejected.Observe(Signal(i.ToString()), Principal());
        Check(worker.Enqueue(new ActivityBatch(signals: [Signal("accepted-once")], signalPrincipal: Principal())), "one accepted gameplay batch");
        loss.RecordUncertain("world", ReportMetric.ReportedKillCredits);
        Task Submit(ObservationLossSnapshot snapshot)
        {
            submits++;
            return worker.ReconcileLossAsync(snapshot).ContinueWith(committed =>
            {
                committed.GetAwaiter().GetResult();
                throw new IOException("acknowledgment lost after the real commit");
            });
        }
        loss.Maintain(0, Submit).GetAwaiter().GetResult();
        Equal(1, loss.UnacknowledgedKeys, "lost acknowledgment retains the diagnostic value");
        for (int i = 0; i < 10; i++) loss.Maintain(i, Submit).GetAwaiter().GetResult();
        Equal(1, submits, "failed acknowledgment cannot trigger immediate retries");
        loss.Maintain(10, Submit).GetAwaiter().GetResult();
        Equal(2, submits, "absolute snapshot alone retried at the next ten-second opportunity");
        worker.MaintainLoss(20).GetAwaiter().GetResult();
        worker.MaintainLoss(30).GetAwaiter().GetResult();
        Equal(0, loss.UnacknowledgedKeys, "successful latest acknowledgment clears only the dirty marker");
        var note = LossNote(worker);
        Equal(3L, note.KnownLosses, "three coalesced queue refusals survive repeated absolute submissions");
        Equal(1L, note.UncertainWrites, "uncertainty is separate and also idempotent");
        Equal(3, gameplayEnqueues, "refused gameplay is never retried");
        Equal(1, db.GetCollection("reported_kill_credits_v1").Count(), "accepted gameplay inserted only once");
        Equal(1, db.GetCollection("server_observation_v2_loss").Count(), "one document per epoch/key");
    }

    private static void LossEpochsAndReopen(string directory)
    {
        string path = Path.Combine(directory, "records.db");
        ObservationLossSnapshot Snapshot(string epoch, long count, long uncertain, ObservationRejection reason = ObservationRejection.QueueFull) =>
            new("world", ReportMetric.ReportedKillCredits, epoch, new Dictionary<ObservationRejection, long> { [reason] = count }, uncertain, Start);
        using (LiteDatabase db = new(path))
        {
            using ActivityStore worker = new(db, new CombatStore(db), new PlayerActionStore(db), new CookingStore(db), _ => { }, observationLoss: NewLoss());
            worker.Enqueue(new ActivityBatch(coverage: [new("world", ReportMetric.ReportedKillCredits, CoverageState.Observing, Start, CoverageReason.Dropped, 7)]));
            var before = db.GetCollection("server_observation_v1_coverage");
            worker.ReconcileLossAsync(Snapshot("one", 3, 2)).GetAwaiter().GetResult();
            string historical = before.FindAll().Single().ToString();
            worker.ReconcileLossAsync(Snapshot("one", 1, 1)).GetAwaiter().GetResult();
            worker.ReconcileLossAsync(Snapshot("one", 3, 2)).GetAwaiter().GetResult();
            worker.ReconcileLossAsync(Snapshot("one", 4, 1, ObservationRejection.PrincipalQuota)).GetAwaiter().GetResult();
            worker.ReconcileLossAsync(Snapshot("two", 5, 3)).GetAwaiter().GetResult();
            var other = new ObservationLossSnapshot("WORLD", ReportMetric.ReportedKillCredits, "one", new Dictionary<ObservationRejection, long> { [ObservationRejection.QueueFull] = 99 }, 99, Start);
            worker.ReconcileLossAsync(other).GetAwaiter().GetResult();
            var note = LossNote(worker);
            Equal(12L, note.KnownLosses, "per-field maxima in one epoch, sum once across distinct epochs");
            Equal(5L, note.UncertainWrites, "lower uncertainty never overwrites a committed total");
            Equal(7L, note.Counters[CoverageReason.Dropped], "historical counters remain separate, not replaced or added twice");
            Equal(CoverageState.Partial, note.State, "new diagnostic evidence qualifies an observing state without rewriting history");
            Equal(historical, before.FindAll().Single().ToString(), "old coverage document byte representation is unchanged");
            Equal(99L, LossNote(worker, "WORLD").KnownLosses, "ordinal world filtering");
        }
        using (LiteDatabase db = new(path))
        {
            using ActivityStore worker = new(db, new CombatStore(db), new PlayerActionStore(db), new CookingStore(db), _ => { }, observationLoss: NewLoss("three"));
            worker.ReconcileLossAsync(Snapshot("one", 0, 0)).GetAwaiter().GetResult();
            Equal(12L, LossNote(worker).KnownLosses, "reopen and stale resubmission preserve maxima");
            Equal(5L, LossNote(worker).UncertainWrites, "reopen preserves uncertainty separately");
        }
    }

    private static void LossBounds(string directory)
    {
        int warnings = 0;
        ObservationLoss loss = new(_ => { warnings++; throw new Exception("warning sink"); }, "epoch", () => Start);
        for (int i = 0; i < 64; i++) loss.Record("world" + i, ReportMetric.ReportedKillCredits, ObservationRejection.QueueFull);
        for (int i = 0; i < 64; i++) loss.Maintain(i * 10, _ => Task.CompletedTask).GetAwaiter().GetResult();
        Equal(0, loss.UnacknowledgedKeys, "acknowledged states retain their cumulative baselines");
        for (int i = 0; i < 10; i++) loss.Record("65th", ReportMetric.ReportedKillCredits, ObservationRejection.QueueFull);
        loss.Record(new string('w', 257), ReportMetric.ReportedKillCredits, ObservationRejection.QueueFull);
        Equal(64, loss.Retained, "hard state bound; no eviction of unacknowledged counters");
        Check(loss.Snapshot("65th", ReportMetric.ReportedKillCredits) == null && loss.Incomplete, "65th key refused with incomplete coverage");
        Equal(1, warnings, "overflow warning bounded even when sink throws");
        loss.Record("world0", ReportMetric.ReportedKillCredits, ObservationRejection.QueueFull, long.MaxValue);
        loss.Record("world0", ReportMetric.ReportedKillCredits, ObservationRejection.QueueFull);
        loss.RecordUncertain("world0", ReportMetric.ReportedKillCredits, long.MaxValue);
        loss.RecordUncertain("world0", ReportMetric.ReportedKillCredits);
        loss.Record("ignored", ReportMetric.ReportedKillCredits, ObservationRejection.Duplicate, long.MaxValue);
        loss.Record("ignored", ReportMetric.ReportedKillCredits, ObservationRejection.None);
        var snapshot = loss.Snapshot("world0", ReportMetric.ReportedKillCredits)!;
        Equal(long.MaxValue, snapshot.Rejections[ObservationRejection.QueueFull], "confirmed saturation");
        Equal(long.MaxValue, snapshot.UncertainWrites, "uncertainty saturation independent");
        using LiteDatabase db = new(Path.Combine(directory, "records.db"));
        using ActivityStore worker = new(db, new CombatStore(db), new PlayerActionStore(db), new CookingStore(db), _ => { }, observationLoss: loss);
        worker.ReconcileLossAsync(snapshot).GetAwaiter().GetResult();
        Equal(long.MaxValue, LossNote(worker, "world0").KnownLosses, "saturated totals survive real persistence/query");
        Throws<ArgumentException>(() => new ObservationLossSnapshot("world", ReportMetric.ReportedKillCredits, "epoch", new Dictionary<ObservationRejection, long> { [ObservationRejection.Duplicate] = 1 }, 0, Start));
        Throws<ArgumentException>(() => loss.Record("world", ReportMetric.ReportedKillCredits, ObservationRejection.QueueFull, -1));
        Throws<ArgumentOutOfRangeException>(() => loss.RecordUncertain("world", ReportMetric.ReportedKillCredits, -1));
        var counters = new Dictionary<ObservationRejection, long> { [ObservationRejection.QueueFull] = 2 };
        var immutable = new ObservationLossSnapshot("world", ReportMetric.ReportedKillCredits, "epoch", counters, 0, Start);
        counters[ObservationRejection.QueueFull] = 99;
        Equal(2L, immutable.Rejections[ObservationRejection.QueueFull], "snapshot is a defensive immutable copy");
    }

    private static void LossQueueReservation(string directory)
    {
        using LiteDatabase db = new(Path.Combine(directory, "records.db"));
        ObservationLoss loss = NewLoss();
        loss.Record("world", ReportMetric.ReportedKillCredits, ObservationRejection.QueueFull);
        using ActivityStore worker = new(db, new CombatStore(db), new PlayerActionStore(db), new CookingStore(db), _ => { }, observationLoss: loss);
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        TaskCompletionSource barrier = new(TaskCreationOptions.RunContinuationsAsynchronously);
        typeof(ActivityStore).GetField("tail", flags)!.SetValue(worker, barrier.Task);
        Task? diagnostic = null;
        try
        {
            for (int i = 0; i < 255; i++) Check(worker.Enqueue(new ActivityBatch()), "255 gameplay slots");
            Check(!worker.Enqueue(new ActivityBatch()), "reserved slot cannot admit gameplay");
            diagnostic = worker.MaintainLoss(0);
            Equal(256, (int)typeof(ActivityStore).GetField("pendingBatches", flags)!.GetValue(worker)!, "diagnostic occupies one existing slot, not a 257th");
            Check(!diagnostic.IsCompleted, "same serialized worker waits behind admitted gameplay");
            Check(worker.ReconcileLossAsync(loss.Snapshot("world", ReportMetric.ReportedKillCredits)!).IsFaulted, "second diagnostic job rejected while pending");
            worker.MaintainLoss(10).GetAwaiter().GetResult();
            Check(!worker.Enqueue(new ActivityBatch()), "total bound remains 256 with diagnostic pending");
        }
        finally { barrier.SetResult(); diagnostic?.GetAwaiter().GetResult(); }
        Equal(1L, LossNote(worker).KnownLosses, "reserved submission persisted once");
        int retries = 0;
        loss.Maintain(20, _ => { retries++; return Task.CompletedTask; }).GetAwaiter().GetResult();
        Equal(0, retries, "acknowledged snapshot is not submitted again");
        loss.Record("world", ReportMetric.ReportedKillCredits, ObservationRejection.QueueFull);
        worker.SubmitLossBeforeSeal(21).GetAwaiter().GetResult();
        worker.BeginDrain().GetAwaiter().GetResult();
        Equal(2L, db.GetCollection("server_observation_v2_loss").FindAll().Single()["Rejections"].AsDocument["QueueFull"].AsInt64, "one final submission before seal bypasses cadence");
        Check(worker.ReconcileLossAsync(loss.Snapshot("world", ReportMetric.ReportedKillCredits)!).IsFaulted, "sealed admission rejects diagnostics too");
    }

    private static void LossWriteFailure(string directory)
    {
        using LiteDatabase db = new(Path.Combine(directory, "records.db"));
        ObservationLoss loss = NewLoss();
        List<string> warnings = [];
        using ActivityStore worker = new(db, new CombatStore(db), new PlayerActionStore(db), new CookingStore(db), warnings.Add, observationLoss: loss);
        var collection = db.GetCollection("server_observation_v2_loss");
        collection.DropIndex("WorldId"); collection.EnsureIndex("WorldId", true);
        loss.Record("world", ReportMetric.ReportedKillCredits, ObservationRejection.QueueFull, 3);
        worker.MaintainLoss(0).GetAwaiter().GetResult();
        loss.Record("world", ReportMetric.ObservedConsumableUses, ObservationRejection.PrincipalQuota, 2);
        worker.MaintainLoss(10).GetAwaiter().GetResult();
        Equal(1, collection.Count(), "real unique-index failure rejected second key");
        Check(warnings.Any(x => x.Contains("stage=loss")), "diagnostic write failure is stage-labelled without gameplay replay");
        Check(loss.Snapshot("world", ReportMetric.ObservedConsumableUses)!.Rejections[ObservationRejection.PrincipalQuota] == 2, "failure retains latest value");
        collection.DropIndex("WorldId"); collection.EnsureIndex("WorldId");
        worker.MaintainLoss(19.999).GetAwaiter().GetResult();
        Equal(1, collection.Count(), "no retry before next opportunity");
        worker.MaintainLoss(20).GetAwaiter().GetResult();
        Equal(2, collection.Count(), "failed absolute value retried on maintenance");
        loss.Record("world", ReportMetric.ReportedKillCredits, ObservationRejection.QueueFull);
        int attempts = 0;
        loss.Maintain(30, _ => { attempts++; throw new InvalidOperationException("enqueue failure"); }).GetAwaiter().GetResult();
        loss.Maintain(31, _ => { attempts++; return Task.CompletedTask; }).GetAwaiter().GetResult();
        Equal(1, attempts, "throwing submission also retains value and cadence");
        worker.MaintainLoss(40).GetAwaiter().GetResult();
        Equal(4L, LossNote(worker).KnownLosses, "eventual reconciliation neither loses nor doubles increment");
        loss.Record("world", ReportMetric.ReportedKillCredits, ObservationRejection.QueueFull);
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        typeof(ActivityStore).GetField("tail", flags)!.SetValue(worker, null);
        worker.MaintainLoss(50).GetAwaiter().GetResult();
        Equal(1, loss.UnacknowledgedKeys, "real continuation-attachment refusal retains latest absolute snapshot");
        Equal(0, (int)typeof(ActivityStore).GetField("pendingBatches", flags)!.GetValue(worker)!, "attachment failure releases reserved diagnostic slot");
        Check(!(bool)typeof(ActivityStore).GetField("pendingDiagnostic", flags)!.GetValue(worker)!, "attachment failure releases single-job marker");
        typeof(ActivityStore).GetField("tail", flags)!.SetValue(worker, Task.CompletedTask);
        worker.MaintainLoss(60).GetAwaiter().GetResult();
        Equal(5L, LossNote(worker).KnownLosses, "latest snapshot recovers after actual enqueue attachment failure");
    }

    private static void LossCollectorBoundaries(string directory)
    {
        using LiteDatabase db = new(Path.Combine(directory, "records.db"));
        ObservationLoss loss = NewLoss();
        using ActivityStore worker = new(db, new CombatStore(db), new PlayerActionStore(db), new CookingStore(db), _ => { }, observationLoss: loss);
        int attempts = 0;
        ActivityCollector collector = new(_ => { attempts++; return false; }, _ => { }, loss) { ActorIsCurrent = _ => true };
        collector.Tick(Start, 0, "world", new(dodgeRolls: true));
        for (int i = 0; i < 3; i++) collector.ObserveAction(new("world", Actor(), PlayerActionKind.DodgeRoll, "action" + i, Start));
        collector.FlushThrough(End); collector.Reset(); collector.Tick(Start, 1, "other", new(dodgeRolls: true));
        Equal(1, attempts, "flush/reset/world transition never retry refused batch");
        Equal(3L, loss.Snapshot("world", ReportMetric.ObservedDodgeRolls)!.Rejections[ObservationRejection.QueueFull], "three discarded observations counted once and retained across world change");
        worker.MaintainLoss(0).GetAwaiter().GetResult();
        Equal(3L, worker.QueryAsync("world", Start, End, new(dodgeRolls: true)).GetAwaiter().GetResult().Coverage.Single().KnownLosses, "collector rejection reaches numerical query");
        TaskCompletionSource admission = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        typeof(ActivityStore).GetField("tail", flags)!.SetValue(worker, admission.Task);
        try
        {
            for (int i = 0; i < 16; i++) Check(worker.Enqueue(new ActivityBatch(signals: [Signal("accepted" + i)], signalPrincipal: Principal())), "signal quota fixture");
            NativeSignalCollector native = new(worker.Enqueue, _ => { });
            Check(!native.Observe(Signal("refused"), Principal()), "store-origin delegate refused");
            Equal(1L, loss.Snapshot("world", ReportMetric.ReportedKillCredits)!.Rejections[ObservationRejection.PrincipalQuota], "store preserves exact reason, collector does not count batch again as QueueFull");
            Equal(0L, loss.Snapshot("world", ReportMetric.ReportedKillCredits)!.Rejections[ObservationRejection.QueueFull], "no derived rejection category");
        }
        finally { admission.SetResult(); }
        worker.QueryAsync("world", Start, End, new()).GetAwaiter().GetResult();
    }

    private static void LossInFlightUpdate(string directory)
    {
        using LiteDatabase db = new(Path.Combine(directory, "records.db"));
        ObservationLoss loss = NewLoss();
        using ActivityStore worker = new(db, new CombatStore(db), new PlayerActionStore(db), new CookingStore(db), _ => { }, observationLoss: loss);
        TaskCompletionSource acknowledgment = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int submissions = 0;
        async Task Submit(ObservationLossSnapshot snapshot)
        {
            submissions++;
            await worker.ReconcileLossAsync(snapshot);
            await acknowledgment.Task;
        }
        loss.Record("world", ReportMetric.ReportedKillCredits, ObservationRejection.QueueFull);
        Task inFlight = loss.Maintain(0, Submit);
        loss.Record("world", ReportMetric.ReportedKillCredits, ObservationRejection.QueueFull, 2);
        loss.Maintain(10, Submit).GetAwaiter().GetResult();
        Equal(1, submissions, "at most one diagnostic acknowledgment may be pending");
        acknowledgment.SetResult(); inFlight.GetAwaiter().GetResult();
        Equal(1L, LossNote(worker).KnownLosses, "first immutable snapshot captures only its original absolute value");
        loss.Maintain(9.999, Submit).GetAwaiter().GetResult();
        Equal(1, submissions, "cadence survives a late acknowledgment");
        loss.Maintain(10, Submit).GetAwaiter().GetResult();
        Equal(2, submissions, "newer value remains dirty after old acknowledgment");
        Equal(3L, LossNote(worker).KnownLosses, "latest absolute total supersedes the old one without addition");
        loss.Maintain(20, Submit).GetAwaiter().GetResult();
        Equal(2, submissions, "latest acknowledged value stays clean");
    }

    private static void LossGameplayUncertainty(string directory)
    {
        using LiteDatabase db = new(Path.Combine(directory, "records.db"));
        ObservationLoss loss = NewLoss();
        int failedInsertInvocations = 0;
        using ActivityStore worker = new(db, new CombatStore(db), new PlayerActionStore(db), new CookingStore(db), message =>
        { if (message.Contains("stage=signal")) failedInsertInvocations++; }, observationLoss: loss);
        var gameplay = db.GetCollection("reported_kill_credits_v1");
        gameplay.DropIndex("WorldId"); gameplay.EnsureIndex("WorldId", true);
        Check(worker.Enqueue(new ActivityBatch(signals: [Signal("first"), Signal("failed")], signalPrincipal: Principal())), "partially failing real gameplay batch admitted");
        worker.QueryAsync("world", Start, End, new()).GetAwaiter().GetResult();
        worker.MaintainLoss(0).GetAwaiter().GetResult();
        var note = LossNote(worker);
        Equal(0L, note.KnownLosses, "write exception is not proof of absent gameplay");
        Equal(2L, note.UncertainWrites, "failed batch completion is uncertain for its captured records, even with a partial commit");
        Equal(1, gameplay.Count(), "first commit is retained, second write is not retried");
        Equal(1, failedInsertInvocations, "one real failed gameplay insertion reached its diagnostic callback");
        worker.ReconcileLossAsync(loss.Snapshot("world", ReportMetric.ReportedKillCredits)!).GetAwaiter().GetResult();
        Equal(1, gameplay.Count(), "repeated diagnostic submission cannot insert gameplay");
        Equal(1, failedInsertInvocations, "reconciliation cannot invoke the failed gameplay insertion again, even if durable dedup hides replays of the first row");
        Equal(2L, LossNote(worker).UncertainWrites, "uncertainty not incremented by reconciliation");
    }
}
