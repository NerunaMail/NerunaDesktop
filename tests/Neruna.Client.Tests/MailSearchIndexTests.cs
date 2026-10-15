using Microsoft.Extensions.Logging.Abstractions;
using Neruna.Core;
using Neruna.Core.Accounts;
using Neruna.Core.Mail;
using Neruna.Core.Providers;

namespace Neruna.Client.Tests;

/// <summary>The local search index (SQLite FTS5): built in the background, asked at once, per account switchable.</summary>
public class MailSearchIndexTests
{
    [Fact]
    public async Task Messages_are_found_by_word_starts_their_text_once_it_is_here_and_leave_with_the_message()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var env = await TestEnvironment.CreateAsync();
        var connection = await env.AddAccountAsync(ServiceKind.Mail, FakeMailProviderFactory.Id);
        env.MailServer.Deliver("INBOX", "Offerte Netzwerk-Erneuerung", DateTimeOffset.UtcNow.AddHours(-3));
        var trip = env.MailServer.Deliver("INBOX", "Teamausflug auf die Rigi", DateTimeOffset.UtcNow.AddHours(-2));
        env.MailServer.Deliver("INBOX", "Prüfbericht Brücke", DateTimeOffset.UtcNow.AddHours(-1));
        var account = (await env.Accounts.GetAccountsAsync(ct)).Single();
        await env.Mail.SetOfflinePeriodAsync(account.Id, OfflinePeriod.OpenedOnly, ct); // nothing fetched ahead
        await env.Mail.SyncAllAsync(ct);
        var inbox = (await env.Mail.GetFoldersAsync(connection.Id, ct)).Single(f => f.Role == FolderRole.Inbox || f.RemoteId == "INBOX");
        using var indexer = new MailSearchIndexer(env.Get<IMailSearchIndex>(), env.MailStore, env.Accounts, env.Get<ProviderRegistry>(), env.Get<ISettingsStore>(),
            env.Mail, NullLogger<MailSearchIndexer>.Instance);
        await indexer.RunOnceAsync(ct);

        async Task<string[]> Find(MailSearchQuery query) =>
            [.. (await indexer.SearchAsync([inbox], query, cancellationToken: ct)).Hits.Select(h => h.Message.Subject)];

        // Beginnings of words, any field; diacritics do not matter; every word must occur.
        Assert.Equal(["Offerte Netzwerk-Erneuerung"], await Find(new MailSearchQuery(Anywhere: "offe netz")));
        Assert.Equal(["Prüfbericht Brücke"], await Find(new MailSearchQuery(Anywhere: "brucke")));
        Assert.Equal(3, (await Find(new MailSearchQuery(From: "bob@example"))).Length);
        Assert.Empty(await Find(new MailSearchQuery(Subject: "Offerte", From: "lea")));
        Assert.Empty(await Find(new MailSearchQuery(Anywhere: "\" OR *")));

        // The text only once it is on this device (opened): then the indexer adds it.
        Assert.Empty(await Find(new MailSearchQuery(Text: "Hallo")));
        await env.Mail.GetMessageAsync(connection, inbox, trip.RemoteId, ct);
        await indexer.RunOnceAsync(ct);
        Assert.Equal(["Teamausflug auf die Rigi"], await Find(new MailSearchQuery(Text: "hallo")));
        Assert.Equal(new SearchIndexStatus(3, 3, 1), (await indexer.GetStatusAsync(ct)).Single().Status);

        // Gone from the folder, gone from the index.
        await env.MailStore.RemoveMessagesAsync(connection.Id, inbox.RemoteId, [trip.RemoteId], ct);
        Assert.Empty(await Find(new MailSearchQuery(Text: "hallo")));

        // Switched off: the index of the account goes; searching still works on the stored list.
        await indexer.SetEnabledAsync(account, false, ct);
        Assert.Equal(new SearchIndexStatus(2, 0, 0), (await indexer.GetStatusAsync(ct)).Single().Status);
        await indexer.RunOnceAsync(ct);
        Assert.Equal(0, (await indexer.GetStatusAsync(ct)).Single().Status.Indexed);
        Assert.Equal(["Offerte Netzwerk-Erneuerung"], await Find(new MailSearchQuery(Subject: "Offerte")));
    }

    [Fact]
    public void Queries_become_word_beginnings_in_their_fields()
    {
        Assert.Equal("{Sender} : (\"lea\"*) AND {Subject} : (\"Offerte\"* AND \"Q4\"*)", MailSearchIndexer.MatchFor(new MailSearchQuery(From: "lea", Subject: "Offerte, Q4")));
        Assert.Equal("(\"zürich\"*)", MailSearchIndexer.MatchFor(new MailSearchQuery(Anywhere: "\"zürich\"")));
        Assert.Null(MailSearchIndexer.MatchFor(new MailSearchQuery(Anywhere: " – ")));
    }
}
