using System;
using System.Collections.Generic;
using System.Linq;

namespace DiscordConnector.Records;

internal sealed partial class ActivityCollector
{
    private sealed class TravelSample
    {
        internal string Character = "", Biome = "", Connection = "";
        internal double X, Z, Time, LastJump = double.NegativeInfinity;
        internal DateTime Utc;
        internal bool OnShip;
    }
    private sealed class OnlineSession
    {
        internal ActivityActor Actor = null!;
        internal DateTime Start, Utc;
        internal double Time;
    }
    private readonly Dictionary<string, TravelSample> _travel = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OnlineSession> _online = new(StringComparer.Ordinal);

    private void UpdateTravelOptions(LeaderboardsOptions options)
    {
        if (_options.OnFoot != options.OnFoot || _options.BiomeTime != options.BiomeTime || _options.PortalTrips != options.PortalTrips)
            _travel.Clear();
        if (_options.OnlineTime && !options.OnlineTime) { FlushOnline(); _online.Clear(); }
    }

    private void ClearTravel() { _travel.Clear(); _online.Clear(); }

    private static bool Continuous(DateTime beforeUtc, double beforeTime, DateTime utc, double time)
    {
        double elapsed = time - beforeTime, wall = (utc - beforeUtc).TotalSeconds;
        return elapsed > 0 && elapsed <= 2.5 && wall > 0 && wall <= 2.5 && Math.Abs(elapsed - wall) <= .25;
    }

    internal void SampleTravel(string world, string character, ActivityActor actor, double x, double z, string biome,
        DateTime utc, double time, bool fresh, bool onShip)
    {
        if (world != _world || !(_options.OnFoot || _options.BiomeTime || _options.PortalTrips)) return;
        string key = actor.PlayerId;
        if (!fresh || !Finite(x, z, time) || time < 0 || !ActorIsCurrent(actor)) { _travel.Remove(key); return; }
        ObservationValues.Utc(utc); ObservationValues.Text(character);
        bool sampled = _travel.TryGetValue(key, out var before);
        if (!sampled && _travel.Count >= 256)
        {
            foreach (var metric in new[] { ReportMetric.OnFootDistance, ReportMetric.BiomeTime, ReportMetric.PortalTrips })
                if (_options.Includes(metric)) Drop(metric, utc);
            return;
        }
        var sample = new TravelSample { Character = character, Connection = actor.ConnectionToken, X = x, Z = z,
            Biome = biome, Utc = utc, Time = time, OnShip = onShip, LastJump = before?.LastJump ?? double.NegativeInfinity };
        _travel[key] = sample;
        if (before == null || before.Character != character || before.Connection != actor.ConnectionToken ||
            !Continuous(before.Utc, before.Time, utc, time)) return;
        double elapsed = time - before.Time;
        double distance = Math.Sqrt(Math.Pow(x - before.X, 2) + Math.Pow(z - before.Z, 2));
        if (!ObservationValues.Finite(distance)) return;
        if (distance >= 100 && distance / elapsed > 20)
        {
            if (_options.PortalTrips && time - sample.LastJump >= 10)
            {
                sample.LastJump = time;
                // shortcut: a large fresh jump is inferred teleportation, require a portal event source for exact trips.
                Add(new ActivityRecord(ActivityKind.PortalTrip, world, actor, character, Token(), utc));
            }
            return;
        }
        if (distance / elapsed > 20) return;
        if (_options.OnFoot && !onShip && !before.OnShip && distance > 0)
            Add(new ActivityRecord(ActivityKind.OnFoot, world, actor, character, Token(), utc,
                startUtc: before.Utc, seconds: elapsed, meters: distance));
        if (_options.BiomeTime && ObservationValues.KnownBiome(biome) && before.Biome == biome)
            Add(new ActivityRecord(ActivityKind.BiomeTime, world, actor, biome, Token(), utc, biome,
                startUtc: before.Utc, seconds: elapsed));
    }

    // Peer lifecycle, not replication freshness: idle connected players still accrue online time.
    internal void SampleOnline(string world, ActivityActor[] actors, DateTime utc, double time)
    {
        if (!_options.OnlineTime || world != _world) return;
        ObservationValues.Utc(utc);
        if (!ObservationValues.Finite(time) || time < 0 || actors.Length > 256)
        { FlushOnline(); _online.Clear(); Drop(ReportMetric.OnlineTime, utc); return; }
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var actor in actors)
        {
            if (!present.Add(actor.PlayerId)) continue;
            if (_online.TryGetValue(actor.PlayerId, out var session))
            {
                if (session.Actor.ConnectionToken != actor.ConnectionToken || !Continuous(session.Utc, session.Time, utc, time))
                { CloseOnline(session); _online.Remove(actor.PlayerId); }
                else
                {
                    if ((utc - session.Start).TotalSeconds > 10) CloseOnline(session);
                    session.Actor = actor; session.Utc = utc; session.Time = time;
                    continue;
                }
            }
            if (_online.Count >= 256) { Drop(ReportMetric.OnlineTime, utc); continue; }
            _online[actor.PlayerId] = new OnlineSession { Actor = actor, Start = utc, Utc = utc, Time = time };
        }
        foreach (var key in _online.Keys.Where(key => !present.Contains(key)).ToArray())
        {
            var session = _online[key];
            if (Continuous(session.Utc, session.Time, utc, time) && (utc - session.Start).TotalSeconds <= 10) session.Utc = utc;
            CloseOnline(session); _online.Remove(key);
        }
    }

    private void CloseOnline(OnlineSession session)
    {
        DateTime start = session.Start; session.Start = session.Utc;
        double seconds = (session.Utc - start).TotalSeconds;
        if (seconds > 0)
            Add(new ActivityRecord(ActivityKind.OnlineTime, _world, session.Actor, session.Actor.ConnectionToken, Token(), session.Utc,
                startUtc: start, seconds: seconds));
    }

    private void FlushOnline()
    { foreach (var session in _online.Values.ToArray()) CloseOnline(session); }
}
