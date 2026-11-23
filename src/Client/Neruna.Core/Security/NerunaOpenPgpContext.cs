using MimeKit;
using MimeKit.Cryptography;
using Org.BouncyCastle.Bcpg.OpenPgp;

namespace Neruna.Core.Security;

/// <summary>
/// MimeKit's OpenPGP context fed from the user's <see cref="PgpKeyring"/> (in memory, nothing written anywhere). For
/// each address it takes the newest usable key, preferring the more trusted one.
/// </summary>
internal sealed class NerunaOpenPgpContext : OpenPgpContext
{
    private readonly PgpKeyring _keyring;
    private readonly DateTimeOffset _now;

    public NerunaOpenPgpContext(PgpKeyring keyring, DateTimeOffset now)
    {
        _keyring = keyring;
        _now = now;
    }

    public PgpKeyring Keyring => _keyring;

    /// <summary>The key (ring) a key ID belongs to, with what is known about it.</summary>
    public PgpKeyInfo? InfoFor(long keyId) =>
        _keyring.Public.FirstOrDefault(p => p.Ring.GetPublicKey(keyId) is not null).Info;

    protected override string GetPasswordForKey(PgpSecretKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _keyring.Secret.FirstOrDefault(s => s.Ring.GetSecretKey(key.KeyId) is not null).Passphrase
            ?? throw new PrivateKeyNotFoundException(key.KeyId, "No passphrase for this key.");
    }

    protected override PgpPublicKeyRing? GetPublicKeyRing(long keyId, CancellationToken cancellationToken) =>
        _keyring.Public.FirstOrDefault(p => p.Ring.GetPublicKey(keyId) is not null).Ring;

    protected override Task<PgpPublicKeyRing?> GetPublicKeyRingAsync(long keyId, CancellationToken cancellationToken) =>
        Task.FromResult(GetPublicKeyRing(keyId, cancellationToken));

    protected override PgpSecretKey GetSecretKey(long keyId, CancellationToken cancellationToken) =>
        _keyring.Secret.Select(s => s.Ring.GetSecretKey(keyId)).FirstOrDefault(k => k is not null)
        ?? throw new PrivateKeyNotFoundException(keyId, "The private key could not be found.");

    public override PgpSecretKey GetSigningKey(MailboxAddress mailbox, CancellationToken cancellationToken = default) =>
        SigningKey(mailbox?.Address ?? string.Empty) ?? throw new PrivateKeyNotFoundException(mailbox!, "No PGP key to sign with.");

    public override IList<PgpPublicKey> GetPublicKeys(IEnumerable<MailboxAddress> mailboxes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mailboxes);
        var keys = new List<PgpPublicKey>();
        foreach (var mailbox in mailboxes)
        {
            keys.Add(EncryptionKey(mailbox.Address) ?? throw new PublicKeyNotFoundException(mailbox, "No PGP key for this address."));
        }

        return keys;
    }

    public PgpSecretKey? SigningKey(string address)
    {
        foreach (var (ring, _, info) in _keyring.Secret.Where(s => s.Info.Covers(address) && s.Info.IsUsableAt(_now)).OrderByDescending(s => s.Info.Created))
        {
            var primary = ring.GetPublicKey();
            if (ring.GetSecretKeys().Where(k => !k.IsPrivateKeyEmpty && PgpKeyParser.CanSign(k.PublicKey, primary) && Usable(k.PublicKey))
                    .OrderByDescending(k => k.PublicKey.CreationTime).FirstOrDefault() is { } key)
            {
                return key;
            }
        }

        return null;
    }

    /// <summary>The key to encrypt to: confirmed and own/organisation keys before merely known ones, then the newest.</summary>
    public PgpPublicKey? EncryptionKey(string address)
    {
        var candidates = _keyring.Public
            .Where(p => p.Info.Covers(address) && p.Info.CanEncrypt && p.Info.IsUsableAt(_now))
            .OrderByDescending(p => p.Info.IsTrusted)
            .ThenByDescending(p => p.Info.Created);
        foreach (var (ring, _) in candidates)
        {
            if (ring.GetPublicKeys().Where(k => k.IsEncryptionKey && Usable(k)).OrderByDescending(k => k.CreationTime).FirstOrDefault() is { } key)
            {
                return key;
            }
        }

        return null;
    }

    private bool Usable(PgpPublicKey key)
    {
        if (key.HasRevocation())
        {
            return false;
        }

        var seconds = key.GetValidSeconds();
        return seconds <= 0 || DateTime.SpecifyKind(key.CreationTime, DateTimeKind.Utc).AddSeconds(seconds) > _now.UtcDateTime;
    }

    public override bool CanSign(MailboxAddress signer, CancellationToken cancellationToken = default) => SigningKey(signer?.Address ?? string.Empty) is not null;

    public override Task<bool> CanSignAsync(MailboxAddress signer, CancellationToken cancellationToken = default) => Task.FromResult(CanSign(signer, cancellationToken));

    public override bool CanEncrypt(MailboxAddress mailbox, CancellationToken cancellationToken = default) => EncryptionKey(mailbox?.Address ?? string.Empty) is not null;

    public override Task<bool> CanEncryptAsync(MailboxAddress mailbox, CancellationToken cancellationToken = default) => Task.FromResult(CanEncrypt(mailbox, cancellationToken));

    // Keys come in through PgpKeyManager only; MimeKit's own import and export paths are not used.
    public override void Import(PgpPublicKeyRing keyring, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public override void Import(PgpPublicKeyRingBundle bundle, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public override void Import(PgpSecretKeyRing keyring, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public override void Import(PgpSecretKeyRingBundle bundle, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public override void Export(IEnumerable<MailboxAddress> mailboxes, Stream stream, bool armor, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public override Task ExportAsync(IEnumerable<MailboxAddress> mailboxes, Stream stream, bool armor, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public override MimePart Export(IEnumerable<MailboxAddress> mailboxes, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public override Task<MimePart> ExportAsync(IEnumerable<MailboxAddress> mailboxes, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
