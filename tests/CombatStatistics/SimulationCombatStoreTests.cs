using DiscordConnector.Records;
using LiteDB;

internal static partial class Program
{
    private static void SimulationCombatStorage(string directory)
    {
        string path = Path.Combine(directory, "records.db");
        ActivityRecord Kill(string actor, string victim, string world = "world") => new(ActivityKind.CreatureKill, world, Actor(actor), victim, "token", Start);
        ActivityRecord Damage(string token, double hp) => new(ActivityKind.CreatureDamage, "world", Actor("a"), "victim", token, Start, hpLoss: hp);
        using (var db = new LiteDatabase(path))
        using (var store = new ActivityStore(db, new CombatStore(db), new PlayerActionStore(db), new CookingStore(db), _ => { }))
        {
            Check(store.Enqueue(new ActivityBatch(activities: [Kill("a", "v"), Kill("b", "v"), Kill("a", "v"), Kill("a", "v", "other"), Damage("d1", 8.25), Damage("d1", 8.25), Damage("d2", 10)])), "simulation admitted");
            Check(store.Enqueue(new ActivityBatch(signals: [new(ReportedSignalKind.KillCredit, "world", Actor("a"), "fallback", Start)], signalPrincipal: new("server", "local", true))), "fallback admitted");
            var report = store.QueryAsync("world", Start, End, new(reportedKillCredits: true, damageDealt: true)).GetAwaiter().GetResult();
            Equal(2L, report.Rows[ReportMetric.ReportedKillCredits].Single(a => a.Id == "a").Count, "confirmed participant plus independent fallback");
            Equal(1L, report.Rows[ReportMetric.ReportedKillCredits].Single(a => a.Id == "b").Count, "same victim credits another participant once");
            Equal(18.25, report.Rows[ReportMetric.DamageDealt].Single().HpLoss, "exact additive HP with durable token dedup");
            Equal(0, store.QueryAsync("world", Start, End, new()).GetAwaiter().GetResult().Rows.Count, "own flags hide both sources");
        }
        using (var db = new LiteDatabase(path))
        using (var store = new ActivityStore(db, new CombatStore(db), new PlayerActionStore(db), new CookingStore(db), _ => { }))
        {
            Check(store.Enqueue(new ActivityBatch(activities: [Kill("a", "v"), Damage("d1", 8.25)])), "restart replay admitted");
            var report = store.QueryAsync("world", Start, End, new(reportedKillCredits: true, damageDealt: true)).GetAwaiter().GetResult();
            Equal(3L, report.Rows[ReportMetric.ReportedKillCredits].Sum(a => a.Count), "world/victim/account identity survives restart");
            Equal(18.25, report.Rows[ReportMetric.DamageDealt].Single().HpLoss, "damage replay cannot inflate total");
        }
    }

    private static void SimulationCombinedRanking(string directory)
    {
        using var db = new LiteDatabase(Path.Combine(directory, "records.db"));
        using var store = new ActivityStore(db, new CombatStore(db), new PlayerActionStore(db), new CookingStore(db), _ => { });
        var signals = new List<ReportedSignalRecord>(); var confirmed = new List<ActivityRecord>();
        for (int i = 0; i < 18; i++)
            for (int j = 0; j < 3; j++)
            {
                signals.Add(new(ReportedSignalKind.KillCredit, "world", Actor("reported" + i), "signal" + i + ":" + j, Start));
                confirmed.Add(new(ActivityKind.CreatureKill, "world", Actor("confirmed" + i), "victim" + i + ":" + j, "token", Start));
            }
        for (int j = 0; j < 2; j++)
        {
            signals.Add(new(ReportedSignalKind.KillCredit, "world", Actor("winner"), "winner" + j, Start));
            confirmed.Add(new(ActivityKind.CreatureKill, "world", Actor("winner"), "winner" + j, "token", Start));
        }
        foreach (var batch in signals.Chunk(8))
        {
            Check(store.Enqueue(new ActivityBatch(signals: batch, signalPrincipal: new("server", "local", true))), "bounded signal admission");
            store.QueryAsync("world", Start, End, new()).GetAwaiter().GetResult();
        }
        foreach (var batch in confirmed.Chunk(32)) Check(store.Enqueue(new ActivityBatch(activities: batch)), "confirmed admission");
        var rows = store.QueryAsync("world", Start, End, new(reportedKillCredits: true)).GetAwaiter().GetResult().Rows[ReportMetric.ReportedKillCredits];
        Equal(16, rows.Count, "bounded merged ranking"); Equal("winner", rows[0].Id, "merge before top sixteen reduction"); Equal(4L, rows[0].Count, "both sources contribute without losing sub-top actors");
    }
}
