using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

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
    private readonly int topCount;
    private readonly Func<string, DateTime, DateTime, List<CombatRecord>> query;
    private readonly Func<string, DateTime, bool> tryClaim;
    private readonly Action<string> send;
    private readonly Action<string> warn;
    private readonly TimeZoneInfo? timeZone;
    private string worldId = "";
    private DateTime dailyEndUtc;
    private DateTime weeklyEndUtc;
    private bool disposed;

    internal CombatReports(bool dailyEnabled, bool weeklyEnabled, string timeZoneId, int topCount,
        Func<string, DateTime, DateTime, List<CombatRecord>> query,
        Func<string, DateTime, bool> tryClaim, Action<string> send, Action<string> warn)
    {
        this.dailyEnabled = dailyEnabled;
        this.weeklyEnabled = weeklyEnabled;
        this.topCount = topCount;
        this.query = query;
        this.tryClaim = tryClaim;
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
                worldId = currentWorldId;
                dailyEndUtc = day.EndUtc;
                weeklyEndUtc = week.EndUtc;
                return;
            }

            if (day.EndUtc > dailyEndUtc)
            {
                dailyEndUtc = day.EndUtc;
                if (dailyEnabled) Attempt(day, nowUtc);
            }

            if (week.EndUtc > weeklyEndUtc)
            {
                weeklyEndUtc = week.EndUtc;
                if (weeklyEnabled) Attempt(week, nowUtc);
            }
        }
        catch (Exception ex)
        {
            Dispose();
            warn($"Combat reports disabled after calendar failure ({ex.GetType().Name}); tracking is unchanged.");
        }
    }

    private void Attempt(Period period, DateTime nowUtc)
    {
        try
        {
            string? report = Render(query(worldId, period.StartUtc, period.EndUtc), period, timeZone!.Id, topCount);
            if (report == null) return;
            // Length prefixes keep world/timezone boundaries unambiguous; no names or webhook URLs in keys/logs.
            string id = "combat-report:v1:" + worldId.Length + ":" + worldId + ":" + timeZone.Id.Length + ":" +
                timeZone.Id + ":" + (period.Weekly ? "week:" : "day:") +
                period.StartUtc.Ticks.ToString(CultureInfo.InvariantCulture) + ":" +
                period.EndUtc.Ticks.ToString(CultureInfo.InvariantCulture);
            if (tryClaim(id, nowUtc)) send(report);
        }
        catch (Exception ex)
        {
            warn($"Combat report attempt failed ({ex.GetType().Name}); no automatic retry. Tracking is unchanged.");
        }
    }

    internal readonly struct Period
    {
        internal Period(DateTime startLocal, DateTime endLocal, TimeZoneInfo zone, bool weekly)
        {
            StartLocal = startLocal;
            EndLocal = endLocal;
            StartUtc = BoundaryUtc(startLocal, zone);
            EndUtc = BoundaryUtc(endLocal, zone);
            Weekly = weekly;
        }

        internal DateTime StartLocal { get; }
        internal DateTime EndLocal { get; }
        internal DateTime StartUtc { get; }
        internal DateTime EndUtc { get; }
        internal bool Weekly { get; }
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

    internal static string? Render(IEnumerable<CombatRecord> records, Period period, string timeZoneId, int topCount)
    {
        var players = records.Where(r => !string.IsNullOrWhiteSpace(r.PlayerId))
            .GroupBy(r => r.PlayerId, StringComparer.Ordinal).ToList();
        if (players.Count == 0) return null;
        var rankings = players.Select(g => new
        {
            Id = g.Key,
            Name = g.OrderByDescending(r => r.TimestampUtc).ThenByDescending(r => r.Id, StringComparer.Ordinal).First().PlayerName,
            Creatures = g.Count(r => !r.IsBoss),
            Bosses = g.Count(r => r.IsBoss)
        }).ToList();
        string dates = period.StartLocal.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (period.Weekly) dates += " to " + period.EndLocal.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var text = new StringBuilder($"**SERVER-OBSERVED combat — {(period.Weekly ? "weekly" : "daily")}**\n" +
            $"{dates} ({SafeText(timeZoneId)})\nPartial server-owned observations only (partial coverage).\n");

        AddSection(text, "Creature kills (excluding bosses)", rankings.Where(p => p.Creatures > 0)
            .OrderByDescending(p => p.Creatures).ThenBy(p => p.Id, StringComparer.Ordinal).Take(topCount)
            .Select((p, i) => $"{i + 1}. {SafeText(p.Name)}: {p.Creatures}"));
        AddSection(text, "Boss kills", rankings.Where(p => p.Bosses > 0)
            .OrderByDescending(p => p.Bosses).ThenBy(p => p.Id, StringComparer.Ordinal).Take(topCount)
            .Select((p, i) => $"{i + 1}. {SafeText(p.Name)}: {p.Bosses}"));
        var rows = players.SelectMany(g => g).ToList();
        AddSection(text, "Creature types (including bosses)", Totals(rows, r => r.Creature));
        AddSection(text, "Biomes", Totals(rows, r => r.Biome));
        return text.ToString();
    }

    private static IEnumerable<string> Totals(IEnumerable<CombatRecord> rows, Func<CombatRecord, string> key) =>
        rows.GroupBy(key, StringComparer.Ordinal).OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{SafeText(g.Key)}: {g.Count()}");

    private static void AddSection(StringBuilder text, string title, IEnumerable<string> lines)
    {
        // Four independently bounded sections preserve every category within one <1900-character message.
        var section = new StringBuilder("\n**" + title + "**\n");
        bool any = false;
        foreach (string line in lines)
        {
            any = true;
            if (section.Length + line.Length + 1 > 380)
            {
                section.Append("… (truncated)\n");
                break;
            }
            section.Append(line).Append('\n');
        }
        if (!any) section.Append("None observed\n");
        text.Append(section);
    }

    private static string SafeText(string value)
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
            if (text.Length + token.Length > 48) break;
            text.Append(token);
            if (token.Length == 2 && char.IsHighSurrogate(c)) i++;
        }
        if (i < value.Length) text.Append('…');
        return text.Length == 0 ? "Unknown" : text.ToString();
    }

    public void Dispose() => disposed = true;
}
