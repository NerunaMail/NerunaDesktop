using MimeKit;
using MimeKit.Cryptography;
using Neruna.Core.Mail;
using Neruna.Core.Security;

namespace Neruna.Client.Tests;

public class SecureMimeTests
{
    private static readonly Lazy<(Org.BouncyCastle.X509.X509Certificate Certificate, Org.BouncyCastle.Crypto.AsymmetricKeyParameter PrivateKey)> Anna = new(() => TestPki.User("Anna Muster", "anna@example.com"));
    private static readonly Lazy<(Org.BouncyCastle.X509.X509Certificate Certificate, Org.BouncyCastle.Crypto.AsymmetricKeyParameter PrivateKey)> Bob = new(() => TestPki.User("Bob Brunner", "bob@example.com"));

    [Fact]
    public async Task Pkcs12_import_reads_email_validity_and_usage()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var manager = env.Get<CertificateManager>();

        var imported = Assert.Single(await manager.ImportAsync(TestPki.Pkcs12(Anna.Value, "pw"), "pw", cancellationToken: TestContext.Current.CancellationToken));

        Assert.True(imported.HasPrivateKey);
        Assert.Equal(["anna@example.com"], imported.EmailAddresses);
        Assert.Equal("Anna Muster", imported.SubjectName);
        Assert.Equal("Neruna Test CA", imported.IssuerName);
        Assert.True(imported.CanSign);
        Assert.True(imported.CanEncrypt);
        Assert.Equal(CertificateStatus.Valid, imported.StatusAt(DateTimeOffset.UtcNow));
        Assert.Equal(imported.Thumbprint, Assert.Single(await manager.GetAllAsync(TestContext.Current.CancellationToken)).Thumbprint);
    }

    [Fact]
    public async Task Pkcs12_with_wrong_password_is_rejected()
    {
        await using var env = await TestEnvironment.CreateAsync();

        await Assert.ThrowsAsync<CertificatePasswordException>(() => env.Get<CertificateManager>().ImportAsync(TestPki.Pkcs12(Anna.Value, "richtig"), "falsch", cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<CertificatePasswordException>(() => env.Get<CertificateManager>().ImportAsync(TestPki.Pkcs12(Anna.Value, "richtig"), null, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Public_certificates_import_as_pem_and_der_and_ca_is_recognized()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var manager = env.Get<CertificateManager>();

        await manager.ImportAsync(TestPki.Pem(Bob.Value.Certificate), null, cancellationToken: TestContext.Current.CancellationToken);
        await manager.ImportAsync(TestPki.Ca.GetEncoded(), null, cancellationToken: TestContext.Current.CancellationToken);

        var all = await manager.GetAllAsync(TestContext.Current.CancellationToken);
        Assert.Contains(all, c => c.Covers("bob@example.com") && !c.HasPrivateKey && !c.IsAuthority);
        Assert.Contains(all, c => c.IsAuthority && c.SubjectName == "Neruna Test CA" && !c.CanSign);
        await Assert.ThrowsAsync<CertificatePasswordException>(() => manager.ImportAsync("kein zertifikat"u8.ToArray(), null, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Status_reflects_expiry()
    {
        var expired = TestPki.User("Alt", "alt@example.com", DateTime.UtcNow.AddYears(-3), DateTime.UtcNow.AddDays(-1));
        var expiring = TestPki.User("Bald", "bald@example.com", DateTime.UtcNow.AddYears(-1), DateTime.UtcNow.AddDays(10));
        var now = DateTimeOffset.UtcNow;

        Assert.Equal(CertificateStatus.Expired, CertificateParser.Describe(expired.Certificate, true, CertificateSource.Imported, now).StatusAt(now));
        Assert.Equal(CertificateStatus.ExpiringSoon, CertificateParser.Describe(expiring.Certificate, true, CertificateSource.Imported, now).StatusAt(now));
    }

    [Fact]
    public async Task Chosen_hash_and_cipher_are_used_and_still_readable()
    {
        await using var annaEnv = await TestEnvironment.CreateAsync();
        await using var bobEnv = await TestEnvironment.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        await annaEnv.Get<CertificateManager>().ImportAsync(TestPki.Pkcs12(Anna.Value, "a"), "a", cancellationToken: ct);
        await annaEnv.Get<CertificateManager>().ImportAsync(Bob.Value.Certificate.GetEncoded(), null, cancellationToken: ct);
        await bobEnv.Get<CertificateManager>().ImportAsync(TestPki.Pkcs12(Bob.Value, "b"), "b", cancellationToken: ct);
        await bobEnv.Get<CertificateManager>().ImportAsync(TestPki.Ca.GetEncoded(), null, cancellationToken: ct);
        await annaEnv.Get<Neruna.Core.ISettingsStore>().SetAsync(Neruna.Core.SettingKeys.SmimeDigest, "sha512", ct);
        await annaEnv.Get<Neruna.Core.ISettingsStore>().SetAsync(Neruna.Core.SettingKeys.SmimeCipher, "aes128", ct);

        // Signature: SHA-512 (announced as micalg).
        var signed = Message("Signiert", "Hallo Bob");
        await annaEnv.Get<SecureMimeService>().ProtectAsync(signed, sign: true, encrypt: false, ct);
        Assert.Equal("sha-512", Assert.IsType<MultipartSigned>(signed.Body).ContentType.Parameters["micalg"]);
        Assert.Equal(SignatureStatus.Valid, Assert.Single((await bobEnv.Get<SecureMimeService>().OpenAsync(Reparse(signed), ct)).Security.Signatures).Status);

        // Encryption: AES-128-CBC (OID in the CMS envelope).
        var encrypted = Message("Verschlüsselt", "Geheim");
        await annaEnv.Get<SecureMimeService>().ProtectAsync(encrypted, sign: false, encrypt: true, ct);
        using var content = new MemoryStream();
        await Assert.IsType<ApplicationPkcs7Mime>(encrypted.Body).Content!.DecodeToAsync(content, ct);
        Assert.Equal("2.16.840.1.101.3.4.1.2", new Org.BouncyCastle.Cms.CmsEnvelopedData(content.ToArray()).EncryptionAlgOid);
        var opened = await bobEnv.Get<SecureMimeService>().OpenAsync(Reparse(encrypted), ct);
        Assert.Null(opened.Security.DecryptionError);
        Assert.Contains("Geheim", MessageContent.From(opened.Readable).PlainText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Signed_and_encrypted_mail_round_trips_between_two_people()
    {
        await using var annaEnv = await TestEnvironment.CreateAsync();
        await using var bobEnv = await TestEnvironment.CreateAsync();
        var ct = TestContext.Current.CancellationToken;

        // Anna has her own key and Bob's public certificate; Bob has his key and trusts the CA.
        await annaEnv.Get<CertificateManager>().ImportAsync(TestPki.Pkcs12(Anna.Value, "a"), "a", cancellationToken: ct);
        await annaEnv.Get<CertificateManager>().ImportAsync(Bob.Value.Certificate.GetEncoded(), null, cancellationToken: ct);
        await bobEnv.Get<CertificateManager>().ImportAsync(TestPki.Pkcs12(Bob.Value, "b"), "b", cancellationToken: ct);
        await bobEnv.Get<CertificateManager>().ImportAsync(TestPki.Ca.GetEncoded(), null, cancellationToken: ct);

        var capabilities = await annaEnv.Get<SecureMimeService>().GetCapabilitiesAsync("anna@example.com", ["bob@example.com"], ct);
        Assert.True(capabilities.CanSign);
        Assert.Empty(capabilities.RecipientsWithoutCertificate);

        var message = Message("Vertraulich: Offerte", "Der Rabatt beträgt 12 %.");
        await annaEnv.Get<SecureMimeService>().ProtectAsync(message, sign: true, encrypt: true, ct);
        Assert.IsType<ApplicationPkcs7Mime>(message.Body);
        Assert.DoesNotContain("Rabatt", Serialize(message), StringComparison.Ordinal);

        var opened = await bobEnv.Get<SecureMimeService>().OpenAsync(Reparse(message), ct);

        Assert.True(opened.Security.WasEncrypted);
        Assert.Null(opened.Security.DecryptionError);
        var signature = Assert.Single(opened.Security.Signatures);
        Assert.Equal(SignatureStatus.Valid, signature.Status);
        Assert.True(signature.EmailMatchesSender);
        Assert.Equal("Anna Muster", signature.SignerName);
        Assert.Contains("12 %", MessageContent.From(opened.Readable).PlainText, StringComparison.Ordinal);

        // Bob now has Anna's certificate and could answer encrypted.
        Assert.Contains(await bobEnv.Get<CertificateManager>().GetAllAsync(ct), c => c.Covers("anna@example.com") && c.Source == CertificateSource.CollectedFromMail);

        // Anna can still read her own sent copy.
        var own = await annaEnv.Get<SecureMimeService>().OpenAsync(Reparse(message), ct);
        Assert.Null(own.Security.DecryptionError);
    }

    [Fact]
    public async Task Signature_from_unknown_ca_is_valid_but_untrusted_and_tampering_is_detected()
    {
        await using var annaEnv = await TestEnvironment.CreateAsync();
        await using var readerEnv = await TestEnvironment.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        await annaEnv.Get<CertificateManager>().ImportAsync(TestPki.Pkcs12(Anna.Value, "a"), "a", cancellationToken: ct);

        var message = Message("Signiert", "Original");
        await annaEnv.Get<SecureMimeService>().ProtectAsync(message, sign: true, encrypt: false, ct);
        Assert.IsType<MultipartSigned>(message.Body);

        var untrusted = await readerEnv.Get<SecureMimeService>().OpenAsync(Reparse(message), ct);
        Assert.Equal(SignatureStatus.ValidUntrusted, Assert.Single(untrusted.Security.Signatures).Status);

        var tampered = Serialize(message).Replace("Original", "Gefälscht", StringComparison.Ordinal);
        var opened = await readerEnv.Get<SecureMimeService>().OpenAsync(MimeMessage.Load(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(tampered)), ct), ct);
        Assert.Equal(SignatureStatus.Invalid, Assert.Single(opened.Security.Signatures).Status);
    }

    [Fact]
    public async Task Missing_recipient_certificate_and_missing_own_key_are_reported()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        var service = env.Get<SecureMimeService>();

        var noKey = await Assert.ThrowsAsync<SecureMimeException>(() => service.ProtectAsync(Message("x", "y"), sign: true, encrypt: false, ct));
        Assert.Contains("anna@example.com", noKey.Message, StringComparison.Ordinal);

        await env.Get<CertificateManager>().ImportAsync(TestPki.Pkcs12(Anna.Value, "a"), "a", cancellationToken: ct);
        var missing = await Assert.ThrowsAsync<SecureMimeException>(() => service.ProtectAsync(Message("x", "y"), sign: false, encrypt: true, ct));
        Assert.Contains("bob@example.com", missing.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Message_encrypted_for_someone_else_reports_decryption_error()
    {
        await using var annaEnv = await TestEnvironment.CreateAsync();
        await using var strangerEnv = await TestEnvironment.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        await annaEnv.Get<CertificateManager>().ImportAsync(TestPki.Pkcs12(Anna.Value, "a"), "a", cancellationToken: ct);
        await annaEnv.Get<CertificateManager>().ImportAsync(Bob.Value.Certificate.GetEncoded(), null, cancellationToken: ct);
        var message = Message("Nur für Bob", "geheim");
        await annaEnv.Get<SecureMimeService>().ProtectAsync(message, sign: false, encrypt: true, ct);

        var opened = await strangerEnv.Get<SecureMimeService>().OpenAsync(Reparse(message), ct);

        Assert.True(opened.Security.WasEncrypted);
        Assert.NotNull(opened.Security.DecryptionError);
    }

    [Fact]
    public async Task Removing_a_certificate_deletes_its_key()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        var manager = env.Get<CertificateManager>();
        var info = Assert.Single(await manager.ImportAsync(TestPki.Pkcs12(Anna.Value, "a"), "a", cancellationToken: ct));

        await manager.RemoveAsync(info.Thumbprint, ct);

        Assert.Empty(await manager.GetAllAsync(ct));
        Assert.Empty((await manager.LoadMaterialAsync(ct)).PrivateKeys);
    }

    private static MimeMessage Message(string subject, string text)
    {
        var message = new MimeMessage { Subject = subject, Body = new TextPart("plain") { Text = text } };
        message.From.Add(new MailboxAddress("Anna Muster", "anna@example.com"));
        message.To.Add(new MailboxAddress("Bob Brunner", "bob@example.com"));
        return message;
    }

    private static string Serialize(MimeMessage message)
    {
        using var stream = new MemoryStream();
        message.WriteTo(stream);
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static MimeMessage Reparse(MimeMessage message) => MimeMessage.Load(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(Serialize(message))));
}
