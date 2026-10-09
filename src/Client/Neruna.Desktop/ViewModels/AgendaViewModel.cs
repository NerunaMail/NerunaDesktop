using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Neruna.Core;
using Neruna.Core.Calendar;

namespace Neruna.Desktop.ViewModels;

/// <summary>
/// "Tagesansicht" beside the mail: today's and the next days' appointments as a plain list, top to bottom. Whether it is
/// shown is remembered across restarts.
/// </summary>
internal sealed partial class AgendaViewModel : ViewModelBase
{
    /// <summary>Today and the following days.</summary>
    public const int Days = 7;

    private static CultureInfo Culture => Neruna.Core.Localization.Texts.Culture;
    private readonly CalendarController _calendar;
    private readonly ISettingsStore _settings;
    private readonly ILogger<AgendaViewModel> _logger;
    private readonly DispatcherTimer _clock;
    private bool _loadingState;
    private int _version;

    public AgendaViewModel(CalendarController calendar, ISettingsStore settings, ILogger<AgendaViewModel> logger)
    {
        _calendar = calendar;
        _settings = settings;
        _logger = logger;

        // Past appointments fade and "Heute" moves on at midnight without a sync.
        _clock = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _clock.Tick += async (_, _) => await ReloadAsync();
    }

    /// <summary>An appointment was clicked: open it in the calendar.</summary>
    public event EventHandler<CalendarOccurrence>? OpenRequested;

    [ObservableProperty]
    public partial bool IsOpen { get; set; }

    public ObservableCollection<object> Entries { get; } = [];

    /// <summary>Which calendars the agenda shows (its own choice, the calendar page is not affected).</summary>
    public ObservableCollection<AgendaCalendarChoice> Calendars { get; } = [];

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    /// <summary>Restores the remembered state (at start).</summary>
    public async Task LoadStateAsync()
    {
        _loadingState = true;
        IsOpen = await _settings.GetBoolAsync(SettingKeys.MailAgendaOpen, false);
        _loadingState = false;
        if (IsOpen)
        {
            await ReloadAsync();
        }
    }

    async partial void OnIsOpenChanged(bool value)
    {
        if (value)
        {
            _clock.Start();
            await ReloadAsync();
        }
        else
        {
            _clock.Stop();
        }

        if (!_loadingState)
        {
            await _settings.SetAsync(SettingKeys.MailAgendaOpen, value ? "true" : "false");
        }
    }

    /// <summary>Reads the appointments again (after a sync or a change); nothing to do while hidden.</summary>
    public async Task ReloadAsync()
    {
        if (!IsOpen)
        {
            return;
        }

        var version = ++_version;
        try
        {
            var now = DateTimeOffset.Now;
            var today = new DateTimeOffset(now.Date, now.Offset);
            var calendars = await _calendar.GetCalendarsAsync();
            var colors = await CalendarViewModel.ColorsAsync(_settings, calendars);
            var hidden = await HiddenAsync();
            var shown = calendars.Where(c => !hidden.Contains(Key(c))).ToList();
            var occurrences = await Task.Run(() => _calendar.GetOccurrencesAsync(shown, today, today.AddDays(Days)));
            if (version != _version)
            {
                return;
            }

            Calendars.Clear();
            foreach (var info in calendars)
            {
                var choice = new AgendaCalendarChoice(info, Avalonia.Media.Brush.Parse(colors[(info.ConnectionId, info.RemoteId)])) { IsShown = !hidden.Contains(Key(info)) };
                choice.PropertyChanged += async (_, _) => await SaveChoiceAsync();
                Calendars.Add(choice);
            }

            Entries.Clear();
            for (var i = 0; i < Days; i++)
            {
                var day = now.Date.AddDays(i);
                var items = occurrences
                    .Where(o => o.Start.LocalDateTime.Date <= day && (o.End.LocalDateTime > day || o.Start.LocalDateTime.Date == day))
                    .OrderByDescending(o => o.IsAllDay)
                    .ThenBy(o => o.Start)
                    .Select(o => new AgendaItem(o, colors.GetValueOrDefault((o.Calendar.ConnectionId, o.Calendar.RemoteId)) ?? "#0F6CBD", day, now))
                    .ToList();
                if (items.Count == 0)
                {
                    continue;
                }

                Entries.Add(new AgendaDayHeader(DayTitle(day, now.Date)));
                foreach (var item in items)
                {
                    Entries.Add(item);
                }
            }

            IsEmpty = Entries.Count == 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Loading the agenda failed");
        }
    }

    private static string Key(CalendarInfo info) => $"{info.ConnectionId:N}|{info.RemoteId}";

    private async Task<HashSet<string>> HiddenAsync()
    {
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<HashSet<string>>(await _settings.GetAsync(SettingKeys.MailAgendaHidden) ?? "[]") ?? [];
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }

    private async Task SaveChoiceAsync()
    {
        var hidden = Calendars.Where(c => !c.IsShown).Select(c => Key(c.Info)).ToList();
        await _settings.SetAsync(SettingKeys.MailAgendaHidden, System.Text.Json.JsonSerializer.Serialize(hidden));
        await ReloadAsync();
    }

    [RelayCommand]
    private void Open(AgendaItem? item)
    {
        if (item is not null)
        {
            OpenRequested?.Invoke(this, item.Occurrence);
        }
    }

    private static string DayTitle(DateTime day, DateTime today) => (day - today).Days switch
    {
        0 => "Heute · " + day.ToString("d. MMMM", Culture),
        1 => "Morgen · " + day.ToString("d. MMMM", Culture),
        _ => day.ToString("dddd, d. MMMM", Culture),
    };
}

internal sealed record AgendaDayHeader(string Title);

/// <summary>A calendar with its tick in the agenda's calendar choice.</summary>
internal sealed partial class AgendaCalendarChoice(CalendarInfo info, IBrush brush) : ObservableObject
{
    public CalendarInfo Info { get; } = info;

    public IBrush Brush { get; } = brush;

    public string Name => Info.Name;

    [ObservableProperty]
    public partial bool IsShown { get; set; } = true;
}

/// <summary>One appointment in the agenda: time, title, place, calendar colour; finished ones are faded.</summary>
internal sealed class AgendaItem(CalendarOccurrence occurrence, string color, DateTime day, DateTimeOffset now)
{
    public CalendarOccurrence Occurrence { get; } = occurrence;

    public IBrush Brush { get; } = Avalonia.Media.Brush.Parse(color);

    public string Title => string.IsNullOrWhiteSpace(Occurrence.Summary) ? "(ohne Titel)" : Occurrence.Summary;

    public string? Location => string.IsNullOrWhiteSpace(Occurrence.Location) ? null : Occurrence.Location;

    public bool HasLocation => Location is not null;

    public string Time
    {
        get
        {
            if (Occurrence.IsAllDay)
            {
                return "Ganztägig";
            }

            var start = Occurrence.Start.LocalDateTime;
            var end = Occurrence.End.LocalDateTime;
            // Over several days: the part on this day.
            var from = start.Date < day ? "00:00" : start.ToString("HH:mm", CultureInfo.InvariantCulture);
            var to = end.Date > day ? "24:00" : end.ToString("HH:mm", CultureInfo.InvariantCulture);
            return $"{from}–{to}";
        }
    }

    public bool IsPast { get; } = occurrence.End <= now;

    public bool IsNow { get; } = occurrence.Start <= now && occurrence.End > now && !occurrence.IsAllDay;

    public double Opacity => IsPast ? 0.5 : 1;
}
