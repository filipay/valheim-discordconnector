namespace BepInEx.Configuration
{
    internal sealed class ConfigEntry<T>(T value)
    {
        public T Value { get; set; } = value;
    }

    internal sealed class ConfigFile
    {
        public Dictionary<string, object> Values { get; } = new();
        public ConfigEntry<T> Bind<T>(string section, string key, T value, string description) =>
            new(Values.TryGetValue(section + "/" + key, out object? configured) || Values.TryGetValue(key, out configured) ? (T)configured : value);
    }
}

namespace DiscordConnector
{
    internal static class DiscordConnectorPlugin
    {
        internal static readonly CaptureLogger StaticLogger = new();
        internal static readonly PluginConfig StaticConfig = new();
        internal static string PublicIpAddress => "test-ip";
    }

    internal sealed class CaptureLogger
    {
        internal List<string> Messages { get; } = new();
        internal void LogDebug(object? message) => Messages.Add(message?.ToString() ?? "");
        internal void LogInfo(object? message) => LogDebug(message);
        internal bool ThrowWarning;
        internal void LogWarning(object? message)
        {
            LogDebug(message);
            if (ThrowWarning) throw new InvalidOperationException("secret warning failure");
        }
        internal void LogError(object? message) => LogDebug(message);
    }

    internal sealed class PluginConfig
    {
        public bool DiscordEmbedsEnabled { get; set; }
        public bool DebugLeaderboardOperations { get; set; }
        // Empty URLs exercise the real serialization/routing path but return before HTTP dispatch.
        public WebhookEntry PrimaryWebhook { get; } = new("");
        public WebhookEntry SecondaryWebhook { get; } = new("", new());
        public List<WebhookEntry> ExtraWebhooks { get; } = new();
        public string DefaultWebhookUsernameOverride => "";
        public bool AllowMentionsHereEveryone => true;
        public bool AllowMentionsAnyRole => true;
        public bool AllowMentionsAnyUser => true;
        public List<string> AllowedRoleMentions { get; } = new() { "456" };
        public List<string> AllowedUserMentions { get; } = new() { "123" };
        public string UserVariable { get; set; } = "";
        public string UserVariable1 { get; set; } = "";
        public string UserVariable2 => "";
        public string UserVariable3 => "";
        public string UserVariable4 => "";
        public string UserVariable5 => "";
        public string UserVariable6 => "";
        public string UserVariable7 => "";
        public string UserVariable8 => "";
        public string UserVariable9 => "";
        public string ServerName => "Test server";
        public string PosVarFormat => "%X%, %Y%, %Z%";
        public string AppendedPosFormat => "%POS%";
        public bool ChatShoutAllCaps => false;
        public bool PlayerDeathPosEnabled => false;
        public bool ShowPlayerIds => false;
        public bool SendPositionsEnabled => false;
        public bool EmbedTitleEnabled => true;
        public bool EmbedDescriptionEnabled { get; set; } = true;
        public bool EmbedAuthorEnabled => true;
        public bool EmbedFooterEnabled => true;
        public bool EmbedTimestampEnabled => true;
        public bool EmbedThumbnailEnabled => false;
        public string EmbedUrlTemplate => "";
        public string EmbedFooterText => "";
        public string EmbedAuthorIconUrl => "";
        public string EmbedThumbnailUrl => "";
        public List<string> EmbedFieldDisplayOrder { get; } = new();
        public string EmbedServerStartColor => "#43B581";
        public string EmbedServerStopColor => "#F04747";
        public string EmbedPlayerJoinColor => "#43B581";
        public string EmbedPlayerLeaveColor => "#FAA61A";
        public string EmbedDeathEventColor => "#F04747";
        public string EmbedShoutMessageColor => "#7289DA";
        public string EmbedPositionMessageColor => "#7289DA";
        public string EmbedWorldEventColor => "#747F8D";
        public string EmbedOtherEventColor => "#747F8D";
        public string EmbedNewDayColor => "#747F8D";
        public string EmbedServerSaveColor => "#747F8D";
        public string EmbedActivePlayersColor => "#747F8D";
        public string EmbedLeaderboardEmbedColor => "#747F8D";
    }

    internal static class GuidHelper
    {
        internal static string GenerateShortHexGuid() => throw new Exception("HTTP dispatch must not run in this harness");
    }
}

namespace UnityEngine
{
    internal struct Vector3 { public float x { get; set; } public float y { get; set; } public float z { get; set; } }
}

internal sealed class ZNet
{
    internal static ZNet? instance = null;
    internal string GetWorldName() => "Test world";
    internal int GetNrOfPlayers() => 0;
}

internal sealed class EnvMan
{
    internal static EnvMan? instance = null;
    internal int GetCurrentDay() => 0;
}

internal static class ZPlayFabMatchmaking { internal static string JoinCode => "test-join-code"; }
internal sealed class TestSocket { internal string GetHostName() => "test-host"; }
internal sealed class ZNetPeer
{
    internal string m_playerName = "Test player";
    internal TestSocket m_socket = new();
    internal UnityEngine.Vector3 m_refPos = new();
}
