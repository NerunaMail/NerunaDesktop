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
internal sealed record PlanningBlock(double Start, double Length, IBrush Brush, double Opacity, string Tip, CalendarOccurrence Occurrence);

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
    [NotifyPropertyChangedFor(nameof(ShowTimeGrid), nameof(ShowList))]
    public partial bool IsPlanning { get; set; }

    public bool ShowTimeGrid => IsTimeGrid && !IsPlanning;

    public bool ShowList => !IsTimeGrid && !IsPlanning;

    public ObservableCollection<PlanningRow> PlanningRows { get; } = [];

    public ObservableCollection<PlanningDay> PlanningDays { get; } = [];

    public static IReadOnlyList<ShowAsOption> PlanningLegend => ShowAsOption.All;

    partial void OnIsPlanningChanged(bool value)
    {
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
        var days = DayCount;
        var step = span > 12 ? 3 : span > 6 ? 2 : 1;

        PlanningDays.Clear();
        for (var i = 0; i < days; i++)
        {
            var day = WeekStart.AddDays(i);
            var hours = new List<string>();
            for (var h = from; h < to; h += step)
            {
                hours.Add(h.ToString("00", System.Globalization.CultureInfo.InvariantCulture));
            }

            PlanningDays.Add(new PlanningDay(day.ToString("ddd d.M.", Culture), day == DateTime.Today, hours));
        }

        // Position on the axis: whole days, each from "from" to "to" o'clock; earlier and later times sit at the edges.
        double At(DateTime local)
        {
            var index = (local.Date - WeekStart).TotalDays;
            if (index < 0)
            {
                return 0;
            }

            if (index >= days)
            {
                return 1;
            }

            var hour = Math.Clamp(local.TimeOfDay.TotalHours, from, to);
            return (index + (hour - from) / span) / days;
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
                    // Split at midnight: the hours between the visible ranges are not on the axis.
                    for (var day = start.Date; day < end || day == start.Date; day = day.AddDays(1))
                    {
                        var a = At(o.IsAllDay ? day.AddHours(from) : (day == start.Date ? start : day.AddHours(from)));
                        var b = At(o.IsAllDay ? day.AddHours(to) : (end.Date == day ? end : day.AddHours(to)));
                        if (b > a)
                        {
                            var when = o.IsAllDay ? T("ganztägig") : $"{start:HH:mm}–{end:HH:mm}";
                            blocks.Add(new PlanningBlock(a, b - a, ShowAsColors.Brush(o.ShowAs), o.IsAllDay ? 0.45 : 0.9,
                                $"{o.Summary}\n{when}" + (string.IsNullOrWhiteSpace(o.Location) ? string.Empty : $"\n{o.Location}") + $"\n{ShowAsOption.Of(o.ShowAs).Label}", o));
                        }

                        if (day >= end.Date)
                        {
                            break;
                        }
                    }
                }

                PlanningRows.Add(new PlanningRow(item.Name, group.Title, item.Brush, blocks));
            }
        }
    }
}
