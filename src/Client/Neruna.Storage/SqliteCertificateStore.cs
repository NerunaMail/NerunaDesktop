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

/// <summary>
/// Settings, read from the database once and then answered from memory (the UI asks for many of them on every
/// page load); writes go to the database right away.
/// </summary>
public sealed class SqliteSettingsStore(IDbContextFactory<NerunaDbContext> contexts) : ISettingsStore
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string?> _values = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private Task? _loaded;

    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        await LoadedAsync().WaitAsync(cancellationToken);
        return _values.TryGetValue(key, out var value) ? value : null;
    }

    public async Task<IReadOnlyDictionary<string, string>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await LoadedAsync().WaitAsync(cancellationToken);
        return _values.Where(v => v.Value is not null).ToDictionary(v => v.Key, v => v.Value!, StringComparer.Ordinal);
    }

    public async Task SetAsync(string key, string? value, CancellationToken cancellationToken = default)
    {
        await LoadedAsync().WaitAsync(cancellationToken);
        _values[key] = value;
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

    private Task LoadedAsync()
    {
        lock (_gate)
        {
            return _loaded ??= LoadAsync();
        }
    }

    private async Task LoadAsync()
    {
        await using var db = await contexts.CreateDbContextAsync();
        foreach (var row in await db.Settings.AsNoTracking().ToListAsync())
        {
            _values.TryAdd(row.Key, row.Value);
        }
    }
}
