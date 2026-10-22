using Neruna.Core.Accounts;
using Neruna.Core.Mail;
using Neruna.Providers.Imap.Sieve;

namespace Neruna.Client.Tests;

/// <summary>An out-of-office series ("jeden Freitag"): by the server itself (Sieve), or renewed by Neruna (Exchange, Microsoft 365).</summary>
public class AutoReplySeriesTests
{
    private static DateTimeOffset Local(int year, int month, int day, int hour = 0) =>
        new(new DateTime(year, month, day, hour, 0, 0), TimeZoneInfo.Local.GetUtcOffset(new DateTime(year, month, day, hour, 0, 0)));

    [Fact]
    public void The_next_period_is_today_or_the_next_series_day_with_the_days_right_after_it()
    {
        // Thursday 22 October 2026.
        var thursday = Local(2026, 10, 22, 15);
        Assert.Equal((Local(2026, 10, 23), Local(2026, 10, 24)), AutoReplySeries.NextPeriod([DayOfWeek.Friday], thursday));
        Assert.Equal((Local(2026, 10, 22), Local(2026, 10, 23)), AutoReplySeries.NextPeriod([DayOfWeek.Thursday], thursday)); // today, though it has begun
        Assert.Equal((Local(2026, 10, 22), Local(2026, 10, 24)), AutoReplySeries.NextPeriod([DayOfWeek.Thursday, DayOfWeek.Friday], thursday)); // one period
        Assert.Equal((Local(2026, 10, 26), Local(2026, 10, 27)), AutoReplySeries.NextPeriod([DayOfWeek.Monday], thursday));
        Assert.Equal((Local(2026, 10, 22), Local(2026, 10, 29)), AutoReplySeries.NextPeriod([.. AutoReplySeries.Week], thursday));
    }

    [Fact]
    public void A_series_is_active_on_its_days_only()
    {
        var fridays = new AutoReply(true, "Freitags nicht im Büro", Weekdays: [DayOfWeek.Friday]);
        Assert.True(fridays.IsActive(Local(2026, 10, 23, 9)));
        Assert.False(fridays.IsActive(Local(2026, 10, 22, 9)));
        Assert.False((fridays with { IsEnabled = false }).IsActive(Local(2026, 10, 23, 9)));
        Assert.Equal("Jeden Freitag", AutoReplySeries.Describe([DayOfWeek.Friday], System.Globalization.CultureInfo.GetCultureInfo("de-CH")));
    }

    [Fact]
    public void Sieve_answers_on_the_weekdays_itself_and_reads_the_series_back()
    {
        var reply = new AutoReply(true, "Freitags nicht im Büro", Weekdays: [DayOfWeek.Monday, DayOfWeek.Friday]);
        var script = SieveVacation.Write(null, reply, ["anna@example.com"], withDates: false, withWeekdays: true);

        Assert.StartsWith("require [\"vacation\", \"date\"];", script, StringComparison.Ordinal);
        Assert.Contains(":is \"weekday\" [\"1\", \"5\"] {", script, StringComparison.Ordinal);
        var read = SieveVacation.Read(script);
        Assert.Equal([DayOfWeek.Monday, DayOfWeek.Friday], read.Weekdays);
        Assert.False(read.IsScheduled);
        Assert.Throws<ArgumentException>(() => SieveVacation.Write(null, reply, ["anna@example.com"], withDates: true, withWeekdays: false));
    }

    [Fact]
    public async Task Without_weekdays_on_the_server_neruna_sets_the_next_day_and_renews_it_when_it_has_passed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var env = await TestEnvironment.CreateAsync();
        var connection = await env.AddAccountAsync(ServiceKind.Mail, "fake");
        var account = (await env.Accounts.GetAccountsAsync(ct)).Single();
        env.MailServer.AutoReplyFeatures = AutoReplyFeatures.Schedule;

        var series = new AutoReply(true, "Freitags nicht im Büro", Weekdays: [DayOfWeek.Friday]);
        await env.Mail.SetAutoReplyAsync(account, connection, series, ct);

        // The server got the next Friday as its period; Neruna shows the series.
        var onServer = env.MailServer.AutoReply;
        Assert.Null(onServer.Weekdays);
        Assert.Equal(AutoReplySeries.NextPeriod([DayOfWeek.Friday], DateTimeOffset.Now), (onServer.Start!.Value, onServer.End!.Value));
        var (shown, _) = await env.Mail.GetAutoReplyAsync(connection, ct);
        Assert.Equal([DayOfWeek.Friday], shown.Weekdays);
        Assert.Equal(1, env.MailServer.AutoReplyWrites);

        // That Friday is over: the next start of Neruna sets the following one.
        var lastFriday = Enumerable.Range(1, 7).Select(i => DateTime.Today.AddDays(-i)).First(d => d.DayOfWeek == DayOfWeek.Friday);
        env.MailServer.AutoReply = onServer with { Start = Local(lastFriday.Year, lastFriday.Month, lastFriday.Day), End = Local(lastFriday.Year, lastFriday.Month, lastFriday.Day).AddDays(1) };
        (shown, _) = await env.Mail.GetAutoReplyAsync(connection, ct);
        Assert.Equal(2, env.MailServer.AutoReplyWrites);
        Assert.True(env.MailServer.AutoReply.End > DateTimeOffset.Now);
        Assert.Equal(DayOfWeek.Friday, env.MailServer.AutoReply.Start!.Value.DayOfWeek);

        // Switched off in the webmail: the series ends, nothing is set again.
        env.MailServer.AutoReply = env.MailServer.AutoReply with { IsEnabled = false };
        (shown, _) = await env.Mail.GetAutoReplyAsync(connection, ct);
        Assert.Null(shown.Weekdays);
        Assert.Equal(2, env.MailServer.AutoReplyWrites);
    }

    [Fact]
    public async Task A_server_with_neither_weekdays_nor_periods_cannot_have_a_series()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var env = await TestEnvironment.CreateAsync();
        var connection = await env.AddAccountAsync(ServiceKind.Mail, "fake");
        var account = (await env.Accounts.GetAccountsAsync(ct)).Single();
        env.MailServer.AutoReplyFeatures = AutoReplyFeatures.None;

        await Assert.ThrowsAsync<AutoReplyUnavailableException>(() =>
            env.Mail.SetAutoReplyAsync(account, connection, new AutoReply(true, "x", Weekdays: [DayOfWeek.Friday]), ct));
    }
}
