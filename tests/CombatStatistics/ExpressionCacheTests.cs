using System.Collections;
using System.Reflection;

using DiscordConnector.Records;

using LiteDB;

internal static partial class Program
{
    private static void ExpressionCacheBounded(string directory)
    {
        var caches = new[] { "_cacheScalar", "_cacheEnumerable" }.Select(name =>
            (IDictionary)typeof(BsonExpression).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!).ToArray();
        int Size() => caches.Sum(cache => cache.Count);
        using var db = new LiteDatabase(Path.Combine(directory, "records.db"));
        var actions = new PlayerActionStore(db); var signals = new ReportedSignalStore(db); var legacy = new CombatStore(db);
        using var worker = new ActivityStore(db, legacy, actions, new CookingStore(db), _ => { }, observationLoss: NewLoss());
        var options = new LeaderboardsOptions(bossKills: true, biomes: true, healthLost: true, sailing: true,
            exploration: true, cookingCompletions: true, dodgeRolls: true, reportedKillCredits: true);
        void Exercise(int i)
        {
            string id = "cache-" + i, world = "world-" + i;
            DateTime utc = Start.AddSeconds(i * 120);
            var actor = new ActivityActor(id, "Cache User", "session-" + i, utc);
            Check(actions.InsertAction(new(world, actor, PlayerActionKind.DodgeRoll, id, utc)), "distinct action inserted");
            Check(signals.Insert(new(ReportedSignalKind.KillCredit, world, actor, id, utc)), "distinct signal inserted");
            Check(legacy.Insert(Record(id, world, utc)), "compatibility record inserted");
            Equal(1, legacy.Query(world, utc, utc.AddTicks(1)).Count, "parameterized compatibility period");
            var cooking = new CookingStore(db);
            CookingSlotSnapshot Slot(CookingStatus status) => new(world, id, 0, 1, "raw", status, 0, "collector");
            Check(cooking.Apply(new(CookingChangeKind.AcceptedInput, Slot(CookingStatus.NotDone), id, cooking.ObservationEpoch, 1, actor, utc)), "new cooking session input");
            Check(cooking.Apply(new(CookingChangeKind.Completed, Slot(CookingStatus.Done), id, cooking.ObservationEpoch, 1, null, utc, new("cooked", 1))), "distinct completion inserted");
            Check(worker.Enqueue(new ActivityBatch(activities: [
                new(ActivityKind.HealthLost, world, actor, id, id, utc, hpLoss: 1),
                new(ActivityKind.BossCredit, world, actor, "defeated_eikthyr", id, utc, "defeated_eikthyr"),
                new(ActivityKind.BiomeVisited, world, actor, "Meadows", id, utc, "Meadows"),
                new(ActivityKind.Sailing, world, actor, id, id, utc, startUtc: utc.AddSeconds(-1), seconds: 1, meters: 1)
            ], chunks: [new(world, actor, new ExplorationGrid(32, 1, 1), 0, 0, [new(0, utc)])])), "distinct activity batch");
            var report = worker.QueryAsync(world, utc.AddSeconds(-2), utc.AddSeconds(1), options).GetAwaiter().GetResult();
            Equal(1.0, report.Rows[ReportMetric.HealthLost].Single().HpLoss, "query parameters stay bound to the current world/period");
            Equal(1L, report.Rows[ReportMetric.ParticipantBossCredits].Single().Count, "boss predicate parameters retained");
            Check(worker.TryClaimReportAsync(id, utc).GetAwaiter().GetResult(), "distinct report claim");
            Equal(1, signals.PruneExpired(utc.AddDays(91)), "changing retention cutoff stays bound");
        }
        Exercise(0); Exercise(1);
        int before = Size();
        for (int i = 2; i < 130; i++) Exercise(i);
        Equal(before, Size(), "only fixed expression shapes may remain in the process-wide LiteDB caches");
    }
}
