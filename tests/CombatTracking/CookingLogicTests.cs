using DiscordConnector.Records;

internal static class CookingLogicTests
{
    internal static void Run()
    {
        var changes = new List<CookingChange>();
        var tracker = new CookingTracker(12, c => { changes.Add(c); return true; }, _ => { });
        tracker.SetAvailability(true);
        var utc = new DateTime(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);
        var actor = new ActivityActor("account", "name", "connection", utc);
        CookingSlotSnapshot Slot(CookingStatus status, string item, double progress = 0) =>
            new("world", "station", 0, 1, item, status, progress, tracker.CollectorEpoch);
        void Check(bool value, string message) { if (!value) throw new Exception(message); }
        var free = Slot(CookingStatus.Free, ""); var raw = Slot(CookingStatus.NotDone, "raw");
        var done = Slot(CookingStatus.Done, "cooked", 11); var burnt = Slot(CookingStatus.Burnt, "coal", 30);
        var conversion = new CookingConversion("cooked", 1);
        tracker.ObserveInput(raw, raw, actor, true, utc); // Full/unchanged request.
        tracker.ObserveInput(free, free, actor, true, utc);
        tracker.ObserveInput(free, raw, actor, false, utc);
        tracker.ObserveCooking(raw, done, conversion, true, utc); // Preexisting slot zero is unknown.
        Check(changes.Count == 0, "full/unchanged/failed/preexisting inputs must count zero");
        tracker.ObserveInput(free, raw, actor, true, utc);
        tracker.ObserveCooking(raw, done, conversion, true, utc);
        tracker.ObserveCooking(raw, done, conversion, true, utc);
        tracker.ObserveCooking(done, burnt, conversion, true, utc);
        Check(changes.Count(c => c.Kind == CookingChangeKind.Completed) == 1, "verified output once, done-then-burn retains credit");
        var accepted = changes.Single(c => c.Kind == CookingChangeKind.AcceptedInput);
        var completed = changes.Single(c => c.Kind == CookingChangeKind.Completed);
        Check(completed.Generation == accepted.Generation && completed.ObservationEpoch == 12 &&
            completed.GenerationSequence == accepted.GenerationSequence && completed.Snapshot.ItemPrefab == "raw" &&
            completed.Conversion!.Units == 1 && ReferenceEquals(completed.Actor, actor), "generation order/input/output/actor preserved");
        tracker.ObserveInput(free, raw, actor, true, utc);
        tracker.ObserveCooking(raw, burnt, conversion, true, utc);
        Check(changes.Count(c => c.Kind == CookingChangeKind.Completed) == 1 && tracker.PendingCount == 0, "direct burn zero");
        tracker.ObserveInput(free, raw, actor, true, utc);
        tracker.ObserveCooking(raw, done, null, true, utc);
        Check(changes.Count(c => c.Kind == CookingChangeKind.Completed) == 1 && tracker.PendingCount == 0, "unknown conversion zero");
        long beforeReset = tracker.AcceptanceSequence;
        tracker.Reset();
        var oldFree = free;
        free = Slot(CookingStatus.Free, ""); raw = Slot(CookingStatus.NotDone, "raw"); done = Slot(CookingStatus.Done, "cooked", 11);
        tracker.ObserveInput(oldFree, raw, actor, true, utc);
        Check(tracker.PendingCount == 0, "acceptance cannot cross a collector epoch reset");
        tracker.ObserveCooking(raw, done, conversion, true, utc); // Restart reset cannot resume input.
        tracker.ObserveInput(free, raw, actor, true, utc);
        tracker.ObserveCooking(raw, done, conversion, true, utc);
        Check(changes.Count(c => c.Kind == CookingChangeKind.Completed) == 2 && tracker.AcceptanceSequence > beforeReset,
            "equal-time reuse after reset advances sequence, never resumes unknown slot");
        long highWater = tracker.AcceptanceSequence;
        var replacement = new CookingTracker(12, c => { changes.Add(c); return true; }, _ => { });
        replacement.SetAvailability(true);
        replacement.ObserveInput(new CookingSlotSnapshot("world", "station", 0, 1, "", CookingStatus.Free, 0, replacement.CollectorEpoch),
            new CookingSlotSnapshot("world", "station", 0, 1, "raw", CookingStatus.NotDone, 0, replacement.CollectorEpoch), actor, true, utc);
        Check(changes.Last().GenerationSequence > highWater, "replacement on same worker retains sequence high water");
        Console.WriteLine("PASS cooking: CookingAcceptanceAndCompletion");
        NoSimulationNativeOwnership();
    }

    private static void NoSimulationNativeOwnership()
    {
        foreach (string reason in new[] { "eligible", "client owned", "no view", "no world", "client", "listen", "own flag off",
            "no provenance", "stale actor", "wrong target", "wrong sender", "wrong input", "unchanged slot", "failed insertion",
            "skipped insertion", "failed completion", "skipped completion", "disable re-enable", "owner changed", "nested failure" })
        {
            BepInEx.Bootstrap.Chainloader.PluginInfos.Clear();
            ZNet.instance = new();
            var changes = new List<CookingChange>();
            var tracker = new CookingTracker(13, c => { changes.Add(c); return true; }, _ => { });
            var runtime = new DiscordConnector.Patches.ActivityRuntime { Cooking = tracker };
            DiscordConnector.Patches.ActivityRuntime.Current = runtime;
            var station = new CookingStation();
            var actor = new ActivityActor("account", "name", reason == "stale actor" ? "old connection" : "connection", DateTime.UtcNow);
            var ingress = new DiscordConnector.Patches.ActivityPatches.Ingress { Actor = actor, Target = station.m_nview.Zdo!.m_uid };
            DiscordConnector.Patches.ActivityPatches.Sender = ingress;
            switch (reason)
            {
                case "client owned": station.m_nview.Zdo!.Owner = 2; break;
                case "no view": station.m_nview.Zdo = null; break;
                case "no world": ZNet.instance.World = 0; break;
                case "client": ZNet.instance.Server = false; break;
                case "listen": ZNet.instance.Dedicated = false; break;
                case "own flag off": runtime.Options = new(); break;
                case "no provenance": DiscordConnector.Patches.ActivityPatches.Sender = null; break;
                case "wrong target": ingress.Target.ID++; break;
                case "wrong sender": ingress.Sender++; break;
                case "wrong input": ingress.CookingItem = "other"; break;
            }
            void Invoke(string method, Action mutation, bool ran = true, Exception? error = null)
            {
                var target = typeof(CookingStation).GetMethod(method)!;
                DiscordConnector.Patches.CookingPatches.Prefix(station, target, new object[] { 7L, "raw" }, out var frame);
                mutation();
                DiscordConnector.Patches.CookingPatches.Postfix(station, frame, ran);
                DiscordConnector.Patches.CookingPatches.Finalizer(frame, error);
            }
            try
            {
                Invoke(nameof(CookingStation.RPC_AddItem), () =>
                {
                    if (reason != "unchanged slot") station.ItemName = "raw";
                }, reason != "skipped insertion", reason == "failed insertion" ? new Exception("failed") : null);
                if (reason == "disable re-enable")
                {
                    runtime.Options = new();
                    Invoke(nameof(CookingStation.UpdateCooking), () => { });
                    runtime.Options = new(cookingCompletions: true);
                }
                if (reason == "owner changed")
                {
                    tracker.InvalidateStation("50", station.m_nview.Zdo!.m_uid.ToString());
                    station.m_nview.Zdo.Owner = 2;
                }
                Invoke(nameof(CookingStation.UpdateCooking), () =>
                {
                    station.ItemName = "cooked"; station.SlotStatus = CookingStation.Status.Done; station.Progress = 11;
                    if (reason == "nested failure")
                        Invoke(nameof(CookingStation.UpdateCooking), () => { }, error: new Exception("nested failure"));
                }, reason != "skipped completion", reason == "failed completion" ? new Exception("failed") : null);
                int expected = reason == "eligible" ? 1 : 0;
                if (changes.Count(c => c.Kind == CookingChangeKind.Completed) != expected)
                    throw new Exception(reason + ": cooking must require accepted input, actual server ownership and completed invocation");
                if (reason == "eligible" && (changes.Count != 2 || changes.Last().Actor != actor || changes.Last().Conversion!.Units != 1))
                    throw new Exception("no-plugin cooking lost accepted inserter/output provenance");
                if (reason == "disable re-enable")
                {
                    if (tracker.PendingCount != 0 || !changes.Any(c => c.Kind == CookingChangeKind.Invalidate))
                        throw new Exception("disable/re-enable retained old generation");
                    station.ItemName = ""; station.Progress = 0; station.SlotStatus = CookingStation.Status.NotDone;
                    Invoke(nameof(CookingStation.RPC_AddItem), () => station.ItemName = "raw");
                    Invoke(nameof(CookingStation.UpdateCooking), () =>
                    { station.ItemName = "cooked"; station.SlotStatus = CookingStation.Status.Done; station.Progress = 11; });
                    if (changes.Count(c => c.Kind == CookingChangeKind.Completed) != 1)
                        throw new Exception("re-enable did not accept a fresh proven generation");
                }
            }
            finally
            {
                tracker.Reset();
                DiscordConnector.Patches.ActivityRuntime.Current = null;
                DiscordConnector.Patches.ActivityPatches.Sender = null;
            }
        }
        Console.WriteLine("PASS cooking: NoSimulationNativeOwnership (production cooking hooks; runtime/ingress/game surfaces are stubs)");
    }
}
