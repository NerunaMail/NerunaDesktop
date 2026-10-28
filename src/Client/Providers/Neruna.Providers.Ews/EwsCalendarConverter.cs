using System.Globalization;
using System.Xml.Linq;
using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Serialization;
using Neruna.Core.Calendar;
using ICalendar = Ical.Net.Calendar;

namespace Neruna.Providers.Ews;

/// <summary>
/// Exchange calendar items ↔ iCalendar. Read: the item with its series (pattern, removed and changed occurrences, the
/// latter as overrides with RECURRENCE-ID), attendees and the own answer. Written: what <see cref="EventDraft"/> edits.
/// Times are UTC on the wire; the item's time zone (Windows id) decides the wall-clock time and the all-day date.
/// </summary>
internal static class EwsCalendarConverter
{
    /// <summary>What Neruna reads of a calendar item.</summary>
    public static readonly object[] Fields =
    [
        "item:Subject", "item:Body", "item:Categories", "item:Sensitivity", "item:ReminderIsSet", "item:ReminderMinutesBeforeStart",
        "calendar:UID", "calendar:Start", "calendar:End", "calendar:IsAllDayEvent", "calendar:LegacyFreeBusyStatus", "calendar:Location",
        "calendar:IsMeeting", "calendar:IsCancelled", "calendar:MyResponseType", "calendar:Organizer", "calendar:RequiredAttendees",
        "calendar:OptionalAttendees", "calendar:Recurrence", "calendar:ModifiedOccurrences", "calendar:DeletedOccurrences", "calendar:StartTimeZone",
    ];

    private static readonly string[] Days = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];
    private static readonly string[] IcalDays = ["SU", "MO", "TU", "WE", "TH", "FR", "SA"];
    private static readonly string[] Indexes = ["First", "Second", "Third", "Fourth", "Last"];
    private static readonly string[] Months = ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];

    /// <summary>A calendar item (and its changed occurrences) as iCalendar.</summary>
    /// <param name="mailbox">The own address: its attendee gets the own answer (MyResponseType).</param>
    public static string ToICalendar(XElement item, IReadOnlyList<XElement> occurrences, string? mailbox)
    {
        ArgumentNullException.ThrowIfNull(item);
        var zone = ZoneOf(item);
        var calendar = new ICalendar { ProductId = "-//Neruna//Neruna Desktop//DE" };
        var master = EventOf(item, zone, mailbox, calendar);
        var allDay = item.Bool("IsAllDayEvent");
        if (RecurrenceOf(item.Get("Recurrence"), zone, allDay) is { } rule)
        {
            master.RecurrenceRule = new RecurrenceRule(rule);
            foreach (var deleted in item.Get("DeletedOccurrences")?.Elements(X.T + "DeletedOccurrence") ?? [])
            {
                if (deleted.Date("Start") is { } start)
                {
                    master.ExceptionDates.Add(When(start, zone, allDay));
                }
            }
        }

        calendar.Events.Add(master);
        foreach (var occurrence in master.RecurrenceRule is null ? [] : occurrences)
        {
            var changed = EventOf(occurrence, ZoneOf(occurrence) is var z && z != "UTC" ? z : zone, mailbox, calendar);
            changed.Uid = master.Uid;
            if (occurrence.Date("OriginalStart") is { } original)
            {
                changed.RecurrenceIdentifier = new RecurrenceIdentifier(When(original, zone, allDay), null);
                calendar.Events.Add(changed);
            }
        }

        return new CalendarSerializer().SerializeToString(calendar) ?? throw new InvalidOperationException("Serializing the event failed.");
    }

    private static CalendarEvent EventOf(XElement item, string zone, string? mailbox, ICalendar calendar)
    {
        var evt = new CalendarEvent
        {
            Uid = item.Val("UID") ?? item.Id(),
            Summary = item.Val("Subject") ?? string.Empty,
            Location = item.Val("Location") is { Length: > 0 } location ? location : null,
            Description = item.Val("Body") is { Length: > 0 } text ? text.Trim() : null,
            Categories = [.. EwsMailProvider.CategoriesOf(item)],
        };
        var allDay = item.Bool("IsAllDayEvent");
        var start = item.Date("Start") ?? DateTimeOffset.UnixEpoch;
        var end = item.Date("End") ?? start.AddHours(1);
        if (allDay)
        {
            var first = DateOnly.FromDateTime(Local(start, zone));
            var last = DateOnly.FromDateTime(Local(end, zone));
            evt.DtStart = new CalDateTime(first);
            evt.DtEnd = new CalDateTime(last > first ? last : first.AddDays(1));
        }
        else
        {
            if (calendar.TimeZones.All(tz => tz.TzId != zone))
            {
                calendar.AddTimeZone(zone);
            }

            evt.DtStart = new CalDateTime(Local(start, zone), zone);
            evt.DtEnd = new CalDateTime(Local(end, zone), zone);
        }

        // Busy, free, tentative, away – kept through an edit in Neruna.
        EventDraft.ApplyShowAs(evt, item.Val("LegacyFreeBusyStatus") switch
        {
            "Free" => ShowAs.Free,
            "Tentative" => ShowAs.Tentative,
            "OOF" => ShowAs.OutOfOffice,
            _ => ShowAs.Busy,
        });

        if (item.Val("Sensitivity") is "Private" or "Confidential")
        {
            evt.Class = item.Val("Sensitivity")!.ToUpperInvariant();
        }

        var attendees = Attendees(item, "RequiredAttendees", "REQ-PARTICIPANT").Concat(Attendees(item, "OptionalAttendees", "OPT-PARTICIPANT")).ToList();
        var organizer = item.Get("Organizer")?.Get("Mailbox");
        if (attendees.Count > 0 && EwsMailProvider.AddressOf(organizer) is { } from)
        {
            evt.Organizer = new Organizer("mailto:" + from.Address) { CommonName = from.Name };
        }

        foreach (var attendee in attendees)
        {
            // The own copy knows the own answer better than the attendee list does.
            if (mailbox is not null && string.Equals(EventDraft.AddressOf(attendee.Value), mailbox, StringComparison.OrdinalIgnoreCase) && item.Val("MyResponseType") is { } mine)
            {
                attendee.ParticipationStatus = Status(mine);
            }

            evt.Attendees.Add(attendee);
        }

        if (item.Bool("ReminderIsSet"))
        {
            evt.Alarms.Add(new Alarm { Action = "DISPLAY", Description = evt.Summary, Trigger = new Trigger(Duration.FromMinutes(-item.Int("ReminderMinutesBeforeStart"))) });
        }

        return evt;
    }

    private static IEnumerable<Attendee> Attendees(XElement item, string list, string role) =>
        (item.Get(list)?.Elements(X.T + "Attendee") ?? [])
            .Select(a => (Address: EwsMailProvider.AddressOf(a.Get("Mailbox")), Response: a.Val("ResponseType")))
            .Where(a => a.Address is not null)
            .Select(a => new Attendee("mailto:" + a.Address!.Address) { CommonName = a.Address.Name, Role = role, ParticipationStatus = Status(a.Response) });

    private static string Status(string? response) => response switch
    {
        "Accept" or "Organizer" => "ACCEPTED",
        "Tentative" => "TENTATIVE",
        "Decline" => "DECLINED",
        _ => "NEEDS-ACTION",
    };

    /// <summary>The own answer as EWS response type (for comparing with the item's MyResponseType).</summary>
    public static string ResponseOf(Participation status) => status switch
    {
        Participation.Accepted => "Accept",
        Participation.Tentative => "Tentative",
        Participation.Declined => "Decline",
        _ => "NoResponseReceived",
    };

    /// <summary>The IANA zone of an item (its start time zone), else the local one.</summary>
    internal static string ZoneOf(XElement item)
    {
        var windows = item.Get("StartTimeZone")?.Attribute("Id")?.Value;
        if (windows is not null && TimeZoneInfo.TryConvertWindowsIdToIanaId(windows, out var iana))
        {
            return iana;
        }

        return windows is not null && TimeZoneInfo.TryFindSystemTimeZoneById(windows, out var found) && found.HasIanaId ? found.Id : EventDraft.LocalTimeZoneId();
    }

    private static DateTime Local(DateTimeOffset utc, string zone) =>
        DateTime.SpecifyKind(TimeZoneInfo.TryFindSystemTimeZoneById(zone, out var tz) ? TimeZoneInfo.ConvertTime(utc, tz).DateTime : utc.UtcDateTime, DateTimeKind.Unspecified);

    private static CalDateTime When(DateTimeOffset utc, string zone, bool allDay) =>
        allDay ? new CalDateTime(DateOnly.FromDateTime(Local(utc, zone))) : new CalDateTime(Local(utc, zone), zone);

    /// <summary>EWS's recurrence as an RRULE value; null when there is none (or one Neruna cannot express).</summary>
    internal static string? RecurrenceOf(XElement? recurrence, string zone, bool allDay)
    {
        var pattern = recurrence?.Elements().FirstOrDefault(e => e.Name.LocalName.EndsWith("Recurrence", StringComparison.Ordinal) && e.Name.LocalName is not ("NoEndRecurrence" or "EndDateRecurrence" or "NumberedRecurrence"));
        if (pattern is null)
        {
            return null;
        }

        var parts = new List<string>();
        string ByDay() => DaysOf(pattern.Val("DaysOfWeek"));
        string SetPos() => "BYSETPOS=" + (Array.IndexOf(Indexes, pattern.Val("DayOfWeekIndex")) is var i and >= 0 ? (i == 4 ? -1 : i + 1) : 1);
        string Month() => "BYMONTH=" + (Array.IndexOf(Months, pattern.Val("Month")) + 1);
        switch (pattern.Name.LocalName)
        {
            case "DailyRecurrence": parts.Add("FREQ=DAILY"); break;
            case "WeeklyRecurrence": parts.Add("FREQ=WEEKLY"); parts.Add("BYDAY=" + ByDay()); break;
            case "AbsoluteMonthlyRecurrence": parts.Add("FREQ=MONTHLY"); parts.Add("BYMONTHDAY=" + pattern.Int("DayOfMonth")); break;
            case "RelativeMonthlyRecurrence": parts.Add("FREQ=MONTHLY"); parts.Add("BYDAY=" + ByDay()); parts.Add(SetPos()); break;
            case "AbsoluteYearlyRecurrence": parts.Add("FREQ=YEARLY"); parts.Add(Month()); parts.Add("BYMONTHDAY=" + pattern.Int("DayOfMonth")); break;
            case "RelativeYearlyRecurrence": parts.Add("FREQ=YEARLY"); parts.Add(Month()); parts.Add("BYDAY=" + ByDay()); parts.Add(SetPos()); break;
            default: return null; // regenerating patterns (tasks)
        }

        if (pattern.Int("Interval") > 1)
        {
            parts.Add("INTERVAL=" + pattern.Int("Interval"));
        }

        if (pattern.Val("FirstDayOfWeek") is { } first && Array.IndexOf(Days, first) is var f and >= 0)
        {
            parts.Add("WKST=" + IcalDays[f]);
        }

        if (recurrence!.Get("EndDateRecurrence") is { } endDate && DateOf(endDate.Val("EndDate")) is { } until)
        {
            // Up to and including that day: for timed series the end of the day in the item's zone, in UTC.
            parts.Add("UNTIL=" + (allDay
                ? until.ToString("yyyyMMdd", CultureInfo.InvariantCulture)
                : EndOfDayUtc(until, zone).ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)));
        }
        else if (recurrence.Get("NumberedRecurrence") is { } numbered)
        {
            parts.Add("COUNT=" + numbered.Int("NumberOfOccurrences"));
        }

        return string.Join(';', parts);
    }

    private static DateTime EndOfDayUtc(DateOnly day, string zone)
    {
        var local = day.ToDateTime(new TimeOnly(23, 59, 59));
        return TimeZoneInfo.TryFindSystemTimeZoneById(zone, out var tz) ? TimeZoneInfo.ConvertTimeToUtc(local, tz) : local;
    }

    // "Monday Wednesday" → "MO,WE"; the groups Day/Weekday/WeekendDay as their days.
    private static string DaysOf(string? days) => string.Join(',', (days ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries).SelectMany(d => d switch
    {
        "Day" => IcalDays,
        "Weekday" => IcalDays[1..6],
        "WeekendDay" => ["SA", "SU"],
        _ => Array.IndexOf(Days, d) is var i and >= 0 ? [IcalDays[i]] : [],
    }));

    // EWS dates may carry an offset ("2026-10-05+02:00"): the date part counts.
    private static DateOnly? DateOf(string? value) =>
        value is { Length: >= 10 } && DateOnly.TryParseExact(value[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

    /// <summary>The answer to GetUserAvailability: one entry per address asked, in that order (times in UTC, as asked).</summary>
    internal static IReadOnlyList<Availability> AvailabilityOf(IReadOnlyList<string> emails, XElement body)
    {
        var responses = body.Descendants(X.M + "FreeBusyResponse").ToList();
        var result = new List<Availability>();
        for (var i = 0; i < emails.Count; i++)
        {
            var response = i < responses.Count ? responses[i] : null;
            var message = response?.Element(X.M + "ResponseMessage");
            if (response is null || message?.Attribute("ResponseClass")?.Value == "Error")
            {
                result.Add(new Availability(emails[i], [], message?.Element(X.M + "MessageText")?.Value ?? "not answered"));
                continue;
            }

            static DateTimeOffset Utc(string? value) => new(DateTime.SpecifyKind(
                DateTime.Parse(value ?? "1970-01-01T00:00:00", CultureInfo.InvariantCulture, DateTimeStyles.None), DateTimeKind.Utc));
            var slots = response.Descendants(X.T + "CalendarEvent")
                .Select(e => (Event: e, Type: e.Element(X.T + "BusyType")?.Value))
                .Where(e => e.Type is "Busy" or "Tentative" or "OOF" or "WorkingElsewhere")
                .Select(e => new BusySlot(Utc(e.Event.Element(X.T + "StartTime")?.Value), Utc(e.Event.Element(X.T + "EndTime")?.Value), e.Type switch
                {
                    "Tentative" => ShowAs.Tentative,
                    "OOF" => ShowAs.OutOfOffice,
                    _ => ShowAs.Busy,
                },
                e.Event.Element(X.T + "CalendarEventDetails")?.Element(X.T + "Subject")?.Value is { Length: > 0 } subject ? subject : null,
                e.Event.Element(X.T + "CalendarEventDetails")?.Element(X.T + "Location")?.Value is { Length: > 0 } location ? location : null))
                .ToList();
            result.Add(new Availability(emails[i], slots));
        }

        return result;
    }

    // ── writing ──

    /// <summary>What Neruna writes of an event, field by field (field URI → element), in schema order.</summary>
    public static List<(string Field, XElement? Value)> FieldsOf(string iCalendarData, out IReadOnlyList<(string Email, string? Name, bool Optional)> attendees)
    {
        var draft = EventDraft.FromICalendar(iCalendarData);
        var master = ICalendar.Load(iCalendarData)?.Events.FirstOrDefault(e => e.RecurrenceIdentifier is null);
        var zoneId = EventDraft.LocalTimeZoneId();
        var zone = TimeZoneInfo.TryFindSystemTimeZoneById(zoneId, out var tz) ? tz : TimeZoneInfo.Local;
        var windows = TimeZoneInfo.TryConvertIanaIdToWindowsId(zoneId, out var w) ? w : zone.HasIanaId ? null : zone.Id;
        var start = draft.IsAllDay ? draft.Start.Date : draft.Start;
        var end = draft.IsAllDay ? (draft.End.Date > draft.Start.Date ? draft.End.Date : draft.Start.Date.AddDays(1)) : draft.End > draft.Start ? draft.End : draft.Start.AddMinutes(30);
        string Utc(DateTime local) => TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

        attendees = [.. (draft.Attendees ?? []).Select(a => (a.Email, a.Name, master?.Attendees.FirstOrDefault(m =>
            string.Equals(EventDraft.AddressOf(m.Value), a.Email, StringComparison.OrdinalIgnoreCase))?.Role == "OPT-PARTICIPANT"))];
        static XElement Mailboxes(string name, IEnumerable<(string Email, string? Name, bool Optional)> list) =>
            X.Typ(name, list.Select(a => X.Typ("Attendee", X.Typ("Mailbox", X.Typ("Name", a.Name ?? string.Empty), X.Typ("EmailAddress", a.Email)))));
        var required = attendees.Where(a => !a.Optional).ToList();
        var optional = attendees.Where(a => a.Optional).ToList();

        return
        [
            ("item:Subject", X.Typ("Subject", draft.Summary)),
            ("item:Body", X.Typ("Body", new XAttribute("BodyType", "Text"), draft.Description ?? string.Empty)),
            // In the schema's order: after the body, before the reminder.
            ("item:Categories", draft.Categories is { Count: > 0 } categories ? X.Typ("Categories", categories.Select(c => X.Typ("String", c))) : null),
            ("item:ReminderIsSet", X.Typ("ReminderIsSet", draft.ReminderMinutes is null ? "false" : "true")),
            ("item:ReminderMinutesBeforeStart", draft.ReminderMinutes is { } minutes ? X.Typ("ReminderMinutesBeforeStart", minutes) : null),
            ("calendar:Start", X.Typ("Start", Utc(start))),
            ("calendar:End", X.Typ("End", Utc(end))),
            ("calendar:IsAllDayEvent", X.Typ("IsAllDayEvent", draft.IsAllDay ? "true" : "false")),
            ("calendar:LegacyFreeBusyStatus", X.Typ("LegacyFreeBusyStatus", draft.ShowAs switch
            {
                ShowAs.Free => "Free",
                ShowAs.Tentative => "Tentative",
                ShowAs.OutOfOffice => "OOF",
                _ => "Busy",
            })),
            ("calendar:Location", string.IsNullOrWhiteSpace(draft.Location) ? null : X.Typ("Location", draft.Location)),
            ("calendar:RequiredAttendees", required.Count > 0 ? Mailboxes("RequiredAttendees", required) : null),
            ("calendar:OptionalAttendees", optional.Count > 0 ? Mailboxes("OptionalAttendees", optional) : null),
            ("calendar:Recurrence", master?.RecurrenceRule is { } rule ? RecurrenceElement(rule, DateOnly.FromDateTime(start), zone) : null),
            ("calendar:StartTimeZone", windows is null ? null : X.Typ("StartTimeZone", new XAttribute("Id", windows))),
            ("calendar:EndTimeZone", windows is null ? null : X.Typ("EndTimeZone", new XAttribute("Id", windows))),
        ];
    }

    /// <summary>An RRULE as EWS recurrence (what EWS can express).</summary>
    internal static XElement RecurrenceElement(RecurrenceRule rule, DateOnly start, TimeZoneInfo zone)
    {
        var days = string.Join(' ', rule.ByDay.Select(d => Days[(int)d.DayOfWeek]));
        var position = rule.BySetPosition.FirstOrDefault() is var p and not 0 ? p : rule.ByDay.FirstOrDefault()?.Offset ?? 0;
        var index = position switch { 1 => "First", 2 => "Second", 3 => "Third", 4 => "Fourth", -1 => "Last", _ => null };
        var dayOfMonth = rule.ByMonthDay.FirstOrDefault() is var md and > 0 ? md : start.Day;
        var month = Months[(rule.ByMonth.FirstOrDefault() is var m and > 0 ? m : start.Month) - 1];
        var interval = X.Typ("Interval", Math.Max(1, rule.Interval));
        var singleDay = rule.ByDay.Count == 1 ? days : Days[(int)start.DayOfWeek];
        var pattern = rule.Frequency switch
        {
            FrequencyType.Daily => X.Typ("DailyRecurrence", interval),
            FrequencyType.Weekly => X.Typ("WeeklyRecurrence", interval, X.Typ("DaysOfWeek", days.Length > 0 ? days : Days[(int)start.DayOfWeek]), X.Typ("FirstDayOfWeek", "Monday")),
            FrequencyType.Monthly when index is not null => X.Typ("RelativeMonthlyRecurrence", interval, X.Typ("DaysOfWeek", singleDay), X.Typ("DayOfWeekIndex", index)),
            FrequencyType.Monthly => X.Typ("AbsoluteMonthlyRecurrence", interval, X.Typ("DayOfMonth", dayOfMonth)),
            FrequencyType.Yearly when index is not null => X.Typ("RelativeYearlyRecurrence", X.Typ("DaysOfWeek", singleDay), X.Typ("DayOfWeekIndex", index), X.Typ("Month", month)),
            _ => X.Typ("AbsoluteYearlyRecurrence", X.Typ("DayOfMonth", dayOfMonth), X.Typ("Month", month)),
        };
        var from = X.Typ("StartDate", start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        var range = rule.Until is { } until
            ? X.Typ("EndDateRecurrence", from, X.Typ("EndDate", DateOnly.FromDateTime(until.IsUtc ? TimeZoneInfo.ConvertTimeFromUtc(until.AsUtc, zone) : until.Value)
                .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)))
            : rule.Count is { } count
                ? X.Typ("NumberedRecurrence", from, X.Typ("NumberOfOccurrences", count))
                : X.Typ("NoEndRecurrence", from);
        return X.Typ("Recurrence", pattern, range);
    }
}
