using Neruna.Core.Calendar;

namespace Neruna.Client.Tests;

public class EventSummaryTextTests
{
    [Fact]
    public void Title_time_and_place_as_text_and_html()
    {
        var draft = EventDraft.New(new DateTime(2026, 11, 3, 14, 0, 0)) with { Summary = "Planung <Q1>", Location = "Rigi & Co" };

        var (html, text) = EventSummaryText.Format(draft);

        Assert.Equal("Termin: Planung <Q1>\nWann: Di, 3. Nov. 2026, 14:00–15:00\nOrt: Rigi & Co", text);
        Assert.Equal("<p><b>Termin:</b> Planung &lt;Q1&gt;<br><b>Wann:</b> Di, 3. Nov. 2026, 14:00–15:00<br><b>Ort:</b> Rigi &amp; Co</p>", html);
    }

    [Fact]
    public void All_day_and_several_days()
    {
        var day = EventDraft.New(new DateTime(2026, 11, 3)) with { Summary = "Messe", IsAllDay = true, End = new DateTime(2026, 11, 4) };
        Assert.Equal("Di, 3. Nov. 2026 (ganztägig)", EventSummaryText.When(day));
        Assert.Equal("Di, 3. Nov. 2026 – Do, 5. Nov. 2026 (ganztägig)", EventSummaryText.When(day with { End = new DateTime(2026, 11, 6) }));

        var night = EventDraft.New(new DateTime(2026, 11, 3, 22, 0, 0)) with { End = new DateTime(2026, 11, 4, 2, 0, 0) };
        Assert.Equal("Di, 3. Nov. 2026, 22:00 – Mi, 4. Nov. 2026, 02:00", EventSummaryText.When(night));
        Assert.DoesNotContain("Ort", EventSummaryText.Format(night).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_recipients_of_a_mail_become_attendees_once()
    {
        var attendees = EventAttendee.FromRecipients("\"Muster, Anna\" <anna@example.com>, lea@example.com", "Anna@Example.com; Marco <marco@example.com>", null, "halb");

        Assert.Equal(
            [new EventAttendee("anna@example.com", "Muster, Anna"), new EventAttendee("lea@example.com"), new EventAttendee("marco@example.com", "Marco")],
            attendees);
    }
}
