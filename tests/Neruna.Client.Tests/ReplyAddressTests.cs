using MimeKit;
using Neruna.Core;
using Neruna.Core.Accounts;
using Neruna.Core.Mail;

namespace Neruna.Client.Tests;

/// <summary>"Antwortadresse": e.g. sent as info@, answered to the own address.</summary>
public class ReplyAddressTests
{
    [Fact]
    public async Task Mail_from_the_account_gets_its_reply_to_unless_it_has_its_own()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var env = await TestEnvironment.CreateAsync();
        var connection = await env.AddAccountAsync(ServiceKind.Mail, FakeMailProviderFactory.Id);
        var account = (await env.Accounts.GetAccountsAsync(ct)).Single();
        var settings = env.Get<ISettingsStore>();

        static MimeMessage Mail()
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress("Info", "anna@example.com"));
            message.To.Add(new MailboxAddress("Kunde", "kunde@example.org"));
            message.Subject = "Offerte";
            message.Body = new TextPart("plain") { Text = "Hallo" };
            return message;
        }

        // None set: nothing added.
        await env.Mail.SendAsync(connection, Mail(), ct);
        Assert.Empty(env.MailServer.Sent[^1].ReplyTo);

        await ReplyAddress.SetAsync(settings, account.Id, "anna.muster@example.com", ct);
        await env.Mail.SendAsync(connection, Mail(), ct);
        Assert.Equal("anna.muster@example.com", Assert.Single(env.MailServer.Sent[^1].ReplyTo.Mailboxes).Address);

        // A mail with its own Reply-To keeps it.
        var own = Mail();
        own.ReplyTo.Add(new MailboxAddress(null, "projekt@example.com"));
        await env.Mail.SendAsync(connection, own, ct);
        Assert.Equal("projekt@example.com", Assert.Single(env.MailServer.Sent[^1].ReplyTo.Mailboxes).Address);

        // Empty clears the own one.
        await ReplyAddress.SetAsync(settings, account.Id, " ", ct);
        Assert.Equal(((string?)null, false), await ReplyAddress.GetAsync(settings, account, ct));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("anna@example.com", true)]
    [InlineData("Anna <anna@example.com>", false)]
    [InlineData("anna", false)]
    [InlineData("anna@example.com, ben@example.com", false)]
    public void Only_a_single_plain_address_is_taken(string address, bool valid) => Assert.Equal(valid, ReplyAddress.IsValid(address));
}
