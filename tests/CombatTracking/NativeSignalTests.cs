using System.Text;
using DiscordConnector.Leaderboards;
using DiscordConnector.Records;

internal static class NativeSignalTests
{
    private static readonly DateTime Utc = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static ManualCommandRouting.Observation? ObserveSignal(ManualCommandRouting routes, long claimed, long actual,
        string session, string world, long message, string target, RoutingObservationKind kind, double time, Action invoke, string account = "Alice") =>
        routes.ObserveSignal(claimed, actual, session, world, message, target, kind, time, invoke, new(account, session, false));
    private static ManualCommandRouting.Observation? ObserveLocalSignal(ManualCommandRouting routes, long claimed,
        string session, string world, long message, string target, double time, Action invoke) =>
        routes.ObserveLocalSignal(claimed, session, world, message, target, time, invoke, new("server", session, true));
    private static NativeSignalPeer Peer(long uid, string account, object? rpc = null, uint character = 1,
        string session = "session", object? socket = null, bool ready = true) =>
        new(uid, (uid, character), new ActivityActor(account, account, session, Utc), rpc ?? new object(), socket ?? new object(), ready);
    private static byte[] Payload(ReportedSignalKind kind, string text = "eat", int boss = 0, int modifiers = 5, int attackers = 1, byte cheats = 0)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write(text);
        if (kind == ReportedSignalKind.KillCredit) { writer.Write(boss); writer.Write(modifiers); writer.Write(attackers); writer.Write(cheats); }
        return stream.ToArray();
    }
    internal static void Run()
    {
        void Test(string name, Action test) { test(); Console.WriteLine("PASS native signals: " + name); }
        Test("real detached identity credits Bob, not reporter Alice; local and remote provenance", Identity);
        Test("forged, stale and ambiguous peer/character bindings fail closed", Rejections);
        Test("disabled before copy; bounded typed payloads and exact exhaustion", Payloads);
        Test("shared decoded witnesses deduplicate; exception and double completion", Routing);
        Test("local/remote nesting masks and restores; staged expiry", OriginsAndAge);
        Test("mixed pending/depth overflow fails root without replay poisoning", Overflow);
        Test("one origin cannot consume global replay; reconnect retains account charge", FairReplay);
        Test("rolling account/connection tombstones, principal capacity and clock fences", AdmissionPolicy);
        Test("root failures spend neither quota nor replay; duplicate and callback loss semantics", AdmissionRoots);
        Test("separate replay budgets; bounded FIFO pruning and node count", Pressure);
        Test("immutable durable token and bounded collector rejection/reset/disposal", Collector);
        Test("real callback generation/connection fences and diagnostic budgets", LifecycleAndDiagnostics);
        Test("routing reasons, pending discard and bounded diagnostic retries are cumulative", LossReasons);
        Test("dodge independent native evidence, additive kinds and origin-based routing losses", DodgeNotifications);
    }
    private static void DodgeNotifications()
    {
        var reporter = Peer(1, "Reporter"); var beneficiary = Peer(2, "Dodger"); var owner = Peer(3, "Owner");
        var peers = new[] { reporter, beneficiary, owner }; var characters = peers.Select(p => p.Character).ToArray();
        foreach (long uid in new long[] { 2, 3, 99 })
        {
            Check(NativeSignalCollector.TryDodgeIdentity(false, reporter.Transport, 99, 1, uid, beneficiary.Character, uid,
                peers, characters, out var actualReporter, out var target) && actualReporter == reporter && target!.Beneficiary == beneficiary &&
                target.Character == beneficiary.Character && target.OwnerUid == uid && target.Owner == (uid == 99 ? null : uid == 2 ? beneficiary : owner), "three independent identities");
        }
        Check(NativeSignalCollector.TryDodgeIdentity(true, null, 99, 99, 3, beneficiary.Character, 3, peers, characters,
            out var localReporter, out _) && localReporter == null, "local origin has no fabricated peer");
        foreach (long recipient in new long[] { 0, 1, 2, 99 })
            Check(!NativeSignalCollector.TryDodgeIdentity(false, reporter.Transport, 99, 1, recipient, beneficiary.Character, 3,
                peers, characters, out _, out _), "recipient must equal actual owner");
        Check(!NativeSignalCollector.TryDodgeIdentity(false, reporter.Transport, 99, 2, 3, beneficiary.Character, 3, peers, characters, out _, out _), "forged reporter");
        Check(!NativeSignalCollector.TryDodgeIdentity(false, reporter.Transport, 99, 1, 3, (2, 2), 3, peers, characters, out _, out _), "stale full character");
        Check(!NativeSignalCollector.TryDodgeIdentity(false, reporter.Transport, 99, 1, 3, beneficiary.Character, 3, peers,
            characters.Concat(new[] { beneficiary.Character }).ToArray(), out _, out _), "ambiguous full player character");
        Check(!NativeSignalCollector.TryDodgeIdentity(false, reporter.Transport, 99, 1, 4, beneficiary.Character, 4, peers, characters, out _, out _), "unknown owner");
        Check(NativeSignalCollector.ValidPayload(ReportedSignalKind.DodgeHitNotification, true, 0, () => throw new Exception()), "empty schema no copy");
        Check(!NativeSignalCollector.ValidPayload(ReportedSignalKind.DodgeHitNotification, false, 0, () => throw new Exception()) &&
            !NativeSignalCollector.ValidPayload(ReportedSignalKind.DodgeHitNotification, true, 1, () => throw new Exception()), "disabled/trailing rejects before copy");
        Check((int)ReportedSignalKind.KillCredit == 0 && (int)ReportedSignalKind.ConsumableUse == 1 && (int)ReportedSignalKind.DodgeHitNotification == 2 &&
            (int)RoutingObservationKind.KillCredit == 1 && (int)RoutingObservationKind.ConsumableUse == 2 && (int)RoutingObservationKind.DodgeHitNotification == 3, "append-only signal enums");
        ObservationLoss loss = new(_ => { }, "dodge", () => Utc); ManualCommandRouting routes = new(() => 0, loss);
        int calls = 0;
        for (int i = 0; i < 130; i++)
        {
            var root = routes.Enter(reporter.Transport);
            routes.Complete(ObserveSignal(routes, 1, 1, "session", "world", i, "Dodger", RoutingObservationKind.DodgeHitNotification, 0, () => calls++, "Reporter"), null);
            routes.Finish(root, null);
        }
        Check(calls == 128 && loss.Snapshot("world", ReportMetric.ReportedDodgeHitNotifications)!.Rejections[ObservationRejection.PrincipalQuota] == 2, "new-kind origin quota/loss mapping");
        var local = routes.EnterLocal(99);
        routes.Complete(routes.ObserveLocalSignal(99, "epoch", "world", 1, "Dodger", RoutingObservationKind.DodgeHitNotification, 0, () => calls++, new("server", "epoch", true)), null);
        routes.Finish(local, null); Check(calls == 129, "local explicit new-kind overload independent allowance");
        using NativeSignalCollector collector = new(_ => false, _ => { }, loss);
        collector.Observe(new(ReportedSignalKind.DodgeHitNotification, "world", beneficiary.Actor!, "dodge", Utc), new("Reporter", "session", false));
        Check(collector.DroppedFor(ReportedSignalKind.DodgeHitNotification) == 1 && collector.DroppedFor(ReportedSignalKind.ConsumableUse) == 0 &&
            loss.Snapshot("world", ReportMetric.ReportedDodgeHitNotifications)!.Rejections[ObservationRejection.QueueFull] == 1, "expanded kind array/collector loss mapping");
    }
    private static void Identity()
    {
        var alice = Peer(1, "Alice"); var bob = Peer(2, "Bob"); var peers = new[] { alice, bob };
        var characters = peers.Select(p => p.Character).ToArray();
        Check(NativeSignalCollector.TryIdentity(ReportedSignalKind.KillCredit, false, alice.Transport, 99, 1, 2, default,
            peers, characters, out var sender, out var target) && sender == alice && target == bob, "sender Alice must credit Bob");
        Check(NativeSignalCollector.TryIdentity(ReportedSignalKind.KillCredit, true, null, 99, 99, 2, default,
            peers, characters, out sender, out target) && sender == null && target == bob, "genuine local kill");
        Check(NativeSignalCollector.TryIdentity(ReportedSignalKind.ConsumableUse, false, alice.Transport, 99, 1, 0, alice.Character,
            peers, characters, out sender, out target) && target == alice, "eat credits sender current full character");
        Check(!NativeSignalCollector.TryIdentity(ReportedSignalKind.ConsumableUse, true, null, 99, 99, 0, bob.Character,
            peers, characters, out _, out _), "local animator is not remote consumption");
    }

    private static void LossReasons()
    {
        double now = 0;
        ObservationLoss loss = new(_ => { }, "tracking", () => Utc);
        ManualCommandRouting routing = new(() => now, loss);
        int callbacks = 0;
        void Attempt(int id)
        {
            var root = routing.Enter(new object());
            routing.Complete(ObserveSignal(routing, 1, 1, "session", "world", id, "Bob", RoutingObservationKind.KillCredit, now, () => callbacks++), null);
            routing.Finish(root, null);
        }
        for (int i = 0; i < 129; i++) Attempt(i);
        Attempt(0);
        Check(loss.Snapshot("world", ReportMetric.ReportedKillCredits)!.Rejections[ObservationRejection.PrincipalQuota] == 1 && callbacks == 128,
            "quota exact; duplicate cannot mint a loss");
        var root = routing.Enter(new object());
        now = 1;
        routing.Complete(ObserveSignal(routing, 1, 1, "session", "world", 200, "Bob", RoutingObservationKind.KillCredit, now, () => callbacks++), null);
        now = 6.001; routing.Finish(root, null);
        routing.Reset(); routing.Finish(root, null);
        Check(loss.Snapshot("world", ReportMetric.ReportedKillCredits)!.Rejections[ObservationRejection.Stale] == 1, "stale pending discarded once, not again by reset/finalizer");
        root = routing.Enter(new object());
        for (int i = 0; i < 17; i++)
            routing.Complete(ObserveSignal(routing, 1, 1, "session", "world", 300 + i, "Bob", RoutingObservationKind.KillCredit, now, () => callbacks++), null);
        routing.Finish(root, null); routing.Reset();
        Check(loss.Snapshot("world", ReportMetric.ReportedKillCredits)!.Rejections[ObservationRejection.PendingOverflow] == 17,
            "overflow counts the refused new observation plus every unique discarded pending observation exactly once");
        int submits = 0;
        loss.Maintain(0, _ => { submits++; return Task.FromException(new IOException("uncertain acknowledgment")); }).GetAwaiter().GetResult();
        loss.Maintain(9.999, _ => { submits++; return Task.CompletedTask; }).GetAwaiter().GetResult();
        loss.Maintain(10, _ => { submits++; return Task.CompletedTask; }).GetAwaiter().GetResult();
        Check(submits == 2 && callbacks == 128, "only absolute diagnostic submission retried, never a gameplay callback");
        Check(loss.Snapshot("world", ReportMetric.ReportedKillCredits)!.Rejections.Values.Sum() == 19, "loss owner survives routing reset and acknowledgment");
        Check(loss.Retained == 1, "one cumulative world/kind key");
    }
    private static void Rejections()
    {
        var alice = Peer(1, "Alice"); var bob = Peer(2, "Bob"); var peers = new[] { alice, bob };
        var chars = peers.Select(p => p.Character).ToArray();
        bool Admit(object? transport, long claim = 1, long target = 2, NativeSignalPeer[]? candidates = null,
            (long User, uint Id)[]? current = null, bool local = false) =>
            NativeSignalCollector.TryIdentity(ReportedSignalKind.KillCredit, local, transport, 99, claim, target, default,
                candidates ?? peers, current ?? chars, out _, out _);
        Check(!Admit(null) && !Admit(alice.Transport, 99) && !Admit(alice.Transport, 2), "absent/forged/server sender");
        Check(!Admit(alice.Transport, target: 0) && !Admit(alice.Transport, target: 99), "broadcast/server kill target");
        Check(!Admit(null, claim: 1, local: true) && !Admit(alice.Transport, claim: 99, local: true), "local is not fake peer");
        Check(!Admit(alice.Transport, current: new[] { alice.Character, (2L, 9U) }), "stale full target character");
        Check(!Admit(alice.Transport, current: new[] { alice.Character, bob.Character, (2L, 9U) }), "ambiguous current player list");
        Check(!Admit(alice.Transport, candidates: new[] { alice, bob, Peer(2, "Other") }), "duplicate uid");
        Check(!Admit(alice.Transport, candidates: new[] { alice, bob, Peer(3, "Bob") }), "duplicate account");
        Check(!Admit(alice.Transport, candidates: new[] { alice, bob, Peer(3, "Other", rpc: alice.Transport) }), "duplicate actual transport");
        Check(!Admit(alice.Transport, candidates: new[] { alice, bob, Peer(3, "Other", socket: bob.Socket) }), "duplicate socket");
        Check(!Admit(alice.Transport, candidates: new[] { alice, Peer(2, "Bob", ready: false) }), "unready target");
        var mismatch = new NativeSignalPeer(2, (7, 1), bob.Actor, bob.Transport, bob.Socket, true);
        Check(!Admit(alice.Transport, candidates: new[] { alice, mismatch }, current: new[] { alice.Character, mismatch.Character }), "uid must equal full character UserID");
        Check(!NativeSignalCollector.TryIdentity(ReportedSignalKind.ConsumableUse, false, alice.Transport, 99, 1, 0, bob.Character,
            peers, chars, out _, out _), "eat cannot target Bob");
        Check(!NativeSignalCollector.SamePeer(bob, Peer(2, "Bob", session: "replacement")) &&
            !NativeSignalCollector.SamePeer(bob, Peer(2, "Bob", character: 2)), "callback must fence session and full character");
    }
    private static void Payloads()
    {
        int copies = 0; byte[] bytes = Payload(ReportedSignalKind.ConsumableUse);
        bool Valid(ReportedSignalKind kind, bool enabled = true, int? size = null) => NativeSignalCollector.ValidPayload(kind, enabled,
            size ?? bytes.Length, () => { copies++; return bytes; });
        Check(!Valid(ReportedSignalKind.ConsumableUse, false) && copies == 0, "disabled flag before payload copy");
        Check(!Valid(ReportedSignalKind.ConsumableUse, size: 65) && copies == 0, "oversized eat before copy");
        Check(Valid(ReportedSignalKind.ConsumableUse), "exact eat");
        foreach (string text in new[] { "Eat", "dodge", "eat\0", "" }) { bytes = Payload(ReportedSignalKind.ConsumableUse, text); Check(!Valid(ReportedSignalKind.ConsumableUse), "exact eat only"); }
        bytes = Payload(ReportedSignalKind.ConsumableUse).Concat(new byte[] { 0 }).ToArray(); Check(!Valid(ReportedSignalKind.ConsumableUse), "trailing bytes");
        bytes = new byte[] { 255, 255, 255, 255, 7 }; Check(!Valid(ReportedSignalKind.ConsumableUse), "hostile string length");
        foreach (var values in new[] { (0, 0, 1, (byte)0), (int.MaxValue, 5, 1024, (byte)1) })
        { bytes = Payload(ReportedSignalKind.KillCredit, new string('x', 256), values.Item1, values.Item2, values.Item3, values.Item4); Check(Valid(ReportedSignalKind.KillCredit), "valid boundary kill"); }
        foreach (byte[] invalid in new[] { Payload(ReportedSignalKind.KillCredit, "x", -1), Payload(ReportedSignalKind.KillCredit, "x", modifiers: 6),
            Payload(ReportedSignalKind.KillCredit, "x", attackers: 0), Payload(ReportedSignalKind.KillCredit, "x", attackers: 1025),
            Payload(ReportedSignalKind.KillCredit, new string('x', 257)), Payload(ReportedSignalKind.KillCredit, cheats: 2),
            Payload(ReportedSignalKind.KillCredit).SkipLast(1).ToArray(), Payload(ReportedSignalKind.KillCredit).Concat(new byte[] { 0 }).ToArray() })
        { bytes = invalid; Check(!Valid(ReportedSignalKind.KillCredit), "malformed kill"); }
        int before = copies; Check(!Valid(ReportedSignalKind.KillCredit, size: 4097) && copies == before, "kill bound before copy");
        Check(!NativeSignalCollector.ValidPayload(ReportedSignalKind.KillCredit, true, 10, () => throw new IOException()), "copy failure observational");
    }
    private static void Routing()
    {
        double now = 0; var r = new ManualCommandRouting(() => now); int count = 0;
        void Signal(long msg, string target = "Bob", RoutingObservationKind kind = RoutingObservationKind.KillCredit)
        { var o = ObserveSignal(r, 1, 1, "session", "world", msg, target, kind, now, () => count++); r.Complete(o, null); r.Complete(o, null); }
        var frame = r.Enter(new object()); Signal(1); Signal(1); Check(count == 0, "wait root"); r.Finish(frame, null); Check(count == 1, "Handle/Route and same finalizer twice");
        frame = r.Enter(new object()); Signal(1); Signal(1, "Alice"); Signal(1, "Bob", RoutingObservationKind.ConsumableUse); r.Finish(frame, null); Check(count == 3, "kind/target separate; retransmit once");
        frame = r.Enter(new object()); Signal(2); r.Finish(frame, new Exception()); Check(count == 3, "enclosing exception");
        frame = r.Enter(new object()); Signal(2); r.Finish(frame, null); Check(count == 4, "exception did not consume replay");
        frame = r.Enter(new object()); r.Complete(ObserveSignal(r, 1, 1, "session", "world", 3, "Bob", RoutingObservationKind.KillCredit, now, () => count++), new Exception()); r.Finish(frame, null); Check(count == 4, "decoded exception");
    }
    private static void OriginsAndAge()
    {
        double now = 0; var r = new ManualCommandRouting(() => now); int count = 0;
        Check(ObserveSignal(r, 1, 1, "s", "w", 1, "t", RoutingObservationKind.KillCredit, now, () => count++) == null, "no ambient provenance");
        var remote = r.Enter(new object());
        Check(ObserveSignal(r, 2, 1, "s", "w", 1, "t", RoutingObservationKind.KillCredit, now, () => count++) == null &&
            ObserveSignal(r, 1, 1, "s", "w", 1, "t", RoutingObservationKind.ManualReport, now, () => count++) == null, "remote sender equality and signal-only kinds");
        var local = r.EnterLocal(99);
        Check(r.Current?.Transport == null && ObserveSignal(r, 1, 1, "s", "w", 1, "t", RoutingObservationKind.KillCredit, now, () => count++) == null, "local masks remote");
        Check(r.Observe(99, 99, "s", "w", 1, "!dc daily", now, () => count++) == null, "manual remote only");
        Check(ObserveLocalSignal(r, 1, "epoch", "w", 1, "t", now, () => count++) == null, "local claimed sender must be the server UID");
        r.Complete(ObserveLocalSignal(r, 99, "epoch", "w", 1, "t", now, () => count++), null);
        var nested = r.Enter(new object()); Check(ObserveLocalSignal(r, 99, "epoch", "w", 1, "t", now, () => count++) == null, "remote masks local"); r.Finish(nested, null);
        Check(r.Current?.ServerUid == 99, "restore local"); r.Finish(local, null); Check(r.Current?.Transport != null, "restore remote"); r.Finish(remote, null); Check(count == 1, "distinct local path");
        remote = r.Enter(new object()); var stale = ObserveSignal(r, 1, 1, "s", "w", 2, "t", RoutingObservationKind.KillCredit, now, () => count++);
        r.Complete(stale, null); now = 5; r.Finish(remote, null); Check(count == 2, "five seconds inclusive");
        remote = r.Enter(new object()); r.Complete(ObserveSignal(r, 1, 1, "s", "w", 3, "t", RoutingObservationKind.KillCredit, now, () => count++), null);
        now = 10.001; r.Finish(remote, null); Check(count == 2, "older than five seconds discarded");
        remote = r.Enter(new object()); r.Complete(stale, null); r.Reset(); r.Finish(remote, null); Check(count == 2 && r.SignalReplayCount == 0, "world reset closes staged scopes and caches");
    }
    private static void Overflow()
    {
        var r = new ManualCommandRouting(() => 0); int calls = 0;
        void Manual() => r.Complete(r.Observe(1, 1, "s", "w", 1, "!dc daily", 0, () => calls++), null);
        var root = r.Enter(new object()); Manual();
        for (int i = 0; i < 16; i++) r.Complete(ObserveSignal(r, 1, 1, "s", "w", i, "t", RoutingObservationKind.KillCredit, 0, () => calls++), null);
        r.Finish(root, null); Check(calls == 0 && r.SignalReplayCount == 0 && r.LastRejection == ObservationRejection.PendingOverflow, "mixed pending overflow fails all without conflating overflow and replay");
        root = r.Enter(new object()); Manual(); var frames = new List<ManualCommandRouting.Frame?>();
        for (int i = 0; i < 16; i++)
        {
            frames.Add(i % 2 == 0 ? r.EnterLocal(99) : r.Enter(new object()));
            if (i % 2 == 0) r.Complete(ObserveLocalSignal(r, 99, "epoch", "w", i, "Bob", 0, () => calls++), null);
        }
        Check(r.Current == null, "depth overflow masks all");
        for (int i = frames.Count - 1; i >= 0; i--) r.Finish(frames[i], null);
        r.Finish(root, null); Check(calls == 0, "mixed depth failure");
        root = r.Enter(new object()); Manual(); r.Finish(root, null); Check(calls == 1, "independent manual recovers with same identity");
    }
    private static void Pressure()
    {
        double now = 0; var r = new ManualCommandRouting(() => now); int calls = 0;
        void Signal(int id)
        { var f = r.Enter(new object()); r.Complete(ObserveSignal(r, 1, 1, "s" + id / 128, "w", id, "t", RoutingObservationKind.KillCredit, now, () => calls++, "account" + id / 128), null); r.Finish(f, null); }
        for (int i = 0; i < 4096; i++) Signal(i);
        Signal(4096); Check(calls == 4096 && r.SignalReplayCount == 4096 && r.SignalReplayNodes == 4096, "capacity rejects, no eviction");
        Check(r.LastRejection == ObservationRejection.ReplayCapacity, "full cache is not a duplicate");
        Signal(0); Check(r.LastRejection == ObservationRejection.Duplicate, "duplicate wins even at capacity");
        var manual = r.Enter(new object()); r.Complete(r.Observe(1, 1, "s", "w", 1, "!dc daily", now, () => calls++), null); r.Finish(manual, null); Check(calls == 4097, "signal pressure cannot steal manual capacity");
        now = 60; Signal(4096); Check(calls == 4097, "TTL boundary retained");
        now = 60.001; Signal(4096); Check(r.SignalReplayCount >= 4033 && r.SignalReplayNodes == r.SignalReplayCount && calls == 4098, "at most 32 oldest inspected per admission/dispatch");
        r.Reset(); Check(r.SignalReplayCount == 0 && r.SignalReplayNodes == 0, "bounded queues reset");
        for (int i = 0; i < 256; i++) { manual = r.Enter(new object()); r.Complete(r.Observe(1, 1, "s", "w", i, "!dc daily", now, () => { }), null); r.Finish(manual, null); }
        Signal(1); Check(calls == 4099, "manual capacity cannot steal signals");
    }

    private static void AdmissionPolicy()
    {
        var policy = new ObservationAdmission(); var alice = new ObservationPrincipal("Alice", "socket1", false);
        for (int i = 0; i < 128; i++) Check(policy.TryAdmit(alice, 0, out _), "128 exact allowance");
        Check(!policy.TryAdmit(alice, 0, out var reason) && reason == ObservationRejection.PrincipalQuota, "129th rejected");
        Check(!policy.TryAdmit(new("Alice", "socket2", false), 0, out reason) && reason == ObservationRejection.PrincipalQuota, "reconnect retains account tombstone");
        Check(!policy.TryAdmit(new("Other", "socket1", false), 0, out reason) && reason == ObservationRejection.PrincipalQuota, "connection is independently bounded");
        Check(policy.RetainedAccounts == 1 && policy.RetainedPrincipals == 1 && policy.Nodes == 128, "failed quota checks retain no new identity or FIFO node");
        Check(policy.TryAdmit(new("Honest1", "honest1", false), 0, out _) && policy.TryAdmit(new("Honest2", "honest2", false), 0, out _), "two honest peers survive flood");
        for (int i = 0; i < 128; i++) Check(policy.TryAdmit(new("server", "epoch", true), 0, out _), "local separate allowance");
        Check(!policy.TryAdmit(new("server", "replacement", true), 0, out reason) && reason == ObservationRejection.PrincipalQuota, "local allowance not per epoch token");
        Check(!policy.TryAdmit(alice, 60, out _), "sixty seconds inclusive");
        int before = policy.Nodes;
        Check(policy.TryAdmit(alice, 60.001, out _) && policy.Nodes == before - 32 + 1, "only 32 expired charges inspected");
        before = policy.Nodes;
        foreach (double invalid in new[] { -1, 0, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            Check(!policy.TryAdmit(alice, invalid, out reason) && reason == ObservationRejection.Stale && policy.Nodes == before, "invalid/backward clock never releases tombstones");
        policy.Reset();
        for (int i = 0; i < 128; i++) Check(policy.TryAdmit(new("account" + i, "connection" + i, false), 0, out _), "128 retained identities");
        Check(!policy.TryAdmit(new("129", "129", false), 0, out reason) && reason == ObservationRejection.PrincipalCapacity, "129th retained principal refused");
        Check(!policy.TryAdmit(new("account0", "connection1", false), 0, out reason) && reason == ObservationRejection.PrincipalCapacity, "new pair cannot bypass principal capacity by reusing both indexes");
        Check(policy.TryAdmit(new("129", "129", false), 61, out _) && policy.RetainedAccounts == 97 && policy.RetainedConnections == 97, "expired disconnected tombstones reclaimed in bounded steps");
        policy.Reset();
        for (int i = 0; i < 128; i++) Check(policy.TryAdmit(new("same", "churn" + i, false), 0, out _), "connection churn retains independent tombstones");
        Check(policy.RetainedConnections == 128 && policy.RetainedAccounts == 1 && policy.RetainedPrincipals == 128 && policy.Nodes == 128, "all indexes bounded by retained charge state");
        var raw = new ObservationAdmission(7);
        for (int i = 0; i < 128; i++) Check(raw.TryAdmit(alice, 0, out _), "raw independent allowance");
        Check(!raw.TryAdmit(alice, 7, out _) && raw.TryAdmit(alice, 7.001, out _), "raw seven-second boundary");
        var bounded = new ObservationAdmission();
        for (int account = 0; account < 128; account++)
            for (int observation = 0; observation < 128; observation++)
                Check(bounded.TryAdmit(new("account" + account, "connection" + account, false), 0, out _), "bounded full policy fixture");
        for (int i = 0; i < 128; i++) Check(bounded.TryAdmit(new("server", "epoch", true), 0, out _), "local quota outside remote principal slots");
        Check(bounded.Nodes == 16512 && bounded.RetainedPrincipals == 128 && bounded.RetainedAccounts == 128 && bounded.RetainedConnections == 128, "all FIFO/index state has a fixed worst-case bound");
        Check(!bounded.TryAdmit(new("129", "129", false), 0, out reason) && reason == ObservationRejection.PrincipalCapacity && bounded.Nodes == 16512, "full principal table never evicts live charges");
    }

    private static void AdmissionRoots()
    {
        double now = 0; var r = new ManualCommandRouting(() => now); int calls = 0;
        void Attempt(int id, Exception? failure = null, bool twice = false)
        {
            var root = r.Enter(new object());
            for (int i = 0; i < (twice ? 2 : 1); i++)
                r.Complete(ObserveSignal(r, 1, 1, "socket", "world", id, "Bob", RoutingObservationKind.KillCredit, now, () => calls++), null);
            r.Finish(root, failure);
        }
        for (int i = 0; i < 128; i++) Attempt(i, new Exception());
        Check(calls == 0 && r.SignalReplayCount == 0, "failed scopes reserve nothing");
        for (int i = 0; i < 128; i++) Attempt(i, twice: true);
        Attempt(129); Check(calls == 128 && r.LastRejection == ObservationRejection.PrincipalQuota, "Handle+Route quota charged once");
        var root = r.Enter(new object());
        r.Complete(r.Observe(1, 1, "socket", "world", 1, "!dc daily", now, () => calls++), null); r.Finish(root, null);
        Check(calls == 129, "manual bypasses all signal quotas");
        r.Reset(); Attempt(129); Check(calls == 130, "world replacement resets admission");
        root = r.Enter(new object());
        r.Complete(ObserveSignal(r, 1, 1, "socket", "world", 130, "Bob", RoutingObservationKind.KillCredit, now, () => throw new Exception()), null);
        r.Finish(root, null); Attempt(130);
        Check(calls == 130 && r.LastRejection == ObservationRejection.Duplicate, "callback/queue loss is never replayable");
        now = 1; Attempt(131); now = 0; Attempt(132);
        Check(r.LastRejection == ObservationRejection.Stale, "backward observe cannot reset replay");
        foreach (double invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -1 })
        {
            now = invalid; Attempt(133);
            Check(r.LastRejection == ObservationRejection.Stale && r.SignalReplayCount == 3, "invalid observe clock cannot clear replay or charge quota");
        }
    }

    private static void FairReplay()
    {
        var routes = new ManualCommandRouting(() => 0); int calls = 0;
        for (int i = 0; i < 129; i++)
        {
            var frame = routes.Enter(new object());
            routes.Complete(ObserveSignal(routes, 1, 1, "connection", "world", i, "beneficiary",
                RoutingObservationKind.KillCredit, 0, () => calls++), null);
            routes.Finish(frame, null);
        }
        int beforeReconnect = calls;
        var reconnect = routes.Enter(new object());
        routes.Complete(ObserveSignal(routes, 1, 1, "reconnected", "world", 130, "other-beneficiary",
            RoutingObservationKind.KillCredit, 0, () => calls++), null);
        routes.Finish(reconnect, null);
        Check(beforeReconnect == 128 && calls == 128 && routes.SignalReplayCount == 128,
            $"principal replay policy must cap 128/129 and retain reconnect charge; accepted {beforeReconnect} before/{calls} after reconnect");
    }
    private static void Collector()
    {
        var target = Peer(2, "Bob");
        string token = NativeSignalCollector.Token(ReportedSignalKind.KillCredit, "world", false, "reporter-session", 1, target);
        Check(token.Length <= 256 && token == NativeSignalCollector.Token(ReportedSignalKind.KillCredit, "world", false, "reporter-session", 1, target), "stable token");
        Check(new[] { NativeSignalCollector.Token(ReportedSignalKind.ConsumableUse, "world", false, "reporter-session", 1, target),
            NativeSignalCollector.Token(ReportedSignalKind.KillCredit, "other", false, "reporter-session", 1, target),
            NativeSignalCollector.Token(ReportedSignalKind.KillCredit, "world", true, "reporter-session", 1, target),
            NativeSignalCollector.Token(ReportedSignalKind.KillCredit, "world", false, "other", 1, target),
            NativeSignalCollector.Token(ReportedSignalKind.KillCredit, "world", false, "reporter-session", 2, target),
            NativeSignalCollector.Token(ReportedSignalKind.KillCredit, "world", false, "reporter-session", 1, Peer(2, "Bob", character: 2)) }.All(t => t != token), "all durable key dimensions");
        int attempts = 0, warnings = 0; bool accept = false; var batches = new List<ActivityBatch>();
        using var collector = new NativeSignalCollector(b => { attempts++; batches.Add(b); return accept; }, _ => warnings++);
        var record = new ReportedSignalRecord(ReportedSignalKind.KillCredit, "world", target.Actor!, token, Utc);
        Check(!collector.Observe(record, new ObservationPrincipal("Alice", "reporter-session", false)) && collector.Dropped == 1 && attempts == 1 && warnings == 1, "queue false counted once, never retried");
        accept = true; Check(collector.Observe(record, new ObservationPrincipal("Alice", "reporter-session", false)) && attempts == 2 && batches.Last().Signals.Single() == record && batches.Last().PhysicalDocumentCount == 1 &&
            batches.Last().SignalPrincipal?.AccountId == "Alice" && batches.Last().Signals.Single().Actor.PlayerId == "Bob", "collector carries reporter separately from beneficiary");
        collector.Reset(); Check(collector.Dropped == 1, "reset must not erase cumulative loss counters"); collector.Dispose(); Check(!collector.Observe(record, new ObservationPrincipal("Alice", "reporter-session", false)) && attempts == 2, "disposed seals admission");
        using var throwing = new NativeSignalCollector(_ => throw new InvalidOperationException("private"), _ => throw new Exception());
        Check(!throwing.Observe(record, new ObservationPrincipal("Alice", "reporter-session", false)) && throwing.Dropped == 1, "no throw capture and diagnostics");
        int rejects = 0;
        using var rejecting = new NativeSignalCollector(_ => { rejects++; return false; }, _ => { });
        var routes = new ManualCommandRouting(() => 0);
        for (int i = 0; i < 2; i++)
        {
            var frame = routes.Enter(new object());
            routes.Complete(ObserveSignal(routes, 1, 1, "session", "world", 1, token, RoutingObservationKind.KillCredit, 0,
                () => rejecting.Observe(record, new ObservationPrincipal("Alice", "reporter-session", false))), null);
            routes.Finish(frame, null);
        }
        Check(rejects == 1 && rejecting.Dropped == 1 && rejecting.DroppedFor(ReportedSignalKind.KillCredit) == 1 &&
            rejecting.DroppedFor(ReportedSignalKind.ConsumableUse) == 0, "enqueue false consumes volatile replay once, never retries");
    }

    private static void LifecycleAndDiagnostics()
    {
        var alice = Peer(1, "Alice"); var bob = Peer(2, "Bob");
        NativeSignalPeer Current(NativeSignalPeer p, string? account = null, string? session = null, uint? id = null,
            object? transport = null, object? socket = null) => new(p.Uid, (p.Uid, id ?? p.Character.Id),
            new ActivityActor(account ?? p.Actor!.PlayerId, "renamed", session ?? p.Actor!.ConnectionToken, Utc.AddSeconds(1)),
            transport ?? p.Transport, socket ?? p.Socket, true);
        bool Valid(string world = "world", long generation = 1, NativeSignalPeer? sender = null, NativeSignalPeer? target = null) =>
            NativeSignalCollector.IsCurrent("world", 1, alice, bob, world, generation, sender ?? Current(alice), target ?? Current(bob));
        Check(Valid(), "unchanged current connection remains valid despite display rename");
        Check(!Valid(world: "replacement") && !Valid(generation: 2), "world change or away-and-back generation rejects");
        Check(!Valid(sender: Current(alice, session: "new")) && !Valid(target: Current(bob, session: "new")), "both sessions revalidated");
        Check(!Valid(sender: Current(alice, id: 2)) && !Valid(target: Current(bob, id: 2)), "both full characters revalidated");
        Check(!Valid(sender: Current(alice, transport: new object())) && !Valid(target: Current(bob, socket: new object())) &&
            !Valid(target: Current(bob, account: "replacement")), "actual transport, target socket and stable account revalidated");
        Check(NativeSignalCollector.IsCurrent("world", 1, null, bob, "world", 1, null, Current(bob)), "local has no fabricated sender");
        Check(bob.Actor!.PlayerName == "Bob" && bob.Actor.NameObservedUtc == Utc, "captured UTC actor immutable");
        var logs = new List<string>(); double seconds = 0;
        ManualReportDiagnostics.Current = new ManualReportDiagnostics(logs.Add, () => seconds);
        try
        {
            ManualReportDiagnostics.NativeCapability(true, 4, true);
            for (int i = 0; i < 100; i++)
                foreach (NativeSignalProbe code in Enum.GetValues<NativeSignalProbe>()) ManualReportDiagnostics.SignalProbe(code);
            for (int i = 0; i < 8; i++) ManualReportDiagnostics.Emit(ManualReportStage.Query, ManualReportReason.Completed);
            Check(logs.Count == 16, "signal/capability logs share eight early + eight progress records");
            seconds = 60; ManualReportDiagnostics.Poll();
            Check(logs.Count == 18 && logs.Last().Contains("stage=NativeSignal reason=Summary") &&
                logs.Last().Contains("Decoded=100") && logs.Last().Contains("Dropped=100"), "fixed counters coalesced");
            Check(logs.All(line => !line.Contains("Alice") && !line.Contains("Bob") && !line.Contains("session") && !line.Contains("http")), "no identifiers or payload in diagnostics");
        }
        finally { ManualReportDiagnostics.Current = null; }
    }
}
