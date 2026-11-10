using System;
using System.Collections.Generic;
using System.Linq;

using LiteDB;

namespace DiscordConnector.Records;

internal sealed class PlayerActionStore
{
    private readonly ILiteCollection<BsonDocument> actions;

    internal PlayerActionStore(LiteDatabase db)
    {
        actions = (db ?? throw new ArgumentNullException(nameof(db))).GetCollection("player_actions_v1");
        ObservationValues.Index(actions);
    }

    internal bool InsertAction(PlayerActionRecord record)
    {
        if (record == null) throw new ArgumentNullException(nameof(record));
        BsonDocument document = ObservationValues.Document(ObservationValues.Key(record.WorldId, record.ActionToken, record.Actor.PlayerId, record.Kind.ToString()),
            record.WorldId, record.TimestampUtc, record.Actor);
        document["Kind"] = record.Kind.ToString(); document["ActionToken"] = record.ActionToken;
        return InsertUnique(actions, document);
    }

    internal CombinedReportSnapshot Query(string world, DateTime startUtc, DateTime endUtc, LeaderboardsOptions options)
    {
        ObservationValues.Range(world, startUtc, endUtc);
        if (options == null) throw new ArgumentNullException(nameof(options));
        Dictionary<ReportMetric, IReadOnlyList<ReportRow>> rows = new();
        if (options.DodgeRolls) ObservationValues.Put(rows, ReportMetric.ObservedDodgeRolls, ObservationValues.Aggregate(
            ObservationValues.Period(actions, world, startUtc, endUtc, "PlayerKey").Where(x => x["Kind"].AsString == PlayerActionKind.DodgeRoll.ToString())));
        return new CombinedReportSnapshot(world, rows);
    }

    internal static bool InsertUnique(ILiteCollection<BsonDocument> collection, BsonDocument document)
    {
        // Do not swallow unrelated unique-index failures: those are database errors, not replay.
        if (collection.Exists(BsonExpression.Create("_id = @0", document["_id"]))) return false;
        collection.Insert(document); return true;
    }
}
