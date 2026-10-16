using System.Text.Json;
using System.Xml.Linq;
using Neruna.Core.Calendar;
using Neruna.Providers.Ews;
using Neruna.Providers.Graph;

namespace Neruna.Client.Tests;

/// <summary>Free/busy of colleagues from Microsoft 365 (getSchedule) and Exchange (GetUserAvailability).</summary>
public class FreeBusyTests
{
    [Fact]
    public void Microsoft_365_get_schedule()
    {
        var answer = JsonDocument.Parse("""
            {"value":[
              {"scheduleId":"lea@example.com","availabilityView":"0220","scheduleItems":[
                {"status":"busy","subject":"Kundentermin","location":"Zürich","start":{"dateTime":"2026-10-12T07:00:00.0000000","timeZone":"UTC"},"end":{"dateTime":"2026-10-12T08:00:00.0000000","timeZone":"UTC"}},
                {"status":"free","start":{"dateTime":"2026-10-12T09:00:00.0000000","timeZone":"UTC"},"end":{"dateTime":"2026-10-12T10:00:00.0000000","timeZone":"UTC"}},
                {"status":"oof","start":{"dateTime":"2026-10-13T00:00:00.0000000","timeZone":"UTC"},"end":{"dateTime":"2026-10-14T00:00:00.0000000","timeZone":"UTC"}}]},
              {"scheduleId":"nobody@other.example","error":{"message":"The user does not exist","responseCode":"ErrorMailRecipientNotFound"}}]}
            """).RootElement.GetProperty("value");

        var lea = GraphCalendarProvider.FreeBusyOf("lea@example.com", answer[0]);
        Assert.True(lea.IsKnown);
        Assert.Equal(2, lea.Slots.Count);
        Assert.Equal(new BusySlot(new DateTimeOffset(2026, 10, 12, 7, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 12, 8, 0, 0, TimeSpan.Zero), ShowAs.Busy, "Kundentermin", "Zürich"), lea.Slots[0]);
        Assert.Equal(ShowAs.OutOfOffice, lea.Slots[1].ShowAs);

        var nobody = GraphCalendarProvider.FreeBusyOf("nobody@other.example", answer[1]);
        Assert.False(nobody.IsKnown);
        Assert.Empty(nobody.Slots);
    }

    [Fact]
    public void Exchange_get_user_availability()
    {
        var request = EwsCalendarProvider.AvailabilityRequest(["lea@example.com", "marco@example.com"],
            new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.FromHours(2)), new DateTimeOffset(2026, 10, 19, 0, 0, 0, TimeSpan.FromHours(2)));
        Assert.Equal("GetUserAvailabilityRequest", request.Name.LocalName);
        Assert.Equal(["lea@example.com", "marco@example.com"], request.Descendants(X.T + "Address").Select(a => a.Value));
        Assert.Equal("2026-10-11T22:00:00", request.Descendants(X.T + "StartTime").Single().Value);
        Assert.Equal("Detailed", request.Descendants(X.T + "RequestedView").Single().Value);

        var body = XElement.Parse($"""
            <s:Body xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><m:GetUserAvailabilityResponse xmlns:m="{X.M}" xmlns:t="{X.T}"><m:FreeBusyResponseArray>
              <m:FreeBusyResponse><m:ResponseMessage ResponseClass="Success"><m:ResponseCode>NoError</m:ResponseCode></m:ResponseMessage>
                <m:FreeBusyView><t:FreeBusyViewType>Detailed</t:FreeBusyViewType><t:CalendarEventArray>
                  <t:CalendarEvent><t:StartTime>2026-10-12T07:00:00</t:StartTime><t:EndTime>2026-10-12T08:00:00</t:EndTime><t:BusyType>Busy</t:BusyType>
                    <t:CalendarEventDetails><t:Subject>Kundentermin</t:Subject><t:Location>Zürich</t:Location></t:CalendarEventDetails></t:CalendarEvent>
                  <t:CalendarEvent><t:StartTime>2026-10-12T09:00:00</t:StartTime><t:EndTime>2026-10-12T10:00:00</t:EndTime><t:BusyType>Free</t:BusyType></t:CalendarEvent>
                  <t:CalendarEvent><t:StartTime>2026-10-13T12:00:00</t:StartTime><t:EndTime>2026-10-13T13:00:00</t:EndTime><t:BusyType>Tentative</t:BusyType></t:CalendarEvent>
                </t:CalendarEventArray></m:FreeBusyView></m:FreeBusyResponse>
              <m:FreeBusyResponse><m:ResponseMessage ResponseClass="Error"><m:MessageText>Mailbox not found</m:MessageText><m:ResponseCode>ErrorMailRecipientNotFound</m:ResponseCode></m:ResponseMessage></m:FreeBusyResponse>
            </m:FreeBusyResponseArray></m:GetUserAvailabilityResponse></s:Body>
            """);

        var answers = EwsCalendarConverter.AvailabilityOf(["lea@example.com", "marco@example.com"], body);
        Assert.Equal(2, answers[0].Slots.Count);
        Assert.Equal(("Kundentermin", "Zürich", ShowAs.Busy), (answers[0].Slots[0].Subject, answers[0].Slots[0].Location, answers[0].Slots[0].ShowAs));
        Assert.Equal(new DateTimeOffset(2026, 10, 12, 7, 0, 0, TimeSpan.Zero), answers[0].Slots[0].Start);
        Assert.Equal(ShowAs.Tentative, answers[0].Slots[1].ShowAs);
        Assert.Equal(("Mailbox not found", false), (answers[1].Error, answers[1].IsKnown));
    }
}
