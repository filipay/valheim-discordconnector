using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace DiscordConnector.Records;

// shortcut: ownership detection requires direct store delegates; add explicit queue ownership before introducing wrappers.
internal interface IObservationLossSource
{
    ObservationLoss Loss { get; }
}

internal sealed class ObservationLossSnapshot
{
    internal string WorldId { get; }
    internal ReportMetric Metric { get; }
    internal string ProcessEpoch { get; }
    internal IReadOnlyDictionary<ObservationRejection, long> Rejections { get; }
    internal long UncertainWrites { get; }
    internal DateTime ObservedUtc { get; }

    internal ObservationLossSnapshot(string world, ReportMetric metric, string processEpoch,
        IDictionary<ObservationRejection, long> rejections, long uncertainWrites, DateTime observedUtc)
    {
        ObservationValues.Text(world); ObservationValues.Text(processEpoch); ObservationValues.Utc(observedUtc);
        if (world.Length > 256 || processEpoch.Length > 128 || !Enum.IsDefined(typeof(ReportMetric), metric) ||
            uncertainWrites < 0 || rejections == null || rejections.Any(x => !ObservationLoss.IsLoss(x.Key) || x.Value < 0))
            throw new ArgumentException("Invalid observation loss snapshot.");
        WorldId = world; Metric = metric; ProcessEpoch = processEpoch; UncertainWrites = uncertainWrites; ObservedUtc = observedUtc;
        Rejections = new ReadOnlyDictionary<ObservationRejection, long>(ObservationLoss.Reasons.ToDictionary(x => x,
            x => rejections.TryGetValue(x, out long count) ? count : 0));
    }
}

internal sealed class ObservationLoss
{
    internal static readonly IReadOnlyList<ObservationRejection> Reasons = Array.AsReadOnly(Enum.GetValues(typeof(ObservationRejection)).Cast<ObservationRejection>()
        .Where(IsLoss).ToArray());
    internal static readonly ObservationLoss Process = new(_ => { });
    private sealed class State
    {
        internal ObservationLossSnapshot Latest;
        internal bool Dirty = true;
        internal State(ObservationLossSnapshot latest) => Latest = latest;
    }
    private readonly object gate = new();
    private readonly Dictionary<(string World, ReportMetric Metric), State> states = new();
    private readonly string epoch;
    private readonly Func<DateTime> utc;
    private Action<string> warn;
    private bool pending;
    private double nextAttempt;
    private int cursor;
    internal bool Incomplete { get; private set; }
    internal int Retained { get { lock (gate) return states.Count; } }
    internal int UnacknowledgedKeys { get { lock (gate) return states.Values.Count(x => x.Dirty); } }

    internal ObservationLoss(Action<string> warn, string? processEpoch = null, Func<DateTime>? utc = null)
    {
        this.warn = warn ?? throw new ArgumentNullException(nameof(warn));
        epoch = processEpoch ?? Guid.NewGuid().ToString("N");
        ObservationValues.Text(epoch);
        if (epoch.Length > 128) throw new ArgumentException("Invalid process epoch.");
        this.utc = utc ?? (() => DateTime.UtcNow);
    }

    internal void SetWarning(Action<string> warning) { lock (gate) warn = warning; }
    internal static bool IsLoss(ObservationRejection reason) => Enum.IsDefined(typeof(ObservationRejection), reason) &&
        reason != ObservationRejection.None && reason != ObservationRejection.Duplicate;

    internal void Record(string world, ReportMetric metric, ObservationRejection reason, long count = 1)
    {
        if (!Enum.IsDefined(typeof(ObservationRejection), reason) || count < 0) throw new ArgumentException("Invalid rejection.");
        if (!IsLoss(reason) || count == 0) return;
        Update(world, metric, reason, count);
    }

    internal void RecordUncertain(string world, ReportMetric metric, long count = 1)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        if (count != 0) Update(world, metric, null, count);
    }

    private void Update(string world, ReportMetric metric, ObservationRejection? reason, long count)
    {
        ObservationValues.Text(world);
        if (!Enum.IsDefined(typeof(ReportMetric), metric)) throw new ArgumentException("Invalid loss key.");
        if (world.Length > 256) { MarkIncomplete(); return; }
        bool incomplete = false;
        lock (gate)
        {
            var key = (world, metric);
            if (!states.TryGetValue(key, out State? state))
            {
                if (states.Count >= 64) incomplete = true;
                else states.Add(key, state = new State(new ObservationLossSnapshot(world, metric, epoch,
                    new Dictionary<ObservationRejection, long>(), 0, utc())));
            }
            if (state != null)
            {
                Dictionary<ObservationRejection, long> totals = state.Latest.Rejections.ToDictionary(x => x.Key, x => x.Value);
                long uncertainty = state.Latest.UncertainWrites;
                if (reason.HasValue) totals[reason.Value] = ObservationValues.Add(totals[reason.Value], count);
                else uncertainty = ObservationValues.Add(uncertainty, count);
                incomplete = uncertainty == long.MaxValue || totals.Values.Any(x => x == long.MaxValue);
                state.Latest = new ObservationLossSnapshot(world, metric, epoch, totals, uncertainty, utc());
                state.Dirty = true;
            }
        }
        if (incomplete) MarkIncomplete();
    }

    internal void MarkIncomplete()
    {
        Action<string> warning;
        lock (gate)
        {
            if (Incomplete) return;
            Incomplete = true;
            warning = warn;
        }
        try { warning("Observation loss diagnostics incomplete: bounded state or counter capacity exceeded."); } catch { }
    }

    internal ObservationLossSnapshot? Snapshot(string world, ReportMetric metric)
    { lock (gate) return states.TryGetValue((world, metric), out State? state) ? state.Latest : null; }

    // Only absolute diagnostic values may be retried. Never retain a gameplay batch or dispatch callback here.
    internal Task Maintain(double monotonic, Func<ObservationLossSnapshot, Task> submit, bool final = false)
    {
        State? state = null;
        ObservationLossSnapshot sent;
        lock (gate)
        {
            if (!ObservationValues.Finite(monotonic) || monotonic < 0 || !final && monotonic < nextAttempt || pending) return Task.CompletedTask;
            nextAttempt = monotonic + 10;
            for (int checkedKeys = 0; checkedKeys < states.Count; checkedKeys++)
            {
                state = states.ElementAt(cursor++ % states.Count).Value;
                if (cursor == int.MaxValue) cursor = 0;
                if (state.Dirty) break;
                state = null;
            }
            if (state == null) return Task.CompletedTask;
            sent = state.Latest;
            pending = true;
        }
        Task acknowledgment;
        try { acknowledgment = submit(sent) ?? Task.FromException(new InvalidOperationException("Missing diagnostic acknowledgment.")); }
        catch { lock (gate) pending = false; return Task.CompletedTask; }
        return acknowledgment.ContinueWith(completed =>
        {
            lock (gate)
            {
                if (completed.Status == TaskStatus.RanToCompletion && ReferenceEquals(state.Latest, sent)) state.Dirty = false;
                _ = completed.Exception;
                pending = false;
            }
        }, System.Threading.CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    internal static ReportMetric Metric(ReportedSignalKind kind) => kind == ReportedSignalKind.KillCredit ?
        ReportMetric.ReportedKillCredits : kind == ReportedSignalKind.ConsumableUse ?
        ReportMetric.ObservedConsumableUses : ReportMetric.ReportedDodgeHitNotifications;
    internal static ReportMetric Metric(ActivityKind kind) => kind == ActivityKind.BowHit ? ReportMetric.LongestBowHit : kind == ActivityKind.CreatureDamage ? ReportMetric.DamageDealt :
        kind == ActivityKind.CreatureKill ? ReportMetric.ReportedKillCredits : kind == ActivityKind.OnFoot ? ReportMetric.OnFootDistance :
        kind == ActivityKind.BiomeTime ? ReportMetric.BiomeTime : kind == ActivityKind.OnlineTime ? ReportMetric.OnlineTime :
        kind == ActivityKind.PortalTrip ? ReportMetric.PortalTrips : kind == ActivityKind.Sailing ? ReportMetric.SailingDistance :
        kind == ActivityKind.BiomeVisited ? ReportMetric.Biomes :
        kind == ActivityKind.BossCredit ? ReportMetric.ParticipantBossCredits :
        kind == ActivityKind.HealthLost ? ReportMetric.HealthLost :
        kind == ActivityKind.InferredPickup ? ReportMetric.InferredPickups :
        kind == ActivityKind.CropPlanted ? ReportMetric.CropsPlanted : ReportMetric.NewPieces;

    internal void Batch(ActivityBatch batch, ObservationRejection? reason)
    {
        void Count(string world, ReportMetric metric, long count = 1)
        { if (reason.HasValue) Record(world, metric, reason.Value, count); else RecordUncertain(world, metric, count); }
        foreach (var record in batch.Activities)
        {
            Count(record.WorldId, Metric(record.Kind));
        }
        foreach (var chunk in batch.Chunks) Count(chunk.WorldId, ReportMetric.ExplorationCells);
        foreach (var action in batch.Actions) Count(action.WorldId, ReportMetric.ObservedDodgeRolls);
        foreach (var change in batch.Cooking)
            if (change.Kind == CookingChangeKind.Completed) Count(change.Snapshot.WorldId, ReportMetric.CookingCompletions);
        foreach (var signal in batch.Signals) Count(signal.WorldId, Metric(signal.Kind));
    }
}
