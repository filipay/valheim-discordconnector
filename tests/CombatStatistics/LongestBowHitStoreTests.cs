using DiscordConnector.Records;
using LiteDB;

internal static partial class Program
{
    private static void LongestBowHitStorage(string directory)
    {
        string path = Path.Combine(directory, "records.db");
        DateTime monday = new(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc);
        ActivityRecord Hit(string token, double meters, int day = 0, string actor = "a", string world = "world") =>
            new(ActivityKind.BowHit, world, Actor(actor), "victim", token, monday.AddDays(day), meters: meters);
        using (var db = new LiteDatabase(path))
        using (var store = new ActivityStore(db, new CombatStore(db), new PlayerActionStore(db), new CookingStore(db), _ => { }))
        {
            Check(store.Enqueue(new ActivityBatch(activities: [Hit("first", 20), Hit("best", 87), Hit("smaller", 50), Hit("best", 87),
                Hit("tomorrow", 120, 1), Hit("next-week", 200, 7), Hit("other-player", 100, actor: "b"), Hit("other-world", 250, world: "other")])), "bow hits admitted");
            var day = store.QueryAsync("world", monday, monday.AddDays(1), new(longestBowHit: true)).GetAwaiter().GetResult().Rows[ReportMetric.LongestBowHit];
            Equal(87d, day.Single(r => r.Id == "a").Meters, "daily maximum, not sum or last hit");
            Equal("b", day[0].Id, "rank by maximum distance");
            var week = store.QueryAsync("world", monday, monday.AddDays(7), new(longestBowHit: true)).GetAwaiter().GetResult().Rows[ReportMetric.LongestBowHit];
            Equal(120d, week.Single(r => r.Id == "a").Meters, "weekly maximum across days with exclusive end");
            Equal(0, store.QueryAsync("world", monday, monday.AddDays(7), new()).GetAwaiter().GetResult().Rows.Count, "independent flag off");
            Equal(250d, store.QueryAsync("other", monday, monday.AddDays(7), new(longestBowHit: true)).GetAwaiter().GetResult().Rows[ReportMetric.LongestBowHit].Single().Meters, "world isolation");
        }
        using (var db = new LiteDatabase(path))
        using (var store = new ActivityStore(db, new CombatStore(db), new PlayerActionStore(db), new CookingStore(db), _ => { }))
        {
            Check(store.Enqueue(new ActivityBatch(activities: [Hit("best", 87)])), "restart replay admitted");
            Equal(120d, store.QueryAsync("world", monday, monday.AddDays(7), new(longestBowHit: true)).GetAwaiter().GetResult().Rows[ReportMetric.LongestBowHit].Single(r => r.Id == "a").Meters, "restart retains maximum");
            Equal(7, db.GetCollection("activity_observations").Count(), "durable replay dedup");
        }
        foreach (double distance in new[] { 0, -1, 250.001, double.NaN, double.PositiveInfinity })
        {
            bool rejected = false;
            try { _ = Hit("invalid", distance); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "record boundary rejects invalid or over-cap distance");
        }
    }
}
