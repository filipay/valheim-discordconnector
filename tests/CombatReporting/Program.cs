using BepInEx.Configuration;
using DiscordConnector;
using DiscordConnector.Config;
using DiscordConnector.Leaderboards;
using DiscordConnector.Records;

int passed = 0;
int failed = 0;
void Test(string name, Action check)
{
    try
    {
        check();
        Console.WriteLine($"PASS {name}");
        passed++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"FAIL {name}: {ex.GetType().Name}: {ex.Message}");
        failed++;
    }
}
void Check(bool condition, string message = "assertion failed")
{
    if (!condition) throw new Exception(message);
}
DateTime U(string s) => DateTime.Parse(s, null, System.Globalization.DateTimeStyles.AdjustToUniversal |
    System.Globalization.DateTimeStyles.AssumeUniversal);
TimeZoneInfo dublin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Dublin");
CombatRecord Row(string id, string player, string name, bool boss = false, int hour = 12) => new()
{
    Id = id, WorldId = "world", VictimId = id, PlayerId = player, PlayerName = name,
    Creature = boss ? "Eikthyr" : "Boar", Biome = "Meadows", IsBoss = boss,
    TimestampUtc = U("2026-03-27T00:00:00Z").AddHours(hour)
};
CombinedReportSnapshot Snapshot(IEnumerable<CombatRecord> rows, string world = "world") => new(world,
    new Dictionary<ReportMetric, IReadOnlyList<ReportRow>> {
        [ReportMetric.ReportedKillCredits] = rows.Where(row => !string.IsNullOrWhiteSpace(row.PlayerId)).GroupBy(row => row.PlayerId, StringComparer.Ordinal)
            .Select(group => new ReportRow(group.Key, group.OrderByDescending(row => row.TimestampUtc).First().PlayerName,
                group.Count(), 0, 0, group.Max(row => row.TimestampUtc))).ToArray()
    });
var daily = CombatReports.PreviousPeriod(U("2026-03-28T12:00:00Z"), dublin, false);
var headings = new Dictionary<ReportMetric, string> {
    [ReportMetric.ParticipantBossCredits] = "👑 The Giantbane — boss kills",
    [ReportMetric.Biomes] = "🧭 The Wanderer — biomes visited",
    [ReportMetric.ObservedDodgeRolls] = "🌀 The Trickster — dodge rolls",
    [ReportMetric.SailingDistance] = "🌊 Njord's Chosen — distance sailed",
    [ReportMetric.InferredPickups] = "🎒 The Fafnir — items picked up",
    [ReportMetric.NewPieces] = "🔨 The Builder of Asgard — pieces built",
    [ReportMetric.CropsPlanted] = "🌱 Hand of Freyr — crops planted",
    [ReportMetric.ExplorationCells] = "🗺️ Huginn's Eye — areas explored",
    [ReportMetric.CookingCompletions] = "🍲 The Andhrimnir — meals cooked",
    [ReportMetric.ReportedKillCredits] = "⚔️ The Berserker — kills",
    [ReportMetric.ObservedConsumableUses] = "🍖 The Einherjar Feaster — meals eaten",
    [ReportMetric.ReportedDodgeHitNotifications] = "🤸 The Fate-Dodger — dodge hits",
    [ReportMetric.HealthLost] = "🩸 Baldr's Fate — health lost",
    [ReportMetric.OnFootDistance] = "👟 The Bifrost Runner — distance on foot",
    [ReportMetric.BiomeTime] = "🏡 The Hearthkeeper — most time in one biome",
    [ReportMetric.OnlineTime] = "⏳ The Heimdall — time online",
    [ReportMetric.PortalTrips] = "🌀 The Omnipresent — portal trips",
    [ReportMetric.DamageDealt] = "🪓 The Axe of Odin — damage dealt",
    [ReportMetric.LongestBowHit] = "🏹 The Hawk of Huginn — longest bow hit"
};
Test("all nineteen creative metric labels match the requested mapping", () =>
{
    var heading = typeof(CombatReports).GetMethod("Heading", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
    Check(headings.Count == Enum.GetValues<ReportMetric>().Length);
    foreach (var pair in headings) Check((string)heading.Invoke(null, new object[] { pair.Key })! == pair.Value);
});

Test("opt-in defaults are independent of legacy config", () =>
{
    var config = new CombatConfig(new ConfigFile());
    Check(!config.DailyEnabled && !config.WeeklyEnabled);
    Check(config.TimeZoneId == "Europe/Dublin" && config.TopCount == 5);
});
Test("Dublin spring day is 23 hours, half-open UTC", () =>
{
    var p = CombatReports.PreviousPeriod(U("2026-03-30T08:00:00Z"), dublin, false);
    Check(p.StartUtc == U("2026-03-29T00:00:00Z") && p.EndUtc == U("2026-03-29T23:00:00Z"));
    Check(p.StartUtc.Kind == DateTimeKind.Utc && p.EndUtc.Kind == DateTimeKind.Utc);
});
Test("Dublin autumn day is 25 hours", () =>
{
    var p = CombatReports.PreviousPeriod(U("2026-10-26T08:00:00Z"), dublin, false);
    Check(p.StartUtc == U("2026-10-24T23:00:00Z") && p.EndUtc == U("2026-10-26T00:00:00Z"));
});
Test("month and year boundaries", () =>
{
    var month = CombatReports.PreviousPeriod(U("2026-05-01T08:00:00Z"), dublin, false);
    Check(month.StartUtc == U("2026-04-29T23:00:00Z") && month.EndUtc == U("2026-04-30T23:00:00Z"));
    var year = CombatReports.PreviousPeriod(U("2027-01-01T08:00:00Z"), dublin, false);
    Check(year.StartUtc == U("2026-12-31T00:00:00Z") && year.EndUtc == U("2027-01-01T00:00:00Z"));
});
Test("Monday-Sunday weeks, including Sunday and year crossing", () =>
{
    var monday = CombatReports.PreviousPeriod(U("2026-03-30T08:00:00Z"), dublin, true);
    Check(monday.StartUtc == U("2026-03-23T00:00:00Z") && monday.EndUtc == U("2026-03-29T23:00:00Z"));
    var sunday = CombatReports.PreviousPeriod(U("2026-03-29T08:00:00Z"), dublin, true);
    Check(sunday.StartUtc == U("2026-03-16T00:00:00Z") && sunday.EndUtc == U("2026-03-23T00:00:00Z"));
    var year = CombatReports.PreviousPeriod(U("2027-01-04T08:00:00Z"), dublin, true);
    Check(year.StartUtc == U("2026-12-28T00:00:00Z") && year.EndUtc == U("2027-01-04T00:00:00Z"));
    var autumn = CombatReports.PreviousPeriod(U("2026-10-26T08:00:00Z"), dublin, true);
    Check((autumn.EndUtc - autumn.StartUtc).TotalHours == 169);
});
Test("top five by stable ID with latest name across boss/creature categories", () =>
{
    var rows = new List<CombatRecord> { Row("old", "p0", "Old", hour: 1), Row("new", "p0", "Renamed", true, 20) };
    for (int i = 1; i <= 6; i++) rows.Add(Row($"r{i}", $"p{i}", $"Player{i}"));
    string report = CombatReports.Render(Snapshot(rows), daily, "Europe/Dublin", 5, Array.Empty<string>())!;
    Check(report.Contains("Renamed: 2") && !report.Contains("Old: ") && !report.Contains("Player5: ") && !report.Contains("Player6: "));
    Check(report.Contains("**⚔️ The Berserker** — kills"));
    Check(report.StartsWith("🏆 Daily leaderboard — Mar 27\n") && report.EndsWith("-# Server-observed; some counts are partial."));
});
Test("same display name never merges distinct players", () =>
{
    string report = CombatReports.Render(Snapshot(new[] { Row("a", "a", "Twin"), Row("b", "b", "Twin") }), daily, "Europe/Dublin", 5, Array.Empty<string>())!;
    Check(report.Contains("1. Twin: 1\n2. Twin: 1"));
});
Test("meaningful-empty reports are skipped", () =>
{
    Check(CombatReports.Render(Snapshot(Array.Empty<CombatRecord>()), daily, "Europe/Dublin", 5, Array.Empty<string>()) == null);
    Check(CombatReports.Render(Snapshot(new[] { Row("a", "", "Unknown") }), daily, "Europe/Dublin", 5, Array.Empty<string>()) == null);
});
Test("oversized strings, markdown, controls, mentions and surrogate pairs are safe", () =>
{
    var rows = Enumerable.Range(0, 100).Select(i => Row($"r{i}", $"p{i}", "@everyone <@123> **_[x](url)`\n\u202e" + new string('x', 10000))).ToArray();
    string report = CombatReports.Render(Snapshot(rows), daily, new string('t', 10000), 16, Array.Empty<string>())!;
    Check(report.Length <= 1900 && report.Contains("…"));
    Check(!report.Contains("@everyone") && !report.Contains("@here") && !report.Contains("<@") && !report.Contains('\u202e'));
    Check(report.Contains("\\*\\*\\_"));
    Check(System.Text.Encoding.UTF8.GetString(System.Text.Encoding.UTF8.GetBytes(report)) == report);
    Check(report.Contains("**⚔️ The Berserker** — kills"));
});

var claims = new HashSet<string>();
var sends = new List<string>();
var warnings = new List<string>();
var queries = new List<(string World, DateTime Start, DateTime End)>();
CombatReports Scheduler(bool weekly = false, string zone = "Europe/Dublin", int top = 5, Action<string>? send = null) =>
    new(true, weekly, zone, top,
        (world, start, end) => { queries.Add((world, start, end)); return Snapshot(new[] { Row("r", "p", "Player") }, world); },
        (id, now) => claims.Add(id), send ?? sends.Add, warnings.Add);
void Reset() { claims.Clear(); sends.Clear(); warnings.Clear(); queries.Clear(); }

Test("startup baseline skips history; exact midnight sends one combined report", () =>
{
    Reset();
    using var scheduler = Scheduler();
    scheduler.Tick(U("2026-03-27T12:00:00Z"), "world");
    Check(sends.Count == 0 && queries.Count == 0);
    scheduler.Tick(U("2026-03-28T00:00:00Z"), "world");
    scheduler.Tick(U("2026-03-28T00:01:00Z"), "world");
    Check(sends.Count == 1 && claims.Count == 1);
    Check(queries.Single() == ("world", U("2026-03-27T00:00:00Z"), U("2026-03-28T00:00:00Z")));
});
Test("restart, overlap and clock rollback cannot repeat a claimed period", () =>
{
    Reset();
    using var first = Scheduler();
    using var overlapping = Scheduler();
    first.Tick(U("2026-03-27T12:00:00Z"), "world");
    overlapping.Tick(U("2026-03-27T12:00:00Z"), "world");
    first.Tick(U("2026-03-28T00:00:00Z"), "world");
    overlapping.Tick(U("2026-03-28T00:01:00Z"), "world");
    using var restarted = Scheduler();
    restarted.Tick(U("2026-03-28T00:02:00Z"), "world");
    restarted.Tick(U("2026-03-27T23:59:00Z"), "world");
    restarted.Tick(U("2026-03-28T01:00:00Z"), "world");
    Check(sends.Count == 1);
});
Test("long gaps send only latest completed period, world switch baselines", () =>
{
    Reset();
    using var scheduler = Scheduler();
    scheduler.Tick(U("2026-03-01T12:00:00Z"), "world");
    scheduler.Tick(U("2026-04-01T12:00:00Z"), "world");
    Check(sends.Count == 1 && queries.Single().Start == U("2026-03-30T23:00:00Z"));
    scheduler.Tick(U("2026-04-02T12:00:00Z"), "other");
    Check(sends.Count == 1);
    scheduler.Tick(U("2026-04-03T12:00:00Z"), "other");
    Check(sends.Count == 2 && queries.Last().World == "other");
});
Test("daily and weekly each produce one report on Monday", () =>
{
    Reset();
    using var scheduler = Scheduler(true);
    scheduler.Tick(U("2026-03-29T12:00:00Z"), "world");
    scheduler.Tick(U("2026-03-29T23:00:00Z"), "world");
    Check(sends.Count == 2 && claims.Count == 2);
    Check(queries.Any(q => q.Start == U("2026-03-23T00:00:00Z")));
});
Test("injected send failure is contained, claimed at-most-once, no retry", () =>
{
    Reset(); int attempts = 0;
    using var scheduler = Scheduler(send: _ => { attempts++; throw new InvalidOperationException("injected"); });
    scheduler.Tick(U("2026-03-27T12:00:00Z"), "world");
    scheduler.Tick(U("2026-03-28T00:00:00Z"), "world");
    scheduler.Tick(U("2026-03-28T00:01:00Z"), "world");
    using var restarted = Scheduler();
    restarted.Tick(U("2026-03-27T12:00:00Z"), "world");
    restarted.Tick(U("2026-03-28T00:02:00Z"), "world");
    Check(attempts == 1 && sends.Count == 0 && claims.Count == 1 && warnings.Count == 1);
});
Test("invalid zone and top count disable reports without changing tracking config", () =>
{
    Reset();
    using var invalidZone = Scheduler(zone: "not/a/timezone");
    using var invalidTop = Scheduler(top: 0);
    invalidZone.Tick(U("2026-03-27T12:00:00Z"), "world");
    invalidZone.Tick(U("2026-03-28T12:00:00Z"), "world");
    invalidTop.Tick(U("2026-03-28T12:00:00Z"), "world");
    Check(warnings.Count == 2 && sends.Count == 0);
    var file = new ConfigFile(); file.Values["Enabled"] = true; file.Values["Time Zone"] = "not/a/timezone";
    Check(new CombatConfig(file).TimeZoneId == "not/a/timezone");
});
Test("disposed scheduler never sends", () =>
{
    Reset();
    var scheduler = Scheduler(); scheduler.Tick(U("2026-03-27T12:00:00Z"), "world"); scheduler.Dispose();
    scheduler.Tick(U("2026-03-28T12:00:00Z"), "world");
    Check(sends.Count == 0);
});
Test("empty scheduled period does not claim or send", () =>
{
    Reset();
    using var scheduler = new CombatReports(true, false, "Europe/Dublin", 5,
        (world, _, _) => new CombinedReportSnapshot(world), (id, now) => claims.Add(id), sends.Add, warnings.Add);
    scheduler.Tick(U("2026-03-27T12:00:00Z"), "world");
    scheduler.Tick(U("2026-03-28T12:00:00Z"), "world");
    Check(claims.Count == 0 && sends.Count == 0 && warnings.Count == 0);
});
Test("storage failure cannot send an unclaimed report", () =>
{
    Reset();
    using var scheduler = new CombatReports(true, false, "Europe/Dublin", 5,
        (world, _, _) => Snapshot(new[] { Row("r", "p", "Player") }, world),
        (_, _) => throw new IOException("injected claim failure"), sends.Add, warnings.Add);
    scheduler.Tick(U("2026-03-27T12:00:00Z"), "world");
    scheduler.Tick(U("2026-03-28T12:00:00Z"), "world");
    Check(sends.Count == 0 && warnings.Count == 1);
});
Test("real store checkpoint survives reopen after injected send failure; UTC query is half-open", () =>
{
    Reset();
    string path = Path.Combine(Path.GetTempPath(), "combat-reporting-" + Guid.NewGuid().ToString("N") + ".db");
    int attempts = 0;
    string? attemptedReport = null;
    try
    {
        using (var database = new LiteDB.LiteDatabase(path))
        {
            var store = new CombatStore(database);
            store.Insert(Row("inside", "p", "Player"));
            var boundary = Row("boundary", "p", "Tomorrow"); boundary.TimestampUtc = U("2026-03-28T00:00:00Z");
            store.Insert(boundary);
            using var scheduler = new CombatReports(true, false, "Europe/Dublin", 5,
                (world, start, end) => Snapshot(store.Query(world, start, end), world), store.TryClaimReport, report =>
                {
                    attempts++;
                    attemptedReport = report;
                    throw new IOException("injected transport failure");
                }, warnings.Add);
            scheduler.Tick(U("2026-03-27T12:00:00Z"), "world");
            scheduler.Tick(U("2026-03-28T00:00:00Z"), "world");
        }
        using (var reopened = new LiteDB.LiteDatabase(path))
        {
            var store = new CombatStore(reopened);
            using var scheduler = new CombatReports(true, false, "Europe/Dublin", 5,
                (world, start, end) => Snapshot(store.Query(world, start, end), world), store.TryClaimReport, sends.Add, warnings.Add);
            // Replaying the same boundary tests the durable claim, not merely the new startup baseline.
            scheduler.Tick(U("2026-03-27T12:00:00Z"), "world");
            scheduler.Tick(U("2026-03-28T00:00:00Z"), "world");
            Check(sends.Count == 0);
            using var changedZone = new CombatReports(true, false, "UTC", 5,
                (world, start, end) => Snapshot(store.Query(world, start, end), world), store.TryClaimReport, sends.Add, warnings.Add);
            changedZone.Tick(U("2026-03-27T12:00:00Z"), "world");
            changedZone.Tick(U("2026-03-28T00:00:00Z"), "world");
            Check(sends.Count == 1, "timezone must be part of checkpoint identity");
        }
        Check(attempts == 1 && warnings.Count == 1);
        Check(attemptedReport != null && attemptedReport.Contains("Player: 1") && !attemptedReport.Contains("Tomorrow"));
    }
    finally
    {
        File.Delete(path);
        File.Delete(Path.ChangeExtension(path, null) + "-log.db");
    }
});
Test("sailing meters and kilometers rank by distance, not time or span count", () =>
{
    var snapshot = new CombinedReportSnapshot("world", new Dictionary<ReportMetric, IReadOnlyList<ReportRow>> {
        [ReportMetric.SailingDistance] = new[] {
            new ReportRow("slow", "Short trip", 100, 100, 0, daily.StartUtc, meters: 25.5),
            new ReportRow("far", "Long trip", 1, 1, 0, daily.StartUtc, meters: 1250) }
    });
    var report = CombatReports.BuildReport(snapshot, daily, "UTC", 5, Array.Empty<string>())!;
    Check(report.Sections.Single() == (headings[ReportMetric.SailingDistance], "1. Long trip: 1.25 km\n2. Short trip: 25.5 m"));
    Check(report.Description == "" && report.Footer == "Server-observed; some counts are partial.");
});
Test("creative headers share medal and numbered rows with embed fields", () =>
{
    var snapshot = new CombinedReportSnapshot("world", new Dictionary<ReportMetric, IReadOnlyList<ReportRow>> {
        [ReportMetric.ReportedKillCredits] = new[] {
            new ReportRow("a", "Logan", 1234, 0, 0, daily.StartUtc),
            new ReportRow("b", "Brisingr", 567, 0, 0, daily.StartUtc),
            new ReportRow("c", "Ragnar", 89, 0, 0, daily.StartUtc),
            new ReportRow("d", "Bjorn", 44, 0, 0, daily.StartUtc),
            new ReportRow("e", "Ulf", 3, 0, 0, daily.StartUtc) }
    });
    var model = CombatReports.BuildReport(snapshot, daily, "UTC", 5, Array.Empty<string>())!;
    const string rows = "1. Logan: 1,234\n2. Brisingr: 567\n3. Ragnar: 89\n4. Bjorn: 44\n5. Ulf: 3";
    Check(model.Sections.Single() == ("⚔️ The Berserker — kills", rows));
    Check(model.PlainText.Contains("**⚔️ The Berserker** — kills\n" + rows + "\n"));
    var payload = ReportPayload(model, true);
    Check((string)payload["embeds"]![0]!["fields"]![0]!["name"]! == "⚔️ The Berserker — kills");
    Check((string)payload["embeds"]![0]!["fields"]![0]!["value"]! == rows);
});
Test("structured report boundary is available without parsing Markdown", () =>
{
    var build = typeof(CombatReports).GetMethod("BuildReport", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
    Check(build != null, "BuildReport must construct the immutable report in the rendering pass");
});
Test("daily and weekly reports use short dates, preview only for manual, and one footer", () =>
{
    DateTime now = U("2026-11-03T15:17:00Z");
    var snapshot = new CombinedReportSnapshot("world", new Dictionary<ReportMetric, IReadOnlyList<ReportRow>> {
        [ReportMetric.ObservedDodgeRolls] = new[] { new ReportRow("p", "Logan", 9, 0, 0, now) },
        [ReportMetric.NewPieces] = new[] { new ReportRow("p", "Logan", 228, 0, 0, now) },
        [ReportMetric.CropsPlanted] = new[] { new ReportRow("p", "Logan", 3, 0, 0, now) }
    }, new[] { new CoverageSnapshot(ReportMetric.NewPieces, CoverageState.Partial, now, now, new Dictionary<CoverageReason, long>()) });
    string body = "**🌀 The Trickster** — dodge rolls\n1. Logan: 9\n**🔨 The Builder of Asgard** — pieces built\n1. Logan: 228\n**🌱 Hand of Freyr** — crops planted\n1. Logan: 3\n-# Server-observed; some counts are partial.";
    string today = CombatReports.Render(snapshot, CombatReports.CurrentPeriod(now, dublin, false), "Europe/Dublin", 5, new[] { "inferred floor pickups" }, manual: true)!;
    Check(today == "🏆 Daily leaderboard — Nov 3 (so far) (preview)\n" + body, "daily output: " + today);
    string week = CombatReports.Render(snapshot, CombatReports.PreviousPeriod(now, dublin, true), "Europe/Dublin", 5, Array.Empty<string>())!;
    Check(week == "🏆 Weekly leaderboard — Oct 26–Nov 1\n" + body, "weekly output: " + week);
    Check(CombatReports.BuildReport(snapshot, CombatReports.CurrentPeriod(now, dublin, true), "Europe/Dublin", 5, Array.Empty<string>())!.Title ==
        "Weekly leaderboard — Nov 2–3 (so far)", "current week title");
    Check(CombatReports.BuildReport(snapshot, CombatReports.PreviousPeriod(U("2027-01-04T12:00:00Z"), dublin, true), "Europe/Dublin", 5, Array.Empty<string>())!.Title ==
        "Weekly leaderboard — Dec 28–Jan 3", "year-crossing title");
    Console.WriteLine("SAMPLE DAILY\n" + today + "\nSAMPLE WEEKLY\n" + week);
});
Test("embed construction fallback emits a fixed warning", () =>
{
    DiscordConnectorPlugin.StaticConfig.DiscordEmbedsEnabled = true;
    DiscordConnectorPlugin.StaticLogger.Messages.Clear();
    DiscordApi.SendRenderedMessage(Webhook.Event.LeaderboardsAll, new LeaderboardReport(new string('t', 260), "context", Array.Empty<(string, string)>(), "invalid report"));
    Check(DiscordConnectorPlugin.StaticLogger.Messages.Any(m => m == "Leaderboard report embed construction failed; sending plain text."),
        "fallback must warn without exposing report data");
});
Newtonsoft.Json.Linq.JObject ReportPayload(LeaderboardReport report, bool embeds)
{
    DiscordConnectorPlugin.StaticConfig.DiscordEmbedsEnabled = embeds;
    DiscordConnectorPlugin.StaticLogger.Messages.Clear();
    DiscordApi.SendRenderedMessage(Webhook.Event.LeaderboardsAll, report);
    const string prefix = "Trying webhook with payload: ";
    return Newtonsoft.Json.Linq.JObject.Parse(DiscordConnectorPlugin.StaticLogger.Messages.Single(m => m.StartsWith(prefix)).Substring(prefix.Length));
}
void NoReportMentions(Newtonsoft.Json.Linq.JObject payload)
{
    Check(!payload["allowed_mentions"]!["parse"]!.Any() && !payload["allowed_mentions"]!["roles"]!.Any() &&
        !payload["allowed_mentions"]!["users"]!.Any() && (bool)payload["allowed_mentions"]!["replied_user"]! == false);
}
string ReportText(string title, string description, IEnumerable<(string Heading, string Value)> sections, string footer) =>
    "🏆 " + title + "\n" + (description.Length == 0 ? "" : description + "\n") +
    string.Concat(sections.Select(s => "**" + s.Heading.Replace(" — ", "** — ") + "\n" + s.Value + "\n")) + "-# " + footer;
string EmbedText(Newtonsoft.Json.Linq.JToken embed) => ReportText(((string)embed["title"]!).Substring("🏆 ".Length),
    (string)embed["description"]!, embed["fields"]!.Select(f => ((string)f["name"]!, (string)f["value"]!)), (string)embed["footer"]!["text"]!);
Test("leaderboard report uses trophy, configured color and nineteen clean full-width fields", () =>
{
    var snapshot = new CombinedReportSnapshot("private-world-id", Enum.GetValues<ReportMetric>().ToDictionary(metric => metric,
        metric => (IReadOnlyList<ReportRow>)new[] { new ReportRow("private-account-id", "**@everyone\r\n<@123>😀", 3, 4, 5, daily.StartUtc, meters: 4) }));
    foreach (bool manual in new[] { false, true })
    {
        var model = CombatReports.BuildReport(snapshot, daily, "Europe/Dublin", 16, new[] { "partial flush" }, manual)!;
        string report = model.PlainText;
        Check(report == CombatReports.Render(snapshot, daily, "Europe/Dublin", 16, new[] { "partial flush" }, manual));
        Check(model.Sections.Count == 19 && report == ReportText(model.Title, model.Description, model.Sections, model.Footer));
        Check(model.Sections.Select(section => section.Heading).SequenceEqual(new[] {
            ReportMetric.ReportedKillCredits, ReportMetric.ParticipantBossCredits, ReportMetric.Biomes,
            ReportMetric.ObservedDodgeRolls, ReportMetric.SailingDistance, ReportMetric.InferredPickups, ReportMetric.NewPieces,
            ReportMetric.ExplorationCells, ReportMetric.CookingCompletions, ReportMetric.ObservedConsumableUses,
            ReportMetric.ReportedDodgeHitNotifications, ReportMetric.CropsPlanted, ReportMetric.HealthLost,
            ReportMetric.OnFootDistance, ReportMetric.BiomeTime, ReportMetric.OnlineTime, ReportMetric.PortalTrips, ReportMetric.DamageDealt, ReportMetric.LongestBowHit }.Select(m => headings[m])));
        var payload = ReportPayload(model, true);
        NoReportMentions(payload);
        var embed = payload["embeds"]![0]!;
        var fields = embed["fields"]!;
        Check((string)embed["title"]! == "🏆 " + model.Title && (string)embed["description"]! == model.Description);
        Check(fields.Select(f => ((string)f["name"]!, (string)f["value"]!)).SequenceEqual(model.Sections));
        Check(((string)embed["title"]!).StartsWith("🏆 Daily leaderboard — Mar 27"));
        Check((int)embed["color"]! == 0x747F8D && fields.Count() == 19);
        string description = (string)embed["description"]!;
        Check(description == "" && (string)embed["footer"]!["text"]! == model.Footer);
        Check(((string)embed["title"]!).Contains(" (preview)") == manual);
        Check(!report.Contains("so far"), "a completed period is not in progress, even in a preview");
        Check(fields.All(f => (bool)f["inline"]! == false && ((string)f["name"]!).Length <= 256 &&
            ((string)f["value"]!).Length is > 0 and <= 1024));
        int total = ((string)embed["title"]!).Length + description.Length + model.Footer.Length + fields.Sum(f => ((string)f["name"]!).Length + ((string)f["value"]!).Length);
        Check(total <= 6000 && report.Length <= 1900);
        string reconstructed = EmbedText(embed);
        Check(reconstructed == report, "embed and fallback must preserve the same title, rows and footer");
        Check(!reconstructed.Contains("@everyone") && !reconstructed.Contains("<@") && !reconstructed.Contains("private-account-id"));
        Check((string)ReportPayload(model, false)["content"]! == report);
    }
});
Test("report embed construction failures fall back to exact text with mentions disabled", () =>
{
    foreach (var report in new[] {
        new LeaderboardReport(new string('t', 260), "context", Array.Empty<(string, string)>(), "literal title fallback"),
        new LeaderboardReport("Report", new string('d', 4097), Array.Empty<(string, string)>(), "literal description fallback"),
        new LeaderboardReport("Report", "", Array.Empty<(string, string)>(), "literal footer fallback", new string('f', 2049)),
        new LeaderboardReport("Report", "context", new[] { ("Field", new string('x', 1025)) }, "literal value fallback"),
        new LeaderboardReport("Report", "context", new[] { (new string('n', 257), "row") }, "literal name fallback"),
        new LeaderboardReport("Report", "context", Enumerable.Range(0, 26).Select(i => ("Field " + i, "row")), "literal count fallback"),
        new LeaderboardReport("Report", "context", new[] { ("Field", "") }, "literal empty fallback"),
        new LeaderboardReport("Report", "context", Enumerable.Range(0, 7).Select(i => ("Field " + i, new string('v', 1000))), "literal total fallback"),
        new LeaderboardReport("Report", "context", Array.Empty<(string, string)>(), new string('l', 1901)) })
    {
        var payload = ReportPayload(report, true);
        Check((string?)payload["content"] == report.PlainText && (payload["embeds"] == null || !payload["embeds"]!.Any()));
        NoReportMentions(payload);
    }
});
Test("structured sections are copied and hostile Markdown cannot create categories", () =>
{
    var sections = new List<(string Heading, string Value)> { ("Original", "row") };
    var copy = new LeaderboardReport("Title", "Description", sections, "literal is not a Markdown model");
    sections[0] = ("Changed", "changed");
    sections.Add(("Extra", "extra"));
    Check(copy.Sections.Count == 1 && copy.Sections[0] == ("Original", "row"));
    Check(((ICollection<(string, string)>)copy.Sections).IsReadOnly);
    var direct = ReportPayload(copy, true)["embeds"]![0]!;
    Check((string)direct["title"]! == "🏆 Title" && (string)direct["description"]! == "Description" &&
        (string)direct["fields"]![0]!["name"]! == "Original" && (string)direct["fields"]![0]!["value"]! == "row");
    foreach (string hostile in new[] { "\n**Fake**\nrow", "**@everyone <@123>", "`|[]()#_\\\r\n" })
    {
        var snapshot = new CombinedReportSnapshot("world", Enum.GetValues<ReportMetric>().ToDictionary(metric => metric,
            metric => (IReadOnlyList<ReportRow>)new[] { new ReportRow("secret", hostile, 1, 1, 1, daily.StartUtc, meters: 1) }));
        var report = CombatReports.BuildReport(snapshot, daily, "Europe/Dublin", 5, Array.Empty<string>())!;
        Check(report.Sections.Count == 19 && !report.Sections.Any(s => s.Heading == "Fake"));
        Check(report.PlainText == ReportText(report.Title, report.Description, report.Sections, report.Footer));
        var payload = ReportPayload(report, true);
        NoReportMentions(payload);
        Check(payload["embeds"]![0]!["fields"]!.Select(f => ((string)f["name"]!, (string)f["value"]!)).SequenceEqual(report.Sections));
    }
});
Test("each embed fallback warns and keeps exact no-mention fallback", () =>
{
    const string warning = "Leaderboard report embed construction failed; sending plain text.";
    var invalid = new LeaderboardReport(new string('t', 260), "context", new[] { ("Field", "row") }, "literal @everyone %VAR1%\n**not reparsed**");
    void Send(bool embeds, int warnings)
    {
        var payload = ReportPayload(invalid, embeds);
        Check((string)payload["content"]! == invalid.PlainText && (payload["embeds"] == null || !payload["embeds"]!.Any()), "exact literal fallback");
        NoReportMentions(payload);
        Check(DiscordConnectorPlugin.StaticLogger.Messages.Count(m => m == warning) == warnings, "one warning per embed fallback");
    }
    Send(true, 1);
    Send(true, 1);
    Send(false, 0);
});
Test("production combat payload stays literal and bounded with embeds ON/OFF", () =>
{
    var row = Row("tokens", "p", "%VAR1% %VAR2% %TIMESTAMP% @everyone <@123>");
    var model = CombatReports.BuildReport(Snapshot(new[] { row }), daily, "Europe/Dublin", 5, Array.Empty<string>())!;
    string report = model.PlainText;
    var config = DiscordConnectorPlugin.StaticConfig;
    config.UserVariable = new string('X', 4200) + " @everyone <@&456>";
    config.UserVariable1 = "**injected**";
    foreach (bool embeds in new[] { false, true })
    {
        config.DiscordEmbedsEnabled = embeds;
        DiscordConnectorPlugin.StaticLogger.Messages.Clear();
        DiscordApi.SendRenderedMessage(Webhook.Event.LeaderboardsAll, model);
        const string prefix = "Trying webhook with payload: ";
        string serialized = DiscordConnectorPlugin.StaticLogger.Messages.Single(m => m.StartsWith(prefix)).Substring(prefix.Length);
        var payload = Newtonsoft.Json.Linq.JObject.Parse(serialized);
        var embed = embeds ? payload["embeds"]![0] : null;
        string actual = embeds ? EmbedText(embed!) : (string)payload["content"]!;
        Check(actual == report, $"embeds={embeds}: expected literal report ({report.Length} chars), got {actual.Length} chars");
        Check(actual.Length <= 1900 && actual.Contains("%VAR1%") && actual.Contains("%VAR2%") && actual.Contains("%TIMESTAMP%"));
        Check(!actual.Contains("@everyone") && !actual.Contains("<@"));
        Check(!payload["allowed_mentions"]!["parse"]!.Any() && !payload["allowed_mentions"]!["roles"]!.Any() &&
            !payload["allowed_mentions"]!["users"]!.Any() && (bool)payload["allowed_mentions"]!["replied_user"]! == false);
    }
});
Test("report data diagnostics include bounded named data, coverage and empty manual/automatic results", () =>
{
    var snapshot = new CombinedReportSnapshot("SECRET-WORLD", Enum.GetValues<ReportMetric>().ToDictionary(metric => metric,
        metric => (IReadOnlyList<ReportRow>)Enumerable.Range(0, 40).Select(i => new ReportRow("SECRET-ACCOUNT-" + i,
            "Alice\r\nFORGED\t\u001b\u202e" + new string('x', 900) + "😀", 12, 34, 56, daily.StartUtc, meters: 34)).ToArray()),
        Enum.GetValues<ReportMetric>().Select(metric => new CoverageSnapshot(metric, CoverageState.Unavailable, daily.StartUtc, daily.EndUtc,
            new Dictionary<CoverageReason, long> { [CoverageReason.CapabilityLost] = 1 })));
    DiscordConnectorPlugin.StaticConfig.DebugLeaderboardOperations = true;
    foreach (bool manual in new[] { false, true })
    {
        var lines = new List<string>();
        string? expected = CombatReports.Render(snapshot, daily, "Europe/Dublin", 16, Array.Empty<string>(), manual);
        string? actual = CombatReports.Render(snapshot, daily, "Europe/Dublin", 16, Array.Empty<string>(), manual, lines.Add);
        Check(actual == expected && lines.Count == snapshot.Rows.Count + 1 && lines.All(line => line.Length <= 512));
        Check(lines[0].Contains(manual ? "mode=manual" : "mode=automatic") && lines[0].Contains("emittedFields=0") &&
            lines[0].Contains("emittedRows=0"));
        Check(manual ? actual!.Contains("No observations") : actual == null, "unavailable history stays in diagnostics, not Discord");
        Check(lines.Skip(1).All(line => line.Contains("available=16") && line.Contains("coverage=Unavailable") && line.Contains("Alice") &&
            line.Contains(line.Contains("metric=HealthLost") || line.Contains("metric=DamageDealt") ? ": 56 HP" : line.Contains("metric=SailingDistance") || line.Contains("metric=OnFootDistance") || line.Contains("metric=LongestBowHit") ? ": 34 m" : line.Contains("metric=BiomeTime") || line.Contains("metric=OnlineTime") ? ": 34 s" : ": 12")), "available named values and coverage");
        Check(lines.All(line => !line.Contains("SECRET") && !line.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format)),
            "diagnostics must not contain IDs or forged lines/terminal controls");
        Check(CombatReports.Render(snapshot, daily, "Europe/Dublin", 16, Array.Empty<string>(), manual, _ => throw new Exception("logger failed")) == expected);
        lines.Clear();
        string? empty = CombatReports.Render(new CombinedReportSnapshot("SECRET-WORLD"), daily, "Europe/Dublin", 5, Array.Empty<string>(), manual, lines.Add);
        Check(lines.Count == 1 && lines[0].Contains("emittedFields=0") && lines[0].Contains("empty=True"));
        Check(manual ? empty!.Contains("No observations") : empty == null, "empty automatic reports must remain silent");
        if (manual)
        {
            var payload = ReportPayload(CombatReports.BuildReport(new CombinedReportSnapshot("SECRET-WORLD"), daily, "Europe/Dublin", 5, Array.Empty<string>(), manual)!, true);
            NoReportMentions(payload);
            Check(((string)payload["embeds"]![0]!["description"]!).Contains("No observations") && !payload["embeds"]![0]!["fields"]!.Any());
        }
    }
    DiscordConnectorPlugin.StaticConfig.DebugLeaderboardOperations = false;
});
Test("optional report diagnostics reach scheduled and manual paths and cannot break delivery", () =>
{
    var snapshot = new CombinedReportSnapshot("world", new Dictionary<ReportMetric, IReadOnlyList<ReportRow>> {
        [ReportMetric.ReportedKillCredits] = new[] { new ReportRow("SECRET-ACCOUNT", "Alice", 12, 0, 0, daily.StartUtc) }
    });
    var lines = new List<string>();
    var sends = new List<string>();
    DiscordConnectorPlugin.StaticConfig.DebugLeaderboardOperations = false;
    Action<string> log = lines.Add;
    CombatReports.Render(snapshot, daily, "Europe/Dublin", 5, Array.Empty<string>(), diagnostic: log);
    Check(lines.Count == 0, "disabled diagnostics must not invoke the callback in Render");
    using var automatic = new CombatReports(true, false, "Europe/Dublin", 5,
        (_, _, _) => Task.FromResult(snapshot), (_, _) => Task.FromResult(true), _ => Array.Empty<string>(), report => sends.Add(report.PlainText), _ => { }, log);
    automatic.Tick(U("2026-03-27T12:00:00Z"), "world");
    automatic.Tick(U("2026-03-28T00:00:08Z"), "world");
    Check(sends.Count == 1 && lines.Count == 0);
    using var quietManual = new ManualLeaderboardReports("Europe/Dublin", 5, (_, _, _) => Task.FromResult(snapshot),
        _ => Array.Empty<string>(), _ => { }, _ => { }, log);
    Check(quietManual.Request("!dc daily", true, U("2026-03-28T12:00:00Z"), 0, "world"));
    quietManual.Poll(8, "world");
    Check(lines.Count == 0, "disabled diagnostics must not invoke the callback in manual reports");
    DiscordConnectorPlugin.StaticConfig.DebugLeaderboardOperations = true;
    automatic.Tick(U("2026-03-29T00:00:08Z"), "world");
    Check(sends.Count == 2 && lines.Any(line => line.Contains("mode=automatic")));
    using var manual = new ManualLeaderboardReports("Europe/Dublin", 5, (_, _, _) => Task.FromResult(snapshot),
        _ => Array.Empty<string>(), report => sends.Add(report.PlainText), _ => { }, log);
    Check(manual.Request("!dc daily", true, U("2026-03-28T12:00:00Z"), 0, "world"));
    manual.Poll(8, "world");
    Check(sends.Count == 3 && lines.Any(line => line.Contains("mode=manual")));
    using var throwing = new ManualLeaderboardReports("Europe/Dublin", 5, (_, _, _) => Task.FromResult(snapshot),
        _ => Array.Empty<string>(), report => sends.Add(report.PlainText), _ => { }, _ => throw new Exception("logger failed"));
    Check(throwing.Request("!dc daily", true, U("2026-03-28T12:00:00Z"), 0, "world"));
    throwing.Poll(8, "world");
    Check(sends.Count == 4);
    using var throwingAutomatic = new CombatReports(true, false, "Europe/Dublin", 5,
        (_, _, _) => Task.FromResult(snapshot), (_, _) => Task.FromResult(true), _ => Array.Empty<string>(), report => sends.Add(report.PlainText), _ => { },
        _ => throw new Exception("logger failed"));
    throwingAutomatic.Tick(U("2026-03-27T12:00:00Z"), "world");
    throwingAutomatic.Tick(U("2026-03-28T00:00:08Z"), "world");
    Check(sends.Count == 5);
    DiscordConnectorPlugin.StaticConfig.DebugLeaderboardOperations = false;
});
Test("legacy SendMessage still expands templates only in its existing embed path", () =>
{
    var config = DiscordConnectorPlugin.StaticConfig;
    config.UserVariable = "configured legacy value";
    foreach (bool embeds in new[] { false, true })
    {
        config.DiscordEmbedsEnabled = embeds;
        DiscordConnectorPlugin.StaticLogger.Messages.Clear();
        DiscordApi.SendMessage(Webhook.Event.LeaderboardsAll, "%VAR1%");
        const string prefix = "Trying webhook with payload: ";
        string serialized = DiscordConnectorPlugin.StaticLogger.Messages.Single(m => m.StartsWith(prefix)).Substring(prefix.Length);
        var payload = Newtonsoft.Json.Linq.JObject.Parse(serialized);
        string actual = (string)(embeds ? payload["embeds"]![0]!["description"]! : payload["content"]!)!;
        Check(actual == (embeds ? "configured legacy value" : "%VAR1%"));
        Check(payload["allowed_mentions"]!["parse"]!.Any(), "legacy mention settings must remain unchanged");
    }
});
Test("Santiago midnight gap resolves to contiguous half-open daily and weekly periods", () =>
{
    var zone = TimeZoneInfo.FindSystemTimeZoneById("America/Santiago");
    Check(zone.IsInvalidTime(new DateTime(2026, 9, 6)), "fixture must include nonexistent midnight");
    var before = CombatReports.PreviousPeriod(U("2026-09-06T12:00:00Z"), zone, false);
    var gap = CombatReports.PreviousPeriod(U("2026-09-07T12:00:00Z"), zone, false);
    var after = CombatReports.PreviousPeriod(U("2026-09-08T12:00:00Z"), zone, false);
    Check(before.EndUtc == U("2026-09-06T04:00:00Z") && before.EndUtc == gap.StartUtc);
    Check(gap.EndUtc == U("2026-09-07T03:00:00Z") && gap.EndUtc == after.StartUtc);
    Check((gap.EndUtc - gap.StartUtc).TotalHours == 23);
    var week = CombatReports.PreviousPeriod(U("2026-09-07T12:00:00Z"), zone, true);
    var nextWeek = CombatReports.PreviousPeriod(U("2026-09-14T12:00:00Z"), zone, true);
    Check((week.EndUtc - week.StartUtc).TotalHours == 167 && week.EndUtc == nextWeek.StartUtc);
});
Test("ambiguous Havana midnight uses its earlier UTC occurrence without overlap", () =>
{
    var zone = TimeZoneInfo.FindSystemTimeZoneById("America/Havana");
    Check(zone.IsAmbiguousTime(new DateTime(2026, 11, 1)), "fixture must include repeated midnight");
    var before = CombatReports.PreviousPeriod(U("2026-11-01T12:00:00Z"), zone, false);
    var repeated = CombatReports.PreviousPeriod(U("2026-11-02T12:00:00Z"), zone, false);
    Check(before.EndUtc == U("2026-11-01T04:00:00Z") && before.EndUtc == repeated.StartUtc);
    Check((repeated.EndUtc - repeated.StartUtc).TotalHours == 25);
});
Test("Santiago daily and weekly scheduling continues through the midnight gap", () =>
{
    Reset();
    using var scheduler = Scheduler(true, "America/Santiago");
    scheduler.Tick(U("2026-09-05T12:00:00Z"), "world");
    scheduler.Tick(U("2026-09-06T04:00:00Z"), "world");
    scheduler.Tick(U("2026-09-07T03:00:00Z"), "world");
    scheduler.Tick(U("2026-09-08T03:00:00Z"), "world");
    Check(sends.Count == 4 && warnings.Count == 0, $"expected 4 reports/no warnings, got {sends.Count}/{warnings.Count}");
    Check(queries[0].End == queries[1].Start && queries[1].End == queries[3].Start);
    scheduler.Tick(U("2026-09-14T03:00:00Z"), "world");
    Check(sends.Count == 6 && warnings.Count == 0 && queries[2].End == queries[5].Start);
});
Test("repeated midnight cannot duplicate a daily report and weekly reporting continues", () =>
{
    Reset();
    using var scheduler = Scheduler(true, "America/Havana");
    scheduler.Tick(U("2026-10-31T12:00:00Z"), "world");
    scheduler.Tick(U("2026-11-01T04:00:00Z"), "world");
    Check(sends.Count == 1 && warnings.Count == 0);
    scheduler.Tick(U("2026-11-01T05:00:00Z"), "world");
    Check(sends.Count == 1 && warnings.Count == 0);
    scheduler.Tick(U("2026-11-02T05:00:00Z"), "world");
    Check(sends.Count == 3 && warnings.Count == 0 && queries[0].End == queries[1].Start);
});
Test("native signal flags default off and each enables only its own independent metric", () =>
{
    foreach (var (key, metric) in new[] {
        ("Server-Reported Combat/Kill Credits Enabled", ReportMetric.ReportedKillCredits),
        ("Server-Reported Combat/Dodge Hit Notifications Enabled", ReportMetric.ReportedDodgeHitNotifications),
        ("Server-Observed Actions/Consumable Uses Enabled", ReportMetric.ObservedConsumableUses)
    })
    {
        var file = new ConfigFile(); file.Values[key] = true;
        var config = new ObservedLeaderboardsConfig(file);
        var activities = new ActivityConfig(file);
        Check(Enum.GetValues<ReportMetric>().Where(config.Snapshot(activities).Includes).SequenceEqual(new[] { metric }));
        var off = new ConfigFile(); off.Values[key] = false;
        Check(Enum.GetValues<ReportMetric>().All(m => !new ObservedLeaderboardsConfig(off).Snapshot(new ActivityConfig(off)).Includes(m)));
    }
});
Test("crop planting flag enables only observed crop pieces without combat or grid prerequisites", () =>
{
    var file = new ConfigFile(); file.Values["Server-Observed Activities/Crops Planted Enabled"] = true;
    var activities = new ActivityConfig(file);
    var observed = new ObservedLeaderboardsConfig(file);
    Check(Enum.GetValues<ReportMetric>().Where(observed.Snapshot(activities).Includes).SequenceEqual(new[] { ReportMetric.CropsPlanted }));
});
Test("health loss matches sailing defaults, has independent capture, and ranks rounded HP", () =>
{
    var file = new ConfigFile();
    var defaults = new ActivityConfig(file);
    Check(!defaults.HealthLost && defaults.HealthLost == defaults.Sailing);
    file.Values["Server-Observed Activities/Health Lost Enabled"] = true;
    var activities = new ActivityConfig(file);
    var observed = new ObservedLeaderboardsConfig(file);
    Check(Enum.GetValues<ReportMetric>().Where(observed.Snapshot(activities).Includes).SequenceEqual(new[] { ReportMetric.HealthLost }));
    var snapshot = new CombinedReportSnapshot("world", new Dictionary<ReportMetric, IReadOnlyList<ReportRow>> {
        [ReportMetric.HealthLost] = new[] {
            new ReportRow("small", "Small", 100, 0, 5.4, daily.StartUtc),
            new ReportRow("large", "Large", 1, 0, 13.6, daily.StartUtc) }
    });
    var model = CombatReports.BuildReport(snapshot, daily, "UTC", 5, Array.Empty<string>())!;
    Check(model.Sections.Single() == ("🩸 Baldr's Fate — health lost", "1. Large: 14 HP\n2. Small: 5 HP"));
    Check(model.PlainText.Contains("**🩸 Baldr's Fate** — health lost\n1. Large: 14 HP"));
    Check((string)ReportPayload(model, true)["embeds"]![0]!["fields"]![0]!["name"]! == headings[ReportMetric.HealthLost]);
});
Test("all thirteen typed sections, own flags, unavailable history and signal/completion/crop/health units", () =>
{
    var defaults = new ConfigFile();
    var defaultOptions = new ObservedLeaderboardsConfig(defaults).Snapshot(new ActivityConfig(defaults));
    Check(Enum.GetValues<ReportMetric>().All(metric => !defaultOptions.Includes(metric)));
    var file = new ConfigFile();
    foreach (string key in new[] { "Sailing", "Floor Pickups", "Construction", "Exploration", "Cooking Completions" })
        file.Values["Server-Observed Activities/" + key + " Enabled"] = true;
    foreach (string key in new[] { "Participation" }) file.Values["Server-Observed Combat/" + key + " Enabled"] = true;
    foreach (string key in new[] { "Dodge Rolls" }) file.Values["Server-Observed Actions/" + key + " Enabled"] = true;
    var oldOnly = new ObservedLeaderboardsConfig(file).Snapshot(new ActivityConfig(file));
    Check(!oldOnly.ReportedKillCredits && !oldOnly.ConsumableUses && !oldOnly.ReportedDodgeHits && !oldOnly.CropsPlanted && !oldOnly.HealthLost, "existing flags must not automatically enable new metrics");
    file.Values["Server-Observed Activities/Crops Planted Enabled"] = true;
    file.Values["Server-Observed Activities/Health Lost Enabled"] = true;
    file.Values["Server-Reported Combat/Kill Credits Enabled"] = true;
    file.Values["Server-Observed Actions/Consumable Uses Enabled"] = true;
    file.Values["Server-Reported Combat/Dodge Hit Notifications Enabled"] = true;
    var activities = new ActivityConfig(file);
    var observed = new ObservedLeaderboardsConfig(file);
    LeaderboardsOptions inclusion = observed.Snapshot(activities);
    var existingMetrics = Enum.GetValues<ReportMetric>().Where(metric => metric < ReportMetric.ReportedKillCredits).ToArray();
    Check(Enum.GetValues<ReportMetric>().Where(metric => (int)metric <= 20).All(inclusion.Includes));
    Check(!inclusion.OnFoot && !inclusion.BiomeTime && !inclusion.OnlineTime && !inclusion.PortalTrips);
    Check(existingMetrics.All(inclusion.Includes));
    // The cooking flag neither enables other activities nor inherits the combat master.
    var onlyCooking = new ConfigFile(); onlyCooking.Values["Cooking Completions Enabled"] = true;
    Check(Enum.GetValues<ReportMetric>().Where(new ObservedLeaderboardsConfig(onlyCooking).Snapshot(new ActivityConfig(onlyCooking)).Includes)
        .SequenceEqual(new[] { ReportMetric.CookingCompletions }));

    string path = Path.Combine(Path.GetTempPath(), "combined-reporting-" + Guid.NewGuid().ToString("N") + ".db");
    try
    {
        using var database = new LiteDB.LiteDatabase(path);
        var legacy = new CombatStore(database);
        legacy.Insert(Row("legacy", "legacy", "LegacyOnly"));
        var loss = new ObservationLoss(_ => { }, "reporting");
        using var store = new ActivityStore(database, legacy, new PlayerActionStore(database), new CookingStore(database), _ => { }, observationLoss: loss);
        DateTime when = daily.StartUtc.AddHours(12);
        var actor = new ActivityActor("p", "%VAR1% @everyone **", "connection", when);
        var signalActor = new ActivityActor("signal", "**@everyone😀<@123>\\_", "signal-session", when);
        CookingSlotSnapshot Slot(string station) => new("world", station, 0, 1, "RawMeat", CookingStatus.NotDone, 0, "epoch");
        var conversion = new CookingConversion("CookedMeat", 7);
        Check(store.Enqueue(new ActivityBatch(cooking: new[] {
            new CookingChange(CookingChangeKind.AcceptedInput, Slot("station"), "generation", store.CookingObservationEpoch, 1, actor, daily.StartUtc.AddMinutes(-1)),
            new CookingChange(CookingChangeKind.AcceptedInput, Slot("boundary"), "boundary", store.CookingObservationEpoch, 2, actor, when)
        })));
        CookingSlotSnapshot Done(string station) => new("world", station, 0, 1, "RawMeat", CookingStatus.Done, 10, "epoch");
        Check(store.Enqueue(new ActivityBatch(
            actions: new[] { new PlayerActionRecord("world", actor, PlayerActionKind.DodgeRoll, "roll", when) },
            activities: new[] {
                new ActivityRecord(ActivityKind.BiomeVisited, "world", actor, "Meadows", "day", when, "Meadows"),
                new ActivityRecord(ActivityKind.BiomeVisited, "world", actor, "BlackForest", "day", when, "BlackForest"),
                new ActivityRecord(ActivityKind.BossCredit, "world", actor, "defeated_eikthyr", "boss", when, "defeated_eikthyr"),
                new ActivityRecord(ActivityKind.BossCredit, "world", new ActivityActor("p2", "Twin", "c2", when), "defeated_eikthyr", "boss", when, "defeated_eikthyr"),
                new ActivityRecord(ActivityKind.BossCredit, "world", new ActivityActor("p3", "Twin", "c3", when), "defeated_eikthyr", "boss", when, "defeated_eikthyr"),
                new ActivityRecord(ActivityKind.Sailing, "world", actor, "ship", "span", when, startUtc: when.AddSeconds(-1), seconds: 1, meters: 12),
                new ActivityRecord(ActivityKind.InferredPickup, "world", actor, "item", "pickup", when, "Wood", 9,
                    provenance: ItemProvenance.ProvenFreshSource, sourceToken: "source", sourceUnits: 9),
                new ActivityRecord(ActivityKind.NewPiece, "world", actor, "piece", "piece", when, "wood_floor"),
                new ActivityRecord(ActivityKind.CropPlanted, "world", actor, "crop", "crop", when, "sapling_carrot"),
                new ActivityRecord(ActivityKind.HealthLost, "world", actor, "character", "health", when, hpLoss: 8.8)
            },
            chunks: new[] { new ExplorationChunk("world", actor, new ExplorationGrid(32, 1, 2), 0, 0, new[] { new ExplorationCell(1, when) }) },
            cooking: new[] {
                new CookingChange(CookingChangeKind.Completed, Done("station"), "generation", store.CookingObservationEpoch, 1, null, when, conversion),
                new CookingChange(CookingChangeKind.Completed, Done("boundary"), "boundary", store.CookingObservationEpoch, 2, null, daily.EndUtc, conversion)
            },
            coverage: new[] {
                new CoverageUpdate("world", ReportMetric.CookingCompletions, CoverageState.Unavailable, when, CoverageReason.CapabilityLost),
                new CoverageUpdate("world", ReportMetric.ReportedKillCredits, CoverageState.Unavailable, when, CoverageReason.CapabilityLost),
                new CoverageUpdate("world", ReportMetric.ObservedConsumableUses, CoverageState.Partial, when, CoverageReason.Restart),
                new CoverageUpdate("world", ReportMetric.ReportedDodgeHitNotifications, CoverageState.Unavailable, when, CoverageReason.CapabilityLost)
            },
            signals: new[] {
                new ReportedSignalRecord(ReportedSignalKind.KillCredit, "world", signalActor, "credit-1", when),
                new ReportedSignalRecord(ReportedSignalKind.KillCredit, "world", signalActor, "credit-2", when),
                new ReportedSignalRecord(ReportedSignalKind.ConsumableUse, "world", signalActor, "eat-1", when),
                new ReportedSignalRecord(ReportedSignalKind.ConsumableUse, "world", signalActor, "eat-2", when),
                new ReportedSignalRecord(ReportedSignalKind.ConsumableUse, "world", signalActor, "eat-3", when),
                new ReportedSignalRecord(ReportedSignalKind.DodgeHitNotification, "world", signalActor, "dodge-1", when),
                new ReportedSignalRecord(ReportedSignalKind.DodgeHitNotification, "world", signalActor, "dodge-1", when),
                new ReportedSignalRecord(ReportedSignalKind.DodgeHitNotification, "world", signalActor, "dodge-2", when)
            }, signalPrincipal: new ObservationPrincipal("signal", "signal-session", false))));
        loss.Record("world", ReportMetric.ReportedKillCredits, ObservationRejection.QueueFull, 3);
        loss.RecordUncertain("world", ReportMetric.ReportedKillCredits, 2);
        var absolute = loss.Snapshot("world", ReportMetric.ReportedKillCredits)!;
        store.ReconcileLossAsync(absolute).GetAwaiter().GetResult();
        store.ReconcileLossAsync(absolute).GetAwaiter().GetResult();
        var snapshot = store.QueryAsync("world", daily.StartUtc, daily.EndUtc, inclusion).GetAwaiter().GetResult();
        var diagnostic = snapshot.Coverage.Single(note => note.Metric == ReportMetric.ReportedKillCredits);
        Check(diagnostic.KnownLosses == 3 && diagnostic.UncertainWrites == 2 && diagnostic.Counters[CoverageReason.CapabilityLost] == 1,
            "cumulative rejections and uncertain outcomes remain separate from historical coverage, without duplicate aggregation");
        Check(snapshot.Rows.Count == 13 && snapshot.Rows[ReportMetric.CookingCompletions].Single().Count == 7,
            "completion time, not accepted-input time or the exclusive end, selects output units");
        Check(snapshot.Rows[ReportMetric.ParticipantBossCredits].Sum(row => row.Count) == 3, "boss-key cohorts retain each credited player");
        Check(snapshot.Rows[ReportMetric.Biomes].Single().Count == 2, "biomes count distinct player visits");
        Check(snapshot.Rows[ReportMetric.InferredPickups].Single().ProvenFreshCount == 9);
        Check(snapshot.Rows[ReportMetric.ReportedKillCredits].Single().Count == 2 &&
            snapshot.Rows[ReportMetric.ObservedConsumableUses].Single().Count == 3, "signals are separate counts, not deaths or food/output units");
        Check(snapshot.Rows[ReportMetric.ReportedDodgeHitNotifications].Single().Count == 2, "dedup envelopes, not dodges/attacks/rewards");
        var dodgeOnly = store.QueryAsync("world", daily.StartUtc, daily.EndUtc, new(reportedDodgeHits: true)).GetAwaiter().GetResult();
        Check(dodgeOnly.Rows.Keys.SequenceEqual(new[] { ReportMetric.ReportedDodgeHitNotifications }));
        Check(CombatReports.Render(dodgeOnly, daily, "Europe/Dublin", 5, Array.Empty<string>()) == null,
            "unavailable history is omitted from Discord without deleting stored counts");
        Check(store.QueryAsync("world", daily.StartUtc, daily.EndUtc, oldOnly).GetAwaiter().GetResult().Rows.Count == 8);
        foreach (var (key, metric) in new[] {
            ("Server-Reported Combat/Kill Credits Enabled", ReportMetric.ReportedKillCredits),
            ("Server-Reported Combat/Dodge Hit Notifications Enabled", ReportMetric.ReportedDodgeHitNotifications),
            ("Server-Observed Actions/Consumable Uses Enabled", ReportMetric.ObservedConsumableUses),
            ("Server-Observed Activities/Crops Planted Enabled", ReportMetric.CropsPlanted),
            ("Server-Observed Activities/Health Lost Enabled", ReportMetric.HealthLost)
        })
        {
            file.Values[key] = false;
            var flags = new ObservedLeaderboardsConfig(file).Snapshot(new ActivityConfig(file));
            var hidden = store.QueryAsync("world", daily.StartUtc, daily.EndUtc, flags).GetAwaiter().GetResult();
            Check(hidden.Rows.Count == 12 && !hidden.Rows.ContainsKey(metric) &&
                hidden.Rows.All(pair => pair.Value.Sum(row => row.Count) == snapshot.Rows[pair.Key].Sum(row => row.Count)),
                "disabling one signal flag must hide only its history, not any other section");
            file.Values[key] = true;
        }
        Check(!snapshot.Rows.Values.SelectMany(rows => rows).Any(row => row.Name == "LegacyOnly"));
        var model = CombatReports.BuildReport(snapshot, daily, "Europe/Dublin", 5, new[] { "simulation unavailable now" })!;
        string report = model.PlainText;
        Check(report.Length <= 1900 && model.Sections.Count == 10, "ten available sections, no competing legacy boards");
        Check(report.Contains("**🌱 Hand of Freyr** — crops planted") && report.EndsWith("-# Server-observed; some counts are partial."));
        Check(!report.Contains('†') && !report.Contains('‡') && !report.Contains("World-lifetime") && !report.Contains("Limits:") &&
            !report.Contains("client-originated") && !report.Contains("not seed debit") && !report.Contains("fresh="), "no audit notes in Discord");
        var later = store.QueryAsync("world", daily.EndUtc.AddDays(1), daily.EndUtc.AddDays(2), inclusion).GetAwaiter().GetResult();
        Check(later.Coverage.Single(c => c.Metric == ReportMetric.ReportedKillCredits).KnownLosses == 3,
            "diagnostics are world-lifetime, not selected-period counts");
        string emptyPreview = CombatReports.Render(later, daily, "Europe/Dublin", 5, Array.Empty<string>(), manual: true)!;
        Check(emptyPreview.Contains("No observations") && !emptyPreview.Contains("World-lifetime"), "empty previews stay player-facing");
        Check(!report.Contains("**⚔️ The Berserker**") && report.Contains("**🍖 The Einherjar Feaster**"));
        Check(report.Contains("\\*\\*@\u200b") && !report.Contains("@everyone") && !report.Contains("<@"));
        string preview = CombatReports.Render(snapshot, daily, "Europe/Dublin", 5, Array.Empty<string>(), manual: true)!;
        Check(preview.Length <= 1900 && preview.Contains(" (preview)") && !preview.Contains("so far") &&
            !preview.Contains("**⚔️ The Berserker**") && preview.Contains("**🍖 The Einherjar Feaster**"));
        Check(!report.Contains("**🍲 The Andhrimnir**") && !report.Contains("**💥 The Berserker**"));
        Check(report.Contains("**🎒 The Fafnir**") && !report.Contains(";fresh="));
        var unknownPickup = new CombinedReportSnapshot("world", new Dictionary<ReportMetric, IReadOnlyList<ReportRow>> {
            [ReportMetric.InferredPickups] = new[] { new ReportRow("p", "Unknown", 9, 0, 0, when) }
        });
        string unknownReport = CombatReports.Render(unknownPickup, daily, "Europe/Dublin", 5, Array.Empty<string>())!;
        Check(unknownReport.Contains("1. Unknown: 9") && !unknownReport.Contains(";fresh="), "pickup rows use the same plain counts");
        Check(report.Contains("some counts are partial") && !report.Contains("LegacyOnly") && !report.Contains("Creature kills"));
        Check(CombatReports.Render(store.QueryAsync("world", daily.StartUtc, daily.EndUtc, new LeaderboardsOptions()).GetAwaiter().GetResult(), daily, "Europe/Dublin", 5, Array.Empty<string>()) == null);
        Check(CombatReports.Render(store.QueryAsync("world", daily.EndUtc.AddDays(1), daily.EndUtc.AddDays(2), inclusion).GetAwaiter().GetResult(), daily, "Europe/Dublin", 5, new[] { "unavailable" }) == null);

        var huge = new CombinedReportSnapshot("world", Enum.GetValues<ReportMetric>().ToDictionary(metric => metric,
            metric => (IReadOnlyList<ReportRow>)Enumerable.Range(0, 16).Select(i => new ReportRow(i.ToString("D2"),
                "**_\\@everyone <@123> 😀" + new string('x', 1000), long.MaxValue, double.MaxValue, double.MaxValue, when, long.MaxValue, double.MaxValue)).ToArray()),
            Enum.GetValues<ReportMetric>().Select(metric => new CoverageSnapshot(metric, CoverageState.Partial, when, when,
                new Dictionary<CoverageReason, long> { [CoverageReason.CapabilityLost] = 1 },
                new Dictionary<ObservationRejection, long> { [ObservationRejection.QueueFull] = long.MaxValue }, long.MaxValue)));
        string bounded = CombatReports.Render(huge, CombatReports.PreviousPeriod(U("2026-03-30T12:00:00Z"), dublin, true), new string('z', 1000), 16,
            new[] { "@everyone\\😀" + new string('x', 1000) })!;
        Check(bounded.Length <= 1900 && bounded.Split('\n').Count(line => line.StartsWith("**")) == 19);
        string boundedPreview = CombatReports.Render(huge, CombatReports.CurrentPeriod(U("2026-10-25T01:30:00Z"), dublin, true),
            new string('z', 1000), 16, new[] { "@everyone\\😀" + new string('x', 1000) }, manual: true)!;
        Check(boundedPreview.Length <= 1900 && boundedPreview.Split('\n').Count(line => line.StartsWith("**")) == 19 &&
            boundedPreview.EndsWith("-# Server-observed; some counts are partial."));
        Console.WriteLine($"BUDGET mixed={report.Length} preview={preview.Length} worst-weekly={bounded.Length} worst-preview={boundedPreview.Length} UTF-16 units; maximum=1900");
        Check(bounded.Contains("9,223,372,036,854,775,807") && !bounded.Contains("World-lifetime") && !bounded.Contains('‡'));
        Check(bounded.Split('\n').Where(line => line.StartsWith("**"))
            .All(line => line.Length - 4 <= 256), "typed heading exceeds the embed field-name limit");
        Check(bounded == CombatReports.Render(huge, CombatReports.PreviousPeriod(U("2026-03-30T12:00:00Z"), dublin, true), new string('z', 1000), 16,
            new[] { "@everyone\\😀" + new string('x', 1000) }));
        Check(bounded.Contains("\\*\\*\\_\\\\") && !bounded.Contains("@everyone") && !bounded.Contains("<@") &&
            System.Text.Encoding.UTF8.GetString(System.Text.Encoding.UTF8.GetBytes(bounded)) == bounded);
        var tinyDamage = new CombinedReportSnapshot("world", new Dictionary<ReportMetric, IReadOnlyList<ReportRow>> {
            [ReportMetric.SailingDistance] = new[] { new ReportRow("s", "Sailor😀", 1, 0, 0, when, meters: double.Epsilon) }
        });
        Check(CombatReports.Render(tinyDamage, daily, "Europe/Dublin", 1, Array.Empty<string>())!.Contains("Sailor😀: ≈4.9E-324"),
             "even a positive subnormal distance must not become zero");
        var surrogate = new CombinedReportSnapshot("world", new Dictionary<ReportMetric, IReadOnlyList<ReportRow>> {
            [ReportMetric.ReportedKillCredits] = new[] { new ReportRow("p", new string('x', 14) + "😀\ud800", 1, 0, 0, when) }
        });
        string safeSurrogate = CombatReports.Render(surrogate, daily, "Europe/Dublin", 1, Array.Empty<string>())!;
        Check(System.Text.Encoding.UTF8.GetString(System.Text.Encoding.UTF8.GetBytes(safeSurrogate)) == safeSurrogate);
        // The native path also uses the existing literal transport, with no variable interpolation or mentions.
        DiscordConnectorPlugin.StaticConfig.DiscordEmbedsEnabled = false;
        DiscordConnectorPlugin.StaticLogger.Messages.Clear();
        DiscordApi.SendRenderedMessage(Webhook.Event.LeaderboardsAll, model);
        string payload = DiscordConnectorPlugin.StaticLogger.Messages.Single(m => m.StartsWith("Trying webhook with payload: ")).Substring("Trying webhook with payload: ".Length);
        Check((string)Newtonsoft.Json.Linq.JObject.Parse(payload)["content"]! == report && report.Contains("%VAR1%"));
    }
    finally { File.Delete(path); File.Delete(Path.ChangeExtension(path, null) + "-log.db"); }
});
Test("nineteen worst-case first rows fit before extra ranks with readable names and grouped counts", () =>
{
    DateTime when = daily.StartUtc;
    var snapshot = new CombinedReportSnapshot("world", Enum.GetValues<ReportMetric>().ToDictionary(metric => metric,
        metric => (IReadOnlyList<ReportRow>)Enumerable.Range(0, 16).Select(i => new ReportRow(i.ToString("D2"),
            "**_\\@everyone <@123> 😀" + new string('x', 1000), long.MaxValue, double.MaxValue, double.MaxValue, when, long.MaxValue, double.MaxValue, biome: "BlackForest")).ToArray()),
        Enum.GetValues<ReportMetric>().Select(metric => new CoverageSnapshot(metric, CoverageState.Partial, when, when,
            new Dictionary<CoverageReason, long> { [CoverageReason.CapabilityLost] = 1 },
            new Dictionary<ObservationRejection, long> { [ObservationRejection.QueueFull] = long.MaxValue }, long.MaxValue)));
    foreach (bool manual in new[] { false, true })
    {
        var model = CombatReports.BuildReport(snapshot, CombatReports.CurrentPeriod(U("2026-10-25T01:30:00Z"), dublin, true),
            new string('z', 1000), 16, new[] { "@everyone\\😀" + new string('x', 1000) }, manual)!;
        string report = model.PlainText;
        Check(report.Length <= 1900 && model.Sections.Count == 19 && model.Sections.All(section => section.Value.Length > 0));
        Console.WriteLine($"BUDGET nineteen-with-biome manual={manual} units={report.Length}; maximum=1900");
        Check(model.Sections.All(section => section.Value.StartsWith("1. ") && section.Value.Contains(": ")), "reserve a ranked first row in every section");
        Check(model.Sections.All(section => section.Value.Contains('…')), "shorten oversized names to preserve all titles and exact values");
        Check(report.Contains("9,223,372,036,854,775,807") && !report.Contains("World-lifetime") && !report.Contains('‡'));
        var payload = ReportPayload(model, true);
        NoReportMentions(payload);
        var embed = payload["embeds"]![0]!;
        Check(embed["fields"]!.Count() == 19 && embed["fields"]!.All(field =>
            ((string)field["name"]!).Length <= 256 && ((string)field["value"]!).Length <= 1024 && (bool)field["inline"]! == false));
        Check(((string)embed["title"]!).Length + ((string)embed["description"]!).Length + model.Footer.Length +
            embed["fields"]!.Sum(field => ((string)field["name"]!).Length + ((string)field["value"]!).Length) <= 6000);
    }
});
Test("ninety UTC days preserve current and previous Dublin day/week signals through DST and grace", () =>
{
    foreach (DateTime now in new[] { U("2026-03-29T23:00:07Z"), U("2026-10-26T00:00:07Z"), U("2026-03-29T01:30:07Z"), U("2026-10-25T01:30:07Z") })
    {
        string directory = Path.Combine(Path.GetTempPath(), "vdc-retention-dst-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var db = new LiteDB.LiteDatabase(Path.Combine(directory, "records.db"));
            using var store = new ActivityStore(db, new CombatStore(db), new PlayerActionStore(db), new CookingStore(db), _ => { });
            int token = 0;
            foreach (bool weekly in new[] { false, true })
                foreach (bool current in new[] { false, true })
                {
                    var period = current ? CombatReports.CurrentPeriod(now, dublin, weekly) : CombatReports.PreviousPeriod(now, dublin, weekly);
                    var actor = new ActivityActor("actor", "Recent", "connection", period.StartUtc);
                    Check(store.Enqueue(new ActivityBatch(signals: Enum.GetValues<ReportedSignalKind>().Select(kind =>
                        new ReportedSignalRecord(kind, "world", actor, "recent-" + token++, period.StartUtc)).ToArray(), signalPrincipal: new("actor", "connection", false))));
                    var before = store.QueryAsync("world", period.StartUtc, period.EndUtc, new(reportedKillCredits: true, consumableUses: true, reportedDodgeHits: true)).GetAwaiter().GetResult();
                    store.MaintainSignalRetention(now, token * 60d).GetAwaiter().GetResult();
                    var after = store.QueryAsync("world", period.StartUtc, period.EndUtc, new(reportedKillCredits: true, consumableUses: true, reportedDodgeHits: true)).GetAwaiter().GetResult();
                    Check(after.Rows.Count == 3 && before.Rows.All(pair => pair.Value.Sum(row => row.Count) == after.Rows[pair.Key].Sum(row => row.Count)), "all three recent signal kinds retained");
                }
        }
        finally { Directory.Delete(directory, true); }
    }
});
Test("async reports poll bounded work after seven-second flush grace and claim before send", () =>
{
    Reset();
    DateTime midnight = U("2026-03-28T00:00:00Z");
    var snapshot = new CombinedReportSnapshot("world", new Dictionary<ReportMetric, IReadOnlyList<ReportRow>> {
        [ReportMetric.CookingCompletions] = new[] { new ReportRow("p", "Player", 7, 0, 0, midnight.AddHours(-1)) },
        [ReportMetric.ReportedKillCredits] = new[] { new ReportRow("p", "Player", 2, 0, 0, midnight.AddHours(-1)) },
        [ReportMetric.ObservedConsumableUses] = new[] { new ReportRow("p", "Player", 3, 0, 0, midnight.AddHours(-1)) }
    });
    var query = new TaskCompletionSource<CombinedReportSnapshot>();
    var claim = new TaskCompletionSource<bool>();
    int queryCalls = 0, claimCalls = 0, flushes = 0;
    using var scheduler = new CombatReports(true, false, "Europe/Dublin", 5,
        (world, start, end) => { queryCalls++; return query.Task; },
        (id, now) => { claimCalls++; Check(id.StartsWith("combat-report:v1:5:world:13:Europe/Dublin:day:")); return claim.Task; },
        end => { flushes++; Check(end == midnight); return new[] { "flush partial" }; }, report => sends.Add(report.PlainText), warnings.Add);
    scheduler.Tick(midnight.AddHours(-12), "world");
    scheduler.Tick(midnight, "world");
    scheduler.Poll(midnight.AddSeconds(6), "world");
    Check(queryCalls == 0 && flushes == 0 && sends.Count == 0);
    scheduler.Poll(midnight.AddSeconds(7), "world");
    for (int i = 0; i < 5; i++) scheduler.Poll(midnight.AddSeconds(8), "world");
    Check(queryCalls == 1 && flushes == 1 && claimCalls == 0);
    query.SetResult(snapshot);
    Check(SpinWait.SpinUntil(() => { scheduler.Poll(midnight.AddSeconds(9), "world"); return claimCalls == 1; }, 5000));
    Check(sends.Count == 0);
    claim.SetResult(true);
    scheduler.Poll(midnight.AddSeconds(10), "world");
    scheduler.Tick(midnight.AddMinutes(1), "world");
    Check(sends.Count == 1 && !sends[0].Contains("flush partial") && claimCalls == 1 && warnings.Count == 0);
    Check(sends[0].Contains("**⚔️ The Berserker** — kills\n1. Player: 2") &&
        sends[0].Contains("**🍖 The Einherjar Feaster** — meals eaten\n1. Player: 3") && sends[0].EndsWith("-# Server-observed; some counts are partial."));

    var slow = new TaskCompletionSource<CombinedReportSnapshot>();
    int slowQueries = 0;
    using var bounded = new CombatReports(true, true, "UTC", 5,
        (_, _, _) => { slowQueries++; return slow.Task; }, (_, _) => Task.FromResult(true),
        _ => Array.Empty<string>(), report => sends.Add(report.PlainText), warnings.Add);
    bounded.Tick(U("2026-03-29T12:00:00Z"), "world");
    bounded.Tick(U("2026-03-30T00:00:07Z"), "world");
    bounded.Tick(U("2026-04-06T00:00:07Z"), "world");
    Check(slowQueries == 2, "slow daily/weekly queries must never grow an unbounded backlog");
    bounded.Tick(U("2026-04-06T01:00:00Z"), "other");
    slow.SetResult(snapshot);
    bounded.Poll(U("2026-04-06T01:00:01Z"), "other");
    bounded.Tick(U("2026-04-06T01:00:02Z"), "world");
    bounded.Poll(U("2026-04-06T01:00:03Z"), "world");
    bounded.Dispose();
    bounded.Poll(U("2026-04-07T01:00:01Z"), "world");
    Check(sends.Count == 1, "obsolete-world and disposed work must not send");
});
Test("manual periods are today/Monday so far with exact DST-fold cutoff and explicit empty preview", () =>
{
    DateTime cutoff = U("2026-10-25T01:30:00Z");
    var today = CombatReports.CurrentPeriod(cutoff, dublin, false);
    var week = CombatReports.CurrentPeriod(cutoff, dublin, true);
    Check(today.StartUtc == U("2026-10-24T23:00:00Z") && today.EndUtc == cutoff);
    Check(week.StartLocal == new DateTime(2026, 10, 19) && week.EndUtc == cutoff);
    Check(CombatReports.CurrentPeriod(U("2026-10-26T00:00:00Z"), dublin, true).StartUtc == U("2026-10-26T00:00:00Z"));
    var empty = new CombinedReportSnapshot("world");
    Check(CombatReports.Render(empty, today, "Europe/Dublin", 5, Array.Empty<string>()) == null);
    string preview = CombatReports.Render(empty, today, "Europe/Dublin", 5, Array.Empty<string>(), manual: true)!;
    Check(preview.Contains(" (preview)") && preview.Contains("No observations") && preview.Contains("so far"));
    var all = new CombinedReportSnapshot("world", Enum.GetValues<ReportMetric>().ToDictionary(metric => metric,
        metric => (IReadOnlyList<ReportRow>)new[] { new ReportRow("p", new string('x', 100), 1, 1, 1, cutoff, meters: 1) }));
    string full = CombatReports.Render(all, week, new string('z', 100), 16, new[] { new string('x', 300) }, manual: true)!;
    Check(full.Length <= 1900 && full.Split('\n').Count(line => line.StartsWith("**")) == 19 && full.EndsWith("-# Server-observed; some counts are partial."));
});
Test("manual authorization precedes work; shared cooldown, single job, flush grace and automatic claims stay independent", () =>
{
    DateTime cutoff = U("2026-10-28T12:00:00Z");
    int queries = 0, flushes = 0, claims = 0;
    var output = new List<string>();
    var errors = new List<string>();
    var slow = new TaskCompletionSource<CombinedReportSnapshot>();
    using var manual = new ManualLeaderboardReports("Europe/Dublin", 5,
        (world, start, end) => { queries++; Check(start == U("2026-10-28T00:00:00Z") && end == cutoff); return slow.Task; },
        end => { flushes++; Check(end == cutoff); return Array.Empty<string>(); }, report => output.Add(report.PlainText), errors.Add);
    using var automatic = new CombatReports(true, false, "UTC", 5,
        (world, start, end) => Snapshot(new[] { new CombatRecord { PlayerId = "p", PlayerName = "P", TimestampUtc = start } }, world),
        (_, _) => { claims++; return true; }, output.Add, errors.Add);
    automatic.Tick(cutoff, "world");
    Check(!manual.Request("!dc daily", false, cutoff, 0, "world"));
    manual.Poll(10, "world");
    Check(queries == 0 && flushes == 0 && output.Count == 0 && claims == 0);
    Check(!manual.Request("!dc bogus", true, cutoff, 10, "world"));
    Check(manual.Request("!dc daily", true, cutoff, 10, "world"));
    Check(!manual.Request("!dc weekly", true, cutoff, 11, "world"));
    manual.Poll(16, "world");
    Check(queries == 0 && flushes == 0);
    manual.Poll(17, "world");
    Check(queries == 1 && flushes == 1);
    Check(!manual.Request("!dc weekly", true, cutoff.AddSeconds(61), 71, "world"), "slow query retains the single slot after cooldown");
    slow.SetResult(new CombinedReportSnapshot("world"));
    Check(output.Count == 0, "worker completion must not send");
    manual.Poll(72, "world");
    Check(output.Count == 1 && output[0].Contains("No observations") && claims == 0);
    Check(manual.Request("!dc weekly", true, cutoff.AddSeconds(63), 73, "world"));
    Check(!manual.Request("!dc daily", true, cutoff.AddSeconds(64), 74, "world"));
    automatic.Tick(U("2026-10-29T00:00:00Z"), "world");
    Check(claims == 1 && output.Count == 2 && !output[1].Contains(" (preview)") && errors.Count == 0);
    using var cooldown = new ManualLeaderboardReports("UTC", 5,
        (_, _, _) => Task.FromResult(new CombinedReportSnapshot("world")), _ => Array.Empty<string>(), report => output.Add(report.PlainText), errors.Add);
    Check(cooldown.Request("!dc daily", true, cutoff, 0, "world"));
    cooldown.Poll(7, "world");
    Check(!cooldown.Request("!dc weekly", true, cutoff.AddSeconds(59), 59, "world"), "cooldown remains after completion and covers both commands");
    Check(cooldown.Request("!dc weekly", true, cutoff.AddSeconds(60), 60, "world"));
});
Test("manual world changes, mismatched snapshots, query failure and disposal suppress sends and release bounded work", () =>
{
    DateTime cutoff = U("2026-10-28T12:00:00Z");
    var output = new List<string>(); var errors = new List<string>();
    var slow = new TaskCompletionSource<CombinedReportSnapshot>();
    int queries = 0;
    var manual = new ManualLeaderboardReports("UTC", 5,
        (_, _, _) => { queries++; return slow.Task; }, _ => Array.Empty<string>(), report => output.Add(report.PlainText), errors.Add);
    Check(manual.Request("!dc daily", true, cutoff, 0, "world"));
    manual.Poll(7, "world");
    manual.Poll(8, "other");
    Check(!manual.Request("!dc weekly", true, cutoff.AddSeconds(70), 70, "other"));
    slow.SetException(new InvalidOperationException("test"));
    manual.Poll(71, "world");
    Check(output.Count == 0 && queries == 1);
    Check(manual.Request("!dc daily", true, cutoff.AddSeconds(72), 72, "world"));
    manual.Poll(79, "world");
    Check(output.Count == 0 && errors.Count == 1 && queries == 2);
    slow = new TaskCompletionSource<CombinedReportSnapshot>();
    Check(manual.Request("!dc daily", true, cutoff.AddSeconds(132), 132, "world"));
    manual.Poll(139, "world");
    slow.SetResult(new CombinedReportSnapshot("wrong-world"));
    manual.Poll(140, "world");
    Check(output.Count == 0 && errors.Count == 2);
    slow = new TaskCompletionSource<CombinedReportSnapshot>();
    Check(manual.Request("!dc weekly", true, cutoff.AddSeconds(192), 192, "world"));
    manual.Poll(199, "world");
    manual.Dispose();
    slow.SetResult(new CombinedReportSnapshot("world"));
    manual.Poll(200, "world");
    Check(!manual.Request("!dc daily", true, cutoff.AddSeconds(300), 300, "world") && output.Count == 0);
    using var waiting = new ManualLeaderboardReports("UTC", 5,
        (_, _, _) => throw new Exception("unexpected query"), _ => throw new Exception("unexpected flush"), report => output.Add(report.PlainText), errors.Add);
    Check(waiting.Request("!dc daily", true, cutoff, 0, "world"));
    waiting.Poll(1, "other");
    waiting.Poll(7, "world");
    Check(output.Count == 0 && errors.Count == 2, "world switch during grace drops the job before any flush/query");
});
Test("manual diagnostics identify actual admission and query progress without changing work or routing", () =>
{
    var logs = new List<string>(); int queries = 0, sent = 0;
    DateTime utc = U("2026-10-30T12:00:00Z");
    ManualReportDiagnostics.Current = new ManualReportDiagnostics(logs.Add, () => 0);
    try
    {
        using var manual = new ManualLeaderboardReports("UTC", 5,
            (_, _, _) => { queries++; return Task.FromResult(new CombinedReportSnapshot("private-world")); },
            _ => Array.Empty<string>(), text => { sent++; ManualReportDiagnostics.Dispatch(() => DiscordApi.SendRenderedMessage(Webhook.Event.LeaderboardsAll, text)); }, _ => { });
        Check(!manual.Request("!dc daily", false, utc, 0, "private-world"));
        Check(!manual.Request("private-raw-command", true, utc, 0, "private-world"));
        Check(!manual.Request("!dc daily", true, utc, 0, ""));
        Check(queries == 0 && sent == 0);
        Check(manual.Request("!dc daily", true, utc, 0, "private-world"));
        Check(!manual.Request("!dc weekly", true, utc, 1, "private-world"));
        manual.Poll(6, "private-world"); manual.Poll(7, "private-world");
        Check(!manual.Request("!dc weekly", true, utc, 8, "private-world"));
        Check(queries == 1 && sent == 1);
        foreach (string reason in new[] { "Unauthorized", "Unsupported", "InvalidWorld", "Accepted", "Busy", "Cooldown" })
            Check(logs.Any(line => line.Contains("stage=Request reason=" + reason)), "missing Request diagnostic: " + reason);
        Check(logs.Any(line => line.Contains("stage=Grace reason=Waiting")) && logs.Any(line => line.Contains("stage=Query reason=Started")) &&
            logs.Any(line => line.Contains("stage=Query reason=Completed")), "actual poll progress must be visible");
        Check(logs.Any(line => line.Contains("stage=Routing reason=DispatchCounts") && line.Contains("matched=1 invoked=1")), "observe real route matching/invocation, not HTTP success");
        Check(logs.All(line => line.StartsWith("ManualReport ") && !line.Contains("private-") && !line.Contains("http")));
    }
    finally { ManualReportDiagnostics.Current = null; }
});
Test("manual diagnostic failures are redacted and cannot alter query failures or send behavior", () =>
{
    var logs = new List<string>(); DateTime utc = U("2026-10-30T12:00:00Z"); int sent = 0, warnings = 0;
    ManualReportDiagnostics.Current = new ManualReportDiagnostics(logs.Add, () => 0);
    try
    {
        using (var failed = new ManualLeaderboardReports("UTC", 5,
            (_, _, _) => Task.FromException<CombinedReportSnapshot>(new InvalidOperationException("secret-url/token/source-path")),
            _ => Array.Empty<string>(), _ => sent++, _ => warnings++))
        {
            Check(failed.Request("!dc daily", true, utc, 0, "secret-world")); failed.Poll(7, "secret-world");
        }
        Check(sent == 0 && warnings == 1 && logs.Any(line => line.Contains("stage=Query reason=Failed") && line.Contains("exception=InvalidOperationException")), "query failure must identify its stage");
        Check(logs.All(line => !line.Contains("secret") && !line.Contains("source-path") && !line.Contains("/")));
        ManualReportDiagnostics.Current = new ManualReportDiagnostics(_ => throw new Exception("logger"), () => 0);
        using var healthy = new ManualLeaderboardReports("UTC", 5,
            (_, _, _) => Task.FromResult(new CombinedReportSnapshot("world")), _ => Array.Empty<string>(), _ => sent++, _ => warnings++);
        Check(healthy.Request("!dc daily", true, utc, 0, "world")); healthy.Poll(7, "world");
        Check(sent == 1 && warnings == 1 && !healthy.Request("!dc weekly", true, utc, 8, "world"));
    }
    finally { ManualReportDiagnostics.Current = null; }
});
Test("manual diagnostic budget is global and early ingress probes coalesce without packet logs", () =>
{
    var logs = new List<string>(); double seconds = 0;
    ManualReportDiagnostics.Current = new ManualReportDiagnostics(logs.Add, () => seconds);
    try
    {
        for (int i = 0; i < 100; i++)
        {
            ManualReportDiagnostics.Emit(i % 2 == 0 ? ManualReportStage.Request : ManualReportStage.Query, ManualReportReason.Failed);
            ManualReportDiagnostics.Probe(ManualIngressProbe.Received);
            ManualReportDiagnostics.Probe(ManualIngressProbe.UnknownId);
        }
        Check(logs.Count == 16, "all stages must share one bounded budget");
        seconds = 59; ManualReportDiagnostics.Poll(); Check(logs.Count == 16);
        seconds = 60; ManualReportDiagnostics.Poll();
        Check(logs.Count == 17 && logs.Last().Contains("stage=IngressProbe reason=Summary") && logs.Last().Contains("Received=100") &&
            logs.Last().Contains("UnknownId=100") && logs.Last().Contains("suppressed=84"));
        for (int i = 0; i < 100; i++) ManualReportDiagnostics.Emit(ManualReportStage.Request, ManualReportReason.Failed);
        Check(logs.Count == 24, "summary and generic failures share eight early records; eight job-progress records stay reserved");

        logs.Clear(); seconds = 0;
        ManualReportDiagnostics.Current = new ManualReportDiagnostics(logs.Add, () => seconds);
        int queries = 0, sends = 0;
        DateTime utc = U("2026-10-30T12:00:00Z");
        using var manual = new ManualLeaderboardReports("UTC", 5,
            (_, _, _) => { queries++; return Task.FromResult(new CombinedReportSnapshot("world")); },
            _ => Array.Empty<string>(), text => { sends++; ManualReportDiagnostics.Dispatch(() => DiscordApi.SendRenderedMessage(Webhook.Event.LeaderboardsAll, text)); },
            _ => throw new Exception("unexpected warning"));
        for (int copy = 0; copy < 2; copy++)
        {
            ManualReportDiagnostics.Emit(ManualReportStage.Ingress, ManualReportReason.Decoded);
            ManualReportDiagnostics.Emit(ManualReportStage.Forward, ManualReportReason.Completed);
            ManualReportDiagnostics.Emit(ManualReportStage.Enclosing, ManualReportReason.Completed);
            ManualReportDiagnostics.Emit(ManualReportStage.Callback, ManualReportReason.Invoking);
            ManualReportDiagnostics.Emit(ManualReportStage.Authorization, ManualReportReason.Passed);
            Check(manual.Request("!dc daily", true, utc, 0, "world") == (copy == 0));
        }
        for (int i = 0; i < 100; i++) ManualReportDiagnostics.Emit(ManualReportStage.Request, ManualReportReason.Busy);
        manual.Poll(6, "world"); manual.Poll(7, "world");
        foreach (string line in logs) Console.WriteLine("TRACE " + line);
        foreach (string marker in new[] { "Request reason=Accepted", "Grace reason=Waiting", "Flush reason=Started", "Query reason=Started",
            "Query reason=Completed", "Dispatch reason=Invoking", "Routing reason=DispatchCounts", "Dispatch reason=Completed" })
            Check(logs.Any(line => line.Contains("stage=" + marker)), "forward copies/busy traffic hid accepted-job progress: " + marker);
        Check(logs.Count <= 16 && queries == 1 && sends == 1 && logs.Any(line => line.Contains("matched=1 invoked=1")),
            "one actual accepted job must retain terminal routing diagnostics inside the unchanged total budget");
    }
    finally { ManualReportDiagnostics.Current = null; }
});
Test("decoded route witnesses survive router replacement and deduplicate before the real manual job", () =>
{
    var routing = new ManualCommandRouting(); var connection = new object();
    int callbacks = 0, admitted = 0, queries = 0, sent = 0;
    DateTime utc = U("2026-10-31T12:00:00Z");
    using var manual = new ManualLeaderboardReports("UTC", 5,
        (_, _, _) => { queries++; return Task.FromResult(new CombinedReportSnapshot("world")); },
        _ => Array.Empty<string>(), _ => sent++, _ => throw new Exception("unexpected warning"));
    Action request = () => { callbacks++; if (manual.Request("!dc daily", true, utc, 0, "world")) admitted++; };
    var receive = routing.Enter(connection);
    var observed = routing.Observe(42, 42, "session", "world", 1, "!dc daily", 0, request);
    Check(observed != null, "a validated decoded route must produce a witness independently of activity/raw parsing");
    routing.Complete(observed, null); // Same finalizer path when NPS relays and skips vanilla.
    routing.Complete(routing.Observe(42, 42, "session", "world", 1, "!dc daily", 0, request), null); // Broadcast Handle + Route.
    Check(callbacks == 0, "route completion must wait for the enclosing receive");
    routing.Finish(receive, null);
    receive = routing.Enter(connection);
    routing.Complete(routing.Observe(42, 42, "session", "world", 1, "!dc daily", 0, request), null);
    routing.Finish(receive, null);
    Check(callbacks == 1, "re-observing one session/message must not invoke twice");
    receive = routing.Enter(connection);
    routing.Complete(routing.Observe(42, 42, "session", "world", 2, "!dc daily", 0, request), null);
    routing.Finish(receive, null); // Recipient copies can have different native message IDs.
    receive = routing.Enter(new object());
    routing.Complete(routing.Observe(42, 42, "new-session", "world", 1, "!dc daily", 0, request), null);
    routing.Finish(receive, null); // A fresh connection is not deduplicated against a previous session.
    manual.Poll(6, "world"); Check(queries == 0);
    manual.Poll(7, "world");
    Check(callbacks == 3 && admitted == 1 && queries == 1 && sent == 1);
});
Test("decoded routing restores nested transport and discards route or enclosing exceptions without stale work", () =>
{
    var routing = new ManualCommandRouting(); var a = new object(); var b = new object(); int callbacks = 0;
    var outer = routing.Enter(a);
    var stale = routing.Observe(1, 1, "a", "world", 1, "!dc daily", 0, () => callbacks++);
    Check(stale != null, "outer receive must capture decoded work"); routing.Complete(stale, null);
    var inner = routing.Enter(b);
    Check(ReferenceEquals(routing.Current?.Transport, b));
    routing.Complete(routing.Observe(2, 2, "b", "world", 2, "!dc weekly", 0, () => callbacks++), null);
    routing.Finish(inner, new InvalidOperationException("receive failed"));
    Check(ReferenceEquals(routing.Current?.Transport, a), "inner finalizer must restore actual outer transport");
    routing.Finish(outer, null);
    Check(callbacks == 0 && routing.Current == null, "nested failure must discard all enclosing staged work");
    outer = routing.Enter(a);
    routing.Complete(stale, null);
    routing.Complete(routing.Observe(1, 1, "a", "world", 3, "!dc daily", 0, () => callbacks++), new Exception("route failed"));
    routing.Finish(outer, null);
    Check(callbacks == 0 && routing.Current == null, "a route exception is not a successful replacement witness");
    outer = routing.Enter(a); routing.Finish(outer, null);
    Check(callbacks == 0, "a skipped receive without any decoded witness cannot execute work");
    outer = routing.Enter(a);
    routing.Complete(routing.Observe(1, 1, "a", "world", 4, "!dc daily", 0, () => callbacks++), null);
    routing.Finish(outer, new Exception("outer failed"));
    Check(callbacks == 0);
    outer = routing.Enter(a);
    routing.Complete(routing.Observe(1, 1, "a", "world", 1, "!dc daily", 0, () => callbacks++), null);
    inner = routing.Enter(b);
    routing.Complete(routing.Observe(2, 2, "b", "world", 2, "!dc weekly", 0, () => callbacks++), null);
    routing.Finish(inner, null);
    Check(callbacks == 0 && ReferenceEquals(routing.Current?.Transport, a));
    routing.Finish(outer, null);
    Check(callbacks == 2 && routing.Current == null, "successful nested work commits only at the root; failed receives did not poison deduplication");
});
Test("decoded routing rejects forged or absent sender context and bounds nesting while keeping admin admission intact", () =>
{
    var routing = new ManualCommandRouting(); int queries = 0, admitted = 0;
    DateTime utc = U("2026-10-31T12:00:00Z");
    using var manual = new ManualLeaderboardReports("UTC", 5,
        (_, _, _) => { queries++; return Task.FromResult(new CombinedReportSnapshot("world")); },
        _ => Array.Empty<string>(), _ => throw new Exception("unexpected send"), _ => { });
    Action unauthorized = () => { if (manual.Request("!dc daily", false, utc, 0, "world")) admitted++; };
    Check(routing.Observe(42, 42, "session", "world", 1, "!dc daily", 0, unauthorized) == null, "server-originated route has no incoming identity");
    var outer = routing.Enter(new object());
    Check(routing.Observe(99, 42, "session", "world", 1, "!dc daily", 0, unauthorized) == null, "claimed sender must equal actual connection peer");
    var inner = routing.Enter(null);
    Check(routing.Observe(42, 42, "session", "world", 1, "!dc daily", 0, unauthorized) == null, "nested missing transport must mask outer authorization");
    routing.Finish(inner, null);
    var valid = routing.Observe(42, 42, "session", "world", 2, "!dc daily", 0, unauthorized);
    Check(valid != null, "restored actual transport permits observation, not admin authority");
    routing.Complete(valid, null); routing.Finish(outer, null); manual.Poll(7, "world");
    Check(queries == 0 && admitted == 0);
    var frames = new List<ManualCommandRouting.Frame?>();
    for (int i = 0; i < 17; i++) frames.Add(routing.Enter(new object()));
    Check(routing.Current == null, "excess nesting must mask, not inherit, the last valid connection");
    for (int i = frames.Count - 1; i >= 0; i--) routing.Finish(frames[i], null);
    Check(routing.Current == null && queries == 0 && admitted == 0);
});
Test("mixed signal overflow preserves the next independent manual authorization, cooldown and query count", () =>
{
    var loss = new ObservationLoss(_ => { }, "manual-reporting");
    var routing = new ManualCommandRouting(() => 0, loss); int callbacks = 0, admitted = 0, queries = 0, sent = 0;
    using var database = new LiteDB.LiteDatabase(new MemoryStream());
    using var store = new ActivityStore(database, new CombatStore(database), new PlayerActionStore(database), new CookingStore(database), _ => { }, observationLoss: loss);
    DateTime utc = U("2026-11-01T12:00:00Z");
    using var manual = new ManualLeaderboardReports("UTC", 5,
        (_, _, _) => { queries++; return Task.FromResult(new CombinedReportSnapshot("world")); },
        _ => Array.Empty<string>(), _ => sent++, _ => throw new Exception("unexpected warning"));
    void Request(bool admin, double time)
    { callbacks++; if (manual.Request("!dc daily", admin, utc.AddSeconds(time), time, "world")) admitted++; }
    void Stage(long message, bool admin, double time) => routing.Complete(
        routing.Observe(42, 42, "session", "world", message, "!dc daily", time, () => Request(admin, time)), null);
    var root = routing.Enter(new object()); Stage(1, true, 0);
    for (int i = 0; i < 16; i++) routing.Complete(routing.ObserveSignal(42, 42, "session", "world", i, "Bob",
        RoutingObservationKind.KillCredit, 0, () => throw new Exception("overflow signal dispatched"), new ObservationPrincipal("authenticated-account-42", "session", false)), null);
    routing.Finish(root, null); manual.Poll(7, "world");
    Check(callbacks == 0 && admitted == 0 && queries == 0 && sent == 0, "overflow consumed no manual work/cooldown/replay");
    root = routing.Enter(new object()); Stage(1, false, 0); routing.Finish(root, null); manual.Poll(7, "world");
    Check(callbacks == 1 && admitted == 0 && queries == 0, "restored routing never grants admin");
    root = routing.Enter(new object()); Stage(2, true, 0); routing.Finish(root, null);
    manual.Poll(6, "world"); Check(queries == 0); manual.Poll(7, "world");
    root = routing.Enter(new object()); Stage(3, true, 59); routing.Finish(root, null);
    Check(callbacks == 3 && admitted == 1 && queries == 1 && sent == 1, "unchanged shared 60-second cooldown");
    root = routing.Enter(new object()); Stage(4, true, 60); routing.Finish(root, null); manual.Poll(67, "world");
    Check(callbacks == 4 && admitted == 2 && queries == 2 && sent == 2, "exact cooldown/grace boundaries recover");
    var absolute = loss.Snapshot("world", ReportMetric.ReportedKillCredits)!;
    store.ReconcileLossAsync(absolute).GetAwaiter().GetResult();
    store.ReconcileLossAsync(absolute).GetAwaiter().GetResult();
    Check(store.QueryAsync("world", utc.AddDays(-1), utc.AddDays(1), new(reportedKillCredits: true)).GetAwaiter().GetResult().Coverage.Single().KnownLosses == 16,
        "mixed overflow persists each discarded signal once, excluding the manual request");
    Check(callbacks == 4 && admitted == 2 && queries == 2 && sent == 2, "absolute reconciliation cannot retry manual admission, queries or sends");
});
Test("emote trigger defaults are off and only the 25 native normalized bindings are accepted", () =>
{
    var defaults = new ManualLeaderboardTriggerConfig(new ConfigFile());
    Check(!defaults.Enabled && defaults.Daily == "flex" && defaults.Weekly == "toast");
    using var off = new EmoteReportTriggers(defaults.Enabled, defaults.Daily, defaults.Weekly);
    Check(off.CommandFor("flex") == null);
    var file = new ConfigFile();
    file.Values["Manual Leaderboard Triggers/Emote Triggers Enabled"] = true;
    file.Values["Manual Leaderboard Triggers/Daily Report Emote"] = " WaVe ";
    file.Values["Manual Leaderboard Triggers/Weekly Report Emote"] = " ChEeR ";
    var config = new ManualLeaderboardTriggerConfig(file);
    using var custom = new EmoteReportTriggers(config.Enabled, config.Daily, config.Weekly);
    Check(custom.CommandFor("wave") == "!dc daily" && custom.CommandFor("cheer") == "!dc weekly", "normalized custom bindings must map to existing manual commands");
    string[] native = { "wave", "sit", "challenge", "cheer", "nonono", "thumbsup", "point", "blowkiss", "bow", "cower", "cry", "despair", "flex", "comehere", "headbang", "kneel", "laugh", "roar", "shrug", "dance", "relax", "toast", "rest", "vibe", "loveyou" };
    foreach (string name in native)
    {
        using var binding = new EmoteReportTriggers(true, name, "");
        Check(binding.CommandFor(name) == "!dc daily", "missing native emote: " + name);
    }
    var warnings = new List<string>();
    using var invalid = new EmoteReportTriggers(true, "Count", " toast ", warnings.Add);
    Check(invalid.CommandFor("Count") == null && invalid.CommandFor("toast") == "!dc weekly");
    using var duplicate = new EmoteReportTriggers(true, " FLEX ", "flex", warnings.Add);
    Check(duplicate.CommandFor("flex") == null && warnings.Count == 2 && warnings.All(line => !line.Contains("Count") && !line.Contains("FLEX")));
});
Test("emote observations baseline, suppress replay and failed receives, and reset bounded state safely", () =>
{
    using var trigger = new EmoteReportTriggers(true, "flex", "toast"); trigger.SetWorld("world");
    var commands = new List<string>();
    EmoteReportTriggers.Snapshot S(uint rev, int id, string name, string character = "char") => new(character, rev, id, name);
    void Accept(EmoteReportTriggers.Snapshot before, EmoteReportTriggers.Snapshot after, double time = 0, string session = "session", string character = "char", bool ran = true, Exception? error = null)
    { var frame = trigger.Enter(); trigger.Stage(frame, "world", session, character, before, after, time, commands.Add); trigger.Finish(frame, ran, error); }
    Accept(default, S(1, 1, "flex")); Check(commands.Count == 0);
    Accept(S(1, 1, "flex"), S(2, 2, "flex"));
    Check(commands.SequenceEqual(new[] { "!dc daily" }), "a new accepted ID on the same emote must trigger after baselining");
    Accept(S(2, 2, "flex"), S(3, 2, "flex")); Accept(S(3, 2, "flex"), S(3, 2, "flex"));
    Accept(S(3, 2, "flex"), S(4, 3, "")); Check(commands.Count == 1);
    Accept(S(4, 3, ""), S(5, 4, "flex")); Check(commands.Count == 2);
    Accept(S(5, 4, "flex"), S(6, 5, "flex", "other")); Check(commands.Count == 2);
    Accept(S(5, 4, "flex"), S(6, 5, "flex"), ran: false);
    Accept(S(6, 5, "flex"), S(7, 6, "flex")); Check(commands.Count == 2, "failed receive must force a fresh baseline");
    var outer = trigger.Enter(); var inner = trigger.Enter();
    trigger.Stage(inner, "world", "session", "char", S(7, 6, "flex"), S(8, 7, "toast"), 0, commands.Add);
    trigger.Finish(inner, true, null); trigger.Finish(outer, true, new Exception("outer failed")); Check(commands.Count == 2);
    Accept(S(8, 7, "toast"), S(9, 8, "flex")); Check(commands.Count == 2);
    Accept(S(9, 8, "flex"), S(10, 9, "toast"), session: "new-session"); Check(commands.Count == 2);
    trigger.SetWorld("other-world"); trigger.SetWorld("world");
    Accept(S(10, 9, "toast"), S(11, 10, "flex")); Check(commands.Count == 2);
    trigger.SetEnabled(false); trigger.SetEnabled(true);
    Accept(S(11, 10, "flex"), S(12, 11, "flex")); Check(commands.Count == 2);
    Accept(S(12, 11, "flex"), S(13, 12, "flex"), time: 301); Check(commands.Count == 2, "expired state rebaselines");
    Accept(S(13, 12, "flex"), S(14, 13, "flex"), time: 302); Check(commands.Count == 3);
    for (int i = 0; i < 129; i++) Accept(default, S(1, 1, "flex", "c" + i), time: 303, character: "c" + i);
    Accept(S(1, 1, "flex", "c0"), S(2, 2, "flex", "c0"), time: 304, character: "c0"); Check(commands.Count == 3, "evicted state must baseline rather than trigger");
    trigger.Dispose(); Accept(S(14, 13, "flex"), S(15, 14, "toast")); Check(commands.Count == 3);
});
Test("emote and chat share actual manual admin admission, cooldown, single job and grace", () =>
{
    using var trigger = new EmoteReportTriggers(true, "flex", "toast"); trigger.SetWorld("world");
    int queries = 0; var output = new List<string>(); DateTime utc = U("2026-10-31T12:00:00Z");
    using var manual = new ManualLeaderboardReports("UTC", 5,
        (_, _, _) => { queries++; return Task.FromResult(new CombinedReportSnapshot("world")); },
        _ => Array.Empty<string>(), report => output.Add(report.PlainText), _ => throw new Exception("unexpected warning"));
    void Emote(uint revision, int id, string name, bool admin, double time)
    {
        var frame = trigger.Enter();
        var before = revision == 1 ? default : new EmoteReportTriggers.Snapshot("char", revision - 1, id - 1, "flex");
        trigger.Stage(frame, "world", "session", "char", before, new("char", revision, id, name), time,
            command => manual.Request(command, admin, utc.AddSeconds(time), time, "world"));
        trigger.Finish(frame, true, null);
    }
    Emote(1, 1, "flex", false, 0); Emote(2, 2, "flex", false, 1); manual.Poll(8, "world"); Check(queries == 0 && output.Count == 0);
    Emote(3, 3, "flex", true, 2);
    Check(!manual.Request("!dc weekly", true, utc.AddSeconds(3), 3, "world"), "chat cannot bypass the emote's pending job");
    manual.Poll(8, "world"); Check(queries == 0); manual.Poll(9, "world");
    Check(queries == 1 && output.Count == 1, "admin emote must use the actual existing report pipeline");
    Emote(4, 4, "toast", true, 10); Check(!manual.Request("!dc daily", true, utc.AddSeconds(11), 11, "world"));
    manual.Poll(20, "world"); Check(queries == 1 && output.Count == 1);
});
Test("travel flags enable exactly one new section each and duration/distance units rank correctly", () =>
{
    foreach (var (key, metric) in new[] { ("On Foot Distance", ReportMetric.OnFootDistance), ("Biome Time", ReportMetric.BiomeTime),
        ("Online Time", ReportMetric.OnlineTime), ("Portal Trips", ReportMetric.PortalTrips) })
    {
        var file = new ConfigFile(); file.Values["Server-Observed Activities/" + key + " Enabled"] = true;
        var options = new ObservedLeaderboardsConfig(file).Snapshot(new ActivityConfig(file));
        Check(Enum.GetValues<ReportMetric>().Where(options.Includes).SequenceEqual(new[] { metric }));
    }
    var rows = new[] { new ReportRow("a", "Short", 99, 60, 0, daily.StartUtc, meters: 10, biome: "Swamp"),
        new ReportRow("b", "Long", 1, 7200, 0, daily.StartUtc, meters: 1500, biome: "BlackForest") };
    var snapshot = new CombinedReportSnapshot("world", new[] { ReportMetric.OnFootDistance, ReportMetric.BiomeTime, ReportMetric.OnlineTime }
        .ToDictionary(metric => metric, metric => (IReadOnlyList<ReportRow>)rows));
    var report = CombatReports.BuildReport(snapshot, daily, "Europe/Dublin", 5, Array.Empty<string>())!;
    Check(report.Sections.All(section => section.Value.StartsWith("1. Long:")));
    Check(report.PlainText.Contains("Long: 1.5 km") && report.PlainText.Contains("Long: 2 h (BlackForest)") && report.PlainText.Contains("Short: 1 min"));
});
Test("simulation damage flag defaults off and independently ranks actual HP under a new identity", () =>
{
    var file = new ConfigFile(); var activities = new ActivityConfig(file);
    Check(!new ObservedLeaderboardsConfig(file).Snapshot(activities).DamageDealt);
    file.Values["Server-Observed Activities/Damage Dealt Enabled"] = true;
    var flags = new ObservedLeaderboardsConfig(file).Snapshot(new ActivityConfig(file));
    Check(Enum.GetValues<ReportMetric>().Where(flags.Includes).SequenceEqual(new[] { ReportMetric.DamageDealt }));
    var snapshot = new CombinedReportSnapshot("world", new Dictionary<ReportMetric, IReadOnlyList<ReportRow>> {
        [ReportMetric.DamageDealt] = new[] { new ReportRow("a", "Low", 99, 0, 5, daily.StartUtc), new ReportRow("b", "High", 1, 0, 100, daily.StartUtc) } });
    Check(CombatReports.BuildReport(snapshot, daily, "Europe/Dublin", 5, Array.Empty<string>())!.Sections.Single() ==
        (headings[ReportMetric.DamageDealt], "1. High: 100 HP\n2. Low: 5 HP"));
});
Test("longest bow flag is default-off independent and renders meter maxima rather than hit counts", () =>
{
    var file = new ConfigFile();
    Check(!new ObservedLeaderboardsConfig(file).Snapshot(new ActivityConfig(file)).LongestBowHit);
    file.Values["Server-Observed Activities/Longest Bow Hit Enabled"] = true;
    var flags = new ObservedLeaderboardsConfig(file).Snapshot(new ActivityConfig(file));
    Check(Enum.GetValues<ReportMetric>().Where(flags.Includes).SequenceEqual(new[] { ReportMetric.LongestBowHit }));
    var snapshot = new CombinedReportSnapshot("world", new Dictionary<ReportMetric, IReadOnlyList<ReportRow>> {
        [ReportMetric.LongestBowHit] = new[] { new ReportRow("a", "Near", 99, 0, 0, daily.StartUtc, meters: 12), new ReportRow("b", "Far", 1, 0, 0, daily.StartUtc, meters: 87) } });
    Check(CombatReports.BuildReport(snapshot, daily, "Europe/Dublin", 5, Array.Empty<string>())!.Sections.Single() ==
        (headings[ReportMetric.LongestBowHit], "1. Far: 87 m\n2. Near: 12 m"));
});
Console.WriteLine($"{passed}/{passed + failed} combat reporting checks passed; no webhook requests made.");
return failed == 0 ? 0 : 1;
