using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

using LiteDB;

namespace DiscordConnector.Records;

internal sealed class CombatStore
{
    private readonly ILiteCollection<BsonDocument> kills;
    private readonly ILiteCollection<BsonDocument> reports;
    private readonly BsonMapper mapper = new() { TrimWhitespace = false, EmptyStringToNull = false };

    internal CombatStore(LiteDatabase db)
    {
        if (db == null) throw new ArgumentNullException(nameof(db));

        // BSON dates lose submillisecond precision; local mapping preserves UTC ticks without changing legacy dates.
        mapper.RegisterType<DateTime>(value => value.Ticks, value => new DateTime(value.AsInt64, DateTimeKind.Utc));
        // Resolve the mapping before concurrent inserts can observe a partially initialized entity map.
        mapper.Entity<CombatRecord>();
        kills = db.GetCollection("combat_kills");
        reports = db.GetCollection("combat_reports");
        kills.EnsureIndex(nameof(CombatRecord.WorldId));
        kills.EnsureIndex(nameof(CombatRecord.TimestampUtc));
        kills.EnsureIndex(nameof(CombatRecord.PlayerId));
    }

    internal bool Insert(CombatRecord record)
    {
        if (record == null) throw new ArgumentNullException(nameof(record));
        RequireText(record.WorldId, nameof(record.WorldId));
        RequireText(record.VictimId, nameof(record.VictimId));
        RequireText(record.PlayerId, nameof(record.PlayerId));
        RequireText(record.PlayerName, nameof(record.PlayerName));
        RequireText(record.Creature, nameof(record.Creature));
        RequireText(record.Biome, nameof(record.Biome));
        RequireUtc(record.TimestampUtc, nameof(record.TimestampUtc));

        // Always derive identity here: caller-supplied IDs must not bypass durable victim deduplication.
        record.Id = Hash(record.WorldId.Length.ToString(CultureInfo.InvariantCulture) + ":" + record.WorldId + record.VictimId);
        try
        {
            kills.Insert(mapper.ToDocument(record));
            return true;
        }
        catch (LiteException exception) when (exception.ErrorCode == LiteException.INDEX_DUPLICATE_KEY)
        {
            return false;
        }
    }

    internal List<CombatRecord> Query(string worldId, DateTime startUtc, DateTime endUtc)
    {
        RequireText(worldId, nameof(worldId));
        RequireUtc(startUtc, nameof(startUtc));
        RequireUtc(endUtc, nameof(endUtc));
        if (endUtc < startUtc) throw new ArgumentException("The end must not precede the start.", nameof(endUtc));

        return kills.Find(LiteDB.Query.And(
                LiteDB.Query.EQ(nameof(CombatRecord.WorldId), worldId),
                LiteDB.Query.GTE(nameof(CombatRecord.TimestampUtc), startUtc.Ticks),
                LiteDB.Query.LT(nameof(CombatRecord.TimestampUtc), endUtc.Ticks)))
            .Select(document => mapper.ToObject<CombatRecord>(document))
            // Legacy databases can have a case-insensitive collation; world identity is still exact.
            .Where(record => string.Equals(record.WorldId, worldId, StringComparison.Ordinal))
            .ToList();
    }

    internal bool TryClaimReport(string reportId, DateTime nowUtc)
    {
        RequireText(reportId, nameof(reportId));
        RequireUtc(nowUtc, nameof(nowUtc));
        try
        {
            reports.Insert(new BsonDocument
            {
                ["_id"] = Hash(reportId),
                ["ReportId"] = reportId,
                ["TimestampUtc"] = nowUtc.Ticks
            });
            return true;
        }
        catch (LiteException exception) when (exception.ErrorCode == LiteException.INDEX_DUPLICATE_KEY)
        {
            return false;
        }
    }

    private static void RequireText(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.IndexOf('\0') >= 0)
            throw new ArgumentException("A nonblank value without NUL characters is required.", name);
    }

    private static void RequireUtc(DateTime value, string name)
    {
        if (value.Kind != DateTimeKind.Utc || value == DateTime.MinValue)
            throw new ArgumentException("A nondefault UTC timestamp is required.", name);
    }

    private static string Hash(string value)
    {
        using SHA256 hash = SHA256.Create();
        return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "");
    }
}
