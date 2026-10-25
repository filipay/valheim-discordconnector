using System;
using System.Collections.Generic;
using System.Linq;

namespace DiscordConnector.Records;

internal sealed class CombatSnapshot
{
    public readonly string WorldId;
    public readonly string VictimId;
    public readonly long Owner;
    public readonly float Health;
    public readonly bool Eligible;
    public readonly DateTime ObservedUtc;

    public CombatSnapshot(string worldId, string victimId, long owner, float health, bool eligible,
        DateTime observedUtc)
    {
        WorldId = worldId;
        VictimId = victimId;
        Owner = owner;
        Health = health;
        Eligible = eligible;
        ObservedUtc = observedUtc;
    }

    public string Id => WorldId + ":" + VictimId;
    public bool Valid => Eligible && Owner != 0 && !string.IsNullOrWhiteSpace(WorldId) &&
        !string.IsNullOrWhiteSpace(VictimId) && !float.IsNaN(Health) && !float.IsInfinity(Health);

    public bool Matches(CombatSnapshot other) => Valid && other.Valid && Id == other.Id && Owner == other.Owner;
}

// No game objects or durable writes: callbacks must independently prove damage and death.
internal sealed class CombatCorrelation<T>
{
    internal sealed class Invocation
    {
        internal Invocation? Parent;
        internal Candidate? Candidate;
        internal bool Observed;
        internal bool Closed;
        internal bool Succeeded;
    }

    internal sealed class Candidate
    {
        internal CombatSnapshot Before = null!;
        internal string AttackerId = "";
        internal string PlayerId = "";
        internal string PlayerName = "";
        internal bool Lethal;
        internal bool Dead;
        internal CombatSnapshot? DeathSnapshot;
        internal T Death = default!;
        internal readonly HashSet<Invocation> Blockers = new();
    }

    internal sealed class Kill
    {
        public readonly CombatSnapshot Before;
        public readonly string AttackerId;
        public readonly string PlayerId;
        public readonly string PlayerName;
        public readonly T Death;
        public string Id => Before.Id;

        internal Kill(Candidate candidate)
        {
            Before = candidate.Before;
            AttackerId = candidate.AttackerId;
            PlayerId = candidate.PlayerId;
            PlayerName = candidate.PlayerName;
            Death = candidate.Death;
        }
    }

    private readonly Dictionary<string, Candidate> _candidates = new();
    private Invocation? _current;

    private Invocation Begin()
    {
        var invocation = new Invocation { Parent = _current };
        _current = invocation;
        return invocation;
    }

    private static void Block(Candidate candidate, Invocation invocation)
    {
        for (Invocation? frame = invocation; frame != null; frame = frame.Parent)
            candidate.Blockers.Add(frame);
    }

    public Invocation BeginDamage(CombatSnapshot before, string attackerId, string playerId, string playerName)
    {
        Expire(before.ObservedUtc);
        var invocation = Begin();
        // Even an unattributable or zero-health hit supersedes the previous invocation.
        _candidates.Remove(before.Id);
        if (!before.Valid || before.Health <= 0 || string.IsNullOrWhiteSpace(attackerId) ||
            string.IsNullOrWhiteSpace(playerId)) return invocation;
        var candidate = new Candidate
        {
            Before = before, AttackerId = attackerId, PlayerId = playerId, PlayerName = playerName
        };
        invocation.Candidate = candidate;
        Block(candidate, invocation);
        _candidates[before.Id] = candidate;
        return invocation;
    }

    public Invocation BeginDeath(CombatSnapshot before)
    {
        Expire(before.ObservedUtc);
        var invocation = Begin();
        if (!_candidates.TryGetValue(before.Id, out Candidate? candidate)) return invocation;
        // A delayed callback is accepted only briefly; this is not a last-hit history.
        TimeSpan age = before.ObservedUtc - candidate.Before.ObservedUtc;
        if (!candidate.Before.Matches(before) || before.Health > 0 || age < TimeSpan.Zero ||
            age > TimeSpan.FromMinutes(1))
        {
            _candidates.Remove(before.Id);
            return invocation;
        }
        invocation.Candidate = candidate;
        Block(candidate, invocation);
        return invocation;
    }

    public void DamageApplied(Invocation invocation, CombatSnapshot? after)
    {
        invocation.Observed = true;
        Candidate? candidate = CurrentCandidate(invocation);
        if (candidate == null) return;
        // OnDeath can destroy the object inside ApplyDamage; its captured zero HP is still evidence.
        after ??= candidate.Dead ? candidate.DeathSnapshot : null;
        if (after == null || !candidate.Before.Matches(after) || after.Health > 0)
            _candidates.Remove(candidate.Before.Id);
        else candidate.Lethal = true;
    }

    public void DeathCompleted(Invocation invocation, CombatSnapshot after, T death)
    {
        invocation.Observed = true;
        Candidate? candidate = CurrentCandidate(invocation);
        if (candidate == null) return;
        if (!candidate.Before.Matches(after) || after.Health > 0)
            _candidates.Remove(candidate.Before.Id);
        else
        {
            candidate.Dead = true;
            candidate.DeathSnapshot = after;
            candidate.Death = death;
        }
    }

    private Candidate? CurrentCandidate(Invocation invocation)
    {
        Candidate? candidate = invocation.Candidate;
        return candidate != null && _candidates.TryGetValue(candidate.Before.Id, out Candidate? current) &&
            ReferenceEquals(candidate, current) ? candidate : null;
    }

    public IReadOnlyList<Kill> End(Invocation invocation, bool succeeded)
    {
        if (!ReferenceEquals(invocation, _current))
        {
            Clear();
            return Array.Empty<Kill>();
        }
        _current = invocation.Parent;
        invocation.Closed = true;
        invocation.Succeeded = succeeded && invocation.Observed;
        Candidate? own = CurrentCandidate(invocation);
        if (!invocation.Observed && own != null) _candidates.Remove(own.Before.Id);

        var kills = new List<Kill>();
        foreach (Candidate candidate in _candidates.Values.ToArray())
        {
            if (candidate.Blockers.Any(frame => frame.Closed && !frame.Succeeded))
                _candidates.Remove(candidate.Before.Id);
            else if (candidate.Lethal && candidate.Dead && candidate.Blockers.All(frame => frame.Closed))
            {
                _candidates.Remove(candidate.Before.Id);
                kills.Add(new Kill(candidate));
            }
        }
        return kills;
    }

    public void Invalidate(string worldId, string victimId) => _candidates.Remove(worldId + ":" + victimId);

    private void Expire(DateTime now)
    {
        foreach (Candidate candidate in _candidates.Values.ToArray())
            if (now - candidate.Before.ObservedUtc > TimeSpan.FromMinutes(1))
                _candidates.Remove(candidate.Before.Id);
    }

    public void Clear()
    {
        _candidates.Clear();
        _current = null;
    }
}
