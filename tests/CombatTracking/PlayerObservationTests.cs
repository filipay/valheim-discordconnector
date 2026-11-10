using DiscordConnector.Records;

internal static class PlayerObservationTests
{
    private static readonly DateTime Start = new(2026, 6, 30, 22, 59, 58, DateTimeKind.Utc);
    private static ActivityActor Actor(string id) => new(id, id, "connection-" + id, Start);
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    internal static void Run()
    {
        var batches = new List<ActivityBatch>();
        var collector = new ActivityCollector(b => { batches.Add(b); return true; }, _ => { }, timeZoneId: "Europe/Dublin")
        { ActorIsCurrent = a => a.PlayerId != "disconnected", PositionIsCurrent = (_, character) => character != "old-character" };
        var options = new LeaderboardsOptions(bossKills: true, biomes: true);
        collector.Tick(Start, 100, "world", options);
        void Biome(string id, string biome, double t, bool fresh = true, string world = "world") =>
            collector.SampleBiome(world, Actor(id), biome, Start.AddSeconds(t), t + 100, fresh);
        Biome("a", "Meadows", 0); Biome("a", "Meadows", 1); Biome("a", "BlackForest", 1);
        Biome("b", "Meadows", 1); Biome("a", "Swamp", 1, false); Biome("disconnected", "Swamp", 1); Biome("a", "Swamp", 1, world: "other");
        collector.FlushThrough(Start.AddSeconds(1));
        Check(batches.SelectMany(b => b.Activities).Count() == 3, "biome sampling must dedup per account/biome/day and reject stale, wrong-world and disconnected samples");
        Console.WriteLine("PASS player observations: fresh biome/account/day dedup and fences");
        Biome("a", "Meadows", 2);
        collector.FlushThrough(Start.AddSeconds(2));
        var visits = batches.SelectMany(b => b.Activities).Where(a => a.Actor.PlayerId == "a" && a.Prefab == "Meadows").ToArray();
        Check(visits.Select(a => a.Token).SequenceEqual(new[] { "2026-06-30", "2026-07-01" }), "report-timezone midnight, not UTC midnight, must open the next daily visit");
        Console.WriteLine("PASS player observations: report timezone midnight opens next period");

        void Position(string id, double x, double y = 0, double t = 0, bool fresh = true, string character = "character") =>
            collector.SamplePlayerPosition("world", character, Actor(id), x, y, 0, Start.AddSeconds(t), t + 100, fresh);
        Position("sender", 0); Position("near", 50); Position("far", 50.01); Position("vertical", 0, 51);
        Position("stale", 1, fresh: false); Position("disconnected", 1); Position("respawn", 1, character: "old-character");
        var recipients = collector.BossRecipients(Actor("sender"), Start, 100);
        Check(recipients.Select(a => a.PlayerId).Order().SequenceEqual(new[] { "near", "sender" }), "sender and fresh, current-character peers within inclusive 50m in 3D only");
        Check(collector.BossRecipients(Actor("sender"), Start.AddSeconds(3), 103).Length == 0, "stale sender cannot anchor any proximity credits");
        Check(collector.BossRecipients(Actor("sender"), Start.AddSeconds(3), 100).Length == 0 && collector.BossRecipients(Actor("sender"), Start.AddSeconds(-1), 100).Length == 0,
            "fresh monotonic time cannot mask stale or future UTC positions");
        Check(collector.BossRecipients(new("sender", "same", "reconnected", Start), Start, 100).Length == 0, "reconnected sender cannot reuse prior position");
        Console.WriteLine("PASS player observations: boss proximity, freshness, identity and 50m boundary");

        string[] keys = { "defeated_eikthyr", "defeated_gdking", "defeated_bonemass", "defeated_dragon", "defeated_goblinking", "defeated_queen", "defeated_fader" };
        Check(keys.All(ActivityCollector.IsBossKey) && new[] { "", "defeated_serpent", "defeated_fake", "defeated_eikthyr ", "Defeated_eikthyr", "KilledTroll", "NoMap" }.All(k => !ActivityCollector.IsBossKey(k)), "only seven exact boss defeat keys qualify");
        Console.WriteLine("PASS player observations: exact boss key allowlist excludes nonboss keys");
        void Boss(string key, double t, string world = "world") => collector.ObserveBoss(world, key, recipients, Start.AddSeconds(t), 100 + t);
        Boss(keys[0], 0); Boss(keys[0], 59.999); Boss(keys[0], -1); Boss(keys[1], 1); Boss("defeated_serpent", 1); Boss(keys[0], 1, "other"); Boss(keys[0], 60);
        collector.FlushThrough(Start.AddSeconds(60));
        var credits = batches.SelectMany(b => b.Activities).Where(a => a.Kind == ActivityKind.BossCredit).ToArray();
        Check(credits.Length == 6 && credits.GroupBy(a => a.Token).All(g => g.Count() == 2), "per-world/key sliding 60s dedup, independent keys, shared immutable cohort tokens");
        Console.WriteLine("PASS player observations: boss per-key dedup window and cohort tokens");
        collector.Tick(Start.AddSeconds(61), 161, "world", new());
        Biome("a", "Swamp", 61); Boss(keys[2], 61);
        collector.FlushThrough(Start.AddSeconds(61));
        Check(batches.SelectMany(b => b.Activities).Count(a => a.Kind == ActivityKind.BossCredit) == 6, "own disable stops boss capture");
        collector.Tick(Start.AddSeconds(62), 162, "other", options);
        collector.SampleBiome("other", Actor("a"), "Meadows", Start.AddSeconds(62), 162, true);
        Boss(keys[0], 62, "other"); collector.FlushThrough(Start.AddSeconds(62));
        Check(batches.SelectMany(b => b.Activities).Count(a => a.WorldId == "other") == 3, "world transition clears biome/day and boss windows");
        Console.WriteLine("PASS player observations: own-disable and world lifecycle fencing");
    }
}
