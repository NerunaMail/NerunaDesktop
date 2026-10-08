using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Neruna.Core;
using Neruna.Core.Calendar;

namespace Neruna.Desktop.ViewModels;

/// <summary>Settings → Kalender. Changes apply immediately to the calendar view.</summary>
internal sealed partial class CalendarOptionsViewModel(ISettingsStore settings, CalendarViewModel calendarPage) : ViewModelBase
{
    private bool _loading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OneMonth))]
    public partial bool TwoMonths { get; set; }

    public bool OneMonth
    {
        get => !TwoMonths;
        set => TwoMonths = !value;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ListLayout))]
    public partial bool TimeGrid { get; set; }

    public bool ListLayout
    {
        get => !TimeGrid;
        set => TimeGrid = !value;
    }

    /// <summary>Time grid subdivision: 60, 30 or 15 minutes.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Grid60), nameof(Grid30), nameof(Grid15))]
    public partial int GridMinutes { get; set; } = 30;

    public bool Grid60
    {
        get => GridMinutes == 60;
        set => SetGrid(value, 60);
    }

    public bool Grid30
    {
        get => GridMinutes == 30;
        set => SetGrid(value, 30);
    }

    public bool Grid15
    {
        get => GridMinutes == 15;
        set => SetGrid(value, 15);
    }

    private void SetGrid(bool selected, int minutes)
    {
        if (selected)
        {
            GridMinutes = minutes;
        }
    }

    partial void OnGridMinutesChanged(int value)
    {
        if (_loading)
        {
            return;
        }

        calendarPage.GridMinutes = value;
        _ = settings.SetAsync(SettingKeys.CalendarGridMinutes, value.ToString(CultureInfo.InvariantCulture));
    }

    partial void OnTimeGridChanged(bool value)
    {
        if (_loading)
        {
            return;
        }

        calendarPage.IsTimeGrid = value;
        _ = settings.SetAsync(SettingKeys.CalendarLayout, value ? "timegrid" : "list");
    }

    /// <summary>"Beantwortete Einladungen in den Papierkorb verschieben" (default on).</summary>
    [ObservableProperty]
    public partial bool DeleteAnsweredInvitations { get; set; } = true;

    partial void OnDeleteAnsweredInvitationsChanged(bool value)
    {
        if (!_loading)
        {
            _ = settings.SetBoolAsync(SettingKeys.DeleteAnsweredInvitations, value);
        }
    }

    /// <summary>"Standard-Erinnerung": for new events and accepted invitations.</summary>
    public static IReadOnlyList<ReminderOption> ReminderOptions => ReminderOption.Standard;

    [ObservableProperty]
    public partial ReminderOption? DefaultReminder { get; set; }

    partial void OnDefaultReminderChanged(ReminderOption? value)
    {
        if (!_loading && value is not null)
        {
            _ = settings.SetDefaultReminderAsync(value.Minutes);
        }
    }

    public async Task ReloadAsync()
    {
        _loading = true;
        try
        {
            DeleteAnsweredInvitations = await settings.GetBoolAsync(SettingKeys.DeleteAnsweredInvitations, fallback: true);
            var reminder = await settings.GetDefaultReminderAsync();
            DefaultReminder = ReminderOption.Standard.FirstOrDefault(o => o.Minutes == reminder) ?? ReminderOption.Standard.First(o => o.Minutes == EventDraft.DefaultReminderMinutes);
            TwoMonths = await settings.GetAsync(SettingKeys.CalendarNavigatorMonths) == "2";
            TimeGrid = await settings.GetAsync(SettingKeys.CalendarLayout) == "timegrid";
            GridMinutes = int.TryParse(await settings.GetAsync(SettingKeys.CalendarGridMinutes), out var minutes) && minutes is 60 or 30 or 15 ? minutes : 30;
        }
        finally
        {
            _loading = false;
        }
    }

    partial void OnTwoMonthsChanged(bool value)
    {
        if (_loading)
        {
            return;
        }

        var months = value ? 2 : 1;
        calendarPage.NavigatorMonthCount = months;
        _ = settings.SetAsync(SettingKeys.CalendarNavigatorMonths, months.ToString(CultureInfo.InvariantCulture));
    }
}
