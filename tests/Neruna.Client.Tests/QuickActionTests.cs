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
            [new QuickActionStep(QuickActionStepKind.StampPdf) { StampId = stamp.Id }, new QuickActionStep(QuickActionStepKind.SaveAttachment) { Folder = "/tmp/offerten", FileName = "{Dateiname}_genehmigt" },
             new QuickActionStep(QuickActionStepKind.Forward) { FromAccountId = Guid.NewGuid(), From = "anna@firma.ch" },
             new QuickActionStep(QuickActionStepKind.MoveToFolder) { AskFolder = true }]) { Shortcut = "Ctrl+M" };
        await service.SaveActionsAsync([action], Ct);
        await service.SaveStampsAsync([stamp with { Text = "Freigegeben" }], Ct);

        var again = new QuickActionService(settings);
        var loaded = Assert.Single(await again.GetActionsAsync(Ct));
        Assert.Equal(action.When, loaded.When);
        Assert.Equal(action.Steps, loaded.Steps);
        Assert.Equal("Ctrl+M", loaded.Shortcut);
        Assert.Equal("Freigegeben", Assert.Single(await again.GetStampsAsync(Ct)).Text);

        // Saved before "Jedes Mal fragen" existed: the fixed folder, as before.
        await settings.SetAsync("mail.quickActions", """[{"Id":"6f1c2a10-0000-4000-8000-000000000001","Label":"Ablegen","Color":"#107C10","When":{},"Steps":[{"Kind":"MoveToFolder","FolderRemoteId":"INBOX/Projekte","FolderName":"Projekte"}]}]""", Ct);
        var old = Assert.Single(Assert.Single(await again.GetActionsAsync(Ct)).Steps);
        Assert.Equal(("INBOX/Projekte", false), (old.FolderRemoteId, old.AskFolder));
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
                new QuickActionStep(QuickActionStepKind.Forward) { FromAccountId = accountHere, From = "rechnungen@firma.ch" },
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
        Assert.Equal((accountThere, "rechnungen@firma.ch"), (imported.Steps[3].FromAccountId, imported.Steps[3].From));
        Assert.Equal(@"\\server\buchhaltung", imported.Steps[1].Folder);
        Assert.Equal("Visiert", Assert.Single(stamps).Text); // only the stamps it uses

        // A computer without that mailbox and template: for all accounts, without template.
        var (elsewhere, _) = QuickActionService.Import(file, new Dictionary<Guid, string>(), new Dictionary<Guid, string>());
        Assert.Null(elsewhere[0].When.AccountId);
        Assert.Null(elsewhere[0].When.FolderRemoteId);
        Assert.Null(elsewhere[0].Steps[2].TemplateId);
        Assert.Equal(((Guid?)null, (string?)null), (elsewhere[0].Steps[3].FromAccountId, elsewhere[0].Steps[3].From)); // from the mail's own account

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

    [Fact]
    public async Task The_third_move_by_hand_suggests_a_quick_action()
    {
        var settings = new MemorySettings();
        var service = new QuickActionService(settings);
        var suggestions = new QuickActionSuggestions(settings, service);
        var account = Guid.NewGuid();
        var projects = new MailFolder(Guid.NewGuid(), "INBOX/Projekte", "Projekte", null, FolderRole.None);
        var trash = projects with { RemoteId = "Trash", Name = "Trash", Role = FolderRole.Trash };

        Assert.Null(await suggestions.RecordMoveAsync(account, "Lea@Example.com", "Lea Keller", projects, Ct));
        Assert.Null(await suggestions.RecordMoveAsync(account, "lea@example.com", "Lea Keller", trash, Ct)); // the trash never
        Assert.Null(await suggestions.RecordMoveAsync(account, "lea@example.com", "Lea Keller", projects, Ct));
        var suggestion = await suggestions.RecordMoveAsync(account, "lea@example.com", "Lea Keller", projects, Ct);
        Assert.NotNull(suggestion);
        Assert.Equal(("lea@example.com", "Projekte"), (suggestion.Sender, suggestion.FolderName));

        // "Ja": read and moved, for this sender in this account; after that nothing more to suggest.
        var action = await suggestions.AcceptAsync(suggestion, Ct);
        Assert.Equal([QuickActionStepKind.MarkRead, QuickActionStepKind.MoveToFolder], action.Steps.Select(s => s.Kind));
        Assert.Equal(("lea@example.com", (Guid?)account), (action.When.SenderContains, action.When.AccountId));
        for (var i = 0; i < 3; i++)
        {
            Assert.Null(await suggestions.RecordMoveAsync(account, "lea@example.com", "Lea Keller", projects, Ct));
        }

        // "Nein, nicht mehr fragen" for another sender; turned off for all.
        for (var i = 0; i < 2; i++)
        {
            await suggestions.RecordMoveAsync(account, "marco@example.com", null, projects, Ct);
        }

        await suggestions.DeclineAsync((await suggestions.RecordMoveAsync(account, "marco@example.com", null, projects, Ct))!, never: true, Ct);
        Assert.Contains("marco@example.com", await suggestions.GetNeverAsync(Ct));
        for (var i = 0; i < 3; i++)
        {
            Assert.Null(await suggestions.RecordMoveAsync(account, "marco@example.com", null, projects, Ct));
        }

        await suggestions.SetEnabledAsync(false, Ct);
        for (var i = 0; i < 3; i++)
        {
            Assert.Null(await suggestions.RecordMoveAsync(account, "nadia@example.com", null, projects, Ct));
        }
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
