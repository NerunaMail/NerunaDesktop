using Ical.Net.DataTypes;
using Ical.Net.CalendarComponents;
using Ical.Net.Serialization;
using Neruna.Core.Calendar;

namespace Neruna.Client.Tests;

/// <summary>The repetitions the editors offer, read from and written as RRULE.</summary>
public class RecurrenceSpecTests
{
    private static readonly DateTime Start = new(2026, 11, 10); // the second Tuesday

    [Theory]
    [InlineData("FREQ=DAILY;INTERVAL=3", RecurrenceFrequency.Daily, 3, null, null)]
    [InlineData("FREQ=WEEKLY;BYDAY=MO,WE,FR", RecurrenceFrequency.Weekly, 1, null, null)]
    [InlineData("FREQ=MONTHLY;BYDAY=2TU", RecurrenceFrequency.Monthly, 1, 2, null)]
    [InlineData("FREQ=MONTHLY;BYDAY=TU;BYSETPOS=-1", RecurrenceFrequency.Monthly, 1, -1, null)]
    [InlineData("FREQ=MONTHLY;BYMONTHDAY=10;COUNT=6", RecurrenceFrequency.Monthly, 1, null, 6)]
    [InlineData("FREQ=YEARLY;INTERVAL=2;BYMONTH=11;BYDAY=2TU", RecurrenceFrequency.Yearly, 2, 2, null)]
    public void Rules_the_editor_offers_are_read(string rrule, RecurrenceFrequency frequency, int interval, int? position, int? count)
    {
        var spec = RecurrenceSpec.Of(new RecurrenceRule(rrule), Start)!;
        Assert.Equal((frequency, interval, position, count), (spec.Frequency, spec.Interval, spec.Position, spec.Count));
    }

    [Theory]
    [InlineData("FREQ=MONTHLY;BYMONTHDAY=1,15")]
    [InlineData("FREQ=DAILY;BYHOUR=9,17")]
    [InlineData("FREQ=YEARLY;BYMONTH=3,9")]
    [InlineData("FREQ=WEEKLY;BYDAY=1MO")]
    public void Other_rules_are_left_alone(string rrule) => Assert.Null(RecurrenceSpec.Of(new RecurrenceRule(rrule), Start));

    [Fact]
    public void Written_rules_read_back_the_same()
    {
        var specs = new[]
        {
            new RecurrenceSpec(RecurrenceFrequency.Weekly, 2) { Weekdays = [DayOfWeek.Monday, DayOfWeek.Thursday] },
            new RecurrenceSpec(RecurrenceFrequency.Monthly) { Position = -1, Weekdays = [DayOfWeek.Friday] },
            new RecurrenceSpec(RecurrenceFrequency.Yearly) { Position = 2, Weekdays = [DayOfWeek.Tuesday], Count = 5 },
        };
        foreach (var spec in specs)
        {
            // Through a whole calendar, as stored: Ical.Net writes a rule only there.
            var calendar = new Ical.Net.Calendar();
            calendar.Events.Add(new CalendarEvent { DtStart = new CalDateTime(DateOnly.FromDateTime(Start)), RecurrenceRule = spec.ToRule(Start, allDay: true, until: null) });
            var text = new CalendarSerializer().SerializeToString(calendar)!;
            var read = Ical.Net.Calendar.Load(text)!.Events[0]!.RecurrenceRule;
            Assert.True(spec.SameAs(RecurrenceSpec.Of(read, Start)), text);
        }

        var until = new RecurrenceSpec(RecurrenceFrequency.Daily).ToRule(Start, allDay: true, until: new DateTime(2026, 12, 24));
        Assert.Equal(new DateTime(2026, 12, 24), RecurrenceSpec.UntilOf(until));
    }

    [Fact]
    public void An_unchanged_rule_stays_the_same_object()
    {
        var current = new RecurrenceRule("FREQ=WEEKLY;BYDAY=TU;WKST=SU");
        var kept = EventDraft.RecurrenceRuleFor(current, RecurrenceKind.Weekly, RecurrenceSpec.Of(current, Start), null, Start, allDay: false);
        Assert.Same(current, kept);
        Assert.Null(EventDraft.RecurrenceRuleFor(current, RecurrenceKind.None, null, null, Start, allDay: false));
    }
}
