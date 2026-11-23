using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neruna.Core;
using Neruna.Core.Accounts;
using Neruna.Core.Security;
using Neruna.Desktop.Infrastructure;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.ViewModels;

/// <summary>
/// PGP keys on the certificates page: own keys (import, make, export, back up) and other people's (confirm, remove),
/// plus how PGP is used when sending.
/// </summary>
internal sealed partial class PgpKeysViewModel(PgpKeyManager manager, CertificateManager certificates, ISettingsStore settings, IFileService files, IAccountStore accounts, TimeProvider clock) : ViewModelBase
{
    private bool _loading;
    private (string FileName, byte[] Data)? _pendingImport;

    public ObservableCollection<PgpKeyItem> Own { get; } = [];

    public ObservableCollection<PgpKeyItem> Others { get; } = [];

    public bool HasOwn => Own.Count > 0;

    public bool HasOthers => Others.Count > 0;

    public string OwnHeader => F("Eigene PGP-Schlüssel ({0})", Own.Count);

    public string OthersHeader => F("PGP-Schlüssel anderer ({0})", Others.Count);

    public static IReadOnlyList<AlgorithmChoice> MethodChoices { get; } =
        [new("smime", "S/MIME"), new("pgp", "PGP")];

    /// <summary>
    /// For one of the own addresses there is both an S/MIME certificate and a PGP key to sign with – only then can the
    /// preferred method make a difference, and only then is it offered.
    /// </summary>
    [ObservableProperty]
    public partial bool HasBothMethods { get; set; }

    /// <summary>The method used where sender and recipients would allow both.</summary>
    [ObservableProperty]
    public partial AlgorithmChoice PreferredMethod { get; set; } = MethodChoices[0];

    [ObservableProperty]
    public partial bool SendAutocrypt { get; set; } = true;

    [ObservableProperty]
    public partial bool LookUpKeys { get; set; } = true;

    [ObservableProperty]
    public partial string? Message { get; set; }

    [ObservableProperty]
    public partial bool MessageIsError { get; set; }

    // Import of an own key: its passphrase is asked for.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PendingFileName))]
    public partial bool IsPassphrasePromptVisible { get; set; }

    [ObservableProperty]
    public partial string ImportPassphrase { get; set; } = string.Empty;

    public string PendingFileName => _pendingImport?.FileName ?? string.Empty;

    // A new key: for which address.
    [ObservableProperty]
    public partial bool IsGenerating { get; set; }

    [ObservableProperty]
    public partial bool IsWorking { get; set; }

    public ObservableCollection<MailIdentity> Identities { get; } = [];

    [ObservableProperty]
    public partial MailIdentity? GenerateFor { get; set; }

    // A backup of an own key: protected by a passphrase the user chooses.
    [ObservableProperty]
    public partial PgpKeyItem? BackingUp { get; set; }

    [ObservableProperty]
    public partial string BackupPassphrase { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string BackupPassphraseAgain { get; set; } = string.Empty;

    public async Task ReloadAsync()
    {
        _loading = true;
        try
        {
            var preferred = await settings.GetAsync(SettingKeys.PreferredSecureMethod);
            PreferredMethod = MethodChoices.FirstOrDefault(m => m.Key == preferred) ?? MethodChoices[0];
            SendAutocrypt = await settings.GetBoolAsync(SettingKeys.Autocrypt, fallback: true);
            LookUpKeys = await settings.GetBoolAsync(SettingKeys.PgpKeyLookup, fallback: true);
        }
        finally
        {
            _loading = false;
        }

        var now = clock.GetUtcNow();
        Own.Clear();
        Others.Clear();
        foreach (var info in await manager.GetAllAsync())
        {
            (info.HasSecretKey ? Own : Others).Add(new PgpKeyItem(info, now));
        }

        var signingCertificates = (await certificates.GetAllAsync())
            .Where(c => c.HasPrivateKey && c.CanSign && c.IsUsableAt(now))
            .SelectMany(c => c.EmailAddresses);
        HasBothMethods = Own.Where(k => k.Info.CanSign && k.Info.IsUsableAt(now))
            .SelectMany(k => k.Info.EmailAddresses)
            .Intersect(signingCertificates, StringComparer.OrdinalIgnoreCase)
            .Any();

        OnPropertyChanged(nameof(HasOwn));
        OnPropertyChanged(nameof(HasOthers));
        OnPropertyChanged(nameof(OwnHeader));
        OnPropertyChanged(nameof(OthersHeader));
    }

    partial void OnPreferredMethodChanged(AlgorithmChoice value)
    {
        if (!_loading && value is not null)
        {
            _ = settings.SetAsync(SettingKeys.PreferredSecureMethod, value.Key);
        }
    }

    partial void OnSendAutocryptChanged(bool value)
    {
        if (!_loading)
        {
            _ = settings.SetBoolAsync(SettingKeys.Autocrypt, value);
        }
    }

    partial void OnLookUpKeysChanged(bool value)
    {
        if (!_loading)
        {
            _ = settings.SetBoolAsync(SettingKeys.PgpKeyLookup, value);
        }
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        Message = null;
        foreach (var path in await files.PickFilesAsync(T("PGP-Schlüssel importieren (.asc, .gpg, .pgp, .key)")))
        {
            byte[] data;
            try
            {
                data = await File.ReadAllBytesAsync(path);
            }
            catch (IOException ex)
            {
                Show(F("«{0}» konnte nicht gelesen werden: {1}", Path.GetFileName(path), ex.Message), error: true);
                continue;
            }

            await ImportDataAsync(Path.GetFileName(path), data, null);
        }
    }

    /// <summary>Also used by tests and the snapshot tool, which have no file picker.</summary>
    public async Task ImportDataAsync(string fileName, byte[] data, string? passphrase)
    {
        try
        {
            var imported = await manager.ImportAsync(data, passphrase);
            Show(imported.Count == 1
                ? F("PGP-Schlüssel «{0}» importiert.", imported[0].Name)
                : F("{0} PGP-Schlüssel aus «{1}» importiert.", imported.Count, fileName), error: false);
            IsPassphrasePromptVisible = false;
            _pendingImport = null;
            await ReloadAsync();
        }
        catch (CertificatePasswordException) when (passphrase is null)
        {
            // An own key with its secret part: ask for the passphrase.
            _pendingImport = (fileName, data);
            ImportPassphrase = string.Empty;
            IsPassphrasePromptVisible = true;
        }
        catch (CertificatePasswordException ex)
        {
            Show(ex.Message, error: true);
        }
        catch (FormatException)
        {
            Show(F("«{0}» enthält keinen PGP-Schlüssel.", fileName), error: true);
        }
    }

    [RelayCommand]
    private Task ConfirmImportAsync() =>
        _pendingImport is { } pending ? ImportDataAsync(pending.FileName, pending.Data, ImportPassphrase) : Task.CompletedTask;

    [RelayCommand]
    private void CancelImport()
    {
        _pendingImport = null;
        IsPassphrasePromptVisible = false;
    }

    [RelayCommand]
    private async Task StartGenerateAsync()
    {
        Message = null;
        Identities.Clear();
        foreach (var identity in (await accounts.GetAccountsAsync()).SelectMany(a => a.Identities).DistinctBy(i => i.Email, StringComparer.OrdinalIgnoreCase))
        {
            Identities.Add(identity);
        }

        GenerateFor = Identities.FirstOrDefault(i => !Own.Any(o => o.Info.Covers(i.Email))) ?? Identities.FirstOrDefault();
        IsGenerating = true;
    }

    [RelayCommand]
    private void CancelGenerate() => IsGenerating = false;

    [RelayCommand]
    private async Task GenerateAsync()
    {
        if (GenerateFor is not { } identity || IsWorking)
        {
            return;
        }

        IsWorking = true;
        try
        {
            var key = await manager.GenerateAsync(identity.DisplayName, identity.Email);
            IsGenerating = false;
            Show(F("Neuer PGP-Schlüssel für {0} erstellt. Bitte eine Sicherung exportieren – ohne sie sind verschlüsselte Nachrichten verloren, wenn dieses Gerät ausfällt.", key.EmailAddresses.FirstOrDefault() ?? key.Name), error: false);
            await ReloadAsync();
        }
        finally
        {
            IsWorking = false;
        }
    }

    [RelayCommand]
    private async Task ExportAsync(PgpKeyItem item)
    {
        var target = await files.SaveFileAsync(T("Öffentlichen PGP-Schlüssel exportieren"), FileService.SanitizeFileName(item.FileBaseName) + ".asc");
        if (target is null)
        {
            return;
        }

        await using (target)
        await using (var writer = new StreamWriter(target))
        {
            await writer.WriteAsync(await manager.ExportPublicAsync(item.Info.Fingerprint));
        }

        Show(T("Öffentlicher PGP-Schlüssel exportiert (enthält keinen privaten Schlüssel)."), error: false);
    }

    [RelayCommand]
    private void StartBackup(PgpKeyItem item)
    {
        Message = null;
        BackupPassphrase = BackupPassphraseAgain = string.Empty;
        BackingUp = item;
    }

    [RelayCommand]
    private void CancelBackup() => BackingUp = null;

    [RelayCommand]
    private async Task BackupAsync()
    {
        if (BackingUp is not { } item)
        {
            return;
        }

        if (BackupPassphrase.Length < 8 || BackupPassphrase != BackupPassphraseAgain)
        {
            Show(BackupPassphrase.Length < 8 ? T("Die Passphrase muss mindestens 8 Zeichen lang sein.") : T("Die beiden Passphrasen stimmen nicht überein."), error: true);
            return;
        }

        var target = await files.SaveFileAsync(T("Sicherung des PGP-Schlüssels speichern"), FileService.SanitizeFileName(item.FileBaseName) + "-privat.asc");
        if (target is null)
        {
            return;
        }

        await using (target)
        await using (var writer = new StreamWriter(target))
        {
            await writer.WriteAsync(await manager.ExportSecretAsync(item.Info.Fingerprint, BackupPassphrase));
        }

        BackingUp = null;
        BackupPassphrase = BackupPassphraseAgain = string.Empty;
        Show(T("Sicherung gespeichert. Sie enthält den privaten Schlüssel, geschützt durch die Passphrase – gut aufbewahren."), error: false);
    }

    [RelayCommand]
    private async Task ConfirmAsync(PgpKeyItem item)
    {
        if (!item.ConfirmVerify)
        {
            item.ConfirmVerify = true;
            return;
        }

        var withContact = await manager.ConfirmAsync(item.Info.Fingerprint);
        Show(withContact
            ? F("Schlüssel von {0} bestätigt und beim Kontakt gespeichert.", item.Name)
            : F("Schlüssel von {0} bestätigt.", item.Name), error: false);
        await ReloadAsync();
    }

    [RelayCommand]
    private async Task RemoveAsync(PgpKeyItem item)
    {
        if (!item.ConfirmRemove)
        {
            item.ConfirmRemove = true;
            return;
        }

        await manager.RemoveAsync(item.Info.Fingerprint);
        Show(F("PGP-Schlüssel «{0}» entfernt.", item.Name), error: false);
        await ReloadAsync();
    }

    private void Show(string text, bool error)
    {
        Message = text;
        MessageIsError = error;
    }
}

internal sealed partial class PgpKeyItem(PgpKeyInfo info, DateTimeOffset now) : ObservableObject
{
    private static readonly IBrush GoodBrush = Brush.Parse("#107C10");
    private static readonly IBrush WarnBrush = Brush.Parse("#BC4B09");
    private static readonly IBrush BadBrush = Brush.Parse("#C50F1F");
    private static readonly IBrush NeutralBrush = Brush.Parse("#616161");

    public PgpKeyInfo Info { get; } = info;

    public string Name => Info.Name;

    public string Emails => Info.EmailAddresses.Count > 0 ? string.Join(", ", Info.EmailAddresses) : Info.Name;

    public bool HasSecondaryName => Info.EmailAddresses.Count > 0 && Info.Name != Info.EmailAddresses[0];

    public string FileBaseName => Info.EmailAddresses.FirstOrDefault() ?? Info.KeyId;

    public string Fingerprint => Info.FingerprintText;

    public string Algorithm => Info.Algorithm;

    public string ValidUntil => Info.Expires is { } end ? end.LocalDateTime.ToString("d", CultureInfo.CurrentCulture) : T("unbegrenzt");

    public CertificateStatus Status => Info.StatusAt(now);

    public string StatusText => Info.IsRevoked ? T("Widerrufen") : Status switch
    {
        CertificateStatus.Valid => T("Gültig"),
        CertificateStatus.ExpiringSoon => F("Läuft in {0} Tagen ab", Math.Max(0, (int)(Info.Expires!.Value - now).TotalDays)),
        CertificateStatus.Expired => T("Abgelaufen"),
        _ => T("Noch nicht gültig"),
    };

    public IBrush StatusBrush => Status switch
    {
        CertificateStatus.Valid => GoodBrush,
        CertificateStatus.ExpiringSoon => WarnBrush,
        CertificateStatus.Expired => BadBrush,
        _ => NeutralBrush,
    };

    /// <summary>Confirmed (own, the organisation's, the contact's or compared by the user) or merely known.</summary>
    public string TrustText => Info.HasSecretKey ? T("Eigener Schlüssel")
        : Info.IsTrusted ? T("Bestätigt")
        : T("Nicht bestätigt – Fingerabdruck mit dem Inhaber vergleichen");

    public IBrush TrustBrush => Info.IsTrusted ? GoodBrush : WarnBrush;

    public bool CanConfirm => !Info.IsTrusted;

    /// <summary>Organisation keys are not offered for backup – they come back through the cloud.</summary>
    public bool CanBackup => Info.HasSecretKey && Info.Source != PgpKeySource.Cloud;

    public bool CanRemove => Info.Source is not (PgpKeySource.Cloud or PgpKeySource.Organization);

    public string SourceText => Info.Source switch
    {
        PgpKeySource.Generated => T("In Neruna erstellt am ") + Date(Info.AddedAt),
        PgpKeySource.Cloud => T("Cloud – von der Organisation bereitgestellt am ") + Date(Info.AddedAt),
        PgpKeySource.Organization => T("Aus dem Verzeichnis der Organisation"),
        PgpKeySource.Contact => T("Beim Kontakt im Adressbuch gespeichert"),
        PgpKeySource.Autocrypt => T("Mit einer Nachricht erhalten (Autocrypt) am ") + Date(Info.AddedAt),
        PgpKeySource.KeyServer => T("Online gefunden am ") + Date(Info.AddedAt),
        _ => T("Importiert am ") + Date(Info.AddedAt),
    };

    public string UsageText
    {
        get
        {
            var usages = new List<string>();
            if (Info.CanSign)
            {
                usages.Add(Info.HasSecretKey ? T("Signieren") : T("Signaturen prüfen"));
            }

            if (Info.CanEncrypt)
            {
                usages.Add(Info.HasSecretKey ? T("Entschlüsseln") : T("Verschlüsseln an"));
            }

            return usages.Count > 0 ? string.Join(" · ", usages) : T("nicht für E-Mail geeignet");
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RemoveText))]
    public partial bool ConfirmRemove { get; set; }

    public string RemoveText => ConfirmRemove ? T("Wirklich entfernen?") : T("Entfernen");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConfirmText))]
    public partial bool ConfirmVerify { get; set; }

    public string ConfirmText => ConfirmVerify ? T("Fingerabdruck verglichen?") : T("Bestätigen");

    private static string Date(DateTimeOffset value) => value.LocalDateTime.ToString("d", CultureInfo.CurrentCulture);
}
