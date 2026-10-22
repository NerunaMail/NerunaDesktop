using Neruna.Core.Mail;
using Neruna.Providers.Imap;
using Candidate = Neruna.Providers.Imap.ImapMailProvider.RoleCandidate;

namespace Neruna.Client.Tests;

/// <summary>A mailbox used by several programs has "Drafts" and "DRAFTS", "Sent" and "Sent Items" …: one is the special folder.</summary>
public class ImapFolderRoleTests
{
    [Fact]
    public void The_marked_folder_wins_else_the_one_with_the_most_mail()
    {
        var roles = ImapMailProvider.UniqueRoles(
        [
            new Candidate(FolderRole.Inbox, true, 0),
            new Candidate(FolderRole.Drafts, false, 2),   // "Drafts"
            new Candidate(FolderRole.Drafts, false, 7),   // "DRAFTS": more mail
            new Candidate(FolderRole.Sent, false, 50),    // "Sent Items"
            new Candidate(FolderRole.Sent, true, 3),      // "Sent", marked by the server
            new Candidate(FolderRole.Trash, false, 0),    // "Trash"
            new Candidate(FolderRole.Trash, false, 0),    // "Deleted Items": a tie, the first stays
            new Candidate(FolderRole.Junk, false, 1),
            new Candidate(FolderRole.None, false, 0),
        ]);

        Assert.Equal(
            [FolderRole.Inbox, FolderRole.None, FolderRole.Drafts, FolderRole.None, FolderRole.Sent, FolderRole.Trash, FolderRole.None, FolderRole.Junk, FolderRole.None],
            roles);
    }
}
