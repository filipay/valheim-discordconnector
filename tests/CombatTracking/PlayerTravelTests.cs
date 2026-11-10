using DiscordConnector.Records;

internal static class PlayerTravelTests
{
    internal static void Run()
    {
        DateTime start = new(2026, 11, 6, 23, 59, 58, DateTimeKind.Utc);
        var actor = new ActivityActor("a", "Alice", "connection", start);
        var batches = new List<ActivityBatch>();
        var loss = new ObservationLoss(_ => { });
        var c = new ActivityCollector(b => { batches.Add(b); return true; }, _ => { }, loss) { ActorIsCurrent = _ => true };
        var flags = new LeaderboardsOptions(onFoot: true, biomeTime: true, onlineTime: true, portalTrips: true);
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
        ActivityRecord[] Records(ActivityKind kind) { c.FlushThrough(start); return batches.SelectMany(b => b.Activities).Where(a => a.Kind == kind).ToArray(); }
        void Sample(double t, double x, string biome = "Meadows", bool fresh = true, bool ship = false, string character = "character", ActivityActor? who = null) =>
            c.SampleTravel("world", character, who ?? actor, x, 0, biome, start.AddSeconds(t), 100 + t, fresh, ship);
        c.Tick(start, 100, "world", flags);
        Sample(0, 0); Sample(2, 12); Sample(4, 24, ship: true); Sample(6, 36); Sample(8, 48);
        Check(Records(ActivityKind.OnFoot).Sum(r => r.Meters) == 24, "horizontal distance excludes both endpoints aboard a ship");
        Console.WriteLine("PASS travel: on-foot distance excludes ship endpoints");
        Sample(10, 1000); Sample(12, 0); Sample(14, 1000); Sample(16, 1000); Sample(18, 1000); Sample(20, 0);
        Check(Records(ActivityKind.PortalTrip).Length == 2 && Records(ActivityKind.OnFoot).Sum(r => r.Meters) == 24,
            "large jumps count once per ten seconds and never become distance");
        Console.WriteLine("PASS travel: inferred teleport jump dedup and foot exclusion");
        Sample(22, 50); Sample(26, 60); Sample(28, 70, fresh: false); Sample(30, 80); Sample(32, 90, character: "respawn");
        Sample(34, 100, who: new("a", "Alice", "new-connection", start));
        Sample(36, double.NaN); Sample(38, 110);
        Check(Records(ActivityKind.OnFoot).Sum(r => r.Meters) == 24, "speed spike, gaps, stale, character, connection and finite fences");
        Console.WriteLine("PASS travel: gaps speed spikes stale respawn reconnect and nonfinite fences");
        Sample(40, 110, "Swamp"); Sample(42, 110, "Swamp"); Sample(44, 110, "None"); Sample(46, 110, "None");
        Check(Records(ActivityKind.BiomeTime).Where(r => r.Prefab == "Swamp").Sum(r => r.Seconds) == 2 &&
            Records(ActivityKind.BiomeTime).All(r => r.Prefab != "None"), "stationary same-biome time only; transition and unknown biome excluded");
        Console.WriteLine("PASS travel: per-biome stationary intervals and transition fencing");
        void Online(double t, params ActivityActor[] actors) => c.SampleOnline("world", actors, start.AddSeconds(t), 100 + t);
        Online(0, actor); Online(2, actor); Online(4); Online(6, actor); Online(8, actor);
        c.Reset();
        Check(Records(ActivityKind.OnlineTime).Sum(r => r.Seconds) == 6, "disconnect closes and reset checkpoints only observed online time");
        c.Tick(start.AddSeconds(100), 200, "world", flags); Online(100, actor); Online(102, actor);
        Check(Records(ActivityKind.OnlineTime).Sum(r => r.Seconds) == 8, "restart never bridges offline time");
        Online(112, actor); Online(114, actor);
        Check(Records(ActivityKind.OnlineTime).Sum(r => r.Seconds) == 10, "long runtime gaps cannot accrue online time");
        Console.WriteLine("PASS travel: online disconnect shutdown restart and runtime gap checkpoints");
        c.Tick(start.AddSeconds(116), 216, "world", new()); Sample(116, 120); Online(116, actor);
        Check(Records(ActivityKind.OnlineTime).Sum(r => r.Seconds) == 10, "own-disable flushes and stops online capture");
        c.Tick(start.AddSeconds(118), 218, "world", flags); Sample(118, 130);
        Check(Records(ActivityKind.OnFoot).Sum(r => r.Meters) == 24, "reenable cannot bridge a disabled baseline");
        Console.WriteLine("PASS travel: independent flags and reenable baseline fencing");
        c.Reset(); c.Tick(start, 100, "world", flags);
        for (int i = 0; i < 300; i++)
        {
            var next = new ActivityActor("p" + i, "name", "c" + i, start);
            c.SampleTravel("world", "char", next, 0, 0, "Meadows", start, 100, true, false);
        }
        var travel = (System.Collections.IDictionary)typeof(ActivityCollector).GetField("_travel", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(c)!;
        Check(travel.Count == 256, "bounded player sample capacity");
        c.Tick(start.AddSeconds(3), 103, "world", flags);
        Check(travel.Count == 0, "expired or disconnected players do not retain capacity forever");
        Console.WriteLine("PASS travel: bounded memory and stale sample reclamation");
        c.Reset(); c.Tick(start, 100, "world", flags);
        var peers = Enumerable.Range(0, 256).Select(i => new ActivityActor("p" + i, "name", "c" + i, start)).ToArray();
        for (int i = 0; i <= 20; i++) Online(i, peers);
        c.Reset();
        Check(Records(ActivityKind.OnlineTime).Where(r => r.Actor.PlayerId == "p0").Sum(r => r.Seconds) == 20 &&
            Records(ActivityKind.OnlineTime).All(r => r.Seconds <= 10), "many peers retain bounded checkpoint spans");
        Console.WriteLine("PASS travel: 256 online sessions checkpoint in bounded spans");

        var bounded = new List<ActivityBatch>();
        var all = new ActivityCollector(b => { bounded.Add(b); return true; }, _ => { }) { ActorIsCurrent = _ => true };
        all.Tick(start, 100, "world", new(dodgeRolls: true));
        foreach (ReportMetric metric in Enum.GetValues<ReportMetric>()) all.Coverage(metric, CoverageState.Partial, start);
        for (int i = 0; i < 120; i++) all.ObserveAction(new("world", actor, PlayerActionKind.DodgeRoll, "roll" + i, start));
        all.FlushThrough(start);
        Check(bounded.Count >= 2 && bounded.All(b => b.PhysicalDocumentCount <= 128) && bounded.Sum(b => b.Actions.Count) == 120,
            "all enabled coverage documents must fit beside buffered gameplay");
        Console.WriteLine("PASS travel: all coverage sections preserve physical batch bounds");
    }
}
