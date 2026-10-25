// Contract-only doubles: these do not execute Harmony, Unity, networking, or the game.
namespace UnityEngine
{
    public class Object
    {
        public bool Alive = true;
        public static implicit operator bool(Object? value) => value != null && value.Alive;
    }
    public struct Vector3 { public float x; }
    public class Transform { public Vector3 position; }
}

public struct ZDOID
{
    public long UserID;
    public uint ID;
    public override string ToString() => UserID + ":" + ID;
}
public class ZDO
{
    public ZDOID m_uid = new() { UserID = 1, ID = 2 };
    public long Owner = 1;
    public long GetOwner() => Owner;
    public void SetOwner(long uid) => Owner = uid;
    public void SetOwnerInternal(long uid) => Owner = uid;
}
public class ZNetView : UnityEngine.Object
{
    public ZDO? Zdo = new();
    public string Prefab = "Troll";
    public ZDO? GetZDO() => Zdo;
    public bool IsOwner() => Zdo?.GetOwner() == ZNet.GetUID();
    public bool IsValid() => Zdo != null;
    public string GetPrefabName() => Prefab;
}
public class Character : UnityEngine.Object
{
    public ZNetView m_nview = new();
    public UnityEngine.Transform transform = new();
    public float Health = 10;
    public bool Player;
    public bool Tamed;
    public bool Boss;
    public ZDOID GetZDOID() => m_nview.GetZDO()?.m_uid ?? default;
    public float GetHealth() => Health;
    public bool IsPlayer() => Player;
    public bool IsTamed() => Tamed;
    public bool IsBoss() => Boss;
    public void ApplyDamage(HitData hit) { }
    public void OnDeath() { }
    public void SetHealth(float health) => Health = health;
}
public class HitData { public ZDOID m_attacker = new() { UserID = 20, ID = 30 }; }
public class Socket
{
    public bool Connected = true;
    public string Host = "Steam_account";
    public bool IsConnected() => Connected;
    public string GetHostName() => Host;
}
public class ZNetPeer
{
    public ZDOID m_characterID = new() { UserID = 20, ID = 30 };
    public string m_playerName = "Player name";
    public bool m_server;
    public bool Ready = true;
    public Socket m_socket = new();
    public bool IsReady() => Ready;
}
public class ZNet : UnityEngine.Object
{
    public static ZNet instance = new();
    public bool Dedicated = true;
    public bool Server = true;
    public long World = 50;
    public List<ZNetPeer> Peers = new() { new() };
    public bool IsDedicated() => Dedicated;
    public bool IsServer() => Server;
    public long GetWorldUID() => World;
    public static long GetUID() => 1;
    public List<ZNetPeer> GetPeers() => Peers;
    public void Shutdown() { }
}
public static class Heightmap
{
    public enum Biome { None, BlackForest, Meadows }
    public static Biome FindBiome(UnityEngine.Vector3 point) => point.x == -1 ? Biome.None :
        point.x == 123 ? Biome.BlackForest : Biome.Meadows;
}
namespace BepInEx.Bootstrap
{
    public class ConfigDefinition
    {
        public string Section = "General";
        public string Key = "Enabled";
    }
    public class ConfigEntry { public object BoxedValue = true; }
    public class Plugin { public Dictionary<ConfigDefinition, ConfigEntry> Config = new(); }
    public class PluginInfo { public Plugin? Instance = new(); }
    public static class Chainloader { public static Dictionary<string, PluginInfo> PluginInfos = new(); }
}
namespace HarmonyLib
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
    public class HarmonyPatch : Attribute
    {
        public HarmonyPatch() { }
        public HarmonyPatch(Type type, string method) { }
    }
    public class HarmonyPrefix : Attribute { }
    public class HarmonyPostfix : Attribute { }
    public class HarmonyFinalizer : Attribute { }
    public class HarmonyTargetMethods : Attribute { }
    public class HarmonyPriority(int priority) : Attribute { public int Value = priority; }
    public static class Priority { public const int Last = 0; }
    public static class AccessTools
    {
        public static System.Reflection.MethodInfo Method(Type type, string method) => type.GetMethod(method)!;
    }
}
namespace DiscordConnector
{
    public class CombatConfig { public bool Enabled; }
    public class Logger
    {
        public List<string> Errors = new();
        public void LogError(string message) => Errors.Add(message);
    }
    public class Database { public Records.CombatStore Combat = new(); }
    public static class DiscordConnectorPlugin
    {
        public static CombatConfig StaticCombatConfig = new();
        public static Database StaticDatabase = new();
        public static Logger StaticLogger = new();
    }
}
namespace DiscordConnector.Records
{
    public class CombatRecord
    {
        public string Id = "", WorldId = "", VictimId = "", PlayerId = "", PlayerName = "", Creature = "", Biome = "";
        public bool IsBoss;
        public DateTime TimestampUtc;
    }
    public class CombatStore
    {
        public Dictionary<string, CombatRecord> Records = new();
        public bool Throw;
        public int FailOnCall;
        public int Calls;
        public bool Insert(CombatRecord record)
        {
            Calls++;
            if (Throw || Calls == FailOnCall) throw new InvalidOperationException("Secret/PII must not appear in logs");
            return Records.TryAdd(record.Id, record);
        }
    }
}
