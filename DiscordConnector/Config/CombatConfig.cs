using BepInEx.Configuration;

namespace DiscordConnector.Config;

internal sealed class CombatConfig
{
    private readonly ConfigEntry<bool> dailyEnabled;
    private readonly ConfigEntry<bool> weeklyEnabled;
    private readonly ConfigEntry<string> timeZoneId;
    private readonly ConfigEntry<int> topCount;

    internal CombatConfig(ConfigFile config)
    {
        const string section = "Server-Observed Combat";
        dailyEnabled = config.Bind(section, "Daily Enabled", false,
            "Send one combined report after a local day completes, via leaderboardsAll. No startup history/backlog; at-most-once attempt, no delivery retry.");
        weeklyEnabled = config.Bind(section, "Weekly Enabled", false,
            "Send one combined report after a local Monday-Sunday week completes, via leaderboardsAll. No startup history/backlog; at-most-once attempt, no delivery retry.");
        timeZoneId = config.Bind(section, "Time Zone", "Europe/Dublin",
            "Timezone for combined server-observed reports only. Invalid values disable reports, not tracking. Report settings and inclusion flags take effect on restart; collection is independent of daily/weekly reporting.");
        topCount = config.Bind(section, "Top Count", 5,
            "Maximum rows per combined report section (1-16); one row per nonempty section is reserved before extras within one 1900-character report. Existing leaderboard defaults are unchanged.");
    }

    public bool DailyEnabled => dailyEnabled.Value;
    public bool WeeklyEnabled => weeklyEnabled.Value;
    public string TimeZoneId => timeZoneId.Value;
    public int TopCount => topCount.Value;
}
