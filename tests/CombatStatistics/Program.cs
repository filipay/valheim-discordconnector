using System.Collections.Concurrent;
using DiscordConnector;
using DiscordConnector.Records;

using LiteDB;

internal static partial class Program
{
    private static readonly DateTime Start = new(2026, 12, 31, 23, 59, 59, DateTimeKind.Utc);
    private static readonly DateTime End = Start.AddSeconds(2);
    private static readonly (string Name, Action<string> Run)[] Tests =
    [
        ("observation ids, worlds, cooking sessions and retention cutoffs do not grow LiteDB expression caches", ExpressionCacheBounded),
        ("coalesced coverage preserves three numerical drops", CoalescedLossCoverage),
        ("full replay cache counts new refusals, not duplicate witnesses", LossReplayRejections),
        ("ambiguous diagnostic acknowledgment reconciles without gameplay replay", LossAmbiguousAcknowledgment),
        ("absolute loss snapshots merge by field and epoch across reopen", LossEpochsAndReopen),
        ("loss state saturates and refuses a 65th key without eviction", LossBounds),
        ("diagnostics reserve one existing slot and serialize with gameplay", LossQueueReservation),
        ("diagnostic failures retain totals and retry only on maintenance", LossWriteFailure),
        ("an in-flight acknowledgment cannot erase a newer cumulative value", LossInFlightUpdate),
        ("collector refusal, flush, reset and world change retain loss ownership", LossCollectorBoundaries),
        ("failed gameplay batch is uncertain, never replayed or declared absent", LossGameplayUncertainty),
        ("legacy collections survive initialization, insert and restart", LegacySurvives),
        ("duplicate inserts are atomic across stores and restart", ConcurrentDuplicates),
        ("world and victim form an unambiguous durable identity", WorldSeparation),
        ("UTC queries use half-open year-crossing boundaries", HalfOpen),
        ("UTC timestamps and submillisecond boundaries round-trip", TickPrecision),
        ("renames preserve account identity, shared names do not merge accounts", PlayerIdentity),
        ("invalid records fail without damaging valid records", InvalidRecords),
        ("invalid query ranges fail explicitly", InvalidQueries),
        ("disposed database failures surface for every store operation", DatabaseFailure),
        ("nonduplicate LiteDB write failures propagate and roll back", IndexFailure),
        ("report claims are atomic and survive restart", ReportClaims),
        ("invalid report claims fail without claiming", InvalidClaims),
        ("early Combat access waits and shutdown closes its database", EarlyAccessAndShutdown),
        ("initialization failure is explicit and does not create another database", InitializationFailure),
        ("activity replay, boundaries and exploration grid fencing survive restart", ActivityRestart),
        ("sampled biomes deduplicate per player/day and distinct report period across restart", BiomeVisitStorage),
        ("boss cohorts deduplicate per key/window and survive restart", BossCreditStorage),
        ("player travel intervals clip and dedup across restart with independent biome totals", PlayerTravelStorage),
        ("simulation damage and per-participant kills preserve dedup flags worlds and restart", SimulationCombatStorage),
        ("reported and confirmed kills merge before bounded ranking", SimulationCombinedRanking),
        ("longest bow hit keeps per-player daily weekly maxima with dedup and range fences", LongestBowHitStorage),
        ("sailing distance is additive, clipped, ranked and survives reopen", SailingDistanceStorage),
        ("health loss persists exact HP with own flag, boundaries, dedup and restart", HealthLostStorage),
        ("retired metric histories remain untouched and new writes contain only kept data", RetiredMetricHistory),
        ("planted crops deduplicate world/ZDOID across accounts, tokens and restart", CropStorage),
        ("ReportedSignalsRestartAndIsolation", ReportedSignalsRestartAndIsolation),
        ("signal retention keeps cutoff equality and unrelated history", SignalRetentionBoundaries),
        ("signal retention uses indexed bounded batches across all kinds", SignalRetentionBounds),
        ("signal retention deletion survives restart", SignalRetentionRestart),
        ("backdated duplicate cannot replace live signal detail", SignalRetentionLiveDuplicate),
        ("signal retention uses the current clock after corrections", SignalRetentionClocks),
        ("signal retention validates UTC and bounded limits before writing", SignalRetentionValidation),
        ("retention skips full queues and preserves diagnostic capacity", RetentionQueueBounds),
        ("retention cadence, quota reservations and clean drain share the existing tail", RetentionCadenceAndDrain),
        ("retention rejects invalid clocks without resetting admission cadence", RetentionWorkerClocks),
        ("queued inserts and pruning respect maintenance ordering", RetentionWorkerOrdering),
        ("retention failure releases admission without classifying gameplay loss", RetentionWorkerFailure),
        ("retention continuation attachment failure releases its slot and marker", RetentionAttachmentFailure),
        ("SignalBatchAccounting", SignalBatchAccounting),
        ("SignalRecordValidation", SignalRecordValidation),
        ("SignalOutstandingAccounting", SignalOutstandingAccounting),
        ("one signal origin cannot monopolize outstanding work", SignalPrincipalFairness),
        ("SignalFailureAndDrain", SignalFailureAndDrain),
        ("signal diagnostics expose only stage and count, not payloads", SignalDiagnostics),
        ("CookingGenerationRestart", CookingGenerationRestart),
        ("database diagnostics redact hostile exceptions and bound detail", DiagnosticRedaction),
        ("database diagnostic budgets suppress and recover independently", DiagnosticBudgets),
        ("rollback failure preserves the original exception and stack", RollbackPreservesOriginal),
        ("worker diagnostics identify stages without exposing records", WorkerDiagnostics),
        ("throwing diagnostic callback cannot poison the worker", DiagnosticCallbackFailure),
        ("shutdown returns while accepted activity is blocked and drains before close", NonblockingShutdown),
        ("shutdown drains accepted legacy writes even when a report fails", ShutdownLegacyWrites),
        ("shutdown waits off-thread for synchronous legacy readers", ShutdownLegacyReader),
        ("early legacy writes are owned through initialization and shutdown", EarlyLegacyShutdown),
        ("activity BeginDrain seals admission without owning the borrowed database", BorrowedActivityDrain)
    ];

    private static int Main(string[] args)
    {
        if (args.Length != 0)
        {
            if (args.SequenceEqual(new[] { "--growth-check" })) return GrowthCheck();
            Console.WriteLine("Usage: CombatStatistics [--growth-check]");
            return 1;
        }
        int failures = 0;
        foreach ((string name, Action<string> run) in Tests)
        {
            string directory = Path.Combine(Path.GetTempPath(), "discord-combat-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                run(directory);
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception exception)
            {
                failures++;
                Console.WriteLine($"FAIL {name}: {exception.GetBaseException().GetType().Name}: {exception.GetBaseException().Message}");
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        Console.WriteLine($"{Tests.Length - failures}/{Tests.Length} passed");
        return failures == 0 ? 0 : 1;
    }

    private static CombatRecord Record(string victim = "victim", string world = "world", DateTime? timestamp = null) => new()
    {
        WorldId = world,
        VictimId = victim,
        PlayerId = "Steam_12345",
        PlayerName = "First name",
        Creature = "Greydwarf",
        Biome = "BlackForest",
        IsBoss = false,
        TimestampUtc = timestamp ?? Start
    };

    private static CombatStore Combat(Database database) => database.Combat;

    private static string PrepareDatabasePath(string directory)
    {
        BepInEx.Paths.ConfigPath = directory;
        string configDirectory = Path.Combine(directory, DiscordConnectorPlugin.LegacyConfigPath);
        Directory.CreateDirectory(configDirectory);
        return Path.Combine(configDirectory, "records.db");
    }

    private static void LegacySurvives(string directory)
    {
        string path = PrepareDatabasePath(directory);
        string[] names = ["deaths", "joins", "leaves", "shouts", "pings", "player_name"];
        BsonDocument sentinel = new()
        {
            ["_id"] = "legacy-sentinel", ["PlayerId"] = "Steam_legacy", ["Name"] = "Legacy",
            ["CharacterName"] = "Legacy", ["Date"] = Start.AddYears(-1), ["unknown_field"] = "preserve me"
        };
        using (LiteDatabase db = new(path))
            foreach (string name in names) db.GetCollection(name).Insert(sentinel);

        Database database = new(directory);
        try
        {
            CombatStore store = Combat(database);
            database.Initialize();
            Check(ReferenceEquals(store, Combat(database)), "Initialize must not replace the connection/store");
            Check(store.Insert(Record()), "first combat insert");
        }
        finally { database.Dispose(); }

        using LiteDatabase reopened = new(path);
        foreach (string name in names)
        {
            BsonDocument actual = reopened.GetCollection(name).FindById("legacy-sentinel");
            Equal(sentinel.ToString(), actual.ToString(), $"unchanged legacy {name}");
            Equal(1, reopened.GetCollection(name).Count(), $"legacy {name} count");
        }
        CombatStore restored = new(reopened);
        Equal(1, restored.Query("world", Start, End).Count, "combat persisted in same database");
        Check(!restored.Insert(Record()), "restart duplicate rejected");
        Check(!reopened.GetCollectionNames().Except(names.Concat(["combat_kills", "combat_reports"]).Concat(ActivityStore.CollectionNames)).Any(), "no unrelated collections");
        string[] indexes = reopened.Execute("SELECT name FROM $indexes WHERE collection = 'combat_kills'")
            .ToArray().Select(x => x["name"].AsString).ToArray();
        foreach (string field in new[] { "WorldId", "TimestampUtc", "PlayerId" })
            Check(indexes.Contains(field), $"index on {field}");
    }

    private static void ConcurrentDuplicates(string directory)
    {
        string path = Path.Combine(directory, "records.db");
        using (LiteDatabase db = new(path))
        {
            CombatStore first = new(db), second = new(db);
            ConcurrentBag<bool> results = new();
            Parallel.For(0, 64, i => results.Add((i % 2 == 0 ? first : second).Insert(Record())));
            Equal(1, results.Count(x => x), "exactly one atomic insert");
            Equal(1, first.Query("world", Start, End).Count, "one stored record");
        }
        using LiteDatabase reopened = new(path);
        Check(!new CombatStore(reopened).Insert(Record()), "durable deduplication");
    }

    private static void WorldSeparation(string directory)
    {
        using LiteDatabase db = new(Path.Combine(directory, "records.db"));
        CombatStore store = new(db);
        foreach ((string world, string victim) in new[] { ("a:b", "c"), ("a", "b:c"), ("other", "c"), ("OTHER", "c") })
            Check(store.Insert(Record(victim, world)), "different world/victim pair");
        foreach (string world in new[] { "a:b", "a", "other", "OTHER" })
            Equal(1, store.Query(world, Start, End).Count, $"world isolation {world}");
        CombatRecord duplicate = Record("c", "a:b");
        duplicate.Id = "caller-controlled-alternate-id";
        Check(!store.Insert(duplicate), "caller ID cannot bypass world/victim deduplication");
    }

    private static void HalfOpen(string directory)
    {
        using LiteDatabase db = new(Path.Combine(directory, "records.db"));
        CombatStore store = new(db);
        foreach ((string victim, DateTime timestamp) in new[]
                 { ("before", Start.AddMilliseconds(-1)), ("start", Start), ("inside", End.AddMilliseconds(-1)), ("end", End) })
            Check(store.Insert(Record(victim, timestamp: timestamp)), "boundary fixture");
        string[] victims = store.Query("world", Start, End).Select(x => x.VictimId).OrderBy(x => x).ToArray();
        Equal("inside,start", string.Join(",", victims), "[start,end)");
        Equal("end", store.Query("world", End, End.AddSeconds(1)).Single().VictimId, "adjacent window");
        Equal(0, store.Query("world", Start, Start).Count, "empty interval");
    }

    private static void TickPrecision(string directory)
    {
        using LiteDatabase db = new(Path.Combine(directory, "records.db"));
        CombatStore store = new(db);
        DateTime timestamp = Start.AddTicks(1234);
        Check(store.Insert(Record(timestamp: timestamp)), "submillisecond insert");
        CombatRecord actual = store.Query("world", timestamp, timestamp.AddTicks(1)).Single();
        Equal(DateTimeKind.Utc, actual.TimestampUtc.Kind, "UTC kind");
        Equal(timestamp, actual.TimestampUtc, "exact timestamp");
        Equal(0, store.Query("world", Start, timestamp).Count, "exclusive submillisecond end");
    }

    private static void PlayerIdentity(string directory)
    {
        using LiteDatabase db = new(Path.Combine(directory, "records.db"));
        CombatStore store = new(db);
        CombatRecord first = Record("first"), renamed = Record("second"), other = Record("third");
        renamed.PlayerName = "Renamed";
        renamed.IsBoss = true;
        other.PlayerId = "PlayFab_other";
        other.PlayerName = renamed.PlayerName;
        foreach (CombatRecord record in new[] { first, renamed, other }) Check(store.Insert(record), "identity fixture");
        List<CombatRecord> actual = store.Query("world", Start, End);
        Equal(2, actual.GroupBy(x => x.PlayerId).Count(), "stable account grouping");
        Equal(2, actual.Count(x => x.PlayerId == "Steam_12345"), "renamed player retains both kills");
        Check(actual.Any(x => x.PlayerName == "First name") && actual.Count(x => x.PlayerName == "Renamed") == 2, "display snapshots preserved");
        Check(actual.Single(x => x.VictimId == "second").IsBoss, "boss flag preserved");
        Check(actual.All(x => x.Creature == "Greydwarf" && x.Biome == "BlackForest"), "breakdown fields preserved");
    }

    private static void InvalidRecords(string directory)
    {
        using LiteDatabase db = new(Path.Combine(directory, "records.db"));
        CombatStore store = new(db);
        Throws<ArgumentException>(() => store.Insert(null));
        foreach (string field in new[] { "WorldId", "VictimId", "PlayerId", "PlayerName", "Creature", "Biome" })
        {
            foreach (string invalid in new[] { null, "", "   " })
            {
                CombatRecord record = Record();
                typeof(CombatRecord).GetProperty(field).SetValue(record, invalid);
                Throws<ArgumentException>(() => store.Insert(record));
            }
        }
        foreach (DateTime invalid in new[] { default, DateTime.SpecifyKind(Start, DateTimeKind.Local), DateTime.SpecifyKind(Start, DateTimeKind.Unspecified), DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc) })
            Throws<ArgumentException>(() => store.Insert(Record(timestamp: invalid)));
        Equal(0, store.Query("world", Start, End).Count, "no invalid writes");
        Check(store.Insert(Record()), "valid record remains usable");
    }

    private static void InvalidQueries(string directory)
    {
        using LiteDatabase db = new(Path.Combine(directory, "records.db"));
        CombatStore store = new(db);
        Throws<ArgumentException>(() => store.Query(null, Start, End));
        Throws<ArgumentException>(() => store.Query(" ", Start, End));
        Throws<ArgumentException>(() => store.Query("world", End, Start));
        Throws<ArgumentException>(() => store.Query("world", DateTime.SpecifyKind(Start, DateTimeKind.Local), End));
        Throws<ArgumentException>(() => store.Query("world", Start, DateTime.SpecifyKind(End, DateTimeKind.Unspecified)));
    }

    private static void DatabaseFailure(string directory)
    {
        LiteDatabase db = new(Path.Combine(directory, "records.db"));
        CombatStore store = new(db);
        db.Dispose();
        foreach (Action operation in new Action[]
                 { () => store.Insert(Record()), () => store.Query("world", Start, End), () => store.TryClaimReport("report", Start) })
        {
            Exception failure = Throws<Exception>(operation);
            Check(failure is ObjectDisposedException || failure is LiteException && failure.Message.Contains("disposed"),
                "disposed-engine failure propagated");
        }
    }

    private static void IndexFailure(string directory)
    {
        using LiteDatabase db = new(Path.Combine(directory, "records.db"));
        CombatStore store = new(db);
        Check(store.Insert(Record()), "valid fixture");
        CombatRecord invalid = Record("another");
        invalid.PlayerId = new string('x', 4096);
        LiteException failure = Throws<LiteException>(() => store.Insert(invalid));
        Check(failure.ErrorCode != LiteException.INDEX_DUPLICATE_KEY, "a real nonduplicate database failure");
        Equal(1, store.Query("world", Start, End).Count, "failed write rolled back");
        Check(store.Insert(Record("another")), "failed write did not consume victim identity");
    }

    private static void ReportClaims(string directory)
    {
        string path = Path.Combine(directory, "records.db");
        using (LiteDatabase db = new(path))
        {
            CombatStore first = new(db), second = new(db);
            ConcurrentBag<bool> results = new();
            Parallel.For(0, 64, i => results.Add((i % 2 == 0 ? first : second).TryClaimReport("world:daily:2026-12-31", Start)));
            Equal(1, results.Count(x => x), "exactly one report claimant");
        }
        using LiteDatabase reopened = new(path);
        CombatStore restored = new(reopened);
        Check(!restored.TryClaimReport("world:daily:2026-12-31", End), "checkpoint survives restart");
        Check(restored.TryClaimReport("other:daily:2026-12-31", End), "separate world report");
        Check(restored.TryClaimReport("OTHER:daily:2026-12-31", End), "case-sensitive world report identity");
        Check(restored.TryClaimReport("world:weekly:2026-12-31", End), "separate cadence report");
    }

    private static void InvalidClaims(string directory)
    {
        using LiteDatabase db = new(Path.Combine(directory, "records.db"));
        CombatStore store = new(db);
        foreach (string id in new[] { null, "", " " }) Throws<ArgumentException>(() => store.TryClaimReport(id, Start));
        foreach (DateTime invalid in new[] { default, DateTime.SpecifyKind(Start, DateTimeKind.Local), DateTime.SpecifyKind(Start, DateTimeKind.Unspecified) })
            Throws<ArgumentException>(() => store.TryClaimReport("report", invalid));
        Check(store.TryClaimReport("report", Start), "invalid claim did not consume valid key");
    }

    private static void EarlyAccessAndShutdown(string directory)
    {
        string path = PrepareDatabasePath(directory);
        for (int i = 0; i < 8; i++)
        {
            Database database = new(directory);
            CombatStore store = Combat(database);
            Check(store.Insert(Record(i.ToString())), "early event persists");
            database.Dispose();
            database.Dispose();
            Throws<ObjectDisposedException>(() => Combat(database));
            Throws<ObjectDisposedException>(() => database.Initialize());
            Throws<Exception>(() => store.Insert(Record("after-shutdown")));
        }
        using LiteDatabase reopened = new(path);
        Equal(8, new CombatStore(reopened).Query("world", Start, End).Count, "all early events durable");
        reopened.Dispose();
        Database immediateShutdown = new(directory);
        immediateShutdown.Dispose();
        using LiteDatabase afterShutdown = new(path);
        Equal(8, new CombatStore(afterShutdown).Query("world", Start, End).Count, "shutdown waited and released connection");
    }

    private static void InitializationFailure(string directory)
    {
        string path = PrepareDatabasePath(directory);
        Directory.CreateDirectory(path);
        Database database = new(directory);
        try
        {
            InvalidOperationException failure = Throws<InvalidOperationException>(() => Combat(database));
            Check(failure.InnerException != null, "initialization cause retained");
            Throws<InvalidOperationException>(() => Combat(database));
            Check(!Directory.EnumerateFiles(directory, "*.db", SearchOption.AllDirectories).Any(), "no fallback database");
        }
        finally
        {
            database.Dispose();
            database.Dispose();
        }
        Check(!Directory.EnumerateFiles(directory, "*.db", SearchOption.AllDirectories).Any(), "shutdown did not create a fallback database");
    }

    private static T Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T exception) { return exception; }
        throw new Exception($"Expected {typeof(T).Name}");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Equal<T>(T expected, T actual, string message) =>
        Check(EqualityComparer<T>.Default.Equals(expected, actual), $"{message}: expected {expected}, got {actual}");
}
