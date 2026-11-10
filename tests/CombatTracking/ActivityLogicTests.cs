using DiscordConnector.Records;

internal static class ActivityLogicTests
{
    private static readonly DateTime Start = new(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);
    private static readonly ActivityActor Actor = new("account", "name", "connection", Start);
    private static DateTime Utc(double seconds) => Start.AddSeconds(seconds);
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }

    internal static void Run()
    {
        ActivityBoundaries(); Console.WriteLine("PASS activities: ActivityBoundaries");
        SailingDistance(); Console.WriteLine("PASS activities: SailingDistance");
        HealthLost(); Console.WriteLine("PASS activities: HealthLost decreases/heals/stale/death/disabled/identity fences");
        CaptureFlagMatrix(); Console.WriteLine("PASS activities: CaptureFlagMatrix");
        ConstructionEpochs(); Console.WriteLine("PASS activities: ConstructionEpochs");
        PickupToggleBoundary(); Console.WriteLine("PASS activities: PickupToggleBoundary");
        CookingOwnFlagFencing(); Console.WriteLine("PASS activities: CookingOwnFlagFencing");
    }

    private static void ActivityBoundaries()
    {
        var batches = new List<ActivityBatch>();
        var collector = new ActivityCollector(b => { Check(b.PhysicalDocumentCount <= 128, "physical batch budget"); batches.Add(b); return true; }, _ => { })
        { ActorIsCurrent = a => a.ConnectionToken == Actor.ConnectionToken };
        var flags = new LeaderboardsOptions(sailing: true, floorPickups: true, construction: true, exploration: true);
        void Tick(double time) => collector.Tick(Utc(time), time, "world", flags);
        FloorItemSignal Item(FloorItemEvent kind, string id, int stack, long owner, uint revision, long sender, double time,
            string token = "mutation", string signature = "wood-signature") => new(kind, "world", id, "wood", signature, stack, owner, revision, sender,
                token, Utc(time), time, kind == FloorItemEvent.Request ? Actor : null);
        void Request(string id, double time)
        {
            collector.ObserveItem(Item(FloorItemEvent.Request, id, 10, 99, 1, 7, time, "request:" + id));
            collector.ObserveItem(Item(FloorItemEvent.Snapshot, id, 10, 7, 1, 99, time + .1));
        }
        Tick(0);
        Request("partial", 0);
        var reduction = Item(FloorItemEvent.Snapshot, "partial", 7, 7, 2, 7, 1, "revision:2");
        collector.ObserveItem(reduction); collector.ObserveItem(reduction);
        collector.ObserveItem(Item(FloorItemEvent.Removed, "partial", 7, 7, 2, 7, 1.1, "removed:2"));
        collector.ObserveItem(Item(FloorItemEvent.Removed, "partial", 7, 7, 2, 7, 1.1, "removed:2"));
        Tick(3.2); collector.FlushThrough(Utc(3.2));
        var pickups = batches.SelectMany(b => b.Activities).Where(a => a.Kind == ActivityKind.InferredPickup).ToArray();
        Check(pickups.Length == 2 && pickups.Sum(a => a.Units) == 10, "partial plus deletion credits only last remainder; replay zero");
        Check(pickups.All(a => a.Provenance == ItemProvenance.UnknownMixed && a.SourceToken == null), "unknown provenance never fresh");
        Tick(10); Request("donor", 10);
        collector.ObserveItem(Item(FloorItemEvent.Removed, "donor", 10, 7, 1, 7, 10.2, "donor-deleted"));
        collector.ObserveItem(Item(FloorItemEvent.Snapshot, "destination", 2, 88, 1, 88, 10.3));
        collector.ObserveItem(Item(FloorItemEvent.Snapshot, "destination", 12, 88, 2, 88, 11));
        Tick(12.3); collector.FlushThrough(Utc(12.3));
        Check(batches.SelectMany(b => b.Activities).Count(a => a.Kind == ActivityKind.InferredPickup) == 2, "reverse-order merge suppressed");
        Request("metadata-donor", 14);
        collector.ObserveItem(Item(FloorItemEvent.Removed, "metadata-donor", 10, 7, 1, 7, 14.2, "metadata-donor-deleted"));
        collector.ObserveItem(Item(FloorItemEvent.Snapshot, "metadata-destination", 2, 88, 1, 88, 14.3, signature: "different-grid-quality-metadata"));
        collector.ObserveItem(Item(FloorItemEvent.Snapshot, "metadata-destination", 12, 88, 2, 88, 15, signature: "different-grid-quality-metadata"));
        Tick(16.3); collector.FlushThrough(Utc(16.3));
        Check(batches.SelectMany(b => b.Activities).Count(a => a.Kind == ActivityKind.InferredPickup) == 2,
            "same-prefab merge with mismatched strict metadata credited the donor");
        Request("full-inventory", 20);
        Request("mutated-metadata", 23);
        collector.ObserveItem(Item(FloorItemEvent.Snapshot, "mutated-metadata", 7, 7, 2, 7, 23.2, signature: "changed-donor-metadata"));
        Tick(25.3); collector.FlushThrough(Utc(25.3));
        Check(batches.SelectMany(b => b.Activities).Count(a => a.Kind == ActivityKind.InferredPickup) == 2,
            "strict same-item metadata validation was weakened by merge compatibility");
        Tick(27.1); collector.FlushThrough(Utc(27.1));
        Check(batches.SelectMany(b => b.Activities).Count(a => a.Kind == ActivityKind.InferredPickup) == 2, "grant without reduction zero");

        collector.ObservePiece(new PieceSignal("world", "baseline-piece", "wall", Actor, Utc(28), 28, false, true, false));
        collector.ObservePiece(new PieceSignal("world", "nonpiece", "not-a-piece", Actor, Utc(28), 28, true, false, false));
        collector.ObservePiece(new PieceSignal("world", "new-piece", "wall", Actor, Utc(28), 28, true, true, false));
        collector.ObservePiece(new PieceSignal("world", "new-piece", "wall", Actor, Utc(58), 58, false, true, false));
        Tick(58); collector.FlushThrough(Utc(58));
        var pieces = batches.SelectMany(b => b.Activities).Where(a => a.Kind == ActivityKind.NewPiece).ToArray();
        Check(pieces.Length == 1 && pieces[0].SubjectId == "new-piece", "baseline/Piece filter");

        var grid = new ExplorationGrid(64, 1, 1);
        collector.SampleExploration(new ExplorationSample("world", Actor, grid, 0, 0, Utc(60), 60, true)); Tick(60);
        collector.SampleExploration(new ExplorationSample("world", Actor, grid, 0, 0, Utc(62), 62, true)); Tick(62);
        collector.FlushThrough(Utc(62));
        Check(batches.SelectMany(b => b.Chunks).Sum(c => c.Cells.Count) == 5, "grid circle and same-player dedup");
        collector.SampleExploration(new ExplorationSample("world", Actor, new ExplorationGrid(64, 2, 1), 10, 10, Utc(64), 64, true)); Tick(64);
        collector.FlushThrough(Utc(64));
        Check(batches.SelectMany(b => b.Chunks).Sum(c => c.Cells.Count) == 5, "grid mismatch never creates another namespace");
        void Ship(double time, double x) => collector.SampleShip(new ShipSample("world", "ship", Actor, 1, x, 0, Utc(time), time, true, true));
        Ship(70, 0); Ship(71, 1); Ship(74, 4); Ship(75, 5);
        flags = new LeaderboardsOptions(); Tick(76);
        flags = new LeaderboardsOptions(sailing: true); Tick(77); Ship(77, 10); collector.FlushThrough(Utc(77));
        Check(batches.SelectMany(b => b.Activities).Where(a => a.Kind == ActivityKind.Sailing).Sum(a => a.Meters) == 2,
            "gap and disable/re-enable do not bridge helm samples");

        var pending = new List<ActivityBatch>();
        var paused = new ActivityCollector(b => { pending.Add(b); return true; }, _ => { }) { ActorIsCurrent = _ => true };
        var exploration = new LeaderboardsOptions(exploration: true);
        paused.Tick(Start, 0, "world", exploration);
        paused.SampleExploration(new ExplorationSample("world", Actor, grid, 0, 0, Utc(1), 1, true));
        paused.Tick(Utc(1), 1, "world", exploration); // Buffered cells, not yet flushed.
        paused.SampleExploration(new ExplorationSample("world", Actor, grid, 10, 10, Utc(3), 3, true)); // Queued circle.
        paused.Tick(Utc(3), 3, "world", exploration, CoverageReason.GridMismatch); // Durable grid feedback, same local grid.
        paused.SampleExploration(new ExplorationSample("world", Actor, grid, 20, 20, Utc(5), 5, true));
        paused.Coverage(ReportMetric.ExplorationCells, CoverageState.Observing, Utc(5));
        paused.Tick(Utc(5), 5, "world", exploration); paused.FlushThrough(Utc(5));
        Check(paused.ExplorationPaused && !pending.SelectMany(b => b.Chunks).Any(), "worker pause drops buffered/queued cells and same-grid samples");
        Check(pending.SelectMany(b => b.Coverage).Single().State == CoverageState.Unavailable,
            "paused coverage cannot be overwritten by observing");

        var cadence = new ActivitySampleCadence();
        for (int i = 0; i < 4096; i++) Check(cadence.Due("old-" + i, 0, 1, out _), "fill cadence cap");
        Check(!cadence.Due("new-player", 1, 2, out bool dropped) && dropped, "genuine capacity rejection is reported");
        Check(!cadence.Due("old-0", .5, 1, out _) && !cadence.Due("old-0", .75, 1, out _), "polls do not advance sample cadence");
        Check(cadence.Due("old-0", 1, 1, out _), "frequent polling still reaches sample cadence");
        cadence.Sweep(3);
        Check(cadence.Count == 4064 && cadence.Due("new-player", 3, 2, out dropped) && !dropped, "bounded expiry admits a new player after ship unload");
        for (int i = 0; i < 128; i++) cadence.Sweep(4);
        Check(cadence.Count == 1, "only recently seen cadence remains");
        cadence.Remove("new-player");
        for (int i = 0; i < 4096; i++) { cadence.Due("reused", 4, 1, out _); cadence.Remove("reused"); }
        cadence.Sweep(8);
        Check(cadence.Count == 0 && cadence.Due("new-ship", 8, 1, out dropped) && !dropped, "removal/touch cycles leave no stale expiry nodes");
    }

    private static void SailingDistance()
    {
        var batches = new List<ActivityBatch>();
        var collector = new ActivityCollector(b => { batches.Add(b); return true; }, _ => { }) { ActorIsCurrent = _ => true };
        collector.Tick(Start, 0, "world", new(sailing: true));
        void Sample(double time, double x, double z, bool fresh = true, bool onboard = true, ActivityActor? actor = null, long owner = 1) =>
            collector.SampleShip(new ShipSample("world", "ship", actor ?? Actor, owner, x, z, Utc(time), time, fresh, onboard));
        Sample(0, 0, 0); Sample(1, 3, 4); Sample(2, 3, 14); // 15m, not two seconds or a straight chord.
        Sample(3, 3000, 14); // Teleport rejected; the next sample starts at this new baseline.
        Sample(4, 3003, 18);
        Sample(7, 3006, 22); // Too long a gap, even at a plausible speed.
        Sample(8, 3009, 26, fresh: false); Sample(9, 3012, 30); // Stale breaks the chain.
        Sample(10, 3012, 30); // Stationary is not sailing.
        Sample(11, 3015, 34, onboard: false); Sample(12, 3018, 38);
        Sample(13, 3021, 42, actor: new("other", "Other", "other-session", Start));
        Sample(14, 3024, 46, owner: 2); // Neither a helm handoff nor owner change bridges distance.
        Sample(15, double.MaxValue, double.MaxValue, owner: 2); // Overflowed distance is rejected.
        collector.FlushThrough(Utc(15));
        var spans = batches.SelectMany(b => b.Activities).ToArray();
        Check(spans.Length == 2 && spans.Sum(span => span.Meters) == 20, "fresh horizontal segments only, with jump/stale/helm/owner fences");
    }

    private static void CaptureFlagMatrix()
    {
        for (int mask = 0; mask < 1024; mask++)
        {
            bool Flag(int bit) => (mask & (1 << bit)) != 0;
            var flags = new LeaderboardsOptions(bossKills: Flag(0), biomes: Flag(1), dodgeRolls: Flag(2),
                sailing: Flag(4), floorPickups: Flag(5), construction: Flag(6),
                exploration: Flag(7), cookingCompletions: Flag(8), healthLost: Flag(9));
            Check(ActivityCollector.NeedsZdoData(flags) == (Flag(0) || Flag(1) || Flag(4) || Flag(5) || Flag(7) || Flag(9)),
                "only pickup or position consumers require raw activity data parsing: " + mask);
            Check(ActivityCollector.NeedsPositionUpdates(flags) == (Flag(0) || Flag(1) || Flag(4) || Flag(7) || Flag(9)),
                "sailing/exploration/health require replication freshness: " + mask);
            var collector = new ActivityCollector(_ => true, _ => { });
            collector.Tick(Start, 0, "world", flags);
            Check(collector.ConstructionReady == Flag(6), "construction epoch is independent of data consumers: " + mask);
        }
    }

    private static void HealthLost()
    {
        var batches = new List<ActivityBatch>();
        var collector = new ActivityCollector(b => { batches.Add(b); return true; }, _ => { }) { ActorIsCurrent = _ => true };
        collector.Tick(Start, 0, "world", new(healthLost: true));
        void Sample(double time, double health, bool fresh = true, ActivityActor? actor = null, string character = "character") =>
            collector.SampleHealth("world", character, actor ?? Actor, health, Utc(time), time, fresh);
        double Total()
        {
            collector.FlushThrough(Utc(100));
            return batches.SelectMany(b => b.Activities).Sum(a => a.HpLoss);
        }
        Sample(0, 100); Sample(2, 80.25);
        Check(Total() == 19.75, "fresh decrease counts exact HP, not samples");
        Sample(4, 95); Sample(6, 90);
        Check(Total() == 24.75, "heal ignored and new health becomes baseline");
        Sample(9, 40); Sample(11, 35);
        Check(Total() == 29.75, "gap greater than 2.5 seconds rebaselines before counting");
        Sample(13, 0); Sample(15, 25); Sample(17, 20);
        Check(Total() == 34.75, "death and first respawn health count zero, subsequent loss counts");
        Sample(19, 10, fresh: false); Sample(21, 8); Sample(23, 7);
        Check(Total() == 35.75, "stale replication breaks the chain");
        Sample(25, double.NaN); Sample(27, 6); Sample(29, double.PositiveInfinity); Sample(31, 5);
        Check(Total() == 35.75, "non-finite health cannot be a baseline or observation");
        Sample(33, 4, actor: new("account", "name", "new-connection", Start));
        Sample(35, 3, character: "respawn-character");
        Check(Total() == 35.75, "connection and character changes never bridge samples");
        collector.Tick(Utc(36), 36, "world", new()); Sample(37, 2); Sample(39, 1);
        Check(Total() == 35.75, "disabled flag records nothing");
        collector.Tick(Utc(40), 40, "world", new(healthLost: true)); Sample(41, .5);
        Check(Total() == 35.75, "re-enable requires a new baseline");
        collector.Tick(Utc(42), 42, "other-world", new(healthLost: true));
        collector.SampleHealth("other-world", "character", Actor, .25, Utc(43), 43, true);
        Check(Total() == 35.75, "world transitions discard the previous baseline");
    }

    private static void ConstructionEpochs()
    {
        var batches = new List<ActivityBatch>();
        var collector = new ActivityCollector(b => { batches.Add(b); return true; }, _ => { }) { ActorIsCurrent = _ => true };
        var enabled = new LeaderboardsOptions(construction: true);
        void Piece(string world, string id, double time, bool created, bool excluded = false) => collector.ObservePiece(
            new PieceSignal(world, id, "wall", Actor, Utc(time), time, created, true, excluded));
        Piece("world", "before-capture", 0, true);
        collector.Tick(Start, 0, "world", enabled);
        Check(collector.ConstructionReady, "construction capture boundary must be ready without a registry walk");
        Piece("world", "before-capture", 30, false);
        Piece("world", "loaded-piece", 0, false);
        Piece("world", "growth", 0, true, true);
        Piece("world", "new-piece", 0, true);
        Piece("world", "pending-disable", 1, true);
        Piece("world", "new-piece", 30, false);
        collector.Tick(Utc(30), 30, "world", enabled);
        collector.Tick(Utc(31), 31, "world", new LeaderboardsOptions());
        Check(!collector.ConstructionReady, "disabled construction closes capture epoch");
        Piece("world", "disabled-piece", 31, true);
        collector.Tick(Utc(32), 32, "world", enabled);
        Check(collector.ConstructionReady, "re-enable opens a new ready epoch immediately");
        Piece("world", "pending-disable", 32, false);
        Piece("world", "disabled-piece", 32, false);
        Piece("world", "pending-world", 32, true);
        collector.Tick(Utc(33), 33, "other-world", enabled);
        Check(collector.ConstructionReady, "world change opens a new ready epoch even with unchanged flags");
        Piece("world", "pending-world", 63, false);
        Piece("other-world", "loaded-piece", 33, false);
        Piece("other-world", "new-world-piece", 33, true);
        Piece("other-world", "new-world-piece", 63, false);
        collector.Tick(Utc(63), 63, "other-world", enabled); collector.FlushThrough(Utc(63));
        Check(batches.SelectMany(b => b.Activities).Where(a => a.Kind == ActivityKind.NewPiece).Select(a => a.SubjectId)
            .SequenceEqual(new[] { "new-piece", "new-world-piece" }), "only successful creations in the current enabled epoch count");
    }

    private static void PickupToggleBoundary()
    {
        var batches = new List<ActivityBatch>();
        var collector = new ActivityCollector(b => { batches.Add(b); return true; }, _ => { }) { ActorIsCurrent = _ => true };
        var enabled = new LeaderboardsOptions(floorPickups: true);
        FloorItemSignal Item(FloorItemEvent kind, string id, int stack, long owner, uint revision, long sender, double time) =>
            new(kind, "world", id, "wood", "signature", stack, owner, revision, sender, id + ":" + time, Utc(time), time,
                kind == FloorItemEvent.Request ? Actor : null);
        void Request(string id, double time)
        {
            collector.ObserveItem(Item(FloorItemEvent.Request, id, 10, 99, 1, 7, time));
            collector.ObserveItem(Item(FloorItemEvent.Snapshot, id, 10, 7, 1, 99, time + .1));
        }
        collector.Tick(Start, 0, "world", enabled);
        Request("pending", 0);
        collector.ObserveItem(Item(FloorItemEvent.Removed, "pending", 10, 7, 1, 7, .2));
        collector.Tick(Utc(3), 3, "world", new LeaderboardsOptions());
        Request("disabled", 3);
        collector.Tick(Utc(4), 4, "world", enabled);
        collector.ObserveItem(Item(FloorItemEvent.Removed, "disabled", 10, 7, 2, 7, 4.1));
        collector.ObserveItem(Item(FloorItemEvent.Snapshot, "pending", 1, 7, 2, 7, 4.2));
        collector.Tick(Utc(7), 7, "world", enabled); collector.FlushThrough(Utc(7));
        Check(!batches.SelectMany(b => b.Activities).Any(), "disable drops pending pickup inference; re-enable never credits old requests");
        Request("fresh", 8);
        collector.ObserveItem(Item(FloorItemEvent.Removed, "fresh", 10, 7, 1, 7, 8.2));
        collector.Tick(Utc(11), 11, "world", enabled); collector.FlushThrough(Utc(11));
        var pickup = batches.SelectMany(b => b.Activities).Single();
        Check(pickup.SubjectId == "fresh" && pickup.Units == 10 && pickup.Provenance == ItemProvenance.UnknownMixed,
            "new enabled request still credits inferred units without inventing fresh provenance");
    }

    private static void CookingOwnFlagFencing()
    {
        var batches = new List<ActivityBatch>(); var changes = new List<CookingChange>();
        var collector = new ActivityCollector(b => { batches.Add(b); return true; }, _ => { }) { ActorIsCurrent = _ => true };
        var cooking = new CookingTracker(1, c => { changes.Add(c); return collector.ObserveCooking(c); }, _ => { });
        var flags = new LeaderboardsOptions(dodgeRolls: true, floorPickups: true, cookingCompletions: true);
        collector.Tick(Start, 0, "world", flags);
        CookingSlotSnapshot Slot(CookingStatus status) => new("world", "station", 0, 1, status == CookingStatus.Free ? "" : "raw",
            status, 0, cooking.CollectorEpoch);
        cooking.SetAvailability(false); cooking.ObserveInput(Slot(CookingStatus.Free), Slot(CookingStatus.NotDone), Actor, true, Start);
        Check(cooking.PendingCount == 0, "disabled cooking has no pending input");
        collector.ObserveAction(new PlayerActionRecord("world", Actor, PlayerActionKind.DodgeRoll, "roll:1", Start));
        cooking.SetAvailability(true); cooking.ObserveInput(Slot(CookingStatus.Free), Slot(CookingStatus.NotDone), Actor, true, Start);
        Check(cooking.PendingCount == 1, "availability permits accepted input");
        long sequence = cooking.AcceptanceSequence;
        cooking.SetAvailability(false);
        Check(cooking.PendingCount == 0 && changes.Any(c => c.Kind == CookingChangeKind.Invalidate && c.GenerationSequence == sequence), "own disable invalidates exact pending generation");
        collector.ObserveAction(new PlayerActionRecord("world", Actor, PlayerActionKind.DodgeRoll, "roll:2", Start));
        cooking.SetAvailability(true);
        cooking.ObserveCooking(Slot(CookingStatus.NotDone), new CookingSlotSnapshot("world", "station", 0, 1, "cooked", CookingStatus.Done, 10,
            cooking.CollectorEpoch), new CookingConversion("cooked", 1), true, Start);
        cooking.SetAvailability(false); collector.FlushThrough(Start);
        Check(!changes.Any(c => c.Kind == CookingChangeKind.Completed) && batches.SelectMany(b => b.Actions).Count() == 2,
            "disable never bridges cooking or gates independent rolls");
    }
}
