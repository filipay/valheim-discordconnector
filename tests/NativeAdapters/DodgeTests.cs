using DiscordConnector.Patches;
using DiscordConnector.Records;

internal static partial class Program
{
    // Metadata/source pins, not proof of delivery or accepted native rewards.
    private const string DodgeAssemblySha = "72f77a0e9c70cf0329e9f1f58687bba5dc9fc77f203bc1826f3a3ca1a7b81312";
    private const string DodgeNpsCommit = "82e7396a007806b878c5db79f39a2ef274d0680d";
    private static LeaderboardsOptions DodgeFlags() => new(reportedDodgeHits: true);
    private static ZRoutedRpc.RoutedRPCData Dodge(long owner = 12, long message = 1)
    {
        Reset(3); flags = DodgeFlags(); ZNetScene.instance = new();
        var d = Data("RPC_HitWhileDodging", message: message);
        d.m_targetZDO = ZNet.instance.Peers[1].m_characterID; d.m_targetPeerID = owner;
        var zdo = new ZDO { m_uid = d.m_targetZDO, Owner = owner };
        ZDOMan.instance.Zdos.Add(zdo.m_uid, zdo);
        ZNetScene.instance.Views.Add(zdo, new ZNetView { Zdo = zdo });
        return d;
    }
    private static void LocalDodge(ZRoutedRpc.RoutedRPCData d, Action? inside = null, Exception? error = null,
        long? recipient = null, ZDOID? character = null, string method = "RPC_HitWhileDodging", object[]? arguments = null)
    {
        NativeSignalPatches.Local.Prefix(recipient ?? d.m_targetPeerID, character ?? d.m_targetZDO, method, out var root, arguments ?? Array.Empty<object>());
        try
        {
            NativeSignalPatches.Decoded.Prefix(d, out var capture);
            NativeSignalPatches.Decoded.Finalizer(capture, null);
            inside?.Invoke();
            Assert(SignalCount == 0, "local committed before enclosing finalizer");
        }
        finally { NativeSignalPatches.Local.Finalizer(root, error); }
    }
    private static void DodgeChecks()
    {
        Test("dodge exact empty payload needs no copy", () =>
        {
            Assert(DodgeAssemblySha.Length == 64 && DodgeNpsCommit.Length == 40 && "RPC_HitWhileDodging".GetStableHashCode() == 755978008, "native pins/hash changed");
            Assert(NativeSignalCollector.ValidPayload(ReportedSignalKind.DodgeHitNotification, true, 0, () => throw new Exception("copy forbidden")), "exact zero payload rejected");
        });
        foreach (long owner in new long[] { 11, 12, 1 })
            Test("dodge independent reporter/beneficiary/owner " + owner, () =>
            {
                var d = Dodge(owner); Decoded(d);
                Assert(SignalCount == 1 && batches.Single().Signals.Single().Actor.PlayerId == "account1" &&
                    batches.Single().SignalPrincipal!.AccountId == "account0", "empty native dodge not independently attributed");
                Assert(batches.Single().Signals.Single().Kind == ReportedSignalKind.DodgeHitNotification && d.m_parameters.Copies == 0 && d.m_parameters.Reads == 0,
                    "dodge reused old kind or copied borrowed empty payload");
            });
        Test("dodge genuine four-argument native invocation creates local envelope", () =>
        {
            var d = Dodge(); d.m_senderPeerID = ZNet.GetUID();
            LocalDodge(d);
            Assert(SignalCount == 1 && batches.Single().SignalPrincipal!.ServerLocal, "genuine local dodge envelope missing");
            Assert(batches.Single().Signals.Single().Actor.PlayerId == "account1", "local origin fabricated a player");
        });
        Test("dodge genuine local server-owned character still credits connected beneficiary", () =>
        {
            var d = Dodge(ZNet.GetUID()); d.m_senderPeerID = ZNet.GetUID(); LocalDodge(d);
            Assert(SignalCount == 1 && batches.Single().Signals.Single().Actor.PlayerId == "account1" && batches.Single().SignalPrincipal!.ServerLocal,
                "server ownership guessed a fake beneficiary or origin peer");
        });
        Test("actual Plugin binds independent default-off dodge flag and native coverage", () => WithPlugin((plugin, store, automatic, manual) =>
        {
            var entry = (BepInEx.Configuration.ConfigEntry<bool>)plugin.Config.Entries["Server-Reported Combat/Dodge Hit Notifications Enabled"];
            Assert(!entry.Value, "new native metric must default off");
            var snapshot = plugin.GetType().GetMethod("EffectiveOptions", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.CreateDelegate<Func<LeaderboardsOptions>>(plugin);
            Assert(!snapshot().ReportedDodgeHits, "Plugin unexpectedly enabled metric");
            entry.Value = true; plugin.Config.Changed();
            Assert(snapshot().ReportedDodgeHits && !snapshot().DodgeRolls && !snapshot().ReportedKillCredits && !snapshot().ConsumableUses,
                "new config conflated independent flags");
            plugin.UpdateObservationState(DateTime.UtcNow, 100);
            var captured = (Dictionary<ReportMetric, CoverageState>)typeof(NativeSignalPatches).GetField("coverage", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null)!;
            Assert(captured[ReportMetric.ReportedDodgeHitNotifications] == CoverageState.Partial, "supported native notification coverage was not partial");
        }));
        foreach (string invalid in new[] { "disabled", "one byte", "wrong method", "null payload" })
            Test("dodge cheap rejection before copy/snapshot: " + invalid, () =>
            {
                var d = Dodge();
                if (invalid == "disabled") flags = new(dodgeRolls: true);
                if (invalid == "one byte") d.m_parameters = new(new byte[] { 0 });
                if (invalid == "wrong method") d.m_methodHash = "RPC_HitWhileDodging ".GetStableHashCode();
                if (invalid == "null payload") d.m_parameters = null!;
                Decoded(d);
                Assert(SignalCount == 0 && ZPackage.ArrayCopies == 0 && ZNet.instance.PeerReads == 0 && ZNet.instance.PlayerReads == 0 &&
                    ActivityPatches.ManualRoutes.SignalReplayCount == 0, "invalid dodge copied, snapshotted or consumed replay");
            });
        foreach (string invalid in new[] { "absent view", "absent zdo", "view zdo mismatch", "zero owner", "unknown owner", "broadcast", "wrong recipient",
            "stale full character", "missing beneficiary", "forged sender", "owner unready", "owner disconnected", "beneficiary unready" })
            Test("dodge unavailable native identity: " + invalid, () =>
            {
                var d = Dodge(); var zdo = ZDOMan.instance.Zdos[d.m_targetZDO];
                switch (invalid)
                {
                    case "absent view": ZNetScene.instance.Views.Clear(); break;
                    case "absent zdo": ZDOMan.instance.Zdos.Clear(); break;
                    case "view zdo mismatch": ZNetScene.instance.Views[zdo].Zdo = new() { m_uid = d.m_targetZDO }; break;
                    case "zero owner": zdo.Owner = 0; d.m_targetPeerID = 0; break;
                    case "unknown owner": zdo.Owner = 500; d.m_targetPeerID = 500; break;
                    case "broadcast": d.m_targetPeerID = 0; break;
                    case "wrong recipient": d.m_targetPeerID = 11; break;
                    case "stale full character": d.m_targetZDO = new(11, 2); break;
                    case "missing beneficiary": ZNet.instance.Players.RemoveAt(1); break;
                    case "forged sender": d.m_senderPeerID = 11; break;
                    case "owner unready": ZNet.instance.Peers[2].Ready = false; break;
                    case "owner disconnected": ZNet.instance.Peers[2].m_socket.Connected = false; break;
                    case "beneficiary unready": ZNet.instance.Peers[1].Ready = false; break;
                }
                Decoded(d);
                Assert(SignalCount == 0 && ActivityPatches.ManualRoutes.SignalReplayCount == 0, "invalid dodge identity admitted");
            });
        Test("dodge reporter and distinct owner need authenticated sessions, not invented attacker characters", () =>
        {
            var d = Dodge();
            foreach (int index in new[] { 0, 2 })
            { ZNet.instance.Peers[index].m_characterID = ZDOID.None; ZNet.instance.Players.RemoveAll(p => p.m_characterID.UserID == index + 10); }
            Decoded(d);
            Assert(SignalCount == 1 && batches.Single().SignalPrincipal!.AccountId == "account0", "authenticated simulation/owner connection incorrectly required a current player attacker");
        });
        Test("dodge beneficiary full character does not imply peer UID or owner", () =>
        {
            var d = Dodge(); var peer = ZNet.instance.Peers[1]; var zdo = ZDOMan.instance.Zdos[d.m_targetZDO];
            ZDOMan.instance.Zdos.Clear(); d.m_targetZDO = peer.m_characterID = new(300, 7);
            ZNet.instance.Players[1].m_characterID = d.m_targetZDO; zdo.m_uid = d.m_targetZDO;
            ZDOMan.instance.Zdos.Add(d.m_targetZDO, zdo);
            Decoded(d); Assert(SignalCount == 1, "dodge inherited kill-credit character/UID mapping");
        });
        foreach (string stale in new[] { "owner transfer", "reporter reconnect", "beneficiary reconnect", "owner reconnect", "full character", "player list",
            "absent view", "world uid", "world epoch", "net epoch", "zdo epoch", "server uid", "session epoch", "disabled" })
            Test("dodge revalidates before dispatch: " + stale, () =>
            {
                var d = Dodge();
                Decoded(d, () =>
                {
                    switch (stale)
                    {
                        case "owner transfer": ZDOMan.instance.Zdos[d.m_targetZDO].Owner = 11; break;
                        case "reporter reconnect": case "beneficiary reconnect": case "owner reconnect":
                            int index = stale == "reporter reconnect" ? 0 : stale == "beneficiary reconnect" ? 1 : 2;
                            var peer = ZNet.instance.Peers[index]; peer.m_socket = new() { Host = "account" + index }; peer.m_rpc = new(peer.m_socket); break;
                        case "full character": ZNet.instance.Peers[1].m_characterID = new(11, 9); break;
                        case "player list": ZNet.instance.Players.RemoveAt(1); break;
                        case "absent view": ZNetScene.instance.Views.Clear(); break;
                        case "world uid": ZNet.instance.WorldUid++; break;
                        case "world epoch": ZNet.World = new(); break;
                        case "net epoch": ZNet.instance = new() { Peers = ZNet.instance.Peers, Players = ZNet.instance.Players }; break;
                        case "zdo epoch": ZDOMan.instance = new() { Zdos = ZDOMan.instance.Zdos }; break;
                        case "server uid": ZNet.instance.ServerUid++; break;
                        case "session epoch": NativeSignalPatches.SetSession("50", "next", "test"); break;
                        case "disabled": flags = new(); break;
                    }
                });
                Assert(SignalCount == 0, "stale dodge dispatched");
                Assert(loss.Snapshot("50", ReportMetric.ReportedDodgeHitNotifications)?.Rejections[ObservationRejection.Stale] == 1, "stale dodge loss not attributed exactly once to new metric");
            });
        Test("dodge Route/Handle/retransmit dedup but distinct notifications count independently", () =>
        {
            var d = Dodge();
            Decoded(d, () => { NativeSignalPatches.Decoded.Prefix(d, out var handle); NativeSignalPatches.Decoded.Finalizer(handle, null); });
            Decoded(d); d.m_msgID++; Decoded(d);
            Assert(SignalCount == 2 && batches.SelectMany(b => b.Signals).Select(s => s.Token).Distinct().Count() == 2,
                "observer dedup became semantic/roll dedup or repeated a single envelope");
        });
        Test("dodge decoded witness survives supported NPS skipped-original route, never proves reward acceptance", () =>
        {
            var d = Dodge();
            foreach (string method in new[] { nameof(ZRoutedRpc.RPC_RoutedRPC), nameof(ZRoutedRpc.RouteRPC), nameof(ZRoutedRpc.HandleRoutedRPC) })
                HarmonyLib.Harmony.Inventory[typeof(ZRoutedRpc).GetMethod(method)!].Prefixes.Add(new HarmonyLib.Patch
                {
                    owner = "MidnightsFX.NetworkPerformanceSystem", priority = 600,
                    PatchMethod = typeof(Program).GetMethod(nameof(OtherPrefix), System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
                });
            NativeSignalPatches.SetSession("50", "supported-nps-dodge", "test");
            // No vanilla Route/Handle or reward-handler body is executed by this counting-double fixture.
            Decoded(d);
            Assert(SignalCount == 1, "supported replaced router lost copied decoded witness");
        });
        Test("dodge quotas charge reporter not beneficiary and retain new-kind losses", () =>
        {
            var d = Dodge();
            for (int i = 0; i < 130; i++) { d.m_msgID = i; Decoded(d); }
            Assert(SignalCount == 128 && loss.Snapshot("50", ReportMetric.ReportedDodgeHitNotifications)!.Rejections[ObservationRejection.PrincipalQuota] == 2,
                "new kind bypassed origin quota or wrong metric counters");
            d.m_senderPeerID = 12; d.m_msgID = 500; Decoded(d, peer: 2);
            Assert(SignalCount == 129 && batches.Last().Signals.Single().Actor.PlayerId == "account1", "beneficiary rather than origin was exhausted");
        });
        foreach (string invalid in new[] { "method", "character", "recipient", "broadcast", "arguments", "exception" })
            Test("dodge genuine local witness rejects mismatched " + invalid, () =>
            {
                var d = Dodge(); d.m_senderPeerID = ZNet.GetUID();
                LocalDodge(d, method: invalid == "method" ? "Other" : "RPC_HitWhileDodging",
                    character: invalid == "character" ? new ZDOID(11, 2) : null,
                    recipient: invalid == "recipient" ? 11 : invalid == "broadcast" ? 0 : null,
                    arguments: invalid == "arguments" ? new object[] { 1 } : null, error: invalid == "exception" ? new Exception() : null);
                Assert(SignalCount == 0 && ActivityPatches.ManualRoutes.SignalReplayCount == 0, "false local provenance committed");
            });
        Test("dodge remote exception consumes no replay and next notification recovers", () =>
        {
            var d = Dodge(); Decoded(d, error: new Exception());
            Assert(SignalCount == 0 && ActivityPatches.ManualRoutes.SignalReplayCount == 0, "failed root consumed notification");
            Decoded(d); Assert(SignalCount == 1, "next genuine notification failed");
        });
        Test("dodge genuine local dispatch also revalidates ownership before enclosing unwind", () =>
        {
            var d = Dodge(); d.m_senderPeerID = ZNet.GetUID();
            LocalDodge(d, () => ZDOMan.instance.Zdos[d.m_targetZDO].Owner = 11);
            Assert(SignalCount == 0 && loss.Snapshot("50", ReportMetric.ReportedDodgeHitNotifications)!.Rejections[ObservationRejection.Stale] == 1,
                "local callback skipped native ownership revalidation");
        });
        Test("dodge local-only native invocation without decoded witness synthesizes nothing", () =>
        {
            var d = Dodge();
            NativeSignalPatches.Local.Prefix(d.m_targetPeerID, d.m_targetZDO, "RPC_HitWhileDodging", out var root, Array.Empty<object>());
            NativeSignalPatches.Local.Finalizer(root, null);
            Assert(SignalCount == 0 && ActivityPatches.ManualRoutes.SignalReplayCount == 0, "local prefix invented a notification");
        });
        Test("dodge nested disabled local invocation masks enabled local parent", () =>
        {
            var d = Dodge(); d.m_senderPeerID = ZNet.GetUID();
            NativeSignalPatches.Local.Prefix(12, d.m_targetZDO, "RPC_HitWhileDodging", out var outer, Array.Empty<object>());
            flags = new();
            NativeSignalPatches.Local.Prefix(12, d.m_targetZDO, "RPC_HitWhileDodging", out var disabled, Array.Empty<object>());
            flags = DodgeFlags();
            NativeSignalPatches.Decoded.Prefix(d, out var rejected); NativeSignalPatches.Decoded.Finalizer(rejected, null);
            NativeSignalPatches.Local.Finalizer(disabled, null);
            Assert(rejected == null, "disabled local child borrowed enabled parent's origin");
            NativeSignalPatches.Decoded.Prefix(d, out var valid); NativeSignalPatches.Decoded.Finalizer(valid, null);
            NativeSignalPatches.Local.Finalizer(outer, null);
            Assert(SignalCount == 1, "local origin did not restore after disabled child");
        });
        Test("dodge local scope masks even when option snapshot throws", () =>
        {
            var d = Dodge();
            NativeSignalPatches.Configure(new(batch => { batches.Add(batch); return true; }, _ => { }, loss), () => throw new Exception("private"));
            NativeSignalPatches.SetSession("50", "throwing-options", "test");
            ActivityPatches.ManualReceive.Prefix(ZNet.instance.Peers[0].m_rpc, out var remote);
            NativeSignalPatches.Local.Prefix(12, d.m_targetZDO, "RPC_HitWhileDodging", out var child, Array.Empty<object>());
            Assert(child?.Frame != null && ActivityPatches.ManualRoutes.Current?.Transport == null && ActivityPatches.ManualRoutes.Current?.ServerUid == 0,
                "throwing local classifier leaked remote provenance");
            NativeSignalPatches.Local.Finalizer(child, null); ActivityPatches.ManualReceive.Finalizer(remote, null);
            Assert(ActivityPatches.ManualRoutes.Current == null && NativeSignalPatches.Local.Current == null, "throwing local scope leaked");
        });
        Test("dodge nested irrelevant local invocation masks remote provenance and restores it", () =>
        {
            var d = Dodge();
            ActivityPatches.ManualReceive.Prefix(ZNet.instance.Peers[0].m_rpc, out var remote);
            NativeSignalPatches.Local.Prefix(0, ZDOID.None, "Other", out var inner, Array.Empty<object>());
            NativeSignalPatches.Decoded.Prefix(d, out var rejected); NativeSignalPatches.Decoded.Finalizer(rejected, null);
            NativeSignalPatches.Local.Finalizer(inner, null);
            Assert(rejected == null, "irrelevant nested local borrowed remote parent");
            NativeSignalPatches.Decoded.Prefix(d, out var valid); NativeSignalPatches.Decoded.Finalizer(valid, null);
            ActivityPatches.ManualReceive.Finalizer(remote, null);
            Assert(SignalCount == 1, "remote provenance did not restore");
        });
        Test("dodge remote scope masks local origin and both genuine origins restore", () =>
        {
            var d = Dodge(); d.m_senderPeerID = ZNet.GetUID();
            NativeSignalPatches.Local.Prefix(12, d.m_targetZDO, "RPC_HitWhileDodging", out var local, Array.Empty<object>());
            ActivityPatches.ManualReceive.Prefix(ZNet.instance.Peers[0].m_rpc, out var remote);
            NativeSignalPatches.Decoded.Prefix(d, out var rejected); NativeSignalPatches.Decoded.Finalizer(rejected, null);
            ActivityPatches.ManualReceive.Finalizer(remote, null);
            Assert(rejected == null, "remote borrowed local identity");
            NativeSignalPatches.Decoded.Prefix(d, out var accepted); NativeSignalPatches.Decoded.Finalizer(accepted, null);
            NativeSignalPatches.Local.Finalizer(local, null);
            Assert(SignalCount == 1 && batches.Single().SignalPrincipal!.ServerLocal, "local provenance failed restoration");
        });
        Test("same-client-only dodge dispatch is invisible and produces no synthetic count", () =>
        {
            var d = Dodge(11); // A local native player handler on a client does not enter any server observer.
            runtime!.Collector.Tick(DateTime.UtcNow, 100, "50", flags);
            runtime.Collector.FlushThrough(DateTime.UtcNow.AddSeconds(1));
            Assert(SignalCount == 0 && ActivityPatches.ManualRoutes.SignalReplayCount == 0, "server synthesized a client-only notification");
            NativeSignalPatches.Decoded.Prefix(d, out var unscoped); NativeSignalPatches.Decoded.Finalizer(unscoped, null);
            Assert(unscoped == null && SignalCount == 0, "unscoped client dispatch became server observation");
        });
    }
}
