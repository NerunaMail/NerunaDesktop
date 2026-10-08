using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Neruna.Storage;

/// <summary>
/// Databases from before migrations (beta versions up to October 2026, created with EnsureCreated and extended
/// additively) are brought to the state of the first migration, <c>InitialCreate</c>, and marked as such. From then on
/// they are migrated like any other. Only adds missing tables, columns and indexes – never drops or changes anything.
/// </summary>
internal static class LegacyDatabase
{
    /// <summary>True if the database has tables but no migration history (created before migrations).</summary>
    public static async Task<bool> IsLegacyAsync(NerunaDbContext db, CancellationToken cancellationToken)
    {
        var tables = await TablesAsync(db, cancellationToken);
        return tables.Count > 0 && !tables.Contains(HistoryRepository.DefaultTableName, StringComparer.OrdinalIgnoreCase);
    }

    /// <returns>What was added, as "Table" or "Table.Column".</returns>
    public static async Task<IReadOnlyList<string>> BaselineAsync(NerunaDbContext db, CancellationToken cancellationToken)
    {
        var assembly = db.GetService<IMigrationsAssembly>();
        var (id, migrationType) = assembly.Migrations.First();
        var initial = assembly.CreateMigration(migrationType, db.Database.ProviderName!);
        var operations = initial.UpOperations;

        var added = new List<string>();
        var tables = await TablesAsync(db, cancellationToken);
        var generator = db.GetService<IMigrationsSqlGenerator>();

        foreach (var create in operations.OfType<CreateTableOperation>())
        {
            if (!tables.Contains(create.Name, StringComparer.OrdinalIgnoreCase))
            {
                // The whole table with its indexes, exactly as the migration would create it.
                var forTable = operations.Where(o => o == create || (o is CreateIndexOperation i && i.Table == create.Name)).ToList();
                foreach (var command in generator.Generate(forTable))
                {
                    await db.Database.ExecuteSqlRawAsync(command.CommandText, cancellationToken);
                }

                added.Add(create.Name);
                continue;
            }

            var columns = await db.Database.SqlQuery<string>($"SELECT name AS Value FROM pragma_table_info({create.Name})").ToListAsync(cancellationToken);
            foreach (var column in create.Columns.Where(c => !columns.Contains(c.Name, StringComparer.OrdinalIgnoreCase)))
            {
                var type = column.ColumnType ?? "TEXT";
                var definition = column.IsNullable ? type : $"{type} NOT NULL DEFAULT {DefaultFor(type)}";
                // Identifiers come from our own migration, never from user input, and cannot be SQL parameters.
                var alter = $"ALTER TABLE \"{create.Name}\" ADD COLUMN \"{column.Name}\" {definition}";
                await db.Database.ExecuteSqlRawAsync(alter, cancellationToken);
                added.Add($"{create.Name}.{column.Name}");
            }
        }

        // Indexes of tables that existed (e.g. added in later beta versions).
        foreach (var index in operations.OfType<CreateIndexOperation>())
        {
            var exists = await db.Database.SqlQuery<string>($"SELECT name AS Value FROM sqlite_master WHERE type = 'index' AND name = {index.Name}").AnyAsync(cancellationToken);
            if (!exists)
            {
                foreach (var command in generator.Generate([index]))
                {
                    await db.Database.ExecuteSqlRawAsync(command.CommandText, cancellationToken);
                }
            }
        }

        var history = db.GetService<IHistoryRepository>();
        await history.CreateIfNotExistsAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync(history.GetInsertScript(new HistoryRow(id, ProductInfo.GetVersion())), cancellationToken);
        return added;
    }

    private static async Task<List<string>> TablesAsync(NerunaDbContext db, CancellationToken cancellationToken) =>
        await db.Database.SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type = 'table'").ToListAsync(cancellationToken);

    private static string DefaultFor(string type) => type.ToUpper(CultureInfo.InvariantCulture) switch
    {
        "INTEGER" or "REAL" => "0",
        "BLOB" => "X''",
        _ => "''",
    };
}
