using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Neruna.Contracts;
using Neruna.Contracts.Cloud;
using Neruna.Core;
using Neruna.Core.Accounts;
using Neruna.Core.Cloud;
using Neruna.Core.Diagnostics;
using Neruna.Core.Security;

namespace Neruna.Client.Tests;

/// <summary>Crash reports: grouped by fingerprint, cleaned before sending, sent only as the user decided.</summary>
public class CrashReportTests
{
    private static InvalidOperationException Thrown(string message)
    {
        try
        {
            Fail(message);
            return null!;
        }
        catch (InvalidOperationException ex)
        {
            return ex;
        }
    }

    private static void Fail(string message) => throw new InvalidOperationException(message);

    [Fact]
    public void The_same_error_has_the_same_fingerprint_whatever_the_message()
    {
        var first = CrashReportBuilder.Fingerprint(Thrown("Konto anna@example.com"));
        Assert.Equal(64, first.Length);
        Assert.Equal(first, CrashReportBuilder.Fingerprint(Thrown("Konto beat@example.org")));
        Assert.NotEqual(first, CrashReportBuilder.Fingerprint(new ArgumentException("x")));
    }

    [Fact]
    public void Addresses_names_hosts_paths_and_tokens_are_removed()
    {
        var account = new Account(Guid.NewGuid(), "Anna Muster", "anna@example.com",
            [new ServiceConnection(Guid.NewGuid(), ServiceKind.Mail, "imap", new Dictionary<string, string> { ["imap.host"] = "mail.firma.ch", ["imap.user"] = "amuster" })],
            Label: "Firma");
        var words = CrashReportBuilder.SensitiveWords([account], new CloudConnection(new Uri("https://neruna.cloud/"), "d", "Example AG", "Anna Muster", DateTimeOffset.Now)).ToList();
        var text = """
            Login failed for anna@example.com at imaps://mail.firma.ch:993 (Anna Muster, Firma, amuster)
            at C:\Users\amuster\AppData\Local\Neruna\neruna.db and /home/amuster/.local/share/neruna
            Bearer eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJhbm5hIn0.c2lnbmF0dXJl from 192.168.1.20 at Example AG
            """;

        var clean = CrashReportBuilder.Scrub(text, words.Where(w => w.Trim().Length >= 3).OrderByDescending(w => w.Length));

        foreach (var secret in new[] { "anna@example.com", "mail.firma.ch", "Anna Muster", "Firma", "amuster", "eyJhbGci", "192.168.1.20", "Example AG" })
        {
            Assert.DoesNotContain(secret, clean, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("imaps://<host>", clean, StringComparison.Ordinal);
        Assert.Contains(@"C:\Users\<user>\AppData", clean, StringComparison.Ordinal);
        Assert.Contains("/home/<user>/.local", clean, StringComparison.Ordinal);
        Assert.Contains("Login failed for <email>", clean, StringComparison.Ordinal);
    }

    [Fact]
    public void The_store_keeps_twenty_and_each_error_is_sent_once_a_day()
    {
        var directory = Path.Combine(Path.GetTempPath(), "neruna-crash-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new CrashReportStore(directory);
            for (var i = 0; i < 25; i++)
            {
                store.Save(CrashReportBuilder.Capture(Thrown("x" + i), fatal: true, "0.1.11", null, DateTimeOffset.Now.AddSeconds(i)));
            }

            var pending = store.Pending();
            Assert.Equal(20, pending.Count);
            Assert.Equal("x24", pending[^1].Record.Message);

            var today = DateOnly.FromDateTime(DateTime.Now);
            Assert.False(store.WasSentToday(pending[0].Record, today));
            store.MarkSent(pending[0].Record, today);
            Assert.True(store.WasSentToday(pending[1].Record, today)); // same error, same version
            Assert.False(store.WasSentToday(pending[0].Record with { AppVersion = "0.1.12" }, today));
            Assert.False(store.WasSentToday(pending[0].Record, today.AddDays(1)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Reports_are_sent_cleaned_anonymously_without_a_cloud_and_removed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var env = await TestEnvironment.CreateAsync();
        await env.Accounts.SaveAccountAsync(new Account(Guid.NewGuid(), "Anna Muster", "anna@example.com", []), ct);
        var settings = env.Get<ISettingsStore>();
        var cloud = new CloudController(new HttpClient(env.Http), settings, env.Get<ICredentialStore>(), NullLogger<CloudController>.Instance);
        var store = new CrashReportStore(Path.Combine(env.Directory, "crashes"));
        var service = new CrashReportService(store, settings, env.Accounts, cloud, NullLogger<CrashReportService>.Instance);
        const string url = "https://neruna.cloud/api/v1/crash-reports";

        Assert.Equal(CrashReportMode.Ask, await service.GetModeAsync(ct));
        await service.SetModeAsync(CrashReportMode.Always, ct);
        Assert.Equal(CrashReportMode.Always, await service.GetModeAsync(ct));

        store.Save(CrashReportBuilder.Capture(Thrown("Keine Verbindung für anna@example.com"), fatal: true, "0.1.11", "INF Sync Anna Muster", DateTimeOffset.Now));
        var pending = Assert.Single(await service.GetPendingAsync(ct));
        Assert.DoesNotContain("anna@example.com", pending.Report.Message, StringComparison.Ordinal);
        Assert.Equal("INF Sync <name>", pending.Report.Log);

        // Offline: kept for later.
        Assert.Equal(0, await service.SendAsync([pending], ct));
        Assert.Single(store.Pending());

        env.Http.Responses[url] = (HttpStatusCode.Accepted, """{"id":"01K"}""");
        Assert.Equal(1, await service.SendAsync([pending], ct));
        Assert.Empty(store.Pending());
        var body = JsonSerializer.Deserialize<CrashReportRequest>(env.Http.Sent.Last(s => s.Url == url).Body, NerunaJson.Options)!;
        Assert.Equal(pending.Report.Fingerprint, body.Fingerprint);
        Assert.Equal("Keine Verbindung für <email>", body.Message);
        Assert.True(body.Fatal);
    }
}
