// Plugin collaborators only; lifecycle bodies, native adapters and database ownership are production code.
using DiscordConnector.Records;

namespace BepInEx
{
    public class BepInPlugin(string guid, string name, string version) : Attribute { }
    public class BaseUnityPlugin
    {
        public object Logger = new();
        public Configuration.ConfigFile Config = new();
    }
    public static class Paths
    {
        public static string ConfigPath = "", GameRootPath = "";
    }
}
namespace UnityEngine.Device { public static class SystemInfo { public static UnityEngine.Rendering.GraphicsDeviceType graphicsDeviceType; } }
namespace BepInEx.Bootstrap { public static class Chainloader { public static readonly Dictionary<string, object> PluginInfos = new(); } }
namespace UnityEngine.Rendering { public enum GraphicsDeviceType { Null } }
namespace DiscordConnector.Common
{
    public class VdcLogger(object logger, string directory)
    {
        public readonly List<string> Warnings = new();
        public bool Throw;
        public void LogWarning(string message) { Warnings.Add(message); if (Throw) throw new IOException("secret"); }
        public void LogDebug(string message) { }
        public void LogInfo(string message) { }
        public void LogError(string message) { }
    }
    public static class Strings { public static string HumanReadableMs(double value) => ""; }
}
namespace DiscordConnector.Config
{
    internal static class MainConfig { internal enum RetrievalDiscernmentMethods { PlayerId, NameAndPlayerId, Name } }
    internal sealed class PluginConfig(BepInEx.Configuration.ConfigFile config)
    {
        internal sealed class Settings { public string Url = ""; public bool Enabled = false; public int PeriodInMinutes = 1; }
        internal Settings PrimaryWebhook = new(), SecondaryWebhook = new(), ActivePlayersAnnouncement = new();
        internal Settings[] LeaderBoards = Enumerable.Range(0, 5).Select(_ => new Settings()).ToArray();
        internal bool DebugDatabaseMethods => false;
        internal bool DebugLeaderboardOperations => false;
        internal bool CollectStatsEnabled => true;
        internal MainConfig.RetrievalDiscernmentMethods RecordRetrievalDiscernmentMethod => MainConfig.RetrievalDiscernmentMethods.PlayerId;
    }
}
namespace DiscordConnector
{
    public class EventWatcher { }
    internal static class PublicIPChecker { internal static string GetPublicIP() => throw new Exception("No HTTP allowed"); }
    internal static class ActivePlayersAnnouncement { internal static void SendOnTimer(object? sender, System.Timers.ElapsedEventArgs args) { } }
    internal static class Webhook { internal enum Event { LeaderboardsAll } }
    internal static class DiscordApi { internal static void SendRenderedMessage(Webhook.Event kind, Leaderboards.LeaderboardReport report) => throw new Exception("No HTTP allowed"); }
}
namespace DiscordConnector.Leaderboards
{
    internal sealed class LeaderbBoard
    {
        internal class Board { public void SendLeaderBoardOnTimer(object? sender, System.Timers.ElapsedEventArgs args) { } }
        internal Board LeaderBoard1 = new(), LeaderBoard2 = new(), LeaderBoard3 = new(), LeaderBoard4 = new();
    }
    internal enum TimeRange { AllTime }
    internal static class DateHelper { internal static Tuple<DateTime, DateTime> StartEndDatesForTimeRange(TimeRange range) => throw new NotSupportedException(); }
    internal static class Categories { internal const string Death = "death", Join = "join", Leave = "leave", Ping = "ping", Shout = "shout", TimeOnline = "time"; }
    internal sealed class ManualLeaderboardReports : IDisposable
    {
        internal Exception? Failure, DisposeFailure;
        internal int Polls;
        internal readonly Func<string, DateTime, DateTime, Task<CombinedReportSnapshot>> Query;
        internal ManualLeaderboardReports(string zone, int top, Func<string, DateTime, DateTime, Task<CombinedReportSnapshot>> query,
            Func<DateTime, string[]> flush, Action<LeaderboardReport> send, Action<string> warn, Action<string>? diagnostic = null) { Query = query; }
        internal void Poll(double time, string world) { Polls++; if (Failure != null) throw Failure; }
        internal void Request(string text, bool authorized, DateTime utc, double time, string world) { }
        public void Dispose() { if (DisposeFailure != null) throw DisposeFailure; }
    }
    internal sealed class CombatReports : IDisposable
    {
        internal int Polls, Ticks, Disposals;
        internal Exception? PollFailure, TickFailure;
        internal readonly Func<string, DateTime, DateTime, Task<CombinedReportSnapshot>> Query;
        internal CombatReports(bool daily, bool weekly, string zone, int top,
            Func<string, DateTime, DateTime, Task<CombinedReportSnapshot>> query, Func<string, DateTime, Task<bool>> claim,
            Func<DateTime, string[]> flush, Action<LeaderboardReport> send, Action<string> warn, Action<string>? diagnostic = null) { Query = query; }
        internal void Poll(DateTime utc, string world) { Polls++; if (PollFailure != null) throw PollFailure; }
        internal void Tick(DateTime utc, string world) { Ticks++; if (TickFailure != null) throw TickFailure; }
        public void Dispose() => Disposals++;
    }
}
