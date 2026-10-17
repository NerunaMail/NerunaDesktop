using System.Globalization;
using System.Net;
using System.Text;
using Neruna.Core.Calendar;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.Infrastructure;

/// <summary>How the calendar is printed.</summary>
internal enum CalendarPrintLayout
{
    /// <summary>One page per day: its events with times, place and calendar.</summary>
    Day,

    /// <summary>One landscape page per week: a column per day.</summary>
    Week,

    /// <summary>One landscape page per month: the month grid with the events in its days.</summary>
    Month,

    /// <summary>All events of the period one below the other, by day.</summary>
    List,
}

/// <summary>
/// The calendar on paper: each page unit (day, week, month, or the whole list) as HTML for <see cref="HtmlPdf"/>.
/// Tables only – the renderer knows them well; colours of the calendars as small marks.
/// </summary>
internal static class CalendarPrint
{
    public const string Stylesheet =
        "body { font-family: 'Segoe UI', Inter, Arial, sans-serif; font-size: 9pt; color: #1b1b1b; margin: 0; }" +
        " h1 { font-size: 15pt; margin: 0 0 10px 0; } h2 { font-size: 11pt; margin: 14px 0 4px 0; border-bottom: 1px solid #C8C8C8; padding-bottom: 2px; }" +
        " table.grid { border-collapse: collapse; width: 100%; } table.grid td, table.grid th { border: 1px solid #C8C8C8; vertical-align: top; padding: 3px 4px; }" +
        " table.grid th { background-color: #F3F3F3; font-weight: 600; text-align: left; }" +
        " .time { color: #505050; white-space: nowrap; } .place { color: #606060; } .other { color: #A0A0A0; }" +
        " .event { margin: 0 0 4px 0; } .mark { font-weight: bold; } .empty { color: #909090; }" +
        " table.list { border-collapse: collapse; width: 100%; } table.list td { padding: 3px 6px 3px 0; vertical-align: top; border-bottom: 1px solid #E6E6E6; }";

    /// <summary>The page units of the period, each its own HTML (a unit may still run over several pages).</summary>
    /// <param name="colors">Calendar key → its colour.</param>
    public static IReadOnlyList<string> Pages(CalendarPrintLayout layout, DateTime from, DateTime to, IReadOnlyList<CalendarOccurrence> events,
        IReadOnlyDictionary<string, string> colors, bool weekend, bool places)
    {
        ArgumentNullException.ThrowIfNull(events);
        var pages = new List<string>();
        switch (layout)
        {
            case CalendarPrintLayout.Day:
                for (var day = from.Date; day <= to.Date; day = day.AddDays(1))
                {
                    pages.Add(Day(day, events, colors, places));
                }

                break;
            case CalendarPrintLayout.Week:
                for (var monday = StartOfWeek(from); monday <= to.Date; monday = monday.AddDays(7))
                {
                    pages.Add(Week(monday, events, colors, weekend, places));
                }

                break;
            case CalendarPrintLayout.Month:
                for (var first = new DateTime(from.Year, from.Month, 1); first <= to.Date; first = first.AddMonths(1))
                {
                    pages.Add(Month(first, events, colors, weekend));
                }

                break;
            default:
                pages.Add(List(from, to, events, colors, places));
                break;
        }

        return pages;
    }

    public static DateTime StartOfWeek(DateTime day) => day.Date.AddDays(-(((int)day.DayOfWeek + 6) % 7));

    private static IEnumerable<CalendarOccurrence> On(DateTime day, IReadOnlyList<CalendarOccurrence> events) =>
        events.Where(e => e.Start.LocalDateTime.Date <= day && (e.End.LocalDateTime > day || e.Start.LocalDateTime.Date == day)
                          && !(e.IsAllDay && e.End.LocalDateTime.Date <= day && e.Start.LocalDateTime.Date != day))
            .OrderBy(e => !e.IsAllDay).ThenBy(e => e.Start);

    private static string Encode(string? text) => WebUtility.HtmlEncode(text ?? string.Empty);

    private static string Mark(CalendarOccurrence e, IReadOnlyDictionary<string, string> colors) =>
        $"<span class=\"mark\" style=\"color: {(colors.TryGetValue(CalendarController.CalendarKey(e.Calendar), out var c) ? c : "#0F6CBD")}\">■</span> ";

    private static string Time(CalendarOccurrence e, DateTime day) => e.IsAllDay
        ? T("ganztägig")
        : $"{(e.Start.LocalDateTime.Date < day ? "…" : e.Start.LocalDateTime.ToString("t", CultureInfo.CurrentCulture))}–{(e.End.LocalDateTime.Date > day ? "…" : e.End.LocalDateTime.ToString("t", CultureInfo.CurrentCulture))}";

    private static string Day(DateTime day, IReadOnlyList<CalendarOccurrence> events, IReadOnlyDictionary<string, string> colors, bool places)
    {
        var html = new StringBuilder($"<h1>{Encode(day.ToString("D", CultureInfo.CurrentCulture))}</h1><table class=\"list\">");
        var any = false;
        foreach (var e in On(day, events))
        {
            any = true;
            html.Append("<tr><td class=\"time\" style=\"width: 110px\">").Append(Encode(Time(e, day))).Append("</td><td>")
                .Append(Mark(e, colors)).Append("<b>").Append(Encode(e.Summary)).Append("</b>");
            if (places && !string.IsNullOrWhiteSpace(e.Location))
            {
                html.Append("<br><span class=\"place\">").Append(Encode(e.Location)).Append("</span>");
            }

            html.Append("</td><td class=\"place\" style=\"width: 160px\">").Append(Encode(e.Calendar.Name)).Append("</td></tr>");
        }

        return html.Append("</table>").Append(any ? string.Empty : $"<p class=\"empty\">{Encode(T("Keine Termine."))}</p>").ToString();
    }

    private static string Week(DateTime monday, IReadOnlyList<CalendarOccurrence> events, IReadOnlyDictionary<string, string> colors, bool weekend, bool places)
    {
        var days = Enumerable.Range(0, weekend ? 7 : 5).Select(i => monday.AddDays(i)).ToList();
        var title = F("Woche {0}: {1} – {2}", ISOWeek.GetWeekOfYear(monday), monday.ToString("d", CultureInfo.CurrentCulture), days[^1].ToString("d", CultureInfo.CurrentCulture));
        var html = new StringBuilder($"<h1>{Encode(title)}</h1><table class=\"grid\"><tr>");
        var width = 100 / days.Count;
        foreach (var day in days)
        {
            html.Append(CultureInfo.InvariantCulture, $"<th style=\"width: {width}%\">").Append(Encode(day.ToString("dddd", CultureInfo.CurrentCulture))).Append("<br>")
                .Append(Encode(day.ToString("d. MMMM", CultureInfo.CurrentCulture))).Append("</th>");
        }

        html.Append("</tr><tr>");
        foreach (var day in days)
        {
            html.Append("<td style=\"height: 560px\">");
            foreach (var e in On(day, events))
            {
                html.Append("<div class=\"event\">").Append(Mark(e, colors)).Append("<span class=\"time\">").Append(Encode(Time(e, day)))
                    .Append("</span><br><b>").Append(Encode(e.Summary)).Append("</b>");
                if (places && !string.IsNullOrWhiteSpace(e.Location))
                {
                    html.Append("<br><span class=\"place\">").Append(Encode(e.Location)).Append("</span>");
                }

                html.Append("</div>");
            }

            html.Append("</td>");
        }

        return html.Append("</tr></table>").ToString();
    }

    private static string Month(DateTime first, IReadOnlyList<CalendarOccurrence> events, IReadOnlyDictionary<string, string> colors, bool weekend)
    {
        var columns = weekend ? 7 : 5;
        var html = new StringBuilder($"<h1>{Encode(first.ToString("MMMM yyyy", CultureInfo.CurrentCulture))}</h1><table class=\"grid\"><tr>");
        var start = StartOfWeek(first);
        for (var i = 0; i < columns; i++)
        {
            html.Append(CultureInfo.InvariantCulture, $"<th style=\"width: {100 / columns}%\">").Append(Encode(start.AddDays(i).ToString("dddd", CultureInfo.CurrentCulture))).Append("</th>");
        }

        html.Append("</tr>");
        var weeks = (int)Math.Ceiling(((first.AddMonths(1) - start).TotalDays) / 7);
        var height = weeks > 5 ? 82 : 100;
        for (var week = start; week < first.AddMonths(1); week = week.AddDays(7))
        {
            html.Append("<tr>");
            for (var i = 0; i < columns; i++)
            {
                var day = week.AddDays(i);
                var other = day.Month != first.Month ? " class=\"other\"" : string.Empty;
                html.Append(CultureInfo.InvariantCulture, $"<td style=\"height: {height}px\"><div{other}><b>{day.Day}</b></div>");
                var list = On(day, events).ToList();
                foreach (var e in list.Take(5))
                {
                    html.Append("<div>").Append(Mark(e, colors)).Append(e.IsAllDay ? string.Empty : Encode(e.Start.LocalDateTime.ToString("t", CultureInfo.CurrentCulture)) + " ")
                        .Append(Encode(e.Summary.Length > 28 ? e.Summary[..28] + "…" : e.Summary)).Append("</div>");
                }

                if (list.Count > 5)
                {
                    html.Append("<div class=\"place\">").Append(Encode(F("+{0} weitere", list.Count - 5))).Append("</div>");
                }

                html.Append("</td>");
            }

            html.Append("</tr>");
        }

        return html.Append("</table>").ToString();
    }

    private static string List(DateTime from, DateTime to, IReadOnlyList<CalendarOccurrence> events, IReadOnlyDictionary<string, string> colors, bool places)
    {
        var html = new StringBuilder($"<h1>{Encode(F("Termine {0} – {1}", from.ToString("d", CultureInfo.CurrentCulture), to.ToString("d", CultureInfo.CurrentCulture)))}</h1>");
        var any = false;
        for (var day = from.Date; day <= to.Date; day = day.AddDays(1))
        {
            var list = On(day, events).ToList();
            if (list.Count == 0)
            {
                continue;
            }

            any = true;
            html.Append("<h2>").Append(Encode(day.ToString("dddd, d. MMMM yyyy", CultureInfo.CurrentCulture))).Append("</h2><table class=\"list\">");
            foreach (var e in list)
            {
                html.Append("<tr><td class=\"time\" style=\"width: 110px\">").Append(Encode(Time(e, day))).Append("</td><td>").Append(Mark(e, colors))
                    .Append("<b>").Append(Encode(e.Summary)).Append("</b>")
                    .Append(places && !string.IsNullOrWhiteSpace(e.Location) ? " <span class=\"place\">· " + Encode(e.Location) + "</span>" : string.Empty)
                    .Append("</td><td class=\"place\" style=\"width: 160px\">").Append(Encode(e.Calendar.Name)).Append("</td></tr>");
            }

            html.Append("</table>");
        }

        return any ? html.ToString() : html.Append(CultureInfo.InvariantCulture, $"<p class=\"empty\">{Encode(T("Keine Termine."))}</p>").ToString();
    }
}
