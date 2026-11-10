using System;

namespace DiscordConnector.Records;

internal enum PlayerActionKind { DodgeRoll }

internal sealed class PlayerActionRecord
{
    internal string WorldId { get; }
    internal ActivityActor Actor { get; }
    internal PlayerActionKind Kind { get; }
    internal string ActionToken { get; }
    internal DateTime TimestampUtc { get; }

    internal PlayerActionRecord(string worldId, ActivityActor actor, PlayerActionKind kind, string actionToken, DateTime timestampUtc)
    {
        ObservationValues.Text(worldId); ObservationValues.Text(actionToken); ObservationValues.Utc(timestampUtc);
        if (!Enum.IsDefined(typeof(PlayerActionKind), kind)) throw new ArgumentException("Invalid action kind.");
        WorldId = worldId; Actor = actor ?? throw new ArgumentNullException(nameof(actor)); Kind = kind; ActionToken = actionToken; TimestampUtc = timestampUtc;
    }
}
