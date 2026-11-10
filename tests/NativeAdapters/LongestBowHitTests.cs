using DiscordConnector.Patches;
using DiscordConnector.Records;

internal static partial class Program
{
    private static Character BowVictim(bool mod = true, long owner = 1, bool enabled = true)
    {
        flags = new(longestBowHit: enabled);
        if (mod) BepInEx.Bootstrap.Chainloader.PluginInfos[SimulationCombatPatches.ModGuid] = new();
        runtime!.Update(DateTime.UtcNow, 100);
        if (enabled) ReceivePosition(0, 100, new());
        return new Character { m_nview = new() { Zdo = new() { m_uid = new(1, 70), Owner = owner, Position = new() { x = 3, y = 4, z = 12 } } } };
    }
    private static void BowHit(Character victim, Skills.SkillType skill = Skills.SkillType.Bows, bool known = true, bool applied = true)
    {
        var hit = new HitData { m_attacker = known ? ZNet.instance.Peers[0].m_characterID : new(999, 1), m_skill = skill };
        SimulationCombatPatches.Damage.Prefix(victim, hit, out var capture);
        if (applied) victim.Health -= 1;
        SimulationCombatPatches.Damage.Postfix(victim, capture, applied);
        SimulationCombatPatches.Damage.Finalizer(capture, null);
    }
    private static void LongestBowChecks()
    {
        Test("bow-only flag captures fresh 3D hit distance and leaves other creature metrics off", () =>
        {
            var victim = BowVictim(); BowHit(victim);
            var records = CreatureRecords(ActivityKind.BowHit);
            Assert(records.Single().Meters == 13 && records[0].Actor.PlayerId == "account0", "3D bow-hit distance not recorded");
            Assert(!CreatureRecords(ActivityKind.CreatureDamage).Any() && !CreatureRecords(ActivityKind.CreatureKill).Any(), "bow flag enabled other stats");
        });
        Test("nonbow skills including crossbows and magic cannot create bow-hit records", () =>
        {
            var victim = BowVictim();
            foreach (var skill in new[] { Skills.SkillType.None, Skills.SkillType.Swords, Skills.SkillType.Crossbows, Skills.SkillType.ElementalMagic, Skills.SkillType.BloodMagic }) BowHit(victim, skill);
            Assert(!CreatureRecords(ActivityKind.BowHit).Any(), "nonbow skill accepted");
        });
        Test("unknown bow attacker and unapplied hit cannot create distance records", () =>
        {
            var victim = BowVictim(); BowHit(victim, known: false); BowHit(victim, applied: false);
            SimulationCombatPatches.Damage.Prefix(victim, new HitData { m_attacker = ZNet.instance.Peers[0].m_characterID, m_skill = Skills.SkillType.Bows }, out var noDamage);
            SimulationCombatPatches.Damage.Postfix(victim, noDamage, true); SimulationCombatPatches.Damage.Finalizer(noDamage, null);
            Assert(!CreatureRecords(ActivityKind.BowHit).Any(), "unknown or unapplied hit accepted");
        });
        Test("bow distance rejects stale future revision-mismatched and owner-mismatched attacker positions", () =>
        {
            var victim = BowVictim();
            UnityEngine.Time.realtimeSinceStartupAsDouble = 102.5; BowHit(victim);
            UnityEngine.Time.realtimeSinceStartupAsDouble = 102.501; BowHit(victim);
            UnityEngine.Time.realtimeSinceStartupAsDouble = 99; BowHit(victim);
            ReceivePosition(0, 103, new());
            var attacker = ZDOMan.instance.GetZDO(ZNet.instance.Peers[0].m_characterID)!;
            attacker.DataRevision++; BowHit(victim);
            ReceivePosition(0, 104, new()); attacker.Owner = 12345; BowHit(victim);
            Assert(CreatureRecords(ActivityKind.BowHit).Single().Meters == 13, "inclusive freshness boundary or stale/mismatched position rejection failed");
        });
        Test("bow distance caps at inclusive 250m and rejects nonfinite endpoints", () =>
        {
            var victim = BowVictim(); victim.m_nview.GetZDO().Position = new() { z = 250 }; BowHit(victim);
            victim.m_nview.GetZDO().Position = new() { z = 250.01f }; BowHit(victim);
            victim.m_nview.GetZDO().Position = new() { x = float.NaN }; BowHit(victim);
            victim.m_nview.GetZDO().Position = new() { x = 13 };
            ReceivePosition(0, 100, new() { y = float.PositiveInfinity }); BowHit(victim);
            Assert(CreatureRecords(ActivityKind.BowHit).Single().Meters == 250, "cap or finite validation incorrect");
        });
        Test("bow positions can be reused after a sampler consumed freshness without consuming it again", () =>
        {
            var victim = BowVictim(); flags = new(longestBowHit: true, healthLost: true);
            runtime!.Update(DateTime.UtcNow, 100); BowHit(victim); BowHit(victim);
            Assert(CreatureRecords(ActivityKind.BowHit).Length == 2 && !CreatureRecords(ActivityKind.HealthLost).Any(), "hit freshness stole a sampler endpoint or rejected a valid consumed endpoint");
        });
        Test("bow flag off missing mod and client-owned victims have no distance fallback", () =>
        {
            var victim = BowVictim(enabled: false); BowHit(victim);
            Assert(!CreatureRecords(ActivityKind.BowHit).Any(), "disabled bow flag recorded");
            Reset(); victim = BowVictim(mod: false); BowHit(victim);
            Assert(!CreatureRecords(ActivityKind.BowHit).Any(), "missing mod recorded a proxy");
            Reset(); victim = BowVictim(owner: 10); BowHit(victim);
            Assert(!CreatureRecords(ActivityKind.BowHit).Any(), "client-owned victim recorded a proxy");
        });
    }
}
