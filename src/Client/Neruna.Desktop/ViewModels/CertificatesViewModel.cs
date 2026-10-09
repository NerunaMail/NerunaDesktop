using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neruna.Core;
using Neruna.Core.Security;
using Neruna.Desktop.Infrastructure;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.ViewModels;

/// <summary>S/MIME certificate overview: own certificates, contacts' certificates, trusted authorities.</summary>
internal sealed partial class CertificatesViewModel(CertificateManager manager, ISettingsStore settings, IFileService files, TimeProvider clock) : ViewModelBase
{
    private bool _loading;
    private (string FileName, byte[] Data)? _pendingImport;

    public ObservableCollection<CertificateItem> Own { get; } = [];

    public ObservableCollection<CertificateItem> Contacts { get; } = [];

    public ObservableCollection<CertificateItem> Authorities { get; } = [];

    public bool HasOwn => Own.Count > 0;

    public bool HasContacts => Contacts.Count > 0;

    public bool HasAuthorities => Authorities.Count > 0;

    [ObservableProperty]
    public partial bool AutoSign { get; set; } = true;

    [ObservableProperty]
    public partial bool AutoEncrypt { get; set; } = true;

    /// <summary>Signature hash and encryption cipher (see SecureMimeAlgorithms).</summary>
    public static IReadOnlyList<AlgorithmChoice> DigestChoices { get; } =
        SecureMimeAlgorithms.Digests.Select(d => new AlgorithmChoice(d.Key, d.Label)).ToList();

    public static IReadOnlyList<AlgorithmChoice> CipherChoices { get; } =
        SecureMimeAlgorithms.Ciphers.Select(c => new AlgorithmChoice(c.Key, c.Label)).ToList();

    [ObservableProperty]
    public partial AlgorithmChoice Digest { get; set; } = DigestChoices[0];

    [ObservableProperty]
    public partial AlgorithmChoice Cipher { get; set; } = CipherChoices[0];

    /// <summary>Tab headers with counts.</summary>
    public string OwnHeader => F("Eigene Zertifikate ({0})", Own.Count);

    public string ContactsHeader => F("Zertifikate von Kontakten ({0})", Contacts.Count);

    public string AuthoritiesHeader => F("Zertifizierungsstellen ({0})", Authorities.Count);

    [ObservableProperty]
    public partial string? Message { get; set; }

    [ObservableProperty]
    public partial bool MessageIsError { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PendingFileName))]
    public partial bool IsPasswordPromptVisible { get; set; }

    [ObservableProperty]
    public partial string ImportPassword { get; set; } = string.Empty;

    public string PendingFileName => _pendingImport?.FileName ?? string.Empty;

    public async Task ReloadAsync()
    {
        _loading = true;
        try
        {
            AutoSign = await settings.GetBoolAsync(SettingKeys.AutoSign, fallback: true);
            AutoEncrypt = await settings.GetBoolAsync(SettingKeys.AutoEncrypt, fallback: true);
            var digest = await settings.GetAsync(SettingKeys.SmimeDigest);
            Digest = DigestChoices.FirstOrDefault(d => d.Key == digest) ?? DigestChoices[0];
            var cipher = await settings.GetAsync(SettingKeys.SmimeCipher);
            Cipher = CipherChoices.FirstOrDefault(c => c.Key == cipher) ?? CipherChoices[0];
        }
        finally
        {
            _loading = false;
        }

        var now = clock.GetUtcNow();
        Own.Clear();
        Contacts.Clear();
        Authorities.Clear();
        foreach (var info in await manager.GetAllAsync())
        {
            var target = info.HasPrivateKey ? Own : info.IsAuthority ? Authorities : Contacts;
            target.Add(new CertificateItem(info, now));
        }

        OnPropertyChanged(nameof(HasOwn));
        OnPropertyChanged(nameof(HasContacts));
        OnPropertyChanged(nameof(HasAuthorities));
        OnPropertyChanged(nameof(OwnHeader));
        OnPropertyChanged(nameof(ContactsHeader));
        OnPropertyChanged(nameof(AuthoritiesHeader));
    }

    partial void OnDigestChanged(AlgorithmChoice value)
    {
        if (!_loading && value is not null)
        {
            _ = settings.SetAsync(SettingKeys.SmimeDigest, value.Key);
        }
    }

    partial void OnCipherChanged(AlgorithmChoice value)
    {
        if (!_loading && value is not null)
        {
            _ = settings.SetAsync(SettingKeys.SmimeCipher, value.Key);
        }
    }

    partial void OnAutoSignChanged(bool value)
    {
        if (!_loading)
        {
            _ = settings.SetBoolAsync(SettingKeys.AutoSign, value);
        }
    }

    partial void OnAutoEncryptChanged(bool value)
    {
        if (!_loading)
        {
            _ = settings.SetBoolAsync(SettingKeys.AutoEncrypt, value);
        }
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        Message = null;
        foreach (var path in await files.PickFilesAsync(T("Zertifikat importieren (.p12, .pfx, .cer, .crt, .pem, .p7b)")))
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
    public async Task ImportDataAsync(string fileName, byte[] data, string? password)
    {
        try
        {
            var imported = await manager.ImportAsync(data, password);
            Show(imported.Count == 1
                ? F("Zertifikat «{0}» importiert.", imported[0].SubjectName)
                : F("{0} Zertifikate aus «{1}» importiert.", imported.Count, fileName), error: false);
            IsPasswordPromptVisible = false;
            _pendingImport = null;
            await ReloadAsync();
        }
        catch (CertificatePasswordException) when (password is null)
        {
            // Probably a .p12/.pfx: ask for its password.
            _pendingImport = (fileName, data);
            ImportPassword = string.Empty;
            IsPasswordPromptVisible = true;
        }
        catch (CertificatePasswordException ex)
        {
            Show(ex.Message, error: true);
        }
        catch (FormatException ex)
        {
            Show(ex.Message, error: true);
        }
    }

    [RelayCommand]
    private Task ConfirmImportAsync() =>
        _pendingImport is { } pending ? ImportDataAsync(pending.FileName, pending.Data, ImportPassword) : Task.CompletedTask;

    [RelayCommand]
    private void CancelImport()
    {
        _pendingImport = null;
        IsPasswordPromptVisible = false;
    }

    [RelayCommand]
    private async Task ExportAsync(CertificateItem item)
    {
        if (!item.CanExport)
        {
            return;
        }

        var target = await files.SaveFileAsync(T("Öffentliches Zertifikat exportieren"), FileService.SanitizeFileName(item.FileBaseName) + ".cer");
        if (target is null)
        {
            return;
        }

        await using (target)
        {
            await target.WriteAsync(await manager.ExportPublicAsync(item.Info.Thumbprint));
        }

        Show(T("Öffentliches Zertifikat exportiert (enthält keinen privaten Schlüssel)."), error: false);
    }

    [RelayCommand]
    private async Task RemoveAsync(CertificateItem item)
    {
        if (!item.ConfirmRemove)
        {
            item.ConfirmRemove = true;
            return;
        }

        await manager.RemoveAsync(item.Info.Thumbprint);
        Show(F("Zertifikat «{0}» entfernt.", item.Name), error: false);
        await ReloadAsync();
    }

    private void Show(string text, bool error)
    {
        Message = text;
        MessageIsError = error;
    }
}

internal sealed record AlgorithmChoice(string Key, string Label)
{
    public override string ToString() => Label;
}

internal sealed partial class CertificateItem(CertificateInfo info, DateTimeOffset now) : ObservableObject
{
    /// <summary>
    /// Certificates from the organisation are not offered for export – no absolute protection, but it keeps them from
    /// being passed on casually.
    /// </summary>
    public bool CanExport => Info.Source != CertificateSource.Cloud;

    private static readonly IBrush GoodBrush = Brush.Parse("#107C10");
    private static readonly IBrush WarnBrush = Brush.Parse("#BC4B09");
    private static readonly IBrush BadBrush = Brush.Parse("#C50F1F");
    private static readonly IBrush NeutralBrush = Brush.Parse("#616161");

    public CertificateInfo Info { get; } = info;

    public string Name => Info.SubjectName;

    /// <summary>Headline: the e-mail addresses, or the name for certificates without one (e.g. authorities).</summary>
    public string Emails => Info.EmailAddresses.Count > 0 ? string.Join(", ", Info.EmailAddresses) : Info.SubjectName;

    public bool HasSecondaryName => Info.EmailAddresses.Count > 0;

    public string FileBaseName => Info.EmailAddresses.FirstOrDefault() ?? Info.SubjectName;

    public string Issuer => T("Ausgestellt von ") + Info.IssuerName;

    public string ValidUntil => Info.NotAfter.LocalDateTime.ToString("d", CultureInfo.CurrentCulture);

    public string ValidRange =>
        $"{Info.NotBefore.LocalDateTime.ToString("d", CultureInfo.CurrentCulture)} – {ValidUntil}";

    public CertificateStatus Status => Info.StatusAt(now);

    public string StatusText => Status switch
    {
        CertificateStatus.Valid => T("Gültig"),
        CertificateStatus.ExpiringSoon => F("Läuft in {0} Tagen ab", Math.Max(0, (int)(Info.NotAfter - now).TotalDays)),
        CertificateStatus.Expired => T("Abgelaufen"),
        _ => T("Noch nicht gültig"),
    };

    public string RemainingText => Status switch
    {
        CertificateStatus.Valid => F("noch {0} Tage", (int)(Info.NotAfter - now).TotalDays),
        CertificateStatus.Expired => F("seit {0} Tagen", (int)(now - Info.NotAfter).TotalDays),
        _ => string.Empty,
    };

    public IBrush StatusBrush => Status switch
    {
        CertificateStatus.Valid => GoodBrush,
        CertificateStatus.ExpiringSoon => WarnBrush,
        CertificateStatus.Expired => BadBrush,
        _ => NeutralBrush,
    };

    public string UsageText
    {
        get
        {
            if (Info.IsAuthority)
            {
                return T("Zertifizierungsstelle (vertrauenswürdig)");
            }

            var usages = new List<string>();
            if (Info.CanSign)
            {
                usages.Add(Info.HasPrivateKey ? T("Signieren") : T("Signaturen prüfen"));
            }

            if (Info.CanEncrypt)
            {
                usages.Add(Info.HasPrivateKey ? T("Entschlüsseln") : T("Verschlüsseln an"));
            }

            return usages.Count > 0 ? string.Join(" · ", usages) : T("nicht für E-Mail geeignet");
        }
    }

    public string SourceText => Info.Source switch
    {
        CertificateSource.CollectedFromMail => T("Aus signierter Nachricht übernommen am ") + Info.AddedAt.LocalDateTime.ToString("d", CultureInfo.CurrentCulture),
        CertificateSource.Cloud => T("Cloud – von der Organisation bereitgestellt am ") + Info.AddedAt.LocalDateTime.ToString("d", CultureInfo.CurrentCulture),
        _ => T("Importiert am ") + Info.AddedAt.LocalDateTime.ToString("d", CultureInfo.CurrentCulture),
    };

    /// <summary>SHA-1 fingerprint in groups of four, as Windows shows it.</summary>
    public string Fingerprint => string.Join(' ', Enumerable.Range(0, Info.Thumbprint.Length / 4).Select(i => Info.Thumbprint.Substring(i * 4, 4)));

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RemoveText))]
    public partial bool ConfirmRemove { get; set; }

    public string RemoveText => ConfirmRemove ? T("Wirklich entfernen?") : T("Entfernen");
}
