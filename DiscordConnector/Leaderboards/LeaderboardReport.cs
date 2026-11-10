using System.Collections.Generic;
using System.Linq;

namespace DiscordConnector.Leaderboards;

internal sealed class LeaderboardReport
{
    internal string Title { get; }
    internal string Description { get; }
    internal string Footer { get; }
    internal IReadOnlyList<(string Heading, string Value)> Sections { get; }
    internal string PlainText { get; }

    internal LeaderboardReport(string title, string description,
        IEnumerable<(string Heading, string Value)> sections, string plainText, string footer = "")
    {
        Title = title;
        Description = description;
        Footer = footer;
        Sections = System.Array.AsReadOnly(sections.ToArray());
        PlainText = plainText;
    }
}
