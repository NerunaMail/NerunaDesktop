using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Neruna.Contracts.Discovery;
using Neruna.Core.Security;
using Neruna.Providers.Imap;

namespace Neruna.Client.Tests;

/// <summary>
/// Finding Proton Mail Bridge on this computer: small local servers that greet like the Bridge (IMAP with implicit TLS
/// or STARTTLS, SMTP with STARTTLS, a self-signed certificate) – at the usual ports and at others.
/// </summary>
public class ProtonBridgeTests
{
    private static X509Certificate2 SelfSigned()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=127.0.0.1", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12, string.Empty), string.Empty);
    }

    [Fact]
    public async Task The_bridge_is_found_at_its_usual_ports_and_its_certificate_remembered()
    {
        var ct = TestContext.Current.CancellationToken;
        using var certificate = SelfSigned();
        await using var imap = FakeServer.Imap(certificate, implicitTls: false);
        await using var smtp = FakeServer.Smtp(certificate);

        var found = await ProtonBridge.DetectAsync(() => throw new InvalidOperationException("only the usual ports"), (imap.Port, smtp.Port), ct);

        Assert.True(found.IsComplete);
        Assert.Equal(new BridgeEndpoint(imap.Port, SocketSecurity.StartTls, TlsCertificates.Fingerprint(certificate)), found.Imap);
        Assert.Equal(new BridgeEndpoint(smtp.Port, SocketSecurity.StartTls, TlsCertificates.Fingerprint(certificate)), found.Smtp);

        var options = ProtonBridge.ConnectionOptions([found.Imap!.CertificateFingerprint, found.Smtp!.CertificateFingerprint]);
        Assert.Equal(TlsCertificates.Fingerprint(certificate), options[TlsCertificates.PinnedCertificateKey]);
        Assert.Equal(("true", ImapSettings.ProtonBridgeKind), (options[ImapSettings.ServerSavesSentKey], options[ImapSettings.KindKey]));
    }

    [Fact]
    public async Task Other_ports_are_searched_and_the_bridge_told_apart_from_other_servers()
    {
        var ct = TestContext.Current.CancellationToken;
        using var certificate = SelfSigned();
        await using var bridge = FakeServer.Imap(certificate, implicitTls: true);
        await using var otherImap = FakeServer.Imap(certificate, implicitTls: false, greeting: "* OK [CAPABILITY IMAP4rev1] Dovecot ready.");
        await using var smtp = FakeServer.Smtp(certificate);
        await using var silent = FakeServer.Silent();
        var unused = FreePort();

        // Nothing at the usual ports: the open ones are tried; the Bridge is the IMAP server that says so.
        var found = await ProtonBridge.DetectAsync(() => [otherImap.Port, silent.Port, bridge.Port, smtp.Port], (unused, unused + 1), ct);

        Assert.Equal((bridge.Port, SocketSecurity.SslOnConnect), (found.Imap!.Port, found.Imap.Security));
        Assert.Equal(TlsCertificates.Fingerprint(certificate), found.Imap.CertificateFingerprint);
        Assert.Equal(smtp.Port, found.Smtp!.Port);

        // Two SMTP servers and none at the usual port: the user chooses.
        await using var secondSmtp = FakeServer.Smtp(certificate);
        var unclear = await ProtonBridge.DetectAsync(() => [bridge.Port, smtp.Port, secondSmtp.Port], (unused, unused + 1), ct);
        Assert.Null(unclear.Smtp);
        Assert.Equal(new[] { smtp.Port, secondSmtp.Port }.Order(), unclear.SmtpCandidates);

        // No Bridge at all.
        var none = await ProtonBridge.DetectAsync(() => [silent.Port], (unused, unused + 1), ct);
        Assert.Null(none.Imap);
    }

    [Fact]
    public void A_pinned_certificate_is_accepted_others_get_the_usual_check()
    {
        using var pinned = SelfSigned();
        using var other = SelfSigned();
        var check = TlsCertificates.For(new Dictionary<string, string> { [TlsCertificates.PinnedCertificateKey] = TlsCertificates.Fingerprint(pinned) });

        Assert.True(check(this, pinned, null, SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch));
        Assert.False(check(this, other, null, SslPolicyErrors.RemoteCertificateNameMismatch));
        Assert.True(check(this, other, null, SslPolicyErrors.None));
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>A line-based mail server on 127.0.0.1: greeting, answers per command, STARTTLS.</summary>
    private sealed class FakeServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;

        private FakeServer(X509Certificate2? certificate, bool implicitTls, string? greeting, Func<string, string?> answer)
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _loop = Task.Run(async () =>
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

                    _ = Task.Run(() => ServeAsync(client, certificate, implicitTls, greeting, answer));
                }
            });
        }

        public int Port { get; }

        public static FakeServer Imap(X509Certificate2 certificate, bool implicitTls, string greeting = "* OK [CAPABILITY IMAP4rev1 IDLE STARTTLS] Proton Mail Bridge 03.15.1 - gluon session ID 1") =>
            new(certificate, implicitTls, greeting, line =>
            {
                var parts = line.Split(' ', 3);
                var command = parts.Length > 1 ? parts[1].ToUpperInvariant() : string.Empty;
                return command switch
                {
                    "STARTTLS" => $"{parts[0]} OK Begin TLS negotiation now",
                    "CAPABILITY" => $"* CAPABILITY IMAP4rev1 IDLE AUTH=PLAIN\r\n{parts[0]} OK CAPABILITY completed",
                    "LOGOUT" => $"* BYE\r\n{parts[0]} OK LOGOUT completed",
                    _ => $"{parts[0]} OK done",
                };
            });

        public static FakeServer Smtp(X509Certificate2 certificate) =>
            new(certificate, false, "220 127.0.0.1 ESMTP Service Ready", line => line.ToUpperInvariant() switch
            {
                var l when l.StartsWith("EHLO", StringComparison.Ordinal) => "250-127.0.0.1\r\n250-STARTTLS\r\n250 AUTH PLAIN LOGIN",
                "STARTTLS" => "220 Ready to start TLS",
                "QUIT" => "221 Bye",
                _ => "250 OK",
            });

        /// <summary>Accepts and says nothing (like many other local services).</summary>
        public static FakeServer Silent() => new(null, false, null, _ => null);

        private async Task ServeAsync(TcpClient client, X509Certificate2? certificate, bool implicitTls, string? greeting, Func<string, string?> answer)
        {
            using (client)
            {
                try
                {
                    Stream stream = client.GetStream();
                    if (implicitTls)
                    {
                        stream = await TlsAsync(stream, certificate!);
                    }

                    if (greeting is null)
                    {
                        await Task.Delay(Timeout.Infinite, _stop.Token);
                    }

                    await WriteAsync(stream, greeting!);
                    var reader = new StreamReader(stream, Encoding.ASCII);
                    while (await reader.ReadLineAsync(_stop.Token) is { } line)
                    {
                        if (answer(line) is not { } response)
                        {
                            continue;
                        }

                        await WriteAsync(stream, response);
                        if (line.Contains("STARTTLS", StringComparison.OrdinalIgnoreCase))
                        {
                            stream = await TlsAsync(stream, certificate!);
                            reader = new StreamReader(stream, Encoding.ASCII);
                        }
                        else if (line.Contains("LOGOUT", StringComparison.OrdinalIgnoreCase) || line.Equals("QUIT", StringComparison.OrdinalIgnoreCase))
                        {
                            return;
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or System.Security.Authentication.AuthenticationException or ObjectDisposedException)
                {
                    // the probe hung up
                }
            }
        }

        private async Task<Stream> TlsAsync(Stream stream, X509Certificate2 certificate)
        {
            var tls = new SslStream(stream, false);
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, _stop.Token);
            return tls;
        }

        private async Task WriteAsync(Stream stream, string text)
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(text + "\r\n"), _stop.Token);
            await stream.FlushAsync(_stop.Token);
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
