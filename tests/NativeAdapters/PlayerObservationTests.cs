using DiscordConnector.Patches;
using DiscordConnector.Records;

internal static partial class Program
{
    private static void ReceivePosition(int index, double time, UnityEngine.Vector3 position)
    {
        UnityEngine.Time.realtimeSinceStartupAsDouble = time;
        var peer = ZNet.instance.Peers[index]; var character = peer.m_characterID;
        if (!ZDOMan.instance.Zdos.TryGetValue(character, out var zdo))
        {
            zdo = new ZDO { m_uid = character, Owner = peer.m_uid };
            zdo.Longs[ZDOVars.s_playerID] = index + 123;
            zdo.Floats[ZDOVars.s_health] = 100;
            ZDOMan.instance.Zdos[character] = zdo;
        }
        using var bytes = new MemoryStream(); using var writer = new BinaryWriter(bytes);
        writer.Write(0); writer.Write(character.UserID); writer.Write(character.ID); writer.Write((ushort)0);
        writer.Write(zdo.DataRevision + 1); writer.Write(peer.m_uid);
        writer.Write(position.x); writer.Write(position.y); writer.Write(position.z); writer.Write(0);
        writer.Write(0L); writer.Write(0U); writer.Flush();
        ActivityPatches.Data.Prefix(peer.m_rpc, new ZPackage(bytes.ToArray()), out var receive);
        Assert(receive != null && !receive.Failed, "player-position-only receive did not authenticate");
        zdo.Position = position; zdo.DataRevision++;
        ActivityPatches.Deserialize.Prefix(out var decoded);
        ActivityPatches.Deserialize.Postfix(zdo, true, ref decoded);
        Assert(decoded?.TrackPosition == true, "biome/boss-only consumer did not admit replication freshness");
        ActivityPatches.Deserialize.Finalizer(decoded, null);
        ActivityPatches.Data.Postfix(receive, true); ActivityPatches.Data.Finalizer(receive, null);
    }

    private static void BossRpc(string key, long message = 1, long sender = 10, long? recipient = null, bool original = true,
        Exception? handlerError = null, Exception? receiveError = null, Action? beforeFinish = null)
    {
        var data = Data("SetGlobalKey", Payload(key), message);
        data.m_targetZDO = ZDOID.None; data.m_senderPeerID = sender; data.m_targetPeerID = recipient ?? ZNet.GetUID();
        ActivityPatches.ManualReceive.Prefix(ZNet.instance.Peers[0].m_rpc, out var receive);
        BossDefeatPatches.Decoded.Prefix(data, out var previous);
        try
        {
            BossDefeatPatches.GlobalKey.Prefix(sender, key, out var observation);
            if (original && handlerError == null) ZoneSystem.instance.RPC_SetGlobalKey(sender, key);
            if (handlerError == null) BossDefeatPatches.GlobalKey.Postfix(observation, original);
            BossDefeatPatches.GlobalKey.Finalizer(observation, handlerError);
            beforeFinish?.Invoke();
        }
        finally
        {
            BossDefeatPatches.Decoded.Finalizer(previous);
            ActivityPatches.ManualReceive.Finalizer(receive, receiveError ?? handlerError);
        }
    }
    private static int BossCredits()
    {
        runtime!.FlushThrough(DateTime.UtcNow);
        return batches.SelectMany(b => b.Activities).Count(a => a.Kind == ActivityKind.BossCredit);
    }
    private static void BossSetup()
    {
        Reset(3); flags = new(bossKills: true);
        runtime!.Update(DateTime.UtcNow, 100);
        ReceivePosition(0, 102, new() { x = 10, y = 10 });
        ReceivePosition(1, 102, new() { x = 50, y = 10 });
        ReceivePosition(2, 102, new() { x = 61, y = 10 });
        runtime.Update(DateTime.UtcNow, 102);
    }

    private static void PlayerObservationChecks()
    {
        Test("biome-only native sampler uses fresh player positions without minimap or creature ownership", () =>
        {
            flags = new(biomes: true); Minimap.instance = null;
            runtime!.Update(DateTime.UtcNow, 100);
            ReceivePosition(0, 102, new() { x = 123, z = 456 });
            runtime.Update(DateTime.UtcNow, 102); runtime.FlushThrough(DateTime.UtcNow);
            var visits = batches.SelectMany(b => b.Activities).Where(a => a.Kind == ActivityKind.BiomeVisited).ToArray();
            Assert(visits.Length == 1 && visits[0].Actor.PlayerId == "account0" && visits[0].Prefab == "Meadows", "resolved player biome missing");
            Assert(WorldGenerator.instance!.X == 123 && WorldGenerator.instance.Z == 456, "biome lookup did not use sampled x/z");
            WorldGenerator.instance.Biome = Heightmap.Biome.Swamp;
            runtime.Update(DateTime.UtcNow, 104); runtime.FlushThrough(DateTime.UtcNow);
            Assert(WorldGenerator.instance.Reads == 1, "stale replication was used for biome lookup");
            ReceivePosition(0, 106, new() { x = 321, z = 654 }); runtime.Update(DateTime.UtcNow, 106); runtime.FlushThrough(DateTime.UtcNow);
            Assert(batches.SelectMany(b => b.Activities).Count(a => a.Kind == ActivityKind.BiomeVisited) == 2, "fresh different biome did not count");
        });
        Test("biome sampler fails closed when generator is missing or biome is invalid", () =>
        {
            flags = new(biomes: true); runtime!.Update(DateTime.UtcNow, 100);
            WorldGenerator.instance = null; ReceivePosition(0, 102, new()); runtime.Update(DateTime.UtcNow, 102);
            WorldGenerator.instance = new() { Biome = Heightmap.Biome.None };
            ReceivePosition(0, 104, new()); runtime.Update(DateTime.UtcNow, 104); runtime.FlushThrough(DateTime.UtcNow);
            Assert(!batches.SelectMany(b => b.Activities).Any(a => a.Kind == ActivityKind.BiomeVisited), "missing/None biome fabricated a visit");
        });
        Test("native boss handler credits authenticated sender and nearby fresh peers including repeat existing keys", () =>
        {
            BossSetup(); ZoneSystem.instance.Keys.Add("defeated_eikthyr");
            BossRpc("defeated_eikthyr"); Assert(BossCredits() == 2, "existing global key suppressed handler observation or proximity attribution");
            Assert(batches.SelectMany(b => b.Activities).Where(a => a.Kind == ActivityKind.BossCredit).Select(a => a.Actor.PlayerId).Order().SequenceEqual(new[] { "account0", "account1" }), "credited wrong proximity cohort");
            BossRpc("defeated_eikthyr", 2); Assert(BossCredits() == 2, "repeated RPC bypassed key window");
            for (int i = 0; i < 3; i++) ReceivePosition(i, 162, new() { x = i * 40 });
            runtime!.Update(DateTime.UtcNow, 162);
            BossRpc("defeated_eikthyr", 3); Assert(BossCredits() == 4, "repeat kill at 60s must count even though key already exists");
        });
        Test("native boss capture rejects nonboss keys, spoofed transport, wrong recipient and skipped/failed handlers", () =>
        {
            BossSetup(); BossRpc("defeated_serpent"); BossRpc("defeated_eikthyr", 2, sender: 11);
            BossRpc("defeated_eikthyr", 3, recipient: 11); BossRpc("defeated_eikthyr", 4, original: false);
            BossRpc("defeated_eikthyr", 5, handlerError: new Exception("private"));
            BossRpc("defeated_eikthyr", 6, receiveError: new Exception("private"));
            BossRpc("defeated_gdking", 7, beforeFinish: () => flags = new());
            Assert(BossCredits() == 0, "invalid/native-failed boss observation escaped barriers");
            flags = new(bossKills: true);
            BossRpc("defeated_eikthyr", 6); Assert(BossCredits() == 2, "failed enclosing receive poisoned later valid event");
        });
        Test("native boss capture rejects stale positions and disabled collection; old victim-owned types are absent", () =>
        {
            BossSetup(); UnityEngine.Time.realtimeSinceStartupAsDouble = 105;
            BossRpc("defeated_eikthyr"); Assert(BossCredits() == 0, "stale sender position was reused");
            flags = new(); BossRpc("defeated_eikthyr", 2); Assert(BossCredits() == 0, "disabled key handler collected");
            foreach (string type in new[] { "Patches.CombatPatches", "Records.CombatObservationCoordinator", "Records.ParticipationLedger", "Records.DirectDamageRecord", "Records.ConfirmedCreatureDeath" })
                Assert(typeof(ActivityRuntime).Assembly.GetType("DiscordConnector." + type) == null, "retired type remains: " + type);
            Assert(!Enum.IsDefined(typeof(ReportMetric), 5), "direct damage metric remains");
        });
    }
}
