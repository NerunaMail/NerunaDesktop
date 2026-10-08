using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Neruna.Storage.Credentials;
using Neruna.Core;
using Neruna.Core.Mail;
using Neruna.Core.Security;

namespace Neruna.Storage;

public static class StorageServiceCollectionExtensions
{
    /// <summary>Registers the SQLite stores, the MIME file cache and the credential store under <paramref name="dataDirectory"/>.</summary>
    /// <param name="useSystemCredentials">
    /// Passwords in the operating system's credential store (the app); false keeps them in an encrypted file in the
    /// data directory (tests – they must never touch the user's keychain).
    /// </param>
    public static IServiceCollection AddNerunaStorage(this IServiceCollection services, string dataDirectory, bool useSystemCredentials = false)
    {
        Directory.CreateDirectory(dataDirectory);
        var databasePath = Path.Combine(dataDirectory, "neruna.db");

        services.AddDbContextFactory<NerunaDbContext>(options => options.UseSqlite($"Data Source={databasePath}"));
        services.AddSingleton(new MessageContentFiles(dataDirectory));
        services.AddSingleton<IAccountStore, SqliteAccountStore>();
        services.AddSingleton<IMailStore, SqliteMailStore>();
        services.AddSingleton<ICalendarStore, SqliteCalendarStore>();
        services.AddSingleton<IContactStore, SqliteContactStore>();
        services.AddSingleton<ICertificateStore, SqliteCertificateStore>();
        services.AddSingleton<ISettingsStore, SqliteSettingsStore>();
        services.AddSingleton<ISignatureStore, SqliteSignatureStore>();
        services.AddSingleton<Neruna.Core.Calendar.IReminderStore, SqliteReminderStore>();
        services.AddSingleton(new LocalCredentialStore(dataDirectory));
        services.AddSingleton<ICredentialStore>(sp =>
            (useSystemCredentials ? CredentialStoreSelector.TrySystemStore(Logger(sp)) : null)
            ?? (ICredentialStore)sp.GetRequiredService<LocalCredentialStore>());
        return services;
    }

    /// <summary>
    /// Creates the database or brings it up to date with the migrations (folder <c>Migrations</c>). Before an existing
    /// database is changed, a copy is kept next to it (<c>neruna.db.bak-…</c>). Call once at startup.
    /// </summary>
    public static async Task InitializeNerunaStorageAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var factory = services.GetRequiredService<IDbContextFactory<NerunaDbContext>>();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        var isLegacy = await LegacyDatabase.IsLegacyAsync(db, cancellationToken);
        var created = !isLegacy && !(await db.Database.GetAppliedMigrationsAsync(cancellationToken)).Any();
        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        if (!created && (isLegacy || pending.Count > 0))
        {
            await BackupAsync(db, cancellationToken);
        }

        if (isLegacy)
        {
            await LegacyDatabase.BaselineAsync(db, cancellationToken);
        }

        await db.Database.MigrateAsync(cancellationToken);
        await UpgradeDataAsync(db, created, cancellationToken);

        // Passwords saved by earlier versions in credentials.json move into the system credential store.
        if (services.GetRequiredService<ICredentialStore>() is SystemCredentialStore system)
        {
            try
            {
                await system.MoveFromAsync(services.GetRequiredService<LocalCredentialStore>(), Logger(services), cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger(services).LogWarning(ex, "Moving secrets to the system credential store failed; credentials.json is kept");
            }
        }

        // WAL lets the UI read while a background sync writes.
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken);
    }

    private static ILogger Logger(IServiceProvider services) =>
        services.GetService<ILoggerFactory>()?.CreateLogger("Neruna.Storage") ?? NullLogger.Instance;

    // A copy of the database before it is changed; the three newest are kept.
    private static async Task BackupAsync(NerunaDbContext db, CancellationToken cancellationToken)
    {
        var path = db.Database.GetDbConnection().DataSource;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return;
        }

        // VACUUM INTO writes a consistent copy, including what is still in the WAL file.
        var backup = $"{path}.bak-{DateTime.Now:yyyyMMdd-HHmmss}";
        await db.Database.ExecuteSqlRawAsync("VACUUM INTO {0}", [backup], cancellationToken);

        var directory = Path.GetDirectoryName(path)!;
        foreach (var old in Directory.GetFiles(directory, Path.GetFileName(path) + ".bak-*").OrderDescending().Skip(3))
        {
            File.Delete(old);
        }
    }

    /// <summary>
    /// One-time data fixes that the schema alone cannot express, tracked by a version in the settings table.
    /// Version 2: message summaries gained the S/MIME marker; folders synced before must be re-read once.
    /// </summary>
    private static async Task UpgradeDataAsync(NerunaDbContext db, bool created, CancellationToken cancellationToken)
    {
        const string key = "storage.dataVersion";
        const int current = 2;

        var row = await db.Settings.FindAsync([key], cancellationToken);
        var version = created ? current : int.TryParse(row?.Value, out var stored) ? stored : 1;

        if (version < 2)
        {
            await db.Database.ExecuteSqlRawAsync("UPDATE \"MailFolders\" SET \"SyncState\" = NULL", cancellationToken);
        }

        if (row is null)
        {
            db.Settings.Add(new SettingEntity { Key = key, Value = current.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        }
        else
        {
            row.Value = current.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
