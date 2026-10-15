using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neruna.Contracts.Cloud;
using Neruna.Core.Cloud;
using Neruna.Core.Discovery;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.ViewModels;

/// <summary>
/// Einstellungen → Cloud: connect Neruna with the Neruna Cloud/Control of one or more organisations (server, one-time
/// code and PIN from the portal), show the profile kept there, choose whose chat is shown and where the backups go,
/// disconnect. The selected connection (<see cref="Connection"/>) is the one shown in detail.
/// </summary>
internal sealed partial class CloudViewModel(
    CloudController cloud,
    CloudSignatureSync signatures,
    CloudTextTemplateSync textTemplates,
    CloudCertificateSync certificates,
    AccountDiscovery discovery,
    SettingsBackupService backup,
    CloudAccountSync accounts,
    CloudPairingService pairing,
    Neruna.Core.Chat.ChatController chat) : ViewModelBase
{
    // ── Handy verbinden: QR code + PIN for the Neruna app; this desktop then re-encrypts for the phone ──

    private Action? _cancelPairing;

    /// <summary>The open pairing (QR code and PIN are shown), or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPairing))]
    public partial PairingSession? PairingSession { get; set; }

    public bool IsPairing => PairingSession is not null;

    [ObservableProperty]
    public partial Avalonia.Media.Imaging.Bitmap? PairingQrCode { get; set; }

    [ObservableProperty]
    public partial string? PairingText { get; set; }

    [ObservableProperty]
    public partial bool PairingFailed { get; set; }

    /// <summary>Before the QR code: the computer login (like an admin prompt) – nobody connects a phone without knowing it.</summary>
    public Infrastructure.LoginGate Login { get; } = new();

    [RelayCommand]
    private async Task StartPairingAsync()
    {
        PairingText = null;
        PairingFailed = false;
        if (Connection is not null && await Login.VerifyAsync(T("Zum Verbinden eines Handys bitte mit dem Login dieses Computers bestätigen.")))
        {
            await BeginPairingAsync();
        }
    }

    private async Task BeginPairingAsync()
    {
        PairingText = null;
        PairingFailed = false;
        using var cancel = new CancellationTokenSource();
        _cancelPairing = cancel.Cancel;
        try
        {
            var connection = Connection ?? throw new CloudException("not_connected", T("Neruna ist nicht mit einer Cloud verbunden."));
            var session = await pairing.StartAsync(connection, cancel.Token);
            var png = new QRCoder.PngByteQRCode(new QRCoder.QRCodeGenerator().CreateQrCode(session.Invitation.ToString(), QRCoder.QRCodeGenerator.ECCLevel.M)).GetGraphic(8);
            using (var stream = new MemoryStream(png))
            {
                PairingQrCode = new Avalonia.Media.Imaging.Bitmap(stream);
            }

            PairingSession = session;
            PairingText = F("Gültig bis {0}. Warte auf das Handy …", session.ExpiresAt.LocalDateTime.ToString("HH:mm", System.Globalization.CultureInfo.CurrentCulture));
            var phone = await pairing.WaitAsync(session, cancel.Token);
            PairingSession = null;
            PairingQrCode = null;
            if (phone is null)
            {
                PairingFailed = true;
                PairingText = T("Der Code ist abgelaufen. Bitte neu starten.");
                return;
            }

            PairingText = F("«{0}» ist verbunden. Zertifikate und Konten werden übertragen, sobald die App ihren Schlüssel gemeldet hat.", phone.Name);
            // The app reports its key right after connecting; deliver as soon as it is there.
            for (var attempt = 0; attempt < 30; attempt++)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancel.Token);
                var paired = (await cloud.GetPairedDevicesAsync(connection, cancel.Token)).Devices.FirstOrDefault(d => d.Id == phone.Id);
                if (paired?.EncryptionPublicKey is not null && paired.PairingProof is not null)
                {
                    await pairing.DeliverAsync(cancel.Token);
                    PairingText = F("«{0}» ist verbunden und eingerichtet.", phone.Name);
                    await LoadPairedPhonesAsync();
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            PairingSession = null;
            PairingQrCode = null;
            PairingText = null;
        }
        catch (Exception ex) when (ex is CloudException or HttpRequestException)
        {
            PairingSession = null;
            PairingQrCode = null;
            PairingFailed = true;
            PairingText = ex.Message;
        }
        finally
        {
            _cancelPairing = null;
        }
    }

    [RelayCommand]
    private void CancelPairing() => _cancelPairing?.Invoke();

    /// <summary>The phones paired from this computer (removing one is up to an admin in the portal).</summary>
    public ObservableCollection<PairedPhone> PairedPhones { get; } = [];

    [ObservableProperty]
    public partial string? PairingLimitsText { get; set; }

    private async Task LoadPairedPhonesAsync()
    {
        PairedPhones.Clear();
        PairingLimitsText = null;
        if (Connection is not { } connection)
        {
            return;
        }

        try
        {
            var paired = await cloud.GetPairedDevicesAsync(connection);
            foreach (var phone in paired.Devices)
            {
                PairedPhones.Add(new PairedPhone(
                    phone.Name,
                    string.Join(" · ", new[]
                    {
                        string.Join(' ', new[] { phone.OsName, phone.OsVersion }.Where(p => !string.IsNullOrWhiteSpace(p))),
                        phone.ConnectedAt is { } at ? F("verbunden am {0}", at.LocalDateTime.ToString("d", System.Globalization.CultureInfo.CurrentCulture)) : null,
                        phone.Approved
                            ? (phone.Missing.Certificates.Count + phone.Missing.Accounts.Count > 0 ? T("wird beim nächsten Abgleich ergänzt") : T("eingerichtet"))
                            : T("wartet auf diesen Computer"),
                    }.Where(p => !string.IsNullOrWhiteSpace(p)))));
            }

            PairingLimitsText = paired.Limits is not { } limits ? null
                : limits.MobileDevicesAllowed == 0
                    ? T("Für Sie sind keine mobilen Geräte freigegeben. Ein Admin kann sie im Portal unter Benutzer erlauben.")
                    : F("Mobile Geräte: {0} von {1} erlaubten verbunden.", limits.MobileDevicesAllowed - limits.MobileDevicesLeft, limits.MobileDevicesAllowed)
                      + (limits.DeviceLimit is { } max ? " " + F("Geräte insgesamt: {0} von {1}.", limits.DevicesInUse, max) : string.Empty);
        }
        catch (Exception ex) when (ex is CloudException or HttpRequestException)
        {
            PairingLimitsText = null; // older server or offline: the list just stays empty
        }
    }

    /// <summary>Accounts of the organisation were set up, changed or removed (the shell reloads and syncs).</summary>
    public event EventHandler? AccountsArrived;

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
        ? (plan.Backup.Note is { } note
            ? F("Sicherung vom {0:g} – «{1}» wiederherstellen?", plan.Backup.CreatedAt.ToLocalTime(), note)
            : F("Sicherung vom {0:g} wiederherstellen?", plan.Backup.CreatedAt.ToLocalTime()))
        : null;

    public string BackupCountText => BackupState is { } state ? F("{0} von {1} Sicherungen – bei einer weiteren wird die älteste gelöscht.", state.Backups.Count, state.MaxBackups) : string.Empty;

    /// <summary>Loads the vault state from the cloud (when connected).</summary>
    public async Task LoadBackupsAsync()
    {
        if (BackupConnection is null)
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
            throw new BackupException(T("Das Tresor-Passwort braucht mindestens 10 Zeichen."));
        }

        if (VaultPassword != VaultPasswordRepeat)
        {
            throw new BackupException(T("Die beiden Passwörter stimmen nicht überein."));
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
        BackupMessage = F("Sicherung erstellt ({0:0.#} KB).", created.Size / 1024.0);
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
        BackupMessage = T("Sicherung wiederhergestellt. Die Konten werden jetzt neu synchronisiert.");
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
        var status = Connection is { } connection ? certificates.StatusOf(connection.Id) : CloudCertificateStatus.None;
        CertificatesNeedAttention = status.Problem is not null || (status.Available && !status.Approved);
        CertificatesText = !status.Available ? null
            : status.Problem ?? (!status.Approved
                ? T("Zertifikate: Dieses Gerät wartet auf die Freigabe im Portal (Freigaben).")
                : status.Waiting > 0
                    ? F("Zertifikate: {0} auf diesem Gerät, {1} warten noch auf die Zustellung im Portal.", status.Received, status.Waiting)
                    : status.Received == 0
                        ? T("Zertifikate: Ihnen ist noch kein Zertifikat zugeordnet.")
                        : F("Zertifikate: {0} von der Organisation auf diesem Gerät.", status.Received));

        var mail = Connection is { } selected ? accounts.StatusOf(selected.Id) : CloudAccountStatus.None;
        AccountsNeedAttention = mail.Problem is not null || mail.Waiting > 0 || mail.Pending.Count > 0 || mail.Elsewhere > 0;
        AccountsText = mail.Problem
            ?? (mail.Received + mail.Waiting + mail.Pending.Count + mail.Elsewhere == 0 ? null
                : string.Join(" ", new[]
                {
                    mail.Received > 0 ? F("E-Mail-Konten: {0} von der Organisation eingerichtet.", mail.Received) : T("E-Mail-Konten:"),
                    mail.Waiting > 0 ? F("{0} warten auf die Freigabe dieses Geräts im Portal.", mail.Waiting) : null,
                    mail.Pending.Count > 0 ? F("{0} brauchen noch Ihr Passwort.", mail.Pending.Count) : null,
                    mail.Elsewhere > 0 ? F("{0} nicht eingerichtet: Die Adresse kommt bereits von einer anderen Organisation.", mail.Elsewhere) : null,
                }.Where(t => t is not null)));
    }

    /// <summary>Mail accounts from the organisation: how many are set up, waiting for approval or for a password.</summary>
    [ObservableProperty]
    public partial string? AccountsText { get; set; }

    [ObservableProperty]
    public partial bool AccountsNeedAttention { get; set; }

    public const string DefaultServer = "https://neruna.cloud";

    /// <summary>"AM" for "Anna Muster" (shown until the photo is there).</summary>
    public static readonly Avalonia.Data.Converters.IValueConverter Initials = new Avalonia.Data.Converters.FuncValueConverter<string?, string>(name =>
        string.Concat((name ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => char.ToUpperInvariant(w[0]))));

    /// <summary>The organisations this computer is connected to.</summary>
    public ObservableCollection<CloudConnection> Connections { get; } = [];

    /// <summary>The connection shown in detail (profile, phones, status).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotConnected))]
    public partial CloudConnection? Connection { get; set; }

    public bool IsNotConnected => Connections.Count == 0;

    /// <summary>Connected, and the form for another organisation is open.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowConnectForm))]
    public partial bool IsAdding { get; set; }

    public bool ShowConnectForm => IsNotConnected || IsAdding;

    public bool HasSeveral => Connections.Count > 1;

    /// <summary>Whose chat is shown (only offered with several connections).</summary>
    [ObservableProperty]
    public partial CloudConnection? ChatConnection { get; set; }

    /// <summary>Where the backups go (only offered with several connections).</summary>
    [ObservableProperty]
    public partial CloudConnection? BackupConnection { get; set; }

    public string? PairingOrganizationText => HasSeveral && Connection is { } c ? F("Das Handy wird mit {0} verbunden.", c.OrganizationName) : null;

    public string? BackupOrganizationText => HasSeveral && BackupConnection is { } c ? F("Die Sicherungen liegen bei {0}.", c.OrganizationName) : null;

    private bool _loadingConnections;

    [RelayCommand]
    private void AddConnection()
    {
        Error = null;
        IsAdding = true;
    }

    [RelayCommand]
    private void CancelAdding()
    {
        Error = null;
        IsAdding = false;
    }

    partial void OnChatConnectionChanged(CloudConnection? oldValue, CloudConnection? newValue)
    {
        if (!_loadingConnections && newValue is not null && newValue.Id != oldValue?.Id)
        {
            _ = SwitchChatAsync(oldValue, newValue);
        }
    }

    private async Task SwitchChatAsync(CloudConnection? previous, CloudConnection chosen)
    {
        try
        {
            await cloud.SetChatConnectionAsync(chosen.Id);
            await chat.SwitchAsync(previous);
        }
        catch (Exception ex) when (ex is CloudException or HttpRequestException)
        {
            Error = ex.Message;
        }

        ChatChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Another organisation's chat was chosen (the chat page reloads).</summary>
    public event EventHandler? ChatChanged;

    partial void OnBackupConnectionChanged(CloudConnection? oldValue, CloudConnection? newValue)
    {
        OnPropertyChanged(nameof(BackupOrganizationText));
        if (!_loadingConnections && newValue is not null && newValue.Id != oldValue?.Id)
        {
            _ = SwitchBackupAsync(newValue);
        }
    }

    // The vault key of the other organisation's vault does not open this one: forget it, unlock (or set up) anew.
    private async Task SwitchBackupAsync(CloudConnection chosen)
    {
        await cloud.SetBackupConnectionAsync(chosen.Id);
        await backup.ForgetAsync();
        RestorePlan = null;
        IsWritingNote = false;
        await LoadBackupsAsync();
        BackedUp?.Invoke(this, EventArgs.Empty);
    }

    private async Task LoadConnectionsAsync(string? select = null)
    {
        _loadingConnections = true;
        try
        {
            var all = await cloud.GetConnectionsAsync();
            var selected = select ?? Connection?.Id;
            Connections.Clear();
            foreach (var connection in all)
            {
                Connections.Add(connection);
            }

            Connection = all.FirstOrDefault(c => c.Id == selected) ?? all.FirstOrDefault();
            ChatConnection = await cloud.GetChatConnectionAsync();
            BackupConnection = await cloud.GetBackupConnectionAsync();

            // The organisations' servers also answer "which mail server?" when an account is added.
            discovery.OrganizationServers = [.. all.Select(c => c.Server)];
        }
        finally
        {
            _loadingConnections = false;
        }

        OnPropertyChanged(nameof(IsNotConnected));
        OnPropertyChanged(nameof(ShowConnectForm));
        OnPropertyChanged(nameof(HasSeveral));
        OnPropertyChanged(nameof(BackupOrganizationText));
        OnPropertyChanged(nameof(PairingOrganizationText));
    }

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

    private static string Describe(DeviceInfo info) => F("{0} {1} · angemeldet als {2} · Neruna {3}", info.OsName, info.OsVersion, info.OsUser, info.AppVersion);

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
        ? string.Join("   ", new[] { p.Member.PhoneDirect is { Length: > 0 } d ? T("Direkt ") + d : null, p.Member.PhoneMobile is { Length: > 0 } m ? T("Mobile ") + m : null }.OfType<string>())
        : null;

    public string? Address => Profile is { } p
        ? string.Join(", ", new[] { p.Organization.Street, string.Join(" ", new[] { p.Organization.PostalCode, p.Organization.City }.Where(s => !string.IsNullOrWhiteSpace(s))) }.Where(s => !string.IsNullOrWhiteSpace(s)))
        : null;

    public string? OrganizationContact => Profile is { } p
        ? string.Join(" · ", new[] { p.Organization.Phone, p.Organization.Email, p.Organization.Website }.Where(s => !string.IsNullOrWhiteSpace(s)))
        : null;

    public string? ConnectedText => Connection is { } c
        ? F("Verbunden mit {0} seit {1:d} · dieses Gerät heisst dort «{2}»", c.Server.Host, c.ConnectedAt.LocalDateTime, Profile?.Device.Name ?? DeviceName)
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

    partial void OnConnectionChanged(CloudConnection? oldValue, CloudConnection? newValue)
    {
        OnPropertyChanged(nameof(PairingOrganizationText));
        OnPropertyChanged(nameof(PersonName));
        OnPropertyChanged(nameof(ConnectedText));
        if (newValue?.Id != oldValue?.Id)
        {
            Profile = null;
            Photo = null;
            UpdateCertificateStatus();
            if (!_loadingConnections && newValue is not null)
            {
                _ = ShowSelectedAsync();
            }
        }
    }

    // Another organisation selected: its profile and phones (the syncs cover all of them anyway).
    private async Task ShowSelectedAsync()
    {
        Error = null;
        try
        {
            await LoadPersonAsync();
            await LoadPairedPhonesAsync();
        }
        catch (Exception ex) when (ex is CloudException or HttpRequestException)
        {
            Error = ex.Message;
        }
    }

    public async Task ReloadAsync()
    {
        await LoadConnectionsAsync();
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
            var connection = await cloud.ConnectAsync(server, Code, Pin, DeviceInfo.Current(DeviceName));
            Code = string.Empty;
            Pin = string.Empty;
            IsAdding = false;
            Server = DefaultServer;
            await LoadConnectionsAsync(connection.Id);
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

    /// <summary>Ends the selected connection (asked first in the view); what came from that organisation goes.</summary>
    [RelayCommand(CanExecute = nameof(CanUse))]
    private async Task DisconnectAsync()
    {
        if (Connection is not { } connection)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var keptBackups = BackupConnection?.Id == connection.Id;
            await cloud.DisconnectAsync(connection);
            await signatures.RemoveDisconnectedAsync();
            await textTemplates.RemoveDisconnectedAsync();
            await certificates.RemoveDisconnectedAsync();
            if (await accounts.RemoveDisconnectedAsync())
            {
                AccountsArrived?.Invoke(this, EventArgs.Empty);
            }

            if (keptBackups)
            {
                await backup.ForgetAsync();
                BackupState = null;
                Backups.Clear();
                RestorePlan = null;
            }

            Profile = null;
            Photo = null;
            Error = null;
            await LoadConnectionsAsync();
            UpdateCertificateStatus();
            if (Connection is not null)
            {
                await ShowSelectedAsync();
                if (keptBackups)
                {
                    await LoadBackupsAsync();
                }
            }

            ChatChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanUse() => !IsBusy;

    private async Task LoadProfileAsync()
    {
        await LoadPersonAsync();
        await LoadPairedPhonesAsync();
        await signatures.SyncAsync();
        await textTemplates.SyncAsync();
        try
        {
            await certificates.SyncAsync();
        }
        catch (Exception ex) when (ex is CloudException or HttpRequestException)
        {
            Error = T("Zertifikate konnten nicht abgeglichen werden: ") + ex.Message;
        }

        try
        {
            // Also when one only waits for its password: the shell asks for it.
            if (await accounts.SyncAsync() || accounts.Status.Pending.Count > 0)
            {
                AccountsArrived?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception ex) when (ex is CloudException or HttpRequestException)
        {
            Error = T("E-Mail-Konten der Organisation konnten nicht abgeglichen werden: ") + ex.Message;
        }

        UpdateCertificateStatus();
        await LoadBackupsAsync();
    }

    private async Task LoadPersonAsync()
    {
        if (Connection is not { } connection)
        {
            return;
        }

        var profile = await cloud.GetProfileAsync(connection);
        Bitmap? photo = null;
        if (profile.Member.HasPhoto && await cloud.GetPhotoAsync(connection) is { } bytes)
        {
            using var stream = new MemoryStream(bytes);
            photo = new Bitmap(stream);
        }

        // Still the one shown (the user may have selected another meanwhile).
        if (Connection?.Id == connection.Id)
        {
            Profile = profile;
            Photo = photo;
        }
    }
}

internal sealed partial class BackupItem(BackupEntry entry) : ObservableObject
{
    public BackupEntry Entry { get; } = entry;

    public string When => Entry.CreatedAt.ToLocalTime().ToString("g", Neruna.Core.Localization.Texts.Culture);

    public string Details => $"{Entry.DeviceName ?? T("Unbekanntes Gerät")} · {Entry.Size / 1024.0:0.#} KB";

    public string Note => string.IsNullOrWhiteSpace(Entry.Note) ? T("(ohne Kommentar)") : Entry.Note;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeleteText))]
    public partial bool ConfirmDelete { get; set; }

    public string DeleteText => ConfirmDelete ? T("Wirklich löschen?") : T("Löschen");
}

/// <summary>Restore preview: what is created, changed or removed here.</summary>
internal sealed record RestoreGroup(BackupChangeKind Kind, IReadOnlyList<string> Items)
{
    public string Title => Kind switch
    {
        BackupChangeKind.Added => T("Wird erstellt"),
        BackupChangeKind.Changed => T("Wird überschrieben"),
        _ => T("Wird entfernt"),
    };

    public bool IsRemoval => Kind == BackupChangeKind.Removed;
}

/// <summary>A phone in the list under "Handy verbinden".</summary>
internal sealed record PairedPhone(string Name, string Details);
