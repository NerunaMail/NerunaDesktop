using Neruna.Core;
using Neruna.Core.Calendar;
using Neruna.Core.Mail;
using Neruna.Core.Workspaces;

namespace Neruna.Client.Tests;

public class WorkspaceTests
{
    private static readonly Guid Business = Guid.NewGuid();
    private static readonly Guid Private = Guid.NewGuid();
    private static readonly Guid Connection = Guid.NewGuid();

    private static MailFolder Folder(string id, FolderRole role = FolderRole.None, string? parent = null) => new(Connection, id, id, parent, role);

    [Fact]
    public void Hidden_folders_take_their_subfolders_along_but_system_folders_always_show()
    {
        IReadOnlyList<MailFolder> folders =
        [
            Folder("INBOX", FolderRole.Inbox),
            Folder("INBOX/Newsletter", parent: "INBOX"),
            Folder("Projekte"),
            Folder("Projekte/Alt", parent: "Projekte"),
            Folder("Archiv", FolderRole.Archive),
            Folder("Neu vom Server"),
        ];
        var workspace = new Workspace(Guid.NewGuid(), "Business", "#0F6CBD", [Business],
            [Workspace.FolderKey(Business, "Projekte"), Workspace.FolderKey(Business, "INBOX"), Workspace.FolderKey(Business, "INBOX/Newsletter"), Workspace.FolderKey(Business, "Archiv")], [], []);

        Assert.Equal(["INBOX", "Archiv", "Neu vom Server"], workspace.FoldersOf(Business, folders).Select(f => f.RemoteId));
        Assert.Empty(workspace.FoldersOf(Private, folders));
        Assert.Equal(folders.Count, Workspace.Full.FoldersOf(Private, folders).Count);
    }

    [Fact]
    public void Calendars_and_address_books_follow_the_accounts_and_the_hidden_lists()
    {
        var team = new CalendarInfo(Connection, "team", "Team", null, false);
        var personal = new CalendarInfo(Connection, "personal", "Kalender", null, false);
        var workspace = new Workspace(Guid.NewGuid(), "Business", "#0F6CBD", [Business], [], [CalendarController.CalendarKey(team)], [Workspace.AddressBookKey(Connection, "privat")]);

        Assert.True(workspace.ShowsCalendar(personal, Business));
        Assert.False(workspace.ShowsCalendar(team, Business));
        Assert.False(workspace.ShowsCalendar(personal, Private));
        Assert.True(workspace.ShowsAddressBook(Connection, "firma", Business));
        Assert.False(workspace.ShowsAddressBook(Connection, "privat", Business));
        Assert.True(Workspace.Full.ShowsCalendar(team, Private));
    }

    [Fact]
    public async Task Workspaces_are_stored_switched_and_the_full_view_stays()
    {
        var settings = new MemorySettings();
        var service = new WorkspaceService(settings);
        await service.LoadAsync(TestContext.Current.CancellationToken);
        Assert.True(service.Current.IsFull);
        var changes = 0;
        service.Changed += (_, _) => changes++;

        var business = new Workspace(Guid.NewGuid(), "Business", "#0F6CBD", [Business], [], [], []);
        var privat = new Workspace(Guid.NewGuid(), "Privat", "#0B6A0B", [Private], [], [], [], HideChat: true);
        await service.SaveAsync(business, TestContext.Current.CancellationToken);
        await service.SaveAsync(privat, TestContext.Current.CancellationToken);
        await service.SwitchAsync(privat.Id, TestContext.Current.CancellationToken);
        await service.MoveAsync(privat.Id, -1, TestContext.Current.CancellationToken);

        var again = new WorkspaceService(settings);
        await again.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["", "Privat", "Business"], again.All.Select(w => w.Name));
        Assert.Equal(privat.Id, again.Current.Id);
        Assert.True(again.Current.HideChat);
        Assert.Equal(4, changes);

        await again.DeleteAsync(privat.Id, TestContext.Current.CancellationToken);
        Assert.True(again.Current.IsFull);
        await Assert.ThrowsAsync<ArgumentException>(() => again.SaveAsync(Workspace.Full, TestContext.Current.CancellationToken));
        Assert.Equal(WorkspaceDisplay.Rail, await again.GetDisplayAsync(TestContext.Current.CancellationToken));
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
