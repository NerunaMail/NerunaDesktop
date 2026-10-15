using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Neruna.Core;

namespace Neruna.Storage;

internal sealed class StorageMaintenance(IDbContextFactory<NerunaDbContext> contexts, string databasePath) : IStorageMaintenance
{
    public long DatabaseSize =>
        new[] { databasePath, databasePath + "-wal" }.Where(File.Exists).Sum(p => new FileInfo(p).Length);

    public async Task CompactAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);

        // The index is merged first (smaller), then the file is rewritten without free pages and the log emptied.
        await db.Database.ExecuteSqlRawAsync("INSERT INTO MessageSearch(MessageSearch) VALUES('optimize')", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("VACUUM", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE)", cancellationToken);
        SqliteConnection.ClearAllPools();
    }
}
