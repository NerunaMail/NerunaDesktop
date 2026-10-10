using Neruna.Providers.Imap;

namespace Neruna.Client.Tests;

/// <summary>The IMAP sync state remembers where the synced range starts; states written before that keep working.</summary>
public class ImapSyncStateTests
{
    [Fact]
    public void Sync_state_keeps_the_floor_and_reads_older_states_without_it()
    {
        var state = ImapMailProvider.SyncState.Parse("7:120:95");
        Assert.Equal(new ImapMailProvider.SyncState(7, 120, 95), state);
        Assert.Equal("7:120:95", state.ToString());

        var old = ImapMailProvider.SyncState.Parse("7:120");
        Assert.Equal(new ImapMailProvider.SyncState(7, 120), old);
        Assert.Null(old!.Value.Floor);
        Assert.Equal("7:120", old.ToString());

        Assert.Null(ImapMailProvider.SyncState.Parse("kaputt"));
        Assert.Null(ImapMailProvider.SyncState.Parse("1:2:3:4"));
    }
}
