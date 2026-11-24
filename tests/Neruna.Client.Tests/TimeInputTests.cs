using Neruna.Core.Calendar;

namespace Neruna.Client.Tests;

public class TimeInputTests
{
    [Theory]
    [InlineData("9", 9, 0)]
    [InlineData("930", 9, 30)]
    [InlineData("0930", 9, 30)]
    [InlineData("9:30", 9, 30)]
    [InlineData("9.30", 9, 30)]
    [InlineData("9,3", 9, 30)]
    [InlineData("14h", 14, 0)]
    [InlineData("1430", 14, 30)]
    [InlineData(" 23:59 ", 23, 59)]
    [InlineData("24", 0, 0)]
    public void Times_are_typed_the_way_people_write_them(string text, int hours, int minutes) =>
        Assert.Equal(new TimeSpan(hours, minutes, 0), TimeInput.Parse(text));

    [Theory]
    [InlineData("")]
    [InlineData("25")]
    [InlineData("9:75")]
    [InlineData("12345")]
    [InlineData("abc")]
    [InlineData("9:300")]
    public void Anything_else_is_no_time(string text) => Assert.Null(TimeInput.Parse(text));

    [Fact]
    public void Arrow_keys_step_onto_the_next_quarter_and_around_midnight()
    {
        Assert.Equal(new TimeSpan(9, 15, 0), TimeInput.Step(new TimeSpan(9, 7, 0), 15, up: true));
        Assert.Equal(new TimeSpan(9, 0, 0), TimeInput.Step(new TimeSpan(9, 7, 0), 15, up: false));
        Assert.Equal(new TimeSpan(8, 45, 0), TimeInput.Step(new TimeSpan(9, 0, 0), 15, up: false));
        Assert.Equal(TimeSpan.Zero, TimeInput.Step(new TimeSpan(23, 45, 0), 15, up: true));
        Assert.Equal(new TimeSpan(23, 0, 0), TimeInput.Step(TimeSpan.Zero, 60, up: false));
        Assert.Equal("09:05", TimeInput.Format(new TimeSpan(9, 5, 0)));
    }
}
