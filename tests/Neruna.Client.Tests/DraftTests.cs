using MimeKit;
using Neruna.Core.Accounts;
using Neruna.Core.Mail;

namespace Neruna.Client.Tests;

public class DraftTests
{
    private static readonly MailboxAddress Anna = new("Anna", "anna@example.com");

    [Fact]
    public async Task Saving_again_replaces_the_previous_version_and_creates_the_folder_if_needed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var env = await TestEnvironment.CreateAsync();
        var connection = await env.AddAccountAsync(ServiceKind.Mail, FakeMailProviderFactory.Id);
        await env.Mail.SyncAllAsync(ct);

        // Half-typed recipients must not prevent saving.
        var draft = new ComposeDraft("lea@example.com, marco@", string.Empty, "Offerte", string.Empty, null, [], [], "<div>Erster Stand</div>");
        var first = await env.Mail.SaveDraftAsync(connection, MessageComposer.Build(Anna, draft, [], forDraft: true), null, ct);

        Assert.NotNull(first);
        var folder = Assert.Single(await env.Mail.GetFoldersAsync(connection.Id, ct), f => f.Role == FolderRole.Drafts);
        var stored = Assert.Single(env.MailServer.Folders["Drafts"]);
        Assert.Equal(MessageFlags.Seen | MessageFlags.Draft, stored.Flags);

        var messageId = env.MailServer.Contents[first].MessageId;
        var second = await env.Mail.SaveDraftAsync(connection,
            MessageComposer.Build(Anna, draft with { HtmlBody = "<div>Zweiter Stand</div>", MessageId = messageId }, [], forDraft: true), first, ct);

        Assert.NotNull(second);
        Assert.NotEqual(first, second);
        Assert.Equal(second, Assert.Single(env.MailServer.Folders["Drafts"]).RemoteId);
        Assert.Equal(second, Assert.Single(await env.Mail.GetMessagesAsync(folder, cancellationToken: ct)).RemoteId);

        // Reopened for editing: recipients, text and identity come back.
        var reopened = MessageComposer.FromDraft(await env.Mail.GetMessageAsync(connection, folder, second, ct), second);
        Assert.Equal("lea@example.com", reopened.To);
        Assert.Equal("Offerte", reopened.Subject);
        Assert.Contains("Zweiter Stand", reopened.HtmlBody, StringComparison.Ordinal);
        Assert.Equal(second, reopened.DraftRemoteId);
        Assert.Equal(messageId, reopened.MessageId);

        await env.Mail.DeleteDraftAsync(connection, second, ct);
        Assert.Empty(env.MailServer.Folders["Drafts"]);
        Assert.Empty(await env.Mail.GetMessagesAsync(folder, cancellationToken: ct));
    }

    [Fact]
    public void Reopened_draft_keeps_images_and_attachments()
    {
        var html = "<div>Logo: <img src=\"data:image/png;base64,iVBORw0KGgo=\"></div>";
        var attachment = new MimePart("application", "pdf") { FileName = "offerte.pdf", Content = new MimeContent(new MemoryStream([1, 2, 3])) };
        var draft = new ComposeDraft("lea@example.com", "marco@example.com", "Bild", string.Empty, "<orig@example.com>", ["<orig@example.com>"], [attachment], html);

        var message = MessageComposer.Build(Anna, draft, [], forDraft: true);
        var reopened = MessageComposer.FromDraft(message, "7");

        Assert.Contains("data:image/png;base64,iVBORw0KGgo=", reopened.HtmlBody, StringComparison.Ordinal);
        Assert.Equal("offerte.pdf", MessageContent.FileNameOf(Assert.Single(reopened.Attachments)));
        Assert.Equal("marco@example.com", reopened.Cc);
        Assert.Equal("orig@example.com", reopened.InReplyTo);
    }
}
