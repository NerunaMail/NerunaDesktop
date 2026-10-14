using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;
using Neruna.Contracts.Discovery;
using Neruna.Core.Security;

namespace Neruna.Providers.Imap;

/// <summary>A mail server of the Bridge on this computer: port, encryption and the fingerprint of its certificate.</summary>
public sealed record BridgeEndpoint(int Port, SocketSecurity Security, string? CertificateFingerprint);

/// <summary>What the search found; <paramref name="SmtpCandidates"/>: further local SMTP servers when it is not clear which one.</summary>
public sealed record BridgeDetection(BridgeEndpoint? Imap, BridgeEndpoint? Smtp, IReadOnlyList<int> SmtpCandidates)
{
    public bool IsComplete => Imap is not null && Smtp is not null;
}

/// <summary>
/// Proton Mail Bridge: runs on this computer and offers the Proton mailbox as IMAP/SMTP on 127.0.0.1 – by default
/// IMAP 1143 and SMTP 1025 with STARTTLS and a self-signed certificate. The search tries those ports first, then every
/// port open on this computer, and recognises the Bridge by its IMAP greeting ("Proton Mail Bridge"). The certificate
/// is remembered (pinned) instead of accepting any.
/// </summary>
public static class ProtonBridge
{
    public const string Host = "127.0.0.1";
    public const int DefaultImapPort = 1143;
    public const int DefaultSmtpPort = 1025;

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(1.5);

    private enum Kind
    {
        None,
        Imap,
        BridgeImap,
        Smtp,
    }

    /// <param name="listeningPorts">The ports open on this computer (tests pass their own); null: asked from the system.</param>
    /// <param name="defaultPorts">IMAP and SMTP port tried first (tests pass their own).</param>
    public static async Task<BridgeDetection> DetectAsync(Func<IReadOnlyList<int>>? listeningPorts = null, (int Imap, int Smtp)? defaultPorts = null, CancellationToken cancellationToken = default)
    {
        var (imapPort, smtpPort) = defaultPorts ?? (DefaultImapPort, DefaultSmtpPort);
        var found = new Dictionary<int, (Kind Kind, SocketSecurity Security)>();
        foreach (var port in new[] { imapPort, smtpPort })
        {
            found[port] = await ProbeAsync(port, cancellationToken);
        }

        // Not both at the usual ports: every other open port, at most a few at a time.
        if (!found.Values.Any(f => f.Kind == Kind.BridgeImap) || !found.Values.Any(f => f.Kind == Kind.Smtp))
        {
            var others = (listeningPorts ?? LocalPorts)().Where(p => !found.ContainsKey(p)).Distinct().Take(400).ToList();
            using var parallel = new SemaphoreSlim(32);
            var probes = others.Select(async port =>
            {
                await parallel.WaitAsync(cancellationToken);
                try
                {
                    return (port, result: await ProbeAsync(port, cancellationToken));
                }
                finally
                {
                    parallel.Release();
                }
            });
            foreach (var (port, result) in await Task.WhenAll(probes))
            {
                found[port] = result;
            }
        }

        // IMAP: the one that says it is the Bridge, else the only IMAP server on this computer.
        var imapServers = found.Where(f => f.Value.Kind is Kind.Imap or Kind.BridgeImap).OrderByDescending(f => f.Value.Kind == Kind.BridgeImap).ThenBy(f => f.Key != imapPort).ToList();
        var imap = imapServers.FirstOrDefault(f => f.Value.Kind == Kind.BridgeImap) is { Key: > 0 } bridge ? bridge
            : imapServers.Count == 1 ? imapServers[0] : default;

        // SMTP: the usual port, else the only SMTP server; several: the user chooses.
        var smtpServers = found.Where(f => f.Value.Kind == Kind.Smtp).Select(f => f.Key).Order().ToList();
        int? smtp = smtpServers.Contains(smtpPort) ? smtpPort : smtpServers.Count == 1 ? smtpServers[0] : null;

        BridgeEndpoint? imapEndpoint = imap.Key > 0
            ? new BridgeEndpoint(imap.Key, imap.Value.Security, await ImapFingerprintAsync(imap.Key, imap.Value.Security, cancellationToken))
            : null;
        BridgeEndpoint? smtpEndpoint = smtp is { } s
            ? new BridgeEndpoint(s, found[s].Security, await SmtpFingerprintAsync(s, found[s].Security, cancellationToken))
            : null;
        return new BridgeDetection(imapEndpoint, smtpEndpoint, smtp is null ? smtpServers : []);
    }

    /// <summary>TCP ports listened on at the loopback (or all) addresses; empty where the system does not tell.</summary>
    private static IReadOnlyList<int> LocalPorts()
    {
        try
        {
            return [.. IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
                .Where(e => IPAddress.IsLoopback(e.Address) || e.Address.Equals(IPAddress.Any) || e.Address.Equals(IPAddress.IPv6Any))
                .Select(e => e.Port)
                .Where(p => p >= 1024)
                .Order()];
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException or InvalidOperationException)
        {
            // Without the list: the range where the Bridge's ports usually are.
            return [.. Enumerable.Range(1024, 1200)];
        }
    }

    /// <summary>What answers at a port: the greeting in plain text, else after a TLS handshake (implicit TLS).</summary>
    private static async Task<(Kind Kind, SocketSecurity Security)> ProbeAsync(int port, CancellationToken cancellationToken)
    {
        if (await GreetingAsync(port, tls: false, cancellationToken) is { } plain)
        {
            var kind = KindOf(plain);
            // SMTP names STARTTLS only after EHLO; the Bridge always offers it.
            return (kind, (kind != Kind.None && plain.Contains("STARTTLS", StringComparison.OrdinalIgnoreCase)) || kind == Kind.Smtp ? SocketSecurity.StartTls : SocketSecurity.None);
        }

        return await GreetingAsync(port, tls: true, cancellationToken) is { } secure ? (KindOf(secure), SocketSecurity.SslOnConnect) : (Kind.None, SocketSecurity.None);
    }

    private static Kind KindOf(string greeting) =>
        greeting.StartsWith("* OK", StringComparison.OrdinalIgnoreCase)
            ? greeting.Contains("Proton", StringComparison.OrdinalIgnoreCase) ? Kind.BridgeImap : Kind.Imap
            : greeting.StartsWith("220", StringComparison.Ordinal) ? Kind.Smtp : Kind.None;

#pragma warning disable CA5359 // Only to read the greeting; the certificate is checked (pinned) when connecting for real.
    private static async Task<string?> GreetingAsync(int port, bool tls, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            Stream stream = client.GetStream();
            if (tls)
            {
                var ssl = new SslStream(stream, false, (_, _, _, _) => true);
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = Host }, timeout.Token);
                stream = ssl;
            }

            var buffer = new byte[512];
            var read = await stream.ReadAsync(buffer, timeout.Token);
            await stream.DisposeAsync();
            return read > 0 ? Encoding.ASCII.GetString(buffer, 0, read).Split('\n')[0].Trim() : null;
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or System.Security.Authentication.AuthenticationException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return null;
        }
    }
#pragma warning restore CA5359

    private static async Task<string?> ImapFingerprintAsync(int port, SocketSecurity security, CancellationToken cancellationToken)
    {
        if (security == SocketSecurity.None)
        {
            return null;
        }

        string? fingerprint = null;
        using var imap = new ImapClient { Timeout = 5000, ServerCertificateValidationCallback = Capture(f => fingerprint = f) };
        try
        {
            await imap.ConnectAsync(Host, port, Options(security), cancellationToken);
            await imap.DisconnectAsync(true, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ImapProtocolException or SslHandshakeException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // no certificate then; the connection test will tell
        }

        return fingerprint;
    }

    private static async Task<string?> SmtpFingerprintAsync(int port, SocketSecurity security, CancellationToken cancellationToken)
    {
        if (security == SocketSecurity.None)
        {
            return null;
        }

        string? fingerprint = null;
        using var smtp = new SmtpClient { Timeout = 5000, ServerCertificateValidationCallback = Capture(f => fingerprint = f) };
        try
        {
            await smtp.ConnectAsync(Host, port, Options(security), cancellationToken);
            await smtp.DisconnectAsync(true, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or SocketException or SmtpProtocolException or SslHandshakeException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // no certificate then; the connection test will tell
        }

        return fingerprint;
    }

#pragma warning disable CA5359 // Accepted only to read its fingerprint, which is then pinned.
    private static RemoteCertificateValidationCallback Capture(Action<string> fingerprint) => (_, certificate, _, _) =>
    {
        if (certificate is not null)
        {
            fingerprint(TlsCertificates.Fingerprint(certificate));
        }

        return true;
    };
#pragma warning restore CA5359

    private static SecureSocketOptions Options(SocketSecurity security) => security switch
    {
        SocketSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
        SocketSecurity.StartTls => SecureSocketOptions.StartTls,
        _ => SecureSocketOptions.None,
    };

    /// <summary>The certificates at ports entered by hand (the search did not find them), to pin them.</summary>
    public static async Task<IReadOnlyList<string>> FingerprintsAsync(int imapPort, SocketSecurity imapSecurity, int smtpPort, SocketSecurity smtpSecurity, CancellationToken cancellationToken = default) =>
        [.. new[] { await ImapFingerprintAsync(imapPort, imapSecurity, cancellationToken), await SmtpFingerprintAsync(smtpPort, smtpSecurity, cancellationToken) }.OfType<string>().Distinct()];

    /// <summary>The connection settings of a Bridge account: localhost, no copy in "Sent" (the Bridge files it), pinned certificates.</summary>
    public static IReadOnlyDictionary<string, string> ConnectionOptions(IEnumerable<string?> fingerprints)
    {
        var options = new Dictionary<string, string>
        {
            [ImapSettings.KindKey] = ImapSettings.ProtonBridgeKind,
            [ImapSettings.ServerSavesSentKey] = "true",
        };
        var pinned = string.Join(',', fingerprints.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase));
        if (pinned.Length > 0)
        {
            options[TlsCertificates.PinnedCertificateKey] = pinned;
        }

        return options;
    }
}
