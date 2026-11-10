using DiscordConnector.Records;
using LiteDB;

internal static partial class Program
{
    private static void PlayerTravelStorage(string directory)
    {
        string path = Path.Combine(directory, "records.db");
        var flags = new LeaderboardsOptions(onFoot: true, biomeTime: true, onlineTime: true, portalTrips: true);
        ActivityRecord Span(ActivityKind kind, string token, string biome = "", string account = "a", string world = "world") =>
            new(kind, world, Actor(account), "subject", token, End, biome, startUtc: Start, seconds: 2, meters: kind == ActivityKind.OnFoot ? 12 : 0);
        var foot = Span(ActivityKind.OnFoot, "foot");
        var online = Span(ActivityKind.OnlineTime, "online");
        var portal = new ActivityRecord(ActivityKind.PortalTrip, "world", Actor("a"), "character", "jump", Start);
        using (var db = new LiteDatabase(path))
        using (var store = new ActivityStore(db, new CombatStore(db), new PlayerActionStore(db), new CookingStore(db), _ => { }))
        {
            Check(store.Enqueue(new ActivityBatch(activities: [foot, foot, online, online, portal, portal,
                Span(ActivityKind.BiomeTime, "meadows1", "Meadows"), Span(ActivityKind.BiomeTime, "meadows2", "Meadows"),
                Span(ActivityKind.BiomeTime, "swamp", "Swamp"), Span(ActivityKind.BiomeTime, "b", "Swamp", "b"),
                Span(ActivityKind.OnFoot, "other", world: "other")])), "travel records admitted");
            var report = store.QueryAsync("world", Start, Start.AddSeconds(1), flags).GetAwaiter().GetResult();
            Equal(6d, report.Rows[ReportMetric.OnFootDistance].Single().Meters, "distance clipped to half interval");
            Equal(1d, report.Rows[ReportMetric.OnlineTime].Single().Seconds, "online interval clipped to half-open period");
            Equal(1L, report.Rows[ReportMetric.PortalTrips].Single().Count, "jump replay dedup");
            var home = report.Rows[ReportMetric.BiomeTime].Single(r => r.Id == "a");
            Equal(2d, home.Seconds, "sum each biome independently, not all biomes combined");
            Equal("Meadows", home.Biome, "largest accumulated single biome label");
            Equal(2, report.Rows[ReportMetric.BiomeTime].Count, "players remain separate");
            Equal(0, store.QueryAsync("world", Start, End, new()).GetAwaiter().GetResult().Rows.Count, "all four independent default-off flags");
            Equal(1, store.QueryAsync("other", Start, End, flags).GetAwaiter().GetResult().Rows.Count, "world isolation");
            Equal(0, store.QueryAsync("world", End, End.AddSeconds(1), flags).GetAwaiter().GetResult().Rows.Count, "end boundary excludes finished spans");
        }
        using (var db = new LiteDatabase(path))
        using (var store = new ActivityStore(db, new CombatStore(db), new PlayerActionStore(db), new CookingStore(db), _ => { }))
        {
            Check(store.Enqueue(new ActivityBatch(activities: [foot, online, portal])), "restart replays admitted");
            var report = store.QueryAsync("world", Start.AddSeconds(1), End, flags).GetAwaiter().GetResult();
            Equal(6d, report.Rows[ReportMetric.OnFootDistance].Single().Meters, "reopen keeps durable distance dedup and adjacent clipping");
            Equal(1d, report.Rows[ReportMetric.OnlineTime].Single().Seconds, "reopen cannot duplicate online spans");
            Check(!report.Rows.ContainsKey(ReportMetric.PortalTrips), "jump belongs only to its timestamp period");
        }
    }
}
