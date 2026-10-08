using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Neruna.Core;
using Neruna.Core.Cloud;
using Neruna.Core.Security;

namespace Neruna.Client.Tests;

public class CloudTests
{
    private static byte[] FromBase64Url(string value) =>
        Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/').PadRight((value.Length + 3) / 4 * 4, '='));

    [Fact]
    public void Device_assertion_is_an_es256_jwt_the_server_can_verify()
    {
        using var key = DeviceKey.Create();
        var now = DateTimeOffset.UtcNow;
        var parts = key.CreateAssertion("01DEVICE", new Uri("https://cloud.example/api/v1/token"), now).Split('.');

        var header = JsonDocument.Parse(FromBase64Url(parts[0])).RootElement;
        var claims = JsonDocument.Parse(FromBase64Url(parts[1])).RootElement;
        Assert.Equal(("ES256", "01DEVICE"), (header.GetProperty("alg").GetString(), header.GetProperty("kid").GetString()));
        Assert.Equal("https://cloud.example/api/v1/token", claims.GetProperty("aud").GetString());
        Assert.Equal(now.ToUnixTimeSeconds() + 60, claims.GetProperty("exp").GetInt64());

        // Only the public key (as sent at enrollment) is needed; the signature is r‖s, 64 bytes.
        using var verifier = ECDsa.Create();
        verifier.ImportFromPem(key.PublicKeyPem);
        var signature = FromBase64Url(parts[2]);
        Assert.Equal(64, signature.Length);
        Assert.True(verifier.VerifyData(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), signature, HashAlgorithmName.SHA256));

        // Every assertion is unique (replay protection on the server).
        Assert.NotEqual(claims.GetProperty("jti").GetString(),
            JsonDocument.Parse(FromBase64Url(key.CreateAssertion("01DEVICE", new Uri("https://cloud.example/api/v1/token"), now).Split('.')[1])).RootElement.GetProperty("jti").GetString());

        // The private key survives a round trip through the keychain format.
        using var again = DeviceKey.Import(key.ExportPrivateKey());
        Assert.Equal(key.PublicKeyPem, again.PublicKeyPem);
    }

    [Theory]
    [InlineData("cloud.neruna.org", "https://cloud.neruna.org/")]
    [InlineData("https://portal.example.ch/neruna/", "https://portal.example.ch/neruna/")]
    [InlineData("http://localhost:8080", "http://localhost:8080/")]
    public void Server_addresses_are_normalised(string typed, string expected) =>
        Assert.Equal(new Uri(expected), CloudController.ParseServer(typed));

    [Theory]
    [InlineData("http://cloud.example.ch")]
    [InlineData("")]
    [InlineData("ftp://cloud.example.ch")]
    public void Unencrypted_or_odd_addresses_are_refused(string typed) =>
        Assert.Equal("invalid_server", Assert.Throws<CloudException>(() => CloudController.ParseServer(typed)).Code);

    /// <summary>
    /// Against a running Neruna server (desktop: NERUNA_TEST_CLOUD_URL=http://localhost:8080, plus a fresh code from
    /// <c>php artisan neruna:code anna.muster@muster-informatik.example</c> in NERUNA_TEST_CLOUD_CODE / _PIN).
    /// </summary>
    [Fact]
    public async Task Connects_reads_the_profile_and_disconnects_against_a_real_server()
    {
        var url = Environment.GetEnvironmentVariable("NERUNA_TEST_CLOUD_URL");
        var code = Environment.GetEnvironmentVariable("NERUNA_TEST_CLOUD_CODE");
        var pin = Environment.GetEnvironmentVariable("NERUNA_TEST_CLOUD_PIN");
        Assert.SkipWhen(string.IsNullOrEmpty(url) || string.IsNullOrEmpty(code) || string.IsNullOrEmpty(pin), "NERUNA_TEST_CLOUD_URL/_CODE/_PIN not set");
        var ct = TestContext.Current.CancellationToken;
        await using var env = await TestEnvironment.CreateAsync();
        using var http = new HttpClient();
        var cloud = new CloudController(http, env.Get<ISettingsStore>(), env.Get<ICredentialStore>(), Microsoft.Extensions.Logging.Abstractions.NullLogger<CloudController>.Instance);

        // A wrong PIN is refused with the server's German message; the code still works afterwards.
        var wrong = await Assert.ThrowsAsync<CloudException>(() => cloud.ConnectAsync(CloudController.ParseServer(url!), code!, pin == "000000" ? "111111" : "000000",
            new DeviceInfo("Testgerät", "0.1.2", "Linux", "Debian 12", "tester"), ct));
        Assert.Equal("invalid_pin", wrong.Code);
        Assert.StartsWith("Die PIN stimmt nicht.", wrong.Message, StringComparison.Ordinal);

        var connection = await cloud.ConnectAsync(CloudController.ParseServer(url!), code!, pin!, new DeviceInfo("Testgerät", "0.1.2", "Linux", "Debian 12", "tester"), ct);
        Assert.Equal("Anna Muster", connection.MemberName);
        Assert.NotNull(await env.Get<ICredentialStore>().GetSecretAsync(CloudController.KeyId, ct));
        Assert.Equal(connection.DeviceId, (await cloud.GetConnectionAsync(ct))!.DeviceId);

        var me = await cloud.GetProfileAsync(ct);
        Assert.Equal(("Anna", "Projektleiterin", "Testgerät"), (me.Member.FirstName, me.Member.Position, me.Device.Name));
        Assert.Equal("Muster Informatik AG", me.Organization.Name);

        // Used once: the same code does not connect a second device.
        var again = await Assert.ThrowsAsync<CloudException>(() => cloud.ConnectAsync(CloudController.ParseServer(url!), code!, pin!, DeviceInfo.Current(), ct));
        Assert.Equal("invalid_code", again.Code);

        await cloud.DisconnectAsync(ct);
        Assert.Null(await cloud.GetConnectionAsync(ct));
        Assert.Null(await env.Get<ICredentialStore>().GetSecretAsync(CloudController.KeyId, ct));
    }
}
