using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using LiteDB;

namespace DiscordConnector.Records;

internal sealed class ActivityStore : IDisposable, IObservationLossSource
{
    internal static readonly string[] CollectionNames =
    {
        "combat_participation_v1_deaths", "combat_participation_v1_contributors", "combat_direct_damage_v1", "player_actions_v1",
        "activity_observations", "activity_item_sources", "activity_exploration_grids", "activity_exploration_chunks",
        "server_observation_v1_coverage", "cooking_slots_v1", "cooking_completions_v1",
        "reported_kill_credits_v1", "observed_consumable_uses_v1", "server_observation_v2_loss", "reported_dodge_hit_notifications_v1"
    };
    private readonly LiteDatabase db;
    private readonly CombatStore legacyCombat;
    private readonly PlayerActionStore actions;
    private readonly CookingStore cooking;
    private readonly ReportedSignalStore signals;
    private readonly Action<string> warn;
    private readonly DatabaseDiagnostics diagnostics;
    private readonly ILiteCollection<BsonDocument> observations, sources, grids, chunks, coverage, loss;
    internal ObservationLoss Loss { get; }
    ObservationLoss IObservationLossSource.Loss => Loss;
    private readonly object gate = new();
    private Task tail = Task.CompletedTask;
    private int pendingBatches, pendingReports;
    private bool pendingDiagnostic;
    private bool pendingSignalRetention;
    private double? lastSignalRetentionAttempt;
    private const int SignalChargeBytes = 4096, MaxOutstandingSignalBytes = 8 * 1024 * 1024;
    private int outstandingSignalBytes;
    private readonly Dictionary<string, (int Batches, int Bytes)> signalAccounts = new(StringComparer.Ordinal), signalConnections = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Account, string Connection), int> signalPrincipals = new();
    private int localSignalBatches, localSignalBytes;
    private bool disposed;
    private string? explorationPausedWorld;
    private bool explorationFeedbackOverflow;
    internal long CookingObservationEpoch => cooking.ObservationEpoch;
    internal (int Batches, int Reports) PendingWork { get { lock (gate) return (pendingBatches, pendingReports); } }

    // One sticky world notice; a second world overflows fail-closed, never overwriting the first notice.
    internal CoverageReason? ExplorationPauseReason(string world)
    {
        lock (gate)
            return explorationPausedWorld == world ? CoverageReason.GridMismatch :
                explorationFeedbackOverflow ? CoverageReason.Dropped : null;
    }

    private void PauseExploration(string world)
    {
        bool overflow = false;
        lock (gate)
        {
            if (explorationPausedWorld == null) explorationPausedWorld = world;
            else if (explorationPausedWorld != world && !explorationFeedbackOverflow)
                overflow = explorationFeedbackOverflow = true;
        }
        if (overflow) Warn("Exploration feedback capacity exceeded; all exploration paused until worker restart.");
    }

    internal ActivityStore(LiteDatabase db, CombatStore legacyCombat, PlayerActionStore actions, CookingStore cooking, Action<string> warn,
        DatabaseDiagnostics? diagnostics = null, ObservationLoss? observationLoss = null)
    {
        this.db = db ?? throw new ArgumentNullException(nameof(db));
        this.legacyCombat = legacyCombat ?? throw new ArgumentNullException(nameof(legacyCombat));
        this.actions = actions ?? throw new ArgumentNullException(nameof(actions));
        this.cooking = cooking ?? throw new ArgumentNullException(nameof(cooking));
        this.warn = warn ?? throw new ArgumentNullException(nameof(warn));
        this.diagnostics = diagnostics ?? new DatabaseDiagnostics(warn);
        Loss = observationLoss ?? ObservationLoss.Process;
        if (observationLoss == null) Loss.SetWarning(warn);
        signals = new ReportedSignalStore(db);
        observations = db.GetCollection("activity_observations"); sources = db.GetCollection("activity_item_sources");
        grids = db.GetCollection("activity_exploration_grids"); chunks = db.GetCollection("activity_exploration_chunks");
        coverage = db.GetCollection("server_observation_v1_coverage");
        loss = db.GetCollection("server_observation_v2_loss"); loss.EnsureIndex("WorldId");
        ObservationValues.Index(observations); observations.EnsureIndex("StartUtc"); observations.EnsureIndex("Kind");
        ObservationValues.Index(chunks); chunks.EnsureIndex("MinUtc"); chunks.EnsureIndex("MaxUtc");
        grids.EnsureIndex("WorldId"); coverage.EnsureIndex("WorldId"); sources.EnsureIndex("WorldId");
    }

    internal bool Enqueue(ActivityBatch batch) => Enqueue(batch, out _);

    internal bool Enqueue(ActivityBatch batch, out ObservationRejection reason)
    {
        if (batch == null) throw new ArgumentNullException(nameof(batch));
        lock (gate)
        {
            int signalBytes = batch.Signals.Count * SignalChargeBytes;
            reason = ObservationRejection.QueueFull;
            if (disposed || pendingBatches >= (pendingDiagnostic ? 256 : 255) || batch.PhysicalDocumentCount > 128
                || signalBytes > MaxOutstandingSignalBytes - outstandingSignalBytes)
            { Loss.Batch(batch, reason); return false; }
            ObservationPrincipal? principal = signalBytes == 0 ? null : batch.SignalPrincipal;
            if (signalBytes != 0 && !ReserveSignal(principal!, signalBytes, out reason))
            { Loss.Batch(batch, reason); return false; }
            pendingBatches++;
            outstandingSignalBytes += signalBytes;
            try
            {
                tail = tail.ContinueWith(_ =>
                {
                    try { Write(batch); }
                    catch
                    {
                        // Batch completion is unknown/partial; this is not proof that its records are absent.
                        Loss.Batch(batch, null);
                        if (batch.Cooking.Count != 0) cooking.InvalidatePending();
                    }
                    finally { lock (gate) { pendingBatches--; outstandingSignalBytes -= signalBytes; ReleaseSignal(principal, signalBytes); } }
                }, TaskScheduler.Default);
            }
            catch { pendingBatches--; outstandingSignalBytes -= signalBytes; ReleaseSignal(principal, signalBytes); Loss.Batch(batch, ObservationRejection.QueueFull); throw; }
            reason = ObservationRejection.None;
            return true;
        }
    }

    internal Task MaintainLoss(double monotonicSeconds) => Loss.Maintain(monotonicSeconds, ReconcileLossAsync);
    internal Task SubmitLossBeforeSeal(double monotonicSeconds) => Loss.Maintain(monotonicSeconds, ReconcileLossAsync, final: true);

    internal Task MaintainSignalRetention(DateTime nowUtc, double monotonic)
    {
        ObservationValues.Utc(nowUtc);
        lock (gate)
        {
            if (disposed || pendingSignalRetention || double.IsNaN(monotonic) || double.IsInfinity(monotonic) || monotonic < 0 ||
                lastSignalRetentionAttempt.HasValue && monotonic - lastSignalRetentionAttempt.Value < 60)
                return Task.CompletedTask;
            // Full queues also consume the opportunity, avoiding per-frame admission retries.
            lastSignalRetentionAttempt = monotonic;
            if (pendingBatches >= (pendingDiagnostic ? 256 : 255)) return Task.CompletedTask;
            pendingSignalRetention = true; pendingBatches++;
            try
            {
                Task result = tail.ContinueWith(_ =>
                {
                    try { signals.PruneExpired(nowUtc); }
                    catch { diagnostics.SignalRetentionFailure(); throw; }
                    finally { lock (gate) { pendingBatches--; pendingSignalRetention = false; } }
                }, TaskScheduler.Default);
                _ = result.ContinueWith(failed => { _ = failed.Exception; }, System.Threading.CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                tail = result;
                return result;
            }
            catch { pendingBatches--; pendingSignalRetention = false; throw; }
        }
    }

    internal Task ReconcileLossAsync(ObservationLossSnapshot snapshot)
    {
        if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
        lock (gate)
        {
            if (disposed || pendingDiagnostic || pendingBatches >= 256)
                return Task.FromException(new InvalidOperationException("Diagnostic admission unavailable."));
            pendingDiagnostic = true; pendingBatches++;
            try
            {
                Task result = tail.ContinueWith(_ =>
                {
                    try { ReconcileLoss(snapshot); }
                    catch (Exception exception) { Diagnose(DatabaseStage.Loss, exception); throw; }
                    finally { lock (gate) { pendingBatches--; pendingDiagnostic = false; } }
                }, TaskScheduler.Default);
                _ = result.ContinueWith(failed => { _ = failed.Exception; }, System.Threading.CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                tail = result;
                return result;
            }
            catch { pendingBatches--; pendingDiagnostic = false; throw; }
        }
    }

    private void ReconcileLoss(ObservationLossSnapshot snapshot)
    {
        string id = ObservationValues.Key(snapshot.WorldId, snapshot.Metric.ToString(), snapshot.ProcessEpoch);
        BsonDocument document = loss.FindById(id) ?? new BsonDocument
        {
            ["_id"] = id, ["WorldId"] = snapshot.WorldId, ["Metric"] = snapshot.Metric.ToString(),
            ["ProcessEpoch"] = snapshot.ProcessEpoch, ["StartedUtc"] = snapshot.ObservedUtc.Ticks,
            ["ObservedUtc"] = snapshot.ObservedUtc.Ticks, ["UncertainWrites"] = 0L, ["Rejections"] = new BsonDocument()
        };
        BsonDocument totals = document["Rejections"].AsDocument;
        foreach (var pair in snapshot.Rejections)
            totals[pair.Key.ToString()] = Math.Max(totals[pair.Key.ToString()].IsNull ? 0 : totals[pair.Key.ToString()].AsInt64, pair.Value);
        document["UncertainWrites"] = Math.Max(document["UncertainWrites"].AsInt64, snapshot.UncertainWrites);
        document["ObservedUtc"] = Math.Max(document["ObservedUtc"].AsInt64, snapshot.ObservedUtc.Ticks);
        loss.Upsert(document);
    }

    private bool ReserveSignal(ObservationPrincipal principal, int bytes, out ObservationRejection reason)
    {
        reason = ObservationRejection.PrincipalQuota;
        if (principal.ServerLocal)
        {
            if (localSignalBatches >= 16 || bytes > 65536 - localSignalBytes) return false;
            localSignalBatches++; localSignalBytes += bytes; return true;
        }
        signalAccounts.TryGetValue(principal.AccountId, out var account);
        signalConnections.TryGetValue(principal.ConnectionToken, out var connection);
        var key = (principal.AccountId, principal.ConnectionToken);
        signalPrincipals.TryGetValue(key, out int retained);
        if (account.Batches >= 16 || connection.Batches >= 16 || bytes > 65536 - account.Bytes || bytes > 65536 - connection.Bytes) return false;
        if (retained == 0 && signalPrincipals.Count >= 128)
        { reason = ObservationRejection.PrincipalCapacity; return false; }
        signalAccounts[principal.AccountId] = (account.Batches + 1, account.Bytes + bytes);
        signalConnections[principal.ConnectionToken] = (connection.Batches + 1, connection.Bytes + bytes);
        signalPrincipals[key] = retained + 1;
        return true;
    }

    private void ReleaseSignal(ObservationPrincipal? principal, int bytes)
    {
        if (principal == null) return;
        if (principal.ServerLocal) { localSignalBatches--; localSignalBytes -= bytes; return; }
        Release(signalAccounts, principal.AccountId, bytes); Release(signalConnections, principal.ConnectionToken, bytes);
        var key = (principal.AccountId, principal.ConnectionToken);
        if (signalPrincipals[key] == 1) signalPrincipals.Remove(key); else signalPrincipals[key]--;
    }

    private static void Release(Dictionary<string, (int Batches, int Bytes)> index, string key, int bytes)
    {
        var previous = index[key];
        if (previous.Batches == 1) index.Remove(key);
        else index[key] = (previous.Batches - 1, previous.Bytes - bytes);
    }

    internal Task<CombinedReportSnapshot> QueryAsync(string world, DateTime startUtc, DateTime endUtc, LeaderboardsOptions options) =>
        ReportJob(DatabaseStage.Query, () => Query(world, startUtc, endUtc, options));

    internal Task<bool> TryClaimReportAsync(string id, DateTime nowUtc) => ReportJob(DatabaseStage.Claim, () => legacyCombat.TryClaimReport(id, nowUtc));

    private Task<T> ReportJob<T>(DatabaseStage stage, Func<T> action)
    {
        ObjectDisposedException rejection;
        lock (gate)
        {
            if (disposed) rejection = new ObjectDisposedException(nameof(ActivityStore));
            else
            {
                if (pendingReports >= 2) return Task.FromException<T>(new InvalidOperationException("At most two report jobs may be pending."));
                pendingReports++;
                Task<T> result = tail.ContinueWith(_ =>
                {
                    try { return action(); }
                    catch (Exception exception) { Diagnose(stage, exception); throw; }
                    finally { lock (gate) pendingReports--; }
                }, TaskScheduler.Default);
                _ = result.ContinueWith(failed => { _ = failed.Exception; },
                    System.Threading.CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                tail = result;
                return result;
            }
        }
        Diagnose(stage, rejection);
        return Task.FromException<T>(rejection);
    }

    internal Task BeginDrain()
    {
        lock (gate) { disposed = true; return tail; }
    }

    public void Dispose()
    {
        try { BeginDrain().GetAwaiter().GetResult(); }
        catch (Exception exception) { Diagnose(DatabaseStage.Drain, exception); }
    }

    private void Warn(string message)
    {
        try { warn(message); }
        catch { /* Logging must not poison the worker or prevent database shutdown. */ }
    }

    private void Write(ActivityBatch batch)
    {
        DatabaseStage stage = DatabaseStage.Activities;
        try
        {
            foreach (ActivityRecord record in batch.Activities) InsertActivity(record);
            stage = DatabaseStage.Chunks;
            foreach (ExplorationChunk chunk in batch.Chunks) MergeChunk(chunk);
            stage = DatabaseStage.Action;
            foreach (PlayerActionRecord action in batch.Actions) actions.InsertAction(action);
            stage = DatabaseStage.Cooking;
            // Slot generations must retain capture order, including within the same batch.
            foreach (CookingChange change in batch.Cooking) cooking.Apply(change);
            stage = DatabaseStage.Signal;
            foreach (ReportedSignalRecord signal in batch.Signals) signals.Insert(signal);
            stage = DatabaseStage.Coverage;
            foreach (IGrouping<string, CoverageUpdate> group in batch.Coverage.GroupBy(x => ObservationValues.Key(x.WorldId, x.Metric.ToString())))
                UpdateCoverage(group.Key, group);
        }
        catch (Exception exception) { Diagnose(stage, exception, batch); throw; }
    }

    private void Diagnose(DatabaseStage stage, Exception exception, ActivityBatch? batch = null, bool rollback = false)
    {
        int batches, reports; bool closing;
        lock (gate) { batches = pendingBatches; reports = pendingReports; closing = disposed; }
        diagnostics.Report(stage, exception, rollback, batch, batches, reports, closing);
    }

    private void InsertActivity(ActivityRecord record)
    {
        string id = record.Kind switch
        {
            ActivityKind.NewPiece => ObservationValues.Key(record.WorldId, "piece", record.SubjectId),
            ActivityKind.CropPlanted => ObservationValues.Key(record.WorldId, "crop", record.SubjectId),
            ActivityKind.InferredPickup => ObservationValues.Key(record.WorldId, "pickup", record.SubjectId, record.Token),
            ActivityKind.HealthLost => ObservationValues.Key(record.WorldId, "health-lost", record.Token),
            ActivityKind.BiomeVisited => ObservationValues.Key(record.WorldId, "biome-visit", record.Actor.PlayerId, record.Prefab, record.Token),
            ActivityKind.BossCredit => ObservationValues.Key(record.WorldId, "boss-key", record.Actor.PlayerId, record.Prefab, record.Token),
            ActivityKind.CreatureKill => ObservationValues.Key(record.WorldId, "simulation-kill", record.SubjectId, record.Actor.PlayerId),
            _ => ObservationValues.Key(record.WorldId, record.Kind == ActivityKind.Sailing ? "sailing" : record.Kind.ToString(), record.Token)
        };
        if (observations.Exists(BsonExpression.Create("_id = @0", id))) return;
        // Keep one boss cohort per key/60s across restart too; a cohort's actors share its token.
        if (record.Kind == ActivityKind.BossCredit && observations.Find(BsonExpression.Create(
            "WorldId = @0 AND Kind = @1 AND Prefab = @2 AND TimestampUtc > @3 AND TimestampUtc < @4",
            record.WorldId, ActivityKind.BossCredit.ToString(), record.Prefab,
            record.TimestampUtc.AddSeconds(-60).Ticks, record.TimestampUtc.AddSeconds(60).Ticks)).Any(d =>
                d["WorldId"].AsString == record.WorldId && d["Prefab"].AsString == record.Prefab && d["Token"].AsString != record.Token)) return;
        BsonDocument document = ObservationValues.Document(id, record.WorldId, record.TimestampUtc, record.Actor);
        document["Kind"] = record.Kind.ToString(); document["SubjectId"] = record.SubjectId; document["Token"] = record.Token;
        document["StartUtc"] = record.StartUtc.Ticks; document["Prefab"] = record.Prefab;
        document["Meters"] = record.Meters;
        if (record.Kind is ActivityKind.HealthLost or ActivityKind.CreatureDamage) document["HpLoss"] = record.HpLoss;
        document["Units"] = record.Units; document["Provenance"] = record.Provenance.ToString();
        document["SourceToken"] = record.SourceToken;
        document["FreshUnits"] = 0L;
        if (record.Kind == ActivityKind.CropPlanted)
        { PlayerActionStore.InsertUnique(observations, document); return; }
        if (!db.BeginTrans()) throw new InvalidOperationException("Nested store transactions are not supported.");
        try
        {
            if (record.Kind == ActivityKind.InferredPickup)
            {
                string sourceId = ObservationValues.Key(record.WorldId, record.SubjectId);
                BsonDocument? source = sources.FindById(sourceId);
                if (record.Provenance == ItemProvenance.ProvenFreshSource)
                {
                    if (source == null) source = new BsonDocument
                    {
                        ["_id"] = sourceId, ["WorldId"] = record.WorldId, ["SubjectId"] = record.SubjectId,
                        ["SourceToken"] = record.SourceToken, ["Remaining"] = record.SourceUnits, ["Tainted"] = false
                    };
                    if (source["Tainted"].AsBoolean || source["SourceToken"].AsString != record.SourceToken || source["Remaining"].AsInt64 < record.Units)
                        throw new InvalidOperationException("Fresh source lineage or remaining quantity mismatch.");
                    source["Remaining"] = source["Remaining"].AsInt64 - record.Units;
                    document["FreshUnits"] = record.Units;
                }
                else if (source != null) { source["Tainted"] = true; source["Remaining"] = 0L; }
                if (source != null) sources.Upsert(source);
            }
            observations.Insert(document); db.Commit();
        }
        catch { DatabaseDiagnostics.Rollback(() => db.Rollback(), e => Diagnose(DatabaseStage.Activities, e, rollback: true)); throw; }
    }

    private void MergeChunk(ExplorationChunk input)
    {
        if (ExplorationPauseReason(input.WorldId).HasValue)
        {
            UpdateCoverage(ObservationValues.Key(input.WorldId, ReportMetric.ExplorationCells.ToString()),
                new[] { new CoverageUpdate(input.WorldId, ReportMetric.ExplorationCells, CoverageState.Unavailable,
                    input.Cells.Min(x => x.FirstObservedUtc)) });
            return;
        }
        string gridId = ObservationValues.Key(input.WorldId);
        BsonDocument? grid = grids.FindById(gridId);
        if (grid != null && grid["GridId"].AsString != input.Grid.Id)
        {
            PauseExploration(input.WorldId);
            // A mismatch never creates a new grid namespace or recounts cells.
            string coverageId = ObservationValues.Key(input.WorldId, ReportMetric.ExplorationCells.ToString());
            BsonDocument? previous = coverage.FindById(coverageId);
            if (previous == null || previous["Counters"].AsDocument[CoverageReason.GridMismatch.ToString()].AsInt64 == 0)
                Warn("Exploration grid mismatch.");
            UpdateCoverage(coverageId, new[] { new CoverageUpdate(input.WorldId, ReportMetric.ExplorationCells,
                CoverageState.Unavailable, input.Cells.Min(x => x.FirstObservedUtc), CoverageReason.GridMismatch) });
            return;
        }
        string id = ObservationValues.Key(input.WorldId, input.Actor.PlayerId, input.Grid.Id, ObservationValues.Number(input.X), ObservationValues.Number(input.Y));
        if (!db.BeginTrans()) throw new InvalidOperationException("Nested store transactions are not supported.");
        try
        {
            if (grid == null) grids.Insert(new BsonDocument
            {
                ["_id"] = gridId, ["WorldId"] = input.WorldId, ["GridId"] = input.Grid.Id,
                ["TextureSize"] = input.Grid.TextureSize, ["PixelSize"] = input.Grid.PixelSize, ["ExploreRadius"] = input.Grid.ExploreRadius,
                ["StartedUtc"] = input.Cells.Min(x => x.FirstObservedUtc.Ticks)
            });
            BsonDocument? existing = chunks.FindById(id);
            Dictionary<int, long> cells = existing == null ? new Dictionary<int, long>() : Unpack(existing["FirstTimes"].AsBinary);
            foreach (ExplorationCell cell in input.Cells)
                if (!cells.ContainsKey(cell.Index)) cells.Add(cell.Index, cell.FirstObservedUtc.Ticks);
            byte[] bitmap = new byte[128];
            foreach (int index in cells.Keys) bitmap[index / 8] |= (byte)(1 << (index % 8));
            BsonDocument document = ObservationValues.Document(id, input.WorldId, input.Cells.Max(x => x.FirstObservedUtc), input.Actor);
            if (existing != null && existing["NameObservedUtc"].AsInt64 > input.Actor.NameObservedUtc.Ticks)
            {
                document["PlayerName"] = existing["PlayerName"]; document["NameObservedUtc"] = existing["NameObservedUtc"];
            }
            document["GridId"] = input.Grid.Id; document["X"] = input.X; document["Y"] = input.Y;
            document["Bitmap"] = bitmap; document["FirstTimes"] = Pack(cells);
            document["MinUtc"] = cells.Values.Min(); document["MaxUtc"] = cells.Values.Max();
            chunks.Upsert(document); db.Commit();
        }
        catch { DatabaseDiagnostics.Rollback(() => db.Rollback(), e => Diagnose(DatabaseStage.Chunks, e, rollback: true)); throw; }
    }

    private static byte[] Pack(Dictionary<int, long> cells)
    {
        byte[] result = new byte[cells.Count * 10]; int offset = 0;
        foreach (KeyValuePair<int, long> cell in cells.OrderBy(x => x.Key))
        {
            result[offset] = (byte)cell.Key; result[offset + 1] = (byte)(cell.Key >> 8);
            for (int i = 0; i < 8; i++) result[offset + 2 + i] = (byte)(cell.Value >> (8 * i));
            offset += 10;
        }
        return result;
    }

    private static Dictionary<int, long> Unpack(byte[] bytes)
    {
        if (bytes.Length > 10240 || bytes.Length % 10 != 0) throw new InvalidOperationException("Invalid exploration chunk.");
        Dictionary<int, long> cells = new();
        for (int offset = 0; offset < bytes.Length; offset += 10)
        {
            int index = bytes[offset] | bytes[offset + 1] << 8;
            long ticks = 0;
            for (int i = 0; i < 8; i++) ticks |= (long)bytes[offset + 2 + i] << (8 * i);
            if (index >= 1024 || ticks <= 0 || ticks > DateTime.MaxValue.Ticks) throw new InvalidOperationException("Invalid exploration cell.");
            cells.Add(index, ticks);
        }
        return cells;
    }

    private void UpdateCoverage(string id, IEnumerable<CoverageUpdate> updates)
    {
        CoverageUpdate first = updates.First();
        BsonDocument document = coverage.FindById(id) ?? new BsonDocument
        {
            ["_id"] = id, ["WorldId"] = first.WorldId, ["Metric"] = first.Metric.ToString(),
            ["StartedUtc"] = first.TimestampUtc.Ticks, ["State"] = first.State.ToString(), ["LastTransitionUtc"] = first.TimestampUtc.Ticks,
            ["Counters"] = new BsonDocument(Enum.GetValues(typeof(CoverageReason)).Cast<CoverageReason>()
                .ToDictionary(x => x.ToString(), _ => new BsonValue(0L)))
        };
        foreach (CoverageUpdate update in updates)
        {
            CoverageState state = update.State;
            if (update.Metric == ReportMetric.ExplorationCells && ExplorationPauseReason(update.WorldId).HasValue &&
                (state == CoverageState.Observing || state == CoverageState.Partial)) state = CoverageState.Unavailable;
            if (document["State"].AsString != state.ToString())
            { document["State"] = state.ToString(); document["LastTransitionUtc"] = update.TimestampUtc.Ticks; }
            if (update.Reason.HasValue)
            {
                BsonDocument counters = document["Counters"].AsDocument;
                string reason = update.Reason.Value.ToString();
                counters[reason] = ObservationValues.Add(counters[reason].AsInt64, update.Count);
            }
        }
        coverage.Upsert(document);
    }

    private CombinedReportSnapshot Query(string world, DateTime startUtc, DateTime endUtc, LeaderboardsOptions options)
    {
        ObservationValues.Range(world, startUtc, endUtc);
        if (options == null) throw new ArgumentNullException(nameof(options));
        Dictionary<ReportMetric, IReadOnlyList<ReportRow>> rows = actions.Query(world, startUtc, endUtc, options).Rows.ToDictionary(x => x.Key, x => x.Value);
        foreach (KeyValuePair<ReportMetric, IReadOnlyList<ReportRow>> pair in signals.Query(world, startUtc, endUtc, options,
            options.ReportedKillCredits ? ObservationValues.Period(observations, world, startUtc, endUtc, "PlayerKey")
                .Where(x => x["Kind"].AsString == ActivityKind.CreatureKill.ToString()) : null).Rows) rows[pair.Key] = pair.Value;
        if (options.CookingCompletions)
            foreach (KeyValuePair<ReportMetric, IReadOnlyList<ReportRow>> pair in cooking.Query(world, startUtc, endUtc).Rows) rows[pair.Key] = pair.Value;
        if (options.FloorPickups) ObservationValues.Put(rows, ReportMetric.InferredPickups, ObservationValues.Aggregate(
            ObservationValues.Period(observations, world, startUtc, endUtc, "PlayerKey").Where(x => x["Kind"].AsString == ActivityKind.InferredPickup.ToString()), countField: "Units", freshField: "FreshUnits"));
        if (options.Construction)
        {
            IEnumerable<BsonDocument> Pieces() => ObservationValues.Period(observations, world, startUtc, endUtc, "PlayerKey")
                .Where(x => x["Kind"].AsString == ActivityKind.NewPiece.ToString());
            ObservationValues.Put(rows, ReportMetric.NewPieces, ObservationValues.Aggregate(Pieces()));
        }
        if (options.Sailing) ObservationValues.Put(rows, ReportMetric.SailingDistance, ObservationValues.Aggregate(Sailing(world, startUtc, endUtc), metersField: "Meters"));
        if (options.CropsPlanted) ObservationValues.Put(rows, ReportMetric.CropsPlanted, ObservationValues.Aggregate(
            ObservationValues.Period(observations, world, startUtc, endUtc, "PlayerKey").Where(x => x["Kind"].AsString == ActivityKind.CropPlanted.ToString())));
        if (options.HealthLost) ObservationValues.Put(rows, ReportMetric.HealthLost, ObservationValues.Aggregate(
            ObservationValues.Period(observations, world, startUtc, endUtc, "PlayerKey").Where(x => x["Kind"].AsString == ActivityKind.HealthLost.ToString()), hpField: "HpLoss"));
        if (options.DamageDealt) ObservationValues.Put(rows, ReportMetric.DamageDealt, ObservationValues.Aggregate(
            ObservationValues.Period(observations, world, startUtc, endUtc, "PlayerKey").Where(x => x["Kind"].AsString == ActivityKind.CreatureDamage.ToString()), hpField: "HpLoss"));
        if (options.LongestBowHit) ObservationValues.Put(rows, ReportMetric.LongestBowHit, ObservationValues.Aggregate(
            ObservationValues.Period(observations, world, startUtc, endUtc, "PlayerKey").Where(x => x["Kind"].AsString == ActivityKind.BowHit.ToString()), metersField: "Meters", maximumMeters: true));
        if (options.Exploration) ObservationValues.Put(rows, ReportMetric.ExplorationCells, ObservationValues.Aggregate(Explored(world, startUtc, endUtc), countField: "Units"));
        if (options.BossKills) ObservationValues.Put(rows, ReportMetric.ParticipantBossCredits, ObservationValues.Aggregate(
            ObservationValues.Period(observations, world, startUtc, endUtc, "PlayerKey").Where(x => x["Kind"].AsString == ActivityKind.BossCredit.ToString())));
        if (options.Biomes) ObservationValues.Put(rows, ReportMetric.Biomes, ObservationValues.Aggregate(VisitedBiomes(world, startUtc, endUtc)));
        if (options.OnFoot) ObservationValues.Put(rows, ReportMetric.OnFootDistance, ObservationValues.Aggregate(
            Intervals(world, startUtc, endUtc, ActivityKind.OnFoot), metersField: "Meters"));
        if (options.OnlineTime) ObservationValues.Put(rows, ReportMetric.OnlineTime, ObservationValues.Aggregate(
            Intervals(world, startUtc, endUtc, ActivityKind.OnlineTime), secondsField: "Seconds"));
        if (options.BiomeTime) ObservationValues.Put(rows, ReportMetric.BiomeTime, BiomeTimes(world, startUtc, endUtc));
        if (options.PortalTrips) ObservationValues.Put(rows, ReportMetric.PortalTrips, ObservationValues.Aggregate(
            ObservationValues.Period(observations, world, startUtc, endUtc, "PlayerKey").Where(x => x["Kind"].AsString == ActivityKind.PortalTrip.ToString())));
        List<CoverageSnapshot> notes = new();
        Dictionary<ReportMetric, (Dictionary<ObservationRejection, long> Reasons, long Uncertain, DateTime Started, DateTime Observed)> totals = new();
        foreach (BsonDocument entry in loss.Find(BsonExpression.Create("WorldId = @0", world)))
        {
            if (entry["WorldId"].AsString != world) continue;
            if (!Enum.TryParse(entry["Metric"].AsString, out ReportMetric metric) || !Enum.IsDefined(typeof(ReportMetric), metric)) continue;
            if (!options.Includes(metric)) continue;
            if (!totals.TryGetValue(metric, out var total)) total = (ObservationLoss.Reasons.ToDictionary(x => x, _ => 0L), 0, DateTime.MaxValue, DateTime.MinValue);
            foreach (var reason in ObservationLoss.Reasons)
                total.Reasons[reason] = ObservationValues.Add(total.Reasons[reason], entry["Rejections"].AsDocument[reason.ToString()].AsInt64);
            total.Uncertain = ObservationValues.Add(total.Uncertain, entry["UncertainWrites"].AsInt64);
            DateTime started = ObservationValues.Date(entry["StartedUtc"]), observed = ObservationValues.Date(entry["ObservedUtc"]);
            if (started < total.Started) total.Started = started;
            if (observed > total.Observed) total.Observed = observed;
            totals[metric] = total;
        }
        foreach (ReportMetric metric in Enum.GetValues(typeof(ReportMetric)))
        {
            if (!options.Includes(metric)) continue;
            BsonDocument? document = coverage.FindById(ObservationValues.Key(world, metric.ToString()));
            bool diagnostic = totals.TryGetValue(metric, out var total);
            if (document == null && !diagnostic) continue;
            CoverageState state = document == null ? CoverageState.Partial : (CoverageState)Enum.Parse(typeof(CoverageState), document["State"].AsString);
            if (diagnostic && state == CoverageState.Observing && (total.Uncertain > 0 || total.Reasons.Values.Any(x => x > 0))) state = CoverageState.Partial;
            notes.Add(new CoverageSnapshot(metric, state,
                document == null ? total.Started : ObservationValues.Date(document["StartedUtc"]),
                document == null ? total.Observed : ObservationValues.Date(document["LastTransitionUtc"]),
                document == null ? new Dictionary<CoverageReason, long>() : document["Counters"].AsDocument.ToDictionary(
                    x => (CoverageReason)Enum.Parse(typeof(CoverageReason), x.Key), x => x.Value.AsInt64),
                diagnostic ? total.Reasons : null, diagnostic ? total.Uncertain : 0));
        }
        return new CombinedReportSnapshot(world, rows, notes);
    }

    private IEnumerable<BsonDocument> VisitedBiomes(string world, DateTime start, DateTime end)
    {
        string? player = null;
        var biomes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var document in ObservationValues.Period(observations, world, start, end, "PlayerKey")
            .Where(x => x["Kind"].AsString == ActivityKind.BiomeVisited.ToString()))
        {
            if (player != document["PlayerKey"].AsString) { player = document["PlayerKey"].AsString; biomes.Clear(); }
            if (biomes.Add(document["Prefab"].AsString)) yield return document;
        }
    }

    private IEnumerable<BsonDocument> Sailing(string world, DateTime start, DateTime end)
        => Intervals(world, start, end, ActivityKind.Sailing);

    private IEnumerable<BsonDocument> Intervals(string world, DateTime start, DateTime end, ActivityKind kind)
    {
        foreach (BsonDocument document in observations.Query().Where(BsonExpression.Create(
                     "WorldId = @0 AND StartUtc < @1 AND TimestampUtc > @2", world, end.Ticks, start.Ticks))
                     .OrderBy("PlayerKey").ToEnumerable())
        {
            if (!string.Equals(document["WorldId"].AsString, world, StringComparison.Ordinal) || document["Kind"].AsString != kind.ToString()) continue;
            // Legacy time-only spans cannot be converted to distance; retain them without inventing meters.
            if ((kind is ActivityKind.Sailing or ActivityKind.OnFoot) && (!document.ContainsKey("Meters") || document["Meters"].AsDouble <= 0)) continue;
            long total = document["TimestampUtc"].AsInt64 - document["StartUtc"].AsInt64;
            long overlap = Math.Min(end.Ticks, document["TimestampUtc"].AsInt64) - Math.Max(start.Ticks, document["StartUtc"].AsInt64);
            if (overlap <= 0 || total <= 0) continue;
            document["Meters"] = document["Meters"].AsDouble * ((double)overlap / total);
            document["Seconds"] = (double)overlap / TimeSpan.TicksPerSecond;
            yield return document;
        }
    }

    private IReadOnlyList<ReportRow> BiomeTimes(string world, DateTime start, DateTime end)
    {
        var top = new List<ReportRow>();
        var totals = new Dictionary<string, double>(StringComparer.Ordinal);
        string? player = null;
        string id = "", name = "";
        DateTime observed = DateTime.MinValue;
        void Finish()
        {
            if (totals.Count == 0) return;
            var best = totals.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal).First();
            top.Add(new ReportRow(id, name, 0, best.Value, 0, observed, biome: best.Key));
            top.Sort((a, b) => { int order = b.Seconds.CompareTo(a.Seconds); return order != 0 ? order : string.CompareOrdinal(a.Id, b.Id); });
            if (top.Count > 16) top.RemoveAt(16);
        }
        foreach (var document in Intervals(world, start, end, ActivityKind.BiomeTime))
        {
            string biome = document["Prefab"].AsString;
            if (!ObservationValues.KnownBiome(biome)) continue;
            if (player != document["PlayerKey"].AsString)
            {
                Finish(); totals.Clear(); player = document["PlayerKey"].AsString;
                id = document["PlayerId"].AsString; name = document["PlayerName"].AsString; observed = DateTime.MinValue;
            }
            var time = ObservationValues.Date(document["NameObservedUtc"]);
            if (time > observed || time == observed && string.CompareOrdinal(document["PlayerName"].AsString, name) < 0)
            { observed = time; name = document["PlayerName"].AsString; }
            totals.TryGetValue(biome, out double previous);
            totals[biome] = Math.Min(double.MaxValue, previous + document["Seconds"].AsDouble);
        }
        Finish(); return top.AsReadOnly();
    }

    private IEnumerable<BsonDocument> Explored(string world, DateTime start, DateTime end)
    {
        foreach (BsonDocument document in chunks.Query().Where(BsonExpression.Create(
                     "WorldId = @0 AND MinUtc < @1 AND MaxUtc >= @2", world, end.Ticks, start.Ticks)).OrderBy("PlayerKey").ToEnumerable())
        {
            if (!string.Equals(document["WorldId"].AsString, world, StringComparison.Ordinal)) continue;
            long count = Unpack(document["FirstTimes"].AsBinary).Values.Count(x => x >= start.Ticks && x < end.Ticks);
            if (count == 0) continue;
            document["Units"] = count; yield return document;
        }
    }
}
