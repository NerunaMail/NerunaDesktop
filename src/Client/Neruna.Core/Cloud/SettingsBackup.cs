using System.Text.Json;
using Microsoft.Extensions.Logging;
using Neruna.Contracts;
using Neruna.Contracts.Cloud;
using Neruna.Core.Accounts;
using Neruna.Core.Mail;
using Neruna.Core.Security;
using Neruna.Vault;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Core.Cloud;

/// <summary>What a backup holds: only personal things – nothing the organisation provides through the cloud.</summary>
/// <param name="Accounts">In the user's order, with the password of every connection.</param>
/// <param name="Settings">Preferences, without what belongs to this device (cloud connection, chat, window layout).</param>
/// <param name="CloudPasswords">Passwords the user entered for accounts of the organisation that came without one
/// (cloud account id → password); the accounts themselves come back from the cloud.</param>
public sealed record BackupContent(
    IReadOnlyList<BackupAccount> Accounts,
    IReadOnlyDictionary<string, string> Settings,
    IReadOnlyList<Signature> Signatures,
    IReadOnlyList<TextTemplate> TextTemplates,
    IReadOnlyList<BackupCertificate> Certificates,
    IReadOnlyDictionary<string, string>? CloudPasswords = null);

public sealed record BackupAccount(Guid Id, string DisplayName, string? EmailAddress, string? Label, IReadOnlyList<BackupConnection> Connections, IReadOnlyList<MailIdentity>? Aliases = null)
{
    public string Title => string.IsNullOrWhiteSpace(Label) ? EmailAddress ?? DisplayName : Label;
}

public sealed record BackupConnection(Guid Id, ServiceKind Kind, string ProviderId, IReadOnlyDictionary<string, string> Settings, string? Secret);

public sealed record BackupCertificate(string Thumbprint, byte[] Der, byte[]? Pkcs12, CertificateSource Source, DateTimeOffset AddedAt, string? Password);

/// <summary>The file inside the encrypted envelope.</summary>
internal sealed record BackupDocument(int Format, DateTimeOffset CreatedAt, BackupContent Content);

/// <summary>Einstellungen → Cloud → Sicherung, as far as the server knows it (notes decrypted when this device has the key).</summary>
/// <param name="HasVault">A vault exists for the person on the server.</param>
/// <param name="IsUnlocked">This device keeps the vault's key (set up or unlocked here) and can make and read backups.</param>
public sealed record BackupOverview(bool HasVault, bool IsUnlocked, IReadOnlyList<BackupEntry> Backups, int MaxBackups);

public sealed record BackupEntry(string Id, DateTimeOffset CreatedAt, string? DeviceName, string? Note, long Size);

public enum BackupChangeKind
{
    Added,
    Changed,
    Removed,
}

/// <param name="Area">"Konten", "Signaturen", "Textvorlagen", "Zertifikate", "Einstellungen".</param>
public sealed record BackupChange(string Area, string Name, BackupChangeKind Kind);

/// <summary>A downloaded, decrypted backup and what restoring it would do here; nothing is changed until it is applied.</summary>
public sealed class RestorePlan
{
    internal RestorePlan(BackupEntry backup, BackupContent content, IReadOnlyList<BackupChange> changes)
    {
        Backup = backup;
        Content = content;
        Changes = changes;
    }

    public BackupEntry Backup { get; }

    public IReadOnlyList<BackupChange> Changes { get; }

    internal BackupContent Content { get; }
}

public sealed class BackupException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Manual backups of the personal settings into the config vault of Neruna Cloud (zero knowledge, Neruna.Vault). The
/// user makes them on purpose, with a note; restoring replaces the personal settings with the backup's after a preview.
/// No merging and no sync between devices. Device keys are never part of it: a new computer keeps its own and gets the
/// organisation's certificates after the admin approved it.
/// </summary>
public sealed class SettingsBackupService(
    CloudController cloud,
    IAccountStore accounts,
    ICredentialStore credentials,
    ISettingsStore settings,
    ISignatureStore signatures,
    ITextTemplateStore textTemplates,
    CertificateManager certificates,
    TimeProvider clock,
    ILogger<SettingsBackupService> logger)
{
    /// <summary>Where this device keeps the vault's data key (system keychain): "vaultId:Base64".</summary>
    public static readonly Guid VaultKeyId = new("6e657275-6e61-436c-6f75-640000000003");

    private const int Format = 1;

    /// <summary>Fingerprint of the personal settings at the last backup or restore (keyed, says nothing about them).</summary>
    private const string SavedKey = "vault.saved";

    /// <summary>Fingerprint for which the user chose "Später" – asked again after the next change.</summary>
    private const string DismissedKey = "vault.dismissed";

    // Belongs to this device, or is only how it was left on screen.
    private static readonly string[] DeviceSettings = ["cloud.", "chat.", "vault.", "ui.layout", "mail.collapsedFolders", "mail.agendaOpen", "mail.lastFolder"];

    public static bool IsPersonalSetting(string key) => !DeviceSettings.Any(prefix => key.StartsWith(prefix, StringComparison.Ordinal));

    public async Task<BackupOverview> GetOverviewAsync(CancellationToken cancellationToken = default)
    {
        var response = await cloud.GetVaultAsync(cancellationToken);
        using var vault = response.Vault is null ? null : await LocalVaultAsync(response.Vault, cancellationToken);
        var backups = response.Backups
            .Select(b => new BackupEntry(b.Id, b.CreatedAt, b.DeviceName, vault?.DecryptText(b.Note), b.Size))
            .ToList();
        return new BackupOverview(response.Vault is not null, vault is not null, backups, response.MaxBackups);
    }

    /// <summary>First backup on any device: a new vault protected by <paramref name="password"/>.</summary>
    /// <returns>The recovery code – shown once, the only way back without the password.</returns>
    public async Task<string> SetUpAsync(string password, CancellationToken cancellationToken = default)
    {
        if ((await cloud.GetVaultAsync(cancellationToken)).Vault is not null)
        {
            throw new BackupException(T("Es gibt bereits einen Tresor. Entsperre ihn mit dem Tresor-Passwort."));
        }

        using var vault = UnlockedVault.Create(password, out var recoveryCode);
        await cloud.SetVaultAsync(new VaultUpdateRequest(vault.VaultId, JsonSerializer.SerializeToElement(vault.Keys, NerunaJson.Options)), cancellationToken);
        await KeepAsync(vault, cancellationToken);
        return recoveryCode;
    }

    /// <summary>On another device: unlock with the vault password or the recovery code; the key is then kept here.</summary>
    public async Task UnlockAsync(string passwordOrRecoveryCode, CancellationToken cancellationToken = default)
    {
        var info = (await cloud.GetVaultAsync(cancellationToken)).Vault ?? throw new BackupException(T("Es ist noch kein Tresor eingerichtet."));
        var envelope = new VaultEnvelope(VaultFormat.CurrentVersion, info.VaultId, Keys(info), string.Empty, string.Empty);
        UnlockedVault vault;
        try
        {
            vault = UnlockedVault.UnlockWithPassword(envelope, passwordOrRecoveryCode);
        }
        catch (VaultUnlockException)
        {
            try
            {
                vault = UnlockedVault.UnlockWithRecoveryCode(envelope, passwordOrRecoveryCode);
            }
            catch (VaultUnlockException ex)
            {
                throw new BackupException(T("Passwort bzw. Wiederherstellungscode ist falsch."), ex);
            }
        }

        using (vault)
        {
            await KeepAsync(vault, cancellationToken);
        }
    }

    public async Task<BackupEntry> CreateBackupAsync(string? note, CancellationToken cancellationToken = default)
    {
        var info = (await cloud.GetVaultAsync(cancellationToken)).Vault ?? throw new BackupException(T("Es ist noch kein Tresor eingerichtet."));
        using var vault = await LocalVaultAsync(info, cancellationToken) ?? throw new BackupException(T("Der Tresor ist auf diesem Gerät nicht entsperrt."));
        var content = await CollectAsync(cancellationToken);
        var document = JsonSerializer.SerializeToUtf8Bytes(new BackupDocument(Format, clock.GetUtcNow(), content), NerunaJson.Options);
        var envelope = vault.Seal(document);
        var text = string.IsNullOrWhiteSpace(note) ? null : vault.EncryptText(note.Trim());
        var created = await cloud.CreateVaultBackupAsync(new VaultBackupRequest(vault.VaultId, envelope.Version, envelope.Nonce, envelope.Ciphertext, text), cancellationToken);
        await settings.SetAsync(SavedKey, Fingerprint(vault, content), cancellationToken);
        logger.LogInformation("Settings backup {Id} created ({Size} bytes)", created.Id, created.Size);
        return new BackupEntry(created.Id, created.CreatedAt, created.DeviceName, note?.Trim(), created.Size);
    }

    /// <summary>Downloads and opens a backup and compares it with this device – the preview before restoring.</summary>
    public async Task<RestorePlan> PrepareRestoreAsync(string backupId, CancellationToken cancellationToken = default)
    {
        var info = (await cloud.GetVaultAsync(cancellationToken)).Vault ?? throw new BackupException(T("Es ist noch kein Tresor eingerichtet."));
        using var vault = await LocalVaultAsync(info, cancellationToken) ?? throw new BackupException(T("Der Tresor ist auf diesem Gerät nicht entsperrt."));
        var backup = await cloud.GetVaultBackupAsync(backupId, cancellationToken);
        BackupDocument document;
        try
        {
            var bytes = vault.Open(new VaultEnvelope(backup.Version, backup.VaultId, [], backup.Nonce, backup.Ciphertext));
            document = JsonSerializer.Deserialize<BackupDocument>(bytes, NerunaJson.Options) ?? throw new JsonException("empty");
        }
        catch (Exception ex) when (ex is VaultUnlockException or JsonException)
        {
            throw new BackupException(T("Die Sicherung lässt sich nicht öffnen (beschädigt oder aus einem anderen Tresor)."), ex);
        }

        if (document.Format > Format)
        {
            throw new BackupException(T("Die Sicherung stammt aus einer neueren Neruna-Version. Bitte zuerst aktualisieren."));
        }

        var entry = new BackupEntry(backup.Id, backup.CreatedAt, backup.DeviceName, vault.DecryptText(backup.Note), backup.Size);
        return new RestorePlan(entry, document.Content, Compare(await CollectAsync(cancellationToken), document.Content));
    }

    /// <summary>Makes the personal settings exactly those of the backup (accounts, passwords, signatures, …).</summary>
    public async Task RestoreAsync(RestorePlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var content = plan.Content;

        var wanted = content.Accounts.Select(a => a.Id).ToHashSet();
        foreach (var account in (await accounts.GetAccountsAsync(cancellationToken)).Where(a => !a.IsFromCloud))
        {
            var kept = content.Accounts.FirstOrDefault(a => a.Id == account.Id);
            foreach (var connection in account.Connections.Where(c => kept?.Connections.Any(k => k.Id == c.Id) != true))
            {
                await credentials.DeleteSecretAsync(connection.Id, cancellationToken);
            }

            if (!wanted.Contains(account.Id))
            {
                await accounts.DeleteAccountAsync(account.Id, cancellationToken);
            }
        }

        foreach (var account in content.Accounts)
        {
            await accounts.SaveAccountAsync(new Account(account.Id, account.DisplayName, account.EmailAddress,
                account.Connections.Select(c => new ServiceConnection(c.Id, c.Kind, c.ProviderId, c.Settings)).ToList(), account.Label, account.Aliases), cancellationToken);
            foreach (var connection in account.Connections)
            {
                if (connection.Secret is null)
                {
                    await credentials.DeleteSecretAsync(connection.Id, cancellationToken);
                }
                else
                {
                    await credentials.SetSecretAsync(connection.Id, connection.Secret, cancellationToken);
                }
            }
        }

        await accounts.SetOrderAsync([.. content.Accounts.Select(a => a.Id)], cancellationToken);
        foreach (var (cloudId, password) in content.CloudPasswords ?? new Dictionary<string, string>())
        {
            await CloudAccountPasswords.SetAsync(credentials, accounts, cloudId, password, cancellationToken);
        }

        foreach (var key in (await settings.GetAllAsync(cancellationToken)).Keys.Where(k => IsPersonalSetting(k) && !content.Settings.ContainsKey(k)))
        {
            await settings.SetAsync(key, null, cancellationToken);
        }

        foreach (var (key, value) in content.Settings.Where(s => IsPersonalSetting(s.Key)))
        {
            await settings.SetAsync(key, value, cancellationToken);
        }

        foreach (var signature in (await signatures.GetAllAsync(cancellationToken)).Where(s => !s.IsFromCloud && content.Signatures.All(b => b.Id != s.Id)))
        {
            await signatures.DeleteAsync(signature.Id, cancellationToken);
        }

        foreach (var signature in content.Signatures)
        {
            await signatures.SaveAsync(signature with { Source = Signature.LocalSource }, cancellationToken);
        }

        foreach (var template in (await textTemplates.GetAllAsync(cancellationToken)).Where(t => !t.IsFromCloud && content.TextTemplates.All(b => b.Id != t.Id)))
        {
            await textTemplates.DeleteAsync(template.Id, cancellationToken);
        }

        foreach (var template in content.TextTemplates)
        {
            await textTemplates.SaveAsync(template with { Source = Signature.LocalSource }, cancellationToken);
        }

        await certificates.ReplacePersonalAsync(
            [.. content.Certificates.Select(c => (new StoredCertificate(c.Thumbprint, c.Der, c.Pkcs12, c.Source, c.AddedAt), c.Password))], cancellationToken);

        // Now equal to the backup: no reminder until something changes again.
        var info = (await cloud.GetVaultAsync(cancellationToken)).Vault;
        using var vault = info is null ? null : await LocalVaultAsync(info, cancellationToken);
        if (vault is not null)
        {
            await settings.SetAsync(SavedKey, Fingerprint(vault, await CollectAsync(cancellationToken)), cancellationToken);
        }

        logger.LogInformation("Settings backup {Id} restored ({Changes} changes)", plan.Backup.Id, plan.Changes.Count);
    }

    public Task DeleteBackupAsync(string backupId, CancellationToken cancellationToken = default) => cloud.DeleteVaultBackupAsync(backupId, cancellationToken);

    /// <summary>Password and recovery code lost: the vault and all backups are deleted; a new one can be set up.</summary>
    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        await cloud.DeleteVaultAsync(cancellationToken);
        await ForgetAsync(cancellationToken);
    }

    /// <summary>When the cloud connection ends: the vault's key leaves this device too.</summary>
    public async Task ForgetAsync(CancellationToken cancellationToken = default)
    {
        await credentials.DeleteSecretAsync(VaultKeyId, cancellationToken);
        await settings.SetAsync(SavedKey, null, cancellationToken);
        await settings.SetAsync(DismissedKey, null, cancellationToken);
    }

    /// <summary>
    /// Whether to suggest a new backup: this device has the vault's key and the personal settings differ from the last
    /// backup (and the user did not say "Später" for exactly this state). Works offline.
    /// </summary>
    public async Task<bool> ShouldSuggestBackupAsync(CancellationToken cancellationToken = default)
    {
        using var vault = await StoredVaultAsync(cancellationToken);
        if (vault is null)
        {
            return false;
        }

        var current = Fingerprint(vault, await CollectAsync(cancellationToken));
        return current != await settings.GetAsync(SavedKey, cancellationToken) && current != await settings.GetAsync(DismissedKey, cancellationToken);
    }

    /// <summary>"Später": not again until something changes.</summary>
    public async Task DismissSuggestionAsync(CancellationToken cancellationToken = default)
    {
        using var vault = await StoredVaultAsync(cancellationToken);
        if (vault is not null)
        {
            await settings.SetAsync(DismissedKey, Fingerprint(vault, await CollectAsync(cancellationToken)), cancellationToken);
        }
    }

    public async Task<BackupContent> CollectAsync(CancellationToken cancellationToken = default)
    {
        var accountList = new List<BackupAccount>();
        var cloudPasswords = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var account in await accounts.GetAccountsAsync(cancellationToken))
        {
            if (account.CloudId is { } cloudId)
            {
                // From the organisation: only what the user typed in themselves.
                if (await credentials.GetSecretAsync(CloudAccountPasswords.SecretId(cloudId), cancellationToken) is { } typed)
                {
                    cloudPasswords[cloudId] = typed;
                }

                continue;
            }

            var connections = new List<BackupConnection>();
            foreach (var connection in account.Connections)
            {
                connections.Add(new BackupConnection(connection.Id, connection.Kind, connection.ProviderId,
                    new SortedDictionary<string, string>(connection.Settings.ToDictionary(), StringComparer.Ordinal),
                    await credentials.GetSecretAsync(connection.Id, cancellationToken)));
            }

            accountList.Add(new BackupAccount(account.Id, account.DisplayName, account.EmailAddress, account.Label, connections, account.Aliases));
        }

        var personalSettings = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in await settings.GetAllAsync(cancellationToken))
        {
            if (IsPersonalSetting(key))
            {
                personalSettings[key] = value;
            }
        }

        return new BackupContent(
            accountList,
            personalSettings,
            [.. (await signatures.GetAllAsync(cancellationToken)).Where(s => !s.IsFromCloud).OrderBy(s => s.Id)],
            [.. (await textTemplates.GetAllAsync(cancellationToken)).Where(t => !t.IsFromCloud).OrderBy(t => t.Id)],
            [.. (await certificates.GetPersonalAsync(cancellationToken)).Select(c => new BackupCertificate(c.Certificate.Thumbprint, c.Certificate.Der, c.Certificate.Pkcs12, c.Certificate.Source, c.Certificate.AddedAt, c.Password))],
            cloudPasswords.Count > 0 ? cloudPasswords : null);
    }

    /// <summary>What restoring <paramref name="backup"/> changes compared with <paramref name="current"/>.</summary>
    public static IReadOnlyList<BackupChange> Compare(BackupContent current, BackupContent backup)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(backup);
        var changes = new List<BackupChange>();
        Diff(changes, T("Konten"), current.Accounts, backup.Accounts, a => a.Id.ToString(), a => a.Title, Same);
        Diff(changes, T("Signaturen"), current.Signatures, backup.Signatures, s => s.Id.ToString(), s => s.Name, (a, b) => a.Name == b.Name && a.Html == b.Html);
        Diff(changes, T("Textvorlagen"), current.TextTemplates, backup.TextTemplates, t => t.Id.ToString(), t => t.Name,
            (a, b) => a.Name == b.Name && a.Html == b.Html && a.Shortcut == b.Shortcut);
        Diff(changes, T("Zertifikate"), current.Certificates, backup.Certificates, c => c.Thumbprint, CertificateName, (a, b) => a.Password == b.Password && a.Source == b.Source);

        var currentPasswords = current.CloudPasswords ?? new Dictionary<string, string>();
        var changedPasswords = (backup.CloudPasswords ?? new Dictionary<string, string>()).Count(p => currentPasswords.GetValueOrDefault(p.Key) != p.Value);
        if (changedPasswords > 0)
        {
            changes.Add(new BackupChange(T("Konten"), changedPasswords == 1 ? T("Passwort eines Kontos der Organisation") : F("Passwörter von {0} Konten der Organisation", changedPasswords), BackupChangeKind.Changed));
        }

        var changedSettings = current.Settings.Keys.Union(backup.Settings.Keys)
            .Count(k => current.Settings.GetValueOrDefault(k) != backup.Settings.GetValueOrDefault(k));
        if (changedSettings > 0)
        {
            changes.Add(new BackupChange(T("Einstellungen"), changedSettings == 1 ? T("1 Einstellung") : F("{0} Einstellungen", changedSettings), BackupChangeKind.Changed));
        }

        return changes;
    }

    private static bool Same(BackupAccount a, BackupAccount b) =>
        a.DisplayName == b.DisplayName && a.EmailAddress == b.EmailAddress && a.Label == b.Label
        && (a.Aliases ?? []).SequenceEqual(b.Aliases ?? [])
        && a.Connections.Count == b.Connections.Count
        && a.Connections.Zip(b.Connections).All(p => p.First.Id == p.Second.Id && p.First.ProviderId == p.Second.ProviderId && p.First.Secret == p.Second.Secret
                                                      && p.First.Settings.Count == p.Second.Settings.Count
                                                      && p.First.Settings.All(s => p.Second.Settings.GetValueOrDefault(s.Key) == s.Value));

    private static void Diff<T>(List<BackupChange> changes, string area, IReadOnlyList<T> current, IReadOnlyList<T> backup, Func<T, string> id, Func<T, string> name, Func<T, T, bool> same)
    {
        foreach (var item in backup)
        {
            var local = current.FirstOrDefault(c => id(c) == id(item));
            if (local is null)
            {
                changes.Add(new BackupChange(area, name(item), BackupChangeKind.Added));
            }
            else if (!same(local, item))
            {
                changes.Add(new BackupChange(area, name(item), BackupChangeKind.Changed));
            }
        }

        foreach (var item in current.Where(c => backup.All(b => id(b) != id(c))))
        {
            changes.Add(new BackupChange(area, name(item), BackupChangeKind.Removed));
        }
    }

    private static string CertificateName(BackupCertificate certificate)
    {
        try
        {
            var info = CertificateParser.Describe(new Org.BouncyCastle.X509.X509CertificateParser().ReadCertificate(certificate.Der), certificate.Pkcs12 is not null, certificate.Source, certificate.AddedAt);
            return info.EmailAddresses.Count > 0 ? string.Join(", ", info.EmailAddresses) : info.SubjectName;
        }
        catch (Exception ex) when (ex is Org.BouncyCastle.Security.Certificates.CertificateException or IOException or ArgumentException)
        {
            return certificate.Thumbprint;
        }
    }

    private static string Fingerprint(UnlockedVault vault, BackupContent content) =>
        vault.Fingerprint(JsonSerializer.SerializeToUtf8Bytes(content, NerunaJson.Options));

    private static List<WrappedKey> Keys(CloudVault info) =>
        info.Keys.Deserialize<List<WrappedKey>>(NerunaJson.Options) ?? [];

    private async Task KeepAsync(UnlockedVault vault, CancellationToken cancellationToken)
    {
        var key = vault.ExportDataKey();
        try
        {
            await credentials.SetSecretAsync(VaultKeyId, $"{vault.VaultId:N}:{Convert.ToBase64String(key)}", cancellationToken);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);
        }
    }

    // The kept key, if it belongs to the vault the server has (another vault = set up again elsewhere: forget it).
    private async Task<UnlockedVault?> LocalVaultAsync(CloudVault info, CancellationToken cancellationToken)
    {
        var vault = await StoredVaultAsync(cancellationToken);
        if (vault is not null && vault.VaultId != info.VaultId)
        {
            vault.Dispose();
            await ForgetAsync(cancellationToken);
            return null;
        }

        return vault is null ? null : UnlockedVaultWithKeys(vault, info);
    }

    private static UnlockedVault UnlockedVaultWithKeys(UnlockedVault vault, CloudVault info)
    {
        using (vault)
        {
            var key = vault.ExportDataKey();
            try
            {
                return UnlockedVault.Resume(vault.VaultId, key, Keys(info));
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);
            }
        }
    }

    private async Task<UnlockedVault?> StoredVaultAsync(CancellationToken cancellationToken)
    {
        var stored = await credentials.GetSecretAsync(VaultKeyId, cancellationToken);
        var parts = stored?.Split(':');
        if (parts is not { Length: 2 } || !Guid.TryParse(parts[0], out var vaultId))
        {
            return null;
        }

        try
        {
            return UnlockedVault.Resume(vaultId, Convert.FromBase64String(parts[1]), []);
        }
        catch (Exception ex) when (ex is FormatException or VaultUnlockException)
        {
            logger.LogWarning(ex, "Stored vault key unreadable");
            return null;
        }
    }
}
