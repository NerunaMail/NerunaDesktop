using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neruna.Core;
using Neruna.Core.Security;
using Neruna.Desktop.Infrastructure;

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
        foreach (var path in await files.PickFilesAsync("Zertifikat importieren (.p12, .pfx, .cer, .crt, .pem, .p7b)"))
        {
            byte[] data;
            try
            {
                data = await File.ReadAllBytesAsync(path);
            }
            catch (IOException ex)
            {
                Show($"«{Path.GetFileName(path)}» konnte nicht gelesen werden: {ex.Message}", error: true);
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
                ? $"Zertifikat «{imported[0].SubjectName}» importiert."
                : $"{imported.Count} Zertifikate aus «{fileName}» importiert.", error: false);
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
        var target = await files.SaveFileAsync("Öffentliches Zertifikat exportieren", FileService.SanitizeFileName(item.FileBaseName) + ".cer");
        if (target is null)
        {
            return;
        }

        await using (target)
        {
            await target.WriteAsync(await manager.ExportPublicAsync(item.Info.Thumbprint));
        }

        Show("Öffentliches Zertifikat exportiert (enthält keinen privaten Schlüssel).", error: false);
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
        Show($"Zertifikat «{item.Name}» entfernt.", error: false);
        await ReloadAsync();
    }

    private void Show(string text, bool error)
    {
        Message = text;
        MessageIsError = error;
    }
}

internal sealed partial class CertificateItem(CertificateInfo info, DateTimeOffset now) : ObservableObject
{
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

    public string Issuer => "Ausgestellt von " + Info.IssuerName;

    public string ValidUntil => Info.NotAfter.LocalDateTime.ToString("dd.MM.yyyy", CultureInfo.CurrentCulture);

    public string ValidRange =>
        $"{Info.NotBefore.LocalDateTime.ToString("dd.MM.yyyy", CultureInfo.CurrentCulture)} – {ValidUntil}";

    public CertificateStatus Status => Info.StatusAt(now);

    public string StatusText => Status switch
    {
        CertificateStatus.Valid => "Gültig",
        CertificateStatus.ExpiringSoon => $"Läuft in {Math.Max(0, (int)(Info.NotAfter - now).TotalDays)} Tagen ab",
        CertificateStatus.Expired => "Abgelaufen",
        _ => "Noch nicht gültig",
    };

    public string RemainingText => Status switch
    {
        CertificateStatus.Valid => $"noch {(int)(Info.NotAfter - now).TotalDays} Tage",
        CertificateStatus.Expired => $"seit {(int)(now - Info.NotAfter).TotalDays} Tagen",
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
                return "Zertifizierungsstelle (vertrauenswürdig)";
            }

            var usages = new List<string>();
            if (Info.CanSign)
            {
                usages.Add(Info.HasPrivateKey ? "Signieren" : "Signaturen prüfen");
            }

            if (Info.CanEncrypt)
            {
                usages.Add(Info.HasPrivateKey ? "Entschlüsseln" : "Verschlüsseln an");
            }

            return usages.Count > 0 ? string.Join(" · ", usages) : "nicht für E-Mail geeignet";
        }
    }

    public string SourceText => Info.Source switch
    {
        CertificateSource.CollectedFromMail => "Aus signierter Nachricht übernommen am " + Info.AddedAt.LocalDateTime.ToString("dd.MM.yyyy", CultureInfo.CurrentCulture),
        CertificateSource.Cloud => "Cloud – von der Organisation bereitgestellt am " + Info.AddedAt.LocalDateTime.ToString("dd.MM.yyyy", CultureInfo.CurrentCulture),
        _ => "Importiert am " + Info.AddedAt.LocalDateTime.ToString("dd.MM.yyyy", CultureInfo.CurrentCulture),
    };

    /// <summary>SHA-1 fingerprint in groups of four, as Windows shows it.</summary>
    public string Fingerprint => string.Join(' ', Enumerable.Range(0, Info.Thumbprint.Length / 4).Select(i => Info.Thumbprint.Substring(i * 4, 4)));

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RemoveText))]
    public partial bool ConfirmRemove { get; set; }

    public string RemoveText => ConfirmRemove ? "Wirklich entfernen?" : "Entfernen";
}
