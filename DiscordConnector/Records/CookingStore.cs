using System;
using System.Collections.Generic;
using System.Linq;

using LiteDB;

namespace DiscordConnector.Records;

internal sealed class CookingStore
{
    private readonly LiteDatabase db;
    private readonly DatabaseDiagnostics? diagnostics;
    private readonly ILiteCollection<BsonDocument> slots, completions;
    private string session = Guid.NewGuid().ToString("N");
    internal long ObservationEpoch { get; }

    internal CookingStore(LiteDatabase db, DatabaseDiagnostics? diagnostics = null)
    {
        this.diagnostics = diagnostics;
        this.db = db ?? throw new ArgumentNullException(nameof(db));
        slots = db.GetCollection("cooking_slots_v1"); completions = db.GetCollection("cooking_completions_v1");
        ObservationValues.Index(slots); ObservationValues.Index(completions);
        slots.EnsureIndex("State"); slots.EnsureIndex("Session");
        // One fixed metadata row fences earlier workers/restarts without a growing epoch history.
        if (!db.BeginTrans()) throw new InvalidOperationException("Nested store transactions are not supported.");
        try
        {
            const string epochId = "cooking-observation-epoch:v1";
            BsonDocument? previous = slots.FindById(epochId);
            ObservationEpoch = checked((previous == null ? 0L : previous["Epoch"].AsInt64) + 1);
            slots.Upsert(new BsonDocument { ["_id"] = epochId, ["Epoch"] = ObservationEpoch });
            db.Commit();
        }
        catch { DatabaseDiagnostics.Rollback(() => db.Rollback(), e => diagnostics?.Report(DatabaseStage.Cooking, e, rollback: true)); throw; }
    }

    internal bool Apply(CookingChange change)
    {
        if (change == null) throw new ArgumentNullException(nameof(change));
        if (change.ObservationEpoch != ObservationEpoch) return false;
        CookingSlotSnapshot snapshot = change.Snapshot;
        string slotId = ObservationValues.Key(snapshot.WorldId, snapshot.StationId, ObservationValues.Number(snapshot.Slot));
        string completionId = ObservationValues.Key(snapshot.WorldId, snapshot.StationId, ObservationValues.Number(snapshot.Slot), change.Generation);
        BsonDocument? current = slots.FindById(slotId);
        if (change.Kind == CookingChangeKind.AcceptedInput)
        {
            if (completions.Exists(BsonExpression.Create("_id = @0", completionId))) return false;
            if (current != null && (current["Generation"].AsString == change.Generation
                || current["ObservationEpoch"].AsInt64 == ObservationEpoch
                    && current["GenerationSequence"].AsInt64 >= change.GenerationSequence)) return false;
            // Old-session pending rows are unknown, not active slots and never completion candidates.
            if ((current == null || current["State"].AsString != "Pending" || current["Session"].AsString != session)
                && slots.Find(BsonExpression.Create("WorldId = @0 AND State = @1 AND Session = @2", snapshot.WorldId, "Pending", session))
                    .Count(x => string.Equals(x["WorldId"].AsString, snapshot.WorldId, StringComparison.Ordinal)) >= 4096) return false;
            BsonDocument next = ObservationValues.Document(slotId, snapshot.WorldId, change.TimestampUtc, change.Actor);
            next["StationId"] = snapshot.StationId; next["Slot"] = snapshot.Slot; next["Owner"] = snapshot.Owner;
            next["InputPrefab"] = snapshot.ItemPrefab; next["CollectorEpoch"] = snapshot.CollectorEpoch;
            next["Generation"] = change.Generation; next["Session"] = session; next["State"] = "Pending";
            next["ObservationEpoch"] = ObservationEpoch; next["GenerationSequence"] = change.GenerationSequence;
            slots.Upsert(next); return true;
        }

        if (current == null || current["Generation"].AsString != change.Generation || current["State"].AsString != "Pending"
            || current["ObservationEpoch"].AsInt64 != change.ObservationEpoch || current["GenerationSequence"].AsInt64 != change.GenerationSequence) return false;
        if (change.Kind == CookingChangeKind.Invalidate)
        {
            current["State"] = "Unknown";
            current["TimestampUtc"] = Math.Max(current["TimestampUtc"].AsInt64, change.TimestampUtc.Ticks);
            slots.Update(current); return true;
        }
        if (current["Session"].AsString != session || current["CollectorEpoch"].AsString != snapshot.CollectorEpoch
            || current["Owner"].AsInt64 != snapshot.Owner || current["InputPrefab"].AsString != snapshot.ItemPrefab)
        {
            current["State"] = "Unknown"; slots.Update(current); return false;
        }
        if (completions.Exists(BsonExpression.Create("_id = @0", completionId))) return false;
        if (!db.BeginTrans()) throw new InvalidOperationException("Nested store transactions are not supported.");
        try
        {
            // Identity comes only from the accepted slot, never from a completion caller or reconnect.
            ActivityActor actor = new(current["PlayerId"].AsString, current["PlayerName"].AsString,
                current["ConnectionToken"].AsString, ObservationValues.Date(current["NameObservedUtc"]));
            BsonDocument credit = ObservationValues.Document(completionId, snapshot.WorldId, change.TimestampUtc, actor);
            credit["StationId"] = snapshot.StationId; credit["Slot"] = snapshot.Slot; credit["Generation"] = change.Generation;
            credit["ObservationEpoch"] = ObservationEpoch; credit["GenerationSequence"] = change.GenerationSequence;
            credit["InputPrefab"] = current["InputPrefab"]; credit["OutputPrefab"] = change.Conversion!.OutputPrefab;
            credit["Units"] = change.Conversion.Units;
            completions.Insert(credit);
            current["State"] = "Completed"; current["TimestampUtc"] = change.TimestampUtc.Ticks;
            slots.Update(current);
            db.Commit(); return true;
        }
        catch { DatabaseDiagnostics.Rollback(() => db.Rollback(), e => diagnostics?.Report(DatabaseStage.Cooking, e, rollback: true)); throw; }
    }

    // Fail closed without another database write when an ordered batch could not be processed.
    internal void InvalidatePending() => session = Guid.NewGuid().ToString("N");

    internal CombinedReportSnapshot Query(string world, DateTime startUtc, DateTime endUtc)
    {
        ObservationValues.Range(world, startUtc, endUtc);
        Dictionary<ReportMetric, IReadOnlyList<ReportRow>> rows = new();
        ObservationValues.Put(rows, ReportMetric.CookingCompletions, ObservationValues.Aggregate(
            ObservationValues.Period(completions, world, startUtc, endUtc, "PlayerKey"), countField: "Units"));
        return new CombinedReportSnapshot(world, rows);
    }
}
