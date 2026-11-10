using System.Collections;
using System.Reflection;
using DiscordConnector.Leaderboards;
using DiscordConnector.Patches;
using DiscordConnector.Records;
using HarmonyLib;

internal static partial class Program
{
    private static int passed, failed;
    private static LeaderboardsOptions flags = new();
    private static readonly List<ActivityBatch> batches = new();
    private static ActivityRuntime? runtime;
    private static ObservationLoss loss = new(_ => { });
    private static void Assert(bool condition, string reason)
    { if (!condition) throw new Exception(reason); }
    private static void Test(string name, Action check)
    {
        try { Reset(); check(); Console.WriteLine("PASS " + name); passed++; }
        catch (Exception error) { Console.WriteLine("FAIL " + name + ": " + error.Message); failed++; }
        finally { runtime?.Dispose(); runtime = null; NativeSignalPatches.Stop(); ActivityPatches.EmoteTriggers = null; ManualReportDiagnostics.Current = null; }
    }
    private static void Reset(int count = 1)
    {
        runtime?.Dispose(); NativeSignalPatches.Stop(); ActivityPatches.ClearIngress();
        ZNet.instance = new(); ZNet.World = new(); ZDOMan.instance = new();
        WorldGenerator.instance = new(); ZoneSystem.instance = new();
        Ship.Instances.Clear();
        BepInEx.Bootstrap.Chainloader.PluginInfos.Clear();
        for (int i = 0; i < count; i++)
        {
            long uid = i + 10;
            var peer = new ZNetPeer { m_uid = uid, m_characterID = new(uid, 1), m_socket = new() { Host = "account" + i } };
            peer.m_rpc = new(peer.m_socket); ZNet.instance.Peers.Add(peer);
            ZNet.instance.Players.Add(new() { m_characterID = peer.m_characterID });
            if (!ZDOID.m_userIDs.Contains(uid)) ZDOID.m_userIDs.Add(uid);
        }
        flags = new(); batches.Clear();
        loss = new(_ => { });
        UnityEngine.Time.realtimeSinceStartupAsDouble = 100;
        runtime = new(new ActivityCollector(batch => { batches.Add(batch); return true; }, _ => { }, loss), () => flags, _ => { }, 1);
        NativeSignalPatches.Configure(new(batch => { batches.Add(batch); return true; }, _ => { }, loss), () => flags);
        typeof(ActivityPatches).GetField("_manualRouting", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, new ManualCommandRouting(() => ActivityRuntime.Now, loss));
        InstallHooks(); NativeSignalPatches.SetSession("50", "fixture-session", "test");
        ActivityPatches.ManualCommand = null;
        Counters();
    }
    private static void Counters()
    {
        ZNet.instance.PeerReads = ZNet.instance.PlayerReads = 0;
        Socket.HostReads = Socket.ConnectedReads = ZNetPeer.ReadyReads = 0;
        ZPackage.ParameterCopies = ZPackage.ArrayCopies = 0;
    }
    private static int RawReplay => ((IDictionary)typeof(ActivityPatches).GetField("Messages", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!).Count;
    private static ZPackage Payload(string text) { var p = new ZPackage(); p.Write(text); p.SetPos(0); return p; }
    private static ZRoutedRpc.RoutedRPCData Data(string method, ZPackage? payload = null, long message = 1) => new()
    {
        m_msgID = message, m_senderPeerID = 10, m_targetPeerID = 0,
        m_targetZDO = ZNet.instance.Peers[0].m_characterID,
        m_methodHash = method.GetStableHashCode(), m_parameters = payload ?? new()
    };
    private static ZPackage Envelope(ZRoutedRpc.RoutedRPCData d)
    {
        var p = new ZPackage(); p.Write(123); // Exercise a nonzero borrowed cursor.
        p.Write(d.m_msgID); p.Write(d.m_senderPeerID); p.Write(d.m_targetPeerID);
        p.Write(d.m_targetZDO.UserID); p.Write(d.m_targetZDO.ID); p.Write(d.m_methodHash); p.Write(d.m_parameters);
        p.SetPos(4); return p;
    }
    private static void Raw(ZRoutedRpc.RoutedRPCData d, Action<ActivityPatches.Ingress?>? inside = null, Exception? error = null, int? envelopeCopies = null, int peer = 0)
    {
        bool peek = runtime != null && ZNet.instance.WorldUid != 0 && (flags.DodgeRolls || flags.FloorPickups || flags.CookingCompletions);
        RawPackage(Envelope(d), inside, error, envelopeCopies, peer, peek ? 6 : 0);
    }
    private static void RawPackage(ZPackage p, Action<ActivityPatches.Ingress?>? inside = null, Exception? error = null,
        int? envelopeCopies = null, int peer = 0, int headerReads = 6)
    {
        int position = p.GetPos(), reads = p.Reads, sets = p.PositionsSet, fixedReads = p.FixedReads.Count;
        int restoredSets = sets + (headerReads == 0 ? 0 : 1);
        void CheckCursor()
        {
            Assert(p.GetPos() == position && p.Reads == reads + headerReads && p.PositionsSet == restoredSets,
                "borrowed envelope exceeded bounded reads/restore or cursor was not restored before original/nested work");
            Assert(p.FixedReads.Skip(fixedReads).SequenceEqual(new[] { "long", "long", "long", "long", "uint", "int" }.Take(headerReads)),
                "borrowed header read order/width changed");
        }
        ActivityPatches.Routed.Prefix(ZNet.instance.Peers[peer].m_rpc, p, out var frame);
        try { CheckCursor(); inside?.Invoke(frame); }
        finally { ActivityPatches.Routed.Postfix(frame, true); ActivityPatches.Routed.Finalizer(frame, error); }
        CheckCursor();
        Assert(p.Copies <= 1, "more than one envelope copy");
        Assert(p.Copies == 0 || p.CopyPosition == position && p.CopyReads == reads + headerReads && p.CopyPositionsSet == restoredSets,
            "relevant envelope copied before borrowed cursor restoration");
        if (envelopeCopies.HasValue) Assert(p.Copies == envelopeCopies, "unexpected envelope copy count");
    }
    private static void Decoded(ZRoutedRpc.RoutedRPCData d, Action? beforeFinish = null, Exception? error = null, int peer = 0)
    {
        ActivityPatches.ManualReceive.Prefix(ZNet.instance.Peers[peer].m_rpc, out var frame);
        NativeSignalPatches.Decoded.Prefix(d, out var capture);
        NativeSignalPatches.Decoded.Finalizer(capture, null);
        beforeFinish?.Invoke();
        ActivityPatches.ManualReceive.Finalizer(frame, error);
    }
    internal static void InstallHooks(string owner = "test", Assembly? assembly = null)
    {
        Harmony.Inventory.Clear();
        foreach (Type hook in (assembly ?? typeof(ActivityPatches).Assembly).GetTypes())
        {
            var declarations = hook.GetCustomAttributes<HarmonyPatch>().ToArray();
            if (declarations.Length == 0) continue;
            var methods = hook.GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
            var targets = new List<MethodBase>();
            foreach (var declaration in declarations.Where(d => d.TargetType != null))
            {
                var target = declaration.Arguments.Length == 0 ? declaration.TargetType!.GetMethod(declaration.MethodName!) :
                    declaration.TargetType!.GetMethod(declaration.MethodName!, declaration.Arguments);
                if (target != null) targets.Add(target);
            }
            var factory = methods.SingleOrDefault(m => m.GetCustomAttribute<HarmonyTargetMethods>() != null);
            if (factory != null) targets.AddRange((IEnumerable<MethodBase>)factory.Invoke(null, null)!);
            foreach (var target in targets.Where(t => t?.DeclaringType == typeof(ZRoutedRpc)))
            {
                if (!Harmony.Inventory.TryGetValue(target, out var patches)) Harmony.Inventory.Add(target, patches = new());
                foreach (var method in methods)
                {
                    var list = method.GetCustomAttribute<HarmonyPrefix>() != null ? patches.Prefixes :
                        method.GetCustomAttribute<HarmonyPostfix>() != null ? patches.Postfixes :
                        method.GetCustomAttribute<HarmonyFinalizer>() != null ? patches.Finalizers : null;
                    if (list == null) continue;
                    list.Add(new Patch { owner = owner, PatchMethod = method, priority = method.GetCustomAttribute<HarmonyPriority>()?.Value ?? 400,
                        before = method.GetCustomAttribute<HarmonyBefore>()?.Owners ?? Array.Empty<string>(),
                        after = method.GetCustomAttribute<HarmonyAfter>()?.Owners ?? Array.Empty<string>() });
                }
            }
        }
    }
    private static int SignalCount => batches.Sum(b => b.Signals.Count);
    private static void SnapshotUnavailable()
    {
        Assert(!StablePlayerResolver.NativeSignalSnapshot(DateTime.UtcNow, out var peers, out var characters), "ambiguous/unbounded snapshot succeeded");
        Assert(peers.Length == 0 && characters.Length == 0, "failed snapshot leaked partial evidence");
    }
    private static ZPackage KillPayload()
    {
        var p = Payload("Troll"); p.SetPos(p.Size());
        p.Write(1); p.Write(0); p.Write(1); p.Write(false); p.SetPos(0); return p;
    }
    private static void Manual(long message, long sender = 10, Exception? error = null)
    {
        var payload = new ZPackage(); payload.Write(new UnityEngine.Vector3()); payload.Write((int)Talker.Type.Shout); payload.Write("!dc daily");
        payload.SetPos(3); int sets = payload.PositionsSet;
        var d = Data("ChatMessage", payload, message); d.m_senderPeerID = sender; d.m_targetZDO = ZDOID.None;
        ActivityPatches.ManualReceive.Prefix(ZNet.instance.Peers[0].m_rpc, out var frame);
        ActivityPatches.ManualDecodedChat.Prefix(d, out var capture);
        ActivityPatches.ManualDecodedChat.Finalizer(capture, null);
        ActivityPatches.ManualReceive.Finalizer(frame, error);
        Assert(payload.GetPos() == 3 && payload.PositionsSet == sets && payload.Reads == 0, "manual observer touched borrowed payload cursor");
    }
    private static void Cook(CookingStation station, Action? afterPrefix = null)
    {
        CookingPatches.Prefix(station, typeof(CookingStation).GetMethod(nameof(CookingStation.RPC_AddItem))!, new object[] { 10L, "raw", false }, out var capture);
        afterPrefix?.Invoke(); station.ItemName = "raw";
        CookingPatches.Postfix(station, capture, true); CookingPatches.Finalizer(capture, null);
    }
    private static void HeaderDiagnostics(Action<string> log, Func<double>? seconds = null)
    {
        // Isolate process-wide cadence between fixtures, not between instances within a fixture.
        foreach (string field in new[] { "lastShortHeader", "lastHeaderReadFailure" })
            typeof(ManualReportDiagnostics).GetField(field, BindingFlags.Static | BindingFlags.NonPublic)?.SetValue(null, null);
        ManualReportDiagnostics.Current = new(log, seconds ?? (() => 100));
    }
    public static int Main()
    {
        InventoryChecks();
        LifecycleChecks();
        DodgeChecks();
        CropChecks();
        PlayerObservationChecks();
        PlayerTravelChecks();
        SimulationCombatChecks();
        LongestBowChecks();
        OracleChecks();
        Test("health sampler uses fresh player ZDOs independently of exploration and honors its own flag", () =>
        {
            flags = new(healthLost: true);
            var character = ZNet.instance.Peers[0].m_characterID;
            var zdo = new ZDO { m_uid = character, Owner = 10 };
            zdo.Longs[ZDOVars.s_playerID] = 123;
            ZDOMan.instance.Zdos[character] = zdo;
            DateTime start = new(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);
            runtime!.Update(start, 100);
            void Sample(double time, float health, bool fresh = true)
            {
                UnityEngine.Time.realtimeSinceStartupAsDouble = time;
                if (fresh)
                {
                    using var bytes = new MemoryStream();
                    using var writer = new BinaryWriter(bytes);
                    writer.Write(0); // No invalid sectors; one incoming ZDO and the None terminator.
                    writer.Write(character.UserID); writer.Write(character.ID); writer.Write((ushort)0);
                    writer.Write(zdo.DataRevision + 1); writer.Write(10L);
                    writer.Write(0f); writer.Write(0f); writer.Write(0f); writer.Write(0);
                    writer.Write(0L); writer.Write(0U); writer.Flush();
                    ActivityPatches.Data.Prefix(ZNet.instance.Peers[0].m_rpc, new ZPackage(bytes.ToArray()), out var receive);
                    Assert(receive != null && !receive.Failed, "health-only replication did not admit the receive");
                    zdo.Floats[ZDOVars.s_health] = health; zdo.DataRevision++;
                    ActivityPatches.Deserialize.Prefix(out var decoded);
                    ActivityPatches.Deserialize.Postfix(zdo, true, ref decoded);
                    Assert(flags.HealthLost ? decoded?.TrackPosition == true : decoded == null, "health-only flag did not control replication freshness");
                    ActivityPatches.Deserialize.Finalizer(decoded, null);
                    ActivityPatches.Data.Postfix(receive, true);
                    ActivityPatches.Data.Finalizer(receive, null);
                }
                else { zdo.Floats[ZDOVars.s_health] = health; zdo.DataRevision++; }
                runtime.Update(start.AddSeconds(time - 100), time);
            }
            Sample(102, 100); Sample(104, 80); Sample(106, 70, fresh: false);
            Sample(108, 60); Sample(110, 50);
            runtime.FlushThrough(start.AddSeconds(10));
            var records = batches.SelectMany(batch => batch.Activities).Where(record => record.Kind == ActivityKind.HealthLost).ToArray();
            Assert(records.Sum(record => record.HpLoss) == 30 && records.All(record => record.Actor.PlayerId == "account0"), "fresh resolved player health was not attributed");
            int reads = zdo.FloatReads;
            flags = new(); Sample(112, 20);
            Assert(zdo.FloatReads == reads, "disabled health sampled a player ZDO");
            flags = new(healthLost: true); Sample(114, 10);
            runtime.FlushThrough(start.AddSeconds(14));
            Assert(batches.SelectMany(batch => batch.Activities).Where(record => record.Kind == ActivityKind.HealthLost).Sum(record => record.HpLoss) == 30,
                "re-enabled health bridged old samples");
        });
        Test("signal admission charges authenticated reporter, not kill beneficiary; two honest peers survive flood", () =>
        {
            Reset(3); flags = new(reportedKillCredits: true);
            for (int i = 0; i < 129; i++)
            {
                var d = Data("RPC_RegisterKill", KillPayload(), i); d.m_targetZDO = ZDOID.None; d.m_targetPeerID = 11;
                Decoded(d);
            }
            Assert(SignalCount == 128 && ActivityPatches.ManualRoutes.LastRejection == ObservationRejection.PrincipalQuota, "reporter exceeded 128/129");
            Assert(batches.All(b => b.SignalPrincipal?.AccountId == "account0" && !b.SignalPrincipal.ServerLocal && b.Signals.Single().Actor.PlayerId == "account1"), "beneficiary was used as origin");
            for (int peer = 1; peer <= 2; peer++)
            {
                var d = Data("RPC_RegisterKill", KillPayload(), 900); d.m_senderPeerID = 10 + peer;
                d.m_targetZDO = ZDOID.None; d.m_targetPeerID = 10;
                Decoded(d, peer: peer);
            }
            Assert(SignalCount == 130, "honest reporters starved by one account");
            ZNet.instance.Peers[0].m_socket = new() { Host = "account0" };
            ZNet.instance.Peers[0].m_rpc = new(ZNet.instance.Peers[0].m_socket);
            var reconnect = Data("RPC_RegisterKill", KillPayload(), 1000); reconnect.m_targetZDO = ZDOID.None; reconnect.m_targetPeerID = 12;
            Decoded(reconnect);
            Assert(SignalCount == 130, "new socket minted another stable-account quota");
            int manuals = 0; ActivityPatches.ManualCommand = (_, _, _, _) => manuals++;
            Manual(1001); Assert(manuals == 1, "signal flood consumed manual replay/authorization route");
            ZNet.instance.WorldUid = 51; NativeSignalPatches.SetSession("51", "next-session", "test");
            Decoded(reconnect); Assert(SignalCount == 131 && batches.Last().Signals.Single().WorldId == "51", "world replacement failed to reset principal policy");
        });
        Test("local signal origin has its own fixed allowance, never beneficiary or peer identity", () =>
        {
            flags = new(reportedKillCredits: true, consumableUses: true);
            for (int i = 0; i < 129; i++)
            {
                NativeSignalPatches.Local.Prefix(10, ZDOID.None, "RPC_RegisterKill", out var root);
                var d = Data("RPC_RegisterKill", KillPayload(), i); d.m_senderPeerID = ZNet.GetUID(); d.m_targetPeerID = 10; d.m_targetZDO = ZDOID.None;
                NativeSignalPatches.Decoded.Prefix(d, out var capture); NativeSignalPatches.Decoded.Finalizer(capture, null);
                NativeSignalPatches.Local.Finalizer(root, null);
            }
            Assert(SignalCount == 128 && batches.All(b => b.SignalPrincipal?.ServerLocal == true), "local allowance not fixed/separate");
            Decoded(Data("SetTrigger", Payload("eat"), 900)); Assert(SignalCount == 129 && !batches.Last().SignalPrincipal!.ServerLocal, "local pressure charged a peer");
        });
        Test("raw activity 128/129 allowance, failed root does not spend replay or quota", () =>
        {
            flags = new(dodgeRolls: true);
            for (int i = 0; i < 128; i++) Raw(Data("SetTrigger", Payload("dodge"), i), error: new Exception());
            Assert(RawReplay == 0, "failed gameplay scope consumed raw replay");
            for (int i = 0; i < 129; i++) Raw(Data("SetTrigger", Payload("dodge"), i));
            Assert(RawReplay == 128 && ActivityPatches.RawRejection == ObservationRejection.PrincipalQuota, "raw 128/129 bound missing");
            Raw(Data("SetTrigger", Payload("dodge"), 1));
            Assert(ActivityPatches.RawRejection == ObservationRejection.Duplicate, "raw duplicate confused with quota");
            ZNet.instance.Peers[0].m_socket = new() { Host = "account0" }; ZNet.instance.Peers[0].m_rpc = new(ZNet.instance.Peers[0].m_socket);
            Raw(Data("SetTrigger", Payload("dodge"), 900)); Assert(RawReplay == 128, "raw reconnect reset stable-account charges");
            UnityEngine.Time.realtimeSinceStartupAsDouble = 107;
            Raw(Data("SetTrigger", Payload("dodge"), 901)); Assert(RawReplay == 128, "raw seven-second inclusive expiry changed");
            UnityEngine.Time.realtimeSinceStartupAsDouble = 107.001;
            Raw(Data("SetTrigger", Payload("dodge"), 902)); Assert(RawReplay == 97, "raw cleanup must remove at most 32, then add one");
            UnityEngine.Time.realtimeSinceStartupAsDouble = 100;
            Raw(Data("SetTrigger", Payload("dodge"), 903)); Assert(RawReplay == 97 && ActivityPatches.RawRejection == ObservationRejection.Stale, "backward raw clock cleared live charges");
            foreach (double invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -1 })
            {
                UnityEngine.Time.realtimeSinceStartupAsDouble = invalid;
                Raw(Data("SetTrigger", Payload("dodge"), 904)); Assert(RawReplay == 97 && ActivityPatches.RawRejection == ObservationRejection.Stale, "invalid raw clock pruned/charged state");
            }
            UnityEngine.Time.realtimeSinceStartupAsDouble = 100;
            ActivityPatches.ClearIngress();
            Raw(Data("SetTrigger", Payload("dodge"), 1)); Assert(RawReplay == 1, "world replacement did not clear raw policy");
        });
        Test("ineligible raw schema consumes no replay quota; staged raw expiry rejects without charge", () =>
        {
            flags = new(dodgeRolls: true);
            for (int i = 0; i < 129; i++) Raw(Data("SetTrigger", Payload("wave"), i));
            Assert(RawReplay == 0, "ineligible raw trigger consumed quota/replay");
            Assert(loss.Retained == 0, "unknown/ineligible raw traffic was labelled as lost gameplay");
            Raw(Data("SetTrigger", Payload("dodge"), 200), _ => UnityEngine.Time.realtimeSinceStartupAsDouble += 6);
            Assert(RawReplay == 0 && ActivityPatches.RawRejection == ObservationRejection.Stale, "staged raw expiry spent replay");
            Raw(Data("SetTrigger", Payload("dodge"), 200)); Assert(RawReplay == 1, "stale raw scope poisoned replay identity");
            Assert(loss.Snapshot("50", ReportMetric.ObservedDodgeRolls)!.Rejections[ObservationRejection.Stale] == 1, "stale valid raw observation was not counted exactly once");
        });
        Test("raw valid quota losses retain exact numerical reasons, while duplicates and malformed schemas add none", () =>
        {
            flags = new(dodgeRolls: true);
            for (int i = 0; i < 131; i++) Raw(Data("SetTrigger", Payload("dodge"), i));
            Raw(Data("SetTrigger", Payload("dodge"), 1));
            Raw(Data("SetTrigger", Payload("wave"), 132));
            var snapshot = loss.Snapshot("50", ReportMetric.ObservedDodgeRolls)!;
            Assert(snapshot.Rejections[ObservationRejection.PrincipalQuota] == 3 && snapshot.Rejections.Values.Sum() == 3,
                "three refused valid messages coalesced, or duplicate/malformed traffic became loss");
        });
        Test("world replacement seals held raw scopes before clearing their policy", () =>
        {
            flags = new(dodgeRolls: true);
            Raw(Data("SetTrigger", Payload("dodge")), _ => ActivityPatches.ClearIngress());
            Assert(RawReplay == 0, "held pre-reset raw scope charged replacement world");
            Raw(Data("SetTrigger", Payload("dodge"))); Assert(RawReplay == 1, "fresh world scope did not recover");
        });
        Test("unrelated raw traffic skips envelope/parameter copies, peers and replay", () =>
        {
            flags = new(dodgeRolls: true, cookingCompletions: true, floorPickups: true);
            Raw(Data("Unrelated", Payload("anything")), envelopeCopies: 0);
            Assert(ZPackage.ParameterCopies == 0, "unrelated RPC materialized nested parameters");
            Assert(ZNet.instance.PeerReads == 0, "unrelated RPC expanded peers");
            Assert(RawReplay == 0, "unrelated RPC consumed raw replay");
        });
        Test("disabled raw traffic skips parameter copies, peers and replay", () =>
        {
            Raw(Data("SetTrigger", Payload("dodge")), envelopeCopies: 0);
            Assert(ZPackage.ParameterCopies == 0 && ZNet.instance.PeerReads == 0 && RawReplay == 0, "disabled RPC did observation work");
        });
        foreach (bool relevant in new[] { false, true })
            foreach (bool nonExposable in new[] { false, true })
                foreach (int position in new[] { 0, 4, 17 })
                    Test($"raw fixed header restores cursor {position}; relevant={relevant}, nonexposable={nonExposable}", () =>
                    {
                        flags = new(dodgeRolls: true);
                        byte[] bytes = Enumerable.Repeat((byte)0xa5, position).Concat(Envelope(Data(relevant ? "SetTrigger" : "Unrelated", Payload("dodge"))).GetArray().Skip(4)).ToArray();
                        var p = new ZPackage(bytes, nonExposable); p.SetPos(position); Counters();
                        RawPackage(p, frame => Assert(relevant ? frame?.Principal != null : frame?.Failed == true, "raw classification/outcome changed"), envelopeCopies: relevant ? 1 : 0);
                        Assert(ZPackage.ParameterCopies == (relevant ? 1 : 0) && ZPackage.ArrayCopies == (relevant ? 1 : 0) && RawReplay == (relevant ? 1 : 0), "raw copy/replay outcome changed");
                        if (!relevant) Assert(ZNet.instance.PeerReads == 0, "irrelevant header expanded peers");
                    });
        foreach (int length in Enumerable.Range(0, 40))
            Test($"truncated raw fixed header {length}/40 skips copies and preserves nonzero cursor", () =>
            {
                flags = new(dodgeRolls: true);
                var p = new ZPackage(Envelope(Data("SetTrigger", Payload("dodge"))).GetArray().Take(4 + length).ToArray());
                p.SetPos(4); Counters();
                var logs = new List<string>(); HeaderDiagnostics(logs.Add);
                RawPackage(p, frame => Assert(frame?.Failed == true, "truncated header established ingress"), envelopeCopies: 0, headerReads: 0);
                Assert(ZPackage.ArrayCopies == 0 && ZPackage.ParameterCopies == 0 && ZNet.instance.PeerReads == 0 && RawReplay == 0, "short header did observation work");
                Assert(logs.SequenceEqual(new[] { $"ActivityIngress reason=short-header bytes={4 + length}" }), "short-header probe missing or exposed more than fixed reason/size");
            });
        foreach (int pastEnd in new[] { 0, 1, 100 })
            Test($"raw cursor at/past end +{pastEnd} skips reads and copies", () =>
            {
                flags = new(dodgeRolls: true);
                var p = Envelope(Data("SetTrigger", Payload("dodge"))); p.SetPos(p.Size() + pastEnd); Counters();
                RawPackage(p, envelopeCopies: 0, headerReads: 0);
                Assert(ZPackage.ArrayCopies == 0 && ZPackage.ParameterCopies == 0 && ZNet.instance.PeerReads == 0 && RawReplay == 0, "end cursor did observation work");
            });
        Test("oversized raw envelope skips even header reads", () =>
        {
            flags = new(dodgeRolls: true);
            var logs = new List<string>(); HeaderDiagnostics(logs.Add);
            var p = new ZPackage(new byte[1024 * 1024 + 1]); p.SetPos(4);
            RawPackage(p, envelopeCopies: 0, headerReads: 0);
            Assert(logs.Count == 0 && ZPackage.ParameterCopies == 0 && ZNet.instance.PeerReads == 0 && RawReplay == 0, "oversized raw ingress probed or did observation work");
        });
        Test("large irrelevant payload is not copied or decoded", () =>
        {
            flags = new(dodgeRolls: true);
            var logs = new List<string>(); HeaderDiagnostics(logs.Add);
            var p = Envelope(Data("Unrelated", Payload(new string('x', 900 * 1024)))); Counters();
            RawPackage(p, envelopeCopies: 0);
            Assert(logs.Count == 0 && ZPackage.ArrayCopies == 0 && ZPackage.ParameterCopies == 0 && ZNet.instance.PeerReads == 0 && RawReplay == 0, "large irrelevant payload was probed/materialized");
        });
        foreach (int read in Enumerable.Range(1, 6))
            Test($"partial raw fixed read failure {read}/6 restores cursor in finally", () =>
            {
                flags = new(dodgeRolls: true);
                var p = Envelope(Data("SetTrigger", Payload("dodge"))); p.ThrowOnRead = read; Counters();
                var logs = new List<string>(); HeaderDiagnostics(message => {
                    Assert(p.GetPos() == 4, "read-failure logger ran before cursor restoration"); logs.Add(message);
                });
                RawPackage(p, frame => Assert(frame?.Failed == true && frame.Actor == null, "read failure established ingress"), envelopeCopies: 0, headerReads: read);
                Assert(ZPackage.ArrayCopies == 0 && ZPackage.ParameterCopies == 0 && ZNet.instance.PeerReads == 0 && RawReplay == 0, "read failure copied/authenticated/charged");
                Assert(logs.SequenceEqual(new[] { $"ActivityIngress reason=header-read-failed bytes={p.Size()}" }), "read-failure probe missing or exposed exception/payload/identity");
            });
        Test("header probes throttle each reason across instances without backward/invalid-clock resets", () =>
        {
            flags = new(dodgeRolls: true);
            double seconds = 100; var logs = new List<string>(); HeaderDiagnostics(logs.Add, () => seconds);
            void Burst()
            {
                for (int i = 0; i < 20; i++)
                {
                    RawPackage(new(new byte[3]), envelopeCopies: 0, headerReads: 0);
                    var p = Envelope(Data("Unrelated", Payload("private-payload"))); p.ThrowOnRead = 6;
                    RawPackage(p, envelopeCopies: 0, headerReads: 6);
                }
            }
            Burst(); Assert(logs.Count == 2, "initial burst did not emit exactly once per fixed reason");
            ManualReportDiagnostics.Current = new(logs.Add, () => seconds);
            ActivityPatches.ClearIngress(); Burst(); Assert(logs.Count == 2, "instance/ingress reset bypassed process throttle");
            seconds = 159.999; Burst(); Assert(logs.Count == 2, "burst before 60 seconds emitted");
            foreach (double invalid in new[] { 99, -1, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            { seconds = invalid; Burst(); Assert(logs.Count == 2, "backward/invalid diagnostic clock reset cadence"); }
            seconds = 160; Burst(); Assert(logs.Count == 4, "60-second boundary did not admit one warning per reason");
            seconds = 219.999; Burst(); Assert(logs.Count == 4, "second window emitted early");
            seconds = 220; Burst(); Assert(logs.Count == 6, "later burst did not recover");
            Assert(logs.All(message => message == "ActivityIngress reason=short-header bytes=3" ||
                message == "ActivityIngress reason=header-read-failed bytes=64"), "probe leaked non-allowlisted content");
            Assert(ZPackage.ParameterCopies == 0 && RawReplay == 0 && loss.Retained == 0, "diagnostic burst copied/charged/labeled gameplay");
        });
        Test("disabled raw fast path emits no malformed-header diagnostic", () =>
        {
            int clockReads = 0; var logs = new List<string>(); HeaderDiagnostics(logs.Add, () => { clockReads++; return 100; });
            clockReads = 0;
            RawPackage(new(new byte[3]), envelopeCopies: 0, headerReads: 0);
            var p = Envelope(Data("SetTrigger", Payload("private-payload"))); p.ThrowOnRead = 1; Counters();
            RawPackage(p, envelopeCopies: 0, headerReads: 0);
            Assert(logs.Count == 0 && clockReads == 0 && p.Reads == 0 && ZPackage.ArrayCopies == 0 &&
                ZPackage.ParameterCopies == 0 && ZNet.instance.PeerReads == 0 && RawReplay == 0, "disabled ingress probed/copied/authenticated");
        });
        Test("header diagnostic byte count clamps both fixed reasons to the ingress ceiling", () =>
        {
            double seconds = 100; var logs = new List<string>(); HeaderDiagnostics(logs.Add, () => seconds);
            foreach (int size in new[] { int.MinValue, -1, 0, 1024 * 1024, int.MaxValue })
            {
                ManualReportDiagnostics.HeaderProbe(false, size); ManualReportDiagnostics.HeaderProbe(true, size);
                int bounded = Math.Max(0, Math.Min(size, 1024 * 1024));
                Assert(logs.TakeLast(2).SequenceEqual(new[] { $"ActivityIngress reason=short-header bytes={bounded}",
                    $"ActivityIngress reason=header-read-failed bytes={bounded}" }), "diagnostic byte count escaped bounds");
                seconds += 60;
            }
            Assert(logs.Count == 10, "bounded probes bypassed per-reason cadence");
        });
        foreach (bool readFailure in new[] { false, true })
            Test($"throwing header logger preserves nested masking and later cooking/manual; readFailure={readFailure}", () =>
            {
                flags = new(cookingCompletions: true);
                runtime!.Collector.Tick(DateTime.UtcNow, 100, "50", flags);
                var input = Payload("raw"); input.SetPos(input.Size()); input.Write(false);
                var station = new CookingStation(); station.m_nview.Zdo.m_uid = new(1, 99);
                var d = Data("RPC_AddItem", input); d.m_targetZDO = station.m_nview.Zdo.m_uid; d.m_targetPeerID = 1;
                int calls = 0;
                HeaderDiagnostics(_ => { calls++; throw new IOException("private-payload account0 fixture-session"); });
                Raw(d, parent => {
                    Assert(parent?.Actor != null && !parent.Failed, "missing authenticated parent");
                    ZPackage p = readFailure ? Envelope(Data("Unrelated")) : new(new byte[3]);
                    if (readFailure) p.ThrowOnRead = 3;
                    bool captured = false;
                    RawPackage(p, child => {
                        Assert(child?.Failed == true && child.Actor == null && ReferenceEquals(ActivityPatches.Sender, child), "malformed child borrowed parent origin");
                        Cook(station); ActivityPatches.Stage(() => captured = true);
                    }, envelopeCopies: 0, headerReads: readFailure ? 3 : 0);
                    Assert(!captured && ReferenceEquals(ActivityPatches.Sender, parent), "throwing logger leaked child capture/scope");
                });
                Assert(calls == 1 && ActivityPatches.Sender == null && RawReplay == 0 && runtime.Cooking.PendingCount == 0,
                    "logger failure affected masking/unwind or admitted invalid cooking");
                ZPackage repeat = readFailure ? Envelope(Data("Unrelated")) : new(new byte[3]);
                if (readFailure) repeat.ThrowOnRead = 3;
                RawPackage(repeat, envelopeCopies: 0, headerReads: readFailure ? 3 : 0);
                Assert(calls == 1, "throwing logger did not consume throttle reservation");
                station.ItemName = "";
                Raw(d, _ => Cook(station));
                Assert(runtime.Cooking.PendingCount == 1 && RawReplay == 1, "later valid cooking did not recover");
                runtime.Collector.FlushThrough(DateTime.UtcNow.AddSeconds(1));
                Assert(batches.SelectMany(b => b.Cooking).Any(c => c.Kind == CookingChangeKind.AcceptedInput && c.Actor?.PlayerId == "account0"), "later cooking lost authenticated inserter");
                int commands = 0; ActivityPatches.ManualCommand = (_, _, _, _) => commands++;
                Manual(20); Manual(20); Assert(commands == 1, "later manual routing/replay did not recover");
                Assert(loss.Retained == 0, "malformed diagnostic or logger failure became gameplay loss");
            });
        foreach (bool relevant in new[] { false, true })
            Test($"unknown raw target user does not mutate registry; relevant={relevant}", () =>
            {
                flags = new(dodgeRolls: true);
                long[] users = ZDOID.m_userIDs.ToArray();
                var d = Data(relevant ? "SetTrigger" : "Unrelated", Payload("dodge")); d.m_targetZDO = new(long.MaxValue, 1);
                Raw(d, frame => Assert(frame?.Failed == true && frame.Actor == null, "unknown target established ingress"), envelopeCopies: relevant ? 1 : 0);
                Assert(ZDOID.m_userIDs.SequenceEqual(users) && ZPackage.ParameterCopies == 0 && ZNet.instance.PeerReads == 0 && RawReplay == 0, "unknown user mutated registry or did observation work");
            });
        foreach (string invalid in new[] { "header only", "negative length", "truncated parameters", "envelope trailing", "malformed parameters", "parameter trailing" })
            Test("relevant raw full decoder still rejects " + invalid, () =>
            {
                flags = new(dodgeRolls: true);
                ZPackage payload = invalid == "malformed parameters" ? new(new byte[] { 0x80 }) : Payload("dodge");
                if (invalid == "parameter trailing") { payload.SetPos(payload.Size()); payload.Write(false); }
                byte[] bytes = Envelope(Data("SetTrigger", payload)).GetArray();
                if (invalid == "header only") bytes = bytes.Take(44).ToArray();
                if (invalid == "negative length") bytes = bytes.Take(44).Concat(BitConverter.GetBytes(-1)).ToArray();
                if (invalid == "truncated parameters") bytes = bytes.Take(bytes.Length - 1).ToArray();
                if (invalid == "envelope trailing") bytes = bytes.Concat(new byte[] { 0 }).ToArray();
                var p = new ZPackage(bytes); p.SetPos(4); Counters();
                RawPackage(p, frame => Assert(frame?.Principal == null && frame?.Pending.Count == 0, "invalid full schema staged observation"), envelopeCopies: 1);
                Assert(RawReplay == 0 && batches.Count == 0 && loss.Retained == 0, "invalid full schema consumed replay or became gameplay/loss");
            });
        Test("nested partial-read failure masks authenticated parent and restores before nested work", () =>
        {
            flags = new(cookingCompletions: true);
            var input = Payload("raw"); input.SetPos(input.Size()); input.Write(false);
            Raw(Data("RPC_AddItem", input), parent => {
                Assert(parent?.Actor != null && !parent.Failed, "missing authenticated parent");
                var p = Envelope(Data("Unrelated")); p.ThrowOnRead = 3;
                bool captured = false;
                RawPackage(p, child => {
                    Assert(child?.Failed == true && child.Actor == null && ReferenceEquals(ActivityPatches.Sender, child), "failed peek borrowed parent origin");
                    ActivityPatches.Stage(() => captured = true);
                }, envelopeCopies: 0, headerReads: 3);
                Assert(!captured && ReferenceEquals(ActivityPatches.Sender, parent), "failed peek leaked capture/scope");
            });
            Assert(ActivityPatches.Sender == null && RawReplay == 0, "failed nested peek leaked root or charged parent");
        });
        Test("non-eat decoded trigger validates schema before full snapshot", () =>
        {
            flags = new(consumableUses: true);
            Decoded(Data("SetTrigger", Payload("wave")));
            Assert(ZNet.instance.PeerReads == 0 && ZNet.instance.PlayerReads == 0, "non-eat trigger expanded identity snapshot");
            Assert(ActivityPatches.ManualRoutes.SignalReplayCount == 0, "non-eat trigger consumed replay");
        });
        Test("unrelated traffic does not consume raw replay", () =>
        {
            flags = new(dodgeRolls: true);
            Raw(Data("Unrelated"));
            Assert(RawReplay == 0, "unrelated RPC consumed raw replay");
        });
        foreach (string method in new[] { "SetTrigger", "RPC_RequestOwn", "RPC_AddItem" })
            Test("independently disabled raw " + method + " skips nested copy and peers", () =>
            {
                flags = new(dodgeRolls: method != "SetTrigger", floorPickups: method == "SetTrigger");
                Raw(Data(method, Payload("dodge")), envelopeCopies: 0);
                Assert(ZPackage.ParameterCopies == 0 && ZNet.instance.PeerReads == 0 && RawReplay == 0, "disabled method bypassed classifier");
            });
        foreach (int count in new[] { 1, 16, 64 })
            Test($"snapshot traversal is linear at {count} peers", () =>
            {
                Reset(count);
                Assert(StablePlayerResolver.NativeSignalSnapshot(DateTime.UtcNow, out var peers, out var characters), "valid snapshot unavailable");
                Assert(peers.Length == count && characters.Length == count, "snapshot lost identity evidence");
                Console.WriteLine($"COUNT peers={count} lists={ZNet.instance.PeerReads}/{ZNet.instance.PlayerReads} hosts={Socket.HostReads} ready={ZNetPeer.ReadyReads} connected={Socket.ConnectedReads}");
                Assert(ZNet.instance.PeerReads == 1 && ZNet.instance.PlayerReads == 1 && Socket.HostReads <= count &&
                    ZNetPeer.ReadyReads <= count && Socket.ConnectedReads <= count, "snapshot repeated peer traversal");
            });
        Test("snapshot rejects 257 peers without truncated evidence", () =>
        {
            Reset(257);
            SnapshotUnavailable();
        });
        Test("snapshot accepts complete 256-peer evidence", () =>
        {
            Reset(256);
            Assert(StablePlayerResolver.NativeSignalSnapshot(DateTime.UtcNow, out var peers, out var characters) && peers.Length == 256 && characters.Length == 256, "valid bound rejected");
        });
        Test("snapshot rejects 257 player records", () =>
        {
            for (int i = 0; i < 256; i++) ZNet.instance.Players.Add(new() { m_characterID = new(i + 100, 2) });
            SnapshotUnavailable(); Assert(Socket.HostReads == 0, "overflow scanned peers before rejecting bound");
        });
        foreach (string duplicate in new[] { "account", "uid", "character", "socket", "transport", "player record", "conflicting player record" })
            Test("snapshot rejects duplicate " + duplicate, () =>
            {
                Reset(2); var a = ZNet.instance.Peers[0]; var b = ZNet.instance.Peers[1];
                switch (duplicate)
                {
                    case "account": b.m_socket.Host = a.m_socket.Host; break;
                    case "uid": b.m_uid = a.m_uid; break;
                    case "character": b.m_characterID = a.m_characterID; break;
                    case "socket": b.m_socket = a.m_socket; break;
                    case "transport": b.m_rpc = a.m_rpc; break;
                    case "player record": ZNet.instance.Players.Add(ZNet.instance.Players[0]); break;
                    case "conflicting player record": ZNet.instance.Players.Add(new() { m_characterID = new(10, 99) }); break;
                }
                SnapshotUnavailable();
            });
        Test("snapshot unavailable outside dedicated world", () => { ZNet.instance.Dedicated = false; SnapshotUnavailable(); });
        Test("classifier flags are independent", () =>
        {
            foreach (string method in new[] { "SetTrigger", "RPC_RequestOwn", "RPC_AddItem" })
            {
                int hash = method.GetStableHashCode();
                Assert(!ActivityPatches.ShouldObserve(hash, new()), "disabled classifier admitted method");
                Assert(ActivityPatches.ShouldObserve(hash, new(dodgeRolls: method == "SetTrigger", floorPickups: method == "RPC_RequestOwn", cookingCompletions: method == "RPC_AddItem")), "enabled classifier missed method");
            }
            Assert(!ActivityPatches.ShouldObserve("ChatMessage".GetStableHashCode(), new(dodgeRolls: true, floorPickups: true, cookingCompletions: true)), "classifier captured manual RPC");
            Assert(!ActivityPatches.ShouldObserve("SetTrigger".GetStableHashCode(), new(consumableUses: true)), "raw classifier duplicated decoded signal path");
        });
        foreach (string method in new[] { "SetTrigger", "RPC_RegisterKill", "Other" })
            Test("disabled/unrelated decoded " + method + " does no payload/peer/replay work", () =>
            {
                flags = method == "Other" ? new(consumableUses: true, reportedKillCredits: true) : new();
                var payload = Payload("eat"); payload.SetPos(2); int sets = payload.PositionsSet;
                Decoded(Data(method, payload));
                Assert(payload.Copies == 0 && payload.Reads == 0 && payload.GetPos() == 2 && payload.PositionsSet == sets &&
                    ZNet.instance.PeerReads == 0 && ZNet.instance.PlayerReads == 0 && ActivityPatches.ManualRoutes.SignalReplayCount == 0, "irrelevant decoded work");
            });
        foreach (string invalid in new[] { "empty", "malformed", "utf8", "trailing", "oversized", "non-eat", "kill trailing", "kill oversized", "kill malformed" })
            Test("decoded schema before peers: " + invalid, () =>
            {
                flags = new(consumableUses: true, reportedKillCredits: true);
                bool kill = invalid.StartsWith("kill");
                ZPackage p = invalid switch {
                    "empty" => new(), "malformed" => new(new byte[] { 0x80 }), "utf8" => new(new byte[] { 1, 0xff }),
                    "oversized" => Payload(new string('e', 65)), "non-eat" => Payload("dodge"),
                    "kill oversized" => Payload(new string('x', 4096)), "kill malformed" => Payload("Troll"),
                    _ => kill ? KillPayload() : Payload("eat") };
                if (invalid.Contains("trailing")) { p.SetPos(p.Size()); p.Write(false); }
                p.SetPos(1); int sets = p.PositionsSet;
                Decoded(Data(kill ? "RPC_RegisterKill" : "SetTrigger", p));
                Assert(ZNet.instance.PeerReads == 0 && ZNet.instance.PlayerReads == 0 && SignalCount == 0 && ActivityPatches.ManualRoutes.SignalReplayCount == 0, "invalid schema expanded peers or reserved replay");
                Assert(p.GetPos() == 1 && p.Reads == 0 && p.PositionsSet == sets, "schema read borrowed cursor");
                if (invalid.Contains("oversized")) Assert(p.Copies == 0, "oversized payload copied");
            });
        Test("valid decoded eat authenticates and revalidates without cursor reads", () =>
        {
            flags = new(consumableUses: true); var p = Payload("eat"); p.SetPos(2); int sets = p.PositionsSet;
            var d = Data("SetTrigger", p); Decoded(d);
            Assert(SignalCount == 1 && ActivityPatches.ManualRoutes.SignalReplayCount == 1 && ZNet.instance.PlayerReads == 2, "valid eat lost capture/dispatch checks");
            Assert(p.GetPos() == 2 && p.PositionsSet == sets && p.Reads == 0 && p.Copies == 1, "decoded cursor/copy contract changed");
            Decoded(d); Assert(SignalCount == 1, "duplicate eat persisted twice");
        });
        Test("decoded kill credits retain distinct reporter and recipient", () =>
        {
            Reset(2); flags = new(reportedKillCredits: true);
            var d = Data("RPC_RegisterKill", KillPayload()); d.m_targetPeerID = 11; d.m_targetZDO = ZDOID.None;
            Decoded(d);
            Assert(SignalCount == 1 && batches.Single().Signals.Single().Actor.PlayerId == "account1", "kill target attribution changed");
        });
        Test("forged decoded sender rejected", () =>
        {
            flags = new(consumableUses: true); var d = Data("SetTrigger", Payload("eat")); d.m_senderPeerID = 11;
            Decoded(d); Assert(SignalCount == 0 && ActivityPatches.ManualRoutes.SignalReplayCount == 0, "forged signal admitted");
        });
        foreach (string invalid in new[] { "disconnected", "not ready", "server peer", "rpc socket mismatch", "invalid platform", "missing player", "wrong character", "wrong recipient" })
            Test("decoded origin/identity still rejects " + invalid, () =>
            {
                flags = new(consumableUses: true); var peer = ZNet.instance.Peers[0]; var d = Data("SetTrigger", Payload("eat"));
                switch (invalid)
                {
                    case "disconnected": peer.m_socket.Connected = false; break;
                    case "not ready": peer.Ready = false; break;
                    case "server peer": peer.m_server = true; break;
                    case "rpc socket mismatch": peer.m_rpc.Socket = new(); break;
                    case "invalid platform": ZNet.instance.Players[0].m_userInfo.m_id.IsValid = false; break;
                    case "missing player": ZNet.instance.Players.Clear(); break;
                    case "wrong character": d.m_targetZDO = new(10, 2); break;
                    case "wrong recipient": d.m_targetPeerID = 1; break;
                }
                Decoded(d);
                Assert(SignalCount == 0 && ActivityPatches.ManualRoutes.SignalReplayCount == 0, "invalid origin admitted");
            });
        Test("decoded signal requires an enclosing receive or genuine local invocation", () =>
        {
            flags = new(consumableUses: true);
            NativeSignalPatches.Decoded.Prefix(Data("SetTrigger", Payload("eat")), out var capture);
            NativeSignalPatches.Decoded.Finalizer(capture, null);
            Assert(capture == null && SignalCount == 0, "unscoped signal accepted");
        });
        Test("local kill keeps genuine invocation provenance", () =>
        {
            flags = new(reportedKillCredits: true);
            var d = Data("RPC_RegisterKill", KillPayload()); d.m_senderPeerID = 1; d.m_targetPeerID = 10; d.m_targetZDO = ZDOID.None;
            NativeSignalPatches.Local.Prefix(10, ZDOID.None, "RPC_RegisterKill", out var frame);
            NativeSignalPatches.Decoded.Prefix(d, out var capture);
            NativeSignalPatches.Decoded.Finalizer(capture, null);
            Assert(SignalCount == 0, "local prefix committed before finalizer");
            NativeSignalPatches.Local.Finalizer(frame, null);
            Assert(SignalCount == 1, "genuine local kill rejected");
        });
        foreach (string stale in new[] { "socket", "character", "world", "player list", "overflow" })
            Test("decoded dispatch rejects stale " + stale, () =>
            {
                flags = new(consumableUses: true);
                Decoded(Data("SetTrigger", Payload("eat")), () => {
                    var peer = ZNet.instance.Peers[0];
                    switch (stale)
                    {
                        case "socket": peer.m_socket = new() { Host = "account0" }; peer.m_rpc.Socket = peer.m_socket; break;
                        case "character": peer.m_characterID = new(10, 99); break;
                        case "world": ZNet.instance.WorldUid++; break;
                        case "player list": ZNet.instance.Players.Clear(); break;
                        case "overflow": for (int i = 0; i < 256; i++) ZNet.instance.Peers.Add(new()); break;
                    }
                });
                Assert(SignalCount == 0, "stale signal dispatched");
            });
        Test("decoded capture rejects unavailable snapshot", () =>
        {
            Reset(257); flags = new(consumableUses: true);
            Decoded(Data("SetTrigger", Payload("eat")));
            Assert(SignalCount == 0 && ActivityPatches.ManualRoutes.SignalReplayCount == 0, "unbounded identity admitted");
        });
        Test("raw forged sender never establishes cooking origin", () =>
        {
            flags = new(cookingCompletions: true); var p = Payload("raw"); p.SetPos(p.Size()); p.Write(false);
            var d = Data("RPC_AddItem", p); d.m_senderPeerID = 99;
            Raw(d, frame => Assert(frame != null && frame.Failed && frame.Actor == null, "forged cooking origin trusted"));
            Assert(RawReplay == 0, "forged raw sender consumed replay");
        });
        Test("cooking AddItem keeps real authenticated provenance and accepted input", () =>
        {
            flags = new(cookingCompletions: true);
            runtime!.Collector.Tick(DateTime.UtcNow, 100, "50", flags);
            var station = new CookingStation(); station.m_nview.Zdo.m_uid = new(1, 99);
            var p = Payload("raw"); p.SetPos(p.Size()); p.Write(false);
            var d = Data("RPC_AddItem", p); d.m_targetZDO = station.m_nview.Zdo.m_uid; d.m_targetPeerID = 1;
            Raw(d, frame => {
                Assert(frame?.Actor?.PlayerId == "account0" && frame.CookingItem == "raw" && !frame.Failed, "cooking transport provenance lost");
                Cook(station); Assert(runtime.Cooking.PendingCount == 0, "cooking committed before enclosing finalizer");
            });
            Assert(runtime.Cooking.PendingCount == 1, "accepted cooking input was lost");
            runtime.Collector.FlushThrough(DateTime.UtcNow.AddSeconds(1));
            Assert(batches.SelectMany(b => b.Cooking).Any(c => c.Kind == CookingChangeKind.AcceptedInput && c.Actor.PlayerId == "account0"), "wrong accepted inserter");
        });
        foreach (string reason in new[] { "unrelated", "disabled", "no world", "runtime unavailable", "exception" })
            Test("nested raw ingress masks parent before early return: " + reason, () =>
            {
                flags = new(cookingCompletions: true);
                var p = Payload("raw"); p.SetPos(p.Size()); p.Write(false);
                Raw(Data("RPC_AddItem", p), parent => {
                    Assert(parent?.Actor != null && !parent.Failed, "missing authenticated parent");
                    if (reason == "disabled") flags = new();
                    if (reason == "no world") ZNet.instance.WorldUid = 0;
                    if (reason == "runtime unavailable") { runtime!.Dispose(); runtime = null; }
                    bool captured = false;
                    Raw(Data("Unrelated"), child => {
                        Assert(child != null && child.Failed && child.Actor == null && ReferenceEquals(ActivityPatches.Sender, child) && !ReferenceEquals(child, parent), "nested origin borrowed parent");
                        ActivityPatches.Stage(() => captured = true);
                    }, reason == "exception" ? new Exception("native failure") : null);
                    Assert(!captured && ReferenceEquals(ActivityPatches.Sender, parent), "failed child leaked capture or failed unwind");
                });
                Assert(ActivityPatches.Sender == null, "raw root scope leaked");
            });
        Test("manual scope recovers independently after raw skip and native exception", () =>
        {
            int commands = 0;
            ActivityPatches.ManualCommand = (rpc, actor, world, text) => {
                Assert(ReferenceEquals(rpc, ZNet.instance.Peers[0].m_rpc) && actor.PlayerId == "account0" && world == "50" && text == "!dc daily", "manual origin changed"); commands++;
            };
            Raw(Data("Other"), error: new Exception("native failure"));
            Manual(1, error: new Exception("native failure")); Assert(commands == 0, "failed manual root invoked");
            Manual(2, sender: 11); Assert(commands == 0, "forged manual sender invoked");
            Manual(3); Assert(commands == 1, "next independent manual command failed");
            Manual(3); Assert(commands == 1, "manual replay protection lost");
        });
        Test("nested failed manual receive rejects parent signal and recovers", () =>
        {
            flags = new(consumableUses: true);
            ActivityPatches.ManualReceive.Prefix(ZNet.instance.Peers[0].m_rpc, out var outer);
            NativeSignalPatches.Decoded.Prefix(Data("SetTrigger", Payload("eat")), out var capture);
            NativeSignalPatches.Decoded.Finalizer(capture, null);
            ActivityPatches.ManualReceive.Prefix(new(new()), out var inner);
            ActivityPatches.ManualReceive.Finalizer(inner, new Exception("native failure"));
            ActivityPatches.ManualReceive.Finalizer(outer, null);
            Assert(SignalCount == 0, "failed nested receive dispatched parent");
            Decoded(Data("SetTrigger", Payload("eat"), 2)); Assert(SignalCount == 1, "next signal scope did not recover");
        });
        Test("native emote adapter still enforces admin authorization", () =>
        {
            ActivityPatches.EmoteTriggers = new(true, "flex", "toast");
            ZNet.instance.Admin = false;
            ActivityPatches.EmoteData.Prefix(ZDOMan.instance, ZNet.instance.Peers[0].m_rpc, out var capture);
            Assert(capture != null && capture.Actor == null, "non-admin emote obtained authorization");
            ActivityPatches.EmoteData.Postfix(capture, true);
            ActivityPatches.EmoteData.Finalizer(capture, null);
        });
        Console.WriteLine($"{passed} native adapter checks passed; {failed} failed (native/Harmony surfaces are counting doubles)");
        return failed == 0 ? 0 : 1;
    }
}
