using MimeKit;
using Neruna.Core.Accounts;
using Neruna.Core.Mail;

namespace Neruna.Client.Tests;

/// <summary>Safety copies of drafts on this device, for when "Entwürfe" cannot be reached.</summary>
public class LocalDraftTests
{
    private static readonly MailboxAddress Anna = new("Anna", "anna@example.com");

    private static MimeMessage Draft(string text, string? messageId = null) =>
        MessageComposer.Build(Anna, new ComposeDraft("lea@example.com", string.Empty, "Offerte", string.Empty, null, [], [], $"<div>{text}</div>") { MessageId = messageId }, [], forDraft: true);

    [Fact]
    public async Task A_copy_left_behind_goes_into_the_drafts_folder_with_the_next_sync_and_is_removed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var env = await TestEnvironment.CreateAsync();
        var connection = await env.AddAccountAsync(ServiceKind.Mail, FakeMailProviderFactory.Id);
        var local = env.Mail.LocalDrafts!;

        // The form saved its copy, the server could not be reached, the form was closed.
        var key = local.Begin(connection.Id);
        await local.SaveAsync(key, Draft("Nur lokal"), ct);
        local.End(key);

        await env.Mail.SyncAllAsync(ct);

        var stored = Assert.Single(env.MailServer.Folders["Drafts"]);
        Assert.Contains("Nur lokal", env.MailServer.Contents[stored.RemoteId].HtmlBody, StringComparison.Ordinal);
        Assert.Empty(local.Waiting(connection.Id));
        Assert.Empty(System.IO.Directory.GetFiles(Path.Combine(env.Directory, "drafts")));
    }

    [Fact]
    public async Task The_copy_of_an_open_form_is_left_alone()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var env = await TestEnvironment.CreateAsync();
        var connection = await env.AddAccountAsync(ServiceKind.Mail, FakeMailProviderFactory.Id);
        var local = env.Mail.LocalDrafts!;

        var key = local.Begin(connection.Id);
        await local.SaveAsync(key, Draft("Noch offen"), ct);

        await env.Mail.SyncAllAsync(ct);

        Assert.False(env.MailServer.Folders.TryGetValue("Drafts", out var drafts) && drafts.Count > 0);
        Assert.Single(System.IO.Directory.GetFiles(Path.Combine(env.Directory, "drafts")));
    }

    [Fact]
    public async Task An_earlier_version_in_the_drafts_folder_is_replaced_not_doubled()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var env = await TestEnvironment.CreateAsync();
        var connection = await env.AddAccountAsync(ServiceKind.Mail, FakeMailProviderFactory.Id);
        await env.Mail.SyncAllAsync(ct);
        var local = env.Mail.LocalDrafts!;

        var first = await env.Mail.SaveDraftAsync(connection, Draft("Erster Stand"), null, ct);
        var messageId = env.MailServer.Contents[first!].MessageId;

        var key = local.Begin(connection.Id);
        await local.SaveAsync(key, Draft("Zweiter Stand", messageId), ct);
        local.End(key);

        Assert.Equal(1, await env.Mail.StoreWaitingDraftsAsync(connection, ct));

        var stored = Assert.Single(env.MailServer.Folders["Drafts"]);
        Assert.Contains("Zweiter Stand", env.MailServer.Contents[stored.RemoteId].HtmlBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_waiting_copy_shows_in_the_drafts_folder_can_be_opened_and_deleted()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var env = await TestEnvironment.CreateAsync();
        var connection = await env.AddAccountAsync(ServiceKind.Mail, FakeMailProviderFactory.Id);
        await env.Mail.SyncAllAsync(ct);
        await env.Mail.SaveDraftAsync(connection, Draft("Auf dem Server"), null, ct);
        var folder = Assert.Single(await env.Mail.GetFoldersAsync(connection.Id, ct), f => f.Role == FolderRole.Drafts);
        var local = env.Mail.LocalDrafts!;

        var key = local.Begin(connection.Id);
        await local.SaveAsync(key, Draft("Nur lokal"), ct);
        Assert.Single(await env.Mail.GetMessagesAsync(folder, cancellationToken: ct)); // open in a form: not listed
        local.End(key);

        var listed = await env.Mail.GetMessagesAsync(folder, cancellationToken: ct);
        Assert.Equal(2, listed.Count);
        var waiting = listed[0];
        Assert.True(LocalDraftStore.IsLocal(waiting.RemoteId));
        Assert.Equal("Offerte", waiting.Subject);
        Assert.Equal("lea@example.com", Assert.Single(waiting.To).Address);

        var opened = MessageComposer.FromDraft(await env.Mail.GetMessageAsync(connection, folder, waiting.RemoteId, ct), waiting.RemoteId);
        Assert.Contains("Nur lokal", opened.HtmlBody, StringComparison.Ordinal);

        // Flags leave it alone; deleting removes the copy and nothing on the server.
        await env.Mail.SetFlagsAsync(connection, folder, [waiting.RemoteId], MessageFlags.Flagged, true, ct);
        await env.Mail.DeleteAsync(connection, folder, [waiting.RemoteId], ct);
        Assert.Single(await env.Mail.GetMessagesAsync(folder, cancellationToken: ct));
        Assert.Single(env.MailServer.Folders["Drafts"]);
        Assert.Empty(System.IO.Directory.GetFiles(Path.Combine(env.Directory, "drafts")));
    }

    [Fact]
    public void Only_keys_of_the_store_count_as_local_ids()
    {
        Assert.False(LocalDraftStore.IsLocal("42"));
        Assert.False(LocalDraftStore.IsLocal("neruna-local-draft:../../etc/passwd"));
        Assert.True(LocalDraftStore.IsLocal(LocalDraftStore.RemoteIdOf(new LocalDraftStore(Path.GetTempPath()).Begin(Guid.NewGuid()))));
    }

    [Fact]
    public async Task Copies_of_a_removed_account_go_with_it()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var env = await TestEnvironment.CreateAsync();
        var local = env.Mail.LocalDrafts!;
        var key = local.Begin(Guid.NewGuid());
        await local.SaveAsync(key, Draft("Konto weg"), ct);
        local.End(key);

        await env.Mail.SyncAllAsync(ct);

        Assert.Empty(System.IO.Directory.GetFiles(Path.Combine(env.Directory, "drafts")));
    }
}
