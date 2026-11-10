using DiscordConnector.Records;
using LiteDB;

internal static partial class Program
{
    private static ObservationPrincipal Principal(string account = "origin", string? connection = null, bool local = false) => new(account, connection ?? account + "-connection", local);
    private static ReportedSignalRecord Signal(string token = "signal", ReportedSignalKind kind = ReportedSignalKind.KillCredit,
        string world = "world", ActivityActor? actor = null, DateTime? timestamp = null) =>
        new(kind, world, actor ?? Actor(), token, timestamp ?? Start);

    private static void ReportedSignalsRestartAndIsolation(string directory)
    {
        string path = Path.Combine(directory, "records.db");
        LeaderboardsOptions options = new(reportedKillCredits: true, consumableUses: true);
        using (LiteDatabase db = new(path))
        {
            ReportedSignalStore signals = new(db);
            Check(signals.Insert(Signal()), "first signal inserted");
            Check(!signals.Insert(Signal(actor: Actor("other", "Replacement"), timestamp: End)), "same world/kind/token is immutable");
            Check(signals.Insert(Signal(kind: ReportedSignalKind.ConsumableUse)), "same token in other kind is separate");
            Check(signals.Insert(Signal(world: "WORLD")), "same token in case-distinct world is separate");
            Check(signals.Insert(Signal(world: "other")), "same token in another world is separate");
            foreach ((string token, DateTime timestamp) in new[]
                     { ("before", Start.AddTicks(-1)), ("inside", End.AddTicks(-1)), ("end", End) })
                Check(signals.Insert(Signal(token, timestamp: timestamp)), "exact tick boundary inserted");
            Check(signals.Insert(Signal("case-account", actor: Actor("ACCOUNT"))), "case-distinct account inserted");
            Check(signals.Insert(Signal("rename", actor: new("account", "Later", "next-connection", Start.AddTicks(1)))), "rename inserted");
            PlayerActionStore participants = new(db);
            CombatStore combat = new(db);
            Check(combat.Insert(Record("old-kill")), "old combat row inserted");
            db.GetCollection("combat_participation_v1_deaths").Insert(new BsonDocument { ["_id"] = "old-death", ["untouched"] = true });
            Check(participants.InsertAction(new("world", Actor(), PlayerActionKind.DodgeRoll, "old-action", Start)), "old action inserted");
            using ActivityStore worker = new(db, combat, participants, new CookingStore(db), _ => { });
            CombinedReportSnapshot result = worker.QueryAsync("world", Start, End, options).GetAwaiter().GetResult();
            Equal(2, result.Rows.Count, "only the two enabled new metrics");
            Equal(2, result.Rows[ReportMetric.ReportedKillCredits].Count, "ordinal accounts do not merge");
            ReportRow account = result.Rows[ReportMetric.ReportedKillCredits].Single(x => x.Id == "account");
            Equal(3L, account.Count, "one per signal with exact half-open ticks, no legacy credits");
            Equal("Later", account.Name, "latest observed name");
            Equal(1L, result.Rows[ReportMetric.ObservedConsumableUses].Single().Count, "one use, no old actions");
            Equal(0, signals.Query("world", Start, Start, options).Rows.Count, "empty window");
            Equal(1L, signals.Query("world", End, End.AddTicks(1), options).Rows[ReportMetric.ReportedKillCredits].Single().Count, "end moves to next window");
            Equal(1L, signals.Query("WORLD", Start, End, options).Rows[ReportMetric.ReportedKillCredits].Single().Count, "ordinal world filtering");
            Equal(0, signals.Query("world", Start, End, new()).Rows.Count, "new options default off");
            Equal(1, signals.Query("world", Start, End, new(reportedKillCredits: true)).Rows.Count, "kill option independent");
            Equal(1, signals.Query("world", Start, End, new(consumableUses: true)).Rows.Count, "consumable option independent");
            string[] expected = ["combat_participation_v1_deaths", "combat_participation_v1_contributors", "combat_direct_damage_v1", "player_actions_v1",
                "activity_observations", "activity_item_sources", "activity_exploration_grids", "activity_exploration_chunks",
                "server_observation_v1_coverage", "cooking_slots_v1", "cooking_completions_v1", "reported_kill_credits_v1", "observed_consumable_uses_v1", "server_observation_v2_loss", "reported_dodge_hit_notifications_v1"];
            Equal(string.Join(",", expected.Order()), string.Join(",", ActivityStore.CollectionNames.Order()), "explicit additive collection allowlist");
            Check(!db.GetCollectionNames().Except(expected.Concat(["combat_kills", "combat_reports"])).Any(), "no unexpected collections");
            Throws<ArgumentException>(() => signals.Query("world", End, Start, options));
            Throws<ArgumentNullException>(() => signals.Query("world", Start, End, null!));
            Throws<ArgumentNullException>(() => signals.Insert(null!));
            for (int i = 19; i >= 0; i--)
                Check(signals.Insert(Signal(i.ToString(), world: "top", actor: Actor($"account-{i:00}"))), "top fixture");
            ReportRow[] top = signals.Query("top", Start, End, options).Rows[ReportMetric.ReportedKillCredits].ToArray();
            Equal(16, top.Length, "streaming top bounded to sixteen");
            Equal("account-00", top[0].Id, "ordinal tie-break first");
            Equal("account-15", top[^1].Id, "ordinal tie-break cutoff");
        }
        using (LiteDatabase db = new(path))
        {
            ReportedSignalStore signals = new(db);
            Check(!signals.Insert(Signal()), "durable dedup after reopen");
            Check(!signals.Insert(Signal(kind: ReportedSignalKind.ConsumableUse)), "durable dedup for other kind");
            Equal(4L, signals.Query("world", Start, End, options).Rows[ReportMetric.ReportedKillCredits].Sum(x => x.Count), "restart count unchanged");
            Equal(1L, signals.Query("other", Start, End, options).Rows[ReportMetric.ReportedKillCredits].Single().Count, "other world preserved");
        }
        DodgeHistory(path);
    }

    private static void DodgeHistory(string path)
    {
        const ReportMetric metric = ReportMetric.ReportedDodgeHitNotifications;
        LeaderboardsOptions options = new(reportedDodgeHits: true);
        Equal(16, (int)metric, "seventeenth metric appended without reinterpreting avoidance");
        Check(!new LeaderboardsOptions().ReportedDodgeHits && !new LeaderboardsOptions(dodgeRolls: true).Includes(metric), "new flag independent/default off");
        using (LiteDatabase db = new(path))
        {
            ObservationLoss loss = new(_ => { }, "dodge", () => Start);
            using ActivityStore worker = new(db, new CombatStore(db), new PlayerActionStore(db), new CookingStore(db), _ => { }, observationLoss: loss);
            var record = Signal("same-envelope", ReportedSignalKind.DodgeHitNotification);
            Check(worker.Enqueue(new(signals: [record, record, Signal("distinct-envelope", ReportedSignalKind.DodgeHitNotification)], signalPrincipal: Principal("reporter"))), "notification batch admitted");
            var snapshot = worker.QueryAsync("world", Start, End, options).GetAwaiter().GetResult();
            Equal(1, snapshot.Rows.Count, "new flag queries only separate history");
            Equal(2L, snapshot.Rows[metric].Single().Count, "envelope dedup, not semantic/roll dedup");
            Equal(1, worker.QueryAsync("world", Start, End, new(dodgeRolls: true)).GetAwaiter().GetResult().Rows.Count, "roll inclusion reads only its existing row, not notifications");
            Check(!worker.Enqueue(new(signals: Enumerable.Range(0, 17).Select(i => Signal("refused" + i, ReportedSignalKind.DodgeHitNotification)).ToArray(), signalPrincipal: Principal("origin")), out var reason) &&
                reason == ObservationRejection.PrincipalQuota, "new kind uses existing outstanding origin quota");
            Equal(17L, loss.Snapshot("world", metric)!.Rejections[ObservationRejection.PrincipalQuota], "new metric refused captured-record units");
            worker.ReconcileLossAsync(loss.Snapshot("world", metric)!).GetAwaiter().GetResult();
            var coverage = worker.QueryAsync("world", Start, End, options).GetAwaiter().GetResult().Coverage.Single();
            Equal(17L, coverage.KnownLosses, "new metric cumulative diagnostics queried independently");
            Check(worker.Enqueue(new(coverage: [new("world", metric, CoverageState.Unavailable, Start, CoverageReason.CapabilityLost)])), "unavailable capability note admitted");
            snapshot = worker.QueryAsync("world", Start, End, options).GetAwaiter().GetResult();
            Equal(CoverageState.Unavailable, snapshot.Coverage.Single().State, "capability unavailable does not hide notification history");
            Equal(2L, snapshot.Rows[metric].Single().Count, "capability loss keeps positive history");
            Equal(1, db.GetCollection("player_actions_v1").Count(), "old roll row unchanged, no invented avoidance or roll");
            Equal(2, db.GetCollection("reported_dodge_hit_notifications_v1").Count(), "separate append-only collection");
        }
        using (LiteDatabase db = new(path))
        {
            ReportedSignalStore store = new(db);
            Check(!store.Insert(Signal("same-envelope", ReportedSignalKind.DodgeHitNotification)), "notification dedup survives reopen");
            Equal(2L, store.Query("world", Start, End, options).Rows[metric].Single().Count, "notification history survives reopen");
            Equal(0, store.Query("world", Start, End, new()).Rows.Count, "disabled inclusion does not query history");
            Equal(0, store.Query("WORLD", Start, End, options).Rows.Count, "notification world ordinal isolation");
        }
    }

    private static void SignalBatchAccounting(string directory)
    {
        using LiteDatabase db = new(Path.Combine(directory, "records.db"));
        using ActivityStore worker = new(db, new CombatStore(db), new PlayerActionStore(db), new CookingStore(db), _ => { });
        ReportedSignalRecord[] records = Enumerable.Range(0, 128).Select(i => Signal(i.ToString())).ToArray();
        ActivityBatch batch = new(signals: records, signalPrincipal: Principal());
        Equal(128, batch.PhysicalDocumentCount, "one physical document per signal");
        records[0] = Signal("caller-replacement");
        Equal("0", batch.Signals[0].Token, "signal array defensively copied");
        Throws<ArgumentException>(() => new ActivityBatch(signals: Enumerable.Range(0, 129).Select(i => Signal(i.ToString())).ToArray()));
        Throws<ArgumentException>(() => new ActivityBatch(signals: [null!]));
        Check(!worker.Enqueue(new ActivityBatch(signals: records, actions: [new("world", Actor(), PlayerActionKind.DodgeRoll, "overflow", Start)], signalPrincipal: Principal())),
            "129 mixed documents rejected before enqueue");
        Equal(0, db.GetCollection("reported_kill_credits_v1").Count(), "rejected work not written");
        Check(!worker.Enqueue(batch), "128 physical signals exceed one principal's outstanding bytes");
        Check(!worker.Enqueue(new ActivityBatch(signals: batch.Signals.Take(17).ToArray(), signalPrincipal: Principal()), out var reason) && reason == ObservationRejection.PrincipalQuota,
            "seventeen records reject the independent byte limit even in one batch");
        Equal(0, SignalCharge(worker), "oversized principal batch reserves nothing");
        Check(worker.Enqueue(new ActivityBatch(signals: batch.Signals.Take(16).ToArray(), signalPrincipal: Principal())), "sixteen signals at exact byte cap accepted");
        Equal(16L, worker.QueryAsync("world", Start, End, new(reportedKillCredits: true)).GetAwaiter().GetResult()
            .Rows[ReportMetric.ReportedKillCredits].Single().Count, "sixteen signals persisted");
        Check(worker.Enqueue(new ActivityBatch(signals: [Signal("ordered"), Signal("ordered", actor: Actor("replacement"))], signalPrincipal: Principal())), "one origin may credit multiple beneficiaries; ordered duplicate accepted");
        CombinedReportSnapshot ordered = worker.QueryAsync("world", Start, End, new(reportedKillCredits: true)).GetAwaiter().GetResult();
        Equal(17L, ordered.Rows[ReportMetric.ReportedKillCredits].Single().Count, "first signal in capture order wins immutable dedup");
        Equal("account", ordered.Rows[ReportMetric.ReportedKillCredits].Single().Id, "later duplicate cannot replace credited actor");
        CoverageUpdate[] coverage = Enum.GetValues<ReportMetric>().Select(metric => new CoverageUpdate("world", metric, CoverageState.Observing, Start)).ToArray();
        Check(worker.Enqueue(new ActivityBatch(coverage: coverage)), "all coverage metrics accepted");
        LeaderboardsOptions options = new(bossKills: true, biomes: true, dodgeRolls: true, sailing: true, floorPickups: true, construction: true, exploration: true, cookingCompletions: true, reportedKillCredits: true, consumableUses: true, reportedDodgeHits: true, cropsPlanted: true, healthLost: true);
        CombinedReportSnapshot result = worker.QueryAsync("world", Start, End, options).GetAwaiter().GetResult();
        Equal(13, result.Coverage.Count, "active coverage survives snapshot construction without retired sailing time");
        Equal(Enum.GetValues<ReportMetric>().Length, new CombinedReportSnapshot("world", coverage: result.Coverage.Concat(result.Coverage)).Coverage.Count, "coverage history remains bounded");
        var allReasons = Enum.GetValues<ReportMetric>().SelectMany(metric => Enum.GetValues<CoverageReason>()
            .Select(reason => new CoverageUpdate("world", metric, CoverageState.Partial, Start, reason))).ToArray();
        Equal(Enum.GetValues<ReportMetric>().Length * Enum.GetValues<CoverageReason>().Length, allReasons.Length, "all metric/reason slots accounted for, including retained legacy coverage");
        Equal(Enum.GetValues<ReportMetric>().Length, new ActivityBatch(coverage: allReasons).PhysicalDocumentCount, "full bounded coverage array coalesces physical documents");
        Equal(14, (int)ReportMetric.ReportedKillCredits, "existing enum values preserved");
        Equal(15, (int)ReportMetric.ObservedConsumableUses, "metrics appended");
        Check(!new LeaderboardsOptions().Includes(ReportMetric.ReportedKillCredits) && !new LeaderboardsOptions().Includes(ReportMetric.ObservedConsumableUses), "new flags default off");
    }

    private static void SignalRecordValidation(string _)
    {
        Throws<ArgumentException>(() => new ActivityBatch(signals: [Signal()]));
        foreach (string invalid in new[] { "", " ", "nul\0value" })
        {
            Throws<ArgumentException>(() => new ObservationPrincipal(invalid, "connection", false));
            Throws<ArgumentException>(() => new ObservationPrincipal("account", invalid, false));
        }
        Throws<ArgumentException>(() => new ObservationPrincipal(new string('a', 257), "connection", false));
        Throws<ArgumentException>(() => new ObservationPrincipal("account", new string('c', 129), false));
        string text256 = new('x', 256), text128 = new('n', 128);
        ReportedSignalRecord maximum = new(ReportedSignalKind.ConsumableUse, text256, new(text256, text128, text128, Start), text256, Start);
        Equal(text256, maximum.WorldId, "world at exact bound retained");
        Equal(text256, maximum.Actor.PlayerId, "account at exact bound retained");
        Equal(text128, maximum.Actor.PlayerName, "name at exact bound retained");
        Equal(text128, maximum.Actor.ConnectionToken, "connection at exact bound retained");
        Equal(text256, maximum.Token, "token at exact bound retained");
        Throws<ArgumentException>(() => Signal(world: text256 + "x"));
        Throws<ArgumentException>(() => Signal(actor: new(text256 + "x", "Name", "connection", Start)));
        Throws<ArgumentException>(() => Signal(actor: new("account", text128 + "x", "connection", Start)));
        Throws<ArgumentException>(() => Signal(actor: new("account", "Name", text128 + "x", Start)));
        Throws<ArgumentException>(() => Signal(text256 + "x"));
        foreach (string invalid in new[] { "", " ", "with\0nul" })
        {
            Throws<ArgumentException>(() => Signal(world: invalid));
            Throws<ArgumentException>(() => Signal(invalid));
        }
        Throws<ArgumentException>(() => Signal(timestamp: DateTime.SpecifyKind(Start, DateTimeKind.Local)));
        Throws<ArgumentException>(() => Signal(timestamp: DateTime.SpecifyKind(Start, DateTimeKind.Unspecified)));
        Throws<ArgumentException>(() => Signal(timestamp: DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc)));
        Throws<ArgumentException>(() => Signal(kind: (ReportedSignalKind)3));
        Throws<ArgumentNullException>(() => new ReportedSignalRecord(ReportedSignalKind.KillCredit, "world", null!, "token", Start));
        // The new bounds belong to signal records, not every existing observation's actors.
        Equal(257, new ActivityActor(text256 + "x", text128 + "x", text128 + "x", Start).PlayerId.Length, "legacy actor behavior unchanged");
    }

    private static int SignalCharge(ActivityStore worker) => (int)typeof(ActivityStore).GetField("outstandingSignalBytes",
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(worker)!;

    private static void SignalReservationsReleased(ActivityStore worker)
    {
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        foreach (string name in new[] { "signalAccounts", "signalConnections", "signalPrincipals" })
            Equal(0, ((System.Collections.IDictionary)typeof(ActivityStore).GetField(name, flags)!.GetValue(worker)!).Count, "reservation index removed on final release");
        foreach (string name in new[] { "localSignalBatches", "localSignalBytes", "pendingBatches", "outstandingSignalBytes" })
            Equal(0, (int)typeof(ActivityStore).GetField(name, flags)!.GetValue(worker)!, "reservation counter released: " + name);
    }

    private static void SignalPrincipalFairness(string directory)
    {
        using LiteDatabase db = new(Path.Combine(directory, "records.db"));
        using ActivityStore worker = new(db, new CombatStore(db), new PlayerActionStore(db), new CookingStore(db), _ => { });
        TaskCompletionSource barrier = new(TaskCreationOptions.RunContinuationsAsynchronously);
        typeof(ActivityStore).GetField("tail", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(worker, barrier.Task);
        Task? report = null, claim = null;
        try
        {
            for (int i = 0; i < 16; i++) Check(worker.Enqueue(new ActivityBatch(signals: [Signal(i.ToString())], signalPrincipal: Principal())), "sixteen batches admitted");
            Equal(65536, SignalCharge(worker), "exact per-origin 64 KiB");
            Check(!worker.Enqueue(new ActivityBatch(signals: [Signal("seventeenth")], signalPrincipal: Principal()), out var reason) && reason == ObservationRejection.PrincipalQuota, "principal policy must refuse seventeenth outstanding batch");
            Check(!worker.Enqueue(new ActivityBatch(signals: [Signal("reconnect")], signalPrincipal: Principal(connection: "replacement"))), "reconnect cannot bypass account reservation");
            Check(!worker.Enqueue(new ActivityBatch(signals: [Signal("connection")], signalPrincipal: Principal("different-account", "origin-connection"))), "connection limit independent of account");
            foreach (string honest in new[] { "honest1", "honest2" })
                Check(worker.Enqueue(new ActivityBatch(signals: [Signal(honest)], signalPrincipal: Principal(honest))), "two honest peers survive flood even with same beneficiary");
            for (int i = 0; i < 16; i++) Check(worker.Enqueue(new ActivityBatch(signals: [Signal("local" + i)], signalPrincipal: Principal("server", "epoch", true))), "local sixteen separate batches");
            Check(!worker.Enqueue(new ActivityBatch(signals: [Signal("local17")], signalPrincipal: Principal("server", "next-epoch", true))), "local does not mint allowance by token");
            Check(worker.Enqueue(new ActivityBatch()), "non-signal work never spends principal quotas");
            report = worker.QueryAsync("world", Start, End, new());
            claim = worker.TryClaimReportAsync("task2-manual", Start);
            Check(worker.QueryAsync("world", Start, End, new()).IsFaulted, "two report slots preserved during flood");
        }
        finally { barrier.SetResult(); }
        report?.GetAwaiter().GetResult(); claim?.GetAwaiter().GetResult();
        worker.QueryAsync("world", Start, End, new()).GetAwaiter().GetResult();
        Equal(0, SignalCharge(worker), "all successful work releases reservations");
        Check(worker.Enqueue(new ActivityBatch(signals: [Signal("reusable")], signalPrincipal: Principal())), "origin capacity reusable after completion");
        worker.QueryAsync("world", Start, End, new()).GetAwaiter().GetResult();
        Equal(0, SignalCharge(worker), "reuse has no charge leak");

        typeof(ActivityStore).GetField("tail", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(worker, null);
        Throws<NullReferenceException>(() => worker.Enqueue(new ActivityBatch(signals: [Signal("attachment")], signalPrincipal: Principal())));
        Equal(0, SignalCharge(worker), "attachment failure releases global reservation");
        typeof(ActivityStore).GetField("tail", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(worker, Task.CompletedTask);
        Check(worker.Enqueue(new ActivityBatch(signals: Enumerable.Range(0, 16).Select(i => Signal("after-attachment" + i)).ToArray(), signalPrincipal: Principal())), "attachment failure releases per-origin reservation too");
        worker.QueryAsync("world", Start, End, new()).GetAwaiter().GetResult();
        Equal(0, SignalCharge(worker), "attachment recovery releases exactly once");
        SignalReservationsReleased(worker);

        TaskCompletionSource byteBarrier = new(TaskCreationOptions.RunContinuationsAsynchronously);
        typeof(ActivityStore).GetField("tail", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(worker, byteBarrier.Task);
        try
        {
            Check(worker.Enqueue(new ActivityBatch(signals: Enumerable.Range(0, 16).Select(i => Signal("byte" + i)).ToArray(), signalPrincipal: Principal())), "one sixteen-record batch reaches exact 64 KiB");
            Check(!worker.Enqueue(new ActivityBatch(signals: [Signal("byte17")], signalPrincipal: Principal())), "second batch rejected on bytes, not sixteen-batch count");
        }
        finally { byteBarrier.SetResult(); }
        worker.QueryAsync("world", Start, End, new()).GetAwaiter().GetResult();
        SignalReservationsReleased(worker);

        TaskCompletionSource capacityBarrier = new(TaskCreationOptions.RunContinuationsAsynchronously);
        typeof(ActivityStore).GetField("tail", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(worker, capacityBarrier.Task);
        try
        {
            for (int i = 0; i < 128; i++)
                Check(worker.Enqueue(new ActivityBatch(signals: [Signal("principal" + i)], signalPrincipal: Principal("principal" + i))), "128 outstanding principal entries");
            Check(!worker.Enqueue(new ActivityBatch(signals: [Signal("principal129")], signalPrincipal: Principal("principal129")), out var reason) && reason == ObservationRejection.PrincipalCapacity, "129th outstanding principal fails bounded index capacity");
            Check(!worker.Enqueue(new ActivityBatch(signals: [Signal("connection129")], signalPrincipal: Principal("principal0", "replacement")), out reason) && reason == ObservationRejection.PrincipalCapacity, "retained outstanding connections bounded independently");
        }
        finally { capacityBarrier.SetResult(); }
        worker.QueryAsync("world", Start, End, new()).GetAwaiter().GetResult();
        SignalReservationsReleased(worker);
    }

    private static void SignalOutstandingAccounting(string directory)
    {
        using LiteDatabase db = new(Path.Combine(directory, "records.db"));
        using ActivityStore worker = new(db, new CombatStore(db), new PlayerActionStore(db), new CookingStore(db), _ => { });
        TaskCompletionSource barrier = new(TaskCreationOptions.RunContinuationsAsynchronously);
        typeof(ActivityStore).GetField("tail", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(worker, barrier.Task);
        ActivityBatch batch = new(signals: Enumerable.Range(0, 16).Select(i => Signal(i.ToString())).ToArray(), signalPrincipal: Principal());
        try
        {
            Check(worker.Enqueue(new ActivityBatch(signals: [Signal()], signalPrincipal: Principal())), "single signal accepted");
            Equal(4096, SignalCharge(worker), "exact 4 KiB charge");
            Check(worker.Enqueue(new ActivityBatch(signals: batch.Signals.Take(15).ToArray(), signalPrincipal: Principal())), "remaining fifteen signals reach account byte cap");
            for (int i = 1; i < 128; i++) Check(worker.Enqueue(new ActivityBatch(signals: batch.Signals.ToArray(), signalPrincipal: Principal("origin" + i))), "independent accounts accepted below global byte cap");
            Equal(8 * 1024 * 1024, SignalCharge(worker), "exact 8 MiB aggregate charge");
            Check(!worker.Enqueue(new ActivityBatch(signals: [Signal("overflow")], signalPrincipal: Principal("honest-overflow")), out var reason) && reason == ObservationRejection.QueueFull, "global cap still enforced independently");
            Equal(8 * 1024 * 1024, SignalCharge(worker), "overflow rejection reserves nothing");
            Check(worker.Enqueue(new ActivityBatch()), "signal cap does not change legacy payload admission");
        }
        finally { barrier.SetResult(); }
        worker.QueryAsync("world", Start, End, new(reportedKillCredits: true)).GetAwaiter().GetResult();
        Equal(0, SignalCharge(worker), "successful and duplicate jobs release all charges");
        Check(worker.Enqueue(batch), "capacity reusable after completion");
        worker.QueryAsync("world", Start, End, new(reportedKillCredits: true)).GetAwaiter().GetResult();
        Equal(0, SignalCharge(worker), "reused capacity released");
        TaskCompletionSource queueBarrier = new(TaskCreationOptions.RunContinuationsAsynchronously);
        typeof(ActivityStore).GetField("tail", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(worker, queueBarrier.Task);
        try
        {
            for (int i = 0; i < 255; i++) Check(worker.Enqueue(new ActivityBatch()), "255 gameplay jobs leave one existing slot for diagnostics");
            Check(!worker.Enqueue(new ActivityBatch(signals: [Signal()], signalPrincipal: Principal())), "pending batch cap rejects signals too");
            Equal(0, SignalCharge(worker), "queue rejection reserves nothing");
        }
        finally { queueBarrier.SetResult(); }
        worker.BeginDrain().GetAwaiter().GetResult();
    }

    private static void SignalFailureAndDrain(string directory)
    {
        using LiteDatabase db = new(Path.Combine(directory, "records.db"));
        using ManualResetEventSlim entered = new(), release = new();
        List<string> messages = [];
        using ActivityStore worker = new(db, new CombatStore(db), new PlayerActionStore(db), new CookingStore(db), message =>
        {
            messages.Add(message);
            entered.Set(); release.Wait();
            throw new InvalidOperationException("logger failure must not leak reservations");
        });
        Check(db.GetCollection("reported_kill_credits_v1").DropIndex("WorldId"), "replace normal world index for failure injection");
        Check(db.GetCollection("reported_kill_credits_v1").EnsureIndex("WorldId", true), "unique world failure index installed");
        ActivityBatch batch = new(signals: Enumerable.Range(0, 16).Select(i => Signal(i.ToString())).ToArray(), signalPrincipal: Principal());
        Task? drain = null;
        try
        {
            Check(worker.Enqueue(batch), "failing signal batch admitted");
            Check(entered.Wait(TimeSpan.FromSeconds(5)), "real signal insert failure reached diagnostic barrier");
            Equal(16 * 4096, SignalCharge(worker), "executing failed job charged until its finally");
            Check(!worker.Enqueue(new ActivityBatch(signals: [Signal("same-origin")], signalPrincipal: Principal())), "executing work keeps its principal reservation");
            for (int i = 1; i < 128; i++) Check(worker.Enqueue(new ActivityBatch(signals: batch.Signals.ToArray(), signalPrincipal: Principal("origin" + i))), "pending independent signal failure batches accepted");
            Equal(8 * 1024 * 1024, SignalCharge(worker), "executing plus queued charges share cap");
            Check(!worker.Enqueue(new ActivityBatch(signals: [Signal()], signalPrincipal: Principal("overflow"))), "executing charge cannot be bypassed");
            drain = worker.BeginDrain();
            Check(!drain.IsCompleted, "drain waits for executing and accepted queued signals");
            Check(!worker.Enqueue(new ActivityBatch(signals: [Signal("sealed")], signalPrincipal: Principal())), "sealed admission rejected");
            Equal(8 * 1024 * 1024, SignalCharge(worker), "sealed rejection adds no charge or premature release");
        }
        finally
        {
            release.Set();
            (drain ?? worker.BeginDrain()).GetAwaiter().GetResult();
        }
        Equal(0, SignalCharge(worker), "all failed jobs release charges during drain, even with throwing logger");
        SignalReservationsReleased(worker);
        Check(messages.Any(x => x.Contains("stage=signal") && x.Contains("signals=16")), "failed signal stage and count diagnosed");
        Equal(1, db.GetCollection("reported_kill_credits_v1").Count(), "borrowed database still open; first immutable insert retained");
        Check(!worker.Enqueue(new ActivityBatch(signals: [Signal()], signalPrincipal: Principal())), "post-drain signal rejected");
        Equal(0, SignalCharge(worker), "post-drain rejection leaves no charge");
    }

    private static void BorrowedActivityDrain(string directory)
    {
        using LiteDatabase db = new(Path.Combine(directory, "records.db"));
        using ActivityStore worker = new(db, new CombatStore(db), new PlayerActionStore(db), new CookingStore(db), _ => { });
        Check(worker.Enqueue(new ActivityBatch(activities: [Piece("accepted")])), "borrowed worker accepted batch");
        Task drain = worker.BeginDrain();
        Check(ReferenceEquals(drain, worker.BeginDrain()), "sealed tail is stable");
        Check(!worker.Enqueue(new ActivityBatch()), "sealed worker rejects batches");
        drain.GetAwaiter().GetResult();
        worker.Dispose();
        Equal(1, db.GetCollection("activity_observations").Count(), "borrowed database remains open after disposal");
    }

    private static void EarlyLegacyShutdown(string directory)
    {
        string path = PrepareDatabasePath(directory);
        for (int i = 0; i < 8; i++)
        {
            Database database = new(directory);
            database.InsertSimpleStatRecord("join", "Name", $"account-{i}");
            Task shutdown = database.BeginShutdown();
            Check(ReferenceEquals(shutdown, database.BeginShutdown()), "early shutdown task is idempotent");
            shutdown.GetAwaiter().GetResult();
            database.Dispose();
        }
        using LiteDatabase reopened = new(path);
        Equal(8, reopened.GetCollection("joins").Count(), "pre-initialization accepted rows persisted");
        Equal(8, reopened.GetCollection("player_name").Count(), "accepted name mappings persisted");
    }

    private static void ShutdownLegacyWrites(string directory)
    {
        string path = PrepareDatabasePath(directory);
        Database database = new(directory);
        ActivityStore worker = database.Activity;
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        LiteDatabase owned = (LiteDatabase)typeof(Database).GetField("db", flags)!.GetValue(database)!;
        var enqueue = (Func<Action, bool>)typeof(Database).GetMethod("QueueLegacyWrite", flags)!
            .CreateDelegate(typeof(Func<Action, bool>), database);
        using ManualResetEventSlim entered = new(), release = new();
        Task? shutdown = null;
        ArgumentException failure = Throws<ArgumentException>(() => worker.QueryAsync("world", End, Start, AllOptions()).GetAwaiter().GetResult());
        try
        {
            Check(enqueue(() =>
            {
                entered.Set();
                release.Wait();
                owned.GetCollection<SimpleStat>("shouts").Insert(new SimpleStat("barrier", "account", 0, 0, 0));
            }), "barrier-controlled real legacy write accepted");
            Check(entered.Wait(TimeSpan.FromSeconds(5)), "legacy write reached barrier");
            Check(enqueue(() => throw new InvalidOperationException("test legacy failure")), "failing legacy job accepted");
            database.InsertSimpleStatRecord("join", "Name", "account");
            database.InsertSimpleStatRecord("death", "Name", "account");
            shutdown = database.BeginShutdown();
            Check(!shutdown.Wait(TimeSpan.FromMilliseconds(100)), "a faulted report cannot close over accepted legacy work");
            Throws<ObjectDisposedException>(() => database.InsertSimpleStatRecord("join", "late", "late-account"));
            Check(!enqueue(() => throw new Exception("must never run")), "late private admission also rejected");
            Equal(0, owned.GetCollection("joins").Count(), "owned handle remains open behind legacy barrier");
        }
        finally
        {
            release.Set();
            shutdown ??= database.BeginShutdown();
            ArgumentException actual = Throws<ArgumentException>(() => shutdown.GetAwaiter().GetResult());
            Check(ReferenceEquals(failure, actual), "shutdown preserves the report exception object and type");
            Throws<ArgumentException>(database.Dispose);
        }
        using LiteDatabase reopened = new(path);
        Equal(1, reopened.GetCollection("shouts").Count(), "barrier write drained before close");
        Equal(1, reopened.GetCollection("joins").Count(), "legacy queue recovers after a failed write");
        Equal(1, reopened.GetCollection("deaths").Count(), "all accepted legacy rows persisted");
        Equal(1, reopened.GetCollection("player_name").Count(), "accepted join name mapping persisted");
    }

    private static void ShutdownLegacyReader(string directory)
    {
        string path = PrepareDatabasePath(directory);
        Database database = new(directory);
        _ = database.Activity;
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        LiteDatabase owned = (LiteDatabase)typeof(Database).GetField("db", flags)!.GetValue(database)!;
        var read = (Func<Func<int>, int>)typeof(Database).GetMethod("ReadLegacy", flags)!.MakeGenericMethod(typeof(int))
            .CreateDelegate(typeof(Func<Func<int>, int>), database);
        using ManualResetEventSlim entered = new(), release = new();
        Task<int> reader = Task.Run(() => read(() =>
        {
            entered.Set();
            release.Wait();
            // This nested query is part of the reader admitted before closing.
            return database.CountOfRecordsByName("join", "Name");
        }));
        Task? shutdown = null;
        try
        {
            Check(entered.Wait(TimeSpan.FromSeconds(5)), "reader holds actual legacy access lock");
            shutdown = database.BeginShutdown();
            Check(!shutdown.Wait(TimeSpan.FromMilliseconds(100)), "shutdown worker waits for admitted synchronous reader");
            Throws<ObjectDisposedException>(() => database.CountOfRecordsByName("join", "late"));
            Equal(0, owned.GetCollection("joins").Count(), "owned handle remains open until reader exits");
        }
        finally
        {
            release.Set();
            Equal(0, reader.GetAwaiter().GetResult(), "admitted nested query completes after admissions close");
            (shutdown ?? database.BeginShutdown()).GetAwaiter().GetResult();
            database.Dispose();
        }
        using LiteDatabase reopened = new(path);
        Equal(0, reopened.GetCollection("joins").Count(), "reader released handle for immediate reopen");
    }

    private static void NonblockingShutdown(string directory)
    {
        string path = PrepareDatabasePath(directory);
        Database database = new(directory);
        _ = database.Activity;
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        LiteDatabase owned = (LiteDatabase)typeof(Database).GetField("db", flags)!.GetValue(database)!;
        using ManualResetEventSlim entered = new(), release = new();
        int warnings = 0;
        ActivityStore worker = new(owned, database.Combat, new PlayerActionStore(owned), new CookingStore(owned), _ =>
        {
            if (Interlocked.Increment(ref warnings) == 1)
            {
                entered.Set();
                release.Wait();
            }
        });
        typeof(Database).GetField("activity", flags)!.SetValue(database, worker);
        Task? shutdown = null;
        Task<Task>? begin = null;
        Task<CombinedReportSnapshot>? report = null;
        try
        {
            Check(worker.Enqueue(new ActivityBatch(activities: [Piece("failed", Actor(new string('x', 4096)))])), "failing job accepted");
            Check(entered.Wait(TimeSpan.FromSeconds(5)), "real failed write reached diagnostic barrier");
            Check(worker.Enqueue(new ActivityBatch(activities: [Piece("accepted")])), "accepted batch behind barrier");
            report = worker.QueryAsync("world", Start, End, AllOptions());
            begin = Task.Factory.StartNew(database.BeginShutdown,
                CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default);
            Check(begin.Wait(TimeSpan.FromSeconds(2)), "shutdown admission must return while a job is blocked");
            shutdown = begin.Result;
            Check(ReferenceEquals(shutdown, database.BeginShutdown()), "one idempotent owned shutdown task");
            Check(!shutdown.IsCompleted, "owned shutdown waits for accepted work");
            Check(!worker.Enqueue(new ActivityBatch()), "late activity rejected");
            Throws<ObjectDisposedException>(() => database.InsertSimpleStatRecord("join", "late", "late-account"));
            Throws<ObjectDisposedException>(() => worker.QueryAsync("world", Start, End, AllOptions()).GetAwaiter().GetResult());
            Equal(0, owned.GetCollection("combat_participation_v1_deaths").Count(), "owned database remains open before drain");
        }
        finally
        {
            release.Set();
            if (begin != null) shutdown = begin.GetAwaiter().GetResult();
            shutdown?.GetAwaiter().GetResult();
            database.Dispose();
        }
        Equal(1L, report!.GetAwaiter().GetResult().Rows[ReportMetric.NewPieces].Single().Count, "accepted report completed before close");
        using LiteDatabase reopened = new(path);
        Equal(1, reopened.GetCollection("activity_observations").Count(), "accepted batch persisted after reopen");
    }

    private static ActivityActor Actor(string id = "account", string name = "Name") => new(id, name, "connection", Start);
    private static LeaderboardsOptions AllOptions() => new(bossKills: true, biomes: true,
        dodgeRolls: true, sailing: true, floorPickups: true, construction: true,
        exploration: true, cookingCompletions: true);

    private static ActivityRecord Piece(string id = "piece", ActivityActor? actor = null) =>
        new(ActivityKind.NewPiece, "world", actor ?? Actor(), id, id, Start, "wood_floor");

    private static void ActivityRestart(string directory)
    {
        string path = Path.Combine(directory, "records.db");
        using (LiteDatabase db = new(path))
        {
            BsonDocument sentinel = new() { ["_id"] = "legacy", ["Date"] = Start, ["Extra"] = "untouched" };
            db.GetCollection("joins").Insert(sentinel);
            PlayerActionStore participants = new(db);
            CookingStore cooking = new(db);
            using ActivityStore worker = new(db, new CombatStore(db), participants, cooking, _ => { });
            // A faulted job must not poison the serial tail.
            Throws<ArgumentException>(() => worker.QueryAsync("world", End, Start, AllOptions()).GetAwaiter().GetResult());
            Check(worker.TryClaimReportAsync("combat-report:v1:test", Start).GetAwaiter().GetResult(), "worker recovered/legacy claim");
            Check(!worker.TryClaimReportAsync("combat-report:v1:test", Start).GetAwaiter().GetResult(), "same claim collection");
            Check(new CombatStore(db).Insert(Record("legacy-only")), "legacy compatibility store still works independently");
            CheckActivityRows(worker, db);
        }
        using (LiteDatabase db = new(path))
        {
            PlayerActionStore participants = new(db);
            CombinedReportSnapshot result;
            using ActivityStore reopened = new(db, new CombatStore(db), participants, new CookingStore(db), _ => { });
            Check(reopened.Enqueue(new ActivityBatch(chunks:
                [new("world", Actor(), new ExplorationGrid(64, 3, 2), 0, 0, [new(7, Start)])], coverage:
                [new("world", ReportMetric.ExplorationCells, CoverageState.Observing, Start)])), "restart grid mismatch queued");
            Check(reopened.Enqueue(new ActivityBatch(coverage:
                [new("world", ReportMetric.ExplorationCells, CoverageState.Partial, End)])), "later stale coverage queued");
            result = reopened.QueryAsync("world", Start, End, AllOptions()).GetAwaiter().GetResult();
            Equal(CoverageState.Unavailable, result.Coverage.Single(c => c.Metric == ReportMetric.ExplorationCells).State,
                "durable grid rejection survives same-batch and later stale observing updates");
            Equal(CoverageReason.GridMismatch, reopened.ExplorationPauseReason("world")!.Value, "durable mismatch reaches capture without database access");
            Check(!reopened.ExplorationPauseReason("other-world").HasValue, "single-world notice leaves other worlds available");
            Check(reopened.Enqueue(new ActivityBatch(chunks:
                [new("world", Actor(), new ExplorationGrid(64, 1, 2), 0, 0, [new(8, Start)]),
                 new("other-world", Actor(), new ExplorationGrid(64, 1, 2), 0, 0, [new(0, Start)])])), "matching-grid replay and other-world baseline queued");
            Check(reopened.Enqueue(new ActivityBatch(chunks:
                [new("other-world", Actor(), new ExplorationGrid(64, 2, 2), 0, 0, [new(1, Start)])])), "second-world mismatch queued");
            result = reopened.QueryAsync("world", Start, End, AllOptions()).GetAwaiter().GetResult();
            Equal(2L, result.Rows[ReportMetric.ExplorationCells].Single().Count, "paused world rejects even matching durable-grid work");
            Equal(CoverageReason.GridMismatch, reopened.ExplorationPauseReason("world")!.Value, "overflow cannot lose first-world notice");
            Equal(CoverageReason.Dropped, reopened.ExplorationPauseReason("third-world")!.Value, "fixed feedback overflow pauses all worlds fail-closed");
        }
    }

    private static void CheckActivityRows(ActivityStore worker, LiteDatabase db)
    {
        ActivityRecord pickup = new(ActivityKind.InferredPickup, "world", Actor(), "item", "mutation", Start,
            "Wood", 2, provenance: ItemProvenance.ProvenFreshSource, sourceToken: "source", sourceUnits: 3);
        ActivityRecord piece = new(ActivityKind.NewPiece, "world", Actor(), "piece", "creation", Start,
            "wood_wall");
        ActivityRecord sailing = new(ActivityKind.Sailing, "world", Actor(), "ship", "session:sequence", Start.AddSeconds(1),
            startUtc: Start.AddSeconds(-1), seconds: 2, meters: 20);
        ExplorationGrid grid = new(64, 1, 2);
        ExplorationChunk chunk = new("world", Actor(), grid, 0, 0, [new(0, Start), new(1, End)]);
        Check(worker.Enqueue(new ActivityBatch(activities: [pickup, piece, sailing], chunks: [chunk], coverage:
            [new("world", ReportMetric.InferredPickups, CoverageState.Partial, Start, CoverageReason.Dropped, long.MaxValue),
             new("world", ReportMetric.InferredPickups, CoverageState.Partial, Start, CoverageReason.Dropped)])), "activity batch");
        Check(worker.Enqueue(new ActivityBatch(activities: [pickup, piece, sailing], chunks:
            [new("world", Actor(), grid, 0, 0, [new(0, Start.AddTicks(1)), new(2, Start)])])), "replay/merge");
        CombinedReportSnapshot result = worker.QueryAsync("world", Start, End, AllOptions()).GetAwaiter().GetResult();
        ReportRow row = result.Rows[ReportMetric.InferredPickups].Single();
        Equal(2L, row.Count, "pickup replay dedup");
        Equal(2L, row.ProvenFreshCount, "fresh subtotal, not another section");
        Equal(1L, result.Rows[ReportMetric.NewPieces].Single().Count, "piece lifecycle dedup");
        Equal(10.0, result.Rows[ReportMetric.SailingDistance].Single().Meters, "overlapping sailing distance clipped");
        Equal(2L, result.Rows[ReportMetric.ExplorationCells].Single().Count, "first cells only, end exclusive");
        Equal(long.MaxValue, result.Coverage.Single().Counters[CoverageReason.Dropped], "coverage saturation");
        Equal(1, db.GetCollection("server_observation_v1_coverage").Count(), "one bounded coverage document");
        Equal(1L, db.GetCollection("activity_item_sources").FindAll().Single()["Remaining"].AsInt64, "source decremented only once");

        Check(worker.Enqueue(new ActivityBatch(activities: [new(ActivityKind.InferredPickup, "world", Actor(), "item", "excess", Start,
            "Wood", 2, provenance: ItemProvenance.ProvenFreshSource, sourceToken: "source", sourceUnits: 3)])), "enqueue failed source transaction");
        result = worker.QueryAsync("world", Start, End, AllOptions()).GetAwaiter().GetResult();
        Equal(2L, result.Rows[ReportMetric.InferredPickups].Single().Count, "source failure creates no pickup");
        Equal(1L, db.GetCollection("activity_item_sources").FindAll().Single()["Remaining"].AsInt64, "source failure rolled back");
        Check(worker.Enqueue(new ActivityBatch(chunks: [new("world", Actor(), new ExplorationGrid(64, 2, 2), 0, 0, [new(5, Start)])])), "mismatched grid queued");
        result = worker.QueryAsync("world", Start, End, AllOptions()).GetAwaiter().GetResult();
        Equal(2L, result.Rows[ReportMetric.ExplorationCells].Single().Count, "mismatched grid does not recount");
        result = worker.QueryAsync("world", Start, Start.AddTicks(1), AllOptions()).GetAwaiter().GetResult();
        Equal(2L, result.Rows[ReportMetric.ExplorationCells].Single().Count, "revisit never rewrites first timestamp");
    }
}
