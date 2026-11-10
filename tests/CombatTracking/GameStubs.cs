// Contract-only doubles: these do not execute Harmony, Unity, networking, or the game.
namespace UnityEngine
{
    public class Object
    {
        public bool Alive = true;
        public static implicit operator bool(Object? value) => value != null && value.Alive;
    }
}

public struct ZDOID
{
    public long UserID;
    public uint ID;
    public override string ToString() => UserID + ":" + ID;
    public static bool operator ==(ZDOID a, ZDOID b) => a.UserID == b.UserID && a.ID == b.ID;
    public static bool operator !=(ZDOID a, ZDOID b) => !(a == b);
    public override bool Equals(object? other) => other is ZDOID id && this == id;
    public override int GetHashCode() => HashCode.Combine(UserID, ID);
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
public class CookingStation : UnityEngine.Object
{
    public enum Status { NotDone, Done, Burnt }
    public class Item : UnityEngine.Object
    {
        public string name = "";
        public ItemData m_itemData = new();
    }
    public class ItemData { public int m_stack = 1; public Food m_shared = new(); }
    public class Food { public float m_food = 1, m_foodStamina, m_foodEitr; }
    public class ItemConversion { public Item m_from = new() { name = "raw" }, m_to = new() { name = "cooked" }; public float m_cookTime = 10; }
    public ZNetView m_nview = new();
    public object[] m_slots = new object[1];
    public List<ItemConversion> m_conversion = new() { new() };
    public string ItemName = "";
    public float Progress;
    public Status SlotStatus;
    public void GetSlot(int slot, out string name, out float progress, out Status status, out int unused)
    { name = ItemName; progress = Progress; status = SlotStatus; unused = 0; }
    public void RPC_AddItem(long sender, string item) { }
    public void UpdateCooking() { }
    public void RPC_RemoveDoneItem() { }
    public void DropAllItems() { }
}
public static class StringExtensions
{
    public static int GetStableHashCode(this string value) => value.GetHashCode(StringComparison.Ordinal);
}
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
namespace BepInEx.Configuration
{
    internal sealed class ConfigEntry<T>(T value) { public T Value { get; set; } = value; }
    internal sealed class ConfigFile
    {
        public Dictionary<string, object> Values { get; } = new();
        public ConfigEntry<T> Bind<T>(string section, string key, T value, string description) =>
            new(Values.TryGetValue(section + "/" + key, out var configured) ? (T)configured : value);
    }
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
    public class Logger
    {
        public List<string> Errors = new();
        public void LogError(string message) => Errors.Add(message);
        public void LogWarning(string message) => Errors.Add(message);
    }
    public static class DiscordConnectorPlugin
    {
        public static Logger StaticLogger = new();
    }
}
namespace DiscordConnector.Patches
{
    // Only the cooking adapter seam is exercised here, not runtime sampling or ingress authentication.
    internal sealed class ActivityRuntime
    {
        internal static ActivityRuntime? Current;
        internal Records.CookingTracker Cooking = null!;
        internal Records.LeaderboardsOptions Options = new(cookingCompletions: true);
    }
    internal static class StablePlayerResolver
    {
        internal static bool World(out string world)
        {
            world = ZNet.instance.GetWorldUID().ToString();
            return ZNet.instance && ZNet.instance.IsServer() && ZNet.instance.IsDedicated() && ZNet.instance.GetWorldUID() != 0;
        }
        internal static bool Current(Records.ActivityActor actor) => actor.ConnectionToken == "connection";
    }
    internal static class ActivityPatches
    {
        internal sealed class Ingress
        {
            internal bool Failed, Data;
            internal Records.ActivityActor? Actor;
            internal int MethodHash = "RPC_AddItem".GetStableHashCode();
            internal ZDOID Target;
            internal long Sender = 7;
            internal string CookingItem = "raw";
        }
        internal static Ingress? Sender;
        internal static void Fail(Exception error) => throw new Exception("Cooking adapter failed", error);
        internal static void Stage(Action capture, Action invalidate) => capture();
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
}
