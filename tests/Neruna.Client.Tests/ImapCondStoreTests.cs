using MailKit;
using MailKit.Net.Imap;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using Neruna.Contracts.Discovery;
using Neruna.Core.Mail;
using Neruna.Providers.Imap;

namespace Neruna.Client.Tests;

/// <summary>
/// A large folder synced every few minutes: with CONDSTORE nothing is fetched while nothing changed, and only the
/// changed flags afterwards. Against Dovecot (GreenMail has no CONDSTORE):
///   NERUNA_TEST_SIEVE_HOST=127.0.0.1 dotnet test
/// </summary>
public class ImapCondStoreTests
{
    private static readonly string? Host = Environment.GetEnvironmentVariable("NERUNA_TEST_SIEVE_HOST");
    private const int Port = 3144;

    [Fact]
    public async Task An_unchanged_folder_costs_nothing_and_only_changed_flags_come()
    {
        Assert.SkipWhen(Host is null, "NERUNA_TEST_SIEVE_HOST not set");
        var ct = TestContext.Current.CancellationToken;
        var user = $"anna-{Guid.NewGuid():N}@example.com";
        using (var other = new ImapClient())
        {
            await other.ConnectAsync(Host!, Port, MailKit.Security.SecureSocketOptions.None, ct);
            await other.AuthenticateAsync(user, "pass", ct);
            for (var i = 0; i < 5; i++)
            {
                await other.Inbox.AppendAsync(new AppendRequest(new MimeMessage([new MailboxAddress("Lea", "lea@example.com")], [new MailboxAddress("Anna", user)], $"Mail {i}", new TextPart("plain") { Text = "Hallo" })), ct);
            }

            await other.DisconnectAsync(true, ct);
        }

        await using var env = await TestEnvironment.CreateAsync();
        var connectionId = Guid.NewGuid();
        await env.Get<Neruna.Core.Security.ICredentialStore>().SetSecretAsync(connectionId, "pass", ct);
        var settings = new ImapSettings(Host!, Port, SocketSecurity.None, Host!, 3025, SocketSecurity.None, user);
        await using var provider = new ImapMailProvider(connectionId, settings, env.Get<Neruna.Core.Security.ICredentialStore>(), NullLogger<ImapMailProvider>.Instance);
        var inbox = (await provider.GetFoldersAsync(ct)).Single(f => f.Role == FolderRole.Inbox);

        var first = await provider.SyncFolderAsync(inbox, [], ct);
        Assert.Equal(5, first.AddedOrChanged.Count);
        var state = ImapMailProvider.SyncState.Parse(first.NewSyncState);
        Assert.NotNull(state?.ModSeq);
        Assert.Equal(5, state!.Value.Exists);
        var known = first.AddedOrChanged.Select(m => m.RemoteId).ToList();
        var synced = inbox with { SyncState = first.NewSyncState, TotalCount = first.TotalCount, UnreadCount = first.UnreadCount };

        // Nothing changed: no message, no flag.
        var again = await provider.SyncFolderAsync(synced, known, ct);
        Assert.Equal((0, 0, 0, first.NewSyncState), (again.AddedOrChanged.Count, again.FlagUpdates.Count, again.RemovedRemoteIds.Count, again.NewSyncState));
        Assert.Equal(5, again.TotalCount);

        // Another program reads one mail: only that one's flags come.
        using (var other = new ImapClient())
        {
            await other.ConnectAsync(Host!, Port, MailKit.Security.SecureSocketOptions.None, ct);
            await other.AuthenticateAsync(user, "pass", ct);
            await other.Inbox.OpenAsync(FolderAccess.ReadWrite, ct);
            await other.Inbox.AddFlagsAsync(new UniqueId(uint.Parse(known[2], System.Globalization.CultureInfo.InvariantCulture)), MailKit.MessageFlags.Seen, true, ct);
            await other.DisconnectAsync(true, ct);
        }

        var changed = await provider.SyncFolderAsync(synced, known, ct);
        var update = Assert.Single(changed.FlagUpdates);
        Assert.Equal(known[2], update.Key);
        Assert.True(update.Value.HasFlag(Neruna.Core.Mail.MessageFlags.Seen));
        Assert.Equal(4, changed.UnreadCount);
    }
}
