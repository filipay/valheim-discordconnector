using DiscordConnector.Records;
using LiteDB;

internal static partial class Program
{
    private static CookingSlotSnapshot Slot(int slot = 0, string epoch = "epoch", CookingStatus status = CookingStatus.NotDone) =>
        new("world", "station", slot, 123, "RawMeat", status, 0, epoch);
    private static CookingChange Cook(CookingChangeKind kind, string generation = "generation", int slot = 0, string epoch = "epoch",
        long sequence = 1, long observationEpoch = 1) =>
        new(kind, Slot(slot, epoch, kind == CookingChangeKind.Completed ? CookingStatus.Done : CookingStatus.NotDone),
            generation, observationEpoch, sequence, kind == CookingChangeKind.AcceptedInput ? Actor() : null,
            epoch == "new-epoch" ? Start.AddTicks(2) : generation == "restart-pending" ? Start.AddTicks(1) : Start,
            kind == CookingChangeKind.Completed ? new CookingConversion("CookedMeat", 1) : null);

    private static void CookingGenerationRestart(string directory)
    {
        string path = Path.Combine(directory, "records.db");
        using (LiteDatabase db = new(path))
        {
            CookingStore cooking = new(db);
            using ActivityStore worker = new(db, new CombatStore(db), new PlayerActionStore(db), cooking, _ => { });
            Equal(cooking.ObservationEpoch, worker.CookingObservationEpoch, "capture epoch exposed without database I/O");
            Equal(3, new ActivityBatch(cooking: [Cook(CookingChangeKind.AcceptedInput), Cook(CookingChangeKind.Completed)]).PhysicalDocumentCount,
                "cooking completion reserves slot update plus credit");
            Check(worker.Enqueue(new ActivityBatch(cooking: [Cook(CookingChangeKind.AcceptedInput), Cook(CookingChangeKind.Completed),
                Cook(CookingChangeKind.Completed)])), "ordered generation and completion");
            CombinedReportSnapshot result = worker.QueryAsync("world", Start, End, AllOptions()).GetAwaiter().GetResult();
            Equal(1L, result.Rows[ReportMetric.CookingCompletions].Single().Count, "one output once");
            Check(cooking.Apply(Cook(CookingChangeKind.AcceptedInput, "unfinished", 1)), "unfinished generation");
            Check(cooking.Apply(Cook(CookingChangeKind.AcceptedInput, "burn", 2)), "burn input");
            Check(cooking.Apply(new CookingChange(CookingChangeKind.Invalidate, Slot(2, status: CookingStatus.Burnt), "burn", cooking.ObservationEpoch, 1, null, Start)), "burn invalidation");
            Check(!cooking.Apply(Cook(CookingChangeKind.Completed, "burn", 2)), "burn cannot later complete");
            cooking.InvalidatePending();
            Check(!cooking.Apply(Cook(CookingChangeKind.Completed, "unfinished", 1)), "worker failure closes pending provenance without database I/O");
            Check(cooking.Apply(new CookingChange(CookingChangeKind.AcceptedInput, Slot(1), "restart-pending", cooking.ObservationEpoch, 2, Actor(), Start.AddTicks(1))), "new acceptance after failure");
            // A failed completion transaction must not mark the slot done or consume its generation.
            db.GetCollection("cooking_completions_v1").EnsureIndex("OutputPrefab", true);
            Check(cooking.Apply(Cook(CookingChangeKind.AcceptedInput, "retry", 3)), "retry input");
            Throws<LiteException>(() => cooking.Apply(Cook(CookingChangeKind.Completed, "retry", 3)));
            db.GetCollection("cooking_completions_v1").DropIndex("OutputPrefab");
            Check(cooking.Apply(Cook(CookingChangeKind.Completed, "retry", 3)), "completion failure left provenance pending");
            Check(cooking.Apply(Cook(CookingChangeKind.AcceptedInput, "old-unfinished", 4)), "older input");
            Check(cooking.Apply(new CookingChange(CookingChangeKind.Invalidate, Slot(4), "old-unfinished", cooking.ObservationEpoch, 1, null, Start)), "older input invalidated");
            Check(cooking.Apply(Cook(CookingChangeKind.AcceptedInput, "A", 4, sequence: 2)), "equal-time generation A accepted");
            Check(cooking.Apply(Cook(CookingChangeKind.Completed, "A", 4, sequence: 2)), "equal-time generation A completed");
            Check(cooking.Apply(Cook(CookingChangeKind.AcceptedInput, "B", 4, sequence: 3)), "equal-time ordered generation B accepted");
            Check(!cooking.Apply(Cook(CookingChangeKind.AcceptedInput, "old-unfinished", 4)), "unfinished stale generation cannot resurrect");
            Check(!cooking.Apply(Cook(CookingChangeKind.Completed, "old-unfinished", 4)), "unfinished stale completion cannot affect B");
            Check(!cooking.Apply(Cook(CookingChangeKind.AcceptedInput, "A", 4, sequence: 2)), "completed stale A acceptance rejected");
            Check(!cooking.Apply(Cook(CookingChangeKind.Completed, "A", 4, sequence: 2)), "completed stale A completion rejected");
            Check(!cooking.Apply(Cook(CookingChangeKind.AcceptedInput, "different-id-same-sequence", 4, sequence: 3)), "sequence cannot be bypassed with another ID");
            Check(cooking.Apply(Cook(CookingChangeKind.Completed, "B", 4, sequence: 3)), "equal-time generation B completed");
            Check(!cooking.Apply(Cook(CookingChangeKind.Completed, "B", 4, sequence: 3)), "B completion once");
            Check(!cooking.Apply(Cook(CookingChangeKind.AcceptedInput, "A", 4, sequence: 2)), "A acceptance replay after B completed");
            Check(!cooking.Apply(Cook(CookingChangeKind.Completed, "A", 4, sequence: 2)), "A completion replay after B completed");
            Equal(4L, cooking.Query("world", Start, End).Rows[ReportMetric.CookingCompletions].Single().Count, "A and B each credited once");
        }
        using (LiteDatabase db = new(path))
        {
            CookingStore cooking = new(db);
            Equal(2L, cooking.ObservationEpoch, "durable observation epoch advanced on reopen");
            Check(!cooking.Apply(Cook(CookingChangeKind.Completed)), "completed replay after restart");
            Check(!cooking.Apply(Cook(CookingChangeKind.Completed, "restart-pending", 1, sequence: 2)), "old provenance cannot resume even with old epoch");
            Check(!cooking.Apply(Cook(CookingChangeKind.Completed, "restart-pending", 1, "new-epoch", sequence: 2, observationEpoch: 2)), "item equality cannot bridge epoch");
            Check(!cooking.Apply(Cook(CookingChangeKind.AcceptedInput, "unfinished", 1)), "old generation replay cannot resurrect provenance");
            Check(cooking.Apply(Cook(CookingChangeKind.AcceptedInput, "new-generation", 1, "new-epoch", observationEpoch: 2)), "fresh accepted transition with reset sequence");
            Check(!cooking.Apply(Cook(CookingChangeKind.AcceptedInput, "restart-pending", 1, sequence: 2)), "prior observation epoch cannot replace newer input despite higher sequence");
            Check(!cooking.Apply(Cook(CookingChangeKind.AcceptedInput, "old-unfinished", 4)), "old unfinished acceptance rejected after reopen");
            Check(!cooking.Apply(Cook(CookingChangeKind.Completed, "A", 4, sequence: 2)), "A completion replay after reopen");
            Check(!cooking.Apply(Cook(CookingChangeKind.Completed, "B", 4, sequence: 3)), "B completion replay after reopen");
            Check(cooking.Apply(Cook(CookingChangeKind.Completed, "new-generation", 1, "new-epoch", observationEpoch: 2)), "fresh epoch completion");
            Equal(5L, cooking.Query("world", Start, End).Rows[ReportMetric.CookingCompletions].Single().Count, "durable outputs only");
            Equal(0, cooking.Query("WORLD", Start, End).Rows.Count, "ordinal cooking world");
        }
    }
}
