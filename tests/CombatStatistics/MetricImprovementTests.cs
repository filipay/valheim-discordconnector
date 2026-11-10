using DiscordConnector.Records;
using LiteDB;

internal static partial class Program
{
    private static void HealthLostStorage(string directory)
    {
        string path = Path.Combine(directory, "records.db");
        ActivityRecord Loss(string token, double hp, DateTime? utc = null, string world = "world", ActivityActor? actor = null) =>
            new(ActivityKind.HealthLost, world, actor ?? Actor(), "character", token, utc ?? Start, hpLoss: hp);
        var observation = Loss("first", 19.75);
        using (var db = new LiteDatabase(path))
        using (var store = new ActivityStore(db, new CombatStore(db), new PlayerActionStore(db), new CookingStore(db), _ => { }))
        {
            Check(store.Enqueue(new ActivityBatch(activities: [observation, Loss("second", 5.5), observation,
                Loss("end", 100, End), Loss("other", 999, world: "other"), Loss("bigger", 40, actor: Actor("bigger"))])), "health activity batch accepted");
            var rows = store.QueryAsync("world", Start, End, new(healthLost: true)).GetAwaiter().GetResult().Rows[ReportMetric.HealthLost];
            Equal("bigger", rows[0].Id, "health ranks by HP, not activity count");
            Equal(25.25, rows[1].HpLoss, "exact sum with durable dedup and half-open interval");
            Equal(0, store.QueryAsync("world", Start, End, new()).GetAwaiter().GetResult().Rows.Count, "own flag excludes health history");
            Equal(0, store.QueryAsync("WORLD", Start, End, new(healthLost: true)).GetAwaiter().GetResult().Rows.Count, "ordinal world isolation");
            foreach (double invalid in new[] { 0, -1, double.NaN, double.PositiveInfinity })
                Throws<ArgumentException>(() => Loss("invalid", invalid));
        }
        using (var db = new LiteDatabase(path))
        using (var store = new ActivityStore(db, new CombatStore(db), new PlayerActionStore(db), new CookingStore(db), _ => { }))
        {
            Check(store.Enqueue(new ActivityBatch(activities: [observation])), "health replay admitted after reopen");
            Equal(65.25, store.QueryAsync("world", Start, End, new(healthLost: true)).GetAwaiter().GetResult().Rows[ReportMetric.HealthLost].Sum(row => row.HpLoss), "health survives reopen without duplicate");
        }
        var loss = new ObservationLoss(_ => { });
        loss.Batch(new ActivityBatch(activities: [observation]), ObservationRejection.QueueFull);
        Equal(1L, loss.Snapshot("world", ReportMetric.HealthLost)!.Rejections[ObservationRejection.QueueFull], "health rejection has its own metric");
    }

    private static void CropStorage(string directory)
    {
        string path = Path.Combine(directory, "records.db");
        ActivityRecord Crop(string world, string id, string token, ActivityActor? actor = null, DateTime? when = null) =>
            new(ActivityKind.CropPlanted, world, actor ?? Actor(), id, token, when ?? Start, "sapling_carrot");
        using (var db = new LiteDatabase(path))
        using (var store = new ActivityStore(db, new CombatStore(db), new PlayerActionStore(db), new CookingStore(db), _ => { }))
        {
            Check(store.Enqueue(new ActivityBatch(activities: [Crop("world", "10:2", "first"),
                Crop("world", "10:2", "duplicate", Actor("other")), Crop("other-world", "10:2", "first"),
                Crop("world", "10:3", "end", when: End)])), "crop batch accepted");
            var snapshot = store.QueryAsync("world", Start, End, new(cropsPlanted: true)).GetAwaiter().GetResult();
            Equal(1L, snapshot.Rows[ReportMetric.CropsPlanted].Single().Count, "one credit per world/ZDOID, half-open period");
            Equal(0, store.QueryAsync("world", Start, End, new()).GetAwaiter().GetResult().Rows.Count, "own crop flag only");
            Equal(0, store.QueryAsync("WORLD", Start, End, new(cropsPlanted: true)).GetAwaiter().GetResult().Rows.Count, "ordinal world identity");
        }
        using (var db = new LiteDatabase(path))
        using (var store = new ActivityStore(db, new CombatStore(db), new PlayerActionStore(db), new CookingStore(db), _ => { }))
        {
            Check(store.Enqueue(new ActivityBatch(activities: [Crop("world", "10:2", "restart")])), "restart replay admitted");
            Equal(1L, store.QueryAsync("world", Start, End, new(cropsPlanted: true)).GetAwaiter().GetResult().Rows[ReportMetric.CropsPlanted].Single().Count,
                "durable dedup survives restart");
        }
        var loss = new ObservationLoss(_ => { });
        loss.Batch(new ActivityBatch(activities: [Crop("world", "10:4", "lost")]), ObservationRejection.QueueFull);
        Equal(1L, loss.Snapshot("world", ReportMetric.CropsPlanted)!.Rejections[ObservationRejection.QueueFull], "crop queue loss has its own metric");
    }

    private static void SailingDistanceStorage(string directory)
    {
        string path = Path.Combine(directory, "records.db");
        using (var db = new LiteDatabase(path))
        {
            using var store = new ActivityStore(db, new CombatStore(db), new PlayerActionStore(db), new CookingStore(db), _ => { });
            ActivityRecord Span(string token, double meters, ActivityActor? actor = null) => new(ActivityKind.Sailing, "world", actor ?? Actor(), "ship", token,
                End, startUtc: Start.AddSeconds(-2), seconds: 4, meters: meters);
            var distance = Span("distance", 40);
            Check(store.Enqueue(new ActivityBatch(activities: [Span("legacy", 0), distance, Span("faster", 80, Actor("faster"))])), "sailing batch accepted");
            var snapshot = store.QueryAsync("world", Start, End, new(sailing: true)).GetAwaiter().GetResult();
            // Reproduce an actual pre-distance document (not just a zero-valued new record).
            var observations = db.GetCollection("activity_observations");
            var legacy = observations.FindAll().Single(x => x["Token"].AsString == "legacy");
            legacy.Remove("Meters"); observations.Update(legacy);
            Check(store.Enqueue(new ActivityBatch(activities: [distance])), "replay accepted for durable dedup");
            snapshot = store.QueryAsync("world", Start, End, new(sailing: true)).GetAwaiter().GetResult();
            var rows = snapshot.Rows[ReportMetric.SailingDistance];
            Equal("faster", rows[0].Id, "rank by meters, not span count");
            Equal(40.0, rows[0].Meters, "half-open overlap clips distance proportionally");
            Equal(20.0, rows[1].Meters, "legacy seconds do not become meters and replay does not double distance");
            Check(!snapshot.Rows.ContainsKey((ReportMetric)8), "retired time metric identity is not reused");
            Equal(0, store.QueryAsync("world", Start, End, new()).GetAwaiter().GetResult().Rows.Count, "own flag hides distance");
        }
        using (var db = new LiteDatabase(path))
        using (var store = new ActivityStore(db, new CombatStore(db), new PlayerActionStore(db), new CookingStore(db), _ => { }))
        {
            var snapshot = store.QueryAsync("world", Start, End, new(sailing: true)).GetAwaiter().GetResult();
            Equal(60.0, snapshot.Rows[ReportMetric.SailingDistance].Sum(row => row.Meters), "distance survives reopen");
            Equal(3, db.GetCollection("activity_observations").Count(), "legacy sailing retained");
        }
    }

    private static void RetiredMetricHistory(string directory)
    {
        Check(Enum.GetValues<ReportMetric>().Select(metric => (int)metric).SequenceEqual(new[] { 1, 4, 6, 9, 10, 12, 13, 14, 15, 16, 17, 19, 20, 21, 22, 23, 24, 25, 26 }),
            "retained metric identities stay unchanged; travel and simulation damage append without reusing retired identities");
        string path = Path.Combine(directory, "records.db");
        using var db = new LiteDatabase(path);
        var deathRows = db.GetCollection("combat_participation_v1_deaths");
        var oldDeath = new BsonDocument { ["_id"] = "old-death", ["WorldId"] = "world", ["TimestampUtc"] = Start.Ticks,
            ["Biome"] = "BlackForest", ["BiomeKey"] = ObservationValues.Key("BlackForest"), ["IsBoss"] = false,
            ["Prefab"] = "Boar", ["CreatureKey"] = "old-creature", ["PlayerId"] = "old-final-blow", ["PlayerKey"] = "old-player" };
        deathRows.Insert(oldDeath);
        var oldDamage = new BsonDocument { ["_id"] = "old-damage", ["WorldId"] = "world", ["TimestampUtc"] = Start.Ticks, ["HpLoss"] = 999 };
        db.GetCollection("combat_direct_damage_v1").Insert(oldDamage);
        var oldLoss = new BsonDocument { ["_id"] = "old-loss", ["WorldId"] = "world", ["Metric"] = "FinalBlows" };
        db.GetCollection("server_observation_v2_loss").Insert(oldLoss);
        var oldAction = new BsonDocument { ["_id"] = "old-action", ["WorldId"] = "world", ["TimestampUtc"] = Start.Ticks,
            ["Kind"] = "DodgeAvoidance" };
        db.GetCollection("player_actions_v1").Insert(oldAction);
        var oldPiece = ObservationValues.Document("old-piece", "world", Start, Actor());
        oldPiece["Kind"] = "NewPiece"; oldPiece["EstimatedUnits"] = 99;
        oldPiece["Materials"] = new BsonArray { new BsonDocument { ["Prefab"] = "Wood", ["Units"] = 99 } };
        db.GetCollection("activity_observations").Insert(oldPiece);
        string deathJson = oldDeath.ToString(), lossJson = oldLoss.ToString(), actionJson = oldAction.ToString();
        string pieceJson = oldPiece.ToString();
        var participants = new PlayerActionStore(db);
        using var worker = new ActivityStore(db, new CombatStore(db), participants, new CookingStore(db), _ => { });
        Check(worker.Enqueue(new ActivityBatch(activities: [new(ActivityKind.NewPiece, "world", Actor(), "new-piece", "placement", Start, "wood_wall")])), "new placement admitted");
        var snapshot = worker.QueryAsync("world", Start, End, new(bossKills: true, biomes: true, dodgeRolls: true, construction: true)).GetAwaiter().GetResult();
        Check(!snapshot.Rows.ContainsKey(ReportMetric.ParticipantBossCredits), "old victim-owned participation never becomes boss-key credits");
        Check(!snapshot.Rows.ContainsKey(ReportMetric.Biomes), "old death-location rows never become visited biomes");
        Check(!snapshot.Rows.ContainsKey(ReportMetric.ObservedDodgeRolls), "old avoidance rows never become rolls");
        Equal(2L, snapshot.Rows[ReportMetric.NewPieces].Single().Count, "old and new placements count without material estimates");
        Check(db.GetCollection("activity_observations").FindAll().Where(d => d["_id"].AsString != "old-piece").All(d => !d.ContainsKey("Materials") && !d.ContainsKey("EstimatedUnits")), "new placements omit recipe data");
        Equal(deathJson, deathRows.FindById("old-death").ToString(), "stored retired death fields untouched");
        Equal(oldDamage.ToString(), db.GetCollection("combat_direct_damage_v1").FindById("old-damage").ToString(), "stored retired direct damage remains untouched and excluded");
        Equal(lossJson, db.GetCollection("server_observation_v2_loss").FindById("old-loss").ToString(), "stored retired loss document untouched and ignored");
        Equal(actionJson, db.GetCollection("player_actions_v1").FindById("old-action").ToString(), "stored retired avoidance document untouched");
        Equal(pieceJson, db.GetCollection("activity_observations").FindById("old-piece").ToString(), "stored retired recipe fields untouched");
    }
}
