using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Avalonia.Media;
using Neruna.Core;
using Neruna.Core.Calendar;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.ViewModels;

/// <summary>A day on the axis of the planning view, with the hours shown under it.</summary>
internal sealed record PlanningDay(string Label, bool IsToday, IReadOnlyList<string> Hours);

/// <summary>An event in the planning view: where it lies on the axis (fractions of the whole width) and its show-as colour.</summary>
internal sealed record PlanningBlock(double Start, double Length, IBrush Brush, double Opacity, string Tip, CalendarOccurrence? Occurrence);

/// <summary>A calendar as a row of the planning view.</summary>
internal sealed record PlanningRow(string Name, string? Owner, IBrush Color, IReadOnlyList<PlanningBlock> Blocks);

/// <summary>
/// "Planung": the week as a time line – each shown calendar a row, its events as blocks coloured by how they show
/// (busy red, free green, tentative orange, away purple), the days and hours of the visible range as the axis.
/// </summary>
internal sealed partial class CalendarViewModel
{
    /// <summary>Toolbar "Planung" instead of the week (remembered).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTimeGrid), nameof(ShowList), nameof(ShowMonth), nameof(ShowPlanning))]
    public partial bool IsPlanning { get; set; }

    // The view switcher (1 Tag · Mo–Fr · Woche · Monat · Planung): one of them is on. A span switches the planning off,
    // "Planung" keeps the span (a month becomes its week, the month has no planning).
    public bool ViewDay { get => IsSingleDay && !IsPlanning; set => ChooseView(value, 1); }

    public bool ViewWorkWeek { get => IsWorkWeek && !IsPlanning; set => ChooseView(value, 5); }

    public bool ViewWeek { get => IsFullWeek && !IsPlanning; set => ChooseView(value, 7); }

    public bool ViewMonth { get => IsMonth && !IsPlanning; set => ChooseView(value, MonthDays); }

    public bool ViewPlanning
    {
        get => ShowPlanning;
        set
        {
            if (value && !ShowPlanning)
            {
                if (IsMonth || HasChosenDays)
                {
                    ShowSpan(7);
                }

                IsPlanning = true;
            }

            NotifyViews();
        }
    }

    private void ChooseView(bool on, int days)
    {
        if (on)
        {
            IsPlanning = false;
            ShowSpan(days);
        }

        NotifyViews(); // a toggle in the group cannot be switched off by clicking it again
    }

    private void NotifyViews()
    {
        OnPropertyChanged(nameof(ViewDay));
        OnPropertyChanged(nameof(ViewWorkWeek));
        OnPropertyChanged(nameof(ViewWeek));
        OnPropertyChanged(nameof(ViewMonth));
        OnPropertyChanged(nameof(ViewPlanning));
    }

    /// <summary>The month has no planning view: "Planung" waits until a week or a day is shown again.</summary>
    public bool ShowPlanning => IsPlanning && !IsMonth;

    public bool ShowMonth => IsMonth;

    public bool ShowTimeGrid => IsTimeGrid && !ShowPlanning && !IsMonth;

    public bool ShowList => !IsTimeGrid && !ShowPlanning && !IsMonth;

    public ObservableCollection<PlanningRow> PlanningRows { get; } = [];

    /// <summary>People shown by their free/busy from the server (Microsoft 365, Exchange) – no subscription needed.</summary>
    public ObservableCollection<PlanningRow> PlanningPeople { get; } = [];

    /// <summary>"Personen" above the planning view: addresses as typed, with suggestions (remembered).</summary>
    [ObservableProperty]
    public partial string PlanningPeopleText { get; set; } = string.Empty;

    /// <summary>Contacts suggested in "Personen".</summary>
    public Infrastructure.RecipientDirectory Recipients => recipients;

    /// <summary>Shown when no connected server can tell free/busy.</summary>
    [ObservableProperty]
    public partial string? PlanningPeopleNote { get; set; }

    private int _peopleVersion;

    partial void OnPlanningPeopleTextChanged(string value)
    {
        if (!_loadingSettings)
        {
            _ = settings.SetAsync(SettingKeys.CalendarPlanningPeople, value);
            _ = LoadPlanningPeopleLaterAsync(++_peopleVersion);
        }
    }

    private async Task LoadPlanningPeopleLaterAsync(int version)
    {
        await Task.Delay(600);
        if (version == _peopleVersion)
        {
            await LoadPlanningPeopleAsync(version);
        }
    }

    /// <summary>Asks the server for the people's free/busy in the shown week and turns it into rows.</summary>
    private async Task LoadPlanningPeopleAsync(int version)
    {
        var people = EventAttendee.FromRecipients(PlanningPeopleText);
        if (people.Count == 0)
        {
            PlanningPeople.Clear();
            PlanningPeopleNote = null;
            return;
        }

        var dates = ShownDates;
        var from = new DateTimeOffset(dates[0]);
        var to = new DateTimeOffset(dates[^1].AddDays(1));
        IReadOnlyList<Availability>? answers;
        try
        {
            answers = await Task.Run(() => calendar.GetFreeBusyAsync([.. people.Select(p => p.Email)], from, to));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            answers = null;
            PlanningPeopleNote = T("Verfügbarkeit konnte nicht abgefragt werden: ") + ex.Message;
        }

        if (version != _peopleVersion)
        {
            return;
        }

        PlanningPeople.Clear();
        if (answers is null)
        {
            PlanningPeopleNote ??= T("Keines der Konten kann die Verfügbarkeit anderer Personen abfragen (möglich mit Microsoft 365 und Exchange).");
            return;
        }

        PlanningPeopleNote = null;
        foreach (var (person, availability) in people.Zip(answers))
        {
            var blocks = new List<PlanningBlock>();
            foreach (var slot in availability.Slots)
            {
                foreach (var (a, b) in Spans(slot.Start.LocalDateTime, slot.End.LocalDateTime, false))
                {
                    var tip = $"{slot.Subject ?? ShowAsOption.Of(slot.ShowAs).Label}\n{slot.Start.LocalDateTime:ddd HH:mm}–{slot.End.LocalDateTime:HH:mm}"
                              + (slot.Location is { } place ? $"\n{place}" : string.Empty);
                    blocks.Add(new PlanningBlock(a, b - a, ShowAsColors.Brush(slot.ShowAs), 0.9, tip, null));
                }
            }

            PlanningPeople.Add(new PlanningRow(person.Name ?? person.Email,
                availability.IsKnown ? person.Email : T("keine Angaben vom Server"), PersonBrush, blocks));
        }
    }

    private static readonly IBrush PersonBrush = Brush.Parse("#8A8886");

    public ObservableCollection<PlanningDay> PlanningDays { get; } = [];

    public static IReadOnlyList<ShowAsOption> PlanningLegend => ShowAsOption.All;

    partial void OnIsPlanningChanged(bool value)
    {
        NotifyViews();
        if (!_loadingSettings)
        {
            _ = settings.SetBoolAsync(SettingKeys.CalendarPlanning, value);
        }
    }

    partial void OnIsTimeGridChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowTimeGrid));
        OnPropertyChanged(nameof(ShowList));
    }

    /// <summary>The rows and the axis for the week just loaded.</summary>
    private void BuildPlanning(IReadOnlyList<CalendarOccurrence> occurrences)
    {
        var from = Math.Clamp(VisibleFrom, 0, 23);
        var to = Math.Clamp(VisibleTo, from + 1, 24);
        var span = to - from;
        var step = span > 12 ? 3 : span > 6 ? 2 : 1;

        PlanningDays.Clear();
        foreach (var day in ShownDates)
        {
            var hours = new List<string>();
            for (var h = from; h < to; h += step)
            {
                hours.Add(h.ToString("00", System.Globalization.CultureInfo.InvariantCulture));
            }

            PlanningDays.Add(new PlanningDay(day.ToString("ddd d.M.", Culture), day == DateTime.Today, hours));
        }

        PlanningRows.Clear();
        foreach (var group in CalendarGroups)
        {
            foreach (var item in group.Items.Where(IsShown))
            {
                var blocks = new List<PlanningBlock>();
                foreach (var o in occurrences.Where(o => !o.IsTask && o.Calendar.ConnectionId == item.Info.ConnectionId && o.Calendar.RemoteId == item.Info.RemoteId))
                {
                    var start = o.Start.LocalDateTime;
                    var end = o.End.LocalDateTime;
                    foreach (var (a, b) in Spans(start, end, o.IsAllDay))
                    {
                        var when = o.IsAllDay ? T("ganztägig") : $"{start:HH:mm}–{end:HH:mm}";
                        blocks.Add(new PlanningBlock(a, b - a, ShowAsColors.Brush(o.ShowAs), o.IsAllDay ? 0.45 : 0.9,
                            $"{o.Summary}\n{when}" + (string.IsNullOrWhiteSpace(o.Location) ? string.Empty : $"\n{o.Location}") + $"\n{ShowAsOption.Of(o.ShowAs).Label}", o));
                    }
                }

                PlanningRows.Add(new PlanningRow(item.Name, group.Title, item.Brush, blocks));
            }
        }

        // The people follow the week shown.
        _ = LoadPlanningPeopleAsync(++_peopleVersion);
    }

    /// <summary>
    /// Where a time lies on the axis (fractions of the width): whole days, each from the first to the last visible hour,
    /// split at midnight – the hours outside the visible range are not on the axis.
    /// </summary>
    private IEnumerable<(double Start, double End)> Spans(DateTime start, DateTime end, bool allDay)
    {
        var from = Math.Clamp(VisibleFrom, 0, 23);
        var to = Math.Clamp(VisibleTo, from + 1, 24);
        var span = to - from;
        // The days need not follow each other (days chosen in the navigator): each its own place on the axis.
        var dates = ShownDates;
        var index = dates.Select((d, i) => (d, i)).ToDictionary(x => x.d, x => x.i);
        var days = dates.Count;
        double At(int column, DateTime local)
        {
            var hour = Math.Clamp(local.TimeOfDay.TotalHours, from, to);
            return (column + (hour - from) / span) / days;
        }

        for (var day = start.Date; day < end || day == start.Date; day = day.AddDays(1))
        {
            if (index.TryGetValue(day, out var column))
            {
                var a = At(column, allDay ? day.AddHours(from) : (day == start.Date ? start : day.AddHours(from)));
                var b = allDay || end.Date != day ? (column + 1.0) / days : At(column, end);
                if (b > a)
                {
                    yield return (a, b);
                }
            }

            if (day >= end.Date)
            {
                yield break;
            }
        }
    }
}
