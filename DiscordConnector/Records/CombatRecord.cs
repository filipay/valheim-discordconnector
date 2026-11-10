using System;

using LiteDB;

namespace DiscordConnector.Records;

internal sealed class CombatRecord
{
    [BsonId]
    public string Id { get; set; } = "";
    public string WorldId { get; set; } = "";
    public string VictimId { get; set; } = "";
    public string PlayerId { get; set; } = "";
    public string PlayerName { get; set; } = "";
    public string Creature { get; set; } = "";
    public string Biome { get; set; } = "";
    public bool IsBoss { get; set; }
    public DateTime TimestampUtc { get; set; }
}
