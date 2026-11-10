using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;

using DiscordConnector.Records;

namespace DiscordConnector.Patches;

internal static class StablePlayerResolver
{
    private sealed class Connection { internal readonly string Token = Guid.NewGuid().ToString("N"); }
    private static readonly ConditionalWeakTable<object, Connection> Connections = new();

    internal static bool World(out string world)
    {
        world = "";
        var net = ZNet.instance;
        if (!net || !net.IsDedicated() || !net.IsServer() || ZNet.World == null || net.GetWorldUID() == 0 || ZDOMan.instance == null) return false;
        world = net.GetWorldUID().ToString(CultureInfo.InvariantCulture); return true;
    }

    internal static bool Transport(ZRpc rpc, DateTime utc, out ZNetPeer? peer, out ActivityActor? actor)
    {
        peer = null; actor = null;
        if (!World(out _)) return false;
        foreach (var candidate in ZNet.instance.GetPeers())
        {
            if (!ReferenceEquals(candidate.m_rpc, rpc)) continue;
            if (peer != null) { peer = null; return false; }
            peer = candidate;
        }
        return peer != null && Resolve(peer.m_characterID, utc, out actor);
    }

    internal static bool Resolve(ZDOID character, DateTime utc, out ActivityActor? actor)
    {
        actor = null;
        if (!World(out _) || character.ID == 0 || character.UserID == 0) return false;
        ZNetPeer? match = null;
        foreach (var peer in ZNet.instance.GetPeers())
        {
            if (peer.m_characterID != character) continue;
            if (match != null) return false;
            match = peer;
        }
        if (match == null || match.m_server || !match.IsReady() || match.m_uid == 0 || match.m_socket == null || !match.m_socket.IsConnected()) return false;
        string id = match.m_socket.GetHostName(), name = match.m_playerName;
        if (!Text(id) || !Text(name)) return false;
        foreach (var peer in ZNet.instance.GetPeers())
            if (!ReferenceEquals(peer, match) && (peer.m_uid == match.m_uid ||
                peer.IsReady() && peer.m_socket != null && peer.m_socket.IsConnected() && peer.m_socket.GetHostName() == id)) return false;
        actor = new ActivityActor(id, name, Connections.GetValue(match.m_socket, _ => new Connection()).Token, utc);
        return true;
    }

    internal static bool Profile(long profile, DateTime utc, out ActivityActor? actor, out ZDOID character)
    {
        actor = null; character = ZDOID.None;
        if (!World(out _) || profile == 0) return false;
        int matches = 0;
        foreach (var peer in ZNet.instance.GetPeers())
        {
            var zdo = ZDOMan.instance.GetZDO(peer.m_characterID);
            if (zdo == null || zdo.GetLong(ZDOVars.s_playerID, 0) != profile) continue;
            matches++; character = peer.m_characterID;
        }
        return matches == 1 && Resolve(character, utc, out actor);
    }

    internal static bool Creator(ZDO piece, DateTime utc, out ActivityActor? actor)
    {
        actor = null;
        if (!World(out _)) return false;
        long profile = piece.GetLong(ZDOVars.s_creator, 0);
        int index = piece.GetInt(ZDOVars.s_creatorIndex, -1);
        var history = ZNet.World?.m_playerHistory;
        if (history == null || index < 0 || index >= history.Count || !history[index].m_id.IsValid ||
            !Profile(profile, utc, out var resolved, out var character)) return false;
        int matches = 0;
        foreach (var player in ZNet.instance.GetPlayerList())
        {
            if (!player.m_userInfo.m_id.IsValid || !player.m_userInfo.m_id.Equals(history[index].m_id)) continue;
            if (player.m_characterID != character) return false;
            matches++;
        }
        if (matches != 1) return false;
        actor = resolved; return true;
    }

    internal static bool Current(ActivityActor actor)
    {
        if (!World(out _)) return false;
        int matches = 0;
        foreach (var peer in ZNet.instance.GetPeers())
        {
            if (peer.m_socket == null || peer.m_socket.GetHostName() != actor.PlayerId) continue;
            if (!Resolve(peer.m_characterID, DateTime.UtcNow, out var current) || current!.ConnectionToken != actor.ConnectionToken) return false;
            matches++;
        }
        return matches == 1;
    }

    private sealed class ReferenceComparer : IEqualityComparer<object>
    {
        internal static readonly ReferenceComparer Instance = new();
        public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
        public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
    }

    internal static bool NativeSignalSnapshot(DateTime utc, out NativeSignalPeer[] peers,
        out (long User, uint Id)[] characters)
    {
        peers = Array.Empty<NativeSignalPeer>(); characters = Array.Empty<(long User, uint Id)>();
        if (!World(out _)) return false;
        try
        {
            var nativePeers = ZNet.instance.GetPeers();
            var players = ZNet.instance.GetPlayerList();
            if (nativePeers.Count > 256 || players.Count > 256) return false;
            var uids = new HashSet<long>();
            var ids = new HashSet<ZDOID>();
            var accounts = new HashSet<string>(StringComparer.Ordinal);
            var sockets = new HashSet<object>(ReferenceComparer.Instance);
            var transports = new HashSet<object>(ReferenceComparer.Instance);
            var connections = new List<NativeSignalPeer>(nativePeers.Count);
            foreach (var peer in nativePeers)
            {
                string? account = peer.m_socket?.GetHostName();
                if (peer.m_uid != 0 && !uids.Add(peer.m_uid) ||
                    !peer.m_characterID.IsNone() && !ids.Add(peer.m_characterID) ||
                    account != null && !accounts.Add(account) ||
                    peer.m_socket != null && !sockets.Add(peer.m_socket) ||
                    peer.m_rpc != null && !transports.Add(peer.m_rpc)) return false;
                bool ready = !peer.m_server && peer.IsReady() && peer.m_socket != null && peer.m_socket.IsConnected() &&
                    peer.m_rpc != null && ReferenceEquals(peer.m_rpc.GetSocket(), peer.m_socket);
                ActivityActor? actor = ready && peer.m_uid != 0 &&
                    account != null && Text(account) && Text(peer.m_playerName) ?
                    new ActivityActor(account, peer.m_playerName, Connections.GetValue(peer.m_socket!, _ => new Connection()).Token, utc) : null;
                connections.Add(new NativeSignalPeer(peer.m_uid, (peer.m_characterID.UserID, peer.m_characterID.ID), actor,
                    peer.m_rpc, peer.m_socket, ready));
            }
            var currentCharacters = new List<(long User, uint Id)>(players.Count);
            var playerUsers = new HashSet<long>();
            foreach (var player in players)
            {
                if (player.m_characterID.UserID != 0 && !playerUsers.Add(player.m_characterID.UserID)) return false;
                currentCharacters.Add((player.m_characterID.UserID, player.m_userInfo.m_id.IsValid ? player.m_characterID.ID : 0));
            }
            peers = connections.ToArray(); characters = currentCharacters.ToArray();
            return true;
        }
        catch (Exception) { return false; }
    }

    internal static bool NativeDodgeOwner(ZDOID character, out long owner)
    {
        owner = 0;
        if (!World(out _) || character.UserID == 0 || character.ID == 0 || !ZNetScene.instance) return false;
        var zdo = ZDOMan.instance.GetZDO(character);
        if (zdo == null || zdo.m_uid != character) return false;
        var view = ZNetScene.instance.FindInstance(zdo);
        if (!view || !view.IsValid() || !ReferenceEquals(view.GetZDO(), zdo)) return false;
        owner = zdo.GetOwner();
        return owner != 0;
    }

    private static bool Text(string value) => !string.IsNullOrWhiteSpace(value) && value.IndexOf('\0') < 0;
}
