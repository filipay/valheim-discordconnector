using BepInEx.Configuration;

namespace DiscordConnector.Config;

internal sealed class ManualLeaderboardTriggerConfig
{
    private readonly ConfigEntry<bool> enabled;
    private readonly ConfigEntry<string> daily, weekly;
    internal ManualLeaderboardTriggerConfig(ConfigFile config)
    {
        const string section = "Manual Leaderboard Triggers";
        enabled = config.Bind(section, "Emote Triggers Enabled", false,
            "Allow current server admins to request manual previews using accepted emote replication. Shares chat's cooldown and single job.");
        daily = config.Bind(section, "Daily Report Emote", "flex",
            "Native emote name for today's preview. Case/whitespace normalized; invalid/empty disables this binding, identical bindings disable both. Restart after editing bindings.");
        weekly = config.Bind(section, "Weekly Report Emote", "toast",
            "Native emote name for the Monday-so-far preview. Case/whitespace normalized; invalid/empty disables this binding, identical bindings disable both. Restart after editing bindings.");
    }
    internal bool Enabled => enabled.Value;
    internal string Daily => daily.Value;
    internal string Weekly => weekly.Value;
}
