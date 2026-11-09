using Neruna.Core.Calendar;

namespace Neruna.Client.Tests;

public class OutlookColorsTests
{
    [Theory]
    [InlineData("#D13438", 0)]
    [InlineData("#e81123", 0)]
    [InlineData("#0078D4", 7)]
    [InlineData("#1E90FF", 7)]
    [InlineData("#00B7C3", 5)]
    [InlineData("#13A10E", 4)]
    [InlineData("#000000", 14)]
    [InlineData("#5C2E91", 23)]
    public void The_nearest_category_colour_is_taken(string color, int preset) =>
        Assert.Equal(preset, OutlookColors.NearestCategory(color));

    [Theory]
    [InlineData("#0078D4", "lightBlue")]
    [InlineData("#0A8F12", "lightGreen")]
    [InlineData("#D13438", "lightRed")]
    [InlineData("#FFB900", "lightYellow")]
    public void The_nearest_calendar_colour_is_taken(string color, string name) =>
        Assert.Equal(name, OutlookColors.NearestCalendar(color));

    [Fact]
    public void Unknown_or_no_colour_is_none() =>
        Assert.Equal((null, null, "#0078D4"), (OutlookColors.CategoryColor(-1), OutlookColors.CategoryColor(25), OutlookColors.CategoryColor(7)));
}
