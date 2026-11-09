using Neruna.Core.Accounts;
using Neruna.Core.Mail;

namespace Neruna.Client.Tests;

/// <summary>"Kopieren nach …": the message is stored in the target as well, the original stays.</summary>
public class MailCopyTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_copy_lands_in_the_target_with_its_flags_and_the_original_stays()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var env = await TestEnvironment.CreateAsync();
        env.MailServer.Folders["Projekte"] = [];
        env.MailServer.ExtraCapabilities = MailProviderCapabilities.Delete; // a move would remove the original here
        env.MailServer.Deliver("INBOX", "Offerte", Now);
        env.MailServer.Deliver("INBOX", "bleibt nur hier", Now);
        var connection = await env.AddAccountAsync(ServiceKind.Mail, FakeMailProviderFactory.Id);
        await env.Mail.SyncAllAsync(ct);
        var folders = await env.Mail.GetFoldersAsync(connection.Id, ct);
        var inbox = folders.Single(f => f.Role == FolderRole.Inbox);
        var projects = folders.Single(f => f.RemoteId == "Projekte");
        var offer = (await env.Mail.GetMessagesAsync(inbox, cancellationToken: ct)).Single(m => m.Subject == "Offerte");
        await env.Mail.SetFlagsAsync(connection, inbox, [offer.RemoteId], MessageFlags.Flagged, add: true, ct);
        offer = (await env.Mail.GetMessagesAsync(inbox, cancellationToken: ct)).Single(m => m.Subject == "Offerte");

        Assert.Equal(1, await env.Mail.CopyAsync(connection, inbox, [offer], connection, projects, ct));

        Assert.Equal(2, env.MailServer.Folders["INBOX"].Count);
        var copy = Assert.Single(env.MailServer.Folders["Projekte"]);
        Assert.Equal("Offerte", copy.Subject);
        Assert.True(copy.Flags.HasFlag(MessageFlags.Flagged));
        Assert.Single(await env.Mail.GetMessagesAsync(projects, cancellationToken: ct)); // the local store knows it too
    }
}
