// Counting native/Harmony doubles only. Production adapters and their helpers are source-linked.
using System.Reflection;
using System.Text;

namespace UnityEngine
{
    public class Object
    {
        public static implicit operator bool(Object? value) => value != null;
    }
    public struct Vector3 { public float x, y, z; public static Vector3 zero => default; }
    public class Transform { public Vector3 position; }
    public class GameObject : Object
    {
        public string name = "prefab";
        public readonly Dictionary<Type, object> Components = new();
        public T? GetComponent<T>() where T : class => Components.GetValueOrDefault(typeof(T)) as T;
    }
    public class Component : Object
    {
        public GameObject gameObject = new();
        public Transform transform = new();
        public string name = "item";
        public T? GetComponent<T>() where T : class => gameObject.GetComponent<T>();
    }
    public class Texture : Object { public int width, height; }
    public static class Time { public static double realtimeSinceStartupAsDouble = 100; }
}

public struct ZDOID
{
    public static readonly List<long> m_userIDs = new() { 0, 1 };
    public long UserID;
    public uint ID;
    public ZDOID(long user, uint id) { UserID = user; ID = id; }
    public static ZDOID None => default;
    public bool IsNone() => this == None;
    public static bool operator ==(ZDOID a, ZDOID b) => a.UserID == b.UserID && a.ID == b.ID;
    public static bool operator !=(ZDOID a, ZDOID b) => !(a == b);
    public override bool Equals(object? value) => value is ZDOID id && this == id;
    public override int GetHashCode() => HashCode.Combine(UserID, ID);
    public override string ToString() => UserID + ":" + ID;
}
public sealed class ZPackage
{
    private readonly MemoryStream stream;
    private readonly BinaryReader reader;
    private readonly BinaryWriter writer;
    public static int ParameterCopies, ArrayCopies;
    public int Reads, PositionsSet, Copies;
    public int CopyPosition, CopyReads, CopyPositionsSet, ThrowOnRead;
    public readonly List<string> FixedReads = new();
    public ZPackage() : this(Array.Empty<byte>()) { }
    public ZPackage(byte[] bytes, bool nonExposable = false)
    {
        // Optional robustness mode; native constructors use MemoryStream() followed by Write.
        stream = nonExposable ? new MemoryStream(bytes) : new MemoryStream();
        if (!nonExposable) stream.Write(bytes);
        stream.Position = 0;
        reader = new(stream, Encoding.UTF8, true); writer = new(stream, Encoding.UTF8, true);
    }
    public byte[] GetArray()
    {
        CopyPosition = GetPos(); CopyReads = Reads; CopyPositionsSet = PositionsSet;
        ArrayCopies++; Copies++; writer.Flush(); return stream.ToArray();
    }
    public int Size() { writer.Flush(); return (int)stream.Length; }
    public int GetPos() => (int)stream.Position;
    public void SetPos(int position) { PositionsSet++; stream.Position = position; }
    private void FixedRead(string kind)
    {
        Reads++; FixedReads.Add(kind);
        if (Reads == ThrowOnRead) { reader.ReadByte(); throw new EndOfStreamException("injected partial read"); }
    }
    public long ReadLong() { FixedRead("long"); return reader.ReadInt64(); }
    public int ReadInt() { FixedRead("int"); return reader.ReadInt32(); }
    public uint ReadUInt() { FixedRead("uint"); return reader.ReadUInt32(); }
    public ushort ReadUShort() { Reads++; return reader.ReadUInt16(); }
    public string ReadString() { Reads++; return reader.ReadString(); }
    public bool ReadBool() { Reads++; return reader.ReadBoolean(); }
    public UnityEngine.Vector3 ReadVector3() { Reads++; return new() { x = reader.ReadSingle(), y = reader.ReadSingle(), z = reader.ReadSingle() }; }
    public ZPackage ReadPackage() { ParameterCopies++; int size = ReadInt(); return new(reader.ReadBytes(size)); }
    public void Write(long value) => writer.Write(value);
    public void Write(uint value) => writer.Write(value);
    public void Write(int value) => writer.Write(value);
    public void Write(string value) => writer.Write(value);
    public void Write(bool value) => writer.Write(value);
    public void Write(UnityEngine.Vector3 value) { writer.Write(value.x); writer.Write(value.y); writer.Write(value.z); }
    public void Write(ZPackage value) { var bytes = value.GetArray(); writer.Write(bytes.Length); writer.Write(bytes); }
}
public class Socket
{
    public static int HostReads, ConnectedReads;
    public bool Connected = true;
    public string Host = "account";
    public bool IsConnected() { ConnectedReads++; return Connected; }
    public string GetHostName() { HostReads++; return Host; }
}
public class ZRpc
{
    public Socket Socket;
    public ZRpc(Socket socket) => Socket = socket;
    public Socket GetSocket() => Socket;
}
public class ZNetPeer
{
    public static int ReadyReads;
    public long m_uid;
    public ZDOID m_characterID;
    public string m_playerName = "Player";
    public bool m_server, Ready = true;
    public Socket m_socket = new();
    public ZRpc m_rpc = null!;
    public bool IsReady() { ReadyReads++; return Ready; }
}
public class PlatformId
{
    public bool IsValid = true;
}
public class UserInfo
{
    public PlatformId m_id = new();
    public void Deserialize(ref ZPackage package) { }
}
public class World
{
    public class History { public PlatformId m_id = new(); }
    public List<History> m_playerHistory = new();
}
public class ZNet : UnityEngine.Object
{
    public class PlayerInfo { public ZDOID m_characterID; public UserInfo m_userInfo = new(); }
    public static ZNet instance = new();
    public static World? World = new();
    public bool Server = true, Dedicated = true, Admin = true;
    public Exception? WorldFailure;
    public long WorldUid = 50;
    public long ServerUid = 1;
    public List<ZNetPeer> Peers = new();
    public List<PlayerInfo> Players = new();
    public int PeerReads, PlayerReads;
    public List<ZNetPeer> GetPeers() { PeerReads++; return Peers; }
    public List<PlayerInfo> GetPlayerList() { PlayerReads++; return Players; }
    public bool IsServer() => Server;
    public bool IsDedicated() => Dedicated;
    public long GetWorldUID() { if (WorldFailure != null) throw WorldFailure; return WorldUid; }
    public static long GetUID() => instance.ServerUid;
    public bool IsAdmin(string id) => Admin;
}
public class ZRoutedRpc
{
    public class RoutedRPCData
    {
        public long m_msgID, m_senderPeerID, m_targetPeerID;
        public ZDOID m_targetZDO;
        public int m_methodHash;
        public ZPackage m_parameters = new();
    }
    public void RPC_RoutedRPC(ZRpc rpc, ZPackage package) { }
    public void RouteRPC(RoutedRPCData data) { }
    public void HandleRoutedRPC(RoutedRPCData data) { }
    public void InvokeRoutedRPC(long peer, ZDOID target, string method, params object[] args) { }
}
public class ZDO
{
    public ZDOID m_uid;
    public uint DataRevision;
    public ushort OwnerRevision;
    public long Owner = 1;
    public long GetOwner() => Owner;
    public void SetOwner(long value) => Owner = value;
    public void SetOwnerInternal(long value) => Owner = value;
    public void Deserialize() { }
    public readonly Dictionary<int, long> Longs = new();
    public readonly Dictionary<int, int> Ints = new();
    public readonly Dictionary<int, float> Floats = new();
    public int FloatReads;
    public int Prefab;
    public long GetLong(int key, long fallback) => Longs.GetValueOrDefault(key, fallback);
    public int GetInt(int key, int fallback) => Ints.GetValueOrDefault(key, fallback);
    public float GetFloat(int key, float fallback) { FloatReads++; return Floats.GetValueOrDefault(key, fallback); }
    public string GetString(int key, string fallback) => fallback;
    public byte[] GetByteArray(int key) => Array.Empty<byte>();
    public int GetPrefab() => Prefab;
    public UnityEngine.Vector3 Position;
    public UnityEngine.Vector3 GetPosition() => Position;
    public bool IsCheated() => false;
}
public class ZDOMan
{
    public static ZDOMan instance = new();
    public Dictionary<ZDOID, ZDO> Zdos = new();
    public ZDO? GetZDO(ZDOID id) => Zdos.GetValueOrDefault(id);
    public void RPC_ZDOData() { }
    public void CreateNewZDO() { }
    public void HandleDestroyedZDO() { }
}
public class ZNetView : UnityEngine.Component
{
    public ZDO Zdo = new();
    public ZDO GetZDO() => Zdo;
    public bool IsValid() => true;
    public bool IsOwner() => Zdo.GetOwner() == ZNet.GetUID();
}
public class ZNetScene : UnityEngine.Object
{
    public static ZNetScene instance = new();
    public Dictionary<ZDO, ZNetView> Views = new();
    public Dictionary<int, UnityEngine.GameObject> Prefabs = new();
    public UnityEngine.GameObject? GetPrefab(int hash) => Prefabs.GetValueOrDefault(hash);
    public ZNetView? FindInstance(ZDO zdo) => Views.GetValueOrDefault(zdo);
}
public static class ZDOVars
{
    public static int s_playerID = 1, s_creator = 2, s_creatorIndex = 3, s_emoteID = 4, s_emote = 5, s_itemData = 6, s_health = 7;
}
public class Chat { public void RPC_ChatMessage() { } }
public class Talker { public enum Type { Shout = 2 } }
public interface IMonoUpdater { }
public class Ship : UnityEngine.Component, IMonoUpdater
{
    public static List<IMonoUpdater> Instances = new();
    public ZNetView m_nview = new();
    public ShipControls m_shipControlls = new();
    public readonly HashSet<long> Passengers = new();
    public bool IsPlayerInBoat(long id) => Passengers.Contains(id);
}
public class ShipControls : UnityEngine.Object
{
    public long GetUser() => 0;
    public bool HaveValidUser() => false;
}
public class Skills { public enum SkillType { None = 0, Swords = 1, Bows = 8, ElementalMagic = 9, BloodMagic = 10, Crossbows = 14 } }
public class HitData { public ZDOID m_attacker; public Skills.SkillType m_skill; }
public class Character : UnityEngine.Component
{
    public ZNetView m_nview = new();
    public double Health = 100;
    public bool Player, Tamed, Dead;
    public bool IsPlayer() => Player;
    public bool IsTamed() => Tamed;
    public bool IsDead() => Dead;
    public float GetHealth() => (float)Health;
    public void ApplyDamage(HitData hit, bool showDamageText, bool triggerEffects, object mod) { }
    public void OnDeath() { }
}
public class Piece : UnityEngine.Component
{
    public class Requirement { public ItemDrop m_resItem = new(); public int m_amount; }
    public Requirement[] m_resources = Array.Empty<Requirement>();
    public string FreeBuildKey() => "";
    public void DropResources() { }
}
public class Player : UnityEngine.Object
{
    public void PlacePiece() { }
    public bool NoCostCheat() => false;
    public static bool IsPlacementGhost(UnityEngine.GameObject gameObject) => false;
}
public class Plant : UnityEngine.Component
{
    public UnityEngine.GameObject[] m_grownPrefabs = Array.Empty<UnityEngine.GameObject>();
    public void Grow() { }
}
public class Pickable : UnityEngine.Component { }
public class ZoneSystem : UnityEngine.Object
{
    public static ZoneSystem instance = new();
    public readonly HashSet<string> Keys = new();
    public bool GetGlobalKey(string key) => Keys.Contains(key);
    public void RPC_SetGlobalKey(long sender, string key) { if (Keys.Contains(key)) return; Keys.Add(key); }
}
public class Heightmap { public enum Biome { None, Meadows, BlackForest, Swamp, Mountain, Plains, Ocean, Mistlands, AshLands, DeepNorth } }
public class WorldGenerator
{
    public static WorldGenerator? instance = new();
    public Heightmap.Biome Biome = Heightmap.Biome.Meadows;
    public int Reads;
    public float X, Z;
    public Heightmap.Biome GetBiome(float x, float z, float oceanLevel = .02f, bool waterAlwaysOcean = false)
    { Reads++; X = x; Z = z; return Biome; }
}
public class Minimap : UnityEngine.Object
{
    public static Minimap? instance;
    public bool[]? m_explored;
    public UnityEngine.Texture? m_mapTexture;
    public int m_textureSize;
    public float m_pixelSize, m_exploreRadius;
}
public class ItemDrop : UnityEngine.Component
{
    public class Shared { public int m_maxStackSize = 100; public float m_food = 1, m_foodStamina, m_foodEitr; }
    public class ItemData
    {
        public int m_stack = 1;
        public Shared m_shared = new();
        public ItemData Clone() => new();
        public void Save(ZPackage package) { }
    }
    public ItemData m_itemData = new();
    public ZNetView m_nview = new();
    public static void LoadFromZDO(ItemData data, ZDO zdo) { }
    public void SaveToZDO() { }
    public void AutoStackItems() { }
    public void TimedDestruction() { }
    public void RemoveOne() { }
    public void Eat() { }
    public void OnPlayerDrop() { }
}
public class CookingStation : UnityEngine.Object
{
    public enum Status { NotDone, Done, Burnt }
    public class ItemConversion { public ItemDrop m_from = new() { name = "raw" }, m_to = new() { name = "cooked" }; public float m_cookTime = 10; }
    public ZNetView m_nview = new();
    public object[] m_slots = new object[1];
    public List<ItemConversion> m_conversion = new() { new() };
    public string ItemName = "";
    public float Progress;
    public Status SlotStatus;
    public void GetSlot(int slot, out string name, out float progress, out Status status, out int unused)
    { name = ItemName; progress = Progress; status = SlotStatus; unused = 0; }
    public void RPC_AddItem(long sender, string item, bool cookFromInventory) { }
    public void UpdateCooking() { }
    public void RPC_RemoveDoneItem() { }
    public void DropAllItems() { }
}
public static class StringExtensions
{
    public static int GetStableHashCode(this string value)
    {
        unchecked
        {
            int a = 5381, b = a;
            for (int i = 0; i < value.Length && value[i] != '\0'; i += 2)
            {
                a = ((a << 5) + a) ^ value[i];
                if (i == value.Length - 1 || value[i + 1] == '\0') break;
                b = ((b << 5) + b) ^ value[i + 1];
            }
            return a + b * 1566083941;
        }
    }
}
namespace HarmonyLib
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
    public class HarmonyPatch : Attribute
    {
        public Type? TargetType;
        public string? MethodName;
        public Type[] Arguments = Array.Empty<Type>();
        public HarmonyPatch() { }
        public HarmonyPatch(Type type, string method, params Type[] args) { TargetType = type; MethodName = method; Arguments = args; }
    }
    public class HarmonyPrefix : Attribute { }
    public class HarmonyPostfix : Attribute { }
    public class HarmonyFinalizer : Attribute { }
    public class HarmonyTargetMethods : Attribute { }
    public class HarmonyPriority(int value) : Attribute { public int Value = value; }
    public class HarmonyBefore(params string[] owners) : Attribute { public string[] Owners = owners; }
    public class HarmonyAfter(params string[] owners) : Attribute { public string[] Owners = owners; }
    public static class Priority { public const int First = 800, Last = 0; }
    public static class AccessTools
    {
        public static MethodInfo Method(Type type, string method, Type[]? args = null) =>
            (args == null ? type.GetMethod(method) : type.GetMethod(method, args))!;
    }
    public class Patch
    {
        public string owner = "test";
        public MethodInfo PatchMethod = null!;
        public int priority;
        public string[] before = Array.Empty<string>(), after = Array.Empty<string>();
    }
    public class Patches
    {
        public List<Patch> Prefixes = new(), Finalizers = new(), Postfixes = new(), Transpilers = new();
    }
    public class Harmony
    {
        public static Dictionary<MethodBase, Patches> Inventory = new();
        public static int Inspections;
        public static int Unpatches;
        public static Patches? GetPatchInfo(MethodBase method) { Inspections++; return Inventory.GetValueOrDefault(method); }
        public static Harmony CreateAndPatchAll(Assembly assembly, string owner) { Program.InstallHooks(owner, assembly); return new(); }
        public void UnpatchSelf() { Unpatches++; }
    }
}
namespace BepInEx.Configuration
{
    public sealed class ConfigEntry<T>(T value) { public T Value { get; set; } = value; }
    public sealed class ConfigFile
    {
        public Dictionary<string, object> Entries = new();
        public event EventHandler? SettingChanged;
        public void Changed() => SettingChanged?.Invoke(this, EventArgs.Empty);
        public ConfigEntry<T> Bind<T>(string section, string key, T value, string description)
        {
            var entry = new ConfigEntry<T>(value); Entries[section + "/" + key] = entry; return entry;
        }
    }
}
