using Neruna.Core;
using Neruna.Core.Mail;

namespace Neruna.Client.Tests;

public class QuickActionTests
{
    private static readonly Guid Account = Guid.NewGuid();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static QuickActionMessage Mail(string subject = "Offerte 4711", string from = "Anna Muster <anna@lieferant.ch>", string? folder = "INBOX", params string[] files) =>
        new(Account, folder, from, subject, [.. files.Select(f => new QuickActionAttachment(f, "application/octet-stream"))]);

    private static QuickAction Action(QuickActionCondition when) => new(Guid.NewGuid(), "Offerte genehmigen", "#107C10", when, []);

    [Fact]
    public void PdfConditionNeedsAPdf()
    {
        var action = Action(new QuickActionCondition { Attachment = QuickActionAttachmentKind.Pdf });
        Assert.True(QuickActionService.Matches(action, Mail(files: ["bild.png", "Offerte.PDF"])));
        Assert.False(QuickActionService.Matches(action, Mail(files: ["bild.png"])));
        Assert.False(QuickActionService.Matches(action, Mail()));
    }

    [Fact]
    public void AllConditionsMustApply()
    {
        var action = Action(new QuickActionCondition { SenderContains = "@LIEFERANT.ch", SubjectContains = "offerte", AccountId = Account, FolderRemoteId = "INBOX" });
        Assert.True(QuickActionService.Matches(action, Mail()));
        Assert.False(QuickActionService.Matches(action, Mail(subject: "Rechnung")));
        Assert.False(QuickActionService.Matches(action, Mail(from: "bob@example.com")));
        Assert.False(QuickActionService.Matches(action, Mail(folder: "Archiv")));
        Assert.False(QuickActionService.Matches(action with { When = action.When with { AccountId = Guid.NewGuid() } }, Mail()));
    }

    [Fact]
    public void WithoutConditionsTheButtonIsAlwaysThere() =>
        Assert.True(QuickActionService.Matches(Action(new QuickActionCondition()), Mail()));

    [Fact]
    public void StepsWorkOnTheAttachmentsOfTheCondition()
    {
        var files = new[] { "a.pdf", "b.xlsx", "c.XML", "d.png" };
        string[] Of(QuickActionCondition when) =>
            [.. QuickActionService.AttachmentsFor(Action(when), files, f => new QuickActionAttachment(f, "application/octet-stream"))];

        Assert.Equal(["b.xlsx"], Of(new QuickActionCondition { Attachment = QuickActionAttachmentKind.Office }));
        Assert.Equal(["c.XML", "d.png"], Of(new QuickActionCondition { Attachment = QuickActionAttachmentKind.Extension, Extensions = "xml, *.png" }));
        Assert.Equal(["d.png"], Of(new QuickActionCondition { Attachment = QuickActionAttachmentKind.Image }));
        Assert.Equal(files, Of(new QuickActionCondition()));
    }

    [Theory]
    [InlineData(null, "Offerte.pdf")]
    [InlineData("{Datum}_{Absender}_{Dateiname}", "2026-10-14_Anna Muster_Offerte.pdf")]
    [InlineData("{Betreff}.pdf", "AW_ Offerte 4711.pdf")]
    [InlineData("genehmigt", "genehmigt.pdf")]
    public void FileNameFromPattern(string? pattern, string expected) =>
        Assert.Equal(expected, QuickActionService.FileName(pattern, "Offerte.pdf", "Anna Muster", "AW: Offerte 4711", new DateTime(2026, 10, 14)));

    [Fact]
    public async Task ActionsAndStampsAreKept()
    {
        var settings = new MemorySettings();
        var service = new QuickActionService(settings);
        Assert.Equal(4, (await service.GetStampsAsync(Ct)).Count);
        Assert.Empty(await service.GetActionsAsync(Ct));

        var stamp = (await service.GetStampsAsync(Ct))[0];
        var action = new QuickAction(Guid.NewGuid(), "Offerte genehmigen", "#107C10",
            new QuickActionCondition { Attachment = QuickActionAttachmentKind.Pdf, SubjectContains = "Offerte" },
            [new QuickActionStep(QuickActionStepKind.StampPdf) { StampId = stamp.Id }, new QuickActionStep(QuickActionStepKind.SaveAttachment) { Folder = "/tmp/offerten", FileName = "{Dateiname}_genehmigt" }]);
        await service.SaveActionsAsync([action], Ct);
        await service.SaveStampsAsync([stamp with { Text = "Freigegeben" }], Ct);

        var again = new QuickActionService(settings);
        var loaded = Assert.Single(await again.GetActionsAsync(Ct));
        Assert.Equal(action.When, loaded.When);
        Assert.Equal(action.Steps, loaded.Steps);
        Assert.Equal("Freigegeben", Assert.Single(await again.GetStampsAsync(Ct)).Text);
    }

    [Fact]
    public void ExportedActionsFitTheOtherComputer()
    {
        var stamp = new PdfStamp(Guid.NewGuid(), "Visiert", "#0F6CBD");
        var (accountHere, accountThere, templateHere, templateThere) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var action = new QuickAction(Guid.NewGuid(), "Rechnung ablegen", "#CA5010",
            new QuickActionCondition { Attachment = QuickActionAttachmentKind.Pdf, AccountId = accountHere, FolderRemoteId = "INBOX/Rechnungen", FolderName = "Rechnungen" },
            [
                new QuickActionStep(QuickActionStepKind.StampPdf) { StampId = stamp.Id },
                new QuickActionStep(QuickActionStepKind.SaveAttachment) { Folder = @"\\server\buchhaltung" },
                new QuickActionStep(QuickActionStepKind.Reply) { TemplateId = templateHere },
            ]);
        var file = QuickActionService.Export([action], [stamp, new PdfStamp(Guid.NewGuid(), "Unbenutzt", "#000000")],
            new Dictionary<Guid, string> { [accountHere] = "info@firma.ch" }, new Dictionary<Guid, string> { [templateHere] = "Rechnung erhalten" });

        // Another computer: the same mailbox and template under other ids.
        var (actions, stamps) = QuickActionService.Import(file,
            new Dictionary<Guid, string> { [accountThere] = "INFO@firma.ch" }, new Dictionary<Guid, string> { [templateThere] = "Rechnung erhalten" });
        var imported = Assert.Single(actions);
        Assert.Equal(accountThere, imported.When.AccountId);
        Assert.Equal("INBOX/Rechnungen", imported.When.FolderRemoteId);
        Assert.Equal(templateThere, imported.Steps[2].TemplateId);
        Assert.Equal(@"\\server\buchhaltung", imported.Steps[1].Folder);
        Assert.Equal("Visiert", Assert.Single(stamps).Text); // only the stamps it uses

        // A computer without that mailbox and template: for all accounts, without template.
        var (elsewhere, _) = QuickActionService.Import(file, new Dictionary<Guid, string>(), new Dictionary<Guid, string>());
        Assert.Null(elsewhere[0].When.AccountId);
        Assert.Null(elsewhere[0].When.FolderRemoteId);
        Assert.Null(elsewhere[0].Steps[2].TemplateId);

        Assert.Throws<FormatException>(() => QuickActionService.Import("{\"format\":\"other\"}", new Dictionary<Guid, string>(), new Dictionary<Guid, string>()));
        Assert.Throws<FormatException>(() => QuickActionService.Import("kein json", new Dictionary<Guid, string>(), new Dictionary<Guid, string>()));
    }

    [Fact]
    public void StampLinesAsChosen()
    {
        var stamp = new PdfStamp(Guid.NewGuid(), "Genehmigt", "#107C10");
        Assert.Equal(2, stamp.Lines("Anna Muster", DateTime.Now).Count);
        Assert.Equal(["Anna Muster"], (stamp with { WithDate = false }).Lines("Anna Muster", DateTime.Now));
        Assert.Empty((stamp with { WithDate = false }).Lines(null, DateTime.Now));
    }

    private sealed class MemorySettings : ISettingsStore
    {
        private readonly Dictionary<string, string> _values = [];

        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(_values.GetValueOrDefault(key));

        public Task SetAsync(string key, string? value, CancellationToken cancellationToken = default)
        {
            if (value is null)
            {
                _values.Remove(key);
            }
            else
            {
                _values[key] = value;
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyDictionary<string, string>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(_values);
    }
}
