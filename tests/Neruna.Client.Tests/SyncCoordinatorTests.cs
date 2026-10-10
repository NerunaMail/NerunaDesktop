using Neruna.Core;
using Neruna.Core.Accounts;

namespace Neruna.Client.Tests;

/// <summary>Timer, button, new accounts and the cloud ask for syncs: never two at once, waiting ones share a run.</summary>
public class SyncCoordinatorTests
{
    [Fact]
    public async Task Syncs_never_overlap_and_requests_made_meanwhile_share_one_run()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var env = await TestEnvironment.CreateAsync();
        var mail = new ServiceConnection(Guid.NewGuid(), ServiceKind.Mail, FakeMailProviderFactory.Id, new Dictionary<string, string>());
        await env.Accounts.SaveAccountAsync(new Account(Guid.NewGuid(), "Anna", "anna@example.com", [mail]), ct);
        var coordinator = env.Get<SyncCoordinator>();

        // The first sync hangs on the server …
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        env.MailServer.FolderListingGate = () => release.Task;
        var first = coordinator.SyncAllAsync(ct);
        while (Volatile.Read(ref env.MailServer.FolderListings) == 0)
        {
            await Task.Delay(10, ct);
        }

        // … meanwhile three more requests (timer, button, new account) come in.
        var waiting = new[] { coordinator.SyncAllAsync(ct), coordinator.SyncAllAsync(ct), coordinator.SyncAllAsync(ct) };
        await Task.Delay(100, ct);
        Assert.Equal(1, env.MailServer.FolderListings);

        release.SetResult();
        await first;
        var reports = await Task.WhenAll(waiting);

        // One more run for all three, never two at the same time.
        Assert.Equal(2, env.MailServer.FolderListings);
        Assert.Equal(1, env.MailServer.MaxConcurrentListings);
        Assert.Same(reports[0], reports[1]);
        Assert.Same(reports[1], reports[2]);

        // A request after that gets a run of its own.
        await coordinator.SyncAllAsync(ct);
        Assert.Equal(3, env.MailServer.FolderListings);
    }
}
