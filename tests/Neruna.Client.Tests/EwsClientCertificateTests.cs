using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Neruna.Core.Accounts;
using Neruna.Core.Providers;
using Neruna.Core.Security;
using Neruna.Providers.Ews;

namespace Neruna.Client.Tests;

/// <summary>
/// Exchange behind a reverse proxy that demands a client certificate: a real TLS server on localhost that refuses
/// connections without one, and Neruna's real HTTP stack (no fake handler) with an imported .p12.
/// </summary>
public class EwsClientCertificateTests
{
    [Fact]
    public async Task The_imported_certificate_is_shown_to_a_proxy_that_demands_one()
    {
        var ct = TestContext.Current.CancellationToken;
        using var ca = Certificates.Authority();
        using var serverCertificate = Certificates.Issue(ca, "CN=localhost", server: true);
        using var clientCertificate = Certificates.Issue(ca, "CN=Anna Muster", server: false);
        await using var proxy = new ClientCertificateProxy(serverCertificate, ca);

        var credentials = new MemoryCredentialStore();
        var certificates = new ClientCertificates(credentials);
        var pkcs12 = clientCertificate.Export(X509ContentType.Pkcs12, "zertifikat-passwort");
        await Assert.ThrowsAsync<CertificatePasswordException>(() => certificates.ImportAsync(pkcs12, "falsch", ct));
        var imported = await certificates.ImportAsync(pkcs12, "zertifikat-passwort", ct);
        Assert.Equal(("Anna Muster", "Neruna Test Proxy CA", false), (imported.Subject, imported.Issuer, imported.IsFromSystem));
        Assert.Equal(clientCertificate.Thumbprint, imported.Reference);

        // Without a certificate the proxy refuses the connection.
        var factory = new EwsConnectionFactory(credentials, certificates, NullLoggerFactory.Instance);
        var without = Connection(proxy.Url, null, credentials);
        await using (var mail = new EwsMailProviderFactory(factory).Create(without))
        {
            var refused = await Assert.ThrowsAsync<EwsException>(() => mail.TestConnectionAsync(ct));
            Assert.Contains("localhost", refused.Message, StringComparison.Ordinal);
        }

        // With it: TLS succeeds, the proxy sees Anna's certificate, Basic goes along, EWS answers.
        var with = Connection(proxy.Url, imported.Reference, credentials);
        await using (var mail = new EwsMailProviderFactory(factory).Create(with))
        {
            await mail.TestConnectionAsync(ct);
        }

        Assert.Equal("CN=Anna Muster", proxy.SeenClient);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("anna@example.com:geheim")), proxy.SeenAuthorization);

        // Removed from the keychain: a clear message instead of a TLS error.
        await certificates.RemoveAsync(imported.Reference, ct);
        var factoryAfter = new EwsConnectionFactory(credentials, certificates, NullLoggerFactory.Instance);
        await using (var mail = new EwsMailProviderFactory(factoryAfter).Create(with))
        {
            Assert.Equal("ClientCertificate", (await Assert.ThrowsAsync<EwsException>(() => mail.TestConnectionAsync(ct))).Code);
        }
    }

    private static ServiceConnection Connection(Uri url, string? certificate, MemoryCredentialStore credentials)
    {
        var settings = new Dictionary<string, string>(new EwsSettings(url, "anna@example.com", "anna@example.com", EwsAuthentication.Basic).ToDictionary())
        {
            // The test CA is not trusted by the system.
            [TlsCertificates.AcceptAnyCertificateKey] = "true",
        };
        if (certificate is not null)
        {
            settings[TlsCertificates.ClientCertificateKey] = certificate;
        }

        var connection = new ServiceConnection(Guid.NewGuid(), ServiceKind.Mail, ProviderIds.Ews, settings);
        credentials.Secrets[connection.Id] = "geheim";
        return connection;
    }

    private static class Certificates
    {
        public static X509Certificate2 Authority()
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=Neruna Test Proxy CA", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
            return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        }

        public static X509Certificate2 Issue(X509Certificate2 ca, string subject, bool server)
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(server ? "1.3.6.1.5.5.7.3.1" : "1.3.6.1.5.5.7.3.2")], false));
            if (server)
            {
                var names = new SubjectAlternativeNameBuilder();
                names.AddDnsName("localhost");
                request.CertificateExtensions.Add(names.Build());
            }

            using var issued = request.Create(ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddMonths(6), RandomNumberGenerator.GetBytes(8));
            using var withKey = issued.CopyWithPrivateKey(key);

            // Through PKCS#12, so the key works for TLS on every platform.
            return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pkcs12, string.Empty), string.Empty);
        }
    }

    /// <summary>A minimal HTTPS endpoint that requires a client certificate issued by <c>ca</c> and answers one EWS request.</summary>
    private sealed class ClientCertificateProxy : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;

        public ClientCertificateProxy(X509Certificate2 certificate, X509Certificate2 ca)
        {
            _listener.Start();
            Url = new Uri($"https://localhost:{((IPEndPoint)_listener.LocalEndpoint).Port}/EWS/Exchange.asmx");
            _loop = Task.Run(() => AcceptAsync(certificate, ca));
        }

        public Uri Url { get; }

        public string? SeenClient { get; private set; }

        public string? SeenAuthorization { get; private set; }

        private async Task AcceptAsync(X509Certificate2 certificate, X509Certificate2 ca)
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        try
                        {
                            await ServeAsync(client, certificate, ca);
                        }
                        catch (Exception ex) when (ex is IOException or System.Security.Authentication.AuthenticationException or OperationCanceledException)
                        {
                            // refused or closed: what a proxy does without a valid certificate
                        }
                    }
                });
            }
        }

        private async Task ServeAsync(TcpClient client, X509Certificate2 certificate, X509Certificate2 ca)
        {
            await using var tls = new SslStream(client.GetStream());
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate,
                ClientCertificateRequired = true,
                RemoteCertificateValidationCallback = (_, presented, _, _) =>
                    presented is not null && X509CertificateLoader.LoadCertificate(presented.GetRawCertData()).Issuer == ca.Subject,
            }, _stop.Token);
            SeenClient = tls.RemoteCertificate?.Subject;

            // One request: headers, then the body by Content-Length.
            var buffer = new MemoryStream();
            var chunk = new byte[4096];
            string head;
            while (true)
            {
                var read = await tls.ReadAsync(chunk, _stop.Token);
                if (read == 0)
                {
                    return;
                }

                buffer.Write(chunk, 0, read);
                var text = Encoding.ASCII.GetString(buffer.ToArray());
                var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (end < 0)
                {
                    continue;
                }

                head = text[..end];
                var length = head.Split("\r\n").FirstOrDefault(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) is { } line
                    ? int.Parse(line[15..].Trim(), System.Globalization.CultureInfo.InvariantCulture)
                    : 0;
                while (buffer.Length < end + 4 + length && (read = await tls.ReadAsync(chunk, _stop.Token)) > 0)
                {
                    buffer.Write(chunk, 0, read);
                }

                break;
            }

            SeenAuthorization = head.Split("\r\n").FirstOrDefault(l => l.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase))?[14..].Trim();
            const string Body = """
                <?xml version="1.0" encoding="utf-8"?><s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body>
                <m:GetFolderResponse xmlns:m="http://schemas.microsoft.com/exchange/services/2006/messages" xmlns:t="http://schemas.microsoft.com/exchange/services/2006/types">
                <m:ResponseMessages><m:GetFolderResponseMessage ResponseClass="Success"><m:ResponseCode>NoError</m:ResponseCode>
                <m:Folders><t:Folder><t:FolderId Id="INBOX"/></t:Folder></m:Folders></m:GetFolderResponseMessage></m:ResponseMessages></m:GetFolderResponse></s:Body></s:Envelope>
                """;
            var bytes = Encoding.UTF8.GetBytes(Body);
            await tls.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/xml; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"), _stop.Token);
            await tls.WriteAsync(bytes, _stop.Token);
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            await _loop;
            _stop.Dispose();
        }
    }
}
