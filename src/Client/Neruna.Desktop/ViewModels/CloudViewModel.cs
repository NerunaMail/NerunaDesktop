using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neruna.Contracts.Cloud;
using Neruna.Core.Cloud;
using Neruna.Core.Discovery;

namespace Neruna.Desktop.ViewModels;

/// <summary>
/// Einstellungen → Cloud: connect Neruna with the organisation's Neruna Cloud/Control (server, one-time code and PIN from
/// the portal), show the profile kept there, disconnect.
/// </summary>
internal sealed partial class CloudViewModel(
    CloudController cloud,
    CloudSignatureSync signatures,
    CloudTextTemplateSync textTemplates,
    CloudCertificateSync certificates,
    AccountDiscovery discovery,
    SettingsBackupService backup) : ViewModelBase
{
    // ── Sicherung: manual backups of the personal settings into the vault (zero knowledge) ──

    /// <summary>A backup was restored: everything has to be reloaded and synced.</summary>
    public event EventHandler? Restored;

    /// <summary>A backup was made (the hint "Neue Sicherung erstellen?" can go).</summary>
    public event EventHandler? BackedUp;

    public ObservableCollection<BackupItem> Backups { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowVaultSetup), nameof(ShowVaultUnlock), nameof(ShowBackups))]
    public partial BackupOverview? BackupState { get; set; }

    [ObservableProperty]
    public partial bool BackupBusy { get; set; }

    [ObservableProperty]
    public partial string? BackupError { get; set; }

    [ObservableProperty]
    public partial string? BackupMessage { get; set; }

    [ObservableProperty]
    public partial string VaultPassword { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string VaultPasswordRepeat { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string UnlockSecret { get; set; } = string.Empty;

    /// <summary>Shown once after setting up; nothing else until the user confirmed it is stored.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowVaultSetup), nameof(ShowVaultUnlock), nameof(ShowBackups), nameof(HasRecoveryCode))]
    public partial string? RecoveryCode { get; set; }

    [ObservableProperty]
    public partial bool RecoveryStored { get; set; }

    [ObservableProperty]
    public partial bool IsWritingNote { get; set; }

    [ObservableProperty]
    public partial string BackupNote { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRestorePlan), nameof(RestoreTitle), nameof(RestoreIsEmpty))]
    public partial RestorePlan? RestorePlan { get; set; }

    public ObservableCollection<RestoreGroup> RestoreGroups { get; } = [];

    [ObservableProperty]
    public partial bool ConfirmReset { get; set; }

    public bool HasRecoveryCode => RecoveryCode is not null;

    public bool ShowVaultSetup => BackupState is { HasVault: false } && RecoveryCode is null;

    public bool ShowVaultUnlock => BackupState is { HasVault: true, IsUnlocked: false } && RecoveryCode is null;

    public bool ShowBackups => BackupState is { HasVault: true, IsUnlocked: true } && RecoveryCode is null;

    public bool HasRestorePlan => RestorePlan is not null;

    public bool RestoreIsEmpty => RestorePlan is { Changes.Count: 0 };

    public string? RestoreTitle => RestorePlan is { } plan
        ? $"Sicherung vom {plan.Backup.CreatedAt.ToLocalTime():dd.MM.yyyy HH:mm}{(plan.Backup.Note is { } note ? $" – «{note}»" : string.Empty)} wiederherstellen?"
        : null;

    public string BackupCountText => BackupState is { } state ? $"{state.Backups.Count} von {state.MaxBackups} Sicherungen – bei einer weiteren wird die älteste gelöscht." : string.Empty;

    /// <summary>Loads the vault state from the cloud (when connected).</summary>
    public async Task LoadBackupsAsync()
    {
        if (Connection is null)
        {
            BackupState = null;
            Backups.Clear();
            return;
        }

        await RunBackupAsync(async () => ShowOverview(await backup.GetOverviewAsync()));
    }

    /// <summary>From the hint "Neue Sicherung erstellen?": opens the note field.</summary>
    public void StartBackup()
    {
        BackupMessage = null;
        IsWritingNote = true;
    }

    [RelayCommand]
    private Task SetUpVaultAsync() => RunBackupAsync(async () =>
    {
        if (VaultPassword.Length < 10)
        {
            throw new BackupException("Das Tresor-Passwort braucht mindestens 10 Zeichen.");
        }

        if (VaultPassword != VaultPasswordRepeat)
        {
            throw new BackupException("Die beiden Passwörter stimmen nicht überein.");
        }

        RecoveryCode = await backup.SetUpAsync(VaultPassword);
        VaultPassword = VaultPasswordRepeat = string.Empty;
        RecoveryStored = false;
        ShowOverview(await backup.GetOverviewAsync());
    });

    [RelayCommand]
    private void ConfirmRecoveryCode()
    {
        if (RecoveryStored)
        {
            RecoveryCode = null;
            StartBackup();
        }
    }

    [RelayCommand]
    private Task UnlockVaultAsync() => RunBackupAsync(async () =>
    {
        await backup.UnlockAsync(UnlockSecret.Trim());
        UnlockSecret = string.Empty;
        ShowOverview(await backup.GetOverviewAsync());
    });

    [RelayCommand]
    private void NewBackup() => StartBackup();

    [RelayCommand]
    private void CancelBackup()
    {
        IsWritingNote = false;
        BackupNote = string.Empty;
    }

    [RelayCommand]
    private Task CreateBackupAsync() => RunBackupAsync(async () =>
    {
        var created = await backup.CreateBackupAsync(BackupNote);
        IsWritingNote = false;
        BackupNote = string.Empty;
        ShowOverview(await backup.GetOverviewAsync());
        BackupMessage = $"Sicherung erstellt ({created.Size / 1024.0:0.#} KB).";
        BackedUp?.Invoke(this, EventArgs.Empty);
    });

    [RelayCommand]
    private Task PrepareRestoreAsync(BackupItem item) => RunBackupAsync(async () =>
    {
        var plan = await backup.PrepareRestoreAsync(item.Entry.Id);
        RestoreGroups.Clear();
        foreach (var kind in new[] { BackupChangeKind.Added, BackupChangeKind.Changed, BackupChangeKind.Removed })
        {
            var changes = plan.Changes.Where(c => c.Kind == kind).ToList();
            if (changes.Count > 0)
            {
                RestoreGroups.Add(new RestoreGroup(kind, changes.Select(c => $"{c.Area}: {c.Name}").ToList()));
            }
        }

        RestorePlan = plan;
    });

    [RelayCommand]
    private void CancelRestore() => RestorePlan = null;

    [RelayCommand]
    private Task RestoreAsync() => RunBackupAsync(async () =>
    {
        if (RestorePlan is not { } plan)
        {
            return;
        }

        await backup.RestoreAsync(plan);
        RestorePlan = null;
        BackupMessage = "Sicherung wiederhergestellt. Die Konten werden jetzt neu synchronisiert.";
        Restored?.Invoke(this, EventArgs.Empty);
    });

    [RelayCommand]
    private Task DeleteBackupAsync(BackupItem item) => RunBackupAsync(async () =>
    {
        if (!item.ConfirmDelete)
        {
            item.ConfirmDelete = true;
            return;
        }

        await backup.DeleteBackupAsync(item.Entry.Id);
        ShowOverview(await backup.GetOverviewAsync());
    });

    /// <summary>Password and recovery code lost: delete the vault with every backup and start over (asked twice).</summary>
    [RelayCommand]
    private Task ResetVaultAsync() => RunBackupAsync(async () =>
    {
        if (!ConfirmReset)
        {
            ConfirmReset = true;
            return;
        }

        ConfirmReset = false;
        await backup.ResetAsync();
        ShowOverview(await backup.GetOverviewAsync());
    });

    private void ShowOverview(BackupOverview overview)
    {
        BackupState = overview;
        Backups.Clear();
        foreach (var entry in overview.Backups)
        {
            Backups.Add(new BackupItem(entry));
        }

        OnPropertyChanged(nameof(BackupCountText));
    }

    private async Task RunBackupAsync(Func<Task> action)
    {
        BackupError = null;
        BackupMessage = null;
        BackupBusy = true;
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is BackupException or CloudException or HttpRequestException)
        {
            BackupError = ex.Message;
        }
        finally
        {
            BackupBusy = false;
        }
    }

    /// <summary>Certificates from the organisation: how many arrived, whether the portal still has to approve this device.</summary>
    [ObservableProperty]
    public partial string? CertificatesText { get; set; }

    [ObservableProperty]
    public partial bool CertificatesNeedAttention { get; set; }

    /// <summary>After each certificate sync (here or in the background).</summary>
    public void UpdateCertificateStatus()
    {
        var status = certificates.Status;
        CertificatesNeedAttention = status.Problem is not null || (status.Available && !status.Approved);
        CertificatesText = !status.Available ? null
            : status.Problem ?? (!status.Approved
                ? "Zertifikate: Dieses Gerät wartet auf die Freigabe im Portal (Zertifikate → Ausstehende Freigaben)."
                : status.Waiting > 0
                    ? $"Zertifikate: {status.Received} auf diesem Gerät, {status.Waiting} warten noch auf die Zustellung im Portal."
                    : status.Received == 0
                        ? "Zertifikate: Ihnen ist noch kein Zertifikat zugeordnet."
                        : $"Zertifikate: {status.Received} von der Organisation auf diesem Gerät.");
    }

    public const string DefaultServer = "https://neruna.cloud";

    /// <summary>"AM" for "Anna Muster" (shown until the photo is there).</summary>
    public static readonly Avalonia.Data.Converters.IValueConverter Initials = new Avalonia.Data.Converters.FuncValueConverter<string?, string>(name =>
        string.Concat((name ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => char.ToUpperInvariant(w[0]))));

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotConnected))]
    public partial CloudConnection? Connection { get; set; }

    public bool IsNotConnected => Connection is null;

    [ObservableProperty]
    public partial string Server { get; set; } = DefaultServer;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    public partial string Code { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    public partial string Pin { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DeviceName { get; set; } = DeviceInfo.Current().Name;

    /// <summary>"Windows 11 (Build 26100) · AD\anna · Neruna 0.1.2" – what the portal will show about this computer.</summary>
    public string DeviceDescription { get; } = Describe(DeviceInfo.Current());

    private static string Describe(DeviceInfo info) => $"{info.OsName} {info.OsVersion} · angemeldet als {info.OsUser} · Neruna {info.AppVersion}";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand), nameof(RefreshCommand), nameof(DisconnectCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    [ObservableProperty]
    public partial MeResponse? Profile { get; set; }

    [ObservableProperty]
    public partial Bitmap? Photo { get; set; }

    public string? PersonName => Profile is { } p ? $"{p.Member.FirstName} {p.Member.LastName}" : Connection?.MemberName;

    public string? PersonDetails => Profile is { } p
        ? string.Join(" · ", new[] { p.Member.Position, p.Member.Department }.Where(s => !string.IsNullOrWhiteSpace(s)))
        : null;

    public string? Phones => Profile is { } p
        ? string.Join("   ", new[] { p.Member.PhoneDirect is { Length: > 0 } d ? "Direkt " + d : null, p.Member.PhoneMobile is { Length: > 0 } m ? "Mobile " + m : null }.OfType<string>())
        : null;

    public string? Address => Profile is { } p
        ? string.Join(", ", new[] { p.Organization.Street, string.Join(" ", new[] { p.Organization.PostalCode, p.Organization.City }.Where(s => !string.IsNullOrWhiteSpace(s))) }.Where(s => !string.IsNullOrWhiteSpace(s)))
        : null;

    public string? OrganizationContact => Profile is { } p
        ? string.Join(" · ", new[] { p.Organization.Phone, p.Organization.Email, p.Organization.Website }.Where(s => !string.IsNullOrWhiteSpace(s)))
        : null;

    public string? ConnectedText => Connection is { } c
        ? $"Verbunden mit {c.Server.Host} seit {c.ConnectedAt.LocalDateTime:d.M.yyyy} · dieses Gerät heisst dort «{Profile?.Device.Name ?? DeviceName}»"
        : null;

    partial void OnProfileChanged(MeResponse? value)
    {
        OnPropertyChanged(nameof(PersonName));
        OnPropertyChanged(nameof(PersonDetails));
        OnPropertyChanged(nameof(Phones));
        OnPropertyChanged(nameof(Address));
        OnPropertyChanged(nameof(OrganizationContact));
        OnPropertyChanged(nameof(ConnectedText));
    }

    partial void OnConnectionChanged(CloudConnection? value)
    {
        // The organisation's server also answers "which mail server?" when an account is added.
        discovery.OrganizationServer = value?.Server;
        OnPropertyChanged(nameof(PersonName));
        OnPropertyChanged(nameof(ConnectedText));
    }

    public async Task ReloadAsync()
    {
        Connection = await cloud.GetConnectionAsync();
        if (Connection is not null && Profile is null)
        {
            await RefreshAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        Error = null;
        IsBusy = true;
        try
        {
            var server = CloudController.ParseServer(Server);
            Connection = await cloud.ConnectAsync(server, Code, Pin, DeviceInfo.Current(DeviceName));
            Code = string.Empty;
            Pin = string.Empty;
            await LoadProfileAsync();
        }
        catch (CloudException ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanConnect() => !IsBusy && Code.Trim().Length >= 20 && Pin.Trim().Length >= 6;

    [RelayCommand(CanExecute = nameof(CanUse))]
    private async Task RefreshAsync()
    {
        Error = null;
        IsBusy = true;
        try
        {
            await LoadProfileAsync();
        }
        catch (CloudException ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Ends the connection (asked first in the view).</summary>
    [RelayCommand(CanExecute = nameof(CanUse))]
    private async Task DisconnectAsync()
    {
        IsBusy = true;
        try
        {
            await cloud.DisconnectAsync();
            await signatures.RemoveAllAsync();
            await textTemplates.RemoveAllAsync();
            await certificates.RemoveAllAsync();
            await backup.ForgetAsync();
            BackupState = null;
            Backups.Clear();
            RestorePlan = null;
            UpdateCertificateStatus();
            Connection = null;
            Profile = null;
            Photo = null;
            Error = null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanUse() => !IsBusy;

    private async Task LoadProfileAsync()
    {
        Profile = await cloud.GetProfileAsync();
        await signatures.SyncAsync();
        await textTemplates.SyncAsync();
        try
        {
            await certificates.SyncAsync();
        }
        catch (Exception ex) when (ex is CloudException or HttpRequestException)
        {
            Error = "Zertifikate konnten nicht abgeglichen werden: " + ex.Message;
        }

        UpdateCertificateStatus();
        await LoadBackupsAsync();
        Photo = null;
        if (Profile.Member.HasPhoto && await cloud.GetPhotoAsync() is { } bytes)
        {
            using var stream = new MemoryStream(bytes);
            Photo = new Bitmap(stream);
        }
    }
}

internal sealed partial class BackupItem(BackupEntry entry) : ObservableObject
{
    public BackupEntry Entry { get; } = entry;

    public string When => Entry.CreatedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm", System.Globalization.CultureInfo.GetCultureInfo("de-CH"));

    public string Details => $"{Entry.DeviceName ?? "Unbekanntes Gerät"} · {Entry.Size / 1024.0:0.#} KB";

    public string Note => string.IsNullOrWhiteSpace(Entry.Note) ? "(ohne Kommentar)" : Entry.Note;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeleteText))]
    public partial bool ConfirmDelete { get; set; }

    public string DeleteText => ConfirmDelete ? "Wirklich löschen?" : "Löschen";
}

/// <summary>Restore preview: what is created, changed or removed here.</summary>
internal sealed record RestoreGroup(BackupChangeKind Kind, IReadOnlyList<string> Items)
{
    public string Title => Kind switch
    {
        BackupChangeKind.Added => "Wird erstellt",
        BackupChangeKind.Changed => "Wird überschrieben",
        _ => "Wird entfernt",
    };

    public bool IsRemoval => Kind == BackupChangeKind.Removed;
}
