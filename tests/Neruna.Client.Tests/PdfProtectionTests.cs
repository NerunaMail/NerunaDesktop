using Neruna.Core.Mail;
using PdfSharp.Pdf.IO;

namespace Neruna.Client.Tests;

public class PdfProtectionTests
{
    [Fact]
    public void Protected_pdf_needs_the_password_and_opens_with_it()
    {
        var original = AttachmentPreviewTests.MinimalPdf("0 0 100 100 re f", "");
        Assert.False(PdfPages.NeedsPassword(original));

        var password = PdfProtection.NewPassword();
        var locked = PdfProtection.Protect(original, password);
        Assert.True(PdfPages.NeedsPassword(locked));
        Assert.Throws<PdfPreviewException>(() => PdfPages.Render(locked, 0, 1, 50));

        // PDFium (the other PDF engine) opens it with the password only.
        Assert.True(PdfPages.Opens(locked, password));
        Assert.False(PdfPages.Opens(locked, "falsch"));

        using var opened = PdfReader.Open(new MemoryStream(locked), password, PdfDocumentOpenMode.Import);
        Assert.Equal(2, opened.PageCount);

        // Protecting again is refused (it would need the password to read it).
        Assert.Throws<PdfProtectionException>(() => PdfProtection.Protect(locked, "x"));
    }

    [Fact]
    public void Passwords_are_readable_groups_and_rules_match_addresses_and_domains()
    {
        Assert.Matches("^[A-Za-z2-9]{4}(-[A-Za-z2-9]{4}){3}$", PdfProtection.NewPassword());
        Assert.NotEqual(PdfProtection.NewPassword(), PdfProtection.NewPassword());

        var domain = new PdfProtectionRule(Guid.NewGuid(), "@treuhand.example", false);
        Assert.True(domain.Matches("Anna@Treuhand.example"));
        Assert.False(domain.Matches("anna@nottreuhand.example.org"));
        Assert.True(new PdfProtectionRule(Guid.NewGuid(), "lea@example.com", true).Matches(" LEA@example.com"));
    }

    [Fact]
    public async Task Rules_keep_the_password_in_the_keychain_and_the_address_rule_wins()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        var service = new PdfProtectionService(env.Get<Neruna.Core.ISettingsStore>(), env.Get<Neruna.Core.Security.ICredentialStore>());
        Assert.True(await service.GetOfferAsync(ct));

        var domain = new PdfProtectionRule(Guid.NewGuid(), "@treuhand.example", false);
        var anna = new PdfProtectionRule(Guid.NewGuid(), "anna@treuhand.example", true);
        await service.SaveRuleAsync(domain, "domain-pw", ct);
        await service.SaveRuleAsync(anna, "anna-pw", ct);
        Assert.DoesNotContain("pw", await env.Get<Neruna.Core.ISettingsStore>().GetAsync(PdfProtectionService.RulesKey, ct), StringComparison.Ordinal);

        var matches = await service.MatchAsync(["anna@treuhand.example", "beat@treuhand.example", "lea@example.com"], ct);
        Assert.Equal([("anna@treuhand.example", anna), ("beat@treuhand.example", domain)], matches);
        Assert.Equal("anna-pw", await service.GetPasswordAsync(anna, ct));

        await service.RemoveRuleAsync(anna, ct);
        Assert.Null(await service.GetPasswordAsync(anna, ct));
        Assert.Equal([domain], await service.GetRulesAsync(ct));
    }
}
