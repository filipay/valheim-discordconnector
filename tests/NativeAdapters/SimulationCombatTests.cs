using DiscordConnector.Patches;
using DiscordConnector.Records;

internal static partial class Program
{
    private static Character SimulatedVictim(bool mod = true, long owner = 1)
    {
        flags = new(reportedKillCredits: true, damageDealt: true);
        if (mod) BepInEx.Bootstrap.Chainloader.PluginInfos[SimulationCombatPatches.ModGuid] = new();
        runtime!.Update(DateTime.UtcNow, UnityEngine.Time.realtimeSinceStartupAsDouble);
        return new Character { m_nview = new() { Zdo = new() { m_uid = new(1, 50), Owner = owner } } };
    }
    private static void SimulatedHit(Character victim, int peer, double loss, bool ran = true, Exception? error = null)
    {
        SimulationCombatPatches.Damage.Prefix(victim, new HitData { m_attacker = ZNet.instance.Peers[peer].m_characterID }, out var capture);
        if (ran && error == null) victim.Health -= loss;
        SimulationCombatPatches.Damage.Postfix(victim, capture, ran);
        SimulationCombatPatches.Damage.Finalizer(capture, error);
    }
    private static void LocalKillCredit(int peer, long message)
    {
        long uid = ZNet.instance.Peers[peer].m_uid;
        NativeSignalPatches.Local.Prefix(uid, ZDOID.None, "RPC_RegisterKill", out var root);
        var data = Data("RPC_RegisterKill", KillPayload(), message);
        data.m_senderPeerID = ZNet.GetUID(); data.m_targetPeerID = uid; data.m_targetZDO = ZDOID.None;
        NativeSignalPatches.Decoded.Prefix(data, out var capture); NativeSignalPatches.Decoded.Finalizer(capture, null);
        NativeSignalPatches.Local.Finalizer(root, null);
    }
    private static void SimulatedDeath(Character victim, int peers = 1, bool ran = true, Exception? error = null)
    {
        victim.Health = 0; victim.Dead = true;
        SimulationCombatPatches.Death.Prefix(victim, out var capture);
        for (int i = 0; i < peers; i++) LocalKillCredit(i, 500 + i);
        runtime!.Removed("50", victim.m_nview.GetZDO().m_uid.ToString());
        SimulationCombatPatches.Death.Postfix(capture, ran);
        SimulationCombatPatches.Death.Finalizer(capture, error);
    }
    private static ActivityRecord[] CreatureRecords(ActivityKind kind)
    { runtime!.FlushThrough(DateTime.UtcNow); return batches.SelectMany(b => b.Activities).Where(a => a.Kind == kind).ToArray(); }

    private static void SimulationCombatChecks()
    {
        Test("without simulation mod server-owned victims retain reported kill fallback and no damage capture", () =>
        {
            var victim = SimulatedVictim(mod: false); SimulatedHit(victim, 0, 100); SimulatedDeath(victim);
            Assert(CreatureRecords(ActivityKind.CreatureDamage).Length == 0 && CreatureRecords(ActivityKind.CreatureKill).Length == 0 && SignalCount == 1, "no-mod behavior changed");
        });
        Test("simulation presence alone cannot authorize client-owned victims", () =>
        {
            var victim = SimulatedVictim(owner: 10); SimulatedHit(victim, 0, 100); SimulatedDeath(victim);
            Assert(CreatureRecords(ActivityKind.CreatureDamage).Length == 0 && CreatureRecords(ActivityKind.CreatureKill).Length == 0 && SignalCount == 1, "client ownership bypassed real gate");
        });
        Test("server-owned death credits every damaging participant once without local RPC double count", () =>
        {
            Reset(2); var victim = SimulatedVictim();
            SimulatedHit(victim, 0, 30); SimulatedHit(victim, 0, 10); SimulatedHit(victim, 1, 200);
            Assert(CreatureRecords(ActivityKind.CreatureDamage).Sum(a => a.HpLoss) == 100, "net applied HP or overkill cap incorrect");
            SimulatedDeath(victim, 2); SimulatedDeath(victim, 2);
            Assert(CreatureRecords(ActivityKind.CreatureKill).Select(a => a.Actor.PlayerId).Order().SequenceEqual(new[] { "account0", "account1" }) && SignalCount == 0, "participant dedup, destruction or fallback suppression failed");
            LocalKillCredit(0, 900);
            Assert(SignalCount == 1 && !SimulationCombatPatches.SuppressReportedKill, "suppression leaked outside covered death");
        });
        Test("single participant receives confirmed credit even when final damage has no player", () =>
        {
            var victim = SimulatedVictim(); SimulatedHit(victim, 0, 25);
            victim.Health = 0; SimulatedDeath(victim);
            Assert(CreatureRecords(ActivityKind.CreatureKill).Single().Actor.PlayerId == "account0" && SignalCount == 0, "final-blow requirement incorrectly retained");
        });
        Test("expired participants are not revived by a recent attacker", () =>
        {
            Reset(2); var victim = SimulatedVictim(); SimulatedHit(victim, 0, 10);
            UnityEngine.Time.realtimeSinceStartupAsDouble = 221; SimulatedHit(victim, 1, 100); SimulatedDeath(victim, 2);
            Assert(CreatureRecords(ActivityKind.CreatureKill).Single().Actor.PlayerId == "account1" && SignalCount == 0, "expired contributor got confirmed or duplicate fallback credit");
        });
        Test("ownership loss drops participants and preserves uncovered death fallback", () =>
        {
            var victim = SimulatedVictim(); SimulatedHit(victim, 0, 10);
            runtime!.OwnershipChanged("50", victim.m_nview.GetZDO().m_uid.ToString()); SimulatedDeath(victim);
            Assert(CreatureRecords(ActivityKind.CreatureKill).Length == 0 && SignalCount == 1, "ownership invalidation did not restore fallback");
        });
        Test("skipped failed and ineligible damage never creates participants or HP", () =>
        {
            var victim = SimulatedVictim(); SimulatedHit(victim, 0, 10, ran: false); SimulatedHit(victim, 0, 10, error: new Exception());
            victim.Tamed = true; SimulatedHit(victim, 0, 10); victim.Tamed = false; victim.Player = true; SimulatedHit(victim, 0, 10);
            Assert(CreatureRecords(ActivityKind.CreatureDamage).Length == 0 && runtime!.Simulation.Participants(victim.m_nview.GetZDO().m_uid.ToString(), 100) == null, "unapplied or ineligible damage captured");
        });
        Test("failed death rejects confirmed credit and restores fallback scope", () =>
        {
            var victim = SimulatedVictim(); SimulatedHit(victim, 0, 100); SimulatedDeath(victim, error: new Exception());
            Assert(CreatureRecords(ActivityKind.CreatureKill).Length == 0 && !SimulationCombatPatches.SuppressReportedKill, "failed death escaped or leaked suppression");
        });
        Test("actual server damage is independent of the raw activity decoder and unrelated routed scopes", () =>
        {
            var victim = SimulatedVictim();
            Raw(Data("Damage"), _ => SimulatedHit(victim, 0, 25));
            Assert(CreatureRecords(ActivityKind.CreatureDamage).Single().HpLoss == 25, "unused raw decoder masked actual applied damage");
        });
    }
}
