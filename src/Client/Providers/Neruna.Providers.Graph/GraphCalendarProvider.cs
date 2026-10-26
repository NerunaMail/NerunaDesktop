using System.Globalization;
using System.Text.Json;
using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Serialization;
using Neruna.Core;
using Neruna.Core.Calendar;
using Neruna.Core.Meetings;
using CalendarObject = Neruna.Core.Calendar.CalendarObject;
using ICalendar = Ical.Net.Calendar;

namespace Neruna.Providers.Graph;

/// <summary>
/// Calendars through Microsoft Graph. Graph events are converted to iCalendar (and back), so the rest of Neruna sees
/// what it sees from CalDAV. The version of an event is Graph's change key. The server delivers invitations itself.
/// Not (yet) mapped: changed single occurrences of a series.
/// </summary>
internal sealed class GraphCalendarProvider(Guid connectionId, GraphClient graph) : ICalendarProvider, ICalendarScheduling, ICalendarFreeBusy, ICalendarOnlineMeetings
{
    /// <summary>
    /// Free/busy of people in the organisation (getSchedule) – also without their calendar being shared; subject and
    /// place only where they allow it. In batches of 20 addresses, times in UTC.
    /// </summary>
    public async Task<IReadOnlyList<Availability>> GetFreeBusyAsync(IReadOnlyList<string> emails, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(emails);
        var result = new List<Availability>();
        object When(DateTimeOffset value) => new { dateTime = value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture), timeZone = "UTC" };
        foreach (var batch in emails.Chunk(20))
        {
            var answer = await graph.SendJsonAsync(HttpMethod.Post, "me/calendar/getSchedule",
                new { schedules = batch, startTime = When(windowStart), endTime = When(windowEnd), availabilityViewInterval = 30 },
                cancellationToken, ("Prefer", "outlook.timezone=\"UTC\""));
            var byAddress = answer.Arr("value").ToDictionary(v => v.Str("scheduleId") ?? string.Empty, StringComparer.OrdinalIgnoreCase);
            foreach (var email in batch)
            {
                result.Add(byAddress.TryGetValue(email, out var schedule) ? FreeBusyOf(email, schedule) : new Availability(email, [], "not answered"));
            }
        }

        return result;
    }

    internal static Availability FreeBusyOf(string email, JsonElement schedule)
    {
        if (schedule.Obj("error").Str("message") is { } error)
        {
            return new Availability(email, [], error);
        }

        static DateTimeOffset Utc(JsonElement when) => new(DateTime.SpecifyKind(
            DateTime.Parse(when.Str("dateTime") ?? "1970-01-01T00:00:00", CultureInfo.InvariantCulture, DateTimeStyles.None), DateTimeKind.Utc));
        var slots = schedule.Arr("scheduleItems")
            .Select(i => (Item: i, Status: i.Str("status")))
            .Where(i => i.Status is not ("free" or "unknown"))
            .Select(i => new BusySlot(Utc(i.Item.Obj("start")), Utc(i.Item.Obj("end")), i.Status switch
            {
                "tentative" => ShowAs.Tentative,
                "oof" => ShowAs.OutOfOffice,
                _ => ShowAs.Busy,
            }, i.Item.Str("subject") is { Length: > 0 } subject ? subject : null, i.Item.Str("location") is { Length: > 0 } location ? location : null))
            .ToList();
        return new Availability(email, slots);
    }

    private const string EventFields = "id,changeKey,iCalUId,subject,body,start,end,isAllDay,location,attendees,organizer,recurrence,isReminderOn,reminderMinutesBeforeStart,showAs,isOnlineMeeting,onlineMeetingProvider,onlineMeeting";

    /// <summary>
    /// Teams, where the organisation has it for this calendar (work or school accounts): Exchange then creates the
    /// meeting itself when the event is saved – no Teams add-in, no further permission.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetOnlineMeetingKindsAsync(CalendarInfo calendar, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        if (GraphTasks.IsTaskList(calendar))
        {
            return [];
        }

        var info = await graph.GetAsync($"me/calendars/{calendar.RemoteId}?$select=allowedOnlineMeetingProviders", cancellationToken);
        return info.Arr("allowedOnlineMeetingProviders").Any(p => p.GetString() == TeamsProvider) ? [MeetingKinds.Teams] : [];
    }

    // Graph's name of Teams (personal accounts have "skypeForConsumer", which cannot be created any more).
    private const string TeamsProvider = "teamsForBusiness";

    public CalendarProviderCapabilities Capabilities => CalendarProviderCapabilities.Write | CalendarProviderCapabilities.Scheduling;

    public async Task TestConnectionAsync(CancellationToken cancellationToken = default) =>
        await graph.GetAsync("me/calendars?$top=1&$select=id", cancellationToken);

    /// <summary>The calendars, and – with the Tasks permission – the Microsoft To Do lists as task lists.</summary>
    public async Task<IReadOnlyList<CalendarInfo>> GetCalendarsAsync(CancellationToken cancellationToken = default) =>
        [.. (await graph.GetAllAsync("me/calendars?$top=100&$select=id,name,hexColor,canEdit,isDefaultCalendar", cancellationToken))
            .Select(c => new CalendarInfo(connectionId, c.Str("id")!, c.Str("name") ?? string.Empty,
                c.Str("hexColor") is { Length: > 0 } color ? color : null, !c.Bool("canEdit"), IsDefault: c.Bool("isDefaultCalendar"))),
         .. await GraphTasks.GetListsAsync(connectionId, graph, cancellationToken)];

    public async Task<CalendarSyncResult> SyncCalendarAsync(CalendarInfo calendar, IReadOnlyDictionary<string, string?> knownVersions, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        ArgumentNullException.ThrowIfNull(knownVersions);
        if (GraphTasks.IsTaskList(calendar))
        {
            return await GraphTasks.SyncAsync(calendar, knownVersions, graph, cancellationToken);
        }

        var listed = await graph.GetAllAsync($"me/calendars/{calendar.RemoteId}/events?$select=id,changeKey&$top=250", cancellationToken);
        var changed = new List<CalendarObject>();
        foreach (var entry in listed)
        {
            var id = entry.Str("id")!;
            if (knownVersions.TryGetValue(id, out var version) && version == entry.Str("changeKey"))
            {
                continue;
            }

            changed.Add(ToObject(await GetEventAsync(id, cancellationToken)));
        }

        var present = listed.Select(e => e.Str("id")).ToHashSet(StringComparer.Ordinal);
        return new CalendarSyncResult(string.Empty, false, changed, [.. knownVersions.Keys.Where(k => !present.Contains(k))]);
    }

    public async Task<CalendarObject> SaveAsync(CalendarInfo calendar, CalendarObject item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        ArgumentNullException.ThrowIfNull(item);
        if (GraphTasks.IsTaskList(calendar))
        {
            return await GraphTasks.SaveAsync(calendar, item, graph, cancellationToken);
        }

        var body = ToGraph(item.ICalendarData);
        JsonElement saved;
        if (string.IsNullOrEmpty(item.RemoteId))
        {
            saved = await graph.SendJsonAsync(HttpMethod.Post, $"me/calendars/{calendar.RemoteId}/events", body, cancellationToken);
        }
        else
        {
            // Graph has no ETag check on events: compare the change key first.
            var current = await graph.GetAsync($"me/events/{item.RemoteId}?$select=changeKey", cancellationToken);
            if (item.ETag is not null && current.Str("changeKey") != item.ETag)
            {
                throw new RemoteConflictException();
            }

            saved = await graph.SendJsonAsync(HttpMethod.Patch, $"me/events/{item.RemoteId}", body, cancellationToken);
        }

        return ToObject(await GetEventAsync(saved.Str("id")!, cancellationToken));
    }

    public async Task DeleteAsync(CalendarInfo calendar, CalendarObject item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        ArgumentNullException.ThrowIfNull(item);
        if (GraphTasks.IsTaskList(calendar))
        {
            await GraphTasks.DeleteAsync(calendar, item, graph, cancellationToken);
            return;
        }

        await graph.SendJsonAsync(HttpMethod.Delete, $"me/events/{item.RemoteId}", null, cancellationToken);
    }

    /// <summary>Exchange sends invitations and replies itself.</summary>
    public Task<bool> SchedulesItselfAsync(CalendarInfo calendar, CancellationToken cancellationToken = default) => Task.FromResult(true);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private Task<JsonElement> GetEventAsync(string id, CancellationToken cancellationToken) =>
        graph.GetAsync($"me/events/{id}?$select={EventFields}", cancellationToken, ("Prefer", "outlook.body-content-type=\"text\""));

    private static CalendarObject ToObject(JsonElement e) => new(e.Str("id")!, e.Str("changeKey"), ToICalendar(e));

    /// <summary>A Graph event as iCalendar (what Neruna stores and shows).</summary>
    internal static string ToICalendar(JsonElement e)
    {
        var calendar = new ICalendar { ProductId = "-//Neruna//Neruna Desktop//DE" };
        var evt = new CalendarEvent
        {
            Uid = e.Str("iCalUId") ?? e.Str("id"),
            Summary = e.Str("subject") ?? string.Empty,
            Location = e.Obj("location").Str("displayName") is { Length: > 0 } location ? location : null,
            Description = e.Obj("body").Str("content") is { Length: > 0 } text ? text.Trim() : null,
        };
        EventDraft.ApplyShowAs(evt, e.Str("showAs") switch
        {
            "free" => ShowAs.Free,
            "tentative" => ShowAs.Tentative,
            "oof" => ShowAs.OutOfOffice,
            _ => ShowAs.Busy,
        });
        var allDay = e.Bool("isAllDay");
        var (start, startZone) = DateOf(e.Obj("start"));
        var (end, _) = DateOf(e.Obj("end"));
        if (allDay)
        {
            evt.DtStart = new CalDateTime(DateOnly.FromDateTime(start));
            evt.DtEnd = new CalDateTime(DateOnly.FromDateTime(end > start ? end : start.AddDays(1)));
        }
        else
        {
            calendar.AddTimeZone(startZone);
            evt.DtStart = new CalDateTime(start, startZone);
            evt.DtEnd = new CalDateTime(end, startZone);
        }

        if (RecurrenceOf(e.Obj("recurrence")) is { } rule)
        {
            evt.RecurrenceRule = new RecurrenceRule(rule);
        }

        if (e.Obj("organizer").Obj("emailAddress").Str("address") is { } organizer && e.Arr("attendees").Any())
        {
            evt.Organizer = new Organizer("mailto:" + organizer) { CommonName = e.Obj("organizer").Obj("emailAddress").Str("name") };
        }

        foreach (var attendee in e.Arr("attendees"))
        {
            var address = attendee.Obj("emailAddress");
            if (address.Str("address") is not { } mail)
            {
                continue;
            }

            evt.Attendees.Add(new Attendee("mailto:" + mail)
            {
                CommonName = address.Str("name"),
                Role = attendee.Str("type") == "optional" ? "OPT-PARTICIPANT" : "REQ-PARTICIPANT",
                ParticipationStatus = attendee.Obj("status").Str("response") switch
                {
                    "accepted" or "organizer" => "ACCEPTED",
                    "tentativelyAccepted" => "TENTATIVE",
                    "declined" => "DECLINED",
                    _ => "NEEDS-ACTION",
                },
            });
        }

        // The join link Exchange created (Teams), for the editor's "Teilnehmen" and other programs.
        if (e.Bool("isOnlineMeeting") && e.Obj("onlineMeeting").Str("joinUrl") is { Length: > 0 } joinUrl)
        {
            OnlineMeeting.Apply(evt, new OnlineMeeting(e.Str("onlineMeetingProvider") == TeamsProvider ? MeetingKinds.Teams : MeetingKinds.FromUrl(joinUrl), joinUrl));
        }

        if (e.Bool("isReminderOn"))
        {
            evt.Alarms.Add(new Alarm { Action = "DISPLAY", Description = evt.Summary, Trigger = new Trigger(Duration.FromMinutes(-e.Int("reminderMinutesBeforeStart"))) });
        }

        calendar.Events.Add(evt);
        return new CalendarSerializer().SerializeToString(calendar) ?? throw new InvalidOperationException("Serializing the event failed.");
    }

    /// <summary>iCalendar (as Neruna edits it) as a Graph event.</summary>
    internal static Dictionary<string, object?> ToGraph(string iCalendarData)
    {
        var draft = EventDraft.FromICalendar(iCalendarData);
        var master = ICalendar.Load(iCalendarData)?.Events.FirstOrDefault(ev => ev.RecurrenceIdentifier is null);
        var zone = WindowsZone(EventDraft.LocalTimeZoneId());
        object When(DateTime value) => new { dateTime = value.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture), timeZone = zone };
        var start = draft.IsAllDay ? draft.Start.Date : draft.Start;
        var end = draft.IsAllDay ? (draft.End.Date > draft.Start.Date ? draft.End.Date : draft.Start.Date.AddDays(1)) : draft.End;
        var graphEvent = new Dictionary<string, object?>
        {
            ["subject"] = draft.Summary,
            ["body"] = new { contentType = "text", content = draft.Description ?? string.Empty },
            ["location"] = new { displayName = draft.Location ?? string.Empty },
            ["isAllDay"] = draft.IsAllDay,
            ["showAs"] = draft.ShowAs switch
            {
                ShowAs.Free => "free",
                ShowAs.Tentative => "tentative",
                ShowAs.OutOfOffice => "oof",
                _ => "busy",
            },
            ["start"] = When(start),
            ["end"] = When(end),
            ["isReminderOn"] = draft.ReminderMinutes is not null,
            ["reminderMinutesBeforeStart"] = draft.ReminderMinutes ?? 0,
            ["attendees"] = (draft.Attendees ?? []).Select(a => new { emailAddress = new { address = a.Email, name = a.Name }, type = "required" }).ToList(),
            ["recurrence"] = master?.RecurrenceRule is { } rule ? GraphRecurrence(rule, DateOnly.FromDateTime(start)) : null,
        };

        // A Teams meeting still to be created: Exchange makes it and adds the join text to the body. Once there it
        // stays (Graph cannot turn it off again), so later saves leave these fields alone.
        if (draft.Meeting is { IsPending: true, Service: MeetingKinds.Teams })
        {
            graphEvent["isOnlineMeeting"] = true;
            graphEvent["onlineMeetingProvider"] = TeamsProvider;
        }

        return graphEvent;
    }

    private static (DateTime Value, string Zone) DateOf(JsonElement when)
    {
        var value = DateTime.Parse(when.Str("dateTime") ?? "1970-01-01T00:00:00", CultureInfo.InvariantCulture, DateTimeStyles.None);
        var zone = when.Str("timeZone") ?? "UTC";
        return (DateTime.SpecifyKind(value, DateTimeKind.Unspecified), TimeZoneInfo.TryConvertWindowsIdToIanaId(zone, out var iana) ? iana : zone);
    }

    private static string WindowsZone(string zone) => TimeZoneInfo.TryConvertIanaIdToWindowsId(zone, out var windows) ? windows : zone;

    private static readonly string[] Days = ["sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday"];
    private static readonly string[] IcalDays = ["SU", "MO", "TU", "WE", "TH", "FR", "SA"];
    private static readonly string[] Index = ["first", "second", "third", "fourth", "last"];

    /// <summary>Graph's recurrence as an RRULE value; null when there is none.</summary>
    internal static string? RecurrenceOf(JsonElement recurrence)
    {
        var pattern = recurrence.Obj("pattern");
        if (pattern.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var days = string.Join(',', pattern.Arr("daysOfWeek").Select(d => IcalDays[Array.IndexOf(Days, d.GetString())]));
        var setPos = Array.IndexOf(Index, pattern.Str("index")) is var i and >= 0 ? (i == 4 ? -1 : i + 1) : 1;
        var parts = new List<string>();
        switch (pattern.Str("type"))
        {
            case "daily": parts.Add("FREQ=DAILY"); break;
            case "weekly": parts.Add("FREQ=WEEKLY"); parts.Add("BYDAY=" + days); break;
            case "absoluteMonthly": parts.Add("FREQ=MONTHLY"); parts.Add("BYMONTHDAY=" + pattern.Int("dayOfMonth")); break;
            case "relativeMonthly": parts.Add("FREQ=MONTHLY"); parts.Add("BYDAY=" + days); parts.Add("BYSETPOS=" + setPos); break;
            case "absoluteYearly": parts.Add("FREQ=YEARLY"); parts.Add("BYMONTH=" + pattern.Int("month")); parts.Add("BYMONTHDAY=" + pattern.Int("dayOfMonth")); break;
            case "relativeYearly": parts.Add("FREQ=YEARLY"); parts.Add("BYMONTH=" + pattern.Int("month")); parts.Add("BYDAY=" + days); parts.Add("BYSETPOS=" + setPos); break;
            default: return null;
        }

        if (pattern.Int("interval") > 1)
        {
            parts.Add("INTERVAL=" + pattern.Int("interval"));
        }

        var range = recurrence.Obj("range");
        if (range.Str("type") == "endDate" && DateOnly.TryParse(range.Str("endDate"), CultureInfo.InvariantCulture, out var until))
        {
            parts.Add("UNTIL=" + until.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        }
        else if (range.Str("type") == "numbered")
        {
            parts.Add("COUNT=" + range.Int("numberOfOccurrences"));
        }

        return string.Join(';', parts);
    }

    /// <summary>An RRULE as Graph's patterned recurrence (what Graph can express).</summary>
    internal static object GraphRecurrence(RecurrenceRule rule, DateOnly start)
    {
        var days = rule.ByDay.Select(d => Days[(int)d.DayOfWeek]).ToList();
        var position = rule.BySetPosition.FirstOrDefault() is var p and not 0 ? p : rule.ByDay.FirstOrDefault()?.Offset ?? 0;
        var index = position switch { 1 => "first", 2 => "second", 3 => "third", 4 => "fourth", -1 => "last", _ => (string?)null };
        var dayOfMonth = rule.ByMonthDay.FirstOrDefault() is var md and > 0 ? md : start.Day;
        var month = rule.ByMonth.FirstOrDefault() is var m and > 0 ? m : start.Month;
        object pattern = rule.Frequency switch
        {
            FrequencyType.Daily => new { type = "daily", interval = Math.Max(1, rule.Interval) },
            FrequencyType.Weekly => new { type = "weekly", interval = Math.Max(1, rule.Interval), daysOfWeek = days.Count > 0 ? days : [Days[(int)start.DayOfWeek]], firstDayOfWeek = "monday" },
            FrequencyType.Monthly when index is not null && days.Count > 0 => new { type = "relativeMonthly", interval = Math.Max(1, rule.Interval), daysOfWeek = days, index },
            FrequencyType.Monthly => new { type = "absoluteMonthly", interval = Math.Max(1, rule.Interval), dayOfMonth },
            FrequencyType.Yearly when index is not null && days.Count > 0 => new { type = "relativeYearly", interval = Math.Max(1, rule.Interval), daysOfWeek = days, index, month },
            _ => new { type = "absoluteYearly", interval = Math.Max(1, rule.Interval), dayOfMonth, month },
        };
        object range = rule.Until is { } until
            ? new { type = "endDate", startDate = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), endDate = DateOnly.FromDateTime(until.Value).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) }
            : rule.Count is { } count
                ? new { type = "numbered", startDate = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), numberOfOccurrences = count }
                : new { type = "noEnd", startDate = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) };
        return new { pattern, range };
    }
}
