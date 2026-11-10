using System;
using System.Collections.Generic;
using System.Linq;

using DiscordConnector.Records;

using UnityEngine;

namespace DiscordConnector.Patches;

internal sealed class ActivityRuntime : IDisposable
{
    private sealed class Freshness
    {
        internal uint Revision;
        internal long Owner;
        internal double Time;
        internal bool Consumed;
    }
    internal static ActivityRuntime? Current { get; private set; }
    internal readonly ActivityCollector Collector;
    internal readonly CookingTracker Cooking;
    internal readonly SimulationCombatCollector Simulation;
    private readonly Func<LeaderboardsOptions> _options;
    private readonly Action<string> _warn;
    private readonly Func<string, CoverageReason?>? _explorationPauseReason;
    private readonly Dictionary<string, Freshness> _fresh = new(StringComparer.Ordinal);
    private readonly ActivitySampleCadence _sampled = new();
    private readonly Dictionary<ZDOID, (double Born, bool Construction, bool Crop)> _newPieces = new();
    private readonly Queue<ZDOID> _pieceQueue = new();
    private readonly Dictionary<int, bool> _cropPrefabs = new();
    private readonly Dictionary<ReportMetric, CoverageState> _states = new();
    private string _world = "";
    private bool _construction, _cropsPlanted, _disposed;
    private int _sampleCursor;
    internal LeaderboardsOptions Options => _options();
    internal static double Now => Time.realtimeSinceStartupAsDouble;

    internal ActivityRuntime(ActivityCollector collector, Func<LeaderboardsOptions> options, Action<string> warn,
        long cookingObservationEpoch,
        Func<string, CoverageReason?>? explorationPauseReason = null)
    {
        if (Current != null) throw new InvalidOperationException("Only one activity runtime may own capture.");
        Collector = collector; _options = options; _warn = warn;
        _explorationPauseReason = explorationPauseReason;
        Collector.ActorIsCurrent = StablePlayerResolver.Current;
        Collector.PositionIsCurrent = (actor, character) => StablePlayerResolver.Current(actor) &&
            ZNet.instance.GetPeers().Any(peer => peer.m_characterID.ToString() == character && peer.m_socket?.GetHostName() == actor.PlayerId);
        Cooking = new CookingTracker(cookingObservationEpoch, Collector.ObserveCooking, warn);
        Simulation = new SimulationCombatCollector(Collector);
        Current = this;
    }

    internal void Update(DateTime utc, double monotonicSeconds)
    {
        if (_disposed) return;
        try
        {
            if (!StablePlayerResolver.World(out string world)) { Suspend(); return; }
            if (_world != world) ChangeWorld(world);
            var options = Options;
            if (!ActivityCollector.NeedsPositionUpdates(options)) _fresh.Clear();
            Cooking.SetAvailability(options.CookingCompletions);
            Collector.Tick(utc, monotonicSeconds, world, options, _explorationPauseReason?.Invoke(world));
            Simulation.Tick(SimulationCombatPatches.Present && options.ReportedKillCredits, monotonicSeconds);
            State(ReportMetric.DamageDealt, !options.DamageDealt ? CoverageState.Disabled : SimulationCombatPatches.Present ? CoverageState.Partial : CoverageState.Unavailable, utc);
            State(ReportMetric.LongestBowHit, !options.LongestBowHit ? CoverageState.Disabled : SimulationCombatPatches.Present ? CoverageState.Partial : CoverageState.Unavailable, utc);
            _sampled.Sweep(monotonicSeconds);
            State(ReportMetric.CookingCompletions, options.CookingCompletions ? CoverageState.Partial : CoverageState.Disabled, utc);
            State(ReportMetric.InferredPickups, options.FloorPickups ? CoverageState.Partial : CoverageState.Disabled, utc);
            State(ReportMetric.ObservedDodgeRolls, options.DodgeRolls ? CoverageState.Partial : CoverageState.Disabled, utc);
            State(ReportMetric.SailingDistance, options.Sailing ? CoverageState.Partial : CoverageState.Disabled, utc);
            State(ReportMetric.HealthLost, options.HealthLost ? CoverageState.Partial : CoverageState.Disabled, utc);
            State(ReportMetric.OnFootDistance, options.OnFoot ? CoverageState.Partial : CoverageState.Disabled, utc);
            State(ReportMetric.PortalTrips, options.PortalTrips ? CoverageState.Partial : CoverageState.Disabled, utc);
            State(ReportMetric.BiomeTime, !options.BiomeTime ? CoverageState.Disabled : WorldGenerator.instance == null ? CoverageState.Unavailable : CoverageState.Partial, utc);
            State(ReportMetric.OnlineTime, options.OnlineTime ? CoverageState.Partial : CoverageState.Disabled, utc);
            if (options.OnlineTime)
            {
                bool available = StablePlayerResolver.NativeSignalSnapshot(utc, out var online, out _);
                Collector.SampleOnline(world, available ? online.Where(peer => peer.Ready && peer.Actor != null).Select(peer => peer.Actor!).ToArray() : Array.Empty<ActivityActor>(), utc, monotonicSeconds);
            }
            State(ReportMetric.Biomes, !options.Biomes ? CoverageState.Disabled : WorldGenerator.instance == null ? CoverageState.Unavailable : CoverageState.Partial, utc);
            State(ReportMetric.ParticipantBossCredits, !options.BossKills ? CoverageState.Disabled : !ZoneSystem.instance ? CoverageState.Unavailable : CoverageState.Partial, utc);
            if (options.Construction != _construction || options.CropsPlanted != _cropsPlanted)
            {
                if (!options.Construction && !options.CropsPlanted) { _newPieces.Clear(); _pieceQueue.Clear(); }
                else foreach (var id in _newPieces.Keys.ToArray())
                {
                    var candidate = _newPieces[id];
                    candidate.Construction &= options.Construction; candidate.Crop &= options.CropsPlanted;
                    _newPieces[id] = candidate;
                }
            }
            _construction = options.Construction;
            _cropsPlanted = options.CropsPlanted;
            State(ReportMetric.CropsPlanted, options.CropsPlanted ? CoverageState.Partial : CoverageState.Disabled, utc);
            State(ReportMetric.NewPieces, !options.Construction ? CoverageState.Disabled :
                Collector.ConstructionReady ? CoverageState.Partial : CoverageState.Unavailable, utc);
            SamplePieces(utc, monotonicSeconds);
            ExplorationGrid? grid = options.Exploration && !Collector.ExplorationPaused ? RuntimeGrid() : null;
            State(ReportMetric.ExplorationCells, !options.Exploration ? CoverageState.Disabled :
                grid == null ? CoverageState.Unavailable : CoverageState.Partial, utc);
            SamplePlayersAndShips(utc, monotonicSeconds, options, grid);
            List<string>? expired = null;
            foreach (var entry in _fresh)
            {
                if (monotonicSeconds >= entry.Value.Time && monotonicSeconds - entry.Value.Time <= 2.5) continue;
                (expired ??= new List<string>()).Add(entry.Key);
                if (expired.Count == 32) break;
            }
            if (expired != null) foreach (string id in expired) _fresh.Remove(id);
        }
        catch (Exception error) { Failed(error); }
    }

    private void State(ReportMetric metric, CoverageState state, DateTime utc)
    {
        if (_states.TryGetValue(metric, out var old) && old == state) return;
        _states[metric] = state;
        Collector.Coverage(metric, state, utc, state == CoverageState.Unavailable ? CoverageReason.CapabilityLost : null);
        if (state == CoverageState.Unavailable)
            _warn(metric == ReportMetric.ExplorationCells ? Collector.ExplorationPaused
                ? "Exploration paused by grid validation or bounded worker feedback."
                : "Exploration unavailable: no supported active runtime grid; no default substituted."
                : metric is ReportMetric.Biomes or ReportMetric.BiomeTime ? "Biomes unavailable: world generator is not initialized."
                : metric == ReportMetric.ParticipantBossCredits ? "Boss credits unavailable: zone system is not initialized."
                : metric is ReportMetric.DamageDealt or ReportMetric.LongestBowHit ? "Dedicated Simulation creature statistics unavailable: the mod is not detected."
                : "Construction unavailable until the capture epoch starts.");
    }

    private void ChangeWorld(string world)
    {
        try { Suspend(); }
        finally { _world = world; ActivityPatches.ClearIngress(); }
    }

    internal void ResetSession(string world) => ChangeWorld(world);

    internal void Suspend()
    {
        try { Cooking.Reset(); Simulation.Reset(); }
        finally
        {
            try { Collector.Reset(); }
            finally
            {
                _fresh.Clear(); _sampled.Clear(); _newPieces.Clear(); _pieceQueue.Clear(); _cropPrefabs.Clear();
                _states.Clear(); _construction = _cropsPlanted = false; _sampleCursor = 0;
                ActivityPatches.InvalidateIngress();
            }
        }
    }

    internal void NewZdo(string world, ZDOID id, DateTime utc, double time, bool excluded)
    {
        var options = Options;
        if (world != _world || !(options.Construction && Collector.ConstructionReady || options.CropsPlanted) || excluded || _newPieces.ContainsKey(id)) return;
        if (_newPieces.Count >= 4096)
        {
            if (options.Construction) Collector.Coverage(ReportMetric.NewPieces, CoverageState.Partial, utc, CoverageReason.Dropped);
            if (options.CropsPlanted) Collector.Coverage(ReportMetric.CropsPlanted, CoverageState.Partial, utc, CoverageReason.Dropped);
            return;
        }
        // Save/chunk loading uses ZDOPool directly; scene streaming does not create a new ZDO.
        _newPieces.Add(id, (time, options.Construction, options.CropsPlanted)); _pieceQueue.Enqueue(id);
        if (options.Construction)
            Collector.ObservePiece(new PieceSignal(world, id.ToString(), "", null, utc, time, true, false, false));
    }

    private void SamplePieces(DateTime utc, double time)
    {
        int count = Math.Min(32, _pieceQueue.Count);
        for (int i = 0; i < count; i++)
        {
            ZDOID id = _pieceQueue.Dequeue();
            if (!_newPieces.TryGetValue(id, out var candidate)) continue;
            var zdo = ZDOMan.instance.GetZDO(id);
            if (zdo == null || time - candidate.Born > 32 || time < candidate.Born) { _newPieces.Remove(id); continue; }
            if (ZNetScene.instance)
            {
                var prefab = ZNetScene.instance.GetPrefab(zdo.GetPrefab());
                if (!prefab) { _pieceQueue.Enqueue(id); continue; }
                var piece = prefab.GetComponent<Piece>();
                bool construction = _construction && candidate.Construction && piece;
                bool crop = _cropsPlanted && candidate.Crop && IsCrop(zdo.GetPrefab());
                if (!construction && !crop)
                { _newPieces.Remove(id); Collector.DiscardPiece(id.ToString()); continue; }
                if (crop &&
                    StablePlayerResolver.Creator(zdo, utc, out var creator) && creator != null)
                {
                    Collector.ObserveCrop(_world, id.ToString(), ZNetScene.instance.GetPrefab(zdo.GetPrefab()).name, creator, utc);
                    candidate.Crop = false; _newPieces[id] = candidate;
                }
                if (!construction && !candidate.Crop)
                { _newPieces.Remove(id); continue; }
                if (!construction) { _pieceQueue.Enqueue(id); continue; }
                if (piece)
                {
                    StablePlayerResolver.Creator(zdo, utc, out var actor);
                    var instance = ZNetScene.instance.FindInstance(zdo);
                    bool ghost = instance && Player.IsPlacementGhost(instance.gameObject);
                    Collector.ObservePiece(new PieceSignal(_world, id.ToString(), prefab.name, actor, utc, time,
                        false, true, ghost));
                }
            }
            _pieceQueue.Enqueue(id);
        }
    }

    private bool IsCrop(int hash)
    {
        if (_cropPrefabs.TryGetValue(hash, out bool crop)) return crop;
        var prefab = ZNetScene.instance.GetPrefab(hash);
        var plant = prefab ? prefab.GetComponent<Plant>() : null;
        crop = plant != null && prefab != null && prefab.GetComponent<Piece>() && plant.m_grownPrefabs != null && plant.m_grownPrefabs.Length > 0;
        if (crop)
            foreach (var grown in plant!.m_grownPrefabs!)
                if (!grown || !grown.GetComponent<Pickable>()) { crop = false; break; }
        if (_cropPrefabs.Count < 4096) _cropPrefabs.Add(hash, crop);
        return crop;
    }

    private void SamplePlayersAndShips(DateTime utc, double time, LeaderboardsOptions options, ExplorationGrid? grid)
    {
        var peers = ZNet.instance.GetPeers(); var ships = Ship.Instances;
        int count = peers.Count + ships.Count;
        for (int units = 0; units < Math.Min(32, count); units++)
        {
            int index = _sampleCursor++ % count;
            if (_sampleCursor == int.MaxValue) _sampleCursor = 0;
            if (index < peers.Count)
            {
                bool travel = options.OnFoot || options.BiomeTime || options.PortalTrips;
                if (grid == null && !options.HealthLost && !options.Biomes && !options.BossKills && !travel) continue;
                var peer = peers[index]; var zdo = ZDOMan.instance.GetZDO(peer.m_characterID);
                if (zdo == null || !Due(zdo, time, 2, options.HealthLost ? ReportMetric.HealthLost : options.Biomes ? ReportMetric.Biomes : options.BossKills ? ReportMetric.ParticipantBossCredits : ReportMetric.ExplorationCells, utc) ||
                    !StablePlayerResolver.Resolve(peer.m_characterID, utc, out var actor)) continue;
                bool fresh = Fresh(zdo, time);
                if (options.HealthLost)
                    Collector.SampleHealth(_world, zdo.m_uid.ToString(), actor!, zdo.GetFloat(ZDOVars.s_health, float.NaN), utc, time, fresh);
                if (grid == null && !options.Biomes && !options.BossKills && !travel) continue;
                var position = zdo.GetPosition();
                bool validPosition = ObservationValues.Finite(position.x) && ObservationValues.Finite(position.y) && ObservationValues.Finite(position.z);
                if (options.BossKills)
                    Collector.SamplePlayerPosition(_world, zdo.m_uid.ToString(), actor!, position.x, position.y, position.z, utc, time, fresh && validPosition);
                string biomeName = "";
                if ((options.Biomes || options.BiomeTime) && fresh && validPosition && WorldGenerator.instance != null)
                {
                    var biome = WorldGenerator.instance.GetBiome(position.x, position.z);
                    if (biome != Heightmap.Biome.None && Enum.IsDefined(typeof(Heightmap.Biome), biome))
                    {
                        biomeName = biome.ToString();
                        if (options.Biomes) Collector.SampleBiome(_world, actor!, biomeName, utc, time, true);
                    }
                }
                if (travel)
                {
                    long profile = zdo.GetLong(ZDOVars.s_playerID, 0);
                    bool onShip = options.OnFoot && (ships.Count > 256 || profile == 0 || ships.Any(ship => ship is Ship boat && boat && boat.IsPlayerInBoat(profile)));
                    Collector.SampleTravel(_world, zdo.m_uid.ToString(), actor!, position.x, position.z, biomeName, utc, time,
                        fresh && validPosition && zdo.GetFloat(ZDOVars.s_health, 0) > 0, onShip);
                }
                if (grid != null)
                {
                    Collector.SampleExploration(new ExplorationSample(_world, actor!, grid, position.x, position.z, utc, time, fresh));
                }
            }
            else if (options.Sailing && ships[index - peers.Count] is Ship ship && ship && ship.m_nview && ship.m_nview.IsValid())
            {
                var zdo = ship.m_nview.GetZDO();
                if (zdo == null || !Due(zdo, time, 1, ReportMetric.SailingDistance, utc) || !ship.m_shipControlls) continue;
                long user = ship.m_shipControlls.GetUser();
                if (!StablePlayerResolver.Profile(user, utc, out var actor, out _) || actor == null)
                { Collector.InvalidateShip(zdo.m_uid.ToString()); continue; }
                var position = ship.m_nview.IsOwner() ? ship.transform.position : zdo.GetPosition();
                Collector.SampleShip(new ShipSample(_world, zdo.m_uid.ToString(), actor, zdo.GetOwner(), position.x, position.z,
                    utc, time, ship.m_nview.IsOwner() || Fresh(zdo, time), ship.m_shipControlls.HaveValidUser() && ship.IsPlayerInBoat(user)));
            }
        }
    }

    private bool Due(ZDO zdo, double time, double cadence, ReportMetric metric, DateTime utc)
    {
        bool due = _sampled.Due(zdo.m_uid.ToString(), time, cadence, out bool dropped);
        if (dropped) Collector.Coverage(metric, CoverageState.Partial, utc, CoverageReason.Dropped);
        return due;
    }

    internal void AcceptedPosition(string world, string id, uint revision, long owner, long sender, double time)
    {
        if (!ActivityCollector.NeedsPositionUpdates(Options) || world != _world || sender != owner || owner == 0) return;
        if (_fresh.TryGetValue(id, out var old) && old.Owner == owner && revision <= old.Revision) return;
        if (!_fresh.ContainsKey(id) && _fresh.Count >= 4096)
        {
            Collector.Coverage(ReportMetric.ExplorationCells, CoverageState.Partial, DateTime.UtcNow, CoverageReason.Dropped);
            if (Options.LongestBowHit) Collector.Coverage(ReportMetric.LongestBowHit, CoverageState.Partial, DateTime.UtcNow, CoverageReason.Dropped);
            return;
        }
        _fresh[id] = new Freshness { Revision = revision, Owner = owner, Time = time };
    }

    private bool Fresh(ZDO zdo, double time, bool consume = true)
    {
        if (!_fresh.TryGetValue(zdo.m_uid.ToString(), out var fresh) || consume && fresh.Consumed || fresh.Owner != zdo.GetOwner() ||
            fresh.Revision != zdo.DataRevision ||
            time < fresh.Time || time - fresh.Time > 2.5) return false;
        if (consume) fresh.Consumed = true;
        return true;
    }

    internal bool TryFreshPosition(string world, ZDOID character, double time, out Vector3 position)
    {
        position = default;
        if (world != _world || !Options.LongestBowHit) return false;
        var zdo = ZDOMan.instance.GetZDO(character);
        // Hits may reuse a recent accepted endpoint even after a sampler consumed it.
        if (zdo == null || !Fresh(zdo, time, consume: false)) return false;
        position = zdo.GetPosition();
        return ObservationValues.Finite(position.x) && ObservationValues.Finite(position.y) && ObservationValues.Finite(position.z);
    }

    internal void Removed(string world, string id)
    {
        if (world != _world) return;
        Cooking.InvalidateStation(world, id); _fresh.Remove(id); _sampled.Remove(id);
        if (!SimulationCombatPatches.Completing(id)) Simulation.Invalidate(id);
    }

    internal void OwnershipChanged(string world, string id)
    {
        if (world != _world) return;
        Cooking.InvalidateStation(world, id); _fresh.Remove(id);
        if (!SimulationCombatPatches.Completing(id)) Simulation.Invalidate(id);
    }

    private static ExplorationGrid? RuntimeGrid()
    {
        var map = Minimap.instance;
        // An initialized runtime map is required; field initializer constants prove no active grid.
        if (!map || map.m_explored == null || !map.m_mapTexture || map.m_mapTexture.width != map.m_textureSize ||
            map.m_mapTexture.height != map.m_textureSize || map.m_explored.Length != (long)map.m_textureSize * map.m_textureSize) return null;
        try { return new ExplorationGrid(map.m_textureSize, map.m_pixelSize, map.m_exploreRadius); }
        catch (ArgumentException) { return null; }
    }

    internal string[] FlushThrough(DateTime endUtc)
    {
        CoverageReason? pause = _explorationPauseReason?.Invoke(_world);
        if (pause.HasValue) Collector.PauseExploration(DateTime.UtcNow, pause.Value);
        Collector.FlushThrough(endUtc);
        return Array.Empty<string>();
    }

    internal void Failed(Exception error)
    {
        Suspend();
        _warn("Activity capture failed (" + error.GetType().Name + "); pending observations discarded.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { ChangeWorld(""); }
        finally { if (ReferenceEquals(Current, this)) Current = null; }
    }
}
