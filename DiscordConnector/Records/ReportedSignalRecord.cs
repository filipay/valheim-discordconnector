using System;

namespace DiscordConnector.Records;

internal enum ReportedSignalKind { KillCredit, ConsumableUse, DodgeHitNotification }

internal sealed class ReportedSignalRecord
{
    internal ReportedSignalKind Kind { get; }
    internal string WorldId { get; }
    internal ActivityActor Actor { get; }
    internal string Token { get; }
    internal DateTime TimestampUtc { get; }

    internal ReportedSignalRecord(ReportedSignalKind kind, string worldId, ActivityActor actor, string token, DateTime timestampUtc)
    {
        if (actor == null) throw new ArgumentNullException(nameof(actor));
        if (!Enum.IsDefined(typeof(ReportedSignalKind), kind)) throw new ArgumentException("Invalid signal kind.");
        ObservationValues.Text(worldId); ObservationValues.Text(token); ObservationValues.Utc(timestampUtc);
        if (worldId.Length > 256 || actor.PlayerId.Length > 256 || actor.PlayerName.Length > 128
            || actor.ConnectionToken.Length > 128 || token.Length > 256) throw new ArgumentException("Signal text exceeds its bound.");
        Kind = kind; WorldId = worldId; Actor = actor; Token = token; TimestampUtc = timestampUtc;
    }
}
