using System.Text.Json;
using System.Xml.Linq;
using Neruna.Core.Accounts;
using Neruna.Core.Calendar;
using Neruna.Providers.Ews;
using Neruna.Providers.Graph;

namespace Neruna.Client.Tests;

/// <summary>"Anzeigen als": busy, free, tentative, away – kept through iCalendar, Exchange and Microsoft 365.</summary>
public class ShowAsTests
{
    private static EventDraft Draft(ShowAs showAs) => EventDraft.New(new DateTime(2026, 11, 3, 14, 0, 0)) with { Summary = "Planung", ShowAs = showAs };

    [Theory]
    [InlineData(ShowAs.Busy, "TRANSP:OPAQUE", null)]
    [InlineData(ShowAs.Free, "TRANSP:TRANSPARENT", null)]
    [InlineData(ShowAs.Tentative, "TRANSP:OPAQUE", "X-MICROSOFT-CDO-BUSYSTATUS:TENTATIVE")]
    [InlineData(ShowAs.OutOfOffice, "TRANSP:OPAQUE", "X-MICROSOFT-CDO-BUSYSTATUS:OOF")]
    public void Show_as_goes_into_icalendar_and_back(ShowAs showAs, string transp, string? status)
    {
        var ics = Draft(showAs).ToICalendar(null);

        Assert.Contains(transp, ics, StringComparison.Ordinal);
        if (status is null)
        {
            Assert.DoesNotContain("X-MICROSOFT-CDO-BUSYSTATUS", ics, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains(status, ics, StringComparison.Ordinal);
        }

        Assert.Equal(showAs, EventDraft.FromICalendar(ics).ShowAs);
    }

    [Fact]
    public void Other_programs_free_events_and_a_change_back_to_busy()
    {
        const string free = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VEVENT\r\nUID:x\r\nSUMMARY:Ferien\r\nDTSTART;VALUE=DATE:20261012\r\nDTEND;VALUE=DATE:20261017\r\nTRANSP:TRANSPARENT\r\nX-MICROSOFT-CDO-BUSYSTATUS:FREE\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var draft = EventDraft.FromICalendar(free);
        Assert.Equal(ShowAs.Free, draft.ShowAs);

        var busy = (draft with { ShowAs = ShowAs.Busy }).ToICalendar(free);
        Assert.Contains("TRANSP:OPAQUE", busy, StringComparison.Ordinal);
        Assert.Contains("X-MICROSOFT-CDO-BUSYSTATUS:BUSY", busy, StringComparison.Ordinal);
        Assert.Equal(ShowAs.Busy, EventDraft.FromICalendar(busy).ShowAs);
    }

    [Fact]
    public void Microsoft_365_show_as()
    {
        var graphEvent = JsonDocument.Parse("""
            {"id":"E1","subject":"Kundenbesuch","showAs":"oof","isAllDay":false,
             "start":{"dateTime":"2026-10-12T09:00:00","timeZone":"UTC"},"end":{"dateTime":"2026-10-12T10:00:00","timeZone":"UTC"}}
            """).RootElement;
        var ical = GraphCalendarProvider.ToICalendar(graphEvent);
        Assert.Equal(ShowAs.OutOfOffice, EventDraft.FromICalendar(ical).ShowAs);

        Assert.Equal("oof", JsonSerializer.SerializeToElement(GraphCalendarProvider.ToGraph(ical)).GetProperty("showAs").GetString());
        Assert.Equal("free", JsonSerializer.SerializeToElement(GraphCalendarProvider.ToGraph(Draft(ShowAs.Free).ToICalendar(null))).GetProperty("showAs").GetString());
    }

    [Fact]
    public void Exchange_show_as()
    {
        var item = XElement.Parse($"""
            <t:CalendarItem xmlns:t="http://schemas.microsoft.com/exchange/services/2006/types"><t:ItemId Id="E1"/><t:Subject>Vielleicht</t:Subject>
              <t:Start>2026-10-12T07:00:00Z</t:Start><t:End>2026-10-12T08:00:00Z</t:End><t:LegacyFreeBusyStatus>Tentative</t:LegacyFreeBusyStatus></t:CalendarItem>
            """);
        var ical = EwsCalendarConverter.ToICalendar(item, [], null);
        Assert.Equal(ShowAs.Tentative, EventDraft.FromICalendar(ical).ShowAs);

        var fields = EwsCalendarConverter.FieldsOf(Draft(ShowAs.Free).ToICalendar(null), out _);
        Assert.Equal("Free", fields.Single(f => f.Field == "calendar:LegacyFreeBusyStatus").Value!.Value);
    }

    [Fact]
    public void Whose_calendar_is_known_for_conflicts()
    {
        var connection = new ServiceConnection(Guid.NewGuid(), ServiceKind.Calendar, "caldav", new Dictionary<string, string>());
        var anna = new Account(Guid.NewGuid(), "Anna", "anna@example.com", [connection], Aliases: [new MailIdentity("info@example.com", "Info")]);
        var sources = new[] { new CalendarSource(anna, connection) };
        CalendarInfo Calendar(string path) => new(connection.Id, "https://dav.example.com" + path, "K", null, false);

        // Own account (also an alias), but not a calendar of someone else reached through it.
        Assert.True(EventConflicts.Belongs(Calendar("/calendars/anna@example.com/personal/"), "anna@example.com", sources));
        Assert.True(EventConflicts.Belongs(Calendar("/calendars/anna/personal/"), "info@example.com", sources));
        Assert.False(EventConflicts.Belongs(Calendar("/calendars/lea@example.com/personal/"), "anna@example.com", sources));
        // Someone else: their own calendar home.
        Assert.True(EventConflicts.Belongs(Calendar("/calendars/lea%40example.com/personal/"), "lea@example.com", sources));
        Assert.False(EventConflicts.Belongs(Calendar("/calendars/anna/personal/"), "lea@example.com", sources));

        // Shared with the user: the address in a subscription's name or path, as the calendar's name, as whose it is,
        // or the person's user name as the home (Nextcloud) – but never the own home for a namesake elsewhere.
        Assert.True(EventConflicts.Belongs(Calendar("/SOGo/dav/anna/Calendar/luke.hasse@ender-informatics.ch_personal/"), "luke.hasse@ender-informatics.ch", sources));
        Assert.True(EventConflicts.Belongs(Calendar("/calendars/anna/abc123/") with { Name = "luke.hasse@ender-informatics.ch" }, "Luke.Hasse@ender-informatics.ch", sources));
        Assert.True(EventConflicts.Belongs(Calendar("/calendars/anna/abc123/") with { SharedBy = "Luke Hasse <luke.hasse@ender-informatics.ch>" }, "luke.hasse@ender-informatics.ch", sources));
        Assert.True(EventConflicts.Belongs(Calendar("/remote.php/dav/calendars/luke.hasse/personal/"), "luke.hasse@ender-informatics.ch", sources));
        Assert.False(EventConflicts.Belongs(Calendar("/remote.php/dav/calendars/anna/personal/"), "anna@gmail.com", sources));
        Assert.False(EventConflicts.Belongs(Calendar("/calendars/anna/abc123/") with { Name = "Ferien" }, "luke.hasse@ender-informatics.ch", sources));
    }
}
