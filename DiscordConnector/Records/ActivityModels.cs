using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

using LiteDB;

namespace DiscordConnector.Records;

internal sealed class ActivityActor
{
    internal string PlayerId { get; }
    internal string PlayerName { get; }
    internal string ConnectionToken { get; }
    internal DateTime NameObservedUtc { get; }

    internal ActivityActor(string playerId, string playerName, string connectionToken, DateTime nameObservedUtc)
    {
        ObservationValues.Text(playerId); ObservationValues.Text(playerName); ObservationValues.Text(connectionToken);
        ObservationValues.Utc(nameObservedUtc);
        PlayerId = playerId; PlayerName = playerName; ConnectionToken = connectionToken; NameObservedUtc = nameObservedUtc;
    }
}

internal sealed class LeaderboardsOptions
{
    internal bool BossKills { get; }
    internal bool Biomes { get; }
    internal bool DodgeRolls { get; }
    internal bool Sailing { get; }
    internal bool FloorPickups { get; }
    internal bool Construction { get; }
    internal bool Exploration { get; }
    internal bool CookingCompletions { get; }
    internal bool ReportedKillCredits { get; }
    internal bool ConsumableUses { get; }
    internal bool ReportedDodgeHits { get; }
    internal bool CropsPlanted { get; }
    internal bool HealthLost { get; }
    internal bool OnFoot { get; }
    internal bool BiomeTime { get; }
    internal bool OnlineTime { get; }
    internal bool PortalTrips { get; }
    internal bool DamageDealt { get; }
    internal bool LongestBowHit { get; }

    internal LeaderboardsOptions(bool bossKills = false, bool biomes = false, bool dodgeRolls = false,
        bool sailing = false, bool floorPickups = false, bool construction = false,
        bool exploration = false, bool cookingCompletions = false, bool reportedKillCredits = false, bool consumableUses = false,
        bool reportedDodgeHits = false, bool cropsPlanted = false, bool healthLost = false,
        bool onFoot = false, bool biomeTime = false, bool onlineTime = false, bool portalTrips = false, bool damageDealt = false,
        bool longestBowHit = false)
    {
        BossKills = bossKills; Biomes = biomes; DodgeRolls = dodgeRolls;
        Sailing = sailing; FloorPickups = floorPickups;
        Construction = construction; Exploration = exploration; CookingCompletions = cookingCompletions;
        ReportedKillCredits = reportedKillCredits; ConsumableUses = consumableUses;
        ReportedDodgeHits = reportedDodgeHits;
        CropsPlanted = cropsPlanted;
        HealthLost = healthLost;
        OnFoot = onFoot; BiomeTime = biomeTime; OnlineTime = onlineTime; PortalTrips = portalTrips;
        DamageDealt = damageDealt;
        LongestBowHit = longestBowHit;
    }

    internal bool Includes(ReportMetric metric) => metric switch
    {
        ReportMetric.ParticipantBossCredits => BossKills,
        ReportMetric.Biomes => Biomes,
        ReportMetric.ObservedDodgeRolls => DodgeRolls,
        ReportMetric.SailingDistance => Sailing,
        ReportMetric.InferredPickups => FloorPickups,
        ReportMetric.NewPieces => Construction,
        ReportMetric.ExplorationCells => Exploration,
        ReportMetric.CookingCompletions => CookingCompletions,
        ReportMetric.ReportedKillCredits => ReportedKillCredits,
        ReportMetric.ObservedConsumableUses => ConsumableUses,
        ReportMetric.ReportedDodgeHitNotifications => ReportedDodgeHits,
        ReportMetric.CropsPlanted => CropsPlanted,
        ReportMetric.HealthLost => HealthLost,
        ReportMetric.OnFootDistance => OnFoot,
        ReportMetric.BiomeTime => BiomeTime,
        ReportMetric.OnlineTime => OnlineTime,
        ReportMetric.PortalTrips => PortalTrips,
        ReportMetric.DamageDealt => DamageDealt,
        ReportMetric.LongestBowHit => LongestBowHit,
        _ => false
    };
}

internal enum ReportMetric
{
    // Preserve the identities of retained metrics; retired history is ignored, never rewritten.
    ParticipantBossCredits = 1, Biomes = 4, ObservedDodgeRolls = 6,
    InferredPickups = 9, NewPieces = 10, ExplorationCells = 12, CookingCompletions = 13,
    ReportedKillCredits = 14, ObservedConsumableUses = 15, ReportedDodgeHitNotifications = 16,
    SailingDistance = 17, CropsPlanted = 19, HealthLost = 20,
    OnFootDistance = 21, BiomeTime = 22, OnlineTime = 23, PortalTrips = 24, DamageDealt = 25, LongestBowHit = 26
}

internal sealed class ReportRow
{
    internal string Id { get; }
    internal string Name { get; }
    internal long Count { get; }
    internal double Seconds { get; }
    internal double HpLoss { get; }
    internal double Meters { get; }
    internal DateTime NameObservedUtc { get; }
    internal long ProvenFreshCount { get; }
    internal string Biome { get; }

    internal ReportRow(string id, string name, long count, double seconds, double hpLoss, DateTime nameObservedUtc, long provenFreshCount = 0, double meters = 0, string biome = "")
    {
        Id = id; Name = name; Count = count; Seconds = seconds; HpLoss = hpLoss; Meters = meters; NameObservedUtc = nameObservedUtc; ProvenFreshCount = provenFreshCount;
        Biome = biome;
    }
}

internal enum CoverageState { Observing, Disabled, Unavailable, Partial }
internal enum CoverageReason { Dropped, Expired, Invalid, QueueFull, DatabaseFailure, Restart, CapabilityLost, UnknownProvenance, GridMismatch }

internal sealed class CoverageUpdate
{
    internal string WorldId { get; }
    internal ReportMetric Metric { get; }
    internal CoverageState State { get; }
    internal DateTime TimestampUtc { get; }
    internal CoverageReason? Reason { get; }
    internal long Count { get; }

    internal CoverageUpdate(string worldId, ReportMetric metric, CoverageState state, DateTime timestampUtc,
        CoverageReason? reason = null, long count = 1)
    {
        ObservationValues.Text(worldId); ObservationValues.Utc(timestampUtc);
        if (!Enum.IsDefined(typeof(ReportMetric), metric) || !Enum.IsDefined(typeof(CoverageState), state)
            || reason.HasValue && !Enum.IsDefined(typeof(CoverageReason), reason.Value) || count < 0) throw new ArgumentException("Invalid coverage.");
        WorldId = worldId; Metric = metric; State = state; TimestampUtc = timestampUtc; Reason = reason; Count = count;
    }
}

internal sealed class CoverageSnapshot
{
    internal ReportMetric Metric { get; }
    internal CoverageState State { get; }
    internal DateTime StartedUtc { get; }
    internal DateTime LastTransitionUtc { get; }
    internal IReadOnlyDictionary<CoverageReason, long> Counters { get; }
    internal IReadOnlyDictionary<ObservationRejection, long> Rejections { get; }
    internal long KnownLosses { get; }
    internal long UncertainWrites { get; }

    internal CoverageSnapshot(ReportMetric metric, CoverageState state, DateTime startedUtc, DateTime lastTransitionUtc,
        IDictionary<CoverageReason, long> counters, IDictionary<ObservationRejection, long>? rejections = null, long uncertainWrites = 0)
    {
        Metric = metric; State = state; StartedUtc = startedUtc; LastTransitionUtc = lastTransitionUtc;
        Counters = new ReadOnlyDictionary<CoverageReason, long>(new Dictionary<CoverageReason, long>(counters));
        Rejections = new ReadOnlyDictionary<ObservationRejection, long>(new Dictionary<ObservationRejection, long>(
            rejections ?? new Dictionary<ObservationRejection, long>()));
        KnownLosses = Rejections.Values.Aggregate(0L, ObservationValues.Add); UncertainWrites = uncertainWrites;
    }
}

internal sealed class CombinedReportSnapshot
{
    internal string WorldId { get; }
    internal IReadOnlyDictionary<ReportMetric, IReadOnlyList<ReportRow>> Rows { get; }
    internal IReadOnlyList<CoverageSnapshot> Coverage { get; }

    internal CombinedReportSnapshot(string worldId, IDictionary<ReportMetric, IReadOnlyList<ReportRow>>? rows = null,
        IEnumerable<CoverageSnapshot>? coverage = null)
    {
        WorldId = worldId;
        Rows = new ReadOnlyDictionary<ReportMetric, IReadOnlyList<ReportRow>>((rows ?? new Dictionary<ReportMetric, IReadOnlyList<ReportRow>>())
            .ToDictionary(x => x.Key, x => (IReadOnlyList<ReportRow>)Array.AsReadOnly(x.Value.Take(16).ToArray())));
        Coverage = Array.AsReadOnly((coverage ?? Array.Empty<CoverageSnapshot>()).Take(Enum.GetValues(typeof(ReportMetric)).Length).ToArray());
    }
}

internal enum ActivityKind { Sailing, InferredPickup, NewPiece, CropPlanted, HealthLost, BiomeVisited, BossCredit, OnFoot, BiomeTime, OnlineTime, PortalTrip, CreatureDamage, CreatureKill, BowHit }
internal enum ItemProvenance { UnknownMixed, ProvenFreshSource, InferredSource, PlayerDropOrRefund }

internal sealed class ActivityRecord
{
    internal ActivityKind Kind { get; }
    internal string WorldId { get; }
    internal ActivityActor Actor { get; }
    internal string SubjectId { get; }
    internal string Token { get; }
    internal DateTime TimestampUtc { get; }
    internal DateTime StartUtc { get; }
    internal double Seconds { get; }
    internal double Meters { get; }
    internal string Prefab { get; }
    internal double HpLoss { get; }
    internal long Units { get; }
    internal ItemProvenance Provenance { get; }
    internal string? SourceToken { get; }
    internal long SourceUnits { get; }

    internal ActivityRecord(ActivityKind kind, string worldId, ActivityActor actor, string subjectId, string token,
        DateTime timestampUtc, string prefab = "", long units = 1, DateTime? startUtc = null, double seconds = 0,
        ItemProvenance provenance = ItemProvenance.UnknownMixed, string? sourceToken = null, long sourceUnits = 0,
        double meters = 0, double hpLoss = 0)
    {
        ObservationValues.Text(worldId); ObservationValues.Text(subjectId); ObservationValues.Text(token); ObservationValues.Utc(timestampUtc);
        if (actor == null) throw new ArgumentNullException(nameof(actor));
        if (!Enum.IsDefined(typeof(ActivityKind), kind) || !Enum.IsDefined(typeof(ItemProvenance), provenance)) throw new ArgumentException("Invalid activity kind/provenance.");
        DateTime start = startUtc ?? timestampUtc;
        ObservationValues.Utc(start);
        if (units <= 0 || start > timestampUtc || !ObservationValues.Finite(seconds) || seconds < 0) throw new ArgumentException("Invalid activity units/interval.");
        if (!ObservationValues.Finite(meters) || meters < 0 || kind is not (ActivityKind.Sailing or ActivityKind.OnFoot or ActivityKind.BowHit) && meters != 0)
            throw new ArgumentException("Only movement and bow hits have a nonnegative finite distance.");
        if (kind == ActivityKind.BowHit && (meters <= 0 || meters > ObservationValues.MaxBowHitMeters))
            throw new ArgumentException("Bow-hit distance must be positive and within the observation cap.");
        if (!ObservationValues.Finite(hpLoss) || (kind is ActivityKind.HealthLost or ActivityKind.CreatureDamage ? hpLoss <= 0 : hpLoss != 0))
            throw new ArgumentException("Only health-loss and creature-damage observations have positive finite HP loss.");
        if (kind != ActivityKind.InferredPickup && units != 1) throw new ArgumentException("Only pickups have item quantities.");
        if ((kind is ActivityKind.Sailing or ActivityKind.OnFoot or ActivityKind.BiomeTime or ActivityKind.OnlineTime) &&
            (seconds <= 0 || seconds > 10 || start == timestampUtc || (timestampUtc - start).TotalSeconds > 10))
            throw new ArgumentException("Sampled spans must be positive and at most ten seconds.");
        if (kind is not (ActivityKind.Sailing or ActivityKind.HealthLost or ActivityKind.OnFoot or ActivityKind.OnlineTime or ActivityKind.PortalTrip or ActivityKind.CreatureDamage or ActivityKind.CreatureKill or ActivityKind.BowHit)) ObservationValues.Text(prefab);
        if (provenance == ItemProvenance.ProvenFreshSource)
        {
            ObservationValues.Text(sourceToken!);
            if (kind != ActivityKind.InferredPickup || sourceUnits <= 0 || units > sourceUnits) throw new ArgumentException("Invalid proven source quantity.");
        }
        Kind = kind; WorldId = worldId; Actor = actor; SubjectId = subjectId; Token = token; TimestampUtc = timestampUtc;
        StartUtc = start; Seconds = seconds; Meters = meters; HpLoss = hpLoss; Prefab = prefab; Units = units; Provenance = provenance;
        SourceToken = sourceToken; SourceUnits = sourceUnits;
    }
}

internal sealed class ExplorationGrid
{
    internal int TextureSize { get; }
    internal double PixelSize { get; }
    internal double ExploreRadius { get; }
    internal string Id { get; }
    internal ExplorationGrid(int textureSize, double pixelSize, double exploreRadius)
    {
        if (textureSize <= 0 || textureSize > 16384 || !ObservationValues.Finite(pixelSize) || pixelSize <= 0
            || !ObservationValues.Finite(exploreRadius) || exploreRadius <= 0 || Math.Ceiling(exploreRadius / pixelSize) > 128)
            throw new ArgumentException("Unsupported runtime grid.");
        TextureSize = textureSize; PixelSize = pixelSize; ExploreRadius = exploreRadius;
        Id = "circle-v1:" + textureSize.ToString(CultureInfo.InvariantCulture) + ":" + pixelSize.ToString("R", CultureInfo.InvariantCulture)
            + ":" + exploreRadius.ToString("R", CultureInfo.InvariantCulture);
    }
}

internal sealed class ExplorationCell
{
    internal int Index { get; }
    internal DateTime FirstObservedUtc { get; }
    internal ExplorationCell(int index, DateTime firstObservedUtc)
    {
        if (index < 0 || index >= 1024) throw new ArgumentOutOfRangeException(nameof(index));
        ObservationValues.Utc(firstObservedUtc); Index = index; FirstObservedUtc = firstObservedUtc;
    }
}

internal sealed class ExplorationChunk
{
    internal string WorldId { get; }
    internal ActivityActor Actor { get; }
    internal ExplorationGrid Grid { get; }
    internal int X { get; }
    internal int Y { get; }
    internal IReadOnlyList<ExplorationCell> Cells { get; }
    internal ExplorationChunk(string worldId, ActivityActor actor, ExplorationGrid grid, int x, int y, IEnumerable<ExplorationCell> cells)
    {
        ObservationValues.Text(worldId);
        if (actor == null || grid == null || cells == null) throw new ArgumentNullException();
        ExplorationCell[] captured = cells.Take(1025).ToArray();
        if (x < 0 || y < 0 || (long)x * 32 >= grid.TextureSize || (long)y * 32 >= grid.TextureSize || captured.Length == 0
            || captured.Length > 1024 || captured.Any(c => c == null || (long)x * 32 + c.Index % 32 >= grid.TextureSize
                || (long)y * 32 + c.Index / 32 >= grid.TextureSize) || captured.Select(c => c.Index).Distinct().Count() != captured.Length)
            throw new ArgumentException("Invalid bounded chunk.");
        WorldId = worldId; Actor = actor; Grid = grid; X = x; Y = y; Cells = Array.AsReadOnly(captured);
    }
}

internal sealed class ActivityBatch
{
    internal IReadOnlyList<ActivityRecord> Activities { get; }
    internal IReadOnlyList<ExplorationChunk> Chunks { get; }
    internal IReadOnlyList<PlayerActionRecord> Actions { get; }
    internal IReadOnlyList<CoverageUpdate> Coverage { get; }
    internal IReadOnlyList<CookingChange> Cooking { get; }
    internal IReadOnlyList<ReportedSignalRecord> Signals { get; }
    internal ObservationPrincipal? SignalPrincipal { get; }
    internal int PhysicalDocumentCount { get; }

    internal ActivityBatch(ActivityRecord[]? activities = null, ExplorationChunk[]? chunks = null,
        PlayerActionRecord[]? actions = null,
        CoverageUpdate[]? coverage = null, CookingChange[]? cooking = null,
        ReportedSignalRecord[]? signals = null, ObservationPrincipal? signalPrincipal = null)
    {
        Activities = Capture(activities); Chunks = Capture(chunks);
        Actions = Capture(actions); Coverage = Capture(coverage, Enum.GetValues(typeof(ReportMetric)).Length * Enum.GetValues(typeof(CoverageReason)).Length);
        Cooking = Capture(cooking); Signals = Capture(signals);
        // One authenticated origin covers the whole signal array; beneficiaries may differ.
        if (Signals.Count != 0 && signalPrincipal == null) throw new ArgumentException("Signal work requires its authenticated origin.");
        SignalPrincipal = signalPrincipal;
        PhysicalDocumentCount = checked(Activities.Sum(x => x.Kind == ActivityKind.InferredPickup ? 2 : 1)
            + Chunks.Count + Chunks.Select(x => ObservationValues.Key(x.WorldId)).Distinct(StringComparer.Ordinal).Count()
            + Actions.Count + Signals.Count
            + Cooking.Sum(x => x.Kind == CookingChangeKind.Completed ? 2 : 1)
            + Coverage.Select(x => ObservationValues.Key(x.WorldId, x.Metric.ToString())).Distinct(StringComparer.Ordinal).Count());
    }

    private static IReadOnlyList<T> Capture<T>(T[]? values, int maximum = 128) where T : class
    {
        if (values != null && (values.Length > maximum || values.Any(x => x == null))) throw new ArgumentException("Invalid batch array.");
        return Array.AsReadOnly(values == null ? Array.Empty<T>() : (T[])values.Clone());
    }

}

internal static class ObservationValues
{
    internal const double MaxBowHitMeters = 250;
    internal static bool KnownBiome(string biome) => biome is "Meadows" or "Swamp" or "Mountain" or "BlackForest" or
        "Plains" or "AshLands" or "DeepNorth" or "Ocean" or "Mistlands";
    internal static void Text(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.IndexOf('\0') >= 0) throw new ArgumentException("A nonblank value without NUL is required.");
    }
    internal static void Utc(DateTime value)
    {
        if (value.Kind != DateTimeKind.Utc || value == DateTime.MinValue) throw new ArgumentException("A nondefault UTC timestamp is required.");
    }
    internal static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    internal static void Range(string world, DateTime start, DateTime end)
    {
        Text(world); Utc(start); Utc(end);
        if (end < start) throw new ArgumentException("The end must not precede the start.");
    }
    internal static string Key(params string[] values)
    {
        string input = string.Concat(values.Select(value => value.Length.ToString(CultureInfo.InvariantCulture) + ":" + value));
        using SHA256 hash = SHA256.Create();
        return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(input))).Replace("-", "");
    }
    internal static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    internal static DateTime Date(BsonValue value) => new(value.AsInt64, DateTimeKind.Utc);
    internal static long Add(long left, long right) => right > long.MaxValue - left ? long.MaxValue : left + right;
    internal static BsonDocument Document(string id, string world, DateTime utc, ActivityActor? actor = null)
    {
        BsonDocument result = new() { ["_id"] = id, ["WorldId"] = world, ["TimestampUtc"] = utc.Ticks };
        if (actor != null)
        {
            result["PlayerId"] = actor.PlayerId; result["PlayerKey"] = Key(actor.PlayerId);
            result["PlayerName"] = actor.PlayerName; result["NameObservedUtc"] = actor.NameObservedUtc.Ticks;
            result["ConnectionToken"] = actor.ConnectionToken;
        }
        return result;
    }
    internal static IEnumerable<BsonDocument> Period(ILiteCollection<BsonDocument> collection, string world, DateTime start, DateTime end, string order)
    {
        return collection.Query().Where(BsonExpression.Create("WorldId = @0 AND TimestampUtc >= @1 AND TimestampUtc < @2", world, start.Ticks, end.Ticks))
            .OrderBy(order).ToEnumerable().Where(x => string.Equals(x["WorldId"].AsString, world, StringComparison.Ordinal));
    }
    internal static void Index(ILiteCollection<BsonDocument> collection, bool player = true)
    {
        collection.EnsureIndex("WorldId"); collection.EnsureIndex("TimestampUtc");
        if (player) { collection.EnsureIndex("PlayerId"); collection.EnsureIndex("PlayerKey"); }
    }

    // Stream one exact-key group at a time, retaining only the top sixteen finished aggregates.
    internal static IReadOnlyList<ReportRow> Aggregate(IEnumerable<BsonDocument> documents, string keyField = "PlayerKey",
        string idField = "PlayerId", string nameField = "PlayerName", string? countField = null,
        string? hpField = null, string? freshField = null, string? metersField = null, string? secondsField = null, bool maximumMeters = false)
    {
        List<ReportRow> top = new();
        string? key = null;
        string id = "", name = "";
        long count = 0, fresh = 0; double hp = 0, meters = 0, seconds = 0;
        DateTime observed = DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);
        void Finish()
        {
            if (key == null) return;
            top.Add(new ReportRow(id, name, count, seconds, hp, observed, fresh, meters));
            top.Sort((a, b) =>
            {
                int comparison = secondsField != null ? b.Seconds.CompareTo(a.Seconds) : metersField != null ? b.Meters.CompareTo(a.Meters) : hpField != null ? b.HpLoss.CompareTo(a.HpLoss) : b.Count.CompareTo(a.Count);
                return comparison != 0 ? comparison : string.CompareOrdinal(a.Id, b.Id);
            });
            if (top.Count > 16) top.RemoveAt(16);
        }
        foreach (BsonDocument document in documents)
        {
            string next = document[keyField].AsString;
            if (!string.Equals(key, next, StringComparison.Ordinal))
            {
                Finish(); key = next; count = 0; fresh = 0; hp = 0; meters = 0; seconds = 0;
                id = document[idField].AsString; name = document[nameField].AsString;
                observed = DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);
            }
            DateTime time = Date(document.ContainsKey("NameObservedUtc") ? document["NameObservedUtc"] : document["TimestampUtc"]);
            if (time > observed || time == observed && string.CompareOrdinal(document[nameField].AsString, name) < 0)
            { name = document[nameField].AsString; observed = time; }
            count = Add(count, countField == null ? 1 : document[countField].AsInt64);
            if (freshField != null) fresh = Add(fresh, document[freshField].AsInt64);
            if (hpField != null) hp = Math.Min(double.MaxValue, hp + document[hpField].AsDouble);
            if (metersField != null) meters = maximumMeters ? Math.Max(meters, document[metersField].AsDouble) : Math.Min(double.MaxValue, meters + document[metersField].AsDouble);
            if (secondsField != null) seconds = Math.Min(double.MaxValue, seconds + document[secondsField].AsDouble);
        }
        Finish();
        return Array.AsReadOnly(top.ToArray());
    }
    internal static void Put(IDictionary<ReportMetric, IReadOnlyList<ReportRow>> rows, ReportMetric metric, IReadOnlyList<ReportRow> values)
    {
        if (values.Count != 0) rows[metric] = values;
    }

    internal static IEnumerable<BsonDocument> Merge(IEnumerable<BsonDocument> first, IEnumerable<BsonDocument>? second)
    {
        using var left = first.GetEnumerator();
        using var right = (second ?? Array.Empty<BsonDocument>()).GetEnumerator();
        bool a = left.MoveNext(), b = right.MoveNext();
        while (a || b)
        {
            if (a && (!b || string.CompareOrdinal(left.Current["PlayerKey"].AsString, right.Current["PlayerKey"].AsString) <= 0))
            { yield return left.Current; a = left.MoveNext(); }
            else { yield return right.Current; b = right.MoveNext(); }
        }
    }
}
