using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Neruna.Core;
using Neruna.Core.Accounts;
using Neruna.Core.Mail;
using Neruna.Core.Security;
using Neruna.Storage;

namespace Neruna.Client.Tests;

public sealed class SchemaUpgradeTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "neruna-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Every_model_change_has_a_migration()
    {
        // Fails when the model was changed without "dotnet ef migrations add <Name> --project src/Client/Neruna.Storage".
        using var db = new NerunaDbContext(new DbContextOptionsBuilder<NerunaDbContext>().UseSqlite("Data Source=:memory:").Options);
        Assert.False(db.Database.HasPendingModelChanges(), "Datenmodell geändert, aber keine Migration erzeugt.");
    }

    [Fact]
    public async Task New_database_is_created_by_migrations_and_restarts_without_backup()
    {
        var ct = TestContext.Current.CancellationToken;
        await using (var services = Build())
        {
            await services.InitializeNerunaStorageAsync(ct);
            Assert.Equal(await AllMigrationsAsync(services, ct), await AppliedAsync(services, ct));
        }

        await using (var services = Build())
        {
            await services.InitializeNerunaStorageAsync(ct);
        }

        Assert.Empty(Backups());
    }

    [Fact]
    public async Task Database_from_before_migrations_is_completed_marked_and_backed_up_without_losing_data()
    {
        var ct = TestContext.Current.CancellationToken;

        // 1. A beta database: created with EnsureCreated (no migration history), with an account and a synced message.
        var connectionId = Guid.NewGuid();
        await using (var services = Build())
        {
            var factory = services.GetRequiredService<IDbContextFactory<NerunaDbContext>>();
            await using (var db = await factory.CreateDbContextAsync(ct))
            {
                await db.Database.EnsureCreatedAsync(ct);
            }

            await services.GetRequiredService<IAccountStore>().SaveAccountAsync(
                new Account(Guid.NewGuid(), "Anna", "anna@example.com", [new ServiceConnection(connectionId, ServiceKind.Mail, "imap", new Dictionary<string, string>())]), ct);
            var mail = services.GetRequiredService<IMailStore>();
            var inbox = (await mail.MergeFoldersAsync(connectionId, [new MailFolder(connectionId, "INBOX", "INBOX", null, FolderRole.Inbox)], ct))[0];
            await mail.ApplySyncResultAsync(inbox, new FolderSyncResult("1:2", true, [new MessageSummary("1", null, null, "Alt", null, [], DateTimeOffset.UtcNow, MessageFlags.None, 1, false, null)], new Dictionary<string, MessageFlags>(), [], 1, 1), ct);
        }

        // 2. As an early beta had it: no certificates/settings/signatures/reminder tables (the latter came with a later
        //    migration), no Security/SortOrder columns.
        SqliteConnection.ClearAllPools();
        await using (var raw = new SqliteConnection($"Data Source={Path.Combine(_directory, "neruna.db")}"))
        {
            await raw.OpenAsync(ct);
            await using var command = raw.CreateCommand();
            command.CommandText = "DROP TABLE Certificates; DROP TABLE Settings; DROP TABLE Signatures; DROP TABLE ReminderStates; " +
                                  "ALTER TABLE Messages DROP COLUMN Security; ALTER TABLE Accounts DROP COLUMN SortOrder;";
            await command.ExecuteNonQueryAsync(ct);
        }

        SqliteConnection.ClearAllPools();

        // 3. Starting the new version completes the schema, marks it as migrated and keeps a copy of the old file.
        await using (var services = Build())
        {
            await services.InitializeNerunaStorageAsync(ct);

            Assert.Equal(await AllMigrationsAsync(services, ct), await AppliedAsync(services, ct));
            var message = Assert.Single(await services.GetRequiredService<IMailStore>().GetMessagesAsync(connectionId, "INBOX", 0, 10, ct));
            Assert.Equal("Alt", message.Subject);
            Assert.Equal(MessageSecurity.None, message.Security);
            Assert.Equal("Anna", Assert.Single(await services.GetRequiredService<IAccountStore>().GetAccountsAsync(ct)).DisplayName);

            // The new column cannot be filled locally, so the folder is fully re-synced once.
            Assert.Null(Assert.Single(await services.GetRequiredService<IMailStore>().GetFoldersAsync(connectionId, ct)).SyncState);

            await services.GetRequiredService<ISettingsStore>().SetBoolAsync(SettingKeys.AutoSign, true, ct);
            Assert.True(await services.GetRequiredService<ISettingsStore>().GetBoolAsync(SettingKeys.AutoSign, cancellationToken: ct));
            Assert.Empty(await services.GetRequiredService<ICertificateStore>().GetAllAsync(ct));
            Assert.Empty(await services.GetRequiredService<ISignatureStore>().GetAllAsync(ct));
        }

        var backup = Assert.Single(Backups());
        SqliteConnection.ClearAllPools();
        await using (var copy = new SqliteConnection($"Data Source={backup};Mode=ReadOnly"))
        {
            await copy.OpenAsync(ct);
            await using var command = copy.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name = '__EFMigrationsHistory'";
            Assert.Equal(0L, await command.ExecuteScalarAsync(ct));
        }

        // 4. The next start has nothing to do.
        SqliteConnection.ClearAllPools();
        await using (var services = Build())
        {
            await services.InitializeNerunaStorageAsync(ct);
        }

        Assert.Single(Backups());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string[] Backups() => Directory.GetFiles(_directory, "neruna.db.bak-*");

    private ServiceProvider Build() => new ServiceCollection().AddNerunaStorage(_directory).BuildServiceProvider();

    private static async Task<List<string>> AppliedAsync(IServiceProvider services, CancellationToken ct)
    {
        await using var db = await services.GetRequiredService<IDbContextFactory<NerunaDbContext>>().CreateDbContextAsync(ct);
        return (await db.Database.GetAppliedMigrationsAsync(ct)).ToList();
    }

    private static async Task<List<string>> AllMigrationsAsync(IServiceProvider services, CancellationToken ct)
    {
        await using var db = await services.GetRequiredService<IDbContextFactory<NerunaDbContext>>().CreateDbContextAsync(ct);
        return db.Database.GetMigrations().ToList();
    }
}
