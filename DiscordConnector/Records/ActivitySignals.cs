using System;
using System.Collections.Generic;
using System.Linq;

namespace DiscordConnector.Records;

internal enum FloorItemEvent { Request, Snapshot, Removed, Excluded, PlayerDrop }

// All adapter inputs are capture-thread scalar snapshots, never pooled ZDOs.
internal sealed class FloorItemSignal
{
    internal readonly FloorItemEvent Kind;
    internal readonly string World, Item, Prefab, Signature, Token;
    internal readonly long Owner, Sender;
    internal readonly uint Revision;
    internal readonly int Stack;
    internal readonly ActivityActor? Actor;
    internal readonly DateTime Utc;
    internal readonly double Time;
    internal readonly ItemProvenance Provenance;

    internal FloorItemSignal(FloorItemEvent kind, string world, string item, string prefab, string signature,
        int stack, long owner, uint revision, long sender, string token, DateTime utc, double time,
        ActivityActor? actor = null, ItemProvenance provenance = ItemProvenance.UnknownMixed)
    {
        ObservationValues.Text(world); ObservationValues.Text(item); ObservationValues.Text(token); ObservationValues.Utc(utc);
        if (!ObservationValues.Finite(time) || stack < 0) throw new ArgumentException("Invalid item snapshot.");
        Kind = kind; World = world; Item = item; Prefab = prefab; Signature = signature; Stack = stack;
        Owner = owner; Revision = revision; Sender = sender; Token = token; Utc = utc; Time = time;
        Actor = actor; Provenance = provenance;
    }
}

internal sealed class PieceSignal
{
    internal readonly string World, Piece, Prefab;
    internal readonly ActivityActor? Actor;
    internal readonly DateTime Utc;
    internal readonly double Time;
    internal readonly bool NewLifecycle, IsPiece, Excluded;

    internal PieceSignal(string world, string piece, string prefab, ActivityActor? actor, DateTime utc, double time,
        bool newLifecycle, bool isPiece, bool excluded)
    {
        World = world; Piece = piece; Prefab = prefab; Actor = actor; Utc = utc; Time = time;
        NewLifecycle = newLifecycle; IsPiece = isPiece; Excluded = excluded;
    }
}

internal sealed class ShipSample
{
    internal readonly string World, Ship;
    internal readonly ActivityActor Actor;
    internal readonly long Owner;
    internal readonly double X, Z, Time;
    internal readonly DateTime Utc;
    internal readonly bool Fresh, Onboard;
    internal ShipSample(string world, string ship, ActivityActor actor, long owner, double x, double z,
        DateTime utc, double time, bool fresh, bool onboard)
    { World = world; Ship = ship; Actor = actor; Owner = owner; X = x; Z = z; Utc = utc; Time = time; Fresh = fresh; Onboard = onboard; }
}

internal sealed class ExplorationSample
{
    internal readonly string World;
    internal readonly ActivityActor Actor;
    internal readonly ExplorationGrid Grid;
    internal readonly double X, Z, Time;
    internal readonly DateTime Utc;
    internal readonly bool Fresh;
    internal ExplorationSample(string world, ActivityActor actor, ExplorationGrid grid, double x, double z,
        DateTime utc, double time, bool fresh)
    { World = world; Actor = actor; Grid = grid; X = x; Z = z; Utc = utc; Time = time; Fresh = fresh; }
}

internal sealed class ActivitySampleCadence
{
    private sealed class Entry
    {
        internal double Sampled, Seen;
        internal LinkedListNode<string> Node = null!;
    }
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _seen = new();
    internal int Count => _entries.Count;

    internal bool Due(string id, double time, double cadence, out bool capacityDropped)
    {
        capacityDropped = false;
        if (!_entries.TryGetValue(id, out var entry))
        {
            if (_entries.Count == 4096) { capacityDropped = true; return false; }
            entry = new Entry { Sampled = time, Seen = time, Node = _seen.AddLast(id) };
            _entries.Add(id, entry); return true;
        }
        entry.Seen = time;
        _seen.Remove(entry.Node); _seen.AddLast(entry.Node);
        if (time >= entry.Sampled && time - entry.Sampled < cadence) return false;
        entry.Sampled = time; return true;
    }

    internal void Sweep(double time)
    {
        for (int i = 0; i < 32 && _seen.First != null; i++)
        {
            string id = _seen.First.Value;
            double seen = _entries[id].Seen;
            if (time >= seen && time - seen <= 2.5) break;
            Remove(id);
        }
    }

    internal void Remove(string id)
    {
        if (!_entries.TryGetValue(id, out var entry)) return;
        _seen.Remove(entry.Node); _entries.Remove(id);
    }
    internal void Clear() { _entries.Clear(); _seen.Clear(); }
}
