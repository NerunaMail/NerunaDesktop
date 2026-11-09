using Neruna.Contracts.Cloud;
using Neruna.Core.Cloud;

namespace Neruna.Client.Tests;

public sealed class SchedulingPollsTests
{
    private static readonly CloudPollOption Monday = new("a", new DateTimeOffset(2026, 12, 7, 8, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 12, 7, 9, 0, 0, TimeSpan.Zero), false);
    private static readonly CloudPollOption Tuesday = new("b", new DateTimeOffset(2026, 12, 8, 8, 0, 0, TimeSpan.Zero), null, false);
    private static readonly CloudPollOption Wednesday = new("c", new DateTimeOffset(2026, 12, 9, 0, 0, 0, TimeSpan.Zero), null, true);

    private static CloudPollParticipant Person(string name, params (string Option, string Answer)[] answers) =>
        new(name, name, null, false, null, null, answers.Length > 0 ? DateTimeOffset.UnixEpoch : null, answers.ToDictionary(a => a.Option, a => a.Answer));

    private static CloudPoll Poll(params CloudPollParticipant[] people) =>
        new("p", "Sitzung", "Raum 2", null, "Europe/Zurich", "all", false, true, new Uri("https://neruna.cloud/poll/abc"), null,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, [Monday, Tuesday, Wednesday], people);

    [Fact]
    public void Counts_answers_and_finds_the_best_dates()
    {
        var poll = Poll(
            Person("Anna", ("a", "yes"), ("b", "yes"), ("c", "no")),
            Person("Beat", ("a", "ifneeded"), ("b", "yes"), ("c", "maybe")),
            Person("Carla", ("a", "yes"), ("b", "maybe")));

        Assert.Equal(1, SchedulingPolls.Count(poll, Wednesday)["no"]);
        Assert.Equal(2, SchedulingPolls.Count(poll, Monday)["yes"]);
        // Monday: 2 yes + 1 if needed (2.9) beats Tuesday: 2 yes + 1 maybe (2.5).
        Assert.Equal(["a"], SchedulingPolls.Best(poll).Select(o => o.Id));
        Assert.Empty(SchedulingPolls.Best(Poll(Person("Dora"))));
    }

    [Fact]
    public void Equal_dates_are_all_best()
    {
        var poll = Poll(Person("Anna", ("a", "yes"), ("b", "yes"), ("c", "no")));
        Assert.Equal(["a", "b"], SchedulingPolls.Best(poll).Select(o => o.Id));
    }

    [Fact]
    public void The_mail_text_has_the_link_and_password()
    {
        var (html, text) = SchedulingPolls.InvitationText(Poll(), "geheim & gut");

        Assert.Contains("https://neruna.cloud/poll/abc", text, StringComparison.Ordinal);
        Assert.Contains("geheim & gut", text, StringComparison.Ordinal);
        Assert.Contains("<a href=\"https://neruna.cloud/poll/abc\">", html, StringComparison.Ordinal);
        Assert.Contains("geheim &amp; gut", html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_whole_day_shows_no_time_and_changing_keeps_ids()
    {
        Assert.DoesNotContain(":", SchedulingPolls.When(Wednesday, System.Globalization.CultureInfo.InvariantCulture).Split('·')[0], StringComparison.Ordinal);

        var poll = Poll(Person("Anna", ("a", "yes")) with { Invited = true, Email = "anna@example.com" }, Person("Beat", ("a", "no")));
        var request = SchedulingPolls.ToRequest(poll);
        Assert.Equal(["a", "b", "c"], request.Options.Select(o => o.Id));
        Assert.Null(request.Password);
        Assert.Equal("anna@example.com", Assert.Single(request.Invitees).Email);
        Assert.False(string.IsNullOrEmpty(SchedulingPolls.LocalTimeZoneId()));
    }
}
