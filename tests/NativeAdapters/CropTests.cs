using DiscordConnector.Patches;
using DiscordConnector.Records;

internal static partial class Program
{
    private static DateTime CropUtc => new(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);
    private static ZDO CropSetup(bool tree = false)
    {
        ZNetScene.instance = new();
        var grown = new UnityEngine.GameObject();
        if (!tree) grown.Components[typeof(Pickable)] = new Pickable();
        var prefab = new UnityEngine.GameObject { name = "sapling_carrot" };
        prefab.Components[typeof(Plant)] = new Plant { m_grownPrefabs = new[] { grown } };
        prefab.Components[typeof(Piece)] = new Piece();
        ZNetScene.instance.Prefabs[42] = prefab;
        var identity = ZNet.instance.Players[0].m_userInfo.m_id;
        ZNet.World!.m_playerHistory.Add(new() { m_id = identity });
        var character = ZNet.instance.Peers[0].m_characterID;
        ZDOMan.instance.Zdos[character] = new() { m_uid = character };
        ZDOMan.instance.Zdos[character].Longs[ZDOVars.s_playerID] = 123;
        return new ZDO { m_uid = new(10, 2), Prefab = 42,
            Longs = { [ZDOVars.s_creator] = 123 }, Ints = { [ZDOVars.s_creatorIndex] = 0 } };
    }
    private static void CropStart()
    {
        flags = new(cropsPlanted: true);
        runtime!.Update(CropUtc, 100);
    }
    private static void CropCreated(ZDO zdo, Exception? error = null, bool ran = true)
    {
        ZDOMan.instance.Zdos[zdo.m_uid] = zdo;
        ActivityPatches.Created.Prefix(out var capture);
        ActivityPatches.Created.Postfix(zdo, ran, ref capture);
        ActivityPatches.Created.Finalizer(capture, error);
    }
    private static int CropCount()
    {
        runtime!.Update(CropUtc.AddSeconds(1), 101);
        runtime.FlushThrough(CropUtc.AddSeconds(1));
        return batches.Sum(batch => batch.Activities.Count(record => record.Kind == ActivityKind.CropPlanted));
    }
    private static void CropChecks()
    {
        Test("new crop lifecycle resolves its creator independently of construction/combat", () =>
        {
            var crop = CropSetup(); CropStart(); CropCreated(crop); CropCreated(crop);
            Assert(CropCount() == 1, "known crop creator not counted");
            var record = batches.SelectMany(b => b.Activities).Single();
            Assert(record.Actor.PlayerId == "account0" && record.SubjectId == "10:2" && record.Prefab == "sapling_carrot", "crop identity changed");
            Assert(CropCount() == 1, "candidate counted again while sampling");
        });
        Test("startup baseline and streamed existing crop state are never seeded as plantings", () =>
        {
            var crop = CropSetup(); CropCreated(crop); CropStart();
            Assert(CropCount() == 0, "baseline crop counted");
            crop.DataRevision++;
            Assert(CropCount() == 0, "existing ZDO replication/scene load counted");
        });
        Test("growth replacements and absent or unknown creators count zero", () =>
        {
            var crop = CropSetup(); CropStart();
            crop.Longs.Clear(); CropCreated(crop);
            Assert(CropCount() == 0, "creatorless growth replacement counted");
            crop.Longs[ZDOVars.s_creator] = 999;
            Assert(CropCount() == 0, "unknown creator counted");
        });
        Test("native Grow scope excludes even a replacement carrying a creator", () =>
        {
            var crop = CropSetup(); CropStart();
            ActivityPatches.Growth.Prefix(out var growth);
            CropCreated(crop);
            ActivityPatches.Growth.Postfix(growth, true);
            ActivityPatches.Growth.Finalizer(growth, null);
            Assert(CropCount() == 0, "growth lifecycle counted as planting");
        });
        foreach (bool fail in new[] { false, true })
            Test("new crop ZDOData lifecycle commits only after successful receive: failure=" + fail, () =>
            {
                var crop = CropSetup(); CropStart();
                ActivityPatches.Data.Prefix(ZNet.instance.Peers[0].m_rpc, new ZPackage(), out var receive);
                CropCreated(crop);
                Assert(!batches.SelectMany(batch => batch.Activities).Any(), "crop committed inside receive");
                ActivityPatches.Data.Postfix(receive, true);
                ActivityPatches.Data.Finalizer(receive, fail ? new Exception("receive failed") : null);
                Assert(CropCount() == (fail ? 0 : 1), "receive exception barrier not respected");
            });
        Test("tree saplings and failed or skipped creation hooks count zero", () =>
        {
            var crop = CropSetup(tree: true); CropStart(); CropCreated(crop);
            Assert(CropCount() == 0, "tree counted as a crop");
            runtime!.ResetSession("50");
            crop = CropSetup(); runtime.Update(CropUtc, 100);
            CropCreated(crop, new Exception("failed")); CropCreated(crop, ran: false);
            Assert(CropCount() == 0, "failed/skipped creation counted");
        });
        Test("crop disable/re-enable and world changes discard pending creator candidates", () =>
        {
            var crop = CropSetup(); CropStart(); crop.Longs.Clear(); CropCreated(crop);
            flags = new(); runtime!.Update(CropUtc, 100);
            crop.Longs[ZDOVars.s_creator] = 123; CropStart();
            Assert(CropCount() == 0, "old lifecycle backfilled on re-enable");
            CropCreated(crop); ZNet.instance.WorldUid = 51;
            Assert(CropCount() == 0, "old world candidate leaked");
        });
        Test("construction toggles do not discard pending crops or backfill old piece lifecycles", () =>
        {
            var crop = CropSetup(); CropStart(); crop.Longs.Clear(); CropCreated(crop);
            flags = new(cropsPlanted: true, construction: true); runtime!.Update(CropUtc, 100);
            flags = new(cropsPlanted: true); runtime.Update(CropUtc, 100);
            crop.Longs[ZDOVars.s_creator] = 123;
            Assert(CropCount() == 1 && batches.SelectMany(batch => batch.Activities).All(record => record.Kind == ActivityKind.CropPlanted),
                "unrelated flag lost crop provenance or captured an old construction lifecycle");
        });
    }
}
