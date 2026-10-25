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
var daily = CombatReports.PreviousPeriod(U("2026-03-28T12:00:00Z"), dublin, false);

Test("opt-in defaults are independent of legacy config", () =>
{
    var config = new CombatConfig(new ConfigFile());
    Check(!config.Enabled && !config.DailyEnabled && !config.WeeklyEnabled);
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
    string report = CombatReports.Render(rows, daily, "Europe/Dublin", 5)!;
    Check(report.Contains("Renamed: 1") && !report.Contains("Old:") && !report.Contains("Player5:") && !report.Contains("Player6:"));
    Check(report.Contains("Creature kills (excluding bosses)") && report.Contains("Boss kills"));
    Check(report.Contains("Boar: 7") && report.Contains("Eikthyr: 1") && report.Contains("Meadows: 8"));
    Check(report.Contains("SERVER-OBSERVED") && report.Contains("partial coverage"));
});
Test("same display name never merges distinct players", () =>
{
    string report = CombatReports.Render(new[] { Row("a", "a", "Twin"), Row("b", "b", "Twin") }, daily, "Europe/Dublin", 5)!;
    Check(report.Contains("1. Twin: 1\n2. Twin: 1"));
});
Test("meaningful-empty reports are skipped", () =>
{
    Check(CombatReports.Render(Array.Empty<CombatRecord>(), daily, "Europe/Dublin", 5) == null);
    Check(CombatReports.Render(new[] { Row("a", "", "Unknown") }, daily, "Europe/Dublin", 5) == null);
});
Test("oversized strings, markdown, controls, mentions and surrogate pairs are safe", () =>
{
    var rows = Enumerable.Range(0, 100).Select(i => Row($"r{i}", $"p{i}", "@everyone <@123> **_[x](url)`\n\u202e" + new string('x', 10000))).ToArray();
    foreach (var row in rows) { row.Creature = "<@&456>@here" + new string('x', 10000) + row.Id; row.Biome = "😀" + new string('z', 10000) + row.Id; }
    string report = CombatReports.Render(rows, daily, new string('t', 10000), 16)!;
    Check(report.Length <= 1900 && report.Contains("…"));
    Check(!report.Contains("@everyone") && !report.Contains("@here") && !report.Contains("<@") && !report.Contains('\u202e'));
    Check(report.Contains("\\*\\*\\_\\[x\\]\\(url\\)\\`"));
    Check(System.Text.Encoding.UTF8.GetString(System.Text.Encoding.UTF8.GetBytes(report)) == report);
    Check(report.Contains("Creature types") && report.Contains("Biomes"));
});

var claims = new HashSet<string>();
var sends = new List<string>();
var warnings = new List<string>();
var queries = new List<(string World, DateTime Start, DateTime End)>();
CombatReports Scheduler(bool weekly = false, string zone = "Europe/Dublin", int top = 5, Action<string>? send = null) =>
    new(true, weekly, zone, top,
        (world, start, end) => { queries.Add((world, start, end)); return new List<CombatRecord> { Row("r", "p", "Player") }; },
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
    Check(new CombatConfig(file).Enabled);
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
        (_, _, _) => new List<CombatRecord>(), (id, now) => claims.Add(id), sends.Add, warnings.Add);
    scheduler.Tick(U("2026-03-27T12:00:00Z"), "world");
    scheduler.Tick(U("2026-03-28T12:00:00Z"), "world");
    Check(claims.Count == 0 && sends.Count == 0 && warnings.Count == 0);
});
Test("storage failure cannot send an unclaimed report", () =>
{
    Reset();
    using var scheduler = new CombatReports(true, false, "Europe/Dublin", 5,
        (_, _, _) => new List<CombatRecord> { Row("r", "p", "Player") },
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
                store.Query, store.TryClaimReport, report =>
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
                store.Query, store.TryClaimReport, sends.Add, warnings.Add);
            // Replaying the same boundary tests the durable claim, not merely the new startup baseline.
            scheduler.Tick(U("2026-03-27T12:00:00Z"), "world");
            scheduler.Tick(U("2026-03-28T00:00:00Z"), "world");
            Check(sends.Count == 0);
            using var changedZone = new CombatReports(true, false, "UTC", 5,
                store.Query, store.TryClaimReport, sends.Add, warnings.Add);
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
Test("production combat payload stays literal and bounded with embeds ON/OFF", () =>
{
    var row = Row("tokens", "p", "%VAR1% @everyone <@123>");
    row.Creature = "%VAR2%"; row.Biome = "%TIMESTAMP%";
    string report = CombatReports.Render(new[] { row }, daily, "Europe/Dublin", 5)!;
    var config = DiscordConnectorPlugin.StaticConfig;
    config.UserVariable = new string('X', 4200) + " @everyone <@&456>";
    config.UserVariable1 = "**injected**";
    foreach (bool embeds in new[] { false, true })
    {
        config.DiscordEmbedsEnabled = embeds;
        DiscordConnectorPlugin.StaticLogger.Messages.Clear();
        DiscordApi.SendRenderedMessage(Webhook.Event.LeaderboardsAll, report);
        const string prefix = "Trying webhook with payload: ";
        string serialized = DiscordConnectorPlugin.StaticLogger.Messages.Single(m => m.StartsWith(prefix)).Substring(prefix.Length);
        var payload = Newtonsoft.Json.Linq.JObject.Parse(serialized);
        string actual = (string)(embeds ? payload["embeds"]![0]!["description"]! : payload["content"]!)!;
        Check(actual == report, $"embeds={embeds}: expected literal report ({report.Length} chars), got {actual.Length} chars");
        Check(actual.Length <= 1900 && actual.Contains("%VAR1%") && actual.Contains("%VAR2%") && actual.Contains("%TIMESTAMP%"));
        Check(!actual.Contains("@everyone") && !actual.Contains("<@"));
        Check(!payload["allowed_mentions"]!["parse"]!.Any() && !payload["allowed_mentions"]!["roles"]!.Any() &&
            !payload["allowed_mentions"]!["users"]!.Any() && (bool)payload["allowed_mentions"]!["replied_user"]! == false);
    }
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
Console.WriteLine($"{passed}/{passed + failed} combat reporting checks passed; no webhook requests made.");
return failed == 0 ? 0 : 1;
