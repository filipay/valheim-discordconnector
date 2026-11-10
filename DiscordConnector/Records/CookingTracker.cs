using System;
using System.Collections.Generic;
using System.Linq;

namespace DiscordConnector.Records;

internal sealed class CookingTracker
{
    private readonly Func<CookingChange, bool> _enqueue;
    private readonly Action<string> _warn;
    private readonly long _observationEpoch;
    private readonly Dictionary<string, CookingChange> _pending = new(StringComparer.Ordinal);
    private static readonly object SequenceLock = new();
    private static long _sequence;
    internal string CollectorEpoch { get; private set; } = Guid.NewGuid().ToString("N");
    internal long AcceptanceSequence => _sequence;
    internal int PendingCount => _pending.Count;
    internal bool Available { get; private set; }

    // Process-wide high water also protects a replacement tracker on the same durable worker epoch.
    internal CookingTracker(long observationEpoch, Func<CookingChange, bool> enqueue, Action<string> warn)
    {
        if (observationEpoch <= 0) throw new ArgumentOutOfRangeException(nameof(observationEpoch));
        _observationEpoch = observationEpoch;
        _enqueue = enqueue; _warn = warn;
    }

    internal void SetAvailability(bool enabled)
    {
        if (Available && !enabled) Reset();
        Available = enabled;
    }

    internal void ObserveInput(CookingSlotSnapshot before, CookingSlotSnapshot after, ActivityActor actor,
        bool completedInvocation) => ObserveInput(before, after, actor, completedInvocation, DateTime.UtcNow);

    internal void ObserveInput(CookingSlotSnapshot before, CookingSlotSnapshot after, ActivityActor actor,
        bool completedInvocation, DateTime utc)
    {
        if (!Available || !completedInvocation || !SameSlot(before, after) || before.Owner == 0 ||
            before.Owner != after.Owner || before.Status != CookingStatus.Free || after.Status != CookingStatus.NotDone ||
            before.ItemPrefab.Length != 0 || before.CollectorEpoch != after.CollectorEpoch || after.CollectorEpoch != CollectorEpoch) return;
        string key = Key(after);
        Invalidate(after.WorldId, after.StationId, after.Slot);
        if (_pending.Count >= 4096) { _warn("Cooking candidate limit reached; input skipped."); return; }
        long sequence;
        lock (SequenceLock)
        {
            if (_sequence == long.MaxValue) { _warn("Cooking generation sequence exhausted; input skipped."); return; }
            sequence = checked(++_sequence);
        }
        var accepted = new CookingChange(CookingChangeKind.AcceptedInput, after,
            CollectorEpoch + ":" + ObservationValues.Number(sequence), _observationEpoch, sequence, actor, utc);
        if (_enqueue(accepted)) _pending.Add(key, accepted);
        else DropPending();
    }

    internal void ObserveCooking(CookingSlotSnapshot before, CookingSlotSnapshot after, CookingConversion? conversion,
        bool completedInvocation) => ObserveCooking(before, after, conversion, completedInvocation, DateTime.UtcNow);

    internal void ObserveCooking(CookingSlotSnapshot before, CookingSlotSnapshot after, CookingConversion? conversion,
        bool completedInvocation, DateTime utc)
    {
        string key = Key(before);
        if (!_pending.TryGetValue(key, out var accepted)) return;
        var input = accepted.Snapshot;
        if (!Available || !completedInvocation || !SameSlot(before, after) || before.Owner != input.Owner || after.Owner != input.Owner ||
            before.CollectorEpoch != input.CollectorEpoch || after.CollectorEpoch != input.CollectorEpoch ||
            before.ItemPrefab != input.ItemPrefab || before.Status != CookingStatus.NotDone ||
            after.CookProgress < before.CookProgress)
        { Invalidate(input.WorldId, input.StationId, input.Slot); return; }
        if (after.Status == CookingStatus.NotDone && after.ItemPrefab == input.ItemPrefab) return;
        if (after.Status != CookingStatus.Done || conversion == null || after.ItemPrefab != conversion.OutputPrefab)
        { Invalidate(input.WorldId, input.StationId, input.Slot); return; }
        // Persistence keeps canonical input identity separate from the observed output.
        var done = new CookingSlotSnapshot(input.WorldId, input.StationId, input.Slot, input.Owner,
            input.ItemPrefab, CookingStatus.Done, after.CookProgress, input.CollectorEpoch);
        var change = new CookingChange(CookingChangeKind.Completed, done, accepted.Generation,
            accepted.ObservationEpoch, accepted.GenerationSequence, accepted.Actor, utc, conversion);
        _pending.Remove(key);
        if (!_enqueue(change)) DropPending();
    }

    internal void Invalidate(string world, string station, int slot)
    {
        string key = ObservationValues.Key(world, station, ObservationValues.Number(slot));
        if (!_pending.TryGetValue(key, out var accepted)) return;
        _pending.Remove(key);
        if (!_enqueue(new CookingChange(CookingChangeKind.Invalidate, accepted.Snapshot, accepted.Generation,
            accepted.ObservationEpoch, accepted.GenerationSequence, accepted.Actor, DateTime.UtcNow))) DropPending();
    }

    internal void InvalidateStation(string world, string station)
    {
        foreach (var accepted in _pending.Values.Where(x => x.Snapshot.WorldId == world && x.Snapshot.StationId == station).ToArray())
            Invalidate(world, station, accepted.Snapshot.Slot);
    }

    internal void Reset()
    {
        foreach (var accepted in _pending.Values.ToArray())
            Invalidate(accepted.Snapshot.WorldId, accepted.Snapshot.StationId, accepted.Snapshot.Slot);
        DropPending();
    }

    private void DropPending() { _pending.Clear(); CollectorEpoch = Guid.NewGuid().ToString("N"); }
    private static string Key(CookingSlotSnapshot s) => ObservationValues.Key(s.WorldId, s.StationId, ObservationValues.Number(s.Slot));
    private static bool SameSlot(CookingSlotSnapshot a, CookingSlotSnapshot b) => a.WorldId == b.WorldId && a.StationId == b.StationId && a.Slot == b.Slot;
}
