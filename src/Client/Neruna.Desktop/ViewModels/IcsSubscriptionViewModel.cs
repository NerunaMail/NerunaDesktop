using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neruna.Core.Accounts;
using Neruna.Core.Providers;
using Neruna.Providers.Ics;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.ViewModels;

/// <summary>Subscribe to an internet calendar (ICS/webcal) – a second calendar provider next to CalDAV.</summary>
internal sealed partial class IcsSubscriptionViewModel(AccountSetupService setup, Infrastructure.IFileService files) : ViewModelBase
{
    public event EventHandler<bool>? Finished;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubscribeCommand))]
    public partial string Url { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubscribeCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    [RelayCommand(CanExecute = nameof(CanSubscribe))]
    private async Task SubscribeAsync()
    {
        Error = null;
        Uri url;
        try
        {
            url = IcsProviderFactory.NormalizeUrl(Url.Trim());
        }
        catch (Exception ex) when (ex is UriFormatException or ArgumentException)
        {
            Error = T("Bitte eine gültige https- oder webcal-Adresse oder eine .ics-Datei angeben.");
            return;
        }

        var settings = new Dictionary<string, string> { [IcsCalendarProvider.UrlSetting] = url.AbsoluteUri };
        if (!string.IsNullOrWhiteSpace(Name))
        {
            settings[IcsCalendarProvider.NameSetting] = Name.Trim();
        }

        var title = string.IsNullOrWhiteSpace(Name) ? IcsProviderFactory.FallbackName(url) : Name.Trim();
        var account = new Account(Guid.NewGuid(), title, null, [new ServiceConnection(Guid.NewGuid(), ServiceKind.Calendar, ProviderIds.Ics, settings)]);

        IsBusy = true;
        try
        {
            await setup.CreateAsync(account, password: null);
            Finished?.Invoke(this, true);
        }
        catch (AccountSetupException ex)
        {
            Error = T("Kalender konnte nicht geladen werden: ") + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanSubscribe() => !IsBusy && Url.Trim().Length > 0;

    /// <summary>"Datei wählen …": a calendar file on this computer or a network drive instead of a web address.</summary>
    [RelayCommand]
    private async Task PickFileAsync()
    {
        if (await files.PickFileAsync(T("Kalenderdatei wählen"), T("iCalendar-Dateien"), ["*.ics", "*.ical", "*.ifb"]) is { } path)
        {
            Url = path;
            if (string.IsNullOrWhiteSpace(Name))
            {
                Name = Path.GetFileNameWithoutExtension(path);
            }
        }
    }

    [RelayCommand]
    private void Cancel() => Finished?.Invoke(this, false);
}
