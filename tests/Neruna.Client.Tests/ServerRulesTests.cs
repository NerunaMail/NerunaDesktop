using Microsoft.Extensions.Logging.Abstractions;
using Neruna.Contracts.Discovery;
using Neruna.Core.Mail;
using Neruna.Providers.Imap;
using Neruna.Providers.Imap.Sieve;

namespace Neruna.Client.Tests;

/// <summary>
/// Server rules as Sieve: Neruna's block at the end of the active script, next to the user's filters and the
/// out-of-office reply. With a real ManageSieve server (which checks the syntax):
///   NERUNA_TEST_SIEVE_HOST=127.0.0.1 dotnet test
/// </summary>
public class ServerRulesTests
{
    private const string UserFilters = "require [\"fileinto\"];\r\n# Webmail\r\nif header :contains \"subject\" \"Newsletter\" {\r\n  fileinto \"Newsletter\";\r\n}\r\n";

    private static readonly ServerRule Invoices = new(null, "Rechnungen", true,
        [new RuleCondition(RuleField.From, "rechnung@lieferant.ch"), new RuleCondition(RuleField.Subject, "Rechnung \"Oktober\"")],
        MoveTo: "INBOX.Rechnungen", MarkRead: true, Flag: true, Stop: true);

    [Fact]
    public void Rules_go_after_the_users_filters_and_the_reply_and_read_back_the_same()
    {
        var withReply = SieveVacation.Write(UserFilters, new AutoReply(true, "Weg."), ["anna@example.com"], withDates: false);
        var rules = new[]
        {
            Invoices with { Id = "r1" },
            new ServerRule("r2", "Chef weiterleiten", true, [new RuleCondition(RuleField.Recipient, "chef@firma.ch")], ForwardTo: "anna@privat.ch"),
            new ServerRule("r3", "Werbung weg", true, [new RuleCondition(RuleField.Body, "abmelden")], Delete: true),
            new ServerRule("r4", "Aus", false, [new RuleCondition(RuleField.Subject, "x")], MarkRead: true),
        };

        var script = SieveRules.Write(withReply, rules, "INBOX.Trash", hasCopy: true);

        Assert.Equal(rules, SieveRules.Read(script), new RuleComparer());
        Assert.Contains("if header :contains \"subject\" \"Newsletter\"", script, StringComparison.Ordinal); // the user's filter stays
        Assert.True(script.IndexOf("vacation", StringComparison.Ordinal) < script.IndexOf("# BEGIN Neruna-Regeln", StringComparison.Ordinal)); // the reply answers first
        Assert.Contains("if allof (address :contains \"from\" \"rechnung@lieferant.ch\", header :contains \"subject\" \"Rechnung \\\"Oktober\\\"\") {", script, StringComparison.Ordinal);
        Assert.Contains("  addflag \"\\\\Seen\";\r\n  addflag \"\\\\Flagged\";\r\n  fileinto \"INBOX.Rechnungen\";\r\n  stop;", script, StringComparison.Ordinal);
        Assert.Contains("address :contains [\"to\", \"cc\"] \"chef@firma.ch\"", script, StringComparison.Ordinal);
        Assert.Contains("redirect :copy \"anna@privat.ch\";", script, StringComparison.Ordinal);
        Assert.Contains("body :text :contains \"abmelden\"", script, StringComparison.Ordinal);
        Assert.Contains("fileinto \"INBOX.Trash\";", script, StringComparison.Ordinal);
        Assert.DoesNotContain("# Aus\r\n", script, StringComparison.Ordinal); // switched off: kept, but not active
        Assert.Contains("require [\"fileinto\", \"imap4flags\", \"body\", \"copy\"]; # Neruna-Regeln", script, StringComparison.Ordinal);

        // Written again: replaced, not added; without rules only the user's filter and the reply are left.
        Assert.Equal(script, SieveRules.Write(script, rules, "INBOX.Trash", hasCopy: true));
        var none = SieveRules.Write(script, [], null, hasCopy: true);
        Assert.Empty(SieveRules.Read(none));
        Assert.DoesNotContain("Neruna-Regeln\r\nif", none, StringComparison.Ordinal);
        Assert.True(SieveVacation.Read(none).IsEnabled);
        Assert.True(SieveRules.HasOthers(none));
        Assert.False(SieveRules.HasOthers(SieveRules.Write(null, rules, "INBOX.Trash", hasCopy: true)));
    }

    [Fact]
    public void Without_copy_a_forward_keeps_the_mail_as_well()
    {
        var script = SieveRules.Write(null, [new ServerRule("r", "Weiter", true, [new RuleCondition(RuleField.From, "a@b.ch")], ForwardTo: "x@y.ch; z@y.ch")], null, hasCopy: false);
        Assert.Contains("  redirect \"x@y.ch\";\r\n  redirect \"z@y.ch\";\r\n  keep;", script, StringComparison.Ordinal);
        Assert.DoesNotContain("copy", script, StringComparison.Ordinal);
    }

    private static readonly string? Host = Environment.GetEnvironmentVariable("NERUNA_TEST_SIEVE_HOST");

    [Fact]
    public async Task ManageSieve_server_accepts_the_rules_and_keeps_the_users_filters()
    {
        Assert.SkipWhen(Host is null, "NERUNA_TEST_SIEVE_HOST not set");
        var ct = TestContext.Current.CancellationToken;
        await using var env = await TestEnvironment.CreateAsync();
        var credentials = env.Get<Neruna.Core.Security.ICredentialStore>();
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

        var initial = await provider.GetRulesAsync(ct);
        Assert.Empty(initial.Rules);
        Assert.Single(initial.Others); // the webmail's filter
        Assert.True(initial.Features.HasFlag(ServerRuleFeatures.Flag | ServerRuleFeatures.Forward | ServerRuleFeatures.Stop));

        // The server checks the syntax when the script is stored.
        await provider.SetAutoReplyAsync(new AutoReply(true, "Weg."), [user], ct);
        await provider.SaveRulesAsync([Invoices, new ServerRule(null, "Chef", true, [new RuleCondition(RuleField.Recipient, "chef@firma.ch")], ForwardTo: "anna@privat.ch")], ct);
        var stored = await provider.GetRulesAsync(ct);
        Assert.Equal(["Rechnungen", "Chef"], stored.Rules.Select(r => r.Name));
        Assert.All(stored.Rules, r => Assert.NotNull(r.Id));
        Assert.Equal(Invoices.Conditions, stored.Rules[0].Conditions);
        Assert.True((await provider.GetAutoReplyAsync(ct)).Reply.IsEnabled); // the reply stays

        // One removed, the other switched off.
        await provider.SaveRulesAsync([stored.Rules[1] with { IsEnabled = false }], ct);
        var after = Assert.Single((await provider.GetRulesAsync(ct)).Rules);
        Assert.Equal(("Chef", false), (after.Name, after.IsEnabled));
        await using (var sieve = await ManageSieveClient.ConnectAsync(Host!, ImapMailProvider.SievePort, allowPlainText: true, ct))
        {
            await sieve.AuthenticateAsync(user, "pass", ct);
            Assert.Contains("fileinto \"Newsletter\"", await sieve.GetScriptAsync("webmail", ct), StringComparison.Ordinal);
        }
    }

    // Records with lists compare by reference: here by what they say.
    private sealed class RuleComparer : IEqualityComparer<ServerRule>
    {
        public bool Equals(ServerRule? x, ServerRule? y) =>
            x is not null && y is not null && x with { Conditions = [] } == y with { Conditions = [] } && x.Conditions.SequenceEqual(y.Conditions);

        public int GetHashCode(ServerRule obj) => obj.Name.GetHashCode(StringComparison.Ordinal);
    }
}
