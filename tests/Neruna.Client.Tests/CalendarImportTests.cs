using Neruna.Core.Calendar;
using Calendar = Ical.Net.Calendar;

namespace Neruna.Client.Tests;

public class CalendarImportTests
{
    private const string File = """
        BEGIN:VCALENDAR
        VERSION:2.0
        PRODID:-//Schule//DE
        METHOD:REQUEST
        BEGIN:VTIMEZONE
        TZID:Europe/Zurich
        BEGIN:STANDARD
        DTSTART:19701025T030000
        TZOFFSETFROM:+0200
        TZOFFSETTO:+0100
        RRULE:FREQ=YEARLY;BYMONTH=10;BYDAY=-1SU
        END:STANDARD
        BEGIN:DAYLIGHT
        DTSTART:19700329T020000
        TZOFFSETFROM:+0100
        TZOFFSETTO:+0200
        RRULE:FREQ=YEARLY;BYMONTH=3;BYDAY=-1SU
        END:DAYLIGHT
        END:VTIMEZONE
        BEGIN:VEVENT
        UID:mathe@schule.example
        DTSTAMP:20261001T000000Z
        DTSTART;TZID=Europe/Zurich:20261019T081000
        DTEND;TZID=Europe/Zurich:20261019T085500
        RRULE:FREQ=WEEKLY;COUNT=10
        SUMMARY:Mathematik
        ORGANIZER:mailto:sekretariat@schule.example
        ATTENDEE;RSVP=TRUE:mailto:anna@example.com
        END:VEVENT
        BEGIN:VEVENT
        UID:mathe@schule.example
        RECURRENCE-ID;TZID=Europe/Zurich:20261026T081000
        DTSTAMP:20261001T000000Z
        DTSTART;TZID=Europe/Zurich:20261026T100000
        DTEND;TZID=Europe/Zurich:20261026T104500
        SUMMARY:Mathematik (Zimmer 12)
        END:VEVENT
        BEGIN:VEVENT
        DTSTAMP:20261001T000000Z
        DTSTART;VALUE=DATE:20261224
        SUMMARY:Ferienbeginn
        END:VEVENT
        BEGIN:VTODO
        UID:aufgabe@schule.example
        SUMMARY:Hausaufgaben
        END:VTODO
        END:VCALENDAR
        """;

    [Fact]
    public void A_file_becomes_one_event_per_uid_without_attendees_and_with_its_time_zone()
    {
        var events = CalendarImport.Read(File);

        Assert.Equal(["Mathematik", "Ferienbeginn"], events.Select(e => e.Summary));
        var series = Calendar.Load(events[0].ICalendarData)!;
        Assert.Equal(2, series.Events.Count);
        Assert.All(series.Events, e => Assert.Equal("mathe@schule.example", e.Uid));
        Assert.All(series.Events, e => Assert.Empty(e.Attendees));
        Assert.All(series.Events, e => Assert.Null(e.Organizer));
        Assert.Equal("Europe/Zurich", Assert.Single(series.TimeZones).TzId);
        Assert.DoesNotContain("METHOD", events[0].ICalendarData, StringComparison.Ordinal);

        // Without a UID: one is made up.
        Assert.False(string.IsNullOrWhiteSpace(events[1].Uid));
        Assert.Contains("UID:" + events[1].Uid, events[1].ICalendarData, StringComparison.Ordinal);

        var copy = CalendarImport.WithNewUid(events[0]);
        Assert.NotEqual(events[0].Uid, copy.Uid);
        Assert.All(Calendar.Load(copy.ICalendarData)!.Events, e => Assert.Equal(copy.Uid, e.Uid));
    }

    [Fact]
    public void Something_else_than_icalendar_is_refused()
    {
        Assert.Throws<FormatException>(() => CalendarImport.Read("Das ist kein Kalender."));
        Assert.Throws<FormatException>(() => CalendarImport.Read("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nEND:VCALENDAR\r\n"));
    }
}
