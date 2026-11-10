using System;
using System.Collections.Generic;

namespace DiscordConnector.Records;

internal sealed class ObservationPrincipal
{
    internal string AccountId { get; }
    internal string ConnectionToken { get; }
    internal bool ServerLocal { get; }

    internal ObservationPrincipal(string accountId, string connectionToken, bool serverLocal)
    {
        ObservationValues.Text(accountId); ObservationValues.Text(connectionToken);
        if (accountId.Length > 256 || connectionToken.Length > 128) throw new ArgumentException("Invalid observation principal.");
        AccountId = accountId; ConnectionToken = connectionToken; ServerLocal = serverLocal;
    }
}

internal enum ObservationRejection { None, Duplicate, ReplayCapacity, PrincipalQuota, PrincipalCapacity, PendingOverflow, Stale, QueueFull }

// One policy per world/replay owner. Charges survive disconnects; only expiry or world replacement releases them.
internal sealed class ObservationAdmission
{
    private readonly double window;
    private readonly Dictionary<string, int> accounts = new(StringComparer.Ordinal), connections = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Account, string Connection), int> principals = new();
    private readonly Queue<(ObservationPrincipal Principal, double Time)> charges = new();
    private int local;
    private double last = double.NegativeInfinity;
    internal int RetainedAccounts => accounts.Count;
    internal int RetainedConnections => connections.Count;
    internal int RetainedPrincipals => principals.Count;
    internal int Nodes => charges.Count;

    internal ObservationAdmission(double window = 60)
    {
        if (!ObservationValues.Finite(window) || window <= 0) throw new ArgumentOutOfRangeException(nameof(window));
        this.window = window;
    }

    internal bool TryAdmit(ObservationPrincipal principal, double monotonic, out ObservationRejection reason)
    {
        reason = ObservationRejection.Stale;
        if (principal == null || !ObservationValues.Finite(monotonic) || monotonic < 0 || monotonic < last) return false;
        last = monotonic;
        for (int examined = 0; examined < 32 && charges.Count != 0; examined++)
        {
            if (monotonic - charges.Peek().Time <= window) break;
            var expired = charges.Dequeue().Principal;
            if (expired.ServerLocal) local--;
            else
            {
                Release(accounts, expired.AccountId); Release(connections, expired.ConnectionToken);
                var key = (expired.AccountId, expired.ConnectionToken);
                if (principals[key] == 1) principals.Remove(key); else principals[key]--;
            }
        }
        if (principal.ServerLocal)
        {
            if (local >= 128) { reason = ObservationRejection.PrincipalQuota; return false; }
            local++;
        }
        else
        {
            accounts.TryGetValue(principal.AccountId, out int account);
            connections.TryGetValue(principal.ConnectionToken, out int connection);
            var key = (principal.AccountId, principal.ConnectionToken);
            principals.TryGetValue(key, out int retained);
            if (account >= 128 || connection >= 128) { reason = ObservationRejection.PrincipalQuota; return false; }
            if (retained == 0 && principals.Count >= 128)
            { reason = ObservationRejection.PrincipalCapacity; return false; }
            accounts[principal.AccountId] = account + 1; connections[principal.ConnectionToken] = connection + 1;
            principals[key] = retained + 1;
        }
        charges.Enqueue((principal, monotonic)); reason = ObservationRejection.None; return true;
    }

    private static void Release(Dictionary<string, int> index, string key)
    {
        int remaining = index[key] - 1;
        if (remaining == 0) index.Remove(key); else index[key] = remaining;
    }

    internal void Reset()
    { accounts.Clear(); connections.Clear(); principals.Clear(); charges.Clear(); local = 0; last = double.NegativeInfinity; }
}
