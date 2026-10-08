using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using Neruna.Contracts.Discovery;
using Neruna.Core;
using Neruna.Core.Accounts;
using Neruna.Core.Mail;
using Neruna.Core.Providers;
using Neruna.Core.Security;
using Neruna.Providers.Imap;

namespace Neruna.Client.Tests;

public class MailSearchTests
{
    private static readonly string? Host = Environment.GetEnvironmentVariable("NERUNA_TEST_IMAP_HOST");

    [Fact]
    public void Local_search_matches_sender_subject_preview_and_any_field()
    {
        var message = new MessageSummary("1", null, null, "Offerte Netzwerk", new MailAddress("Marco Bernasconi", "marco@bernasconi.example"),
            [new MailAddress("Anna", "anna@example.com")], DateTimeOffset.UtcNow, MessageFlags.None, 1, false, "Anbei die Offerte für die Switches");

        Assert.True(new MailSearchQuery(From: "bernasconi").Matches(message));
        Assert.True(new MailSearchQuery(Subject: "netzwerk", Text: "SWITCHES").Matches(message));
        Assert.False(new MailSearchQuery(Subject: "netzwerk", Text: "Router").Matches(message));
        Assert.True(new MailSearchQuery(Anywhere: "anna@example").Matches(message));
        Assert.True(new MailSearchQuery().IsEmpty);
    }

    [Fact]
    public async Task Older_messages_load_on_demand_and_search_finds_them_on_the_server()
    {
        Assert.SkipWhen(Host is null, "NERUNA_TEST_IMAP_HOST not set");
        var ct = TestContext.Current.CancellationToken;
        await using var env = await TestEnvironment.CreateAsync();
        var credentials = env.Get<ICredentialStore>();

        // The first sync takes only the newest 3 messages.
        var registry = new ProviderRegistry([new ImapProviderFactory(credentials, NullLoggerFactory.Instance, maxInitialMessages: 3)], [], []);
        var mail = new MailController(env.Accounts, env.MailStore, registry, NullLogger<MailController>.Instance);
        var address = "marco@example.com";
        var settings = new ImapSettings(Host!, 3143, SocketSecurity.None, Host!, 3025, SocketSecurity.None, address);
        var connection = new ServiceConnection(Guid.NewGuid(), ServiceKind.Mail, ProviderIds.Imap, settings.ToDictionary());
        await credentials.SetSecretAsync(connection.Id, "geheim", ct);
        await env.Accounts.SaveAccountAsync(new Account(Guid.NewGuid(), "Marco", address, [connection]), ct);

        // A folder of its own with 8 messages; one has an umlaut in subject and body, one comes from Lea.
        var folderName = "Suche-" + Guid.NewGuid().ToString("N")[..6];
        using (var imap = new MailKit.Net.Imap.ImapClient())
        {
            await imap.ConnectAsync(Host!, 3143, MailKit.Security.SecureSocketOptions.None, ct);
            await imap.AuthenticateAsync(address, "geheim", ct);
            var folder = await imap.GetFolder(imap.PersonalNamespaces[0]).CreateAsync(folderName, true, ct);
            for (var i = 1; i <= 8; i++)
            {
                var message = new MimeMessage
                {
                    Subject = i == 2 ? "Prüfbericht Brücke" : $"Nachricht {i}",
                    Date = DateTimeOffset.UtcNow.AddDays(-10 + i),
                    Body = new TextPart("plain") { Text = i == 2 ? "Die Brücke über die Reuss ist geprüft." : "Inhalt " + i },
                };
                message.From.Add(i == 5 ? new MailboxAddress("Lea Keller", "lea@example.com") : new MailboxAddress("Anna", "anna@example.com"));
                message.To.Add(new MailboxAddress("Marco", address));
                await folder!.AppendAsync(new MailKit.AppendRequest(message, MailKit.MessageFlags.Seen) { InternalDate = message.Date }, ct);
            }

            await imap.DisconnectAsync(true, ct);
        }

        await mail.SyncAllAsync(ct);
        var stored = (await mail.GetFoldersAsync(connection.Id, ct)).Single(f => f.Name == folderName);
        Assert.Equal(8, stored.TotalCount);
        Assert.Equal(3, await mail.CountStoredAsync(stored, ct));

        // Older ones come in portions and keep the folder's sync state.
        Assert.Equal(4, await mail.LoadOlderAsync(connection, stored, 4, ct));
        Assert.Equal(1, await mail.LoadOlderAsync(connection, stored, 4, ct));
        Assert.Equal(0, await mail.LoadOlderAsync(connection, stored, 4, ct));
        Assert.Equal(8, (await mail.GetMessagesAsync(stored, cancellationToken: ct)).Count);
        await mail.SyncAllAsync(ct);
        Assert.Equal(8, (await mail.GetMessagesAsync(stored, cancellationToken: ct)).Count);

        // The server searches all messages, also by text and with umlauts.
        async Task<IReadOnlyList<string>> FindAsync(MailSearchQuery query) =>
            (await mail.SearchAsync([stored], query, cancellationToken: ct)).Hits.Select(h => h.Message.Subject).ToList();

        Assert.Equal(["Prüfbericht Brücke"], await FindAsync(new MailSearchQuery(Subject: "Brücke")));
        Assert.Equal(["Prüfbericht Brücke"], await FindAsync(new MailSearchQuery(Text: "Reuss")));
        // GreenMail compares FROM with whole addresses only; real servers (Dovecot …) also match parts and names.
        Assert.Equal(["Nachricht 5"], await FindAsync(new MailSearchQuery(From: "lea@example.com")));
        Assert.Equal(["Nachricht 5"], await FindAsync(new MailSearchQuery(From: "lea@example.com", Subject: "Nachricht")));
        Assert.Equal(["Prüfbericht Brücke"], await FindAsync(new MailSearchQuery(Anywhere: "geprüft")));
        Assert.Empty(await FindAsync(new MailSearchQuery(From: "lea", Subject: "Brücke")));
    }
}
