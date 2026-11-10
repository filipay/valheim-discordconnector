using BepInEx.Configuration;

using DiscordConnector.Records;

namespace DiscordConnector.Config;

internal sealed class ObservedLeaderboardsConfig
{
    private readonly ConfigEntry<bool> participation, dodgeRolls, reportedKillCredits, consumableUses, reportedDodgeHits;

    internal ObservedLeaderboardsConfig(ConfigFile config)
    {
        participation = config.Bind("Server-Observed Combat", "Participation Enabled", false,
            "Observe distinct player biomes from fresh positions and boss-defeat global-key credits for the sender and players within 50m. Partial inferred participation, not confirmed combat. Independent of creature ownership and combat Enabled; retains the existing shared key.");
        dodgeRolls = config.Bind("Server-Observed Actions", "Dodge Rolls Enabled", false,
            "Observe validated vanilla dodge triggers for the sender's current character, not proof of avoided hits. Independent of combat/report schedules.");
        reportedKillCredits = config.Bind("Server-Reported Combat", "Kill Credits Enabled", false,
            "Collect reported kill credits by default. With Dedicated Simulation and actual server-owned untamed victims, credit each observed damaging participant once per confirmed creature death instead; matching server-local reported credits are suppressed. Other kills retain the reported fallback. Partial coverage; independent of report schedules.");
        consumableUses = config.Bind("Server-Observed Actions", "Consumable Uses Enabled", false,
            "Observe native eat presentation signals for food, potions and other native eat paths, not verified consumption, inventory debit or food units. Partial coverage; independent of combat Enabled and report schedules.");
        reportedDodgeHits = config.Bind("Server-Reported Combat", "Dodge Hit Notifications Enabled", false,
            "Collect reported dodge-hit notifications with partial server visibility and possible multiple notifications per dodge. Not proof of accepted rewards or unique attacks; independent of combat Enabled and report schedules.");
    }

    // Inclusion uses only each metric's own flag: capability loss must not hide valid stored history.
    internal LeaderboardsOptions Snapshot(ActivityConfig activities) => new(
        participation.Value, participation.Value, dodgeRolls.Value,
        activities.Sailing, activities.FloorPickups, activities.Construction, activities.Exploration, activities.CookingCompletions,
        reportedKillCredits.Value, consumableUses.Value, reportedDodgeHits.Value, activities.CropsPlanted, activities.HealthLost,
        activities.OnFoot, activities.BiomeTime, activities.OnlineTime, activities.PortalTrips, activities.DamageDealt, activities.LongestBowHit);
}
