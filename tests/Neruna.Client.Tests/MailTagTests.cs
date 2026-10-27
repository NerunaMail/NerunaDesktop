using System.Text.Json;
using Neruna.Core;
using Neruna.Core.Mail;
using Neruna.Providers.Graph;

namespace Neruna.Client.Tests;

public class MailTagTests
{
    [Theory]
    [InlineData("Persönlich & dringend", "personlich_dringend")]
    [InlineData("Grösse", "grosse")]
    [InlineData("Strasse/Fuss", "strasse_fuss")]
    [InlineData("Weiß", "weiss")]
    [InlineData("!!!", "tag")]
    [InlineData("To-do 2026", "to-do_2026")]
    public void The_keyword_is_made_from_the_name_like_neruna_cloud_does(string name, string expected) =>
        Assert.Equal(expected, MailTag.KeywordFor(name, []));

    [Fact]
    public void A_taken_keyword_gets_a_number_and_categories_match_by_name()
    {
        Assert.Equal("offerte_3", MailTag.KeywordFor("Offerte", ["offerte", "OFFERTE_2"]));

        var tag = new MailTag("$label1", "Wichtig", "#ff0000");
        Assert.True(tag.Matches("$Label1"));
        Assert.True(tag.Matches("wichtig")); // a category at Microsoft 365 / Exchange
        Assert.False(tag.Matches("$label2"));
    }

    [Fact]
    public async Task Own_tags_start_as_the_usual_five_and_the_organisation_s_come_first()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        var service = new MailTagService(env.Get<ISettingsStore>());
        var changes = 0;
        service.Changed += (_, _) => changes++;

        Assert.Equal(["$label1", "$label2", "$label3", "$label4", "$label5"], (await service.GetAllAsync(ct)).Select(t => t.Keyword));

        await service.SaveOwnAsync([new MailTag("$label1", "Sehr wichtig", "#ff0000"), new MailTag("privat", "Privat", "#009900")], ct);
        Assert.True(await service.SetCloudAsync("cloud:a", [new MailTag("offerte", "Offerte", "#e8590c"), new MailTag("privat", "Privat (Firma)", "#000000")], ct));
        Assert.False(await service.SetCloudAsync("cloud:a", [new MailTag("offerte", "Offerte", "#e8590c"), new MailTag("privat", "Privat (Firma)", "#000000")], ct));

        var all = await service.GetAllAsync(ct);
        Assert.Equal(["offerte", "privat", "$label1"], all.Select(t => t.Keyword));
        Assert.Equal(["cloud:a", "cloud:a", null], all.Select(t => t.Source));
        Assert.Equal("Privat (Firma)", all[1].Name); // the organisation's wins over an own one with the same keyword

        Assert.True(await service.SetCloudAsync("cloud:a", [], ct));
        Assert.Equal(["$label1", "privat"], (await service.GetAllAsync(ct)).Select(t => t.Keyword));
        Assert.Empty(await service.GetCloudSourcesAsync(ct));
        Assert.Equal(3, changes);
    }

    [Fact]
    public async Task Keywords_are_stored_updated_by_sync_and_changed_locally()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        var store = env.MailStore;
        var connectionId = Guid.NewGuid();
        await env.Accounts.SaveAccountAsync(new Neruna.Core.Accounts.Account(Guid.NewGuid(), "Anna", "anna@example.com",
            [new Neruna.Core.Accounts.ServiceConnection(connectionId, Neruna.Core.Accounts.ServiceKind.Mail, "imap", new Dictionary<string, string>())]), ct);
        var inbox = (await store.MergeFoldersAsync(connectionId, [new MailFolder(connectionId, "INBOX", "INBOX", null, FolderRole.Inbox)], ct))[0];
        MessageSummary Message(string id, IReadOnlyList<string>? keywords) =>
            new(id, null, null, "Betreff " + id, null, [], DateTimeOffset.UtcNow, MessageFlags.None, 1, false, null, Keywords: keywords);

        await store.ApplySyncResultAsync(inbox, new FolderSyncResult("1", true, [Message("1", ["$label1", "Offerte mit Leerzeichen"]), Message("2", null)],
            new Dictionary<string, MessageFlags>(), [], 2, 2), ct);
        var stored = await store.GetMessagesAsync(connectionId, "INBOX", 0, 10, ct);
        Assert.Equal(["$label1", "Offerte mit Leerzeichen"], stored.Single(m => m.RemoteId == "1").Keywords);
        Assert.Null(stored.Single(m => m.RemoteId == "2").Keywords);

        // A sync brings the server's keywords of known messages; a provider without them leaves them alone.
        await store.ApplySyncResultAsync(inbox with { SyncState = "1" }, new FolderSyncResult("2", false, [], new Dictionary<string, MessageFlags> { ["2"] = MessageFlags.Seen },
            [], 2, 1, new Dictionary<string, IReadOnlyList<string>> { ["1"] = [] }), ct);
        await store.UpdateKeywordsAsync(connectionId, "INBOX", ["2"], "offerte", add: true, ct);
        await store.UpdateKeywordsAsync(connectionId, "INBOX", ["2"], "offerte", add: true, ct);
        stored = await store.GetMessagesAsync(connectionId, "INBOX", 0, 10, ct);
        Assert.Null(stored.Single(m => m.RemoteId == "1").Keywords);
        Assert.Equal(["offerte"], stored.Single(m => m.RemoteId == "2").Keywords);

        await store.UpdateKeywordsAsync(connectionId, "INBOX", ["2"], "OFFERTE", add: false, ct);
        Assert.Null((await store.GetMessagesAsync(connectionId, "INBOX", 0, 10, ct)).Single(m => m.RemoteId == "2").Keywords);
    }

    [Fact]
    public void Graph_reads_categories_and_tells_a_missing_field_from_none()
    {
        using var withCategories = JsonDocument.Parse("""{"id":"A","subject":"x","categories":["Offerte","Rot"]}""");
        using var without = JsonDocument.Parse("""{"id":"B","subject":"y"}""");

        Assert.Equal(["Offerte", "Rot"], GraphMailProvider.SummaryOf(withCategories.RootElement).Keywords);
        Assert.Null(GraphMailProvider.CategoriesOf(without.RootElement));
    }
}
