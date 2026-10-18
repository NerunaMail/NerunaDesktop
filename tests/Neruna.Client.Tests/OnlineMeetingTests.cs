using Ical.Net;
using Neruna.Core.Calendar;
using Neruna.Core.Meetings;

namespace Neruna.Client.Tests;

public class OnlineMeetingTests
{
    private static EventDraft Draft() => EventDraft.New(new DateTime(2026, 11, 3, 10, 0, 0), null) with { Summary = "Projekt" };

    [Fact]
    public void Meeting_is_stored_as_conference_with_label_and_read_back()
    {
        var meeting = new OnlineMeeting(MeetingKinds.Zoom, "https://us02web.zoom.us/j/123?pwd=x", "Meeting-ID: 123");
        var data = (Draft() with { Description = meeting.AddTo("Agenda") }).ToICalendarWith(meeting);

        Assert.Contains("CONFERENCE;VALUE=URI;FEATURE=VIDEO;LABEL=Zoom:https://us02web.zoom.us/j/123?pwd=x", data.Replace("\r\n ", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        var read = EventDraft.FromICalendar(data);
        Assert.Equal(meeting with { Details = null }, read.Meeting);
        Assert.StartsWith("Agenda\n\n____", read.Description, StringComparison.Ordinal);
        Assert.Contains("Teilnehmen: https://us02web.zoom.us/j/123?pwd=x", read.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Teams_link_also_gets_the_microsoft_property_and_a_pending_meeting_only_its_service()
    {
        var teams = Draft().ToICalendarWith(new OnlineMeeting(MeetingKinds.Teams, "https://teams.microsoft.com/l/meetup-join/abc")).Replace("\r\n ", string.Empty, StringComparison.Ordinal);
        Assert.Contains("X-MICROSOFT-SKYPETEAMSMEETINGURL:https://teams.microsoft.com/l/meetup-join/abc", teams, StringComparison.Ordinal);

        var pending = EventDraft.FromICalendar(Draft().ToICalendarWith(new OnlineMeeting(MeetingKinds.Teams, null)));
        Assert.Equal(new OnlineMeeting(MeetingKinds.Teams, null), pending.Meeting);
        Assert.True(pending.Meeting!.IsPending);
    }

    [Fact]
    public void Removing_the_meeting_removes_its_properties_and_join_text_but_keeps_other_text()
    {
        var meeting = new OnlineMeeting(MeetingKinds.Jitsi, "https://meet.example.org/NerunaAbc");
        var data = (Draft() with { Description = meeting.AddTo("Vorher") + "\n\nNachher" }).ToICalendarWith(meeting);
        var draft = EventDraft.FromICalendar(data);

        var removed = (draft with { Meeting = null, Description = OnlineMeeting.RemoveFrom(draft.Description, meeting.JoinUrl) }).ToICalendar(data);
        var evt = Calendar.Load(removed)!.Events[0]!;
        Assert.Null(OnlineMeeting.Of(evt));
        Assert.Equal("Vorher\n\nNachher", evt.Description);
    }

    [Fact]
    public void Links_from_other_programs_are_recognised()
    {
        const string outlook = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VEVENT\r\nUID:1\r\nDTSTART:20261103T090000Z\r\nDTEND:20261103T100000Z\r\nX-MICROSOFT-SKYPETEAMSMEETINGURL:https://teams.microsoft.com/l/meetup-join/xyz\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        Assert.Equal(new OnlineMeeting(MeetingKinds.Teams, "https://teams.microsoft.com/l/meetup-join/xyz"), EventDraft.FromICalendar(outlook).Meeting);
        Assert.Equal(MeetingKinds.Google, MeetingKinds.FromUrl("https://meet.google.com/abc-defg-hij"));
        Assert.Equal(MeetingKinds.Zoom, MeetingKinds.FromUrl("https://ethz.zoom.us/j/1"));
    }

    [Fact]
    public async Task Jitsi_rooms_are_random_and_on_the_chosen_server()
    {
        var service = new MeetingService(Guid.NewGuid(), MeetingKinds.Jitsi, "Jitsi", new Dictionary<string, string> { [JitsiMeetingProvider.ServerKey] = "https://meet.example.org/" });
        using var http = new HttpClient();
        var provider = new JitsiMeetingProvider(http);
        var request = new MeetingRequest("Projekt", DateTimeOffset.Now, DateTimeOffset.Now.AddHours(1));
        var first = (await provider.CreateAsync(service, request, TestContext.Current.CancellationToken)).JoinUrl!;
        var second = (await provider.CreateAsync(service, request, TestContext.Current.CancellationToken)).JoinUrl!;

        Assert.Matches("^https://meet\\.example\\.org/Neruna[a-z2-9]{16}$", first);
        Assert.NotEqual(first, second);
    }
}

internal static class MeetingDraftExtensions
{
    public static string ToICalendarWith(this EventDraft draft, OnlineMeeting meeting) => (draft with { Meeting = meeting }).ToICalendar(null);
}
