using BepInEx.Configuration;

namespace DiscordConnector.Config;

internal sealed class ActivityConfig
{
    private readonly ConfigEntry<bool> sailing, floorPickups, construction, exploration, cookingCompletions, cropsPlanted, healthLost;
    private readonly ConfigEntry<bool> onFoot, biomeTime, onlineTime, portalTrips;
    private readonly ConfigEntry<bool> damageDealt, longestBowHit;

    internal ActivityConfig(ConfigFile config)
    {
        const string section = "Server-Observed Activities";
        longestBowHit = config.Bind(section, "Longest Bow Hit Enabled", false,
            "Observe each player's longest damaging Bows-skill hit on server-owned untamed creatures with Dedicated Simulation present; no fallback without the mod or for client-owned victims. Uses 3D victim-to-latest-attacker-ZDO distance at hit time, not launch distance. Attacker position must have matching owner/revision and be at most 2.5 seconds old; distances over 250m are rejected (headroom above roughly 200m normal bow range). Partial visibility; crossbows and magic excluded.");
        damageDealt = config.Bind(section, "Damage Dealt Enabled", false,
            "Observe actual net HP removed by resolved players from server-owned untamed creatures when Dedicated Simulation is present. Overkill is capped at pre-hit health. Partial: client-owned victims and unattributed damage are excluded; no reported-damage substitute.");
        onFoot = config.Bind(section, "On Foot Distance Enabled", false,
            "Observe horizontal player distance from consecutive fresh two-second samples. Gaps, speed spikes, respawns and detected ship passengers are excluded. Partial: swimming, mounts and unseen ships cannot always be distinguished from walking.");
        biomeTime = config.Bind(section, "Biome Time Enabled", false,
            "Observe time between fresh two-second positions in the same biome. The Hearthkeeper ranks the largest accumulated single-biome time per player; gaps and biome transitions count zero. Independent of Participation Enabled.");
        onlineTime = config.Bind(section, "Online Time Enabled", false,
            "Observe connected ready peer time, including idle time. Sessions checkpoint at most every ten seconds; disconnect, shutdown and restart never accrue offline time. Abrupt exit can lose unflushed time. Independent of legacy online-time boards.");
        portalTrips = config.Bind(section, "Portal Trips Enabled", false,
            "Infer portal trips from fresh same-character position jumps of at least 100m within 2.5 seconds, deduplicated for ten seconds. Not proof of portal use: admin/mod teleports can count and short trips or delayed replication can be missed.");
        sailing = config.Bind(section, "Sailing Enabled", false,
            "Observe horizontal sailing distance in meters while at a moving helm, from fresh server signals. Partial coverage; independent of combat and daily/weekly reporting.");
        healthLost = config.Bind(section, "Health Lost Enabled", false,
            "Observe net player health decreases between fresh two-second ZDO samples. Partial: healing and hits can net out, player-owned replication can lag, and death/respawn samples are excluded. Independent of combat and reporting.");
        floorPickups = config.Bind(section, "Floor Pickups Enabled", false,
            "Observe inferred floor-pickup units, with a proven-fresh subtotal where provenance permits. Not proof of gathering; independent of combat/report schedules.");
        construction = config.Bind(section, "Construction Enabled", false,
            "Observe new Piece placements, not material spending. Independent of combat/report schedules.");
        cropsPlanted = config.Bind(section, "Crops Planted Enabled", false,
            "Count observed planted crop pieces with a known creator after capture starts. Plant grown forms must be Pickables; trees, loaded plants and growth replacements are excluded. Partial coverage, not seed debit or verified placement; independent of combat/report schedules.");
        exploration = config.Bind(section, "Exploration Enabled", false,
            "Observe first runtime-grid cells per account/world, not private client map history. Missing/mismatched runtime grid is unavailable, not zero; independent of combat/report schedules.");
        cookingCompletions = config.Bind(section, "Cooking Completions Enabled", false,
            "Credit accepted inserters for validated server-owned rack/oven completed output units at completion time. Independent of combat Enabled. Partial coverage; no eating, cauldron crafting, retrieval or unknown restart provenance.");
    }

    internal bool Sailing => sailing.Value;
    internal bool FloorPickups => floorPickups.Value;
    internal bool Construction => construction.Value;
    internal bool Exploration => exploration.Value;
    internal bool CookingCompletions => cookingCompletions.Value;
    internal bool CropsPlanted => cropsPlanted.Value;
    internal bool HealthLost => healthLost.Value;
    internal bool OnFoot => onFoot.Value;
    internal bool BiomeTime => biomeTime.Value;
    internal bool OnlineTime => onlineTime.Value;
    internal bool PortalTrips => portalTrips.Value;
    internal bool DamageDealt => damageDealt.Value;
    internal bool LongestBowHit => longestBowHit.Value;
}
