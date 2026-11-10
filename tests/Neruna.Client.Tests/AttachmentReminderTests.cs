using Neruna.Core;
using Neruna.Core.Mail;

namespace Neruna.Client.Tests;

public class AttachmentReminderTests
{
    private static readonly IReadOnlyList<string> Defaults = AttachmentReminderService.DefaultKeywords;

    [Theory]
    [InlineData("Im Anhang finden Sie die Offerte.", "Anhang")]
    [InlineData("Die Beilagen folgen.", "Beilage")]
    [InlineData("anbei die Unterlagen", "anbei")]
    [InlineData("Siehe Offerte_2026.PDF bitte", ".PDF")]
    [InlineData("Vous trouverez la pièce\njointe ci-dessous", "pièce\njointe")]
    [InlineData("Please find the attachments below.", "attachment")]
    [InlineData("Erst Begleitschreiben, dann Anhang", "Begleitschreiben")]
    public void Finds_words_that_announce_an_attachment(string text, string expected) =>
        Assert.Equal(expected, AttachmentReminderService.Find(text, Defaults));

    [Theory]
    [InlineData("Im Zusammenhang mit dem Projekt")]
    [InlineData("Die Datei heisst bericht.pdfx")]
    [InlineData("Wir sprechen über PDF-Formulare")]
    [InlineData("")]
    public void Ignores_words_inside_other_words(string text) =>
        Assert.Null(AttachmentReminderService.Find(text, Defaults));

    [Fact]
    public void Plain_text_counts_only_the_own_lines()
    {
        var text = "Danke für die Infos.\n> Im Anhang die Offerte\n\n-- \nAnhang: siehe Website";
        Assert.Null(AttachmentReminderService.Find(AttachmentReminderService.OwnPlainText(text), Defaults));

        var forward = "Zur Info.\n\n-----Ursprüngliche Nachricht-----\nVon: a@example.com\n\nIm Anhang die Rechnung";
        Assert.Equal("Zur Info.\n", AttachmentReminderService.OwnPlainText(forward));
    }

    [Fact]
    public async Task Words_and_switch_are_kept_and_reset()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        var service = new AttachmentReminderService(env.Get<ISettingsStore>());
        Assert.True(await service.GetEnabledAsync(ct));
        Assert.Equal(Defaults, await service.GetKeywordsAsync(ct));

        await service.SetKeywordsAsync([" Offerte ", "offerte", "", "Rechnung  als PDF"], ct);
        Assert.Equal(["Offerte", "Rechnung als PDF"], await service.GetKeywordsAsync(ct));
        Assert.Equal("Rechnung\nals PDF", AttachmentReminderService.Find("Hier die Rechnung\nals PDF", await service.GetKeywordsAsync(ct)));

        await service.SetEnabledAsync(false, ct);
        Assert.Empty(await service.GetActiveKeywordsAsync(ct));

        await service.SetKeywordsAsync(null, ct);
        Assert.Equal(Defaults, await service.GetKeywordsAsync(ct));
    }
}
