using Ical.Net;
using ICalendar = Ical.Net.Calendar;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Serialization;
using static Neruna.Core.Localization.Texts;

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

/// <summary>How the time of an event shows to others (free/busy, the planning view): busy unless said otherwise.</summary>
public enum ShowAs
{
    Busy,
    Free,
    Tentative,
    OutOfOffice,
}

/// <summary>A person invited to an event.</summary>
public sealed record EventAttendee(string Email, string? Name = null, Participation Status = Participation.NeedsAction)
{
    /// <summary>The people in recipient fields ("Anna Muster &lt;anna@…&gt;, lea@…"), each once; half-typed parts are left out.</summary>
    public static IReadOnlyList<EventAttendee> FromRecipients(params string?[] fields)
    {
        var result = new List<EventAttendee>();
        foreach (var field in fields.Where(f => !string.IsNullOrWhiteSpace(f)))
        {
            // The whole field first ("Muster, Anna" <…> has a comma in the name); while it is half typed, part by part.
            var mailboxes = MimeKit.InternetAddressList.TryParse(field, out var list)
                ? list.Mailboxes.ToList()
                : field!.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(p => MimeKit.MailboxAddress.TryParse(p, out var m) ? m : null).OfType<MimeKit.MailboxAddress>().ToList();
            foreach (var mailbox in mailboxes.Where(m => m.Address.Contains('@', StringComparison.Ordinal)))
            {
                if (!result.Any(a => string.Equals(a.Email, mailbox.Address, StringComparison.OrdinalIgnoreCase)))
                {
                    result.Add(new EventAttendee(mailbox.Address, string.IsNullOrWhiteSpace(mailbox.Name) ? null : mailbox.Name));
                }
            }
        }

        return result;
    }

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
/// alarms, exceptions and vendor properties survive a round trip. A series is edited as a whole, or one occurrence of it
/// alone (<see cref="ToOccurrenceICalendar"/>, an override with RECURRENCE-ID).
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

    /// <summary>
    /// Busy, free, tentative or away. In iCalendar TRANSP (free or not) plus X-MICROSOFT-CDO-BUSYSTATUS, which Exchange,
    /// Outlook and many other programs read for tentative and away.
    /// </summary>
    public ShowAs ShowAs { get; init; } = ShowAs.Busy;

    /// <summary>The online meeting (Teams, Zoom, Jitsi …); null: none. Its join text is part of <see cref="Description"/>.</summary>
    public Meetings.OnlineMeeting? Meeting { get; init; }

    /// <summary>Categories (iCalendar CATEGORIES) by name, also those Neruna does not know; null leaves them as they are.</summary>
    public IReadOnlyList<string>? Categories { get; init; }

    /// <summary>The categories of an event, each once (a CATEGORIES line may hold several, separated by commas).</summary>
    public static IReadOnlyList<string> CategoriesOf(CalendarEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        return [.. evt.Categories.SelectMany(c => c.Split(',')).Select(c => c.Trim()).Where(c => c.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The Microsoft property for tentative and away (TRANSP knows only free and busy).</summary>
    public const string BusyStatusProperty = "X-MICROSOFT-CDO-BUSYSTATUS";

    /// <summary>The show-as of an event (see <see cref="ShowAs"/>).</summary>
    public static ShowAs ShowAsOf(CalendarEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        return evt.Properties.Get<string>(BusyStatusProperty)?.Trim().ToUpperInvariant() switch
        {
            "FREE" => ShowAs.Free,
            "TENTATIVE" => ShowAs.Tentative,
            "OOF" => ShowAs.OutOfOffice,
            "BUSY" => ShowAs.Busy,
            _ => string.Equals(evt.Transparency, "TRANSPARENT", StringComparison.OrdinalIgnoreCase) ? ShowAs.Free : ShowAs.Busy,
        };
    }

    /// <summary>Writes the show-as as TRANSP and, where TRANSP is not enough or the property is there already, X-MICROSOFT-CDO-BUSYSTATUS.</summary>
    public static void ApplyShowAs(CalendarEvent evt, ShowAs showAs)
    {
        ArgumentNullException.ThrowIfNull(evt);
        evt.Transparency = showAs == ShowAs.Free ? "TRANSPARENT" : "OPAQUE";
        var status = showAs switch
        {
            ShowAs.Free => "FREE",
            ShowAs.Tentative => "TENTATIVE",
            ShowAs.OutOfOffice => "OOF",
            _ => "BUSY",
        };
        if (showAs is ShowAs.Tentative or ShowAs.OutOfOffice || evt.Properties.Get<string>(BusyStatusProperty) is not null)
        {
            evt.Properties.Set(BusyStatusProperty, status);
        }
    }

    /// <param name="reminderMinutes">The user's default reminder (null = none).</param>
    public static EventDraft New(DateTime start, int? reminderMinutes = DefaultReminderMinutes) =>
        new(string.Empty, null, null, start, start.AddHours(1), false, RecurrenceKind.None, reminderMinutes, []);

    /// <exception cref="FormatException">The data contains no event.</exception>
    public static EventDraft FromICalendar(string iCalendarData) => FromEvent(MasterOf(Load(iCalendarData)));

    /// <summary>
    /// One occurrence of a series as a single event: its override if it has one, else the series at that time.
    /// </summary>
    /// <param name="originalStart">The occurrence's start in the series (<see cref="CalendarOccurrence.RecurrenceId"/>).</param>
    /// <exception cref="FormatException">The data contains no event.</exception>
    public static EventDraft FromOccurrence(string iCalendarData, DateTimeOffset originalStart)
    {
        var calendar = Load(iCalendarData);
        if (OverrideOf(calendar, originalStart) is { } own)
        {
            return FromEvent(own) with { Recurrence = RecurrenceKind.None };
        }

        var series = FromEvent(MasterOf(calendar));
        var start = series.IsAllDay ? originalStart.Date : originalStart.LocalDateTime;
        return series with { Start = start, End = start + (series.End - series.Start), Recurrence = RecurrenceKind.None };
    }

    private static EventDraft FromEvent(CalendarEvent master)
    {
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
            AddressOf(master.Organizer?.Value))
        {
            ShowAs = ShowAsOf(master),
            Meeting = Meetings.OnlineMeeting.Of(master),
            Categories = CategoriesOf(master),
        };
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

        Write(calendar, master, existingData is not null, series: true);
        return Serialize(calendar);
    }

    /// <summary>
    /// The series in <paramref name="existingData"/> with only the occurrence at <paramref name="originalStart"/> changed
    /// to this draft: its override (an event with the series' UID and RECURRENCE-ID) is created or updated. The series
    /// itself and its other occurrences stay as they are; <see cref="Recurrence"/> is ignored.
    /// </summary>
    /// <exception cref="FormatException">The data contains no event.</exception>
    public string ToOccurrenceICalendar(string existingData, DateTimeOffset originalStart)
    {
        var calendar = Load(existingData);
        var own = OverrideOf(calendar, originalStart);
        if (own is null)
        {
            var master = MasterOf(calendar);
            own = master.Copy<CalendarEvent>() ?? throw new FormatException("The event cannot be copied.");
            own.RecurrenceRule = null;
            own.RecurrenceDates.Clear();
            own.ExceptionDates.Clear();
            own.RecurrenceIdentifier = new RecurrenceIdentifier(SeriesTime(master, originalStart), null);
            calendar.Events.Add(own);
        }

        Write(calendar, own, existing: true, series: false);
        return Serialize(calendar);
    }

    /// <summary>
    /// The series without the occurrence at <paramref name="originalStart"/>: an EXDATE for it, its override removed.
    /// </summary>
    /// <exception cref="FormatException">The data contains no event.</exception>
    public static string WithoutOccurrence(string existingData, DateTimeOffset originalStart)
    {
        var calendar = Load(existingData);
        var master = MasterOf(calendar);
        if (OverrideOf(calendar, originalStart) is { } own)
        {
            // By reference: Ical.Net's Remove compares events by value and may take the series with the same UID.
            var keep = calendar.Events.Where(e => !ReferenceEquals(e, own)).ToList();
            calendar.Events.Clear();
            foreach (var evt in keep)
            {
                calendar.Events.Add(evt);
            }
        }

        master.ExceptionDates.Add(SeriesTime(master, originalStart));
        master.Sequence++;
        master.DtStamp = new CalDateTime(DateTime.UtcNow, "UTC");
        master.LastModified = master.DtStamp;
        return Serialize(calendar);
    }

    /// <summary>
    /// The occurrence at <paramref name="originalStart"/> as a VCALENDAR of its own (without RECURRENCE-ID), for servers
    /// that keep occurrences as items of their own; null when it has no override.
    /// </summary>
    public static string? OccurrenceAlone(string iCalendarData, DateTimeOffset originalStart)
    {
        var calendar = Load(iCalendarData);
        if (OverrideOf(calendar, originalStart) is not { } own)
        {
            return null;
        }

        var alone = new ICalendar { ProductId = calendar.ProductId };
        foreach (var zone in calendar.TimeZones)
        {
            alone.TimeZones.Add(zone);
        }

        var copy = own.Copy<CalendarEvent>() ?? throw new FormatException("The event cannot be copied.");
        copy.RecurrenceIdentifier = null;
        alone.Events.Add(copy);
        return Serialize(alone);
    }

    /// <summary>An X-property a provider may put on an override: the server's own id of that occurrence.</summary>
    public const string OccurrenceIdProperty = "X-NERUNA-OCCURRENCE-ID";

    /// <summary>The server's id of the occurrence at <paramref name="originalStart"/> (see <see cref="OccurrenceIdProperty"/>).</summary>
    public static string? OccurrenceIdOf(string iCalendarData, DateTimeOffset originalStart) =>
        OverrideOf(Load(iCalendarData), originalStart)?.Properties.Get<string>(OccurrenceIdProperty);

    /// <summary>The override of the occurrence that starts at <paramref name="originalStart"/> in the series, if any.</summary>
    private static CalendarEvent? OverrideOf(ICalendar calendar, DateTimeOffset originalStart) =>
        calendar.Events.FirstOrDefault(e => e.RecurrenceIdentifier is { } id && SameStart(id.StartTime, originalStart));

    private static bool SameStart(CalDateTime value, DateTimeOffset start) =>
        value.HasTime ? CalendarController.ToDateTimeOffset(value) == start : DateOnly.FromDateTime(value.Value) == DateOnly.FromDateTime(start.Date);

    // The occurrence's start as the series writes it (same time zone or date form): what RECURRENCE-ID and EXDATE need.
    private static CalDateTime SeriesTime(CalendarEvent master, DateTimeOffset originalStart)
    {
        var from = new CalDateTime(originalStart.UtcDateTime.AddDays(-1), "UTC");
        var until = originalStart.AddDays(1);
        foreach (var occurrence in master.GetOccurrences(from))
        {
            var start = CalendarController.ToDateTimeOffset(occurrence.Period.StartTime);
            if (start > until)
            {
                break;
            }

            if (SameStart(occurrence.Period.StartTime, originalStart))
            {
                return occurrence.Period.StartTime;
            }
        }

        // Not in the rule (anymore): written like the series' start.
        var first = master.DtStart ?? throw new FormatException("Event has no start.");
        if (!first.HasTime)
        {
            return new CalDateTime(DateOnly.FromDateTime(originalStart.Date));
        }

        return first.TzId is { } tzId && TimeZoneInfo.TryFindSystemTimeZoneById(tzId, out var zone)
            ? new CalDateTime(DateTime.SpecifyKind(TimeZoneInfo.ConvertTime(originalStart, zone).DateTime, DateTimeKind.Unspecified), tzId)
            : new CalDateTime(originalStart.UtcDateTime, "UTC");
    }

    private static string Serialize(ICalendar calendar) =>
        new CalendarSerializer().SerializeToString(calendar) ?? throw new InvalidOperationException("Serializing the event failed.");

    // Writes the draft's fields into one event of the calendar (a series master, single event or override).
    private void Write(ICalendar calendar, CalendarEvent master, bool existing, bool series)
    {
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

        if (series && Recurrence != KindOf(master.RecurrenceRule))
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
        ApplyShowAs(master, ShowAs);
        Meetings.OnlineMeeting.Apply(master, Meeting);
        if (Categories is not null && !CategoriesOf(master).SequenceEqual(Categories))
        {
            master.Categories = [.. Categories];
        }

        // Other clients only pick up changes reliably when SEQUENCE and DTSTAMP move forward.
        if (existing)
        {
            master.Sequence++;
        }

        master.DtStamp = new CalDateTime(DateTime.UtcNow, "UTC");
        master.LastModified = master.DtStamp;
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
                Description = string.IsNullOrWhiteSpace(Summary) ? T("Erinnerung") : Summary,
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
        ICalendarLoader.Load(data) ?? throw new FormatException("Empty iCalendar data.");

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
