using MimeKit;
using Neruna.Core.Accounts;
using Neruna.Core.Mail;
using Neruna.Core.Notes;

namespace Neruna.Client.Tests;

public class NoteTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void A_note_is_stored_in_the_format_of_the_phone_notes_apps_and_read_back()
    {
        var note = Note.New(null, "Kunden", "<div>Meeting Müller</div><div>Offerte vorbereiten</div>") with { Created = new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero) };

        var mime = NoteMime.ToMime(note, "anna@example.com");

        Assert.Equal("com.apple.mail-note", mime.Headers["X-Uniform-Type-Identifier"]);
        Assert.Equal(note.Id, mime.Headers["X-Universally-Unique-Identifier"]);
        Assert.Equal("Meeting Müller", mime.Subject);
        Assert.True(NoteMime.IsNote(mime));

        var back = NoteMime.FromMime(mime, Guid.NewGuid(), "Kunden");
        Assert.Equal(note.Id, back.Id);
        Assert.Equal(note.Html, back.Html);
        Assert.Equal(note.Created, back.Created);
        Assert.Equal(("Meeting Müller", "Offerte vorbereiten"), (back.Title, back.Preview));
    }

    [Fact]
    public void A_note_another_app_wrote_as_plain_text_keeps_its_subject_as_title()
    {
        var message = new MimeMessage { Subject = "Einkauf", Body = new TextPart("plain") { Text = "" } };

        var note = NoteMime.FromMime(message, Guid.NewGuid(), string.Empty);

        Assert.Equal("Einkauf", note.Title);
    }

    [Fact]
    public void Links_to_mails_contacts_and_appointments_and_written_addresses_become_keys()
    {
        var html = "<div>Rückruf</div>"
                   + NoteLinks.Anchor(NoteLinks.MailHref("<abc@example.com>"), "Offerte")
                   + NoteLinks.Anchor(NoteLinks.ContactHref("uid-1", "Anna@Example.com"), "Anna")
                   + NoteLinks.Anchor(NoteLinks.EventHref("ev-9"), "Besprechung")
                   + "<div>auch an lea@example.org</div>";

        Assert.Equal(["mid:abc@example.com", "contact:uid-1", "email:anna@example.com", "event:ev-9", "email:lea@example.org"], NoteLinks.Keys(html));
        Assert.Equal(new NoteLink(NoteLinkKind.Mail, "abc@example.com"), NoteLinks.Parse("mid:abc%40example.com"));
        Assert.Null(NoteLinks.Parse("https://neruna.org"));
    }

    [Fact]
    public async Task Notes_on_this_computer_are_saved_found_by_their_links_and_deleted()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var notes = env.Get<NoteController>();
        await notes.CreateNotebookAsync(null, "Projekte", Token);

        var note = await notes.SaveAsync(Note.New(null, "Projekte", "<div>Idee</div><div>mit bob@example.com besprechen</div>"), cancellationToken: Token);

        Assert.Equal(["Projekte"], await notes.GetNotebooksAsync(null, Token));
        Assert.Equal("Idee", Assert.Single(await notes.GetNotesAsync(null, cancellationToken: Token)).Title);
        Assert.Single(await notes.FindLinkedAsync([NoteLinks.AddressKey("BOB@example.com")], Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => notes.DeleteNotebookAsync(null, "Projekte", Token));

        await notes.DeleteAsync(note, Token);
        Assert.Empty(await notes.GetNotesAsync(null, cancellationToken: Token));
    }

    [Fact]
    public async Task A_note_in_a_mailbox_goes_into_its_notes_folder_and_replaces_its_old_version()
    {
        await using var env = await TestEnvironment.CreateAsync();
        env.MailServer.SupportsTags = true;
        var connection = await env.AddAccountAsync(ServiceKind.Mail, FakeMailProviderFactory.Id);
        await env.Mail.SyncAllAsync(Token);
        var notes = env.Get<NoteController>();

        var note = await notes.SaveAsync(Note.New(connection.Id, "Kunden", "<div>Müller</div>") with { Pinned = true, Tags = ["$label1"] }, cancellationToken: Token);

        var stored = Assert.Single(env.MailServer.Folders["Notes/Kunden"]);
        Assert.Equal("Müller", stored.Subject);
        Assert.True(stored.Flags.HasFlag(MessageFlags.Flagged));
        Assert.Equal(["$label1"], stored.Keywords);
        Assert.Equal(note.Id, env.MailServer.Contents[stored.RemoteId].Headers[NoteMime.IdHeader]);
        Assert.Equal((NotePending.None, stored.RemoteId), (note.Pending, note.RemoteId));
        Assert.DoesNotContain(NoteFolders.WithoutNotes(await env.Mail.GetFoldersAsync(connection.Id, Token)), f => f.RemoteId.StartsWith("Notes", StringComparison.Ordinal));

        await notes.SaveAsync(note with { Html = "<div>Müller AG</div>" }, cancellationToken: Token);

        var replaced = Assert.Single(env.MailServer.Folders["Notes/Kunden"]);
        Assert.Equal("Müller AG", replaced.Subject);
        Assert.Equal("Müller AG", Assert.Single(await notes.GetNotesAsync(connection.Id, cancellationToken: Token)).Title);
    }

    [Fact]
    public async Task Notes_written_on_another_device_arrive_and_deleted_ones_leave()
    {
        await using var env = await TestEnvironment.CreateAsync();
        env.MailServer.Folders["Notes"] = [];
        var connection = await env.AddAccountAsync(ServiceKind.Mail, FakeMailProviderFactory.Id);
        var other = Note.New(connection.Id, string.Empty, "<div>Vom Handy</div>");
        await AppendAsync(env, connection, "Notes", other);
        var notes = env.Get<NoteController>();

        await env.Mail.SyncAllAsync(Token);
        await notes.SyncAsync(Token);

        var arrived = Assert.Single(await notes.GetNotesAsync(connection.Id, cancellationToken: Token));
        Assert.Equal((other.Id, "Vom Handy"), (arrived.Id, arrived.Title));

        env.MailServer.Folders["Notes"].Clear();
        await notes.SyncAsync(Token);
        Assert.Empty(await notes.GetNotesAsync(connection.Id, cancellationToken: Token));
    }

    [Fact]
    public async Task A_note_changed_here_and_on_another_device_is_kept_twice()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var connection = await env.AddAccountAsync(ServiceKind.Mail, FakeMailProviderFactory.Id);
        await env.Mail.SyncAllAsync(Token);
        var notes = env.Get<NoteController>();
        var note = await notes.SaveAsync(Note.New(connection.Id, string.Empty, "<div>Liste</div>"), cancellationToken: Token);

        // The other device replaced the version this one knows …
        env.MailServer.Folders["Notes"].Clear();
        await AppendAsync(env, connection, "Notes", note with { Html = "<div>Liste vom Handy</div>", Modified = DateTimeOffset.Now });
        // … while this one changed it without reaching the server.
        await notes.SaveAsync(note with { Html = "<div>Liste vom Computer</div>" }, upload: false, Token);

        await notes.SyncAsync(Token);

        var all = await notes.GetNotesAsync(connection.Id, cancellationToken: Token);
        Assert.Equal(["Liste vom Computer", "Liste vom Handy"], all.Select(n => n.Title).Order());
        Assert.Equal(note.Id, Assert.Single(all, n => n.ConflictOf is not null).ConflictOf);
        Assert.Equal(2, env.MailServer.Folders["Notes"].Count);
    }

    private static Task AppendAsync(TestEnvironment env, ServiceConnection connection, string folder, Note note) =>
        env.Providers.CreateMail(connection).AppendAsync(new MailFolder(connection.Id, folder, folder, null, FolderRole.None),
            NoteMime.ToMime(note, "anna@example.com"), MessageFlags.Seen, note.Modified, Token);
}
