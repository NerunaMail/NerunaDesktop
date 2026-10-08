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
internal sealed partial class CloudViewModel(CloudController cloud, AccountDiscovery discovery) : ViewModelBase
{
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
        Photo = null;
        if (Profile.Member.HasPhoto && await cloud.GetPhotoAsync() is { } bytes)
        {
            using var stream = new MemoryStream(bytes);
            Photo = new Bitmap(stream);
        }
    }
}
