using Microsoft.EntityFrameworkCore;
using Neruna.Core;
using Neruna.Core.Security;

namespace Neruna.Storage;

public sealed class SqliteCertificateStore(IDbContextFactory<NerunaDbContext> contexts) : ICertificateStore
{
    public async Task<IReadOnlyList<StoredCertificate>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var rows = await db.Certificates.AsNoTracking().ToListAsync(cancellationToken);
        return rows.Select(r => new StoredCertificate(r.Thumbprint, r.Der, r.Pkcs12, r.Source, DateTimeOffset.FromUnixTimeMilliseconds(r.AddedUnixMs))).ToList();
    }

    public async Task SaveAsync(StoredCertificate certificate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var row = await db.Certificates.FindAsync([certificate.Thumbprint], cancellationToken);
        if (row is null)
        {
            db.Certificates.Add(new CertificateEntity
            {
                Thumbprint = certificate.Thumbprint,
                Der = certificate.Der,
                Pkcs12 = certificate.Pkcs12,
                Source = certificate.Source,
                AddedUnixMs = certificate.AddedAt.ToUnixTimeMilliseconds(),
            });
        }
        else
        {
            // Never lose a private key because the same certificate is imported again without it.
            row.Pkcs12 = certificate.Pkcs12 ?? row.Pkcs12;
            row.Source = certificate.Source == CertificateSource.Imported ? CertificateSource.Imported : row.Source;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(string thumbprint, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await db.Certificates.Where(c => c.Thumbprint == thumbprint).ExecuteDeleteAsync(cancellationToken);
    }
}

public sealed class SqliteSettingsStore(IDbContextFactory<NerunaDbContext> contexts) : ISettingsStore
{
    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        return (await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key, cancellationToken))?.Value;
    }

    public async Task SetAsync(string key, string? value, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var row = await db.Settings.FindAsync([key], cancellationToken);
        if (row is null)
        {
            db.Settings.Add(new SettingEntity { Key = key, Value = value });
        }
        else
        {
            row.Value = value;
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
