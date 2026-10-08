using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Neruna.Storage;

/// <summary>Lets <c>dotnet ef</c> create the context without starting the app (used only for creating migrations).</summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<NerunaDbContext>
{
    public NerunaDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<NerunaDbContext>().UseSqlite("Data Source=design-time.db").Options);
}
