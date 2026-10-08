using Neruna.Core.Calendar;

namespace Neruna.Client.Tests;

public class EventDraftTests
{
    private const string Meeting = """
        BEGIN:VCALENDAR
        VERSION:2.0
        PRODID:-//SOGo//EN
        BEGIN:VEVENT
        UID:meeting-1@example.com
        DTSTAMP:20261001T080000Z
        DTSTART:20261014T070000Z
        DTEND:20261014T080000Z
        RRULE:FREQ=WEEKLY;BYDAY=MO,WE
        SUMMARY:Jour fixe
        SEQUENCE:3
        ORGANIZER;CN=Thomas Frei:mailto:thomas.frei@example.com
        ATTENDEE;CN=Anna Muster;PARTSTAT=ACCEPTED:mailto:anna@example.com
        X-SOGO-SEND-APPOINTMENT-NOTIFICATIONS:NO
        BEGIN:VALARM
        ACTION:DISPLAY
        TRIGGER:-PT15M
        DESCRIPTION:Erinnerung
        END:VALARM
        END:VEVENT
        BEGIN:VEVENT
        UID:meeting-1@example.com
        RECURRENCE-ID:20261021T070000Z
        DTSTAMP:20261001T080000Z
        DTSTART:20261021T090000Z
        DTEND:20261021T100000Z
        SUMMARY:Jour fixe (verschoben)
        END:VEVENT
        END:VCALENDAR
        """;

    [Fact]
    public void Editing_touches_only_editable_fields()
    {
        var draft = EventDraft.FromICalendar(Meeting);
        Assert.Equal("Jour fixe", draft.Summary);
        Assert.Equal(RecurrenceKind.Custom, draft.Recurrence);

        var updated = (draft with { Summary = "Jour fixe Team", Location = "Pilatus" }).ToICalendar(Meeting);

        Assert.Contains("SUMMARY:Jour fixe Team", updated, StringComparison.Ordinal);
        Assert.Contains("LOCATION:Pilatus", updated, StringComparison.Ordinal);
        Assert.Contains("ATTENDEE;CN=Anna Muster;PARTSTAT=ACCEPTED:mailto:anna@example.com", updated, StringComparison.Ordinal);
        Assert.Contains("X-SOGO-SEND-APPOINTMENT-NOTIFICATIONS:NO", updated, StringComparison.Ordinal);
        Assert.Contains("TRIGGER:-PT15M", updated, StringComparison.Ordinal);
        Assert.Contains("RRULE:FREQ=WEEKLY;BYDAY=MO,WE", updated, StringComparison.Ordinal);
        Assert.Contains("SUMMARY:Jour fixe (verschoben)", updated, StringComparison.Ordinal);
        Assert.Contains("SEQUENCE:4", updated, StringComparison.Ordinal);
        Assert.Contains("UID:meeting-1@example.com", updated, StringComparison.Ordinal);
    }

    [Fact]
    public void New_timed_event_carries_a_complete_time_zone()
    {
        var data = (EventDraft.New(new DateTime(2026, 10, 14, 9, 0, 0)) with { Summary = "Neu", Recurrence = RecurrenceKind.Weekly }).ToICalendar(null);

        var tzId = EventDraft.LocalTimeZoneId();
        if (tzId != "UTC")
        {
            Assert.Contains($"DTSTART;TZID={tzId}:20261014T090000", data, StringComparison.Ordinal);
            Assert.Contains("BEGIN:VTIMEZONE", data, StringComparison.Ordinal);
            Assert.Matches("BEGIN:(STANDARD|DAYLIGHT)", data);
        }

        Assert.Contains("RRULE:FREQ=WEEKLY", data, StringComparison.Ordinal);
        Assert.Matches("UID:[0-9a-f-]{36}@neruna", data);

        var reread = EventDraft.FromICalendar(data);
        Assert.Equal(new DateTime(2026, 10, 14, 9, 0, 0), reread.Start);
        Assert.Equal(new DateTime(2026, 10, 14, 10, 0, 0), reread.End);
        Assert.Equal(RecurrenceKind.Weekly, reread.Recurrence);
    }

    [Fact]
    public void All_day_events_use_dates_with_exclusive_end()
    {
        var draft = new EventDraft("Ausflug", "Rigi", null, new DateTime(2026, 10, 16), new DateTime(2026, 10, 18), true, RecurrenceKind.None);

        var data = draft.ToICalendar(null);

        Assert.Contains("DTSTART;VALUE=DATE:20261016", data, StringComparison.Ordinal);
        Assert.Contains("DTEND;VALUE=DATE:20261018", data, StringComparison.Ordinal);
        var reread = EventDraft.FromICalendar(data);
        Assert.True(reread.IsAllDay);
        Assert.Equal(new DateTime(2026, 10, 18), reread.End);
    }

    [Fact]
    public void Removing_recurrence_drops_rule()
    {
        var data = (EventDraft.FromICalendar(Meeting) with { Recurrence = RecurrenceKind.None }).ToICalendar(Meeting);

        // The VTIMEZONE may carry its own DST rules; the event must not.
        Assert.DoesNotContain("RRULE:FREQ=WEEKLY", data, StringComparison.Ordinal);
    }

    [Fact]
    public void Event_with_duration_instead_of_end_can_be_edited()
    {
        const string withDuration = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:x\r\nBEGIN:VEVENT\r\nUID:d\r\nDTSTAMP:20261001T000000Z\r\nDTSTART:20261014T070000Z\r\nDURATION:PT90M\r\nSUMMARY:Lang\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

        var draft = EventDraft.FromICalendar(withDuration);
        Assert.Equal(TimeSpan.FromMinutes(90), draft.End - draft.Start);

        var data = (draft with { Summary = "Kurz" }).ToICalendar(withDuration);
        Assert.DoesNotContain("DURATION", data, StringComparison.Ordinal);
        Assert.Contains("DTEND", data, StringComparison.Ordinal);
    }
}
