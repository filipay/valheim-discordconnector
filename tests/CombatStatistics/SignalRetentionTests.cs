using DiscordConnector.Records;
using LiteDB;

internal static partial class Program
{
    private static readonly string[] SignalDetails =
        ["reported_kill_credits_v1", "observed_consumable_uses_v1", "reported_dodge_hit_notifications_v1"];
    private static readonly LeaderboardsOptions AllSignalOptions = new(reportedKillCredits: true, consumableUses: true, reportedDodgeHits: true);

    private static int SignalDetailCount(LiteDatabase db) => SignalDetails.Sum(name => db.GetCollection(name).Count());
    private static string SignalRows(LiteDatabase db, string name) => string.Join("\n",
        db.GetCollection(name).FindAll().Select(x => x.ToString()).Order(StringComparer.Ordinal));

    private static void SignalRetentionBoundaries(string directory)
    {
        using LiteDatabase db = new(Path.Combine(directory, "records.db"));
        string[] untouched = ActivityStore.CollectionNames.Except(SignalDetails)
            .Concat(["combat_kills", "combat_reports", "deaths", "joins", "leaves", "shouts", "pings", "player_name"]).ToArray();
        foreach (string name in untouched)
            db.GetCollection(name).Insert(new BsonDocument { ["_id"] = "sentinel", ["TimestampUtc"] = 1L, ["unknown"] = "keep" });
        Dictionary<string, string> before = untouched.ToDictionary(x => x, x => SignalRows(db, x));
        ReportedSignalStore store = new(db);
        DateTime cutoff = Start.AddDays(-90);
        foreach (ReportedSignalKind kind in Enum.GetValues<ReportedSignalKind>())
            foreach (string world in new[] { "world", "WORLD" })
                foreach ((string token, DateTime time) in new[] { ("old", cutoff.AddTicks(-1)), ("equal", cutoff), ("new", cutoff.AddTicks(1)) })
                    Check(store.Insert(Signal(token, kind, world, timestamp: time)), "boundary fixture");
        CombinedReportSnapshot historical = store.Query("world", cutoff.AddTicks(-1), cutoff, AllSignalOptions);
        Equal(3, historical.Rows.Count, "historical fixtures remain queryable without reading clock");
        Equal(6, store.PruneExpired(Start), "only strict older-than detail expires");
        Equal(12, SignalDetailCount(db), "cutoff equality and newer ticks survive in both worlds");
        foreach (string world in new[] { "world", "WORLD" })
        {
            CombinedReportSnapshot snapshot = store.Query(world, cutoff, End, AllSignalOptions);
            Equal(3, snapshot.Rows.Count, "all three independent metrics");
            Check(snapshot.Rows.Values.All(rows => rows.Single().Count == 2), "case-distinct worlds do not merge");
        }
        foreach (string name in untouched) Equal(before[name], SignalRows(db, name), $"untouched {name}");
    }

    private static void SignalRetentionBounds(string directory)
    {
        using LiteDatabase db = new(Path.Combine(directory, "records.db"));
        ReportedSignalStore store = new(db);
        DateTime cutoff = Start.AddDays(-90);
        for (int i = 0; i < 257; i++)
            Check(store.Insert(Signal("old-" + i, (ReportedSignalKind)(i % 3), timestamp: cutoff.AddTicks(-1))), "backlog fixture");
        foreach (ReportedSignalKind kind in Enum.GetValues<ReportedSignalKind>())
            Check(store.Insert(Signal("current", kind)), "unexpired fixture");
        foreach (string name in SignalDetails)
        {
            BsonDocument plan = db.GetCollection(name).Query().Where(LiteDB.Query.LT("TimestampUtc", cutoff.Ticks)).Limit(256).GetPlan();
            Equal("TimestampUtc", plan["index"]["name"].AsString, "timestamp index used for candidate range");
            Equal(256, plan["limit"].AsInt32, "limit applied by engine before materialization");
            Check(!plan.ContainsKey("orderBy"), "no unbounded candidate sort");
            Console.WriteLine($"RETENTION PLAN {name}: {plan}");
        }
        Equal(256, store.PruneExpired(Start), "257 eligible rows remove only 256 total, not per collection");
        Equal(4, SignalDetailCount(db), "one eligible plus three current rows remain");
        Equal(1, store.PruneExpired(Start, 1), "custom one-row batch drains remainder");
        Equal(0, store.PruneExpired(Start), "no current detail removed");
        Equal(3, SignalDetailCount(db), "all current kinds survive");
    }

    private static void SignalRetentionRestart(string directory)
    {
        string path = Path.Combine(directory, "records.db");
        DateTime cutoff = Start.AddDays(-90);
        using (LiteDatabase db = new(path))
        {
            ReportedSignalStore store = new(db);
            foreach (ReportedSignalKind kind in Enum.GetValues<ReportedSignalKind>())
                Check(store.Insert(Signal("expired", kind, timestamp: cutoff.AddTicks(-1))), "pre-retention fixture");
            Equal(3, store.PruneExpired(Start), "expired all kinds");
        }
        using (LiteDatabase db = new(path))
        {
            ReportedSignalStore store = new(db);
            Equal(0, SignalDetailCount(db), "expired detail stays deleted after reopen");
            foreach (ReportedSignalKind kind in Enum.GetValues<ReportedSignalKind>())
                Check(store.Insert(Signal("equal", kind, timestamp: cutoff)), "new cutoff-equal detail survives");
            Equal(0, store.PruneExpired(Start), "reopened store keeps cutoff-equal rows");
            Equal(3, SignalDetailCount(db), "all kinds survive at cutoff");
        }
    }

    private static void SignalRetentionLiveDuplicate(string directory)
    {
        using LiteDatabase db = new(Path.Combine(directory, "records.db"));
        ReportedSignalStore store = new(db);
        foreach (ReportedSignalKind kind in Enum.GetValues<ReportedSignalKind>()) Check(store.Insert(Signal("live", kind)), "live fixture");
        Equal(0, store.PruneExpired(Start), "maintenance keeps live rows");
        foreach (ReportedSignalKind kind in Enum.GetValues<ReportedSignalKind>())
            Check(!store.Insert(Signal("live", kind, timestamp: Start.AddDays(-91))), "duplicate identity is rejected");
        Equal(3, SignalDetailCount(db), "live originals intact");
    }

    private static void SignalRetentionClocks(string directory)
    {
        using LiteDatabase db = new(Path.Combine(directory, "records.db"));
        ReportedSignalStore store = new(db);
        DateTime cutoff = Start.AddDays(-90);
        Check(store.Insert(Signal("late", timestamp: cutoff.AddTicks(-1))), "new backdated identity accepted");
        Equal(0, store.PruneExpired(Start.AddYears(-1)), "backward clock uses its own ninety-day cutoff");
        Equal(1, store.PruneExpired(Start), "corrected clock prunes expired detail");
        Check(store.Insert(Signal("current")), "current fixture");
        DateTime forward = new(9000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Equal(1, store.PruneExpired(forward), "huge false-forward clock irreversibly expires current detail");
        Equal(0, store.PruneExpired(Start), "clock correction cannot restore detail");
        Check(store.Insert(Signal("after-correction")), "new identity still accepted after clock correction");
        Equal(0, store.PruneExpired(Start), "no persisted horizon expires new current detail");
        Equal(1, SignalDetailCount(db), "current detail survives clock correction");
    }

    private static void SignalRetentionValidation(string directory)
    {
        using LiteDatabase db = new(Path.Combine(directory, "records.db"));
        ReportedSignalStore store = new(db);
        foreach (DateTime invalid in new[] { default, DateTime.SpecifyKind(Start, DateTimeKind.Local), DateTime.SpecifyKind(Start, DateTimeKind.Unspecified) })
            Throws<ArgumentException>(() => store.PruneExpired(invalid));
        foreach (int limit in new[] { int.MinValue, -1, 0, 257, int.MaxValue })
            Throws<ArgumentOutOfRangeException>(() => store.PruneExpired(Start, limit));
        Equal(0, SignalDetailCount(db), "invalid calls cannot write detail");
        Equal(0, store.PruneExpired(DateTime.SpecifyKind(DateTime.MinValue.AddTicks(1), DateTimeKind.Utc), 1), "early UTC date clamps cutoff without underflow");
    }
}
