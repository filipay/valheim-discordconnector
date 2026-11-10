using System;
using System.Collections.Generic;
using System.Linq;

namespace DiscordConnector.Records;

// Only immutable identities/health deltas enter this bounded ledger; no creature or peer references survive a hook.
internal sealed class SimulationCombatCollector
{
    private sealed class Victim
    {
        internal double Last;
        internal bool Completed;
        internal readonly Dictionary<string, (ActivityActor Actor, double Time)> Players = new(StringComparer.Ordinal);
    }
    private readonly Dictionary<string, Victim> _victims = new(StringComparer.Ordinal);
    private readonly ActivityCollector _activities;
    private readonly string _epoch = Guid.NewGuid().ToString("N");
    private long _sequence;
    private double _nextSweep;
    internal long Generation { get; private set; }
    internal SimulationCombatCollector(ActivityCollector activities) => _activities = activities;

    internal void Reset() { _victims.Clear(); _nextSweep = 0; Generation++; }
    internal void Invalidate(string victim) => _victims.Remove(victim);
    internal void Tick(bool enabled, double time)
    {
        if (!enabled || !ObservationValues.Finite(time) || time < 0) { Reset(); return; }
        if (time < _nextSweep && time >= _nextSweep - 1) return;
        _nextSweep = time + 1;
        foreach (var pair in _victims.ToArray())
        {
            if (time < pair.Value.Last || time - pair.Value.Last > 120) _victims.Remove(pair.Key);
            else if (!pair.Value.Completed) Prune(pair.Value, time);
        }
    }

    internal void Damage(string world, string victim, ActivityActor actor, double before, double after, DateTime utc, double time, LeaderboardsOptions flags, double bowMeters = 0)
    {
        if (!ObservationValues.Finite(before) || !ObservationValues.Finite(after) || before <= 0 || after >= before || !ObservationValues.Finite(time) || time < 0) return;
        double hp = Math.Min(before, before - after);
        if (flags.DamageDealt)
            _activities.ObserveCreature(new ActivityRecord(ActivityKind.CreatureDamage, world, actor, victim,
                _epoch + ":" + ObservationValues.Number(checked(++_sequence)), utc, hpLoss: hp));
        if (flags.LongestBowHit && ObservationValues.Finite(bowMeters) && bowMeters > 0 && bowMeters <= ObservationValues.MaxBowHitMeters)
            _activities.ObserveCreature(new ActivityRecord(ActivityKind.BowHit, world, actor, victim,
                _epoch + ":" + ObservationValues.Number(checked(++_sequence)), utc, meters: bowMeters));
        if (!flags.ReportedKillCredits) return;
        if (!_victims.TryGetValue(victim, out var entry))
        {
            if (_victims.Count >= 1024) { Dropped(utc); return; }
            _victims[victim] = entry = new Victim();
        }
        if (entry.Completed) return;
        entry.Last = time;
        if (entry.Players.Count >= 32) Prune(entry, time);
        if (!entry.Players.ContainsKey(actor.PlayerId) && entry.Players.Count >= 32) { Dropped(utc); return; }
        entry.Players[actor.PlayerId] = (actor, time);
    }

    // Null means uncovered: preserve the native reported-credit fallback. Empty means an already covered death.
    internal ActivityActor[]? Participants(string victim, double time)
    {
        if (!ObservationValues.Finite(time) || time < 0) return null;
        if (!_victims.TryGetValue(victim, out var entry) || time < entry.Last || time - entry.Last > 120) return null;
        if (entry.Completed) return Array.Empty<ActivityActor>();
        var actors = entry.Players.Values.Where(player => time >= player.Time && time - player.Time <= 120).Select(player => player.Actor).ToArray();
        return actors.Length == 0 ? null : actors;
    }

    internal void Death(string world, string victim, ActivityActor[] actors, DateTime utc, double time)
    {
        if (!_victims.TryGetValue(victim, out var entry) || entry.Completed) return;
        entry.Completed = true; entry.Last = time; entry.Players.Clear();
        foreach (var actor in actors)
            _activities.ObserveCreature(new ActivityRecord(ActivityKind.CreatureKill, world, actor, victim, victim, utc));
    }

    private void Dropped(DateTime utc) => _activities.Coverage(ReportMetric.ReportedKillCredits, CoverageState.Partial, utc, CoverageReason.Dropped);

    private static void Prune(Victim entry, double time)
    {
        foreach (string player in entry.Players.Where(pair => time < pair.Value.Time || time - pair.Value.Time > 120).Select(pair => pair.Key).ToArray())
            entry.Players.Remove(player);
    }
}
