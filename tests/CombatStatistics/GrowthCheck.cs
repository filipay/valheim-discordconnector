using System.Diagnostics;
using DiscordConnector.Records;
using LiteDB;

internal static partial class Program
{
    private static int GrowthCheck()
    {
        string directory = Path.Combine(Path.GetTempPath(), "discord-signal-growth-" + Guid.NewGuid().ToString("N"));
        Check(!Directory.Exists(directory), "growth directory must be new");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "owned-growth.db");
        try
        {
            Check(!File.Exists(path), "growth database must be new");
            DateTime day = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
            const string collection = "reported_dodge_hit_notifications_v1";
            using LiteDatabase db = new(path);
            ReportedSignalStore store = new(db);
            var options = new LeaderboardsOptions(reportedDodgeHits: true);
            ReportedSignalRecord Row(int i) => new(ReportedSignalKind.DodgeHitNotification, "growth-world",
                new ActivityActor("account-" + (i / 2 % 10), "Player " + (i / 2 % 10), "fixture-session", day),
                "notification-" + i, day.AddDays(i % 2).AddSeconds(i / 2));
            string[] indexes = db.Execute("SELECT name FROM $indexes WHERE collection = '" + collection + "'")
                .ToArray().Select(row => row["name"].AsString).ToArray();
            foreach (string field in new[] { "WorldId", "TimestampUtc", "PlayerId", "PlayerKey" })
                Check(indexes.Contains(field), "growth query index: " + field);
            int previous = 0;
            foreach (int count in new[] { 1000, 10000 })
            {
                Stopwatch insert = Stopwatch.StartNew();
                for (int i = previous; i < count; i++) Check(store.Insert(Row(i)), "unique signal insert");
                insert.Stop();
                Stopwatch duplicate = Stopwatch.StartNew();
                for (int i = 0; i < count; i++) Check(!store.Insert(Row(i)), "durable duplicate refused");
                duplicate.Stop();
                Equal(count, db.GetCollection(collection).Count(), "exact unique row count after duplicate inserts");
                Stopwatch query = Stopwatch.StartNew();
                var first = store.Query("growth-world", day, day.AddDays(1), options).Rows[ReportMetric.ReportedDodgeHitNotifications];
                var second = store.Query("growth-world", day.AddDays(1), day.AddDays(2), options).Rows[ReportMetric.ReportedDodgeHitNotifications];
                var both = store.Query("growth-world", day, day.AddDays(2), options).Rows[ReportMetric.ReportedDodgeHitNotifications];
                query.Stop();
                Equal(10, both.Count, "ten independent accounts");
                foreach (var rows in new[] { first, second })
                {
                    Equal(10, rows.Count, "ten accounts in each half-open day");
                    Equal((long)count / 2, rows.Sum(row => row.Count), "exact day count");
                    Check(rows.All(row => row.Count == count / 20), "exact per-account day count");
                }
                Equal((long)count, both.Sum(row => row.Count), "adjacent day windows cover all rows exactly once");
                Check(both.All(row => row.Count == count / 10), "exact per-account total");
                Equal(0, store.Query("growth-world", day.AddDays(-1), day, options).Rows.Count, "exclusive start boundary of previous day");
                Equal(0, store.Query("growth-world", day.AddDays(2), day.AddDays(3), options).Rows.Count, "outside day window");
                Equal(0, store.Query("GROWTH-WORLD", day, day.AddDays(2), options).Rows.Count, "ordinal world isolation");
                Equal(0, store.Query("growth-world", day, day.AddDays(2), new()).Rows.Count, "default-off history");
                db.Checkpoint();
                Console.WriteLine(FormattableString.Invariant($"GROWTH rows={count} accounts=10 inserted={count - previous} duplicates={count} day1={first.Sum(row => row.Count)} day2={second.Sum(row => row.Count)} db_bytes={new FileInfo(path).Length} insert_ms={insert.Elapsed.TotalMilliseconds:F3} duplicate_ms={duplicate.Elapsed.TotalMilliseconds:F3} query_3_windows_ms={query.Elapsed.TotalMilliseconds:F3}"));
                previous = count;
            }
            Console.WriteLine("PASS growth-check: exact counts/dedup/indexes/half-open windows; disposable local evidence, not a universal speed promise");
            return 0;
        }
        catch (Exception exception)
        {
            Console.WriteLine($"FAIL growth-check: {exception.GetType().Name}: {exception.Message}");
            return 1;
        }
        finally
        {
            // Only this invocation's newly created fixture directory is owned here.
            Directory.Delete(directory, true);
            Console.WriteLine("GROWTH owned temporary fixture removed=" + !Directory.Exists(directory));
        }
    }
}
