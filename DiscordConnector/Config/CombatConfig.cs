using BepInEx.Configuration;

namespace DiscordConnector.Config;

internal sealed class CombatConfig
{
    private readonly ConfigEntry<bool> enabled;
    private readonly ConfigEntry<bool> dailyEnabled;
    private readonly ConfigEntry<bool> weeklyEnabled;
    private readonly ConfigEntry<string> timeZoneId;
    private readonly ConfigEntry<int> topCount;

    internal CombatConfig(ConfigFile config)
    {
        const string section = "Server-Observed Combat";
        enabled = config.Bind(section, "Enabled", false,
            "Track attributable server-owned creature deaths only. Partial coverage, not all player kills.");
        dailyEnabled = config.Bind(section, "Daily Enabled", false,
            "Send one combined report after a local day completes, via leaderboardsAll. No startup history/backlog; at-most-once attempt, no delivery retry.");
        weeklyEnabled = config.Bind(section, "Weekly Enabled", false,
            "Send one combined report after a local Monday-Sunday week completes, via leaderboardsAll. No startup history/backlog; at-most-once attempt, no delivery retry.");
        timeZoneId = config.Bind(section, "Time Zone", "Europe/Dublin",
            "Timezone for new combat reports only. Invalid values disable reports, not tracking. Report settings take effect on restart.");
        topCount = config.Bind(section, "Top Count", 5,
            "Players per combat ranking (1-16). Long reports are truncated. Existing leaderboard defaults are unchanged.");
    }

    public bool Enabled => enabled.Value;
    public bool DailyEnabled => dailyEnabled.Value;
    public bool WeeklyEnabled => weeklyEnabled.Value;
    public string TimeZoneId => timeZoneId.Value;
    public int TopCount => topCount.Value;
}
