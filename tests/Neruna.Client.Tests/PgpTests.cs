using MimeKit;
using MimeKit.Cryptography;
using Neruna.Core.Mail;
using Neruna.Core.Security;
using Microsoft.Extensions.DependencyInjection;
using Neruna.Core.Contacts;
using Org.BouncyCastle.Bcpg.OpenPgp;

namespace Neruna.Client.Tests;

public class PgpTests
{
    // Key generation is quick (Curve25519), but once per test run is enough.
    private static readonly Lazy<PgpSecretKeyRing> Anna = new(() => PgpKeyGenerator.Generate("Anna Muster", "anna@example.com", "anna-pw", DateTime.UtcNow.AddDays(-1)));
    private static readonly Lazy<PgpSecretKeyRing> Bob = new(() => PgpKeyGenerator.Generate("Bob Brunner", "bob@example.com", "bob-pw", DateTime.UtcNow.AddDays(-1)));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_key_made_here_is_ed25519_with_a_curve25519_subkey_like_other_programs_make_them()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var manager = env.Get<PgpKeyManager>();

        var key = await manager.GenerateAsync("Anna Muster", "anna@example.com", Ct);

        Assert.Equal("Ed25519", key.Algorithm);
        Assert.Equal(["anna@example.com"], key.EmailAddresses);
        Assert.Equal("Anna Muster", key.Name);
        Assert.True(key.HasSecretKey && key.CanSign && key.CanEncrypt && key.IsTrusted);
        Assert.Equal(40, key.Fingerprint.Length);
        Assert.InRange((key.Expires!.Value - key.Created).TotalDays, 3 * 365 - 1, 3 * 365 + 1);
        var keyring = await manager.LoadKeyringAsync(Ct);
        Assert.Single(keyring.Secret);
        Assert.Contains("BEGIN PGP PUBLIC KEY BLOCK", await manager.ExportPublicAsync(key.Fingerprint, Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_own_key_imports_only_with_its_passphrase_and_a_backup_opens_with_the_new_one()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var manager = env.Get<PgpKeyManager>();
        var file = Armored(Anna.Value);

        await Assert.ThrowsAsync<CertificatePasswordException>(() => manager.ImportAsync(file, null, cancellationToken: Ct));
        await Assert.ThrowsAsync<CertificatePasswordException>(() => manager.ImportAsync(file, "falsch", cancellationToken: Ct));
        await Assert.ThrowsAsync<FormatException>(() => manager.ImportAsync("kein schlüssel"u8.ToArray(), null, cancellationToken: Ct));
        var imported = Assert.Single(await manager.ImportAsync(file, "anna-pw", cancellationToken: Ct));
        Assert.True(imported.HasSecretKey);

        var backup = await manager.ExportSecretAsync(imported.Fingerprint, "sicherung", Ct);
        var (_, secret) = PgpKeyParser.Read(System.Text.Encoding.ASCII.GetBytes(backup));
        Assert.True(PgpKeyParser.Opens(Assert.Single(secret), "sicherung"));
        Assert.False(PgpKeyParser.Opens(secret[0], "anna-pw"));
    }

    [Fact]
    public async Task A_signed_mail_is_valid_with_a_confirmed_key_and_unconfirmed_with_a_merely_known_one()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var manager = env.Get<PgpKeyManager>();
        var key = Assert.Single(await manager.ImportAsync(PublicArmored(Anna.Value), null, cancellationToken: Ct));
        Assert.False(key.IsTrusted);
        var message = await SignedByAnnaAsync("Hallo Bob");

        var opened = await env.Get<SecureMimeService>().OpenAsync(message, Ct);
        var signature = Assert.Single(opened.Security.Signatures);
        Assert.Equal(SignatureStatus.ValidUntrusted, signature.Status);
        Assert.True(signature.IsPgp && signature.EmailMatchesSender);
        Assert.Contains(key.FingerprintText, signature.Detail, StringComparison.Ordinal);
        Assert.Contains("Hallo Bob", MessageContent.From(opened.Readable).PlainText, StringComparison.Ordinal);

        await manager.SetVerifiedAsync(key.Fingerprint, true, Ct);
        signature = Assert.Single((await env.Get<SecureMimeService>().OpenAsync(message, Ct)).Security.Signatures);
        Assert.Equal(SignatureStatus.Valid, signature.Status);
        Assert.Equal(MessageSecurityLevel.Good, MessageSecurityText.Describe(new MessageSecurityInfo(false, null, [signature])).Level);
    }

    [Fact]
    public async Task The_key_of_a_signed_mail_is_confirmed_right_from_the_mail()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var manager = env.Get<PgpKeyManager>();
        var service = env.Get<SecureMimeService>();
        var key = Assert.Single(await manager.ImportAsync(PublicArmored(Anna.Value), null, cancellationToken: Ct));
        var message = await SignedByAnnaAsync("Hallo Bob");

        var security = (await service.OpenAsync(message, Ct)).Security;
        Assert.Equal(key.Fingerprint, security.PgpKeyToConfirm);

        await service.ConfirmPgpKeyAsync(security.PgpKeyToConfirm!, Ct);
        var confirmed = security.WithPgpKeyConfirmed(key.Fingerprint);
        Assert.Null(confirmed.PgpKeyToConfirm);
        Assert.Equal(MessageSecurityLevel.Good, MessageSecurityText.Describe(confirmed).Level);
        Assert.True(Assert.Single(await manager.GetAllAsync(Ct)).IsTrusted);
        Assert.Equal(SignatureStatus.Valid, Assert.Single((await service.OpenAsync(message, Ct)).Security.Signatures).Status);

        // Nothing to confirm when the key is not the sender's or the mail was changed.
        var other = security with { Signatures = [security.Signatures[0] with { EmailMatchesSender = false }] };
        Assert.Null(other.PgpKeyToConfirm);
        Assert.Null((security with { Signatures = [security.Signatures[0] with { Status = SignatureStatus.Invalid }] }).PgpKeyToConfirm);
    }

    [Fact]
    public async Task A_changed_signed_mail_is_invalid_and_one_without_a_known_key_cannot_be_checked()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var service = env.Get<SecureMimeService>();
        var message = await SignedByAnnaAsync("Hallo Bob");

        Assert.Equal(SignatureStatus.Unverifiable, Assert.Single((await service.OpenAsync(message, Ct)).Security.Signatures).Status);

        await env.Get<PgpKeyManager>().ImportAsync(PublicArmored(Anna.Value), null, verified: true, cancellationToken: Ct);
        var raw = Serialize(message).Replace("Hallo Bob", "Hallo Bub", StringComparison.Ordinal);
        var changed = MimeMessage.Load(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(raw)), Ct);
        Assert.Equal(SignatureStatus.Invalid, Assert.Single((await service.OpenAsync(changed, Ct)).Security.Signatures).Status);
    }

    [Fact]
    public async Task The_senders_key_from_the_autocrypt_header_is_kept_as_known_and_checks_the_signature()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var message = await SignedByAnnaAsync("Hallo Bob");
        message.Headers.Add(Autocrypt.HeaderName, Autocrypt.Format("anna@example.com", PgpKeyParser.PublicOf(Anna.Value), preferEncrypt: true));
        message = MimeMessage.Load(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(Serialize(message))), Ct);

        var signature = Assert.Single((await env.Get<SecureMimeService>().OpenAsync(message, Ct)).Security.Signatures);

        Assert.Equal(SignatureStatus.ValidUntrusted, signature.Status);
        var key = Assert.Single(await env.Get<PgpKeyManager>().GetAllAsync(Ct));
        Assert.Equal(PgpKeySource.Autocrypt, key.Source);
        Assert.False(key.IsTrusted);
    }

    [Fact]
    public async Task An_encrypted_and_signed_mail_opens_with_the_own_key_and_tells_why_not_without_it()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var service = env.Get<SecureMimeService>();
        var message = await EncryptedForBobAsync("Geheim");

        var locked = await service.OpenAsync(message, Ct);
        Assert.True(locked.Security.WasEncrypted && locked.Security.IsPgp);
        Assert.NotNull(locked.Security.DecryptionError);

        var manager = env.Get<PgpKeyManager>();
        await manager.ImportAsync(Armored(Bob.Value), "bob-pw", cancellationToken: Ct);
        await manager.ImportAsync(PublicArmored(Anna.Value), null, verified: true, cancellationToken: Ct);
        var opened = await service.OpenAsync(message, Ct);

        Assert.Null(opened.Security.DecryptionError);
        Assert.True(opened.Security.WasEncrypted);
        Assert.Contains("Geheim", MessageContent.From(opened.Readable).PlainText, StringComparison.Ordinal);
        var signature = Assert.Single(opened.Security.Signatures);
        Assert.Equal(SignatureStatus.Valid, signature.Status);
        Assert.Contains("PGP", MessageSecurityText.Describe(opened.Security).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Autocrypt_headers_with_unknown_attributes_or_without_key_are_ignored()
    {
        var key = Convert.ToBase64String(PgpKeyParser.PublicOf(Anna.Value).GetEncoded());
        Assert.NotNull(Autocrypt.Parse($"addr=anna@example.com; _extra=1; keydata={key[..40]}\r\n {key[40..]}"));
        Assert.Null(Autocrypt.Parse($"addr=anna@example.com; critical=1; keydata={key}"));
        Assert.Null(Autocrypt.Parse("addr=anna@example.com"));
    }

    [Fact]
    public async Task An_inline_clear_signed_text_shows_without_the_armor_and_its_signature_is_checked()
    {
        await using var env = await TestEnvironment.CreateAsync();
        await env.Get<PgpKeyManager>().ImportAsync(PublicArmored(Anna.Value), null, verified: true, cancellationToken: Ct);
        var block = ClearSigned("Hallo Bob,\n- eine Zeile mit Strich \nGrüsse");
        var message = Message("Vorwort\n\n" + block + "\nNachwort");

        var opened = await env.Get<SecureMimeService>().OpenAsync(message, Ct);

        var text = MessageContent.From(opened.Readable).PlainText!;
        Assert.Contains("- eine Zeile mit Strich", text, StringComparison.Ordinal);
        Assert.Contains("Grüsse", text, StringComparison.Ordinal);
        Assert.Contains("Vorwort", text, StringComparison.Ordinal);
        Assert.DoesNotContain("BEGIN PGP", text, StringComparison.Ordinal);
        Assert.Equal(SignatureStatus.Valid, Assert.Single(opened.Security.Signatures).Status);

        var changed = Message(block.Replace("Hallo Bob", "Hallo Bub", StringComparison.Ordinal));
        Assert.Equal(SignatureStatus.Invalid, Assert.Single((await env.Get<SecureMimeService>().OpenAsync(changed, Ct)).Security.Signatures).Status);
    }

    [Fact]
    public async Task An_inline_encrypted_block_is_decrypted_in_place()
    {
        await using var env = await TestEnvironment.CreateAsync();
        await env.Get<PgpKeyManager>().ImportAsync(Armored(Bob.Value), "bob-pw", cancellationToken: Ct);
        await env.Get<PgpKeyManager>().ImportAsync(PublicArmored(Anna.Value), null, cancellationToken: Ct);
        string armored;
        using (var context = Sender())
        {
            var part = await context.SignAndEncryptAsync(new MailboxAddress("Anna Muster", "anna@example.com"), DigestAlgorithm.Sha256,
                [new MailboxAddress("Bob Brunner", "bob@example.com")], new MemoryStream(System.Text.Encoding.UTF8.GetBytes("Geheime Grüsse")), Ct);
            using var content = new MemoryStream();
            await part.Content!.DecodeToAsync(content, Ct);
            armored = System.Text.Encoding.ASCII.GetString(content.ToArray());
        }

        var opened = await env.Get<SecureMimeService>().OpenAsync(Message("Hier die Nachricht:\n" + armored), Ct);

        Assert.Null(opened.Security.DecryptionError);
        Assert.True(opened.Security.WasEncrypted && opened.Security.IsPgp);
        var text = MessageContent.From(opened.Readable).PlainText!;
        Assert.Contains("Geheime Grüsse", text, StringComparison.Ordinal);
        Assert.Contains("Hier die Nachricht:", text, StringComparison.Ordinal);
        Assert.Equal(SignatureStatus.ValidUntrusted, Assert.Single(opened.Security.Signatures).Status);
    }

    [Fact]
    public void Pgp_keys_in_vcards_are_read_written_and_removed_in_both_versions_without_touching_certificates()
    {
        var key = PgpKeyParser.PublicOf(Anna.Value).GetEncoded();
        const string card4 = "BEGIN:VCARD\r\nVERSION:4.0\r\nFN:Anna\r\nEMAIL:anna@example.com\r\nEND:VCARD\r\n";
        const string card3 = "BEGIN:VCARD\r\nVERSION:3.0\r\nFN:Anna\r\nEMAIL:anna@example.com\r\nEND:VCARD\r\n";

        var with4 = ContactPgpKeys.Add(card4, key);
        var with3 = ContactPgpKeys.Add(card3, key);

        Assert.Contains("application/pgp-keys", with4, StringComparison.Ordinal);
        Assert.Contains("TYPE=PGP", with3, StringComparison.Ordinal);
        Assert.Equal(key, Assert.Single(ContactPgpKeys.Read(with4)));
        Assert.Equal(key, Assert.Single(ContactPgpKeys.Read(with3)));
        Assert.Empty(ContactCertificates.Read(with4));
        Assert.Empty(ContactCertificates.Read(with3));
        Assert.Equal(with4, ContactPgpKeys.Add(with4, key));
        Assert.Empty(ContactPgpKeys.Read(ContactPgpKeys.Remove(with4, key)));
        Assert.Empty(ContactPgpKeys.Read(ContactPgpKeys.Remove(with3, key)));
    }

    [Fact]
    public async Task A_key_kept_with_a_contact_counts_as_confirmed_and_confirming_a_known_key_puts_it_there()
    {
        var contacts = new FakeContactPgpKeys("anna@example.com");
        await using var env = await TestEnvironment.CreateAsync(s => s.AddSingleton<IContactPgpKeys>(contacts));
        var manager = env.Get<PgpKeyManager>();
        var known = await manager.RememberAsync(PgpKeyParser.PublicOf(Anna.Value), PgpKeySource.Autocrypt, Ct);
        Assert.False(known!.IsTrusted);

        Assert.True(await manager.ConfirmAsync(known.Fingerprint, Ct));
        Assert.Single(contacts.Stored);
        await manager.RemoveAsync(known.Fingerprint, Ct);
        Assert.Empty(contacts.Stored);

        contacts.Stored.Add((PgpKeyParser.PublicOf(Anna.Value).GetEncoded(), ["anna@example.com"]));
        var fromContact = Assert.Single(await manager.GetAllAsync(Ct));
        Assert.Equal(PgpKeySource.Contact, fromContact.Source);
        Assert.True(fromContact.IsTrusted);
        var signature = Assert.Single((await env.Get<SecureMimeService>().OpenAsync(await SignedByAnnaAsync("Hallo"), Ct)).Security.Signatures);
        Assert.Equal(SignatureStatus.Valid, signature.Status);
    }

    [Fact]
    public void Wkd_addresses_follow_the_standard()
    {
        // The example of the WKD specification (draft-koch-openpgp-webkey-service).
        var urls = PgpKeyLookup.WkdUrls("Joe.Doe@Example.ORG");

        Assert.Equal("https://openpgpkey.example.org/.well-known/openpgpkey/example.org/hu/iy9q119eutrkn8s1mk4r39qejnbu3n5q?l=Joe.Doe", urls[0].AbsoluteUri);
        Assert.Equal("https://example.org/.well-known/openpgpkey/hu/iy9q119eutrkn8s1mk4r39qejnbu3n5q?l=Joe.Doe", urls[1].AbsoluteUri);
    }

    [Fact]
    public async Task A_recipients_key_is_looked_up_on_their_domain_first_then_on_the_key_server()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var lookup = env.Get<PgpKeyLookup>();
        var armored = PgpKeyParser.Armor(PgpKeyParser.PublicOf(Bob.Value));

        env.Http.Responses["https://keys.openpgp.org/vks/v1/by-email/bob%40example.com"] = (System.Net.HttpStatusCode.OK, armored);
        var found = Assert.Single(await lookup.FindAsync("bob@example.com", Ct));
        Assert.Equal(PgpKeyParser.FingerprintOf(PgpKeyParser.PublicOf(Bob.Value)), PgpKeyParser.FingerprintOf(found));
        Assert.Equal(3, env.Http.Requested.Count);
        Assert.StartsWith("https://openpgpkey.example.com/", env.Http.Requested[0], StringComparison.Ordinal);

        // A key that does not name the address is not taken.
        Assert.Empty(await lookup.FindAsync("carla@example.com", Ct));
        env.Http.Responses[PgpKeyLookup.WkdUrls("carla@example.com")[1].AbsoluteUri] = (System.Net.HttpStatusCode.OK, armored);
        Assert.Empty(await lookup.FindAsync("carla@example.com", Ct));
    }

    private sealed class FakeContactPgpKeys(params string[] addresses) : IContactPgpKeys
    {
        public HashSet<string> Addresses { get; } = new(addresses, StringComparer.OrdinalIgnoreCase);

        public List<(byte[] Key, IReadOnlyList<string> Emails)> Stored { get; } = [];

        public Task<IReadOnlyList<(byte[] Key, IReadOnlyList<string> Emails)>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<(byte[], IReadOnlyList<string>)>>([.. Stored]);

        public Task<bool> AddAsync(byte[] key, IReadOnlyCollection<string> emails, CancellationToken cancellationToken = default)
        {
            var matching = emails.Where(Addresses.Contains).ToList();
            if (matching.Count > 0 && !Stored.Any(s => s.Key.AsSpan().SequenceEqual(key)))
            {
                Stored.Add((key, matching));
            }

            return Task.FromResult(matching.Count > 0);
        }

        public Task RemoveAsync(byte[] key, CancellationToken cancellationToken = default)
        {
            Stored.RemoveAll(s => s.Key.AsSpan().SequenceEqual(key));
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Sent_mail_is_signed_with_pgp_carries_the_own_key_and_checks_out_at_the_recipient()
    {
        await using var anna = await TestEnvironment.CreateAsync();
        await anna.Get<PgpKeyManager>().ImportAsync(Armored(Anna.Value), "anna-pw", cancellationToken: Ct);
        var message = Message("Hallo Bob");

        await anna.Get<SecureMimeService>().ProtectAsync(message, sign: true, encrypt: false, Ct);

        var signed = Assert.IsType<MultipartSigned>(message.Body);
        Assert.Equal("application/pgp-signature", signed.ContentType.Parameters["protocol"]);
        Assert.NotNull(Autocrypt.Read(message));

        await using var bob = await TestEnvironment.CreateAsync();
        var received = MimeMessage.Load(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(Serialize(message))), Ct);
        var signature = Assert.Single((await bob.Get<SecureMimeService>().OpenAsync(received, Ct)).Security.Signatures);
        Assert.Equal(SignatureStatus.ValidUntrusted, signature.Status);
        Assert.Equal(PgpKeySource.Autocrypt, Assert.Single(await bob.Get<PgpKeyManager>().GetAllAsync(Ct)).Source);
    }

    [Fact]
    public async Task Sent_mail_is_encrypted_with_pgp_to_every_recipient_and_the_sender_or_not_at_all()
    {
        await using var anna = await TestEnvironment.CreateAsync();
        var keys = anna.Get<PgpKeyManager>();
        await keys.ImportAsync(Armored(Anna.Value), "anna-pw", cancellationToken: Ct);
        var service = anna.Get<SecureMimeService>();

        var tooEarly = Message("Geheim");
        var error = await Assert.ThrowsAsync<SecureMimeException>(() => service.ProtectAsync(tooEarly, sign: true, encrypt: true, Ct));
        Assert.Contains("bob@example.com", error.Message, StringComparison.Ordinal);

        await keys.ImportAsync(PublicArmored(Bob.Value), null, cancellationToken: Ct);
        var capabilities = await service.GetCapabilitiesAsync("anna@example.com", ["bob@example.com", "anna@example.com"], Ct);
        Assert.Equal(SecureMethod.Pgp, capabilities.Method);
        Assert.True(capabilities.CanSign);
        Assert.Empty(capabilities.RecipientsWithoutCertificate);

        var message = Message("Geheim");
        await service.ProtectAsync(message, sign: true, encrypt: true, Ct);
        Assert.IsType<MultipartEncrypted>(message.Body);
        Assert.DoesNotContain("Geheim", Serialize(message), StringComparison.Ordinal);

        // The copy in "Gesendet" opens for the sender, the mail for Bob.
        var own = await service.OpenAsync(Reloaded(message), Ct);
        Assert.Contains("Geheim", MessageContent.From(own.Readable).PlainText, StringComparison.Ordinal);
        await using var bob = await TestEnvironment.CreateAsync();
        await bob.Get<PgpKeyManager>().ImportAsync(Armored(Bob.Value), "bob-pw", cancellationToken: Ct);
        var opened = await bob.Get<SecureMimeService>().OpenAsync(Reloaded(message), Ct);
        Assert.Null(opened.Security.DecryptionError);
        Assert.Contains("Geheim", MessageContent.From(opened.Readable).PlainText, StringComparison.Ordinal);
        Assert.Single(opened.Security.Signatures);
    }

    [Fact]
    public void The_method_is_the_one_all_recipients_can_be_encrypted_with_then_signing_then_the_preferred_one()
    {
        SecureMimeCapabilities Smime(bool sign, params string[] missing) => new(sign, missing, SecureMethod.SMime);
        SecureMimeCapabilities Pgp(bool sign, params string[] missing) => new(sign, missing, SecureMethod.Pgp);

        Assert.Equal(SecureMethod.Pgp, SecureMimeService.Choose(Smime(true, "b@x.ch"), Pgp(false), true, SecureMethod.SMime).Method);
        Assert.Equal(SecureMethod.SMime, SecureMimeService.Choose(Smime(true), Pgp(true, "b@x.ch"), true, SecureMethod.Pgp).Method);
        Assert.Equal(SecureMethod.Pgp, SecureMimeService.Choose(Smime(false), Pgp(true), false, SecureMethod.SMime).Method);
        Assert.Equal(SecureMethod.SMime, SecureMimeService.Choose(Smime(true, "a@x.ch", "b@x.ch"), Pgp(true, "b@x.ch", "c@x.ch", "d@x.ch"), true, SecureMethod.Pgp).Method);
        Assert.Equal(SecureMethod.Pgp, SecureMimeService.Choose(Smime(true), Pgp(true), true, SecureMethod.Pgp).Method);
        Assert.Equal(SecureMethod.SMime, SecureMimeService.Choose(Smime(false), Pgp(false), false, SecureMethod.SMime).Method);
    }

    [Fact]
    public async Task Keys_of_recipients_found_online_are_kept_as_known_and_make_encryption_possible()
    {
        await using var env = await TestEnvironment.CreateAsync();
        await env.Get<PgpKeyManager>().ImportAsync(Armored(Anna.Value), "anna-pw", cancellationToken: Ct);
        env.Http.Responses["https://keys.openpgp.org/vks/v1/by-email/bob%40example.com"] = (System.Net.HttpStatusCode.OK, PgpKeyParser.Armor(PgpKeyParser.PublicOf(Bob.Value)));
        var service = env.Get<SecureMimeService>();

        Assert.Equal(["bob@example.com"], await service.FindPgpKeysAsync(["bob@example.com", "anna@example.com"], Ct));

        var key = Assert.Single(await env.Get<PgpKeyManager>().GetAllAsync(Ct), k => !k.HasSecretKey);
        Assert.Equal(PgpKeySource.KeyServer, key.Source);
        Assert.False(key.IsTrusted);
        Assert.Empty((await service.GetCapabilitiesAsync("anna@example.com", ["bob@example.com", "anna@example.com"], SecureMethod.Pgp, Ct)).RecipientsWithoutCertificate);

        await env.Get<Neruna.Core.ISettingsStore>().SetAsync(Neruna.Core.SettingKeys.PgpKeyLookup, "false", Ct);
        Assert.Empty(await service.FindPgpKeysAsync(["carla@example.com"], Ct));
        Assert.DoesNotContain(env.Http.Requested, r => r.Contains("carla", StringComparison.Ordinal));
    }

    private static MimeMessage Reloaded(MimeMessage message) => MimeMessage.Load(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(Serialize(message))), Ct);

    [Fact]
    public async Task Own_pgp_keys_move_to_another_computer_with_the_settings_backup_and_stay_usable()
    {
        await using var oldComputer = await TestEnvironment.CreateAsync();
        await using var newComputer = await TestEnvironment.CreateAsync();
        var own = await oldComputer.Get<PgpKeyManager>().GenerateAsync("Anna Muster", "anna@example.com", Ct);
        var bob = Assert.Single(await oldComputer.Get<PgpKeyManager>().ImportAsync(PublicArmored(Bob.Value), null, verified: true, cancellationToken: Ct));

        using var file = new MemoryStream();
        await Backup(oldComputer).ExportToFileAsync(file, "ein sicheres Passwort", Ct);
        file.Position = 0;
        var plan = await Backup(newComputer).PrepareRestoreFromFileAsync(file, "ein sicheres Passwort", Ct);
        Assert.Contains(plan.Changes, c => c.Area == "PGP-Schlüssel" && c.Name == "anna@example.com");
        await Backup(newComputer).RestoreAsync(plan, Ct);

        var keys = await newComputer.Get<PgpKeyManager>().GetAllAsync(Ct);
        Assert.Contains(keys, k => k.Fingerprint == own.Fingerprint && k.HasSecretKey);
        Assert.Contains(keys, k => k.Fingerprint == bob.Fingerprint && k.IsTrusted);
        var message = Message("Hallo");
        await newComputer.Get<SecureMimeService>().ProtectAsync(message, sign: true, encrypt: true, Ct);
        Assert.IsType<MultipartEncrypted>(message.Body);
    }

    private static Neruna.Core.Cloud.SettingsBackupService Backup(TestEnvironment env) => new(
        new Neruna.Core.Cloud.CloudController(new HttpClient(env.Http), env.Get<Neruna.Core.ISettingsStore>(), env.Get<ICredentialStore>(), Microsoft.Extensions.Logging.Abstractions.NullLogger<Neruna.Core.Cloud.CloudController>.Instance),
        env.Accounts, env.Get<ICredentialStore>(), env.Get<Neruna.Core.ISettingsStore>(), env.Get<ISignatureStore>(), env.Get<ITextTemplateStore>(),
        env.Get<CertificateManager>(), TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<Neruna.Core.Cloud.SettingsBackupService>.Instance, env.Get<PgpKeyManager>());

    // As programs write it: clear text with dash escaping, then the armored signature over the canonical text.
    private static string ClearSigned(string text)
    {
        var secret = Anna.Value.GetSecretKey();
        var generator = new PgpSignatureGenerator(secret.PublicKey.Algorithm, Org.BouncyCastle.Bcpg.HashAlgorithmTag.Sha256);
        generator.InitSign(PgpSignature.CanonicalTextDocument, secret.ExtractPrivateKey("anna-pw".ToCharArray()));
        using var output = new MemoryStream();
        using (var armored = new Org.BouncyCastle.Bcpg.ArmoredOutputStream(output))
        {
            armored.BeginClearText(Org.BouncyCastle.Bcpg.HashAlgorithmTag.Sha256);
            var lines = text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var line = System.Text.Encoding.UTF8.GetBytes(lines[i]);
                generator.Update(System.Text.Encoding.UTF8.GetBytes(lines[i].TrimEnd(' ', '\t')));
                if (i < lines.Length - 1)
                {
                    generator.Update("\r\n"u8.ToArray());
                }

                armored.Write(line);
                armored.Write("\r\n"u8);
            }

            armored.EndClearText();
            generator.Generate().Encode(new Org.BouncyCastle.Bcpg.BcpgOutputStream(armored));
        }

        return System.Text.Encoding.UTF8.GetString(output.ToArray());
    }

    private static async Task<MimeMessage> SignedByAnnaAsync(string text)
    {
        using var context = Sender();
        var message = Message(text);
        message.Body = await MultipartSigned.CreateAsync(context, message.From.Mailboxes.First(), DigestAlgorithm.Sha256, message.Body!, Ct);
        return message;
    }

    private static async Task<MimeMessage> EncryptedForBobAsync(string text)
    {
        using var context = Sender();
        var message = Message(text);
        message.Body = await MultipartEncrypted.SignAndEncryptAsync(context, message.From.Mailboxes.First(), DigestAlgorithm.Sha256, message.To.Mailboxes, message.Body!, Ct);
        return message;
    }

    // Anna's side: her own key to sign, Bob's public key to encrypt to.
    private static NerunaOpenPgpContext Sender()
    {
        var anna = (Anna.Value, "anna-pw", PgpKeyParser.Describe(PgpKeyParser.PublicOf(Anna.Value), true, PgpKeySource.Generated, true, DateTimeOffset.UtcNow));
        var bob = PgpKeyParser.PublicOf(Bob.Value);
        return new NerunaOpenPgpContext(new PgpKeyring(
            [(PgpKeyParser.PublicOf(Anna.Value), anna.Item3), (bob, PgpKeyParser.Describe(bob, false, PgpKeySource.Imported, true, DateTimeOffset.UtcNow))],
            [anna]), DateTimeOffset.UtcNow);
    }

    private static MimeMessage Message(string text)
    {
        var message = new MimeMessage { Subject = "PGP", Body = new TextPart("plain") { Text = text } };
        message.From.Add(new MailboxAddress("Anna Muster", "anna@example.com"));
        message.To.Add(new MailboxAddress("Bob Brunner", "bob@example.com"));
        return message;
    }

    private static byte[] Armored(PgpSecretKeyRing ring) => System.Text.Encoding.ASCII.GetBytes(PgpKeyParser.Armor(ring));

    private static byte[] PublicArmored(PgpSecretKeyRing ring) => System.Text.Encoding.ASCII.GetBytes(PgpKeyParser.Armor(PgpKeyParser.PublicOf(ring)));

    private static string Serialize(MimeMessage message)
    {
        using var stream = new MemoryStream();
        message.WriteTo(stream);
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}
