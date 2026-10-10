using Microsoft.Extensions.Logging.Abstractions;
using Neruna.Contracts.Discovery;
using Neruna.Core.Mail;
using Neruna.Providers.Imap;
using Neruna.Providers.Imap.Sieve;

namespace Neruna.Client.Tests;

/// <summary>The out-of-office reply in Sieve: a marked block in the user's script, filters made elsewhere stay.</summary>
public class SieveVacationTests
{
    private const string UserFilters = "# Filter aus dem Webmail\r\nrequire [\"fileinto\"];\r\nif header :contains \"subject\" \"Rechnung\" {\r\n  fileinto \"Rechnungen\";\r\n}\r\n";

    private static readonly DateTimeOffset Monday = new(2026, 10, 12, 0, 0, 0, TimeSpan.FromHours(2));

    [Fact]
    public void A_new_script_gets_the_reply_and_reads_back_the_same()
    {
        var reply = new AutoReply(true, "Bin weg – \"bald\" zurück.\nGruss\\Anna", Subject: "Abwesend");
        var script = SieveVacation.Write(null, reply, ["anna@example.com", "info@example.com"], withDates: true);

        Assert.StartsWith("require [\"vacation\"];", script, StringComparison.Ordinal);
        Assert.Contains("vacation :days 1 :subject \"Abwesend\" :addresses [\"anna@example.com\", \"info@example.com\"] \"Bin weg – \\\"bald\\\" zurück.\r\nGruss\\\\Anna\";", script, StringComparison.Ordinal);
        Assert.Equal((true, "Abwesend", false), (SieveVacation.Read(script).IsEnabled, SieveVacation.Read(script).Subject, SieveVacation.Read(script).IsScheduled));
        Assert.Equal(reply.Message, SieveVacation.Read(script).Message);
    }

    [Fact]
    public void The_users_filters_stay_and_the_block_is_replaced_not_added_twice()
    {
        var on = SieveVacation.Write(UserFilters, new AutoReply(true, "Ferien", Monday, Monday.AddDays(5)), ["anna@example.com"], withDates: true);
        Assert.StartsWith("require [\"vacation\", \"date\", \"relational\"];", on, StringComparison.Ordinal);
        Assert.Contains("fileinto \"Rechnungen\";", on, StringComparison.Ordinal);
        Assert.Contains("currentdate :zone \"+0200\" :value \"ge\" \"date\" \"2026-10-12\"", on, StringComparison.Ordinal);
        Assert.Contains("currentdate :zone \"+0200\" :value \"lt\" \"date\" \"2026-10-17\"", on, StringComparison.Ordinal);
        // After the user's require, before their rules.
        Assert.True(on.IndexOf("require [\"fileinto\"]", StringComparison.Ordinal) < on.IndexOf("vacation :days", StringComparison.Ordinal));
        Assert.True(on.IndexOf("vacation :days", StringComparison.Ordinal) < on.IndexOf("if header", StringComparison.Ordinal));

        var again = SieveVacation.Write(on, new AutoReply(true, "Andere Ferien"), ["anna@example.com"], withDates: true);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(again, "vacation :days"));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(again, "# BEGIN Neruna-Abwesenheit"));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(again, "require \\[\"vacation\""));

        // Off: no rule and no require of ours, the text is kept for next time, the user's script is as it was.
        var off = SieveVacation.Write(again, new AutoReply(false, "Andere Ferien"), ["anna@example.com"], withDates: true);
        Assert.DoesNotContain("vacation :days", off, StringComparison.Ordinal);
        Assert.DoesNotContain("\"vacation\"", off, StringComparison.Ordinal);
        Assert.Equal(("Andere Ferien", false), (SieveVacation.Read(off).Message, SieveVacation.Read(off).IsEnabled));
        Assert.Contains(UserFilters.Replace("# Filter aus dem Webmail\r\nrequire [\"fileinto\"];\r\n", string.Empty, StringComparison.Ordinal), off, StringComparison.Ordinal);
        Assert.StartsWith("# Filter aus dem Webmail\r\nrequire [\"fileinto\"];\r\n", off, StringComparison.Ordinal);
        Assert.False(SieveVacation.HasOtherVacation(off));
    }

    [Fact]
    public void A_period_needs_the_date_extension()
    {
        Assert.Throws<ArgumentException>(() => SieveVacation.Write(null, new AutoReply(true, "x", Monday, Monday.AddDays(1)), [], withDates: false));
        Assert.True(SieveVacation.HasOtherVacation("require \"vacation\";\r\nvacation \"anders\";\r\n"));
    }

    private static readonly string? Host = Environment.GetEnvironmentVariable("NERUNA_TEST_SIEVE_HOST");

    [Fact]
    public async Task ManageSieve_server_accepts_the_reply_and_keeps_the_users_filters()
    {
        Assert.SkipWhen(Host is null, "NERUNA_TEST_SIEVE_HOST not set");
        var ct = TestContext.Current.CancellationToken;
        await using var env = await TestEnvironment.CreateAsync();
        var credentials = env.Get<Neruna.Core.Security.ICredentialStore>();

        // A user with filters from the webmail …
        var user = $"anna-{Guid.NewGuid():N}@example.com";
        await using (var sieve = await ManageSieveClient.ConnectAsync(Host!, ImapMailProvider.SievePort, allowPlainText: true, ct))
        {
            await sieve.AuthenticateAsync(user, "pass", ct);
            await sieve.PutScriptAsync("webmail", UserFilters, ct);
            await sieve.SetActiveAsync("webmail", ct);
        }

        var connectionId = Guid.NewGuid();
        await credentials.SetSecretAsync(connectionId, "pass", ct);
        var settings = new ImapSettings(Host!, 3143, SocketSecurity.None, Host!, 3025, SocketSecurity.None, user);
        await using var provider = new ImapMailProvider(connectionId, settings, credentials, NullLogger<ImapMailProvider>.Instance);

        var (initial, features) = await provider.GetAutoReplyAsync(ct);
        Assert.False(initial.IsEnabled);
        Assert.Equal(AutoReplyFeatures.Schedule | AutoReplyFeatures.Subject, features);

        // … switches the reply on for a period: the server checks the script's syntax when it is stored.
        var reply = new AutoReply(true, "Bin in den Ferien.\nGruss Anna", Monday, Monday.AddDays(5), "Abwesend");
        await provider.SetAutoReplyAsync(reply, [user, "info@example.com"], ct);
        var (stored, _) = await provider.GetAutoReplyAsync(ct);
        Assert.Equal((true, reply.Message, "Abwesend", reply.Start!.Value.Date, reply.End!.Value.Date), (stored.IsEnabled, stored.Message, stored.Subject, stored.Start!.Value.Date, stored.End!.Value.Date));

        await using (var sieve = await ManageSieveClient.ConnectAsync(Host!, ImapMailProvider.SievePort, allowPlainText: true, ct))
        {
            await sieve.AuthenticateAsync(user, "pass", ct);
            var scripts = await sieve.ListScriptsAsync(ct);
            Assert.Equal("webmail", Assert.Single(scripts, s => s.Active).Name);
            Assert.Contains("fileinto \"Rechnungen\"", await sieve.GetScriptAsync("webmail", ct), StringComparison.Ordinal);
        }

        await provider.SetAutoReplyAsync(reply with { IsEnabled = false }, [user], ct);
        var (off, _) = await provider.GetAutoReplyAsync(ct);
        Assert.Equal((false, reply.Message), (off.IsEnabled, off.Message));

        // Someone without any script gets one of Neruna's own, active.
        var fresh = $"lea-{Guid.NewGuid():N}@example.com";
        var freshId = Guid.NewGuid();
        await credentials.SetSecretAsync(freshId, "pass", ct);
        await using var leaProvider = new ImapMailProvider(freshId, settings with { Username = fresh }, credentials, NullLogger<ImapMailProvider>.Instance);
        await leaProvider.SetAutoReplyAsync(new AutoReply(true, "Weg."), [fresh], ct);
        Assert.True((await leaProvider.GetAutoReplyAsync(ct)).Reply.IsEnabled);
    }
}
