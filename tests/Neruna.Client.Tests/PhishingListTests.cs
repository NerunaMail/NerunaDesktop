using System.Net;
using MimeKit;
using Neruna.Core.Privacy;

namespace Neruna.Client.Tests;

/// <summary>Known phishing hosts from neruna.cloud, checked on the device (contract/fixtures/phishing-hosts-example.bin).</summary>
public class PhishingListTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void The_list_of_the_contract_is_read_and_found_with_parents_and_www()
    {
        var list = new PhishingList();
        Assert.True(list.Apply(ContractFixtures.ReadBytes("phishing-hosts-example.bin")));

        Assert.Equal((3, 1760000000L), (list.Count, list.Version));
        Assert.True(list.Contains("phish.example"));
        Assert.True(list.Contains("WWW.Phish.Example."));
        Assert.True(list.Contains("secure.login-bank.example")); // below a listed host
        Assert.False(list.Contains("example")); // not above the registrable domain
        Assert.False(list.Contains("bank.example"));
        Assert.Equal(ContractFixtures.ReadBytes("phishing-hosts-example.bin"), list.ToFull());
    }

    [Fact]
    public void Changes_are_applied_to_their_version_only()
    {
        var list = new PhishingList();
        list.Apply(Build(1, 0, ["a-phish.example", "b-phish.example"], []));

        Assert.False(list.Apply(Build(3, 2, ["c-phish.example"], []))); // changes to another version
        Assert.True(list.Apply(Build(2, 1, ["c-phish.example"], ["a-phish.example"])));

        Assert.Equal((2, 2L), (list.Count, list.Version));
        Assert.False(list.Contains("a-phish.example"));
        Assert.True(list.Contains("b-phish.example"));
        Assert.True(list.Contains("c-phish.example"));
        Assert.Throws<FormatException>(() => list.Apply("NPH1"u8));
    }

    [Fact]
    public void Known_phishing_hosts_make_links_and_senders_dangerous()
    {
        var list = new PhishingList();
        list.Apply(Build(1, 0, ["evil.github.io", "bad-sender.example"], []));

        Assert.Contains(LinkInspector.Inspect("https://evil.github.io/login", known: list).Findings, f => f.Severity == Severity.High);
        Assert.DoesNotContain(LinkInspector.Inspect("https://good.github.io/", known: list).Findings, f => f.Severity == Severity.High);

        var message = new MimeMessage { Subject = "Konto", Body = new TextPart("html") { Text = "<a href=\"https://evil.github.io/login\">Anmelden</a>" } };
        message.From.Add(new MailboxAddress("Bank", "info@bad-sender.example"));
        var result = MessageAnalyzer.Analyze(message, known: list);

        Assert.Equal(ThreatLevel.Dangerous, result.Level);
        Assert.Equal(2, result.Findings.Count(f => f.Severity == Severity.High));
    }

    [Fact]
    public async Task The_updater_downloads_once_then_only_changes_and_keeps_a_copy()
    {
        var directory = Directory.CreateTempSubdirectory("neruna-phishing").FullName;
        try
        {
            var http = new FakeServer();
            http.Answers["https://neruna.example/api/v1/phishing/hosts"] = Build(1, 0, ["a-phish.example"], []);
            var list = new PhishingList();
            var updater = new PhishingListUpdater(new HttpClient(http), directory, list, new Uri("https://neruna.example/"));

            Assert.True(await updater.UpdateIfDueAsync(TimeSpan.FromHours(12), Ct));
            Assert.True(list.Contains("a-phish.example"));
            Assert.False(await updater.UpdateIfDueAsync(TimeSpan.FromHours(12), Ct)); // just asked

            http.Answers["https://neruna.example/api/v1/phishing/hosts?since=1"] = Build(2, 1, ["b-phish.example"], []);
            await updater.UpdateAsync(Ct);
            Assert.True(list.Contains("b-phish.example"));

            // The next start: the stored copy, before any network.
            var again = new PhishingList();
            new PhishingListUpdater(new HttpClient(http), directory, again, new Uri("https://neruna.example/")).LoadStored();
            Assert.Equal((2, 2L), (again.Count, again.Version));

            // Unchanged: 304, nothing downloaded.
            http.Answers.Remove("https://neruna.example/api/v1/phishing/hosts?since=2");
            await updater.UpdateAsync(Ct);
            Assert.Equal(2L, list.Version);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task A_busy_server_is_asked_once_more_then_left_alone()
    {
        var directory = Directory.CreateTempSubdirectory("neruna-phishing").FullName;
        try
        {
            // The host's guard: 429 without Retry-After once, then the list.
            var http = new FakeServer { BusyTimes = 1 };
            http.Answers["https://neruna.example/api/v1/phishing/hosts"] = Build(1, 0, ["a-phish.example"], []);
            var list = new PhishingList();
            var updater = new PhishingListUpdater(new HttpClient(http), directory, list, new Uri("https://neruna.example/")) { RetryDelay = TimeSpan.Zero };
            await updater.UpdateAsync(Ct);
            Assert.True(list.Contains("a-phish.example"));

            // Still busy: no error storm – it waits, also "Jetzt aktualisieren".
            http.BusyTimes = 5;
            await Assert.ThrowsAsync<PhishingListBusyException>(() => updater.UpdateAsync(Ct));
            Assert.True(updater.BusyUntil > DateTimeOffset.UtcNow.AddMinutes(4));
            var asked = http.Requests;
            await Assert.ThrowsAsync<PhishingListBusyException>(() => updater.UpdateAsync(Ct));
            Assert.Equal(asked, http.Requests);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void The_real_list_is_fast_enough()
    {
        // Optional: NERUNA_TEST_PHISHING=<full list from neruna.cloud>.
        var file = Environment.GetEnvironmentVariable("NERUNA_TEST_PHISHING");
        Assert.SkipWhen(file is null, "NERUNA_TEST_PHISHING not set");
        var list = new PhishingList();
        list.Apply(File.ReadAllBytes(file!));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 10_000; i++)
        {
            list.Contains($"host{i}.sub.example.com");
        }

        Assert.True(list.Count > 100_000);
        Assert.True(watch.ElapsedMilliseconds < 2000, $"{watch.ElapsedMilliseconds} ms for 10 000 lookups");
    }

    // The server's format, for tests: "NPH1", kind, version, base, counts, keys.
    private static byte[] Build(long version, long baseVersion, string[] added, string[] removed)
    {
        static byte[] Keys(string[] hosts) => [.. hosts.Select(h => PhishingList.KeyOf(PhishingList.Normalize(h)!)).OrderBy(k => k, Comparer<byte[]>.Create((a, b) => a.AsSpan().SequenceCompareTo(b))).SelectMany(k => k)];
        var data = new List<byte>("NPH1"u8.ToArray()) { (byte)(baseVersion == 0 ? 1 : 2) };
        var number = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(number, version);
        data.AddRange(number);
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(number, baseVersion);
        data.AddRange(number);
        var count = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(count, (uint)added.Length);
        data.AddRange(count);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(count, (uint)removed.Length);
        data.AddRange(count);
        data.AddRange(Keys(added));
        data.AddRange(Keys(removed));
        return [.. data];
    }

    private sealed class FakeServer : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Answers { get; } = [];

        public int BusyTimes { get; set; }

        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            if (BusyTimes > 0)
            {
                BusyTimes--;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
            }

            return Task.FromResult(Answers.TryGetValue(request.RequestUri!.ToString(), out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotModified));
        }
    }
}
