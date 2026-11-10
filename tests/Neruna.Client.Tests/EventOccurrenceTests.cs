using Calendar = Ical.Net.Calendar;
using Neruna.Core.Calendar;

namespace Neruna.Client.Tests;

public class EventOccurrenceTests
{
    private const string Series = """
        BEGIN:VCALENDAR
        VERSION:2.0
        PRODID:-//SOGo//EN
        BEGIN:VEVENT
        UID:series-1@example.com
        DTSTAMP:20261001T080000Z
        DTSTART:20261014T070000Z
        DTEND:20261014T080000Z
        RRULE:FREQ=WEEKLY
        SUMMARY:Jour fixe
        SEQUENCE:3
        BEGIN:VALARM
        ACTION:DISPLAY
        TRIGGER:-PT15M
        DESCRIPTION:Erinnerung
        END:VALARM
        END:VEVENT
        BEGIN:VEVENT
        UID:series-1@example.com
        RECURRENCE-ID:20261021T070000Z
        DTSTAMP:20261001T080000Z
        DTSTART:20261021T090000Z
        DTEND:20261021T100000Z
        SUMMARY:Jour fixe (verschoben)
        X-NERUNA-OCCURRENCE-ID:occ-21
        END:VEVENT
        END:VCALENDAR
        """;

    private static readonly DateTimeOffset Third = new(2026, 10, 28, 7, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Moved = new(2026, 10, 21, 7, 0, 0, TimeSpan.Zero);
    private static readonly CalendarInfo Info = new(Guid.NewGuid(), "cal", "Kalender", null, false);

    private static List<CalendarOccurrence> Occurrences(string data) =>
        [.. CalendarController.Expand(Info, new CalendarObject("1", null, data), new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero))];

    [Fact]
    public void Occurrences_know_their_place_in_the_series()
    {
        var list = Occurrences(Series);

        Assert.Equal(4, list.Count);
        Assert.All(list, o => Assert.NotNull(o.RecurrenceId));
        var moved = list.Single(o => o.Summary == "Jour fixe (verschoben)");
        Assert.Equal(Moved, moved.RecurrenceId);
        Assert.Equal(new DateTimeOffset(2026, 10, 21, 9, 0, 0, TimeSpan.Zero), moved.Start);
    }

    [Fact]
    public void Draft_of_an_occurrence_is_the_series_at_that_time_or_its_override()
    {
        var plain = EventDraft.FromOccurrence(Series, Third);
        Assert.Equal("Jour fixe", plain.Summary);
        Assert.Equal(Third.LocalDateTime, plain.Start);
        Assert.Equal(TimeSpan.FromHours(1), plain.End - plain.Start);
        Assert.Equal(RecurrenceKind.None, plain.Recurrence);
        Assert.Equal(15, plain.ReminderMinutes);

        var moved = EventDraft.FromOccurrence(Series, Moved);
        Assert.Equal("Jour fixe (verschoben)", moved.Summary);
        Assert.Equal(new DateTimeOffset(2026, 10, 21, 9, 0, 0, TimeSpan.Zero).LocalDateTime, moved.Start);
    }

    [Fact]
    public void Changing_one_occurrence_adds_an_override_and_leaves_the_series()
    {
        var draft = EventDraft.FromOccurrence(Series, Third) with { Summary = "Ausnahmsweise", Start = Third.LocalDateTime.AddHours(2), End = Third.LocalDateTime.AddHours(3) };

        var data = draft.ToOccurrenceICalendar(Series, Third);

        var calendar = Calendar.Load(data)!;
        Assert.Equal(3, calendar.Events.Count);
        var master = calendar.Events.Single(e => e.RecurrenceIdentifier is null);
        Assert.Equal("Jour fixe", master.Summary);
        Assert.NotNull(master.RecurrenceRule);
        Assert.Equal(3, master.Sequence);
        var own = calendar.Events.Single(e => e.Summary == "Ausnahmsweise");
        Assert.Equal("series-1@example.com", own.Uid);
        Assert.Null(own.RecurrenceRule);
        Assert.Single(own.Alarms);

        var list = Occurrences(data);
        Assert.Equal(4, list.Count);
        var changed = list.Single(o => o.Summary == "Ausnahmsweise");
        Assert.Equal(Third, changed.RecurrenceId);
        Assert.Equal(Third.AddHours(2), changed.Start);
    }

    [Fact]
    public void Changing_an_override_again_updates_it()
    {
        var draft = EventDraft.FromOccurrence(Series, Moved) with { Location = "Sitzungszimmer" };

        var data = draft.ToOccurrenceICalendar(Series, Moved);

        var calendar = Calendar.Load(data)!;
        Assert.Equal(2, calendar.Events.Count);
        var own = calendar.Events.Single(e => e.RecurrenceIdentifier is not null);
        Assert.Equal("Sitzungszimmer", own.Location);
        Assert.Equal("occ-21", EventDraft.OccurrenceIdOf(data, Moved));
        Assert.Null(EventDraft.OccurrenceIdOf(data, Third));
    }

    [Fact]
    public void Removing_one_occurrence_leaves_the_others()
    {
        var data = EventDraft.WithoutOccurrence(Series, Moved);
        data = EventDraft.WithoutOccurrence(data, Third);

        var list = Occurrences(data);
        Assert.Equal(2, list.Count);
        Assert.DoesNotContain(list, o => o.RecurrenceId == Moved || o.RecurrenceId == Third);
        Assert.Single(Calendar.Load(data)!.Events);
    }

    [Fact]
    public void An_override_alone_is_a_single_event()
    {
        var alone = EventDraft.OccurrenceAlone(Series, Moved);

        Assert.NotNull(alone);
        var evt = Calendar.Load(alone)!.Events.Single();
        Assert.Null(evt.RecurrenceIdentifier);
        Assert.Equal("Jour fixe (verschoben)", EventDraft.FromICalendar(alone).Summary);
        Assert.Null(EventDraft.OccurrenceAlone(Series, Third));
    }

    [Fact]
    public void All_day_series_get_a_date_as_recurrence_id()
    {
        const string allDay = """
            BEGIN:VCALENDAR
            VERSION:2.0
            PRODID:test
            BEGIN:VEVENT
            UID:day@example.com
            DTSTAMP:20261001T080000Z
            DTSTART;VALUE=DATE:20261005
            DTEND;VALUE=DATE:20261006
            RRULE:FREQ=DAILY;COUNT=5
            SUMMARY:Ferien
            END:VEVENT
            END:VCALENDAR
            """;
        var occurrence = Occurrences(allDay)[2];

        var data = (EventDraft.FromOccurrence(allDay, occurrence.RecurrenceId!.Value) with { Summary = "Ausflug" }).ToOccurrenceICalendar(allDay, occurrence.RecurrenceId.Value);

        Assert.Contains("RECURRENCE-ID;VALUE=DATE:20261007", data.Replace("\r\n ", string.Empty), StringComparison.Ordinal);
        Assert.Equal(["Ferien", "Ferien", "Ausflug", "Ferien", "Ferien"], Occurrences(data).Select(o => o.Summary));
        Assert.Equal(4, Occurrences(EventDraft.WithoutOccurrence(allDay, occurrence.RecurrenceId.Value)).Count);
    }
}
