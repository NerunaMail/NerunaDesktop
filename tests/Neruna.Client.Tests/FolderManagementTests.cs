using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using Neruna.Contracts.Discovery;
using Neruna.Core.Accounts;
using Neruna.Core.Mail;
using Neruna.Core.Providers;
using Neruna.Core.Security;
using Neruna.Providers.Imap;

namespace Neruna.Client.Tests;

/// <summary>Folders created, renamed and deleted from the folder tree; the IMAP part against the test lab.</summary>
[Collection(Testlab.Name)]
public class FolderManagementTests
{
    private static readonly string? Host = Environment.GetEnvironmentVariable("NERUNA_TEST_IMAP_HOST");

    [Theory]
    [InlineData("Projekte 2026", null)]
    [InlineData("Projekte.2026", "«.»")]
    [InlineData("Kunden/Müller", "«/»")]
    [InlineData("Alles*", "«*»")]
    [InlineData(" Rechnungen", "Leerzeichen")]
    [InlineData("", "Namen")]
    [InlineData("archiv", "gibt es hier schon")]
    public void ImapFolderNames(string name, string? problem)
    {
        var found = FolderNames.Problem(name, new ImapMailProvider(Guid.Empty, null!, null!, NullLogger<ImapMailProvider>.Instance).ForbiddenFolderNameCharacters, ["Archiv", "Kunden"]);
        if (problem is null)
        {
            Assert.Null(found);
        }
        else
        {
            Assert.Contains(problem, found, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Create_rename_and_delete_folders_with_their_mail_going_to_the_trash()
    {
        Assert.SkipWhen(Host is null, "NERUNA_TEST_IMAP_HOST not set");
        var ct = TestContext.Current.CancellationToken;
        await using var env = await TestEnvironment.CreateAsync();
        var credentials = env.Get<ICredentialStore>();
        var registry = new ProviderRegistry([new ImapProviderFactory(credentials, NullLoggerFactory.Instance)], [], []);
        var controller = new MailController(env.Accounts, env.MailStore, registry, NullLogger<MailController>.Instance);
        var settings = new ImapSettings(Host!, 3143, SocketSecurity.None, Host!, 3025, SocketSecurity.None, "anna@example.com");
        var connection = new ServiceConnection(Guid.NewGuid(), ServiceKind.Mail, ProviderIds.Imap, settings.ToDictionary());
        await credentials.SetSecretAsync(connection.Id, "geheim", ct);
        await env.Accounts.SaveAccountAsync(new Account(Guid.NewGuid(), "Anna", "anna@example.com", [connection]), ct);

        // A trash to move into (servers without SPECIAL-USE: recognised by its name).
        using (var imap = new MailKit.Net.Imap.ImapClient())
        {
            await imap.ConnectAsync(Host!, 3143, MailKit.Security.SecureSocketOptions.None, ct);
            await imap.AuthenticateAsync("anna@example.com", "geheim", ct);
            var personal = imap.GetFolder(imap.PersonalNamespaces[0]);
            if ((await personal.GetSubfoldersAsync(false, ct)).All(f => f.Name != "Trash"))
            {
                await personal.CreateAsync("Trash", true, ct);
            }

            await imap.DisconnectAsync(true, ct);
        }

        await controller.SyncConnectionAsync(connection, ct);
        async Task<IReadOnlyList<MailFolder>> FoldersAsync() => await controller.GetFoldersAsync(connection.Id, ct);

        var name = "Projekt " + Guid.NewGuid().ToString("N")[..6];
        await controller.CreateFolderAsync(connection, name, null, ct);
        var project = Assert.Single(await FoldersAsync(), f => f.Name == name);
        await controller.CreateFolderAsync(connection, "Offerten", project, ct);
        Assert.Single(await FoldersAsync(), f => f.Name == "Offerten" && f.ParentRemoteId == project.RemoteId);

        // Renamed: the subfolder goes along.
        var renamed = name + " alt";
        await controller.RenameFolderAsync(connection, project, renamed, ct);
        Assert.DoesNotContain(await FoldersAsync(), f => f.Name == name);
        project = Assert.Single(await FoldersAsync(), f => f.Name == renamed);
        var offers = Assert.Single(await FoldersAsync(), f => f.Name == "Offerten" && f.ParentRemoteId == project.RemoteId);

        // Two mails in the subfolder (one never loaded here): deleting it moves both to the trash.
        using (var imap = new MailKit.Net.Imap.ImapClient())
        {
            await imap.ConnectAsync(Host!, 3143, MailKit.Security.SecureSocketOptions.None, ct);
            await imap.AuthenticateAsync("anna@example.com", "geheim", ct);
            var folder = await imap.GetFolderAsync(offers.RemoteId, ct);
            foreach (var subject in new[] { "Offerte A " + name, "Offerte B " + name })
            {
                var message = new MimeMessage { Subject = subject, Body = new TextPart("plain") { Text = "x" } };
                message.From.Add(new MailboxAddress("Marco", "marco@example.com"));
                await folder.AppendAsync(new MailKit.AppendRequest(message), ct);
            }

            await imap.DisconnectAsync(true, ct);
        }

        Assert.Equal(2, await controller.CountFolderMessagesAsync(connection, offers, ct));
        await controller.DeleteFolderAsync(connection, offers, ct);
        Assert.DoesNotContain(await FoldersAsync(), f => f.RemoteId == offers.RemoteId);
        var trash = Assert.Single(await FoldersAsync(), f => f.Role == FolderRole.Trash);
        var inTrash = (await controller.GetMessagesAsync(trash, cancellationToken: ct)).Select(m => m.Subject).ToList();
        Assert.Contains("Offerte A " + name, inTrash);
        Assert.Contains("Offerte B " + name, inTrash);

        // The empty parent goes without anything to move.
        Assert.Equal(0, await controller.CountFolderMessagesAsync(connection, project, ct));
        await controller.DeleteFolderAsync(connection, project, ct);
        Assert.DoesNotContain(await FoldersAsync(), f => f.Name == renamed);
    }
}
