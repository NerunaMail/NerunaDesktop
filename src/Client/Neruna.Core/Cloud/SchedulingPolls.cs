using System.Globalization;
using System.Net;
using Neruna.Contracts.Cloud;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Core.Cloud;

/// <summary>
/// Scheduling polls (Terminumfrage): planned here, answered on a web page of a connected organisation's Neruna Cloud
/// (with several, the person chooses which one when planning). Only while a poll runs is it kept there; fixing the date
/// deletes it (the event with its invitations is the result).
/// </summary>
public sealed class SchedulingPolls(CloudController cloud)
{
    /// <summary>The answers a participant can give, in this order on the page.</summary>
    public static readonly IReadOnlyList<string> Answers = ["yes", "ifneeded", "maybe", "no"];

    /// <summary>What a new poll offers unless the planner chooses otherwise.</summary>
    public static readonly IReadOnlyList<string> DefaultChoices = ["yes", "maybe", "no"];

    /// <summary>The answers a poll offers, in the order of <see cref="Answers"/> (an older server: all four).</summary>
    public static IReadOnlyList<string> Choices(CloudPoll poll)
    {
        ArgumentNullException.ThrowIfNull(poll);
        return poll.Choices is { } chosen ? Answers.Where(a => a == "yes" || chosen.Contains(a)).ToList() : Answers;
    }

    /// <summary>The organisations a poll can be made with; empty without Neruna Cloud.</summary>
    public Task<IReadOnlyList<CloudConnection>> ConnectionsAsync(CancellationToken cancellationToken = default) => cloud.GetConnectionsAsync(cancellationToken);

    /// <summary>The person's polls with one organisation; null when its server does not know polls yet.</summary>
    public async Task<IReadOnlyList<CloudPoll>?> ListAsync(CloudConnection connection, CancellationToken cancellationToken = default) =>
        (await cloud.GetPollsAsync(connection, cancellationToken))?.Polls;

    public Task<CloudPoll> SaveAsync(CloudConnection connection, string? id, PollRequest poll, CancellationToken cancellationToken = default) =>
        cloud.SavePollAsync(connection, id, poll, cancellationToken);

    public Task DeleteAsync(CloudConnection connection, string id, CancellationToken cancellationToken = default) =>
        cloud.DeletePollAsync(connection, id, cancellationToken);

    /// <summary>
    /// Over but not fixed yet: answers are closed (the deadline passed) or every proposed date is past – the planner
    /// should fix the date (or delete the poll).
    /// </summary>
    public static bool IsOverdue(CloudPoll poll, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(poll);
        return poll.Deadline < now
               || (poll.Options.Count > 0 && poll.Options.All(o => (o.End ?? (o.AllDay ? o.Start.AddDays(1) : o.Start)) < now));
    }

    /// <summary>
    /// How many polls run (with every organisation) and how many of them are over (<see cref="IsOverdue"/>); an
    /// organisation that cannot be reached counts nothing.
    /// </summary>
    public async Task<(int Open, int Overdue)> SummaryAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var (open, overdue) = (0, 0);
        foreach (var connection in await ConnectionsAsync(cancellationToken))
        {
            try
            {
                foreach (var poll in await ListAsync(connection, cancellationToken) ?? [])
                {
                    open++;
                    overdue += IsOverdue(poll, now) ? 1 : 0;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or CloudException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                // Not reachable now: the next sync counts again.
            }
        }

        return (open, overdue);
    }

    /// <summary>The IANA name of this computer's time zone (Windows names are converted); UTC when unknown.</summary>
    public static string LocalTimeZoneId()
    {
        var local = TimeZoneInfo.Local;
        if (local.HasIanaId)
        {
            return local.Id;
        }

        return TimeZoneInfo.TryConvertWindowsIdToIanaId(local.Id, out var iana) ? iana : "UTC";
    }

    /// <summary>"Ja", "Wenn nötig", "Vielleicht", "Nein".</summary>
    public static string AnswerText(string answer) => answer switch
    {
        "yes" => T("Ja"),
        "ifneeded" => T("Wenn nötig"),
        "maybe" => T("Vielleicht"),
        "no" => T("Nein"),
        _ => answer,
    };

    /// <summary>"Di, 8. Dez. 2026, 09:00–11:00" (local time), a whole day without a time.</summary>
    public static string When(CloudPollOption option, CultureInfo? culture = null)
    {
        ArgumentNullException.ThrowIfNull(option);
        culture ??= CultureInfo.CurrentCulture;
        if (option.AllDay)
        {
            var day = option.Start.UtcDateTime.Date;
            var last = option.End is { } end && end.UtcDateTime.Date > day ? end.UtcDateTime.Date : day;
            return last == day
                ? day.ToString("ddd, d. MMM yyyy", culture) + " · " + T("ganztägig")
                : day.ToString("ddd, d. MMM", culture) + " – " + last.ToString("ddd, d. MMM yyyy", culture) + " · " + T("ganztägig");
        }

        var start = option.Start.ToLocalTime();
        var text = start.ToString("ddd, d. MMM yyyy, HH:mm", culture);
        if (option.End is { } to)
        {
            var stop = to.ToLocalTime();
            text += stop.Date == start.Date ? "–" + stop.ToString("HH:mm", culture) : " – " + stop.ToString("ddd, d. MMM yyyy, HH:mm", culture);
        }

        return text;
    }

    /// <summary>How many gave each answer to an option.</summary>
    public static IReadOnlyDictionary<string, int> Count(CloudPoll poll, CloudPollOption option)
    {
        ArgumentNullException.ThrowIfNull(poll);
        ArgumentNullException.ThrowIfNull(option);
        return Answers.ToDictionary(a => a, a => poll.Participants.Count(p => p.Answers.TryGetValue(option.Id, out var given) && given == a));
    }

    /// <summary>
    /// The options that suit best: most yes plus "if needed" (counted almost fully), then "maybe" (half); none when no
    /// one answered yet. Several when they are equal.
    /// </summary>
    public static IReadOnlyList<CloudPollOption> Best(CloudPoll poll)
    {
        ArgumentNullException.ThrowIfNull(poll);
        if (!poll.Participants.Any(p => p.AnsweredAt is not null))
        {
            return [];
        }

        var scored = poll.Options.Select(o => (Option: o, Score: Score(Count(poll, o)))).ToList();
        var top = scored.Max(s => s.Score);
        return top <= 0 ? [] : scored.Where(s => s.Score == top).Select(s => s.Option).ToList();
    }

    private static double Score(IReadOnlyDictionary<string, int> counts) => counts["yes"] + (counts["ifneeded"] * 0.9) + (counts["maybe"] * 0.5);

    /// <summary>
    /// The text for a mail: title, the link and (when set) the password; as HTML and as plain text.
    /// </summary>
    public static (string Html, string Text) InvitationText(CloudPoll poll, string? password, CultureInfo? culture = null)
    {
        ArgumentNullException.ThrowIfNull(poll);
        culture ??= CultureInfo.CurrentCulture;
        var lines = new List<(string Label, string Value, bool IsLink)>();
        if (poll.Url is { } url)
        {
            lines.Add((T("Link"), url.AbsoluteUri, true));
        }

        if (!string.IsNullOrEmpty(password))
        {
            lines.Add((T("Passwort"), password, false));
        }

        if (poll.Deadline is { } deadline)
        {
            lines.Add((T("Antworten bis"), deadline.ToLocalTime().ToString("ddd, d. MMM yyyy, HH:mm", culture), false));
        }

        var intro = F("Terminumfrage «{0}» – bitte die passenden Termine wählen:", poll.Title);
        var text = intro + "\n" + string.Join("\n", lines.Select(l => $"{l.Label}: {l.Value}")) + "\n";
        var html = "<p>" + WebUtility.HtmlEncode(intro) + "<br>"
                   + string.Join("<br>", lines.Select(l => "<b>" + WebUtility.HtmlEncode(l.Label) + ":</b> "
                       + (l.IsLink ? $"<a href=\"{WebUtility.HtmlEncode(l.Value)}\">{WebUtility.HtmlEncode(l.Value)}</a>" : WebUtility.HtmlEncode(l.Value))))
                   + "</p>";
        return (html, text);
    }

    /// <summary>The poll as a request again (to change it): everything kept, the password unchanged.</summary>
    public static PollRequest ToRequest(CloudPoll poll)
    {
        ArgumentNullException.ThrowIfNull(poll);
        return new PollRequest(poll.Title, poll.Location, poll.Description, poll.TimeZone, poll.Visibility, null, poll.OpenLink, poll.Deadline,
            poll.Options.Select(o => new PollOptionRequest(o.Id, o.Start, o.End, o.AllDay)).ToList(),
            poll.Participants.Where(p => p.Invited).Select(p => new PollInviteeRequest(p.Id, p.Name, p.Email)).ToList(), poll.Choices);
    }
}
