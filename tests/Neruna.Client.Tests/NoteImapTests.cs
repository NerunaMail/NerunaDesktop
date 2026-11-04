using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using Microsoft.Extensions.Logging.Abstractions;
using Neruna.Contracts.Discovery;
using Neruna.Core;
using Neruna.Core.Accounts;
using Neruna.Core.Mail;
using Neruna.Core.Notes;
using Neruna.Core.Providers;
using Neruna.Core.Security;
using Neruna.Providers.Imap;

namespace Neruna.Client.Tests;

/// <summary>
/// Notes in a real mailbox (Dovecot of the test lab, with keywords): the phone-notes format, notebooks as
/// subfolders, an edit replacing the message, notes of another device arriving.
///   NERUNA_TEST_SIEVE_HOST=127.0.0.1 dotnet test
/// </summary>
public class NoteImapTests
{
    private static readonly string? Host = Environment.GetEnvironmentVariable("NERUNA_TEST_SIEVE_HOST");
    private const int Port = 3144;

    [Fact]
    public async Task Notes_go_into_the_notes_folder_of_the_mailbox_and_come_back()
    {
        Assert.SkipWhen(Host is null, "NERUNA_TEST_SIEVE_HOST not set");
        var ct = TestContext.Current.CancellationToken;
        var user = $"anna-{Guid.NewGuid():N}@example.com";
        await using var env = await TestEnvironment.CreateAsync();
        var credentials = env.Get<ICredentialStore>();
        var registry = new ProviderRegistry([new ImapProviderFactory(credentials, NullLoggerFactory.Instance)], [], []);
        var mail = new MailController(env.Accounts, env.MailStore, registry, NullLogger<MailController>.Instance);
        var notes = new NoteController(env.Accounts, env.MailStore, mail, registry, env.Get<INoteStore>(), env.Get<ISettingsStore>(), NullLogger<NoteController>.Instance);
        var settings = new ImapSettings(Host!, Port, SocketSecurity.None, Host!, 3025, SocketSecurity.None, user);
        var connection = new ServiceConnection(Guid.NewGuid(), ServiceKind.Mail, ProviderIds.Imap, settings.ToDictionary());
        await credentials.SetSecretAsync(connection.Id, "pass", ct);
        await env.Accounts.SaveAccountAsync(new Account(Guid.NewGuid(), "Anna", user, [connection]), ct);
        await mail.SyncAllAsync(ct);

        Assert.Contains(await notes.GetLocationsAsync(ct), l => l.ConnectionId == connection.Id);
        var note = await notes.SaveAsync(Note.New(connection.Id, "Kunden", "<div>Müller AG</div><div>Offerte</div>") with { Pinned = true, Tags = ["$label1"] }, cancellationToken: ct);
        Assert.Equal(NotePending.None, note.Pending);
        note = await notes.SaveAsync(note with { Html = "<div>Müller AG</div><div>Offerte bestätigt</div>" }, cancellationToken: ct);

        using var imap = new ImapClient();
        await imap.ConnectAsync(Host!, Port, MailKit.Security.SecureSocketOptions.None, ct);
        await imap.AuthenticateAsync(user, "pass", ct);
        var personal = imap.GetFolder(imap.PersonalNamespaces[0]);
        // At the top, or under the inbox on servers with the "INBOX." prefix.
        var all = (await personal.GetSubfoldersAsync(false, ct)).Concat(await imap.Inbox.GetSubfoldersAsync(false, ct));
        var root = all.First(f => f.Name == "Notes");
        var kunden = (await root.GetSubfoldersAsync(false, ct)).Single(f => f.Name == "Kunden");
        await kunden.OpenAsync(FolderAccess.ReadOnly, ct);
        var uids = await kunden.SearchAsync(SearchQuery.All, ct);
        var stored = Assert.Single(uids);
        var message = await kunden.GetMessageAsync(stored, ct);
        Assert.Equal(("Müller AG", NoteMime.TypeValue, note.Id), (message.Subject, message.Headers[NoteMime.TypeHeader], message.Headers[NoteMime.IdHeader]));
        Assert.Contains("bestätigt", message.HtmlBody, StringComparison.Ordinal);
        var summary = Assert.Single(await kunden.FetchAsync(uids, MessageSummaryItems.Flags, ct));
        Assert.True(summary.Flags!.Value.HasFlag(MailKit.MessageFlags.Flagged));
        Assert.Contains("$label1", summary.Keywords);
        await kunden.CloseAsync(false, ct);

        // A note written on the phone arrives with the next sync.
        await root.OpenAsync(FolderAccess.ReadWrite, ct);
        var phone = Note.New(connection.Id, string.Empty, "<div>Vom Handy</div>");
        await root.AppendAsync(new AppendRequest(NoteMime.ToMime(phone, user), MailKit.MessageFlags.Seen), ct);
        await root.CloseAsync(false, ct);
        await notes.SyncAsync(ct);
        var titles = (await notes.GetNotesAsync(connection.Id, cancellationToken: ct)).Select(n => n.Title).Order().ToList();
        Assert.Equal(["Müller AG", "Vom Handy"], titles);

        // Deleted here: gone from the notes folder.
        await notes.DeleteAsync(note, ct);
        await kunden.OpenAsync(FolderAccess.ReadOnly, ct);
        Assert.Empty(await kunden.SearchAsync(SearchQuery.NotDeleted, ct));
        Assert.Equal(["Vom Handy"], (await notes.GetNotesAsync(connection.Id, cancellationToken: ct)).Select(n => n.Title));
    }
}
