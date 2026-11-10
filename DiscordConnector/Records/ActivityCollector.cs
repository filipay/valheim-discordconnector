using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DiscordConnector.Records;

internal sealed partial class ActivityCollector
{
    private sealed class Item
    {
        internal FloorItemSignal Last = null!;
        internal FloorItemSignal? Request;
        internal bool Granted, Excluded, Removed, PlayerDrop;
        internal readonly List<FloorItemSignal> Mutations = new();
        internal readonly List<int> Units = new();
        internal double LastSeen;
    }
    private sealed class PieceCandidate
    {
        internal readonly double Born;
        internal PieceSignal Latest;
        internal PieceCandidate(PieceSignal s) { Born = s.Time; Latest = s; }
    }
    private sealed class Circle
    {
        internal readonly ExplorationSample Sample;
        internal readonly int X, Y, Radius;
        internal int Dx, Dy;
        internal Circle(ExplorationSample s, int x, int y, int radius)
        { Sample = s; X = x; Y = y; Radius = radius; Dx = -radius; Dy = -radius; }
    }
    private readonly Func<ActivityBatch, bool> _enqueue;
    private readonly Action<string> _warn;
    private readonly ObservationLoss _loss;
    private readonly bool _queueAccountsLoss;
    private readonly Dictionary<string, Item> _items = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _merges = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PieceCandidate> _pieces = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ShipSample> _ships = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Character, string Connection, double Health, DateTime Utc, double Time)> _health = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Character, ActivityActor Actor, double X, double Y, double Z, DateTime Utc, double Time)> _positions = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Account, string Biome), string> _biomeDays = new();
    private readonly Dictionary<string, double> _bosses = new(StringComparer.Ordinal);
    private readonly TimeZoneInfo? _timeZone;
    private readonly Dictionary<string, ActivityRecord> _spans = new(StringComparer.Ordinal);
    private readonly Queue<Circle> _circles = new();
    private readonly Dictionary<string, double> _exploredAt = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Account, string Grid, int X, int Y), bool[]> _cells = new();
    private readonly List<ActivityRecord> _activities = new();
    private readonly List<PlayerActionRecord> _actions = new();
    private readonly List<CookingChange> _cooking = new();
    private readonly List<ExplorationChunk> _chunks = new();
    private readonly Dictionary<ReportMetric, CoverageUpdate> _coverage = new();
    private readonly Dictionary<(ReportMetric Metric, CoverageReason Reason), long> _coverageCounts = new();
    private LeaderboardsOptions _options = new();
    private string _world = "", _grid = "";
    private bool _constructionReady, _gridMismatch;
    private CoverageReason? _explorationPauseReason;
    private int _physical, _mutations;
    private double _lastFlush;
    private double _nextPieceSweep;
    private long _sequence;
    private readonly string _session = Guid.NewGuid().ToString("N");
    private static readonly int MaxBufferedDocuments = 128 - Enum.GetValues(typeof(ReportMetric)).Length;
    internal Func<ActivityActor, bool> ActorIsCurrent { get; set; } = _ => false;
    internal Func<ActivityActor, string, bool> PositionIsCurrent { get; set; } = (_, _) => false;
    internal bool ConstructionReady => _constructionReady;
    internal bool ExplorationPaused => _gridMismatch;
    internal void Reject(string world, ReportMetric metric, ObservationRejection reason, long count = 1) => _loss.Record(world, metric, reason, count);

    internal ActivityCollector(Func<ActivityBatch, bool> enqueue, Action<string> warn, ObservationLoss? loss = null, string timeZoneId = "UTC")
    {
        _enqueue = enqueue; _warn = warn;
        var source = enqueue.Target as IObservationLossSource;
        _queueAccountsLoss = source != null; _loss = loss ?? source?.Loss ?? ObservationLoss.Process;
        try { _timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId); }
        catch (Exception error) when (error is ArgumentException || error is TimeZoneNotFoundException || error is InvalidTimeZoneException)
        { warn("Biome capture unavailable: invalid report timezone."); }
    }

    internal static bool NeedsPositionUpdates(LeaderboardsOptions options) => options.Sailing || options.Exploration || options.HealthLost || options.Biomes || options.BossKills || options.OnFoot || options.BiomeTime || options.PortalTrips || options.LongestBowHit;
    internal static bool NeedsZdoData(LeaderboardsOptions options) => options.FloorPickups || NeedsPositionUpdates(options);
    internal void DiscardPickupCandidates() { _items.Clear(); _merges.Clear(); _mutations = 0; }

    internal void ObserveItem(FloorItemSignal s)
    {
        if (!_options.FloorPickups || s.World != _world) return;
        string key = s.Item;
        if (!_items.TryGetValue(key, out var item))
        {
            if (_items.Count >= 4096) { Drop(ReportMetric.InferredPickups, s.Utc); return; }
            item = new Item { Last = s, LastSeen = s.Time };
            _items.Add(key, item);
        }
        item.LastSeen = s.Time;
        if (s.Kind == FloorItemEvent.PlayerDrop) { item.PlayerDrop = true; return; }
        if (s.Kind == FloorItemEvent.Excluded)
        { item.Excluded = true; MarkMerge(s.Prefab, s.Time); return; }
        if (s.Kind == FloorItemEvent.Request)
        {
            if (s.Actor == null || !ActorIsCurrent(s.Actor) || s.Stack <= 0 || s.Signature.Length == 0 || s.Owner == 0) return;
            if (item.Request != null && s.Time - item.Request.Time <= 7)
            {
                if (item.Request.Actor!.ConnectionToken != s.Actor.ConnectionToken) item.Excluded = true;
                return;
            }
            item.Request = s; item.Last = s; item.Granted = false; item.Removed = false;
            item.Excluded = false; _mutations -= item.Mutations.Count; item.Mutations.Clear(); item.Units.Clear();
            return;
        }
        var previous = item.Last;
        if (s.Kind == FloorItemEvent.Snapshot && s.Stack > previous.Stack && s.Prefab == previous.Prefab)
            MarkMerge(s.Prefab, s.Time);
        var request = item.Request;
        if (request == null) { item.Last = s; return; }
        if (item.Removed) return;
        if (s.Time < request.Time || s.Time - request.Time > 5 || s.Signature != request.Signature ||
            !ActorIsCurrent(request.Actor!) || s.Prefab != request.Prefab) { item.Excluded = true; return; }
        if (s.Kind == FloorItemEvent.Snapshot && !item.Granted && s.Owner == request.Owner &&
            s.Sender == request.Owner && s.Stack == previous.Stack && s.Revision >= previous.Revision)
        { item.Last = s; return; }
        if (s.Owner != request.Sender || s.Kind == FloorItemEvent.Snapshot && s.Owner != previous.Owner &&
            s.Sender != previous.Owner && previous.Owner != request.Sender)
        { item.Excluded = true; return; }
        if (s.Kind == FloorItemEvent.Snapshot)
        {
            // Owner-only grants can keep the data revision unchanged; they never prove a pickup.
            if (s.Owner == request.Sender && (s.Sender == previous.Owner || item.Granted && s.Sender == request.Sender)) item.Granted = true;
            if (s.Revision < previous.Revision || s.Revision == previous.Revision && s.Stack != previous.Stack)
            { item.Excluded = true; return; }
            if (s.Revision == previous.Revision) { item.Last = s; return; }
            if (s.Stack < previous.Stack && item.Granted && s.Sender == request.Sender)
                Stage(item, s, previous.Stack - s.Stack);
            item.Last = s;
        }
        else if (s.Kind == FloorItemEvent.Removed)
        {
            if (item.Granted && s.Sender == request.Sender && previous.Stack > 0 && previous.Time <= s.Time)
                Stage(item, s, previous.Stack);
            item.Removed = true;
        }
    }

    private void Stage(Item item, FloorItemSignal signal, int units)
    {
        if (item.Mutations.Count >= 128 || _mutations >= 4096)
        { item.Excluded = true; Drop(ReportMetric.InferredPickups, signal.Utc); return; }
        item.Mutations.Add(signal); item.Units.Add(units); _mutations++;
    }

    private void MarkMerge(string prefab, double time)
    {
        // shortcut: world/prefab-wide exclusions ignore metadata/quality; refine only if false negatives hurt coverage.
        if (prefab.Length == 0) return;
        if (_merges.ContainsKey(prefab) || _merges.Count < 4096) _merges[prefab] = time;
        else foreach (var item in _items.Values) item.Excluded = true;
    }

    internal void ObservePiece(PieceSignal s)
    {
        if (!_options.Construction || !_constructionReady || s.World != _world) return;
        if (!_pieces.TryGetValue(s.Piece, out var candidate))
        {
            if (!s.NewLifecycle) return;
            if (_pieces.Count >= 4096) { Drop(ReportMetric.NewPieces, s.Utc); return; }
            _pieces.Add(s.Piece, new PieceCandidate(s));
        }
        else candidate.Latest = s;
    }

    internal void DiscardPiece(string piece) => _pieces.Remove(piece);

    internal void ObserveCrop(string world, string id, string prefab, ActivityActor actor, DateTime utc)
    {
        if (_options.CropsPlanted && world == _world && ActorIsCurrent(actor))
            Add(new ActivityRecord(ActivityKind.CropPlanted, world, actor, id, id, utc, prefab));
    }

    internal void ObserveAction(PlayerActionRecord action)
    {
        if (action.WorldId != _world || !ActorIsCurrent(action.Actor) || !_options.DodgeRolls) return;
        if (!Reserve(1))
        { _loss.Record(action.WorldId, ReportMetric.ObservedDodgeRolls, ObservationRejection.QueueFull); return; }
        _actions.Add(action);
    }

    internal void ObserveCreature(ActivityRecord record)
    {
        if (record.WorldId == _world && (record.Kind == ActivityKind.CreatureDamage && _options.DamageDealt ||
            record.Kind == ActivityKind.CreatureKill && _options.ReportedKillCredits || record.Kind == ActivityKind.BowHit && _options.LongestBowHit)) Add(record);
    }

    internal bool ObserveCooking(CookingChange change)
    {
        if (!Reserve(change.Kind == CookingChangeKind.Completed ? 2 : 1))
        {
            if (change.Kind == CookingChangeKind.Completed) _loss.Record(change.Snapshot.WorldId, ReportMetric.CookingCompletions, ObservationRejection.QueueFull);
            return false;
        }
        _cooking.Add(change); return true;
    }

    internal void SampleShip(ShipSample s)
    {
        if (!_options.Sailing || s.World != _world) return;
        string key = s.Ship;
        if (!s.Fresh || !s.Onboard || s.Owner == 0 || !Finite(s.X, s.Z, s.Time) || !ActorIsCurrent(s.Actor))
        { CloseSpan(key); _ships.Remove(key); return; }
        if (!_ships.TryGetValue(key, out var before))
        {
            if (_ships.Count >= 4096) { Drop(ReportMetric.SailingDistance, s.Utc); return; }
            _ships[key] = s; return;
        }
        double elapsed = s.Time - before.Time;
        if (elapsed < 1 && elapsed > 0) return;
        _ships[key] = s;
        double wall = (s.Utc - before.Utc).TotalSeconds;
        double distance = Math.Sqrt((s.X - before.X) * (s.X - before.X) + (s.Z - before.Z) * (s.Z - before.Z));
        // Keep the existing 2.5s freshness window; 20m/s allows ship/network jitter but rejects teleport segments.
        if (before.Owner != s.Owner || before.Actor.ConnectionToken != s.Actor.ConnectionToken || before.Actor.PlayerId != s.Actor.PlayerId ||
            elapsed <= 0 || elapsed > 2.5 || wall <= 0 || wall > 2.5 || Math.Abs(wall - elapsed) > .25 ||
            !ObservationValues.Finite(distance) || distance / elapsed < .5 || distance / elapsed > 20)
        { CloseSpan(key); return; }
        if (_spans.TryGetValue(key, out var span) && span.TimestampUtc == before.Utc && span.Seconds + elapsed <= 10 &&
            (s.Utc - span.StartUtc).TotalSeconds <= 10)
            _spans[key] = new ActivityRecord(ActivityKind.Sailing, s.World, s.Actor, s.Ship, span.Token, s.Utc,
                startUtc: span.StartUtc, seconds: span.Seconds + elapsed, meters: span.Meters + distance);
        else
        {
            CloseSpan(key);
            _spans[key] = new ActivityRecord(ActivityKind.Sailing, s.World, s.Actor, s.Ship, Token(), s.Utc,
                startUtc: before.Utc, seconds: elapsed, meters: distance);
        }
    }

    internal void InvalidateShip(string ship) { CloseSpan(ship); _ships.Remove(ship); }

    internal void SampleHealth(string world, string character, ActivityActor actor, double health, DateTime utc, double time, bool fresh)
    {
        if (!_options.HealthLost || world != _world) return;
        ObservationValues.Text(character); ObservationValues.Utc(utc);
        string key = actor.PlayerId;
        if (!fresh || !ObservationValues.Finite(health) || !ObservationValues.Finite(time) || time < 0 || health <= 0 || !ActorIsCurrent(actor))
        { _health.Remove(key); return; }
        bool sampled = _health.TryGetValue(key, out var before);
        if (!sampled && _health.Count >= 4096) { Drop(ReportMetric.HealthLost, utc); return; }
        _health[key] = (character, actor.ConnectionToken, health, utc, time);
        double elapsed = time - before.Time, wall = (utc - before.Utc).TotalSeconds;
        if (!sampled || before.Character != character || before.Connection != actor.ConnectionToken ||
            elapsed <= 0 || elapsed > 2.5 || wall <= 0 || wall > 2.5 || Math.Abs(wall - elapsed) > .25 || health >= before.Health) return;
        // Net sampled loss only: healing between samples and player-owned replication lag can hide hits.
        Add(new ActivityRecord(ActivityKind.HealthLost, world, actor, character, Token(), utc, hpLoss: before.Health - health));
    }

    internal void SampleBiome(string world, ActivityActor actor, string biome, DateTime utc, double time, bool fresh)
    {
        if (!_options.Biomes || _timeZone == null || world != _world || !fresh || !ObservationValues.Finite(time) || time < 0 || !ActorIsCurrent(actor)) return;
        ObservationValues.Utc(utc); ObservationValues.Text(biome);
        string day = TimeZoneInfo.ConvertTimeFromUtc(utc, _timeZone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var key = (actor.PlayerId, biome);
        if (_biomeDays.TryGetValue(key, out string? previous) && previous == day) return;
        if (!_biomeDays.ContainsKey(key) && _biomeDays.Count >= 4096) { Drop(ReportMetric.Biomes, utc); return; }
        _biomeDays[key] = day;
        Add(new ActivityRecord(ActivityKind.BiomeVisited, world, actor, biome, day, utc, biome));
    }

    internal void SamplePlayerPosition(string world, string character, ActivityActor actor, double x, double y, double z, DateTime utc, double time, bool fresh)
    {
        if (!_options.BossKills || world != _world) return;
        if (!fresh || !Finite(x, z, time) || !ObservationValues.Finite(y) || time < 0 || !ActorIsCurrent(actor))
        { _positions.Remove(actor.PlayerId); return; }
        ObservationValues.Utc(utc);
        if (!_positions.ContainsKey(actor.PlayerId) && _positions.Count >= 256) { Drop(ReportMetric.ParticipantBossCredits, utc); return; }
        _positions[actor.PlayerId] = (character, actor, x, y, z, utc, time);
    }

    internal static bool IsBossKey(string key) => key is "defeated_eikthyr" or "defeated_gdking" or "defeated_bonemass" or
        "defeated_dragon" or "defeated_goblinking" or "defeated_queen" or "defeated_fader";

    internal ActivityActor[] BossRecipients(ActivityActor sender, DateTime utc, double time)
    {
        bool Fresh((string Character, ActivityActor Actor, double X, double Y, double Z, DateTime Utc, double Time) p) =>
            time >= p.Time && time - p.Time <= 2.5 && utc >= p.Utc && (utc - p.Utc).TotalSeconds <= 2.5 &&
            ActorIsCurrent(p.Actor) && PositionIsCurrent(p.Actor, p.Character);
        if (!_options.BossKills || !_positions.TryGetValue(sender.PlayerId, out var origin) || origin.Actor.ConnectionToken != sender.ConnectionToken || !Fresh(origin)) return Array.Empty<ActivityActor>();
        return _positions.Values.Where(p => Fresh(p) && Math.Pow(p.X - origin.X, 2) + Math.Pow(p.Y - origin.Y, 2) + Math.Pow(p.Z - origin.Z, 2) <= 2500)
            .Select(p => p.Actor).ToArray();
    }

    internal void ObserveBoss(string world, string key, ActivityActor[] recipients, DateTime utc, double time)
    {
        if (!_options.BossKills || world != _world || !IsBossKey(key) || recipients.Length == 0 || !ObservationValues.Finite(time) || time < 0) return;
        if (_bosses.TryGetValue(key, out double last) && time - last < 60) return;
        _bosses[key] = time;
        string token = Token();
        foreach (var actor in recipients)
            if (ActorIsCurrent(actor)) Add(new ActivityRecord(ActivityKind.BossCredit, world, actor, key, token, utc, key));
    }

    internal void PauseExploration(DateTime utc, CoverageReason reason)
    {
        if (_gridMismatch) return;
        _gridMismatch = true; _explorationPauseReason = reason;
        _circles.Clear(); _exploredAt.Clear(); _cells.Clear();
        _physical -= _chunks.Count * 2; _chunks.Clear();
        Coverage(ReportMetric.ExplorationCells, CoverageState.Unavailable, utc, reason);
    }

    internal void SampleExploration(ExplorationSample s)
    {
        if (!_options.Exploration || _gridMismatch || s.World != _world || !s.Fresh || !Finite(s.X, s.Z, s.Time) || !ActorIsCurrent(s.Actor)) return;
        if (_grid.Length != 0 && _grid != s.Grid.Id)
        {
            PauseExploration(s.Utc, CoverageReason.GridMismatch); return;
        }
        _grid = s.Grid.Id;
        if (_exploredAt.TryGetValue(s.Actor.ConnectionToken, out double last) && s.Time - last < 2) return;
        if (_circles.Count >= 4096 || _exploredAt.Count >= 4096 && !_exploredAt.ContainsKey(s.Actor.ConnectionToken))
        { Drop(ReportMetric.ExplorationCells, s.Utc); return; }
        _exploredAt[s.Actor.ConnectionToken] = s.Time;
        double x = Math.Round(s.X / s.Grid.PixelSize + s.Grid.TextureSize / 2, MidpointRounding.ToEven);
        double y = Math.Round(s.Z / s.Grid.PixelSize + s.Grid.TextureSize / 2, MidpointRounding.ToEven);
        int radius = (int)Math.Ceiling(s.Grid.ExploreRadius / s.Grid.PixelSize);
        if (x < -radius || y < -radius || x >= s.Grid.TextureSize + radius || y >= s.Grid.TextureSize + radius) return;
        _circles.Enqueue(new Circle(s, (int)x, (int)y, radius));
    }

    private void Paint(double now)
    {
        int visits = 4096;
        while (visits > 0 && _circles.Count != 0)
        {
            var c = _circles.Peek(); var s = c.Sample;
            if (now - s.Time > 2.5 || now < s.Time || !ActorIsCurrent(s.Actor)) { _circles.Dequeue(); continue; }
            var chunks = new Dictionary<(int, int), List<ExplorationCell>>();
            while (visits-- > 0 && c.Dy <= c.Radius)
            {
                int dx = c.Dx, dy = c.Dy;
                if (++c.Dx > c.Radius) { c.Dx = -c.Radius; c.Dy++; }
                int x = c.X + dx, y = c.Y + dy;
                if (dx * dx + dy * dy > c.Radius * c.Radius || x < 0 || y < 0 || x >= s.Grid.TextureSize || y >= s.Grid.TextureSize) continue;
                int cx = x / 32, cy = y / 32, index = y % 32 * 32 + x % 32;
                var key = (s.Actor.PlayerId, s.Grid.Id, cx, cy);
                if (!_cells.TryGetValue(key, out var bits))
                {
                    // Bounded hot cache only; durable chunk membership remains authoritative after eviction/restart.
                    if (_cells.Count == 4096) _cells.Remove(_cells.Keys.First());
                    bits = new bool[1024]; _cells.Add(key, bits);
                }
                if (bits[index]) continue;
                bits[index] = true;
                if (!chunks.TryGetValue((cx, cy), out var cells)) chunks.Add((cx, cy), cells = new List<ExplorationCell>());
                cells.Add(new ExplorationCell(index, s.Utc));
            }
            int remaining = chunks.Count;
            foreach (var chunk in chunks)
            {
                if (!Reserve(2)) { _loss.Record(s.World, ReportMetric.ExplorationCells, ObservationRejection.QueueFull, remaining); _cells.Clear(); break; }
                _chunks.Add(new ExplorationChunk(s.World, s.Actor, s.Grid, chunk.Key.Item1, chunk.Key.Item2, chunk.Value));
                remaining--;
            }
            if (c.Dy > c.Radius && _circles.Count != 0 && ReferenceEquals(_circles.Peek(), c)) _circles.Dequeue();
        }
    }

    internal void Tick(DateTime utc, double monotonicSeconds, string world, LeaderboardsOptions options,
        CoverageReason? explorationPause = null)
    {
        if (_world != world) { Reset(); _world = world; }
        UpdateTravelOptions(options);
        foreach (string key in _travel.Where(pair => monotonicSeconds < pair.Value.Time || monotonicSeconds - pair.Value.Time > 2.5)
            .Select(pair => pair.Key).ToArray()) _travel.Remove(key);
        if (explorationPause.HasValue) PauseExploration(utc, explorationPause.Value);
        if (_options.FloorPickups != options.FloorPickups) DiscardPickupCandidates();
        if (_options.Sailing && !options.Sailing) { foreach (string key in _spans.Keys.ToArray()) CloseSpan(key); _ships.Clear(); }
        if (_options.HealthLost && !options.HealthLost) _health.Clear();
        if (_options.BossKills && !options.BossKills) _positions.Clear();
        if (_options.Construction != options.Construction || options.Construction && !_constructionReady)
        {
            // Only successful creation hooks open candidates; existing ZDO state never seeds this epoch.
            _pieces.Clear(); _constructionReady = options.Construction;
        }
        if (_options.Exploration && !options.Exploration) { _circles.Clear(); _exploredAt.Clear(); }
        bool disabled = _options.FloorPickups && !options.FloorPickups || _options.Sailing && !options.Sailing ||
            _options.Construction && !options.Construction || _options.Exploration && !options.Exploration ||
            _options.DodgeRolls && !options.DodgeRolls || _options.CropsPlanted && !options.CropsPlanted || _options.HealthLost && !options.HealthLost ||
            _options.Biomes && !options.Biomes || _options.BossKills && !options.BossKills ||
            _options.OnFoot && !options.OnFoot || _options.BiomeTime && !options.BiomeTime ||
            _options.OnlineTime && !options.OnlineTime || _options.PortalTrips && !options.PortalTrips || _options.DamageDealt && !options.DamageDealt ||
            _options.LongestBowHit && !options.LongestBowHit;
        _options = options;
        if (disabled) FlushThrough(utc);
        Mature(utc, monotonicSeconds);
        foreach (string key in _exploredAt.Where(x => monotonicSeconds < x.Value || monotonicSeconds - x.Value > 2.5)
            .Select(x => x.Key).Take(32).ToArray()) _exploredAt.Remove(key);
        if (monotonicSeconds >= _nextPieceSweep || monotonicSeconds < _nextPieceSweep - 1)
        {
            _nextPieceSweep = monotonicSeconds + 1;
            foreach (var pair in _pieces.ToArray())
            {
                var c = pair.Value; var s = c.Latest;
                if (monotonicSeconds >= c.Born && monotonicSeconds - c.Born < 30) continue;
                _pieces.Remove(pair.Key);
                if (monotonicSeconds < c.Born || !s.IsPiece || s.Excluded || s.Actor == null || !ActorIsCurrent(s.Actor) || monotonicSeconds - s.Time > 2.5) continue;
                Add(new ActivityRecord(ActivityKind.NewPiece, s.World, s.Actor, s.Piece, Token(), s.Utc, s.Prefab));
            }
        }
        foreach (string key in _ships.Where(x => monotonicSeconds - x.Value.Time > 2.5).Select(x => x.Key).ToArray())
        { CloseSpan(key); _ships.Remove(key); }
        foreach (string key in _health.Where(x => monotonicSeconds < x.Value.Time || monotonicSeconds - x.Value.Time > 2.5)
            .Select(x => x.Key).Take(32).ToArray()) _health.Remove(key);
        foreach (string key in _positions.Where(x => monotonicSeconds < x.Value.Time || monotonicSeconds - x.Value.Time > 2.5)
            .Select(x => x.Key).Take(32).ToArray()) _positions.Remove(key);
        Paint(monotonicSeconds);
        if (monotonicSeconds < _lastFlush || monotonicSeconds - _lastFlush >= 10)
        { FlushThrough(utc); _lastFlush = monotonicSeconds; }
    }

    private void Mature(DateTime utc, double time)
    {
        foreach (var pair in _items.ToArray())
        {
            var item = pair.Value; var request = item.Request;
            if (request != null)
            {
                if (_merges.TryGetValue(request.Prefab, out double merge) && merge >= request.Time - 2 && merge <= time) item.Excluded = true;
                for (int i = 0; i < item.Mutations.Count;)
                {
                    var mutation = item.Mutations[i];
                    if (time - mutation.Time < 2) { i++; continue; }
                    int units = item.Units[i]; item.Mutations.RemoveAt(i); item.Units.RemoveAt(i); _mutations--;
                    if (!item.Excluded && ActorIsCurrent(request.Actor!))
                        Add(new ActivityRecord(ActivityKind.InferredPickup, request.World, request.Actor!, request.Item,
                            mutation.Token, mutation.Utc, request.Prefab, units, provenance:
                            item.PlayerDrop || request.Provenance == ItemProvenance.PlayerDropOrRefund ? ItemProvenance.PlayerDropOrRefund : ItemProvenance.UnknownMixed));
                    if (!_items.ContainsKey(pair.Key)) return; // A rejected flush discarded all transient candidates.
                }
                if (time < request.Time || time - request.Time >= 7)
                { item.Request = null; _mutations -= item.Mutations.Count; item.Mutations.Clear(); item.Units.Clear(); }
            }
            if (time < item.LastSeen || time - item.LastSeen >= 7) _items.Remove(pair.Key);
        }
        foreach (string key in _merges.Where(x => time - x.Value >= 7 || time < x.Value).Select(x => x.Key).ToArray()) _merges.Remove(key);
    }

    internal void Coverage(ReportMetric metric, CoverageState state, DateTime utc, CoverageReason? reason = null, long count = 1)
    {
        if (_world.Length == 0) return;
        if (metric == ReportMetric.ExplorationCells && _gridMismatch &&
            (state == CoverageState.Observing || state == CoverageState.Partial)) state = CoverageState.Unavailable;
        if (metric == ReportMetric.ExplorationCells && _gridMismatch && state == CoverageState.Unavailable)
            reason = _explorationPauseReason;
        var update = new CoverageUpdate(_world, metric, state, utc, reason, count);
        if (reason.HasValue)
        {
            var key = (metric, reason.Value);
            _coverageCounts.TryGetValue(key, out long previous);
            _coverageCounts[key] = ObservationValues.Add(previous, count);
        }
        _coverage[metric] = update;
    }
    private void Drop(ReportMetric metric, DateTime utc)
    { Coverage(metric, CoverageState.Partial, utc, CoverageReason.Dropped); }
    private void Add(ActivityRecord record)
    {
        if (Reserve(record.Kind == ActivityKind.InferredPickup ? 2 : 1)) _activities.Add(record);
        else
        {
            _loss.Record(record.WorldId, ObservationLoss.Metric(record.Kind), ObservationRejection.QueueFull);
        }
    }
    private void CloseSpan(string key)
    { if (_spans.TryGetValue(key, out var span)) { _spans.Remove(key); Add(span); } }
    private bool Reserve(int physical)
    { if (_physical + physical > MaxBufferedDocuments && !Flush()) return false; _physical += physical; return true; }
    private string Token() => _session + ":" + ObservationValues.Number(checked(++_sequence));
    private static bool Finite(double x, double z, double time) => ObservationValues.Finite(x) && ObservationValues.Finite(z) && ObservationValues.Finite(time);

    internal void FlushThrough(DateTime endUtc)
    { FlushOnline(); foreach (string key in _spans.Keys.ToArray()) CloseSpan(key); Flush(); }

    private bool Flush()
    {
        if (_physical == 0 && _coverage.Count == 0) return true;
        var updates = new List<CoverageUpdate>();
        foreach (var update in _coverage.Values)
        {
            var counts = _coverageCounts.Where(x => x.Key.Metric == update.Metric).ToArray();
            if (counts.Length == 0) updates.Add(new CoverageUpdate(update.WorldId, update.Metric, update.State, update.TimestampUtc));
            foreach (var pair in counts) updates.Add(new CoverageUpdate(update.WorldId, update.Metric, update.State,
                update.TimestampUtc, pair.Key.Reason, pair.Value));
        }
        var batch = new ActivityBatch(activities: _activities.ToArray(), chunks: _chunks.ToArray(),
            actions: _actions.ToArray(), cooking: _cooking.ToArray(), coverage: updates.ToArray());
        _activities.Clear(); _chunks.Clear(); _actions.Clear(); _cooking.Clear(); _coverage.Clear(); _coverageCounts.Clear(); _physical = 0;
        bool ambiguous = false;
        try { if (_enqueue(batch)) return true; }
        catch { ambiguous = true; }
        if (!_queueAccountsLoss) _loss.Batch(batch, ambiguous ? null : ObservationRejection.QueueFull);
        foreach (var span in _spans.Values) _loss.Record(span.WorldId, ReportMetric.SailingDistance, ObservationRejection.QueueFull);
        DiscardPickupCandidates(); _pieces.Clear(); _ships.Clear(); _health.Clear(); _spans.Clear(); _circles.Clear(); _cells.Clear();
        _biomeDays.Clear(); _positions.Clear();
        ClearTravel();
        try { _warn("Activity queue refused observations; no automatic replay."); } catch { }
        return false;
    }

    internal void Reset()
    {
        FlushThrough(DateTime.UtcNow);
        DiscardPickupCandidates(); _pieces.Clear(); _ships.Clear(); _health.Clear(); _spans.Clear(); _circles.Clear();
        _biomeDays.Clear(); _positions.Clear(); _bosses.Clear();
        ClearTravel();
        _nextPieceSweep = 0;
        _cells.Clear(); _exploredAt.Clear(); _constructionReady = false; _grid = ""; _gridMismatch = false; _explorationPauseReason = null; _world = "";
    }
}
