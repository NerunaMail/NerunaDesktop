using System.Text.Json;
using System.Xml.Linq;
using Neruna.Core.Calendar;
using Neruna.Providers.Ews;
using Neruna.Providers.Graph;

namespace Neruna.Client.Tests;

/// <summary>Categories of events: iCalendar CATEGORIES, the categories of Microsoft 365 and Exchange, colour by name.</summary>
public class CalendarCategoryTests
{
    private static EventDraft Draft(params string[] categories) =>
        EventDraft.New(new DateTime(2026, 11, 3, 14, 0, 0)) with { Summary = "Planung", Categories = categories };

    [Fact]
    public void Categories_go_into_icalendar_and_back()
    {
        var ics = Draft("Besprechung", "Kunde").ToICalendar(null);
        Assert.Contains("CATEGORIES:Besprechung,Kunde", ics, StringComparison.Ordinal);
        Assert.Equal(["Besprechung", "Kunde"], EventDraft.FromICalendar(ics).Categories);

        var none = (EventDraft.FromICalendar(ics) with { Categories = [] }).ToICalendar(ics);
        Assert.DoesNotContain("CATEGORIES", none, StringComparison.Ordinal);
    }

    [Fact]
    public void Other_programs_lines_are_read_and_kept_unless_changed()
    {
        // Several CATEGORIES lines, one with two names; a draft without a list (null) leaves them as they are.
        const string ics = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VEVENT\r\nUID:x\r\nSUMMARY:Messe\r\nDTSTART:20261012T090000Z\r\nDTEND:20261012T100000Z\r\nCATEGORIES:Reise,Kunde\r\nCATEGORIES:Wichtig\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var draft = EventDraft.FromICalendar(ics);
        Assert.Equal(["Reise", "Kunde", "Wichtig"], draft.Categories);

        var kept = (draft with { Categories = null, Summary = "Messe Basel" }).ToICalendar(ics);
        Assert.Equal(["Reise", "Kunde", "Wichtig"], EventDraft.FromICalendar(kept).Categories);
    }

    [Fact]
    public void Colours_come_from_the_settings_by_name()
    {
        var known = new List<CalendarCategory> { new("Besprechung", "#0F6CBD"), new("Kunde", "#CA5010"), new("Reise", "#0B6A0B") };

        // In the order of the settings, case does not matter, unknown names have no colour.
        Assert.Equal(["Kunde", "Reise"], CalendarCategoryService.Of(["reise", "Unbekannt", "KUNDE"], known).Select(c => c.Name));
    }

    [Fact]
    public void Microsoft_365_categories()
    {
        var graphEvent = JsonDocument.Parse("""
            {"id":"E1","subject":"Kundenbesuch","categories":["Kunde","Rote Kategorie"],"isAllDay":false,
             "start":{"dateTime":"2026-10-12T09:00:00","timeZone":"UTC"},"end":{"dateTime":"2026-10-12T10:00:00","timeZone":"UTC"}}
            """).RootElement;
        var ical = GraphCalendarProvider.ToICalendar(graphEvent);
        Assert.Equal(["Kunde", "Rote Kategorie"], EventDraft.FromICalendar(ical).Categories);

        var sent = JsonSerializer.SerializeToElement(GraphCalendarProvider.ToGraph(Draft("Anruf").ToICalendar(null))).GetProperty("categories");
        Assert.Equal(["Anruf"], sent.EnumerateArray().Select(c => c.GetString()));
    }

    [Fact]
    public void Exchange_categories()
    {
        var item = XElement.Parse("""
            <t:CalendarItem xmlns:t="http://schemas.microsoft.com/exchange/services/2006/types"><t:ItemId Id="E1"/><t:Subject>Messe</t:Subject>
              <t:Categories><t:String>Reise</t:String><t:String>Kunde</t:String></t:Categories>
              <t:Start>2026-10-12T07:00:00Z</t:Start><t:End>2026-10-12T08:00:00Z</t:End></t:CalendarItem>
            """);
        Assert.Equal(["Reise", "Kunde"], EventDraft.FromICalendar(EwsCalendarConverter.ToICalendar(item, [], null)).Categories);

        var fields = EwsCalendarConverter.FieldsOf(Draft("Projekt").ToICalendar(null), out _);
        Assert.Equal(["Projekt"], fields.Single(f => f.Field == "item:Categories").Value!.Elements().Select(e => e.Value));
        Assert.Null(EwsCalendarConverter.FieldsOf(Draft().ToICalendar(null), out _).Single(f => f.Field == "item:Categories").Value);
    }
}
