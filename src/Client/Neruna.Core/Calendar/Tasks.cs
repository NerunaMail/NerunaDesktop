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
    public static TaskDraft New(string summary) => new(summary, null, null, false, TaskPriority.None, false);

    public static TaskDraft FromItem(TaskItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return new TaskDraft(item.Summary, item.Notes, item.Due?.LocalDateTime, item.DueHasTime, item.Priority, item.IsCompleted);
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
            todo.RecurrenceRule is not null);
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

        var wasCompleted = string.Equals(todo.Status, "COMPLETED", StringComparison.OrdinalIgnoreCase) || todo.Completed is not null;
        if (IsCompleted && !wasCompleted)
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

        todo.DtStamp = new CalDateTime(DateTime.UtcNow, "UTC");
        todo.LastModified = todo.DtStamp;
        return new CalendarSerializer().SerializeToString(calendar) ?? throw new InvalidOperationException("Serializing the task failed.");
    }

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
