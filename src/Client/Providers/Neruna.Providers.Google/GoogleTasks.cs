using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Neruna.Core;
using Neruna.Core.Calendar;
using static Neruna.Core.Localization.Texts;
using CalendarObject = Neruna.Core.Calendar.CalendarObject;

namespace Neruna.Providers.Google;

/// <summary>
/// Google Tasks (REST; Google's CalDAV has no tasks), shown as task lists (CalendarContent.Tasks). Tasks become VTODO
/// and back, so the task list sees what it sees from CalDAV. Google keeps only a due date (no time), the notes and
/// whether the task is done. The version of a task is its "updated" time.
/// </summary>
internal sealed class GoogleTasks(HttpClient http, Func<CancellationToken, Task<string>> accessToken)
{
    /// <summary>Prefix of a task list's RemoteId (CalDAV calendars have their URL).</summary>
    public const string ListPrefix = "gtasks:";

    private static readonly Uri Base = new("https://tasks.googleapis.com/tasks/v1/");

    public static bool IsTaskList(CalendarInfo calendar) => calendar.RemoteId.StartsWith(ListPrefix, StringComparison.Ordinal);

    private static string ListId(CalendarInfo calendar) => Uri.EscapeDataString(calendar.RemoteId[ListPrefix.Length..]);

    public async Task<IReadOnlyList<CalendarInfo>> GetListsAsync(Guid connectionId, CancellationToken cancellationToken) =>
        [.. (await GetAllAsync("users/@me/lists?maxResults=100", cancellationToken))
            .Select(l => new CalendarInfo(connectionId, ListPrefix + Str(l, "id"), Str(l, "title") ?? string.Empty, null, false, Content: CalendarContent.Tasks))];

    public async Task<CalendarSyncResult> SyncAsync(CalendarInfo list, IReadOnlyDictionary<string, string?> knownVersions, CancellationToken cancellationToken)
    {
        // Done tasks too (hidden ones are those cleared in Google's own view: still tasks of the list).
        var tasks = await GetAllAsync($"lists/{ListId(list)}/tasks?maxResults=100&showCompleted=true&showHidden=true", cancellationToken);
        var changed = tasks.Where(t => !knownVersions.TryGetValue(Str(t, "id")!, out var version) || version != Str(t, "updated"))
            .Select(ToObject).ToList();
        var present = tasks.Select(t => Str(t, "id")).ToHashSet(StringComparer.Ordinal);
        return new CalendarSyncResult(string.Empty, false, changed, [.. knownVersions.Keys.Where(k => !present.Contains(k))]);
    }

    public async Task<CalendarObject> SaveAsync(CalendarInfo list, CalendarObject item, CancellationToken cancellationToken)
    {
        var body = ToGoogle(item.ICalendarData);
        var path = $"lists/{ListId(list)}/tasks";
        if (string.IsNullOrEmpty(item.RemoteId))
        {
            return ToObject(await SendAsync(HttpMethod.Post, path, body, cancellationToken));
        }

        var task = $"{path}/{Uri.EscapeDataString(item.RemoteId)}";
        var current = await SendAsync(HttpMethod.Get, task, null, cancellationToken);
        if (current.ValueKind != JsonValueKind.Object || (item.ETag is not null && Str(current, "updated") != item.ETag))
        {
            throw new RemoteConflictException();
        }

        return ToObject(await SendAsync(HttpMethod.Patch, task, body, cancellationToken));
    }

    public Task DeleteAsync(CalendarInfo list, CalendarObject item, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Delete, $"lists/{ListId(list)}/tasks/{Uri.EscapeDataString(item.RemoteId)}", null, cancellationToken);

    private static CalendarObject ToObject(JsonElement t) => new(Str(t, "id")!, Str(t, "updated"), ToICalendar(t));

    /// <summary>A Google task as VTODO.</summary>
    internal static string ToICalendar(JsonElement t)
    {
        var text = new StringBuilder("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Neruna//Google Tasks//DE\r\nBEGIN:VTODO\r\n");
        Line(text, "UID", Str(t, "id")!);
        Line(text, "DTSTAMP", Utc(Str(t, "updated")) ?? "20000101T000000Z");
        Line(text, "SUMMARY", Escape(Str(t, "title") ?? string.Empty));
        if (Str(t, "notes") is { Length: > 0 } notes)
        {
            Line(text, "DESCRIPTION", Escape(notes.Trim()));
        }

        // Google keeps the day only, written as midnight UTC.
        if (Str(t, "due") is { Length: >= 10 } due && DateOnly.TryParseExact(due[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            Line(text, "DUE;VALUE=DATE", day.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        }

        var completed = Str(t, "status") == "completed";
        Line(text, "STATUS", completed ? "COMPLETED" : "NEEDS-ACTION");
        if (completed && Utc(Str(t, "completed")) is { } done)
        {
            Line(text, "COMPLETED", done);
        }

        text.Append("END:VTODO\r\nEND:VCALENDAR\r\n");
        return text.ToString();
    }

    /// <summary>A VTODO (as Neruna edits it) as a Google task; null fields clear what was there.</summary>
    internal static Dictionary<string, object?> ToGoogle(string iCalendarData)
    {
        var todo = TaskDraft.TodoOf(iCalendarData) ?? throw new FormatException("The data contains no task.");
        var completed = string.Equals(todo.Status, "COMPLETED", StringComparison.OrdinalIgnoreCase) || todo.Completed is not null;
        return new Dictionary<string, object?>
        {
            ["title"] = todo.Summary ?? string.Empty,
            ["notes"] = todo.Description ?? string.Empty,
            ["due"] = todo.Due is { } due ? due.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "T00:00:00.000Z" : null,
            ["status"] = completed ? "completed" : "needsAction",
            // Back to open: Google keeps the completion time unless it is cleared.
            ["completed"] = completed ? todo.Completed is { } at ? at.AsUtc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture) : null : null,
        };
    }

    private async Task<List<JsonElement>> GetAllAsync(string path, CancellationToken cancellationToken)
    {
        var result = new List<JsonElement>();
        string? page = null;
        do
        {
            var answer = await SendAsync(HttpMethod.Get, page is null ? path : $"{path}&pageToken={Uri.EscapeDataString(page)}", null, cancellationToken);
            if (answer.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                result.AddRange(items.EnumerateArray().Select(i => i.Clone()));
            }

            page = Str(answer, "nextPageToken");
        }
        while (page is not null);

        return result;
    }

    /// <summary>The answer as JSON; an empty element for 204 and for a task that is gone (404).</summary>
    private async Task<JsonElement> SendAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, new Uri(Base, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await accessToken(cancellationToken));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        using var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotFound)
        {
            return default;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(F("Google Tasks antwortet mit Fehler {0}.", (int)response.StatusCode), null, response.StatusCode);
        }

        return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
    }

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? Utc(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)
            : null;

    private static void Line(StringBuilder text, string name, string value) => text.Append(name).Append(':').Append(value).Append("\r\n");

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace(";", "\\;", StringComparison.Ordinal).Replace(",", "\\,", StringComparison.Ordinal)
            .Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
}
