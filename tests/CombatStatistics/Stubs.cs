namespace BepInEx
{
    internal static class Paths
    {
        internal static string ConfigPath { get; set; }
    }
}

namespace UnityEngine
{
    internal struct Vector3
    {
        internal float x { get; set; }
        internal float y { get; set; }
        internal float z { get; set; }
        internal static Vector3 zero => default;
    }
}

namespace DiscordConnector
{
    internal static class DiscordConnectorPlugin
    {
        internal const string LegacyConfigPath = "DiscordConnector";
        internal const string LegacyModName = "DiscordConnector";
        internal static readonly TestLogger StaticLogger = new();
        internal static readonly TestConfig StaticConfig = new();
        internal static Records.Database StaticDatabase { get; set; }
    }

    internal sealed class TestLogger
    {
        internal void LogDebug(string message) { }
        internal void LogError(string message) { }
        internal void LogWarning(string message) { }
    }

    internal sealed class TestConfig
    {
        internal bool DebugDatabaseMethods => false;
        internal bool CollectStatsEnabled => true;
        internal Config.MainConfig.RetrievalDiscernmentMethods RecordRetrievalDiscernmentMethod =>
            Config.MainConfig.RetrievalDiscernmentMethods.PlayerId;
    }
}

namespace DiscordConnector.Config
{
    internal static class MainConfig
    {
        internal enum RetrievalDiscernmentMethods { PlayerId, NameAndPlayerId, Name }
    }
}

namespace DiscordConnector.Leaderboards
{
    internal enum TimeRange { AllTime }

    internal static class DateHelper
    {
        internal static Tuple<DateTime, DateTime> StartEndDatesForTimeRange(TimeRange range) =>
            throw new NotSupportedException("Legacy reporting is not under test.");
    }

    internal static class Categories
    {
        internal const string Death = "death", Join = "join", Leave = "leave", Ping = "ping", Shout = "shout", TimeOnline = "time";
    }
}
