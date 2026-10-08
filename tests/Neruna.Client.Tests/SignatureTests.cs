using MimeKit;
using Neruna.Core.Mail;

namespace Neruna.Client.Tests;

public class SignatureTests
{
    private static readonly Signature Formal = new(Guid.NewGuid(), "Standard", "<b>Anna Muster</b><br>Example AG", DateTimeOffset.Now);

    [Fact]
    public void New_mail_gets_space_to_write_then_the_signature()
    {
        var draft = SignatureBlock.Apply(ComposeDraft.Empty, Formal);

        Assert.Equal("<div><br></div><div><br></div><div id=\"neruna-signature\"><b>Anna Muster</b><br>Example AG</div>", draft.HtmlBody);
        Assert.Equal("\n\nAnna Muster\nExample AG", draft.Body);
    }

    [Fact]
    public void Reply_signature_sits_above_the_quote()
    {
        var original = new MimeMessage { Subject = "Offerte" };
        original.From.Add(new MailboxAddress("Marco", "marco@example.com"));
        original.Body = new TextPart("html") { Text = "<p>Original <span id=\"neruna-signature\">Marco</span></p>" };
        var reply = MessageComposer.Reply(original, "anna@example.com", replyAll: false);

        var html = SignatureBlock.Apply(reply, Formal).HtmlBody!;

        Assert.StartsWith("<div><br></div><div><br></div><div id=\"neruna-signature\"><b>Anna Muster</b>", html, StringComparison.Ordinal);
        Assert.True(html.IndexOf("Anna Muster", StringComparison.Ordinal) < html.IndexOf("<b>Von:</b>", StringComparison.Ordinal));

        // Only our signature carries the marker; a quoted Neruna signature must never be replaced.
        Assert.Equal(1, html.Split("id=\"neruna-signature\"").Length - 1);
    }

    [Fact]
    public void Without_signature_an_empty_container_is_left_for_choosing_one_later()
    {
        var draft = SignatureBlock.Apply(ComposeDraft.Empty with { Body = "Text" }, null);

        Assert.Contains("<div id=\"neruna-signature\"></div>", draft.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("<div>Text</div>", draft.HtmlBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Signatures_are_stored_and_assigned_per_account_and_kind()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var service = env.Get<SignatureService>();
        var ct = TestContext.Current.CancellationToken;
        var shortOne = new Signature(Guid.NewGuid(), "Kurz", "Gruss Anna", DateTimeOffset.Now);
        await service.SaveAsync(Formal, ct);
        await service.SaveAsync(shortOne, ct);
        var accountA = Guid.NewGuid();
        var accountB = Guid.NewGuid();

        await service.SetAssignmentAsync(accountA, new SignatureAssignment(Formal.Id, shortOne.Id), ct);

        Assert.Equal(["Kurz", "Standard"], (await service.GetAllAsync(ct)).Select(s => s.Name));
        Assert.Equal("Standard", (await service.ResolveAsync(accountA, ComposeKind.New, ct))?.Name);
        Assert.Equal("Kurz", (await service.ResolveAsync(accountA, ComposeKind.Reply, ct))?.Name);
        Assert.Equal("Kurz", (await service.ResolveAsync(accountA, ComposeKind.Forward, ct))?.Name);
        Assert.Null(await service.ResolveAsync(accountB, ComposeKind.New, ct));

        await service.SaveAsync(shortOne with { Html = "Liebe Grüsse" }, ct);
        await service.DeleteAsync(Formal.Id, ct);
        Assert.Null(await service.ResolveAsync(accountA, ComposeKind.New, ct));
        Assert.Equal("Liebe Grüsse", (await service.ResolveAsync(accountA, ComposeKind.Reply, ct))?.Html);
    }
}
