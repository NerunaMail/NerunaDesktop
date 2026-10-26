using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neruna.Core;
using Neruna.Core.Calendar;
using Neruna.Desktop.Infrastructure;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.ViewModels;

/// <summary>What writing a mail needs to make an event for it: the calendars, the invitations and a window for the editor.</summary>
internal sealed record ComposeCalendar(CalendarController Calendar, InvitationService Invitations, IWindowService Windows, Neruna.Core.Meetings.MeetingController? Meetings = null);

/// <summary>The event made while writing (calendar, id there, what was saved).</summary>
internal sealed record ComposeAppointment(CalendarInfo Calendar, string RemoteId, EventDraft Draft)
{
    /// <summary>"Planung · Di, 3. Nov. 2026, 14:00–15:00 · Rigi" for the bar above the text.</summary>
    public string Text => string.Join(" · ", new[] { Draft.Summary, EventSummaryText.When(Draft), Draft.Location }.Where(p => !string.IsNullOrWhiteSpace(p)));
}

/// <summary>
/// "Termin" while writing: an event for this mail – the recipients in An and Cc become its attendees (more can be added),
/// the invitation goes the usual way (by the server, or by Neruna). Its details can then be put into the mail as text;
/// the mail itself carries no invitation, so nobody gets two.
/// </summary>
internal sealed partial class ComposeViewModel
{
    public ComposeCalendar? Calendars { get; init; }

    public bool CanMakeAppointment => Calendars is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAppointment))]
    [NotifyCanExecuteChangedFor(nameof(InsertAppointmentCommand))]
    public partial ComposeAppointment? Appointment { get; set; }

    public bool HasAppointment => Appointment is not null;

    [RelayCommand]
    private async Task MakeAppointmentAsync()
    {
        if (Calendars is not { } c)
        {
            return;
        }

        try
        {
            var owners = (await c.Calendar.GetSourcesAsync()).ToDictionary(s => s.Connection.Id, s => s.Account.EmailAddress);
            var writable = (await c.Calendar.GetCalendarsAsync()).Where(cal => !cal.IsReadOnly && cal.HasEvents).ToList();
            if (writable.Count == 0)
            {
                Notice?.Invoke(this, T("Kein beschreibbarer Kalender vorhanden, in den der Termin eingetragen werden kann."));
                return;
            }

            EventDraft draft;
            (CalendarInfo Calendar, string RemoteId)? existing = null;
            if (Appointment is { } made && await c.Calendar.GetObjectAsync(made.Calendar, made.RemoteId) is { } stored)
            {
                draft = EventDraft.FromICalendar(stored.ICalendarData);
                existing = (made.Calendar, made.RemoteId);
            }
            else
            {
                var now = DateTime.Now;
                draft = EventDraft.New(now.Date.AddHours(now.Hour + 1), await _settings.GetDefaultReminderAsync()) with
                {
                    Summary = Subject.Trim(),
                    Attendees = EventAttendee.FromRecipients(To, Cc),
                };
            }

            var editor = new EventEditorViewModel(c.Calendar, c.Invitations, writable, draft, existing, cal => owners.GetValueOrDefault(cal.ConnectionId))
            {
                Recipients = Recipients,
                Meetings = c.Meetings,
            };
            if (existing is null)
            {
                // The default calendar of the account the mail goes out from (Kalender verwalten, else the server's);
                // the general one (Einstellungen → Kalender) where it is this account's or the account has none.
                var preferred = await _settings.GetAsync(SettingKeys.CalendarDefaultCalendar);
                var best = (await c.Invitations.TargetCalendarsAsync(_account, preferred: preferred)).FirstOrDefault();
                editor.SelectedCalendar = writable.FirstOrDefault(cal => best is not null && CalendarController.CalendarKey(cal) == CalendarController.CalendarKey(best))
                                          ?? editor.SelectedCalendar;
            }

            if (!await c.Windows.ShowEventEditorAsync(editor))
            {
                return;
            }

            // Saved: shown above the text; deleted in the editor: gone.
            Appointment = editor.Saved is { } saved ? new ComposeAppointment(saved.Calendar, saved.RemoteId, saved.Draft) : null;
            if (editor.ResultMessage is { } message)
            {
                Notice?.Invoke(this, message);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Notice?.Invoke(this, T("Der Termin konnte nicht geöffnet werden: ") + ex.Message);
        }
    }

    /// <summary>Title, date, time and place as text at the caret – no iCalendar.</summary>
    [RelayCommand(CanExecute = nameof(HasAppointment))]
    private async Task InsertAppointmentAsync()
    {
        if (Appointment is not { } appointment || _editor is null)
        {
            return;
        }

        MarkChanged();
        var (html, text) = EventSummaryText.Format(appointment.Draft);
        await _editor.InsertAsync(html, text);
    }
}
