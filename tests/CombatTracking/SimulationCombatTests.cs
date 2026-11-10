using DiscordConnector.Records;

internal static class SimulationCombatTests
{
    internal static void Run()
    {
        DateTime utc = new(2026, 11, 6, 0, 0, 0, DateTimeKind.Utc);
        var batches = new List<ActivityBatch>();
        var c = new ActivityCollector(b => { batches.Add(b); return true; }, _ => { });
        var flags = new LeaderboardsOptions(reportedKillCredits: true, damageDealt: true);
        c.Tick(utc, 0, "world", flags);
        var tracker = new SimulationCombatCollector(c);
        ActivityActor Actor(string id) => new(id, id, "connection", utc);
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
        tracker.Damage("world", "victim", Actor("a"), 10, -50, utc, 0, flags);
        tracker.Damage("world", "victim", Actor("a"), 20, 10, utc, 1, flags);
        tracker.Damage("world", "victim", Actor("b"), 20, 10, utc, 121, flags);
        var actors = tracker.Participants("victim", 121)!;
        Check(actors.Select(a => a.PlayerId).Order().SequenceEqual(new[] { "a", "b" }), "inclusive contributor TTL and dedup");
        Check(tracker.Participants("victim", 122)!.Single().PlayerId == "b", "individual TTL, not victim-wide refresh");
        tracker.Death("world", "victim", actors, utc, 121); tracker.Death("world", "victim", actors, utc, 122);
        c.FlushThrough(utc);
        Check(batches.SelectMany(b => b.Activities).Where(a => a.Kind == ActivityKind.CreatureKill).Count() == 2, "one credit per participant");
        Check(batches.SelectMany(b => b.Activities).Where(a => a.Kind == ActivityKind.CreatureDamage).Sum(a => a.HpLoss) == 30, "overkill capped to available HP");
        Console.WriteLine("PASS simulation: damage cap participant dedup individual TTL and confirmed death");

        tracker.Reset();
        for (int i = 0; i < 1100; i++) tracker.Damage("world", "v" + i, Actor("a"), 10, 9, utc, 0, flags);
        for (int i = 0; i < 40; i++) tracker.Damage("world", "v0", Actor("p" + i), 10, 9, utc, 0, flags);
        var victims = (System.Collections.IDictionary)typeof(SimulationCombatCollector).GetField("_victims", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(tracker)!;
        Check(victims.Count == 1024 && tracker.Participants("v0", 0)!.Length == 32, "victim and contributor caps");
        tracker.Damage("world", "v0", Actor("new"), 10, 9, utc, 121, flags);
        Check(tracker.Participants("v0", 121)!.Single().PlayerId == "new", "expired contributors release slots on a still-active victim");
        tracker.Tick(true, 242); Check(victims.Count == 0, "expired victims release capacity");
        tracker.Damage("world", "v", Actor("a"), 10, 9, utc, 122, flags); tracker.Invalidate("v");
        Check(tracker.Participants("v", 122) == null, "ownership invalidation");
        tracker.Damage("world", "v", Actor("a"), 10, 9, utc, 122, flags); tracker.Tick(false, 123);
        Check(victims.Count == 0, "disable and world reset clear ledger");
        Console.WriteLine("PASS simulation: bounded victims contributors expiry invalidation and disable");
    }
}
