using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using DiscordConnector.Records;

namespace DiscordConnector.Leaderboards;

/// <summary>
/// Main-thread calendar scheduler. The first observation of each world baselines both calendars without
/// querying history; later ticks consider only the latest completed period, never a backlog. Restarting
/// deliberately skips periods completed offline. A durable claim precedes transport: at-most-once attempt,
/// NOT delivery, because the existing async-void webhook API cannot acknowledge delivery or safely retry.
/// </summary>
internal sealed class CombatReports : IDisposable
{
    private readonly bool dailyEnabled;
    private readonly bool weeklyEnabled;
    private readonly Func<string, Period, string[], Task<LeaderboardReport?>> queryReport;
    private readonly Func<string, DateTime, Task<bool>> tryClaim;
    private readonly Func<DateTime, string[]> flushThrough;
    private readonly TimeSpan flushGrace = TimeSpan.FromSeconds(7);
    private readonly List<PendingReport> pending = new();
    private readonly Action<LeaderboardReport> send;
    private readonly Action<string> warn;
    private readonly TimeZoneInfo? timeZone;
    private string worldId = "";
    private DateTime dailyEndUtc;
    private DateTime weeklyEndUtc;
    private bool disposed;

    // Synchronous snapshots use the same typed report without the asynchronous flush grace.
    internal CombatReports(bool dailyEnabled, bool weeklyEnabled, string timeZoneId, int topCount,
        Func<string, DateTime, DateTime, CombinedReportSnapshot> query,
        Func<string, DateTime, bool> tryClaim, Action<string> send, Action<string> warn)
        : this(dailyEnabled, weeklyEnabled, timeZoneId, topCount,
            (world, start, end) => Task.FromResult(query(world, start, end)),
            (id, now) => Task.FromResult(tryClaim(id, now)), _ => Array.Empty<string>(), report => send(report.PlainText), warn)
    {
        flushGrace = TimeSpan.Zero;
    }

    internal CombatReports(bool dailyEnabled, bool weeklyEnabled, string timeZoneId, int topCount,
        Func<string, DateTime, DateTime, Task<CombinedReportSnapshot>> query,
        Func<string, DateTime, Task<bool>> tryClaim, Func<DateTime, string[]> flushThrough,
        Action<LeaderboardReport> send, Action<string> warn, Action<string>? diagnostic = null)
    {
        this.dailyEnabled = dailyEnabled;
        this.weeklyEnabled = weeklyEnabled;
        queryReport = async (world, period, limitations) =>
        {
            CombinedReportSnapshot snapshot = await query(world, period.StartUtc, period.EndUtc).ConfigureAwait(false);
            if (!string.Equals(snapshot.WorldId, world, StringComparison.Ordinal))
                throw new InvalidOperationException("Report world mismatch.");
            LeaderboardReport? report = BuildReport(snapshot, period, timeZoneId, topCount, limitations);
            if (DiscordConnectorPlugin.StaticConfig.DebugLeaderboardOperations)
                LogReport(snapshot, report, period, topCount, false, diagnostic);
            return report;
        };
        this.tryClaim = tryClaim;
        this.flushThrough = flushThrough;
        this.send = send;
        this.warn = warn;
        if (!dailyEnabled && !weeklyEnabled)
        {
            disposed = true;
            return;
        }

        try
        {
            if (topCount < 1 || topCount > 16) throw new ArgumentOutOfRangeException(nameof(topCount));
            timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (Exception ex) when (ex is ArgumentException || ex is TimeZoneNotFoundException || ex is InvalidTimeZoneException)
        {
            disposed = true;
            warn("Combat reports disabled: invalid timezone or Top Count (1-16). Combat tracking is unchanged.");
        }
    }

    // Called only by Plugin.Update with an already-snapshotted world UID, never from a pool timer.
    internal void Tick(DateTime nowUtc, string currentWorldId)
    {
        if (disposed || timeZone == null || string.IsNullOrWhiteSpace(currentWorldId)) return;
        try
        {
            Period day = PreviousPeriod(nowUtc, timeZone, false);
            Period week = PreviousPeriod(nowUtc, timeZone, true);
            if (!string.Equals(worldId, currentWorldId, StringComparison.Ordinal))
            {
                foreach (PendingReport job in pending) job.Obsolete = true;
                worldId = currentWorldId;
                dailyEndUtc = day.EndUtc;
                weeklyEndUtc = week.EndUtc;
                return;
            }

            Poll(nowUtc, currentWorldId);
            if (day.EndUtc > dailyEndUtc)
            {
                dailyEndUtc = day.EndUtc;
                if (dailyEnabled) Schedule(day);
            }

            if (week.EndUtc > weeklyEndUtc)
            {
                weeklyEndUtc = week.EndUtc;
                if (weeklyEnabled) Schedule(week);
            }
            Poll(nowUtc, currentWorldId);
        }
        catch (Exception ex)
        {
            Dispose();
            warn($"Combat reports disabled after calendar failure ({ex.GetType().Name}); tracking is unchanged.");
        }
    }

    private sealed class PendingReport
    {
        internal readonly string World;
        internal readonly Period Period;
        internal Task<LeaderboardReport?>? Query;
        internal Task<bool>? Claim;
        internal LeaderboardReport? Report;
        internal bool Obsolete;
        internal PendingReport(string world, Period period) { World = world; Period = period; }
    }

    private void Schedule(Period period)
    {
        // At most one day and one week job; slow storage must not grow a report backlog.
        if (pending.Count < 2) pending.Add(new PendingReport(worldId, period));
        else warn("Combat report skipped: report work limit; no automatic retry.");
    }

    // Poll from the main thread between calendar ticks; never block on storage or send from a worker.
    internal void Poll(DateTime nowUtc, string currentWorldId)
    {
        if (disposed || timeZone == null) return;
        for (int i = 0; i < pending.Count;)
        {
            PendingReport job = pending[i];
            try
            {
                job.Obsolete |= !string.Equals(job.World, currentWorldId, StringComparison.Ordinal) ||
                    job.Period.EndUtc != PreviousPeriod(nowUtc, timeZone, job.Period.Weekly).EndUtc;
                if (job.Obsolete)
                {
                    // In-flight obsolete work still occupies its slot until observed, preserving the bound.
                    if (job.Query != null && !job.Query.IsCompleted || job.Claim != null && !job.Claim.IsCompleted) { i++; continue; }
                    if (job.Query?.IsFaulted == true) _ = job.Query.Exception;
                    if (job.Claim?.IsFaulted == true) _ = job.Claim.Exception;
                    pending.RemoveAt(i);
                    continue;
                }
                if (nowUtc < job.Period.EndUtc + flushGrace) { i++; continue; }
                if (job.Query == null)
                    job.Query = queryReport(job.World, job.Period, flushThrough(job.Period.EndUtc));
                if (!job.Query.IsCompleted) { i++; continue; }
                if (job.Claim == null)
                {
                    job.Report = job.Query.GetAwaiter().GetResult();
                    if (job.Report == null) { pending.RemoveAt(i); continue; }
                    // Preserve the existing durable namespace and length-prefixed world/timezone identity.
                    string id = "combat-report:v1:" + job.World.Length + ":" + job.World + ":" + timeZone.Id.Length + ":" +
                        timeZone.Id + ":" + (job.Period.Weekly ? "week:" : "day:") +
                        job.Period.StartUtc.Ticks.ToString(CultureInfo.InvariantCulture) + ":" +
                        job.Period.EndUtc.Ticks.ToString(CultureInfo.InvariantCulture);
                    job.Claim = tryClaim(id, nowUtc);
                }
                if (!job.Claim.IsCompleted) { i++; continue; }
                bool claimed = job.Claim.GetAwaiter().GetResult();
                pending.RemoveAt(i);
                if (claimed) send(job.Report!);
            }
            catch (Exception ex)
            {
                if (i < pending.Count && ReferenceEquals(pending[i], job)) pending.RemoveAt(i);
                warn($"Combat report attempt failed ({ex.GetType().Name}); no automatic retry. Tracking is unchanged.");
            }
        }
    }

    internal readonly struct Period
    {
        internal Period(DateTime startLocal, DateTime endLocal, TimeZoneInfo zone, bool weekly, DateTime? endUtc = null)
        {
            StartLocal = startLocal;
            EndLocal = endLocal;
            StartUtc = BoundaryUtc(startLocal, zone);
            EndUtc = endUtc ?? BoundaryUtc(endLocal, zone);
            Weekly = weekly;
            InProgress = endUtc.HasValue;
        }

        internal DateTime StartLocal { get; }
        internal DateTime EndLocal { get; }
        internal DateTime StartUtc { get; }
        internal DateTime EndUtc { get; }
        internal bool Weekly { get; }
        internal bool InProgress { get; }
    }

    private static DateTime BoundaryUtc(DateTime local, TimeZoneInfo zone)
    {
        // Gaps advance to the first valid instant; repeated boundaries use the earlier UTC occurrence.
        if (zone.IsInvalidTime(local))
        {
            long invalid = local.Ticks;
            DateTime valid = local.AddHours(1);
            while (zone.IsInvalidTime(valid)) valid = valid.AddHours(1);
            long upper = valid.Ticks;
            while (upper - invalid > 1)
            {
                long middle = invalid + (upper - invalid) / 2;
                if (zone.IsInvalidTime(new DateTime(middle, DateTimeKind.Unspecified))) invalid = middle;
                else upper = middle;
            }
            local = new DateTime(upper, DateTimeKind.Unspecified);
        }
        if (zone.IsAmbiguousTime(local))
            return new DateTimeOffset(local, zone.GetAmbiguousTimeOffsets(local).Max()).UtcDateTime;
        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }

    internal static Period PreviousPeriod(DateTime nowUtc, TimeZoneInfo zone, bool weekly)
    {
        if (nowUtc.Kind != DateTimeKind.Utc) throw new ArgumentException("UTC clock required", nameof(nowUtc));
        DateTime end = DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(nowUtc, zone).Date, DateTimeKind.Unspecified);
        if (weekly) end = end.AddDays(-(((int)end.DayOfWeek + 6) % 7));
        return new Period(end.AddDays(weekly ? -7 : -1), end, zone, weekly);
    }

    internal static Period CurrentPeriod(DateTime nowUtc, TimeZoneInfo zone, bool weekly)
    {
        DateTime start = PreviousPeriod(nowUtc, zone, weekly).EndLocal;
        // Preserve the captured instant even during the second occurrence of a DST-fold local time.
        return new Period(start, TimeZoneInfo.ConvertTimeFromUtc(nowUtc, zone), zone, weekly, nowUtc);
    }

    internal static string? Render(CombinedReportSnapshot snapshot, Period period, string zone, int topCount, string[] limitations,
        bool manual = false, Action<string>? diagnostic = null)
    {
        LeaderboardReport? report = BuildReport(snapshot, period, zone, topCount, limitations, manual);
        if (DiscordConnectorPlugin.StaticConfig.DebugLeaderboardOperations)
            LogReport(snapshot, report, period, topCount, manual, diagnostic);
        return report?.PlainText;
    }

    internal static LeaderboardReport? BuildReport(CombinedReportSnapshot snapshot, Period period, string zone, int topCount, string[] limitations,
        bool manual = false)
    {
        if (topCount < 1 || topCount > 16) throw new ArgumentOutOfRangeException(nameof(topCount));
        var sections = new List<(string Heading, List<(string Name, string Value)> Rows, int Included)>();
        foreach (ReportMetric metric in Enum.GetValues(typeof(ReportMetric)).Cast<ReportMetric>()
            .OrderBy(metric => metric == ReportMetric.ReportedKillCredits ? 0 : metric == ReportMetric.SailingDistance ? 8 : (int)metric))
        {
            if (!snapshot.Rows.TryGetValue(metric, out IReadOnlyList<ReportRow>? stored)) continue;
            IEnumerable<ReportRow> positive = stored.Where(row => Positive(metric, row));
            IOrderedEnumerable<ReportRow> ordered = metric switch
            {
                ReportMetric.HealthLost or ReportMetric.DamageDealt => positive.OrderByDescending(row => row.HpLoss),
                ReportMetric.SailingDistance => positive.OrderByDescending(row => row.Meters),
                ReportMetric.OnFootDistance or ReportMetric.LongestBowHit => positive.OrderByDescending(row => row.Meters),
                ReportMetric.BiomeTime or ReportMetric.OnlineTime => positive.OrderByDescending(row => row.Seconds),
                _ => positive.OrderByDescending(row => row.Count)
            };
            List<ReportRow> ranked = ordered.ThenBy(row => row.Id, StringComparer.Ordinal).Take(topCount).ToList();
            if (ranked.Count == 0) continue;
            CoverageSnapshot? coverage = snapshot.Coverage.FirstOrDefault(c => c.Metric == metric);
            if (coverage?.State == CoverageState.Unavailable) continue;
            string heading = Heading(metric);
            // Reserve every first row, including exact grouped counts, within 1,900 units.
            var rows = ranked.Select(row => (row.Name, Numeric(metric, row))).ToList();
            sections.Add((heading, rows, 1));
        }
        if (sections.Count == 0 && !manual) return null;

        string title = Title(period, manual);
        string description = sections.Count == 0 ? "No observations for enabled sections in this period." : "";
        string header = "🏆 " + title + "\n" + (description.Length == 0 ? "" : description + "\n");
        string footer = "-# " + CoverageFooter;
        if (sections.Count == 0) return new LeaderboardReport(title, description, Array.Empty<(string, string)>(), header + footer, CoverageFooter);
        // Reserve every nonempty section's heading and first row before allocating any spare rows.
        int nameLength = 32;
        string Row((string Name, string Value) row, int rank) => RankedRow(row.Name, row.Value, rank, nameLength) + "\n";
        int ReservedLength() => header.Length + footer.Length + sections.Sum(section => section.Heading.Length + 6 + Row(section.Rows[0], 0).Length);
        // Shorten only names when longer titles would otherwise crowd out a section's first row.
        while (nameLength > 1 && ReservedLength() > 1900) nameLength--;
        int length = ReservedLength();
        if (length > 1900) throw new InvalidOperationException("Report reservation exceeded.");
        for (int rank = 1; rank < topCount; rank++)
        {
            for (int i = 0; i < sections.Count; i++)
            {
                var section = sections[i];
                if (section.Rows.Count <= rank || section.Included != rank || length + Row(section.Rows[rank], rank).Length > 1900) continue;
                length += Row(section.Rows[rank], rank).Length;
                sections[i] = (section.Heading, section.Rows, rank + 1);
            }
        }
        var text = new StringBuilder(header);
        var fields = new List<(string Heading, string Value)>();
        foreach (var section in sections)
        {
            string value = string.Concat(section.Rows.Take(section.Included).Select((row, rank) => Row(row, rank))).TrimEnd('\n');
            fields.Add((section.Heading, value));
            text.Append("**").Append(section.Heading.Replace(" — ", "** — ")).Append('\n').Append(value).Append('\n');
        }
        text.Append(footer);
        return new LeaderboardReport(title, description, fields, text.ToString(), CoverageFooter);
    }

    private const string CoverageFooter = "Server-observed; some counts are partial.";

    private static string Title(Period period, bool manual = false)
    {
        string dates = period.StartLocal.ToString("MMM d", CultureInfo.InvariantCulture);
        if (period.Weekly)
        {
            DateTime end = period.InProgress ? period.EndLocal : period.EndLocal.AddDays(-1);
            dates += "–" + end.ToString(end.Month == period.StartLocal.Month ? "%d" : "MMM d", CultureInfo.InvariantCulture);
        }
        return $"{(period.Weekly ? "Weekly" : "Daily")} leaderboard — {dates}" +
            (period.InProgress ? " (so far)" : "") + (manual ? " (preview)" : "");
    }

    internal static void LogReport(CombinedReportSnapshot snapshot, LeaderboardReport? report, Period period, int topCount,
        bool manual, Action<string>? diagnostic)
    {
        if (diagnostic == null) return;
        try
        {
            // TODO: Remove or anonymize these opt-in personal-use display-name diagnostics after validation.
            diagnostic($"LeaderboardReport mode={(manual ? "manual" : "automatic")} period={(period.Weekly ? "weekly" : "daily")} " +
                $"availableFields={snapshot.Rows.Count} emittedFields={report?.Sections.Count ?? 0} emittedRows={report?.Sections.Sum(section => section.Value.Count(c => c == '\n') + 1) ?? 0} " +
                $"reportUnits={report?.PlainText.Length ?? 0} empty={(report?.Sections.Count ?? 0) == 0}");
            foreach (ReportMetric metric in Enum.GetValues(typeof(ReportMetric)))
            {
                if (!snapshot.Rows.TryGetValue(metric, out IReadOnlyList<ReportRow>? available)) continue;
                CoverageSnapshot? coverage = snapshot.Coverage.FirstOrDefault(note => note.Metric == metric);
                var line = new StringBuilder($"LeaderboardReport metric={metric} available={available.Count} coverage={coverage?.State.ToString() ?? "Unknown"} " +
                    $"gaps={coverage?.Counters.Values.Sum(value => (double)value) ?? 0} rejected={Number(coverage?.KnownLosses ?? 0)} uncertain={Number(coverage?.UncertainWrites ?? 0)} data=");
                foreach (ReportRow row in available.Take(topCount))
                {
                    string entry = SafeText(row.Name, 32) + ": " + Numeric(metric, row) + "; ";
                    if (line.Length + entry.Length > 511) { line.Append('…'); break; }
                    line.Append(entry);
                }
                diagnostic(line.ToString());
            }
        }
        catch { /* Diagnostics must not affect rendering or delivery. */ }
    }

    private static bool Positive(ReportMetric metric, ReportRow row) => metric switch
    {
        ReportMetric.HealthLost or ReportMetric.DamageDealt => row.HpLoss > 0 && !double.IsInfinity(row.HpLoss),
        ReportMetric.SailingDistance => row.Meters > 0 && !double.IsInfinity(row.Meters),
        ReportMetric.OnFootDistance or ReportMetric.LongestBowHit => row.Meters > 0 && !double.IsInfinity(row.Meters),
        ReportMetric.BiomeTime or ReportMetric.OnlineTime => row.Seconds > 0 && !double.IsInfinity(row.Seconds),
        _ => row.Count > 0
    };

    private static string Heading(ReportMetric metric) => metric switch
    {
        ReportMetric.ParticipantBossCredits => "👑 The Giantbane — boss kills",
        ReportMetric.Biomes => "🧭 The Wanderer — biomes visited",
        ReportMetric.ObservedDodgeRolls => "🌀 The Trickster — dodge rolls",
        ReportMetric.SailingDistance => "🌊 Njord's Chosen — distance sailed",
        ReportMetric.InferredPickups => "🎒 The Fafnir — items picked up",
        ReportMetric.NewPieces => "🔨 The Builder of Asgard — pieces built",
        ReportMetric.CropsPlanted => "🌱 Hand of Freyr — crops planted",
        ReportMetric.ExplorationCells => "🗺️ Huginn's Eye — areas explored",
        ReportMetric.CookingCompletions => "🍲 The Andhrimnir — meals cooked",
        ReportMetric.ReportedKillCredits => "⚔️ The Berserker — kills",
        ReportMetric.ObservedConsumableUses => "🍖 The Einherjar Feaster — meals eaten",
        ReportMetric.ReportedDodgeHitNotifications => "🤸 The Fate-Dodger — dodge hits",
        ReportMetric.HealthLost => "🩸 Baldr's Fate — health lost",
        ReportMetric.OnFootDistance => "👟 The Bifrost Runner — distance on foot",
        ReportMetric.BiomeTime => "🏡 The Hearthkeeper — most time in one biome",
        ReportMetric.OnlineTime => "⏳ The Heimdall — time online",
        ReportMetric.PortalTrips => "🌀 The Omnipresent — portal trips",
        ReportMetric.DamageDealt => "🪓 The Axe of Odin — damage dealt",
        ReportMetric.LongestBowHit => "🏹 The Hawk of Huginn — longest bow hit",
        _ => throw new ArgumentOutOfRangeException(nameof(metric))
    };

    private static string RankedRow(string name, string value, int rank, int nameLength = 49) =>
        (rank + 1).ToString(CultureInfo.InvariantCulture) + "." +
        " " + SafeText(name, nameLength) + ": " + value;

    private static string Number(double value)
    {
        bool compact = value >= 1e15 || value > 0 && value < 0.01;
        return (compact || value != Math.Round(value, 2) ? "≈" : "") +
            value.ToString(compact ? "0.#E+0" : "#,0.##", CultureInfo.InvariantCulture);
    }
    private static string Numeric(ReportMetric metric, ReportRow row) => metric switch
    {
        ReportMetric.HealthLost or ReportMetric.DamageDealt => Number(Math.Round(row.HpLoss, MidpointRounding.AwayFromZero)) + " HP",
        ReportMetric.SailingDistance or ReportMetric.OnFootDistance or ReportMetric.LongestBowHit => row.Meters >= 1000 ? Number(row.Meters / 1000) + " km" : Number(row.Meters) + " m",
        ReportMetric.OnlineTime => Duration(row.Seconds),
        ReportMetric.BiomeTime => Duration(row.Seconds) + (row.Biome.Length == 0 ? "" : " (" + SafeText(row.Biome, 12) + ")"),
        _ => row.Count.ToString("N0", CultureInfo.InvariantCulture)
    };

    private static string Duration(double seconds) => seconds >= 3600 ? Number(seconds / 3600) + " h" :
        seconds >= 60 ? Number(seconds / 60) + " min" : Number(seconds) + " s";

    private static string SafeText(string value, int maxLength = 49)
    {
        if (string.IsNullOrWhiteSpace(value)) return "Unknown";
        var text = new StringBuilder();
        int i = 0;
        for (; i < value.Length; i++)
        {
            char c = value[i];
            string token;
            if (char.IsWhiteSpace(c)) token = " ";
            else if (char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format) continue;
            else if (c == '@') token = "@\u200b";
            else if (c == '<') token = "‹";
            else if (c == '>') token = "›";
            else if ("\\*_~`|[]()#-".IndexOf(c) >= 0) token = "\\" + c;
            else if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                token = value.Substring(i, 2);
            else if (char.IsSurrogate(c)) continue;
            else token = c.ToString();
            if (text.Length + token.Length > maxLength - 1) break;
            text.Append(token);
            if (token.Length == 2 && char.IsHighSurrogate(c)) i++;
        }
        if (i < value.Length) text.Append('…');
        return text.Length == 0 ? "Unknown" : text.ToString();
    }

    public void Dispose()
    {
        disposed = true;
        foreach (PendingReport job in pending)
        {
            // Disposal does not wait for storage; observe abandoned failures without a send continuation.
            Task? task = (Task?)job.Claim ?? job.Query;
            if (task != null) _ = task.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        }
        pending.Clear();
    }
}
