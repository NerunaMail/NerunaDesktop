using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Neruna.Core.Security;

namespace Neruna.Client.Tests;

/// <summary>Which server certificates the mail connections accept.</summary>
public class TlsCertificatesTests
{
    [Fact]
    public void An_unknown_revocation_status_is_no_reason_to_refuse_anything_else_is()
    {
        Assert.True(TlsCertificates.IsAcceptable(SslPolicyErrors.None, []));
        // Behind a firewall with TLS inspection: revocation lists not reachable.
        Assert.True(TlsCertificates.IsAcceptable(SslPolicyErrors.RemoteCertificateChainErrors,
            [X509ChainStatusFlags.RevocationStatusUnknown, X509ChainStatusFlags.OfflineRevocation | X509ChainStatusFlags.RevocationStatusUnknown]));

        Assert.False(TlsCertificates.IsAcceptable(SslPolicyErrors.RemoteCertificateChainErrors, [X509ChainStatusFlags.Revoked]));
        Assert.False(TlsCertificates.IsAcceptable(SslPolicyErrors.RemoteCertificateChainErrors, [X509ChainStatusFlags.UntrustedRoot]));
        Assert.False(TlsCertificates.IsAcceptable(SslPolicyErrors.RemoteCertificateChainErrors, [X509ChainStatusFlags.RevocationStatusUnknown | X509ChainStatusFlags.NotTimeValid]));
        Assert.False(TlsCertificates.IsAcceptable(SslPolicyErrors.RemoteCertificateNameMismatch, []));
        Assert.False(TlsCertificates.IsAcceptable(SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateChainErrors, [X509ChainStatusFlags.RevocationStatusUnknown]));
    }

    [Fact]
    public void Accepting_any_certificate_is_a_setting_of_the_connection()
    {
        Assert.False(TlsCertificates.AcceptsAnyCertificate(new Dictionary<string, string>()));
        Assert.True(TlsCertificates.AcceptsAnyCertificate(new Dictionary<string, string> { [TlsCertificates.AcceptAnyCertificateKey] = "true" }));
        Assert.True(TlsCertificates.For(acceptAny: true)(this, null, null, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.False(TlsCertificates.For(acceptAny: false)(this, null, null, SslPolicyErrors.RemoteCertificateNameMismatch));

        var settings = new Neruna.Providers.Imap.ImapSettings("mail.test", 993, Neruna.Contracts.Discovery.SocketSecurity.SslOnConnect, "mail.test", 465,
            Neruna.Contracts.Discovery.SocketSecurity.SslOnConnect, "anna", AcceptAnyCertificate: true);
        Assert.True(Neruna.Providers.Imap.ImapSettings.FromDictionary(settings.ToDictionary()).AcceptAnyCertificate);
        Assert.False(Neruna.Providers.Imap.ImapSettings.FromDictionary((settings with { AcceptAnyCertificate = false }).ToDictionary()).AcceptAnyCertificate);
    }
}
