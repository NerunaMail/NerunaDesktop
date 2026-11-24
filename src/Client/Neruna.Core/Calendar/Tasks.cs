using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Serialization;
using ICalendar = Ical.Net.Calendar;

namespace Neruna.Core.Calendar;

/// <summary>What a calendar collection holds: events, tasks (VTODO) or both (many CalDAV servers mix them).</summary>
[Flags]
public enum CalendarContent
{
    None = 0,
    Events = 1,
    Tasks = 2,
}

/// <summary>Task priority as people think of it; iCalendar uses 1 (highest) to 9 (lowest), 0 = none.</summary>
public enum TaskPriority
{
    None,
    Low,
    Normal,
    High,
}

/// <summary>Where a task stands besides done (iCalendar STATUS).</summary>
public enum TaskState
{
    Open,
    InProgress,
    Cancelled,
}

/// <summary>
/// The fields of a task beyond the basic ones: start, reminder, repetition, place, privacy, link, link attachments,
/// categories and state. In a <see cref="TaskDraft"/>, null <see cref="TaskDraft.Details"/> leaves all of them as they are.
/// </summary>
public sealed record TaskDetails
{
    /// <summary>The day work starts (DTSTART); date only.</summary>
    public DateTime? Start { get; init; }

    /// <summary>When to remind (a VALARM at a fixed time); null: no reminder.</summary>
    public DateTime? ReminderAt { get; init; }

    public RecurrenceKind Recurrence { get; init; }

    public RecurrenceSpec? Repeat { get; init; }

    public DateTime? RecurrenceUntil { get; init; }

    public string? Location { get; init; }

    public EventPrivacy Privacy { get; init; }

    /// <summary>The task's web address (iCalendar URL).</summary>
    public string? Url { get; init; }

    /// <summary>Attachments given as web addresses (ATTACH with a URI); embedded files stay untouched.</summary>
    public IReadOnlyList<Uri> Links { get; init; } = [];

    public IReadOnlyList<string> Categories { get; init; } = [];

    public TaskState State { get; init; }

    /// <summary>The day a recurrence counts from: the start, else the due day, else today.</summary>
    public static DateTime SeriesStart(DateTime? start, DateTime? due) => (start ?? due ?? DateTime.Today).Date;

    public static TaskDetails Of(Todo todo)
    {
        ArgumentNullException.ThrowIfNull(todo);
        var start = todo.DtStart is { } s ? ToLocalDate(s) : (DateTime?)null;
        var due = todo.Due is { } d ? ToLocalDate(d) : (DateTime?)null;
        var seriesStart = SeriesStart(start, due);
        var rule = todo.RecurrenceRule;
        return new TaskDetails
        {
            Start = start,
            ReminderAt = ReminderOf(todo, start, due),
            Recurrence = rule is null ? RecurrenceKind.None : RecurrenceSpec.Of(rule, seriesStart)?.Kind ?? RecurrenceKind.Custom,
            Repeat = RecurrenceSpec.Of(rule, seriesStart),
            RecurrenceUntil = RecurrenceSpec.UntilOf(rule),
            Location = string.IsNullOrWhiteSpace(todo.Location) ? null : todo.Location.Trim(),
            Privacy = EventDraft.PrivacyOf(todo.Class),
            Url = todo.Url is { IsAbsoluteUri: true } url ? url.OriginalString : null,
            Links = [.. todo.Attachments.Where(a => a.Uri is { IsAbsoluteUri: true }).Select(a => a.Uri!).Distinct()],
            Categories = [.. todo.Categories.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)],
            State = todo.Status?.ToUpperInvariant() switch
            {
                "IN-PROCESS" => TaskState.InProgress,
                "CANCELLED" => TaskState.Cancelled,
                _ => TaskState.Open,
            },
        };
    }

    /// <summary>The first reminder meant for the user, as a local time (absolute, or relative to start or due).</summary>
    private static DateTime? ReminderOf(Todo todo, DateTime? start, DateTime? due)
    {
        foreach (var alarm in todo.Alarms.Where(a => a.Action is null or "DISPLAY" or "AUDIO"))
        {
            if (alarm.Trigger is not { } trigger)
            {
                continue;
            }

            if (trigger.DateTime is { } at)
            {
                return at.HasTime && !at.IsFloating ? at.AsUtc.ToLocalTime() : at.Value;
            }

            if (trigger.Duration is { } offset)
            {
                var anchor = string.Equals(trigger.Related?.ToString(), "END", StringComparison.OrdinalIgnoreCase)
                    ? todo.Due is { } d ? ToLocalTime(d) : (DateTime?)null
                    : todo.DtStart is { } s ? ToLocalTime(s) : todo.Due is { } d2 ? ToLocalTime(d2) : null;
                if (anchor is { } from)
                {
                    return from + offset.ToTimeSpanUnspecified();
                }
            }
        }

        return null;
    }

    internal static DateTime ToLocalTime(CalDateTime value) =>
        !value.HasTime || value.IsFloating ? value.Value : value.AsUtc.ToLocalTime();

    internal static DateTime ToLocalDate(CalDateTime value) => ToLocalTime(value).Date;
}

/// <summary>One task (VTODO) as shown in the task list.</summary>
/// <param name="Due">Due date; with <paramref name="DueHasTime"/> false only the date counts.</param>
public sealed record TaskItem(
    CalendarInfo List,
    string ObjectRemoteId,
    string Uid,
    string Summary,
    string? Notes,
    DateTimeOffset? Due,
    bool DueHasTime,
    bool IsCompleted,
    DateTimeOffset? CompletedAt,
    TaskPriority Priority,
    bool IsRecurring)
{
    /// <summary>Start, reminder, repetition, place …; empty for tasks read without them.</summary>
    public TaskDetails Details { get; init; } = new();

    /// <summary>Not done and due before today (date-only tasks) or before now (tasks with a time).</summary>
    public bool IsOverdue(DateTimeOffset now) =>
        !IsCompleted && Due is { } due && (DueHasTime ? due < now : due.Date < now.Date);
}

/// <summary>
/// The editable part of a task. <see cref="ToICalendar"/> changes only these properties of an existing VTODO, so
/// categories, alarms, relations and vendor extensions of other programs stay.
/// </summary>
public sealed record TaskDraft(string Summary, string? Notes, DateTime? Due, bool DueHasTime, TaskPriority Priority, bool IsCompleted)
{
    /// <summary>The further fields to write; null leaves them as they are (e.g. when only ticking a task done).</summary>
    public TaskDetails? Details { get; init; }

    public static TaskDraft New(string summary) => new(summary, null, null, false, TaskPriority.None, false);

    public static TaskDraft FromItem(TaskItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return new TaskDraft(item.Summary, item.Notes, item.Due?.LocalDateTime, item.DueHasTime, item.Priority, item.IsCompleted) { Details = item.Details };
    }

    /// <summary>Reads the first VTODO of iCalendar data; null if there is none (an event).</summary>
    public static Todo? TodoOf(string iCalendarData)
    {
        ArgumentNullException.ThrowIfNull(iCalendarData);
        if (!iCalendarData.Contains("BEGIN:VTODO", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var calendar = ICalendar.Load(iCalendarData);
        return calendar?.Todos.FirstOrDefault(t => t.RecurrenceIdentifier is null) ?? calendar?.Todos.FirstOrDefault();
    }

    public static TaskItem? ToItem(CalendarInfo list, CalendarObject item)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(item);
        Todo? todo;
        try
        {
            todo = TodoOf(item.ICalendarData);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException)
        {
            return null; // unreadable: not shown rather than breaking the list
        }

        if (todo is null)
        {
            return null;
        }

        var due = todo.Due;
        var completed = string.Equals(todo.Status, "COMPLETED", StringComparison.OrdinalIgnoreCase) || todo.Completed is not null;
        return new TaskItem(
            list,
            item.RemoteId,
            todo.Uid ?? item.RemoteId,
            string.IsNullOrWhiteSpace(todo.Summary) ? string.Empty : todo.Summary.Trim(),
            string.IsNullOrWhiteSpace(todo.Description) ? null : todo.Description,
            due is null ? null : ToLocal(due),
            due is { HasTime: true },
            completed,
            todo.Completed is null ? null : ToLocal(todo.Completed),
            PriorityOf(todo.Priority),
            todo.RecurrenceRule is not null)
        {
            Details = TaskDetails.Of(todo),
        };
    }

    /// <summary>Complete iCalendar data: <paramref name="existingData"/> updated, or a new VCALENDAR with one VTODO.</summary>
    public string ToICalendar(string? existingData)
    {
        ICalendar calendar;
        Todo todo;
        if (existingData is null)
        {
            calendar = new ICalendar { ProductId = "-//Neruna//Neruna Desktop//DE" };
            todo = new Todo { Uid = Guid.NewGuid().ToString("D") + "@neruna" };
            todo.Created = new CalDateTime(DateTime.UtcNow, "UTC");
            calendar.Todos.Add(todo);
        }
        else
        {
            calendar = ICalendar.Load(existingData) ?? throw new FormatException("Empty iCalendar data.");
            todo = calendar.Todos.FirstOrDefault(t => t.RecurrenceIdentifier is null) ?? calendar.Todos.FirstOrDefault()
                   ?? throw new FormatException("The data contains no task.");
            todo.Sequence++;
        }

        todo.Summary = Summary.Trim();
        todo.Description = string.IsNullOrWhiteSpace(Notes) ? null : Notes.Trim();
        if (Due is { } due)
        {
            if (DueHasTime)
            {
                var tzId = EventDraft.LocalTimeZoneId();
                if (calendar.TimeZones.All(tz => tz.TzId != tzId))
                {
                    calendar.AddTimeZone(tzId);
                }

                todo.Due = new CalDateTime(DateTime.SpecifyKind(due, DateTimeKind.Unspecified), tzId);
            }
            else
            {
                todo.Due = new CalDateTime(DateOnly.FromDateTime(due));
            }

            // A start after the due date confuses other clients (and is invalid): it goes.
            if (todo.DtStart is { } start && start.Value > todo.Due.Value)
            {
                todo.DtStart = null;
            }
        }
        else
        {
            todo.Due = null;
        }

        todo.Priority = Priority switch
        {
            TaskPriority.High => 1,
            TaskPriority.Normal => 5,
            TaskPriority.Low => 9,
            _ => 0,
        };

        if (Details is { } details)
        {
            ApplyDetails(todo, details);
        }

        var wasCompleted = string.Equals(todo.Status, "COMPLETED", StringComparison.OrdinalIgnoreCase) || todo.Completed is not null;
        if (IsCompleted && !wasCompleted && todo.RecurrenceRule is not null && MoveToNext(todo))
        {
            // A repeating task done: it comes back on its next day (as task programs do), open again.
        }
        else if (IsCompleted && !wasCompleted)
        {
            todo.Status = "COMPLETED";
            todo.Completed = new CalDateTime(DateTime.UtcNow, "UTC");
            todo.PercentComplete = 100;
        }
        else if (!IsCompleted && wasCompleted)
        {
            todo.Status = "NEEDS-ACTION";
            todo.Completed = null;
            todo.PercentComplete = 0;
        }
        else if (!IsCompleted && Details is { } state)
        {
            var wanted = state.State switch
            {
                TaskState.InProgress => "IN-PROCESS",
                TaskState.Cancelled => "CANCELLED",
                _ => "NEEDS-ACTION",
            };
            if (!string.Equals(todo.Status ?? "NEEDS-ACTION", wanted, StringComparison.OrdinalIgnoreCase))
            {
                todo.Status = wanted;
            }
        }

        todo.DtStamp = new CalDateTime(DateTime.UtcNow, "UTC");
        todo.LastModified = todo.DtStamp;
        return new CalendarSerializer().SerializeToString(calendar) ?? throw new InvalidOperationException("Serializing the task failed.");
    }

    private void ApplyDetails(Todo todo, TaskDetails details)
    {
        var read = TaskDetails.Of(todo);

        // Start: a day; after the due day it would be invalid (and confuses other programs).
        var start = details.Start is { } day && (Due is not { } due || day.Date <= due.Date) ? day.Date : (DateTime?)null;
        if (read.Start != start)
        {
            todo.DtStart = start is { } s ? new CalDateTime(DateOnly.FromDateTime(s)) : null;
        }

        var seriesStart = TaskDetails.SeriesStart(start, Due);
        todo.RecurrenceRule = EventDraft.RecurrenceRuleFor(todo.RecurrenceRule, details.Recurrence, details.Repeat, details.RecurrenceUntil, seriesStart, allDay: true);

        if (read.ReminderAt != details.ReminderAt)
        {
            foreach (var alarm in todo.Alarms.Where(a => a.Action is null or "DISPLAY" or "AUDIO").ToList())
            {
                todo.Alarms.Remove(alarm);
            }

            if (details.ReminderAt is { } at)
            {
                var utc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(at, DateTimeKind.Unspecified), TimeZoneInfo.Local);
                todo.Alarms.Add(new Alarm
                {
                    Action = "DISPLAY",
                    Description = Summary.Trim(),
                    Trigger = AbsoluteTrigger(new CalDateTime(utc, "UTC")),
                });
            }
        }

        var location = string.IsNullOrWhiteSpace(details.Location) ? null : details.Location.Trim();
        if (read.Location != location)
        {
            todo.Location = location;
        }

        if (read.Privacy != details.Privacy)
        {
            todo.Class = EventDraft.ClassOf(details.Privacy);
        }

        var url = string.IsNullOrWhiteSpace(details.Url) ? null : details.Url.Trim();
        if (read.Url != url)
        {
            todo.Url = url is not null && Uri.TryCreate(url, UriKind.Absolute, out var address) ? address : null;
        }

        if (!read.Links.SequenceEqual(details.Links))
        {
            foreach (var link in todo.Attachments.Where(a => a.Uri is not null).ToList())
            {
                todo.Attachments.Remove(link);
            }

            foreach (var link in details.Links)
            {
                todo.Attachments.Add(new Attachment { Uri = link });
            }
        }

        if (!read.Categories.SequenceEqual(details.Categories))
        {
            todo.Categories = [.. details.Categories];
        }
    }

    /// <summary>
    /// Moves a repeating task to its next occurrence (start and due shifted alike, a counting rule counts one down).
    /// False when the series has no further occurrence: then the task is simply done.
    /// </summary>
    internal static bool MoveToNext(Todo todo)
    {
        var rule = todo.RecurrenceRule!;
        var anchor = todo.DtStart ?? todo.Due;
        if (anchor is null || rule.Count is 1)
        {
            return false;
        }

        // The occurrences of the rule from the anchor on: the second one is the next.
        var probe = new Todo { DtStart = anchor, RecurrenceRule = rule };
        var next = probe.GetOccurrences(anchor).Skip(1).Select(o => o.Period.StartTime).FirstOrDefault();
        if (next is null)
        {
            return false;
        }

        var shift = next.Value - anchor.Value;
        if (todo.DtStart is { } start)
        {
            todo.DtStart = Shifted(start, shift);
        }

        if (todo.Due is { } due)
        {
            todo.Due = Shifted(due, shift);
        }

        if (rule.Count is { } count)
        {
            rule.Count = count - 1;
            todo.RecurrenceRule = rule;
        }

        foreach (var alarm in todo.Alarms)
        {
            if (alarm.Trigger?.DateTime is { } at)
            {
                alarm.Trigger = AbsoluteTrigger(Shifted(at, shift));
            }
        }

        todo.Status = "NEEDS-ACTION";
        todo.Completed = null;
        todo.PercentComplete = 0;
        return true;
    }

    // Ical.Net writes an absolute trigger only with VALUE=DATE-TIME set (else "TRIGGER:" stays empty).
    private static Trigger AbsoluteTrigger(CalDateTime at)
    {
        var trigger = new Trigger();
        trigger.Parameters.Set("VALUE", "DATE-TIME");
        trigger.DateTime = at;
        return trigger;
    }

    private static CalDateTime Shifted(CalDateTime value, TimeSpan shift) => !value.HasTime
        ? new CalDateTime(DateOnly.FromDateTime(value.Value + shift))
        : value.IsFloating ? new CalDateTime(value.Value + shift) : new CalDateTime(value.Value + shift, value.TzId);

    public static TaskPriority PriorityOf(int value) => value switch
    {
        >= 1 and <= 4 => TaskPriority.High,
        5 => TaskPriority.Normal,
        >= 6 and <= 9 => TaskPriority.Low,
        _ => TaskPriority.None,
    };

    private static DateTimeOffset ToLocal(CalDateTime value)
    {
        if (!value.HasTime)
        {
            var date = value.Date;
            return new DateTimeOffset(new DateTime(date.Year, date.Month, date.Day, 0, 0, 0, DateTimeKind.Local));
        }

        return value.IsFloating
            ? new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Local))
            : new DateTimeOffset(value.AsUtc).ToLocalTime();
    }
}
