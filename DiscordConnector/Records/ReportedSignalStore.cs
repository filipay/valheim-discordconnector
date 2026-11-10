using System;
using System.Collections.Generic;

using LiteDB;

namespace DiscordConnector.Records;

internal sealed class ReportedSignalStore
{
    private readonly LiteDatabase db;
    private readonly ILiteCollection<BsonDocument> kills, consumables, dodgeHits;

    internal ReportedSignalStore(LiteDatabase db)
    {
        this.db = db ?? throw new ArgumentNullException(nameof(db));
        kills = db.GetCollection("reported_kill_credits_v1");
        consumables = db.GetCollection("observed_consumable_uses_v1");
        dodgeHits = db.GetCollection("reported_dodge_hit_notifications_v1");
        ObservationValues.Index(kills); ObservationValues.Index(consumables); ObservationValues.Index(dodgeHits);
    }

    internal bool Insert(ReportedSignalRecord record)
    {
        if (record == null) throw new ArgumentNullException(nameof(record));
        ILiteCollection<BsonDocument> collection = record.Kind == ReportedSignalKind.KillCredit ? kills :
            record.Kind == ReportedSignalKind.ConsumableUse ? consumables : dodgeHits;
        return PlayerActionStore.InsertUnique(collection, ObservationValues.Document(
            ObservationValues.Key(record.WorldId, record.Kind.ToString(), record.Token), record.WorldId, record.TimestampUtc, record.Actor));
    }

    internal int PruneExpired(DateTime nowUtc, int limit = 256)
    {
        ObservationValues.Utc(nowUtc);
        if (limit < 1 || limit > 256) throw new ArgumentOutOfRangeException(nameof(limit));
        long ticks = Math.Max(0, nowUtc.Ticks - 90 * TimeSpan.TicksPerDay);
        if (!db.BeginTrans()) throw new InvalidOperationException("Nested store transactions are not supported.");
        int removed = 0;
        try
        {
            foreach (ILiteCollection<BsonDocument> collection in new[] { kills, consumables, dodgeHits })
            {
                if (removed == limit) break;
                // Materialize only the bounded indexed range before mutating its collection.
                BsonDocument[] candidates = collection.Query().Where(BsonExpression.Create("TimestampUtc < @0", ticks)).Limit(limit - removed).ToArray();
                foreach (BsonDocument document in candidates)
                {
                    if (collection.Delete(document["_id"])) removed++;
                }
            }
            if (!db.Commit()) throw new InvalidOperationException("Signal retention transaction did not commit.");
        }
        catch { DatabaseDiagnostics.Rollback(() => db.Rollback(), _ => { }); throw; }
        return removed;
    }

    internal CombinedReportSnapshot Query(string world, DateTime startUtc, DateTime endUtc, LeaderboardsOptions options,
        System.Collections.Generic.IEnumerable<BsonDocument>? confirmedKills = null)
    {
        ObservationValues.Range(world, startUtc, endUtc);
        if (options == null) throw new ArgumentNullException(nameof(options));
        Dictionary<ReportMetric, IReadOnlyList<ReportRow>> rows = new();
        if (options.ReportedKillCredits) ObservationValues.Put(rows, ReportMetric.ReportedKillCredits,
            ObservationValues.Aggregate(ObservationValues.Merge(ObservationValues.Period(kills, world, startUtc, endUtc, "PlayerKey"), confirmedKills)));
        if (options.ConsumableUses) ObservationValues.Put(rows, ReportMetric.ObservedConsumableUses,
            ObservationValues.Aggregate(ObservationValues.Period(consumables, world, startUtc, endUtc, "PlayerKey")));
        if (options.ReportedDodgeHits) ObservationValues.Put(rows, ReportMetric.ReportedDodgeHitNotifications,
            ObservationValues.Aggregate(ObservationValues.Period(dodgeHits, world, startUtc, endUtc, "PlayerKey")));
        return new CombinedReportSnapshot(world, rows);
    }
}
