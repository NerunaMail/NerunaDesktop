using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neruna.Core;
using Neruna.Core.Calendar;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.ViewModels;

/// <summary>Week view across all calendars, whatever provider they come from.</summary>
internal sealed partial class CalendarViewModel(CalendarController calendar, TaskController tasks, InvitationService invitations, ISettingsStore settings) : ViewModelBase, ICollectionListHost
{
    internal static readonly string[] Palette = ["#0F6CBD", "#C239B3", "#0B6A0B", "#CA5010", "#8764B8", "#038387"];

    /// <summary>Colors offered when the user picks a calendar color (readable on white).</summary>
    public static IReadOnlyList<string> ColorChoices { get; } =
    [
        "#0F6CBD", "#2B88D8", "#038387", "#00B7C3", "#0B6A0B", "#498205", "#8CBD18", "#C19C00",
        "#CA5010", "#D13438", "#A4262C", "#E3008C", "#C239B3", "#8764B8", "#5C2E91", "#69797E",
    ];

    public event EventHandler? SubscribeRequested;

    /// <summary>"Kalender verwalten": choose which server calendars are shown.</summary>
    public event EventHandler? ManageRequested;

    /// <summary>"Spezieller Kalender": birthdays or holidays.</summary>
    public event EventHandler? SpecialRequested;

    /// <summary>The shell shows the editor as an overlay and reloads the week when it reports a change.</summary>
    public event EventHandler<EventEditorViewModel>? EditorRequested;

    /// <summary>"Aufgaben im Kalender anzeigen" was changed here: the tasks page shows the same switch.</summary>
    public event EventHandler? TaskListsChanged;

    /// <summary>A task shown in the calendar was opened: the shell shows it under "Aufgaben".</summary>
    public event EventHandler<CalendarOccurrence>? TaskOpenRequested;

    public ObservableCollection<CalendarListItem> Calendars { get; } = [];

    public ObservableCollection<CalendarDay> Days { get; } = [];

    /// <summary>The date navigator: one or two months (setting "Kalender → Monatsübersicht").</summary>
    public ObservableCollection<MiniMonth> MiniMonths { get; } = [];

    public static IReadOnlyList<string> WeekdayHeaders => MiniMonth.WeekdayHeaders;

    /// <summary>First month shown in the navigator; it follows the week unless the user browses months.</summary>
    [ObservableProperty]
    public partial DateTime NavigatorMonth { get; set; } = new(DateTime.Today.Year, DateTime.Today.Month, 1);

    [ObservableProperty]
    public partial int NavigatorMonthCount { get; set; } = 1;

    /// <summary>Week as hourly time grid instead of a list per day (setting "Kalender → Darstellung").</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsListLayout))]
    public partial bool IsTimeGrid { get; set; }

    public bool IsListLayout => !IsTimeGrid;

    /// <summary>Monday–Friday only (toolbar toggle "Mo–Fr"), otherwise the full week.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DayCount), nameof(RangeTitle), nameof(IsFullWeek))]
    public partial bool IsWorkWeek { get; set; }

    public bool IsFullWeek
    {
        get => !IsWorkWeek;
        set => IsWorkWeek = !value;
    }

    public int DayCount => IsWorkWeek ? 5 : 7;

    partial void OnIsWorkWeekChanged(bool value)
    {
        if (!_loadingSettings)
        {
            _ = settings.SetAsync(SettingKeys.CalendarWorkWeek, value ? "true" : "false");
            _ = LoadWeekAsync();
        }
    }

    private bool _loadingSettings;

    public static IReadOnlyList<string> Hours { get; } = Enumerable.Range(0, 24).Select(h => $"{h:00}:00").ToList();

    /// <summary>Time grid subdivision in minutes: 60, 30 or 15 (setting "Kalender → Raster").</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GridLines))]
    public partial int GridMinutes { get; set; } = 30;

    /// <summary>One entry per slot of the day: full hours get a solid line, subdivisions a lighter one.</summary>
    public IReadOnlyList<GridLine> GridLines => GridLine.ForDay(GridMinutes);

    /// <summary>Position of the "now" line in the time grid (pixels from midnight), refreshed every minute.</summary>
    [ObservableProperty]
    public partial double NowTop { get; set; } = CurrentTop();

    public void RefreshNow() => NowTop = CurrentTop();

    private static double CurrentTop() => (DateTime.Now - DateTime.Today).TotalHours * TimedEventItem.HourHeight;

    partial void OnNavigatorMonthCountChanged(int value) => _ = RefreshNavigatorAsync();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RangeTitle))]
    public partial DateTime WeekStart { get; set; } = StartOfWeek(DateTime.Today);

    public string RangeTitle
    {
        get
        {
            var end = WeekStart.AddDays(DayCount - 1);
            return WeekStart.Month == end.Month
                ? $"{WeekStart:d.}–{end:d. MMMM yyyy}"
                : $"{WeekStart:d. MMMM} – {end:d. MMMM yyyy}";
        }
    }

    public bool HasCalendars => Calendars.Count > 0;

    public async Task ReloadAsync()
    {
        NavigatorMonthCount = int.TryParse(await settings.GetAsync(SettingKeys.CalendarNavigatorMonths), out var months) && months is 1 or 2 ? months : 1;
        IsTimeGrid = await settings.GetAsync(SettingKeys.CalendarLayout) != "list"; // default: time grid
        _loadingSettings = true;
        IsWorkWeek = await settings.GetBoolAsync(SettingKeys.CalendarWorkWeek);
        _loadingSettings = false;
        GridMinutes = int.TryParse(await settings.GetAsync(SettingKeys.CalendarGridMinutes), out var minutes) && minutes is 60 or 30 or 15 ? minutes : 30;
        var hidden = await calendar.GetHiddenCalendarsAsync();

        // The new list is built first and swapped in at once: a week load starting meanwhile must never see no calendars.
        var items = new List<CalendarListItem>();
        var all = await calendar.GetCalendarsAsync();
        var colors = await ColorsAsync(settings, all);
        var tasksShown = await tasks.GetListsInCalendarAsync();
        // Pure task lists belong under "Aufgaben", not in the calendar list.
        foreach (var info in all.Where(c => c.HasEvents))
        {
            var color = colors[(info.ConnectionId, info.RemoteId)];
            var item = new CalendarListItem(info, color)
            {
                IsVisible = !hidden.Contains(CalendarController.CalendarKey(info)),
                ShowTasks = tasksShown.Contains(CalendarController.CalendarKey(info)),
            };
            _solo.Apply(item);
            item.PropertyChanged += async (_, e) =>
            {
                if (e.PropertyName == nameof(CalendarListItem.IsVisible))
                {
                    // Ticking a calendar ends "only this one": the ticks are the selection again.
                    if (_solo.IsActive)
                    {
                        _solo.Set(null, Calendars);
                    }

                    await calendar.SetCalendarVisibleAsync(item.Info, item.IsVisible);
                    await LoadWeekAsync(dataChanged: true);
                }
                else if (e.PropertyName == nameof(CalendarListItem.ShowTasks))
                {
                    await tasks.SetListInCalendarAsync(item.Info, item.ShowTasks);
                    await LoadWeekAsync(dataChanged: true);
                    TaskListsChanged?.Invoke(this, EventArgs.Empty);
                }
            };
            items.Add(item);
        }

        Calendars.Clear();
        foreach (var item in items)
        {
            Calendars.Add(item);
        }

        OnPropertyChanged(nameof(HasCalendars));
        await LoadWeekAsync(dataChanged: true);
    }

    [RelayCommand]
    private Task PreviousWeekAsync()
    {
        WeekStart = WeekStart.AddDays(-7);
        return LoadWeekAsync();
    }

    [RelayCommand]
    private Task NextWeekAsync()
    {
        WeekStart = WeekStart.AddDays(7);
        return LoadWeekAsync();
    }

    [RelayCommand]
    private Task TodayAsync()
    {
        WeekStart = StartOfWeek(DateTime.Today);
        return LoadWeekAsync();
    }

    [RelayCommand]
    private Task PreviousMonthAsync()
    {
        NavigatorMonth = NavigatorMonth.AddMonths(-1);
        return RefreshNavigatorAsync();
    }

    [RelayCommand]
    private Task NextMonthAsync()
    {
        NavigatorMonth = NavigatorMonth.AddMonths(1);
        return RefreshNavigatorAsync();
    }

    /// <summary>Clicking a day (or a week number) in the navigator shows that week.</summary>
    [RelayCommand]
    private Task SelectDayAsync(DateTime date)
    {
        WeekStart = StartOfWeek(date);
        return LoadWeekAsync();
    }

    private readonly SoloFilter _solo = new();

    [RelayCommand]
    private Task ToggleSoloAsync(CalendarListItem item)
    {
        _solo.Toggle(item, Calendars);
        return LoadWeekAsync(dataChanged: true);
    }

    private bool IsShown(CalendarListItem item) => _solo.Shows(item.Info, item.IsVisible);

    /// <summary>Sets a calendar's color; null restores the server's color.</summary>
    public async Task SetColorAsync(CollectionListItem item, string? color)
    {
        ArgumentNullException.ThrowIfNull(item);
        await settings.SetAsync(ColorKey(item.Info), color);
        await ReloadAsync();
    }

    /// <summary>Gives a calendar an own display name (null: the server's again).</summary>
    public async Task RenameAsync(CollectionListItem item, string? name)
    {
        ArgumentNullException.ThrowIfNull(item);
        await calendar.SetDisplayNameAsync(item.Info, name);
        await ReloadAsync();
    }

    /// <summary>
    /// The colour of each calendar, as the calendar page shows it (also for the agenda beside the mail): one chosen in
    /// Neruna wins over the server's (stored locally, also for read-only subscriptions), otherwise a palette colour.
    /// </summary>
    internal static async Task<Dictionary<(Guid, string), string>> ColorsAsync(ISettingsStore settings, IReadOnlyList<CalendarInfo> calendars)
    {
        var result = new Dictionary<(Guid, string), string>();
        var index = 0;
        foreach (var info in calendars)
        {
            result[(info.ConnectionId, info.RemoteId)] = await settings.GetAsync(ColorKey(info)) ?? info.Color ?? Palette[index++ % Palette.Length];
        }

        return result;
    }

    internal static string ColorKey(CalendarInfo info) => $"calendar.color.{info.ConnectionId:N}.{info.RemoteId}";

    [RelayCommand]
    private void Subscribe() => SubscribeRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void Manage() => ManageRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void AddSpecial() => SpecialRequested?.Invoke(this, EventArgs.Empty);

    /// <param name="day">Day column that was used, or null for "next full hour today".</param>
    [RelayCommand]
    private void NewEvent(DateTime? day)
    {
        var now = DateTime.Now;
        var start = day is { } d && d.Date != now.Date
            ? d.Date.AddHours(9)
            : now.Date.AddHours(now.Hour + 1);
        _ = NewEventAtAsync(start);
    }

    /// <summary>Double-click into the time grid: new event at that time (one hour).</summary>
    [RelayCommand]
    private async Task NewEventAtAsync(DateTime start) =>
        await ShowEditorAsync(EventDraft.New(start, await settings.GetDefaultReminderAsync()), null);

    [RelayCommand]
    private Task EditEventAsync(CalendarEventItem item) => OpenOccurrenceAsync(item.Occurrence);

    /// <summary>Opens the event of an occurrence in the editor (also from a reminder).</summary>
    public async Task OpenOccurrenceAsync(CalendarOccurrence occurrence)
    {
        ArgumentNullException.ThrowIfNull(occurrence);
        if (occurrence.IsTask)
        {
            TaskOpenRequested?.Invoke(this, occurrence);
            return;
        }

        var stored = await calendar.GetObjectAsync(occurrence.Calendar, occurrence.ObjectRemoteId);
        if (stored is null)
        {
            return;
        }

        await ShowEditorAsync(EventDraft.FromICalendar(stored.ICalendarData), (occurrence.Calendar, occurrence.ObjectRemoteId));
    }

    private async Task ShowEditorAsync(EventDraft draft, (CalendarInfo Calendar, string RemoteId)? existing)
    {
        var writable = Calendars.Select(c => c.Info).Where(c => !c.IsReadOnly).ToList();

        // The own address per calendar: organizer of new invitations, and "me" among the attendees.
        var owners = (await calendar.GetSourcesAsync()).ToDictionary(s => s.Connection.Id, s => s.Account.EmailAddress);
        var editor = new EventEditorViewModel(calendar, invitations, writable, draft, existing, c => owners.GetValueOrDefault(c.ConnectionId));
        EditorRequested?.Invoke(this, editor);
    }

    /// <param name="dataChanged">Calendars, their visibility or events changed: busy days are recomputed.</param>
    private int _weekVersion;

    // Events plus the tasks marked for the calendar; showing one calendar alone leaves the other task lists out.
    private async Task<IReadOnlyList<CalendarOccurrence>> OccurrencesAsync(IReadOnlyList<CalendarInfo> shown, DateTimeOffset from, DateTimeOffset to) =>
        await tasks.AddToCalendarAsync(await calendar.GetOccurrencesAsync(shown, from, to), shown, from, to, includeTaskLists: !_solo.IsActive);

    private async Task LoadWeekAsync(bool dataChanged = false)
    {
        var visible = Calendars.Where(IsShown).ToList();
        var colors = visible.ToDictionary(c => (c.Info.ConnectionId, c.Info.RemoteId), c => c.Color);
        var from = new DateTimeOffset(WeekStart);
        var shown = visible.Select(c => c.Info).ToList();
        var version = ++_weekVersion;
        var occurrences = await Task.Run(() => OccurrencesAsync(shown, from, from.AddDays(7)));
        if (version != _weekVersion)
        {
            return; // A newer load (other week, calendars changed) is under way; an older result must not overwrite it.
        }

        Days.Clear();
        for (var i = 0; i < DayCount; i++)
        {
            var day = WeekStart.AddDays(i);
            var events = occurrences
                .Where(o => o.Start.LocalDateTime.Date <= day && (o.End.LocalDateTime > day || o.Start.LocalDateTime.Date == day))
                .OrderByDescending(o => o.IsAllDay)
                .ThenBy(o => o.Start)
                .Select(o => new CalendarEventItem(o, colors.GetValueOrDefault((o.Calendar.ConnectionId, o.Calendar.RemoteId)) ?? Palette[0]))
                .ToList();
            Days.Add(new CalendarDay(day, events));
        }

        // The navigator follows the week (e.g. "Heute", arrows) when the week is not visible in it.
        if (dataChanged)
        {
            _busyRange = default;
        }

        var shownUntil = NavigatorMonth.AddMonths(NavigatorMonthCount);
        if (WeekStart.AddDays(6) < NavigatorMonth || WeekStart >= shownUntil)
        {
            NavigatorMonth = new DateTime(WeekStart.Year, WeekStart.Month, 1);
        }

        await RefreshNavigatorAsync();
    }

    // Navigating months redraws at once from this cache; busy days are computed for a wider range in the background.
    private HashSet<DateTime> _busyDays = [];
    private (DateTime From, DateTime To) _busyRange;
    private int _busyVersion;

    private Task RefreshNavigatorAsync()
    {
        var count = Math.Clamp(NavigatorMonthCount, 1, 2);
        var gridStart = StartOfWeek(NavigatorMonth);
        var gridEnd = StartOfWeek(NavigatorMonth.AddMonths(count - 1)).AddDays(42);
        BuildNavigator(count);

        return gridStart >= _busyRange.From && gridEnd <= _busyRange.To
            ? Task.CompletedTask
            : LoadBusyDaysAsync(NavigatorMonth.AddMonths(-3), NavigatorMonth.AddMonths(count + 3));
    }

    // Reuses the month objects (and with them all cells); only the count change adds or removes one.
    private void BuildNavigator(int count)
    {
        while (MiniMonths.Count > count)
        {
            MiniMonths.RemoveAt(MiniMonths.Count - 1);
        }

        while (MiniMonths.Count < count)
        {
            MiniMonths.Add(new MiniMonth());
        }

        for (var i = 0; i < count; i++)
        {
            MiniMonths[i].Show(NavigatorMonth.AddMonths(i), i == 0, WeekStart, _busyDays);
        }
    }

    /// <summary>Days with appointments (bold), for a few months around the navigator.</summary>
    private async Task LoadBusyDaysAsync(DateTime fromMonth, DateTime toMonth)
    {
        var version = ++_busyVersion;
        var from = StartOfWeek(fromMonth);
        var to = toMonth.AddDays(7);
        var visible = Calendars.Where(IsShown).Select(c => c.Info).ToList();
        var occurrences = visible.Count == 0 ? [] : await Task.Run(() => OccurrencesAsync(visible, new DateTimeOffset(from), new DateTimeOffset(to)));

        var busy = await Task.Run(() =>
        {
            var days = new HashSet<DateTime>();
            foreach (var occurrence in occurrences)
            {
                var first = occurrence.Start.LocalDateTime.Date;
                var last = occurrence.End.LocalDateTime > occurrence.Start.LocalDateTime ? occurrence.End.LocalDateTime.AddTicks(-1).Date : first;
                for (var day = first; day <= last && day < to; day = day.AddDays(1))
                {
                    days.Add(day);
                }
            }

            return days;
        });

        // A newer request (further navigation, changed calendars) wins.
        if (version != _busyVersion)
        {
            return;
        }

        _busyDays = busy;
        _busyRange = (from, to);
        BuildNavigator(Math.Clamp(NavigatorMonthCount, 1, 2));
    }

    private static DateTime StartOfWeek(DateTime date) => date.Date.AddDays(-(((int)date.DayOfWeek + 6) % 7));
}

internal sealed class CalendarListItem(CalendarInfo info, string color) : CollectionListItem(info, color)
{
    public override string SoloTip => IsSolo ? T("Wieder alle ausgewählten Kalender anzeigen") : T("Nur diesen Kalender anzeigen");
}

internal sealed class CalendarDay(DateTime date, IReadOnlyList<CalendarEventItem> events)
{
    public DateTime Date { get; } = date;

    /// <summary>Time grid: all-day events in the row on top …</summary>
    public IReadOnlyList<CalendarEventItem> AllDayEvents { get; } = events.Where(e => e.Occurrence.IsAllDay).ToList();

    /// <summary>… and the others at their time, overlapping ones side by side.</summary>
    public IReadOnlyList<TimedEventItem> TimedEvents { get; } = TimedEventItem.Layout(date, events.Where(e => !e.Occurrence.IsAllDay));

    public bool HasAllDayEvents => AllDayEvents.Count > 0;

    public string DayName => Date.ToString("dddd", CultureInfo.CurrentCulture);

    public string DayNumber => Date.ToString("d.M.", CultureInfo.CurrentCulture);

    public bool IsToday => Date == DateTime.Today;

    public IReadOnlyList<CalendarEventItem> Events { get; } = events;
}

internal sealed class CalendarEventItem(CalendarOccurrence occurrence, string color)
{
    public CalendarOccurrence Occurrence { get; } = occurrence;

    public string Title => Occurrence.Summary;

    public bool IsRecurring => Occurrence.IsRecurring;

    public string? Location => Occurrence.Location;

    public bool HasLocation => !string.IsNullOrWhiteSpace(Occurrence.Location);

    public IBrush Brush { get; } = Avalonia.Media.Brush.Parse(color);

    public IBrush Background { get; } = new SolidColorBrush(Avalonia.Media.Color.Parse(color), 0.14);

    public string TimeText => Occurrence.IsAllDay
        ? T("Ganztägig")
        : $"{Occurrence.Start.LocalDateTime:HH:mm}–{Occurrence.End.LocalDateTime:HH:mm}";

    /// <summary>Hover text: title, location (if any) and time, one per line.</summary>
    public string Tooltip => string.Join('\n', new[] { Title, Location, TimeText }.Where(line => !string.IsNullOrWhiteSpace(line)));
}

/// <summary>An event placed in the time grid of one day: vertical position from its time, column from overlaps.</summary>
internal sealed class TimedEventItem(CalendarEventItem item, double top, double height, int column, int columns)
{
    public const double HourHeight = 48;
    private const double MinHeight = 22;

    public CalendarEventItem Item { get; } = item;

    public double Top { get; } = top;

    public double Height { get; } = height;

    public double LeftFraction { get; } = (double)column / columns;

    public double WidthFraction { get; } = 1.0 / columns;

    /// <summary>Short events show title and time on one line.</summary>
    public bool IsShort => Height < 44;

    /// <summary>
    /// Clips events to the day, then assigns columns: events that overlap form a cluster sharing the width,
    /// each one taking the first free column (the usual calendar layout).
    /// </summary>
    public static IReadOnlyList<TimedEventItem> Layout(DateTime day, IEnumerable<CalendarEventItem> events)
    {
        var spans = events
            .Select(e => (Item: e, Start: Max(e.Occurrence.Start.LocalDateTime, day), End: Min(e.Occurrence.End.LocalDateTime, day.AddDays(1))))
            .Select(s => (s.Item, s.Start, End: s.End <= s.Start ? s.Start.AddMinutes(30) : s.End))
            .OrderBy(s => s.Start)
            .ThenByDescending(s => s.End)
            .ToList();

        var result = new List<TimedEventItem>();
        var cluster = new List<(CalendarEventItem Item, DateTime Start, DateTime End, int Column)>();
        var clusterEnd = DateTime.MinValue;

        void Flush()
        {
            var columns = cluster.Count == 0 ? 1 : cluster.Max(c => c.Column) + 1;
            foreach (var c in cluster)
            {
                var top = (c.Start - day).TotalHours * HourHeight;
                var height = Math.Max(MinHeight, (c.End - c.Start).TotalHours * HourHeight);
                result.Add(new TimedEventItem(c.Item, top, Math.Min(height, 24 * HourHeight - top), c.Column, columns));
            }

            cluster.Clear();
        }

        foreach (var span in spans)
        {
            if (cluster.Count > 0 && span.Start >= clusterEnd)
            {
                Flush();
            }

            // First column whose last event has ended (very short events count as at least MinHeight).
            var column = 0;
            while (cluster.Any(c => c.Column == column && Overlaps(c.Start, c.End, span.Start, span.End)))
            {
                column++;
            }

            cluster.Add((span.Item, span.Start, span.End, column));
            var visualEnd = Max(span.End, span.Start.AddHours(MinHeight / HourHeight));
            clusterEnd = cluster.Count == 1 ? visualEnd : Max(clusterEnd, visualEnd);
        }

        Flush();
        return result;
    }

    private static bool Overlaps(DateTime aStart, DateTime aEnd, DateTime bStart, DateTime bEnd)
    {
        var minimum = TimeSpan.FromHours(MinHeight / HourHeight);
        var aVisualEnd = aEnd - aStart < minimum ? aStart + minimum : aEnd;
        return bStart < aVisualEnd && aStart < bEnd;
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
}

internal sealed record GridLine(double Height, bool IsHour)
{
    public bool IsSubdivision => !IsHour;

    public static IReadOnlyList<GridLine> ForDay(int minutes)
    {
        var slotsPerHour = 60 / Math.Clamp(minutes, 15, 60);
        var height = TimedEventItem.HourHeight / slotsPerHour;
        return Enumerable.Range(0, 24 * slotsPerHour).Select(i => new GridLine(height, i % slotsPerHour == 0)).ToList();
    }
}
