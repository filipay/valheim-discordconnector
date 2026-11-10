using DiscordConnector.Records;
using LiteDB;

internal static partial class Program
{
    private static void BiomeVisitStorage(string directory)
    {
        string path = Path.Combine(directory, "records.db");
        DateTime begin = new(2026, 10, 19, 23, 0, 0, DateTimeKind.Utc);
        ActivityRecord Visit(string account, string biome, int day, string world = "world") =>
            new(ActivityKind.BiomeVisited, world, Actor(account), biome, "2026-10-" + (20 + day), begin.AddDays(day).AddMinutes(1), biome);
        using (var db = new LiteDatabase(path))
        using (var worker = new ActivityStore(db, new CombatStore(db), new PlayerActionStore(db), new CookingStore(db), _ => { }))
        {
            var visits = new[] { Visit("a", "Meadows", 0), Visit("a", "Meadows", 0), Visit("a", "Meadows", 1),
                Visit("a", "Swamp", 0), Visit("b", "Meadows", 0), Visit("a", "Plains", 0, "other"), Visit("a", "BlackForest", 7) };
            Check(worker.Enqueue(new ActivityBatch(activities: visits)), "visits accepted");
            var day = worker.QueryAsync("world", begin, begin.AddDays(1), new(biomes: true)).GetAwaiter().GetResult();
            Equal(2L, day.Rows[ReportMetric.Biomes].Single(r => r.Id == "a").Count, "daily distinct biomes, not samples/deaths");
            Equal(1L, day.Rows[ReportMetric.Biomes].Single(r => r.Id == "b").Count, "same biome is separate for each actor");
            var week = worker.QueryAsync("world", begin, begin.AddDays(7), new(biomes: true)).GetAwaiter().GetResult();
            Equal(2L, week.Rows[ReportMetric.Biomes].Single(r => r.Id == "a").Count, "weekly does not sum repeated daily visits");
            Equal(0, worker.QueryAsync("world", begin, begin.AddDays(7), new()).GetAwaiter().GetResult().Rows.Count, "own flag inclusion");
            Equal(1L, worker.QueryAsync("other", begin, begin.AddDays(7), new(biomes: true)).GetAwaiter().GetResult().Rows[ReportMetric.Biomes].Single().Count, "world isolation");
            Equal(1L, worker.QueryAsync("world", begin.AddDays(7), begin.AddDays(8), new(biomes: true)).GetAwaiter().GetResult().Rows[ReportMetric.Biomes].Single().Count, "adjacent period retains boundary visit");
        }
        using (var db = new LiteDatabase(path))
        using (var worker = new ActivityStore(db, new CombatStore(db), new PlayerActionStore(db), new CookingStore(db), _ => { }))
        {
            Check(worker.Enqueue(new ActivityBatch(activities: [Visit("a", "Meadows", 0)])), "restart replay admitted");
            Equal(3L, worker.QueryAsync("world", begin, begin.AddDays(7), new(biomes: true)).GetAwaiter().GetResult().Rows[ReportMetric.Biomes].Sum(r => r.Count), "restart keeps durable player/biome/day dedup and report distinctness");
            Equal(6, db.GetCollection("activity_observations").Count(), "duplicates never create additional daily visit documents");
        }
    }

    private static void BossCreditStorage(string directory)
    {
        string path = Path.Combine(directory, "records.db");
        ActivityRecord Credit(string account, string token, double seconds, string key = "defeated_eikthyr", string world = "world") =>
            new(ActivityKind.BossCredit, world, Actor(account), key, token, Start.AddSeconds(seconds), key);
        using (var db = new LiteDatabase(path))
        using (var worker = new ActivityStore(db, new CombatStore(db), new PlayerActionStore(db), new CookingStore(db), _ => { }))
        {
            Check(worker.Enqueue(new ActivityBatch(activities: [Credit("a", "event", 0), Credit("b", "event", 0), Credit("a", "event", 0),
                Credit("a", "repeat", 59.999), Credit("c", "event", 0), Credit("a", "next", 60),
                Credit("a", "elder", 1, "defeated_gdking"), Credit("a", "other", 0, world: "other")])), "cohorts admitted");
            var report = worker.QueryAsync("world", Start, Start.AddSeconds(120), new(bossKills: true)).GetAwaiter().GetResult();
            Equal(5L, report.Rows[ReportMetric.ParticipantBossCredits].Sum(r => r.Count), "shared token admits every cohort actor once; different tokens within 60s suppressed");
            Equal(3L, report.Rows[ReportMetric.ParticipantBossCredits].Single(r => r.Id == "a").Count, "distinct key and next-window kill both count");
            Equal(0, worker.QueryAsync("world", Start, Start.AddSeconds(120), new()).GetAwaiter().GetResult().Rows.Count, "boss own flag inclusion");
        }
        using (var db = new LiteDatabase(path))
        using (var worker = new ActivityStore(db, new CombatStore(db), new PlayerActionStore(db), new CookingStore(db), _ => { }))
        {
            Check(worker.Enqueue(new ActivityBatch(activities: [Credit("a", "restarted-repeat", 90), Credit("a", "after-window", 120)])), "restart signals admitted");
            Equal(5L, worker.QueryAsync("world", Start, Start.AddSeconds(120), new(bossKills: true)).GetAwaiter().GetResult().Rows[ReportMetric.ParticipantBossCredits].Sum(r => r.Count), "restart cannot bypass recent key window");
            Equal(1L, worker.QueryAsync("world", Start.AddSeconds(120), Start.AddSeconds(121), new(bossKills: true)).GetAwaiter().GetResult().Rows[ReportMetric.ParticipantBossCredits].Single().Count, "window equality and half-open report boundary");
            Check(!Enum.IsDefined(typeof(ReportMetric), 5), "retired direct-damage numeric identity is not reused");
        }
    }
}
