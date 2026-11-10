using System;

namespace DiscordConnector.Records;

internal enum CookingStatus { Unknown, Free, NotDone, Done, Burnt }
internal enum CookingChangeKind { AcceptedInput, Invalidate, Completed }

internal sealed class CookingSlotSnapshot
{
    internal string WorldId { get; }
    internal string StationId { get; }
    internal int Slot { get; }
    internal long Owner { get; }
    internal string ItemPrefab { get; }
    internal CookingStatus Status { get; }
    internal double CookProgress { get; }
    internal string CollectorEpoch { get; }

    internal CookingSlotSnapshot(string worldId, string stationId, int slot, long owner, string itemPrefab,
        CookingStatus status, double cookProgress, string collectorEpoch)
    {
        ObservationValues.Text(worldId); ObservationValues.Text(stationId); ObservationValues.Text(collectorEpoch);
        if (slot < 0 || slot >= 32 || !Enum.IsDefined(typeof(CookingStatus), status)
            || !ObservationValues.Finite(cookProgress) || cookProgress < 0 || itemPrefab == null)
            throw new ArgumentException("Invalid bounded cooking slot snapshot.");
        if (status == CookingStatus.NotDone || status == CookingStatus.Done || status == CookingStatus.Burnt)
            ObservationValues.Text(itemPrefab);
        WorldId = worldId; StationId = stationId; Slot = slot; Owner = owner; ItemPrefab = itemPrefab;
        Status = status; CookProgress = cookProgress; CollectorEpoch = collectorEpoch;
    }
}

internal sealed class CookingConversion
{
    internal string OutputPrefab { get; }
    internal long Units { get; }
    internal CookingConversion(string outputPrefab, long units)
    {
        ObservationValues.Text(outputPrefab);
        if (units <= 0) throw new ArgumentOutOfRangeException(nameof(units));
        OutputPrefab = outputPrefab; Units = units;
    }
}

internal sealed class CookingChange
{
    internal CookingChangeKind Kind { get; }
    internal CookingSlotSnapshot Snapshot { get; }
    internal string Generation { get; }
    internal long ObservationEpoch { get; }
    internal long GenerationSequence { get; }
    internal ActivityActor? Actor { get; }
    internal DateTime TimestampUtc { get; }
    internal CookingConversion? Conversion { get; }

    internal CookingChange(CookingChangeKind kind, CookingSlotSnapshot snapshot, string generation,
        long observationEpoch, long generationSequence, ActivityActor? actor, DateTime timestampUtc, CookingConversion? conversion = null)
    {
        if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
        ObservationValues.Text(generation); ObservationValues.Utc(timestampUtc);
        if (observationEpoch <= 0 || generationSequence <= 0) throw new ArgumentException("Positive observation epoch and generation sequence are required.");
        if (!Enum.IsDefined(typeof(CookingChangeKind), kind)) throw new ArgumentException("Invalid cooking change.");
        if (kind == CookingChangeKind.AcceptedInput && (actor == null || snapshot.Status != CookingStatus.NotDone || snapshot.Owner == 0)
            || kind == CookingChangeKind.Completed && (conversion == null || snapshot.Status != CookingStatus.Done || snapshot.Owner == 0))
            throw new ArgumentException("Cooking change lacks accepted input/completion proof.");
        Kind = kind; Snapshot = snapshot; Generation = generation; ObservationEpoch = observationEpoch; GenerationSequence = generationSequence;
        Actor = actor; TimestampUtc = timestampUtc; Conversion = conversion;
    }
}
