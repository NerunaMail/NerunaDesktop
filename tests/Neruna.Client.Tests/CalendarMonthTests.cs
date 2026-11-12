using Neruna.Core.Calendar;

namespace Neruna.Client.Tests;

public class CalendarMonthTests
{
    [Theory]
    [InlineData(2026, 11, 6)] // starts on a Sunday: 26 Oct – 6 Dec
    [InlineData(2027, 2, 4)]  // February 2027 starts on a Monday and has 28 days
    [InlineData(2026, 12, 5)]
    public void The_month_is_whole_weeks_from_the_monday_before_the_first(int year, int month, int weeks)
    {
        var dates = CalendarMonth.Dates(new DateTime(year, month, 1));

        Assert.Equal(weeks * 7, dates.Count);
        Assert.Equal(DayOfWeek.Monday, dates[0].DayOfWeek);
        Assert.Equal(DayOfWeek.Sunday, dates[^1].DayOfWeek);
        Assert.Contains(new DateTime(year, month, 1), dates);
        Assert.Contains(new DateTime(year, month, DateTime.DaysInMonth(year, month)), dates);
    }
}
