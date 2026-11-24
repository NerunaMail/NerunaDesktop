using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Neruna.Core;
using Neruna.Core.Cloud;
using Neruna.Core.Security;

namespace Neruna.Client.Tests;

/// <summary>Only Neruna servers: their certificate must name the address – before connecting and, after a grace period, for syncing.</summary>
public class ServerCertificateTests
{
    private const string Server = "https://cloud.example/";

    [Fact]
    public void The_contract_fixture_is_valid_for_its_hosts_only_until_it_expires()
    {
        var fixture = ContractFixtures.Read("server-certificate-example.json");
        var key = TestServerCertificate.PublicKey;
        var now = new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero);

        Assert.Equal(ServerCertificateStatus.Valid, ServerCertificate.Check(fixture, new Uri("https://neruna.example/"), now, key));
        Assert.Equal(ServerCertificateStatus.Valid, ServerCertificate.Check(fixture, new Uri("https://MAIL.neruna.example:8443/"), now, key));
        Assert.Equal(ServerCertificateStatus.WrongHost, ServerCertificate.Check(fixture, new Uri("https://neruna.example.evil/"), now, key));
        Assert.Equal(ServerCertificateStatus.Expired, ServerCertificate.Check(fixture, new Uri("https://neruna.example/"), new DateTimeOffset(2027, 5, 24, 0, 0, 1, TimeSpan.Zero), key));

        // Not signed with the trusted key, changed, or no certificate at all.
        Assert.Equal(ServerCertificateStatus.Invalid, ServerCertificate.Check(fixture, new Uri("https://neruna.example/"), now, Convert.FromBase64String("7Gv4qUYZT0MUiA5GLDpos3RZ4jOSf3qAi1NRbHZQQ/o=")));
        using var parsed = JsonDocument.Parse(fixture);
        var data = Convert.FromBase64String(parsed.RootElement.GetProperty("data").GetString()!);
        var changed = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(System.Text.Encoding.UTF8.GetString(data).Replace("mail.neruna.example", "mail.evil.example", StringComparison.Ordinal)));
        var tampered = JsonSerializer.Serialize(new { data = changed, signature = parsed.RootElement.GetProperty("signature").GetString() });
        Assert.Equal(ServerCertificateStatus.Invalid, ServerCertificate.Check(tampered, new Uri("https://mail.evil.example/"), now, key));
        Assert.Equal(ServerCertificateStatus.Missing, ServerCertificate.Check(null, new Uri("https://neruna.example/"), now, key));
        Assert.Equal(ServerCertificateStatus.Missing, ServerCertificate.Check("{\"error\":\"no_certificate\"}", new Uri("https://neruna.example/"), now, key));
        Assert.Equal(ServerCertificateStatus.Missing, ServerCertificate.Check("<html>", new Uri("https://neruna.example/"), now, key));
    }

    private static void Organisation(TestEnvironment env)
    {
        env.Http.Responses[Server + "api/v1/enrollment"] = (HttpStatusCode.OK, JsonSerializer.Serialize(new { deviceId = "DEV", organizationName = "Example AG", memberName = "Anna Muster" }));
        env.Http.Responses[Server + "api/v1/token"] = (HttpStatusCode.OK, """{"accessToken":"t","tokenType":"Bearer","expiresIn":900}""");
        env.Http.Responses[Server + "api/v1/signatures"] = (HttpStatusCode.OK, """{"available":true,"signatures":[]}""");
    }

    private static CloudController Cloud(TestEnvironment env) =>
        new(new HttpClient(env.Http), env.Get<ISettingsStore>(), env.Get<ICredentialStore>(), NullLogger<CloudController>.Instance);

    private static readonly DeviceInfo Device = new("Testgerät", "0.9.0", "Linux", "Debian 13", "anna");

    [Fact]
    public async Task Connecting_needs_a_certificate_for_this_address()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var env = await TestEnvironment.CreateAsync();
        Organisation(env);
        var cloud = Cloud(env);

        env.Http.NoServerCertificate = true;
        var missing = await Assert.ThrowsAsync<CloudException>(() => cloud.ConnectAsync(new Uri(Server), "CODE", "123456", Device, ct));
        Assert.Equal(CloudController.NotVerifiedCode, missing.Code);
        Assert.Contains("cloud.example", missing.Message, StringComparison.Ordinal);

        // Another server's certificate, copied: not this address.
        env.Http.Responses[Server + "api/v1/server-certificate"] = (HttpStatusCode.OK, TestServerCertificate.For(["neruna.example"]));
        Assert.Equal(CloudController.NotVerifiedCode, (await Assert.ThrowsAsync<CloudException>(() => cloud.ConnectAsync(new Uri(Server), "CODE", "123456", Device, ct))).Code);
        Assert.DoesNotContain(env.Http.Requested, u => u.EndsWith("/enrollment", StringComparison.Ordinal));
        Assert.Empty(await cloud.GetConnectionsAsync(ct));

        env.Http.Responses[Server + "api/v1/server-certificate"] = (HttpStatusCode.OK, TestServerCertificate.For(["cloud.example"]));
        Assert.Equal("Example AG", (await cloud.ConnectAsync(new Uri(Server), "CODE", "123456", Device, ct)).OrganizationName);
    }

    [Fact]
    public async Task A_server_that_loses_its_certificate_is_synced_with_for_a_month_then_again_once_it_has_one()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var env = await TestEnvironment.CreateAsync();
        Organisation(env);
        var connection = await Cloud(env).ConnectAsync(new Uri(Server), "CODE", "123456", Device, ct);

        // Gone: syncing goes on, and since when is noted (each new controller signs in anew, as after a restart).
        env.Http.NoServerCertificate = true;
        var cloud = Cloud(env);
        var changed = 0;
        cloud.ServerStatusChanged += (_, _) => changed++;
        await cloud.GetSignaturesAsync(connection, ct);
        var since = await cloud.GetUnverifiedSinceAsync(connection, ct);
        Assert.NotNull(since);
        Assert.Equal(1, changed);
        await Cloud(env).GetSignaturesAsync(connection, ct);
        Assert.Equal(since, await cloud.GetUnverifiedSinceAsync(connection, ct));

        // A month later: stopped.
        await env.Get<ISettingsStore>().SetAsync(SettingKeys.CloudServerUnverifiedSince, DateTimeOffset.UtcNow.AddDays(-31).ToString("O", System.Globalization.CultureInfo.InvariantCulture), ct);
        var stopped = await Assert.ThrowsAsync<CloudException>(() => Cloud(env).GetSignaturesAsync(connection, ct));
        Assert.Equal(CloudController.NotVerifiedCode, stopped.Code);
        Assert.Contains("Example AG", stopped.Message, StringComparison.Ordinal);

        // Back: syncing again, the note gone.
        env.Http.NoServerCertificate = false;
        cloud = Cloud(env);
        await cloud.GetSignaturesAsync(connection, ct);
        Assert.Null(await cloud.GetUnverifiedSinceAsync(connection, ct));
    }
}
