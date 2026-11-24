using Avalonia.Media;
using CommunityToolkit.Mvvm.Input;
using Neruna.Core.Calendar;
using Neruna.Core.Meetings;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.ViewModels;

/// <summary>An attendee as the details show them: name or address, and the answer.</summary>
internal sealed record EventDetailsAttendee(string Name, string? Status);

/// <summary>
/// An event to read before anything is changed: when, where, who, the online meeting and the description with its web
/// addresses clickable. "Bearbeiten" opens the editor – for an occurrence of a series the choice between this one and
/// the whole series is offered right here.
/// </summary>
internal sealed partial class EventDetailsViewModel : ViewModelBase
{
    private readonly Func<bool?, Task> _edit;

    /// <param name="draft">The event (for an occurrence of a series: that occurrence).</param>
    /// <param name="series">The whole series: how it repeats (the occurrence itself has no rule of its own).</param>
    /// <param name="edit">Opens the editor: null for an event alone, true "Nur diesen Termin", false "Ganze Serie".</param>
    public EventDetailsViewModel(CalendarOccurrence occurrence, EventDraft draft, EventDraft series, string color, Func<bool?, Task> edit)
    {
        ArgumentNullException.ThrowIfNull(occurrence);
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(series);
        Occurrence = occurrence;
        _edit = edit;
        Title = string.IsNullOrWhiteSpace(draft.Summary) ? T("(ohne Titel)") : draft.Summary;
        Location = string.IsNullOrWhiteSpace(draft.Location) ? null : draft.Location.Trim();
        Description = string.IsNullOrWhiteSpace(draft.Description) ? null : draft.Description.Trim();
        CalendarName = occurrence.Calendar.Name;
        Brush = Avalonia.Media.Brush.Parse(color);
        When = WhenText(occurrence);
        Repeats = occurrence.IsRecurring
            ? series.Repeat is { } spec && series.Recurrence != RecurrenceKind.Custom
                ? F("Serie: {0}", spec.Describe(series.Start, series.RecurrenceUntil))
                : T("Teil einer Serie") + (series.RecurrenceUntil is { } last ? " " + F("bis {0}", last.ToString("d. MMMM yyyy", Culture)) : string.Empty)
            : null;
        IsCancelled = draft.IsCancelled;
        PrivacyText = draft.Privacy switch
        {
            EventPrivacy.Private => T("Privat"),
            EventPrivacy.Confidential => T("Vertraulich"),
            _ => null,
        };
        Organizer = string.IsNullOrWhiteSpace(draft.Organizer) ? null : draft.Organizer;
        Attendees = [.. (draft.Attendees ?? []).Select(a => new EventDetailsAttendee(
            string.IsNullOrWhiteSpace(a.Name) ? a.Email : $"{a.Name} <{a.Email}>",
            a.Status switch
            {
                Participation.Accepted => T("zugesagt"),
                Participation.Tentative => T("mit Vorbehalt"),
                Participation.Declined => T("abgesagt"),
                _ => null,
            }))];
        Categories = string.Join(", ", occurrence.Categories);
        Url = string.IsNullOrWhiteSpace(draft.Url) ? null : draft.Url;
        Links = draft.Links is { Count: > 0 } links ? string.Join("\n", links.Select(l => l.OriginalString)) : null;
        if (draft.Meeting is { JoinUrl: { Length: > 0 } url } meeting && Uri.TryCreate(url, UriKind.Absolute, out var join))
        {
            MeetingName = MeetingKinds.NameOf(meeting.Service);
            MeetingUrl = join;
        }
    }

    public event EventHandler<bool>? Finished;

    public CalendarOccurrence Occurrence { get; }

    public string Title { get; }

    public string When { get; }

    public string? Repeats { get; }

    public bool HasRepeats => Repeats is not null;

    /// <summary>Called off (STATUS:CANCELLED): the title is struck through.</summary>
    public bool IsCancelled { get; }

    public Avalonia.Media.TextDecorationCollection? Decorations => IsCancelled ? Avalonia.Media.TextDecorations.Strikethrough : null;

    /// <summary>"Privat" or "Vertraulich" (shown with a lock); null for normal events.</summary>
    public string? PrivacyText { get; }

    public bool HasPrivacy => PrivacyText is not null;

    public string? Location { get; }

    public bool HasLocation => Location is not null;

    public string? Description { get; }

    public bool HasDescription => Description is not null;

    public string CalendarName { get; }

    public IBrush Brush { get; }

    public string? Organizer { get; }

    public bool HasOrganizer => Organizer is not null;

    public IReadOnlyList<EventDetailsAttendee> Attendees { get; }

    public bool HasAttendees => Attendees.Count > 0;

    /// <summary>The event's web page (iCalendar URL).</summary>
    public string? Url { get; }

    public bool HasUrl => Url is not null;

    /// <summary>Attachments given as web addresses, one per line.</summary>
    public string? Links { get; }

    public bool HasLinks => Links is not null;

    public string Categories { get; }

    public bool HasCategories => Categories.Length > 0;

    public string? MeetingName { get; }

    public Uri? MeetingUrl { get; }

    public bool HasMeeting => MeetingUrl is not null;

    public bool IsReadOnly => Occurrence.Calendar.IsReadOnly;

    /// <summary>One occurrence of a series in a calendar that can be changed: two ways to edit.</summary>
    public bool IsSeries => Occurrence.RecurrenceId is not null && !IsReadOnly;

    public bool IsSingle => !IsSeries && !IsReadOnly;

    [RelayCommand]
    private Task Edit() => _edit(null);

    [RelayCommand]
    private Task EditOccurrence() => _edit(true);

    [RelayCommand]
    private Task EditSeries() => _edit(false);

    [RelayCommand]
    private void Close() => Finished?.Invoke(this, false);

    private static string WhenText(CalendarOccurrence o)
    {
        var start = o.Start.LocalDateTime;
        var end = o.End.LocalDateTime;
        if (o.IsAllDay)
        {
            var last = end.Date.AddDays(-1);
            return last <= start.Date
                ? start.ToString("dddd, d. MMMM yyyy", Culture) + " · " + T("ganztägig")
                : start.ToString("ddd d. MMMM", Culture) + " – " + last.ToString("ddd d. MMMM yyyy", Culture) + " · " + T("ganztägig");
        }

        return start.Date == end.Date
            ? start.ToString("dddd, d. MMMM yyyy", Culture) + " · " + start.ToString("t", Culture) + " – " + end.ToString("t", Culture)
            : start.ToString("ddd d. MMMM yyyy, t", Culture) + " – " + end.ToString("ddd d. MMMM yyyy, t", Culture);
    }
}
