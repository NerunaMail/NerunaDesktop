using Microsoft.Extensions.Logging;
using MimeKit;
using MimeKit.Cryptography;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Core.Security;

/// <summary>OpenPGP (PGP/MIME) next to S/MIME: the same verdicts, keys from <see cref="PgpKeyManager"/>.</summary>
public sealed partial class SecureMimeService
{
    private static bool IsPgpSigned(MultipartSigned signed) =>
        signed.ContentType.Parameters["protocol"]?.Trim().ToLowerInvariant() is "application/pgp-signature";

    private async Task<NerunaOpenPgpContext> CreatePgpContextAsync(CancellationToken cancellationToken) =>
        new(await pgp!.LoadKeyringAsync(cancellationToken), clock.GetUtcNow());

    private async Task<IReadOnlyList<SignatureInfo>> VerifyPgpAsync(MultipartSigned signed, NerunaOpenPgpContext context, string? sender, CancellationToken cancellationToken)
    {
        try
        {
            return await EvaluatePgpAsync(await signed.VerifyAsync(context, cancellationToken), context, sender, cancellationToken);
        }
        catch (Exception ex) when (ex is FormatException or Org.BouncyCastle.Bcpg.OpenPgp.PgpException or IOException or InvalidOperationException or NotSupportedException)
        {
            logger.LogInformation(ex, "PGP signature could not be read");
            return [new SignatureInfo(T("Unbekannt"), [], null, SignatureStatus.Invalid, false, null, ex.Message, IsPgp: true)];
        }
    }

    private Task<IReadOnlyList<SignatureInfo>> EvaluatePgpAsync(DigitalSignatureCollection signatures, NerunaOpenPgpContext context, string? sender, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = new List<SignatureInfo>();
        foreach (var signature in signatures)
        {
            var signedAt = signature.CreationDate == default ? (DateTimeOffset?)null : new DateTimeOffset(DateTime.SpecifyKind(signature.CreationDate, DateTimeKind.Utc));
            var keyId = (signature.SignerCertificate as OpenPgpDigitalCertificate)?.PublicKey.KeyId;
            result.Add(DescribePgp(keyId, context, sender, signedAt, () =>
            {
                try
                {
                    return (signature.Verify(), null);
                }
                catch (DigitalSignatureVerifyException ex)
                {
                    return (false, ex.Message);
                }
            }));
        }

        return Task.FromResult<IReadOnlyList<SignatureInfo>>(result);
    }

    // One PGP signature in words: unknown key, changed content, or intact – trusted or merely known.
    private SignatureInfo DescribePgp(long? keyId, NerunaOpenPgpContext context, string? sender, DateTimeOffset? signedAt, Func<(bool Intact, string? Error)> verify)
    {
        if (keyId is not { } id || context.InfoFor(id) is not { } info)
        {
            return new SignatureInfo(T("Unbekannt"), [], null, SignatureStatus.Unverifiable, false, signedAt,
                T("Der öffentliche Schlüssel des Absenders ist hier nicht bekannt. Er kann unter Einstellungen → Zertifikate importiert werden."), IsPgp: true);
        }

        var (intact, detail) = verify();
        var status = intact ? info.IsTrusted ? SignatureStatus.Valid : SignatureStatus.ValidUntrusted : SignatureStatus.Invalid;
        var matches = sender is not null && info.Covers(sender);
        if (status == SignatureStatus.ValidUntrusted)
        {
            var otherTrusted = sender is not null && context.Keyring.Public.Any(p => p.Info.Covers(sender) && p.Info.IsTrusted && p.Info.Fingerprint != info.Fingerprint);
            detail = (otherTrusted ? T("Achtung: Für diese Adresse ist ein anderer, bestätigter Schlüssel bekannt. ") : string.Empty)
                + F("Die Nachricht ist unverändert. Um dem Schlüssel zu vertrauen, den Fingerabdruck {0} mit dem Absender vergleichen (z. B. am Telefon) und bestätigen.", info.FingerprintText);
        }

        logger.LogInformation("PGP signature by key {Fingerprint}: {Status}", info.Fingerprint, status);
        return new SignatureInfo(info.Name, info.EmailAddresses, info.FingerprintText, status, matches, signedAt, detail, IsPgp: true);
    }

    /// <summary>Opens inline PGP blocks in the text parts: decrypted or with their signature checked, the rest of the text kept.</summary>
    /// <returns>Whether something was encrypted, and why it could not be opened (null: fine).</returns>
    private async Task<(MimeEntity Entity, bool Encrypted, string? Error)> OpenInlinePgpAsync(MimeEntity entity, NerunaOpenPgpContext context, string? sender,
        List<SignatureInfo> signatures, CancellationToken cancellationToken)
    {
        var encrypted = false;
        string? error = null;
        foreach (var (parent, index, part) in InlinePgp.Candidates(entity).ToList())
        {
            var raw = await InlinePgp.RawAsync(part, cancellationToken);
            var charset = InlinePgp.CharsetOf(part);
            var output = new System.Text.StringBuilder();
            var changed = false;
            var position = 0;
            while (position < raw.Length)
            {
                var message = raw.IndexOf(InlinePgp.MessageStart, position, StringComparison.Ordinal);
                var signed = raw.IndexOf(InlinePgp.SignedStart, position, StringComparison.Ordinal);
                var start = message < 0 ? signed : signed < 0 ? message : Math.Min(message, signed);
                var isMessage = start >= 0 && start == message;
                var endMarker = isMessage ? InlinePgp.MessageEnd : InlinePgp.SignatureEnd;
                var end = start < 0 ? -1 : raw.IndexOf(endMarker, start, StringComparison.Ordinal);
                if (start < 0 || end < 0)
                {
                    output.Append(charset.GetString(InlinePgp.Bytes.GetBytes(raw[position..])));
                    break;
                }

                output.Append(charset.GetString(InlinePgp.Bytes.GetBytes(raw[position..start])));
                var block = raw[start..(end + endMarker.Length)];
                position = end + endMarker.Length;
                if (isMessage)
                {
                    encrypted = true;
                    try
                    {
                        using var input = new MemoryStream(InlinePgp.Bytes.GetBytes(block));
                        using var plain = new MemoryStream();
                        if (await context.DecryptToAsync(input, plain, cancellationToken) is { } found)
                        {
                            signatures.AddRange(await EvaluatePgpAsync(found, context, sender, cancellationToken));
                        }

                        output.Append(charset.GetString(plain.ToArray()));
                        changed = true;
                    }
                    catch (Exception ex) when (ex is PrivateKeyNotFoundException or Org.BouncyCastle.Bcpg.OpenPgp.PgpException or FormatException or ArgumentException
                        or InvalidOperationException or IOException)
                    {
                        logger.LogInformation(ex, "Could not decrypt inline PGP block");
                        error ??= ex is PrivateKeyNotFoundException
                            ? T("Kein passender privater PGP-Schlüssel vorhanden. Bitte den eigenen Schlüssel unter Einstellungen → Zertifikate importieren.")
                            : T("Die Nachricht konnte nicht entschlüsselt werden: ") + ex.Message;
                        output.Append(block);
                    }
                }
                else if (InlinePgp.ReadClearSigned(block) is { } clear)
                {
                    for (var i = 0; i < clear.Signatures.Count; i++)
                    {
                        var signature = clear.Signatures[i];
                        var key = context.Keyring.Public.Select(p => p.Ring.GetPublicKey(signature.KeyId)).FirstOrDefault(k => k is not null);
                        var signedAt = new DateTimeOffset(DateTime.SpecifyKind(signature.CreationTime, DateTimeKind.Utc));
                        signatures.Add(DescribePgp(key is null ? null : signature.KeyId, context, sender, signedAt, () =>
                        {
                            try
                            {
                                return (InlinePgp.Verify(signature, key!, clear.Text), null);
                            }
                            catch (Org.BouncyCastle.Bcpg.OpenPgp.PgpException ex)
                            {
                                return (false, ex.Message);
                            }
                        }));
                    }

                    output.Append(charset.GetString(InlinePgp.Bytes.GetBytes(clear.Text)));
                    changed = true;
                }
                else
                {
                    output.Append(charset.GetString(InlinePgp.Bytes.GetBytes(block)));
                }
            }

            if (!changed)
            {
                continue;
            }

            var opened = new TextPart("plain") { Text = output.ToString() };
            if (parent is null)
            {
                entity = opened;
            }
            else
            {
                parent[index] = opened;
            }
        }

        return (entity, encrypted, error);
    }

    private async Task ProtectPgpAsync(MimeMessage message, MailboxAddress from, bool sign, bool encrypt, CancellationToken cancellationToken)
    {
        using var context = await CreatePgpContextAsync(cancellationToken);
        var body = message.Body ?? throw new SecureMimeException(T("Die Nachricht hat keinen Inhalt."));
        var digest = SecureMimeAlgorithms.Digest(await settings.GetAsync(SettingKeys.SmimeDigest, cancellationToken));
        var signer = sign ? context.SigningKey(from.Address) ?? throw new SecureMimeException(F("Kein gültiger eigener PGP-Schlüssel für {0} vorhanden.", from.Address)) : null;
        if (!encrypt)
        {
            message.Body = await MultipartSigned.CreateAsync(context, signer!, digest, body, cancellationToken);
            return;
        }

        var keys = new List<Org.BouncyCastle.Bcpg.OpenPgp.PgpPublicKey>();
        var missing = new List<string>();
        foreach (var recipient in AllRecipients(message))
        {
            if (context.EncryptionKey(recipient.Address) is { } key)
            {
                keys.Add(key);
            }
            else
            {
                missing.Add(recipient.Address);
            }
        }

        if (missing.Count > 0)
        {
            throw new SecureMimeException(T("Für folgende Empfänger ist kein gültiger PGP-Schlüssel vorhanden: ") + string.Join(", ", missing));
        }

        message.Body = signer is null
            ? await MultipartEncrypted.EncryptAsync(context, EncryptionAlgorithm.Aes256, keys, body, cancellationToken)
            : await MultipartEncrypted.SignAndEncryptAsync(context, signer, digest, EncryptionAlgorithm.Aes256, keys, body, cancellationToken);
    }

    // The own public key along with every mail (autocrypt.org), so replies can be encrypted without exchanging keys first.
    private async Task AddAutocryptAsync(MimeMessage message, CancellationToken cancellationToken)
    {
        if (pgp is null || message.Headers.Contains(Autocrypt.HeaderName) || message.From.Mailboxes.FirstOrDefault() is not { } from
            || await settings.GetAsync(SettingKeys.Autocrypt, cancellationToken) == "false")
        {
            return;
        }

        using var context = await CreatePgpContextAsync(cancellationToken);
        if (context.SigningKey(from.Address) is not { } key
            || context.Keyring.Secret.FirstOrDefault(s => s.Ring.GetSecretKey(key.KeyId) is not null) is not { Ring: { } ring })
        {
            return;
        }

        message.Headers.Add(Autocrypt.HeaderName, Autocrypt.Format(from.Address, PgpKeyParser.PublicOf(ring), preferEncrypt: true));
    }

    /// <summary>
    /// The user compared the fingerprint of a key that signed a mail: it counts as confirmed from now on (and goes to
    /// the sender's contact, as from Einstellungen → Zertifikate).
    /// </summary>
    public Task ConfirmPgpKeyAsync(string fingerprint, CancellationToken cancellationToken = default) =>
        pgp is null ? throw new InvalidOperationException("PGP is not set up") : pgp.ConfirmAsync(fingerprint, cancellationToken);

    /// <summary>
    /// Looks up the PGP keys of recipients that have none here (WKD, keys.openpgp.org) and keeps those found as known.
    /// </summary>
    /// <returns>The recipients a key was found for.</returns>
    public async Task<IReadOnlyList<string>> FindPgpKeysAsync(IEnumerable<string> recipients, CancellationToken cancellationToken = default)
    {
        if (pgp is null || lookup is null || await settings.GetAsync(SettingKeys.PgpKeyLookup, cancellationToken) == "false")
        {
            return [];
        }

        using var context = await CreatePgpContextAsync(cancellationToken);
        var found = new List<string>();
        foreach (var address in recipients.Distinct(StringComparer.OrdinalIgnoreCase).Where(a => context.EncryptionKey(a) is null))
        {
            foreach (var ring in await lookup.FindAsync(address, cancellationToken))
            {
                await pgp.RememberAsync(ring, PgpKeySource.KeyServer, cancellationToken);
                found.Add(address);
            }
        }

        return [.. found.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    // The sender's key from the Autocrypt header: kept as known (not confirmed) when it is new here.
    private async Task RememberAutocryptAsync(MimeMessage message, CancellationToken cancellationToken)
    {
        if (pgp is null || !message.Headers.Contains(Autocrypt.HeaderName) || Autocrypt.Read(message) is not { } header)
        {
            return;
        }

        try
        {
            var fingerprint = PgpKeyParser.FingerprintOf(header.Key);
            if ((await pgp.GetAllAsync(cancellationToken)).Any(k => k.Fingerprint == fingerprint))
            {
                return;
            }

            await pgp.RememberAsync(header.Key, PgpKeySource.Autocrypt, cancellationToken);
            logger.LogInformation("PGP key {Fingerprint} taken from an Autocrypt header", fingerprint);
        }
        catch (Exception ex) when (ex is Org.BouncyCastle.Bcpg.OpenPgp.PgpException or IOException or ArgumentException)
        {
            logger.LogDebug(ex, "Autocrypt key not usable");
        }
    }
}

/// <summary>
/// Inline PGP: a "-----BEGIN PGP MESSAGE-----" or clear-signed block right in the text of a mail (older programs and
/// webmail add-ons send it so). Found in the text itself or in a text part directly below the top.
/// </summary>
internal static class InlinePgp
{
    public const string MessageStart = "-----BEGIN PGP MESSAGE-----";
    public const string MessageEnd = "-----END PGP MESSAGE-----";
    public const string SignedStart = "-----BEGIN PGP SIGNED MESSAGE-----";
    public const string SignatureEnd = "-----END PGP SIGNATURE-----";

    // Latin-1 maps each byte to one char and back: the text can be searched and cut without changing a single byte.
    public static readonly System.Text.Encoding Bytes = System.Text.Encoding.Latin1;

    public static IEnumerable<(Multipart? Parent, int Index, TextPart Part)> Candidates(MimeEntity? body)
    {
        if (body is TextPart { IsPlain: true } single)
        {
            yield return (null, -1, single);
        }
        else if (body is Multipart multipart and not MultipartSigned and not MultipartEncrypted)
        {
            for (var i = 0; i < multipart.Count; i++)
            {
                if (multipart[i] is TextPart { IsPlain: true, IsAttachment: false } part)
                {
                    yield return (multipart, i, part);
                }
            }
        }
    }

    public static bool Contains(MimeEntity? body) => Candidates(body).Any(c => Has(c.Part.Text));

    private static bool Has(string? text) =>
        text is not null && (text.Contains(MessageStart, StringComparison.Ordinal) || text.Contains(SignedStart, StringComparison.Ordinal));

    /// <summary>The transfer-decoded bytes of the part, as Latin-1 text (see <see cref="Bytes"/>).</summary>
    public static async Task<string> RawAsync(TextPart part, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        if (part.Content is not null)
        {
            await part.Content.DecodeToAsync(stream, cancellationToken);
        }

        return Bytes.GetString(stream.ToArray());
    }

    public static System.Text.Encoding CharsetOf(TextPart part)
    {
        try
        {
            return part.ContentType.CharsetEncoding ?? System.Text.Encoding.UTF8;
        }
        catch (NotSupportedException)
        {
            return System.Text.Encoding.UTF8;
        }
    }

    /// <summary>
    /// A clear-signed block: the signed text (bytes as Latin-1, dash-escaping undone) and its signatures; null if the
    /// block cannot be read.
    /// </summary>
    public static (string Text, Org.BouncyCastle.Bcpg.OpenPgp.PgpSignatureList Signatures)? ReadClearSigned(string block)
    {
        try
        {
            using var armored = new Org.BouncyCastle.Bcpg.ArmoredInputStream(new MemoryStream(Bytes.GetBytes(block)));
            using var clear = new MemoryStream();
            int ch;
            while ((ch = armored.ReadByte()) >= 0 && armored.IsClearText())
            {
                clear.WriteByte((byte)ch);
            }

            var text = Bytes.GetString(clear.ToArray()).ReplaceLineEndings("\n");
            // The line break before the signature is not part of what was signed.
            text = text.EndsWith('\n') ? text[..^1] : text;
            return new Org.BouncyCastle.Bcpg.OpenPgp.PgpObjectFactory(armored).NextPgpObject() is Org.BouncyCastle.Bcpg.OpenPgp.PgpSignatureList { Count: > 0 } signatures
                ? (text, signatures)
                : null;
        }
        catch (Exception ex) when (ex is IOException or Org.BouncyCastle.Bcpg.OpenPgp.PgpException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Checks a clear-text signature: each line without trailing blanks, lines joined by CRLF (RFC 4880, 7.1).</summary>
    public static bool Verify(Org.BouncyCastle.Bcpg.OpenPgp.PgpSignature signature, Org.BouncyCastle.Bcpg.OpenPgp.PgpPublicKey key, string text)
    {
        signature.InitVerify(key);
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            signature.Update(Bytes.GetBytes(lines[i].TrimEnd(' ', '\t', '\r')));
            if (i < lines.Length - 1)
            {
                signature.Update("\r\n"u8.ToArray());
            }
        }

        return signature.Verify();
    }
}
