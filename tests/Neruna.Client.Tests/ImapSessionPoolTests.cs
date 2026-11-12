using System.Net;
using System.Net.Sockets;
using MailKit;
using MailKit.Net.Imap;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using Neruna.Contracts.Discovery;
using Neruna.Core.Mail;
using Neruna.Core.Security;
using Neruna.Providers.Imap;

namespace Neruna.Client.Tests;

/// <summary>
/// IMAP connections kept for the next action on the same account (<see cref="ImapSessionPool"/>). Against Dovecot
/// (UNSELECT, any user with the password "pass"):
///   NERUNA_TEST_SIEVE_HOST=127.0.0.1 dotnet test
/// </summary>
public class ImapSessionPoolTests
{
    private static readonly string? Host = Environment.GetEnvironmentVariable("NERUNA_TEST_SIEVE_HOST");
    private const int Port = 3144;

    private static string NewUser() => $"pool-{Guid.NewGuid():N}@example.com";

    private static async Task<ImapClient> RawAsync(string user, CancellationToken ct)
    {
        var client = new ImapClient();
        await client.ConnectAsync(Host!, Port, MailKit.Security.SecureSocketOptions.None, ct);
        await client.AuthenticateAsync(user, "pass", ct);
        return client;
    }

    private static async Task AppendAsync(string user, string subject, CancellationToken ct)
    {
        using var client = await RawAsync(user, ct);
        await client.Inbox.AppendAsync(new AppendRequest(new MimeMessage([new MailboxAddress("Lea", "lea@example.com")], [new MailboxAddress("Anna", user)], subject, new TextPart("plain") { Text = "Hallo" })), ct);
        await client.DisconnectAsync(true, ct);
    }

    private static async Task WaitForIdleAsync(ImapSessionPool pool, int count, CancellationToken ct)
    {
        for (var i = 0; i < 100 && pool.IdleCount != count; i++)
        {
            await Task.Delay(50, ct);
        }

        Assert.Equal(count, pool.IdleCount);
    }

    private sealed record Account(Guid Id, ImapSettings Settings, ICredentialStore Credentials, ImapSessionPool Pool, string? LogDirectory = null)
    {
        public ImapMailProvider Provider() => new(Id, Settings, Credentials, NullLogger<ImapMailProvider>.Instance, LogDirectory, sessions: Pool);
    }

    private static async Task<Account> AccountAsync(TestEnvironment env, ImapSessionPool pool, string user, int port = Port, string? logDirectory = null)
    {
        var id = Guid.NewGuid();
        var credentials = env.Get<ICredentialStore>();
        await credentials.SetSecretAsync(id, "pass", TestContext.Current.CancellationToken);
        return new Account(id, new ImapSettings(Host!, port, SocketSecurity.None, Host!, 3025, SocketSecurity.None, user), credentials, pool, logDirectory);
    }

    [Fact]
    public async Task The_next_action_uses_the_same_connection_and_sees_the_folder_as_it_is_now()
    {
        Assert.SkipWhen(Host is null, "NERUNA_TEST_SIEVE_HOST not set");
        var ct = TestContext.Current.CancellationToken;
        await using var env = await TestEnvironment.CreateAsync();
        using var pool = new ImapSessionPool(NullLogger<ImapSessionPool>.Instance);
        var user = NewUser();
        await AppendAsync(user, "Eins", ct);
        var logs = Path.Combine(env.Directory, "protocol");
        var account = await AccountAsync(env, pool, user, logDirectory: logs);

        Neruna.Core.Mail.MailFolder inbox;
        FolderSyncResult first;
        await using (var provider = account.Provider())
        {
            inbox = (await provider.GetFoldersAsync(ct)).Single(f => f.Role == FolderRole.Inbox);
            first = await provider.SyncFolderAsync(inbox, [], ct);
            Assert.Single(first.AddedOrChanged);
        }

        await WaitForIdleAsync(pool, 1, ct);

        // Mail arrives while the connection waits: the next action must see it (a fresh SELECT, not the old state).
        await AppendAsync(user, "Zwei", ct);
        await using (var provider = account.Provider())
        {
            var synced = inbox with { SyncState = first.NewSyncState, TotalCount = first.TotalCount, UnreadCount = first.UnreadCount };
            var second = await provider.SyncFolderAsync(synced, [.. first.AddedOrChanged.Select(m => m.RemoteId)], ct);
            Assert.Equal("Zwei", Assert.Single(second.AddedOrChanged).Subject);
            Assert.Equal(2, second.TotalCount);
        }

        await WaitForIdleAsync(pool, 1, ct);

        // One connection, one sign-in: one protocol log.
        var log = Assert.Single(System.IO.Directory.GetFiles(logs, "imap-*.log"));
        pool.Dispose();
        var text = await File.ReadAllTextAsync(log, ct);
        Assert.Contains("UNSELECT", text, StringComparison.Ordinal);
        Assert.DoesNotContain(" CLOSE", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Messages_marked_as_deleted_survive_the_connection_going_back()
    {
        Assert.SkipWhen(Host is null, "NERUNA_TEST_SIEVE_HOST not set");
        var ct = TestContext.Current.CancellationToken;
        await using var env = await TestEnvironment.CreateAsync();
        using var pool = new ImapSessionPool(NullLogger<ImapSessionPool>.Instance);
        var user = NewUser();
        await AppendAsync(user, "Bleibt", ct);
        await AppendAsync(user, "Markiert", ct);
        using (var other = await RawAsync(user, ct))
        {
            await other.Inbox.OpenAsync(FolderAccess.ReadWrite, ct);
            await other.Inbox.AddFlagsAsync(new UniqueId(2), MailKit.MessageFlags.Deleted, true, ct);
            await other.DisconnectAsync(true, ct); // LOGOUT does not expunge
        }

        var account = await AccountAsync(env, pool, user);
        await using (var provider = account.Provider())
        {
            // Opens the folder read-write – the state in which a CLOSE would expunge.
            var inbox = (await provider.GetFoldersAsync(ct)).Single(f => f.Role == FolderRole.Inbox);
            await provider.SetFlagsAsync(inbox, ["1"], Neruna.Core.Mail.MessageFlags.Seen, true, ct);
        }

        await WaitForIdleAsync(pool, 1, ct);
        using var check = await RawAsync(user, ct);
        await check.Inbox.OpenAsync(FolderAccess.ReadOnly, ct);
        Assert.Equal(2, check.Inbox.Count);
    }

    [Fact]
    public async Task A_connection_the_server_dropped_meanwhile_is_replaced_without_an_error()
    {
        Assert.SkipWhen(Host is null, "NERUNA_TEST_SIEVE_HOST not set");
        var ct = TestContext.Current.CancellationToken;
        await using var env = await TestEnvironment.CreateAsync();
        using var pool = new ImapSessionPool(NullLogger<ImapSessionPool>.Instance);
        var user = NewUser();
        await AppendAsync(user, "Eins", ct);
        await using var proxy = new CuttableProxy(Host!, Port);
        var account = await AccountAsync(env, pool, user, proxy.Port);
        account = account with { Settings = account.Settings with { ImapHost = "127.0.0.1" } };

        await using (var provider = account.Provider())
        {
            Assert.Contains(await provider.GetFoldersAsync(ct), f => f.Role == FolderRole.Inbox);
        }

        await WaitForIdleAsync(pool, 1, ct);
        proxy.CutAll(); // a firewall or the server closes the waiting connection

        await using (var provider = account.Provider())
        {
            var inbox = (await provider.GetFoldersAsync(ct)).Single(f => f.Role == FolderRole.Inbox);
            Assert.Single((await provider.SyncFolderAsync(inbox, [], ct)).AddedOrChanged);
        }

        Assert.Equal(2, proxy.Accepted); // connected anew, once
    }

    [Fact]
    public async Task Accounts_working_at_the_same_time_never_get_each_others_connection()
    {
        Assert.SkipWhen(Host is null, "NERUNA_TEST_SIEVE_HOST not set");
        var ct = TestContext.Current.CancellationToken;
        await using var env = await TestEnvironment.CreateAsync();
        using var pool = new ImapSessionPool(NullLogger<ImapSessionPool>.Instance);
        var users = new[] { NewUser(), NewUser(), NewUser() };
        var accounts = new List<Account>();
        foreach (var user in users)
        {
            await AppendAsync(user, "Für " + user, ct);
            accounts.Add(await AccountAsync(env, pool, user));
        }

        // Many actions at once, all accounts mixed, several per account at the same moment.
        async Task<string> SubjectAsync(Account account)
        {
            await using var provider = account.Provider();
            var inbox = (await provider.GetFoldersAsync(ct)).Single(f => f.Role == FolderRole.Inbox);
            return Assert.Single((await provider.SyncFolderAsync(inbox, [], ct)).AddedOrChanged).Subject;
        }

        for (var round = 0; round < 5; round++)
        {
            var work = Enumerable.Range(0, 12).Select(i => (Account: accounts[i % 3], Task: Task.Run(() => SubjectAsync(accounts[i % 3]), ct))).ToList();
            foreach (var (account, task) in work)
            {
                Assert.Equal("Für " + account.Settings.Username, await task);
            }
        }

        // Kept afterwards: at most two per account.
        for (var i = 0; i < 100 && pool.IdleCount > ImapSessionPool.MaxIdlePerAccount * accounts.Count; i++)
        {
            await Task.Delay(50, ct);
        }

        Assert.InRange(pool.IdleCount, 1, ImapSessionPool.MaxIdlePerAccount * accounts.Count);
    }

    [Fact]
    public async Task Testing_an_account_signs_in_anew_a_kept_connection_does_not_hide_a_wrong_password()
    {
        Assert.SkipWhen(Host is null, "NERUNA_TEST_SIEVE_HOST not set");
        var ct = TestContext.Current.CancellationToken;
        await using var env = await TestEnvironment.CreateAsync();
        using var pool = new ImapSessionPool(NullLogger<ImapSessionPool>.Instance);
        var account = await AccountAsync(env, pool, NewUser());
        await using (var provider = account.Provider())
        {
            await provider.GetFoldersAsync(ct);
        }

        await WaitForIdleAsync(pool, 1, ct);
        await account.Credentials.SetSecretAsync(account.Id, "falsch", ct);

        await using var testing = account.Provider();
        await Assert.ThrowsAsync<MailKit.Security.AuthenticationException>(() => testing.TestConnectionAsync(ct));
        Assert.Equal(0, pool.IdleCount);
    }

    [Fact]
    public async Task After_the_pool_is_closed_nothing_is_kept()
    {
        Assert.SkipWhen(Host is null, "NERUNA_TEST_SIEVE_HOST not set");
        var ct = TestContext.Current.CancellationToken;
        await using var env = await TestEnvironment.CreateAsync();
        var pool = new ImapSessionPool(NullLogger<ImapSessionPool>.Instance);
        var account = await AccountAsync(env, pool, NewUser());
        await using (var provider = account.Provider())
        {
            await provider.GetFoldersAsync(ct);
            pool.Dispose();
        }

        await Task.Delay(300, ct);
        Assert.Equal(0, pool.IdleCount);
        await using var later = account.Provider();
        Assert.NotEmpty(await later.GetFoldersAsync(ct)); // works as without a pool
    }

    /// <summary>Forwards to the server and can cut every connection, as a firewall dropping idle ones does.</summary>
    private sealed class CuttableProxy : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly List<TcpClient> _clients = [];
        private readonly CancellationTokenSource _stop = new();
        private readonly string _host;
        private readonly int _port;

        public CuttableProxy(string host, int port)
        {
            _host = host;
            _port = port;
            _listener.Start();
            _ = AcceptAsync();
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public int Accepted { get; private set; }

        private async Task AcceptAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var incoming = await _listener.AcceptTcpClientAsync(_stop.Token);
                    var outgoing = new TcpClient();
                    await outgoing.ConnectAsync(_host, _port, _stop.Token);
                    lock (_clients)
                    {
                        Accepted++;
                        _clients.Add(incoming);
                        _clients.Add(outgoing);
                    }

                    _ = PumpAsync(incoming, outgoing);
                    _ = PumpAsync(outgoing, incoming);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
            }
        }

        private static async Task PumpAsync(TcpClient from, TcpClient to)
        {
            try
            {
                await from.GetStream().CopyToAsync(to.GetStream());
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
            }
            finally
            {
                to.Close();
            }
        }

        public void CutAll()
        {
            lock (_clients)
            {
                foreach (var client in _clients)
                {
                    client.Close();
                }

                _clients.Clear();
            }
        }

        public ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            CutAll();
            _stop.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
