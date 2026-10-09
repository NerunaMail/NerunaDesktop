using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Neruna.Desktop.ViewModels;

/// <summary>
/// One month of the date navigator above the calendar list ("Datumsnavigator"). The 6×7 cells are
/// created once and only relabelled when browsing: rebuilding ~100 buttons per click made switching visibly slow.
/// </summary>
internal sealed partial class MiniMonth : ObservableObject
{
    private static CultureInfo German => Neruna.Core.Localization.Texts.Culture;

    public MiniMonth()
    {
        Weeks = Enumerable.Range(0, 6).Select(_ => new MiniWeek()).ToList();
    }

    // Short German weekday names, Monday first (ISO 8601 / Switzerland).
    public static IReadOnlyList<string> WeekdayHeaders { get; } = ["Mo", "Di", "Mi", "Do", "Fr", "Sa", "So"];

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    /// <summary>The first (top) month carries both arrows.</summary>
    [ObservableProperty]
    public partial bool IsFirst { get; set; }

    /// <summary>Always six weeks, so two months stacked stay aligned and the size never jumps.</summary>
    public IReadOnlyList<MiniWeek> Weeks { get; }

    public void Show(DateTime month, bool isFirst, DateTime selectedWeekStart, IReadOnlySet<DateTime> daysWithEvents)
    {
        var first = new DateTime(month.Year, month.Month, 1);
        Title = first.ToString("MMMM yyyy", German);
        IsFirst = isFirst;

        var start = first.AddDays(-(((int)first.DayOfWeek + 6) % 7));
        for (var w = 0; w < 6; w++)
        {
            var weekStart = start.AddDays(7 * w);
            Weeks[w].Show(weekStart, weekStart == selectedWeekStart, first.Month, daysWithEvents);
        }
    }
}

internal sealed partial class MiniWeek : ObservableObject
{
    public IReadOnlyList<MiniDay> Days { get; } = Enumerable.Range(0, 7).Select(_ => new MiniDay()).ToList();

    [ObservableProperty]
    public partial string NumberText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public DateTime Start => Days[0].Date;

    public void Show(DateTime weekStart, bool isSelected, int month, IReadOnlySet<DateTime> daysWithEvents)
    {
        NumberText = ISOWeek.GetWeekOfYear(weekStart).ToString(CultureInfo.InvariantCulture);
        IsSelected = isSelected;
        for (var d = 0; d < 7; d++)
        {
            var date = weekStart.AddDays(d);
            Days[d].Show(date, date.Month == month, date == DateTime.Today, daysWithEvents.Contains(date));
        }
    }
}

internal sealed partial class MiniDay : ObservableObject
{
    private static CultureInfo German => Neruna.Core.Localization.Texts.Culture;

    [ObservableProperty]
    public partial DateTime Date { get; set; }

    [ObservableProperty]
    public partial string Text { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOtherMonth))]
    public partial bool IsCurrentMonth { get; set; }

    [ObservableProperty]
    public partial bool IsToday { get; set; }

    [ObservableProperty]
    public partial bool HasEvents { get; set; }

    public bool IsOtherMonth => !IsCurrentMonth;

    public string Tooltip => Date.ToString("dddd, d. MMMM yyyy", German);

    public void Show(DateTime date, bool isCurrentMonth, bool isToday, bool hasEvents)
    {
        if (Date != date)
        {
            Date = date;
            Text = date.Day.ToString(CultureInfo.InvariantCulture);
            OnPropertyChanged(nameof(Tooltip));
        }

        IsCurrentMonth = isCurrentMonth;
        IsToday = isToday;
        HasEvents = hasEvents;
    }
}
