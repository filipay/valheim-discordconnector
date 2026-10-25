using BepInEx.Bootstrap;
using DiscordConnector;
using DiscordConnector.Patches;

internal static class Integration
{
    private static readonly Exception Failure = new("Secret/PII must not appear in logs");
    private static int _passed;
    private static Character Reset()
    {
        CombatPatches.Reset.Prefix();
        ZNet.instance = new();
        DiscordConnectorPlugin.StaticCombatConfig = new() { Enabled = true };
        DiscordConnectorPlugin.StaticDatabase = new();
        DiscordConnectorPlugin.StaticLogger = new();
        Chainloader.PluginInfos.Clear();
        Chainloader.PluginInfos["MVP.Valheim_Serverside_Simulations"] = new();
        var victim = new Character { Boss = true };
        victim.transform.position = new() { x = 123 };
        return victim;
    }

    private static void Count(int expected)
    {
        int actual = DiscordConnectorPlugin.StaticDatabase.Combat.Records.Count;
        if (actual != expected) throw new Exception($"Expected {expected} persisted, got {actual}");
    }

    private static void Test(string name, Action test)
    {
        test();
        Console.WriteLine($"PASS adapter: {name}");
        _passed++;
    }

    private static void Die(Character victim, bool fail = false, bool runOriginal = true)
    {
        CombatPatches.Death.Prefix(victim, out var death);
        Count(0); // A prefix may capture facts but must never persist.
        victim.m_nview.Zdo = null; // Actual OnDeath resets the view before the postfix.
        victim.Alive = false; // Also exercise a Unity-destroyed object.
        CombatPatches.Death.Postfix(death, runOriginal);
        CombatPatches.Death.Finalizer(death, fail ? Failure : null);
    }

    private static void Kill(Character victim, HitData? hit = null, bool inside = false, bool failDamage = false,
        bool failDeath = false, bool runDamage = true, bool runDeath = true)
    {
        CombatPatches.Damage.Prefix(victim, hit ?? new(), out var damage);
        victim.Health = 0;
        if (inside) Die(victim, failDeath, runDeath);
        CombatPatches.Damage.Postfix(victim, damage, runDamage);
        CombatPatches.Damage.Finalizer(damage, failDamage ? Failure : null);
        if (!inside) Die(victim, failDeath, runDeath);
    }

    public static void Run()
    {
        foreach (bool inside in new[] { false, true })
            Test($"destroyed victim metadata survives callback (inside={inside})", () =>
            {
                var victim = Reset();
                Kill(victim, inside: inside);
                Count(1);
                var record = DiscordConnectorPlugin.StaticDatabase.Combat.Records.Values.Single();
                if (record.Id != "50:1:2" || record.WorldId != "50" || record.VictimId != "1:2" ||
                    record.PlayerId != "Steam_account" || record.PlayerName != "Player name" ||
                    record.Creature != "Troll" || record.Biome != "BlackForest" || !record.IsBoss ||
                    record.TimestampUtc.Kind != DateTimeKind.Utc) throw new Exception("Wrong captured metadata");
            });
        foreach (string reason in new[] { "off", "client", "listen", "mod absent", "mod disabled", "mod invalid config",
                     "player", "tamed", "client owned", "no victim id", "no victim user", "no world", "no attacker",
                     "unmatched", "ambiguous", "duplicate account", "disconnected", "not ready", "empty account", "server peer", "empty name", "unknown biome" })
            Test($"reject {reason}", () =>
            {
                var v = Reset();
                var hit = new HitData();
                var peer = ZNet.instance.Peers[0];
                switch (reason)
                {
                    case "off": DiscordConnectorPlugin.StaticCombatConfig.Enabled = false; break;
                    case "client": ZNet.instance.Server = false; break;
                    case "listen": ZNet.instance.Dedicated = false; break;
                    case "mod absent": Chainloader.PluginInfos.Clear(); break;
                    case "mod disabled": Chainloader.PluginInfos.Values.Single().Instance!.Config.Add(new(), new() { BoxedValue = false }); break;
                    case "mod invalid config": Chainloader.PluginInfos.Values.Single().Instance!.Config.Add(new(), new() { BoxedValue = "true" }); break;
                    case "player": v.Player = true; break;
                    case "tamed": v.Tamed = true; break;
                    case "client owned": v.m_nview.Zdo!.Owner = 2; break;
                    case "no victim id": v.m_nview.Zdo!.m_uid.ID = 0; break;
                    case "no victim user": v.m_nview.Zdo!.m_uid.UserID = 0; break;
                    case "no world": ZNet.instance.World = 0; break;
                    case "no attacker": hit.m_attacker = default; break;
                    case "unmatched": hit.m_attacker.ID++; break;
                    case "ambiguous": ZNet.instance.Peers.Add(new()); break;
                    case "duplicate account": ZNet.instance.Peers.Add(new() { m_characterID = new() { UserID = 9, ID = 9 } }); break;
                    case "disconnected": peer.m_socket.Connected = false; break;
                    case "not ready": peer.Ready = false; break;
                    case "empty account": peer.m_socket.Host = " "; break;
                    case "server peer": peer.m_server = true; break;
                    case "empty name": peer.m_playerName = " "; break;
                    case "unknown biome": v.transform.position.x = -1; break;
                }
                Kill(v, hit);
                Count(0);
            });
        foreach (string failure in new[] { "damage", "death", "damage skipped", "death skipped" })
            Test($"reject {failure}", () =>
            {
                var v = Reset();
                Kill(v, inside: true, failDamage: failure == "damage", failDeath: failure == "death",
                    runDamage: failure != "damage skipped", runDeath: failure != "death skipped");
                Count(0);
            });
        foreach (string change in new[] { "disconnect", "owner", "revival", "shutdown" })
            Test($"deferred attribution invalidated by {change}", () =>
            {
                var v = Reset();
                CombatPatches.Damage.Prefix(v, new(), out var damage);
                v.Health = 0;
                CombatPatches.Damage.Postfix(v, damage, true);
                CombatPatches.Damage.Finalizer(damage, null);
                switch (change)
                {
                    case "disconnect": ZNet.instance.Peers.Clear(); break;
                    case "owner":
                        CombatPatches.Ownership.Prefix(v.m_nview.Zdo!, 2);
                        v.m_nview.Zdo!.Owner = 2;
                        break;
                    case "revival":
                        CombatPatches.Revival.Prefix(v, 5);
                        v.Health = 5;
                        v.Health = 0;
                        break;
                    case "shutdown": CombatPatches.Reset.Prefix(); break;
                }
                Die(v);
                Count(0);
            });
        Test("storage failures are explicit without exposing exception text", () =>
        {
            var v = Reset();
            DiscordConnectorPlugin.StaticDatabase.Combat.Throw = true;
            Kill(v);
            Count(0);
            string logged = DiscordConnectorPlugin.StaticLogger.Errors.Single();
            if (!logged.Contains("InvalidOperationException") || logged.Contains("Secret/PII"))
                throw new Exception("Unsafe or absent failure log");
        });
        Test("one failed insert preserves released peers and unrelated deferred attribution", () =>
        {
            var pending = Reset();
            pending.m_nview.Zdo!.m_uid.ID = 10;
            CombatPatches.Damage.Prefix(pending, new(), out var pendingDamage);
            pending.Health = 0;
            CombatPatches.Damage.Postfix(pending, pendingDamage, true);
            CombatPatches.Damage.Finalizer(pendingDamage, null);

            var enclosing = new Character();
            enclosing.m_nview.Zdo!.m_uid.ID = 20;
            CombatPatches.Damage.Prefix(enclosing, new(), out var enclosingDamage);
            foreach (uint id in new uint[] { 30, 40 })
            {
                var nested = new Character();
                nested.m_nview.Zdo!.m_uid.ID = id;
                Kill(nested, inside: true);
            }
            Count(0);
            var store = DiscordConnectorPlugin.StaticDatabase.Combat;
            store.FailOnCall = 1;
            enclosing.Health = 5;
            CombatPatches.Damage.Postfix(enclosing, enclosingDamage, true);
            CombatPatches.Damage.Finalizer(enclosingDamage, null);
            Count(1);
            if (store.Calls != 2) throw new Exception("A failed insert skipped another released event");

            CombatPatches.Death.Prefix(pending, out var pendingDeath);
            pending.m_nview.Zdo = null;
            pending.Alive = false;
            CombatPatches.Death.Postfix(pendingDeath, true);
            CombatPatches.Death.Finalizer(pendingDeath, null);
            Count(2);
            if (store.Calls != 3 || !store.Records.Values.Any(record => record.VictimId == "1:10"))
                throw new Exception("A failed insert cleared unrelated deferred attribution");
            string logged = DiscordConnectorPlugin.StaticLogger.Errors.Single();
            if (!logged.Contains("InvalidOperationException") || logged.Contains("Secret/PII"))
                throw new Exception("Unsafe or absent per-event failure log");
        });
        Test("ambiguous prefix is not rescued by a later unique peer mapping", () =>
        {
            var v = Reset();
            ZNet.instance.Peers.Add(new() { m_characterID = new() { UserID = 9, ID = 9 } });
            CombatPatches.Damage.Prefix(v, new(), out var damage);
            ZNet.instance.Peers.RemoveAt(1);
            v.Health = 0;
            CombatPatches.Damage.Postfix(v, damage, true);
            CombatPatches.Damage.Finalizer(damage, null);
            Die(v);
            Count(0);
        });
        Test("ownership change during death invalidates captured prefix", () =>
        {
            var v = Reset();
            CombatPatches.Damage.Prefix(v, new(), out var damage);
            v.Health = 0;
            CombatPatches.Damage.Postfix(v, damage, true);
            CombatPatches.Damage.Finalizer(damage, null);
            CombatPatches.Death.Prefix(v, out var death);
            CombatPatches.Ownership.Prefix(v.m_nview.Zdo!, 2);
            v.m_nview.Zdo!.Owner = 2;
            v.m_nview.Zdo = null;
            CombatPatches.Death.Postfix(death, true);
            CombatPatches.Death.Finalizer(death, null);
            Count(0);
        });
        Test("duplicate durable insert false is not reported as an error", () =>
        {
            var first = Reset();
            Kill(first);
            Count(1);
            CombatPatches.Reset.Prefix();
            var second = new Character();
            CombatPatches.Damage.Prefix(second, new(), out var damage);
            second.Health = 0;
            CombatPatches.Damage.Postfix(second, damage, true);
            CombatPatches.Damage.Finalizer(damage, null);
            CombatPatches.Death.Prefix(second, out var death);
            second.m_nview.Zdo = null;
            CombatPatches.Death.Postfix(death, true);
            CombatPatches.Death.Finalizer(death, null);
            Count(1);
            if (DiscordConnectorPlugin.StaticDatabase.Combat.Calls != 2 || DiscordConnectorPlugin.StaticLogger.Errors.Count != 0)
                throw new Exception("Duplicate handling changed");
        });
        Test("ownership interception covers both current setter APIs", () =>
        {
            var methods = CombatPatches.Ownership.Targets().Select(m => m.Name).ToArray();
            if (!methods.SequenceEqual(new[] { "SetOwner", "SetOwnerInternal" })) throw new Exception("Missing ownership hook");
        });
        Console.WriteLine($"{_passed} adapter checks passed (game/Harmony surfaces are stubs)");
    }
}
