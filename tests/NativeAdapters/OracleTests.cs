using System.Collections;
using System.Diagnostics;
using System.Reflection;
using DiscordConnector;
using DiscordConnector.Leaderboards;
using DiscordConnector.Patches;
using DiscordConnector.Records;
using HarmonyLib;
using LiteDB;

internal static partial class Program
{
    private static ZPackage ReplicationPackage(int length = 3, long user = 10)
    {
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes);
        writer.Write(123); writer.Write(0);
        writer.Write(user); writer.Write(1U); writer.Write((ushort)0);
        writer.Write(1U); writer.Write(10L);
        writer.Write(0f); writer.Write(0f); writer.Write(0f);
        writer.Write(length); writer.Write(new byte[] { 1, 2, 3 });
        writer.Write(0L); writer.Write(0U); writer.Flush();
        var package = new ZPackage(bytes.ToArray()); package.SetPos(4); return package;
    }

    private static void OracleChecks()
    {
        Test("ZDO observer skips borrowed payload without copying and restores nonzero cursor", () =>
        {
            flags = new(healthLost: true);
            var package = ReplicationPackage(); Counters();
            ActivityPatches.Data.Prefix(ZNet.instance.Peers[0].m_rpc, package, out var frame);
            try
            {
                Assert(frame != null && !frame.Failed && frame.Before.Count == 1, "valid replication rejected");
                Assert(package.GetPos() == 4 && package.Copies == 0 && ZPackage.ArrayCopies == 0 && ZPackage.ParameterCopies == 0,
                    "payload copied or borrowed cursor changed");
            }
            finally { ActivityPatches.Data.Finalizer(frame, null); }
        });
        foreach (var (name, length, user, throwing) in new[] {
            ("negative payload", -1, 10L, 0), ("oversized payload", int.MaxValue, 10L, 0),
            ("unknown id", 3, 999L, 0), ("partial read", 3, 10L, 5) })
            Test("ZDO observer rejects " + name + " without copies/cursor damage", () =>
            {
                flags = new(healthLost: true);
                var package = ReplicationPackage(length, user); package.ThrowOnRead = throwing; Counters();
                ActivityPatches.Data.Prefix(ZNet.instance.Peers[0].m_rpc, package, out var frame);
                try
                {
                    Assert(frame == null || frame.Failed, "invalid replication accepted");
                    Assert(package.GetPos() == 4 && package.Copies == 0 && ZPackage.ArrayCopies == 0 && ZPackage.ParameterCopies == 0,
                        "rejected replication copied data or damaged cursor");
                }
                finally { ActivityPatches.Data.Finalizer(frame, null); }
            });
        Test("known non-Piece candidates are discarded while delayed prefab metadata is retained", () =>
        {
            flags = new(construction: true);
            DateTime utc = DateTime.UtcNow;
            runtime!.Update(utc, 100); ZNetScene.instance = new();
            var irrelevant = new ZDOID(10, 2); var delayed = new ZDOID(10, 3);
            ZDOMan.instance.Zdos[irrelevant] = new() { m_uid = irrelevant, Prefab = 42 };
            ZDOMan.instance.Zdos[delayed] = new() { m_uid = delayed, Prefab = 43 };
            ZNetScene.instance.Prefabs[42] = new();
            runtime.NewZdo("50", irrelevant, utc, 100, false); runtime.NewZdo("50", delayed, utc, 100, false);
            runtime.Update(utc, 100.1);
            var candidates = Field<IDictionary>(runtime, "_newPieces");
            var pieces = Field<IDictionary>(runtime.Collector, "_pieces");
            Assert(!candidates.Contains(irrelevant) && !pieces.Contains(irrelevant.ToString()), "irrelevant candidate retained");
            Assert(candidates.Contains(delayed) && pieces.Contains(delayed.ToString()), "unknown prefab prematurely dropped");
            ZNetScene.instance.Prefabs[43] = new();
            runtime.Update(utc, 100.2);
            Assert(candidates.Count == 0 && pieces.Count == 0, "late irrelevant metadata not discarded");
        });
        Test("placement sweep is cadenced, avoids per-frame candidate-sized allocations and resets", () =>
        {
            var collector = runtime!.Collector;
            var options = new LeaderboardsOptions(construction: true);
            DateTime utc = DateTime.UtcNow;
            collector.Tick(utc, 100, "50", options);
            long Allocated()
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 20; i++) collector.Tick(utc, 100.1, "50", options);
                return GC.GetAllocatedBytesForCurrentThread() - before;
            }
            Allocated(); long empty = Allocated();
            for (int i = 0; i < 1024; i++) collector.ObservePiece(new("50", "piece-" + i, "", null, utc, 100, true, false, false));
            long populated = Allocated();
            Assert(populated <= empty + 1024, "per-frame allocation grows with placement candidates");
            Assert(Field<double>(collector, "_nextPieceSweep") == 101, "subsecond tick swept candidates");
            collector.Tick(utc, 101, "50", options);
            Assert(Field<double>(collector, "_nextPieceSweep") == 102, "one-second sweep not advanced");
            collector.Reset(); Assert(Field<double>(collector, "_nextPieceSweep") == 0, "reset retained sweep deadline");
        });
        Test("actual manual and scheduled queries refresh report inclusion after config changes", () => WithPlugin((plugin, store, automatic, manual) =>
        {
            DateTime utc = DateTime.UtcNow;
            var db = Field<LiteDatabase>(DiscordConnectorPlugin.StaticDatabase, "db");
            new ReportedSignalStore(db).Insert(new(ReportedSignalKind.KillCredit, "50", new("actor", "Player", "session", utc), "signal", utc));
            var observed = Field<object>(plugin, "_observedConfig");
            foreach (bool enabled in new[] { false, true, false })
            {
                Field<BepInEx.Configuration.ConfigEntry<bool>>(observed, "reportedKillCredits").Value = enabled;
                plugin.Config.Changed();
                foreach (var query in new[] { manual.Query, automatic.Query })
                {
                    var report = query("50", utc.AddSeconds(-1), utc.AddSeconds(1)).GetAwaiter().GetResult();
                    int count = report.Rows.TryGetValue(ReportMetric.ReportedKillCredits, out var rows) ? rows.Count : 0;
                    Assert(count == (enabled ? 1 : 0), "query retained startup inclusion");
                }
            }
        }));
        Test("all finalizers avoid HarmonyX runOriginal injection and skipped emote cannot submit", () =>
        {
            var finalizers = typeof(ActivityPatches).Assembly.GetTypes().SelectMany(type => type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                .Where(method => method.GetCustomAttribute<HarmonyFinalizer>() != null);
            Assert(finalizers.All(method => method.GetParameters().All(parameter => parameter.Name != "__runOriginal")), "finalizer depends on removed prefix/postfix local");
            int requests = 0;
            ActivityPatches.EmoteTriggers = new(true, "wave", "cheer");
            ActivityPatches.EmoteData.Prefix(ZDOMan.instance, ZNet.instance.Peers[0].m_rpc, out var capture);
            Assert(capture?.Scope != null, "emote scope not opened");
            capture.Helper.Stage(capture.Scope, "50", "session", "character", new("character", 1, 1, "wave"), new("character", 2, 2, "wave"), 100, _ => requests++);
            ActivityPatches.EmoteData.Postfix(capture, false);
            ActivityPatches.EmoteData.Finalizer(capture, null);
            Assert(requests == 0 && capture.Scope.Closed && capture.Scope.Pending.Count == 0, "skipped original did not discard pending emote");
        });
        Test("application quit joins admitted writes and skips unpatch even on subsequent destroy", () => WithPlugin((plugin, store, automatic, manual) =>
        {
            DateTime utc = DateTime.UtcNow; int unpatches = Harmony.Unpatches;
            var actor = new ActivityActor("actor", "Player", "session", utc);
            Assert(store.Enqueue(new(signals: [new(ReportedSignalKind.KillCredit, "50", actor, "quit", utc)], signalPrincipal: new("actor", "session", false))), "write not admitted");
            Call(plugin, "OnApplicationQuit"); Call(plugin, "OnDestroy");
            Assert(Field<Task>(plugin, "_observationShutdown").IsCompletedSuccessfully, "quit returned before drain");
            Assert(Harmony.Unpatches == unpatches && !store.Enqueue(new()), "quit unpatched or failed to seal admission");
            using var reopened = new LiteDatabase(Field<string>(DiscordConnectorPlugin.StaticDatabase, "DbPath"));
            Assert(reopened.GetCollection("reported_kill_credits_v1").Count() == 1, "quit lost admitted write");
        }));
        Test("quit drain times out around five seconds without closing worker-owned database", () => WithPlugin((plugin, store, automatic, manual) =>
        {
            var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Put(store, "tail", blocked.Task); int unpatches = Harmony.Unpatches;
            try
            {
                var timer = Stopwatch.StartNew(); Call(plugin, "OnApplicationQuit"); timer.Stop();
                Assert(timer.Elapsed.TotalSeconds >= 4.8 && timer.Elapsed.TotalSeconds < 8, "quit wait not bounded near five seconds");
                Assert(!Field<Task>(plugin, "_observationShutdown").IsCompleted && Harmony.Unpatches == unpatches, "timeout forced drain or unpatched");
                var db = Field<LiteDatabase>(DiscordConnectorPlugin.StaticDatabase, "db");
                Assert(db.GetCollection("reported_kill_credits_v1").Count() == 0, "timeout closed database before worker drained");
                Assert(DiscordConnectorPlugin.StaticLogger.Warnings.Any(message => message.Contains("timed out after 5 seconds")), "timeout not logged");
            }
            finally { blocked.TrySetResult(); }
        }));
        Test("ordinary destroy remains nonblocking and unpatches", () => WithPlugin((plugin, store, automatic, manual) =>
        {
            var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Put(store, "tail", blocked.Task); int unpatches = Harmony.Unpatches;
            try
            {
                var timer = Stopwatch.StartNew(); Call(plugin, "OnDestroy"); timer.Stop();
                Assert(timer.Elapsed.TotalSeconds < 1 && !Field<Task>(plugin, "_observationShutdown").IsCompleted, "ordinary unload blocked on drain");
                Assert(Harmony.Unpatches == unpatches + 1, "ordinary unload did not unpatch");
            }
            finally { blocked.TrySetResult(); }
        }));
    }
}
