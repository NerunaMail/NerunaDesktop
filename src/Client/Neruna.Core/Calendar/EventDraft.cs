using Ical.Net;
using ICalendar = Ical.Net.Calendar;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Serialization;

namespace Neruna.Core.Calendar;

public enum RecurrenceKind
{
    None,
    Daily,
    Weekly,
    Monthly,
    Yearly,

    /// <summary>A rule the simple editor cannot express (BYDAY, COUNT, …); kept untouched unless changed.</summary>
    Custom,
}

/// <summary>How an attendee answered an invitation (iCalendar PARTSTAT).</summary>
public enum Participation
{
    NeedsAction,
    Accepted,
    Tentative,
    Declined,
}

/// <summary>A person invited to an event.</summary>
public sealed record EventAttendee(string Email, string? Name = null, Participation Status = Participation.NeedsAction)
{
    public static Participation ParseStatus(string? value) => value?.ToUpperInvariant() switch
    {
        "ACCEPTED" => Participation.Accepted,
        "TENTATIVE" => Participation.Tentative,
        "DECLINED" => Participation.Declined,
        _ => Participation.NeedsAction,
    };

    public static string FormatStatus(Participation status) => status switch
    {
        Participation.Accepted => "ACCEPTED",
        Participation.Tentative => "TENTATIVE",
        Participation.Declined => "DECLINED",
        _ => "NEEDS-ACTION",
    };
}

/// <summary>
/// The editable part of an event. Applied to existing iCalendar data it only touches these fields, so attendees,
/// alarms, exceptions and vendor properties survive a round trip. Recurring events are edited as a whole series.
/// </summary>
/// <param name="Start">Local wall-clock time; for all-day events only the date counts.</param>
/// <param name="End">Exclusive end; for all-day events the day after the last day (iCalendar semantics).</param>
/// <param name="ReminderMinutes">Reminder this many minutes before the start (0 = at the start); null = none.</param>
/// <param name="Attendees">Invited people; null leaves the event's attendees as they are.</param>
/// <param name="Organizer">Address of the organizer; set for new invitations (the own address).</param>
public sealed record EventDraft(
    string Summary,
    string? Location,
    string? Description,
    DateTime Start,
    DateTime End,
    bool IsAllDay,
    RecurrenceKind Recurrence,
    int? ReminderMinutes = null,
    IReadOnlyList<EventAttendee>? Attendees = null,
    string? Organizer = null)
{
    /// <summary>Reminder until the user chooses another default (Einstellungen → Kalender): 15 minutes ahead.</summary>
    public const int DefaultReminderMinutes = 15;

    /// <param name="reminderMinutes">The user's default reminder (null = none).</param>
    public static EventDraft New(DateTime start, int? reminderMinutes = DefaultReminderMinutes) =>
        new(string.Empty, null, null, start, start.AddHours(1), false, RecurrenceKind.None, reminderMinutes, []);

    /// <exception cref="FormatException">The data contains no event.</exception>
    public static EventDraft FromICalendar(string iCalendarData)
    {
        var master = MasterOf(Load(iCalendarData));
        var start = ToLocal(master.DtStart ?? throw new FormatException("Event has no start."));
        var end = master.DtEnd is { } dtEnd
            ? ToLocal(dtEnd)
            : master.Duration is { } duration ? ToLocal(master.DtStart.Add(duration)) : start.AddHours(master.IsAllDay ? 24 : 1);

        return new EventDraft(
            master.Summary ?? string.Empty,
            master.Location,
            master.Description,
            start,
            end,
            master.IsAllDay,
            KindOf(master.RecurrenceRule),
            ReminderOf(master),
            AttendeesOf(master),
            AddressOf(master.Organizer?.Value));
    }

    /// <summary>UID of the (first) event in iCalendar data; null if there is none or it cannot be read.</summary>
    public static string? UidOf(string iCalendarData)
    {
        try
        {
            return Load(iCalendarData).Events.FirstOrDefault()?.Uid;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>The first reminder before (or at) the start, in minutes; null without one.</summary>
    internal static int? ReminderOf(CalendarEvent evt)
    {
        foreach (var alarm in evt.Alarms.Where(IsUserAlarm))
        {
            if (alarm.Trigger?.Duration is { } duration && !string.Equals(alarm.Trigger.Related, "END", StringComparison.OrdinalIgnoreCase))
            {
                return Math.Max(0, (int)Math.Round(-duration.ToTimeSpanUnspecified().TotalMinutes));
            }

            if (alarm.Trigger?.DateTime is { } at && evt.DtStart is { } start)
            {
                return Math.Max(0, (int)Math.Round((start.AsUtc - at.AsUtc).TotalMinutes));
            }
        }

        return null;
    }

    /// <summary>Alarms meant for the user (pop-up or sound); e-mail alarms are the server's business.</summary>
    internal static bool IsUserAlarm(Alarm alarm) =>
        alarm.Action is null || string.Equals(alarm.Action, "DISPLAY", StringComparison.OrdinalIgnoreCase) || string.Equals(alarm.Action, "AUDIO", StringComparison.OrdinalIgnoreCase);

    internal static IReadOnlyList<EventAttendee> AttendeesOf(CalendarEvent evt) =>
        evt.Attendees
            .Select(a => (Attendee: a, Email: AddressOf(a.Value)))
            .Where(a => a.Email is not null)
            .Select(a => new EventAttendee(a.Email!, a.Attendee.CommonName, EventAttendee.ParseStatus(a.Attendee.ParticipationStatus)))
            .ToList();

    /// <summary>"mailto:anna@example.com" → "anna@example.com".</summary>
    public static string? AddressOf(Uri? value)
    {
        if (value is null)
        {
            return null;
        }

        var text = value.OriginalString;
        return text.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ? Uri.UnescapeDataString(text[7..]) : text;
    }

    /// <summary>Returns complete iCalendar data: <paramref name="existingData"/> updated, or a new VCALENDAR.</summary>
    public string ToICalendar(string? existingData)
    {
        ICalendar calendar;
        CalendarEvent master;
        if (existingData is null)
        {
            calendar = new ICalendar { ProductId = "-//Neruna//Neruna Desktop//DE" };
            master = new CalendarEvent { Uid = Guid.NewGuid().ToString("D") + "@neruna" };
            calendar.Events.Add(master);
        }
        else
        {
            calendar = Load(existingData);
            master = MasterOf(calendar);
        }

        master.Summary = Summary;
        master.Location = string.IsNullOrWhiteSpace(Location) ? null : Location;
        master.Description = string.IsNullOrWhiteSpace(Description) ? null : Description;
        // DURATION and DTEND are mutually exclusive; we always write DTEND.
        if (master.Duration is not null)
        {
            master.Duration = null;
        }

        if (IsAllDay)
        {
            var first = DateOnly.FromDateTime(Start);
            var last = DateOnly.FromDateTime(End);
            master.DtStart = new CalDateTime(first);
            master.DtEnd = new CalDateTime(last > first ? last : first.AddDays(1));
        }
        else
        {
            var tzId = LocalTimeZoneId();
            if (calendar.TimeZones.All(tz => tz.TzId != tzId))
            {
                calendar.AddTimeZone(tzId);
            }

            master.DtStart = new CalDateTime(DateTime.SpecifyKind(Start, DateTimeKind.Unspecified), tzId);
            master.DtEnd = new CalDateTime(DateTime.SpecifyKind(End > Start ? End : Start.AddMinutes(30), DateTimeKind.Unspecified), tzId);
        }

        if (Recurrence != KindOf(master.RecurrenceRule))
        {
            master.RecurrenceRule = Recurrence switch
            {
                RecurrenceKind.Daily => new RecurrenceRule(FrequencyType.Daily),
                RecurrenceKind.Weekly => new RecurrenceRule(FrequencyType.Weekly),
                RecurrenceKind.Monthly => new RecurrenceRule(FrequencyType.Monthly),
                RecurrenceKind.Yearly => new RecurrenceRule(FrequencyType.Yearly),
                RecurrenceKind.Custom => master.RecurrenceRule,
                _ => null,
            };
        }

        ApplyReminder(master);
        ApplyAttendees(master);

        // Other clients only pick up changes reliably when SEQUENCE and DTSTAMP move forward.
        if (existingData is not null)
        {
            master.Sequence++;
        }

        master.DtStamp = new CalDateTime(DateTime.UtcNow, "UTC");
        master.LastModified = master.DtStamp;

        return new CalendarSerializer().SerializeToString(calendar)
               ?? throw new InvalidOperationException("Serializing the event failed.");
    }

    // Only when the user changed it: other alarms (several, e-mail alarms of the server) stay untouched otherwise.
    private void ApplyReminder(CalendarEvent master)
    {
        if (ReminderOf(master) == ReminderMinutes)
        {
            return;
        }

        foreach (var alarm in master.Alarms.Where(IsUserAlarm).ToList())
        {
            master.Alarms.Remove(alarm);
        }

        if (ReminderMinutes is { } minutes)
        {
            master.Alarms.Add(new Alarm
            {
                Action = "DISPLAY",
                Description = string.IsNullOrWhiteSpace(Summary) ? "Erinnerung" : Summary,
                Trigger = new Trigger(Duration.FromMinutes(-minutes)),
            });
        }
    }

    // Keeps what other clients stored about known attendees (role, answer, delegation); new ones are asked to reply.
    private void ApplyAttendees(CalendarEvent master)
    {
        if (Attendees is null)
        {
            return;
        }

        var wanted = Attendees.DistinctBy(a => a.Email, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var existing in master.Attendees.ToList())
        {
            if (!wanted.Any(a => string.Equals(a.Email, AddressOf(existing.Value), StringComparison.OrdinalIgnoreCase)))
            {
                master.Attendees.Remove(existing);
            }
        }

        foreach (var attendee in wanted)
        {
            var existing = master.Attendees.FirstOrDefault(a => string.Equals(AddressOf(a.Value), attendee.Email, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                master.Attendees.Add(new Attendee("mailto:" + attendee.Email)
                {
                    CommonName = attendee.Name,
                    Role = "REQ-PARTICIPANT",
                    ParticipationStatus = EventAttendee.FormatStatus(attendee.Status),
                    Rsvp = attendee.Status == Participation.NeedsAction,
                });
            }
            else
            {
                existing.ParticipationStatus = EventAttendee.FormatStatus(attendee.Status);
            }
        }

        if (wanted.Count == 0)
        {
            // No longer a meeting: an organizer without attendees would make other clients treat it as one.
            master.Organizer = null;
        }
        else if (master.Organizer is null && Organizer is { } organizer)
        {
            master.Organizer = new Organizer("mailto:" + organizer);
        }
    }

    /// <summary>IANA id of the local time zone (Windows ids are converted, other clients expect IANA).</summary>
    public static string LocalTimeZoneId()
    {
        var local = TimeZoneInfo.Local;
        if (local.HasIanaId)
        {
            return local.Id;
        }

        return TimeZoneInfo.TryConvertWindowsIdToIanaId(local.Id, out var iana) ? iana : "UTC";
    }

    private static ICalendar Load(string data) =>
        ICalendar.Load(data) ?? throw new FormatException("Empty iCalendar data.");

    // The series master is the event without RECURRENCE-ID; overrides of single occurrences stay untouched.
    private static CalendarEvent MasterOf(ICalendar calendar) =>
        calendar.Events.FirstOrDefault(e => e.RecurrenceIdentifier is null)
        ?? calendar.Events.FirstOrDefault()
        ?? throw new FormatException("The calendar object contains no event.");

    private static DateTime ToLocal(CalDateTime value)
    {
        if (!value.HasTime || value.IsFloating)
        {
            return value.Value;
        }

        return DateTime.SpecifyKind(value.AsUtc, DateTimeKind.Utc).ToLocalTime();
    }

    private static RecurrenceKind KindOf(RecurrenceRule? rule)
    {
        if (rule is null)
        {
            return RecurrenceKind.None;
        }

        var simple = rule.Interval <= 1 && rule.Count is null && rule.Until is null && rule.ByDay.Count == 0
                     && rule.ByMonthDay.Count == 0 && rule.ByMonth.Count == 0 && rule.BySetPosition.Count == 0;
        if (!simple)
        {
            return RecurrenceKind.Custom;
        }

        return rule.Frequency switch
        {
            FrequencyType.Daily => RecurrenceKind.Daily,
            FrequencyType.Weekly => RecurrenceKind.Weekly,
            FrequencyType.Monthly => RecurrenceKind.Monthly,
            FrequencyType.Yearly => RecurrenceKind.Yearly,
            _ => RecurrenceKind.Custom,
        };
    }
}
