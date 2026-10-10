using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neruna.Core;
using Neruna.Core.Accounts;
using Neruna.Core.Calendar;
using Neruna.Core.Providers;
using Neruna.Providers.Special;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.ViewModels;

/// <summary>
/// "Spezieller Kalender": birthdays from the contacts (with the reminder asked here) or the public holidays of a
/// country and region. Both are computed by Neruna and stored like a subscription (an account with one calendar).
/// </summary>
internal sealed partial class SpecialCalendarViewModel : ViewModelBase
{
    private readonly AccountSetupService _setup;
    private readonly IReadOnlyList<Account> _existing;

    public SpecialCalendarViewModel(AccountSetupService setup, IReadOnlyList<Account> existing)
    {
        _setup = setup;
        _existing = existing;
        HasBirthdays = existing.Any(a => a.Connections.Any(c => c.ProviderId == ProviderIds.Birthdays));
        IsBirthdays = !HasBirthdays;
        IsHolidays = HasBirthdays;
        SelectedCountry = Countries[0];
    }

    public event EventHandler<bool>? Finished;

    /// <summary>A birthday calendar exists already – one is enough.</summary>
    public bool HasBirthdays { get; }

    public bool CanChooseBirthdays => !HasBirthdays;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    public partial bool IsBirthdays { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    public partial bool IsHolidays { get; set; }

    public IReadOnlyList<string> Reminders { get; } =
        [T("Keine Erinnerung"), T("Am Tag um 9:00"), T("Am Vortag um 9:00"), T("Eine Woche vorher um 9:00")];

    /// <summary>Index into <see cref="Reminders"/> (= <see cref="BirthdayReminder"/>); the day before by default.</summary>
    [ObservableProperty]
    public partial int ReminderIndex { get; set; } = (int)BirthdayReminder.DayBefore;

    public IReadOnlyList<HolidayCountry> Countries { get; } = Holidays.Countries;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Regions), nameof(HasRegions))]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    public partial HolidayCountry? SelectedCountry { get; set; }

    public IReadOnlyList<HolidayRegion> Regions => SelectedCountry?.Regions ?? [];

    public bool HasRegions => Regions.Count > 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    public partial HolidayRegion? SelectedRegion { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    partial void OnSelectedCountryChanged(HolidayCountry? value) => SelectedRegion = null;

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private async Task AddAsync()
    {
        Error = null;
        Account account;
        if (IsBirthdays)
        {
            var settings = new Dictionary<string, string> { [BirthdayCalendarProvider.ReminderSetting] = BirthdayCalendarProvider.Format((BirthdayReminder)ReminderIndex) };
            account = new Account(Guid.NewGuid(), T("Geburtstage"), null, [new ServiceConnection(Guid.NewGuid(), ServiceKind.Calendar, ProviderIds.Birthdays, settings)]);
        }
        else
        {
            var country = SelectedCountry!.Code;
            var region = SelectedRegion?.Code;
            if (_existing.SelectMany(a => a.Connections).Any(c => c.ProviderId == ProviderIds.Holidays
                    && c.Settings.GetValueOrDefault(HolidayCalendarProvider.CountrySetting) == country
                    && c.Settings.GetValueOrDefault(HolidayCalendarProvider.RegionSetting) == region))
            {
                Error = T("Dieser Feiertagskalender ist bereits vorhanden.");
                return;
            }

            var settings = new Dictionary<string, string> { [HolidayCalendarProvider.CountrySetting] = country };
            if (region is not null)
            {
                settings[HolidayCalendarProvider.RegionSetting] = region;
            }

            account = new Account(Guid.NewGuid(), F("Feiertage {0}", Holidays.DisplayName(country, region)), null,
                [new ServiceConnection(Guid.NewGuid(), ServiceKind.Calendar, ProviderIds.Holidays, settings)]);
        }

        IsBusy = true;
        try
        {
            await _setup.CreateAsync(account, password: null);
            Finished?.Invoke(this, true);
        }
        catch (AccountSetupException ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanAdd() => !IsBusy && ((IsBirthdays && !HasBirthdays) || (IsHolidays && SelectedCountry is not null && (!HasRegions || SelectedRegion is not null)));

    [RelayCommand]
    private void Cancel() => Finished?.Invoke(this, false);
}
