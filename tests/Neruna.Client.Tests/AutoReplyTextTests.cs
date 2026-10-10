using System.Globalization;
using Neruna.Core;
using Neruna.Core.Mail;

namespace Neruna.Client.Tests;

/// <summary>Placeholders in the out-of-office text and the template Neruna keeps for them.</summary>
public class AutoReplyTextTests
{
    // Days are the user's local days: midnight in the time zone the tests run in (CI runs in UTC).
    private static readonly DateTimeOffset Monday = new(new DateTime(2026, 10, 12), TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 12)));
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-CH");

    [Fact]
    public void Placeholders_become_the_first_and_the_last_day_away()
    {
        // The end is exclusive (the first day back): {{end}} is the day before.
        var text = AutoReplyText.Render("Abwesend vom {{start}} bis {{ END }}.", Monday, Monday.AddDays(5), German);
        Assert.Equal("Abwesend vom Montag, 12. Oktober 2026 bis Freitag, 16. Oktober 2026.", text);
        Assert.Equal("Ohne {{start}}", AutoReplyText.Render("Ohne {{start}}", null, null, German));
        Assert.True(AutoReplyText.HasPlaceholders("bis {{end}}"));
        Assert.False(AutoReplyText.HasPlaceholders("bis Freitag"));
    }

    [Fact]
    public void Switched_on_with_placeholders_needs_a_period()
    {
        Assert.Throws<ArgumentException>(() => AutoReplyText.ForServer("bis {{end}}", enabled: true, null, null));
        Assert.Equal("bis {{end}}", AutoReplyText.ForServer("bis {{end}}", enabled: false, null, null)); // off: kept for next time
        Assert.Equal("Weg.", AutoReplyText.ForServer(" Weg. ", enabled: true, null, null));
    }

    [Fact]
    public async Task The_template_comes_back_while_the_server_has_what_Neruna_made_of_it()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var env = await TestEnvironment.CreateAsync();
        var settings = env.Get<ISettingsStore>();
        var connection = Guid.NewGuid();
        const string template = "Zurück am Tag nach dem {{end}}.";
        await AutoReplyText.RememberAsync(settings, connection, template, ct);

        var onServer = new AutoReply(true, AutoReplyText.Render(template, Monday, Monday.AddDays(5)).Replace("\n", "\r\n", StringComparison.Ordinal), Monday, Monday.AddDays(5));
        Assert.Equal(template, await AutoReplyText.TemplateForAsync(settings, connection, onServer, ct));

        // Changed in the webmail: the server's text wins.
        Assert.Equal("Anders.", await AutoReplyText.TemplateForAsync(settings, connection, onServer with { Message = "Anders." }, ct));
    }

    [Fact]
    public async Task Named_templates_are_kept_sorted_and_replaced_by_name()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var env = await TestEnvironment.CreateAsync();
        var settings = env.Get<ISettingsStore>();
        await AutoReplyText.SaveTemplateAsync(settings, new AutoReplyTemplate("Ferien", "Bis {{end}} in den Ferien."), ct);
        await AutoReplyText.SaveTemplateAsync(settings, new AutoReplyTemplate("Bei Kunden", "Heute bei Kunden.", "Unterwegs"), ct);
        await AutoReplyText.SaveTemplateAsync(settings, new AutoReplyTemplate("ferien", "Neu: bis {{end}} weg."), ct);

        var list = await AutoReplyText.LoadTemplatesAsync(settings, ct);
        Assert.Equal(["Bei Kunden", "ferien"], list.Select(t => t.Name));
        Assert.Equal("Unterwegs", list[0].Subject);
        Assert.Equal(["Bei Kunden"], (await AutoReplyText.DeleteTemplateAsync(settings, "FERIEN", ct)).Select(t => t.Name));
    }
}
