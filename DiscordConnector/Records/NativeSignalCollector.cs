using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

using DiscordConnector.Leaderboards;

namespace DiscordConnector.Records;

// Detached connection evidence, also used by the game-free admission tests. No game objects reach the worker.
internal sealed class NativeSignalPeer
{
    internal readonly long Uid;
    internal readonly (long User, uint Id) Character;
    internal readonly ActivityActor? Actor;
    internal readonly object? Transport, Socket;
    internal readonly bool Ready;
    internal NativeSignalPeer(long uid, (long User, uint Id) character, ActivityActor? actor,
        object? transport, object? socket, bool ready)
    { Uid = uid; Character = character; Actor = actor; Transport = transport; Socket = socket; Ready = ready; }
}

internal sealed class NativeDodgeTarget
{
    internal NativeSignalPeer Beneficiary { get; }
    internal (long User, uint Id) Character { get; }
    internal long OwnerUid { get; }
    internal NativeSignalPeer? Owner { get; }
    internal NativeDodgeTarget(NativeSignalPeer beneficiary, (long User, uint Id) character, long ownerUid, NativeSignalPeer? owner)
    { Beneficiary = beneficiary; Character = character; OwnerUid = ownerUid; Owner = owner; }
}

internal sealed class NativeSignalCollector : IDisposable
{
    private readonly Func<ActivityBatch, bool> enqueue;
    private readonly Action<string> warn;
    private readonly ObservationLoss loss;
    private readonly bool queueAccountsLoss;
    private bool disposed, warned;
    private readonly long[] droppedByKind = new long[3];
    internal long Dropped { get; private set; }
    internal NativeSignalCollector(Func<ActivityBatch, bool> enqueue, Action<string> warn, ObservationLoss? loss = null)
    {
        this.enqueue = enqueue ?? throw new ArgumentNullException(nameof(enqueue)); this.warn = warn ?? throw new ArgumentNullException(nameof(warn));
        var source = enqueue.Target as IObservationLossSource;
        queueAccountsLoss = source != null; this.loss = loss ?? source?.Loss ?? ObservationLoss.Process;
    }

    internal bool Observe(ReportedSignalRecord record, ObservationPrincipal principal)
    {
        if (record == null) throw new ArgumentNullException(nameof(record));
        if (principal == null) throw new ArgumentNullException(nameof(principal));
        if (disposed) { Reject(record, ObservationRejection.Stale); return false; }
        bool ambiguous = false;
        try
        {
            if (enqueue(new ActivityBatch(signals: new[] { record }, signalPrincipal: principal)))
            { ManualReportDiagnostics.SignalProbe(NativeSignalProbe.Accepted); return true; }
        }
        catch (Exception) { ambiguous = true; }
        if (!queueAccountsLoss)
        {
            if (ambiguous) loss.RecordUncertain(record.WorldId, ObservationLoss.Metric(record.Kind));
            else Reject(record, ObservationRejection.QueueFull);
        }
        if (Dropped < long.MaxValue) Dropped++;
        if (record != null && (uint)record.Kind < droppedByKind.Length && droppedByKind[(int)record.Kind] < long.MaxValue)
            droppedByKind[(int)record.Kind]++;
        ManualReportDiagnostics.SignalProbe(NativeSignalProbe.Dropped);
        if (!warned)
        {
            warned = true;
            try { warn("Native reported signals dropped by bounded admission; no automatic replay."); } catch { }
        }
        return false;
    }
    internal void Reject(ReportedSignalRecord record, ObservationRejection reason) => loss.Record(record.WorldId, ObservationLoss.Metric(record.Kind), reason);
    internal long DroppedFor(ReportedSignalKind kind) => droppedByKind[(int)kind];
    internal void Reset() { warned = false; }
    public void Dispose() { disposed = true; Reset(); }

    internal static bool TryIdentity(ReportedSignalKind kind, bool local, object? actualTransport, long serverUid,
        long claimedSender, long targetPeer, (long User, uint Id) targetCharacter, IReadOnlyList<NativeSignalPeer> peers,
        IReadOnlyList<(long User, uint Id)> characters, out NativeSignalPeer? sender, out NativeSignalPeer? target)
    {
        sender = null; target = null;
        if (serverUid == 0 || (kind != ReportedSignalKind.KillCredit && kind != ReportedSignalKind.ConsumableUse)) return false;
        if (local)
        {
            if (kind != ReportedSignalKind.KillCredit || actualTransport != null || claimedSender != serverUid) return false;
        }
        else
        {
            if (actualTransport == null || claimedSender == 0 || claimedSender == serverUid) return false;
            foreach (var peer in peers)
            {
                if (!ReferenceEquals(peer.Transport, actualTransport)) continue;
                if (sender != null) return false;
                sender = peer;
            }
            if (sender == null || sender.Uid != claimedSender || !ValidPeer(sender, peers, characters)) return false;
        }
        if (kind == ReportedSignalKind.ConsumableUse)
        {
            if (targetPeer != 0 || sender == null || targetCharacter != sender.Character) return false;
            target = sender; return true;
        }
        if (targetPeer == 0 || targetPeer == serverUid || targetCharacter != (0L, 0U)) return false;
        foreach (var peer in peers)
        {
            if (peer.Uid != targetPeer) continue;
            if (target != null) return false;
            target = peer;
        }
        return target != null && ValidPeer(target, peers, characters);
    }

    internal static bool TryDodgeIdentity(bool local, object? actualTransport, long serverUid, long claimedSender,
        long recipient, (long User, uint Id) character, long actualOwner, IReadOnlyList<NativeSignalPeer> peers,
        IReadOnlyList<(long User, uint Id)> characters, out NativeSignalPeer? reporter, out NativeDodgeTarget? target)
    {
        reporter = null; target = null;
        if (serverUid == 0 || actualOwner == 0 || recipient != actualOwner || character.User == 0 || character.Id == 0) return false;
        if (local)
        {
            if (actualTransport != null || claimedSender != serverUid) return false;
        }
        else
        {
            if (actualTransport == null || claimedSender == 0 || claimedSender == serverUid) return false;
            foreach (var peer in peers)
            {
                if (!ReferenceEquals(peer.Transport, actualTransport)) continue;
                if (reporter != null) return false;
                reporter = peer;
            }
            if (reporter == null || reporter.Uid != claimedSender || !ValidConnection(reporter, peers)) return false;
        }
        NativeSignalPeer? beneficiary = null, owner = null;
        foreach (var peer in peers)
        {
            if (peer.Character == character)
            {
                if (beneficiary != null) return false;
                beneficiary = peer;
            }
            if (peer.Uid == actualOwner)
            {
                if (owner != null) return false;
                owner = peer;
            }
        }
        if (beneficiary == null || !ValidConnection(beneficiary, peers)) return false;
        int matches = 0;
        foreach (var current in characters) if (current == character) matches++;
        if (matches != 1 || (actualOwner == serverUid ? owner != null : owner == null || !ValidConnection(owner, peers))) return false;
        target = new NativeDodgeTarget(beneficiary, character, actualOwner, owner);
        return true;
    }

    private static bool ValidConnection(NativeSignalPeer match, IReadOnlyList<NativeSignalPeer> peers)
    {
        if (!match.Ready || match.Uid == 0 || match.Actor == null || match.Transport == null || match.Socket == null ||
            match.Actor.PlayerId.Length > 256 || match.Actor.PlayerName.Length > 128 || match.Actor.ConnectionToken.Length > 128) return false;
        foreach (var peer in peers)
            if (!ReferenceEquals(peer, match) && (peer.Uid == match.Uid ||
                ReferenceEquals(peer.Transport, match.Transport) || ReferenceEquals(peer.Socket, match.Socket) ||
                peer.Actor?.PlayerId == match.Actor.PlayerId)) return false;
        return true;
    }

    private static bool ValidPeer(NativeSignalPeer match, IReadOnlyList<NativeSignalPeer> peers,
        IReadOnlyList<(long User, uint Id)> characters)
    {
        if (!match.Ready || match.Uid == 0 || match.Character.User != match.Uid || match.Character.Id == 0 ||
            match.Actor == null || match.Transport == null || match.Socket == null || match.Actor.PlayerId.Length > 256 ||
            match.Actor.PlayerName.Length > 128 || match.Actor.ConnectionToken.Length > 128) return false;
        foreach (var peer in peers)
            if (!ReferenceEquals(peer, match) && (peer.Uid == match.Uid || peer.Character == match.Character ||
                ReferenceEquals(peer.Transport, match.Transport) || ReferenceEquals(peer.Socket, match.Socket) ||
                peer.Actor?.PlayerId == match.Actor.PlayerId)) return false;
        int matches = 0;
        foreach (var character in characters)
        {
            if (character.User != match.Uid) continue;
            if (character != match.Character) return false;
            matches++;
        }
        return matches == 1;
    }

    internal static bool SamePeer(NativeSignalPeer? captured, NativeSignalPeer? current) =>
        captured == null ? current == null : current != null && captured.Uid == current.Uid && captured.Character == current.Character &&
        ReferenceEquals(captured.Transport, current.Transport) && ReferenceEquals(captured.Socket, current.Socket) &&
        captured.Actor?.PlayerId == current.Actor?.PlayerId && captured.Actor?.ConnectionToken == current.Actor?.ConnectionToken;

    internal static bool IsCurrent(string capturedWorld, long capturedGeneration, NativeSignalPeer? capturedSender, NativeSignalPeer target,
        string liveWorld, long liveGeneration, NativeSignalPeer? liveSender, NativeSignalPeer? liveTarget) =>
        capturedWorld == liveWorld && capturedGeneration == liveGeneration && SamePeer(capturedSender, liveSender) && SamePeer(target, liveTarget);

    internal static string Token(ReportedSignalKind kind, string world, bool local, string session, long message, NativeSignalPeer target) =>
        ObservationValues.Key(kind.ToString(), world, local ? "local" : "remote", session, ObservationValues.Number(message),
            target.Actor!.PlayerId, ObservationValues.Number(target.Character.User), ObservationValues.Number(target.Character.Id));

    internal static bool ValidPayload(ReportedSignalKind kind, bool enabled, int size, Func<byte[]> copy)
    {
        if (kind == ReportedSignalKind.DodgeHitNotification) return enabled && size == 0;
        int limit = kind == ReportedSignalKind.KillCredit ? 4096 : kind == ReportedSignalKind.ConsumableUse ? 64 : 0;
        if (!enabled || size <= 0 || size > limit) return false;
        try
        {
            byte[] bytes = copy();
            if (bytes == null || bytes.Length != size) return false;
            using var stream = new MemoryStream(bytes, false);
            using var reader = new BinaryReader(stream, Encoding.UTF8);
            int length = 0, shift = 0;
            byte part;
            do
            {
                part = reader.ReadByte();
                if (shift >= 28) return false;
                length |= (part & 127) << shift; shift += 7;
            } while ((part & 128) != 0);
            if (length > 1024 || length > stream.Length - stream.Position) return false;
            string text = new UTF8Encoding(false, true).GetString(reader.ReadBytes(length));
            if (kind == ReportedSignalKind.ConsumableUse) return text == "eat" && stream.Position == stream.Length;
            if (text.Length > 256 || reader.ReadInt32() < 0) return false;
            int modifiers = reader.ReadInt32(), attackers = reader.ReadInt32(); byte cheats = reader.ReadByte();
            return modifiers >= 0 && modifiers <= 5 && attackers >= 1 && attackers <= 1024 && cheats <= 1 && stream.Position == stream.Length;
        }
        catch (Exception) { return false; }
    }
}
