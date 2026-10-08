using System.Globalization;
using System.Text;
using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Serialization;
using Microsoft.Extensions.Logging;
using MimeKit;
using Neruna.Core.Accounts;
using Neruna.Core.Mail;
using ICalendar = Ical.Net.Calendar;

namespace Neruna.Core.Calendar;

/// <summary>What an iTIP message (RFC 5546) asks for.</summary>
public enum InvitationMethod
{
    /// <summary>An invitation, or an update of one.</summary>
    Request,

    /// <summary>An attendee's answer (to the organizer).</summary>
    Reply,

    /// <summary>The organizer cancelled the event (or uninvited this attendee).</summary>
    Cancel,
}

/// <summary>An invitation, answer or cancellation found in a mail (text/calendar with METHOD).</summary>
/// <param name="ICalendarData">The calendar data as received, including METHOD.</param>
public sealed record Invitation(
    InvitationMethod Method,
    string Uid,
    int Sequence,
    string Summary,
    string? Location,
    DateTimeOffset Start,
    DateTimeOffset End,
    bool IsAllDay,
    bool IsRecurring,
    string? Organizer,
    string? OrganizerName,
    IReadOnlyList<EventAttendee> Attendees,
    string ICalendarData)
{
    public EventAttendee? AttendeeFor(string? address) =>
        Attendees.FirstOrDefault(a => string.Equals(a.Email, address, StringComparison.OrdinalIgnoreCase));
}

/// <summary>For an invitation shown in a mail: is it already in the calendar, what did I answer, what does it collide with.</summary>
/// <param name="IsOutdated">The calendar already has a newer version (a later update was applied).</param>
public sealed record InvitationState(
    CalendarInfo? Calendar,
    CalendarObject? Item,
    Participation? MyAnswer,
    bool IsOutdated,
    IReadOnlyList<CalendarOccurrence> Conflicts);

/// <summary>
/// iTIP over e-mail (iMIP, RFC 6047): reading invitations from mails, writing invitations, answers and cancellations.
/// Pure functions on iCalendar text; <see cref="InvitationService"/> applies them to calendars and sends the mails.
/// </summary>
public static class ITip
{
    private const string ProductId = "-//Neruna//Neruna Desktop//DE";

    /// <summary>The iTIP part of a mail, if it has one (an .ics without METHOD is no invitation).</summary>
    public static Invitation? FromMessage(MimeMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var parts = message.BodyParts.OfType<MimePart>()
            .Where(p => p.ContentType.IsMimeType("text", "calendar") || p.ContentType.IsMimeType("application", "ics")
                        || (p.FileName?.EndsWith(".ics", StringComparison.OrdinalIgnoreCase) ?? false))
            .OrderByDescending(p => p.ContentType.IsMimeType("text", "calendar"));

        foreach (var part in parts)
        {
            using var stream = new MemoryStream();
            part.Content?.DecodeTo(stream);
            var charset = part.ContentType.Charset is { } name ? EncodingOf(name) : Encoding.UTF8;
            if (Parse(charset.GetString(stream.ToArray())) is { } invitation)
            {
                return invitation;
            }
        }

        return null;
    }

    public static Invitation? Parse(string data)
    {
        ICalendar? calendar;
        try
        {
            calendar = ICalendar.Load(data);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }

        var method = calendar?.Method?.ToUpperInvariant() switch
        {
            "REQUEST" => InvitationMethod.Request,
            "REPLY" => InvitationMethod.Reply,
            "CANCEL" => InvitationMethod.Cancel,
            _ => (InvitationMethod?)null,
        };
        var evt = calendar?.Events.FirstOrDefault(e => e.RecurrenceIdentifier is null) ?? calendar?.Events.FirstOrDefault();
        if (method is null || evt?.Uid is null || evt.DtStart is null)
        {
            return null;
        }

        var start = ToOffset(evt.DtStart);
        var end = evt.DtEnd is { } dtEnd ? ToOffset(dtEnd) : evt.Duration is { } duration ? ToOffset(evt.DtStart.Add(duration)) : start.AddHours(evt.IsAllDay ? 24 : 1);
        return new Invitation(
            method.Value,
            evt.Uid,
            evt.Sequence,
            string.IsNullOrWhiteSpace(evt.Summary) ? "(ohne Titel)" : evt.Summary,
            evt.Location,
            start,
            end,
            evt.IsAllDay,
            evt.RecurrenceRule is not null,
            EventDraft.AddressOf(evt.Organizer?.Value),
            evt.Organizer?.CommonName,
            EventDraft.AttendeesOf(evt),
            data);
    }

    /// <summary>The event as an invitation (METHOD:REQUEST); sent again after every change.</summary>
    public static string Request(string eventData) => WithMethod(eventData, "REQUEST", _ => { });

    /// <summary>The event as a cancellation (METHOD:CANCEL, STATUS:CANCELLED).</summary>
    public static string Cancel(string eventData) => WithMethod(eventData, "CANCEL", evt =>
    {
        evt.Status = "CANCELLED";
        evt.Sequence++;
    });

    /// <summary>An attendee's answer to the organizer: only the essentials and the one attendee (RFC 5546 §3.2.3).</summary>
    public static string Reply(Invitation invitation, string attendee, string? attendeeName, Participation answer)
    {
        ArgumentNullException.ThrowIfNull(invitation);
        var source = MasterOf(ICalendar.Load(invitation.ICalendarData)!);
        var calendar = new ICalendar { ProductId = ProductId, Method = "REPLY" };
        var evt = new CalendarEvent
        {
            Uid = invitation.Uid,
            Sequence = invitation.Sequence,
            DtStamp = new CalDateTime(DateTime.UtcNow, "UTC"),
            DtStart = source.DtStart,
            DtEnd = source.DtEnd,
            Summary = source.Summary,
            Organizer = source.Organizer,
        };
        evt.Attendees.Add(new Attendee("mailto:" + attendee) { CommonName = attendeeName, ParticipationStatus = EventAttendee.FormatStatus(answer) });
        calendar.Events.Add(evt);
        return Serialize(calendar);
    }

    /// <summary>The invitation as it goes into the own calendar: without METHOD, with the own answer.</summary>
    /// <param name="reminderMinutes">The user's default reminder, added if the event has none (null = none).</param>
    public static string ForOwnCalendar(string invitationData, string me, Participation answer, int? reminderMinutes = EventDraft.DefaultReminderMinutes)
    {
        var calendar = ICalendar.Load(invitationData) ?? throw new FormatException("Empty iCalendar data.");
        calendar.Method = null;
        foreach (var evt in calendar.Events)
        {
            var own = evt.Attendees.FirstOrDefault(a => string.Equals(EventDraft.AddressOf(a.Value), me, StringComparison.OrdinalIgnoreCase));
            if (own is null)
            {
                // Invited through a distribution list or another address: add oneself, so the answer is recorded.
                own = new Attendee("mailto:" + me);
                evt.Attendees.Add(own);
            }

            own.ParticipationStatus = EventAttendee.FormatStatus(answer);
            own.Rsvp = false;

            // The organizer's reminders are not sent along; an accepted meeting gets the user's default.
            if (reminderMinutes is { } minutes && !evt.Alarms.Any(EventDraft.IsUserAlarm))
            {
                evt.Alarms.Add(new Alarm
                {
                    Action = "DISPLAY",
                    Description = evt.Summary ?? "Erinnerung",
                    Trigger = new Trigger(Duration.FromMinutes(-minutes)),
                });
            }
        }

        return Serialize(calendar);
    }

    /// <summary>The organizer's copy with an attendee's answer recorded; null if nothing changes.</summary>
    public static string? ApplyReply(string eventData, Invitation reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        if (reply.Attendees.FirstOrDefault() is not { } answer)
        {
            return null;
        }

        var calendar = ICalendar.Load(eventData) ?? throw new FormatException("Empty iCalendar data.");
        var changed = false;
        foreach (var evt in calendar.Events)
        {
            var attendee = evt.Attendees.FirstOrDefault(a => string.Equals(EventDraft.AddressOf(a.Value), answer.Email, StringComparison.OrdinalIgnoreCase));
            var status = EventAttendee.FormatStatus(answer.Status);
            if (attendee is not null && !string.Equals(attendee.ParticipationStatus, status, StringComparison.OrdinalIgnoreCase))
            {
                attendee.ParticipationStatus = status;
                attendee.Rsvp = false;
                changed = true;
            }
        }

        return changed ? Serialize(calendar) : null;
    }

    /// <summary>
    /// The iMIP mail: a readable text, the calendar data as text/calendar (what calendar programs read) and as an
    /// attached invite.ics (for programs that only look at attachments).
    /// </summary>
    public static MimeMessage Mail(MailboxAddress from, IEnumerable<MailboxAddress> to, string subject, string text, string iCalendarData, string method)
    {
        var message = new MimeMessage { Subject = subject };
        message.From.Add(from);
        message.To.AddRange(to);

        var calendarPart = new TextPart("calendar") { Text = iCalendarData };
        calendarPart.ContentType.Parameters.Add("method", method);
        calendarPart.ContentType.Charset = "utf-8";
        var attachment = new MimePart("application", "ics")
        {
            Content = new MimeContent(new MemoryStream(Encoding.UTF8.GetBytes(iCalendarData))),
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
            ContentTransferEncoding = ContentEncoding.Base64,
            FileName = "invite.ics",
        };

        var alternative = new MultipartAlternative { new TextPart("plain") { Text = text }, calendarPart };
        message.Body = new Multipart("mixed") { alternative, attachment };
        return message;
    }

    /// <summary>"Do, 15. Okt. 2026 14:00–15:00" for subjects and texts.</summary>
    public static string When(DateTimeOffset start, DateTimeOffset end, bool isAllDay)
    {
        var culture = CultureInfo.GetCultureInfo("de-CH");
        var day = start.ToString("ddd, d. MMM yyyy", culture);
        return isAllDay ? day + " (ganztägig)" : $"{day} {start:HH:mm}–{end:HH:mm}";
    }

    private static string WithMethod(string eventData, string method, Action<CalendarEvent> change)
    {
        var calendar = ICalendar.Load(eventData) ?? throw new FormatException("Empty iCalendar data.");
        calendar.Method = method;
        foreach (var evt in calendar.Events)
        {
            change(evt);

            // Reminders are personal; the attendees set their own.
            evt.Alarms.Clear();
        }

        return Serialize(calendar);
    }

    private static CalendarEvent MasterOf(ICalendar calendar) =>
        calendar.Events.FirstOrDefault(e => e.RecurrenceIdentifier is null) ?? calendar.Events.First();

    private static string Serialize(ICalendar calendar) =>
        new CalendarSerializer().SerializeToString(calendar) ?? throw new InvalidOperationException("Serializing failed.");

    private static DateTimeOffset ToOffset(CalDateTime value) =>
        !value.HasTime || value.IsFloating
            ? new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Local))
            : new DateTimeOffset(value.AsUtc, TimeSpan.Zero).ToLocalTime();

    private static Encoding EncodingOf(string name)
    {
        try
        {
            return Encoding.GetEncoding(name);
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8;
        }
    }
}

/// <summary>
/// Invitations end to end: as organizer, sends invitations, updates and cancellations; as attendee, answers
/// (accept / tentative / decline) and applies cancellations; as organizer again, records the answers.
/// When the calendar server delivers these itself (RFC 6638: SOGo, Nextcloud …), only the calendar is changed and the
/// server sends the mails – otherwise Neruna sends them through the account's mail connection.
/// </summary>
public sealed class InvitationService(CalendarController calendars, MailController mail, IAccountStore accounts, ISettingsStore settings, ILogger<InvitationService> logger)
{
    public async Task<InvitationState> GetStateAsync(Invitation invitation, Account account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invitation);
        ArgumentNullException.ThrowIfNull(account);
        var existing = await calendars.FindByUidAsync(invitation.Uid, cancellationToken);
        Participation? answer = null;
        var outdated = false;
        if (existing is { } e && ITip.Parse(AsRequest(e.Item.ICalendarData)) is { } stored)
        {
            answer = stored.AttendeeFor(account.EmailAddress)?.Status;
            outdated = stored.Sequence > invitation.Sequence;
        }

        var all = await calendars.GetCalendarsAsync(cancellationToken);
        var conflicts = invitation.IsAllDay
            ? []
            : (await calendars.GetOccurrencesAsync(all, invitation.Start, invitation.End, cancellationToken))
                .Where(o => o.Uid != invitation.Uid && !o.IsAllDay && o.Start < invitation.End && o.End > invitation.Start)
                .ToList();
        return new InvitationState(existing?.Calendar, existing?.Item, answer, outdated, conflicts);
    }

    /// <summary>
    /// Answers an invitation: accepted/tentative puts it into the calendar (or updates it there), declined removes it;
    /// then the organizer is told.
    /// </summary>
    /// <exception cref="InvalidOperationException">No writable calendar, or no mail connection to answer with.</exception>
    /// <param name="target">Where a new event goes; null for <see cref="DefaultCalendarAsync"/>. Ignored if the event is already in a calendar.</param>
    public async Task RespondAsync(Invitation invitation, Participation answer, Account account, CalendarInfo? target = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invitation);
        ArgumentNullException.ThrowIfNull(account);
        var me = account.EmailAddress ?? throw new InvalidOperationException("Das Konto hat keine E-Mail-Adresse.");
        var existing = await calendars.FindByUidAsync(invitation.Uid, cancellationToken);
        if (existing is null && target is not null)
        {
            await settings.SetAsync(DefaultKey(account), target.ConnectionId.ToString("N") + "|" + target.RemoteId, cancellationToken);
        }

        target = existing?.Calendar ?? target ?? await DefaultCalendarAsync(account, cancellationToken);
        var serverReplies = await calendars.SchedulesItselfAsync(target, cancellationToken);

        if (answer == Participation.Declined)
        {
            if (existing is { } declined)
            {
                await calendars.DeleteEventAsync(declined.Calendar, declined.Item.RemoteId, cancellationToken);
            }
        }
        else
        {
            // A newer version from the organizer replaces the stored one; the same version keeps local additions.
            var basis = existing is { } current && ITip.Parse(AsRequest(current.Item.ICalendarData)) is { } stored && stored.Sequence >= invitation.Sequence
                ? current.Item.ICalendarData
                : invitation.ICalendarData;
            var reminder = await settings.GetDefaultReminderAsync(cancellationToken);
            await calendars.SaveDataAsync(target, ITip.ForOwnCalendar(basis, me, answer, reminder), existing?.Item, cancellationToken);
        }

        if (serverReplies || invitation.Organizer is null)
        {
            logger.LogInformation("Answered {Uid} with {Answer}; the server tells the organizer", invitation.Uid, answer);
            return;
        }

        var verb = answer switch
        {
            Participation.Accepted => "Zugesagt",
            Participation.Tentative => "Mit Vorbehalt zugesagt",
            _ => "Abgelehnt",
        };
        var text = answer switch
        {
            Participation.Accepted => "hat die Einladung angenommen.",
            Participation.Tentative => "hat die Einladung mit Vorbehalt angenommen.",
            _ => "hat die Einladung abgelehnt.",
        };
        var sender = new MailboxAddress(account.DisplayName, me);
        var message = ITip.Mail(
            sender,
            [new MailboxAddress(invitation.OrganizerName, invitation.Organizer)],
            $"{verb}: {invitation.Summary}",
            $"{account.DisplayName} {text}\n\n{invitation.Summary}\n{ITip.When(invitation.Start, invitation.End, invitation.IsAllDay)}",
            ITip.Reply(invitation, me, account.DisplayName, answer),
            "REPLY");
        await mail.SendAsync(MailConnection(account), message, cancellationToken);
        logger.LogInformation("Answered {Uid} with {Answer} by e-mail", invitation.Uid, answer);
    }

    /// <summary>A cancellation: removes the event from the calendar.</summary>
    public async Task ApplyCancelAsync(Invitation invitation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invitation);
        if (await calendars.FindByUidAsync(invitation.Uid, cancellationToken) is { } existing)
        {
            await calendars.DeleteEventAsync(existing.Calendar, existing.Item.RemoteId, cancellationToken);
        }
    }

    /// <summary>An attendee's answer to my invitation: recorded in my event. Returns false if there was nothing to change.</summary>
    public async Task<bool> ApplyReplyAsync(Invitation reply, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reply);
        if (await calendars.FindByUidAsync(reply.Uid, cancellationToken) is not { } existing
            || existing.Calendar.IsReadOnly
            || ITip.ApplyReply(existing.Item.ICalendarData, reply) is not { } updated)
        {
            return false;
        }

        await calendars.SaveDataAsync(existing.Calendar, updated, existing.Item, cancellationToken);
        return true;
    }

    /// <summary>
    /// After the organizer saved an event: invitations (or updates) to all attendees, cancellations to removed ones.
    /// Nothing if the server does it, the event has no attendees, or someone else organizes it.
    /// </summary>
    /// <returns>How many people were written to.</returns>
    public async Task<int> SendAfterSaveAsync(CalendarInfo calendar, string? previousData, string newData, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        var account = await AccountOfAsync(calendar.ConnectionId, cancellationToken);
        var me = account.EmailAddress ?? throw new InvalidOperationException("Das Konto hat keine E-Mail-Adresse.");
        var current = ITip.Parse(AsRequest(newData));
        var before = previousData is null ? null : ITip.Parse(AsRequest(previousData));
        if (current is null || !IsMine(current, me) || await calendars.SchedulesItselfAsync(calendar, cancellationToken))
        {
            return 0;
        }

        var invited = Others(current, me);
        var removed = before is null ? [] : Others(before, me).Where(b => !invited.Any(a => Same(a, b))).ToList();
        if (invited.Count == 0 && removed.Count == 0)
        {
            return 0;
        }

        var sender = new MailboxAddress(account.DisplayName, me);
        var when = ITip.When(current.Start, current.End, current.IsAllDay);
        if (invited.Count > 0)
        {
            var update = before is not null;
            var text = $"{account.DisplayName} {(update ? "hat den Termin geändert" : "lädt Sie ein")}:\n\n{current.Summary}\n{when}" +
                       (string.IsNullOrWhiteSpace(current.Location) ? string.Empty : $"\nOrt: {current.Location}");
            await mail.SendAsync(MailConnection(account), ITip.Mail(sender, invited.Select(ToMailbox), $"{(update ? "Aktualisiert" : "Einladung")}: {current.Summary} ({when})", text, ITip.Request(newData), "REQUEST"), cancellationToken);
        }

        if (removed.Count > 0)
        {
            await mail.SendAsync(MailConnection(account), ITip.Mail(sender, removed.Select(ToMailbox), $"Abgesagt: {current.Summary} ({when})", $"{account.DisplayName} hat Sie von diesem Termin ausgeladen:\n\n{current.Summary}\n{when}", ITip.Cancel(previousData!), "CANCEL"), cancellationToken);
        }

        logger.LogInformation("Invitations for {Uid}: {Invited} invited, {Removed} cancelled", current.Uid, invited.Count, removed.Count);
        return invited.Count + removed.Count;
    }

    /// <summary>Before the organizer deletes an event with attendees: tells them it is cancelled.</summary>
    public async Task<int> SendCancellationAsync(CalendarInfo calendar, string data, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        var account = await AccountOfAsync(calendar.ConnectionId, cancellationToken);
        if (account.EmailAddress is null || ITip.Parse(AsRequest(data)) is not { } evt || !IsMine(evt, account.EmailAddress) || Others(evt, account.EmailAddress) is not { Count: > 0 } attendees
            || await calendars.SchedulesItselfAsync(calendar, cancellationToken))
        {
            return 0;
        }

        var when = ITip.When(evt.Start, evt.End, evt.IsAllDay);
        await mail.SendAsync(MailConnection(account), ITip.Mail(
            new MailboxAddress(account.DisplayName, account.EmailAddress), attendees.Select(ToMailbox),
            $"Abgesagt: {evt.Summary} ({when})", $"{account.DisplayName} hat diesen Termin abgesagt:\n\n{evt.Summary}\n{when}",
            ITip.Cancel(data), "CANCEL"), cancellationToken);
        return attendees.Count;
    }

    /// <summary>Is <paramref name="me"/> the organizer of this event (or nobody is)?</summary>
    public static bool IsMine(Invitation evt, string? me) =>
        evt.Organizer is null || string.Equals(evt.Organizer, me, StringComparison.OrdinalIgnoreCase);

    // Stored events carry no METHOD; for reading them with the same parser, pretend they were sent.
    private static string AsRequest(string data) =>
        data.Contains("METHOD:", StringComparison.OrdinalIgnoreCase) ? data : data.Replace("BEGIN:VCALENDAR\r\n", "BEGIN:VCALENDAR\r\nMETHOD:REQUEST\r\n", StringComparison.Ordinal).Replace("BEGIN:VCALENDAR\n", "BEGIN:VCALENDAR\nMETHOD:REQUEST\n", StringComparison.Ordinal);

    private static List<EventAttendee> Others(Invitation evt, string? me) =>
        evt.Attendees.Where(a => !string.Equals(a.Email, me, StringComparison.OrdinalIgnoreCase)).ToList();

    private static bool Same(EventAttendee a, EventAttendee b) => string.Equals(a.Email, b.Email, StringComparison.OrdinalIgnoreCase);

    private static MailboxAddress ToMailbox(EventAttendee attendee) => new(attendee.Name, attendee.Email);

    private static ServiceConnection MailConnection(Account account) =>
        account.ConnectionsOf(ServiceKind.Mail).FirstOrDefault()
        ?? throw new InvalidOperationException($"Das Konto {account.EmailAddress} hat keine E-Mail-Verbindung – Einladungen und Antworten können nicht versendet werden.");

    private async Task<Account> AccountOfAsync(Guid connectionId, CancellationToken cancellationToken) =>
        (await accounts.GetAccountsAsync(cancellationToken)).FirstOrDefault(a => a.Connections.Any(c => c.Id == connectionId))
        ?? throw new InvalidOperationException("Das Konto dieses Kalenders existiert nicht mehr.");

    /// <summary>Writable calendars an accepted invitation can go into, the best choice first.</summary>
    public async Task<IReadOnlyList<CalendarInfo>> TargetCalendarsAsync(Account account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        var writable = (await calendars.GetCalendarsAsync(cancellationToken)).Where(c => !c.IsReadOnly).ToList();
        var own = account.Connections.Select(c => c.Id).ToHashSet();
        var chosen = await settings.GetAsync(DefaultKey(account), cancellationToken);
        var local = account.EmailAddress?.Split('@')[0];

        // The one chosen last time; else a calendar of this account in the user's own home (its address contains the
        // user name – subscribed calendars of others live under their names); else any of this account; else any.
        int Rank(CalendarInfo c)
        {
            if (chosen == c.ConnectionId.ToString("N") + "|" + c.RemoteId)
            {
                return 0;
            }

            var path = Uri.UnescapeDataString(c.RemoteId);
            var inOwnHome = (account.EmailAddress is { } email && path.Contains("/" + email + "/", StringComparison.OrdinalIgnoreCase))
                            || (local is not null && path.Contains("/" + local + "/", StringComparison.OrdinalIgnoreCase));
            return own.Contains(c.ConnectionId) ? (inOwnHome ? 1 : 2) : 3;
        }

        // Among equals, the usual default calendar ("personal" in SOGo and Nextcloud).
        static bool LooksDefault(CalendarInfo c) =>
            c.RemoteId.TrimEnd('/').EndsWith("/personal", StringComparison.OrdinalIgnoreCase)
            || c.Name is "Kalender" or "Calendar" or "Persönlicher Kalender" or "Personal" or "personal";

        return writable.OrderBy(Rank).ThenBy(c => LooksDefault(c) ? 0 : 1).ToList();
    }

    public async Task<CalendarInfo> DefaultCalendarAsync(Account account, CancellationToken cancellationToken = default) =>
        (await TargetCalendarsAsync(account, cancellationToken)).FirstOrDefault()
        ?? throw new InvalidOperationException("Kein beschreibbarer Kalender vorhanden, in den der Termin eingetragen werden kann.");

    private static string DefaultKey(Account account) => $"invitations.calendar.{account.Id:N}";
}
