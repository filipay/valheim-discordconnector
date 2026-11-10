using DiscordConnector.Patches;
using DiscordConnector.Records;

internal static partial class Program
{
    private static void PlayerTravelChecks()
    {
        DateTime start = new(2026, 11, 6, 0, 0, 0, DateTimeKind.Utc);
        ActivityRecord[] TravelRecords(ActivityKind kind)
        { runtime!.FlushThrough(start); return batches.SelectMany(b => b.Activities).Where(a => a.Kind == kind).ToArray(); }
        Test("foot-only player replication collects horizontal distance without minimap or participation", () =>
        {
            flags = new(onFoot: true); Minimap.instance = null;
            runtime!.Update(start, 100);
            ReceivePosition(0, 102, new()); runtime.Update(start.AddSeconds(2), 102);
            ReceivePosition(0, 104, new() { x = 12, y = 100 }); runtime.Update(start.AddSeconds(4), 104);
            Assert(TravelRecords(ActivityKind.OnFoot).Single().Meters == 12, "horizontal foot distance missing");
            runtime.Update(start.AddSeconds(6), 106);
            Assert(TravelRecords(ActivityKind.OnFoot).Length == 1, "stale position counted twice");
        });
        Test("native travel sampler excludes detected ship passengers and dead characters", () =>
        {
            flags = new(onFoot: true); runtime!.Update(start, 100);
            ReceivePosition(0, 102, new()); runtime.Update(start.AddSeconds(2), 102);
            var ship = new Ship(); ship.Passengers.Add(123); Ship.Instances.Add(ship);
            ReceivePosition(0, 104, new() { x = 12 }); runtime.Update(start.AddSeconds(4), 104);
            Ship.Instances.Clear();
            ReceivePosition(0, 106, new() { x = 24 }); runtime.Update(start.AddSeconds(6), 106);
            var zdo = ZDOMan.instance.GetZDO(ZNet.instance.Peers[0].m_characterID)!;
            zdo.Floats[ZDOVars.s_health] = 0;
            ReceivePosition(0, 108, new() { x = 36 }); runtime.Update(start.AddSeconds(8), 108);
            zdo.Floats[ZDOVars.s_health] = 100;
            ReceivePosition(0, 110, new() { x = 48 }); runtime.Update(start.AddSeconds(10), 110);
            Assert(TravelRecords(ActivityKind.OnFoot).Length == 0, "boat/death endpoints bridged");
        });
        Test("biome-time-only native sampler handles stationary players without Wanderer enabled", () =>
        {
            flags = new(biomeTime: true); runtime!.Update(start, 100);
            ReceivePosition(0, 102, new()); runtime.Update(start.AddSeconds(2), 102);
            ReceivePosition(0, 104, new()); runtime.Update(start.AddSeconds(4), 104);
            Assert(TravelRecords(ActivityKind.BiomeTime).Single().Seconds == 2 && !TravelRecords(ActivityKind.BiomeVisited).Any(), "independent biome duration missing");
            WorldGenerator.instance = null;
            ReceivePosition(0, 106, new()); runtime.Update(start.AddSeconds(6), 106);
            Assert(TravelRecords(ActivityKind.BiomeTime).Length == 1, "missing generator invented duration");
        });
        Test("portal-only native sampler deduplicates jumps and rejects stale replication", () =>
        {
            flags = new(portalTrips: true); runtime!.Update(start, 100);
            ReceivePosition(0, 102, new()); runtime.Update(start.AddSeconds(2), 102);
            ReceivePosition(0, 104, new() { x = 1000 }); runtime.Update(start.AddSeconds(4), 104);
            ReceivePosition(0, 106, new()); runtime.Update(start.AddSeconds(6), 106);
            runtime.Update(start.AddSeconds(8), 108);
            Assert(TravelRecords(ActivityKind.PortalTrip).Length == 1 && !TravelRecords(ActivityKind.OnFoot).Any(), "jump dedup/own flag failed");
        });
        Test("online-only native peer lifecycle includes idle connections and closes removal and shutdown", () =>
        {
            flags = new(onlineTime: true); runtime!.Update(start, 100);
            runtime.Update(start.AddSeconds(2), 102);
            ZNet.instance.Peers.Clear(); runtime.Update(start.AddSeconds(4), 104);
            Assert(TravelRecords(ActivityKind.OnlineTime).Single().Seconds == 4, "idle peer session did not close at removal");
            Reset(); flags = new(onlineTime: true); runtime!.Update(start, 100); runtime.Update(start.AddSeconds(2), 102);
            runtime.Dispose();
            Assert(batches.SelectMany(b => b.Activities).Single(a => a.Kind == ActivityKind.OnlineTime).Seconds == 2, "shutdown accrued beyond observed session");
        });
    }
}
