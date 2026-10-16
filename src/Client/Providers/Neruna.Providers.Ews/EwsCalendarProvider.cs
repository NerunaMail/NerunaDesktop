using System.Xml.Linq;
using Neruna.Core;
using Neruna.Core.Accounts;
using Neruna.Core.Calendar;
using static Neruna.Core.Localization.Texts;
using CalendarObject = Neruna.Core.Calendar.CalendarObject;

namespace Neruna.Providers.Ews;

/// <summary>
/// Calendars through Exchange Web Services, as iCalendar (<see cref="EwsCalendarConverter"/>). The version of an event
/// is its change key. Exchange sends invitations, updates, cancellations and answers itself: saving an invitation with
/// a new own answer accepts or declines it on the server, deleting someone else's meeting declines it.
/// </summary>
internal sealed class EwsCalendarProvider(ServiceConnection connection, EwsConnectionFactory connections) : ICalendarProvider, ICalendarScheduling, ICalendarFreeBusy
{
    /// <summary>
    /// Free/busy of people in the organisation (GetUserAvailability) – also without their calendar being shared;
    /// subject and place where they allow it. Asked in UTC, in batches of 100 addresses and at most 42 days.
    /// </summary>
    public async Task<IReadOnlyList<Availability>> GetFreeBusyAsync(IReadOnlyList<string> emails, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(emails);
        var client = await ClientAsync(cancellationToken);
        var end = windowEnd - windowStart > TimeSpan.FromDays(42) ? windowStart.AddDays(42) : windowEnd;
        var result = new List<Availability>();
        foreach (var batch in emails.Chunk(100))
        {
            var body = await client.SendAsync(AvailabilityRequest(batch, windowStart, end), cancellationToken);
            result.AddRange(EwsCalendarConverter.AvailabilityOf(batch, body));
        }

        return result;
    }

    internal static XElement AvailabilityRequest(IReadOnlyList<string> emails, DateTimeOffset windowStart, DateTimeOffset windowEnd)
    {
        static XElement Utc(string name) => X.Typ(name, X.Typ("Bias", 0), X.Typ("Time", "00:00:00"), X.Typ("DayOrder", 0), X.Typ("Month", 0), X.Typ("DayOfWeek", "Sunday"));
        static string At(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        return X.Msg("GetUserAvailabilityRequest",
            X.Typ("TimeZone", X.Typ("Bias", 0), Utc("StandardTime"), Utc("DaylightTime")),
            X.Msg("MailboxDataArray", emails.Select(e => X.Typ("MailboxData", X.Typ("Email", X.Typ("Address", e)), X.Typ("AttendeeType", "Required"), X.Typ("ExcludeConflicts", "false")))),
            X.Typ("FreeBusyViewOptions",
                X.Typ("TimeWindow", X.Typ("StartTime", At(windowStart)), X.Typ("EndTime", At(windowEnd))),
                X.Typ("MergedFreeBusyIntervalInMinutes", 30),
                X.Typ("RequestedView", "Detailed")));
    }

    private EwsClient? _client;

    public CalendarProviderCapabilities Capabilities => CalendarProviderCapabilities.Write | CalendarProviderCapabilities.Scheduling;

    private async Task<EwsClient> ClientAsync(CancellationToken cancellationToken) =>
        _client ??= await connections.ClientForAsync(connection, cancellationToken);

    private string Mailbox => EwsSettings.FromDictionary(connection.Settings).Mailbox;

    public async Task TestConnectionAsync(CancellationToken cancellationToken = default) =>
        await EwsOperations.WellKnownAsync(await ClientAsync(cancellationToken), ["calendar"], cancellationToken);

    /// <summary>The own calendar first, then the others of the mailbox; Exchange's birthday calendar is left out (Neruna has its own).</summary>
    public async Task<IReadOnlyList<CalendarInfo>> GetCalendarsAsync(CancellationToken cancellationToken = default)
    {
        var client = await ClientAsync(cancellationToken);
        var main = (await EwsOperations.WellKnownAsync(client, ["calendar"], cancellationToken)).GetValueOrDefault("calendar");
        var (_, folders) = await EwsOperations.FindFoldersAsync(client, cancellationToken, "folder:EffectiveRights");
        return [.. folders.Where(IsCalendar)
            .OrderBy(f => EwsOperations.FolderIdOf(f) != main)
            .Select(f => new CalendarInfo(connection.Id, EwsOperations.FolderIdOf(f), f.Val("DisplayName") ?? string.Empty, null,
                f.Get("EffectiveRights") is { } rights && !rights.Bool("CreateContents")))];
    }

    internal static bool IsCalendar(XElement folder) =>
        folder.Val("FolderClass") is { } folderClass && folderClass.StartsWith("IPF.Appointment", StringComparison.Ordinal) && folderClass != "IPF.Appointment.Birthday";

    public async Task<CalendarSyncResult> SyncCalendarAsync(CalendarInfo calendar, IReadOnlyDictionary<string, string?> knownVersions, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        ArgumentNullException.ThrowIfNull(knownVersions);
        var client = await ClientAsync(cancellationToken);

        // Without a calendar view, FindItem lists single events and series (not their occurrences).
        var (listed, _) = await EwsOperations.FindItemIdsAsync(client, calendar.RemoteId, cancellationToken);
        var changed = listed.Where(i => !knownVersions.TryGetValue(i.Id, out var version) || version != i.ChangeKey).Select(i => i.Id).ToList();
        var objects = await LoadAsync(client, changed, cancellationToken);
        var present = listed.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        return new CalendarSyncResult(string.Empty, false, objects, [.. knownVersions.Keys.Where(k => !present.Contains(k))]);
    }

    private async Task<List<CalendarObject>> LoadAsync(EwsClient client, IEnumerable<string> ids, CancellationToken cancellationToken)
    {
        var shape = EwsOperations.Shape(EwsCalendarConverter.Fields, "Text");
        var result = new List<CalendarObject>();
        foreach (var item in await EwsOperations.GetItemsAsync(client, ids, shape, cancellationToken, 50))
        {
            // Changed occurrences of a series are items of their own.
            var occurrenceIds = item.Get("ModifiedOccurrences")?.Elements(X.T + "Occurrence").Select(o => o.Id()).OfType<string>().ToList() ?? [];
            var occurrences = occurrenceIds.Count == 0 ? [] : await EwsOperations.GetItemsAsync(client, occurrenceIds, shape, cancellationToken, 50);
            result.Add(new CalendarObject(item.Id()!, item.ChangeKey(), EwsCalendarConverter.ToICalendar(item, occurrences, Mailbox)));
        }

        return result;
    }

    public async Task<CalendarObject> SaveAsync(CalendarInfo calendar, CalendarObject item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        ArgumentNullException.ThrowIfNull(item);
        var client = await ClientAsync(cancellationToken);
        var fields = EwsCalendarConverter.FieldsOf(item.ICalendarData, out var attendees);
        string id;
        if (string.IsNullOrEmpty(item.RemoteId))
        {
            // Someone else's meeting cannot be created here – Exchange would make it an invitation of the own.
            if (EventDraft.FromICalendar(item.ICalendarData).Organizer is { } organizer && attendees.Count > 0 && !IsMe(organizer))
            {
                throw new EwsException("ForeignMeeting", T("Die Einladung ist noch nicht im Exchange-Kalender. Bitte nach dem nächsten Abgleich erneut antworten."));
            }

            var created = (await client.CallAsync(X.Msg("CreateItem", new XAttribute("SendMeetingInvitations", attendees.Count > 0 ? "SendToAllAndSaveCopy" : "SendToNone"),
                X.Msg("SavedItemFolderId", X.FolderId(calendar.RemoteId)),
                X.Msg("Items", X.Typ("CalendarItem", fields.Select(f => f.Value)))), cancellationToken)).Single();
            id = created.FirstItem().Id() ?? throw new EwsException(null, "Exchange returned no item id.");
        }
        else
        {
            var current = await CurrentAsync(client, item.RemoteId, cancellationToken) ?? throw new RemoteConflictException();
            if (item.ETag is not null && current.ChangeKey() != item.ETag)
            {
                throw new RemoteConflictException();
            }

            if (IsForeignMeeting(current))
            {
                await AnswerAsync(client, current, item.ICalendarData, fields, cancellationToken);
            }
            else
            {
                await UpdateAsync(client, current, fields, attendees.Count > 0, cancellationToken);
            }

            id = item.RemoteId;
        }

        return (await LoadAsync(client, [id], cancellationToken)).SingleOrDefault() ?? throw new RemoteConflictException();
    }

    /// <summary>Only what changed, so Exchange tells attendees only about real changes (and a series keeps its exceptions).</summary>
    private async Task UpdateAsync(EwsClient client, XElement current, List<(string Field, XElement? Value)> fields, bool meeting, CancellationToken cancellationToken)
    {
        var before = EwsCalendarConverter.FieldsOf(EwsCalendarConverter.ToICalendar(current, [], Mailbox), out _).ToDictionary(f => f.Field, f => f.Value?.ToString());
        var timeChanged = fields.Any(f => f.Field is "calendar:Start" or "calendar:End" or "calendar:IsAllDayEvent" && before[f.Field] != f.Value?.ToString());
        var updates = new List<XElement>();
        foreach (var (field, value) in fields)
        {
            var zone = field is "calendar:StartTimeZone" or "calendar:EndTimeZone";
            if ((zone && !timeChanged) || (!zone && before[field] == value?.ToString()) || (value is null && field == "item:ReminderMinutesBeforeStart"))
            {
                continue;
            }

            updates.Add(value is null
                ? X.Typ("DeleteItemField", X.Field(field))
                : X.Typ("SetItemField", X.Field(field), X.Typ("CalendarItem", value)));
        }

        if (updates.Count == 0)
        {
            return;
        }

        await client.CallAsync(X.Msg("UpdateItem", new XAttribute("ConflictResolution", "NeverOverwrite"),
            new XAttribute("SendMeetingInvitationsOrCancellations", meeting ? "SendToChangedAndSaveCopy" : "SendToNone"),
            X.Msg("ItemChanges", X.Typ("ItemChange", X.ItemId(current.Id()!, current.ChangeKey()), X.Typ("Updates", updates)))), cancellationToken);
    }

    /// <summary>
    /// Someone else's meeting: a new own answer goes to the organizer (Exchange accepts or declines); otherwise only the
    /// own reminder can change.
    /// </summary>
    private async Task AnswerAsync(EwsClient client, XElement current, string iCalendarData, List<(string Field, XElement? Value)> fields, CancellationToken cancellationToken)
    {
        var mine = EventDraft.FromICalendar(iCalendarData).Attendees?.FirstOrDefault(a => IsMe(a.Email));
        if (mine is { Status: not Participation.NeedsAction } && EwsCalendarConverter.ResponseOf(mine.Status) != current.Val("MyResponseType"))
        {
            var answer = mine.Status switch { Participation.Accepted => "AcceptItem", Participation.Tentative => "TentativelyAcceptItem", _ => "DeclineItem" };
            await client.CallAsync(X.Msg("CreateItem", new XAttribute("MessageDisposition", "SendAndSaveCopy"),
                X.Msg("Items", X.Typ(answer, X.Typ("ReferenceItemId", new XAttribute("Id", current.Id()!), new XAttribute("ChangeKey", current.ChangeKey()!))))), cancellationToken);
            if (mine.Status == Participation.Declined)
            {
                return; // Exchange removes a declined meeting from the calendar
            }
        }

        var reminder = fields.Where(f => f.Field.StartsWith("item:Reminder", StringComparison.Ordinal) && f.Value is not null).ToList();
        if (reminder.Any(f => current.Val(f.Value!.Name.LocalName) != f.Value.Value))
        {
            await client.CallAsync(X.Msg("UpdateItem", new XAttribute("ConflictResolution", "AlwaysOverwrite"), new XAttribute("SendMeetingInvitationsOrCancellations", "SendToNone"),
                X.Msg("ItemChanges", X.Typ("ItemChange", X.ItemId(current.Id()!),
                    X.Typ("Updates", reminder.Select(f => X.Typ("SetItemField", X.Field(f.Field), X.Typ("CalendarItem", f.Value))))))), cancellationToken);
        }
    }

    public async Task DeleteAsync(CalendarInfo calendar, CalendarObject item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        var client = await ClientAsync(cancellationToken);
        if (await CurrentAsync(client, item.RemoteId, cancellationToken) is not { } current)
        {
            return; // already gone
        }

        // Someone else's meeting that still counts: declined (the organizer learns it), which also removes it.
        if (IsForeignMeeting(current) && !current.Bool("IsCancelled") && current.Val("MyResponseType") != "Decline")
        {
            await client.CallAsync(X.Msg("CreateItem", new XAttribute("MessageDisposition", "SendAndSaveCopy"),
                X.Msg("Items", X.Typ("DeclineItem", X.Typ("ReferenceItemId", new XAttribute("Id", current.Id()!), new XAttribute("ChangeKey", current.ChangeKey()!))))), cancellationToken);
            return;
        }

        var cancel = current.Bool("IsMeeting") && !IsForeignMeeting(current) && !current.Bool("IsCancelled");
        await client.CallAsync(X.Msg("DeleteItem", new XAttribute("DeleteType", "MoveToDeletedItems"),
            new XAttribute("SendMeetingCancellations", cancel ? "SendToAllAndSaveCopy" : "SendToNone"),
            X.Msg("ItemIds", X.ItemId(item.RemoteId))), cancellationToken, "ErrorItemNotFound");
    }

    /// <summary>Exchange sends invitations and answers itself.</summary>
    public Task<bool> SchedulesItselfAsync(CalendarInfo calendar, CancellationToken cancellationToken = default) => Task.FromResult(true);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static async Task<XElement?> CurrentAsync(EwsClient client, string id, CancellationToken cancellationToken) =>
        (await EwsOperations.GetItemsAsync(client, [id], EwsOperations.Shape(EwsCalendarConverter.Fields, "Text"), cancellationToken)).FirstOrDefault();

    private static bool IsForeignMeeting(XElement item) =>
        item.Bool("IsMeeting") && item.Val("MyResponseType") is { } response && response != "Organizer";

    private bool IsMe(string address) => string.Equals(address, Mailbox, StringComparison.OrdinalIgnoreCase);
}
