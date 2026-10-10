using System.Globalization;
using System.Text;
using System.Text.Json;
using Neruna.Core;
using Neruna.Core.Calendar;
using CalendarObject = Neruna.Core.Calendar.CalendarObject;

namespace Neruna.Providers.Graph;

/// <summary>
/// Microsoft To Do lists through Graph, shown as task lists (CalendarContent.Tasks). Tasks become VTODO and back, so
/// the task list sees what it sees from CalDAV. Needs the Tasks.ReadWrite permission: accounts signed in before it was
/// added get tasks after signing in again. The version of a task is its last-modified time.
/// </summary>
internal static class GraphTasks
{
    /// <summary>Prefix of a To Do list's RemoteId (calendars have Graph's bare id).</summary>
    public const string ListPrefix = "todo:";

    public static bool IsTaskList(CalendarInfo calendar) => calendar.RemoteId.StartsWith(ListPrefix, StringComparison.Ordinal);

    private static string ListId(CalendarInfo calendar) => calendar.RemoteId[ListPrefix.Length..];

    public static async Task<IReadOnlyList<CalendarInfo>> GetListsAsync(Guid connectionId, GraphClient graph, CancellationToken cancellationToken)
    {
        if (!await graph.GrantsAsync(MicrosoftAccount.TasksScope, cancellationToken))
        {
            return [];
        }

        return [.. (await graph.GetAllAsync("me/todo/lists?$top=100", cancellationToken))
            .Select(l => new CalendarInfo(connectionId, ListPrefix + l.Str("id"), l.Str("displayName") ?? string.Empty, null, false, Content: CalendarContent.Tasks))];
    }

    public static async Task<CalendarSyncResult> SyncAsync(CalendarInfo list, IReadOnlyDictionary<string, string?> knownVersions, GraphClient graph, CancellationToken cancellationToken)
    {
        var tasks = await graph.GetAllAsync($"me/todo/lists/{ListId(list)}/tasks?$top=100", cancellationToken);
        var changed = tasks.Where(t => !knownVersions.TryGetValue(t.Str("id")!, out var version) || version != t.Str("lastModifiedDateTime"))
            .Select(ToObject).ToList();
        var present = tasks.Select(t => t.Str("id")).ToHashSet(StringComparer.Ordinal);
        return new CalendarSyncResult(string.Empty, false, changed, [.. knownVersions.Keys.Where(k => !present.Contains(k))]);
    }

    public static async Task<CalendarObject> SaveAsync(CalendarInfo list, CalendarObject item, GraphClient graph, CancellationToken cancellationToken)
    {
        var body = ToGraph(item.ICalendarData);
        var path = $"me/todo/lists/{ListId(list)}/tasks";
        if (string.IsNullOrEmpty(item.RemoteId))
        {
            return ToObject(await graph.SendJsonAsync(HttpMethod.Post, path, body, cancellationToken));
        }

        var current = await graph.GetAsync($"{path}/{item.RemoteId}", cancellationToken);
        if (item.ETag is not null && current.Str("lastModifiedDateTime") != item.ETag)
        {
            throw new RemoteConflictException();
        }

        return ToObject(await graph.SendJsonAsync(HttpMethod.Patch, $"{path}/{item.RemoteId}", body, cancellationToken));
    }

    public static Task DeleteAsync(CalendarInfo list, CalendarObject item, GraphClient graph, CancellationToken cancellationToken) =>
        graph.SendJsonAsync(HttpMethod.Delete, $"me/todo/lists/{ListId(list)}/tasks/{item.RemoteId}", null, cancellationToken);

    private static CalendarObject ToObject(JsonElement t) => new(t.Str("id")!, t.Str("lastModifiedDateTime"), ToICalendar(t));

    /// <summary>A To Do task as VTODO. To Do keeps only a due date (no time).</summary>
    internal static string ToICalendar(JsonElement t)
    {
        var text = new StringBuilder("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Neruna//Microsoft To Do//DE\r\nBEGIN:VTODO\r\n");
        Line(text, "UID", t.Str("id")!);
        Line(text, "DTSTAMP", Utc(t.Str("lastModifiedDateTime")) ?? "20000101T000000Z");
        Line(text, "SUMMARY", Escape(t.Str("title") ?? string.Empty));
        if (t.Obj("body").Str("content") is { Length: > 0 } notes && t.Obj("body").Str("contentType") != "html")
        {
            Line(text, "DESCRIPTION", Escape(notes.Trim()));
        }

        if (DateOf(t.Obj("dueDateTime")) is { } due)
        {
            Line(text, "DUE;VALUE=DATE", due.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        }

        var completed = t.Str("status") == "completed";
        Line(text, "STATUS", completed ? "COMPLETED" : t.Str("status") == "inProgress" ? "IN-PROCESS" : "NEEDS-ACTION");
        if (completed && Utc(t.Obj("completedDateTime").Str("dateTime")) is { } done)
        {
            Line(text, "COMPLETED", done);
        }

        Line(text, "PRIORITY", t.Str("importance") switch { "high" => "1", "low" => "9", _ => "0" });
        if (GraphCalendarProvider.RecurrenceOf(t.Obj("recurrence")) is { } rule)
        {
            Line(text, "RRULE", rule);
        }

        text.Append("END:VTODO\r\nEND:VCALENDAR\r\n");
        return text.ToString();
    }

    /// <summary>A VTODO (as Neruna edits it) as a To Do task.</summary>
    internal static Dictionary<string, object?> ToGraph(string iCalendarData)
    {
        var todo = TaskDraft.TodoOf(iCalendarData) ?? throw new FormatException("The data contains no task.");
        var completed = string.Equals(todo.Status, "COMPLETED", StringComparison.OrdinalIgnoreCase) || todo.Completed is not null;
        return new Dictionary<string, object?>
        {
            ["title"] = todo.Summary ?? string.Empty,
            ["body"] = new { content = todo.Description ?? string.Empty, contentType = "text" },
            ["dueDateTime"] = todo.Due is { } due
                ? new { dateTime = due.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "T00:00:00", timeZone = "UTC" }
                : null,
            ["status"] = completed ? "completed" : "notStarted",
            ["importance"] = TaskDraft.PriorityOf(todo.Priority) switch { TaskPriority.High => "high", TaskPriority.Low => "low", _ => "normal" },
        };
    }

    // To Do stores the due day as midnight in the user's time zone (often written as UTC): the date part is the day.
    private static DateOnly? DateOf(JsonElement dateTime) =>
        dateTime.Str("dateTime") is { Length: >= 10 } value && DateOnly.TryParseExact(value[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;

    private static string? Utc(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)
            : null;

    private static void Line(StringBuilder text, string name, string value) => text.Append(name).Append(':').Append(value).Append("\r\n");

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace(";", "\\;", StringComparison.Ordinal).Replace(",", "\\,", StringComparison.Ordinal)
            .Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
}
