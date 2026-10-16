using System.Net;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Core.Calendar;

/// <summary>
/// An event as text for a mail ("Termininfos einfügen" while writing): title, date and time, place – plain text in the
/// mail, no iCalendar (the invitation goes separately, by the server or by Neruna).
/// </summary>
public static class EventSummaryText
{
    /// <returns>HTML for the editor and the same as plain text.</returns>
    public static (string Html, string Text) Format(EventDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var lines = new List<(string Label, string Value)>
        {
            (T("Termin"), draft.Summary.Trim().Length > 0 ? draft.Summary.Trim() : T("(ohne Titel)")),
            (T("Wann"), When(draft)),
        };
        if (!string.IsNullOrWhiteSpace(draft.Location))
        {
            lines.Add((T("Ort"), draft.Location.Trim()));
        }

        var html = "<p>" + string.Join("<br>", lines.Select(l => $"<b>{WebUtility.HtmlEncode(l.Label)}:</b> {WebUtility.HtmlEncode(l.Value)}")) + "</p>";
        var text = string.Join("\n", lines.Select(l => $"{l.Label}: {l.Value}"));
        return (html, text);
    }

    /// <summary>"Di, 3. Nov. 2026, 14:00–15:00"; over several days both dates; all day without times.</summary>
    public static string When(EventDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var culture = Localization.Texts.Culture;
        string Day(DateTime d) => d.ToString("ddd, d. MMM yyyy", culture);
        if (draft.IsAllDay)
        {
            var last = draft.End.Date.AddDays(-1);
            return last > draft.Start.Date ? $"{Day(draft.Start)} – {Day(last)}" + T(" (ganztägig)") : Day(draft.Start) + T(" (ganztägig)");
        }

        return draft.End.Date > draft.Start.Date
            ? $"{Day(draft.Start)}, {draft.Start:HH:mm} – {Day(draft.End)}, {draft.End:HH:mm}"
            : $"{Day(draft.Start)}, {draft.Start:HH:mm}–{draft.End:HH:mm}";
    }
}
