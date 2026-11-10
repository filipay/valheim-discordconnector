using System.Reflection;
using DiscordConnector;
using DiscordConnector.Leaderboards;
using DiscordConnector.Patches;
using DiscordConnector.Records;
using HarmonyLib;
using LiteDB;

internal static partial class Program
{
    private const string PluginOwner = "nwesterhausen.DiscordConnector";
    private static void Call(object instance, string method, params object[] args)
    {
        try { instance.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(instance, args); }
        catch (TargetInvocationException error) { throw error.InnerException!; }
    }
    private static T Field<T>(object instance, string field) => (T)instance.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance)!;
    private static void Put(object instance, string field, object? value) => instance.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(instance, value);
    private static void Session(string world, string epoch) => NativeSignalPatches.SetSession(world, epoch, "test");
    private static void OtherPrefix() { }
    private static Exception? NpsPassThroughFinalizer(Exception? __exception) => __exception;
    private static Patch OwnHook(Patches patches, Type type, string name) => patches.Prefixes.Concat(patches.Postfixes)
        .Concat(patches.Finalizers).Single(p => p.PatchMethod == type.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static));
    private static void InventoryChecks()
    {
        Test("complete production-declared own inventory permits reported consumable and kill collection", () =>
        {
            flags = new(consumableUses: true, reportedKillCredits: true);
            Harmony.Inventory.Clear();
            Harmony.CreateAndPatchAll(typeof(DiscordConnectorPlugin).Assembly, "test");
            Session("50", "full-production");
            var receive = Harmony.Inventory[typeof(ZRoutedRpc).GetMethod(nameof(ZRoutedRpc.RPC_RoutedRPC))!];
            Assert(receive.Prefixes.Count == 3 && receive.Postfixes.Count == 2 && receive.Finalizers.Count == 3,
                "fixture omitted production receive siblings");
            Assert(OwnHook(receive, typeof(ActivityPatches.Routed), "Postfix") != null &&
                OwnHook(receive, typeof(ActivityPatches.EmoteData), "Prefix") != null, "fixture omitted actual method identities");
            foreach (string method in new[] { nameof(ZRoutedRpc.RouteRPC), nameof(ZRoutedRpc.HandleRoutedRPC) })
            {
                var decoded = Harmony.Inventory[typeof(ZRoutedRpc).GetMethod(method)!];
                int expected = method == nameof(ZRoutedRpc.HandleRoutedRPC) ? 3 : 2;
                Assert(decoded.Prefixes.Count == expected && decoded.Finalizers.Count == expected &&
                    OwnHook(decoded, typeof(ActivityPatches.ManualDecodedChat), "Finalizer") != null, "fixture omitted production decoded-chat siblings");
            }
            Assert(Harmony.Inventory.Values.Sum(p => p.Prefixes.Count + p.Postfixes.Count + p.Finalizers.Count) == 20,
                "production-declared inventory does not have all twenty hooks");
            Decoded(Data("SetTrigger", Payload("eat"), 1));
            var kill = Data("RPC_RegisterKill", KillPayload(), 2); kill.m_targetZDO = ZDOID.None; kill.m_targetPeerID = 10;
            Decoded(kill);
            Assert(SignalCount == 2, "normal full own inventory disabled reported signals");
        });
        foreach (string mutation in new[] { "missing", "extra", "duplicate", "wrong method", "priority", "ordering" })
            Test("complete own inventory rejects every " + mutation + " hook", () =>
            {
                var expected = Harmony.Inventory.SelectMany(target => new[] {
                    (Stage: "Prefix", Hooks: target.Value.Prefixes), (Stage: "Postfix", Hooks: target.Value.Postfixes),
                    (Stage: "Finalizer", Hooks: target.Value.Finalizers) }.SelectMany(stage => stage.Hooks.Select(p =>
                        (Target: target.Key, stage.Stage, Method: p.PatchMethod)))).ToArray();
                Assert(expected.Length == 20, "tamper matrix omitted declared own hooks");
                var logs = new List<string>(); ManualReportDiagnostics.Current = new(logs.Add, () => 100);
                int inspected = 0;
                foreach (var entry in expected)
                {
                    InstallHooks();
                    var installed = Harmony.Inventory[entry.Target];
                    var hooks = entry.Stage == "Prefix" ? installed.Prefixes : entry.Stage == "Postfix" ? installed.Postfixes : installed.Finalizers;
                    var patch = hooks.Single(p => p.PatchMethod == entry.Method);
                    switch (mutation)
                    {
                        case "missing": hooks.Remove(patch); break;
                        case "extra": hooks.Add(new Patch { owner = "test", PatchMethod = typeof(Program).GetMethod(nameof(OtherPrefix), BindingFlags.NonPublic | BindingFlags.Static)! }); break;
                        case "duplicate": hooks.Add(patch); break;
                        case "wrong method": patch.PatchMethod = typeof(Program).GetMethod(nameof(OtherPrefix), BindingFlags.NonPublic | BindingFlags.Static)!; break;
                        case "priority": patch.priority = patch.priority == Priority.First ? Priority.Last : Priority.First; break;
                        case "ordering": patch.after = new[] { "test" }; break;
                    }
                    logs.Clear(); Session("50", "tamper-" + inspected++);
                    Assert(!(bool)typeof(NativeSignalPatches).GetField("available", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!,
                        $"accepted {mutation} {entry.Target.Name}/{entry.Method.DeclaringType!.Name}.{entry.Method.Name}");
                    string detail = logs.Single(line => line.Contains(" method="));
                    string condition = mutation == "wrong method" ? "missing-own-hook" : mutation == "priority" || mutation == "ordering" ? "own-hook-order" : "own-hook-count";
                    Assert(detail.Contains($"method=ZRoutedRpc.{entry.Target.Name} condition={condition}") && detail.Contains("foreignOwners=[]"),
                        "own inventory diagnostic omitted target, first failure or owner inventory: " + detail);
                    if (condition == "own-hook-order")
                        Assert(detail.Contains("priority=") && detail.Contains("expectedPriority=") && detail.Contains("expectedBefore=[") && detail.Contains("expectedAfter=["),
                            "own ordering mismatch omitted actual/expected constraints");
                }
                Assert(inspected == 20, "not every expected method identity was checked");
            });
        foreach (string unsafePatch in new[] { "unknown prefix", "before owner", "tied priority", "postfix", "finalizer", "transpiler" })
            Test("full inventory rejects foreign " + unsafePatch + " on every target", () =>
            {
                var logs = new List<string>(); ManualReportDiagnostics.Current = new(logs.Add, () => 100);
                int inspected = 0;
                foreach (string target in new[] { nameof(ZRoutedRpc.RPC_RoutedRPC), nameof(ZRoutedRpc.RouteRPC), nameof(ZRoutedRpc.HandleRoutedRPC), nameof(ZRoutedRpc.InvokeRoutedRPC) })
                {
                    InstallHooks();
                    var installed = Harmony.Inventory[typeof(ZRoutedRpc).GetMethod(target)!];
                    var patch = new Patch { owner = "MidnightsFX.NetworkPerformanceSystem", priority = 600,
                        PatchMethod = typeof(Program).GetMethod(nameof(OtherPrefix), BindingFlags.NonPublic | BindingFlags.Static)! };
                    switch (unsafePatch)
                    {
                        case "unknown prefix": patch.owner = "unknown"; installed.Prefixes.Add(patch); break;
                        case "before owner": patch.before = new[] { "test" }; installed.Prefixes.Add(patch); break;
                        case "tied priority": patch.priority = Priority.First; installed.Prefixes.Add(patch); break;
                        case "postfix": installed.Postfixes.Add(patch); break;
                        case "finalizer": patch.owner = "unknown"; installed.Finalizers.Add(patch); break;
                        case "transpiler": installed.Transpilers.Add(patch); break;
                    }
                    logs.Clear(); Session("50", "unsafe-" + inspected++);
                    Assert(!(bool)typeof(NativeSignalPatches).GetField("available", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!,
                        $"accepted foreign {unsafePatch} on {target}");
                    string condition = unsafePatch switch {
                        "unknown prefix" => "foreign-owner", "postfix" => "foreign-postfix", "finalizer" => "foreign-finalizer", "transpiler" => "transpiler-present",
                        _ when target == nameof(ZRoutedRpc.InvokeRoutedRPC) => "foreign-local-hook",
                        "before owner" => "foreign-order", _ => "foreign-priority"
                    };
                    string detail = logs.Single(line => line.Contains(" method="));
                    Assert(detail.Contains($"method=ZRoutedRpc.{target} condition={condition}") && detail.Contains($"foreignOwners=[{patch.owner}]"),
                        "foreign inventory diagnostic omitted target, first failure or owner inventory: " + detail);
                    if (unsafePatch == "postfix" || unsafePatch == "finalizer" || unsafePatch == "transpiler")
                        Assert(detail.Contains($"owners=[{patch.owner}]"), "failing patch stage did not identify its owners");
                    if (condition == "foreign-order" || condition == "foreign-priority")
                        Assert(detail.Contains("priority=") && detail.Contains("before=[") && detail.Contains("after=[") && detail.Contains("expected"),
                            "foreign ordering mismatch omitted actual/expected constraints");
                }
            });
        Test("pinned NPS HandleRoutedRPC prefix and pass-through finalizer permit signals without hiding failures", () =>
        {
            flags = new(consumableUses: true);
            var patches = Harmony.Inventory[typeof(ZRoutedRpc).GetMethod(nameof(ZRoutedRpc.HandleRoutedRPC))!];
            patches.Prefixes.Add(new Patch { owner = "MidnightsFX.NetworkPerformanceSystem", priority = 400,
                PatchMethod = typeof(Program).GetMethod(nameof(OtherPrefix), BindingFlags.NonPublic | BindingFlags.Static)! });
            patches.Finalizers.Add(new Patch { owner = "MidnightsFX.NetworkPerformanceSystem", priority = 400,
                PatchMethod = typeof(Program).GetMethod(nameof(NpsPassThroughFinalizer), BindingFlags.NonPublic | BindingFlags.Static)! });
            Session("50", "nps-pass-through");
            Assert((bool)typeof(NativeSignalPatches).GetField("available", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!,
                "exception-transparent NPS finalizer disabled capability");
            var error = new IOException("handler failure");
            Assert(ReferenceEquals(error, NpsPassThroughFinalizer(error)), "NPS fixture changed the exception");
            Decoded(Data("SetTrigger", Payload("eat"), 1), error: NpsPassThroughFinalizer(error));
            Assert(SignalCount == 0, "exceptional routed invocation released observations");
            Decoded(Data("SetTrigger", Payload("eat"), 2));
            Assert(SignalCount == 1, "successful routed invocation did not recover");
        });
        foreach (string invalid in new[] { "tied priority", "after own", "local target" })
            Test("pinned NPS finalizer rejects " + invalid, () =>
            {
                var target = invalid == "local target" ? nameof(ZRoutedRpc.InvokeRoutedRPC) : nameof(ZRoutedRpc.HandleRoutedRPC);
                Harmony.Inventory[typeof(ZRoutedRpc).GetMethod(target)!].Finalizers.Add(new Patch
                {
                    owner = "MidnightsFX.NetworkPerformanceSystem", priority = invalid == "tied priority" ? Priority.First : 400,
                    after = invalid == "after own" ? new[] { "test" } : Array.Empty<string>(),
                    PatchMethod = typeof(Program).GetMethod(nameof(NpsPassThroughFinalizer), BindingFlags.NonPublic | BindingFlags.Static)!
                });
                Session("50", "unsafe-nps-finalizer"); flags = new(consumableUses: true);
                Decoded(Data("SetTrigger", Payload("eat")));
                Assert(!(bool)typeof(NativeSignalPatches).GetField("available", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)! && SignalCount == 0,
                    "unsafe NPS finalizer was accepted");
            });
        Test("startup inventory logs all foreign owners and one first failure per target despite exhausted diagnostic budget", () =>
        {
            var logs = new List<string>(); ManualReportDiagnostics.Current = new(logs.Add, () => 100);
            for (int i = 0; i < 20; i++) ManualReportDiagnostics.Emit(ManualReportStage.NativeSignal, ManualReportReason.Failed);
            foreach (var installed in Harmony.Inventory.Values)
            {
                installed.Transpilers.Add(new Patch { owner = "transpiler-owner" });
                installed.Prefixes.Add(new Patch { owner = "prefix-owner" });
                installed.Postfixes.Add(new Patch { owner = "postfix-owner" });
                installed.Finalizers.Add(new Patch { owner = "finalizer-owner" });
            }
            logs.Clear(); int inspections = Harmony.Inspections;
            Session("50", "startup-details");
            Assert(logs.Count == 4 && Harmony.Inspections == inspections + 4, "startup details were suppressed, duplicated or re-read patch inventory");
            Assert(logs.All(line => line.StartsWith("ManualReport stage=NativeCapability reason=Unsupported method=ZRoutedRpc.") &&
                line.Contains("condition=transpiler-present owners=[transpiler-owner]") &&
                line.EndsWith("foreignOwners=[finalizer-owner,postfix-owner,prefix-owner,transpiler-owner] execution=unverified")),
                "first failure or complete foreign owner set missing");
            Session("50", "startup-details");
            Assert(logs.Count == 4 && Harmony.Inspections == inspections + 4, "same session repeated startup details");
        });
        Test("complete expected own-hook sets accept registration enumeration order changes", () =>
        {
            foreach (var patches in Harmony.Inventory.Values) { patches.Prefixes.Reverse(); patches.Postfixes.Reverse(); patches.Finalizers.Reverse(); }
            Session("50", "reordered-registration"); flags = new(consumableUses: true);
            Decoded(Data("SetTrigger", Payload("eat")));
            Assert(SignalCount == 1, "method set validation incorrectly depended on enumeration order");
        });
        Test("pinned-owner prefix is still unsupported on server-local invocation", () =>
        {
            Harmony.Inventory[typeof(ZRoutedRpc).GetMethod(nameof(ZRoutedRpc.InvokeRoutedRPC))!].Prefixes.Add(new Patch
            {
                owner = "MidnightsFX.NetworkPerformanceSystem", priority = 600,
                PatchMethod = typeof(Program).GetMethod(nameof(OtherPrefix), BindingFlags.NonPublic | BindingFlags.Static)!
            });
            Session("50", "unsupported-local-owner"); flags = new(consumableUses: true);
            Decoded(Data("SetTrigger", Payload("eat")));
            Assert(SignalCount == 0, "foreign local-invocation replacement was accepted");
        });
    }
    private static void WithPlugin(Action<DiscordConnectorPlugin, ActivityStore, CombatReports, ManualLeaderboardReports> check)
    {
        runtime?.Dispose(); runtime = null;
        string directory = Path.Combine(Path.GetTempPath(), "vdc-task4-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, DiscordConnectorPlugin.LegacyConfigPath));
        BepInEx.Paths.ConfigPath = directory;
        var plugin = new DiscordConnectorPlugin();
        var store = DiscordConnectorPlugin.StaticDatabase.Activity;
        Assert(ReferenceEquals(store.Loss, ObservationLoss.Process), "production database replaced the process loss owner");
        Put(store, "<Loss>k__BackingField", new ObservationLoss(_ => { }));
        InstallHooks(PluginOwner);
        Field<BepInEx.Configuration.ConfigEntry<bool>>(DiscordConnectorPlugin.StaticCombatConfig, "dailyEnabled").Value = true;
        Call(plugin, "Awake");
        var automatic = Field<CombatReports>(plugin, "_combatReports");
        var manual = Field<ManualLeaderboardReports>(plugin, "_manualReports");
        try { check(plugin, store, automatic, manual); }
        finally
        {
            manual.DisposeFailure = null; DiscordConnectorPlugin.StaticLogger.Throw = false;
            try { Call(plugin, "OnDestroy"); } catch { }
            try { DiscordConnectorPlugin.StaticDatabase.BeginShutdown().GetAwaiter().GetResult(); } catch { }
            Field<Task?>(plugin, "_observationShutdown")?.GetAwaiter().GetResult();
            ActivityRuntime.Current?.Dispose();
            Directory.Delete(directory, true);
        }
    }
    private static void LifecycleChecks()
    {
        Test("actual Plugin polls retention off-thread on bounded maintenance and drains admitted work", () => WithPlugin((plugin, store, automatic, manual) =>
        {
            var db = Field<LiteDatabase>(DiscordConnectorPlugin.StaticDatabase, "db");
            DateTime utc = new(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);
            var signals = new ReportedSignalStore(db);
            var actor = new ActivityActor("actor", "Recent", "connection", utc);
            signals.Insert(new ReportedSignalRecord(ReportedSignalKind.KillCredit, "50", actor, "expired", utc.AddDays(-91)));
            signals.Insert(new ReportedSignalRecord(ReportedSignalKind.KillCredit, "50", actor, "cutoff", utc.AddDays(-90)));
            var details = db.GetCollection("reported_kill_credits_v1");
            var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); Put(store, "tail", blocked.Task);
            var acknowledgment = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); Put(plugin, "_lossMaintenance", acknowledgment.Task);
            ZNet.instance.WorldUid = 0;
            try
            {
                plugin.UpdateObservationState(utc, double.PositiveInfinity);
                plugin.UpdateObservationState(utc, double.NaN);
                Assert(store.PendingWork.Batches == 0, "invalid monotonic clocks admitted maintenance");
                plugin.UpdateObservationState(utc, 100);
                Assert(store.PendingWork.Batches == 1, "Plugin did not admit retention independent of world lookup and pending diagnostic acknowledgment");
                Assert(details.Count() == 2, "Plugin performed synchronous database maintenance");
                plugin.UpdateObservationState(utc.AddDays(1), 110);
                Assert(store.PendingWork.Batches == 1, "blocked retention was admitted twice");
                blocked.SetResult();
                store.QueryAsync("50", utc.AddDays(-1), utc, new()).GetAwaiter().GetResult();
                Assert(details.Count() == 1, "queued time changed before execution");
                plugin.UpdateObservationState(utc.AddDays(1), 150);
                store.QueryAsync("50", utc.AddDays(-1), utc, new()).GetAwaiter().GetResult();
                Assert(details.Count() == 1, "Plugin bypassed retention cadence");
                acknowledgment.SetResult();
                var finalBlock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); Put(store, "tail", finalBlock.Task);
                try
                {
                    plugin.UpdateObservationState(utc.AddDays(1), 160);
                    Assert(store.PendingWork.Batches == 1, "next bounded opportunity missed");
                    Task shutdown = plugin.BeginObservationShutdown();
                    Assert(!shutdown.IsCompleted && store.PendingWork.Batches == 1, "shutdown did not join or added a purge");
                    finalBlock.SetResult(); shutdown.GetAwaiter().GetResult();
                    string path = Field<string>(DiscordConnectorPlugin.StaticDatabase, "DbPath");
                    using var reopened = new LiteDatabase(path);
                    Assert(reopened.GetCollection("reported_kill_credits_v1").Count() == 0,
                        "clean shutdown did not persist admitted retention");
                }
                finally { finalBlock.TrySetResult(); }
            }
            finally { blocked.TrySetResult(); acknowledgment.TrySetResult(); }
        }));
        Test("session inventory fails closed, recovers next session and invalidates unsupported replacement", () =>
        {
            flags = new(consumableUses: true);
            NativeSignalPatches.Configure(new(batch => { batches.Add(batch); return true; }, _ => { }, loss), () => flags);
            Harmony.Inventory.Clear(); Harmony.Inspections = 0;
            Session("50", "first"); Decoded(Data("SetTrigger", Payload("eat")));
            Assert(SignalCount == 0, "unsupported first session collected");
            InstallHooks(); Session("50", "second"); Decoded(Data("SetTrigger", Payload("eat"), 2));
            Assert(SignalCount == 1, "supported next session did not inspect/recover");
            int inspected = Harmony.Inspections;
            Session("50", "second"); Assert(Harmony.Inspections == inspected, "same session reflected again");
            Harmony.Inventory.Clear(); Session("50", "third"); Decoded(Data("SetTrigger", Payload("eat"), 3));
            Assert(SignalCount == 1, "unsupported later session reused capability");
        });
        Test("temporary world loss preserves native replay and stable-account quota", () =>
        {
            flags = new(consumableUses: true);
            Decoded(Data("SetTrigger", Payload("eat"), 1));
            NativeSignalPatches.SetWorld(""); NativeSignalPatches.SetWorld("50");
            Decoded(Data("SetTrigger", Payload("eat"), 1));
            Assert(SignalCount == 1 && ActivityPatches.ManualRoutes.SignalReplayCount == 1, "world lookup loss minted replay capacity");
        });
        Test("temporary runtime world loss preserves raw replay tombstones", () =>
        {
            flags = new(dodgeRolls: true);
            runtime!.Update(DateTime.UtcNow, 100);
            Raw(Data("SetTrigger", Payload("dodge"), 1));
            Assert(RawReplay == 1, "setup missing raw replay");
            ZNet.instance.WorldUid = 0; runtime.Update(DateTime.UtcNow, 100);
            ZNet.instance.WorldUid = 50; runtime.Update(DateTime.UtcNow, 100);
            Raw(Data("SetTrigger", Payload("dodge"), 1));
            Assert(RawReplay == 1 && ActivityPatches.RawRejection == ObservationRejection.Duplicate, "transient runtime lookup cleared replay");
        });
        Test("actual Plugin manual failure does not dispose or starve automatic polling/tick", () => WithPlugin((plugin, store, automatic, manual) =>
        {
            manual.Failure = new IOException("private path and token");
            Call(plugin, "Update");
            Assert(automatic.Disposals == 0 && automatic.Polls == 1 && automatic.Ticks == 1, "manual failure disabled/starved automatic scheduler");
            Assert(DiscordConnectorPlugin.StaticLogger.Warnings.Any(x => x.Contains("stage=manual-poll") && !x.Contains("private path")), "manual failure stage/redaction missing");
        }));
        Test("actual Plugin cleanup failure and throwing logger still seal admission", () => WithPlugin((plugin, store, automatic, manual) =>
        {
            manual.DisposeFailure = new IOException("private cleanup failure");
            DiscordConnectorPlugin.StaticLogger.Throw = true;
            try { Call(plugin, "OnDestroy"); } catch { }
            Assert(!store.Enqueue(new ActivityBatch()), "cleanup failure prevented admission sealing");
            Assert(automatic.Disposals == 1, "earlier cleanup failure prevented automatic disposal");
        }));
        Test("actual Plugin stage seams isolate automatic poll and tick failures with bounded redaction", () => WithPlugin((plugin, store, automatic, manual) =>
        {
            DateTime utc = DateTime.UtcNow;
            automatic.PollFailure = new IOException("private token /path");
            automatic.TickFailure = new IOException("private token /path");
            plugin.UpdateReportState(utc, 100, "50");
            Assert(manual.Polls == 1 && automatic.Polls == 1 && automatic.Ticks == 1 && automatic.Disposals == 0, "automatic stages were not independent");
            var warnings = DiscordConnectorPlugin.StaticLogger.Warnings;
            Assert(warnings.Any(x => x.Contains("stage=automatic-poll")) && warnings.Any(x => x.Contains("stage=automatic-tick")), "wrong failing stage logged");
            for (int i = 0; i < 100; i++) plugin.UpdateReportState(utc, 100, "50");
            Assert(warnings.Count(x => x.StartsWith("Observation failure")) == 4 && !warnings.Any(x => x.Contains("private token") || x.Contains("/path")), "stage logs unbounded or leaked messages");
            Assert(automatic.Ticks == 1, "automatic tick failure retried before next calendar check");
        }));
        Test("actual Plugin inventories first usable/new session, never Awake or each frame", () => WithPlugin((plugin, store, automatic, manual) =>
        {
            var observed = Field<object>(plugin, "_observedConfig");
            Field<BepInEx.Configuration.ConfigEntry<bool>>(observed, "consumableUses").Value = true;
            plugin.Config.Changed();
            Harmony.Inventory.Clear(); Harmony.Inspections = 0;
            ZNet.instance.WorldUid = 0;
            plugin.UpdateObservationState(DateTime.UtcNow, 100);
            Assert(Harmony.Inspections == 0, "unusable world inspected hooks");
            ZNet.instance.WorldUid = 50;
            plugin.UpdateObservationState(DateTime.UtcNow, 100.1);
            Decoded(Data("SetTrigger", Payload("eat"), 1));
            Assert(ActivityPatches.ManualRoutes.SignalReplayCount == 0 && Harmony.Inspections == 4, "unsupported initial session collected or skipped inventory");
            InstallHooks(PluginOwner);
            plugin.UpdateObservationState(DateTime.UtcNow, 100.2);
            Assert(Harmony.Inspections == 4, "mid-session hot patch caused automatic reflection");
            ZNet.World = new(); plugin.UpdateObservationState(DateTime.UtcNow, 100.3);
            Decoded(Data("SetTrigger", Payload("eat"), 2));
            store.QueryAsync("50", DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1), new(consumableUses: true)).GetAwaiter().GetResult();
            Assert(ActivityPatches.ManualRoutes.SignalReplayCount == 1 && Harmony.Inspections == 8, "supported replacement session did not recover");
            Harmony.Inventory.Clear(); ZNet.World = new();
            plugin.UpdateObservationState(DateTime.UtcNow, 100.4);
            Decoded(Data("SetTrigger", Payload("eat"), 3));
            Assert(ActivityPatches.ManualRoutes.SignalReplayCount == 0 && Harmony.Inspections == 12, "unsupported later session reused capability");
        }));
        foreach (string invalid in new[] { "unknown prefix", "transpiler", "own priority", "own constraints", "extra own prefix", "foreign postfix", "NPS before own", "NPS tied priority" })
            Test("session inventory rejects " + invalid, () =>
            {
                flags = new(consumableUses: true);
                var patches = Harmony.Inventory[typeof(ZRoutedRpc).GetMethod(nameof(ZRoutedRpc.RPC_RoutedRPC))!];
                var other = new Patch { owner = "unknown", priority = 600, PatchMethod = typeof(Program).GetMethod(nameof(OtherPrefix), BindingFlags.Static | BindingFlags.NonPublic)! };
                switch (invalid)
                {
                    case "unknown prefix": patches.Prefixes.Add(other); break;
                    case "transpiler": patches.Transpilers.Add(other); break;
                    case "own priority": OwnHook(patches, typeof(ActivityPatches.ManualReceive), "Prefix").priority--; break;
                    case "own constraints": OwnHook(patches, typeof(ActivityPatches.ManualReceive), "Prefix").before = Array.Empty<string>(); break;
                    case "extra own prefix": other.owner = "test"; patches.Prefixes.Add(other); break;
                    case "foreign postfix": patches.Postfixes.Add(other); break;
                    case "NPS before own": other.owner = "MidnightsFX.NetworkPerformanceSystem"; other.before = new[] { "test" }; patches.Prefixes.Add(other); break;
                    case "NPS tied priority": other.owner = "MidnightsFX.NetworkPerformanceSystem"; other.priority = Priority.First; patches.Prefixes.Add(other); break;
                }
                Session("50", "invalid-inventory"); Decoded(Data("SetTrigger", Payload("eat"), 1));
                Assert(SignalCount == 0, "unsupported patch inventory collected");
            });
        Test("supported pinned-owner NPS receive/route prefixes preserve ordering contract", () =>
        {
            flags = new(consumableUses: true);
            foreach (string method in new[] { nameof(ZRoutedRpc.RPC_RoutedRPC), nameof(ZRoutedRpc.RouteRPC), nameof(ZRoutedRpc.HandleRoutedRPC) })
                Harmony.Inventory[typeof(ZRoutedRpc).GetMethod(method)!].Prefixes.Add(new Patch
                {
                    owner = "MidnightsFX.NetworkPerformanceSystem", priority = 600,
                    PatchMethod = typeof(Program).GetMethod(nameof(OtherPrefix), BindingFlags.Static | BindingFlags.NonPublic)!
                });
            Session("50", "pinned-owner"); Decoded(Data("SetTrigger", Payload("eat")));
            Assert(SignalCount == 1, "declared supported NPS ordering became unavailable");
        });
        Test("native temporary unavailability retains full stable-account allowance and held work is stale", () =>
        {
            flags = new(consumableUses: true);
            for (int i = 0; i < 128; i++) Decoded(Data("SetTrigger", Payload("eat"), i));
            Decoded(Data("SetTrigger", Payload("eat"), 129), () => NativeSignalPatches.SetWorld(""));
            NativeSignalPatches.SetSession("50", "fixture-session", "test");
            Decoded(Data("SetTrigger", Payload("eat"), 130));
            Assert(SignalCount == 128 && ActivityPatches.ManualRoutes.LastRejection == ObservationRejection.PrincipalQuota, "temporary unavailable minted quota or committed stale work");
            var peer = ZNet.instance.Peers[0]; peer.m_socket = new() { Host = "account0" }; peer.m_rpc = new(peer.m_socket);
            Decoded(Data("SetTrigger", Payload("eat"), 131));
            Assert(SignalCount == 128, "reconnect after temporary failure minted quota");
        });
        Test("options transition invalidates staged native/raw work without clearing tombstones", () =>
        {
            flags = new(consumableUses: true, dodgeRolls: true);
            Decoded(Data("SetTrigger", Payload("eat"), 1)); Raw(Data("SetTrigger", Payload("dodge"), 2));
            Decoded(Data("SetTrigger", Payload("eat"), 3), NativeSignalPatches.InvalidateOptions);
            Raw(Data("SetTrigger", Payload("dodge"), 4), _ => NativeSignalPatches.InvalidateOptions());
            Assert(SignalCount == 1 && RawReplay == 1 && ActivityPatches.ManualRoutes.SignalReplayCount == 2, "config transition collected staged work or erased consumed replay");
        });
        Test("actual Plugin world failure skips stale-world work and retains checked session/replay", () => WithPlugin((plugin, store, automatic, manual) =>
        {
            plugin.UpdateObservationState(DateTime.UtcNow, 100);
            string epoch = Field<string>(plugin, "_sessionEpoch");
            int inspected = Harmony.Inspections;
            ZNet.instance.WorldFailure = new IOException("private world");
            plugin.UpdateObservationState(DateTime.UtcNow, 100.1);
            Assert(Field<string>(plugin, "_observationWorld") == "", "failed lookup reused stale world");
            plugin.UpdateReportState(DateTime.UtcNow, 100.1, Field<string>(plugin, "_observationWorld"));
            Assert(manual.Polls == 0 && automatic.Polls == 0, "world-dependent polls ran with stale world");
            ZNet.instance.WorldFailure = null;
            plugin.UpdateObservationState(DateTime.UtcNow, 100.2);
            Assert(Field<string>(plugin, "_sessionEpoch") == epoch && Harmony.Inspections == inspected, "temporary failure became a new session/inventory");
            ZNet.World = new(); plugin.UpdateObservationState(DateTime.UtcNow, 100.3);
            Assert(Field<string>(plugin, "_sessionEpoch") != epoch && Harmony.Inspections == inspected + 4, "same UID/new World lifetime was not inspected");
        }));
        Test("actual Plugin caches effective options without equivalent allocations", () => WithPlugin((plugin, store, automatic, manual) =>
        {
            var snapshot = plugin.GetType().GetMethod("EffectiveOptions", BindingFlags.Instance | BindingFlags.NonPublic)!.CreateDelegate<Func<LeaderboardsOptions>>(plugin);
            var original = snapshot();
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) Assert(ReferenceEquals(snapshot(), original), "equivalent options reallocated");
            Assert(GC.GetAllocatedBytesForCurrentThread() == before, "cached option access allocated in pure adapter fixture");
            plugin.Config.Changed();
            Assert(!ReferenceEquals(snapshot(), original), "config change did not invalidate snapshots");
        }));
        Test("actual Plugin bounds native coverage polling and caches static metric inventory", () => WithPlugin((plugin, store, automatic, manual) =>
        {
            DateTime utc = DateTime.UtcNow;
            plugin.UpdateObservationState(utc, 100);
            int inspected = Harmony.Inspections;
            for (int i = 0; i < 100; i++) plugin.UpdateObservationState(utc, 100.5);
            plugin.Config.Changed(); plugin.UpdateObservationState(utc.AddSeconds(1), 101);
            Assert(Harmony.Inspections == inspected, "same-session coverage/config invalidation reflected again");
            Assert(typeof(NativeSignalPatches).GetField("Metrics", BindingFlags.Static | BindingFlags.NonPublic)!.IsInitOnly, "metric array is not static readonly");
            NativeSignalPatches.UpdateCoverage(utc);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) NativeSignalPatches.UpdateCoverage(utc);
            Assert(GC.GetAllocatedBytesForCurrentThread() == before, "unchanged coverage allocated lists or metric arrays");
        }));
        Test("actual Plugin maintains loss every ten seconds even while world lookup is unavailable", () => WithPlugin((plugin, store, automatic, manual) =>
        {
            store.Loss.Record("50", ReportMetric.ReportedKillCredits, ObservationRejection.QueueFull);
            ZNet.instance.WorldUid = 0;
            plugin.UpdateObservationState(DateTime.UtcNow, 100);
            Field<Task>(plugin, "_lossMaintenance").GetAwaiter().GetResult();
            Assert(store.Loss.UnacknowledgedKeys == 0, "world-independent diagnostics were not acknowledged");
            store.Loss.Record("50", ReportMetric.ReportedKillCredits, ObservationRejection.QueueFull);
            plugin.UpdateObservationState(DateTime.UtcNow, 109.999);
            Assert(store.Loss.UnacknowledgedKeys == 1, "loss maintenance ignored ten-second cadence");
            plugin.UpdateObservationState(DateTime.UtcNow, 110);
            Field<Task>(plugin, "_lossMaintenance").GetAwaiter().GetResult();
            Assert(store.Loss.UnacknowledgedKeys == 0, "next ten-second opportunity missed");
        }));
        Test("actual Plugin final no-op preserves outstanding real loss acknowledgment and newer dirty state", () => WithPlugin((plugin, store, automatic, manual) =>
        {
            var blocked = new TaskCompletionSource(); Put(store, "tail", blocked.Task);
            try
            {
            store.Loss.Record("50", ReportMetric.ReportedKillCredits, ObservationRejection.QueueFull);
            plugin.UpdateObservationState(DateTime.UtcNow, 100);
            Task acknowledgment = Field<Task>(plugin, "_lossMaintenance");
            Assert(!acknowledgment.IsCompleted, "setup did not retain actual acknowledgment bookkeeping");
            store.Loss.Record("50", ReportMetric.ReportedKillCredits, ObservationRejection.QueueFull);
            Assert(store.SubmitLossBeforeSeal(100).IsCompleted, "pending final hook must be a no-op");
            Task shutdown = plugin.BeginObservationShutdown();
            Assert(!shutdown.IsCompleted && ReferenceEquals(acknowledgment, Field<Task>(plugin, "_lossMaintenance")), "shutdown discarded outstanding bookkeeping");
            Assert(ReferenceEquals(shutdown, plugin.BeginObservationShutdown()), "plugin shutdown was not idempotent");
            blocked.SetResult(); shutdown.GetAwaiter().GetResult();
            Assert(acknowledgment.IsCompleted && store.Loss.UnacknowledgedKeys == 1, "old snapshot acknowledgment cleaned a newer total");
            Assert(DiscordConnectorPlugin.StaticLogger.Warnings.Any(x => x.Contains("Observation shutdown state=drained") && x.Contains("lossUnacknowledged=1")), "dirty shutdown evidence missing");
            }
            finally { blocked.TrySetResult(); }
        }));
        Test("actual Plugin final diagnostic failure is drained but remains dirty and known-failed", () => WithPlugin((plugin, store, automatic, manual) =>
        {
            var db = Field<LiteDatabase>(DiscordConnectorPlugin.StaticDatabase, "db");
            var collection = db.GetCollection("server_observation_v2_loss");
            collection.DropIndex("WorldId");
            collection.EnsureIndex("WorldId", true);
            collection.Insert(new BsonDocument { ["_id"] = "conflict", ["WorldId"] = "50" });
            store.Loss.Record("50", ReportMetric.ReportedKillCredits, ObservationRejection.QueueFull);
            plugin.BeginObservationShutdown().GetAwaiter().GetResult();
            Assert(store.Loss.UnacknowledgedKeys == 1, "completed maintenance was mistaken for persisted success");
            Assert(DiscordConnectorPlugin.StaticLogger.Warnings.Any(x => x.Contains("Database shutdown state=drained") && x.Contains("knownFailures=True")), "diagnostic write failure was hidden by drain completion");
        }));
        Test("actual Plugin flush cleanup failure still submits final loss before sealing", () => WithPlugin((plugin, store, automatic, manual) =>
        {
            var capture = Field<ActivityRuntime>(plugin, "_activityRuntime");
            Put(capture, "_explorationPauseReason", (Func<string, CoverageReason?>)(_ => throw new IOException("flush failure")));
            store.Loss.Record("50", ReportMetric.ReportedKillCredits, ObservationRejection.QueueFull);
            plugin.BeginObservationShutdown().GetAwaiter().GetResult();
            Assert(store.Loss.UnacknowledgedKeys == 0 && ActivityRuntime.Current == null, "flush exception prevented final acknowledgment or runtime release");
            Assert(DiscordConnectorPlugin.StaticLogger.Warnings.Any(x => x.Contains("stage=observation-flush")), "wrong cleanup stage logged");
        }));
        Test("actual database shutdown drains admitted success/failure/report jobs, seals and closes only afterward", () => WithPlugin((plugin, store, automatic, manual) =>
        {
            var db = Field<LiteDatabase>(DiscordConnectorPlugin.StaticDatabase, "db");
            db.GetCollection("observed_consumable_uses_v1").DropIndex("WorldId");
            db.GetCollection("observed_consumable_uses_v1").EnsureIndex("WorldId", true);
            var blocked = new TaskCompletionSource(); Put(store, "tail", blocked.Task);
            try
            {
            var actor = new ActivityActor("a", "A", "connection", DateTime.UtcNow);
            ReportedSignalRecord Signal(string token) => new(ReportedSignalKind.ConsumableUse, "50", actor, token, DateTime.UtcNow);
            var principal = new ObservationPrincipal("a", "connection", false);
            Assert(store.Enqueue(new ActivityBatch(signals: new[] { Signal("one") }, signalPrincipal: principal)), "successful candidate not admitted");
            Assert(store.Enqueue(new ActivityBatch(signals: new[] { Signal("two") }, signalPrincipal: principal)), "failing candidate not admitted");
            Task report = store.QueryAsync("50", DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1), new(consumableUses: true));
            Task shutdown = DiscordConnectorPlugin.StaticDatabase.BeginShutdown();
            Assert(ReferenceEquals(shutdown, DiscordConnectorPlugin.StaticDatabase.BeginShutdown()) && !shutdown.IsCompleted, "database shutdown blocked or was not idempotent");
            Assert(!store.Enqueue(new ActivityBatch()) && db.GetCollection("observed_consumable_uses_v1").Count() == 0, "admission open or database closed before drain");
            Assert(DiscordConnectorPlugin.StaticLogger.Warnings.Any(x => x.Contains("state=sealing") && x.Contains("pendingBatches=2") && x.Contains("pendingReports=1")), "fixed pending-work evidence missing");
            blocked.SetResult(); shutdown.GetAwaiter().GetResult(); report.GetAwaiter().GetResult();
            Assert(store.PendingWork == (0, 0), "queues not drained");
            string path = Field<string>(DiscordConnectorPlugin.StaticDatabase, "DbPath");
            using var reopened = new LiteDatabase(path);
            Assert(reopened.GetCollection("observed_consumable_uses_v1").Count() == 1, "successful write lost or failed gameplay replayed");
            Assert(store.Loss.Snapshot("50", ReportMetric.ObservedConsumableUses)!.UncertainWrites == 1, "failed admitted write not uncertain");
            Assert(DiscordConnectorPlugin.StaticLogger.Warnings.Any(x => x.Contains("state=drained") && x.Contains("knownFailures=True")), "swallowed gameplay failure was reported as all-success");
            }
            finally { blocked.TrySetResult(); }
        }));
        Test("actual database shutdown waits off-thread for admitted synchronous readers", () => WithPlugin((plugin, store, automatic, manual) =>
        {
            var database = DiscordConnectorPlugin.StaticDatabase;
            object access = Field<object>(database, "legacyAccess");
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            Task reader = Task.Run(() =>
            {
                var read = database.GetType().GetMethod("ReadLegacy", BindingFlags.Instance | BindingFlags.NonPublic)!.MakeGenericMethod(typeof(int));
                read.Invoke(database, new object[] { (Func<int>)(() => { entered.Set(); release.Wait(); return 1; }) });
            });
            Assert(entered.Wait(TimeSpan.FromSeconds(10)), "reader did not enter");
            try
            {
                Task shutdown = database.BeginShutdown();
                Assert(!shutdown.IsCompleted && Monitor.IsEntered(access) == false, "shutdown closed database or waited on plugin thread");
                Assert(!store.Enqueue(new ActivityBatch()), "reader delay reopened admissions");
                release.Set(); reader.GetAwaiter().GetResult(); shutdown.GetAwaiter().GetResult();
            }
            finally { release.Set(); reader.GetAwaiter().GetResult(); }
        }));
    }
}
